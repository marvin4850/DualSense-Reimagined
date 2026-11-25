using System.Diagnostics;
using DsBridge.Haptics;

namespace DsBridge;

/// <summary>`lp-bench`: a stand for Tactile Lockpicking 2.0 without the game. It feeds synthetic "lp" events into the SAME
/// haptic runtime, rules (haptics/packs/30-lockpick.json), HD engine and trigger output as `run`, so every command, move and
/// trigger profile can be felt and compared, a blind recognition test of the four navigation commands can be run, and the
/// left / right HD actuators can be calibrated (haptics/calibration.json). No game action is ever sent.</summary>
static class LpBench
{
    const string Help = """

        Tactile Lockpicking 2.0 - bench (all through the real rules / HD / triggers; no game)
          Navigation:           1 = right plate, push LEFT  (centre click, then left click; L2 crisp)
                                2 = right plate, push RIGHT (centre click, then right click; R2 crisp)
                                3 = wrong plate, go UP = R1 (one right click)   4 = wrong plate, go DOWN = L1 (one left click)
                                0 = right plate, Trained (centre click only)
          Moves (press -> motion -> crossings -> settle, 300 ms):
                                5 = 1 plate            6 = 2 plates same way      7 = 4 plates (3 same + 1 opposite)
                                8 = 7 plates (5 + 2)   9 = 3 plates, one reaches the centre
          Results:              s = strain   b = break   o = unlock   x = close (triggers off)
                                u = Trained: useful move (settle + double click)   y = Trained links 1 / 2 / 3 / 4+ in turn   j = the same, last pulse = route's plate
                                t / l = end stop: R1 on the top plate / L1 on the bottom plate
                                i = pick life light 6 -> 5 -> ... -> 0 -> 6 (player LEDs + lightbar)
                                g = pick break scene v35 (fracture + handle, then the broken tip at 2356 ms)
          Trigger clicks:       a = compare: L2 soft (safe) / R2 crisp (right)   d = L2 wall (danger) / R2 crisp
                                w = wall on L2   e = wall on R2   n = both soft   v = next click variant set
                                m = Novice: L2 warn (medium) / R2 wall (maximum)
                                c = calibrate the click point (press slowly; Space = "clicked now")   - / = = fire point -/+
                                f = write the fire point to the mod (DsLp_Lockpick.ini)
          Blind test:           z = 20 random commands (answer 1-4)
          Calibration:          [ ] = left gain -/+   , . = right gain -/+   p = left/right comparison tick   k = save
          h = help   q = quit
        """;

    // click variant sets for A/B: (right direction, safe direction, danger) - profile names of the pack
    static readonly (string ok, string soft, string wall)[] Sets =
    {
        ("click_ok", "click_soft", "wall"),
        ("click_ok_w", "click_soft", "wall"),
        ("click_ok_b", "click_soft_b", "wall_hard"),
        ("click_ok", "click_soft_b", "wall"),
    };

