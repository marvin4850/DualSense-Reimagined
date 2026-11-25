using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DsBridge;

/// <summary>Real DualSense HD haptics over USB: the pad is a 4-channel Windows audio device;
/// channels 1-2 = speaker/headset, channels 3-4 = left/right haptic actuators (voice coils).
/// Bluetooth exposes no audio device in Windows, so this works only with the cable.</summary>
public static class HdHaptics
{
    /// <summary>Per-channel peak levels Windows metered during the last Play() (0..1).</summary>
    public static float[] LastPeaks = Array.Empty<float>();

    /// <summary>How the last FindDevice chose the endpoint ("container" / "name" / "name, ambiguous: N pads").</summary>
    public static string LastMatch = "";

    /// <summary>The pad's audio endpoint. With the opened pad's HID path: the endpoint of the SAME physical device (Windows
    /// Container ID, universal plan 6: with two pads the first name match could be the other pad); without it, or when
    /// Windows gives no container, the first endpoint named DualSense / Wireless Controller (old behaviour).</summary>
    public static MMDevice? FindDevice(string? hidPath = null)
    {
        var en = new MMDeviceEnumerator();
        var all = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        if (hidPath != null && DeviceIds.ContainerOfHid(hidPath) is Guid cid)
        {
            var same = all.FirstOrDefault(d => DeviceIds.ContainerOf(d) == cid);
            if (same != null) { LastMatch = "container"; return same; }
        }
        var named = all.Where(d => d.FriendlyName.Contains("DualSense", StringComparison.OrdinalIgnoreCase)
                                || d.FriendlyName.Contains("Wireless Controller", StringComparison.OrdinalIgnoreCase)).ToList();
        LastMatch = named.Count > 1 ? $"name, ambiguous: {named.Count} pad audio devices" : "name";
        return named.FirstOrDefault();
    }

    /// <summary>Windows default playback devices (multimedia / communications) — game sound goes there, not to the pad,
    /// unless the pad itself is the default.</summary>
    public static string DefaultsInfo(MMDevice pad)
    {
        var en = new MMDeviceEnumerator();
        string One(Role role)
        {
            try { var d = en.GetDefaultAudioEndpoint(DataFlow.Render, role); return $"'{d.FriendlyName}'{(d.ID == pad.ID ? " <- THE CONTROLLER (game sound would play from it!)" : "")}"; }
            catch { return "none"; }
        }
        return $"default playback: multimedia={One(Role.Multimedia)}, communications={One(Role.Communications)}";
    }

    public static string Describe(MMDevice d)
    {
        var f = d.AudioClient.MixFormat;
        var vol = d.AudioEndpointVolume;
        return $"'{d.FriendlyName}' mix={f.SampleRate} Hz, {f.Channels} ch, {f.BitsPerSample} bit, {f.Encoding}; " +
               $"volume={vol.MasterVolumeLevelScalar * 100:F0}%{(vol.Mute ? " MUTED" : "")}";
    }

    /// <summary>Plays left/right actuator signals (mono each, -1..1) on channels 3/4; channels 1/2 stay silent.</summary>
    public static bool Exclusive;   // bypass the Windows mixer and audio enhancements (APOs)

