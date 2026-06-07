using System;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Reconciler;
using static FluentGpu.Dsl.Ui;

// Capability-gallery feature pages (authored in parallel by a workflow, assembled here).


// ===== TypographyPage =====
sealed class TypographyPage : Component
{
    static readonly ColorF Grey = ColorF.FromRgba(0x9A, 0x9A, 0x9A);
    static readonly ColorF Card = ColorF.FromRgba(0x24, 0x24, 0x28);
    static readonly ColorF CardBorder = ColorF.FromRgba(0x3A, 0x3A, 0x40);

    public override Element Render()
    {
        return ScrollView(
            new BoxEl
            {
                Direction = 1,
                Gap = 20f,
                Padding = Edges4.All(24),
                Grow = 1f,
                Children =
                [
                    Heading("Typography"),
                    Text("A type scale built on DirectWrite glyph runs. Sizes, weights, and colors below are all driven by the same Text/TextEl primitives the engine renders on the GPU.")
                        .Foreground(Grey)
                        .FontSize(15f),

                    SizeScaleCard(),
                    WeightsCard(),
                    ColorsCard(),
                    ParagraphCard()
                ]
            });
    }

    Element Label(string s) =>
        new TextEl(s) { Size = 12f, Color = Grey };

    Element Section(string title, params Element[] body)
    {
        var children = new Element[body.Length + 1];
        children[0] = new TextEl(title) { Size = 13f, Bold = true, Color = Grey };
        for (int i = 0; i < body.Length; i++) children[i + 1] = body[i];

        return new BoxEl
        {
            Direction = 1,
            Gap = 16f,
            Padding = Edges4.All(20),
            Fill = Card,
            BorderColor = CardBorder,
            BorderWidth = 1f,
            Corners = CornerRadius4.All(10f),
            Children = children,
        };
    }

    Element ScaleRow(string label, float size, string sample, bool bold = false)
    {
        var t = new TextEl(sample) { Size = size, Bold = bold, Color = Theme.WindowText };
        return new BoxEl
        {
            Direction = 0,
            Gap = 16f,
            AlignItems = FlexAlign.Center,
            Children =
            [
                new BoxEl
                {
                    Direction = 1,
                    Width = 110f,
                    Justify = FlexJustify.Center,
                    Children = [Label(label)]
                },
                t
            ]
        };
    }

    Element SizeScaleCard() =>
        Section("Type scale",
            ScaleRow("40px Display", 40f, "Aa Display", bold: true),
            ScaleRow("28px Heading", 28f, "Aa Heading", bold: true),
            ScaleRow("20px Title", 20f, "Aa Title"),
            ScaleRow("16px Subtitle", 16f, "Aa Subtitle"),
            ScaleRow("14px Body", 14f, "Aa Body text"),
            ScaleRow("12px Caption", 12f, "Aa Caption text"));

    Element WeightsCard() =>
        Section("Weights",
            new BoxEl
            {
                Direction = 0,
                Gap = 40f,
                Wrap = true,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1,
                        Gap = 6f,
                        Children =
                        [
                            Label("Regular"),
                            new TextEl("The quick brown fox") { Size = 18f, Color = Theme.WindowText }
                        ]
                    },
                    new BoxEl
                    {
                        Direction = 1,
                        Gap = 6f,
                        Children =
                        [
                            Label("Semibold"),
                            new TextEl("The quick brown fox") { Size = 18f, Color = Theme.WindowText }.Strong()
                        ]
                    }
                ]
            });

    Element ColorsCard() =>
        Section("Colors",
            new BoxEl
            {
                Direction = 0,
                Gap = 40f,
                Wrap = true,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1,
                        Gap = 6f,
                        Children =
                        [
                            Label("WindowText"),
                            new TextEl("Primary text") { Size = 18f, Color = Theme.WindowText }
                        ]
                    },
                    new BoxEl
                    {
                        Direction = 1,
                        Gap = 6f,
                        Children =
                        [
                            Label("Accent"),
                            Text("Accent text").Foreground(Theme.Accent).FontSize(18f)
                        ]
                    },
                    new BoxEl
                    {
                        Direction = 1,
                        Gap = 6f,
                        Children =
                        [
                            Label("Muted"),
                            Text("Secondary grey").Foreground(Grey).FontSize(18f)
                        ]
                    }
                ]
            });

    Element ParagraphCard() =>
        Section("Body copy",
            new BoxEl
            {
                Direction = 1,
                Gap = 8f,
                MaxWidth = 620f,
                Children =
                [
                    Text("Lorem ipsum dolor sit amet, consectetur adipiscing elit. Sed do eiusmod")
                        .Foreground(Theme.WindowText).FontSize(15f),
                    Text("tempor incididunt ut labore et dolore magna aliqua. Ut enim ad minim veniam,")
                        .Foreground(Theme.WindowText).FontSize(15f),
                    Text("quis nostrud exercitation ullamco laboris nisi ut aliquip ex ea commodo")
                        .Foreground(Theme.WindowText).FontSize(15f),
                    Text("consequat. Duis aute irure dolor in reprehenderit in voluptate velit esse")
                        .Foreground(Theme.WindowText).FontSize(15f),
                    Text("cillum dolore eu fugiat nulla pariatur. Excepteur sint occaecat cupidatat")
                        .Foreground(Theme.WindowText).FontSize(15f),
                    Text("non proident, sunt in culpa qui officia deserunt mollit anim id est laborum.")
                        .Foreground(Grey).FontSize(15f)
                ]
            });
}

// ===== ButtonsPage =====
sealed class ButtonsPage : Component
{
    public override Element Render()
    {
        var (count, setCount) = UseState(0);
        var (shuffle, setShuffle) = UseState(false);

        var greenStyle = Button.AccentStyle with
        {
            Background = ColorF.FromRgba(0x10, 0x7C, 0x10),
            Foreground = ColorF.FromRgba(255, 255, 255),
            CornerRadius = 6f,
            HoverBackground = ColorF.FromRgba(0x16, 0x95, 0x16),
        };

        return ScrollView(
            new BoxEl
            {
                Direction = 1,
                Gap = 16f,
                Padding = Edges4.All(24),
                Grow = 1f,
                Children =
                [
                    Heading("Buttons & commands"),
                    Text("Buttons trigger actions. fluent-gpu ships accent, standard, icon, and toggle variants; hover and press visual states are handled automatically by the renderer.")
                        .Foreground(Theme.WindowText),

                    // ---- Primary / Standard / custom Save ----
                    SectionLabel("Button variants"),
                    Card(
                        new BoxEl
                        {
                            Direction = 0,
                            Gap = 12f,
                            AlignItems = FlexAlign.Center,
                            Wrap = true,
                            Children =
                            [
                                Button.Accent("Primary", () => { }),
                                Button.Standard("Standard", () => { }),
                                Button.Accent("Save", () => { }, greenStyle)
                            ],
                        }),

                    // ---- Icon buttons ----
                    SectionLabel("Icon buttons"),
                    Card(
                        new BoxEl
                        {
                            Direction = 0,
                            Gap = 8f,
                            AlignItems = FlexAlign.Center,
                            Children =
                            [
                                IconButton.Create(Icons.Previous, () => { }),
                                IconButton.Create(Icons.Play, () => { }, IconButton.DefaultStyle with { Size = 44f, Foreground = Theme.Accent }),
                                IconButton.Create(Icons.Next, () => { }),
                                IconButton.Create(Icons.Heart, () => { }, IconButton.DefaultStyle with { Size = 36f, Foreground = ColorF.FromRgba(0xE0, 0x4A, 0x5A) }),
                                IconButton.Create(Icons.More, () => { })
                            ],
                        }),

                    // ---- Toggle ----
                    SectionLabel("Toggle button (controlled)"),
                    Card(
                        new BoxEl
                        {
                            Direction = 0,
                            Gap = 12f,
                            AlignItems = FlexAlign.Center,
                            Children =
                            [
                                ToggleButton.Create("Shuffle", shuffle, () => setShuffle(!shuffle)),
                                new TextEl(shuffle ? "Shuffle is ON" : "Shuffle is OFF")
                                    .FontSize(14f)
                                    .Foreground(shuffle ? Theme.Accent : Theme.WindowText)
                            ],
                        }),

                    // ---- Live counter ----
                    SectionLabel("Live counter (UseState)"),
                    Card(
                        new BoxEl
                        {
                            Direction = 1,
                            Gap = 12f,
                            Children =
                            [
                                Heading($"Count: {count}"),
                                new BoxEl
                                {
                                    Direction = 0,
                                    Gap = 8f,
                                    AlignItems = FlexAlign.Center,
                                    Children =
                                    [
                                        Button.Accent("−", () => setCount(count - 1)),
                                        Button.Accent("+", () => setCount(count + 1)),
                                        Button.Standard("Reset", () => setCount(0))
                                    ],
                                }
                            ],
                        }),

                    new TextEl("Note: hover and press visual states are automatic — every button reacts to pointer enter/leave and pointer down without extra wiring.")
                        .FontSize(13f)
                        .Foreground(Theme.ControlText)
                ],
            });
    }

