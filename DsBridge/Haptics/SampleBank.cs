using System.Text.Json;

namespace DsBridge.Haptics;

/// <summary>Bank of pre-rendered HD effects made by `forge` (haptics/bank/&lt;bank&gt;/&lt;name&gt;.&lt;variant&gt;.wav, stereo:
/// ch1 = left actuator, ch2 = right actuator) and the variant selection used by the rules' "Sample" preset.
///
/// Selection (haptics/bank/selection.json, re-read automatically when it changes - no restart):
///   { "default": "B", "slots": { "lp.guide": "legacy" } }
/// A slot is "&lt;bank&gt;.&lt;name&gt;" (or the "slot" a rule names). The reserved variant "legacy" plays nothing from the bank:
/// the rule's procedural layers marked "ifVariant": "&lt;slot&gt;:legacy" play instead (the pre-forge effects, for A/B).
/// The bench `hd-compare` sets an in-memory override (no file write until the user saves).
/// Nothing here talks to the pad: samples become ordinary rule renderings that go through the same HD engine / mixer.</summary>
public static class SampleBank
{
    public const string Legacy = "legacy";
    static readonly object Lk = new();
    static string _root = "";
    static long _selChecked; static DateTime _selStamp = DateTime.MinValue;
    static string _fileDefault = Legacy; static Dictionary<string, string> _fileSlots = new(StringComparer.OrdinalIgnoreCase);
    static string? _ovDefault; static readonly Dictionary<string, string> _ovSlots = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, (DateTime stamp, float[] l, float[] r)> _cache = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Requested samples that were not found (path), for rules-check / logs.</summary>
    public static readonly HashSet<string> Missing = new(StringComparer.OrdinalIgnoreCase);

    public static string BankDir => Path.Combine(_root, "bank");
    public static string SelectionPath => Path.Combine(BankDir, "selection.json");

    /// <summary>Called by HapticConfig.Load: the bank lives next to haptics.json.</summary>
    public static void Init(string hapticsRoot)
    {
        lock (Lk)
        {
            if (string.Equals(_root, hapticsRoot, StringComparison.OrdinalIgnoreCase)) return;
            _root = hapticsRoot; _selStamp = DateTime.MinValue; _selChecked = 0; _cache.Clear(); _lead.Clear(); Missing.Clear();
            ReloadSelection(force: true);
        }
    }

