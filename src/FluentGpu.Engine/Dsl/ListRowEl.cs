using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace FluentGpu.Dsl;

/// <summary>One cell of a <see cref="ListRowEl"/>'s paint payload: a row-LOCAL rect (pre-computed by the row's own
/// column layout — same job <c>GridEl</c>/<c>RowMetrics</c> already do for the eager grid; this is NOT measured text
/// layout) plus what to draw there. <see cref="Text"/> is shaped by the SAME <c>DrawGlyphRun</c> opcode/backend glyph
/// cache <see cref="TextEl"/> uses (Render/SceneRecorder.cs), so a cell's glyphs reshape only when its (text, style)
/// changes — never once per frame — with no new shaping-cache plumbing. <see cref="ImageSource"/> resolves through
/// the SAME <c>ImageCache</c> pipeline <see cref="ImageEl"/> uses (decode is off-thread + cached).</summary>
public readonly record struct RowCell()
{
    public RowCellKind Kind { get; init; }
    /// <summary>Row-local rect: this cell paints at <c>(rowOrigin.X + Rect.X, rowOrigin.Y + Rect.Y, Rect.W, Rect.H)</c>.
    /// Fixed by the row's own layout (column widths / RowMetrics) — Placeholder does not move it (layout.md's "same
    /// geometry" contract).</summary>
    public RectF Rect { get; init; }
    /// <summary>Text/Glyph content (Glyph = one icon-font character, e.g. <c>Icons.Play</c>).</summary>
    public string? Text { get; init; }
    /// <summary>Image source path/URL — the same unified channel shape as <see cref="ImageEl.Source"/>, resolved once
    /// per distinct source by the shared <c>ImageCache</c> (off-thread decode, cached, residency-tracked).</summary>
    public string? ImageSource { get; init; }
    /// <summary>Text/Glyph foreground, Rect fill, or Image placeholder tint (paints under the decode like
    /// <see cref="ImageEl.Placeholder"/>).</summary>
    public ColorF Color { get; init; }
    /// <summary>Image/Rect corner rounding (ignored for Text/Glyph).</summary>
    public CornerRadius4 Corners { get; init; }
    public TextTrim Trim { get; init; }
    public float FontSize { get; init; } = 12f;
    public ushort FontWeight { get; init; }
    /// <summary>Null = the row's default UI font (Glyph cells set the icon font here, e.g. <c>Tok.Definition.Fonts.Icon</c>).</summary>
    public string? FontFamily { get; init; }
}

/// <summary>A fixed-identity array+count view over ≤8 <see cref="RowCell"/>s — the exact <c>TextSpans</c> shape
/// (Foundation/SpanText.cs), generalized from styled text ranges to independent cells. <see cref="ListRowEl.Cells"/>
/// binds this AS ONE channel; the reconciler copies it into a scene-owned array on write (Reconciler.ListRow.cs),
/// so refilling a reused <see cref="RowCellBuffer"/> on the next recycle can never retroactively mutate a row the
/// scene already committed this frame.</summary>
public readonly struct RowCells(RowCell[] array, int count)
{
    public readonly RowCell[] Array = array;
    public readonly int Count = count;
    public static readonly RowCells Empty = new(System.Array.Empty<RowCell>(), 0);
    public ReadOnlySpan<RowCell> AsSpan() => Array is null ? default : Array.AsSpan(0, Count);
    public static implicit operator RowCells(RowCell[]? array) => array is null ? Empty : new RowCells(array, array.Length);
}

/// <summary>Per-slot scratch (mirrors <c>SpanBuffer</c>): allocated ONCE per slot at template build time (by
/// <c>BoundItemScopeExtensions.Cells</c>, or by hand inside a bound row template, which runs once per slot) and refilled
/// from the <see cref="ListRowEl.Cells"/> thunk on every recycle with zero further allocation (grow-only capacity, never
/// shrinks).</summary>
public sealed class RowCellBuffer
{
    private RowCell[] _array = System.Array.Empty<RowCell>();
    private int _count;
    public void Clear() => _count = 0;
    public void Add(in RowCell cell)
    {
        if (_count >= _array.Length)
        {
            int cap = _array.Length == 0 ? 8 : _array.Length * 2;
            System.Array.Resize(ref _array, cap);
        }
        _array[_count++] = cell;
    }
    public RowCells Current => new(_array, _count);
}

