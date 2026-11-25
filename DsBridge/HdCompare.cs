using System.Diagnostics;
using System.Text.Json;
using DsBridge.Haptics;

namespace DsBridge;

/// <summary>`hd-compare`: the final user comparison of the forge variants on the controller. Synthetic "lp" events go into
/// the SAME haptic runtime / rules / HD engine / trigger output as `run` (what is felt here is what the game plays);
/// the variant is switched in memory (SampleBank override) in an instant; a "round" replays the last scene in every
/// variant one after another; the choice (global and per slot) is saved to haptics/bank/selection.json, which `run`
/// re-reads by itself. No game action is ever sent; the speaker is never used.</summary>
static class HdCompare
{
    // scene key -> (title, rule slots it exercises)
    static readonly Dictionary<char, (string title, string[] slots)> Scenes = new()
    {
        ['i'] = ("вставка отмычки", new[] { "lp.insert" }),
        ['n'] = ("выбор пластины (L1/R1)", new[] { "lp.notch" }),
        ['g'] = ("подсказка: верная пластина, толкать вправо", new[] { "lp.guide" }),
        ['h'] = ("подсказка: не та пластина, вниз (L1)", new[] { "lp.guide" }),
        ['m'] = ("ход одной пластины", new[] { "lp.glide", "lp.cross", "lp.settle" }),
        ['l'] = ("ход связанных пластин: 2 + 1 встречная", new[] { "lp.glide", "lp.cross", "lp.settle" }),
        ['k'] = ("ход 7 связанных пластин: 5 + 2 встречных", new[] { "lp.glide", "lp.cross", "lp.settle" }),
        ['c'] = ("пластина встала в центр (фиксация)", new[] { "lp.glide", "lp.cross", "lp.settle" }),
        ['s'] = ("надрыв отмычки", new[] { "lp.strain" }),
        ['b'] = ("поломка отмычки", new[] { "lp.break" }),
        ['r'] = ("сброс (храповик)", new[] { "lp.reset" }),
        ['o'] = ("открытие замка", new[] { "lp.unlock" }),
        ['a'] = ("весь взлом подряд", new[] { "lp.insert", "lp.notch", "lp.guide", "lp.glide", "lp.cross", "lp.settle", "lp.unlock" }),
    };

