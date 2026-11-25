using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using DsBridge.Haptics;

namespace DsBridge;

static class UnlockGallery
{
    public const int Count = 6;
    public const string Pack = "experiments/unlock-gallery.json";
    static Rule Find(HapticConfig cfg,int n) => cfg.Rules.Single(r=>r.Id==$"preview-unlock-{n}");
    public static IEnumerable<(string name,bool ok)> Checks(HapticConfig cfg)
    {
        var rules=cfg.Rules.Where(r=>r.Pack=="unlock-preview").ToArray();
        yield return ("Unlock gallery: six full 2500 ms compositions, no trigger output",rules.Length==Count && rules.All(r=>r.Pad==null) &&
            Enumerable.Range(1,Count).All(n=>rules.Count(r=>r.Id==$"preview-unlock-{n}")==1));
        foreach(var rule in rules)
        {
            float[] l=Array.Empty<float>(),r=Array.Empty<float>();
            foreach(var layer in rule.Effect.Layers)
            {
                var(a,b)=Presets.RenderLayer(layer.Preset,HapticRuntime.Resolve(layer,_=>null),48000);
                int length=Math.Max(l.Length,Math.Max(a.Length,b.Length)); Array.Resize(ref l,length); Array.Resize(ref r,length);
                for(int i=0;i<a.Length;i++) l[i]+=a[i]; for(int i=0;i<b.Length;i++) r[i]+=b[i];
            }
            var m=Analysis.Measure(l,r);
            yield return ($"{rule.Id}: PCM {m.LengthMs:F0} ms; {m.Short()}, DC {m.DcL:F3}",m.Peak<=.855 && m.OverKnee==0 && m.Clipped==0 &&
                Math.Abs(m.DcL)<.015 && Math.Abs(m.DcR)<.015 && m.Rms20>=.25 && m.Rms20<=.7 &&
                (l.Length==120000 && r.Length==120000 && m.ActiveMs>=2400 && m.EdgeEnd<.001) && m.Core+m.Low>=.65 && m.High<=.08 && l.SequenceEqual(r));
        }
    }

    public static void Run(HapticConfig cfg,DualSense pad,Action<string> log,string report,int startVariant=0)
    {
        if(pad.IsBluetooth) throw new InvalidOperationException("Нужен USB: галерея использует HD-хаптику, а не моторную эмуляцию.");
        report=Path.GetFullPath(report); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        using(new FileStream(report,FileMode.CreateNew,FileAccess.Write)) { }
        string recipe=File.ReadAllText(Path.Combine(cfg.Root,Pack));
        int current=-1,selected=0,seq=0; string status="started";
        var history=new List<object>(); var clock=Stopwatch.StartNew();
        var inputs=new ConcurrentQueue<(int buttons,long at)>(); long lastInput=-1;
        using var cancel=new CancellationTokenSource(); Thread? reader=null; HdEngine? hd=null; HapticRuntime? runtime=null;
        void Save()=>File.WriteAllText(report,JsonSerializer.Serialize(new {version=1,status,recipe,current=current+1,selected,updated=DateTimeOffset.Now,history},new JsonSerializerOptions{WriteIndented=true}));
        void Play(int n)
        {
            current=n; runtime!.StopAll("replace unlock preview");
            var ev=EventMonitor.ParseRecord($"{++seq}:lp:update:ph=unlockpreview,variant={n+1}")!;
            var decisions=runtime.Handle(ev,DateTime.UtcNow);
            if(decisions.Count!=1 || decisions[0].Route!="hd") throw new IOException("Образец не воспроизводится: "+string.Join("; ",decisions));
            log($"№{n+1}: {Find(cfg,n+1).Note}"); history.Add(new {ms=clock.ElapsedMilliseconds,variant=n+1}); Save();
        }
        try
        {
            hd=HdEngine.TryStart(pad,log);
            if(hd==null || !hd.Ready) throw new IOException("Аудиоустройство DualSense недоступно: нужна HD-хаптика по USB.");
            Replay.ApplyLimits(hd.Mixer,cfg.Limits); hd.StallMs=cfg.Limits.StallMs;
            runtime=new HapticRuntime(cfg,new HdOutLive {E=hd},new OfflineMotor());
            pad.ResetTriggers(); pad.SetLightbar(0,70,120,releaseStartupLight:true);
            int previous=0; bool primed=false;
            reader=pad.StartInputReader((data,len)=>
            {
                if(!LpGuideTest.Decode(data,len,false,out var shoulders,out var face,out _,out _)) return;
                int held=shoulders|(face<<4),pressed=held&~previous; previous=held;
                Interlocked.Exchange(ref lastInput,clock.ElapsedMilliseconds);
                if(!primed){if(held==0) primed=true; return;}
                if(pressed!=0) inputs.Enqueue((pressed,clock.ElapsedMilliseconds));
            },cancel.Token);
            Console.WriteLine("\nОТКРЫТИЕ ЗАМКА — 6 ПОЛНЫХ КОМПОЗИЦИЙ ПО 2500 мс. Только рукоятки, без динамика и дребезжания триггеров.");
            Console.WriteLine("L1/R1 — предыдущий/следующий; Cross или Triangle — повтор; Square — сохранить выбор; Circle/Esc — выход.");
            Console.WriteLine("Клавиатура: 1–6 — образец; Space — повтор; s — сохранить. В игру выбор автоматически не применяется.");
            for(int i=1;i<=Count;i++) Console.WriteLine($"{i}. {Find(cfg,i).Note}");
            Save(); if(startVariant>0) Play(startVariant-1); bool running=true;
            void Handle(int pressed)
            {
                if((pressed&0x400)!=0){running=false;return;}
                switch(pressed)
                {
                    case 1: Play(current<0?0:(current+Count-1)%Count); break;
                    case 2: Play(current<0?0:(current+1)%Count); break;
                    case 0x200: case 0x800: Play(Math.Max(0,current)); break;
                    case 0x100:
                        if(current<0){log("Сначала воспроизведите вариант.");break;}
                        selected=current+1;status="choice-saved";Save();log($"Выбран №{selected}. Выбор сохранён, игровые правила не изменены.");break;
                }
            }
            while(running)
            {
                if(hd.Failed) throw new IOException("Потерян HD-аудиоканал.");
                long at=Interlocked.Read(ref lastInput);
                if((at<0 && clock.ElapsedMilliseconds>5000)||(at>=0 && clock.ElapsedMilliseconds-at>3000)) throw new IOException("Нет свежих данных контроллера.");
                runtime.Tick(DateTime.UtcNow);
                while(running && inputs.TryDequeue(out var input)) if(clock.ElapsedMilliseconds-input.at<500) Handle(input.buttons);
                if(!running) break;
                if(Console.KeyAvailable)
                {
                    var key=Console.ReadKey(true);
                    if(key.Key==ConsoleKey.Escape) running=false;
                    else if(key.KeyChar is >= '1' and <= '6') Play(key.KeyChar-'1');
                    else if(key.Key==ConsoleKey.Spacebar) Handle(0x200);
                    else if(key.KeyChar=='s') Handle(0x100);
                }
                Thread.Sleep(3);
            }
            status=selected>0?"choice-saved":"closed-without-choice";
        }
        catch(Exception ex){status="error";log(ex.Message);}
        finally
        {
            cancel.Cancel();
            try{runtime?.StopAll("unlock preview exit");pad.ResetTriggers();pad.SetLightbar(0,0,0);}
            finally{hd?.Dispose();reader?.Join(1000);Save();log("Отчёт: "+report);}
        }
    }
}
