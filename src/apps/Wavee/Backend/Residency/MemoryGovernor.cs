using System;
using System.Collections.Generic;
using System.Linq;

namespace Wavee.Backend.Residency;

public enum MemoryPressure { Normal, Moderate, Critical }

// ── The cross-arena shedding coordinator (the plan's MemoryGovernor) ─────────────────────────────────────────────────
// Arenas register a shed action with a priority; Trim() sheds them in priority order, escalating with OS memory pressure,
// so a higher-pressure level sheds everything a lower one does PLUS more — and pinned working sets (rootlist, set indexes,
// visible-row entities/art) survive because they are simply not registered as sheddable. The concrete arenas plug in at
// the app level (engine ImageCache prefetch-lane drop / EvictToBudget; WARM membership demotion; unpinned entity drop);
// the ordering + escalation are the governor's, and unit-tested here without the GPU.
public sealed class MemoryGovernor
{
    readonly List<(int Priority, string Name, Func<long> Shed)> _arenas = new();

    /// <summary>Register a sheddable arena. <paramref name="priority"/> 1 = cheapest/first (prefetch art), higher = shed
    /// only under greater pressure (2 = WARM demotion, 3 = unpinned entities, 4 = emergency clear). <paramref name="shed"/>
    /// returns the bytes it freed.</summary>
    public void Register(int priority, string name, Func<long> shed) => _arenas.Add((priority, name, shed));

    /// <summary>Shed every arena whose priority is within the pressure level (Normal=1, Moderate=2, Critical=4), in
    /// ascending priority order. Returns total bytes freed.</summary>
    public long Trim(MemoryPressure level)
    {
        int maxPriority = level switch
        {
            MemoryPressure.Normal => 1,
            MemoryPressure.Moderate => 2,
            MemoryPressure.Critical => 4,
            _ => 1,
        };
        long freed = 0;
        foreach (var arena in _arenas.OrderBy(a => a.Priority))
            if (arena.Priority <= maxPriority)
                freed += arena.Shed();
        return freed;
    }
}