    public static void Run(HapticConfig cfg, DualSense pad, Action<string> log, string modDir)
    {
        var rumble = new RumbleMixer(pad);
        var hd = HdEngine.TryStart(pad, log);
        if (hd != null) { Replay.ApplyLimits(hd.Mixer, cfg.Limits); hd.StallMs = cfg.Limits.StallMs; }
        else log("HD haptics not available (Bluetooth or no audio device): the motor versions are played");
        var padOut = new PadOutLive(() => pad) { Log = log };
        var lightOut = new LightOutLive(() => pad, new byte[] { 0, 0, 255 }) { Log = log };   // v41: the pick-life light (key i); reset = blue idle
        var rt = new HapticRuntime(cfg, new HdOutLive { E = hd }, new MotorOutLive(rumble)) { Log = log, Pad = padOut, LightOut = lightOut };
        int seq = 0, tx = 0, gen = 0;
        int set = 0;
        string Ok() => Sets[set].ok;
        string Soft() => Sets[set].soft;
        string Wall() => Sets[set].wall;
        double firePoint = ReadFirePoint(modDir);
        bool quiet = false;
        int linksK = 0;
        int livesN = 6;

        void Ev(string type, string phase, string ctx)
        {
            var e = EventMonitor.ParseRecord($"{++seq}:{type}:{phase}:t={Environment.TickCount64 % 100000000},src=bench{(ctx.Length > 0 ? "," + ctx : "")}");
            if (e == null) { log("bad record: " + ctx); return; }
            var d = rt.Handle(e, DateTime.UtcNow);
            if (!quiet) log($"  {type}:{phase} {ctx} -> {(d.Count == 0 ? "(no rule)" : string.Join("; ", d))}");
        }
        void Wait(int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms) { rumble.Update(); rt.Tick(DateTime.UtcNow); padOut.Refresh(DateTime.UtcNow); Thread.Sleep(2); }
        }
        void Trig(string l, string r, string next) => Ev("lp", "update", $"ph=trig,l={l},r={r},next={next},skill=2");
        void Guide(string kind, string side, string next) => Ev("lp", "update", $"ph=guide,gen={++gen},kind={kind},side={side},next={next},cur=1,prof=2");
        void Nav(int cmd)   // 1 L1, 2 R1, 3 L2, 4 R2
        {
            switch (cmd)
            {
                case 1: Trig(Ok(), Soft(), "A"); Guide("plate", "L", "A"); break;
                case 2: Trig(Soft(), Ok(), "D"); Guide("plate", "R", "D"); break;
                case 3: Trig(Soft(), Soft(), "W"); Guide("sel", "R", "W"); break;
                case 4: Trig(Soft(), Soft(), "S"); Guide("sel", "L", "S"); break;
                case 5: Trig(Soft(), Soft(), "A"); Guide("plate", "-", "A"); break;
            }
        }
        void Move(string dir, int same, int opp, int cin)
        {
            int ch = same + opp, id = ++tx;
            Ev("lp", "update", $"ph=press,tx={id},dir={dir},cur=1,valid=1,ch={ch},same={same},opp={opp},cin={cin},cout=0,mask=0,conf=predicted");
            Wait(60);
            Ev("lp", "update", $"ph=motion,stage=start,tx={id},dir={dir},ch={ch},same={same},opp={opp},est=300,pm=60,conf=observed");
            string load = opp > 0 ? "loadopp" : ch >= 2 ? "loadn" : "load1";
            if (dir == "L") Ev("lp", "update", $"ph=trig,l={load},r={Soft()},next=none,skill=2,load={load}");
            else Ev("lp", "update", $"ph=trig,l={Soft()},r={load},next=none,skill=2,load={load}");
            Wait(140);
            Ev("lp", "update", $"ph=cross,tx={id},dir={dir},k={same},same={same},opp=0,mask=0,p=500,ch={ch},t=140");
            if (opp > 0) { Wait(25); Ev("lp", "update", $"ph=cross,tx={id},dir={dir},k={opp},same=0,opp={opp},mask=0,p=520,ch={ch},t=165"); }
            Wait(opp > 0 ? 135 : 160);
            Ev("lp", "update", $"ph=settle,status=accepted,tx={id},ch={ch},dir={dir},ctr={1 + cin},n=5,cin={cin},cout=0,unx=0,mismatch=0,pm=60,md=300");
            Trig(Soft(), Soft(), "none");
        }
        void Show() => log($"calibration: left {cfg.Calib.GainL:0.00}, right {cfg.Calib.GainR:0.00}; clicks: right = {Ok()}, safe = {Soft()}, danger = {Wall()}; fire point {firePoint:0.00}; HD {(hd?.Ready == true ? "ON" : "off")}");

