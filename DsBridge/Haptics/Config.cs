using System.Globalization;
using System.Text.Json;

namespace DsBridge.Haptics;

/// <summary>A rule/effect parameter: number, text, expression ("=..."), event reference ("$x" / "$event.x") or list.</summary>
public sealed class Param
{
    public double? Number; public string? Text; public Expr.Compiled? Expression; public List<Param>? List;

    public static Param Parse(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Number: return new Param { Number = e.GetDouble() };
            case JsonValueKind.True: return new Param { Number = 1 };
            case JsonValueKind.False: return new Param { Number = 0 };
            case JsonValueKind.Array: return new Param { List = e.EnumerateArray().Select(Parse).ToList() };
            case JsonValueKind.String:
            {
                var s = e.GetString() ?? "";
                if (s.StartsWith("=")) return new Param { Expression = Expr.Compile(s[1..]) };
                if (s.StartsWith("$")) return new Param { Expression = Expr.Compile(s) };
                return new Param { Text = s };
            }
            default: throw new FormatException($"unsupported parameter value {e}");
        }
    }

    /// <summary>Resolves to double / string / List&lt;object?&gt; / null.</summary>
    public object? Resolve(Expr.Lookup vars)
    {
        if (Number is double d) return d;
        if (Text != null) return Text;
        if (Expression != null) return Expression.Eval(vars);
        if (List != null) return List.Select(p => p.Resolve(vars)).ToList();
        return null;
    }
}

/// <summary>One synthesized layer of an effect: a preset + parameters (+ generic delayMs, gain, gainL, gainR, pan).</summary>
public sealed class Layer
{
    public string Preset = "";
    public Dictionary<string, Param> P = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Effect = layers mixed together; Motor = optional explicit rendering for the rumble-emulation path.</summary>
public sealed class EffectSpec
{
    public List<Layer> Layers = new();
    public EffectSpec? Motor;
}

/// <summary>Adaptive-trigger output of a rule: l2 / r2 = a trigger profile name (or an expression giving one), reset = both off.</summary>
/// <summary>Lightbar and player LEDs from a rule: "leds" = how many of the five are lit (0-5, symmetric pattern),
/// "color" = name or "R,G,B" (may be an expression); "reset" releases this channel.
/// Optional channel, priority and timeoutMs arbitrate independent sources; default channel preserves existing rules.</summary>
public sealed class LightSpec
{
    public string Channel = "default";
    public int Priority, TimeoutMs; // 0 = normal pad watchdog; channels expire independently

