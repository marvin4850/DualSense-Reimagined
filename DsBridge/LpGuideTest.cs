using System.Diagnostics;
using System.Text.Json;
using DsBridge.Haptics;

namespace DsBridge;

/// <summary>Diagnostic only: balanced, randomized recognition trials; never sends input to the game.
/// The candidate rule exists only in this process. Production lockpick rules and bank selection stay untouched.</summary>
static class LpGuideTest
{
    static readonly string[] Names = { "L1", "R1", "L2", "R2" };
    public sealed record Trial(string Scheme, int Number, string Expected, string Answer,
        long ReactionMs, int Repeats, bool Correct);
    public sealed record Plan(int Seed, string[] Order, int[][] Decks);

    public static Plan MakePlan(int seed, int rounds, bool finger = false, bool metal = false, bool height = false)
    {
        if (rounds < (height ? 2 : 4) || rounds > 100 || rounds % (height ? 2 : 4) != 0)
            throw new ArgumentException("--rounds: кратное 4 число от 4 до 100 (на каждую схему)");
        var random = new Random(seed);
        void Shuffle<T>(T[] a)
        {
            for (int i = a.Length - 1; i > 0; i--) { int j = random.Next(i + 1); (a[i], a[j]) = (a[j], a[i]); }
        }
        var order = height ? new[] { "height" } : metal ? new[] { "metal" } : finger ? new[] { "four", "finger" } : new[] { "v21", "four" }; Shuffle(order);
        var decks = new int[order.Length][];
        for (int b = 0; b < order.Length; b++) { decks[b] = Enumerable.Range(0, rounds).Select(i => i % (height ? 2 : 4)).ToArray(); Shuffle(decks[b]); }
        return new Plan(seed, order, decks);
    }

    // Report layouts include the report ID. Refuse short/unknown reports instead of interpreting stale bytes.
    public static bool Decode(byte[] data, int length, bool bt, out byte shoulders, out byte face, out byte l2, out byte r2)
    {
        shoulders = face = l2 = r2 = 0;
        if (length <= 0 || length > data.Length) return false;
        int at = data[0] switch
        {
            0x31 when bt && length >= 11 => 9,
            0x01 when bt && length >= 10 => 5,
            0x01 when !bt && length >= 10 => 8,
            _ => -1
        };
        if (at < 0 || !DualSense.TryGetTriggers(data, length, bt, out l2, out r2)) return false;
        face = (byte)(data[at] & 0xf0);
        shoulders = (byte)(data[at + 1] & 3);
        return true;
    }

    public sealed class InputState
    {
        public int Held { get; private set; }
        public void Update(byte shoulders, byte face, byte l2, byte r2)
        {
            int triggers = Held & 12;
            if (l2 >= 148) triggers |= 4; else if (l2 <= 64) triggers &= ~4;
            if (r2 >= 148) triggers |= 8; else if (r2 <= 64) triggers &= ~8;
            Held = shoulders | triggers | ((face & 0x80) != 0 ? 16 : 0) | ((face & 0x40) != 0 ? 32 : 0);
        }
    }

    // Clone the existing guide timbre (including legacy fallback), changing only count and interval.
    // Rhythm and side, not a different material, distinguish the candidate commands.
    public static void AddCandidate(HapticConfig cfg)
    {
        var original = cfg.Rules.Single(r => r.Id == "lp-guide-sel");
        var effect = new EffectSpec { Motor = new EffectSpec() };
        foreach (var layer in original.Effect.Layers)
        {
            var copy = new Layer { Preset = layer.Preset, P = new(layer.P, StringComparer.OrdinalIgnoreCase) };
            copy.P["count"] = new Param { Expression = Expr.Compile("shoulder == 1 ? 2 : 1") };
            copy.P["intervalMs"] = new Param { Number = 100 };
            effect.Layers.Add(copy);
        }
        using var json = JsonDocument.Parse("[[0, \"=side == 'R' ? 240 : 0\", \"=side == 'L' ? 240 : 0\", 35], [100, \"=shoulder == 1 && side == 'R' ? 240 : 0\", \"=shoulder == 1 && side == 'L' ? 240 : 0\", 35]]");
        effect.Motor.Layers.Add(new Layer { Preset = "Rumble", P = new() { ["steps"] = Param.Parse(json.RootElement) } });
        cfg.Rules.Insert(0, new Rule
        {
            Id = "bench-four-guide", Event = "lp", Phases = new() { "update" },
            Where = new() { ["ph"] = new[] { "benchguide" } }, Priority = 2, Effect = effect
        });
    }

