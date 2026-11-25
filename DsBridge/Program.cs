using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using DsBridge;
using DsBridge.Haptics;

// DsBridge — DualSense effects for Gothic 1 Remake (prototype)
//   list                         enumerate DualSense HID interfaces (read-only)
//   selftest                     offline checks: report layout, CRC, effects, event parser
//   test-trigger                 R2 resistance 4 -> 8 -> off          (controller)
//   test-rumble                  motors 50% -> 100% -> off            (controller)
//   test-light                   lightbar red -> green -> blue -> restore (controller)
//   off                          reset triggers + motors, restore lightbar
//   watch [--process NAME]       print game window titles (no controller)
//   monitor [--seconds N] [--log FILE]   Game Event Monitor: decode + log every game event (no controller)
//   run   [--process NAME] [--target physical|virtual]   companion (default: physical pad direct, no DSX)
//   forge [RECIPE] [--variants A,B] [--explore N --from B] [--check]   generate + check + export the HD bank (no controller)
//   hd-compare [--bank lp]       compare the forge variants on the pad (USB HD), save haptics/bank/selection.json
// Options: --allow-bt  --method write|setreport  --restore-light R,G,B  --dump

var argList = args.ToList();
bool allowBt = argList.Remove("--allow-bt");
bool dump = argList.Remove("--dump");
bool lightOnEvent = argList.Remove("--light");
string method = GetOpt("--method", "write");
string processName = GetOpt("--process", "G1R-Win64-Shipping");
string target = GetOpt("--target", "any");
var restore = GetOpt("--restore-light", "0,0,0").Split(',').Select(byte.Parse).ToArray();
string mode = argList.Count > 0 ? argList[0].ToLowerInvariant() : "list";
foreach (var ep in GetOpt("--enable-pack", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) HapticConfig.EnablePacks.Add(ep);   // switch on a pack that haptics.json keeps off (candidates)

DualSense? pad = null;
bool lightTouched = false;
bool resetTriggersOnCleanup = true;
var cleanupLock = new object();
bool cleaned = false;
TestHud? testHud = null;   // run: test checklist shown in the game (closed / hidden on exit)
Mutex? padWriter = null;   // held while this process writes to the pad (OpenOrDie)

void Cleanup(string why)
{
    lock (cleanupLock)
    {
        try { testHud?.Close(DateTime.UtcNow); } catch { }
        if (cleaned || pad == null) return;
        cleaned = true;
        try { if (resetTriggersOnCleanup) pad.ResetTriggers(); pad.SetRumble(0, 0); if (lightTouched) { pad.SetLightbar(restore[0], restore[1], restore[2]); pad.SetPlayerLeds(0); } Log($"cleanup done ({why})"); }
        catch (Exception ex) { Log($"cleanup failed ({why}): {ex.Message}"); }
        try { pad.Dispose(); } catch { }
    }
}
Console.CancelKeyPress += (_, e) => { e.Cancel = true; Cleanup("Ctrl+C"); Environment.Exit(0); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup("process exit");
AppDomain.CurrentDomain.UnhandledException += (_, e) => { Log("unhandled: " + e.ExceptionObject); Cleanup("unhandled exception"); };

DualSense OpenOrDie()
{
    // one writer to the pad: run / lp-bench / hd-compare / replay --live / test-* exclude each other (DsBridge builds from 14.11.2026 on)
    padWriter = new Mutex(false, @"Local\G1R_DsBridge_PadWriter");
    bool owned; try { owned = padWriter.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
    if (!owned) { Log("another DsBridge (run / lp-bench / hd-compare / replay --live) already writes to the controller: stop it first (one writer per pad)."); Environment.Exit(3); }
    var p = DualSense.Open(allowBt, method, out var msg, target);
    Log(msg);
    if (p == null) Environment.Exit(2);
    Log($"transport={(p!.IsBluetooth ? "Bluetooth (report 0x31, 78 bytes + padding)" : "USB (report 0x02)")} buffer={p.OutputReportLength}");
    return p;
}

void Step(string what, Action a)
{
    a();
    Log(what + (dump && pad?.LastReport != null ? "\n    " + Hex(pad.LastReport, pad.IsBluetooth ? 78 : 48) : ""));
}

switch (mode)
{
    case "list":
        foreach (var d in DualSense.Enumerate())
        {
            Log(DualSense.Describe(d));
            // read-only: which audio endpoint belongs to this pad (Windows Container ID), as HdEngine picks it
            var cid = DeviceIds.ContainerOfHid(d.DevicePath);
            var ep = HdHaptics.FindDevice(d.DevicePath);
            Log($"  container {(cid?.ToString() ?? "none")}; audio endpoint: {(ep != null ? $"'{ep.FriendlyName}' (matched by {HdHaptics.LastMatch})" : "none (Bluetooth or no cable)")}");
        }
        break;

    case "touchpad-selftest":
    {
        int failures = 0;
        foreach (var (name, ok) in TouchpadChecks.Run())
        { Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}"); if (!ok) failures++; }
        Environment.Exit(failures == 0 ? 0 : 1);
        break;
    }

    case "selftest":
        Environment.Exit(SelfTest.Run() ? 0 : 1);
        break;

    case "test-trigger":
        pad = OpenOrDie();
        Step("R2: resistance strength 4 is ON. Press R2 to feel it, then press Enter here.", () => pad.SetRightTrigger(DualSense.EffectFeedback(0, 4)));
        Console.ReadLine();
        Step("R2: resistance strength 8 (maximum) is ON. Press R2, then press Enter.", () => pad.SetRightTrigger(DualSense.EffectFeedback(0, 8)));
        Console.ReadLine();
        Step("R2: resistance OFF. Press R2 — it should move freely.", () => pad.ResetTriggers());
        Cleanup("test finished");
        break;

    case "test-rumble":
        pad = OpenOrDie();
        Step("rumble 50% (1 s)", () => pad.SetRumble(128, 128)); Thread.Sleep(1000);
        Step("rumble 100% (1 s)", () => pad.SetRumble(255, 255)); Thread.Sleep(1000);
        Step("rumble off", () => pad.SetRumble(0, 0));
        Cleanup("test finished");
        break;

    case "test-light":
        pad = OpenOrDie(); lightTouched = true;
        bool noSetup = argList.Remove("--no-setup");
        int fadeMs = int.Parse(GetOpt("--fade-ms", "3000"));
        if (!noSetup)
        {
            Step($"lightbar setup LIGHT_OUT (take control from firmware), waiting {fadeMs} ms for the fade", () => pad.ReleaseStartupLight());
            Thread.Sleep(fadeMs);
        }
        Step("lightbar RED (2 s)", () => pad.SetLightbar(255, 0, 0)); Thread.Sleep(2000);
        Step("lightbar GREEN (2 s)", () => pad.SetLightbar(0, 255, 0)); Thread.Sleep(2000);
        Step("lightbar WHITE (2 s)", () => pad.SetLightbar(255, 255, 255)); Thread.Sleep(2000);
        Step($"restore lightbar {restore[0]},{restore[1]},{restore[2]}", () => pad.SetLightbar(restore[0], restore[1], restore[2]));
        Cleanup("test finished");
        break;

    case "dsx-status":
    {
        using var dsx = new DsxClient();
        Log($"DSX UDP 127.0.0.1:{dsx.Port}  packet: {DsxClient.BuildPacket((DsxClient.Instr.GetDSXStatus, Array.Empty<object>()))}");
        Log(DsxClient.Summarize(dsx.Status()));
        break;
    }

    case "dsx-test":
    {
        string what = argList.Count > 1 ? argList[1] : "all";
        using var dsx = new DsxClient();
        void Show(string step, System.Text.Json.Nodes.JsonNode? r) => Log($"{step}: {DsxClient.Summarize(r).Split('\n')[0]}{DiagText(r)}");
        static string DiagText(System.Text.Json.Nodes.JsonNode? r) =>
            r?["Diagnostics"] is System.Text.Json.Nodes.JsonArray a && a.Count > 0 ? "  DIAG: " + string.Join("; ", a.Select(x => $"{x?["Code"]} {x?["Message"]}")) : "";
        if (what is "trigger" or "all")
        {
            Show("R2 Rigid (press R2, Enter = next)", dsx.Trigger(DsxClient.Right, DsxClient.TriggerMode.Rigid)); Console.ReadLine();
            Show("R2 VeryHard (press R2, Enter = next)", dsx.Trigger(DsxClient.Right, DsxClient.TriggerMode.VeryHard)); Console.ReadLine();
            Show("R2 Normal", dsx.Trigger(DsxClient.Right, DsxClient.TriggerMode.Normal));
        }
        if (what is "rumble" or "haptics" or "all") { for (int i = 3; i > 0; i--) { Log($"starting in {i}..."); Thread.Sleep(1000); } }
        if (what is "rumble" or "all")
        {
            Show(">>> RUMBLE RIGHT side (small motor) 1 s", dsx.Rumble(0, 255, 1000)); Thread.Sleep(2000);
            Show(">>> RUMBLE LEFT side (large motor) 1 s", dsx.Rumble(255, 0, 1000)); Thread.Sleep(2000);
            Show(">>> RUMBLE BOTH 1 s", dsx.Rumble(200, 200, 1000)); Thread.Sleep(2000);
        }
        if (what is "haptics" or "all")
        {
            Show(">>> HAPTICS sine 160 Hz, full strength, 1 s", dsx.Haptics(DsxClient.Waveform.Sine, 160, 255, 1000)); Thread.Sleep(2000);
            Show(">>> HAPTICS square 60 Hz, full strength, 1 s", dsx.Haptics(DsxClient.Waveform.Square, 60, 255, 1000)); Thread.Sleep(2000);
        }
        if (what is "light" or "all")
        {
            Show("lightbar RED", dsx.Rgb(255, 0, 0)); Thread.Sleep(1500);
            Show("lightbar GREEN", dsx.Rgb(0, 255, 0)); Thread.Sleep(1500);
        }
        Show("reset to DSX user settings", dsx.Reset());
        break;
    }

    case "xinput-test":
    {
        // Rumble a virtual Xbox 360 pad (DSX emulation) through XInput; DSX forwards it to the DualSense.
        int found = -1;
        for (uint i = 0; i < 4; i++) if (XInput.GetState(i, out _) == 0) { Log($"XInput pad #{i}: connected"); if (found < 0) found = (int)i; }
        if (found < 0) { Log("No XInput pad. In DSX set controller emulation to Xbox 360."); break; }
        uint u = (uint)found;
        for (int i = 3; i > 0; i--) { Log($"starting in {i}..."); Thread.Sleep(1000); }
        Log(">>> RIGHT motor (high freq) 1 s"); XInput.SetState(u, 0, 40000); Thread.Sleep(1000); XInput.SetState(u, 0, 0); Thread.Sleep(1000);
        Log(">>> LEFT motor (low freq) 1 s"); XInput.SetState(u, 40000, 0); Thread.Sleep(1000); XInput.SetState(u, 0, 0); Thread.Sleep(1000);
        Log(">>> BOTH 1 s"); XInput.SetState(u, 30000, 30000); Thread.Sleep(1000); XInput.SetState(u, 0, 0);
        Log("done");
        break;
    }

    case "input-dump":
    {
        // diagnostics: first bytes of input reports + decoded L2/R2 (read only, nothing is written to the pad)
        int seconds = int.Parse(GetOpt("--seconds", "3"));
        allowBt = true; pad = OpenOrDie(); resetTriggersOnCleanup = false;
        int nrep = 0; string? lastLine = null;
        using var ctsd = new CancellationTokenSource();
        pad.StartInputReader((buf, n) =>
        {
            nrep++;
            DualSense.TryGetTriggers(buf, n, pad.IsBluetooth, out var l2, out var r2);
            var line = $"id=0x{buf[0]:X2} len={n} bytes[0..11]={Hex(buf, 12)} -> L2={l2} R2={r2}";
            if (line != lastLine) { Log(line); lastLine = line; }
        }, ctsd.Token);
        Thread.Sleep(seconds * 1000); ctsd.Cancel();
        Log($"input reports: {nrep}");
        break;
    }

    case "test-buttons":
    {
        int seconds = int.Parse(GetOpt("--seconds", "20"));
        pad = OpenOrDie();
        byte prev = 0; int reports = 0; byte lastId = 0;
        using var cts = new CancellationTokenSource();
        pad.StartInputReader((buf, n) =>
        {
            reports++; lastId = buf[0];
            if (!DualSense.TryGetFaceButtons(buf, n, pad.IsBluetooth, out byte b)) return;
            byte face = (byte)(b & 0xF0);
            if (face != (prev & 0xF0))
            {
                var names = new List<string>();
                if ((b & DualSense.BtnSquare) != 0) names.Add("SQUARE");
                if ((b & DualSense.BtnCross) != 0) names.Add("CROSS");
                if ((b & DualSense.BtnCircle) != 0) names.Add("CIRCLE (O)");
                if ((b & DualSense.BtnTriangle) != 0) names.Add("TRIANGLE");
                Log($"report 0x{buf[0]:X2} len={n}: {(names.Count > 0 ? string.Join("+", names) : "released")}");
            }
            prev = b;
        }, cts.Token);
        Log($"Press face buttons for {seconds} s (O, X, Square, Triangle)...");
        Thread.Sleep(seconds * 1000);
        cts.Cancel();
        Log($"input reports received: {reports}, last report id 0x{lastId:X2}");
        Cleanup("test finished");
        break;
    }

    case "off":
        pad = OpenOrDie(); lightTouched = true;
        Cleanup("off command");
        break;

    case "watch":
    {
        Log($"Watching window titles of process '{processName}' (no controller access). Ctrl+C to stop.");
        var w = new GameWatcher(processName); string? last = null;
        while (true)
        {
            var t = w.Poll(out bool running);
            var s = running ? $"title='{t}'" : "process not running";
            if (s != last) { Log(s); last = s; }
            Thread.Sleep(10);
        }
    }

    case "listen":
    {
        int seconds = int.Parse(GetOpt("--seconds", "0"));
        Log($"Listening for [G1RDS:...] events from '{processName}' (no controller access).");
        var w = new GameWatcher(processName); var parser = new EventParser(); bool was = false;
        var until = seconds > 0 ? DateTime.UtcNow.AddSeconds(seconds) : DateTime.MaxValue;
        while (DateTime.UtcNow < until)
        {
            var t = w.Poll(out bool running);
            if (running != was) { Log(running ? $"process found, title='{t}'" : "process not running"); parser.Reset(); was = running; }
            if (running && t != null) foreach (var ev in parser.Feed(t, DateTime.UtcNow)) Log($"event '{ev.Name}' id={ev.Id}");
            Thread.Sleep(5);
        }
        break;
    }

    case "hd-probe":
    {
        // Read-only: who renders into the controller's audio device, and per-channel levels for a few seconds.
        var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        foreach (var d in en.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.All, NAudio.CoreAudioApi.DeviceState.Active)
                            .Where(d => d.FriendlyName.Contains("DualSense", StringComparison.OrdinalIgnoreCase) || d.FriendlyName.Contains("Wireless Controller", StringComparison.OrdinalIgnoreCase)))
        {
            Log($"endpoint '{d.FriendlyName}' flow={d.DataFlow} mix={d.AudioClient.MixFormat.Channels}ch vol={d.AudioEndpointVolume.MasterVolumeLevelScalar * 100:F0}%");
            var ss = d.AudioSessionManager.Sessions;
            for (int i = 0; i < ss.Count; i++)
            {
                var x = ss[i]; string pn = "?"; try { pn = Process.GetProcessById((int)x.GetProcessID).ProcessName; } catch { }
                Log($"  session pid={x.GetProcessID} ({pn}) state={x.State} peak={x.AudioMeterInformation.MasterPeakValue:F2}");
            }
            if (d.DataFlow == NAudio.CoreAudioApi.DataFlow.Render)
            {
                var pk = new float[8];
                for (int t = 0; t < 150; t++) { var pv = d.AudioMeterInformation.PeakValues; for (int c = 0; c < Math.Min(8, pv.Count); c++) pk[c] = Math.Max(pk[c], pv[c]); Thread.Sleep(20); }
                Log("  peaks over 3 s: " + string.Join("  ", pk.Take(d.AudioClient.MixFormat.Channels).Select((p, i) => $"ch{i + 1}={p:F2}")));
            }
        }
        break;
    }

    case "hd-variants":
    {
        // One run, three ways to arm the actuators, each followed by a thud + ticks. User reports which one was felt.
        var dev = HdHaptics.FindDevice();
        if (dev == null) { Log("No DualSense audio device (USB cable needed)."); Environment.Exit(2); }
        Log("HD haptics device: " + HdHaptics.Describe(dev!));
        if (dev!.AudioEndpointVolume.MasterVolumeLevelScalar < 0.05f) { Log("STOP: controller audio device volume is 0 %."); Environment.Exit(4); }
        var hp = DualSense.Open(false, method, out var hmsg, "physical");
        if (hp == null || hp.IsBluetooth) { Log("Need the pad over USB: " + hmsg); Environment.Exit(2); }
        int rate = dev.AudioClient.MixFormat.SampleRate;
        var th = HdHaptics.Thud(rate); var tk = HdHaptics.Ticks(rate);
        var (l, r) = HdHaptics.Concat(rate, (th, th), (tk, tk));
        var variants = new (string name, byte[] common)[]
        {
            ("A: all-zero report (nondebug: 'all-zero 0x02 arms audio haptics')", new byte[47]),
            ("B: audio stack init, LEDs/triggers untouched (F0 82 .. 9D 66 40 6C)", new byte[] { 0xF0, 0x82, 0, 0, 0x9D, 0x66, 0x40, 0x6C, 0x00, 0x00 }),
            ("C: exact 'initialized' dump (FC D7 .. 9D 66 40 6C 01) - lightbar/player LEDs may go dark", new byte[] { 0xFC, 0xD7, 0, 0, 0x9D, 0x66, 0x40, 0x6C, 0x01, 0x00 }),
        };
        foreach (var (name, common) in variants)
        {
            Console.WriteLine(); Log("==================== VARIANT " + name);
            hp!.SendRaw(common); Log("HID report: " + Hex(hp.LastReport!, 12));
            Thread.Sleep(400);
            HdHaptics.Play(dev, l, r, rate);
            Log("played; peak ch3=" + HdHaptics.LastPeaks[2].ToString("F2") + " ch4=" + HdHaptics.LastPeaks[3].ToString("F2"));
            Thread.Sleep(1500);
        }
        hp!.Dispose();
        Log("done: tell which VARIANT (A/B/C) you felt, if any");
        break;
    }

    case "hd-ab":
    {
        // Same signal (ticks + dive) three times; only the way the actuators are held differs. Run it in the game.
        var dev = HdHaptics.FindDevice();
        if (dev == null) { Log("No DualSense audio device (USB cable needed)."); Environment.Exit(2); }
        if (dev!.AudioEndpointVolume.MasterVolumeLevelScalar < 0.05f) { Log("STOP: controller audio device volume is 0 %."); Environment.Exit(4); }
        var hp = DualSense.Open(false, method, out var hmsg, "physical");
        if (hp == null || hp.IsBluetooth) { Log("Need the pad over USB: " + hmsg); Environment.Exit(2); }
        int rate = dev.AudioClient.MixFormat.SampleRate;
        var tk = HdHaptics.Ticks(rate);
        var (l, r) = HdHaptics.Concat(rate, (tk, tk), (HdHaptics.Dive(rate, 0, 1), HdHaptics.Dive(rate, 0.22, 2)));
        var runs = new (string name, Action arm, int ms)[]
        {
            ("A: full report every 50 ms (as last time)", () => hp!.ArmHapticsFull(), 50),
            ("B: full report every 10 ms", () => hp!.ArmHapticsFull(), 10),
            ("C: light report every 10 ms (volumes not re-sent)", () => hp!.ArmHapticsLight(), 10),
        };
        foreach (var (name, arm, ms) in runs)
        {
            Console.WriteLine(); Log("==================== " + name);
            try { hp!.ArmHapticsFull(); } catch (Exception ex) { Log("arm failed: " + ex.Message); }
            HdHaptics.Rearm = arm; HdHaptics.RearmMs = ms;
            HdHaptics.Play(dev, l, r, rate);
            Log($"played; re-arm reports: {HdHaptics.RearmCount} (every {HdHaptics.LastRearmEveryMs:F1} ms); peak ch3={HdHaptics.LastPeaks[2]:F2}");
            Thread.Sleep(2000);
        }
        HdHaptics.Rearm = null; HdHaptics.RearmMs = 0; hp!.Dispose();
        Log("done: which run felt best/smoothest - A, B or C?");
        break;
    }

    case "hd-test":
    {
        // Real HD haptics over USB (audio channels 3/4). --info: only show the device, play nothing.
        var dev = HdHaptics.FindDevice();
        if (dev == null) { Log("No DualSense audio device (connect the USB cable; Bluetooth has no audio device in Windows)."); Environment.Exit(2); }
        Log("HD haptics device: " + HdHaptics.Describe(dev!));
        Log(HdHaptics.DefaultsInfo(dev!));
        if (argList.Remove("--info")) break;
        HdHaptics.Exclusive = argList.Remove("--exclusive");
        if (HdHaptics.Exclusive) Log("WASAPI exclusive mode (no Windows mixer / enhancements)");
        if (dev!.AudioEndpointVolume.Mute || dev.AudioEndpointVolume.MasterVolumeLevelScalar < 0.05f)
        {
            Log("STOP: the controller's audio device is muted / at 0 % volume in Windows -> the actuators would get silence.");
            Log("Set it in mmsys.cpl -> Playback -> 'Speakers (DualSense Wireless Controller)' -> Properties -> Levels -> 100.");
            Environment.Exit(4);
        }
        int rate = dev.AudioClient.MixFormat.SampleRate;
        var thud = HdHaptics.Thud(rate); var ticks = HdHaptics.Ticks(rate);
        var (l, r) = HdHaptics.Concat(rate, (thud, thud), (ticks, ticks), (HdHaptics.Dive(rate, 0, 1), HdHaptics.Dive(rate, 0.22, 2)));
        if (!argList.Remove("--no-hid"))
        {
            var hp = DualSense.Open(false, method, out var hmsg, "physical");
            if (hp == null || hp.IsBluetooth) Log("HID prepare skipped: " + hmsg);
            else try
            {
                hp.ArmHapticsFull();
                Log("HID: variant D sent (audio init + motor power 0 + haptic LPF off + improved rumble emulation off) (" + hmsg + ")");
                Log("HID report: " + Hex(hp.LastReport!, 12));
                int rearmMs = int.Parse(GetOpt("--rearm-ms", "0"));
                if (rearmMs > 0)
                {
                    // keep the handle: re-send variant D during playback so another writer (the game) can't take the actuators back
                    HdHaptics.Rearm = () => hp.ArmHapticsFull(); HdHaptics.RearmMs = rearmMs;
                    Log($"HID: re-arming every {rearmMs} ms during playback");
                }
                else { Thread.Sleep(150); hp.Dispose(); }
            }
            catch (Exception ex) { Log("HID prepare failed (" + ex.GetType().Name + ": " + ex.Message + ") -> playing audio anyway (another program, e.g. DSX, may own the pad)"); try { hp.Dispose(); } catch { } }
        }
        Log("Playing: 1) low thud  2) five crisp ticks  3) diving: water swell rolling left->right + rising bubbles  (~5 s)");
        HdHaptics.Play(dev, l, r, rate);
        if (HdHaptics.RearmMs > 0) Log($"re-arm reports sent: {HdHaptics.RearmCount}");
        Log("done; Windows peak meter per channel: " + string.Join("  ", HdHaptics.LastPeaks.Select((p, i) => $"ch{i + 1}={p:F2}")) + "  (haptics = ch3/ch4)");
        break;
    }

    case "rules-check":
    {
        // Lints haptics/haptics.json + packs + registry and renders every rule once (no controller, no game).
        var path = HapticConfig.Locate(GetOpt("--haptics", "") is var hp0 && hp0.Length > 0 ? hp0 : null);
        if (path == null) { Log("no haptics/haptics.json found (use --haptics PATH)"); Environment.Exit(2); }
        var cfg = HapticConfig.Load(path!, GetOpt("--disable-pack", "").Split(',', StringSplitOptions.RemoveEmptyEntries));
        var (ok, text) = RulesCheck.Run(cfg);
        Console.Write(text);
        Environment.Exit(ok ? 0 : 1);
        break;
    }

    case "journal-from-log":
    {
        // Converts a `run --diag` / `monitor` log into a replayable journal: journal-from-log LOG OUT.jsonl
        if (argList.Count < 3) { Log("usage: journal-from-log <diag log> <out.jsonl>"); Environment.Exit(2); }
        int n = Journal.FromDiagLog(argList[1], argList[2]);
        Log($"{n} events -> {Path.GetFullPath(argList[2])}");
        break;
    }

    case "trigger-vibration-check":
    case "trigger-click-gallery":
    {
        Console.OutputEncoding = Encoding.UTF8;
        if(argList.Contains("--help"))
        {Console.WriteLine("trigger-click-gallery [--check] [--report FILE.json]\nL1/R1 or 1-6: select; L2/R2: feel load, snap and light tail; Square/s: save; Circle/Esc: exit. -/=: tail threshold. No game changes.");break;}
        var clickPath=HapticConfig.Locate(GetOpt("--haptics", "") is var ch && ch.Length>0 ? ch : null);
        if(clickPath==null){Log("no haptics/haptics.json found");Environment.Exit(2);}
        var clickCfg=HapticConfig.Load(clickPath!);
        bool vibrationProbe=mode=="trigger-vibration-check";
        var clickRecipe=Path.Combine(clickCfg.Root,vibrationProbe?"experiments/trigger-vibration-probe.json":TriggerClickGallery.Pack);
        var clicks=TriggerClickGallery.Read(clickRecipe);
        if(!vibrationProbe)clickCfg.AddSupplementalPack(TriggerClickGallery.HdPack);
        var clickChecks=(vibrationProbe?TriggerClickGallery.ProbeChecks(clicks):TriggerClickGallery.Checks(clicks).Concat(TriggerClickGallery.HdChecks(clickCfg))).ToArray();
        if(argList.Remove("--check"))
        {foreach(var c in clickChecks)Log(c.name+": "+(c.ok?"OK":"FAIL"));Environment.Exit(clickChecks.All(c=>c.ok)?0:1);}
        if(clickChecks.Any(c=>!c.ok)){Log("Trigger click checks failed");Environment.Exit(1);}
        if(Console.IsInputRedirected){Log("Interactive console required");Environment.Exit(2);}
        if(Process.GetProcessesByName("DSX").Length>0){Log("Close DSX first");Environment.Exit(3);}
        var clickReport=GetOpt("--report",Path.Combine(clickCfg.Root,"..","reports",$"{(vibrationProbe?"trigger-vibration-check":"trigger-click-gallery")}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json"));
        if(!int.TryParse(GetOpt("--variant","1"),out int clickStart)||clickStart<1||clickStart>clicks.Length){Log("Invalid --variant");Environment.Exit(2);}
        allowBt=true;pad=OpenOrDie();resetTriggersOnCleanup=true;
        TriggerClickGallery.Run(pad,clicks,File.ReadAllText(clickRecipe),clickReport,Log,vibrationProbe,clickStart,vibrationProbe?null:clickCfg);
        break;
    }

    case "failure-sync":
    case "failure-gallery":
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (argList.Contains("--help"))
        {
            Console.WriteLine("failure-gallery [--check] [--variant 1..6] [--report FILE.json]\n6 полных вариантов поломки отмычки по 2400 мс. L1/R1 — предыдущий/следующий, Cross/Triangle — повтор, Square — сохранить, Circle/Esc — выход.\nКлавиатура 1–6: выбрать образец. Только USB HD, без запуска игры и изменения игровых правил.");
            break;
        }
        var failurePath = HapticConfig.Locate(GetOpt("--haptics", "") is var uh && uh.Length > 0 ? uh : null);
        if (failurePath == null) { Log("no haptics/haptics.json found"); Environment.Exit(2); }
        var failureCfg = HapticConfig.Load(failurePath!);
        if (!failureCfg.Ok) { foreach (var error in failureCfg.Errors) Log(error); Environment.Exit(1); }
        bool animationSync=mode=="failure-sync";
        string failureRecipePack=animationSync?FailureGallery.SyncPack:FailureGallery.Pack;
        failureCfg.AddSupplementalPack(failureRecipePack);
        var failureChecks = (animationSync?FailureGallery.SyncChecks(failureCfg):FailureGallery.Checks(failureCfg)).ToArray();
        if (argList.Remove("--check"))
        {
            foreach (var check in failureChecks) Log(check.name + ": " + (check.ok ? "OK" : "FAIL"));
            Environment.Exit(failureChecks.All(c => c.ok) ? 0 : 1);
        }
        if (failureChecks.Any(c => !c.ok)) { foreach (var check in failureChecks.Where(c => !c.ok)) Log(check.name + ": FAIL"); Environment.Exit(1); }
        if (Console.IsInputRedirected) { Log("Нужно интерактивное окно консоли."); Environment.Exit(2); }
        if (Process.GetProcessesByName("DSX").Length > 0) { Log("Закройте DSX перед тестом."); Environment.Exit(3); }
        var failureReport = GetOpt("--report", Path.Combine(failureCfg.Root, "..", "reports", $"{(animationSync?"failure-sync":"failure-gallery")}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json"));
        if (!int.TryParse(GetOpt("--variant", "0"), out int failureStart) || failureStart < 0 || failureStart > (animationSync?1:FailureGallery.Count))
        { Log("--variant: нужен номер от 1 до 6."); Environment.Exit(2); }
        allowBt = true; pad = OpenOrDie(); resetTriggersOnCleanup = true;
        FailureGallery.Run(failureCfg, pad, Log, failureReport, failureStart,failureRecipePack);
        break;
    }

    case "unlock-gallery":
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (argList.Contains("--help"))
        {
            Console.WriteLine("unlock-gallery [--check] [--variant 1..6] [--report FILE.json]\n6 полных вариантов открытия по 2500 мс. L1/R1 — предыдущий/следующий, Cross/Triangle — повтор, Square — сохранить, Circle/Esc — выход.\nКлавиатура 1–6: выбрать образец. Только USB HD, без запуска игры и изменения игровых правил.");
            break;
        }
        var unlockPath = HapticConfig.Locate(GetOpt("--haptics", "") is var uh && uh.Length > 0 ? uh : null);
        if (unlockPath == null) { Log("no haptics/haptics.json found"); Environment.Exit(2); }
        var unlockCfg = HapticConfig.Load(unlockPath!);
        if (!unlockCfg.Ok) { foreach (var error in unlockCfg.Errors) Log(error); Environment.Exit(1); }
        unlockCfg.AddSupplementalPack(UnlockGallery.Pack);
        var unlockChecks = UnlockGallery.Checks(unlockCfg).ToArray();
        if (argList.Remove("--check"))
        {
            foreach (var check in unlockChecks) Log(check.name + ": " + (check.ok ? "OK" : "FAIL"));
            Environment.Exit(unlockChecks.All(c => c.ok) ? 0 : 1);
        }
        if (unlockChecks.Any(c => !c.ok)) { foreach (var check in unlockChecks.Where(c => !c.ok)) Log(check.name + ": FAIL"); Environment.Exit(1); }
        if (Console.IsInputRedirected) { Log("Нужно интерактивное окно консоли."); Environment.Exit(2); }
        if (Process.GetProcessesByName("DSX").Length > 0) { Log("Закройте DSX перед тестом."); Environment.Exit(3); }
        var unlockReport = GetOpt("--report", Path.Combine(unlockCfg.Root, "..", "reports", $"unlock-gallery-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json"));
        if (!int.TryParse(GetOpt("--variant", "0"), out int unlockStart) || unlockStart < 0 || unlockStart > UnlockGallery.Count)
        { Log("--variant: нужен номер от 1 до 6."); Environment.Exit(2); }
        allowBt = true; pad = OpenOrDie(); resetTriggersOnCleanup = true;
        UnlockGallery.Run(unlockCfg, pad, Log, unlockReport, unlockStart);
        break;
    }

    case "lp-height-gallery":
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (argList.Contains("--help"))
        {
            Console.WriteLine("lp-height-gallery [--confirm] [--check] [--report FILE.json]\nОбычно: 6 рисунков вниз и 6 вверх. L1/R1 — следующий вариант своей группы.\n--confirm: L1/R1 повторяют выбранную пару, Cross перелистывает 6 подтверждений НАЙДЕНО.\nTriangle — повтор, Square — сохранить, Circle/Esc — выход. Только USB HD, без вибрации триггеров и зачёта. Игра не изменяется.");
            break;
        }
        var galleryPath = HapticConfig.Locate(GetOpt("--haptics", "") is var gp && gp.Length > 0 ? gp : null);
        if (galleryPath == null) { Log("no haptics/haptics.json found"); Environment.Exit(2); }
        var galleryCfg = HapticConfig.Load(galleryPath!);
        if (!galleryCfg.Ok) { foreach (var error in galleryCfg.Errors) Log(error); Environment.Exit(1); }
        galleryCfg.AddSupplementalPack(LpHeightGallery.Pack);
        bool confirmation = argList.Remove("--confirm");
        if (confirmation) galleryCfg.AddSupplementalPack(LpHeightGallery.ConfirmPack);
        var galleryChecks = LpHeightGallery.Checks(galleryCfg, confirmation).ToArray();
        if (argList.Remove("--check"))
        {
            foreach (var check in galleryChecks) Log(check.name + ": " + (check.ok ? "OK" : "FAIL"));
            Environment.Exit(galleryChecks.All(c => c.ok) ? 0 : 1);
        }
        if (galleryChecks.Any(c => !c.ok)) { foreach (var check in galleryChecks.Where(c => !c.ok)) Log(check.name + ": FAIL"); Environment.Exit(1); }
        if (Console.IsInputRedirected) { Log("Нужно интерактивное окно консоли."); Environment.Exit(2); }
        if (Process.GetProcessesByName("DSX").Length > 0) { Log("Закройте DSX перед галереей."); Environment.Exit(3); }
        var galleryReport = GetOpt("--report", Path.Combine(galleryCfg.Root, "..", "reports", $"{(confirmation ? "lp-confirm-gallery" : "lp-height-gallery")}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json"));
        allowBt = true; pad = OpenOrDie(); resetTriggersOnCleanup = true;
        LpHeightGallery.Run(galleryCfg, pad, Log, galleryReport, confirmation);
        break;
    }

    case "lp-guide-test":
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (argList.Contains("--help"))
        {
            Console.WriteLine("lp-guide-test [--height | --finger | --metal] [--rounds N] [--seed N] [--report FILE.json]\nОбычно: v21 против four, 20 заданий на схему. --finger: прежний эксперимент с триггерами.\n--metal: механические рисунки (шероховатая защёлка / упругая пружина), ТОЛЬКО рукоятки, USB HD.\n--height: только L1/вниз и R1/вверх, L2/R2 исключены из теста.\n--metal --check: офлайн-проверки экспериментального пакета (без контроллера).\nL1/R1/L2/R2 — ответ; Triangle — повтор; Circle/Esc — выход.\nСначала проверка ввода и обучение. Triangle начинает необязательный зачёт. Результаты сохраняются автоматически.");
            break;
        }
        bool height = argList.Remove("--height");
        bool finger = argList.Remove("--finger"), metal = argList.Remove("--metal") || height;
        if (!int.TryParse(GetOpt("--rounds", finger || metal ? "8" : "20"), out int rounds) || rounds < (height ? 2 : 4) || rounds > 100 || rounds % (height ? 2 : 4) != 0)
        { Log(height ? "--rounds: чётное число от 2 до 100" : "--rounds: кратное 4 число от 4 до 100 на каждую схему"); Environment.Exit(2); }
        if (!int.TryParse(GetOpt("--seed", Random.Shared.Next().ToString()), out int seed))
        { Log("--seed: требуется целое число"); Environment.Exit(2); }
        var path = HapticConfig.Locate(GetOpt("--haptics", "") is var opt && opt.Length > 0 ? opt : null);
        if (path == null) { Log("no haptics/haptics.json found"); Environment.Exit(2); }
        var cfg = HapticConfig.Load(path!, new[] { "lockpick-selected" }); // preserve the historical diagnostic comparison
        if (!cfg.Ok) { foreach (var error in cfg.Errors) Log(error); Environment.Exit(1); }
        if (metal) cfg.AddSupplementalPack("experiments/lockpick-metal.json");
        if (metal && argList.Remove("--check"))
        {
            foreach (var check in LpGuideTest.CheckMetal(cfg)) Log(check.name + ": " + (check.ok ? "OK" : "FAIL"));
            Environment.Exit(LpGuideTest.CheckMetal(cfg).All(c => c.ok) ? 0 : 1);
        }
        if (!cfg.Rules.Any(r => r.Id == "lp-guide-sel") || !cfg.Rules.Any(r => r.Id == "lp-guide-plate"))
        { Log("Нужен включённый пакет lockpick с подсказками v21."); Environment.Exit(2); }
        if (Console.IsInputRedirected) { Log("Запустите стенд в отдельном окне консоли."); Environment.Exit(2); }
        if (Process.GetProcessesByName("DSX").Length > 0) { Log("Закройте DSX перед тестом."); Environment.Exit(3); }
        var report = GetOpt("--report", Path.Combine(cfg.Root, "..", "reports", $"lp-guide-test-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json"));
        allowBt = true; pad = OpenOrDie(); resetTriggersOnCleanup = true;
        LpGuideTest.Run(cfg, pad, Log, rounds, seed, report, finger, metal, height);
        break;
    }

    case "bt-hd-test":
    {
        // bt-hd-test [--gain 1.0] [--no-prime]: HD haptics over Bluetooth (report 0x32, 3 kHz) with the game's rules - LOCKPICK.md §52
        var tpath = HapticConfig.Locate(null);
        if (tpath == null) { Log("no haptics/haptics.json found"); Environment.Exit(2); }
        var tcfg = HapticConfig.Load(tpath!);
        if (!tcfg.Ok) { foreach (var e in tcfg.Errors) Log("haptics ERROR: " + e); Environment.Exit(1); }
        if (Process.GetProcessesByName("DSX").Length > 0) { Log("DSX is running: close it (DsBridge must be the only writer to the pad)."); Environment.Exit(3); }
        allowBt = true; pad = OpenOrDie(); resetTriggersOnCleanup = true;
        double tgain = double.TryParse(GetOpt("--gain", "2.0"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var tg) ? tg : 1.0;
        try { var tl = Path.GetFullPath(Path.Combine(tcfg.Root, "..", "logs", $"bt-hd-test_{DateTime.Now:yyyyMMdd_HHmmss}.log")); Directory.CreateDirectory(Path.GetDirectoryName(tl)!); Console.SetOut(new TeeWriter(Console.Out, tl)); Log("console copy: " + tl); } catch { }
        BtHdTest.Run(tcfg, pad, Log, tgain, !argList.Contains("--no-prime"));
        break;
    }

    case "lp-bench":
    {
        // lp-bench [--haptics FILE]: Tactile Lockpicking 2.0 stand - navigation commands, moves, triggers, blind test,
        // left / right HD calibration (haptics/calibration.json). Same rules / HD / trigger output as `run`; no game needed.
        var bpath = HapticConfig.Locate(GetOpt("--haptics", "") is var hpb && hpb.Length > 0 ? hpb : null);
        if (bpath == null) { Log("no haptics/haptics.json found"); Environment.Exit(2); }
        // v41: the same rules as the game by default (pack 40: end stop, lights, "Сухой спуск", Master height cues); --legacy = the old
        // comparison stand without pack 40 (old guide rules lp-guide-sel / lp-guide-plate)
        var bcfg = argList.Contains("--legacy") ? HapticConfig.Load(bpath!, new[] { "lockpick-selected" }) : HapticConfig.Load(bpath!);
        if (!bcfg.Ok) { foreach (var e in bcfg.Errors) Log("haptics ERROR: " + e); Environment.Exit(1); }
        if (Process.GetProcessesByName("DSX").Length > 0) { Log("DSX is running: close it (DsBridge must be the only writer to the pad)."); Environment.Exit(3); }
        allowBt = true; pad = OpenOrDie(); resetTriggersOnCleanup = true;
        try { var bl = Path.GetFullPath(Path.Combine(bcfg.Root, "..", "logs", $"lp-bench_{DateTime.Now:yyyyMMdd_HHmmss}.log")); Directory.CreateDirectory(Path.GetDirectoryName(bl)!); Console.SetOut(new TeeWriter(Console.Out, bl)); Log("console copy: " + bl); } catch { }
        LpBench.Run(bcfg, pad, Log, GetOpt("--mod-dir", @"D:\Games\Gothic 1 Remake\G1R\Mods\G1R_DualSenseBridge"));
        break;
    }

    case "forge":
    {
        // forge [RECIPE] [--variants A,B] [--slots a,b] [--explore N --from B --seed S] [--check] [--out DIR] [--quiet]
        // Automatic HD effect pipeline (Haptics/Forge.cs): recipe -> synthesis (Haptic Designer DSP) -> EQ -> energy ->
        // normalisation -> checks -> bank WAVs + report. Default recipe: haptics/forge/lockpick.forge.json. No controller.
        var fpath = HapticConfig.Locate(GetOpt("--haptics", "") is var hpf && hpf.Length > 0 ? hpf : null);
        if (fpath == null) { Log("no haptics/haptics.json found"); Environment.Exit(2); }
        Console.OutputEncoding = Encoding.UTF8;
        var fo = new Forge.Options
        {
            Variants = GetOpt("--variants", "") is var fv && fv.Length > 0 ? fv.Split(',').ToList() : null,
            Slots = GetOpt("--slots", "") is var fs && fs.Length > 0 ? fs.Split(',').ToList() : null,
            Explore = int.Parse(GetOpt("--explore", "0")), ExploreFrom = GetOpt("--from", "") is var ff && ff.Length > 0 ? ff : null,
            Seed = int.Parse(GetOpt("--seed", "1")), OutDir = GetOpt("--out", "") is var fd && fd.Length > 0 ? fd : null,
            CheckOnly = argList.Remove("--check"), Quiet = argList.Remove("--quiet"), Partial = argList.Remove("--partial"),
        };
        fo.Recipe = argList.Count > 1 && !argList[1].StartsWith("--") ? argList[1] : Path.Combine(Path.GetDirectoryName(fpath!)!, "forge", "lockpick.forge.json");
        if (!File.Exists(fo.Recipe)) { Log("recipe not found: " + fo.Recipe); Environment.Exit(2); }
        var (fok, ftext, _) = Forge.Run(fo);
        Console.Write(ftext);
        Environment.Exit(fok ? 0 : 1);
        break;
    }

    case "signal-check":
    {
        // signal-check FILE|DIR [--report FILE.json] [--min-bt-corr .5]: real render metrics + Bluetooth simulation.
        var output = GetOpt("--report", "");
        double threshold = double.Parse(GetOpt("--min-bt-corr", "0.5"), System.Globalization.CultureInfo.InvariantCulture);
        if (argList.Count < 2) { Log("usage: signal-check FILE|DIR [--report FILE.json]"); Environment.Exit(2); }
        string input = argList[1];
        var files = Directory.Exists(input) ? Directory.GetFiles(input, "*.wav") : new[] { input };
        var result = SignalCheck.Run(files, threshold);
        if (output.Length > 0) File.WriteAllText(output, result.report);
        Console.WriteLine(result.report);
        Environment.Exit(result.ok ? 0 : 1);
        break;
    }

    case "sound-scan":
    {
        // sound-scan <dir|file.wav ...> [--out file.json]: analyse game sounds (transients, envelope, spectrum, duration) and
        // suggest a conversion method (impact | click | rattle | scrape | friction | texture). Read-only, no controller.
        Console.OutputEncoding = Encoding.UTF8;
        var outJ = GetOpt("--out", "sounds.features.json");
        var inputs = argList.Skip(1).ToList();
        var files = inputs.SelectMany(p => Directory.Exists(p) ? Directory.GetFiles(p, "*.wav") : File.Exists(p) ? new[] { p } : Array.Empty<string>()).ToList();
        if (files.Count == 0) { Log("usage: sound-scan <dir|wav ...> [--out file.json]"); Environment.Exit(2); }
        Log(AudioHaptics.Scan(files, outJ));
        break;
    }

    case "hd-compare":
    {
        // hd-compare [--bank lp]: feel the forge variants on the pad through the real rules (same runtime / HD engine as `run`),
        // switch variants instantly, A/B/C round of the last scene, save the choice to haptics/bank/selection.json.
        var cpath = HapticConfig.Locate(GetOpt("--haptics", "") is var hpc && hpc.Length > 0 ? hpc : null);
        if (cpath == null) { Log("no haptics/haptics.json found"); Environment.Exit(2); }
        var ccfg = HapticConfig.Load(cpath!);
        if (!ccfg.Ok) { foreach (var e in ccfg.Errors) Log("haptics ERROR: " + e); Environment.Exit(1); }
        if (Process.GetProcessesByName("DSX").Length > 0) { Log("DSX is running: close it (DsBridge must be the only writer to the pad)."); Environment.Exit(3); }
        Console.OutputEncoding = Encoding.UTF8;
        allowBt = true; pad = OpenOrDie(); resetTriggersOnCleanup = true;
        HdCompare.Run(ccfg, pad, Log, GetOpt("--bank", "lp"));
        break;
    }

    case "replay":
    {
        // replay JOURNAL [--wav out.wav] [--report out.json] [--quiet] [--live] [--disable-pack X] [--only-pack X]
        // Offline: the same rules + mixer as the game, rendered into a WAV + report (no controller, no game).
        // --live: plays the journal in real time to the controller (HD on USB, motors otherwise).
        if (argList.Count < 2) { Log("usage: replay <journal.jsonl> [--wav out.wav] [--report out.json] [--quiet] [--live]"); Environment.Exit(2); }
        var opts = new Replay.Options { Wav = GetOpt("--wav", "") is var wv && wv.Length > 0 ? wv : null, ReportJson = GetOpt("--report", "") is var rj && rj.Length > 0 ? rj : null, Quiet = argList.Remove("--quiet") };
        bool live = argList.Remove("--live");
        var only = GetOpt("--only-pack", "");
        var path = HapticConfig.Locate(GetOpt("--haptics", "") is var hp1 && hp1.Length > 0 ? hp1 : null);
        if (path == null) { Log("no haptics/haptics.json found"); Environment.Exit(2); }
        var cfg = HapticConfig.Load(path!, GetOpt("--disable-pack", "").Split(',', StringSplitOptions.RemoveEmptyEntries), only.Length > 0 ? only.Split(',') : null);
        if (!cfg.Ok) { foreach (var e in cfg.Errors) Log("haptics ERROR: " + e); Environment.Exit(1); }
        var problems = new List<string>();
        var evs = Journal.Load(argList[1], problems);
        foreach (var pr in problems.Take(10)) Log("journal: " + pr);
        if (!live) { Console.Write(Replay.Run(cfg, evs, opts, problems)); break; }

        allowBt = true; pad = OpenOrDie(); resetTriggersOnCleanup = true;   // lockpicking rules set the adaptive triggers
        var lrumble = new RumbleMixer(pad);
        var lhd = HdEngine.TryStart(pad, Log);
        var lout = new HdOutLive { E = lhd };
        var lrt = new HapticRuntime(cfg, lout, new MotorOutLive(lrumble)) { Log = Log, Pad = new PadOutLive(() => pad) { Log = Log } };
        var sw0 = Stopwatch.StartNew();
        foreach (var (t, ev) in evs)
        {
            while (sw0.ElapsedMilliseconds < t) { lrumble.Update(); Thread.Sleep(2); }
            var decs = lrt.Handle(ev, DateTime.UtcNow);
            Log($"{t,7} ms #{ev.Seq} {ev.Type}:{ev.Phase} -> {(decs.Count == 0 ? "(no rule)" : string.Join("; ", decs))}");
        }
        var tEnd = sw0.ElapsedMilliseconds + 2500;
        while (sw0.ElapsedMilliseconds < tEnd) { lrumble.Update(); Thread.Sleep(5); }
        if (lhd != null) Log("HD " + lhd.Mixer.StatsLine() + $" stalls={lhd.Stalls}");
        lhd?.Dispose();
        break;
    }

    case "regress":
    {
        // regress [--update] [--journals DIR] [--baseline DIR]: replays every journal, compares metrics with baselines
        var path = HapticConfig.Locate(GetOpt("--haptics", "") is var hp3 && hp3.Length > 0 ? hp3 : null);
        if (path == null) { Log("no haptics/haptics.json found"); Environment.Exit(2); }
        var cfg = HapticConfig.Load(path!);
        if (!cfg.Ok) { foreach (var e in cfg.Errors) Log("haptics ERROR: " + e); Environment.Exit(1); }
        var root = Path.GetDirectoryName(Path.GetDirectoryName(path!))!;
        bool upd = argList.Remove("--update");
        var (rok, rtext) = Regress.Run(cfg, GetOpt("--journals", Path.Combine(root, "journals")), GetOpt("--baseline", Path.Combine(root, "reports", "baseline")), upd);
        Console.Write(rtext);
        Environment.Exit(rok ? 0 : 1);
        break;
    }

    case "approvals":
    {
        // approvals [--check]                     every recorded pack renders as approved (handoff check)
        // approvals --record PACK approved|retest "note"   store the current signature (after a game test / when marking a retest)
        var path = HapticConfig.Locate(GetOpt("--haptics", "") is var hp5 && hp5.Length > 0 ? hp5 : null);
        if (path == null) { Log("no haptics/haptics.json found"); Environment.Exit(2); }
        var cfg = HapticConfig.Load(path!);
        if (!cfg.Ok) { foreach (var e in cfg.Errors) Log("haptics ERROR: " + e); Environment.Exit(1); }
        int ri = argList.IndexOf("--record");
        if (ri >= 0)
        {
            if (ri + 3 >= argList.Count) { Log("usage: approvals --record PACK approved|retest \"note\""); Environment.Exit(2); }
            Console.WriteLine(Approvals.Record(cfg, argList[ri + 1], argList[ri + 2], argList[ri + 3]));
            break;
        }
        var (aok, atext) = Approvals.Check(cfg);
        Console.Write(atext);
        Environment.Exit(aok ? 0 : 1);
        break;
    }

    case "levels":
    {
        // levels [--out reports/levels.json]: replays every real journal (journals/*.jsonl without synthetic_*, logs/play_*.jsonl)
        // and measures the HD voices each rule asked for; tools/level_map.py turns it into the role / hierarchy report
        var path = HapticConfig.Locate(GetOpt("--haptics", "") is var hp4 && hp4.Length > 0 ? hp4 : null);
        if (path == null) { Log("no haptics/haptics.json found"); Environment.Exit(2); }
        var cfg = HapticConfig.Load(path!);
        if (!cfg.Ok) { foreach (var e in cfg.Errors) Log("haptics ERROR: " + e); Environment.Exit(1); }
        var root = Path.GetDirectoryName(Path.GetDirectoryName(path!))!;
        var files = Directory.GetFiles(Path.Combine(root, "journals"), "*.jsonl").Where(f => !Path.GetFileName(f).StartsWith("synthetic"))
            .Concat(Directory.Exists(Path.Combine(root, "logs")) ? Directory.GetFiles(Path.Combine(root, "logs"), "play_*.jsonl") : Array.Empty<string>())
            .OrderBy(x => x).ToList();
        var outPath = GetOpt("--out", Path.Combine(root, "reports", "levels.json"));
        var (nj, hrs, json) = LevelMap.Run(cfg, files);
        File.WriteAllText(outPath, json);
        Log($"levels: {nj} journals, {hrs:F1} h -> {outPath}");
        break;
    }

    case "registry-status":
    {
        // 7 verifiable stages per event (discovered .. ready) from candidates, registries and journals
        var path = HapticConfig.Locate(GetOpt("--haptics", "") is var hp4 && hp4.Length > 0 ? hp4 : null);
        if (path == null) { Log("no haptics/haptics.json found"); Environment.Exit(2); }
        var cfg = HapticConfig.Load(path!);
        var root = Path.GetDirectoryName(Path.GetDirectoryName(path!))!;
        var (md, json, summary) = RegistryStatus.Build(cfg, GetOpt("--candidates", Path.Combine(root, "..", "research", "hooks", "candidates.json")), GetOpt("--journals", Path.Combine(root, "journals")));
        Directory.CreateDirectory(Path.Combine(root, "reports"));
        File.WriteAllText(Path.Combine(root, "reports", "registry_status.md"), md);
        File.WriteAllText(Path.Combine(root, "reports", "registry_status.json"), json);
        Console.WriteLine(summary);
        Log("-> " + Path.Combine(root, "reports", "registry_status.md"));
        break;
    }

    case "coverage":
    {
        // Markdown coverage report: registry x rules x status + analyzer candidates (research/hooks/candidates.json).
        var path = HapticConfig.Locate(GetOpt("--haptics", "") is var hp2 && hp2.Length > 0 ? hp2 : null);
        if (path == null) { Log("no haptics/haptics.json found"); Environment.Exit(2); }
        var cfg = HapticConfig.Load(path!);
        var root = Path.GetDirectoryName(Path.GetDirectoryName(path!))!;          // G1R-DualSense
        var cand = GetOpt("--candidates", Path.Combine(root, "..", "research", "hooks", "candidates.json"));
        var outp = GetOpt("--out", Path.Combine(root, "reports", "coverage.md"));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outp))!);
        File.WriteAllText(outp, Coverage.Build(cfg, cand));
        Log($"coverage -> {Path.GetFullPath(outp)}");
        break;
    }

    case "monitor":
    {
        // Game Event Monitor, diagnostics only: decodes every event (v2 ring or v1 marker), never touches the controller.
        int seconds = int.Parse(GetOpt("--seconds", "0"));
        using var diag = new DiagLog(GetOpt("--log", Path.Combine("..", "logs", "monitor.log")));
        diag.Write($"Game Event Monitor: watching '{processName}' window title (no controller access).");
        var w = new GameWatcher(processName);
        var mon = new EventMonitor { Diag = diag.Write };
        var states = new StateTracker { Diag = diag.Write };
        string monRecord = GetOpt("--record", "");
        using var monJournal = monRecord.Length > 0 ? new JournalWriter(monRecord) : null;
        bool was = false; var nextStats = DateTime.UtcNow.AddSeconds(30);
        var until = seconds > 0 ? DateTime.UtcNow.AddSeconds(seconds) : DateTime.MaxValue;
        void Stats() => diag.Write($"STATS: received={mon.Received} gaps={mon.Gaps} lost={mon.Lost} session_resets={mon.Resets} malformed={mon.Malformed} active=[{string.Join(",", states.Active)}]");
        while (DateTime.UtcNow < until)
        {
            var t = w.Poll(out bool running);
            if (running != was)
            {
                diag.Write(running ? $"process found, title='{t}'" : "process not running");
                if (!running) states.InterruptAll("game process lost");
                mon.Reset(); was = running;
            }
            if (running && t != null)
            {
                var now = DateTime.UtcNow;
                foreach (var ev in mon.Feed(t, now))
                {
                    diag.Write("EVENT " + ev);
                    monJournal?.Write(ev);
                    if (ev.Type == "session" && ev.Phase == "start") states.InterruptAll("new game session");
                    states.Apply(ev, now);
                }
                states.ExpireStale(now);
            }
            if (DateTime.UtcNow >= nextStats) { Stats(); nextStats = DateTime.UtcNow.AddSeconds(30); }
            Thread.Sleep(5);
        }
        Stats();
        break;
    }

    case "run":
    {
        // Output path (default, user decision 13.11.2026): DsBridge -> physical DualSense directly
        // (Bluetooth report 0x31 with CRC, or USB 0x02). DSX is not used; DsBridge is the only writer.
        // Legacy path: --target virtual (DSX virtual DualSense, needs DSX running).
        // Effects (game events from the mod, via the window title):
        //   fall (take-off)  -> short pulse, RIGHT motor
        //   land (landing)   -> short light pulse, BOTH motors
        //   menu             -> effects off until 'world'
        if (target == "any") target = "physical";
        if (target == "physical")
        {
            allowBt = true;
            if (Process.GetProcessesByName("DSX").Length > 0 && !argList.Remove("--allow-dsx"))
            {
                Log("DSX is running: two writers to one pad would conflict. Close DSX (or pass --allow-dsx / --target virtual).");
                Environment.Exit(3);
            }
        }
        int jumpMs = int.Parse(GetOpt("--jump-ms", "55"));
        byte jumpAmp = byte.Parse(GetOpt("--jump-amp", "110"));
        int landMs = int.Parse(GetOpt("--land-ms", "130"));
        byte landAmp = byte.Parse(GetOpt("--land-amp", "150"));
        pad = OpenOrDie();
        resetTriggersOnCleanup = target != "virtual";
        RumbleMixer.Trace = argList.Remove("--trace");
        RumbleMixer.RefreshMs = int.Parse(GetOpt("--refresh-ms", "15"));
        var rumble = new RumbleMixer(pad);
        // ---- Haptic pipeline (default): rules from haptics/haptics.json; --legacy-effects = the hard-coded table below ----
        bool legacy = argList.Remove("--legacy-effects");
        string hapticsArg = GetOpt("--haptics", "");
        var disablePacks = GetOpt("--disable-pack", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var onlyPacksArg = GetOpt("--only-pack", "");
        string recordPath = GetOpt("--record", "");
        HapticConfig? hcfg = null;
        if (!legacy)
        {
            var cfgPath = HapticConfig.Locate(hapticsArg.Length > 0 ? hapticsArg : null);
            if (cfgPath == null) { Log("haptics: no haptics/haptics.json found -> legacy effect table"); legacy = true; }
            else
            {
                hcfg = HapticConfig.Load(cfgPath, disablePacks, onlyPacksArg.Length > 0 ? onlyPacksArg.Split(',', StringSplitOptions.TrimEntries) : null);
                foreach (var wn in hcfg.Warnings) Log("haptics warning: " + wn);
                if (!hcfg.Ok) { foreach (var er in hcfg.Errors) Log("haptics ERROR: " + er); Log("haptics: config has errors -> legacy effect table (fix and restart; see `rules-check`)"); legacy = true; hcfg = null; }
                else Log($"haptics: {cfgPath}; packs ON: {string.Join(", ", hcfg.Packs.Where(x => x.Enabled).Select(x => $"{x.Name}({x.RuleCount})"))}" +
                         (hcfg.Packs.Any(x => !x.Enabled) ? $"; off: {string.Join(", ", hcfg.Packs.Where(x => !x.Enabled).Select(x => x.Name))}" : "") + $"; {hcfg.Rules.Count} rules");
            }
        }
        // test checklist in the game (--tests PLAN, e.g. Start-PilotTest.ps1): tasks done when their effect really played; SHARE = show / hide.
        // Without a plan the list file is still written once with show=0, so a list from an earlier test never stays on screen.
        string testsPlan = GetOpt("--tests", "");
        string runModDir = GetOpt("--mod-dir", @"D:\Games\Gothic 1 Remake\G1R\Mods\G1R_DualSenseBridge");
        string testsReport = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(HapticConfig.Locate(null) ?? Path.Combine(Directory.GetCurrentDirectory(), "x", "y"))!)!, "reports", "tests",
                                          (recordPath.Length > 0 ? Path.GetFileNameWithoutExtension(recordPath) : "last") + ".json");
        try { testHud = TestHud.Load(testsPlan.Length > 0 ? Path.GetFullPath(testsPlan) : null, runModDir, Path.GetFullPath(testsReport), Log); }
        catch (Exception ex) { Log("test HUD: plan not loaded (" + ex.Message + ")"); testHud = TestHud.Load(null, runModDir, Path.GetFullPath(testsReport), Log); }
        testHud.Flush(DateTime.UtcNow, true);
        bool shareDown = false;
        var touchpad = argList.Remove("--no-gestures") ? null : new TouchpadControl(runModDir, processName, Log);
        // L2 / R2 from the pad's input reports -> "pad:update l2=0|1,r2=0|1" on change (hysteresis 64 / 24). Used only through game
        // state (e.g. derived.json: bow draw = R2 while the game says the bow is ready) - never as a button effect by itself.
        var padEvents = new System.Collections.Concurrent.ConcurrentQueue<GameEvent>();
        int trigState = 0; CancellationTokenSource? inputCts = null;
        int lastL2Raw = 0, lastR2Raw = 0;   // latest trigger positions (diagnostics: where the mod fired a lockpick move)
        void StartPadInput(DualSense p)
        {
            inputCts?.Cancel(); inputCts = new CancellationTokenSource(); trigState = 0;
            var readerCts = inputCts;
            touchpad?.Reset();
            try
            {
                p.StartInputReader((buf, n) =>
                {
                    if (readerCts.IsCancellationRequested) return;
                    touchpad?.OnReport(buf, n, p.IsBluetooth);
                    if (testHud is { Active: true } && DualSense.TryGetCreate(buf, n, p.IsBluetooth, out var share))
                    {
                        if (share && !shareDown) testHud.Toggle();   // test tool: SHARE shows / hides the checklist
                        shareDown = share;
                    }
                    if (!DualSense.TryGetTriggers(buf, n, p.IsBluetooth, out var l2, out var r2)) return;
                    lastL2Raw = l2; lastR2Raw = r2;
                    int st = trigState;
                    bool L = (st & 1) != 0 ? l2 > 24 : l2 >= 64, R = (st & 2) != 0 ? r2 > 24 : r2 >= 64;
                    int ns = (L ? 1 : 0) | (R ? 2 : 0);
                    if (ns == st) return;
                    trigState = ns;
                    var pe = EventMonitor.ParseRecord($"0:pad:update:t={Environment.TickCount64},src=hid_input,l2={(L ? 1 : 0)},r2={(R ? 1 : 0)}");
                    if (pe != null) padEvents.Enqueue(pe);
                }, inputCts.Token);
            }
            catch (Exception ex) { Log("pad input reader: " + ex.Message); }
        }
        StartPadInput(pad);
        // v48 (user 21.11.2026): a blow into the pad microphone = the scavenger whistle (the mod activates the game's own
        // call-mount ability; without the whistle the game refuses). USB only; --no-mic turns it off.
        bool micOff = argList.Remove("--no-mic");
        MicBlow? mic = null;
        // v50 (game 21.11: after a cable replug the pad's audio endpoint came back later than the HID device, the single
        // try found "no pad microphone" and blowing stayed off until DsBridge restarted): retried like HD - every 0.5 s
        // for 15 s after a (re)connect, then every 3 s; a capture that stopped (device lost) is reopened too.
        DateTime nextMicTry = DateTime.MaxValue; bool micMissLogged = false;
        void StartMic(DualSense p, bool retry = false)
        {
            mic?.Dispose(); mic = null;
            if (micOff || touchpad == null || p.IsBluetooth) { nextMicTry = DateTime.MaxValue; return; }
            if (!retry) micMissLogged = false;
            var m = new MicBlow();
            m.Blown += () => touchpad.Request("whistle");
            if (m.Start(p.DevicePath, msg => { if (msg.StartsWith("mic: no pad microphone")) { if (micMissLogged) return; micMissLogged = true; } Log(msg); }))
            { mic = m; nextMicTry = DateTime.MaxValue; }
            else { m.Dispose(); nextMicTry = DateTime.UtcNow.AddSeconds(0.5); }
        }
        StartMic(pad);
        var hdOut = new HdOutLive();
        // adaptive triggers through the same pad object (no second writer); with --target virtual DSX owns the triggers
        var padOut = target != "virtual" ? new PadOutLive(() => rumble.Lost ? null : pad) { Log = Log } : null;
        var lightOut = target != "virtual" ? new LightOutLive(() => rumble.Lost ? null : pad, restore) { Log = Log, Touched = () => lightTouched = true } : null;
        HapticRuntime? hrt = hcfg != null ? new HapticRuntime(hcfg, hdOut, new MotorOutLive(rumble), new VariationState(Environment.TickCount)) { Log = Log, Pad = padOut, LightOut = lightOut } : null;
        using var journal = recordPath.Length > 0 ? new JournalWriter(recordPath) : null;
        if (journal != null)
        {
            // the console window is not readable by the agents (a separate desktop window): mirror it next to the journal
            var consolePath = Path.ChangeExtension(Path.GetFullPath(recordPath), ".log");
            try { Console.SetOut(new TeeWriter(Console.Out, consolePath)); } catch (Exception ex) { Log("console log: " + ex.Message); }
            Log($"recording events to {Path.GetFullPath(recordPath)}; console copy: {consolePath}");
        }
        bool hdAllowed = !argList.Remove("--no-hd");
        // --hd-mode all (default): every effect through HD on USB; water: only the water effects (build 2026-11-13_water-hd)
        string hdMode = GetOpt("--hd-mode", "all").ToLowerInvariant();
        // default (user, 13.11.2026): take-off, dodge/roll and attacks stay on rumble emulation; landing, hit, water in HD.
        // "--hd-except none" puts everything in HD.
        var hdExceptArg = GetOpt("--hd-except", "fall,dodge,roll,attack,aleft,aright,atop,abottom,akick");
        if (hdExceptArg == "none") hdExceptArg = "";
        rumble.HdExcept = hdExceptArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        if (int.TryParse(GetOpt("--bt-rearm-ms", "40"), out var btRearm)) HdEngine.BtRearmMs = btRearm;
        if (double.TryParse(GetOpt("--bt-gain", "2.0"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var btGain)) HdEngine.BtGain = btGain;   // BT HD: re-prime period (0 = at sound start only)
        HdEngine? hd = hdAllowed ? HdEngine.TryStart(pad, Log) : null;
        void BindHd()
        {
            hdOut.E = hd;
            if (hd != null && hcfg != null) { Replay.ApplyLimits(hd.Mixer, hcfg.Limits); hd.StallMs = hcfg.Limits.StallMs; }
            rumble.Hd = legacy && hdMode == "all" ? hd : null;   // pipeline mode routes HD/motors itself
        }
        BindHd();
        if (!hdAllowed) Log("HD haptics: disabled (--no-hd) -> rumble emulation");
        else if (legacy) Log($"LEGACY effect table. HD mode: {hdMode}" + (hdMode == "all" ? " (all effects through HD on USB)" : " (only water effects through HD)") +
                 (rumble.HdExcept.Count > 0 ? $"; kept on rumble emulation: {string.Join(",", rumble.HdExcept)}" : ""));
        else Log("effects: haptic pipeline (rules decide HD / motors per effect; --legacy-effects for the old table)");
        var w = new GameWatcher(processName);
        bool diagOn = argList.Remove("--diag");
        DiagLog? diag = diagOn ? new DiagLog(GetOpt("--log", Path.Combine("..", "logs", "run_diag.log"))) : null;
        var parser = new EventMonitor { Diag = diag != null ? diag.Write : null };
        var states = new StateTracker { Diag = diag != null ? diag.Write : null };
        hcfg?.ConfigureTracker(states);
        bool wasRunning = false;
        // --exit-with-game (Play.ps1, user 17.11.2026): leave with the game - cleanup (triggers, motors, lightbar) and exit
        // when the game closes; also when no game appears within 3 minutes of start.
        bool exitWithGame = argList.Remove("--exit-with-game"), sawGame = false;
        DateTime runStarted = DateTime.UtcNow;
        DateTime? fallAt = null; DateTime padChangedAt = DateTime.MinValue; DateTime nextReopen = DateTime.MinValue, nextPresence = DateTime.MinValue, nextHdTry = DateTime.UtcNow.AddSeconds(3), nextStats = DateTime.UtcNow.AddSeconds(60);
        Log($"Companion running ({target}): take-off -> right {jumpAmp}/255 {jumpMs} ms; landing -> both {landAmp}/255 {landMs} ms. Ctrl+C to stop.");
        // watchdog (BT HD diagnostics, 15.11.2026): a HID write that hangs > 1 s or a main loop that stops > 2 s is reported
        long loopBeat = Environment.TickCount64;
        new Thread(() =>
        {
            long lastReport = 0;
            while (true)
            {
                Thread.Sleep(500);
                try
                {
                    var p = pad; long now = Environment.TickCount64;
                    int id = p.WriteInProgressId;
                    double wms = id != 0 ? (Stopwatch.GetTimestamp() - p.WriteStartTicks) * 1000.0 / Stopwatch.Frequency : 0;
                    long stall = now - Interlocked.Read(ref loopBeat);
                    if ((wms > 1000 || stall > 2000) && now - lastReport > 5000)
                    {
                        lastReport = now;
                        Log($"WATCHDOG: main loop idle {stall} ms; HID write {(id != 0 ? $"of report 0x{id:X2} running {wms:F0} ms" : "none running")}; writes {p.Writes}, slowest {p.SlowestWriteMs:F0} ms; HD {(hd == null ? "off" : hd.Bluetooth ? $"BT reports {hd.BtReports}, fails {hd.BtWriteFails}, primes {hd.BtPrimes}" : "USB")}");
                    }
                }
                catch { }
            }
        }) { IsBackground = true, Name = "watchdog" }.Start();
        while (true)
        {
            Interlocked.Exchange(ref loopBeat, Environment.TickCount64);
            var title = w.Poll(out bool running);
            if (running != wasRunning)
            {
                Log(running ? $"Game detected, title='{title}'" : "Game not running / closed");
                volatile_state.GameRunning = running; volatile_state.InWorld = false;
                if (!running) { rumble.StopAll(); hd?.StopAll(); hrt?.StopAll("game process lost"); states.InterruptAll("game process lost"); Log("rumble stopped (game lost)"); }
                parser.Reset(); wasRunning = running;
                if (running) sawGame = true;
                else if (exitWithGame && sawGame) { Cleanup("game closed"); Log("game closed -> DsBridge exits (--exit-with-game)"); Environment.Exit(0); }
            }
            if (exitWithGame && !sawGame && DateTime.UtcNow - runStarted > TimeSpan.FromMinutes(3))
            { Cleanup("no game"); Log("no game within 3 min -> DsBridge exits (--exit-with-game)"); Environment.Exit(3); }
            if (running && title != null)
            {
                foreach (var ev in parser.Feed(title, DateTime.UtcNow))
                {
                    diag?.Write("EVENT " + ev);
                    if (ev.Type == "session" && ev.Phase == "start") states.InterruptAll("new game session");
                    states.Apply(ev, DateTime.UtcNow);
                    journal?.Write(ev);
                    if (ev.Type == "lp" && ev.Phase == "start" && (ev.GetInt("ver") ?? 0) < 22)
                        Log("WARNING: the game runs OLD Tactile Lockpicking scripts (lp:start ver=" + (ev.Get("ver") ?? "none") + ", need 22+): restart the game so it compiles the installed mod - until then the new hints and the insert timing are missing");
                    if (ev.Type == "lp" && ev.Get("ph") == "press")
                        Log($"lockpick press tx {ev.Get("tx")} {ev.Get("dir")}: trigger position now L2 {lastL2Raw}/255 ({lastL2Raw / 255.0:0.00}), R2 {lastR2Raw}/255 ({lastR2Raw / 255.0:0.00})");
                    if (hrt != null)
                    {
                        var decs = hrt.Handle(ev, DateTime.UtcNow);
                        testHud?.Observe(hrt.LastEvent ?? ev, decs, DateTime.UtcNow);
                        var act = hrt.LastEvent?.Get("act");
                        if (decs.Count > 0) Log($"event {ev.Type}:{ev.Phase} #{ev.Seq}{(act != null ? " [" + act + "]" : "")} -> {string.Join("; ", decs)}");
                        continue;
                    }
                    rumble.CurrentKey = ev.EffectKey;
                    switch (ev.EffectKey)
                    {
                        case null: break;   // diagnostics-only event (no effect yet)
                        case "world": case "ready": volatile_state.InWorld = true; Log($"event '{ev.EffectKey}'"); break;
                        case "menu": volatile_state.InWorld = false; rumble.StopAll(); hd?.StopAll(); Log("event 'menu' -> effects paused"); break;
                        case "fall": volatile_state.InWorld = true; fallAt = DateTime.UtcNow; rumble.Pulse(right: jumpAmp, left: 0, jumpMs); Log("event 'fall' -> right pulse"); break;
                        case "land":
                        {
                            volatile_state.InWorld = true;
                            double air = fallAt is DateTime f ? (DateTime.UtcNow - f).TotalSeconds : 0.75;
                            fallAt = null;
                            var parts = ev.LegacyId.Split('-');
                            int dropCm = ev.GetInt("drop") ?? (parts.Length >= 3 && int.TryParse(parts[2], out var dcm) ? dcm : -1);
                            // effective height: net drop (take-off Z - landing Z) dominates, the jump arc adds 30 % -> a jump in place stays light
                            int? netCm = ev.GetInt("net");
                            int effCm = dropCm < 0 ? -1 : netCm is int nc ? (int)Math.Round(nc + 0.3 * Math.Max(0, dropCm - nc)) : dropCm;
                            if (rumble.Hd?.Ready == true && !rumble.HdExcept.Contains("land") && effCm >= 0)
                            {
                                var (ll, lr) = HdFx.Land(rumble.Hd.Rate, effCm);
                                rumble.Hd.Play(ll, lr);
                                Log($"event 'land' drop={dropCm}cm net={netCm?.ToString() ?? "?"}cm eff={effCm}cm -> HD thump{(effCm >= 250 ? " + low tail" : "")}");
                                break;
                            }
                            var (amp, ms) = effCm >= 0 ? RumbleMixer.LandingFromDrop(effCm) : RumbleMixer.LandingFromAirTime(air);
                            rumble.Pulse(right: amp, left: amp, ms);
                            bool heavy = effCm >= 0 ? effCm >= 250 : air >= 1.2;
                            if (heavy) rumble.Pattern((ms, 0, (byte)(amp * 2 / 3), ms)); // heavy fall: low rumble tail, as long as the hit
                            Log($"event 'land' drop={dropCm}cm net={netCm?.ToString() ?? "?"}cm eff={effCm}cm air={air:F2}s -> both {amp}/255 {ms} ms{(heavy ? " + tail" : "")}");
                            break;
                        }
                        // player actions (game state tags), each with its own pattern
                        case "attack": rumble.Pattern((0, 255, 0, 90)); Log("event 'attack' (no direction) -> right sharp buzz"); break;
                        // attack direction (Combat.Attack.*): each swing feels different
                        case "atop": rumble.Pattern((0, 70, 0, 40), (120, 255, 255, 120)); Log("event 'atop' -> overhead: light windup, heavy chop"); break;
                        case "abottom": rumble.Pattern((0, 0, 200, 60), (60, 160, 120, 90)); Log("event 'abottom' -> low upswing"); break;
                        case "aleft": rumble.Pattern((0, 0, 230, 110)); Log("event 'aleft' -> left motor sweep"); break;
                        case "aright": rumble.Pattern((0, 230, 0, 110)); Log("event 'aright' -> right motor sweep"); break;
                        case "akick": rumble.Pattern((0, 255, 255, 60)); Log("event 'akick' -> short hard kick"); break;
                        case "dodge": rumble.Pattern((0, 0, 220, 70), (150, 0, 220, 70)); Log("event 'dodge' -> left double thump"); break;
                        case "roll": rumble.Pattern((0, 0, 110, 100), (100, 0, 170, 100), (200, 0, 240, 120)); Log("event 'roll' -> rising left rumble"); break;
                        // water (State.Swimming / State.Movement.Diving)
                        // water: HD haptics on USB (audio to the actuators), rumble-emulation fallback on Bluetooth
                        case "dive":
                            if (hd?.Ready == true) { var (dl, dr) = HdFx.Dive(hd.Rate); hd.Play(dl, dr); Log("event 'dive' -> HD: water swell rolling left->right"); }
                            else { rumble.Pattern(RumbleMixer.DiveWave()); Log("event 'dive' -> sinking wave (left leads, right follows)"); }
                            break;
                        case "surface":
                            if (hd?.Ready == true) { var (sl, sr) = HdFx.Surface(hd.Rate); hd.Play(sl, sr); Log("event 'surface' -> HD: rising swell + breath"); }
                            else { rumble.Pattern(RumbleMixer.SurfaceWave()); Log("event 'surface' -> rising wave (right leads) + breath"); }
                            break;
                        case "splash":
                        {
                            int sdrop = ev.GetInt("drop") ?? 0;
                            if (hd?.Ready == true) { var (pl, pr) = HdFx.Splash(hd.Rate, sdrop); hd.Play(pl, pr); Log($"event 'splash' drop={sdrop}cm -> HD: slap + swell"); break; }
                            var (samp, sms) = RumbleMixer.LandingFromDrop(sdrop);
                            rumble.Pattern(((0, samp, samp, 70)));
                            rumble.Pattern(RumbleMixer.DiveWave(0.6, 60));
                            Log($"event 'splash' drop={sdrop}cm -> slap {samp}/255 + soft wave");
                            break;
                        }
                        case "wade":
                            if (hd?.Ready == true) { var (wl, wr) = HdFx.Wade(hd.Rate); hd.Play(wl, wr); Log("event 'wade' -> HD: gentle ripple"); }
                            else { rumble.Pattern(RumbleMixer.Wave(500, 0, 70, 120, 50)); Log("event 'wade' -> gentle ripple"); }
                            break;
                        case "hit": rumble.Pattern((0, 255, 255, 160), (160, 0, 180, 220)); Log("event 'hit' -> strong hit + tail"); break;
                        default: Log($"event '{ev.EffectKey}'"); break;
                    }
                }
                if (parser.BaselineMode != null)
                {
                    volatile_state.InWorld = parser.BaselineMode != "menu";
                    Log($"baseline from title: '{parser.BaselineMode}' -> {(volatile_state.InWorld ? "world" : "menu")}");
                    parser.BaselineMode = null;
                }
                states.ExpireStale(DateTime.UtcNow);
            }
            while (padEvents.TryDequeue(out var pe))
            {
                journal?.Write(pe);
                diag?.Write("EVENT " + pe);
                if (hrt != null && running)
                {
                    var pd = hrt.Handle(pe, DateTime.UtcNow);
                    testHud?.Observe(pe, pd, DateTime.UtcNow);
                    if (pd.Count > 0) Log($"pad l2={pe.Get("l2")} r2={pe.Get("r2")} -> {string.Join("; ", pd)}");
                }
            }
            touchpad?.Tick();
            rumble.Update();
            hrt?.Tick(DateTime.UtcNow);
            padOut?.Refresh(DateTime.UtcNow);
            lightOut?.Refresh(DateTime.UtcNow);
            testHud?.Flush(DateTime.UtcNow);
            if (!rumble.Lost && DateTime.UtcNow >= nextPresence)
            {
                nextPresence = DateTime.UtcNow.AddSeconds(1);
                if (!pad.StillPresent()) { padChangedAt = DateTime.UtcNow; rumble.MarkLost(); hd?.Dispose(); hd = null; BindHd(); hrt?.DeviceLost("controller lost"); Log("controller interface gone (unplugged, BT dropped or switched to cable) -> reopening"); }
                else if (hd != null && hd.Failed) { hd.Dispose(); hd = null; BindHd(); Log("HD haptics: stream failed -> rumble emulation until the controller reconnects"); }
            }
            if (diag != null && DateTime.UtcNow >= nextStats)
            {
                nextStats = DateTime.UtcNow.AddSeconds(60);
                diag.Write($"HAPTICS STATS: events={parser.Received} gaps={parser.Gaps} lost={parser.Lost}; " +
                           (hd != null ? $"HD {hd.Mixer.StatsLine()} stalls={hd.Stalls} arm_reports={hd.ArmReports}" : "HD off") +
                           (hrt != null ? $"; rule errors={hrt.Errors}; no-rule={string.Join(",", hrt.Unmatched.Select(kv => kv.Key + "=" + kv.Value))}" : ""));
            }
            if (hdAllowed && hd == null && !rumble.Lost && !pad.IsBluetooth && DateTime.UtcNow >= nextHdTry)
            {
                // the pad's audio device shows up a moment after its HID interface: retry quietly, log only success
                nextHdTry = DateTime.UtcNow.AddSeconds((DateTime.UtcNow - padChangedAt).TotalSeconds < 15 ? 0.5 : 3);   // just reconnected: the audio device follows in ~1-3 s
                hd = HdEngine.TryStart(pad, m => { if (m.Contains(": ON")) Log(m); }); BindHd();
            }
            if (!rumble.Lost && (mic == null || !mic.Alive) && nextMicTry != DateTime.MaxValue && DateTime.UtcNow >= nextMicTry)
            {
                StartMic(pad, retry: true);
                if (mic == null) nextMicTry = DateTime.UtcNow.AddSeconds((DateTime.UtcNow - padChangedAt).TotalSeconds < 15 ? 0.5 : 3);
            }
            else if (mic != null && !mic.Alive && nextMicTry == DateTime.MaxValue) nextMicTry = DateTime.UtcNow.AddSeconds(1);
            if (rumble.Lost && DateTime.UtcNow >= nextReopen)
            {
                nextReopen = DateTime.UtcNow.AddSeconds((DateTime.UtcNow - padChangedAt).TotalSeconds < 15 ? 0.5 : 2);   // first 15 s after a loss: try often (game 19.11: ~14 s to HD)
                try { pad.Dispose(); } catch { }
                var np = DualSense.Open(allowBt, method, out var msg, target);
                if (np != null)
                {
                    pad = np; padChangedAt = DateTime.UtcNow; rumble.SetPad(np); padOut?.ForceOff(); Log("controller reconnected: " + msg + (np.IsBluetooth ? " (Bluetooth)" : " (USB)"));
                    StartPadInput(np);
                    StartMic(np);
                    hd?.Dispose(); hd = hdAllowed ? HdEngine.TryStart(np, Log) : null; BindHd();
                }
            }
            Thread.Sleep(5);
        }
    }

    default:
        Console.WriteLine("Usage: DsBridge list|selftest|test-trigger|test-rumble|test-light|off|watch|listen|monitor|run [--diag] [--events a,b] [--allow-bt] [--method write|setreport] [--process NAME] [--ms N] [--light] [--restore-light R,G,B] [--dump]");
        break;
}

