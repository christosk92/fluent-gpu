using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>Post-invoke behavior (kept from the WinUI-shaped surface for source compatibility). Auto and Close both
/// close the control after the invoke; RemainOpen settles back to the open rest.</summary>
public enum SwipeBehaviorOnInvoked : byte { Auto = 0, Close = 1, RemainOpen = 2 }

/// <summary>Legacy side mode (kept for source compatibility). Maps onto <see cref="SwipeSide.FullSwipe"/>:
/// Execute → FullSwipe=true (free travel + the full-swipe arm/morph/commit), Reveal → FullSwipe=false
/// (rubber-band past the cluster; tap-to-invoke only).</summary>
public enum SwipeMode : byte { Reveal = 0, Execute = 1 }

/// <summary>One swipe action: an <see cref="Icon"/> (a PUA glyph OR a layered ThemedIcon — the polymorphic
/// <see cref="IconRef"/>; a bare glyph string converts implicitly, so every positional call-site is unchanged), a
/// label, an optional custom capsule <paramref name="Color"/> (flips the content to on-accent text), the
/// <see cref="OnInvoked"/> callback and <see cref="BehaviorOnInvoked"/>.</summary>
public sealed record SwipeAction(IconRef Icon, string Label, ColorF? Color = null)
{
    /// <summary>Raised on a capsule tap or, for a FullSwipe side, when a committed full swipe lands (invoked only
    /// after the release — a threshold crossing can still be reversed before lift and never fires early).</summary>
    public Action? OnInvoked { get; init; }
    /// <summary>Default Auto — closes after the invoke unless RemainOpen.</summary>
    public SwipeBehaviorOnInvoked BehaviorOnInvoked { get; init; } = SwipeBehaviorOnInvoked.Auto;
    /// <summary>Optional foreground override for a custom plate (for example critical text on a soft critical tint).</summary>
    public ColorF? Foreground { get; init; }
    /// <summary>Dynamic visual sources for recycled/bound rows. Evaluated from the core render computation.</summary>
    public Func<IconRef>? IconSource { get; init; }
    public Func<string>? LabelSource { get; init; }
    public Func<bool>? IsEnabledSource { get; init; }
}

/// <summary>One side's swipe configuration (the iOS <c>UISwipeActionsConfiguration</c> shape). <c>Actions[0]</c> is
/// the PRIMARY — the OUTERMOST capsule (nearest the control edge) on both sides, and the one a full swipe commits.</summary>
public sealed record SwipeSide(IReadOnlyList<SwipeAction> Actions)
{
    /// <summary>A full swipe (dragged/projected past half the control width) commits <c>Actions[0]</c> on release —
    /// iOS <c>performsFirstActionWithFullSwipe</c>. Default true. False sides rubber-band past the cluster instead.</summary>
    public bool FullSwipe { get; init; } = true;
    public static SwipeSide Of(params SwipeAction[] actions) => new(actions);
}

/// <summary>Single-open coordination across a list of SwipeControls: the wrapper whose pan CLAIMS (or rests open)
/// registers its close here, and the NEXT row's claim closes the previously open one first — so at most one row in the
/// group is ever open. Share ONE instance per list (a <c>UseRef</c>); pass it to <see cref="SwipeControl.Create"/>.</summary>
public sealed class SwipeGroup
{
    internal Action? CloseLast;
    object? _openToken;
    /// <summary>True while a member row is currently open (settled-open or fling-committed). A list's scroll observer
    /// gates its per-frame OffsetY projection on this so nothing is dispatched while every row is shut — the observer
    /// only exists to close the open row on scroll. Single-open by design, so one token (the open row's stable
    /// group-close delegate identity) tracks which member is the open one.</summary>
    public bool AnyOpen => _openToken is not null;
    internal void MarkOpen(object token) => _openToken = token;
    internal void MarkClosed(object token) { if (ReferenceEquals(_openToken, token)) _openToken = null; }
    /// <summary>Close the currently open member, if any (list scroll/focus-loss integration).</summary>
    public void Close() => CloseLast?.Invoke();
}

/// <summary>Swipe-to-action rows with the iOS 26 Mail behavior model (measured frame-by-frame from a recording):
/// the content tracks the finger 1:1 as an elevated card, compact capsule buttons are BORN under it with a spring
/// catch-up lag and proportional distribution, a FullSwipe side arms at half the control width (the primary capsule
/// morphs to fill the row) and commits on release past the threshold, and every release resolves by position +
/// projected fling velocity. Horizontal (leading/trailing) actions only. Deliberate superset of the phone gesture:
/// ours pans with the mouse too unless <c>touchOnly</c>.</summary>
public static class SwipeControl
{
    /// <summary>String-content convenience (the gallery surface): a card-filled text cell with TRAILING actions.
    /// The cell is given a 320px MinWidth so the collapsed control leaves a swipeable surface.</summary>
    public static Element Create(string content, IReadOnlyList<SwipeAction> actions, SwipeMode mode = SwipeMode.Reveal)
        => Create(new BoxEl
        {
            MinWidth = 320f,   // gallery affordance only
            Padding = new Edges4(16, 14, 16, 14),
            Fill = Tok.FillCardDefault,
            AlignItems = FlexAlign.Center,
            Children = [new TextEl(content) { Size = 14f, Color = Tok.TextPrimary }],
        }, rightActions: actions, rightMode: mode);

    /// <summary>The iOS-shaped surface: <paramref name="leading"/> is revealed by swiping RIGHT (the left cluster),
    /// <paramref name="trailing"/> by swiping LEFT (the right cluster). <paramref name="group"/> gives single-open
    /// coordination across a list; <paramref name="resetKey"/> (a bound row slot's index signal) snap-closes on
    /// recycle; <paramref name="touchOnly"/> is the belt that a mouse/touchpad never pans this control.</summary>
    public static Element Create(Element content, SwipeSide? leading = null, SwipeSide? trailing = null,
                                 SwipeGroup? group = null, IReadSignal<int>? resetKey = null, bool touchOnly = false,
                                 ColorF? contentFill = null, CornerRadius4? corners = null, Edges4 margin = default)
        => Embed.Comp(new Props(content, Normalize(leading), Normalize(trailing), group, resetKey, touchOnly,
                                contentFill, corners ?? default, margin),
                      () => new SwipeControlCore());

    /// <summary>Legacy WinUI-shaped overload (kept source-compatible; maps onto <see cref="SwipeSide"/>):
    /// left/right action lists with per-side <see cref="SwipeMode"/> — Execute ⇒ FullSwipe, Reveal ⇒ tap-only.</summary>
    public static Element Create(Element content,
                                 IReadOnlyList<SwipeAction>? leftActions = null,
                                 IReadOnlyList<SwipeAction>? rightActions = null,
                                 SwipeMode leftMode = SwipeMode.Reveal,
                                 SwipeMode rightMode = SwipeMode.Reveal,
                                 SwipeGroup? group = null,
                                 IReadSignal<int>? resetKey = null,
                                 bool touchOnly = false,
                                 ColorF? contentFill = null,
                                 CornerRadius4? corners = null,
                                 Edges4 margin = default)
        => Create(content,
                  leading: leftActions is { Count: > 0 } ? new SwipeSide(leftActions) { FullSwipe = leftMode == SwipeMode.Execute } : null,
                  trailing: rightActions is { Count: > 0 } ? new SwipeSide(rightActions) { FullSwipe = rightMode == SwipeMode.Execute } : null,
                  group: group, resetKey: resetKey, touchOnly: touchOnly, contentFill: contentFill, corners: corners,
                  margin: margin);

