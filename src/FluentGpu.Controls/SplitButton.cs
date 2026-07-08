using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;

namespace FluentGpu.Controls;

/// <summary>
/// A WinUI 3 <c>SplitButton</c>: a primary action button joined to a secondary dropdown half, sharing one rounded
/// chrome with a 1px divider between them. The two halves are <b>independently</b> interactive — the primary half runs
/// <see cref="OnInvoke"/> (the <c>Click</c> event / <c>OnClickPrimary</c>), the secondary half opens a
/// <see cref="MenuFlyout"/> anchored below the whole control (<c>OnClickSecondary → OpenFlyout</c>). Each half carries
/// its own hover/press fill + foreground ramp; the inner corner where each half meets the divider is squared so the
/// pair reads as one control (WinUI PrimaryButtonBorder <c>4,0,0,4</c> / SecondaryButtonBorder <c>0,4,4,0</c>).
///
/// Source of truth: <c>microsoft-ui-xaml/controls/dev/SplitButton/SplitButton.xaml</c> (+ <c>_themeresources.xaml</c>,
/// <c>SplitButton.cpp</c>). Tokens, sizes, padding, corners and the state matrix are 1:1 with that template.
/// </summary>
public sealed class SplitButton : Component
{
    // Template parts (the WinUI x:Name vocabulary; see TemplateParts, docs/guide/control-fidelity.md §6). Each part's
    // doc lists the props the control OWNS (re-asserted after any modifier — a Parts customization cannot win those).
    /// <summary>The joined chrome (WinUI RootGrid + Border): fill, border, corners, clip. Owned: OnKeyDown (the
    /// Space/Enter/Down/F4 chords), OnRealized (flyout anchor capture, chained), Children.</summary>
    public const string PartRoot = "Root";
    /// <summary>The primary half (WinUI PrimaryButton). Owned: OnClick (<see cref="OnInvoke"/>), Role,
    /// Children (the <see cref="PrimaryContent"/> slot).</summary>
    public const string PartPrimaryButton = "PrimaryButton";
    /// <summary>The dropdown half (WinUI SecondaryButton). Owned: OnClick (the flyout toggle), Role, Children.</summary>
    public const string PartSecondaryButton = "SecondaryButton";
    /// <summary>The 1px divider between the halves (WinUI DividerBackgroundGrid). Owned: none.</summary>
    public const string PartDivider = "Divider";
    /// <summary>The 12×12 chevron box inside the dropdown half (the AnimatedIcon column). Owned: none.</summary>
    public const string PartChevron = "Chevron";

    // ── WinUI geometry (SplitButton.xaml / _themeresources.xaml) ──
    public const float PrimaryButtonMinWidth = 35f;   // SplitButtonPrimaryButtonSize (PrimaryButtonColumn MinWidth)
    public const float SecondaryButtonSize   = 35f;    // SplitButtonSecondaryButtonSize (SecondaryButtonColumn Width)
    public const float ControlHeight         = 32f;    // effective SplitButton height
    public const float DividerWidth          = 1f;     // Separator column
    public const float DividerHeight         = ControlHeight; // DividerBackgroundGrid stretches the full control height
    public const float FontSize              = 14f;    // ControlContentThemeFontSize
    public const float ChevronSize           = 12f;    // AnimatedChevronDownSmall is 12x12
    public const float ChevronGlyphSize      = 8f;     // fallback FontIconSource FontSize
    public const float CornerRadius          = Radii.Control;   // ControlCornerRadius = 4
    static readonly Edges4 PrimaryPadding    = new(11, 6, 11, 7);   // SplitButtonPadding
    static readonly Edges4 SecondaryPadding  = new(0, 0, 12, 0);    // SecondaryButton Padding="0,0,12,0"

    public string Label = "";
    public string? Glyph;
    public Element? PrimaryContent;
    public Action? OnInvoke;
    public IReadOnlyList<MenuFlyoutItem> Items = [];
    public bool IsEnabled = true;
    public bool OpenOnMount;   // deterministic visual-shot hook: open the real popup after first mount
    /// <summary>Lightweight per-part styling (CSS ::part): modifiers keyed by the <c>PartXxx</c> consts; see
    /// <see cref="TemplateParts"/> for the contract.</summary>
    public TemplateParts? Parts;

    public static Element Create(string label, Action onInvoke, IReadOnlyList<MenuFlyoutItem> items, string? glyph = null, bool isEnabled = true)
        => Embed.Comp(() => new SplitButton { Label = label, OnInvoke = onInvoke, Items = items, Glyph = glyph, IsEnabled = isEnabled });

    public static Element Create(Element primaryContent, Action onInvoke, IReadOnlyList<MenuFlyoutItem> items, bool isEnabled = true)
        => Embed.Comp(() => new SplitButton { PrimaryContent = primaryContent, OnInvoke = onInvoke, Items = items, IsEnabled = isEnabled });