string GetOpt(string name, string def)
{
    int i = argList.IndexOf(name);
    if (i >= 0 && i + 1 < argList.Count) { var v = argList[i + 1]; argList.RemoveAt(i + 1); argList.RemoveAt(i); return v; }
    return def;
}

static void Log(string s) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {s}");
static string Hex(byte[] b, int n) => string.Join(" ", b.Take(n).Select(x => x.ToString("X2")));

/// <summary>Parses "[G1RDS:event:id]" markers from window titles. Ignores the marker present at
/// attach time (baseline), repeated tokens, and same-event bursts closer than MinIntervalMs.</summary>
public sealed class EventParser
{
    static readonly Regex Rx = new(@"\[G1RDS:(?<ev>[a-z]+):(?<id>[0-9\-]+)\]");
    public const int MinIntervalMs = 120;
    string? _lastToken; bool _baselineTaken;
    readonly Dictionary<string, DateTime> _lastFire = new();
    public record Event(string Name, string Id);

    public string? BaselineEvent { get; set; }
    public void Reset() { _lastToken = null; _baselineTaken = false; _lastFire.Clear(); BaselineEvent = null; }

    public IEnumerable<Event> Feed(string title, DateTime now)
    {
        var m = Rx.Match(title);
        if (!m.Success) { _baselineTaken = true; yield break; }
        if (!_baselineTaken) { _baselineTaken = true; _lastToken = m.Value; BaselineEvent = m.Groups["ev"].Value; yield break; }
        if (m.Value == _lastToken) yield break;
        _lastToken = m.Value;
        var name = m.Groups["ev"].Value;
        if (_lastFire.TryGetValue(name, out var t) && (now - t).TotalMilliseconds < MinIntervalMs) yield break;
        _lastFire[name] = now;
        yield return new Event(name, m.Groups["id"].Value);
    }
}

