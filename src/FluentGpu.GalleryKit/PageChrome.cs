using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static FluentGpu.Dsl.Ui;

namespace FluentGpu.GalleryKit;

/// <summary>
/// A small copy-to-clipboard button (the WinUI Gallery CopyButton): glyph (+ optional label), writes <see cref="Text"/>
/// through the <c>IClipboard</c> PAL seam, and swaps to a checkmark + "Copied" for ~1.5s. The cooldown rides
/// <c>UseAnimatedValue</c> so the revert needs no timer; the extra re-renders are scoped to this component.
/// </summary>
public sealed class CopyButton : Component
{
    public string Text = "";
    public string Glyph = Icons.Copy;
    public string? Label;
    public string? Tip;

    public override Element Render()
    {
        var hooks = UseContext(InputHooks.Current);
        var (copied, setCopied) = UseState(false);
        float cool = UseAnimatedValue(copied ? 1f : 0f, 1500f);
        bool coolDone = copied && cool >= 0.999f;
        UseEffect(() => { if (coolDone) setCopied(false); }, coolDone);

        string glyph = copied ? Icons.Accept : Glyph;
        string? label = Label is null ? null : copied ? "Copied" : Label;
        var fg = copied ? Tok.AccentDefault : Tok.TextSecondary;

        var children = new List<Element> { Icon(glyph, 13f).Foreground(fg) };
        if (label is not null) children.Add(new TextEl(label) { Size = 12f, Color = fg });

        return new BoxEl
        {
            Direction = 0, Gap = 6, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            MinHeight = 30f, MinWidth = 30f, Padding = new Edges4(8, 4, 8, 4), Corners = Radii.ControlAll,
            Fill = Tok.FillSubtleTransparent, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            Focusable = true, Role = AutomationRole.Button,
            OnClick = () => { hooks.Clipboard?.SetText(Text); setCopied(true); },
            Children = children.ToArray(),
        };
    }
}

/// <summary>The WinUI ItemPage "Related controls" section: hyperlink-style chips that navigate the gallery shell via
/// the ambient <c>NavigationView.Nav</c> context. A component (not a factory) so it can read the navigation context.</summary>
public sealed class RelatedLinks : Component
{
    public string[] Keys = [];
    /// <summary>Optional label→key display: when set, chip i shows <c>Labels[i]</c> but navigates <c>Keys[i]</c>.</summary>
    public string[]? Labels;

    public override Element Render()
    {
        var navigate = UseContext(NavigationView.Nav);
        if (Keys.Length == 0) return new BoxEl();

        var chips = new Element[Keys.Length];
        for (int i = 0; i < Keys.Length; i++)
        {
            var key = Keys[i];
            string text = Labels is { } l && i < l.Length ? l[i] : key;
            chips[i] = HyperlinkButton.Create(text, () => navigate(key));
        }
        return new BoxEl
        {
            Direction = 1, Gap = 8, Margin = new Edges4(0, 20, 0, 0),
            Children =
            [
                Subtitle("Related controls"),
                new BoxEl { Direction = 0, Wrap = true, Gap = 4, Children = chips },
            ],
        };
    }
}

/// <summary>
/// The WinUI ItemPage "PageHeader" — pure layout, graduated into GalleryKit. A <c>Heading</c> row, an optional
/// Documentation/Source dropdown row (with a copy-page-link affordance on the right), an optional API line, and the
/// description. All page-metadata business logic (which docs, which source files, the deep-link string) stays in the
/// consuming app — the caller passes the already-assembled <c>MenuFlyoutItem</c> lists + strings, so GalleryKit needs no
/// knowledge of the app's per-page metadata registry.
/// </summary>
public static class PageHeader
{
    public static Element Build(string title, string? description, string? apiLine = null,
        IReadOnlyList<MenuFlyoutItem>? docItems = null, IReadOnlyList<MenuFlyoutItem>? sourceItems = null,
        string? deepLink = null)
    {
        var kids = new List<Element> { Heading(title) };

        bool hasDocs = docItems is { Count: > 0 };
        bool hasSrc = sourceItems is { Count: > 0 };
        if (hasDocs || hasSrc || deepLink is not null)
        {
            var row = new List<Element>();
            if (hasDocs) row.Add(DropDownButton.Create("Documentation", new List<MenuFlyoutItem>(docItems!), Icons.Document));
            if (hasSrc) row.Add(DropDownButton.Create("Source", new List<MenuFlyoutItem>(sourceItems!), Icons.Code));
            row.Add(new BoxEl { Grow = 1, MinWidth = 16 });
            if (deepLink is { } dl)
                row.Add(Embed.Comp(() => new CopyButton { Text = dl, Glyph = Icons.Link, Tip = "Copy page link" }));
            kids.Add(new BoxEl { Direction = 0, Gap = 8, AlignItems = FlexAlign.Center, Wrap = true, Margin = new Edges4(0, 4, 0, 0), Children = row.ToArray() });
        }

        if (apiLine is { } api)
            kids.Add(new TextEl(api) { Size = 12f, Color = Tok.TextTertiary, FontFamily = "Cascadia Code" });

        if (!string.IsNullOrEmpty(description)) kids.Add(Body(description!).Secondary());

        return new BoxEl { Direction = 1, Gap = 8, Children = kids.ToArray() };
    }
}

/// <summary>The standard gallery page scaffold (the graduated <c>GalleryPage.Shell</c> layout): a scrollable padded
/// column with the supplied <paramref name="header"/> chrome on top, the page body, and an optional
/// <see cref="RelatedLinks"/> footer. Metadata resolution (which builds <paramref name="header"/> and
/// <paramref name="relatedKeys"/>) stays app-side.</summary>
public static class GalleryScaffold
{
    public static Element Page(Element header, string[]? relatedKeys, params Element[] body)
        => Page(header, relatedKeys, null, body);

    public static Element Page(Element header, string[]? relatedKeys, string[]? relatedLabels, params Element[] body)
    {
        var kids = new List<Element> { header, new BoxEl { Height = 8 } };
        kids.AddRange(body);
        if (relatedKeys is { Length: > 0 })
            kids.Add(Embed.Comp(() => new RelatedLinks { Keys = relatedKeys, Labels = relatedLabels }));
        return ScrollView(new BoxEl { Direction = 1, Gap = 4f, Padding = Edges4.All(28), Children = kids.ToArray() });
    }
}
