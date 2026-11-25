using System.Globalization;
using System.Numerics;

namespace DsBridge.Haptics;

/// <summary>Objective measurements of a stereo actuator signal (left, right) for the forge checks and rules-check:
/// clipping, limiter contact, DC, edges, attack, energy (rms20 = max RMS over 20 ms - what the voice coil really "feels",
/// LOCKPICK.md §11), spectral balance in actuator bands and the left / right balance.</summary>
public sealed class HapticMetrics
{
    public double LengthMs, ActiveMs, PeakL, PeakR, Rms20, Crest, DcL, DcR, EdgeStart, EdgeEnd, AttackMs;
    public long Clipped, OverKnee;
    public double Sub, Low, Core, Upper, High;          // energy shares: <40, 40-80, 80-400, 400-700, >700 Hz
    public double CentroidHz, DominantHz, BalanceDb, Correlation;
    public double Peak => Math.Max(PeakL, PeakR);
    public double[] Spectrum = Array.Empty<double>();   // energy per 10 Hz bin 0..1200 Hz (for distances between variants)

    static string F(double v, string f = "0.00") => v.ToString(f, CultureInfo.InvariantCulture);
    public string Short() =>
        $"{F(ActiveMs, "0")} ms pk {F(Peak)} rms20 {F(Rms20)} crest {F(Crest, "0.0")} atk {F(AttackMs, "0.0")} ms " +
        $"40-400 {F((Core + Low) * 100, "0")}% sub {F(Sub * 100, "0")}% 400-700 {F(Upper * 100, "0")}% >700 {F(High * 100, "0.0")}% cen {F(CentroidHz, "0")} Hz bal {Signed(BalanceDb)} dB";
    public static string Signed(double v) { double r = Math.Round(v, 1); if (r == 0) r = 0; return (r >= 0 ? "+" : "") + r.ToString("0.0", CultureInfo.InvariantCulture); }
}

public static class Analysis
{
    public const double LimiterKnee = 0.855;   // mixer: transparent up to knee x ceiling (0.9 x 0.95)

    public static HapticMetrics Measure(float[] l, float[] r, int rate = 48000)
    {
        var m = new HapticMetrics();
        int n = Math.Max(l.Length, r.Length);
        if (n == 0) return m;
        float A(float[] x, int i) => i < x.Length ? x[i] : 0f;
        m.LengthMs = 1000.0 * n / rate;
        double el = 0, er = 0, elr = 0, sl = 0, sr = 0;
        for (int i = 0; i < n; i++)
        {
            float a = A(l, i), b = A(r, i);
            m.PeakL = Math.Max(m.PeakL, Math.Abs(a)); m.PeakR = Math.Max(m.PeakR, Math.Abs(b));
            if (Math.Abs(a) >= 0.999f || Math.Abs(b) >= 0.999f) m.Clipped++;
            if (Math.Abs(a) > LimiterKnee || Math.Abs(b) > LimiterKnee) m.OverKnee++;
            el += a * a; er += b * b; elr += a * b; sl += a; sr += b;
        }
        m.DcL = sl / n; m.DcR = sr / n;
        m.BalanceDb = 10 * Math.Log10((el + 1e-12) / (er + 1e-12));
        m.Correlation = elr / Math.Sqrt((el + 1e-12) * (er + 1e-12));
        m.EdgeStart = Math.Max(Math.Abs(A(l, 0)), Math.Abs(A(r, 0)));
        m.EdgeEnd = Math.Max(Math.Abs(A(l, n - 1)), Math.Abs(A(r, n - 1)));

        // envelope (max |L|,|R|), active extent above -40 dB of the peak, attack 5 % -> 90 % of the smoothed envelope peak
        double peak = m.Peak, thr = peak * 0.01;
        int first = -1, last = -1;
        for (int i = 0; i < n; i++) { double v = Math.Max(Math.Abs(A(l, i)), Math.Abs(A(r, i))); if (v > thr) { if (first < 0) first = i; last = i; } }
        m.ActiveMs = first < 0 ? 0 : 1000.0 * (last - first + 1) / rate;
        var env = new double[n]; double e = 0, rel = Math.Exp(-1.0 / (rate * 0.002));
        for (int i = 0; i < n; i++) { double v = Math.Max(Math.Abs(A(l, i)), Math.Abs(A(r, i))); e = v > e ? v : e * rel; env[i] = e; }
        double emax = env.Max(); int i5 = Array.FindIndex(env, x => x >= emax * 0.05), i90 = Array.FindIndex(env, x => x >= emax * 0.9);
        m.AttackMs = i5 < 0 || i90 < 0 ? 0 : 1000.0 * (i90 - i5) / rate;

        m.Rms20 = Math.Max(Rms20(l, rate), Rms20(r, rate));
        double act = first < 0 ? 0 : Math.Sqrt(Enumerable.Range(first, last - first + 1).Sum(i => (double)A(l, i) * A(l, i) + (double)A(r, i) * A(r, i)) / (2.0 * (last - first + 1)));
        m.Crest = act > 0 ? peak / act : 0;

        // spectrum: energy of L and R summed (each actuator separately, then added)
        int nfft = 1; while (nfft < n && nfft < (1 << 17)) nfft <<= 1;
        nfft = Math.Max(nfft, 4096);
        var pw = new double[nfft / 2];
        foreach (var x in new[] { l, r })
        {
            var buf = new Complex[nfft];
            for (int i = 0; i < Math.Min(x.Length, nfft); i++) buf[i] = x[i];
            Fft(buf);
            for (int k = 0; k < nfft / 2; k++) pw[k] += buf[k].Real * buf[k].Real + buf[k].Imaginary * buf[k].Imaginary;
        }
        double tot = 0, cen = 0, best = 0; double bw = (double)rate / nfft;
        double sub = 0, low = 0, core = 0, up = 0, hi = 0;
        m.Spectrum = new double[120];
        for (int k = 1; k < nfft / 2; k++)
        {
            double f = k * bw, p = pw[k];
            tot += p; cen += p * f;
            if (p > best) { best = p; m.DominantHz = f; }
            if (f < 40) sub += p; else if (f < 80) low += p; else if (f < 400) core += p; else if (f < 700) up += p; else hi += p;
            int bin = (int)(f / 10); if (bin < m.Spectrum.Length) m.Spectrum[bin] += p;
        }
        if (tot > 0) { m.Sub = sub / tot; m.Low = low / tot; m.Core = core / tot; m.Upper = up / tot; m.High = hi / tot; m.CentroidHz = cen / tot; }
        double st = m.Spectrum.Sum(); if (st > 0) for (int i = 0; i < m.Spectrum.Length; i++) m.Spectrum[i] /= st;
        return m;
    }