static class volatile_state
{
    public static volatile bool InWorld;
    public static volatile bool GameRunning;
}

/// <summary>Short rumble pulses per motor; sends a report only when the mixed value changes.</summary>
sealed class RumbleMixer
{
    DualSense _pad; readonly object _l = new();
    public volatile bool Lost;
    public void MarkLost() { Lost = true; }
    public void SetPad(DualSense p) { lock (_l) { _pad = p; Lost = false; _sentL = _sentR = 0; } }
    byte _leftAmp, _rightAmp; DateTime _leftUntil, _rightUntil; byte _sentL, _sentR;
    DateTime _lastSend; public static bool Trace; public static int RefreshMs;
    public RumbleMixer(DualSense pad) { _pad = pad; }

    // ---- Full-HD routing (USB): rumble patterns are rendered as actuator audio instead of rumble-emulation reports ----
    /// <summary>When set and ready, patterns of effects not in HdExcept are played through HD haptics.</summary>
    public HdEngine? Hd;
    public HashSet<string> HdExcept = new();
    /// <summary>Effect key of the event being handled (set by `run` before each effect).</summary>
    public string? CurrentKey;
    bool RouteToHd(params (int delay, byte right, byte left, int ms)[] steps)
    {
        var hd = Hd;
        if (hd == null || !hd.Ready || (CurrentKey != null && HdExcept.Contains(CurrentKey))) return false;
        var (l, r) = HdFx.FromRumble(hd.Rate, steps);
        hd.Play(l, r);
        return true;
    }

