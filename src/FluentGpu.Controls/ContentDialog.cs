using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;

namespace FluentGpu.Controls;

/// <summary>
/// WinUI-style ContentDialog: a modal, page-level smoke scrim with a centered solid card. The dialog is hosted through
/// <see cref="OverlayHost"/> instead of being drawn inside the sample card, so it overlays the whole page.
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

    // ContentDialog_themeresources.xaml / Common_themeresources_any.xaml.
    const float MinW = 320f, MaxW = 548f, MinH = 184f, MaxH = 756f;
    const float Pad = 24f;
    const float ContentGap = 12f;
    const float BtnGap = 8f;
    const float TitleSize = 20f;
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

            void Close() => opened.Value?.Close();

            BoxEl CommandButton(string text, bool accent)
            {
                var b = accent ? Button.Accent(text, Close) : Button.Standard(text, Close);
                return b with
                {
                    MinWidth = ButtonMinW,
                    Height = ButtonH,
                    MinHeight = ButtonH,
                    Grow = 1f,
                    Justify = FlexJustify.Center,
                };
            }

            var buttons = new List<BoxEl>(3);
            if (primaryShown) buttons.Add(CommandButton(PrimaryText, primaryAccent));
            if (secondaryShown) buttons.Add(CommandButton(SecondaryText, secondaryAccent));
            if (closeShown) buttons.Add(CommandButton(CloseText, closeAccent));
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
                        Bold = true,
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
                    Fill = Tok.FillSolidBase,
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
                Children = cardChildren.ToArray(),
            };
        }

        void Open()
        {
            if (opened.Value is { IsOpen: true }) return;
            var handle = svc.Open(
                () => NodeHandle.Null,
                BuildCard,
                FlyoutPlacement.BottomCenter,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.Modal, Chrome: PopupChrome.Modal));
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
}
