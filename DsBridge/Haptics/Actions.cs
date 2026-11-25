using System.Text.Json;

namespace DsBridge.Haptics;

/// <summary>One correlated game action, declared in haptics/actions.json:
/// start (opens an instance) -> steps (attached only while the instance is open) -> end / interrupt (closes it).
/// Correlation is state-based, not proximity-based: a step that arrives while no instance is open is an orphan, never
/// glued to a nearby action. maxMs is only a safety net (instance closed as timed out).</summary>
public sealed class ActionDef
{
    public string Id = "";
    public (string type, string phase, Dictionary<string, string[]> where) Start;
    public List<(string type, string phase, Dictionary<string, string[]> where)> Steps = new(), End = new(), Interrupt = new();
    public int MaxMs = 15000;
    public string OnRestart = "restart";
    public bool ExpectTimeout;             // a timeout is normal for this action (reported as INFO)
    public Expr.Compiled? OvertimeExpr;   // "overtimeMs": "=formula" - evaluated with the runtime context when an instance opens
    public int OvertimeMs;                 // > 0: once the instance is open this long, an "overtime:start a=<id>" event is raised (e.g. bow held too long)   // restart: close the open instance (as "restarted") and open a new one | ignore: keep the open one

    public static bool Match((string type, string phase, Dictionary<string, string[]> where) p, GameEvent ev)
    {
        if (p.type != ev.Type || (p.phase != "*" && p.phase != ev.Phase)) return false;
        foreach (var (k, allowed) in p.where)
        {
            var v = ev.Get(k); bool ok = false;
            foreach (var a in allowed)
            {
                if (a == "*") { if (v != null) { ok = true; break; } continue; }
                if (a.StartsWith("!~")) { if (v == null || !v.Contains(a[2..])) { ok = true; break; } continue; }
                if (a.StartsWith("~")) { if (v != null && v.Contains(a[1..])) { ok = true; break; } continue; }   // contains (e.g. montage names)
                if (a.StartsWith("!")) { if (v != a[1..]) { ok = true; break; } continue; }
                if (v == a) { ok = true; break; }
            }
            if (!ok) return false;
        }
        return true;
    }
}

public sealed class ActionInstance
{
    public ActionDef Def = null!; public long N; public DateTime Started; public int StepCount; public string Last = ""; public long StartSeq; public bool OvertimeRaised; public int OvertimeMs;
}

/// <summary>Annotates events with the actions they belong to. Added context fields (usable in rules: where / when / let):
///   act_&lt;id&gt;      = start | step | end | interrupt | orphan
///   act_&lt;id&gt;_n    = instance number (per session)
///   act_&lt;id&gt;_ms   = ms since the instance started
///   act_&lt;id&gt;_step = steps seen so far in the instance (incl. this one)
///   act_&lt;id&gt;_prev = previous step type:phase in the instance
///   act           = id of the first action the event belongs to (shortcut)</summary>
public sealed class Correlator
{
    public readonly List<ActionDef> Defs = new();
    /// <summary>Context for formula fields (overtimeMs "=..."): set by the runtime.</summary>
    public Func<Expr.Lookup>? Vars;
    readonly Dictionary<string, ActionInstance> _open = new();
    readonly Dictionary<string, long> _counter = new();
    public readonly Dictionary<string, int> Started = new(), Completed = new(), Interrupted = new(), TimedOut = new(), Restarted = new(), Orphans = new(), SystemClosed = new();
    public readonly Dictionary<string, List<int>> Durations = new();
    public event Action<string, string>? Closed;   // (action id, how) - lets the runtime stop effects bound to the action

    static void Bump(Dictionary<string, int> d, string k) => d[k] = d.TryGetValue(k, out var v) ? v + 1 : 1;
    public IEnumerable<string> OpenActions => _open.Keys;

