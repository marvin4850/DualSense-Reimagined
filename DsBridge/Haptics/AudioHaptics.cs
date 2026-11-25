using System.Globalization;
using System.Text.Json;

namespace DsBridge.Haptics;

/// <summary>Game sound -> HD haptics (forge layer "audio"). The original sound is the source of TIMING and TEXTURE, not a signal
/// for the actuators: it is analysed (band envelopes, transients, decay, spectrum) and the tactile part is re-synthesised in the
/// actuator band by one of six methods, each with its own carrier band and default EQ (no universal equaliser):
///   impact   - every strong transient -> a body thump (modal, frequency from the sound's low / mid / high balance, decay measured
///              from the sound) + the sound's high-band envelope as grit on a 240 Hz noise band
///   click    - transients -> short bright metallic ticks (bar modes 1 / 2.756), decay 2-8 ms
///   rattle   - dense transient trains -> one small tick per transient (deterministic pitch variation), strength per transient
///   scrape   - high-band (0.8-6 kHz) grain envelope modulates a noise band + stick-slip ticks at the grain peaks
///   friction - mid-band envelope (slow) on a low noise band + sub tone: smooth rubbing
///   texture  - full envelope (slow) on a wide low noise band with AM from the high-band flux: continuous textures
/// Speaker never used; output goes through the usual forge pipeline (EQ, energy, normalisation, checks incl. Bluetooth).</summary>
public static class AudioHaptics
{
    public const int Rate = 48000;

    public sealed class Features
    {
        public string Name = ""; public double DurMs, ActiveMs, Peak, Centroid, Low, Mid, High, OnsetRate, Crest, DecayMs, Sustain; public int Onsets, Channels;
        public string Method = "";
    }

    /// <summary>Default EQ per method (hpf, lpf): different sounds need different processing.</summary>
    public static readonly Dictionary<string, (double hpf, double lpf, double[] bands)> MethodEq = new(StringComparer.OrdinalIgnoreCase)
    {
        ["impact"] = (35, 420, new double[] { 0, 4, 3, 0, -3, 0, 0, 0, 0, 0 }),
        ["click"] = (90, 620, new double[] { -12, -6, 0, 3, 3, 0, 0, 0, 0, 0 }),
        ["rattle"] = (80, 560, new double[] { -12, -3, 2, 3, 0, 0, 0, 0, 0, 0 }),
        ["scrape"] = (90, 480, new double[] { -12, -3, 3, 3, 0, 0, 0, 0, 0, 0 }),
        ["friction"] = (40, 320, new double[] { 0, 3, 3, -2, -6, 0, 0, 0, 0, 0 }),
        ["texture"] = (35, 360, new double[] { 0, 3, 2, 0, -4, 0, 0, 0, 0, 0 }),
    };

    // ------------------------------------------------------------------ loading / analysis

    public static (float[] l, float[] r, int channels) Load(string path)
    {
        var (sr, ch) = Dsp.DecodeWav(File.ReadAllBytes(path));
        float[] l = ch[0], r = ch.Length > 1 ? ch[1] : ch[0];
        if (sr != Rate) { l = Dsp.Resample(l, sr, Rate); r = Dsp.Resample(r, sr, Rate); }
        return (l, r, ch.Length);
    }

    static float[] Mono(float[] l, float[] r) { var m = new float[Math.Max(l.Length, r.Length)]; for (int i = 0; i < m.Length; i++) m[i] = 0.5f * ((i < l.Length ? l[i] : 0) + (i < r.Length ? r[i] : 0)); return m; }

    /// <summary>Band-limited copy (two cascaded RBJ high-pass + low-pass).</summary>
    public static float[] Band(float[] x, double lo, double hi)
    {
        var f = new List<Dsp.Biquad>();
        if (lo > 0) { f.Add(new Dsp.Biquad().SetHighpass(lo, 0.707, Rate)); f.Add(new Dsp.Biquad().SetHighpass(lo, 0.707, Rate)); }
        if (hi > 0) { f.Add(new Dsp.Biquad().SetLowpass(hi, 0.707, Rate)); f.Add(new Dsp.Biquad().SetLowpass(hi, 0.707, Rate)); }
        var y = new float[x.Length];
        for (int i = 0; i < x.Length; i++) { double v = x[i]; foreach (var b in f) v = b.Process(v); y[i] = (float)v; }
        return y;
    }

