using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static FluentGpu.Dsl.Ui;

// ── WinUI ItemPage "PageHeader" parity ─────────────────────────────────────────────────────────────────────────────
// Title row (Heading left; Documentation/Source dropdowns + copy-page-link right), an API line ("FluentGpu.Controls ·
// Button.cs"), and the page description. Data comes from the PageInfo registry; pages without metadata fall back to
// the plain title + description header.
static class PageHeader
{
    public static Element Build(string title, string? description, PageMeta? meta)
    {
        var kids = new List<Element> { Heading(title) };

        // WinUI ItemPage layout: the Documentation/Source dropdowns sit on their own row BELOW the title,
        // left-aligned; the copy-link affordance rides the right edge of that row.
        if (meta is not null)
        {
            var row = new List<Element>();
            var docs = BuildDocItems(meta);
            if (docs.Count > 0) row.Add(DropDownButton.Create("Documentation", docs, Icons.Document));
            var src = BuildSourceItems(meta);
            if (src.Count > 0) row.Add(DropDownButton.Create("Source", src, Icons.Code));
            row.Add(new BoxEl { Grow = 1, MinWidth = 16 });
            // WinUI's "copy a shareable link" button — ours copies the CLI deep-link to this page.
            string deepLink = $"dotnet run --project src/FluentGpu.WindowsApp -- --page {meta.Key}";
            row.Add(Embed.Comp(() => new CopyButton { Text = deepLink, Glyph = Icons.Link, Tip = "Copy page link" }));
            kids.Add(new BoxEl { Direction = 0, Gap = 8, AlignItems = FlexAlign.Center, Wrap = true, Margin = new Edges4(0, 4, 0, 0), Children = row.ToArray() });
            if (meta.ControlSource is { } srcPath) kids.Add(ApiLine(srcPath));
        }

        if (!string.IsNullOrEmpty(description)) kids.Add(Body(description!).Secondary());

        return new BoxEl { Direction = 1, Gap = 8, Children = kids.ToArray() };
    }

    // "FluentGpu.Controls · Button.cs" — the engine namespace + implementing file (WinUI shows the API namespace here).
    static Element ApiLine(string controlSource)
    {
        string file = FileName(controlSource);
        string ns = AssemblyOf(controlSource);
        return new TextEl($"{ns} · {file}") { Size = 12f, Color = Tok.TextTertiary, FontFamily = "Cascadia Code" };
    }

    static string FileName(string path)
    {
        int i = path.LastIndexOf('/');
        return i >= 0 ? path[(i + 1)..] : path;
    }

    static string AssemblyOf(string path)
    {
        // "src/FluentGpu.Controls/Button.cs" → "FluentGpu.Controls"
        var parts = path.Split('/');
        return parts.Length >= 2 ? parts[1] : "FluentGpu";
    }

    static List<MenuFlyoutItem> BuildDocItems(PageMeta meta)
    {
        var items = new List<MenuFlyoutItem>();
        foreach (var d in meta.Docs)
        {
            var uri = d.Uri;   // capture per item (not the loop variable's final value)
            bool external = uri.StartsWith("http", StringComparison.Ordinal);
            items.Add(new(d.Title, external ? Icons.Globe : Icons.Document, true, () => PageInfo.Open(uri)));
        }
        if (meta.WinUiTemplate is not null)
        {
            // Control pages: the learn.microsoft.com links above + the engine's own fidelity/controls docs.
            if (items.Count > 0) items.Add(MenuFlyoutItem.Separator);
            items.Add(new("Guide — building WinUI-faithful controls", Icons.Document, true, () => PageInfo.Open("docs/guide/control-fidelity.md")));
            items.Add(new("Guide — elements, controls & theming", Icons.Document, true, () => PageInfo.Open("docs/guide/components-elements-layout.md")));
            items.Add(new("Spec — controls subsystem", Icons.Document, true, () => PageInfo.Open("design/subsystems/controls.md")));
        }
        return items;
    }

    static List<MenuFlyoutItem> BuildSourceItems(PageMeta meta)
    {
        var items = new List<MenuFlyoutItem>();
        if (meta.ControlSource is { } cs)
            items.Add(new($"Engine implementation — {FileName(cs)}", Icons.Code, true, () => PageInfo.Open(cs)));
        if (meta.SamplePage is { } sp)
            items.Add(new($"This sample page — {FileName(sp)}", Icons.Document, true, () => PageInfo.Open(sp)));
        if (meta.WinUiTemplate is { } tpl)
        {
            if (items.Count > 0) items.Add(MenuFlyoutItem.Separator);
            items.Add(new($"WinUI reference template — {FileName(tpl)}", Icons.Globe, true,
                () => PageInfo.Open(PageInfo.WinUiXamlBlobUrl + tpl)));
        }
        return items;
    }
}

/// <summary>
/// A small copy-to-clipboard button (the WinUI Gallery CopyButton): glyph (+ optional label), writes <see cref="Text"/>
/// through the <c>IClipboard</c> PAL seam, and swaps to a checkmark + "Copied" for ~1.5s. The cooldown rides
/// <c>UseAnimatedValue</c> so the revert needs no timer facility; the extra re-renders are scoped to this component.
/// </summary>
sealed class CopyButton : Component
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
        // Icon-only buttons (Label == null) stay icon-only when copied — swapping in a "Copied" text would grow the
        // button and overflow fixed-width hosts (the Iconography copy rows). Labeled buttons swap their label.
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

/// <summary>The WinUI ItemPage "Related controls" section: hyperlink-style chips that navigate the gallery shell.
/// A component (not a factory) so it can read the navigation context.</summary>
sealed class RelatedLinks : Component
{
    public string[] Keys = [];

    public override Element Render()
    {
        var navigate = UseContext(NavigationView.Nav);
        if (Keys.Length == 0) return new BoxEl();

        var chips = new Element[Keys.Length];
        for (int i = 0; i < Keys.Length; i++)
        {
            var key = Keys[i];
            chips[i] = HyperlinkButton.Create(key, () => navigate(key));
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
