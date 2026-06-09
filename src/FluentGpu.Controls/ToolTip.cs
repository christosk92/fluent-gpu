using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;

namespace FluentGpu.Controls;

/// <summary>
/// A WinUI <c>ToolTip</c> (framework <c>ToolTip</c> + <c>ToolTipService</c>): wraps a target element and surfaces a small
/// text bubble anchored above it (WinUI default <c>PlacementMode.Top</c>, flipping below on a collision). 1:1 with the
/// framework's automatic-tooltip behavior:
/// <list type="bullet">
/// <item>Hover trigger — pointer-over the target opens the bubble after the <see cref="MouseShowDelayMs"/> initial show
///   delay (<c>SPI_GETMOUSEHOVERTIME</c> 400ms × 2 = 800ms; a re-show within <see cref="BetweenShowDelayMs"/> = 200ms uses
///   × 1.5 = 600ms). Pointer-leave cancels a pending open or closes the open bubble.</item>
/// <item>Auto-dismiss — an open bubble closes itself after <see cref="ShowDurationMs"/> (<c>SPI_GETMESSAGEDURATION</c>
///   default 5s).</item>
/// <item>Click trigger — the legacy click-to-toggle path is preserved for call-site compat (re-click / Escape closes).</item>
/// </list>
/// Chrome matches <c>ToolTip_themeresources.xaml</c>: AcrylicInAppFillColorDefault fill, 1px SurfaceStrokeColorFlyout
/// stroke, ControlCornerRadius (4px) corners, soft flyout elevation, 9,6,9,8 padding, 12px TextFillColorPrimary text,
/// MaxWidth 320, TextWrapping=Wrap. The bubble does NOT trap focus and does NOT light-dismiss on outside click
/// (<see cref="DismissBehavior.None"/>) — dismissal is owned by hover-leave + the auto-dismiss timer, exactly like
/// <c>ToolTipService</c>.
/// </summary>
public sealed class ToolTip : Component
{
    public Element Target = new BoxEl();
    public string Text = "";
    public bool OpenOnMount;   // deterministic visual-shot hook: open the real tooltip after first mount

    // ── ToolTipService timing constants (ToolTipService_Partial.h / .cpp). ────────────────────────────────────────────
    /// <summary><c>SPI_GETMOUSEHOVERTIME</c> default (DEFAULT_SPI_GETMOUSEHOVERTIME) = 400ms.</summary>
    public const float MouseHoverTimeMs = 400f;
    /// <summary>Mouse initial show delay: hover time × 2 = 800ms (GetInitialShowDelay, Mouse, first show).</summary>
    public const float MouseShowDelayMs = MouseHoverTimeMs * 2f;
    /// <summary>Mouse re-show delay: hover time × 1.5 = 600ms (GetInitialShowDelay, Mouse, isReshow).</summary>
    public const float MouseReshowDelayMs = MouseHoverTimeMs * 1.5f;
    /// <summary>BETWEEN_SHOW_DELAY_MS = 200ms — a re-open inside this window uses the shorter re-show delay.</summary>
    public const float BetweenShowDelayMs = 200f;
    /// <summary>DEFAULT_SHOW_DURATION_SECONDS = 5s — auto-dismiss after this long open.</summary>
    public const float ShowDurationMs = 5000f;

    public static Element Wrap(Element target, string text)
        => Embed.Comp(() => new ToolTip { Target = target, Text = text });

    public override Element Render()
    {
        var svc = UseContext(Overlay.Service);
        var anchor = UseRef<NodeHandle>(default);
        var h = UseRef<OverlayHandle?>(null);
        var autoOpened = UseRef(false);

        // Timer phase: 0 = idle, 1 = show-delay counting down (open after it), 2 = bubble open (auto-dismiss counting down).
        var phase = UseSignal(0);
        // GetTickCount()-style monotonic ms at the last close → re-show detection (BETWEEN_SHOW_DELAY_MS window). WinUI
        // uses GetTickCount() literally; Environment.TickCount64 is the same monotonic OS clock (and a pure, alloc-free read).
        var lastClosedAtMs = UseRef<long>(long.MinValue / 2);

        void OpenNow()
        {
            phase.Value = 2;   // bubble open → arm the auto-dismiss countdown
            if (h.Value is { IsOpen: true }) return;
            h.Value = svc.Open(
                () => anchor.Value,
                BubbleContent,
                // WinUI default PlacementMode.Top: open above the target; FlyoutPositioner flips below on a collision.
                FlyoutPlacement.TopLeft,
                // A tooltip never traps focus and never light-dismisses on outside click — it is transient and
                // dismissal is driven by hover-leave + the auto-dismiss timer (ToolTipService owns close, not the user).
                new PopupOptions(FocusTrap: false, DismissBehavior: DismissBehavior.None, Chrome: PopupChrome.Raw));
        }

        void CloseNow()
        {
            bool wasOpen = phase.Peek() == 2;
            if (h.Value is { IsOpen: true } o) o.Close();
            h.Value = null;
            if (wasOpen) lastClosedAtMs.Value = Environment.TickCount64;   // mark close time for the re-show window
            phase.Value = 0;
        }

        // Pointer-enter (OnHoverMove fires on any move while hovering): begin the initial-show-delay countdown if idle.
        // ToolTipService.OnOwnerEnterInternal — Mouse mode, reshow if the previous tooltip closed < 200ms ago.
        void OnEnter(Point2 _)
        {
            if (phase.Peek() != 0 || (h.Value is { IsOpen: true })) return;
            phase.Value = 1;   // show-delay counting down
        }

        // Pointer-leave: cancel a pending open, or close an open bubble (ToolTipService.OnOwnerLeaveInternal → Cancel).
        void OnLeave() => CloseNow();

        void Toggle()
        {
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
        // GetInitialShowDelay(Mouse): × 2 normally, × 1.5 if re-opening within BETWEEN_SHOW_DELAY_MS of the last close.
        bool isReshow = Environment.TickCount64 - lastClosedAtMs.Value < (long)BetweenShowDelayMs;
        float delay = isReshow ? MouseReshowDelayMs : MouseShowDelayMs;

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
            OnHoverMove = OnEnter,       // mouse-enter trigger (makes the target hit-testable for hover)
            OnPointerExit = OnLeave,     // mouse-leave → cancel pending / close open
            OnClick = Toggle,            // legacy click-to-toggle (call-site compat); re-click closes
            Children = clock is null ? [Target] : [Target, clock],
        };
    }

    // WinUI ToolTip bubble (ToolTip_themeresources.xaml DefaultToolTipStyle):
    //   Background = AcrylicInAppFillColorDefault (Tok.FillLayerDefault, the solid acrylic-default fallback)
    //   BorderBrush = SurfaceStrokeColorFlyout (Tok.StrokeFlyoutDefault), BorderThickness = 1
    //   CornerRadius = ControlCornerRadius (4px), Padding = ToolTipBorderPadding 9,6,9,8
    //   FontSize = ToolTipContentThemeFontSize 12, Foreground = TextFillColorPrimary, MaxWidth = 320, TextWrapping = Wrap.
    Element BubbleContent() => new BoxEl
    {
        Fill = ColorF.Transparent,
        Acrylic = AcrylicSpec.Flyout,
        BorderColor = Tok.StrokeFlyoutDefault,
        BorderWidth = 1f,
        Corners = Radii.ControlAll,
        Shadow = Elevation.Flyout,
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
