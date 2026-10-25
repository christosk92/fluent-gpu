using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;

namespace FluentGpu.Controls;

/// <summary>How a ContentDialog was dismissed (WinUI <c>ContentDialogResult</c>): the primary/secondary command, or
/// None (close button / Escape / programmatic / light-of-modal never applies).</summary>
public enum ContentDialogResult : byte { None = 0, Primary = 1, Secondary = 2 }

/// <summary>One of a ContentDialog's three built-in commands (WinUI PrimaryButton / SecondaryButton / CloseButton): the unit of
/// <see cref="ContentDialog.Style.CommandOrder"/> and what a theme's command presenter is told it is drawing.</summary>
public enum ContentDialogCommand : byte { Primary = 0, Secondary = 1, Close = 2 }

/// <summary>What a theme's command presenter (<see cref="ContentDialog.Style.CommandButton"/>) is handed for one command: which
/// command it is, its label, whether it is the dialog's default (the accent one Enter invokes), whether it is enabled, the
/// dialog's <see cref="ContentDialog.CommandSize"/>, and <see cref="Invoke"/> (the cancelable click → close path the built-in
/// button runs). The presenter returns the command's box; the dialog keeps its focus ranking (the default command first).</summary>
public readonly record struct ContentDialogCommandArgs(
    ContentDialogCommand Command, string Text, bool IsDefault, bool IsEnabled, ControlSize Size, Action Invoke);

/// <summary>
/// WinUI-style ContentDialog: a modal, page-level smoke scrim with a centered solid card. The dialog is hosted through
/// <see cref="OverlayHost"/> instead of being drawn inside the sample card, so it overlays the whole page.
/// <para>1:1 with <c>ContentDialog_themeresources.xaml</c>:</para>
/// <list type="bullet">
/// <item>Chrome — smoke #4D000000 (SmokeFillColorDefault), background SolidBackgroundFillColorBase, 1px
/// SurfaceStrokeColorDefault border, OverlayCornerRadius 8, dialog padding 24 (ContentDialogPadding), title margin
/// 0,0,0,12 (ContentDialogTitleMargin), button spacing 8 (ContentDialogButtonSpacing), separator
/// CardStrokeColorDefault 0,0,0,1, Min/Max 320×184 / 548×756 (:12-15).</item>
/// <item>Motion — open: scale 1.05→1.0 over ControlNormal (250ms) + opacity 0→1 over ControlFaster (83ms, linear);
/// close: scale 1.0→1.05 over ControlFast (167ms) + opacity 1→0 over 83ms; both scale legs on the
/// ControlFastOutSlowInKeySpline (0,0,0,1) — ContentDialog_themeresources.xaml:77-117 DialogShowing/Hidden
/// transitions, driven by the OverlayHost's Modal chrome.</item>
/// <item>Keyboard — Enter invokes the <see cref="DefaultButton"/> (the accent-styled one); Escape closes with
/// <see cref="ContentDialogResult.None"/> (the overlay's Escape preview); Tab/Shift-Tab CYCLE inside the dialog
/// (DialogShowing sets BackgroundElement.TabFocusNavigation=Cycle, :123 — a REAL dispatcher focus scope via the
/// host's PopupOptions.FocusTrap). Initial focus lands on the default button (TabIndex ranks it first).</item>
/// </list>
/// </summary>
public sealed class ContentDialog : Component
{
    public enum DefaultBtn : byte { None, Primary, Secondary, Close }

    public string TriggerLabel = "Show dialog";
    public string Title = "Title";
    /// <summary>The title carries CONTENT (a name): the style's <see cref="Style.TitleCase"/> leaves it as written.</summary>
    public bool TitleLiteral;
    public string Message = "";
    // null = the localized default label (Strings.Dialog.Ok, neutral "OK") resolved at render — so it translates and
    // re-resolves on a culture change. Set to a non-null string to override; set to "" to hide the primary button.
    public string? PrimaryText;
    public string SecondaryText = "";
    public string CloseText = "";
    public bool OpenOnMount;
    public DefaultBtn DefaultButton = DefaultBtn.Primary;