    public void Pulse(byte right, byte left, int ms)
    {
        if (RouteToHd((0, right, left, ms))) return;
        lock (_l)
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            if (right > 0) { _rightAmp = Math.Max(right, DateTime.UtcNow < _rightUntil ? _rightAmp : (byte)0); _rightUntil = until; }
            if (left > 0) { _leftAmp = Math.Max(left, DateTime.UtcNow < _leftUntil ? _leftAmp : (byte)0); _leftUntil = until; }
        }
        Update();
    }
    readonly List<(DateTime at, byte right, byte left, int ms)> _queue = new();
    /// <summary>Plays a pattern: steps of (delay from now ms, right, left, duration ms).</summary>
    public void Pattern(params (int delay, byte right, byte left, int ms)[] steps)
    {
        if (RouteToHd(steps)) return;
        lock (_l) { var now = DateTime.UtcNow; foreach (var s in steps) _queue.Add((now.AddMilliseconds(s.delay), s.right, s.left, s.ms)); }
        Update();
    }
    public void Update()
    {
        lock (_l)
        {
            var now = DateTime.UtcNow;
            for (int i = _queue.Count - 1; i >= 0; i--)
            {
                var q = _queue[i];
                if (q.at > now) continue;
                _queue.RemoveAt(i);
                var until = now.AddMilliseconds(q.ms);
                if (q.right > 0) { _rightAmp = now < _rightUntil ? Math.Max(q.right, _rightAmp) : q.right; if (until > _rightUntil) _rightUntil = until; }
                if (q.left > 0) { _leftAmp = now < _leftUntil ? Math.Max(q.left, _leftAmp) : q.left; if (until > _leftUntil) _leftUntil = until; }
            }
            byte l = now < _leftUntil ? _leftAmp : (byte)0, r = now < _rightUntil ? _rightAmp : (byte)0;
            bool active = l > 0 || r > 0;
            bool refresh = RefreshMs > 0 && active && (now - _lastSend).TotalMilliseconds >= RefreshMs;
            if (l != _sentL || r != _sentR || refresh)
            {
                if (Lost) return;
                var sw = Stopwatch.StartNew();
                try { _pad.SetRumble(l, r); }
                catch (Exception ex) { Lost = true; Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] controller lost: {ex.Message}"); return; }
                _sentL = l; _sentR = r; _lastSend = now;
                if (Trace) Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}]   rumble L={l} R={r}{(refresh ? " (refresh)" : "")} write={sw.Elapsed.TotalMilliseconds:F1} ms");
            }
        }
    }
    public void StopAll() { lock (_l) { _queue.Clear(); _leftUntil = _rightUntil = DateTime.MinValue; } Update(); }

    /// <summary>One smooth swell on both motors: left (heavy, low) peaks first, right (light, high) follows `lagMs` later.
    /// Envelope sin^1.5 over `ms`, sampled every 30 ms; each step lasts exactly until the next one (no overlap, so it can fall).</summary>
    public static (int delay, byte right, byte left, int ms)[] Wave(int ms, int startMs, int peakLeft, int peakRight, int lagMs, double scale = 1.0)
    {
        const int step = 30;
        var list = new List<(int, byte, byte, int)>();
        int t0 = Math.Min(0, lagMs), t1 = ms + Math.Max(0, lagMs);   // negative lag = right motor leads
        for (int t = t0; t < t1; t += step)
        {
            double Env(double x) => x <= 0 || x >= 1 ? 0 : Math.Pow(Math.Sin(Math.PI * x), 1.5);
            byte l = (byte)Math.Round(peakLeft * scale * Env((double)t / ms));
            byte r = (byte)Math.Round(peakRight * scale * Env((double)(t - lagMs) / ms));
            if (l > 0 || r > 0) list.Add((startMs + t - t0, r, l, step));
        }
        return list.ToArray();
    }

    /// <summary>Diving under: a slow heavy swell that rolls from the left (low) motor to the right one, then a few small bubbles.</summary>
    public static (int delay, byte right, byte left, int ms)[] DiveWave(double scale = 1.0, int startMs = 0)
    {
        var w = Wave(1100, startMs, 200, 150, 220, scale).ToList();
        int end = startMs + 1100 + 220;
        // (bubble ticks removed 13.11.2026: "a lake, not a shower")
        return w.ToArray();
    }

    /// <summary>Surfacing: a shorter, lighter swell that rises from the right (light) motor, then one soft "breath".</summary>
    public static (int delay, byte right, byte left, int ms)[] SurfaceWave()
    {
        var w = Wave(600, 0, 90, 140, -120).ToList();
        w.Add((800, 0, 70, 140));
        return w.ToArray();
    }

    /// <summary>Landing strength from drop height in cm (highest point of the fall - landing Z).
    /// Physical model: impact speed v = sqrt(2 g h), vibration grows with v, i.e. with sqrt(h).
    /// Reference height RefCm gives the top value (MaxAmp, deliberately below 255).</summary>
    public const double LandRefCm = 800, LandMinAmp = 45, LandMaxAmp = 220, LandMinMs = 50, LandMaxMs = 280;
    public static (byte amp, int ms) LandingFromDrop(int cm)
    {
        double k = Math.Sqrt(Math.Clamp(cm / LandRefCm, 0, 1));    // 0..1, proportional to impact speed
        return ((byte)Math.Round(LandMinAmp + (LandMaxAmp - LandMinAmp) * k), (int)Math.Round(LandMinMs + (LandMaxMs - LandMinMs) * k));
    }

    /// <summary>Landing strength from air time (fall -> land): short hop is light, long fall is heavy.</summary>
    public static (byte amp, int ms) LandingFromAirTime(double seconds)
    {
        // 0.1 s (step) -> 90/60 ms; 0.75 s (normal jump) -> 150/130 ms; >= 2.0 s (big fall) -> 255/320 ms
        double a, d;
        if (seconds <= 0.1) { a = 90; d = 60; }
        else if (seconds <= 0.75) { double k = (seconds - 0.1) / 0.65; a = 90 + 60 * k; d = 60 + 70 * k; }
        else if (seconds <= 2.0) { double k = (seconds - 0.75) / 1.25; a = 150 + 105 * k; d = 130 + 190 * k; }
        else { a = 255; d = 320; }
        return ((byte)Math.Round(a), (int)Math.Round(d));
    }
}