        Ev("lp", "start", "lock=bench,pl=5,prec=2,cut=2,src=bench,known=1");
        Ev("lp", "update", "ph=ready,ctr=1,n=5,holes=75775");
        Wait(300);
        Console.WriteLine(Help);
        Show();
        while (true)
        {
            var key = Console.ReadKey(true).KeyChar;
            switch (key)
            {
                case '1': case '2': case '3': case '4': Nav(key - '0'); break;
                case '0': Nav(5); break;
                case '5': Move("R", 1, 0, 0); break;
                case '6': Move("R", 2, 0, 0); break;
                case '7': Move("L", 3, 1, 0); break;
                case '8': Move("R", 5, 2, 0); break;
                case '9': Move("L", 3, 0, 1); break;
                case 's': Ev("lp", "update", $"ph=press,tx={++tx},dir=R,cur=1,valid=0,ch=0,same=0,opp=0,cin=0,cout=0,mask=0,conf=predicted"); Ev("lp", "update", $"ph=strain,dir=R,cur=1,tx={tx},conf=derived"); break;
                case 'b': Ev("lp", "update", $"ph=settle,status=break,tx=0,ch=4,dir=0,ctr=1,n=5,cin=0,cout=1,unx=0,mismatch=0,pm=0,md=0"); break;
                case 'u': Ev("lp", "update", "ph=settle,status=accepted,tx=1,ch=2,dir=R,ctr=1,n=5,cin=1,cout=0,unx=0,mismatch=0,pm=60,md=280"); Ev("lp", "update", "ph=progress,tx=1,d0=7,d1=6,prof=1"); log("  Trained: a useful move - settle, then the double click (170 / 235 ms)"); break;
                case 't': Ev("lp", "update", "ph=navpress,k=W,cur=4,nav=1,requested=1,edge=1"); log("  end stop: R1 on the top plate (right hand)"); break;
                case 'l': Ev("lp", "update", "ph=navpress,k=S,cur=0,nav=1,requested=1,edge=1"); log("  end stop: L1 on the bottom plate (left hand)"); break;
                case 'j': { int k = linksK % 4 + 1; linksK++; Ev("lp", "update", "ph=sel,cur=1,n=5"); Ev("lp", "update", $"ph=links,cur=1,k={k},moved={k},goal=1,prof=1"); log($"  Trained links: {k}{(k == 4 ? "+" : "")} plate(s), the last pulse = the route's plate (j again = next count)"); break; }
                case 'y': { int k = linksK % 4 + 1; linksK++; Ev("lp", "update", "ph=sel,cur=1,n=5"); Ev("lp", "update", $"ph=links,cur=1,k={k},moved={k},prof=1"); log($"  Trained links: {k}{(k == 4 ? "+" : "")} plate(s) (y again = next count)"); break; }
                case 'g': Ev("lp_break", "start", "state=4,prev=1,tx=1,lives=0,n=1"); Wait(2356); Ev("lp_break", "update", "ph=out,src=bench,since=2356,n=1"); log("  pick break: the approved scene, then the broken tip at 2356 ms (BrokenPick_Out ~2316 + detection 40)"); break;
                case 'o': Ev("lp", "complete", "res=open,moves=9,strains=0,n=5"); Wait(700); Ev("lp", "update", "ph=ready,ctr=1,n=5,holes=75775"); break;
                case 'x': Ev("lp", "complete", "res=close,moves=0,strains=0"); break;
                case 'w': Trig(Wall(), Soft(), "none"); break;
                case 'e': Trig(Soft(), Wall(), "none"); break;
                case 'n': Trig(Soft(), Soft(), "none"); break;
                case 'i': { int n = livesN; livesN = livesN <= 0 ? 6 : livesN - 1; string bar = n >= 2 ? "white" : (n == 1 ? "red" : "off"); Ev("lp", "update", $"ph=lives,n={n},max=6,leds={Math.Clamp(n - 1, 0, 5)},bar={bar},src=bench"); log($"  pick life {n}/6: {Math.Clamp(n - 1, 0, 5)} LEDs, lightbar {bar} (i again = one strain less; after 0 a new pick)"); break; }
                case 'm': Trig("warn", "wall", "none"); log("  Novice: L2 = danger, 2+ lives (warn, medium from 0), R2 = danger, last life (wall, max from 0); n = both safe clicks"); break;
                case 'a': Trig(Soft(), Ok(), "D"); log("  compare: L2 = safe (soft), R2 = right (crisp)"); break;
                case 'd': Trig(Wall(), Ok(), "D"); log("  compare: L2 = danger (wall), R2 = right (crisp)"); break;
                case 'v': set = (set + 1) % Sets.Length; Trig(Soft(), Ok(), "D"); Show(); break;
                case 'c': { var cp = Calibrate(); if (cp > 0) { firePoint = Math.Round(Math.Min(0.95, cp + 0.04), 2); Show(); log("  press f to write it to the mod"); } break; }
                case '-': firePoint = Math.Max(0.15, Math.Round(firePoint - 0.02, 2)); Show(); break;
                case '=': firePoint = Math.Min(0.95, Math.Round(firePoint + 0.02, 2)); Show(); break;
                case 'f': WriteFirePoint(modDir, firePoint, log); break;
                case '[': cfg.Calib.GainL = Math.Max(0, cfg.Calib.GainL - 0.05f); Show(); break;
                case ']': cfg.Calib.GainL = Math.Min(2, cfg.Calib.GainL + 0.05f); Show(); break;
                case ',': cfg.Calib.GainR = Math.Max(0, cfg.Calib.GainR - 0.05f); Show(); break;
                case '.': cfg.Calib.GainR = Math.Min(2, cfg.Calib.GainR + 0.05f); Show(); break;
                case 'p': Guide("plate", "L", "A"); Wait(600); Guide("plate", "R", "D"); break;
                case 'k': cfg.Calib.Save(); log("saved " + cfg.Calib.Path); break;
                case 'z': Blind(); break;
                case 'h': Console.WriteLine(Help); Show(); break;
                case 'q':
                    Ev("lp", "complete", "res=close,moves=0,strains=0");
                    Wait(200);
                    hd?.Dispose();
                    return;
            }
            Wait(50);
        }

