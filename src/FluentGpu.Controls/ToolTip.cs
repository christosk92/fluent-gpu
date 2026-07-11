using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;

namespace FluentGpu.Controls;

/// <summary>How the tooltip bubble is positioned — WinUI <c>PlacementMode</c> (the two modes ToolTipService actually
/// drives): <see cref="Top"/> = centered above the TARGET, flipping on a collision (the WinUI default,
/// ToolTip_Partial.cpp:1119-1122 "Fall back to the default - PlacementMode_Top"), the target rect inflated by the
/// input-mode offset — 20px mouse / 12px keyboard (<see cref="ToolTip.MousePlacementOffset"/>); <see cref="Mouse"/> =
/// at the last POINTER position, offset 11px below the cursor (ToolTip_Partial.h:56
/// <c>m_mousePlacementVerticalOffset = 11</c>; ToolTip_Partial.cpp:976-984 "align ToolTip with the bottom left corner
/// of mouse bounding rectangle").</summary>
public enum ToolTipPlacementMode : byte { Top, Mouse }

/// <summary>
/// A WinUI <c>ToolTip</c> (framework <c>ToolTip</c> + <c>ToolTipService</c>): wraps a target element and surfaces a small
/// text bubble anchored above it (or at the pointer in <see cref="ToolTipPlacementMode.Mouse"/>). 1:1 with the
/// framework's automatic-tooltip behavior:
/// <list type="bullet">
/// <item>Hover trigger — pointer-over the target opens the bubble after the initial show delay
///   (<c>SPI_GETMOUSEHOVERTIME</c> 400ms × 2 = 800ms; a re-show within <see cref="BetweenShowDelayMs"/> = 200ms uses the
///   reshow delay — see <see cref="MouseReshowDelayMs"/> for why that is 400ms, not the spec'd 600ms). Pointer-leave
///   cancels a PENDING open immediately (ToolTipService_Partial.cpp:1435-1442); an OPEN bubble is kept and closed by
///   the safe-zone monitor instead — see the safe-zone item below.</item>
/// <item>Safe zone — an open bubble does NOT close the instant the pointer leaves the target: WinUI keeps it while the
///   pointer is inside owner ∪ tooltip (∪ their convex hull) and a 1s check timer closes it once outside
///   (ToolTipService_Partial.cpp:1433-1453 owner-exit only records the owner; cpp:349-381 OnSafeZoneCheck;
///   cpp:1060-1098 IsToolTipInSafeZone; .h:22 <c>s_safeZoneCheckTimerDuration</c> = 1s). Implemented against the REAL
///   pointer (<c>InputHooks.GetPointerPosition</c>): owner-exit arms the 1s <see cref="SafeZoneCheckMs"/> poll, and each
///   elapse geometry-tests the live pointer against owner ∪ bubble — inside keeps polling, one full interval outside
///   closes. The bubble itself is hit-test-INVISIBLE (a real tooltip never intercepts pointer or wheel input — the
///   hover-handler approximation this replaces made the bubble swallow wheel scrolling under it), so bubble-hover is
///   detected by geometry, never by events. The 5s dwell stays authoritative while parked on the bubble.</item>
/// <item>Keyboard trigger — KEYBOARD focus landing inside the target opens the bubble after 800ms (×2, normal AND
///   reshow: ToolTipService_Partial.cpp:1777-1779), exactly WinUI's OnOwnerGotFocus gate
///   (ToolTipService_Partial.cpp:1648-1664: only <c>FocusState::Keyboard</c> shows the tooltip — pointer focus does
///   not). Focus leaving closes it (cpp:1696-1706 OnOwnerLostFocus → OnOwnerLeaveInternal).</item>
/// <item>Press dismiss — a pointer press over the target closes an open (or pending) bubble, and it stays closed
///   until the pointer LEAVES and re-enters (classic tooltip behavior; WinUI 3's ToolTipService itself registers no
///   PointerPressed handler — it closes via safe-zone exit monitoring, ToolTipService_Partial.cpp:1437-1453 — but
///   cannot re-open a dismissed owner until a real leave + re-enter either, cpp:725-737 CancelAutomaticToolTip).</item>
/// <item>Auto-dismiss — an open bubble closes itself after <see cref="ShowDurationMs"/> (<c>SPI_GETMESSAGEDURATION</c>
///   default 5s), and the dismissal LATCHES like press dismiss: WinUI's only show trigger is PointerEntered
///   (ToolTipService_Partial.cpp:1395-1418), so in-place hover moves can never re-open a timed-out tooltip — it takes
///   a real leave + re-enter.</item>
/// <item>No click trigger — the wrapper adds NO OnClick (ToolTipService registers none,
///   ToolTipService_Partial.cpp:176-220), so a tooltipped element is never a tab stop by itself.</item>
/// </list>
/// Chrome matches <c>ToolTip_themeresources.xaml</c>: AcrylicInAppFillColorDefault fill, 1px SurfaceStrokeColorFlyout
/// stroke, ControlCornerRadius (4px) corners, the light tooltip elevation (<see cref="Elevation.Tooltip"/>), 9,6,9,8
/// padding, 12px TextFillColorPrimary text, MaxWidth 320, TextWrapping=Wrap. Open/close are the WinUI
/// FadeIn/FadeOutThemeAnimation fades (167ms — <see cref="PopupChrome.Raw"/> in the overlay host). The bubble does NOT
/// trap focus and does NOT light-dismiss on outside click (<see cref="DismissBehavior.None"/>) — dismissal is owned by
/// hover-leave/focus-leave/press + the auto-dismiss timer, exactly like <c>ToolTipService</c>.
/// </summary>
public sealed class ToolTip : Component
{
    // Template parts (see TemplateParts). The part's doc lists the props the control OWNS (re-asserted after any
    // modifier — a Parts customization cannot win those). The hover/focus/press trigger wrapper around Target is NOT
    // a part — its handlers ARE the ToolTipService mechanics.
    /// <summary>The text bubble surface (the control-built chrome inside the raw overlay host — acrylic fill, flyout
    /// stroke, 4px corners, tooltip elevation, 9,6,9,8 padding). Owned: Children (the <see cref="Text"/> content),
    /// hit-test invisibility (a tooltip never intercepts input), and the realized-node capture (safe-zone geometry).</summary>
    public const string PartBubble = "Bubble";

