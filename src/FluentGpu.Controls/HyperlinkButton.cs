using FluentGpu.Foundation;
using FluentGpu.Dsl;
using FluentGpu.Hooks;

namespace FluentGpu.Controls;

/// <summary>
/// A WinUI HyperlinkButton: accent-colored link text with a subtle hover/press surface and a HAND cursor (WinUI sets
/// MouseCursorHand at initialize — dxaml\xcp\dxaml\lib\HyperLinkButton_Partial.cpp:28-34). Raises a click (in-app
/// navigation); pass <c>navigateUri</c> to additionally launch the OS default handler after Click, exactly WinUI's
/// OnClick order (Click event first, then Launcher::TryInvokeLauncher — HyperLinkButton_Partial.cpp:149-177) via the
/// <c>IPlatformApp.OpenUri</c> PAL seam (host-wired through <see cref="InputHooks.OpenUri"/>; headless records).
/// Underline: WinUI 3 sets the <c>HyperlinkUnderlineVisible</c> resource to FALSE
/// (controls\dev\CommonStyles\Hyperlink_themeresources.xaml:20), and HyperlinkButton only underlines when that
/// directive is true OR a HighContrast theme is active (HyperLinkButton_Partial.cpp:207-212) — so the WinUI 3 default
/// is NO underline; <see cref="Style.UnderlineVisible"/> is the opt-in (the E8 HighContrast pass forces it on).
/// Style source: controls\dev\CommonStyles\HyperlinkButton_themeresources.xaml (Default = dark :4-18, Light :19-33).
/// </summary>
public static partial class HyperlinkButton
{
    // Template parts (see TemplateParts; docs/guide/control-fidelity.md §6). Each part's doc lists the props the
    // control OWNS (re-asserted after any modifier — a Parts customization cannot win those).
    /// <summary>The link surface. Owned: OnClick (Click → OpenUri order), Role, Children (the label slot).</summary>
    public const string PartRoot = "Root";
    /// <summary>The link text — a <see cref="TextEl"/>, so customize via <c>parts.Set&lt;TextEl&gt;(HyperlinkButton.PartLabel, …)</c>.
    /// Owned: none (the accent foreground ramp and the Underline opt-in are style-driven; a modifier may override them).</summary>
    public const string PartLabel = "Label";

    public sealed record Style
    {
        public float FontSize { get; init; } = 14f;   // ControlContentThemeFontSize (HyperlinkButton_themeresources.xaml:61)
        // WinUI HyperlinkButtonForeground{,PointerOver,Pressed,Disabled} = AccentTextFillColor{Primary,Secondary,Tertiary,Disabled}
        // (HyperlinkButton_themeresources.xaml:5-8 Default / :20-23 Light). Foreground (rest) drives the visible text;
        // the hover/pressed values are the eased TextEl ramps and ForegroundDisabled is used when IsEnabled=false.
        public ColorF Foreground { get; init; }
        public ColorF ForegroundHover { get; init; }
        public ColorF ForegroundPressed { get; init; }
        public ColorF ForegroundDisabled { get; init; }
        // Background: HyperlinkButtonBackground{,PointerOver,Pressed,Disabled} = SubtleFillColor{Transparent,Secondary,Tertiary,Disabled}
        // (HyperlinkButton_themeresources.xaml:9-12 / :24-27).
        public ColorF BackgroundRest { get; init; }
        public ColorF HoverFill { get; init; }
        public ColorF PressedFill { get; init; }
        public ColorF DisabledFill { get; init; }
        public Edges4 Padding { get; init; } = new(11, 5, 11, 6);   // Padding = ButtonPadding (HyperlinkButton_themeresources.xaml:57 → Button_themeresources.xaml:152)
        /// <summary>WinUI FocusVisualMargin = −3 (HyperlinkButton_themeresources.xaml:63); the engine draws the ring (E1).</summary>
        public Edges4 FocusVisualMargin { get; init; } = Edges4.All(-3f);
        /// <summary>WinUI ContentPresenter.BackgroundTransition = BrushTransition 83ms (HyperlinkButton_themeresources.xaml:69-71). NaN = snap.</summary>
        public float BrushTransitionMs { get; init; } = 83f;
        /// <summary>Text underline opt-in. Default FALSE = the WinUI 3 default (<c>HyperlinkUnderlineVisible</c> = False,
        /// Hyperlink_themeresources.xaml:20; underline otherwise only under HighContrast — HyperLinkButton_Partial.cpp:207-212).
        /// When true the label sets <c>TextEl.Underline</c> — the E9 production path: the recorder emits the face-metric
        /// underline bar (DWrite underline position/thickness cached at measure) in the label's eased foreground.
        /// The E8 HighContrast pass switches this on structurally.</summary>
        public bool UnderlineVisible { get; init; }
    }

