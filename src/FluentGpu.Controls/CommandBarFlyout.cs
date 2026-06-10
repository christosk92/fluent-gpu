using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>The kind of a <see cref="AppBarCommand"/> in a <see cref="CommandBarFlyout"/>/<see cref="CommandBar"/>:
/// a plain push button, a checkable toggle (shows a check column + accent pill in the overflow), or a divider row.</summary>
public enum AppBarCommandKind : byte { Button, ToggleButton, Separator }

/// <summary>A single command in a <see cref="CommandBarFlyout"/>/<see cref="CommandBar"/> — the engine analog of
/// WinUI's <c>ICommandBarElement</c> (AppBarButton / AppBarToggleButton / AppBarSeparator). A <see cref="Glyph"/> +
/// <see cref="Label"/> + optional <see cref="Invoke"/> callback. Use <see cref="Separator"/> for a divider in the
/// overflow menu. Optional extras: <see cref="AcceleratorText"/> (the right-aligned KeyboardAcceleratorTextOverride
/// hint), <see cref="Accelerator"/> (a REAL engine chord that invokes the command from anywhere), and
/// <see cref="Flyout"/> (a cascading sub-menu — the secondary row shows the E76C chevron and clicking it opens the
/// sub-menu WITHOUT closing the parent flyout, CommandBarFlyout.cpp:95-108).</summary>
public sealed record AppBarCommand(
    string Glyph,
    string Label,
    Action? Invoke = null,
    AppBarCommandKind Kind = AppBarCommandKind.Button,
    bool IsChecked = false,
    bool Enabled = true)
{
    /// <summary>Right-aligned keyboard-accelerator hint (KeyboardAcceleratorTextLabel), e.g. "Ctrl+S".</summary>
    public string? AcceleratorText { get; init; }
    /// <summary>A real keyboard chord that invokes <see cref="Invoke"/> from anywhere (WinUI KeyboardAccelerator).</summary>
    public KeyAccelerator? Accelerator { get; init; }
    /// <summary>Cascading sub-menu items (secondary commands only): the row gains the E76C SubItemChevron
    /// (CommandBarFlyout_themeresources.xaml:303) and clicking it opens the sub-menu, keeping the parent open.</summary>
    public IReadOnlyList<MenuFlyoutItem>? Flyout { get; init; }

    /// <summary>A divider row for the secondary (overflow) menu (mirrors <c>MenuFlyoutItem.Separator</c>).</summary>
    public static AppBarCommand Separator => new("", "", Kind: AppBarCommandKind.Separator);
}

/// <summary>A WinUI CommandBarFlyout: a trigger button that opens a contextual command toolbar anchored below it.
/// The popup has a horizontal PRIMARY row of icon AppBarButtons plus a trailing "…" More ellipsis toggle, and a
/// vertical SECONDARY overflow menu of labeled rows that the … toggle expands. 1:1 with
/// <c>CommandBarFlyout_themeresources.xaml</c> + <c>CommandBarFlyoutCommandBar.cpp</c>:
/// <list type="bullet">
/// <item>Primary buttons: MinWidth 40 ContentRoot, InnerBorderMargin 2 (:107), 16px icon, subtle fill ramp with the
/// 83ms BrushTransition (:282) on every state swap.</item>
/// <item>Overflow rows: check glyph E73E @12 Margin 15,4,14,4 (:505); 16×16 icon at left Margin 12,0,12,0 (39,0,12,0
/// with toggles); OverflowTextLabel Body 14 Margin 12/39/67,0,12,0 + Padding 0,6,0,7 (:301; TouchInputMode →
/// 0,9,0,11 :464); KeyboardAcceleratorTextLabel Caption 12 Margin 24,0,12,0 TextSecondary ramp (:302); SubItemChevron
/// E76C @12 (:303). CHECKED toggles paint the accent pill (CommandBarFlyoutAppBarButtonBackgroundChecked =
/// AccentFillColorDefault :24, hover Secondary, pressed Tertiary, disabled AccentFillColorDisabled — a disabled
/// checked row keeps the disabled-accent pill and suppresses hover/press feedback).</item>
/// <item>Expand/collapse: the overflow region reveals through a synced clip + translate (OverflowContentRootClipTransform
/// + OverflowContentTransform, generic.xaml:17009/17018; PlayOpenAnimation/PlayCloseAnimation
/// CommandBarFlyoutCommandBar.cpp) — 250ms open / 167ms close on the FastOutSlowIn spline, driven on the region node
/// through the AnimEngine clip channels. The host FlyoutSurface owns the drop shadow for the whole popup (WinUI
/// AddDropShadow/RemoveDropShadow manage the same single overflow shadow, cpp:205/:236).</item>
/// </list>
/// The flyout body returns INNER content only — <see cref="OverlayHost"/>'s FlyoutSurface supplies the acrylic
/// backdrop, 1px stroke, shadow and rounded corners. The popup is WINDOWED (ShouldConstrainToRootBounds=False,
/// generic.xaml:16987).</summary>
public sealed class CommandBarFlyout : Component
{
    public string TriggerLabel = "Commands";
    public IReadOnlyList<AppBarCommand> PrimaryCommands = [];
    public IReadOnlyList<AppBarCommand> SecondaryCommands = [];
    /// <summary>WinUI V2 AlwaysExpanded: keep the overflow menu shown and hide the … More button.</summary>
    public bool AlwaysExpanded = false;
    public FlyoutPlacement Placement = FlyoutPlacement.BottomLeft;

