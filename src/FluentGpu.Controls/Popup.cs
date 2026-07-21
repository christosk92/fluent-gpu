using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>
/// A controlled WinUI <c>Popup</c>/<c>Flyout</c> primitive over the shared <see cref="OverlayHost"/> machinery: an
/// <paramref name="anchor"/> element plus arbitrary <paramref name="content"/> displayed above the page, its open state
/// owned by a caller <see cref="Signal{Boolean}"/>. Setting <c>isOpen.Value</c> opens/closes it; a light-dismiss
/// (click-outside) or Escape writes the signal BACK to <c>false</c> and fires <c>onOpenChanged(false)</c> exactly once
/// (cause-mapped — a programmatic close, i.e. the caller writing <c>false</c>, does NOT echo <c>onOpenChanged</c>). The
/// flip/nudge/live-anchor-follow/focus-restore/light-dismiss all come free from the overlay host + FlyoutPositioner.
/// The signal freezes at mount (bind wiring is mount-only) — swapping the signal requires a re-key (the controlled-input
/// contract). For an event-driven, self-managed flyout button use <see cref="Flyout.Attach"/>.
/// </summary>
public static class Popup
{
    /// <param name="anchor">The trigger/anchor element (a button, a row, any element) the popup is positioned against.</param>
    /// <param name="content">The popup body, built lazily at open time (wrapped by the host's acrylic FlyoutSurface).</param>
    /// <param name="isOpen">The controlled open-state signal. <c>null</c> = the primitive materializes its own internal
    /// signal (uncontrolled: "the control made its own signal"), so a caller who only wants light-dismiss behaviour need
    /// not thread one.</param>
    /// <param name="onOpenChanged">Fired when the popup CLOSES itself via light-dismiss/Escape (with <c>false</c>);
    /// never on a programmatic open/close (no echo).</param>
    public static Element Create(
        Element anchor,
        Func<Element> content,
        Signal<bool>? isOpen = null,
        Action<bool>? onOpenChanged = null,
        FlyoutPlacement placement = FlyoutPlacement.BottomLeft,
        PopupOptions options = default)
        => Embed.Comp(() => new PopupCore
        {
            Anchor = anchor,
            Content = content,
            IsOpenSignal = isOpen,
            OnOpenChanged = onOpenChanged,
            Placement = placement,
            Options = options.Equals(default) ? new PopupOptions(Chrome: PopupChrome.Popup) : options,
        });
}

/// <summary>Internal controlled-popup component: captures the anchor node (<see cref="BoxEl.OnRealized"/>), resolves the
/// overlay service (<c>UseRequiredContext</c>), and drives an AUTO-TRACKED effect off the open signal — open when it
/// reads <c>true</c>, close when <c>false</c>. Light-dismiss/Escape (any non-programmatic close cause) writes the signal
/// back + fires onOpenChanged(false) once.</summary>
internal sealed class PopupCore : Component
{
    public required Element Anchor;
    public required Func<Element> Content;
    public Signal<bool>? IsOpenSignal;
    public Action<bool>? OnOpenChanged;
    public FlyoutPlacement Placement = FlyoutPlacement.BottomLeft;
    public PopupOptions Options;

    public override Element Render()
    {
        // Auto-materialize the open signal when the caller passed none (the controlled-input "control made its own
        // signal" contract). The UseSignal call is unconditional (stable hook order); the field only selects which one.
        var owned = UseSignal(false);
        var isOpen = IsOpenSignal ?? owned;
        var svc = UseRequiredContext(Overlay.Service);
        var anchorRef = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var placement = Placement;
        var options = Options;
        var content = Content;
        var onOpenChanged = OnOpenChanged;

        // Auto-tracked open/close driver: reading isOpen.Value subscribes this effect, so it re-runs on every open-state
        // change (no deps list). Render itself reads no signals → the wrapper renders once; all reactivity is here.
        UseEffect(() =>
        {
            if (isOpen.Value)
            {
                if (handle.Value is { IsOpen: true }) return null;   // already open
                var h = svc.Open(() => anchorRef.Value, content, placement, options);
                handle.Value = h;
                // Close STARTED by the host (light-dismiss / Escape / programmatic). ClosedWithCauseAction fires once at
                // finalize: map the cause → a programmatic close (the caller already wrote false) must not echo.
                h.ClosedWithCauseAction = cause =>
                {
                    if (cause == OverlayCloseCause.Programmatic) return;
                    isOpen.Value = false;          // write the controlled signal BACK (light-dismiss/Escape)
                    onOpenChanged?.Invoke(false);
                };
            }
            else
            {
                if (handle.Value is { IsOpen: true } open) open.Close();   // programmatic → cause Programmatic (no echo)
                handle.Value = null;
            }
            return null;
        });

        // Unmount safety: a popup still open when its owner unmounts must close (mount-once effect; cleanup at unmount).
        UseEffect(() => (Action?)(() => { if (handle.Value is { IsOpen: true } h) h.Close(); }), default);

        return new BoxEl
        {
            AlignSelf = FlexAlign.Start,
            OnRealized = h => anchorRef.Value = h,
            Children = [Anchor],
        };
    }
}

/// <summary>
/// Event-driven sugar over <see cref="OverlayHost"/> (the <see cref="ContextMenu.Attach"/> precedent): attaches a
/// light-dismissable content flyout to a <see cref="BoxEl"/>, chaining its <see cref="BoxEl.OnClick"/> (never clobbering
/// an existing one) to open the flyout anchored to the element. Re-clicking closes it — the light-dismiss scrim (topmost
/// while open) consumes the press before it reaches the trigger, so no toggle state is needed (WinUI Flyout semantics).
/// For a fully CONTROLLED popup (a caller-owned signal, re-render-safe binding, onOpenChanged), use
/// <see cref="Popup.Create"/> instead.
/// </summary>
public static class Flyout
{
    /// <param name="anchor">The trigger element; its own <c>OnClick</c>/<c>OnRealized</c> are preserved (chained).</param>
    /// <param name="svc">The overlay service (one <c>UseContext(Overlay.Service)</c> per component — the ContextMenu/DropDownButton canon).</param>
    /// <param name="content">The flyout body, built lazily at open time.</param>
    public static BoxEl Attach(
        BoxEl anchor,
        IOverlayService svc,
        Func<Element> content,
        FlyoutPlacement placement = FlyoutPlacement.BottomLeft,
        PopupOptions options = default)
    {
        // A content flyout uses the WinUI FlyoutPresenter chrome (PopupThemeTransition) by default; a caller that
        // configured any option is taken verbatim.
        var opts = options.Equals(default) ? new PopupOptions(Chrome: PopupChrome.Popup) : options;
        var node = new Ref<NodeHandle>(default);
        void Open() { if (!node.Value.IsNull) svc.Open(() => node.Value, content, placement, opts); }
        return anchor with
        {
            OnRealized = TemplateParts.Chain(anchor.OnRealized, h => node.Value = h),
            OnClick = ChainClick(anchor.OnClick, Open),
        };
    }

    // Compose two parameterless click handlers: the element's own runs first, then ours (mirrors TemplateParts.Chain<T>).
    private static Action ChainClick(Action? existing, Action added)
        => existing is null ? added : () => { existing(); added(); };
}
