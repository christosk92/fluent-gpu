using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Features.Detail;

namespace Wavee;

/// <summary>One entry in a context band's pivot: the section's stable key (the same key its
/// <see cref="SectionAnchors"/> registration uses) and its already-localized title.</summary>
readonly record struct ContextPivotItem(string Key, string Label);

/// <summary>The page-owned registry the context band's scroll spy reads WITHOUT being an ancestor of the scroller:
/// each pivot-visible section reports its wrapper node here (keyed by the section key) and the page reports the scroll
/// viewport. The band resolves "which section am I in" from these through <c>SceneStore.AbsoluteRect</c> — the same
/// reachable pattern <c>ShyMonthPill</c> and the sidebar selection pill use.
///
/// <para>A plain mutable class, not a signal graph: it is written from <c>OnRealized</c> (a layout-time callback) and
/// read from a scroll effect, both on the UI thread, and turning each of ~14 node handles into a signal would buy a
/// reactivity nobody subscribes to — the SCROLL signal is what drives the recompute.</para></summary>
sealed class SectionAnchors
{
    /// <summary>The scrolling viewport the sections live in. Captured with <c>ScrollEl.OnRealized</c>.</summary>
    public NodeHandle Viewport;

    readonly Dictionary<string, NodeHandle> _nodes = new(StringComparer.Ordinal);

    public void Set(string key, NodeHandle node) => _nodes[key] = node;
    public NodeHandle Get(string key) => _nodes.TryGetValue(key, out var n) ? n : default;

    /// <summary>Drop every registration — called when the page swaps identity (artist → artist in the reused slot),
    /// so a stale node from the previous artist can never answer a pivot click.</summary>
    public void Reset() { _nodes.Clear(); Viewport = default; }
}

/// <summary>
/// Wavee's ONE sticky page header: the <b>text-chrome context bar</b>. A full-width 56-DIP band that slides in once
/// the page's hero has scrolled past, carrying the page title on the left, (where the page has sections) its own
/// sections as a text PIVOT in the middle, and text actions on the right.
///
/// <para><b>What it replaced, and why.</b> The app had two unrelated sticky headers. The artist page had an
/// art-tinted band with an avatar, the name and Play/Following capsules — a second, smaller copy of the hero it was
/// replacing, which is the one thing a collapsed header must not be. The track-detail pages had three FLOATING
/// objects instead of a header at all: a shadowed bordered capsule holding a cover and a title, a pair of bare glyph
/// buttons, and an accent circle play FAB, all hovering over live scrolling rows with nothing behind them. Both are
/// gone. This band is a single opaque surface (<see cref="WaveeColors.ContextBand"/>), ONE hairline, NO shadow, no
/// thumbnail and no plates: the chrome is typography, which is the Zune pivot idiom the app's type ramp was already
/// aimed at.</para>
///
/// <para><b>The parts are separate on purpose.</b> The artist band pins as one node inside the hero's own collapse
/// ZStack; the detail band pins as the identity row while the tracklist's column header pins directly under it as a
/// second node. Both must read as ONE surface, so this class publishes the material, the hairline and the row
/// scaffolding as pieces either page can assemble, rather than one Build() that would have to grow a mode flag for
/// the difference.</para>
///
/// <para>Arithmetic (what fits, what the pivot drops, which section is active) lives in the engine-free
/// <see cref="ContextBandLayout"/>, where the tests drive it.</para>
/// </summary>
static class ContextBand
{
    /// <summary>The band's opaque material. See <see cref="WaveeColors.ContextBand"/> for why it is opaque and why it
    /// is that particular flatten.</summary>
    public static ColorF Fill => WaveeColors.ContextBand;

    /// <summary>The band's single lower edge — a low-alpha SEPARATOR token, never the accent. The accent in this band
    /// is spent entirely on the two things that earn it: the primary action and the active-section underline.</summary>
    public static ColorF HairlineColor => Tok.StrokeDividerDefault;

