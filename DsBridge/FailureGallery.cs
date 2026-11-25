using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using DsBridge.Haptics;

namespace DsBridge;

static class FailureGallery
{
    public const int Count = 6;
    public const string SyncPack = "experiments/failure-animation-sync.json";
    public const string Pack = "experiments/failure-gallery.json";
    static Rule Find(HapticConfig cfg,int n) => cfg.Rules.Single(r=>r.Id==$"preview-failure-{n}");
    public static IEnumerable<(string name,bool ok)> Checks(HapticConfig cfg)
    {
        var rules=cfg.Rules.Where(r=>r.Pack=="failure-preview").ToArray();
        yield return ("Failure gallery: six forceful single fractures in a 2400 ms phase, no trigger output",rules.Length==Count && rules.All(r=>r.Pad==null) &&
            Enumerable.Range(1,Count).All(n=>rules.Count(r=>r.Id==$"preview-failure-{n}")==1));
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
                Math.Abs(m.DcL)<.015 && Math.Abs(m.DcR)<.015 && m.Rms20>=.35 && m.Rms20<=.65 &&
                (l.Length==115200 && r.Length==115200 && m.ActiveMs>=250 && m.ActiveMs<=900 && m.EdgeEnd<.001) && m.CentroidHz>=150 && m.CentroidHz<=550 && m.Sub+m.Low<=.15 && m.High<=.15 && l.SequenceEqual(r));
            var ticks=rule.Effect.Layers.Where(x=>x.Preset.Equals("Tick",StringComparison.OrdinalIgnoreCase)).Select(x=>HapticRuntime.Resolve(x,_=>null)).ToArray();
            yield return ($"{rule.Id}: one fracture onset, no cascades, silence after first second",ticks.Length==2 &&
                ticks.All(x=>Presets.N(x,"count",1)==1 && Presets.N(x,"pairGapMs",0)==0) &&
                Presets.N(ticks[0],"delayMs",0)==Presets.N(ticks[1],"delayMs",0) &&
                rule.Effect.Layers.All(x=>x.Preset is "Tick" or "Charge" or "Impact" or "Continuous") &&
                rule.Effect.Layers.Count(x=>x.Preset=="Impact")==1 &&
                rule.Effect.Layers.Where(x=>x.Preset=="Impact").Select(x=>HapticRuntime.Resolve(x,_=>null)).All(x=>Presets.N(x,"delayMs",0)==Presets.N(ticks[0],"delayMs",0) && Presets.N(x,"durationMs",0)<=130 && Presets.N(x,"tail",0)==0) &&
                l.Skip(48000).All(x=>x==0) && r.Skip(48000).All(x=>x==0));
        }
    }

    public static IEnumerable<(string name,bool ok)> SyncChecks(HapticConfig cfg)
    {
        var rules=cfg.Rules.Where(r=>r.Pack=="failure-preview").ToArray();
        yield return ("Animation profile: one cue, no pad overrides",cfg.Ok && rules.Length==1 && rules[0].Pad==null);
        if(rules.Length!=1)yield break;
        float[] l=[],r=[];
        foreach(var layer in rules[0].Effect.Layers)
        {
            var(a,b)=Presets.RenderLayer(layer.Preset,HapticRuntime.Resolve(layer,_=>null),48000);
            int n=Math.Max(l.Length,Math.Max(a.Length,b.Length));Array.Resize(ref l,n);Array.Resize(ref r,n);
            for(int i=0;i<a.Length;i++)l[i]+=a[i];for(int i=0;i<b.Length;i++)r[i]+=b[i];
        }
        var m=Analysis.Measure(l,r);
        yield return ($"Animation profile: {m.Short()}",l.Length==115200 && l.SequenceEqual(r) && m.Peak>.7 && m.Peak<=.855 && m.OverKnee==0 && m.Clipped==0 && Math.Abs(m.DcL)<.015 && m.Rms20>=.45 && m.Rms20<=.65);
        double Peak(int start,int end)=>l.Skip(start*48).Take((end-start)*48).Select(x=>Math.Abs(x)).DefaultIfEmpty().Max();
        yield return ("Animation phases: strong fracture at336ms, withdrawal at1233-1502ms, silence after1600ms",Peak(336,380)>.65 && Peak(1200,1500)>.03 && l.Skip(1600*48).All(x=>x==0));
        var impact=rules[0].Effect.Layers.Where(x=>x.Preset=="Impact").ToArray();
        var ticks=rules[0].Effect.Layers.Where(x=>x.Preset=="Tick").Select(x=>HapticRuntime.Resolve(x,_=>null)).ToArray();
        yield return ("Native notify anchors and no repeated fracture",impact.Length==1 && ticks.Length==3 && ticks.All(x=>Presets.N(x,"count",1)==1) &&
            ticks.Count(x=>Math.Abs(Presets.N(x,"delayMs",0)-336.3003730773926)<.001)==2 && ticks.Count(x=>Math.Abs(Presets.N(x,"delayMs",0)-1501.8194913864136)<.001)==1);
    }
    public static void Run(HapticConfig cfg,DualSense pad,Action<string> log,string report,int startVariant=0,string recipePack=Pack)
    {
        if(pad.IsBluetooth) throw new InvalidOperationException("Нужен USB: галерея использует HD-хаптику, а не моторную эмуляцию.");
        report=Path.GetFullPath(report); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        using(new FileStream(report,FileMode.CreateNew,FileAccess.Write)) { }
        string recipe=File.ReadAllText(Path.Combine(cfg.Root,recipePack));
        int count=cfg.Rules.Count(r=>r.Pack=="failure-preview");
        using var presentation=JsonDocument.Parse(recipe);
        string title=presentation.RootElement.TryGetProperty("title",out var label)?label.GetString()!:"ПОЛОМКА — 6 ВАРИАНТОВ ЖЁСТКОГО ОДИНОЧНОГО ИЗЛОМА. Фаза 2400 мс.";
        int current=-1,selected=0,seq=0; string status="started";
        var history=new List<object>(); var clock=Stopwatch.StartNew();
        var inputs=new ConcurrentQueue<(int buttons,long at)>(); long lastInput=-1;
        using var cancel=new CancellationTokenSource(); Thread? reader=null; HdEngine? hd=null; HapticRuntime? runtime=null;
        void Save()=>File.WriteAllText(report,JsonSerializer.Serialize(new {version=1,status,recipe,current=current+1,selected,updated=DateTimeOffset.Now,history},new JsonSerializerOptions{WriteIndented=true}));
        void Play(int n)
        {
            current=n; runtime!.StopAll("replace failure preview");
            var ev=EventMonitor.ParseRecord($"{++seq}:lp:update:ph=failurepreview,variant={n+1}")!;
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
            Console.WriteLine("\n"+title+" Только рукоятки, динамик выключен.");
            Console.WriteLine("L1/R1 — предыдущий/следующий; Cross или Triangle — повтор; Square — сохранить выбор; Circle/Esc — выход.");
            Console.WriteLine("Клавиатура: 1–6 — образец; Space — повтор; s — сохранить. В игру выбор автоматически не применяется.");
            for(int i=1;i<=count;i++) Console.WriteLine($"{i}. {Find(cfg,i).Note}");
            Save(); if(startVariant>0) Play(startVariant-1); bool running=true;
            void Handle(int pressed)
            {
                if((pressed&0x400)!=0){running=false;return;}
                switch(pressed)
                {
                    case 1: Play(current<0?0:(current+count-1)%count); break;
                    case 2: Play(current<0?0:(current+1)%count); break;
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
                    else if(key.KeyChar is >= '1' and <= '6' && key.KeyChar-'1'<count) Play(key.KeyChar-'1');
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
            try{runtime?.StopAll("failure preview exit");pad.ResetTriggers();pad.SetLightbar(0,0,0);}
            finally{hd?.Dispose();reader?.Join(1000);Save();log("Отчёт: "+report);}
        }
    }
}