    static SwipeSide? Normalize(SwipeSide? side) => side is { Actions.Count: > 0 } ? side : null;

    /// <summary>Controlled props RE-PUSHED to the core (<c>Embed.Comp(props, …)</c>) — a reused ComponentEl never
    /// re-runs its factory — so props are delivered live (equality-gated); the core reads them with <c>UseProps</c>
    /// (the SelectorBar/PipsPager convention). ONE model: both public overloads normalize here.</summary>
    internal sealed record Props(Element Content, SwipeSide? Leading, SwipeSide? Trailing,
                                 SwipeGroup? Group = null, IReadSignal<int>? ResetKey = null, bool TouchOnly = false,
                                 ColorF? ContentFill = null, CornerRadius4 Corners = default, Edges4 Margin = default);
}

/// <summary>The stateful core: 1:1 pan tracking, capsule birth/distribution springs, the full-swipe arm/morph,
/// position+projected-velocity release resolution, item invocation, and the Esc/tap dismissal plumbing.</summary>
internal sealed class SwipeControlCore : Component
{
    // ── Geometry (capsule cluster) ────────────────────────────────────────────────────────────────────────────
    const float EdgeInsetPx = 10f;         // cluster inset from the revealed control edge (measured iOS 26 Mail)
    const float CapsuleGapPx = 8f;         // gap between capsules
    const float CapsuleMinWidthPx = 52f;   // capsule width ≥ max(52, height×1.3)
    const float CapsuleWidthRatio = 1.3f;
    const float CapsuleHeightPad = 22f;    // capsuleH = clamp(controlH − 22, 26, 44) — leaves room for the label row
    const float CapsuleMinHeight = 26f;
    const float CapsuleMaxHeight = 44f;
    const float LabelMinControlH = 52f;    // label under the capsule only when the row is tall enough (the iOS look)
    const float IconSizePx = 17f;
    const float FallbackControlW = 300f;   // pre-layout fallbacks (the root is laid out by the first PanMove in practice)
    const float FallbackControlH = 56f;

    // ── Gesture / resolution ─────────────────────────────────────────────────────────────────────────────────
    const float PanSlopPx = 4f;             // the engine drag-box convention
    const float OverdragFraction = 0.20f;   // Reveal sides: bounded elastic travel beyond the cluster extent
    const float ArmFraction = 0.5f;         // full-swipe arm ceiling at 0.5×width — measured iOS 26 Mail: armed 599-650px on a 1206px screen
    // The arm threshold is CLUSTER-TIED, ceilinged by 0.5×width. On a phone-width row the cluster nearly reaches
    // half-width so both agree (iOS never shows a gap between the buttons and the arm point); on a desktop-width row
    // a bare 0.5×width would leave hundreds of px of dead band past a ~90px cluster before anything armed.
    const float MinArmSlackPx = 80f;        // arming must stay a distinct, deliberate step past resting open
    const float MaxArmSlackPx = 140f;       // …but never a long empty pull on a wide row
    const float OpenFraction = 0.6f;        // fresh open: projected rest ≥ 0.6×cluster width
    const float OpenHysteresis = 0.50f;     // an already-open row closes only after crossing halfway home
    const float CloseVelocityPxPerS = 31f;  // the floor speed an inward release must carry to close an open row
    const float RevealNeedFraction = 0.8f;  // capsule i pops to full scale once |offset| ≥ 0.8×its cumulative reveal need
    const float UnrevealedScale = 0.55f;    // capsule scale while its slot is not yet revealed
    const float UnrevealedOpacity = 0.35f;
    const float DimmedSecondaryOpacity = 0.35f;   // secondaries dim (in place) while the primary is armed/expanded
    const long CommitInvokeDelayMs = 320;   // full-swipe commit: invoke once the fling visually lands (~320ms)
    const long CloseUnmountDelayMs = 420;   // unmount the reveal layer after the close settle spring rests

    // ── Springs ──────────────────────────────────────────────────────────────────────────────────────────────
    // Birth/distribution: a visible ~150-200ms catch-up lag behind the finger, slightly lively (measured iOS).
    static readonly SpringParams BirthSpring = SpringParams.FromResponse(0.30f, 0.80f);
    // Full-swipe morph (primary expansion / icon ride / secondary dim): ~180ms perceived.
    static readonly SpringParams MorphSpring = SpringParams.FromResponse(0.35f, 0.85f);
    // Release settle (open/commit/release-close) with the lift velocity injected: fast flick ≈160ms, slow ≈300ms,
    // at most a tiny pass-through, no wobble.
    static readonly SpringParams SettleSpring = SpringParams.FromResponse(0.38f, 0.95f);
    // Programmatic dismissals (Esc, outside-press, focus loss, window blur, group close) carry no gesture velocity —
    // they get the stiffer ~150ms critical snap (iOS dismissals are snappier than drag-release settles).
    static readonly SpringParams DismissSpring = SpringParams.FromResponse(0.15f, 1.0f);

    // Fling-distance projection divisor for the release decision. A release of speed v (px/s) coasts an extra
    // v / FlingProjectK px in the snap-settle window before resting. Derived from the engine scroller decay
    // (ScrollIntegrator.FlingDecayPerS = 0.05/s survival) over the ControlNormal settle window T:
    // coast = v·(1−decay^T)/−ln(decay), so the divisor is −ln(decay)/(1−decay^T) ≈ 4.0. A BOUNDED window (not the
    // full infinite-decay scroll coast) is the right model for a threshold snap — a slow drag projects only a
    // little, a fast flick projects past the threshold. Equivalent in spirit to the WWDC projection
    // distance = (v/1000)·rate/(1−rate).
    static readonly float FlingProjectK = ProjectDivisor(ScrollIntegrator.FlingDecayPerS, Motion.ControlNormal / 1000f);
    static float ProjectDivisor(float decayPerS, float windowS)
    {
        float k = -MathF.Log(decayPerS);                 // the per-second decay rate (−ln survival)
        float frac = 1f - MathF.Exp(-k * windowS);       // fraction of the full coast reached within the window
        return frac > 1e-4f ? k / frac : k;              // divisor: projectedExtra = v / divisor = v·frac/k
    }

    // ── Pure resolution statics (unit-tested by gate.arena.swipe-phone-settle) ──────────────────────────────────

