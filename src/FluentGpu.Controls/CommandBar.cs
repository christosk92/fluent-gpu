using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;

namespace FluentGpu.Controls;

/// <summary>A single primary command in a <see cref="CommandBar"/>, rendered as an AppBarButton (legacy shim record —
/// new code passes <see cref="AppBarCommand"/> lists).</summary>
public sealed record CommandBarButton(string Glyph, string Label, Action OnClick);

/// <summary>WinUI <c>CommandBar.ClosedDisplayMode</c>: how the CLOSED bar renders — Compact (48px icon strip),
/// Minimal (24px sliver with only the … More button), Hidden (collapsed).</summary>
public enum CommandBarDisplayMode : byte { Compact = 0, Minimal = 1, Hidden = 2 }

/// <summary>
/// A WinUI CommandBar (CommandBar_themeresources.xaml): a horizontal toolbar of primary commands with a trailing
/// "…" MoreButton that opens the SECONDARY commands as an overflow menu and expands the bar.
/// <list type="bullet">
/// <item><b>Closed</b> — chromeless: Background = CommandBarBackground = ControlFillColorTransparent (:9), no border;
/// height = AppBarThemeCompactHeight 48 (:72) in Compact mode (icon-only buttons), 24 in Minimal, 0 in Hidden.</item>
/// <item><b>Open</b> — Background swaps to CommandBarBackgroundOpen = AcrylicInAppFillColorDefault (:10) with the
/// ControlCornerRadius 4 + OpenBorder; primary buttons grow to the labeled 64px FullSize layout
/// (ContentTransform.Y → CompactVerticalDelta) and the overflow popup opens under the … button. The WinUI
/// DisplayModeStates storyboards run the open leg over ControlNormalAnimationDuration (250ms) and the close leg over
/// ControlFastAnimationDuration (167ms), both on the ControlFastOutSlowInKeySpline 0,0,0,1 (:124-247
/// CompactClosed↔CompactOpenUp/Down VisualTransitions) — the bar's height/label reveal rides a matching
/// LayoutTransition tween; the overflow popup itself is the overlay's menu reveal.</item>
/// <item><b>Overflow</b> — CommandBarOverflowPresenter: MinWidth 160 / MaxWidth 480 (:5/:7), acrylic +
/// transient-border chrome from the overlay's FlyoutSurface; secondary commands render as Overflow-state
/// AppBarButtons (icons + toggle checks + right-aligned accelerator text) with AppBarSeparator Overflow rows.</item>
/// <item><b>MoreButton</b> — Width 48 (AppBarExpandButtonThemeWidth, generic.xaml:23), E712 ellipsis, subtle fills,
/// inner-border margin 2,6,6,6 (AppBarEllipsisButtonInnerBorderMargin :73), FocusVisualMargin −3.</item>
/// <item><b>IsSticky</b> — a non-sticky open bar light-dismisses (click-outside/Escape closes); sticky stays open
/// until the … button is clicked again (WinUI AppBar sticky semantics).</item>
/// </list>
/// </summary>
public sealed class CommandBar : Component
{
    // Template parts (the WinUI x:Name vocabulary where one exists; see TemplateParts). Each part's doc lists the
    // props the control OWNS (re-asserted after any modifier — a Parts customization cannot win those).
    /// <summary>The bar root row (the WinUI LayoutRoot grid). Owned: Animate (the open/close height tween —
    /// Bounds+Reveal on the WinUI storyboard timings), Children (the command row).</summary>
    public const string PartBar = "Bar";
    /// <summary>The trailing … More button (WinUI MoreButton). Owned: OnClick (the open/close toggle), Role,
    /// OnRealized (the overflow anchor capture, chained with any modifier-supplied handler).</summary>
    public const string PartOverflowButton = "OverflowButton";
    /// <summary>The overflow popup body (the CommandBarOverflowPresenter content — popup-built each open, so
    /// modifiers run inside the overlay body's render). Owned: Children (the secondary command rows).</summary>
    public const string PartOverflowMenu = "OverflowMenu";

