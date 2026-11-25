using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DsBridge.Haptics;

namespace DsBridge;

/// <summary>HD haptics over Bluetooth: HID output report 0x32 (layout of DS5Dongle, ported from the controller firmware; the same
/// in chiaki-ng PR #858 and ds5-bt-haptics). 142 bytes: [0] 0x32, [1] seq &lt;&lt; 4 (shared with 0x31), [2] 0x91 (audio control
/// packet 0x11, sized), [3] 4, [4] flags 0x66 (mic off), [5] [6] buffer length 48, [7] packet counter, [8] 0x92 (haptics packet
/// 0x12, sized), [9] 64, [10..73] 32 frames of signed 8-bit stereo L,R at 3 kHz, [138..141] CRC32 (seed 0xA2) little-endian.
/// Community protocol, no Sony document; see LOCKPICK.md §52.</summary>
static class BtHapticsReport
{
    public const int Rate = 3000, Frames = 32, BlockBytes = 64, ReportLen = 142, CrcOffset = 138;

    public static byte[] Build(ReadOnlySpan<sbyte> block, byte seq, byte counter, int bufferLen, byte flags = 0x66, byte audioBuf = 48)
    {
        var r = new byte[Math.Max(bufferLen, ReportLen)];
        r[0] = 0x32; r[1] = (byte)(seq << 4);
        r[2] = 0x91; r[3] = 0x04; r[4] = flags; r[5] = audioBuf; r[6] = audioBuf; r[7] = counter;
        r[8] = 0x92; r[9] = BlockBytes;
        for (int i = 0; i < Math.Min(block.Length, BlockBytes); i++) r[10 + i] = (byte)block[i];
        uint crc = DualSense.Crc32(r, CrcOffset);
        r[138] = (byte)crc; r[139] = (byte)(crc >> 8); r[140] = (byte)(crc >> 16); r[141] = (byte)(crc >> 24);
        return r;
    }

    /// <summary>48 kHz float stereo -> 3 kHz signed 8-bit interleaved L,R. Anti-alias: two passes of a 16-sample box (first null
    /// at 3 kHz, -6 dB near 1 kHz; the actuator band ends ~650 Hz), then every 16th sample.</summary>
    /// <summary>BT level. gain <= 1: linear. gain > 1: tanh(gain*x) / tanh(gain) - weak effects get ~gain x, strong ones less, the
    /// order is kept over the whole range (a hard / knee clip flattened 0.6 and 0.83 to the same maximum and lost the contrast).
    /// Returns -127..127.</summary>
    public static sbyte Level(double x, double gain)
    {
        double y = gain <= 1 ? x * gain : Math.Tanh(gain * x) / Math.Tanh(gain);
        return (sbyte)Math.Clamp((int)Math.Round(y * 127), -127, 127);
    }

    public static sbyte[] From48k(float[] l, float[] r, double gain = 1.0)
    {
        int n = Math.Max(l.Length, r.Length) / 16;
        var o = new sbyte[n * 2];
        float At(float[] x, int i) => i >= 0 && i < x.Length ? x[i] : 0f;
        for (int k = 0; k < n; k++)
        {
            double sl = 0, sr = 0; int c = k * 16;
            for (int j = -15; j <= 15; j++) { double w = 16 - Math.Abs(j); sl += w * At(l, c + j); sr += w * At(r, c + j); }
            sl /= 256; sr /= 256;
            o[2 * k] = Level(sl, gain);
            o[2 * k + 1] = Level(sr, gain);
        }
        return o;
    }
}

