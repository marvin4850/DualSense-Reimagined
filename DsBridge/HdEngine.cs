using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DsBridge;

/// <summary>Live HD-haptics output for the companion (USB only).
/// A persistent WASAPI stream to the pad's audio device (channels 3/4 = actuators, 1/2 = speaker kept silent);
/// effects are mixed in as "voices" with ~20 ms latency. While a voice plays, the actuators are re-armed every 10 ms
/// with the light HID report (the game's own HID reports would otherwise hand them back to rumble emulation, test 56-57).
/// When nothing plays, the pad is left alone (the game's own rumble works normally).</summary>
public sealed class HdEngine : IDisposable
{
    readonly MMDevice? _dev; readonly DualSense _pad; readonly Action<string> _log;
    readonly StreamProvider? _stream; WasapiOut? _out; Thread? _armThread; volatile bool _run;
    /// <summary>Bluetooth: the same mixer goes out as HID report 0x32 (3 kHz, BtHaptics.cs) instead of the USB audio device.</summary>
    public bool Bluetooth { get; }
    public long BtReports, BtWriteFails, BtPrimes;
    /// <summary>BT: re-send the prime (0x31: rumble emulation off, haptic LPF, no power reduction) at the start of every sound and
    /// this often while it plays - like ArmHapticsLight on USB. Game test 15.11.2026: with the prime only at start the pad stayed
    /// silent in the game (trigger / light 0x31 reports in between), while bt-hd-test (no other reports) worked. 0 = start only.</summary>
    public static int BtRearmMs = 40;
    /// <summary>BT level vs USB (user 15.11.2026: without the cable every effect is at least 2x weaker). Gain + soft knee
    /// (BtHapticsReport.Level). `run --bt-gain X`.</summary>
    public static double BtGain = 2.0;
    public int Rate { get; }
    public volatile bool Failed;
    public long ArmReports;
    /// <summary>The voice mixer (priorities, ducking, limiter, statistics) shared with offline replay.</summary>
    public Haptics.HapticMixer Mixer { get; } = new();
    /// <summary>Audio callback gaps longer than StallMs (stream starved / blocked).</summary>
    public long Stalls => _stream?.Stalls ?? 0;
    public int StallMs { get => _stream?.StallMs ?? 80; set { if (_stream != null) _stream.StallMs = value; } }

    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

    HdEngine(MMDevice dev, DualSense pad, Action<string> log)
    {
        _dev = dev; _pad = pad; _log = log;
        var fmt = dev.AudioClient.MixFormat;
        Rate = fmt.SampleRate;
        _stream = new StreamProvider(fmt, Mixer);
    }

    HdEngine(DualSense pad, Action<string> log)   // Bluetooth
    {
        _pad = pad; _log = log; Bluetooth = true; Rate = 48000;
    }

    /// <summary>Starts HD output if the pad is on USB and its audio device is usable; otherwise null (rumble fallback).</summary>
    public static HdEngine? TryStart(DualSense pad, Action<string> log)
    {
        if (pad.IsBluetooth)
        {
            // v42 (user test 15.11.2026: bt-hd-test "works perfectly"): HD over Bluetooth via report 0x32 - LOCKPICK.md §52-53
            try
            {
                var b = new HdEngine(pad, log);
                b.StartBt();
                log($"HD haptics: ON via Bluetooth report 0x32 (3 kHz, 32 frames / 10.67 ms; mixer 48 kHz; gain {BtGain:0.##}, re-prime {BtRearmMs} ms)");
                return b;
            }
            catch (Exception ex) { log("HD haptics: off on Bluetooth (" + ex.GetType().Name + ": " + ex.Message + ") -> rumble emulation"); return null; }
        }
        try
        {
            string? hidPath = null; try { hidPath = pad.DevicePath; } catch { }
            var dev = HdHaptics.FindDevice(hidPath);
            if (dev == null) { log("HD haptics: off (no DualSense audio device) -> rumble emulation"); return null; }
            var f = dev.AudioClient.MixFormat;
            if (f.Channels < 4 || f.BitsPerSample != 32) { log($"HD haptics: off (audio format {f.Channels} ch / {f.BitsPerSample} bit, need 4 ch float) -> rumble emulation"); return null; }
            if (dev.AudioEndpointVolume.Mute || dev.AudioEndpointVolume.MasterVolumeLevelScalar < 0.05f)
            { log("HD haptics: off (controller audio device muted / 0 % in Windows; set 100 % in mmsys.cpl) -> rumble emulation"); return null; }
            var e = new HdEngine(dev, pad, log);
            e.Start();
            log($"HD haptics: ON via '{dev.FriendlyName}' ({e.Rate} Hz, actuators = ch3/ch4, speaker ch1/ch2 silent; matched by {HdHaptics.LastMatch})");
            return e;
        }
        catch (Exception ex) { log("HD haptics: off (" + ex.GetType().Name + ": " + ex.Message + ") -> rumble emulation"); return null; }
    }

