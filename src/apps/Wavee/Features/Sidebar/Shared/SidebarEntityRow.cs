using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core.Sidebar;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// F.1.4 — THE ONE sidebar entity row. Classic's pinned rows, Library V3's list rows and Curated's entity rows all come
// out of Create(in SidebarRowSpec) so the three designs cannot drift apart visually.
//
// WHAT IT OWNS (and why nothing else may re-implement it):
//
//  • The 4-STATE SELECTION-AWARE HOVER/PRESS RAMP — the stock NavigationViewItem backplate ladder
//    (NavigationView.cs:1174-1176 / NavigationView_themeresources): rest Transparent · Selected=Secondary; hover
//    Secondary · SelectedPointerOver=Tertiary; pressed Tertiary · SelectedPressed=Secondary. A selected row must DARKEN
//    on hover, never flatten into its own rest fill. This is copied verbatim from the landed WaveeSidebar rows; the whole
//    reason this factory exists is that the ramp is written ONCE.
//  • The height ladder by density, the 3-DIP selection gutter (the pill's reserve — see SidebarSelectionPill), the depth
//    indent, and the slot order.
//  • OnRealized CHAINING for the selection pill: a row both registers its node for the pill AND can carry a caller's own
//    realize handler; ContextMenu.Attach chains onto whatever this leaves behind (it never clobbers).
//
// PURE STATIC by design: a Component per row would cost a mount per slot in a virtualized 10k list. Nothing here
// allocates beyond the returned records and the children array.

/// <summary>Row-height + metric rules, split out so a virtualizing surface can size a slot WITHOUT building the row.
///
/// <para>The height / indent ARITHMETIC is not here: it lives in <see cref="SidebarRowGeometry"/> (engine-free, and
/// therefore the only version a test can reach — this file is engine-bound and is deliberately not source-included by
/// Wavee.Tests). These members forward, so there is still ONE ladder; only the art ladder, which needs the engine-side
/// <see cref="SidebarCover"/> sizes, is owned here.</para></summary>
static class SidebarRowMetrics
{
    /// <inheritdoc cref="SidebarRowGeometry.ClassicHeight"/>
    public const float ClassicHeight = SidebarRowGeometry.ClassicHeight;

    /// <inheritdoc cref="SidebarRowGeometry.HeightFor(SidebarDensity, bool)"/>
    public static float HeightFor(SidebarDensity density, bool hasSubtitle)
        => SidebarRowGeometry.HeightFor(density, hasSubtitle);

    /// <summary>Art size by density — the art slot shrinks with the row so the 3-DIP gutter and the text baseline stay put.</summary>
    public static float ArtFor(SidebarDensity density) => density switch
    {
        SidebarDensity.Compact => SidebarCover.S20,
        SidebarDensity.Comfortable => SidebarCover.S40,
        _ => SidebarCover.S32,
    };

    /// <inheritdoc cref="SidebarRowGeometry.IndentFor"/>
    public static float IndentFor(int depth) => SidebarRowGeometry.IndentFor(depth);

    /// <inheritdoc cref="SidebarRowGeometry.SubtitleVisible"/>
    public static bool SubtitleVisible(SidebarDensity density, string? subtitle)
        => SidebarRowGeometry.SubtitleVisible(density, subtitle);
}