    Element SectionLabel(string text) =>
        new TextEl(text) { Size = 16f, Bold = true, Color = Theme.WindowText };

    Element Card(Element child) =>
        new BoxEl
        {
            Direction = 1,
            Gap = 8f,
            Padding = Edges4.All(16),
            Fill = Theme.ControlFill,
            BorderColor = Theme.ControlBorder,
            BorderWidth = 1f,
            Corners = CornerRadius4.All(8f),
            Children = [child],
        };
}

// ===== InputsPage =====
sealed class InputsPage : Component
{
    static string FormatTime(float fraction, int totalSeconds)
    {
        int cur = (int)(fraction * totalSeconds);
        int mm = cur / 60;
        int ss = cur % 60;
        return mm.ToString() + ":" + (ss < 10 ? "0" + ss.ToString() : ss.ToString());
    }

    Element DemoBlock(string label, string hint, Element body)
    {
        return new BoxEl
        {
            Direction = 1,
            Gap = 10f,
            Padding = Edges4.All(18f),
            Fill = Theme.ControlFill,
            BorderColor = Theme.ControlBorder,
            BorderWidth = 1f,
            Corners = CornerRadius4.All(10f),
            Children =
            [
                new TextEl(label) { Size = 16f, Bold = true, Color = Theme.WindowText },
                new TextEl(hint) { Size = 12.5f, Color = Theme.ControlText with { A = 0.7f } },
                body
            ],
        };
    }

    Element SectionLabel(string text, float width)
    {
        return new BoxEl
        {
            Width = width,
            AlignItems = FlexAlign.Center,
            Children =
            [
                new TextEl(text) { Size = 14f, Bold = true, Color = Theme.WindowText }
            ],
        };
    }

    public override Element Render()
    {
        var (vol, setVol) = UseState(0.6f);
        var (seek, setSeek) = UseState(0.3f);
        var (shuffle, setShuffle) = UseState(false);
        var (repeat, setRepeat) = UseState(true);
        var (pos, setPos) = UseState(0f);

        const int trackSeconds = 214; // ~3:34 song

        var volumeDemo = DemoBlock(
            "Volume",
            "A controlled slider bound to UseState; the percentage label updates live.",
            new BoxEl
            {
                Direction = 0,
                Gap = 14f,
                AlignItems = FlexAlign.Center,
                Children =
                [
                    SectionLabel("Volume", 70f),
                    Slider.Create(vol, setVol, 260f),
                    new BoxEl
                    {
                        Width = 52f,
                        AlignItems = FlexAlign.End,
                        Children =
                        [
                            new TextEl(((int)(vol * 100f)).ToString() + "%")
                            {
                                Size = 14f,
                                Bold = true,
                                Color = Theme.Accent,
                            }
                        ],
                    }
                ],
            });

        var seekDemo = DemoBlock(
            "Seek / scrubber",
            "Same slider primitive used as a playback scrubber with mm:ss readouts.",
            new BoxEl
            {
                Direction = 0,
                Gap = 14f,
                AlignItems = FlexAlign.Center,
                Children =
                [
                    new BoxEl
                    {
                        Width = 52f,
                        Children =
                        [
                            new TextEl(FormatTime(seek, trackSeconds))
                            {
                                Size = 13f,
                                Color = Theme.ControlText,
                            }
                        ],
                    },
                    Slider.Create(seek, setSeek, 240f),
                    new BoxEl
                    {
                        Width = 52f,
                        AlignItems = FlexAlign.End,
                        Children =
                        [
                            new TextEl(FormatTime(1f, trackSeconds))
                            {
                                Size = 13f,
                                Color = Theme.ControlText with { A = 0.6f },
                            }
                        ],
                    }
                ],
            });

        var toggleDemo = DemoBlock(
            "Toggle buttons",
            "Controlled ToggleButtons: the caller owns each on/off flag via UseState.",
            new BoxEl
            {
                Direction = 0,
                Gap = 12f,
                AlignItems = FlexAlign.Center,
                Children =
                [
                    ToggleButton.Create("Shuffle", shuffle, () => setShuffle(!shuffle)),
                    ToggleButton.Create("Repeat", repeat, () => setRepeat(!repeat)),
                    new BoxEl
                    {
                        Children =
                        [
                            new TextEl(
                                "shuffle=" + (shuffle ? "on" : "off") +
                                "  repeat=" + (repeat ? "on" : "off"))
                            {
                                Size = 12.5f,
                                Color = Theme.ControlText with { A = 0.7f },
                            }
                        ],
                    }
                ],
            });

        var scrollBarDemo = DemoBlock(
            "ScrollBar",
            "A standalone vertical ScrollBar thumb (30% page) reporting normalized position.",
            new BoxEl
            {
                Direction = 0,
                Gap = 18f,
                AlignItems = FlexAlign.Center,
                Children =
                [
                    ScrollBar.Create(0.3f, pos, setPos, 160f, ScrollBar.DefaultStyle with { ThumbWidth = 10f }),
                    new BoxEl
                    {
                        Children =
                        [
                            new TextEl("position " + pos.ToString("0.00"))
                            {
                                Size = 14f,
                                Color = Theme.WindowText,
                            }
                        ],
                    }
                ],
            });

        return ScrollView(
            new BoxEl
            {
                Direction = 1,
                Gap = 16f,
                Padding = Edges4.All(24f),
                Grow = 1f,
                Children =
                [
                    Heading("Inputs & sliders"),
                    new TextEl(
                        "Controlled input primitives — sliders, toggle buttons and scroll bars. " +
                        "Every control is stateless; the page owns the value via UseState and feeds it back in.")
                    {
                        Size = 14f,
                        Color = Theme.ControlText with { A = 0.8f },
                    },
                    volumeDemo,
                    seekDemo,
                    toggleDemo,
                    scrollBarDemo
                ],
            });
    }
}