    public static Element Create(
        string triggerLabel,
        IReadOnlyList<AppBarCommand> primaryCommands,
        IReadOnlyList<AppBarCommand> secondaryCommands,
        bool alwaysExpanded = false,
        FlyoutPlacement placement = FlyoutPlacement.BottomLeft)
        => Embed.Comp(() => new CommandBarFlyout
        {
            TriggerLabel = triggerLabel,
            PrimaryCommands = primaryCommands,
            SecondaryCommands = secondaryCommands,
            AlwaysExpanded = alwaysExpanded,
            Placement = placement,
        });

    /// <summary>Back-compat shim: the zero/one-arg form still works, supplying a representative sample command set so
    /// existing call sites (and the gallery) keep compiling.</summary>
    public static Element Create(string triggerLabel = "Commands") => Create(triggerLabel, DefaultPrimary, DefaultSecondary);

    static readonly AppBarCommand[] DefaultPrimary =
    [
        new(Icons.Accept, "Accept"),
        new(Icons.Share, "Share"),
        new(Icons.Tag, "Tag"),
    ];

    static readonly AppBarCommand[] DefaultSecondary =
    [
        new(Icons.Settings, "Settings"),
        AppBarCommand.Separator,
        new(Icons.Accept, "Show grid", Kind: AppBarCommandKind.ToggleButton, IsChecked: true),
    ];

    public override Element Render()
    {
        var svc = UseContext(Overlay.Service);
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        // `expanded` is a SIGNAL (not UseState) so the … toggle re-renders the overflow region granularly inside the
        // overlay body Component without re-opening the popup or bumping the OverlayHost version.
        var expanded = UseSignal(AlwaysExpanded);

        void Toggle()
        {
            if (handle.Value is { IsOpen: true } o) { o.Close(); return; }
            expanded.Value = AlwaysExpanded;   // reset expansion each open (matches WinUI: opens collapsed unless AlwaysExpanded)
            handle.Value = svc.Open(
                () => anchor.Value,
                () => Embed.Comp(() => new CommandBarFlyoutBody
                {
                    Primary = PrimaryCommands,
                    Secondary = SecondaryCommands,
                    AlwaysExpanded = AlwaysExpanded,
                    Expanded = expanded,
                    Close = () => handle.Value?.Close(),
                }),
                Placement,
                // ShouldConstrainToRootBounds=False on the CommandBarFlyout popup (generic.xaml:16987).
                new PopupOptions(Chrome: PopupChrome.Flyout) { ConstrainToRootBounds = false });
        }

        return new BoxEl
        {
            AlignSelf = FlexAlign.Start,
            OnRealized = x => anchor.Value = x,
            OnClick = Toggle,
            Role = AutomationRole.Button,
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Gap = 8f,
            MinHeight = 32f,
            Padding = new Edges4(11, 5, 11, 6),
            Corners = Radii.ControlAll,
            BorderWidth = 1f,
            BorderBrush = Tok.ControlElevationBorder,
            Fill = Tok.FillControlDefault,
            HoverFill = Tok.FillControlSecondary,
            Children =
            [
                new TextEl(TriggerLabel) { Size = 14f, Color = Tok.TextPrimary },
                new TextEl(Icons.ChevronDown) { Size = 10f, Color = Tok.TextSecondary, FontFamily = Theme.IconFont },
            ],
        };
    }
}

