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
    /// <see cref="Hover"/> scrolls only while the control is hovered - note self-hover makes the control a
    /// pointer target, so prefer <see cref="Always"/> when the control sits inside a clickable row.</summary>
    public enum TriggerMode { Hover, Always }

    public sealed record Style
    {
        public float FontSize { get; init; } = 14f;
        public ushort Weight { get; init; } = 400;
        /// <summary>The text colour — a reactive channel like every other engine property. A static <see cref="ColorF"/>
        /// (e.g. <c>Foreground = Tok.TextSecondary</c>) for a fixed colour, or a bind (<c>Prop.Of(() =&gt; …)</c> / a
        /// signal) when it tracks state. It is forwarded straight to the inner <c>TextEl.Color</c>, so a bound colour
        /// repaints reactively without re-rendering the marquee.</summary>
        public Prop<ColorF> Foreground { get; init; } = Tok.TextPrimary;
        public string? FontFamily { get; init; }
        public float Speed { get; init; } = 15f;         // pixels per second — a calm reading pace (constant velocity:
                                                          // longer text scrolls for longer, never faster). Used only when CycleMs == 0.
        // Fixed scroll-cycle duration (ms) for ONE traversal, OVERRIDING Speed when > 0. Constant velocity (Speed) makes
        // a line's duration depend on its width, so two lines of different widths drift out of phase. Giving sibling
        // marquees the SAME CycleMs locks their cadence: both pause at the start together, advance through the same
        // FRACTION of their own text together, and reset together — i.e. visually synced (each still covers its own
        // distance, so wider text moves faster). 0 ⇒ derive the duration from Speed (a standalone, constant-pace line).
        public float CycleMs { get; init; } = 0f;
        public float Gap { get; init; } = 48f;           // space between the two copies in Loop mode
        public float FadeBand { get; init; } = 18f;      // edge fade width in px
        public float FadeStrength { get; init; } = 0.55f; // edge-fade intensity 0..1 (1 = fades fully to transparent; lower = softer)
        public float StartDelayMs { get; init; } = 350f;  // pause showing the START of the text before it scrolls
        public float EndPauseMs { get; init; } = 900f;    // pause at each end (Loop start / PingPong turns)
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
    /// <param name="scrollWhen">An OPTIONAL external "scroll now" gate (used with <see cref="TriggerMode.Hover"/>):
    /// when supplied the marquee scrolls while this signal is true and does NOT wire its own self-hover — so a GROUP of
    /// marquees (a now-playing title + subtitle) can be driven by ONE shared hover over their common area and stay in
    /// sync, instead of each toggling only when the pointer is over its own line. Null = self-hover (Hover) / always
    /// (Always), unchanged. The edge fade is unaffected either way (right-edge cue at rest, both edges while scrolling).</param>
    public static Element Of(Prop<string> text, Style? style = null, IReadSignal<bool>? scrollWhen = null)
        => Embed.Comp(() => new MarqueeHost { Text = text, Sty = style ?? Default, External = scrollWhen });
}

/// <summary>The clip + edge-fade frame (and the optional self-hover trigger). The moving content is a child
/// component; dynamic state crosses the component boundary through reactive channels, NOT constructor args (a parent
/// re-render does NOT push new args into an already-mounted child - it is autonomous). The measurement/hover state
/// crosses as shared Signals (containerW/textW/hovered); the <see cref="Text"/> and foreground cross as <see cref="Prop{T}"/>
/// binds forwarded down to the leaf <c>TextEl</c>, whose own bind re-measures (text) / repaints (colour) on change.</summary>
internal sealed class MarqueeHost : Component
{
    public Prop<string> Text = string.Empty;
    public Marquee.Style Sty = Marquee.Default;
    public IReadSignal<bool>? External;     // an external "scroll now" gate (a shared group hover) — replaces self-hover