        void Blind()
        {
            const int rounds = 20;
            var names = new[] { "", "push L", "push R", "up R1", "down L1" };
            var m = new int[5, 5];
            var rnd = new Random();
            log("BLIND TEST: after each signal press 1 (right plate, push left), 2 (right plate, push right), 3 (go up, R1), 4 (go down, L1); Esc = stop.");
            quiet = true;
            int done = 0;
            for (int i = 0; i < rounds; i++)
            {
                Wait(700);
                int cmd = rnd.Next(1, 5);
                Nav(cmd);
                Console.Write($"  {i + 1}/{rounds}: ? ");
                int ans = 0;
                while (ans == 0)
                {
                    var k = Console.ReadKey(true);
                    if (k.Key == ConsoleKey.Escape) { ans = -1; break; }
                    if (k.KeyChar is >= '1' and <= '4') ans = k.KeyChar - '0';
                    else if (k.KeyChar == 'r') Nav(cmd);   // repeat the signal
                }
                if (ans < 0) { Console.WriteLine("stopped"); break; }
                m[cmd, ans]++; done++;
                Console.WriteLine(ans == cmd ? "ok" : $"wrong (it was {names[cmd]})");
                Trig(Soft(), Soft(), "none");
            }
            quiet = false;
            int correct = 0;
            for (int c = 1; c <= 4; c++) correct += m[c, c];
            log($"BLIND TEST: {correct}/{done} correct ({(done > 0 ? 100.0 * correct / done : 0):0}%). Rows = signal, columns = answer:");
            log("        pushL pushR upR1 downL1");
            for (int c = 1; c <= 4; c++) log($"  {names[c],-4} {m[c, 1],3} {m[c, 2],3} {m[c, 3],3} {m[c, 4],3}");
        }