    void StartBt()
    {
        _pad.PrimeBtHaptics();
        _run = true;
        _armThread = new Thread(BtLoop) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "HD over Bluetooth" };
        _armThread.Start();
    }

    /// <summary>Bluetooth sender: while the mixer has voices (and 3 blocks after), every 10.67 ms render 512 samples at 48 kHz,
    /// low-pass (31-tap triangle, as BtHapticsReport.From48k; 15 samples = 0.3 ms delay) and take every 16th -> 32 frames int8
    /// -> report 0x32. Silence = no reports. 1 ms timer only while sending. 20 failed writes in a row -> Failed (rumble fallback).</summary>
    void BtLoop()
    {
        const int Block = 512, Hist = 30;
        var bufL = new float[Hist + Block]; var bufR = new float[Hist + Block];
        var outBlock = new sbyte[64];
        double period = 1000.0 * 32 / 3000;
        var sw = System.Diagnostics.Stopwatch.StartNew(); double next = -1, lastPrime = -1e9; int tail = 0, fails = 0; bool fine = false;
        try
        {
            while (_run)
            {
                bool active = Mixer.Active;
                if (active) tail = 3;
                if (!active && tail <= 0)
                {
                    if (next >= 0) { Array.Clear(bufL); Array.Clear(bufR); next = -1; }
                    if (fine) { timeEndPeriod(1); fine = false; }
                    Thread.Sleep(2); continue;
                }
                if (!fine) { timeBeginPeriod(1); fine = true; }
                double now = sw.Elapsed.TotalMilliseconds;
                if (next < 0) { next = now; lastPrime = -1e9; }
                if (now - lastPrime >= (BtRearmMs > 0 ? BtRearmMs : 1e12) || lastPrime < -1e8)
                {
                    try { _pad.PrimeBtHaptics(); BtPrimes++; } catch { }
                    lastPrime = sw.Elapsed.TotalMilliseconds;
                }
                if (now < next - 1.5) { Thread.Sleep(1); continue; }
                while (sw.Elapsed.TotalMilliseconds < next) Thread.SpinWait(50);
                Array.Copy(bufL, Block, bufL, 0, Hist); Array.Copy(bufR, Block, bufR, 0, Hist);
                Mixer.Render(bufL.AsSpan(Hist, Block), bufR.AsSpan(Hist, Block), Block);
                for (int k = 0; k < 32; k++)
                {
                    int c = 15 + k * 16; double sl = 0, sr = 0;
                    for (int j = -15; j <= 15; j++) { double w = 16 - Math.Abs(j); sl += w * bufL[c + j]; sr += w * bufR[c + j]; }
                    outBlock[2 * k] = BtHapticsReport.Level(sl / 256, BtGain);
                    outBlock[2 * k + 1] = BtHapticsReport.Level(sr / 256, BtGain);
                }
                try { _pad.SendBtHaptics(outBlock); BtReports++; fails = 0; }
                catch (Exception ex)
                {
                    BtWriteFails++;
                    if (++fails == 1) _log("HD over Bluetooth: write failed (" + ex.Message + ")");
                    if (fails >= 20) { Failed = true; _log("HD over Bluetooth: 20 failed writes in a row -> rumble emulation"); return; }
                }
                if (!active) tail--;
                next += period;
                if (sw.Elapsed.TotalMilliseconds > next + 4 * period) next = sw.Elapsed.TotalMilliseconds;   // fell behind: resync
            }
        }
        finally { if (fine) timeEndPeriod(1); }
    }

    void Start()
    {
        _out = new WasapiOut(_dev!, AudioClientShareMode.Shared, true, 20);
        _out.PlaybackStopped += (_, e) => { if (_run) { Failed = true; _log("HD haptics: audio stream stopped" + (e.Exception != null ? " (" + e.Exception.Message + ")" : "")); } };
        _out.Init(_stream!);
        _out.Play();
        try { _pad.ArmHapticsFull(); } catch (Exception ex) { _log("HD haptics: initial arm failed: " + ex.Message); }
        _run = true;
        _armThread = new Thread(ArmLoop) { IsBackground = true, Name = "HD re-arm" };
        _armThread.Start();
    }

    void ArmLoop()
    {
        bool fine = false; var lastActive = DateTime.MinValue;
        try
        {
            while (_run)
            {
                if (Mixer.Active) lastActive = DateTime.UtcNow;
                bool active = (DateTime.UtcNow - lastActive).TotalMilliseconds < 150;   // keep the actuators a moment after the last voice
                if (active && !fine) { timeBeginPeriod(1); fine = true; }
                if (!active && fine) { timeEndPeriod(1); fine = false; }
                if (active)
                {
                    try { _pad.ArmHapticsLight(); Interlocked.Increment(ref ArmReports); } catch { }
                    Thread.Sleep(10);
                }
                else Thread.Sleep(15);
            }
        }
        finally { if (fine) timeEndPeriod(1); }
    }

    public bool Ready => _run && !Failed;

    /// <summary>Adds an effect (normal priority). Overlapping effects are summed and limited.</summary>
    public void Play(float[] left, float[] right) => Play(left, right, 1, "");

    /// <summary>Adds an effect with a priority (0 low .. 3 critical); returns a voice id (0 = rejected / not ready).</summary>
    public long Play(float[] left, float[] right, int priority, string tag) => Play(left, right, new Haptics.VoiceOpts(priority, tag));

    public long Play(float[] left, float[] right, Haptics.VoiceOpts o)
    {
        if (!Ready) return 0;
        if (!Bluetooth) { try { _pad.ArmHapticsLight(); } catch { } }   // USB: take the actuators back right now, before the first audio block
        return Mixer.Add(left, right, o);
    }

    public void Stop(long id, int fadeMs) => Mixer.Stop(id, Rate * fadeMs / 1000);
    public void StopAll() => Mixer.Clear();

    public void Dispose()
    {
        _run = false;
        try { _armThread?.Join(200); } catch { }
        try { _out?.Stop(); } catch { }
        try { _out?.Dispose(); } catch { }
    }

    /// <summary>Endless float stream: silence on ch1/ch2, the mixer on ch3/ch4; detects callback stalls.</summary>
    sealed class StreamProvider : IWaveProvider
    {
        readonly Haptics.HapticMixer _mix; readonly int _ch; float[] _l = new float[4800], _r = new float[4800];
        readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew(); long _lastMs = -1;
        public long Stalls; public int StallMs = 80;
        public WaveFormat WaveFormat { get; }
        public StreamProvider(WaveFormat fmt, Haptics.HapticMixer mix) { WaveFormat = fmt; _ch = fmt.Channels; _mix = mix; }

        public int Read(byte[] buffer, int offset, int count)
        {
            long now = _sw.ElapsedMilliseconds;
            if (_lastMs >= 0 && now - _lastMs > StallMs) Stalls++;
            _lastMs = now;
            int frames = count / (4 * _ch);
            if (_l.Length < frames) { _l = new float[frames]; _r = new float[frames]; }
            _mix.Render(_l, _r, frames);
            var f = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, frames * 4 * _ch));
            f.Clear();
            for (int i = 0; i < frames; i++) { f[i * _ch + 2] = _l[i]; f[i * _ch + 3] = _r[i]; }
            return frames * 4 * _ch;
        }
    }
}

