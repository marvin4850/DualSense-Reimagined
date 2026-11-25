using DsBridge.Haptics;
namespace DsBridge;

static class ThreatLightChecks
{
    public static IEnumerable<(string name, bool ok)> Run(HapticConfig cfg)
    {
        var sink = new OfflineLight();
        var pad = new OfflinePad();
        var rt = new HapticRuntime(cfg, null, new OfflineMotor()) { LightOut = sink, Pad = pad };
        var now = DateTime.UnixEpoch; int seq = 0;
        void Send(string record, int ms = 0) => rt.Handle(EventMonitor.ParseRecord($"{++seq}:{record}")!, now.AddMilliseconds(ms));
        bool Color(byte r, byte g, byte b) => sink.Rgb != null && sink.Rgb.SequenceEqual(new byte[]{r,g,b});
        foreach (int sneak in new[]{0,1})
        {
            Send($"threat:update:level=noticed,sneak={sneak},seen=1,fighters=0");
            bool yellow = Color(255,255,0);
            Send($"threat:update:level=combat,sneak={sneak},seen=1,fighters=1");
            bool red = Color(255,0,0);
            Send($"threat:update:level=noticed,sneak={sneak},seen=1,fighters=0");
            bool deescalate = Color(255,255,0);
            Send($"threat:update:level=idle,sneak={sneak},seen=0,fighters=0");
            yield return ($"threat: sneak={sneak}: yellow -> red -> yellow -> idle", yellow && red && deescalate && !rt.LightActive && sink.Rgb == null);
        }
        Send("lp:update:ph=lives,n=6,max=6,leds=5,bar=white");
        Send("threat:update:level=combat,sneak=0");
        bool danger = Color(255,0,0) && sink.Leds == 5;
        Send("lp:update:ph=lives,n=2,max=6,leds=1,bar=white");
        bool retained = Color(255,0,0) && sink.Leds == 1;
        Send("threat:update:level=idle,sneak=0");
        yield return ("threat: danger overrides lockpick colour, preserves LEDs, restores current lockpick state", danger && retained && Color(255,255,255) && sink.Leds == 1);
        Send("threat:update:level=noticed,sneak=1");
        Send("lp:complete:res=close");
        yield return ("threat: closing a lock does not clear danger or leave player LEDs lit", Color(255,255,0) && sink.Leds == null);
        rt.Tick(now.AddMilliseconds(1501));
        yield return ("threat: missing telemetry expires colour after 1500 ms", !rt.LightActive && sink.Rgb == null);
        Send("threat:update:level=combat",2000);
        Send("threat:update:level=combat",2500);
        rt.Tick(now.AddMilliseconds(3800));
        yield return ("threat: heartbeat renews lease", Color(255,0,0));
        Send("mode:update:m=menu",3900);
        bool menu = !rt.LightActive && sink.Rgb == null;
        Send("mode:update:m=world",4000);
        Send("threat:update:level=combat",4100);
        bool resume = Color(255,0,0);
        Send("session:start:src=check",4200);
        yield return ("threat: menu and load clear colour; world heartbeat restores current danger", menu && resume && !rt.LightActive);
        Send("threat:update:level=noticed",4300);
        rt.StopAll("game lost");
        yield return ("threat: losing the game resets colour without setting adaptive triggers", !rt.LightActive && pad.L2 == null && pad.R2 == null && rt.Errors == 0);
    }
}