    public static Style? StyleOverride;
    public static Style DefaultStyle => StyleOverride ?? new Style
    {
        // WinUI HyperlinkButtonForeground = AccentTextFillColor{Primary,Secondary,Tertiary} — the accent TEXT palette
        // (solid, readability-adjusted), NOT the accent FILL: dark = SystemAccentColorLight3/Light3/Light2, light =
        // SystemAccentColorDark2/Dark3/Dark1 (Common_themeresources_any.xaml:93-95 dark / :297-299 light). In dark,
        // Primary==Secondary (only the fill changes on hover); Tertiary (pressed) is one step deeper.
        // Disabled = AccentTextFillColorDisabled = #5DFFFFFF dark / #5C000000 light (Common_themeresources_any.xaml:10/214)
        // — value-identical to TextFillColorDisabled, so Tok.TextDisabled is the exact color in both themes.
        Foreground = Tok.AccentTextPrimary, ForegroundHover = Tok.AccentTextSecondary,
        ForegroundPressed = Tok.AccentTextTertiary, ForegroundDisabled = Tok.TextDisabled,
        // SubtleFillColorDisabled = #00FFFFFF in BOTH themes (Common_themeresources_any.xaml:28/232) → transparent.
        BackgroundRest = Tok.FillSubtleTransparent, HoverFill = Tok.FillSubtleSecondary,
        PressedFill = Tok.FillSubtleTertiary, DisabledFill = Tok.FillSubtleTransparent,
    };

    /// <summary>The per-control clamp seam (adjustment #6 — the axes a control can refuse, Radix precedent).
    /// HyperlinkButton REFUSES the <see cref="ButtonAppearance"/> axis entirely: it is accent link text with no fill
    /// chrome, so Subtle/Outline (which are fill/border ramps) are meaningless — the parameter is simply not exposed.
    /// It DOES adopt the <see cref="ControlSize"/> axis, mapping it to the text size + padding (Medium unchanged).</summary>
    internal static ControlSize ClampSize(ControlSize size) => size;

    /// <summary>Resolve the effective style: an explicit style wins; else the size axis maps to the link's text size +
    /// padding via the shared <see cref="ControlMetrics"/> (Medium byte-identical to the pre-axis default).</summary>
    static Style? Sized(Style? style, ControlSize size)
    {
        if (style is not null) return style;
        var cs = ClampSize(size);
        if (cs == ControlSize.Medium) return null;   // null ⇒ Build uses DefaultStyle unchanged
        var m = ControlMetrics.For(cs);
        return DefaultStyle with { FontSize = m.FontSize, Padding = m.Padding };
    }

    /// <summary>A link that raises <paramref name="onClick"/> (in-app navigation).</summary>
    public static BoxEl Create(string text, Action onClick, Style? style = null, bool isEnabled = true, TemplateParts? parts = null, ControlSize size = ControlSize.Medium)
        => Build(text, onClick, Sized(style, size), isEnabled, parts);