// ===== FlexPage =====
sealed class FlexPage : Component
{
    static Element Tile(int i, float w = 44, float h = 44) => new BoxEl
    {
        Width = w,
        Height = h,
        Corners = CornerRadius4.All(6),
        Fill = ColorF.FromRgba((byte)(60 + i * 40), 120, 200),
        AlignItems = FlexAlign.Center,
        Justify = FlexJustify.Center,
        Children =
        [
            new TextEl(i.ToString()) { Size = 14f, Bold = true, Color = ColorF.FromRgba(255, 255, 255) }
        ]
    };

    static Element Label(string text) => new TextEl(text) { Size = 13f, Bold = true, Color = Theme.WindowText };

    static Element Caption(string text) => new TextEl(text) { Size = 12f, Color = ColorF.FromRgba(160, 160, 170) };

    static Element DemoRow(Element row) => new BoxEl
    {
        Direction = 0,
        Width = 320,
        Padding = Edges4.All(8),
        BorderColor = Theme.ControlBorder,
        BorderWidth = 1f,
        Corners = CornerRadius4.All(8),
        AlignItems = FlexAlign.Stretch,
        Children = [row]
    };

    static Element JustifyDemo(string title, FlexJustify justify) => VStack(6f,
        Label(title),
        DemoRow(new BoxEl
        {
            Direction = 0,
            Gap = 6f,
            Grow = 1f,
            Justify = justify,
            AlignItems = FlexAlign.Center,
            Children = [Tile(1), Tile(2), Tile(3)]
        }));

    static Element AlignDemo(string title, FlexAlign align) => VStack(6f,
        Label(title),
        DemoRow(new BoxEl
        {
            Direction = 0,
            Gap = 6f,
            Grow = 1f,
            Height = 80,
            Justify = FlexJustify.Start,
            AlignItems = align,
            Children =
            [
                Tile(1, 44, 30),
                Tile(2, 44, 55),
                align == FlexAlign.Stretch ? new BoxEl
                {
                    Width = 44,
                    Corners = CornerRadius4.All(6),
                    Fill = ColorF.FromRgba(180, 120, 200),
                    AlignItems = FlexAlign.Center,
                    Justify = FlexJustify.Center,
                    Children = [new TextEl("3") { Size = 14f, Bold = true, Color = ColorF.FromRgba(255, 255, 255) }]
                } : Tile(3, 44, 70)
            ]
        }));

    public override Element Render()
    {
        var justifyDemos = new Element[]
        {
            JustifyDemo("Start", FlexJustify.Start),
            JustifyDemo("Center", FlexJustify.Center),
            JustifyDemo("End", FlexJustify.End),
            JustifyDemo("SpaceBetween", FlexJustify.SpaceBetween),
            JustifyDemo("SpaceAround", FlexJustify.SpaceAround),
            JustifyDemo("SpaceEvenly", FlexJustify.SpaceEvenly),
        };

        var alignDemos = new Element[]
        {
            AlignDemo("Start", FlexAlign.Start),
            AlignDemo("Center", FlexAlign.Center),
            AlignDemo("End", FlexAlign.End),
            AlignDemo("Stretch", FlexAlign.Stretch),
        };

        var growDemo = VStack(6f,
            Label("Grow 1 : 2 : 1"),
            DemoRow(new BoxEl
            {
                Direction = 0,
                Gap = 6f,
                Grow = 1f,
                Height = 44,
                AlignItems = FlexAlign.Stretch,
                Children =
                [
                    new BoxEl { Grow = 1f, Height = 44, Corners = CornerRadius4.All(6), Fill = ColorF.FromRgba(100, 120, 200), AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children =
                        [new TextEl("1") { Size = 14f, Bold = true, Color = ColorF.FromRgba(255,255,255) }]
                    },
                    new BoxEl { Grow = 2f, Height = 44, Corners = CornerRadius4.All(6), Fill = ColorF.FromRgba(140, 120, 200), AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children =
                        [new TextEl("2") { Size = 14f, Bold = true, Color = ColorF.FromRgba(255,255,255) }]
                    },
                    new BoxEl { Grow = 1f, Height = 44, Corners = CornerRadius4.All(6), Fill = ColorF.FromRgba(180, 120, 200), AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children =
                        [new TextEl("3") { Size = 14f, Bold = true, Color = ColorF.FromRgba(255,255,255) }]
                    }
                ]
            }));

        var wrapTiles = new Element[8];
        for (int i = 0; i < 8; i++) wrapTiles[i] = Tile(i + 1, 50, 40);
        var wrapDemo = VStack(6f,
            Label("Wrap (Width 240)"),
            new BoxEl
            {
                Direction = 0,
                Width = 256,
                Padding = Edges4.All(8),
                BorderColor = Theme.ControlBorder,
                BorderWidth = 1f,
                Corners = CornerRadius4.All(8),
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0,
                        Wrap = true,
                        Gap = 6f,
                        Width = 240,
                        AlignItems = FlexAlign.Start,
                        Children = wrapTiles
                    }
                ]
            });

        return ScrollView(new BoxEl
        {
            Direction = 1,
            Gap = 16f,
            Grow = 1f,
            Padding = Edges4.All(24),
            Children =
            [
                Heading("Flexbox layout"),
                Text("The engine lays out children with a flexbox model: a main axis (justify-content) and a cross axis (align-items), with flex-grow distribution and line wrapping. Each block below shows one knob in isolation."),

                Label("justify-content (main-axis distribution)"),
                Caption("Three equal tiles in a 320-wide row; only the spacing rule changes."),
                VStack(12f, justifyDemos),

                Label("align-items (cross-axis alignment)"),
                Caption("Tiles of differing heights in an 80-tall row."),
                VStack(12f, alignDemos),

                Label("flex-grow (proportional sizing)"),
                Caption("No fixed widths: free space splits 1:2:1 across the three children."),
                growDemo,

                Label("flex-wrap (multi-line flow)"),
                Caption("Eight tiles in a width-constrained row spill onto new lines."),
                wrapDemo
            ]
        });
    }
}

// ===== GridPage =====
sealed class GridPage : Component
{
    static readonly ColorF[] TileTints =
    [
        ColorF.FromRgba(244, 114, 182),
        ColorF.FromRgba(167, 139, 250),
        ColorF.FromRgba(96, 165, 250),
        ColorF.FromRgba(45, 212, 191),
        ColorF.FromRgba(74, 222, 128),
        ColorF.FromRgba(250, 204, 21),
        ColorF.FromRgba(251, 146, 60),
        ColorF.FromRgba(248, 113, 113),
        ColorF.FromRgba(232, 121, 249),
        ColorF.FromRgba(129, 140, 248),
        ColorF.FromRgba(56, 189, 248),
        ColorF.FromRgba(52, 211, 153)
    ];