    /// <summary>Max RMS over a sliding 20 ms window (signals shorter than 20 ms: their energy spread over 20 ms).</summary>
    public static double Rms20(float[] x, int rate = 48000)
    {
        int w = rate / 50; double best = 0, acc = 0;
        if (x.Length == 0) return 0;
        if (x.Length < w) return Math.Sqrt(x.Sum(v => (double)v * v) / w);
        for (int i = 0; i < x.Length; i++) { acc += (double)x[i] * x[i]; if (i >= w) acc -= (double)x[i - w] * x[i - w]; if (i >= w - 1) best = Math.Max(best, Math.Sqrt(Math.Max(0, acc) / w)); }
        return best;
    }

    /// <summary>Bluetooth trip of the HD path (BtHapticsReport.From48k): 16-sample triangular anti-alias, 3 kHz, tanh gain 2.0,
    /// 8 bit, back to 48 kHz by linear interpolation. Returns the correlation with the original low-passed at 650 Hz (the BT band)
    /// and the rms20 of the BT version (what the pad plays, at wire level).</summary>
    public static (double corr, double rms20) BtSim(float[] l, float[] r, int rate = 48000, double gain = 2.0)
    {
        double corrSum = 0, rmsMax = 0; int used = 0;
        foreach (var x in new[] { l, r })
        {
            if (x.Length < 64 || x.Max(Math.Abs) < 1e-4) continue;
            int n = x.Length / 16; var q = new double[n];
            for (int k = 0; k < n; k++)
            {
                double s = 0; int c = k * 16;
                for (int j = -15; j <= 15; j++) { int i = c + j; if (i >= 0 && i < x.Length) s += (16 - Math.Abs(j)) * x[i]; }
                s /= 256;
                double y = Math.Tanh(gain * s) / Math.Tanh(gain);
                q[k] = Math.Clamp(Math.Round(y * 127), -127, 127) / 127.0;
            }
            var up = new float[x.Length];
            for (int i = 0; i < up.Length; i++) { double p = i / 16.0; int k0 = (int)p; double f = p - k0; double a = k0 < n ? q[k0] : 0, b = k0 + 1 < n ? q[k0 + 1] : a; up[i] = (float)(a + (b - a) * f); }
            var lp1 = new Dsp.Biquad().SetLowpass(650, 0.707, rate); var lp2 = new Dsp.Biquad().SetLowpass(650, 0.707, rate);
            double sxy = 0, sxx = 0, syy = 0;
            for (int i = 0; i < x.Length; i++) { double o = lp2.Process(lp1.Process(x[i])); double u = up[i]; sxy += o * u; sxx += o * o; syy += u * u; }
            corrSum += sxx > 0 && syy > 0 ? sxy / Math.Sqrt(sxx * syy) : 0; used++;
            rmsMax = Math.Max(rmsMax, Rms20(up, rate));
        }
        return (used > 0 ? corrSum / used : 1, rmsMax);
    }

    /// <summary>Spectral distance between two signals (half the L1 distance of the normalised 0-1200 Hz spectra: 0 same .. 1 disjoint).</summary>
    public static double SpectralDistance(HapticMetrics a, HapticMetrics b) =>
        0.5 * a.Spectrum.Zip(b.Spectrum, (x, y) => Math.Abs(x - y)).Sum();

    /// <summary>In-place radix-2 FFT (length = power of two).</summary>
    public static void Fft(Complex[] a)
    {
        int n = a.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (a[i], a[j]) = (a[j], a[i]);
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len; var wl = new Complex(Math.Cos(ang), Math.Sin(ang));
            for (int i = 0; i < n; i += len)
            {
                Complex w = Complex.One;
                for (int k = 0; k < len / 2; k++)
                {
                    Complex u = a[i + k], v = a[i + k + len / 2] * w;
                    a[i + k] = u + v; a[i + k + len / 2] = u - v; w *= wl;
                }
            }
        }
    }
}
