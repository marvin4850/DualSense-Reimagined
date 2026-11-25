namespace DsBridge.Haptics;

public interface IHdOut
{
    bool Ready { get; }
    int Rate { get; }
    bool Active { get; }
    long Play(float[] l, float[] r, VoiceOpts o);
    void Stop(long id, int fadeMs);
    void StopAll();
}

public interface IMotorOut
{
    void Pattern((int delay, byte right, byte left, int ms)[] steps);
    void StopAll();
}

/// <summary>Adaptive triggers. null profile = leave that trigger unchanged. The implementation keeps the state and
/// re-sends it (the game or a reconnect may overwrite it); Reset turns both off.</summary>
public interface IPadOut
{
    void Triggers(TriggerProfile? l2, TriggerProfile? r2);
    void Reset(string reason);
    /// <summary>Both triggers play this profile for ms, then the current state (Triggers) returns. Never blocks.</summary>
    void Pulse(TriggerProfile pulse, int ms, bool left = true, bool right = true, int delayMs = 0, bool hold = false);
    /// <summary>The finger let go of these triggers: a trigger parked after a pulse gets the current state back.</summary>
    void Release(bool left, bool right);
}

/// <summary>Lightbar (r, g, b) and player LEDs (count 0-5); null = untouched. Reset = idle look (lightbar back, LEDs off).</summary>
public interface ILightOut
{
    void Light(byte[]? rgb, int? leds);
    void Reset(string reason);
}

/// <summary>What the runtime did with one event (for logs and replay reports).</summary>
public sealed record Decision(string RuleId, string Route, string Detail)
{
    public override string ToString() => $"{RuleId} -> {Route}{(Detail.Length > 0 ? " (" + Detail + ")" : "")}";
}

/// <summary>Event -> rule -> effect. Rules are data (haptics/packs/*.json); nothing here is event-specific.
/// System policy (not configurable): `mode m=menu` and `session start` stop everything; continuous effects stop on their
/// rule's stopOn phases, on InterruptContinuous (companion state tracker) and at maxMs.</summary>
public sealed class HapticRuntime
{
    readonly HapticConfig _cfg;
    public IHdOut? Hd; public IMotorOut Motor; public Action<string>? Log;
    /// <summary>Adaptive triggers (null in replay / rules-check: pad decisions are still reported).</summary>
    public IPadOut? Pad;
    /// <summary>Lightbar + player LEDs (null in replay / rules-check: light decisions are still reported).</summary>
    public ILightOut? LightOut;
    sealed record LightChannel(string Owner, byte[]? Rgb, int? Leds, int Priority, int TimeoutMs, DateTime Last);
    readonly Dictionary<string, LightChannel> _lightChannels = new();
    byte[]? _lightRgb; int? _lightLeds; public bool LightActive => _lightRgb != null || _lightLeds != null;
    public string LightState => $"bar {(_lightRgb == null ? "-" : string.Join(",", _lightRgb))}, leds {(_lightLeds?.ToString() ?? "-")}";
    // trigger state as the runtime set it: owner event type (watchdog), last event of that type, current profiles
    string? _padOwner; DateTime _padLast; TriggerProfile? _padL2, _padR2;
    public bool PadActive => (_padL2 != null && !_padL2.IsOff) || (_padR2 != null && !_padR2.IsOff);
    public string PadState => $"L2 {_padL2?.ToString() ?? "-"}, R2 {_padR2?.ToString() ?? "-"}";
    public int PadResets;
    readonly Dictionary<string, DateTime> _lastFire = new();
    readonly Dictionary<string, (long voice, Rule rule)> _continuous = new();
    readonly Dictionary<string, (Rule rule, List<long> voices)> _cancelable = new();   // rule id -> its voices (rules with "cancel")
    readonly Dictionary<string, DateTime> _continuousStart = new();
    readonly Dictionary<string, GameEvent> _continuousEvent = new();
    readonly Dictionary<string, (Rule rule, GameEvent ev)> _suspended = new();   // continuous effects paused by the menu
    public int Resumed;
    /// <summary>Context: the latest fields of "update" events of context/system types (e.g. last_weapon_fam, last_mode_m),
    /// available to every rule's where/when/let - lets one event's effect depend on the current game context.</summary>
    readonly Dictionary<string, object?> _ctxVars = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, object?> Context => _ctxVars;
    readonly Dictionary<string, DateTime> _ctxAt = new(StringComparer.Ordinal);   // "<type>_<field>" -> when the value last changed
    readonly Dictionary<string, double> _ctxHeld = new(StringComparer.Ordinal);   // "<type>_<field>" -> ms the PREVIOUS value lasted (held_...)
    /// <summary>Context variables plus age_&lt;type&gt;_&lt;field&gt; = ms since that context value last changed.</summary>
    Dictionary<string, object?> Vars()
    {
        var d = new Dictionary<string, object?>(_ctxVars, StringComparer.Ordinal);
        foreach (var (k, at) in _ctxAt) d["age_" + k] = (_now - at).TotalMilliseconds;
        foreach (var (k, ms) in _ctxHeld) d["held_" + k] = ms;
        return d;
    }
    DateTime _now;
    public readonly Dictionary<string, int> RuleHits = new(), Unmatched = new(), CooldownDrops = new(), Routes = new(), SituationDrops = new();
    readonly Dictionary<string, (DateTime at, int prio, string rule)> _situations = new();
    public int Errors; public readonly List<string> ErrorList = new();
    /// <summary>The last handled event with action annotations (for logs).</summary>
    public GameEvent? LastEvent;
    const int DefaultRate = 48000;