/// <summary>
/// Everything <see cref="SidebarEntityRow.Create"/> needs. A mutable struct with public fields (the
/// <c>InteractionRecipe</c> precedent): a caller fills the slots it cares about with an object initializer and passes it
/// by <c>in</c>, so a row costs no allocation beyond its elements.
///
/// <para><b>Struct-default caveat</b> (same as <c>InteractionRecipe</c>): the field defaults below apply only through the
/// parameterless constructor — always write <c>new SidebarRowSpec { … }</c>, never <c>default</c>.</para>
/// </summary>
struct SidebarRowSpec
{
    // EVERY field is assigned here, explicitly: the non-nullable string fields would otherwise trip CS8618 and the rest
    // would rely on C# 11's auto-default — and this file builds under TreatWarningsAsErrors.
    public SidebarRowSpec()
    {
        Key = "";
        Label = "";
        Subtitle = null;
        Selected = false;
        Enabled = true;
        Depth = 0;
        TreeNode = false;
        TreeDepth = 0;
        TreeContinuationMask = 0;
        Density = SidebarDensity.Cozy;
        Gap = float.NaN;
        Height = float.NaN;
        ArtSize = float.NaN;
        Leading = null;
        Glyph = null;
        LeadingChevron = null;
        Trailing = null;
        Playing = false;
        PlayingAnimated = false;
        Track = false;
        Overflow = false;
        OnClick = null;
        OnRealized = null;
        MenuOverlay = null;
        Menu = null;
        Drag = null;
        DropTarget = null;
        Animate = null;
        Caption = null;
        Focusable = false;
    }

    // ── identity ─────────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>REQUIRED. The reconciler key AND the key the caller registers this row's node under for the selection
    /// pill. Must be stable per item (an entry/pin id or a route key) — never an index.</summary>
    public string Key;

    /// <summary>REQUIRED. The row's title. This is also its accessible name (the engine has no separate automation-name
    /// channel; the text under the cursor IS the announced name).</summary>
    public string Label;

    /// <summary>The second line (track count / kind · creator / item count). Ignored at <see cref="SidebarDensity.Compact"/>.</summary>
    public string? Subtitle;

    // ── state ────────────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>Drives the 4-state ramp (rest/hover/pressed all shift when selected). Compute it from the live route.</summary>
    public bool Selected;

    /// <summary>False dims the row and drops its ramp — the missing-entity / unavailable-extension retention state.</summary>
    public bool Enabled;

    /// <summary>Nesting depth (rootlist folders / Curated groups). 12 DIP per level, clamped at 4.</summary>
    public int Depth;

    /// <summary>True for a PlaylistTree row. Tree rows reserve one stable disclosure column so sibling artwork aligns
    /// whether the row is a folder or a leaf.</summary>
    public bool TreeNode;

    /// <summary>Depth inside the playlist tree itself (separate from <see cref="Depth"/>, which may also include a
    /// containing CustomGroup). Each level is drawn as a connector cell instead of anonymous left padding.</summary>
    public int TreeDepth;

    /// <summary>One bit per tree level: a set bit means that level has a later sibling and its vertical connector must
    /// continue through this row. The current level always draws an elbow and stops at its midpoint when its bit is clear.</summary>
    public byte TreeContinuationMask;

    /// <summary>Row height + art size ladder.</summary>
    public SidebarDensity Density;

    /// <summary>Main-axis gap. NaN ⇒ 10 with an art slot, 12 with a bare glyph (the landed Classic metrics).</summary>
    public float Gap;

    /// <summary>PIN the row height instead of deriving it from <see cref="Density"/> + subtitle presence. NaN ⇒ derive.
    /// Set it whenever a section must be UNIFORM regardless of which rows happen to carry a subtitle — a
    /// <c>Reorderable</c>'s slot pitch and a virtualizing host's extent both assume one height per section, and a mixed
    /// 40/44 list silently breaks their geometry. (Classic's pinned section pins it to
    /// <see cref="SidebarRowMetrics.ClassicHeight"/> for exactly that reason.)</summary>
    public float Height;

    /// <summary>Art-slot edge. NaN ⇒ <see cref="SidebarRowMetrics.ArtFor"/>.</summary>
    public float ArtSize;

    // ── slots ────────────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>The leading visual — build it with <see cref="SidebarCover"/>. Null falls back to <see cref="Glyph"/>.</summary>
    public Element? Leading;

    /// <summary>A bare 16-DIP glyph in the leading column when <see cref="Leading"/> is null (the shortcut-row shape).
    /// It tints with selection exactly like Classic's library rows.</summary>
    public string? Glyph;

