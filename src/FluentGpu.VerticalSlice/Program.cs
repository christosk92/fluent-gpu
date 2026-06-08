using System.Buffers;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Media;
using FluentGpu.Pal;
using FluentGpu.Input;
using FluentGpu.Layout;
using FluentGpu.Pal.Headless;
using FluentGpu.Reconciler;
using FluentGpu.Controls;
using FluentGpu.Render;
using FluentGpu.Rhi;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using static FluentGpu.Dsl.Ui;

// ── The component (authored exactly as in the spec) ───────────────────────────────
sealed class Counter : Component
{
    public override Element Render()
    {
        var (count, setCount) = UseState(0);
        return VStack(12,
            Heading($"Count: {count}"),
            HStack(8,
                Button.Accent("-", () => setCount(count - 1)),
                Button.Accent("+", () => setCount(count + 1))));
    }
}

// Nested composition: a parent embedding a stateful child component (its own hooks).
sealed class NestChild : Component
{
    public override Element Render()
    {
        var (n, setN) = UseState(0);
        return Button.Accent($"child {n}", () => setN(n + 1));
    }
}

sealed class NestParent : Component
{
    public override Element Render()
        => VStack(8, Heading("parent"), Embed.Comp(() => new NestChild()));
}

// Context: a provider feeds a value to a nested consumer component across the component boundary.
sealed class CtxConsumer : Component
{
    public override Element Render() => Ui.Text($"ctx {UseContext(Slice.NumCtx)}");
}

sealed class CtxParent : Component
{
    public override Element Render()
    {
        var (v, setV) = UseState(7);
        return VStack(4,
            Button.Accent("inc", () => setV(v + 1)),
            Ctx.Provide(Slice.NumCtx, v, Embed.Comp(() => new CtxConsumer())));
    }
}

sealed class HoverProbe : Component
{
    public override Element Render() => Button.Accent("hi", () => { });
}

sealed class AnimProbe : Component
{
    public float Target;
    public float Value;
    public override Element Render() { Value = UseAnimatedValue(Target, 100f); return Ui.Text("x"); }
}

// A component that animates ITS OWN node declaratively — UseSpring seeds a track on the host node (no per-frame re-render).
sealed class SpringProbe : Component
{
    public override Element Render()
    {
        UseSpring(AnimChannel.ScaleX, 1.2f, SpringParams.FromResponse(0.2f, 1f), Array.Empty<object>());
        return new BoxEl { Width = 20, Height = 20, Fill = ColorF.FromRgba(0, 128, 255) };
    }
}

// Probe component for the expanded hooks.
sealed class HookProbe : Component
{
    public int Dep = 1, MemoRuns, State, Memo;
    public Action<int>? Dispatch;
    public Ref<int>? RefBox;

    public override Element Render()
    {
        var (s, d) = UseReducer<int, int>((st, a) => st + a, 0);
        var m = UseMemo(() => { MemoRuns++; return Dep * 10; }, Dep);
        var r = UseRef(7);
        State = s; Dispatch = d; Memo = m; RefBox = r;
        return Ui.Text("probe");
    }
}

// A 200×200 ScrollView over a 20×40px=800px-tall column → proves layout-free scroll + clip culling.
sealed class ScrollProbe : Component
{
    public override Element Render()
    {
        var items = new Element[20];
        for (int i = 0; i < items.Length; i++)
            items[i] = new BoxEl { Width = 180, Height = 40, Fill = ColorF.FromRgba(40, 40, 40) };
        return new ScrollEl
        {
            Width = 200, Height = 200, Fill = ColorF.FromRgba(20, 20, 20),
            Content = new BoxEl { Direction = 1, Children = items },
        };
    }
}

// A 10,000-row virtualized list (40px uniform rows) in a 400px viewport → proves windowing + recycle at scale.
sealed class VirtualProbe : Component
{
    public const int N = 10_000;
    public override Element Render()
        => Virtual.List(N, 40f,
               renderItem: i => new BoxEl
               {
                   Height = 40, Fill = ColorF.FromRgba(30, 30, 30),
                   Children = [new TextEl($"row {i}") { Size = 12f }],
               },
               keyOf: i => "r" + i)
           with { Width = 300, Height = 400 };
}

// A NavigationView with 3 items + a footer → proves adaptive Expanded/Compact/Minimal display modes.
sealed class NavProbe : Component
{
    public override Element Render() => Embed.Comp(() => new NavigationView
    {
        Items = [new NavItem("home", "H", "Home"), new NavItem("search", "S", "Search"), new NavItem("lib", "L", "Library")],
        Footer = [new NavItem("settings", "G", "Settings")],
        Header = "Wavee",
        Content = key => new BoxEl { Children = [new TextEl("PAGE:" + key)] },
    });
}

// A 1,000-item virtualized 4-column card grid → proves 2-D (VirtualGrid) windowing + recycle.
sealed class VGridProbe : Component
{
    public const int N = 1000;
    public override Element Render()
        => Virtual.Grid(N, columns: 4, itemHeight: 100f, gap: 12f,
               renderItem: i => new BoxEl { Fill = ColorF.FromRgba(40, 40, 40), Children = [new TextEl($"#{i}") { Size = 12f }] },
               keyOf: i => "g" + i)
           with { Width = 520, Height = 400 };
}

// A 200-row variable-height list (heights 40/60/80) in a 300px viewport → proves the Fenwick extent table + anchoring.
sealed class VarProbe : Component
{
    public const int N = 200;
    public static float H(int i) => 40f + (i % 3) * 20f;   // 40, 60, 80, 40, …
    public override Element Render()
        => Virtual.VariableList(N, 50f,
               renderItem: i => new BoxEl { Height = H(i), Fill = ColorF.FromRgba(30, 30, 30) },
               keyOf: i => "v" + i)
           with { Width = 300, Height = 300 };
}

// An async image (album art) inside a box → proves the decode→ready→draw pipeline + residency pinning.
sealed class ImageProbe : Component
{
    public override Element Render() => new BoxEl
    {
        Width = 120, Height = 120, Padding = Edges4.All(10),
        Children = [Ui.Image("album/1.jpg", 80, 80, 6f)],
    };
}

// A component using the UseImage hook → renders a spinner while loading, the image once ready (state observability).
sealed class UseImageProbe : Component
{
    public static ImageState LastState;
    public override Element Render()
    {
        var b = UseImage("uimg", 64);
        LastState = b.State;
        return b.IsReady
            ? Ui.Image("uimg", 64, 64)
            : new BoxEl { Width = 64, Height = 64, Fill = ColorF.FromRgba(50, 50, 50) };   // "spinner" placeholder
    }
}

// An image with a BlurHash → proves the LQIP preview decodes + uploads instantly (before the full-res decode).
sealed class BlurHashProbe : Component
{
    public override Element Render() => new BoxEl
    {
        Width = 200, Height = 200,
        Children = [Ui.Image("album/9.jpg", 64, 64, 4f, blurHash: "LEHV6nWB2yk8pyo0adR*.7kCMdnj")],
    };
}

// Slider + ToggleButton + IconButton + ScrollBar driven by state → proves controlled controls + pointer drag input.
sealed class ControlsProbe : Component
{
    public float SliderVal, ScrollPos;
    public bool Toggled;
    public int IconClicks;

    public override Element Render()
    {
        var (sv, setSv) = UseState(0f);
        var (on, setOn) = UseState(false);
        var (sp, setSp) = UseState(0f);
        SliderVal = sv; Toggled = on; ScrollPos = sp;
        return new BoxEl
        {
            Direction = 1, Gap = 0,
            Children =
            [
                Slider.Create(sv, setSv, 200f, 24f),
                ToggleButton.Create("Shuffle", on, () => setOn(!on)),
                IconButton.Create("▶", () => IconClicks++),
                ScrollBar.Create(0.25f, sp, setSp, 200f),
            ],
        };
    }
}

// A 3-column uniform (Star) grid of 5 cells → proves track sizing + row-major auto-flow.
sealed class GridProbe : Component
{
    static Element Cell() => new BoxEl { Fill = ColorF.FromRgba(40, 40, 40) };
    public override Element Render()
        => Ui.UniformGrid(3, 10f, 50f, Cell(), Cell(), Cell(), Cell(), Cell()) with { Width = 320, Height = 400 };
}

// A STRETCH-width grid (no explicit Width) inside a column, followed by a sibling — the gallery shape (a UniformGrid
// is the body of a Section/card). The grid must MEASURE to its real content height so the column stacks the next
// sibling below it; if Measure can't see the available width it collapses to 0 and the sibling overlaps the grid's
// overflowing rows (the "messed-up layout" on the Images / CSS-Grid pages).
sealed class GridStretchProbe : Component
{
    static Element Cell() => new BoxEl { Fill = ColorF.FromRgba(40, 40, 40) };
    public override Element Render()
        => new BoxEl
        {
            Direction = 1,
            Gap = 10f,
            Width = 420f,   // the column has a width; the grid inside has none → it stretches to fill it
            Children =
            [
                Ui.UniformGrid(4, 12f, 90f, Cell(), Cell(), Cell(), Cell(), Cell(), Cell(), Cell(), Cell()),
                new TextEl("after") { Size = 14f, Color = ColorF.FromRgba(255, 255, 255) }
            ],
        };
}

// An auto-fill responsive grid (CSS repeat(auto-fill, minmax(120, 1fr))) in a 520-wide box: it must pack as many equal
// 1fr columns as fit at >=120 and stretch them to FILL the width (no ragged edge), reflowing the count with the width.
sealed class AutoGridProbe : Component
{
    static Element Cell() => new BoxEl { Fill = ColorF.FromRgba(40, 40, 40) };
    public override Element Render()
    {
        var cells = new Element[7];
        for (int i = 0; i < cells.Length; i++) cells[i] = Cell();
        return Ui.AutoGrid(120f, 10f, 50f, cells) with { Width = 520f };
    }
}

// The Wavee skeleton: a shell composing EVERY subsystem — sidebar nav → PageHost back stack; a Home page (album-art
// card Grid in a ScrollView) and a Playlist page (5,000-row virtualized track list with art thumbs); a now-playing
// PlayerBar (image + Slider + transport IconButtons + ToggleButton). This is the acceptance test for "can host Wavee".
sealed class WaveeShell : Component
{
    readonly Navigator _nav = new(new Route("home"));
    public Navigator Nav => _nav;

    public override Element Render()
    {
        var (playing, setPlaying) = UseState(false);
        var (seek, setSeek) = UseState(0.3f);
        return new BoxEl
        {
            Direction = 1,
            Children =
            [
                new BoxEl   // top: sidebar + page host
                {
                    Direction = 0, Grow = 1,
                    Children = [Sidebar(), Embed.Comp(() => new PageHost(_nav, Page))],
                },
                PlayerBar(playing, setPlaying, seek, setSeek),
            ],
        };
    }

    Element Sidebar() => new BoxEl
    {
        Width = 200, Direction = 1, Gap = 4, Padding = Edges4.All(12), Fill = ColorF.FromRgba(0x0E, 0x0E, 0x0E),
        Children = [NavItem("Home", "home"), NavItem("Search", "search"), NavItem("Your Library", "playlist")],
    };
    Element NavItem(string label, string route) => new BoxEl
    {
        Padding = new Edges4(10, 8, 10, 8), Corners = CornerRadius4.All(6), HoverFill = ColorF.FromRgba(0x22, 0x22, 0x22),
        OnClick = () => _nav.Push(route), Children = [new TextEl(label)],
    };

    Element Page(Route r) => r.Name == "playlist" ? Playlist() : Home();

    Element Home() => Ui.ScrollView(Ui.UniformGrid(4, 16f, 210f, AlbumCards()));
    Element[] AlbumCards()
    {
        var a = new Element[12];
        for (int i = 0; i < a.Length; i++) a[i] = AlbumCard(i);
        return a;
    }
    Element AlbumCard(int i) => new BoxEl
    {
        Direction = 1, Gap = 8, Padding = Edges4.All(8), Corners = CornerRadius4.All(8),
        HoverFill = ColorF.FromRgba(0x1E, 0x1E, 0x1E), OnClick = () => _nav.Push("playlist", "p" + i),
        Children = [Ui.Image("album/" + i, 150, 150, 6f), new TextEl("Album " + i) { Bold = true }, new TextEl("Artist") { Size = 12 }],
    };

    Element Playlist() => Virtual.List(5000, 56f, TrackRow, keyOf: i => "t" + i) with { Grow = 1f };
    Element TrackRow(int i) => new BoxEl
    {
        Direction = 0, Height = 56, Gap = 12, AlignItems = FlexAlign.Center, Padding = new Edges4(16, 8, 16, 8),
        HoverFill = ColorF.FromRgba(0x22, 0x22, 0x22), OnClick = () => { },
        Children =
        [
            new TextEl((i + 1).ToString()) { Size = 12 },
            Ui.Image("art/" + i, 40, 40, 4f),
            new BoxEl { Direction = 1, Grow = 1, Children = [new TextEl("Track " + i), new TextEl("Artist") { Size = 12 }] },
            new TextEl("3:21") { Size = 12 },
        ],
    };

    Element PlayerBar(bool playing, Action<bool> setPlaying, float seek, Action<float> setSeek) => new BoxEl
    {
        Direction = 0, Height = 80, AlignItems = FlexAlign.Center, Gap = 16, Padding = new Edges4(16, 0, 16, 0),
        Fill = ColorF.FromRgba(0x18, 0x18, 0x18),
        Children =
        [
            Ui.Image("nowplaying", 56, 56, 4f),
            new BoxEl { Direction = 1, Width = 150, Children = [new TextEl("Now Playing") { Bold = true }, new TextEl("Artist") { Size = 12 }] },
            IconButton.Create("⏮", () => { }),
            IconButton.Create(playing ? "⏸" : "▶", () => setPlaying(!playing)),
            IconButton.Create("⏭", () => { }),
            Slider.Create(seek, setSeek, 220f),
            ToggleButton.Create("Shuffle", false, () => { }),
        ],
    };
}

// ── Signals-first probes: granular re-render, the compositor bypass, reactive control-flow ──
static class Gran { public static int[] Counts = new int[2]; public static int Parent; }

sealed class GranChild : Component
{
    private readonly int _id;
    public GranChild(int id) => _id = id;
    public override Element Render()
    {
        Gran.Counts[_id]++;
        var (n, setN) = UseState(0);
        return new BoxEl { Width = 100, Height = 30, OnClick = () => setN(n + 1), Children = [Text($"c{_id}:{n}")] };
    }
}

sealed class GranParent : Component
{
    public override Element Render()
    {
        Gran.Parent++;
        return new BoxEl { Direction = 1, Children = [Embed.Comp(() => new GranChild(0)), Embed.Comp(() => new GranChild(1))] };
    }
}

// A signal bound straight to the slider — a drag updates node transforms only (no re-render / reconcile / layout).
sealed class SliderSignalProbe : Component
{
    public static int Renders;
    public FloatSignal? Sig;
    public override Element Render()
    {
        Renders++;
        var sig = UseFloatSignal(0.3f);
        Sig = sig;
        return Slider.Bind(sig, onChange: null, width: 200f, height: 24f);
    }
}

// Reactive control-flow: For (keyed list) + Show (conditional) update structure with NO parent re-render.
sealed class FlowProbe : Component
{
    public static int Renders;
    public Signal<int>? Count;
    public Signal<bool>? Toggle;
    public override Element Render()
    {
        Renders++;
        var count = UseSignal(3);
        var show = UseSignal(true);
        Count = count; Toggle = show;
        return new BoxEl
        {
            Direction = 1,
            Children =
            [
                Flow.For(() => count.Value, i => new BoxEl { Key = "r" + i, Width = 40, Height = 12, Children = [Text("row" + i)] }),
                Flow.Show(() => show.Value, new BoxEl { Width = 40, Height = 12, Children = [Text("SHOWN")] }, new BoxEl { Width = 40, Height = 12, Children = [Text("HIDDEN")] }),
            ],
        };
    }
}

// ── Basic-input infrastructure probes (overlay / text input / repeat) ─────────────
sealed class RepeatProbe : Component
{
    public int Clicks;
    public override Element Render() => RepeatButton.Create("+", () => Clicks++);
}

sealed class EditTextProbe : Component
{
    public Signal<string>? Text;
    public override Element Render()
    {
        var t = UseSignal("");
        Text = t;
        return Embed.Comp(() => new EditableText { Text = t, Width = 160, Sanitize = s => s.Length > 8 ? s[..8] : s });
    }
}

// Hosts an overlay layer and exposes the ambient service + an anchored button so a test can open/close flyouts.
sealed class OverlayProbe : Component
{
    public IOverlayService? Service;
    public NodeHandle Anchor;
    public int Selected = -1;
    public override Element Render() => Embed.Comp(() => new OverlayHost { Child = Embed.Comp(() => new OverlayProbeInner(this)) });
}

sealed class OverlayProbeInner : Component
{
    readonly OverlayProbe _p;
    public OverlayProbeInner(OverlayProbe p) => _p = p;
    public override Element Render()
    {
        _p.Service = UseContext(Overlay.Service);
        return new BoxEl
        {
            Width = 200, Height = 120, Padding = Edges4.All(20),
            Children =
            [
                new BoxEl
                {
                    Width = 120, Height = 32, Role = AutomationRole.Button, OnClick = () => { },
                    OnRealized = h => _p.Anchor = h,
                    Children = [Text("anchor")],
                },
            ],
        };
    }
}

