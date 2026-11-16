using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>What a realized item knows about its box. Written when a pack changes it (an edge), never per frame.</summary>
public readonly record struct PackBox(int Cols, float Rows, float Width, float Height, float ToolStrip);

/// <summary>A realized item's state in its grid. <see cref="Provisional"/>: a foreign drag's item.</summary>
public enum PackItemPhase : byte { Rest, Lifted, KeyboardLifted, Provisional }

/// <summary>The per-item handle a <see cref="PackGrid"/> template gets: its key, its packed box and its lift phase as signals.</summary>
public sealed class PackCell
{
    internal readonly Signal<PackBox> BoxSig = new(default);
    internal readonly Signal<PackItemPhase> PhaseSig = new(PackItemPhase.Rest);

    internal PackCell(string key) => Key = key;

    public string Key { get; }
    public IReadSignal<PackBox> Box => BoxSig;
    public IReadSignal<PackItemPhase> Phase => PhaseSig;
}

/// <summary>Options of a <see cref="PackGrid"/>. The delegates are read at pack time; the grid re-packs when a signal they read
/// changes.</summary>
public sealed record PackGridOptions<T>
{
    /// <summary>Stable item identity; also the item wrapper's node key.</summary>
    public required Func<T, string> KeyOf { get; init; }
    /// <summary>Read at pack time: reads signals, re-packs when they change.</summary>
    public required Func<T, PackEntry> EntryOf { get; init; }
    /// <summary>From the theme's tier record; read at pack time.</summary>
    public required Func<PackMetrics> Metrics { get; init; }
    public LayoutTransition Reflow { get; init; } = PackGrid.Reflow;
    /// <summary>An item ADDED while mounted; an item the window reaches mounts without it.</summary>
    public EnterExit Enter { get; init; } = PackGrid.FadeIn;
    public EnterExit Exit { get; init; } = PackGrid.FadeOut;
    /// <summary>The ancestor scroller; null: the nearest (<c>UseScroll()</c>).</summary>
    public ScrollHandle? Scroll { get; init; }
    /// <summary>Realized area beyond the viewport on each side, in viewports; widened by the scroll velocity.</summary>
    public float OverscanViewports { get; init; } = 1f;
    public PackGridController? Controller { get; init; }
    public PackReorder<T>? Reorder { get; init; }
    /// <summary>Classic: tiles take focus only in Edit Home. Null: tiles are focusable.</summary>
    public Func<bool>? ItemsFocusable { get; init; }
    /// <summary>Arrows move FOCUS between tiles (<see cref="PackLayout.Nearest"/>).</summary>
    public bool ArrowNavigation { get; init; } = true;
    public Func<T, PackAutomation>? Automation { get; init; }
    /// <summary>[first, last) of the realized entries, on change: the live-budget gate reads it.</summary>
    public Action<int, int>? OnRealizedRange { get; init; }
    /// <summary>The grid's own node key.</summary>
    public string? Key { get; init; }
}

/// <summary>What a tile announces itself as.</summary>
public readonly record struct PackAutomation(string Name, AutomationRole Role, int PositionInSet, int SizeOfSet);

/// <summary>
/// A bento grid of keyed items placed at <see cref="PackLayout"/> rects inside the page's own scroller: a natural-height box
/// (not a scroller) that realizes only the bands near the ancestor viewport, and animates every rect change with one FLIP
/// re-flow. See <c>design/subsystems/pack-grid.md</c>.
/// </summary>
public static class PackGrid
{
    /// <summary>Bounds, a 250 ms tween on cubic-bezier(0.25, 1, 0.5, 1), SizeMode.Reveal. Reduced motion: a 120 ms fade.</summary>
    public static LayoutTransition Reflow => Motion.ReducedMotion
        ? new LayoutTransition(TransitionChannels.Opacity, TransitionDynamics.Tween(120f, EasingSpec.Named(Easing.Linear)), SizeMode.Reveal)
        : new LayoutTransition(TransitionChannels.Bounds, TransitionDynamics.Tween(250f, EasingSpec.CubicBezier(0.25f, 1f, 0.5f, 1f)), SizeMode.Reveal);

