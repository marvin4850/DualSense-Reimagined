using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NAudio.Wave;

namespace DsBridge.Haptics;

/// <summary>Event journal (JSON lines): {"t": ms since start, "raw": "seq:type:phase:k=v,..."}.</summary>
public sealed class JournalWriter : IDisposable
{
    readonly StreamWriter _w; readonly DateTime _t0 = DateTime.UtcNow;
    public int Count;
    public JournalWriter(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _w = new StreamWriter(path, append: false, new UTF8Encoding(false)) { AutoFlush = true };   // no BOM: plain JSON lines
    }
    public void Write(GameEvent ev)
    {
        var raw = ev.Legacy ? $"{ev.Seq}:{ev.Type}:{ev.Phase}:src=v1" : ev.Raw;
        _w.WriteLine(JsonSerializer.Serialize(new { t = (long)(DateTime.UtcNow - _t0).TotalMilliseconds, raw }));
        Count++;
    }
    public void Dispose() => _w.Dispose();
}

public static class Journal
{
    public static List<(long t, GameEvent ev)> Load(string path, List<string>? problems = null)
    {
        var res = new List<(long, GameEvent)>(); int line = 0;
        // the companion may still be writing this journal: open shared
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var rd = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        for (string? l = rd.ReadLine(); l != null; l = rd.ReadLine())
        {
            line++;
            if (string.IsNullOrWhiteSpace(l)) continue;
            try
            {
                using var d = JsonDocument.Parse(l);
                long t = d.RootElement.GetProperty("t").GetInt64();
                var raw = d.RootElement.GetProperty("raw").GetString()!;
                var ev = EventMonitor.ParseRecord(raw);
                if (ev == null) { problems?.Add($"line {line}: unparseable record '{raw}'"); continue; }
                res.Add((t, ev));
            }
            catch (Exception ex) { problems?.Add($"line {line}: {ex.Message}"); }
        }
        return res;
    }

    static readonly Regex LogEvent = new(@"^\[(?<ts>\d\d:\d\d:\d\d\.\d{3})\] EVENT #(?<seq>\d+) (?<type>[a-z0-9_]+):(?<phase>[a-z]+) src=(?<src>\S+)(?<rest>.*)$");

