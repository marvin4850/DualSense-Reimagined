using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using DsBridge.Haptics;

namespace DsBridge;

static class TriggerClickGallery
{
    public const string HdPack = "experiments/trigger-click-hd.json";
    public const string Pack = "experiments/trigger-click-gallery.json";
    public sealed record Variant(string Name,string Mode,int Pos,int End,int Strength,int Snap,int PulseAmp,int PulseHz,int PulseMs,int DelayMs,double Fire)
    {
        public byte[] Load() => new TriggerProfile(Name,Mode,Pos,Strength,0,0,End,Snap:Snap).Bytes();
        public byte[] Pulse() => DualSense.EffectVibration(0,PulseAmp,PulseHz);
    }
    public static Variant[] Read(string path) => JsonSerializer.Deserialize<Variant[]>(File.ReadAllText(path),new JsonSerializerOptions{PropertyNameCaseInsensitive=true}) ?? [];
    enum Output { None, Load, Pulse, Off }
    // Independent fingers: one tail per pull. Click tails finish despite release; pure probe cancels on release.
    sealed class Finger(Variant variant,bool finishPulse=false)
    {
        int state; long due;
        public bool Busy => state is 2 or 3;
        public Output Step(int value,long now)
        {
            if(state==3 && finishPulse)
            {
                if(now<due)return Output.None;
                state=value<=51?1:4;
                return state==1?Output.Load:Output.Off;
            }
            if(value<=51) { if(state==1) return Output.None; state=1; return Output.Load; }
            if(state==1 && value>=Math.Round(variant.Fire*255)) {state=2;due=now+variant.DelayMs;}
            if(state==2 && now>=due) {state=3;due=now+variant.PulseMs;return Output.Pulse;}
            if(state==3 && now>=due) {state=4;return Output.Off;}
            return Output.None;
        }
    }
    public static IEnumerable<(string name,bool ok)> Checks(Variant[] variants)
    {
        bool valid=variants.Length==6 && variants.All(v=>v.Mode is "bow" or "weapon" && v.Pos>=0 && v.Pos<v.End && v.End<=8 &&
            (v.Mode!="weapon" || v.Pos>=2) && v.Strength is >=4 and <=8 && v.Snap is >=1 and <=8 &&
            v.PulseAmp is >=1 and <=3 && v.PulseHz is >=40 and <=240 && v.PulseMs is >=10 and <=150 &&
            v.DelayMs is >=0 and <=35 && v.Fire is >=.45 and <=.8);
        yield return ("Trigger clicks: six bounded mechanical profiles with short light tails",valid);
        if(!valid) yield break;
        yield return ("Trigger clicks: six distinct recipes",variants.Select(v=>JsonSerializer.Serialize(v with {Name=""})).Distinct().Count()==6);
        foreach(var v in variants)
        {
            yield return ($"{v.Name}: {v.Mode} {v.Pos}->{v.End}, load {v.Strength}, snap {v.Snap}; tail {v.PulseMs} ms / {v.PulseAmp} / {v.PulseHz} Hz",v.Load().Length==11 && v.Pulse().Length==11);
            var f=new Finger(v); int full=255;
            bool ok=f.Step(full,0)==Output.None && f.Step(0,1)==Output.Load && f.Step(full,10)==Output.None &&
                f.Step(full,10+v.DelayMs-1)==Output.None && f.Step(full,10+v.DelayMs)==Output.Pulse &&
                f.Step(full,10+v.DelayMs+v.PulseMs)==Output.Off && f.Step(full,500)==Output.None && f.Step(0,510)==Output.Load;
            f.Step(full,520); ok &= f.Step(0,521)==Output.Load && f.Step(0,900)==Output.None;
            var left=new Finger(v);var right=new Finger(v);left.Step(0,0);right.Step(0,0);left.Step(full,10);right.Step(full,10);
            ok &= left.Step(full,10+v.DelayMs)==Output.Pulse && right.Step(full,10+v.DelayMs)==Output.Pulse;
            var finish=new Finger(v,true);finish.Step(0,0);finish.Step(full,10);
            ok &= finish.Step(full,10+v.DelayMs)==Output.Pulse && finish.Step(0,10+v.DelayMs+v.PulseMs-1)==Output.None &&
                finish.Step(0,10+v.DelayMs+v.PulseMs)==Output.Load && finish.Step(0,1000)==Output.None;
            yield return ($"{v.Name}: release gating, one complete tail, pending cancellation, independent fingers",ok);
        }
    }
    public static IEnumerable<(string name,bool ok)> ProbeChecks(Variant[] variants)
    {
        bool valid=variants.Length==1 && variants[0].PulseAmp==6 && variants[0].PulseHz==40 && variants[0].PulseMs==800 && variants[0].DelayMs==0 && variants[0].Fire==.30;
        yield return ("Vibration probe: one explicit 40 Hz / 6 / 800 ms recipe",valid);
        yield return ("Trigger vibration encoding: Nielk1 full-zone reference vector",Convert.ToHexString(DualSense.EffectVibration(0,8,40))=="26FF03FFFFFF3F00002800");
        if(!valid)yield break;
        var f=new Finger(variants[0]);
        bool ok=f.Step(0,0)==Output.Load && f.Step(100,10)==Output.Pulse && f.Step(170,809)==Output.None && f.Step(170,810)==Output.Off && f.Step(170,1000)==Output.None;
        var cancel=new Finger(variants[0]);cancel.Step(0,0);cancel.Step(100,10);
        ok &= cancel.Step(0,100)==Output.Load && cancel.Step(0,2000)==Output.None;
        yield return ("Vibration probe: 800 ms cap, no repeat while held, release cancellation",ok);
    }
    public static IEnumerable<(string name,bool ok)> HdChecks(HapticConfig cfg)
    {
        var rules=cfg.Rules.Where(r=>r.Pack=="trigger-click-hd").ToArray();
        bool valid=cfg.Ok && rules.Length==1 && rules[0].Pad==null && rules[0].Effect.Layers.Count==1;
        yield return ("Dry trigger HD: one data-driven cue without trigger overrides",valid);
        if(!valid)yield break;
        var layer=rules[0].Effect.Layers[0];
        var (l,r)=Presets.RenderLayer(layer.Preset,HapticRuntime.Resolve(layer,_=>null),48000);
        var m=Analysis.Measure(l,r);
        yield return ($"Dry trigger HD: PCM {m.LengthMs:F0} ms, {m.Short()}",l.Length==2160 && r.Length==2160 && m.ActiveMs>=35 && m.ActiveMs<=42 && l.SequenceEqual(r) && m.CentroidHz>=300 && m.Peak*2<.855 && m.Clipped==0 && m.OverKnee==0 && Math.Abs(m.DcL)<.015 && m.Rms20>=.1 && m.Rms20<=.5 && m.EdgeEnd<.001);
        var rt=new HapticRuntime(cfg,new OfflineHd(),new OfflineMotor());
        var now=DateTime.UtcNow;
        var no=rt.Handle(EventMonitor.ParseRecord("1:lp:update:ph=triggerclick,variant=1,side=L")!,now);
        var yes=rt.Handle(EventMonitor.ParseRecord("2:lp:update:ph=triggerclick,variant=2,side=L")!,now.AddSeconds(1));
        yield return ("Dry trigger HD: only selected dry click emits exactly one HD voice",no.Count==0 && yes.Count==1 && yes[0].RuleId=="preview-dry-trigger-hd" && yes[0].Route=="hd");
        rt.StopAll("check finished");
    }
    public static void Run(DualSense pad,Variant[] variants,string recipe,string report,Action<string> log,bool probe=false,int startVariant=1,HapticConfig? hdCfg=null)
    {
        report=Path.GetFullPath(report);Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        using(new FileStream(report,FileMode.CreateNew,FileAccess.Write)){}
        int current=0,selected=0;double trim=0;string status="started";Variant? chosen=null;
        Finger l=new(variants[0],!probe),r=new(variants[0],!probe);
        var clock=Stopwatch.StartNew();var history=new List<object>();var notices=new Queue<string>();bool dirty=false;long[] pulseAt=[-1,-1];
        var gate=new object();int lv=0,rv=0;long last=-1;var buttons=new ConcurrentQueue<int>();
        using var cancel=new CancellationTokenSource();Thread? reader=null;HdEngine? hd=null;HapticRuntime? body=null;int hdSeq=0;long audioSnapshotDue=-1;
        string hdRecipe=hdCfg==null?"":File.ReadAllText(Path.Combine(hdCfg.Root,HdPack));
        void Save()=>File.WriteAllText(report,JsonSerializer.Serialize(new{version=3,status,probe,recipe,hdRecipe,current=current+1,selected,chosen,trim,audio=hd==null?null:new{hd.Ready,hd.Rate,hd.ArmReports,hd.Stalls,hd.Mixer.Started,hd.Mixer.RenderedSamples,hd.Mixer.PeakOut},history},new JsonSerializerOptions{WriteIndented=true}));
        Variant Active()=>probe?variants[current]:variants[current] with {Fire=Math.Clamp(variants[current].Fire+trim,.4,.85)};
        void Choose(int n)
        {
            current=n;body?.StopAll("variant change");pad.ResetTriggers();l=new(Active(),!probe);r=new(Active(),!probe);
            var v=Active();if(probe)log(v.Name);else log($"№{n+1}: {v.Name}. {v.Mode} {v.Pos}→{v.End}, сила {v.Strength}, срыв {v.Snap}; хвост {v.PulseMs} мс / ампл. {v.PulseAmp} / {v.PulseHz} Гц. Порог {v.Fire:P0}.");
            log(probe ? "ПРОБА: отпустите всё. Мягко нажмите R2 до СЕРЕДИНЫ хода и удерживайте 1 секунду. Не прижимайте к упору. Отпускание сразу прекращает вибрацию." : "Отпустите L2/R2, затем плавно продавите через щелчок. После хвоста ход свободен до отпускания.");
            history.Add(new{ms=clock.ElapsedMilliseconds,kind="select",variant=n+1,fire=v.Fire});Save();
        }
        void PlayHd(string side,bool solo,long triggerAt)
        {
            if(body==null){log("HD недоступна в этом режиме.");return;}
            if(solo)pad.ArmHapticsFull();
            var cue=EventMonitor.ParseRecord($"{++hdSeq}:lp:update:ph=triggerclick,variant=2,side={side}")!;
            var decisions=body.Handle(cue,DateTime.UtcNow);
            if(decisions.Count!=1 || decisions[0].Route!="hd")throw new IOException("HD-щелчок не запущен: "+string.Join("; ",decisions));
            history.Add(new{ms=clock.ElapsedMilliseconds,kind=solo?"hd-only":"hd-click",side,rule="preview-dry-trigger-hd",afterTriggerMs=clock.ElapsedMilliseconds-triggerAt});
            audioSnapshotDue=clock.ElapsedMilliseconds+180;
            notices.Enqueue(solo?"✕: отправлен ТОЛЬКО HD-щелчок рукояток, без триггерного импульса.":"HD-щелчок вместе с триггером");dirty=true;
        }
        void Send(bool left,Output action)
        {
            if(action==Output.None)return;
            var bytes=action switch{Output.Load=>probe?DualSense.EffectOff():Active().Load(),Output.Pulse=>Active().Pulse(),_=>DualSense.EffectOff()};
            pad.SetTriggers(left?bytes:null,left?null:bytes);
            long triggerAt=clock.ElapsedMilliseconds;
            if(action==Output.Pulse && !probe && current==1 && body!=null)PlayHd(left?"L2":"R2",false,triggerAt);
            int finger=left?0:1;
            if(action!=Output.Pulse && pulseAt[finger]>=0){history.Add(new{ms=clock.ElapsedMilliseconds,kind="pulse-end",targetMs=Active().PulseMs,side=left?"L2":"R2",elapsedMs=clock.ElapsedMilliseconds-pulseAt[finger]});pulseAt[finger]=-1;dirty=true;}
            if(action==Output.Pulse){pulseAt[finger]=clock.ElapsedMilliseconds;history.Add(new{ms=clock.ElapsedMilliseconds,kind="pulse",variant=current+1,payload=Convert.ToHexString(bytes),side=left?"L2":"R2",value=left?lv:rv,fire=Active().Fire});notices.Enqueue((left?"L2":"R2")+(probe?": отправлена диагностическая вибрация 40 Гц":": щелчок → короткий хвост"));dirty=true;}
        }
        try
        {
            if(hdCfg!=null && pad.IsBluetooth)throw new IOException("Для HD-щелчка нужен USB.");
            // Clear rumble BEFORE audio initialization: even a zero rumble command selects emulation.
            pad.SetRumble(0,0);pad.ResetTriggers();
            // USB PCM activates the pad; only dry click cues queue body samples, speakers remain silent.
            if(!pad.IsBluetooth){hd=HdEngine.TryStart(pad,log);if(hd==null || !hd.Ready)throw new IOException("Недоступен USB-аудиоканал для активации контроллера.");}
            if(hdCfg!=null)
            {
                Replay.ApplyLimits(hd!.Mixer,hdCfg.Limits);hd.StallMs=hdCfg.Limits.StallMs;
                body=new HapticRuntime(hdCfg,new HdOutLive{E=hd},new OfflineMotor());
                Console.WriteLine("№2 СУХОЙ СПУСК + HD: металлический щелчок 360 Гц / 40 мс вместе с хвостом триггера. Динамик выключен. ✕ или Space — тот же HD отдельно; сначала отпустите триггеры.");
            }
            int previous=0;bool primed=false;
            reader=pad.StartInputReader((buf,len)=>
            {
                if(!LpGuideTest.Decode(buf,len,pad.IsBluetooth,out var shoulders,out var face,out var a,out var b))return;
                int held=shoulders|(face<<4),pressed=held&~previous;previous=held;
                lock(gate){lv=a;rv=b;last=clock.ElapsedMilliseconds;}
                if(!primed){if(held==0)primed=true;return;}if(pressed!=0)buttons.Enqueue(pressed);
            },cancel.Token);
            Console.WriteLine(probe ? "\nПРОВЕРКА ВИБРАЦИИ ТРИГГЕРА: только 40 Гц, без сопротивления, без вибрации корпуса." : "\nШЕСТЬ ЩЕЛЧКОВ ТРИГГЕРОВ: сопротивление → механический срыв → лёгкий короткий хвост.");
            Console.WriteLine(probe ? "Проверьте один R2: половина хода, лёгкое нажатие. Повтор: отпустить и нажать снова. ○/Esc: выход." : "L1/R1 или 1–6: вариант. L2/R2: пробовать. □/s: сохранить. ○/Esc: выход.");
            if(!probe) Console.WriteLine("Если хвост приходит раньше/позже щелчка: - / = сдвигают порог на 2%. Настройки игры не меняются.");
            for(int i=0;i<variants.Length;i++)Console.WriteLine($"{i+1}. {variants[i].Name}");
            Choose(startVariant-1);bool running=true;
            void Control(int pressed)
            {
                if((pressed&0x400)!=0){running=false;return;}
                if((pressed&1)!=0)Choose((current+variants.Length-1)%variants.Length);
                else if((pressed&2)!=0)Choose((current+1)%variants.Length);
                else if((pressed&0x200)!=0 && !probe)PlayHd("both",true,clock.ElapsedMilliseconds);
                else if((pressed&0x100)!=0){selected=current+1;chosen=Active();status="choice-saved";Save();log($"Сохранён №{selected}: {variants[current].Name}");}
            }
            while(running)
            {
                int a,b;long inputAt;lock(gate){a=lv;b=rv;inputAt=last;}
                if((inputAt<0 && clock.ElapsedMilliseconds>5000)||(inputAt>=0 && clock.ElapsedMilliseconds-inputAt>2000))throw new IOException("Нет свежего ввода: выход со сбросом триггеров.");
                if(hd?.Failed==true)throw new IOException("USB-аудиоканал потерян.");
                while(buttons.TryDequeue(out var key)&&running)Control(key);
                if(Console.KeyAvailable)
                {
                    var key=Console.ReadKey(true);
                    if(key.Key==ConsoleKey.Escape)running=false;
                    else if(key.KeyChar is >= '1' and <= '6' && key.KeyChar-'1'<variants.Length)Choose(key.KeyChar-'1');
                    else if(key.Key==ConsoleKey.Spacebar && !probe)Control(0x200);
                    else if(key.KeyChar=='s')Control(0x100);
                    else if(!probe && (key.KeyChar is '-' or '=')){trim=Math.Clamp(trim+(key.KeyChar=='-'?-.02:.02),-.15,.15);Choose(current);}
                }
                if(!running)break;
                if(inputAt>=0){Send(true,l.Step(a,clock.ElapsedMilliseconds));Send(false,r.Step(b,clock.ElapsedMilliseconds));}
                if(!l.Busy && !r.Busy && dirty){while(notices.TryDequeue(out var notice))log(notice);Save();dirty=false;}
                if(audioSnapshotDue>=0 && clock.ElapsedMilliseconds>=audioSnapshotDue && !l.Busy && !r.Busy){Save();audioSnapshotDue=-1;}
                body?.Tick(DateTime.UtcNow);
                Thread.Sleep(2);
            }
            status=selected>0?"choice-saved":"closed-without-choice";
        }
        catch(Exception ex){status="error";log(ex.Message);}
        finally
        {
            cancel.Cancel();reader?.Join(1000);
            try{body?.StopAll("trigger gallery exit");hd?.Dispose();}finally{pad.ResetTriggers();pad.SetRumble(0,0);Save();log("Отчёт: "+report);}
        }
    }
}
