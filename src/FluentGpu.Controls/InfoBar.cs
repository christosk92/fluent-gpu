using FluentGpu.Foundation;
using FluentGpu.Dsl;

namespace FluentGpu.Controls;

/// <summary>The status level of an <see cref="InfoBar"/>, which selects its standard icon glyph and severity color
/// (WinUI InfoBarSeverity). Default is <see cref="Informational"/>.</summary>
public enum InfoBarSeverity : byte { Informational = 0, Success = 1, Warning = 2, Error = 3 }

/// <summary>Why an <see cref="InfoBar"/> closed (WinUI InfoBarCloseReason): the user clicked the X
/// (<see cref="CloseButton"/>) or it was closed in code / by the host (<see cref="Programmatic"/>).</summary>
public enum InfoBarCloseReason : byte { CloseButton = 0, Programmatic = 1 }

/// <summary>Closing event payload (WinUI InfoBarClosingEventArgs). Set <see cref="Cancel"/> = true to veto the close
/// (WinUI then reverts IsOpen back to true); the bar stays open and no Closed fires.</summary>
public sealed class InfoBarClosingEventArgs
{
    public InfoBarCloseReason Reason { get; }
    public bool Cancel { get; set; }
    internal InfoBarClosingEventArgs(InfoBarCloseReason reason) => Reason = reason;
}

/// <summary>Closed event payload (WinUI InfoBarClosedEventArgs).</summary>
public sealed class InfoBarClosedEventArgs
{
    public InfoBarCloseReason Reason { get; }
    internal InfoBarClosedEventArgs(InfoBarCloseReason reason) => Reason = reason;
}

/// <summary>
/// A WinUI InfoBar: an inline, app-wide status surface. A severity icon (a filled circle glyph with an inverse-colored
/// status glyph on top), a Title (SemiBold) + Message content panel (laid out horizontally when it fits on one line,
/// vertically otherwise, like WinUI's InfoBarPanel), an optional trailing action button, and an optional close button.
/// The background is a subtle severity tint over a card stroke. Controlled by <c>isOpen</c>.
/// </summary>
public static class InfoBar
{
    // Template parts (the WinUI x:Name vocabulary; see TemplateParts). Each part's doc lists the props the control
    // OWNS (re-asserted after any modifier — a Parts customization cannot win those).
    /// <summary>The severity-tinted bar root. Owned: Children (the icon / panel / close columns — conditional
    /// mounts), Role. The severity tint/stroke are stock per-render styling a modifier may override.</summary>
    public const string PartRoot = "Root";
    /// <summary>The 16x16 standard severity icon Z-stack (WinUI StandardIcon area). Owned: none — the per-severity
    /// glyph colors are recomputed per render in the stock build (override-able).</summary>
    public const string PartIcon = "Icon";
    /// <summary>The SemiBold title (WinUI Title). A TextEl part — restyle via
    /// <c>parts.Set&lt;TextEl&gt;(InfoBar.PartTitle, t =&gt; t with { … })</c>. Owned: none.</summary>
    public const string PartTitle = "Title";
    /// <summary>The message body (WinUI Message). A TextEl part — restyle via
    /// <c>parts.Set&lt;TextEl&gt;(InfoBar.PartMessage, t =&gt; t with { … })</c>. Owned: none.</summary>
    public const string PartMessage = "Message";
    /// <summary>The trailing 38x38 X button (WinUI CloseButton). Owned: OnClick (the Closing→onClose→Closed lifecycle
    /// chain — kept whenever the control has close handlers; a modifier OnClick survives only when it has none), Role.</summary>
    public const string PartCloseButton = "CloseButton";

    // WinUI InfoBar_themeresources.xaml:70-74 standard icon glyphs (Segoe Fluent Icons / SymbolThemeFontFamily),
    // single-sourced from Icons.cs as VISIBLE \uXXXX escapes (raw PUA literals read as "empty" in most viewers —
    // the audit's blocker — so the named constants are the canonical spelling).
    private const string IconBackgroundGlyph = Icons.InfoBarBackgroundCircle; // F136 (InfoBar_themeresources.xaml:70)
    // The per-severity status glyphs (F13F/F13E/F13C/F13D) + severity color ramp live in the shared SeverityVisuals
    // helper (single-sourced with Toast; see InfoBarTemplateSettings.For).