    public static EnterExit FadeIn => new(Opacity: 0f, Sx: Motion.ReducedMotion ? 1f : 0.96f, Sy: Motion.ReducedMotion ? 1f : 0.96f, Active: true);
    public static EnterExit FadeOut => new(Opacity: 0f, Sx: Motion.ReducedMotion ? 1f : 0.96f, Sy: Motion.ReducedMotion ? 1f : 0.96f, Active: true);

    public static Element Create<T>(IReadSignal<IReadOnlyList<T>> items, Func<T, PackCell, Element> template,
        PackGridOptions<T> options)
        => Embed.Comp(new PackGridView<T>.Props(items, template, options), () => new PackGridView<T>()) with { Key = options.Key };
}

/// <summary>The app's handle on a mounted <see cref="PackGrid"/>: the live pack, rect and neighbour queries, focus and
/// bring-into-view. Create one, pass it in <see cref="PackGridOptions{T}.Controller"/>.</summary>
public sealed class PackGridController
{
    internal readonly PackResult Live = new();
    internal readonly Signal<int> Version = new(0);
    internal IPackGridHost? Host;

    /// <summary>The live pack, read-only.</summary>
    public PackResult Layout => Live;
    public IReadSignal<int> LayoutVersion => Version;

    /// <summary>The packed rect of <paramref name="key"/> in grid space.</summary>
    public bool TryRect(string key, out RectF rect)
    {
        rect = default;
        if (Host is null) return false;
        int i = Host.IndexOf(key);
        if (i < 0 || i >= Live.Count || Live.BandOf[i] < 0) return false;
        rect = Live.Rects[i];
        return true;
    }

    public string? KeyAt(float x, float y)
    {
        if (Host is null) return null;
        int i = PackLayout.HitTest(Live, x, y);
        return i < 0 ? null : Host.KeyAt(i);
    }

    public string? Neighbour(string key, int dx, int dy, bool tilesOnly = true)
    {
        if (Host is null) return null;
        int i = Host.IndexOf(key);
        if (i < 0) return null;
        int j = PackLayout.Nearest(Live, Host.Entries, i, dx, dy, tilesOnly);
        return j < 0 ? null : Host.KeyAt(j);
    }

    public bool IsRealized(string key) => Host?.IsRealized(key) ?? false;

    public void BringIntoView(string key, ScrollMove move = ScrollMove.Glide, float margin = 0f) => Host?.BringIntoView(key, move, margin);

    public void Focus(string key) => Host?.Focus(key);

    /// <summary><see cref="PackLayout.Verify"/> over the live pack.</summary>
    public int Verify(Span<PackFault> faults) => Host is null ? 0 : PackLayout.Verify(Host.Entries, Host.Metrics, Live, faults);

    /// <summary>The mounted grid's metrics and width (the inspector's lint snapshot reads them with <see cref="Entries"/>).</summary>
    public PackMetrics Metrics => Host?.Metrics ?? default;
    /// <summary>The entries of the live pack, in order.</summary>
    public ReadOnlySpan<PackEntry> Entries => Host is null ? default : Host.Entries;
    /// <summary>The item key of entry <paramref name="index"/>, or null.</summary>
    public string? KeyOf(int index) => Host?.KeyAt(index);
}

/// <summary>What a <see cref="PackGridController"/> asks of the mounted grid.</summary>
internal interface IPackGridHost
{
    int IndexOf(string key);
    string? KeyAt(int index);
    ReadOnlySpan<PackEntry> Entries { get; }
    PackMetrics Metrics { get; }
    bool IsRealized(string key);
    void BringIntoView(string key, ScrollMove move, float margin);
    void Focus(string key);
}