    public override Element Render()
    {
        return ScrollView(
            new BoxEl
            {
                Direction = 1,
                Gap = 16f,
                Padding = Edges4.All(24),
                Grow = 1f,
                Children =
                [
                    Heading("CSS Grid"),
                    new TextEl(
                        "Two-dimensional layout with real tracks. Like CSS Grid, columns can be fixed " +
                        "pixels, fractional star units that share leftover space, or auto-sized to content. " +
                        "Cells flow into the grid row by row at a uniform row height.")
                        { Size = 14f, Color = Theme.WindowText },

                    SectionLabel("Demo 1 — UniformGrid(4 columns)"),
                    new TextEl(
                        "Twelve cells laid out in 4 equal star columns. The grid is wrapped in a 520px-wide " +
                        "box so the star tracks have a concrete width to divide.")
                        { Size = 13f, Color = Theme.ControlText },
                    UniformGridDemo(),

                    SectionLabel("Demo 2 — Mixed tracks: 80px | 1fr | 2fr | auto"),
                    new TextEl(
                        "Four heterogeneous columns. The first is a fixed 80px. The 1fr and 2fr star tracks " +
                        "split the remaining width 1:2. The final auto track shrinks to fit its content. " +
                        "These are true tracks: every cell in a column shares that column's measured width, " +
                        "so the grid stays aligned across rows.")
                        { Size = 13f, Color = Theme.ControlText },
                    MixedTracksDemo()
                ],
            });
    }

    Element SectionLabel(string text) =>
        new TextEl(text) { Size = 18f, Bold = true, Color = Theme.WindowText };

    Element UniformGridDemo()
    {
        var cells = new Element[12];
        for (int i = 0; i < cells.Length; i++)
            cells[i] = Tile(i + 1, TileTints[i % TileTints.Length]);

        return new BoxEl
        {
            Direction = 1,
            Width = 520f,
            Padding = Edges4.All(16),
            Gap = 8f,
            Fill = Theme.ControlFill,
            BorderColor = Theme.ControlBorder,
            BorderWidth = 1f,
            Corners = CornerRadius4.All(10f),
            Children =
            [
                UniformGrid(4, 12f, 90f, cells)
            ],
        };
    }

    Element Tile(int number, ColorF tint) =>
        new BoxEl
        {
            Direction = 1,
            Justify = FlexJustify.Center,
            AlignItems = FlexAlign.Center,
            Fill = tint with { A = 0.85f },
            HoverFill = tint,
            Corners = CornerRadius4.All(8f),
            Children =
            [
                new TextEl(number.ToString())
                    { Size = 20f, Bold = true, Color = ColorF.FromRgba(20, 20, 24) }
            ],
        };

    Element MixedTracksDemo()
    {
        var columns = new[]
        {
            TrackSize.Px(80),
            TrackSize.Star(1f),
            TrackSize.Star(2f),
            TrackSize.Auto,
        };

        var c0 = TrackCell("80px", "fixed", ColorF.FromRgba(96, 165, 250));
        var c1 = TrackCell("1fr", "star", ColorF.FromRgba(74, 222, 128));
        var c2 = TrackCell("2fr", "star", ColorF.FromRgba(251, 146, 60));
        var c3 = TrackCell("auto", "content", ColorF.FromRgba(232, 121, 249));

        return new BoxEl
        {
            Direction = 1,
            Width = 520f,
            Padding = Edges4.All(16),
            Gap = 8f,
            Fill = Theme.ControlFill,
            BorderColor = Theme.ControlBorder,
            BorderWidth = 1f,
            Corners = CornerRadius4.All(10f),
            Children =
            [
                Grid(columns, 12f, 12f, 64f, c0, c1, c2, c3)
            ],
        };
    }

    Element TrackCell(string kind, string note, ColorF tint) =>
        new BoxEl
        {
            Direction = 1,
            Gap = 2f,
            Justify = FlexJustify.Center,
            AlignItems = FlexAlign.Center,
            Padding = Edges4.All(8),
            Fill = tint with { A = 0.8f },
            Corners = CornerRadius4.All(8f),
            Children =
            [
                new TextEl(kind) { Size = 15f, Bold = true, Color = ColorF.FromRgba(20, 20, 24) },
                new TextEl(note) { Size = 11f, Color = ColorF.FromRgba(30, 30, 36) }
            ],
        };
}

// ===== ImagesPage =====
sealed class ImagesPage : Component
{
    static readonly ColorF Grey = ColorF.FromRgba(150, 150, 156, 255);
    static readonly ColorF CardFill = ColorF.FromRgba(32, 32, 38, 255);
    static readonly ColorF CardBorder = ColorF.FromRgba(58, 58, 66, 255);
    static readonly ColorF SectionFill = ColorF.FromRgba(24, 24, 30, 255);

    static readonly string[] AlbumTitles =
    [
        "Midnight City", "Solar Drift", "Velvet Echo", "Neon Harbor",
        "Glass Horizon", "Lunar Tide", "Crimson Vale", "Static Bloom"
    ];

    static readonly string[] Artists =
    [
        "The Wanderers", "Aria Vance", "Cobalt Sky", "Mono Lake",
        "Echo Sparrow", "Nova Reign", "Pale Fire", "Drift Theory"
    ];

    static readonly string[] AlbumPlaceholders =
    [
        "#273E6C", "#4F776C", "#5C496D", "#254D63",
        "#6E5147", "#3D5E7A", "#7A3544", "#4F6066"
    ];

    // A real, stable, distinct sample cover from a public image CDN (picsum.photos), requested ~2× the display size
    // for crispness at high DPI. These fetch over HTTP/2, decode off-thread on the worker pool, cache to disk, and
    // upload to GPU textures — the full FluentGpu.Media pipeline end to end.
    static string Cover(int seed, int displayPx) => $"https://picsum.photos/seed/fluentgpu-{seed}/{displayPx * 2}/{displayPx * 2}";

    Element AlbumCard(int i)
    {
        return new BoxEl
        {
            Direction = 1,
            Gap = 8f,
            Padding = Edges4.All(12),
            Fill = CardFill,
            HoverFill = ColorF.FromRgba(40, 40, 48, 255),
            BorderColor = CardBorder,
            BorderWidth = 1f,
            Corners = CornerRadius4.All(10),
            Children =
            [
                Image(Cover(i, 150), 150f, 150f, 8f, AlbumPlaceholders[i % AlbumPlaceholders.Length]),
                Text($"Album {AlbumTitles[i % AlbumTitles.Length]}").Strong(),
                Text(Artists[i % Artists.Length]).Foreground(Grey).FontSize(12f)
            ],
        };
    }

    Element Section(string title, string subtitle, Element body)
    {
        return new BoxEl
        {
            Direction = 1,
            Gap = 12f,
            Padding = Edges4.All(20),
            Fill = SectionFill,
            BorderColor = CardBorder,
            BorderWidth = 1f,
            Corners = CornerRadius4.All(12),
            Children =
            [
                Text(title).Strong().FontSize(18f),
                Text(subtitle).Foreground(Grey).FontSize(13f),
                body
            ],
        };
    }

    Element LabeledTile(string label, Element tile)
    {
        return new BoxEl
        {
            Direction = 1,
            Gap = 8f,
            AlignItems = FlexAlign.Center,
            Children =
            [
                tile,
                Text(label).Foreground(Grey).FontSize(12f)
            ],
        };
    }

