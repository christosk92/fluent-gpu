using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;

namespace FluentGpu.Controls;

/// <summary>
/// The DEFAULT link flow of <see cref="MarkdownTextBlock"/> when the consumer set no
/// <see cref="MarkdownOptions.OnLinkClicked"/> (markdown.md §6.1): a modal warning dialog in the Windows 11
/// <see cref="ContentDialog"/> style — the Outlook link-warning shape — that shows the FULL real destination (so a
/// misleading label can never hide it), with <b>Open</b> and <b>Cancel</b>; Open launches the URL through the
/// <c>IPlatformApp.OpenUri</c> seam exactly once. Only <c>http</c> and <c>https</c> may open: any other scheme
/// (<c>file:</c>, <c>javascript:</c>, <c>mailto:</c>, a custom protocol, a relative path) gets a refusal dialog that
/// still shows the destination and offers only Close. Without an <see cref="OverlayHost"/> ancestor there is no dialog
/// and nothing opens. A consumer handler replaces this whole flow. Images are untouched by this policy: they never
/// fetch without <see cref="MarkdownOptions.ImageResolver"/>.
/// </summary>
public static class MarkdownLinkPolicy
{
    /// <summary>True when <paramref name="url"/> has an <c>http</c> or <c>https</c> scheme (case-insensitive). The
    /// scheme is reported either way (empty for none) so a refusal can name it.</summary>
    public static bool IsOpenable(string url, out string scheme)
    {
        scheme = "";
        if (string.IsNullOrEmpty(url)) return false;
        int colon = url.IndexOf(':');
        if (colon <= 0) return false;
        for (int i = 0; i < colon; i++)
        {
            char c = url[i];
            if (!(char.IsAsciiLetterOrDigit(c) || c == '+' || c == '-' || c == '.')) return false;
            if (i == 0 && !char.IsAsciiLetter(c)) return false;
        }
        scheme = url.Substring(0, colon).ToLowerInvariant();
        return scheme == "http" || scheme == "https" || scheme == "mailto";
    }

    /// <summary>Show the warning (or refusal) dialog for <paramref name="url"/> on <paramref name="overlay"/>. The
    /// context default (no <see cref="OverlayHost"/> in the tree) opens nothing and returns false.</summary>
    public static bool ConfirmAndOpen(IOverlayService overlay, string url)
    {
        if (ReferenceEquals(overlay, Overlay.Service.Default)) return false;
        bool openable = IsOpenable(url, out string scheme);
        ContentDialog.Show(overlay, d =>
        {
            d.TitleLiteral = true;
            bool mail = openable && scheme == "mailto";
            d.Title = Loc.Get(mail ? Strings.Markdown.LinkMailTitle : openable ? Strings.Markdown.LinkOpenTitle : Strings.Markdown.LinkRefusedTitle);
            string message = mail
                ? Loc.Get(Strings.Markdown.LinkMailMessage)
                : openable
                ? Loc.Get(Strings.Markdown.LinkOpenMessage)
                : scheme.Length == 0
                    ? Loc.Get(Strings.Markdown.LinkRefusedNoScheme)
                    : Strings.Markdown.LinkRefusedScheme(scheme);
            // Content replaces the dialog's Message slot, so the explanation and the REAL destination (verbatim, selectable,
            // in the code face so look-alike characters show as written) form one column.
            d.Content = new BoxEl
            {
                Direction = 1, Gap = Spacing.S,
                Children =
                [
                    new TextEl(message) { Size = Tok.Definition.Type.Body.Size, LineHeight = Tok.Definition.Type.Body.LineHeight, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, Shrink = 1f, MinWidth = 0f },
                    new BoxEl
                    {
                        Padding = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.S),
                        Fill = Tok.FillCardSecondary, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, Corners = Radii.ControlAll,
                        Children =
                        [
                            new TextEl(url)
                            {
                                Size = Tok.Definition.Type.Body.Size, LineHeight = Tok.Definition.Type.Body.LineHeight,
                                FontFamily = "Cascadia Code", Color = Tok.TextPrimary, Wrap = TextWrap.Wrap,
                                IsTextSelectionEnabled = true, Shrink = 1f, MinWidth = 0f,
                            },
                        ],
                    },
                ],
            };
            if (openable)
            {
                d.PrimaryText = Loc.Get(mail ? Strings.Markdown.Write : Strings.Markdown.Open);
                d.CloseText = Loc.Get(Strings.Markdown.Cancel);
                // Enter must not open an unseen destination: the safe button is the default (the Outlook prompt's stance).
                d.DefaultButton = ContentDialog.DefaultBtn.Close;
                d.PrimaryClick = () => InputHooks.Current.Default.OpenUri?.Invoke(url);
            }
            else
            {
                d.PrimaryText = "";   // hidden: there is nothing to open
                d.CloseText = Loc.Get(Strings.Markdown.Close);
                d.DefaultButton = ContentDialog.DefaultBtn.Close;
            }
        });
        return true;
    }
}
