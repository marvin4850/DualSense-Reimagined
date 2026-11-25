using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DsBridge.Haptics;

/// <summary>Approval signatures (universal master plan, G: "замена WAV, единиц, тайминга или формы лишает объект прежнего
/// допуска"). haptics/approvals.json keeps, per approved pack, a signature of every rule = its definition (without notes)
/// + what it renders with the registry sample context (HD PCM, motor steps, trigger profiles) under the pack's options
/// (onsetLead...) and the bank WAVs it uses. `approvals --check` fails when an APPROVED pack renders differently: the change
/// must be re-approved in game (`approvals --record PACK approved "gameNN ..."`) or marked for a retest
/// (`approvals --record PACK retest "why"`). Status "retest" passes the check but is listed every time.</summary>
public static class Approvals
{
    public static string PathOf(HapticConfig cfg) => Path.Combine(cfg.Root, "approvals.json");

    /// <summary>Rule id -> 12-hex signature for every rule of one pack (as loaded, i.e. with the pack's haptics.json options).</summary>
    public static SortedDictionary<string, string> Sign(HapticConfig cfg, string pack)
    {
        var info = cfg.Packs.FirstOrDefault(p => p.Name.Equals(pack, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ArgumentException($"pack '{pack}' is not in haptics.json");
        var defs = RuleDefinitions(Path.Combine(cfg.Root, info.File));
        var res = new SortedDictionary<string, string>(StringComparer.Ordinal);
        // active rules of the pack (a rule replaced by a later pack, or a disabled pack, no longer plays = a change)
        foreach (var r in cfg.Rules.Where(r => r.Pack.Equals(info.Name, StringComparison.OrdinalIgnoreCase)))
        {
            var sb = new StringBuilder();
            sb.Append(defs.TryGetValue(r.Id, out var d) ? d : "?").Append('|');
            try
            {
                var (l, rr, steps, pad) = RulesCheck.RenderSample(cfg, r);
                sb.Append(l.Length).Append(',').Append(rr.Length).Append('|');
                foreach (var x in l) sb.Append((int)Math.Round(x * 4096)).Append(',');
                sb.Append('|');
                foreach (var x in rr) sb.Append((int)Math.Round(x * 4096)).Append(',');
                sb.Append('|').Append(string.Join(";", steps.Select(s => $"{s.delay},{s.right},{s.left},{s.ms}"))).Append('|').Append(pad);
            }
            catch (Exception ex) { sb.Append("render error ").Append(ex.GetType().Name); }
            res[r.Id] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..12].ToLowerInvariant();
        }
        return res;
    }

    /// <summary>Rule JSON without "note" fields (comments may change freely).</summary>
    static Dictionary<string, string> RuleDefinitions(string packPath)
    {
        var res = new Dictionary<string, string>();
        var node = JsonNode.Parse(File.ReadAllText(packPath), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        foreach (var r in node?["rules"]?.AsArray() ?? new JsonArray())
        {
            if (r is not JsonObject o || o["id"]?.GetValue<string>() is not string id) continue;
            res[id] = Strip(o.DeepClone()).ToJsonString();
        }
        return res;
        static JsonNode Strip(JsonNode n)
        {
            if (n is JsonObject ob) { ob.Remove("note"); foreach (var k in ob.Select(kv => kv.Key).ToList()) if (ob[k] != null) Strip(ob[k]!); }
            else if (n is JsonArray a) foreach (var x in a) if (x != null) Strip(x);
            return n;
        }
    }

    static string Combined(SortedDictionary<string, string> rules) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", rules.Select(kv => kv.Key + "=" + kv.Value)))))[..16].ToLowerInvariant();

    static JsonObject Load(HapticConfig cfg)
    {
        var p = PathOf(cfg);
        if (!File.Exists(p)) return new JsonObject { ["packs"] = new JsonObject() };
        return (JsonNode.Parse(File.ReadAllText(p)) as JsonObject) ?? new JsonObject { ["packs"] = new JsonObject() };
    }

    public static string Record(HapticConfig cfg, string pack, string status, string note)
    {
        if (status is not ("approved" or "retest")) throw new ArgumentException("status must be approved|retest");
        var doc = Load(cfg); var packs = doc["packs"] as JsonObject ?? new JsonObject(); doc["packs"] = packs;
        var sig = Sign(cfg, pack);
        var name = cfg.Packs.First(p => p.Name.Equals(pack, StringComparison.OrdinalIgnoreCase)).Name;
        var prev = packs[name] as JsonObject;
        var entry = new JsonObject
        {
            ["status"] = status,
            ["approved"] = status == "approved" ? note : prev?["approved"]?.GetValue<string>() ?? "",
            ["retest"] = status == "retest" ? note : null,
            ["recorded"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            ["signature"] = Combined(sig),
            ["rules"] = new JsonObject(sig.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value))))
        };
        if (entry["retest"] == null) entry.Remove("retest");
        packs[name] = entry;
        doc["about"] = "Approval signatures (DsBridge approvals): an approved pack must render exactly as when the user approved it. " +
                       "Changed -> re-approve in game (--record PACK approved \"gameNN ...\") or mark --record PACK retest \"why\".";
        File.WriteAllText(PathOf(cfg), doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
        return $"{name}: {status}, {sig.Count} rules, signature {Combined(sig)}";
    }

    public static (bool ok, string text) Check(HapticConfig cfg)
    {
        var sb = new StringBuilder(); bool ok = true;
        var packs = Load(cfg)["packs"] as JsonObject ?? new JsonObject();
        if (packs.Count == 0) return (true, "APPROVALS: none recorded\n");
        foreach (var (name, node) in packs)
        {
            if (node is not JsonObject e) continue;
            string status = e["status"]?.GetValue<string>() ?? "?";
            if (!cfg.Packs.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) { sb.AppendLine($"  gone   {name}: not in haptics.json"); ok = false; continue; }
            var now = Sign(cfg, name);
            var then = (e["rules"] as JsonObject)?.ToDictionary(kv => kv.Key, kv => kv.Value?.GetValue<string>() ?? "") ?? new();
            var changed = now.Where(kv => !then.TryGetValue(kv.Key, out var h) || h != kv.Value).Select(kv => kv.Key)
                             .Concat(then.Keys.Where(k => !now.ContainsKey(k))).Distinct().ToList();
            if (status == "retest")
                sb.AppendLine($"  retest {name}: awaiting re-approval in game - {e["retest"]?.GetValue<string>()}{(changed.Count > 0 ? $" (and {changed.Count} rule(s) changed since it was marked)" : "")}");
            else if (changed.Count == 0)
                sb.AppendLine($"  same   {name}: {now.Count} rules as approved ({e["approved"]?.GetValue<string>()})");
            else
            {
                ok = false;
                sb.AppendLine($"  CHANGED {name}: {changed.Count} of {now.Count} approved rule(s) render differently: {string.Join(", ", changed.Take(8))}{(changed.Count > 8 ? ", ..." : "")}");
            }
        }
        sb.AppendLine(ok ? "APPROVALS: OK" : "APPROVALS: an approved pack changed - re-approve it in game (approvals --record PACK approved \"gameNN\") or mark it (approvals --record PACK retest \"why\")");
        return (ok, sb.ToString());
    }
}
