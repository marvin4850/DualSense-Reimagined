using DsBridge.Haptics;

namespace DsBridge;

static class LpSelectedChecks
{
    public static IEnumerable<(string name,bool ok)> Run()
    {
        var path=HapticConfig.Locate(null);
        if(path==null) yield break;
        var cfg=HapticConfig.Load(path);
        if(!cfg.Packs.Any(p=>p.Name=="lockpick-selected" && p.Enabled)) yield break;
        var legacy=HapticConfig.Load(path,new[] { "lockpick-selected" });
        yield return ("Selected triad: original trigger profiles and lock physics retained", cfg.TriggerProfiles.Count==legacy.TriggerProfiles.Count &&
            cfg.TriggerProfiles.All(p=>legacy.TriggerProfiles.TryGetValue(p.Key,out var old) && p.Value.Bytes().SequenceEqual(old.Bytes())) &&
            legacy.Rules.Where(r=>r.Pack=="lockpick" && r.Id is not ("lp-guide-sel" or "lp-guide-plate" or "lp-select" or "lp-unlock"))
                .All(r=>cfg.Rules.Any(n=>n.Id==r.Id && n.Pack==r.Pack)));
        yield return ("Selected triad: replaces old height/centre arrows, no periodic trigger vibration", !cfg.Rules.Any(r=>r.Id is "lp-guide-sel" or "lp-guide-plate") &&
            // v27 (user request): the only trigger output allowed here is the one-shot knock pulse of lp-step-end
            cfg.Rules.Where(r=>r.Pack=="lockpick-selected").All(r=>r.Pad==null || (r.Id=="lp-step-end" && r.Pad.Pulse!=null && r.Pad.L2==null && r.Pad.R2==null && !r.Pad.Reset)
                || (r.Id=="lp-unlock-chain" && r.Pad.Reset && r.Pad.L2==null && r.Pad.R2==null && r.Pad.Pulse==null)
                || (r.Id=="lp-press-tail" && r.Pad.Pulse!=null && r.Pad.PulseSide!=null && r.Pad.PulseHold && r.Pad.L2==null && r.Pad.R2==null && !r.Pad.Reset)
                || (r.Id=="lp-trigger-up" && r.Pad.Release!=null && r.Pad.Pulse==null && r.Pad.L2==null && r.Pad.R2==null && !r.Pad.Reset))
            && cfg.Rules.Count(r=>r.Id.StartsWith("lp-height-"))==3);
        {
            // v28: the lock opens with the user's chosen 2500 ms composition (unlock-selection.json), triggers off
            var chain=cfg.Rules.SingleOrDefault(r=>r.Id=="lp-unlock-chain");
            var (ul,ur)=chain!=null ? HapticRuntime.RenderHd(chain.Effect,_=>null,48000) : (new float[0],new float[0]);
            float peak=ul.Length>0 ? ul.Max(Math.Abs) : 0f;
            yield return ("Unlock: 'Цепь освобождения' replaces lp-unlock, 15 layers, 2500 ms (120000 frames), no clipping, triggers reset",
                chain!=null && !cfg.Rules.Any(r=>r.Id=="lp-unlock") && chain.Effect.Layers.Count==15 && ul.Length==120000 && ul.SequenceEqual(ur)
                && peak>0.3f && peak<0.95f && chain.Pad!=null && chain.Pad.Reset && chain.Where.TryGetValue("res",out var res) && res.Contains("open"));
            var pathP=cfg.TriggerProfiles["path"]; var offP=cfg.TriggerProfiles["offpath"];
            var tailP=cfg.TriggerProfiles["tail"];
            yield return ("Triggers: planned move = 'Сухой спуск' Weapon 4->5/8 (0x25), tail = vibration 0/3/40 Hz (0x26), off-path = max feedback, wall kept",
                pathP.Mode=="weapon" && pathP.Pos==4 && pathP.End==5 && pathP.Str==8 && pathP.Bytes()[0]==0x25
                && tailP.Mode=="vibration" && tailP.Pos==0 && tailP.Amp==3 && tailP.Hz==40 && tailP.Bytes()[0]==0x26
                && offP.Mode=="feedback" && offP.Str==8 && cfg.TriggerProfiles.ContainsKey("wall"));
        }
        cfg.AddSupplementalPack(LpHeightGallery.Pack); cfg.AddSupplementalPack(LpHeightGallery.ConfirmPack);
        foreach(var (name,source) in new[] { ("down","height-down-5"),("up","height-up-3"),("found","height-found-5") })
        {
            var selected=cfg.Rules.Single(r=>r.Id=="lp-height-"+name);
            var expected=cfg.Rules.Single(r=>r.Id==source);
            var a=HapticRuntime.RenderHd(selected.Effect,_=>null,48000);
            var b=HapticRuntime.RenderHd(expected.Effect,_=>null,48000);
            yield return ($"Selected {name}: exact approved waveform, no extra directional tail", a.l.SequenceEqual(b.l) && a.r.SequenceEqual(b.r));
        }
        var hd=new TestHd(); var motor=new TestMotor(); var pad=new TestPad();
        var rt=new HapticRuntime(cfg,hd,motor) { Pad=pad };
        var time=new DateTime(2026,11,14,0,0,0,DateTimeKind.Utc); int seq=0;
        List<Decision> Ev(string ctx,int ms=0,string phase="update") => rt.Handle(EventMonitor.ParseRecord($"{++seq}:lp:{phase}:{ctx}")!,time.AddMilliseconds(ms));
        Ev("ph=trig,l=click_soft,r=click_ok,next=D,skill=2");
        Ev("ph=guide,kind=sel,side=L,prof=2",10);
        long downId=hd.Last;
        Ev("ph=alive",12); Ev("ph=trig,l=click_soft,r=click_ok,next=D,skill=2",13);
        yield return ("Selected triad: heartbeat and unchanged trigger context do not truncate a cue", rt.ActiveContinuous.Any() && !hd.Stopped.Any(x=>x.id==downId));
        Ev("ph=navpress,k=W,cur=1,requested=1",30);
        Ev("ph=sel,cur=2,n=5",40);
        yield return ("Selected triad: navigation input cancels old hint in 8ms and always acknowledges input", hd.Stopped.Contains((downId,8)) && rt.RuleHits.GetValueOrDefault("lp-nav-contact")==1 && hd.LastTag=="lp-select" && !rt.ActiveContinuous.Any());
        hd.Render(4800);
        Ev("ph=guide,kind=sel,side=R,prof=2",140);
        hd.Render(960); // user acts 20ms into the up pattern, before its late mechanical catch
        long upId=hd.Last;
        Ev("ph=press,dir=R,valid=1,tx=1,via=pad",160);
        bool tickRightOnly=pad.Pulsed=="tail" && !pad.PulseL && pad.PulseR && pad.PulseDelay==5 && pad.PulseMs==150 && pad.PulseHold;
        Ev("ph=trigup,side=R",165);
        bool upRight=pad.Released=="R";
        var after=hd.Render(10000);
        yield return ("Selected triad: cancelled late catch never sounds after fade", hd.Stopped.Contains((upId,8)) &&
            after.l.Skip(384).All(x=>x==0) && after.r.Skip(384).All(x=>x==0) && pad.Right=="click_ok" && pad.Left=="click_soft");
        var decisions=Ev("ph=guide,kind=plate,side=R,prof=2",300);
        long foundId=hd.Last;
        yield return ("Selected triad: correct plate gives only deep-seat confirmation", decisions.Count==1 && decisions[0].RuleId=="lp-height-found" && hd.LastTag=="lp-height-found");
        Ev("res=close",320,"complete");
        yield return ("Selected triad: closing lock cancels confirmation and resets triggers", hd.Stopped.Contains((foundId,8)) && !rt.ActiveContinuous.Any() && pad.Resets>0);
        Ev("ph=guide,kind=sel,side=L,prof=2",400);
        long lostId=hd.Last;
        rt.InterruptContinuous("lost game");
        yield return ("Selected triad: lost-game cleanup cancels guidance", hd.Stopped.Any(x=>x.id==lostId) && !rt.ActiveContinuous.Any());
        hd.Render(4800);
        int before=rt.RuleHits.GetValueOrDefault("lp-nav-contact");
        for(int i=0;i<10;i++) { Ev("ph=navpress,k=S,cur=1,requested=1",500+i*50); hd.Render(2400); }
        yield return ("Navigation: every received press at 20Hz gets contact, no cooldown drops", rt.RuleHits.GetValueOrDefault("lp-nav-contact")==before+10 && !rt.CooldownDrops.ContainsKey("lp-nav-contact"));
        var leave=Ev("ph=leave,cur=1,target=2,next=W,scope=step",1100);
        long leaveId=hd.Last; Ev("ph=alive",1110);
        bool kept=rt.ActiveContinuous.Any(); Ev("ph=navpress,k=W,cur=1,requested=1",1120);
        yield return ("Step end: distinct stop-turning cue survives heartbeat and cancels on next input", leave.Any(d=>d.RuleId=="lp-step-end") && kept && hd.Stopped.Contains((leaveId,8)));
        yield return ("Move: 'Сухой спуск' tail on the pulled trigger only (R2 for dir=R): 5 ms after the fire point, 150 ms, held; release = trigup R", tickRightOnly && upRight);
        foreach(var c in PadAutomaton()) yield return c;
        {
            // Bluetooth HD haptics (report 0x32): layout + CRC against a zlib reference (crc32(0xA2 + report[0..138)) = 0x99AA11A9)
            var blk=new sbyte[64]; for(int i=0;i<64;i++) blk[i]=unchecked((sbyte)(byte)((i*3)&0xFF));
            var rep=BtHapticsReport.Build(blk,5,7,547);
            uint crc=(uint)(rep[138]|rep[139]<<8|rep[140]<<16|rep[141]<<24);
            yield return ("BT HD report 0x32: 547-byte buffer, 0x32 / seq<<4 / 0x91 04 66 30 30 / counter / 0x92 40 / 64 samples at 10, CRC = zlib reference",
                rep.Length==547 && rep[0]==0x32 && rep[1]==0x50 && rep[2]==0x91 && rep[3]==4 && rep[4]==0x66 && rep[5]==48 && rep[6]==48 && rep[7]==7
                && rep[8]==0x92 && rep[9]==0x40 && rep[10]==0 && rep[73]==189 && rep.Skip(74).Take(64).All(b=>b==0) && crc==0x99AA11A9u && rep.Skip(142).All(b=>b==0));
            float[] Sine(double hz,int ms){ var x=new float[48*ms]; for(int i=0;i<x.Length;i++) x[i]=(float)(0.8*Math.Sin(2*Math.PI*hz*i/48000)); return x; }
            double Amp(sbyte[] s3){ return s3.Where((v,i)=>i%2==0 && i>40 && i<s3.Length-40).Select(v=>Math.Abs((int)v)).Max()/127.0; }
            var low=BtHapticsReport.From48k(Sine(150,200),Sine(150,200)); var high=BtHapticsReport.From48k(Sine(2800,200),Sine(2800,200));
            yield return ($"BT HD resample 48k -> 3k: 200 ms = 600 frames, 150 Hz kept ({Amp(low):F2} of 0.80), 2.8 kHz suppressed ({Amp(high):F2})",
                low.Length==1200 && Amp(low)>0.75 && Amp(high)<0.05);
            int Lv(double x)=>BtHapticsReport.Level(x,2.0);
            yield return ($"BT HD level x2 (tanh, contrast kept): 0.2 -> {Lv(0.2)}, 0.4 -> {Lv(0.4)}, 0.6 -> {Lv(0.6)}, 0.83 -> {Lv(0.83)} (order kept, <= 127), x1 unchanged below 0.85",
                Lv(0.2)>=48 && Lv(0.2)<=52 && Lv(0.4)-Lv(0.2)>=20 && Lv(0.6)-Lv(0.4)>=10 && Lv(0.83)-Lv(0.6)>=6 && Lv(0.83)<=127 && Lv(-0.6)==-Lv(0.6) && BtHapticsReport.Level(0.5,1.0)==64);
        }
        {
            // v38: the selector's end stop (all profiles)
            var stop=cfg.Rules.SingleOrDefault(r=>r.Id=="lp-nav-stop"); var contact=cfg.Rules.Single(r=>r.Id=="lp-nav-contact");
            (float[] l,float[] r) S(string k)=>HapticRuntime.RenderHd(stop!.Effect,n=>n=="k" ? (object)k : null,48000);
            double E(float[] x)=>x.Sum(v=>(double)v*v);
            var (wl,wr)=stop!=null ? S("W") : (new float[1],new float[1]); var (sl,sr)=stop!=null ? S("S") : (new float[1],new float[1]);
            var (cl,cr)=HapticRuntime.RenderHd(contact.Effect,_=>null,48000);
            var shd=new TestHd(); var srt=new HapticRuntime(cfg,shd,new TestMotor()){ Pad=new TestPad() }; int sseq=0;
            List<Decision> N(string ctx) => srt.Handle(EventMonitor.ParseRecord($"{++sseq}:lp:update:{ctx}")!,time.AddMilliseconds(sseq*500));
            var top=N("ph=navpress,k=W,cur=4,nav=1,requested=1,edge=1"); var bottom=N("ph=navpress,k=S,cur=0,nav=2,requested=1,edge=1");
            var mid=N("ph=navpress,k=W,cur=2,nav=3,requested=1,edge=0"); var old=N("ph=navpress,k=W,cur=2,nav=4,requested=1");
            bool Only(List<Decision> d,string id)=>d.Count(x=>x.RuleId is "lp-nav-stop" or "lp-nav-contact")==1 && d.Any(x=>x.RuleId==id);
            yield return ("End stop: R1 on the top plate / L1 on the bottom one = a heavy thud in that hand (R1 right, L1 left) instead of the input contact; inner presses / old journals = the contact",
                stop!=null && Only(top,"lp-nav-stop") && Only(bottom,"lp-nav-stop") && Only(mid,"lp-nav-contact") && Only(old,"lp-nav-contact")
                && E(wr)>E(wl)*2 && E(sl)>E(sr)*2 && E(wl)+E(wr)>(E(cl)+E(cr))*2 && stop.Pad==null);
        }
        {
            // v36 Trained: progress confirmation (pack 46) and links diagnostics (pack 47, experimental)
            var prog=cfg.Rules.SingleOrDefault(r=>r.Id=="lp-progress"); var links=cfg.Rules.SingleOrDefault(r=>r.Id=="lp-links");
            var (pl,_)=prog!=null ? HapticRuntime.RenderHd(prog.Effect,_=>null,48000) : (new float[0],new float[0]);
            int Onset(float[] x,int from){ for(int i=from;i<x.Length;i++) if(Math.Abs(x[i])>0.05f) return i; return -1; }
            int o1=Onset(pl,0);
            int PeakAt(int a,int b){ int at=-1; float m=0; for(int i=a*48;i<Math.Min(b*48,pl.Length);i++) if(Math.Abs(pl[i])>m){ m=Math.Abs(pl[i]); at=i; } return at; }
            int p1=PeakAt(55,130), p2=PeakAt(130,230);
            float a1=p1>=0 ? Math.Abs(pl[p1]) : 0, a2=p2>=0 ? Math.Abs(pl[p2]) : 0;
            var settleRule=cfg.Rules.Single(r=>r.Id=="lp-settle"); var (stl,str)=HapticRuntime.RenderHd(settleRule.Effect,_=>null,48000);
            var mp=Analysis.Measure(pl,pl); var ms=Analysis.Measure(stl,str);
            yield return ($"Trained progress: a strong rising double pulse as the end of the cascade, right behind the settle detent (starts {o1/48} ms, peaks {p1/48} / {p2/48} ms, {a1:F2} -> {a2:F2}), stronger than lp-settle (rms20 {mp.Rms20:F2} vs {ms.Rms20:F2}), no limiter, no triggers",
                prog!=null && prog.Pad==null && o1>=55*48 && o1<=75*48 && p1>0 && p2>0 && (p2-p1)>=55*48 && (p2-p1)<=95*48 && a2>=a1
                && mp.Rms20>ms.Rms20 && mp.Peak<=0.855 && mp.OverKnee==0);
            var thd=new TestHd(); var tpad=new TestPad(); var trt=new HapticRuntime(cfg,thd,new TestMotor()){ Pad=tpad };
            int tseq=0; List<Decision> T(string ctx,int ms) => trt.Handle(EventMonitor.ParseRecord($"{++tseq}:lp:update:{ctx}")!,time.AddMilliseconds(ms));
            bool none=new[]{ T("ph=settle,status=accepted,tx=1,ch=2,dir=L,ctr=1,n=5,cin=1,cout=0",0), T("ph=strain,dir=L,cur=0,tx=2,conf=derived",400),
                              T("ph=settle,status=reset,tx=0,ch=3,dir=0,ctr=0,n=5",800), T("ph=settle,status=break,tx=0,ch=1,dir=0,ctr=0,n=5,anim=1",1200),
                              T("ph=press,tx=3,dir=R,cur=0,valid=1,ch=1,same=1,opp=0,via=pad",1600) }.All(d=>!d.Any(x=>x.RuleId=="lp-progress"));
            var pd=T("ph=progress,tx=4,d0=7,d1=6,prof=1",2000); long pv=thd.Last;
            T("ph=trig,l=click_soft,r=warn,next=S,skill=1,lives=4",2005); bool keptAfterTrig=!thd.Stopped.Any(x=>x.id==pv);
            T("ph=press,tx=5,dir=L,cur=0,valid=1,ch=1,same=1,opp=0,via=pad",2100); bool cancelled=thd.Stopped.Any(x=>x.id==pv);
            yield return ("Trained progress: only on ph=progress (never on settle / strain / reset / break / press), survives the trigger update, a new press cancels it",
                none && pd.Any(d=>d.RuleId=="lp-progress") && keptAfterTrig && cancelled);
            float lpk=links!=null ? HapticRuntime.RenderHd(links.Effect,n=>n=="k" ? (object)4.0 : null,48000).l.Max(Math.Abs) : 0f;
            int Thuds(int k){ var (x,_)=HapticRuntime.RenderHd(links!.Effect,n=>n=="k" ? (object)(double)k : null,48000); int c=0,i=0; while((i=Onset(x,i))>=0){ c++; i+=48*100; } return c; }
            yield return ($"Trained links: 1, 2, 3, 4 countable pulses for 1, 2, 3, 4+ plates (6 -> 4), 130 ms apart, strong (peak {lpk:F2}) but below the progress reward, no trigger output",
                links!=null && lpk>0.6f && lpk<pl.Max(Math.Abs) && links.Pad==null && links.Path=="hd" && Thuds(1)==1 && Thuds(2)==2 && Thuds(3)==3 && Thuds(4)==4 && Thuds(6)==4);
            bool GoalOk(int k)
            {
                (float[] l,float[] r) R(double goal)=>HapticRuntime.RenderHd(links!.Effect,n=>n=="k" ? (object)(double)k : n=="goal" ? (object)goal : null,48000);
                var g=R(1).l; var z=R(0).l;
                int last=60+(Math.Min(k,4)-1)*130;                                        // the last count slot
                float Pk(float[] x,int a,int len)=>x.Skip(a*48).Take(len*48).Select(Math.Abs).DefaultIfEmpty().Max();
                float goalPk=Pk(g,last,110), countPk=Pk(z,last,110);
                bool sameBefore=g.Take(last*48).SequenceEqual(z.Take(last*48));
                bool differs=!g.Skip(last*48).Take(110*48).SequenceEqual(z.Skip(last*48).Take(110*48));
                int c=0,i=0; while((i=Onset(g,i))>=0){ c++; i+=48*100; }
                return c==Math.Min(k,4) && sameBefore && differs && goalPk>countPk*0.5f && goalPk<countPk && Pk(g,last+120,300)<0.02f;
            }
            yield return ("Trained links v37d: on the route's plate the LAST count pulse is the goal pulse (k = 1..4: still k pulses, the earlier ones unchanged, the last softer and different), goal=0 / missing = the plain count",
                links!=null && GoalOk(1) && GoalOk(2) && GoalOk(3) && GoalOk(4) && Thuds(3)==3);
            T("ph=links,cur=1,k=3,moved=3,prof=1",3000); long lv=thd.Last;
            T("ph=sel,cur=2,n=5",3060); bool linkCut=thd.Stopped.Any(x=>x.id==lv);
            var noLinks=HapticConfig.Load(path,new[]{ "lockpick-links" });
            yield return ("Trained links: a fast new selection cancels the rest; the pack switches off on its own (progress stays)",
                linkCut && !noLinks.Rules.Any(r=>r.Id=="lp-links") && noLinks.Rules.Any(r=>r.Id=="lp-progress"));
        }
        {
            // v34: the approved pick-break scene (FAILURE_HAPTICS_HANDOFF.md) on lp_break:start
            var game=cfg.Rules.SingleOrDefault(r=>r.Id=="lp-break-anim");
            var stand=HapticConfig.Load(path); stand.AddSupplementalPack(FailureGallery.SyncPack);
            var preview=stand.Rules.SingleOrDefault(r=>r.Pack=="failure-preview");
            bool same=false; int gameMs=0;
            if(game!=null && preview!=null)
            {
                var (gl,gr)=HapticRuntime.RenderHd(game.Effect,_=>null,48000);
                var (sl,sr)=HapticRuntime.RenderHd(preview.Effect,_=>null,48000);
                gameMs=gl.Length/48;
                same=gl.Length<sl.Length && gl.SequenceEqual(sl.Take(gl.Length)) && gr.SequenceEqual(sr.Take(gr.Length))
                    && sl.Skip(gl.Length).All(x=>x==0) && sr.Skip(gr.Length).All(x=>x==0) && game.Effect.Layers.Count==preview.Effect.Layers.Count-1;
            }
            yield return ($"Pick break: the game scene = the approved stand PCM sample for sample, only the silent padding dropped ({gameMs} ms)",
                same && gameMs>=1500 && gameMs<1600 && game!.Event=="lp_break" && game.Priority==3 && game.Continuous && !game.Loop && game.MaxMs>=gameMs);
            var bhd=new TestHd(); var brt=new HapticRuntime(cfg,bhd,new TestMotor()){ Pad=new TestPad() };
            int bseq=0; List<Decision> B(string rec,int ms) => brt.Handle(EventMonitor.ParseRecord($"{++bseq}:{rec}")!,time.AddMilliseconds(ms));
            var start=B("lp_break:start:state=4,prev=1,tx=5,lives=0,n=1",0); long scene=bhd.Last;
            B("lp:update:ph=lives,n=2,max=2,leds=1,bar=white,src=derived",1500);
            B("lp:update:ph=trig,l=warn,r=click_soft,next=S,skill=0,lives=2",1505);
            B("lp:update:ph=guide,gen=9,kind=sel,side=R,next=W,cur=0,target=1,prof=2",1510);
            bool bKept=start.Any(d=>d.RuleId=="lp-break-anim" && d.Route=="hd") && !bhd.Stopped.Any(x=>x.id==scene);
            var late=B("lp:update:ph=settle,status=break,tx=0,ch=1,dir=0,ctr=0,n=5,anim=1",1909);
            var fallback=B("lp:update:ph=settle,status=break,tx=0,ch=1,dir=0,ctr=0,n=5,anim=0",5000);
            var bOld=B("lp:update:ph=settle,status=break,tx=0,ch=1,dir=0,ctr=0,n=5",9000);
            yield return ("Pick break: lp:update cues (lives, trig, guide) never cut the scene; the old late lp-break is skipped after it (anim=1), still a fallback (anim=0 / old journals)",
                bKept && !late.Any(d=>d.RuleId=="lp-break") && fallback.Any(d=>d.RuleId=="lp-break") && bOld.Any(d=>d.RuleId=="lp-break"));
            B("lp_break:start:state=4,prev=1,tx=6,lives=0,n=2",10000); long scene2=bhd.Last;
            B("lp:complete:res=close,moves=3,strains=2",10300);
            yield return ("Pick break: closing the minigame stops the scene", bhd.Stopped.Any(x=>x.id==scene2));
            // v35: the second part - the broken tip (BrokenPick_Out) from the game's Remove/Break sounds + pick_1 motion
            var outRule=cfg.Rules.SingleOrDefault(r=>r.Id=="lp-break-out");
            var (ol,orr)=outRule!=null ? HapticRuntime.RenderHd(outRule.Effect,_=>null,48000) : (new float[0],new float[0]);
            float opk=ol.Length>0 ? ol.Max(Math.Abs) : 0f;
            double OPeak(int a,int b)=>ol.Skip(a*48).Take((b-a)*48).Select(x=>(double)Math.Abs(x)).DefaultIfEmpty().Max();
            yield return ($"Pick break, second part: tipout.game.wav 640 ms, release ~177 ms is its strongest moment, exit edge ~377 ms, peak {opk:F2} (< main 0.83)",
                outRule!=null && outRule.Event=="lp_break" && outRule.Priority==3 && ol.Length==30720 && ol.SequenceEqual(orr)
                && opk>0.5f && opk<0.7f && OPeak(160,200)>=opk*0.99 && OPeak(370,400)>0.1 && OPeak(600,640)<0.05);
            B("lp_break:start:state=4,prev=1,tx=7,lives=0,n=3",20000);
            var outD=B("lp_break:update:ph=out,src=bone,since=2360,d100=150,n=3",22360); long tip=bhd.Last;
            B("lp:update:ph=settle,status=break,tx=0,ch=1,dir=0,ctr=0,n=5,anim=1",22400);
            B("lp:update:ph=guide,gen=10,kind=sel,side=R,next=W,cur=0,target=1,prof=2",22450);
            bool tipKept=outD.Any(d=>d.RuleId=="lp-break-out" && d.Route=="hd") && !bhd.Stopped.Any(x=>x.id==tip);
            B("lp:complete:res=close,moves=3,strains=2",22600);
            yield return ("Pick break, second part: plays on ph=out, lp:update cues do not cut it, closing the minigame does",
                tipKept && bhd.Stopped.Any(x=>x.id==tip));
        }
        {
            // v32 Novice: safe = click_soft, dangerous = warn (medium) with 2+ lives, wall on the last life, warn again on a new pick
            var np=new TestPad(); var nrt=new HapticRuntime(cfg,new TestHd(),new TestMotor()){ Pad=np };
            int nseq=0; void N(string ctx) => nrt.Handle(EventMonitor.ParseRecord($"{++nseq}:lp:update:{ctx}")!,time);
            N("ph=trig,l=click_soft,r=click_soft,next=D,skill=0,lives=2"); bool s1=np.Left=="click_soft" && np.Right=="click_soft";
            N("ph=trig,l=warn,r=click_soft,next=D,skill=0,lives=2"); bool s2=np.Left=="warn" && np.Right=="click_soft";
            N("ph=trig,l=wall,r=click_soft,next=D,skill=0,lives=1"); bool s3=np.Left=="wall" && np.Right=="click_soft";
            N("ph=trig,l=warn,r=click_soft,next=D,skill=0,lives=2"); bool s4=np.Left=="warn";
            var soft=cfg.TriggerProfiles["click_soft"]; var warn=cfg.TriggerProfiles["warn"]; var wall=cfg.TriggerProfiles["wall"];
            // v32b: only zones 0..5 are felt (fire point 0.58, then the tail) - the three must differ from the start of the travel
            yield return ("Novice: safe = click (free to zone 4), danger = medium feedback from 0, last life = maximum from 0, new pick = medium again",
                s1 && s2 && s3 && s4 && soft.Mode=="weapon" && soft.Pos>=4 && warn.Mode=="feedback" && warn.Pos==0 && warn.Bytes()[0]==0x21
                && wall.Mode=="feedback" && wall.Pos==0 && wall.Str==8 && warn.Str<=wall.Str-3);
        }
        {
            // v30: pick life on the lightbar + player LEDs
            var light=new OfflineLight(); var lrt=new HapticRuntime(cfg,new TestHd(),new TestMotor()){ Pad=new TestPad(), LightOut=light };
            int lseq=0; List<Decision> L(string ctx,string phase="update") => lrt.Handle(EventMonitor.ParseRecord($"{++lseq}:lp:{phase}:{ctx}")!,time);
            L("ph=lives,n=6,max=6,leds=5,bar=white,src=derived"); bool full=light.Leds==5 && light.Rgb!=null && light.Rgb.All(x=>x==255);
            L("ph=lives,n=2,max=6,leds=1,bar=white,src=derived"); bool two=light.Leds==1 && light.Rgb!.All(x=>x==255);
            L("ph=lives,n=1,max=6,leds=0,bar=red,src=derived"); bool last=light.Leds==0 && light.Rgb![0]==255 && light.Rgb[1]==0 && light.Rgb[2]==0;
            L("ph=lives,n=0,max=6,leds=0,bar=off,src=derived"); bool broken=light.Leds==0 && light.Rgb!.All(x=>x==0);
            L("res=close","complete"); bool reset=light.Resets>0 && light.Rgb==null && light.Leds==null;
            yield return ("Pick life: 6 = five LEDs + white lightbar, 2 = one LED + white, 1 = red lightbar only, 0 = dark; closing resets the light",
                full && two && last && broken && reset);
            yield return ("Player LEDs: symmetric pattern 1..5 (centre, pairs, all)",
                DualSense.PlayerLedPattern(0)==0 && DualSense.PlayerLedPattern(1)==0x04 && DualSense.PlayerLedPattern(5)==0x1F && DualSense.PlayerLedPattern(9)==0x1F);
        }
        yield return ("Step end: one knock on both triggers (vibration pulse 0/3/40 Hz, ~65 ms), the trigger state is not changed by it",
            pad.Pulsed=="knock" && pad.PulseMs>=60 && pad.PulseMs<=70);
        yield return ("Selected triad: lifecycle has no rule errors", rt.Errors==0);
    }
    sealed class TestHd : IHdOut
    {
        readonly HapticMixer mixer=new();
        public bool Ready=>true; public int Rate=>48000; public bool Active=>mixer.Active;
        public long Last; public string LastTag=""; public List<(long id,int fade)> Stopped=new();
        public long Play(float[] l,float[] r,VoiceOpts o) { LastTag=o.Tag; return Last=mixer.Add(l,r,o); }
        public void Stop(long id,int fadeMs) { Stopped.Add((id,fadeMs)); mixer.Stop(id,fadeMs*48); }
        public void StopAll()=>mixer.Clear();
        public (float[] l,float[] r) Render(int frames) { var l=new float[frames]; var r=new float[frames]; mixer.Render(l,r,frames); return(l,r); }
    }
    sealed class TestMotor : IMotorOut
    {
        public void Pattern((int delay,byte right,byte left,int ms)[] steps) { }
        public void StopAll() { }
    }
    // "Сухой спуск" automaton of the live trigger output: the tail always plays to the end; a held trigger stays off after it
    // (no resistance re-armed under the finger) until the release; a released one gets its profile back at once.
    static IEnumerable<(string name,bool ok)> PadAutomaton()
    {
        var weapon=new TriggerProfile("path","weapon",4,8,0,0,5); var tail=new TriggerProfile("tail","vibration",0,0,3,40);
        var off=DualSense.EffectOff(); string Hex(byte[]? x)=>x==null?"-":Convert.ToHexString(x);
        var writes=new List<(string l,string r)>(); var live=new PadOutLive(()=>null){ Sink=(l,r)=>writes.Add((Hex(l),Hex(r))) };
        live.Triggers(weapon,weapon); var t0=DateTime.UtcNow;
        live.Pulse(tail,150,false,true,5,hold:true);                   // R2 pulled past 0.58
        live.Refresh(t0.AddMilliseconds(10)); var started=writes.LastOrDefault();
        live.Triggers(weapon,weapon); var during=writes.LastOrDefault(); // a state change during the tail touches only L2
        int n=writes.Count;
        live.Release(false,true);                                        // let go mid-tail: the tail must not be cut
        live.Refresh(t0.AddMilliseconds(100)); bool notCut=writes.Count==n;
        live.Refresh(t0.AddMilliseconds(170)); var after=writes.LastOrDefault();
        yield return ("Trigger automaton: tail only on R2, plays to the end after an early release, then the click profile is back",
            started.l=="-" && started.r==Hex(tail.Bytes()) && during.r=="-" && notCut && after.r==Hex(weapon.Bytes()));
        writes.Clear(); var t1=DateTime.UtcNow;
        live.Pulse(tail,150,true,false,5,hold:true);                     // L2 pulled and held
        live.Refresh(t1.AddMilliseconds(10)); live.Refresh(t1.AddMilliseconds(170)); var parked=writes.LastOrDefault();
        live.Triggers(weapon,weapon); var stillParked=writes.LastOrDefault();
        live.Release(true,false); var rearmed=writes.LastOrDefault();
        yield return ("Trigger automaton: held after the tail -> off (no resistance under the finger), profile only after trigup",
            parked.l==Hex(off) && stillParked.l=="-" && rearmed.l==Hex(weapon.Bytes()));
        live.Pulse(tail,150,true,true,5,hold:true); live.Reset("test"); var reset=writes.LastOrDefault();
        yield return ("Trigger automaton: reset (exit / menu / lost pad) clears tails and parking at once", reset.l==Hex(off) && reset.r==Hex(off));
    }

    sealed class TestPad : IPadOut
    {
        public string? Left,Right; public int Resets;
        public void Triggers(TriggerProfile? l,TriggerProfile? r) { if(l!=null) Left=l.Name; if(r!=null) Right=r.Name; }
        public void Reset(string reason) { Left=Right=null; Resets++; }
        public string? Pulsed; public int PulseMs, PulseDelay; public bool PulseL, PulseR;
        public bool PulseHold; public string Released="";
        public void Pulse(TriggerProfile pulse,int ms,bool left=true,bool right=true,int delayMs=0,bool hold=false) { Pulsed=pulse.Name; PulseMs=ms; PulseL=left; PulseR=right; PulseDelay=delayMs; PulseHold=hold; }
        public void Release(bool left,bool right) { Released=(left?"L":"")+(right?"R":""); }
    }
}