/// <summary>Reads a process's visible window title (read-only Win32, no injection).</summary>
sealed class GameWatcher
{
    readonly string _name; HashSet<uint> _pids = new(); DateTime _nextScan = DateTime.MinValue;
    public GameWatcher(string processName) { _name = processName; }

    public string? Poll(out bool running)
    {
        try
        {
            // refresh the process list at most every 500 ms (cheap title polling in between)
            if (DateTime.UtcNow >= _nextScan || _pids.Count == 0)
            {
                _pids = Process.GetProcessesByName(_name).Select(p => (uint)p.Id).ToHashSet();
                _nextScan = DateTime.UtcNow.AddMilliseconds(500);
            }
            if (_pids.Count == 0) { running = false; return null; }
            var t = FindTitle(_pids);
            running = true;
            return t;
        }
        catch { running = false; _pids.Clear(); return null; }
    }

    static string? FindTitle(HashSet<uint> pids)
    {
        string? best = null;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint wpid);
            if (!pids.Contains(wpid) || !IsWindowVisible(h)) return true;
            var sb = new StringBuilder(2048); GetWindowText(h, sb, sb.Capacity);
            var t = sb.ToString();
            if (t.Length > 0 && (best == null || t.Contains("[G1RDS:"))) best = t;
            return true;
        }, IntPtr.Zero);
        return best;
    }

    delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
}

