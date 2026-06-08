using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>The kind of a <see cref="AppBarCommand"/> in a <see cref="CommandBarFlyout"/>: a plain push button, a
/// checkable toggle (shows a check column + accent pill in the overflow), or a divider row.</summary>
public enum AppBarCommandKind : byte { Button, ToggleButton, Separator }

/// <summary>A single command in a <see cref="CommandBarFlyout"/> — the engine analog of WinUI's
/// <c>ICommandBarElement</c> (AppBarButton / AppBarToggleButton / AppBarSeparator). A <see cref="Glyph"/> +
/// <see cref="Label"/> + optional <see cref="Invoke"/> callback. Use <see cref="Separator"/> for a divider in the
/// overflow menu.</summary>
public sealed record AppBarCommand(
    string Glyph,
    string Label,
    Action? Invoke = null,
    AppBarCommandKind Kind = AppBarCommandKind.Button,
    bool IsChecked = false,
    bool Enabled = true)
{
    /// <summary>A divider row for the secondary (overflow) menu (mirrors <c>MenuFlyoutItem.Separator</c>).</summary>
    public static AppBarCommand Separator => new("", "", Kind: AppBarCommandKind.Separator);
}

/// <summary>A WinUI CommandBarFlyout: a trigger button that opens a contextual command toolbar anchored below it.
/// The popup has a horizontal PRIMARY row of icon AppBarButtons (mirrors <see cref="CommandBar"/>) plus a trailing
/// "…" More ellipsis toggle, and a vertical SECONDARY overflow menu of labeled rows (mirrors <c>MenuFlyout.Build</c>:
/// 36px rows, icon column, separators, optional toggle check column + accent pill) that the … toggle expands.
/// The flyout body returns INNER content only — <see cref="OverlayHost"/>'s FlyoutSurface already supplies the
/// acrylic backdrop, 1px stroke, shadow and rounded corners, so we do not re-draw them.</summary>
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
                Placement);
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
/// OverflowRegion of full-width labeled rows.</summary>
internal sealed class CommandBarFlyoutBody : Component
{
    public IReadOnlyList<AppBarCommand> Primary = [];
    public IReadOnlyList<AppBarCommand> Secondary = [];
    public bool AlwaysExpanded;
    public Signal<bool> Expanded = new(false);
    public Action Close = () => { };

    // Dim constants from CommandBarFlyout_themeresources.xaml.
    const float PrimaryRowHeight = 40f;        // PrimaryItemsControl Height
    const float AppBarBtnMinWidth = 40f;       // CommandBarFlyoutAppBarButton ContentRoot MinWidth
    const float MoreButtonWidth = 44f;         // EllipsisButton Width
    const float OverflowMinWidth = 136f;       // CommandBarOverflowPresenter MinWidth
    const float OverflowMaxWidth = 440f;       // CommandBarOverflowPresenter MaxWidth / FlyoutMaxWidth
    const float OverflowRowHeight = 36f;       // OverflowTextLabel line + 6/7 padding ≈ 36
    const float FlyoutMaxWidth = 440f;         // CommandBarFlyoutCommandBar MaxWidth