    /// <summary>The 1-DIP lower edge, as a row child. Deliberately a real laid-out child rather than a border on the
    /// band: the detail page's band is TWO pinned nodes (identity row + column header) and only the LAST of them may
    /// carry the line, or the page shows a seam through the middle of one surface.</summary>
    public static Element Hairline() => new BoxEl
    {
        Height = ContextBandLayout.HairlineHeight, AlignSelf = FlexAlign.Stretch,
        Fill = HairlineColor, HitTestVisible = false,
    };

    /// <summary>The band's title: BodyStrong (14 / 20 / 600) in primary ink, one line, ellipsized. It never wraps and
    /// never drops — see <see cref="ContextBandLayout"/> for the priority order.</summary>
    public static TextEl Title(string text) => Ui.BodyStrong(text) with
    {
        Color = Tok.TextPrimary, MinWidth = 0f, MaxLines = 1,
        Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
    };

    /// <summary>The optional second line under the title (owner · count on a detail page). Caption / tertiary — it is
    /// context for the title, not a competing label.</summary>
    public static TextEl Byline(string text) => Ui.Caption(text) with
    {
        Color = Tok.TextTertiary, MinWidth = 0f, MaxLines = 1,
        Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
    };

    /// <summary>The band's lower edge as an OVERLAY layer, for a band whose height is fixed by a collapse ladder (the
    /// artist hero's <c>PresentedH</c> bind targets exactly <see cref="ContextBandLayout.Height"/>, so the line has to
    /// sit inside that 56 rather than add a 57th DIP and re-open the collapse arithmetic).</summary>
    public static Element HairlineOverlay(float width) => new BoxEl
    {
        Width = width, Height = ContextBandLayout.Height,
        Direction = 1, Justify = FlexJustify.End, HitTestVisible = false,
        Children = [Hairline()],
    };

    /// <summary>The identity row: <see cref="ContextBandLayout.Height"/> tall, gutter-padded, opaque, and carrying no
    /// edge of its own — the caller decides where the band's ONE hairline goes (overlaid on the artist page, in flow
    /// under the tracklist's column header on the detail pages).</summary>
    public static Element Row(float width, float gutter, Element[] children) => new BoxEl
    {
        Direction = 0, Width = width, Height = ContextBandLayout.Height,
        Padding = new Edges4(gutter, 0f, gutter, 0f),
        Gap = ContextBandLayout.ClusterGap,
        AlignItems = FlexAlign.Center,
        Fill = Fill,
        Children = children,
    };

    /// <summary>The band's arrival: opacity 0→1 with a small upward settle, ramped over the final collapse band so it
    /// is reversible and tracks the finger rather than snapping at a threshold.
    ///
    /// <para>REDUCED MOTION IS A VALUE here, not a branch: the translate leg is simply absent from the returned array
    /// (the opacity cross-fade stays — a fade aids orientation, it is not motion). Same shape either way, no hook
    /// count changes, which is the rule a scroll-bound header is especially exposed to because a resize grip can flip
    /// the flag mid-session.</para></summary>
    public static ScrollBindDsl[] RevealBinds(float revealStart, float collapseDistance)
    {
        var opacity = new ScrollBindDsl
        {
            From = ScrollChannel.Offset, To = BindSink.Opacity,
            Range = ScrollRange.Px(revealStart, collapseDistance),
            OutStart = 0f, OutEnd = 1f, Ease = Easing.Linear,
        };
        if (Motion.ReducedMotion) return [opacity];
        return
        [
            opacity,
            new ScrollBindDsl
            {
                From = ScrollChannel.Offset, To = BindSink.TransY,
                Range = ScrollRange.Px(revealStart, collapseDistance),
                OutStart = Spacing.XS, OutEnd = 0f, Ease = Easing.Linear,
            },
        ];
    }

    /// <summary>Wrap a page section so the band's scroll spy can find it. Layout-neutral by construction — a bare
    /// column wrapper around the section, adding no size, no padding and no flex of its own.</summary>
    public static Element Anchor(SectionAnchors anchors, string key, Element section) => new BoxEl
    {
        Key = "anchor:" + key, Direction = 1, MinWidth = 0f,
        OnRealized = h => anchors.Set(key, h),
        Children = [section],
    };
}