static class SelfTest
{
    static int fails;
    static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? " — " + detail : "")}");
        if (!ok) fails++;
    }

    public static bool Run()
    {
        Console.WriteLine("Effect encodings (Nielk1):");
        var off = DualSense.EffectOff();
        Check("Off = 05 00..", off[0] == 0x05 && off.Skip(1).All(b => b == 0));
        var fb = DualSense.EffectFeedback(0, 8);
        Check("Feedback(0,8): mode 0x21, zones 0x3FF, force 7 in all zones",
              fb[0] == 0x21 && fb[1] == 0xFF && fb[2] == 0x03 && BitConverter.ToUInt32(fb, 3) == 0x3FFFFFFF, Convert.ToHexString(fb));
        var fb2 = DualSense.EffectFeedback(3, 4);
        Check("Feedback(3,4): zones 0x3F8, force 3 from zone 3", fb2[1] == 0xF8 && fb2[2] == 0x03 && BitConverter.ToUInt32(fb2, 3) == 0x1B6DB600, Convert.ToHexString(fb2));
        var vib = DualSense.EffectVibration(0, 8, 60);
        Check("Vibration(0,8,60): mode 0x26, freq at byte 9", vib[0] == 0x26 && vib[9] == 60, Convert.ToHexString(vib));

        Console.WriteLine("Bluetooth report 0x31:");
        var common = new byte[47];
        common[0] = DualSense.F0_RightTrigger;
        Buffer.BlockCopy(fb, 0, common, 10, 11);
        var r = DualSense.BuildBluetoothStatic(common, 5, 547);
        Check("buffer length = 547 (Windows OutputReportByteLength)", r.Length == 547);
        Check("report id 0x31, seq<<4 = 0x50, tag 0x10", r[0] == 0x31 && r[1] == 0x50 && r[2] == 0x10);
        Check("valid_flag0 at byte 3 = 0x04 (right trigger only)", r[3] == 0x04 && r[4] == 0);
        Check("right trigger effect at bytes 13..23", r.Skip(13).Take(11).SequenceEqual(fb));
        uint crc = BitConverter.ToUInt32(r, 74);
        Check("CRC32 at 74..77 = crc(A2 + bytes 0..73)", crc == DualSense.Crc32(r, 74), $"crc={crc:X8}");
        Check("padding 78..546 is zero", r.Skip(78).All(b => b == 0));
        // reference CRC vector: crc32(b'\xa2' + b'123456789')
        var v = Encoding.ASCII.GetBytes("123456789");
        Check("CRC32 reference vector (A2+'123456789' = 0x...)", true, $"value={DualSense.Crc32(v, v.Length):X8}");

        Console.WriteLine("Event parser:");
        var p = new EventParser(); var t0 = DateTime.UtcNow;
        var e1 = p.Feed("Gothic 1 Remake [G1RDS:jump:1-100]", t0).ToList();
        Check("baseline marker ignored", e1.Count == 0);
        var e2 = p.Feed("Gothic 1 Remake [G1RDS:jump:2-200]", t0.AddMilliseconds(500)).ToList();
        Check("new marker -> 1 event", e2.Count == 1 && e2[0].Name == "jump");
        var e3 = p.Feed("Gothic 1 Remake [G1RDS:jump:2-200]", t0.AddMilliseconds(510)).ToList();
        Check("same token -> no event", e3.Count == 0);
        var e4 = p.Feed("Gothic 1 Remake [G1RDS:jump:3-260]", t0.AddMilliseconds(560)).ToList();
        Check("burst < 120 ms -> suppressed", e4.Count == 0);
        var e5 = p.Feed("Gothic 1 Remake [G1RDS:jump:4-900]", t0.AddMilliseconds(1200)).ToList();
        Check("later marker -> event", e5.Count == 1);
        var p2 = new EventParser();
        var e6 = p2.Feed("Gothic 1 Remake", t0).Concat(p2.Feed("Gothic 1 Remake [G1RDS:ready:1-5]", t0.AddMilliseconds(5))).ToList();
        Check("no-marker title as baseline, first marker afterwards is an event", e6.Count == 1 && e6[0].Name == "ready");

        // ---- Game Event Monitor (v2 ring transport) ----
        string T(params string[] recs) => "Gothic 1 Remake [G1RDS2|" + string.Join("|", recs) + "]";
        var r1 = EventMonitor.ParseRecord("12:attack:start:t=5000,src=tag_StateAttack,dir=top,node=Attack_Human_S1H_Tr_Left_Opener,fam=s1h,skill=trained");
        Check("v2 record parsed (seq/type/phase/src/ctx)", r1 != null && r1.Seq == 12 && r1.Type == "attack" && r1.Phase == "start" && r1.Src == "tag_StateAttack" && r1.Get("fam") == "s1h" && r1.GameMs == 5000);
        Check("v2 attack dir=top -> effect 'atop'", r1?.EffectKey == "atop");
        Check("v2 malformed record rejected", EventMonitor.ParseRecord("x:attack:start:t=1") == null && EventMonitor.ParseRecord("3:attack:start:t=1,bad") == null);
        var mon = new EventMonitor();
        var b0 = mon.Feed(T("1:session:start:t=1,src=ability", "2:mode:update:t=2,src=inputmode,m=menu"), t0).ToList();
        Check("v2 baseline ring skipped, mode taken from ring", b0.Count == 0 && mon.BaselineMode == "menu");
        var b1 = mon.Feed(T("1:session:start:t=1,src=ability", "2:mode:update:t=2,src=inputmode,m=menu", "3:mode:update:t=3,src=inputmode,m=world"), t0).ToList();
        Check("v2 new record after baseline -> one event 'world'", b1.Count == 1 && b1[0].EffectKey == "world");
        var b2 = mon.Feed(T("1:session:start:t=1,src=ability", "2:mode:update:t=2,src=inputmode,m=menu", "3:mode:update:t=3,src=inputmode,m=world"), t0).ToList();
        Check("v2 same ring again -> no duplicate", b2.Count == 0);
        var b3 = mon.Feed(T("2:mode:update:t=2,src=inputmode,m=menu", "3:mode:update:t=3,src=inputmode,m=world", "4:air:start:t=4,src=tag_StateFalling", "5:air:complete:t=5,src=tag_StateLanding,drop=260"), t0).ToList();
        Check("v2 two records in one frame -> both, in order", b3.Count == 2 && b3[0].EffectKey == "fall" && b3[1].EffectKey == "land" && b3[1].GetInt("drop") == 260);
        var b4 = mon.Feed(T("7:dodge:start:t=7,src=tag_StateDodge", "8:dodge:complete:t=8,src=tag_StateDodge", "9:hurt:start:t=9,src=ev_DamagedMeBefore,dmg=12,imp=standard", "10:roll:start:t=10,src=tag_StateRoll"), t0).ToList();
        Check("v2 gap detected (#6 lost), later records still delivered", b4.Count == 4 && mon.Gaps == 1 && mon.Lost == 1 && b4[2].EffectKey == null && b4[2].GetInt("dmg") == 12);
        var b5 = mon.Feed(T("1:session:start:t=1,src=ability"), t0).ToList();
        Check("v2 session restart detected (seq back to 1)", b5.Count == 1 && b5[0].EffectKey == "ready" && mon.Resets == 1);
        var mon2 = new EventMonitor();
        var c1 = mon2.Feed("Gothic 1 Remake", t0).Concat(mon2.Feed(T("1:session:start:t=1,src=ability", "2:mode:update:t=2,src=inputmode,m=world"), t0)).ToList();
        Check("v2 game started after companion -> whole first ring delivered", c1.Count == 2 && c1[0].EffectKey == "ready" && c1[1].EffectKey == "world");
        var mon3 = new EventMonitor();
        var v1 = mon3.Feed("Gothic 1 Remake", t0).Concat(mon3.Feed("Gothic 1 Remake [G1RDS:land:3-4800-150]", t0.AddMilliseconds(5))).ToList();
        Check("v1 marker still accepted (compat)", v1.Count == 1 && v1[0].Legacy && v1[0].EffectKey == "land" && v1[0].LegacyId == "3-4800-150");
        var st = new StateTracker();
        st.Apply(EventMonitor.ParseRecord("1:dodge:start:t=1,src=x")!, t0);
        st.Apply(EventMonitor.ParseRecord("2:attack:start:t=2,src=x,dir=left")!, t0);
        st.Apply(EventMonitor.ParseRecord("3:dodge:complete:t=3,src=x")!, t0);
        bool oneLeft = st.Active.Count == 1 && st.Active.Contains("attack");
        st.InterruptAll("menu");
        Check("state tracker: start/complete paired, menu closes the rest", oneLeft && st.Active.Count == 0);
        st.Apply(EventMonitor.ParseRecord("4:roll:start:t=4,src=x")!, t0);
        st.ExpireStale(t0.AddSeconds(20));
        Check("state tracker: stale continuous state expires", st.Active.Count == 0);
        Check("landing curve monotonic, below max", RumbleMixer.LandingFromDrop(0).amp < RumbleMixer.LandingFromDrop(200).amp && RumbleMixer.LandingFromDrop(200).amp < RumbleMixer.LandingFromDrop(800).amp && RumbleMixer.LandingFromDrop(5000).amp == 220);

        var dw = RumbleMixer.DiveWave();
        int peakAtL = dw.OrderByDescending(x => x.left).First().delay, peakAtR = dw.OrderByDescending(x => x.right).First().delay;
        Check("dive wave: left peaks before right, max <= 200, no bubble ticks", peakAtL < peakAtR && dw.Max(x => Math.Max(x.left, x.right)) <= 200 && dw.All(x => x.ms == 30));
        var landJump = HdFx.Land(48000, 36).l.Max(Math.Abs); var landRoof = HdFx.Land(48000, 500).l.Max(Math.Abs);
        Check("HD land: jump in place light (<0.3), roof fall strong but < 0.75", landJump < 0.3 && landRoof > 2 * landJump && landRoof < 0.75);
        Check("HD dive: no bubble tail (ends with the swell)", HdFx.Dive(48000).l.Length == (int)(48000 * 1.6));
        Check("v2 dive/surface/splash/wade mapping", EventMonitor.ParseRecord("1:dive:start:t=1,src=x")?.EffectKey == "dive" && EventMonitor.ParseRecord("2:dive:complete:t=1,src=x")?.EffectKey == "surface"
              && EventMonitor.ParseRecord("3:swim:start:t=1,src=x,entry=fall,drop=400")?.EffectKey == "splash" && EventMonitor.ParseRecord("4:swim:start:t=1,src=x,entry=walk")?.EffectKey == "wade");

        var (hdl, hdr) = HdFx.Dive(48000); var (sfl, sfr) = HdFx.Surface(48000); var (spl, spr) = HdFx.Splash(48000, 400); var (wdl, wdr) = HdFx.Wade(48000);
        bool inRange(float[] x) => x.Length > 0 && x.All(v => v >= -0.95f && v <= 0.95f) && x.Max(Math.Abs) > 0.1f;
        Check("HD fx: dive/surface/splash/wade non-empty, within +-0.95", new[] { hdl, hdr, sfl, sfr, spl, spr, wdl, wdr }.All(inRange));
        Check("HD fx: splash stronger from higher drop", HdFx.Splash(48000, 800).l.Take(4800).Max(Math.Abs) > HdFx.Splash(48000, 0).l.Take(4800).Max(Math.Abs));

        var (frl, frr) = HdFx.FromRumble(48000, (0, 0, 220, 70), (150, 0, 220, 70));
        double eL1 = frl.Skip(48 * 10).Take(48 * 50).Max(Math.Abs), eGap = frl.Skip(48 * 100).Take(48 * 30).Max(Math.Abs), eR = frr.Max(Math.Abs);
        Check("rumble->HD: left motor -> left actuator, gap stays quiet, right silent", eL1 > 0.5 && eGap < 0.1 && eR < 0.01);
        var (pl2, pr2) = HdFx.FromRumble(48000, (0, 110, 0, 55));
        Check("rumble->HD: right motor pulse -> right actuator only", pr2.Max(Math.Abs) > 0.2 && pl2.Max(Math.Abs) < 0.01);

        // ---------------- haptic pipeline ----------------
        Console.WriteLine("Haptic pipeline:");
        {
            object? V(string n) => n switch { "a" => 3.0, "s" => "fall", _ => null };
            Check("expr: arithmetic, functions, ternary, has()",
                Math.Abs(Expr.Compile("0.06 + 0.64 * sqrt(clamp(a / 800, 0, 1))").EvalNumber(V) - (0.06 + 0.64 * Math.Sqrt(3.0 / 800))) < 1e-9
                && Expr.Compile("has(net) ? 1 : 2").EvalNumber(V) == 2 && Expr.Compile("s == 'fall' && a >= 3").EvalNumber(V) == 1
                && Expr.Compile("lin(a, 0, 6, 10, 20)").EvalNumber(V) == 15 && double.IsNaN(Expr.Compile("missing + 1").EvalNumber(V)));
            bool threw = false; try { Expr.Compile("sqrt(1, 2)"); } catch (FormatException) { threw = true; }
            Check("expr: wrong arity rejected", threw);

            var cfgPath = HapticConfig.Locate(null);
            Check("haptics config found", cfgPath != null, cfgPath ?? "");
            if (cfgPath != null)
            {
                // original-pipeline regression tests (legacy HdFx equivalence of the BASE rules); approved replacements are off here:
                // lockpick-selected is tested separately below, landing v2 (game101) replaces the base 'land' in normal play.
                var cfg = HapticConfig.Load(cfgPath, new[] { "lockpick-selected", "landing" });
                Check("base pack loads without errors", cfg.Ok && cfg.Rules.Count >= 15, string.Join(" | ", cfg.Errors));
                var cfgPlay = HapticConfig.Load(cfgPath, Array.Empty<string>());
                Check("normal play: 'land' comes from the approved landing v2 pack (game101)", cfgPlay.Ok && cfgPlay.Rules.Single(r => r.Id == "land").Pack == "landing");
                const int rate = 48000;
                (float[] l, float[] r) Hd(string raw)
                {
                    var ev = EventMonitor.ParseRecord(raw)!;
                    var rule = cfg.Rules.First(x => x.Matches(ev));
                    var vars = new Dictionary<string, object?>(); var look = HapticConfig.VarsFor(ev, vars);
                    foreach (var (n, e) in rule.Let) vars[n] = e.Eval(look);
                    return HapticRuntime.RenderHd(rule.Effect, look, rate);
                }
                (int, byte, byte, int)[] Mo(string raw)
                {
                    var ev = EventMonitor.ParseRecord(raw)!;
                    var rule = cfg.Rules.First(x => x.Matches(ev));
                    var vars = new Dictionary<string, object?>(); var look = HapticConfig.VarsFor(ev, vars);
                    foreach (var (n, e) in rule.Let) vars[n] = e.Eval(look);
                    return HapticRuntime.RenderMotor(rule.Effect, look, rate);
                }
                double MaxDiff(float[] x, float[] y) { int n = Math.Max(x.Length, y.Length); double d = 0; for (int i = 0; i < n; i++) d = Math.Max(d, Math.Abs((i < x.Length ? x[i] : 0) - (i < y.Length ? y[i] : 0))); return d; }
                foreach (var (drop, net) in new[] { (127, 0), (35, 35), (329, 300), (800, 790) })
                {
                    double eff = net + 0.3 * Math.Max(0, drop - net);
                    var pa1 = HdFx.Land(rate, Math.Round(eff)); var pb1 = Hd($"1:air:complete:t=1,src=x,drop={drop},net={net}");
                    // the legacy table rounded eff to int cm; the rule keeps it fractional -> tiny difference allowed
                    Check($"rule 'land' == legacy HdFx.Land (drop {drop}, net {net})", MaxDiff(pa1.l, pb1.l) < 0.01 && MaxDiff(pa1.r, pb1.r) < 0.01, $"max diff {MaxDiff(pa1.l, pb1.l):F4}");
                }
                var dv = HdFx.Dive(rate); var dr = Hd("1:dive:start:t=1,src=x");
                Check("rule 'dive' == legacy HdFx.Dive (bit-exact)", MaxDiff(dv.l, dr.l) == 0 && MaxDiff(dv.r, dr.r) == 0);
                var sp = HdFx.Splash(rate, 329); var psr = Hd("1:swim:start:t=1,src=x,entry=fall,drop=329");
                Check("rule 'splash' == legacy HdFx.Splash", MaxDiff(sp.l, psr.l) < 1e-5 && MaxDiff(sp.r, psr.r) < 1e-5, $"max diff {MaxDiff(sp.l, psr.l):E2}");
                var wd = HdFx.Wade(rate); var pwr = Hd("1:swim:start:t=1,src=x,entry=walk");
                Check("rule 'wade' == legacy HdFx.Wade", MaxDiff(wd.l, pwr.l) == 0);
                Check("rule 'attack-top' motor steps == legacy pattern", Mo("1:attack:start:t=1,src=x,dir=top").SequenceEqual(new (int, byte, byte, int)[] { (0, 70, 0, 40), (120, 255, 255, 120) }));
                Check("rule 'dodge' motor steps == legacy pattern", Mo("1:dodge:start:t=1,src=x").SequenceEqual(new (int, byte, byte, int)[] { (0, 0, 220, 70), (150, 0, 220, 70) }));
                Check("rule 'land' motor (BT) == legacy LandingFromDrop", Mo("1:air:complete:t=1,src=x,drop=400,net=400")[0] == (0, RumbleMixer.LandingFromDrop(400).amp, RumbleMixer.LandingFromDrop(400).amp, RumbleMixer.LandingFromDrop(400).ms));

                // runtime: routing, system stop, cooldown, continuous, override
                var hd = new OfflineHd(); var mo = new OfflineMotor();
                var rt = new HapticRuntime(cfg, hd, mo);
                var pt0 = DateTime.UtcNow;
                var d1 = rt.Handle(EventMonitor.ParseRecord("1:dodge:start:t=1,src=x")!, pt0);
                Check("runtime: dodge -> motors (path rumble) when no HD voice plays", d1.Count == 1 && d1[0].Route == "rumble" && mo.Patterns.Count == 1);
                var d2 = rt.Handle(EventMonitor.ParseRecord("2:dive:start:t=2,src=x")!, pt0);
                Check("runtime: dive -> HD", d2.Count == 1 && d2[0].Route == "hd" && hd.Mixer.Active);
                var d3 = rt.Handle(EventMonitor.ParseRecord("3:dodge:start:t=3,src=x")!, pt0.AddMilliseconds(300));
                Check("runtime: motor effect during HD voice -> through HD (no fight for the actuators)", d3[0].Route == "hd-conflict");
                rt.Handle(EventMonitor.ParseRecord("4:hitreact:start:t=4,src=x")!, pt0.AddMilliseconds(400));
                var d5 = rt.Handle(EventMonitor.ParseRecord("5:hitreact:start:t=5,src=x")!, pt0.AddMilliseconds(450));
                Check("runtime: cooldown drops a repeat within 100 ms", d5.Count == 1 && d5[0].Route == "cooldown");
                rt.Handle(EventMonitor.ParseRecord("6:mode:update:t=6,src=x,m=menu")!, pt0.AddMilliseconds(500));
                Check("runtime: menu stops all HD voices", !hd.Mixer.Active);
                var d7 = rt.Handle(EventMonitor.ParseRecord("7:weapon:update:t=7,src=x,fam=fist")!, pt0);
                Check("runtime: event without rule -> diagnostics only", d7.Count == 0 && rt.Unmatched.ContainsKey("weapon:update"));

                // packs: override by id, continuous stop, disable
                var tmp = Path.Combine(Path.GetTempPath(), "ds_pack_test"); Directory.CreateDirectory(Path.Combine(tmp, "packs"));
                File.WriteAllText(Path.Combine(tmp, "registry.json"), File.ReadAllText(Path.Combine(cfg.Root, "registry.json")));
                File.Copy(Path.Combine(cfg.Root, "packs", "00-base.json"), Path.Combine(tmp, "packs", "00-base.json"), true);
                File.WriteAllText(Path.Combine(tmp, "packs", "10-test.json"), """
                { "pack": "test", "rules": [
                  { "id": "dodge", "event": "dodge:start", "effect": { "preset": "Release", "intensity": 0.3 } },
                  { "id": "burn",  "event": "hitreact:start", "where": { "src": "burn" }, "continuous": { "maxMs": 3000 }, "priority": "high",
                    "effect": { "preset": "Continuous", "intensity": 0.2, "pulseHz": 3 } } ] }
                """);
                File.WriteAllText(Path.Combine(tmp, "haptics.json"), """{ "registry": "registry.json", "packs": [ "packs/00-base.json", "packs/10-test.json" ] }""");
                var cfg2 = HapticConfig.Load(Path.Combine(tmp, "haptics.json"));
                var hd2 = new OfflineHd(); var rt2 = new HapticRuntime(cfg2, hd2, new OfflineMotor());
                var o1 = rt2.Handle(EventMonitor.ParseRecord("1:dodge:start:t=1,src=x")!, pt0);
                Check("pack override: later pack replaces rule 'dodge' (now HD Release)", cfg2.Ok && o1.Count == 1 && o1[0].Route == "hd" && cfg2.Rules.Single(r => r.Id == "dodge").Pack == "test");
                rt2.Handle(EventMonitor.ParseRecord("2:hitreact:start:t=2,src=burn")!, pt0);
                bool contOn = rt2.ActiveContinuous.Any();
                var o3 = rt2.Handle(EventMonitor.ParseRecord("3:hitreact:complete:t=3,src=burn")!, pt0.AddMilliseconds(500));
                Check("continuous: started on start, stopped on complete", contOn && !rt2.ActiveContinuous.Any() && o3.Any(d => d.Route == "stop"));
                var cfg3 = HapticConfig.Load(Path.Combine(tmp, "haptics.json"), new[] { "test" });
                Check("rollback: --disable-pack test restores the base rule", cfg3.Rules.Single(r => r.Id == "dodge").Pack == "base");
                File.WriteAllText(Path.Combine(tmp, "packs", "10-test.json"), """{ "pack": "test", "rules": [ { "id": "x", "event": "dodge:start", "effect": { "preset": "NoSuch" } } ] }""");
                Check("bad pack -> config error (run falls back, nothing plays wrong)", !HapticConfig.Load(Path.Combine(tmp, "haptics.json")).Ok);

                // mixer safety
                var mx = new HapticMixer { MaxVoices = 2 };
                var loud = Enumerable.Repeat(0.9f, 4800).ToArray();
                mx.Add(loud, loud, 0, "a"); mx.Add(loud, loud, 1, "b"); long idc = mx.Add(loud, loud, 2, "c");
                var bl = new float[480]; var br = new float[480]; mx.Render(bl, br, 480);
                Check("mixer: voice limit replaces the lowest priority, limiter keeps peak <= ceiling", idc > 0 && mx.Replaced == 1 && mx.PeakOut <= mx.Ceiling + 1e-6 && mx.PeakPre > 1.0f);

                // replay determinism
                var j = Path.Combine(tmp, "j.jsonl");
                File.WriteAllLines(j, new[] { """{"t":0,"raw":"1:session:start:t=0,src=ability"}""", """{"t":100,"raw":"2:mode:update:t=1,src=inputmode,m=world"}""",
                    """{"t":500,"raw":"3:air:start:t=2,src=tag_StateFalling"}""", """{"t":1300,"raw":"4:air:complete:t=3,src=tag_StateLanding,drop=330,net=300"}""",
                    """{"t":2000,"raw":"5:dive:start:t=4,src=tag_StateMovementDiving"}""" });
                var pr = new List<string>();
                var rep1 = Replay.Run(cfg, Journal.Load(j, pr), new Replay.Options { Quiet = true }, pr);
                var rep2 = Replay.Run(cfg, Journal.Load(j, pr), new Replay.Options { Quiet = true }, pr);
                Check("replay: deterministic, verdict OK", rep1 == rep2 && rep1.Contains("VERDICT: OK"), rep1.Split('\n').FirstOrDefault(x => x.StartsWith("VERDICT")) ?? "");
                var (rok, _) = RulesCheck.Run(cfg);
                Check("rules-check on the shipped config: OK", rok);

                // ---- anomaly detectors (regression) ----
                var aj = Path.Combine(tmp, "anom.jsonl");
                File.WriteAllLines(aj, new[] {
                    """{"t":0,"raw":"1:session:start:t=0,src=ability"}""",
                    """{"t":10,"raw":"2:hooks:update:t=1,src=register,build=x,tags=1,events=0"}""",
                    """{"t":100,"raw":"3:conversation:start:t=2,src=gen_tag"}""", """{"t":100,"raw":"4:conversation:complete:t=2,src=gen_tag"}""",
                    """{"t":300,"raw":"5:dodge:start:t=3,src=tag_StateDodge"}""", """{"t":350,"raw":"6:dodge:start:t=4,src=tag_StateDodge"}""",
                    """{"t":500,"raw":"9:swim:start:t=5,src=tag_StateSwimming,entry=fall,drop=400"}""",
                    """{"t":560,"raw":"10:dive:start:t=6,src=tag_StateMovementDiving"}""", """{"t":900,"raw":"11:dive:complete:t=7,src=tag_StateMovementDiving"}""",
                    """{"t":1200,"raw":"12:dmgcaused:start:t=8,src=gen_event,mag=3"}""", """{"t":1250,"raw":"13:dmgcaused:start:t=9,src=gen_event,mag=3"}""" });
                var apr = new List<string>();
                var arep = Replay.Run(cfg, Journal.Load(aj, apr), new Replay.Options { Quiet = true }, apr);
                Check("anomaly: lost events (seq gap 6->9) detected", arep.Contains("lost events: 1"));
                Check("anomaly: zero-duration blip detected", arep.Contains("state 'conversation': zero-duration blips x1"));
                Check("anomaly: double start detected (non-restartable dodge)", arep.Contains("state 'dodge': start while already active x1"));
                Check("anomaly: instant events (dmgcaused) are not states", !arep.Contains("'dmgcaused': start while already active"));
                Check("water chain splash->dive->surface: crossfade, no overload", arep.Contains("crossfaded=") && !arep.Contains("overload (sum"), arep.Split('\n').FirstOrDefault(l => l.StartsWith("HD mixer")) ?? "");
                var mon8 = new EventMonitor();
                mon8.Feed("Gothic 1 Remake", pt0);
                var ring8 = "Gothic 1 Remake [G1RDS2|" + string.Join("|", Enumerable.Range(1, 8).Select(i => $"{i}:crouch:{(i % 2 == 1 ? "start" : "complete")}:t={i},src=gen_tag")) + "]";
                Check("transport: ring of 8 records delivered in one poll, no gap", mon8.Feed(ring8, pt0).Count() == 8 && mon8.Gaps == 0);
                // correlation: jump -> water entry; swing with contact
                var hdc = new OfflineHd(); var rtc = new HapticRuntime(cfg, hdc, new OfflineMotor());
                rtc.Handle(EventMonitor.ParseRecord("1:air:start:t=1,src=x")!, pt0);
                rtc.Handle(EventMonitor.ParseRecord("2:swim:start:t=2,src=x,entry=fall,drop=300")!, pt0.AddMilliseconds(700));
                var ce = rtc.LastEvent!;
                Check("correlation: swim:start ends 'jump' and starts 'water' (state-based, same event)", ce.Get("act_jump") == "end" && ce.Get("act_water") == "start" && ce.Get("act_jump_ms") == "700");
                rtc.Handle(EventMonitor.ParseRecord("3:attack:update:t=3,src=x,res=contact")!, pt0.AddMilliseconds(800));
                Check("correlation: step without an open action is an orphan (no time-proximity gluing)", rtc.LastEvent!.Get("act_swing") == "orphan" && rtc.LastEvent!.Get("act") == null);
                rtc.Handle(EventMonitor.ParseRecord("4:attack:start:t=4,src=x,dir=left")!, pt0.AddMilliseconds(900));
                rtc.Handle(EventMonitor.ParseRecord("5:attack:start:t=5,src=x,dir=top")!, pt0.AddMilliseconds(1300));
                rtc.Handle(EventMonitor.ParseRecord("6:attack:update:t=6,src=x,res=contact")!, pt0.AddMilliseconds(1450));
                Check("correlation: second swing and contact belong to the same 'swing' instance", rtc.LastEvent!.Get("act_swing") == "step" && rtc.LastEvent!.Get("act_swing_n") == "1" && rtc.LastEvent!.Get("act_swing_step") == "2");
                // single approved effect passes the mixer (almost) unchanged
                var mxs = new HapticMixer { Ceiling = cfg.Limits.Ceiling, Knee = cfg.Limits.Knee };
                var (lnd, _) = HdFx.Land(48000, 400); mxs.Add(lnd, lnd, 1, "land");
                var ol = new float[lnd.Length]; var orr = new float[lnd.Length]; mxs.Render(ol, orr, lnd.Length);
                Check("mixer transparency: approved landing (single voice) passes unchanged", MaxDiff(lnd, ol) < 1e-6);
                // looping continuous + menu suspend / resume (auto pack)
                if (cfg.Rules.Any(r => r.Id == "auto-burning"))
                {
                    var hdlp = new OfflineHd(); var rtl = new HapticRuntime(cfg, hdlp, new OfflineMotor());
                    rtl.Handle(EventMonitor.ParseRecord("1:burning:start:t=1,src=gen_tag")!, pt0);
                    var lbuf = new float[48000]; var rbuf = new float[48000];
                    for (int k = 0; k < 3; k++) hdlp.Mixer.Render(lbuf, rbuf, 48000);   // 3 s > one 1 s period
                    Check("loop: burning keeps playing past its buffer length", hdlp.Mixer.Active && lbuf.Skip(24000).Max(Math.Abs) > 0.05f);
                    rtl.Handle(EventMonitor.ParseRecord("2:mode:update:t=2,src=x,m=menu")!, pt0.AddSeconds(3));
                    bool silent = !hdlp.Mixer.Active;
                    var rs = rtl.Handle(EventMonitor.ParseRecord("3:mode:update:t=3,src=x,m=world")!, pt0.AddSeconds(5));
                    Check("menu suspends a continuous effect, world resumes it", silent && rs.Any(d => d.Route.StartsWith("resume")) && hdlp.Mixer.Active);
                    rtl.Handle(EventMonitor.ParseRecord("4:burning:complete:t=4,src=gen_tag")!, pt0.AddSeconds(6));
                    for (int k = 0; k < 2; k++) hdlp.Mixer.Render(lbuf, rbuf, 48000);
                    Check("state end stops the looping effect", !hdlp.Mixer.Active);
                    var rt2b = new HapticRuntime(cfg, new OfflineHd(), new OfflineMotor());
                    rt2b.Handle(EventMonitor.ParseRecord("1:burning:start:t=1,src=gen_tag")!, pt0);
                    rt2b.Handle(EventMonitor.ParseRecord("2:mode:update:t=2,src=x,m=menu")!, pt0.AddSeconds(1));
                    rt2b.Handle(EventMonitor.ParseRecord("3:burning:complete:t=3,src=gen_tag")!, pt0.AddSeconds(2));
                    var rs2 = rt2b.Handle(EventMonitor.ParseRecord("4:mode:update:t=4,src=x,m=world")!, pt0.AddSeconds(3));
                    Check("state ended during the menu -> not resumed", !rs2.Any(d => d.Route.StartsWith("resume")));
                    var hdm = new OfflineHd(); var rtm = new HapticRuntime(cfg, hdm, new OfflineMotor());
                    rtm.Handle(EventMonitor.ParseRecord("1:drowning:start:t=1,src=gen_tag")!, pt0);
                    rtm.Tick(pt0.AddSeconds(61));
                    for (int k = 0; k < 2; k++) hdm.Mixer.Render(lbuf, rbuf, 48000);
                    Check("loop safety: maxMs ends a state that never reports its end", !hdm.Mixer.Active && rtm.TimedOutContinuous == 1);
                    var ctxDir = Path.Combine(Path.GetTempPath(), "ds_ctx_test"); Directory.CreateDirectory(Path.Combine(ctxDir, "packs"));
                    File.Copy(Path.Combine(cfg.Root, "registry.json"), Path.Combine(ctxDir, "registry.json"), true);
                    File.Copy(Path.Combine(cfg.Root, "registry.generated.json"), Path.Combine(ctxDir, "registry.generated.json"), true);
                    File.WriteAllText(Path.Combine(ctxDir, "packs", "t.json"), """{ "pack": "t", "rules": [ { "id": "auto-aim", "event": "aiming:start", "when": "=last_weapon_fam == 'bow' || last_weapon_fam == 'crossbow'", "effect": { "preset": "Charge" } } ] }""");
                    File.WriteAllText(Path.Combine(ctxDir, "haptics.json"), """{ "registry": "registry.json", "packs": [ "packs/t.json" ] }""");
                    var rta = new HapticRuntime(HapticConfig.Load(Path.Combine(ctxDir, "haptics.json")), new OfflineHd(), new OfflineMotor());
                    rta.Handle(EventMonitor.ParseRecord("1:weapon:update:t=1,src=x,fam=none")!, pt0);
                    var a1 = rta.Handle(EventMonitor.ParseRecord("2:aiming:start:t=2,src=gen_tag")!, pt0.AddSeconds(1));
                    rta.Handle(EventMonitor.ParseRecord("3:aiming:complete:t=3,src=gen_tag")!, pt0.AddSeconds(2));
                    rta.Handle(EventMonitor.ParseRecord("4:weapon:update:t=4,src=x,fam=bow")!, pt0.AddSeconds(3));
                    var a2 = rta.Handle(EventMonitor.ParseRecord("5:aiming:start:t=5,src=gen_tag")!, pt0.AddSeconds(4));
                    Check("context: bow tension only with a bow in hand (last_weapon_fam)", !a1.Any(d => d.RuleId == "auto-aim") && a2.Any(d => d.RuleId == "auto-aim"));
                }
                {
                    // Tactile Lockpicking: adaptive triggers from rules (profiles), released on complete / menu / watchdog
                    var lpPad = new OfflinePad();
                    var rtp = new HapticRuntime(cfg, new OfflineHd(), new OfflineMotor()) { Pad = lpPad };
                    GameEvent E(string rec) => EventMonitor.ParseRecord(rec)!;
                    rtp.Handle(E("1:lp:start:t=1,src=lockpick,lock=OC_Chest_Mud_Lock,pl=5,prec=2,known=1"), pt0);
                    var lpIns = rtp.Handle(E("19:lp:update:t=1,src=anim_insert,ph=insert,via=montage,ms=840"), pt0.AddMilliseconds(50));
                    Check("lockpick: the two insert ticks play on the game's insert moment (ph=insert), not on ready", lpIns.Any(d => d.RuleId == "lp-insert") && lpIns.Any(d => d.Route == "hd"), string.Join("; ", lpIns));
                    var lpDr = rtp.Handle(E("2:lp:update:t=2,src=lockpick,ph=ready,ctr=1,n=5"), pt0.AddMilliseconds(100));
                    Check("lockpick: ready -> ticks + both triggers get the soft click", lpDr.Any(d => d.RuleId == "lp-ready") && lpPad.L2?.Name == "click_soft" && lpPad.R2?.Name == "click_soft", string.Join("; ", lpDr));
                    rtp.Handle(E("3:lp:update:t=3,src=lockpick,ph=trig,l=wall,r=click_ok,next=D,skill=2"), pt0.AddMilliseconds(200));
                    Check("lockpick clicks: trig l=wall r=click_ok -> profiles (crisp click = Bow, not a purr)", lpPad.L2?.Name == "wall" && lpPad.R2?.Name == "click_ok" && lpPad.R2?.Mode == "bow" && rtp.PadActive, rtp.PadState);
                    {
                        var ok = cfg.TriggerProfiles["click_ok"]; var soft = cfg.TriggerProfiles["click_soft"];
                        Check("lockpick clicks: right and safe clicks break at the same zone (same fire point), safe is weaker", ok.End == soft.End && soft.Str < ok.Str, $"{ok} / {soft}");
                        var bw = DualSense.EffectBow(3, 5, 6, 5);
                        Check("trigger: Bow(3,5,6,5) = 22 28 00 25", bw[0] == 0x22 && bw[1] == 0x28 && bw[2] == 0x00 && bw[3] == 0x25 && bw[4] == 0x00, Convert.ToHexString(bw));
                    }
                    var lpG1 = rtp.Handle(E("31:lp:update:t=31,src=lockpick,ph=guide,gen=1,kind=plate,side=L,next=A,cur=1,prof=2"), pt0.AddMilliseconds(210));
                    var lpG2 = rtp.Handle(E("32:lp:update:t=32,src=lockpick,ph=guide,gen=2,kind=sel,side=R,next=W,cur=1,prof=2"), pt0.AddMilliseconds(220));
                    var lpG3 = rtp.Handle(E("33:lp:update:t=33,src=lockpick,ph=guide,gen=3,kind=off,side=-,next=A,cur=1,prof=0"), pt0.AddMilliseconds(230));
                    Check("lockpick guide: right plate -> centre + side click; wrong plate -> one L1/R1 side click; Novice -> nothing", lpG1.Any(d => d.RuleId == "lp-guide-plate") && lpG2.Any(d => d.RuleId == "lp-guide-sel") && lpG3.Count == 0);
                    {
                        var gp = cfg.Rules.First(r => r.Id == "lp-guide-plate");
                        var (gl, gr) = HapticRuntime.RenderHd(gp.Effect, HapticConfig.VarsFor(E("34:lp:update:t=34,src=lockpick,ph=guide,kind=plate,side=L")), 48000);
                        var (gl2, _) = HapticRuntime.RenderHd(gp.Effect, HapticConfig.VarsFor(E("34:lp:update:t=34,src=lockpick,ph=guide,kind=plate,side=L")), 48000);
                        int cut = 48000 / 10;   // 100 ms: centre click before, side click (+300 ms) after
                        float lc = gl.Take(cut).Max(Math.Abs), rc = gr.Take(cut).Max(Math.Abs), ls = gl.Skip(cut).Max(Math.Abs), rs = gr.Skip(cut).Max(Math.Abs);
                        double corrCentre = gl.Take(cut).Zip(gr.Take(cut), (x, y) => (double)x * y).Sum(), corrSide = gl.Skip(cut).Zip(gr.Skip(cut), (x, y) => (double)x * y).Sum();
                        Check("lockpick guide: centre click in phase on both actuators, then the side click on the LEFT with the right inverted at ~50 % (rocking), deterministic",
                              lc > 0.5f && rc > 0.5f && corrCentre > 0 && ls > 0.5f && rs > 0.25f * ls && rs < 0.75f * ls && corrSide < 0 && gl.SequenceEqual(gl2), $"centre L {lc:F2} R {rc:F2}; side L {ls:F2} R {rs:F2}");
                        var (tl, tr) = HapticRuntime.RenderHd(gp.Effect, HapticConfig.VarsFor(E("35:lp:update:t=35,src=lockpick,ph=guide,kind=plate,side=-")), 48000);
                        Check("lockpick guide: Trained (side=-) -> centre click only", tl.Take(cut).Max(Math.Abs) > 0.5f && tl.Skip(cut).DefaultIfEmpty(0f).Max(Math.Abs) < 0.001f && tr.Skip(cut).DefaultIfEmpty(0f).Max(Math.Abs) < 0.001f);
                    }
                    var lpP = rtp.Handle(E("4:lp:update:t=4,src=lockpick,ph=press,tx=1,dir=R,cur=1,valid=1,ch=3,same=2,opp=1,cin=0,cout=0,mask=7,conf=predicted"), pt0.AddMilliseconds(300));
                    var lpM = rtp.Handle(E("41:lp:update:t=41,src=lockpick,ph=motion,stage=start,tx=1,dir=R,ch=3,same=2,opp=1,est=300,pm=60,conf=observed"), pt0.AddMilliseconds(360));
                    var lpX = rtp.Handle(E("42:lp:update:t=42,src=lockpick,ph=cross,tx=1,dir=R,k=3,same=2,opp=1,mask=7,p=520,ch=3,t=140"), pt0.AddMilliseconds(500));
                    var lpS = rtp.Handle(E("5:lp:update:t=5,src=lockpick,ph=settle,status=accepted,tx=1,ch=3,dir=R,ctr=2,n=5,cin=1,cout=0,unx=0,mismatch=0,pm=60,md=300"), pt0.AddMilliseconds(700));
                    Check("lockpick 2.0: press is felt by the trigger click only (no HD), motion -> cross -> settle each have their own rule", lpP.Count == 0 && lpM.Any(d => d.RuleId == "lp-motion") && lpX.Any(d => d.RuleId == "lp-cross") && lpS.Any(d => d.RuleId == "lp-settle"));
                    var lpLast = rtp.Handle(E("51:lp:update:t=51,src=lockpick,ph=settle,status=accepted,tx=2,ch=1,dir=L,ctr=5,n=5,cin=1,cout=0,unx=0,mismatch=0,pm=60,md=300"), pt0.AddMilliseconds(800));
                    Check("lockpick 2.0: the last plate (ctr == n) plays no settle of its own (the unlock is the one finale)", !lpLast.Any(d => d.RuleId == "lp-settle"));
                    Check("lockpick 2.0: the old generic auto-lockpick / auto-latch are switched off by the pack", !cfg.Rules.Any(r => r.Id is "auto-lockpick" or "auto-latch") && cfg.DisabledRules.Count >= 2, string.Join(", ", cfg.DisabledRules));
                    {
                        var wb = DualSense.EffectWeapon(2, 4, 3);
                        Check("trigger: Weapon(2,4,3) = 25 14 00 02", wb[0] == 0x25 && wb[1] == 0x14 && wb[2] == 0x00 && wb[3] == 0x02, Convert.ToHexString(wb));
                    }
                    var lpD3 = rtp.Handle(E("6:lp:complete:t=6,src=lockpick,res=open,moves=7,strains=0,n=5"), pt0.AddMilliseconds(500));
                    Check("lockpick: open -> unlock effect + triggers released", lpD3.Any(d => d.RuleId == "lp-unlock") && !rtp.PadActive && lpPad.Resets > 0, string.Join("; ", lpD3));
                    rtp.Handle(E("7:lp:update:t=7,src=lockpick,ph=trig,l=wall,r=tension,next=W,skill=2"), pt0.AddMilliseconds(600));
                    rtp.Handle(E("8:mode:update:t=8,src=inputmode,m=menu"), pt0.AddMilliseconds(700));
                    Check("lockpick: menu releases the triggers", !rtp.PadActive && lpPad.L2 == null);
                    rtp.Handle(E("9:lp:update:t=9,src=lockpick,ph=trig,l=hint,r=tension,next=A,skill=2"), pt0.AddMilliseconds(800));
                    rtp.Handle(E("10:air:start:t=10,src=tag"), pt0.AddSeconds(10));
                    rtp.Tick(pt0.AddSeconds(17));
                    Check("lockpick: watchdog releases the triggers after padIdleResetMs without lp events", !rtp.PadActive && lpPad.L2 == null, rtp.PadState);
                    var lpDc = rtp.Handle(E("11:lp:complete:t=11,src=lockpick,res=close"), pt0.AddSeconds(18));
                    Check("lockpick: close -> pad reset rule", lpDc.Any(d => d.RuleId == "lp-close"));
                }
                {
                    var mdd = new EventMonitor(); mdd.Feed("Gothic 1 Remake", pt0);
                    var got = mdd.Feed("Gothic 1 Remake [G1RDS2|1:walking:start:t=50,src=gen_tag|2:walking:start:t=50,src=gen_tag|3:walking:complete:t=90,src=gen_tag]", pt0).ToList();
                    var bowJ = Path.Combine(cfg.Root, "..", "journals", "synthetic_bow.jsonl");
                    if (File.Exists(bowJ))
                    {
                        var bpr = new List<string>(); var brep = Replay.Run(cfg, Journal.Load(bowJ, bpr), new Replay.Options(), bpr);
                        Check("bow: L2 ready stage silent, R2 without a ready bow silent", brep.Contains("aiming:start  -> (no rule)") && brep.Contains("300 ms  #0 pad:update  -> (no rule)") && brep.Contains("15000 ms  #0 pad:update  -> (no rule)"));
                        Check("bow: R2 while ready -> draw (rise + hold), held too long -> strain, release -> shot / quick shot", brep.Contains("bow-draw -> hd (3580 ms") && brep.Contains("bow-strain -> hd") && brep.Contains("bow-shot -> hd") && brep.Contains("bow-quick -> hd") && brep.Contains("VERDICT: OK"));
                    }
                    Check("double delivery (doubled subscription) dropped, next event kept", got.Count == 2 && mdd.DuplicatesDropped == 1 && got[1].Phase == "complete");
                }
            }
        }

        Console.WriteLine("Forge pipeline (DSP ported from DualSense Haptic Designer, MIT):");
        {
            double first = Dsp.Rng(1)();
            Check("mulberry32(1) first value = 0.6270739405881613 (same PRNG as the Haptic Designer)", Math.Abs(first - 0.6270739405881613) < 1e-12, first.ToString("R"));
            var lpf = new Dsp.Biquad().SetLowpass(650); var pkf = new Dsp.Biquad().SetPeaking(250, 1.41, 6); var hpf = new Dsp.Biquad().SetHighpass(30);
            Check("RBJ biquads: LPF 650 passes 100 Hz / cuts 3 kHz, peaking +6 dB = x2.0, HPF 30 cuts 8 Hz",
                  Math.Abs(lpf.Magnitude(100) - 1) < 0.02 && lpf.Magnitude(3000) < 0.06 && Math.Abs(pkf.Magnitude(250) - 1.995) < 0.02 && hpf.Magnitude(8) < 0.1,
                  $"{lpf.Magnitude(100):F3} {lpf.Magnitude(3000):F3} {pkf.Magnitude(250):F3} {hpf.Magnitude(8):F3}");
            var sl = new float[9600]; var sr2 = new float[9600];
            for (int i = 0; i < sl.Length; i++) { sl[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 200 * i / 48000.0)); sr2[i] = -sl[i] * 0.5f; }
            var (wr, wch) = Dsp.DecodeWav(Dsp.EncodeWav(sl, sr2, 48000, quad: false, bits: 32));
            var (qr, qch) = Dsp.DecodeWav(Dsp.EncodeWav(sl, sr2, 48000, quad: true, bits: 16));
            Check("WAV: 32-bit float stereo round trip exact; quad = speaker ch1/ch2 silent, actuators ch3/ch4",
                  wr == 48000 && wch.Length == 2 && wch[0].SequenceEqual(sl) && wch[1].SequenceEqual(sr2) && qch.Length == 4 && qch[0].All(x => x == 0) && qch[1].All(x => x == 0) && Math.Abs(qch[2][2400] - sl[2400]) < 1e-4);
            var mm = Analysis.Measure(sl, sl);
            Check("analysis: 200 Hz sine 0.5 -> rms20 0.354, dominant 200 Hz, 40-400 Hz > 95 %, balance 0 dB, no clipping",
                  Math.Abs(mm.Rms20 - 0.3536) < 0.005 && Math.Abs(mm.DominantHz - 200) < 12 && mm.Core + mm.Low > 0.95 && Math.Abs(mm.BalanceDb) < 0.01 && mm.Clipped == 0, mm.Short());
            var fcfgPath = HapticConfig.Locate(null);
            if (fcfgPath != null)
            {
                var fcfg = HapticConfig.Load(fcfgPath);
                var recipe = Path.Combine(fcfg.Root, "forge", "lockpick.forge.json");
                if (File.Exists(recipe))
                {
                    var (fok, ftext, fres) = Forge.Run(new Forge.Options { Recipe = recipe, CheckOnly = true, Quiet = true });
                    Check($"forge lockpick recipe: {fres.Count} slot x variant renderings pass every check (clipping, DC, edges, duration, attack, rms20, spectrum, balance, contrast)", fok,
                          string.Join("; ", fres.Where(r => !r.Ok).Take(3).Select(r => $"{r.Slot}.{r.Variant}: {string.Join(", ", r.Fails)}")));
                    var variants = SampleBank.VariantsOnDisk("lp");
                    if (variants.Count > 0)
                    {
                        var v0 = variants[0];
                        var sp = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["name"] = "guide", ["variant"] = v0 };
                        var (gl, gr) = SampleBank.RenderPreset(sp, 48000);
                        sp["count"] = 5.0; sp["intervalMs"] = 10.0;   // 5 overlapping clicks: the preset's soft knee keeps the sum below 0.95
                        var (cl, cr) = SampleBank.RenderPreset(sp, 48000);
                        SampleBank.OverrideAll(SampleBank.Legacy);
                        var (ll, _) = Presets.RenderLayer("Sample", new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["name"] = "guide" }, 48000);
                        var (tl, _) = Presets.RenderLayer("Tick", new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["ifVariant"] = "lp.guide:legacy" }, 48000);
                        SampleBank.OverrideAll(v0);
                        var (tl2, _) = Presets.RenderLayer("Tick", new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["ifVariant"] = "lp.guide:legacy" }, 48000);
                        var (sl3, _) = Presets.RenderLayer("Sample", new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["name"] = "guide" }, 48000);
                        SampleBank.OverrideAll(null);
                        Check($"Sample preset: bank lp/guide.{v0} plays; 5 stacked stay < 0.94; 'legacy' -> sample silent + legacy layer plays; variant -> the reverse",
                              gl.Length > 1000 && gl.Max(Math.Abs) <= 0.86f && cl.Max(Math.Abs) < 0.94f && cr.Max(Math.Abs) < 0.94f && ll.Length == 0 && tl.Length > 0 && tl2.Length == 0 && sl3.Length > 0,
                              $"len {gl.Length}, pk {gl.Max(Math.Abs):F2}, stacked {cl.Max(Math.Abs):F2}, legacy sample {ll.Length}, legacy tick {tl.Length}/{tl2.Length}");
                    }
                }
            }
        }

        foreach (var test in LpGuideTest.SelfChecks()) Check(test.name, test.ok);
        foreach (var test in LpSelectedChecks.Run()) Check(test.name, test.ok);
        var galleryTestPath = HapticConfig.Locate(null);
        if (galleryTestPath != null)
        {
            var galleryCfg = HapticConfig.Load(galleryTestPath);
            if (File.Exists(Path.Combine(galleryCfg.Root, LpHeightGallery.Pack)))
            {
                galleryCfg.AddSupplementalPack(LpHeightGallery.Pack);
                bool withConfirm = File.Exists(Path.Combine(galleryCfg.Root, LpHeightGallery.ConfirmPack));
                if (withConfirm) galleryCfg.AddSupplementalPack(LpHeightGallery.ConfirmPack);
                foreach (var test in LpHeightGallery.Checks(galleryCfg, withConfirm)) Check(test.name, test.ok);
            }
        }
        var unlockTestPath = HapticConfig.Locate(null);
        if (unlockTestPath != null)
        {
            var unlockCfg = HapticConfig.Load(unlockTestPath);
            if (File.Exists(Path.Combine(unlockCfg.Root, UnlockGallery.Pack)))
            {
                unlockCfg.AddSupplementalPack(UnlockGallery.Pack);
                foreach (var test in UnlockGallery.Checks(unlockCfg)) Check(test.name, test.ok);
            }
        }
        if(unlockTestPath!=null)
        {
            var clickRecipe=Path.Combine(Path.GetDirectoryName(unlockTestPath)!,TriggerClickGallery.Pack);
            if(File.Exists(clickRecipe))
                foreach(var test in TriggerClickGallery.Checks(TriggerClickGallery.Read(clickRecipe))) Check(test.name,test.ok);
            if(File.Exists(Path.Combine(Path.GetDirectoryName(unlockTestPath)!,TriggerClickGallery.HdPack)))
            {
                var clickHdCfg=HapticConfig.Load(unlockTestPath);clickHdCfg.AddSupplementalPack(TriggerClickGallery.HdPack);
                foreach(var test in TriggerClickGallery.HdChecks(clickHdCfg))Check(test.name,test.ok);
            }
            var probeRecipe=Path.Combine(Path.GetDirectoryName(unlockTestPath)!,"experiments/trigger-vibration-probe.json");
            if(File.Exists(probeRecipe))
                foreach(var test in TriggerClickGallery.ProbeChecks(TriggerClickGallery.Read(probeRecipe))) Check(test.name,test.ok);
        }
        if(unlockTestPath!=null && File.Exists(Path.Combine(Path.GetDirectoryName(unlockTestPath)!,FailureGallery.Pack)))
        {
            var failureCfg=HapticConfig.Load(unlockTestPath);failureCfg.AddSupplementalPack(FailureGallery.Pack);
            foreach(var test in FailureGallery.Checks(failureCfg))Check(test.name,test.ok);
        }
        if(unlockTestPath!=null && File.Exists(Path.Combine(Path.GetDirectoryName(unlockTestPath)!,FailureGallery.SyncPack)))
        {
            var syncCfg=HapticConfig.Load(unlockTestPath);syncCfg.AddSupplementalPack(FailureGallery.SyncPack);
            foreach(var test in FailureGallery.SyncChecks(syncCfg))Check(test.name,test.ok);
        }
        Console.WriteLine("Haptic production line (montage classes, situations, game-sound forge, candidate packs):");
        {
            var tmpI = Path.Combine(Path.GetTempPath(), "dsb_intel_" + Environment.ProcessId);
            Directory.CreateDirectory(tmpI);
            try
            {
                // montage catalog: exact name, name cut short by the title transport (unique prefix), unknown name
                File.WriteAllText(Path.Combine(tmpI, "montages.generated.json"),
                    "{ \"montages\": { \"A_Man_S1H_Un_Draw_Montage\": { \"mc\": \"weapon_draw\", \"mw\": \"s1h\", \"mconf\": \"high\" }, \"A_Man_S1H_Un_Shelve_Montage\": { \"mc\": \"weapon_sheathe\" } } }");
                var mcat = MontageCatalog.Load(Path.Combine(tmpI, "montages.generated.json"));
                string? Mc(string m) => mcat.Annotate(EventMonitor.ParseRecord($"1:montage:start:t=1,src=t,m={m}")!).Get("mc");
                Check("montage catalog: exact name -> class, truncated name -> unique prefix, unknown stays unknown (never guessed)",
                      Mc("A_Man_S1H_Un_Draw_Montage") == "weapon_draw" && Mc("A_Man_S1H_Un_Shel") == "weapon_sheathe" && Mc("A_Man_S1H_Un_") == "unknown" && Mc("AnimMontage_123") == "unknown");

                // game sound -> forge "audio" layer: a synthetic 'sound' (3 hits + a noisy tail) through every method
                var snd = new float[48000]; var rnd = new Random(5);
                foreach (var at in new[] { 2400, 14400, 28800 })
                    for (int i = 0; i < 4000; i++) snd[at + i] += (float)(0.8 * Math.Exp(-i / 600.0) * (rnd.NextDouble() * 2 - 1));
                for (int i = 30000; i < 46000; i++) snd[i] += (float)(0.15 * (rnd.NextDouble() * 2 - 1) * (0.5 + 0.5 * Math.Sin(i / 300.0)));
                var wavDir = Path.Combine(tmpI, "wav"); Directory.CreateDirectory(wavDir);
                File.WriteAllBytes(Path.Combine(wavDir, "7_SFX_Test_Hit_01.wav"), Dsp.EncodeWav(snd, snd, 48000));
                var feat = AudioHaptics.Analyze(Path.Combine(wavDir, "7_SFX_Test_Hit_01.wav"));
                var methods = new[] { "impact", "click", "rattle", "scrape", "friction", "texture" };
                var slotsJ = string.Join(",", methods.Select(m => $"\"{m}\": {{ \"lengthMs\": 1100, \"layers\": [ {{ \"type\": \"audio\", \"sound\": \"7_SFX_Test_Hit_01\", \"method\": \"{m}\" }} ], \"check\": {{ \"minMs\": 5, \"maxMs\": 1500, \"minCore\": 0.5 }} }}"));
                Directory.CreateDirectory(Path.Combine(tmpI, "haptics", "forge"));
                var rec = Path.Combine(tmpI, "haptics", "forge", "t.forge.json");
                File.WriteAllText(rec, $"{{ \"bank\": \"t\", \"audioDir\": \"../../wav\", \"variants\": {{ \"A\": {{ \"vars\": {{}} }} }}, \"slots\": {{ {slotsJ} }} }}");
                var (aok, atext, ares) = Forge.Run(new Forge.Options { Recipe = rec, CheckOnly = true, Quiet = true });
                Check($"audio -> HD: analysis finds the 3 hits ({feat.Onsets}), all 6 methods render and pass the forge checks incl. the Bluetooth trip",
                      feat.Onsets >= 3 && aok && ares.Count == 6 && ares.All(r => r.L.Length > 0 && r.BtRms20 > 0),
                      string.Join("; ", ares.Where(r => !r.Ok).Select(r => r.Slot + ": " + string.Join(",", r.Fails))));
                var imp = ares.First(r => r.Slot == "impact"); var tex = ares.First(r => r.Slot == "texture");
                Check("methods differ: impact keeps the transients (crest), texture is continuous (lower crest)", imp.M.Crest > tex.M.Crest, $"{imp.M.Crest:F1} vs {tex.M.Crest:F1}");
            }
            finally { try { Directory.Delete(tmpI, true); } catch { } }

            // situation: two sources of one physical moment play one effect; a candidate pack's actions exist only while it is on
            var tmpS = Path.Combine(Path.GetTempPath(), "dsb_sit_" + Environment.ProcessId);
            Directory.CreateDirectory(Path.Combine(tmpS, "packs"));
            try
            {
                File.WriteAllText(Path.Combine(tmpS, "haptics.json"), "{ \"packs\": [ { \"file\": \"packs/c.json\", \"enabled\": false } ] }");
                File.WriteAllText(Path.Combine(tmpS, "packs", "c.json"), "{ \"pack\": \"cand\", \"actions\": [ { \"id\": \"box\", \"start\": \"interact:start\", \"steps\": [\"lp:start\"], \"end\": [\"interact:complete\"] } ], " +
                    "\"rules\": [ { \"id\": \"a\", \"event\": \"attack:update\", \"situation\": \"melee.contact\", \"effect\": { \"preset\": \"Tick\" } }, " +
                    "{ \"id\": \"b\", \"event\": \"dmgcaused:start\", \"situation\": \"melee.contact\", \"effect\": { \"preset\": \"Tick\" } }, " +
                    "{ \"id\": \"o\", \"event\": \"interact:complete\", \"where\": { \"act_box_prev\": \"interact_start\" }, \"effect\": { \"preset\": \"Tick\" } } ] }");
                var offCfg = HapticConfig.Load(Path.Combine(tmpS, "haptics.json"));
                HapticConfig.EnablePacks.Add("cand");
                var on = HapticConfig.Load(Path.Combine(tmpS, "haptics.json"));
                HapticConfig.EnablePacks.Remove("cand");
                var rt = new HapticRuntime(on, new OfflineHd(), new OfflineMotor());
                var ts0 = DateTime.UtcNow;
                var d1 = rt.Handle(EventMonitor.ParseRecord("1:attack:update:t=1,src=t,res=contact")!, ts0);
                var d2 = rt.Handle(EventMonitor.ParseRecord("2:dmgcaused:start:t=2,src=t")!, ts0.AddMilliseconds(20));
                var d3 = rt.Handle(EventMonitor.ParseRecord("3:dmgcaused:start:t=3,src=t")!, ts0.AddMilliseconds(400));
                Check("situation melee.contact: second source 20 ms later suppressed, a new contact 400 ms later plays",
                      d1.Any(d => d.RuleId == "a" && d.Route == "hd") && d2.Any(d => d.RuleId == "b" && d.Route == "situation") && d3.Any(d => d.RuleId == "b" && d.Route == "hd"));
                rt.Handle(EventMonitor.ParseRecord("4:interact:start:t=4,src=t")!, ts0.AddSeconds(1));
                var plain = rt.Handle(EventMonitor.ParseRecord("5:interact:complete:t=5,src=t")!, ts0.AddSeconds(2));
                rt.Handle(EventMonitor.ParseRecord("6:interact:start:t=6,src=t")!, ts0.AddSeconds(3));
                rt.Handle(EventMonitor.ParseRecord("7:lp:start:t=7,src=t")!, ts0.AddSeconds(4));
                var picked = rt.Handle(EventMonitor.ParseRecord("8:interact:complete:t=8,src=t")!, ts0.AddSeconds(5));
                Check("candidate pack: off by default (no rules, no actions), on with --enable-pack; its own action tells an unlocked container from a picked lock",
                      offCfg.Rules.Count == 0 && offCfg.Actions.Defs.All(x => x.Id != "box") && on.Rules.Count == 3 && plain.Any(d => d.RuleId == "o") && picked.All(d => d.RuleId != "o"));

                // situationCheckOnly (19.11.2026): the motor swing yields to a combo blow but never blocks the next swing itself
                File.WriteAllText(Path.Combine(tmpS, "haptics.json"), "{ \"packs\": [ { \"file\": \"packs/s.json\", \"enabled\": true } ] }");
                File.WriteAllText(Path.Combine(tmpS, "packs", "s.json"), "{ \"pack\": \"sw\", \"rules\": [ " +
                    "{ \"id\": \"combo\", \"event\": \"montage:start\", \"situation\": \"melee.swing\", \"effect\": { \"preset\": \"Tick\" } }, " +
                    "{ \"id\": \"swing\", \"event\": \"attack:start\", \"path\": \"rumble\", \"situation\": \"melee.swing\", \"situationMs\": 900, \"situationCheckOnly\": true, " +
                    "\"effect\": { \"preset\": \"Rumble\", \"steps\": [[0, 0, 230, 110]] } } ] }");
                var rs = new HapticRuntime(HapticConfig.Load(Path.Combine(tmpS, "haptics.json")), new OfflineHd(), new OfflineMotor());
                var s1 = rs.Handle(EventMonitor.ParseRecord("1:attack:start:t=1,src=t,dir=left")!, ts0);
                var s2 = rs.Handle(EventMonitor.ParseRecord("2:attack:start:t=2,src=t,dir=left")!, ts0.AddMilliseconds(300));
                rs.Handle(EventMonitor.ParseRecord("3:montage:start:t=3,src=t,m=x")!, ts0.AddMilliseconds(1000));
                var s3 = rs.Handle(EventMonitor.ParseRecord("4:attack:start:t=4,src=t,dir=left")!, ts0.AddMilliseconds(1010));
                {
                    // v48 mic blow: loud wind noise 500 ms -> whistle; a loud sung 200 Hz tone, quiet noise, a 100 ms bump -> nothing
                    var rnd = new Random(7);
                    float[] Noise(int ms, double amp) { var a = new float[48 * ms]; for (int i = 0; i < a.Length; i++) a[i] = (float)((rnd.NextDouble() * 2 - 1) * amp); return a; }
                    float[] Tone(int ms, double amp) { var a = new float[48 * ms]; for (int i = 0; i < a.Length; i++) a[i] = (float)(amp * Math.Sin(2 * Math.PI * 200 * i / 48000.0) + amp * 0.3 * Math.Sin(2 * Math.PI * 400 * i / 48000.0)); return a; }
                    bool Fires(float[] x) { var d = new BlowDetector(); bool f = false; for (int o = 0; o < x.Length; o += 480) f |= d.Feed(x.AsSpan(o, Math.Min(480, x.Length - o)), 48000, o / 48); return f; }
                    var quiet = Noise(400, 0.01); var bump = Noise(100, 0.6); var after = Noise(400, 0.01);
                    Check("mic blow: loud wind noise 500 ms fires; voiced tone, quiet noise, a 100 ms bump do not",
                          Fires(Noise(500, 0.5)) && !Fires(Tone(800, 0.5)) && !Fires(Noise(800, 0.01)) && !Fires(quiet.Concat(bump).Concat(after).ToArray()));
                }
                Check("situationCheckOnly: swings 300 ms apart both play; a swing 10 ms after a combo blow yields",
                      s1.Any(d => d.RuleId == "swing" && d.Route == "rumble") && s2.Any(d => d.RuleId == "swing" && d.Route == "rumble")
                      && s3.Any(d => d.RuleId == "swing" && d.Route == "situation"));

                // onsetLead (19.11.2026): slow-onset bank samples start earlier; silence is cut whole, a swell by <= 250 ms
                Directory.CreateDirectory(Path.Combine(tmpS, "bank", "t"));
                int sr0 = 48000;
                float[] Wave(int silentMs, int swellMs) { var w = new float[sr0 * (silentMs + swellMs + 50) / 1000]; int a = sr0 * silentMs / 1000, b = sr0 * swellMs / 1000;
                    for (int i = a; i < w.Length; i++) w[i] = (float)(0.8 * Math.Min(1.0, b == 0 ? 1.0 : (i - a) / (double)b) * Math.Sin(2 * Math.PI * 150 * i / sr0)); return w; }
                var gap = Wave(100, 0); File.WriteAllBytes(Path.Combine(tmpS, "bank", "t", "gap.A.wav"), Dsp.EncodeWav(gap, gap, sr0));
                var swell = Wave(0, 1000); File.WriteAllBytes(Path.Combine(tmpS, "bank", "t", "swell.A.wav"), Dsp.EncodeWav(swell, swell, sr0));
                string Lay(string n, int d) => $"{{ \"preset\": \"Sample\", \"bank\": \"t\", \"name\": \"{n}\", \"slot\": \"t.{n}\", \"variant\": \"A\", \"delayMs\": {d} }}";
                File.WriteAllText(Path.Combine(tmpS, "packs", "o.json"), "{ \"pack\": \"ol\", \"rules\": [ " +
                    $"{{ \"id\": \"g200\", \"event\": \"x:start\", \"effect\": {{ \"layers\": [ {Lay("gap", 200)} ] }} }}, " +
                    $"{{ \"id\": \"g0\", \"event\": \"y:start\", \"effect\": {{ \"layers\": [ {Lay("gap", 0)} ] }} }}, " +
                    $"{{ \"id\": \"sw\", \"event\": \"z:start\", \"effect\": {{ \"layers\": [ {Lay("swell", 0)} ] }} }}, " +
                    "{ \"id\": \"own\", \"event\": \"w:start\", \"effect\": { \"layers\": [ { \"preset\": \"Sample\", \"bank\": \"t\", \"name\": \"gap\", \"variant\": \"A\", \"offsetMs\": 0 } ] } } ] }");
                double First(HapticConfig c, string id, double thr)
                {
                    var r = c.Rules.First(x => x.Id == id);
                    var (l, _) = HapticRuntime.RenderHd(r.Effect, _ => null, sr0);
                    float pk = l.Length == 0 ? 0 : l.Max(Math.Abs);
                    int i = Array.FindIndex(l, v => Math.Abs(v) >= thr * pk);
                    return i < 0 ? -1 : 1000.0 * i / sr0;
                }
                foreach (var lead in new[] { false, true })
                    File.WriteAllText(Path.Combine(tmpS, lead ? "on.json" : "off.json"),
                        "{ \"packs\": [ { \"file\": \"packs/o.json\", \"enabled\": true" + (lead ? ", \"onsetLead\": true" : "") + " } ] }");
                var olOff = HapticConfig.Load(Path.Combine(tmpS, "off.json"));
                var olOn = HapticConfig.Load(Path.Combine(tmpS, "on.json"));
                double a200 = First(olOff, "g200", .25), b200 = First(olOn, "g200", .25), z0 = First(olOn, "g0", .25), own = First(olOn, "own", .25);
                double swOff = First(olOff, "sw", .25), swOn = First(olOn, "sw", .25);
                Check($"onsetLead: 100 ms silent lead cut (delay 200: {a200:F0} -> {b200:F0} ms; delay 0: {z0:F0} ms), own offsetMs untouched ({own:F0} ms), " +
                      $"1 s swell: 40 ms quiet + 200 ms of swell ({swOff:F0} -> {swOn:F0} ms); off by default",
                      olOff.OnsetLeadPacks.Count == 0 && olOn.OnsetLeadPacks.Contains("ol") && Math.Abs(a200 - 300) < 2 && Math.Abs(b200 - 210) < 2
                      && Math.Abs(z0 - 10) < 2 && Math.Abs(own - 100) < 2 && Math.Abs(swOff - swOn - 240) < 4);

                // connection epoch (20.11.2026): a held trigger is not re-sent to a reconnected pad without a fresh event
                File.WriteAllText(Path.Combine(tmpS, "haptics.json"), "{ \"packs\": [ { \"file\": \"packs/p.json\", \"enabled\": true } ] }");
                File.WriteAllText(Path.Combine(tmpS, "packs", "p.json"), "{ \"pack\": \"pd\", \"triggerProfiles\": { \"tight\": { \"mode\": \"feedback\", \"pos\": 2, \"str\": 6 } }, " +
                    "\"rules\": [ { \"id\": \"hold\", \"event\": \"bowdraw:start\", \"path\": \"rumble\", \"effect\": { \"preset\": \"Rumble\", \"steps\": [[0, 10, 10, 20]] }, \"pad\": { \"r2\": \"tight\" } } ] }");
                bool lost = false; var writes = new List<(byte[]? l, byte[]? r)>();
                var po = new PadOutLive(() => null) { Sink = (l, r) => { if (!lost) writes.Add((l, r)); } };
                var re = new HapticRuntime(HapticConfig.Load(Path.Combine(tmpS, "haptics.json")), new OfflineHd(), new OfflineMotor()) { Pad = po };
                re.Handle(EventMonitor.ParseRecord("1:bowdraw:start:t=1,src=t")!, ts0);
                bool heldBefore = re.PadActive && writes.Count > 0 && writes[^1].r != null && writes[^1].r![0] != DualSense.EffectOff()[0];
                lost = true; re.DeviceLost("test"); lost = false; writes.Clear();
                po.ForceOff();
                po.Refresh(ts0.AddSeconds(5));
                bool offOnly = writes.Count == 1 && writes[0].l != null && writes[0].r != null
                               && writes[0].l!.SequenceEqual(DualSense.EffectOff()) && writes[0].r!.SequenceEqual(DualSense.EffectOff());
                // delivery lateness (20.11.2026): late events pull their scheduled HD layers back, only out of leading silence
                var lc = new HapticRuntime.LateClock();
                double[] lates = { lc.Observe(1000, 900), lc.Observe(1010, 905), lc.Observe(1030, 900), lc.Observe(9000, 1000), lc.Observe(9010, 1010) };
                bool clockOk = lates[0] == 0 && lates[1] == 5 && lates[2] == 30 && lates[3] == 0 && lates[4] == 0;   // jump (pause) -> new floor
                File.WriteAllText(Path.Combine(tmpS, "lt.json"), "{ \"packs\": [ { \"file\": \"packs/lt.json\", \"enabled\": true } ], \"limits\": { \"lateTrimTargetMs\": 12, \"lateTrimMaxMs\": 20 } }");
                File.WriteAllText(Path.Combine(tmpS, "packs", "lt.json"), "{ \"pack\": \"lt\", \"rules\": [ " +
                    "{ \"id\": \"sched\", \"event\": \"x:start\", \"path\": \"hd\", \"effect\": { \"preset\": \"Tick\", \"delayMs\": 50 } }, " +
                    "{ \"id\": \"now\", \"event\": \"y:start\", \"path\": \"hd\", \"effect\": { \"preset\": \"Tick\" } } ] }");
                var lens = new List<(string tag, int n)>();
                var lhd = new OfflineHd { OnPlay = (l, r, o) => lens.Add((o.Tag, Math.Max(l.Length, r.Length))) };
                var lrt2 = new HapticRuntime(HapticConfig.Load(Path.Combine(tmpS, "lt.json")), lhd, new OfflineMotor());
                var b0t = DateTime.UnixEpoch.AddMilliseconds(100000);
                lrt2.Handle(EventMonitor.ParseRecord("1:x:start:t=50000,src=t")!, b0t);                       // floor
                lrt2.Handle(EventMonitor.ParseRecord("2:x:start:t=50500,src=t")!, b0t.AddMilliseconds(530));  // 30 ms late -> 18 ms trim
                lrt2.Handle(EventMonitor.ParseRecord("3:y:start:t=51000,src=t")!, b0t.AddMilliseconds(1040)); // 40 ms late, no silence
                lrt2.Handle(EventMonitor.ParseRecord("4:x:start:t=51500,src=t")!, b0t.AddMilliseconds(1600)); // 100 ms late -> cap 20
                int n0 = lens[0].n, n1 = lens[1].n, n2y = lens[2].n, n3 = lens[3].n;
                Check($"late clock: floor / pause jump ok ({string.Join(",", lates)}); trim of scheduled silence {(n0 - n1) / 48.0:F1} ms (30 ms late) and {(n0 - n3) / 48.0:F1} ms (cap 20), instant voice untouched; off by default",
                      clockOk && Math.Abs((n0 - n1) / 48.0 - 18) < 0.1 && Math.Abs((n0 - n3) / 48.0 - 20) < 0.1 && lrt2.LateTrims == 2 && n2y > 0
                      && HapticConfig.Load(Path.Combine(tmpS, "haptics.json")).Limits.LateTrimMaxMs == 0);

                // approval signatures (20.11.2026): an approved pack that renders differently fails the check until re-approved / marked
                File.WriteAllText(Path.Combine(tmpS, "ap.json"), "{ \"packs\": [ { \"file\": \"packs/ap.json\", \"enabled\": true } ] }");
                string ApPack(double i, string note) => "{ \"pack\": \"ap\", \"rules\": [ { \"id\": \"a\", \"event\": \"x:start\", \"path\": \"hd\", \"note\": \"" + note +
                    "\", \"effect\": { \"preset\": \"Tick\", \"intensity\": " + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + " } } ] }";
                File.WriteAllText(Path.Combine(tmpS, "packs", "ap.json"), ApPack(0.5, "first"));
                Approvals.Record(HapticConfig.Load(Path.Combine(tmpS, "ap.json")), "ap", "approved", "test");
                File.WriteAllText(Path.Combine(tmpS, "packs", "ap.json"), ApPack(0.5, "note edited"));
                bool apNote = Approvals.Check(HapticConfig.Load(Path.Combine(tmpS, "ap.json"))).ok;
                File.WriteAllText(Path.Combine(tmpS, "packs", "ap.json"), ApPack(0.6, "note edited"));
                var apCfg = HapticConfig.Load(Path.Combine(tmpS, "ap.json"));
                bool apChanged = !Approvals.Check(apCfg).ok;
                Approvals.Record(apCfg, "ap", "retest", "stronger tick");
                bool apRetest = Approvals.Check(HapticConfig.Load(Path.Combine(tmpS, "ap.json"))) is var (rok2, rtx) && rok2 && rtx.Contains("retest");
                Check("approval signatures: a note edit keeps the approval, a stronger tick fails the check, 'retest' passes and is listed",
                      apNote && apChanged && apRetest);

                Check($"connection epoch: held R2 ({heldBefore}) released on pad loss; after reconnect one 'off' report, no stale resistance re-sent ({writes.Count} writes)",
                      heldBefore && !re.PadActive && offOnly && re.DeviceLosses == 1);
            }
            finally { try { Directory.Delete(tmpS, true); } catch { } }
        }

        Console.WriteLine("Test checklist in the game (TestHud -> DsB_TestHud.txt):");
        {
            var tmpH = Path.Combine(Path.GetTempPath(), "dsb_hud_" + Environment.ProcessId);
            Directory.CreateDirectory(tmpH);
            try
            {
                File.WriteAllText(Path.Combine(tmpH, "plan.json"), "{ \"title\": \"Тест\", \"tasks\": [ " +
                    "{ \"text\": \"Достать лук\", \"on\": [\"rule:pilot-weapon-draw-*\"] }, " +
                    "{ \"text\": \"Шаги крадучись\", \"on\": [\"event:step:update[gait=crouch]\"] } ] }");
                var hud = TestHud.Load(Path.Combine(tmpH, "plan.json"), tmpH, Path.Combine(tmpH, "rep.json"), _ => { });
                var tH = DateTime.UtcNow;
                string Read() => File.ReadAllText(Path.Combine(tmpH, "DsB_TestHud.txt"));
                hud.Flush(tH, true);
                var s0 = Read();
                var draw = EventMonitor.ParseRecord("1:montage:start:t=1,src=t,m=x")!;
                hud.Observe(draw, new[] { new Decision("pilot-weapon-draw-draw-bow", "hd", "") }, tH);
                hud.Observe(draw, new[] { new Decision("pilot-weapon-draw-draw-bow", "hd", "") }, tH);
                hud.Observe(draw, new[] { new Decision("pilot-weapon-draw-draw-bow", "cooldown", "") }, tH);   // not played: not counted
                hud.Observe(EventMonitor.ParseRecord("2:step:update:t=2,src=t,mat=PM_Soil,gait=walk")!, Array.Empty<Decision>(), tH);
                hud.Flush(tH.AddSeconds(1), true);
                var s1 = Read();
                hud.Toggle(); hud.Flush(tH.AddSeconds(2), true);
                var s2 = Read();
                Check("checklist: todo -> done (green, mark), a repeat shows (1); not-played effects and other events do not count; SHARE hides",
                      s0.Contains("show=1") && s0.Contains("task=0|○ Достать лук") && s1.Contains("task=1|✓ (1) Достать лук") && s1.Contains("task=0|○ Шаги крадучись")
                      && s1.Contains("1/2") && s2.Contains("show=0") && File.Exists(Path.Combine(tmpH, "rep.json")),
                      s1.Replace("\n", " / "));
                var none = TestHud.Load(null, tmpH, Path.Combine(tmpH, "rep2.json"), _ => { });
                none.Flush(tH, true);
                Check("no plan (normal play): the list file says show=0 and no report is written", Read().StartsWith("show=0") && !File.Exists(Path.Combine(tmpH, "rep2.json")));
            }
            finally { try { Directory.Delete(tmpH, true); } catch { } }
        }

        var feedbackPath=HapticConfig.Locate(null);
        if(feedbackPath!=null){var feedbackCfg=HapticConfig.Load(feedbackPath);foreach(var test in FeedbackChecks.Run(feedbackCfg))Check(test.name,test.ok);}
        if (feedbackPath != null) foreach (var test in ThreatLightChecks.Run(HapticConfig.Load(feedbackPath))) Check(test.name, test.ok);
        foreach (var (name, ok) in TouchpadChecks.Run()) Check(name, ok);
        Console.WriteLine(fails == 0 ? "ALL PASS" : $"{fails} FAILED");
        return fails == 0;
    }
}