    public IReadOnlyList<AppBarCommand> PrimaryCommands = [];
    public IReadOnlyList<AppBarCommand> SecondaryCommands = [];
    public CommandBarDisplayMode ClosedDisplayMode = CommandBarDisplayMode.Compact;
    /// <summary>WinUI <c>IsSticky</c>: true = the open bar does NOT light-dismiss.</summary>
    public bool IsSticky;
    /// <summary>WinUI <c>DefaultLabelPosition.Collapsed</c> analogue for the OPEN bar: keep icon-only buttons.</summary>
    public bool LabelsOnOpen = true;
    public bool OpenOnMount;   // deterministic visual-shot hook
    /// <summary>WinUI Opening/Closing pair, collapsed to one callback (true = opening).</summary>
    public Action<bool>? OnOpenChanged;
    /// <summary>Lightweight per-part styling (CSS ::part): modifiers keyed by the <c>PartXxx</c> consts; see
    /// <see cref="TemplateParts"/> for the contract.</summary>
    public TemplateParts? Parts;

    // ── WinUI metrics (CommandBar_themeresources.xaml unless noted) ──
    public const float CompactHeight = 48f;       // AppBarThemeCompactHeight (:72)
    public const float FullSizeHeight = 64f;      // AppBarThemeMinHeight (:71)
    public const float MinimalHeight = 24f;       // Minimal closed sliver
    public const float MoreButtonWidth = 48f;     // AppBarExpandButtonThemeWidth (generic.xaml:23)
    public const float OverflowMinWidth = 160f;   // CommandBarOverflowMinWidth (:5)
    public const float OverflowMaxWidth = 480f;   // CommandBarOverflowMaxWidth (:7)

    /// <summary>Legacy shim: a static chromeless icon row (the pre-Wave-3 surface) — kept so existing call sites
    /// compile; the commands map to compact AppBarButtons with a working overflow-less More button.</summary>
    public static BoxEl Create(IReadOnlyList<CommandBarButton> commands)
    {
        var primary = new AppBarCommand[commands.Count];
        for (int i = 0; i < commands.Count; i++)
            primary[i] = new AppBarCommand(commands[i].Glyph, commands[i].Label, commands[i].OnClick);
        return new BoxEl { Children = [Create(primary, [])] };
    }

    public static Element Create(
        IReadOnlyList<AppBarCommand> primaryCommands,
        IReadOnlyList<AppBarCommand> secondaryCommands,
        CommandBarDisplayMode closedDisplayMode = CommandBarDisplayMode.Compact,
        bool isSticky = false,
        Action<bool>? onOpenChanged = null)
        => Embed.Comp(() => new CommandBar
        {
            PrimaryCommands = primaryCommands,
            SecondaryCommands = secondaryCommands,
            ClosedDisplayMode = closedDisplayMode,
            IsSticky = isSticky,
            OnOpenChanged = onOpenChanged,
        });

