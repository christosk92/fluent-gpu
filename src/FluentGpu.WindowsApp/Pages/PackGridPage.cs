using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;

// ── The "Pack grid" gallery page ─────────────────────────────────────────────────────────────────────────────────────
// The engine's own look at PackGrid (pinned Home's bento): 500 mixed tiles in six groups inside the page's own scroller,
// a column slider, the tool strip, reorder (pointer, keyboard lift mode), and resize / add / remove / open on the selected
// tile. DEBUG builds print "[pack] fault ..." to stderr for any pack that breaks the bento (PackLayout.Verify).
// Screenshot arms (CLI, read once at mount): --pack-columns N, --pack-strip, --pack-open (opens the third tile of group 1),
// --pack-ink (full rows get a 6 DIP ink outset on the left and their title overhangs it: the ink must show, the layout must not move).
//   FluentGpu.WindowsApp.exe --screenshot out.png --shot page-content:pack-grid --w 1200 --h 900 --pack-columns 4
[GalleryPage("pack-grid", "Pack grid", "Fundamentals", Icon = Icons.Grid)]
sealed class PackGridPage : Component
{
    enum Size : byte { S, M, L, Full }

    sealed record Item(string Id, bool Head, string Title, Size Size, bool Auto, int Group, bool Open = false);

    static readonly ColorF[] GroupFill =
    [
        ColorF.FromRgba(0x2E, 0x5E, 0x9E), ColorF.FromRgba(0x7A, 0x3E, 0x9A), ColorF.FromRgba(0x2E, 0x84, 0x5A),
        ColorF.FromRgba(0x9E, 0x5A, 0x22), ColorF.FromRgba(0x8A, 0x2E, 0x4A), ColorF.FromRgba(0x3A, 0x6E, 0x7E),
    ];

    readonly Signal<IReadOnlyList<Item>> _items = new(Build());
    readonly FloatSignal _columns = new(4f);
    readonly Signal<bool> _strip = new(false);
    readonly Signal<bool> _reorderOn = new(true);
    readonly Signal<string?> _selected = new(null);
    readonly Signal<string> _said = new("");
    readonly PackGridController _grid = new();
    readonly PackReorder<Item> _reorder;
    int _nextId = 1000;
    bool _argsRead;
    bool _ink;

