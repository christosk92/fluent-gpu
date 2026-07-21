using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using static FluentGpu.Dsl.Ui;

// A CUSTOM virtualizing layout: a single column where each row is indented by a repeating step — proof that any
// deterministic, allocation-free geometry plugs straight into the virtualizer (no engine changes).
sealed class StaggerLayout : IVirtualLayout
{
    readonly float _extent, _step;
    public StaggerLayout(float extent, float step) { _extent = extent; _step = step; }
    public float ContentExtent(int n, float cross) => n * _extent;
    public void Window(int n, float cross, float vp, float off, int over, out int first, out int last)
    {
        first = Math.Max(0, (int)MathF.Floor(off / _extent) - over);
        last = Math.Min(n, (int)MathF.Ceiling((off + vp) / _extent) + over);
        if (last < first) last = first;
    }
    public RectF ItemRect(int i, float cross)
    {
        float indent = (i % 6) * _step;
        return new RectF(indent, i * _extent, MathF.Max(40f, cross - indent - 16f), _extent - 6f);
    }
}

// Showcases the ItemsRepeater abstraction: one control, data + template + a pluggable layout (Wrap / Grid / Custom).
[GalleryPage("repeater", "ItemsRepeater", "Fundamentals", Icon = Icons.List)]
sealed class RepeaterPage : Component
{
    static readonly ColorF Grey = ColorF.FromRgba(0x9A, 0x9A, 0x9A);

    public override Element Render() => ScrollView(new BoxEl
    {
        Direction = 1, Gap = 18, Padding = Edges4.All(24),
        Children =
        [
            Heading("ItemsRepeater & custom layouts"),
            Text("One control: data + a template + a pluggable layout. Stack/Grid VIRTUALIZE (only the window realizes, recycled over the slab free-list, 0-alloc steady scroll); Wrap is non-virtual for small sets. Any IVirtualLayout you implement plugs in unchanged.")
                .Foreground(Grey)
                .Wrapped(),

            Label("RepeatLayout.Wrap — non-virtual chips"),
            Repeater.ItemsRepeater(14, Chip, RepeatLayout.Wrap(8f)),

            Label("RepeatLayout.Grid — virtualized 4-column card grid (1,000 items)"),
            new BoxEl { Height = 260, Children = [Repeater.ItemsRepeater(1000, Card, RepeatLayout.Grid(4, 110f, 12f), keyOf: i => "c" + i)] },

            Label("RepeatLayout.Custom(StaggerLayout) — your own geometry (5,000 items)"),
            new BoxEl { Height = 260, Children = [Repeater.ItemsRepeater(5000, Row, RepeatLayout.Custom(new StaggerLayout(44f, 16f)), keyOf: i => "s" + i)] },
        ],
    });

    static Element Label(string s) => new TextEl(s) { Size = 13f, Bold = true, Color = Grey };
    static Element Chip(int i) => new BoxEl { Padding = new Edges4(12, 7, 12, 7), Corners = CornerRadius4.All(14f), Fill = ColorF.FromRgba(0x26, 0x26, 0x2A), Children = [new TextEl("Tag " + i) { Size = 13f }] };
    static Element Card(int i) => new BoxEl { Corners = CornerRadius4.All(8f), Fill = Tint(i), Padding = Edges4.All(8), Children = [new TextEl("#" + i) { Bold = true }] };
    static Element Row(int i) => new BoxEl { Corners = CornerRadius4.All(6f), Fill = Tint(i), AlignItems = FlexAlign.Center, Padding = new Edges4(12, 0, 12, 0), Children = [new TextEl("Item " + i) { Size = 13f }] };
    static ColorF Tint(int i) => ColorF.FromRgba((byte)(50 + (i * 40) % 160), (byte)(60 + (i * 70) % 150), (byte)(90 + (i * 30) % 140));
}