    public override Element Render()
    {
        var svc = UseContext(Overlay.Service);
        var moreAnchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var open = UseSignal(false);
        var autoOpened = UseRef(false);
        bool isOpen = open.Value;
        bool hasSecondary = SecondaryCommands.Count > 0;

        void CloseOverflow() { if (handle.Value is { IsOpen: true } h) h.Close(); }

        void OpenBar()
        {
            if (open.Peek()) return;
            open.Value = true;
            OnOpenChanged?.Invoke(true);
            if (!hasSecondary) return;
            handle.Value = svc.Open(
                () => moreAnchor.Value,
                BuildOverflow,
                // The overflow drops under the … button, right edges aligned (the WinUI overflow alignment);
                // opens UP automatically when there's no room (the positioner's fallback). Menus are windowed.
                FlyoutPlacement.BottomEdgeAlignedRight,
                new PopupOptions(
                    DismissBehavior: IsSticky ? DismissBehavior.None : DismissBehavior.LightDismiss,
                    Chrome: PopupChrome.Flyout)
                { ConstrainToRootBounds = false });
            handle.Value.ClosedAction = () =>
            {
                handle.Value = null;
                if (open.Peek()) { open.Value = false; OnOpenChanged?.Invoke(false); }   // light-dismiss closes the BAR too
            };
        }

        void CloseBar()
        {
            if (!open.Peek()) return;
            open.Value = false;
            OnOpenChanged?.Invoke(false);
            CloseOverflow();
        }

        void Toggle() { if (open.Peek()) CloseBar(); else OpenBar(); }

        UseEffect(() =>
        {
            if (!OpenOnMount || autoOpened.Value) return;
            autoOpened.Value = true;
            OpenBar();
        }, OpenOnMount);

        // ── The overflow popup body: secondary commands as Overflow-state AppBarButtons + Overflow separators. ──
        Element BuildOverflow()
        {
            bool hasToggles = false, hasIcons = false;
            foreach (var c in SecondaryCommands)
            {
                if (c.Kind == AppBarCommandKind.Separator) continue;
                if (c.Kind == AppBarCommandKind.ToggleButton) hasToggles = true;
                if (c.Icon.HasContent) hasIcons = true;
            }
            var rows = new List<Element>(SecondaryCommands.Count);
            foreach (var cmd in SecondaryCommands)
            {
                if (cmd.Kind == AppBarCommandKind.Separator) { rows.Add(AppBarSeparator.Create(overflow: true)); continue; }
                var c = cmd;
                rows.Add(AppBarButton.CreateOverflow(c, hasToggles, hasIcons, onInvoke: () =>
                {
                    c.Invoke?.Invoke();
                    // Toggles keep the overflow open (WinUI AppBarToggleButton in overflow); buttons close the bar.
                    if (c.Kind != AppBarCommandKind.ToggleButton) CloseBar();
                }));
            }
            var menu = new BoxEl
            {
                Direction = 1,
                MinWidth = OverflowMinWidth,
                MaxWidth = OverflowMaxWidth,
                Padding = new Edges4(0, 2, 0, 2),   // ItemsPresenter margin inside the overflow presenter
                Children = rows.ToArray(),
            };
            // Parts: restyle the overflow chrome (widths, padding…); the secondary rows are the mechanism.
            return Parts.Apply(PartOverflowMenu, menu) with { Children = menu.Children };
        }

        // ── The bar ──
        bool hidden = !isOpen && ClosedDisplayMode == CommandBarDisplayMode.Hidden;
        bool minimal = !isOpen && ClosedDisplayMode == CommandBarDisplayMode.Minimal;
        if (hidden) return new BoxEl { Height = 0f, ClipToBounds = true };

        float barHeight = isOpen ? (LabelsOnOpen ? FullSizeHeight : CompactHeight)
                        : minimal ? MinimalHeight
                        : CompactHeight;

        var children = new List<Element>(PrimaryCommands.Count + 2);
        if (!minimal)
        {
            foreach (var cmd in PrimaryCommands)
            {
                var c = cmd;
                if (c.Kind == AppBarCommandKind.Separator) { children.Add(AppBarSeparator.Create()); continue; }
                bool labeled = isOpen && LabelsOnOpen;
                // The top-level CommandBar strip uses the glyph-font buttons; a themed name falls back to its glyph here
                // (themed layered icons are the flyout/overflow path — CommandBarFlyout + the overflow rows above).
                children.Add(c.Kind == AppBarCommandKind.ToggleButton
                    ? AppBarToggleButton.Create(c.Icon.Glyph ?? "", c.Label, c.IsChecked, c.Enabled, isCompact: !labeled,
                        onToggled: _ => c.Invoke?.Invoke())
                    : AppBarButton.Create(c.Icon.Glyph ?? "", c.Label, () => c.Invoke?.Invoke(), c.Enabled, isCompact: !labeled,
                        accelerator: c.Accelerator));
            }
        }
        // Spacer pins the … More button to the right edge (PrimaryItemsControl is right-anchored next to the
        // MoreButton column in the WinUI grid, :821 — left-flow + right-pinned More reads identically here).
        children.Add(new BoxEl { Grow = 1f });
        Action<NodeHandle> moreCapture = h => moreAnchor.Value = h;
        var more = new BoxEl
        {
            Width = MoreButtonWidth,                            // AppBarExpandButtonThemeWidth 48 (generic.xaml:23)
            AlignSelf = FlexAlign.Stretch,                      // open: MoreButton VerticalAlignment=Stretch (:135)
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            Margin = new Edges4(2, 6, 6, 6),                    // AppBarEllipsisButtonInnerBorderMargin (:73)
            Corners = Radii.ControlAll,
            Fill = Tok.FillSubtleTransparent,
            HoverFill = Tok.FillSubtleSecondary,
            PressedFill = Tok.FillSubtleTertiary,
            HoverDurationMs = Motion.ControlFaster, PressDurationMs = Motion.ControlFaster,
            HoverEasing = Easing.FluentPopOpen, PressEasing = Easing.FluentPopOpen,
            Focusable = true,
            FocusVisualMargin = Edges4.All(-3f),                // EllipsisButton FocusVisualMargin -3 (:16)
            Role = AutomationRole.Button,
            OnRealized = moreCapture,                           // capture: the overflow popup anchors to this node
            OnClick = Toggle,
            Children =
            [
                new TextEl(Icons.More)                          // EllipsisIcon E712
                {
                    Size = 16f, FontFamily = Theme.IconFont,
                    Color = Tok.TextPrimary, PressedColor = Tok.TextSecondary,
                    DisabledColor = Tok.TextDisabled,           // CommandBarEllipsisIconForegroundDisabled (:15)
                },
            ],
        };
        // Parts: restyle the … button freely; the open/close toggle and the overflow anchor capture always win.
        if (Parts is { } mp)
        {
            var m = mp.Apply(PartOverflowButton, more);
            more = m with { OnClick = Toggle, Role = AutomationRole.Button, OnRealized = TemplateParts.Chain(moreCapture, m.OnRealized) };
        }
        children.Add(more);

        var bar = new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Start,
            Gap = 4f,
            MinHeight = barHeight,
            Padding = new Edges4(4, 0, 0, 0),
            Corners = Radii.ControlAll,                          // ControlCornerRadius 4 (applies visibly when open)
            // Closed: ControlFillColorTransparent, chromeless (:9). Open: AcrylicInAppFillColorDefault + OpenBorder
            // (:10 + the CompactClosed→Open storyboard's discrete Background/OpenBorder.Visibility keyframes).
            Fill = ColorF.Transparent,
            Acrylic = isOpen ? Tok.AcrylicFlyout : null,
            BorderWidth = isOpen ? 1f : 0f,
            BorderColor = isOpen ? Tok.StrokeFlyoutDefault : ColorF.Transparent,
            // The open/close HEIGHT change (compact 48 ↔ labeled 64) animates over the WinUI storyboard timings:
            // open = ControlNormal 250ms, close = ControlFast 167ms, both FastOutSlowIn 0,0,0,1 (:124/:165
            // GeneratedDuration) — via the engine's layout-transition (clip-reveal, compositor-only).
            Animate = new LayoutTransition(
                TransitionChannels.Bounds,
                TransitionDynamics.Tween(Motion.ControlNormal, Easing.FluentPopOpen),
                SizeMode.Reveal,
                ExitDynamics: TransitionDynamics.Tween(Motion.ControlFast, Easing.FluentPopOpen)),
            ClipToBounds = isOpen,
            Children = children.ToArray(),
        };
        // Parts: restyle the bar chrome (acrylic, border, padding…); the open/close tween + the command row always win.
        return Parts.Apply(PartBar, bar) with { Animate = bar.Animate, Children = bar.Children };
    }
}