    /// <summary>Command callbacks (WinUI PrimaryButtonClick / SecondaryButtonClick / CloseButtonClick).</summary>
    public Action? PrimaryClick;
    public Action? SecondaryClick;
    public Action? CloseClick;
    /// <summary>Raised once per close with the WinUI <c>ContentDialogResult</c> (the ShowAsync return value):
    /// Primary/Secondary for the commands, None for the close button, Escape, or a programmatic close.</summary>
    public Action<ContentDialogResult>? Closed;

    /// <summary>WinUI <c>ContentControl.Content</c> — an arbitrary body rendered in place of <see cref="Message"/> when
    /// set (the title still shows above it). If its natural height exceeds the card, it scrolls inside a vertical viewer
    /// (the WinUI ContentScrollViewer) so long bodies (e.g. a version list) scroll within the fixed card.</summary>
    public Element? Content;

    /// <summary>Fixed card width override (WinUI has no direct knob, but rich content wants more than the 320 default).
    /// Null keeps the built-in sizing. Clamped to the WinUI Min/Max (320…548).</summary>
    public float? DialogWidth;

    /// <summary>WinUI <c>IsPrimaryButtonEnabled</c> / <c>IsSecondaryButtonEnabled</c>. A disabled command renders via the
    /// Button disabled state, ignores clicks, and is skipped by the Enter default-button routing.</summary>
    public bool IsPrimaryButtonEnabled = true;
    public bool IsSecondaryButtonEnabled = true;

    /// <summary>The command buttons' density on the kit's size axis (<see cref="ControlSize"/>): <see cref="ControlSize.Medium"/>
    /// (the default) is WinUI's 32-DIP command button; <see cref="ControlSize.Large"/> is the 40-DIP touch target a compact
    /// window asks for (the Button's own Large metrics); <see cref="ControlSize.Small"/> the 24-DIP compact button. The
    /// buttons' height is the size's <see cref="ControlMetrics.MinHeight"/>.</summary>
    public ControlSize CommandSize = ControlSize.Medium;

    /// <summary>The least width of each command button (WinUI's 130-DIP command column floor, the default). A narrow window
    /// lowers it so two buttons still sit side by side in a card narrower than 2 x 130 plus the gap.</summary>
    public float CommandMinWidth = ButtonMinW;

    /// <summary>The command buttons share the row EQUALLY (WinUI's star columns: every button is the same width whatever its
    /// label) instead of the default, where each grows from its own label's width. Off by default.</summary>
    public bool UniformCommandWidths;

    /// <summary>Cancelable command clicks (WinUI <c>ContentDialogButtonClickEventArgs.Cancel</c>): setting
    /// <see cref="ContentDialogButtonClickArgs.Cancel"/> keeps the dialog OPEN — this is what lets one dialog advance
    /// through phases (Offer → Downloading → Ready) instead of closing on the first click. Runs BEFORE the matching
    /// simple <see cref="PrimaryClick"/>/<see cref="SecondaryClick"/>/<see cref="CloseClick"/> (which fire only if the
    /// dialog actually closes).</summary>
    public Action<ContentDialogButtonClickArgs>? PrimaryButtonClick;
    public Action<ContentDialogButtonClickArgs>? SecondaryButtonClick;
    public Action<ContentDialogButtonClickArgs>? CloseButtonClick;

    /// <summary>A custom command row that REPLACES the built-in Primary/Secondary/Close buttons inside the command
    /// space (after the separator, same padding/fill). Pass an <c>Embed.Comp(...)</c> so the row re-renders reactively —
    /// this is how a phased dialog (Offer → Downloading → Ready) swaps its actions without re-opening. When set, the
    /// built-in button texts are ignored.</summary>
    public Element? Footer;

