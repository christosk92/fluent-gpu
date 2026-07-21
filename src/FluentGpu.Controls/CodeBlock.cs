using System;
using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static FluentGpu.Dsl.Ui;

namespace FluentGpu.Controls;

/// <summary>
/// A read-only, syntax-tinted C# code block (the graduated gallery <c>CodeText</c> tinter). Theme-aware — it selects a
/// VS-dark or VS-light palette from <see cref="Theme.Dark"/> at render time, so a live theme swap (which re-renders the
/// component via <c>RethemeAll</c>) re-colors the code (the old hardcoded VS-dark constants were invisible on a light
/// page). Indentation is preserved with MEASURED monospace space runs (real space glyphs the text engine advances),
/// not a hardcoded pixel advance, so it stays correct across fonts/DPI. Long lines scroll horizontally; an optional
/// copy affordance writes the source through the clipboard PAL seam. The zero-dependency lexer is a cold path — it runs
/// only when a block is shown, never per frame.
/// </summary>
public sealed class CodeBlock : Component
{
    /// <summary>The source text to display (verbatim; leading/trailing blank lines are trimmed).</summary>
    public string Code = "";
    /// <summary>Show a compact "Copy" affordance above the code (default true). Hosts that provide their own copy
    /// toolbar (e.g. ExampleCard's source expander) set this false.</summary>
    public bool Copyable = true;
    /// <summary>The monospace font size in px.</summary>
    public float FontSize = 12.5f;

    /// <summary>The one canonical factory.</summary>
    public static Element Create(string code, bool copyable = true, float fontSize = 12.5f)
        => Embed.Comp(() => new CodeBlock { Code = code, Copyable = copyable, FontSize = fontSize });

    /// <summary>Convenience alias for <see cref="Create(string, bool, float)"/> — the no-copy inline form.</summary>
    public static Element Of(string code) => Create(code, copyable: false);

    // ── palettes (VS Code default themes) — selected by Theme.Dark at render, so a theme swap re-colors ──────────────
    private readonly record struct Palette(ColorF Plain, ColorF Kw, ColorF Str, ColorF Com, ColorF Num, ColorF Typ, ColorF Meth);

    private static readonly Palette DarkPalette = new(
        Plain: ColorF.FromRgba(0xD4, 0xD4, 0xD4), Kw: ColorF.FromRgba(0x56, 0x9C, 0xD6),
        Str: ColorF.FromRgba(0xCE, 0x91, 0x78), Com: ColorF.FromRgba(0x6A, 0x99, 0x55),
        Num: ColorF.FromRgba(0xB5, 0xCE, 0xA8), Typ: ColorF.FromRgba(0x4E, 0xC9, 0xB0),
        Meth: ColorF.FromRgba(0xDC, 0xDC, 0xAA));

    private static readonly Palette LightPalette = new(
        Plain: ColorF.FromRgba(0x1F, 0x1F, 0x1F), Kw: ColorF.FromRgba(0x00, 0x00, 0xFF),
        Str: ColorF.FromRgba(0xA3, 0x15, 0x15), Com: ColorF.FromRgba(0x00, 0x80, 0x00),
        Num: ColorF.FromRgba(0x09, 0x88, 0x58), Typ: ColorF.FromRgba(0x2B, 0x91, 0xAF),
        Meth: ColorF.FromRgba(0x74, 0x53, 0x1F));