    /// <summary>Peak envelope follower (attack / release in ms).</summary>
    public static float[] Env(float[] x, double attMs, double relMs)
    {
        double a = Math.Exp(-1.0 / (Rate * Math.Max(0.05, attMs) / 1000)), r = Math.Exp(-1.0 / (Rate * Math.Max(0.05, relMs) / 1000)), e = 0;
        var y = new float[x.Length];
        for (int i = 0; i < x.Length; i++) { double v = Math.Abs(x[i]); e = v > e ? a * e + (1 - a) * v : r * e + (1 - r) * v; y[i] = (float)e; }
        return y;
    }

    /// <summary>Transients: rise of the log envelope over 4 ms, peaks above `rel` x the largest rise, at least `gapMs` apart.
    /// Strength = envelope peak in the 30 ms after the onset.</summary>
    public static List<(int at, double s)> Onsets(float[] x, double gapMs = 25, double rel = 0.25, int max = 64)
    {
        var env = Env(x, 0.3, 6);
        int hop = Rate * 4 / 1000, gap = (int)(Rate * gapMs / 1000), win = Rate * 30 / 1000;
        var rise = new double[env.Length];
        double best = 0;
        for (int i = hop; i < env.Length; i++) { rise[i] = Math.Max(0, Math.Log(env[i] + 1e-4) - Math.Log(env[i - hop] + 1e-4)) * Math.Sqrt(env[i]); best = Math.Max(best, rise[i]); }
        var res = new List<(int, double)>();
        if (best <= 0) return res;
        int last = -gap;
        for (int i = 1; i < env.Length - 1; i++)
        {
            if (rise[i] < rel * best || rise[i] < rise[i - 1] || rise[i] < rise[i + 1] || i - last < gap) continue;
            double pk = 0; for (int j = i; j < Math.Min(env.Length, i + win); j++) pk = Math.Max(pk, env[j]);
            res.Add((Math.Max(0, i - hop), pk)); last = i;
        }
        return res.OrderByDescending(o => o.Item2).Take(max).OrderBy(o => o.Item1).ToList();
    }

    public static Features Analyze(string path)
    {
        var (l, r, chn) = Load(path);
        var m = Mono(l, r);
        var f = new Features { Name = Path.GetFileNameWithoutExtension(path), DurMs = 1000.0 * m.Length / Rate, Channels = chn };
        if (m.Length == 0) return f;
        var env = Env(m, 0.5, 20);
        double pk = env.Max(); f.Peak = m.Max(v => Math.Abs(v));
        int first = Array.FindIndex(env, v => v > pk * 0.05), last = Array.FindLastIndex(env, v => v > pk * 0.05);
        f.ActiveMs = first < 0 ? 0 : 1000.0 * (last - first) / Rate;
        f.Sustain = first < 0 ? 0 : (double)env.Skip(first).Take(last - first + 1).Count(v => v > pk * 0.25) / Math.Max(1, last - first + 1);
        int ipk = Array.IndexOf(env, (float)pk); int idec = ipk; while (idec < env.Length && env[idec] > pk * 0.1) idec++;
        f.DecayMs = 1000.0 * (idec - ipk) / Rate;
        var ons = Onsets(m, 25, 0.2);
        f.Onsets = ons.Count; f.OnsetRate = f.ActiveMs > 0 ? 1000.0 * ons.Count / Math.Max(50, f.ActiveMs) : 0;
        double rms = Math.Sqrt(m.Sum(v => (double)v * v) / m.Length); f.Crest = rms > 0 ? f.Peak / rms : 0;
        double e(float[] x) => x.Sum(v => (double)v * v);
        double tot = e(m) + 1e-12, low = e(Band(m, 0, 150)), mid = e(Band(m, 150, 1000)), high = e(Band(m, 1000, 0));
        f.Low = low / tot; f.Mid = mid / tot; f.High = high / tot;
        f.Centroid = SpectralCentroidFull(m);
        f.Method = Suggest(f);
        return f;
    }