    /// <summary>WinUI <c>Opened</c> — raised once when the dialog is shown.</summary>
    public Action? Opened;
    /// <summary>WinUI <c>Closing</c> — raised before any dismissal (button, Escape, programmatic); set
    /// <see cref="ContentDialogClosingArgs.Cancel"/> to veto (e.g. block Escape/close mid-download).</summary>
    public Action<ContentDialogClosingArgs>? Closing;

    // Template parts (see TemplateParts). Each const's doc lists the props the control OWNS (re-asserted after any
    // modifier — a Parts customization cannot win those).
    /// <summary>The dialog card (WinUI BackgroundElement): fill, border, corners, shadow, min/max sizes… Owned:
    /// OnKeyDown (Enter → default button, chained with any modifier-supplied handler), Children (the content ·
    /// separator · command-row structure).</summary>
    public const string PartPlate = "Plate";
    /// <summary>The title text (the WinUI Title presenter) — a TextEl part: customize via
    /// <c>Parts.Set&lt;TextEl&gt;(ContentDialog.PartTitle, t =&gt; t with { … })</c>. Owned: none (pure styling).</summary>
    public const string PartTitle = "Title";
    /// <summary>The padded content region above the separator (the WinUI DialogSpace top overlay). Owned: Children
    /// (the title + message structure).</summary>
    public const string PartContent = "Content";
    /// <summary>The footer command row (WinUI CommandSpace) — built only when a button is shown. Owned: Children
    /// (the command buttons: their click handlers, default-button accent, and TabIndex focus-trap ranking).</summary>
    public const string PartCommandRow = "CommandRow";

    /// <summary>Lightweight per-part styling (CSS ::part): modifiers keyed by the <c>PartXxx</c> consts; see
    /// <see cref="TemplateParts"/> for the contract.</summary>
    public TemplateParts? Parts;