    private const string Font = "Cascadia Code";
    private const float LineH = 18f;

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "using", "static", "var", "new", "true", "false", "null", "return", "void", "int", "float", "double",
        "string", "bool", "char", "sealed", "class", "record", "struct", "public", "private", "internal", "override",
        "readonly", "const", "out", "ref", "in", "is", "not", "and", "or", "if", "else", "switch", "case", "default",
        "for", "foreach", "while", "do", "break", "continue", "this", "base", "with", "get", "set", "init", "params",
        "async", "await", "nameof", "typeof",
    };

    public override Element Render()
    {
        var hooks = UseContext(InputHooks.Current);
        var (copied, setCopied) = UseState(false);
        // Theme.Dark is read here so RethemeAll's re-render re-selects the palette (the theme-swap re-color contract).
        var pal = Theme.Dark ? DarkPalette : LightPalette;

        var block = Block(Code, pal, FontSize);
        if (!Copyable) return block;

        var copyFg = copied ? Tok.AccentDefault : Tok.TextSecondary;
        var copyBtn = new BoxEl
        {
            Direction = 0, Gap = 6, AlignItems = FlexAlign.Center, MinHeight = 28f,
            Padding = new Edges4(8, 4, 8, 4), Corners = Radii.ControlAll,
            Fill = Tok.FillSubtleTransparent, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            Focusable = true, Role = AutomationRole.Button,
            OnClick = () => { hooks.Clipboard?.SetText(Code); setCopied(true); },
            Children =
            [
                Icon(copied ? Icons.Accept : Icons.Copy, 13f).Foreground(copyFg),
                new TextEl(copied ? "Copied" : "Copy") { Size = 12f, Color = copyFg },
            ],
        };
        return new BoxEl
        {
            Direction = 1,
            Children =
            [
                new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Padding = new Edges4(8, 4, 8, 0), Children = [new BoxEl { Grow = 1 }, copyBtn] },
                block,
            ],
        };
    }

    private static Element Block(string code, in Palette pal, float fontSize)
    {
        // A horizontal ScrollEl has no intrinsic height in a column, so compute an explicit viewport height from the
        // line count (row heights + gaps + padding); the inner column keeps its natural overflowing width for the pan.
        var lines = (code ?? "").Replace("\r\n", "\n").Trim('\n').Split('\n');
        var rows = new Element[lines.Length];
        float h = 8f + 14f;
        for (int i = 0; i < lines.Length; i++)
        {
            rows[i] = Line(lines[i], pal, fontSize);
            h += lines[i].Trim().Length == 0 ? 8f : LineH;
            if (i > 0) h += 3f;
        }
        var content = new BoxEl { Direction = 1, Gap = 3f, Padding = new Edges4(16, 8, 16, 14), Children = rows };
        return new ScrollEl { Horizontal = true, Height = h, Content = content };
    }

    private static Element Line(string line, in Palette pal, float fontSize)
    {
        if (line.Trim().Length == 0) return new BoxEl { Height = 8f };

        var runs = new List<Element>();
        int i = 0, n = line.Length;
        while (i < n)
        {
            char c = line[i];
            if (c == ' ')
            {
                int s = i;
                while (i < n && line[i] == ' ') i++;
                // MEASURED space run: real monospace space glyphs the text engine advances (not a hardcoded pixel width).
                runs.Add(Run(new string(' ', i - s), pal.Plain, fontSize));
                continue;
            }
            if (c == '/' && i + 1 < n && line[i + 1] == '/') { runs.Add(Run(line[i..], pal.Com, fontSize)); break; }
            if (c == '"')
            {
                int s = i;
                i++;
                while (i < n && line[i] != '"') { if (line[i] == '\\') i++; i++; }
                if (i < n) i++;
                runs.Add(Run(line[s..i], pal.Str, fontSize));
                continue;
            }
            if (char.IsDigit(c))
            {
                int s = i;
                while (i < n && (char.IsLetterOrDigit(line[i]) || line[i] == '.')) i++;
                runs.Add(Run(line[s..i], pal.Num, fontSize));
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                int s = i;
                while (i < n && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
                string w = line[s..i];
                ColorF col = Keywords.Contains(w) ? pal.Kw
                    : i < n && line[i] == '(' ? pal.Meth
                    : char.IsUpper(w[0]) ? pal.Typ
                    : pal.Plain;
                runs.Add(Run(w, col, fontSize));
                continue;
            }
            int p = i;
            while (i < n && line[i] != ' ' && line[i] != '"' && !char.IsLetterOrDigit(line[i]) && line[i] != '_'
                   && !(line[i] == '/' && i + 1 < n && line[i + 1] == '/')) i++;
            runs.Add(Run(line[p..i], pal.Plain, fontSize));
        }
        return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Children = runs.ToArray() };
    }

    private static TextEl Run(string text, ColorF color, float fontSize)
        => new(text) { Size = fontSize, Color = color, FontFamily = Font };
}