        // Where does the click break? The pad reports the trigger position (HID, 0-255). With L2 = safe click and R2 =
        // right click, every slow press is recorded; the break is the sample after which the position jumps fastest
        // (the resistance gives way). Space marks "it clicked now" by hand. Result: the median position (0..1).
        double Calibrate()
        {
            Trig(Soft(), Ok(), "D");
            log("CALIBRATION: press L2 and R2 SLOWLY to the end and release, 4+ times each. Space = 'it clicked now' (optional). Esc = done.");
            var sw = Stopwatch.StartNew();
            var lockObj = new object();
            var samples = new List<(long ms, int l, int r)>();
            using var cts = new CancellationTokenSource();
            try
            {
                pad.StartInputReader((buf, n) =>
                {
                    if (!DualSense.TryGetTriggers(buf, n, pad.IsBluetooth, out var l2, out var r2)) return;
                    lock (lockObj) samples.Add((sw.ElapsedMilliseconds, l2, r2));
                }, cts.Token);
            }
            catch (Exception ex) { log("input reader: " + ex.Message); return 0; }
            var manual = new List<int>();
            while (true)
            {
                Wait(20);
                if (!Console.KeyAvailable) continue;
                var k = Console.ReadKey(true);
                if (k.Key == ConsoleKey.Escape) break;
                if (k.Key == ConsoleKey.Spacebar)
                {
                    lock (lockObj) if (samples.Count > 0) { var last = samples[^1]; manual.Add(Math.Max(last.l, last.r)); log($"  manual mark at {Math.Max(last.l, last.r)}/255"); }
                }
            }
            cts.Cancel();
            List<(long ms, int l, int r)> all;
            lock (lockObj) all = samples.ToList();
            var breaksL = Breaks(all.Select(x => (x.ms, x.l)).ToList());
            var breaksR = Breaks(all.Select(x => (x.ms, x.r)).ToList());
            log($"  L2 ({Soft()}) breaks: {string.Join(" ", breaksL)}   R2 ({Ok()}) breaks: {string.Join(" ", breaksR)}   manual: {string.Join(" ", manual)}   ({all.Count} samples)");
            var allBreaks = breaksL.Concat(breaksR).Concat(manual).OrderBy(x => x).ToList();
            if (allBreaks.Count == 0) { log("  no click found - press slower, or mark with Space"); return 0; }
            double med = allBreaks[allBreaks.Count / 2] / 255.0;
            log($"  click point (median) {allBreaks[allBreaks.Count / 2]}/255 = {med:0.00}; L2 median {(breaksL.Count > 0 ? breaksL.OrderBy(x => x).ElementAt(breaksL.Count / 2) : 0)}, R2 median {(breaksR.Count > 0 ? breaksR.OrderBy(x => x).ElementAt(breaksR.Count / 2) : 0)}; suggested fire point {Math.Min(0.95, med + 0.04):0.00}");
            return med;
        }
    }

    // One break per press: a press = from < 20 up to > 200 and back; the break = the position just before the largest
    // forward jump per millisecond between 25 and 235.
    static List<int> Breaks(List<(long ms, int v)> s)
    {
        var res = new List<int>();
        int i = 0;
        while (i < s.Count)
        {
            while (i < s.Count && s[i].v >= 20) i++;
            int start = i;
            while (i < s.Count && s[i].v < 200) i++;
            if (i >= s.Count) break;
            double best = 0; int at = -1;
            for (int j = start + 1; j <= i; j++)
            {
                if (s[j - 1].v < 25 || s[j - 1].v > 235) continue;
                double dt = Math.Max(1, s[j].ms - s[j - 1].ms);
                double v = (s[j].v - s[j - 1].v) / dt;
                if (v > best) { best = v; at = s[j - 1].v; }
            }
            if (at > 0 && best >= 2.0) res.Add(at);
            while (i < s.Count && s[i].v >= 20) i++;
        }
        return res;
    }

    static string IniPath(string modDir) => Path.Combine(modDir, "DsLp_Lockpick.ini");

    static double ReadFirePoint(string modDir)
    {
        try
        {
            foreach (var line in File.ReadAllLines(IniPath(modDir)))
                if (line.StartsWith("FirePoint=") && double.TryParse(line[10..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) return v;
        }
        catch { }
        return 0.58;
    }

    static void WriteFirePoint(string modDir, double fire, Action<string> log)
    {
        try
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            double release = Math.Max(0.05, Math.Min(0.25, fire - 0.15));
            File.WriteAllText(IniPath(modDir), $"; Tactile Lockpicking: trigger fire point (written by DsBridge lp-bench {DateTime.Now:yyyy-MM-dd HH:mm})\n" +
                                               $"FirePoint={fire.ToString("0.00", inv)}\nReleasePoint={release.ToString("0.00", inv)}\n");
            log($"written {IniPath(modDir)}: FirePoint={fire:0.00}, ReleasePoint={release:0.00} (the mod reads it when the minigame opens)");
        }
        catch (Exception ex) { log("cannot write the mod settings: " + ex.Message); }
    }
}