    public Element Target = new BoxEl();
    public string Text = "";
    public bool OpenOnMount;   // deterministic visual-shot hook: open the real tooltip after first mount
    /// <summary>Lightweight per-part styling (CSS ::part): modifiers keyed by the <c>PartXxx</c> consts; see
    /// <see cref="TemplateParts"/> for the contract.</summary>
    public TemplateParts? Parts;

    /// <summary>WinUI <c>ToolTip.Placement</c> (Top/Mouse subset). Default <see cref="ToolTipPlacementMode.Top"/> —
    /// the WinUI default (ToolTip_Partial.cpp:1119-1122).</summary>
    public ToolTipPlacementMode Placement = ToolTipPlacementMode.Top;

    // ── ToolTipService timing constants (ToolTipService_Partial.h / .cpp). ────────────────────────────────────────────
    /// <summary><c>SPI_GETMOUSEHOVERTIME</c> default (DEFAULT_SPI_GETMOUSEHOVERTIME, ToolTipService_Partial.h:18) = 400ms.</summary>
    public const float MouseHoverTimeMs = 400f;
    /// <summary>Mouse initial show delay: hover time × 2 = 800ms (GetInitialShowDelay, Mouse, first show —
    /// ToolTipService_Partial.cpp:1774-1775).</summary>
    public const float MouseShowDelayMs = MouseHoverTimeMs * 2f;
    /// <summary>Mouse RE-show delay = 400ms (× 1), NOT the spec-comment's × 1.5 = 600ms: the shipping code is
    /// <c>ticks *= static_cast&lt;INT64&gt;(isReshow ? 1.5 : 2)</c> (ToolTipService_Partial.cpp:1775) and the C++
    /// <c>static_cast&lt;INT64&gt;(1.5)</c> TRUNCATES to 1 — behavior parity follows the compiled code, not the
    /// comment table at cpp:1737-1742.</summary>
    public const float MouseReshowDelayMs = MouseHoverTimeMs * 1f;
    /// <summary>Keyboard show delay: hover time × 2 = 800ms for BOTH normal and reshow
    /// (ToolTipService_Partial.cpp:1777-1779 — Keyboard ignores isReshow).</summary>
    public const float KeyboardShowDelayMs = MouseHoverTimeMs * 2f;
    /// <summary>BETWEEN_SHOW_DELAY_MS = 200ms (ToolTipService_Partial.h:17) — a re-open inside this window of the last
    /// close uses the reshow delay (cpp:659 <c>GetTickCount() - s_lastToolTipOpenedTime &lt; BETWEEN_SHOW_DELAY_MS</c>).</summary>
    public const float BetweenShowDelayMs = 200f;
    /// <summary>DEFAULT_SHOW_DURATION_SECONDS = 5s — auto-dismiss after this long open.</summary>
    public const float ShowDurationMs = 5000f;
    /// <summary>Pointer-mode vertical offset below the cursor — ToolTip_Partial.h:56
    /// <c>m_mousePlacementVerticalOffset = 11</c> (the brief's "14px" did not survive source verification).</summary>
    public const float MousePlacementVerticalOffset = 11f;
    /// <summary><c>DEFAULT_MOUSE_OFFSET</c> = 20 (ToolTip_Partial.h:11): target-mode placement inflates the dock rect
    /// by this on BOTH axes for a mouse-opened automatic tooltip before positioning (ToolTip_Partial.cpp:1224-1258
    /// <c>InflateRect(&amp;rcDockTo, horizontalOffset, verticalOffset)</c> + :1275).</summary>
    public const float MousePlacementOffset = 20f;
    /// <summary><c>DEFAULT_KEYBOARD_OFFSET</c> = 12 (ToolTip_Partial.h:10) — the dock-rect inflation when the tooltip
    /// was opened by keyboard focus.</summary>
    public const float KeyboardPlacementOffset = 12f;
    /// <summary><c>s_safeZoneCheckTimerDuration</c> = 1s (ToolTipService_Partial.h:22) — the safe-zone poll cadence;
    /// the bubble closes once the pointer has been outside owner ∪ bubble for one full check interval.</summary>
    public const float SafeZoneCheckMs = 1000f;