/// <summary>A virtualized list row as ONE scene node (scroll-rework Wave 0.E, scroll-rework-design.md §B.4): a
/// track-row-shaped worth of content (index glyph, cover art, title, badges, artist/album links' plain-text form,
/// date, duration, transport buttons) that a ~90-node/~50-bind <c>GridEl</c> row costs today, recorded as ONE span so
/// 10k–100k visible rows realize in one frame. Up to 8 <see cref="RowCell"/>s; background/hover/selected fill reuse
/// the same generic <c>Fill</c>/<c>HoverFill</c>/<c>PressedFill</c> paint channels every other element already has
/// (Selected rides the Pressed-state channel — rows have no separate pressed visual). <see cref="Placeholder"/>
/// swaps EVERY Text/Image/Glyph cell's draw for a rounded grey bar/box at its own <see cref="RowCell.Rect"/> — same
/// geometry, no relayout, no type swap (recorder-only decision, Reconciler.ListRow.cs / Render/SceneRecorder.cs).
/// Recycling a slot (an <c>ItemsView.CreateBound&lt;T&gt;</c> / <c>Virtual.ListBound</c> row) is exactly ONE signal write:
/// the slot's item signal re-runs the <see cref="Cells"/> thunk (<c>BoundItemScopeExtensions.Cells</c>, which refills the
/// slot's <see cref="RowCellBuffer"/>) plus whichever of <see cref="Placeholder"/>/
/// <see cref="SelectedFill"/>/<see cref="HoverFill"/> are bound — never a remount (<c>IsRecyclable</c> ⇒ true,
/// Reconciler.cs).</summary>
public partial struct ListRowEl : IElementKind<ListRowEl>
{
    public const ushort TypeId = 17;
    static ushort IElementKind<ListRowEl>.Kind => 17;
    public static implicit operator Element(ListRowEl value) => Element.Push(in value);
    public static explicit operator ListRowEl(Element element) => element.As<ListRowEl>();
    public Prop<RowCells> Cells { get; init; }
    public ListRowEl(Prop<RowCells> Cells) { this.Cells = Cells; }
    // ── base channels (the former Element record), per kind ──
    internal ColdRef<ElementCold> _ecold;
    public string? Key { get; init; }
    public string? MorphId { readonly get => _ecold.Value.MorphId; init { if (!EqualityComparer<string?>.Default.Equals(_ecold.Value.MorphId, value)) { var c = _ecold.Value; c.MorphId = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Scroll.Effects.ScrollEffectSpec[] ScrollEffects { readonly get => _ecold.Value.ScrollEffects; init { if (!EqualityComparer<FluentGpu.Scroll.Effects.ScrollEffectSpec[]>.Default.Equals(_ecold.Value.ScrollEffects, value)) { var c = _ecold.Value; c.ScrollEffects = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public string? ScrollScope { readonly get => _ecold.Value.ScrollScope; init { if (!EqualityComparer<string?>.Default.Equals(_ecold.Value.ScrollScope, value)) { var c = _ecold.Value; c.ScrollScope = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public Prop<bool> Visible { get; init; } = true;
    public SkeletonMode SkeletonMode { readonly get => _ecold.Value.SkeletonMode; init { if (!EqualityComparer<SkeletonMode>.Default.Equals(_ecold.Value.SkeletonMode, value)) { var c = _ecold.Value; c.SkeletonMode = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public Element? SkeletonOverride { readonly get => _ecold.Value.SkeletonOverride; init { if (!EqualityComparer<Element?>.Default.Equals(_ecold.Value.SkeletonOverride, value)) { var c = _ecold.Value; c.SkeletonOverride = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Animation.MotionTokenDef? Transition { readonly get => _ecold.Value.Transition; init { if (!EqualityComparer<FluentGpu.Animation.MotionTokenDef?>.Default.Equals(_ecold.Value.Transition, value)) { var c = _ecold.Value; c.Transition = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Animation.MotionTarget? WhileHover { readonly get => _ecold.Value.WhileHover; init { if (!EqualityComparer<FluentGpu.Animation.MotionTarget?>.Default.Equals(_ecold.Value.WhileHover, value)) { var c = _ecold.Value; c.WhileHover = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Animation.MotionTarget? WhilePressed { readonly get => _ecold.Value.WhilePressed; init { if (!EqualityComparer<FluentGpu.Animation.MotionTarget?>.Default.Equals(_ecold.Value.WhilePressed, value)) { var c = _ecold.Value; c.WhilePressed = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Animation.MotionTarget? WhileFocus { readonly get => _ecold.Value.WhileFocus; init { if (!EqualityComparer<FluentGpu.Animation.MotionTarget?>.Default.Equals(_ecold.Value.WhileFocus, value)) { var c = _ecold.Value; c.WhileFocus = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public EnterExit? Enter { readonly get => _ecold.Value.Enter; init { if (!EqualityComparer<EnterExit?>.Default.Equals(_ecold.Value.Enter, value)) { var c = _ecold.Value; c.Enter = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public EnterExit? Exit { readonly get => _ecold.Value.Exit; init { if (!EqualityComparer<EnterExit?>.Default.Equals(_ecold.Value.Exit, value)) { var c = _ecold.Value; c.Exit = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public float Stagger { readonly get => _ecold.Value.Stagger; init { if (!EqualityComparer<float>.Default.Equals(_ecold.Value.Stagger, value)) { var c = _ecold.Value; c.Stagger = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public LayoutTransition? Layout { readonly get => _ecold.Value.Layout; init { if (!EqualityComparer<LayoutTransition?>.Default.Equals(_ecold.Value.Layout, value)) { var c = _ecold.Value; c.Layout = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Scroll.Runtime.ScrollHandle? WheelTarget { readonly get => _ecold.Value.WheelTarget; init { if (!EqualityComparer<FluentGpu.Scroll.Runtime.ScrollHandle?>.Default.Equals(_ecold.Value.WheelTarget, value)) { var c = _ecold.Value; c.WheelTarget = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public float ScrollLineDip { readonly get => _ecold.Value.ScrollLineDip; init { if (!EqualityComparer<float>.Default.Equals(_ecold.Value.ScrollLineDip, value)) { var c = _ecold.Value; c.ScrollLineDip = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public string? RelativeTo { readonly get => _ecold.Value.RelativeTo; init { if (!EqualityComparer<string?>.Default.Equals(_ecold.Value.RelativeTo, value)) { var c = _ecold.Value; c.RelativeTo = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public readonly ushort ElementTypeId => 17;

    /// <summary>Ctor sugar for a plain array — the common unbound case (a static/eager row).</summary>
    public ListRowEl(RowCell[] cells) : this((RowCells)cells) { }

    public float Height { get; init; } = float.NaN;
    public float Width { get; init; } = float.NaN;
    public float MinWidth { get; init; } = float.NaN;
    public float MinHeight { get; init; } = float.NaN;
    public float MaxWidth { get; init; } = float.NaN;
    public float MaxHeight { get; init; } = float.NaN;
    public float Grow { get; init; }
    public float Shrink { get; init; }
    public float Basis { get; init; } = float.NaN;
    public FlexAlign AlignSelf { get; init; } = FlexAlign.Auto;
    /// <summary>ZStack-only horizontal self-placement (CSS <c>justify-self</c>); ignored outside a ZStack — the same
    /// knob every other leaf element (<c>TextEl</c>/<c>ImageEl</c>) carries.</summary>
    public FlexAlign JustifySelf { get; init; } = FlexAlign.Auto;
    public Edges4 Margin { get; init; }

    /// <summary>Resting background (unified channel — static, thunk, or signal, wired exactly like <c>BoxEl.Fill</c>).</summary>
    public Prop<ColorF> Fill { get; init; } = ColorF.Transparent;
    /// <summary>Row hover wash (the generic <c>HoverFill</c> channel — hover-fades via the SAME <c>HoverFade</c>
    /// animation channel/BrushFade cross-fade every other element's HoverFill already gets, so a row hover-highlights
    /// with zero bespoke animation code).</summary>
    public Prop<ColorF> HoverFill { get; init; } = ColorF.Transparent;
    /// <summary>Selected-row fill — reuses the generic <c>PressedFill</c> paint channel/state (rows have no separate
    /// "pressed" visual, so the third state slot every element already carries is repurposed for Selected instead of
    /// adding a fourth paint channel engine-wide).</summary>
    public Prop<ColorF> SelectedFill { get; init; } = ColorF.Transparent;
    public CornerRadius4 Corners { get; init; }

    /// <summary>Placeholder mode (unified channel): true ⇒ the recorder draws every Text/Image/Glyph cell as a
    /// rounded <see cref="PlaceholderColor"/> bar/box at its own rect instead of its real content. Rect cells are
    /// unaffected (already a flat rect). Toggling this rebinds ONE bool — no relayout, no remount.</summary>
    public Prop<bool> Placeholder { get; init; } = false;
    public ColorF PlaceholderColor { get; init; } = ColorF.FromRgba(0x33, 0x33, 0x33);

    /// <summary>Per-cell click, index-resolved (the <c>SpanTextEl.OnSpanClick</c> shape generalized from span index to
    /// cell index): fires with the CURRENT cell index hit-tested against this row's cell rects. Mount-static (a sparse
    /// SceneStore table, not a bound <see cref="Prop{T}"/> channel) — same category as an ordinary click handler.
    /// Hit-testable ONLY over a cell's own rect (InteractionInfo.RowCellsBit) — the gaps between cells fall through to
    /// this row's own row-level click/select handler (the exact SpanLinksBit shape).</summary>
    public Action<int>? OnCellClick { get; init; }
    static void IElementKind<ListRowEl>.FreezeMembers(ref ListRowEl v)
    {
        v._ecold = v._ecold.Frozen();
    }
}