    /// <summary>Called about every RearmMs while audio plays (re-arm the actuators against another app's HID reports).</summary>
    public static Action? Rearm; public static int RearmMs = 0; public static int RearmCount; public static double LastRearmEveryMs;
    [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

    public static void Play(MMDevice dev, float[] left, float[] right, int rate)
    {
        var mix = dev.AudioClient.MixFormat;
        int ch = mix.Channels;
        if (ch < 4) throw new InvalidOperationException($"device has {ch} channels; HD haptics needs 4 (USB connection)");
        if (mix.SampleRate != rate) throw new InvalidOperationException($"mix rate {mix.SampleRate} != generated {rate}");
        int n = Math.Max(left.Length, right.Length);
        var buf = new float[n * ch];
        for (int i = 0; i < n; i++)
        {
            buf[i * ch + 2] = i < left.Length ? left[i] : 0f;
            buf[i * ch + 3] = i < right.Length ? right[i] : 0f;
        }
        byte[] bytes;
        if (Exclusive)
        {
            // exclusive mode: the device's native 16-bit PCM, 4 ch, 48 kHz
            bytes = new byte[buf.Length * 2];
            for (int i = 0; i < buf.Length; i++) { short v = (short)Math.Round(Math.Clamp(buf[i], -1f, 1f) * 32767); bytes[2 * i] = (byte)v; bytes[2 * i + 1] = (byte)(v >> 8); }
        }
        else
        {
            bytes = new byte[buf.Length * 4];
            Buffer.BlockCopy(buf, 0, bytes, 0, bytes.Length);
        }
        // >2 channels need WAVEFORMATEXTENSIBLE with a channel mask: use the device's own mix format (float32, 4 ch)
        if (!Exclusive && mix.BitsPerSample != 32) throw new InvalidOperationException($"unexpected mix format {mix} (need 32-bit float)");
        WaveFormat fmt = Exclusive ? new WaveFormatExtensible(rate, 16, ch) : mix;
        using var src = new RawSourceWaveStream(new MemoryStream(bytes), fmt);
        using var outDev = new WasapiOut(dev, Exclusive ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared, true, 30);
        outDev.Init(src);
        bool fineTimer = RearmMs > 0 && RearmMs < 16;
        if (fineTimer) timeBeginPeriod(1);   // 1 ms sleep resolution while re-arming fast
        try
        {
            outDev.Play();
            var peak = new float[ch];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long nextRearm = 0, nextMeter = 0; RearmCount = 0;
            while (outDev.PlaybackState == PlaybackState.Playing)
            {
                long now = sw.ElapsedMilliseconds;
                if (Rearm != null && RearmMs > 0 && now >= nextRearm)
                {
                    try { Rearm(); RearmCount++; } catch { }
                    nextRearm = now + RearmMs;
                }
                if (now >= nextMeter)
                {
                    try { var pv = dev.AudioMeterInformation.PeakValues; for (int i = 0; i < Math.Min(ch, pv.Count); i++) peak[i] = Math.Max(peak[i], pv[i]); } catch { }
                    nextMeter = now + 20;
                }
                Thread.Sleep(RearmMs > 0 ? 1 : 20);
            }
            LastPeaks = peak;
            LastRearmEveryMs = RearmCount > 1 ? sw.ElapsedMilliseconds / (double)RearmCount : 0;
        }
        finally { if (fineTimer) timeEndPeriod(1); }
    }

    // ---------- test signals ----------

    static float[] Silence(int rate, double sec) => new float[(int)(rate * sec)];

    /// <summary>Part 1: a clean low "thud": 55 Hz sine with a fast decay.</summary>
    public static float[] Thud(int rate)
    {
        var s = new float[(int)(rate * 0.6)];
        for (int i = 0; i < s.Length; i++) { double t = (double)i / rate; s[i] = (float)(0.9 * Math.Exp(-t * 7) * Math.Sin(2 * Math.PI * 55 * t)); }
        return s;
    }

    /// <summary>Part 2: crisp ticks — five single 160 Hz cycles (impossible with classic rumble).</summary>
    public static float[] Ticks(int rate)
    {
        var s = new float[(int)(rate * 1.0)];
        for (int k = 0; k < 5; k++)
        {
            int start = (int)(rate * 0.18 * k), len = rate / 160;
            for (int i = 0; i < len && start + i < s.Length; i++) s[start + i] = (float)(0.9 * Math.Sin(2 * Math.PI * i / len));
        }
        return s;
    }

    /// <summary>Part 3: diving texture — filtered noise "water" swelling up, then rising bubble chirps.
    /// `lagSec` shifts the swell between the two actuators (left leads).</summary>
    public static float[] Dive(int rate, double lagSec, int seed, bool bubbles = true)
    {
        var rnd = new Random(seed);
        double dur = 1.6;
        var s = new float[(int)(rate * (dur + (bubbles ? 0.9 : 0.0)))];
        double lp = 0, lp2 = 0;
        for (int i = 0; i < (int)(rate * dur); i++)
        {
            double t = (double)i / rate - lagSec;
            double env = t <= 0 || t >= 1.3 ? 0 : Math.Pow(Math.Sin(Math.PI * t / 1.3), 1.5);
            double noise = rnd.NextDouble() * 2 - 1;
            lp += 0.02 * (noise - lp); lp2 += 0.02 * (lp - lp2);        // ~40-80 Hz rumble of water
            double body = Math.Sin(2 * Math.PI * 38 * t) * 0.35;         // slow pressure swell
            s[i] = (float)(env * (lp2 * 9 + body));
        }
        int bStart = (int)(rate * (dur - 0.1));
        foreach (var (at, f0) in bubbles ? new[] { (0.05, 180.0), (0.25, 230.0), (0.42, 200.0), (0.65, 260.0) } : Array.Empty<(double, double)>())
        {
            int st = bStart + (int)(rate * (at + lagSec * 0.5)), len = (int)(rate * 0.045);
            for (int i = 0; i < len && st + i < s.Length; i++)
            {
                double t = (double)i / rate, f = f0 + 900 * t;            // rising chirp = bubble
                s[st + i] += (float)(0.45 * Math.Sin(Math.PI * i / len) * Math.Sin(2 * Math.PI * f * t));
            }
        }
        for (int i = 0; i < s.Length; i++) s[i] = Math.Clamp(s[i], -0.95f, 0.95f);
        return s;
    }

    public static (float[] l, float[] r) Concat(int rate, params (float[] l, float[] r)[] parts)
    {
        var l = new List<float>(); var r = new List<float>();
        foreach (var p in parts)
        {
            int n = Math.Max(p.l.Length, p.r.Length);
            for (int i = 0; i < n; i++) { l.Add(i < p.l.Length ? p.l[i] : 0); r.Add(i < p.r.Length ? p.r[i] : 0); }
            l.AddRange(Silence(rate, 0.5)); r.AddRange(Silence(rate, 0.5));
        }
        return (l.ToArray(), r.ToArray());
    }
}