/// <summary>
/// The band's middle cluster: the page's own sections as text links, with a 2-DIP accent underline marking the one
/// the visitor is currently inside.
///
/// <para><b>Why a component.</b> The active index changes on scroll, and scroll is the hottest signal on the page.
/// Isolating the spy here means a scroll step re-renders THIS cluster and nothing else — the artist page's magazine
/// body, its virtualized discography grids and the band's own title/actions are all untouched. The scroll signal is
/// subscribed by an EFFECT (not by Render), so most scroll steps do not even re-render this: the effect writes the
/// active-index signal through <c>SetIfChanged</c>, so a render happens only when the answer actually changes — at
/// section boundaries, a handful of times per page.</para>
///
/// <para><b>Props are re-pushed, never frozen.</b> The item set genuinely changes after mount (an artist's extras,
/// top albums and biography all arrive after the first Ready render, so sections appear mid-stream), which is exactly
/// the case <c>Embed.Comp(props, factory)</c> + <c>UseProps</c> exists for — a constructor argument would freeze the
/// pivot at whatever the page knew on the first frame. Only the two things that are genuinely mount-stable (the
/// anchor registry and the scroll signal) ride the constructor.</para>
/// </summary>
sealed class ContextPivot : Component
{
    /// <summary>The band never carries more than this many pivot items regardless of what fits — a pivot is a
    /// glance-and-aim affordance, and past a dozen words it is a menu. Also the stack budget the spy scans in.</summary>
    public const int MaxItems = 16;

    /// <summary>Re-pushed per render. <paramref name="Visible"/> is what <see cref="ContextBandLayout.Resolve"/>
    /// allowed at the current width; <paramref name="BandBottom"/> is the lower edge of the sticky chrome the spy
    /// measures arrival against (and the gutter a click scrolls the section to).</summary>
    public sealed record Props(ContextPivotItem[] Items, int Visible, float BandBottom, ColorF Accent);

    readonly SectionAnchors _anchors;
    readonly IReadSignal<float> _scroll;

    // The live props, read by the spy effect (which cannot subscribe to props). A plain render-local field write —
    // the same idiom DetailTracks uses to publish `_recsLive` to its bound slots.
    ContextPivotItem[] _items = [];
    float _bandBottom;

    public ContextPivot(SectionAnchors anchors, IReadSignal<float> scroll)
    {
        _anchors = anchors;
        _scroll = scroll;
    }

    public override Element Render()
    {
        var p = UseProps<Props>();
        _items = p.Items;
        _bandBottom = p.BandBottom;

        var active = UseSignal(0);

        // Scroll → re-resolve. Untracked inside, so writing the active index can never make this effect its own
        // dependency; SetIfChanged is what keeps a 4000-px scroll to a handful of renders.
        UseSignalEffect(() =>
        {
            _ = _scroll.Value;                       // subscribe (throttled by the page's own geometry projector)
            Reactive.Untrack(() => Resolve(active));
        });

        // The item SET changing (a section arrived) also changes the answer, and props are not a signal the effect
        // above can see — so resolve once more after any render that changed the set. DepKey over the count + the
        // first/last keys is enough: sections are appended and removed as whole units, never re-ordered in place.
        UseLayoutEffect(() => Resolve(active), DepKey.From(SetKey(p.Items)));

        int shown = Math.Clamp(p.Visible, 0, Math.Min(p.Items.Length, MaxItems));
        if (shown == 0) return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };

        int current = Math.Clamp(active.Value, 0, shown - 1);   // subscribe → re-render only on a boundary crossing
        var kids = new Element[shown];
        for (int i = 0; i < shown; i++)
        {
            int index = i;
            kids[i] = Link(p.Items[i].Label, i == current, p.Accent, () => GoTo(index));
        }