    public static Correlator Load(string? path, List<string> errors)
    {
        var c = new Correlator();
        if (path == null || !File.Exists(path)) return c;
        try
        {
            using var d = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            foreach (var a in d.RootElement.GetProperty("actions").EnumerateArray())
            {
                var def = new ActionDef { Id = a.GetProperty("id").GetString()! };
                def.Start = P(a.GetProperty("start"));
                if (a.TryGetProperty("steps", out var s)) def.Steps = s.EnumerateArray().Select(P).ToList();
                if (a.TryGetProperty("end", out var e)) def.End = e.EnumerateArray().Select(P).ToList();
                if (a.TryGetProperty("interrupt", out var i)) def.Interrupt = i.EnumerateArray().Select(P).ToList();
                if (a.TryGetProperty("maxMs", out var m)) def.MaxMs = m.GetInt32();
                if (a.TryGetProperty("onRestart", out var r)) def.OnRestart = r.GetString() ?? "restart";
                if (a.TryGetProperty("expectTimeout", out var et)) def.ExpectTimeout = et.GetBoolean();
                if (a.TryGetProperty("overtimeMs", out var ot))
                {
                    if (ot.ValueKind == JsonValueKind.String) { try { def.OvertimeExpr = Expr.Compile(ot.GetString()!.TrimStart('=')); def.OvertimeMs = 1; } catch (Exception ex) { errors.Add($"actions: {def.Id}.overtimeMs: {ex.Message}"); } }
                    else def.OvertimeMs = ot.GetInt32();
                }
                if (!System.Text.RegularExpressions.Regex.IsMatch(def.Id, "^[a-z0-9_]+$")) errors.Add($"actions: bad id '{def.Id}'");
                if (c.Defs.Any(x => x.Id == def.Id)) errors.Add($"actions: duplicate id '{def.Id}'");
                c.Defs.Add(def);
            }
        }
        catch (Exception ex) { errors.Add($"actions {path}: {ex.Message}"); }
        return c;
    }

    /// <summary>"type:phase" or { "event": "type:phase", "where": { ... } }</summary>
    static (string, string, Dictionary<string, string[]>) P(JsonElement e)
    {
        string s; var where = new Dictionary<string, string[]>();
        if (e.ValueKind == JsonValueKind.String) s = e.GetString()!;
        else
        {
            s = e.GetProperty("event").GetString()!;
            if (e.TryGetProperty("where", out var w))
                foreach (var p in w.EnumerateObject())
                    where[p.Name] = p.Value.ValueKind == JsonValueKind.Array ? p.Value.EnumerateArray().Select(x => x.ToString()).ToArray() : new[] { p.Value.ToString() };
        }
        int c = s.IndexOf(':');
        return c > 0 ? (s[..c], s[(c + 1)..], where) : (s, "start", where);
    }

    public void Expire(DateTime now)
    {
        foreach (var id in _open.Keys.ToList())
        {
            var inst = _open[id];
            if ((now - inst.Started).TotalMilliseconds > inst.Def.MaxMs) Close(id, "timeout", now);
        }
    }

    void Close(string id, string how, DateTime now)
    {
        if (!_open.TryGetValue(id, out var inst)) return;
        _open.Remove(id);
        switch (how)
        {
            case "end": Bump(Completed, id); (Durations.TryGetValue(id, out var l) ? l : Durations[id] = new()).Add((int)(now - inst.Started).TotalMilliseconds); break;
            case "interrupt": Bump(Interrupted, id); break;
            case "timeout": Bump(TimedOut, id); break;
            case "restart": Bump(Restarted, id); break;
            default: Bump(SystemClosed, id); break;
        }
        Closed?.Invoke(id, how);
    }

    /// <summary>Instances open longer than their OvertimeMs (raised once per instance): (action id, instance number, ms open).</summary>
    public List<(string id, long n, long ms)> CheckOvertime(DateTime now)
    {
        var res = new List<(string, long, long)>();
        foreach (var inst in _open.Values)
            if (inst.OvertimeMs > 0 && !inst.OvertimeRaised && (now - inst.Started).TotalMilliseconds >= inst.OvertimeMs)
            { inst.OvertimeRaised = true; Bump(Overtime, inst.Def.Id); res.Add((inst.Def.Id, inst.N, (long)(now - inst.Started).TotalMilliseconds)); }
        return res;
    }
    public readonly Dictionary<string, int> Overtime = new();

    public void CloseAll(string reason, DateTime now) { foreach (var id in _open.Keys.ToList()) Close(id, "system:" + reason, now); }

