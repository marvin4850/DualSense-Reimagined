using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DsBridge.Haptics;

/// <summary>Haptic forge: automatic generation of HD effects from data recipes (haptics/forge/*.forge.json).
///
/// Pipeline per slot x variant (no editor, no hand work):
///   1. synthesis: layers "modal" (G1R: decaying inharmonic modes + band-passed contact noise = metal parts),
///      "grain" / "material" (Haptic Designer grains), "tone" / "noise" / "pulses" (Haptic Designer clips: band-passed
///      noise, stick-slip grain trains); every number may be an expression of the variant's vars ("=f0 * 1.2");
///   2. per-actuator EQ (Haptic Designer chain: HPF -> 10-band peaking -> LPF -> gain), default 30 Hz .. 650 Hz
///      (below ~30 Hz only wastes coil excursion, above ~700 Hz the pad is HEARD, not felt);
///   3. energy: soft saturation tanh(drive) raised until rms20 reaches the target (LOCKPICK.md §11: a click is felt by its
///      energy, not its peak), then the same LPF again (removes the saturation's harmonics) and a DC blocker;
///   4. edges: 0.2 ms fade-in, trailing silence trimmed, raised-cosine fade-out;
///   5. peak normalisation (default 0.85 = below the mixer limiter's knee 0.855, so the bank sample is played unchanged);
///   6. checks: clipping, limiter contact, DC, edges, duration, attack, rms20, spectral balance (sub / core / high),
///      left-right balance, contrast to other slots (grammar: e.g. the navigation click must not feel like a physics notch),
///      distinctness of the variants;
///   7. export: bank/&lt;bank&gt;/&lt;slot&gt;.&lt;variant&gt;.wav (32-bit float stereo = left/right actuator), preview reels
///      (stereo 16-bit + 4-ch "quad": ch1/2 silent, ch3/4 actuators - the layout of the Haptic Designer / the pad's USB device),
///      manifest.json and report.md.
/// `--explore N --from B`: N automatic mutations of variant B (vars x0.85..1.15, seeded); only candidates that pass every
/// check are kept (named B1..BN).</summary>
public static class Forge
{
    public sealed class Options
    {
        public string Recipe = ""; public string? OutDir; public List<string>? Variants; public List<string>? Slots;
        public int Explore; public string? ExploreFrom; public int Seed = 1; public bool CheckOnly; public bool Quiet;
        /// <summary>Batch mode: export the slots that pass, report the failed ones (default: all or nothing).</summary>
        public bool Partial;
    }

    public sealed class SlotResult
    {
        public string Slot = "", Variant = "", Role = ""; public HapticMetrics M = new(); public double Drive; public double Gain; public double BtCorr = 1, BtRms20;
        public List<string> Fails = new(), Warns = new(); public float[] L = Array.Empty<float>(), R = Array.Empty<float>();
        public bool Ok => Fails.Count == 0;
    }

    sealed record VariantDef(string Name, string Label, string Note, Dictionary<string, object?> Vars, bool Auto);

    static readonly JsonDocumentOptions JOpt = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    static string F(double v, string f = "0.00") => f.Contains(';') ? Sg(v) : v.ToString(f, CultureInfo.InvariantCulture);
    static string Sg(double v) => HapticMetrics.Signed(v);

