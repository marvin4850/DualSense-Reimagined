using System.Text;
using System.Text.RegularExpressions;

namespace DsBridge;

/// <summary>One game event, as decoded from the window title.
/// Logical model: seq, type, phase, timestamp (game ms), source hook, context key/values.</summary>
public sealed record GameEvent(long Seq, string Type, string Phase, long? GameMs, string Src,
                               IReadOnlyDictionary<string, string> Ctx, bool Legacy, string Raw)
{
    /// <summary>Effect key understood by the `run` effect table (v1 event names); null = diagnostics only.</summary>
    public string? EffectKey { get; init; }
    /// <summary>v1 id ("counter-ms[-extra]"), empty for v2.</summary>
    public string LegacyId { get; init; } = "";

    public string? Get(string key) => Ctx.TryGetValue(key, out var v) ? v : null;
    public int? GetInt(string key) => Ctx.TryGetValue(key, out var v) && int.TryParse(v, out var i) ? i : null;

    public override string ToString()
    {
        var ctx = string.Join(" ", Ctx.Where(kv => kv.Key != "t" && kv.Key != "src").Select(kv => $"{kv.Key}={kv.Value}"));
        return $"#{Seq} {Type}:{Phase} src={Src}{(ctx.Length > 0 ? " " + ctx : "")}{(EffectKey != null ? $" -> effect '{EffectKey}'" : "")}{(Legacy ? " [v1]" : "")}";
    }
}

/// <summary>Decodes the mod's window-title channel. Supports
///  v2: "[G1RDS2|seq:type:phase:k=v,k=v|...]" (ring of the last records, newest last) and
///  v1: "[G1RDS:event:id]" (single marker, via the old EventParser).
/// Guarantees: each v2 record is delivered once, in seq order; records already present when the
/// companion attached are skipped (baseline); gaps (lost records) and session restarts are detected.</summary>
public sealed class EventMonitor
{
    static readonly Regex RxV2 = new(@"\[G1RDS2\|(?<body>[^\]]*)\]");
    static readonly Regex RxRec = new(@"^(?<seq>\d+):(?<type>[a-z0-9_]+):(?<phase>[a-z]+):(?<kv>[A-Za-z0-9_=,\-]*)$");

    readonly EventParser _v1 = new();
    bool _baselineTaken;
    long _lastSeq;
    string? _lastRaw;

    public string? BaselineMode { get; set; }      // "world"/"menu" from the ring at attach time
    public Action<string>? Diag;                    // diagnostics sink (null = off, near-zero cost)
    public long Received, Gaps, Lost, Resets, Malformed, DuplicatesDropped;
    string? _lastBody;   // record without its seq: a second identical record right after it = the same game event delivered twice

    public void Reset() { _v1.Reset(); _baselineTaken = false; _lastSeq = 0; _lastRaw = null; _lastBody = null; BaselineMode = null; }

    public IEnumerable<GameEvent> Feed(string title, DateTime now)
    {
        var m = RxV2.Match(title);
        if (!m.Success)
        {
            if (title.Contains("[G1RDS:"))
            {
                var list = new List<GameEvent>();
                foreach (var e in _v1.Feed(title, now))
                {
                    Received++;
                    list.Add(new GameEvent(0, e.Name, "start", null, "v1", new Dictionary<string, string>(), true, e.Name + ":" + e.Id)
                        { EffectKey = e.Name, LegacyId = e.Id });
                }
                if (_v1.BaselineEvent != null) { BaselineMode = _v1.BaselineEvent == "menu" ? "menu" : "world"; _v1.BaselineEvent = null; }
                return list;
            }
            _baselineTaken = true;   // plain title: everything that appears later is new
            foreach (var _ in _v1.Feed(title, now)) { }   // same rule for the v1 parser
            return Array.Empty<GameEvent>();
        }

        var recs = new List<GameEvent>();
        foreach (var part in m.Groups["body"].Value.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var ev = ParseRecord(part);
            if (ev == null) { Malformed++; Diag?.Invoke($"MALFORMED record '{part}'"); continue; }
            recs.Add(ev);
        }
        if (recs.Count == 0) return Array.Empty<GameEvent>();
        recs.Sort((a, b) => a.Seq.CompareTo(b.Seq));

        if (!_baselineTaken)
        {
            _baselineTaken = true;
            var last = recs[^1]; _lastSeq = last.Seq; _lastRaw = last.Raw;
            var mode = recs.LastOrDefault(r => r.Type == "mode");
            if (mode != null) BaselineMode = mode.Get("m");
            Diag?.Invoke($"BASELINE: ring #{recs[0].Seq}..#{last.Seq} was already in the title at attach -> skipped (mode={BaselineMode ?? "unknown"})");
            return Array.Empty<GameEvent>();
        }

        // new session (ability re-activated after a load): seq went back, or our last seq now holds a different record
        var same = recs.FirstOrDefault(r => r.Seq == _lastSeq);
        if (recs[^1].Seq < _lastSeq || (same != null && same.Raw != _lastRaw))
        {
            Resets++;
            Diag?.Invoke($"SESSION RESET: seq restarted (last seen #{_lastSeq}, ring now #{recs[0].Seq}..#{recs[^1].Seq})");
            _lastSeq = 0; _lastRaw = null;
        }

        var outList = new List<GameEvent>();
        foreach (var r in recs)
        {
            if (r.Seq <= _lastSeq) continue;   // still in the ring from an earlier poll (normal)
            if (r.Seq > _lastSeq + 1)
            {
                long lost = r.Seq - _lastSeq - 1;
                Gaps++; Lost += lost;
                Diag?.Invoke($"GAP: {lost} record(s) lost between #{_lastSeq} and #{r.Seq} (more than 4 events between two title polls)");
            }
            _lastSeq = r.Seq; _lastRaw = r.Raw;
            var body = r.Raw[(r.Raw.IndexOf(':') + 1)..];
            if (body == _lastBody)
            {
                DuplicatesDropped++;
                Diag?.Invoke($"DUPLICATE: #{r.Seq} repeats #{r.Seq - 1} exactly (same type/phase/game time/source) -> dropped");
                continue;
            }
            _lastBody = body;
            Received++;
            outList.Add(r);
        }
        return outList;
    }