/// <summary>The inner content of the CommandBarFlyout popup (NO surface chrome — the host's FlyoutSurface supplies
/// acrylic + 1px stroke + shadow + corner). Its own Component so reading <see cref="Expanded"/> grants the … toggle a
/// granular re-render of the overflow region. Layout mirrors WinUI's CommandBarFlyoutCommandBar: a horizontal
/// PrimaryItemsRoot (Height 40, Margin 3,3,0,3) of icon buttons + a trailing 44px ellipsis MoreButton, then an
/// OverflowRegion of full-width labeled rows whose EXPAND/COLLAPSE runs the synced clip+translate storyboards.</summary>
internal sealed class CommandBarFlyoutBody : Component
{
    public IReadOnlyList<AppBarCommand> Primary = [];
    public IReadOnlyList<AppBarCommand> Secondary = [];
    public bool AlwaysExpanded;
    public Signal<bool> Expanded = new(false);
    public Action Close = () => { };
    /// <summary>WinUI TouchInputMode/GameControllerInputMode: OverflowTextLabel.Padding 0,9,0,11 + check glyph margin
    /// 12,10,12,10 (CommandBarFlyout_themeresources.xaml:464-465) instead of the pointer metrics.</summary>
    public bool TouchInputMode;

    // Dim constants from CommandBarFlyout_themeresources.xaml.
    const float PrimaryRowHeight = 40f;        // PrimaryItemsControl Height
    const float AppBarBtnMinWidth = 40f;       // CommandBarFlyoutAppBarButton ContentRoot MinWidth (:285 MinWidth=40)
    const float MoreButtonWidth = 44f;         // EllipsisButton Width
    const float OverflowMinWidth = 136f;       // CommandBarOverflowPresenter MinWidth
    const float OverflowMaxWidth = 440f;       // CommandBarOverflowPresenter MaxWidth / FlyoutMaxWidth
    const float FlyoutMaxWidth = 440f;         // CommandBarFlyoutCommandBar MaxWidth