    public override Element Render()
    {
        var containerW = UseSignal(0f);     // set from this node's bounds
        var textW = UseSignal(0f);          // set by the child after it measures one copy
        var hovered = UseSignal(false);     // scroll gate: self-hover (Trigger.Hover), or mirrored from External below

        // An external gate (a group's shared hover) drives the SAME `hovered` signal both this host and the scroller
        // read — so the rest of the logic is untouched and self-hover is skipped (see selfHover). No-op when unset.
        UseSignalEffect(() => { if (External is { } ext) { bool v = ext.Value; if (hovered.Peek() != v) hovered.Value = v; } });

        float cw = containerW.Value, tw = textW.Value;
        bool overflow = tw > cw + 1f && cw > 0f;
        bool active = Sty.Trigger == Marquee.TriggerMode.Always || hovered.Value;
        bool scrolling = Sty.Enabled && overflow && active && !Motion.ReducedMotion;

        EdgeFadeSpec? fade = !overflow ? null
            : scrolling ? new EdgeFadeSpec(EdgeMask.Horizontal, Sty.FadeBand, FadeFalloff.Smoothstep, Sty.FadeStrength)
                        : new EdgeFadeSpec(EdgeMask.Right, Sty.FadeBand, FadeFalloff.Smoothstep, Sty.FadeStrength);

        bool selfHover = Sty.Trigger == Marquee.TriggerMode.Hover && External is null;

        return new BoxEl
        {
            MinWidth = 0f,
            ClipToBounds = true,
            EdgeFade = fade,
            OnBoundsChanged = r => { if (r.W != containerW.Value) containerW.Value = r.W; },
            OnHoverMove = selfHover ? _ => { if (!hovered.Value) hovered.Value = true; } : null,
            OnPointerExit = selfHover ? () => { if (hovered.Value) hovered.Value = false; } : null,
            Children =
            [
                Embed.Comp(() => new MarqueeScroller
                {
                    Text = Text, Sty = Sty, ContainerW = containerW, TextW = textW, Hovered = hovered,
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
    public Marquee.Style Sty = Marquee.Default;
    public Signal<float> ContainerW = null!;
    public Signal<float> TextW = null!;
    public Signal<bool> Hovered = null!;

    public override Element Render()
    {
        float cw = ContainerW.Value;
        float tw = TextW.Value;
        bool active = Sty.Trigger == Marquee.TriggerMode.Always || Hovered.Value;
        bool overflow = tw > cw + 1f && cw > 0f;
        bool scrolling = Sty.Enabled && overflow && active && !Motion.ReducedMotion;

        bool loop = Sty.Mode == Marquee.ScrollMode.Loop;
        bool seamless = loop && scrolling;
        float loopDist = tw + Sty.Gap;
        float tailDist = MathF.Max(0f, tw - cw);

        // One animation hook per Mode (Mode is fixed for an instance, so the hook order is stable across renders).
        if (Sty.Mode == Marquee.ScrollMode.SinglePass)
        {
            UseSpring(AnimChannel.TranslateX, scrolling ? -tailDist : 0f,
                      SpringParams.FromResponse(0.45f, 0.9f), scrolling, tailDist);
        }
        else
        {
            (Keyframe[] keys, float durMs, bool looping) = BuildTrack(loop, scrolling, loopDist, tailDist);
            UseKeyframes(AnimChannel.TranslateX, keys, durMs, looping, scrolling, loopDist, tailDist);
        }

        var copies = new List<Element>(seamless ? 2 : 1) { Measured() };
        if (seamless) copies.Add(Copy());

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
        Children = [Glyphs()],
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

    private (Keyframe[] keys, float durMs, bool loop) BuildTrack(bool loop, bool scrolling, float loopDist, float tailDist)
    {
        if (!scrolling)
            return ([new Keyframe(0f, 0f), new Keyframe(1f, 0f)], 200f, false);

        float speed = MathF.Max(1f, Sty.Speed);
        // A fixed CycleMs (when set) makes the traversal time width-INDEPENDENT, so sibling lines with the same CycleMs
        // share one cadence (synced); otherwise the time is loopDist/speed for a constant velocity.
        bool fixedCycle = Sty.CycleMs > 0f;

        if (loop)
        {
            float travel = fixedCycle ? Sty.CycleMs : loopDist / speed * 1000f;
            float dur = MathF.Max(1f, travel + Sty.StartDelayMs);
            float delayFrac = Sty.StartDelayMs / dur;
            return (
            [
                new Keyframe(0f, 0f, Easing.Linear),
                new Keyframe(delayFrac, 0f, Easing.Linear),
                new Keyframe(1f, -loopDist, Easing.Linear),
            ], dur, true);
        }

        // PingPong: pause, out to the tail, pause, back.
        float travelP = fixedCycle ? Sty.CycleMs : tailDist / speed * 1000f;
        float pause = Sty.EndPauseMs;
        float total = MathF.Max(1f, 2f * travelP + 2f * pause);
        float f1 = pause / total;
        float f2 = f1 + travelP / total;
        float f3 = f2 + pause / total;
        return (
        [
            new Keyframe(0f, 0f, Easing.Linear),
            new Keyframe(f1, 0f, Easing.Linear),
            new Keyframe(f2, -tailDist, Easing.Linear),
            new Keyframe(f3, -tailDist, Easing.Linear),
            new Keyframe(1f, 0f, Easing.Linear),
        ], total, true);
    }
}
