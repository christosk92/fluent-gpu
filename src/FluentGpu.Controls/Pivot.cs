using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;

namespace FluentGpu.Controls;

/// <summary>A WinUI Pivot: a row of large (24px) text headers above a content region. Selection is shown by a 3px accent
/// underline pipe at the bottom (NOT bold weight): the selected header is <see cref="Tok.TextPrimary"/>, the rest are
/// <see cref="Tok.TextSecondary"/>. Clicking a header swaps the content shown below. Owns its own selection state.
/// Per-part restyling goes through <see cref="Parts"/> (see <see cref="TemplateParts"/> for the contract).</summary>
public sealed class Pivot : Component
{
    // Template parts (the WinUI x:Name vocabulary; see TemplateParts). Each part's doc lists the props the control
    // OWNS (re-asserted after any modifier — a Parts customization cannot win those).
    /// <summary>Each clickable header box (WinUI PivotHeaderItem). The SAME modifier runs for every header — branch
    /// on app state for per-header styling. Owned: OnClick (select), Role.</summary>
    public const string PartHeaderItem = "HeaderItem";
    /// <summary>The 3px selection pipe under each header (WinUI SelectedPipe; rendered transparent when unselected).
    /// Owned: nothing — pure styling.</summary>
    public const string PartPipe = "Pipe";
    /// <summary>The content region below the headers. Owned: Children.</summary>
    public const string PartContent = "Content";

    public IReadOnlyList<string> Headers = [];
    /// <summary>Lightweight per-part styling (CSS ::part): modifiers keyed by the <c>PartXxx</c> consts; see
    /// <see cref="TemplateParts"/> for the contract.</summary>
    public TemplateParts? Parts;

    public static Element Create(IReadOnlyList<string> headers)
        => Embed.Comp(() => new Pivot { Headers = headers });

    public override Element Render()
    {
        var (sel, setSel) = UseState(0);

        var count = Headers.Count;
        if (count == 0)
            return new BoxEl { Direction = 1, Grow = 1f };

        int selected = sel < 0 ? 0 : (sel >= count ? count - 1 : sel);

        var headerItems = new Element[count];
        for (int i = 0; i < count; i++)
        {
            int index = i;
            bool isSelected = index == selected;
            Action select = () => setSel(index);

            // WinUI PivotHeaderItem SelectedPipe: Height=3, HorizontalAlignment=Stretch (full header width),
            // CornerRadius=PivotHeaderItemSelectedPipeCornerRadius=1.5, Margin="0,0,0,2" (no right inset).
            var pipe = new BoxEl
            {
                AlignSelf = FlexAlign.Stretch,   // stretch to full header width (was fixed 24f)
                Height = 3f,
                Corners = Radii.Circle(3f),      // diameter 3 → radius 1.5 (PivotHeaderItemSelectedPipeCornerRadius)
                Fill = isSelected ? Tok.AccentDefault : ColorF.Transparent,
                Margin = new Edges4(0, 0, 0, 2), // was (0,4,0,2); WinUI margin "0,0,0,2"
            };

            var item = new BoxEl
            {
                Direction = 1,
                Height = 48f,
                AlignItems = FlexAlign.Center,
                Justify = FlexJustify.Center,
                Padding = new Edges4(12, 0, 12, 0),
                Corners = Radii.ControlAll,
                Role = AutomationRole.Tab,
                OnClick = select,
                Children =
                [
                    new TextEl(Headers[index])
                    {
                        Size = 24f,
                        Color = isSelected ? Tok.TextPrimary : Tok.TextSecondary,
                    },
                    Parts.Apply(PartPipe, pipe),
                ],
            };
            // Parts: restyle anything; the select mechanics always win.
            headerItems[index] = Parts.Apply(PartHeaderItem, item) with { OnClick = select, Role = AutomationRole.Tab };
        }

        var content = new BoxEl
        {
            Direction = 1,
            Grow = 1f,
            Padding = Edges4.All(8),
            Children =
            [
                new TextEl($"Content for {Headers[selected]}")
                {
                    Size = 14f,
                    Color = Tok.TextPrimary,
                },
            ],
        };
        content = Parts.Apply(PartContent, content) with { Children = content.Children };

        return new BoxEl
        {
            Direction = 1,
            Grow = 1f,
            Children =
            [
                new BoxEl
                {
                    Direction = 0,
                    Gap = 4f,
                    AlignItems = FlexAlign.End,
                    Padding = new Edges4(0, 0, 0, 0),
                    Children = headerItems,
                },
                content,
            ],
        };
    }
}
