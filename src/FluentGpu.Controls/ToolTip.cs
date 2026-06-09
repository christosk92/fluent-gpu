using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;

namespace FluentGpu.Controls;

/// <summary>How the tooltip bubble is positioned — WinUI <c>PlacementMode</c> (the two modes ToolTipService actually
/// drives): <see cref="Top"/> = centered above the TARGET, flipping on a collision (the WinUI default,
/// ToolTip_Partial.cpp:1119-1122 "Fall back to the default - PlacementMode_Top"); <see cref="Mouse"/> = at the last
/// POINTER position, offset 11px below the cursor (ToolTip_Partial.h:56 <c>m_mousePlacementVerticalOffset = 11</c>;
/// ToolTip_Partial.cpp:976-984 "align ToolTip with the bottom left corner of mouse bounding rectangle").</summary>
public enum ToolTipPlacementMode : byte { Top, Mouse }

/// <summary>
/// A WinUI <c>ToolTip</c> (framework <c>ToolTip</c> + <c>ToolTipService</c>): wraps a target element and surfaces a small
/// text bubble anchored above it (or at the pointer in <see cref="ToolTipPlacementMode.Mouse"/>). 1:1 with the
/// framework's automatic-tooltip behavior:
/// <list type="bullet">
/// <item>Hover trigger — pointer-over the target opens the bubble after the initial show delay
///   (<c>SPI_GETMOUSEHOVERTIME</c> 400ms × 2 = 800ms; a re-show within <see cref="BetweenShowDelayMs"/> = 200ms uses the
///   reshow delay — see <see cref="MouseReshowDelayMs"/> for why that is 400ms, not the spec'd 600ms). Pointer-leave
///   cancels a pending open or closes the open bubble (ToolTipService_Partial.cpp:670 OnOwnerLeaveInternal).</item>
/// <item>Keyboard trigger — KEYBOARD focus landing inside the target opens the bubble after 800ms (×2, normal AND
///   reshow: ToolTipService_Partial.cpp:1777-1779), exactly WinUI's OnOwnerGotFocus gate
///   (ToolTipService_Partial.cpp:1648-1664: only <c>FocusState::Keyboard</c> shows the tooltip — pointer focus does
///   not). Focus leaving closes it (cpp:1696-1706 OnOwnerLostFocus → OnOwnerLeaveInternal).</item>
/// <item>Press dismiss — a pointer press over the target closes an open bubble without re-arming it (tooltips never
///   survive an interaction with their owner).</item>
/// <item>Auto-dismiss — an open bubble closes itself after <see cref="ShowDurationMs"/> (<c>SPI_GETMESSAGEDURATION</c>
///   default 5s).</item>
/// <item>Click trigger — the legacy click-to-toggle path is preserved for call-site compat (re-click / Escape closes).</item>
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
    public Element Target = new BoxEl();
    public string Text = "";
    public bool OpenOnMount;   // deterministic visual-shot hook: open the real tooltip after first mount

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

    public static Element Wrap(Element target, string text)
        => Embed.Comp(() => new ToolTip { Target = target, Text = text });

    public override Element Render()
    {
        var svc = UseContext(Overlay.Service);
        var hooks = UseContext(InputHooks.Current);
        var anchor = UseRef<NodeHandle>(default);
        var h = UseRef<OverlayHandle?>(null);
        var autoOpened = UseRef(false);
        var lastPointerLocal = UseRef<Point2>(default);   // last hover position (wrapper-local) → Mouse placement
        var suppressClick = UseRef(false);                // a press-dismiss must not re-toggle on the click that follows

        // Timer phase: 0 = idle, 1 = show-delay counting down (open after it), 2 = bubble open (auto-dismiss counting down).
        var phase = UseSignal(0);
        // 3-state input mode of the pending/open tooltip (WinUI AutomaticToolTipInputMode): 0 = mouse, 1 = keyboard.
        var keyboardMode = UseRef(false);
        // GetTickCount()-style monotonic ms at the last close → re-show detection (BETWEEN_SHOW_DELAY_MS window). WinUI
        // uses GetTickCount() literally; Environment.TickCount64 is the same monotonic OS clock (and a pure, alloc-free read).
        var lastClosedAtMs = UseRef<long>(long.MinValue / 2);
        var placementMode = Placement;

        void OpenNow()
        {
            phase.Value = 2;   // bubble open → arm the auto-dismiss countdown
            if (h.Value is { IsOpen: true }) return;
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
                    BubbleContent,
                    FlyoutPlacement.BottomEdgeAlignedLeft,
                    options,
                    owner: () => anchor.Value);
            }
            else
            {
                h.Value = svc.Open(
                    () => anchor.Value,
                    BubbleContent,
                    // WinUI default PlacementMode.Top: CENTERED above the target (ToolTip_Partial.cpp:1119-1122 default;
                    // target-mode placement centers on the target); FlyoutPositioner flips below on a collision.
                    FlyoutPlacement.Top,
                    options);
            }
        }

        void CloseNow()
        {
            bool wasOpen = phase.Peek() == 2;
            if (h.Value is { IsOpen: true } o) o.Close();
            h.Value = null;
            if (wasOpen) lastClosedAtMs.Value = Environment.TickCount64;   // mark close time for the re-show window
            keyboardMode.Value = false;
            phase.Value = 0;
        }

        // Pointer-enter (OnHoverMove fires on any move while hovering): begin the initial-show-delay countdown if idle.
        // ToolTipService.OnOwnerEnterInternal — Mouse mode, reshow if the previous tooltip closed < 200ms ago.
        void OnEnter(Point2 local)
        {
            lastPointerLocal.Value = local;   // tracked even while open — WinUI re-reads the current point at placement
            if (phase.Peek() != 0 || (h.Value is { IsOpen: true })) return;
            keyboardMode.Value = false;
            phase.Value = 1;   // show-delay counting down
        }

        // Pointer-leave: cancel a pending open, or close an open bubble (ToolTipService.OnOwnerLeaveInternal → Cancel).
        void OnLeave() => CloseNow();

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

        // Pointer press over the target: dismiss an open bubble (and a pending one) — and swallow the click that
        // follows so the legacy click-to-toggle doesn't instantly re-open it.
        void OnPressed(PointerEventArgs _)
        {
            if (phase.Peek() == 0) return;
            suppressClick.Value = true;
            CloseNow();
        }

        void Toggle()
        {
            if (suppressClick.Value) { suppressClick.Value = false; return; }
            if (h.Value is { IsOpen: true } || phase.Peek() == 2) { CloseNow(); return; }
            OpenNow();
        }

        UseEffect(() =>
        {
            if (!OpenOnMount || autoOpened.Value) return;
            autoOpened.Value = true;
            OpenNow();
        }, OpenOnMount);

        int ph = phase.Value;   // subscribe → re-render when the timer phase changes (mount/unmount the clock)
        // GetInitialShowDelay: Mouse ×2 normal / ×1 reshow (truncated 1.5 — see MouseReshowDelayMs); Keyboard ×2 always.
        bool isReshow = Environment.TickCount64 - lastClosedAtMs.Value < (long)BetweenShowDelayMs;
        float delay = keyboardMode.Value ? KeyboardShowDelayMs : (isReshow ? MouseReshowDelayMs : MouseShowDelayMs);

        // Mount the per-frame countdown ONLY while a phase is live (1 = show-delay, 2 = auto-dismiss). When idle it is
        // absent, so the tooltip costs nothing per frame (the host only ticks FrameClock while something subscribes).
        Element? clock = ph == 0 ? null : Embed.Comp(() => new ToolTipClock
        {
            DurationMs = ph == 1 ? delay : ShowDurationMs,
            OnElapsed = ph == 1 ? OpenNow : CloseNow,
        });

        return new BoxEl
        {
            AlignSelf = FlexAlign.Start,
            OnRealized = x => anchor.Value = x,
            OnHoverMove = OnEnter,         // mouse-enter trigger (makes the target hit-testable for hover)
            OnPointerExit = OnLeave,       // mouse-leave → cancel pending / close open
            OnPointerPressed = OnPressed,  // press over the target → dismiss (never survives an interaction)
            OnFocusChanged = OnFocus,      // keyboard focus in/out of the target subtree (a11y trigger)
            OnClick = Toggle,              // legacy click-to-toggle (call-site compat); re-click closes
            Children = clock is null ? [Target] : [Target, clock],
        };
    }

    // WinUI ToolTip bubble (ToolTip_themeresources.xaml DefaultToolTipStyle):
    //   Background = AcrylicInAppFillColorDefault (AcrylicSpec.Flyout — the same in-app acrylic recipe + solid fallback)
    //   BorderBrush = SurfaceStrokeColorFlyout (Tok.StrokeFlyoutDefault), BorderThickness = 1
    //   CornerRadius = ControlCornerRadius (4px), Padding = ToolTipBorderPadding 9,6,9,8
    //   FontSize = ToolTipContentThemeFontSize 12, Foreground = TextFillColorPrimary, MaxWidth = 320, TextWrapping = Wrap.
    //   Shadow = the light transient elevation class (Elevation.Tooltip) — tooltips sit on the lowest popup band.
    Element BubbleContent() => new BoxEl
    {
        Fill = ColorF.Transparent,
        Acrylic = AcrylicSpec.Flyout,
        BorderColor = Tok.StrokeFlyoutDefault,
        BorderWidth = 1f,
        Corners = Radii.ControlAll,
        Shadow = Elevation.Tooltip,
        MaxWidth = 320f,
        Padding = new Edges4(9, 6, 9, 8),
        Children =
        [
            new TextEl(Text)
            {
                Size = 12f,
                Color = Tok.TextPrimary,
                Wrap = TextWrap.Wrap,
                MaxWidth = 302f,   // 320 − (9 + 9) horizontal padding
            },
        ],
    };
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
