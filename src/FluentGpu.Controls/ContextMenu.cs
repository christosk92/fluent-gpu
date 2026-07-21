using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;

namespace FluentGpu.Controls;

/// <summary>The content of a context menu, built lazily at open time. A non-empty <see cref="Primary"/> icon strip
/// selects the Win11 Explorer <b>command-bar</b> body (a horizontal quick-action row over the <see cref="Rows"/>); an
/// empty <see cref="Primary"/> selects a plain vertical <b>menu</b> (<see cref="MenuFlyout"/>). <see cref="Rows"/> are
/// the labeled/toggle/sub-menu rows either way (mapped onto the command-bar overflow when a primary strip is present).
/// Return this from the <c>ContextMenu.Attach</c> factory — a <c>null</c> factory result, or a model with no enabled
/// non-separator entry, opens nothing.</summary>
public readonly record struct ContextMenuModel(
    IReadOnlyList<AppBarCommand> Primary,
    IReadOnlyList<MenuFlyoutItem> Rows)
{
    /// <summary>A plain vertical menu (no primary strip).</summary>
    public ContextMenuModel(IReadOnlyList<MenuFlyoutItem> rows) : this([], rows) { }
}

/// <summary>Tunables for an attached context menu. <c>TouchInputMode</c> is not a knob — it is derived from the
/// trigger (a touch long-press opens with the roomier touch metrics).</summary>
public sealed record ContextMenuOptions
{
    /// <summary>Minimum menu width — Win11 Explorer app menus want ~250 (the bare <see cref="MenuFlyout"/> default is
    /// the 96px FlyoutThemeMinWidth). Applied to the menu column and the command-bar overflow region alike.</summary>
    public float MinWidth { get; init; } = 250f;
    /// <summary>Placement of a pointer/long-press-opened menu relative to the right-tap POINT (the positioner flips it
    /// against the monitor work area when it would overflow).</summary>
    public FlyoutPlacement PointerPlacement { get; init; } = FlyoutPlacement.BottomEdgeAlignedLeft;
    /// <summary>Placement of a keyboard-opened menu relative to the element RECT (WinUI ContextRequested with
    /// TryGetPosition == false anchors to the element, not a point).</summary>
    public FlyoutPlacement KeyboardPlacement { get; init; } = FlyoutPlacement.BottomEdgeAlignedLeft;
    /// <summary>Invoked when the menu closes, with the close cause (light-dismiss / Escape / programmatic).</summary>
    public Action<OverlayCloseCause>? OnClosed { get; init; }

    internal static readonly ContextMenuOptions Default = new();
}

/// <summary>
/// Attaches a Win11-style context menu to any <see cref="BoxEl"/> in ONE line — all open/position/dismiss logic lives
/// here (over the <see cref="OverlayHost"/>/engine seams). The attach COMPOSES onto the element (chaining, never
/// clobbering, its <see cref="BoxEl.OnRealized"/>/<see cref="BoxEl.OnContextRequested"/>), so a bound-row skin keeps
/// its own press/key/focus handlers. The <paramref name="factory"/> runs AT OPEN TIME (lazy — read the current
/// selection/row index there, e.g. <c>scope.Index.Peek()</c> in a bound row), so one attach per virtualized slot is
/// recycle-correct. A right-click / Menu key / touch long-press over the element raises the request; a new context
/// menu closes any previously-open one (WinUI's outside-right-click-supersedes).
/// </summary>
public static class ContextMenu
{
    // The single currently-open context menu (process-static): a new open supersedes it (close-then-open). Fine for a
    // single interaction stream; revisit if true multi-window input concurrency lands (risk flagged in the design).
    private static OverlayHandle? _currentHandle;
    private static Action? _currentClose;   // fade-aware close for a tracker-forced supersede (rides the 83ms fade for CommandBar)