        return new BoxEl
        {
            Direction = 0, Gap = ContextBandLayout.PivotGap, Shrink = 0f,
            AlignItems = FlexAlign.Center,
            Children = kids,
        };
    }

    /// <summary>One pivot link. The hover handler is on the LINK's own box (never on the row) so
    /// <c>TextEl.HoverColor</c> scopes to the word the pointer is actually over.
    /// <para>The underline is ALWAYS mounted and switches COLOUR — transparent when inactive — rather than being
    /// conditionally mounted: a mount/unmount would re-run the reconciler for a 2-DIP rect on every boundary, and a
    /// brush cross-fade is what makes the mark move as one gesture. No FLIP flight between items (a 2-DIP rule flying
    /// across a 56-DIP bar is a distraction, not wayfinding).</para></summary>
    static Element Link(string label, bool isActive, ColorF accent, Action go) => new BoxEl
    {
        Direction = 1, Shrink = 0f,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Height = ContextBandLayout.Height - 2f * Spacing.M,
        Padding = new Edges4(ContextBandLayout.PivotPadX, 0f, ContextBandLayout.PivotPadX, 0f),
        Corners = Radii.ControlAll,
        Role = AutomationRole.Tab, Focusable = true, Cursor = CursorId.Hand, OnClick = go,
        Children =
        [
            new TextEl(label)
            {
                Size = WaveeCta.TextActionSize, LineHeight = WaveeCta.TextActionLineHeight,
                Weight = WaveeCta.TextActionWeight,
                Color = isActive ? Tok.TextPrimary : Tok.TextSecondary,
                HoverColor = Tok.TextPrimary,
                MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
            },
            new BoxEl
            {
                Height = ContextBandLayout.UnderlineHeight, AlignSelf = FlexAlign.Stretch,
                Margin = new Edges4(0f, ContextBandLayout.UnderlineGap, 0f, 0f),
                Fill = isActive ? accent : ColorF.Transparent,
                BrushTransitionMs = WaveeMotion.Fast,
                HitTestVisible = false,
            },
        ],
    };

    /// <summary>Click → scroll that section's top to just under the band. Through the engine's ONE programmatic
    /// bring-into-view seam against the EXPLICIT viewport the page registered, so a nested scroller deeper in the
    /// page can never be the thing that moves. Animated unless reduced motion is on, in which case it snaps.</summary>
    void GoTo(int index)
    {
        if ((uint)index >= (uint)_items.Length) return;
        var node = _anchors.Get(_items[index].Key);
        if (node.IsNull) return;
        ScrollIntoView.BringInto(Context, _anchors.Viewport, node,
            margin: _bandBottom, alignmentRatio: 0f, animate: !Motion.ReducedMotion);
    }

    /// <summary>Read committed layout and publish the active index. One AbsoluteRect per pivot item, only on a scroll
    /// step the page's own projector already let through (its 24-DIP write floor), and only up to the first
    /// unrealized section — a page whose lower half has not laid out costs a couple of reads, not a walk.</summary>
    void Resolve(Signal<int> active)
    {
        var scene = Context.Scene;
        if (scene is null) return;
        var vp = _anchors.Viewport;
        if (vp.IsNull || !scene.IsLive(vp)) return;

        int n = Math.Min(_items.Length, MaxItems);
        if (n == 0) return;
        float vpTop = scene.AbsoluteRect(vp).Y;
        Span<float> tops = stackalloc float[MaxItems];
        for (int i = 0; i < n; i++)
        {
            var node = _anchors.Get(_items[i].Key);
            tops[i] = node.IsNull || !scene.IsLive(node) ? float.NaN : scene.AbsoluteRect(node).Y - vpTop;
        }

        int at = ContextBandLayout.ActiveSection(tops[..n], _bandBottom);
        if (at >= 0) active.SetIfChanged(at);
    }

    static int SetKey(ContextPivotItem[] items)
    {
        var hash = new HashCode();
        hash.Add(items.Length);
        for (int i = 0; i < items.Length; i++) hash.Add(items[i].Key, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}