    public readonly Correlator Actions;
    readonly VariationState _variations;
    public readonly Dictionary<string,int> LastVariations = new();

    public HapticRuntime(HapticConfig cfg, IHdOut? hd, IMotorOut motor, VariationState? variations=null)
    {
        _cfg = cfg; Hd = hd; Motor = motor; _variations=variations??new VariationState();
        Actions = cfg.ActionsPath != null ? Correlator.Load(cfg.ActionsPath, new List<string>()) : new Correlator();   // own instance per runtime
        foreach (var pp in cfg.PackActionPaths) Actions.Defs.AddRange(Correlator.Load(pp, new List<string>()).Defs);   // actions of enabled packs
        Actions.Vars = () => { var d = Vars(); return name => d.TryGetValue(name, out var v) ? v : null; };
        Actions.Closed += (id, how) =>
        {
            foreach (var key in _continuous.Where(kv => kv.Value.rule.UntilAction == id).Select(kv => kv.Key).ToList())
            {
                Hd?.Stop(_continuous[key].voice, 120); _continuous.Remove(key);
                Log?.Invoke($"continuous '{key}' stopped: action '{id}' closed ({how})");
            }
        };
    }

    public IEnumerable<string> ActiveContinuous => _continuous.Select(kv => $"{kv.Value.rule.Id}({kv.Key})");

    /// <summary>Delivery clock (universal master plan 6, "часы"): d = host ms - game ms of each game event; the floor is the
    /// smallest d of the last 2 s of host time, so d - floor = how much later than the best case this event arrived (frame
    /// position of the emit + title poll; journals 19.11: p50 11, p95 26, p99 37 ms). A jump of the game clock (pause, load,
    /// new session) restarts the floor; companion-side events (hid_*) carry no game time.</summary>
    public sealed class LateClock
    {
        readonly Queue<(double host, double d)> _w = new();
        public const double WindowMs = 2000, JumpMs = 150;
        public double LastLateMs { get; private set; }
        public double Observe(double hostMs, double gameMs)
        {
            double d = hostMs - gameMs;
            while (_w.Count > 0 && hostMs - _w.Peek().host > WindowMs) _w.Dequeue();
            double floor = _w.Count > 0 ? _w.Min(x => x.d) : d;
            if (d - floor > JumpMs || d < floor - JumpMs) { _w.Clear(); floor = d; }   // game clock jumped: new segment
            _w.Enqueue((hostMs, d));
            return LastLateMs = Math.Max(0, d - Math.Min(floor, d));
        }
    }
    public readonly LateClock Clock = new();
    double _lateTrimMs;          // for the event being handled: ms its HD voices may start earlier (leading silence only)
    public int LateTrims; public double LateTrimmedMs;