    /// <summary>Attach a context menu to <paramref name="element"/>. <paramref name="svc"/> is the overlay service
    /// (one <c>UseContext(Overlay.Service)</c> per component — the DropDownButton canon). <paramref name="factory"/> is
    /// invoked at open time; return <c>null</c> (or an all-disabled/empty model) to open nothing. The anchor node is
    /// read from <see cref="ContextRequestEventArgs.Node"/> at open time — NOT captured via <c>OnRealized</c>, which
    /// fires at realization only and goes stale the first time a factory-built element re-renders (the stale-null
    /// capture opened menus at the window origin on re-rendered Home cards).</summary>
    public static BoxEl Attach(BoxEl element, IOverlayService svc, Func<ContextMenuModel?> factory, ContextMenuOptions? options = null)
    {
        var opts = options ?? ContextMenuOptions.Default;
        Action<ContextRequestEventArgs> open = args => Open(svc, factory, opts, args);
        // Chain: the element's own handler runs FIRST (routed-event flavor), then ours opens.
        return element with { OnContextRequested = TemplateParts.Chain(element.OnContextRequested, open) };
    }

    private static void Open(IOverlayService svc, Func<ContextMenuModel?> factory, ContextMenuOptions opts, ContextRequestEventArgs args)
    {
        // A new context request always supersedes the previous menu — even when THIS request then opens nothing
        // (WinUI: a right-click elsewhere dismisses the open menu regardless of whether a new one opens).
        CloseCurrent();

        if (factory() is not { } model || !HasAnyEnabled(model)) return;

        bool commandBar = model.Primary.Count > 0;
        bool keyboard = args.Trigger == ContextRequestTrigger.Keyboard;
        bool invoke = args.Trigger == ContextRequestTrigger.Invoke;
        bool rectAnchor = keyboard || invoke;   // no pointer point → anchor to a node RECT, not a contact point
        bool touch = args.Trigger == ContextRequestTrigger.Hold;
        Point2 point = args.Position;   // node-LOCAL (rect-anchored triggers: the node centre — unused, we anchor to the rect)

        OverlayHandle? handle = null;
        var fadeSlot = new Ref<Action?>(null);   // the CommandBar body fills this with its 83ms fade-close
        Action close = () => handle?.Close();
        Func<Element> content = () => Body(model, opts, close, fadeSlot, focusFirst: keyboard, touch: touch);

        // Identical to the DropDownButton canon: windowed DWM-acrylic popup (ConstrainToRootBounds=false), work-area
        // flip, Esc / outside / blur light-dismiss, SavedFocus restore, FocusTrap Tab-cycle. Motion is per-chrome:
        // Menu style = MenuPopupThemeTransition slide-reveal; CommandBar style = the WinUI CommandBarFlyout contract
        // (no popup transition — the body's own 83ms OpeningOpacityStoryboard fade — but still windowed with the
        // transient-acrylic material + 83ms host close fade, CommandBarFlyout.cpp:43-44 ShouldConstrainToRootBounds(false)).
        var popts = new PopupOptions(
            FocusTrap: true,
            DismissBehavior: DismissBehavior.LightDismiss,
            Chrome: commandBar ? PopupChrome.CommandBar : PopupChrome.Flyout)
        {
            ConstrainToRootBounds = false,
            // Pointer/invoke menus preserve the invoker's focus. Keyboard menus still focus the first command.
            PreserveFocusOnOpen = !keyboard,
        };

        // The anchor node, value-copied from the reused args. Rect-anchored triggers use args.Source: Keyboard sets
        // Source == Node (the focused owner), Invoke sets Source == the ClickRequestsContext button (below the owner in
        // the tree) — so a click-invoked menu opens against the BUTTON, not the whole row. Pointer / long-press open AT
        // the point on args.Node (Source == Node there too).
        var anchor = rectAnchor ? args.Source : args.Node;
        Func<NodeHandle> owner = () => anchor;
        // Rect-anchored (keyboard / invoke): anchor to the node RECT via KeyboardPlacement. Keyboard focuses the first
        // item (TryGetPosition-false), Invoke does NOT (pointer-originated) — carried by `focusFirst: keyboard` above.
        // Pointer / long-press: open AT the point (owner rect + local − FlyoutMargin, via OpenAtLocal).
        handle = rectAnchor
            ? svc.Open(owner, content, opts.KeyboardPlacement, popts)
            : svc.OpenAtLocal(owner, point, content, opts.PointerPlacement, popts);

        var opened = handle!;   // both Open/OpenAtLocal return a live handle
        _currentHandle = opened;
        _currentClose = () => { if (opened.IsOpen) { if (fadeSlot.Value is { } fade) fade(); else opened.Close(); } };
        opened.ClosedAction = () => { if (ReferenceEquals(_currentHandle, opened)) { _currentHandle = null; _currentClose = null; } };
        if (opts.OnClosed is { } onClosed) opened.ClosedWithCauseAction = onClosed;
    }

