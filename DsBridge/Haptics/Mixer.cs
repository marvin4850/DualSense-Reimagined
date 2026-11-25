namespace DsBridge.Haptics;

/// <summary>How a voice joins the mix.</summary>
public sealed record VoiceOpts(int Priority = 1, string Tag = "", string Group = "", string GroupMode = "mix", int FadeMs = 250,
                               int FadeInMs = 0, float DuckOthers = 1f, bool Loop = false);

/// <summary>Voice mixer shared by the live HD stream and offline replay. Safety rules:
/// - groups: "mix" (sum), "crossfade" (a new voice fades the group's playing voices out over FadeMs), "exclusive"
///   (a new voice is rejected while a same-group voice of higher-or-equal priority plays, otherwise it replaces it);
/// - at most MaxVoices voices: a new voice replaces the oldest voice of lower-or-equal priority, otherwise it is rejected;
/// - ducking: lower-priority voices x DuckLower while a higher-priority voice plays; a voice with DuckOthers &lt; 1 ducks all others;
/// - soft limiter: transparent up to Knee x Ceiling, smooth tanh knee to Ceiling (a single approved effect passes almost unchanged);
/// - overload guard: if the summed signal exceeds Ceiling x 1.15 in a block, the master is lowered (x0.9 per block, floor
///   GuardFloor) and recovers slowly - prevents permanent limiting when many effects pile up;
/// - Stop(id) fades a voice out (continuous effects), Clear() stops everything at once.</summary>
public sealed class HapticMixer
{
    sealed class Voice
    {
        public long Id; public float[] L = Array.Empty<float>(), R = Array.Empty<float>(); public int Pos, Len;
        public int Priority; public string Tag = "", Group = ""; public int FadeLeft = -1, FadeLen, FadeInLen; public long Order; public float DuckOthers = 1f;
        public bool Loop, Looped;
    }
    readonly List<Voice> _v = new(); readonly object _l = new(); long _nextId = 1, _order;

    public int MaxVoices = 8; public float DuckLower = 0.5f, Ceiling = 0.95f, Knee = 0.9f, MasterGain = 1f, GuardFloor = 0.6f;
    public int Rate = 48000;
    float _guard = 1f;
    // statistics
    public long Started, Rejected, Replaced, Crossfaded, GroupRejected, LimitedSamples, RenderedSamples, OverloadBlocks, GroupOverlapBlocks, Blocks;
    public int MaxConcurrent; public float PeakPre, PeakOut, GuardMin = 1f;
    public readonly List<string> Overlaps = new();   // first episodes of same-group overlap / overload (for reports)
    public long NowMs;                                // set by the caller (replay) for episode timestamps

    public bool Active { get { lock (_l) return _v.Count > 0; } }
    public int Count { get { lock (_l) return _v.Count; } }
    public bool IsPlaying(long id) { lock (_l) return _v.Any(x => x.Id == id); }

    public long Add(float[] l, float[] r, int priority, string tag) => Add(l, r, new VoiceOpts(priority, tag));

    public long Add(float[] l, float[] r, VoiceOpts o)
    {
        lock (_l)
        {
            bool replacing = false;
            if (o.Group.Length > 0 && o.GroupMode is "crossfade" or "exclusive")
            {
                var same = _v.Where(x => x.Group == o.Group && x.FadeLeft < 0).ToList();
                replacing = same.Count > 0;
                if (o.GroupMode == "exclusive" && same.Any(x => x.Priority >= o.Priority)) { GroupRejected++; return 0; }
                int fade = Math.Max(1, Rate * o.FadeMs / 1000);
                foreach (var x in same) { x.FadeLeft = x.FadeLen = fade; Crossfaded++; }
            }
            if (_v.Count >= MaxVoices)
            {
                var victim = _v.Where(x => x.Priority <= o.Priority).OrderBy(x => x.Priority).ThenBy(x => x.Order).FirstOrDefault();
                if (victim == null) { Rejected++; return 0; }
                _v.Remove(victim); Replaced++;
            }
            var v = new Voice { Id = _nextId++, L = l, R = r, Len = Math.Max(l.Length, r.Length), Priority = o.Priority, Tag = o.Tag, Group = o.Group,
                                Order = _order++, DuckOthers = o.DuckOthers, Loop = o.Loop,
                                FadeInLen = replacing || o.Loop ? Rate * Math.Max(0, o.FadeInMs) / 1000 : 0 };   // fade-in when it replaces a voice, or a loop starts   // fade-in only when it replaces a playing voice
            _v.Add(v); Started++;
            if (_v.Count > MaxConcurrent) MaxConcurrent = _v.Count;
            return v.Id;
        }
    }