    public override Element Render()
    {
        var cards = new Element[AlbumTitles.Length];
        for (int i = 0; i < cards.Length; i++)
            cards[i] = AlbumCard(i);

        var gallery = UniformGrid(4, 16f, 240f, cards);

        var cornerVariants = new BoxEl
        {
            Direction = 0,
            Gap = 24f,
            AlignItems = FlexAlign.End,
            Wrap = true,
            Children =
            [
                LabeledTile("square (0)", Image(Cover(10, 80), 80f, 80f, 0f, AlbumPlaceholders[0])),
                LabeledTile("rounded (12)", Image(Cover(11, 80), 80f, 80f, 12f, AlbumPlaceholders[1])),
                LabeledTile("circle (40)", Image(Cover(12, 80), 80f, 80f, 40f, AlbumPlaceholders[2]))
            ],
        };

        var sizeVariants = new BoxEl
        {
            Direction = 0,
            Gap = 24f,
            AlignItems = FlexAlign.End,
            Wrap = true,
            Children =
            [
                LabeledTile("48 x 48", Image(Cover(20, 48), 48f, 48f, 6f, AlbumPlaceholders[3])),
                LabeledTile("80 x 80", Image(Cover(20, 80), 80f, 80f, 6f, AlbumPlaceholders[4])),
                LabeledTile("120 x 120", Image(Cover(20, 120), 120f, 120f, 6f, AlbumPlaceholders[5]))
            ],
        };

        return ScrollView(
            VStack(16f,
                Heading("Async images (album art)"),
                Text("Real cover art fetched from a web CDN over HTTP/2, decoded off-thread on a worker pool (WIC, constrained to the display size), cached to disk, and uploaded to GPU textures. The placeholder tint shows until each decode lands.")
                    .Foreground(Grey),
                Section(
                    "Album grid",
                    "A UniformGrid of 8 album cards. Each card stacks the art tile over a strong title and a muted artist line.",
                    gallery),
                Section(
                    "Corner-radius variants",
                    "The same decode feeds any corner radius — square, rounded, or a full circle (radius = half the tile size).",
                    cornerVariants),
                Section(
                    "Size variants",
                    "One source, requested at several display sizes; the cache keys on logical extent so each gets its own residency slot.",
                    sizeVariants)
            ),
            false
        );
    }
}

// ===== ScrollPage =====
sealed class ScrollPage : Component
{
    static readonly ColorF RowA = ColorF.FromRgba(38, 38, 44, 255);
    static readonly ColorF RowB = ColorF.FromRgba(28, 28, 33, 255);
    static readonly ColorF RowText = ColorF.FromRgba(225, 225, 232, 255);
    static readonly ColorF Muted = ColorF.FromRgba(160, 160, 172, 255);
    static readonly ColorF CardFill = ColorF.FromRgba(22, 22, 26, 255);
    static readonly ColorF CardBorder = ColorF.FromRgba(60, 60, 70, 255);

    Element Row(int n)
    {
        bool even = (n & 1) == 0;
        return new BoxEl
        {
            Direction = 0,
            Height = 44,
            Padding = new Edges4(16, 0, 16, 0),
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.SpaceBetween,
            Fill = even ? RowA : RowB,
            Children =
            [
                new TextEl($"Row {n} — scroll me with the wheel") { Size = 14f, Color = RowText },
                new TextEl($"#{n:00}") { Size = 12f, Color = Muted }
            ],
        };
    }

    public override Element Render()
    {
        const int RowCount = 50;
        var rows = new Element[RowCount];
        for (int i = 0; i < RowCount; i++)
            rows[i] = Row(i + 1);

        var scrollCard = new BoxEl
        {
            Direction = 1,
            Height = 360,
            Grow = 0,
            Fill = CardFill,
            BorderColor = CardBorder,
            BorderWidth = 1f,
            Corners = CornerRadius4.All(10f),
            Padding = Edges4.All(4),
            Children =
            [
                ScrollView(VStack(0f, rows))
            ],
        };

        return VStack(16f,
            Heading("Scrolling"),
            Text("Scrolling is layout-free: the viewport clips and the content is offset by a transform — no relayout. Use the mouse wheel."),
            new TextEl($"Demo: a fixed 360px viewport over {RowCount} rows. Only visible rows are painted; the rest are clipped and shifted by a single transform.") { Size = 13f, Color = Muted },
            scrollCard,
            new TextEl("The page itself does not scroll — the inner card is the scroll surface, so the wheel drives the clipped viewport above.") { Size = 12f, Color = Muted }
        ) is BoxEl box
            ? Wrap(box)
            : Wrap(null!);
    }

    Element Wrap(BoxEl inner) => inner with { Padding = Edges4.All(24) };
}

// ===== VirtualizationPage =====
sealed class VirtualizationPage : Component
{
    static readonly ColorF RowEven = ColorF.FromRgba(255, 255, 255, 8);
    static readonly ColorF RowOdd = ColorF.FromRgba(255, 255, 255, 18);
    static readonly ColorF RowHover = ColorF.FromRgba(255, 255, 255, 34);
    static readonly ColorF IndexGrey = ColorF.FromRgba(150, 150, 158, 255);
    static readonly ColorF SubGrey = ColorF.FromRgba(140, 140, 150, 255);

    static ColorF TileTint(int i)
    {
        // A stable, repeating palette — used as each row's placeholder tint until its thumbnail decodes.
        byte r = (byte)(70 + (i * 53) % 160);
        byte g = (byte)(70 + (i * 97) % 160);
        byte b = (byte)(90 + (i * 31) % 150);
        return ColorF.FromRgba(r, g, b, 255);
    }

    // Cycle a fixed set of real CDN thumbnails so scrolling 100k rows recycles → re-requests → atlas-repacks, WITHOUT
    // hammering the CDN with 100k unique downloads. 32px display → bucket 64 → these pack into the small-image atlas.
    static string Cover(int i) => $"https://picsum.photos/seed/fgrow{i % 120}/80/80";

    Element Row(int i)
    {
        return new BoxEl
        {
            Direction = 0,
            Height = 48f,
            Gap = 12f,
            AlignItems = FlexAlign.Center,
            Padding = new Edges4(16, 0, 16, 0),
            Fill = (i % 2 == 0) ? RowEven : RowOdd,
            HoverFill = RowHover,
            Children =
            [
                new BoxEl
                {
                    Width = 64f,
                    Children = [new TextEl($"{i + 1}") { Size = 13f, Color = IndexGrey }],
                },
                Image(Cover(i), 32f, 32f, 8f, TileTint(i)),   // real thumbnail; tint shows until it decodes, then cross-fades in
                new BoxEl
                {
                    Direction = 1,
                    Gap = 2f,
                    Grow = 1f,
                    Justify = FlexJustify.Center,
                    Children =
                    [
                        new TextEl($"Item {i}") { Size = 14f, Color = Theme.WindowText },
                        new TextEl("subtitle") { Size = 12f, Color = SubGrey },
                    ],
                },
            ],
        };
    }

    public override Element Render()
    {
        return new BoxEl
        {
            Direction = 1,
            Grow = 1f,
            Gap = 16f,
            Padding = Edges4.All(24f),
            Children =
            [
                Heading("List virtualization"),
                Text("100,000 rows, each with a real CDN thumbnail — only the ~visible window is realized and recycled over a slab free-list. As rows recycle, their images request/decode off-thread, pack into the small-image atlas, and evict off-screen, so both node and GPU memory stay flat. Wheel to scroll."),
                Virtual.List(
                    100000,
                    48f,
                    i => Row(i),
                    keyOf: i => "r" + i) with { Grow = 1f },
            ],
        };
    }
}