    /// <summary>The dialog's look, read at RENDER through the control style table (<see cref="ControlStyles.ContentDialogStyle"/>):
    /// the card, the title and message, the separator, and the command row (its fill, spacing, order and an optional command
    /// presenter). Every default is the ContentDialog_themeresources value the dialog has always used, so a theme without the key
    /// renders exactly the WinUI dialog. Per-instance knobs (<see cref="DialogWidth"/>, <see cref="CommandSize"/>,
    /// <see cref="CommandMinWidth"/>, <see cref="UniformCommandWidths"/>, <see cref="Parts"/>) still apply on top.</summary>
    public sealed record Style
    {
        /// <summary>The card fill (SolidBackgroundFillColorBase).</summary>
        public ColorF PlateFill { get; init; } = Tok.FillSolidBase;
        /// <summary>The card ring (SurfaceStrokeColorDefault) and its width (1; 0 = no ring).</summary>
        public ColorF PlateBorder { get; init; } = Tok.StrokeSurfaceDefault;
        public float PlateBorderWidth { get; init; } = 1f;
        /// <summary>The card radius (OverlayCornerRadius).</summary>
        public float Radius { get; init; } = Radii.Overlay;
        /// <summary>The card elevation (the dialog band); null = no shadow.</summary>
        public ShadowSpec? Shadow { get; init; } = Elevation.Dialog;
        /// <summary>The card's size limits (ContentDialogMinWidth/MaxWidth/MinHeight/MaxHeight 320 / 548 / 184 / 756).</summary>
        public float MinWidth { get; init; } = MinW;
        public float MaxWidth { get; init; } = MaxW;
        public float MinHeight { get; init; } = MinH;
        public float MaxHeight { get; init; } = MaxH;
        /// <summary>The card width when the instance sets no <see cref="DialogWidth"/>; NaN = the WinUI rule (the min width, 480 with three commands).</summary>
        public float Width { get; init; } = float.NaN;
        /// <summary>The content region (DialogSpace): its padding (ContentDialogPadding 24) and fill (LayerFillColorAlt).</summary>
        public Edges4 ContentPadding { get; init; } = Edges4.All(Pad);
        /// <summary>A long body that scrolls inside the card feathers the edges it overflows (<see cref="ScrollEl.AutoEdgeFade"/>), so a cut field reads as
        /// more below, never as a stray sliver. False = WinUI's ContentScrollViewer, which draws no cue.</summary>
        public bool ContentEdgeFade { get; init; }
        public ColorF ContentFill { get; init; } = Tok.FillLayerAlt;
        /// <summary>The title (SubtitleTextBlockStyle 20 SemiBold, TextFillColorPrimary) and its gap to the body (ContentDialogTitleMargin 12).</summary>
        public float TitleSize { get; init; } = ContentDialog.TitleSize;
        public int TitleWeight { get; init; } = 600;
        public ColorF TitleColor { get; init; } = Tok.TextPrimary;
        public float TitleGap { get; init; } = ContentGap;
        /// <summary>The case transform of the title, applied at shaping (the stored title is never rewritten). Default <see cref="TextCase.AsWritten"/>; a dialog whose title carries a name sets <see cref="TitleLiteral"/>.</summary>
        public TextCase TitleCase { get; init; } = TextCase.AsWritten;
        /// <summary>An element at the end of the title's row, given the dialog's close action (a theme's close mark); null = none (the default).</summary>
        public Func<Action, Element>? TitleTrailing { get; init; }
        /// <summary>The message text (14, TextFillColorPrimary).</summary>
        public float MessageSize { get; init; } = ContentSize;
        public ColorF MessageColor { get; init; } = Tok.TextPrimary;
        /// <summary>The separator above the command row (ContentDialogSeparatorBorderBrush = CardStrokeColorDefault, 1); 0 = none.</summary>
        public ColorF SeparatorColor { get; init; } = Tok.StrokeCardDefault;
        public float SeparatorThickness { get; init; } = 1f;
        /// <summary>The command row (CommandSpace): its padding (24), fill (ContentDialogBackground), gap (ContentDialogButtonSpacing 8)
        /// and main-axis placement (Start).</summary>
        public Edges4 CommandPadding { get; init; } = Edges4.All(Pad);
        public ColorF CommandFill { get; init; } = Tok.FillSolidBase;
        public float CommandGap { get; init; } = BtnGap;
        public FlexJustify CommandJustify { get; init; } = FlexJustify.Start;
        /// <summary>The order the shown commands take, left to right (WinUI: primary, secondary, close). A command missing from the
        /// list is not shown.</summary>
        public IReadOnlyList<ContentDialogCommand> CommandOrder { get; init; } = s_winUiOrder;
        /// <summary>The commands share the row (WinUI's star columns: every command grows); false = each command hugs its content.</summary>
        public bool CommandsFill { get; init; } = true;
        /// <summary>A lone command sits in the right-hand column with the empty left column before it (WinUI's single-button state).</summary>
        public bool LoneCommandTrailing { get; init; } = true;
        /// <summary>Draws one command; null = the built-in Button (accent for the default command), sized by <see cref="CommandSize"/>
        /// and <see cref="CommandMinWidth"/>.</summary>
        public Func<ContentDialogCommandArgs, BoxEl>? CommandButton { get; init; }
        /// <summary>The case transform of a command's label (the verbs are command words, never names). Default <see cref="TextCase.AsWritten"/>.</summary>
        public TextCase CommandCase { get; init; } = TextCase.AsWritten;
    }

    static readonly ContentDialogCommand[] s_winUiOrder = [ContentDialogCommand.Primary, ContentDialogCommand.Secondary, ContentDialogCommand.Close];

    /// <summary>The dialog style now: the active theme's (<see cref="ControlStyles.ContentDialogStyle"/>), else <see cref="FluentStyle"/>.</summary>
    public static Style DefaultStyle => ControlStyles.Resolve(ControlStyles.ContentDialogStyle, FluentStyle);

