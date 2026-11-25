using System.Security.Cryptography;
using System.Runtime.InteropServices;
namespace DsBridge.Haptics;

/// <summary>Behaviour and acoustic regressions for the shared user-feedback rules.</summary>
public static class FeedbackChecks
{
    sealed record Wave(float[] L,float[] R) {
        public double Energy(int from=0,int to=1000) {int a=Math.Min(L.Length,from*48),b=Math.Min(L.Length,to*48);double e=0;for(int i=a;i<b;i++)e+=L[i]*L[i]+R[i]*R[i];return e/48000;}
        public string Hash {get {var data=new byte[(L.Length+R.Length)*4];Buffer.BlockCopy(L,0,data,0,L.Length*4);Buffer.BlockCopy(R,0,data,L.Length*4,R.Length*4);return Convert.ToHexString(SHA256.HashData(data));}}
    }
    static Wave Render(HapticConfig cfg,string raw,int ms=1500) {
        var hd=new OfflineHd();Replay.ApplyLimits(hd.Mixer,cfg.Limits);var motor=new OfflineMotor();var runtime=new HapticRuntime(cfg,hd,motor);
        var now=new DateTime(2026,1,1,0,0,0,DateTimeKind.Utc);runtime.Handle(EventMonitor.ParseRecord(raw)!,now);
        var l=new float[ms*48];var r=new float[ms*48];var a=new float[480];var b=new float[480];
        for(int t=0;t<ms;t+=10){runtime.Tick(now.AddMilliseconds(t));hd.Mixer.Render(a,b,480);Array.Copy(a,0,l,t*48,480);Array.Copy(b,0,r,t*48,480);}
        if(runtime.Errors>0)throw new Exception(string.Join("; ",runtime.ErrorList));return new(l,r);
    }
    static string Contact(string w)=>$"1:attack:update:src=check,res=contact,dmg=15,imp=standard,wpn={w},def=none,tmat=flesh,tsize=mid,amat=flesh,asize=mid,wmat=metal,blk=0";
    static string Wall(string w)=>$"1:attack:update:src=check,res=deflected,wpn={w},def=none,tmat=none,tsize=mid,amat=flesh,asize=mid,wmat=fists,blk=0";
    public static IEnumerable<(string name,bool ok)> Run(HapticConfig cfg)
    {
        var ev=EventMonitor.ParseRecord("1:step:update:src=check,m=Meal,gait=crouch,mat=Stone")!;
        var cycle=new VariationSpec("cycle",8,"steps","event");var state=new VariationState();
        var indices=Enumerable.Range(0,24).Select(_=>state.Next(cycle,ev)).ToArray();
        yield return ("feedback: cycle groups repeat exactly 1..8",indices.SequenceEqual(Enumerable.Range(0,24).Select(i=>i%8)));
        var random=new VariationSpec("random",8,"items","event");var aState=new VariationState(441);var bState=new VariationState(441);
        var a=Enumerable.Range(0,128).Select(_=>aState.Next(random,ev)).ToArray();var b=Enumerable.Range(0,128).Select(_=>bState.Next(random,ev)).ToArray();
        yield return ("feedback: random selection covers 8, avoids immediate repeats, replay seed reproducible",a.SequenceEqual(b)&&a.Distinct().Count()==8&&a.Zip(a.Skip(1)).All(x=>x.First!=x.Second));
        var episode=new VariationSpec("random",8,"drink","montage");var es=new VariationState(44);int first=es.Next(episode,ev);
        bool held=Enumerable.Range(0,10).All(_=>es.Next(episode,ev)==first);es.BeginMontage("Meal");
        yield return ("feedback: food/drink keep one variation through the whole animation",held&&es.Next(episode,ev)!=first);
        var rule=new Rule{Id="test",Event="step",Phases=["update"],CooldownMs=100,Variation=cycle};
        var rcfg=new HapticConfig{Rules=[rule]};var rt=new HapticRuntime(rcfg,null,new OfflineMotor());var zero=new DateTime(2026,1,1);
        rt.Handle(ev,zero);rt.Handle(ev,zero.AddMilliseconds(20));int index=rt.LastVariations[rule.Id];rt.Handle(ev,zero.AddMilliseconds(150));
        yield return ("feedback: rejected cooldown events do not consume the next variation",index==0&&rt.LastVariations[rule.Id]==1);

        var m1=Render(cfg,Contact("m1h"));var m2=Render(cfg,Contact("m2h"));var staff=Render(cfg,Contact("staff"));
        double ratio=m2.Energy()/m1.Energy(),tail=m2.Energy(160,700)/Math.Max(1e-9,m1.Energy(160,700));
        yield return ($"feedback: 2H mace is heavier and has a longer tail than 1H (energy x{ratio:0.00}, tail x{tail:0.00})",ratio>1.2&&tail>1.6);
        double staffRatio=staff.Energy(120,700)/m2.Energy(120,700);
        yield return ($"feedback: staff follows the 2H blunt body (tail ratio {staffRatio:0.00})",staffRatio>.5&&staffRatio<1.6&&staff.Energy(120,700)>m1.Energy(120,700)*1.4);
        var families=new[]{"s1h","s2h","a1h","a2h","m1h","m2h","staff","fist"};
        var wall=families.Select(f=>Render(cfg,Wall(f))).ToArray();
        yield return ("feedback: every wall/weapon family renders a distinct recoil",wall.Select(w=>w.Hash).Distinct().Count()==families.Length);
        var draw1=Render(cfg,"1:weaponanim:update:src=check,act=draw,fam=s1h,m=A_Man_S1H_Un_Draw_Montage,pos=150");
        var draw2=Render(cfg,"1:weaponanim:update:src=check,act=draw,fam=s2h,m=A_Man_S2H_Un_DrawBack_Montage,pos=150");
        var axe1=Render(cfg,"1:weaponanim:update:src=check,act=draw,fam=a1h,m=A_Man_A1H_Un_Draw_Montage,pos=150");
        var axe2=Render(cfg,"1:weaponanim:update:src=check,act=draw,fam=a2h,m=A_Man_A2H_Un_DrawBack_Montage,pos=150");
        yield return ($"feedback: 2H sword draw has more body than 1H (x{draw2.Energy()/draw1.Energy():0.00})",draw2.Energy()>draw1.Energy()*1.1);
        yield return ($"feedback: 2H axe draw has more body than 1H (x{axe2.Energy()/axe1.Energy():0.00})",axe2.Energy()>axe1.Energy()*1.1);
        var roll=Render(cfg,"1:roll:start:src=check");
        yield return ("feedback: roll has a continuous two-sided rolling tail, not a stronger left-only dodge",roll.Energy(250,650)>roll.Energy(0,200)*.5&&roll.R.Sum(x=>(double)x*x)>.1*roll.L.Sum(x=>(double)x*x));

        var crouchRules=cfg.Rules.Where(r=>r.Variation?.Group=="crouch_steps").ToArray();
        yield return ("feedback: all ten crouch surfaces have eight cycle variations",crouchRules.Length==10&&crouchRules.All(r=>r.Variation is {Mode:"cycle",Count:8}));
        foreach(var stepRule in crouchRules){
            var material=stepRule.Where.TryGetValue("mat",out var materials)?materials[0].TrimStart('~'):"Soil";
            var shd=new OfflineHd();Replay.ApplyLimits(shd.Mixer,cfg.Limits);var srt=new HapticRuntime(cfg,shd,new OfflineMotor());var hashes=new List<string>();var seen=new List<int>();bool audible=true;
            for(int k=0;k<9;k++){
                shd.StopAll();srt.Handle(EventMonitor.ParseRecord($"{k+1}:step:update:src=check,mat=PM_{material},gait=crouch")!,zero.AddSeconds(k));
                seen.Add(srt.LastVariations.GetValueOrDefault(stepRule.Id,-1));var l=new float[14400];var r=new float[14400];shd.Mixer.Render(l,r,l.Length);var wave=new Wave(l,r);hashes.Add(wave.Hash);audible&=wave.Energy()>1e-8;
            }
            yield return ($"feedback: crouch {material}: eight distinct audible textures; ninth repeats first",audible&&srt.Errors==0&&hashes.Take(8).Distinct().Count()==8&&hashes[0]==hashes[8]&&seen.SequenceEqual(Enumerable.Range(0,9).Select(i=>i%8)));
        }
        var quietLadder=Render(cfg,"1:ladder:start:src=check");
        var idleLadder=Render(cfg,"1:craftanim:update:src=check,m=Man_Ladder_Locomotion,sec=IdleLeft,pos=15134");
        yield return ("feedback: approaching a ladder and standing idle are silent",quietLadder.Energy()==0&&idleLadder.Energy()==0);
        var contacts=new[]{("UpLeftRight",34433,54),("UpRightLeft",34900,52),("DownLeftRight",35367,40),("DownRightLeft",35833,68),("UpLeftRightFast",38278,36),("UpRightLeftFast",38589,35)};
        bool timed=contacts.All(c=>{var wave=Render(cfg,$"1:craftanim:update:src=check,m=Man_Ladder_Locomotion,sec={c.Item1},pos={c.Item2}");return wave.Energy(0,c.Item3-2)==0&&wave.Energy(c.Item3,c.Item3+160)>1e-6;});
        yield return ("feedback: six ladder movements play on their own native contact clocks",timed);
        var weed=Render(cfg,"1:weed_high:start:src=check",18000);
        double Difference(int from,int to){double l=0,r=0;for(int i=from*48;i<to*48;i++){l+=weed.L[i]*weed.L[i];r+=weed.R[i]*weed.R[i];}return l-r;}
        yield return ("feedback: swampweed sway travels to both sides without impulses",Difference(1500,2500)*Difference(5000,6000)<0&&weed.Energy(0,500)<weed.Energy(2000,2500));
    }
}