    static void ReloadSelection(bool force = false)
    {
        long now = Environment.TickCount64;
        if (!force && now - _selChecked < 250) return;
        _selChecked = now;
        try
        {
            if (!File.Exists(SelectionPath)) { _fileDefault = Legacy; _fileSlots.Clear(); _selStamp = DateTime.MinValue; return; }
            var st = File.GetLastWriteTimeUtc(SelectionPath);
            if (!force && st == _selStamp) return;
            using var d = JsonDocument.Parse(File.ReadAllText(SelectionPath), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            _fileDefault = d.RootElement.TryGetProperty("default", out var df) ? df.GetString() ?? Legacy : Legacy;
            _fileSlots = new(StringComparer.OrdinalIgnoreCase);
            if (d.RootElement.TryGetProperty("slots", out var sl))
                foreach (var p in sl.EnumerateObject()) if (p.Value.ValueKind == JsonValueKind.String) _fileSlots[p.Name] = p.Value.GetString()!;
            _selStamp = st;
        }
        catch { /* half-written file: keep the previous selection, retry later */ }
    }

    /// <summary>The variant a slot plays now: bench override (slot, then global) -> selection.json (slot, then default) -> legacy.</summary>
    public static string Variant(string slot)
    {
        lock (Lk)
        {
            ReloadSelection();
            if (_ovSlots.TryGetValue(slot, out var o)) return o;
            if (_ovDefault != null) return _ovDefault;
            return _fileSlots.TryGetValue(slot, out var f) ? f : _fileDefault;
        }
    }

    public static (string def, Dictionary<string, string> slots) FileSelection { get { lock (Lk) { ReloadSelection(true); return (_fileDefault, new(_fileSlots, StringComparer.OrdinalIgnoreCase)); } } }

    /// <summary>Bench override: all slots play `variant` (null = back to the file selection; clears slot overrides).</summary>
    public static void OverrideAll(string? variant) { lock (Lk) { _ovDefault = variant; _ovSlots.Clear(); } }
    public static void OverrideSlot(string slot, string? variant) { lock (Lk) { if (variant == null) _ovSlots.Remove(slot); else _ovSlots[slot] = variant; } }
    public static string? OverrideDefault { get { lock (Lk) return _ovDefault; } }

    /// <summary>Writes selection.json (keeps a .bak of the previous file).</summary>
    public static void SaveSelection(string def, IDictionary<string, string> slots, string note = "")
    {
        Directory.CreateDirectory(BankDir);
        if (File.Exists(SelectionPath)) File.Copy(SelectionPath, SelectionPath + ".bak", true);
        var body = "{\n  \"default\": " + JsonSerializer.Serialize(def) + ",\n  \"slots\": {" +
                   string.Join(",", slots.OrderBy(k => k.Key).Select(kv => $"\n    {JsonSerializer.Serialize(kv.Key)}: {JsonSerializer.Serialize(kv.Value)}")) + (slots.Count > 0 ? "\n  " : "") + "}\n}\n";
        File.WriteAllText(SelectionPath, "// Active HD variant per slot (forge bank). \"legacy\" = the pre-forge procedural effect. Written by hd-compare / by hand." +
                          (note.Length > 0 ? "\n// " + note : "") + "\n" + body);
        lock (Lk) ReloadSelection(true);
    }

    /// <summary>Variants that exist on disk for a bank (from file names name.VARIANT.wav).</summary>
    public static List<string> VariantsOnDisk(string bank)
    {
        var dir = Path.Combine(BankDir, bank);
        if (!Directory.Exists(dir)) return new();
        return Directory.GetFiles(dir, "*.wav").Select(f => Path.GetFileNameWithoutExtension(f)).Where(s => s.Contains('.'))
                        .Select(s => s[(s.LastIndexOf('.') + 1)..]).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList();
    }

    /// <summary>Loads bank/&lt;bank&gt;/&lt;name&gt;.&lt;variant&gt;.wav at `rate` (cached; reloaded when the file changes). null = missing.</summary>
    public static (float[] l, float[] r)? Load(string bank, string name, string variant, int rate)
    {
        var path = Path.Combine(BankDir, bank, $"{name}.{variant}.wav");
        lock (Lk)
        {
            try
            {
                if (!File.Exists(path)) { Missing.Add(path); return null; }
                var st = File.GetLastWriteTimeUtc(path);
                string key = path + "|" + rate;
                if (_cache.TryGetValue(key, out var c) && c.stamp == st) return (c.l, c.r);
                var (sr, ch) = Dsp.DecodeWav(File.ReadAllBytes(path));
                var (l, r) = Dsp.HapticChannels(ch);
                if (sr != rate) { l = Dsp.Resample(l, sr, rate); r = Dsp.Resample(r, sr, rate); }
                _cache[key] = (st, l, r);
                return (l, r);
            }
            catch { Missing.Add(path); return null; }
        }
    }

    /// <summary>Onset of a bank sample in ms: first moment its envelope (max of both actuators) reaches `thr` of the peak.
    /// -1 = missing sample. Same measure as tools/bank_onset.py (game105: slow-swelling game sounds felt late).</summary>
    public static double OnsetMs(string bank, string name, string variant, int rate, double thr)
    {
        var smp = Load(bank, name, variant, rate);
        if (smp == null) return -1;
        var (l, r) = smp.Value;
        int n = Math.Max(l.Length, r.Length);
        float pk = 0;
        for (int i = 0; i < n; i++) pk = Math.Max(pk, Math.Max(i < l.Length ? Math.Abs(l[i]) : 0, i < r.Length ? Math.Abs(r[i]) : 0));
        if (pk <= 0) return 0;
        for (int i = 0; i < n; i++)
            if (Math.Max(i < l.Length ? Math.Abs(l[i]) : 0, i < r.Length ? Math.Abs(r[i]) : 0) >= thr * pk) return 1000.0 * i / rate;
        return 0;
    }

    /// <summary>Lead of a slow-onset sample (ms of sample time): its silent lead-in (to 5 % of peak) is removed whole, its swell
    /// (5 % -> 25 %) by at most <see cref="MaxSwellLeadMs"/> (a long designed swell keeps its shape), keeping a 10 ms natural
    /// lead-in. 0 for samples that reach 25 % within 30 ms. Same rule as the approved weapon fix (game105-106).</summary>
    public const double KeepMs = 10, MaxSwellLeadMs = 250, FastOnsetMs = 30;
    static readonly Dictionary<string, double> _lead = new();
    public static double AutoLeadMs(string bank, string name, string variant, int rate)
    {
        string key = $"{bank}|{name}|{variant}|{rate}";
        lock (Lk) if (_lead.TryGetValue(key, out var c)) return c;
        double t5 = OnsetMs(bank, name, variant, rate, 0.05), t25 = OnsetMs(bank, name, variant, rate, 0.25);
        double lead = t25 <= FastOnsetMs ? 0 : Math.Max(0, Math.Max(0, t5 - KeepMs) + Math.Min(Math.Max(0, t25 - Math.Max(t5, KeepMs)), MaxSwellLeadMs));
        lock (Lk) _lead[key] = lead;
        return lead;
    }

    /// <summary>Applies "lead" ("auto" or ms) to a Sample layer: the sample starts that much earlier - out of the layer's delayMs
    /// while there is delay left, otherwise by skipping into the sample (offsetMs, with a 4 ms fade-in so the cut is no click).</summary>
    public static IReadOnlyDictionary<string, object?> WithLead(IReadOnlyDictionary<string, object?> p, int rate)
    {
        string bank = Presets.S(p, "bank", "lp"), name = Presets.S(p, "name", "");
        string variant = Presets.S(p, "variant", "");
        if (variant.Length == 0) variant = Variant(Presets.S(p, "slot", bank + "." + name));
        if (name.Length == 0 || variant.Equals(Legacy, StringComparison.OrdinalIgnoreCase)) return p;
        double lead = p.TryGetValue("lead", out var lv) && lv is string ls
            ? (ls.Equals("auto", StringComparison.OrdinalIgnoreCase) ? AutoLeadMs(bank, name, variant, rate) : 0)
            : Presets.N(p, "lead", 0);
        lead /= Math.Clamp(Presets.N(p, "rate", 1), 0.25, 4);        // sample time -> playback time
        if (lead <= 0) return p;
        var q = new Dictionary<string, object?>(p, StringComparer.OrdinalIgnoreCase);
        double delay = Math.Max(0, Presets.N(p, "delayMs", 0)), off = Math.Max(0, Presets.N(p, "offsetMs", 0));
        if (delay >= lead) q["delayMs"] = delay - lead;
        else
        {
            q["delayMs"] = 0.0;
            q["offsetMs"] = off + (lead - delay) * Math.Clamp(Presets.N(p, "rate", 1), 0.25, 4);
            if (Presets.N(p, "fadeInMs", 0) <= 0) q["fadeInMs"] = 4.0;
        }
        q.Remove("lead");
        return q;
    }

    /// <summary>The "Sample" preset: plays a bank sample `count` times (`intervalMs` apart), each event with amplitude
    /// x(1 + ampStep*k) and playback rate x(1 + rateStep*k) (pitch and length change together, like a smaller / larger part),
    /// pan sweeping panFrom -> panTo (linear balance like Tick; `cross` = opposite actuator inverted, rocking), optional
    /// trim to durationMs with fadeMs, start offsetMs. intensity scales all. The sum is soft-limited like the mixer
    /// (transparent to 0.855, ceiling 0.93). Selected variant "legacy" -> empty (the rule's legacy layers play).</summary>
    public static (float[] l, float[] r) RenderPreset(IReadOnlyDictionary<string, object?> p, int rate)
    {
        string bank = Presets.S(p, "bank", "lp"), name = Presets.S(p, "name", "");
        string slot = Presets.S(p, "slot", bank + "." + name);
        string variant = Presets.S(p, "variant", "");
        if (variant.Length == 0) variant = Variant(slot);
        var empty = (Array.Empty<float>(), Array.Empty<float>());
        if (name.Length == 0 || variant.Equals(Legacy, StringComparison.OrdinalIgnoreCase)) return empty;
        var smp = Load(bank, name, variant, rate);
        if (smp == null) return empty;
        var (sl, sr) = smp.Value;

        double amp = Presets.N(p, "intensity", 1), interval = Presets.N(p, "intervalMs", 60), aStep = Presets.N(p, "ampStep", 0), rStep = Presets.N(p, "rateStep", 0);
        double baseRate = Math.Clamp(Presets.N(p, "rate", 1), 0.25, 4), cross = Math.Clamp(Presets.N(p, "cross", 0), 0, 1);
        double pan0 = Math.Clamp(Presets.N(p, "panFrom", 0), -1, 1), pan1 = Math.Clamp(Presets.N(p, "panTo", pan0), -1, 1);
        double durMs = Presets.N(p, "durationMs", 0), fadeMs = Math.Max(1, Presets.N(p, "fadeMs", 25)), offMs = Math.Max(0, Presets.N(p, "offsetMs", 0));
        double fadeInMs = Math.Max(0, Presets.N(p, "fadeInMs", 0));
        int count = Math.Max(0, (int)Math.Round(Presets.N(p, "count", 1)));
        if (count == 0 || amp <= 0) return empty;

        // one event = the sample (offset, trim + fade, rate)
        (float[] l, float[] r) Event(double rateK)
        {
            int off = (int)(rate * offMs / 1000);
            float[] Cut(float[] x)
            {
                var y = x.Skip(Math.Min(off, x.Length)).ToArray();
                int fin = Math.Min(y.Length, (int)(rate * fadeInMs / 1000));   // entering mid-sample: no step edge
                for (int i = 0; i < fin; i++) y[i] *= (float)(0.5 - 0.5 * Math.Cos(Math.PI * i / fin));
                if (durMs > 0)
                {
                    int max = (int)(rate * durMs / 1000);
                    if (y.Length > max) y = y.Take(max).ToArray();
                    int fade = Math.Min(y.Length, (int)(rate * fadeMs / 1000));
                    for (int i = 0; i < fade; i++) y[y.Length - 1 - i] *= (float)(0.5 - 0.5 * Math.Cos(Math.PI * i / fade));
                }
                return Math.Abs(rateK - 1) < 1e-6 ? y : Dsp.Resample(y, rateK, 1);
            }
            return (Cut(sl), Cut(sr));
        }
        var events = new List<(int st, float[] l, float[] r, double a, double pan)>();
        int n = 0;
        for (int k = 0; k < count; k++)
        {
            double rk = baseRate * Math.Max(0.25, 1 + rStep * k), a = amp * Math.Max(0, 1 + aStep * k);
            double pan = count > 1 ? pan0 + (pan1 - pan0) * k / (count - 1) : pan0;
            var (el, er) = Event(rk);
            int st = (int)(rate * interval * k / 1000);
            events.Add((st, el, er, a, pan));
            n = Math.Max(n, st + Math.Max(el.Length, er.Length));
        }
        var L = new float[n]; var R = new float[n];
        foreach (var (st, el, er, a, pan) in events)
        {
            double gl = pan > 0 ? 1 - pan : 1, gr = pan < 0 ? 1 + pan : 1;
            double xl = cross > 0 && pan > 0 ? -cross * pan : 0, xr = cross > 0 && pan < 0 ? -cross * -pan : 0;   // inverted copy on the far side
            for (int i = 0; i < Math.Max(el.Length, er.Length); i++)
            {
                double vl = i < el.Length ? el[i] : 0, vr = i < er.Length ? er[i] : 0, mono = 0.5 * (vl + vr);
                L[st + i] += (float)(a * (vl * gl + mono * xl));
                R[st + i] += (float)(a * (vr * gr + mono * xr));
            }
        }
        const float knee = (float)Analysis.LimiterKnee, room = 0.93f - knee;   // ceiling 0.93: never reaches the hard 0.95 clamp of RenderHd
        float Lim(float x) { float ax = Math.Abs(x); return ax <= knee ? x : Math.Sign(x) * (knee + room * (float)Math.Tanh((ax - knee) / room)); }
        for (int i = 0; i < n; i++) { L[i] = Lim(L[i]); R[i] = Lim(R[i]); }
        return (L, R);
    }
}
