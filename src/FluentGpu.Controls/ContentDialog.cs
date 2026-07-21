using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;

namespace FluentGpu.Controls;

/// <summary>How a ContentDialog was dismissed (WinUI <c>ContentDialogResult</c>): the primary/secondary command, or
/// None (close button / Escape / programmatic / light-of-modal never applies).</summary>
public enum ContentDialogResult : byte { None = 0, Primary = 1, Secondary = 2 }

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

    // ContentDialog_themeresources.xaml / Common_themeresources_any.xaml.
    const float MinW = 320f, MaxW = 548f, MinH = 184f, MaxH = 756f;
    const float Pad = 24f;
    const float ContentGap = 12f;
    const float BtnGap = 8f;
    const float TitleSize = 20f;     // SubtitleTextBlockStyle (Title presenter) — 20px SemiBold
    const float ContentSize = 14f;
    const float ButtonMinW = 130f;
    const float ButtonH = 32f;

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
        // Escape and programmatic closes report None; button closes pre-set the result before Close().
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
            getHandle()?.Close();
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

        BoxEl CommandButton(string text, bool accent, bool enabled, Action onClick)
        {
            var b = accent ? Button.Accent(text, onClick, isEnabled: enabled) : Button.Standard(text, onClick, isEnabled: enabled);
            return b with
            {
                MinWidth = ButtonMinW,
                Height = ButtonH,
                MinHeight = ButtonH,
                Grow = 1f,
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
            if (primaryShown) buttons.Add(CommandButton(PrimaryText ?? Loc.Get(Strings.Dialog.Ok), primaryAccent, IsPrimaryButtonEnabled, RunPrimary));
            if (secondaryShown) buttons.Add(CommandButton(SecondaryText, secondaryAccent, IsSecondaryButtonEnabled, RunSecondary));
            if (closeShown) buttons.Add(CommandButton(CloseText, closeAccent, true, RunClose));
        }
        float cardW = Math.Clamp(DialogWidth ?? (buttons.Count >= 3 ? 480f : MinW), MinW, MaxW);

        Element[] commandChildren;
        if (buttons.Count == 1)
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
        Element contentBody = Content is not null
            ? new ScrollEl { Content = Content, ContentSized = true, MaxHeight = MaxH - 200f }
            : new TextEl(Message) { Size = ContentSize, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap };

        var contentRegion = new BoxEl
        {
            Direction = 1,
            Padding = Edges4.All(Pad),
            Fill = Tok.FillLayerAlt,   // ContentDialogTopOverlay = LayerFillColorAltBrush
            Children =
            [
                Parts.Apply(PartTitle, new TextEl(Title)
                {
                    Size = TitleSize,
                    Weight = 600,      // FontWeight="SemiBold" on the Title presenter (ContentDialog_themeresources.xaml:238)
                    Color = Tok.TextPrimary,
                    Wrap = TextWrap.Wrap,
                    MaxLines = 2,
                    Trim = TextTrim.WordEllipsis,
                    Margin = new Edges4(0, 0, 0, ContentGap),   // ContentDialogTitleMargin 0,0,0,12
                }),
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
            cardChildren.Add(new BoxEl { Height = 1f, Fill = Tok.StrokeCardDefault });
            // A Footer replaces the built-in buttons inside the command space (same chrome), letting a reactive
            // component swap the actions per phase without re-opening the dialog.
            Element[] commandKids = Footer is not null ? [Footer] : commandChildren;
            var commandRow = new BoxEl
            {
                Direction = 0,
                Gap = BtnGap,
                Justify = FlexJustify.Start,
                AlignItems = FlexAlign.Stretch,
                Padding = Edges4.All(Pad),
                Fill = Tok.FillSolidBase,   // the command space fills (CommandSpace Background = ContentDialogBackground)
                Children = commandKids,
            };
            // Restyle via [PartCommandRow]; the buttons (handlers + TabIndex focus ranking) always win.
            cardChildren.Add(Parts.Apply(PartCommandRow, commandRow) with { Children = commandKids });
        }

        var plate = new BoxEl
        {
            Direction = 1,
            Width = cardW,
            MinWidth = MinW,
            MaxWidth = MaxW,
            MinHeight = MinH,
            MaxHeight = MaxH,
            Corners = Radii.OverlayAll,
            Fill = Tok.FillSolidBase,
            BorderColor = Tok.StrokeSurfaceDefault,
            BorderWidth = 1f,
            Shadow = Elevation.Dialog,
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