    // Frozen-props tripwire (ReuseGuard): Label/Glyph/IsEnabled freeze at mount (PrimaryContent/Items are element/list
    // slots — deliver those via a provider). A reused instance whose scalar caller-data changed is the frozen-props bug.
    public override bool ChecksReuse => ReuseGuard.CompiledIn;
    public override void DebugCheckReuse(Component next)
    {
        if (next is not SplitButton n) return;
        if (n.Label != Label) ReuseGuard.ScalarChanged(this, nameof(Label));
        else if (n.Glyph != Glyph) ReuseGuard.ScalarChanged(this, nameof(Glyph));
        else if (n.IsEnabled != IsEnabled) ReuseGuard.ScalarChanged(this, nameof(IsEnabled));
    }

    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var open = UseSignal(false);
        var autoOpened = UseRef(false);
        var svc = UseContext(Overlay.Service);
        bool enabled = IsEnabled;
        bool menuOpen = open.Value;

        // OnClickSecondary → OpenFlyout (BottomEdgeAlignedLeft); re-click closes (toggle). Focus is captured/restored
        // by the overlay host; the menu is anchored to the WHOLE control (WinUI ShowAt(*this)).
        void ToggleMenu()
        {
            if (handle.Value is { IsOpen: true } h) { h.Close(); return; }
            handle.Value = svc.Open(
                () => anchor.Value,
                () => MenuFlyout.Build(Items, () => handle.Value?.Close()),
                FlyoutPlacement.BottomLeft,
                // WinUI menus are windowed popups (FlyoutBase SetIsWindowedPopup) — may escape the window.
                new PopupOptions(FocusTrap: true) { ConstrainToRootBounds = false });
            handle.Value.ClosedAction = () => { handle.Value = null; open.Value = false; };
            open.Value = true;
        }

        UseEffect(() =>
        {
            if (!OpenOnMount || autoOpened.Value) return;
            autoOpened.Value = true;
            ToggleMenu();
        }, OpenOnMount);

        // Keyboard (SplitButton.cpp OnSplitButtonKeyUp): Space/Enter → invoke the primary action; Down / Alt+Down /
        // F4 → open the menu (the ComboBox-family open chords; Alt+Down arrives as Down with the Alt modifier).
        void OnKey(KeyEventArgs e)
        {
            if (!enabled) return;
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
            {
                OnInvoke?.Invoke();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Down || e.KeyCode == Keys.F4)
            {
                ToggleMenu();
                e.Handled = true;
            }
        }

        // ── Primary half content ──
        Element[] primaryChildren;
        if (PrimaryContent is { } custom)
        {
            primaryChildren = [custom];
        }
        else
        {
            // Primary foreground ramp: rest TextPrimary; PointerOver TextPrimary; Pressed SplitButtonForegroundPressed =
            // TextSecondary; Disabled SplitButtonForegroundDisabled = TextDisabled. (FlyoutOpen also dims to TextSecondary,
            // handled via the box pressed fill — the label press ramp covers the visible foreground change.)
            var label = new TextEl(Label)
            {
                Size = FontSize, Color = menuOpen ? Tok.TextSecondary : Tok.TextPrimary,
                PressedColor = Tok.TextSecondary, DisabledColor = Tok.TextDisabled,
            };
            primaryChildren = Glyph is { Length: > 0 } g
                ? [new TextEl(g)
                    {
                        Size = FontSize, Color = menuOpen ? Tok.TextSecondary : Tok.TextPrimary,
                        PressedColor = Tok.TextSecondary, DisabledColor = Tok.TextDisabled,
                        FontFamily = Theme.IconFont,
                    }, label]
                : [label];
        }