    static double SpectralCentroidFull(float[] x)
    {
        int n = 1; while (n < x.Length && n < (1 << 16)) n <<= 1;
        var buf = new System.Numerics.Complex[Math.Max(n, 1024)];
        for (int i = 0; i < Math.Min(x.Length, buf.Length); i++) buf[i] = x[i];
        Analysis.Fft(buf);
        double s = 0, w = 0, bw = (double)Rate / buf.Length;
        for (int k = 1; k < buf.Length / 2; k++) { double p = buf[k].Magnitude; s += p; w += p * k * bw; }
        return s > 0 ? w / s : 0;
    }

    /// <summary>Method suggestion from the features (the matcher / batch generator use it; a recipe may override).</summary>
    public static string Suggest(Features f)
    {
        if (f.OnsetRate > 14 && f.ActiveMs > 250) return f.Sustain > 0.45 ? "scrape" : "rattle";
        if (f.Sustain > 0.5 && f.ActiveMs > 450) return f.High > 0.45 ? "scrape" : f.Low > 0.35 ? "texture" : "friction";
        if (f.ActiveMs < 900 && f.Onsets <= 3) return f.Centroid > 2200 && f.DecayMs < 90 ? "click" : "impact";
        return f.Onsets > 6 ? "rattle" : "impact";
    }

    // ------------------------------------------------------------------ rendering

    /// <summary>Renders one "audio" layer into L / R (mixing). num(key, default) / str(key, default) read the layer's parameters.</summary>
    public static void Render(string wavPath, Func<string, double, double> num, Func<string, string, string> str, float[] L, float[] R, uint seed)
    {
        var (sl, sr, _) = Load(wavPath);
        int st = (int)(Rate * num("startMs", 0) / 1000), maxN = (int)(Rate * num("maxMs", 2000) / 1000);
        float[] Crop(float[] x) => x.Skip(Math.Min(st, x.Length)).Take(maxN).ToArray();
        sl = Crop(sl); sr = Crop(sr);
        bool stereo = num("stereo", 0) > 0;
        string method = str("method", "impact").ToLowerInvariant();
        var rand = Dsp.Rng(seed);
        var (ol, orr) = stereo ? (One(method, sl, num, rand), One(method, sr, num, Dsp.Rng(seed + 7))) : One2(method, Mono(sl, sr), num, rand);
        // normalise the layer (the slot pipeline sets the final level); per actuator gain / pan / lag
        float pk = Math.Max(ol.Max(Math.Abs), orr.Max(Math.Abs));
        if (pk < 1e-6) return;
        double g = num("gain", 1) / pk, pan = Math.Clamp(num("pan", 0), -1, 1);
        double gl = g * num("gainL", 1) * (pan > 0 ? 1 - pan : 1), gr = g * num("gainR", 1) * (pan < 0 ? 1 + pan : 1);
        int at = (int)(Rate * num("at", 0) / 1000), lag = (int)(Rate * num("lagMs", 0) / 1000);
        for (int i = 0; i < ol.Length; i++)
        {
            if (at + i >= 0 && at + i < L.Length) L[at + i] += (float)(ol[i] * gl);
            if (at + lag + i >= 0 && at + lag + i < R.Length) R[at + lag + i] += (float)(orr[i] * gr);
        }
    }

    static (float[], float[]) One2(string method, float[] m, Func<string, double, double> num, Func<double> rand) { var y = One(method, m, num, rand); return (y, (float[])y.Clone()); }

