using System.Text.Json;
namespace DsBridge.Haptics;

// Generic offline signal validation, also used by semantic-scene evaluation. Never opens a controller.
public static class SignalCheck
{
    public static (bool ok, string report) Run(IEnumerable<string> files, double minBtCorr = .5)
    {
        var rows = new List<object>(); bool all = true;
        foreach (var file in files)
        {
            var (l, r, _) = AudioHaptics.Load(file);
            var m = Analysis.Measure(l, r);
            var bt = Analysis.BtSim(l, r);
            bool ok = m.Peak <= .8551 && m.Clipped == 0 && m.OverKnee == 0 && Math.Abs(m.DcL) < .015 && Math.Abs(m.DcR) < .015 && bt.corr >= minBtCorr;
            all &= ok;
            rows.Add(new { file = Path.GetFileName(file), ok, peak = m.Peak, rms20 = m.Rms20, clipped = m.Clipped, overKnee = m.OverKnee, btCorr = bt.corr, btRms20 = bt.rms20 });
        }
        all &= rows.Count > 0;
        return (all, JsonSerializer.Serialize(new { ok = all, count = rows.Count, minBtCorr, results = rows }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
