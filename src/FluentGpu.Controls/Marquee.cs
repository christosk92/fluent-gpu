using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>
/// Auto-scrolling single-line text. When the text is wider than its container it scrolls horizontally
/// (continuously by default), with a soft alpha fade at the edges; when it fits it renders plainly.
/// Use <see cref="Of"/> to drop it into a tree.
/// </summary>
public static class Marquee
{
    public enum ScrollMode { Loop, PingPong, SinglePass }

    /// <summary>What turns scrolling on. <see cref="Always"/> auto-scrolls whenever the text overflows.
    /// <see cref="Hover"/> scrolls only while the control is hovered.
    /// <see cref="PauseOnHover"/> auto-scrolls when the text overflows and pauses while hovered (read the full title).</summary>
    public enum TriggerMode { Hover, Always, PauseOnHover }

    public sealed record Style
    {
        public float FontSize { get; init; } = 14f;
        public ushort Weight { get; init; } = 400;
        /// <summary>The text colour — a reactive channel like every other engine property. A static <see cref="ColorF"/>
        /// (e.g. <c>Foreground = Tok.TextSecondary</c>) for a fixed colour, or a bind (<c>Prop.Of(() =&gt; …)</c> / a
        /// signal) when it tracks state. It is forwarded straight to the inner <c>TextEl.Color</c>, so a bound colour
        /// repaints reactively without re-rendering the marquee.</summary>
        public Prop<ColorF> Foreground { get; init; } = Prop.Of(static () => Tok.TextPrimary);
        public string? FontFamily { get; init; }
        public float Speed { get; init; } = 9f;           // minimum pixels per second — keeps a barely-overflowing line
                                                          // visibly moving instead of taking CycleMs to travel one glyph.
        // Maximum scroll-cycle duration (ms) for ONE traversal. Constant velocity alone makes a line's duration depend
        // on its width, so two long sibling marquees drift out of phase; CycleMs caps them to the same cadence. Speed is
        // still a FLOOR: a short tail finishes sooner instead of creeping sub-pixel-slow for the full fixed cycle.
        // 0 ⇒ derive the duration entirely from Speed (a standalone, constant-pace line).
        public float CycleMs { get; init; } = 0f;
        public float Gap { get; init; } = 48f;           // space between the two copies in Loop mode
        public float FadeBand { get; init; } = 24f;      // edge fade width in px
        public float FadeStrength { get; init; } = 1f;   // edge-fade intensity 0..1 (1 = fades fully to transparent)
        public float StartDelayMs { get; init; } = 350f;  // pause showing the START of the text before it scrolls
        public float EndPauseMs { get; init; } = 900f;    // pause at the tail before bouncing back (PingPong) / loop reset (Loop)
        public ScrollMode Mode { get; init; } = ScrollMode.Loop;
        public TriggerMode Trigger { get; init; } = TriggerMode.Always;
        public bool Enabled { get; init; } = true;
    }

    public static readonly Style Default = new();

    /// <summary>Build a marquee. It fills the width its parent gives it (cross-axis stretch in a column, or
    /// Grow in a row) and scrolls only when the text overflows that width.
    /// <para><paramref name="text"/> is a reactive channel (<see cref="Prop{T}"/>): pass a plain string for a fixed
    /// label, or a bind — <c>Prop.Of(() =&gt; …)</c> / a signal — when the text changes over the control's lifetime
    /// (e.g. a now-playing title). The text is forwarded to the inner <c>TextEl.Text</c>, whose bind re-measures and
    /// re-scrolls on change; a static string never subscribes. (A frozen constructor arg would NOT update — components
    /// are autonomous; reactive data crosses through the Prop, not the factory closure.)</para></summary>
    /// <param name="scrollWhen">An OPTIONAL external hover gate (used with <see cref="TriggerMode.Hover"/> or
    /// <see cref="TriggerMode.PauseOnHover"/>): when supplied the marquee does NOT wire its own self-hover — a GROUP of
    /// marquees can share ONE parent hover zone. For <see cref="TriggerMode.Hover"/> the gate scrolls while true; for
    /// <see cref="TriggerMode.PauseOnHover"/> it pauses while true. Null = self-hover. The edge fade is unaffected
    /// (right-edge cue at rest, both edges while scrolling).</param>
    public static Element Of(Prop<string> text, Style? style = null, IReadSignal<bool>? scrollWhen = null)
        => new BoxEl
        {
            MinWidth = 0f,
            Grow = 1f,
            Shrink = 1f,
            AlignSelf = FlexAlign.Stretch,
            ClipToBounds = true,
            Children = [Embed.Comp(() => new MarqueeHost { Text = text, Sty = style ?? Default, External = scrollWhen })],
        };

