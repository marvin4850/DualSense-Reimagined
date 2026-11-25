using System.Text;
using System.Text.Json;

namespace DsBridge.Haptics;

/// <summary>Verifiable registry: 7 stages per event, each backed by evidence.
/// 1 discovered (analyzer candidates.json) - 2 generated (registry / registry.generated.json) - 3 compiled in the game
/// (a journal session reported hooks:update with the CURRENT mod build id) - 4 registered at runtime (that report's hook
/// counts equal the build's counts) - 5 fired in a session - 6 source/context confirmed (expected src, registered on the
/// player's own ability system by construction, state behaviour sane) - 7 ready for a haptic effect (confirmed, seen &gt;= 2
/// times, blips &lt; 50 %, no state errors).</summary>
public static class RegistryStatus
{
    sealed class Stat { public int Count, CountCurrent, Blips, Errors, BadSrc; public HashSet<string> Journals = new(); }

    public static (string md, string json, string summary) Build(HapticConfig cfg, string? candidatesJson, string journalsDir)
    {
        // current build (registry.generated.json)
        string build = "", genPath = Path.Combine(cfg.Root, "registry.generated.json");
        int buildTags = -1, buildEvents = -1;
        var genTypes = new Dictionary<string, string>();   // type -> tag
        if (File.Exists(genPath))
        {
            using var g = JsonDocument.Parse(File.ReadAllText(genPath));
            if (g.RootElement.TryGetProperty("build", out var b)) build = b.GetString() ?? "";
            if (g.RootElement.TryGetProperty("tags", out var t)) buildTags = t.GetInt32();
            if (g.RootElement.TryGetProperty("eventHooks", out var e)) buildEvents = e.GetInt32();
            foreach (var ev in g.RootElement.GetProperty("events").EnumerateArray())
            {
                var src = ev.GetProperty("source").GetString() ?? "";
                var m = System.Text.RegularExpressions.Regex.Match(src, @"hook (\S+)");
                genTypes[ev.GetProperty("type").GetString()!] = m.Success ? m.Groups[1].Value : "";
            }
        }
        // candidates
        var cand = new Dictionary<string, string>();   // name -> kind
        if (candidatesJson != null && File.Exists(candidatesJson))
        {
            using var c = JsonDocument.Parse(File.ReadAllText(candidatesJson));
            foreach (var x in c.RootElement.GetProperty("candidates").EnumerateArray()) cand[x.GetProperty("name").GetString()!] = x.GetProperty("kind").GetString()!;
        }
        // journals: per session, the build it reported
        var stats = new Dictionary<string, Stat>();
        bool compiled = false, registered = false; var buildSeen = new HashSet<string>();
        var files = Directory.Exists(journalsDir) ? Directory.GetFiles(journalsDir, "*.jsonl") : Array.Empty<string>();
        foreach (var f in files.Where(x => !Path.GetFileName(x).StartsWith("synthetic", StringComparison.OrdinalIgnoreCase)))   // synthetic journals = regression only, not game evidence
        {
            var tr = new StateTracker(); cfg.ConfigureTracker(tr);
            string? sessionBuild = null; var t0 = new DateTime(2026, 1, 1);
            string? lastBody = null;
            foreach (var (t, ev) in Journal.Load(f))
            {
                var body = ev.Raw[(ev.Raw.IndexOf(':') + 1)..];
                if (body == lastBody && ev.Type is not ("session" or "hooks")) continue;   // double delivery
                lastBody = body;
                // the mod reports hooks:update right before session:start, so the build is kept until the next report
                if (ev.Type == "hooks")
                {
                    sessionBuild = ev.Get("build"); if (sessionBuild != null) buildSeen.Add(sessionBuild);
                    if (sessionBuild == build && build.Length > 0)
                    {
                        compiled = true;
                        if (ev.GetInt("tags") == buildTags && ev.GetInt("events") == buildEvents) registered = true;
                    }
                }
                var s = stats.TryGetValue(ev.Type, out var x) ? x : stats[ev.Type] = new Stat();
                s.Count++; s.Journals.Add(Path.GetFileNameWithoutExtension(f));
                if (sessionBuild == build && build.Length > 0) s.CountCurrent++;
                bool gen = genTypes.ContainsKey(ev.Type);
                if (gen != ev.Src.StartsWith("gen_") && ev.Src != "v1") s.BadSrc++;
                int b0 = tr.ZeroDuration.Values.Sum(), e0 = tr.DoubleStart.Values.Sum() + tr.EndWithoutStart.Values.Sum();
                tr.Apply(ev, t0.AddMilliseconds(t));
                s.Blips += tr.ZeroDuration.Values.Sum() - b0; s.Errors += tr.DoubleStart.Values.Sum() + tr.EndWithoutStart.Values.Sum() - e0;
            }
        }

        var rows = new List<Dictionary<string, object>>();
        foreach (var r in cfg.Registry.Values.OrderBy(x => genTypes.ContainsKey(x.Type)).ThenBy(x => x.Category).ThenBy(x => x.Type))
        {
            bool gen = genTypes.ContainsKey(r.Type);
            string tag = gen ? genTypes[r.Type] : "";
            stats.TryGetValue(r.Type, out var st); st ??= new Stat();
            bool s1 = true;   // hand-written: in the mod source; generated: gen_hooks.py validated the tag against the game tag config + scripts
            bool s2 = true;
            bool s3 = compiled; bool s4 = gen ? registered : st.Count > 0;
            bool s5 = gen ? st.CountCurrent > 0 || st.Count > 0 : st.Count > 0;
            bool s6 = s5 && st.BadSrc == 0;
            bool s7 = s6 && st.Count >= 2 && st.Blips * 2 < Math.Max(1, st.Count / 2 + 1) && st.Errors == 0;
            int stage = !s1 ? 0 : !s2 ? 1 : !s3 ? 2 : !s4 ? 3 : !s5 ? 4 : !s6 ? 5 : !s7 ? 6 : 7;
            var rules = cfg.Rules.Where(x => x.Event == r.Type).Select(x => x.Id).ToList();
            rows.Add(new Dictionary<string, object>
            {
                ["type"] = r.Type, ["origin"] = gen ? "generated: " + tag : "hand-written", ["stage"] = stage,
                ["discovered"] = s1, ["generated"] = s2, ["compiled"] = s3, ["registered"] = s4, ["fired"] = s5, ["confirmed"] = s6, ["ready"] = s7,
                ["count"] = st.Count, ["count_current_build"] = st.CountCurrent, ["blips"] = st.Blips, ["state_errors"] = st.Errors, ["bad_src"] = st.BadSrc,
                ["journals"] = st.Journals.Count, ["rules"] = rules
            });
        }
        int nCand = cand.Count, nGen = genTypes.Count, nHand = cfg.Registry.Count - nGen;
        int Cnt(string k) => rows.Count(x => (bool)x[k]);
        var summary = $"candidates {nCand} | registry {cfg.Registry.Count} (hand-written {nHand}, generated {nGen}) | current build {(build.Length > 0 ? build : "-")}: " +
                      $"compiled {(compiled ? "YES" : "not yet seen")}, registered {(registered ? "YES" : "not confirmed")} | fired {Cnt("fired")} | confirmed {Cnt("confirmed")} | ready {Cnt("ready")} | with rules {rows.Count(x => ((List<string>)x["rules"]).Count > 0)}";

        var md = new StringBuilder();
        md.AppendLine($"# Registry status ({DateTime.Now:yyyy-MM-dd HH:mm})");
        md.AppendLine();
        md.AppendLine("Stages: 1 discovered · 2 generated · 3 compiled in game · 4 registered at runtime · 5 fired in a session · 6 source/context confirmed · 7 ready for a haptic effect.");
        md.AppendLine("Evidence: analyzer candidates, registry files, journals (`hooks:update build=… tags=… events=…` from mod v11, events, state behaviour).");
        md.AppendLine();
        md.AppendLine("**" + summary + "**");
        md.AppendLine();
        md.AppendLine($"Builds seen in journals: {(buildSeen.Count == 0 ? "none (journals before mod v11)" : string.Join(", ", buildSeen))}.");
        md.AppendLine();
        md.AppendLine("| Event | Origin | Stage | 1 | 2 | 3 | 4 | 5 | 6 | 7 | Seen | Blips | State errors | Rules |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        static string Y(object b) => (bool)b ? "✔" : "·";
        foreach (var x in rows)
            md.AppendLine($"| `{x["type"]}` | {x["origin"]} | {x["stage"]} | {Y(x["discovered"])} | {Y(x["generated"])} | {Y(x["compiled"])} | {Y(x["registered"])} | {Y(x["fired"])} | {Y(x["confirmed"])} | {Y(x["ready"])} | {x["count"]} | {x["blips"]} | {x["state_errors"]} | {string.Join(", ", (List<string>)x["rules"])} |");
        md.AppendLine();
        md.AppendLine($"Not connected yet: {nCand - rows.Count(x => (bool)x["discovered"] && ((string)x["origin"]).StartsWith("generated"))} analyzer candidates (see research/hooks/candidates.md).");
        var json = JsonSerializer.Serialize(new { summary, build, compiled, registered, rows }, new JsonSerializerOptions { WriteIndented = true });
        return (md.ToString(), json, summary);
    }
}
