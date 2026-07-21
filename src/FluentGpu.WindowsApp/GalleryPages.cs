using System;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Reconciler;
using static FluentGpu.Dsl.Ui;

// Capability-gallery feature pages (authored in parallel by a workflow, assembled here).


// ===== TypographyPage =====
[GalleryPage("typography", "Typography", "Design")]
[Route("typography")]
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
                    ParagraphCard(),
                    FontFamiliesCard()
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

    // Font families (moved here from the old Icons & fonts page — Iconography is now the full glyph catalog).
    Element FontFamiliesCard() =>
        Section("Font families",
            Text("Every text run has a FontFamily — a system name (\"Segoe UI\", \"Consolas\") or a custom file as \"path/to.ttf#Family Name\" (the WinUI FontIcon syntax).").Foreground(Grey).FontSize(13f),
            new BoxEl
            {
                Direction = 1, Gap = 6f, Padding = Edges4.All(14), Corners = CornerRadius4.All(8f), Fill = ColorF.FromRgba(0x1A, 0x1A, 0x1A),
                Children =
                [
                    Text("The quick brown fox — Segoe UI").Font("Segoe UI").FontSize(16f),
                    Text("The quick brown fox — Consolas").Font("Consolas").FontSize(16f),
                    Text("The quick brown fox — Georgia").Font("Georgia").FontSize(16f),
                ],
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
[GalleryPage("buttons", "Buttons", "Overview", Hidden = true)]
[Route("buttons")]
sealed class ButtonsPage : Component
{
    public override Element Render()
    {
        var (count, setCount) = UseState(0);
        var shuffle = UseSignal(false);

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
                                ToggleButton.Create("Shuffle", shuffle),
                                new TextEl(shuffle.Value ? "Shuffle is ON" : "Shuffle is OFF")
                                    .FontSize(14f)
                                    .Foreground(shuffle.Value ? Theme.Accent : Theme.WindowText)
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
[GalleryPage("inputs", "Inputs", "Overview", Hidden = true)]
[Route("inputs")]
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
        var vol = UseFloatSignal(0.6f);
        var seek = UseFloatSignal(0.3f);
        var shuffle = UseSignal(false);
        var repeat = UseSignal(true);
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
                    Slider.Create(vol, length: 260f),
                    new BoxEl
                    {
                        Width = 52f,
                        AlignItems = FlexAlign.End,
                        Children =
                        [
                            new TextEl("")
                            {
                                Text = Prop.Of(() => ((int)(vol.Value * 100f)).ToString() + "%"),
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
                            new TextEl("")
                            {
                                Text = Prop.Of(() => FormatTime(seek.Value, trackSeconds)),
                                Size = 13f,
                                Color = Theme.ControlText,
                            }
                        ],
                    },
                    Slider.Create(seek, length: 240f),
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
                    ToggleButton.Create("Shuffle", shuffle),
                    ToggleButton.Create("Repeat", repeat),
                    new BoxEl
                    {
                        Children =
                        [
                            new TextEl(
                                "shuffle=" + (shuffle.Value ? "on" : "off") +
                                "  repeat=" + (repeat.Value ? "on" : "off"))
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
[GalleryPage("flex", "Flexbox", "Fundamentals")]
[Route("flex")]
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
[GalleryPage("grid", "CSS Grid", "Fundamentals")]
[Route("grid")]
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

// ===== ImagePage (Media › Image) =====
// The Ui.Image element as a control-style gallery page: GalleryPage.Shell + ControlExample cards, each with a C# code
// panel — a simple image, object-fit (Cover/Contain), corner radius, sizing/decodePx, the placeholder tint, and the
// responsive async album grid (the full FluentGpu.Media pipeline: HTTP/2 fetch → off-thread WIC decode → disk cache →
// GPU texture residency).
[GalleryPage("Image", "Image", "Media")]
[Route("Image")]
sealed class ImagePage : Component
{
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

    // A stable, distinct sample cover from a public image CDN (picsum.photos), requested ~2× the display size for
    // crispness at high DPI.
    static string Cover(int seed, int displayPx) => $"https://picsum.photos/seed/fluentgpu-{seed}/{displayPx * 2}/{displayPx * 2}";

    static Element LabeledTile(string label, Element tile) => new BoxEl
    {
        Direction = 1, Gap = 8f, AlignItems = FlexAlign.Center,
        Children = [tile, Caption(label).Secondary()],
    };

    // A wrapping row of small labeled tiles (wraps + bottom-aligns like the old showcase).
    static Element WrapRow(params Element[] kids) => new BoxEl
    {
        Direction = 0, Gap = 24f, AlignItems = FlexAlign.End, Wrap = true, Children = kids,
    };

    // One object-fit tile: a width-constrained box so the responsive image has a width to fill, height from aspect.
    static Element FitBox(string label, ImageFit fit, string ph) => LabeledTile(label, new BoxEl
    {
        Direction = 1, Width = 180f, AlignItems = FlexAlign.Stretch,
        Children = [Image(Cover(7, 200), fit, aspect: 16f / 9f, decodePx: 200f, corners: 8f, placeholder: ph)],
    });

    static Element AlbumCard(int i) => new BoxEl
    {
        Direction = 1, Gap = 8f, Padding = Edges4.All(12),
        Fill = Tok.FillCardDefault, HoverFill = Tok.FillCardSecondary,
        BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, Corners = Radii.OverlayAll,
        Children =
        [
            // Responsive art: no fixed extent — fills its (fluid) cell and stays square (aspect 1) with the cover crop.
            Image(Cover(i, 150), ImageFit.Cover, aspect: 1f, decodePx: 300f, corners: 8f, placeholder: AlbumPlaceholders[i % AlbumPlaceholders.Length]),
            BodyStrong(AlbumTitles[i % AlbumTitles.Length]),
            Caption(Artists[i % Artists.Length]).Secondary(),
        ],
    };

    public override Element Render()
    {
        var albumCards = new Element[AlbumTitles.Length];
        for (int i = 0; i < albumCards.Length; i++) albumCards[i] = AlbumCard(i);

        return GalleryPage.Shell("Image",
            "Async, GPU-resident images. Ui.Image fetches over HTTP/2, decodes off-thread (WIC, constrained to the display size), caches to disk, and uploads to a GPU texture — with object-fit, corner radius, a decode-size hint, and a placeholder tint shown until the decode lands.",

            ControlExample.Build("A simple image",
                Image(Cover(1, 120), 120f, 120f, 8f, "#273E6C"),
                description: "Ui.Image(source, width, height, corners) renders a fixed-size, GPU-resident texture; the placeholder tint shows until the off-thread decode lands.",
                code: """
                // Fetched over HTTP/2, decoded off-thread (WIC), disk-cached, uploaded to a GPU texture.
                Image("https://picsum.photos/seed/cover/240/240", 120f, 120f, corners: 8f)
                """),

            ControlExample.Build("Object-fit: Cover vs. Contain",
                WrapRow(FitBox("Cover", ImageFit.Cover, "#273E6C"), FitBox("Contain", ImageFit.Contain, "#4F776C")),
                description: "A responsive image fills the width its layout gives it and derives its height from aspect. ImageFit.Cover fills the box and crops the overflow; ImageFit.Contain fits the whole image and letterboxes the remainder with the placeholder.",
                code: """
                // The album/thumbnail shape: no fixed extent — fills its cell, height from aspect.
                Image(src, ImageFit.Cover,   aspect: 16f / 9f, decodePx: 200f, corners: 8f)
                Image(src, ImageFit.Contain, aspect: 16f / 9f, decodePx: 200f, corners: 8f)
                """),

            ControlExample.Build("Corner radius",
                WrapRow(
                    LabeledTile("square (0)", Image(Cover(10, 80), 80f, 80f, 0f, AlbumPlaceholders[0])),
                    LabeledTile("rounded (12)", Image(Cover(11, 80), 80f, 80f, 12f, AlbumPlaceholders[1])),
                    LabeledTile("circle (40)", Image(Cover(12, 80), 80f, 80f, 40f, AlbumPlaceholders[2]))),
                description: "The same decode feeds any corner radius — a radius of half the extent gives a full circle.",
                code: """
                Image(src, 80f, 80f, corners: 0f)    // square
                Image(src, 80f, 80f, corners: 12f)   // rounded
                Image(src, 80f, 80f, corners: 40f)   // circle (radius = size / 2)
                """),

            ControlExample.Build("Sizing & decode resolution",
                WrapRow(
                    LabeledTile("48", Image(Cover(20, 48), 48f, 48f, 6f, AlbumPlaceholders[3])),
                    LabeledTile("80", Image(Cover(20, 80), 80f, 80f, 6f, AlbumPlaceholders[4])),
                    LabeledTile("120", Image(Cover(20, 120), 120f, 120f, 6f, AlbumPlaceholders[5]))),
                description: "One source requested at several display sizes; the cache keys on logical extent, so each size gets its own residency slot decoded to fit — no oversized texture for a thumbnail.",
                code: """
                Image(src, 48f, 48f, corners: 6f)
                Image(src, 80f, 80f, corners: 6f)
                Image(src, 120f, 120f, corners: 6f)
                """),

            ControlExample.Build("Placeholder tint",
                WrapRow(
                    LabeledTile("decoded", Image(Cover(30, 96), 96f, 96f, 8f, "#3D5E7A")),
                    LabeledTile("unresolved → tint", Image("https://example.invalid/cover.jpg", 96f, 96f, 8f, "#7A3544"))),
                description: "Every image shows its placeholder fill until the decode lands; an unresolved source keeps the tint indefinitely — no broken-image box.",
                code: """
                // The argument after corners is the placeholder (ColorF or "#RRGGBB") shown pre-decode.
                Image(src, 96f, 96f, corners: 8f, placeholder: "#3D5E7A")
                """),

            ControlExample.Build("Async album grid",
                AutoGrid(180f, 16f, float.NaN, albumCards),
                description: "A responsive auto-fill grid of 8 covers: the column count reflows with the width and each tile fills its cell as a square (object-fit: cover). Real cover art from a public CDN — fetch → off-thread WIC decode → disk cache → GPU residency, end to end.",
                code: """
                Element AlbumCard(int i) => new BoxEl
                {
                    Direction = 1, Gap = 8f, Padding = Edges4.All(12),
                    Fill = Tok.FillCardDefault, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, Corners = Radii.OverlayAll,
                    Children =
                    [
                        Image(cover(i), ImageFit.Cover, aspect: 1f, decodePx: 300f, corners: 8f),
                        BodyStrong(titles[i]), Caption(artists[i]).Secondary(),
                    ],
                };
                AutoGrid(180f, 16f, float.NaN, cards);   // reflow columns ≥180px, rows size to content
                """));
    }
}

// ===== ScrollPage =====
[GalleryPage("scrolling", "Scrolling", "Fundamentals")]
[Route("scrolling")]
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

    static readonly string[] Months =
        { "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" };

    // A 320px scroll card over 50 rows; `cues` drives the edge-cue affordance (controls.md §8.3).
    Element ScrollCard(ScrollEdgeCues cues, float width)
    {
        const int RowCount = 50;
        var rows = new Element[RowCount];
        for (int i = 0; i < RowCount; i++) rows[i] = Row(i + 1);
        return new BoxEl
        {
            Direction = 1, Height = 320, Grow = 0, Width = width,
            Fill = CardFill, BorderColor = CardBorder, BorderWidth = 1f,
            Corners = CornerRadius4.All(10f), Padding = Edges4.All(4),
            Children = [ ScrollView(VStack(0f, rows)) with { EdgeCues = cues } ],
        };
    }

    public override Element Render()
    {
        Element Caption(string s) => new TextEl(s) { Size = 12f, Color = Muted };
        Element Labeled(string s, Element card) => VStack(8f, card, Caption(s));

        // Horizontal strip wider than its viewport → left/right cues.
        var cells = new Element[Months.Length];
        for (int i = 0; i < Months.Length; i++)
            cells[i] = new BoxEl
            {
                Direction = 1, Width = 104, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Padding = new Edges4(0, 20, 0, 20), Margin = new Edges4(0, 0, 8, 0),
                Fill = RowA, Corners = CornerRadius4.All(8f),
                Children = [ new TextEl(Months[i]) { Size = 13f, Color = RowText } ],
            };
        var hCard = new BoxEl
        {
            Direction = 1, Width = 420, Grow = 0,
            Fill = CardFill, BorderColor = CardBorder, BorderWidth = 1f,
            Corners = CornerRadius4.All(10f), Padding = Edges4.All(8),
            Children = [ ScrollView(HStack(0f, cells), horizontal: true) ],
        };

        return VStack(16f,
            Heading("Scrolling"),
            Text("Scrolling is layout-free: the viewport clips and the content is offset by a transform — no relayout. Use the mouse wheel."),
            new TextEl("Edge cues (controls.md §8.3): a surface-colour fade marks any edge with more content, so a clipped list never looks finished — the affordance macOS's hidden scrollbars remove. On by default; set EdgeCues = ScrollEdgeCues.None to opt out.") { Size = 13f, Color = Muted },
            HStack(20f,
                Labeled("No cue (EdgeCues = None)", ScrollCard(ScrollEdgeCues.None, 300f)),
                Labeled("Edge cue — default", ScrollCard(ScrollEdgeCues.Auto, 300f))),
            new TextEl("Fade + chevron — an explicit directional hint (EdgeCues = ScrollEdgeCues.FadeAndChevron):") { Size = 13f, Color = Muted },
            Labeled("Fade + chevron", ScrollCard(ScrollEdgeCues.FadeAndChevron, 300f)),
            new TextEl("Horizontal — left/right cues on the same gradient logic:") { Size = 13f, Color = Muted },
            Labeled("Horizontal scroll", hCard)
        ) is BoxEl box
            ? Wrap(box)
            : Wrap(null!);
    }

    Element Wrap(BoxEl inner) => inner with { Padding = Edges4.All(24) };
}

// ===== VirtualizationPage =====
[GalleryPage("virtualization", "List virtualization", "Fundamentals")]
[Route("virtualization")]
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

    // BOUND row template (Virtual.ListBound): built ONCE per visible slot with an index SIGNAL — scrolling rebinds the
    // slot by writing the signal, so only these bound Text/Fill/Source thunks re-run (no element rebuild, no
    // reconcile, no node churn). This is the recycler fast path a 100k thumb-drag storm exercises.
    static Element Row(FluentGpu.Signals.IReadSignal<int> idx)
    {
        return new BoxEl
        {
            Direction = 0,
            Height = 48f,
            Gap = 12f,
            AlignItems = FlexAlign.Center,
            Padding = new Edges4(16, 0, 16, 0),
            Fill = Prop.Of(() => (idx.Value % 2 == 0) ? RowEven : RowOdd),
            HoverFill = RowHover,
            Children =
            [
                new BoxEl
                {
                    Width = 64f,
                    Children = [new TextEl("") { Size = 13f, Color = IndexGrey, Text = Prop.Of(() => $"{idx.Value + 1}") }],
                },
                // real thumbnail; tint shows until it decodes, then cross-fades in
                new ImageEl
                {
                    Width = 32f, Height = 32f, Corners = CornerRadius4.All(8f),
                    Source = Prop.Of(() => Cover(idx.Value)), Placeholder = Prop.Of(() => TileTint(idx.Value)),
                },
                new BoxEl
                {
                    Direction = 1,
                    Gap = 2f,
                    Grow = 1f,
                    Justify = FlexJustify.Center,
                    Children =
                    [
                        new TextEl("") { Size = 14f, Color = Theme.WindowText, Text = Prop.Of(() => $"Item {idx.Value}") },
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
                Text("100,000 rows with real CDN thumbnails — visible slots are built once and REBOUND via index signals as you scroll (no element rebuild); images decode off-thread, pack into the atlas, and evict off-screen, so memory stays flat. Wheel to scroll.")
                    with { Wrap = TextWrap.Wrap },   // wraps to the content-frame width (the layout measures grow children against availW − fixed siblings)
                Virtual.ListBound(100000, 48f, Row) with { Grow = 1f },
            ],
        };
    }
}

// ===== AnimationPage lives in AnimationPage.cs — the complete AnimEngine showcase =====

// ===== CompositorPage =====
[GalleryPage("compositor", "Compositor", "Fundamentals")]
[Route("compositor")]
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
// The signals-first state model, demonstrated end to end: the three update mechanisms (binding / granular re-render /
// reactive control flow), the state hooks (UseState, UseSignal, UseComputed, UseReducer, UseContext), and live
// render-count instrumentation that PROVES which path re-renders. Canon: docs/guide/reactivity.md.
[GalleryPage("state", "State & components", "Fundamentals")]
[Route("state")]
sealed class StatePage : Component
{
    public static readonly Context<int> ThemeLevel = new(1);

    public override Element Render()
    {
        return GalleryPage.ShellKeyed("state", "State & components",
            "A change reaches pixels through one mechanism: a signal. Reading a signal subscribes the current reactive " +
            "computation; writing it re-runs only the computations that read it — a property binding, one component's " +
            "render, or a control-flow boundary. No full-tree re-render, no global dirty flag. Each demo below shows " +
            "its own render counter as proof of what actually re-ran.",
            ModelTable(),
            new BoxEl { Height = 8 },
            Embed.Comp(() => new StatePage_CounterHost()),
            Embed.Comp(() => new StatePage_SignalHost()),
            Embed.Comp(() => new StatePage_BindHost()),
            Embed.Comp(() => new StatePage_ThreeFormsHost()),
            Embed.Comp(() => new StatePage_MemoHost()),
            Embed.Comp(() => new StatePage_ShowHost()),
            Embed.Comp(() => new StatePage_ForHost()),
            Embed.Comp(() => new StatePage_ReducerHost()),
            NestedDemo(),
            Embed.Comp(() => new StatePage_ContextHost()),
            RulesCard());
    }

    // The three update mechanisms, cheapest first (docs/guide/reactivity.md §"The three update mechanisms").
    static Element ModelTable() => new BoxEl
    {
        Direction = 1, Corners = Radii.OverlayAll, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
        Fill = Tok.FillSolidBase, ClipToBounds = true,
        Children =
        [
            TableRow(true, "Mechanism", "Re-runs", "Cost"),
            Divider(),
            TableRow(false, "Binding — Transform / Opacity / Fill / Text set to a Func or signal", "one effect → one node property", "compositor-only (no render, no reconcile, no layout)"),
            Divider(),
            TableRow(false, "Granular re-render — UseState / UseSignal read in Render()", "the owning component's subtree", "render + reconcile + scoped relayout of that subtree"),
            Divider(),
            TableRow(false, "Reactive control flow — Flow.For / Flow.Show", "one boundary effect → keyed diff", "structural reconcile of that boundary only"),
        ],
    };

    static Element TableRow(bool header, string a, string b, string c)
    {
        TextEl Cell(string s) => header ? BodyStrong(s) : new TextEl(s) { Size = 13f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap };
        return new BoxEl
        {
            Direction = 0, Gap = 12f, Padding = new Edges4(16, 10, 16, 10), AlignItems = FlexAlign.Start,
            Children =
            [
                new BoxEl { Width = 360f, Children = [Cell(a)] },
                new BoxEl { Width = 230f, Children = [Cell(b)] },
                new BoxEl { Grow = 1f, Children = [Cell(c)] },
            ],
        };
    }

    static Element NestedDemo() => ControlExample.Build("Nested independent components",
        new BoxEl
        {
            Direction = 0, Gap = 16f, Wrap = true,
            Children =
            [
                Embed.Comp(() => new StatePage_Chip()),
                Embed.Comp(() => new StatePage_Chip()),
                Embed.Comp(() => new StatePage_Chip()),
            ],
        },
        description: "Each chip is its own Component with its own UseState. Clicking one re-renders only that chip — parents and siblings never run (there is no prop-diffing cascade).",
        code: """
        // Each chip owns its state; a parent re-render never re-renders a child component.
        HStack(16,
            Embed.Comp(() => new Chip()),
            Embed.Comp(() => new Chip()),
            Embed.Comp(() => new Chip()))
        """);

    static Element RulesCard() => new BoxEl
    {
        Direction = 1, Gap = 8f, Padding = Edges4.All(18), Margin = new Edges4(0, 8, 0, 0),
        Corners = Radii.OverlayAll, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, Fill = Tok.FillCardDefault,
        Children =
        [
            BodyStrong("Rules that prevent most state bugs"),
            Rule(".Value subscribes the current computation; .Peek() reads without subscribing. A bind thunk must read .Value."),
            Rule("A component whose Render() reads no signals renders ONCE — show changing values via a bound prop (Text = sig, or Text = Prop.Of(() => …) for derived text), never Ui.Text(sig.Value)."),
            Rule("Never write a signal during render (infinite loop) — write from an event handler or UseEffect."),
            Rule("Parent→child data flows through signals or context, never constructor args — those freeze at mount."),
            Rule("Prefer Transform/Opacity/Fill binds for hot values; Width/Height/Text binds cost a scoped relayout."),
            Rule("Hooks run in stable call order — no hooks inside if/loops."),
        ],
    };

    static Element Rule(string text) => new BoxEl
    {
        Direction = 0, Gap = 8f, AlignItems = FlexAlign.Start,
        Children =
        [
            new TextEl("•") { Size = 13f, Color = Tok.AccentDefault },
            new TextEl(text) { Size = 13f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, Grow = 1f },
        ],
    };
}

/// <summary>Granular re-render: UseState. The render counter increments per click — exactly this card re-ran.</summary>
sealed class StatePage_CounterHost : Component
{
    int _renders;

    public override Element Render()
    {
        _renders++;
        var (count, setCount) = UseState(0);
        return ControlExample.Build("Granular re-render — UseState",
            new BoxEl
            {
                Direction = 0, Gap = 14f, AlignItems = FlexAlign.Center,
                Children =
                [
                    IconButton.Create(Icons.Remove, () => setCount(count - 1)),
                    new BoxEl
                    {
                        MinWidth = 84f, Justify = FlexJustify.Center, AlignItems = FlexAlign.Center, Padding = Edges4.All(8),
                        Fill = Tok.FillControlDefault, Corners = Radii.ControlAll,
                        Children = [new TextEl(count.ToString()) { Size = 28f, Bold = true, Color = Tok.AccentDefault }],
                    },
                    IconButton.Create(Icons.Add, () => setCount(count + 1)),
                ],
            },
            description: "Reading count subscribes this component's render-effect, so setCount re-renders this card's subtree — and nothing else. Watch the render counter follow the clicks.",
            output: VStack(4, BodyStrong($"count = {count}"), Caption($"host renders: {_renders}").Tertiary()),
            code: """
            var (count, setCount) = UseState(0);   // reading `count` subscribes THIS component

            HStack(14,
                IconButton.Create(Icons.Remove, () => setCount(count - 1)),
                Text($"{count}"),
                IconButton.Create(Icons.Add, () => setCount(count + 1)))
            """);
    }
}

/// <summary>UseSignal + TextBind: the text updates while the host's render counter stays at 1 forever.</summary>
sealed class StatePage_SignalHost : Component
{
    int _renders;

    public override Element Render()
    {
        _renders++;
        var n = UseSignal(0);
        return ControlExample.Build("Signal + TextBind — update text without a re-render",
            new BoxEl
            {
                Direction = 0, Gap = 12f, AlignItems = FlexAlign.Center,
                Children =
                [
                    Button.Accent("n.Value++", () => n.Value = n.Peek() + 1),
                    Button.Standard("Reset", () => n.Value = 0),
                ],
            },
            description: "Render() never reads n.Value, so writes re-run only the TextBind thunk — one effect, one text node. The render counter stays at 1 no matter how many times you click.",
            output: VStack(4, GalleryPage.LiveText(() => $"n = {n.Value}"), Caption($"host renders: {_renders}").Tertiary()),
            code: """
            var n = UseSignal(0);                    // a cell this component OWNS but does not read in Render()

            Button.Accent("n.Value++", () => n.Value = n.Peek() + 1);

            new TextEl("") { Text = Prop.Of(() => $"n = {n.Value}") };   // only this thunk re-runs on writes
            """);
    }
}

/// <summary>Compositor-only bindings: a FloatSignal drives the bound Transform/Fill — no render, no layout.</summary>
sealed class StatePage_BindHost : Component
{
    static readonly ColorF Grey = ColorF.FromRgba(96, 96, 104);
    int _renders;

    public override Element Render()
    {
        _renders++;
        var x = UseFloatSignal(0.3f);
        var track = new BoxEl
        {
            Width = 220f, Height = 36f, Corners = Radii.ControlAll,
            Fill = Tok.FillControlDefault, BorderColor = Tok.StrokeControlDefault, BorderWidth = 1f,
            Children =
            [
                new BoxEl
                {
                    Width = 32f, Height = 32f, Margin = Edges4.All(2), Corners = Radii.ControlAll,
                    Fill = Prop.Of(() => ColorF.Lerp(Grey, Tok.AccentDefault, x.Value)),
                    Transform = Prop.Of(() => Affine2D.Translation(x.Value * 184f, 0f)),
                },
            ],
        };
        return ControlExample.Build("Compositor-only binding — Slider.Create + a bound Transform",
            VStack(14, Slider.Create(x), track),
            description: "The slider drag writes the FloatSignal (no setState per move); the box rides bound Transform + Fill thunks. Frames while dragging are compositor-only: no render, no reconcile, no layout.",
            output: VStack(4, GalleryPage.LiveText(() => $"x = {x.Value:0.00}"), Caption($"host renders: {_renders}").Tertiary()),
            code: """
            var x = UseFloatSignal(0.3f);   // hot scalar → pass the signal, don't setState per move

            Slider.Create(x);

            new BoxEl
            {
                Width = 32, Height = 32,
                Transform = Prop.Of(() => Affine2D.Translation(x.Value * 184f, 0f)),  // compositor-only
                Fill = Prop.Of(() => ColorF.Lerp(grey, Tok.AccentDefault, x.Value)),  // compositor-only
            };
            """);
    }
}

/// <summary>The unified property surface: ONE channel (Opacity), driven all three ways — static value (re-render
/// tier), derived Func thunk, and signal-direct (both compositor-only). The render counter is the proof: the static
/// button re-renders this host; the slider scrub never does.</summary>
sealed class StatePage_ThreeFormsHost : Component
{
    int _renders;

    public override Element Render()
    {
        _renders++;
        var (staticOp, setStaticOp) = UseState(1.0f);
        var op = UseFloatSignal(0.8f);

        // The helper takes Prop<float> — every form flows through the SAME parameter type.
        static Element Chip(string label, Prop<float> opacity) => VStack(4,
            new BoxEl { Width = 56f, Height = 36f, Corners = Radii.ControlAll, Fill = Tok.AccentDefault, Opacity = opacity },
            Caption(label).Tertiary());

        return ControlExample.Build("One channel, three forms — Opacity",
            VStack(14,
                HStack(20,
                    Chip("value (re-render)", staticOp),                  // float        → Prop<float>
                    Chip("Func (compositor)", Prop.Of(() => op.Value * op.Value)),   // derived thunk
                    Chip("signal (compositor)", op)),                     // FloatSignal  → Prop<float>, no closure
                HStack(12,
                    Button.Standard($"static → {(staticOp > 0.7f ? "0.4" : "1.0")}", () => setStaticOp(staticOp > 0.7f ? 0.4f : 1.0f)),
                    Slider.Create(op))),
            description: "Every bindable channel is one Prop<T> property accepting a static value, a Func<T> thunk, or a concrete signal. " +
                         "The static form re-asserts on re-render (the button bumps the counter); the two bound forms ride the compositor — " +
                         "scrubbing the slider updates both right-hand chips with zero host re-renders.",
            output: VStack(4, GalleryPage.LiveText(() => $"op = {op.Value:0.00}"), Caption($"host renders: {_renders}").Tertiary()),
            code: """
            var (staticOp, setStaticOp) = UseState(1.0f);   // value → re-render tier
            var op = UseFloatSignal(0.8f);                  // signal → compositor tier

            new BoxEl { Opacity = staticOp };                          // 1) static value
            new BoxEl { Opacity = Prop.Of(() => op.Value * op.Value) };// 2) derived Func (Prop.Of wraps inline lambdas)
            new BoxEl { Opacity = op };                                // 3) signal-direct — no closure at all
            """);
    }
}

/// <summary>Derived state: UseComputed memo — lazy, cached, recomputed when an input signal changes.</summary>
sealed class StatePage_MemoHost : Component
{
    int _renders;

    public override Element Render()
    {
        _renders++;
        var a = UseSignal(2);
        var b = UseSignal(3);
        var product = UseComputed(() => a.Value * b.Value);
        return ControlExample.Build("Derived state — UseComputed (memo)",
            new BoxEl
            {
                Direction = 0, Gap = 10f, AlignItems = FlexAlign.Center,
                Children =
                [
                    Button.Standard("a++", () => a.Value = a.Peek() + 1),
                    Button.Standard("b++", () => b.Value = b.Peek() + 1),
                ],
            },
            description: "A memo caches its value and recomputes lazily when an input signal changes; readers subscribe through it. The readout binds Text through the memo — the host still never re-renders.",
            output: VStack(4, GalleryPage.LiveText(() => $"{a.Value} × {b.Value} = {product.Value}"), Caption($"host renders: {_renders}").Tertiary()),
            code: """
            var a = UseSignal(2);
            var b = UseSignal(3);
            var product = UseComputed(() => a.Value * b.Value);   // Memo<int>: cached, lazy

            new TextEl("") { Text = Prop.Of(() => $"{a.Value} × {b.Value} = {product.Value}") };
            """);
    }
}

/// <summary>Reactive control flow: Flow.Show mounts/unmounts a branch via a boundary effect — no host re-render.</summary>
sealed class StatePage_ShowHost : Component
{
    int _renders;

    public override Element Render()
    {
        _renders++;
        var open = UseSignal(false);
        var panel = new BoxEl
        {
            Padding = Edges4.All(12), Corners = Radii.ControlAll,
            Fill = Tok.AccentSubtle, BorderColor = Tok.AccentDefault with { A = 0.5f }, BorderWidth = 1f,
            Children = [Body("This branch is mounted. Toggling unmounts it — its component state is discarded, not hidden.")],
        };
        var fallback = Caption("Hidden — the branch is unmounted.").Tertiary();
        return ControlExample.Build("Reactive control flow — Flow.Show",
            VStack(10,
                Button.Standard("Toggle details", () => open.Value = !open.Peek()),
                Flow.Show(() => open.Value, panel, fallback)),
            description: "Flow.Show is a boundary effect: when the condition signal flips, it swaps its own children through the keyed reconciler. The enclosing component does not re-render.",
            output: VStack(4, GalleryPage.LiveText(() => open.Value ? "open" : "closed"), Caption($"host renders: {_renders}").Tertiary()),
            code: """
            var open = UseSignal(false);

            VStack(10,
                Button.Standard("Toggle details", () => open.Value = !open.Peek()),
                Flow.Show(() => open.Value, detailsPanel, fallback));
            """);
    }
}

/// <summary>Reactive control flow: Flow.For keyed list — add/remove/reverse diff rows by key, no host re-render.</summary>
sealed class StatePage_ForHost : Component
{
    int _renders;

    public override Element Render()
    {
        _renders++;
        var items = UseSignal(new List<string> { "Alpha", "Beta", "Gamma" });
        var nextId = UseRef(1);
        void Mutate(Action<List<string>> edit)
        {
            var next = new List<string>(items.Peek());
            edit(next);
            items.Value = next;   // a NEW list instance — signal writes are value-equality gated
        }
        return ControlExample.Build("Reactive control flow — Flow.For (keyed list)",
            VStack(12,
                new BoxEl
                {
                    Direction = 0, Gap = 8f,
                    Children =
                    [
                        Button.Standard("Add", () => Mutate(l => l.Add($"Item {nextId.Value++}"))),
                        Button.Standard("Remove first", () => Mutate(l => { if (l.Count > 0) l.RemoveAt(0); })),
                        Button.Standard("Reverse", () => Mutate(l => l.Reverse())),
                    ],
                },
                Flow.For<string>(() => items.Value, s => s, (s, i) => Row(s))),
            description: "Flow.For diffs its rows by key when the list signal changes: adds mount, removes unmount, moves reorder — row state is preserved by key, and the host never re-renders.",
            output: VStack(4, GalleryPage.LiveText(() => $"{items.Value.Count} items"), Caption($"host renders: {_renders}").Tertiary()),
            code: """
            var items = UseSignal(new List<string> { "Alpha", "Beta", "Gamma" });

            Flow.For(() => items.Value,     // snapshotted once per change
                     s => s,                // key: a stable unique per-item id (NOT the index)
                     (s, i) => Row(s));      // keyed: moves preserve row state

            // mutate by writing a NEW list instance:
            var next = new List<string>(items.Peek()); next.Reverse(); items.Value = next;
            """);
    }

    static Element Row(string label) => new BoxEl
    {
        Direction = 0, Gap = 8f, AlignItems = FlexAlign.Center, MinHeight = 32f, MaxWidth = 280f,
        Padding = new Edges4(12, 4, 12, 4), Margin = new Edges4(0, 0, 0, 4), Corners = Radii.ControlAll,
        Fill = Tok.FillControlDefault, BorderColor = Tok.StrokeControlDefault, BorderWidth = 1f,
        Children =
        [
            Icon(Icons.Tag, 12f).Foreground(Tok.AccentDefault),
            new TextEl(label) { Size = 13f, Color = Tok.TextPrimary },
        ],
    };
}

/// <summary>Folded state: UseReducer — transitions flow through a reducer; dispatch applies immediately.</summary>
sealed class StatePage_ReducerHost : Component
{
    int _renders;

    public override Element Render()
    {
        _renders++;
        var (s, dispatch) = UseReducer<int, int>((st, a) => st + a, 0);
        return ControlExample.Build("Folded state — UseReducer",
            new BoxEl
            {
                Direction = 0, Gap = 12f, AlignItems = FlexAlign.Center, Wrap = true,
                Children =
                [
                    Button.Accent("dispatch +5", () => dispatch(5)),
                    Button.Standard("dispatch −3", () => dispatch(-3)),
                ],
            },
            description: "State transitions flow through a reducer function; each button dispatches a delta action. Reading the folded state subscribes the component, so dispatch re-renders this card.",
            output: VStack(4, BodyStrong($"total = {s}"), Caption($"host renders: {_renders}").Tertiary()),
            code: """
            var (s, dispatch) = UseReducer<int, int>((state, action) => state + action, 0);

            HStack(12,
                Button.Accent("dispatch +5", () => dispatch(5)),
                Button.Standard("dispatch −3", () => dispatch(-3)))
            """);
    }
}

/// <summary>Context: a provider stores a signal per node; consumers subscribe — a change re-renders exactly them.</summary>
sealed class StatePage_ContextHost : Component
{
    public override Element Render()
    {
        var (level, setLevel) = UseState(1);
        return ControlExample.Build("Ambient state — Context (Ctx.Provide + UseContext)",
            new BoxEl
            {
                Direction = 1, Gap = 12f,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, Gap = 10f, AlignItems = FlexAlign.Center,
                        Children =
                        [
                            new TextEl("provided level:") { Size = 13f, Color = Tok.TextSecondary },
                            Button.Standard("− level", () => setLevel(level - 1)),
                            new TextEl(level.ToString()) { Size = 18f, Bold = true, Color = Tok.AccentDefault },
                            Button.Standard("+ level", () => setLevel(level + 1)),
                        ],
                    },
                    Ctx.Provide(StatePage.ThemeLevel, level, Embed.Comp(() => new StatePage_Consumer())),
                ],
            },
            description: "The provider stores a signal per node; UseContext resolves the nearest provider by walking the scene tree and subscribes — a value change re-renders exactly the consumers, with no prop drilling.",
            code: """
            public static readonly Context<int> ThemeLevel = new(1);   // a channel + default

            Ctx.Provide(StatePage.ThemeLevel, level, Embed.Comp(() => new Consumer()));

            sealed class Consumer : Component
            {
                public override Element Render()
                {
                    var x = UseContext(StatePage.ThemeLevel);   // reads + subscribes
                    return Ui.Text($"level {x}");
                }
            }
            """);
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
            Direction = 1, Gap = 8f, Padding = Edges4.All(14),
            Fill = Tok.FillSolidBase, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
            Corners = Radii.OverlayAll, AlignItems = FlexAlign.Center, MinWidth = 130f,
            Children =
            [
                new TextEl($"chip #{_id}") { Size = 12f, Color = Tok.TextTertiary },
                new TextEl(n.ToString()) { Size = 24f, Bold = true, Color = Tok.AccentDefault },
                Button.Standard($"count {n}", () => setN(n + 1)),
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
            Direction = 0, Gap = 10f, AlignItems = FlexAlign.Center, Padding = Edges4.All(12),
            Fill = Tok.AccentSubtle, BorderColor = Tok.AccentDefault with { A = 0.5f }, BorderWidth = 1f,
            Corners = Radii.ControlAll,
            Children =
            [
                new TextEl("consumer reads:") { Size = 13f, Color = Tok.TextSecondary },
                new TextEl($"level {x}") { Size = 18f, Bold = true, Color = Tok.AccentDefault },
            ],
        };
    }
}
