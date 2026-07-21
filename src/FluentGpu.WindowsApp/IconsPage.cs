using System;
using System.Collections.Generic;
using System.IO;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static FluentGpu.Dsl.Ui;

// ── Iconography (WinUI Gallery IconographyPage parity) ─────────────────────────────────────────────────────────────
// The full Segoe Fluent Icons catalog (1,500+ glyphs, bundled as assets/IconsData.tsv by
// scripts/extract-gallery-metadata.ps1), searchable by name / tag / codepoint, in a row-virtualized grid with a
// detail pane offering copy-as-glyph / codepoint / C# actions. Font-family demos live on the Typography page.

readonly record struct IconInfo(string Code, string Name, string[] Tags)
{
    public string Glyph => char.ConvertFromUtf32(Convert.ToInt32(Code, 16));
}

[GalleryPage("icons", "Iconography", "Design")]
[Route("icons")]
sealed class IconsPage : Component
{
    static IconInfo[]? _all;

    static IconInfo[] All => _all ??= Load();

    static IconInfo[] Load()
    {
        var path = Assets.Path("IconsData.tsv");
        if (!File.Exists(path)) return [];
        var lines = File.ReadAllLines(path);
        var list = new List<IconInfo>(lines.Length);
        foreach (var line in lines)
        {
            var parts = line.Split('\t');
            if (parts.Length < 2 || parts[0].Length == 0) continue;
            var tags = parts.Length >= 3 && parts[2].Length > 0 ? parts[2].Split(',') : [];
            list.Add(new IconInfo(parts[0], parts[1], tags));
        }
        return list.ToArray();
    }

    static IconInfo[] Filter(string query)
    {
        if (query.Length == 0) return All;
        var hits = new List<IconInfo>();
        foreach (var icon in All)
        {
            bool match = icon.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                      || icon.Code.Contains(query, StringComparison.OrdinalIgnoreCase);
            if (!match)
                foreach (var t in icon.Tags)
                    if (t.Contains(query, StringComparison.OrdinalIgnoreCase)) { match = true; break; }
            if (match) hits.Add(icon);
        }
        return hits.ToArray();
    }

