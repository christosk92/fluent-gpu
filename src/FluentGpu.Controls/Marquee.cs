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
        /// <summary>Hold (ms) showing the START of the text before it scrolls. It is part of the cycle, not a one-time
        /// lead-in: the first traversal begins after this delay, and in <see cref="ScrollMode.Loop"/> /
        /// <see cref="ScrollMode.PingPong"/> every subsequent cycle opens with the same hold at translate 0. A
        /// <see cref="TriggerMode.Always"/> title with e.g. 2 s rests readable at its head before each pass.</summary>
        public float StartDelayMs { get; init; } = 350f;
        public float EndPauseMs { get; init; } = 900f;    // pause at the tail before bouncing back (PingPong) / loop reset (Loop)
        public ScrollMode Mode { get; init; } = ScrollMode.Loop;
        public TriggerMode Trigger { get; init; } = TriggerMode.Always;
        public bool Enabled { get; init; } = true;
        /// <summary>Park the scroll while the window is in the BACKGROUND - not the focused window, or covered/cloaked
        /// (<see cref="InputHooks.WindowOccluded"/>). Parking glides the content home (the same return a deactivated trigger
        /// makes) and frees the loop row: an unattended title nobody can read must not hold a render wake and a present at
        /// 30-60 Hz for the whole song (F239). The scroll resumes from its start when the window is foregrounded again.
        /// A scroll the pointer drives (<see cref="TriggerMode.Hover"/> with the pointer over it) is never parked.
        /// Default on; set false for a marquee that must keep moving behind another window (an ambient display).</summary>
        public bool ParkInBackground { get; init; } = true;
        /// <summary>With <see cref="ScrollMode.PingPong"/> and a positive <see cref="CycleMs"/>: every row takes the SAME
        /// cycle length (<see cref="StartDelayMs"/> + <see cref="EndPauseMs"/> + 2 x <see cref="CycleMs"/>) whatever its own
        /// tail, a short tail simply resting longer at its ends. Sibling lines (a title over an artist line) then start,
        /// travel and rest together instead of drifting out of phase - so the window changes pixels in ONE shared span,
        /// not the union of two offset ones. Ignored for the other modes.</summary>
        public bool SyncCycle { get; init; }
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
    /// <see cref="TriggerMode.PauseOnHover"/> it pauses while true. Either way, deactivating glides the content back to
    /// its head (translate 0) from wherever it is — it is never frozen mid-scroll. Null = self-hover. The edge fade is
    /// unaffected (right-edge cue at rest, both edges while scrolling).</param>
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

    /// <summary>Above this speed (device px/s) one 30 Hz sample would move the text by more than ~1.2 px, so the row samples
    /// at 60 Hz instead; at or below it a step is at most about one pixel at 30 Hz.</summary>
    internal const float FastStepPxPerSec = 36f;

    public override Element Render()
    {
        var hooks = UseContext(InputHooks.Current);
        float scale = UseContext(Viewport.Scale);
        // A background window's marquee is read by nobody: park it (glide home) while the window is not the focused one or is
        // covered/cloaked. Both reads subscribe, so a focus flip or an occlusion edge re-renders this scroller and re-seeds
        // the track through the same `paused` edge a deactivated trigger uses. A scroll the pointer is DRIVING (Hover mode,
        // pointer over it) is never parked: it is user input, and the unfocused window's cut-off title must stay readable.
        _ = hooks.WindowChromeEpoch?.Value;
        bool hoverDriven = Sty.Trigger == Marquee.TriggerMode.Hover && Hovered.Value;
        bool background = Sty.ParkInBackground && !hoverDriven
            && (!(hooks.IsWindowActive?.Invoke() ?? true) || hooks.WindowOccluded?.Value == true);

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
        bool paused = canScroll && (!active || background);

        var scrollerHost = UseRef(NodeHandle.Null);
        UseLayoutEffect(() => { scrollerHost.Value = Context.HostNode; }, DepKey.Empty);

        bool loop = Sty.Mode == Marquee.ScrollMode.Loop;
        bool textMode = Content is null;
        bool seamless = textMode && loop && canScroll && !paused;
        float loopDist = tw + Sty.Gap;
        float tailDist = MathF.Max(0f, tw - cw);

        // The trigger deactivating (`paused`) glides the content HOME from wherever the live translate is. Never park
        // the track in place — a park at -tailDist left the title's head off-screen, a partial glyph at x=0 and the
        // edge fade frozen — and never re-seed a "0,0" idle track (that snapped back to start). ScrollX is the ticker's
        // mirror of the TranslateX composed at the end of the previous frame, i.e. exactly what is on screen when this
        // render runs; Keyframes seeds from keys[0] (not the live row), so the departure point must be explicit.
        float homeFrom = paused ? ScrollX.Peek() : 0f;

        // One animation hook per Mode (Mode is fixed for an instance, so the hook order is stable across renders).
        // `paused` is part of every DepKey so the track re-seeds exactly on the trigger edge (a same-key re-render
        // mid-glide leaves the in-flight row alone).
        if (Sty.Mode == Marquee.ScrollMode.SinglePass)
        {
            UseSpring(AnimChannel.TranslateX, canScroll && !paused ? -tailDist : 0f,
                      SpringParams.FromResponse(0.45f, 0.9f),
                      DepKey.From(tailDist, (canScroll ? 1f : 0f) + (paused ? 2f : 0f)));
        }
        else
        {
            (Keyframe[] keys, float durMs, bool looping) = paused
                ? HomeTrack(homeFrom, Sty)
                : BuildTrack(loop, canScroll, loopDist, tailDist);
            // A scrolling title is perpetual (it would default to DefaultLoopHz). Its translate is quantised to WHOLE
            // DEVICE PIXELS (pixelSnap) and sampled at a cadence derived from its speed in device px/s - 30 Hz for a slow
            // title (about one pixel per sample), 60 Hz only for a fast one - so a step either moves the text by a pixel or
            // is a held value the host elides; the old unquantised 60 Hz sample moved it by half a pixel and damaged the
            // whole window every other refresh. The cadence is one of two values on purpose: rows sampling on those
            // periods share one render-side clock, so two marquees step on the same tick. The home glide is a
            // short one-shot and takes the display cadence (null) like every other one-shot (still pixel-snapped).
            float stepHz = StepHz(TravelSpeedDip(Sty, loop ? loopDist : tailDist) * scale);
            UseKeyframes(AnimChannel.TranslateX, keys, durMs, looping,
                         DepKey.From(HashCode.Combine(canScroll, paused, loop, loopDist, tailDist, scale)),
                         cadence: paused ? null : Cadence.At(stepHz), pixelSnap: true);
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

    internal (Keyframe[] keys, float durMs, bool loop) BuildTrack(bool loop, bool canScroll, float loopDist, float tailDist)
    {
        if (!canScroll)
            return ([new Keyframe(0f, 0f), new Keyframe(1f, 0f)], 200f, false);

        // CycleMs caps long traversals to a shared cadence; Speed remains the minimum visible pace for short tails.
        // Thus long sibling lines stay synced without making a one-glyph overflow look stationary.
        bool fixedCycle = Sty.CycleMs > 0f;

        if (loop)
        {
            float travel = TravelMs(Sty, loopDist);
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
        float travelP = TravelMs(Sty, tailDist);
        // SyncCycle: the cycle length is the fixed one (2 x CycleMs of travel budget) for every row, the return leg is
        // anchored to the END of the cycle, and a tail shorter than the budget rests longer at its tail instead.
        bool syncCycle = Sty.SyncCycle && fixedCycle;
        float total = MathF.Max(1f, startPause + endPause + 2f * (syncCycle ? Sty.CycleMs : travelP));
        float f1 = startPause / total;
        float f2 = f1 + travelP / total;
        float f3 = syncCycle ? 1f - travelP / total : f2 + endPause / total;
        return (
        [
            new Keyframe(0f, 0f, Easing.Linear),
            new Keyframe(f1, 0f, Easing.Linear),
            new Keyframe(f2, -tailDist, Easing.Linear),
            new Keyframe(f3, -tailDist, Easing.Linear),
            new Keyframe(1f, 0f, Easing.Linear),
        ], total, true);
    }

    /// <summary>One traversal's duration (ms) of <paramref name="distance"/> under <paramref name="sty"/>: constant velocity at
    /// <see cref="Marquee.Style.Speed"/> (a floor on the pace), capped at <see cref="Marquee.Style.CycleMs"/> when that is set.</summary>
    internal static float TravelMs(Marquee.Style sty, float distance)
    {
        float atMinSpeed = MathF.Max(0f, distance) / MathF.Max(1f, sty.Speed) * 1000f;
        return sty.CycleMs > 0f ? MathF.Min(sty.CycleMs, atMinSpeed) : atMinSpeed;
    }

    /// <summary>The constant speed (DIP/s) of one traversal of <paramref name="distance"/> (see <see cref="TravelMs"/>);
    /// <see cref="Marquee.Style.Speed"/> when the distance is zero.</summary>
    internal static float TravelSpeedDip(Marquee.Style sty, float distance)
    {
        float travelMs = TravelMs(sty, distance);
        return travelMs > 0f ? MathF.Max(0f, distance) / travelMs * 1000f : MathF.Max(1f, sty.Speed);
    }

    /// <summary>The sampling rate (Hz) of a marquee row moving at <paramref name="pxPerSec"/> device px/s: 30 Hz while a sample
    /// moves it by about a pixel or less, 60 Hz above <see cref="FastStepPxPerSec"/>. Two values only, so every marquee's
    /// period lands on the render thread's shared per-period clock.</summary>
    internal static float StepHz(float pxPerSec) => pxPerSec > FastStepPxPerSec ? 60f : 30f;

    /// <summary>The trigger-deactivated return: a ONE-SHOT from the LIVE translate <paramref name="fromX"/> back to 0
    /// (the rest pose, where <see cref="ResolveEdgeFade"/> yields the right-edge overflow cue only). Pace is four times
    /// <see cref="Marquee.Style.Speed"/> — a return, not a re-read — clamped to 120..450 ms so a one-glyph offset still
    /// reads as motion and a full-tail return never drags. Already home (|x| &lt; 0.5) ⇒ a 1 ms no-op at 0 (the row
    /// settles next tick and frees; nothing is parked). Pure and engine-free so the gate can pin its shape.</summary>
    internal static (Keyframe[] keys, float durMs, bool loop) HomeTrack(float fromX, Marquee.Style sty)
    {
        float dist = MathF.Abs(fromX);
        if (dist < 0.5f)
            return ([new Keyframe(0f, 0f, Easing.Linear), new Keyframe(1f, 0f, Easing.Linear)], 1f, false);

        float speed = MathF.Max(1f, sty.Speed * 4f);
        float durMs = Math.Clamp(dist / speed * 1000f, 120f, 450f);
        return ([new Keyframe(0f, fromX, Easing.Linear), new Keyframe(1f, 0f, Easing.SmoothOut)], durMs, false);
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
/// A run-once <see cref="Component"/> (its render reads only a stable ambient context, so the render-effect never
/// re-fires) + <see cref="InputHooks.SetAfterAnimations"/> avoids the stale-closure trap of wiring this inside
/// <see cref="MarqueeScroller.Render"/> (where <c>UseSignalEffect</c> freezes <c>canScroll</c> from the first mount).</summary>
internal sealed class MarqueeScrollTicker : Component
{
    public Signal<float> ContainerW = null!;
    public Signal<float> TextW = null!;
    public Signal<float> ScrollX = null!;
    public Ref<NodeHandle> ScrollerHost = null!;

    public override Element Render()
    {
        var hooks = UseContext(InputHooks.Current);
        UseEffect(() => hooks.SetAfterAnimations(this, Sample), DepKey.Empty);   // mount-once (no signal reads)
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