    /// <summary>A disclosure chevron BEFORE the leading visual (folder rows). Build it as
    /// <c>Icon(expanded ? Icons.ChevronDown : Icons.ChevronRight, 10f, Tok.TextTertiary)</c>.</summary>
    public Element? LeadingChevron;

    /// <summary>Trailing content (a count badge, a "new" dot, a state glyph). Placed before the overflow affordance.</summary>
    public Element? Trailing;

    /// <summary>Show the now-playing equalizer in the trailing column (this row's context is the one playing).</summary>
    public bool Playing;

    /// <summary>Animate the equalizer (playing) vs hold it low (paused-on-this-row). Ignored unless <see cref="Playing"/>.</summary>
    public bool PlayingAnimated;

    /// <summary>A TRACK row: the leading art gains a hover-revealed scrim + play glyph, and activation PLAYS rather than
    /// navigates. Tracks are never pinnable; callers may still supply a drag payload for playlist deposit/reorder.</summary>
    public bool Track;

    /// <summary>Render the hover-revealed 26-DIP "…" that re-enters the context-request funnel
    /// (<c>ClickRequestsContext</c>), so the trailing button and right-click open the SAME menu. Requires
    /// <see cref="MenuOverlay"/> + <see cref="Menu"/>, else it is omitted rather than rendered dead.</summary>
    public bool Overflow;

    // ── wiring ───────────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>Activation (navigate, or play for a <see cref="Track"/> row). Null ⇒ a non-interactive row.</summary>
    public Action? OnClick;

    /// <summary>The caller's realize handler — typically <c>h =&gt; rowNodes[key] = h</c> for the selection pill. It is
    /// CHAINED, never replaced, by anything this factory or <c>ContextMenu.Attach</c> adds afterwards.</summary>
    public Action<NodeHandle>? OnRealized;

    /// <summary>The overlay service the context menu opens through (<c>UseContext(Overlay.Service)</c>).</summary>
    public IOverlayService? MenuOverlay;

    /// <summary>The context-menu factory, invoked AT OPEN TIME (never at render time) — e.g.
    /// <c>() =&gt; Menus.SidebarEntry(acts, in entry, toggleFolder, expanded)</c>.</summary>
    public Func<ContextMenuModel?>? Menu;

    /// <summary>Makes the row a typed Wavee resource drag source. Leave null for rows already wrapped by
    /// <c>Reorderable.Item</c> (which installs its own source).</summary>
    public WaveeResourceDragPayload? Drag;

    /// <summary>Optional resource destination (playlist deposit and/or pinned-band insertion).</summary>
    public DropTargetSpec? DropTarget;

    /// <summary>Cold compatibility cue for a live resource drag. The row keeps its normal fill ramp and gains only an
    /// accent outline, so before/after/inside targeting never masquerades as selection.</summary>
    public Func<bool>? DropActive;

    /// <summary>The row's layout transition. Leave null when a <c>Reorderable</c> wraps the row — <c>Reorderable.Item</c>
    /// applies <c>LayoutTransition.Slide</c> FLIP itself, and an authored offset hint plus a position track is a
    /// documented stomp (see <c>ReorderList</c>'s remarks).</summary>
    public LayoutTransition? Animate;

    /// <summary>An extra caption line under the subtitle (the keyboard-reorder position announcement, "3 of 12").</summary>
    public string? Caption;

    /// <summary>Make the row itself a focus stop. Leave false when a <c>Reorderable</c> wraps it — its wrapper is the
    /// focus stop and the keyboard-lift key handler, and two stops per row would double the tab order.</summary>
    public bool Focusable;
}

static class SidebarEntityRow
{
    /// <summary>The Classic 44-DIP row height, re-exported so call sites do not have to know about
    /// <see cref="SidebarRowMetrics"/> to size a <c>Reorderable</c>.</summary>
    public const float ClassicHeight = SidebarRowMetrics.ClassicHeight;

