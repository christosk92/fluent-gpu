using System.Globalization;
using FluentGpu.Foundation;

namespace FluentGpu.Scene;

/// <summary>
/// Readable node names for logs and evidence exports (docs/plans/evidence-diagnostics-implementation.md §A.5). The render
/// thread only ever stores (index, gen); the UI thread resolves them here: the nearest KEYED ancestor-or-self's
/// <c>Element.Key</c> (recorded at mount, <see cref="SceneStore.DebugKeyOf"/>) and the child-index path below it —
/// <c>artist-under-band/1/0/3</c>, or <c>#root/…</c> when no ancestor is keyed. A handle that is no longer live at that
/// generation reads <c>gone:&lt;index&gt;</c>. Zero allocation (writes into the caller's span, truncating).
/// </summary>
public static class NodeDescriber
{
    private const int MaxDepth = 64;

    public static int Describe(SceneStore scene, int nodeIndex, uint gen, Span<char> dst)
    {
        var w = new Writer(dst);
        var h = scene.HandleAt(nodeIndex);
        if (nodeIndex <= 0 || h.IsNull || h.Raw.Gen != gen || !scene.IsLive(h))
        {
            w.Append("gone:");
            w.Append(nodeIndex);
            return w.Count;
        }
        Span<int> path = stackalloc int[MaxDepth];
        int depth = 0;
        string? key = null;
        for (var n = h; !n.IsNull; n = scene.Parent(n))
        {
            if (scene.DebugKeyOf(n) is { } k) { key = k; break; }
            var parent = scene.Parent(n);
            if (parent.IsNull) break;   // the root itself: "#root"
            if (depth < MaxDepth) path[depth++] = IndexInParent(scene, parent, n);
        }
        w.Append(key ?? "#root");
        for (int i = depth - 1; i >= 0; i--)
        {
            w.Append('/');
            w.Append(path[i]);
        }
        return w.Count;
    }

    private static int IndexInParent(SceneStore scene, NodeHandle parent, NodeHandle child)
    {
        int i = 0;
        for (var c = scene.FirstChild(parent); !c.IsNull; c = scene.NextSibling(c), i++)
            if (c == child) return i;
        return -1;
    }

    /// <summary>The element kind a scene node was mounted from (<c>Element.ElementTypeId</c>).</summary>
    public static string ElementTypeName(ushort id) => id switch
    {
        1 => "Box",
        2 => "Text",
        3 => "Component",
        4 => "Provider",
        5 => "Scroll",
        6 => "VirtualList",
        7 => "Show",
        8 => "Image",
        9 => "Grid",
        10 => "For",
        11 => "PolylineStroke",
        12 => "SpanText",
        13 => "SkeletonRegion",
        14 => "KeepAlive",
        15 => "IconLayer",
        16 => "Path",
        17 => "ListRow",
        _ => "T" + id.ToString(CultureInfo.InvariantCulture),
    };

    private ref struct Writer
    {
        private readonly Span<char> _dst;
        public int Count;

        public Writer(Span<char> dst) { _dst = dst; Count = 0; }

        public void Append(char c)
        {
            if (Count < _dst.Length) _dst[Count++] = c;
        }

        public void Append(string s)
        {
            int n = Math.Min(s.Length, _dst.Length - Count);
            if (n <= 0) return;
            s.AsSpan(0, n).CopyTo(_dst[Count..]);
            Count += n;
        }

        public void Append(int v)
        {
            if (v.TryFormat(_dst[Count..], out int written, default, CultureInfo.InvariantCulture)) Count += written;
        }
    }
}