    /// <summary>Converts `run --diag` / `monitor` logs (lines "EVENT #12 attack:start src=... k=v -> effect ...") into a journal.</summary>
    public static int FromDiagLog(string logPath, string outPath)
    {
        var lines = new List<string>(); TimeSpan? t0 = null; TimeSpan prev = TimeSpan.Zero; long dayOffset = 0;
        foreach (var l in File.ReadLines(logPath))
        {
            var m = LogEvent.Match(l);
            if (!m.Success) continue;
            var ts = TimeSpan.ParseExact(m.Groups["ts"].Value, @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
            if (t0 == null) t0 = ts;
            if (ts < prev) dayOffset += 86_400_000;
            prev = ts;
            long t = (long)(ts - t0.Value).TotalMilliseconds + dayOffset;
            var rest = m.Groups["rest"].Value;
            int arrow = rest.IndexOf(" -> effect", StringComparison.Ordinal); if (arrow >= 0) rest = rest[..arrow];
            rest = rest.Replace(" [v1]", "").Trim();
            var kv = rest.Length == 0 ? "" : "," + string.Join(",", rest.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            var raw = $"{m.Groups["seq"].Value}:{m.Groups["type"].Value}:{m.Groups["phase"].Value}:src={m.Groups["src"].Value}{kv}";
            lines.Add(JsonSerializer.Serialize(new { t, raw }));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllLines(outPath, lines);
        return lines.Count;
    }
}

/// <summary>Offline HD output: the same HapticMixer as the live stream, advanced by Replay in 10 ms blocks.</summary>
public sealed class OfflineHd : IHdOut
{
    public readonly HapticMixer Mixer = new();
    public bool Ready => true; public int Rate => 48000; public bool Active => Mixer.Active;
    public Action<float[], float[], VoiceOpts>? OnPlay;   // level map: every rendered voice as the rule asked for it
    public long Play(float[] l, float[] r, VoiceOpts o) { OnPlay?.Invoke(l, r, o); return Mixer.Add(l, r, o); }
    public void Stop(long id, int fadeMs) => Mixer.Stop(id, Rate * fadeMs / 1000);
    public void StopAll() => Mixer.Clear();
}

/// <summary>Offline motor output: keeps the patterns (for the report and the optional preview channels).</summary>
public sealed class OfflineMotor : IMotorOut
{
    public long NowMs; public readonly List<(long t, (int delay, byte right, byte left, int ms)[] steps)> Patterns = new();
    public int StopAlls;
    public void Pattern((int delay, byte right, byte left, int ms)[] steps) => Patterns.Add((NowMs, steps));
    public void StopAll() => StopAlls++;
}

public static class Replay
{
    public static void ApplyLimits(HapticMixer m, Limits l)
    {
        m.MaxVoices = l.MaxVoices; m.DuckLower = l.DuckLower; m.Ceiling = l.Ceiling; m.Knee = l.Knee; m.MasterGain = l.MasterGain; m.GuardFloor = l.GuardFloor;
    }

    public sealed class Options { public string? Wav; public string? ReportJson; public double Speed = 1; public bool Quiet; public Action<float[], float[], VoiceOpts>? OnVoice; }
    /// <summary>Key metrics of the last run (for regression baselines).</summary>
    public static SortedDictionary<string, object> LastMetrics = new();

    static string MotorHash(OfflineMotor m)
    {
        ulong h = 14695981039346656037UL;
        foreach (var (t, steps) in m.Patterns) foreach (var s in steps)
            foreach (var v in new long[] { t, s.delay, s.right, s.left, s.ms }) h = (h ^ (ulong)v) * 1099511628211UL;
        return h.ToString("x16");
    }

    /// <summary>Plays a journal through the rules into an offline mixer; returns the text report.</summary>
    public static string Run(HapticConfig cfg, List<(long t, GameEvent ev)> events, Options o, List<string> journalProblems)
    {
        var hd = new OfflineHd { OnPlay = o.OnVoice }; var motor = new OfflineMotor();
        ApplyLimits(hd.Mixer, cfg.Limits);
        var rt = new HapticRuntime(cfg, hd, motor);
        var log = new StringBuilder();
        if (!o.Quiet) rt.Log = m => log.AppendLine("           runtime: " + m);
        var tracker = new StateTracker();
        cfg.ConfigureTracker(tracker);
        var rate1s = new Dictionary<string, Queue<long>>(); var maxRate = new Dictionary<string, int>();
        var sameFrame = new Dictionary<string, int>(); var lastTypeT = new Dictionary<string, (long t, long seq)>();
        var trackerNotes = new List<string>(); tracker.Diag = trackerNotes.Add;
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        const int rate = 48000, block = 480;
        long endMs = events.Count == 0 ? 0 : events[^1].t + 3000;
        int total = (int)(rate * endMs / 1000);
        var L = new float[o.Wav != null ? total : 0]; var R = new float[o.Wav != null ? total : 0];
        var bl = new float[block]; var br = new float[block];
        double eL = 0, eR = 0; ulong pcmHash = 14695981039346656037UL; long zeros = 0;
        var perType = new SortedDictionary<string, int>();
        long lastSeq = 0, gaps = 0, dups = 0, sessions = 0, doubled = 0; var seenRaw = new HashSet<string>(); string? lastBody = null;
        int ei = 0;
        for (long pos = 0; pos < total; pos += block)
        {
            long ms = pos * 1000 / rate;
            while (ei < events.Count && events[ei].t <= ms)
            {
                var (t, ev) = events[ei++];
                var now = t0.AddMilliseconds(t);
                bool companionSide = ev.Src.StartsWith("hid_");   // events made by the companion (pad input): no game seq
                var body = ev.Raw[(ev.Raw.IndexOf(':') + 1)..];
                if (!companionSide && body == lastBody && ev.Type != "session" && ev.Type != "hooks") { doubled++; lastSeq = ev.Seq; continue; }   // same event delivered twice
                if (!companionSide) lastBody = body;
                motor.NowMs = t;
                perType[ev.Type + ":" + ev.Phase] = perType.TryGetValue(ev.Type + ":" + ev.Phase, out var c) ? c + 1 : 1;
                var tp = ev.Type + ":" + ev.Phase;
                if (lastTypeT.TryGetValue(tp, out var lt) && t - lt.t <= 1 && ev.Seq != lt.seq) sameFrame[tp] = sameFrame.TryGetValue(tp, out var sf) ? sf + 1 : 1;
                lastTypeT[tp] = (t, ev.Seq);
                if (!rate1s.TryGetValue(ev.Type, out var q)) rate1s[ev.Type] = q = new Queue<long>();
                q.Enqueue(t); while (q.Count > 0 && t - q.Peek() > 1000) q.Dequeue();
                if (q.Count > (maxRate.TryGetValue(ev.Type, out var mrv) ? mrv : 0)) maxRate[ev.Type] = q.Count;
                if (ev.Type == "session") { sessions++; lastSeq = 0; seenRaw.Clear(); }
                if (!companionSide)
                {
                    if (!seenRaw.Add(ev.Raw)) dups++;
                    if (lastSeq > 0 && ev.Seq > lastSeq + 1) gaps++;
                    lastSeq = ev.Seq;
                }
                if (ev.Type == "session" && ev.Phase == "start") { tracker.InterruptAll("new game session"); }
                tracker.Apply(ev, now);
                var dec = rt.Handle(ev, now);
                if (!o.Quiet) log.AppendLine($"{t,8} ms  #{ev.Seq} {ev.Type}:{ev.Phase}  -> {(dec.Count == 0 ? "(no rule)" : string.Join("; ", dec))}");
            }
            tracker.ExpireStale(t0.AddMilliseconds(ms));
            rt.Tick(t0.AddMilliseconds(ms));
            int n = (int)Math.Min(block, total - pos);
            hd.Mixer.NowMs = ms;
            hd.Mixer.Render(bl, br, n);
            if (o.Wav != null) { Array.Copy(bl, 0, L, pos, n); Array.Copy(br, 0, R, pos, n); }
            for (int i = 0; i < n; i++)   // digital PCM reference: energy per actuator + FNV-1a of the 13-bit output
            {
                eL += (double)bl[i] * bl[i]; eR += (double)br[i] * br[i];
                if (bl[i] == 0 && br[i] == 0) { zeros++; continue; }
                if (zeros > 0) { pcmHash = (pcmHash ^ (ulong)zeros) * 1099511628211UL; zeros = 0; }
                pcmHash = (pcmHash ^ (ulong)(uint)(int)Math.Round(bl[i] * 8192)) * 1099511628211UL;
                pcmHash = (pcmHash ^ (ulong)(uint)(int)Math.Round(br[i] * 8192)) * 1099511628211UL;
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"=== replay: {events.Count} events, {endMs / 1000.0:F1} s, packs: {string.Join(", ", cfg.Packs.Where(p => p.Enabled).Select(p => p.Name))} ===");
        sb.AppendLine("events by type:phase: " + string.Join("  ", perType.Select(kv => $"{kv.Key}={kv.Value}")));
        sb.AppendLine("rule hits: " + (rt.RuleHits.Count == 0 ? "none" : string.Join("  ", rt.RuleHits.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}"))));
        sb.AppendLine("routes: " + string.Join("  ", rt.Routes.Select(kv => $"{kv.Key}={kv.Value}")));
        sb.AppendLine("no rule (diagnostics only): " + (rt.Unmatched.Count == 0 ? "none" : string.Join("  ", rt.Unmatched.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}"))));
        sb.AppendLine("cooldown drops: " + (rt.CooldownDrops.Count == 0 ? "none" : string.Join("  ", rt.CooldownDrops.Select(kv => $"{kv.Key}={kv.Value}"))));
        sb.AppendLine($"HD mixer: {hd.Mixer.StatsLine()}");
        sb.AppendLine($"motor patterns: {motor.Patterns.Count} (steps {motor.Patterns.Sum(p => p.steps.Length)})");
        sb.AppendLine($"journal: sessions={sessions} seq_gaps={gaps} duplicate_records={dups} double_deliveries_dropped={doubled} problems={journalProblems.Count}");
        var left = rt.ActiveContinuous.ToList();
        sb.AppendLine("continuous effects still active at the end: " + (left.Count == 0 ? "none" : string.Join(", ", left)));
        sb.AppendLine("state tracker notes: " + (trackerNotes.Count == 0 ? "none" : $"{trackerNotes.Count} (first: {trackerNotes[0]})"));
        sb.AppendLine("rule errors: " + (rt.Errors == 0 ? "none" : rt.Errors + " - " + string.Join(" | ", rt.ErrorList.Take(5))));
        sb.AppendLine("actions: " + rt.Actions.Summary() + (rt.Actions.Overtime.Count > 0 ? " overtime=" + string.Join(",", rt.Actions.Overtime.Select(kv => $"{kv.Key}={kv.Value}")) : ""));
        sb.AppendLine("derived events: " + (rt.DerivedRaised.Count == 0 ? "none" : string.Join("  ", rt.DerivedRaised.Select(kv => $"{kv.Key}={kv.Value}"))));
        sb.AppendLine("states: " + tracker.Summary());

        // ---------------- anomalies ----------------
        var an = new List<(string sev, string text)>();
        if (rt.Errors > 0) an.Add(("ERROR", $"rule errors: {rt.Errors}"));
        if (left.Count > 0) an.Add(("ERROR", $"continuous effects not finished at the end: {string.Join(", ", left)}"));
        if (hd.Mixer.PeakOut > cfg.Limits.Ceiling + 1e-3) an.Add(("ERROR", $"output peak {hd.Mixer.PeakOut:F2} above ceiling {cfg.Limits.Ceiling:F2}"));
        if (gaps > 0) an.Add(("WARN", $"lost events: {gaps} seq gap(s) in the journal"));
        if (dups > 0) an.Add(("WARN", $"duplicate records: {dups}"));
        if (doubled > 0) an.Add(("WARN", $"double deliveries dropped: {doubled} (identical consecutive records - e.g. a doubled subscription in the mod)"));
        foreach (var (k, v) in sameFrame) an.Add(("INFO", $"same-frame repeats of {k}: {v} (two different records within 1 ms)"));
        foreach (var (k, v) in tracker.DoubleStart) an.Add(("WARN", $"state '{k}': start while already active x{v}"));
        foreach (var (k, v) in tracker.EndWithoutStart) an.Add(("WARN", $"state '{k}': end without start x{v}"));
        foreach (var (k, v) in tracker.ZeroDuration) an.Add(("INFO", $"state '{k}': zero-duration blips x{v} (tag set and cleared in one frame)"));
        foreach (var (k, v) in tracker.Stale) an.Add((cfg.Registry.TryGetValue(k, out var rs) && rs.ExpectUnended ? "INFO" : "WARN", $"state '{k}': never ended, closed as stale x{v}"));
        foreach (var (k, v) in rt.Actions.TimedOut) an.Add((rt.Actions.Defs.Any(d => d.Id == k && d.ExpectTimeout) ? "INFO" : "WARN", $"action '{k}': timed out x{v}"));
        foreach (var (k, v) in rt.Actions.Orphans) an.Add(("INFO", $"orphan step {k} x{v} (no open action)"));
        if (hd.Mixer.LimitedPercent > 1.0) an.Add(("WARN", $"limiter active on {hd.Mixer.LimitedPercent:F1}% of samples (> 1%)"));
        if (hd.Mixer.OverloadBlocks > 0) an.Add(("WARN", $"overload (sum > ceiling x 1.15): {hd.Mixer.OverloadBlocks} block(s), guard down to {hd.Mixer.GuardMin:F2}"));
        if (hd.Mixer.GroupOverlapBlocks > 0) an.Add(("WARN", $"same-group overlap: {hd.Mixer.GroupOverlapBlocks} block(s)"));
        foreach (var e in hd.Mixer.Overlaps) an.Add(("INFO", "  episode " + e));
        foreach (var (k, v) in maxRate)
        {
            double lim = cfg.Registry.TryGetValue(k, out var re) && re.MaxRatePerSec > 0 ? re.MaxRatePerSec : 12;
            if (v > lim) an.Add(("WARN", $"abnormal rate: '{k}' {v} events within 1 s (limit {lim})"));
        }
        if (rt.Unmatched.Keys.Any(k => !cfg.Registry.ContainsKey(k.Split(':')[0]))) an.Add(("WARN", "events of types missing from the registry: " + string.Join(", ", rt.Unmatched.Keys.Where(k => !cfg.Registry.ContainsKey(k.Split(':')[0])))));
        sb.AppendLine("ANOMALIES:" + (an.Count == 0 ? " none" : ""));
        foreach (var (sev, text) in an.OrderBy(x => x.sev == "ERROR" ? 0 : x.sev == "WARN" ? 1 : 2)) sb.AppendLine($"  {sev,-5} {text}");
        string verdict = an.Any(x => x.sev == "ERROR") ? "FAIL" : an.Any(x => x.sev == "WARN") ? "WARN" : "OK";
        LastMetrics = new SortedDictionary<string, object>
        {
            ["events"] = events.Count, ["rule_hits"] = new SortedDictionary<string, int>(rt.RuleHits), ["routes"] = new SortedDictionary<string, int>(rt.Routes),
            ["unmatched"] = new SortedDictionary<string, int>(rt.Unmatched), ["cooldown"] = new SortedDictionary<string, int>(rt.CooldownDrops),
            ["voices"] = hd.Mixer.Started, ["crossfaded"] = hd.Mixer.Crossfaded, ["max_concurrent"] = hd.Mixer.MaxConcurrent,
            ["peak_out_x100"] = (int)Math.Round(hd.Mixer.PeakOut * 100), ["overload_blocks"] = hd.Mixer.OverloadBlocks, ["group_overlap_blocks"] = hd.Mixer.GroupOverlapBlocks,
            ["limited_pct_x10"] = (int)Math.Round(hd.Mixer.LimitedPercent * 10), ["motor_patterns"] = motor.Patterns.Count,
            ["actions_completed"] = new SortedDictionary<string, int>(rt.Actions.Completed), ["actions_orphans"] = rt.Actions.Orphans.Values.Sum(),
            ["state_errors"] = tracker.DoubleStart.Values.Sum() + tracker.EndWithoutStart.Values.Sum() + tracker.Stale.Values.Sum(),
            ["continuous_left"] = left.Count, ["rule_errors"] = rt.Errors, ["anomalies_error"] = an.Count(x => x.sev == "ERROR"), ["anomalies_warn"] = an.Count(x => x.sev == "WARN"),
            ["verdict"] = verdict,
            // digital PCM reference (master plan 7): any change of form, timing or side of the HD output shows up here
            ["pcm_hash"] = pcmHash.ToString("x16"), ["pcm_energy_ms_x10"] = new[] { (long)Math.Round(eL / rate * 10000), (long)Math.Round(eR / rate * 10000) },
            ["motor_hash"] = MotorHash(motor), ["late_trims"] = rt.LateTrims, ["late_trimmed_ms"] = (long)Math.Round(rt.LateTrimmedMs)
        };
        bool ok = verdict != "FAIL";
        sb.AppendLine("VERDICT: " + verdict);

        if (o.Wav != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(o.Wav))!);
            using var w = new WaveFileWriter(o.Wav, WaveFormat.CreateIeeeFloatWaveFormat(rate, 2));
            var inter = new float[2 * L.Length];
            for (int i = 0; i < L.Length; i++) { inter[2 * i] = L[i]; inter[2 * i + 1] = R[i]; }
            w.WriteSamples(inter, 0, inter.Length);
            sb.AppendLine($"WAV (left/right actuator, 48 kHz float): {o.Wav}");
        }
        if (o.ReportJson != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(o.ReportJson))!);
            File.WriteAllText(o.ReportJson, JsonSerializer.Serialize(new
            {
                events = events.Count, perType, ruleHits = rt.RuleHits, routes = rt.Routes, unmatched = rt.Unmatched, cooldownDrops = rt.CooldownDrops,
                mixer = new { hd.Mixer.Started, hd.Mixer.Replaced, hd.Mixer.Rejected, hd.Mixer.MaxConcurrent, hd.Mixer.PeakPre, hd.Mixer.PeakOut, hd.Mixer.LimitedSamples },
                motorPatterns = motor.Patterns.Count, sessions, gaps, dups, continuousLeft = left, errors = rt.ErrorList, ok,
                metrics = LastMetrics, anomalies = an.Select(x => new { x.sev, x.text })
            }, new JsonSerializerOptions { WriteIndented = true }));
            sb.AppendLine($"report JSON: {o.ReportJson}");
        }
        return (o.Quiet ? "" : log.ToString()) + sb.ToString();
    }
}

public static class RulesCheck
{
    /// <summary>One rule rendered with the registry sample context (same as the lint below): HD, motor steps, trigger text.</summary>
    public static (float[] l, float[] r, (int delay, byte right, byte left, int ms)[] steps, string pad) RenderSample(HapticConfig cfg, Rule r)
    {
        const int rate = 48000;
        var sample = cfg.Registry.TryGetValue(r.Event, out var re) ? re.Sample : new Dictionary<string, string>();
        var ctx = new Dictionary<string, string>(sample);
        foreach (var (k, v) in r.Where) { var a = v.FirstOrDefault(x => x != "*" && !x.StartsWith("!") && !x.StartsWith("~")); if (a != null) ctx[k] = a; }
        var raw = $"1:{r.Event}:{(r.Phases.FirstOrDefault(p => p != "*") ?? "start")}:src=check" + string.Concat(ctx.Select(kv => $",{kv.Key}={kv.Value}"));
        var ev = EventMonitor.ParseRecord(raw) ?? throw new FormatException("cannot build a sample event " + raw);
        var vars = new Dictionary<string, object?> { ["variation_index"] = 0.0 }; var look = HapticConfig.VarsFor(ev, vars);
        foreach (var (name, ex) in r.Let) vars[name] = ex.Eval(look);
        var (l, rr) = HapticRuntime.RenderHd(r.Effect, look, rate);
        var steps = HapticRuntime.RenderMotor(r.Effect, look, rate);
        string P(Param? p) => p == null ? "-" : p.Resolve(look)?.ToString() ?? "";
        string pad = r.Pad == null ? "" : r.Pad.Reset ? "reset" : $"L2 {P(r.Pad.L2)} R2 {P(r.Pad.R2)}";
        return (l, rr, steps, pad);
    }

    /// <summary>Lints the configuration and renders every rule once with the registry sample context.</summary>
    public static (bool ok, string text) Run(HapticConfig cfg)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"config: {cfg.Root}");
        foreach (var p in cfg.Packs) sb.AppendLine($"  pack {p.Name,-16} {(p.Enabled ? "ON " : "off")} {p.RuleCount,3} rules  {p.File}  {p.Description}");
        sb.AppendLine($"registry: {cfg.Registry.Count} event types; active rules: {cfg.Rules.Count}");
        foreach (var e in cfg.Errors) sb.AppendLine("ERROR   " + e);
        foreach (var w in cfg.Warnings) sb.AppendLine("warning " + w);
        foreach (var dr in cfg.DisabledRules) sb.AppendLine("disabled " + dr);
        int problems = cfg.Errors.Count;
        const int rate = 48000;
        foreach (var r in cfg.Rules)
        {
            var sample = cfg.Registry.TryGetValue(r.Event, out var re) ? re.Sample : new Dictionary<string, string>();
            var ctx = new Dictionary<string, string>(sample);
            foreach (var (k, v) in r.Where) { var a = v.FirstOrDefault(x => x != "*" && !x.StartsWith("!") && !x.StartsWith("~")); if (a != null) ctx[k] = a; }
            var raw = $"1:{r.Event}:{(r.Phases.FirstOrDefault(p => p != "*") ?? "start")}:src=check" + string.Concat(ctx.Select(kv => $",{kv.Key}={kv.Value}"));
            var ev = EventMonitor.ParseRecord(raw);
            if (ev == null) { sb.AppendLine($"ERROR   {r.Pack}/{r.Id}: cannot build a sample event ({raw})"); problems++; continue; }
            try
            {
                var vars = new Dictionary<string, object?> { ["variation_index"] = 0.0 }; var look = HapticConfig.VarsFor(ev, vars);
                foreach (var (name, ex) in r.Let) vars[name] = ex.Eval(look);
                var (l, rr) = HapticRuntime.RenderHd(r.Effect, look, rate);
                var steps = HapticRuntime.RenderMotor(r.Effect, look, rate);
                float peak = Math.Max(l.Length > 0 ? l.Max(Math.Abs) : 0, rr.Length > 0 ? rr.Max(Math.Abs) : 0);
                double Rms20(float[] x) { int w = rate / 50; double best = 0, acc = 0; for (int i = 0; i < x.Length; i++) { acc += x[i] * x[i]; if (i >= w) acc -= x[i - w] * x[i - w]; if (i >= w - 1) best = Math.Max(best, Math.Sqrt(Math.Max(0, acc) / w)); } return x.Length < w && x.Length > 0 ? Math.Sqrt(x.Sum(v => v * v) / w) : best; }
                double rms = Math.Max(Rms20(l), Rms20(rr));
                bool finite = l.All(float.IsFinite) && rr.All(float.IsFinite);
                // clipping: RenderHd hard-clamps the layer sum at 0.95; samples above the limiter knee are rounded by the mixer
                long clip = l.Count(x => Math.Abs(x) >= 0.9499f) + rr.Count(x => Math.Abs(x) >= 0.9499f);
                long knee = l.Count(x => Math.Abs(x) > Analysis.LimiterKnee) + rr.Count(x => Math.Abs(x) > Analysis.LimiterKnee);
                long ms = 1000L * Math.Max(l.Length, rr.Length) / rate;
                string padText = "";
                if (r.Pad != null)
                {
                    string P(Param? p) { if (p == null) return "-"; var v = p.Resolve(look)?.ToString() ?? ""; return cfg.TriggerProfiles.TryGetValue(v, out var tp) ? tp.ToString() : $"MISSING '{v}'"; }
                    padText = r.Pad.Reset ? "; pad reset" : $"; pad L2 {P(r.Pad.L2)} R2 {P(r.Pad.R2)}";
                }
                string flag = !finite ? "ERROR non-finite samples" : padText.Contains("MISSING") ? "ERROR unknown trigger profile"
                            : ms == 0 && r.Pad == null ? "warning empty HD rendering" : ms > 30000 && !r.Continuous ? "warning longer than 30 s"
                            : clip > 0 ? "warning hard clipping" : "ok";
                if (flag.StartsWith("ERROR")) problems++;
                sb.AppendLine($"  {flag,-7} {r.Pack}/{r.Id,-16} {r.Event}:{string.Join("|", r.Phases)} path={r.Path} prio={r.Priority} HD {ms} ms peak {peak:F2} rms20 {rms:F2}{(clip > 0 ? $" CLIP {clip}" : "")}{(knee > 0 ? $" knee {100.0 * knee / Math.Max(1, l.Length + rr.Length):0.#}%" : "")}; motor {steps.Length} steps{padText}" +
                              $"{(r.Let.Count > 0 ? "  [" + string.Join(", ", vars.Select(kv => $"{kv.Key}={Fmt(kv.Value)}")) + "]" : "")}");
            }
            catch (Exception ex) { sb.AppendLine($"ERROR   {r.Pack}/{r.Id}: {ex.GetType().Name}: {ex.Message}"); problems++; }
        }
        if (SampleBank.Missing.Count > 0) { foreach (var m in SampleBank.Missing) sb.AppendLine("warning forge bank sample missing: " + m); }
        var (selDef, selSlots) = SampleBank.FileSelection;
        sb.AppendLine($"forge bank selection: default {selDef}{(selSlots.Count > 0 ? ", " + string.Join(", ", selSlots.Select(kv => kv.Key + "=" + kv.Value)) : "")}");
        sb.AppendLine(problems == 0 ? "RULES-CHECK: OK" : $"RULES-CHECK: {problems} problem(s)");
        return (problems == 0, sb.ToString());
    }
    static string Fmt(object? v) => v is double d ? d.ToString("0.###", CultureInfo.InvariantCulture) : v?.ToString() ?? "null";
}

public static class Coverage
{
    /// <summary>Markdown: registry types x rules x status, plus analyzer candidates not connected yet.</summary>
    public static string Build(HapticConfig cfg, string? candidatesJson)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Coverage report ({DateTime.Now:yyyy-MM-dd HH:mm})");
        sb.AppendLine();
        sb.AppendLine("Statuses: DISCOVERED / HOOKABLE / IMPLEMENTED (mod sends it) / TESTED (seen in a real session) / BLOCKED.");
        sb.AppendLine();
        sb.AppendLine("## Connected events (registry) and their effects");
        sb.AppendLine();
        sb.AppendLine("| Event | Phases | Status | Rules (path) | Source |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var e in cfg.Registry.Values.OrderBy(x => x.Category).ThenBy(x => x.Type))
        {
            var rules = cfg.Rules.Where(r => r.Event == e.Type).Select(r => $"{r.Id} ({r.Path}, {string.Join("|", r.Phases)})").ToList();
            sb.AppendLine($"| `{e.Type}` | {string.Join(", ", e.Phases)} | {e.Status} | {(rules.Count == 0 ? "— (diagnostics only)" : string.Join("<br>", rules))} | {e.Source.Replace("|", "\\|")} |");
        }
        if (candidatesJson != null && File.Exists(candidatesJson))
        {
            using var d = JsonDocument.Parse(File.ReadAllText(candidatesJson));
            var cands = d.RootElement.GetProperty("candidates").EnumerateArray().ToList();
            sb.AppendLine();
            sb.AppendLine($"## Analyzer candidates not connected yet ({cands.Count(c => c.GetProperty("connected").GetBoolean() == false)} of {cands.Count})");
            sb.AppendLine();
            sb.AppendLine("| Template | Kind | Name | Status | Hook | Where |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var c in cands.Where(c => !c.GetProperty("connected").GetBoolean()).OrderBy(c => c.GetProperty("template").GetString()).ThenBy(c => c.GetProperty("kind").GetString()))
                sb.AppendLine($"| {c.GetProperty("template").GetString()} | {c.GetProperty("kind").GetString()} | `{c.GetProperty("name").GetString()}` | {c.GetProperty("status").GetString()} | {c.GetProperty("hook").GetString()} | {c.GetProperty("where").GetString()} |");
        }
        else { sb.AppendLine(); sb.AppendLine("_No analyzer output (research/hooks/candidates.json) - run `python research/tools/hook_scan.py`._"); }
        return sb.ToString();
    }
}


/// <summary>Level map (19.11.2026): replays real journals and measures every HD voice each rule really asked for - rms20
/// (strongest 20 ms, the forge loudness measure), peak and length - so strengths of different packs can be compared on one
/// scale (tools/level_map.py groups them by role and flags breaks of the intensity hierarchy).</summary>
public static class LevelMap
{
    public static double Rms20(float[] x, int rate = 48000)
    {
        int w = rate / 50; double best = 0, acc = 0;
        for (int i = 0; i < x.Length; i++) { acc += x[i] * x[i]; if (i >= w) acc -= x[i - w] * x[i - w]; if (i >= w - 1) best = Math.Max(best, Math.Sqrt(Math.Max(0, acc) / w)); }
        return x.Length < w && x.Length > 0 ? Math.Sqrt(x.Sum(v => (double)v * v) / w) : best;
    }

    public static (int journals, double hours, string json) Run(HapticConfig cfg, IEnumerable<string> journals)
    {
        var per = new Dictionary<string, List<(double rms, double peak, double ms)>>();
        int n = 0; double hours = 0;
        foreach (var f in journals)
        {
            var pr = new List<string>();
            var ev = Journal.Load(f, pr);
            if (ev.Count == 0) continue;
            n++; hours += ev[^1].t / 3.6e6;
            Replay.Run(cfg, ev, new Replay.Options
            {
                Quiet = true,
                OnVoice = (l, r, o) =>
                {
                    if (!per.TryGetValue(o.Tag, out var lst)) per[o.Tag] = lst = new();
                    float pk = Math.Max(l.Length > 0 ? l.Max(Math.Abs) : 0, r.Length > 0 ? r.Max(Math.Abs) : 0);
                    lst.Add((Math.Max(Rms20(l), Rms20(r)), pk, 1000.0 * Math.Max(l.Length, r.Length) / 48000));
                }
            }, pr);
        }
        static double Q(List<double> v, double q) { if (v.Count == 0) return 0; v.Sort(); return v[(int)Math.Min(v.Count - 1, Math.Round(q * (v.Count - 1)))]; }
        var byId = cfg.Rules.ToDictionary(r => r.Id, r => r);
        var rows = per.OrderBy(kv => kv.Key).Select(kv =>
        {
            byId.TryGetValue(kv.Key, out var rule);
            var rms = kv.Value.Select(x => x.rms).ToList(); var pk = kv.Value.Select(x => x.peak).ToList(); var ms = kv.Value.Select(x => x.ms).ToList();
            return new Dictionary<string, object?>
            {
                ["id"] = kv.Key, ["pack"] = rule?.Pack, ["event"] = rule?.Event, ["priority"] = rule?.Priority, ["path"] = rule?.Path,
                ["continuous"] = rule?.Continuous, ["n"] = kv.Value.Count,
                ["rms20_p50"] = Math.Round(Q(rms, .5), 4), ["rms20_p90"] = Math.Round(Q(rms, .9), 4), ["rms20_max"] = Math.Round(rms.Max(), 4),
                ["peak_p50"] = Math.Round(Q(pk, .5), 4), ["ms_p50"] = Math.Round(Q(ms, .5))
            };
        }).ToList();
        var doc = new Dictionary<string, object?> { ["generated"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm"), ["journals"] = n, ["hours"] = Math.Round(hours, 2), ["rules"] = rows };
        return (n, hours, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>Regression: replays every journal in a folder and compares the key metrics with saved baselines.
/// A difference = the behaviour changed (rule hits, routes, voices, peaks, anomalies...). --update stores new baselines.</summary>
public static class Regress
{
    public static (bool ok, string text) Run(HapticConfig cfg, string journalsDir, string baselineDir, bool update)
    {
        var sb = new StringBuilder(); bool ok = true;
        Directory.CreateDirectory(baselineDir);
        var files = Directory.GetFiles(journalsDir, "*.jsonl").OrderBy(x => x).ToList();
        if (files.Count == 0) return (false, "no journals in " + journalsDir);
        foreach (var f in files)
        {
            var name = Path.GetFileNameWithoutExtension(f);
            var pr = new List<string>();
            Replay.Run(cfg, Journal.Load(f, pr), new Replay.Options { Quiet = true }, pr);
            var cur = JsonSerializer.Serialize(Replay.LastMetrics, new JsonSerializerOptions { WriteIndented = true });
            var bpath = Path.Combine(baselineDir, name + ".json");
            if (update || !File.Exists(bpath))
            {
                File.WriteAllText(bpath, cur);
                sb.AppendLine($"  BASE  {name,-28} {(update ? "updated" : "created")} (verdict {Replay.LastMetrics["verdict"]})");
                continue;
            }
            var old = JsonDocument.Parse(File.ReadAllText(bpath)).RootElement;
            var now = JsonDocument.Parse(cur).RootElement;
            var diffs = new List<string>();
            foreach (var p in now.EnumerateObject())
            {
                var o = old.TryGetProperty(p.Name, out var ov) ? ov.ToString() : "(missing)";
                if (o != p.Value.ToString()) diffs.Add($"{p.Name}: {o} -> {p.Value}");
            }
            if (diffs.Count == 0) sb.AppendLine($"  same  {name,-28} verdict {Replay.LastMetrics["verdict"]}");
            else { ok = false; sb.AppendLine($"  DIFF  {name,-28} {diffs.Count} metric(s) changed:"); foreach (var d in diffs.Take(12)) sb.AppendLine("          " + d); }
        }
        sb.AppendLine(ok ? "REGRESS: OK (no behaviour change)" : "REGRESS: CHANGED - review the diffs; if intended run `regress --update`");
        return (ok, sb.ToString());
    }
}