    public static void Run(HapticConfig cfg, DualSense pad, Action<string> log, string bank)
    {
        var rumble = new RumbleMixer(pad);
        var hd = HdEngine.TryStart(pad, log);
        if (hd != null) { Replay.ApplyLimits(hd.Mixer, cfg.Limits); hd.StallMs = cfg.Limits.StallMs; }
        else log("ВНИМАНИЕ: HD-хаптика недоступна (Bluetooth или нет аудиоустройства пада) - варианты HD сравнить нельзя, подключите пад кабелем.");
        var padOut = new PadOutLive(() => pad) { Log = log };
        var rt = new HapticRuntime(cfg, new HdOutLive { E = hd }, new MotorOutLive(rumble)) { Log = log, Pad = padOut };

        var variants = SampleBank.VariantsOnDisk(bank);
        var labels = ReadLabels(bank);
        variants.Add(SampleBank.Legacy);
        labels[SampleBank.Legacy] = "прежние процедурные эффекты (до forge)";
        if (variants.Count == 1) log($"в банке '{bank}' нет вариантов: сначала `forge` (сейчас доступен только legacy)");
        var (fileDef, fileSlots) = SampleBank.FileSelection;
        int cur = Math.Max(0, variants.FindIndex(v => v.Equals(fileDef, StringComparison.OrdinalIgnoreCase)));
        var pins = new Dictionary<string, string>(fileSlots, StringComparer.OrdinalIgnoreCase);
        void Apply() { SampleBank.OverrideAll(variants[cur]); foreach (var (s, v) in pins) SampleBank.OverrideSlot(s, v); }
        Apply();

        int seq = 0, tx = 0, gen = 0; bool quiet = true, dirty = false; char last = 'm';
        void Ev(string type, string phase, string ctx)
        {
            var e = EventMonitor.ParseRecord($"{++seq}:{type}:{phase}:t={Environment.TickCount64 % 100000000},src=compare{(ctx.Length > 0 ? "," + ctx : "")}");
            if (e == null) return;
            var d = rt.Handle(e, DateTime.UtcNow);
            if (!quiet) log($"  {type}:{phase} {ctx} -> {(d.Count == 0 ? "(no rule)" : string.Join("; ", d))}");
        }
        void Wait(int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms) { rumble.Update(); rt.Tick(DateTime.UtcNow); padOut.Refresh(DateTime.UtcNow); Thread.Sleep(2); }
        }
        void Trig(string l, string r) => Ev("lp", "update", $"ph=trig,l={l},r={r},next=none,skill=2");
        void Move(string dir, int same, int opp, int cin)
        {
            int ch = same + opp, id = ++tx;
            Ev("lp", "update", $"ph=press,tx={id},dir={dir},cur=1,valid=1,ch={ch},same={same},opp={opp},cin={cin},cout=0,mask=0,conf=predicted");
            Wait(60);
            Ev("lp", "update", $"ph=motion,stage=start,tx={id},dir={dir},ch={ch},same={same},opp={opp},est=300,pm=60,conf=observed");
            Wait(140);
            Ev("lp", "update", $"ph=cross,tx={id},dir={dir},k={same},same={same},opp=0,mask=0,p=500,ch={ch},t=140");
            if (opp > 0) { Wait(25); Ev("lp", "update", $"ph=cross,tx={id},dir={dir},k={opp},same=0,opp={opp},mask=0,p=520,ch={ch},t=165"); }
            Wait(opp > 0 ? 135 : 160);
            Ev("lp", "update", $"ph=settle,status=accepted,tx={id},ch={ch},dir={dir},ctr={1 + cin},n=5,cin={cin},cout=0,unx=0,mismatch=0,pm=60,md=300");
        }
        void Scene(char k)
        {
            switch (k)
            {
                case 'i': Ev("lp", "update", "ph=insert,via=montage,ms=836"); break;
                case 'n': Ev("lp", "update", "ph=sel,cur=2"); Wait(260); Ev("lp", "update", "ph=sel,cur=3"); break;
                case 'g': Ev("lp", "update", $"ph=guide,gen={++gen},kind=plate,side=R,next=D,cur=1,prof=2"); break;
                case 'h': Ev("lp", "update", $"ph=guide,gen={++gen},kind=sel,side=L,next=S,cur=1,prof=2"); break;
                case 'm': Move("R", 1, 0, 0); break;
                case 'l': Move("L", 2, 1, 0); break;
                case 'k': Move("R", 5, 2, 0); break;
                case 'c': Move("L", 2, 0, 1); break;
                case 's': Ev("lp", "update", $"ph=press,tx={++tx},dir=R,cur=1,valid=0,ch=0,same=0,opp=0,cin=0,cout=0,mask=0,conf=predicted"); Ev("lp", "update", $"ph=strain,dir=R,cur=1,tx={tx},conf=derived"); break;
                case 'b': Ev("lp", "update", "ph=settle,status=break,tx=0,ch=4,dir=0,ctr=1,n=5,cin=0,cout=1,unx=0,mismatch=0,pm=0,md=0"); break;
                case 'r': Ev("lp", "update", "ph=settle,status=reset,tx=0,ch=4,dir=0,ctr=1,n=5,cin=0,cout=1,unx=0,mismatch=0,pm=0,md=0"); break;
                case 'o': Ev("lp", "complete", "res=open,moves=9,strains=0,n=5"); Wait(900); Ev("lp", "update", "ph=ready,ctr=1,n=5,holes=75775"); break;
                case 'a':
                    foreach (var step in "inmlcgo") { Scene(step); Wait(step is 'm' or 'l' or 'c' ? 650 : 700); }
                    break;
            }
            Trig("click_soft", "click_soft");
        }
        string Name(int i) => $"{i + 1}:{variants[i]}";
        void Show()
        {
            log($"вариант {Name(cur)} - {labels.GetValueOrDefault(variants[cur], "")}" +
                (pins.Count > 0 ? $"; закреплено по слотам: {string.Join(", ", pins.Select(kv => kv.Key + "=" + kv.Value))}" : "") + (dirty ? "  [не сохранено: w]" : ""));
        }