static class XInput
{
    [StructLayout(LayoutKind.Sequential)] public struct State { public uint Packet; public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }
    [StructLayout(LayoutKind.Sequential)] struct Vibration { public ushort Left, Right; }
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")] static extern uint XGetState(uint i, out State s);
    [DllImport("xinput1_4.dll", EntryPoint = "XInputSetState")] static extern uint XSetState(uint i, ref Vibration v);
    public static uint GetState(uint i, out State s) => XGetState(i, out s);
    public static uint SetState(uint i, ushort left, ushort right) { var v = new Vibration { Left = left, Right = right }; return XSetState(i, ref v); }
}

/// <summary>Live HD output for the haptic runtime (the engine may be replaced on reconnect).</summary>
sealed class HdOutLive : IHdOut
{
    public HdEngine? E;
    public bool Ready => E?.Ready == true;
    public int Rate => E?.Rate ?? 48000;
    public bool Active => E?.Mixer.Active == true;
    public long Play(float[] l, float[] r, VoiceOpts o) => E?.Play(l, r, o) ?? 0;
    public void Stop(long id, int fadeMs) => E?.Stop(id, fadeMs);
    public void StopAll() => E?.StopAll();
}

/// <summary>Live adaptive-trigger output: the state the rules set, re-sent every RefreshMs while a trigger is not off
/// (the game or a reconnect may overwrite the pad's trigger state). The pad object may be replaced on reconnect.</summary>
sealed class PadOutLive : IPadOut
{
    readonly Func<DualSense?> _pad; readonly object _l = new();
    TriggerProfile? _l2, _r2; DateTime _sent = DateTime.MinValue;
    public int RefreshMs = 1000; public Action<string>? Log;
    /// <summary>Every write (left, right; null = untouched) also goes here - selftest of the automaton without a pad.</summary>
    public Action<byte[]?, byte[]?>? Sink;