    /// <summary>The built-in Fluent default (what a theme table without a ContentDialog entry resolves to). Reads the live Tok on every call.</summary>
    public static Style FluentStyle() => new();

    // ContentDialog_themeresources.xaml / Common_themeresources_any.xaml.
    const float MinW = 320f, MaxW = 548f, MinH = 184f, MaxH = 756f;
    const float Pad = 24f;
    const float ContentGap = 12f;
    const float BtnGap = 8f;
    const float TitleSize = 20f;     // SubtitleTextBlockStyle (Title presenter) — 20px SemiBold
    const float ContentSize = 14f;
    const float ButtonMinW = 130f;

    public static Element Create(string triggerLabel, string title, string message, string? primaryText = null,
                                 string secondaryText = "", string closeText = "", DefaultBtn defaultButton = DefaultBtn.Primary)
        => Embed.Comp(() => new ContentDialog
        {
            TriggerLabel = triggerLabel,
            Title = title,
            Message = message,
            PrimaryText = primaryText,
            SecondaryText = secondaryText,
            CloseText = closeText,
            DefaultButton = defaultButton,
        });

    public override Element Render()
    {
        var svc = UseContext(Overlay.Service);
        var opened = UseRef<OverlayHandle?>(null);
        var autoOpened = UseRef(false);

        void Open()
        {
            if (opened.Value is { IsOpen: true }) return;
            var handle = OpenOn(svc);
            handle.ClosedAction = () => opened.Value = null;
            opened.Value = handle;
        }

        UseEffect(() =>
        {
            if (!OpenOnMount || autoOpened.Value) return;
            autoOpened.Value = true;
            Open();
        }, OpenOnMount);

        return new BoxEl
        {
            AlignItems = FlexAlign.Start,
            Children = [Button.Accent(TriggerLabel, Open)],
        };
    }

    /// <summary>Open the dialog directly through <paramref name="overlay"/> (no trigger button) — WinUI
    /// <c>ShowAsync</c> for banner/menu-launched dialogs. Configure the instance in <paramref name="configure"/>
    /// (Title, Content, buttons, callbacks). The result surfaces via <see cref="Closed"/>; the returned handle can be
    /// closed programmatically.</summary>
    public static OverlayHandle Show(IOverlayService overlay, Action<ContentDialog> configure)
    {
        var dialog = new ContentDialog();
        configure(dialog);
        return dialog.OpenOn(overlay);
    }