    public void Stop(long id, int fadeSamples)
    {
        lock (_l)
        {
            var v = _v.FirstOrDefault(x => x.Id == id);
            if (v == null) return;
            if (fadeSamples <= 0) { _v.Remove(v); return; }
            if (v.FadeLeft < 0) { v.FadeLeft = v.FadeLen = fadeSamples; }
        }
    }

    public void Clear() { lock (_l) _v.Clear(); }

    /// <summary>Mixes `frames` samples into outL/outR (overwritten).</summary>
    public void Render(Span<float> outL, Span<float> outR, int frames)
    {
        outL[..frames].Clear(); outR[..frames].Clear();
        Blocks++;
        lock (_l)
        {
            if (_v.Count > 0)
            {
                int top = _v.Max(x => x.Priority);
                float minDuck = _v.Min(x => x.DuckOthers);
                var overlapGroups = _v.Where(x => x.Group.Length > 0 && x.FadeLeft < 0).GroupBy(x => x.Group).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                if (overlapGroups.Count > 0)
                {
                    GroupOverlapBlocks++;
                    if (Overlaps.Count < 20 && (Overlaps.Count == 0 || !Overlaps[^1].StartsWith($"{NowMs / 1000}s")))
                        Overlaps.Add($"{NowMs / 1000}s group overlap: {string.Join(",", overlapGroups)}");
                }
                for (int vi = _v.Count - 1; vi >= 0; vi--)
                {
                    var x = _v[vi];
                    float g = MasterGain * _guard * (x.Priority < top ? DuckLower : 1f);
                    if (minDuck < 1f && x.DuckOthers >= 1f) g *= minDuck;   // ducked by another voice
                    for (int i = 0; i < frames && x.Pos < x.Len; i++, x.Pos++)
                    {
                        float f = g;
                        if (x.FadeInLen > 0 && !x.Looped && x.Pos < x.FadeInLen) f *= (float)x.Pos / x.FadeInLen;
                        if (x.FadeLeft >= 0) { if (x.FadeLeft == 0) { x.Pos = x.Len; break; } f *= (float)x.FadeLeft / Math.Max(1, x.FadeLen); x.FadeLeft--; }
                        if (x.Pos < x.L.Length) outL[i] += x.L[x.Pos] * f;
                        if (x.Pos < x.R.Length) outR[i] += x.R[x.Pos] * f;
                        if (x.Loop && x.FadeLeft < 0 && x.Pos == x.Len - 1) { x.Pos = -1; x.Looped = true; }   // seamless wrap (loop until stopped)
                    }
                    if (x.Pos >= x.Len) _v.RemoveAt(vi);
                }
            }
        }
        float knee = Knee * Ceiling, room = Ceiling - knee, blockPeak = 0;
        for (int i = 0; i < frames; i++)
        {
            float a = Math.Max(Math.Abs(outL[i]), Math.Abs(outR[i]));
            if (a > blockPeak) blockPeak = a;
            outL[i] = Limit(outL[i]); outR[i] = Limit(outR[i]);
        }
        RenderedSamples += frames;
        // overload guard
        if (blockPeak > Ceiling * 1.15f)
        {
            OverloadBlocks++;
            _guard = Math.Max(GuardFloor, _guard * 0.9f);
            if (_guard < GuardMin) GuardMin = _guard;
            if (Overlaps.Count < 20) Overlaps.Add($"{NowMs / 1000}s overload peak {blockPeak:F2} -> guard {_guard:F2}");
        }
        else _guard = Math.Min(1f, _guard + 0.01f);

        float Limit(float x)
        {
            float a = Math.Abs(x);
            if (a > PeakPre) PeakPre = a;
            if (a <= knee) { if (a > PeakOut) PeakOut = a; return x; }
            LimitedSamples++;
            float y = knee + room * (float)Math.Tanh((a - knee) / room);
            if (y > PeakOut) PeakOut = y;
            return Math.Sign(x) * y;
        }
    }

    public double LimitedPercent => RenderedSamples > 0 ? 100.0 * LimitedSamples / (2.0 * RenderedSamples) : 0;

    public string StatsLine() =>
        $"voices started={Started} replaced={Replaced} rejected={Rejected} crossfaded={Crossfaded} group_rejected={GroupRejected} max_concurrent={MaxConcurrent} " +
        $"peak_pre={PeakPre:F2} peak_out={PeakOut:F2} limited={LimitedPercent:F2}% overload_blocks={OverloadBlocks} guard_min={GuardMin:F2} group_overlap_blocks={GroupOverlapBlocks}";
}