    /// <inheritdoc cref="SidebarRowMetrics.HeightFor"/>
    public static float HeightFor(SidebarDensity density, bool hasSubtitle)
        => SidebarRowMetrics.HeightFor(density, hasSubtitle);

    /// <summary>The height <paramref name="spec"/> will render at — for a virtualizing host that must size the slot
    /// before (or without) building the row.</summary>
    public static float HeightOf(in SidebarRowSpec spec)
        => float.IsNaN(spec.Height)
            ? SidebarRowMetrics.HeightFor(spec.Density, SidebarRowMetrics.SubtitleVisible(spec.Density, spec.Subtitle))
            : spec.Height;

    /// <summary>Build the row. Returns a <see cref="BoxEl"/> (not <c>Element</c>) so a caller can still <c>with</c> extra
    /// fields on it — but everything in the F.1.4 contract is already applied, and the fill ramp in particular must never
    /// be overridden downstream.</summary>
    public static BoxEl Create(in SidebarRowSpec spec)
    {
        bool selected = spec.Selected;
        bool enabled = spec.Enabled;
        bool hasSubtitle = SidebarRowMetrics.SubtitleVisible(spec.Density, spec.Subtitle);
        float height = float.IsNaN(spec.Height) ? SidebarRowMetrics.HeightFor(spec.Density, hasSubtitle) : spec.Height;
        float art = float.IsNaN(spec.ArtSize) ? SidebarRowMetrics.ArtFor(spec.Density) : spec.ArtSize;
        bool bareGlyph = spec.Leading is null && spec.Glyph is { Length: > 0 };
        float gap = float.IsNaN(spec.Gap) ? (bareGlyph ? 12f : 10f) : spec.Gap;
        var dropActive = spec.DropActive; // copy: an `in` parameter cannot be captured by the bound paint thunk

        // ── leading column ──────────────────────────────────────────────────────────────────────────────────────────
        Element leading;
        if (spec.Leading is { } given) leading = given;
        else if (bareGlyph) leading = Icon(spec.Glyph!, 16f, selected ? Tok.TextPrimary : Tok.TextSecondary);
        else leading = new BoxEl { Width = art, Height = art, Shrink = 0f };   // keeps the leading column's width stable
        if (spec.Track && spec.Leading is not null)
            leading = TrackArt(leading, art, spec.Density);

        // ── text column ─────────────────────────────────────────────────────────────────────────────────────────────
        Element text;
        if (!hasSubtitle && spec.Caption is null)
        {
            text = Body(spec.Label) with { Grow = 1f, Trim = TextTrim.CharacterEllipsis, MaxLines = 1 };
        }
        else
        {
            int lines = 1 + (hasSubtitle ? 1 : 0) + (spec.Caption is { Length: > 0 } ? 1 : 0);
            var stack = new Element[lines];
            int n = 0;
            stack[n++] = Body(spec.Label) with { Trim = TextTrim.CharacterEllipsis, MaxLines = 1 };
            if (hasSubtitle)
                stack[n++] = Caption(spec.Subtitle!).Secondary() with { Trim = TextTrim.CharacterEllipsis, MaxLines = 1 };
            if (spec.Caption is { Length: > 0 } cap)
                stack[n++] = new TextEl(cap) { Size = 11f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis };
            text = new BoxEl { Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, Gap = 1f, Children = stack };
        }

        // ── children ────────────────────────────────────────────────────────────────────────────────────────────────
        int count = 2                                                   // leading cluster + text
                  + (spec.Playing ? 1 : 0)
                  + (spec.Trailing is null ? 0 : 1)
                  + (ShowsOverflow(in spec) ? 1 : 0);
        var kids = new Element[count];
        int k = 0;
        kids[k++] = spec.TreeNode
            ? TreeLeading(leading, spec.LeadingChevron, spec.TreeDepth, spec.TreeContinuationMask, height)
            : StandardLeading(leading, spec.LeadingChevron, gap);
        kids[k++] = text;
        if (spec.Playing) kids[k++] = WaveeEqualizer.Of(spec.PlayingAnimated, Tok.AccentDefault, 12f);
        if (spec.Trailing is { } trailing) kids[k++] = trailing;
        if (ShowsOverflow(in spec)) kids[k++] = OverflowButton();

        var row = new BoxEl
        {
            Key = spec.Key,
            Animate = spec.Animate,
            OnRealized = spec.OnRealized,
            Direction = 0, Height = height, AlignItems = FlexAlign.Center, Gap = gap,
            Padding = new Edges4(SidebarRowMetrics.IndentFor(spec.Depth), 0f, 8f, 0f),
            Corners = CornerRadius4.All(4f),
            // THE 4-STATE SELECTION-AWARE RAMP (F.1.4). Defined here, once, for every sidebar row in every design.
            Fill = enabled && selected ? Tok.FillSubtleSecondary : ColorF.Transparent,
            HoverFill = !enabled ? ColorF.Transparent : selected ? Tok.FillSubtleTertiary : Tok.FillSubtleSecondary,
            PressedFill = !enabled ? ColorF.Transparent : selected ? Tok.FillSubtleSecondary : Tok.FillSubtleTertiary,
            BorderWidth = dropActive is null ? 0f : 1f,
            BorderColor = dropActive is null ? ColorF.Transparent
                : Prop.Of(() => dropActive() ? Tok.AccentDefault : ColorF.Transparent),
            Opacity = enabled ? 1f : 0.55f,
            IsEnabled = enabled,
            OnClick = enabled ? spec.OnClick : null,
            Focusable = spec.Focusable,
            Draggable = enabled && spec.Drag is { } payload
                ? new DragSource(WaveeDragKinds.Resource, () => payload)
                : null,
            DropTarget = spec.DropTarget,
            Children = kids,
        };

        // Right-click / Menu key / long-press. Attach CHAINS onto the row's existing OnRealized + OnContextRequested —
        // it never clobbers the pill's measurement capture.
        if (spec.MenuOverlay is { } svc && spec.Menu is { } factory) row = row.WithContextMenu(svc, factory);
        return row;
    }