    // WinUI sizes/margins (InfoBar_themeresources.xaml, verbatim).
    private const float MinHeight        = 48f;          // InfoBarMinHeight
    private const float IconFontSize     = 16f;          // InfoBarIconFontSize
    private const float TitleFontSize    = 14f;          // InfoBarTitleFontSize  (FontWeight SemiBold)
    private const float MessageFontSize  = 14f;          // InfoBarMessageFontSize (FontWeight Normal)
    private const float CloseButtonSize  = 38f;          // InfoBarCloseButtonSize
    private const float CloseGlyphSize   = 16f;          // InfoBarCloseButtonGlyphSize

    private static readonly Edges4 ContentRootPadding = new(16f, 0f, 0f, 0f);   // InfoBarContentRootPadding 16,0,0,0
    private static readonly Edges4 IconMargin         = new(0f, 16f, 14f, 16f); // InfoBarIconMargin 0,16,14,16
    private static readonly Edges4 PanelMargin        = new(0f, 0f, 16f, 0f);   // InfoBarPanelMargin 0,0,16,0
    private static readonly Edges4 CloseButtonMargin  = new(5f, 5f, 5f, 5f);    // InfoBarCloseButtonStyle Margin 5

    // InfoBarPanel orientation paddings/margins (InfoBar_themeresources.xaml, verbatim). The panel ignores the first
    // child's leading orientation margin (Measure: "nItems > 0" gate; Arrange: "hasPreviousElement" gate).
    private static readonly Edges4 PanelHorizontalPadding = new(0f, 0f, 0f, 0f);  // InfoBarPanelHorizontalOrientationPadding
    private static readonly Edges4 PanelVerticalPadding   = new(0f, 14f, 0f, 18f); // InfoBarPanelVerticalOrientationPadding
    // Horizontal-orientation per-child margins.
    private static readonly Edges4 TitleHMargin   = new(0f, 14f, 0f, 0f);         // InfoBarTitleHorizontalOrientationMargin
    private static readonly Edges4 MessageHMargin = new(12f, 14f, 0f, 0f);        // InfoBarMessageHorizontalOrientationMargin
    private static readonly Edges4 ActionHMargin  = new(16f, 8f, 0f, 0f);         // InfoBarActionHorizontalOrientationMargin
    // Vertical-orientation per-child margins.
    private static readonly Edges4 TitleVMargin   = new(0f, 14f, 0f, 0f);         // InfoBarTitleVerticalOrientationMargin
    private static readonly Edges4 MessageVMargin = new(0f, 4f, 0f, 0f);          // InfoBarMessageVerticalOrientationMargin
    private static readonly Edges4 ActionVMargin  = new(0f, 12f, 0f, 0f);         // InfoBarActionVerticalOrientationMargin

    // InfoBar's IMPLICIT HyperlinkButton style (scoped to the action ContentPresenter, InfoBar.xaml:117-122):
    // Margin = InfoBarHyperlinkButtonMargin = -12,0,0,0 (InfoBar_themeresources.xaml:69) — pulls the link's ~11px
    // internal padding back so its text lines up with the title/message column. The style's other setter, Foreground =
    // InfoBarHyperlinkButtonForeground = AccentTextFillColorPrimaryBrush (InfoBar_themeresources.xaml:19 dark / :38
    // light), is value-identical to the stock HyperlinkButton Foreground (Tok.AccentTextPrimary) so only the margin
    // needs re-asserting here. The own-margin COMBINES with the panel's per-child orientation margin (WinUI applies
    // both: the panel offsets by the attached orientation margin, the element keeps its own Margin).
    private const float HyperlinkActionMarginLeft = -12f;