// ===== AnimationPage =====
sealed class AnimationPage : Component
{
    public override Element Render()
    {
        return ScrollView(
            new BoxEl
            {
                Direction = 1,
                Gap = 18f,
                Padding = Edges4.All(24),
                Grow = 1f,
                Children =
                [
                    Heading("Animation"),
                    Text("Declarative motion hooks animate a component's OWN node. Each demo below is its own little sub-component so the hook has a single element to drive. Keyframes loop, transitions play once on mount, springs settle physically, and eased values interpolate color."),

                    DemoBlock(
                        "Looping pulse",
                        "UseKeyframes drives ScaleX and ScaleY on a continuous loop, easing the box larger then back.",
                        Embed.Comp(() => new AnimationPage_Pulse())),

                    DemoBlock(
                        "Fade + slide in",
                        "UseTransition animates Opacity 0 -> 1 and TranslateY 16 -> 0 once when the element mounts.",
                        Embed.Comp(() => new AnimationPage_FadeIn())),

                    DemoBlock(
                        "Spring on click",
                        "Clicking flips a UseState bool; UseSpring snaps ScaleX/ScaleY toward the new target with a bouncy response.",
                        Embed.Comp(() => new AnimationPage_SpringToggle())),

                    DemoBlock(
                        "Eased color",
                        "UseAnimatedValue eases a 0..1 driver over 300ms; the Fill is a ColorF.Lerp between grey and the accent.",
                        Embed.Comp(() => new AnimationPage_ColorEase()))
                ]
            });
    }

    Element DemoBlock(string title, string desc, Element demo)
    {
        return new BoxEl
        {
            Direction = 1,
            Gap = 10f,
            Padding = Edges4.All(16),
            Fill = Theme.ControlFill,
            BorderColor = Theme.ControlBorder,
            BorderWidth = 1f,
            Corners = CornerRadius4.All(10f),
            Children =
            [
                new TextEl(title) { Size = 18f, Bold = true, Color = Theme.WindowText },
                new TextEl(desc) { Size = 13f, Color = Theme.ControlText },
                new BoxEl
                {
                    Direction = 0,
                    AlignItems = FlexAlign.Center,
                    Justify = FlexJustify.Start,
                    Padding = Edges4.All(12),
                    MinHeight = 96f,
                    Children = [demo]
                }
            ]
        };
    }
}

sealed class AnimationPage_Pulse : Component
{
    public override Element Render()
    {
        UseKeyframes(AnimChannel.ScaleX, [
            new(0f, 1f, Easing.EaseInOut),
            new(0.5f, 1.25f, Easing.EaseInOut),
            new(1f, 1f, Easing.EaseInOut)
        ], 1400f, loop: true, "pulse");

        UseKeyframes(AnimChannel.ScaleY, [
            new(0f, 1f, Easing.EaseInOut),
            new(0.5f, 1.25f, Easing.EaseInOut),
            new(1f, 1f, Easing.EaseInOut)
        ], 1400f, loop: true, "pulse");

        return new BoxEl
        {
            Width = 60f,
            Height = 60f,
            Fill = Theme.Accent,
            Corners = CornerRadius4.All(12f),
        };
    }
}

sealed class AnimationPage_FadeIn : Component
{
    public override Element Render()
    {
        UseTransition(AnimChannel.Opacity, 0f, 1f, 500f, Easing.EaseOut, "mount");
        UseTransition(AnimChannel.TranslateY, 16f, 0f, 500f, Easing.EaseOut, "mount");

        return new BoxEl
        {
            Width = 160f,
            Height = 60f,
            Padding = Edges4.All(12),
            Fill = Theme.ControlFillHover,
            BorderColor = Theme.AccentText,
            BorderWidth = 1f,
            Corners = CornerRadius4.All(10f),
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            Children =
            [
                new TextEl("Faded in") { Size = 14f, Color = Theme.WindowText }
            ]
        };
    }
}

sealed class AnimationPage_SpringToggle : Component
{
    public override Element Render()
    {
        var (on, setOn) = UseState(false);

        UseSpring(AnimChannel.ScaleX, on ? 1.2f : 1f, SpringParams.FromResponse(0.3f, 0.5f), on);
        UseSpring(AnimChannel.ScaleY, on ? 1.2f : 1f, SpringParams.FromResponse(0.3f, 0.5f), on);

        return Button.Standard(on ? "Spring back" : "Spring up", () => setOn(!on));
    }
}

sealed class AnimationPage_ColorEase : Component
{
    public override Element Render()
    {
        var (on, setOn) = UseState(false);
        float t = UseAnimatedValue(on ? 1f : 0f, 300f);

        var grey = ColorF.FromRgba(80, 80, 88);
        var fill = ColorF.Lerp(grey, Theme.Accent, t);

        return new BoxEl
        {
            Direction = 0,
            Gap = 14f,
            AlignItems = FlexAlign.Center,
            Children =
            [
                new BoxEl
                {
                    Width = 80f,
                    Height = 60f,
                    Fill = fill,
                    Corners = CornerRadius4.All(10f),
                },
                Button.Accent(on ? "To grey" : "To accent", () => setOn(!on))
            ]
        };
    }
}

// ===== CompositorPage =====
sealed class CompositorPage : Component
{
    static ColorF AccentTint(float t) => ColorF.Lerp(Theme.Accent, Theme.WindowBackground, t);

    static BoxEl Tile(string label) => new BoxEl
    {
        Width = 70f,
        Height = 70f,
        Fill = AccentTint(0.15f),
        BorderColor = Theme.Accent,
        BorderWidth = 1f,
        Corners = CornerRadius4.All(10f),
        AlignItems = FlexAlign.Center,
        Justify = FlexJustify.Center,
        Children =
        [
            new TextEl(label) { Size = 13f, Bold = true, Color = Theme.AccentText }
        ],
    };

    static Element RowLabel(string text) => new TextEl(text) { Size = 13f, Bold = true, Color = Theme.ControlText };

    static Element RowCaption(string text) => new TextEl(text) { Size = 12f, Color = ColorF.Lerp(Theme.ControlText, Theme.WindowBackground, 0.35f) };

    static Element DemoBlock(string title, string caption, Element row) => new BoxEl
    {
        Direction = 1,
        Gap = 10f,
        Padding = Edges4.All(18f),
        Fill = ColorF.Lerp(Theme.WindowBackground, Theme.ControlText, 0.05f),
        BorderColor = Theme.ControlBorder,
        BorderWidth = 1f,
        Corners = CornerRadius4.All(12f),
        Children =
        [
            RowLabel(title),
            RowCaption(caption),
            new BoxEl
            {
                Direction = 0,
                Gap = 28f,
                Padding = new Edges4(8f, 18f, 8f, 18f),
                AlignItems = FlexAlign.Center,
                Children = [row],
            }
        ],
    };

    static Element LabeledTile(string tileLabel, string subLabel, Element tile) => new BoxEl
    {
        Direction = 1,
        Gap = 8f,
        AlignItems = FlexAlign.Center,
        Width = 96f,
        Children =
        [
            new BoxEl
            {
                Width = 96f,
                Height = 96f,
                AlignItems = FlexAlign.Center,
                Justify = FlexJustify.Center,
                Children = [tile],
            },
            new TextEl(subLabel) { Size = 11f, Color = ColorF.Lerp(Theme.ControlText, Theme.WindowBackground, 0.3f) }
        ],
    };