    /// <summary>The 3-DIP reserve where the selection accent sits. The MOVING pill is the single overlay
    /// <see cref="SidebarSelectionPill"/>, so the reserve exists purely to keep row content from shifting as selection
    /// moves.</summary>
    public static Element SelGutter() => new BoxEl { Width = 3f, Shrink = 0f };

    /// <summary>The ordinary row's leading cluster, factored so the selection gutter and optional disclosure do not
    /// consume the row's text gap as separate flex children.</summary>
    static Element StandardLeading(Element leading, Element? chevron, float gap)
    {
        Element[] children = chevron is null
            ? [SelGutter(), leading]
            : [SelGutter(), chevron, leading];
        return new BoxEl
        {
            Direction = 0, Shrink = 0f, Gap = gap, AlignItems = FlexAlign.Center,
            Children = children,
        };
    }

    /// <summary>A tree row uses a fixed disclosure cell at every depth, then visible connector cells. Folder and leaf art
    /// consequently share one column, while nesting reads as a relationship instead of a widening blank margin.</summary>
    static Element TreeLeading(Element leading, Element? chevron, int depth, byte continuationMask, float height)
    {
        int levels = Math.Clamp(depth, 0, 4);
        var children = new Element[levels > 0 ? 4 : 3];
        int i = 0;
        children[i++] = SelGutter();
        if (levels > 0) children[i++] = TreeGuides(levels, continuationMask, height);
        children[i++] = new BoxEl
        {
            Width = Spacing.L, Height = height, Shrink = 0f,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            HitTestPassThrough = true,
            Children = chevron is null ? [] : [chevron],
        };
        children[i] = leading;
        return new BoxEl
        {
            Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center,
            Children = children,
        };
    }

