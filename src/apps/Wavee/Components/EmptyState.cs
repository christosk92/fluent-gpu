using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>Friendly EMPTY state — a doorway, not a dead end: a muted glyph, one line, and an optional primary action.</summary>
public static class EmptyState
{
    public static Element Build(string title, string? subtitle = null, string glyph = Icons.MusicNote,
        string? actionLabel = null, Action? onAction = null)
    {
        var kids = new List<Element>
        {
            Icon(glyph, 32f, Tok.TextTertiary),
            new BoxEl { Height = Spacing.M },
            WaveeType.RailHeader(title),
        };
        if (subtitle is not null) kids.Add(WaveeType.TrackMeta(subtitle));
        if (actionLabel is not null && onAction is not null)
        {
            kids.Add(new BoxEl { Height = Spacing.L });
            kids.Add(Button.Accent(actionLabel, onAction));
        }
        return Centered(kids);
    }

    public static Element Default() => Build(Loc.Get(Strings.Common.EmptyTitle), Loc.Get(Strings.Common.EmptySubtitle));

    internal static Element Centered(List<Element> kids) => new BoxEl
    {
        Direction = 1, Grow = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Gap = Spacing.XS, Padding = Edges4.All(Spacing.XXL), Children = kids.ToArray(),
    };
}