/// <summary>Sender: queued 3 kHz frames go out one 32-frame block per report every 10.67 ms on its own thread (1 ms timer,
/// spin for the last ~1.5 ms). Silence = no reports, but a few zero blocks after each sound so the pad's buffer ends at rest.</summary>
sealed class BtHapticsStream : IDisposable
{
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);
    readonly DualSense _pad; readonly Thread _t; volatile bool _stop;
    readonly ConcurrentQueue<sbyte[]> _blocks = new();
    public int Sent, Failed, Underruns; public double MaxWriteMs; public Action<string>? Log;

    public BtHapticsStream(DualSense pad)
    {
        _pad = pad;
        _t = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "bt-haptics" };
        timeBeginPeriod(1); _t.Start();
    }

    /// <summary>Adds interleaved 3 kHz samples (split into 64-byte blocks, the last one zero-padded) + 3 silent blocks.</summary>
    public void Enqueue(sbyte[] interleaved)
    {
        for (int i = 0; i < interleaved.Length; i += BtHapticsReport.BlockBytes)
        {
            var b = new sbyte[BtHapticsReport.BlockBytes];
            Array.Copy(interleaved, i, b, 0, Math.Min(b.Length, interleaved.Length - i));
            _blocks.Enqueue(b);
        }
        for (int i = 0; i < 3; i++) _blocks.Enqueue(new sbyte[BtHapticsReport.BlockBytes]);
    }
    public void Clear() { while (_blocks.TryDequeue(out _)) { } }
    public bool Busy => !_blocks.IsEmpty;

    void Run()
    {
        double period = 1000.0 * BtHapticsReport.Frames / BtHapticsReport.Rate;   // 10.667 ms
        var sw = Stopwatch.StartNew(); double next = -1;
        while (!_stop)
        {
            if (_blocks.IsEmpty) { next = -1; Thread.Sleep(1); continue; }
            double now = sw.Elapsed.TotalMilliseconds;
            if (next < 0) next = now;
            if (now < next - 1.5) { Thread.Sleep(1); continue; }
            while (sw.Elapsed.TotalMilliseconds < next) Thread.SpinWait(50);
            if (!_blocks.TryDequeue(out var b)) continue;
            var w = Stopwatch.StartNew();
            try { _pad.SendBtHaptics(b); Sent++; }
            catch (Exception ex) { if (Failed++ == 0) Log?.Invoke("BT haptics write failed: " + ex.Message); }
            MaxWriteMs = Math.Max(MaxWriteMs, w.Elapsed.TotalMilliseconds);
            next += period;
            if (sw.Elapsed.TotalMilliseconds > next + 4 * period) { next = sw.Elapsed.TotalMilliseconds; Underruns++; }   // fell behind: resync
        }
    }

    public void Dispose() { _stop = true; _t.Join(500); timeEndPeriod(1); }
}

