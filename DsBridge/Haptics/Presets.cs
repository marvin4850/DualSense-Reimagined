namespace DsBridge.Haptics;

/// <summary>Procedural HD effect templates. Every template renders (left, right) actuator signals at `rate` from resolved
/// parameters; the generic layer parameters (delayMs, gain, gainL, gainR, pan) are applied afterwards for all templates.
/// Templates for the motor (rumble-emulation) path: Rumble and MotorWave give exact motor steps; the other templates are
/// converted from their audio envelope (MotorFromAudio).</summary>
public static class Presets
{
    public static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase) { "delayMs", "gain", "gainL", "gainR", "pan", "seed", "ifVariant" };

    public static readonly Dictionary<string, HashSet<string>> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Impact"] = Set("intensity", "durationMs", "lengthMs", "freqHz", "weight", "edgeHz", "edge", "noise", "attackMs", "decayTauMs", "tail", "tailHz", "tailGain"),
        ["SurfaceContact"] = Set("intensity", "durationMs", "surface", "gait", "lowpass", "toneHz", "tone", "grains", "grainHz", "noise"),
        ["FluidMotion"] = Set("style", "intensity", "durationMs", "bodyHz", "body", "noise", "lowpass", "lagMs"),
        ["Charge"] = Set("intensity", "durationMs", "f0", "f1", "noise", "curve", "pulseHz"),
        ["Release"] = Set("intensity", "durationMs", "freqHz", "noise", "attackMs", "decayTauMs"),
        ["Continuous"] = Set("intensity", "durationMs", "freqHz", "noise", "pulseHz", "depth", "fadeMs", "lowpass", "loop", "wobbleHz"),
        ["Mechanical"] = Set("intensity", "count", "intervalMs", "clickHz", "clickMs", "jitterMs"),
        ["Rumble"] = Set("steps"),
        ["MotorWave"] = Set("ms", "startMs", "peakLeft", "peakRight", "lagMs", "scale"),
        // Tactile Lockpicking 2.0: deterministic resonant bursts (markers, detents, cascades) and a sparse rough glide
        ["Tick"] = Set("intensity", "freqHz", "ms", "attackMs", "decay", "tauMs", "partial", "partialGain", "partialTauMs", "drive", "cross", "count", "intervalMs", "pairGapMs", "pairGain", "freqStep", "ampStep", "panFrom", "panTo"),
        ["Grain"] = Set("intensity", "durationMs", "packetMs", "gapMs", "freqLo", "freqHi", "fadeMs", "panFrom", "panTo", "shape", "accentMs", "accentGain"),
        // forge bank (SampleBank): a pre-rendered, checked HD sample of the selected variant
        ["Sample"] = Set("bank", "name", "slot", "variant", "intensity", "count", "intervalMs", "ampStep", "rateStep", "rate", "panFrom", "panTo", "cross", "durationMs", "fadeMs", "offsetMs", "fadeInMs", "lead"),
    };
    static HashSet<string> Set(params string[] s) => new(s, StringComparer.OrdinalIgnoreCase);

    public static double N(IReadOnlyDictionary<string, object?> p, string k, double def)
    {
        if (!p.TryGetValue(k, out var v) || v == null) return def;
        var d = Expr.ToNum(v);
        return double.IsNaN(d) || double.IsInfinity(d) ? def : d;
    }
    public static string S(IReadOnlyDictionary<string, object?> p, string k, string def) => p.TryGetValue(k, out var v) && v is string s ? s : def;
    static float C(double v) => (float)Math.Clamp(v, -0.95, 0.95);

    /// <summary>Renders one layer to HD audio (generic parameters included).</summary>
    public static (float[] l, float[] r) RenderLayer(string preset, IReadOnlyDictionary<string, object?> p, int rate)
    {
        // "ifVariant": "slot:variant" (or "slot:!variant") - the layer plays only while that slot has (has not) that variant
        // selected in the forge bank (A/B of a sample against the legacy procedural layers)
        if (S(p, "ifVariant", "") is var iv && iv.Length > 0)
        {
            int c = iv.LastIndexOf(':');
            string slot = c > 0 ? iv[..c] : iv, want = c > 0 ? iv[(c + 1)..] : SampleBank.Legacy;
            bool neg = want.StartsWith("!"); if (neg) want = want[1..];
            if (string.Equals(SampleBank.Variant(slot), want, StringComparison.OrdinalIgnoreCase) == neg) return (Array.Empty<float>(), Array.Empty<float>());
        }
        // "lead": slow-onset compensation of a bank sample (pack option onsetLead, SampleBank.WithLead)
        if (p.ContainsKey("lead") && preset.Equals("Sample", StringComparison.OrdinalIgnoreCase)) p = SampleBank.WithLead(p, rate);
        var (l, r) = preset.ToLowerInvariant() switch
        {
            "impact" => Impact(p, rate),
            "surfacecontact" => SurfaceContact(p, rate),
            "fluidmotion" => FluidMotion(p, rate),
            "charge" => Charge(p, rate),
            "release" => Release(p, rate),
            "continuous" => Continuous(p, rate),
            "mechanical" => Mechanical(p, rate),
            "tick" => Tick(p, rate),
            "grain" => Grain(p, rate),
            "sample" => SampleBank.RenderPreset(p, rate),
            "rumble" => HdFx.FromRumble(rate, Steps(p)),
            "motorwave" => HdFx.FromRumble(rate, MotorWaveSteps(p)),
            _ => throw new ArgumentException($"unknown preset '{preset}'")
        };
        return ApplyCommon(l, r, p, rate);
    }

    /// <summary>Exact motor steps for Rumble / MotorWave; null for audio templates (use MotorFromAudio).</summary>
    public static (int delay, byte right, byte left, int ms)[]? RenderMotorExact(string preset, IReadOnlyDictionary<string, object?> p)
    {
        int delay = (int)N(p, "delayMs", 0);
        var steps = preset.ToLowerInvariant() switch { "rumble" => Steps(p), "motorwave" => MotorWaveSteps(p), _ => null };
        if (steps == null) return null;
        double g = N(p, "gain", 1);
        return steps.Select(s => (s.delay + delay, (byte)Math.Clamp(s.right * g, 0, 255), (byte)Math.Clamp(s.left * g, 0, 255), s.ms)).ToArray();
    }

    static (float[] l, float[] r) ApplyCommon(float[] l, float[] r, IReadOnlyDictionary<string, object?> p, int rate)
    {
        double g = N(p, "gain", 1), gl = N(p, "gainL", 1), gr = N(p, "gainR", 1), pan = Math.Clamp(N(p, "pan", 0), -1, 1);
        gl *= pan > 0 ? 1 - pan : 1; gr *= pan < 0 ? 1 + pan : 1;
        int delay = (int)(rate * Math.Max(0, N(p, "delayMs", 0)) / 1000);
        int n = Math.Max(l.Length, r.Length) + delay;
        var ol = new float[n]; var or = new float[n];
        for (int i = 0; i < l.Length; i++) ol[i + delay] = (float)(l[i] * g * gl);
        for (int i = 0; i < r.Length; i++) or[i + delay] = (float)(r[i] * g * gr);
        return (ol, or);
    }

    // ---------------- templates ----------------

    /// <summary>Impact: tone (freqHz x weight) + edge tone (edgeHz x edge) + white noise, attack + exponential decay,
    /// optional low tail. Defaults give the HD landing (HdFx.Land) with gainR 0.9.</summary>
    static (float[] l, float[] r) Impact(IReadOnlyDictionary<string, object?> p, int rate)
    {
        double amp = N(p, "intensity", 0.5), durS = N(p, "durationMs", 150) / 1000.0;
        double f1 = N(p, "freqHz", 55), w1 = N(p, "weight", 0.8), f2 = N(p, "edgeHz", 150), w2 = N(p, "edge", 0.25), noise = N(p, "noise", 0);
        double att = N(p, "attackMs", 3) / 1000.0, tau = N(p, "decayTauMs", durS * 450) / 1000.0;
        double tail = N(p, "tail", 0), tailHz = N(p, "tailHz", 40), tailGain = N(p, "tailGain", 0.55);
        var rnd = new Random((int)N(p, "seed", 11));
        double lenMs = N(p, "lengthMs", 0);
        int n = lenMs > 0 ? (int)(rate * lenMs / 1000) : (int)(rate * (durS * (tail > 0 ? 2.2 : 1.0) + 0.02));
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / rate;
            double a = att > 0 ? Math.Min(1, t / att) : 1;
            double env = a * Math.Exp(-t / Math.Max(1e-4, tau));
            double v = env * amp * (w1 * Math.Sin(2 * Math.PI * f1 * t) + w2 * Math.Sin(2 * Math.PI * f2 * t) + (noise > 0 ? noise * (rnd.NextDouble() * 2 - 1) : 0));
            if (tail > 0 && t > durS * 0.6)
            {
                double tt = t - durS * 0.6, tl = durS * 1.6;
                v += amp * tailGain * tail * Math.Sin(Math.PI * Math.Min(1, tt / tl)) * Math.Sin(2 * Math.PI * tailHz * t);
            }
            s[i] = C(v);
        }
        return (s, (float[])s.Clone());
    }

    static readonly Dictionary<string, (double lp, double toneHz, double tone, int grains, double grainHz, double durMul)> Surfaces = new(StringComparer.OrdinalIgnoreCase)
    {
        ["default"] = (0.08, 90, 0.4, 0, 0, 1),
        ["stone"] = (0.25, 160, 0.3, 0, 0, 1),
        ["wood"] = (0.10, 110, 0.6, 0, 0, 1),
        ["dirt"] = (0.06, 70, 0.3, 2, 180, 1),
        ["grass"] = (0.04, 60, 0.15, 0, 0, 1.2),
        ["sand"] = (0.30, 0, 0.0, 6, 300, 1.2),
        ["gravel"] = (0.30, 80, 0.2, 8, 260, 1),
        ["metal"] = (0.35, 240, 0.5, 0, 0, 1),
        ["water"] = (0.03, 50, 0.2, 0, 0, 2),
        ["snow"] = (0.05, 0, 0.0, 4, 150, 1.3),
        ["mud"] = (0.02, 45, 0.3, 1, 90, 1.5),
    };
    static readonly Dictionary<string, double> Gaits = new(StringComparer.OrdinalIgnoreCase) { ["crouch"] = 0.5, ["walk"] = 1.0, ["run"] = 1.4, ["sprint"] = 1.7 };

    /// <summary>SurfaceContact: a short step / slide contact; texture from a surface table (overridable: lowpass, toneHz,
    /// tone, grains, grainHz), strength x gait (crouch 0.5 .. sprint 1.7).</summary>
    static (float[] l, float[] r) SurfaceContact(IReadOnlyDictionary<string, object?> p, int rate)
    {
        var sf = Surfaces.TryGetValue(S(p, "surface", "default"), out var x) ? x : Surfaces["default"];
        double amp = N(p, "intensity", 0.15) * (Gaits.TryGetValue(S(p, "gait", "walk"), out var gm) ? gm : 1);
        double durS = N(p, "durationMs", 45) / 1000.0 * sf.durMul;
        double lp = N(p, "lowpass", sf.lp), toneHz = N(p, "toneHz", sf.toneHz), tone = N(p, "tone", sf.tone), noiseW = N(p, "noise", 1);
        int grains = (int)N(p, "grains", sf.grains); double grainHz = N(p, "grainHz", sf.grainHz);
        var rnd = new Random((int)N(p, "seed", 3));
        int n = (int)(rate * (durS + 0.01));
        var s = new float[n]; double f = 0;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / rate, env = Math.Min(1, t / 0.002) * Math.Exp(-t / Math.Max(1e-4, durS * 0.35));
            f += lp * ((rnd.NextDouble() * 2 - 1) - f);
            s[i] = (float)(env * amp * (noiseW * f * 3 + (toneHz > 0 ? tone * Math.Sin(2 * Math.PI * toneHz * t) : 0)));
        }
        for (int g = 0; g < grains && grainHz > 0; g++)
        {
            int st = (int)(rnd.NextDouble() * durS * rate), len = (int)(rate / grainHz);
            for (int i = 0; i < len && st + i < n; i++) s[st + i] += (float)(amp * 0.5 * Math.Sin(2 * Math.PI * i / len));
        }
        for (int i = 0; i < n; i++) s[i] = C(s[i]);
        return (s, (float[])s.Clone());
    }

    /// <summary>FluidMotion: water. style dive (the approved dive swell, no bubbles), surface, wade, stroke, custom
    /// (filtered-noise "water" + slow pressure tone under a sin^1.5 swell, right side lagging by lagMs).</summary>
    static (float[] l, float[] r) FluidMotion(IReadOnlyDictionary<string, object?> p, int rate)
    {
        double k = N(p, "intensity", 1);
        var style = S(p, "style", "dive").ToLowerInvariant();
        (float[] l, float[] r) w = style switch
        {
            "dive" => HdFx.Dive(rate),
            "surface" => HdFx.Surface(rate),
            "wade" => HdFx.Wade(rate),
            "stroke" => Swell(rate, 0.45, 55, 0.25, 6, 0.03, 0.06, (int)N(p, "seed", 21)),
            _ => Swell(rate, N(p, "durationMs", 1300) / 1000.0, N(p, "bodyHz", 38), N(p, "body", 0.35), N(p, "noise", 9), N(p, "lowpass", 0.02), N(p, "lagMs", 220) / 1000.0, (int)N(p, "seed", 1))
        };
        if (k != 1) { Scale(w.l, k); Scale(w.r, k); }
        return w;
    }

    static (float[] l, float[] r) Swell(int rate, double swellS, double bodyHz, double body, double noise, double lp, double lagS, int seed)
    {
        float[] One(double lag, int sd)
        {
            var rnd = new Random(sd);
            int n = (int)(rate * (swellS + Math.Abs(lagS) + 0.05));
            var s = new float[n]; double a = 0, b = 0;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / rate - lag;
                double env = t <= 0 || t >= swellS ? 0 : Math.Pow(Math.Sin(Math.PI * t / swellS), 1.5);
                a += lp * ((rnd.NextDouble() * 2 - 1) - a); b += lp * (a - b);
                s[i] = C(env * (b * noise + Math.Sin(2 * Math.PI * bodyHz * t) * body));
            }
            return s;
        }
        return (One(Math.Max(0, -lagS), seed), One(Math.Max(0, lagS), seed + 1));
    }

    /// <summary>Charge: rising tension — amplitude (t/T)^curve, frequency f0 -> f1, optional pulsing, short fade-out.</summary>
    static (float[] l, float[] r) Charge(IReadOnlyDictionary<string, object?> p, int rate)
    {
        double amp = N(p, "intensity", 0.5), durS = N(p, "durationMs", 800) / 1000.0, f0 = N(p, "f0", 60), f1 = N(p, "f1", 140);
        double noise = N(p, "noise", 0.1), curve = N(p, "curve", 1.5), pulse = N(p, "pulseHz", 0);
        var rnd = new Random((int)N(p, "seed", 5));
        int n = (int)(rate * durS); var s = new float[n]; double ph = 0;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / rate, x = t / durS;
            ph += 2 * Math.PI * (f0 + (f1 - f0) * x) / rate;
            double env = Math.Pow(x, curve) * Math.Min(1, (durS - t) / 0.02) * (pulse > 0 ? 0.6 + 0.4 * Math.Sin(2 * Math.PI * pulse * t) : 1);
            s[i] = C(amp * env * (Math.Sin(ph) + noise * (rnd.NextDouble() * 2 - 1)));
        }
        return (s, (float[])s.Clone());
    }

    /// <summary>Release: sharp transient (shot, throw, spell launch) with fast decay.</summary>
    static (float[] l, float[] r) Release(IReadOnlyDictionary<string, object?> p, int rate)
    {
        double amp = N(p, "intensity", 0.7), durS = N(p, "durationMs", 120) / 1000.0, f = N(p, "freqHz", 120), noise = N(p, "noise", 0.3);
        double att = N(p, "attackMs", 1) / 1000.0, tau = N(p, "decayTauMs", durS * 300) / 1000.0;
        var rnd = new Random((int)N(p, "seed", 9));
        int n = (int)(rate * durS); var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / rate, env = Math.Min(1, t / Math.Max(1e-4, att)) * Math.Exp(-t / Math.Max(1e-4, tau));
            s[i] = C(amp * env * (Math.Sin(2 * Math.PI * f * t) + noise * (rnd.NextDouble() * 2 - 1)));
        }
        return (s, (float[])s.Clone());
    }

    /// <summary>Continuous: a steady or pulsing texture (burning, freezing, danger) for durationMs (the safety maximum);
    /// stopped early by the rule's stop phases.</summary>
    static (float[] l, float[] r) Continuous(IReadOnlyDictionary<string, object?> p, int rate)
    {
        double amp = N(p, "intensity", 0.3), durS = N(p, "durationMs", 10000) / 1000.0, f = N(p, "freqHz", 45), noise = N(p, "noise", 0.5);
        double pulse = N(p, "pulseHz", 0), depth = N(p, "depth", 0.5), fade = N(p, "fadeMs", 80) / 1000.0, lp = N(p, "lowpass", 0.05);
        bool loop = N(p, "loop", 0) > 0; double wob = N(p, "wobbleHz", 0);
        var rnd = new Random((int)N(p, "seed", 13));
        if (loop)
        {   // one seamless period: whole cycles of every periodic part, ends crossfaded into the start; the mixer loops it
            double per = Math.Max(0.25, durS);
            if (pulse > 0) per = Math.Max(1, Math.Round(per * pulse)) / pulse;
            if (wob > 0) per = Math.Max(1, Math.Round(per * wob)) / wob;
            f = Math.Max(1, Math.Round(f * per)) / per;
            durS = per;
        }
        int n = (int)(rate * durS); var s = new float[n]; var sr = new float[n]; double a = 0, b = 0;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / rate;
            a += lp * ((rnd.NextDouble() * 2 - 1) - a); b += lp * ((rnd.NextDouble() * 2 - 1) - b);
            double edge = loop ? 1 : Math.Min(1, t / fade) * Math.Min(1, (durS - t) / fade);
            double env = edge * (pulse > 0 ? 1 - depth + depth * (0.5 + 0.5 * Math.Sin(2 * Math.PI * pulse * t)) : 1);
            double w = wob > 0 ? 0.5 + 0.5 * Math.Sin(2 * Math.PI * wob * t) : 0.5;   // wobble: left/right sway
            double tone = Math.Sin(2 * Math.PI * f * t) * (1 - noise);
            s[i] = C(amp * env * (tone + a * 4 * noise) * (wob > 0 ? 2 * (1 - w) : 1));
            sr[i] = C(amp * env * (tone + b * 4 * noise) * (wob > 0 ? 2 * w : 1));
        }
        if (loop)
        {
            int xf = Math.Min(n / 4, rate / 50);   // 20 ms: blend the tail into the head so the wrap is seamless
            for (int i = 0; i < xf; i++)
            {
                float k = (float)i / xf;
                s[i] = s[i] * k + s[n - xf + i] * (1 - k); sr[i] = sr[i] * k + sr[n - xf + i] * (1 - k);
            }
            Array.Resize(ref s, n - xf); Array.Resize(ref sr, n - xf);
        }
        return (s, sr);
    }

    /// <summary>Mechanical: a series of short clicks (lever, lock pins, door latch).</summary>
    static (float[] l, float[] r) Mechanical(IReadOnlyDictionary<string, object?> p, int rate)
    {
        double amp = N(p, "intensity", 0.4), interval = N(p, "intervalMs", 90) / 1000.0, hz = N(p, "clickHz", 220), clickS = N(p, "clickMs", 12) / 1000.0, jitter = N(p, "jitterMs", 0) / 1000.0;
        int count = Math.Max(1, (int)N(p, "count", 3));
        var rnd = new Random((int)N(p, "seed", 17));
        int n = (int)(rate * (interval * (count - 1) + clickS + jitter + 0.01)); var s = new float[n];
        for (int c = 0; c < count; c++)
        {
            int st = (int)(rate * (interval * c + jitter * rnd.NextDouble())), len = (int)(rate * clickS);
            for (int i = 0; i < len && st + i < n; i++)
            {
                double t = (double)i / rate;
                s[st + i] += (float)(amp * Math.Exp(-t / (clickS * 0.3)) * Math.Sin(2 * Math.PI * hz * t));
            }
        }
        for (int i = 0; i < n; i++) s[i] = C(s[i]);
        return (s, (float[])s.Clone());
    }

    /// <summary>Tick: `count` identical-grammar resonant bursts (no randomness). One burst = sine `freqHz`, soft linear attack
    /// `attackMs` (1.5), exponential decay tau = `decay` x `ms` (0.35), cosine fade over the last 2 ms, length `ms`.
    /// Event k (0-based) starts at k x `intervalMs`; its frequency x (1 + `freqStep` x k) and amplitude x (1 + `ampStep` x k)
    /// (deterministic variation, e.g. a cascade that descends); `pairGapMs` > 0 makes every event a pair (second burst
    /// `pairGapMs` later at `pairGain` 0.8) = "two scraping transients"; pan sweeps from `panFrom` to `panTo` over the events
    /// (-1 = left actuator only, +1 = right only).</summary>
    static (float[] l, float[] r) Tick(IReadOnlyDictionary<string, object?> p, int rate)
    {
        double amp = N(p, "intensity", 0.3), hz = N(p, "freqHz", 190), ms = Math.Max(2, N(p, "ms", 14)), att = Math.Max(0, N(p, "attackMs", 1.5));
        double decay = Math.Max(0.05, N(p, "decay", 0.35)), interval = N(p, "intervalMs", 60), pairGap = N(p, "pairGapMs", 0), pairGain = N(p, "pairGain", 0.8);
        // "ring": tauMs = absolute decay (overrides decay x ms); partial = an inharmonic overtone ratio (metal bar: 2.76) with its own gain / decay
        double tauAbs = N(p, "tauMs", 0), partial = N(p, "partial", 0), partialGain = N(p, "partialGain", 0.3), partialTau = N(p, "partialTauMs", 0);
        // drive > 0: soft saturation tanh(drive*x)/tanh(drive) - a harder, more "square" metallic click with more energy at the same peak
        double drive = Math.Max(0, N(p, "drive", 0)), driveNorm = drive > 0 ? Math.Tanh(drive) : 1;
        // cross > 0 with a side pan: the opposite actuator plays the same burst INVERTED at cross x |pan| - the shell rocks toward
        // the side instead of shaking as a whole (better left / right localisation)
        double cross = Math.Clamp(N(p, "cross", 0), 0, 1);
        double fStep = N(p, "freqStep", 0), aStep = N(p, "ampStep", 0), pan0 = Math.Clamp(N(p, "panFrom", 0), -1, 1), pan1 = Math.Clamp(N(p, "panTo", pan0), -1, 1);
        int count = Math.Max(0, (int)Math.Round(N(p, "count", 1)));
        if (count == 0 || amp <= 0) return (Array.Empty<float>(), Array.Empty<float>());
        double lastStart = interval * (count - 1) + (pairGap > 0 ? pairGap : 0);
        int n = (int)(rate * (lastStart + ms + 5) / 1000);
        var L = new float[n]; var R = new float[n];
        int len = (int)(rate * ms / 1000), fadeN = Math.Max(1, rate * 2 / 1000);
        void Burst(double startMs, double f, double a, double pan)
        {
            int st = (int)(rate * startMs / 1000);
            double gl = pan > 0 ? 1 - pan : 1, gr = pan < 0 ? 1 + pan : 1, attS = att / 1000.0;
            if (cross > 0 && pan > 0) gl -= cross * pan;
            if (cross > 0 && pan < 0) gr -= cross * -pan;
            double tau = tauAbs > 0 ? tauAbs / 1000.0 : decay * ms / 1000.0, ptau = partialTau > 0 ? partialTau / 1000.0 : tau * 0.5;
            for (int i = 0; i < len && st + i < n; i++)
            {
                double t = (double)i / rate;
                double att01 = attS > 0 ? Math.Min(1, t / attS) : 1, fade = i > len - fadeN ? 0.5 - 0.5 * Math.Cos(Math.PI * (len - i) / fadeN) : 1;
                double raw = Math.Exp(-t / tau) * Math.Sin(2 * Math.PI * f * t)
                           + (partial > 0 ? partialGain * Math.Exp(-t / ptau) * Math.Sin(2 * Math.PI * f * partial * t) : 0);
                if (drive > 0) raw = Math.Tanh(drive * raw) / driveNorm;
                double v = a * att01 * fade * raw;
                L[st + i] += (float)(v * gl); R[st + i] += (float)(v * gr);
            }
        }
        for (int k = 0; k < count; k++)
        {
            double f = hz * Math.Max(0.2, 1 + fStep * k), a = amp * Math.Max(0, 1 + aStep * k);
            double pan = count > 1 ? pan0 + (pan1 - pan0) * k / (count - 1) : pan0;
            Burst(interval * k, f, a, pan);
            if (pairGap > 0) Burst(interval * k + pairGap, f * 1.08, a * pairGain, pan);
        }
        for (int i = 0; i < n; i++) { L[i] = C(L[i]); R[i] = C(R[i]); }
        return (L, R);
    }

    /// <summary>Grain: a sparse rough glide - short Hann-windowed packets (`packetMs` 8) separated by `gapMs` (14) over
    /// `durationMs`, each at a deterministic pseudo-random frequency in [`freqLo`, `freqHi`] (seeded by `seed`), overall
    /// fade in / out `fadeMs`, pan sweeping `panFrom` -> `panTo`. No white noise.</summary>
    static (float[] l, float[] r) Grain(IReadOnlyDictionary<string, object?> p, int rate)
    {
        double amp = N(p, "intensity", 0.1), dur = Math.Clamp(N(p, "durationMs", 250), 10, 1500), pk = Math.Max(2, N(p, "packetMs", 8)), gap = Math.Max(0, N(p, "gapMs", 14));
        double lo = N(p, "freqLo", 90), hi = Math.Max(lo, N(p, "freqHi", 220)), fade = Math.Max(1, N(p, "fadeMs", 40));
        double pan0 = Math.Clamp(N(p, "panFrom", 0), -1, 1), pan1 = Math.Clamp(N(p, "panTo", pan0), -1, 1);
        bool sharp = S(p, "shape", "hann").Equals("sharp", StringComparison.OrdinalIgnoreCase);
        double accentEvery = N(p, "accentMs", 0), accentGain = N(p, "accentGain", 1.0);
        var rnd = new Random((int)N(p, "seed", 23));
        int n = (int)(rate * (dur + 5) / 1000); var L = new float[n]; var R = new float[n];
        if (amp <= 0) return (L, R);
        int plen = (int)(rate * pk / 1000);
        for (double at = 0; at + pk <= dur; at += pk + gap)
        {
            double f = lo + (hi - lo) * rnd.NextDouble(), frac = at / dur, pan = pan0 + (pan1 - pan0) * frac;
            double envG = Math.Min(1, Math.Min(at / fade, (dur - at) / fade));
            double gl = pan > 0 ? 1 - pan : 1, gr = pan < 0 ? 1 + pan : 1;
            if (accentEvery > 0 && (int)(at / accentEvery) != (int)((at + pk + gap) / accentEvery)) envG *= accentGain;   // stick-slip catch
            int st = (int)(rate * at / 1000), edge = Math.Max(1, rate / 1000);
            for (int i = 0; i < plen && st + i < n; i++)
            {
                double w = sharp ? Math.Min(1.0, Math.Min((double)i / edge, (double)(plen - i) / (1.5 * edge))) : 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / plen);
                double v = amp * envG * w * Math.Sin(2 * Math.PI * f * i / rate);
                L[st + i] += (float)(v * gl); R[st + i] += (float)(v * gr);
            }
        }
        for (int i = 0; i < n; i++) { L[i] = C(L[i]); R[i] = C(R[i]); }
        return (L, R);
    }

    static void Scale(float[] a, double k) { for (int i = 0; i < a.Length; i++) a[i] = C(a[i] * k); }

    // ---------------- motor helpers ----------------

    /// <summary>"steps": [[delayMs, right 0-255, left 0-255, ms], ...]</summary>
    public static (int delay, byte right, byte left, int ms)[] Steps(IReadOnlyDictionary<string, object?> p)
    {
        if (!p.TryGetValue("steps", out var v) || v is not List<object?> list) return Array.Empty<(int, byte, byte, int)>();
        var res = new List<(int, byte, byte, int)>();
        foreach (var o in list)
        {
            if (o is not List<object?> s || s.Count < 4) continue;
            double d = Expr.ToNum(s[0]), rr = Expr.ToNum(s[1]), ll = Expr.ToNum(s[2]), ms = Expr.ToNum(s[3]);
            if (new[] { d, rr, ll, ms }.Any(double.IsNaN)) continue;
            res.Add(((int)Math.Round(d), (byte)Math.Round(Math.Clamp(rr, 0, 255)), (byte)Math.Round(Math.Clamp(ll, 0, 255)), (int)Math.Round(ms)));
        }
        return res.ToArray();
    }

    static (int delay, byte right, byte left, int ms)[] MotorWaveSteps(IReadOnlyDictionary<string, object?> p) =>
        RumbleMixer.Wave((int)N(p, "ms", 1100), (int)N(p, "startMs", 0), (int)N(p, "peakLeft", 200), (int)N(p, "peakRight", 150), (int)N(p, "lagMs", 220), N(p, "scale", 1));

    /// <summary>Motor steps from an HD signal: 30 ms windows, peak per actuator mapped back through the HD loudness curve
    /// (inverse of FromRumble: amp = 255 * (peak/0.85)^(1/1.4)).</summary>
    public static (int delay, byte right, byte left, int ms)[] MotorFromAudio(float[] l, float[] r, int rate)
    {
        const int win = 30;
        int w = rate * win / 1000, n = Math.Max(l.Length, r.Length);
        var res = new List<(int, byte, byte, int)>();
        for (int st = 0, k = 0; st < n; st += w, k++)
        {
            float pl = 0, pr = 0;
            for (int i = st; i < Math.Min(n, st + w); i++) { if (i < l.Length) pl = Math.Max(pl, Math.Abs(l[i])); if (i < r.Length) pr = Math.Max(pr, Math.Abs(r[i])); }
            byte bl = (byte)Math.Clamp(255 * Math.Pow(Math.Min(1, pl / 0.85), 1 / 1.4), 0, 255), br = (byte)Math.Clamp(255 * Math.Pow(Math.Min(1, pr / 0.85), 1 / 1.4), 0, 255);
            if (bl > 3 || br > 3) res.Add((k * win, br, bl, win));
        }
        return res.ToArray();
    }
}