    public static (bool ok, string text, List<SlotResult> results) Run(Options o)
    {
        var sb = new StringBuilder();
        using var doc = JsonDocument.Parse(File.ReadAllText(o.Recipe), JOpt);
        var root = doc.RootElement;
        string bank = root.GetProperty("bank").GetString()!;
        int rate = root.TryGetProperty("rate", out var rr) ? rr.GetInt32() : Dsp.SampleRate;
        var defaults = root.TryGetProperty("defaults", out var dd) ? dd.Clone() : default;
        // game sounds (forge layer "audio"): WAVs extracted read-only from the game's FMOD banks (tools/haptic_intel.py sounds --extract)
        _audioDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(o.Recipe))!, root.TryGetProperty("audioDir", out var ad) ? ad.GetString()! : "../../../research/data/fmod/wav"));
        var variants = new List<VariantDef>();
        foreach (var v in root.GetProperty("variants").EnumerateObject())
        {
            var vars = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (v.Value.TryGetProperty("vars", out var vs)) foreach (var p in vs.EnumerateObject()) vars[p.Name] = p.Value.ValueKind == JsonValueKind.Number ? p.Value.GetDouble() : p.Value.ToString();
            variants.Add(new VariantDef(v.Name, Str(v.Value, "label", v.Name), Str(v.Value, "note", ""), vars, false));
        }
        if (o.Explore > 0)
        {
            var from = variants.FirstOrDefault(x => x.Name.Equals(o.ExploreFrom ?? variants[0].Name, StringComparison.OrdinalIgnoreCase)) ?? variants[0];
            var rnd = Dsp.Rng((uint)o.Seed * 7919u + 17);
            for (int k = 1; k <= o.Explore; k++)
            {
                var vars = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var (key, val) in from.Vars) vars[key] = val is double d && !key.StartsWith("_") ? d * (0.85 + 0.30 * rnd()) : val;
                variants.Add(new VariantDef($"{from.Name}{k}", $"{from.Label} (авто {k})", $"automatic mutation of {from.Name}, seed {o.Seed}", vars, true));
            }
        }
        if (o.Variants != null) variants = variants.Where(v => o.Variants.Contains(v.Name, StringComparer.OrdinalIgnoreCase) || (o.Explore > 0 && v.Auto)).ToList();

        var slots = root.GetProperty("slots").EnumerateObject().Where(s => o.Slots == null || o.Slots.Contains(s.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        var results = new List<SlotResult>();
        foreach (var v in variants)
            foreach (var s in slots)
            {
                SlotResult res;
                try { res = Build(bank, s.Name, s.Value, defaults, v, rate); }
                catch (Exception ex) { res = new SlotResult { Slot = s.Name, Variant = v.Name }; res.Fails.Add($"render error: {ex.GetType().Name}: {ex.Message}"); }
                results.Add(res);
            }

        // cross-slot contrast (same variant) and cross-variant distinctness (same slot)
        foreach (var s in slots)
        {
            var cts = Chk(s.Value, defaults, "contrast") is JsonElement cx ? (cx.ValueKind == JsonValueKind.Array ? cx.EnumerateArray().ToList() : new List<JsonElement> { cx }) : new List<JsonElement>();
            foreach (var ct in cts.Where(x => x.ValueKind == JsonValueKind.Object))
            {
                string other = Str(ct, "slot", ""); double minD = ct.TryGetProperty("minDistance", out var md) ? md.GetDouble() : 0.3;
                double minRatio = ct.TryGetProperty("minCentroidRatio", out var mr) ? mr.GetDouble() : 0;
                foreach (var a in results.Where(x => x.Slot == s.Name))
                {
                    var b = results.FirstOrDefault(x => x.Slot == other && x.Variant == a.Variant);
                    if (b == null) continue;
                    double d = Analysis.SpectralDistance(a.M, b.M), ratio = a.M.CentroidHz / Math.Max(1, b.M.CentroidHz);
                    double eRatio = a.M.Rms20 / Math.Max(1e-6, b.M.Rms20);
                    bool okD = d >= minD, okR = minRatio <= 0 || ratio >= minRatio || eRatio >= 1.6;
                    if (!okD && !okR) a.Fails.Add($"too close to '{other}' (spectral distance {F(d)} < {F(minD)}, centroid ratio {F(ratio)}, energy ratio {F(eRatio)})");
                    else a.Warns.Add($"contrast to '{other}': distance {F(d)}, centroid x{F(ratio)}, energy x{F(eRatio)}");
                }
            }
        }
        // explore: keep only candidates that pass everything
        var dropped = new List<string>();
        foreach (var v in variants.Where(x => x.Auto).ToList())
            if (results.Any(r => r.Variant == v.Name && !r.Ok)) { dropped.Add(v.Name); results.RemoveAll(r => r.Variant == v.Name); variants.Remove(v); }
        var distinct = new List<string>();
        foreach (var s in slots)
        {
            var rs = results.Where(x => x.Slot == s.Name && x.L.Length > 0).ToList();
            for (int i = 0; i < rs.Count; i++) for (int j = i + 1; j < rs.Count; j++)
            {
                double d = Analysis.SpectralDistance(rs[i].M, rs[j].M);
                double t = Math.Abs(rs[i].M.ActiveMs - rs[j].M.ActiveMs) / Math.Max(rs[i].M.ActiveMs, rs[j].M.ActiveMs);
                if (d < 0.12 && t < 0.15 && !rs[i].Slot.StartsWith("_")) distinct.Add($"{s.Name}: {rs[i].Variant} ~ {rs[j].Variant} (spectral distance {F(d)}, length diff {F(t * 100, "0")}%)");
            }
        }

        // report
        sb.AppendLine($"forge: {o.Recipe}");
        sb.AppendLine($"bank '{bank}': {variants.Count} variant(s) x {slots.Count} slot(s), {rate} Hz");
        foreach (var v in variants) sb.AppendLine($"  variant {v.Name,-4} {v.Label}{(v.Note.Length > 0 ? " - " + v.Note : "")}");
        foreach (var r in results)
        {
            string st = !r.Ok ? "FAIL" : r.Warns.Any(w => !w.StartsWith("contrast")) ? "warn" : "ok";
            sb.AppendLine($"  {st,-4} {r.Slot,-14} {r.Variant,-4} {r.M.Short()} drive {F(r.Drive, "0.0")}");
            foreach (var f in r.Fails) sb.AppendLine("         FAIL " + f);
            foreach (var w in r.Warns) if (!o.Quiet) sb.AppendLine("         " + (w.StartsWith("contrast") ? "info " : "warn ") + w);
        }
        foreach (var d in distinct) sb.AppendLine("  warn variants alike: " + d);
        if (dropped.Count > 0) sb.AppendLine($"  explore: dropped (failed checks): {string.Join(", ", dropped)}");
        int fails = results.Count(r => !r.Ok);

        if (!o.CheckOnly && (fails == 0 || o.Partial))
        {
            string outDir = o.OutDir ?? Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(o.Recipe)))!, "bank", bank);
            var okResults = results.Where(r => r.Ok).ToList();
            if (fails > 0) sb.AppendLine($"PARTIAL: {fails} failed slot(s) not exported: {string.Join(", ", results.Where(r => !r.Ok).Select(r => r.Slot + "." + r.Variant))}");
            if (okResults.Count > 0) Export(outDir, bank, o.Recipe, variants, okResults, rate, distinct, sb);
        }
        else if (!o.CheckOnly) sb.AppendLine($"NOT EXPORTED: {fails} slot(s) failed the checks (the bank on disk is unchanged)");
        sb.AppendLine(fails == 0 ? "FORGE: OK" : $"FORGE: {fails} FAILED");
        return (fails == 0, sb.ToString(), results);
    }

    // ------------------------------------------------------------------ build one slot x variant

    [ThreadStatic] static string? _audioDir;

    static SlotResult Build(string bank, string slot, JsonElement s, JsonElement defaults, VariantDef v, int rate)
    {
        string? audioMethod = null;
        var res = new SlotResult { Slot = slot, Variant = v.Name, Role = Str(s, "role", "") };
        var vars = new Dictionary<string, object?>(v.Vars, StringComparer.OrdinalIgnoreCase);
        if (s.TryGetProperty("let", out var lets))   // slot-local helper values (may use the variant's vars)
            foreach (var p in lets.EnumerateObject()) vars[p.Name] = Eval(p.Value, vars);
        Expr.Lookup look = name => vars.TryGetValue(name, out var x) ? x : null;
        double Num(JsonElement e, string key, double def) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var x) ? ToD(Eval(x, vars), def) : def;   // section may be absent

        double lengthMs = Num(s, "lengthMs", 120), maxMs = Num(s, "maxLengthMs", Math.Max(lengthMs * 3, 400));
        int n = (int)(rate * maxMs / 1000);
        var L = new float[n]; var R = new float[n];
        uint seedBase = StableSeed(slot);   // string.GetHashCode is randomised per process: renders must be reproducible
        int li = 0;
        foreach (var layer in s.GetProperty("layers").EnumerateArray())
        {
            li++;
            if (layer.TryGetProperty("when", out var wh) && ToD(Eval(wh, vars), 1) == 0) continue;
            string type = Str(layer, "type", "modal");
            double at = Num(layer, "at", 0) / 1000.0, gain = Num(layer, "gain", 1), pan = Num(layer, "pan", 0);
            uint seed = (uint)Num(layer, "seed", seedBase * 31 + li);
            switch (type)
            {
                case "modal": Modal(layer, vars, L, R, rate, lengthMs, seed); break;
                case "grain":
                {
                    var g = new Dsp.Grain(Num(layer, "freq", 200), Num(layer, "durMs", 40) / 1000, Num(layer, "decayMs", 10) / 1000, gain, pan,
                                          layer.TryGetProperty("freqEnd", out _) ? Num(layer, "freqEnd", 200) : null, Num(layer, "attackMs", 1.5) / 1000,
                                          Num(layer, "noise", 0), Num(layer, "partial", 0), at);
                    Dsp.RenderGrain(g, Dsp.Rng(seed), L, R, 0, rate);
                    break;
                }
                case "material":
                {
                    var g = Dsp.ImpactGrain(Str(layer, "material", "metal"), gain, pan, Num(layer, "pitch", 1)) with { Delay = at };
                    if (layer.TryGetProperty("durMs", out _)) g = g with { Duration = Num(layer, "durMs", 100) / 1000 };
                    Dsp.RenderGrain(g, Dsp.Rng(seed), L, R, 0, rate);
                    break;
                }
                case "tone": case "noise": case "pulses":
                {
                    var c = new Dsp.Clip
                    {
                        Kind = type, Start = at, Duration = Num(layer, "durMs", lengthMs) / 1000, Gain = gain,
                        Attack = Num(layer, "attackMs", 0) / 1000, Release = Num(layer, "releaseMs", 0) / 1000, ReleaseCurve = Num(layer, "releaseCurve", 1),
                        PanStart = pan, PanEnd = Num(layer, "panEnd", pan), Wave = Str(layer, "wave", "sine"),
                        FreqStart = Num(layer, "freq", 150), FreqEnd = Num(layer, "freqEnd", Num(layer, "freq", 150)), Q = Num(layer, "q", 2),
                        AmRate = Num(layer, "amRate", 0), AmDepth = Num(layer, "amDepth", 0),
                        GrainKind = Str(layer, "grain", "custom"), Material = Str(layer, "material", "metal"),
                        RateStart = Num(layer, "rate", 20), RateEnd = Num(layer, "rateEnd", Num(layer, "rate", 20)),
                        Jitter = Num(layer, "jitter", 0), Spread = Num(layer, "spread", 0), Pitch = Num(layer, "pitch", 1), Doublet = Num(layer, "doublet", 0) > 0,
                        AmpJitter = Num(layer, "ampJitter", 0.15), AccentEvery = Num(layer, "accentEvery", 0), AccentGain = Num(layer, "accentGain", 1),
                    };
                    if (layer.TryGetProperty("pulse", out var pg))
                        c.Custom = new Dsp.Grain(Num(pg, "freq", 200), Num(pg, "durMs", 12) / 1000, Num(pg, "decayMs", 3) / 1000, Num(pg, "amp", 1), 0,
                                                 pg.TryGetProperty("freqEnd", out _) ? Num(pg, "freqEnd", 200) : null, Num(pg, "attackMs", 0.5) / 1000,
                                                 Num(pg, "noise", 0), Num(pg, "partial", 0));
                    Dsp.RenderClip(c, L, R, rate, seed);
                    break;
                }
                case "audio":
                {
                    // a game sound as the source of timing / texture, re-synthesised by a method (Haptics/AudioHaptics.cs)
                    string snd = Str(layer, "sound", "");
                    string path = Path.IsPathRooted(snd) ? snd : Path.Combine(_audioDir ?? ".", snd.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? snd : snd + ".wav");
                    if (!File.Exists(path)) throw new FileNotFoundException($"sound '{snd}' not extracted ({path}); run: python tools/haptic_intel.py sounds --extract {snd}");
                    if (rate != AudioHaptics.Rate) throw new FormatException("audio layers need rate 48000");
                    audioMethod ??= Str(layer, "method", "impact");
                    var lay = layer;
                    AudioHaptics.Render(path, (k, d) => Num(lay, k, d), (k, d) => Str(lay, k, d), L, R, seed);
                    break;
                }
                default: throw new FormatException($"slot '{slot}': unknown layer type '{type}'");
            }
        }

        // 1b. seamless loop ("loopMs", optional "loopCrossfadeMs"): the material after loopMs is cross-faded (sin²/cos²) onto
        // the head, then three periods run through the whole chain and the middle one is kept - filters / DC blocker are in
        // their steady state, so the mixer's wrap (Mixer: Pos end -> 0) is continuous. No trim, no edge fades for loops.
        int loopN = (int)(rate * Num(s, "loopMs", 0) / 1000);
        if (loopN > 0)
        {
            int xf = Math.Min(loopN / 2, (int)(rate * Num(s, "loopCrossfadeMs", 120) / 1000));
            if (loopN + xf > n) throw new FormatException($"slot '{slot}': loopMs + loopCrossfadeMs exceed maxLengthMs");
            float[] Wrap(float[] x)
            {
                var y = new float[loopN];
                for (int i = 0; i < loopN; i++)
                {
                    if (i >= xf) { y[i] = x[i]; continue; }
                    double w = Math.Sin(0.5 * Math.PI * i / xf); w *= w;
                    y[i] = (float)(x[i] * w + x[loopN + i] * (1 - w));
                }
                var t = new float[loopN * 3];
                for (int k = 0; k < 3; k++) Array.Copy(y, 0, t, k * loopN, loopN);
                return t;
            }
            L = Wrap(L); R = Wrap(R); n = L.Length;
        }

        // 2. per-actuator EQ
        var eqL = EqFrom(Pick(s, defaults, "eq"), vars); var eqR = EqFrom(s.TryGetProperty("eqR", out var er) ? er : Pick(s, defaults, "eq"), vars);
        // a slot built from a game sound without its own "eq" gets the EQ of its method (no universal equaliser)
        if (audioMethod != null && !s.TryGetProperty("eq", out _) && AudioHaptics.MethodEq.TryGetValue(audioMethod, out var meq))
            eqL = eqR = new Dsp.EqSettings(meq.hpf, meq.lpf, 0, meq.bands);
        if (s.TryGetProperty("eqL", out var el)) eqL = EqFrom(el, vars);
        L = Dsp.ApplyEq(L, eqL, rate); R = Dsp.ApplyEq(R, eqR, rate);

        // 3-5. energy, edges, peak: drive until rms20 reaches the target (tanh keeps the peak), band-limit again, DC block,
        // trim + fades, peak normalisation; repeated (up to 4 passes) because the post-processing costs some energy
        var norm = Pick(s, defaults, "normalize");
        double peakT = Num(norm, "peak", 0.85), rmsT = Num(norm, "rms20", 0), maxDrive = Num(norm, "maxDrive", 5);
        double pk0 = Math.Max(Max(L), Max(R));
        if (pk0 < 1e-6) { res.Fails.Add("silent rendering"); return res; }
        Scale(L, peakT / pk0); Scale(R, peakT / pk0);
        float[] baseL = L, baseR = R; double target = rmsT, drive = 0;
        for (int pass = 0; pass < 4; pass++)
        {
            L = (float[])baseL.Clone(); R = (float[])baseR.Clone(); drive = 0;
            if (target > 0 && Math.Max(Analysis.Rms20(L, rate), Analysis.Rms20(R, rate)) < target)
            {
                double lo = 0, hi = maxDrive;
                for (int it = 0; it < 18; it++)
                {
                    double mid = (lo + hi) / 2;
                    if (Math.Max(Analysis.Rms20(Drive(L, mid, peakT), rate), Analysis.Rms20(Drive(R, mid, peakT), rate)) < target) lo = mid; else hi = mid;
                }
                drive = hi;
                L = Drive(L, drive, peakT); R = Drive(R, drive, peakT);
                if (eqL.Lpf > 0) L = Dsp.ApplyEq(L, new Dsp.EqSettings(0, eqL.Lpf), rate);
                if (eqR.Lpf > 0) R = Dsp.ApplyEq(R, new Dsp.EqSettings(0, eqR.Lpf), rate);
            }
            Dsp.DcBlock(L, rate); Dsp.DcBlock(R, rate);
            double pk = Math.Max(Max(L), Max(R)); int last = 0;
            for (int i = 0; i < n; i++) if (Math.Abs(L[i]) > pk * 0.003 || Math.Abs(R[i]) > pk * 0.003) last = i;
            int len = Math.Min(n, last + rate * 5 / 1000);
            int fixedLen = (int)(rate * Num(s, "exactMs", 0) / 1000);
            if (fixedLen > 0) len = Math.Min(n, fixedLen);
            if (loopN > 0)
            {
                // keep the middle period of the three (steady state); the wrap is continuous by construction
                L = L.Skip(loopN).Take(loopN).ToArray(); R = R.Skip(loopN).Take(loopN).ToArray();
            }
            else
            {
                Array.Resize(ref L, len); Array.Resize(ref R, len);
                // optional soft onset "fadeInMs" (raised cosine); default: the original 0.2 ms linear ramp, bit-exact
                double fadeInMs = Num(s, "fadeInMs", 0);
                int fin = fadeInMs > 0 ? Math.Min(len / 4, (int)(rate * fadeInMs / 1000)) : Math.Max(1, rate / 5000), fout = Math.Min(len / 4, (int)(rate * Num(s, "fadeOutMs", 3) / 1000));
                for (int i = 0; i < Math.Min(fin, len); i++) { float k = fadeInMs > 0 ? (float)(0.5 - 0.5 * Math.Cos(Math.PI * i / fin)) : (float)i / fin; L[i] *= k; R[i] *= k; }
                for (int i = 0; i < fout; i++) { float k = (float)(0.5 - 0.5 * Math.Cos(Math.PI * i / fout)); L[len - 1 - i] *= k; R[len - 1 - i] *= k; }
            }
            pk = Math.Max(Max(L), Max(R));
            res.Gain = peakT / pk;   // one gain for both actuators: the balance is part of the design
            Scale(L, res.Gain); Scale(R, res.Gain);
            double got = Math.Max(Analysis.Rms20(L, rate), Analysis.Rms20(R, rate));
            if (rmsT <= 0 || got >= rmsT * 0.985 || drive >= maxDrive - 0.01) break;
            target = Math.Min(0.95, target * rmsT / Math.Max(1e-3, got));
        }
        res.L = L; res.R = R; res.Drive = drive;

        // 6. checks
        var m = res.M = Analysis.Measure(L, R, rate);
        var ck = Pick(s, defaults, "check");
        double C(string k, double def) => ck.ValueKind == JsonValueKind.Object && ck.TryGetProperty(k, out var x) ? ToD(Eval(x, vars), def) : def;
        if (m.Clipped > 0) res.Fails.Add($"clipping: {m.Clipped} samples at full scale");
        if (m.Peak > peakT + 0.01) res.Fails.Add($"peak {F(m.Peak)} above the target {F(peakT)}");
        if (m.OverKnee > 0) res.Warns.Add($"{m.OverKnee} samples above the limiter knee {Analysis.LimiterKnee} (the mixer will round them)");
        if (Math.Max(Math.Abs(m.DcL), Math.Abs(m.DcR)) > C("maxDc", 0.003)) res.Fails.Add($"DC offset {F(Math.Max(Math.Abs(m.DcL), Math.Abs(m.DcR)), "0.0000")}");
        if (loopN > 0)
        {
            // loop wrap: the step last -> first sample must look like any other step of the signal
            double step = 0; for (int i = 1; i < L.Length; i++) step = Math.Max(step, Math.Max(Math.Abs(L[i] - L[i - 1]), Math.Abs(R[i] - R[i - 1])));
            double wrap = Math.Max(Math.Abs(L[0] - L[^1]), Math.Abs(R[0] - R[^1]));
            if (wrap > step * 1.05 + 1e-4) res.Fails.Add($"loop wrap jump {F(wrap, "0.000")} > largest inner step {F(step, "0.000")} - would click when looping");
        }
        else if (m.EdgeStart > 0.02 || m.EdgeEnd > 0.02) res.Fails.Add($"hard edge (start {F(m.EdgeStart, "0.000")}, end {F(m.EdgeEnd, "0.000")}) - would click");
        double minMs = C("minMs", 5), maxMsC = C("maxMs", 2000);
        if (m.ActiveMs < minMs || m.ActiveMs > maxMsC) res.Fails.Add($"duration {F(m.ActiveMs, "0")} ms outside {F(minMs, "0")}..{F(maxMsC, "0")} ms");
        if (m.AttackMs > C("attackMaxMs", 1e9)) res.Fails.Add($"attack {F(m.AttackMs, "0.0")} ms > {F(C("attackMaxMs", 0), "0.0")} ms (soft onset)");
        if (m.Rms20 < C("rms20Min", 0)) res.Fails.Add($"energy rms20 {F(m.Rms20)} < {F(C("rms20Min", 0))} (felt weak / dull)");
        if (m.Rms20 > C("rms20Max", 1)) res.Fails.Add($"energy rms20 {F(m.Rms20)} > {F(C("rms20Max", 1))} (too heavy for its role)");
        if (m.Sub > C("maxSub", 0.12)) res.Fails.Add($"sub-band <40 Hz {F(m.Sub * 100, "0")}% (wasted coil excursion)");
        if (m.High > C("maxHigh", 0.04)) res.Fails.Add($"band >700 Hz {F(m.High * 100, "0")}% (audible buzz, not felt)");
        if (m.Core + m.Low < C("minCore", 0.6)) res.Fails.Add($"only {F((m.Core + m.Low) * 100, "0")}% of the energy in 40-400 Hz");
        double cmin = C("centroidMin", 0), cmax = C("centroidMax", 1e9);
        if (m.CentroidHz < cmin || m.CentroidHz > cmax) res.Fails.Add($"centroid {F(m.CentroidHz, "0")} Hz outside {F(cmin, "0")}..{F(cmax, "0")} Hz");
        string bal = ck.ValueKind == JsonValueKind.Object && ck.TryGetProperty("balance", out var bb) ? bb.GetString() ?? "center" : "center";
        double tol = C("balanceTolDb", 1.0);
        if (bal == "center" && Math.Abs(m.BalanceDb) > tol) res.Fails.Add($"left/right balance {F(m.BalanceDb, "+0.0;-0.0")} dB (centred effect, tolerance {F(tol, "0.0")} dB)");
        if (bal == "left" && m.BalanceDb < 6) res.Fails.Add($"left-side effect but balance only {F(m.BalanceDb, "+0.0;-0.0")} dB");
        if (bal == "right" && m.BalanceDb > -6) res.Fails.Add($"right-side effect but balance only {F(m.BalanceDb, "+0.0;-0.0")} dB");
        // Bluetooth (HID 0x32: 3 kHz, 8 bit, tanh gain 2): does the effect survive? A recipe that names btMinCorr / btMinRms
        // fails below them, otherwise a low correlation is a warning
        var (btCorr, btRms) = Analysis.BtSim(L, R, rate);
        res.BtCorr = btCorr; res.BtRms20 = btRms;
        double bmc = C("btMinCorr", 0), bmr = C("btMinRms", 0);
        if (bmc > 0 && btCorr < bmc) res.Fails.Add($"Bluetooth: correlation {F(btCorr)} < {F(bmc)} (content lost in the 0x32 path)");
        else if (btCorr < 0.6) res.Warns.Add($"Bluetooth: correlation only {F(btCorr)}");
        if (bmr > 0 && btRms < bmr) res.Fails.Add($"Bluetooth: rms20 {F(btRms)} < {F(bmr)} (too weak over BT)");
        return res;
    }

    /// <summary>"modal": metal part = sum of exponentially decaying modes [ratio, gain, tauMs] of a base frequency `f` that
    /// glides to f x `glide` over the slot length, linear attack `attackMs`, plus a contact transient (white noise through
    /// a band-pass at `noiseHz`, Q `noiseQ`, decaying with `noiseMs`) of weight `noise`. Default modes = free-free bar
    /// (1, 2.756, 5.404) - the classic "metal click / ring".</summary>
    static void Modal(JsonElement e, Dictionary<string, object?> vars, float[] L, float[] R, int rate, double lengthMs, uint seed)
    {
        double Num(string k, double def) => e.TryGetProperty(k, out var x) ? ToD(Eval(x, vars), def) : def;
        double f = Num("f", 250), at = Num("at", 0) / 1000, att = Math.Max(0.05, Num("attackMs", 0.3)) / 1000, glide = Num("glide", 1);
        double gain = Num("gain", 1), pan = Num("pan", 0), panEnd = Num("panEnd", pan), dur = Num("durMs", lengthMs) / 1000;
        double noise = Num("noise", 0), noiseHz = Num("noiseHz", f * 2.5), noiseQ = Num("noiseQ", 1.2), noiseMs = Math.Max(0.2, Num("noiseMs", 2.5)) / 1000;
        var modes = new List<(double ratio, double g, double tau)>();
        if (e.TryGetProperty("modes", out var ms))
            foreach (var m in ms.EnumerateArray())
            {
                var a = m.EnumerateArray().Select(x => ToD(Eval(x, vars), 0)).ToArray();
                if (a.Length >= 3 && a[1] != 0) modes.Add((a[0], a[1], Math.Max(0.2, a[2]) / 1000));
            }
        else modes.AddRange(new[] { (1.0, 1.0, 0.012), (2.756, 0.35, 0.005), (5.404, 0.15, 0.002) });
        var rand = Dsp.Rng(seed); var bp = new Dsp.Biquad().SetBandpass(noiseHz, noiseQ, rate);
        int st = (int)(at * rate), cnt = (int)(dur * rate);
        var ph = new double[modes.Count];
        double k = Math.Log(Math.Max(0.05, glide)) / Math.Max(1, cnt);
        for (int i = 0; i < cnt && st + i < L.Length; i++)
        {
            double t = (double)i / rate, fi = f * Math.Exp(k * i), v = 0;
            for (int j = 0; j < modes.Count; j++)
            {
                ph[j] += 2 * Math.PI * fi * modes[j].ratio / rate;
                v += modes[j].g * Math.Exp(-t / modes[j].tau) * Math.Sin(ph[j]);
            }
            if (noise > 0) v += noise * 3 * bp.Process(rand() * 2 - 1) * Math.Exp(-t / noiseMs);
            double env = Math.Min(1, t / att);
            var (gl, gr) = Dsp.PanGains(pan + (panEnd - pan) * i / Math.Max(1, cnt));
            if (st + i < 0) continue;
            L[st + i] += (float)(v * env * gain * gl * Math.Sqrt(2)); R[st + i] += (float)(v * env * gain * gr * Math.Sqrt(2));   // centre = unity
        }
    }

    // ------------------------------------------------------------------ export

    static void Export(string outDir, string bank, string recipe, List<VariantDef> variants, List<SlotResult> results, int rate, List<string> distinct, StringBuilder log)
    {
        Directory.CreateDirectory(outDir);
        var prev = Path.Combine(outDir, "preview"); Directory.CreateDirectory(prev);
        foreach (var r in results) File.WriteAllBytes(Path.Combine(outDir, $"{r.Slot}.{r.Variant}.wav"), Dsp.EncodeWav(r.L, r.R, rate, quad: false, bits: 32));
        foreach (var v in variants)
        {
            var list = results.Where(r => r.Variant == v.Name && !r.Slot.StartsWith("_")).ToList();
            int gap = rate * 450 / 1000, n = list.Sum(r => r.L.Length + gap) + gap;
            var L = new float[n]; var R = new float[n]; int o = gap;
            foreach (var r in list) { Array.Copy(r.L, 0, L, o, r.L.Length); Array.Copy(r.R, 0, R, o, r.R.Length); o += r.L.Length + gap; }
            File.WriteAllBytes(Path.Combine(prev, $"reel.{v.Name}.wav"), Dsp.EncodeWav(L, R, rate, quad: false, bits: 16));
            File.WriteAllBytes(Path.Combine(prev, $"reel.{v.Name}.quad.wav"), Dsp.EncodeWav(L, R, rate, quad: true, bits: 16));
        }
        // manifest (machine) + report (human)
        var man = new
        {
            bank, recipe = Path.GetFileName(recipe), generated = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), rate,
            variants = variants.Select(v => new { name = v.Name, label = v.Label, note = v.Note, auto = v.Auto }),
            slots = results.GroupBy(r => r.Slot).Select(g => new
            {
                slot = g.Key, role = g.First().Role,
                variants = g.Select(r => new
                {
                    variant = r.Variant, ms = Math.Round(r.M.ActiveMs, 1), peak = Math.Round(r.M.Peak, 3), rms20 = Math.Round(r.M.Rms20, 3), crest = Math.Round(r.M.Crest, 2),
                    attackMs = Math.Round(r.M.AttackMs, 2), btCorr = Math.Round(r.BtCorr, 2), btRms20 = Math.Round(r.BtRms20, 3), centroidHz = Math.Round(r.M.CentroidHz), dominantHz = Math.Round(r.M.DominantHz),
                    sub = Math.Round(r.M.Sub, 3), core = Math.Round(r.M.Core + r.M.Low, 3), high = Math.Round(r.M.High, 3), balanceDb = Math.Round(r.M.BalanceDb, 2), drive = Math.Round(r.Drive, 2),
                    warnings = r.Warns
                })
            }),
            alike = distinct
        };
        File.WriteAllText(Path.Combine(outDir, "manifest.json"), JsonSerializer.Serialize(man, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        var md = new StringBuilder();
        md.AppendLine($"# Банк HD-эффектов `{bank}` — отчёт forge");
        md.AppendLine();
        md.AppendLine($"Рецепт `{Path.GetFileName(recipe)}`, сгенерировано {man.generated}, {rate} Гц. Все варианты прошли автоматические проверки " +
                      "(клиппинг, касание лимитера, DC, края, длительность, атака, энергия rms20, спектр: <40 Гц / 40–400 Гц / >700 Гц, баланс Л/П, контраст ролей).");
        md.AppendLine();
        md.AppendLine("| Вариант | Название | Описание |"); md.AppendLine("|---|---|---|");
        foreach (var v in variants) md.AppendLine($"| {v.Name} | {v.Label} | {v.Note} |");
        md.AppendLine();
        md.AppendLine("| Слот | Вар. | мс | пик | rms20 | crest | атака мс | центроид Гц | 40–400 Гц | >700 Гц | Л/П дБ | drive |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in results)
            md.AppendLine($"| {r.Slot} | {r.Variant} | {F(r.M.ActiveMs, "0")} | {F(r.M.Peak)} | {F(r.M.Rms20)} | {F(r.M.Crest, "0.0")} | {F(r.M.AttackMs, "0.0")} | {F(r.M.CentroidHz, "0")} | {F((r.M.Core + r.M.Low) * 100, "0")}% | {F(r.M.High * 100, "0.0")}% | {F(r.M.BalanceDb, "+0.0;-0.0")} | {F(r.Drive, "0.0")} |");
        if (distinct.Count > 0) { md.AppendLine(); md.AppendLine("Похожие варианты (малое спектральное расстояние):"); foreach (var d in distinct) md.AppendLine("- " + d); }
        md.AppendLine();
        md.AppendLine("Файлы: `<слот>.<вариант>.wav` — 32-бит float, стерео (кан. 1 = левый актуатор, 2 = правый); " +
                      "`preview/reel.<вариант>.wav` — все слоты подряд (стерео 16 бит), `preview/reel.<вариант>.quad.wav` — 4 канала (1/2 тишина, 3/4 актуаторы, формат USB-устройства пада и DualSense Haptic Designer).");
        File.WriteAllText(Path.Combine(outDir, "report.md"), md.ToString());
        log.AppendLine($"exported {results.Count} sample(s) -> {outDir} (manifest.json, report.md, preview/)");
    }

    // ------------------------------------------------------------------ helpers

    static JsonElement Pick(JsonElement s, JsonElement defaults, string key) =>
        s.TryGetProperty(key, out var x) ? x : defaults.ValueKind == JsonValueKind.Object && defaults.TryGetProperty(key, out var d) ? d : default;
    static JsonElement? Chk(JsonElement s, JsonElement defaults, string key) =>
        s.TryGetProperty("check", out var c) && c.TryGetProperty(key, out var x) ? x : null;

    static Dsp.EqSettings EqFrom(JsonElement e, Dictionary<string, object?> vars)
    {
        if (e.ValueKind != JsonValueKind.Object) return new Dsp.EqSettings(30, 650);
        var b = e.TryGetProperty("preset", out var pr) && Dsp.EqPresets.TryGetValue(pr.GetString() ?? "", out var p) ? p : new Dsp.EqSettings(30, 650);
        double N(string k, double def) => e.TryGetProperty(k, out var x) ? ToD(Eval(x, vars), def) : def;
        double[]? bands = b.Bands;
        if (e.TryGetProperty("bands", out var bs)) bands = bs.EnumerateArray().Select(x => ToD(Eval(x, vars), 0)).ToArray();
        return new Dsp.EqSettings(N("hpf", b.Hpf), N("lpf", b.Lpf), N("gainDb", b.GainDb), bands);
    }

    static object? Eval(JsonElement e, Dictionary<string, object?> vars)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Number: return e.GetDouble();
            case JsonValueKind.True: return 1.0;
            case JsonValueKind.False: return 0.0;
            case JsonValueKind.String:
                var s = e.GetString() ?? "";
                return s.StartsWith("=") ? Expr.Compile(s[1..]).Eval(n => vars.TryGetValue(n, out var x) ? x : null) : s;
            default: return null;
        }
    }
    static double ToD(object? v, double def) { var d = Expr.ToNum(v); return double.IsNaN(d) || double.IsInfinity(d) ? def : d; }
    static string Str(JsonElement e, string k, string def) => e.TryGetProperty(k, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString()! : def;
    /// <summary>FNV-1a of the slot name (stable across runs and machines), 1..10000.</summary>
    static uint StableSeed(string s) { uint h = 2166136261; foreach (char c in s) { h ^= c; h *= 16777619; } return h % 10000 + 1; }
    static float Max(float[] a) { float m = 0; foreach (var x in a) m = Math.Max(m, Math.Abs(x)); return m; }
    static void Scale(float[] a, double k) { for (int i = 0; i < a.Length; i++) a[i] = (float)(a[i] * k); }
    /// <summary>Soft saturation that keeps the peak: y = peak x tanh(d x / peak) / tanh(d).</summary>
    static float[] Drive(float[] x, double d, double peak)
    {
        if (d < 1e-3) return (float[])x.Clone();
        double tn = Math.Tanh(d); var y = new float[x.Length];
        for (int i = 0; i < x.Length; i++) y[i] = (float)(peak * Math.Tanh(d * x[i] / peak) / tn);
        return y;
    }
}