    public PackGridPage()
    {
        _reorder = new PackReorder<Item>("pack-demo")
        {
            CanLift = it => !it.Head,
            CanLandAt = (_, at) => at > 0,   // never above the first group head
            Enabled = () => _reorderOn.Value,
            Keyboard = PackKeyboard.LiftMode,
            Slot = r => new BoxEl
            {
                Grow = 1f, Corners = CornerRadius4.All(8f), BorderWidth = 2f,
                BorderColor = Tok.AccentDefault, Fill = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x10),
            },
            OnCommit = m => Move(m.From, m.To),
            AnnounceText = a => a.Kind switch
            {
                PackAnnounceKind.Grab => $"Grabbed {a.Key}, position {a.Position} of {a.Count}.",
                PackAnnounceKind.Move => $"{a.Key}, position {a.Position} of {a.Count}.",
                PackAnnounceKind.Drop => $"Dropped {a.Key}, position {a.Position} of {a.Count}.",
                PackAnnounceKind.Cancel => $"Move cancelled. {a.Key} is back.",
                _ => $"{a.Key} is at the {(a.Position <= 1 ? "start" : "end")}.",
            },
        };
    }

    static IReadOnlyList<Item> Build()
    {
        var list = new List<Item>(506);
        uint seed = 0x9E3779B9u;
        uint Next() { seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5; return seed; }
        int id = 0;
        for (int g = 0; g < 6; g++)
        {
            list.Add(new Item($"g{g}", true, $"Group {g + 1}", Size.S, false, g));
            for (int k = 0; k < 83 + (g < 2 ? 1 : 0); k++)
            {
                uint r = Next() % 100;
                var size = r < 45 ? Size.S : r < 75 ? Size.M : r < 93 ? Size.L : Size.Full;
                bool auto = Next() % 3 != 0;
                list.Add(new Item($"t{id}", false, $"Tile {id}", size, auto, g));
                id++;
            }
        }
        return list;
    }

    void ReadArgs()
    {
        if (_argsRead) return;
        _argsRead = true;
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--pack-columns" && i + 1 < args.Length && int.TryParse(args[i + 1], out int c)) _columns.Value = Math.Clamp(c, 1, 12);
            if (args[i] == "--pack-strip") _strip.Value = true;
            if (args[i] == "--pack-ink") _ink = true;
            if (args[i] == "--pack-open") Update(list => SetAt(list, IndexOfFirst(list, 0) + 2, it => it with { Open = true }));
        }
    }

    static int IndexOfFirst(IReadOnlyList<Item> list, int group)
    {
        for (int i = 0; i < list.Count; i++) if (!list[i].Head && list[i].Group == group) return i;
        return -1;
    }

    static PackEntry EntryOf(Item it) => it.Head
        ? new PackEntry(PackEntryKind.GroupHead, Extent: 44f)
        : it.Size switch
        {
            Size.S => new PackEntry(PackEntryKind.Tile, 1, 1, Flags(it)),
            Size.M => new PackEntry(PackEntryKind.Tile, 1, 3, Flags(it), Extent: it.Open ? 300f : 0f, OpenMinCols: 2),
            Size.L => new PackEntry(PackEntryKind.Tile, 2, 3, Flags(it), Extent: it.Open ? 300f : 0f, OpenMinCols: 2),
            _ => new PackEntry(PackEntryKind.Band, 1, 2, Flags(it), Extent: it.Open ? 300f : 0f),
        };

    static PackFlags Flags(Item it) => (it.Auto ? PackFlags.Auto : PackFlags.None) | (it.Open ? PackFlags.Open : PackFlags.None);

    PackMetrics Metrics() => new((int)MathF.Round(_columns.Value), 72f, 12f, 28f, _strip.Value ? 40f : 0f, 2);

    void Update(Func<IReadOnlyList<Item>, IReadOnlyList<Item>> f) => _items.Value = f(_items.Peek());

    static IReadOnlyList<Item> SetAt(IReadOnlyList<Item> list, int i, Func<Item, Item> f)
    {
        if (i < 0) return list;
        var copy = new List<Item>(list);
        copy[i] = f(copy[i]);
        return copy;
    }

    void Move(int from, int to) => Update(list =>
    {
        var copy = new List<Item>(list);
        var it = copy[from];
        copy.RemoveAt(from);
        copy.Insert(to, it);
        return copy;
    });

    int SelectedIndex()
    {
        var list = _items.Peek();
        string? key = _selected.Peek();
        for (int i = 0; i < list.Count; i++) if (list[i].Id == key) return i;
        return -1;
    }

    void Resize() => Update(list => SetAt(list, SelectedIndex(), it => it with { Size = (Size)(((int)it.Size + 1) % 4), Auto = false }));
    void ToggleOpen() => Update(list => SetAt(list, SelectedIndex(), it => it with { Open = !it.Open }));
    void Remove()
    {
        int i = SelectedIndex();
        if (i < 0) return;
        Update(list => { var c = new List<Item>(list); c.RemoveAt(i); return c; });
        _selected.Value = null;
    }
    void Add()
    {
        int i = SelectedIndex();
        var list = _items.Peek();
        int at = i >= 0 ? i + 1 : Math.Max(1, IndexOfFirst(list, 0));
        int g = i >= 0 ? list[i].Group : 0;
        var it = new Item($"n{_nextId}", false, $"New {_nextId}", Size.M, true, g);
        _nextId++;
        Update(l => { var c = new List<Item>(l); c.Insert(at, it); return c; });
        _selected.Value = it.Id;
    }

    Element Tile(Item it, PackCell cell)
    {
        if (it.Head)
            return new BoxEl
            {
                Grow = 1f, Direction = 1, Justify = FlexJustify.End, Padding = new Edges4(4, 0, 0, 8),
                Children = [new TextEl(it.Title) { Size = 20f, Weight = 600, Color = Tok.TextPrimary }],
            };
        return Embed.Comp(new TileFace.Props(it, cell, _selected, GroupFill[it.Group % GroupFill.Length], _ink && it.Size == Size.Full), () => new TileFace());
    }

    public override Element Render()
    {
        ReadArgs();
        var grid = PackGrid.Create<Item>(_items, Tile, new PackGridOptions<Item>
        {
            KeyOf = it => it.Id,
            EntryOf = EntryOf,
            Metrics = Metrics,
            Controller = _grid,
            Reorder = _reorder,
            Key = "pack-demo:grid",
            InkOutset = _ink ? static it => it.Size == Size.Full && !it.Head ? new Edges4(6f, 0f, 0f, 0f) : default : null,
        });
        var bar = new BoxEl
        {
            Direction = 0, Gap = 12f, AlignItems = FlexAlign.Center, Padding = new Edges4(0, 0, 0, 12),
            Children =
            [
                Slider.Create(_columns, v => _columns.Value = MathF.Round(Math.Clamp(v, 1f, 12f)), length: 160f,
                    options: new Slider.SliderOptions { Header = "columns", Min = 1f, Max = 12f, Step = 1f }),
                ToggleSwitch.Create(_strip, header: "tool strip"),
                ToggleSwitch.Create(_reorderOn, header: "reorder"),
                Button.Create("Resize", Resize),
                Button.Create("Add", Add),
                Button.Create("Remove", Remove),
                Button.Create("Open", ToggleOpen),
                new TextEl("") { Text = Prop.Of(() => _selected.Value is { } s ? $"selected {s}" : "click a tile"), Size = 13f, Color = Tok.TextSecondary },
            ],
        };
        return new ScrollEl
        {
            Grow = 1f,
            Content = new BoxEl
            {
                Direction = 1, Padding = new Edges4(24, 16, 24, 24),
                Children = [bar, grid],
            },
        };
    }

    sealed class TileFace : Component
    {
        public sealed record Props(Item Item, PackCell Cell, Signal<string?> Selected, ColorF Fill, bool Overhang = false);

        public override Element Render()
        {
            var p = UseProps<Props>();
            var box = p.Cell.Box.Value;
            var phase = p.Cell.Phase.Value;
            bool sel = p.Selected.Value == p.Item.Id;
            var it = p.Item;
            var face = new BoxEl
            {
                Grow = 1f, Direction = 1, Gap = 4f, Padding = Edges4.All(10f),
                Corners = CornerRadius4.All(8f), Fill = p.Fill, HoverFill = p.Fill, PressedFill = p.Fill,
                BorderWidth = sel ? 2f : 0f, BorderColor = Tok.TextPrimary,
                OnClick = () => p.Selected.Value = it.Id,
                Children =
                [
                    new TextEl(it.Title) { Size = 14f, Weight = 600, Color = ColorF.FromRgba(0xFF, 0xFF, 0xFF), MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
                        Margin = p.Overhang ? new Edges4(-14f, 0f, 0f, 0f) : default },   // --pack-ink: the title reaches 4 DIP past the tile's left edge
                    new TextEl($"{it.Size}{(it.Auto ? " auto" : "")} · {box.Cols}x{box.Rows:0.##}{(it.Open ? " · open" : "")}{(phase != PackItemPhase.Rest ? " · " + phase : "")}")
                        { Size = 12f, Color = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0xC0), MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f },
                ],
            };
            if (box.ToolStrip <= 0f) return face;
            return new BoxEl
            {
                Grow = 1f, Direction = 1,
                Children =
                [
                    new BoxEl
                    {
                        Height = box.ToolStrip, Shrink = 0f, Direction = 0, Gap = 6f, AlignItems = FlexAlign.Center,
                        Children = [Chip("size"), Chip("hide")],
                    },
                    face,
                ],
            };
        }

        static Element Chip(string text) => new BoxEl
        {
            Height = 28f, Padding = new Edges4(10, 0, 10, 0), Corners = CornerRadius4.All(14f), AlignItems = FlexAlign.Center,
            Fill = Tok.FillControlDefault, Children = [new TextEl(text) { Size = 12f, Color = Tok.TextPrimary }],
        };
    }
}
