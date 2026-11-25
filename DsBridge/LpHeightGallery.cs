using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using DsBridge.Haptics;

namespace DsBridge;

/// <summary>Audition only: independent six-item down/up lists. No game input, no trigger cues, no scored trials.</summary>
static class LpHeightGallery
{
    public const string Pack = "experiments/height-gallery.json";
    public const string ConfirmPack = "experiments/height-confirm.json";
    const int Square = 0x100, Cross = 0x200, Circle = 0x400, Triangle = 0x800;
    public static int Next(int current) => (current + 1) % 6;
    static Rule Find(HapticConfig cfg, string dir, int variant) => cfg.Rules.Single(r => r.Id == $"height-{dir}-{variant}");
    public static IEnumerable<(string name, bool ok)> Checks(HapticConfig cfg, bool confirmation = false)
    {
        var variants = cfg.Rules.Where(r => r.Pack == "height-gallery").ToList();
        yield return ("Gallery: exactly six down and six up, no trigger outputs", variants.Count == 12 &&
            variants.All(r => r.Pad == null) && new[] { "down", "up" }.All(dir => Enumerable.Range(1,6).All(i => variants.Count(r => r.Id == $"height-{dir}-{i}") == 1)));
        if (confirmation)
        {
            var found=cfg.Rules.Where(r=>r.Pack=="height-confirm").ToList();
            yield return ("Confirmation: six correct-plate cues, no trigger output", found.Count==6 && found.All(r=>r.Pad==null) &&
                Enumerable.Range(1,6).All(i=>found.Count(r=>r.Id==$"height-found-{i}")==1));
            variants.AddRange(found);
        }
        foreach (var rule in variants)
        {
            // Measure the raw sum before RenderHd's final clamp, so overlap cannot hide behind clipping.
            float[] l = Array.Empty<float>(), r = Array.Empty<float>();
            foreach (var layer in rule.Effect.Layers)
            {
                var (a,b) = Presets.RenderLayer(layer.Preset, HapticRuntime.Resolve(layer, _ => null), 48000);
                int n = Math.Max(l.Length, Math.Max(a.Length,b.Length)); Array.Resize(ref l,n); Array.Resize(ref r,n);
                for (int i=0;i<a.Length;i++) l[i]+=a[i]; for (int i=0;i<b.Length;i++) r[i]+=b[i];
            }
            var m = Analysis.Measure(l,r);
            yield return ($"{rule.Id}: {m.Short()}, DC {m.DcL:F3}", m.Peak <= .855 && m.Clipped == 0 && m.OverKnee == 0 &&
                Math.Abs(m.DcL)<.02 && Math.Abs(m.DcR)<.02 && m.Rms20 >= .28 && m.Rms20<=.7 && m.ActiveMs>=70 && m.ActiveMs<=240 && l.SequenceEqual(r));
        }
        yield return ("Gallery: independent selector wraps 6 -> 1", Next(-1)==0 && Next(4)==5 && Next(5)==0);
    }

