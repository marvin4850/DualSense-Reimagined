// ---------------------------------------------------------------------------------------------------------------------
// Haptic DSP building blocks, ported to C# from DualSense Haptic Designer
//   https://github.com/Jellybit/DualSense-Haptic-Designer  (commit e4653e82cd7320263b88eaaaf3a9a8036524c6a1)
//   files: src/renderer/src/haptics/dsp.ts, haptics/eq.ts, export/wav.ts, designer/clips/model.ts
//
// MIT License
//
// Copyright (c) 2026 Jellybit
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.
//
// Changes in this port (G1R-DualSense, 2026-11-14): TypeScript -> C#; offline only (no AudioWorklet / WebAudio graph);
// the WebAudio EQ graph (HPF -> 10 peaking -> LPF -> gain) is re-implemented with RBJ biquads (HPF/LPF Q is linear
// 0.707 here, WebAudio reads it in dB); added a peaking filter and a 1-pole DC blocker; 32-bit float WAV writer.
// Our own additions (not from the Haptic Designer) are marked "G1R:".
// ---------------------------------------------------------------------------------------------------------------------
using System.Text;

namespace DsBridge.Haptics;

/// <summary>Haptic DSP blocks (port of Haptic Designer dsp.ts / eq.ts / wav.ts / clips/model.ts).
/// DualSense voice-coil actuators respond roughly 20 Hz .. 1 kHz, strongest around 80-300 Hz. Short enveloped sine
/// "grains" in that band feel like crisp taps and impacts; band-limited noise feels like rolling / texture.</summary>
public static class Dsp
{
    public const int SampleRate = 48000;

    // ---------------- materials / grains (dsp.ts) ----------------

    public sealed record Grain(double Freq, double Duration, double Decay, double Amp, double Pan,
                               double? FreqEnd = null, double? Attack = null, double Noise = 0, double Partial = 0, double Delay = 0);

    public sealed record MaterialDef(double Freq, double FreqEnd, double Decay, double Noise, double Partial, double Duration);

