namespace DsBridge.Haptics;

/// <summary>Data-defined small sample variations, selected only after an event actually fires.
/// Cycle groups are shared across rules (e.g. all surfaces advance the same eight-step sequence).
/// Random groups use a seeded PRNG: offline replays are reproducible, live sessions supply a new seed.
/// A montage-scoped random choice remains unchanged across all sections of that action.
/// </summary>
public sealed record VariationSpec(string Mode, int Count, string Group, string Scope);
public sealed class VariationState(int seed=271828)
{
    readonly Random random=new(seed);
    readonly Dictionary<string,int> counters=new(), previous=new(), episodes=new();
    public void BeginMontage(string name)
    {
        foreach(var key in episodes.Keys.Where(k=>k.EndsWith("|"+name,StringComparison.Ordinal)).ToArray())episodes.Remove(key);
    }
    public int Next(VariationSpec spec,GameEvent ev)
    {
        string group=spec.Group;
        string episode=group+"|"+ev.Get("m");
        if(spec.Scope=="montage"&&ev.Get("m")!=null&&episodes.TryGetValue(episode,out var held))return held;
        int index;
        if(spec.Mode=="cycle") {index=counters.GetValueOrDefault(group);counters[group]=(index+1)%spec.Count;}
        else {
            index=random.Next(spec.Count);
            if(spec.Count>1&&previous.TryGetValue(group,out var old)&&index==old)index=(index+1+random.Next(spec.Count-1))%spec.Count;
            previous[group]=index;
        }
        if(spec.Scope=="montage"&&ev.Get("m")!=null)episodes[episode]=index;
        return index;
    }
}