    public override Element Render()
    {
        var rotateRow = new BoxEl
        {
            Direction = 0,
            Gap = 16f,
            AlignItems = FlexAlign.Center,
            Children =
            [
                LabeledTile("0", "Rotate 0deg", Tile("0").Rotate(0f)),
                LabeledTile("15", "Rotate 15deg", Tile("15").Rotate(15f)),
                LabeledTile("30", "Rotate 30deg", Tile("30").Rotate(30f)),
                LabeledTile("45", "Rotate 45deg", Tile("45").Rotate(45f))
            ],
        };

        var scaleRow = new BoxEl
        {
            Direction = 0,
            Gap = 16f,
            AlignItems = FlexAlign.Center,
            Children =
            [
                LabeledTile("0.7", "Scale 0.7", Tile("0.7x").Scale(0.7f)),
                LabeledTile("1.0", "Scale 1.0", Tile("1.0x").Scale(1f)),
                LabeledTile("1.3", "Scale 1.3", Tile("1.3x").Scale(1.3f))
            ],
        };

        var alphaRow = new BoxEl
        {
            Direction = 0,
            Gap = 16f,
            AlignItems = FlexAlign.Center,
            Children =
            [
                LabeledTile("0.3", "Alpha 0.3", Tile("0.3").Alpha(0.3f)),
                LabeledTile("0.6", "Alpha 0.6", Tile("0.6").Alpha(0.6f)),
                LabeledTile("1.0", "Alpha 1.0", Tile("1.0").Alpha(1f))
            ],
        };

        var offsetRow = new BoxEl
        {
            Direction = 0,
            Gap = 16f,
            AlignItems = FlexAlign.Center,
            Children =
            [
                LabeledTile("base", "Offset (0, 0)", Tile("base").Offset(0f, 0f)),
                LabeledTile("up", "Offset (0, -12)", Tile("-12").Offset(0f, -12f))
            ],
        };

        var cumulativeOpacityDemo = new BoxEl
        {
            Direction = 0,
            Gap = 14f,
            Padding = Edges4.All(14f),
            AlignItems = FlexAlign.Center,
            Fill = AccentTint(0.55f),
            BorderColor = Theme.Accent,
            BorderWidth = 1f,
            Corners = CornerRadius4.All(12f),
            Children =
            [
                Tile("A"),
                Tile("B"),
                Tile("C")
            ],
        }.Alpha(0.5f);

        return ScrollView(
            new BoxEl
            {
                Direction = 1,
                Gap = 16f,
                Padding = Edges4.All(24f),
                Grow = 1f,
                Children =
                [
                    Heading("Compositor (static transforms)"),
                    new TextEl("Transform and opacity are composited per-node — applied at record time with no relayout. These are static here; the Animation page drives them over time.")
                    {
                        Size = 14f,
                        Color = Theme.ControlText,
                    },
                    DemoBlock(
                        "Rotation",
                        "Each tile carries its own Rotation (degrees), applied around the node — no layout reflow of siblings.",
                        rotateRow),
                    DemoBlock(
                        "Scale",
                        "ScaleX/ScaleY scale the node visually at composite time; the layout box is unchanged.",
                        scaleRow),
                    DemoBlock(
                        "Opacity",
                        "Per-node Opacity multiplies into the node's pixels during recording.",
                        alphaRow),
                    DemoBlock(
                        "Offset (translate)",
                        "OffsetX/OffsetY translate the painted node without moving its layout slot.",
                        offsetRow),
                    new BoxEl
                    {
                        Direction = 1,
                        Gap = 10f,
                        Padding = Edges4.All(18f),
                        Fill = ColorF.Lerp(Theme.WindowBackground, Theme.ControlText, 0.05f),
                        BorderColor = Theme.ControlBorder,
                        BorderWidth = 1f,
                        Corners = CornerRadius4.All(12f),
                        Children =
                        [
                            RowLabel("Cumulative opacity"),
                            RowCaption("The container has Alpha(0.5); children inherit the parent's opacity, so each fully-opaque tile composites at 50%."),
                            new BoxEl
                            {
                                Direction = 0,
                                Gap = 28f,
                                Padding = new Edges4(8f, 14f, 8f, 14f),
                                AlignItems = FlexAlign.Center,
                                Children = [cumulativeOpacityDemo],
                            }
                        ],
                    }
                ],
            });
    }
}

// ===== StatePage =====
sealed class StatePage : Component
{
    public static readonly Context<int> ThemeLevel = new(1);

    Element DemoBlock(string label, string desc, Element body) =>
        new BoxEl
        {
            Direction = 1,
            Gap = 10f,
            Padding = Edges4.All(18),
            Fill = Theme.ControlFill,
            BorderColor = Theme.ControlBorder,
            BorderWidth = 1f,
            Corners = CornerRadius4.All(10f),
            Children =
            [
                new TextEl(label) { Size = 16f, Bold = true, Color = Theme.WindowText },
                new TextEl(desc) { Size = 12.5f, Color = Theme.ControlText with { A = 0.75f } },
                body
            ],
        };

    Element CounterDemo()
    {
        var (count, setCount) = UseState(0);
        return DemoBlock(
            "1 — UseState counter",
            "A single piece of local state. The +/- buttons call setCount to drive a re-render.",
            new BoxEl
            {
                Direction = 0,
                Gap = 14f,
                AlignItems = FlexAlign.Center,
                Children =
                [
                    IconButton.Create("-", () => setCount(count - 1), IconButton.DefaultStyle with { Size = 40f }),
                    new BoxEl
                    {
                        MinWidth = 84f,
                        Justify = FlexJustify.Center,
                        AlignItems = FlexAlign.Center,
                        Padding = Edges4.All(8),
                        Fill = Theme.WindowBackground,
                        Corners = CornerRadius4.All(8f),
                        Children = [new TextEl(count.ToString()) { Size = 28f, Bold = true, Color = Theme.Accent }],
                    },
                    IconButton.Create("+", () => setCount(count + 1), IconButton.DefaultStyle with { Size = 40f })
                ],
            });
    }

    Element ReducerDemo()
    {
        var (s, dispatch) = UseReducer<int, int>((st, a) => st + a, 0);
        return DemoBlock(
            "2 — UseReducer",
            "State transitions flow through a reducer. Each button dispatches a delta action.",
            new BoxEl
            {
                Direction = 0,
                Gap = 12f,
                AlignItems = FlexAlign.Center,
                Children =
                [
                    Button.Accent("dispatch +5", () => dispatch(5)),
                    Button.Standard("dispatch -3", () => dispatch(-3)),
                    new BoxEl
                    {
                        MinWidth = 110f,
                        Justify = FlexJustify.Center,
                        AlignItems = FlexAlign.Center,
                        Padding = Edges4.All(8),
                        Fill = Theme.WindowBackground,
                        Corners = CornerRadius4.All(8f),
                        Children = [new TextEl($"total = {s}") { Size = 18f, Bold = true, Color = Theme.WindowText }],
                    }
                ],
            });
    }

    Element NestedDemo() =>
        DemoBlock(
            "3 — Nested independent components",
            "Two chips, each its own Component with its own UseState. Clicking one never affects the other.",
            new BoxEl
            {
                Direction = 0,
                Gap = 16f,
                Wrap = true,
                Children =
                [
                    Embed.Comp(() => new StatePage_Chip()),
                    Embed.Comp(() => new StatePage_Chip()),
                    Embed.Comp(() => new StatePage_Chip())
                ],
            });

