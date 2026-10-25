using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace FluentGpu.Reconciler;

// ── --fg mount-census (PERF r3 UI): what a navigation mounts, component type by component type ──────────────────────────
//
// The per-navigation mount model is T_mount ≈ n_comp·β_comp + n_node·β_node + n_rerender·β_rr + n_measure·β_m + shaping. This
// census supplies the per-type inputs: how many instances each component type mounted, how many hook cells an instance holds
// after its first render (0 cells and no re-pushed props signal = a STATELESS instance: an element function would do), and
// how many times an instance re-rendered within the frame that mounted it (a write-after-read during mount), with what
// scheduled those re-renders. Cumulative; one summary every 10 s on stderr. Off: one static bool read per mount and render.
public sealed partial class TreeReconciler
{
    /// <summary><c>--fg mount-census</c>.</summary>
    public static bool MountCensusEnabled;

    private sealed class MountCensusRow
    {
        public long Mounted, Stateless, Cells, MountRerenders, LaterRenders;
        public readonly Dictionary<string, int> RerenderCauses = new(StringComparer.Ordinal);
    }

    private static readonly Dictionary<Type, MountCensusRow> s_mountCensus = new();
    private static long s_mountCensusLastDump = Environment.TickCount64;
    private readonly Dictionary<CompEntry, int> _mountFrameOf = new(ReferenceEqualityComparer.Instance);

    private static MountCensusRow CensusRow(Type t)
    {
        if (!s_mountCensus.TryGetValue(t, out var row)) s_mountCensus[t] = row = new MountCensusRow();
        return row;
    }

    /// <summary>A component instance was mounted this frame.</summary>
    private void NoteCensusMount(CompEntry entry)
    {
        CensusRow(entry.Comp.GetType()).Mounted++;
        _mountFrameOf[entry] = FrameEpoch;
    }

    /// <summary>A component instance finished a render: its first (record its cells, statelessness) or a later one (in the frame
    /// it mounted = a mount re-render, with its cause; afterwards = an ordinary later render).</summary>
    private void NoteCensusRender(CompEntry entry, bool first, ISignalSource? cause)
    {
        var row = CensusRow(entry.Comp.GetType());
        if (first)
        {
            int cells = entry.Comp.Context.CellCount;
            row.Cells += cells;
            if (cells == 0 && entry.PropsSig is null) row.Stateless++;
        }
        else if (_mountFrameOf.TryGetValue(entry, out int frame) && frame == FrameEpoch)
        {
            row.MountRerenders++;
            string why = cause is null ? "sched" : ReferenceEquals(cause, entry.PropsSig) ? "props" : SignalDiag.Describe(cause);
            row.RerenderCauses[why] = row.RerenderCauses.GetValueOrDefault(why) + 1;
        }
        else
        {
            row.LaterRenders++;
            _mountFrameOf.Remove(entry);
        }
        long now = Environment.TickCount64;
        if (now - s_mountCensusLastDump < 10_000) return;
        s_mountCensusLastDump = now;
        DumpMountCensus();
    }

    private static void DumpMountCensus()
    {
        long mounted = 0, stateless = 0, rerenders = 0;
        foreach (var r in s_mountCensus.Values) { mounted += r.Mounted; stateless += r.Stateless; rerenders += r.MountRerenders; }
        var sb = new System.Text.StringBuilder(4096);
        sb.Append("[mount-census] mounted=").Append(mounted).Append(" stateless=").Append(stateless)
          .Append(" mountRerenders=").Append(rerenders).Append(" types=").Append(s_mountCensus.Count).Append('\n');
        foreach (var kv in s_mountCensus.OrderByDescending(k => k.Value.Mounted).Take(60))
        {
            var r = kv.Value;
            sb.Append("  ").Append(kv.Key.Name).Append(" mounted=").Append(r.Mounted).Append(" stateless=").Append(r.Stateless)
              .Append(" cells/inst=").Append(r.Mounted == 0 ? 0 : Math.Round((double)r.Cells / r.Mounted, 2))
              .Append(" mountRerenders=").Append(r.MountRerenders).Append(" later=").Append(r.LaterRenders);
            if (r.RerenderCauses.Count > 0)
            {
                sb.Append(" by:");
                foreach (var c in r.RerenderCauses.OrderByDescending(c => c.Value).Take(3)) sb.Append(' ').Append(c.Key).Append('=').Append(c.Value);
            }
            sb.Append('\n');
        }
        Console.Error.Write(sb.ToString());
    }
}
