using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>
/// A controlled WinUI <c>Popup</c>/<c>Flyout</c> primitive over the shared <see cref="OverlayHost"/> machinery: an
/// <paramref name="anchor"/> element plus arbitrary <paramref name="content"/> displayed above the page, its open state
/// owned by a caller <see cref="Signal{Boolean}"/>. Setting <c>isOpen.Value</c> opens/closes it; any close the
/// caller did not ask for (light-dismiss/click-outside, Escape, or the host closing it on its own: a competing flyout, a
/// KeepAlive park, a dead anchor) writes the signal BACK to <c>false</c> and fires <c>onOpenChanged(false)</c> exactly once
/// (a close the caller asked for by writing <c>false</c> does NOT echo <c>onOpenChanged</c>). The
/// flip/nudge/live-anchor-follow/focus-restore/light-dismiss all come free from the overlay host + FlyoutPositioner.
/// The signal freezes at mount (a plain field the run-once core factory sets — component-props-contract.md) — swapping
/// the signal requires a re-key (the controlled-input contract); the <paramref name="anchor"/> and <paramref name="content"/>, by contrast, are RE-PUSHED live on every
/// parent re-render (<see cref="Popup.Props"/> + <c>UseProps</c>), so a re-rendered trigger stays current. For an
/// event-driven, self-managed flyout button use <see cref="Flyout.Attach"/>.
/// </summary>
public static class Popup
{
    /// <param name="anchor">The trigger/anchor element (a button, a row, any element) the popup is positioned against.</param>
    /// <param name="content">The popup body, built lazily at open time (wrapped by the host's acrylic FlyoutSurface).</param>
    /// <param name="isOpen">The controlled open-state signal. <c>null</c> = the primitive materializes its own internal
    /// signal (uncontrolled: "the control made its own signal"), so a caller who only wants light-dismiss behaviour need
    /// not thread one.</param>
    /// <param name="onOpenChanged">Fired when the popup CLOSES itself (light-dismiss, Escape, or a host close: a competing flyout, a parked/dead anchor) with <c>false</c>;
    /// never on a caller-driven open/close (no echo).</param>
    public static Element Create(
        Element anchor,
        Func<Element> content,
        Signal<bool>? isOpen = null,
        Action<bool>? onOpenChanged = null,
        FlyoutPlacement placement = FlyoutPlacement.BottomLeft,
        PopupOptions options = default)
        => Embed.Comp(new Props(anchor, content), () => new PopupCore
        {
            IsOpenSignal = isOpen,
            OnOpenChanged = onOpenChanged,
            Placement = placement,
            Options = options.Equals(default) ? new PopupOptions(Chrome: PopupChrome.Popup) : options,
        });

    /// <summary>The RE-PUSHED half of the popup's inputs (<c>Embed.Comp(props, …)</c>): the anchor element and the
    /// content factory are rebuilt by the caller on every parent re-render, so they ride the props channel and stay
    /// LIVE on the reused core (a reused ComponentEl never re-runs its factory — see
    /// design/subsystems/component-props-contract.md). The core reads them with <c>UseProps</c>. Everything else
    /// (the open signal, onOpenChanged, placement, options) is a documented MOUNT-ONLY seed: plain fields set in the
    /// run-once factory closure, so a swap requires a re-key.</summary>
    internal sealed record Props(Element Anchor, Func<Element> Content);
}

/// <summary>Internal controlled-popup component: captures the anchor node (<see cref="BoxEl.OnRealized"/>), resolves the
/// overlay service (<c>UseRequiredContext</c>), and drives an AUTO-TRACKED effect off the open signal — open when it
/// reads <c>true</c>, close when <c>false</c>. Any close the core did not start
/// itself (light-dismiss, Escape, a host-initiated programmatic close) writes the signal back + fires onOpenChanged(false) once.</summary>
internal sealed class PopupCore : Component
{
    // Mount-only seeds (the controlled-input contract: plain fields the run-once factory sets). The LIVE inputs —
    // the anchor element + the content factory — arrive through Popup.Props (UseProps in Render).
    public Signal<bool>? IsOpenSignal;
    public Action<bool>? OnOpenChanged;
    public FlyoutPlacement Placement = FlyoutPlacement.BottomLeft;
    public PopupOptions Options;

    public override Element Render()
    {
        // Re-pushed props (anchor + content stay live across parent re-renders); reading them subscribes this render.
        var p = UseProps<Popup.Props>();
        // Auto-materialize the open signal when the caller passed none (the controlled-input "control made its own
        // signal" contract). The UseSignal call is unconditional (stable hook order); the field only selects which one.
        var owned = UseSignal(false);
        var isOpen = IsOpenSignal ?? owned;
        var svc = UseRequiredContext(Overlay.Service);
        var anchorRef = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var placement = Placement;
        var options = Options;
        var content = p.Content;      // latest content factory (the effect body is re-bound every render)
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
                // Close FINISHED (ClosedWithCauseAction fires once at finalize). A close this core started itself (the
                // caller wrote false → the else branch below; the unmount teardown) has already dropped `handle`, so it
                // must not echo. Any other close is the host's own: light-dismiss / Escape, or a Programmatic close the
                // caller never asked for (a competing root flyout, a KeepAlive park, a parked or dead anchor). Those
                // write the controlled signal BACK; otherwise isOpen keeps reading true over a closed popup and the next
                // `isOpen = true` is dropped by the signal's equality gate.
                h.ClosedWithCauseAction = cause =>
                {
                    if (cause == OverlayCloseCause.Programmatic && !ReferenceEquals(handle.Value, h)) return;
                    isOpen.Value = false;          // write the controlled signal BACK
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
        // Drop the handle BEFORE closing so the finalize reads it as a self-initiated close: no write-back into a caller
        // signal a re-keyed successor may already have reopened, and no onOpenChanged after unmount.
        UseEffect(() => (Action?)(() =>
        {
            var h = handle.Value;
            handle.Value = null;
            if (h is { IsOpen: true }) h.Close();
        }), default);

        return new BoxEl
        {
            AlignSelf = FlexAlign.Start,
            OnRealized = h => anchorRef.Value = h,
            Children = [p.Anchor],
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