    /// <summary>The TemplateSettings convention (per the audit's P3) - the per-severity computed geometry WinUI binds
    /// into its severity VisualState: the standard status glyph, the icon-background fill (SystemFillColor*Brush), the
    /// inverse glyph foreground, and the tinted content-root background (SystemFillColor*BackgroundBrush). Computed once
    /// per severity by the pure factory <see cref="For"/> - no per-frame allocation.</summary>
    public readonly record struct InfoBarTemplateSettings(string Glyph, ColorF IconBackground, ColorF IconForeground, ColorF Background)
    {
        // Single-sourced from the shared SeverityVisuals helper so InfoBar and Toast can NEVER drift (gate.toast.severity).
        public static InfoBarTemplateSettings For(InfoBarSeverity severity)
        {
            var v = SeverityVisuals.For(severity);
            return new(v.Glyph, v.IconBackground, v.IconForeground, v.Background);
        }
    }

    /// <param name="severity">Selects the status glyph + severity colors via <see cref="InfoBarTemplateSettings"/>.</param>
    /// <param name="title">SemiBold title line (WinUI InfoBarTitleFontWeight). Empty => omitted.</param>
    /// <param name="message">Regular message body. Empty => omitted.</param>
    /// <param name="onClose">Invoked when the X is clicked (WinUI sets reason=CloseButton then IsOpen=false). Pair with
    /// <paramref name="onClosing"/>/<paramref name="onClosed"/> for the full lifecycle.</param>
    /// <param name="isOpen">Whether the bar is shown. WinUI's IsOpen defaults to <c>false</c>; this stateless factory
    /// defaults to <c>true</c> so a constructed bar renders (callers gate visibility with their own signal/state).</param>
    /// <param name="isClosable">When true (the WinUI default) the trailing 38x38 close button is shown.</param>
    /// <param name="isIconVisible">When true (the WinUI default) the standard severity icon is shown.</param>
    /// <param name="actionButton">Optional trailing action slot (WinUI ActionButton) - laid out inline after the
    /// message when it fits, else below it. Pass e.g. <c>Button.Create(...)</c> or <c>HyperlinkButton.Create(...)</c>.</param>
    /// <param name="onClosing">Closing hook (WinUI Closing). Set <c>e.Cancel = true</c> to veto: the close is aborted and
    /// <paramref name="onClose"/>/<paramref name="onClosed"/> do not run (the caller keeps the bar open).</param>
    /// <param name="onClosed">Closed hook (WinUI Closed), fired after a non-canceled close.</param>
    /// <param name="content">Optional expanded content (WinUI <c>Content</c>/<c>ContentTemplate</c>, InfoBar.idl:94-95):
    /// rendered in the second row of the content column, below the title/message/action panel (ContentArea,
    /// InfoBar.xaml:126). When title, message AND action are all absent it becomes the sole content of the column
    /// (the NoBannerContent state, InfoBar.xaml:86-90 / InfoBar.cpp:282-286).</param>
    /// <param name="iconGlyph">Optional custom icon glyph (WinUI <c>IconSource</c>, InfoBar.idl:77). When set (and
    /// <paramref name="isIconVisible"/>) it replaces the standard severity circle+status stack — the UserIconVisible
    /// state (InfoBar.cpp:261-264) — capped at 16x16 with the standard icon margin (UserIconBox, InfoBar.xaml:111).</param>
    /// <param name="parts">Lightweight per-part styling (CSS ::part): modifiers keyed by the <c>PartXxx</c> consts —
    /// see <see cref="TemplateParts"/> for the contract. Title/Message are TextEl parts
    /// (<c>parts.Set&lt;TextEl&gt;(InfoBar.PartTitle, …)</c>).</param>
    public static Element Create(
        InfoBarSeverity severity,
        string title,
        string message,
        Action? onClose = null,
        bool isOpen = true,
        bool isClosable = true,
        bool isIconVisible = true,
        Element? actionButton = null,
        Action<InfoBarClosingEventArgs>? onClosing = null,
        Action<InfoBarClosedEventArgs>? onClosed = null,
        Element? content = null,
        string? iconGlyph = null,
        TemplateParts? parts = null)
    {
        if (!isOpen)
            return new BoxEl { };

        var ts = InfoBarTemplateSettings.For(severity);

        // Column 0: the icon. UpdateIconVisibility (InfoBar.cpp:261-264): IsIconVisible ? (IconSource ? UserIconVisible
        // : StandardIconVisible) : NoIconVisible — a custom iconGlyph wins over the standard severity stack.
        //  - User icon (UserIconBox, InfoBar.xaml:111): a Viewbox capped at MaxWidth/MaxHeight = InfoBarIconFontSize
        //    (16), Margin = InfoBarIconMargin 0,16,14,16, VerticalAlignment="Top"; the glyph inherits the control
        //    foreground (no template setter) = TextFillColorPrimary.
        //  - Standard icon (StandardIconArea, InfoBar.xaml:107-110): two stacked glyphs — the background glyph (F136,
        //    a filled circle) carries the severity icon-background color; the status glyph sits on top in the inverse
        //    color. Both at FontSize 16, top-aligned, same 0,16,14,16 margin. A 16x16 Z-stack box matches the glyph box.
        Element? icon = !isIconVisible
            ? null
            : iconGlyph is not null
                ? parts.Apply(PartIcon, new BoxEl
                {
                    Width = IconFontSize,          // UserIconBox Viewbox MaxWidth/MaxHeight = InfoBarIconFontSize (InfoBar.xaml:111)
                    Height = IconFontSize,
                    Margin = IconMargin,
                    AlignSelf = FlexAlign.Start,   // VerticalAlignment="Top"
                    AlignItems = FlexAlign.Center,
                    Justify = FlexJustify.Center,
                    Children =
                    [
                        new TextEl(iconGlyph) { Size = IconFontSize, FontFamily = Theme.IconFont, Color = Tok.TextPrimary },
                    ],
                })
                : parts.Apply(PartIcon, new BoxEl
                {
                    ZStack = true,
                    Width = IconFontSize,
                    Height = IconFontSize,
                    Margin = IconMargin,
                    AlignSelf = FlexAlign.Start,   // VerticalAlignment="Top"
                    Children =
                    [
                        new TextEl(IconBackgroundGlyph) { Size = IconFontSize, FontFamily = Theme.IconFont, Color = ts.IconBackground },
                        new TextEl(ts.Glyph)            { Size = IconFontSize, FontFamily = Theme.IconFont, Color = ts.IconForeground },
                    ],
                });

        // Column 1: the InfoBarPanel (Title / Message / Action). WinUI's InfoBarPanel.MeasureOverride switches orientation:
        // it lays the children out VERTICALLY when (a) there is a single laid-out child, (b) the horizontal total width
        // exceeds the available width (the message can't sit inline / would wrap), or (c) any child measured taller than
        // InfoBarMinHeight; otherwise HORIZONTALLY. The engine has no width-aware measure-time orientation switch, so we
        // make the decision here from the available signals: a single content child => vertical; an action button present
        // alongside a (non-trivial) message => the inline row would overflow, so vertical; otherwise horizontal. This is
        // the cold Render path (per-severity), so the bool/factory work below allocates nothing on the hot frame phases.
        bool hasTitle = !string.IsNullOrEmpty(title);
        bool hasMessage = !string.IsNullOrEmpty(message);
        bool hasAction = actionButton is not null;
        bool hasBanner = hasTitle || hasMessage || hasAction;
        int contentItems = (hasTitle ? 1 : 0) + (hasMessage ? 1 : 0) + (hasAction ? 1 : 0);

        // WinUI's implicit InfoBar HyperlinkButton style matches by TYPE inside the action ContentPresenter
        // (InfoBar.xaml:117-122); our HyperlinkButton roots carry Role = Hyperlink, so detect the same way.
        bool isHyperlinkAction = actionButton is BoxEl { Role: AutomationRole.Hyperlink };

        // WinUI: nItems == 1 => vertical (the margins work out better that way). Approximate (b)/(c): when an action button
        // sits next to a long message it will not fit inline on one row, so WinUI flips to vertical — mirror that here.
        const int LongMessageChars = 60; // heuristic for "message wraps / doesn't fit on one line" alongside an action
        bool isVertical = contentItems <= 1
            || (hasAction && hasMessage && message.Length >= LongMessageChars);

        // With no banner items the panel measures ZERO in WinUI (InfoBarPanel.MeasureOverride falls to the horizontal
        // branch with 0 items and 0,0,0,0 padding), so omit it outright instead of rendering dead vertical padding.
        BoxEl? panel = !hasBanner
            ? null
            : isVertical
                ? BuildVerticalPanel(title, message, actionButton, hasTitle, hasMessage, isHyperlinkAction, parts)
                : BuildHorizontalPanel(title, message, actionButton, hasTitle, hasMessage, isHyperlinkAction, parts);

        // Column 1 (the * column): row 0 = the InfoBarPanel, row 1 = the ContentArea ContentPresenter holding
        // Content/ContentTemplate (InfoBar.idl:94-95; InfoBar.xaml:126 — Grid.Row=1 Grid.Column=1, no margin,
        // VerticalAlignment="Center"). Both grid rows are Auto (InfoBar.xaml:103-106), so the rows stack content-sized
        // from the top — a plain vertical stack reproduces the geometry (Center within an Auto row is a no-op). The
        // NoBannerContent state (no title/message/action) moves ContentArea to row 0 (InfoBar.xaml:86-90;
        // InfoBar.cpp:282-286) — i.e. the content alone fills the column.
        Element column = content is not null
            ? new BoxEl
            {
                Direction = 1,
                Grow = 1f,
                // Inside the vertical stack the panel must not absorb the leftover column height (its Grow=1 is the
                // *-column horizontal grow when it is the sole column child).
                Children = panel is null ? [content] : [panel with { Grow = 0f }, content],
            }
            : panel ?? new BoxEl { Grow = 1f };   // empty * column still pushes the close button to the trailing edge

        var children = new List<Element>(3);
        if (icon is not null) children.Add(icon);
        children.Add(column);

        // Column 2: the close button. WinUI uses an AppBarButton-style Button: 38x38, Top-aligned, Margin 5, a 16px
        // Cancel glyph in TextFillColorPrimary, subtle hover/press (AppBarButtonBackgroundPointerOver/Pressed =
        // SubtleFillColorSecondary/Tertiary). The X both invokes onClose AND runs the Closing/Closed lifecycle.
        if (isClosable)
        {
            Action? closeClick = onClose is null && onClosing is null && onClosed is null
                ? null
                : () => RaiseClose(InfoBarCloseReason.CloseButton, onClose, onClosing, onClosed);
            var closeButton = new BoxEl
            {
                Width = CloseButtonSize,
                Height = CloseButtonSize,
                Margin = CloseButtonMargin,
                AlignSelf = FlexAlign.Start,                    // VerticalAlignment="Top"
                AlignItems = FlexAlign.Center,
                Justify = FlexJustify.Center,
                Corners = Radii.ControlAll,                     // ControlCornerRadius = 4
                HoverFill = Tok.FillSubtleSecondary,            // AppBarButtonBackgroundPointerOver
                PressedFill = Tok.FillSubtleTertiary,           // AppBarButtonBackgroundPressed
                OnClick = closeClick,
                Role = AutomationRole.Button,
                Children =
                [
                    new TextEl(Icons.Cancel)
                    {
                        Size = CloseGlyphSize, FontFamily = Theme.IconFont, Color = Tok.TextPrimary,
                        HoverColor = Tok.TextPrimary, PressedColor = Tok.TextSecondary,  // AppBarButtonForegroundPressed
                    },
                ],
            };
            // Parts: restyle the X freely; the Closing→Closed lifecycle always wins when the control owns one (a
            // modifier-supplied OnClick survives only when the caller gave no close handlers).
            var m = parts.Apply(PartCloseButton, closeButton);
            // WinUI attaches a localized "Close" tooltip to the close button in OnApplyTemplate (InfoBar.cpp:55-59,
            // SR_InfoBarCloseButtonTooltip = "Close"). The ToolTip wrapper self-aligns Start (VerticalAlignment="Top").
            children.Add(ToolTip.Wrap(m with { OnClick = closeClick ?? m.OnClick, Role = AutomationRole.Button }, "Close"));
        }

        var root = new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Stretch,                     // columns stretch to row height (icon/close self-align Top)
            MinHeight = MinHeight,                              // InfoBarMinHeight = 48
            Padding = ContentRootPadding,                       // InfoBarContentRootPadding 16,0,0,0
            Corners = Radii.ControlAll,                         // CornerRadius = ControlCornerRadius (4)
            Fill = ts.Background,                               // InfoBar*SeverityBackgroundBrush
            BorderWidth = 1f,                                   // InfoBarBorderThickness = 1
            BorderColor = Tok.StrokeCardDefault,               // InfoBarBorderBrush = CardStrokeColorDefaultBrush
            Role = AutomationRole.InfoBar,
            Children = children.ToArray(),
        };
        // Parts: restyle the bar chrome (the severity tint is stock per-render styling — override-able); the column
        // structure and role always win.
        return parts.Apply(PartRoot, root) with { Children = root.Children, Role = AutomationRole.InfoBar };
    }

    // HORIZONTAL InfoBarPanel: icon-column already trails; here title + message + action sit inline on one row. Panel
    // padding is 0 (InfoBarPanelHorizontalOrientationPadding); per-child horizontal orientation margins give the inline
    // gaps (title 0,14,0,0; message 12,14,0,0; action 16,8,0,0) — the panel ignores the FIRST child's leading-left margin
    // (WinUI "hasPreviousElement"), so the leftmost child gets only its top margin. AlignItems Top mirrors WinUI's
    // VerticalAlignment="Top" on Title/Message and Action. Cold Render path — array build here never hits a hot frame phase.
    private static BoxEl BuildHorizontalPanel(string title, string message, Element? actionButton, bool hasTitle, bool hasMessage, bool isHyperlinkAction, TemplateParts? parts)
    {
        var kids = new List<Element>(3);
        bool first = true;
        // The LAST horizontal child is handed the remaining row width — InfoBarPanel.ArrangeOverride arranges it at
        // max(desired, finalSize.Width − offset) (InfoBarPanel.cpp:144-147). Horizontal needs >= 2 items, so the last
        // is the action when present, else the message (the title is always first).
        bool hasAction = actionButton is not null;
        if (hasTitle)
        {
            // Title FontWeight = SemiBold — InfoBarTitleFontWeight (InfoBar_themeresources.xaml:63, bound at
            // InfoBar.xaml:113); the numeric TextEl weight pipeline shapes 600 directly.
            kids.Add(parts.Apply(PartTitle, new TextEl(title)
            {
                Size = TitleFontSize, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.WrapWholeWords, Shrink = 1f,
                Margin = new Edges4(0f, TitleHMargin.Top, 0f, 0f),   // first child: ignore leading-left margin
            }));
            first = false;
        }
        if (hasMessage)
        {
            kids.Add(parts.Apply(PartMessage, new TextEl(message)
            {
                Size = MessageFontSize, Color = Tok.TextPrimary, Wrap = TextWrap.WrapWholeWords, Shrink = 1f,
                Grow = hasAction ? 0f : 1f,                          // last child takes the remaining width (InfoBarPanel.cpp:144-147)
                Margin = new Edges4(first ? 0f : MessageHMargin.Left, MessageHMargin.Top, 0f, 0f),
            }));
            first = false;
        }
        if (actionButton is not null)
        {
            // A HyperlinkButton action adds its implicit-style own-margin (-12,0,0,0) on top of the orientation margin.
            float left = (first ? 0f : ActionHMargin.Left) + (isHyperlinkAction ? HyperlinkActionMarginLeft : 0f);
            kids.Add(new BoxEl
            {
                Margin = new Edges4(left, ActionHMargin.Top, 0f, 0f),
                AlignSelf = FlexAlign.Start,                        // VerticalAlignment="Top"
                Grow = 1f,                                          // last child takes the remaining width (InfoBarPanel.cpp:144-147)
                Children = [actionButton],
            });
        }
        return new BoxEl
        {
            Direction = 0,                                          // horizontal
            Grow = 1f,
            Gap = 0f,
            Margin = PanelMargin,                                   // InfoBarPanelMargin 0,0,16,0
            Padding = PanelHorizontalPadding,                       // 0,0,0,0
            AlignItems = FlexAlign.Start,                           // children top-aligned (WinUI VerticalAlignment="Top")
            Children = kids.ToArray(),
        };
    }

    // VERTICAL InfoBarPanel: title, then message, then action STACKED. Panel padding is 0,14,0,18
    // (InfoBarPanelVerticalOrientationPadding); per-child vertical orientation margins give the inter-line spacing
    // (title 0,14,0,0; message 0,4,0,0; action 0,12,0,0) — the panel ignores the FIRST child's leading-TOP margin
    // (WinUI "hasPreviousElement"), and the panel's own top padding (14) supplies the leading gap instead.
    private static BoxEl BuildVerticalPanel(string title, string message, Element? actionButton, bool hasTitle, bool hasMessage, bool isHyperlinkAction, TemplateParts? parts)
    {
        var kids = new List<Element>(3);
        bool first = true;
        if (hasTitle)
        {
            // Title FontWeight = SemiBold — InfoBarTitleFontWeight (InfoBar_themeresources.xaml:63, bound at
            // InfoBar.xaml:113); the numeric TextEl weight pipeline shapes 600 directly.
            kids.Add(parts.Apply(PartTitle, new TextEl(title)
            {
                Size = TitleFontSize, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.WrapWholeWords, Shrink = 1f,
                Margin = new Edges4(0f, first ? 0f : TitleVMargin.Top, 0f, 0f),   // first child: ignore leading-top margin
            }));
            first = false;
        }
        if (hasMessage)
        {
            kids.Add(parts.Apply(PartMessage, new TextEl(message)
            {
                Size = MessageFontSize, Color = Tok.TextPrimary, Wrap = TextWrap.WrapWholeWords, Shrink = 1f,
                Margin = new Edges4(0f, first ? 0f : MessageVMargin.Top, 0f, 0f), // 0,4,0,0 below the title
            }));
            first = false;
        }
        if (actionButton is not null)
        {
            kids.Add(new BoxEl
            {
                // A HyperlinkButton action adds its implicit-style own-margin (-12,0,0,0) on top of the orientation margin.
                Margin = new Edges4(isHyperlinkAction ? HyperlinkActionMarginLeft : 0f, first ? 0f : ActionVMargin.Top, 0f, 0f), // 0,12,0,0 below the message
                AlignSelf = FlexAlign.Start,                        // left-aligned action (WinUI HorizontalAlignment default)
                Children = [actionButton],
            });
        }
        return new BoxEl
        {
            Direction = 1,                                          // vertical
            Grow = 1f,
            Gap = 0f,
            Margin = PanelMargin,                                   // InfoBarPanelMargin 0,0,16,0
            Padding = PanelVerticalPadding,                         // 0,14,0,18
            AlignItems = FlexAlign.Start,                           // children left-aligned in the stack
            Children = kids.ToArray(),
        };
    }

    // The close lifecycle (WinUI OnCloseButtonClick -> IsOpen(false) -> RaiseClosingEvent -> RaiseClosedEvent): run
    // Closing first; if the developer cancels, abort (no onClose / Closed). This composes in the cold Render path, so the
    // list/closure allocation here is fine (never a hot bind thunk).
    private static void RaiseClose(InfoBarCloseReason reason, Action? onClose, Action<InfoBarClosingEventArgs>? onClosing, Action<InfoBarClosedEventArgs>? onClosed)
    {
        if (onClosing is not null)
        {
            var args = new InfoBarClosingEventArgs(reason);
            onClosing(args);
            if (args.Cancel) return;   // vetoed - WinUI reverts IsOpen to true; the caller keeps the bar open.
        }
        onClose?.Invoke();
        onClosed?.Invoke(new InfoBarClosedEventArgs(reason));
    }
}