    public static GameEvent? ParseRecord(string raw)
    {
        var m = RxRec.Match(raw);
        if (!m.Success) return null;
        var ctx = new Dictionary<string, string>();
        foreach (var kv in m.Groups["kv"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            int i = kv.IndexOf('=');
            if (i <= 0) return null;
            ctx[kv[..i]] = kv[(i + 1)..];
        }
        long? ms = ctx.TryGetValue("t", out var t) && long.TryParse(t, out var tl) ? tl : null;
        var type = m.Groups["type"].Value; var phase = m.Groups["phase"].Value;
        var ev = new GameEvent(long.Parse(m.Groups["seq"].Value), type, phase, ms, ctx.GetValueOrDefault("src", "?"), ctx, false, raw);
        return ev with { EffectKey = EffectKeyFor(type, phase, ctx) };
    }

    /// <summary>Maps v2 events onto the existing effect table (compatibility with the v1 event names).</summary>
    public static string? EffectKeyFor(string type, string phase, IReadOnlyDictionary<string, string> ctx) => (type, phase) switch
    {
        ("session", "start") => "ready",
        ("mode", "update") => ctx.GetValueOrDefault("m") switch { "world" => "world", "menu" => "menu", _ => null },
        ("air", "start") => "fall",
        ("air", "complete") => "land",
        ("attack", "start") => ctx.GetValueOrDefault("dir") switch
        {
            "top" => "atop", "bottom" => "abottom", "left" => "aleft", "right" => "aright", "kick" => "akick", _ => "attack"
        },
        ("dodge", "start") => "dodge",
        ("roll", "start") => "roll",
        ("hitreact", "start") => "hit",
        ("dive", "start") => "dive",
        ("dive", "complete") => "surface",
        ("swim", "start") => ctx.GetValueOrDefault("entry") == "fall" ? "splash" : "wade",
        _ => null   // attack:update/interrupt/complete, hurt, parry, weapon, ... -> diagnostics only for now
    };
}

/// <summary>Tracks every state-like event type (a "start" followed by "complete"/"cancel") so none stays "active" after a
/// menu, a lost game, a session restart or a missing end record, and counts state errors.
/// Per-type behaviour comes from the registry (Configure): restartable (a new start while active is normal, e.g. each swing of
/// one attack), staleMs (close as stale after that long; 0 = never). Types not in the registry: not restartable, never stale.</summary>
public sealed class StateTracker
{
    readonly Dictionary<string, (long seq, DateTime at)> _active = new();
    readonly Dictionary<string, (bool restartable, int staleMs)> _cfg = new()
    {   // defaults = the hand-written mod events (registry overrides them)
        ["air"] = (false, 15000), ["attack"] = (true, 15000), ["dodge"] = (false, 15000), ["roll"] = (false, 15000),
        ["hitreact"] = (false, 15000), ["swim"] = (false, 0), ["dive"] = (false, 0)
    };
    public Action<string>? Diag;
    public int ZeroMs = 5;
    // state error counters (per type)
    public readonly Dictionary<string, int> DoubleStart = new(), EndWithoutStart = new(), ZeroDuration = new(), Stale = new(), Interrupted = new();

    public IReadOnlyCollection<string> Active => _active.Keys;

    readonly Dictionary<string, List<string>> _endedBy = new() { ["air"] = new() { "swim:start" } };   // "type:phase" that also end a state