    /// <summary>LIVE target+text slots (the SelectorBar/RadioButtons provider idiom). <see cref="Target"/> and
    /// <see cref="Text"/> are plain fields, so via <c>Embed.Comp</c> they freeze at first mount — a re-rendering
    /// parent's new wrapped element or new tooltip text would be silently dropped (the toggle-tooltip staleness bug).
    /// <see cref="Wrap"/> routes them through this provider instead; when present they WIN over the fields and the
    /// ToolTip re-renders reactively (context is signal-backed).</summary>
    public sealed record ToolTipSlots(Element Target, string Text);

    internal static readonly Context<ToolTipSlots?> SlotsChannel = new(null);

    public static Element Wrap(Element target, string text)
        => Ctx.Provide(SlotsChannel, new ToolTipSlots(target, text), Embed.Comp(() => new ToolTip()));

    public override Element Render()
    {
        var slots = UseContext(SlotsChannel);
        Element target = slots?.Target ?? Target;
        string text = slots?.Text ?? Text;
        var svc = UseContext(Overlay.Service);
        var hooks = UseContext(InputHooks.Current);
        var anchor = UseRef<NodeHandle>(default);
        var h = UseRef<OverlayHandle?>(null);
        var autoOpened = UseRef(false);
        var lastPointerLocal = UseRef<Point2>(default);   // last hover position (wrapper-local) → Mouse placement
        var dismissedUntilLeave = UseRef(false);          // press- or timeout-dismissed: no re-open until leave + re-enter
        var bubbleNode = UseRef<NodeHandle>(default);     // the open bubble's realized node → its rect joins the safe zone
        var openedAtMs = UseRef<long>(0);                 // monotonic ms at open → the 5s dwell survives 2↔3 phase flips
        var safePoll = UseSignal(0);                      // bumped per in-zone safe-zone elapse → remounts the 1s poll clock

        // Timer phase: 0 = idle, 1 = show-delay counting down (open after it), 2 = bubble open (auto-dismiss counting
        // down), 3 = bubble open + pointer outside owner ∪ bubble (the 1s safe-zone grace counting down).
        var phase = UseSignal(0);
        // 3-state input mode of the pending/open tooltip (WinUI AutomaticToolTipInputMode): 0 = mouse, 1 = keyboard.
        var keyboardMode = UseRef(false);
        // GetTickCount()-style monotonic ms at the last close → re-show detection (BETWEEN_SHOW_DELAY_MS window).
        // WinUI names this s_lastToolTipOpenedTime, but assigns it in CloseAutomaticToolTip at the close start.
        var lastClosedAtMs = UseRef<long>(long.MinValue / 2);
        var placementMode = Placement;

        Func<Element> bubbleContent = () => BubbleContent(text, x => bubbleNode.Value = x);

        void OpenNow()
        {
            phase.Value = 2;   // bubble open → arm the auto-dismiss countdown
            if (h.Value is { IsOpen: true }) return;
            openedAtMs.Value = Environment.TickCount64;   // dwell epoch (m_tpCloseTimer is armed once per open)
            // A tooltip never traps focus and never light-dismisses on outside click — it is transient and dismissal
            // is driven by hover/focus-leave + press + the auto-dismiss timer (ToolTipService owns close, not the user).
            var options = new PopupOptions(FocusTrap: false, DismissBehavior: DismissBehavior.None, Chrome: PopupChrome.Raw);
            if (placementMode == ToolTipPlacementMode.Mouse && !keyboardMode.Value)
            {
                // PlacementMode.Mouse: top-left at the pointer, 11px below it (ToolTip_Partial.cpp:976-977
                // "align ToolTip with the bottom left corner of mouse bounding rectangle"; .h:56 offset = 11). The
                // positioner adds FlyoutMargin (4) below a Bottom-placed anchor, so the synthetic point-rect carries
                // the remaining 7; collisions flip it above the pointer.
                var local = lastPointerLocal.Value;
                var node = anchor.Value;
                h.Value = svc.OpenAt(
                    () =>
                    {
                        var scene = Context.Scene;
                        RectF abs = scene is not null && !node.IsNull && scene.IsLive(node) ? scene.AbsoluteRect(node) : default;
                        return new RectF(abs.X + local.X, abs.Y + local.Y + (MousePlacementVerticalOffset - FlyoutPositioner.FlyoutMargin), 0f, 0f);
                    },
                    bubbleContent,
                    FlyoutPlacement.BottomEdgeAlignedLeft,
                    options,
                    owner: () => anchor.Value);
            }
            else
            {
                // WinUI default PlacementMode.Top: CENTERED above the target (ToolTip_Partial.cpp:1119-1122 default),
                // against the target rect INFLATED by the input-mode offset — DEFAULT_MOUSE_OFFSET 20 /
                // DEFAULT_KEYBOARD_OFFSET 12 on both axes (ToolTip_Partial.h:10-11; cpp:1224-1258
                // InflateRect(&rcDockTo, horizontalOffset, verticalOffset) feeds QueryRelativePosition at :1275).
                // The positioner adds FlyoutMargin (4) in the major direction, so the synthetic rect carries the rest;
                // FlyoutPositioner flips below on a collision.
                bool keyboard = keyboardMode.Value;
                var node = anchor.Value;
                h.Value = svc.OpenAt(
                    () =>
                    {
                        var scene = Context.Scene;
                        RectF abs = scene is not null && !node.IsNull && scene.IsLive(node) ? scene.AbsoluteRect(node) : default;
                        float inflate = (keyboard ? KeyboardPlacementOffset : MousePlacementOffset) - FlyoutPositioner.FlyoutMargin;
                        return new RectF(abs.X - inflate, abs.Y - inflate, abs.W + inflate * 2f, abs.H + inflate * 2f);
                    },
                    bubbleContent,
                    FlyoutPlacement.Top,
                    options,
                    owner: () => anchor.Value);
            }
        }

        void CloseNow()
        {
            bool wasOpen = phase.Peek() is 2 or 3;
            if (h.Value is { IsOpen: true } o) o.Close();
            h.Value = null;
            bubbleNode.Value = default;   // the bubble unmounts — its rect leaves the safe zone
            if (wasOpen) lastClosedAtMs.Value = Environment.TickCount64;   // mark close start for the re-show window
            keyboardMode.Value = false;
            phase.Value = 0;
        }

        // The SPI_GETMESSAGEDURATION dwell elapsed: close AND latch until leave + re-enter — WinUI's only show trigger
        // is PointerEntered (ToolTipService_Partial.cpp:1395-1418 OnOwnerPointerEntered), so in-place hover moves can
        // never re-open a timed-out tooltip (the owner re-enters the show path via a real exit + enter only).
        void AutoDismiss() { dismissedUntilLeave.Value = true; CloseNow(); }

        // Pointer-enter (OnHoverMove fires on any move while hovering): begin the initial-show-delay countdown if idle.
        // ToolTipService.OnOwnerEnterInternal — Mouse mode, reshow if the previous tooltip closed < 200ms ago.
        void OnEnter(Point2 local)
        {
            lastPointerLocal.Value = local;   // tracked even while open — WinUI re-reads the current point at placement
            if (phase.Peek() == 3) { phase.Value = 2; return; }   // back inside the safe zone → cancel the 1s grace
            if (dismissedUntilLeave.Value) return;   // dismissed: moves while still hovering must NOT re-arm (the WinUI
                                                     // owner stays out of m_nestedOwners until a real leave + re-enter)
            if (phase.Peek() != 0 || (h.Value is { IsOpen: true })) return;
            keyboardMode.Value = false;
            phase.Value = 1;   // show-delay counting down
        }

        // Pointer-leave: cancel a PENDING open (ToolTipService_Partial.cpp:1435-1442 — "Cancel the ToolTip if it had
        // not been opened yet"), but KEEP an open bubble: WinUI's owner-exit only records the owner and lets the
        // safe-zone monitor close it once the pointer is outside owner ∪ tooltip (cpp:1443-1453; the 1s check timer,
        // cpp:349-381 + .h:22). A leave also lifts the press/timeout dismiss latch (the next enter may show again).
        void OnLeave()
        {
            dismissedUntilLeave.Value = false;
            if (phase.Peek() == 2) { phase.Value = 3; return; }   // arm the 1s safe-zone poll (the bubble rect keeps it open via geometry)
            if (phase.Peek() != 3) CloseNow();   // pending open → cancel
        }

        // The 1s safe-zone poll elapsed (phase 3): WinUI OnSafeZoneCheck / IsToolTipInSafeZone against the GLOBAL
        // pointer — the bubble is hit-test-invisible (a real tooltip never intercepts input), so bubble-hover is
        // detected by geometry, never by events. Owner re-entry is event-driven (OnEnter flips 3→2) but the owner
        // rect is tested too (WinUI tests owner ∪ tooltip). The 5s dwell stays authoritative while parked in-zone.
        void SafeZoneCheck()
        {
            if (Environment.TickCount64 - openedAtMs.Value >= (long)ShowDurationMs) { AutoDismiss(); return; }
            var scene = Context.Scene;
            if (scene is not null && hooks.GetPointerPosition?.Invoke() is { } pt)
            {
                var on = anchor.Value;
                var bn = bubbleNode.Value;
                bool inside = (!on.IsNull && scene.IsLive(on) && InRect(scene.AbsoluteRect(on), pt))
                           || (!bn.IsNull && scene.IsLive(bn) && InRect(scene.AbsoluteRect(bn), pt));
                if (inside) { safePoll.Value = safePoll.Peek() + 1; return; }   // stay open — remount the poll clock (keyed by safePoll)
            }
            CloseNow();   // outside owner ∪ bubble for one full interval (or no trustworthy pointer) → close
        }

        // Keyboard focus entering the target subtree (the dispatcher routes focus-changed to ancestors on subtree
        // boundary crossings): WinUI OnOwnerGotFocus (ToolTipService_Partial.cpp:1635-1668) — show ONLY for
        // FocusState::Keyboard (cpp:1652-1656: GetRealFocusStateForFocusedElement() == Keyboard; pointer-driven focus
        // never opens a tooltip). Keyboard delay = ×2 (800ms), reshow included (cpp:1777-1779). Focus leaving = leave.
        void OnFocus(bool got)
        {
            if (!got) { CloseNow(); return; }
            if (phase.Peek() != 0 || h.Value is { IsOpen: true }) return;
            var scene = Context.Scene;
            var focused = hooks.GetFocus?.Invoke() ?? NodeHandle.Null;
            bool keyboardFocus = scene is not null && !focused.IsNull && scene.IsLive(focused)
                                 && (scene.Flags(focused) & NodeFlags.FocusVisual) != 0;
            if (!keyboardFocus) return;
            keyboardMode.Value = true;
            phase.Value = 1;
        }

        // Pointer press over the target: dismiss an open bubble (and a pending one), latched until leave + re-enter.
        // Press-dismiss is classic Win32/WPF tooltip behavior we keep deliberately — WinUI 3's ToolTipService registers
        // no PointerPressed handler (ToolTipService_Partial.cpp:176-220; it closes via safe-zone exit, cpp:1437-1453)
        // but equally cannot re-open a dismissed owner until a real leave + re-enter (cpp:725-737). There is no
        // click-to-toggle — the wrapper adds NO OnClick, so it never becomes a tab stop or intercepts activation.
        void OnPressed(PointerEventArgs _)
        {
            if (phase.Peek() == 0 && h.Value is not { IsOpen: true }) return;
            dismissedUntilLeave.Value = true;
            CloseNow();
        }

        UseEffect(() =>
        {
            if (!OpenOnMount || autoOpened.Value) return;
            autoOpened.Value = true;
            OpenNow();
        }, OpenOnMount);

        int ph = phase.Value;   // subscribe → re-render when the timer phase changes (mount/unmount the clock)
        int poll = safePoll.Value;   // subscribe → an in-zone safe-zone elapse remounts a fresh 1s poll clock
        // GetInitialShowDelay: Mouse ×2 normal / ×1 reshow (truncated 1.5 — see MouseReshowDelayMs); Keyboard ×2 always.
        bool isReshow = Environment.TickCount64 - lastClosedAtMs.Value < (long)BetweenShowDelayMs;
        float delay = keyboardMode.Value ? KeyboardShowDelayMs : (isReshow ? MouseReshowDelayMs : MouseShowDelayMs);
        // The REMAINING show-duration dwell: WinUI's m_tpCloseTimer is armed once per open and keeps running while the
        // safe-zone monitor watches (ToolTipService_Partial.h:54; OpenAutomaticToolTip arms it, cpp:429-459), so the
        // 2↔3 phase flips must not restart the 5s — the remount re-arms with whatever dwell is left.
        float dwellLeft = MathF.Max(1f, ShowDurationMs - (Environment.TickCount64 - openedAtMs.Value));

        // Mount the per-frame countdown ONLY while a phase is live (1 = show-delay, 2 = auto-dismiss, 3 = safe-zone
        // grace). When idle it is absent, so the tooltip costs nothing per frame (the host only ticks FrameClock while
        // something subscribes). The clock is KEYED by phase: the reconciler reuses a same-type component without
        // re-running its factory (constructor props are mount-time only), so a phase flip must REMOUNT a fresh clock
        // or the open bubble keeps the already-fired show-delay clock and the auto-dismiss never arms. WinUI keeps
        // these as separate DispatcherTimers — m_tpOpenTimer (show delay) vs m_tpCloseTimer (SPI_GETMESSAGEDURATION
        // dwell, ToolTipService_Partial.h:54/96-99) vs m_tpSafeZoneCheckTimer (1s poll, .h:22; cpp:384-414).
        Element? clock = ph == 0 ? null : Embed.Comp(() => new ToolTipClock
        {
            DurationMs = ph == 1 ? delay : ph == 2 ? dwellLeft : SafeZoneCheckMs,
            OnElapsed = ph == 1 ? OpenNow : ph == 2 ? AutoDismiss : SafeZoneCheck,
        }) with
        { Key = ph == 1 ? "tt-open-timer" : ph == 2 ? "tt-close-timer" : "tt-safezone-timer:" + poll };

        return new BoxEl
        {
            AlignSelf = FlexAlign.Start,
            OnRealized = x => anchor.Value = x,
            OnHoverMove = OnEnter,         // mouse-enter trigger (makes the target hit-testable for hover)
            OnPointerExit = OnLeave,       // mouse-leave → cancel pending / close open
            OnPointerPressed = OnPressed,  // press over the target → dismiss (never survives an interaction)
            OnFocusChanged = OnFocus,      // keyboard focus in/out of the target subtree (a11y trigger)
            Children = clock is null ? [target] : [target, clock],
        };
    }