/// <summary>The grid component (see <see cref="PackGrid"/>).</summary>
internal sealed class PackGridView<T> : Component, IPackGridHost, IPackReorderHost
{
    public sealed record Props(IReadSignal<IReadOnlyList<T>> Items, Func<T, PackCell, Element> Template, PackGridOptions<T> Options);

    readonly Signal<float> _width = new(0f);
    readonly Action<RectF> _onBounds;
    readonly PackResult _own = new();
    PackEntry[] _entries = [];
    string[] _keys = [];
    int _count;
    PackMetrics _metrics;
    readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
    readonly Dictionary<string, PackCell> _cells = new(StringComparer.Ordinal);
    Props? _p;

    public PackGridView()
    {
        _onBounds = r => { if (r.W > 0f && MathF.Abs(r.W - _width.Peek()) > 0.25f) _width.Value = r.W; };
    }

    PackResult Result => _p?.Options.Controller?.Live ?? _own;

    public override Element Render()
    {
        var p = UseProps<Props>();
        _p = p;
        var o = p.Options;
        if (o.Controller is { } c) c.Host = this;
        if (o.Reorder is { } ro) ro.Host = this;
        var items = p.Items.Value;
        float width = _width.Value;
        var metrics = o.Metrics();
        _metrics = metrics;
        int n = items.Count;
        if (_entries.Length < n) { Array.Resize(ref _entries, Math.Max(n, _entries.Length * 2)); Array.Resize(ref _keys, _entries.Length); }
        _index.Clear();
        for (int i = 0; i < n; i++)
        {
            _entries[i] = o.EntryOf(items[i]);
            _keys[i] = o.KeyOf(items[i]);
            _index[_keys[i]] = i;
        }
        _count = n;
        var r = Result;
        PackLayout.Pack(_entries.AsSpan(0, n), metrics, width, r);
        var children = new List<Element>(n);
        var rects = r.Rects;
        for (int i = 0; i < n; i++)
        {
            if ((_entries[i].Flags & PackFlags.Hidden) != 0 || width <= 0f) continue;
            string key = _keys[i];
            if (!_cells.TryGetValue(key, out var cell)) _cells[key] = cell = new PackCell(key);
            var q = rects[i];
            cell.BoxSig.Value = new PackBox(r.SpanCols[i], r.SpanRows[i], q.W, q.H, r.ToolStripOf[i]);
            children.Add(new BoxEl
            {
                Key = key,
                Width = q.W, Height = q.H,
                Margin = new Edges4(q.X, q.Y, 0f, 0f),
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                Grow = 0f, Shrink = 0f, ClipToBounds = true,
                Direction = 1,
                Animate = o.Reflow,
                Children = [p.Template(items[i], cell)],
            });
        }
        if (o.Controller is { } ctl) ctl.Version.Value = ctl.Version.Peek() + 1;
        return new BoxEl
        {
            ZStack = true,
            Height = r.Height,
            IsolateLayout = true,
            OnBoundsChanged = _onBounds,
            Children = children.ToArray(),
        };
    }

    public int IndexOf(string key) => _index.TryGetValue(key, out int i) ? i : -1;
    public string? KeyAt(int index) => (uint)index < (uint)_count ? _keys[index] : null;
    public ReadOnlySpan<PackEntry> Entries => _entries.AsSpan(0, _count);
    public PackMetrics Metrics => _metrics;
    public bool IsRealized(string key) => _index.ContainsKey(key);
    public void BringIntoView(string key, ScrollMove move, float margin) { }
    public void Focus(string key) { }

    // reorder (inert in the API stub)
    public bool Lift(string key) => false;
    public bool Drop() => false;
    public bool Cancel() => false;
    public bool MoveBy(string key, int delta) => false;
    public bool MoveToward(string key, int dx, int dy) => false;
    public string? LiftedKey => null;
}