    // Close the currently-tracked context menu (if any) through its fade-aware closer. Idempotent.
    private static void CloseCurrent()
    {
        var close = _currentClose;
        _currentHandle = null;
        _currentClose = null;
        close?.Invoke();
    }

    // Build the inner body: the Explorer command-bar shape when a primary strip is present, else a plain menu.
    private static Element Body(ContextMenuModel model, ContextMenuOptions opts, Action close, Ref<Action?> fadeSlot, bool focusFirst, bool touch)
    {
        if (model.Primary.Count > 0)
        {
            // Map the menu rows onto the command-bar overflow (AppBarCommand secondary). AlwaysExpanded = the Explorer
            // "overflow shown immediately" shape; TouchInputMode = roomier rows for a long-press open.
            var secondary = ToBarCommands(model.Rows);
            return CommandBarFlyout.BuildBody(
                model.Primary, secondary, close, fadeSlot,
                parts: null, alwaysExpanded: true, overflowMinWidth: opts.MinWidth, touchInputMode: touch,
                labeledPrimary: true);   // Win11 Explorer shell-menu shape: icon-over-label strip, no accent-pill toggles
        }
        return MenuFlyout.Build(model.Rows, close, opts.MinWidth, parts: null, focusFirst: focusFirst);
    }

    // MenuFlyoutItem → AppBarCommand for the command-bar overflow. Radio maps to a toggle (AppBarCommandKind has no
    // Radio kind — a documented v1 limitation); a SubMenu keeps its nested items on AppBarCommand.Flyout.
    private static AppBarCommand[] ToBarCommands(IReadOnlyList<MenuFlyoutItem> rows)
    {
        var result = new AppBarCommand[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            var it = rows[i];
            result[i] = it.Kind switch
            {
                MenuItemKind.Separator => AppBarCommand.Separator,
                MenuItemKind.Toggle or MenuItemKind.Radio =>
                    new AppBarCommand(it.Icon, it.Label, it.Invoke, AppBarCommandKind.ToggleButton, it.IsChecked, it.Enabled)
                    { AcceleratorText = it.AcceleratorText },
                MenuItemKind.SubMenu =>
                    new AppBarCommand(it.Icon, it.Label, null, AppBarCommandKind.Button, false, it.Enabled)
                    { AcceleratorText = it.AcceleratorText, Flyout = it.SubItems },
                _ =>
                    new AppBarCommand(it.Icon, it.Label, it.Invoke, AppBarCommandKind.Button, false, it.Enabled)
                    { AcceleratorText = it.AcceleratorText },
            };
        }
        return result;
    }

    // At least one enabled, non-separator entry anywhere (primary strip or rows) — else there is nothing to open.
    private static bool HasAnyEnabled(ContextMenuModel m)
    {
        var primary = m.Primary;
        for (int i = 0; i < primary.Count; i++)
            if (primary[i].Kind != AppBarCommandKind.Separator && primary[i].Enabled) return true;
        var rows = m.Rows;
        for (int i = 0; i < rows.Count; i++)
            if (!rows[i].IsSeparator && rows[i].Enabled) return true;
        return false;
    }
}

/// <summary>Fluent sugar for <see cref="ContextMenu.Attach"/>: <c>row.WithContextMenu(svc, () =&gt; model)</c>.</summary>
public static class ContextMenuExtensions
{
    public static BoxEl WithContextMenu(this BoxEl el, IOverlayService svc, Func<ContextMenuModel?> factory, ContextMenuOptions? options = null)
        => ContextMenu.Attach(el, svc, factory, options);
}