    public override Element Render()
    {
        lastService = UseContext(Overlay.Service);          // cascading sub-flyouts open through the same service
        bool expanded = Expanded.Value || AlwaysExpanded;   // SUBSCRIBE: re-renders this body when the … toggle flips
        var collapsing = UseSignal(false);                  // reverse animation in flight; region stays mounted
        var expandSeeded = UseRef(false);                   // open animation seeded for the current expansion
        var overflowNode = UseRef<NodeHandle>(default);
        _overflowNode = overflowNode;
        bool collapsingNow = collapsing.Value;
        bool hasSecondary = Secondary.Count > 0;
        bool showMore = hasSecondary && !AlwaysExpanded;
        bool showOverflow = hasSecondary && (expanded || collapsingNow);

        // ── Expand/collapse storyboards (PlayOpenAnimation/PlayCloseAnimation, CommandBarFlyoutCommandBar.cpp):
        // the overflow region reveals through a clip window SYNCED with a content translate
        // (OverflowContentRootClipTransform + OverflowContentTransform, generic.xaml:17009/:17018) — the content
        // slides down into place while the clip's far edge expands, so nothing paints past the rounded corner.
        // Open = ControlNormal 250ms, close = ControlFast 167ms, FastOutSlowIn 0,0,0,1.
        UseLayoutEffect(() =>
        {
            var scene = Context.Scene;
            var anim = Context.Anim;
            var node = overflowNode.Value;
            if (scene is null || anim is null || node.IsNull || !scene.IsLive(node)) return;
            float h = scene.AbsoluteRect(node).H;
            if (h <= 0f) return;
            if (expanded && !collapsing.Peek() && !expandSeeded.Value)
            {
                expandSeeded.Value = true;
                anim.Animate(node, AnimChannel.TranslateY, -h, 0f, Motion.ControlNormal, Easing.FluentPopOpen);
                anim.Animate(node, AnimChannel.ClipT, h, 0f, Motion.ControlNormal, Easing.FluentPopOpen);
            }
            else if (collapsing.Peek() && expandSeeded.Value)
            {
                expandSeeded.Value = false;
                anim.Animate(node, AnimChannel.TranslateY, 0f, -h, Motion.ControlFast, Easing.FluentPopOpen);
                anim.Animate(node, AnimChannel.ClipT, 0f, h, Motion.ControlFast, Easing.FluentPopOpen);
            }
        }, expanded, collapsingNow);

        void ExpandToggle()
        {
            if (AlwaysExpanded) return;
            if (!Expanded.Peek()) { collapsing.Value = false; Expanded.Value = true; return; }
            if (collapsing.Peek()) return;       // collapse already in flight
            collapsing.Value = true;             // keep the region mounted while the reverse storyboard runs
        }

        void CloseSubFlyout() { if (subHandleLive is { IsOpen: true } s) s.Close(); subHandleLive = null; }

        // ── PrimaryItemsRoot: horizontal row of icon buttons + trailing ellipsis ──────────────────────────────
        var primaryChildren = new List<Element>(Primary.Count + 2);
        foreach (var cmd in Primary)
            primaryChildren.Add(PrimaryButton(cmd));
        primaryChildren.Add(new BoxEl { Grow = 1f });   // spacer pins the … button to the right edge
        if (showMore)
            primaryChildren.Add(MoreButton(ExpandToggle));

        var primaryRow = new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Height = PrimaryRowHeight,
            Margin = new Edges4(3, 3, 0, 3),
            // Joint corner flattens against the overflow when expanded (per-corner via Radii top-only).
            Corners = showOverflow ? Radii.OverlayTop : Radii.OverlayAll,
            Children = primaryChildren.ToArray(),
        };

        if (!showOverflow)
        {
            return new BoxEl
            {
                Direction = 1,
                AlignSelf = FlexAlign.Start,
                MaxWidth = FlyoutMaxWidth,
                Children = [primaryRow],
            };
        }

        // The collapse clock unmounts the region once the 167ms reverse storyboard settles.
        Element? collapseClock = collapsingNow
            ? Embed.Comp(() => new ToolTipClock
            {
                DurationMs = Motion.ControlFast,
                OnElapsed = () => { CloseSubFlyout(); collapsing.Value = false; Expanded.Value = false; },
            }) with { Key = "cbf-collapse" }
            : null;