    static float[] One(string method, float[] x, Func<string, double, double> num, Func<double> rand)
    {
        int n = x.Length + Rate / 5;
        var y = new float[n];
        var full = Env(x, 0.5, 15); double fpk = Math.Max(1e-6, full.Max());
        double low = x.Length > 0 ? Band(x, 0, 150).Sum(v => (double)v * v) : 0, all = x.Sum(v => (double)v * v) + 1e-12, highE = Band(x, 1000, 0).Sum(v => (double)v * v);
        switch (method)
        {
            case "impact":
            case "click":
            {
                bool click = method == "click";
                var ons = Onsets(x, num("gapMs", click ? 12 : 25), num("threshold", click ? 0.2 : 0.3), (int)num("maxHits", click ? 16 : 6));
                if (ons.Count == 0) ons.Add((0, fpk));
                double smax = ons.Max(o => o.s);
                double autoHz = click ? 260 : low / all > 0.4 ? 75 : highE / all > 0.5 ? 200 : 140;
                double f0 = num("carrierHz", autoHz);
                foreach (var (at, s) in ons)
                {
                    // decay measured from the sound: time until the envelope falls to 30 % after the hit
                    int k = at; double pk = s; while (k < full.Length && full[k] > pk * 0.3) k++;
                    double tau = click ? Math.Clamp(1000.0 * (k - at) / Rate / 3, 2, 8) : Math.Clamp(1000.0 * (k - at) / Rate / 1.5, 6, 80);
                    tau *= num("decayScale", 1);
                    double a = Math.Pow(s / smax, num("dynamics", 0.8));
                    Modal(y, at, f0, tau, a, click ? new[] { (1.0, 1.0), (2.756, 0.3) } : new[] { (1.0, 1.0), (2.0, 0.22) }, num("attackMs", click ? 0.3 : 0.6), num("glide", click ? 1.0 : 0.85));
                }
                double grit = num("grit", click ? 0.15 : 0.35);
                if (grit > 0) AddNoiseBand(y, Shape(Env(Band(x, 1000, 6000), 0.5, 4), 0.7), num("gritHz", 240), 1.2, grit, rand);
                break;
            }
            case "rattle":
            {
                var ons = Onsets(x, num("gapMs", 10), num("threshold", 0.12), (int)num("maxHits", 48));
                if (ons.Count == 0) break;
                double smax = ons.Max(o => o.s), f0 = num("carrierHz", 220);
                int idx = 0;
                foreach (var (at, s) in ons)
                {
                    double var = 1 + 0.08 * (((idx++ * 37) % 7) - 3) / 3.0;
                    Modal(y, at, f0 * var, num("tauMs", 4), Math.Pow(s / smax, num("dynamics", 0.7)), new[] { (1.0, 1.0), (2.756, 0.25) }, 0.3, 1);
                }
                break;
            }
            case "scrape":
            {
                var grain = Shape(Env(Band(x, num("bandLo", 800), num("bandHi", 6000)), 1, 6), num("compress", 0.6));
                AddNoiseBand(y, grain, num("carrierHz", 180), 1.4, 1.0, rand);
                var ons = Onsets(Band(x, 800, 6000), num("gapMs", 9), num("threshold", 0.3), (int)num("maxHits", 64));
                if (ons.Count > 0) { double smax = ons.Max(o => o.s); foreach (var (at, s) in ons) Modal(y, at, num("carrierHz", 180) * 1.4, 2.5, num("stick", 0.7) * s / smax, new[] { (1.0, 1.0) }, 0.2, 1); }
                break;
            }
            case "friction":
            {
                var env = Shape(Env(Band(x, 150, 1500), 3, num("releaseMs", 15)), num("compress", 0.7));
                AddNoiseBand(y, env, num("carrierHz", 130), 1.0, 1.0, rand);
                AddTone(y, env, num("carrierHz", 130) * 0.6, num("tone", 0.35));
                break;
            }
            case "texture":
            {
                var env = Shape(Env(x, 5, num("releaseMs", 30)), num("compress", 0.8));
                var flux = Shape(Env(Band(x, 1000, 0), 1, 8), 1.0);
                var am = new float[env.Length];
                for (int i = 0; i < am.Length; i++) am[i] = (float)(env[i] * (1 - num("am", 0.4) + num("am", 0.4) * flux[i]));
                AddNoiseBand(y, am, num("carrierHz", 100), 0.8, 1.0, rand);
                break;
            }
            default: throw new FormatException($"audio method '{method}' (impact | click | rattle | scrape | friction | texture)");
        }
        return y;
    }

