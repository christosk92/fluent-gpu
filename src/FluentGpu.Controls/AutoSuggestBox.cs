using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>
/// A WinUI AutoSuggestBox: an <see cref="EditableText"/> field that shows a filtered list of suggestions directly
/// beneath it as the user types. The panel is rendered <b>inline</b> (a sibling box in the same column), not in an
/// overlay — simpler, and it reflows the page rather than floating. Reading <c>text.Value</c> in <see cref="Render"/>
/// subscribes the component to the input signal, so each keystroke re-renders the component and re-filters the list.
/// Clicking a suggestion writes it back into the field (and, since the query then matches itself, collapses the panel
/// to the single self-match — WinUI behaviour is to dismiss; v1 keeps the inline reflow simple).
/// </summary>
public sealed class AutoSuggestBox : Component
{
    public IReadOnlyList<string> Suggestions = [];
    public string Placeholder = "Search";
    public float Width = 280f;

    public static Element Create(IReadOnlyList<string> suggestions, string placeholder = "Search", float width = 280f)
        => Embed.Comp(() => new AutoSuggestBox { Suggestions = suggestions, Placeholder = placeholder, Width = width });

    public override Element Render()
    {
        var text = UseSignal("");
        var query = text.Value;                       // subscribes → re-render + re-filter on every keystroke

        var matches = new List<string>();
        if (query.Length > 0)
        {
            var q = query.ToLowerInvariant();
            foreach (var s in Suggestions)
                if (s.ToLowerInvariant().Contains(q)) matches.Add(s);
        }

        var kids = new List<Element>
        {
            Embed.Comp(() => new EditableText { Placeholder = Placeholder, Width = Width, Text = text }),
        };

        if (matches.Count > 0)
        {
            var rows = new Element[matches.Count];
            for (int i = 0; i < matches.Count; i++)
            {
                var mm = matches[i];                  // capture per-row for the click closure
                rows[i] = new BoxEl
                {
                    MinHeight = 32f,
                    AlignItems = FlexAlign.Center,
                    Padding = new Edges4(11, 0, 11, 0),
                    Corners = Radii.ControlAll,
                    Margin = new Edges4(4, 2, 4, 2),
                    HoverFill = Tok.FillSubtleSecondary,
                    OnClick = () => { text.Value = mm; },
                    Role = AutomationRole.MenuItem,
                    Children = [new TextEl(mm) { Size = 14f, Color = Tok.TextPrimary }],
                };
            }

            kids.Add(new BoxEl
            {
                Direction = 1,
                Width = Width,
                Margin = new Edges4(0, 4, 0, 0),
                Padding = new Edges4(0, 2, 0, 2),
                Corners = Radii.OverlayAll,
                Fill = Tok.FillLayerDefault,
                BorderColor = Tok.StrokeCardDefault,
                BorderWidth = 1f,
                Shadow = Elevation.Flyout,
                Children = rows,
            });
        }

        return new BoxEl { Direction = 1, Children = kids.ToArray() };
    }
}
