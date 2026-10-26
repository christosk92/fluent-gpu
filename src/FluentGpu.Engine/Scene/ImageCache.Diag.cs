using System.Globalization;
using System.Text;

namespace FluentGpu.Scene;

/// <summary>The image memory census (diagnostics, UI thread): entries by state, split pinned (<c>Refs &gt; 0</c>, never
/// evicted) from unpinned (evictable, LRU), with their charged bytes, against the budget. Says whether a cache over its cap
/// is over it because something pins the images or because nothing has run the eviction.</summary>
public sealed partial class ImageCache
{
    public string MemoryCensus()
    {
        const double MiB = 1024.0 * 1024.0;
        var rows = new SortedDictionary<string, (int N, long B)>(StringComparer.Ordinal);
        long pinnedB = 0, unpinnedB = 0; int pinnedN = 0, unpinnedN = 0, maxRefs = 0;
        foreach (var e in _byId.Values)
        {
            bool pinned = e.Refs > 0;
            if (e.Refs > maxRefs) maxRefs = e.Refs;
            if (pinned) { pinnedB += e.Bytes; pinnedN++; } else { unpinnedB += e.Bytes; unpinnedN++; }
            string k = e.State + (e.Derived ? " derived" : "") + (pinned ? " pinned" : " unpinned");
            var v = rows.GetValueOrDefault(k); rows[k] = (v.N + 1, v.B + e.Bytes);
        }
        var sb = new StringBuilder(512).Append("image-census entries=").Append(_byId.Count)
            .Append(" used=").Append((UsedBytes / MiB).ToString("0.0", CultureInfo.InvariantCulture))
            .Append(" budget=").Append((_budgetBytes / MiB).ToString("0.0", CultureInfo.InvariantCulture))
            .Append(" pinned=").Append(pinnedN).Append('/').Append((pinnedB / MiB).ToString("0.0", CultureInfo.InvariantCulture))
            .Append(" unpinned=").Append(unpinnedN).Append('/').Append((unpinnedB / MiB).ToString("0.0", CultureInfo.InvariantCulture))
            .Append(" maxRefs=").Append(maxRefs).Append(" (MiB)").AppendLine();
        foreach (var kv in rows) sb.Append("  ").Append(kv.Key).Append(" x").Append(kv.Value.N).Append(' ').Append((kv.Value.B / MiB).ToString("0.00", CultureInfo.InvariantCulture)).AppendLine(" MiB");
        return sb.ToString();
    }
}