    /// <summary>Normalised envelope ^ exponent (exponent &lt; 1 compresses: quiet grain stays above the ~10 % Bluetooth floor).</summary>
    static float[] Shape(float[] env, double exp)
    {
        double pk = Math.Max(1e-6, env.Max());
        var y = new float[env.Length];
        for (int i = 0; i < env.Length; i++) y[i] = (float)Math.Pow(env[i] / pk, exp);
        return y;
    }

    static void Modal(float[] y, int at, double f, double tauMs, double a, (double ratio, double g)[] modes, double attMs, double glide)
    {
        int len = (int)(Rate * Math.Min(0.4, tauMs * 6 / 1000));
        double tau = tauMs / 1000, att = Math.Max(0.05, attMs) / 1000, k = Math.Log(Math.Max(0.05, glide)) / Math.Max(1, len);
        var ph = new double[modes.Length];
        for (int i = 0; i < len && at + i < y.Length; i++)
        {
            double t = (double)i / Rate, fi = f * Math.Exp(k * i), v = 0;
            for (int j = 0; j < modes.Length; j++) { ph[j] += 2 * Math.PI * fi * modes[j].ratio / Rate; v += modes[j].g * Math.Exp(-t / (j == 0 ? tau : tau * 0.5)) * Math.Sin(ph[j]); }
            double fade = Math.Min(1, (len - i) / (0.002 * Rate));
            y[at + i] += (float)(a * Math.Min(1, t / att) * fade * v);
        }
    }

    static void AddNoiseBand(float[] y, float[] env, double hz, double q, double w, Func<double> rand)
    {
        var b1 = new Dsp.Biquad().SetBandpass(hz, q, Rate); var b2 = new Dsp.Biquad().SetBandpass(hz, q, Rate);
        for (int i = 0; i < Math.Min(y.Length, env.Length); i++) y[i] += (float)(w * env[i] * b2.Process(b1.Process(rand() * 2 - 1)) * 3.5);
    }

    static void AddTone(float[] y, float[] env, double hz, double w)
    {
        double ph = 0;
        for (int i = 0; i < Math.Min(y.Length, env.Length); i++) { ph += 2 * Math.PI * hz / Rate; y[i] += (float)(w * env[i] * Math.Sin(ph)); }
    }

    // ------------------------------------------------------------------ command: sound-scan

    /// <summary>Analyses every WAV of a folder (or the given files) and writes the features + suggested method as JSON.</summary>
    public static string Scan(IEnumerable<string> files, string outJson)
    {
        var list = new List<Features>();
        foreach (var f in files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            try { list.Add(Analyze(f)); } catch (Exception ex) { Console.Error.WriteLine($"{Path.GetFileName(f)}: {ex.Message}"); }
        var inv = CultureInfo.InvariantCulture;
        var rows = list.Select(f => new Dictionary<string, object>
        {
            ["name"] = f.Name, ["ms"] = Math.Round(f.DurMs), ["activeMs"] = Math.Round(f.ActiveMs), ["onsets"] = f.Onsets, ["onsetRate"] = Math.Round(f.OnsetRate, 1),
            ["centroid"] = Math.Round(f.Centroid), ["low"] = Math.Round(f.Low, 3), ["mid"] = Math.Round(f.Mid, 3), ["high"] = Math.Round(f.High, 3),
            ["sustain"] = Math.Round(f.Sustain, 2), ["decayMs"] = Math.Round(f.DecayMs), ["crest"] = Math.Round(f.Crest, 1), ["channels"] = f.Channels, ["method"] = f.Method
        });
        File.WriteAllText(outJson, JsonSerializer.Serialize(new { generated = DateTime.Now.ToString("yyyy-MM-dd HH:mm", inv), count = list.Count, sounds = rows }, new JsonSerializerOptions { WriteIndented = true }));
        return $"{list.Count} sound(s) analysed -> {outJson} (" + string.Join(", ", list.GroupBy(x => x.Method).Select(g => $"{g.Key} {g.Count()}")) + ")";
    }
}