    // WinUI ToolTip bubble (ToolTip_themeresources.xaml DefaultToolTipStyle:42-76):
    //   Background = ToolTipBackgroundBrush = AcrylicInAppFillColorDefaultBrush (:14 dark / :40 light) —
    //     theme-aware Tok.AcrylicFlyout (dark #2C2C2C @0.15 lum 0.96 fb #2C2C2C; light #FCFCFC @0.05 lum 0.96 fb #F9F9F9
    //     — light luminosity raised from WinUI's 0.85 so the plate reads solid over the Mica-lit pale pages)
    //   BorderBrush = SurfaceStrokeColorFlyout (Tok.StrokeFlyoutDefault), BorderThickness = 1
    //   CornerRadius = ControlCornerRadius (4px), Padding = ToolTipBorderPadding 9,6,9,8
    //   FontSize = ToolTipContentThemeFontSize 12, Foreground = TextFillColorPrimary, MaxWidth = 320, TextWrapping = Wrap.
    //   Shadow = the light transient elevation class (Elevation.Tooltip) — tooltips sit on the lowest popup band.
    Element BubbleContent(string text, Action<NodeHandle> onRealized)
    {
        var bubble = new BoxEl
        {
            Fill = ColorF.Transparent,
            Acrylic = Tok.AcrylicFlyout,
            BorderColor = Tok.StrokeFlyoutDefault,
            BorderWidth = 1f,
            Corners = Radii.ControlAll,
            Shadow = Elevation.Tooltip,
            MaxWidth = 320f,
            Padding = new Edges4(9, 6, 9, 8),
            // A tooltip never intercepts input (WinUI tooltips are hit-test-transparent): the bubble must not swallow
            // wheel/press under the pointer — scrolling continues through it. Its bounds still join the safe zone,
            // tested by GEOMETRY against the real pointer (SafeZoneCheck), not by hover events.
            HitTestVisible = false,
            OnRealized = onRealized,   // fresh mount per open → the capture cannot go stale (unlike a recycled row)
            Children =
            [
                new TextEl(text)
                {
                    Size = 12f,
                    Color = Tok.TextPrimary,
                    Wrap = TextWrap.Wrap,
                    MaxWidth = 302f,   // 320 − (9 + 9) horizontal padding
                },
            ],
        };
        // Parts: restyle the bubble chrome (acrylic, stroke, elevation, padding…); the Text content, the input
        // transparency, and the safe-zone node capture always win.
        return Parts.Apply(PartBubble, bubble) with { Children = bubble.Children, HitTestVisible = false, OnRealized = onRealized };
    }