    /// <summary>Like <see cref="Of"/> but scrolls arbitrary interactive content (e.g. a row of links) when it overflows.
    /// <paramref name="content"/> is a <see cref="Component"/> factory — reactive data must be read inside that component
    /// (context/signals), not captured from the parent's render.</summary>
    public static Element Content(Func<Component> content, Style? style = null, IReadSignal<bool>? scrollWhen = null)
        => new BoxEl
        {
            MinWidth = 0f,
            Grow = 1f,
            Shrink = 1f,
            AlignSelf = FlexAlign.Stretch,
            ClipToBounds = true,
            Children = [Embed.Comp(() => new MarqueeHost { Content = content, Sty = style ?? Default, External = scrollWhen })],
        };
}

/// <summary>The clip + edge-fade frame (and the optional self-hover trigger). The moving content is a child
/// component; dynamic state crosses the component boundary through reactive channels, NOT constructor args (a parent
/// re-render does NOT push new args into an already-mounted child - it is autonomous). The measurement/hover state
/// crosses as shared Signals (containerW/textW/hovered); the <see cref="Text"/> and foreground cross as <see cref="Prop{T}"/>
/// binds forwarded down to the leaf <c>TextEl</c>, whose own bind re-measures (text) / repaints (colour) on change.</summary>
internal sealed class MarqueeHost : Component
{
    public Prop<string> Text = string.Empty;
    public Func<Component>? Content;
    public Marquee.Style Sty = Marquee.Default;
    const float MaxFadeViewportFraction = 0.3f;
    public IReadSignal<bool>? External;     // an external "scroll now" gate (a shared group hover) — replaces self-hover

    public override Element Render()
    {
        var containerW = UseSignal(0f);     // set from this node's bounds
        var textW = UseSignal(0f);          // set by the child after it measures one copy
        var scrollX = UseSignal(0f);        // live TranslateX of the scroller (drives per-edge fade)
        var hovered = UseSignal(false);     // scroll gate: self-hover (Trigger.Hover), or mirrored from External below

        // An external gate (a group's shared hover) drives the SAME `hovered` signal both this host and the scroller
        // read — so the rest of the logic is untouched and self-hover is skipped (see selfHover). No-op when unset.
        UseSignalEffect(() => { if (External is { } ext) { bool v = ext.Value; if (hovered.Peek() != v) hovered.Value = v; } });

        float cw = containerW.Value, tw = textW.Value;
        bool overflow = tw > cw + 1f && cw > 0f;
        float fadeBand = overflow ? MathF.Min(Sty.FadeBand, MathF.Max(0f, cw * MaxFadeViewportFraction)) : 0f;
        EdgeFadeSpec? fade = overflow && fadeBand > 0f
            ? MarqueeScroller.ResolveEdgeFade(Sty, scrollX.Value, cw, tw, fadeBand)
            : null;

        bool selfHover = External is null && Sty.Trigger is Marquee.TriggerMode.Hover or Marquee.TriggerMode.PauseOnHover;

        return new BoxEl
        {
            MinWidth = 0f,
            Shrink = 1f,
            AlignSelf = FlexAlign.Stretch,
            ClipToBounds = true,
            EdgeFade = fade,
            OnBoundsChanged = r => { if (r.W != containerW.Value) containerW.Value = r.W; },
            OnHoverMove = selfHover ? _ => { if (!hovered.Value) hovered.Value = true; } : null,
            OnPointerExit = selfHover ? () => { if (hovered.Value) hovered.Value = false; } : null,
            Children =
            [
                Embed.Comp(() => new MarqueeScroller
                {
                    Text = Text, Content = Content, Sty = Sty, ContainerW = containerW, TextW = textW,
                    ScrollX = scrollX, Hovered = hovered,
                }),
            ],
        };
    }
}

/// <summary>The moving content. Its rendered root IS the animated node (the engine seeds the TranslateX track
/// on a component's own host node), so the parent <see cref="MarqueeHost"/> clips and fades it. It reads the
/// shared Signals (so it re-renders when hover/size change) and reports its measured width back through one.</summary>
internal sealed class MarqueeScroller : Component
{
    public Prop<string> Text = string.Empty;
    public Func<Component>? Content;
    public Marquee.Style Sty = Marquee.Default;
    public Signal<float> ContainerW = null!;
    public Signal<float> TextW = null!;
    public Signal<float> ScrollX = null!;
    public Signal<bool> Hovered = null!;