    public static readonly Dictionary<string, MaterialDef> Materials = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wood"] = new(190, 150, 0.018, 0.35, 0, 0.07),
        ["glass"] = new(340, 330, 0.04, 0.15, 2.4, 0.14),
        ["metal"] = new(260, 255, 0.09, 0.1, 2.76, 0.3),
        ["rubber"] = new(85, 60, 0.03, 0.05, 0, 0.09),
        ["stone"] = new(150, 110, 0.012, 0.5, 0, 0.05),
        ["soft"] = new(60, 45, 0.05, 0.0, 0, 0.12),
    };

    public static Grain ImpactGrain(string material, double amp, double pan, double pitchScale = 1)
    {
        var m = Materials.TryGetValue(material, out var x) ? x : Materials["metal"];
        return new Grain(m.Freq * pitchScale, m.Duration, m.Decay, amp, pan, m.FreqEnd * pitchScale, 0.0015, m.Noise, m.Partial);
    }
    /// <summary>Short crisp tap.</summary>
    public static Grain TapGrain(double amp, double pan, double freq = 230) => new(freq, 0.03, 0.007, amp, pan, freq * 0.8, 0.001, 0.15);
    /// <summary>Low thump with pitch drop.</summary>
    public static Grain ThumpGrain(double amp, double pan, double freq = 110) => new(freq, 0.12, 0.035, amp, pan, freq * 0.45, 0.003);

    /// <summary>Equal-power pan: -1 = left grip only, +1 = right grip only, 0 = 0.707 each.</summary>
    public static (double l, double r) PanGains(double pan)
    {
        double p = Math.Clamp(pan, -1, 1), a = (p + 1) * Math.PI / 4;
        return (Math.Cos(a), Math.Sin(a));
    }

    /// <summary>Small deterministic PRNG (mulberry32) so offline renders are reproducible.</summary>
    public static Func<double> Rng(uint seed)
    {
        uint a = seed;
        return () =>
        {
            unchecked
            {
                a += 0x6D2B79F5;
                uint t = a;
                t = (t ^ (t >> 15)) * (t | 1);
                t ^= t + (t ^ (t >> 7)) * (t | 61);
                return (t ^ (t >> 14)) / 4294967296.0;
            }
        };
    }

    /// <summary>Soft limiter so stacked grains never hard-clip the actuators.</summary>
    public static double SoftClip(double x) => Math.Tanh(x);

    /// <summary>RBJ biquad (band-pass constant 0 dB peak, low-pass, high-pass; G1R: peaking).</summary>
    public sealed class Biquad
    {
        double b0, b1, b2, a1, a2, x1, x2, y1, y2;
        static double W(double f, int sr) => 2 * Math.PI * Math.Max(10, Math.Min(f, sr * 0.45)) / sr;

        public Biquad SetBandpass(double freq, double q, int sr = SampleRate)
        {
            double w = W(freq, sr), alpha = Math.Sin(w) / (2 * Math.Max(0.1, q)), a0 = 1 + alpha;
            b0 = alpha / a0; b1 = 0; b2 = -alpha / a0; a1 = -2 * Math.Cos(w) / a0; a2 = (1 - alpha) / a0;
            return this;
        }
        public Biquad SetLowpass(double freq, double q = 0.707, int sr = SampleRate)
        {
            double w = W(freq, sr), alpha = Math.Sin(w) / (2 * q), cw = Math.Cos(w), a0 = 1 + alpha;
            b0 = (1 - cw) / 2 / a0; b1 = (1 - cw) / a0; b2 = b0; a1 = -2 * cw / a0; a2 = (1 - alpha) / a0;
            return this;
        }
        public Biquad SetHighpass(double freq, double q = 0.707, int sr = SampleRate)
        {
            double w = W(freq, sr), alpha = Math.Sin(w) / (2 * q), cw = Math.Cos(w), a0 = 1 + alpha;
            b0 = (1 + cw) / 2 / a0; b1 = -(1 + cw) / a0; b2 = b0; a1 = -2 * cw / a0; a2 = (1 - alpha) / a0;
            return this;
        }
        /// <summary>G1R: RBJ peaking EQ (the WebAudio "peaking" type used by the Haptic Designer's graphic EQ).</summary>
        public Biquad SetPeaking(double freq, double q, double gainDb, int sr = SampleRate)
        {
            double A = Math.Pow(10, gainDb / 40), w = W(freq, sr), alpha = Math.Sin(w) / (2 * Math.Max(0.05, q)), cw = Math.Cos(w), a0 = 1 + alpha / A;
            b0 = (1 + alpha * A) / a0; b1 = -2 * cw / a0; b2 = (1 - alpha * A) / a0; a1 = -2 * cw / a0; a2 = (1 - alpha / A) / a0;
            return this;
        }
        public double Process(double x)
        {
            double y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1; x1 = x; y2 = y1; y1 = y;
            return y;
        }
        public void Reset() { x1 = x2 = y1 = y2 = 0; }
        /// <summary>G1R: magnitude response (linear) at a frequency, for tests and the EQ report.</summary>
        public double Magnitude(double f, int sr = SampleRate)
        {
            double w = 2 * Math.PI * f / sr;
            System.Numerics.Complex z1 = System.Numerics.Complex.FromPolarCoordinates(1, -w), z2 = z1 * z1;
            return ((b0 + b1 * z1 + b2 * z2) / (1 + a1 * z1 + a2 * z2)).Magnitude;
        }
    }

    /// <summary>One grain rendered into L/R (port of GrainVoice): exponential glide freq -> freqEnd, linear attack,
    /// exponential decay, optional second partial (ratio, 0.25 gain, decays 1.6x faster), optional low-passed noise transient
    /// (decays 3x faster than the body), last 2 ms faded.</summary>
    public static void RenderGrain(Grain g, Func<double> rand, float[] L, float[] R, int offset, int sr = SampleRate)
    {
        int delay = (int)Math.Round(g.Delay * sr), total = Math.Max(1, (int)Math.Round(g.Duration * sr));
        var (gl, gr) = PanGains(g.Pan);
        double end = g.FreqEnd ?? g.Freq, k = Math.Log(Math.Max(1, end) / Math.Max(1, g.Freq)) / total;
        var noiseLp = new Biquad().SetLowpass(Math.Max(200, g.Freq * 3), 0.7, sr);
        double attack = Math.Max(1, (g.Attack ?? 0.002) * sr), decayK = 1 / Math.Max(1e-4, g.Decay * sr), ph = 0, ph2 = 0;
        for (int t = 0; t < total; t++)
        {
            int o = offset + delay + t;
            double f = g.Freq * Math.Exp(k * t);
            ph += 2 * Math.PI * f / sr;
            double s = Math.Sin(ph);
            if (g.Partial > 0)
            {
                ph2 += 2 * Math.PI * f * g.Partial / sr;
                s = s * 0.75 + Math.Sin(ph2) * 0.25 * Math.Exp(-t * decayK * 1.6);
            }
            if (g.Noise > 0)
            {
                double nz = noiseLp.Process(rand() * 2 - 1) * Math.Exp(-t * decayK * 3);
                s = s * (1 - g.Noise) + nz * g.Noise * 2;
            }
            double env = Math.Min(1, t / attack) * Math.Exp(-t * decayK), tail = Math.Min(1, (total - t) / (0.002 * sr));
            double v = s * env * tail * g.Amp;
            if (o < 0 || o >= L.Length) continue;
            L[o] += (float)(v * gl); R[o] += (float)(v * gr);
        }
    }

    // ---------------- clips (designer/clips/model.ts) ----------------

    /// <summary>Clip sources: "tone" (sine|square|triangle|saw with exponential glide + AM), "noise" (two cascaded band-passes,
    /// centre gliding freqStart -> freqEnd, + AM), "pulses" (a train of grains; rate glides, jitter, pan spread, doublets).</summary>
    public sealed class Clip
    {
        public string Kind = "tone";                       // tone | noise | pulses
        public double Start, Duration = 0.5, Gain = 1, Attack, Release, ReleaseCurve = 1, PanStart, PanEnd;
        public string Wave = "sine";                       // tone
        public double FreqStart = 150, FreqEnd = 150, Q = 2, AmRate, AmDepth;   // tone / noise
        public string GrainKind = "tap";                   // pulses: tap | thump | impact | custom
        public string Material = "metal";
        public double RateStart = 10, RateEnd = 10, Jitter, Spread, Pitch = 1;
        public bool Doublet;
        public Grain? Custom;                              // G1R: pulses with an explicit grain (amp / pan are set per pulse)
        public double AmpJitter = 0.15;                    // G1R: the original fixes 0.85 + 0.15 x rand
        public double AccentEvery, AccentGain = 1;         // G1R: every N-th pulse x AccentGain (stick-slip catch)
    }

    static double Wave(string kind, double ph)
    {
        double x = ph / (2 * Math.PI), f = x - Math.Floor(x);
        return kind switch { "square" => f < 0.5 ? 0.7 : -0.7, "triangle" => 1 - 4 * Math.Abs(f - 0.5), "saw" => (2 * f - 1) * 0.8, _ => Math.Sin(ph) };
    }

    /// <summary>Renders one clip into L/R (mixing). Port of renderClip.</summary>
    public static void RenderClip(Clip c, float[] L, float[] R, int sr = SampleRate, uint seed = 1)
    {
        int start = (int)Math.Round(c.Start * sr), n = (int)Math.Round(c.Duration * sr);
        var rand = Rng(seed);
        double Env(int i)
        {
            double t = (double)i / sr;
            double a = c.Attack > 0 ? Math.Min(1, t / c.Attack) : 1;
            double r = c.Release > 0 ? Math.Pow(Math.Max(0, Math.Min(1, (c.Duration - t) / c.Release)), c.ReleaseCurve) : 1;
            double edge = Math.Min(1, Math.Min(i / (0.0015 * sr), (n - i) / (0.0015 * sr)));   // 1.5 ms anti-click fades
            return Math.Max(0, a * r * edge) * c.Gain;
        }
        double PanAt(int i) => c.PanStart + (c.PanEnd - c.PanStart) * i / Math.Max(1, n);

        if (c.Kind is "tone" or "noise")
        {
            double ph = 0, am = 0;
            var bp1 = new Biquad(); var bp2 = new Biquad();
            for (int i = 0; i < n; i++)
            {
                int o = start + i;
                if (o < 0 || o >= L.Length) continue;
                double t = (double)i / Math.Max(1, n), f = c.FreqStart * Math.Pow(c.FreqEnd / Math.Max(1, c.FreqStart), t), v;
                if (c.Kind == "tone") { ph += 2 * Math.PI * f / sr; v = Wave(c.Wave, ph); }
                else
                {
                    if ((i & 63) == 0) { bp1.SetBandpass(f, c.Q, sr); bp2.SetBandpass(f, c.Q, sr); }
                    v = bp2.Process(bp1.Process(rand() * 2 - 1)) * 3.5;
                }
                if (c.AmRate > 0 && c.AmDepth > 0) { am += 2 * Math.PI * c.AmRate / sr; v *= 1 - c.AmDepth * (0.5 + 0.5 * Math.Sin(am)); }
                var (gl, gr) = PanGains(PanAt(i));
                double e = Env(i);
                L[o] += (float)(v * e * gl); R[o] += (float)(v * e * gr);
            }
            return;
        }

        // pulses: integrate the (possibly gliding) rate to place pulses
        var grains = new List<(int at, Grain g)>();
        double phase = 0.999, tt = 0, dt = 1.0 / sr; int idx = 0, count = 0;
        for (int i = 0; i < n;)
        {
            double rate = c.RateStart + (c.RateEnd - c.RateStart) * i / Math.Max(1, n);
            phase += rate * dt * 32; i += 32; tt += 32 * dt;
            if (phase < 1) continue;
            phase -= 1;
            double jitter = (rand() - 0.5) * c.Jitter * (1 / Math.Max(0.1, rate));
            int at = Math.Max(0, (int)Math.Round((tt + jitter) * sr));
            if (at >= n) continue;
            double pan = Math.Clamp(PanAt(at) + (rand() * 2 - 1) * c.Spread, -1, 1);
            double amp = 1 - c.AmpJitter + rand() * c.AmpJitter;
            if (c.AccentEvery > 0 && (++count % (int)Math.Max(1, c.AccentEvery)) == 0) amp *= c.AccentGain;
            Grain Mk(double a) => c.GrainKind switch
            {
                "tap" => TapGrain(a, pan, 230 * c.Pitch),
                "thump" => ThumpGrain(a, pan, 110 * c.Pitch),
                "custom" when c.Custom != null => c.Custom with { Amp = c.Custom.Amp * a, Pan = pan, Freq = c.Custom.Freq * c.Pitch, FreqEnd = (c.Custom.FreqEnd ?? c.Custom.Freq) * c.Pitch },
                _ => ImpactGrain(c.Material, a, pan, c.Pitch)
            };
            grains.Add((at, Mk(amp)));
            if (c.Doublet && at + (int)Math.Round(0.18 * sr) < n) grains.Add((at + (int)Math.Round(0.18 * sr), Mk(amp * 0.7)));
            idx++;
        }
        var tmpL = new float[n + sr]; var tmpR = new float[n + sr];   // allow tails
        foreach (var (at, g) in grains) RenderGrain(g, rand, tmpL, tmpR, at, sr);
        for (int k = 0; k < tmpL.Length; k++)
        {
            int o = start + k;
            if (o < 0 || o >= L.Length) continue;
            double e = k < n ? Env(k) : 0;
            if (e == 0 && k < n) continue;
            double g = k < n ? e : c.Gain;   // grain tails past the clip end ring out naturally
            L[o] += (float)(tmpL[k] * g); R[o] += (float)(tmpR[k] * g);
        }
    }

    // ---------------- per-actuator EQ (eq.ts) ----------------

    public static readonly double[] EqBands = { 31, 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };

    /// <summary>EQ settings of one actuator: high-pass (low cut), 10-band graphic EQ (peaking, Q 1.41), low-pass (high cut), gain.</summary>
    public sealed record EqSettings(double Hpf = 0, double Lpf = 0, double GainDb = 0, double[]? Bands = null, bool Mute = false);

    /// <summary>Shapes from the Haptic Designer (EQ_PRESETS).</summary>
    public static readonly Dictionary<string, EqSettings> EqPresets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["flat"] = new(),
        ["low-boom"] = new(20, 150, 3, new double[] { 6, 6, 0, -12, -24, -24, -24, -24, -24, -24 }),
        ["bass"] = new(25, 250, 0, new double[] { 6, 6, 3, 0, -6, -12, -24, -24, -24, -24 }),
        ["punchy"] = new(40, 500, 3, new double[] { 0, 6, 6, 3, 0, -6, -18, -24, -24, -24 }),
        ["mid-crackle"] = new(300, 1000, 3, new double[] { -24, -24, -12, 0, 6, 6, -6, -18, -24, -24 }),
        ["texture"] = new(50, 1000, 0, new double[] { -6, 0, 3, 6, 6, 3, -6, -18, -24, -24 }),
        ["high-ticks"] = new(150, 1000, 3, new double[] { -24, -24, -6, 3, 6, 3, -6, -18, -24, -24 }),
    };

    /// <summary>Mono EQ chain: input -> HPF -> 10 peaking -> LPF -> gain (offline version of EqChain).</summary>
    public static float[] ApplyEq(float[] x, EqSettings s, int sr = SampleRate)
    {
        var chain = BuildEq(s, sr);
        double g = s.Mute ? 0 : Math.Pow(10, s.GainDb / 20);
        var y = new float[x.Length];
        for (int i = 0; i < x.Length; i++)
        {
            double v = x[i];
            foreach (var f in chain) v = f.Process(v);
            y[i] = (float)(v * g);
        }
        return y;
    }

    public static List<Biquad> BuildEq(EqSettings s, int sr = SampleRate)
    {
        var chain = new List<Biquad>();
        if (s.Hpf > 0) chain.Add(new Biquad().SetHighpass(s.Hpf, 0.707, sr));
        if (s.Bands != null) for (int i = 0; i < Math.Min(EqBands.Length, s.Bands.Length); i++)
            if (s.Bands[i] != 0 && EqBands[i] < sr * 0.45) chain.Add(new Biquad().SetPeaking(EqBands[i], 1.41, s.Bands[i], sr));
        if (s.Lpf > 0) chain.Add(new Biquad().SetLowpass(Math.Min(s.Lpf, sr * 0.45), 0.707, sr));
        return chain;
    }

    /// <summary>G1R: 1-pole DC blocker (y = x - x1 + r*y1), r for a ~5 Hz corner.</summary>
    public static void DcBlock(float[] x, int sr = SampleRate)
    {
        double r = 1 - 2 * Math.PI * 5 / sr, x1 = 0, y1 = 0;
        for (int i = 0; i < x.Length; i++) { double y = x[i] - x1 + r * y1; x1 = x[i]; y1 = y; x[i] = (float)y; }
    }

    // ---------------- WAV (wav.ts) ----------------

    /// <summary>Haptic WAV: "stereo" = ch1 left actuator, ch2 right actuator (Sony tooling / engines); "quad" = the USB device
    /// layout, ch1/ch2 speaker (always silent here - the speaker is never used), ch3/ch4 actuators.
    /// bits: 16 (PCM, like the Haptic Designer) or 32 (G1R: IEEE float, lossless for the bank).</summary>
    public static byte[] EncodeWav(float[] L, float[] R, int sampleRate, bool quad = false, int bits = 16)
    {
        int ch = quad ? 4 : 2, frames = Math.Max(L.Length, R.Length), bps = bits / 8, dataBytes = frames * ch * bps;
        using var ms = new MemoryStream(44 + dataBytes);
        using var w = new BinaryWriter(ms, Encoding.ASCII);
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + dataBytes); w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)(bits == 32 ? 3 : 1)); w.Write((short)ch);
        w.Write(sampleRate); w.Write(sampleRate * ch * bps); w.Write((short)(ch * bps)); w.Write((short)bits);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(dataBytes);
        void S(float v) { if (bits == 32) w.Write(v); else w.Write((short)Math.Clamp(Math.Round(v * 32767.0), -32768, 32767)); }
        for (int i = 0; i < frames; i++)
        {
            if (quad) { S(0); S(0); }
            S(i < L.Length ? L[i] : 0); S(i < R.Length ? R[i] : 0);
        }
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Decodes PCM WAV (8/16/24/32-bit int or 32-bit float, WAVE_FORMAT_EXTENSIBLE too): (rate, channels).</summary>
    public static (int rate, float[][] ch) DecodeWav(byte[] b)
    {
        string Tag(int o) => Encoding.ASCII.GetString(b, o, 4);
        if (b.Length < 12 || Tag(0) != "RIFF" || Tag(8) != "WAVE") throw new FormatException("not a WAV file");
        int o = 12, format = 0, chans = 0, rate = 0, bits = 0; bool haveFmt = false;
        while (o + 8 <= b.Length)
        {
            string id = Tag(o); int size = BitConverter.ToInt32(b, o + 4), body = o + 8;
            if (id == "fmt ")
            {
                format = BitConverter.ToUInt16(b, body);
                if (format == 0xFFFE && size >= 26) format = BitConverter.ToUInt16(b, body + 24);
                chans = BitConverter.ToUInt16(b, body + 2); rate = BitConverter.ToInt32(b, body + 4); bits = BitConverter.ToUInt16(b, body + 14); haveFmt = true;
            }
            else if (id == "data")
            {
                if (!haveFmt) throw new FormatException("WAV data before fmt chunk");
                int bps = bits / 8, frames = Math.Min(size, b.Length - body) / (bps * chans);
                var res = Enumerable.Range(0, chans).Select(_ => new float[frames]).ToArray();
                int p = body;
                for (int i = 0; i < frames; i++)
                    for (int c = 0; c < chans; c++, p += bps)
                        res[c][i] = format == 3 && bits == 32 ? BitConverter.ToSingle(b, p)
                                  : bits == 8 ? (b[p] - 128) / 128f
                                  : bits == 16 ? BitConverter.ToInt16(b, p) / 32768f
                                  : bits == 24 ? ((b[p] | (b[p + 1] << 8) | ((sbyte)b[p + 2] << 16)) / 8388608f)
                                  : bits == 32 ? BitConverter.ToInt32(b, p) / 2147483648f
                                  : throw new FormatException($"unsupported WAV bit depth {bits}");
                return (rate, res);
            }
            o = body + size + (size & 1);
        }
        throw new FormatException("WAV has no data chunk");
    }

    /// <summary>Linear-interpolation resample. Good enough for haptic-band content.</summary>
    public static float[] Resample(float[] x, double from, double to)
    {
        if (Math.Abs(from - to) < 1e-9) return x;
        int n = (int)Math.Round(x.Length * to / from);
        var y = new float[n]; double r = from / to;
        for (int i = 0; i < n; i++)
        {
            double p = i * r; int i0 = (int)Math.Floor(p); double f = p - i0;
            float a = i0 < x.Length ? x[i0] : 0, b = i0 + 1 < x.Length ? x[i0 + 1] : a;
            y[i] = (float)(a * (1 - f) + b * f);
        }
        return y;
    }

    /// <summary>Picks the actuator channels of a decoded file: 4 ch -> ch3/ch4, 2 ch -> ch1/ch2, 1 ch -> both.</summary>
    public static (float[] L, float[] R) HapticChannels(float[][] c) =>
        c.Length >= 4 ? (c[2], c[3]) : c.Length >= 2 ? (c[0], c[1]) : (c[0], c[0]);
}
