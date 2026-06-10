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
    // WinUI InfoBar_themeresources.xaml:70-74 standard icon glyphs (Segoe Fluent Icons / SymbolThemeFontFamily),
    // single-sourced from Icons.cs as VISIBLE \uXXXX escapes (raw PUA literals read as "empty" in most viewers —
    // the audit's blocker — so the named constants are the canonical spelling).
    private const string IconBackgroundGlyph = Icons.InfoBarBackgroundCircle; // F136 (InfoBar_themeresources.xaml:70)
    private const string GlyphInfo    = Icons.StatusInfo;                     // F13F (:71)
    private const string GlyphSuccess = Icons.StatusSuccess;                  // F13E (:74)
    private const string GlyphWarning = Icons.StatusWarning;                  // F13C (:73)
    private const string GlyphError   = Icons.StatusError;                    // F13D (:72)

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

    /// <summary>The TemplateSettings convention (per the audit's P3) - the per-severity computed geometry WinUI binds
    /// into its severity VisualState: the standard status glyph, the icon-background fill (SystemFillColor*Brush), the
    /// inverse glyph foreground, and the tinted content-root background (SystemFillColor*BackgroundBrush). Computed once
    /// per severity by the pure factory <see cref="For"/> - no per-frame allocation.</summary>
    public readonly record struct InfoBarTemplateSettings(string Glyph, ColorF IconBackground, ColorF IconForeground, ColorF Background)
    {
        public static InfoBarTemplateSettings For(InfoBarSeverity severity) => severity switch
        {
            // InfoBar*SeverityIconBackground = SystemFillColor*Brush; IconForeground = TextFillColorInverseBrush;
            // background = SystemFillColor*BackgroundBrush. Informational follows the OS accent (SystemFillColorAttention).
            InfoBarSeverity.Success => new(GlyphSuccess, Tok.SystemFillSuccess,   Tok.TextInverse, Tok.SystemFillSuccessBackground),
            InfoBarSeverity.Warning => new(GlyphWarning, Tok.SystemFillCaution,   Tok.TextInverse, Tok.SystemFillCautionBackground),
            InfoBarSeverity.Error   => new(GlyphError,   Tok.SystemFillCritical,  Tok.TextInverse, Tok.SystemFillCriticalBackground),
            _                       => new(GlyphInfo,    Tok.SystemFillAttention, Tok.TextInverse, Tok.SystemFillAttentionBackground),
        };
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
        Action<InfoBarClosedEventArgs>? onClosed = null)
    {
        if (!isOpen)
            return new BoxEl { };

        var ts = InfoBarTemplateSettings.For(severity);

        // Column 0: the standard icon - two stacked glyphs (WinUI StandardIconArea). The background glyph (F136, a
        // filled circle) carries the severity icon-background color; the status glyph sits on top in the inverse color.
        // Both at FontSize 16, top-aligned, with the 0,16,14,16 icon margin. A 16x16 Z-stack box matches the glyph box.
        Element? icon = isIconVisible
            ? new BoxEl
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
            }
            : null;

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
        int contentItems = (hasTitle ? 1 : 0) + (hasMessage ? 1 : 0) + (hasAction ? 1 : 0);

        // WinUI: nItems == 1 => vertical (the margins work out better that way). Approximate (b)/(c): when an action button
        // sits next to a long message it will not fit inline on one row, so WinUI flips to vertical — mirror that here.
        const int LongMessageChars = 60; // heuristic for "message wraps / doesn't fit on one line" alongside an action
        bool isVertical = contentItems <= 1
            || (hasAction && hasMessage && message.Length >= LongMessageChars);

        var panel = isVertical
            ? BuildVerticalPanel(title, message, actionButton, hasTitle, hasMessage)
            : BuildHorizontalPanel(title, message, actionButton, hasTitle, hasMessage);

        var children = new List<Element>(3);
        if (icon is not null) children.Add(icon);
        children.Add(panel);

        // Column 2: the close button. WinUI uses an AppBarButton-style Button: 38x38, Top-aligned, Margin 5, a 16px
        // Cancel glyph in TextFillColorPrimary, subtle hover/press (AppBarButtonBackgroundPointerOver/Pressed =
        // SubtleFillColorSecondary/Tertiary). The X both invokes onClose AND runs the Closing/Closed lifecycle.
        if (isClosable)
        {
            children.Add(new BoxEl
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
                OnClick = onClose is null && onClosing is null && onClosed is null
                    ? null
                    : () => RaiseClose(InfoBarCloseReason.CloseButton, onClose, onClosing, onClosed),
                Role = AutomationRole.Button,
                Children =
                [
                    new TextEl(Icons.Cancel)
                    {
                        Size = CloseGlyphSize, FontFamily = Theme.IconFont, Color = Tok.TextPrimary,
                        HoverColor = Tok.TextPrimary, PressedColor = Tok.TextSecondary,  // AppBarButtonForegroundPressed
                    },
                ],
            });
        }

        return new BoxEl
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
    }

    // HORIZONTAL InfoBarPanel: icon-column already trails; here title + message + action sit inline on one row. Panel
    // padding is 0 (InfoBarPanelHorizontalOrientationPadding); per-child horizontal orientation margins give the inline
    // gaps (title 0,14,0,0; message 12,14,0,0; action 16,8,0,0) — the panel ignores the FIRST child's leading-left margin
    // (WinUI "hasPreviousElement"), so the leftmost child gets only its top margin. AlignItems Top mirrors WinUI's
    // VerticalAlignment="Top" on Title/Message and Action. Cold Render path — array build here never hits a hot frame phase.
    private static BoxEl BuildHorizontalPanel(string title, string message, Element? actionButton, bool hasTitle, bool hasMessage)
    {
        var kids = new List<Element>(3);
        bool first = true;
        if (hasTitle)
        {
            // Title FontWeight = SemiBold (600) in WinUI (InfoBarTitleFontWeight); TextEl only exposes bool Bold (700), so
            // Bold=true is the closest weight pending a numeric TextEl FontWeight (engine-wide limitation — fix in the engine).
            kids.Add(new TextEl(title)
            {
                Size = TitleFontSize, Bold = true, Color = Tok.TextPrimary, Wrap = TextWrap.WrapWholeWords, Shrink = 1f,
                Margin = new Edges4(0f, TitleHMargin.Top, 0f, 0f),   // first child: ignore leading-left margin
            });
            first = false;
        }
        if (hasMessage)
        {
            kids.Add(new TextEl(message)
            {
                Size = MessageFontSize, Color = Tok.TextPrimary, Wrap = TextWrap.WrapWholeWords, Shrink = 1f,
                Margin = new Edges4(first ? 0f : MessageHMargin.Left, MessageHMargin.Top, 0f, 0f),
            });
            first = false;
        }
        if (actionButton is not null)
        {
            kids.Add(new BoxEl
            {
                Margin = new Edges4(first ? 0f : ActionHMargin.Left, ActionHMargin.Top, 0f, 0f),
                AlignSelf = FlexAlign.Start,                        // VerticalAlignment="Top"
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
    private static BoxEl BuildVerticalPanel(string title, string message, Element? actionButton, bool hasTitle, bool hasMessage)
    {
        var kids = new List<Element>(3);
        bool first = true;
        if (hasTitle)
        {
            // Title FontWeight = SemiBold (600) in WinUI (InfoBarTitleFontWeight); TextEl only exposes bool Bold (700), so
            // Bold=true is the closest weight pending a numeric TextEl FontWeight (engine-wide limitation — fix in the engine).
            kids.Add(new TextEl(title)
            {
                Size = TitleFontSize, Bold = true, Color = Tok.TextPrimary, Wrap = TextWrap.WrapWholeWords, Shrink = 1f,
                Margin = new Edges4(0f, first ? 0f : TitleVMargin.Top, 0f, 0f),   // first child: ignore leading-top margin
            });
            first = false;
        }
        if (hasMessage)
        {
            kids.Add(new TextEl(message)
            {
                Size = MessageFontSize, Color = Tok.TextPrimary, Wrap = TextWrap.WrapWholeWords, Shrink = 1f,
                Margin = new Edges4(0f, first ? 0f : MessageVMargin.Top, 0f, 0f), // 0,4,0,0 below the title
            });
            first = false;
        }
        if (actionButton is not null)
        {
            kids.Add(new BoxEl
            {
                Margin = new Edges4(0f, first ? 0f : ActionVMargin.Top, 0f, 0f), // 0,12,0,0 below the message
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