    public override Element Render()
    {
        float cw = ContainerW.Value;
        float tw = TextW.Value;
        bool overflow = tw > cw + 1f && cw > 0f;
        bool active = Sty.Trigger switch
        {
            Marquee.TriggerMode.Always => true,
            Marquee.TriggerMode.Hover => Hovered.Value,
            Marquee.TriggerMode.PauseOnHover => !Hovered.Value,
            _ => true,
        };
        bool canScroll = Sty.Enabled && overflow && !Motion.ReducedMotion;
        bool paused = canScroll && !active;

        var scrollerHost = UseRef(NodeHandle.Null);
        UseLayoutEffect(() => { scrollerHost.Value = Context.HostNode; });

        bool loop = Sty.Mode == Marquee.ScrollMode.Loop;
        bool textMode = Content is null;
        bool seamless = textMode && loop && canScroll && !paused;
        float loopDist = tw + Sty.Gap;
        float tailDist = MathF.Max(0f, tw - cw);

        // Park/unpark the translate track on hover-pause — never re-seed a "0,0" idle track (that snapped back to start).
        UseLayoutEffect(() =>
        {
            if (Context.Anim is { } a && !Context.HostNode.IsNull)
                a.SetNodeParked(Context.HostNode, paused);
        }, paused, canScroll);

        // One animation hook per Mode (Mode is fixed for an instance, so the hook order is stable across renders).
        if (Sty.Mode == Marquee.ScrollMode.SinglePass)
        {
            UseSpring(AnimChannel.TranslateX, canScroll ? -tailDist : 0f,
                      SpringParams.FromResponse(0.45f, 0.9f), canScroll, tailDist);
        }
        else
        {
            (Keyframe[] keys, float durMs, bool looping) = BuildTrack(loop, canScroll, loopDist, tailDist);
            UseKeyframes(AnimChannel.TranslateX, keys, durMs, looping, canScroll, loop, loopDist, tailDist);
        }

        var copies = new List<Element>(seamless ? 2 : 1) { Measured() };
        if (seamless) copies.Add(Copy());
        copies.Add(Embed.Comp(() => new MarqueeScrollTicker
        {
            ContainerW = ContainerW, TextW = TextW, ScrollX = ScrollX, ScrollerHost = scrollerHost,
        }));

        return new BoxEl
        {
            Direction = 0,
            Shrink = 0f,              // never shrink to the container; overflow, then the parent clips
            AlignItems = FlexAlign.Center,
            Gap = seamless ? Sty.Gap : 0f,
            Children = copies.ToArray(),
        };
    }

    // The first copy carries the bounds probe so we measure ONE copy's natural width.
    private Element Measured() => new BoxEl
    {
        Shrink = 0f,
        OnBoundsChanged = r => { if (r.W != TextW.Value) TextW.Value = r.W; },
        Children = [Content is { } mk ? Embed.Comp(mk) : Glyphs()],
    };

    private Element Copy() => new BoxEl { Shrink = 0f, Children = [Glyphs()] };

    private TextEl Glyphs() => new(Text)
    {
        Size = Sty.FontSize,
        Weight = Sty.Weight,
        Color = Sty.Foreground,
        FontFamily = Sty.FontFamily,
        Wrap = TextWrap.NoWrap,
        MaxLines = 1,
    };

