using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;

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
    public string PrimaryText = "OK";
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

    // ContentDialog_themeresources.xaml / Common_themeresources_any.xaml.
    const float MinW = 320f, MaxW = 548f, MinH = 184f, MaxH = 756f;
    const float Pad = 24f;
    const float ContentGap = 12f;
    const float BtnGap = 8f;
    const float TitleSize = 20f;     // SubtitleTextBlockStyle (Title presenter) — 20px SemiBold
    const float ContentSize = 14f;
    const float ButtonMinW = 130f;
    const float ButtonH = 32f;

    public static Element Create(string triggerLabel, string title, string message, string primaryText = "OK",
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
        var result = UseRef(ContentDialogResult.None);
        var autoOpened = UseRef(false);

        bool primaryShown = PrimaryText.Length > 0;
        bool secondaryShown = SecondaryText.Length > 0;
        bool closeShown = CloseText.Length > 0;

        Element BuildCard()
        {
            DefaultBtn def = DefaultButton;
            bool primaryAccent = def == DefaultBtn.Primary && primaryShown;
            bool secondaryAccent = def == DefaultBtn.Secondary && secondaryShown;
            bool closeAccent = def == DefaultBtn.Close && closeShown;

            void CloseWith(ContentDialogResult r, Action? click)
            {
                result.Value = r;
                click?.Invoke();
                opened.Value?.Close();
            }

            void RunPrimary() => CloseWith(ContentDialogResult.Primary, PrimaryClick);
            void RunSecondary() => CloseWith(ContentDialogResult.Secondary, SecondaryClick);
            void RunClose() => CloseWith(ContentDialogResult.None, CloseClick);

            BoxEl CommandButton(string text, bool accent, Action onClick)
            {
                var b = accent ? Button.Accent(text, onClick) : Button.Standard(text, onClick);
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
                if (primaryAccent) { RunPrimary(); e.Handled = true; }
                else if (secondaryAccent) { RunSecondary(); e.Handled = true; }
                else if (closeAccent) { RunClose(); e.Handled = true; }
            }

            var buttons = new List<BoxEl>(3);
            if (primaryShown) buttons.Add(CommandButton(PrimaryText, primaryAccent, RunPrimary));
            if (secondaryShown) buttons.Add(CommandButton(SecondaryText, secondaryAccent, RunSecondary));
            if (closeShown) buttons.Add(CommandButton(CloseText, closeAccent, RunClose));
            float cardW = buttons.Count >= 3 ? 480f : MinW;

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

            var contentRegion = new BoxEl
            {
                Direction = 1,
                Padding = Edges4.All(Pad),
                Fill = Tok.FillLayerAlt,   // ContentDialogTopOverlay = LayerFillColorAltBrush
                Children =
                [
                    new TextEl(Title)
                    {
                        Size = TitleSize,
                        Bold = true,       // SemiBold (600) in WinUI; TextEl exposes Bold (700) — engine-wide weight limitation
                        Color = Tok.TextPrimary,
                        Wrap = TextWrap.Wrap,
                        MaxLines = 2,
                        Trim = TextTrim.WordEllipsis,
                        Margin = new Edges4(0, 0, 0, ContentGap),   // ContentDialogTitleMargin 0,0,0,12
                    },
                    new TextEl(Message) { Size = ContentSize, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap },
                ],
            };

            var cardChildren = new List<Element>(3) { contentRegion };
            if (buttons.Count > 0)
            {
                // ContentScrollViewer's inner grid carries ContentDialogSeparatorThickness=0,0,0,1 and
                // ContentDialogSeparatorBorderBrush=CardStrokeColorDefault. Model it as the bottom separator between
                // the top overlay and the command row.
                cardChildren.Add(new BoxEl { Height = 1f, Fill = Tok.StrokeCardDefault });
                cardChildren.Add(new BoxEl
                {
                    Direction = 0,
                    Gap = BtnGap,
                    Justify = FlexJustify.Start,
                    AlignItems = FlexAlign.Stretch,
                    Padding = Edges4.All(Pad),
                    Fill = Tok.FillSolidBase,   // the command space fills (CommandSpace Background = ContentDialogBackground)
                    Children = commandChildren,
                });
            }

            return new BoxEl
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
                OnKeyDown = OnCardKey,   // Enter → default button (bubbles from any focused child inside the trap)
                Children = cardChildren.ToArray(),
            };
        }

        void Open()
        {
            if (opened.Value is { IsOpen: true }) return;
            result.Value = ContentDialogResult.None;
            var handle = svc.Open(
                () => NodeHandle.Null,
                BuildCard,
                FlyoutPlacement.BottomCenter,
                // Modal: no light dismiss, survives window deactivation; FocusTrap pushes a REAL dispatcher focus
                // scope (Tab cycles inside; initial focus = first tab stop = the default button).
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.Modal, Chrome: PopupChrome.Modal));
            handle.ClosedAction = () => opened.Value = null;
            // Escape and programmatic closes report None; button closes pre-set the result before Close().
            handle.ClosedWithCauseAction = _ => Closed?.Invoke(result.Value);
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
}