    public Param? Leds, Color;
    public bool Reset;
}

public sealed class PadSpec
{
    public Param? L2, R2;
    public bool Reset;
    /// <summary>A short pulse on both triggers (profile name, usually a trigger vibration) for PulseMs, then the state
    /// the rules set comes back by itself (non-blocking; PadOutLive ends it from the main loop).</summary>
    public Param? Pulse;
    public int PulseMs = 65;
    /// <summary>Which trigger pulses: "L", "R" or "both" (default); may be an expression, e.g. "=dir".</summary>
    public Param? PulseSide;
    /// <summary>The pulse starts this long after the event (0..200 ms), e.g. to let a Bow click snap first.</summary>
    public int PulseDelayMs = 0;
    /// <summary>The pulse comes from a finger pressing the trigger: the side counts as held until a "release" for it,
    /// so after the pulse the trigger stays off (no resistance re-armed under the finger) until the release.</summary>
    public bool PulseHold;
    /// <summary>The finger let go of this trigger ("L", "R", "both"; expression): a parked trigger gets its state back.</summary>
    public Param? Release;
}

/// <summary>A named adaptive-trigger effect (pack "triggerProfiles"): off | feedback (pos 0-9, str 1-8) |
/// vibration (pos, amp 1-8, hz) | weapon (pos = start 2-7, end, str: a step that gives way) | zones (10 strengths 0-8).</summary>
public sealed record TriggerProfile(string Name, string Mode, int Pos, int Str, int Amp, int Hz, int End = 0, int[]? Zones = null, int Snap = 0)
{
    public byte[] Bytes() => Mode switch
    {
        "feedback" => DualSense.EffectFeedback(Pos, Str),
        "vibration" => DualSense.EffectVibration(Pos, Amp, Hz),
        "weapon" => DualSense.EffectWeapon(Pos, End, Str),
        "bow" => DualSense.EffectBow(Pos, End, Str, Snap),
        "zones" => DualSense.EffectFeedbackZones(Zones ?? new int[10]),
        _ => DualSense.EffectOff()
    };
    public bool IsOff => Mode == "off";
    public override string ToString() => Mode switch
    {
        "feedback" => $"{Name}(feedback {Pos}/{Str})",
        "vibration" => $"{Name}(vibration {Pos}/{Amp}/{Hz}Hz)",
        "weapon" => $"{Name}(step {Pos}-{End}/{Str})",
        "bow" => $"{Name}(bow {Pos}-{End}/{Str} snap {Snap})",
        "zones" => $"{Name}(zones {string.Join("", Zones ?? Array.Empty<int>())})",
        _ => $"{Name}(off)"
    };
}

/// <summary>Per-actuator calibration (haptics/calibration.json, written by `lp-bench`): HD gain of the left / right actuator.</summary>
public sealed class Calibration
{
    public float GainL = 1f, GainR = 1f;
    public string Path = "";
    public static Calibration Load(string root)
    {
        var c = new Calibration { Path = System.IO.Path.Combine(root, "calibration.json") };
        try
        {
            if (!File.Exists(c.Path)) return c;
            using var d = JsonDocument.Parse(File.ReadAllText(c.Path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (d.RootElement.TryGetProperty("gainL", out var l)) c.GainL = (float)Math.Clamp(l.GetDouble(), 0, 2);
            if (d.RootElement.TryGetProperty("gainR", out var r)) c.GainR = (float)Math.Clamp(r.GetDouble(), 0, 2);
        }
        catch { }
        return c;
    }
    public void Save() => File.WriteAllText(Path, $"// HD actuator calibration (lp-bench). 1.0 = unchanged.\n{{ \"gainL\": {GainL.ToString("0.00", CultureInfo.InvariantCulture)}, \"gainR\": {GainR.ToString("0.00", CultureInfo.InvariantCulture)} }}\n");
}

public sealed class Rule
{
    public string Id = "", Pack = "", Event = "*";
    public VariationSpec? Variation; public int StopOnFadeMs = 60;
    public PadSpec? Pad;                                          // adaptive triggers (optional; a rule may have only this)
    public LightSpec? Light;                                      // lightbar + player LEDs (optional; a rule may have only this)
    public HashSet<string> Phases = new() { "start" };
    public Dictionary<string, string[]> Where = new();          // key -> allowed values ("!x" = not x, "*" = present)
    public Expr.Compiled? When;
    public List<(string name, Expr.Compiled expr)> Let = new();
    public EffectSpec Effect = new();
    public string Path = "auto";                                 // auto | hd | rumble
    public int Priority = 1;                                      // low 0, normal 1, high 2, critical 3
    public int CooldownMs;
    public bool Continuous; public HashSet<string> StopOn = new() { "complete", "cancel", "interrupt" }; public int MaxMs = 10000;
    public HashSet<string> StopWhen = new();                     // continuous: also stop on any of these "type:phase" events
    public int StopFadeMs = 120;                                // stopWhen fade; preserve existing behaviour unless explicitly set
    public Expr.Compiled? StopIf;                               // optional predicate over the incoming event; heartbeats need not cancel a cue
    public string UntilAction = "";                              // continuous: stop when this correlated action closes
    public bool Loop; public int LoopFadeMs = 150;               // continuous: loop the rendering until stopped (maxMs still applies)
    public string Group = "";                                    // mixer group (haptics.json "groups" defines the policy)
    public string Situation = "";                                // one physical situation (e.g. "melee.contact"): within SituationMs only the
    public int SituationMs = 150;                                // first rule of that situation plays (a higher priority one may still play) -
    public bool SituationCheckOnly;                              // "situationCheckOnly": true - only yields to the situation, never claims it
                                                                 // two sources of the same moment never stack two effects
    public float DuckOthers = 1f;                                // < 1: all other voices x this while this effect plays
    public bool Also;                                             // keep matching further rules
    // "cancel": {"when": ["type:phase"...], "if": "<expr over the cancelling event>", "fadeMs": 60}: every voice this rule
    // started that still sounds (including a not yet reached delayMs) is faded out - e.g. an interrupted cast must not
    // play its scheduled release / explosion (magic, mod v44)
    public HashSet<string> CancelWhen = new(); public Expr.Compiled? CancelIf; public int CancelFadeMs = 60;
    public string Note = "";

    public bool Matches(GameEvent ev)
    {
        if (Event != "*" && !string.Equals(Event, ev.Type, StringComparison.Ordinal)) return false;
        if (!Phases.Contains("*") && !Phases.Contains(ev.Phase)) return false;
        foreach (var (key, allowed) in Where)
        {
            var v = ev.Get(key);
            bool ok = false;
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

public sealed class RegistryEntry
{
    public string Type = "", Source = "", Status = "DISCOVERED", Category = "", Note = "";
    public List<string> Phases = new();
    public Dictionary<string, string> Fields = new();
    public Dictionary<string, string> Sample = new();             // example context for rules-check
    public bool Continuous;
    public bool Restartable, ExpectUnended; public int StaleMs; public List<string> EndedBy = new();   // state tracking (StateTracker.Configure)
    public double MaxRatePerSec;                                                        // anomaly threshold (0 = default)
}

public sealed record GroupPolicy(string Mode = "mix", int FadeMs = 250, int FadeInMs = 0, float DuckOthers = 1f);

public sealed class Limits
{
    public float MasterGain = 1f, Ceiling = 0.95f, Knee = 0.9f, DuckLower = 0.5f, GuardFloor = 0.6f;
    public int MaxVoices = 8, StallMs = 80;
    public string ConflictPolicy = "hd-wins";                     // motor effects go through HD while HD voices are active
    /// <summary>Delivery-lateness trim (HapticRuntime.LateClock): an event that reached DsBridge later than usual (its delay
    /// above the 2 s floor beyond LateTrimTargetMs) starts its HD voice that much earlier, at most LateTrimMaxMs and only out
    /// of the voice's own leading silence (scheduled layers). 0 = off.</summary>
    public int LateTrimTargetMs = 12, LateTrimMaxMs = 0;
}

public sealed record PackInfo(string Name, string File, bool Enabled, string Description, int RuleCount);

/// <summary>haptics.json + packs + registry. Errors make a config unusable; warnings do not.</summary>
public sealed class HapticConfig
{
    public string Root = "";
    public List<PackInfo> Packs = new();
    /// <summary>Packs with "onsetLead": true in haptics.json (slow-onset samples start earlier).</summary>
    public HashSet<string> OnsetLeadPacks = new(StringComparer.OrdinalIgnoreCase);
    public List<Rule> Rules = new();                               // evaluation order: later packs first, then file order
    public Dictionary<string, RegistryEntry> Registry = new(StringComparer.Ordinal);
    public Limits Limits = new();
    public Dictionary<string, GroupPolicy> Groups = new();
    public Correlator Actions = new();
    /// <summary>Derived (edge) events: a polled / update event becomes start / complete of a new type when a formula turns true.</summary>
    public List<(string type, string from, Expr.Compiled? start, Expr.Compiled? complete, Expr.Compiled? update, List<(string name, Expr.Compiled f)> fields)> Derived = new();
    public string? ActionsPath;
    /// <summary>Enabled packs that declare their own correlated actions (merged after actions.json).</summary>
    public List<string> PackActionPaths = new();
    /// <summary>Adaptive-trigger profiles from all packs (a later pack replaces a profile of the same name).</summary>
    public Dictionary<string, TriggerProfile> TriggerProfiles = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Triggers left non-off with no event of the owning type for this long are reset (packs: "padIdleResetMs").</summary>
    public int PadIdleResetMs = 15000;
    /// <summary>HD actuator calibration (calibration.json next to haptics.json).</summary>
    public Calibration Calib = new();
    /// <summary>Montage name -> action class (montages.generated.json, tools/haptic_intel.py): montage events get mc / mw / md / ...</summary>
    public MontageCatalog Montages = new();
    /// <summary>Rule ids a pack switches off while it is enabled (pack "disableRules"), e.g. older generic effects it replaces.</summary>
    public Dictionary<string, List<string>> PackDisables = new(StringComparer.OrdinalIgnoreCase);
    public List<string> DisabledRules = new();
    public List<string> Errors = new(), Warnings = new();
    public bool Ok => Errors.Count == 0;

    static readonly JsonDocumentOptions JOpt = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Finds haptics/haptics.json: explicit path, then ./haptics, ../haptics, and up from the executable.</summary>
    public static string? Locate(string? explicitPath)
    {
        if (!string.IsNullOrEmpty(explicitPath)) return File.Exists(explicitPath) ? Path.GetFullPath(explicitPath) : null;
        var starts = new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
        foreach (var s in starts)
        {
            var d = new DirectoryInfo(s);
            for (int i = 0; i < 7 && d != null; i++, d = d.Parent)
            {
                var f = Path.Combine(d.FullName, "haptics", "haptics.json");
                if (File.Exists(f)) return f;
            }
        }
        return null;
    }

    /// <summary>Packs switched ON for this process although haptics.json has them "enabled": false (command line --enable-pack),
    /// e.g. an automatically generated candidate pack under test. Set once by Program before any Load.</summary>
    public static HashSet<string> EnablePacks = new(StringComparer.OrdinalIgnoreCase);

    public static HapticConfig Load(string mainPath, IEnumerable<string>? disablePacks = null, IEnumerable<string>? onlyPacks = null)
    {
        var cfg = new HapticConfig { Root = Path.GetDirectoryName(Path.GetFullPath(mainPath))! };
        SampleBank.Init(cfg.Root);   // forge bank (haptics/bank) + variant selection for the "Sample" preset
        var disable = new HashSet<string>(disablePacks ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var only = onlyPacks == null ? null : new HashSet<string>(onlyPacks, StringComparer.OrdinalIgnoreCase);
        JsonDocument main;
        try { main = JsonDocument.Parse(File.ReadAllText(mainPath), JOpt); }
        catch (Exception ex) { cfg.Errors.Add($"{mainPath}: {ex.Message}"); return cfg; }

        var m = main.RootElement;
        if (m.TryGetProperty("limits", out var lim)) cfg.Limits = ParseLimits(lim, cfg);
        if (m.TryGetProperty("registry", out var reg)) cfg.LoadRegistry(Path.Combine(cfg.Root, reg.GetString() ?? "registry.json"));
        var gen = Path.Combine(cfg.Root, "registry.generated.json");          // types added by tools/gen_hooks.py
        if (File.Exists(gen)) cfg.LoadRegistry(gen);
        if (m.TryGetProperty("groups", out var grps))
            foreach (var g in grps.EnumerateObject())
            {
                var gp = new GroupPolicy();
                foreach (var p in g.Value.EnumerateObject())
                    gp = p.Name switch
                    {
                        "mode" => gp with { Mode = p.Value.GetString() ?? "mix" },
                        "fadeMs" => gp with { FadeMs = p.Value.GetInt32() },
                        "fadeInMs" => gp with { FadeInMs = p.Value.GetInt32() },
                        "duckOthers" => gp with { DuckOthers = (float)p.Value.GetDouble() },
                        "note" => gp,
                        _ => throw new FormatException($"groups.{g.Name}: unknown key '{p.Name}'")
                    };
                if (gp.Mode is not ("mix" or "crossfade" or "exclusive")) cfg.Errors.Add($"groups.{g.Name}: mode must be mix|crossfade|exclusive");
                cfg.Groups[g.Name] = gp;
            }
        if (m.TryGetProperty("derived", out var der))
        {
            var dp = Path.Combine(cfg.Root, der.GetString() ?? "derived.json");
            try
            {
                using var dd = JsonDocument.Parse(File.ReadAllText(dp), JOpt);
                foreach (var d in dd.RootElement.GetProperty("derived").EnumerateArray())
                {
                    string type = d.GetProperty("type").GetString()!, from = d.GetProperty("from").GetString()!;
                    Expr.Compiled? C(string k) => d.TryGetProperty(k, out var x) ? Expr.Compile(x.GetString()!.TrimStart('=')) : null;
                    var fl = new List<(string, Expr.Compiled)>();
                    if (d.TryGetProperty("fields", out var fs)) foreach (var f in fs.EnumerateObject()) fl.Add((f.Name, Expr.Compile(f.Value.GetString()!.TrimStart('='))));
                    cfg.Derived.Add((type, from, C("start"), C("complete"), C("update"), fl));
                }
            }
            catch (Exception ex) { cfg.Errors.Add($"derived {dp}: {ex.Message}"); }
        }
        if (m.TryGetProperty("actions", out var act))
        {
            cfg.ActionsPath = Path.Combine(cfg.Root, act.GetString() ?? "actions.json");
            cfg.Actions = Correlator.Load(cfg.ActionsPath, cfg.Errors);
        }

        var packRules = new List<List<Rule>>();
        if (m.TryGetProperty("packs", out var packs))
            foreach (var p in packs.EnumerateArray())
            {
                string file = p.ValueKind == JsonValueKind.String ? p.GetString()! : p.GetProperty("file").GetString()!;
                bool enabled = p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("enabled", out var en) || en.GetBoolean();
                var (name, desc, rules) = cfg.LoadPack(Path.Combine(cfg.Root, file));
                if (EnablePacks.Contains(name) || EnablePacks.Contains(file)) enabled = true;
                if (disable.Contains(name) || disable.Contains(file)) enabled = false;
                if (only != null) enabled = only.Contains(name) || only.Contains(file);
                cfg.Packs.Add(new PackInfo(name, file, enabled, desc, rules.Count));
                // "onsetLead": true - every Sample layer of the pack without its own offsetMs / lead starts slow-onset samples
                // earlier (SampleBank.WithLead); one switch per pack, so an approved pack can be returned to its old timing
                if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("onsetLead", out var ol) && ol.ValueKind == JsonValueKind.True)
                {
                    cfg.OnsetLeadPacks.Add(name);
                    foreach (var r in rules)
                        foreach (var spec in new[] { r.Effect, r.Effect?.Motor })
                            foreach (var layer in spec?.Layers ?? new List<Layer>())
                                if (layer.Preset.Equals("Sample", StringComparison.OrdinalIgnoreCase) && !layer.P.ContainsKey("offsetMs") && !layer.P.ContainsKey("lead"))
                                    layer.P["lead"] = new Param { Text = "auto" };
                }
                if (enabled) packRules.Add(rules);
                // a pack may bring its own correlated actions ("actions": [...], same format as actions.json): they exist only
                // while the pack is enabled (e.g. a candidate pack), so the base configuration keeps its exact behaviour
                var packPath = Path.Combine(cfg.Root, file);
                if (enabled && File.Exists(packPath) && File.ReadAllText(packPath).Contains("\"actions\""))
                {
                    var extra = Correlator.Load(packPath, cfg.Errors);
                    if (extra.Defs.Count > 0) { cfg.PackActionPaths.Add(packPath); cfg.Actions.Defs.AddRange(extra.Defs); }
                }
            }
        // later packs override earlier ones: same id replaces, otherwise later packs are evaluated first
        var byId = new Dictionary<string, Rule>();
        for (int i = 0; i < packRules.Count; i++) foreach (var r in packRules[i]) byId[r.Id] = r;
        foreach (var pi in cfg.Packs.Where(x => x.Enabled))
            if (cfg.PackDisables.TryGetValue(pi.Name, out var dis))
                foreach (var id in dis)
                    if (byId.TryGetValue(id, out var dRule) && !string.Equals(dRule.Pack, pi.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        byId.Remove(id); cfg.DisabledRules.Add($"{id} (by pack {pi.Name})");
                        foreach (var lst in packRules) lst.RemoveAll(x => x.Id == id);
                    }
        for (int i = packRules.Count - 1; i >= 0; i--)
            foreach (var r in packRules[i]) if (ReferenceEquals(byId[r.Id], r) && !cfg.Rules.Contains(r)) cfg.Rules.Add(r);
        foreach (var r in cfg.Rules)
        {
            if (r.Event != "*" && cfg.Registry.Count > 0 && !cfg.Registry.ContainsKey(r.Event))
                cfg.Warnings.Add($"rule '{r.Id}' ({r.Pack}): event type '{r.Event}' is not in the registry");
            if (r.Group.Length > 0 && !cfg.Groups.ContainsKey(r.Group)) cfg.Warnings.Add($"rule '{r.Id}' ({r.Pack}): group '{r.Group}' has no policy in haptics.json (mixed normally)");
            if (r.UntilAction.Length > 0 && cfg.Actions.Defs.All(a => a.Id != r.UntilAction)) cfg.Errors.Add($"rule '{r.Id}' ({r.Pack}): untilAction '{r.UntilAction}' is not defined in actions.json");
            foreach (var (side, p) in new[] { ("l2", r.Pad?.L2), ("r2", r.Pad?.R2) })
                if (p?.Text is string prof && !cfg.TriggerProfiles.ContainsKey(prof))
                    cfg.Errors.Add($"rule '{r.Id}' ({r.Pack}): pad.{side} profile '{prof}' is not defined in any pack's triggerProfiles");
        }
        cfg.Calib = Calibration.Load(cfg.Root);
        cfg.Montages = MontageCatalog.Load(Path.Combine(cfg.Root, "montages.generated.json"));
        return cfg;
    }

    static Limits ParseLimits(JsonElement e, HapticConfig cfg)
    {
        var l = new Limits();
        foreach (var p in e.EnumerateObject())
            switch (p.Name)
            {
                case "masterGain": l.MasterGain = (float)p.Value.GetDouble(); break;
                case "ceiling": l.Ceiling = (float)p.Value.GetDouble(); break;
                case "knee": l.Knee = (float)p.Value.GetDouble(); break;
                case "guardFloor": l.GuardFloor = (float)p.Value.GetDouble(); break;
                case "duckLower": l.DuckLower = (float)p.Value.GetDouble(); break;
                case "maxVoices": l.MaxVoices = p.Value.GetInt32(); break;
                case "stallMs": l.StallMs = p.Value.GetInt32(); break;
                case "conflictPolicy": l.ConflictPolicy = p.Value.GetString() ?? "hd-wins"; break;
                case "lateTrimTargetMs": l.LateTrimTargetMs = p.Value.GetInt32(); break;
                case "lateTrimMaxMs": l.LateTrimMaxMs = p.Value.GetInt32(); break;
                default: cfg.Warnings.Add($"limits: unknown key '{p.Name}'"); break;
            }
        return l;
    }

    void LoadRegistry(string path)
    {
        try
        {
            using var d = JsonDocument.Parse(File.ReadAllText(path), JOpt);
            foreach (var e in d.RootElement.GetProperty("events").EnumerateArray())
            {
                var r = new RegistryEntry { Type = e.GetProperty("type").GetString()! };
                if (e.TryGetProperty("phases", out var ph)) r.Phases = ph.EnumerateArray().Select(x => x.GetString()!).ToList();
                if (e.TryGetProperty("source", out var s)) r.Source = s.GetString() ?? "";
                if (e.TryGetProperty("status", out var st)) r.Status = st.GetString() ?? "";
                if (e.TryGetProperty("category", out var c)) r.Category = c.GetString() ?? "";
                if (e.TryGetProperty("note", out var n)) r.Note = n.GetString() ?? "";
                if (e.TryGetProperty("continuous", out var co)) r.Continuous = co.GetBoolean();
                if (e.TryGetProperty("restartable", out var rs)) r.Restartable = rs.GetBoolean();
                if (e.TryGetProperty("expectUnended", out var eu)) r.ExpectUnended = eu.GetBoolean();
                if (e.TryGetProperty("staleMs", out var sms)) r.StaleMs = sms.GetInt32();
                if (e.TryGetProperty("endedBy", out var eb)) r.EndedBy = eb.EnumerateArray().Select(x => x.GetString()!).ToList();
                if (e.TryGetProperty("maxRatePerSec", out var mr)) r.MaxRatePerSec = mr.GetDouble();
                if (e.TryGetProperty("fields", out var f)) foreach (var p in f.EnumerateObject()) r.Fields[p.Name] = p.Value.ToString();
                if (e.TryGetProperty("sample", out var sm)) foreach (var p in sm.EnumerateObject()) r.Sample[p.Name] = p.Value.ToString();
                if (Registry.ContainsKey(r.Type)) Warnings.Add($"registry: duplicate type '{r.Type}'");
                Registry[r.Type] = r;
            }
        }
        catch (Exception ex) { Errors.Add($"registry {path}: {ex.Message}"); }
    }

    /// <summary>Explicit opt-in overlay, e.g. a diagnostic experiment. Never discovered automatically.</summary>
    public void AddSupplementalPack(string file)
    {
        int errors = Errors.Count;
        var (name, description, rules) = LoadPack(Path.Combine(Root, file));
        foreach (var rule in rules)
            if (Rules.Any(r => r.Id == rule.Id)) Errors.Add($"supplemental pack duplicates active rule {rule.Id}");
        if (Errors.Count != errors) throw new FormatException(string.Join("; ", Errors.Skip(errors)));
        Rules.InsertRange(0, rules);
        Packs.Add(new PackInfo(name, file, true, description, rules.Count));
    }

    (string name, string desc, List<Rule> rules) LoadPack(string path)
    {
        var rules = new List<Rule>(); string name = Path.GetFileNameWithoutExtension(path), desc = "";
        JsonDocument d;
        try { d = JsonDocument.Parse(File.ReadAllText(path), JOpt); }
        catch (Exception ex) { Errors.Add($"pack {path}: {ex.Message}"); return (name, desc, rules); }
        using (d)
        {
            var root = d.RootElement;
            if (root.TryGetProperty("pack", out var pn)) name = pn.GetString() ?? name;
            if (root.TryGetProperty("description", out var pd)) desc = pd.GetString() ?? "";
            if (root.TryGetProperty("padIdleResetMs", out var pir)) PadIdleResetMs = pir.GetInt32();
            if (root.TryGetProperty("disableRules", out var dr)) PackDisables[name] = dr.EnumerateArray().Select(x => x.GetString()!).ToList();
            if (root.TryGetProperty("triggerProfiles", out var tps))
                foreach (var tp in tps.EnumerateObject())
                {
                    try { TriggerProfiles[tp.Name] = ParseTriggerProfile(tp.Name, tp.Value); }
                    catch (Exception ex) { Errors.Add($"{name}: triggerProfiles.{tp.Name}: {ex.Message}"); }
                }
            var ids = new HashSet<string>();
            int idx = 0;
            foreach (var re in root.GetProperty("rules").EnumerateArray())
            {
                idx++;
                string where = $"{name} rule #{idx}";
                try
                {
                    var r = ParseRule(re, name);
                    where = $"{name}/{r.Id}";
                    if (!ids.Add(r.Id)) Errors.Add($"{where}: duplicate rule id in pack");
                    if (re.TryGetProperty("enabled", out var en) && !en.GetBoolean()) continue;
                    Validate(r, where);
                    rules.Add(r);
                }
                catch (Exception ex) { Errors.Add($"{where}: {ex.Message}"); }
            }
        }
        return (name, desc, rules);
    }

    static readonly Dictionary<string, int> Prio = new(StringComparer.OrdinalIgnoreCase) { ["low"] = 0, ["normal"] = 1, ["high"] = 2, ["critical"] = 3 };

    static Rule ParseRule(JsonElement e, string pack)
    {
        var r = new Rule { Pack = pack };
        r.Id = e.TryGetProperty("id", out var id) ? id.GetString()! : throw new FormatException("missing 'id'");
        if (e.TryGetProperty("event", out var ev))
        {
            var s = ev.GetString() ?? "*";
            int c = s.IndexOf(':');
            if (c > 0) { r.Event = s[..c]; r.Phases = new HashSet<string> { s[(c + 1)..] }; } else r.Event = s;
        }
        if (e.TryGetProperty("phase", out var ph))
            r.Phases = ph.ValueKind == JsonValueKind.Array ? ph.EnumerateArray().Select(x => x.GetString()!).ToHashSet() : new HashSet<string> { ph.GetString()! };
        if (e.TryGetProperty("where", out var w))
            foreach (var p in w.EnumerateObject())
                r.Where[p.Name] = p.Value.ValueKind == JsonValueKind.Array ? p.Value.EnumerateArray().Select(x => x.ToString()).ToArray() : new[] { p.Value.ToString() };
        if (e.TryGetProperty("when", out var wh)) { var s = wh.GetString()!; r.When = Expr.Compile(s.StartsWith("=") ? s[1..] : s); }
        if (e.TryGetProperty("let", out var let))
            foreach (var p in let.EnumerateObject()) { var s = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.ToString(); r.Let.Add((p.Name, Expr.Compile(s.StartsWith("=") ? s[1..] : s))); }
        if (e.TryGetProperty("light", out var light))
        {
            r.Light = new LightSpec();
            foreach (var p in light.EnumerateObject())
                switch (p.Name)
                {
                    case "leds": r.Light.Leds = Param.Parse(p.Value); break;
                    case "color": r.Light.Color = Param.Parse(p.Value); break;
                    case "reset": r.Light.Reset = p.Value.GetBoolean(); break;
                    case "channel": r.Light.Channel = p.Value.GetString() ?? "default"; break;
                    case "priority": r.Light.Priority = p.Value.GetInt32(); break;
                    case "timeoutMs": r.Light.TimeoutMs = p.Value.GetInt32(); if (r.Light.TimeoutMs < 0) throw new FormatException("light.timeoutMs must be >= 0"); break;
                    case "note": break;
                    default: throw new FormatException($"light: unknown key '{p.Name}' (leds, color, reset, channel, priority, timeoutMs)");
                }
        }
        if (e.TryGetProperty("pad", out var pad))
        {
            r.Pad = new PadSpec();
            foreach (var p in pad.EnumerateObject())
                switch (p.Name)
                {
                    case "l2": r.Pad.L2 = Param.Parse(p.Value); break;
                    case "r2": r.Pad.R2 = Param.Parse(p.Value); break;
                    case "reset": r.Pad.Reset = p.Value.GetBoolean(); break;
                    case "pulse": r.Pad.Pulse = Param.Parse(p.Value); break;
                    case "pulseMs": r.Pad.PulseMs = Math.Clamp(p.Value.GetInt32(), 10, 500); break;
                    case "pulseSide": r.Pad.PulseSide = Param.Parse(p.Value); break;
                    case "pulseDelayMs": r.Pad.PulseDelayMs = Math.Clamp(p.Value.GetInt32(), 0, 200); break;
                    case "pulseHold": r.Pad.PulseHold = p.Value.GetBoolean(); break;
                    case "release": r.Pad.Release = Param.Parse(p.Value); break;
                    case "note": break;
                    default: throw new FormatException($"pad: unknown key '{p.Name}' (l2, r2, reset, pulse, pulseMs, pulseSide, pulseDelayMs, pulseHold, release)");
                }
        }
        if(e.TryGetProperty("variation",out var variation)) {
            string mode=variation.GetProperty("mode").GetString()!;
            int count=variation.TryGetProperty("count",out var vc)?vc.GetInt32():8;
            string group=variation.TryGetProperty("group",out var vg)?vg.GetString()!:r.Id;
            string scope=variation.TryGetProperty("scope",out var vs)?vs.GetString()!:"event";
            if(mode is not ("cycle" or "random")||count<1||count>64||scope is not ("event" or "montage")||group.Length==0)throw new FormatException("invalid variation mode/count/group/scope");
            r.Variation=new VariationSpec(mode,count,group,scope);
        }
        r.Effect = e.TryGetProperty("effect", out var ef) ? ParseEffect(ef) : r.Pad != null || r.Light != null ? new EffectSpec() : throw new FormatException("missing 'effect' (or 'pad' / 'light')");
        if (e.TryGetProperty("path", out var pa)) r.Path = pa.GetString()!.ToLowerInvariant();
        if (e.TryGetProperty("priority", out var pr)) r.Priority = pr.ValueKind == JsonValueKind.Number ? pr.GetInt32() : Prio.TryGetValue(pr.GetString()!, out var pv) ? pv : throw new FormatException($"unknown priority '{pr}'");
        if (e.TryGetProperty("cooldownMs", out var cd)) r.CooldownMs = cd.GetInt32();
        if (e.TryGetProperty("also", out var al)) r.Also = al.GetBoolean();
        if (e.TryGetProperty("note", out var no)) r.Note = no.GetString() ?? "";
        if (e.TryGetProperty("group", out var gr)) r.Group = gr.GetString() ?? "";
        if (e.TryGetProperty("situation", out var sitn)) r.Situation = sitn.GetString() ?? "";
        if (e.TryGetProperty("situationMs", out var sitm)) r.SituationMs = sitm.GetInt32();
        if (e.TryGetProperty("situationCheckOnly", out var sitc)) r.SituationCheckOnly = sitc.GetBoolean();
        if (e.TryGetProperty("duckOthers", out var du)) r.DuckOthers = (float)du.GetDouble();
        if (e.TryGetProperty("cancel", out var ca))
        {
            if (ca.TryGetProperty("when", out var cw)) r.CancelWhen = cw.EnumerateArray().Select(x => x.GetString()!).ToHashSet();
            if (ca.TryGetProperty("if", out var ci)) r.CancelIf = Expr.Compile(ci.GetString()!.TrimStart('='));
            if (ca.TryGetProperty("fadeMs", out var cf)) r.CancelFadeMs = Math.Clamp(cf.GetInt32(), 0, 2000);
            if (r.CancelWhen.Count == 0) throw new FormatException("cancel: 'when' needs at least one \"type:phase\"");
        }
        if (e.TryGetProperty("continuous", out var co))
        {
            if (co.ValueKind == JsonValueKind.True) r.Continuous = true;
            else if (co.ValueKind == JsonValueKind.Object)
            {
                r.Continuous = true;
                if (co.TryGetProperty("stopOn", out var so)) r.StopOn = so.EnumerateArray().Select(x => x.GetString()!).ToHashSet();
                if (co.TryGetProperty("maxMs", out var mx)) r.MaxMs = mx.GetInt32();
                if (co.TryGetProperty("stopWhen", out var sw)) r.StopWhen = sw.EnumerateArray().Select(x => x.GetString()!).ToHashSet();
                if (co.TryGetProperty("stopOnFadeMs",out var sof)) r.StopOnFadeMs=Math.Clamp(sof.GetInt32(),0,5000);
                if (co.TryGetProperty("stopFadeMs", out var sf)) r.StopFadeMs = Math.Clamp(sf.GetInt32(), 0, 1000);
                if (co.TryGetProperty("stopIf", out var si)) r.StopIf = Expr.Compile(si.GetString()!.TrimStart('='));
                if (co.TryGetProperty("untilAction", out var ua)) r.UntilAction = ua.GetString() ?? "";
                if (co.TryGetProperty("loop", out var lo)) r.Loop = lo.GetBoolean();
                if (co.TryGetProperty("fadeMs", out var lf)) r.LoopFadeMs = lf.GetInt32();
            }
        }
        return r;
    }

    static TriggerProfile ParseTriggerProfile(string name, JsonElement e)
    {
        string mode = e.TryGetProperty("mode", out var m) ? (m.GetString() ?? "off").ToLowerInvariant() : "off";
        int I(string k, int def) => e.TryGetProperty(k, out var v) ? v.GetInt32() : def;
        int[]? zones = e.TryGetProperty("zones", out var z) ? z.EnumerateArray().Select(x => x.GetInt32()).ToArray() : null;
        var tp = new TriggerProfile(name, mode, I("pos", 0), I("str", 0), I("amp", 0), I("hz", 0), I("end", 0), zones, I("snap", 0));
        if (mode is not ("off" or "feedback" or "vibration" or "weapon" or "bow" or "zones")) throw new FormatException("mode must be off|feedback|vibration|weapon|bow|zones");
        tp.Bytes();   // range check (throws on pos/str/amp/hz out of range)
        return tp;
    }

    static EffectSpec ParseEffect(JsonElement e)
    {
        var spec = new EffectSpec();
        if (e.TryGetProperty("layers", out var ls)) foreach (var l in ls.EnumerateArray()) spec.Layers.Add(ParseLayer(l));
        else spec.Layers.Add(ParseLayer(e));
        if (e.TryGetProperty("motor", out var mo)) spec.Motor = ParseEffect(mo);
        return spec;
    }

    static Layer ParseLayer(JsonElement e)
    {
        var l = new Layer { Preset = e.TryGetProperty("preset", out var p) ? p.GetString()! : throw new FormatException("layer without 'preset'") };
        foreach (var prop in e.EnumerateObject())
            if (prop.Name is not ("preset" or "layers" or "motor")) l.P[prop.Name] = Param.Parse(prop.Value);
        return l;
    }

    void Validate(Rule r, string where)
    {
        if (r.Path is not ("auto" or "hd" or "rumble")) Errors.Add($"{where}: path must be auto|hd|rumble");
        void CheckSpec(EffectSpec s, string tag)
        {
            foreach (var l in s.Layers)
            {
                if (!Presets.Known.TryGetValue(l.Preset, out var known)) { Errors.Add($"{where}: {tag} unknown preset '{l.Preset}' (known: {string.Join(", ", Presets.Known.Keys)})"); continue; }
                foreach (var k in l.P.Keys)
                    if (!known.Contains(k) && !Presets.Common.Contains(k)) Warnings.Add($"{where}: {tag}{l.Preset} has unknown parameter '{k}'");
            }
            if (s.Motor != null) CheckSpec(s.Motor, "motor ");
        }
        CheckSpec(r.Effect, "");
    }

    /// <summary>Configures a state tracker from the registry (restartable / staleMs / endedBy per type).</summary>
    public void ConfigureTracker(StateTracker t)
    {
        t.Configure(Registry.Values.Where(r => r.Restartable || r.StaleMs > 0 || r.EndedBy.Count > 0).Select(r => (r.Type, r.Restartable, r.StaleMs, (IEnumerable<string>)r.EndedBy)));
        foreach (var r in Registry.Values)
            if (r.Phases.Count > 0 && !r.Phases.Contains("complete") && !r.Phases.Contains("cancel")) t.Instant.Add(r.Type);
    }

    /// <summary>Variables for expressions: event context (numbers parsed), type, phase, seq.</summary>
    public static Expr.Lookup VarsFor(GameEvent ev, Dictionary<string, object?>? extra = null) => name =>
    {
        if (extra != null && extra.TryGetValue(name, out var x)) return x;
        switch (name) { case "type": return ev.Type; case "phase": return ev.Phase; case "seq": return (double)ev.Seq; }
        var v = ev.Get(name);
        if (v == null) return null;
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : v;
    };
}