    private (Keyframe[] keys, float durMs, bool loop) BuildTrack(bool loop, bool canScroll, float loopDist, float tailDist)
    {
        if (!canScroll)
            return ([new Keyframe(0f, 0f), new Keyframe(1f, 0f)], 200f, false);

        float speed = MathF.Max(1f, Sty.Speed);
        // CycleMs caps long traversals to a shared cadence; Speed remains the minimum visible pace for short tails.
        // Thus long sibling lines stay synced without making a one-glyph overflow look stationary.
        bool fixedCycle = Sty.CycleMs > 0f;

        float TravelMs(float distance)
        {
            float atMinSpeed = MathF.Max(0f, distance) / speed * 1000f;
            return fixedCycle ? MathF.Min(Sty.CycleMs, atMinSpeed) : atMinSpeed;
        }

        if (loop)
        {
            float travel = TravelMs(loopDist);
            float dur = MathF.Max(1f, travel + Sty.StartDelayMs);
            float delayFrac = Sty.StartDelayMs / dur;
            return (
            [
                new Keyframe(0f, 0f, Easing.Linear),
                new Keyframe(delayFrac, 0f, Easing.Linear),
                new Keyframe(1f, -loopDist, Easing.Linear),
            ], dur, true);
        }

        // PingPong: pause at start, scroll out, hold at tail, bounce back.
        float startPause = Sty.StartDelayMs;
        float endPause = Sty.EndPauseMs;
        float travelP = TravelMs(tailDist);
        float total = MathF.Max(1f, startPause + endPause + 2f * travelP);
        float f1 = startPause / total;
        float f2 = f1 + travelP / total;
        float f3 = f2 + endPause / total;
        return (
        [
            new Keyframe(0f, 0f, Easing.Linear),
            new Keyframe(f1, 0f, Easing.Linear),
            new Keyframe(f2, -tailDist, Easing.Linear),
            new Keyframe(f3, -tailDist, Easing.Linear),
            new Keyframe(1f, 0f, Easing.Linear),
        ], total, true);
    }

    // Feather only edges with hidden overflow (scroll-cue parity): at translateX=0 fade right only; at the tail fade left only.
    internal static EdgeFadeSpec? ResolveEdgeFade(Marquee.Style sty, float translateX, float viewportW, float contentW, float maxBand)
    {
        float tail = MathF.Max(0f, contentW - viewportW);
        if (tail <= 0.5f) return null;

        if (sty.Mode == Marquee.ScrollMode.Loop)
            return new EdgeFadeSpec(EdgeMask.Horizontal, maxBand, FadeFalloff.Smoothstep, sty.FadeStrength);

        float scrolled = MathF.Max(0f, -translateX);
        float pastL = scrolled;
        float pastR = MathF.Max(0f, tail - scrolled);
        const float runway = 24f;
        EdgeMask edges = EdgeMask.None;
        float bl = 0f, br = 0f;
        if (pastL > 0.5f) { edges |= EdgeMask.Left; bl = maxBand * MathF.Min(1f, pastL / runway); }
        if (pastR > 0.5f) { edges |= EdgeMask.Right; br = maxBand * MathF.Min(1f, pastR / runway); }
        return edges == EdgeMask.None ? null : new EdgeFadeSpec(edges, bl, 0f, br, 0f, FadeFalloff.Smoothstep, sty.FadeStrength);
    }
}

/// <summary>After <c>_anim.Tick</c>, mirrors the scroller host's live <see cref="AnimChannel.TranslateX"/> into the
/// shared <see cref="MarqueeScroller.ScrollX"/> signal so <see cref="MarqueeHost"/> can derive per-edge fade bands.
/// ReactiveComponent + <see cref="InputHooks.SetAfterAnimations"/> avoids the stale-closure trap of wiring this inside
/// <see cref="MarqueeScroller.Render"/> (where <c>UseSignalEffect</c> freezes <c>canScroll</c> from the first mount).</summary>
internal sealed class MarqueeScrollTicker : ReactiveComponent
{
    public Signal<float> ContainerW = null!;
    public Signal<float> TextW = null!;
    public Signal<float> ScrollX = null!;
    public Ref<NodeHandle> ScrollerHost = null!;

    public override Element Setup()
    {
        var hooks = UseContext(InputHooks.Current);
        UseEffect(() => hooks.SetAfterAnimations(this, Sample));
        return new BoxEl { HitTestVisible = false, Width = 0f, Height = 0f };
    }

    void Sample()
    {
        float cw = ContainerW.Peek(), tw = TextW.Peek();
        if (tw <= cw + 1f || cw <= 0f)
        {
            if (ScrollX.Peek() != 0f) ScrollX.Value = 0f;
            return;
        }
        var host = ScrollerHost.Value;
        if (host.IsNull) return;
        if (Context.Scene is { } scene && !scene.IsLive(host))
        {
            UseContext(InputHooks.Current).SetAfterAnimations(this, null);
            return;
        }
        float tx = ReadTranslateX(host);
        if (ScrollX.Peek() != tx) ScrollX.Value = tx;
    }

    float ReadTranslateX(NodeHandle host)
    {
        if (Context.Anim?.TryGetTrackValue(host, AnimChannel.TranslateX, out float tx) == true)
            return tx;
        if (Context.Scene is { } scene && scene.IsLive(host))
            return scene.Paint(host).LocalTransform.Dx;
        return 0f;
    }
}