    int _depth;
    public List<Decision> Handle(GameEvent ev, DateTime now)
    {
        // derived / correlated events are handled inside their game event's Handle: they keep its lateness
        bool top = _depth++ == 0; double saved = _lateTrimMs;
        try
        {
            if (top)
            {
                _lateTrimMs = 0;
                if (ev.GameMs is long gms && gms > 0 && !ev.Src.StartsWith("hid_") && !ev.Src.StartsWith("derived"))
                {
                    double late = Clock.Observe((now - DateTime.UnixEpoch).TotalMilliseconds, gms);
                    if (_cfg.Limits.LateTrimMaxMs > 0) _lateTrimMs = Math.Clamp(late - _cfg.Limits.LateTrimTargetMs, 0, _cfg.Limits.LateTrimMaxMs);
                }
            }
            return HandleCore(ev, now);
        }
        finally { _depth--; if (!top) _lateTrimMs = saved; }
    }

    List<Decision> HandleCore(GameEvent ev, DateTime now)
    {
        var res = new List<Decision>();
        if(ev.Type=="montage"&&ev.Phase=="start")_variations.BeginMontage(ev.Get("m")??"");
        Tick(now);
        if (ev.Type == "mode" && ev.Get("m") == "menu")
        {
            // menu: silence everything, but remember continuous effects whose game state is still running
            foreach (var (k, v) in _continuous) if (_continuousEvent.TryGetValue(k, out var cev)) _suspended[k] = (v.rule, cev);
            _continuous.Clear();
            StopAll("menu");
            Actions.CloseAll("menu", now);
            res.Add(new Decision("system", "stop-all", "menu" + (_suspended.Count > 0 ? $", suspended {_suspended.Count}" : "")));
        }
        else if (ev.Type == "session" && ev.Phase == "start")
        {
            _suspended.Clear();
            StopAll("new session");
            Actions.CloseAll("session", now);
            res.Add(new Decision("system", "stop-all", "new session"));
        }
        else if (ev.Type == "mode" && ev.Get("m") == "world" && _suspended.Count > 0)
        {
            foreach (var (k, s) in _suspended.ToList())
            {
                var vars = new Dictionary<string, object?>(); var look = HapticConfig.VarsFor(s.ev, vars);
                try { foreach (var (name, expr) in s.rule.Let) vars[name] = expr.Eval(look); var d = Play(s.rule, s.ev, look); res.Add(d with { Route = "resume:" + d.Route }); Resumed++; }
                catch (Exception ex) { Fail(s.rule, ex); }
            }
            _suspended.Clear();
        }
        if (_suspended.TryGetValue(ev.Type, out var sus) && sus.rule.StopOn.Contains(ev.Phase)) _suspended.Remove(ev.Type);   // ended during the menu
        foreach (var (key, value) in _lightChannels.ToArray())
            if (value.Owner == ev.Type) _lightChannels[key] = value with { Last = now };
        if (_padOwner == ev.Type) _padLast = now;   // trigger watchdog: the owner is still talking
        if (ev.Type == "montage") ev = _cfg.Montages.Annotate(ev);   // mc (class), mw (weapon), md (dir), mstage, mskill, mconf
        ev = Actions.Annotate(ev, now);
        LastEvent = ev;
        if (ev.Phase == "update" && _cfg.Registry.TryGetValue(ev.Type, out var regE) && regE.Category is "context" or "system")
            foreach (var (k, v) in ev.Ctx)
                if (k is not ("t" or "src"))
                {
                    _ctxVars[$"prev_{ev.Type}_{k}"] = _ctxVars.TryGetValue($"last_{ev.Type}_{k}", out var pv) ? pv : null;
                    object nv = double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dv) ? dv : v;
                    if (pv == null || !Equals(pv, nv))
                    {
                        var ck = $"{ev.Type}_{k}";
                        if (_ctxAt.TryGetValue(ck, out var was)) _ctxHeld[ck] = (now - was).TotalMilliseconds;
                        _ctxAt[ck] = now;
                    }
                    _ctxVars[$"last_{ev.Type}_{k}"] = nv;
                }
        if (_continuous.TryGetValue(ev.Type, out var c) && c.rule.StopOn.Contains(ev.Phase))
        {
            Hd?.Stop(c.voice, c.rule.StopOnFadeMs); _continuous.Remove(ev.Type);
            res.Add(new Decision(c.rule.Id, "stop", ev.Phase));
        }
        if (_cancelable.Count > 0)
        {
            string tp = ev.Type + ":" + ev.Phase;
            foreach (var (rid, entry) in _cancelable.ToList())
            {
                if (!entry.rule.CancelWhen.Contains(tp)) continue;
                if (entry.rule.CancelIf != null && Expr.ToNum(entry.rule.CancelIf.Eval(HapticConfig.VarsFor(ev, Vars()))) is var cv && (double.IsNaN(cv) || cv == 0)) continue;
                foreach (var voice in entry.voices) Hd?.Stop(voice, entry.rule.CancelFadeMs);
                _cancelable.Remove(rid);
                res.Add(new Decision(rid, "cancel", tp));
            }
        }
        foreach (var key in _continuous.Where(kv => kv.Value.rule.StopWhen.Contains(ev.Type + ":" + ev.Phase) &&
            (kv.Value.rule.StopIf == null || Expr.ToNum(kv.Value.rule.StopIf.Eval(HapticConfig.VarsFor(ev, Vars()))) != 0)).Select(kv => kv.Key).ToList())
        {
            Hd?.Stop(_continuous[key].voice, _continuous[key].rule.StopFadeMs); res.Add(new Decision(_continuous[key].rule.Id, "stop", ev.Type + ":" + ev.Phase)); _continuous.Remove(key);
        }