    // Phone-like bounded overdrag for a Reveal (FullSwipe=false) side: continuous at the cluster extent, 0.2 initial
    // gain, asymptotically capped at an additional 20% of that side's width.
    internal static float ResistPastExtent(float raw, float min, float max)
    {
        if (raw > max)
        {
            float extent = MathF.Max(1f, max);
            float excess = raw - max;
            return max + OverdragFraction * extent * excess / (extent + excess);
        }
        if (raw < min)
        {
            float extent = MathF.Max(1f, -min);
            float excess = min - raw;
            return min - OverdragFraction * extent * excess / (extent + excess);
        }
        return raw;
    }

    /// <summary>Signed drag offset for a raw pointer offset: a FullSwipe side tracks 1:1 out to the full control
    /// width (the full-swipe zone needs the travel — NO rubber band), a Reveal side rubber-bands past its cluster.</summary>
    internal static float DragOffset(float raw, float leftExtent, float rightExtent,
                                     bool leftFullSwipe, bool rightFullSwipe, float controlW)
    {
        if (raw > 0f)
            return leftFullSwipe ? MathF.Min(raw, controlW)
                                 : ResistPastExtent(raw, -rightExtent, leftExtent);
        if (raw < 0f)
            return rightFullSwipe ? MathF.Max(raw, -controlW)
                                  : ResistPastExtent(raw, -rightExtent, leftExtent);
        return 0f;
    }

    /// <summary>Projected resting distance: |offset| plus the bounded fling coast of the outward release speed.</summary>
    internal static float ProjectedDistance(float distancePx, float outwardVelocityPxPerS)
        => MathF.Max(0f, distancePx + outwardVelocityPxPerS / FlingProjectK);

    /// <summary>Release → rest-open decision. Fresh: projected ≥ 0.6×cluster (a fast short flick opens; a reversed
    /// flick cancels what position alone would have opened). Already open: halfway hysteresis so finger wobble does
    /// not collapse the row, while a deliberate ≥31px/s inward flick closes decisively.</summary>
    internal static bool ShouldRestOpen(bool wasOpen, float distancePx, float extentPx, float outwardVelocityPxPerS)
    {
        float projected = ProjectedDistance(distancePx, outwardVelocityPxPerS);
        if (!wasOpen) return projected >= extentPx * OpenFraction;
        return projected >= extentPx * OpenHysteresis && outwardVelocityPxPerS > -CloseVelocityPxPerS;
    }

    /// <summary>The full-swipe arm threshold: cluster width + a bounded slack, ceilinged by 0.5×controlW. Phone-width
    /// rows resolve to ≈0.5×width (the measured iOS value); wide desktop rows arm just past the cluster instead of
    /// after a long dead pull.</summary>
    internal static float ArmThreshold(float controlWPx, float clusterWPx)
        => clusterWPx + Math.Clamp(controlWPx * ArmFraction - clusterWPx, MinArmSlackPx, MaxArmSlackPx);

    /// <summary>The full-swipe arm predicate against <see cref="ArmThreshold"/>.</summary>
    internal static bool IsArmed(float absOffsetPx, float armThresholdPx) => absOffsetPx >= armThresholdPx;

    /// <summary>Commit predicate on release: the PROJECTED rest must sit past the arm threshold. Nothing commits
    /// during the drag itself.</summary>
    internal static bool ShouldCommit(float projectedPx, float armThresholdPx) => projectedPx >= armThresholdPx;

    /// <summary>Presented primary width while armed. The plate follows the live reveal rather than jumping to the
    /// full control width, keeping its riding icon inside the gap between the control edge and the moving card.</summary>
    internal static float ExpandedPrimaryWidth(float absOffsetPx, float restWidthPx, float secondaryExtentPx)
        => MathF.Max(restWidthPx, absOffsetPx - 2f * EdgeInsetPx - secondaryExtentPx);

    // ── Structural transitions ───────────────────────────────────────────────────────────────────────────────
    // Arm cue (the "releasing now commits" moment): re-key the primary icon so a flip REMOUNTS it with a spring
    // overshoot pop — the TrackRow HeartPopIn pattern (spring dynamics survive the reduced-motion easing policy).
    static readonly LayoutTransition ThresholdPop = new(
        TransitionChannels.Opacity,
        TransitionDynamics.Spring(0.30f, 0.55f),   // low damping → the overshoot pop
        Enter: new EnterExit(Sx: 0.4f, Sy: 0.4f, Opacity: 0.4f, Active: true));

    // Capsule birth: each capsule seeds at scale 0.7 and SPRINGS toward its live target (SwipeCellKit's Mail clone
    // uses 0.8→1.0; the measured recording reads smaller — 0.7 splits it). Only the capsule scales; the label just
    // fades with the column.
    static readonly LayoutTransition CapsuleBirth = new(
        TransitionChannels.Opacity,
        TransitionDynamics.Spring(0.30f, 0.80f),
        Enter: new EnterExit(Sx: 0.7f, Sy: 0.7f, Active: true));

    // Column birth: the whole button (capsule + label) fades in from 0.
    static readonly LayoutTransition ColumnBirth = new(
        TransitionChannels.Opacity,
        TransitionDynamics.Spring(0.30f, 0.80f),
        Enter: new EnterExit(Opacity: 0f, Active: true));

    /// <summary>A pressed COLORED capsule dips to 80% fill alpha (the gray pressed plate is wrong on saturated
    /// fills); neutral capsules keep the standard pressed plate.</summary>
    static ColorF PressDip(ColorF fill) => fill with { A = fill.A * 0.8f };

    // The primary's icon slot: wrapped + keyed on the armed flip so the crossing REMOUNTS it with the overshoot pop;
    // the wrapper is also the node the LEFT-side morph translates so the icon rides the expanding inner edge.
    static Element SwipeItemIcon(IconRef iconRef, ColorF fg, bool onAccent, bool primaryFullSwipe, bool armedNow,
                                 Action<NodeHandle>? onRealized)
    {
        // Flatten layered icons to their neutral/on-accent role on a colored capsule. Otherwise a checked icon whose
        // only layer is Accent (HeartFill) is the exact same blue as the Accent capsule and appears to vanish.
        Element icon = IconView.Render(iconRef, IconSizePx, fg,
                                       themedColor: onAccent ? IconColorType.Neutral : IconColorType.Normal,
                                       onAccent: onAccent);
        if (!primaryFullSwipe) return icon;
        return new BoxEl
        {
            Key = armedNow ? "sw-ic:on" : "sw-ic:off",
            Animate = armedNow ? ThresholdPop : null,
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            OnRealized = onRealized,
            Children = [icon],
        };
    }