sealed class CheckBoxProbe : Component
{
    public CheckState State;
    public override Element Render()
    {
        var (st, setSt) = UseState(CheckState.Unchecked);
        State = st;
        return CheckBox.Create("opt", st, next => setSt(next));
    }
}

sealed class RadioProbe : Component
{
    public int Selected = -1;
    public override Element Render()
    {
        var (sel, setSel) = UseState(-1);
        Selected = sel;
        return RadioButton.Group(new[] { "A", "B", "C" }, sel, setSel);
    }
}

sealed class ToggleSwitchProbe : Component
{
    public bool On;
    public override Element Render()
    {
        var (on, setOn) = UseState(false);
        On = on;
        return ToggleSwitch.Create(on, () => setOn(!on));
    }
}

sealed class RatingProbe : Component
{
    public FloatSignal? Val;
    public override Element Render()
    {
        var v = UseFloatSignal(0f);
        Val = v;
        return RatingControl.Create(v);
    }
}

sealed class ComboProbe : Component
{
    public Signal<int>? Sel;
    public Signal<string>? Txt;
    readonly bool _editable;
    public ComboProbe(bool editable) => _editable = editable;
    public override Element Render()
    {
        var sel = UseSignal(-1); Sel = sel;
        var txt = UseSignal(""); Txt = txt;
        return Embed.Comp(() => new OverlayHost { Child = ComboBox.Create(new[] { "Red", "Green", "Blue" }, sel, _editable, txt, 200f, "pick") });
    }
}

sealed class RangeSliderProbe : Component
{
    public float Val;
    public override Element Render()
    {
        var (v, setV) = UseState(0f);
        Val = v;
        return Slider.Ranged(v, setV, new Slider.Options { Min = 0f, Max = 100f, Step = 10f, TickFrequency = 20f }, length: 200f, thickness: 32f);
    }
}

sealed class ColorPickerProbe : Component
{
    public Signal<ColorF>? Color;
    public override Element Render()
    {
        var c = UseSignal(ColorF.FromRgba(255, 0, 0));
        Color = c;
        return ColorPicker.Create(c, alphaEnabled: true);
    }
}

sealed class SplitButtonProbe : Component
{
    public int Invoked;
    public override Element Render()
        => SplitButton.Create("Paste", () => Invoked++, [new MenuFlyoutItem("Paste as text", Icons.Document)], Icons.Document);
}

sealed class NavHierarchyProbe : Component
{
    public override Element Render() => Embed.Comp(() => new NavigationView
    {
        Initial = "home",
        Items =
        [
            new NavItem("home", "H", "Home"),
            new NavItem("group", "G", "Group")
            {
                Children = [new NavItem("c1", "1", "ChildOne"), new NavItem("c2", "2", "ChildTwo")],
            },
        ],
        Content = key => new BoxEl { Children = [Ui.Text("PAGE:" + key)] },
    });
}

// ── The harness: run the slice end-to-end on the headless backends + assert ───────
static class Slice
{
    static int s_failures;
    public static readonly Context<int> NumCtx = new(0);