    /// <summary>Shared opener for both the declarative trigger path and <see cref="Show"/>: wires the modal chrome, the
    /// Closing veto, and the Closed result. The card content re-renders reactively (it's a Component subtree), so the
    /// phase-driven setup body updates without re-opening.</summary>
    OverlayHandle OpenOn(IOverlayService overlay)
    {
        // Boxed so BuildCardCore's close callback (invoked on click) sees the latest result, and getHandle sees the
        // handle even though it's assigned after Open() returns.
        var result = new ContentDialogResult[] { ContentDialogResult.None };
        OverlayHandle? handle = null;
        handle = overlay.Open(
            () => NodeHandle.Null,
            () => BuildCardCore(() => handle, r => result[0] = r),
            FlyoutPlacement.BottomCenter,
            // Modal: no light dismiss, survives window deactivation; FocusTrap pushes a REAL dispatcher focus scope
            // (Tab cycles inside; initial focus = first tab stop = the default button).
            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.Modal, Chrome: PopupChrome.Modal));
        // Veto hook (Escape / light-dismiss / programmatic) — lets Closing.Cancel block dismissal mid-download.
        handle.ClosingAction = VetoClosing;
        // Escape and programmatic closes report None (a vetoed button close resets the result); button closes pre-set
        // the result before Close().
        handle.ClosedWithCauseAction = _ => Closed?.Invoke(result[0]);
        Opened?.Invoke();
        return handle;
    }

    bool VetoClosing(OverlayCloseCause cause)
    {
        if (Closing is null) return true;
        var args = new ContentDialogClosingArgs(cause);
        Closing(args);
        return !args.Cancel;
    }

    Element BuildCardCore(Func<OverlayHandle?> getHandle, Action<ContentDialogResult> setResult)
    {
        // The dialog's look, read now (this builds inside the overlay's render): a theme swap restyles an open dialog's next render.
        var st = DefaultStyle;
        bool primaryShown = PrimaryText != "";   // null => localized default (shown); "" => explicitly hidden
        bool secondaryShown = SecondaryText.Length > 0;
        bool closeShown = CloseText.Length > 0;

        DefaultBtn def = DefaultButton;
        bool primaryAccent = def == DefaultBtn.Primary && primaryShown;
        bool secondaryAccent = def == DefaultBtn.Secondary && secondaryShown;
        bool closeAccent = def == DefaultBtn.Close && closeShown;

        void CloseWith(ContentDialogResult r, Action? click)
        {
            setResult(r);
            click?.Invoke();
            if (getHandle() is not { } h) return;
            h.Close();
            // A Closing veto keeps the dialog open: drop this button's result so a later Escape or programmatic close
            // reports None (WinUI computes the result per close attempt, not per dialog).
            if (h.IsOpen) setResult(ContentDialogResult.None);
        }

        // Run a cancelable click handler; returns true if it vetoed the close (dialog stays open to advance phases).
        static bool Canceled(Action<ContentDialogButtonClickArgs>? handler)
        {
            if (handler is null) return false;
            var a = new ContentDialogButtonClickArgs();
            handler(a);
            return a.Cancel;
        }

        void RunPrimary()
        {
            if (!IsPrimaryButtonEnabled) return;
            if (Canceled(PrimaryButtonClick)) return;
            CloseWith(ContentDialogResult.Primary, PrimaryClick);
        }
        void RunSecondary()
        {
            if (!IsSecondaryButtonEnabled) return;
            if (Canceled(SecondaryButtonClick)) return;
            CloseWith(ContentDialogResult.Secondary, SecondaryClick);
        }
        void RunClose()
        {
            if (Canceled(CloseButtonClick)) return;
            CloseWith(ContentDialogResult.None, CloseClick);
        }

        // The size axis: Medium is WinUI's 32-DIP button (ControlMetrics.For(Medium).MinHeight == 32, the pre-knob height).
        float commandH = ControlMetrics.For(CommandSize).MinHeight;
        float commandMinW = MathF.Max(0f, CommandMinWidth);
        BoxEl CommandButton(ContentDialogCommand command, string text, bool accent, bool enabled, Action onClick)
        {
            text = FluentGpu.Text.TextCaseMap.Apply(text, st.CommandCase);
            // A theme's command presenter draws the command; the dialog keeps the focus ranking and the row's star columns.
            if (st.CommandButton is { } present)
            {
                var drawn = present(new ContentDialogCommandArgs(command, text, accent, enabled, CommandSize, onClick));
                return drawn with
                {
                    Grow = st.CommandsFill ? 1f : drawn.Grow,
                    Basis = st.CommandsFill && UniformCommandWidths ? 0f : drawn.Basis,
                    TabIndex = accent ? 1 : 2,
                };
            }
            var b = Button.Create(text, onClick, accent ? ButtonAppearance.Accent : ButtonAppearance.Standard, CommandSize, isEnabled: enabled);
            return b with
            {
                MinWidth = commandMinW,
                Height = commandH,
                MinHeight = commandH,
                Grow = st.CommandsFill ? 1f : b.Grow,
                Basis = st.CommandsFill && UniformCommandWidths ? 0f : b.Basis,   // star columns: grow from zero, so every button ends the same width
                Justify = FlexJustify.Center,
                // The DEFAULT (accent) button ranks FIRST in tab order so the focus trap's initial focus lands on
                // it — WinUI focuses the default button when the dialog opens (ContentDialog_Partial SetInitialFocus).
                TabIndex = accent ? 1 : 2,
            };
        }

        // Enter activates the default button from ANYWHERE in the dialog (WinUI ContentDialog::ProcessEnterKey —
        // Enter routes to the DefaultButton unless a focused control handled it). Escape is the overlay preview.
        void OnCardKey(KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter) return;
            if (primaryAccent && IsPrimaryButtonEnabled) { RunPrimary(); e.Handled = true; }
            else if (secondaryAccent && IsSecondaryButtonEnabled) { RunSecondary(); e.Handled = true; }
            else if (closeAccent) { RunClose(); e.Handled = true; }
        }
        // One delegate instance, so the Chain re-assert collapses when a [PartPlate] modifier leaves it in place.
        Action<KeyEventArgs> onCardKey = OnCardKey;

        var buttons = new List<BoxEl>(3);
        if (Footer is null)
        {
            // The theme's order (WinUI: primary, secondary, close); a command it leaves out is not shown.
            foreach (var command in st.CommandOrder)
            {
                switch (command)
                {
                    case ContentDialogCommand.Primary when primaryShown:
                        buttons.Add(CommandButton(command, PrimaryText ?? Loc.Get(Strings.Dialog.Ok), primaryAccent, IsPrimaryButtonEnabled, RunPrimary));
                        break;
                    case ContentDialogCommand.Secondary when secondaryShown:
                        buttons.Add(CommandButton(command, SecondaryText, secondaryAccent, IsSecondaryButtonEnabled, RunSecondary));
                        break;
                    case ContentDialogCommand.Close when closeShown:
                        buttons.Add(CommandButton(command, CloseText, closeAccent, true, RunClose));
                        break;
                }
            }
        }
        float stMin = st.MinWidth, stMax = MathF.Max(st.MinWidth, st.MaxWidth);
        float cardW = Math.Clamp(DialogWidth ?? (!float.IsNaN(st.Width) ? st.Width : buttons.Count >= 3 ? 480f : stMin), stMin, stMax);

        Element[] commandChildren;
        if (buttons.Count == 1 && st.LoneCommandTrailing)
        {
            // WinUI single-button states move the visible button into the right star column; the unused left star
            // column remains, separated by ContentDialogButtonSpacing (8).
            commandChildren = [new BoxEl { Grow = 1f, HitTestVisible = false }, buttons[0]];
        }
        else
        {
            commandChildren = new Element[buttons.Count];
            for (int i = 0; i < buttons.Count; i++)
                commandChildren[i] = buttons[i];
        }

        // WinUI ContentControl.Content replaces the Message TextEl; long bodies scroll inside the fixed card
        // (the WinUI ContentScrollViewer) instead of overflowing MaxHeight.
        //
        // No edge cue on the dialog body: WinUI's ContentScrollViewer draws none. (The cue is the analytic edge feather
        // now — colour-independent — so this is a fidelity choice, not the old painted band's wrong-plate hazard.) A style that
        // wants the overflow seen feathers it (Style.ContentEdgeFade).
        Element contentBody = Content is not null
            ? new ScrollEl { Content = Content.Value, ContentSized = true, MaxHeight = st.MaxHeight - 200f, EdgeCues = ScrollEdgeCues.None, AutoEdgeFade = st.ContentEdgeFade }
            : new TextEl(Message) { Size = st.MessageSize, Color = st.MessageColor, Wrap = TextWrap.Wrap };

        Element TitleRow(Style st, Action close)
        {
            var title = Parts.Apply(PartTitle, new TextEl(Title)
            {
                Size = st.TitleSize,
                Weight = (ushort)st.TitleWeight,      // FontWeight="SemiBold" on the Title presenter (ContentDialog_themeresources.xaml:238)
                Color = st.TitleColor,
                Case = TitleLiteral ? TextCase.AsWritten : st.TitleCase,
                Wrap = TextWrap.Wrap,
                MaxLines = 2,
                Trim = TextTrim.WordEllipsis,
                Margin = new Edges4(0, 0, 0, st.TitleGap),   // ContentDialogTitleMargin 0,0,0,12
            });
            if (st.TitleTrailing is not { } trailing) return title;
            // A theme's trailing element (a close mark) ends the title's row; the gap under the title moves to the row.
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, Margin = title.Margin,
                Children = [title with { Margin = default, Grow = 1f, MinWidth = 0f }, trailing(close)],
            };
        }

        var contentRegion = new BoxEl
        {
            Direction = 1,
            Padding = st.ContentPadding,
            Fill = st.ContentFill,   // ContentDialogTopOverlay = LayerFillColorAltBrush
            Children =
            [
                TitleRow(st, RunClose),
                contentBody,
            ],
        };
        contentRegion = Parts.Apply(PartContent, contentRegion) with { Children = contentRegion.Children };   // structure = title + content

        var cardChildren = new List<Element>(3) { contentRegion };
        if (buttons.Count > 0 || Footer is not null)
        {
            // ContentScrollViewer's inner grid carries ContentDialogSeparatorThickness=0,0,0,1 and
            // ContentDialogSeparatorBorderBrush=CardStrokeColorDefault. Model it as the bottom separator between
            // the top overlay and the command row.
            if (st.SeparatorThickness > 0f) cardChildren.Add(new BoxEl { Height = st.SeparatorThickness, Fill = st.SeparatorColor });
            // A Footer replaces the built-in buttons inside the command space (same chrome), letting a reactive
            // component swap the actions per phase without re-opening the dialog.
            Element[] commandKids = Footer is not null ? [Footer.Value] : commandChildren;
            var commandRow = new BoxEl
            {
                Direction = 0,
                Gap = st.CommandGap,
                Justify = st.CommandJustify,
                AlignItems = FlexAlign.Stretch,
                Padding = st.CommandPadding,
                Fill = st.CommandFill,   // the command space fills (CommandSpace Background = ContentDialogBackground)
                Children = commandKids,
            };
            // Restyle via [PartCommandRow]; the buttons (handlers + TabIndex focus ranking) always win.
            cardChildren.Add(Parts.Apply(PartCommandRow, commandRow) with { Children = commandKids });
        }

        var plate = new BoxEl
        {
            Direction = 1,
            Width = cardW,
            MinWidth = stMin,
            MaxWidth = stMax,
            MinHeight = st.MinHeight,
            MaxHeight = st.MaxHeight,
            Corners = CornerRadius4.All(st.Radius),
            Fill = st.PlateFill,
            BorderColor = st.PlateBorder,
            BorderWidth = st.PlateBorderWidth,
            Shadow = st.Shadow,
            ClipToBounds = true,
            OnKeyDown = onCardKey,   // Enter → default button (bubbles from any focused child inside the trap)
            Children = cardChildren.ToArray(),
        };
        if (Parts is { } pp)
        {
            var m = pp.Apply(PartPlate, plate);
            plate = m with
            {
                Children = plate.Children,
                OnKeyDown = TemplateParts.Chain(onCardKey, m.OnKeyDown),   // Enter routing first, then any modifier handler
            };
        }
        return plate;
    }
}

/// <summary>WinUI <c>ContentDialogButtonClickEventArgs</c>: set <see cref="Cancel"/> in a
/// Primary/Secondary/CloseButtonClick handler to keep the dialog open.</summary>
public sealed class ContentDialogButtonClickArgs
{
    public bool Cancel;
}

/// <summary>WinUI <c>ContentDialogClosingEventArgs</c>: set <see cref="Cancel"/> to veto the dismissal.
/// <see cref="Cause"/> is why the close was attempted (button/programmatic, Escape, or light-dismiss).</summary>
public sealed class ContentDialogClosingArgs
{
    public ContentDialogClosingArgs(OverlayCloseCause cause) => Cause = cause;
    public OverlayCloseCause Cause { get; }
    public bool Cancel;
}