    public override Element Render()
    {
        // Hooks — stable order, unconditionally.
        var props = UseProps<SwipeControl.Props>();
        var hooks = UseContext(InputHooks.Current);
        var contentRef = UseRef<NodeHandle>(default);
        var rootRef = UseRef<NodeHandle>(default);
        var stackRef = UseRef<NodeHandle>(default);      // the capsule cluster (incl. edge insets) — the open rest extent
        var revealRef = UseRef<NodeHandle>(default);     // reveal host, clipped to the finger-exposed gap
        var colRefs = UseRef(System.Array.Empty<NodeHandle>());   // per-button column nodes (edge order; rebuilt on side mount)
        var capRefs = UseRef(System.Array.Empty<NodeHandle>());   // per-button capsule nodes (edge order)
        var primaryIconRef = UseRef<NodeHandle>(default);         // the primary's icon wrapper (left-side morph ride)
        var builtSide = UseRef(0);                       // which side the ref arrays were built for
        var panKeys = UseRef(new Keyframe[2]);           // reused pan-follow keyframes (0-alloc per pointer move)
        var clipLKeys = UseRef(new Keyframe[2]);         // Keyframes retains arrays, so each channel owns one
        var clipRKeys = UseRef(new Keyframe[2]);
        var panStart = UseRef(0f);
        var panBase = UseRef(0f);
        var panning = UseRef(false);
        var offset = UseRef(0f);                         // current content translation (px; + = leading revealed)
        var side = UseSignal(0);                         // +1 = leading (left cluster), -1 = trailing (right), 0 = none mounted
        var isOpen = UseSignal(false);
        var armed = UseSignal(false);                    // transient full-swipe sub-state while dragging past 0.5×controlW
        var closeDeadline = UseRef(0L);
        var closePending = UseSignal(false);             // unmounts the reveal layer once the close settle lands
        var execDeadline = UseRef(0L);
        var execPending = UseSignal(false);              // full-swipe commit: invoke after the fling visually lands
        var escPreview = UseRef<Func<int, bool>?>(null);
        var savedPreview = UseRef<Func<int, bool>?>(null);
        var escInstalled = UseRef(false);
        var closeAction = UseRef<Action?>(null);
        var groupCloseAction = UseRef<Action?>(null);
        var outsidePress = UseRef<Action<Point2>?>(null);
        var windowBlur = UseRef<Action?>(null);
        var scrollStarted = UseRef<Action?>(null);
        var lastKind = UseRef(PointerKind.Touch);        // TouchOnly belt: kind of the last DIRECT press on the root
                                                         // (a walk-found row-swipe never presses the root ⇒ stays Touch)

        var p = props;

        float LiveOffset()
            => Context.Anim is { } a && !contentRef.Value.IsNull &&
               a.TryGetTrackValue(contentRef.Value, AnimChannel.TranslateX, out float v)
                ? v
                : offset.Value;

        float ControlWidth()
        {
            var scene = Context.Scene;
            if (scene is not null && !rootRef.Value.IsNull && scene.IsLive(rootRef.Value))
            {
                float w = scene.Bounds(rootRef.Value).W;
                if (w > 0f) return w;
            }
            return FallbackControlW;
        }

        float ControlHeight()
        {
            var scene = Context.Scene;
            if (scene is not null && !rootRef.Value.IsNull && scene.IsLive(rootRef.Value))
            {
                float h = scene.Bounds(rootRef.Value).H;
                if (h > 0f) return h;
            }
            return FallbackControlH;
        }

        // The mounted cluster's laid-out width (capsules + gaps + both edge insets) — the open resting extent and the
        // fresh-open threshold base. Falls back to the capsule metric until the cluster is laid out.
        float StackWidth(SwipeSide cfg)
        {
            var scene = Context.Scene;
            if (scene is not null && !stackRef.Value.IsNull && scene.IsLive(stackRef.Value))
            {
                float bw = scene.Bounds(stackRef.Value).W;
                if (bw > 0f) return bw;
            }
            int n = cfg.Actions.Count;
            return n * CapsuleMinWidthPx + (n - 1) * CapsuleGapPx + 2f * EdgeInsetPx;
        }

        void PinRevealClip(int s, float absOff, float controlW)
        {
            var node = revealRef.Value;
            var anim = Context.Anim;
            if (s == 0 || node.IsNull || anim is null) return;
            float l = s > 0 ? 0f : MathF.Max(0f, controlW - absOff);
            float r = s > 0 ? MathF.Min(controlW, absOff) : controlW;
            var lk = clipLKeys.Value;
            lk[0] = new Keyframe(0f, l, Easing.Linear);
            lk[1] = new Keyframe(1f, l, Easing.Linear);
            var rk = clipRKeys.Value;
            rk[0] = new Keyframe(0f, r, Easing.Linear);
            rk[1] = new Keyframe(1f, r, Easing.Linear);
            anim.Keyframes(node, AnimChannel.ClipL, lk, 1f);
            anim.Keyframes(node, AnimChannel.ClipR, rk, 1f);
        }

        void SpringRevealClip(float to, in SpringParams sp, float releaseVelocityPxPerS)
        {
            var node = revealRef.Value;
            var anim = Context.Anim;
            int s = to > 0f ? 1 : to < 0f ? -1 : side.Peek();
            if (s == 0 || node.IsNull || anim is null) return;
            float controlW = ControlWidth();
            float absTo = MathF.Abs(to);
            if (s > 0)
            {
                anim.Spring(node, AnimChannel.ClipL, 0f, in sp);
                anim.Spring(node, AnimChannel.ClipR, absTo, in sp, initialVelocity: releaseVelocityPxPerS);
            }
            else
            {
                anim.Spring(node, AnimChannel.ClipL, controlW - absTo, in sp, initialVelocity: releaseVelocityPxPerS);
                anim.Spring(node, AnimChannel.ClipR, controlW, in sp);
            }
        }

        void SpringContentTo(float to, in SpringParams sp, float releaseVelocityPxPerS)
        {
            if (contentRef.Value.IsNull) return;
            // The pan pin is a Keyframes row → Spring takes the FRESH-seed path from the pinned value with the lift
            // velocity injected; a settle already in flight retargets velocity-continuously instead.
            Context.Anim?.Spring(contentRef.Value, AnimChannel.TranslateX, to, in sp,
                                 initialVelocity: releaseVelocityPxPerS);
            SpringRevealClip(to, in sp, releaseVelocityPxPerS);
            offset.Value = to;
        }

        // ── Per-move capsule choreography: retarget every button's springs toward its live target (slab-only,
        //    0-alloc; AnimEngine.Spring retargets velocity-continuously, so calling it per PanMove is snap-free).
        //    This is the iOS proportional distribution + catch-up lag: buttons park compressed toward the revealed
        //    edge and spread to their laid-out slots as the reveal grows, each popping to full scale as its own slot
        //    uncovers. ─────────────────────────────────────────────────────────────────────────────────────────
        void UpdateButtons(int s, float off, SwipeSide cfg, float clusterW, float controlW)
        {
            var anim = Context.Anim;
            var scene = Context.Scene;
            if (anim is null || scene is null) return;
            var cols = colRefs.Value;
            var caps = capRefs.Value;
            int n = Math.Min(cfg.Actions.Count, Math.Min(cols.Length, caps.Length));
            if (n == 0) return;
            float absOff = MathF.Abs(off);
            bool armedNow = cfg.FullSwipe && IsArmed(absOff, ArmThreshold(controlW, clusterW));
            if (armed.Peek() != armedNow) armed.Value = armedNow;   // re-renders only the icon re-key pop
            float edgeDir = s > 0 ? -1f : 1f;   // toward the revealed control edge (the leading cluster sits at the left edge)

            float ColW(int i)
            {
                var c = cols[i];
                if (!c.IsNull && scene.IsLive(c))
                {
                    float w = scene.Bounds(c).W;
                    if (w > 0f) return w;
                }
                return CapsuleMinWidthPx;
            }

            float cum = EdgeInsetPx;      // cumulative width from the revealed edge through button i
            float secExtent = 0f;         // secondaries' extent (+ their gaps) — the space the expanded primary spares
            for (int i = 1; i < n; i++) secExtent += ColW(i) + CapsuleGapPx;

            for (int i = 0; i < n; i++)
            {
                float w = ColW(i);
                float needed = cum + w;   // per-button reveal need: the edge-through-i extent
                cum += w + CapsuleGapPx;
                var col = cols[i];
                if (col.IsNull || !scene.IsLive(col)) continue;
                bool revealed = absOff >= needed * RevealNeedFraction;
                // Armed secondaries: the trailing side parks them dimmed beside the expansion (measured iOS 26); the
                // LEADING side's origin-anchored expansion sweeps OVER their slots and flex order would paint them on
                // top of it — so leading arms fade them out entirely instead.
                float opT = armedNow && i > 0 ? (s > 0 ? 0f : DimmedSecondaryOpacity)
                          : revealed ? 1f : UnrevealedOpacity;
                // Distribution shift: pushed toward its edge while unrevealed, settling to 0 as the cluster fully
                // reveals — the linearized proportional compression (the innermost capsule displaces the most).
                float shift = edgeDir * MathF.Max(0f, clusterW - absOff) * (i + 1) / n;
                anim.Spring(col, AnimChannel.TranslateX, shift, in BirthSpring);
                anim.Spring(col, AnimChannel.Opacity, opT, in BirthSpring);
                var cap = caps[i];
                if (cap.IsNull || !scene.IsLive(cap)) continue;
                float scaleT = revealed ? 1f : UnrevealedScale;
                anim.Spring(cap, AnimChannel.ScaleX, scaleT, in BirthSpring);
                anim.Spring(cap, AnimChannel.ScaleY, scaleT, in BirthSpring);
            }

            // Full-swipe morph: the PRIMARY capsule expands toward the row width via the presented SizeW (fill +
            // child clip at the presented size, NO relayout); fully reversible — a disarm retargets the same springs
            // back to rest, symmetric.
            if (cfg.FullSwipe)
            {
                var cap0 = caps[0];
                if (cap0.IsNull || !scene.IsLive(cap0)) return;
                float restW = scene.Bounds(cap0).W;
                if (restW <= 0f) restW = CapsuleMinWidthPx;
                // Expand only through the space the finger has actually uncovered. Expanding immediately to the
                // control width pushes the riding icon underneath the opaque card on wide rows. At full commit
                // absOff == controlW, so the plate still reaches its full extent.
                float expandedW = ExpandedPrimaryWidth(absOff, restW, secExtent);
                anim.Spring(cap0, AnimChannel.SizeW, armedNow ? expandedW : restW, in MorphSpring);
                if (s < 0)
                {
                    // Right side: SizeW grows rightward from the node origin, so counter-translate by −Δ to keep the
                    // right edge anchored at the edge inset — the origin (and the icon, placed at the box start)
                    // rides the expanding inner edge, exactly like iOS.
                    anim.Spring(cap0, AnimChannel.TranslateX, armedNow ? -(expandedW - restW) : 0f, in MorphSpring);
                }
                else
                {
                    // Left side: the origin is already edge-anchored; ride the ICON toward the expanding inner edge
                    // (it keeps the same restW/2 stand-off from the riding edge the right side gets for free).
                    var ic = primaryIconRef.Value;
                    if (!ic.IsNull && scene.IsLive(ic))
                        anim.Spring(ic, AnimChannel.TranslateX, armedNow ? MathF.Max(0f, expandedW - restW) : 0f, in MorphSpring);
                }
            }
        }

        void CloseCore(float releaseVelocityPxPerS, in SpringParams sp)
        {
            if (!isOpen.Peek() && side.Peek() == 0) return;
            isOpen.Value = false;
            armed.Value = false;
            // Disarm the morph in the same beat: a committed fling must not park an expanded primary under the card
            // through the close window (the reveal layer stays mounted until the unmount ticker fires).
            var caps = capRefs.Value;
            if (caps.Length > 0 && Context.Anim is { } an && Context.Scene is { } sc
                && !caps[0].IsNull && sc.IsLive(caps[0]))
            {
                float restW = sc.Bounds(caps[0]).W;
                if (restW <= 0f) restW = CapsuleMinWidthPx;
                an.Spring(caps[0], AnimChannel.SizeW, restW, in MorphSpring);
                an.Spring(caps[0], AnimChannel.TranslateX, 0f, in MorphSpring);
                var ic = primaryIconRef.Value;
                if (!ic.IsNull && sc.IsLive(ic)) an.Spring(ic, AnimChannel.TranslateX, 0f, in MorphSpring);
            }
            // Fade the tray while the clip follows the card home. Once the clip spring retires to Infinite, the
            // capsule is already invisible, so a transparent/Mica-backed foreground cannot expose a closing ghost.
            if (Context.Anim is { } hideAnim)
                foreach (var col in colRefs.Value)
                    if (!col.IsNull) hideAnim.Spring(col, AnimChannel.Opacity, 0f, in DismissSpring);
            SpringContentTo(0f, in sp, releaseVelocityPxPerS);
            // Unmount the reveal layer once the settle spring visually rests. Button/morph springs die with the
            // unmount (the engine reclaims a dead node's rows on its next tick).
            closeDeadline.Value = Environment.TickCount64 + CloseUnmountDelayMs;
            closePending.Value = true;
        }
        void Close() => CloseCore(0f, in DismissSpring);

        // Programmatic dismissals animate. Group handoff is atomic: when another row claims the horizontal arena,
        // the previous row must not remain visibly open beside it for the duration of its close spring.
        void SnapClosed()
        {
            if (!isOpen.Peek() && side.Peek() == 0 && offset.Value == 0f) return;
            panning.Value = false;
            isOpen.Value = false;
            armed.Value = false;
            side.Value = 0;
            offset.Value = 0f;
            closePending.Value = false;
            execPending.Value = false;
            if (contentRef.Value.IsNull) return;
            var keys = panKeys.Value;
            keys[0] = new Keyframe(0f, 0f, Easing.Linear);
            keys[1] = new Keyframe(1f, 0f, Easing.Linear);
            Context.Anim?.Keyframes(contentRef.Value, AnimChannel.TranslateX, keys, 1f);
        }

        // SwipeGroup ownership depends on stable delegate identity. Replacing these after every state re-render made
        // a re-grab look like a different row and could dismiss itself before following the finger.
        closeAction.Value ??= Close;
        groupCloseAction.Value ??= SnapClosed;
        outsidePress.Value ??= point =>
        {
            if (!isOpen.Peek() && side.Peek() == 0) return;
            var scene = Context.Scene;
            if (scene is not null && !rootRef.Value.IsNull && scene.IsLive(rootRef.Value))
            {
                RectF r = scene.AbsoluteRect(rootRef.Value);
                if (point.X >= r.X && point.X < r.X + r.W && point.Y >= r.Y && point.Y < r.Y + r.H) return;
            }
            closeAction.Value?.Invoke();
        };
        windowBlur.Value ??= () => closeAction.Value?.Invoke();
        scrollStarted.Value ??= () => closeAction.Value?.Invoke();
        Context.UseSignalEffect(() =>
        {
            hooks.PointerDownObserved += outsidePress.Value;
            hooks.WindowBlurObserved += windowBlur.Value;
            hooks.ScrollStartedObserved += scrollStarted.Value;
            Reactive.OnCleanup(() =>
            {
                hooks.PointerDownObserved -= outsidePress.Value;
                hooks.WindowBlurObserved -= windowBlur.Value;
                hooks.ScrollStartedObserved -= scrollStarted.Value;
            });
        });

        // Reflect this row's open state into the group so a list's scroll observer can gate its per-frame OffsetY
        // dispatch on SwipeGroup.AnyOpen — nothing needs closing (or even observing) while every row is shut. Reads
        // isOpen reactively (re-runs on each open/close toggle, an interaction-time event, never a per-frame one); the
        // token is this row's stable group-close delegate identity so the group tracks the single open member.
        Context.UseSignalEffect(() =>
        {
            bool o = isOpen.Value;
            if (p?.Group is { } g && groupCloseAction.Value is { } token)
            {
                if (o) g.MarkOpen(token); else g.MarkClosed(token);
                Reactive.OnCleanup(() => g.MarkClosed(token));
            }
        });

        void InvokeItem(SwipeAction a)
        {
            if (!(a.IsEnabledSource?.Invoke() ?? true)) return;
            a.OnInvoked?.Invoke();
            if (a.BehaviorOnInvoked != SwipeBehaviorOnInvoked.RemainOpen) Close();
        }

        // ── The pan gesture (root-anchored: the root never moves, so its local coords are gesture-stable) ─────────
        void PanDown(Point2 lp)
        {
            panStart.Value = lp.X;
            panBase.Value = LiveOffset();
            panning.Value = false;
            // Fresh anchor ⇒ assume touch (the walk-found row-swipe path fires PanDown with no OnPointerPressed). A
            // DIRECT press on the root re-fires OnPointerPressed right after this and overwrites the real kind,
            // so a TouchOnly mouse press is still correctly gated in PanMove.
            lastKind.Value = PointerKind.Touch;
        }
        void PanMove(Point2 lp)
        {
            if (p is null || contentRef.Value.IsNull) return;
            if (p.TouchOnly && lastKind.Value != PointerKind.Touch) return;   // belt: a mouse/touchpad never pans a touch-only swipe
            float delta = lp.X - panStart.Value;
            if (!panning.Value && MathF.Abs(delta) < PanSlopPx) return;
            bool firstClaim = !panning.Value;
            panning.Value = true;
            // Single-open: at THIS row's pan claim, close the previously open sibling in the group, then register
            // ourselves as the group's open one.
            if (firstClaim && p.Group is { } g)
            {
                if (!ReferenceEquals(g.CloseLast, groupCloseAction.Value)) g.CloseLast?.Invoke();
                g.CloseLast = groupCloseAction.Value;
            }
            // Re-grab during a close settle: the pin below overwrites the spring (Keyframes replaces the track), and
            // the pending unmount ticker must not clear the side out from under the live drag.
            if (firstClaim && closePending.Peek()) closePending.Value = false;
            var lead = p.Leading;
            var trail = p.Trailing;
            bool hasLeft = lead is not null;
            bool hasRight = trail is not null;
            float controlW = ControlWidth();
            float maxLeft = hasLeft ? StackWidth(lead!) : 0f;
            float maxRight = hasRight ? StackWidth(trail!) : 0f;
            // 1:1 tracking, no dead zone; the offset may cross zero mid-gesture and continue into the other side in
            // one motion.
            float off = DragOffset(panBase.Value + delta, maxLeft, maxRight,
                                   hasLeft && lead!.FullSwipe, hasRight && trail!.FullSwipe, controlW);
            int s = off > 0.5f ? 1 : off < -0.5f ? -1 : side.Peek();
            if (s != side.Peek() && (s != 1 || hasLeft) && (s != -1 || hasRight)) side.Value = s;
            var keys = panKeys.Value;
            keys[0] = new Keyframe(0f, off, Easing.Linear);
            keys[1] = new Keyframe(1f, off, Easing.Linear);
            Context.Anim?.Keyframes(contentRef.Value, AnimChannel.TranslateX, keys, 1f);   // follow the pointer (0-alloc)
            offset.Value = off;
            PinRevealClip(s, MathF.Abs(off), controlW);
            if (s != 0)
            {
                var cfg = s > 0 ? lead : trail;
                if (cfg is not null) UpdateButtons(s, off, cfg, s > 0 ? maxLeft : maxRight, controlW);
            }
        }
        void ReleaseOrTap()   // the OnDrag gesture's release/commit edge; a plain tap lands here with panning=false
        {
            if (!panning.Value)
            {
                // Tap on the content of an open swipe dismisses it.
                if (isOpen.Peek()) Close();
                return;
            }
            panning.Value = false;
            int s = side.Peek();
            if (s == 0) return;
            var cfg = s > 0 ? p.Leading : p.Trailing;
            if (cfg is null) { Close(); return; }
            float clusterW = StackWidth(cfg);
            float controlW = ControlWidth();
            // REAL release velocity from the arena VelocitySampler (px/s along the swipe axis), projected to the
            // bounded-coast resting position: a fast flick rests open (or commits) even short of the positional
            // threshold, while velocity toward home subtracts so a late reversal cancels.
            float vAxis = hooks.PointerVelocity?.Invoke().X ?? 0f;   // horizontal swipe ⇒ X is the swipe axis
            float dist = MathF.Abs(offset.Value);
            float outward = s > 0 ? vAxis : -vAxis;                  // speed in the reveal direction
            float projected = ProjectedDistance(dist, outward);
            if (cfg.FullSwipe && ShouldCommit(projected, ArmThreshold(controlW, clusterW)))
            {
                // COMMIT: fling the content fully off-screen with the lift velocity injected, then invoke the primary
                // once the fling visually lands. Nothing commits during the drag; only this release path commits.
                isOpen.Value = true;
                armed.Value = true;
                SpringContentTo(s * controlW, in SettleSpring, vAxis);
                UpdateButtons(s, s * controlW, cfg, clusterW, controlW);   // hold the expanded morph through the fling
                execDeadline.Value = Environment.TickCount64 + CommitInvokeDelayMs;
                execPending.Value = true;
                return;
            }
            if (!ShouldRestOpen(isOpen.Peek(), dist, clusterW, outward)) { CloseCore(vAxis, in SettleSpring); return; }
            isOpen.Value = true;
            SpringContentTo(s * clusterW, in SettleSpring, vAxis);
            UpdateButtons(s, s * clusterW, cfg, clusterW, controlW);   // land fully revealed (also disarms the morph)
        }

        void CommitExecute()
        {
            if (p is null || !isOpen.Peek()) return;
            int s = side.Peek();
            var cfg = s > 0 ? p.Leading : s < 0 ? p.Trailing : null;
            if (cfg is not { FullSwipe: true }) return;
            var item = cfg.Actions[0];
            bool enabled = item.IsEnabledSource?.Invoke() ?? true;
            if (enabled) item.OnInvoked?.Invoke();
            if (!enabled || item.BehaviorOnInvoked != SwipeBehaviorOnInvoked.RemainOpen)
            {
                Close();
                return;
            }
            // RemainOpen: settle back from the off-screen fling to the open rest.
            armed.Value = false;
            float clusterW = StackWidth(cfg);
            SpringContentTo(s * clusterW, in SettleSpring, 0f);
            UpdateButtons(s, s * clusterW, cfg, clusterW, ControlWidth());
        }

        // ── Esc dismisses while open, via the dispatcher's pre-focus key preview so it works without focus in the
        //    control. Chained + restored by reference so an overlay's own preview survives us.
        bool open = isOpen.Value;
        escPreview.Value ??= k =>
        {
            if ((k == Keys.Escape || k == Keys.GamepadB) && isOpen.Peek())
            {
                closeAction.Value?.Invoke();
                return true;
            }
            return savedPreview.Value?.Invoke(k) ?? false;
        };
        UseEffect(() =>
        {
            if (open && !escInstalled.Value)
            {
                escInstalled.Value = true;
                savedPreview.Value = hooks.KeyPreview;
                hooks.KeyPreview = escPreview.Value;
            }
            else if (!open && escInstalled.Value)
            {
                escInstalled.Value = false;
                if (ReferenceEquals(hooks.KeyPreview, escPreview.Value)) hooks.KeyPreview = savedPreview.Value;
                savedPreview.Value = null;
            }
        }, open);
        // UNMOUNT cleanup for the Esc preview: the `open` effect above only UNINSTALLS on a re-render with open=false —
        // a row torn down WHILE open (recycled off-screen, removed mid-swipe) never re-renders, so its hijacked
        // KeyPreview would leak. Restore it on dispose iff we still own the chain (an overlay that chained after us wins).
        Context.UseSignalEffect(() => Reactive.OnCleanup(() =>
        {
            if (escInstalled.Value && ReferenceEquals(hooks.KeyPreview, escPreview.Value))
                hooks.KeyPreview = savedPreview.Value;
        }));

        // RECYCLE reset (bound rows): when the slot's index signal changes the slot rebinds to a NEW row, which must
        // present CLOSED the same frame — snap TranslateX to 0 via a const keyframe (cancels any in-flight settle
        // spring, no animation) and clear the pan/open state. Button/morph springs die with the side unmount (the
        // engine reclaims a dead node's rows). No-op on first mount and on an already-closed steady key.
        int resetKey = p?.ResetKey?.Value ?? 0;   // subscribe → the effect re-runs on a recycle bump
        UseEffect(() =>
        {
            if (contentRef.Value.IsNull) return;
            if (!isOpen.Peek() && side.Peek() == 0 && offset.Value == 0f) return;   // already closed → nothing to snap
            panning.Value = false;
            isOpen.Value = false;
            armed.Value = false;
            side.Value = 0;
            offset.Value = 0f;
            closePending.Value = false;
            execPending.Value = false;
            var keys = panKeys.Value;
            keys[0] = new Keyframe(0f, 0f, Easing.Linear);
            keys[1] = new Keyframe(1f, 0f, Easing.Linear);
            Context.Anim?.Keyframes(contentRef.Value, AnimChannel.TranslateX, keys, 1f);   // const write ⇒ cancels + pins to 0 (0-alloc)
        }, resetKey);

        // ── One action button: a compact capsule (icon-only, radius = height/2) with the label BELOW it when the
        //    row is tall enough — the iOS 26 Mail look. The whole column is the tap target. ─────────────────────
        bool armedRender = armed.Value;
        float controlH = ControlHeight();
        float capsuleH = Math.Clamp(controlH - CapsuleHeightPad, CapsuleMinHeight, CapsuleMaxHeight);
        bool showLabel = controlH >= LabelMinControlH;
        Element ItemCell(SwipeAction a, int edgeIndex, bool fullSwipe, NodeHandle[] colArr, NodeHandle[] capArr)
        {
            IconRef icon = a.IconSource?.Invoke() ?? a.Icon;
            string label = a.LabelSource?.Invoke() ?? a.Label;
            bool enabled = a.IsEnabledSource?.Invoke() ?? true;
            // Colors: a custom action Color wins (on-accent content); otherwise the PRIMARY gets the accent fill and
            // the secondaries the neutral tertiary plate.
            ColorF fill, fg;
            if (a.Color is { } custom) { fill = custom; fg = a.Foreground ?? Tok.TextOnAccentPrimary; }
            else if (edgeIndex == 0) { fill = Tok.AccentDefault; fg = Tok.TextOnAccentPrimary; }
            else { fill = Tok.FillControlTertiary; fg = Tok.TextPrimary; }
            bool neutralFill = a.Color is null && edgeIndex != 0;
            bool primary = fullSwipe && edgeIndex == 0;
            int i = edgeIndex;
            var capsule = new BoxEl
            {
                MinWidth = MathF.Max(CapsuleMinWidthPx, capsuleH * CapsuleWidthRatio),
                Height = capsuleH,
                Corners = CornerRadius4.All(capsuleH * 0.5f),
                Fill = fill,
                PressedFill = neutralFill ? Tok.FillControlAltQuaternary : PressDip(fill),
                AlignItems = FlexAlign.Center,
                Justify = FlexJustify.Center,
                Opacity = enabled ? 1f : 0.45f,   // static disabled dim on the capsule — the column's opacity is spring-driven
                Animate = CapsuleBirth,
                OnRealized = nh => capArr[i] = nh,
                Children = [SwipeItemIcon(icon, fg, onAccent: !neutralFill, primary, armedRender,
                                          primary ? nh => primaryIconRef.Value = nh : null)],
            };
            return new BoxEl
            {
                Direction = 1,
                Gap = 3f,
                AlignItems = FlexAlign.Center,
                Justify = FlexJustify.Center,
                Role = AutomationRole.Button,
                IsEnabled = enabled,
                Animate = ColumnBirth,
                OnClick = () => InvokeItem(a),   // tap invokes
                OnRealized = nh => colArr[i] = nh,
                Children = showLabel
                    ?
                    [
                        capsule,
                        new TextEl(label)
                        {
                            Size = 10f, Weight = 600,
                            // Text has no opacity prop — state dims route through the color's alpha: the expanded
                            // primary shows its icon only (measured iOS 26 Mail: parked capsules carry labels, the
                            // full-swipe capsule drops its label; it returns on disarm via the armed re-render), and
                            // a disabled label dims the same way.
                            Color = primary && armedRender ? Tok.TextSecondary with { A = 0f }
                                  : enabled ? Tok.TextSecondary
                                  : Tok.TextSecondary with { A = Tok.TextSecondary.A * 0.45f },
                            MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        },
                    ]
                    : [capsule],
            };
        }

        // ── Assembly: ZStack — the reveal layer (mounted only while a side is engaged) under the panning content ──
        int sideNow = side.Value;
        bool closeTicking = closePending.Value;
        bool execTicking = execPending.Value;

        var children = new List<Element>(4);
        var sideCfg = sideNow > 0 ? p.Leading : sideNow < 0 ? p.Trailing : null;
        if (sideNow != 0 && sideCfg is not null)
        {
            var acts = sideCfg.Actions;
            int n = acts.Count;
            // The per-button handle arrays live across re-renders of the same side mount (a diffed re-render does not
            // re-fire OnRealized); a side FLIP remounts the keyed cluster, so fresh arrays get refilled then.
            if (builtSide.Value != sideNow || colRefs.Value.Length != n)
            {
                builtSide.Value = sideNow;
                colRefs.Value = new NodeHandle[n];
                capRefs.Value = new NodeHandle[n];
                primaryIconRef.Value = default;
            }
            var colArr = colRefs.Value;
            var capArr = capRefs.Value;
            var cells = new Element[n];
            for (int k = 0; k < n; k++)
            {
                // actions[0] is OUTERMOST on both sides: the left cluster lays [a0 a1 …] left→right, the right
                // cluster lays [a_{n-1} … a1 a0] (a0 nearest the right edge). This also puts the right-side primary
                // LAST in draw order, so its full-swipe expansion paints over the parked, dimmed secondaries.
                int i = sideNow > 0 ? k : n - 1 - k;
                cells[k] = ItemCell(acts[i], i, sideCfg.FullSwipe, colArr, capArr) with { Key = "sw-cap:" + i };
            }
            children.Add(new BoxEl
            {
                // Keyed PER SIDE: a side flip must REMOUNT the cluster (fresh handles + the birth animation replays),
                // and the keyed reconciler must never positionally re-pair this layer with the content node (which
                // carries the live pan translation).
                Key = sideNow > 0 ? "swipe-reveal:L" : "swipe-reveal:R",
                ClipToBounds = true,
                OnRealized = nh =>
                {
                    revealRef.Value = nh;
                    PinRevealClip(sideNow, MathF.Abs(offset.Value), ControlWidth());
                },
                Direction = 0,
                AlignItems = FlexAlign.Stretch,
                Justify = sideNow > 0 ? FlexJustify.Start : FlexJustify.End,
                Children =
                [
                    // The capsule cluster: parked at the revealed edge, vertically centered, 10px edge inset, 8px
                    // gaps. Its measured width (incl. insets) is the open resting extent.
                    new BoxEl
                    {
                        Direction = 0,
                        Gap = CapsuleGapPx,
                        AlignItems = FlexAlign.Center,
                        Padding = new Edges4(EdgeInsetPx, 0, EdgeInsetPx, 0),
                        OnRealized = nh => stackRef.Value = nh,
                        Children = cells,
                    },
                ],
            });
        }
        // The content layer: pans on its TranslateX; carries no handlers itself so presses fall through to the root.
        // While a side is engaged it presents as an ELEVATED CARD (the iOS lift): card fill + rounded corners, with
        // a 120ms brush cross-fade in/out; closed-and-settled reverts (and the reveal layer unmounts below).
        bool engaged = sideNow != 0;
        CornerRadius4 liftCorners = p.Corners == default ? CornerRadius4.All(10f) : p.Corners;
        children.Add(new BoxEl
        {
            Key = "swipe-content",
            ZStack = true,   // an unsized app content child fills the cell
            // Preserve the caller's real surface. Wavee rows are transparent/zebra overlays on Mica; the reveal host
            // is clipped to the exposed gap, so no opaque replacement card is needed to mask the action plate.
            Fill = p.ContentFill ?? ColorF.Transparent,
            Corners = engaged ? liftCorners : p.Corners,
            ClipToBounds = true,
            OnRealized = nh => contentRef.Value = nh,
            Children = [p.Content],
        });
        // Open-row tap shield (row-swipe / TouchOnly consumers only — the gallery's capsules must stay tappable):
        // OnClick fires on the HIT node with no bubbling, so an interactive row would swallow the root's
        // tap-to-dismiss. A transparent topmost layer intercepts the dismiss while open; it carries no drag handler,
        // so a SECOND swipe still re-pans through the engine's ancestor walk.
        if (open && p.TouchOnly)
            children.Add(new BoxEl
            {
                Key = "swipe-tap-shield",
                ZStack = true,
                Role = AutomationRole.Button,
                OnClick = () => closeAction.Value?.Invoke(),
            });
        if (closeTicking)
            children.Add(Embed.Comp(() => new DebounceTicker
            {
                DeadlineMs = closeDeadline,
                Pending = closePending,
                // Guarded: a re-grab or re-open during the settle window must keep its reveal layer mounted.
                Fire = () => { if (!panning.Value && !isOpen.Peek()) { side.Value = 0; armed.Value = false; } },
            }) with { Key = "swipe-close-settle" });
        if (execTicking)
            children.Add(Embed.Comp(() => new DebounceTicker
            {
                DeadlineMs = execDeadline,
                Pending = execPending,
                Fire = CommitExecute,   // full-swipe invokes at visual idle; Auto/Close then settles home
            }) with { Key = "swipe-exec-close" });

        // The root is bare — no fill or corner rounding of its own; the clip bounds the panning content and the
        // reveal underneath.
        return new BoxEl
        {
            ZStack = true,
            Margin = p.Margin,
            ClipToBounds = true,
            Corners = p.Corners,
            OnRealized = nh => rootRef.Value = nh,
            OnPointerDown = PanDown,
            // Record the DIRECT-press pointer kind for the TouchOnly belt (fires only on a press that lands on the root
            // itself — the gallery superset; a walk-found row-swipe presses the inner row, keeping PanDown's Touch default).
            OnPointerPressed = args => lastKind.Value = args.Kind,
            OnDrag = PanMove,
            // Cross-axis swipe (§7A): inside a vertical list this drag competes axis-locked with the list's Pan — a
            // horizontal swipe along the row wins (reveals the actions), a vertical drag yields (the list scrolls). The
            // root box is a row (Direction default 0) ⇒ the dispatcher locks the swipe to the X axis.
            DragYieldsToPan = true,
            OnClick = ReleaseOrTap,
            // Got/LostFocus is routed through ancestors. Moving focus within the swipe keeps it open; leaving the
            // subtree closes it, so a keyboard/touch interaction elsewhere never strands a translated row.
            OnFocusChanged = got => { if (!got) Close(); },
            // Esc also routes here when focus is inside the control (a revealed capsule is focusable).
            OnKeyDown = a =>
            {
                if (a.Handled || a.KeyCode != Keys.Escape || !isOpen.Peek()) return;
                a.Handled = true;
                Close();
            },
            Children = children.ToArray(),
        };
    }
}