/// <summary>`bt-hd-test`: plays the approved HD effects (the same rules and renderer as the game) over Bluetooth via report 0x32.</summary>
static class BtHdTest
{
    public static void Run(HapticConfig cfg, DualSense pad, Action<string> log, double gain, bool prime)
    {
        if (!pad.IsBluetooth) { log("The controller is on USB: this test is for Bluetooth (USB has HD through the audio device)."); return; }
        if (prime) { pad.PrimeBtHaptics(); log("0x31 prime sent: haptics not muted, motor power reduction 0"); }
        using var bt = new BtHapticsStream(pad) { Log = log };
        (float[] l, float[] r) Rule(string id, Expr.Lookup? vars = null)
        {
            var rule = cfg.Rules.FirstOrDefault(x => x.Id == id);
            if (rule == null) { log("no rule " + id); return (new float[0], new float[0]); }
            return HapticRuntime.RenderHd(rule.Effect, vars ?? (_ => null), 48000);
        }
        (float[] l, float[] r) Tone(double hz, int ms, double amp)
        {
            int n = 48 * ms; var x = new float[n];
            for (int i = 0; i < n; i++) { double env = Math.Min(1, Math.Min(i, n - i) / 480.0); x[i] = (float)(amp * env * Math.Sin(2 * Math.PI * hz * i / 48000)); }
            return (x, (float[])x.Clone());
        }
        (float[] l, float[] r) Side(float[] x, bool right) => right ? (new float[x.Length], x) : (x, new float[x.Length]);
        (float[] l, float[] r) Concat((float[] l, float[] r) a, int gapMs, (float[] l, float[] r) b)
        {
            int off = Math.Max(a.l.Length, a.r.Length) + 48 * gapMs, n = off + Math.Max(b.l.Length, b.r.Length);
            var l = new float[n]; var r = new float[n];
            Array.Copy(a.l, l, a.l.Length); Array.Copy(a.r, r, a.r.Length);
            Array.Copy(b.l, 0, l, off, b.l.Length); Array.Copy(b.r, 0, r, off, b.r.Length);
            return (l, r);
        }
        var items = new (string key, string name, Func<(float[] l, float[] r)> make)[]
        {
            ("1", "test tone 150 Hz, 400 ms (both)", () => Tone(150, 400, 0.6)),
            ("2", "left only, then right only (180 Hz, 200 ms each)", () => Concat(Side(Tone(180, 200, 0.6).Item1, false), 250, Side(Tone(180, 200, 0.6).Item1, true))),
            ("3", "move settled: detent (lp-settle)", () => Rule("lp-settle", n => n switch { "unx" => 0.0, "cin" => 1.0, "cout" => 0.0, "dir" => "R", "ch" => 1.0, _ => null })),
            ("4", "Trained reward: settle + rising double pulse (lp-settle + lp-progress)", () => Concat(Rule("lp-settle", n => n switch { "unx" => 0.0, "cin" => 1.0, "cout" => 0.0, "dir" => "R", "ch" => 1.0, _ => null }), 0, Rule("lp-progress"))),
            ("5", "Trained links: 3 pulses, the last = the route's plate (lp-links k=3 goal=1)", () => Rule("lp-links", n => n switch { "k" => 3.0, "goal" => 1.0, _ => null })),
            ("6", "end stop R1 (lp-nav-stop, right hand)", () => Rule("lp-nav-stop", n => n == "k" ? "W" : null)),
            ("7", "pick break: approved scene + broken tip at 2356 ms", () => Concat(Rule("lp-break-anim"), 2356 - 1536, Rule("lp-break-out"))),
            ("8", "plate motion (lp-motion, 1 plate)", () => Rule("lp-motion", n => n switch { "ch" => 1.0, "same" => 1.0, "opp" => 0.0, "est" => 300.0, "dir" => "R", _ => null })),
            ("9", "as in the game: trigger + light reports, then the tone WITHOUT a new prime", () => Tone(150, 400, 0.6)),
            ("0", "as in the game: trigger + light reports, then the tone WITH a new prime", () => Tone(150, 400, 0.6)),
        };
        log($"Bluetooth HD test (report 0x32, 3 kHz, gain {gain:0.##}). Keys:");
        foreach (var it in items) log($"  {it.key} = {it.name}");
        log("  + / - = gain +0.25 / -0.25 (for the next sound)   f = full audio-stack arm (volumes, as on USB), then the tone");
        log("  q = quit");
        while (true)
        {
            var k = Console.ReadKey(true).KeyChar;
            if (k == 'q' || k == 'Q') break;
            if (k == '+' || k == '=') { gain = Math.Round(gain + 0.25, 2); log($"  gain {gain:0.00}"); continue; }
            if (k == '-' || k == '_') { gain = Math.Max(0.25, Math.Round(gain - 0.25, 2)); log($"  gain {gain:0.00}"); continue; }
            if (k == 'f' || k == 'F')
            {
                pad.ArmHapticsFull(); Thread.Sleep(30);
                var (fl, fr) = Tone(150, 400, 0.6); var fs = BtHapticsReport.From48k(fl, fr, gain);
                bt.Clear(); bt.Enqueue(fs); log($"  full arm (volumes) + tone 150 Hz at gain {gain:0.00}"); continue;
            }
            var it = items.FirstOrDefault(x => x.key == k.ToString());
            if (it.key == null) continue;
            var (l, r) = it.make();
            if (l.Length == 0 && r.Length == 0) continue;
            if (k == '9' || k == '0')
            {
                // the game sends these between sounds (trigger profiles, refresh, pick-life light)
                pad.SetLeftTrigger(DualSense.EffectFeedback(0, 4)); pad.SetRightTrigger(DualSense.EffectFeedback(0, 8));
                pad.SetLightbar(255, 255, 255); pad.SetPlayerLeds(DualSense.PlayerLedPattern(3));
                Thread.Sleep(60);
                if (k == '0') pad.PrimeBtHaptics();
                Thread.Sleep(20);
            }
            var s = BtHapticsReport.From48k(l, r, gain);
            bt.Clear(); bt.Enqueue(s);
            log($"  {it.name} (gain {gain:0.00}): {s.Length / 2 * 1000 / BtHapticsReport.Rate} ms, {(s.Length + 63) / 64} reports; so far sent {bt.Sent}, failed {bt.Failed}, resyncs {bt.Underruns}, max write {bt.MaxWriteMs:F1} ms");
        }
        while (bt.Busy) Thread.Sleep(20);
        log($"done: sent {bt.Sent}, failed {bt.Failed}, resyncs {bt.Underruns}, max write {bt.MaxWriteMs:F1} ms");
    }
}