        // The actual buttons in the WinUI template have BorderThickness=0 and no corner radius; the rounded border is
        // drawn by overlay borders on top of the shared root chrome. Keep half backgrounds square and clip them by the
        // root so hover/press cannot create extra rounded lobes at the divider.
        var primary = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = 8f,
            Grow = 1f, MinWidth = PrimaryButtonMinWidth, Height = ControlHeight,
            Padding = PrimaryPadding,
            // Each half owns its own interaction ramp → hovering the primary leaves the secondary at rest, exactly as
            // PrimaryPointerOver keeps SecondaryBackgroundGrid at SplitButtonBackground (and vice versa).
            Fill = menuOpen ? Tok.FillControlTertiary : ColorF.Transparent,
            HoverFill = Tok.FillControlSecondary,    // SplitButtonBackgroundPointerOver
            PressedFill = Tok.FillControlTertiary,   // SplitButtonBackgroundPressed
            HoverDurationMs = Motion.ControlFaster, PressDurationMs = Motion.ControlFaster,
            HoverEasing = Easing.FluentPopOpen, PressEasing = Easing.FluentPopOpen,   // ControlFastOutSlowInKeySpline = 0,0,0,1
            Role = AutomationRole.Button, IsEnabled = enabled, OnClick = OnInvoke,
            Children = primaryChildren,
        };
        // Parts: restyle anything; the invoke mechanics and the primary content slot always win.
        primary = Parts.Apply(PartPrimaryButton, primary) with { OnClick = OnInvoke, Role = AutomationRole.Button, Children = primary.Children };

        // DividerBackgroundGrid: 1px, SplitButtonBorderBrushDivider = ControlStrokeColorDefaultBrush = StrokeControlDefault.
        var divider = Parts.Apply(PartDivider, new BoxEl
        {
            Width = DividerWidth, Height = DividerHeight, AlignSelf = FlexAlign.Stretch,
            Fill = enabled ? Tok.StrokeControlDefault : ColorF.Transparent,
        });

        // Secondary half: chevron, foreground SplitButtonForegroundSecondary = TextSecondary; PointerOver → TextPrimary
        // (SplitButtonForegroundPointerOver), Pressed → SplitButtonForegroundSecondaryPressed = TextTertiary.
        var drop = new BoxEl
        {
            Direction = 0, Width = SecondaryButtonSize, Height = ControlHeight,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.End,
            Padding = SecondaryPadding,
            Fill = menuOpen ? Tok.FillControlTertiary : ColorF.Transparent,
            HoverFill = Tok.FillControlSecondary,    // SplitButtonBackgroundPointerOver
            PressedFill = Tok.FillControlTertiary,   // SplitButtonBackgroundPressed
            HoverDurationMs = Motion.ControlFaster, PressDurationMs = Motion.ControlFaster,
            HoverEasing = Easing.FluentPopOpen, PressEasing = Easing.FluentPopOpen,
            Role = AutomationRole.Button, IsEnabled = enabled, OnClick = ToggleMenu,
            Children =
            [
                Parts.Apply(PartChevron, new BoxEl
                {
                    Width = ChevronSize, Height = ChevronSize, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children =
                    [
                        new TextEl(Icons.ChevronDownSmall)
                        {
                            Size = ChevronGlyphSize, Color = menuOpen ? Tok.TextTertiary : Tok.TextSecondary,
                            HoverColor = Tok.TextPrimary, PressedColor = Tok.TextTertiary, DisabledColor = Tok.TextDisabled,
                            FontFamily = Theme.IconFont,
                        },
                    ],
                }),
            ],
        };
        // Parts: restyle anything; the flyout-toggle mechanics and the chevron mount always win.
        drop = Parts.Apply(PartSecondaryButton, drop) with { OnClick = ToggleMenu, Role = AutomationRole.Button, Children = drop.Children };

        // ── Joined chrome (RootGrid + Border) ──
        // Background = SplitButtonBackground = ControlFillColorDefaultBrush; BorderBrush = ControlElevationBorderBrush;
        // BorderThickness = 1; CornerRadius = ControlCornerRadius (4). Disabled: transparent fill + StrokeControlDefault
        // border (SplitButtonBorderBrushDisabled), per the Disabled VisualState.
        Action<NodeHandle> anchorCapture = h => anchor.Value = h;
        var root = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center,
            // SplitButtonStyle sets HorizontalAlignment="Left" (SplitButton.xaml:8): the control HUGS its content in
            // a stretch container instead of growing to the cross-axis width.
            AlignSelf = FlexAlign.Start,
            MinHeight = ControlHeight,
            Fill = enabled ? Tok.FillControlDefault : Tok.FillControlDisabled,
            BorderWidth = 1f,
            BorderBrush = enabled
                ? (menuOpen ? GradientSpec.Solid(Tok.StrokeControlDefault) : Tok.ControlElevationBorder)
                : GradientSpec.Solid(Tok.StrokeControlDefault),
            Corners = Radii.ControlAll,
            ClipToBounds = true,
            // The outer node is the joined chrome + keyboard/focus host (WinUI RootGrid). The two HALVES carry the
            // Button a11y role (PrimaryButton / SecondaryButton); the chrome itself is not a separate button role.
            Focusable = enabled, IsEnabled = enabled,
            OnKeyDown = OnKey,
            OnRealized = anchorCapture,
            Children = [primary, divider, drop],
        };
        // Parts: restyle the joined chrome; the keyboard/focus/anchor mechanics and the half mounts always win.
        var rootM = Parts.Apply(PartRoot, root);
        return rootM with
        {
            Focusable = enabled, IsEnabled = enabled,
            OnKeyDown = OnKey,
            OnRealized = TemplateParts.Chain(anchorCapture, rootM.OnRealized),
            Children = root.Children,
        };
    }
}