    public override Element Render()
    {
        var (query, setQuery) = UseState("");
        var (selectedCode, setSelected) = UseState("E700");
        var viewport = UseContext(Viewport.Size);

        var filtered = Filter(query);

        // Selected icon (falls back to the catalog head if the data file is missing).
        IconInfo selected = All.Length > 0 ? All[0] : new IconInfo("E700", "GlobalNavButton", []);
        foreach (var icon in All) { if (icon.Code == selectedCode) { selected = icon; break; } }

        // Adapt the column count to the window (nav pane ≈ 320 + detail pane 320 + paddings).
        int columns = Math.Clamp((int)((viewport.Width - 720f) / 104f), 4, 12);

        var grid = Virtual.Grid(filtered.Length, columns, 92f, 8f,
            i => Tile(filtered[i], filtered[i].Code == selectedCode, setSelected),
            keyOf: i => filtered[i].Code);

        return new BoxEl
        {
            Direction = 1, Gap = 12f, Padding = Edges4.All(28), Grow = 1f,
            Children =
            [
                PageInfo.HeaderFor("Iconography", null, PageInfo.Find("icons")),
                Body("Segoe Fluent Icons is the standard glyph font for Windows apps. Search the full catalog by name, tag, or codepoint; select a glyph for copyable usage snippets.").Secondary(),
                new BoxEl
                {
                    Direction = 0, Gap = 12f, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        TextBox.Create(onChange: setQuery, options: new TextBox.TextBoxOptions { Placeholder = "Search icons by name, tag, or codepoint", Width = 320f }),
                        Caption($"{filtered.Length} / {All.Length} icons").Tertiary(),
                    ],
                },
                new BoxEl
                {
                    Direction = 0, Gap = 16f, Grow = 1f, AlignItems = FlexAlign.Stretch,
                    Children =
                    [
                        new BoxEl { Grow = 1f, ClipToBounds = true, Children = [grid] },
                        DetailPane(selected),
                    ],
                },
            ],
        };
    }

    static Element Tile(IconInfo icon, bool selected, Action<string> onSelect) => new BoxEl
    {
        Direction = 1, Gap = 6f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Margin = new Edges4(0, 0, 8, 0), Padding = new Edges4(4, 8, 4, 8), Corners = Radii.ControlAll,
        Fill = selected ? Tok.AccentSubtle : Tok.FillCardDefault,
        BorderColor = selected ? Tok.AccentDefault : Tok.StrokeCardDefault, BorderWidth = 1f,
        HoverFill = selected ? Tok.AccentSubtle : Tok.FillCardSecondary, PressedFill = Tok.FillSubtleTertiary,
        OnClick = () => onSelect(icon.Code),
        Focusable = true, Role = AutomationRole.Button,
        Children =
        [
            Icon(icon.Glyph, 22f).Foreground(selected ? Tok.AccentDefault : Tok.TextPrimary),
            new TextEl(icon.Name) { Size = 10.5f, Color = Tok.TextSecondary, MaxLines = 1 },
        ],
    };

    static Element DetailPane(IconInfo icon) => new BoxEl
    {
        Width = 320f, Direction = 1, Gap = 14f, Padding = Edges4.All(16),
        Fill = Tok.FillCardDefault, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, Corners = Radii.OverlayAll,
        Children =
        [
            Subtitle(icon.Name),
            new BoxEl
            {
                Height = 96f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Fill = Tok.FillSolidBase, Corners = Radii.ControlAll, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
                Children = [Icon(icon.Glyph, 48f)],
            },
            CopyRow("Glyph", icon.Glyph, icon.Glyph, mono: false),
            CopyRow("Codepoint", "U+" + icon.Code, icon.Code, mono: true),
            CopyRow("C#", $"Ui.Icon(\"\\u{icon.Code}\")", $"Ui.Icon(\"\\u{icon.Code}\")", mono: true),
            CopyRow("XAML entity", $"&#x{icon.Code};", $"&#x{icon.Code};", mono: true),
            TagChips(icon.Tags),
        ],
    };

    static Element CopyRow(string label, string display, string copyText, bool mono)
    {
        var text = copyText;
        return new BoxEl
        {
            Direction = 0, Gap = 8f, AlignItems = FlexAlign.Center, MinHeight = 32f,
            Padding = new Edges4(10, 2, 4, 2), Corners = Radii.ControlAll,
            Fill = Tok.FillControlDefault, BorderColor = Tok.StrokeControlDefault, BorderWidth = 1f,
            Children =
            [
                new BoxEl { Width = 78f, Children = [Caption(label).Tertiary()] },
                mono
                    ? new TextEl(display) { Size = 12.5f, Color = Tok.TextPrimary, FontFamily = "Cascadia Code", Grow = 1f, MaxLines = 1 }
                    : new TextEl(display) { Size = 14f, Color = Tok.TextPrimary, FontFamily = Theme.IconFont, Grow = 1f },
                Embed.Comp(() => new CopyButton { Text = text }) with { Key = label + text },
            ],
        };
    }

    static Element TagChips(string[] tags)
    {
        if (tags.Length == 0) return new BoxEl();
        var chips = new Element[tags.Length];
        for (int i = 0; i < tags.Length; i++)
            chips[i] = new BoxEl
            {
                Padding = new Edges4(8, 3, 8, 3), Corners = Radii.PillAll, Fill = Tok.FillSubtleSecondary,
                Children = [new TextEl(tags[i]) { Size = 11f, Color = Tok.TextSecondary }],
            };
        return VStack(8f, Caption("Tags").Tertiary(), new BoxEl { Direction = 0, Wrap = true, Gap = 6f, Children = chips });
    }
}