    public static void Run(HapticConfig cfg, DualSense pad, Action<string> log, string report, bool confirmation = false)
    {
        if (pad.IsBluetooth) throw new InvalidOperationException("Галерея требует USB HD: эти фактуры не заменяются моторной эмуляцией.");
        report=Path.GetFullPath(report); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        var recipe=File.ReadAllText(Path.Combine(cfg.Root,Pack));
        string? confirmationRecipe=confirmation ? File.ReadAllText(Path.Combine(cfg.Root,ConfirmPack)) : null;
        var clock=Stopwatch.StartNew(); var queue=new ConcurrentQueue<(int buttons,long at)>();
        var history=new List<object>(); int down=-1,up=-1,foundIndex=-1,last=-1,seq=0;
        if(confirmation)
        {
            using var selected=JsonDocument.Parse(File.ReadAllText(Path.Combine(cfg.Root,"experiments","height-selection.json")));
            string sha=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(recipe)));
            if(!string.Equals(sha,selected.RootElement.GetProperty("sourceSha256").GetString(),StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Галерея изменилась после выбора: сначала требуется сверить сохранённую пару.");
            down=selected.RootElement.GetProperty("down").GetProperty("variant").GetInt32()-1;
            up=selected.RootElement.GetProperty("up").GetProperty("variant").GetInt32()-1;
            if(down<0 || down>=6 || up<0 || up>=6) throw new FormatException("Неверный номер выбранной пары.");
        }
        using (new FileStream(report,FileMode.CreateNew,FileAccess.Write)) { }
        object? choice=null; string status="started"; long lastReport=-1, reports=0;
        bool compare=false; long compareAt=0;
        using var cancel=new CancellationTokenSource(); Thread? reader=null; HdEngine? hd=null; HapticRuntime? runtime=null;
        void Save() => File.WriteAllText(report,JsonSerializer.Serialize(new
        {
            version=1,status,transport="USB",scope=confirmation ? "correct selected plate found; selected down/up pair fixed" : "height only: L1 down / R1 up; gallery, not scored",recipe,confirmationRecipe,
            updated=DateTimeOffset.Now,reports=Interlocked.Read(ref reports),
            preview=new { down=down+1,up=up+1,found=foundIndex+1 },choice,history
        },new JsonSerializerOptions { WriteIndented=true }));
        void Stop(string reason) { compare=false; runtime?.StopAll(reason); }
        void Play(int direction,bool advance)
        {
            Stop("replace gallery cue");
            if(direction==0) down=advance?Next(down):Math.Max(0,down);
            else if(direction==1) up=advance?Next(up):Math.Max(0,up);
            else foundIndex=advance?Next(foundIndex):Math.Max(0,foundIndex);
            last=direction; string dir=direction==0?"down":direction==1?"up":"found"; int variant=(direction==0?down:direction==1?up:foundIndex)+1;
            var rule=Find(cfg,dir,variant);
            var ev=EventMonitor.ParseRecord($"{++seq}:lp:update:ph=heightgallery,dir={dir},variant={variant}")!;
            var decisions=runtime!.Handle(ev,DateTime.UtcNow);
            if (decisions.Count!=1 || decisions[0].Route!="hd") throw new IOException("Не удалось воспроизвести образец: "+string.Join("; ",decisions));
            log($"{(direction==0?"ВНИЗ / L1":direction==1?"ВВЕРХ / R1":"НУЖНАЯ ПЛАСТИНА НАЙДЕНА")} №{variant}: {rule.Note}");
            history.Add(new { ms=clock.ElapsedMilliseconds,dir,variant }); Save();
        }
        try
        {
            hd=HdEngine.TryStart(pad,log);
            if(hd==null || !hd.Ready) throw new IOException("HD недоступна. Нужен DualSense по USB с активным аудиоустройством.");
            Replay.ApplyLimits(hd.Mixer,cfg.Limits); hd.StallMs=cfg.Limits.StallMs;
            runtime=new HapticRuntime(cfg,new HdOutLive { E=hd },new GalleryMotor());
            pad.ResetTriggers(); pad.SetLightbar(0,70,120,releaseStartupLight:true);
            int previous=0; bool primed=false;
            reader=pad.StartInputReader((data,len)=>
            {
                if(!LpGuideTest.Decode(data,len,false,out var shoulders,out var face,out _,out _)) return;
                int held=shoulders | (face << 4); int pressed=held & ~previous; previous=held;
                Interlocked.Exchange(ref lastReport,clock.ElapsedMilliseconds); Interlocked.Increment(ref reports);
                if(!primed) { if(held==0) primed=true; return; }
                if(pressed!=0) queue.Enqueue((pressed,clock.ElapsedMilliseconds));
            },cancel.Token);
            Console.WriteLine(confirmation ? "\nПОДТВЕРЖДЕНИЕ: нужная пластина для следующего действия найдена. Это не открытие всего замка."
                : "\nГАЛЕРЕЯ ВЫСОТЫ: 6 вариантов ВНИЗ и 6 ВВЕРХ. Триггеры не вибрируют. Зачёта нет.");
            Console.WriteLine(confirmation ? $"L1 — выбранный ВНИЗ №{down+1}; R1 — выбранный ВВЕРХ №{up+1}; Cross — следующий вариант НАЙДЕНО (1–6)."
                : "L1 — следующий ВНИЗ; R1 — следующий ВВЕРХ. Первое нажатие даёт №1.");
            Console.WriteLine(confirmation ? "Triangle — повтор; Square — сохранить подтверждение; Circle/Esc — выйти. Триггеры не вибрируют."
                : "Triangle — повтор последнего; Cross — сравнить текущие вниз / вверх; Square — сохранить пару; Circle/Esc — выйти.");
            Console.WriteLine(confirmation ? "Клавиатура: 1–6 — НАЙДЕНО; d/u — выбранные вниз/вверх; r — повтор; s — сохранить."
                : "Клавиатура: 1–6 — выбрать вниз; F1–F6 — выбрать вверх; r — повтор; c — сравнить; s — сохранить.");
            foreach(string dir in confirmation ? new[] { "found" } : new[] { "down","up" })
            {
                Console.WriteLine(dir=="down"?"\nВНИЗ:":dir=="up"?"\nВВЕРХ:":"\nНАЙДЕНО:");
                for(int i=1;i<=6;i++) Console.WriteLine($"  {i}. {Find(cfg,dir,i).Note}");
            }
            Save(); bool running=true;
            void Handle(int pressed)
            {
                if((pressed & Circle)!=0) { running=false; return; }
                // Do not guess a choice from a chord. Navigation triggers are absent from the input mask.
                switch(pressed)
                {
                    case 1: Play(0,!confirmation); break;
                    case 2: Play(1,!confirmation); break;
                    case Triangle: if(last>=0) Play(last,false); break;
                    case Cross:
                        if(confirmation) { Play(2,true); break; }
                        Play(0,false); compare=true; compareAt=clock.ElapsedMilliseconds+750; break;
                    case Square:
                        if(down<0 || up<0) { log("Сначала попробуйте хотя бы по одному варианту вниз и вверх."); break; }
                        if(confirmation && foundIndex<0) { log("Сначала попробуйте подтверждение кнопкой Cross."); break; }
                        choice=new { down=down+1,up=up+1,found=confirmation ? foundIndex+1 : 0,downName=Find(cfg,"down",down+1).Note,upName=Find(cfg,"up",up+1).Note,foundName=confirmation?Find(cfg,"found",foundIndex+1).Note:null };
                        status="choice-saved"; Save(); log(confirmation ? $"Подтверждение №{foundIndex+1} сохранено. Пара высоты не изменена."
                            : $"Пара сохранена: ВНИЗ {down+1} / ВВЕРХ {up+1}. Игра не изменена."); break;
                }
            }
            while(running)
            {
                if(hd.Failed) throw new IOException("HD-аудиоканал отключился.");
                long at=Interlocked.Read(ref lastReport);
                if((at<0 && clock.ElapsedMilliseconds>5000)||(at>=0 && clock.ElapsedMilliseconds-at>3000)) throw new IOException("Нет свежих входных данных контроллера.");
                runtime.Tick(DateTime.UtcNow);
                while(running && queue.TryDequeue(out var input))
                    if(clock.ElapsedMilliseconds-input.at<500) Handle(input.buttons); // discard stale events after a terminal pause
                if(!running) break;
                if(Console.KeyAvailable)
                {
                    var key=Console.ReadKey(true);
                    if(key.Key==ConsoleKey.Escape) running=false;
                    else if(key.KeyChar is >= '1' and <= '6')
                    { if(confirmation) { foundIndex=key.KeyChar-'1'; Play(2,false); } else { down=key.KeyChar-'1'; Play(0,false); } }
                    else if(!confirmation && key.Key>=ConsoleKey.F1 && key.Key<=ConsoleKey.F6) { up=key.Key-ConsoleKey.F1; Play(1,false); }
                    else if(confirmation && key.KeyChar=='d') Play(0,false);
                    else if(confirmation && key.KeyChar=='u') Play(1,false);
                    else if(key.KeyChar=='r') Handle(Triangle);
                    else if(key.KeyChar=='c') Handle(Cross);
                    else if(key.KeyChar=='s') Handle(Square);
                }
                if(compare && clock.ElapsedMilliseconds>=compareAt) Play(1,false);
                Thread.Sleep(3);
            }
            status=choice==null?"closed-without-choice":"choice-saved";
        }
        catch(Exception ex) { status="error"; log(ex.Message); }
        finally
        {
            cancel.Cancel();
            try { Stop("gallery exit"); pad.ResetTriggers(); pad.SetLightbar(0,0,0); }
            finally { hd?.Dispose(); reader?.Join(1000); Save(); log("Отчёт и выбор: "+report); }
        }
    }
    sealed class GalleryMotor : IMotorOut
    {
        public void Pattern((int delay,byte right,byte left,int ms)[] steps) => throw new InvalidOperationException("Галерея использует только HD.");
        public void StopAll() { }
    }
}