    static bool InRect(in RectF r, Point2 p)
        => p.X >= r.X && p.Y >= r.Y && p.X <= r.X + r.W && p.Y <= r.Y + r.H;
}

/// <summary>
/// Invisible per-frame countdown, mounted by <see cref="ToolTip"/> only while a show-delay or auto-dismiss is pending.
/// It is the engine analogue of ToolTipService's DispatcherTimer: on mount it seeds an invisible <see cref="DurationMs"/>
/// track on its own hidden node (an Opacity 1→1 tween — no visible effect, the node is non-hit-testable), driven by the
/// AnimEngine with the real per-frame delta. Each frame it polls <c>HasTracks</c>; when the track has settled (the
/// duration elapsed) it invokes <see cref="OnElapsed"/> exactly once. Unmounts (stopping the per-frame wake) when the
/// tooltip returns to idle. This rides the host's animation clock — the same real-time source the open/close fade uses —
/// so the 800ms show delay and 5s auto-dismiss are wall-accurate without a Hosting-layer dependency.
/// </summary>
internal sealed class ToolTipClock : Component
{
    public required float DurationMs;
    public required Action OnElapsed;

    public override Element Render()
    {
        var tick = UseContext(FrameClock.Tick);   // re-render every frame while mounted
        var self = UseRef<NodeHandle>(default);
        var seeded = UseRef<bool>(false);
        var fired = UseRef<bool>(false);

        UseEffect(() =>
        {
            var anim = Context.Anim;
            var scene = Context.Scene;
            if (anim is null || scene is null || self.Value.IsNull || !scene.IsLive(self.Value)) return;

            if (!seeded.Value)
            {
                seeded.Value = true;
                // Invisible duration track on this hidden node: Opacity 1→1 over DurationMs (linear). No paint change;
                // its only purpose is to be ticked by the AnimEngine and settle (be removed) once DurationMs has elapsed.
                anim.Animate(self.Value, AnimChannel.Opacity, 1f, 1f, DurationMs, Easing.Linear);
                return;
            }
            // Track settled (countdown elapsed) → fire the pending action once.
            if (!fired.Value && !anim.HasTracks(self.Value))
            {
                fired.Value = true;
                OnElapsed();
            }
        }, tick);

        return new BoxEl { HitTestVisible = false, OnRealized = x => self.Value = x };
    }
}