    static void Check(string name, bool ok, string? detail = null)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail is null ? "" : $"  ({detail})")}");
        if (!ok) s_failures++;
    }

    static NodeHandle Child(SceneStore s, NodeHandle parent, int index)
    {
        var c = s.FirstChild(parent);
        for (int i = 0; i < index && !c.IsNull; i++) c = s.NextSibling(c);
        return c;
    }

    static bool HasGlyph(HeadlessGpuDevice dev, StringTable strings, string text)
    {
        foreach (var g in dev.LastGlyphs)
            if (strings.Resolve(g.Text) == text) return true;
        return false;
    }

    static bool Near(float a, float b) => MathF.Abs(a - b) < 0.5f;
    static bool Near(float a, float b, float tol) => MathF.Abs(a - b) < tol;

    // Depth-first search for the first node carrying a given automation role (for locating controls in tests).
    static NodeHandle FindRole(SceneStore s, NodeHandle n, AutomationRole role)
    {
        if (n.IsNull) return NodeHandle.Null;
        if (s.Interaction(n).Role == role) return n;
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
        {
            var r = FindRole(s, c, role);
            if (!r.IsNull) return r;
        }
        return NodeHandle.Null;
    }

    static Point2 CenterOf(SceneStore s, NodeHandle n)
    {
        var r = s.AbsoluteRect(n);
        return new Point2(r.X + r.W / 2f, r.Y + r.H / 2f);
    }

    static void CollectRole(SceneStore s, NodeHandle n, AutomationRole role, List<NodeHandle> outList)
    {
        if (n.IsNull) return;
        if (s.Interaction(n).Role == role) outList.Add(n);
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c)) CollectRole(s, c, role, outList);
    }

    static List<NodeHandle> Roles(SceneStore s, AutomationRole role)
    {
        var list = new List<NodeHandle>();
        CollectRole(s, s.Root, role, list);
        return list;
    }

    static void ClickNode(AppHost host, HeadlessWindow window, NodeHandle n)
    {
        var c = CenterOf(host.Scene, n);
        window.QueueInput(new InputEvent(InputKind.PointerDown, c, 0, 0));
        window.QueueInput(new InputEvent(InputKind.PointerUp, c, 0, 0));
        host.RunFrame();
    }

    static SceneStore LayoutTree(StringTable strings, Element tree)
    {
        var scene = new SceneStore();
        new TreeReconciler(scene, strings).ReconcileRoot(tree, null);
        new FlexLayout(scene, new HeadlessFontSystem(strings)).Run(scene.Root);
        return scene;
    }

    // Golden flexbox checks (deterministic, no text): justify-content, flex-grow, align-items.
    static void FlexChecks(StringTable strings)
    {
        // justify space-between: row 300 wide, two 40-wide boxes → x = 0 and 260
        var sb = LayoutTree(strings, new BoxEl
        {
            Direction = 0, Width = 300, Height = 50, Justify = FlexJustify.SpaceBetween,
            Children = [new BoxEl { Width = 40, Height = 20 }, new BoxEl { Width = 40, Height = 20 }],
        });
        var a = sb.AbsoluteRect(Child(sb, sb.Root, 0));
        var b = sb.AbsoluteRect(Child(sb, sb.Root, 1));
        Check("10. justify space-between", Near(a.X, 0) && Near(b.X, 260) && Near(b.W, 40), $"x0={a.X:0.#} x1={b.X:0.#}");

        // flex-grow: row 300 wide, two grow:1 children → each 150 wide at x = 0 and 150
        var gr = LayoutTree(strings, new BoxEl
        {
            Direction = 0, Width = 300, Height = 40,
            Children = [new BoxEl { Grow = 1, Height = 20 }, new BoxEl { Grow = 1, Height = 20 }],
        });
        var g0 = gr.AbsoluteRect(Child(gr, gr.Root, 0));
        var g1 = gr.AbsoluteRect(Child(gr, gr.Root, 1));
        Check("11. flex-grow splits free space", Near(g0.W, 150) && Near(g1.W, 150) && Near(g1.X, 150), $"w0={g0.W:0.#} x1={g1.X:0.#}");

        // align-items center on the cross axis: row 100 tall, child 20 tall → y = 40
        var al = LayoutTree(strings, new BoxEl
        {
            Direction = 0, Width = 200, Height = 100, AlignItems = FlexAlign.Center,
            Children = [new BoxEl { Width = 40, Height = 20 }],
        });
        var ac = al.AbsoluteRect(Child(al, al.Root, 0));
        Check("12. align-items center", Near(ac.Y, 40), $"y={ac.Y:0.#}");

        // padding + gap: column, padding 10, gap 8, two 20-tall children → y = 10 and 38
        var pg = LayoutTree(strings, new BoxEl
        {
            Direction = 1, Width = 100, Height = 200, Padding = Edges4.All(10), Gap = 8,
            Children = [new BoxEl { Width = 40, Height = 20 }, new BoxEl { Width = 40, Height = 20 }],
        });
        var p0 = pg.AbsoluteRect(Child(pg, pg.Root, 0));
        var p1 = pg.AbsoluteRect(Child(pg, pg.Root, 1));
        Check("13. padding + gap stacking", Near(p0.Y, 10) && Near(p1.Y, 38) && Near(p0.X, 10), $"y0={p0.Y:0.#} y1={p1.Y:0.#}");
    }

    // UseReducer / UseMemo / UseRef exercised through a real Component across renders.
    static void HookChecks()
    {
        var p = new HookProbe();

        p.RenderWithHooks();   // frame 1 (mount)
        var r1 = p.RefBox;
        bool ok1 = p.State == 0 && p.Memo == 10 && p.MemoRuns == 1 && r1!.Value == 7;

        p.Dispatch!(5); p.Dispatch!(3);   // fold: 0+5=5, +3=8 (a reducer dispatch applies to the signal immediately)
        r1!.Value = 42;

        p.RenderWithHooks();   // frame 2 (same dep)
        bool ok2 = p.State == 8 && p.Memo == 10 && p.MemoRuns == 1 && ReferenceEquals(p.RefBox, r1) && p.RefBox!.Value == 42;

        p.Dep = 2;
        p.RenderWithHooks();   // frame 3 (changed dep)
        bool ok3 = p.Memo == 20 && p.MemoRuns == 2;

        Check("14. UseReducer folds dispatches", ok1 && p.State == 8, "0 →(+5,+3)→ 8");
        Check("15. UseMemo recomputes only on dep change", ok2 && ok3, $"memoRuns={p.MemoRuns}");
        Check("16. UseRef persists & is stable", p.RefBox!.Value == 42 && ReferenceEquals(p.RefBox, r1));
    }

    // Keyed reconcile: reorder preserves node identity (state), removal frees only the dropped key.
    static void KeyedChecks(StringTable strings)
    {
        var scene = new SceneStore();
        var recon = new TreeReconciler(scene, strings);

        static Element Row(params string[] keys)
        {
            var ch = new Element[keys.Length];
            for (int i = 0; i < keys.Length; i++) ch[i] = new BoxEl { Key = keys[i], Width = 10, Height = 10 };
            return new BoxEl { Direction = 0, Children = ch };
        }

        var t1 = Row("a", "b", "c");
        recon.ReconcileRoot(t1, null);
        var hA = Child(scene, scene.Root, 0);
        var hB = Child(scene, scene.Root, 1);
        var hC = Child(scene, scene.Root, 2);

        var t2 = Row("c", "a", "b");
        recon.ReconcileRoot(t2, t1);
        bool reordered = Child(scene, scene.Root, 0) == hC && Child(scene, scene.Root, 1) == hA && Child(scene, scene.Root, 2) == hB;
        Check("17. keyed reconcile reorders, preserving identity", reordered, "[a,b,c] → [c,a,b]");

        var t3 = Row("a", "b");
        recon.ReconcileRoot(t3, t2);
        int count = 0;
        for (var c = scene.FirstChild(scene.Root); !c.IsNull; c = scene.NextSibling(c)) count++;
        bool removed = count == 2 && Child(scene, scene.Root, 0) == hA && Child(scene, scene.Root, 1) == hB;
        Check("18. keyed reconcile removes only the dropped key", removed, "[c,a,b] → [a,b]");
    }

    // Focus + keyboard routing: Tab cycles focus, Enter activates a clickable, keys bubble (Handled stops).
    static void KeyboardChecks(StringTable strings)
    {
        var scene = new SceneStore();
        var recon = new TreeReconciler(scene, strings);
        var dispatcher = new InputDispatcher(scene);

        bool clicked = false, innerSaw = false; int rootSaw = 0;
        var tree = new BoxEl
        {
            Direction = 0,
            OnKeyDown = a => rootSaw = a.KeyCode,                          // ancestor (bubble target)
            Children =
            [
                new BoxEl { Key = "b1", Width = 20, Height = 20, OnClick = () => clicked = true },
                new BoxEl { Key = "b2", Width = 20, Height = 20, Focusable = true,
                    OnKeyDown = a => { innerSaw = true; if (a.KeyCode == Keys.Escape) a.Handled = true; } },
            ],
        };
        recon.ReconcileRoot(tree, null);
        new FlexLayout(scene, new HeadlessFontSystem(strings)).Run(scene.Root);
        var b1 = Child(scene, scene.Root, 0);
        var b2 = Child(scene, scene.Root, 1);

        dispatcher.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Tab) });
        var f1 = dispatcher.Focused;
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Tab) });
        var f2 = dispatcher.Focused;
        Check("19. Tab cycles focus through focusables", f1 == b1 && f2 == b2, "→ b1 → b2");

        dispatcher.SetFocus(b1);
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Enter) });
        Check("20. Enter activates the focused clickable", clicked, "OnClick fired via keyboard");

        dispatcher.SetFocus(b2);
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Down) });   // not handled → bubbles
        bool bubbled = innerSaw && rootSaw == Keys.Down;
        innerSaw = false; rootSaw = 0;
        dispatcher.SetFocus(b2);
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Escape) });  // b2 marks Handled → stops
        bool stopped = innerSaw && rootSaw == 0;
        Check("21. keys bubble to ancestor; Handled stops propagation", bubbled && stopped);
    }

    // Animation timelines: eased opacity completes (PaintDirty, never LayoutDirty); translate marks TransformDirty.
    static void AnimChecks()
    {
        var scene = new SceneStore();
        var node = scene.CreateNode(1);
        scene.Root = node;
        var engine = new AnimEngine(scene);

        scene.Paint(node).Opacity = 0f;
        scene.Flags(node) &= ~(NodeFlags.PaintDirty | NodeFlags.LayoutDirty | NodeFlags.TransformDirty);
        engine.Animate(node, AnimChannel.Opacity, 0f, 1f, 100f, Easing.Linear);
        engine.Tick(50f);
        float op = scene.Paint(node).Opacity;
        var fl = scene.Flags(node);
        bool midOk = MathF.Abs(op - 0.5f) < 0.02f && (fl & NodeFlags.PaintDirty) != 0 && (fl & NodeFlags.LayoutDirty) == 0;
        engine.Tick(60f);   // 110ms > 100ms → complete
        bool doneOk = MathF.Abs(scene.Paint(node).Opacity - 1f) < 0.001f && !engine.HasActive;
        Check("22. opacity timeline eases & completes (no relayout)", midOk && doneOk, $"@50ms={op:0.00}");

        scene.Flags(node) &= ~(NodeFlags.TransformDirty | NodeFlags.LayoutDirty);
        engine.Animate(node, AnimChannel.TranslateX, 0f, 100f, 100f, Easing.Linear);
        engine.Tick(25f);
        float dx = scene.Paint(node).LocalTransform.Dx;
        var fl2 = scene.Flags(node);
        bool transOk = MathF.Abs(dx - 25f) < 0.5f && (fl2 & NodeFlags.TransformDirty) != 0 && (fl2 & NodeFlags.LayoutDirty) == 0;
        Check("23. translate timeline marks TransformDirty only", transOk, $"@25ms dx={dx:0.0}");
    }

    // General layout-transition projection (continuous FLIP): the side-table plumbing, the spring that drives a moved
    // node's presented offset → 0, and the velocity-continuous reframe that keeps an interrupted move from jumping.
    static void ProjectionChecks(StringTable strings)
    {
        // 23a — BoxEl.Animate wires the BoundsAnimated flag + the per-node transition side-table (Phase 0 plumbing).
        {
            var scene = new SceneStore();
            var engine = new AnimEngine(scene);
            var recon = new TreeReconciler(scene, strings) { Anim = engine };
            recon.ReconcileRoot(new BoxEl { Animate = LayoutTransition.Slide, Width = 50, Height = 20 }, null);
            var root = scene.Root;
            bool flagSet = (scene.Flags(root) & NodeFlags.BoundsAnimated) != 0;
            bool roundTrip = engine.TryGetTransition(root, out var spec) && spec.Channels == TransitionChannels.Position;
            // dropping Animate clears both
            recon.ReconcileRoot(new BoxEl { Width = 50, Height = 20 }, new BoxEl { Animate = LayoutTransition.Slide, Width = 50, Height = 20 });
            bool cleared = (scene.Flags(root) & NodeFlags.BoundsAnimated) == 0 && !engine.TryGetTransition(root, out _);
            Check("23a. BoxEl.Animate ↔ BoundsAnimated + transition side-table (set/clear)", flagSet && roundTrip && cleared);
        }

        // 23b — a moved node FLIPs: the presented offset springs old→new monotonically and settles, never relaying out.
        {
            var scene = new SceneStore();
            var n = scene.CreateNode(1); scene.Root = n;
            ref RectF nb = ref scene.Bounds(n); nb = new RectF(0, 200, 50, 20);   // final laid-out position
            var engine = new AnimEngine(scene);
            scene.Flags(n) &= ~(NodeFlags.TransformDirty | NodeFlags.LayoutDirty);
            var crit = new LayoutTransition(TransitionChannels.Position, TransitionDynamics.Spring(0.18f, 1.0f));  // critically damped → no overshoot
            engine.AnimateBounds(n, new RectF(0, 100, 50, 20), new RectF(0, 200, 50, 20), crit);  // was at y=100, now laid out at y=200

            bool monotonic = true; float prev = -1e9f; int settledAt = -1;
            for (int i = 0; i < 80 && settledAt < 0; i++)
            {
                engine.Tick(16f);
                float dy = scene.Paint(n).LocalTransform.Dy;
                if (i > 0 && dy < prev - 0.6f) monotonic = false;   // offset climbs -100 → 0
                prev = dy;
                if (!engine.HasActive) settledAt = i;
            }
            var f = scene.Flags(n);
            bool noRelayout = (f & NodeFlags.LayoutDirty) == 0 && (f & NodeFlags.TransformDirty) != 0;
            bool settledZero = settledAt >= 0 && MathF.Abs(scene.Paint(n).LocalTransform.Dy) < 0.5f;
            Check("23b. projection FLIPs a moved node (offset springs → 0, settles, no relayout)",
                monotonic && settledZero && noRelayout, $"settled@{settledAt}");
        }

        // 23c — interruption is velocity-continuous: re-projecting mid-flight must NOT snap the presented position
        // (the old code overwrote the transform, losing the in-flight offset → the visible jump).
        {
            var scene = new SceneStore();
            var n = scene.CreateNode(1); scene.Root = n;
            ref RectF nb = ref scene.Bounds(n); nb = new RectF(0, 200, 50, 20);
            var engine = new AnimEngine(scene);
            // a slow spring keeps per-tick motion tiny (~3px), so the continuity test isolates the reframe (≈3px) from
            // the old overwrite bug (which loses the in-flight offset → a ~90px snap).
            var spring = new LayoutTransition(TransitionChannels.Position, TransitionDynamics.Spring(1.0f, 1.0f));
            engine.AnimateBounds(n, new RectF(0, 100, 50, 20), new RectF(0, 200, 50, 20), spring);
            for (int i = 0; i < 5; i++) engine.Tick(16f);
            float d = scene.Paint(n).LocalTransform.Dy;     // in-flight offset (large for a slow spring)
            float presentedBefore = nb.Y + d;               // its on-screen Y this instant
            // it moves again to y=300; layout snaps Bounds, the transform is unchanged → toAbs = 300 + d
            nb = new RectF(0, 300, 50, 20);
            engine.AnimateBounds(n, new RectF(0, presentedBefore, 50, 20), new RectF(0, 300f + d, 50, 20), spring);
            engine.Tick(16f);
            float presentedAfter = 300f + scene.Paint(n).LocalTransform.Dy;
            bool continuous = MathF.Abs(presentedAfter - presentedBefore) < 15f;
            Check("23c. projection reframes on interruption (velocity-continuous, no jump)",
                continuous, $"presented {presentedBefore:0.0}→{presentedAfter:0.0}");
        }

        // 23d — Reveal (size): the presented extent springs old→new with NO relayout (model Bounds stay final), and
        // resets to NaN on settle so the recorder falls back to the layout size. This replaces the deleted Width channel.
        {
            var scene = new SceneStore();
            var n = scene.CreateNode(1); scene.Root = n;
            ref RectF nb = ref scene.Bounds(n); nb = new RectF(0, 0, 48, 600);   // final (collapsed) model width
            scene.Flags(n) &= ~NodeFlags.LayoutDirty;
            var engine = new AnimEngine(scene);
            var reveal = LayoutTransition.BoundsT(SizeMode.Reveal) with { Dynamics = TransitionDynamics.Spring(0.18f, 1.0f) };
            engine.AnimateBounds(n, new RectF(0, 0, 320, 600), new RectF(0, 0, 48, 600), reveal);  // collapsing 320 → 48
            engine.Tick(16f);
            float firstW = scene.Paint(n).PresentedW;                    // presented starts near 320 (not snapped to 48)
            bool startedWide = firstW > 200f;
            bool noRelayout = (scene.Flags(n) & NodeFlags.LayoutDirty) == 0;
            bool modelFinal = Near(scene.Bounds(n).W, 48f);              // only the presented extent animates
            int settledAt = -1;
            for (int i = 0; i < 90 && settledAt < 0; i++) { engine.Tick(16f); if (!engine.HasActive) settledAt = i; }
            bool resetNaN = float.IsNaN(scene.Paint(n).PresentedW);      // on settle, falls back to the (final) layout size
            Check("23d. Reveal springs presented size (no relayout, model final, resets on settle)",
                startedWide && noRelayout && modelFinal && resetNaN && settledAt >= 0, $"firstW={firstW:0} settled@{settledAt}");
        }
    }

    // Enter/exit lifecycle: a removed node with Exit.Active is kept live (an orphan) and drawn while it fades, then
    // deferred-freed on settle; a mounted node with Enter.Active appears from its enter terminal.
    static void EnterExitChecks(StringTable strings)
    {
        // 23e — exit orphan: removing the child keeps it live + drawing until its fade settles, then reclaims (gen bump).
        {
            var scene = new SceneStore();
            var engine = new AnimEngine(scene);
            var recon = new TreeReconciler(scene, strings) { Anim = engine };
            var exit = new LayoutTransition(TransitionChannels.Opacity, TransitionDynamics.Spring(0.12f, 1f),
                Exit: new EnterExit(Opacity: 0f, Active: true));
            Element Tree(bool present) => new BoxEl
            {
                Width = 100, Height = 100,
                Children = present ? [new BoxEl { Key = "x", Width = 50, Height = 20, Animate = exit }] : [],
            };
            var old = Tree(true);
            recon.ReconcileRoot(old, null);
            new FlexLayout(scene, new HeadlessFontSystem(strings)).Run(scene.Root);
            var child = Child(scene, scene.Root, 0);
            bool mountedLive = scene.IsLive(child);

            recon.ReconcileRoot(Tree(false), old);                       // remove → orphan + seed exit
            bool orphaned = scene.IsOrphan(child) && scene.IsLive(child) && scene.OrphanCount == 1;

            int settledAt = -1;
            for (int i = 0; i < 90 && settledAt < 0; i++)
            {
                engine.Tick(16f);
                for (int k = scene.OrphanCount - 1; k >= 0; k--)        // host's ReclaimSettledOrphans
                { var o = scene.OrphanAt(k, out _, out _); if (!engine.HasTracks(o)) scene.ReclaimOrphan(o); }
                if (scene.OrphanCount == 0) settledAt = i;
            }
            bool reclaimed = settledAt >= 0 && !scene.IsLive(child);     // deferred free → handle dead
            Check("23e. exit orphan stays live while fading, then reclaims (deferred free)",
                mountedLive && orphaned && reclaimed, $"settled@{settledAt}");
        }

        // 23f — enter: a mounted node with Enter.Active starts at the enter terminal (opacity 0) and springs to 1.
        {
            var scene = new SceneStore();
            var engine = new AnimEngine(scene);
            var recon = new TreeReconciler(scene, strings) { Anim = engine };
            var enter = new LayoutTransition(TransitionChannels.Opacity, TransitionDynamics.Spring(0.15f, 1f),
                Enter: new EnterExit(Opacity: 0f, Active: true));
            recon.ReconcileRoot(new BoxEl { Width = 100, Height = 100, Children = [new BoxEl { Width = 50, Height = 20, Animate = enter }] }, null);
            new FlexLayout(scene, new HeadlessFontSystem(strings)).Run(scene.Root);
            var child = Child(scene, scene.Root, 0);
            engine.Tick(16f);
            float a1 = scene.Paint(child).Opacity;                      // entering: near 0
            for (int i = 0; i < 90; i++) engine.Tick(16f);
            float a2 = scene.Paint(child).Opacity;                      // settled to 1
            Check("23f. enter animates a mounted node from the enter terminal (opacity 0 → 1)",
                a1 < 0.5f && Near(a2, 1f), $"opacity {a1:0.00}→{a2:0.00}");
        }
    }

    // The opt-in size modes: ScaleCorrect (GPU scale → 1, compositor-only) and Relayout (re-solve the subtree at the
    // interpolated size each tick → live text reflow), both via the same general AnimateBounds entry point.
    static void SizeModeChecks(StringTable strings)
    {
        // 23g — ScaleCorrect: a grown node starts scaled-down and springs its scale to 1, never relaying out.
        {
            var scene = new SceneStore();
            var n = scene.CreateNode(1); scene.Root = n;
            ref RectF nb = ref scene.Bounds(n); nb = new RectF(0, 0, 200, 100);
            scene.Flags(n) &= ~NodeFlags.LayoutDirty;
            var engine = new AnimEngine(scene);
            var sc = LayoutTransition.BoundsT(SizeMode.ScaleCorrect) with { Dynamics = TransitionDynamics.Spring(0.2f, 1f) };
            engine.AnimateBounds(n, new RectF(0, 0, 100, 100), new RectF(0, 0, 200, 100), sc);  // width 100→200 ⇒ scaleX 0.5→1
            engine.Tick(16f);
            float m11a = scene.Paint(n).LocalTransform.M11;
            bool noRelayout = (scene.Flags(n) & NodeFlags.LayoutDirty) == 0;
            int settledAt = -1;
            for (int i = 0; i < 90 && settledAt < 0; i++) { engine.Tick(16f); if (!engine.HasActive) settledAt = i; }
            float m11b = scene.Paint(n).LocalTransform.M11;
            Check("23g. ScaleCorrect springs the node scale → 1 (compositor-only, no relayout)",
                m11a > 0.3f && m11a < 0.7f && Near(m11b, 1f, 0.02f) && noRelayout && settledAt >= 0, $"M11 {m11a:0.00}→{m11b:0.00}");
        }

        // 23h — Relayout: the node's MODEL width interpolates via scoped RunSubtree (so its content re-solves live).
        {
            var scene = new SceneStore();
            var fonts = new HeadlessFontSystem(strings);
            var engine = new AnimEngine(scene);
            var layout = new FlexLayout(scene, fonts);
            var recon = new TreeReconciler(scene, strings) { Anim = engine };
            var rel = LayoutTransition.BoundsT(SizeMode.Relayout) with { Dynamics = TransitionDynamics.Spring(0.2f, 1f) };
            recon.ReconcileRoot(new BoxEl { Width = 100, Height = 200, Animate = rel,
                Children = [new TextEl("the quick brown fox jumps over the lazy dog") { Wrap = TextWrap.Wrap }] }, null);
            layout.Run(scene.Root, new Size2(400, 200));
            var panel = scene.Root;
            engine.AnimateBounds(panel, new RectF(0, 0, 300, 200), new RectF(0, 0, 100, 200), rel);   // 300 → 100
            bool relayouting = (scene.Flags(panel) & NodeFlags.Relayouting) != 0;
            float midW = -1f; int runs = 0;
            for (int i = 0; i < 8; i++)
            {
                engine.Tick(16f);
                runs += engine.IncrementalRoots.Count;                // exactly one root re-solves per tick (scoped, not full-tree)
                foreach (var r in engine.IncrementalRoots)
                {
                    ref LayoutInput li = ref scene.Layout(r);
                    ref NodePaint pp = ref scene.Paint(r);
                    if (!float.IsNaN(pp.PresentedW)) li.Width = pp.PresentedW;
                    layout.RunSubtree(r);
                }
                engine.IncrementalRoots.Clear();
                if (i == 1) midW = scene.Bounds(panel).W;
            }
            bool interpolated = midW > 105f && midW < 300f;          // model width genuinely moved through the range
            Check("23h. Relayout re-solves only the subtree at the interpolated size (live reflow)",
                relayouting && interpolated && runs >= 2, $"midW={midW:0} runs={runs}");
        }
    }

    // Nested stateful component: renders, owns its own UseState, and re-renders on its own setState.
    static void NestedChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("nest", new Size2(480, 320), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var root = new NestParent();
        using var host = new AppHost(app, window, device, fonts, strings, root);

        host.RunFrame();
        bool mounted = HasGlyph(device, strings, "child 0");

        // parent VStack → [Heading, componentHost]; host → [Button]
        var compHost = Child(host.Scene, host.Scene.Root, 1);
        var btn = Child(host.Scene, compHost, 0);
        var r = host.Scene.AbsoluteRect(btn);
        var center = new Point2(r.X + r.W / 2f, r.Y + r.H / 2f);
        window.QueueInput(new InputEvent(InputKind.PointerDown, center, 0, 0));
        window.QueueInput(new InputEvent(InputKind.PointerUp, center, 0, 0));
        var f2 = host.RunFrame();

        bool childUpdated = HasGlyph(device, strings, "child 1");
        Check("24. nested component renders & owns state", mounted && f2.ClicksHandled == 1 && childUpdated, "child 0 → click → child 1");
    }

    // UseContext: provider value reaches a nested consumer, and a change propagates on re-render.
    static void ContextChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("ctx", new Size2(480, 320), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var root = new CtxParent();
        using var host = new AppHost(app, window, device, fonts, strings, root);

        host.RunFrame();
        bool c0 = HasGlyph(device, strings, "ctx 7");

        var incBtn = Child(host.Scene, host.Scene.Root, 0);   // VStack child 0 = "inc" button
        var rr = host.Scene.AbsoluteRect(incBtn);
        var center = new Point2(rr.X + rr.W / 2f, rr.Y + rr.H / 2f);
        window.QueueInput(new InputEvent(InputKind.PointerDown, center, 0, 0));
        window.QueueInput(new InputEvent(InputKind.PointerUp, center, 0, 0));
        host.RunFrame();
        bool c1 = HasGlyph(device, strings, "ctx 8");

        Check("25. UseContext provides + propagates across components", c0 && c1, "ctx 7 → ctx 8");
    }

    // Hover/pressed visual states: the dispatcher tracks them as node flags following the pointer.
    static void HoverChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("hover", new Size2(480, 320), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var root = new HoverProbe();
        using var host = new AppHost(app, window, device, fonts, strings, root);

        host.RunFrame();
        var btn = host.Scene.Root;
        var r = host.Scene.AbsoluteRect(btn);
        var center = new Point2(r.X + r.W / 2f, r.Y + r.H / 2f);
        var outside = new Point2(r.Right + 50f, r.Bottom + 50f);

        window.QueueInput(new InputEvent(InputKind.PointerMove, center, 0, 0));
        host.RunFrame();
        bool hov = (host.Scene.Flags(btn) & NodeFlags.Hovered) != 0;

        window.QueueInput(new InputEvent(InputKind.PointerDown, center, 0, 0));
        host.RunFrame();
        bool prs = (host.Scene.Flags(btn) & NodeFlags.Pressed) != 0;

        window.QueueInput(new InputEvent(InputKind.PointerUp, center, 0, 0));
        host.RunFrame();
        bool released = (host.Scene.Flags(btn) & NodeFlags.Pressed) == 0;

        window.QueueInput(new InputEvent(InputKind.PointerMove, outside, 0, 0));
        host.RunFrame();
        bool unhov = (host.Scene.Flags(btn) & NodeFlags.Hovered) == 0;

        Check("26. hover/pressed states track the pointer", hov && prs && released && unhov, "enter→hover, down→pressed, up→release, leave→unhover");
    }

    // Controls are barebone + a default Fluent style on top, overrideable per-instance (ButtonStyle) or via modifiers.
    static void StyleChecks()
    {
        var s = new Button.Style
        {
            Background = ColorF.FromRgba(10, 20, 30),
            Foreground = ColorF.FromRgba(40, 50, 60),
            HoverBackground = ColorF.FromRgba(70, 80, 90),
            CornerRadius = 8f,
        };
        var btn = Button.Accent("x", () => { }, s);
        bool styled = btn.Fill == s.Background
            && btn.HoverFill == s.HoverBackground
            && Near(btn.Corners.TopLeft, 8f)
            && btn.Children[0] is TextEl t && t.Color == s.Foreground;

        var modded = Button.Accent("y", () => { }).Background(ColorF.FromRgba(1, 2, 3)).Rounded(12f);
        bool overridden = modded.Fill == ColorF.FromRgba(1, 2, 3) && Near(modded.Corners.TopLeft, 12f);

        var animatedButton = Button.Standard("z", () => { });
        var animatedIcon = IconButton.Create("i", () => { });
        bool animation = animatedButton.PressScale < 1f
            && animatedIcon.Children[0] is BoxEl iconGlyph
            && iconGlyph.HoverScale > 1f
            && iconGlyph.PressScale < 1f;

        Check("27. controls are user-styleable + animated (ButtonStyle, modifiers, AnimatedIcon)", styled && overridden && animation,
            "custom style + .Background().Rounded() + press/icon scale");
    }

    // UseAnimatedValue eases toward a changed target across renders, then settles (React/framer-style transition).
    static void AnimValueChecks()
    {
        var p = new AnimProbe { Target = 0f };
        p.RenderWithHooks();                 // mount → value = 0
        p.Target = 1f;
        p.RenderWithHooks();                 // target changed → first eased step
        float v1 = p.Value;
        for (int i = 0; i < 20; i++) p.RenderWithHooks();   // advance past the 100ms duration
        float v2 = p.Value;
        Check("28. UseAnimatedValue eases then settles", v1 > 0f && v1 < 1f && Near(v2, 1f), $"step={v1:0.00} settled={v2:0.0}");
    }

    // flex-wrap: a 100-wide row of three 40-wide boxes flows to two lines ([0,1] then [2]).
    static void WrapChecks(StringTable strings)
    {
        var scene = LayoutTree(strings, new BoxEl
        {
            Direction = 0, Width = 100, Height = 100, Wrap = true,
            Children =
            [
                new BoxEl { Width = 40, Height = 20 },
                new BoxEl { Width = 40, Height = 20 },
                new BoxEl { Width = 40, Height = 20 },
            ],
        });
        var c0 = scene.AbsoluteRect(Child(scene, scene.Root, 0));
        var c1 = scene.AbsoluteRect(Child(scene, scene.Root, 1));
        var c2 = scene.AbsoluteRect(Child(scene, scene.Root, 2));
        Check("29. flex-wrap flows children to lines", Near(c0.X, 0) && Near(c1.X, 40) && Near(c2.X, 0) && Near(c2.Y, 20), $"c2=({c2.X:0.#},{c2.Y:0.#})");
    }

    // Regression for the gallery shell shape: a root overlay (ZStack) contains a fixed nav pane + grow content. The
    // content page has a wrapped caption followed by a grow virtual list. The overlay must pass the finite window width
    // into measure, and wrapping text must measure against the content frame width, not its full single-line width.
    static void ConstrainedWrapChecks(StringTable strings)
    {
        const string caption =
            "100,000 rows with real CDN thumbnails - only the visible window is realized and recycled over a slab free-list; " +
            "images decode off-thread, pack into the atlas, and evict off-screen, so memory stays flat. Wheel to scroll.";

        var tree = Ui.ZStack(new BoxEl
        {
            Direction = 0,
            Children =
            [
                new BoxEl { Width = 320f, Direction = 1 },
                new BoxEl
                {
                    Direction = 1,
                    Grow = 1f,
                    Gap = 16f,
                    Padding = Edges4.All(24f),
                    Children =
                    [
                        new TextEl("List virtualization") { Size = 28f, Bold = true },
                        new TextEl(caption) { Size = 14f, Wrap = TextWrap.Wrap },
                        Virtual.List(100000, 48f, _ => new BoxEl { Height = 48f }, keyOf: i => "r" + i) with { Grow = 1f },
                    ],
                },
            ],
        });

        var scene = new SceneStore();
        new TreeReconciler(scene, strings).ReconcileRoot(tree, null);
        new FlexLayout(scene, new HeadlessFontSystem(strings)).Run(scene.Root, new Size2(900f, 720f));

        NodeHandle captionNode = default, listNode = default;
        void Visit(NodeHandle n)
        {
            if (n.IsNull) return;
            ref var paint = ref scene.Paint(n);
            if (paint.VisualKind == VisualKind.Text && strings.Resolve(paint.Text) == caption) captionNode = n;
            if (scene.TryGetScroll(n, out var sc) && sc.ItemCount == 100000) listNode = n;
            for (var c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c)) Visit(c);
        }
        Visit(scene.Root);

        var cap = scene.AbsoluteRect(captionNode);
        var list = scene.AbsoluteRect(listNode);
        bool captionWrapped = cap.H > 30f;                 // > one 14px line in the headless font system
        bool listConstrained = Near(list.X, 344f) && Near(list.W, 532f) && list.Right <= 900f;

        Check("29a. wrapping text in fixed-pane + grow content is measured to the content frame",
            captionWrapped && listConstrained,
            $"caption={cap.W:0}x{cap.H:0} list=({list.X:0},{list.W:0},right={list.Right:0})");
    }

    // Compositor: the renderer applies a per-node world transform + cumulative opacity (CSS compositor model).
    static void CompositorChecks(StringTable strings)
    {
        var scene = new SceneStore();
        new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
        {
            Direction = 1, Width = 200, Height = 100, OffsetX = 20, OffsetY = 30, Opacity = 0.5f,
            Fill = ColorF.FromRgba(255, 0, 0),
            Children = [new BoxEl { Width = 40, Height = 20, Fill = ColorF.FromRgba(0, 255, 0), Opacity = 0.5f }],
        }, null);
        new FlexLayout(scene, new HeadlessFontSystem(strings)).Run(scene.Root);

        var dl = new DrawList();
        SceneRecorder.Record(scene, dl);
        var dev = new HeadlessGpuDevice();
        dev.SubmitDrawList(dl.Bytes, dl.SortKeys, new FrameInfo(new Size2(400, 300), 1f, ColorF.Transparent));

        var parent = dev.LastRects[0];   // root box
        var child = dev.LastRects[1];    // nested box
        bool parentOk = Near(parent.Transform.Dx, 20) && Near(parent.Transform.Dy, 30) && Near(parent.Opacity, 0.5f);
        bool childOk = Near(child.Opacity, 0.25f) && child.Transform.Dx >= 20f;   // opacity composes 0.5*0.5; inherits parent offset
        Check("30. compositor: transform + cumulative opacity", parentOk && childOk, $"pOffset=({parent.Transform.Dx:0.#},{parent.Transform.Dy:0.#}) childOpacity={child.Opacity:0.00}");
    }

    // Phase 2 — the generic runtime: keyframes, composite (add), springs, and scroll-driven timelines.
    static void AnimEngineChecks(StringTable strings)
    {
        // eased multi-keyframe tween → composed into LocalTransform
        var s1 = Single(strings);
        var a1 = new AnimEngine(s1);
        a1.Animate(s1.Root, AnimChannel.TranslateX, 0f, 100f, 100f, Easing.Linear);
        a1.Tick(50f);
        float mid = s1.Paint(s1.Root).LocalTransform.Dx;
        a1.Tick(100f);
        float end = s1.Paint(s1.Root).LocalTransform.Dx;
        Check("31. eased keyframe tween + hold", Near(mid, 50f, 1f) && Near(end, 100f, 0.5f), $"mid={mid:0.#} end={end:0.#}");

        // composite Add: two tracks on one channel combine (animation-composition: add)
        var s2 = Single(strings);
        var a2 = new AnimEngine(s2);
        a2.Animate(s2.Root, AnimChannel.TranslateX, 0f, 30f, 100f, Easing.Linear, CompositeOp.Replace);
        a2.Animate(s2.Root, AnimChannel.TranslateX, 0f, 20f, 100f, Easing.Linear, CompositeOp.Add);
        a2.Tick(100f);
        float add = s2.Paint(s2.Root).LocalTransform.Dx;
        Check("32. composite add combines tracks", Near(add, 50f, 0.5f), $"dx={add:0.#}");

        // spring settles to its target (semi-implicit ODE)
        var s3 = Single(strings);
        var a3 = new AnimEngine(s3);
        a3.Spring(s3.Root, AnimChannel.ScaleX, 1.3f, SpringParams.FromResponse(0.2f, 1f), initial: 1.0f);
        for (int i = 0; i < 150; i++) a3.Tick(16f);
        float sx = s3.Paint(s3.Root).LocalTransform.M11;
        Check("33. spring settles to target", Near(sx, 1.3f, 0.02f), $"scaleX={sx:0.###}");

        // scroll-driven timeline: a value source maps to progress (animation-timeline: scroll())
        var s4 = Single(strings);
        var a4 = new AnimEngine(s4);
        float scroll = 0f;
        int clk = a4.Clocks.Register(() => scroll);
        a4.Drive(s4.Root, AnimChannel.Opacity, [new(0f, 0f, Easing.Linear), new(1f, 1f, Easing.Linear)], clk, 0f, 100f);
        scroll = 25f; a4.Tick(16f);
        float op25 = s4.Paint(s4.Root).Opacity;
        scroll = 100f; a4.Tick(16f);
        float op100 = s4.Paint(s4.Root).Opacity;
        Check("34. scroll-driven timeline", Near(op25, 0.25f, 0.02f) && Near(op100, 1f, 0.01f), $"op@25={op25:0.00} op@100={op100:0.00}");
    }

    // Phase 3 — declarative hooks: a component animates its own node via UseSpring (composited, no re-render per frame).
    static void AnimHookChecks(StringTable strings)
    {
        var scene = new SceneStore();
        var anim = new AnimEngine(scene);
        var recon = new TreeReconciler(scene, strings) { Anim = anim };
        recon.ReconcileRoot(Embed.Comp(() => new SpringProbe()), null);

        foreach (var c in recon.LiveComponents)   // phase 6.5: drain layout effects → seeds the spring on the host node
        {
            foreach (var e in c.Context.PendingLayoutEffects) e();
            c.Context.PendingLayoutEffects.Clear();
        }
        for (int i = 0; i < 150; i++) anim.Tick(16f);

        var host = scene.FirstChild(scene.Root);   // the SpringProbe's box node
        float sx = scene.Paint(host).LocalTransform.M11;
        Check("35. UseSpring hook seeds + drives the node", !host.IsNull && Near(sx, 1.2f, 0.03f), $"scaleX={sx:0.###}");
    }

    // ScrollView: layout publishes ContentSize, the viewport clips overflow, and a wheel scrolls via a transform
    // (layout-free) clamped to the content — no relayout, offscreen rows culled.
    static void ScrollChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("scroll", new Size2(480, 320), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new ScrollProbe());

        host.RunFrame();   // mount + layout → ContentSize published
        var vp = host.Scene.Root;
        host.Scene.TryGetScroll(vp, out var sc0);
        bool sized = Near(sc0.ContentH, 800) && Near(sc0.ViewportH, 200);

        // a 200px viewport over 40px rows shows ~5 of 20 — the rest are clip-culled; clip is balanced.
        int drawnAtTop = device.LastRects.Count;
        bool clipped = device.LastClips.Count >= 1 && device.ClipBalance == 0 && drawnAtTop is >= 5 and < 20;

        // wheel down 100 → offset 100, content transform −100, NO re-render (transform-only frame)
        var center = new Point2(100, 100);
        window.QueueInput(new InputEvent(InputKind.Wheel, center, 0, 0, 100f));
        var f = host.RunFrame();
        host.Scene.TryGetScroll(vp, out var sc1);
        bool scrolled = Near(sc1.OffsetY, 100)
            && Near(host.Scene.Paint(sc1.ContentNode).LocalTransform.Dy, -100)
            && !f.Rendered;

        // fling past the end → clamp to ContentH − ViewportH = 600
        window.QueueInput(new InputEvent(InputKind.Wheel, center, 0, 0, 10000f));
        host.RunFrame();
        host.Scene.TryGetScroll(vp, out var sc2);
        bool clamped = Near(sc2.OffsetY, 600);

        Check("36. ScrollView publishes ContentSize + clips overflow", sized && clipped, $"content={sc0.ContentH:0} drawn={drawnAtTop} clips={device.LastClips.Count}");
        Check("37. wheel scrolls via transform (layout-free) + clamps", scrolled && clamped, $"off→{sc1.OffsetY:0}, clamp={sc2.OffsetY:0}");
    }

    // Virtualization: a 10k-row list realizes only the viewport window; scrolling recycles via the slab free-list
    // (bounded live nodes, no leak); a sub-extent scroll is a transform-only frame (no realize / no relayout).
    // Overlay scrollbar visual states: hover over the lane shows the full WinUI gutter; leaving collapses to a thin
    // indicator; after the idle timeout it auto-hides to no scrollbar draw.
    static void ScrollOverlayChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("scrollbar", new Size2(480, 320), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new ScrollProbe());

        static RectF DeviceRect(FillRoundRectCmd cmd) => cmd.Transform.TransformBounds(cmd.Rect);
        static bool LaneRect(FillRoundRectCmd cmd, out RectF r)
        {
            r = DeviceRect(cmd);
            return r.X >= 186f && r.X <= 200.5f && r.W <= 13.5f && r.H > 20f;
        }

        host.RunFrame();

        window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(198f, 100f), 0, 0));
        for (int i = 0; i < 18; i++) host.RunFrame();

        bool expandedGutter = false, expandedThumb = false;
        foreach (var rect in device.LastRects)
        {
            if (!LaneRect(rect, out var r)) continue;
            expandedGutter |= r.W >= 10f && r.H >= 190f;
            expandedThumb |= r.W >= 10f && r.H >= 30f && r.H < 190f;
        }

        window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(260f, 260f), 0, 0));
        for (int i = 0; i < 30; i++) host.RunFrame();

        bool collapsedGutter = false, collapsedThumb = false;
        foreach (var rect in device.LastRects)
        {
            if (!LaneRect(rect, out var r)) continue;
            collapsedGutter |= r.W >= 10f && r.H >= 190f;
            collapsedThumb |= r.W <= 7f && r.H >= 30f;   // resting thumb is a visible 6px (was a 2px hairline)
        }

        for (int i = 0; i < 90; i++) host.RunFrame();

        bool anyScrollbar = false;
        foreach (var rect in device.LastRects)
        {
            anyScrollbar |= LaneRect(rect, out _);
        }

        Check("38a. overlay scrollbar expands, collapses to a visible thumb, then auto-hides",
            expandedGutter && expandedThumb && !collapsedGutter && collapsedThumb && !anyScrollbar,
            $"expanded=({expandedGutter},{expandedThumb}) collapsed=({collapsedGutter},{collapsedThumb}) hidden={!anyScrollbar}");
    }

    static void VirtualChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("virt", new Size2(640, 480), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new VirtualProbe());

        host.RunFrame();
        var vp = host.Scene.Root;
        host.Scene.TryGetScroll(vp, out var sc0);
        var content = sc0.ContentNode;
        int realized = host.Scene.ChildCount(content);
        bool windowed = realized >= 10 && realized < 40;
        bool contentSize = Near(sc0.ContentH, VirtualProbe.N * 40f);
        Check("38. virtualizes 10k rows to a small window", windowed && contentSize, $"realized={realized}/{VirtualProbe.N} content={sc0.ContentH:0}");

        // in-window (sub-extent) scroll = transform-only frame: no re-render, window unchanged, content shifted.
        int firstA = sc0.FirstRealized;
        var ptr = new Point2(150, 200);
        window.QueueInput(new InputEvent(InputKind.Wheel, ptr, 0, 0, 10f));
        var f1 = host.RunFrame();
        host.Scene.TryGetScroll(vp, out var sc1);
        bool transformOnly = !f1.Rendered && sc1.FirstRealized == firstA
            && Near(host.Scene.Paint(content).LocalTransform.Dy, -10f);
        Check("39. in-window scroll is transform-only (no realize/relayout)", transformOnly, $"rendered={f1.Rendered} first={sc1.FirstRealized}");

        // boundary-crossing fling to the end: window re-realizes directly; recycle keeps live nodes bounded (no leak).
        long live0 = host.Scene.LiveCount;
        for (int s = 0; s < 60; s++) { window.QueueInput(new InputEvent(InputKind.Wheel, ptr, 0, 0, 7000f)); host.RunFrame(); }
        host.Scene.TryGetScroll(vp, out var sc2);
        long liveEnd = host.Scene.LiveCount;
        bool reachedEnd = sc2.FirstRealized > 9000;
        bool bounded = liveEnd < 90 && Math.Abs(liveEnd - live0) < 40;
        Check("40. fling recycles via free-list (bounded live nodes, no leak)", reachedEnd && bounded, $"first={sc2.FirstRealized} live {live0}→{liveEnd}");
    }

    // The Fenwick extent table: O(log n) prefix-sum (OffsetOf) + binary-lift (IndexAt) + O(log n) correction.
    static void ExtentTableChecks()
    {
        var t = new ExtentTable(5, 10f);   // 5 items × 10 = 50
        bool init = Near((float)t.Total, 50) && Near(t.OffsetOf(0), 0) && Near(t.OffsetOf(2), 20) && Near(t.OffsetOf(5), 50);
        bool indexAt = t.IndexAt(0) == 0 && t.IndexAt(15) == 1 && t.IndexAt(25) == 2 && t.IndexAt(49) == 4;
        t.SetExtent(2, 30f);   // correct item 2: 10 → 30 (total 50 → 70)
        bool corrected = Near((float)t.Total, 70) && Near(t.OffsetOf(3), 50) && t.IndexAt(45) == 2 && t.IndexAt(55) == 3;
        Check("41. extent table: O(log n) offset↔index + correction", init && indexAt && corrected, $"total={t.Total:0}");
    }

    // Variable-height virtualization: rows are positioned by their measured extents (the corrected Fenwick table),
    // and a scroll anchors on the visible item (the offset stays inside the anchor item's band; clamped to content).
    static void VariableChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("var", new Size2(640, 480), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new VarProbe());

        host.RunFrame();
        var vp = host.Scene.Root;
        host.Scene.TryGetScroll(vp, out var sc);
        var content = sc.ContentNode;

        // rows positioned by cumulative measured heights (OffsetOf), and the extent table corrected for the window
        var r0 = Child(host.Scene, content, 0);
        var r1 = Child(host.Scene, content, 1);
        var r2 = Child(host.Scene, content, 2);
        var r3 = Child(host.Scene, content, 3);
        bool positions = Near(host.Scene.Bounds(r0).Y, 0)
            && Near(host.Scene.Bounds(r1).Y, VarProbe.H(0))
            && Near(host.Scene.Bounds(r2).Y, VarProbe.H(0) + VarProbe.H(1))
            && Near(host.Scene.Bounds(r3).Y, VarProbe.H(0) + VarProbe.H(1) + VarProbe.H(2));
        host.Scene.TryGetExtents(vp, out var table);
        bool measured = table is not null && Near(table.ExtentAt(0), VarProbe.H(0)) && Near(table.ExtentAt(2), VarProbe.H(2));
        Check("42. variable rows positioned by measured extents", positions && measured, $"y0..3={host.Scene.Bounds(r0).Y:0},{host.Scene.Bounds(r1).Y:0},{host.Scene.Bounds(r2).Y:0},{host.Scene.Bounds(r3).Y:0}");

        // scroll into the middle → anchor tracks the visible item; offset stays in its band and clamped to content
        for (int s = 0; s < 6; s++) { window.QueueInput(new InputEvent(InputKind.Wheel, new Point2(150, 150), 0, 0, 350f)); host.RunFrame(); }
        host.Scene.TryGetScroll(vp, out var sc2);
        host.Scene.TryGetExtents(vp, out var t2);
        int anchor = t2!.IndexAt(sc2.OffsetY);
        float band0 = t2.OffsetOf(anchor), band1 = t2.OffsetOf(anchor + 1);
        bool anchored = sc2.AnchorIndex == anchor
            && sc2.OffsetY >= band0 - 0.5f && sc2.OffsetY < band1 + 0.5f
            && sc2.OffsetY <= sc2.ContentH - sc2.ViewportH + 1f && sc2.FirstRealized > 0;
        Check("43. variable scroll anchors on the visible item (in-band, clamped)", anchored, $"anchor={anchor} off={sc2.OffsetY:0} content={sc2.ContentH:0} first={sc2.FirstRealized}");
    }

    // The central virtualization claim: an in-window (sub-extent) scroll is a transform-only frame with ZERO managed
    // allocation on the paint half (no realize, no reconcile, no relayout — just re-record the shifted window).
    static void ZeroAllocScrollChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("zalloc", new Size2(640, 480), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new VirtualProbe());

        host.RunFrame();   // mount
        var ptr = new Point2(150, 200);
        // warm: several sub-extent (5px) scrolls — all stay within item 0 (5×5 = 25 < 40px row) → transform-only
        for (int i = 0; i < 5; i++) { window.QueueInput(new InputEvent(InputKind.Wheel, ptr, 0, 0, 2f)); host.RunFrame(); }
        window.QueueInput(new InputEvent(InputKind.Wheel, ptr, 0, 0, 2f));
        var fz = host.RunFrame();
        bool zero = !fz.Rendered && fz.HotPhaseAllocBytes == 0;
        Check("44. in-window scroll: 0 managed alloc on the paint half", zero, $"{fz.HotPhaseAllocBytes} bytes, rendered={fz.Rendered}");
    }

    // ImageCache: state machine (Pending→Ready), source dedup, and liveness-pinned LRU eviction (pinned = on screen,
    // never evicted — the single biggest real-world image-cache bug per the research).
    static void ImageCacheChecks()
    {
        var cache = new ImageCache(new FakeImageDecoder(), budgetBytes: 1000);   // tiny budget to force eviction
        var a = cache.Request("a", 10, 10);                                      // 10×10×4 = 400 bytes when ready
        bool pending = cache.StateOf(a) == ImageState.Pending;
        cache.Pump();
        bool ready = cache.StateOf(a) == ImageState.Ready && cache.SizeOf(a) == (10, 10);
        bool dedup = cache.Request("a", 10, 10).Id == a.Id;

        cache.Pin(a);                                                            // a is "on screen"
        var b = cache.Request("b", 10, 10);
        var c = cache.Request("c", 10, 10);
        cache.Pump();                                                           // a+b+c = 1200 > 1000 → evict LRU unpinned (b)
        bool keptPinned = cache.StateOf(a) == ImageState.Ready;                  // pinned survived eviction
        bool withinBudget = cache.UsedBytes <= 1000;
        Check("45. ImageCache: states, dedup, liveness-pinned LRU evict", pending && ready && dedup && keptPinned && withinBudget,
            $"used={cache.UsedBytes} ready={cache.ReadyCount} aRefs={cache.RefsOf(a)}");
    }

    // ImageEl end-to-end: the reconciler requests the decode + pins residency, the cache completes it (Pump), and the
    // recorder emits a DrawImage carrying the handle + ready flag + placeholder.
    static void ImageElChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("img", new Size2(480, 320), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new ImageProbe());

        host.RunFrame();
        bool drawn = device.LastImages.Count == 1;
        var cmd = drawn ? device.LastImages[0] : default;
        var h = new ImageHandle(cmd.ImageId);
        bool ready = drawn && cmd.Ready == 1 && cmd.ImageId != 0 && host.Images.StateOf(h) == ImageState.Ready;
        bool pinned = host.Images.RefsOf(h) == 1;
        bool placeholder = Near(cmd.Placeholder.R, 0x33 / 255f) && Near(cmd.Radii.TopLeft, 6f);
        Check("46. ImageEl: decode→ready, residency-pinned, DrawImage emitted", drawn && ready && pinned && placeholder,
            $"images={device.LastImages.Count} ready={cmd.Ready} refs={host.Images.RefsOf(h)}");

        // The decode's pixels must reach the GPU backend via the UploadImage seam (media-pipeline §4.1) at the decoded
        // bucket size — proves the decoder→cache.Pump→host sink→device texture-upload chain end to end.
        bool uploaded = device.Uploads.Count == 1
            && device.Uploads[0].id == cmd.ImageId && device.Uploads[0].w == 80 && device.Uploads[0].h == 80
            && device.ResidentImages.ContainsKey(cmd.ImageId);
        int uw = device.Uploads.Count > 0 ? device.Uploads[0].w : 0;
        int uh = device.Uploads.Count > 0 ? device.Uploads[0].h : 0;
        Check("46b. ImageEl: decoded pixels uploaded to the GPU backend at bucket size", uploaded,
            $"uploads={device.Uploads.Count} dims={uw}x{uh}");
    }

    // Deterministic test codec/fetcher exercise the REAL DecodeScheduler (worker pool, channels, retry) with no network.
    sealed class TestCodec : IImageCodec
    {
        readonly Action? _onDecode;
        public TestCodec(Action? onDecode = null) => _onDecode = onDecode;
        public bool DecodeConstrained(ReadOnlySpan<byte> encoded, int tw, int th, Span<byte> dst, out int w, out int h)
        {
            _onDecode?.Invoke();
            w = tw; h = th;
            dst.Slice(0, tw * th * 4).Fill(0xFF);
            return true;
        }
    }

    sealed class TestFetcher : IImageFetcher
    {
        readonly Func<string, FetchResult>? _map;
        public TestFetcher(Func<string, FetchResult>? map = null) => _map = map;
        public Task<FetchResult> FetchAsync(string source, System.Threading.CancellationToken ct)
        {
            if (_map != null) return Task.FromResult(_map(source));
            return Task.FromResult(FetchResult.Pooled(ArrayPool<byte>.Shared.Rent(16), 16));
        }
    }

    static (bool ok, ImageFailureKind fail, int att) DrainOne(DecodeScheduler sched, int id)
    {
        sched.Begin(id, "x", 8, 8);
        (bool ok, ImageFailureKind fail, int att) res = (false, ImageFailureKind.None, 0);
        bool got = false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!got && sw.ElapsedMilliseconds < 5000)
        {
            sched.Pump((cid, ok, w, h, f, a) => { res = (ok, f, a); got = true; }, (cid, px, w, h) => { });
            System.Threading.Thread.Sleep(3);
        }
        return res;
    }

    // The A+B asks (off-thread + parallel + non-blocking) and the robustness taxonomy, on the real scheduler.
    static void DecodeSchedulerChecks()
    {
        int cur = 0, maxc = 0; object g = new();
        var codec = new TestCodec(() =>
        {
            int c = System.Threading.Interlocked.Increment(ref cur);
            lock (g) { if (c > maxc) maxc = c; }
            System.Threading.Thread.Sleep(60);                       // hold the worker so decodes overlap
            System.Threading.Interlocked.Decrement(ref cur);
        });
        int done = 0;
        using (var sched = new DecodeScheduler(codec, new TestFetcher(), new DecodeOptions { MaxConcurrency = 4 }))
        {
            const int M = 8;
            for (int i = 1; i <= M; i++) sched.Begin(i, "t" + i, 8, 8);   // non-blocking enqueues
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (done < M && sw.ElapsedMilliseconds < 5000)
            {
                sched.Pump((id, ok, w, h, f, a) => { if (ok) done++; }, (id, px, w, h) => { });
                System.Threading.Thread.Sleep(3);                    // UI stays responsive while workers decode
            }
            Check("46c. DecodeScheduler: off-thread, parallel (N-way), non-blocking decode",
                done == 8 && maxc >= 2, $"done={done}/8 maxConcurrent={maxc} workers={sched.WorkerCount}");
        }

        (bool ok, ImageFailureKind fail, int att) r1;
        using (var sched = new DecodeScheduler(new TestCodec(), new TestFetcher(_ => FetchResult.Fail(ImageFailureKind.NotFound)),
                   new DecodeOptions { MaxAttempts = 3, BackoffBase = TimeSpan.FromMilliseconds(1) }))
            r1 = DrainOne(sched, 1);

        int calls = 0;
        var flaky = new TestFetcher(_ =>
        {
            int c = System.Threading.Interlocked.Increment(ref calls);
            return c < 3 ? FetchResult.Fail(ImageFailureKind.ServerError) : FetchResult.Pooled(ArrayPool<byte>.Shared.Rent(16), 16);
        });
        (bool ok, ImageFailureKind fail, int att) r2;
        using (var sched = new DecodeScheduler(new TestCodec(), flaky, new DecodeOptions { MaxAttempts = 3, BackoffBase = TimeSpan.FromMilliseconds(1) }))
            r2 = DrainOne(sched, 1);

        bool permanent = !r1.ok && r1.fail == ImageFailureKind.NotFound && r1.att == 1;   // 404 → fail fast, no retry
        bool transient = r2.ok && r2.att == 3;                                            // 5xx ×2 then 200 → success on attempt 3
        Check("46d. DecodeScheduler: 404 fails fast (no retry); transient 5xx retried to success",
            permanent && transient, $"404=(ok={r1.ok} {r1.fail} att={r1.att}) flaky=(ok={r2.ok} att={r2.att})");
    }

    static void BlurHashChecks(StringTable strings)
    {
        // (a) the decoder produces a valid, non-uniform preview from the canonical hash.
        Span<byte> px = stackalloc byte[8 * 8 * 4];
        bool decoded = BlurHash.Decode("LEHV6nWB2yk8pyo0adR*.7kCMdnj", 8, 8, px);
        bool varies = decoded && (px[0] != px[63 * 4] || px[1] != px[63 * 4 + 1] || px[2] != px[63 * 4 + 2]);

        // (b) pipeline: the 32×32 LQIP is uploaded at request (before the 64×64 full-res decode in the same frame).
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("blur", new Size2(320, 320), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new BlurHashProbe());
        host.RunFrame();
        bool lqipFirst = device.Uploads.Count >= 2
            && device.Uploads[0].w == 32 && device.Uploads[0].h == 32   // blurhash preview, uploaded first
            && device.Uploads[1].w == 64 && device.Uploads[1].h == 64;  // full-res, replaces it

        Check("46e. BlurHash: decoder valid + LQIP uploaded instantly, replaced by full-res", varies && lqipFirst,
            $"decoded={decoded} varies={varies} uploads={device.Uploads.Count}");
    }

    static void ImageTransitionChecks()
    {
        var cache = new ImageCache(new FakeImageDecoder());
        var h = cache.Request("x", 16, 16);   // default reveal (220ms FluentDecelerate)
        cache.Pump();                          // decode completes → texture appears at t=0
        float cf0 = cache.CrossFadeOf(h);      // just appeared → ~0
        for (int i = 0; i < 20; i++) cache.Tick(16f);   // 320ms elapsed > 220ms
        float cf1 = cache.CrossFadeOf(h);      // settled → 1
        bool fades = cf0 < 0.2f && cf1 >= 0.999f;

        var hn = cache.Request("y", 16, 16, ImagePriority.Visible, null, ImageTransition.None);   // disabled
        cache.Pump();
        bool disabled = cache.CrossFadeOf(hn) >= 0.999f;   // instant, no fade

        Check("46f. ImageTransition: default fade eases 0→1; None disables (instant)", fades && disabled,
            $"cf0={cf0:0.00} cf1={cf1:0.00}");
    }

    static void ImageEvictChecks()
    {
        // Unpinned images over budget → LRU eviction, each freeing its GPU texture via the evict sink.
        var evicted = new List<int>();
        var cache = new ImageCache(new FakeImageDecoder(), budgetBytes: 50_000);
        cache.SetEvictSink(evicted.Add);
        for (int i = 0; i < 5; i++) cache.Request("img" + i, 64, 64);   // 5 × 16KB = 80KB > 50KB
        cache.Pump();                                                    // decode → ready → evict unpinned LRU
        bool freed = evicted.Count >= 1;

        // Pinned (on-screen) images are NEVER evicted, regardless of budget.
        var evicted2 = new List<int>();
        var pinned = new ImageCache(new FakeImageDecoder(), budgetBytes: 50_000);
        pinned.SetEvictSink(evicted2.Add);
        for (int i = 0; i < 5; i++) pinned.Pin(pinned.Request("p" + i, 64, 64));
        pinned.Pump();
        bool pinnedSafe = evicted2.Count == 0;

        Check("46g. Residency: evicts unpinned LRU + frees its GPU texture; never evicts pinned", freed && pinnedSafe,
            $"evicted={evicted.Count} pinnedEvicted={evicted2.Count}");
    }

    static void UseImageChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("useimg", new Size2(200, 200), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new UseImageProbe());
        host.RunFrame();   // render → UseImage requests; pump completes the fake decode; status-change marks dirty
        host.RunFrame();   // re-render: UseImage now reports Ready → the component swaps the spinner for the image
        Check("46h. UseImage: hook surfaces load state to the component (spinner → ready)",
            UseImageProbe.LastState == ImageState.Ready, $"state={UseImageProbe.LastState}");
    }

    // Controls: a slider press-sets and drag-scrubs its value; a toggle flips; an icon button clicks; a scrollbar drags.
    static void ControlsChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("ctl", new Size2(480, 480), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var probe = new ControlsProbe();
        using var host = new AppHost(app, window, device, fonts, strings, probe);
        host.RunFrame();

        NodeHandle Kid(int i) => Child(host.Scene, host.Scene.Root, i);
        void Press(NodeHandle n, float lx, float ly)
        {
            var r = host.Scene.AbsoluteRect(n);
            window.QueueInput(new InputEvent(InputKind.PointerDown, new Point2(r.X + lx, r.Y + ly), 0, 0));
        }

        // slider: press at x=100/200 → 0.5
        Press(Kid(0), 100f, 12f);
        host.RunFrame();
        bool press = Near(probe.SliderVal, 0.5f);
        // drag to x=160 → 0.8 (the drag target survives the in-place re-render)
        var sr = host.Scene.AbsoluteRect(Kid(0));
        window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(sr.X + 160f, sr.Y + 12f), 0, 0));
        host.RunFrame();
        bool drag = Near(probe.SliderVal, 0.8f);
        window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(sr.X + 160f, sr.Y + 12f), 0, 0));
        host.RunFrame();
        Check("47. Slider press-sets + drag-scrubs value", press && drag, $"press={probe.SliderVal:0.0} (0.5→0.8)");

        // toggle: click flips on
        var tr = host.Scene.AbsoluteRect(Kid(1));
        window.QueueInput(new InputEvent(InputKind.PointerDown, new Point2(tr.X + tr.W / 2, tr.Y + tr.H / 2), 0, 0));
        window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(tr.X + tr.W / 2, tr.Y + tr.H / 2), 0, 0));
        host.RunFrame();
        bool toggled = probe.Toggled;

        // icon button: click increments
        var ir = host.Scene.AbsoluteRect(Kid(2));
        window.QueueInput(new InputEvent(InputKind.PointerDown, new Point2(ir.X + ir.W / 2, ir.Y + ir.H / 2), 0, 0));
        window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(ir.X + ir.W / 2, ir.Y + ir.H / 2), 0, 0));
        host.RunFrame();
        bool iconClicked = probe.IconClicks == 1;

        // scrollbar: drag the thumb to ~bottom → position near 1
        Press(Kid(3), 5f, 190f);
        host.RunFrame();
        bool scrolled = probe.ScrollPos > 0.5f;

        Check("48. Toggle flips, IconButton clicks, ScrollBar drags", toggled && iconClicked && scrolled,
            $"toggled={probe.Toggled} icon={probe.IconClicks} scrollPos={probe.ScrollPos:0.0}");
    }

    // Navigation as serializable state: push/pop/depth + round-trip serialize for deep-link / cold-launch restore.
    static void NavigationChecks()
    {
        var nav = new Navigator(new Route("home"));
        bool d1 = nav.Current.Name == "home" && !nav.CanGoBack && nav.Depth == 1;
        nav.Push("playlist", "p1");
        bool d2 = nav.Current is { Name: "playlist", Arg: "p1" } && nav.CanGoBack && nav.Depth == 2;
        string ser = nav.Serialize();
        nav.Pop();
        bool d3 = nav.Current.Name == "home" && !nav.CanGoBack;
        var restored = Navigator.Deserialize(ser);
        bool d4 = restored.Depth == 2 && restored.Current is { Name: "playlist", Arg: "p1" };
        Check("49. Navigator: push/pop/depth + serialize round-trip", d1 && d2 && d3 && d4, ser);
    }

    // PageHost renders the top route and re-renders the view tree on push/pop (the back stack drives the UI).
    static void PageHostChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("nav", new Size2(480, 320), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var nav = new Navigator(new Route("home"));
        Element View(Route r) => r.Name == "home"
            ? new BoxEl { Children = [new TextEl("HOME PAGE")] }
            : new BoxEl { Children = [new TextEl("PLAYLIST " + r.Arg)] };
        using var host = new AppHost(app, window, device, fonts, strings, new PageHost(nav, View));

        host.RunFrame();
        bool onHome = HasGlyph(device, strings, "HOME PAGE");
        nav.Push("playlist", "x1");
        host.RunFrame();
        bool onDetail = HasGlyph(device, strings, "PLAYLIST x1");
        nav.Pop();
        host.RunFrame();
        bool backHome = HasGlyph(device, strings, "HOME PAGE");
        Check("50. PageHost renders + navigates the back stack", onHome && onDetail && backHome, "home → playlist → back");
    }

    // Grid: 3 equal Star tracks across 320 (gaps 10) → 100px columns; 5 cells flow row-major into 2 rows.
    static void GridChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("grid", new Size2(640, 480), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new GridProbe());
        host.RunFrame();

        var grid = host.Scene.Root;
        var b0 = host.Scene.Bounds(Child(host.Scene, grid, 0));
        var b1 = host.Scene.Bounds(Child(host.Scene, grid, 1));
        var b2 = host.Scene.Bounds(Child(host.Scene, grid, 2));
        var b3 = host.Scene.Bounds(Child(host.Scene, grid, 3));
        var b4 = host.Scene.Bounds(Child(host.Scene, grid, 4));
        bool cols = Near(b0.X, 0) && Near(b1.X, 110) && Near(b2.X, 220) && Near(b0.W, 100);
        bool rows = Near(b3.X, 0) && Near(b3.Y, 60) && Near(b4.X, 110) && Near(b4.Y, 60);
        Check("51. Grid: star tracks split width + row-major auto-flow", cols && rows,
            $"cols x={b0.X:0},{b1.X:0},{b2.X:0} w={b0.W:0}; row2 y={b3.Y:0}");
    }

    // Regression: a stretch-width grid (no explicit Width) must measure its real content height so a following
    // sibling stacks below it instead of overlapping. 8 cells / 4 cols = 2 rows × 90 + one 12 row-gap = 192.
    static void GridStretchChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("gridstretch", new Size2(640, 480), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new GridStretchProbe());
        host.RunFrame();

        var root = host.Scene.Root;
        var grid = Child(host.Scene, root, 0);
        var after = Child(host.Scene, root, 1);
        var gb = host.Scene.Bounds(grid);
        var ab = host.Scene.Bounds(after);
        bool gridHeight = Near(gb.H, 192f, 1f);          // grid measured its 2 real rows (not 0)
        bool gridWidth = Near(gb.W, 420f, 1f);           // stretched to fill the 420-wide column
        bool noOverlap = ab.Y >= 192f - 0.5f;            // sibling below the grid's real content, not on top of it
        Check("51b. Grid: stretch-width grid measures content height (sibling doesn't overlap)",
            gridHeight && gridWidth && noOverlap, $"gridW={gb.W:0} gridH={gb.H:0} afterY={ab.Y:0}");
    }

    // Auto-fill responsive grid: 520 inner width, minCol 120, gap 10 → floor((520+10)/(120+10)) = 4 columns, each
    // (520 − 3×10)/4 = 122.5, packed flush so the 4th column's right edge meets the inner width (no ragged gap).
    static void AutoGridChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("autogrid", new Size2(640, 480), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new AutoGridProbe());
        host.RunFrame();

        var grid = host.Scene.Root;
        var b0 = host.Scene.Bounds(Child(host.Scene, grid, 0));
        var b3 = host.Scene.Bounds(Child(host.Scene, grid, 3));
        var b4 = host.Scene.Bounds(Child(host.Scene, grid, 4));
        bool count = Near(b0.W, 122.5f, 0.5f) && Near(b0.X, 0) && Near(b3.X, 397.5f, 0.5f);   // 4 columns
        bool fillsWidth = Near(b3.X + b3.W, 520f, 0.5f);                                       // right edge flush — fills the width
        bool wraps = Near(b4.X, 0) && Near(b4.Y, 60f, 0.5f);                                   // 5th cell wraps to row 2 (50 + 10 gap)
        Check("51c. AutoGrid: auto-fill packs equal columns that fill the width + reflow",
            count && fillsWidth && wraps, $"col0w={b0.W:0.0} col3right={b3.X + b3.W:0.0} row2y={b4.Y:0}");
    }

    // The Wavee skeleton acceptance test: the shell composes nav + grid + images + controls; navigating to the
    // playlist renders a VIRTUALIZED 5,000-row list (first track realized, last track NOT) — all subsystems at once.
    static void WaveeSkeletonChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("wavee", new Size2(1100, 720), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var shell = new WaveeShell();
        using var host = new AppHost(app, window, device, fonts, strings, shell);

        host.RunFrame();
        bool home = HasGlyph(device, strings, "Album 0") && HasGlyph(device, strings, "Home");
        bool playerBar = HasGlyph(device, strings, "Now Playing") && HasGlyph(device, strings, "Shuffle");
        bool artRequested = host.Images.Count >= 12;   // 12 album cards + now-playing art all requested + pinned

        shell.Nav.Push("playlist", "p0");
        host.RunFrame();
        long liveOnPlaylist = host.Scene.LiveCount;
        bool virtualized = HasGlyph(device, strings, "Track 0")
            && !HasGlyph(device, strings, "Track 4999")     // last row never realized → virtualization holds in the shell
            && liveOnPlaylist < 600;                         // 5,000 rows × multiple nodes would be ≫ this if not virtualized

        bool back = false;
        if (shell.Nav.Pop()) { host.RunFrame(); back = HasGlyph(device, strings, "Album 0"); }   // back-stack returns Home

        Check("52. Wavee skeleton: shell composes nav + grid + images + controls + virtualized list", home && playerBar && artRequested && virtualized && back,
            $"home={home} player={playerBar} art={host.Images.Count} liveOnList={liveOnPlaylist} back={back}");
    }

    // VirtualGrid (2-D): a 1,000-item, 4-column grid realizes only the visible row-window and positions items in cells.
    static void VirtualGridChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("vgrid", new Size2(800, 600), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new VGridProbe());
        host.RunFrame();

        var vp = host.Scene.Root;
        host.Scene.TryGetScroll(vp, out var sc);
        var content = sc.ContentNode;
        int realized = host.Scene.ChildCount(content);
        bool windowed = realized >= 16 && realized < 80;                 // ~visible rows × 4 cols, not 1000
        // ContentExtent = 250 rows × 100 + 249 gaps × 12 = 27,988
        bool contentSize = Near(sc.ContentH, 27988f, 2f);
        // colW = (520 − 3×12)/4 = 121; item 0 at (0,0), item 1 at (133,0), item 4 at (0,112)
        var b0 = host.Scene.Bounds(Child(host.Scene, content, 0));
        var b1 = host.Scene.Bounds(Child(host.Scene, content, 1));
        var b4 = host.Scene.Bounds(Child(host.Scene, content, 4));
        bool cells = Near(b0.X, 0) && Near(b0.W, 121, 1f) && Near(b1.X, 133, 1f) && Near(b4.X, 0) && Near(b4.Y, 112, 1f);

        long live0 = host.Scene.LiveCount;
        for (int s = 0; s < 40; s++) { window.QueueInput(new InputEvent(InputKind.Wheel, new Point2(200, 200), 0, 0, 4000f)); host.RunFrame(); }
        long liveEnd = host.Scene.LiveCount;
        bool recycled = liveEnd < 120 && Math.Abs(liveEnd - live0) < 40;

        Check("53. VirtualGrid: 2-D row-window + cell positions + recycle", windowed && contentSize && cells && recycled,
            $"realized={realized} content={sc.ContentH:0} cell0w={b0.W:0} live {live0}→{liveEnd}");
    }

    // NavigationView adapts its pane to available width: Expanded (labels) → Compact (icon rail) → Minimal (hamburger).
    static (bool label, bool content, float rootW) NavAt(StringTable strings, int width, float scale = 1f)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("nav", new Size2(width, 700), scale));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new NavProbe());
        host.RunFrame();
        return (HasGlyph(device, strings, "Home"), HasGlyph(device, strings, "PAGE:home"), host.Scene.AbsoluteRect(host.Scene.Root).W);
    }

    static void NavigationViewChecks(StringTable strings)
    {
        var exp = NavAt(strings, 1200);   // ≥1008 → Expanded (labels visible)
        var comp = NavAt(strings, 760);   // 641..1008 → Compact (icon rail, no labels)
        var min = NavAt(strings, 520);    // <641 → Minimal (hamburger, no rail labels)
        var dpiComp = NavAt(strings, 1200, 1.5f);
        bool modes = exp.label && !comp.label && !min.label;
        bool content = exp.content && comp.content && min.content;
        Check("54. NavigationView adapts Expanded/Compact/Minimal by width", modes && content,
            $"labels exp={exp.label} comp={comp.label} min={min.label}; content={content}");
        Check("54a. AppHost lays out scaled windows in DIPs", !dpiComp.label && Near(dpiComp.rootW, 800f),
            $"rootW={dpiComp.rootW:0.#} label={dpiComp.label}");
    }

    static void NavigationViewAnimationChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("navanim", new Size2(1200, 700), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new NavProbe());
        host.RunFrame();

        NodeHandle FindTopLeftButton()
        {
            NodeHandle best = default;
            void Visit(NodeHandle n)
            {
                if (n.IsNull || !best.IsNull) return;
                var role = host.Scene.Interaction(n).Role;
                var r = host.Scene.AbsoluteRect(n);
                if (role == AutomationRole.Button && r.X < 64f && r.Y < 64f && r.W >= 36f && r.W <= 52f && r.H >= 36f && r.H <= 52f)
                {
                    best = n;
                    return;
                }
                for (var c = host.Scene.FirstChild(n); !c.IsNull; c = host.Scene.NextSibling(c)) Visit(c);
            }
            Visit(host.Scene.Root);
            return best;
        }

        // The content frame's presented LEFT edge slides 320 → 48 as the pane collapses. AbsoluteRect includes the
        // in-flight LocalTransform, so this reads the ANIMATING value (the model x snaps; the projection animates it).
        float ContentLeft()
        {
            float best = 1e9f;
            void Visit(NodeHandle n)
            {
                if (n.IsNull) return;
                var r = host.Scene.AbsoluteRect(n);
                if (r.W > 400f && r.H > 600f && r.X > 30f && r.X < 340f) best = MathF.Min(best, r.X);
                for (var c = host.Scene.FirstChild(n); !c.IsNull; c = host.Scene.NextSibling(c)) Visit(c);
            }
            Visit(host.Scene.Root);
            return best > 1e8f ? -1f : best;
        }

        float x0 = ContentLeft();                 // expanded: content frame at ~320
        var toggle = FindTopLeftButton();
        var tr = host.Scene.AbsoluteRect(toggle);
        var center = new Point2(tr.X + tr.W * 0.5f, tr.Y + tr.H * 0.5f);
        window.QueueInput(new InputEvent(InputKind.PointerDown, center, 0, 0));
        window.QueueInput(new InputEvent(InputKind.PointerUp, center, 0, 0));
        host.RunFrame();                          // reconcile: collapse → seed the content slide + label exits
        var compositorFrame = host.RunFrame();    // next frame advances the springs with NO reconcile / NO relayout
        bool compositorOnly = !compositorFrame.Rendered && host.Animation.HasActive;
        float x1 = ContentLeft();                 // mid-slide: strictly between 48 and 320
        for (int i = 0; i < 30; i++) host.RunFrame();
        float x2 = ContentLeft();                 // settled: ~48

        Check("54b. NavigationView collapse slides content via compositor-only projection (no re-render ticks)",
            !toggle.IsNull && x0 > 300f && x1 < x0 - 4f && x1 > 48f && Near(x2, 48f, 3f) && compositorOnly,
            $"contentX={x0:0}->{x1:0}->{x2:0} compositorOnly={compositorOnly}");
    }

    // ZStack overlays children at the origin (last on top); ItemsRepeater builds (Inline) or virtualizes (Stack).
    static void ZStackRepeaterChecks(StringTable strings)
    {
        var scene = LayoutTree(strings, Ui.ZStack(
            new BoxEl { Width = 120, Height = 40, Fill = ColorF.FromRgba(200, 0, 0) },
            new BoxEl { Width = 60, Height = 20, Fill = ColorF.FromRgba(0, 200, 0) }) with { Width = 200, Height = 100 });
        var z0 = scene.AbsoluteRect(Child(scene, scene.Root, 0));
        var z1 = scene.AbsoluteRect(Child(scene, scene.Root, 1));
        bool zstack = Near(z0.X, 0) && Near(z0.Y, 0) && Near(z1.X, 0) && Near(z1.Y, 0) && Near(z0.W, 120) && Near(z1.W, 60);

        var inlineEl = Repeater.ItemsRepeater(5, i => new BoxEl { Width = 10, Height = 10 }, RepeatLayout.Inline(gap: 2f), keyOf: i => "k" + i);
        bool inlineN = inlineEl is BoxEl box && box.Children.Length == 5 && box.Children[0].Key == "k0";
        bool stackVirtual = Repeater.ItemsRepeater(1000, i => new BoxEl(), RepeatLayout.Stack(40f)) is VirtualListEl;

        Check("55. ZStack overlays at origin; ItemsRepeater builds (Inline) / virtualizes (Stack)", zstack && inlineN && stackVirtual,
            $"z0=({z0.X:0},{z0.Y:0}) z1=({z1.X:0},{z1.Y:0}) inlineN={inlineN} stackVirt={stackVirtual}");
    }

    // Per-run font family threads from TextEl → TextStyle → DrawGlyphRun (the renderer resolves the face by family);
    // Ui.Icon renders a glyph from the icon font. (Real glyph rendering is needs-pixels; the threading is verified here.)
    static void FontFamilyChecks(StringTable strings)
    {
        var scene = new SceneStore();
        new TreeReconciler(scene, strings).ReconcileRoot(
            new BoxEl { Direction = 1, Children = [new TextEl("hi") { FontFamily = "Segoe Fluent Icons" }, new TextEl("plain")] }, null);
        new FlexLayout(scene, new HeadlessFontSystem(strings)).Run(scene.Root);
        var dl = new DrawList();
        SceneRecorder.Record(scene, dl);
        var dev = new HeadlessGpuDevice();
        dev.SubmitDrawList(dl.Bytes, dl.SortKeys, new FrameInfo(new Size2(400, 300), 1f, ColorF.Transparent));

        bool hiFam = false, plainEmpty = false;
        foreach (var g in dev.LastGlyphs)
        {
            string t = strings.Resolve(g.Text), fam = strings.Resolve(g.Family);
            if (t == "hi") hiFam = fam == "Segoe Fluent Icons";
            if (t == "plain") plainEmpty = fam.Length == 0;
        }
        var icon = Ui.Icon(Icons.Play, 20f);
        bool iconFactory = icon.FontFamily == Theme.IconFont && icon.Text == Icons.Play && icon.Size == 20f;
        Check("56. per-run font family threads to the glyph cmd; Ui.Icon uses the icon font", hiFam && plainEmpty && iconFactory,
            $"hiFam={hiFam} plainEmpty={plainEmpty} iconFont='{icon.FontFamily}'");
    }

    // Gradient elevation border (WinUI ControlElevationBorderBrush): a BoxEl with a 2-stop vertical BorderBrush emits a
    // DrawGradientStroke band (inset by bw/2, the gradient PS sampled vertically) on top of the solid fill.
    static void GradientBorderChecks(StringTable strings)
    {
        var brush = Ui.LinearGradient(90f,
            new GradientStop(0.33f, ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x18)),
            new GradientStop(1f, ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x12)));
        var scene = LayoutTree(strings, new BoxEl
        {
            Width = 120, Height = 40, Corners = CornerRadius4.All(4f), Fill = ColorF.FromRgba(20, 20, 20),
            BorderBrush = brush, BorderWidth = 1f,
        });
        var dl = new DrawList();
        SceneRecorder.Record(scene, dl);
        var dev = new HeadlessGpuDevice();
        dev.SubmitDrawList(dl.Bytes, dl.SortKeys, new FrameInfo(new Size2(400, 300), 1f, ColorF.Transparent));

        bool oneStroke = dev.LastGradientStrokes.Count == 1;
        var gs = oneStroke ? dev.LastGradientStrokes[0] : default;
        bool band = oneStroke && Near(gs.StrokeWidth, 1f) && gs.StopCount == 2
            && Near(gs.Rect.X, 0.5f) && Near(gs.Rect.W, 119f);          // ring inset by bw/2, width = bounds − bw
        bool solidFill = dev.LastRects.Count == 1;                      // the fill drew once (full bounds), border is the gradient stroke
        Check("57. gradient elevation border emits a DrawGradientStroke band", oneStroke && band && solidFill,
            $"strokes={dev.LastGradientStrokes.Count} w={gs.StrokeWidth:0.0} stops={gs.StopCount} ring=({gs.Rect.X:0.0},{gs.Rect.W:0.0})");

        var fillScene = LayoutTree(strings, new BoxEl
        {
            Width = 120, Height = 40, Corners = CornerRadius4.All(4f),
            Gradient = Ui.LinearGradient(0f, new GradientStop(0f, ColorF.FromRgba(0, 0, 0)), new GradientStop(1f, ColorF.FromRgba(255, 255, 255))),
        });
        dl.Reset();
        SceneRecorder.Record(fillScene, dl);
        dev.SubmitDrawList(dl.Bytes, dl.SortKeys, new FrameInfo(new Size2(400, 300), 1f, ColorF.Transparent));
        bool oneFill = dev.LastGradients.Count == 1 && dev.LastGradients[0].StopCount == 2 && dev.LastRects.Count == 0;
        Check("57b. gradient-only BoxEl emits a DrawGradientRect", oneFill,
            $"gradients={dev.LastGradients.Count} rects={dev.LastRects.Count}");
    }

    // Hover cross-fade: the InteractionAnimator eases HoverT and the recorder lerps Fill→HoverFill in LINEAR light
    // (the WinUI ~83ms BackgroundTransition) — it must ease through an intermediate value, not snap, then settle.
    static void CrossfadeChecks(StringTable strings)
    {
        var scene = LayoutTree(strings, new BoxEl
        {
            Width = 100, Height = 40, Fill = ColorF.FromRgba(0, 0, 0), HoverFill = ColorF.FromRgba(255, 255, 255),
        });
        var node = scene.Root;
        var ia = new InteractionAnimator(scene);
        ia.SetHover(node, true);

        var dl = new DrawList();
        var dev = new HeadlessGpuDevice();
        float Grey()
        {
            dl.Reset(); SceneRecorder.Record(scene, dl);
            dev.SubmitDrawList(dl.Bytes, dl.SortKeys, new FrameInfo(new Size2(200, 100), 1f, ColorF.Transparent));
            return dev.LastRects[0].Fill.R;
        }

        ia.Tick(4f);                                   // small step → partway, not snapped
        float mid = Grey();
        bool eased = mid > 0.02f && mid < 0.98f;
        for (int i = 0; i < 16; i++) ia.Tick(16f);     // run past 83ms → settle
        float settled = Grey();
        bool done = settled > 0.99f && !ia.HasActive;
        Check("58. hover cross-fade eases in linear light, then settles", eased && done, $"mid={mid:0.00} settled={settled:0.00}");
    }

    static SceneStore Single(StringTable strings)
    {
        var scene = new SceneStore();
        new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl { Width = 20, Height = 20, Fill = ColorF.FromRgba(200, 0, 0) }, null);
        return scene;
    }

    // Granular re-render: a nested component's setState re-renders ONLY that component — not its sibling, not the app.
    static void GranularityChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("gran", new Size2(480, 320), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        Gran.Counts[0] = 0; Gran.Counts[1] = 0; Gran.Parent = 0;
        using var host = new AppHost(app, window, device, fonts, strings, new GranParent());
        host.RunFrame();
        int p0 = Gran.Parent, a0 = Gran.Counts[0], b0 = Gran.Counts[1];

        var child0 = Child(host.Scene, host.Scene.Root, 0);     // GranChild(0) anchor
        var box0 = Child(host.Scene, child0, 0);                // its rendered clickable box
        var r = host.Scene.AbsoluteRect(box0);
        var c = new Point2(r.X + r.W / 2f, r.Y + r.H / 2f);
        window.QueueInput(new InputEvent(InputKind.PointerDown, c, 0, 0));
        window.QueueInput(new InputEvent(InputKind.PointerUp, c, 0, 0));
        var f = host.RunFrame();

        bool only0 = Gran.Counts[0] == a0 + 1 && Gran.Counts[1] == b0 && Gran.Parent == p0;
        Check("59. setState re-renders ONLY the owning component (granular, not the app)",
            only0 && f.ComponentsRendered == 1 && HasGlyph(device, strings, "c0:1"),
            $"c0+{Gran.Counts[0] - a0} c1+{Gran.Counts[1] - b0} parent+{Gran.Parent - p0} componentsRendered={f.ComponentsRendered}");
    }

    // The slider tank, fixed: a signal-bound slider drag updates the thumb/fill transforms with no render/reconcile/layout.
    static void SliderSignalChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("slidersig", new Size2(320, 120), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        SliderSignalProbe.Renders = 0;
        var root = new SliderSignalProbe();
        using var host = new AppHost(app, window, device, fonts, strings, root);
        host.RunFrame();
        int renders0 = SliderSignalProbe.Renders;

        var thumbRow = Child(host.Scene, host.Scene.Root, 1);
        var thumb = Child(host.Scene, thumbRow, 0);
        float dx0 = host.Scene.Paint(thumb).LocalTransform.Dx;

        root.Sig!.Value = 0.7f;          // a drag would do exactly this
        var f = host.RunFrame();
        float dx1 = host.Scene.Paint(thumb).LocalTransform.Dx;

        bool moved = MathF.Abs(dx1 - dx0) > 50f;                  // ~0.4 * 200 = 80px
        bool noRerender = SliderSignalProbe.Renders == renders0;  // the owning component did NOT re-render
        bool compositorOnly = !f.Rendered;                        // no reconcile + no layout this frame
        Check("60. signal-bound slider: value→transform, NO re-render/reconcile/layout (the slider tank, fixed)",
            moved && noRerender && compositorOnly,
            $"thumbDx {dx0:0}→{dx1:0} renders+{SliderSignalProbe.Renders - renders0} rendered={f.Rendered}");
    }

    // Reactive control-flow: For (keyed list) + Show (conditional) restructure the tree with NO parent re-render.
    static void FlowChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("flow", new Size2(320, 480), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        FlowProbe.Renders = 0;
        var root = new FlowProbe();
        using var host = new AppHost(app, window, device, fonts, strings, root);
        host.RunFrame();
        int r0 = FlowProbe.Renders;
        bool init = HasGlyph(device, strings, "row0") && HasGlyph(device, strings, "row2") && !HasGlyph(device, strings, "row3") && HasGlyph(device, strings, "SHOWN");

        root.Count!.Value = 5;
        host.RunFrame();
        bool grew = HasGlyph(device, strings, "row4") && FlowProbe.Renders == r0;

        root.Toggle!.Value = false;
        host.RunFrame();
        bool toggled = HasGlyph(device, strings, "HIDDEN") && !HasGlyph(device, strings, "SHOWN") && FlowProbe.Renders == r0;

        Check("61. reactive For/Show restructure the tree with NO parent re-render", init && grew && toggled,
            $"init={init} grew={grew} toggled={toggled} parentRenders+{FlowProbe.Renders - r0}");
    }

    static void RepeatButtonChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("repeat", new Size2(320, 200), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var root = new RepeatProbe();
        using var host = new AppHost(app, window, device, fonts, strings, root);

        host.RunFrame();
        var btn = FindRole(host.Scene, host.Scene.Root, AutomationRole.Button);
        var center = CenterOf(host.Scene, btn);

        // Press and HOLD (no up): the ticker fires once immediately, then repeats while held.
        window.QueueInput(new InputEvent(InputKind.PointerDown, center, 0, 0));
        host.RunFrame();
        int afterPress = root.Clicks;                     // 1 (fired on arm)
        bool activeHeld = host.HasActiveWork;             // frames keep flowing while held
        for (int i = 0; i < 45; i++) host.RunFrame();     // ~720ms: past the 500ms initial delay + a few intervals
        int heldClicks = root.Clicks;

        // Release: the repeat stops; clicks no longer grow (no busy loop).
        window.QueueInput(new InputEvent(InputKind.PointerUp, center, 0, 0));
        host.RunFrame();
        int atRelease = root.Clicks;
        for (int i = 0; i < 10; i++) host.RunFrame();
        int afterRelease = root.Clicks;

        Check("62. RepeatButton: press fires once, holds repeat, release stops",
            afterPress == 1 && activeHeld && heldClicks >= 2 && afterRelease == atRelease,
            $"press={afterPress} held={heldClicks} release={atRelease}→{afterRelease}");
        Check("62a. RepeatButton: idle after release does no work (no busy loop)", !host.HasActiveWork);
    }

    static void TextInputChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("edit", new Size2(320, 200), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var root = new EditTextProbe();
        using var host = new AppHost(app, window, device, fonts, strings, root);

        host.RunFrame();
        var field = FindRole(host.Scene, host.Scene.Root, AutomationRole.Text);
        bool found = !field.IsNull;
        var center = CenterOf(host.Scene, field);

        // Focus the field, then type "hi5" via WM_CHAR-style events.
        window.QueueInput(new InputEvent(InputKind.PointerDown, center, 0, 0));
        window.QueueInput(new InputEvent(InputKind.PointerUp, center, 0, 0));
        host.RunFrame();
        window.QueueInput(new InputEvent(InputKind.Char, default, 0, 'h'));
        window.QueueInput(new InputEvent(InputKind.Char, default, 0, 'i'));
        window.QueueInput(new InputEvent(InputKind.Char, default, 0, '5'));
        host.RunFrame();
        string typed = root.Text!.Peek();

        // Backspace removes the last char.
        window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Back));
        host.RunFrame();
        string afterBack = root.Text!.Peek();

        // Sanitize clamps to 8 chars: type a long run.
        for (int i = 0; i < 12; i++) window.QueueInput(new InputEvent(InputKind.Char, default, 0, 'x'));
        host.RunFrame();
        string clamped = root.Text!.Peek();

        Check("63. text input: focused field accepts WM_CHAR, backspace, sanitize",
            found && typed == "hi5" && afterBack == "hi" && clamped.Length == 8,
            $"typed='{typed}' back='{afterBack}' clampedLen={clamped.Length}");
    }

    static void OverlayChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("overlay", new Size2(480, 360), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var root = new OverlayProbe();
        using var host = new AppHost(app, window, device, fonts, strings, root);

        host.RunFrame();
        var svc = root.Service!;
        Func<Element> menu = () => new BoxEl
        {
            Width = 140, Direction = 1,
            Children =
            [
                new BoxEl
                {
                    Height = 32, Role = AutomationRole.MenuItem, OnClick = () => { root.Selected = 7; svc.CloseTop(); },
                    Children = [Ui.Text("Item A")],
                },
            ],
        };

        // Let the close fade animation (83ms) settle, then the OverlayCloseDriver removes the popup.
        void Settle() { for (int i = 0; i < 16; i++) host.RunFrame(); }

        // Open → the popup (a MenuItem) appears in the scene.
        svc.Open(() => root.Anchor, menu, FlyoutPlacement.BottomLeft);
        host.RunFrame();
        bool opened = !FindRole(host.Scene, host.Scene.Root, AutomationRole.MenuItem).IsNull;

        // Escape closes it (via the dispatcher key-preview hook).
        window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Escape));
        host.RunFrame();
        Settle();
        bool escClosed = FindRole(host.Scene, host.Scene.Root, AutomationRole.MenuItem).IsNull;

        // Re-open → light dismiss: a click away from the popup hits the scrim and closes it.
        svc.Open(() => root.Anchor, menu, FlyoutPlacement.BottomLeft);
        host.RunFrame();
        window.QueueInput(new InputEvent(InputKind.PointerDown, new Point2(460, 350), 0, 0));
        window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(460, 350), 0, 0));
        host.RunFrame();
        Settle();
        bool lightDismissed = FindRole(host.Scene, host.Scene.Root, AutomationRole.MenuItem).IsNull;

        // Re-open → click the item: invokes its command and closes.
        svc.Open(() => root.Anchor, menu, FlyoutPlacement.BottomLeft);
        host.RunFrame();
        var item = FindRole(host.Scene, host.Scene.Root, AutomationRole.MenuItem);
        var ic = CenterOf(host.Scene, item);
        window.QueueInput(new InputEvent(InputKind.PointerDown, ic, 0, 0));
        window.QueueInput(new InputEvent(InputKind.PointerUp, ic, 0, 0));
        host.RunFrame();
        Settle();
        bool invoked = root.Selected == 7 && FindRole(host.Scene, host.Scene.Root, AutomationRole.MenuItem).IsNull;

        Check("64. overlay: anchored flyout opens, Escape + light-dismiss close, item invokes",
            opened && escClosed && lightDismissed && invoked,
            $"open={opened} esc={escClosed} dismiss={lightDismissed} invoke={invoked}");
    }

    static void OverlayAnimationChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("ovanim", new Size2(480, 400), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var root = new OverlayProbe();
        using var host = new AppHost(app, window, device, fonts, strings, root);
        host.RunFrame();

        var svc = root.Service!;
        Func<Element> menu = () => MenuFlyout.Build(new[]
        {
            new MenuFlyoutItem("One"), new MenuFlyoutItem("Two"), new MenuFlyoutItem("Three"),
            new MenuFlyoutItem("Four"), new MenuFlyoutItem("Five"),
        }, () => svc.CloseTop());

        NodeHandle SurfaceOf(NodeHandle n) { for (; !n.IsNull; n = host.Scene.Parent(n)) if (host.Scene.TryGetAcrylic(n, out _)) return n; return NodeHandle.Null; }

        svc.Open(() => root.Anchor, menu, FlyoutPlacement.BottomLeft);
        host.RunFrame();   // mount + seed the SizeH clip-reveal + fade (seed runs in a layout effect)
        host.RunFrame();   // tick the animation once → the first PresentedH lands
        var surface = SurfaceOf(FindRole(host.Scene, host.Scene.Root, AutomationRole.MenuItem));
        float fullH = surface.IsNull ? 0f : host.Scene.Bounds(surface).H;
        float ph1 = surface.IsNull ? float.NaN : host.Scene.Paint(surface).PresentedH;   // mid-reveal (top-anchored), < full
        bool revealing = !surface.IsNull && !float.IsNaN(ph1) && ph1 > 1f && ph1 < fullH - 2f;

        for (int i = 0; i < 24; i++) host.RunFrame();             // open settles
        float ph2 = surface.IsNull ? float.NaN : host.Scene.Paint(surface).PresentedH;
        bool revealed = float.IsNaN(ph2) || ph2 >= fullH - 1.5f;

        // Close → the surface fades (opacity animates down) while staying on top (not removed instantly).
        svc.CloseTop();
        host.RunFrame();
        float op = host.Scene.IsLive(surface) ? host.Scene.Paint(surface).Opacity : 0f;
        bool fading = host.Scene.IsLive(surface) && op < 0.99f;

        Check("64d. flyout: open clip-reveals (SizeH top-anchored) then close fades", revealing && revealed && fading,
            $"reveal {ph1:0}/{fullH:0}→{ph2:0} closeOpacity={op:0.00}");
    }

    static void MenuFlyoutStyleChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("menustyle", new Size2(480, 360), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var root = new OverlayProbe();
        using var host = new AppHost(app, window, device, fonts, strings, root);

        host.RunFrame();
        var items = new[]
        {
            new MenuFlyoutItem("Small", Icons.Tag),
            new MenuFlyoutItem("Medium", Icons.Tag),
            new MenuFlyoutItem("Large", Icons.Tag),
            MenuFlyoutItem.Separator,
            new MenuFlyoutItem("Disabled", Icons.Cancel, false),
        };
        root.Service!.Open(() => root.Anchor, () => MenuFlyout.Build(items, () => root.Service!.CloseTop()), FlyoutPlacement.BottomLeft);
        host.RunFrame();

        var rows = Roles(host.Scene, AutomationRole.MenuItem);
        bool rowMetrics = rows.Count == 4 && Near(host.Scene.Bounds(rows[0]).H, 36f);
        // Walk up from a row to the acrylic FlyoutSurface (the presenter card; structure: surface > clip > content > rows).
        NodeHandle surface = rows.Count > 0 ? rows[0] : NodeHandle.Null;
        while (!surface.IsNull && !host.Scene.TryGetAcrylic(surface, out _)) surface = host.Scene.Parent(surface);
        bool acrylic = !surface.IsNull;
        float surfaceW = surface.IsNull ? 0f : host.Scene.AbsoluteRect(surface).W;
        bool minWidth = surfaceW >= 96f;   // FlyoutThemeMinWidth (generic.xaml)
        bool hasLayer = device.LastLayers.Count > 0 && device.LayerBalance == 0;
        bool disabled = rows.Count == 4 && (host.Scene.Interaction(rows[3]).HandlerMask & InteractionInfo.ClickBit) == 0;

        Check("64b. MenuFlyout: WinUI-like presenter chrome, row metrics, disabled command",
            rowMetrics && minWidth && acrylic && hasLayer && disabled,
            $"rows={rows.Count} h={(rows.Count > 0 ? host.Scene.Bounds(rows[0]).H : 0):0} w={surfaceW:0} acrylic={acrylic} layers={device.LastLayers.Count} disabled={disabled}");
    }

    static void SplitButtonStyleChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("splitstyle", new Size2(360, 160), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new SplitButtonProbe());
        host.RunFrame();

        var buttons = Roles(host.Scene, AutomationRole.Button);
        var primary = buttons.Count > 0 ? buttons[0] : NodeHandle.Null;
        var drop = buttons.Count > 1 ? buttons[1] : NodeHandle.Null;
        var outer = primary.IsNull ? NodeHandle.Null : host.Scene.Parent(primary);
        bool twoHalves = buttons.Count == 2;
        bool joinedChrome = !outer.IsNull && host.Scene.Paint(outer).Fill.A > 0f && Near(host.Scene.Paint(outer).BorderWidth, 1f)
                            && (host.Scene.Flags(outer) & NodeFlags.ClipsToBounds) != 0;
        bool halfMetrics = !primary.IsNull && !drop.IsNull
                           // WinUI SplitButtonSecondaryButtonSize = 35 (width); control height = 32.
                           && Near(host.Scene.Bounds(primary).H, 32f) && Near(host.Scene.Bounds(drop).W, 35f) && Near(host.Scene.Bounds(drop).H, 32f);
        bool transparentHalves = !primary.IsNull && !drop.IsNull
                                 && host.Scene.Paint(primary).Fill.A == 0f && host.Scene.Paint(drop).Fill.A == 0f;

        Check("64c. SplitButton: joined outer chrome with independently interactive halves",
            twoHalves && joinedChrome && halfMetrics && transparentHalves,
            $"buttons={buttons.Count} chrome={joinedChrome} primaryH={(primary.IsNull ? 0 : host.Scene.Bounds(primary).H):0} drop={(drop.IsNull ? 0 : host.Scene.Bounds(drop).W):0}x{(drop.IsNull ? 0 : host.Scene.Bounds(drop).H):0}");
    }

    static void NavHierarchyChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("navhier", new Size2(1200, 700), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new NavHierarchyProbe());
        host.RunFrame();

        var items = new List<NodeHandle>();
        CollectRole(host.Scene, host.Scene.Root, AutomationRole.NavigationItem, items);
        int collapsedCount = items.Count;   // home, group (children hidden — group starts collapsed)

        // Click the group → its children appear.
        var groupCenter = CenterOf(host.Scene, items[1]);
        window.QueueInput(new InputEvent(InputKind.PointerDown, groupCenter, 0, 0));
        window.QueueInput(new InputEvent(InputKind.PointerUp, groupCenter, 0, 0));
        host.RunFrame();
        items.Clear();
        CollectRole(host.Scene, host.Scene.Root, AutomationRole.NavigationItem, items);
        int expandedCount = items.Count;    // home, group, c1, c2
        bool childrenAppeared = expandedCount == collapsedCount + 2;

        // Select the first child → content updates.
        bool childSelected = false;
        if (childrenAppeared)
        {
            var childCenter = CenterOf(host.Scene, items[2]);
            window.QueueInput(new InputEvent(InputKind.PointerDown, childCenter, 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerUp, childCenter, 0, 0));
            host.RunFrame();
            childSelected = HasGlyph(device, strings, "PAGE:c1");
        }

        // Click the group again → it collapses (children disappear).
        items.Clear();
        CollectRole(host.Scene, host.Scene.Root, AutomationRole.NavigationItem, items);
        var g2 = CenterOf(host.Scene, items[1]);
        window.QueueInput(new InputEvent(InputKind.PointerDown, g2, 0, 0));
        window.QueueInput(new InputEvent(InputKind.PointerUp, g2, 0, 0));
        host.RunFrame();
        items.Clear();
        CollectRole(host.Scene, host.Scene.Root, AutomationRole.NavigationItem, items);
        bool collapsedAgain = items.Count == collapsedCount;

        Check("65. NavigationView: group expands/collapses + child selection updates content",
            collapsedCount == 2 && childrenAppeared && childSelected && collapsedAgain,
            $"collapsed={collapsedCount} expanded={expandedCount} childPage={childSelected} recollapsed={collapsedAgain}");
    }

    static void BasicInputControlChecks(StringTable strings)
    {
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);

        // CheckBox — two/three-state cycle.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cb", new Size2(320, 160), 1f)); window.Show();
            var root = new CheckBoxProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var s0 = root.State;
            ClickNode(host, window, FindRole(host.Scene, host.Scene.Root, AutomationRole.CheckBox)); var s1 = root.State;
            ClickNode(host, window, FindRole(host.Scene, host.Scene.Root, AutomationRole.CheckBox)); var s2 = root.State;
            ClickNode(host, window, FindRole(host.Scene, host.Scene.Root, AutomationRole.CheckBox)); var s3 = root.State;
            Check("66. CheckBox cycles Unchecked→Checked→Indeterminate→Unchecked",
                s0 == CheckState.Unchecked && s1 == CheckState.Checked && s2 == CheckState.Indeterminate && s3 == CheckState.Unchecked,
                $"{s0}→{s1}→{s2}→{s3}");
        }

        // RadioButton — mutual exclusion via a shared index.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("rb", new Size2(320, 200), 1f)); window.Show();
            var root = new RadioProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var radios = Roles(host.Scene, AutomationRole.RadioButton);
            ClickNode(host, window, radios[1]); int sel1 = root.Selected;
            radios = Roles(host.Scene, AutomationRole.RadioButton);
            ClickNode(host, window, radios[2]); int sel2 = root.Selected;
            Check("67. RadioButton group: selecting one deselects the others",
                radios.Count == 3 && sel1 == 1 && sel2 == 2, $"count={radios.Count} sel {sel1}→{sel2}");
        }

        // ToggleSwitch — flips on/off.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("ts", new Size2(320, 160), 1f)); window.Show();
            var root = new ToggleSwitchProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            ClickNode(host, window, FindRole(host.Scene, host.Scene.Root, AutomationRole.ToggleSwitch)); bool on1 = root.On;
            ClickNode(host, window, FindRole(host.Scene, host.Scene.Root, AutomationRole.ToggleSwitch)); bool on2 = root.On;
            Check("68. ToggleSwitch toggles on/off", on1 && !on2, $"{on1}→{on2}");
        }

        // RatingControl — click sets, drag sweeps.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("rt", new Size2(320, 120), 1f)); window.Show();
            var root = new RatingProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var rating = FindRole(host.Scene, host.Scene.Root, AutomationRole.Rating);
            var rr = host.Scene.AbsoluteRect(rating);
            var p3 = new Point2(rr.X + 70f, rr.Y + rr.H / 2f);   // 3rd star (stride 28)
            window.QueueInput(new InputEvent(InputKind.PointerDown, p3, 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerUp, p3, 0, 0));
            host.RunFrame();
            float v3 = root.Val!.Peek();
            var p5 = new Point2(rr.X + 130f, rr.Y + rr.H / 2f); // sweep to 5th
            window.QueueInput(new InputEvent(InputKind.PointerDown, p3, 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerMove, p5, 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerUp, p5, 0, 0));
            host.RunFrame();
            float v5 = root.Val!.Peek();
            Check("69. RatingControl: click sets value, drag sweeps", Near(v3, 3f) && Near(v5, 5f), $"click={v3} drag={v5}");
        }

        // ComboBox — closed selection.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cmb", new Size2(420, 320), 1f)); window.Show();
            var root = new ComboProbe(false);
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            ClickNode(host, window, FindRole(host.Scene, host.Scene.Root, AutomationRole.ComboBox));
            var menuItems = Roles(host.Scene, AutomationRole.MenuItem);
            bool opened = menuItems.Count == 3;
            int sel = -2;
            if (opened) { ClickNode(host, window, menuItems[1]); sel = root.Sel!.Peek(); }
            Check("70. ComboBox: opens a list and selects an item", opened && sel == 1, $"items={menuItems.Count} sel={sel}");
        }

        // ComboBox — editable text entry.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cmbe", new Size2(420, 320), 1f)); window.Show();
            var root = new ComboProbe(true);
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            ClickNode(host, window, FindRole(host.Scene, host.Scene.Root, AutomationRole.Text));
            window.QueueInput(new InputEvent(InputKind.Char, default, 0, 'l'));
            window.QueueInput(new InputEvent(InputKind.Char, default, 0, 'o'));
            window.QueueInput(new InputEvent(InputKind.Char, default, 0, 'w'));
            host.RunFrame();
            string txt = root.Txt!.Peek();
            Check("71. ComboBox: editable mode accepts typed text", txt == "low", $"text='{txt}'");
        }

        // Slider (ranged) — value range mapping + step snapping.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("rsl", new Size2(320, 120), 1f)); window.Show();
            var root = new RangeSliderProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var track = FindRole(host.Scene, host.Scene.Root, AutomationRole.Slider);
            var tr = host.Scene.AbsoluteRect(track);
            var mid = new Point2(tr.X + 100f, tr.Y + tr.H / 2f);   // raw 0.5 of length 200 → 50, snapped to step 10
            window.QueueInput(new InputEvent(InputKind.PointerDown, mid, 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerUp, mid, 0, 0));
            host.RunFrame();
            float v = root.Val;
            Check("72a. Slider.Ranged: maps to [min,max] and snaps to step", Near(v, 50f), $"value={v}");
        }

        // ColorPicker — hue / spectrum / alpha drags + a hex channel edit.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cp", new Size2(420, 420), 1f)); window.Show();
            var root = new ColorPickerProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            bool gradientsDrawn = device.LastGradients.Count >= 9;   // spectrum(2) + hue(6) + alpha(1)
            var sliders = Roles(host.Scene, AutomationRole.Slider);   // [spectrum, hue, alpha]
            var sr = host.Scene.AbsoluteRect(sliders[0]);
            bool spectrumSquare = Near(sr.W, 256f) && Near(sr.H, 256f);
            void DragTo(NodeHandle n, float fx, float fy)
            {
                var r = host.Scene.AbsoluteRect(n);
                var p = new Point2(r.X + r.W * fx, r.Y + r.H * fy);
                window.QueueInput(new InputEvent(InputKind.PointerDown, p, 0, 0));
                window.QueueInput(new InputEvent(InputKind.PointerUp, p, 0, 0));
                host.RunFrame();
            }
            DragTo(sliders[1], 0.5f, 0.5f);  var hueHsv = root.Color!.Peek().ToHsv();
            DragTo(sliders[0], 0.5f, 0.5f);  var sv = root.Color!.Peek().ToHsv();
            DragTo(sliders[2], 0.5f, 0.5f);  float a = root.Color!.Peek().A;

            // Hex channel: clear the field and type a pure green.
            var hex = Roles(host.Scene, AutomationRole.Text)[0];
            ClickNode(host, window, hex);
            for (int i = 0; i < 6; i++) window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Back));
            foreach (char ch in "00FF00") window.QueueInput(new InputEvent(InputKind.Char, default, 0, ch));
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Enter));
            host.RunFrame();
            var ce = root.Color!.Peek();

            bool hueOk = MathF.Abs(hueHsv.H - 180f) < 45f;
            bool svOk = sv.S > 0.3f && sv.S < 0.7f && sv.V > 0.3f && sv.V < 0.7f;
            bool alphaOk = a > 0.3f && a < 0.7f;
            bool hexOk = ce.G > 0.8f && ce.R < 0.2f && ce.B < 0.2f;
            Check("72. ColorPicker: hue/spectrum/alpha drags + hex channel update the color",
                gradientsDrawn && spectrumSquare && hueOk && svOk && alphaOk && hexOk,
                $"gradients={device.LastGradients.Count} spectrum={sr.W:0}x{sr.H:0} H={hueHsv.H:0} S={sv.S:0.00} V={sv.V:0.00} A={a:0.00} hex=({ce.R:0.0},{ce.G:0.0},{ce.B:0.0})");
        }
    }

    static int Main()
    {
        Console.WriteLine("FluentGpu — minimum vertical slice (headless RHI/PAL/Text)\n");

        var strings = new StringTable();
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("FluentGpu slice", new Size2(480, 320), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var root = new Counter();
        using var host = new AppHost(app, window, device, fonts, strings, root);

        // Frame 1 — mount: window→clear→two button rects (SDF) + three text runs, flex-laid-out.
        var f1 = host.RunFrame();
        Check("1. window + GPU clear + present", device.FrameCount == 1, $"backend={device.BackendName}, clear=#{device.LastClear.R:0.0},{device.LastClear.G:0.0},{device.LastClear.B:0.0}");
        Check("2. rounded-rect primitives (2 accent buttons × fill + gradient elevation border)", device.LastRects.Count == 2 && device.LastGradientStrokes.Count == 2, $"rects={device.LastRects.Count} gradBorders={device.LastGradientStrokes.Count}");
        Check("3. text runs (heading + 2 labels)", device.LastGlyphs.Count == 3, $"glyphs={device.LastGlyphs.Count}");
        Check("4. flex layout produced bounds", host.Scene.AbsoluteRect(host.Scene.Root).W > 0, $"rootW={host.Scene.AbsoluteRect(host.Scene.Root).W:0.#}");
        Check("5. reconciler + UseState (initial render)", f1.Rendered && HasGlyph(device, strings, "Count: 0"));

        // Locate the "+" button (VStack → [Heading, HStack] → HStack.[ '-', '+' ]) and click its center.
        var hstack = Child(host.Scene, host.Scene.Root, 1);
        var plus = Child(host.Scene, hstack, 1);
        var r = host.Scene.AbsoluteRect(plus);
        var center = new Point2(r.X + r.W / 2f, r.Y + r.H / 2f);
        window.QueueInput(new InputEvent(InputKind.PointerDown, center, 0, 0));
        window.QueueInput(new InputEvent(InputKind.PointerUp, center, 0, 0));

        // Frame 2 — the click→setState→repaint round-trip.
        var f2 = host.RunFrame();
        Check("6. clickable Button → OnClick fired", f2.ClicksHandled == 1, $"hit @ ({center.X:0.#},{center.Y:0.#})");
        Check("7. setState re-rendered the label", f2.Rendered && HasGlyph(device, strings, "Count: 1"), "Count: 0 → Count: 1");

        // Warm, then assert the steady paint half (phases 6–13) is zero managed allocation.
        for (int i = 0; i < 6; i++) host.RunFrame();
        var steady = host.RunFrame();
        Check("8. steady frame does no work (memoized)", !steady.Rendered);
        Check("9. ZERO managed alloc on the paint half (phases 6–11)", steady.HotPhaseAllocBytes == 0, $"{steady.HotPhaseAllocBytes} bytes");

        FlexChecks(strings);
        HookChecks();
        KeyedChecks(strings);
        KeyboardChecks(strings);
        AnimChecks();
        ProjectionChecks(strings);
        EnterExitChecks(strings);
        SizeModeChecks(strings);
        NestedChecks(strings);
        ContextChecks(strings);
        HoverChecks(strings);
        StyleChecks();
        AnimValueChecks();
        WrapChecks(strings);
        ConstrainedWrapChecks(strings);
        CompositorChecks(strings);
        AnimEngineChecks(strings);
        AnimHookChecks(strings);
        ScrollChecks(strings);
        ScrollOverlayChecks(strings);
        VirtualChecks(strings);
        ExtentTableChecks();
        VariableChecks(strings);
        ZeroAllocScrollChecks(strings);
        ImageCacheChecks();
        ImageElChecks(strings);
        DecodeSchedulerChecks();
        BlurHashChecks(strings);
        ImageTransitionChecks();
        ImageEvictChecks();
        UseImageChecks(strings);
        ControlsChecks(strings);
        NavigationChecks();
        PageHostChecks(strings);
        GridChecks(strings);
        GridStretchChecks(strings);
        AutoGridChecks(strings);
        VirtualGridChecks(strings);
        ZStackRepeaterChecks(strings);
        NavigationViewChecks(strings);
        NavigationViewAnimationChecks(strings);
        FontFamilyChecks(strings);
        GradientBorderChecks(strings);
        CrossfadeChecks(strings);
        WaveeSkeletonChecks(strings);

        // Signals-first model: granular re-render, the compositor bypass (slider tank), reactive control-flow.
        GranularityChecks(strings);
        SliderSignalChecks(strings);
        FlowChecks(strings);

        // Basic-input infrastructure (Part A): repeat timing, text input, anchored overlays.
        RepeatButtonChecks(strings);
        TextInputChecks(strings);
        OverlayChecks(strings);
        OverlayAnimationChecks(strings);
        MenuFlyoutStyleChecks(strings);
        SplitButtonStyleChecks(strings);

        // Hierarchical NavigationView (Part B).
        NavHierarchyChecks(strings);

        // Basic-input controls (Part C).
        BasicInputControlChecks(strings);

        Console.WriteLine();
        if (s_failures == 0) { Console.WriteLine("ALL CHECKS PASSED — the vertical slice exercises every seam end-to-end."); return 0; }
        Console.WriteLine($"{s_failures} CHECK(S) FAILED."); return 1;
    }
}