    // The trigger motor is a separate HID output, so grip PCM and finger cues can coexist.
    // Bench only: production tension/click profiles must later be explicitly restored between cues.
    public sealed class FingerPulse
    {
        readonly Action<bool, bool> write;
        long start;
        bool left, active, on;
        int count;
        int interval = 100, duration = 60;
        public FingerPulse(Action<bool, bool> write) => this.write = write;
        public void Start(long now, bool isLeft, bool shoulder, int intervalMs = 100, int durationMs = 60)
        {
            Cancel(); start = now; left = isLeft; count = shoulder ? 2 : 1; active = true;
            interval = intervalMs; duration = durationMs;
            on = true; write(left, true);
        }
        public void Tick(long now)
        {
            if (!active) return;
            long age = now - start;
            bool next = age >= 0 && (age < duration || count == 2 && age >= interval && age < interval + duration);
            if (next != on) { on = next; write(left, on); }
            if (age >= (count == 2 ? interval + duration : duration)) active = false;
        }
        public void Cancel()
        {
            if (active || on) write(left, false);
            active = on = false;
        }
    }

    public static void Run(HapticConfig cfg, DualSense pad, Action<string> log, int rounds, int seed, string report, bool finger = false, bool metal = false, bool height = false)
    {
        var plan = MakePlan(seed, rounds, finger, metal, height);
        int commandCount = height ? 2 : 4;
        if (Console.IsInputRedirected) throw new InvalidOperationException("Стенду нужно отдельное интерактивное окно консоли.");
        // Check output permissions before starting the pad or collecting results.
        report = Path.GetFullPath(report);
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        using (new FileStream(report, FileMode.CreateNew, FileAccess.Write)) { }
        AddCandidate(cfg);
        string? metalRecipe = metal ? File.ReadAllText(Path.Combine(cfg.Root, "experiments", "lockpick-metal.json")) : null;
        var trials = new List<Trial>();
        var started = DateTimeOffset.Now;
        var clock = Stopwatch.StartNew();
        var input = new InputState();
        var gate = new object();
        long inputAt = -1;
        int held = 0;
        long inputReports = 0;
        string stage = "initializing";
        bool hdAvailable = false;
        var rumble = new RumbleMixer(pad);
        HdEngine? hd = null;
        HapticRuntime? rt = null;
        var pulse = new FingerPulse((left, on) =>
        {
            var effect = on ? DualSense.EffectVibration(0, 3, 40) : DualSense.EffectOff();
            pad.SetTriggers(left ? effect : DualSense.EffectOff(), left ? DualSense.EffectOff() : effect);
        });
        void StopCue(string reason) { pulse.Cancel(); rt?.StopAll(reason); }
        using var cts = new CancellationTokenSource();
        Thread? reader = null;
        string status = "started";
        int seq = 0, generation = 0;
        void Save() => File.WriteAllText(report, JsonSerializer.Serialize(new
        {
            version = 1, started, updated = DateTimeOffset.Now, status, transport = pad.IsBluetooth ? "Bluetooth" : "USB",
            hd = hdAvailable, roundsPerScheme = rounds, plan,
            inputReports = Interlocked.Read(ref inputReports), stage,
            scope = height ? "height only: L1=down, R1=up; L2/R2 excluded" : "four commands",
            metalRecipe,
            selection = new { defaultVariant = SampleBank.FileSelection.def, slots = SampleBank.FileSelection.slots },
            calibration = new { cfg.Calib.GainL, cfg.Calib.GainR },
            protocol = metal ? "metal v2: grip-only mechanical gestures, contact/friction-or-loading/catch/resonance; L rough latch / R elastic spring; shoulder brush versus longer plate travel; identical grip channels; NO trigger vibration; USB HD only"
                : finger ? "four versus finger: same grip cues; finger adds side trigger, 40Hz amplitude 3 zone 0, 60ms pulses, 100ms onset interval; 0.58/0.25 input thresholds; timeout 10s"
                : "isolated cues; neutral triggers in both schemes; 0.58/0.25 trigger thresholds; timeout 10s; repeat Triangle; simultaneous=ambiguous",
            trials
        }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        try
        {
            hd = HdEngine.TryStart(pad, log);
            if (hd != null) { Replay.ApplyLimits(hd.Mixer, cfg.Limits); hd.StallMs = cfg.Limits.StallMs; }
            hdAvailable = hd?.Ready == true;
            if (metal && !hdAvailable) throw new InvalidOperationException("Тембровый тест требует USB и доступной HD-хаптики: моторная версия не передаёт эти тембры.");
            rt = new HapticRuntime(cfg, new HdOutLive { E = hd }, new MotorOutLive(rumble));
            pad.ResetTriggers();
            pad.SetLightbar(0, 70, 120, releaseStartupLight: true);
            reader = pad.StartInputReader((data, len) =>
            {
                if (!Decode(data, len, pad.IsBluetooth, out var shoulder, out var face, out var l, out var r)) return;
                lock (gate) { input.Update(shoulder, face, l, r); held = input.Held; inputAt = clock.ElapsedMilliseconds; }
                Interlocked.Increment(ref inputReports);
            }, cts.Token);
            Save();
            void Pump()
            {
                rumble.Update(); rt.Tick(DateTime.UtcNow); Thread.Sleep(2);
                pulse.Tick(clock.ElapsedMilliseconds);
                if (hd?.Failed == true) throw new IOException("Аудиоканал HD отключился. Тест остановлен, чтобы не смешивать результаты HD и моторов.");
                if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape) throw new OperationCanceledException();
            }
            int Read()
            {
                lock (gate)
                {
                    if (inputAt < 0 && clock.ElapsedMilliseconds < 5000) return -1;
                    if (inputAt < 0 || clock.ElapsedMilliseconds - inputAt > 3000)
                        throw new IOException("Нет свежих данных контроллера. Тест остановлен; результаты сохранены.");
                    return height ? held & ~12 : held;
                }
            }
            void Wait(int ms)
            {
                long until = clock.ElapsedMilliseconds + ms;
                while (clock.ElapsedMilliseconds < until) { Pump(); Read(); }
            }
            void Neutral()
            {
                long since = -1, deadline = clock.ElapsedMilliseconds + 30000;
                while (true)
                {
                    Pump(); int buttons = Read();
                    if (buttons >= 0 && (buttons & 32) != 0) throw new OperationCanceledException();
                    if (buttons == 0) { if (since < 0) since = clock.ElapsedMilliseconds; }
                    else since = -1;
                    if (since >= 0 && clock.ElapsedMilliseconds - since >= 180) return;
                    if (clock.ElapsedMilliseconds > deadline) throw new IOException("Кнопки не отпущены за 30 с. Тест остановлен.");
                }
            }
            void Cue(string scheme, int cmd)
            {
                StopCue("replace bench cue"); // cancel PCM tail and any remaining finger pulses
                bool shoulder = cmd < 2, left = cmd is 0 or 2;
                string ctx = scheme is "metal" or "height" ? $"ph=metalguide,side={(left ? "L" : "R")},shoulder={(shoulder ? 1 : 0)}"
                    : scheme is "four" or "finger"
                    ? $"ph=benchguide,side={(left ? "L" : "R")},shoulder={(shoulder ? 1 : 0)}"
                    : $"ph=guide,gen={++generation},kind={(shoulder ? "sel" : "plate")},side={(left ? "L" : "R")},prof=2";
                var decisions = rt.Handle(EventMonitor.ParseRecord($"{++seq}:lp:update:{ctx}")!, DateTime.UtcNow);
                if (decisions.Count == 0 || decisions.All(d => d.Route is not ("hd" or "rumble")))
                    throw new InvalidOperationException("Подсказка не воспроизводится: " + string.Join("; ", decisions));
                if (scheme == "finger") pulse.Start(clock.ElapsedMilliseconds, left, shoulder);
            }
            int Press()
            {
                while (true)
                {
                    Pump(); int buttons = Read();
                    if (buttons < 0) continue;
                    if ((buttons & 32) != 0) throw new OperationCanceledException();
                    if (buttons != 0) return buttons;
                }
            }
            Console.WriteLine(height ? "\nПРОВЕРКА ВЫСОТЫ: сейчас автоматически рисунок ВНИЗ (L1), затем ВВЕРХ (R1)."
                : "\nПРОВЕРКА ГОТОВНОСТИ. Подсветка должна стать голубой. Сейчас автоматически: левая рукоятка, затем правая.");
            stage = "automatic-output-check"; Save();
            string startupScheme = height ? "height" : metal ? "metal" : finger ? "finger" : "four";
            Cue(startupScheme, height ? 0 : 2); Wait(700); Cue(startupScheme, height ? 1 : 3); Wait(700); StopCue("startup output check finished");
            Console.WriteLine(height ? "Проверка кнопок: нажмите и отпустите ТОЛЬКО L1 и R1. L2/R2 в этом тесте не участвуют."
                : "Проверка кнопок: по очереди нажмите и отпустите L1, R1, L2, R2. Окно покажет каждое полученное нажатие.");
            stage = "input-check"; Save();
            int seen = 0, previous = 0;
            long reminder = clock.ElapsedMilliseconds;
            while (seen != (height ? 3 : 15))
            {
                Pump(); int buttons = Read();
                if (buttons < 0) continue;
                if ((buttons & 32) != 0) throw new OperationCanceledException();
                int pressed = buttons & ~previous & 15;
                for (int cmd = 0; cmd < commandCount; cmd++)
                    if ((pressed & (1 << cmd)) != 0)
                    {
                        log("Получено с контроллера: " + Names[cmd] + (height ? cmd == 0 ? " — вниз" : " — вверх" : ""));
                        seen |= 1 << cmd; Cue(startupScheme, cmd); Save();
                    }
                previous = buttons;
                if (clock.ElapsedMilliseconds - reminder > 10000)
                {
                    log($"USB/BT-отчёты приходят ({Interlocked.Read(ref inputReports)}), ожидаю: " +
                        string.Join(", ", Enumerable.Range(0, commandCount).Where(c => (seen & (1 << c)) == 0).Select(c => Names[c])));
                    reminder = clock.ElapsedMilliseconds; Save();
                }
            }
            StopCue("input check finished"); Neutral();
            log(height ? "L1 и R1 проверены. Готово к настройке высоты." : "Все четыре кнопки проверены. Контроллер готов к тесту.");
            Console.WriteLine(height ? "\nТЕСТ ВЫСОТЫ: L1 — вниз; R1 — вверх. △ повтор; ○ или Esc — выход."
                : "\nТЕСТ ПОДСКАЗОК: отвечайте L1 / R1 / L2 / R2. △ повтор; ○ или Esc — выход.");
            Console.WriteLine(height ? "НАСТРОЙКА ВЫСОТЫ: два рисунка для L1/вниз и R1/вверх. Направления сдвига L2/R2 здесь не оцениваются."
                : metal ? "МЕХАНИЗМ: слева контакт, шероховатое скольжение и сухой зацеп; справа натяжение и упругое освобождение. ТРИГГЕРЫ НЕ ВИБРИРУЮТ."
                : finger ? "Сравниваем рукоятки (four) и рукоятки + вибрацию триггера (finger). Пальцы держите на L2/R2."
                : "Триггеры без сопротивления в обеих схемах: здесь проверяем только язык подсказок.");
            Console.WriteLine("Сначала обучение с экраном, затем случайные задания без экрана и звука.");
            foreach (var (scheme, block) in plan.Order.Select((s, b) => (s, b)))
            {
                stage = "training-" + scheme; Save();
                Console.WriteLine($"\nСхема {scheme}: " + (scheme == "height"
                    ? "ВНИЗ/L1: шероховатая протяжка и сухой зацеп. ВВЕРХ/R1: натяжение и упругое освобождение. Триггеры вне теста."
                    : scheme == "metal"
                    ? "L = шероховатая защёлка; R = упругая пружина. L1/R1 — короткая лёгкая протяжка, L2/R2 — более длинный ход. Сторона определяется фактурой."
                    : scheme == "finger"
                    ? "двойной сигнал в рукоятке и триггере = L1/R1; одиночный = L2/R2. Сторона триггера указывает руку."
                    : scheme == "four"
                    ? "двойной боковой = L1/R1, одиночный боковой = L2/R2."
                    : "один боковой = L1/R1; центр, затем сторона = L2/R2."));
                Console.WriteLine(height ? "Обучение: нажимайте L1/R1 и сравнивайте рисунки ВНИЗ/ВВЕРХ. △ — необязательный зачёт."
                    : "Обучение: нажмите любую из четырёх кнопок, чтобы почувствовать её сигнал. △ — начать зачёт.");
                Neutral();
                while (true)
                {
                    int press = Press();
                    if (press == 16) { StopCue("training ended"); Neutral(); break; }
                    if (press is 1 or 2 or 4 or 8)
                    {
                        int cmd = press == 1 ? 0 : press == 2 ? 1 : press == 4 ? 2 : 3;
                        Console.WriteLine(Names[cmd] + (height ? cmd == 0 ? " — вниз" : " — вверх" : "")); Cue(scheme, cmd); Wait(600);
                    }
                    Neutral();
                }
                Console.WriteLine("Зачёт. Теперь можно закрыть глаза. После ответа следующий сигнал придёт автоматически.");
                stage = "scoring-" + scheme; Save();
                foreach (int expected in plan.Decks[block])
                {
                    Neutral(); Wait(700); Neutral();
                    Cue(scheme, expected);
                    long onset = clock.ElapsedMilliseconds;
                    int repeats = 0, answer = -1;
                    string answerName = "timeout";
                    while (clock.ElapsedMilliseconds - onset < 10000)
                    {
                        Pump(); int press = Read();
                        if (press < 0 || press == 0) continue;
                        if ((press & 32) != 0) throw new OperationCanceledException();
                        // A short observation window detects a chord instead of arbitrarily accepting its first report.
                        int combined = press;
                        for (int elapsed = 0; elapsed < 40; elapsed += 2) { Pump(); int v = Read(); if (v >= 0) combined |= v; }
                        if (combined == 16)
                        {
                            repeats++; StopCue("repeat requested"); Neutral(); Cue(scheme, expected);
                            continue;
                        }
                        answer = combined == 1 ? 0 : combined == 2 ? 1 : combined == 4 ? 2 : combined == 8 ? 3 : -1;
                        answerName = answer < 0 ? "ambiguous" : Names[answer];
                        break;
                    }
                    long reaction = clock.ElapsedMilliseconds - onset;
                    StopCue("answer received");
                    trials.Add(new Trial(scheme, trials.Count(t => t.Scheme == scheme) + 1, Names[expected], answerName,
                        reaction, repeats, answer == expected));
                    status = "in-progress"; Save();
                    // No correctness cue or screen answer during a scored block: avoid teaching the hidden sequence.
                    Console.WriteLine($"Принят ответ {trials.Count(t => t.Scheme == scheme)}/{rounds}.");
                }
                PrintSummary(trials.Where(t => t.Scheme == scheme).ToList(), log, height);
            }
            status = "complete";
        }
        catch (OperationCanceledException) { status = "cancelled"; log("Тест остановлен пользователем."); }
        catch (Exception ex) { status = "error"; log(ex.Message); }
        finally
        {
            cts.Cancel();
            try { StopCue("guide test exit"); pad.ResetTriggers(); pad.SetLightbar(0, 0, 0); } finally { hd?.Dispose(); reader?.Join(1000); }
            Save();
            log("Отчёт: " + report);
        }
        foreach (string scheme in plan.Order)
            PrintSummary(trials.Where(t => t.Scheme == scheme).ToList(), log, height);
    }

    static void PrintSummary(List<Trial> trials, Action<string> log, bool height = false)
    {
        if (trials.Count == 0) return;
        int correct = trials.Count(t => t.Correct), first = trials.Count(t => t.Correct && t.Repeats == 0);
        log($"{trials[0].Scheme}: {correct}/{trials.Count} верно; без повтора {first}/{trials.Count}; повторов {trials.Sum(t => t.Repeats)}.");
        var commands = height ? Names.Take(2).ToArray() : Names;
        log(height ? "Строки — сигнал; столбцы — L1/вниз R1/вверх timeout ambiguous:" : "Строки — сигнал; столбцы — L1 R1 L2 R2 timeout ambiguous:");
        foreach (string name in commands)
            log(name + ": " + string.Join(" ", commands.Concat(new[] { "timeout", "ambiguous" })
                .Select(a => trials.Count(t => t.Expected == name && t.Answer == a).ToString().PadLeft(3))));
        var times = trials.Where(t => t.Correct && t.Repeats == 0).Select(t => t.ReactionMs).Order().ToArray();
        if (times.Length > 0) log($"Медиана времени правильного ответа без повтора: {times[times.Length / 2]} мс (от начала сигнала).");
    }

    public static IEnumerable<(string name, bool ok)> CheckMetal(HapticConfig cfg)
    {
        var rendered = new List<HapticMetrics>();
        foreach (string side in new[] { "L", "R" })
            foreach (int shoulder in new[] { 0, 1 })
            {
                var rule = cfg.Rules.Single(r => r.Id == (side == "L" ? "bench-metal-left" : "bench-metal-right"));
                var signal = HapticRuntime.RenderHd(rule.Effect,
                    key => key == "side" ? side : key == "shoulder" ? (double)shoulder : null, 48000);
                var m = Analysis.Measure(signal.l, signal.r);
                rendered.Add(m);
                yield return ($"Metal {side} {(shoulder == 1 ? "shoulder" : "move")}: {m.Short()}",
                    m.Peak <= 0.855 && m.Clipped == 0 && m.OverKnee == 0 && m.Rms20 >= 0.28 && m.Rms20 <= 0.7 &&
                    Math.Abs(m.DcL) < 0.02 && Math.Abs(m.DcR) < 0.02 && m.ActiveMs < 240 && signal.l.SequenceEqual(signal.r));
                bool matching = Enumerable.Range(0, 4).Any(cmd =>
                {
                    string dir = cmd is 0 or 2 ? "L" : "R";
                    var ev = EventMonitor.ParseRecord($"1:lp:update:ph=metalguide,side={dir},shoulder={(cmd < 2 ? 1 : 0)}")!;
                    return dir == side && (cmd < 2 ? 1 : 0) == shoulder && cfg.Rules.Count(r => r.Matches(ev)) == 1;
                });
                yield return ($"Metal {side}/{shoulder}: unique runtime rule", matching);
            }
        yield return ("Metal: distinct resonance and decay, no side-channel dependence", rendered[2].CentroidHz > rendered[0].CentroidHz * 1.2
            && rendered[2].ActiveMs > rendered[0].ActiveMs + 20);
        yield return ("Metal: balanced single-block recognition plan", MakePlan(7, 8, metal: true).Order.SequenceEqual(new[] { "metal" }));
    }

    public static IEnumerable<(string name, bool ok)> SelfChecks()
    {
        var heightPlan = MakePlan(99, 8, metal: true, height: true);
        yield return ("Height bench: only L1/down and R1/up, balanced, no trigger tasks", heightPlan.Order.SequenceEqual(new[] { "height" }) &&
            heightPlan.Decks.Length == 1 && heightPlan.Decks[0].Count(c => c == 0) == 4 && heightPlan.Decks[0].Count(c => c == 1) == 4);
        var pulseEvents = new List<(bool left, bool on)>();
        var pulseTest = new FingerPulse((left, on) => pulseEvents.Add((left, on)));
        pulseTest.Start(0, true, true); pulseTest.Tick(60); pulseTest.Tick(100); pulseTest.Tick(160);
        bool doubles = pulseEvents.SequenceEqual(new[] { (true, true), (true, false), (true, true), (true, false) });
        pulseEvents.Clear(); pulseTest.Start(200, false, true); pulseTest.Tick(1000);
        bool noLatePulse = pulseEvents.SequenceEqual(new[] { (false, true), (false, false) });
        pulseEvents.Clear(); pulseTest.Start(2000, true, true); pulseTest.Cancel(); pulseTest.Tick(2100);
        yield return ("Finger guide: synchronized side pulses, cancellation and no late replay", doubles && noLatePulse &&
            pulseEvents.SequenceEqual(new[] { (true, true), (true, false) }));
        yield return ("Finger guide: comparison uses four versus finger", MakePlan(71, 8, true).Order.Order().SequenceEqual(new[] { "finger", "four" }));
        var p = MakePlan(71, 20); var again = MakePlan(71, 20);
        yield return ("Guide test: balanced decks, reproducible seed and both schemes", p.Order.Distinct().Count() == 2 &&
            p.Order.SequenceEqual(again.Order) && p.Decks.SelectMany(x => x).SequenceEqual(again.Decks.SelectMany(x => x)) &&
            p.Decks.All(deck => Enumerable.Range(0, 4).All(c => deck.Count(x => x == c) == 5)));
        var usb = new byte[64]; usb[0] = 1; usb[5] = 180; usb[6] = 90; usb[8] = 0x80; usb[9] = 3;
        var bt = new byte[78]; bt[0] = 0x31; bt[6] = 180; bt[7] = 90; bt[9] = 0x80; bt[10] = 3;
        var basic = new byte[10]; basic[0] = 1; basic[8] = 180; basic[9] = 90; basic[5] = 0x80; basic[6] = 3;
        bool Reports(byte[] bytes, bool wireless) => Decode(bytes, bytes.Length, wireless, out var s, out var f, out var l, out var r)
            && s == 3 && f == 0x80 && l == 180 && r == 90;
        yield return ("Guide test: USB, BT extended and BT basic input layouts", Reports(usb, false) && Reports(bt, true) && Reports(basic, true));
        yield return ("Guide test: rejects truncated and empty input", !Decode(Array.Empty<byte>(), 0, false, out _, out _, out _, out _)
            && !Decode(usb, 9, false, out _, out _, out _, out _) && !Decode(bt, 10, true, out _, out _, out _, out _));
        var state = new InputState(); state.Update(0, 0, 150, 0); bool down = state.Held == 4;
        state.Update(0, 0, 100, 0); bool held = state.Held == 4;
        state.Update(0, 0, 64, 0); bool released = state.Held == 0;
        state.Update(3, 0xc0, 0, 160);
        yield return ("Guide test: trigger hysteresis, shoulders and repeat/exit buttons", down && held && released && state.Held == 59);
        var cfg = new HapticConfig();
        cfg.Rules.Add(new Rule { Id = "lp-guide-sel", Effect = new EffectSpec { Layers = new() { new Layer { Preset = "Tick" } } } });
        AddCandidate(cfg);
        (int delay, byte right, byte left, int ms)[] Motor(string side, int shoulder) => HapticRuntime.RenderMotor(cfg.Rules[0].Effect,
            key => key == "side" ? side : key == "shoulder" ? (double)shoulder : null, 48000);
        var leftDouble = Motor("L", 1); var rightSingle = Motor("R", 0);
        yield return ("Guide test: candidate motor rhythm and side carry all four commands", leftDouble.Count(x => x.left > 0) == 2 &&
            leftDouble.All(x => x.right == 0) && rightSingle.Count(x => x.right > 0) == 1 && rightSingle.All(x => x.left == 0) &&
            cfg.Rules[1].Effect.Layers[0].P.Count == 0);
        var path = HapticConfig.Locate(null);
        if (path != null)
        {
            var actual = HapticConfig.Load(path, new[] { "lockpick-selected" }); // historical v21/four bench, not the production triad
            if (File.Exists(Path.Combine(actual.Root, "experiments", "lockpick-metal.json")))
            {
                actual.AddSupplementalPack("experiments/lockpick-metal.json");
                foreach (var check in CheckMetal(actual)) yield return check;
            }
            if (actual.Rules.Any(r => r.Id == "lp-guide-sel"))
            {
                AddCandidate(actual);
                var doubleCue = HapticRuntime.RenderHd(actual.Rules[0].Effect,
                    key => key == "side" ? "L" : key == "shoulder" ? 1.0 : null, 48000);
                var singleCue = HapticRuntime.RenderHd(actual.Rules[0].Effect,
                    key => key == "side" ? "R" : key == "shoulder" ? 0.0 : null, 48000);
                yield return ("Guide test: real HD bank yields double versus single and localized sides",
                    doubleCue.l.Length - singleCue.l.Length == 4800 && singleCue.r.Any(x => x != 0) &&
                    doubleCue.l.Sum(x => (double)x * x) > doubleCue.r.Sum(x => (double)x * x) &&
                    singleCue.r.Sum(x => (double)x * x) > singleCue.l.Sum(x => (double)x * x));
                var output = new ProbeHd();
                var runtime = new HapticRuntime(actual, output, new ProbeMotor());
                bool routed = true;
                foreach (string scheme in new[] { "v21", "four" })
                    for (int cmd = 0; cmd < 4; cmd++)
                    {
                        string side = cmd is 0 or 2 ? "L" : "R";
                        string context = scheme == "four" ? $"ph=benchguide,side={side},shoulder={(cmd < 2 ? 1 : 0)}"
                            : $"ph=guide,gen=1,kind={(cmd < 2 ? "sel" : "plate")},side={side},prof=2";
                        var ev = EventMonitor.ParseRecord("1:lp:update:" + context);
                        runtime.StopAll("probe replace");
                        var decisions = runtime.Handle(ev!, DateTime.UtcNow);
                        routed &= decisions.Count == 1 && decisions[0].Route == "hd" && output.Nonzero;
                    }
                yield return ("Guide test: all 8 real events reach non-silent HD output and replace prior cues", routed && output.Stops == 8 && runtime.Errors == 0);
            }
        }
    }

    sealed class ProbeMotor : IMotorOut
    {
        public void Pattern((int delay, byte right, byte left, int ms)[] steps) { }
        public void StopAll() { }
    }
    sealed class ProbeHd : IHdOut
    {
        public bool Ready => true;
        public int Rate => 48000;
        public bool Active => Nonzero;
        public bool Nonzero;
        public int Stops;
        public long Play(float[] l, float[] r, VoiceOpts options)
        { Nonzero = l.Any(x => x != 0) || r.Any(x => x != 0); return 1; }
        public void Stop(long id, int fadeMs) => Nonzero = false;
        public void StopAll() { Stops++; Nonzero = false; }
    }
}