        bool matched = false;
        foreach (var rule in _cfg.Rules)
        {
            if (!rule.Matches(ev)) continue;
            var vars = Vars();
            var look = HapticConfig.VarsFor(ev, vars);
            try { foreach (var (name, expr) in rule.Let) vars[name] = expr.Eval(look); }
            catch (Exception ex) { Fail(rule, ex); continue; }
            if (rule.When != null && Expr.ToNum(rule.When.Eval(look)) is var w && (double.IsNaN(w) || w == 0)) continue;
            matched = true;
            if (rule.CooldownMs > 0 && _lastFire.TryGetValue(rule.Id, out var lt) && (now - lt).TotalMilliseconds < rule.CooldownMs)
            {
                Bump(CooldownDrops, rule.Id);
                res.Add(new Decision(rule.Id, "cooldown", $"< {rule.CooldownMs} ms"));
                if (!rule.Also) break; else continue;
            }
            if (rule.Situation.Length > 0 && _situations.TryGetValue(rule.Situation, out var sit) && (now - sit.at).TotalMilliseconds < rule.SituationMs && rule.Priority <= sit.prio)
            {
                Bump(SituationDrops, rule.Id);
                res.Add(new Decision(rule.Id, "situation", $"{rule.Situation} already played by {sit.rule} {(now - sit.at).TotalMilliseconds:0} ms ago"));
                if (!rule.Also) break; else continue;
            }
            if (rule.Situation.Length > 0 && !rule.SituationCheckOnly) _situations[rule.Situation] = (now, rule.Priority, rule.Id);
            _lastFire[rule.Id] = now;
            if(rule.Variation!=null){int index=_variations.Next(rule.Variation,ev);vars["variation_index"]=(double)index;LastVariations[rule.Id]=index;}
            try { var d = Play(rule, ev, look); res.Add(d); Bump(RuleHits, rule.Id); Bump(Routes, d.Route); }
            catch (Exception ex) { Fail(rule, ex); res.Add(new Decision(rule.Id, "error", ex.Message)); }
            if (!rule.Also) break;
        }
        if (!matched) Bump(Unmatched, ev.Type + ":" + ev.Phase);
        // derived (edge) events from this event, then handled like any game event
        if (!ev.Src.StartsWith("derived"))
            foreach (var (dtype, from, start, complete, update, fields) in _cfg.Derived)
            {
                if (from != ev.Type + ":" + ev.Phase && from != ev.Type) continue;
                var look = HapticConfig.VarsFor(ev, Vars());
                foreach (var (ph, f) in new[] { ("start", start), ("complete", complete), ("update", update) })
                {
                    if (f == null || Expr.ToNum(f.Eval(look)) is var x && (double.IsNaN(x) || x == 0)) continue;
                    var kv = string.Concat(ev.Ctx.Where(c => c.Key is not ("t" or "src") && !c.Key.StartsWith("act")).Select(c => $",{c.Key}={c.Value}"));
                    foreach (var (fname, fx) in fields)
                    {
                        var fv = fx.Eval(look);
                        kv += "," + fname + "=" + (fv is double fd ? Math.Round(fd).ToString(System.Globalization.CultureInfo.InvariantCulture) : fv?.ToString() ?? "");
                    }
                    var dev = EventMonitor.ParseRecord($"{ev.Seq}:{dtype}:{ph}:t={ev.GameMs ?? 0},src=derived_{ev.Type}{kv}");
                    if (dev != null) { Bump(DerivedRaised, dtype + ":" + ph); res.AddRange(Handle(dev, now)); }
                }
            }
        return res;
    }

    void Fail(Rule rule, Exception ex)
    {
        Errors++;
        var m = $"{rule.Pack}/{rule.Id}: {ex.GetType().Name}: {ex.Message}";
        if (ErrorList.Count < 50) ErrorList.Add(m);
        Log?.Invoke("RULE ERROR " + m);
    }

    static void Bump(Dictionary<string, int> d, string k) => d[k] = d.TryGetValue(k, out var v) ? v + 1 : 1;

    Decision Play(Rule rule, GameEvent ev, Expr.Lookup look)
    {
        string padDetail = rule.Pad != null ? ApplyPad(rule, ev, look) : "";
        if (rule.Light != null) padDetail = (padDetail.Length > 0 ? padDetail + "; " : "") + ApplyLight(rule, ev, look);
        if (rule.Effect.Layers.Count == 0) return new Decision(rule.Id, "pad", padDetail);
        var hd = PlayHaptic(rule, ev, look);
        return padDetail.Length > 0 ? hd with { Detail = hd.Detail + "; pad " + padDetail } : hd;
    }

    /// <summary>Applies a rule's adaptive-trigger output; the rule's event type becomes the owner (watchdog).</summary>
    string ApplyPad(Rule rule, GameEvent ev, Expr.Lookup look)
    {
        var spec = rule.Pad!;
        if (spec.Reset) { ResetPad($"rule {rule.Id}"); return "reset"; }
        TriggerProfile? Prof(Param? p, string side)
        {
            if (p == null) return null;
            var v = p.Resolve(look);
            string name = v switch { string s => s, double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture), _ => "" };
            if (_cfg.TriggerProfiles.TryGetValue(name, out var tp)) return tp;
            throw new InvalidOperationException($"pad.{side}: no trigger profile '{name}'");
        }
        string pulseText = "";
        if (spec.Release != null)
        {
            string rs = spec.Release.Resolve(look) switch { string s => s, _ => "both" };
            bool rl = rs is not ("R" or "r"), rr = rs is not ("L" or "l");
            Pad?.Release(rl, rr);
            pulseText = "release " + (rl && rr ? "L2+R2" : (rl ? "L2" : "R2"));
            if (spec.Pulse == null && spec.L2 == null && spec.R2 == null) return pulseText;
        }
        if (spec.Pulse != null)
        {
            var pp = Prof(spec.Pulse, "pulse")!;
            string side = "both";
            if (spec.PulseSide != null)
                side = spec.PulseSide.Resolve(look) switch { string s => s, _ => "both" };
            bool pl = side is not ("R" or "r"), pr = side is not ("L" or "l");
            Pad?.Pulse(pp, spec.PulseMs, pl, pr, spec.PulseDelayMs, spec.PulseHold);
            string sideText = pl && pr ? "L2+R2" : (pl ? "L2" : "R2");
            pulseText = $"pulse {sideText} {pp} {spec.PulseMs} ms" + (spec.PulseDelayMs > 0 ? $" after {spec.PulseDelayMs} ms" : "") + (spec.PulseHold ? ", held" : "");
            if (spec.L2 == null && spec.R2 == null) return pulseText;
        }
        var l = Prof(spec.L2, "l2"); var r = Prof(spec.R2, "r2");
        if (l != null) _padL2 = l;
        if (r != null) _padR2 = r;
        _padOwner = ev.Type; _padLast = _now;
        Pad?.Triggers(l, r);
        return PadState;
    }

    /// <summary>Both triggers off and the default light channel released. StopAll also releases independent light channels.</summary>
    public void ResetPad(string reason)
    {
        bool was = PadActive;
        _padL2 = _padR2 = null; _padOwner = null;
        Pad?.Reset(reason);
        if (was) { PadResets++; Log?.Invoke($"triggers reset ({reason})"); }
        ResetLightChannel("default", reason);
    }

    /// <summary>The pad is gone (unplugged / BT dropped): a new connection epoch. Every voice, trigger owner and light channel
    /// of the old epoch is released here, so nothing held is re-sent to the reconnected pad without a fresh game event
    /// (universal master plan 6: "удержание без свежего подтверждения не возобновляется"). Continuous effects come back with
    /// their next game event, menu-suspended ones are dropped.</summary>
    public int DeviceLosses;
    public void DeviceLost(string reason)
    {
        DeviceLosses++;
        _suspended.Clear();
        StopAll(reason);
    }

    public void ResetLight(string reason)
    {
        _lightChannels.Clear();
        ComposeLight(reason);
    }

    void ResetLightChannel(string channel, string reason)
    {
        _lightChannels.Remove(channel);
        ComposeLight(reason);
    }

    void ComposeLight(string reason)
    {
        var ordered = _lightChannels.Values.OrderByDescending(x => x.Priority).ThenByDescending(x => x.Last).ToArray();
        byte[]? rgb = ordered.FirstOrDefault(x => x.Rgb != null)?.Rgb;
        int? leds = ordered.FirstOrDefault(x => x.Leds != null)?.Leds;
        bool sameRgb = rgb == null ? _lightRgb == null : _lightRgb != null && rgb.SequenceEqual(_lightRgb);
        if (sameRgb && leds == _lightLeds) return;
        bool removed = (rgb == null && _lightRgb != null) || (leds == null && _lightLeds != null);
        _lightRgb = rgb; _lightLeds = leds;
        // Reset any field whose final channel disappeared, then reapply surviving channels.
        if (removed) LightOut?.Reset(reason);
        if (rgb != null || leds != null) LightOut?.Light(rgb, leds);
        Log?.Invoke($"light {LightState} ({reason})");
    }

    static readonly Dictionary<string, byte[]> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["white"] = new byte[] { 255, 255, 255 }, ["red"] = new byte[] { 255, 0, 0 }, ["green"] = new byte[] { 0, 255, 0 },
        ["blue"] = new byte[] { 0, 0, 255 }, ["off"] = new byte[] { 0, 0, 0 },
    };

    /// <summary>A rule's light channel has its own owner, priority and watchdog, independent of adaptive triggers.</summary>
    string ApplyLight(Rule rule, GameEvent ev, Expr.Lookup look)
    {
        var spec = rule.Light!;
        if (spec.Reset) { ResetLightChannel(spec.Channel, $"rule {rule.Id}"); return "light reset"; }
        byte[]? rgb = null; int? leds = null;
        if (spec.Color != null)
        {
            string c = spec.Color.Resolve(look) switch { string s => s, double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture), _ => "" };
            if (NamedColors.TryGetValue(c.Trim(), out var named)) rgb = named;
            else
            {
                var parts = c.Split(',');
                if (parts.Length != 3 || !parts.All(x => byte.TryParse(x.Trim(), out _)))
                    throw new InvalidOperationException($"light.color: '{c}' is not a colour name or R,G,B");
                rgb = parts.Select(x => byte.Parse(x.Trim())).ToArray();
            }
        }
        if (spec.Leds != null)
        {
            var v = spec.Leds.Resolve(look);
            double n = v switch { double d => d, string s when double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) => x, _ => throw new InvalidOperationException($"light.leds: '{v}' is not a number") };
            leds = Math.Clamp((int)Math.Round(n), 0, 5);
        }
        _lightChannels.TryGetValue(spec.Channel, out var previous);
        _lightChannels[spec.Channel] = new LightChannel(ev.Type, rgb ?? previous?.Rgb, leds ?? previous?.Leds,
            spec.Priority, spec.TimeoutMs > 0 ? spec.TimeoutMs : _cfg.PadIdleResetMs, _now);
        ComposeLight($"rule {rule.Id}");
        return "light " + LightState;
    }

    Decision PlayHaptic(Rule rule, GameEvent ev, Expr.Lookup look)
    {
        bool hdReady = Hd?.Ready == true;
        int rate = Hd?.Rate ?? DefaultRate;
        string route = rule.Path switch { "hd" => hdReady ? "hd" : "skip", "rumble" => "rumble", _ => hdReady ? "hd" : "rumble" };
        if (route == "rumble" && hdReady && _cfg.Limits.ConflictPolicy == "hd-wins" && Hd!.Active) route = "hd-conflict";
        switch (route)
        {
            case "skip":
                return new Decision(rule.Id, "skip", "path=hd, HD not available");
            case "hd":
            case "hd-conflict":
            {
                var (l, r) = route == "hd" ? RenderHd(rule.Effect, look, rate) : HdFx.FromRumble(rate, RenderMotor(rule.Effect, look, rate));
                if (_lateTrimMs > 0 && route == "hd") (l, r) = TrimLeadingSilence(l, r, (int)(rate * _lateTrimMs / 1000));
                if (_cfg.Calib.GainL != 1f) for (int i = 0; i < l.Length; i++) l[i] = Math.Clamp(l[i] * _cfg.Calib.GainL, -0.95f, 0.95f);   // lp-bench calibration
                if (_cfg.Calib.GainR != 1f) for (int i = 0; i < r.Length; i++) r[i] = Math.Clamp(r[i] * _cfg.Calib.GainR, -0.95f, 0.95f);
                if (rule.Continuous && !rule.Loop) (l, r) = Trim(l, r, (int)Math.Min(int.MaxValue, (long)rate * rule.MaxMs / 1000), rate / 20);   // long: 60 s x 48 kHz overflowed int
                var gp = rule.Group.Length > 0 && _cfg.Groups.TryGetValue(rule.Group, out var g) ? g : null;
                bool loop = rule.Continuous && rule.Loop;
                var opts = new VoiceOpts(rule.Priority, rule.Id, rule.Group, gp?.Mode ?? "mix", gp?.FadeMs ?? 250, loop ? rule.LoopFadeMs : gp?.FadeInMs ?? 0,
                                         Math.Min(rule.DuckOthers, gp?.DuckOthers ?? 1f), loop);
                long id = Hd!.Play(l, r, opts);
                if (rule.CancelWhen.Count > 0 && id > 0)
                {
                    if (!_cancelable.TryGetValue(rule.Id, out var ce)) _cancelable[rule.Id] = ce = (rule, new List<long>());
                    ce.voices.Add(id);
                    if (ce.voices.Count > 16) ce.voices.RemoveAt(0);   // long finished voices: stopping them is a no-op anyway
                }
                if (rule.Continuous && id > 0)
                {
                    if (_continuous.TryGetValue(ev.Type, out var old)) Hd.Stop(old.voice, 60);
                    _continuous[ev.Type] = (id, rule); _continuousStart[ev.Type] = _now; _continuousEvent[ev.Type] = ev;
                }
                return new Decision(rule.Id, id > 0 ? route : (rule.Group.Length > 0 ? "group-rejected" : "rejected"), $"{1000L * Math.Max(l.Length, r.Length) / rate} ms, prio {rule.Priority}{(rule.Continuous ? ", continuous" : "")}");
            }
            default:
            {
                var steps = RenderMotor(rule.Effect, look, rate);
                Motor.Pattern(steps);
                return new Decision(rule.Id, "rumble", $"{steps.Length} steps");
            }
        }
    }

    /// <summary>Time passes: continuous effects older than their maxMs are faded out (looping voices never end by themselves).</summary>
    public readonly Dictionary<string, int> DerivedRaised = new();

    public void Tick(DateTime now)
    {
        _now = now;
        foreach (var (id, n, ms) in Actions.CheckOvertime(now))
        {
            var oev = EventMonitor.ParseRecord($"0:overtime:start:t=0,src=derived_action,a={id},n={n},ms={ms}");
            if (oev != null) { var d = Handle(oev, now); if (d.Count > 0) Log?.Invoke($"action '{id}' overtime ({ms} ms) -> {string.Join("; ", d)}"); }
        }
        foreach (var key in _continuous.Keys.ToList())
            if (_continuousStart.TryGetValue(key, out var st) && (now - st).TotalMilliseconds > _continuous[key].rule.MaxMs)
            {
                Hd?.Stop(_continuous[key].voice, _continuous[key].rule.LoopFadeMs);
                Log?.Invoke($"continuous '{_continuous[key].rule.Id}' stopped at maxMs {_continuous[key].rule.MaxMs}");
                _continuous.Remove(key); TimedOutContinuous++;
            }
        if (_padOwner != null && PadActive && (now - _padLast).TotalMilliseconds > _cfg.PadIdleResetMs)
            ResetPad($"watchdog: no '{_padOwner}' event for {_cfg.PadIdleResetMs} ms");
        foreach (var (key, value) in _lightChannels.ToArray())
            if ((now - value.Last).TotalMilliseconds > value.TimeoutMs)
                ResetLightChannel(key, "light watchdog");
    }
    public int TimedOutContinuous;

    public void StopAll(string reason)
    {
        Hd?.StopAll(); Motor.StopAll();
        ResetPad(reason);
        ResetLight(reason);
        if (_continuous.Count > 0) Log?.Invoke($"continuous effects stopped ({reason}): {string.Join(", ", ActiveContinuous)}");
        _continuous.Clear();
    }

    /// <summary>Stops continuous effects (state tracker closed their state: menu, lost game, stale).</summary>
    public void InterruptContinuous(string reason)
    {
        foreach (var kv in _continuous) Hd?.Stop(kv.Value.voice, 60);
        if (_continuous.Count > 0) Log?.Invoke($"continuous effects interrupted ({reason}): {string.Join(", ", ActiveContinuous)}");
        _continuous.Clear();
    }

    // ---------------- rendering (static: also used by replay and rules-check) ----------------

    /// <summary>Removes up to `max` samples of the voice's leading silence (both actuators below 1e-4): a late event's
    /// scheduled layers keep their place on the game's timeline; sound itself is never cut.</summary>
    (float[] l, float[] r) TrimLeadingSilence(float[] l, float[] r, int max)
    {
        int n = Math.Max(l.Length, r.Length), i = 0;
        while (i < n && i < max && (i >= l.Length || Math.Abs(l[i]) < 1e-4f) && (i >= r.Length || Math.Abs(r[i]) < 1e-4f)) i++;
        if (i < (Hd?.Rate ?? DefaultRate) / 1000) return (l, r);   // < 1 ms = the zero crossing of an instant voice, not a gap
        LateTrims++; LateTrimmedMs += 1000.0 * i / (Hd?.Rate ?? DefaultRate);
        return (l.Length > i ? l[i..] : Array.Empty<float>(), r.Length > i ? r[i..] : Array.Empty<float>());
    }

    public static Dictionary<string, object?> Resolve(Layer l, Expr.Lookup look) =>
        l.P.ToDictionary(kv => kv.Key, kv => kv.Value.Resolve(look), StringComparer.OrdinalIgnoreCase);

    public static (float[] l, float[] r) RenderHd(EffectSpec spec, Expr.Lookup look, int rate)
    {
        float[] L = Array.Empty<float>(), R = Array.Empty<float>();
        foreach (var layer in spec.Layers)
        {
            var (l, r) = Presets.RenderLayer(layer.Preset, Resolve(layer, look), rate);
            int n = Math.Max(Math.Max(L.Length, R.Length), Math.Max(l.Length, r.Length));
            if (L.Length < n) Array.Resize(ref L, n);
            if (R.Length < n) Array.Resize(ref R, n);
            for (int i = 0; i < l.Length; i++) L[i] += l[i];
            for (int i = 0; i < r.Length; i++) R[i] += r[i];
        }
        for (int i = 0; i < L.Length; i++) L[i] = Math.Clamp(L[i], -0.95f, 0.95f);
        for (int i = 0; i < R.Length; i++) R[i] = Math.Clamp(R[i], -0.95f, 0.95f);
        return (L, R);
    }

    /// <summary>Motor path: the effect's explicit "motor" spec if present, else exact steps (Rumble/MotorWave) or the
    /// envelope of the HD rendering.</summary>
    public static (int delay, byte right, byte left, int ms)[] RenderMotor(EffectSpec spec, Expr.Lookup look, int rate)
    {
        var s = spec.Motor ?? spec;
        var res = new List<(int, byte, byte, int)>();
        foreach (var layer in s.Layers)
        {
            var p = Resolve(layer, look);
            var exact = Presets.RenderMotorExact(layer.Preset, p);
            if (exact != null) { res.AddRange(exact); continue; }
            var (l, r) = Presets.RenderLayer(layer.Preset, p, rate);
            res.AddRange(Presets.MotorFromAudio(l, r, rate));
        }
        return res.ToArray();
    }

    static (float[] l, float[] r) Trim(float[] l, float[] r, int max, int fade)
    {
        if (Math.Max(l.Length, r.Length) <= max) return (l, r);
        float[] T(float[] a)
        {
            var o = a.Take(max).ToArray();
            for (int i = 0; i < Math.Min(fade, o.Length); i++) o[o.Length - 1 - i] *= (float)i / fade;
            return o;
        }
        return (T(l), T(r));
    }
}