    /// <summary>A link with a WinUI <c>NavigateUri</c>: raises Click first, then launches <paramref name="navigateUri"/>
    /// in the OS default handler (browser/mail) — WinUI's exact OnClick order (Click → Launcher::TryInvokeLauncher,
    /// HyperLinkButton_Partial.cpp:166-173) through the <c>IPlatformApp.OpenUri</c> PAL seam. Headless hosts record the
    /// URI instead of launching (HeadlessPlatformApp.OpenedUris).</summary>
    public static BoxEl Create(string text, string navigateUri, Style? style = null, bool isEnabled = true, Action? onClick = null, TemplateParts? parts = null, ControlSize size = ControlSize.Medium)
        => Build(text, () =>
        {
            onClick?.Invoke();
            // Host-wired to IPlatformApp.OpenUri in the AppHost ctor: the host mirrors the delegate onto the
            // InputHooks.Current channel-DEFAULT instance (static factories have no component scope → no UseContext,
            // so they reach the seam via the default). Null until a host exists — building elements never launches.
            InputHooks.Current.Default.OpenUri?.Invoke(navigateUri);
        }, Sized(style, size), isEnabled, parts);

    private static BoxEl Build(string text, Action onClick, Style? style, bool isEnabled, TemplateParts? parts)
    {
        var s = style ?? DefaultStyle;
        var label = parts.Apply(PartLabel, new TextEl(text)
        {
            // P2 foreground ramp: rest → hover → pressed; the disabled step applies via the ancestor's IsEnabled gate.
            Size = s.FontSize, Color = s.Foreground,
            HoverColor = s.ForegroundHover, PressedColor = s.ForegroundPressed,
            DisabledColor = s.ForegroundDisabled,
            // Underline opt-in: the E9 face-metric decoration bar (DWrite underlinePosition/underlineThickness via
            // TextMeasureCache), riding the same hover/press easing as the label — WinUI gates it on
            // HyperlinkUnderlineVisible / HighContrast (HyperLinkButton_Partial.cpp:207-212).
            Underline = s.UnderlineVisible,
        });
        var root = new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Padding = s.Padding,
            Corners = Radii.ControlAll,   // CornerRadius = ControlCornerRadius (HyperlinkButton_themeresources.xaml:64)
            Role = AutomationRole.Hyperlink,
            Fill = isEnabled ? s.BackgroundRest : s.DisabledFill,   // SubtleFillColorTransparent rest / disabled (both transparent)
            HoverFill = s.HoverFill,
            PressedFill = s.PressedFill,
            // HyperlinkButtonBorderBrush* = SubtleFillColorTransparent in ALL states (HyperlinkButton_themeresources.xaml:13-16)
            // → no border drawn (BorderWidth 0). (WinUI keeps a 1px transparent BorderThickness purely for layout; with
            // BackgroundSizing=OuterBorderEdge — line 53 — the fill covers it, so omitting the border is pixel-identical.)
            // WinUI ContentPresenter.BackgroundTransition 83ms (lines 69-71) — E3 primitive.
            BrushTransitionMs = s.BrushTransitionMs,
            // WinUI UseSystemFocusVisuals=True + FocusVisualMargin −3 (HyperlinkButton_themeresources.xaml:62-63).
            Focusable = true,
            FocusVisualMargin = s.FocusVisualMargin,
            // The ONE WinUI control that shows the hand: SetCursor(MouseCursorHand) at initialize
            // (HyperLinkButton_Partial.cpp:32). Explicit so it stays correct if the engine's clickable default changes.
            Cursor = CursorId.Hand,
            IsEnabled = isEnabled,
            OnClick = onClick,
            Children = [label],
        };
        // Parts: restyle anything (fills, padding, even the Hand cursor); the Click→OpenUri order and the label slot win.
        return parts.Apply(PartRoot, root) with { OnClick = onClick, Role = AutomationRole.Hyperlink, Children = root.Children };
    }
}