    Element ContextDemo()
    {
        var (level, setLevel) = UseState(1);
        return DemoBlock(
            "4 — Context",
            "The page provides a value; a descendant reads it via UseContext without prop drilling.",
            new BoxEl
            {
                Direction = 1,
                Gap = 12f,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0,
                        Gap = 10f,
                        AlignItems = FlexAlign.Center,
                        Children =
                        [
                            new TextEl("provided level:") { Size = 13f, Color = Theme.ControlText },
                            Button.Standard("- level", () => setLevel(level - 1)),
                            new TextEl(level.ToString()) { Size = 18f, Bold = true, Color = Theme.Accent },
                            Button.Standard("+ level", () => setLevel(level + 1))
                        ],
                    },
                    Ctx.Provide(ThemeLevel, level, Embed.Comp(() => new StatePage_Consumer()))
                ],
            });
    }

    public override Element Render()
    {
        return ScrollView(
            new BoxEl
            {
                Direction = 1,
                Gap = 16f,
                Padding = Edges4.All(24),
                Grow = 1f,
                Children =
                [
                    Heading("State & components"),
                    new TextEl("How fluent-gpu manages local state and composition: UseState for simple values, UseReducer for action-driven transitions, independent nested components, and Context for ambient values shared down the tree.")
                    {
                        Size = 14f,
                        Color = Theme.ControlText with { A = 0.85f },
                    },
                    Embed.Comp(() => new StatePage_CounterHost()),
                    Embed.Comp(() => new StatePage_ReducerHost()),
                    NestedDemo(),
                    Embed.Comp(() => new StatePage_ContextHost())
                ],
            });
    }
}

sealed class StatePage_CounterHost : Component
{
    Element DemoBlock(string label, string desc, Element body) =>
        new BoxEl
        {
            Direction = 1,
            Gap = 10f,
            Padding = Edges4.All(18),
            Fill = Theme.ControlFill,
            BorderColor = Theme.ControlBorder,
            BorderWidth = 1f,
            Corners = CornerRadius4.All(10f),
            Children =
            [
                new TextEl(label) { Size = 16f, Bold = true, Color = Theme.WindowText },
                new TextEl(desc) { Size = 12.5f, Color = Theme.ControlText with { A = 0.75f } },
                body
            ],
        };

    public override Element Render()
    {
        var (count, setCount) = UseState(0);
        return DemoBlock(
            "1 — UseState counter",
            "A single piece of local state. The +/- buttons call setCount to drive a re-render.",
            new BoxEl
            {
                Direction = 0,
                Gap = 14f,
                AlignItems = FlexAlign.Center,
                Children =
                [
                    IconButton.Create("-", () => setCount(count - 1), IconButton.DefaultStyle with { Size = 40f }),
                    new BoxEl
                    {
                        MinWidth = 84f,
                        Justify = FlexJustify.Center,
                        AlignItems = FlexAlign.Center,
                        Padding = Edges4.All(8),
                        Fill = Theme.WindowBackground,
                        Corners = CornerRadius4.All(8f),
                        Children = [new TextEl(count.ToString()) { Size = 28f, Bold = true, Color = Theme.Accent }],
                    },
                    IconButton.Create("+", () => setCount(count + 1), IconButton.DefaultStyle with { Size = 40f })
                ],
            });
    }
}

sealed class StatePage_ReducerHost : Component
{
    Element DemoBlock(string label, string desc, Element body) =>
        new BoxEl
        {
            Direction = 1,
            Gap = 10f,
            Padding = Edges4.All(18),
            Fill = Theme.ControlFill,
            BorderColor = Theme.ControlBorder,
            BorderWidth = 1f,
            Corners = CornerRadius4.All(10f),
            Children =
            [
                new TextEl(label) { Size = 16f, Bold = true, Color = Theme.WindowText },
                new TextEl(desc) { Size = 12.5f, Color = Theme.ControlText with { A = 0.75f } },
                body
            ],
        };

    public override Element Render()
    {
        var (s, dispatch) = UseReducer<int, int>((st, a) => st + a, 0);
        return DemoBlock(
            "2 — UseReducer",
            "State transitions flow through a reducer. Each button dispatches a delta action.",
            new BoxEl
            {
                Direction = 0,
                Gap = 12f,
                AlignItems = FlexAlign.Center,
                Wrap = true,
                Children =
                [
                    Button.Accent("dispatch +5", () => dispatch(5)),
                    Button.Standard("dispatch -3", () => dispatch(-3)),
                    new BoxEl
                    {
                        MinWidth = 110f,
                        Justify = FlexJustify.Center,
                        AlignItems = FlexAlign.Center,
                        Padding = Edges4.All(8),
                        Fill = Theme.WindowBackground,
                        Corners = CornerRadius4.All(8f),
                        Children = [new TextEl($"total = {s}") { Size = 18f, Bold = true, Color = Theme.WindowText }],
                    }
                ],
            });
    }
}

sealed class StatePage_ContextHost : Component
{
    Element DemoBlock(string label, string desc, Element body) =>
        new BoxEl
        {
            Direction = 1,
            Gap = 10f,
            Padding = Edges4.All(18),
            Fill = Theme.ControlFill,
            BorderColor = Theme.ControlBorder,
            BorderWidth = 1f,
            Corners = CornerRadius4.All(10f),
            Children =
            [
                new TextEl(label) { Size = 16f, Bold = true, Color = Theme.WindowText },
                new TextEl(desc) { Size = 12.5f, Color = Theme.ControlText with { A = 0.75f } },
                body
            ],
        };

    public override Element Render()
    {
        var (level, setLevel) = UseState(1);
        return DemoBlock(
            "4 — Context",
            "The page provides a value; a descendant reads it via UseContext without prop drilling.",
            new BoxEl
            {
                Direction = 1,
                Gap = 12f,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0,
                        Gap = 10f,
                        AlignItems = FlexAlign.Center,
                        Children =
                        [
                            new TextEl("provided level:") { Size = 13f, Color = Theme.ControlText },
                            Button.Standard("- level", () => setLevel(level - 1)),
                            new TextEl(level.ToString()) { Size = 18f, Bold = true, Color = Theme.Accent },
                            Button.Standard("+ level", () => setLevel(level + 1))
                        ],
                    },
                    Ctx.Provide(StatePage.ThemeLevel, level, Embed.Comp(() => new StatePage_Consumer()))
                ],
            });
    }
}

sealed class StatePage_Chip : Component
{
    static int _seq;
    int _id = ++_seq;

    public override Element Render()
    {
        var (n, setN) = UseState(0);
        return new BoxEl
        {
            Direction = 1,
            Gap = 8f,
            Padding = Edges4.All(14),
            Fill = Theme.WindowBackground,
            BorderColor = Theme.ControlBorder,
            BorderWidth = 1f,
            Corners = CornerRadius4.All(10f),
            AlignItems = FlexAlign.Center,
            MinWidth = 130f,
            Children =
            [
                new TextEl($"chip #{_id}") { Size = 12f, Color = Theme.ControlText with { A = 0.7f } },
                new TextEl(n.ToString()) { Size = 24f, Bold = true, Color = Theme.Accent },
                Button.Standard($"count {n}", () => setN(n + 1))
            ],
        };
    }
}

sealed class StatePage_Consumer : Component
{
    public override Element Render()
    {
        var x = UseContext(StatePage.ThemeLevel);
        return new BoxEl
        {
            Direction = 0,
            Gap = 10f,
            AlignItems = FlexAlign.Center,
            Padding = Edges4.All(12),
            Fill = ColorF.Lerp(Theme.WindowBackground, Theme.Accent, 0.12f),
            BorderColor = Theme.Accent with { A = 0.5f },
            BorderWidth = 1f,
            Corners = CornerRadius4.All(8f),
            Children =
            [
                new TextEl("consumer reads:") { Size = 13f, Color = Theme.ControlText },
                new TextEl($"level {x}") { Size = 18f, Bold = true, Color = Theme.Accent }
            ],
        };
    }
}