    static Element TreeGuides(int depth, byte continuationMask, float height)
    {
        var cells = new Element[depth];
        for (int level = 1; level <= depth; level++)
        {
            bool current = level == depth;
            bool continues = (continuationMask & (1 << (level - 1))) != 0;
            var marks = new List<Element>(2);
            if (current || continues)
                marks.Add(new BoxEl
                {
                    Width = 1f, Height = current && !continues ? height / 2f : height, Shrink = 0f,
                    AlignSelf = FlexAlign.Start, Margin = new Edges4(Spacing.XS, 0f, 0f, 0f),
                    Fill = Tok.StrokeDividerDefault,
                });
            if (current)
                marks.Add(new BoxEl
                {
                    Width = Spacing.S, Height = 1f, Shrink = 0f, AlignSelf = FlexAlign.Start,
                    Margin = new Edges4(Spacing.XS, height / 2f, 0f, 0f), Fill = Tok.StrokeDividerDefault,
                });
            cells[level - 1] = new BoxEl
            {
                Width = Spacing.M, Height = height, Shrink = 0f, ZStack = true,
                HitTestPassThrough = true, Children = [.. marks],
            };
        }
        return new BoxEl
        {
            Direction = 0, Width = depth * Spacing.M, Height = height, Shrink = 0f,
            HitTestPassThrough = true, Children = cells,
        };
    }

    /// <summary>Name the activation of a TRACK row ("Play track"). The engine exposes no automation-name channel, so the
    /// only place a non-visual name can live is a tooltip — call this on every <see cref="SidebarRowSpec.Track"/> row
    /// (<c>Create</c> cannot: it returns a <see cref="BoxEl"/>, and <c>ToolTip.Wrap</c> returns a component wrapper).
    /// One helper, one loc key, so the queue / now-playing / artist-top-tracks feeds all announce it identically.</summary>
    public static Element WithPlayTrackHint(Element row)
        => ToolTip.Wrap(row, Loc.Get(Strings.Sidebar.Item.PlayTrack));

    static bool ShowsOverflow(in SidebarRowSpec spec)
        => spec.Overflow && spec.Enabled && spec.MenuOverlay is not null && spec.Menu is not null;

    /// <summary>The hover-revealed trailing "…". <c>ClickRequestsContext</c> re-enters the context-request funnel, so the
    /// walk finds the row's own <c>OnContextRequested</c> (the <c>WithContextMenu</c> attach) and the button and the
    /// right-click open the same menu anchored at the button.</summary>
    static Element OverflowButton() => new BoxEl
    {
        Opacity = 0f, HoverOpacity = 1f, Shrink = 0f,
        Children =
        [
            new BoxEl
            {
                Width = 26f, Height = 26f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = CornerRadius4.All(13f),
                HoverFill = Tok.FillSubtleTertiary,
                Role = AutomationRole.Button, Cursor = CursorId.Hand,
                ClickRequestsContext = true,
                Children = [Icon(Icons.More, 14f, Tok.TextSecondary)],
            },
        ],
    };

    /// <summary>A track row's art: the cover with a hover-revealed scrim + play glyph over it. The reveal rides
    /// <c>HoverOpacity</c> on a DESCENDANT, which the engine's hover propagation resolves against the ROW's hover
    /// (AnimScheduler.Hover: only reveal/scale affordances follow their container) — so hovering anywhere on the row
    /// lights it.</summary>
    static Element TrackArt(Element cover, float art, SidebarDensity density) => ZStack(
        cover,
        new BoxEl
        {
            Width = art, Height = art, Shrink = 0f,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(SidebarCover.Radius(art, circular: false)),
            // A scrim is dark in BOTH themes, so the glyph over it is literal white (never Tok.TextOnAccentPrimary,
            // which is BLACK in the dark theme).
            Fill = new ColorF(0f, 0f, 0f, 0.55f),
            Opacity = 0f, HoverOpacity = 1f, HitTestVisible = false,
            Children = [Icon(Icons.Play, density == SidebarDensity.Compact ? 10f : 14f, ColorF.FromRgba(0xFF, 0xFF, 0xFF))],
        }) with { Width = art, Height = art, Shrink = 0f };
}