        var children = new List<Element>(4)
        {
            primaryRow,
            // 1px seam border between the primary row and the overflow region (the CommandBarFlyout joint seam).
            new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault },
            BuildOverflow(),
        };
        if (collapseClock is not null) children.Add(collapseClock);

        return new BoxEl
        {
            Direction = 1,
            AlignSelf = FlexAlign.Start,
            MaxWidth = FlyoutMaxWidth,
            ClipToBounds = true,   // the expand clip+translate must never paint past the popup's rounded corners
            Children = children.ToArray(),
        };
    }

    // ── A single primary (icon-only) AppBarButton — CommandBarFlyoutAppBarButtonStyleBase metrics + the 83ms
    //    BrushTransition on the InnerBorder background (CommandBarFlyout_themeresources.xaml:280-283). ──────────────
    Element PrimaryButton(AppBarCommand cmd)
    {
        var fg = cmd.Enabled ? Tok.TextPrimary : Tok.TextDisabled;
        return new BoxEl
        {
            Direction = 1,
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            MinWidth = AppBarBtnMinWidth,
            MinHeight = PrimaryRowHeight,
            Margin = new Edges4(2, 2, 2, 2),    // CommandBarFlyoutAppBarButtonInnerBorderMargin (:107)
            Corners = Radii.ControlAll,
            HoverFill = cmd.Enabled ? Tok.FillSubtleSecondary : ColorF.Transparent,
            PressedFill = cmd.Enabled ? Tok.FillSubtleTertiary : ColorF.Transparent,
            // AppBarButtonInnerBorder BackgroundTransition = BrushTransition Duration 0:0:0.083 (:282).
            HoverDurationMs = Motion.ControlFaster, PressDurationMs = Motion.ControlFaster,
            HoverEasing = Easing.FluentPopOpen, PressEasing = Easing.FluentPopOpen,
            BrushTransitionMs = Motion.ControlFaster,
            Accelerator = cmd.Accelerator,
            OnClick = cmd.Enabled ? () => Run(cmd) : null,
            HitTestVisible = cmd.Enabled,
            IsEnabled = cmd.Enabled,
            Focusable = cmd.Enabled,
            Role = AutomationRole.Button,
            Children =
            [
                new TextEl(cmd.Glyph) { Size = 16f, Color = fg, PressedColor = cmd.Enabled ? Tok.TextSecondary : fg, DisabledColor = Tok.TextDisabled, FontFamily = Theme.IconFont },
            ],
        };
    }

    // ── The trailing … ellipsis toggle (EllipsisButton: Width 44, glyph E712 @16, inner margin 2,2,6,2). ─────────
    Element MoreButton(Action toggle) => new BoxEl
    {
        Direction = 0,
        AlignItems = FlexAlign.Center,
        Justify = FlexJustify.Center,
        Width = MoreButtonWidth,
        MinHeight = PrimaryRowHeight,
        Margin = new Edges4(2, 2, 6, 2),
        Corners = Radii.ControlAll,
        HoverFill = Tok.FillSubtleSecondary,
        PressedFill = Tok.FillSubtleTertiary,
        HoverDurationMs = Motion.ControlFaster, PressDurationMs = Motion.ControlFaster,
        HoverEasing = Easing.FluentPopOpen, PressEasing = Easing.FluentPopOpen,
        Focusable = true,
        OnClick = toggle,
        Role = AutomationRole.Button,
        Children =
        [
            new TextEl(Icons.More) { Size = 16f, Color = Tok.TextPrimary, PressedColor = Tok.TextSecondary, FontFamily = Theme.IconFont },
        ],
    };

    // ── OverflowRegion: vertical labeled rows (CommandBarOverflowPresenter, ItemsPresenter margin 0,4,0,4). ───────
    Element BuildOverflow()
    {
        bool hasIconColumn = false, hasCheckColumn = false;
        for (int i = 0; i < Secondary.Count; i++)
        {
            var c = Secondary[i];
            if (c.Kind == AppBarCommandKind.Separator) continue;
            if (c.Kind == AppBarCommandKind.ToggleButton) hasCheckColumn = true;
            if (c.Glyph is { Length: > 0 }) hasIconColumn = true;
        }

        var rows = new List<Element>(Secondary.Count);
        foreach (var cmd in Secondary)
            rows.Add(cmd.Kind == AppBarCommandKind.Separator
                ? OverflowSeparator()
                : OverflowRow(cmd, hasIconColumn, hasCheckColumn));

        return new BoxEl
        {
            Direction = 1,
            MinWidth = OverflowMinWidth,
            MaxWidth = OverflowMaxWidth,
            Padding = new Edges4(0, 4, 0, 4),   // OverflowPresenter ItemsPresenter Margin 0,4,0,4 (generic.xaml:17000-17031)
            // Bottom corners stay rounded; the top meets the primary row flush.
            Corners = Radii.OverlayBottom,
            ClipToBounds = true,
            OnRealized = h => { if (_overflowNode is { } r) r.Value = h; },
            Children = rows.ToArray(),
        };
    }

    static Element OverflowSeparator() => AppBarSeparator.Create(overflow: true);

    Element OverflowRow(AppBarCommand cmd, bool hasIconColumn, bool hasCheckColumn)
    {
        bool isToggle = cmd.Kind == AppBarCommandKind.ToggleButton;
        bool isChecked = isToggle && cmd.IsChecked;
        bool enabled = cmd.Enabled;
        bool hasSub = cmd.Flyout is { Count: > 0 };
        // Checked toggle = accent pill + on-accent foreground (CommandBarFlyoutAppBarButtonForegroundChecked =
        // TextOnAccentFillColorPrimary :28; pressed → ...Secondary :30; disabled → ...Disabled :31).
        ColorF fg = isChecked
            ? (enabled ? Tok.TextOnAccentPrimary : Tok.TextOnAccentDisabled)
            : (enabled ? Tok.TextPrimary : Tok.TextDisabled);
        ColorF pressedFg = isChecked ? Tok.TextOnAccentSecondary : Tok.TextSecondary;

        var children = new List<Element>(5);
        // Check glyph: E73E @12, Margin 15,4,14,4 (:505); TouchInputMode → 12,10,12,10 (:465). Painted when checked.
        if (hasCheckColumn)
        {
            Element check = isChecked
                ? new TextEl(Icons.Accept) { Size = 12f, Color = fg, PressedColor = enabled ? pressedFg : fg, FontFamily = Theme.IconFont }
                : new BoxEl { Width = 12f };
            children.Add(new BoxEl
            {
                Margin = TouchInputMode ? new Edges4(12, 10, 12, 10) : new Edges4(15, 4, 14, 4),
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, AlignSelf = FlexAlign.Center,
                Children = [check],
            });
        }
        // Menu icon: 16×16 at left, Margin 12,0,12,0 (the toggle column above supplies the 39 lead when present —
        // OverflowWithToggleButtonsAndMenuIcons ContentViewbox Margin 39,0,12,0 :177).
        if (hasIconColumn)
        {
            Element icon = cmd.Glyph is { Length: > 0 } g
                ? new TextEl(g) { Size = 16f, Color = fg, PressedColor = enabled ? pressedFg : fg, DisabledColor = Tok.TextDisabled, FontFamily = Theme.IconFont }
                : new BoxEl();
            children.Add(new BoxEl
            {
                Width = 16f, Height = 16f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, AlignSelf = FlexAlign.Center,
                Margin = hasCheckColumn ? new Edges4(0, 0, 12, 0) : new Edges4(12, 0, 12, 0),
                Children = [icon],
            });
        }
        // OverflowTextLabel: Body 14, Padding 0,6,0,7 (:301); TouchInputMode → 0,9,0,11 (:243/:464); lead margin 12
        // when no leading columns (12/39/67 ladder — the columns above supply the 39/67 leads).
        children.Add(new BoxEl
        {
            Grow = 1f,
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Padding = TouchInputMode ? new Edges4(0, 9, 0, 11) : new Edges4(0, 6, 0, 7),
            Margin = (hasCheckColumn || hasIconColumn) ? default : new Edges4(12, 0, 12, 0),
            Children = [new TextEl(cmd.Label) { Size = 14f, Color = fg, PressedColor = enabled ? pressedFg : fg, DisabledColor = Tok.TextDisabled, Trim = TextTrim.Clip }],
        });
        // KeyboardAcceleratorTextLabel: Caption 12, right-aligned, Margin 24,0,12,0, TextSecondary→pressed Tertiary
        // ramp (:302 + CommandBarFlyoutAppBarButtonKeyboardTextLabelForeground* :16-18).
        if (cmd.AcceleratorText is { Length: > 0 } acc)
            children.Add(new TextEl(acc)
            {
                Size = 12f, Margin = new Edges4(24, 0, 12, 0), AlignSelf = FlexAlign.Center,
                Color = isChecked ? fg : (enabled ? Tok.TextSecondary : Tok.TextDisabled),
                PressedColor = isChecked ? pressedFg : (enabled ? Tok.TextTertiary : Tok.TextDisabled),
                DisabledColor = Tok.TextDisabled,
            });
        // SubItemChevron: E76C @12, Margin 12,0,12,0, SubItemChevron foreground ramp (:303 + :19-23).
        if (hasSub)
            children.Add(new TextEl(Icons.ChevronRight)
            {
                Size = 12f, FontFamily = Theme.IconFont, Margin = new Edges4(12, 0, 12, 0), AlignSelf = FlexAlign.Center,
                Color = enabled ? Tok.TextSecondary : Tok.TextDisabled,
                PressedColor = enabled ? Tok.TextTertiary : Tok.TextDisabled,
                DisabledColor = Tok.TextDisabled,
            });

        var rowNode = new NodeHandle[1];
        return new BoxEl
        {
            Direction = 0,
            MinHeight = 0f,
            AlignItems = FlexAlign.Center,
            Margin = new Edges4(4, 2, 4, 2),
            Corners = Radii.ControlAll,
            Role = isToggle ? AutomationRole.ToggleButton : AutomationRole.MenuItem,
            // CommandBarFlyoutAppBarButtonBackgroundChecked = AccentFillColorDefault (:24); CheckedPointerOver =
            // AccentFillColorSecondary (:25); CheckedPressed = AccentFillColorTertiary (:26); CheckedDisabled =
            // AccentFillColorDisabled (:27) — a DISABLED checked row keeps the disabled pill and suppresses
            // hover/press feedback (the engine's IsEnabled gate stops hover/press; the resting fill is control-chosen).
            Fill = isChecked ? (enabled ? Tok.AccentDefault : Tok.AccentDisabled) : ColorF.Transparent,
            HoverFill = !enabled ? (isChecked ? Tok.AccentDisabled : ColorF.Transparent)
                       : (isChecked ? Tok.AccentSecondary : Tok.FillSubtleSecondary),
            PressedFill = !enabled ? (isChecked ? Tok.AccentDisabled : ColorF.Transparent)
                        : (isChecked ? Tok.AccentTertiary : Tok.FillSubtleTertiary),
            BrushTransitionMs = Motion.ControlFaster,   // 83ms BrushTransition on the row background (:282)
            HoverDurationMs = Motion.ControlFaster, PressDurationMs = Motion.ControlFaster,
            HoverEasing = Easing.FluentPopOpen, PressEasing = Easing.FluentPopOpen,
            Accelerator = cmd.Accelerator,
            OnClick = enabled ? () => RunSecondary(cmd, rowNode) : null,
            HitTestVisible = enabled,
            IsEnabled = enabled,
            Focusable = enabled,
            OnRealized = h => rowNode[0] = h,
            Children = children.ToArray(),
        };
    }

    void Run(AppBarCommand cmd)
    {
        cmd.Invoke?.Invoke();
        // Toggle commands flip their checked state but keep the flyout open (CommandBarFlyout.cpp:335-340: toggle
        // handlers do not Hide()); plain buttons commit and close.
        if (cmd.Kind == AppBarCommandKind.ToggleButton) return;
        Close();
    }

    // Secondary rows: a command WITH a Flyout opens its cascading sub-menu and must NOT close the parent
    // (CommandBarFlyout.cpp:95-108 — the Hide() call is gated on !button.Flyout()); toggles stay open; buttons close.
    void RunSecondary(AppBarCommand cmd, NodeHandle[] rowNode)
    {
        if (cmd.Flyout is { Count: > 0 } subItems)
        {
            if (subHandleLive is { IsOpen: true } s) { s.Close(); subHandleLive = null; }
            var svc2 = lastService;
            if (svc2 is null) return;
            subHandleLive = svc2.Open(
                () => rowNode[0],
                () => MenuFlyout.Build(subItems, Close),   // invoking a sub item closes the WHOLE chain
                FlyoutPlacement.RightEdgeAlignedTop,
                new PopupOptions(Chrome: PopupChrome.Flyout) { ConstrainToRootBounds = false });
            return;
        }
        Run(cmd);
    }

    // Cascading sub-flyout plumbing + the overflow-region node ref: captured at render time (UseContext/UseRef are
    // render-scoped; instance fields let the row builders — instance methods — reach them).
    IOverlayService? lastService;
    OverlayHandle? subHandleLive;
    Ref<NodeHandle>? _overflowNode;
}