/// <summary>HD effect library: actuator waveforms (left, right) for game events.</summary>
public static class HdFx
{
    static float Clip(double v) => (float)Math.Clamp(v, -0.95, 0.95);

    /// <summary>Diving under: water swell rolling left->right (the signal the user liked in hd-test), WITHOUT the bubble
    /// ticks at the end (user, 13.11.2026: "dived into a lake, not stood under a shower").</summary>
    public static (float[] l, float[] r) Dive(int rate) => (HdHaptics.Dive(rate, 0, 1, bubbles: false), HdHaptics.Dive(rate, 0.22, 2, bubbles: false));

    /// <summary>Landing. effCm = net drop + 0.3 x (apex drop - net): a jump in place (net ~0) stays light, a real fall grows with
    /// sqrt(height) (impact speed). Low 55 Hz thump on both actuators + 150 Hz edge, fast decay; >= 250 cm adds a low 40 Hz tail.
    /// Amplitude 0.06 .. 0.70 (never full scale).</summary>
    public static (float[] l, float[] r) Land(int rate, double effCm)
    {
        double k = Math.Sqrt(Math.Clamp(effCm / 800.0, 0, 1));
        double amp = 0.06 + 0.64 * k, durS = (60 + 200 * k) / 1000.0;
        bool heavy = effCm >= 250;
        int n = (int)(rate * (durS * (heavy ? 2.2 : 1.0) + 0.02));
        var l = new float[n]; var r = new float[n];
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / rate;
            double att = Math.Min(1, t / 0.003);
            double env = att * Math.Exp(-t / (durS * 0.45));
            double v = env * amp * (0.8 * Math.Sin(2 * Math.PI * 55 * t) + 0.25 * Math.Sin(2 * Math.PI * 150 * t));
            if (heavy && t > durS * 0.6)
            {
                double tt = t - durS * 0.6, tl = durS * 1.6;
                v += amp * 0.55 * Math.Sin(Math.PI * Math.Min(1, tt / tl)) * Math.Sin(2 * Math.PI * 40 * t);
            }
            l[i] = Clip(v); r[i] = Clip(v * 0.9);
        }
        return (l, r);
    }

    /// <summary>Surfacing: short light swell that rises from the right side, then a soft "breath" puff of low noise.</summary>
    public static (float[] l, float[] r) Surface(int rate)
    {
        var rnd = new Random(7);
        int n = (int)(rate * 1.1);
        var l = new float[n]; var r = new float[n];
        double lpL = 0, lpR = 0;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / rate;
            double envR = t < 0.55 ? Math.Pow(Math.Sin(Math.PI * t / 0.55), 1.5) : 0;
            double envL = t > 0.12 && t < 0.67 ? Math.Pow(Math.Sin(Math.PI * (t - 0.12) / 0.55), 1.5) : 0;
            double breath = t > 0.75 && t < 1.05 ? Math.Sin(Math.PI * (t - 0.75) / 0.3) : 0;
            lpL += 0.03 * ((rnd.NextDouble() * 2 - 1) - lpL); lpR += 0.03 * ((rnd.NextDouble() * 2 - 1) - lpR);
            r[i] = Clip(envR * (lpR * 6 + 0.25 * Math.Sin(2 * Math.PI * 70 * t)) + breath * lpR * 2.5);
            l[i] = Clip(envL * (lpL * 5 + 0.2 * Math.Sin(2 * Math.PI * 50 * t)) + breath * lpL * 2.5);
        }
        return (l, r);
    }

    /// <summary>Falling into water: a broadband slap (strength from the drop height, sqrt like landing) + a softer dive swell.</summary>
    public static (float[] l, float[] r) Splash(int rate, int dropCm)
    {
        double k = 0.45 + 0.5 * Math.Sqrt(Math.Clamp(dropCm / 800.0, 0, 1));   // 0.45 .. 0.95
        var rnd = new Random(11);
        var (dl, dr) = Dive(rate);
        int slap = (int)(rate * 0.12), off = (int)(rate * 0.06);
        int n = Math.Max(dl.Length, dr.Length) + off;
        var l = new float[n]; var r = new float[n];
        for (int i = 0; i < slap; i++)
        {
            double t = (double)i / rate, env = Math.Exp(-t * 35);
            double s = env * ((rnd.NextDouble() * 2 - 1) * 0.6 + Math.Sin(2 * Math.PI * 90 * t) * 0.6) * k;
            l[i] += (float)s; r[i] += (float)s;
        }
        for (int i = 0; i < dl.Length; i++) l[i + off] += dl[i] * 0.6f;
        for (int i = 0; i < dr.Length; i++) r[i + off] += dr[i] * 0.6f;
        for (int i = 0; i < n; i++) { l[i] = Clip(l[i]); r[i] = Clip(r[i]); }
        return (l, r);
    }

    /// <summary>Renders a rumble-emulation pattern as actuator audio, so the same effect description works on both paths.
    /// Left ("heavy") motor -> left actuator, 60 Hz; right ("light") motor -> right actuator, 170 Hz
    /// (like the firmware's own rumble emulation, but without HID rumble reports that take the actuators away).
    /// Envelope per motor = max of overlapping steps, smoothed (~4 ms) to avoid clicks; amplitude (amp/255)^1.4 * 0.85.</summary>
    public static (float[] l, float[] r) FromRumble(int rate, params (int delay, byte right, byte left, int ms)[] steps)
    {
        int endMs = steps.Length == 0 ? 0 : steps.Max(x => x.delay + x.ms);
        int n = (int)((long)rate * (endMs + 15) / 1000);
        var envL = new float[n]; var envR = new float[n];
        foreach (var (delay, right, left, ms) in steps)
        {
            int a = (int)((long)rate * delay / 1000), b = Math.Min(n, (int)((long)rate * (delay + ms) / 1000));
            // ^1.4: weak rumble values become much weaker in HD (the actuators are far more pronounced than emulated motors)
            float vl = (float)(Math.Pow(left / 255.0, 1.4) * 0.85), vr = (float)(Math.Pow(right / 255.0, 1.4) * 0.85);
            for (int i = Math.Max(0, a); i < b; i++) { if (vl > envL[i]) envL[i] = vl; if (vr > envR[i]) envR[i] = vr; }
        }
        double k = 1 - Math.Exp(-1.0 / (rate * 0.004));
        double sl = 0, sr = 0;
        var l = new float[n]; var r = new float[n];
        for (int i = 0; i < n; i++)
        {
            sl += k * (envL[i] - sl); sr += k * (envR[i] - sr);
            double t = (double)i / rate;
            l[i] = Clip(sl * Math.Sin(2 * Math.PI * 60 * t));
            r[i] = Clip(sr * Math.Sin(2 * Math.PI * 170 * t));
        }
        return (l, r);
    }

    /// <summary>Walking into water: a gentle low ripple, half a second.</summary>
    public static (float[] l, float[] r) Wade(int rate)
    {
        var rnd = new Random(5);
        int n = (int)(rate * 0.6);
        var l = new float[n]; var r = new float[n];
        double lp = 0;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / rate, env = Math.Pow(Math.Sin(Math.PI * t / 0.6), 2);
            lp += 0.02 * ((rnd.NextDouble() * 2 - 1) - lp);
            double s = env * (lp * 4 + 0.15 * Math.Sin(2 * Math.PI * 45 * t));
            l[i] = Clip(s); r[i] = Clip(s * 0.8);
        }
        return (l, r);
    }
}