        Ev("lp", "start", "lock=compare,pl=5,prec=2,cut=2,src=bench,known=1");
        Ev("lp", "update", "ph=ready,ctr=1,n=5,holes=75775");
        Wait(300);
        Console.WriteLine(Help(variants, labels));
        Show();
        while (true)
        {
            var key = Console.ReadKey(true);
            char ch = key.KeyChar;
            if (ch >= '1' && ch <= '9' && ch - '1' < variants.Count) { cur = ch - '1'; Apply(); Show(); Scene(last); }
            else if (key.Key == ConsoleKey.Tab) { cur = (cur + 1) % variants.Count; Apply(); Show(); Scene(last); }
            else if (Scenes.ContainsKey(ch)) { last = ch; log($"сцена: {Scenes[ch].title} [{variants[cur]}]"); Scene(ch); }
            else if (ch == ' ')
            {   // round: the last scene in every variant
                log($"круг: {Scenes[last].title} - все варианты по очереди");
                int keep = cur;
                for (int i = 0; i < variants.Count; i++)
                {
                    cur = i; Apply();
                    log($"  >> {Name(i)}");
                    Scene(last); Wait(last == 'a' ? 600 : 1100);
                    if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape) break;
                }
                cur = keep; Apply(); Show();
            }
            else if (ch == 'p')
            {   // pin the current variant to the slots of the last scene
                foreach (var s in Scenes[last].slots) pins[s] = variants[cur];
                dirty = true; Apply(); Show();
            }
            else if (ch == 'u') { pins.Clear(); dirty = true; Apply(); Show(); }
            else if (ch == 'w')
            {
                SampleBank.SaveSelection(variants[cur], pins, $"saved by hd-compare {DateTime.Now:yyyy-MM-dd HH:mm}");
                dirty = false;
                log($"сохранено: {SampleBank.SelectionPath} (по умолчанию {variants[cur]}{(pins.Count > 0 ? ", слоты: " + string.Join(", ", pins.Select(kv => kv.Key + "=" + kv.Value)) : "")}); `run` подхватит без перезапуска");
            }
            else if (ch == 'd') { quiet = !quiet; log("журнал решений правил: " + (quiet ? "выкл" : "вкл")); }
            else if (ch == '?' || ch == 'H'){ Console.WriteLine(Help(variants, labels)); Show(); }
            else if (ch == 'q' || key.Key == ConsoleKey.Escape)
            {
                if (dirty) log("выбор НЕ сохранён (w - сохранить); выход");
                Ev("lp", "complete", "res=close,moves=0,strains=0");
                Wait(200);
                SampleBank.OverrideAll(null);
                hd?.Dispose();
                return;
            }
            Wait(30);
        }
    }

    static string Help(List<string> variants, Dictionary<string, string> labels) =>
        "\nСравнение тактильных вариантов взлома (через настоящие правила и HD, как в игре; динамик пада не используется)\n" +
        "  Варианты:  " + string.Join("   ", variants.Select((v, i) => $"{i + 1} = {v} ({labels.GetValueOrDefault(v, "")})")) + "\n" +
        "             Tab = следующий вариант (сразу повторяет последнюю сцену)\n" +
        "  Сцены:     i вставка   n выбор пластины   g подсказка (верная, вправо)   h подсказка (вниз)\n" +
        "             m ход 1 пластины   l связанные 2+1   k связанные 5+2   c фиксация в центре\n" +
        "             s надрыв   b поломка   r сброс   o открытие   a весь взлом подряд\n" +
        "  Пробел     круг: последняя сцена во всех вариантах по очереди (Esc - прервать)\n" +
        "  p          закрепить текущий вариант за слотами последней сцены   u - снять закрепления\n" +
        "  w          сохранить выбор (haptics/bank/selection.json; run подхватит сам)   d - журнал правил   ? - справка   q - выход\n";

    static Dictionary<string, string> ReadLabels(string bank)
    {
        var res = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var d = JsonDocument.Parse(File.ReadAllText(Path.Combine(SampleBank.BankDir, bank, "manifest.json")));
            foreach (var v in d.RootElement.GetProperty("variants").EnumerateArray()) res[v.GetProperty("name").GetString()!] = v.GetProperty("label").GetString() ?? "";
        }
        catch { }
        return res;
    }
}