    /// <summary>Returns the event with action fields added (or the same event if it belongs to no action).</summary>
    public GameEvent Annotate(GameEvent ev, DateTime now)
    {
        if (Defs.Count == 0) return ev;
        Expire(now);
        Dictionary<string, string>? add = null; string? first = null;
        void Put(ActionDef d, string role, ActionInstance? inst)
        {
            add ??= new Dictionary<string, string>();
            add[$"act_{d.Id}"] = role;
            if (inst != null)
            {
                add[$"act_{d.Id}_n"] = inst.N.ToString();
                add[$"act_{d.Id}_ms"] = ((long)(now - inst.Started).TotalMilliseconds).ToString();
                add[$"act_{d.Id}_step"] = inst.StepCount.ToString();
                if (inst.Last.Length > 0) add[$"act_{d.Id}_prev"] = inst.Last.Replace(':', '_');
            }
            if (role != "orphan") first ??= d.Id;
        }
        foreach (var d in Defs)
        {
            _open.TryGetValue(d.Id, out var inst);
            bool isStart = ActionDef.Match(d.Start, ev);
            bool isEnd = d.End.Any(p => ActionDef.Match(p, ev));
            bool isInt = d.Interrupt.Any(p => ActionDef.Match(p, ev));
            bool isStep = d.Steps.Any(p => ActionDef.Match(p, ev));
            if (inst != null && (isEnd || isInt))
            {
                inst.StepCount++;
                Put(d, isEnd ? "end" : "interrupt", inst);
                Close(d.Id, isEnd ? "end" : "interrupt", now);
                inst = null;
                if (!isStart) continue;
            }
            if (isStart)
            {
                if (inst != null)
                {
                    if (d.OnRestart == "ignore") { inst.StepCount++; inst.Last = ev.Type + ":" + ev.Phase; Put(d, "step", inst); continue; }
                    Close(d.Id, "restart", now);
                }
                long n = _counter[d.Id] = (_counter.TryGetValue(d.Id, out var cn) ? cn : 0) + 1;
                var ni = new ActionInstance { Def = d, N = n, Started = now, StepCount = 0, StartSeq = ev.Seq, Last = ev.Type + ":" + ev.Phase, OvertimeMs = d.OvertimeMs };
                if (d.OvertimeExpr != null)
                {
                    double ov = Vars != null ? Expr.ToNum(d.OvertimeExpr.Eval(Vars())) : double.NaN;
                    ni.OvertimeMs = double.IsNaN(ov) || ov <= 0 ? 0 : (int)Math.Round(ov);
                }
                _open[d.Id] = ni; Bump(Started, d.Id);
                Put(d, "start", ni);
                continue;
            }
            if (isStep)
            {
                if (inst != null) { inst.StepCount++; Put(d, "step", inst); inst.Last = ev.Type + ":" + ev.Phase; }
                else { Bump(Orphans, d.Id + "<-" + ev.Type + ":" + ev.Phase); Put(d, "orphan", null); }
            }
            else if (isEnd || isInt) { Bump(Orphans, d.Id + "<-" + ev.Type + ":" + ev.Phase); Put(d, "orphan", null); }
        }
        if (add == null) return ev;
        if (first != null) add["act"] = first;
        var ctx = new Dictionary<string, string>(ev.Ctx);
        foreach (var kv in add) ctx[kv.Key] = kv.Value;
        return ev with { Ctx = ctx };
    }

    public string Summary()
    {
        static string D(Dictionary<string, int> d) => d.Count == 0 ? "0" : string.Join(",", d.Select(kv => $"{kv.Key}={kv.Value}"));
        var dur = string.Join(" ", Durations.Select(kv => $"{kv.Key}~{kv.Value.OrderBy(x => x).ElementAt(kv.Value.Count / 2)}ms"));
        return $"started={D(Started)} completed={D(Completed)} interrupted={D(Interrupted)} timed_out={D(TimedOut)} restarted={D(Restarted)} " +
               $"orphans={D(Orphans)} closed_by_system={D(SystemClosed)} median_duration: {(dur.Length == 0 ? "-" : dur)}";
    }
}