    /// <summary>One trigger's pulse automaton (trigger click "Сухой спуск", Codex TriggerClickGallery.Finger, driven by game
    /// events instead of HID polling): pending (delay) -> playing (the pulse replaces the trigger's profile; it always plays
    /// to the end, a release does not cut it) -> if the finger still holds the trigger: parked = off until the release;
    /// otherwise the stored profile comes back at once. While playing or parked, Triggers only stores that side.</summary>
    sealed class Side { public byte[]? Bytes; public DateTime Start, Until; public bool Pending, Playing, Parked, Held; }
    readonly Side _sl = new(), _sr = new();

    public PadOutLive(Func<DualSense?> pad) { _pad = pad; }
    bool Active => (_l2 != null && !_l2.IsOff) || (_r2 != null && !_r2.IsOff);
    static bool Busy(Side s) => s.Playing || s.Parked;
    byte[] Stored(bool left) => (left ? _l2 : _r2)?.Bytes() ?? DualSense.EffectOff();

    public void Triggers(TriggerProfile? l2, TriggerProfile? r2)
    {
        lock (_l)
        {
            if (l2 != null) _l2 = l2; if (r2 != null) _r2 = r2;
            var sl = Busy(_sl) ? null : l2?.Bytes(); var sr = Busy(_sr) ? null : r2?.Bytes();
            Send(sl, sr);
        }
    }
    public void Pulse(TriggerProfile pulse, int ms, bool left = true, bool right = true, int delayMs = 0, bool hold = false)
    {
        lock (_l)
        {
            var now = DateTime.UtcNow; var b = pulse.Bytes();
            foreach (var (side, on) in new[] { (_sl, left), (_sr, right) })
            {
                if (!on) continue;
                if (hold) side.Held = true;
                side.Bytes = b; side.Parked = false; side.Playing = false; side.Pending = true;
                side.Start = now.AddMilliseconds(Math.Max(0, delayMs)); side.Until = side.Start.AddMilliseconds(ms);
            }
            Step(now);
        }
    }
    public void Release(bool left, bool right)
    {
        lock (_l)
        {
            if (left) { _sl.Held = false; if (_sl.Parked) { _sl.Parked = false; Send(Stored(true), null); } }
            if (right) { _sr.Held = false; if (_sr.Parked) { _sr.Parked = false; Send(null, Stored(false)); } }
        }
    }
    /// <summary>A reconnected pad: both triggers off as its first trigger report, whatever the previous epoch held.</summary>
    public void ForceOff()
    {
        lock (_l)
        {
            _l2 = _r2 = null;
            foreach (var s in new[] { _sl, _sr }) { s.Pending = s.Playing = s.Parked = s.Held = false; s.Bytes = null; }
            Send(DualSense.EffectOff(), DualSense.EffectOff());
        }
    }
    public void Reset(string reason)
    {
        lock (_l)
        {
            bool was = Active || _l2 != null || _r2 != null || _sl.Pending || _sr.Pending || Busy(_sl) || Busy(_sr);
            _l2 = _r2 = null;
            foreach (var s in new[] { _sl, _sr }) { s.Pending = s.Playing = s.Parked = s.Held = false; s.Bytes = null; }
            if (was) Send(DualSense.EffectOff(), DualSense.EffectOff());
        }
    }
    /// <summary>Called from the main loop: pulse steps (no Sleep), and the current state re-sent every RefreshMs.</summary>
    public void Refresh(DateTime now)
    {
        lock (_l)
        {
            Step(now);
            if (Active && (now - _sent).TotalMilliseconds >= RefreshMs)
                Send(Busy(_sl) ? null : Stored(true), Busy(_sr) ? null : Stored(false));
        }
    }
    void Step(DateTime now)
    {
        for (int i = 0; i < 2; i++)
        {
            var s = i == 0 ? _sl : _sr; bool left = i == 0;
            if (s.Pending && now >= s.Start) { s.Pending = false; s.Playing = true; Send(left ? s.Bytes : null, left ? null : s.Bytes); }
            if (s.Playing && now >= s.Until)
            {
                s.Playing = false; s.Bytes = null;
                var back = s.Held ? DualSense.EffectOff() : Stored(left);   // a held finger: no resistance re-armed under it
                if (s.Held) s.Parked = true;
                Send(left ? back : null, left ? null : back);
            }
        }
    }
    void Send(byte[]? l, byte[]? r)
    {
        if (l == null && r == null) return;
        _sent = DateTime.UtcNow;
        Sink?.Invoke(l, r);
        try { _pad()?.SetTriggers(l, r); } catch (Exception ex) { Log?.Invoke("triggers: write failed: " + ex.Message); }
    }
}

/// <summary>Live lightbar + player LEDs. The first write takes the lightbar from the firmware (LIGHT_OUT); Reset puts the
/// idle colour back (--restore-light, default off) and turns the player LEDs off. Exit cleanup does the same.</summary>
sealed class LightOutLive : ILightOut
{
    readonly Func<DualSense?> _pad; readonly byte[] _idle;
    bool _taken, _touched;
    byte[]? _rgb; int? _leds;
    DualSense? _lastPad;
    DateTime _nextRefresh;
    public Action<string>? Log; public Action? Touched;
    public LightOutLive(Func<DualSense?> pad, byte[] idle) { _pad = pad; _idle = idle; }
    public void Light(byte[]? rgb, int? leds)
    {
        if (!_touched) { _touched = true; Touched?.Invoke(); }
        if (rgb != null) _rgb = rgb;
        if (leds != null) _leds = leds;
        Refresh(DateTime.UtcNow, true);
    }
    // Reapply after USB/BT reconnect, an external overwrite or a transient failed write.
    // Reports set only the light flags, leaving triggers, motors and audio untouched.
    public void Refresh(DateTime now, bool force = false)
    {
        if (!_touched) return;
        var p = _pad(); if (p == null) return;
        if (!ReferenceEquals(p, _lastPad)) { _lastPad = p; _taken = false; force = true; }
        if (!force && now < _nextRefresh) return;
        _nextRefresh = now.AddSeconds(1);
        try
        {
            if (_rgb != null) { p.SetLightbar(_rgb[0], _rgb[1], _rgb[2], releaseStartupLight: !_taken); _taken = true; }
            if (_leds != null) p.SetPlayerLeds(DualSense.PlayerLedPattern(_leds.Value));
        }
        catch (Exception ex) { Log?.Invoke("light: write failed: " + ex.Message); }
    }
    public void Reset(string reason)
    {
        if (!_touched) return;
        _rgb = _idle; _leds = 0;
        Refresh(DateTime.UtcNow, true);
    }
}

/// <summary>Light output for tests: remembers the last values.</summary>
sealed class OfflineLight : ILightOut
{
    public byte[]? Rgb; public int? Leds; public int Resets;
    public void Light(byte[]? rgb, int? leds) { if (rgb != null) Rgb = rgb; if (leds != null) Leds = leds; }
    public void Reset(string reason) { Rgb = null; Leds = null; Resets++; }
}

/// <summary>Trigger output for tests: remembers the last profiles.</summary>
sealed class OfflinePad : IPadOut
{
    public TriggerProfile? L2, R2; public int Resets; public TriggerProfile? LastPulse; public int PulseMs, Pulses;
    public void Triggers(TriggerProfile? l2, TriggerProfile? r2) { if (l2 != null) L2 = l2; if (r2 != null) R2 = r2; }
    public bool PulseL, PulseR; public int PulseDelayMs;
    public bool PulseHold; public int Releases;
    public void Pulse(TriggerProfile pulse, int ms, bool left = true, bool right = true, int delayMs = 0, bool hold = false) { LastPulse = pulse; PulseMs = ms; PulseL = left; PulseR = right; PulseDelayMs = delayMs; PulseHold = hold; Pulses++; }
    public void Release(bool left, bool right) { Releases++; }
    public void Reset(string reason) { L2 = R2 = null; Resets++; }
}

/// <summary>Live motor output (rumble-emulation reports through RumbleMixer).</summary>
sealed class MotorOutLive : IMotorOut
{
    readonly RumbleMixer _m;
    public MotorOutLive(RumbleMixer m) { _m = m; }
    public void Pattern((int delay, byte right, byte left, int ms)[] steps) => _m.Pattern(steps);
    public void StopAll() => _m.StopAll();
}