    public override Element Render()
    {
        bool expanded = Expanded.Value || AlwaysExpanded;   // SUBSCRIBE: re-renders this body when the … toggle flips
        bool hasSecondary = Secondary.Count > 0;
        bool showMore = hasSecondary && !AlwaysExpanded;
        bool showOverflow = hasSecondary && expanded;

        // ── PrimaryItemsRoot: horizontal row of icon buttons + trailing ellipsis ──────────────────────────────
        var primaryChildren = new List<Element>(Primary.Count + 2);
        foreach (var cmd in Primary)
            primaryChildren.Add(PrimaryButton(cmd));
        primaryChildren.Add(new BoxEl { Grow = 1f });   // spacer pins the … button to the right edge
        if (showMore)
            primaryChildren.Add(MoreButton());

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

        return new BoxEl
        {
            Direction = 1,
            AlignSelf = FlexAlign.Start,
            MaxWidth = FlyoutMaxWidth,
            Children =
            [
                primaryRow,
                // 1px seam border between the primary row and the overflow region (approximates WinUI's joint seam).
                new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault },
                BuildOverflow(),
            ],
        };
    }

    // ── A single primary (icon-only) AppBarButton — mirrors AppBarButton.cs / CommandBar.cs primary metrics. ──────
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
            Margin = new Edges4(2, 2, 2, 2),    // AppBarButtonInnerBorderMargin
            Corners = Radii.ControlAll,
            HoverFill = cmd.Enabled ? Tok.FillSubtleSecondary : ColorF.Transparent,
            PressedFill = cmd.Enabled ? Tok.FillSubtleTertiary : ColorF.Transparent,
            OnClick = cmd.Enabled ? () => Run(cmd) : null,
            HitTestVisible = cmd.Enabled,
            Role = AutomationRole.Button,
            Children =
            [
                new TextEl(cmd.Glyph) { Size = 16f, Color = fg, FontFamily = Theme.IconFont },
            ],
        };
    }

    // ── The trailing … ellipsis toggle (EllipsisButton: Width 44, glyph E712 @16, inner margin 2,2,6,2). ─────────
    Element MoreButton() => new BoxEl
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
        OnClick = () => Expanded.Value = !Expanded.Value,
        Role = AutomationRole.Button,
        Children =
        [
            new TextEl(Icons.More) { Size = 16f, Color = Tok.TextPrimary, FontFamily = Theme.IconFont },
        ],
    };

    // ── OverflowRegion: vertical labeled rows — mirrors MenuFlyout.Build (icon column, 36px rows, separators). ────
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
            Padding = new Edges4(0, 3, 0, 3),   // CommandBarOverflowPresenter ItemsPresenter margin
            // Bottom corners stay rounded; the top meets the primary row flush.
            Corners = Radii.OverlayBottom,
            Children = rows.ToArray(),
        };
    }

    static Element OverflowSeparator() => new BoxEl
    {
        Direction = 1,
        Height = 9f,
        Justify = FlexJustify.Center,
        Padding = new Edges4(12, 0, 12, 0),
        Children = [new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault }],
    };

    Element OverflowRow(AppBarCommand cmd, bool hasIconColumn, bool hasCheckColumn)
    {
        bool isToggle = cmd.Kind == AppBarCommandKind.ToggleButton;
        bool isChecked = isToggle && cmd.IsChecked;
        // Checked toggle = accent pill + on-accent foreground (mirrors AppBarToggleButton.cs); else standard text colors.
        var fg = isChecked ? Tok.TextOnAccentPrimary : (cmd.Enabled ? Tok.TextPrimary : Tok.TextDisabled);

        var children = new List<Element>(3);
        if (hasCheckColumn)
        {
            // Check column (E73E @12). The glyph paints only when this toggle is checked.
            Element check = isChecked
                ? new TextEl(Icons.Accept) { Size = 12f, Color = fg, FontFamily = Theme.IconFont }
                : new BoxEl();
            children.Add(new BoxEl { Width = 28f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [check] });
        }
        if (hasIconColumn)
        {
            Element icon = cmd.Glyph is { Length: > 0 } g
                ? new TextEl(g) { Size = 16f, Color = fg, FontFamily = Theme.IconFont }
                : new BoxEl();
            children.Add(new BoxEl { Width = 28f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [icon] });
        }
        children.Add(new TextEl(cmd.Label) { Size = 14f, Color = fg, Grow = 1f });

        return new BoxEl
        {
            Direction = 0,
            Height = OverflowRowHeight,
            AlignItems = FlexAlign.Center,
            Margin = new Edges4(4, 2, 4, 2),
            Padding = new Edges4(12, 0, 12, 0),   // OverflowTextLabel margin 12,0,12,0
            Gap = 4f,
            Corners = Radii.ControlAll,
            Role = isToggle ? AutomationRole.ToggleButton : AutomationRole.MenuItem,
            Fill = isChecked ? Tok.AccentDefault : ColorF.Transparent,
            HoverFill = cmd.Enabled ? (isChecked ? Tok.AccentSecondary : Tok.FillSubtleSecondary) : ColorF.Transparent,
            PressedFill = cmd.Enabled ? (isChecked ? Tok.AccentTertiary : Tok.FillSubtleTertiary) : ColorF.Transparent,
            OnClick = cmd.Enabled ? () => Run(cmd) : null,
            HitTestVisible = cmd.Enabled,
            Children = children.ToArray(),
        };
    }

    void Run(AppBarCommand cmd)
    {
        cmd.Invoke?.Invoke();
        // Toggle commands flip their checked state but keep the menu open (WinUI keeps the overflow open on toggle);
        // plain buttons commit and close. The caller owns the AppBarCommand records, so a toggle's persisted state is
        // re-supplied via the command list on the next open — here we only run the action.
        if (cmd.Kind == AppBarCommandKind.ToggleButton) return;
        Expanded.Value = AlwaysExpanded;
        Close();
    }
}