    /// <summary>Types that are instant events (registry phases without complete/cancel): never tracked as states.</summary>
    public readonly HashSet<string> Instant = new();

    public void Configure(IEnumerable<(string type, bool restartable, int staleMs, IEnumerable<string> endedBy)> types)
    {
        foreach (var (t, r, ms, eb) in types) { _cfg[t] = (r, ms); var l = eb.ToList(); if (l.Count > 0) _endedBy[t] = l; }
    }

    static void Bump(Dictionary<string, int> d, string k) => d[k] = d.TryGetValue(k, out var v) ? v + 1 : 1;

    public void Apply(GameEvent ev, DateTime now)
    {
        var key = ev.Type + ":" + ev.Phase;
        foreach (var (st, ends) in _endedBy)
            if (st != ev.Type && ends.Contains(key) && _active.ContainsKey(st)) _active.Remove(st);   // e.g. a jump that ends in the water
        if (Instant.Contains(ev.Type)) return;
        var c = _cfg.TryGetValue(ev.Type, out var x) ? x : (false, 0);
        switch (ev.Phase)
        {
            case "start":
                if (_active.ContainsKey(ev.Type) && !c.Item1) { Bump(DoubleStart, ev.Type); Diag?.Invoke($"STATE: '{ev.Type}' start #{ev.Seq} while already active since #{_active[ev.Type].seq}"); }
                if (!_active.ContainsKey(ev.Type) || !c.Item1) _active[ev.Type] = (ev.Seq, now);
                break;
            case "complete":
            case "cancel":
                if (_active.TryGetValue(ev.Type, out var a))
                {
                    if ((now - a.at).TotalMilliseconds < ZeroMs) { Bump(ZeroDuration, ev.Type); Diag?.Invoke($"STATE: '{ev.Type}' #{a.seq}..#{ev.Seq} lasted < {ZeroMs} ms (blip)"); }
                    _active.Remove(ev.Type);
                }
                else if (_cfg.ContainsKey(ev.Type) || ev.Src.StartsWith("gen_"))
                {
                    Bump(EndWithoutStart, ev.Type); Diag?.Invoke($"STATE: '{ev.Type}' {ev.Phase} without an active start (#{ev.Seq})");
                }
                break;
        }
    }

    public void InterruptAll(string reason)
    {
        foreach (var k in _active.Keys.ToList()) { Bump(Interrupted, k); Diag?.Invoke($"STATE: '{k}' (started #{_active[k].seq}) closed by companion: {reason}"); }
        _active.Clear();
    }

    public void ExpireStale(DateTime now)
    {
        foreach (var k in _active.Keys.ToList())
        {
            int ms = _cfg.TryGetValue(k, out var x) ? x.staleMs : 0;
            if (ms <= 0 || (now - _active[k].at).TotalMilliseconds <= ms) continue;
            Bump(Stale, k);
            Diag?.Invoke($"STATE: '{k}' (started #{_active[k].seq}) still active after {ms / 1000.0:F0} s -> closed as stale");
            _active.Remove(k);
        }
    }

    public string Summary()
    {
        static string D(Dictionary<string, int> d) => d.Count == 0 ? "0" : string.Join(",", d.Select(kv => $"{kv.Key}={kv.Value}"));
        return $"double_start={D(DoubleStart)} end_without_start={D(EndWithoutStart)} zero_duration={D(ZeroDuration)} stale={D(Stale)} closed_by_companion={D(Interrupted)}";
    }
}

/// <summary>Size-capped diagnostics log (console + file, one rotation to .1).</summary>
public sealed class DiagLog : IDisposable
{
    readonly string? _path; readonly long _maxBytes; StreamWriter? _w; readonly object _l = new();
    public DiagLog(string? path, long maxBytes = 2 * 1024 * 1024)
    {
        _path = path; _maxBytes = maxBytes;
        if (_path != null) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!); Open(); }
    }
    void Open() { _w = new StreamWriter(new FileStream(_path!, FileMode.Append, FileAccess.Write, FileShare.Read), Encoding.UTF8) { AutoFlush = true }; }
    public void Write(string line)
    {
        var s = $"[{DateTime.Now:HH:mm:ss.fff}] {line}";
        lock (_l)
        {
            Console.WriteLine(s);
            if (_w == null) return;
            try
            {
                _w.WriteLine(s);
                if (_w.BaseStream.Length > _maxBytes)
                {
                    _w.Dispose();
                    var old = _path + ".1";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(_path!, old);
                    Open();
                }
            }
            catch (Exception ex) { Console.WriteLine($"diag log write failed: {ex.Message}"); _w = null; }
        }
    }
    public void Dispose() { lock (_l) { _w?.Dispose(); _w = null; } }
}
