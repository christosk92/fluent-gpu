using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
using FluentGpu.Text;
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

// E3 — implicit BrushTransition: a logical state flip (signal → re-render with a different Fill / text Color) must
// CROSS-FADE the displayed color over BrushTransitionMs instead of snapping (WinUI BrushTransition, 83ms).
sealed class BrushTransitionProbe : Component
{
    public Signal<bool>? On;
    public static readonly ColorF FillA = ColorF.FromRgba(0xFF, 0x00, 0x00);
    public static readonly ColorF FillB = ColorF.FromRgba(0x00, 0x00, 0xFF);
    public static readonly ColorF TextA = ColorF.FromRgba(0x00, 0xFF, 0x00);
    public static readonly ColorF TextB = ColorF.FromRgba(0xFF, 0x00, 0xFF);
    public override Element Render()
    {
        var on = UseSignal(false);
        On = on;
        return new BoxEl
        {
            Width = 77, Height = 33,
            Fill = on.Value ? FillB : FillA,
            BrushTransitionMs = 83f,
            Children =
            [
                new TextEl("bt") { Size = 12f, Color = on.Value ? TextB : TextA, BrushTransitionMs = 83f },
            ],
        };
    }
}

// Two bare clickable boxes for the E1 focus-ring geometry checks: default FocusVisualMargin (−3) and the Slider
// asymmetric −7,0,−7,0. Bare BoxEls (no border/gradient) so LastStrokes carries ONLY the focus rings.
sealed class FocusRingProbe : Component
{
    public override Element Render() => new BoxEl
    {
        Direction = 1, Gap = 20, Padding = Edges4.All(20),
        Children =
        [
            new BoxEl { Width = 100, Height = 40, Fill = ColorF.FromRgba(0x20, 0x20, 0x20), OnClick = () => { } },
            new BoxEl
            {
                Width = 100, Height = 40, Fill = ColorF.FromRgba(0x20, 0x20, 0x20), OnClick = () => { },
                FocusVisualMargin = new Edges4(-7, 0, -7, 0),
            },
        ],
    };
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
sealed class CountingVirtualProbe : Component
{
    public int RenderItemCalls;
    public override Element Render()
        => Virtual.List(1000, 40f,
               renderItem: i => { RenderItemCalls++; return new BoxEl { Height = 40f }; },
               keyOf: i => "r" + i)
           with { Width = 300, Height = 200 };
}

// A BOUND 10k-row list (Virtual.ListBound): the template runs once per slot; scrolling rebinds index signals only.
sealed class BoundVirtualProbe : Component
{
    public const int N = 10_000;
    public int TemplateCalls;
    public override Element Render()
        => Virtual.ListBound(N, 40f, idx =>
           {
               TemplateCalls++;
               return new BoxEl
               {
                   Height = 40,
                   Fill = Prop.Of(() => ColorF.FromRgba(30, 30, (byte)(idx.Value % 2 == 0 ? 30 : 50))),
                   Children = [new TextEl("") { Size = 12f, Text = Prop.Of(() => $"row {idx.Value}") }],
               };
           })
           with { Width = 300, Height = 400 };
}

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

sealed class FlowReorderProbe : Component
{
    public static int Renders;
    public Signal<List<string>>? Items;
    public override Element Render()
    {
        Renders++;
        var items = UseSignal(new List<string> { "fa", "fb", "fc" });
        Items = items;
        return new BoxEl
        {
            Direction = 1,
            Children =
            [
                Flow.For(() => items.Value.Count,
                    i => new BoxEl { Width = 40, Height = 12, Children = [Text(items.Value[i])] },
                    keyOf: i => items.Value[i]),
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

// Two raw clickable boxes: one always enabled, one whose IsEnabled is signal-gated (starts disabled). Exercises the
// engine disabled gate (P1) without depending on any control's hand-rolled handler-nulling.
sealed class DisabledProbe : Component
{
    public int EnabledClicks;
    public int GatedClicks;
    public Signal<bool>? Gate;        // false ⇒ the gated box is disabled
    public NodeHandle EnabledBox;
    public NodeHandle GatedBox;
    public override Element Render()
    {
        var gate = UseSignal(false);
        Gate = gate;
        return new BoxEl
        {
            Direction = 1, Width = 200, Height = 120, Gap = 8,
            Children =
            [
                new BoxEl
                {
                    Width = 120, Height = 32, Role = AutomationRole.Button,
                    Fill = new ColorF(0.2f, 0.2f, 0.2f, 1f),
                    OnClick = () => EnabledClicks++,
                    OnRealized = h => EnabledBox = h,
                },
                new BoxEl
                {
                    Width = 120, Height = 32, Role = AutomationRole.CheckBox,   // distinct role so the test can locate it
                    Fill = new ColorF(0.3f, 0.3f, 0.3f, 1f),
                    IsEnabled = gate.Value,
                    OnClick = () => GatedClicks++,
                    OnRealized = h => GatedBox = h,
                },
            ],
        };
    }
}

// An interactive box (with an interaction-anim row so hover/press EASE) wrapping a TextEl with primary-color state
// ramps, so a test can read the resolved glyph color per interaction/disabled state. Exercises P2.
sealed class TextRampProbe : Component
{
    public Signal<bool>? Enabled;
    public override Element Render()
    {
        var enabled = UseSignal(true);
        Enabled = enabled;
        return new BoxEl
        {
            Width = 160, Height = 40, Role = AutomationRole.Button,
            Fill = new ColorF(0.15f, 0.15f, 0.15f, 1f),
            IsEnabled = enabled.Value,
            OnClick = () => { },
            HoverDurationMs = 80f, PressDurationMs = 80f,   // force an InteractionAnim row → hover/press progress eases
            Children =
            [
                new TextEl("ramp")
                {
                    Color = ColorF.FromRgba(0xFF, 0x00, 0x00),         // resting  = red
                    HoverColor = ColorF.FromRgba(0x00, 0xFF, 0x00),    // hover    = green
                    PressedColor = ColorF.FromRgba(0x00, 0x00, 0xFF),  // pressed  = blue
                    DisabledColor = ColorF.FromRgba(0xFF, 0xFF, 0xFF), // disabled = white
                },
            ],
        };
    }
}

// A real HyperlinkButton for the accent-text / accent-override checks.
sealed class HyperlinkProbe : Component
{
    public override Element Render() => new BoxEl
    {
        Padding = Edges4.All(10),
        Children = [HyperlinkButton.Create("link-text", () => { })],
    };
}

// Two real Buttons (one enabled, one disabled via the adopted IsEnabled gate) for the Wave-2 control checks.
sealed class ButtonProbe : Component
{
    public int Clicks;
    public override Element Render() => new BoxEl
    {
        Direction = 1, Width = 220, Padding = Edges4.All(10), Gap = 8,
        Children =
        [
            Button.Standard("enabled-btn", () => Clicks++),
            Button.Standard("disabled-btn", () => Clicks++, isEnabled: false),
        ],
    };
}

// Hosts an overlay layer with a focusable anchor button, so a test can verify the overlay restores focus to the
// pre-open node when it closes. Exercises P5 focus-restoration.
sealed class FocusRestoreProbe : Component
{
    public IOverlayService? Service;
    public NodeHandle AnchorNode;
    public OverlayHandle? Handle;
    public override Element Render() => Embed.Comp(() => new OverlayHost { Child = Embed.Comp(() => new FocusRestoreInner(this)) });
}

sealed class FocusRestoreInner : Component
{
    readonly FocusRestoreProbe _p;
    public FocusRestoreInner(FocusRestoreProbe p) => _p = p;
    public override Element Render()
    {
        _p.Service = UseContext(Overlay.Service);
        return new BoxEl
        {
            Width = 200, Height = 120, Padding = Edges4.All(10),
            Children =
            [
                new BoxEl { Width = 80, Height = 30, Role = AutomationRole.Button, OnClick = () => { }, OnRealized = h => _p.AnchorNode = h },
            ],
        };
    }
}

// An interactive box whose gradient fill has hover/pressed variants, so a test can read the recorder's per-frame
// interpolated first stop (C0) per interaction state. Exercises P4b.
sealed class GradientRampProbe : Component
{
    public override Element Render() => new BoxEl
    {
        Width = 120, Height = 40, Role = AutomationRole.Button,
        OnClick = () => { },
        HoverDurationMs = 80f, PressDurationMs = 80f,   // force an InteractionAnim row → progress eases
        Gradient = GradientSpec.Vertical(ColorF.FromRgba(0xFF, 0x00, 0x00), ColorF.FromRgba(0xFF, 0x00, 0x00)),         // red
        HoverGradient = GradientSpec.Vertical(ColorF.FromRgba(0x00, 0xFF, 0x00), ColorF.FromRgba(0x00, 0xFF, 0x00)),    // green
        PressedGradient = GradientSpec.Vertical(ColorF.FromRgba(0x00, 0x00, 0xFF), ColorF.FromRgba(0x00, 0x00, 0xFF)),  // blue
    };
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

// W0e — the full EditableText-on-TextEditCore matrix: caret/selection/clipboard/undo/IME/mask/multi-line/delete-button.
sealed class W0eProbe : Component
{
    public Signal<string>? Text;
    public EditableText? Edit;
    public string Initial = "";
    public bool Multi;
    public bool MaskOn;
    public bool ReadOnly;
    public int MaxLen;
    public bool ShowDelete;
    public Func<string, bool>? Before;
    public float W = 160f;
    public float H = 32f;
    public int CancelCount;
    public string? Committed;
    public readonly List<(int Start, int Len)> SelLog = new();

    public override Element Render()
    {
        var t = UseSignal(Initial);
        Text = t;
        return Embed.Comp(() =>
        {
            var e = new EditableText
            {
                Text = t, Width = W, Height = H,
                AcceptsReturn = Multi, Mask = MaskOn, IsReadOnly = ReadOnly, MaxLength = MaxLen,
                ShowDeleteButton = ShowDelete, BeforeTextChanging = Before,
                OnCommit = s => Committed = s,
                OnCancel = () => CancelCount++,
                OnSelectionChanged = (s, l) => SelLog.Add((s, l)),
            };
            Edit = e;
            return e;
        });
    }
}

// W0f — the text-input consumer controls (PasswordBox/NumberBox/AutoSuggestBox/editable ComboBox) on W0e EditableText.
sealed class W0fPasswordProbe : Component
{
    public Signal<string>? Pw;
    public PasswordRevealMode Mode = PasswordRevealMode.Peek;
    public char Char = '●';
    public string Initial = "secret";
    public override Element Render()
    {
        var pw = UseSignal(Initial);
        Pw = pw;
        return PasswordBox.Create("Password", 280f, revealMode: Mode, passwordChar: Char, password: pw);
    }
}

sealed class W0fNumberProbe : Component
{
    public Signal<double>? Val;
    public Signal<string>? Txt;
    public double Initial = 5;
    public NumberBoxSpinButtonPlacementMode Mode = NumberBoxSpinButtonPlacementMode.Hidden;
    public readonly List<(double Old, double New)> Changes = new();
    public override Element Render()
    {
        var v = UseSignal(Initial); Val = v;
        var t = UseSignal(""); Txt = t;
        return Embed.Comp(() => new OverlayHost
        {
            Child = NumberBox.Create(value: v, minimum: 0, maximum: 10, smallChange: 1, largeChange: 5,
                spinButtonPlacementMode: Mode, text: t, onValueChanged: (o, n) => Changes.Add((o, n))),
        });
    }
}

sealed class W0fAsbProbe : Component
{
    public Signal<string>? Query;
    public bool UpdateTextOnSelect = true;
    public readonly List<(string Text, TextChangeReason Reason)> Changes = new();
    public readonly List<string> Chosen = new();
    public readonly List<string> Submitted = new();
    public readonly List<string> Order = new();   // interleaved C:/Q: markers for the SelectionChanged→SuggestionChosen→QuerySubmitted ordering
    public override Element Render()
    {
        var q = UseSignal(""); Query = q;
        return Embed.Comp(() => new OverlayHost
        {
            Child = AutoSuggestBox.Create(
                new[] { "Cascadia Code", "Calendar", "Calculator" }, "Search", 260f, q, debounceMs: 0f,
                textChanged: (s, r) => Changes.Add((s, r)),
                onSuggestionChosen: s => { Chosen.Add(s); Order.Add("C:" + s); },
                onQuerySubmitted: s => { Submitted.Add(s); Order.Add("Q:" + s); },
                updateTextOnSelect: UpdateTextOnSelect),
        });
    }
}

sealed class W0fComboProbe : Component
{
    public Signal<int>? Sel;
    public Signal<string>? Txt;
    public bool HandleSubmit;
    public readonly List<string> Submitted = new();
    public override Element Render()
    {
        var sel = UseSignal(-1); Sel = sel;
        var txt = UseSignal(""); Txt = txt;
        return Embed.Comp(() => new OverlayHost
        {
            Child = ComboBox.Create(new[] { "Red", "Green", "Blue" }, sel, editable: true, text: txt, width: 200f,
                placeholder: "pick", onTextSubmitted: s => { Submitted.Add(s); return HandleSubmit; }),
        });
    }
}

sealed class W0fStaticProbe : Component
{
    public required Func<Element> Build;
    public override Element Render() => Build();
}

// The NavPill resting-opacity shape (anim.rest.pill): a fade transition owns Opacity while animating, but a settled
// track frees WITHOUT resetting the channel — so the element MUST declare the state-dependent static at the same
// terminal, or any unrelated re-render snaps the hidden node back to the default 1f.
sealed class PillRestProbe : Component
{
    public required FluentGpu.Signals.Signal<bool> Visible;
    public required FluentGpu.Signals.Signal<int> Unrelated;
    public override Element Render()
    {
        bool visible = Visible.Value;
        _ = Unrelated.Value;                       // unrelated re-render trigger
        UseTransition(AnimChannel.Opacity, visible ? 0f : 1f, visible ? 1f : 0f, 150f, Easing.EaseOut, visible);
        return new BoxEl { Width = 3f, Height = 16f, Fill = Tok.AccentDefault, Opacity = visible ? 1f : 0f };
    }
}

// Spring-retarget probe (check 23s): the gallery spring-lab path — a component effect re-seeds a spring on its own
// captured node when state flips; a mid-flight retarget must keep position+velocity (no snap back to an endpoint).
sealed class SpringLabProbe : Component
{
    public NodeHandle Dot;

    public override Element Render()
    {
        var (on, setOn) = UseState(false);
        var armed = UseRef(false);
        UseLayoutEffect(() =>
        {
            if (Context.Anim is not { } anim) return;
            if (!armed.Value) { armed.Value = true; return; }
            if (!Dot.IsNull) anim.Spring(Dot, AnimChannel.TranslateX, on ? 210f : 0f, SpringParams.FromResponse(0.45f, 0.8f));
        }, on);
        return new BoxEl
        {
            Direction = 1,
            Children =
            [
                new BoxEl { Width = 60f, Height = 24f, OnClick = () => setOn(!on) },
                new BoxEl { Width = 226f, Height = 16f, Direction = 0, Children = [new BoxEl { Width = 16f, Height = 16f, OnRealized = h => Dot = h }] },
            ],
        };
    }
}

// Relayout-restore probe (check 23t): a width-toggled card with an AUTO height and wrapping text — after the
// SizeMode.Relayout animation settles, the declared LayoutInput must be RESTORED (auto height stays auto), not left
// frozen at the last interpolated solve.
sealed class RelayoutRestoreProbe : Component
{
    public override Element Render()
    {
        var (wide, setWide) = UseState(false);
        return new BoxEl
        {
            Direction = 1, AlignItems = FlexAlign.Start,
            Children =
            [
                new BoxEl { Width = 60f, Height = 24f, OnClick = () => setWide(!wide) },
                new BoxEl
                {
                    Width = wide ? 300f : 160f,
                    Animate = LayoutTransition.BoundsT(SizeMode.Relayout),
                    Children = [new TextEl("the quick brown fox jumps over the lazy dog again and again") { Size = 13f, Wrap = TextWrap.Wrap }],
                },
            ],
        };
    }
}

// SizeMode.Reflow probe (checks 23r/23x): a reflow wrapper above a row carrying a BoundsAnimated mover. `toggle`
// opens/closes the reveal; `noise` re-commits the SAME elements mid-flight (exercises the snap/skip/re-establish
// path); `shift` grows the leading spacer (a genuine LOCAL move that must still FLIP the mover).
sealed class ReflowProbe : Component
{
    static readonly LayoutTransition Reflow = new(TransitionChannels.Size,
        TransitionDynamics.Tween(333f, Easing.FluentPopOpen),
        Size: SizeMode.Reflow,
        ExitDynamics: TransitionDynamics.Tween(167f, EasingSpec.CubicBezier(1f, 1f, 0f, 1f)),
        Anchor: SizeAnchor.Trailing);
    static readonly LayoutTransition Slide = new(TransitionChannels.Position,
        TransitionDynamics.Tween(167f, Easing.FluentPopOpen));

    public override Element Render()
    {
        var (open, setOpen) = UseState(false);
        var (noise, setNoise) = UseState(0);
        var (shifted, setShifted) = UseState(false);
        return new BoxEl
        {
            Direction = 1,
            Children =
            [
                new BoxEl { Height = 32f, OnClick = () => setOpen(!open) },        // [0] toggle the reveal
                new BoxEl { Height = 16f, OnClick = () => setNoise(noise + 1) },   // [1] unrelated re-commit
                new BoxEl { Height = 16f, OnClick = () => setShifted(!shifted) },  // [2] local spacer move
                new BoxEl                                                          // [3] the reflow wrapper
                {
                    Direction = 1, ClipToBounds = true,
                    Height = open ? float.NaN : 0f,
                    Animate = Reflow,
                    Children = [new BoxEl { Height = 60f, Children = [new TextEl("reflow-content") { Size = 12f }] }],
                },
                new BoxEl                                                          // [4] sibling row below
                {
                    Direction = 0,
                    Children =
                    [
                        new BoxEl { Width = shifted ? 40f : 0f, Height = 30f },
                        new BoxEl { Width = 30f, Height = 30f, Animate = Slide },  // the rigidity probe
                    ],
                },
            ],
        };
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

// E4 — ToolTip timing probe: a plain (non-interactive) target wrapped by ToolTip inside an OverlayHost; the ToolTip
// wrapper itself carries the hover/press handlers, so the pointer hits IT (the inner target has no handlers).
sealed class E4ToolTipProbe : Component
{
    public override Element Render() => Embed.Comp(() => new OverlayHost
    {
        Child = new BoxEl
        {
            Width = 480, Height = 360, Padding = Edges4.All(40),
            Children =
            [
                ToolTip.Wrap(new BoxEl { Width = 120, Height = 32, Fill = ColorF.FromRgba(40, 40, 40) }, "tip-body"),
            ],
        },
    });
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
    public bool ReadOnly;
    public float Initial = 0f;
    public override Element Render()
    {
        var v = UseFloatSignal(Initial);
        Val = v;
        return RatingControl.Create(v, readOnly: ReadOnly);
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

sealed class AutoSuggestProbe : Component
{
    public Signal<string>? Query;
    public override Element Render()
    {
        var query = UseSignal("ca");
        Query = query;
        return Embed.Comp(() => new OverlayHost
        {
            Child = AutoSuggestBox.Create(
                new[] { "Cascadia Code", "Calendar", "Calculator", "Camera", "Canvas" },
                "Search",
                260f,
                query,
                debounceMs: 0f),
        });
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

// ── Wave-1 control-parity probes (w1controls.*) ─────────────────────────────────────────────
// A standard Button stretched wider than its label (content-alignment + no-scale + focus-margin assertions), behind a
// leading dummy focusable so the Tab order is deterministic (dummy → button).
sealed class W1ButtonProbe : Component
{
    public int Clicks;
    public override Element Render() => new BoxEl
    {
        Direction = 1, Gap = 12, Padding = Edges4.All(20),
        Children =
        [
            new BoxEl { Width = 40, Height = 20, OnClick = () => { } },
            Button.Standard("w1-btn", () => Clicks++) with { Width = 200f },
        ],
    };
}

// A ToggleButton over a bool signal: the signal flip (not the pointer) drives the checked-state BrushTransition, so the
// 83ms cross-fade is sampled without hover/press-ramp pollution.
sealed class W1ToggleButtonProbe : Component
{
    public Signal<bool>? On;
    public override Element Render()
    {
        var on = UseSignal(false);
        On = on;
        return new BoxEl
        {
            Padding = Edges4.All(20),
            Children = [ToggleButton.Create("w1-tb", on.Value, () => on.Value = !on.Value)],
        };
    }
}

// A HyperlinkButton with a NavigateUri: records how many URIs had already launched when Click fired — WinUI raises
// Click FIRST, then Launcher::TryInvokeLauncher (HyperLinkButton_Partial.cpp:149-177).
sealed class W1HyperlinkProbe : Component
{
    public HeadlessPlatformApp? App;
    public int UrisAtClick = -1;
    public override Element Render() => new BoxEl
    {
        Padding = Edges4.All(20),
        Children = [HyperlinkButton.Create("w1-link", "https://wavee.app/w1", onClick: () => UrisAtClick = App!.OpenedUris.Count)],
    };
}

// The RadioButtons container: 5 string items in 2 columns + header (column-major grid + roving-keyboard assertions).
sealed class W1RadioButtonsProbe : Component
{
    public int Selected;
    public int SelectCalls;
    public override Element Render()
    {
        var (sel, setSel) = UseState(0);
        Selected = sel;
        return new BoxEl
        {
            Padding = Edges4.All(10),
            Children =
            [
                RadioButtons.Create(new[] { "A", "B", "C", "D", "E" }, sel,
                    i => { SelectCalls++; setSel(i); }, header: "w1-group", maxColumns: 2),
            ],
        };
    }
}

// Slider.Ranged over 0..200 with a header — exercises the AUTO step sizes (SmallChange 0 → range/100 = 2,
// LargeChange 0 → range/10 = 20; WinUI's absolute defaults 1/10 on its 0–100 range, Slider_Partial.h:13-15).
sealed class W1SliderKeysProbe : Component
{
    public float Val;
    public override Element Render()
    {
        var (v, setV) = UseState(0f);
        Val = v;
        return new BoxEl
        {
            Padding = Edges4.All(20),
            Children = [Slider.Ranged(v, setV, new Slider.Options { Min = 0f, Max = 200f, Header = "w1-vol" }, length: 200f, thickness: 32f)],
        };
    }
}

// Slider.Ranged inside an OverlayHost (the thumb value tooltip needs a real overlay service) + inline ticks; a leading
// dummy focusable pins the Tab order. The probe never re-renders — the tooltip readout is the live tipValue signal.
sealed class W1SliderTipProbe : Component
{
    public float Val = -1f;
    public override Element Render() => Embed.Comp(() => new OverlayHost
    {
        Child = new BoxEl
        {
            Direction = 1, Gap = 12, Padding = Edges4.All(20),
            Children =
            [
                new BoxEl { Width = 40, Height = 20, OnClick = () => { } },
                Slider.Ranged(0f, v => Val = v, new Slider.Options { Min = 0f, Max = 200f, TickFrequency = 50f }, length: 200f, thickness: 32f),
            ],
        },
    });
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

sealed class SplitButtonLongMenuProbe : Component
{
    public override Element Render() => Embed.Comp(() => new OverlayHost
    {
        Child = Embed.Comp(() => new SplitButton
        {
            Label = "Paste",
            Glyph = Icons.Document,
            OnInvoke = () => { },
            Items =
            [
                new MenuFlyoutItem("Paste as text", Icons.Document),
                new MenuFlyoutItem("Paste special", Icons.Document),
            ],
        }),
    });
}

sealed class ContentDialogProbe : Component
{
    public override Element Render() => Embed.Comp(() => new OverlayHost
    {
        Child = Embed.Comp(() => new ContentDialog
        {
            TriggerLabel = "Show dialog",
            Title = "Save your work?",
            Message = "Lorem ipsum dolor sit amet, adipisicing elit.",
            PrimaryText = "Save",
            SecondaryText = "Don't Save",
            CloseText = "Cancel",
            DefaultButton = ContentDialog.DefaultBtn.Primary,
            OpenOnMount = true,
        }),
    });
}

sealed class TeachingTipProbe : Component
{
    public override Element Render() => Embed.Comp(() => new OverlayHost
    {
        Child = new BoxEl
        {
            Direction = 1,
            Width = 520,
            Padding = new Edges4(120, 48, 0, 0),
            Children =
            [
                Embed.Comp(() => new TeachingTip
                {
                    TriggerLabel = "Show teaching tip",
                    Title = "Save your work",
                    Body = "Click the disk icon, or press Ctrl+S, to save your changes.",
                    OpenOnMount = true,
                }),
            ],
        },
    });
}

sealed class NavHierarchyProbe : Component
{
    public override Element Render() => Embed.Comp(() => new NavigationView
    {
        Initial = "home",
        Items =
        [
            new NavItem("home", "H", "Home"),
            new NavItem("h", "", "Header", IsHeader: true),
            new NavItem("group", "G", "Group")
            {
                Children = [new NavItem("c1", "1", "ChildOne"), new NavItem("c2", "2", "ChildTwo")],
            },
            new NavItem("after", "A", "After"),
        ],
        Content = key => new BoxEl { Children = [Ui.Text("PAGE:" + key)] },
    });
}

// E5 — a draggable row for the drag-frame alloc tripwire: the delta handler copies one scalar (alloc-free), so a
// steady pointer-rate drag frame must be 0-alloc on phases 6–13 (transform-only repaint of the lifted visual).
sealed class DragFrameProbe : Component
{
    public float LastTotalDx;
    public override Element Render() => new BoxEl
    {
        Direction = 1, Gap = 8, Padding = Edges4.All(12),
        Children =
        [
            new BoxEl
            {
                Key = "drag", Width = 160, Height = 40, Fill = ColorF.FromRgba(0x40, 0x40, 0x40),
                CanDrag = true, OnDragDelta = e => LastTotalDx = e.TotalDx,
            },
            new BoxEl { Key = "rest", Width = 160, Height = 40, Fill = ColorF.FromRgba(0x30, 0x30, 0x30) },
        ],
    };
}

// ── The harness: run the slice end-to-end on the headless backends + assert ───────
sealed class PipsPagerOutputProbe : Component
{
    public override Element Render()
    {
        var selected = UseSignal(0);
        return new BoxEl
        {
            Direction = 1,
            Children =
            [
                PipsPager.Create(5, selected.Value, i => selected.Value = i),
                new TextEl("") { Size = 14f, Color = Tok.TextPrimary, Text = Prop.Of(() => $"Page {selected.Value + 1} / 5") },
            ],
        };
    }
}

// ── E11 virtualization-substrate probes (measured seam / repeater lifecycle / ItemsView L3) ──────────

// E11-L0 — a variable-extent list through the USER-REACHABLE IMeasuredVirtualLayout seam: rows realize at the
// 40px estimate and correct to H(i) at arrange (SetMeasured); scrolls must anchor across corrections.
sealed class MeasuredSeamProbe : Component
{
    public const int N = 300;
    public const float Estimate = 40f;
    public static float H(int i) => 40f + (i % 4) * 14f;   // 40, 54, 68, 82 — mean 61 ≠ the 40 estimate (≥ estimate, so
                                                           // a fresh correction never shrinks the anchor row's band)
    public MeasuredStackVirtualLayout? Layout;
    public override Element Render()
    {
        var layout = UseMemo(static () => new MeasuredStackVirtualLayout(Estimate));
        Layout = layout;
        return Virtual.Measured(N, layout,
                   renderItem: i => new BoxEl { Height = H(i), Fill = ColorF.FromRgba(30, 30, 30) },
                   keyOf: i => "m" + i)
               with { Width = 300, Height = 300 };
    }
}

// E11-L2 — ItemsRepeater lifecycle (ElementPrepared/ElementClearing/visible-range) recorded across a scroll recycle.
sealed class LifecycleRepeaterProbe : Component
{
    public const int N = 1000;
    public readonly List<int> Prepared = new();
    public readonly List<int> Cleared = new();
    public readonly List<(int First, int Last)> Ranges = new();
    public override Element Render()
        => ((VirtualListEl)Repeater.ItemsRepeater(N, i => new BoxEl { Height = 40f, Fill = ColorF.FromRgba(28, 28, 28) },
                RepeatLayout.Stack(40f), keyOf: i => "lc" + i,
                elementPrepared: Prepared.Add, elementClearing: Cleared.Add,
                visibleRange: (f, l) => Ranges.Add((f, l))))
           with { Width = 300, Height = 400 };   // explicit size ⇒ the MOUNT realize windows against 400, not the hint
}

// E11-L3 — ItemsView keyboard surface (Single over a virtualized stack): arrows/Home/End/PageUp-Down, typeahead,
// StartBringItemIntoView, the CanRaiseItemInvoked matrix (ItemsView.cpp:423-426).
sealed class ItemsViewKeyboardProbe : Component
{
    public const int N = 100;
    public const float Row = 40f;
    public readonly ItemsViewController Controller = new();
    public int InvokedCount;
    public int LastInvoked = -1;
    public static string NameOf(int i) => i == 57 ? "zebra" : $"item {i:000}";
    public override Element Render()
        => new BoxEl
        {
            Width = 360, Height = 320,
            Children =
            [
                ItemsView.Create(N,
                    itemTemplate: i => new BoxEl { Children = [new TextEl(NameOf(i)) { Size = 12f }] },
                    layout: RepeatLayout.Stack(Row),
                    isItemInvokedEnabled: true,
                    itemInvoked: i => { InvokedCount++; LastInvoked = i; },
                    itemText: NameOf,
                    controller: Controller),
            ],
        };
}

// E11-L3 — grid arrow navigation: Left/Right = index ±1, Up/Down = ±columns (the index-based layout-orientation
// path, ItemsViewInteractions.cpp:1051-1067).
sealed class ItemsViewGridProbe : Component
{
    public const int N = 40;
    public readonly ItemsViewController Controller = new();
    public override Element Render()
        => new BoxEl
        {
            Width = 360, Height = 320,
            Children = [ItemsView.Create(N, i => new BoxEl(), RepeatLayout.Grid(4, 72f, 8f), controller: Controller)],
        };
}

// E11-L3 — Extended-mode pointer chords (plain / Shift / Ctrl) + Shift+arrow + Ctrl+A (ExtendedSelector.cpp).
sealed class ItemsViewExtendedProbe : Component
{
    public const int N = 60;
    public readonly ItemsViewController Controller = new();
    public int SelectionChangedCount;
    public override Element Render()
        => new BoxEl
        {
            Width = 360, Height = 320,
            Children =
            [
                ItemsView.Create(N, i => new BoxEl(), RepeatLayout.Stack(40f),
                    selectionMode: ItemsSelectionMode.Extended,
                    selectionChanged: () => SelectionChangedCount++,
                    controller: Controller),
            ],
        };
}

// E11-L3 — Multiple mode over 10k items: select-all must store ONE range and realize nothing (window-only re-skin).
sealed class ItemsViewMultipleProbe : Component
{
    public const int N = 10_000;
    public readonly ItemsViewController Controller = new();
    public int TemplateCalls;
    public override Element Render()
        => new BoxEl
        {
            Width = 360, Height = 320,
            Children =
            [
                ItemsView.Create(N, i => { TemplateCalls++; return new BoxEl(); }, RepeatLayout.Stack(40f),
                    selectionMode: ItemsSelectionMode.Multiple, controller: Controller),
            ],
        };
}

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

    static ColorF GlyphColor(HeadlessGpuDevice dev, StringTable strings, string text)
    {
        foreach (var g in dev.LastGlyphs)
            if (strings.Resolve(g.Text) == text) return g.Color;
        return default;
    }

    static int CountGlyph(HeadlessGpuDevice dev, StringTable strings, string text)
    {
        int n = 0;
        foreach (var g in dev.LastGlyphs)
            if (strings.Resolve(g.Text) == text) n++;
        return n;
    }

    static ColorF FirstGradientC0(HeadlessGpuDevice dev) => dev.LastGradients.Count > 0 ? dev.LastGradients[0].C0 : default;

    // Full-ARGB color comparison (WinUI foreground state changes are often ALPHA-only, so RGB-only checks are too weak).
    static bool ColorClose(ColorF a, ColorF b, float tol)
        => MathF.Abs(a.R - b.R) < tol && MathF.Abs(a.G - b.G) < tol && MathF.Abs(a.B - b.B) < tol && MathF.Abs(a.A - b.A) < tol;

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

    // The node currently carrying the keyboard-focus flag (the dispatcher sets NodeFlags.Focused on it).
    static NodeHandle FocusedNode(SceneStore s, NodeHandle n)
    {
        if (n.IsNull) return NodeHandle.Null;
        if ((s.Flags(n) & NodeFlags.Focused) != 0) return n;
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
        {
            var r = FocusedNode(s, c);
            if (!r.IsNull) return r;
        }
        return NodeHandle.Null;
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

    static NodeHandle FindTextNode(SceneStore s, StringTable strings, NodeHandle n, string text)
    {
        if (n.IsNull) return NodeHandle.Null;
        ref var p = ref s.Paint(n);
        if (p.VisualKind == VisualKind.Text && strings.Resolve(p.Text) == text) return n;
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
        {
            var r = FindTextNode(s, strings, c, text);
            if (!r.IsNull) return r;
        }
        return NodeHandle.Null;
    }

    // D67 menu chrome: the acrylic lives on the stretch PLATE (first child of the channel-carrying surface).
    static bool ChildHasAcrylic(SceneStore s, NodeHandle n)
    {
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c)) if (s.TryGetAcrylic(c, out _)) return true;
        return false;
    }

    static NodeHandle FindPolylineStrokeNode(SceneStore s, NodeHandle n)
    {
        if (n.IsNull) return NodeHandle.Null;
        if (s.Paint(n).VisualKind == VisualKind.PolylineStroke) return n;
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
        {
            var r = FindPolylineStrokeNode(s, c);
            if (!r.IsNull) return r;
        }
        return NodeHandle.Null;
    }

    static int DrawPayloadSize(DrawOp op) => op switch
    {
        DrawOp.FillRoundRect => Unsafe.SizeOf<FillRoundRectCmd>(),
        DrawOp.DrawGlyphRun => Unsafe.SizeOf<DrawGlyphRunCmd>(),
        DrawOp.PushClip => Unsafe.SizeOf<ClipCmd>(),
        DrawOp.PopClip => 0,
        DrawOp.DrawImage => Unsafe.SizeOf<DrawImageCmd>(),
        DrawOp.DrawRoundRectStroke => Unsafe.SizeOf<DrawRoundRectStrokeCmd>(),
        DrawOp.DrawShadow => Unsafe.SizeOf<DrawShadowCmd>(),
        DrawOp.DrawGradientRect => Unsafe.SizeOf<DrawGradientRectCmd>(),
        DrawOp.PushLayer => Unsafe.SizeOf<PushLayerCmd>(),
        DrawOp.PopLayer => Unsafe.SizeOf<PopLayerCmd>(),
        DrawOp.DrawGradientStroke => Unsafe.SizeOf<DrawGradientStrokeCmd>(),
        DrawOp.DrawArc => Unsafe.SizeOf<DrawArcCmd>(),
        DrawOp.DrawPolylineStroke => Unsafe.SizeOf<DrawPolylineStrokeCmd>(),
        DrawOp.DrawTabShape => Unsafe.SizeOf<DrawTabShapeCmd>(),
        _ => 0,
    };

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
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.KeyUp, default, 0, Keys.Enter) });   // WinUI: click on key-UP
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
        engine.Tick(0f);
        bool startOk = MathF.Abs(scene.Paint(node).Opacity) < 0.001f;
        engine.Tick(50f);
        float op = scene.Paint(node).Opacity;
        var fl = scene.Flags(node);
        bool midOk = MathF.Abs(op - 0.5f) < 0.02f && (fl & NodeFlags.PaintDirty) != 0 && (fl & NodeFlags.LayoutDirty) == 0;
        engine.Tick(60f);   // 110ms > 100ms → complete
        bool doneOk = MathF.Abs(scene.Paint(node).Opacity - 1f) < 0.001f && !engine.HasActive;
        Check("22. opacity timeline samples t0, eases & completes (no relayout)", startOk && midOk && doneOk, $"@50ms={op:0.00}");

        scene.Flags(node) &= ~(NodeFlags.TransformDirty | NodeFlags.LayoutDirty);
        engine.Animate(node, AnimChannel.TranslateX, 0f, 100f, 100f, Easing.Linear);
        engine.Tick(0f);
        engine.Tick(25f);
        float dx = scene.Paint(node).LocalTransform.Dx;
        var fl2 = scene.Flags(node);
        bool transOk = MathF.Abs(dx - 25f) < 0.5f && (fl2 & NodeFlags.TransformDirty) != 0 && (fl2 & NodeFlags.LayoutDirty) == 0;
        Check("23. translate timeline marks TransformDirty only", transOk, $"@25ms dx={dx:0.0}");

        var modal = scene.CreateNode(1);
        ref NodePaint mp = ref scene.Paint(modal);
        mp.Opacity = 1f;
        mp.LocalTransform = Affine2D.Identity;
        engine.Animate(modal, AnimChannel.ScaleX, 1f, 1.05f, 167f, Easing.FluentPopOpen);
        engine.Animate(modal, AnimChannel.ScaleY, 1f, 1.05f, 167f, Easing.FluentPopOpen);
        engine.Animate(modal, AnimChannel.Opacity, 1f, 0f, 83f, Easing.Linear);
        engine.Tick(0f);
        for (int i = 0; i < 6; i++) engine.Tick(16f);  // 96ms: opacity settled/removed, scale still active
        float faded = scene.Paint(modal).Opacity;
        bool scaleStillActive = engine.HasTracks(modal);
        engine.Tick(16f);                              // previous bug: remaining scale tracks reset Opacity to 1 here
        float held = scene.Paint(modal).Opacity;
        Check("23z. multi-channel animation preserves completed channels while longer tracks continue",
            faded < 0.01f && held < 0.01f && scaleStillActive,
            $"opacity {faded:0.00}->{held:0.00}, active={scaleStillActive}");
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

        // (The CheckBox checkmark draw-on is a component reveal hook → it needs the host's layout-effect drain, so it is
        //  exercised through the real AppHost in check 66b, not the bare reconciler here.)

        // 23h — WinUI RadioButton motion: CheckGlyph is 12px at rest, 14px on PointerOver, 10px on Pressed; unchecked
        // Pressed uses a separate PressedCheckGlyph that appears while held and grows from 4px toward 10px.
        {
            var scene = new SceneStore();
            var recon = new TreeReconciler(scene, strings);
            recon.ReconcileRoot(RadioButton.Create("x", true, () => { }), null);   // root = row; ring = child0; dot = ring.child0
            new FlexLayout(scene, new HeadlessFontSystem(strings)).Run(scene.Root);
            var ring = Child(scene, scene.Root, 0);
            var dot = Child(scene, ring, 0);
            bool sized = Near(scene.Bounds(dot).W, 12f, 0.01f) && Near(scene.Bounds(dot).H, 12f, 0.01f);
            bool interactive = scene.TryGetInteract(dot, out var ia)
                && Near(ia.HoverScale, 14f / 12f, 0.001f)
                && Near(ia.PressScale, 10f / 12f, 0.001f)
                && Near(ia.HoverDurationMs, 250f, 0.01f)
                && Near(ia.PressDurationMs, 250f, 0.01f);
            bool instantChecked = scene.Paint(dot).LocalTransform.IsIdentity;
            Check("23h. RadioButton wires WinUI CheckGlyph size states (12 rest, 14 hover, 10 pressed)",
                sized && interactive && instantChecked,
                $"size={scene.Bounds(dot).W:0.#} hoverScale={ia.HoverScale:0.###} pressScale={ia.PressScale:0.###}");

            var unselected = new SceneStore();
            var unselectedRecon = new TreeReconciler(unselected, strings);
            unselectedRecon.ReconcileRoot(RadioButton.Create("x", false, () => { }), null);
            new FlexLayout(unselected, new HeadlessFontSystem(strings)).Run(unselected.Root);
            var unselectedRing = Child(unselected, unselected.Root, 0);
            var pressedGlyph = Child(unselected, unselectedRing, 0);
            bool hiddenAtRest = Near(unselected.Bounds(pressedGlyph).W, 4f, 0.01f)
                && Near(unselected.Paint(pressedGlyph).Opacity, 0f, 0.001f)
                && Near(unselected.Paint(pressedGlyph).PressedOpacity, 1f, 0.001f);
            bool growsToPressedSize = unselected.TryGetInteract(pressedGlyph, out var pia)
                && Near(pia.PressScale, 10f / 4f, 0.001f)
                && Near(pia.PressDurationMs, 167f, 0.01f);

            var iax = new InteractionAnimator(unselected);
            iax.SetPress(unselected.Root, true);
            iax.Tick(16f);
            var dl = new DrawList();
            SceneRecorder.Record(unselected, dl);
            var dev = new HeadlessGpuDevice();
            dev.SubmitDrawList(dl.Bytes, dl.SortKeys, new FrameInfo(new Size2(120, 80), 1f, ColorF.Transparent));
            bool drewPressedGlyph = false;
            foreach (var rect in dev.LastRects)
                if (Near(rect.Rect.W, 4f, 0.01f) && rect.Opacity > 0.08f && rect.Transform.M11 > 1.08f)
                    drewPressedGlyph = true;
            Check("23h2. RadioButton unchecked press draws PressedCheckGlyph (4px hidden → visible/growing toward 10px)",
                hiddenAtRest && growsToPressedSize && drewPressedGlyph,
                $"rest={hiddenAtRest} scale={pia.PressScale:0.###} drew={drewPressedGlyph}");
        }

        // 23i — visual-state RAMP wiring (the StateBrush model, not a 12-state matrix): an unchecked CheckBox wires the
        // full interaction ladder into the box's scene columns. Crucially the PRESSED stroke DIMS to
        // ControlStrongStrokeColorDisabled (the exact WinUI press feedback) — provable here without pixels, the empirical
        // counterpart to a screenshot. The recorder eases BorderColor→PressedBorderColor on PressT (covered by check 58).
        {
            var scene = new SceneStore();
            var recon = new TreeReconciler(scene, strings);
            recon.ReconcileRoot(CheckBox.Create("x", CheckState.Unchecked, _ => { }), null);
            ref var p = ref scene.Paint(Child(scene, scene.Root, 0));   // the 20px box
            bool restRing = MathF.Abs(p.BorderColor.A - Tok.StrokeControlStrongDefault.A) < 0.02f;
            bool pressDims = MathF.Abs(p.PressedBorderColor.A - Tok.StrokeControlStrongDisabled.A) < 0.02f && p.PressedBorderColor.A < p.BorderColor.A;
            bool hoverFill = MathF.Abs(p.HoverFill.A - Tok.FillControlAltTertiary.A) < 0.02f;
            bool pressFill = MathF.Abs(p.PressedFill.A - Tok.FillControlAltQuaternary.A) < 0.02f;
            Check("23i. CheckBox wires the interaction ramp (pressed stroke dims to StrongDisabled, no 12-state matrix)",
                restRing && pressDims && hoverFill && pressFill,
                $"ring.A={p.BorderColor.A:0.00}→press {p.PressedBorderColor.A:0.00}; fill hover.A={p.HoverFill.A:0.00} press.A={p.PressedFill.A:0.00}");
        }

    }

    // Smallest in-flight presented width anywhere in a subtree (a DrawnCheckmark/DrawnDash reveal), else NaN. The
    // checkmark "draws itself" by sweeping its clip box's presented width 0→full — this lets a check assert the draw-on
    // empirically (mid-reveal: 0 &lt; w &lt; full; settled: NaN, since AnimEngine resets a finished reveal to NaN).
    static float RevealingW(SceneStore s, NodeHandle n)
    {
        float best = float.NaN;
        float w = s.Paint(n).PresentedW;
        if (!float.IsNaN(w)) best = w;
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
        {
            float cw = RevealingW(s, c);
            if (!float.IsNaN(cw) && (float.IsNaN(best) || cw < best)) best = cw;
        }
        return best;
    }

    // The opt-in size modes: ScaleCorrect (GPU scale → 1, compositor-only) and Relayout (re-solve the subtree at the
    // interpolated size each tick → live text reflow), both via the same general AnimateBounds entry point.
    // Smallest active stroke-trim end in a subtree (DrawnCheckmark/DrawnDash), else NaN. A freshly seeded draw-on starts
    // at 0, advances between 0 and 1 while active, then AnimEngine resets the override to NaN when the track settles.
    static float ActiveStrokeTrimEnd(SceneStore s, NodeHandle n)
    {
        float best = float.NaN;
        float t = s.Paint(n).StrokeTrimEnd;
        if (!float.IsNaN(t)) best = t;
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
        {
            float ct = ActiveStrokeTrimEnd(s, c);
            if (!float.IsNaN(ct) && (float.IsNaN(best) || ct < best)) best = ct;
        }
        return best;
    }

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

    // Animation regressions surfaced by the gallery AnimationPage: a component-effect spring must retarget mid-flight
    // without snapping (23s), and a settled SizeMode.Relayout animation must RESTORE the element-declared LayoutInput
    // instead of freezing the node at the last interpolated solve (23t — the auto-height axis is where it shows).
    static void AnimRegressionChecks(StringTable strings)
    {
        // 23s — spring retarget continuity through the live AppHost path (UseLayoutEffect → Context.Anim.Spring).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("springlab", new Size2(320, 120), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new SpringLabProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var toggle = Child(host.Scene, host.Scene.Root, 0);

            ClickNode(host, window, toggle);                       // 0 → 210
            for (int i = 0; i < 8; i++) host.RunFrame();           // mid-flight
            float before = host.Scene.Paint(root.Dot).LocalTransform.Dx;
            ClickNode(host, window, toggle);                       // retarget mid-flight → 0
            float atClick = host.Scene.Paint(root.Dot).LocalTransform.Dx;
            host.RunFrame();
            float after = host.Scene.Paint(root.Dot).LocalTransform.Dx;
            bool continuous = MathF.Abs(atClick - before) < 25f && MathF.Abs(after - atClick) < 25f;   // no snap to an endpoint
            bool carried = after > 5f;                             // velocity carry: still well away from 0 right after
            for (int i = 0; i < 90; i++) host.RunFrame();
            bool settled = MathF.Abs(host.Scene.Paint(root.Dot).LocalTransform.Dx) < 0.5f && !host.Animation.HasTracks(root.Dot);
            Check("23s. spring retarget mid-flight keeps position+velocity through the component-effect path (no snap)",
                before > 30f && continuous && carried && settled,
                $"before={before:0.0} atClick={atClick:0.0} after={after:0.0} settled={settled}");
        }

        // 23w — alpha-weighted (premultiplied) linear-light lerp: a translucent white-tinted card fill cross-fading
        // to an OPAQUE DARK solid must stay dark mid-flight. The straight per-channel lerp passed through bright
        // half-transparent grey (~0.74 sRGB) — the sticky-header "white flash". Same-alpha pairs are bit-identical
        // to the straight linear-light lerp (every pre-existing mid-color assertion stays valid).
        {
            static float S2L(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
            static float L2S(float c) => c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(MathF.Max(c, 0f), 1f / 2.4f) - 0.055f;
            var cardWhite5 = new ColorF(1f, 1f, 1f, 0.051f);     // CardBackgroundFillColorDefault (dark theme): white @ 5%
            var solidDark = ColorF.FromRgba(0x20, 0x20, 0x20);   // SolidBackgroundFillColorBase: opaque dark
            var mid = ColorF.LerpLinear(cardWhite5, solidDark, 0.5f);
            bool staysDark = mid.R < 0.35f && mid.G < 0.35f && mid.B < 0.35f && Near(mid.A, 0.5255f, 0.01f);
            var sa = new ColorF(0.2f, 0.4f, 0.6f, 0.8f);
            var sb = new ColorF(0.6f, 0.2f, 0.4f, 0.8f);
            var sm = ColorF.LerpLinear(sa, sb, 0.5f);
            bool sameAlphaIdentical =
                Near(sm.R, L2S((S2L(sa.R) + S2L(sb.R)) * 0.5f), 0.002f) &&
                Near(sm.G, L2S((S2L(sa.G) + S2L(sb.G)) * 0.5f), 0.002f) &&
                Near(sm.B, L2S((S2L(sa.B) + S2L(sb.B)) * 0.5f), 0.002f) && Near(sm.A, 0.8f, 0.002f);
            Check("23w. LerpLinear is alpha-weighted: translucent-white → opaque-dark stays dark mid-flight; same-alpha pairs unchanged",
                staysDark && sameAlphaIdentical,
                $"mid=({mid.R:0.00},{mid.G:0.00},{mid.B:0.00},{mid.A:0.00}) sameAlpha={sameAlphaIdentical}");
        }

        // 23u — CSS position:sticky (BoxEl.StickyTop): the header scrolls normally, PINS at the viewport top while
        // its parent card is in view (hit-test follows — AbsoluteRect includes the pin transform), CLAMPS at the
        // card's end (never escapes its containing block), releases on scroll-back, and fires OnPinned per transition.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("sticky", new Size2(320, 200), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            int pinEvents = 0; bool lastPin = false;
            var root = new W0fStaticProbe
            {
                Build = () => ScrollView(new BoxEl
                {
                    Direction = 1,
                    Children =
                    [
                        new BoxEl { Height = 100f },                              // lead-in
                        new BoxEl                                                  // the card (containing block)
                        {
                            Direction = 1,
                            Children =
                            [
                                new BoxEl { Height = 40f, StickyTop = 0f, OnPinned = p => { pinEvents++; lastPin = p; } },
                                new BoxEl { Height = 400f },                       // card content
                            ],
                        },
                        new BoxEl { Height = 600f },                               // after the card
                    ],
                }),
            };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var s = host.Scene;
            NodeHandle FindScrollable(NodeHandle n)
            {
                if (n.IsNull) return NodeHandle.Null;
                if (s.HasScroll(n)) return n;
                for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
                {
                    var r = FindScrollable(c);
                    if (!r.IsNull) return r;
                }
                return NodeHandle.Null;
            }
            var vp = FindScrollable(s.Root);                       // the ScrollView viewport
            var content = s.ScrollRef(vp).ContentNode;
            var headerN = NodeHandle.Null;                         // the sticky node, straight from the registry
            foreach (var kv in s.StickyNodes) headerN = kv.Value.Node;
            float vpTop = s.AbsoluteRect(vp).Y;
            float restY = s.AbsoluteRect(headerN).Y;

            void ScrollTo(float y)
            {
                ref ScrollState st = ref s.ScrollRef(vp);
                st.OffsetY = y; st.TargetY = y;
                s.Paint(content).LocalTransform = Affine2D.Translation(0f, -y);
                s.Mark(content, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
                // Wake the frame loop like real input would (a wheel scroll sets frameNeeded via dispatch; a raw
                // ScrollRef write does not) — the sticky pass runs in the full frame pipeline.
                window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(8f, 8f), 0, 0));
                host.RunFrame();
            }

            ScrollTo(250f);   // header's natural Y (100) is far above the viewport top → pinned at the top
            bool pinnedNow = (s.Flags(headerN) & NodeFlags.StickyPinned) != 0;
            float pinnedY = s.AbsoluteRect(headerN).Y;
            // Card spans content 100..540; the header (40h) can pin until content-y 500 (shift limit 400). At offset
            // 520 the clamp holds it at content-y 500 → viewport −20: the card's end pushes it out, CSS-exactly.
            ScrollTo(520f);
            float clampedY = s.AbsoluteRect(headerN).Y;
            bool stillPinned = (s.Flags(headerN) & NodeFlags.StickyPinned) != 0;
            ScrollTo(0f);     // released, back at its natural slot
            bool releasedFlag = (s.Flags(headerN) & NodeFlags.StickyPinned) == 0;
            float releasedY = s.AbsoluteRect(headerN).Y;
            Check("23u. position:sticky — pins at viewport top, clamps at the card's end, releases, OnPinned fires per transition",
                pinnedNow && Near(pinnedY, vpTop, 0.5f)
                && stillPinned && Near(clampedY, vpTop - 20f, 0.5f)
                && releasedFlag && Near(releasedY, restY, 0.5f)
                && pinEvents == 2 && !lastPin,
                $"restY={restY:0} pinnedY={pinnedY:0} (vpTop={vpTop:0}) clampedY={clampedY:0} releasedY={releasedY:0} pinEvents={pinEvents} lastPin={lastPin}");
        }

        // 23t — SizeMode.Relayout restores the DECLARED LayoutInput at settle (auto height stays auto).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("relayoutfix", new Size2(360, 240), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new RelayoutRestoreProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var toggle = Child(host.Scene, host.Scene.Root, 0);
            var card = Child(host.Scene, host.Scene.Root, 1);
            float narrowH = host.Scene.AbsoluteRect(card).H;       // tall: text wraps hard at 160

            ClickNode(host, window, toggle);                       // widen 160 → 300
            for (int i = 0; i < 90; i++) host.RunFrame();          // settle the spring fully
            bool wideAuto = float.IsNaN(host.Scene.Layout(card).Height);          // declared auto RESTORED
            bool wideDeclaredW = host.Scene.Layout(card).Width == 300f;           // declared width restored too
            float wideH = host.Scene.AbsoluteRect(card).H;                        // fewer lines → shorter

            ClickNode(host, window, toggle);                       // back to narrow
            for (int i = 0; i < 90; i++) host.RunFrame();
            bool narrowAuto = float.IsNaN(host.Scene.Layout(card).Height);
            float narrowH2 = host.Scene.AbsoluteRect(card).H;                     // re-wraps back to the tall layout
            Check("23t. Relayout settle restores declared LayoutInput (auto axis stays auto; round-trip re-wraps)",
                wideAuto && wideDeclaredW && narrowAuto && wideH < narrowH - 4f && Near(narrowH2, narrowH, 1.5f),
                $"narrowH={narrowH:0} wideH={wideH:0} narrowH2={narrowH2:0} wideAuto={wideAuto} narrowAuto={narrowAuto}");
        }
    }

    // SizeMode.Reflow — the layout-participating size transition (smooth reflow): the interpolated size runs through
    // REAL layout each tick (boundary-scoped re-solve), so siblings ease instead of snapping; the declared LayoutInput
    // is restored at settle; the Trailing anchor rides the content's end edge on the animated edge; and the parent-
    // relative FLIP projection keeps BoundsAnimated nodes below RIGID while everything reflows.
    static void ReflowChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("reflow", new Size2(360, 420), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var root = new ReflowProbe();
        using var host = new AppHost(app, window, device, fonts, strings, root);
        var s = host.Scene;

        host.RunFrame();   // mount collapsed (first frame never captures → no spurious enter reveal)
        var toggle = Child(s, s.Root, 0);
        var noise = Child(s, s.Root, 1);
        var shift = Child(s, s.Root, 2);
        var wrap = Child(s, s.Root, 3);
        var row = Child(s, s.Root, 4);
        var mover = Child(s, row, 1);
        float sibY0 = s.AbsoluteRect(row).Y;          // the row below IS the observable sibling
        float wrapH0 = s.AbsoluteRect(wrap).H;

        // 23r.a — expand: old size on the click frame (no jump), sibling eases MONOTONICALLY through the 333ms reveal,
        // Trailing child-shift == interp − contentExtent mid-flight, settle restores the declared NaN(auto) input.
        ClickNode(host, window, toggle);
        float wrapHClick = s.AbsoluteRect(wrap).H;
        float sibYClick = s.AbsoluteRect(row).Y;
        bool monotone = true;
        float prevY = sibYClick, midH = 0f, midShift = 0f;
        for (int i = 0; i < 30; i++)
        {
            host.RunFrame();
            float y = s.AbsoluteRect(row).Y;
            if (y < prevY - 0.25f) monotone = false;
            prevY = y;
            if (i == 2) { midH = s.AbsoluteRect(wrap).H; midShift = s.Paint(wrap).ChildShiftY; }
        }
        float wrapHOpen = s.AbsoluteRect(wrap).H;
        float sibYOpen = s.AbsoluteRect(row).Y;
        bool liRestoredOpen = float.IsNaN(s.Layout(wrap).Height);
        bool shiftRest = s.Paint(wrap).ChildShiftY == 0f;
        Check("23r.a Reflow expand: old-size click frame, sibling eases monotonically, trailing shift rides, settle restores declared input",
            wrapH0 < 0.5f && wrapHClick < 0.5f && Near(sibYClick, sibY0, 0.5f) && monotone
            && midH > 4f && midH < 56f && Near(midShift, midH - 60f, 1.5f)
            && Near(wrapHOpen, 60f, 0.5f) && Near(sibYOpen, sibY0 + 60f, 0.5f) && liRestoredOpen && shiftRest,
            $"wrapH {wrapHClick:0.0}→{midH:0.0}→{wrapHOpen:0.0} sibY {sibY0:0.0}→{sibYClick:0.0}→{sibYOpen:0.0} shift={midShift:0.0} liNaN={liRestoredOpen}");

        // 23r.b — collapse with a mid-flight UNRELATED re-commit: the commit snap-solves the wrapper at its declared
        // value inside the frame, but the target/echo guards keep the in-flight track (no restart) and phase 7
        // re-establishes the interp before record — so the collapse still settles ON SCHEDULE (167ms + pad).
        ClickNode(host, window, toggle);                 // collapse — ExitDynamics leg
        host.RunFrame(); host.RunFrame();                // ~32ms in
        float hA = s.AbsoluteRect(wrap).H;
        ClickNode(host, window, noise);                  // unrelated state commit mid-flight
        float hB = s.AbsoluteRect(wrap).H;
        bool stillFlying = host.Animation.HasTracks(wrap);
        for (int i = 0; i < 11; i++) host.RunFrame();    // total ≈ 224ms ≥ 167ms — would NOT settle if the tween restarted
        bool closedOnSchedule = Near(s.AbsoluteRect(wrap).H, 0f, 0.5f) && !host.Animation.HasTracks(wrap);
        bool liRestoredClosed = s.Layout(wrap).Height == 0f;
        bool sibHome = Near(s.AbsoluteRect(row).Y, sibY0, 0.5f);
        Check("23r.b Reflow collapse: mid-flight reconcile does not restart the track (guards), settles on schedule, declared 0 restored",
            hA < 59.5f && hA > 0.5f && hB <= hA + 0.25f && stillFlying && closedOnSchedule && liRestoredClosed && sibHome,
            $"hA={hA:0.0} hB={hB:0.0} flying={stillFlying} closed={closedOnSchedule} li0={liRestoredClosed}");

        // 23x — rigidity: a BoundsAnimated node below the reflowing wrapper rides the reveal RIGIDLY (parent-relative
        // projection skips it on commit frames — exercised by clicking noise EVERY ride frame), then a genuine LOCAL
        // move (the leading spacer) still FLIPs it.
        ClickNode(host, window, toggle);                 // expand again
        bool rigid = true;
        float prevMovY = s.AbsoluteRect(mover).Y, prevRowY = s.AbsoluteRect(row).Y;
        float rideStartY = prevMovY;
        for (int i = 0; i < 5; i++)
        {
            ClickNode(host, window, noise);              // every ride frame is a COMMIT frame (capture+apply run)
            float my = s.AbsoluteRect(mover).Y, ry = s.AbsoluteRect(row).Y;
            if (!Near(my - prevMovY, ry - prevRowY, 0.25f)) rigid = false;
            if (host.Animation.HasTracks(mover)) rigid = false;
            if (MathF.Abs(s.Paint(mover).LocalTransform.Dy) > 0.01f) rigid = false;
            prevMovY = my; prevRowY = ry;
        }
        bool rode = prevMovY > rideStartY + 4f;          // it genuinely moved with the reveal
        for (int i = 0; i < 30; i++) host.RunFrame();    // settle the reveal
        float movX0 = s.AbsoluteRect(mover).X;
        ClickNode(host, window, shift);                  // spacer 0→40: a LOCAL move within the row
        bool seeded = host.Animation.HasTracks(mover);
        float dx0 = s.Paint(mover).LocalTransform.Dx;    // JustSeeded samples u=0 → −40 on the commit frame
        bool held = Near(s.AbsoluteRect(mover).X, movX0, 1.5f);   // presented X holds (FLIP "Invert")
        for (int i = 0; i < 30; i++) host.RunFrame();
        bool landed = Near(s.AbsoluteRect(mover).X, movX0 + 40f, 0.5f) && !host.Animation.HasTracks(mover);
        Check("23x. parent-relative projection: ancestor reflow rides rigidly (no tracks, no transform); a local move still FLIPs",
            rigid && rode && seeded && dx0 < -30f && held && landed,
            $"rigid={rigid} rode={rode} seeded={seeded} dx0={dx0:0.0} held={held} landed={landed}");
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
        bool styled = btn.Fill.Value == s.Background
            && btn.HoverFill == s.HoverBackground
            && Near(btn.Corners.TopLeft, 8f)
            && btn.Children[0] is TextEl t && t.Color.Value == s.Foreground;

        var modded = Button.Accent("y", () => { }).Background(ColorF.FromRgba(1, 2, 3)).Rounded(12f);
        bool overridden = modded.Fill.Value == ColorF.FromRgba(1, 2, 3) && Near(modded.Corners.TopLeft, 12f);

        // Wave-1 parity: WinUI Button storyboards swap brushes ONLY (Button_themeresources.xaml:176-229 — no scale),
        // so the default Button must have NO press scale; IconButton (engine media-transport control) keeps its
        // deliberate glyph pop, proving the scale CHANNEL still works for controls that opt in.
        var animatedButton = Button.Standard("z", () => { });
        var animatedIcon = IconButton.Create("i", () => { });
        bool animation = animatedButton.PressScale == 1f && animatedButton.HoverScale == 1f
            && animatedIcon.Children[0] is BoxEl iconGlyph
            && iconGlyph.HoverScale > 1f
            && iconGlyph.PressScale < 1f;

        Check("27. controls are user-styleable + animated (ButtonStyle, modifiers, AnimatedIcon)", styled && overridden && animation,
            "custom style + .Background().Rounded() + WinUI no-scale Button / opt-in icon scale");
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
        a1.Tick(0f);
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
        a2.Tick(0f);
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

    static void ScrollCrossAxisChecks(StringTable strings)
    {
        const string longLine =
            "This intentionally unwrapped caption is long enough to exceed the viewport width and must not widen the vertical scroll content or nested virtual grids.";

        var tree = Ui.ScrollView(new BoxEl
        {
            Direction = 1,
            Gap = 12f,
            Padding = Edges4.All(24f),
            Children =
            [
                new TextEl(longLine) { Size = 14f },
                new BoxEl
                {
                    Height = 260f,
                    Children =
                    [
                        Virtual.Grid(1000, columns: 4, itemHeight: 110f, gap: 12f,
                            renderItem: i => new BoxEl { Fill = ColorF.FromRgba(40, 40, 40) },
                            keyOf: i => "g" + i),
                    ],
                },
            ],
        });

        var scene = new SceneStore();
        new TreeReconciler(scene, strings).ReconcileRoot(tree, null);
        new FlexLayout(scene, new HeadlessFontSystem(strings)).Run(scene.Root, new Size2(480f, 320f));

        scene.TryGetScroll(scene.Root, out var outer);
        NodeHandle gridViewport = NodeHandle.Null;
        void Visit(NodeHandle n)
        {
            if (n.IsNull) return;
            if (scene.TryGetScroll(n, out var sc) && sc.ItemCount == 1000) gridViewport = n;
            for (var c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c)) Visit(c);
        }
        Visit(scene.Root);

        scene.TryGetScroll(gridViewport, out var gridScroll);
        var firstCard = scene.FirstChild(gridScroll.ContentNode);
        var grid = scene.AbsoluteRect(gridViewport);
        var card = scene.AbsoluteRect(firstCard);

        bool contentClamped = Near(outer.ContentW, outer.ViewportW) && Near(scene.Bounds(outer.ContentNode).W, outer.ViewportW);
        bool gridConstrained = !gridViewport.IsNull && Near(grid.W, 432f) && card.W <= 101f;

        Check("37a. vertical ScrollView clamps cross-axis content before nested virtual layout",
            contentClamped && gridConstrained,
            $"contentW={outer.ContentW:0} viewportW={outer.ViewportW:0} gridW={grid.W:0} cardW={card.W:0}");
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
        for (int i = 0; i < 40; i++) host.RunFrame();

        bool expandedGutter = false, expandedThumb = false;
        foreach (var rect in device.LastRects)
        {
            if (!LaneRect(rect, out var r)) continue;
            expandedGutter |= r.W >= 10f && r.H >= 190f;
            expandedThumb |= r.W >= 5f && r.W < 10f && r.H >= 30f && r.H < 190f;
        }
        bool hoverSettledIdle = !host.HasActiveWork;

        window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(260f, 260f), 0, 0));
        for (int i = 0; i < 30; i++) host.RunFrame();

        bool collapsedGutter = false, collapsedThumb = false;
        foreach (var rect in device.LastRects)
        {
            if (!LaneRect(rect, out var r)) continue;
            collapsedGutter |= r.W >= 10f && r.H >= 190f;
            collapsedThumb |= r.W <= 3f && r.H >= 30f;   // WinUI resting thumb = 2px visible (8 − 6 stroke)
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
        Check("38b. hover-visible scrollbar settles without keeping frames active",
            hoverSettledIdle, $"active={host.HasActiveWork}");
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

        var counted = new CountingVirtualProbe();
        using var app2 = new HeadlessPlatformApp();
        var window2 = new HeadlessWindow(new WindowDesc("virt-overlap", new Size2(640, 480), 1f));
        window2.Show();
        using var host2 = new AppHost(app2, window2, new HeadlessGpuDevice(), fonts, strings, counted);
        host2.RunFrame();
        int calls0 = counted.RenderItemCalls;
        window2.QueueInput(new InputEvent(InputKind.Wheel, new Point2(150, 100), 0, 0, 80f));
        var guardFrame = host2.RunFrame();
        int guardCalls = counted.RenderItemCalls - calls0;
        host2.Scene.TryGetScroll(host2.Scene.Root, out var guardScroll);
        bool guardHeld = !guardFrame.Rendered && guardScroll.FirstRealized == 0 && guardCalls == 0;

        window2.QueueInput(new InputEvent(InputKind.Wheel, new Point2(150, 100), 0, 0, 120f));
        host2.RunFrame();
        int calls1 = counted.RenderItemCalls - calls0 - guardCalls;
        host2.Scene.TryGetScroll(host2.Scene.Root, out var countedScroll);
        bool reusedOverlap = guardHeld && countedScroll.FirstRealized > 0 && calls1 <= 5;
        Check("40a. virtual scroll keeps overscan guard and reuses overlapping item elements",
            reusedOverlap, $"guardCalls={guardCalls} first={countedScroll.FirstRealized} newTemplateCalls={calls1}");

        // Far jump (zero overlap — the scrollbar thumb-drag storm): the window's scene NODES are recycled in place
        // (columns rebound to the new items), not mounted/removed — the drag path becomes a column rewrite.
        var beforeNodes = new List<NodeHandle>();
        for (var c = host.Scene.FirstChild(content); !c.IsNull; c = host.Scene.NextSibling(c)) beforeNodes.Add(c);
        long liveBeforeJump = host.Scene.LiveCount;
        window.QueueInput(new InputEvent(InputKind.Wheel, ptr, 0, 0, -400_000f));   // end → top: no overlap with the old window
        host.RunFrame();
        var afterNodes = new HashSet<NodeHandle>();
        for (var c = host.Scene.FirstChild(content); !c.IsNull; c = host.Scene.NextSibling(c)) afterNodes.Add(c);
        host.Scene.TryGetScroll(vp, out var scTop);
        int recycledCount = 0;
        foreach (var n in beforeNodes) if (afterNodes.Contains(n)) recycledCount++;
        var firstRowText = host.Scene.FirstChild(host.Scene.FirstChild(content));
        string rebound = strings.Resolve(host.Scene.Paint(firstRowText).Text);
        bool recycledOk = scTop.FirstRealized == 0 && afterNodes.Count == beforeNodes.Count
            && recycledCount == beforeNodes.Count && host.Scene.LiveCount == liveBeforeJump && rebound == "row 0";
        Check("40b. far-jump realize recycles the window's scene nodes (rebind, no mount/remove)",
            recycledOk, $"first={scTop.FirstRealized} recycled={recycledCount}/{beforeNodes.Count} live {liveBeforeJump}→{host.Scene.LiveCount} text='{rebound}'");

        // Streaming thousands of unique row strings must NOT accrete in the interner: scrolled-out text releases its
        // ref, the map entry drops immediately, and the slot clears behind the reader quarantine (StringTable.Tick).
        int mapBase = strings.MapCount;
        for (int s = 0; s < 40; s++) { window.QueueInput(new InputEvent(InputKind.Wheel, ptr, 0, 0, 5_000f)); host.RunFrame(); }
        for (int s = 0; s < 25; s++) { window.QueueInput(new InputEvent(InputKind.Wheel, ptr, 0, 0, s % 2 == 0 ? 1f : -1f)); host.RunFrame(); }   // settle past the quarantine (painted frames tick the table)
        int mapAfter = strings.MapCount;
        host.Scene.TryGetScroll(vp, out var scStream);
        bool streamed = scStream.FirstRealized > 3000;
        bool reclaimed = mapAfter - mapBase < 200 && strings.PendingReclaim < 200;
        Check("40c. scrolled-out row text is reclaimed by the interner (no per-row string accretion)",
            streamed && reclaimed, $"first={scStream.FirstRealized} map {mapBase}→{mapAfter} pending={strings.PendingReclaim}");

        // BOUND list (Virtual.ListBound): the template runs once per visible slot; a far jump rebinds index SIGNALS
        // only — same nodes, same elements, zero template re-runs, text/fill rebound in the same frame.
        var bound = new BoundVirtualProbe();
        using var app3 = new HeadlessPlatformApp();
        var window3 = new HeadlessWindow(new WindowDesc("virt-bound", new Size2(640, 480), 1f));
        window3.Show();
        using var host3 = new AppHost(app3, window3, new HeadlessGpuDevice(), fonts, strings, bound);
        host3.RunFrame();
        var vp3 = host3.Scene.Root;
        host3.Scene.TryGetScroll(vp3, out var bsc0);
        var content3 = bsc0.ContentNode;

        // First jump to mid-list (the window stabilizes at full size: overscan extends both directions), THEN measure.
        window3.QueueInput(new InputEvent(InputKind.Wheel, new Point2(150, 200), 0, 0, 100_000f));
        host3.RunFrame();
        int slots0 = host3.Scene.ChildCount(content3);
        int templateCalls0 = bound.TemplateCalls;
        var slotNodes = new HashSet<NodeHandle>();
        for (var c = host3.Scene.FirstChild(content3); !c.IsNull; c = host3.Scene.NextSibling(c)) slotNodes.Add(c);

        window3.QueueInput(new InputEvent(InputKind.Wheel, new Point2(150, 200), 0, 0, 100_000f));   // far jump: zero overlap
        host3.RunFrame();
        host3.Scene.TryGetScroll(vp3, out var bsc1);
        int slots1 = host3.Scene.ChildCount(content3);
        bool sameNodes = true;
        for (var c = host3.Scene.FirstChild(content3); !c.IsNull; c = host3.Scene.NextSibling(c)) sameNodes &= slotNodes.Contains(c);
        var boundFirstText = strings.Resolve(host3.Scene.Paint(host3.Scene.FirstChild(host3.Scene.FirstChild(content3))).Text);
        bool boundOk = bsc1.FirstRealized > 4000 && bound.TemplateCalls == templateCalls0 && slots1 == slots0
            && sameNodes && boundFirstText == $"row {bsc1.FirstRealized}";
        Check("40d. bound list rebinds via index signals on a far jump (no template re-run, no node churn)",
            boundOk, $"first={bsc1.FirstRealized} template {templateCalls0}→{bound.TemplateCalls} slots {slots0}→{slots1} sameNodes={sameNodes} text='{boundFirstText}'");

        // Scrollbar THUMB DRAG on a bound list (the 100k storm path): grab the thumb, drag in small steps — the
        // offset must track the thumb the whole way (the drag must never silently disengage mid-travel).
        window3.QueueInput(new InputEvent(InputKind.Wheel, new Point2(150, 200), 0, 0, -10_000_000f));   // back to the top
        host3.RunFrame();
        float laneX = 294f;
        window3.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(laneX, 10f), 0, 0));
        host3.RunFrame();
        window3.QueueInput(new InputEvent(InputKind.PointerDown, new Point2(laneX, 10f), 0, 0));
        host3.RunFrame();
        float lastOff = 0f;
        int advanceSteps = 0;
        const int dragSteps = 120;
        for (int s = 1; s <= dragSteps; s++)
        {
            window3.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(laneX, 10f + s * 2.5f), 0, 0));
            host3.RunFrame();
            host3.Scene.TryGetScroll(vp3, out var dsc);
            if (dsc.OffsetY > lastOff) advanceSteps++;
            lastOff = dsc.OffsetY;
        }
        window3.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(laneX, 10f + dragSteps * 2.5f), 0, 0));
        host3.RunFrame();
        host3.Scene.TryGetScroll(vp3, out var dragEnd);
        bool dragOk = advanceSteps >= dragSteps - 5 && dragEnd.OffsetY > 100_000f && dragEnd.FirstRealized > 2000;
        Check("40e. scrollbar thumb drag tracks the full travel on a bound list (never disengages)",
            dragOk, $"advanced {advanceSteps}/{dragSteps} off={dragEnd.OffsetY:0} first={dragEnd.FirstRealized}");
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

        // 54c — a per-monitor DPI hop MID-SESSION (the WM_DPICHANGED path): EnsureSize watches scale as well as px
        // size, so a scale-only change re-lays-out in the new DIP viewport, and the suggested-rect resize restores it.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("dpihop", new Size2(1200, 700), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            using var host = new AppHost(app, window, device, fonts, strings, new NavProbe());
            host.RunFrame();
            bool labels1 = HasGlyph(device, strings, "Home");
            float w1 = host.Scene.AbsoluteRect(host.Scene.Root).W;          // 1200 DIP @1x → Expanded

            window.Scale = 1.5f;                                            // monitor hop, px not yet adjusted
            host.RunFrame();
            bool labels2 = HasGlyph(device, strings, "Home");
            float w2 = host.Scene.AbsoluteRect(host.Scene.Root).W;          // 800 DIP @1.5x → Compact

            window.ClientSizePx = new Size2(1800, 1050);                    // the OS-suggested rect at the new DPI
            host.RunFrame();
            bool labels3 = HasGlyph(device, strings, "Home");
            float w3 = host.Scene.AbsoluteRect(host.Scene.Root).W;          // 1200 DIP again → Expanded restored

            Check("54c. mid-session DPI change re-lays-out in the new DIP viewport (scale-only, then the suggested-rect resize)",
                labels1 && Near(w1, 1200f) && !labels2 && Near(w2, 800f) && labels3 && Near(w3, 1200f),
                $"w {w1:0}@1x → {w2:0}@1.5x (labels={labels2}) → {w3:0}@1.5x/1800px (labels={labels3})");
        }
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
        bool iconFactory = icon.FontFamily == Theme.IconFont && icon.Text.Value == Icons.Play && icon.Size == 20f;
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

        var chromeScene = LayoutTree(strings, new BoxEl
        {
            Width = 120,
            Height = 72,
            Corners = Radii.OverlayAll,
            Fill = ColorF.FromRgba(0x20, 0x20, 0x20),
            BorderColor = ColorF.FromRgba(0x80, 0x80, 0x80),
            BorderWidth = 1f,
            Children =
            [
                new BoxEl
                {
                    Width = 120,
                    Height = 36,
                    Fill = ColorF.FromRgba(0x40, 0x40, 0x40),
                },
            ],
        });
        dl.Reset();
        SceneRecorder.Record(chromeScene, dl);
        int firstFill = -1, secondFill = -1, stroke = -1, opIndex = 0, pos = 0;
        var bytes = dl.Bytes;
        while (pos + sizeof(int) <= bytes.Length)
        {
            var op = (DrawOp)MemoryMarshal.Read<int>(bytes.Slice(pos));
            pos += sizeof(int);
            if (op == DrawOp.FillRoundRect)
            {
                if (firstFill < 0) firstFill = opIndex;
                else if (secondFill < 0) secondFill = opIndex;
            }
            else if (op == DrawOp.DrawRoundRectStroke && stroke < 0)
            {
                stroke = opIndex;
            }
            pos += DrawPayloadSize(op);
            opIndex++;
        }
        bool chromeOrder = firstFill >= 0 && secondFill > firstFill && stroke > secondFill;
        Check("57c. BoxEl chrome order: parent border records after descendant fills",
            chromeOrder, $"parentFill={firstFill} childFill={secondFill} stroke={stroke}");

        // 57e — WinUI MappingMode=Absolute (ControlElevationBorderBrush EndPoint 0,3): AxisLengthPx squeezes the stop
        // ramp into 3 physical px of the 40px axis (offsets ×3/40); AnchorEnd (the light/accent ScaleY=-1 mirror)
        // measures the band from the BOTTOM, reversing the stop order so offsets stay ascending.
        {
            var sec = ColorF.FromRgba(0x10, 0x10, 0x10);
            var def = ColorF.FromRgba(0xF0, 0xF0, 0xF0);
            var absBand = new GradientSpec(GradientShape.Linear, 90f,
                [new GradientStop(0.33f, sec), new GradientStop(1f, def)]) { AxisLengthPx = 3f };
            var topScene = LayoutTree(strings, new BoxEl
            {
                Width = 120, Height = 40, Corners = CornerRadius4.All(4f), BorderBrush = absBand, BorderWidth = 1f,
            });
            var dl2 = new DrawList();
            SceneRecorder.Record(topScene, dl2);
            var dev2 = new HeadlessGpuDevice();
            dev2.SubmitDrawList(dl2.Bytes, dl2.SortKeys, new FrameInfo(new Size2(400, 300), 1f, ColorF.Transparent));
            var g1 = dev2.LastGradientStrokes[0];
            float k = 3f / 40f;
            bool topBand = Near(g1.O0, 0.33f * k) && Near(g1.O1, k) && g1.C0 == sec && g1.C1 == def;

            var bottomScene = LayoutTree(strings, new BoxEl
            {
                Width = 120, Height = 40, Corners = CornerRadius4.All(4f),
                BorderBrush = absBand with { AnchorEnd = true }, BorderWidth = 1f,
            });
            var dl3 = new DrawList();
            SceneRecorder.Record(bottomScene, dl3);
            var dev3 = new HeadlessGpuDevice();
            dev3.SubmitDrawList(dl3.Bytes, dl3.SortKeys, new FrameInfo(new Size2(400, 300), 1f, ColorF.Transparent));
            var g2 = dev3.LastGradientStrokes[0];
            bool bottomBand = Near(g2.O0, 1f - k) && Near(g2.O1, 1f - 0.33f * k) && g2.C0 == def && g2.C1 == sec;

            Check("57e. absolute-axis elevation band: 3px ramp at the top; AnchorEnd mirrors it to the bottom (stops reversed)",
                topBand && bottomBand,
                $"top=({g1.O0:0.0000},{g1.O1:0.0000} secFirst={g1.C0 == sec}) bottom=({g2.O0:0.0000},{g2.O1:0.0000} defFirst={g2.C0 == def})");
        }
    }

    // Hover cross-fade: the InteractionAnimator eases HoverT and the recorder lerps Fill→HoverFill in LINEAR light
    // (the WinUI ~83ms BackgroundTransition) — it must ease through an intermediate value, not snap, then settle.
    static void PolylineStrokeChecks(StringTable strings)
    {
        var scene = LayoutTree(strings, new PolylineStrokeEl
        {
            Width = 24,
            Height = 24,
            P0 = new Point2(2, 12),
            P1 = new Point2(10, 20),
            P2 = new Point2(22, 4),
            PointCount = 3,
            Color = ColorF.FromRgba(255, 255, 255),
            Thickness = 2f,
            TrimEnd = 0.5f,
        });
        var dl = new DrawList();
        SceneRecorder.Record(scene, dl);
        var dev = new HeadlessGpuDevice();
        dev.SubmitDrawList(dl.Bytes, dl.SortKeys, new FrameInfo(new Size2(100, 100), 1f, ColorF.Transparent));

        bool staticStroke = dev.LastPolylines.Count == 1 && Near(dev.LastPolylines[0].TrimEnd, 0.5f, 0.001f)
            && dev.LastPolylines[0].PointCount == 3;

        var anim = new AnimEngine(scene);
        anim.Keyframes(scene.Root, AnimChannel.StrokeTrimEnd,
            [new Keyframe(0f, 0f, Easing.Linear), new Keyframe(1f, 1f, EasingSpec.CubicBezier(0.55f, 0f, 0f, 1f))], 100f);
        anim.Tick(0f);
        float t0 = scene.Paint(scene.Root).StrokeTrimEnd;
        anim.Tick(16f);
        float t16 = scene.Paint(scene.Root).StrokeTrimEnd;
        dl.Reset();
        SceneRecorder.Record(scene, dl);
        dev.SubmitDrawList(dl.Bytes, dl.SortKeys, new FrameInfo(new Size2(100, 100), 1f, ColorF.Transparent));

        bool animatedStroke = Near(t0, 0f, 0.001f) && t16 > 0f && t16 < 0.35f
            && dev.LastPolylines.Count == 1 && Near(dev.LastPolylines[0].TrimEnd, t16, 0.001f);
        Check("57d. PolylineStroke emits DrawPolylineStroke and supports animated trim-end",
            staticStroke && animatedStroke, $"static={staticStroke} t0={t0:0.00} t16={t16:0.00} cmds={dev.LastPolylines.Count}");
    }

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

    // A PURE keyed reorder (reverse: same keys, same count) must still relayout — the diff creates/removes nothing,
    // but the re-appended child order has to move the rows (the gallery "Reverse does nothing" regression).
    static void FlowReorderChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("flowreorder", new Size2(320, 480), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        FlowReorderProbe.Renders = 0;
        var root = new FlowReorderProbe();
        using var host = new AppHost(app, window, device, fonts, strings, root);
        host.RunFrame();
        int r0 = FlowReorderProbe.Renders;

        var forHost = Child(host.Scene, host.Scene.Root, 0);
        var rowA = Child(host.Scene, forHost, 0);                 // the "fa" row — identity must survive the reorder
        var b0 = host.Scene.Bounds(rowA);

        var rev = new List<string>(root.Items!.Peek()); rev.Reverse();
        root.Items!.Value = rev;                                  // pure move: no add, no remove, count unchanged
        host.RunFrame();
        var b1 = host.Scene.Bounds(rowA);

        bool lastIsA = Child(host.Scene, forHost, 2) == rowA;     // scene order reversed, node preserved by key
        bool movedInLayout = MathF.Abs(b1.X - b0.X) + MathF.Abs(b1.Y - b0.Y) > 10f;   // and layout actually moved it
        Check("61b. Flow.For pure reorder (reverse) relayouts the rows (key-preserved node moves slots)",
            lastIsA && movedInLayout && FlowReorderProbe.Renders == r0,
            $"rowA ({b0.X:0},{b0.Y:0})→({b1.X:0},{b1.Y:0}) lastIsA={lastIsA} parentRenders+{FlowReorderProbe.Renders - r0}");
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

    // Wave 2 — the button/input-state controls adopt the Wave-1 primitives: the IsEnabled engine gate (P1) and the
    // TextEl foreground ramps (P2). Verified end-to-end through the real Button factory.
    static void Wave2ControlChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("wave2", new Size2(320, 240), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var root = new ButtonProbe();
        using var host = new AppHost(app, window, device, fonts, strings, root);

        host.RunFrame();
        var buttons = Roles(host.Scene, AutomationRole.Button);
        var enabledBtn = buttons[0];
        var disabledBtn = buttons[1];
        var restFg = GlyphColor(device, strings, "enabled-btn");   // resting foreground (ButtonForeground = TextPrimary)

        // W2.a the disabled Button swallows the click (the control now sets IsEnabled=false instead of nulling handlers).
        ClickNode(host, window, disabledBtn);
        int afterDisabledClick = root.Clicks;
        ClickNode(host, window, enabledBtn);
        Check("W2.a Button adopts the IsEnabled gate (disabled swallows click; enabled clicks)",
            afterDisabledClick == 0 && root.Clicks == 1, $"disabledClicks={afterDisabledClick} enabled={root.Clicks}");

        // W2.b the disabled Button's label resolves ButtonForegroundDisabled — matched on FULL ARGB (WinUI dims via ALPHA),
        // and proven actually dimmer than the resting foreground (not just the same white RGB).
        var disFg = GlyphColor(device, strings, "disabled-btn");
        var disExpect = Tok.TextDisabled;
        bool disMatchesToken = ColorClose(disFg, disExpect, 0.03f);
        bool disActuallyDimmer = disFg.A < restFg.A - 0.05f;
        Check("W2.b disabled Button label = DisabledForeground (ARGB) and is dimmer than resting",
            disMatchesToken && disActuallyDimmer,
            $"label=({disFg.R:0.00},{disFg.G:0.00},{disFg.B:0.00},A={disFg.A:0.00}) token A={disExpect.A:0.00} restA={restFg.A:0.00}");

        // W2.c pressing the enabled Button ramps its label to ButtonForegroundPressed (TextSecondary) — full ARGB, and
        // the alpha actually changed from resting (the WinUI press dim).
        var c = CenterOf(host.Scene, enabledBtn);
        window.QueueInput(new InputEvent(InputKind.PointerDown, c, 0, 0));
        for (int i = 0; i < 20; i++) host.RunFrame();
        var pressFg = GlyphColor(device, strings, "enabled-btn");
        var pressExpect = Tok.TextSecondary;
        bool pressMatchesToken = ColorClose(pressFg, pressExpect, 0.06f);
        bool pressChangedFromRest = MathF.Abs(pressFg.A - restFg.A) > 0.02f || !ColorClose(pressFg, restFg, 0.02f);
        window.QueueInput(new InputEvent(InputKind.PointerUp, c, 0, 0));
        host.RunFrame();
        Check("W2.c pressed Button label ramps to PressedForeground (ARGB, changed from resting)",
            pressMatchesToken && pressChangedFromRest,
            $"label=({pressFg.R:0.00},{pressFg.G:0.00},{pressFg.B:0.00},A={pressFg.A:0.00}) pressTokenA={pressExpect.A:0.00} restA={restFg.A:0.00}");

        // W2.d HyperlinkButton uses the accent TEXT palette (AccentTextPrimary), NOT the accent FILL (AccentDefault),
        // and that foreground tracks a live accent override (OS accent / Tok.SetAccent) by recomputing its shade.
        ColorF LinkForeground(string id)
        {
            using var a = new HeadlessPlatformApp();
            var w = new HeadlessWindow(new WindowDesc(id, new Size2(240, 120), 1f)); w.Show();
            var dev = new HeadlessGpuDevice();
            using var h = new AppHost(a, w, dev, new HeadlessFontSystem(strings), strings, new HyperlinkProbe());
            h.RunFrame();
            return GlyphColor(dev, strings, "link-text");
        }

        var defFg = LinkForeground("hlink");
        bool usesAccentText = ColorClose(defFg, Tok.AccentTextPrimary, 0.02f) && !ColorClose(defFg, Tok.AccentDefault, 0.02f);

        Tok.SetAccent(ColorF.FromRgba(0xE0, 0x40, 0x40));   // developer/OS override (red)
        var ovFg = LinkForeground("hlink2");
        Tok.SetAccent(null);                                 // clear the override (revert to theme default)
        bool tracksOverride = !ColorClose(ovFg, defFg, 0.05f) && ovFg.R > ovFg.B + 0.1f;   // now reddish, changed

        Check("W2.d HyperlinkButton foreground = accent TEXT (not fill) and tracks the accent override",
            usesAccentText && tracksOverride,
            $"def=({defFg.R:0.00},{defFg.G:0.00},{defFg.B:0.00}) override=({ovFg.R:0.00},{ovFg.G:0.00},{ovFg.B:0.00}) accentFill=({Tok.AccentDefault.R:0.00},{Tok.AccentDefault.G:0.00},{Tok.AccentDefault.B:0.00})");
    }

    // Wave 1 / P3 — typed computed "TemplateSettings" convention: the Expander derives ExpanderTemplateSettings once from
    // its open state and feeds them into channels — the chevron ROTATION (one glyph, down→up) and the content reveal.
    static void ExpanderSettingsChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("expander", new Size2(360, 240), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var root = new Expander { Header = "Section", Content = new TextEl("expander-body") { Size = 14f }, InitiallyExpanded = false };
        using var host = new AppHost(app, window, device, fonts, strings, root);

        host.RunFrame();   // collapsed (chevron rotation seeded to 0° → identity)
        var chevron0 = Child(host.Scene, Child(host.Scene, host.Scene.Root, 0), 1);
        float m11Collapsed = host.Scene.Paint(chevron0).LocalTransform.M11;
        bool noContent = Child(host.Scene, Child(host.Scene, host.Scene.Root, 1), 0).IsNull;   // clip mounted, panel not

        // Toggle open. (a) The chevron rotation TWEENS (167ms): track peak sin θ — a tween passes through a mid-angle
        // (sin θ → ~1 near 90°), an instant snap never leaves ~0. (b) The content panel SLIDES out from under the
        // header: the clip wrapper's SizeMode.Reflow Trailing anchor keeps the panel's bottom edge on the reveal edge
        // (ChildShiftY < 0 mid-flight, 0 at rest) — an instant appear would read 0 every frame.
        ClickNode(host, window, Child(host.Scene, host.Scene.Root, 0));
        float peakSin = 0f, minShift = 0f;
        for (int i = 0; i < 16; i++)
        {
            host.RunFrame();
            var ch = Child(host.Scene, Child(host.Scene, host.Scene.Root, 0), 1);
            peakSin = MathF.Max(peakSin, MathF.Abs(host.Scene.Paint(ch).LocalTransform.M12));
            var clipW = Child(host.Scene, host.Scene.Root, 1);   // the reflow clip wrapper carries the child-shift
            if (!clipW.IsNull) minShift = MathF.Min(minShift, host.Scene.Paint(clipW).ChildShiftY);
        }
        bool rotating = peakSin > 0.5f;
        bool contentSlidIn = minShift < -4f;

        for (int i = 0; i < 16; i++) host.RunFrame();   // settle
        var chevronDone = Child(host.Scene, Child(host.Scene, host.Scene.Root, 0), 1);
        float m11Done = host.Scene.Paint(chevronDone).LocalTransform.M11;   // cos 180° ≈ -1
        bool settled = m11Done < -0.9f;
        bool hasContent = !Child(host.Scene, Child(host.Scene, host.Scene.Root, 1), 0).IsNull && HasGlyph(device, strings, "expander-body");

        Check("W1-P3.a Expander animates chevron rotation + content slide (mid-flight)",
            Near(m11Collapsed, 1f, 0.05f) && rotating && settled && contentSlidIn && hasContent,
            $"m11→{m11Done:0.00} peakSinθ={peakSin:0.00} minShift={minShift:0.0} content {!noContent}→{hasContent}");
    }

    // Wave 1 / P5a — popup placement result: vertical flip when a side can't fit, corner-join against the anchor, and
    // height clamping under a full collision. Pure math — deterministic, no host.
    static void PlacementChecks()
    {
        var vp = new Size2(480, 400);

        // Anchor near the bottom: a 120-tall free flyout can't fit below → flips ABOVE; free flyouts keep all corners.
        var low = new RectF(20, 380, 100, 24);
        var pa = FlyoutPositioner.Place(in low, new Size2(100, 120), in vp, FlyoutPlacement.BottomLeft);
        bool flipped = pa.OpensUp && pa.Y < low.Y && pa.CornerJoin == CornerJoin.None;

        // Anchor near the top: a free flyout opens BELOW (default) and still keeps all corners rounded.
        var high = new RectF(20, 10, 100, 24);
        var pb = FlyoutPositioner.Place(in high, new Size2(100, 120), in vp, FlyoutPlacement.BottomLeft);
        // WinUI FlyoutBase::FlyoutMargin = 4 (FlyoutBase_Partial.cpp:65) — the flyout sits 4px off the anchor edge.
        bool below = !pb.OpensUp && pb.Y >= high.Y + high.H + 3.5f && pb.CornerJoin == CornerJoin.None;

        // Attached stretch dropdowns (ComboBox/AutoSuggest style) stay flush and join the field edge.
        var attach = FlyoutPositioner.Place(in high, new Size2(100, 120), in vp, FlyoutPlacement.BottomStretch);
        bool attachedJoin = !attach.OpensUp && Near(attach.Y, high.Y + high.H, 0.01f) && attach.CornerJoin == CornerJoin.Top;

        // Popup taller than the viewport (collides both ways): clamp MeasuredH to the larger side (here, below).
        var mid = new RectF(20, 180, 100, 24);
        var pc = FlyoutPositioner.Place(in mid, new Size2(100, 600), in vp, FlyoutPlacement.BottomLeft);
        bool clamped = pc.MeasuredH > 0f && pc.MeasuredH < 600f;

        Check("W1-P5.a placement: flip-up + free/attached corner-join + collision clamp",
            flipped && below && attachedJoin && clamped,
            $"flip={flipped} below={below} attach={attachedJoin} clampH={pc.MeasuredH:0}");
    }

    // Wave 1 / P5b — overlay focus-restoration: an overlay captures the focused node at open time and restores it when
    // it finishes closing (host-wired through the InputHooks focus get/restore delegates).
    static void OverlayFocusRestoreChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("ovfocus", new Size2(480, 360), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var root = new FocusRestoreProbe();
        using var host = new AppHost(app, window, device, fonts, strings, root);

        host.RunFrame();
        ClickNode(host, window, root.AnchorNode);   // focus the anchor (pre-open focus)
        bool anchorFocused = (host.Scene.Flags(root.AnchorNode) & NodeFlags.Focused) != 0;

        // Open an overlay (captures SavedFocus = anchor), Tab moves focus into it, then close and let it settle.
        root.Handle = root.Service!.Open(() => root.AnchorNode,
            () => new BoxEl { Width = 120, Height = 80, Children = [new BoxEl { Width = 100, Height = 24, OnClick = () => { } }] });
        host.RunFrame();
        window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Tab));
        host.RunFrame();
        bool movedAway = (host.Scene.Flags(root.AnchorNode) & NodeFlags.Focused) == 0;
        void DumpP5b(NodeHandle n, int d)
        {
            var fl = host.Scene.Flags(n);
            Console.Error.WriteLine($"[DBG-P5b] {new string(' ', d * 2)}{n} focusable={(fl & NodeFlags.Focusable) != 0} focused={(fl & NodeFlags.Focused) != 0} role={host.Scene.Interaction(n).Role}");
            for (var c = host.Scene.FirstChild(n); !c.IsNull; c = host.Scene.NextSibling(c)) DumpP5b(c, d + 1);
        }
        DumpP5b(host.Scene.Root, 0);

        Console.Error.WriteLine($"[DBG-P5b] beforeClose anchor={root.AnchorNode}");
        root.Handle.Close();
        Console.Error.WriteLine($"[DBG-P5b] afterClose anchorFocused={(host.Scene.Flags(root.AnchorNode) & NodeFlags.Focused) != 0}");
        for (int i = 0; i < 30; i++)
        {
            host.RunFrame();   // close fade settles → Finalize → restore
            if (i < 5 || i == 29) Console.Error.WriteLine($"[DBG-P5b] f{i} anchorFocused={(host.Scene.Flags(root.AnchorNode) & NodeFlags.Focused) != 0}");
        }
        bool restored = (host.Scene.Flags(root.AnchorNode) & NodeFlags.Focused) != 0;

        Check("W1-P5.b overlay restores focus to the pre-open node on close",
            anchorFocused && movedAway && restored, $"anchor0={anchorFocused} movedAway={movedAway} restored={restored}");
    }

    // Wave 1 / P6 — focus/keyboard navigation service: TabIndex-respecting tab order, XY arrow navigation, and scoped
    // roving (within an overlay/menu subtree). Drives the InputDispatcher directly (no AppHost needed).
    // E2 — the widened input vocabulary: modifiers, Space-on-keyup activation, click count, right-tap context,
    // accelerators, access keys, focus scopes, window blur, hover-resolved cursors.
    static void InputVocabularyChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);

        // E2.a — Shift+Tab walks focus backward.
        {
            var scene = new SceneStore();
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Direction = 0, Gap = 10,
                Children =
                [
                    new BoxEl { Key = "A", Width = 20, Height = 20, OnClick = () => { } },
                    new BoxEl { Key = "B", Width = 20, Height = 20, OnClick = () => { } },
                ],
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            var a = Child(scene, scene.Root, 0); var b = Child(scene, scene.Root, 1);
            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Tab) });
            var f1 = disp.Focused;   // A
            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Tab, Mods: KeyModifiers.Shift) });
            var f2 = disp.Focused;   // wraps back to B
            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Tab, Mods: KeyModifiers.Shift) });
            var f3 = disp.Focused;   // back to A
            Check("E2.a Shift+Tab walks focus backward (wraps)", f1 == a && f2 == b && f3 == a,
                $"f1=A?{f1 == a} f2=B?{f2 == b} f3=A?{f3 == a}");
        }

        // E2.b/c/d — WinUI ButtonBase activation (ButtonBaseKeyProcess.h): Space/Enter key-DOWN arms the pressed
        // visual (held-key repeats ignored — ONE activation per hold); the click fires on key-UP (ClickMode.Release,
        // ButtonBase_Partial.cpp:475-483); Escape or ANY other key while held cancels without firing (:64-70).
        {
            var scene = new SceneStore();
            int clicks = 0;
            new TreeReconciler(scene, strings).ReconcileRoot(
                new BoxEl { Width = 40, Height = 20, OnClick = () => clicks++ }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            disp.MoveFocus(forward: true);
            var node = disp.Focused;

            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Enter) });
            bool enterArms = clicks == 0 && (scene.Flags(node) & NodeFlags.Pressed) != 0;   // pressed, no click yet
            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Enter, IsRepeat: true) });
            bool heldOnce = clicks == 0;                                                    // held Enter never re-fires
            disp.Dispatch(new[] { new InputEvent(InputKind.KeyUp, default, 0, Keys.Enter) });
            bool enterUp = clicks == 1 && (scene.Flags(node) & NodeFlags.Pressed) == 0;     // click on the UP edge

            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Space) });
            bool armedNoFire = clicks == 1 && (scene.Flags(node) & NodeFlags.Pressed) != 0;
            disp.Dispatch(new[] { new InputEvent(InputKind.KeyUp, default, 0, Keys.Space) });
            bool firedOnUp = clicks == 2 && (scene.Flags(node) & NodeFlags.Pressed) == 0;
            Check("E2.b Space/Enter arm pressed on key-down, click on key-up; a held key activates exactly once",
                enterArms && heldOnce && enterUp && armedNoFire && firedOnUp,
                $"enterArm={enterArms} held={heldOnce} enterUp={enterUp} spaceArm={armedNoFire} spaceUp={firedOnUp}");

            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Space) });
            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Escape) });
            disp.Dispatch(new[] { new InputEvent(InputKind.KeyUp, default, 0, Keys.Space) });
            Check("E2.c Escape cancels a held Space without firing",
                clicks == 2 && (scene.Flags(node) & NodeFlags.Pressed) == 0, $"clicks={clicks}");

            // ANY other key while held also cancels without firing (ButtonBaseKeyProcess.h:64-70) — the press visual
            // clears on the foreign key-down and the eventual Space-up does nothing.
            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Space) });
            bool reArmed = (scene.Flags(node) & NodeFlags.Pressed) != 0;
            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.A) });
            bool canceledByKey = (scene.Flags(node) & NodeFlags.Pressed) == 0;
            disp.Dispatch(new[] { new InputEvent(InputKind.KeyUp, default, 0, Keys.Space) });
            Check("E2.c2 any other key-down cancels a held Space/Enter press without firing",
                reArmed && canceledByKey && clicks == 2, $"armed={reArmed} canceled={canceledByKey} clicks={clicks}");
        }

        // E2.e — double/triple-click promotion (timestamps + slop) surfaces in OnPointerPressed.ClickCount.
        {
            var scene = new SceneStore();
            var counts = new List<byte>();
            new TreeReconciler(scene, strings).ReconcileRoot(
                new BoxEl { Width = 60, Height = 30, OnPointerPressed = e => counts.Add(e.ClickCount) }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            var p = new Point2(10, 10);
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, p, 0, 0, TimestampMs: 1000) });
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, p, 0, 0, TimestampMs: 1040) });
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, p, 0, 0, TimestampMs: 1100) });
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, p, 0, 0, TimestampMs: 1140) });
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, p, 0, 0, TimestampMs: 1200) });
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, p, 0, 0, TimestampMs: 1240) });
            // A press 600ms later resets to 1; so does a press 5px away.
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, p, 0, 0, TimestampMs: 2400) });
            Check("E2.e click count promotes 1→2→3 inside the window and resets after it",
                counts.Count == 4 && counts[0] == 1 && counts[1] == 2 && counts[2] == 3 && counts[3] == 1,
                string.Join(",", counts));
        }

        // E2.f/g — right-click release fires OnContextRequested (left never does); the Menu key (VK_APPS) fires it
        // on the focused node at its centre.
        {
            var scene = new SceneStore();
            int ctx = 0; Point2 ctxAt = default;
            new TreeReconciler(scene, strings).ReconcileRoot(
                new BoxEl { Width = 60, Height = 30, OnClick = () => { }, OnContextRequested = p => { ctx++; ctxAt = p; } }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            var p = new Point2(20, 10);
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, p, 0, 0) });
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, p, 0, 0) });
            bool leftSilent = ctx == 0;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, p, 1, 0) });
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, p, 1, 0) });
            bool rightFired = ctx == 1 && Near(ctxAt.X, 20) && Near(ctxAt.Y, 10);
            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Apps) });   // focused by the left click
            bool appsFired = ctx == 2 && Near(ctxAt.X, 30) && Near(ctxAt.Y, 15);             // node centre
            Check("E2.f right-click release fires OnContextRequested (left stays silent)", leftSilent && rightFired,
                $"left={leftSilent} right={rightFired} at=({ctxAt.X:0.#},{ctxAt.Y:0.#})");
            Check("E2.g VK_APPS requests the context menu at the focused node's centre", appsFired,
                $"ctx={ctx} at=({ctxAt.X:0.#},{ctxAt.Y:0.#})");
        }

        // E2.h/i — a Ctrl+K accelerator and an Alt+S access-key chord invoke their owner from anywhere.
        {
            var scene = new SceneStore();
            int accel = 0, access = 0;
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Direction = 0, Gap = 8,
                Children =
                [
                    new BoxEl { Key = "plain", Width = 20, Height = 20, OnClick = () => { } },
                    new BoxEl { Key = "accel", Width = 20, Height = 20, OnClick = () => accel++, Accelerator = new KeyAccelerator(Keys.K, KeyModifiers.Ctrl) },
                    new BoxEl { Key = "access", Width = 20, Height = 20, OnClick = () => access++, AccessKey = 'S' },
                ],
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            disp.MoveFocus(forward: true);   // focus the PLAIN node — accelerator must still find its owner
            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.K, Mods: KeyModifiers.Ctrl) });
            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.K) });   // bare K: no accelerator
            Check("E2.h Ctrl+K accelerator invokes its owner from anywhere (bare K does not)", accel == 1, $"accel={accel}");
            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.S, Mods: KeyModifiers.Alt) });
            Check("E2.i Alt+S access-key chord invokes the mnemonic owner", access == 1, $"access={access}");
        }

        // E2.j — a pushed focus scope traps Tab inside its subtree until popped (dialog focus trap).
        {
            var scene = new SceneStore();
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Direction = 1,
                Children =
                [
                    new BoxEl { Key = "outside", Width = 20, Height = 20, OnClick = () => { } },
                    new BoxEl { Key = "dialog", Direction = 0, Gap = 4, Children = [
                        new BoxEl { Key = "d1", Width = 20, Height = 20, OnClick = () => { } },
                        new BoxEl { Key = "d2", Width = 20, Height = 20, OnClick = () => { } } ] },
                ],
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            var outside = Child(scene, scene.Root, 0);
            var dialog = Child(scene, scene.Root, 1);
            var d1 = Child(scene, dialog, 0); var d2 = Child(scene, dialog, 1);
            disp.PushFocusScope(dialog);
            bool stays = true;
            disp.MoveFocus(forward: true);
            for (int i = 0; i < 4; i++)
            {
                disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Tab) });
                stays &= disp.Focused == d1 || disp.Focused == d2;
            }
            disp.PopFocusScope();
            bool escapes = false;   // scope released → full-tree cycling reaches the outside node again
            for (int i = 0; i < 3 && !escapes; i++)
            {
                disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Tab) });
                escapes = disp.Focused == outside;
            }
            Check("E2.j focus scope traps Tab inside the dialog subtree until popped", stays && escapes,
                $"trapped={stays} released={escapes}");
        }

        // E2.k — WindowBlur clears pressed/hover state and raises the host blur hook (light-dismiss trigger).
        {
            var scene = new SceneStore();
            new TreeReconciler(scene, strings).ReconcileRoot(
                new BoxEl { Width = 40, Height = 20, OnClick = () => { } }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            bool blurred = false;
            disp.OnWindowBlur = () => blurred = true;
            var node = Child(scene, scene.Root, 0).IsNull ? scene.Root : scene.Root;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(10, 10), 0, 0) });
            bool pressedBefore = (scene.Flags(scene.Root) & NodeFlags.Pressed) != 0;
            disp.Dispatch(new[] { new InputEvent(InputKind.WindowBlur, default, 0, 0) });
            bool clearedAfter = (scene.Flags(scene.Root) & NodeFlags.Pressed) == 0;
            Check("E2.k WindowBlur clears pressed state and raises the blur hook",
                pressedBefore && clearedAfter && blurred, $"before={pressedBefore} after={clearedAfter} hook={blurred}");
        }

        // E2.l — hover resolves the cursor with WinUI semantics: clickability does NOT imply the hand (arrow unless an
        // element declares a cursor — WinUI sets the hand only on HyperlinkButton); an explicit cursor INHERITS down to
        // cursor-less descendants; and an explicit Arrow on a child MASKS an ancestor's I-beam (CursorBit stops the
        // walk — WinUI's forced SetCursor(MouseCursorArrow) on TextBox's delete button, TextBox_Partial.cpp:884).
        {
            var scene = new SceneStore();
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Direction = 0, Gap = 10, Padding = Edges4.All(0),
                Children =
                [
                    new BoxEl { Key = "plain", Width = 20, Height = 20, OnClick = () => { } },
                    new BoxEl
                    {
                        Key = "field", Direction = 0, Gap = 10, Cursor = CursorId.IBeam,   // an editing surface
                        Children =
                        [
                            new BoxEl { Key = "text", Width = 20, Height = 20, OnClick = () => { } },                            // inherits I-beam
                            new BoxEl { Key = "affix", Width = 20, Height = 20, OnClick = () => { }, Cursor = CursorId.Arrow }, // masks I-beam
                        ],
                    },
                    new BoxEl { Key = "link", Width = 20, Height = 20, OnClick = () => { }, Cursor = CursorId.Hand },
                ],
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            CursorId last = CursorId.Arrow;
            disp.OnCursorChanged = c => last = c;
            // Row layout: plain 0–20 | field 30–80 (text 30–50, gap 50–60, affix 60–80) | link 90–110.
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(100, 10), 0, 0) });
            bool hand = last == CursorId.Hand;                  // explicit Hand (the HyperlinkButton case)
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(10, 10), 0, 0) });
            bool plainArrow = last == CursorId.Arrow;           // clickable WITHOUT a declared cursor → arrow, not hand
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(40, 10), 0, 0) });
            bool inherited = last == CursorId.IBeam;            // cursor-less child falls through to the field's I-beam
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(70, 10), 0, 0) });
            bool masked = last == CursorId.Arrow;               // child's explicit Arrow masks the ancestor I-beam
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(55, 10), 0, 0) });
            bool ownSurface = last == CursorId.IBeam;           // the field's OWN gap resolves its I-beam (CursorBit
                                                                // makes a cursor-declared node hover-resolvable, like
                                                                // WinUI's background-gated hit testing)
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(300, 200), 0, 0) });
            bool offArrow = last == CursorId.Arrow;             // off-control: a REAL IBeam→Arrow transition must fire
            Check("E2.l hover resolves the cursor (no clickable hand; explicit inherits; Arrow masks ancestor I-beam; own surface; off→arrow)",
                hand && plainArrow && inherited && masked && ownSurface && offArrow,
                $"hand={hand} plain={plainArrow} inherit={inherited} mask={masked} own={ownSurface} off={offArrow}");
        }
    }

    // E5 — the drag-reorder gesture engine: BoxEl.CanDrag arms Input.DragController on press; pointer travel past the
    // 4px per-axis drag box (Win32 SM_CXDRAG/SM_CYDRAG defaults — microsoft-ui-xaml
    // dxaml\xcp\dxaml\lib\ListViewBaseItem_Partial.cpp:1864-1878 IsOutsideDragRectangle: dx > maxDx || dy > maxDy)
    // promotes the press to a drag: the node follows the pointer at WinUI ListViewItemDragThemeOpacity 0.80
    // (controls\dev\CommonStyles\ListViewItem_themeresources.xaml:7) with a flyout-class shadow, stops hit-testing,
    // and the eventual release SUPPRESSES the click (a finished WinUI drag never raises the item's click/Tapped).
    // ReorderList carries the live-reorder slot math: midpoint rule (GetDragOverIndex —
    // ListViewBase_Partial_Reorder.cpp:984-1063), the 200ms LISTVIEW_LIVEREORDER_TIMER dwell (:50) and the
    // part-to-make-room displacement (MoveItemsForLiveReorder :2125-2158).
    // Wave B input-semantics parity (WinUI ButtonBase/RepeatButton/NumberBox/TabView ground truth): pressed tracks the
    // held pointer, repeat pause/resume, focus-on-press + AllowFocusOnInteraction, middle-click release routing, the
    // element wheel hook, ActivateOnEnter opt-out, keyboard repeat arming, cancel delivery, touch hover, theme text
    // default, and the PersonPicture geometry contract.
    static void WaveBInputChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);

        // B.1 — pressed visual tracks the pointer while held (ButtonBase_Partial.cpp:629-638): drag-off un-presses,
        // drag-back re-presses, and a release back over the node still clicks.
        {
            var scene = new SceneStore();
            int clicks = 0;
            new TreeReconciler(scene, strings).ReconcileRoot(
                new BoxEl { Width = 40, Height = 20, OnClick = () => clicks++ }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            var node = scene.Root;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(10, 10), 0, 0) });
            bool pressed = (scene.Flags(node) & NodeFlags.Pressed) != 0;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(200, 100), 0, 0) });
            bool offCleared = (scene.Flags(node) & NodeFlags.Pressed) == 0;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(10, 10), 0, 0) });
            bool backPressed = (scene.Flags(node) & NodeFlags.Pressed) != 0;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(10, 10), 0, 0) });
            bool clicked = clicks == 1 && (scene.Flags(node) & NodeFlags.Pressed) == 0;
            Check("B.1 pressed tracks the held pointer (off→clear, back→press, release-over→click)",
                pressed && offCleared && backPressed && clicked,
                $"down={pressed} off={offCleared} back={backPressed} click={clicked}");
        }

        // B.2 — repeat pause/resume hooks fire on drag-off/drag-back while held; ticker honors per-node Delay/Interval
        // with a FRESH delay (no immediate re-fire) on resume (RepeatButton_Partial.cpp:530-574).
        {
            var scene = new SceneStore();
            int clicks = 0;
            new TreeReconciler(scene, strings).ReconcileRoot(
                new BoxEl { Width = 40, Height = 20, Repeats = true, RepeatDelayMs = 80f, RepeatIntervalMs = 30f, OnClick = () => clicks++ }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var ticker = new FluentGpu.Animation.RepeatTicker(scene);
            var disp = new InputDispatcher(scene)
            {
                OnRepeatArmed = ticker.Arm, OnRepeatReleased = ticker.Disarm,
                OnRepeatPaused = ticker.Pause, OnRepeatResumed = ticker.Resume,
            };
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(10, 10), 0, 0) });
            bool armFired = clicks == 1;                       // Arm fires once immediately (ClickMode.Press)
            ticker.Tick(100f);                                 // crosses the 80ms custom delay → second fire
            bool delayHonored = clicks == 2;
            ticker.Tick(60f);                                  // two 30ms intervals
            bool intervalHonored = clicks == 4;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(200, 100), 0, 0) });   // off → pause
            ticker.Tick(500f);
            bool pausedNoFire = clicks == 4 && !ticker.HasActive;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(10, 10), 0, 0) });     // back → resume
            bool resumedNoImmediate = clicks == 4 && ticker.HasActive;   // fresh delay, NO re-fire on re-entry
            ticker.Tick(79f);
            bool freshDelay = clicks == 4;                     // still inside the fresh 80ms delay
            ticker.Tick(2f);
            bool resumedFires = clicks == 5;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(10, 10), 0, 0) });
            bool releaseStops = !ticker.HasActive && clicks == 5;   // release does NOT re-click a repeat node
            Check("B.2 repeat: per-node 80/30 cadence; drag-off pauses; re-entry resumes with a fresh delay",
                armFired && delayHonored && intervalHonored && pausedNoFire && resumedNoImmediate && freshDelay && resumedFires && releaseStops,
                $"arm={armFired} delay={delayHonored} interval={intervalHonored} pause={pausedNoFire} resume={resumedNoImmediate} fresh={freshDelay} fires={resumedFires} stop={releaseStops}");
        }

        // B.3 — pointer focus moves on the PRESS edge (ButtonBase_Partial.cpp:700-709); AllowFocusOnInteraction=false
        // blocks the move entirely while Tab still reaches the node (AppBarButton_themeresources.xaml:136).
        {
            var scene = new SceneStore();
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Direction = 0, Gap = 10,
                Children =
                [
                    new BoxEl { Key = "a", Width = 20, Height = 20, OnClick = () => { } },
                    new BoxEl { Key = "b", Width = 20, Height = 20, OnClick = () => { }, AllowFocusOnInteraction = false },
                ],
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            var a = Child(scene, scene.Root, 0); var b = Child(scene, scene.Root, 1);
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(10, 10), 0, 0) });
            bool focusOnPress = disp.Focused == a;             // BEFORE any release
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(10, 10), 0, 0) });
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(40, 10), 0, 0) });
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(40, 10), 0, 0) });
            bool blocked = disp.Focused == a;                  // press on b never moved focus
            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Tab) });
            bool tabReaches = disp.Focused == b;               // keyboard still reaches it
            Check("B.3 pointer focus on press; AllowFocusOnInteraction=false blocks pointer focus, Tab still works",
                focusOnPress && blocked && tabReaches, $"press={focusOnPress} blocked={blocked} tab={tabReaches}");
        }

        // B.4 — middle-button release over the press target delivers OnPointerPressed with Button=2 (the WinUI
        // TabViewItem middle-click-close commit, TabViewItem.cpp:418-462); release elsewhere delivers nothing.
        {
            var scene = new SceneStore();
            var seen = new List<byte>();
            new TreeReconciler(scene, strings).ReconcileRoot(
                new BoxEl { Width = 40, Height = 20, OnPointerPressed = e => seen.Add(e.Button) }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(10, 10), 2, 0) });
            bool noneOnDown = seen.Count == 0;                 // middle never presses/activates on the down edge
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(10, 10), 2, 0) });
            bool delivered = seen.Count == 1 && seen[0] == 2;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(10, 10), 2, 0) });
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(200, 100), 2, 0) });
            bool offDropped = seen.Count == 1;                 // release elsewhere → no delivery
            Check("B.4 middle-click: typed args Button=2 on release-over-same; nothing on down or off-release",
                noneOnDown && delivered && offDropped, $"down={noneOnDown} hit={delivered} off={offDropped}");
        }

        // B.5 — the element wheel hook sees the wheel BEFORE the viewport and consumes it when Handled
        // (NumberBox.cpp:578-597); an unhandled hook lets the dispatch fall through.
        {
            var scene = new SceneStore();
            float sawDelta = 0f; int calls = 0;
            new TreeReconciler(scene, strings).ReconcileRoot(
                new BoxEl { Width = 40, Height = 20, OnPointerWheel = e => { calls++; sawDelta = e.Delta; e.Handled = true; } }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            int handled = disp.Dispatch(new[] { new InputEvent(InputKind.Wheel, new Point2(10, 10), 0, 0, ScrollDelta: -48f) });
            Check("B.5 element wheel hook consumes the wheel (Handled) with the raw delta",
                handled == 1 && calls == 1 && sawDelta == -48f, $"handled={handled} calls={calls} delta={sawDelta}");
        }

        // B.6 — ActivateOnEnter=false (CheckBox/RadioButton/ToggleSwitch — KeyPress::Button bAcceptsReturn=false):
        // Enter does NOT activate (it routes to OnKeyDown instead); Space still activates on key-up.
        {
            var scene = new SceneStore();
            int clicks = 0, sawKey = 0;
            new TreeReconciler(scene, strings).ReconcileRoot(
                new BoxEl { Width = 40, Height = 20, ActivateOnEnter = false, OnClick = () => clicks++, OnKeyDown = a => sawKey = a.KeyCode }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            disp.MoveFocus(forward: true);
            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Enter) });
            disp.Dispatch(new[] { new InputEvent(InputKind.KeyUp, default, 0, Keys.Enter) });
            bool enterRouted = clicks == 0 && sawKey == Keys.Enter;
            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Space) });
            disp.Dispatch(new[] { new InputEvent(InputKind.KeyUp, default, 0, Keys.Space) });
            bool spaceClicks = clicks == 1;
            Check("B.6 ActivateOnEnter=false: Enter falls through to key routing; Space still toggles",
                enterRouted && spaceClicks, $"enter={enterRouted} (saw={sawKey}) space={spaceClicks}");
        }

        // B.7 — keyboard repeat: a held Space arms the engine repeat timer ONCE (no OS auto-repeat involvement);
        // Enter on a repeat node yields exactly one click on its down edge (RepeatButton_Partial.cpp:212-217, :29).
        {
            var scene = new SceneStore();
            int clicks = 0, armed = 0, released = 0;
            new TreeReconciler(scene, strings).ReconcileRoot(
                new BoxEl { Width = 40, Height = 20, Repeats = true, OnClick = () => clicks++ }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene) { OnRepeatArmed = _ => armed++, OnRepeatReleased = _ => released++ };
            disp.MoveFocus(forward: true);
            var node = disp.Focused;
            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Space) });
            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Space, IsRepeat: true) });
            bool armedOnce = armed == 1 && (scene.Flags(node) & NodeFlags.Pressed) != 0;   // OS repeat ignored
            disp.Dispatch(new[] { new InputEvent(InputKind.KeyUp, default, 0, Keys.Space) });
            bool releasedOnce = released == 1 && clicks == 0;   // ticker owns the clicks; key-up never re-fires
            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Enter) });
            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Enter, IsRepeat: true) });
            disp.Dispatch(new[] { new InputEvent(InputKind.KeyUp, default, 0, Keys.Enter) });
            bool enterOnce = clicks == 1 && armed == 1;         // Enter: ONE direct click, never arms the timer
            Check("B.7 keyboard repeat: Space arms the ticker once; Enter fires exactly one click on a repeat node",
                armedOnce && releasedOnce && enterOnce, $"armed={armedOnce} released={releasedOnce} enter={enterOnce}");
        }

        // B.8 — PointerCancel delivers OnPointerExit to the captured OnDrag target (capture-loss reset — the
        // RatingControl alt-tab mid-sweep case); touch lift clears hover (no resting touch hover).
        {
            var scene = new SceneStore();
            int exits = 0;
            new TreeReconciler(scene, strings).ReconcileRoot(
                new BoxEl { Width = 40, Height = 20, OnDrag = _ => { }, OnPointerExit = () => exits++ }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(10, 10), 0, 0) });
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerCancel, default, 0, 0) });
            bool cancelExit = exits >= 1;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(10, 10), 0, 0, Pointer: PointerKind.Touch) });
            bool hovered = (scene.Flags(scene.Root) & NodeFlags.Hovered) != 0;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(10, 10), 0, 0, Pointer: PointerKind.Touch) });
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(10, 10), 0, 0, Pointer: PointerKind.Touch) });
            bool touchHoverCleared = (scene.Flags(scene.Root) & NodeFlags.Hovered) == 0;
            Check("B.8 cancel delivers exit to the captured drag target; touch lift clears hover",
                cancelExit && hovered && touchHoverCleared, $"exit={cancelExit} hover={hovered} lifted={touchHoverCleared}");
        }

        // B.9 — TextEl's default Color resolves the LIVE theme's TextFillColorPrimary at construction
        // (dark #FFFFFF / light #E4000000) — guards the Tok.TextPrimary default against a hardcoded revert.
        {
            var darkDefault = new TextEl("x").Color;
            Tok.Use(ThemeKind.Light);
            var lightDefault = new TextEl("x").Color;
            Tok.Use(ThemeKind.Dark);
            bool dark = darkDefault.Value == Tok.TextPrimary && darkDefault.Value == ColorF.FromRgba(0xFF, 0xFF, 0xFF);
            bool light = lightDefault.Value == ColorF.FromRgba(0x00, 0x00, 0x00, 0xE4);
            Check("B.9 TextEl default color = theme TextFillColorPrimary (dark #FFFFFF / light #E4000000)",
                dark && light, $"dark={dark} light={light}");
        }

        // B.10 — PersonPicture geometry contract: initials centered in the circle; the badge plate hangs 4px outside
        // the top-right (root UNclipped, left = size+4−plate, top = −4); a negative badge number shows NO badge.
        {
            var scene = new SceneStore();
            new TreeReconciler(scene, strings).ReconcileRoot(PersonPicture.Create("JD", 96f, badgeNumber: 5), null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var root = scene.Root;
            bool unclipped = (scene.Flags(root) & NodeFlags.ClipsToBounds) == 0;
            var face = Child(scene, root, 0);
            var text = Child(scene, face, 0);
            var rootR = scene.AbsoluteRect(root);
            var textR = scene.AbsoluteRect(text);
            bool centered = MathF.Abs((textR.X + textR.W / 2f) - (rootR.X + 48f)) <= 1f
                         && MathF.Abs((textR.Y + textR.H / 2f) - (rootR.Y + 48f)) <= 1f;
            var badge = Child(scene, root, 1);
            var badgeR = scene.AbsoluteRect(badge);
            bool badgePos = MathF.Abs(badgeR.X - rootR.X - 52f) <= 0.5f && MathF.Abs(badgeR.Y - rootR.Y + 4f) <= 0.5f
                         && MathF.Abs(badgeR.W - 48f) <= 0.5f;

            var scene2 = new SceneStore();
            new TreeReconciler(scene2, strings).ReconcileRoot(PersonPicture.Create("JD", 96f, badgeNumber: -3, badgeGlyph: ""), null);
            bool negativeNoBadge = Child(scene2, scene2.Root, 1).IsNull;   // number<0 owns the slot → NO badge, glyph ignored
            Check("B.10 PersonPicture: centered initials; badge at (52,−4) 48px on an unclipped root; negative number = no badge",
                unclipped && centered && badgePos && negativeNoBadge,
                $"unclipped={unclipped} centered={centered} badge={badgePos} negNone={negativeNoBadge}");
        }
    }

    static void E5DragDropChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);

        // e5dragdrop.1 — a press that never leaves the 4px per-axis drag box stays a plain click: release fires
        // OnClick, no drag lifecycle event fires, and the node's transform is untouched. +4/+4 sits ON the box edge —
        // WinUI promotes only strictly OUTSIDE it (dx > maxDx || dy > maxDy, ListViewBaseItem_Partial.cpp:1877).
        {
            var scene = new SceneStore();
            int clicks = 0, started = 0, deltas = 0, completed = 0, canceled = 0;
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Width = 200, Height = 60, CanDrag = true,
                OnClick = () => clicks++,
                OnDragStarted = _ => started++,
                OnDragDelta = _ => deltas++,
                OnDragCompleted = _ => completed++,
                OnDragCanceled = () => canceled++,
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            var node = scene.Root;

            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(100, 30), 0, 0) });
            bool armed = disp.Drag.IsArmed && !disp.Drag.IsActive;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(104, 34), 0, 0) });
            bool stillArmed = disp.Drag.IsArmed && !disp.Drag.IsActive && started == 0;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(104, 34), 0, 0) });
            bool clicked = clicks == 1 && started == 0 && deltas == 0 && completed == 0 && canceled == 0;
            bool untouched = scene.Paint(node).LocalTransform.Dx == 0f && !disp.Drag.IsArmed && !disp.Drag.IsActive;
            Check("e5dragdrop.1 press inside the 4px per-axis drag box stays a click (no drag lifecycle)",
                armed && stillArmed && clicked && untouched,
                $"armed={armed} stillArmed={stillArmed} clicks={clicks} started={started}");
        }

        // e5dragdrop.2/.2b — crossing the drag box on a press that began on a CHILD of the CanDrag row promotes the
        // ROW (TryArm walks up like WinUI's item container): OnDragStarted fires once BEFORE the first OnDragDelta,
        // the transient pressed visuals are cleared, and the row carries the drag visuals — opacity 0.80
        // (ListViewItemDragThemeOpacity — ListViewItem_themeresources.xaml:7), the flyout-class shadow, hit-test off,
        // and a parent-space translate equal to the gesture delta. Release restores everything, fires DragCompleted,
        // SUPPRESSES the click, and hands OnSettle the (drop → resting) rects for the FLIP glide.
        {
            var scene = new SceneStore();
            int rowClicks = 0, childClicks = 0, started = 0, deltas = 0, completed = 0;
            int firstEvent = 0;                       // 1 = started first, 2 = delta first — order proof
            float doneDx = 0f, doneDy = 0f;
            NodeHandle settleNode = default; RectF settleFrom = default, settleTo = default; int settles = 0;
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Width = 240, Height = 120,
                Children =
                [
                    new BoxEl
                    {
                        Key = "row", Width = 200, Height = 60, CanDrag = true,
                        OnClick = () => rowClicks++,
                        OnDragStarted = _ => { started++; if (firstEvent == 0) firstEvent = 1; },
                        OnDragDelta = _ => { deltas++; if (firstEvent == 0) firstEvent = 2; },
                        OnDragCompleted = e => { completed++; doneDx = e.TotalDx; doneDy = e.TotalDy; },
                        Children = [new BoxEl { Key = "child", Width = 80, Height = 30, OnClick = () => childClicks++ }],
                    },
                ],
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            disp.Drag.OnSettle = (n, from, to) => { settles++; settleNode = n; settleFrom = from; settleTo = to; };
            var row = Child(scene, scene.Root, 0);
            var child = Child(scene, row, 0);

            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(40, 15), 0, 0) });
            bool pressedChild = (scene.Flags(child) & NodeFlags.Pressed) != 0 && disp.Drag.IsArmed;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(50, 15), 0, 0) });   // dx 10 > 4 → promote
            bool promoted = started == 1 && deltas == 1 && firstEvent == 1
                && disp.Drag.IsActive && disp.Drag.ActiveNode == row
                && (scene.Flags(child) & NodeFlags.Pressed) == 0;                  // pressed visuals cleared on promotion
            bool visuals = Near(scene.Paint(row).Opacity, 0.80f)                   // ListViewItemDragThemeOpacity
                && scene.TryGetShadow(row, out var sh) && sh == DragController.DragShadow
                && (scene.Flags(row) & NodeFlags.HitTestVisible) == 0              // drop-targets see THROUGH the visual
                && Near(scene.Paint(row).LocalTransform.Dx, 10f) && Near(scene.Paint(row).LocalTransform.Dy, 0f);
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(70, 40), 0, 0) });
            bool follows = deltas == 2
                && Near(scene.Paint(row).LocalTransform.Dx, 30f) && Near(scene.Paint(row).LocalTransform.Dy, 25f);
            Check("e5dragdrop.2 over-threshold promotes the CanDrag row (child press arms it): Started→Delta, pressed cleared, drag visuals on",
                pressedChild && promoted && visuals && follows,
                $"pressed={pressedChild} promoted={promoted} visuals={visuals} follows={follows}");

            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(70, 40), 0, 0) });
            bool suppressed = rowClicks == 0 && childClicks == 0;                  // WinUI: a finished drag never clicks
            bool restored = Near(scene.Paint(row).Opacity, 1f) && !scene.TryGetShadow(row, out _)
                && (scene.Flags(row) & NodeFlags.HitTestVisible) != 0
                && scene.Paint(row).LocalTransform.Dx == 0f && scene.Paint(row).LocalTransform.Dy == 0f
                && !disp.Drag.IsActive && disp.Drag.ActiveNode.IsNull;
            bool settled = completed == 1 && Near(doneDx, 30f) && Near(doneDy, 25f)
                && settles == 1 && settleNode == row
                && Near(settleFrom.X - settleTo.X, 30f) && Near(settleFrom.Y - settleTo.Y, 25f);
            Check("e5dragdrop.2b release after a drag suppresses the click, restores resting visuals, and hands OnSettle the drop→resting rects",
                suppressed && restored && settled,
                $"suppressed={suppressed} restored={restored} completed={completed} settles={settles} dxdy=({doneDx:0.#},{doneDy:0.#})");
        }

        // e5dragdrop.3 — DragEventArgs: Total deltas measured from the arming press, Absolute = the raw pointer,
        // Local ≈ the grab offset on the MOVING box, and the ~50ms-EMA pointer velocity driven by PLATFORM timestamps
        // (alpha = dt/(dt+50): 10px/16ms moves → 625 px/s instantaneous → 151.5 then 266.3 px/s smoothed). A gesture
        // whose events carry TimestampMs == 0 (the headless default) leaves the velocity at 0.
        {
            var scene = new SceneStore();
            float vx = float.NaN, vy = float.NaN, dx = float.NaN, dy = float.NaN;
            Point2 local = default, abs = default;
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Width = 200, Height = 100, CanDrag = true,
                OnDragDelta = e => { vx = e.VelocityX; vy = e.VelocityY; dx = e.TotalDx; dy = e.TotalDy; local = e.Local; abs = e.Absolute; },
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);

            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(50, 50), 0, 0, 0f, KeyModifiers.None, PointerKind.Mouse, false, 1_000) });
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(60, 50), 0, 0, 0f, KeyModifiers.None, PointerKind.Mouse, false, 1_016) });
            bool firstMove = Near(dx, 10f) && Near(vx, 151.5f, 0.5f) && vy == 0f;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(70, 50), 0, 0, 0f, KeyModifiers.None, PointerKind.Mouse, false, 1_032) });
            bool secondMove = Near(dx, 20f) && Near(dy, 0f) && Near(vx, 266.3f, 0.5f)
                && Near(abs.X, 70f) && Near(abs.Y, 50f)
                && Near(local.X, 50f) && Near(local.Y, 50f);   // grab offset: Local tracks the MOVING box
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(70, 50), 0, 0, 0f, KeyModifiers.None, PointerKind.Mouse, false, 1_048) });

            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(50, 50), 0, 0) });
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(80, 50), 0, 0) });
            bool zeroStamp = Near(dx, 30f) && vx == 0f && vy == 0f;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(80, 50), 0, 0) });
            Check("e5dragdrop.3 DragEventArgs coords + ~50ms-EMA velocity from platform timestamps (0-stamps leave velocity 0)",
                firstMove && secondMove && zeroStamp, $"first={firstMove} second={secondMove} zero={zeroStamp} vx={vx:0.#}");
        }

        // e5dragdrop.4 — cancel paths: Escape mid-drag (the most-modal gesture — WinUI drag cancel routes before any
        // other key handling) and window deactivation both abort the drag: resting visuals restore, OnDragCanceled
        // fires (never OnDragCompleted), OnSettle glides the visual home, and the still-down pointer's eventual
        // release does NOT click (a canceled drag never raises a click or a drop).
        {
            var scene = new SceneStore();
            int clicks = 0, canceled = 0, completed = 0, settles = 0;
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Width = 200, Height = 60, CanDrag = true,
                OnClick = () => clicks++,
                OnDragCompleted = _ => completed++,
                OnDragCanceled = () => canceled++,
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            var node = scene.Root;
            disp.Drag.OnSettle = (_, _, _) => settles++;

            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(50, 30), 0, 0) });
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(70, 30), 0, 0) });
            bool active1 = disp.Drag.IsActive && Near(scene.Paint(node).LocalTransform.Dx, 20f);
            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Escape) });
            bool escCancel = canceled == 1 && completed == 0 && settles == 1 && !disp.Drag.IsActive
                && scene.Paint(node).LocalTransform.Dx == 0f && Near(scene.Paint(node).Opacity, 1f)
                && !scene.TryGetShadow(node, out _) && (scene.Flags(node) & NodeFlags.HitTestVisible) != 0;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(70, 30), 0, 0) });
            bool noClick1 = clicks == 0;

            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(50, 30), 0, 0) });
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(75, 30), 0, 0) });
            bool active2 = disp.Drag.IsActive;
            disp.Dispatch(new[] { new InputEvent(InputKind.WindowBlur, default, 0, 0) });
            bool blurCancel = canceled == 2 && settles == 2 && !disp.Drag.IsActive
                && scene.Paint(node).LocalTransform.Dx == 0f && Near(scene.Paint(node).Opacity, 1f);
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(75, 30), 0, 0) });
            bool noClick2 = clicks == 0 && completed == 0;
            Check("e5dragdrop.4 Escape / window-blur cancel the drag: visuals restore, DragCanceled fires, release does not click",
                active1 && escCancel && noClick1 && active2 && blurCancel && noClick2,
                $"esc={escCancel} blur={blurCancel} clicks={clicks} canceled={canceled} settles={settles}");
        }

        // e5dragdrop.5 — arena-lite (promotion-time arbitration, DragController.YieldsToPan): the item's reorder axis
        // is its PARENT container's main axis; a dominant-axis gesture PERPENDICULAR to it yields to a scrollable
        // ancestor that actually overflows along the gesture axis (the WinUI manipulation-arena outcome for a tab
        // strip inside a scrolling page) — the candidate silently disarms, no DragStarted. Along-axis gestures and
        // no-overflow scrollables never yield.
        {
            // a) horizontal strip (row ⇒ items drag horizontally) inside a vertically OVERFLOWING scroll viewport.
            var sceneA = new SceneStore();
            int startedA = 0;
            new TreeReconciler(sceneA, strings).ReconcileRoot(new ScrollEl
            {
                Width = 200, Height = 100,
                Content = new BoxEl
                {
                    Direction = 1,
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 0,
                            Children =
                            [
                                new BoxEl { Key = "a", Width = 60, Height = 40, CanDrag = true, OnDragStarted = _ => startedA++ },
                                new BoxEl { Key = "b", Width = 60, Height = 40, CanDrag = true, OnDragStarted = _ => startedA++ },
                                new BoxEl { Key = "c", Width = 60, Height = 40, CanDrag = true, OnDragStarted = _ => startedA++ },
                            ],
                        },
                        new BoxEl { Key = "filler", Width = 10, Height = 300 },
                    ],
                },
            }, null);
            new FlexLayout(sceneA, fonts).Run(sceneA.Root);
            var dispA = new InputDispatcher(sceneA);
            sceneA.TryGetScroll(sceneA.Root, out var scA);
            bool overflows = scA.ContentH - scA.ViewportH > 0.5f;   // 340 content over a 100 viewport

            dispA.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(30, 20), 0, 0) });
            bool armedA = dispA.Drag.IsArmed;
            dispA.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(30, 60), 0, 0) });   // dy 40 ⊥ the row axis
            bool yielded = startedA == 0 && !dispA.Drag.IsActive && !dispA.Drag.IsArmed;                 // the pan owns it
            dispA.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(30, 60), 0, 0) });

            dispA.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(30, 20), 0, 0) });
            dispA.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(80, 20), 0, 0) });   // dx 50 along the row axis
            bool alongDrags = startedA == 1 && dispA.Drag.IsActive;
            dispA.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Escape) });

            // b) the same strip in a NON-overflowing viewport: the vertical gesture has no pan to yield to → it drags.
            var sceneB = new SceneStore();
            int startedB = 0;
            new TreeReconciler(sceneB, strings).ReconcileRoot(new ScrollEl
            {
                Width = 200, Height = 100,
                Content = new BoxEl
                {
                    Direction = 0,
                    Children =
                    [
                        new BoxEl { Key = "a", Width = 60, Height = 40, CanDrag = true, OnDragStarted = _ => startedB++ },
                        new BoxEl { Key = "b", Width = 60, Height = 40, CanDrag = true, OnDragStarted = _ => startedB++ },
                    ],
                },
            }, null);
            new FlexLayout(sceneB, fonts).Run(sceneB.Root);
            var dispB = new InputDispatcher(sceneB);
            dispB.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(30, 20), 0, 0) });
            dispB.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(30, 60), 0, 0) });
            bool noOverflowDrags = startedB == 1 && dispB.Drag.IsActive;
            dispB.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Escape) });

            // c) a vertical (column) list inside the overflowing vertical viewport: the vertical gesture runs ALONG
            //    the item's own reorder axis → the drag wins even over a real pan candidate.
            var sceneC = new SceneStore();
            int startedC = 0;
            new TreeReconciler(sceneC, strings).ReconcileRoot(new ScrollEl
            {
                Width = 200, Height = 100,
                Content = new BoxEl
                {
                    Direction = 1,
                    Children =
                    [
                        new BoxEl { Key = "a", Width = 160, Height = 60, CanDrag = true, OnDragStarted = _ => startedC++ },
                        new BoxEl { Key = "b", Width = 160, Height = 60, CanDrag = true, OnDragStarted = _ => startedC++ },
                        new BoxEl { Key = "c", Width = 160, Height = 60, CanDrag = true, OnDragStarted = _ => startedC++ },
                    ],
                },
            }, null);
            new FlexLayout(sceneC, fonts).Run(sceneC.Root);
            var dispC = new InputDispatcher(sceneC);
            sceneC.TryGetScroll(sceneC.Root, out var scC);
            bool overflowsC = scC.ContentH - scC.ViewportH > 0.5f;   // 180 content over a 100 viewport
            dispC.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(80, 30), 0, 0) });
            dispC.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(80, 70), 0, 0) });
            bool axisDrags = startedC == 1 && dispC.Drag.IsActive;
            dispC.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Escape) });

            Check("e5dragdrop.5 arena-lite: cross-axis gesture over an overflowing scrollable yields to the pan; along-axis and no-overflow gestures drag",
                overflows && armedA && yielded && alongDrags && noOverflowDrags && overflowsC && axisDrags,
                $"overflow={overflows} yielded={yielded} along={alongDrags} noOverflow={noOverflowDrags} axis={axisDrags}");
        }

        // e5dragdrop.6 — ReorderList midpoint slot math: the dragged item's centre crossing a sibling's midpoint
        // claims its slot (GetDragOverIndex — ListViewBase_Partial_Reorder.cpp:984-1063); the SHOWN target waits on
        // the 200ms live-reorder dwell that re-arms on every pending change (LISTVIEW_LIVEREORDER_TIMER :50, restart
        // :1068-1074); displaced items shift one dragged-extent (+spacing) toward the vacated slot.
        {
            var rl = new ReorderList();
            bool defaults = rl.DwellMs == ReorderList.ListDwellMs && ReorderList.ListDwellMs == 200f && ReorderList.GridDwellMs == 300f;
            rl.Begin(1, 5, itemExtent: 40f, spacing: 8f);     // starts 0,48,96,144,192; dragged centre 68
            bool init = rl.IsActive && rl.DraggedIndex == 1 && rl.PendingIndex == 1 && rl.TargetIndex == 1;
            bool stay = !rl.Update(47f) && rl.PendingIndex == 1;                       // 115 < sibling-2 mid 116
            bool cross = rl.Update(49f) && rl.PendingIndex == 2 && rl.TargetIndex == 1;   // 117 > 116; dwell pending
            bool dwellHeld = !rl.Advance(199f) && rl.TargetIndex == 1;
            bool dwellFire = rl.Advance(1f) && rl.TargetIndex == 2;
            bool hints = Near(rl.OffsetFor(2), -48f) && rl.OffsetFor(0) == 0f && rl.OffsetFor(1) == 0f
                && rl.OffsetFor(3) == 0f && rl.OffsetFor(4) == 0f;
            Span<int> order = stackalloc int[5];
            rl.ProjectOrder(order);
            bool proj = order[0] == 0 && order[1] == 2 && order[2] == 1 && order[3] == 3 && order[4] == 4;
            bool tgtStart = Near(rl.DraggedTargetStart, 96f);

            bool flip = rl.Update(-100f) && rl.PendingIndex == 0 && rl.TargetIndex == 2;   // centre −32 < sibling-0 mid 20
            bool reArm = !rl.Advance(199f) && rl.Advance(1f) && rl.TargetIndex == 0;       // the dwell re-armed in full
            bool upHints = Near(rl.OffsetFor(0), 48f) && rl.OffsetFor(2) == 0f;
            rl.ProjectOrder(order);
            bool upProj = order[0] == 1 && order[1] == 0 && order[2] == 2 && Near(rl.DraggedTargetStart, 0f);
            Check("e5dragdrop.6 ReorderList midpoint slot math + 200ms dwell-committed target + displacement hints + ProjectOrder",
                defaults && init && stay && cross && dwellHeld && dwellFire && hints && proj && tgtStart && flip && reArm && upHints && upProj,
                $"init={init} stay={stay} cross={cross} dwell={dwellHeld}/{dwellFire} hints={hints} proj={proj} flip={flip} reArm={reArm}");
        }

        // e5dragdrop.7 — drop commit: Complete() lands at the LATEST pending slot (the release point never waits for
        // the dwell), resets all hints BEFORE firing OnCommit (from,to in ORIGINAL indices), and ReorderList.Move
        // applies exactly WinUI's RemoveAt(from)+Insert(to) drop (ListViewBase::ReorderItemsTo —
        // ListViewBase_Partial_Reorder.cpp:1536-1537). Cancel drops the hints without committing. Variable extents
        // honor per-item midpoints; DwellMs = 0 commits the shown target on the next Advance.
        {
            var rl = new ReorderList { DwellMs = 0f };
            rl.Begin(0, new[] { 30f, 50f, 20f }, spacing: 4f);    // starts 0,34,88; dragged centre 15
            bool varStay = !rl.Update(40f);                       // 55 < sibling-1 mid 59
            bool varCross = rl.Update(45f) && rl.PendingIndex == 1;   // 60 > 59
            bool zeroDwell = rl.Advance(0f) && rl.TargetIndex == 1 && Near(rl.OffsetFor(1), -34f);
            rl.Cancel();
            bool dropped = !rl.IsActive && rl.OffsetFor(1) == 0f && rl.PendingIndex == -1;

            int commitFrom = -1, commitTo = -1; bool hintsClearedAtCommit = false;
            var rl2 = new ReorderList();
            rl2.OnCommit = (from, to) => { commitFrom = from; commitTo = to; hintsClearedAtCommit = rl2.TargetIndex == -1 && rl2.OffsetFor(1) == 0f; };
            rl2.Begin(0, 4, itemExtent: 40f);                     // starts 0,40,80,120; dragged centre 20
            rl2.Update(85f);                                      // 105 > mid-1 60 and > mid-2 100 → pending 2 (no Advance)
            int dest = rl2.Complete();
            bool commit = dest == 2 && commitFrom == 0 && commitTo == 2 && hintsClearedAtCommit && !rl2.IsActive;

            var list = new List<char> { 'a', 'b', 'c', 'd' };
            ReorderList.Move(list, 0, 2);
            bool moved = list[0] == 'b' && list[1] == 'c' && list[2] == 'a' && list[3] == 'd';
            ReorderList.Move(list, 0, 9);                         // out of range → ignored
            ReorderList.Move(list, 2, 2);                         // no-op
            bool guarded = list[0] == 'b' && list[1] == 'c' && list[2] == 'a' && list[3] == 'd';
            Check("e5dragdrop.7 Complete commits at the latest pending slot (hints reset before OnCommit); Move applies RemoveAt+Insert; Cancel drops",
                varStay && varCross && zeroDwell && dropped && commit && moved && guarded,
                $"varCross={varCross} zeroDwell={zeroDwell} dropped={dropped} dest={dest} commit=({commitFrom}->{commitTo}) moved={moved}");
        }

        // e5dragdrop.7b — the full pipeline: CanDrag rows wired to ReorderList through the drag lifecycle (Begin in
        // OnDragStarted, Update(e.TotalDy) in OnDragDelta, Complete in OnDragCompleted); dragging row 0 past row 1's
        // midpoint and releasing commits the collection move 0→1 via OnCommit + ReorderList.Move.
        {
            var scene = new SceneStore();
            var items = new List<int> { 0, 1, 2 };
            var rl = new ReorderList();
            rl.OnCommit = (from, to) => ReorderList.Move(items, from, to);
            var children = new Element[3];
            for (int i = 0; i < 3; i++)
            {
                int idx = i;
                children[i] = new BoxEl
                {
                    Key = "row" + i, Width = 120, Height = 40, CanDrag = true,
                    OnDragStarted = _ => rl.Begin(idx, 3, itemExtent: 40f),
                    OnDragDelta = e => rl.Update(e.TotalDy),
                    OnDragCompleted = _ => rl.Complete(),
                    OnDragCanceled = rl.Cancel,
                };
            }
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl { Direction = 1, Children = children }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);

            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(60, 20), 0, 0) });
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(60, 40), 0, 0) });   // promote; centre 40 < mid-1 60
            bool pendingHome = rl.IsActive && rl.DraggedIndex == 0 && rl.PendingIndex == 0;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(60, 67), 0, 0) });   // centre 67 > mid-1 60 → pending 1
            bool pendingNext = rl.PendingIndex == 1;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(60, 67), 0, 0) });
            bool committed = !rl.IsActive && items[0] == 1 && items[1] == 0 && items[2] == 2;
            Check("e5dragdrop.7b end-to-end: dragging row 0 past row 1's midpoint commits the reorder through the drag lifecycle",
                pendingHome && pendingNext && committed,
                $"home={pendingHome} next={pendingNext} items=[{string.Join(",", items)}]");
        }

        // e5dragdrop.8 — steady-state drag dispatch is allocation-free: the controller reuses ONE DragEventArgs for
        // the whole gesture and the move path writes only scene columns (no closures, no boxing).
        {
            var scene = new SceneStore();
            float lastDx = 0f;
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Width = 400, Height = 60, CanDrag = true, OnDragDelta = e => lastDx = e.TotalDx,
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            var ev = new InputEvent[1];
            ev[0] = new InputEvent(InputKind.PointerDown, new Point2(50, 30), 0, 0, 0f, KeyModifiers.None, PointerKind.Mouse, false, 1_000);
            disp.Dispatch(ev);
            for (int i = 1; i <= 6; i++)   // promote + warm the move path (shadow row, EMA, transform writes)
            {
                ev[0] = new InputEvent(InputKind.PointerMove, new Point2(50 + i * 10, 30), 0, 0, 0f, KeyModifiers.None, PointerKind.Mouse, false, 1_000 + (uint)(i * 16));
                disp.Dispatch(ev);
            }
            ev[0] = new InputEvent(InputKind.PointerMove, new Point2(140, 30), 0, 0, 0f, KeyModifiers.None, PointerKind.Mouse, false, 1_200);
            long before = GC.GetAllocatedBytesForCurrentThread();
            disp.Dispatch(ev);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Check("e5dragdrop.8 steady drag-move dispatch allocates 0 bytes (one reused DragEventArgs per gesture)",
                bytes == 0 && Near(lastDx, 90f), $"{bytes} bytes dx={lastDx:0.#}");
        }

        // e5dragdrop.8b — the whole drag FRAME at pointer rate is 0-alloc on phases 6–13: a drag move never
        // reconciles or relayouts (LocalTransform + dirty flags only), and the record/submit of the lifted visual
        // (0.80 opacity + shadow) reuses pooled storage.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("e5-alloc", new Size2(480, 320), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var root = new DragFrameProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);

            host.RunFrame();   // mount + layout
            var item = Child(host.Scene, host.Scene.Root, 0);
            var c = CenterOf(host.Scene, item);
            window.QueueInput(new InputEvent(InputKind.PointerDown, c, 0, 0, 0f, KeyModifiers.None, PointerKind.Mouse, false, 1_000));
            host.RunFrame();
            for (int i = 1; i <= 12; i++)   // promote, then warm: shadow slab, draw-list growth, eased press/hover settle
            {
                window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(c.X + i * 4, c.Y), 0, 0, 0f, KeyModifiers.None, PointerKind.Mouse, false, 1_000 + (uint)(i * 16)));
                host.RunFrame();
            }
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(c.X + 60, c.Y), 0, 0, 0f, KeyModifiers.None, PointerKind.Mouse, false, 1_300));
            var dragFrame = host.RunFrame();
            bool zero = dragFrame.HotPhaseAllocBytes == 0;
            // E5b spring-lag follow (the adopted Flutter/rbd ghost feel): with real timestamps the lifted visual EASES
            // toward the pointer instead of snapping, so the transform reaches 60 only once the spring settles —
            // assert the SETTLED position, while the 0-alloc gate stays on the immediate pointer-rate drag frame.
            for (int i = 0; i < 30; i++) host.RunFrame();
            bool tracked = Near(root.LastTotalDx, 60f) && Near(host.Scene.Paint(item).LocalTransform.Dx, 60f, 1.5f)
                && Near(host.Scene.Paint(item).Opacity, 0.80f);
            Check("e5dragdrop.8b steady drag frame is 0-alloc on phases 6–13 (transform-only repaint of the lifted visual)",
                zero && tracked, $"{dragFrame.HotPhaseAllocBytes} bytes dx={root.LastTotalDx:0.#} tdx={host.Scene.Paint(item).LocalTransform.Dx:0.#}");
        }
    }

    // E1 — the WinUI focus visual: 2px primary + 1px secondary BOTH outside the bounds (FocusVisualMargin −3 default),
    // keyboard-only, margin-aware (Slider −7,0,−7,0), light-theme inner alpha #B3FFFFFF.
    static void FocusRingChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("focusring", new Size2(300, 200), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var root = new FocusRingProbe();
        using var host = new AppHost(app, window, device, fonts, strings, root);
        host.RunFrame();

        // Pointer focus: click the button — NO ring may appear (keyboard-only focus visuals).
        var btn = Child(host.Scene, host.Scene.Root, 0);
        ClickNode(host, window, btn);
        host.RunFrame();
        bool pointerSilent = device.LastStrokes.Count == 0;

        // Keyboard focus: the click focused button1, so Tab lands on button2 — the ASYMMETRIC margin (−7,0,−7,0):
        // 100×40 ⇒ focus rect (−7,0,114,40); primary centerline inset 1 ⇒ (−6,1,112,38).
        window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Tab));
        host.RunFrame();
        DrawRoundRectStrokeCmd p2 = default;
        foreach (var s in device.LastStrokes) if (Near(s.StrokeWidth, 2f)) p2 = s;
        bool asym = Near(p2.Rect.X, -6f) && Near(p2.Rect.Y, 1f) && Near(p2.Rect.W, 112f) && Near(p2.Rect.H, 38f);
        Check("E1.b FocusVisualMargin −7,0,−7,0 widens the ring horizontally only (Slider shape)", asym,
            $"prim=({p2.Rect.X:0.#},{p2.Rect.Y:0.#} {p2.Rect.W:0.#}x{p2.Rect.H:0.#})");

        // Second Tab wraps to button1 — the DEFAULT margin −3: focus rect (−3,−3,106,46); primary inset 1; secondary 2.5.
        window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Tab));
        host.RunFrame();
        DrawRoundRectStrokeCmd primary = default, secondary = default;
        foreach (var s in device.LastStrokes)
        {
            if (Near(s.StrokeWidth, 2f)) primary = s;
            else if (Near(s.StrokeWidth, 1f)) secondary = s;
        }
        bool primaryGeom = Near(primary.Rect.X, -2f) && Near(primary.Rect.Y, -2f) && Near(primary.Rect.W, 104f) && Near(primary.Rect.H, 44f);
        bool secondaryGeom = Near(secondary.Rect.X, -0.5f) && Near(secondary.Rect.Y, -0.5f) && Near(secondary.Rect.W, 101f) && Near(secondary.Rect.H, 41f);
        bool colors = ColorClose(primary.Color, Tok.FocusOuter, 0.004f) && ColorClose(secondary.Color, Tok.FocusInner, 0.004f);
        Check("E1.a keyboard focus draws the WinUI dual ring OUTSIDE the bounds (pointer focus stays bare)",
            pointerSilent && primaryGeom && secondaryGeom && colors,
            $"ptr={pointerSilent} prim=({primary.Rect.X:0.#},{primary.Rect.Y:0.#} {primary.Rect.W:0.#}x{primary.Rect.H:0.#}) " +
            $"sec=({secondary.Rect.X:0.#},{secondary.Rect.Y:0.#} {secondary.Rect.W:0.#}x{secondary.Rect.H:0.#}) colors={colors}");

        // Light theme: FocusStrokeColorInner is #B3FFFFFF (white @ 0.70) — the audit's alpha fix.
        var light = Tok.Light.FocusInner;
        Check("E1.c Light FocusStrokeColorInner = #B3FFFFFF (alpha corrected)",
            ColorClose(light, ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0xB3), 0.004f),
            $"A={light.A:0.###} R={light.R:0.###}");

        // E1.d — a focused ClipsToBounds control (a TextBox field) must NOT scissor away its own ring: the ring is
        // recorded AFTER the node's clip pops, so its strokes decode at the PARENT clip depth (0 here), full geometry.
        {
            using var app2 = new HeadlessPlatformApp();
            var window2 = new HeadlessWindow(new WindowDesc("focusclip", new Size2(300, 200), 1f));
            window2.Show();
            var device2 = new HeadlessGpuDevice();
            var root2 = new FocusClipProbe();
            using var host2 = new AppHost(app2, window2, device2, fonts, strings, root2);
            host2.RunFrame();
            window2.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Tab));
            host2.RunFrame();
            bool ringFound = false, outsideClip = true, geom = false;
            for (int i = 0; i < device2.LastStrokes.Count; i++)
            {
                var s = device2.LastStrokes[i];
                if (!Near(s.StrokeWidth, 2f)) continue;
                ringFound = true;
                outsideClip &= device2.LastStrokeClipDepths[i] == 0;
                geom = Near(s.Rect.X, -2f) && Near(s.Rect.W, 104f);
            }
            Check("E1.d focus ring escapes the focused node's OWN ClipsToBounds scissor (clipped-TextBox-ring fix)",
                ringFound && outsideClip && geom && device2.LastClips.Count > 0,
                $"found={ringFound} depth0={outsideClip} geom={geom} clips={device2.LastClips.Count}");
        }
    }

    // E1.d — one clipping, clickable field (the EditableText shape: ClipToBounds + focusable).
    sealed class FocusClipProbe : Component
    {
        public override Element Render() => new BoxEl
        {
            Padding = Edges4.All(30),
            Children =
            [
                new BoxEl
                {
                    Width = 100, Height = 40, Fill = ColorF.FromRgba(0x20, 0x20, 0x20),
                    ClipToBounds = true, OnClick = () => { },
                    Children = [new TextEl("clip") { Size = 12f }],
                },
            ],
        };
    }

    // W0a — the clipboard + IME PAL seams (headless fakes drive the full composition lifecycle deterministically).
    static void TextServicesSeamChecks()
    {
        using var app = new HeadlessPlatformApp();
        var clip = app.Clipboard;
        uint seq0 = clip.SequenceNumber;
        clip.SetText("héllo ✂");
        bool roundTrip = clip.TryGetText(out var read) && read == "héllo ✂" && clip.SequenceNumber == seq0 + 1;
        ((HeadlessClipboard)clip).Clear();
        bool cleared = !clip.TryGetText(out _) && clip.SequenceNumber == seq0 + 2;
        Check("W0a.1 clipboard seam: unicode round-trip + epoch bumps + clear", roundTrip && cleared,
            $"read='{read}' seq={clip.SequenceNumber - seq0}");

        var window = new HeadlessWindow(new WindowDesc("ime", new Size2(100, 100), 1f));
        var ti = (HeadlessTextInput)window.TextInput;
        var sink = new RecordingSink();
        ti.SetSink(sink);
        ti.BeginComposition();                       // not editable yet → must no-op
        bool gated = sink.Log.Count == 0;
        ti.SetEditable(true);
        ti.BeginComposition();
        ti.UpdateComposition("にほ", 2, new ImeClause(0, 2, ImeClauseKind.Input));
        ti.UpdateComposition("日本", 2, new ImeClause(0, 2, ImeClauseKind.TargetConverted));
        ti.Commit("日本");
        bool flow = sink.Log.Count == 5 && sink.Log[0] == "start" && sink.Log[1] == "upd:にほ@2:Input"
                    && sink.Log[2] == "upd:日本@2:TargetConverted" && sink.Log[3] == "commit:日本" && sink.Log[4] == "end";
        sink.Log.Clear();
        ti.BeginComposition();
        ti.UpdateComposition("か", 1, new ImeClause(0, 1, ImeClauseKind.Input));
        ti.SetEditable(false);                       // focus leaves the editor mid-composition → cancel (empty update + end)
        bool cancelled = sink.Log.Count == 4 && sink.Log[2] == "upd:@0:" && sink.Log[3] == "end";
        Check("W0a.2 IME seam: editable gate, composition lifecycle, cancel-on-blur", gated && flow && cancelled,
            $"gated={gated} flow={flow} cancel={cancelled} log=[{string.Join("|", sink.Log)}]");
    }

    private sealed class RecordingSink : FluentGpu.Pal.ITextInputSink
    {
        public readonly List<string> Log = new();
        public void OnCompositionStart() => Log.Add("start");
        public void OnCompositionUpdate(ReadOnlySpan<char> text, int caret, ReadOnlySpan<FluentGpu.Pal.ImeClause> clauses)
            => Log.Add($"upd:{text.ToString()}@{caret}:{(clauses.Length > 0 ? clauses[0].Kind.ToString() : "")}");
        public void OnCompositionCommit(ReadOnlySpan<char> text) => Log.Add($"commit:{text.ToString()}");
        public void OnCompositionEnd() => Log.Add("end");
    }

    // W0e — EditableText rebuilt on TextEditCore: the full WinUI editing matrix, exercised headlessly against the
    // deterministic advance model (advance = 0.55×size, line = 1.4×size @ FontSize 14) — geometry asserts are exact math.
    static void EditableTextCoreChecks(StringTable strings)
    {
        const float Adv = 14f * 0.55f;     // 7.7 dip per UTF-16 unit
        const float LineH = 14f * 1.4f;    // 19.6 dip per line

        static NodeHandle TextVisual(SceneStore s, NodeHandle n)
        {
            if (n.IsNull) return NodeHandle.Null;
            if (s.Paint(n).VisualKind == VisualKind.Text) return n;
            for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
            {
                var r = TextVisual(s, c);
                if (!r.IsNull) return r;
            }
            return NodeHandle.Null;
        }

        // ── "hello world": caret click, keyboard selection, double/triple click, drag rects (+0-alloc), clipboard ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0e", new Size2(420, 240), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0eProbe { Initial = "hello world" };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            var field = FindRole(scene, scene.Root, AutomationRole.Text);
            var tn = TextVisual(scene, field);
            var ta = scene.AbsoluteRect(tn);

            void Press(float x, float y, uint t, KeyModifiers mods = KeyModifiers.None)
            {
                window.QueueInput(new InputEvent(InputKind.PointerDown, new Point2(x, y), 0, 0, 0f, mods, PointerKind.Mouse, false, t));
                window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(x, y), 0, 0, 0f, mods, PointerKind.Mouse, false, t + 10));
                host.RunFrame();
            }
            void Key(int key, KeyModifiers mods = KeyModifiers.None)
            {
                window.QueueInput(new InputEvent(InputKind.Key, default, 0, key, 0f, mods));
                host.RunFrame();
            }

            // W0e.1 — caret click placement round-trips the advance model; pointer focus arms the blinker.
            Press(ta.X + 3f * Adv + 1f, ta.Y + ta.H / 2f, 1_000);
            scene.TryGetTextEdit(tn, out var tes1);
            const byte focusedBits = TextEditState.Focused | TextEditState.CaretVisible;
            Check("W0e.1 caret click placement round-trips the headless advance model; focus arms the blinker",
                root.Edit!.Core.Active == 3 && Near(tes1.CaretX, 3f * Adv)
                && (tes1.Flags & focusedBits) == focusedBits && (scene.Flags(field) & NodeFlags.Focused) != 0,
                $"caret={root.Edit!.Core.Active} caretX={tes1.CaretX:0.##} flags={tes1.Flags}");

            // W0e.2 — Shift+arrow / Ctrl+Shift+arrow word / Ctrl+A selection states (+SelectionChanged fires).
            Key(Keys.Right, KeyModifiers.Shift);
            Key(Keys.Right, KeyModifiers.Shift);
            bool s1 = root.Edit!.SelectionStart == 3 && root.Edit!.SelectionLength == 2 && root.Edit!.SelectedText == "lo";
            Key(Keys.Right, KeyModifiers.Shift | KeyModifiers.Ctrl);
            bool s2 = root.Edit!.SelectionStart == 3 && root.Edit!.SelectionLength == 3 && root.Edit!.SelectedText == "lo ";
            Key(Keys.A, KeyModifiers.Ctrl);
            bool s3 = root.Edit!.SelectionStart == 0 && root.Edit!.SelectionLength == 11;
            Check("W0e.2 Shift+arrow extends, Ctrl+Shift+arrow extends by word, Ctrl+A selects all (+SelectionChanged)",
                s1 && s2 && s3 && root.SelLog.Count >= 3, $"s1={s1} s2={s2} s3={s3} selEvents={root.SelLog.Count}");

            // W0e.3 — double-click selects the word at the hit; triple-click selects all (ClickCount synthesis).
            float wx = ta.X + 7f * Adv + 1f;   // inside "world"
            float wy = ta.Y + ta.H / 2f;
            Press(wx, wy, 5_000);
            Press(wx, wy, 5_100);              // chained within slop+window → ClickCount 2
            bool dbl = root.Edit!.SelectedText == "world" && root.Edit!.SelectionStart == 6;
            Press(wx, wy, 5_200);              // → ClickCount 3
            bool trp = root.Edit!.SelectionStart == 0 && root.Edit!.SelectionLength == 11;
            Check("W0e.3 double-click selects the word at the hit; triple-click selects all", dbl && trp, $"dbl={dbl} trp={trp}");

            // W0e.4 — drag-select publishes rects into the scene slab (count + X/W math) while the button is held.
            window.QueueInput(new InputEvent(InputKind.PointerDown, new Point2(ta.X + 1f * Adv + 1f, wy), 0, 0, 0f, KeyModifiers.None, PointerKind.Mouse, false, 9_000));
            host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(ta.X + 7f * Adv + 1f, wy), 0, 0));
            host.RunFrame();
            var selRects = scene.GetTextEditSelectionRects(tn);
            scene.TryGetTextEdit(tn, out var tes4);
            bool dragSel = selRects.Length == 1 && Near(selRects[0].X, 1f * Adv) && Near(selRects[0].W, 6f * Adv)
                && root.Edit!.SelectionStart == 1 && root.Edit!.SelectionLength == 6
                && (tes4.Flags & TextEditState.SelectionActive) != 0;
            Check("W0e.4 drag-select updates the selection rects in the scene slab (count + first-rect X/W math)",
                dragSel, $"rects={selRects.Length} x={(selRects.Length > 0 ? selRects[0].X : -1f):0.##} w={(selRects.Length > 0 ? selRects[0].W : -1f):0.##}");

            for (int i = 0; i < 5; i++)        // warm the pooled slab, then a steady pointer-rate drag frame
            {
                window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(ta.X + (i % 2 == 0 ? 6f : 7f) * Adv + 1f, wy), 0, 0));
                host.RunFrame();
            }
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(ta.X + 8f * Adv + 1f, wy), 0, 0));
            var dragSteady = host.RunFrame();
            Check("W0e.4b drag-select frame is 0-alloc on phases 6–13 (pooled slab, no re-render)",
                dragSteady.HotPhaseAllocBytes == 0, $"{dragSteady.HotPhaseAllocBytes} bytes");
            window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(ta.X + 8f * Adv + 1f, wy), 0, 0, 0f, KeyModifiers.None, PointerKind.Mouse, false, 9_500));
            host.RunFrame();

            // W0e.5 — clipboard round-trip via the HeadlessClipboard seam.
            var clip = (HeadlessClipboard)app.Clipboard;
            root.Edit!.Select(6, 5);           // "world"
            Key(Keys.C, KeyModifiers.Ctrl);
            bool copied = clip.TryGetText(out var c1) && c1 == "world";
            root.Edit!.Select(0, 6);           // "hello "
            Key(Keys.X, KeyModifiers.Ctrl);
            bool cutOk = clip.TryGetText(out var c2) && c2 == "hello " && root.Text!.Peek() == "world";
            Key(Keys.End);
            Key(Keys.V, KeyModifiers.Ctrl);
            bool pasted = root.Text!.Peek() == "worldhello ";
            Check("W0e.5 clipboard: Ctrl+C copies, Ctrl+X removes, Ctrl+V inserts at the caret",
                copied && cutOk && pasted, $"copy='{c1}' cut='{c2}' text='{root.Text!.Peek()}'");
        }

        // ── W0e.6 — PasswordBox mask: '●' display, copy/cut blocked, paste allowed ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0e-mask", new Size2(420, 240), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0eProbe { Initial = "secret", MaskOn = true };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            var field = FindRole(scene, scene.Root, AutomationRole.Text);
            var tn = TextVisual(scene, field);
            void Key(int key, KeyModifiers mods = KeyModifiers.None)
            {
                window.QueueInput(new InputEvent(InputKind.Key, default, 0, key, 0f, mods));
                host.RunFrame();
            }
            ClickNode(host, window, field);
            Key(Keys.A, KeyModifiers.Ctrl);
            var clip = (HeadlessClipboard)app.Clipboard;
            clip.Clear();
            Key(Keys.C, KeyModifiers.Ctrl);
            bool copyBlocked = !clip.TryGetText(out _);
            Key(Keys.X, KeyModifiers.Ctrl);
            bool cutBlocked = root.Text!.Peek() == "secret" && !clip.TryGetText(out _);
            clip.SetText("pw");
            Key(Keys.V, KeyModifiers.Ctrl);    // paste replaces the (kept) select-all
            host.RunFrame();
            string disp = strings.Resolve(scene.Paint(tn).Text);
            Check("W0e.6 Mask blocks copy/cut, allows paste; display is '\\u25CF' per grapheme (model text real)",
                copyBlocked && cutBlocked && root.Text!.Peek() == "pw" && disp == "●●",
                $"copyBlocked={copyBlocked} cutBlocked={cutBlocked} text='{root.Text!.Peek()}' disp='{disp}'");
        }

        // ── W0e.7 — undo coalescing: a typing burst is ONE step; paste is its own step; Ctrl+Z/Ctrl+Y replay ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0e-undo", new Size2(420, 240), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0eProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            var field = FindRole(scene, scene.Root, AutomationRole.Text);
            void Key(int key, KeyModifiers mods = KeyModifiers.None)
            {
                window.QueueInput(new InputEvent(InputKind.Key, default, 0, key, 0f, mods));
                host.RunFrame();
            }
            void Type(string s)
            {
                foreach (char c in s) window.QueueInput(new InputEvent(InputKind.Char, default, 0, c));
                host.RunFrame();
            }
            ClickNode(host, window, field);
            Type("abc");
            bool oneStep = root.Edit!.Core.UndoDepth == 1 && root.Text!.Peek() == "abc";
            Key(Keys.Z, KeyModifiers.Ctrl);
            bool undone = root.Text!.Peek() == "";
            Key(Keys.Y, KeyModifiers.Ctrl);
            bool redone = root.Text!.Peek() == "abc";
            ((HeadlessClipboard)app.Clipboard).SetText("XY");
            Key(Keys.End);
            Key(Keys.V, KeyModifiers.Ctrl);
            bool pasteStep = root.Text!.Peek() == "abcXY" && root.Edit!.Core.UndoDepth == 2;
            Type("z");
            bool thirdStep = root.Edit!.Core.UndoDepth == 3;
            Key(Keys.Z, KeyModifiers.Ctrl);
            Key(Keys.Z, KeyModifiers.Ctrl);
            bool back = root.Text!.Peek() == "abc";
            Check("W0e.7 undo coalescing: 'abc' = one step back to empty; paste = its own step; Ctrl+Z/Ctrl+Y replay",
                oneStep && undone && redone && pasteStep && thirdStep && back,
                $"one={oneStep} undo={undone} redo={redone} paste={pasteStep} depth3={thirdStep} back='{root.Text!.Peek()}'");
        }

        // ── W0e.8 — MaxLength clamp + BeforeTextChanging rejection ──
        bool maxClamp;
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0e-max", new Size2(420, 240), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0eProbe { MaxLen = 5 };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            ClickNode(host, window, FindRole(host.Scene, host.Scene.Root, AutomationRole.Text));
            foreach (char c in "abcdefgh") window.QueueInput(new InputEvent(InputKind.Char, default, 0, c));
            host.RunFrame();
            maxClamp = root.Text!.Peek() == "abcde";
        }
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0e-before", new Size2(420, 240), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0eProbe { Before = s => s.Length <= 3 };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            ClickNode(host, window, FindRole(host.Scene, host.Scene.Root, AutomationRole.Text));
            foreach (char c in "abcde") window.QueueInput(new InputEvent(InputKind.Char, default, 0, c));
            host.RunFrame();
            Check("W0e.8 MaxLength clamps inserts at the limit; BeforeTextChanging(false) rejects the proposed edit",
                maxClamp && root.Text!.Peek() == "abc", $"maxLen→'{(maxClamp ? "abcde" : "?")}' gated='{root.Text!.Peek()}'");
        }

        // ── W0e.9 — IsReadOnly: caret + selection + copy work, typing gated, IME stays disabled ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0e-ro", new Size2(420, 240), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0eProbe { Initial = "locked", ReadOnly = true };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            var field = FindRole(scene, scene.Root, AutomationRole.Text);
            var tn = TextVisual(scene, field);
            var ta = scene.AbsoluteRect(tn);
            window.QueueInput(new InputEvent(InputKind.PointerDown, new Point2(ta.X + 2f * Adv + 1f, ta.Y + ta.H / 2f), 0, 0, 0f, KeyModifiers.None, PointerKind.Mouse, false, 1_000));
            window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(ta.X + 2f * Adv + 1f, ta.Y + ta.H / 2f), 0, 0, 0f, KeyModifiers.None, PointerKind.Mouse, false, 1_010));
            host.RunFrame();
            bool caretOk = root.Edit!.Core.Active == 2;
            window.QueueInput(new InputEvent(InputKind.Char, default, 0, 'z'));
            window.QueueInput(new InputEvent(InputKind.Char, default, 0, 'z'));
            host.RunFrame();
            bool typingGated = root.Text!.Peek() == "locked";
            void Key(int key, KeyModifiers mods = KeyModifiers.None)
            {
                window.QueueInput(new InputEvent(InputKind.Key, default, 0, key, 0f, mods));
                host.RunFrame();
            }
            Key(Keys.A, KeyModifiers.Ctrl);
            Key(Keys.C, KeyModifiers.Ctrl);
            bool copyOk = ((HeadlessClipboard)app.Clipboard).TryGetText(out var t) && t == "locked";
            bool imeOff = !((HeadlessTextInput)window.TextInput).Editable;
            Check("W0e.9 IsReadOnly gates typing but allows caret placement, selection and copy (IME disabled)",
                caretOk && typingGated && copyOk && imeOff, $"caret={root.Edit!.Core.Active} text='{root.Text!.Peek()}' copy='{t}' imeOff={imeOff}");
        }

        // ── W0e.10 — Enter commits + KEEPS focus; Escape reverts to the focus-time snapshot + cancels + blurs ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0e-esc", new Size2(420, 240), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0eProbe { Initial = "seed" };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            var field = FindRole(scene, scene.Root, AutomationRole.Text);
            var tn = TextVisual(scene, field);
            void Key(int key, KeyModifiers mods = KeyModifiers.None)
            {
                window.QueueInput(new InputEvent(InputKind.Key, default, 0, key, 0f, mods));
                host.RunFrame();
            }
            void Type(string s)
            {
                foreach (char c in s) window.QueueInput(new InputEvent(InputKind.Char, default, 0, c));
                host.RunFrame();
            }
            ClickNode(host, window, field);    // center is beyond the text → caret at end
            Type("XY");
            Key(Keys.Enter);
            bool committed = root.Committed == "seedXY" && (scene.Flags(field) & NodeFlags.Focused) != 0;
            Type("Z");
            Key(Keys.Escape);
            scene.TryGetTextEdit(tn, out var tesEsc);
            bool reverted = root.Text!.Peek() == "seed" && root.CancelCount == 1
                && (scene.Flags(field) & NodeFlags.Focused) == 0 && (tesEsc.Flags & TextEditState.Focused) == 0;
            Check("W0e.10 Enter commits and KEEPS focus (WinUI); Escape reverts to the focus-time snapshot, cancels, blurs",
                committed && reverted, $"committed='{root.Committed}' text='{root.Text!.Peek()}' cancels={root.CancelCount}");
        }

        // ── W0e.11/12 — IME composition lifecycle + caret blink ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0e-ime", new Size2(420, 240), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0eProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            var field = FindRole(scene, scene.Root, AutomationRole.Text);
            var tn = TextVisual(scene, field);
            ClickNode(host, window, field);
            var ti = (HeadlessTextInput)window.TextInput;
            bool editable = ti.Editable;

            ti.BeginComposition();
            ti.UpdateComposition("にほ", 2, new ImeClause(0, 2, ImeClauseKind.Input));
            host.RunFrame();
            scene.TryGetTextEdit(tn, out var ic);
            var ul = scene.GetTextEditUnderlineRects(tn);
            // The honest contract under test: the provisional composition lives in the DOC (display shows it) while the
            // SIGNAL stays unchanged until commit.
            bool provisional = strings.Resolve(scene.Paint(tn).Text) == "にほ" && root.Text!.Peek().Length == 0
                && ic.CompStart == 0 && ic.CompLen == 2 && ul.Length == 1 && Near(ul[0].H, 1f);

            ti.UpdateComposition("日本", 2, new ImeClause(0, 2, ImeClauseKind.TargetConverted));
            host.RunFrame();
            var ul2 = scene.GetTextEditUnderlineRects(tn);
            bool targetThick = ul2.Length == 1 && Near(ul2[0].H, 2f);

            ti.Commit("日本");
            host.RunFrame();
            scene.TryGetTextEdit(tn, out var ic2);
            bool committed = root.Text!.Peek() == "日本" && strings.Resolve(scene.Paint(tn).Text) == "日本"
                && ic2.CompLen == 0 && scene.GetTextEditUnderlineRects(tn).Length == 0 && root.Edit!.Core.UndoDepth == 1;

            ti.BeginComposition();
            ti.UpdateComposition("か", 1, new ImeClause(0, 1, ImeClauseKind.Input));
            host.RunFrame();
            bool midCancel = strings.Resolve(scene.Paint(tn).Text) == "日本か" && root.Text!.Peek() == "日本";
            ti.Cancel();
            host.RunFrame();
            bool cancelled = strings.Resolve(scene.Paint(tn).Text) == "日本" && root.Text!.Peek() == "日本";
            bool caretRect = Near(ti.LastCaretRectPx.X, scene.AbsoluteRect(tn).X + 2f * Adv);
            Check("W0e.11 IME: provisional in the DOC only (signal commits on commit), clause underline kinds, cancel restores, caret-rect placed",
                editable && provisional && targetThick && committed && midCancel && cancelled && caretRect,
                $"editable={editable} prov={provisional} thick={targetThick} commit={committed} mid={midCancel} cancel={cancelled} rectX={ti.LastCaretRectPx.X:0.#}");

            // W0e.12 — caret blink toggles CaretVisible over frames (500ms half-period @16ms fixed dt) + resets on edit.
            bool wentOff = false;
            int frames = 0;
            for (; frames < 90 && !wentOff; frames++)
            {
                host.RunFrame();
                scene.TryGetTextEdit(tn, out var bf);
                wentOff = (bf.Flags & TextEditState.CaretVisible) == 0;
            }
            window.QueueInput(new InputEvent(InputKind.Char, default, 0, 'x'));
            host.RunFrame();
            scene.TryGetTextEdit(tn, out var br);
            bool resetOn = (br.Flags & TextEditState.CaretVisible) != 0;
            Check("W0e.12 caret blink toggles CaretVisible over frames and snaps visible on edit",
                wentOff && resetOn, $"offAfter={frames}f resetOn={resetOn}");
            for (int i = 0; i < 6; i++) host.RunFrame();
            var blinkSteady = host.RunFrame();
            Check("W0e.12b focused steady (caret blink) frame allocates 0 on phases 6–13",
                blinkSteady.HotPhaseAllocBytes == 0, $"{blinkSteady.HotPhaseAllocBytes} bytes");
        }

        // ── W0e.13 — Tab (keyboard) focus selects all; the DeleteButton clears + keeps focus + hides when empty ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0e-del", new Size2(420, 240), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0eProbe { Initial = "abc", ShowDelete = true };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            var field = FindRole(scene, scene.Root, AutomationRole.Text);
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Tab));
            host.RunFrame();
            host.RunFrame();   // the focus re-render mounts the delete button
            bool selAll = root.Edit!.SelectionStart == 0 && root.Edit!.SelectionLength == 3;
            bool visual = (scene.Flags(field) & NodeFlags.FocusVisual) != 0;
            var btns = Roles(scene, AutomationRole.Button);
            bool shown = btns.Count == 1;
            if (shown) ClickNode(host, window, btns[0]);
            host.RunFrame();
            bool cleared = root.Text!.Peek() == "" && (scene.Flags(field) & NodeFlags.Focused) != 0;
            bool gone = Roles(scene, AutomationRole.Button).Count == 0;
            Check("W0e.13 Tab keyboard-focus selects all; DeleteButton (E894) clears, keeps focus, hides when empty",
                selAll && visual && shown && cleared && gone,
                $"selAll={selAll} visual={visual} shown={shown} cleared={cleared} gone={gone}");
        }

        // ── W0e.14 — caret-follow: ScrollX clamps the caret into the viewport; the wrapper transform applies -ScrollX ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0e-scroll", new Size2(420, 240), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0eProbe { W = 80f };   // lane viewport = 80 − (10+6) padding = 64
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            var field = FindRole(scene, scene.Root, AutomationRole.Text);
            var tn = TextVisual(scene, field);
            ClickNode(host, window, field);
            for (int i = 0; i < 20; i++) window.QueueInput(new InputEvent(InputKind.Char, default, 0, 'm'));
            host.RunFrame();
            scene.TryGetTextEdit(tn, out var tf);
            // caret = 20×7.7 = 154; follow wants 154−(64−8) = 98, clamped to maxScroll = (154+2)−64 = 92.
            bool follow = Near(tf.CaretX, 154f) && Near(tf.ScrollX, 92f);
            host.RunFrame();   // the TransformBind flush applies -ScrollX to the wrapper
            var scroller = scene.Parent(tn);
            float appliedDx = scene.Paint(scroller).LocalTransform.Dx;
            bool applied = Near(appliedDx, -92f);
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Home));
            host.RunFrame();
            scene.TryGetTextEdit(tn, out var th);
            bool home = Near(th.ScrollX, 0f);
            Check("W0e.14 caret-follow clamps ScrollX (caret stays inside the viewport) and the wrapper transform applies -ScrollX",
                follow && applied && home, $"caretX={tf.CaretX:0.#} scrollX={tf.ScrollX:0.#} dx={appliedDx:0.#} home={th.ScrollX:0.#}");
            for (int i = 0; i < 6; i++) host.RunFrame();
            var idle = host.RunFrame();
            Check("W0e.14b typing-burst-then-idle steady frame allocates 0 on phases 6–13",
                idle.HotPhaseAllocBytes == 0, $"{idle.HotPhaseAllocBytes} bytes");
        }

        // ── W0e.15 — multi-line: Up/Down honor the StickyX goal column over wrapped lines; Enter inserts '\r' ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0e-multi", new Size2(420, 240), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            // wrap width 144 → "aaaa bbbb cccc " | "dddd eeee" (greedy word wrap of the headless model)
            var root = new W0eProbe { Multi = true, W = 160f, H = 64f, Initial = "aaaa bbbb cccc dddd eeee" };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            var field = FindRole(scene, scene.Root, AutomationRole.Text);
            var tn = TextVisual(scene, field);
            var ta = scene.AbsoluteRect(tn);
            void Key(int key, KeyModifiers mods = KeyModifiers.None)
            {
                window.QueueInput(new InputEvent(InputKind.Key, default, 0, key, 0f, mods));
                host.RunFrame();
            }
            window.QueueInput(new InputEvent(InputKind.PointerDown, new Point2(ta.X + 2f * Adv + 1f, ta.Y + LineH * 0.5f), 0, 0, 0f, KeyModifiers.None, PointerKind.Mouse, false, 1_000));
            window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(ta.X + 2f * Adv + 1f, ta.Y + LineH * 0.5f), 0, 0, 0f, KeyModifiers.None, PointerKind.Mouse, false, 1_010));
            host.RunFrame();
            bool caretLine1 = root.Edit!.Core.Active == 2;
            Key(Keys.Down);
            bool down1 = root.Edit!.Core.Active == 17;     // line 2 starts at 15; sticky column 2 → 15+2
            Key(Keys.Down);
            bool down2 = root.Edit!.Core.Active == 17;     // clamped at the last line
            Key(Keys.Up);
            bool up1 = root.Edit!.Core.Active == 2;        // StickyX goal column held across the round-trip
            Key(Keys.Down, KeyModifiers.Shift);
            bool extended = root.Edit!.SelectionStart == 2 && root.Edit!.SelectionLength == 15;
            Key(Keys.Right);                                // collapse to the selection end (17)
            Key(Keys.Enter);                                // multi-line Enter inserts a hard '\r' break
            string after = root.Text!.Peek();
            bool newline = after.Length == 25 && after[17] == '\r';
            Check("W0e.15 multi-line: Up/Down/StickyX over wrapped lines (clamped at edges); Shift+Down extends; Enter inserts '\\r'",
                caretLine1 && down1 && down2 && up1 && extended && newline,
                $"caret={caretLine1} d1={down1} d2={down2} up={up1} ext={extended} nl={newline}");
        }
    }

    // W0f — the text-input consumer controls on the rewritten EditableText: PasswordBox peek/reveal-mode matrix,
    // NumberBox keyboard stepping + Text/format seam + spin visuals, AutoSuggestBox TextChanged-reason matrix +
    // SuggestionChosen semantics + UpdateTextOnSelect, editable ComboBox prefix-match/TextSubmitted/Escape.
    static void TextConsumerControlChecks(StringTable strings)
    {
        const float Adv = 14f * 0.55f;     // headless advance model @ FontSize 14 (see EditableTextCoreChecks)

        static NodeHandle TextVisual(SceneStore s, NodeHandle n)
        {
            if (n.IsNull) return NodeHandle.Null;
            if (s.Paint(n).VisualKind == VisualKind.Text) return n;
            for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
            {
                var r = TextVisual(s, c);
                if (!r.IsNull) return r;
            }
            return NodeHandle.Null;
        }

        // ── W0f.1 — PasswordBox Peek: press-and-hold reveals, release re-masks; reveal button = F78D @ width 30 ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0f-pw", new Size2(420, 240), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0fPasswordProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            var field = FindRole(scene, scene.Root, AutomationRole.Text);
            var tn = TextVisual(scene, field);
            bool masked0 = strings.Resolve(scene.Paint(tn).Text) == "●●●●●●";
            bool noBtn0 = Roles(scene, AutomationRole.Button).Count == 0;   // ButtonCollapsed while unfocused

            ClickNode(host, window, field);
            host.RunFrame();
            // Focusing a POPULATED box does NOT show the eye: CPasswordBox::OnGotFocus clears m_fCanShowRevealButton
            // (PasswordBox.cpp:572–581) and CanInvokeRevealButton requires it (PasswordBox.cpp:618–626).
            bool noBtnOnFocus = Roles(scene, AutomationRole.Button).Count == 0;

            // Empty the box, then type a fresh password — the empty→non-empty content change arms the button
            // (OnContentChanged, PasswordBox.cpp:366–377: "only allow password reveal button if transitioning from
            // empty to non-empty state").
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.A, 0f, KeyModifiers.Ctrl));
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Back, 0f, KeyModifiers.None));
            host.RunFrame();
            foreach (char c in "secret") window.QueueInput(new InputEvent(InputKind.Char, default, 0, c));
            host.RunFrame();
            host.RunFrame();
            var btns = Roles(scene, AutomationRole.Button);
            bool shown = btns.Count == 1;
            bool w30 = shown && Near(scene.AbsoluteRect(btns[0]).W, 30f);
            // RevealButton glyph F78D @ TextControlButtonForeground = TextFillColorSecondary #C5FFFFFF (dark)
            // (PasswordBox_themeresources.xaml:100 + TextBox_themeresources.xaml:45).
            bool glyph = shown && HasGlyph(device, strings, "")
                && ColorClose(GlyphColor(device, strings, ""), Tok.TextSecondary, 0.004f);

            // Press-and-HOLD (no release yet) → the password shows in clear text.
            var bc = CenterOf(scene, btns[0]);
            window.QueueInput(new InputEvent(InputKind.PointerDown, bc, 0, 0, 0f, KeyModifiers.None, PointerKind.Mouse, false, 2_000));
            host.RunFrame();
            host.RunFrame();
            bool peeked = strings.Resolve(scene.Paint(tn).Text) == "secret";

            // Release → re-masks and the FIELD keeps focus (WinUI keeps the field focused across reveal interactions).
            window.QueueInput(new InputEvent(InputKind.PointerUp, bc, 0, 0, 0f, KeyModifiers.None, PointerKind.Mouse, false, 2_100));
            host.RunFrame();
            host.RunFrame();
            bool remasked = strings.Resolve(scene.Paint(tn).Text) == "●●●●●●";
            bool focusKept = (scene.Flags(field) & NodeFlags.Focused) != 0;
            Check("W0f.1 PasswordBox Peek: no eye on focusing a populated box; typing fresh content shows it (F78D, width 30, TextSecondary); press-and-hold reveals, release re-masks + keeps focus",
                masked0 && noBtn0 && noBtnOnFocus && shown && w30 && glyph && peeked && remasked && focusKept,
                $"masked0={masked0} noBtn0={noBtn0} noBtnOnFocus={noBtnOnFocus} shown={shown} w30={w30} glyph={glyph} peeked={peeked} remasked={remasked} focus={focusKept}");
        }

        // ── W0f.1b — PasswordBox reveal arming (the REAL WinUI ButtonStates rule): the eye appears while TYPING a
        // new password from empty (CPasswordBox::OnContentChanged arms m_fCanShowRevealButton only on the
        // empty→non-empty transition, PasswordBox.cpp:366–377; CanInvokeRevealButton = canShow ∧ hasSpace ∧ focused,
        // PasswordBox.cpp:618–626), and does NOT appear from merely focusing a populated box
        // (CPasswordBox::OnGotFocus clears m_fCanShowRevealButton, PasswordBox.cpp:572–581). ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0f-pwt", new Size2(420, 240), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0fPasswordProbe { Initial = "" };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            var field = FindRole(scene, scene.Root, AutomationRole.Text);

            ClickNode(host, window, field);
            host.RunFrame();
            bool noBtnEmptyFocused = Roles(scene, AutomationRole.Button).Count == 0;   // focused but empty → collapsed

            // Type into the focused empty box — the empty→non-empty content change arms the reveal button
            // (PasswordBox.cpp:366–377) and it must actually RENDER (F78D glyph, the user-visible eye).
            foreach (char c in "abc") window.QueueInput(new InputEvent(InputKind.Char, default, 0, c));
            host.RunFrame();
            host.RunFrame();
            int countAfterType = Roles(scene, AutomationRole.Button).Count;
            bool glyphAfterType = HasGlyph(device, strings, "");   // the reveal eye (PasswordBox_themeresources.xaml:100)
            bool shownAfterType = countAfterType == 1 && glyphAfterType;

            // Clear to empty → the flag drops (OnContentChanged: IsEmpty ⇒ m_fCanShowRevealButton = FALSE,
            // PasswordBox.cpp:430–434) and the button unmounts.
            void Key(int key, KeyModifiers mods = KeyModifiers.None)
            {
                window.QueueInput(new InputEvent(InputKind.Key, default, 0, key, 0f, mods));
                host.RunFrame();
            }
            Key(Keys.A, KeyModifiers.Ctrl);
            Key(Keys.Back);
            host.RunFrame();
            bool hiddenOnEmpty = Roles(scene, AutomationRole.Button).Count == 0;

            // Typing again from empty re-arms (the empty→non-empty transition, PasswordBox.cpp:366–377).
            window.QueueInput(new InputEvent(InputKind.Char, default, 0, 'z'));
            host.RunFrame();
            host.RunFrame();
            bool rearmed = Roles(scene, AutomationRole.Button).Count == 1;
            Check("W0f.1b PasswordBox reveal arming: typing from empty shows the eye; focused-empty does not; emptying hides + retype re-arms",
                noBtnEmptyFocused && shownAfterType && hiddenOnEmpty && rearmed,
                $"noBtnEmptyFocused={noBtnEmptyFocused} shownAfterType={shownAfterType} (count={countAfterType} glyph={glyphAfterType}) hiddenOnEmpty={hiddenOnEmpty} rearmed={rearmed}");
        }

        // ── W0f.2 — PasswordRevealMode matrix: Hidden = no button + masked; Visible = no button + clear; copy blocked ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0f-pwh", new Size2(420, 240), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0fPasswordProbe { Mode = PasswordRevealMode.Hidden };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            var field = FindRole(scene, scene.Root, AutomationRole.Text);
            var tn = TextVisual(scene, field);
            ClickNode(host, window, field);
            host.RunFrame();
            bool hiddenOk = strings.Resolve(scene.Paint(tn).Text) == "●●●●●●"
                && Roles(scene, AutomationRole.Button).Count == 0;

            using var app2 = new HeadlessPlatformApp();
            var window2 = new HeadlessWindow(new WindowDesc("w0f-pwv", new Size2(420, 240), 1f)); window2.Show();
            var device2 = new HeadlessGpuDevice();
            var fonts2 = new HeadlessFontSystem(strings);
            var root2 = new W0fPasswordProbe { Mode = PasswordRevealMode.Visible };
            using var host2 = new AppHost(app2, window2, device2, fonts2, strings, root2);
            host2.RunFrame();
            var scene2 = host2.Scene;
            var field2 = FindRole(scene2, scene2.Root, AutomationRole.Text);
            var tn2 = TextVisual(scene2, field2);
            bool visibleOk = strings.Resolve(scene2.Paint(tn2).Text) == "secret";
            ClickNode(host2, window2, field2);
            host2.RunFrame();
            bool noBtnVisible = Roles(scene2, AutomationRole.Button).Count == 0;
            // Copy stays BLOCKED even while revealed (WinUI never allows copying out of a PasswordBox).
            void Key2(int key, KeyModifiers mods = KeyModifiers.None)
            {
                window2.QueueInput(new InputEvent(InputKind.Key, default, 0, key, 0f, mods));
                host2.RunFrame();
            }
            Key2(Keys.A, KeyModifiers.Ctrl);
            Key2(Keys.C, KeyModifiers.Ctrl);
            bool copyBlocked = !((HeadlessClipboard)app2.Clipboard).TryGetText(out _);
            Check("W0f.2 PasswordRevealMode: Hidden = masked + no reveal button; Visible = clear text + no button, copy still blocked",
                hiddenOk && visibleOk && noBtnVisible && copyBlocked,
                $"hidden={hiddenOk} visible={visibleOk} noBtn={noBtnVisible} copyBlocked={copyBlocked}");
        }

        // ── W0f.3 — PasswordChar customization (WinUI PasswordBox.PasswordChar, default '●') ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0f-pwc", new Size2(420, 240), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0fPasswordProbe { Char = '*' };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            var tn = TextVisual(scene, FindRole(scene, scene.Root, AutomationRole.Text));
            Check("W0f.3 PasswordBox PasswordChar: a custom mask char renders per grapheme",
                strings.Resolve(scene.Paint(tn).Text) == "******", $"disp='{strings.Resolve(scene.Paint(tn).Text)}'");
        }

        // ── W0f.4 — NumberBox keyboard stepping: Up/Down=SmallChange, PageUp/PageDown=LargeChange, clamp + Text ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0f-nb", new Size2(420, 240), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0fNumberProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            bool textSeed = root.Txt!.Peek() == "5";   // mount Value→Text sync (UpdateTextToValue)
            ClickNode(host, window, FindRole(scene, scene.Root, AutomationRole.Text));
            void Key(int key)
            {
                window.QueueInput(new InputEvent(InputKind.Key, default, 0, key));
                host.RunFrame();
            }
            Key(Keys.Up);
            bool up = root.Val!.Peek() == 6;                       // +SmallChange (NumberBox.cpp:538–541)
            Key(Keys.PageUp);
            bool pgUp = root.Val!.Peek() == 10;                    // +LargeChange 6+5=11 → clamp 10 (cpp:548–551)
            Key(Keys.Up);
            bool clamped = root.Val!.Peek() == 10;                 // at Maximum: stays (Coerce clamps)
            Key(Keys.PageDown);
            bool pgDn = root.Val!.Peek() == 5;                     // −LargeChange (cpp:553–556)
            Key(Keys.Down);
            bool dn = root.Val!.Peek() == 4 && root.Txt!.Peek() == "4";   // −SmallChange + Text reformat
            Check("W0f.4 NumberBox keys: Up/Down step SmallChange, PageUp/PageDown step LargeChange, clamped at bounds; Text carries the formatted value",
                textSeed && up && pgUp && clamped && pgDn && dn,
                $"seed={textSeed} up={up} pgUp={pgUp} clamp={clamped} pgDn={pgDn} dn={dn} txt='{root.Txt!.Peek()}'");
        }

        // ── W0f.5 — NumberBox: NaN does not step (cpp StepValue isnan guard); programmatic Text validates + clamps ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0f-nbn", new Size2(420, 240), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0fNumberProbe { Initial = double.NaN };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            ClickNode(host, window, FindRole(scene, scene.Root, AutomationRole.Text));
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Up));
            host.RunFrame();
            bool nanNoStep = double.IsNaN(root.Val!.Peek()) && root.Txt!.Peek() == "";   // cpp:604–629

            using var app2 = new HeadlessPlatformApp();
            var window2 = new HeadlessWindow(new WindowDesc("w0f-nbt", new Size2(420, 240), 1f)); window2.Show();
            var device2 = new HeadlessGpuDevice();
            var fonts2 = new HeadlessFontSystem(strings);
            var root2 = new W0fNumberProbe();
            using var host2 = new AppHost(app2, window2, device2, fonts2, strings, root2);
            host2.RunFrame();
            root2.Txt!.Value = "7";    // external programmatic Text write — OnTextPropertyChanged → ValidateInput (cpp:325–340)
            host2.RunFrame();
            bool progSet = root2.Val!.Peek() == 7;
            root2.Txt!.Value = "25";   // out-of-range → InvalidInputOverwritten clamps to Maximum, text reformats
            host2.RunFrame();
            bool progClamp = root2.Val!.Peek() == 10 && root2.Txt!.Peek() == "10";
            Check("W0f.5 NumberBox: NaN value does not step; a programmatic Text write validates immediately (clamping out-of-range)",
                nanNoStep && progSet && progClamp,
                $"nanNoStep={nanNoStep} progSet={progSet} progClamp={progClamp} v={root2.Val!.Peek()} t='{root2.Txt!.Peek()}'");
        }

        // ── W0f.6 — NumberBox spin visuals: inline E70E/E70D @ TextSecondary in 32-wide cells; Compact EC8F indicator,
        //            popup opens on focus with 36×36 spin buttons and closes on blur ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0f-nbi", new Size2(420, 240), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0fNumberProbe { Mode = NumberBoxSpinButtonPlacementMode.Inline };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            // Glyphs E70E/E70D (NumberBox.xaml:174–175) at TextControlButtonForeground = TextFillColorSecondary
            // #C5FFFFFF dark (TextBox_themeresources.xaml:45) — full-ARGB.
            bool glyphs = HasGlyph(device, strings, "") && HasGlyph(device, strings, "")
                && ColorClose(GlyphColor(device, strings, ""), Tok.TextSecondary, 0.004f)
                && ColorClose(GlyphColor(device, strings, ""), Tok.TextSecondary, 0.004f);
            var spins = Roles(scene, AutomationRole.Button);
            bool cells = spins.Count == 2
                && Near(scene.AbsoluteRect(spins[0]).W, 32f) && Near(scene.AbsoluteRect(spins[1]).W, 32f);   // MinWidth 32 (NumberBox.xaml:185)
            ClickNode(host, window, spins[0]);   // up spin → +SmallChange
            bool stepped = root.Val!.Peek() == 6;

            using var app2 = new HeadlessPlatformApp();
            var window2 = new HeadlessWindow(new WindowDesc("w0f-nbc", new Size2(420, 320), 1f)); window2.Show();
            var device2 = new HeadlessGpuDevice();
            var fonts2 = new HeadlessFontSystem(strings);
            var root2 = new W0fNumberProbe { Mode = NumberBoxSpinButtonPlacementMode.Compact };
            using var host2 = new AppHost(app2, window2, device2, fonts2, strings, root2);
            host2.RunFrame();
            var scene2 = host2.Scene;
            bool indicator = HasGlyph(device2, strings, "");          // PopupIndicator (NumberBox.xaml:365)
            bool noPopup0 = Roles(scene2, AutomationRole.Button).Count == 0; // the indicator is NOT a button
            ClickNode(host2, window2, FindRole(scene2, scene2.Root, AutomationRole.Text));   // focus → popup opens
            host2.RunFrame();
            var popupBtns = Roles(scene2, AutomationRole.Button);
            bool popupOpen = popupBtns.Count == 2
                && Near(scene2.AbsoluteRect(popupBtns[0]).W, 36f) && Near(scene2.AbsoluteRect(popupBtns[0]).H, 36f);   // 36×36 (NumberBox.xaml:197–198)
            window2.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Escape));      // Escape blurs → popup closes
            for (int i = 0; i < 10; i++) host2.RunFrame();   // ride out the 83ms overlay close fade
            bool popupClosed = Roles(scene2, AutomationRole.Button).Count == 0;
            Check("W0f.6 NumberBox spin visuals: inline 32-wide E70E/E70D cells @ TextSecondary step on click; Compact = non-interactive EC8F indicator, popup (36×36 spins) opens on focus / closes on blur",
                glyphs && cells && stepped && indicator && noPopup0 && popupOpen && popupClosed,
                $"glyphs={glyphs} cells={cells} stepped={stepped} ind={indicator} no0={noPopup0} open={popupOpen} closed={popupClosed}");
        }

        // ── W0f.7 — AutoSuggestBox TextChanged-reason matrix + arrow preview + SuggestionChosen-per-selection ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0f-asb", new Size2(420, 320), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0fAsbProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            ClickNode(host, window, FindRole(scene, scene.Root, AutomationRole.Text));
            void Key(int key)
            {
                window.QueueInput(new InputEvent(InputKind.Key, default, 0, key));
                host.RunFrame();
            }
            foreach (char c in "ca") window.QueueInput(new InputEvent(InputKind.Char, default, 0, c));
            host.RunFrame();
            host.RunFrame();   // overlay content realizes
            bool typed = root.Changes.Count > 0 && root.Changes[^1] == ("ca", TextChangeReason.UserInput)
                && Roles(scene, AutomationRole.MenuItem).Count == 3;

            Key(Keys.Down);    // selection → item 0: preview text + SuggestionChosen (reason SuggestionChosen)
            host.RunFrame();   // the deferred TextChanged effect
            bool preview = root.Query!.Peek() == "Cascadia Code"
                && root.Chosen.Count == 1 && root.Chosen[0] == "Cascadia Code"
                && root.Changes[^1] == ("Cascadia Code", TextChangeReason.SuggestionChosen);

            Key(Keys.Down); Key(Keys.Down);   // → Calendar → Calculator
            Key(Keys.Down);                   // past the end → restore the typed text (reason ProgrammaticChange)
            host.RunFrame();
            bool restored = root.Query!.Peek() == "ca" && root.Chosen.Count == 3
                && root.Changes[^1] == ("ca", TextChangeReason.ProgrammaticChange);

            Key(Keys.Down);                   // back onto item 0
            int chosenBeforeEnter = root.Chosen.Count;
            Key(Keys.Enter);                  // submit: QueryText = field text; NO extra SuggestionChosen (cpp:1149–1160)
            bool submitted = root.Submitted.Count == 1 && root.Submitted[0] == "Cascadia Code"
                && root.Chosen.Count == chosenBeforeEnter;
            Check("W0f.7 AutoSuggestBox: TextChanged reasons (UserInput → SuggestionChosen preview → ProgrammaticChange restore); SuggestionChosen per selection move, NOT on Enter",
                typed && preview && restored && submitted,
                $"typed={typed} preview={preview} restored={restored} submit={submitted} chosen={root.Chosen.Count} changes={root.Changes.Count}");
        }

        // ── W0f.8 — AutoSuggestBox UpdateTextOnSelect=false: arrows cycle + SuggestionChosen, field text untouched ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0f-asbu", new Size2(420, 320), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0fAsbProbe { UpdateTextOnSelect = false };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            ClickNode(host, window, FindRole(scene, scene.Root, AutomationRole.Text));
            foreach (char c in "cal") window.QueueInput(new InputEvent(InputKind.Char, default, 0, c));
            host.RunFrame();
            host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Down));
            host.RunFrame();
            bool ok = root.Query!.Peek() == "cal" && root.Chosen.Count == 1 && root.Chosen[0] == "Calendar";
            Check("W0f.8 AutoSuggestBox UpdateTextOnSelect=false: arrow keeps the typed text but still raises SuggestionChosen (cpp:2367 gates only the text write)",
                ok, $"query='{root.Query!.Peek()}' chosen={string.Join("|", root.Chosen)}");
        }

        // ── W0f.9 — AutoSuggestBox row click: SuggestionChosen BEFORE QuerySubmitted; item content inset 12 ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0f-asbc", new Size2(420, 320), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0fAsbProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            ClickNode(host, window, FindRole(scene, scene.Root, AutomationRole.Text));
            foreach (char c in "cal") window.QueueInput(new InputEvent(InputKind.Char, default, 0, c));
            host.RunFrame();
            host.RunFrame();
            var rows = Roles(scene, AutomationRole.MenuItem);
            // Content inset: DefaultListViewItemStyle Padding 16,0,12,0 from the ITEM edge; plate inset 4 → 12 inside.
            var rowText = TextVisual(scene, rows[1]);
            float inset = scene.AbsoluteRect(rowText).X - scene.AbsoluteRect(rows[1]).X;
            bool insetOk = Near(inset, 12f);
            ClickNode(host, window, rows[1]);   // "Calculator"
            int ci = root.Order.IndexOf("C:Calculator");
            int qi = root.Order.IndexOf("Q:Calculator");
            bool ordered = ci >= 0 && qi > ci && root.Query!.Peek() == "Calculator";
            Check("W0f.9 AutoSuggestBox click: SelectionChanged → SuggestionChosen → QuerySubmitted sequential; row content inset 12 (ListViewItem 16,0,12,0 minus the 4px plate)",
                insetOk && ordered, $"inset={inset:0.#} order=[{string.Join(",", root.Order)}]");
        }

        // ── W0f.10 — editable ComboBox: prefix auto-match autocompletes with the suffix selected, commit on Enter ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0f-cmb", new Size2(420, 320), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0fComboProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            var field = FindRole(scene, scene.Root, AutomationRole.Text);
            var tn = TextVisual(scene, field);
            ClickNode(host, window, field);
            foreach (char c in "gr") window.QueueInput(new InputEvent(InputKind.Char, default, 0, c));
            host.RunFrame();
            // ProcessSearch prefix hit → auto-complete "Green" with the completed suffix "een" selected
            // (ComboBox_Partial.cpp:4108–4119 + UpdateEditableTextBox :1543–1551); SelectedIndex NOT committed while
            // typing (SelectionChangedTrigger default = Committed).
            var selRects = scene.GetTextEditSelectionRects(tn);
            bool completed = root.Txt!.Peek() == "Green" && root.Sel!.Peek() == -1
                && selRects.Length == 1 && Near(selRects[0].X, 2f * Adv) && Near(selRects[0].W, 3f * Adv);
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Enter));
            host.RunFrame();
            bool committed = root.Sel!.Peek() == 1 && root.Submitted.Count == 0;   // a search hit commits WITHOUT TextSubmitted
            Check("W0f.10 editable ComboBox: typing 'gr' auto-completes to 'Green' with 'een' selected (no commit); Enter commits the match, no TextSubmitted",
                completed && committed,
                $"txt='{root.Txt!.Peek()}' sel={root.Sel!.Peek()} rects={selRects.Length} committed={committed}");
        }

        // ── W0f.11 — editable ComboBox TextSubmitted: unhandled → custom value (sel −1); handled → selection kept ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0f-cmbt", new Size2(420, 320), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0fComboProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            var field = FindRole(scene, scene.Root, AutomationRole.Text);
            void Key(int key, KeyModifiers mods = KeyModifiers.None)
            {
                window.QueueInput(new InputEvent(InputKind.Key, default, 0, key, 0f, mods));
                host.RunFrame();
            }
            void Type(string s)
            {
                foreach (char c in s) window.QueueInput(new InputEvent(InputKind.Char, default, 0, c));
                host.RunFrame();
            }
            ClickNode(host, window, field);
            Type("gr"); Key(Keys.Enter);                       // commit "Green" (sel 1)
            Key(Keys.A, KeyModifiers.Ctrl); Type("xyz"); Key(Keys.Enter);
            bool custom = root.Submitted.Count == 1 && root.Submitted[0] == "xyz"
                && root.Sel!.Peek() == -1 && root.Txt!.Peek() == "xyz";   // unhandled: custom value, no selection (cpp:2540–2543)

            using var app2 = new HeadlessPlatformApp();
            var window2 = new HeadlessWindow(new WindowDesc("w0f-cmbh", new Size2(420, 320), 1f)); window2.Show();
            var device2 = new HeadlessGpuDevice();
            var fonts2 = new HeadlessFontSystem(strings);
            var root2 = new W0fComboProbe { HandleSubmit = true };
            using var host2 = new AppHost(app2, window2, device2, fonts2, strings, root2);
            host2.RunFrame();
            var scene2 = host2.Scene;
            var field2 = FindRole(scene2, scene2.Root, AutomationRole.Text);
            void Key2(int key, KeyModifiers mods = KeyModifiers.None)
            {
                window2.QueueInput(new InputEvent(InputKind.Key, default, 0, key, 0f, mods));
                host2.RunFrame();
            }
            ClickNode(host2, window2, field2);
            foreach (char c in "gr") window2.QueueInput(new InputEvent(InputKind.Char, default, 0, c));
            host2.RunFrame();
            Key2(Keys.Enter);                                  // sel = 1
            Key2(Keys.A, KeyModifiers.Ctrl);
            foreach (char c in "xyz") window2.QueueInput(new InputEvent(InputKind.Char, default, 0, c));
            host2.RunFrame();
            Key2(Keys.Enter);
            bool handled = root2.Submitted.Count == 1 && root2.Sel!.Peek() == 1 && root2.Txt!.Peek() == "xyz";
            Check("W0f.11 editable ComboBox TextSubmitted: unhandled custom value → SelectedIndex −1 + text kept; handled (return true) → selection untouched",
                custom && handled,
                $"custom={custom} (sel={root.Sel!.Peek()} txt='{root.Txt!.Peek()}') handled={handled} (sel2={root2.Sel!.Peek()})");
        }

        // ── W0f.12 — editable ComboBox: arrows preview without committing; Escape restores the pre-edit text ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0f-cmbe", new Size2(420, 320), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0fComboProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            var field = FindRole(scene, scene.Root, AutomationRole.Text);
            void Key(int key)
            {
                window.QueueInput(new InputEvent(InputKind.Key, default, 0, key));
                host.RunFrame();
            }
            ClickNode(host, window, field);   // focus snapshot: "" / sel −1
            Key(Keys.Down);
            bool prev1 = root.Txt!.Peek() == "Red" && root.Sel!.Peek() == -1;     // preview select-all, NO commit (cpp:2939–2949)
            Key(Keys.Down);
            bool prev2 = root.Txt!.Peek() == "Green" && root.Sel!.Peek() == -1;
            Key(Keys.Escape);
            host.RunFrame();
            bool reverted = root.Txt!.Peek() == "" && root.Sel!.Peek() == -1
                && (scene.Flags(field) & NodeFlags.Focused) == 0;                  // Escape reverts + blurs, no commit
            Check("W0f.12 editable ComboBox: Up/Down preview into the field without committing; Escape restores the pre-edit text + selection",
                prev1 && prev2 && reverted,
                $"prev1={prev1} prev2={prev2} reverted={reverted} txt='{root.Txt!.Peek()}' sel={root.Sel!.Peek()}");
        }

        // ── W0f.13 — header/description chrome: TextControlHeaderForeground (+disabled) and the Description row ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0f-hdr", new Size2(420, 320), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Direction = 1, Gap = 12,
                    Children =
                    [
                        TextBox.Create("ph", 280f, "Email", description: "Helper"),
                        PasswordBox.Create("Password", 280f, "Pw", isEnabled: false,
                                           password: new Signal<string>("secret")),
                    ],
                },
            };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            // Header = TextControlHeaderForeground (BaseHigh #FFFFFFFF dark, generic.xaml:886+207); Description =
            // SystemControlDescriptionTextForegroundBrush (BaseMedium #99FFFFFF dark, generic.xaml:327+209); disabled
            // header = TextControlHeaderForegroundDisabled (BaseMediumLow #66FFFFFF dark, generic.xaml:887+211);
            // disabled field TEXT = TemporaryTextFillColorDisabled #5DFEFEFE (TextBox_themeresources.xaml:22+34).
            bool header = ColorClose(GlyphColor(device, strings, "Email"), Tok.TextControlHeaderForeground, 0.004f);
            bool desc = ColorClose(GlyphColor(device, strings, "Helper"), Tok.TextControlDescriptionForeground, 0.004f);
            bool disHeader = ColorClose(GlyphColor(device, strings, "Pw"), Tok.TextControlHeaderForegroundDisabled, 0.004f);
            bool disText = ColorClose(GlyphColor(device, strings, "●●●●●●"), Tok.TextControlForegroundDisabled, 0.004f);
            Check("W0f.13 header/description chrome: header BaseHigh, description BaseMedium, disabled header BaseMediumLow, disabled field text #5DFEFEFE (full ARGB)",
                header && desc && disHeader && disText,
                $"header={header} desc={desc} disHeader={disHeader} disText={disText}");
        }
    }

    // E3 — implicit BrushTransition: a logical flip cross-fades fill + foreground over 83ms (no snap), then settles
    // exactly at the target and the frame loop idles again (the row self-removes).
    static void BrushTransitionChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("brush", new Size2(300, 200), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var root = new BrushTransitionProbe();
        using var host = new AppHost(app, window, device, fonts, strings, root);
        host.RunFrame();

        static FillRoundRectCmd ProbeRect(HeadlessGpuDevice dev)
        {
            foreach (var r in dev.LastRects) if (Near(r.Rect.W, 77f) && Near(r.Rect.H, 33f)) return r;
            return default;
        }

        bool restingA = ColorClose(ProbeRect(device).Fill, BrushTransitionProbe.FillA, 0.004f);

        root.On!.Value = true;   // logical flip → re-render with FillB/TextB
        host.RunFrame();         // first frame: T has advanced one fixed dt (~16.7/83) — mid-fade
        var mid = ProbeRect(device).Fill;
        var midText = GlyphColor(device, strings, "bt");
        bool fillMid = mid.B > 0.05f && mid.B < 0.95f && mid.R > 0.05f && mid.R < 0.95f;
        bool textMid = midText.G > 0.05f && midText.G < 0.95f && midText.B > 0.05f && midText.B < 0.95f;

        for (int i = 0; i < 10; i++) host.RunFrame();   // ≥83ms of fixed frames → settled
        bool fillSettled = ColorClose(ProbeRect(device).Fill, BrushTransitionProbe.FillB, 0.004f);
        bool textSettled = ColorClose(GlyphColor(device, strings, "bt"), BrushTransitionProbe.TextB, 0.004f);
        bool idle = !host.HasActiveWork;

        Check("E3.a BrushTransition cross-fades the fill on a logical flip (mid ≠ snap, settles exact)",
            restingA && fillMid && fillSettled, $"rest={restingA} mid=({mid.R:0.##},{mid.G:0.##},{mid.B:0.##}) settled={fillSettled}");
        Check("E3.b BrushTransition cross-fades the text foreground too, then the loop idles",
            textMid && textSettled && idle, $"mid=({midText.R:0.##},{midText.G:0.##},{midText.B:0.##}) settled={textSettled} idle={idle}");
    }

    static void FocusNavChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);

        // P6.a — TabIndex orders tab navigation: document order A,B,C but TabIndex 3,1,2 → visits B→C→A.
        {
            var scene = new SceneStore();
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Direction = 0, Gap = 10,
                Children =
                [
                    new BoxEl { Key = "A", Width = 20, Height = 20, OnClick = () => { }, TabIndex = 3 },
                    new BoxEl { Key = "B", Width = 20, Height = 20, OnClick = () => { }, TabIndex = 1 },
                    new BoxEl { Key = "C", Width = 20, Height = 20, OnClick = () => { }, TabIndex = 2 },
                ],
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            var a = Child(scene, scene.Root, 0); var b = Child(scene, scene.Root, 1); var c = Child(scene, scene.Root, 2);
            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Tab) }); var f1 = disp.Focused;
            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Tab) }); var f2 = disp.Focused;
            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Tab) }); var f3 = disp.Focused;
            Check("W1-P6.a TabIndex orders tab navigation (1→2→3, not document order)",
                f1 == b && f2 == c && f3 == a, $"f1=B?{f1 == b} f2=C?{f2 == c} f3=A?{f3 == a}");
        }

        // P6.b — XY arrow navigation across a 2×2 grid: Right→Down→Left→Up walks TL→TR→BR→BL→TL.
        {
            var scene = new SceneStore();
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Direction = 1, Gap = 10,
                Children =
                [
                    new BoxEl { Direction = 0, Gap = 10, Children = [
                        new BoxEl { Key = "TL", Width = 30, Height = 20, OnClick = () => { } },
                        new BoxEl { Key = "TR", Width = 30, Height = 20, OnClick = () => { } } ] },
                    new BoxEl { Direction = 0, Gap = 10, Children = [
                        new BoxEl { Key = "BL", Width = 30, Height = 20, OnClick = () => { } },
                        new BoxEl { Key = "BR", Width = 30, Height = 20, OnClick = () => { } } ] },
                ],
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            var r0 = Child(scene, scene.Root, 0); var r1 = Child(scene, scene.Root, 1);
            var tl = Child(scene, r0, 0); var tr = Child(scene, r0, 1);
            var bl = Child(scene, r1, 0); var br = Child(scene, r1, 1);
            disp.SetFocus(tl, visual: true);
            disp.MoveFocusArrow(FocusDirection.Right); var aRight = disp.Focused;
            disp.MoveFocusArrow(FocusDirection.Down); var aDown = disp.Focused;
            disp.MoveFocusArrow(FocusDirection.Left); var aLeft = disp.Focused;
            disp.MoveFocusArrow(FocusDirection.Up); var aUp = disp.Focused;
            Check("W1-P6.b arrow XY nav walks the 2×2 grid (R→D→L→U)",
                aRight == tr && aDown == br && aLeft == bl && aUp == tl,
                $"R=TR?{aRight == tr} D=BR?{aDown == br} L=BL?{aLeft == bl} U=TL?{aUp == tl}");
        }

        // P6.c — scoped roving: NextFocusableIn cycles within a subtree and never escapes to an outside focusable.
        {
            var scene = new SceneStore();
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Direction = 1,
                Children =
                [
                    new BoxEl { Key = "outside", Width = 20, Height = 20, OnClick = () => { } },
                    new BoxEl { Key = "sub", Direction = 0, Gap = 4, Children = [
                        new BoxEl { Key = "s1", Width = 20, Height = 20, OnClick = () => { } },
                        new BoxEl { Key = "s2", Width = 20, Height = 20, OnClick = () => { } },
                        new BoxEl { Key = "s3", Width = 20, Height = 20, OnClick = () => { } } ] },
                ],
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            var outside = Child(scene, scene.Root, 0);
            var sub = Child(scene, scene.Root, 1);
            var s1 = Child(scene, sub, 0); var s3 = Child(scene, sub, 2);
            var next = disp.NextFocusableIn(sub, s1);          // s1 → s2
            var wrap = disp.NextFocusableIn(sub, s3);          // s3 → s1 (cycles, never escapes to 'outside')
            var first = disp.FirstFocusableIn(sub);
            var last = disp.LastFocusableIn(sub);
            Check("W1-P6.c scoped roving cycles within a subtree (never escapes)",
                next == Child(scene, sub, 1) && wrap == s1 && first == s1 && last == s3 && first != outside,
                $"next=s2?{next == Child(scene, sub, 1)} wrap=s1?{wrap == s1} first=s1?{first == s1} last=s3?{last == s3}");
        }
    }

    // Wave 1 / P4a — authored clip-rect channel: AnimEngine ClipL/T/R/B drive NodePaint.ClipRect (node-local); the
    // recorder intersects it into the child clip. Un-animated edges default to the node box; settling clears the override.
    static void ClipChannelChecks()
    {
        var scene = new SceneStore();
        var node = scene.CreateNode(1);
        scene.Root = node;
        scene.Bounds(node) = new RectF(0f, 0f, 100f, 80f);
        var engine = new AnimEngine(scene);

        bool startInfinite = scene.Paint(node).ClipRect.IsInfinite;   // no clip before any animation

        // Reveal the bottom edge 0 → 80 (a one-edge clip; L/T/R default to the node box → only the bottom clips).
        engine.Animate(node, AnimChannel.ClipB, 0f, 80f, 100f, Easing.Linear);
        engine.Tick(0f);
        engine.Tick(50f);
        var mid = scene.Paint(node).ClipRect;
        bool applied = !mid.IsInfinite && Near(mid.X, 0f) && Near(mid.Y, 0f) && Near(mid.W, 100f) && Near(mid.H, 40f, 2f);

        engine.Tick(60f);   // 110ms total → animation completes and the clip override clears
        bool reset = scene.Paint(node).ClipRect.IsInfinite;

        Check("W1-P4a.a clip-rect channel applies mid-anim (bottom reveal), resets on settle",
            startInfinite && applied && reset,
            $"mid=({mid.X:0},{mid.Y:0},{mid.W:0},{mid.H:0}) start∞={startInfinite} reset∞={reset}");
    }

    // Wave 1 / P1 — the engine disabled gate: a single NodeFlags.Disabled bit gates hit-test, focus, and keyboard
    // activation, replacing each control's hand-rolled handler-nulling. Visuals stay control-chosen.
    static void DisabledChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("disabled", new Size2(320, 240), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var root = new DisabledProbe();
        using var host = new AppHost(app, window, device, fonts, strings, root);

        host.RunFrame();   // mount — the gated box starts disabled

        // disabled-no-hit: clicking the disabled box invokes nothing; the always-enabled box still clicks.
        ClickNode(host, window, root.GatedBox);
        int gatedAfterDisabledClick = root.GatedClicks;
        ClickNode(host, window, root.EnabledBox);
        Check("W1-P1.a disabled node does not hit-test (click swallowed); enabled still clicks",
            gatedAfterDisabledClick == 0 && root.EnabledClicks == 1,
            $"gated={gatedAfterDisabledClick} enabled={root.EnabledClicks}");

        // disabled-no-focus: Tab skips the disabled box → focus lands on the only enabled focusable.
        window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Tab));
        host.RunFrame();
        bool focusEnabled = FocusedNode(host.Scene, host.Scene.Root) == root.EnabledBox;
        bool gatedNotFocused = (host.Scene.Flags(root.GatedBox) & NodeFlags.Focused) == 0;
        Check("W1-P1.b disabled node is not a tab stop (focus skips it)", focusEnabled && gatedNotFocused,
            $"focusEnabled={focusEnabled} gatedFocused={!gatedNotFocused}");

        // disabled-no-key-activate: Enter activates the focused ENABLED box (pressed on down, click on key-UP — the
        // WinUI ClickMode.Release contract); the disabled box never key-activates.
        int beforeEnter = root.EnabledClicks;
        window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Enter));
        window.QueueInput(new InputEvent(InputKind.KeyUp, default, 0, Keys.Enter));
        host.RunFrame();
        Check("W1-P1.c Enter activates the focused enabled node; disabled never key-activates",
            root.EnabledClicks == beforeEnter + 1 && root.GatedClicks == 0,
            $"enabled={root.EnabledClicks} gated={root.GatedClicks}");

        // disabled-toggle-reenables: flip IsEnabled via the signal → the box now hit-tests and clicks.
        root.Gate!.Value = true;
        host.RunFrame();
        ClickNode(host, window, root.GatedBox);
        Check("W1-P1.d flipping IsEnabled re-enables hit-test (Mark/Unmark each reconcile)",
            root.GatedClicks == 1, $"gated={root.GatedClicks}");

        // zero-alloc: the gate is a flag bittest — steady idle frames allocate nothing on the paint half.
        for (int i = 0; i < 6; i++) host.RunFrame();
        var steady = host.RunFrame();
        Check("W1-P1.e disabled gate adds no steady-state allocation", steady.HotPhaseAllocBytes == 0,
            $"{steady.HotPhaseAllocBytes} bytes");
    }

    // Wave 1 / P2 — stateful text/glyph foreground ramps: a TextEl under an interactive box inherits the box's eased
    // hover/press progress (no per-control animator) and steps to a disabled color via the P1 gate flag on its ancestor.
    static void TextRampChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("textramp", new Size2(320, 200), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var root = new TextRampProbe();
        using var host = new AppHost(app, window, device, fonts, strings, root);

        host.RunFrame();   // mount
        var box = host.Scene.Root;
        var c = CenterOf(host.Scene, box);
        var outside = new Point2(c.X + 300f, c.Y + 300f);

        var rest = GlyphColor(device, strings, "ramp");
        bool restOk = rest.R > 0.5f && rest.G < 0.2f && rest.B < 0.2f;   // resting = red

        // hover → green (eased through the ancestor box's interaction progress)
        window.QueueInput(new InputEvent(InputKind.PointerMove, c, 0, 0));
        for (int i = 0; i < 24; i++) host.RunFrame();
        var hov = GlyphColor(device, strings, "ramp");
        bool hovOk = hov.G > 0.5f && hov.R < 0.2f;

        // press → blue (press composes over hover)
        window.QueueInput(new InputEvent(InputKind.PointerDown, c, 0, 0));
        for (int i = 0; i < 24; i++) host.RunFrame();
        var prs = GlyphColor(device, strings, "ramp");
        bool prsOk = prs.B > 0.5f && prs.G < 0.2f;

        // release + leave → back to red
        window.QueueInput(new InputEvent(InputKind.PointerUp, c, 0, 0));
        window.QueueInput(new InputEvent(InputKind.PointerMove, outside, 0, 0));
        for (int i = 0; i < 24; i++) host.RunFrame();
        var back = GlyphColor(device, strings, "ramp");
        bool backOk = back.R > 0.5f && back.G < 0.2f;

        Check("W1-P2.a text foreground ramps: hover→green, press→blue, release→red",
            restOk && hovOk && prsOk && backOk,
            $"rest=({rest.R:0.0},{rest.G:0.0},{rest.B:0.0}) hov=({hov.R:0.0},{hov.G:0.0},{hov.B:0.0}) prs=({prs.R:0.0},{prs.G:0.0},{prs.B:0.0}) back=({back.R:0.0},{back.G:0.0},{back.B:0.0})");

        // disabled → white (a step, regardless of pointer position; gated by the ancestor's NodeFlags.Disabled)
        root.Enabled!.Value = false;
        host.RunFrame();
        var dis = GlyphColor(device, strings, "ramp");
        Check("W1-P2.b disabled text uses the DisabledColor step", dis.R > 0.8f && dis.G > 0.8f && dis.B > 0.8f,
            $"dis=({dis.R:0.00},{dis.G:0.00},{dis.B:0.00})");

        // zero-alloc: the resolve walks ancestors with struct reads only — steady idle frames allocate nothing.
        for (int i = 0; i < 6; i++) host.RunFrame();
        var steady = host.RunFrame();
        Check("W1-P2.c text ramp adds no steady-state allocation", steady.HotPhaseAllocBytes == 0, $"{steady.HotPhaseAllocBytes} bytes");
    }

    // Wave 1 / P4b — stateful gradient transitions: the recorder per-frame interpolates the resting gradient's stops
    // toward the hover/pressed gradients by the eased progress (no new GradientSpec per frame).
    static void GradientRampChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("gradramp", new Size2(320, 200), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var root = new GradientRampProbe();
        using var host = new AppHost(app, window, device, fonts, strings, root);

        host.RunFrame();
        var c = CenterOf(host.Scene, host.Scene.Root);
        var outside = new Point2(c.X + 300f, c.Y + 300f);

        var rest = FirstGradientC0(device);
        bool restOk = rest.R > 0.5f && rest.G < 0.2f && rest.B < 0.2f;

        window.QueueInput(new InputEvent(InputKind.PointerMove, c, 0, 0));
        for (int i = 0; i < 24; i++) host.RunFrame();
        var hov = FirstGradientC0(device);
        bool hovOk = hov.G > 0.5f && hov.R < 0.2f;

        window.QueueInput(new InputEvent(InputKind.PointerDown, c, 0, 0));
        for (int i = 0; i < 24; i++) host.RunFrame();
        var prs = FirstGradientC0(device);
        bool prsOk = prs.B > 0.5f && prs.G < 0.2f;

        window.QueueInput(new InputEvent(InputKind.PointerUp, c, 0, 0));
        window.QueueInput(new InputEvent(InputKind.PointerMove, outside, 0, 0));
        for (int i = 0; i < 24; i++) host.RunFrame();
        var back = FirstGradientC0(device);
        bool backOk = back.R > 0.5f && back.G < 0.2f;

        Check("W1-P4b.a gradient fill ramps: hover→green, press→blue, release→red",
            restOk && hovOk && prsOk && backOk,
            $"rest=({rest.R:0.0},{rest.G:0.0},{rest.B:0.0}) hov=({hov.R:0.0},{hov.G:0.0},{hov.B:0.0}) prs=({prs.R:0.0},{prs.G:0.0},{prs.B:0.0}) back=({back.R:0.0},{back.G:0.0},{back.B:0.0})");

        for (int i = 0; i < 6; i++) host.RunFrame();
        var steady = host.RunFrame();
        Check("W1-P4b.b gradient ramp adds no steady-state allocation", steady.HotPhaseAllocBytes == 0, $"{steady.HotPhaseAllocBytes} bytes");
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
        var clock = new ManualFrameTimeSource();
        using var host = new AppHost(app, window, device, fonts, strings, root, frameTime: clock);
        host.RunFrame();

        var svc = root.Service!;
        Func<Element> menu = () => MenuFlyout.Build(new[]
        {
            new MenuFlyoutItem("One"), new MenuFlyoutItem("Two"), new MenuFlyoutItem("Three"),
            new MenuFlyoutItem("Four"), new MenuFlyoutItem("Five"),
        }, () => svc.CloseTop());

        NodeHandle SurfaceOf(NodeHandle n)
        {
            for (; !n.IsNull; n = host.Scene.Parent(n))
            {
                if (host.Scene.TryGetAcrylic(n, out _)) return n;
                // D67 menu chrome: the acrylic lives on the stretch PLATE (first child); the channels live on its parent.
                for (var c = host.Scene.FirstChild(n); !c.IsNull; c = host.Scene.NextSibling(c))
                    if (host.Scene.TryGetAcrylic(c, out _)) return n;
            }
            return NodeHandle.Null;
        }
        NodeHandle SmokeScrim()
        {
            NodeHandle best = NodeHandle.Null;
            float bestArea = 0f;
            void Walk(NodeHandle n)
            {
                if (n.IsNull) return;
                ref var p = ref host.Scene.Paint(n);
                var r = host.Scene.AbsoluteRect(n);
                float area = r.W * r.H;
                if (p.Fill.A > 0.1f && r.W >= 400f && r.H >= 320f && area > bestArea)
                {
                    best = n;
                    bestArea = area;
                }
                for (var c = host.Scene.FirstChild(n); !c.IsNull; c = host.Scene.NextSibling(c)) Walk(c);
            }
            Walk(host.Scene.Root);
            return best;
        }

        (bool Fading, bool Settled, bool NoPopBack, float MaxSurface, float MaxScrim) CloseChrome(PopupChrome chrome)
        {
            NodeHandle body = NodeHandle.Null;
            Func<Element> content = () => new BoxEl
            {
                Width = chrome == PopupChrome.TeachingTip ? 320f : 260f,
                Height = chrome == PopupChrome.TeachingTip ? 96f : 88f,
                Fill = Tok.FillCardDefault,
                OnRealized = h => body = h,
                Children = [new TextEl("overlay body") { Color = Tok.TextPrimary }],
            };
            var options = new PopupOptions(
                FocusTrap: chrome == PopupChrome.Modal,
                DismissBehavior: chrome == PopupChrome.Modal ? DismissBehavior.Modal : DismissBehavior.LightDismiss,
                Chrome: chrome);
            svc.Open(() => root.Anchor, content, FlyoutPlacement.BottomLeft, options);
            host.RunFrame();
            for (int i = 0; i < 24; i++) { clock.Advance(16f); host.RunFrame(); }

            NodeHandle surface = NodeHandle.Null;
            if (!body.IsNull)
            {
                if (chrome == PopupChrome.Flyout)
                {
                    surface = SurfaceOf(body);
                }
                else
                {
                    for (var n = host.Scene.Parent(body); !n.IsNull; n = host.Scene.Parent(n))
                    {
                        if ((host.Scene.Flags(n) & NodeFlags.ClipsToBounds) != 0) { surface = n; break; }
                    }
                }
            }
            NodeHandle scrim = chrome == PopupChrome.Modal ? SmokeScrim() : NodeHandle.Null;

            svc.CloseTop();
            host.RunFrame();
            clock.Advance(16f);
            host.RunFrame();
            float closeOp = !surface.IsNull && host.Scene.IsLive(surface) ? host.Scene.Paint(surface).Opacity : 0f;
            float closeScrim = !scrim.IsNull && host.Scene.IsLive(scrim) ? host.Scene.Paint(scrim).Opacity : 0f;
            // The TeachingTip close is the muxc CONTRACT animation: scale 1 → 20/Width over 200ms
            // cubic-bezier(0.7,0,1,0.5) with NO opacity keyframes (TeachingTip.cpp:1695-1712 contractAnimation;
            // TeachingTip.h:235 + :306-307) — the host-owned close is a live scale track, not a fade (the ease-in
            // curve has barely moved at a 16ms sample, so assert the seeded track; 64k samples the kinematics).
            bool fading = chrome == PopupChrome.TeachingTip
                ? !surface.IsNull && host.Scene.IsLive(surface) && host.Animation.HasTracks(surface)
                : !surface.IsNull && host.Scene.IsLive(surface) && closeOp < 0.99f
                    && (chrome != PopupChrome.Modal || (!scrim.IsNull && host.Scene.IsLive(scrim) && closeScrim < 0.99f));

            for (int i = 0; i < 16; i++) { clock.Advance(16f); host.RunFrame(); }
            bool settled = surface.IsNull || !host.Scene.IsLive(surface) || !host.Animation.HasTracks(surface);
            if (chrome == PopupChrome.Modal && !scrim.IsNull && host.Scene.IsLive(scrim))
                settled &= !host.Animation.HasTracks(scrim);

            bool noPopBack = true;
            float maxSurface = 0f;
            float maxScrim = 0f;
            for (int i = 0; i < 6; i++)
            {
                clock.Advance(16f);
                host.RunFrame();
                if (!surface.IsNull && host.Scene.IsLive(surface))
                {
                    float liveOp = host.Scene.Paint(surface).Opacity;
                    maxSurface = MathF.Max(maxSurface, liveOp);
                    if (liveOp > 0.01f) noPopBack = false;
                }
                if (!scrim.IsNull && host.Scene.IsLive(scrim))
                {
                    float liveScrim = host.Scene.Paint(scrim).Opacity;
                    maxScrim = MathF.Max(maxScrim, liveScrim);
                    if (liveScrim > 0.01f) noPopBack = false;
                }
            }
            return (fading, settled, noPopBack, maxSurface, maxScrim);
        }

        svc.Open(() => root.Anchor, menu, FlyoutPlacement.BottomLeft);
        host.RunFrame();   // mount + seed the authored clip-rect reveal + fade (seed runs in a layout effect)
        clock.Advance(16f);
        host.RunFrame();   // tick the animation once → the first ClipRect lands
        var surface = SurfaceOf(FindRole(host.Scene, host.Scene.Root, AutomationRole.MenuItem));
        float fullH = surface.IsNull ? 0f : host.Scene.Bounds(surface).H;
        RectF cr1 = surface.IsNull ? RectF.Infinite : host.Scene.Paint(surface).ClipRect;   // mid-reveal: finite, < full height
        bool revealing = !surface.IsNull && !cr1.IsInfinite && cr1.H > 1f && cr1.H < fullH - 2f;

        for (int i = 0; i < 24; i++) { clock.Advance(16f); host.RunFrame(); }             // open settles
        RectF cr2 = surface.IsNull ? RectF.Infinite : host.Scene.Paint(surface).ClipRect;
        bool revealed = cr2.IsInfinite;

        // Close → the surface fades (opacity animates down) while staying on top (not removed instantly).
        svc.CloseTop();
        host.RunFrame();
        clock.Advance(16f);
        host.RunFrame();
        float op = host.Scene.IsLive(surface) ? host.Scene.Paint(surface).Opacity : 0f;
        bool fading = host.Scene.IsLive(surface) && op < 0.99f;

        Check("64d. flyout: open clip-reveals (authored ClipRect) then close fades", revealing && revealed && fading,
            $"clipH {cr1.H:0}/{fullH:0}→{(cr2.IsInfinite ? "∞" : cr2.H.ToString("0"))} closeOpacity={op:0.00}");
        // Regression for overlay close "pop back": after the close track settles, later frames must not record the
        // surface visible while structural overlay removal catches up.
        for (int i = 0; i < 10; i++) { clock.Advance(16f); host.RunFrame(); }
        bool settled = surface.IsNull || !host.Scene.IsLive(surface) || !host.Animation.HasTracks(surface);
        bool noPopBack = true;
        float maxAfterSettle = 0f;
        for (int i = 0; i < 4; i++)
        {
            clock.Advance(16f);
            host.RunFrame();
            if (!surface.IsNull && host.Scene.IsLive(surface))
            {
                float liveOp = host.Scene.Paint(surface).Opacity;
                maxAfterSettle = MathF.Max(maxAfterSettle, liveOp);
                if (liveOp > 0.01f) noPopBack = false;
            }
        }
        Check("64e. overlay close settles without a one-frame pop-back", settled && noPopBack,
            $"settled={settled} maxOpacityAfterSettle={maxAfterSettle:0.00} live={(!surface.IsNull && host.Scene.IsLive(surface))}");

        var flyoutClose = CloseChrome(PopupChrome.Flyout);
        var modalClose = CloseChrome(PopupChrome.Modal);
        var teachingClose = CloseChrome(PopupChrome.TeachingTip);
        bool allChrome = flyoutClose.Fading && flyoutClose.Settled && flyoutClose.NoPopBack
            && modalClose.Fading && modalClose.Settled && modalClose.NoPopBack
            && teachingClose.Fading && teachingClose.Settled && teachingClose.NoPopBack;
        Check("64f. overlay close lifecycle is host-owned for flyout, modal scrim+card, and TeachingTip",
            allChrome,
            $"flyout=({flyoutClose.Fading},{flyoutClose.Settled},max={flyoutClose.MaxSurface:0.00}) " +
            $"modal=({modalClose.Fading},{modalClose.Settled},card={modalClose.MaxSurface:0.00},scrim={modalClose.MaxScrim:0.00}) " +
            $"tip=({teachingClose.Fading},{teachingClose.Settled},max={teachingClose.MaxSurface:0.00})");

        // ── 64g–64l. Per-kind WinUI motion: KEYFRAME SAMPLES at known t (ManualFrameTimeSource). ──
        // cubic-bezier(0,0,0,1) (the WinUI "control fast out / slow in" curve used by MenuPopup/Split open) has the
        // closed form y(x) = 3·x^(2/3) − 2x (P1.x = P2.x = 0 ⇒ x(s) = s³): E(48/250)=0.6144, E(128/250)=0.8960,
        // E(48/167)=0.7320 — the expected sample values asserted below.
        {
            (NodeHandle Surface, OverlayHandle Handle) OpenKind(PopupChrome chrome, float w = 240f, float h = 88f)
            {
                NodeHandle body = NodeHandle.Null;
                var hd = svc.Open(() => root.Anchor,
                    () => new BoxEl { Width = w, Height = h, Fill = Tok.FillCardDefault, OnRealized = n => body = n },
                    FlyoutPlacement.BottomLeft,
                    new PopupOptions(Chrome: chrome));
                host.RunFrame();   // mount + place + seed (layout effect)
                host.RunFrame();   // compose the t=0 keyframes
                NodeHandle s = SurfaceOf(body);
                if (s.IsNull)      // bare chromes (Raw/TeachingTip) have no acrylic — find the clipping surface box
                    for (var n = host.Scene.Parent(body); !n.IsNull; n = host.Scene.Parent(n))
                        if ((host.Scene.Flags(n) & NodeFlags.ClipsToBounds) != 0) { s = n; break; }
                return (s, hd);
            }
            void SettleAll() { for (int i = 0; i < 40; i++) { clock.Advance(16f); host.RunFrame(); } }

            // 64g — menus (MenuPopupThemeTransition load, LayoutTransition_partial.cpp:441-473): clip-translate and
            // content translate BOTH go ±H·ClosedRatio(0.5) → 0 over s_OpenDuration=250ms cubic-bezier(0,0,0,1)
            // (MenuPopupThemeTransition_Partial.h:24; cpp:443-444), with NO presenter opacity at load (only the
            // overlay element fades, cpp:508-519). Unload = 83ms linear fade (cpp:525-531, _Partial.h:23).
            {
                var (s, _) = OpenKind(PopupChrome.Flyout);
                float menuH = host.Scene.Bounds(s).H, slide = menuH * 0.5f;
                var p0 = host.Scene.Paint(s);
                bool t0 = Near(p0.ClipRect.Y, slide, 1f) && Near(p0.LocalTransform.Dy, -slide, 1f) && p0.Opacity > 0.99f;
                clock.Advance(128f); host.RunFrame();
                var p1 = host.Scene.Paint(s);
                bool t128 = Near(p1.ClipRect.Y, slide * (1f - 0.8960f), 1f)
                    && Near(p1.LocalTransform.Dy, -slide * (1f - 0.8960f), 1f) && p1.Opacity > 0.99f;
                clock.Advance(160f); host.RunFrame();   // t=288 > 250 → settled
                var p2 = host.Scene.Paint(s);
                bool tEnd = p2.ClipRect.IsInfinite && Near(p2.LocalTransform.Dy, 0f, 0.1f);
                svc.CloseTop(); host.RunFrame();
                clock.Advance(48f); host.RunFrame();
                float op48 = host.Scene.IsLive(s) ? host.Scene.Paint(s).Opacity : -1f;
                bool close48 = Near(op48, 1f - 48f / 83f, 0.02f);   // 83ms LINEAR fade → 0.4217 at t=48
                SettleAll();
                Check("64g. menu motion samples: clip+translate H/2→0 over 250ms cb(0,0,0,1), no open fade; close = 83ms linear fade",
                    t0 && t128 && tEnd && close48,
                    $"t0={t0} (clipT={p0.ClipRect.Y:0.0}/{slide:0.0} dy={p0.LocalTransform.Dy:0.0}) t128={t128} " +
                    $"(clipT={p1.ClipRect.Y:0.0}≈{slide * 0.104f:0.0}) end={tEnd} close48={close48} (op={op48:0.000}≈{1f - 48f / 83f:0.000})");
            }

            // 64h — ComboBox dropdown (SplitOpen/SplitCloseThemeAnimation, generic.xaml:9047/9056). Open: the clip
            // grows from closedRatio 0.50 over 250ms cubic-bezier(0,0,0,1) with NO content translate (the template
            // sets no ContentTranslationOffset, ThemeAnimations.cpp:692-711) and NO fade (opacity pinned 1,
            // cpp:684). Close: clip collapses to closedRatio 0.15 over s_CloseDuration=167ms (cpp:741 + 826-828),
            // opacity fades only during the LAST 83ms (s_OpacityChangeBeginTime = 167−83 = 84,
            // SplitCloseThemeAnimation_Partial.h:16-18).
            {
                var (s, _) = OpenKind(PopupChrome.Dropdown);
                float ddH = host.Scene.Bounds(s).H;
                var p0 = host.Scene.Paint(s);
                bool t0 = Near(p0.ClipRect.H, ddH * 0.5f, 1f) && Near(p0.LocalTransform.Dy, 0f, 0.1f) && p0.Opacity > 0.99f;
                clock.Advance(128f); host.RunFrame();
                var p1 = host.Scene.Paint(s);
                bool t128 = Near(p1.ClipRect.H, ddH * (0.5f + 0.5f * 0.8960f), 1f)
                    && Near(p1.LocalTransform.Dy, 0f, 0.1f) && p1.Opacity > 0.99f;
                clock.Advance(160f); host.RunFrame();
                bool tEnd = host.Scene.Paint(s).ClipRect.IsInfinite;
                svc.CloseTop(); host.RunFrame();
                clock.Advance(48f); host.RunFrame();
                var c48 = host.Scene.Paint(s);
                // t=48 < 84ms begin time → STILL FULLY OPAQUE while the clip is already collapsing: E(48/167)=0.7320
                // → clipH = H·(1 − 0.85·0.7320) = 0.3778·H.
                bool close48 = c48.Opacity > 0.99f && Near(c48.ClipRect.H, ddH * (1f - 0.85f * 0.7320f), 1.5f);
                clock.Advance(64f); host.RunFrame();   // t=112 → fade progress (112−84)/83 = 0.3373
                float op112 = host.Scene.IsLive(s) ? host.Scene.Paint(s).Opacity : -1f;
                bool close112 = Near(op112, 1f - (112f - 84f) / 83f, 0.03f);
                SettleAll();
                Check("64h. ComboBox dropdown samples: SplitOpen clip 0.5H→H 250ms no translate/fade; SplitClose 167ms clip→0.15H + fade only after 84ms",
                    t0 && t128 && tEnd && close48 && close112,
                    $"t0={t0} (clipH={p0.ClipRect.H:0.0}/{ddH * 0.5f:0.0}) t128={t128} (clipH={p1.ClipRect.H:0.0}≈{ddH * 0.948f:0.0}) " +
                    $"end={tEnd} close48={close48} (op={c48.Opacity:0.00} clipH={c48.ClipRect.H:0.0}≈{ddH * 0.3778f:0.0}) close112={close112} (op={op112:0.000})");
            }

            // 64i — Flyout/FlyoutPresenter (PopupThemeTransition → TAS_SHOWPOPUP/TAS_HIDEPOPUP; FlyoutBase attaches it,
            // FlyoutBase_Partial.cpp:1968-1975). OS PVL ground truth (uxtheme "Animations" storyboard 18/19 dump,
            // stock Windows 11): TRANSLATE offset→0 over 367ms cubic-bezier(0.1,0.9,0.2,1.0); OPACITY 0→1 with
            // start=83ms dur=83ms LINEAR (holds 0 for the first 83ms); hide = OPACITY →0 over 83ms linear. The
            // offset is ±g_entranceThemeOffset = 50px (FlyoutBase_Partial.cpp:68 + 2024-2059): below-anchor starts
            // 50px above its resting spot.
            {
                var (s, _) = OpenKind(PopupChrome.Popup);
                var p0 = host.Scene.Paint(s);
                bool t0 = Near(p0.LocalTransform.Dy, -50f, 1f) && p0.Opacity < 0.01f;
                clock.Advance(48f); host.RunFrame();
                var p1 = host.Scene.Paint(s);
                bool t48 = p1.Opacity < 0.01f                       // still inside the 83ms opacity hold
                    && p1.LocalTransform.Dy > -25f && p1.LocalTransform.Dy < -10f;   // decel curve well underway
                clock.Advance(80f); host.RunFrame();                // t=128 → fade progress (128−83)/83 = 0.5422
                var p2 = host.Scene.Paint(s);
                bool t128 = Near(p2.Opacity, (128f - 83f) / 83f, 0.03f)
                    && p2.LocalTransform.Dy > -8f && p2.LocalTransform.Dy < -0.5f;
                clock.Advance(320f); host.RunFrame();               // t=448 > 367 → settled
                var p3 = host.Scene.Paint(s);
                bool tEnd = Near(p3.LocalTransform.Dy, 0f, 0.1f) && p3.Opacity > 0.99f;
                svc.CloseTop(); host.RunFrame();
                clock.Advance(48f); host.RunFrame();
                float op48 = host.Scene.IsLive(s) ? host.Scene.Paint(s).Opacity : -1f;
                bool close48 = Near(op48, 1f - 48f / 83f, 0.02f);   // TAS_HIDEPOPUP: 83ms linear
                SettleAll();
                Check("64i. Flyout samples: PopupThemeTransition slide −50→0 over 367ms decel + opacity 0 held 83ms then 83ms linear; close 83ms fade",
                    t0 && t48 && t128 && tEnd && close48,
                    $"t0={t0} (dy={p0.LocalTransform.Dy:0.0} op={p0.Opacity:0.00}) t48={t48} (dy={p1.LocalTransform.Dy:0.0} op={p1.Opacity:0.00}) " +
                    $"t128={t128} (op={p2.Opacity:0.000}≈{(128f - 83f) / 83f:0.000} dy={p2.LocalTransform.Dy:0.0}) end={tEnd} close48={close48} (op={op48:0.000})");
            }

            // 64j — ToolTip (FadeIn/FadeOutThemeAnimation, the template's Opened/Closed states,
            // ToolTip_themeresources.xaml:56-70): TAS_FADEIN/TAS_FADEOUT = OPACITY over 167ms LINEAR both ways
            // (OS PVL storyboard 4/5 dump; WinTheme.cpp:165-182).
            {
                var (s, _) = OpenKind(PopupChrome.Raw);
                clock.Advance(80f); host.RunFrame();
                float opIn = host.Scene.Paint(s).Opacity;
                bool in80 = Near(opIn, 80f / 167f, 0.02f);
                SettleAll();
                bool inEnd = host.Scene.Paint(s).Opacity > 0.99f;
                svc.CloseTop(); host.RunFrame();
                clock.Advance(80f); host.RunFrame();
                float opOut = host.Scene.IsLive(s) ? host.Scene.Paint(s).Opacity : -1f;
                bool out80 = Near(opOut, 1f - 80f / 167f, 0.02f);
                SettleAll();
                Check("64j. ToolTip samples: FadeIn/FadeOut 167ms linear (TAS_FADEIN/TAS_FADEOUT)",
                    in80 && inEnd && out80,
                    $"in80={in80} (op={opIn:0.000}≈{80f / 167f:0.000}) inEnd={inEnd} out80={out80} (op={opOut:0.000}≈{1f - 80f / 167f:0.000})");
            }

            // 64k — TeachingTip (muxc expand/contract): expand scale Min(0.01, 20/W)→1 over 300ms
            // cubic-bezier(0.1,0.9,0.2,1) (TeachingTip.cpp:1660-1664; TeachingTip.h:234+304-305); contract 1→20/W
            // over 200ms cubic-bezier(0.7,0,1,0.5) (cpp:1695-1712; h:235+306-307). NEITHER storyboard has opacity
            // keyframes — the tip is fully opaque while scaling.
            {
                var (s, _) = OpenKind(PopupChrome.TeachingTip, w: 320f, h: 96f);
                var p0 = host.Scene.Paint(s);
                bool t0 = Near(p0.LocalTransform.M11, 0.01f, 0.005f) && p0.Opacity > 0.99f;
                clock.Advance(160f); host.RunFrame();
                bool mid = host.Scene.Paint(s).Opacity > 0.99f;   // no fade at any point of the expand
                SettleAll();
                var pEnd = host.Scene.Paint(s);
                bool tEnd = Near(pEnd.LocalTransform.M11, 1f, 0.01f) && pEnd.Opacity > 0.99f;
                svc.CloseTop(); host.RunFrame();
                clock.Advance(208f); host.RunFrame();             // ≥200ms → contract target reached
                var pC = host.Scene.Paint(s);
                bool contracted = !host.Scene.IsLive(s) || Near(pC.LocalTransform.M11, 20f / 320f, 0.01f) || pC.Opacity < 0.01f;
                SettleAll();
                Check("64k. TeachingTip samples: expand scale 0.01→1 (300ms), contract →20/W (200ms), NO opacity keyframes",
                    t0 && mid && tEnd && contracted,
                    $"t0={t0} (sx={p0.LocalTransform.M11:0.000}) mid={mid} end={tEnd} (sx={pEnd.LocalTransform.M11:0.00}) contracted={contracted}");
            }

            // 64l — AutoSuggestBox suggestions (a bare WinUI Popup, no TransitionCollection in the template and
            // AutoSuggestBox_Partial.cpp attaches none): NO open/close animation — instant in, instant out.
            {
                var (s, _) = OpenKind(PopupChrome.Static);
                var p0 = host.Scene.Paint(s);
                bool instantIn = p0.Opacity > 0.99f && p0.ClipRect.IsInfinite
                    && Near(p0.LocalTransform.Dy, 0f, 0.1f) && !host.Animation.HasTracks(s);
                svc.CloseTop(); host.RunFrame();
                bool instantOut = !host.Scene.IsLive(s) || host.Scene.Paint(s).Opacity < 0.01f;
                SettleAll();
                Check("64l. AutoSuggest popup: bare Popup — no open/close animation (instant show/hide)",
                    instantIn && instantOut, $"in={instantIn} out={instantOut}");
            }
        }
    }

    // ── E4 — popup windowing: out-of-bounds popup WINDOWS (WinUI windowed CPopup — Popup_Partial.cpp:951-970,
    // FlyoutBase_Partial.cpp:3181-3205 SetIsWindowedPopup), monitor work-area placement (FlyoutBase_Partial.cpp:
    // 3382-3392 useMonitorBounds), window-deactivation light dismiss (Popup_Partial.h:38 DismissalTriggerFlags),
    // the full FlyoutPlacementMode matrix + fallback (FlyoutBase_Partial.cpp:2503-2659), ToolTipService timing
    // (ToolTipService_Partial.cpp:1771-1780), focus-restore-at-close-START (Popup_Partial.h:63-64 SavedFocusState)
    // and nested cascade close (MenuFlyoutSubItem child-first close order). All headless.
    static void E4PopupWindowingChecks(StringTable strings)
    {
        // e4popup.1 — WindowBlur (WM_ACTIVATE WA_INACTIVE) closes every LIGHT-DISMISS overlay; Modal (ContentDialog)
        // and None (ToolTip) stay. WinUI: DismissalTriggerFlags::WindowDeactivated is part of the default
        // light-dismiss trigger set (Popup_Partial.h:38); ContentDialog's modal popup excludes it; ToolTipService
        // owns its tooltip's close itself.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("e4blur", new Size2(480, 360), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new OverlayProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var svc = root.Service!;

            static Func<Element> Body(string label) => () => new BoxEl
            {
                Width = 120, Height = 40,
                Children = [new TextEl(label) { Size = 12f }],
            };
            var light = svc.Open(() => root.Anchor, Body("ld-body"), FlyoutPlacement.BottomLeft);
            var modal = svc.Open(() => root.Anchor, Body("modal-body"), FlyoutPlacement.BottomLeft,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.Modal, Chrome: PopupChrome.Modal));
            var none = svc.Open(() => root.Anchor, Body("none-body"), FlyoutPlacement.BottomLeft,
                new PopupOptions(DismissBehavior: DismissBehavior.None, Chrome: PopupChrome.Raw));
            host.RunFrame();
            bool allOpen = !FindTextNode(host.Scene, strings, host.Scene.Root, "ld-body").IsNull
                && !FindTextNode(host.Scene, strings, host.Scene.Root, "modal-body").IsNull
                && !FindTextNode(host.Scene, strings, host.Scene.Root, "none-body").IsNull;

            window.QueueInput(new InputEvent(InputKind.WindowBlur, default, 0, 0));
            host.RunFrame();
            bool lightClosedNow = !light.IsOpen && modal.IsOpen && none.IsOpen;
            for (int i = 0; i < 20; i++) host.RunFrame();   // the 83ms close fade settles → entry finalized
            bool after = FindTextNode(host.Scene, strings, host.Scene.Root, "ld-body").IsNull
                && !FindTextNode(host.Scene, strings, host.Scene.Root, "modal-body").IsNull
                && !FindTextNode(host.Scene, strings, host.Scene.Root, "none-body").IsNull;
            Check("e4popup.1 WindowBlur closes light-dismiss overlays only (Modal + None stay)",
                allOpen && lightClosedNow && after,
                $"allOpen={allOpen} closedNow={lightClosedNow} after={after}");
        }

        // e4popup.2 — work-area clamp math (pure FlyoutPositioner). Windowed popups collide against the MONITOR work
        // area passed as the container (FlyoutBase_Partial.cpp:3382-3392) and skip the final min-edge clamp
        // (cpp:2648-2653 — applied to non-windowed popups only), so they may extend above/left of the window.
        {
            // (a) min-edge clamp: a flyout fitting EXACTLY above lands at container-top − FlyoutMargin(4) after the
            // margin shift (cpp:2622-2646); non-windowed clamps back to the container origin, windowed keeps −4.
            var anchorA = new RectF(100, 60, 80, 20);
            var view = new RectF(0, 0, 480, 360);
            var pcA = FlyoutPositioner.Place(anchorA, new Size2(100, 60), view, FlyoutPlacement.TopEdgeAlignedLeft, isWindowed: false);
            var pwA = FlyoutPositioner.Place(anchorA, new Size2(100, 60), view, FlyoutPlacement.TopEdgeAlignedLeft, isWindowed: true);
            bool minEdge = Near(pcA.X, 100) && Near(pcA.Y, 0) && pcA.OpensUp
                        && Near(pwA.X, 100) && Near(pwA.Y, -4) && pwA.OpensUp;

            // (b) monitor container vs viewport: an anchor near the window top fits ABOVE against the work area
            // (negative window-DIP coords) but must FLIP BELOW when constrained to the viewport.
            var anchorB = new RectF(10, 10, 50, 20);
            var workB = new RectF(-500, -300, 1000, 400);
            var pwB = FlyoutPositioner.Place(anchorB, new Size2(100, 50), workB, FlyoutPlacement.TopEdgeAlignedLeft, isWindowed: true);
            var pcB = FlyoutPositioner.Place(anchorB, new Size2(100, 50), view, FlyoutPlacement.TopEdgeAlignedLeft, isWindowed: false);
            bool monitor = pwB.OpensUp && Near(pwB.X, 10) && Near(pwB.Y, 10 - 50 - 4)
                        && !pcB.OpensUp && Near(pcB.Y, 30 + 4) && pcB.Placement == FlyoutPlacement.BottomEdgeAlignedLeft;

            // (c) secondary-axis clamp INTO the work area: a left-justified popup wider than the space right of the
            // anchor slides left to stay on the monitor (TestAndCenterAlignWithinLimits clamp, cpp:415-483).
            var anchorC = new RectF(940, 50, 50, 20);
            var workC = new RectF(0, 0, 1000, 400);
            var pwC = FlyoutPositioner.Place(anchorC, new Size2(200, 60), workC, FlyoutPlacement.BottomEdgeAlignedLeft, isWindowed: true);
            bool secondary = Near(pwC.X, 800) && Near(pwC.Y, 74) && !pwC.OpensUp;

            Check("e4popup.2 work-area clamp math: windowed skips the min-edge clamp, collides vs the monitor rect, clamps the secondary axis",
                minEdge && monitor && secondary,
                $"minEdge={minEdge} monitor={monitor} secondary={secondary} (pwA.Y={pwA.Y:0.#} pwB.Y={pwB.Y:0.#} pwC.X={pwC.X:0.#})");
        }

        // e4popup.3 — the windowed-popup headless pipeline: PopupOptions.ConstrainToRootBounds=false leases a PAL
        // popup window + its own swapchain, the subtree records into ITS OWN DrawList re-origined to the popup's
        // (0,0) (SceneRecorder.RecordSubtree) while the MAIN record skips it (skipRoots); default (true) keeps
        // today's in-window path, and PopupWindowsEnabled=false falls back silently (CPopup::
        // DoesPlatformSupportWindowedPopup == false, FlyoutBase_Partial.cpp:3188).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("e4win", new Size2(480, 360), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new OverlayProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var svc = root.Service!;
            var anchorRect = host.Scene.AbsoluteRect(root.Anchor);

            var hWin = svc.Open(() => root.Anchor,
                () => new BoxEl { Width = 180, Height = 80, Children = [new TextEl("popup-window-body") { Size = 12f }] },
                FlyoutPlacement.BottomLeft, new PopupOptions { ConstrainToRootBounds = false });
            host.RunFrame();

            bool leased = host.PopupWindows.Count == 1 && app.PopupWindows.Count == 1;
            PopupWindowSlot? slot = host.PopupWindows.Count > 0 ? host.PopupWindows[0] : null;
            HeadlessPopupWindow? pal = app.PopupWindows.Count > 0 ? app.PopupWindows[0] : null;
            bool shown = pal is { IsShown: true } && pal.ShowCount >= 1;
            // BottomLeft = below the anchor + FlyoutMargin 4; window scale 1 + client origin (0,0) ⇒ px == DIP.
            bool placed = slot is not null && pal is not null
                && Near(slot.BoundsDip.X, anchorRect.X) && Near(slot.BoundsDip.Y, anchorRect.Bottom + FlyoutPositioner.FlyoutMargin)
                && slot.BoundsDip.W >= 180f && slot.BoundsDip.H >= 80f
                && Near(pal.BoundsPx.X, slot.BoundsDip.X) && Near(pal.BoundsPx.Y, slot.BoundsDip.Y)
                && Near(pal.BoundsPx.W, slot.BoundsDip.W) && Near(pal.BoundsPx.H, slot.BoundsDip.H);

            for (int i = 0; i < 20; i++) host.RunFrame();   // the 250ms open clip-reveal settles → full content records

            var scratch = new HeadlessGpuDevice();          // decode the popup's own DrawList
            if (slot is not null)
                scratch.SubmitDrawList(slot.DrawList.Bytes, slot.DrawList.SortKeys,
                    new FrameInfo(new Size2(slot.BoundsDip.W, slot.BoundsDip.H), 1f, default));
            bool routed = HasGlyph(scratch, strings, "popup-window-body");
            bool reorigined = scratch.LastLayers.Count > 0   // the acrylic surface layer sits at the popup's own (0,0)
                && Near(scratch.LastLayers[0].DeviceRect.X, 0f, 1.5f) && Near(scratch.LastLayers[0].DeviceRect.Y, 0f, 1.5f);
            bool mainSkips = !HasGlyph(device, strings, "popup-window-body") && HasGlyph(device, strings, "anchor")
                && !FindTextNode(host.Scene, strings, host.Scene.Root, "popup-window-body").IsNull;   // scene keeps it (hit-test)
            bool presented = slot?.Swapchain is HeadlessSwapchain sc && sc.PresentCount >= 1;

            // Default (ConstrainToRootBounds = true): in-window popup — no window lease, content in the MAIN DrawList.
            var hIn = svc.Open(() => root.Anchor,
                () => new BoxEl { Width = 120, Height = 40, Children = [new TextEl("inwin-body") { Size = 12f }] },
                FlyoutPlacement.BottomLeft);
            host.RunFrame();
            bool defaultInWindow = app.PopupWindows.Count == 1 && HasGlyph(device, strings, "inwin-body");
            hIn.Close();
            for (int i = 0; i < 20; i++) host.RunFrame();

            // Close: a CLOSING entry keeps its popup window while it fades; the lease releases with the entry.
            hWin.Close();
            host.RunFrame();   // 16ms into the 83ms fade
            bool keptWhileFading = host.PopupWindows.Count == 1 && pal is { IsShown: true };
            for (int i = 0; i < 20; i++) host.RunFrame();
            bool released = host.PopupWindows.Count == 0 && pal is { Disposed: true, IsShown: false };

            // PopupWindowsEnabled = false (the DoesPlatformSupportWindowedPopup gate): silent constrained fallback.
            host.PopupWindowsEnabled = false;
            var hFb = svc.Open(() => root.Anchor,
                () => new BoxEl { Width = 120, Height = 40, Children = [new TextEl("fb-body") { Size = 12f }] },
                FlyoutPlacement.BottomLeft, new PopupOptions { ConstrainToRootBounds = false });
            for (int i = 0; i < 20; i++) host.RunFrame();
            bool fallback = app.PopupWindows.Count == 1 && HasGlyph(device, strings, "fb-body");
            hFb.Close();
            for (int i = 0; i < 20; i++) host.RunFrame();

            Check("e4popup.3 windowed popup: PAL lease + own DrawList/swapchain, main record skips the subtree; default + disabled stay in-window",
                leased && shown && placed && routed && reorigined && mainSkips && presented
                && defaultInWindow && keptWhileFading && released && fallback,
                $"leased={leased} shown={shown} placed={placed} routed={routed} reorig={reorigined} skip={mainSkips} present={presented} def={defaultInWindow} kept={keptWhileFading} rel={released} fb={fallback}");
        }

        // e4popup.4 — the GetWorkArea seam through the host: the work-area query lands at the anchor's centre in
        // physical virtual-screen px (client origin + scale, IPlatformWindow.ClientOriginPx), the windowed popup
        // flips ABOVE because the MONITOR (not the viewport) has no room below, may take negative window-DIP Y
        // (out-of-bounds), and the PAL window bounds round-trip DIP→px. A constrained popup at the same anchor
        // stays BELOW (the viewport has room) — the monitor flip is windowed-only behavior.
        {
            using var app = new HeadlessPlatformApp();
            Point2 queried = default;
            app.WorkAreaResolver = p => { queried = p; return new RectF(0f, 0f, 2000f, 800f); };
            var window = new HeadlessWindow(new WindowDesc("e4wa", new Size2(960, 720), 2f)) { ClientOriginPx = new Point2(1000f, 600f) };
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new OverlayProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var svc = root.Service!;
            var anchorRect = host.Scene.AbsoluteRect(root.Anchor);   // (20,20,120,32) DIP

            var hWin = svc.Open(() => root.Anchor,
                () => new BoxEl { Width = 180, Height = 80, Children = [new TextEl("wa-body") { Size = 12f }] },
                FlyoutPlacement.BottomLeft, new PopupOptions { ConstrainToRootBounds = false });
            host.RunFrame();

            bool leased = host.PopupWindows.Count == 1 && app.PopupWindows.Count == 1;
            PopupWindowSlot? slot = host.PopupWindows.Count > 0 ? host.PopupWindows[0] : null;
            HeadlessPopupWindow? pal = app.PopupWindows.Count > 0 ? app.PopupWindows[0] : null;
            bool query = Near(queried.X, 1000f + (anchorRect.X + anchorRect.W * 0.5f) * 2f)
                      && Near(queried.Y, 600f + (anchorRect.Y + anchorRect.H * 0.5f) * 2f);
            float hDip = slot?.BoundsDip.H ?? 0f;
            // Work area px (0,0,2000,800) → window DIP (−500,−300,1000,400): monitor bottom = 100 DIP < the popup's
            // below-anchor bottom ⇒ flips ABOVE the anchor, past the window top (negative DIP Y).
            bool flipped = slot is not null
                && Near(slot.BoundsDip.Y, anchorRect.Y - hDip - FlyoutPositioner.FlyoutMargin) && slot.BoundsDip.Y < 0f;
            bool px = slot is not null && pal is not null
                && Near(pal.BoundsPx.X, 1000f + anchorRect.X * 2f) && Near(pal.BoundsPx.Y, 600f + slot.BoundsDip.Y * 2f)
                && Near(pal.BoundsPx.W, slot.BoundsDip.W * 2f) && Near(pal.BoundsPx.H, hDip * 2f);
            bool inWork = pal is not null && pal.BoundsPx.Y >= 0f && pal.BoundsPx.Y + pal.BoundsPx.H <= 800f;

            var hIn = svc.Open(() => root.Anchor,
                () => new BoxEl { Width = 180, Height = 80, Children = [new TextEl("wa-in-body") { Size = 12f }] },
                FlyoutPlacement.BottomLeft);
            for (int i = 0; i < 20; i++) host.RunFrame();
            var inBody = FindTextNode(host.Scene, strings, host.Scene.Root, "wa-in-body");
            bool constrainedBelow = !inBody.IsNull && host.Scene.AbsoluteRect(inBody).Y > anchorRect.Bottom;

            Check("e4popup.4 GetWorkArea seam: px query at the anchor centre, monitor flip above the window (negative DIP Y), DIP→px bounds round-trip",
                leased && query && flipped && px && inWork && constrainedBelow,
                $"leased={leased} query={query}@({queried.X:0.#},{queried.Y:0.#}) flip={flipped}(dipY={slot?.BoundsDip.Y ?? 0f:0.#}) px={px} inWork={inWork} below={constrainedBelow}");
        }

        // e4popup.5 — the 8 edge-aligned FlyoutPlacementMode variants (FlyoutBase_Partial.cpp:84-110 major side +
        // :78-113 edge justification; TryPlacement cpp:415-483) with room on every side: exact rects including the
        // FlyoutMargin 4 shift away from the anchor (cpp:65 + 2622-2646).
        {
            var anchor = new RectF(200, 150, 80, 40);
            var c = new RectF(0, 0, 600, 500);
            var size = new Size2(100, 60);
            bool At(FlyoutPlacement p, float x, float y, bool up)
            {
                var r = FlyoutPositioner.Place(anchor, size, c, p, isWindowed: false);
                return Near(r.X, x) && Near(r.Y, y) && r.OpensUp == up && r.Placement == p;
            }
            bool tal = At(FlyoutPlacement.TopEdgeAlignedLeft, 200, 86, true);       // y = 150−60−4
            bool tar = At(FlyoutPlacement.TopEdgeAlignedRight, 180, 86, true);      // x = 280−100
            bool bal = At(FlyoutPlacement.BottomEdgeAlignedLeft, 200, 194, false);  // y = 190+4
            bool bar = At(FlyoutPlacement.BottomEdgeAlignedRight, 180, 194, false);
            bool lat = At(FlyoutPlacement.LeftEdgeAlignedTop, 96, 150, false);      // x = 200−100−4
            bool lab = At(FlyoutPlacement.LeftEdgeAlignedBottom, 96, 130, false);   // y = 190−60
            bool rat = At(FlyoutPlacement.RightEdgeAlignedTop, 284, 150, false);    // x = 280+4
            bool rab = At(FlyoutPlacement.RightEdgeAlignedBottom, 284, 130, false);
            Check("e4popup.5 the 8 edge-aligned placements produce the exact WinUI rects (major side + edge justify + FlyoutMargin 4)",
                tal && tar && bal && bar && lat && lab && rat && rab,
                $"TAL={tal} TAR={tar} BAL={bal} BAR={bar} LAT={lat} LAB={lab} RAT={rat} RAB={rab}");
        }

        // e4popup.6 — PerformPlacementWithFallback (FlyoutBase_Partial.cpp:488-537; per-side order cpp:2559-2593):
        // a side that can't fit walks its fallback order and REPORTS the effective placement; an edge justification
        // survives a vertical↔vertical (or horizontal↔horizontal) flip and centers on a perpendicular fallback.
        {
            var c = new RectF(0, 0, 600, 500);
            var size = new Size2(100, 60);

            var low = new RectF(50, 440, 80, 20);     // no room below → Bottom flips to Top, Left justify kept
            var pb = FlyoutPositioner.Place(low, size, c, FlyoutPlacement.BottomEdgeAlignedLeft, isWindowed: false);
            bool flipUp = pb.OpensUp && Near(pb.X, 50) && Near(pb.Y, 376) && pb.Placement == FlyoutPlacement.TopEdgeAlignedLeft;

            var right = new RectF(520, 100, 60, 30);  // no room right → Right flips to Left (order R,L,T,B), Top justify kept
            var pr = FlyoutPositioner.Place(right, size, c, FlyoutPlacement.RightEdgeAlignedTop, isWindowed: false);
            bool flipLeft = !pr.OpensUp && Near(pr.X, 416) && Near(pr.Y, 100) && pr.Placement == FlyoutPlacement.LeftEdgeAlignedTop;

            var wide = new RectF(10, 200, 280, 30);   // Left AND Right blocked → 3rd choice Top (order L,R,T,B), centered
            var narrow = new RectF(0, 0, 300, 500);
            var pt = FlyoutPositioner.Place(wide, size, narrow, FlyoutPlacement.Left, isWindowed: false);
            bool thirdChoice = pt.OpensUp && Near(pt.X, 100) && Near(pt.Y, 136) && pt.Placement == FlyoutPlacement.Top;

            Check("e4popup.6 placement fallback: Bottom→Top and Right→Left flips keep the justification; a blocked Left walks to Top centered",
                flipUp && flipLeft && thirdChoice,
                $"flipUp={flipUp} flipLeft={flipLeft} third={thirdChoice} (pb=({pb.X:0.#},{pb.Y:0.#}) pr=({pr.X:0.#},{pr.Y:0.#}) pt=({pt.X:0.#},{pt.Y:0.#}))");
        }

        // e4popup.7 — ToolTipService timing (ToolTipService_Partial.cpp:1771-1780): mouse initial show delay =
        // SPI_GETMOUSEHOVERTIME(400) × 2 = 800ms; auto-dismiss after SPI_GETMESSAGEDURATION = 5s
        // (DEFAULT_SHOW_DURATION_SECONDS, ToolTipService_Partial.h:19; the close timer is armed on automatic open,
        // cpp:429-459); a re-hover within BETWEEN_SHOW_DELAY_MS 200ms of the close (cpp:659, .h:17) uses the RESHOW
        // delay = 400ms — ×1, because the shipping static_cast<INT64>(1.5) truncates (cpp:1775).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("e4tip", new Size2(480, 360), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new E4ToolTipProbe();
            var clock = new ManualFrameTimeSource();
            using var host = new AppHost(app, window, device, fonts, strings, root, frameTime: clock);
            host.RunFrame();

            bool TipOpen() => !FindTextNode(host.Scene, strings, host.Scene.Root, "tip-body").IsNull;
            void Hover(float x, float y) { window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(x, y), 0, 0)); host.RunFrame(); }
            void Step(float ms) { clock.Advance(ms); host.RunFrame(); }
            // 0-dt frames: a just-seeded AnimEngine track consumes no dt on its FIRST tick (AnimEngine.cs JustSeeded —
            // idle time pending before the seed must not run a track early), and a finished track frees at the END of
            // the tick that crossed it with the clock firing one passive pass later. Polling at dt=0 drains that
            // frame-granularity latency WITHOUT advancing the clock — the ms thresholds below stay exact.
            void Poll() { for (int i = 0; i < 4; i++) host.RunFrame(); }

            Hover(50f, 50f);                  // pointer enters the target → the 800ms show-delay clock arms
            Poll();                           // the countdown track seeds + clears JustSeeded at 0ms
            Step(700f);
            Poll();
            bool notAt700 = !TipOpen();       // 700ms < 800 — must still be closed
            Step(150f);                       // 850ms total ≥ 800 → the delay elapses
            Poll();
            bool openAt800 = TipOpen();

            Poll();                           // the 5s auto-dismiss clock seeds with the open bubble
            Step(4800f);                      // dwell: 4.8s of the 5s auto-dismiss window
            Poll();
            bool stillOpenAt4800 = TipOpen();
            Step(300f);                       // ≥ 5000 → auto-dismiss fires
            Poll();
            for (int i = 0; i < 4; i++) Step(80f);   // the FadeOut (167ms) settles → bubble removed
            Poll();
            bool autoClosed = !TipOpen();

            Hover(10f, 10f);                  // auto-dismiss latches until a real owner leave
            Hover(52f, 50f);                  // re-enter < 200ms (wall) after the close → RESHOW delay (400ms)
            Poll();
            Step(300f);
            Poll();
            bool reshowNotAt300 = !TipOpen(); // 300ms < 400 — must still be closed
            Step(150f);                       // 450ms ≥ 400 → fires (an un-truncated 1.5× = 600ms would still be closed)
            Poll();
            bool reshowAt400 = TipOpen();

            Check("e4popup.7 ToolTip timing: 800ms initial show, 5s auto-dismiss, 400ms reshow inside the 200ms window",
                notAt700 && openAt800 && stillOpenAt4800 && autoClosed && reshowNotAt300 && reshowAt400,
                $"!700={notAt700} 800={openAt800} dwell={stillOpenAt4800} auto={autoClosed} !300={reshowNotAt300} 400={reshowAt400}");
        }

        // e4popup.8 — focus restores to the pre-open node when the close STARTS, not when the fade finishes: WinUI
        // restores the popup's SavedFocusState synchronously in Hide()/CPopup::Close (Popup_Partial.h:63-64;
        // FlyoutBase returns focus to the invoker on Hide, not after the close animation) — the popup is still on
        // screen fading while the invoker already has focus back.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("e4focus", new Size2(480, 360), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new OverlayProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var svc = root.Service!;

            ClickNode(host, window, root.Anchor);   // pre-open focus = the anchor
            bool anchorFocused0 = (host.Scene.Flags(root.Anchor) & NodeFlags.Focused) != 0;

            NodeHandle body = default;
            var h = svc.Open(() => root.Anchor, () => new BoxEl
            {
                Width = 140, Height = 60, Padding = Edges4.All(10),
                Children = [new BoxEl { Width = 100, Height = 24, Role = AutomationRole.Button, OnClick = () => { }, OnRealized = n => body = n }],
            }, FlyoutPlacement.BottomLeft);
            for (int i = 0; i < 18; i++) host.RunFrame();   // the open reveal settles
            ClickNode(host, window, body);                  // focus moves INTO the popup
            bool movedIn = (host.Scene.Flags(body) & NodeFlags.Focused) != 0;

            h.Close();   // the restore happens HERE — synchronously at close start
            bool restoredAtCloseStart = (host.Scene.Flags(root.Anchor) & NodeFlags.Focused) != 0;
            bool stillFadingNow = host.Scene.IsLive(body) && !h.IsOpen;
            host.RunFrame();   // 16ms into the 83ms fade — the popup is still alive on screen
            bool fadingAfterFrame = host.Scene.IsLive(body);
            for (int i = 0; i < 20; i++) host.RunFrame();
            bool removed = !host.Scene.IsLive(body) && (host.Scene.Flags(root.Anchor) & NodeFlags.Focused) != 0;

            Check("e4popup.8 focus restores at close START (popup still fading) and survives the removal",
                anchorFocused0 && movedIn && restoredAtCloseStart && stillFadingNow && fadingAfterFrame && removed,
                $"pre={anchorFocused0} in={movedIn} atStart={restoredAtCloseStart} fading={stillFadingNow}/{fadingAfterFrame} removed={removed}");
        }

        // e4popup.9 — nested cascade close (WinUI MenuFlyoutSubItem close order): closing the parent closes its
        // children FIRST; each level restores its own saved focus, so the chain unwinds child → parent content →
        // original invoker; both windowed-popup leases release with their entries.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("e4cascade", new Size2(480, 360), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new OverlayProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var svc = root.Service!;
            ClickNode(host, window, root.Anchor);   // pre-open focus = the anchor

            NodeHandle pItem = default, cItem = default;
            OverlayHandle? hChild = null;
            var hParent = svc.Open(() => root.Anchor, () => new BoxEl
            {
                Width = 160, Height = 80, Padding = Edges4.All(8),
                Children =
                [
                    new BoxEl
                    {
                        Width = 120, Height = 24, Role = AutomationRole.MenuItem, OnRealized = n => pItem = n,
                        OnClick = () => hChild = svc.Open(() => pItem, () => new BoxEl
                        {
                            Width = 120, Height = 50,
                            Children = [new BoxEl { Width = 100, Height = 24, Role = AutomationRole.MenuItem, OnClick = () => { }, OnRealized = n2 => cItem = n2 }],
                        }, FlyoutPlacement.RightEdgeAlignedTop, new PopupOptions { ConstrainToRootBounds = false }),
                    },
                ],
            }, FlyoutPlacement.BottomLeft, new PopupOptions { ConstrainToRootBounds = false });
            for (int i = 0; i < 18; i++) host.RunFrame();   // parent open settles

            ClickNode(host, window, pItem);                  // focuses the item + opens the windowed child
            for (int i = 0; i < 18; i++) host.RunFrame();    // child open settles
            bool bothLeased = app.PopupWindows.Count == 2 && host.PopupWindows.Count == 2;

            ClickNode(host, window, cItem);                  // focus moves into the CHILD popup
            bool inChild = (host.Scene.Flags(cItem) & NodeFlags.Focused) != 0;

            hParent.Close();
            bool cascade = !hParent.IsOpen && hChild is { IsOpen: false };
            // Child closed first: its restore put focus on pItem (inside the parent wrapper), so the parent's restore
            // then chained to the original anchor — a parent-first order would strand focus on pItem.
            bool chained = (host.Scene.Flags(root.Anchor) & NodeFlags.Focused) != 0;
            for (int i = 0; i < 20; i++) host.RunFrame();
            bool released = host.PopupWindows.Count == 0
                && app.PopupWindows.Count == 2 && app.PopupWindows[0].Disposed && app.PopupWindows[1].Disposed;
            bool gone = !host.Scene.IsLive(pItem) && !host.Scene.IsLive(cItem);

            Check("e4popup.9 cascade close: parent close closes children first, focus chains back to the invoker, windowed leases release",
                bothLeased && inChild && cascade && chained && released && gone,
                $"leased={bothLeased} inChild={inChild} cascade={cascade} chained={chained} released={released} gone={gone}");
        }
    }

    // 64m — every WinUI transient surface records the ONE default acrylic recipe (PushLayer cmd) in BOTH themes.
    // WinUI ground truth (AcrylicBrush_themeresources.xaml): AcrylicBackgroundFillColorDefaultBrush ==
    // AcrylicInAppFillColorDefaultBrush =
    //   dark  "Default": TintColor #2C2C2C, TintOpacity 0.15, TintLuminosityOpacity 0.96, FallbackColor #2C2C2C
    //   light "Light":   TintColor #FCFCFC, TintOpacity 0.0,  TintLuminosityOpacity 0.85, FallbackColor #F9F9F9
    // Carried by: FlyoutPresenterBackground (FlyoutPresenter_themeresources.xaml:5/15), the MenuFlyout system
    // backdrop AcrylicBackgroundFillColorDefaultBackdrop (MenuFlyout_themeresources.xaml:264+271),
    // ComboBoxDropDownBackground (ComboBox_themeresources.xaml:63/273), AutoSuggestBoxSuggestionsListBackground
    // (AutoSuggestBox_themeresources.xaml:5/17) and ToolTipBackgroundBrush (ToolTip_themeresources.xaml:14/40).
    // The FALLBACK color in the PushLayer cmd is the solid paint when blur is unavailable (AcrylicBrush FallbackColor).
    static void FlyoutAcrylicChecks(StringTable strings)
    {
        try
        {
            foreach (var (kind, label) in new[] { (ThemeKind.Dark, "dark"), (ThemeKind.Light, "light") })
            {
                Tok.Use(kind);
                ColorF tint = kind == ThemeKind.Dark ? ColorF.FromRgba(0x2C, 0x2C, 0x2C) : ColorF.FromRgba(0xFC, 0xFC, 0xFC);
                ColorF fall = kind == ThemeKind.Dark ? ColorF.FromRgba(0x2C, 0x2C, 0x2C) : ColorF.FromRgba(0xF9, 0xF9, 0xF9);
                float tintOp = kind == ThemeKind.Dark ? 0.15f : 0.0f;
                float lumOp = kind == ThemeKind.Dark ? 0.96f : 0.85f;

                using var app = new HeadlessPlatformApp();
                var window = new HeadlessWindow(new WindowDesc("acry-" + label, new Size2(480, 400), 1f)); window.Show();
                var device = new HeadlessGpuDevice();
                var fonts = new HeadlessFontSystem(strings);
                var root = new OverlayProbe();
                using var host = new AppHost(app, window, device, fonts, strings, root);
                host.RunFrame();
                var svc = root.Service!;

                bool SurfaceLayerOk(PopupChrome chrome)
                {
                    var hd = svc.Open(() => root.Anchor,
                        () => new BoxEl { Width = 220f, Height = 80f, Fill = Tok.FillCardDefault },
                        FlyoutPlacement.BottomLeft, new PopupOptions(Chrome: chrome));
                    host.RunFrame();
                    host.RunFrame();
                    bool found = false;
                    foreach (var l in device.LastLayers)
                        if (ColorClose(l.Tint, tint, 0.004f) && ColorClose(l.Fallback, fall, 0.004f)
                            && Near(l.TintOpacity, tintOp, 0.005f) && Near(l.LuminosityOpacity, lumOp, 0.005f)
                            && Near(l.BlurSigma, 30f, 0.5f) && l.NoiseOpacity > 0f)
                            found = true;
                    hd.Close();
                    for (int i = 0; i < 16; i++) host.RunFrame();
                    return found;
                }

                bool menuOk = SurfaceLayerOk(PopupChrome.Flyout);      // MenuFlyoutPresenter (system backdrop recipe)
                bool comboOk = SurfaceLayerOk(PopupChrome.Dropdown);   // ComboBox PopupBorder
                bool suggestOk = SurfaceLayerOk(PopupChrome.Static);   // AutoSuggestBox SuggestionsContainer
                bool flyoutOk = SurfaceLayerOk(PopupChrome.Popup);     // FlyoutPresenter
                // ToolTip + the Slider value tip bind Tok.AcrylicFlyout directly (ToolTip.cs BubbleContent /
                // Slider.cs TipBubble) — assert the token equals the WinUI recipe so those surfaces are covered too.
                var spec = Tok.AcrylicFlyout;
                bool tokenOk = ColorClose(spec.Tint, tint, 0.004f) && ColorClose(spec.Fallback, fall, 0.004f)
                    && Near(spec.TintOpacity, tintOp, 0.005f) && Near(spec.LuminosityOpacity, lumOp, 0.005f);
                Check($"64m. {label} flyout acrylic: PushLayer tint/fallback/tint-opacity/luminosity match the WinUI default acrylic on every transient surface",
                    menuOk && comboOk && suggestOk && flyoutOk && tokenOk,
                    $"menu={menuOk} combo={comboOk} suggest={suggestOk} flyout={flyoutOk} token={tokenOk}");
            }
        }
        finally { Tok.Use(ThemeKind.Dark); }
    }

    // 64n — the in-app acrylic effect runner's portable math (FluentGpu.Render AcrylicBackdropMath; consumed by the
    // D3D12 AcrylicCompositor's PushLayer{Acrylic} schedule: region snapshot → pooled downsampled RT → two-pass
    // separable gaussian → WinUI composite). WinUI ground truth: microsoft-ui-xaml AcrylicBrush.h:64
    // sc_blurRadius = 30.0f applied as GaussianBlurEffect.BlurAmount (= gaussian STANDARD DEVIATION in DIPs,
    // AcrylicBrush.cpp:521-525). The runner reproduces sigma = 30·dpiScale physical px with ONE fixed kernel by
    // choosing the snapshot downsample factor (down · KernelSigma == sigmaPhys). The composited GPU pixels themselves
    // are needs-pixels (manual --shot) — these checks pin every number the leaf composites with.
    static void AcrylicBackdropMathChecks()
    {
        var off = AcrylicBackdropMath.TapOffsets;
        var wgt = AcrylicBackdropMath.TapWeights;
        double mass = wgt[0];                                        // total kernel mass: center + 2·(mirrored taps)
        double variance = 0;
        for (int i = 1; i < wgt.Length; i++) { mass += 2.0 * wgt[i]; variance += 2.0 * wgt[i] * off[i] * off[i]; }
        double sigma = Math.Sqrt(variance);                          // effective std-dev of the folded bilinear kernel
        bool monotone = true;
        for (int i = 2; i < off.Length; i++) if (off[i] <= off[i - 1]) monotone = false;
        Check("64n. acrylic blur kernel: normalized symmetric linear-tap gaussian at the fixed sigma the /down schedule assumes",
            off.Length == AcrylicBackdropMath.TapCount && wgt.Length == AcrylicBackdropMath.TapCount
            && off[0] == 0f && monotone && off[^1] <= AcrylicBackdropMath.KernelRadius
            && Math.Abs(mass - 1.0) < 1e-4 && Math.Abs(sigma - AcrylicBackdropMath.KernelSigma) < 0.25,
            $"taps={off.Length} mass={mass:0.00000} sigma={sigma:0.000} (kernel sigma {AcrylicBackdropMath.KernelSigma})");

        // down(BlurSigma=30 DIP) ⇒ effective full-res sigma = down·KernelSigma = 30·scale physical px, exactly
        // reproducing AcrylicBrush.h:64 sc_blurRadius at the common DPI scales ("downsample /2 or /4" + DPI-aware).
        bool downOk = AcrylicBackdropMath.DownsampleFactor(30f, 1f) == 4       // 4·7.5  = 30  phys px @ 100%
            && AcrylicBackdropMath.DownsampleFactor(30f, 1.5f) == 6            // 6·7.5  = 45  phys px @ 150%
            && AcrylicBackdropMath.DownsampleFactor(30f, 2f) == 8              // 8·7.5  = 60  phys px @ 200%
            && AcrylicBackdropMath.DownsampleFactor(15f, 1f) == 2              // smaller sigma ⇒ /2
            && AcrylicBackdropMath.DownsampleFactor(120f, 2f) == 8;            // clamped at /8
        Check("64n2. acrylic downsample factor: down·KernelSigma reproduces WinUI BlurAmount 30 DIP across DPI scales (clamped /8)",
            downOk,
            $"down@1x={AcrylicBackdropMath.DownsampleFactor(30f, 1f)} @1.5x={AcrylicBackdropMath.DownsampleFactor(30f, 1.5f)} @2x={AcrylicBackdropMath.DownsampleFactor(30f, 2f)}");

        // Snapshot region: the layer rect inflated by the FULL blur support (KernelRadius·down phys px) on every side
        // (so blurred texels under the rect see real backdrop — bit-identical to blurring the whole backdrop inside
        // the rect), clamped to the canvas at window edges.
        int pad = AcrylicBackdropMath.KernelRadius * 4;   // 88 px at /4
        AcrylicBackdropMath.SnapshotRegion(new RectF(200f, 160f, 200f, 120f), 1f, 4, 1920, 1080, out int x, out int y, out int w, out int h);
        bool interiorOk = x == 200 - pad && y == 160 - pad && w == 200 + 2 * pad && h == 120 + 2 * pad;
        AcrylicBackdropMath.SnapshotRegion(new RectF(2f, 2f, 60f, 40f), 1f, 4, 480, 400, out int cx, out int cy, out int cw, out int ch);
        bool clampOk = cx == 0 && cy == 0 && cw == 2 + 60 + pad && ch == 2 + 40 + pad;   // left/top clamped at the canvas edge
        int pad8 = AcrylicBackdropMath.KernelRadius * 8;  // 176 px at /8 (200% DPI)
        AcrylicBackdropMath.SnapshotRegion(new RectF(200f, 160f, 100f, 60f), 2f, 8, 4000, 4000, out int sx, out int sy, out int sw, out int sh);
        bool scaleOk = sx == 400 - pad8 && sy == 320 - pad8 && sw == 200 + 2 * pad8 && sh == 120 + 2 * pad8;   // DIP→phys at 200% DPI
        Check("64n3. acrylic snapshot region: rect inflated by the full kernel support and clamped to the canvas (phys px, DPI-aware)",
            interiorOk && clampOk && scaleOk, $"interior={interiorOk} clamp={clampOk} scale={scaleOk}");

        // LayerPool size buckets (gpu-renderer.md §7.1 quantized pow2 buckets, floor 64): monotone, covering, few
        // distinct sizes ⇒ a steady-state frame re-acquires the same bucket and never creates a texture.
        bool bucketOk = AcrylicBackdropMath.BucketDim(1) == 64 && AcrylicBackdropMath.BucketDim(64) == 64
            && AcrylicBackdropMath.BucketDim(65) == 128 && AcrylicBackdropMath.BucketDim(240) == 256
            && AcrylicBackdropMath.BucketDim(960) == 1024;
        Check("64n4. acrylic LayerPool buckets: next-pow2 (floor 64) so pooled RTs reuse across layers and frames",
            bucketOk,
            $"b(1)={AcrylicBackdropMath.BucketDim(1)} b(65)={AcrylicBackdropMath.BucketDim(65)} b(960)={AcrylicBackdropMath.BucketDim(960)}");
    }

    static void ContentDialogChromeChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("dialogchrome", new Size2(760, 520), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new ContentDialogProbe());

        for (int i = 0; i < 24; i++) host.RunFrame();   // open + settle the modal scale/fade

        var title = FindTextNode(host.Scene, strings, host.Scene.Root, "Save your work?");
        NodeHandle outer = title;
        while (!outer.IsNull)
        {
            ref var p = ref host.Scene.Paint(outer);
            if (Near(p.BorderWidth, 1f, 0.01f) && ColorClose(p.BorderColor, Tok.StrokeSurfaceDefault, 0.02f))
                break;
            outer = host.Scene.Parent(outer);
        }

        var top = outer.IsNull ? NodeHandle.Null : Child(host.Scene, outer, 0);
        var sep = outer.IsNull ? NodeHandle.Null : Child(host.Scene, outer, 1);
        var command = outer.IsNull ? NodeHandle.Null : Child(host.Scene, outer, 2);

        bool realOuterBorder = !outer.IsNull
            && ColorClose(host.Scene.Paint(outer).Fill, Tok.FillSolidBase, 0.02f)
            && ColorClose(host.Scene.Paint(outer).BorderColor, Tok.StrokeSurfaceDefault, 0.02f)
            && Near(host.Scene.Paint(outer).BorderWidth, 1f, 0.01f)
            && Near(host.Scene.Paint(outer).Corners.TopLeft, Radii.Overlay, 0.01f)
            && (host.Scene.Flags(outer) & NodeFlags.ClipsToBounds) != 0;
        bool topOverlay = !top.IsNull && ColorClose(host.Scene.Paint(top).Fill, Tok.FillLayerAlt, 0.02f)
            && Near(host.Scene.Layout(top).Padding.Left, 24f, 0.01f)
            && Near(host.Scene.Paint(top).Corners.TopLeft, 0f, 0.01f)
            && Near(host.Scene.Paint(top).Corners.BottomLeft, 0f, 0.01f);
        bool separator = !sep.IsNull && Near(host.Scene.Bounds(sep).H, 1f, 0.01f)
            && ColorClose(host.Scene.Paint(sep).Fill, Tok.StrokeCardDefault, 0.02f);
        bool commandRow = !command.IsNull && ColorClose(host.Scene.Paint(command).Fill, Tok.FillSolidBase, 0.02f)
            && Near(host.Scene.Layout(command).Padding.Left, 24f, 0.01f)
            && Near(host.Scene.Layout(command).Gap, 8f, 0.01f)
            && Near(host.Scene.Paint(command).Corners.TopLeft, 0f, 0.01f)
            && Near(host.Scene.Paint(command).Corners.BottomLeft, 0f, 0.01f);

        Check("64g. ContentDialog chrome: real outer border; square internal content/command layers",
            realOuterBorder && topOverlay && separator && commandRow,
            $"outer={realOuterBorder} top={topOverlay} sep={separator} cmd={commandRow}");
    }

    static void TeachingTipPlacementChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("tipplace", new Size2(760, 520), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new TeachingTipProbe());

        for (int i = 0; i < 16; i++) host.RunFrame();   // mount, place, publish placement signal, settle the beak component

        var triggerText = FindTextNode(host.Scene, strings, host.Scene.Root, "Show teaching tip");
        NodeHandle trigger = triggerText;
        while (!trigger.IsNull && host.Scene.Interaction(trigger).Role != AutomationRole.Button)
            trigger = host.Scene.Parent(trigger);
        var beakStroke = FindPolylineStrokeNode(host.Scene, host.Scene.Root);
        var ar = trigger.IsNull ? default : host.Scene.AbsoluteRect(trigger);
        var br = beakStroke.IsNull ? default : host.Scene.AbsoluteRect(beakStroke);
        float anchorCx = ar.X + ar.W * 0.5f;
        float beakCx = br.X + br.W * 0.5f;
        bool aligned = !trigger.IsNull && !beakStroke.IsNull && Near(beakCx, anchorCx, 1.5f);

        // TEMP-DIAG
        for (var n = beakStroke; !n.IsNull; n = host.Scene.Parent(n))
        {
            var bb = host.Scene.Bounds(n);
            var pp = host.Scene.Paint(n);
            Console.Error.WriteLine($"[64h] node={n.Raw.Index} bounds=({bb.X:0.0},{bb.Y:0.0} {bb.W:0.0}x{bb.H:0.0}) tdx={pp.LocalTransform.Dx:0.0} tdy={pp.LocalTransform.Dy:0.0}");
        }
        Console.Error.WriteLine($"[64h] anchor=({ar.X:0.0},{ar.Y:0.0} {ar.W:0.0}x{ar.H:0.0})");

        Check("64h. TeachingTip tail aligns to resolved target center after popup placement",
            aligned, $"anchorCx={anchorCx:0.0} beakCx={beakCx:0.0} dx={beakCx - anchorCx:0.0}");
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
        while (!surface.IsNull && !host.Scene.TryGetAcrylic(surface, out _) && !ChildHasAcrylic(host.Scene, surface)) surface = host.Scene.Parent(surface);
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

        using var app2 = new HeadlessPlatformApp();
        var window2 = new HeadlessWindow(new WindowDesc("splitlong", new Size2(360, 220), 1f));
        window2.Show();
        var device2 = new HeadlessGpuDevice();
        var fonts2 = new HeadlessFontSystem(strings);
        using var host2 = new AppHost(app2, window2, device2, fonts2, strings, new SplitButtonLongMenuProbe());
        host2.RunFrame();
        var splitButtons = Roles(host2.Scene, AutomationRole.Button);
        var secondary = splitButtons.Count > 1 ? splitButtons[1] : NodeHandle.Null;
        if (!secondary.IsNull) ClickNode(host2, window2, secondary);
        host2.RunFrame();
        var specialText = FindTextNode(host2.Scene, strings, host2.Scene.Root, "Paste special");
        NodeHandle surface = specialText;
        while (!surface.IsNull && !host2.Scene.TryGetAcrylic(surface, out _) && !ChildHasAcrylic(host2.Scene, surface)) surface = host2.Scene.Parent(surface);
        var sr = surface.IsNull ? default : host2.Scene.AbsoluteRect(surface);
        var tr = specialText.IsNull ? default : host2.Scene.AbsoluteRect(specialText);
        bool longLabelFits = !surface.IsNull && !specialText.IsNull && tr.Right <= sr.Right - 10f && sr.W > 150f;
        Check("64c2. SplitButton menu: long item text fits inside the flyout surface",
            longLabelFits, $"surfaceW={sr.W:0} textRight={tr.Right:0} surfaceRight={sr.Right:0}");
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

        bool HasAccentPillBeside(NodeHandle row)
        {
            if (row.IsNull) return false;
            var rr = host.Scene.AbsoluteRect(row);
            bool found = false;
            void Visit(NodeHandle n)
            {
                if (n.IsNull || found) return;
                ref var p = ref host.Scene.Paint(n);
                var r = host.Scene.AbsoluteRect(n);
                if (ColorClose(p.Fill, Tok.AccentDefault, 0.02f)
                    && Near(r.W, 3f, 0.75f)
                    && Near(r.H, 16f, 0.75f)
                    && MathF.Abs((r.Y + r.H * 0.5f) - (rr.Y + rr.H * 0.5f)) < 4f
                    && r.X >= rr.X
                    && r.X <= rr.X + 14f)
                {
                    found = true;
                    return;
                }

                for (var c = host.Scene.FirstChild(n); !c.IsNull; c = host.Scene.NextSibling(c))
                    Visit(c);
            }

            Visit(host.Scene.Root);
            return found;
        }

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
            for (int i = 0; i < 4; i++) host.RunFrame();   // let the child rows clear their staggered enter delay
            ClickNode(host, window, items[2]);
            childSelected = HasGlyph(device, strings, "PAGE:c1");
        }

        // Collapse the expanded pane to the icon rail while a child is selected. WinUI keeps the hierarchical child
        // selection in the model, but the closed compact rail shows only top-level containers and paints the selected
        // child indication on the visible parent chain.
        bool compactRailRootOnly = false;
        bool compactRailParentChrome = false;
        bool compactRailKeepsChildPage = false;
        bool reopenedStillExpanded = false;
        var buttons = Roles(host.Scene, AutomationRole.Button);
        if (childSelected && buttons.Count > 0)
        {
            ClickNode(host, window, buttons[0]);
            for (int i = 0; i < 24; i++) host.RunFrame();

            items.Clear();
            CollectRole(host.Scene, host.Scene.Root, AutomationRole.NavigationItem, items);
            compactRailRootOnly = items.Count == collapsedCount;
            compactRailKeepsChildPage = HasGlyph(device, strings, "PAGE:c1");
            if (items.Count > 1)
            {
                compactRailParentChrome = ColorClose(host.Scene.Paint(items[1]).Fill, Tok.FillSubtleSecondary, 0.02f)
                    && HasAccentPillBeside(items[1]);
            }

            buttons = Roles(host.Scene, AutomationRole.Button);
            if (buttons.Count > 0)
            {
                ClickNode(host, window, buttons[0]);
                for (int i = 0; i < 4; i++) host.RunFrame();
                items.Clear();
                CollectRole(host.Scene, host.Scene.Root, AutomationRole.NavigationItem, items);
                reopenedStillExpanded = items.Count == expandedCount;
            }
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
        var afterText = FindTextNode(host.Scene, strings, host.Scene.Root, "After");
        var afterLabel = afterText.IsNull ? NodeHandle.Null : host.Scene.Parent(afterText);
        var afterRow = afterLabel.IsNull ? NodeHandle.Null : host.Scene.Parent(afterLabel);
        float afterLabelDy = afterLabel.IsNull ? 0f : host.Scene.Paint(afterLabel).LocalTransform.Dy;
        bool labelNotProjected = !afterLabel.IsNull && MathF.Abs(afterLabelDy) < 0.01f;
        bool rowOwnsMotion = !afterRow.IsNull && host.Animation.HasTracks(afterRow);

        Check("65. NavigationView: group expands/collapses + child selection updates content",
            collapsedCount == 3 && childrenAppeared && childSelected && collapsedAgain,
            $"collapsed={collapsedCount} expanded={expandedCount} childPage={childSelected} recollapsed={collapsedAgain}");
        Check("65a. NavigationView: hierarchy reflow motion is owned by the whole row, not the label",
            collapsedAgain && rowOwnsMotion && labelNotProjected,
            $"rowTracks={rowOwnsMotion} labelDy={afterLabelDy:0.###}");
        Check("65a2. NavigationView: closed icon rail hides child rows and maps child selection chrome to parent",
            compactRailRootOnly && compactRailParentChrome && compactRailKeepsChildPage && reopenedStillExpanded,
            $"rootOnly={compactRailRootOnly} parentChrome={compactRailParentChrome} childPage={compactRailKeepsChildPage} reopenExpanded={reopenedStillExpanded}");
    }

    static void PipsPagerOutputChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("pipsout", new Size2(320, 160), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new PipsPagerOutputProbe());
        host.RunFrame();

        var pips = Roles(host.Scene, AutomationRole.Pager);
        bool initial = HasGlyph(device, strings, "Page 1 / 5");
        if (pips.Count > 2) ClickNode(host, window, pips[2]);   // index 1
        bool odd = HasGlyph(device, strings, "Page 2 / 5");
        pips = Roles(host.Scene, AutomationRole.Pager);
        if (pips.Count > 3) ClickNode(host, window, pips[3]);   // index 2, the reported blank-output path
        bool even = HasGlyph(device, strings, "Page 3 / 5");

        Check("65b. PipsPager output TextBind survives owner re-render for even selected indices",
            pips.Count >= 6 && initial && odd && even,
            $"pips={pips.Count} initial={initial} odd={odd} even={even}");
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

        // 66b — the LIVE checkmark DRAW-ON through AppHost (the EXACT click path the gallery uses). Toggling
        // unchecked→checked must leave the checkmark mid-DRAW on the click frame: its clip box's presented width is
        // sweeping 0→full (the stroke drawing itself, WinUI-style), then settles (reveal resets to NaN). If nothing is
        // revealing on that frame, the draw-on isn't running — the "animation not showing" report.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cbanim", new Size2(320, 160), 1f)); window.Show();
            var root = new CheckBoxProbe();
            var clock = new ManualFrameTimeSource();
            using var host = new AppHost(app, window, device, fonts, strings, root, frameTime: clock);
            host.RunFrame();   // mount unchecked
            ClickNode(host, window, FindRole(host.Scene, host.Scene.Root, AutomationRole.CheckBox));   // toggle → checked + 1 frame
            var box = Child(host.Scene, FindRole(host.Scene, host.Scene.Root, AutomationRole.CheckBox), 0);
            float t0 = ActiveStrokeTrimEnd(host.Scene, box);
            clock.Advance(80f);
            host.RunFrame();
            float t1 = ActiveStrokeTrimEnd(host.Scene, box);
            bool drewPolyline = device.LastPolylines.Count > 0 && device.LastPolylines[0].TrimEnd > 0f && device.LastPolylines[0].TrimEnd < 1f;
            bool drawing = Near(t0, 0f, 0.001f) && t1 > 0.01f && t1 < 1f && host.Animation.HasActive && drewPolyline;
            clock.Advance(400f);
            host.RunFrame();
            bool settled = float.IsNaN(ActiveStrokeTrimEnd(host.Scene, box)) && !host.Animation.HasActive;
            Check("66b. LIVE: toggling a CheckBox DRAWS the checkmark in (stroke-trim polyline, AppHost click path)",
                drawing && settled, $"trim {t0:0.00}->{t1:0.00} poly={device.LastPolylines.Count} settled={settled}");
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
            // WinUI percentage model: rating = ceil(x / actualRatingWidth * Max), actualRatingWidth = Max*16 + (Max-1)*8 = 112.
            var p3 = new Point2(rr.X + 56f, rr.Y + rr.H / 2f);   // x=56 -> 56/112*5=2.5 -> ceil=3 (3rd star)
            window.QueueInput(new InputEvent(InputKind.PointerDown, p3, 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerUp, p3, 0, 0));
            host.RunFrame();
            float v3 = root.Val!.Peek();
            var p5 = new Point2(rr.X + 110f, rr.Y + rr.H / 2f); // x=110 -> 110/112*5=4.91 -> ceil=5 (sweep to 5th)
            window.QueueInput(new InputEvent(InputKind.PointerDown, p3, 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerMove, p5, 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerUp, p5, 0, 0));
            host.RunFrame();
            float v5 = root.Val!.Peek();
            Check("69. RatingControl: click sets value, drag sweeps", Near(v3, 3f) && Near(v5, 5f), $"click={v3} drag={v5}");

            // 69b. Keyboard (Left/Right/Home/End) + IsClearEnabled clear-on-reclick. The prior click focused the row,
            // so arrow keys bubble to the control's OnKeyDown. Value starts at 5 (from the sweep above).
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Left));   // 5 -> 4
            host.RunFrame(); float kLeft = root.Val!.Peek();
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Home));   // -> clear (-1)
            host.RunFrame(); float kHome = root.Val!.Peek();
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Right));  // unset + Right -> InitialSetValue (1)
            host.RunFrame(); float kRight = root.Val!.Peek();
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.End));    // -> MaxRating (5)
            host.RunFrame(); float kEnd = root.Val!.Peek();
            // Re-click the current value (5) with IsClearEnabled (default true) -> clears to -1.
            window.QueueInput(new InputEvent(InputKind.PointerDown, p5, 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerUp, p5, 0, 0));
            host.RunFrame(); float reClear = root.Val!.Peek();
            Check("69b. RatingControl: keyboard range + clear-on-reclick",
                Near(kLeft, 4f) && Near(kHome, -1f) && Near(kRight, 1f) && Near(kEnd, 5f) && Near(reClear, -1f),
                $"L={kLeft} Home={kHome} R={kRight} End={kEnd} reclick={reClear}");
        }

        // 69c. RatingControl read-only: pointer + keyboard are inert (fixed rating).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("rt-ro", new Size2(320, 120), 1f)); window.Show();
            var root = new RatingProbe { ReadOnly = true, Initial = 3f };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var rating = FindRole(host.Scene, host.Scene.Root, AutomationRole.Rating);
            var rr = host.Scene.AbsoluteRect(rating);
            var pp = new Point2(rr.X + 110f, rr.Y + rr.H / 2f);
            window.QueueInput(new InputEvent(InputKind.PointerDown, pp, 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerUp, pp, 0, 0));
            host.RunFrame();
            Check("69c. RatingControl read-only: input is inert", Near(root.Val!.Peek(), 3f), $"val={root.Val!.Peek()}");
        }

        // 69d. RatingControl bare-hover PREVIEW: a pointer MOVE with NO button down fills the stars to the cursor
        // (WinUI OnPointerMovedOverBackgroundStackPanel) -- the foreground clip layer widens to the hovered rating.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("rt-hov", new Size2(320, 120), 1f)); window.Show();
            var root = new RatingProbe { Initial = RatingControl.NoValueSet };   // unset -> resting foreground clipped to 0
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var rating = FindRole(host.Scene, host.Scene.Root, AutomationRole.Rating);
            var rr = host.Scene.AbsoluteRect(rating);
            const string filledStar = "";   // RatingControl filled glyph (E735); each FULL star renders exactly one
            int restFilled = CountGlyph(device, strings, filledStar);   // unset -> 0 filled (single-glyph rows, no overlay halo)
            // BARE hover (no PointerDown): x=56 -> ceil(56/112*5)=3 stars filled.
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(rr.X + 56f, rr.Y + rr.H / 2f), 0, 0));
            host.RunFrame();
            int hovFilled = CountGlyph(device, strings, filledStar);
            bool committedNothing = root.Val!.Peek() <= RatingControl.NoValueSet;   // preview only — not committed
            // Pointer EXIT (move far off the strip): the preview drops and the stars revert to the committed rating (0 filled).
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(rr.Right + 240f, rr.Bottom + 240f), 0, 0));
            host.RunFrame();
            int exitFilled = CountGlyph(device, strings, filledStar);
            bool coerce = Near(RatingControl.Coerce(0.5f, 5), 1f) && Near(RatingControl.Coerce(-3f, 5), -1f)
                && Near(RatingControl.Coerce(0f, 5), 1f) && Near(RatingControl.Coerce(3.4f, 5), 3.4f) && Near(RatingControl.Coerce(9f, 5), 5f);
            Check("69d. RatingControl: bare-hover fills 3 (single-glyph, no overlay), reverts on pointer-exit, no commit",
                restFilled == 0 && hovFilled == 3 && committedNothing && exitFilled == 0 && coerce,
                $"rest={restFilled} hov={hovFilled} exit={exitFilled} committed={!committedNothing} coerce={coerce}");
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

        // AutoSuggestBox -- open popup width matches the owning field (WinUI popup/list is field-width, not content-width).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("asb", new Size2(420, 320), 1f)); window.Show();
            var root = new AutoSuggestProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();   // mount + post-commit open effect
            host.RunFrame();   // overlay content
            var field = FindRole(host.Scene, host.Scene.Root, AutomationRole.ComboBox);
            var rows = Roles(host.Scene, AutomationRole.MenuItem);
            float fieldW = host.Scene.AbsoluteRect(field).W;
            float rowW = rows.Count > 0 ? host.Scene.AbsoluteRect(rows[0]).W : 0f;
            Check("70a. AutoSuggestBox: suggestions popup width matches the field", rows.Count == 5 && rowW >= fieldW - 16f,
                $"rows={rows.Count} fieldW={fieldW:0.#} rowW={rowW:0.#}");
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

    // ── w1controls — Wave-1 control parity (Button family, ToggleSwitch, RadioButtons, Slider, RatingControl).
    //    Every value asserted against microsoft-ui-xaml (citations inline); colors compared on FULL ARGB. ─────────────
    static void W1ControlsChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);

        // Recorder geometry: rect/stroke commands carry the NODE-LOCAL rect with the absolute placement on Transform.
        static FillRoundRectCmd FillAt(HeadlessGpuDevice dev, RectF abs)
        {
            foreach (var r in dev.LastRects)
                if (Near(r.Transform.Dx, abs.X, 0.6f) && Near(r.Transform.Dy, abs.Y, 0.6f)
                    && Near(r.Rect.W, abs.W, 0.6f) && Near(r.Rect.H, abs.H, 0.6f))
                    return r;
            return default;
        }
        static DrawRoundRectStrokeCmd StrokeOfWidth(HeadlessGpuDevice dev, float strokeW, float rectW)
        {
            foreach (var s in dev.LastStrokes)
                if (Near(s.StrokeWidth, strokeW, 0.01f) && Near(s.Rect.W, rectW, 0.6f)) return s;
            return default;
        }

        // w1controls.1 — Button: color-only states (NO scale — WinUI Button state storyboards swap brushes only,
        // Button_themeresources.xaml:176-229), content centred (Control defaults HorizontalContentAlignment /
        // VerticalContentAlignment = Center, DependencyProperty.cpp:646-652), resting fill = ButtonBackground =
        // ControlFillColorDefault (Button_themeresources.xaml:30/128), FocusVisualMargin −3 (:167).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w1btn", new Size2(360, 200), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var root = new W1ButtonProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();

            var btn = FindRole(host.Scene, host.Scene.Root, AutomationRole.Button);
            var br = host.Scene.AbsoluteRect(btn);
            var fill = FillAt(device, br).Fill;
            bool restFill = ColorClose(fill, Tok.FillControlDefault, 0.004f);

            var label = FindTextNode(host.Scene, strings, host.Scene.Root, "w1-btn");
            var lr = host.Scene.AbsoluteRect(label);
            bool centred = Near(lr.X + lr.W / 2f, br.X + br.W / 2f, 1f) && Near(lr.Y + lr.H / 2f, br.Y + br.H / 2f, 1f);

            bool noScaleWired = !host.Scene.TryGetInteract(btn, out var ia)
                || (Near(ia.HoverScale, 1f, 0.001f) && Near(ia.PressScale, 1f, 0.001f));
            var c = CenterOf(host.Scene, btn);
            window.QueueInput(new InputEvent(InputKind.PointerDown, c, 0, 0));
            bool pressIdentity = true;
            for (int i = 0; i < 6; i++)
            {
                host.RunFrame();
                var t = host.Scene.Paint(btn).LocalTransform;
                pressIdentity &= Near(t.M11, 1f, 0.001f) && Near(t.M22, 1f, 0.001f) && Near(t.Dx, 0f, 0.001f) && Near(t.Dy, 0f, 0.001f);
            }
            window.QueueInput(new InputEvent(InputKind.PointerUp, c, 0, 0));
            host.RunFrame();

            Check("w1controls.1 Button: NO scale on press (color-only states), content centres both axes, resting fill = ControlFillColorDefault (ARGB)",
                restFill && centred && noScaleWired && pressIdentity && root.Clicks == 1,
                $"fillA={fill.A:0.###} centred={centred} noScale={noScaleWired} identity={pressIdentity} clicks={root.Clicks}");

            // The −3 focus rect inset by the 2px primary's 1px centerline → local (−2,−2,W+4,H+4); 1px secondary at −0.5.
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Tab));
            host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Tab));
            host.RunFrame();
            DrawRoundRectStrokeCmd prim = default, sec = default;
            foreach (var s in device.LastStrokes)
            {
                if (Near(s.StrokeWidth, 2f)) prim = s;
                else if (Near(s.StrokeWidth, 1f)) sec = s;
            }
            bool primGeom = Near(prim.Rect.X, -2f) && Near(prim.Rect.Y, -2f) && Near(prim.Rect.W, br.W + 4f) && Near(prim.Rect.H, br.H + 4f);
            bool secGeom = Near(sec.Rect.X, -0.5f) && Near(sec.Rect.Y, -0.5f) && Near(sec.Rect.W, br.W + 1f) && Near(sec.Rect.H, br.H + 1f);
            Check("w1controls.1b Button keyboard focus ring honours FocusVisualMargin −3 (primary −2,−2,+4,+4; secondary −0.5)",
                primGeom && secGeom,
                $"prim=({prim.Rect.X:0.#},{prim.Rect.Y:0.#} {prim.Rect.W:0.#}x{prim.Rect.H:0.#}) sec=({sec.Rect.X:0.#},{sec.Rect.Y:0.#} {sec.Rect.W:0.#}x{sec.Rect.H:0.#})");
        }

        // w1controls.2 — RepeatButton cadence-exact: Delay = 500ms, Interval = 33ms — the WinUI DP metadata defaults
        // (dxaml\xcp\components\DependencyObject\DependencyProperty.cpp:714-720), sampled on a manual clock.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w1rpt", new Size2(320, 200), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var root = new RepeatProbe();
            var clock = new ManualFrameTimeSource();
            using var host = new AppHost(app, window, device, fonts, strings, root, frameTime: clock);
            host.RunFrame();
            var btn = FindRole(host.Scene, host.Scene.Root, AutomationRole.Button);
            var c = CenterOf(host.Scene, btn);

            window.QueueInput(new InputEvent(InputKind.PointerDown, c, 0, 0));
            host.RunFrame();                                  // arm → fires once immediately
            int onPress = root.Clicks;                        // 1
            clock.Advance(499f); host.RunFrame();
            int at499 = root.Clicks;                          // still 1 (inside the 500ms initial delay)
            clock.Advance(1f); host.RunFrame();
            int at500 = root.Clicks;                          // 2 — fired exactly at the 500ms boundary
            clock.Advance(32f); host.RunFrame();
            int at532 = root.Clicks;                          // still 2 (inside the 33ms interval)
            clock.Advance(1f); host.RunFrame();
            int at533 = root.Clicks;                          // 3 — fired exactly at the 33ms boundary
            clock.Advance(66f); host.RunFrame();
            int at599 = root.Clicks;                          // 5 — a slow frame fires once per elapsed interval
            window.QueueInput(new InputEvent(InputKind.PointerUp, c, 0, 0));
            host.RunFrame();
            clock.Advance(1000f); host.RunFrame();
            int afterRelease = root.Clicks;

            Check("w1controls.2 RepeatButton cadence-exact: fire on press, again at exactly 500ms, then every 33ms (catch-up on a slow frame); release stops",
                onPress == 1 && at499 == 1 && at500 == 2 && at532 == 2 && at533 == 3 && at599 == 5 && afterRelease == 5,
                $"press={onPress} 499={at499} 500={at500} 532={at532} 533={at533} 599={at599} rel={afterRelease}");
        }

        // w1controls.3 — HyperlinkButton: the ONE WinUI control with a hand cursor (SetCursor(MouseCursorHand) at
        // initialize, HyperLinkButton_Partial.cpp:28-34); Click raises FIRST, then the NavigateUri launches through the
        // IPlatformApp.OpenUri PAL seam (Click → Launcher::TryInvokeLauncher, HyperLinkButton_Partial.cpp:149-177 —
        // headless records into OpenedUris instead of launching).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w1link", new Size2(320, 160), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var root = new W1HyperlinkProbe { App = app };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();

            var link = FindRole(host.Scene, host.Scene.Root, AutomationRole.Hyperlink);
            var c = CenterOf(host.Scene, link);
            window.QueueInput(new InputEvent(InputKind.PointerMove, c, 0, 0));
            host.RunFrame();
            bool hand = window.LastCursor == CursorId.Hand;
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(5f, 5f), 0, 0));
            host.RunFrame();
            bool arrowOff = window.LastCursor == CursorId.Arrow;

            ClickNode(host, window, link);
            bool launched = app.OpenedUris.Count == 1 && app.OpenedUris[0] == "https://wavee.app/w1";
            bool clickFirst = root.UrisAtClick == 0;

            Check("w1controls.3 HyperlinkButton: hand cursor on hover (arrow off-control); Click→OpenUri records the NavigateUri in WinUI order (Click first)",
                hand && arrowOff && launched && clickFirst,
                $"hand={hand} arrow={arrowOff} uris=[{string.Join(",", app.OpenedUris)}] urisAtClick={root.UrisAtClick}");
        }

        // w1controls.4 — ToggleButton checked flip: the fill cross-fades over the 83ms ContentPresenter.BackgroundTransition
        // (ToggleButton_themeresources.xaml:199-201) while the FOREGROUND flips discretely (KeyTime-0 storyboards, :202-357).
        // Sampled cadence-exact on a manual clock: T=0 old brush, T=0.5 mid, T=1.0 (83ms) exactly AccentFillColorDefault.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w1tb", new Size2(320, 160), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var root = new W1ToggleButtonProbe();
            var clock = new ManualFrameTimeSource();
            using var host = new AppHost(app, window, device, fonts, strings, root, frameTime: clock);
            host.RunFrame();

            var tb = FindRole(host.Scene, host.Scene.Root, AutomationRole.ToggleButton);
            var tr = host.Scene.AbsoluteRect(tb);
            var off = FillAt(device, tr).Fill;
            var fgOff = GlyphColor(device, strings, "w1-tb");
            bool offState = ColorClose(off, Tok.FillControlDefault, 0.004f) && ColorClose(fgOff, Tok.TextPrimary, 0.004f);

            root.On!.Value = true;
            host.RunFrame();                                     // commit frame, dt 0 → T=0: fill still the unchecked brush
            var atFlip = FillAt(device, tr).Fill;
            var fgFlip = GlyphColor(device, strings, "w1-tb");
            bool t0 = ColorClose(atFlip, Tok.FillControlDefault, 0.004f) && ColorClose(fgFlip, Tok.TextOnAccentPrimary, 0.004f);

            clock.Advance(41.5f); host.RunFrame();               // T = 0.5: mid cross-fade (neither endpoint)
            var mid = FillAt(device, tr).Fill;
            bool midFade = mid.A > Tok.FillControlDefault.A + 0.15f && mid.A < Tok.AccentDefault.A - 0.15f;

            clock.Advance(41.5f); host.RunFrame();               // T = 1.0 at exactly 83ms: settled, anim row dropped
            var done = FillAt(device, tr).Fill;
            bool settled = ColorClose(done, Tok.AccentDefault, 0.004f) && !host.Scene.HasBrushAnims;

            ClickNode(host, window, tb);                         // the pointer path toggles back
            bool clicked = !root.On!.Peek();

            Check("w1controls.4 ToggleButton checked flip: 83ms BrushTransition on the fill (old at T0 → mid → exact accent at 83ms); foreground steps discretely; click toggles",
                offState && t0 && midFade && settled && clicked,
                $"off={offState} t0={t0} midA={mid.A:0.00} settled={settled} clicked={clicked}");
        }

        // w1controls.5 — ToggleSwitch geometry + brush ladder (ToggleSwitch_themeresources.xaml, "the template"):
        // 40×20 track (:507), 20×20 knob host (:509), knob 12 rest (:510/515 + Normal :231-242) / 14 hover (:268-279) /
        // 17×14 pressed pinned 3px off the near edge (:311-322 + :284-287); tap toggles and the knob travels +20 (:445).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w1ts", new Size2(320, 160), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var root = new ToggleSwitchProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();

            var control = FindRole(host.Scene, host.Scene.Root, AutomationRole.ToggleSwitch);
            var track = Child(host.Scene, control, 0);
            var knobHost = Child(host.Scene, track, 1);
            var knob = Child(host.Scene, knobHost, 0);
            var trk = host.Scene.AbsoluteRect(track);
            var kr = host.Scene.AbsoluteRect(knob);
            var khr = host.Scene.AbsoluteRect(knobHost);
            bool geom = Near(trk.W, 40f) && Near(trk.H, 20f) && Near(khr.W, 20f) && Near(khr.H, 20f)
                && Near(kr.W, 12f) && Near(kr.H, 12f) && Near(kr.X - khr.X, 4f) && Near(khr.X - trk.X, 0f);

            // Off ARGB: fill = ControlAltFillColorSecondary (template:15/135), stroke = ControlStrongStrokeColorDefault
            // (:19/139), knob = TextFillColorSecondary (:31-33/151-153). Stroke cmds carry the CENTERLINE rect: the 1px
            // border of the 40×20 track records as (0.5,0.5,39,19).
            var trackFill = FillAt(device, trk).Fill;
            var knobFill = FillAt(device, kr).Fill;
            var trackStroke = StrokeOfWidth(device, 1f, 39f);
            bool offColors = ColorClose(trackFill, Tok.FillControlAltSecondary, 0.004f)
                && ColorClose(knobFill, Tok.TextSecondary, 0.004f)
                && ColorClose(trackStroke.Color, Tok.StrokeControlStrongDefault, 0.004f);

            var c = CenterOf(host.Scene, control);
            window.QueueInput(new InputEvent(InputKind.PointerMove, c, 0, 0));
            host.RunFrame();
            var kHover = host.Scene.AbsoluteRect(knob);
            bool hover14 = Near(kHover.W, 14f) && Near(kHover.H, 14f);

            // The knob's size/anchor change rides its FLIP transition and AbsoluteRect includes the presented
            // transform — hold the press a few frames so the 83ms grow/pin settles before sampling the geometry.
            window.QueueInput(new InputEvent(InputKind.PointerDown, c, 0, 0));
            for (int i = 0; i < 12; i++) host.RunFrame();
            var kPress = host.Scene.AbsoluteRect(knob);
            var khPress = host.Scene.AbsoluteRect(knobHost);
            bool press17 = Near(kPress.W, 17f) && Near(kPress.H, 14f) && Near(kPress.X - khPress.X, 3f);

            window.QueueInput(new InputEvent(InputKind.PointerUp, c, 0, 0));
            host.RunFrame();
            bool toggled = root.On;
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(310f, 150f), 0, 0));
            for (int i = 0; i < 30; i++) host.RunFrame();   // settle: 167ms travel + 83ms brush fades + hover decay

            var kOn = host.Scene.AbsoluteRect(knob);
            bool traveled = Near(kOn.W, 12f) && Near(kOn.H, 12f) && Near(kOn.X - trk.X, 24f);   // +20 travel, re-centred at 4
            // On ARGB: track = AccentFillColorDefault (template:23/143), knob = TextOnAccentFillColorPrimary (:35-37/155-157);
            // ToggleSwitchOnStrokeThickness = 0 (template:5/125) — the 40-wide stroke disappears.
            var onTrack = FillAt(device, host.Scene.AbsoluteRect(track)).Fill;
            var onKnob = FillAt(device, kOn).Fill;
            bool onColors = ColorClose(onTrack, Tok.AccentDefault, 0.004f) && ColorClose(onKnob, Tok.TextOnAccentPrimary, 0.004f);
            bool strokeGone = StrokeOfWidth(device, 1f, 39f).StrokeWidth == 0f;

            Check("w1controls.5 ToggleSwitch geometry + ARGB: 40×20 track, knob 12 rest / 14 hover / 17×14 pressed (3px pin), tap travels +20; off/on brush ladder exact",
                geom && offColors && hover14 && press17 && toggled && traveled && onColors && strokeGone,
                $"geom={geom} off={offColors} hover={kHover.W:0.#}x{kHover.H:0.#} press={kPress.W:0.#}x{kPress.H:0.#}@+{kPress.X - khPress.X:0.#} on={onColors} strokeGone={strokeGone} knobX=+{kOn.X - trk.X:0.#}");
        }

        // w1controls.6 — ToggleSwitch keyboard: Space activates on KEY-UP (engine focused-clickable contract;
        // HandlesKey = Space/GamepadA, ToggleSwitch_Partial.cpp:1002-1007), the knob travel TWEENS over the 167ms
        // ControlFast reposition (template:418-439 → Motion.ControlFast), and arrows toggle directionally
        // (ToggleSwitchKeyProcess.h:52-71).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w1tsk", new Size2(320, 160), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var root = new ToggleSwitchProbe();
            var clock = new ManualFrameTimeSource();
            using var host = new AppHost(app, window, device, fonts, strings, root, frameTime: clock);
            host.RunFrame();
            var control = FindRole(host.Scene, host.Scene.Root, AutomationRole.ToggleSwitch);
            var track = Child(host.Scene, control, 0);
            var knobHost = Child(host.Scene, track, 1);            // the 20×20 positioning host OWNS the travel FLIP
            var knob = Child(host.Scene, knobHost, 0);

            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Tab));
            host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Space));
            host.RunFrame();
            bool armedNotToggled = !root.On && (host.Scene.Flags(control) & NodeFlags.Pressed) != 0;
            window.QueueInput(new InputEvent(InputKind.KeyUp, default, 0, Keys.Space));
            host.RunFrame();                                       // commit frame (dt 0): FLIP seeded at the full inverse
            bool toggledOnUp = root.On;
            float dx0 = host.Scene.Paint(knobHost).LocalTransform.Dx;  // ≈ −20: presented still at the off spot

            clock.Advance(50f); host.RunFrame();                   // mid-travel of the 167ms tween
            float dxMid = host.Scene.Paint(knobHost).LocalTransform.Dx;
            clock.Advance(500f); host.RunFrame(); host.RunFrame();
            float dxEnd = host.Scene.Paint(knobHost).LocalTransform.Dx;
            var trk = host.Scene.AbsoluteRect(track);
            var kOn = host.Scene.AbsoluteRect(knob);
            bool seeded = Near(dx0, -20f, 1.5f);
            bool tweened = dxMid > -19.5f && dxMid < -0.5f;
            bool settledTravel = MathF.Abs(dxEnd) < 0.5f && Near(kOn.X - trk.X, 24f);

            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Left));
            host.RunFrame(); bool leftOff = !root.On;
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Right));
            host.RunFrame(); bool rightOn = root.On;
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Right));
            host.RunFrame(); bool rightNoop = root.On;             // already on → no toggle (Key only handled when it toggles)

            Check("w1controls.6 ToggleSwitch keys: Space toggles on KEY-UP (armed pressed until then); knob travel tweens (seed −20 → mid → settle +20); arrows toggle directionally",
                armedNotToggled && toggledOnUp && seeded && tweened && settledTravel && leftOff && rightOn && rightNoop,
                $"armed={armedNotToggled} up={toggledOnUp} dx {dx0:0.#}→{dxMid:0.#}→{dxEnd:0.##} L={leftOff} R={rightOn} Rnoop={rightNoop}");
        }

        // w1controls.7 — ToggleSwitch drag-to-toggle (ToggleSwitch_Partial.cpp): the 4px drag box arms the knob drag
        // (:829-836 over the SM_CXDRAG threshold), the knob FOLLOWS the pointer clamped to the travel (:455-457,
        // :579-589), release toggles iff the knob crossed HALF the travel (MoveCompleted :591-619), and a pointer that
        // leaves mid-press cancels — the captured outside release must NOT toggle (capture-lost cleanup :728-746).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w1tsd", new Size2(480, 320), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var root = new ToggleSwitchProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var control = FindRole(host.Scene, host.Scene.Root, AutomationRole.ToggleSwitch);
            var track = Child(host.Scene, control, 0);
            var knob = Child(host.Scene, Child(host.Scene, track, 1), 0);
            var trk = host.Scene.AbsoluteRect(track);
            var c = CenterOf(host.Scene, control);

            // 6px: past the 4px drag box but under half the 20px travel → release does NOT toggle.
            window.QueueInput(new InputEvent(InputKind.PointerDown, c, 0, 0)); host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(c.X + 6f, c.Y), 0, 0)); host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(c.X + 6f, c.Y), 0, 0)); host.RunFrame();
            bool smallDragStaysOff = !root.On;

            // 12px: crosses half the travel; mid-drag the knob follows the pointer (dragX 12 + the 3px pressed pin).
            // AbsoluteRect includes the presented FLIP transform — hold a few frames so the drag's snap-follow
            // (1ms tween) and the press-grow settle onto the model spot before sampling.
            window.QueueInput(new InputEvent(InputKind.PointerDown, c, 0, 0)); host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(c.X + 12f, c.Y), 0, 0));
            for (int i = 0; i < 10; i++) host.RunFrame();
            var kDrag = host.Scene.AbsoluteRect(knob);
            bool knobFollows = Near(kDrag.X - trk.X, 15f) && Near(kDrag.W, 17f) && Near(kDrag.H, 14f);
            window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(c.X + 12f, c.Y), 0, 0)); host.RunFrame();
            bool bigDragTogglesOn = root.On;

            // Drag back toward off, then EXIT the control and release outside: cancelled, stays ON.
            window.QueueInput(new InputEvent(InputKind.PointerDown, c, 0, 0)); host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(c.X - 12f, c.Y), 0, 0)); host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(450f, 300f), 0, 0)); host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(450f, 300f), 0, 0)); host.RunFrame();
            bool releaseOutsideCancels = root.On;

            // The cancel doesn't wedge the gesture: a later plain tap still toggles.
            ClickNode(host, window, control);
            bool tapAfterCancel = !root.On;

            Check("w1controls.7 ToggleSwitch drag: 4px drag box, half-travel rule (6px no / 12px yes), knob follows the pointer, exit + release-outside cancels without toggling",
                smallDragStaysOff && knobFollows && bigDragTogglesOn && releaseOutsideCancels && tapAfterCancel,
                $"small={smallDragStaysOff} follows={knobFollows} big={bigDragTogglesOn} cancel={releaseOutsideCancels} tap={tapAfterCancel}");
        }

        // w1controls.8 — RadioButtons container (controls\dev\RadioButtons): column-major MaxColumns grid
        // (ColumnMajorUniformToLargestGridLayout.cpp:48-163; ColumnSpacing 7 / RowSpacing 8 / header gap 8,
        // RadioButtons_themeresources.xaml:18-20), ONE roving tab stop (RadioButtons.xaml:5-6 + OnGettingFocus :80-97),
        // arrows rove with SELECTION FOLLOWS FOCUS unless Ctrl (:100-107, :135-183), edges swallow (:216-242).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w1rb", new Size2(420, 320), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var root = new W1RadioButtonsProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();

            var radios = Roles(host.Scene, AutomationRole.RadioButton);
            var a = host.Scene.AbsoluteRect(radios[0]);
            var b = host.Scene.AbsoluteRect(radios[1]);
            var d = host.Scene.AbsoluteRect(radios[3]);
            bool grid = radios.Count == 5
                && Near(d.Y, a.Y) && Near(d.X, a.X + a.W + 7f)
                && Near(b.Y, a.Y + a.H + 8f) && Near(b.X, a.X);
            var header = FindTextNode(host.Scene, strings, host.Scene.Root, "w1-group");
            var hrr = host.Scene.AbsoluteRect(header);
            bool headerRow = !header.IsNull && Near(a.Y - (hrr.Y + hrr.H), 8f, 1f)
                && ColorClose(GlyphColor(device, strings, "w1-group"), Tok.TextPrimary, 0.004f);   // RadioButtonsHeaderForeground = TextFillColorPrimary (themeresources:4-10)

            int FocusableIdx(out int count)
            {
                count = 0; int idx = -1;
                for (int i = 0; i < radios.Count; i++)
                    if ((host.Scene.Flags(radios[i]) & NodeFlags.Focusable) != 0) { count++; idx = i; }
                return idx;
            }
            bool roving0 = FocusableIdx(out int fc0) == 0 && fc0 == 1;

            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Tab));
            host.RunFrame();
            bool tabLands = FocusedNode(host.Scene, host.Scene.Root) == radios[0];

            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Down));
            host.RunFrame();
            bool downSelects = root.Selected == 1 && FocusedNode(host.Scene, host.Scene.Root) == radios[1];

            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Down, Mods: KeyModifiers.Ctrl));
            host.RunFrame();
            bool ctrlMovesOnly = root.Selected == 1 && FocusedNode(host.Scene, host.Scene.Root) == radios[2];

            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Right));
            host.RunFrame();
            bool rightColumn = root.Selected == 4 && FocusedNode(host.Scene, host.Scene.Root) == radios[4];   // (col0,row2) → col1 clamped to row1 = E

            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Down));
            host.RunFrame();
            bool edgeSwallow = root.Selected == 4 && FocusedNode(host.Scene, host.Scene.Root) == radios[4];

            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Left));
            host.RunFrame();
            bool leftBack = root.Selected == 1 && FocusableIdx(out int fc1) == 1 && fc1 == 1;   // the tab stop follows the selection

            Check("w1controls.8 RadioButtons: column-major MaxColumns grid (7/8 spacing, header gap 8, TextPrimary ARGB), ONE roving tab stop, arrows rove + selection-follows-focus, Ctrl exempts, edges swallow",
                grid && headerRow && roving0 && tabLands && downSelects && ctrlMovesOnly && rightColumn && edgeSwallow && leftBack,
                $"grid={grid} hdr={headerRow} roving={roving0} tab={tabLands} down={downSelects} ctrl={ctrlMovesOnly} right={rightColumn} edge={edgeSwallow} left={leftBack}");
        }

        // w1controls.9 — Slider keyboard matrix (KeyPress::Slider::KeyDown, SliderKeyProcess.h:28-71 + the PageUp/Down
        // parity rows on Slider::Step, Slider_Partial.cpp:1713-1819): steps snap to the closest step multiple, and the
        // AUTO step sizes derive range/100 and range/10 (WinUI's absolute defaults 1/10 on the 0–100 range,
        // Slider_Partial.h:13-15). Header per Slider_themeresources.xaml:396 + SliderTopHeaderMargin 0,0,0,4 (:161).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w1sl", new Size2(320, 160), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var root = new W1SliderKeysProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();

            var track = FindRole(host.Scene, host.Scene.Root, AutomationRole.Slider);
            var tr = host.Scene.AbsoluteRect(track);
            var hdr = FindTextNode(host.Scene, strings, host.Scene.Root, "w1-vol");
            var hr2 = host.Scene.AbsoluteRect(hdr);
            bool headered = !hdr.IsNull && Near(tr.Y - (hr2.Y + hr2.H), 4f, 1f)
                && ColorClose(GlyphColor(device, strings, "w1-vol"), Tok.TextPrimary, 0.004f);   // SliderHeaderForeground = TextFillColorPrimary (:28)

            var p = new Point2(tr.X + 100f, tr.Y + tr.H / 2f);
            window.QueueInput(new InputEvent(InputKind.PointerDown, p, 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerUp, p, 0, 0));
            host.RunFrame();
            bool clicked = Near(root.Val, 100f, 0.01f);

            float Key(int key) { window.QueueInput(new InputEvent(InputKind.Key, default, 0, key)); host.RunFrame(); return root.Val; }
            float right = Key(Keys.Right);     // +SmallChange(auto 2) → 102
            float pgUp = Key(Keys.PageUp);     // +LargeChange(auto 20), snapped to the 20-grid → 120 (not 122)
            float pgDn = Key(Keys.PageDown);   // 100
            float left = Key(Keys.Left);       // 98
            float down = Key(Keys.Down);       // 96 (Down = backward, SliderKeyProcess.h:52-59)
            float up = Key(Keys.Up);           // 98 (Up = forward, :44-51)
            float home = Key(Keys.Home);       // Minimum (:60-65)
            float end = Key(Keys.End);         // Maximum (:66-71)

            Check("w1controls.9 Slider keyboard matrix + AUTO Small/Large (range/100, range/10): ±2 arrows, PageUp 102→120 (closest-multiple snap), Home/End; header 4px above (ARGB)",
                headered && clicked && Near(right, 102f, 0.01f) && Near(pgUp, 120f, 0.01f) && Near(pgDn, 100f, 0.01f)
                && Near(left, 98f, 0.01f) && Near(down, 96f, 0.01f) && Near(up, 98f, 0.01f) && Near(home, 0f, 0.01f) && Near(end, 200f, 0.01f),
                $"hdr={headered} click={clicked} R={right:0.#} PgUp={pgUp:0.#} PgDn={pgDn:0.#} L={left:0.#} D={down:0.#} U={up:0.#} Home={home:0.#} End={end:0.#}");
        }

        // w1controls.10 — Slider visuals: inline tick rects (TickPlacement default Inline, visibility mapping
        // Slider_Partial.cpp:2248-2303; SliderInlineTickBarFill = ControlFillColorInputActive,
        // Slider_themeresources.xaml:32), the thumb value tooltip shows on PRESS and scrubs live
        // (UpdateThumbToolTipVisibility, Slider_Partial.cpp:478-543; default converter :1859-1936), hides on release
        // (PerformPointerUpAction :645-659); FocusVisualMargin −7,0,−7,0 (:184) widens the ring horizontally only.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w1slt", new Size2(360, 240), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var root = new W1SliderTipProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();

            var track = FindRole(host.Scene, host.Scene.Root, AutomationRole.Slider);
            var tr = host.Scene.AbsoluteRect(track);

            var tickXs = new List<float>();
            bool tickColor = true;
            foreach (var r in device.LastRects)
                if (Near(r.Rect.W, 1f, 0.01f) && Near(r.Rect.H, 4f, 0.01f))
                {
                    tickXs.Add(r.Transform.Dx);
                    tickColor &= ColorClose(r.Fill, Tok.FillControlInputActive, 0.004f);
                }
            tickXs.Sort();
            bool ticks = tickXs.Count == 5 && tickColor;
            for (int i = 1; i < tickXs.Count; i++) ticks &= Near(tickXs[i] - tickXs[i - 1], 50f, 1f);

            var p100 = new Point2(tr.X + 100f, tr.Y + tr.H / 2f);
            window.QueueInput(new InputEvent(InputKind.PointerDown, p100, 0, 0));
            host.RunFrame(); host.RunFrame();                    // open → the overlay content mounts + places next frame
            bool tipShows = HasGlyph(device, strings, "100") && Near(root.Val, 100f, 0.01f);
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(tr.X + 150f, tr.Y + tr.H / 2f), 0, 0));
            host.RunFrame();
            bool tipScrubs = HasGlyph(device, strings, "150") && Near(root.Val, 150f, 0.01f);

            window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(tr.X + 150f, tr.Y + tr.H / 2f), 0, 0));
            for (int i = 0; i < 20; i++) host.RunFrame();
            bool tipHides = !HasGlyph(device, strings, "150");

            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Tab)); host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Tab)); host.RunFrame();
            DrawRoundRectStrokeCmd prim = default;
            foreach (var s in device.LastStrokes) if (Near(s.StrokeWidth, 2f)) prim = s;
            bool ring = Near(prim.Rect.X, -6f) && Near(prim.Rect.Y, 1f) && Near(prim.Rect.W, tr.W + 12f) && Near(prim.Rect.H, tr.H - 2f);

            Check("w1controls.10 Slider visuals: 5 inline tick rects 50px apart (InputActive ARGB); press shows the thumb tooltip '100', drag scrubs to '150', release hides; focus ring −7,0,−7,0",
                ticks && tipShows && tipScrubs && tipHides && ring,
                $"ticks={tickXs.Count} color={tickColor} show={tipShows} scrub={tipScrubs} hide={tipHides} ring=({prim.Rect.X:0.#},{prim.Rect.Y:0.#} {prim.Rect.W:0.#}x{prim.Rect.H:0.#})");
        }

        // w1controls.11 — RatingControl: the per-star focal hover SCALE (the composition expression,
        // RatingControl.cpp:350-371, re-based ×2 into the 16px-native strip → focal star 2×c_mouseOverScale = 1.6,
        // far stars floor at 2×0.5 = 1.0), the pointer-over-UNSET preview brush (RatingControlPointerOverUnselected-
        // Foreground = ControlAltFillColorTertiary), and the drag-off-the-left-side clear (capture keeps the sweep
        // alive off-strip, cpp:799-805/856-863; release commits swept 0 → the cleared sentinel, :888-906).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w1rt", new Size2(320, 120), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var root = new RatingProbe { Initial = RatingControl.NoValueSet };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();

            var rating = FindRole(host.Scene, host.Scene.Root, AutomationRole.Rating);
            var rr = host.Scene.AbsoluteRect(rating);
            var starRow = Child(host.Scene, rating, 0);
            var cell0 = Child(host.Scene, starRow, 0);
            var cell4 = Child(host.Scene, starRow, 4);

            var hov = new Point2(rr.X + 8f, rr.Y + rr.H / 2f);   // star 1 centre (StarCenter(0) = 8)
            window.QueueInput(new InputEvent(InputKind.PointerMove, hov, 0, 0));
            host.RunFrame(); host.RunFrame();
            float s0 = host.Scene.Paint(cell0).LocalTransform.M11;
            float s4 = host.Scene.Paint(cell4).LocalTransform.M11;
            bool focal = Near(s0, 1.6f, 0.05f) && Near(s4, 1.0f, 0.02f);

            const string filled = "";
            int hovFilled = CountGlyph(device, strings, filled);
            bool hovColor = ColorClose(GlyphColor(device, strings, filled), Tok.FillControlAltTertiary, 0.004f);
            bool uncommitted = root.Val!.Peek() <= RatingControl.NoValueSet;

            var p3 = new Point2(rr.X + 56f, rr.Y + rr.H / 2f);   // ceil(56/112·5) = 3
            window.QueueInput(new InputEvent(InputKind.PointerDown, p3, 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerUp, p3, 0, 0));
            host.RunFrame();
            float committed = root.Val!.Peek();

            var p1 = new Point2(rr.X + 20f, rr.Y + rr.H / 2f);   // press on star 1...
            window.QueueInput(new InputEvent(InputKind.PointerDown, p1, 0, 0)); host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(rr.X - 80f, rr.Y + rr.H / 2f), 0, 0)); host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(rr.X - 80f, rr.Y + rr.H / 2f), 0, 0)); host.RunFrame();
            float cleared = root.Val!.Peek();                     // ...drag past the LEFT edge and release → cleared
            host.RunFrame();
            float s0After = host.Scene.Paint(cell0).LocalTransform.M11;   // focal back at the −100 sentinel → 1.0

            Check("w1controls.11 RatingControl: focal hover scale 1.6 focal / 1.0 far (mouse 0.8 expression), pointer-over-unset preview ARGB, drag-off-left clears to −1, focal resets on release",
                focal && hovFilled == 1 && hovColor && uncommitted && Near(committed, 3f) && Near(cleared, -1f) && Near(s0After, 1f, 0.02f),
                $"s0={s0:0.00} s4={s4:0.00} filled={hovFilled} color={hovColor} committed={committed} cleared={cleared} reset={s0After:0.00}");
        }
    }

    // ── E11 — unified virtualization substrate (L0 measured seam, L1 viewport, L2 repeater data layer, L3 ItemsView).
    //    Every behavior/value verified against the WinUI sources cited inline (controls\dev\ItemsView, ItemContainer,
    //    ItemsRepeater, LinedFlowLayout; Common_themeresources_any.xaml for ARGB). ──────────────────────────────────
    static void E11VirtChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);

        // e11virt.1/2 — the IMeasuredVirtualLayout seam (E11-L0): estimate-then-correct + scroll anchoring.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("e11-measured", new Size2(640, 480), 1f));
            window.Show();
            var probe = new MeasuredSeamProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();

            var vp = host.Scene.Root;
            host.Scene.TryGetScroll(vp, out var sc0);
            var content = sc0.ContentNode;
            var layout = probe.Layout!;
            const float cross = 300f;

            // Realized rows correct from the 40px estimate to their measured extents at arrange (SetMeasured);
            // positions are the corrected prefix sums (OffsetOf) — virtualization.md §6.2 through the USER seam.
            var r1 = Child(host.Scene, content, 1);
            var r2 = Child(host.Scene, content, 2);
            var r3 = Child(host.Scene, content, 3);
            float h01 = MeasuredSeamProbe.H(0) + MeasuredSeamProbe.H(1);
            bool corrected = Near(host.Scene.Bounds(r1).Y, MeasuredSeamProbe.H(0))
                && Near(host.Scene.Bounds(r2).Y, h01)
                && Near(host.Scene.Bounds(r3).Y, h01 + MeasuredSeamProbe.H(2))
                && Near(layout.ItemRect(1, cross).H, MeasuredSeamProbe.H(1))
                && Near(layout.OffsetOf(3, cross), h01 + MeasuredSeamProbe.H(2));

            // Unrealized rows still report the estimate; published ContentSize = corrected window + estimate tail.
            float expected = 0f;
            for (int i = 0; i < sc0.LastRealized; i++) expected += MeasuredSeamProbe.H(i);
            expected += (MeasuredSeamProbe.N - sc0.LastRealized) * MeasuredSeamProbe.Estimate;
            bool estimated = Near(layout.ItemRect(MeasuredSeamProbe.N - 1, cross).H, MeasuredSeamProbe.Estimate)
                && Near(sc0.ContentH, expected, 1f);
            Check("e11virt.1 measured seam: realized rows correct to measured extents (positions = corrected prefix sums); unrealized keep the estimate",
                corrected && estimated, $"y1..3={host.Scene.Bounds(r1).Y:0},{host.Scene.Bounds(r2).Y:0},{host.Scene.Bounds(r3).Y:0} content={sc0.ContentH:0} expected={expected:0}");

            // Anchoring: scroll into the middle — the offset stays inside the anchor item's band across the
            // realize+correction waves (corrections above the viewport never jump the visible top).
            var ptr = new Point2(150, 150);
            for (int s = 0; s < 8; s++) { window.QueueInput(new InputEvent(InputKind.Wheel, ptr, 0, 0, 400f)); host.RunFrame(); }
            host.Scene.TryGetScroll(vp, out var sc1);
            int anchor = layout.IndexAt(sc1.OffsetY, cross);
            float band0 = layout.OffsetOf(anchor, cross), band1 = layout.OffsetOf(anchor + 1, cross);
            bool anchored = sc1.AnchorIndex == anchor && sc1.FirstRealized > 0
                && sc1.OffsetY >= band0 - 0.5f && sc1.OffsetY < band1 + 0.5f;

            // Fling to the end: each fling clamps against the content published SO FAR; the realize wave then corrects
            // the freshly measured rows and EXTENDS the content (estimate-then-correct), so the true end takes a
            // couple of flings — after which the offset clamps to the fully corrected extent and realize reaches row N.
            for (int s = 0; s < 3; s++) { window.QueueInput(new InputEvent(InputKind.Wheel, ptr, 0, 0, 1_000_000f)); host.RunFrame(); }
            host.Scene.TryGetScroll(vp, out var sc2);
            bool clamped = sc2.LastRealized == MeasuredSeamProbe.N && Near(sc2.OffsetY, sc2.ContentH - sc2.ViewportH, 2f);
            Check("e11virt.2 measured seam anchoring: offset pinned inside the anchor band mid-list; end-fling clamps to corrected content",
                anchored && clamped, $"anchor={anchor} off={sc1.OffsetY:0} band=[{band0:0},{band1:0}) end={sc2.OffsetY:0}/{sc2.ContentH - sc2.ViewportH:0}");
        }

        // e11virt.3 — LinedFlowLayout (the WinUI ItemsView photo-wall): uniform-height lines, width = aspect × lineHeight
        // clamped to the cross size, wrap when the next item + MinItemSpacing would overflow, O(1) line-stride windowing.
        // Defaults LineSpacing 0 / MinItemSpacing 0 (LinedFlowLayout.h s_defaultLineSpacing/s_defaultMinItemSpacing).
        {
            float[] aspects = [2f, 1f, 0.5f, 4f];
            var lf = new LinedFlowLayout(lineHeight: 100f, aspectRatio: i => aspects[i % 4], lineSpacing: 10f, minItemSpacing: 5f);
            const float cross = 350f;
            // Flow on cross 350: line0=[0:w200@0, 1:w100@205] line1=[2:w50@0] line2=[3:w350@0 (clamped)]
            //                    line3=[4:w200@0, 5:w100@205] line4=[6:w50@0] line5=[7:w350@0] → 6 lines.
            float extent = lf.ContentExtent(8, cross);
            var i0 = lf.ItemRect(0, cross); var i1 = lf.ItemRect(1, cross); var i2 = lf.ItemRect(2, cross);
            var i3 = lf.ItemRect(3, cross); var i5 = lf.ItemRect(5, cross); var i7 = lf.ItemRect(7, cross);
            bool widths = Near(i0.W, 200f) && Near(i1.W, 100f) && Near(i2.W, 50f) && Near(i3.W, 350f) && Near(i0.H, 100f);
            bool flow = Near(i0.X, 0f) && Near(i0.Y, 0f)
                && Near(i1.X, 205f) && Near(i1.Y, 0f)            // 200 + MinItemSpacing 5
                && Near(i2.X, 0f) && Near(i2.Y, 110f)            // wrapped (305+5+50 > 350); line stride = 100+10
                && Near(i3.X, 0f) && Near(i3.Y, 220f)            // over-wide item → its own full-width line
                && Near(i5.X, 205f) && Near(i5.Y, 330f)
                && Near(i7.Y, 550f) && Near(extent, 650f);       // 6×100 + 5×10
            lf.Window(8, cross, 200f, 115f, 0, out int f0, out int l0);   // band [115,315] → lines 1..3 → items [2,6)
            lf.Window(8, cross, 200f, 115f, 2, out int f1, out int l1);   // ±2 items of overscan, clamped
            lf.Window(8, cross, 200f, 0f, 0, out int f2, out int l2);     // top: lines 0..2 → items [0,4)
            bool windows = f0 == 2 && l0 == 6 && f1 == 0 && l1 == 8 && f2 == 0 && l2 == 4;
            // WinUI defaults: no spacing — 3 unit-aspect items pack one 100px line.
            var lfDef = new LinedFlowLayout(100f);
            bool defaults = Near(lfDef.ContentExtent(3, cross), 100f) && Near(lfDef.ItemRect(2, cross).X, 200f);
            Check("e11virt.3 LinedFlow: aspect widths (cross-clamped), spacing-aware wrap, line-stride rects + windowing, 0-spacing defaults",
                widths && flow && windows && defaults, $"extent={extent:0} w0..3={i0.W:0},{i1.W:0},{i2.W:0},{i3.W:0} win=({f0},{l0})/({f1},{l1})/({f2},{l2})");
        }

        // e11virt.4 — GroupedListVirtualLayout (E11-L0 grouping): headers are a measured item KIND at their own flat
        // indices; StickyHeaderIndexAt = last header at-or-above the offset band (−1 above the first header), and the
        // pivot tracks estimate-then-correct band moves.
        {
            var gl = new GroupedListVirtualLayout([0, 6, 13], headerExtent: 32f, itemEstimate: 48f);
            const int n = 20; const float cross = 300f;
            float total = gl.ContentExtent(n, cross);            // 3×32 + 17×48 = 912
            bool seeded = Near(total, 912f) && gl.IsHeader(6) && !gl.IsHeader(7)
                && Near(gl.OffsetOf(6, cross), 272f)             // 32 + 5×48
                && Near(gl.ItemRect(6, cross).H, 32f) && Near(gl.ItemRect(7, cross).H, 48f);
            bool sticky = gl.StickyHeaderIndexAt(0f) == 0
                && gl.StickyHeaderIndexAt(100f) == 0
                && gl.StickyHeaderIndexAt(272f) == 6             // exactly at the group-2 band start
                && gl.StickyHeaderIndexAt(591f) == 6             // last row of group 2 (header 13 starts at 592)
                && gl.StickyHeaderIndexAt(593f) == 13;
            gl.SetMeasured(3, 80f, cross);                       // estimate-then-correct: row 3 48 → 80 (+32)
            bool correctedG = Near(gl.ContentExtent(n, cross), 944f)
                && Near(gl.OffsetOf(6, cross), 304f)
                && gl.StickyHeaderIndexAt(300f) == 0 && gl.StickyHeaderIndexAt(305f) == 6;
            var gl2 = new GroupedListVirtualLayout([4], 32f, 48f);
            _ = gl2.ContentExtent(10, cross);
            bool none = gl2.StickyHeaderIndexAt(0f) == -1 && gl2.StickyHeaderIndexAt(4 * 48f + 1f) == 4;
            Check("e11virt.4 GroupedList: header extents seeded, sticky index per offset band (−1 above first), correction moves the pivot",
                seeded && sticky && correctedG && none, $"total={total:0}→{gl.ContentExtent(n, cross):0} hdr6@{gl.OffsetOf(6, cross):0}");
        }

        // e11virt.5 — ItemsRepeater lifecycle (E11-L2, ItemsRepeater.idl:186-188): ElementPrepared on entering the
        // realized window, ElementClearing on leaving (recycle = Clearing(old)+Prepared(new)), visible-range prefetch;
        // a steady in-window scroll fires NOTHING (transform-only frames never realize).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("e11-lifecycle", new Size2(640, 480), 1f));
            window.Show();
            var probe = new LifecycleRepeaterProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();

            var vp = host.Scene.Root;
            host.Scene.TryGetScroll(vp, out var sc0);
            bool mountSequential = probe.Prepared.Count == sc0.LastRealized && probe.Cleared.Count == 0;
            for (int i = 0; i < probe.Prepared.Count; i++) mountSequential &= probe.Prepared[i] == i;
            bool mountRange = probe.Ranges.Count > 0 && probe.Ranges[^1] == (0, sc0.LastRealized);

            // sub-extent scroll: in-window → no realize → no lifecycle.
            int p0 = probe.Prepared.Count, c0 = probe.Cleared.Count, rg0 = probe.Ranges.Count;
            var ptr = new Point2(150, 200);
            window.QueueInput(new InputEvent(InputKind.Wheel, ptr, 0, 0, 2f));
            host.RunFrame();
            bool quiet = probe.Prepared.Count == p0 && probe.Cleared.Count == c0 && probe.Ranges.Count == rg0;

            // boundary-crossing scroll: 400px over 40px rows → window [0,14) → [6,24): Clearing 0..5, Prepared 14..23.
            window.QueueInput(new InputEvent(InputKind.Wheel, ptr, 0, 0, 398f));
            host.RunFrame();
            host.Scene.TryGetScroll(vp, out var sc1);
            var live = new HashSet<int>();
            foreach (var i in probe.Prepared) live.Add(i);
            foreach (var i in probe.Cleared) live.Remove(i);
            bool windowSet = live.Count == sc1.LastRealized - sc1.FirstRealized && sc1.FirstRealized > 0;
            for (int i = sc1.FirstRealized; i < sc1.LastRealized; i++) windowSet &= live.Contains(i);
            bool conserved = probe.Prepared.Count - probe.Cleared.Count == sc1.LastRealized - sc1.FirstRealized
                && probe.Cleared.Count == sc1.FirstRealized                          // exactly the rows that left the top
                && probe.Ranges[^1] == (sc1.FirstRealized, sc1.LastRealized);
            Check("e11virt.5 ItemsRepeater lifecycle: Prepared/Clearing mirror the realized window across a recycle; in-window scroll fires nothing",
                mountSequential && mountRange && quiet && windowSet && conserved,
                $"mount=[0,{sc0.LastRealized}) → [{sc1.FirstRealized},{sc1.LastRealized}) prepared={probe.Prepared.Count} cleared={probe.Cleared.Count}");
        }

        // e11virt.6 — typed ItemsRepeater (E11-L2): the (index, item) template binds without casts, and an
        // ItemCollectionTransition stamps the engine FLIP/fade spec on each item root — Moves = Position FLIP,
        // Adds/Removes = opacity 0↔1, over ControlFastAnimationDuration 167ms decelerate (the ItemContainer.xaml:54-56
        // KeySpline 0,0,0,1 timing).
        {
            var data = new List<string> { "alpha", "beta", "gamma" };
            var el = Repeater.ItemsRepeater(data, (i, s) => new BoxEl { Children = [new TextEl(s) { Size = 12f }] },
                RepeatLayout.Inline(), transition: ItemCollectionTransition.Default);
            bool typed = el is BoxEl row && row.Children.Length == 3
                && row.Children[1] is BoxEl b1 && b1.Children[0] is TextEl t1 && t1.Text.Value == "beta";
            var spec = el is BoxEl row2 && row2.Children[0] is BoxEl item0 ? item0.Animate : null;
            bool stamped = spec is { } sp
                && (sp.Channels & TransitionChannels.Position) != 0 && (sp.Channels & TransitionChannels.Opacity) != 0
                && sp.Dynamics.Kind == DynamicsKind.Tween && Near(sp.Dynamics.DurationMs, 167f)
                && sp.Dynamics.Easing == Easing.FluentDecelerate
                && sp.Enter.Active && Near(sp.Enter.Opacity, 0f) && sp.Exit.Active && Near(sp.Exit.Opacity, 0f);
            Check("e11virt.6 ItemsRepeater typed template + ItemCollectionTransition → Position FLIP + 167ms enter/exit fades on item roots",
                typed && stamped, $"typed={typed} stamped={stamped}");
        }

        // e11virt.7 — SelectionModel Single (SingleSelector.cpp:25-57): Select REPLACES; Ctrl+interact toggles;
        // plain focus follows (m_followFocus default true); Ctrl+focus moves without selecting.
        {
            var m = new SelectionModel { ItemCount = 100 };
            int events = 0;
            m.SelectionChanged = () => events++;
            bool def = m.Mode == ItemsSelectionMode.Single && m.SelectedCount == 0 && m.AnchorIndex == -1;   // ItemsView.h s_defaultSelectionMode
            m.Select(3); m.Select(7);
            bool replaces = m.IsSelected(7) && !m.IsSelected(3) && m.SelectedCount == 1 && events == 2;
            m.OnInteractedAction(7, ctrl: true, shift: false);    // selected + Ctrl → deselect (cpp:39-43)
            bool ctrlOff = m.SelectedCount == 0 && events == 3;
            m.OnInteractedAction(7, ctrl: true, shift: false);    // unselected + Ctrl → select (cpp:35-38)
            bool ctrlOn = m.IsSelected(7) && events == 4;
            m.OnFocusedAction(8, ctrl: false, shift: false);      // follow-focus (cpp:46-57)
            bool follow = m.IsSelected(8) && m.SelectedCount == 1;
            m.OnFocusedAction(9, ctrl: true, shift: false);       // Ctrl+focus: no selection change
            bool ctrlFocus = m.IsSelected(8) && !m.IsSelected(9) && m.Version.Peek() == events;
            Check("e11virt.7 SelectionModel Single: replace-on-select, ctrl-toggle, focus-follow, ctrl-focus inert (SingleSelector.cpp:25-57)",
                def && replaces && ctrlOff && ctrlOn && follow && ctrlFocus, $"events={events} version={m.Version.Peek()}");
        }

        // e11virt.8 — SelectionModel Multiple (MultipleSelector.cpp:18-92): toggle without modifiers; Shift extends or
        // deselects the anchor range by the ANCHOR's state (only when the states differ); Shift with NO anchor is a
        // NO-OP (the toggle is cpp's `else` — it never runs while Shift is held); plain focus moves never select.
        {
            var m = new SelectionModel { ItemCount = 100, Mode = ItemsSelectionMode.Multiple };
            m.OnInteractedAction(4, ctrl: false, shift: true);    // no anchor yet → no-op (cpp:24-63)
            bool shiftNoAnchor = m.SelectedCount == 0 && m.AnchorIndex == -1;
            m.OnInteractedAction(2, false, false);                // toggle on → anchor 2
            m.OnInteractedAction(6, false, true);                 // anchor selected, 6 not → SelectRangeFromAnchorTo
            bool shiftRange = m.SelectedCount == 5 && m.RangeCount == 1 && m.GetRange(0) == (2, 6);
            m.OnInteractedAction(4, false, true);                 // anchor and 4 BOTH selected → states equal → nothing (cpp:44-52)
            bool statesEqual = m.SelectedCount == 5;
            m.OnFocusedAction(8, false, false);                   // plain focus move never selects (cpp:65-92)
            bool focusInert = m.SelectedCount == 5 && !m.IsSelected(8);
            m.OnInteractedAction(2, false, false);                // toggle the anchor itself OFF (anchor stays 2)
            m.OnInteractedAction(5, false, true);                 // anchor UNselected, 5 selected → DeselectRangeFromAnchorTo
            bool shiftDeselect = !m.IsSelected(3) && !m.IsSelected(5) && m.IsSelected(6) && m.SelectedCount == 1;
            Check("e11virt.8 SelectionModel Multiple: modifier-free toggle, shift range by anchor state, shift-no-anchor no-op (MultipleSelector.cpp:18-92)",
                shiftNoAnchor && shiftRange && statesEqual && focusInert && shiftDeselect,
                $"count={m.SelectedCount} ranges={m.RangeCount}");
        }

        // e11virt.9 — SelectionModel Extended (ExtendedSelector.cpp:18-83): plain replaces ONLY on an unselected item;
        // Ctrl toggles; Shift replaces with the anchor range; focus: Shift+Ctrl additive, Shift replace, plain replace,
        // Ctrl alone moves without selecting.
        {
            var m = new SelectionModel { ItemCount = 100, Mode = ItemsSelectionMode.Extended };
            m.OnInteractedAction(2, false, false);                // plain → clear+select, anchor 2
            m.OnInteractedAction(6, false, true);                 // Shift → replace with [anchor..6] (cpp:23-32)
            bool range = m.SelectedCount == 5 && m.GetRange(0) == (2, 6) && m.AnchorIndex == 2;
            m.OnInteractedAction(9, true, false);                 // Ctrl → additive toggle (cpp:33-43)
            bool ctrlAdd = m.SelectedCount == 6 && m.IsSelected(9);
            m.OnInteractedAction(4, false, false);                // plain on a SELECTED item → keep (cpp:46 "Only clear ... different item")
            bool keepOnSelected = m.SelectedCount == 6;
            m.OnInteractedAction(20, false, false);               // plain on unselected → clear+select
            bool replace = m.SelectedCount == 1 && m.IsSelected(20) && m.AnchorIndex == 20;
            m.OnFocusedAction(23, false, true);                   // Shift+focus → replace with the anchor range (cpp:66-75)
            bool focusShift = m.SelectedCount == 4 && m.GetRange(0) == (20, 23) && m.AnchorIndex == 20;
            m.OnFocusedAction(30, true, false);                   // Ctrl+focus → nothing (cpp falls through)
            bool focusCtrl = m.SelectedCount == 4;
            m.OnFocusedAction(28, true, true);                    // Shift+Ctrl+focus → ADDITIVE anchor range (cpp:59-65)
            bool focusCtrlShift = m.SelectedCount == 9 && m.GetRange(0) == (20, 28);
            m.OnFocusedAction(40, false, false);                  // plain focus → clear+select (cpp:76-80)
            bool focusPlain = m.SelectedCount == 1 && m.IsSelected(40);
            Check("e11virt.9 SelectionModel Extended: plain/ctrl/shift interact + the four focus chords (ExtendedSelector.cpp:18-83)",
                range && ctrlAdd && keepOnSelected && replace && focusShift && focusCtrl && focusCtrlShift && focusPlain,
                $"count={m.SelectedCount}");
        }

        // e11virt.10 — selection is DECOUPLED from realization: SelectAll over 10k stores ONE inclusive range
        // (never walks indices), deselect splits it, invert complements it, shrinking ItemCount trims it.
        {
            var m = new SelectionModel { ItemCount = 10_000, Mode = ItemsSelectionMode.Extended };
            int events = 0;
            m.SelectionChanged = () => events++;
            m.SelectAll();
            bool one = m.RangeCount == 1 && m.GetRange(0) == (0, 9_999) && m.SelectedCount == 10_000 && events == 1;
            m.SelectAll();                                       // no actual change → no event (WinUI raises only on change)
            bool idempotent = events == 1;
            m.DeselectRange(100, 199);
            bool split = m.RangeCount == 2 && m.SelectedCount == 9_900 && !m.IsSelected(150) && events == 2;
            m.InvertSelection();
            bool inverted = m.RangeCount == 1 && m.GetRange(0) == (100, 199) && m.SelectedCount == 100;
            m.ItemCount = 150;                                   // shrink trims out-of-range selection
            bool trimmed = m.SelectedCount == 50 && m.GetRange(0) == (100, 149);
            Check("e11virt.10 SelectionModel ranges: select-all-over-10k = ONE range (realizes nothing), split/invert/trim stay range-shaped",
                one && idempotent && split && inverted && trimmed, $"events={events} ranges={m.RangeCount} count={m.SelectedCount}");
        }

        // e11virt.10b — selection follows ITEM identity across a RemoveAt+Insert reorder (ListViewBase::ReorderItemsTo),
        // including range splits, instead of staying on the old slot.
        {
            var single = new SelectionModel { ItemCount = 8 };
            int singleEvents = 0;
            single.SelectionChanged = () => singleEvents++;
            single.Select(4);
            single.RemapMove(4, 2);
            bool selectedItemMoved = single.IsSelected(2) && !single.IsSelected(4) && single.FirstSelectedIndex == 2
                && single.AnchorIndex == 2 && singleEvents == 2;

            var range = new SelectionModel { ItemCount = 8, Mode = ItemsSelectionMode.Multiple };
            range.SelectRange(4, 5);
            range.AnchorIndex = 4;
            range.RemapMove(4, 2);
            bool splitRange = range.RangeCount == 2 && range.GetRange(0) == (2, 2) && range.GetRange(1) == (5, 5)
                && range.AnchorIndex == 2;

            var all = new SelectionModel { ItemCount = 10, Mode = ItemsSelectionMode.Extended };
            int allEvents = 0;
            all.SelectionChanged = () => allEvents++;
            all.SelectAll();
            all.RemapMove(8, 2);
            bool allStillCompact = all.RangeCount == 1 && all.GetRange(0) == (0, 9) && allEvents == 1;

            Check("e11virt.10b SelectionModel RemapMove preserves selected item identity across reorder (single, split range, select-all compact)",
                selectedItemMoved && splitRange && allStillCompact,
                $"single={single.FirstSelectedIndex} events={singleEvents} split={range.RangeCount} allEvents={allEvents}");
        }

        // e11virt.11 — ItemContainer state ARGB, BOTH themes (full #AARRGGBB; ItemContainer_themeresources.xaml:5-18
        // dark / :37-49 light → Common_themeresources_any.xaml) + the selected dual-stroke geometry + checkbox plate
        // + disabled collapse.
        {
            bool all = true;
            var details = new System.Text.StringBuilder();
            try
            {
                foreach (var (kind, hover, pressed, ring, inner, plate, plateStroke) in new (ThemeKind, ColorF, ColorF, ColorF, ColorF, ColorF, ColorF)[]
                {
                    // DARK — PointerOver = SubtleFillColorSecondary #0FFFFFFF, Pressed = SubtleFillColorTertiary
                    // #0AFFFFFF (Common_themeresources_any.xaml:26-27); SelectionVisual = AccentFillColorDefault =
                    // SystemAccentColorLight2 #60CDFF (:125); SelectedInnerBorder = ControlSolidFillColorDefault
                    // #454545 (:24); checkbox plate = ControlOnImageFillColorDefault #B31C1C1C (:34); plate stroke =
                    // CheckBoxCheckBackgroundStrokeUnchecked → ControlStrongStrokeColorDefault #8BFFFFFF (:48).
                    (ThemeKind.Dark,
                     ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x0F), ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x0A),
                     ColorF.FromRgba(0x60, 0xCD, 0xFF), ColorF.FromRgba(0x45, 0x45, 0x45),
                     ColorF.FromRgba(0x1C, 0x1C, 0x1C, 0xB3), ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x8B)),
                    // LIGHT — Secondary #09000000 / Tertiary #06000000 (:230-231); accent = SystemAccentColorDark1
                    // #005FB8 (:329); inner = #FFFFFF (:228); plate = #C9FFFFFF (:238); stroke = #72000000 (:252).
                    (ThemeKind.Light,
                     ColorF.FromRgba(0x00, 0x00, 0x00, 0x09), ColorF.FromRgba(0x00, 0x00, 0x00, 0x06),
                     ColorF.FromRgba(0x00, 0x5F, 0xB8), ColorF.FromRgba(0xFF, 0xFF, 0xFF),
                     ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0xC9), ColorF.FromRgba(0x00, 0x00, 0x00, 0x72)),
                })
                {
                    Tok.Use(kind);
                    const float tol = 1.5f / 255f;

                    // Selected + multi-select + unchecked plate → [ic-content, ic-ring, ic-common, ic-check].
                    var scene = LayoutTree(strings, ItemContainer.Build(new BoxEl(), isSelected: true,
                        onInteraction: (t, mods) => { }, showSelectionCheckbox: true, isChecked: false, width: 120f, height: 48f));
                    var root = scene.Root;
                    ref var p = ref scene.Paint(root);
                    var ringN = Child(scene, root, 1);
                    var commonN = Child(scene, root, 2);
                    var plateN = Child(scene, Child(scene, root, 3), 0);
                    bool states = ColorClose(p.Fill, ColorF.Transparent, tol)                      // ItemContainerBackground = SubtleFillColorTransparent (:5/:37)
                        && ColorClose(scene.Paint(commonN).HoverFill, hover, tol) && ColorClose(scene.Paint(commonN).PressedFill, pressed, tol)
                        && ColorClose(scene.Paint(ringN).BorderColor, ring, tol) && Near(scene.Paint(ringN).BorderWidth, 3f)   // PART_SelectionVisual (ItemContainer.xaml:116-126)
                        && ColorClose(scene.Paint(commonN).BorderColor, inner, tol) && Near(scene.Paint(commonN).BorderWidth, 1f);
                    var ir = scene.AbsoluteRect(commonN);
                    var pr = scene.AbsoluteRect(plateN);
                    bool geometry = Near(ir.X, 2f) && Near(ir.Y, 2f) && Near(ir.W, 116f) && Near(ir.H, 44f)   // ItemContainerSelectedInnerMargin 2 (themeresources:57)
                        && Near(pr.W, 20f) && Near(pr.H, 20f) && Near(pr.X, 96f) && Near(pr.Y, -2f);          // 20px checkbox, top-right, Margin 4,−2 (:56,:59-60)
                    bool plateColors = ColorClose(scene.Paint(plateN).Fill, plate, tol)
                        && ColorClose(scene.Paint(plateN).BorderColor, plateStroke, tol)
                        && FindPolylineStrokeNode(scene, root).IsNull;                            // unchecked → no checkmark glyph

                    // Checked plate flips to the accent fill + drawn checkmark.
                    var checkedScene = LayoutTree(strings, ItemContainer.Build(new BoxEl(), true,
                        (t, mods) => { }, showSelectionCheckbox: true, isChecked: true, width: 120f, height: 48f));
                    var cPlate = Child(checkedScene, Child(checkedScene, checkedScene.Root, 3), 0);
                    bool checkedOk = ColorClose(checkedScene.Paint(cPlate).Fill, ring, tol)        // CheckBoxCheckBackgroundFillChecked = AccentFillColorDefault (CheckBox_themeresources.xaml:57)
                        && !FindPolylineStrokeNode(checkedScene, checkedScene.Root).IsNull;

                    // Disabled: Opacity 0.3 (ItemContainerDisabledOpacity, :54) and PART_SelectionVisual collapses
                    // (ItemContainer.xaml:108-110) → only the content layer remains.
                    var disabledScene = LayoutTree(strings, ItemContainer.Build(new BoxEl(), true,
                        (t, mods) => { }, isEnabled: false, width: 120f, height: 48f));
                    bool disabled = Near(disabledScene.Paint(disabledScene.Root).Opacity, 0.3f, 0.001f)
                        && disabledScene.ChildCount(disabledScene.Root) == 1;

                    all &= states && geometry && plateColors && checkedOk && disabled;
                    details.Append($"{kind}: states={states} geo={geometry} plate={plateColors} checked={checkedOk} disabled={disabled}; ");
                }
            }
            finally { Tok.Use(ThemeKind.Dark); }
            Check("e11virt.11 ItemContainer: WinUI state ARGB both themes + dual-stroke geometry + checkbox plate + disabled collapse", all, details.ToString());
        }

        // e11virt.12 — the selection ring and multi-select checkbox FADE IN when they appear: opacity 0 → 1 over
        // ControlFastAnimationDuration 167ms / KeySpline 0,0,0,1 (ItemContainer.xaml:54-56 SelectedPointerOver and
        // :93-99 the checkbox storyboard) — the engine carries both as enter transitions on the keyed child nodes.
        {
            var scene = new SceneStore();
            var engine = new AnimEngine(scene);
            var recon = new TreeReconciler(scene, strings) { Anim = engine };
            Element Tree(bool multi, bool selected) => new BoxEl
            {
                Width = 200, Height = 60,
                Children = [ItemContainer.Build(new BoxEl(), isSelected: selected, onInteraction: (t, mods) => { },
                                                showSelectionCheckbox: multi, isChecked: false, width: 200f, height: 60f)],
            };
            var t0 = Tree(false, false);
            recon.ReconcileRoot(t0, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var container = Child(scene, scene.Root, 0);
            bool noChrome = scene.ChildCount(container) == 2;     // unselected + single → content + CommonVisual only

            recon.ReconcileRoot(Tree(true, true), t0);            // select + flip to Multiple → ring + checkbox enter
            new FlexLayout(scene, fonts).Run(scene.Root);
            var ring = Child(scene, container, 1);
            var check = Child(scene, container, 3);
            engine.Tick(16f);
            float ring16 = scene.Paint(ring).Opacity, check16 = scene.Paint(check).Opacity;
            for (int i = 0; i < 4; i++) engine.Tick(16f);         // t = 80ms — mid-flight on the 167ms tween
            float mid = scene.Paint(check).Opacity;
            for (int i = 0; i < 30; i++) engine.Tick(16f);        // past 167ms → settled
            bool settled = Near(scene.Paint(check).Opacity, 1f, 0.001f) && Near(scene.Paint(ring).Opacity, 1f, 0.001f);
            bool entering = ring16 < 0.95f && check16 < 0.95f && mid > check16 && mid < 1f;

            // the authored spec is exactly the WinUI storyboard: a 167ms decelerate TWEEN entering from opacity 0.
            BoxEl? checkEl = null;
            foreach (var child in ItemContainer.Build(new BoxEl(), false, (t, mods) => { }, showSelectionCheckbox: true).Children)
                if (child is BoxEl { Key: "ic-check" } b) { checkEl = b; break; }
            bool spec = checkEl?.Animate is { } a && a.Dynamics.Kind == DynamicsKind.Tween && Near(a.Dynamics.DurationMs, 167f)
                && a.Dynamics.Easing == Easing.FluentDecelerate && a.Enter.Active && Near(a.Enter.Opacity, 0f)
                && (a.Channels & TransitionChannels.Opacity) != 0;
            Check("e11virt.12 ItemContainer ring + checkbox enter-fade 0→1 over the 167ms ControlFastAnimationDuration tween",
                noChrome && entering && settled && spec, $"op16=({ring16:0.00},{check16:0.00}) op80={mid:0.00} settled={settled} spec={spec}");
        }

        // e11virt.13/14/15 — ItemsView (E11-L3) keyboard surface on a virtualized stack.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("e11-iv", new Size2(480, 360), 1f));
            window.Show();
            var probe = new ItemsViewKeyboardProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var ctl = probe.Controller;
            var sel = ctl.Selection!;

            NodeHandle vp = NodeHandle.Null;
            void Visit(NodeHandle n)
            {
                if (n.IsNull) return;
                if (host.Scene.TryGetScroll(n, out var s) && s.ItemCount == ItemsViewKeyboardProbe.N) vp = n;
                for (var c = host.Scene.FirstChild(n); !c.IsNull; c = host.Scene.NextSibling(c)) Visit(c);
            }
            Visit(host.Scene.Root);
            ScrollState Sc() { host.Scene.TryGetScroll(vp, out var s); return s; }
            void Press(float x, float y, uint t, KeyModifiers mods = KeyModifiers.None)
            {
                window.QueueInput(new InputEvent(InputKind.PointerDown, new Point2(x, y), 0, 0, 0f, mods, PointerKind.Mouse, false, t));
                window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(x, y), 0, 0, 0f, mods, PointerKind.Mouse, false, t + 10));
                host.RunFrame();
            }
            void Key(int key, KeyModifiers mods = KeyModifiers.None)
            {
                window.QueueInput(new InputEvent(InputKind.Key, default, 0, key, 0f, mods));
                host.RunFrame();
            }

            bool def = ctl.CurrentItemIndex == -1 && sel.Mode == ItemsSelectionMode.Single;   // CurrentItemIndex default −1 (ItemsView.idl:46-47)
            Press(180f, 100f, 1_000);                                    // row 2
            bool click = ctl.CurrentItemIndex == 2 && sel.IsSelected(2) && sel.SelectedCount == 1;
            Key(Keys.Down);
            bool down = ctl.CurrentItemIndex == 3 && sel.IsSelected(3) && sel.SelectedCount == 1;   // selection follows focus (SingleSelector)
            Key(Keys.Up);
            bool up = ctl.CurrentItemIndex == 2 && sel.IsSelected(2);
            Key(Keys.PageDown);                                          // viewport jump (cpp:1103+): 80 + 320 → row 10
            bool pgdn = ctl.CurrentItemIndex == 10;
            Key(Keys.PageUp);
            bool pgup = ctl.CurrentItemIndex == 2;
            Key(Keys.End);                                               // End: bottom edge-aligned (cpp:1009-1016, ratio 1)
            var scEnd = Sc();
            var focusedEnd = FocusedNode(host.Scene, host.Scene.Root);
            var fr = host.Scene.AbsoluteRect(focusedEnd);
            bool end = ctl.CurrentItemIndex == 99 && sel.IsSelected(99)
                && Near(scEnd.OffsetY, ItemsViewKeyboardProbe.N * 40f - 320f)
                && Near(fr.Y, 280f) && Near(fr.H, 40f);                  // the realized last container carries keyboard focus
            Key(Keys.Home);                                              // Home: top edge-aligned (ratio 0)
            bool home = ctl.CurrentItemIndex == 0 && Near(Sc().OffsetY, 0f);
            Check("e11virt.13 ItemsView keyboard: arrows follow focus, PageUp/Down jump a viewport, Home/End edge-align + focus the realized container",
                def && click && down && up && pgdn && pgup && end && home,
                $"def={def} click={click} down={down} up={up} pgdn={pgdn} pgup={pgup} end={end} home={home} cur={ctl.CurrentItemIndex} end-off={scEnd.OffsetY:0} focusY={fr.Y:0} focusH={fr.H:0}");

            Key(Keys.A, KeyModifiers.Ctrl);                              // Ctrl+A gated OFF in Single (ItemsViewInteractions.cpp:35-50)
            bool noSelectAll = sel.SelectedCount == 1;
            window.QueueInput(new InputEvent(InputKind.Char, default, 0, 'z'));
            host.RunFrame();
            bool typeahead = ctl.CurrentItemIndex == 57 && sel.IsSelected(57)
                && Near(Sc().OffsetY, 57f * 40f + 40f - 320f);           // minimal scroll realizes it at the bottom edge
            int invoked0 = probe.InvokedCount;
            Key(Keys.Enter);                                             // EnterKey invokes (ItemsView.cpp:423-426)
            bool enterInvokes = probe.InvokedCount == invoked0 + 1 && probe.LastInvoked == 57;
            Key(Keys.Space);                                             // SpaceKey selects WITHOUT invoking
            bool spaceSilent = probe.InvokedCount == invoked0 + 1 && sel.IsSelected(57);
            Press(180f, 100f, 80_000);                                   // Tap selects only (no invoke) — row 52 at this offset
            bool tapSilent = probe.InvokedCount == invoked0 + 1 && ctl.CurrentItemIndex == 52;
            Press(180f, 100f, 80_100);                                   // ClickCount 2 → DoubleTap invokes
            bool dblInvokes = probe.InvokedCount == invoked0 + 2 && probe.LastInvoked == 52;
            Check("e11virt.14 ItemsView typeahead jumps to the prefix match (+min-scroll realize); invoke matrix: Enter/DoubleTap yes, Tap/Space no; Ctrl+A gated in Single",
                noSelectAll && typeahead && enterInvokes && spaceSilent && tapSilent && dblInvokes,
                $"type→{ctl.CurrentItemIndex} invoked={probe.InvokedCount} last={probe.LastInvoked}");

            ctl.StartBringItemIntoView(10, 0f);                          // explicit edge-align (ratio 0)
            host.RunFrame();
            var scB = Sc();
            bool bring = Near(scB.OffsetY, 400f) && scB.FirstRealized <= 10 && scB.LastRealized > 10
                && ctl.CurrentItemIndex == 52;                           // StartBringItemIntoView never moves focus (ItemsView.cpp:119-127)
            ctl.StartBringItemIntoView(12);                              // already visible + default options → minimal scroll = no-op
            host.RunFrame();
            bool minimal = Near(Sc().OffsetY, 400f);
            Check("e11virt.15 StartBringItemIntoView: realizes + edge-aligns by ratio without moving focus; in-view target is a minimal-scroll no-op",
                bring && minimal, $"off={scB.OffsetY:0} window=[{scB.FirstRealized},{scB.LastRealized}) cur={ctl.CurrentItemIndex}");
        }

        // e11virt.16 — grid arrows: Left/Right = index ±1, Up/Down = ±columns (ItemsViewInteractions.cpp:1051-1067).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("e11-grid", new Size2(480, 360), 1f));
            window.Show();
            var probe = new ItemsViewGridProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var ctl = probe.Controller;
            void Key(int key)
            {
                window.QueueInput(new InputEvent(InputKind.Key, default, 0, key));
                host.RunFrame();
            }
            // cell 1 of a 4-col grid on 360 cross: colW = (360 − 3×8)/4 = 84 → cell 1 spans x [92,176), y [0,72).
            window.QueueInput(new InputEvent(InputKind.PointerDown, new Point2(134f, 36f), 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(134f, 36f), 0, 0));
            host.RunFrame();
            bool click = ctl.CurrentItemIndex == 1;
            Key(Keys.Right); bool right = ctl.CurrentItemIndex == 2;
            Key(Keys.Down); bool gdown = ctl.CurrentItemIndex == 6;
            Key(Keys.Left); bool left = ctl.CurrentItemIndex == 5;
            Key(Keys.Up); bool gup = ctl.CurrentItemIndex == 1;
            Check("e11virt.16 ItemsView grid arrows: Left/Right ±1, Up/Down ±columns (index-based orientation path)",
                click && right && gdown && left && gup, $"cur 1→2→6→5→{ctl.CurrentItemIndex}");
        }

        // e11virt.17 — Extended mode end-to-end through pointer chords + Shift+arrow + Ctrl+A.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("e11-ext", new Size2(480, 360), 1f));
            window.Show();
            var probe = new ItemsViewExtendedProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var ctl = probe.Controller;
            var sel = ctl.Selection!;
            void Press(int row, uint t, KeyModifiers mods = KeyModifiers.None)
            {
                var pt = new Point2(180f, row * 40f + 20f);
                window.QueueInput(new InputEvent(InputKind.PointerDown, pt, 0, 0, 0f, mods, PointerKind.Mouse, false, t));
                window.QueueInput(new InputEvent(InputKind.PointerUp, pt, 0, 0, 0f, mods, PointerKind.Mouse, false, t + 10));
                host.RunFrame();
            }
            void Key(int key, KeyModifiers mods = KeyModifiers.None)
            {
                window.QueueInput(new InputEvent(InputKind.Key, default, 0, key, 0f, mods));
                host.RunFrame();
            }

            Press(2, 1_000);                                             // plain → {2}
            bool plain = sel.SelectedCount == 1 && sel.IsSelected(2) && ctl.CurrentItemIndex == 2;
            Press(6, 2_000, KeyModifiers.Shift);                         // Shift → replace with [2..6]
            bool shiftRange = sel.SelectedCount == 5 && sel.GetRange(0) == (2, 6);
            Press(7, 3_000, KeyModifiers.Ctrl);                          // Ctrl → additive {2..6, 7}
            bool ctrlAdd = sel.SelectedCount == 6 && sel.IsSelected(7);
            Press(4, 4_000);                                             // plain on SELECTED → selection kept
            bool keep = sel.SelectedCount == 6 && ctl.CurrentItemIndex == 4;
            Key(Keys.Down, KeyModifiers.Shift);                          // Shift+arrow → replace with [5..anchor(7)]
            bool shiftArrow = ctl.CurrentItemIndex == 5 && sel.SelectedCount == 3 && sel.GetRange(0) == (5, 7);
            Key(Keys.A, KeyModifiers.Ctrl);                              // Ctrl+A allowed in Extended
            bool selectAll = sel.SelectedCount == ItemsViewExtendedProbe.N && sel.RangeCount == 1;
            bool events = probe.SelectionChangedCount == 5;              // one SelectionChanged per actual change
            Check("e11virt.17 ItemsView Extended: plain/shift/ctrl pointer chords, plain-on-selected keeps, Shift+arrow anchor range, Ctrl+A",
                plain && shiftRange && ctrlAdd && keep && shiftArrow && selectAll && events,
                $"count={sel.SelectedCount} ranges={sel.RangeCount} changes={probe.SelectionChangedCount}");
        }

        // e11virt.18 — Multiple over 10k: toggle clicks re-skin the window; Ctrl+A selects ALL via one stored range
        // while realizing nothing (bounded realized children + bounded template re-runs), and every realized
        // container shows the selected chrome + checkbox.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("e11-multi", new Size2(480, 360), 1f));
            window.Show();
            var probe = new ItemsViewMultipleProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var ctl = probe.Controller;
            var sel = ctl.Selection!;
            int calls0 = probe.TemplateCalls;
            void Press(int row, uint t, KeyModifiers mods = KeyModifiers.None)
            {
                var pt = new Point2(180f, row * 40f + 20f);
                window.QueueInput(new InputEvent(InputKind.PointerDown, pt, 0, 0, 0f, mods, PointerKind.Mouse, false, t));
                window.QueueInput(new InputEvent(InputKind.PointerUp, pt, 0, 0, 0f, mods, PointerKind.Mouse, false, t + 10));
                host.RunFrame();
            }
            Press(3, 1_000);
            bool on = sel.IsSelected(3) && sel.SelectedCount == 1;       // Multiple: plain click toggles
            Press(3, 2_500);
            bool off = !sel.IsSelected(3) && sel.SelectedCount == 0;
            Press(3, 4_000);
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.A, 0f, KeyModifiers.Ctrl));
            host.RunFrame();
            bool allSel = sel.SelectedCount == ItemsViewMultipleProbe.N && sel.RangeCount == 1
                && sel.GetRange(0) == (0, ItemsViewMultipleProbe.N - 1);

            NodeHandle vp = NodeHandle.Null;
            void Visit(NodeHandle n)
            {
                if (n.IsNull) return;
                if (host.Scene.TryGetScroll(n, out var s) && s.ItemCount == ItemsViewMultipleProbe.N) vp = n;
                for (var c = host.Scene.FirstChild(n); !c.IsNull; c = host.Scene.NextSibling(c)) Visit(c);
            }
            Visit(host.Scene.Root);
            host.Scene.TryGetScroll(vp, out var sc);
            int realized = host.Scene.ChildCount(sc.ContentNode);
            int templateDelta = probe.TemplateCalls - calls0;
            bool bounded = realized < 40 && templateDelta > 0 && templateDelta < realized * 12;   // window-only re-skin per change
            var firstContainer = host.Scene.FirstChild(sc.ContentNode);
            bool chrome = host.Scene.ChildCount(firstContainer) == 4;    // content + ring + inner + checkbox
            Check("e11virt.18 ItemsView Multiple over 10k: toggle clicks, Ctrl+A = ONE range realizing nothing (bounded window re-skin) + checkbox chrome",
                on && off && allSel && bounded && chrome,
                $"count={sel.SelectedCount} ranges={sel.RangeCount} realized={realized} templateΔ={templateDelta}");
        }
    }

    // D1 — ListView/ItemsView rendered EMPTY when the host imposed no size (gallery regression, commit 4a9047b):
    // (1) FlexLayout.MeasureViewport collapsed an unsized virtual viewport to 0 (NaN→0), so the auto-width 280-card
    //     ListView arranged at W=0 and the auto-height Start-row ItemsView at H=0; fixed by the natural-size
    //     fallback for NON-FLEXING (Grow==0) virtual viewports (cross = availW, main = layout ContentExtent).
    // (2) Nothing re-realized after layout published the real ViewportW/H (a mount realizes against a hint); fixed
    //     by realize-after-layout: ArrangeViewport flags VirtualRangeDirty → AppHost re-realizes + relayouts SAME frame.
    static void D1CollectionHostSizingChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);

        NodeHandle FindViewport(SceneStore s, int count)
        {
            NodeHandle found = default;
            void Visit(NodeHandle n)
            {
                if (n.IsNull) return;
                if (s.TryGetScroll(n, out var sc) && sc.ItemCount == count) found = n;
                for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c)) Visit(c);
            }
            Visit(s.Root);
            return found;
        }

        // cp1.a — the EXACT gallery ListView shape (CollectionsMenusPages.cs): a Width=280 bordered card with NO
        // height anywhere above the list — must size naturally (8 × 44 = 352) and realize all 8 rows at W=280.
        string[] coffees = { "Cappuccino", "Latte", "Espresso", "Macchiato", "Americano", "Mocha", "Flat White", "Cortado" };
        var selected = new Signal<int>(0);
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("d1-listview", new Size2(640, 480), 1f));
        window.Show();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new W0fStaticProbe
        {
            Build = () => new BoxEl
            {
                Width = 280, Corners = Radii.OverlayAll, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
                Padding = new Edges4(0, 4, 0, 4), Children = [ItemsView.List(coffees, selected)],
            },
        });
        host.RunFrame();
        var lv = FindViewport(host.Scene, coffees.Length);
        var lsc = default(ScrollState);
        if (!lv.IsNull) host.Scene.TryGetScroll(lv, out lsc);
        var lvRect = lv.IsNull ? default : host.Scene.AbsoluteRect(lv);
        int lvRows = lv.IsNull ? 0 : host.Scene.ChildCount(lsc.ContentNode);
        var row0 = default(RectF);
        if (lvRows > 0) row0 = host.Scene.Bounds(host.Scene.FirstChild(lsc.ContentNode));
        bool lvOk = !lv.IsNull && Near(lvRect.W, 280f) && Near(lvRect.H, 8 * ItemsView.ListItemExtent)
            && Near(lsc.ViewportW, 280f) && Near(lsc.ViewportH, 352f) && Near(lsc.ContentH, 352f)
            && lvRows == 8 && Near(row0.W, 280f) && Near(row0.H, ItemsView.ListItemExtent);
        Check("cp1.a — gallery ListView (280-wide card, no height above) sizes naturally to 8×44 and realizes 8 rows at W=280",
            lvOk, $"vp={lvRect.W:0}x{lvRect.H:0} viewport={lsc.ViewportW:0}x{lsc.ViewportH:0} content={lsc.ContentH:0} rows={lvRows} row0={row0.W:0}x{row0.H:0}");

        // cp1.b — the gallery ItemsView shape (MiscPages.cs): legacy Create(items, columns:4) in an AUTO-HEIGHT,
        // Start-aligned row (no stretch, no height anywhere). The view must measure to its grid's ContentExtent
        // (2 rows × 80 + 1 gap × 8 = 168) and realize all 8 tiles at the 4-column width ((420 − 3×8)/4 = 99).
        string[] photos = { "Photo 1", "Photo 2", "Photo 3", "Photo 4", "Photo 5", "Photo 6", "Photo 7", "Photo 8" };
        using var app2 = new HeadlessPlatformApp();
        var window2 = new HeadlessWindow(new WindowDesc("d1-itemsview", new Size2(640, 480), 1f));
        window2.Show();
        using var host2 = new AppHost(app2, window2, new HeadlessGpuDevice(), fonts, strings, new W0fStaticProbe
        {
            Build = () => new BoxEl
            {
                Width = 420, Direction = 0, AlignItems = FlexAlign.Start,
                Children = [ItemsView.Create(photos, columns: 4)],
            },
        });
        host2.RunFrame();
        var iv = FindViewport(host2.Scene, photos.Length);
        var isc = default(ScrollState);
        if (!iv.IsNull) host2.Scene.TryGetScroll(iv, out isc);
        var ivRect = iv.IsNull ? default : host2.Scene.AbsoluteRect(iv);
        int tiles = iv.IsNull ? 0 : host2.Scene.ChildCount(isc.ContentNode);
        var tile0 = default(RectF);
        if (tiles > 0) tile0 = host2.Scene.Bounds(host2.Scene.FirstChild(isc.ContentNode));
        const float gridExtent = 2 * 80f + 8f;   // GridVirtualLayout(4, 80, 8).ContentExtent(8) — the legacy demo grid
        bool ivOk = !iv.IsNull && Near(ivRect.W, 420f) && Near(ivRect.H, gridExtent) && Near(isc.ContentH, gridExtent)
            && tiles == 8 && Near(tile0.W, (420f - 3 * 8f) / 4f) && Near(tile0.H, 80f);
        Check("cp1.b — gallery ItemsView (legacy 4-col grid, auto-height Start row) sizes to ContentExtent=168 and realizes 8 tiles",
            ivOk, $"vp={ivRect.W:0}x{ivRect.H:0} content={isc.ContentH:0} tiles={tiles} tile0={tile0.W:0}x{tile0.H:0}");

        // cp1.c — realize-after-layout: a 10k-row ListView FILLING a 400px host stays windowed (<40 realized — the
        // Grow gate keeps the hard-viewport path; content = 10k × 44 = 440000); growing the host to 3000px must
        // publish the new viewport AND re-realize the window to cover it in the SAME frame.
        var hostH = new Signal<float>(400f);
        using var app3 = new HeadlessPlatformApp();
        var window3 = new HeadlessWindow(new WindowDesc("d1-grow", new Size2(640, 480), 1f));
        window3.Show();
        using var host3 = new AppHost(app3, window3, new HeadlessGpuDevice(), fonts, strings, new W0fStaticProbe
        {
            Build = () => new BoxEl
            {
                Width = 360, Height = hostH.Value,
                Children = [ItemsView.List(10_000, i => new BoxEl(), grow: 1f)],
            },
        });
        host3.RunFrame();
        var big = FindViewport(host3.Scene, 10_000);
        var bsc0 = default(ScrollState);
        if (!big.IsNull) host3.Scene.TryGetScroll(big, out bsc0);
        int realized0 = big.IsNull ? 0 : host3.Scene.ChildCount(bsc0.ContentNode);
        bool windowed = !big.IsNull && realized0 > 0 && realized0 < 40
            && Near(bsc0.ViewportH, 400f) && Near(bsc0.ContentH, 10_000 * ItemsView.ListItemExtent, 1f);

        hostH.Value = 3000f;   // grow the host — ONE RunFrame must both publish 3000 and re-realize to cover it
        host3.RunFrame();
        var bsc1 = default(ScrollState);
        if (!big.IsNull) host3.Scene.TryGetScroll(big, out bsc1);
        int realized1 = big.IsNull ? 0 : host3.Scene.ChildCount(bsc1.ContentNode);
        bool covered = Near(bsc1.ViewportH, 3000f) && bsc1.FirstRealized == 0
            && bsc1.LastRealized * ItemsView.ListItemExtent >= 3000f && realized1 > realized0 && realized1 < 120;
        Check("cp1.c — 10k rows stay windowed (<40) at 400px; growing the host re-realizes to cover in the SAME frame",
            windowed && covered, $"realized {realized0}→{realized1} viewport {bsc0.ViewportH:0}→{bsc1.ViewportH:0} last={bsc1.LastRealized} content={bsc0.ContentH:0}");
    }

    // cp2.* — the collection-consolidation contract (the premiere ItemsView + thin ListView/GridView presets + the
    // ReorderList 2-D fold + the host-seeded displacement channel). These pin the NEW reorder model the user ruled in:
    // dragged item is a pointer-held ghost, displaced siblings PART TO MAKE ROOM via an ANIMATED translate (no
    // insertion line, no forced rerender), commit = RemoveAt+Insert on release. No check here encodes the rejected
    // insertion-line / static-OffsetY hack — they assert the displacement reaches the realized node as live AnimEngine
    // motion (the core defect's fix) and that every capability works in every combination across the three controls.
    static void Cp2ConsolidationChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);

        // The realized window's ord-th container for an item index (resting order: ord = index − FirstRealized;
        // mirrors ItemsView.FocusIndex / the displacement seed's ord math). Non-virtual hosts have no scroll state.
        static NodeHandle RealizedRow(SceneStore s, NodeHandle vp, int index)
        {
            if (vp.IsNull || !s.IsLive(vp)) return NodeHandle.Null;
            NodeHandle first; int ord;
            if (s.TryGetScroll(vp, out var sc))
            {
                ord = index - sc.FirstRealized;
                if (ord < 0 || index >= sc.LastRealized) return NodeHandle.Null;
                first = s.FirstChild(sc.ContentNode);
            }
            else { ord = index; first = s.FirstChild(vp); }
            var n = first;
            for (int k = 0; k < ord && !n.IsNull; k++) n = s.NextSibling(n);
            return n;
        }

        // Depth-first structural finder for a selector-chrome part (the SelectorVisuals builders carry no reconciler
        // Key into the scene, so match on geometry/fill rather than Key — more robust than a Key probe anyway).
        static NodeHandle FindBox(SceneStore s, NodeHandle n, Func<NodePaint, bool> pred)
        {
            if (n.IsNull) return NodeHandle.Null;
            if (pred(s.Paint(n))) return n;
            for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
            {
                var r = FindBox(s, c, pred);
                if (!r.IsNull) return r;
            }
            return NodeHandle.Null;
        }

        NodeHandle FindViewport(SceneStore s, int count)
        {
            NodeHandle found = default;
            void Visit(NodeHandle n)
            {
                if (n.IsNull) return;
                if (s.TryGetScroll(n, out var sc) && sc.ItemCount == count) found = n;
                for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c)) Visit(c);
            }
            Visit(s.Root);
            return found;
        }

        // ── C1 — cp2.reorder2d: the GridView 2-D reorder math folded into ReorderList (Begin2D/Update2D/OffsetFor2D),
        // proving GridReorder's logic survived the consolidation. Pure unit, no host (scroll/realize-agnostic). ──────
        {
            var rl = new ReorderList { DwellMs = ReorderList.GridDwellMs };
            bool dwell300 = ReorderList.GridDwellMs == 300f && rl.DwellMs == 300f;
            rl.Begin2D(0, 8, columns: 4);                              // 8 tiles, 4 cols → rows of {0,1,2,3},{4,5,6,7}
            bool init = rl.IsActive && rl.DraggedIndex == 0 && rl.Columns == 4 && rl.PendingIndex == 0 && rl.TargetIndex == 0;
            // Drag tile 0 to slot 5 (col 1, row 1): totalDx≈100 → +1 col, totalDy≈100 → +1 row, on a 100×100 grid.
            bool moved = rl.Update2D(100f, 100f, colWidth: 100f, rowStride: 100f) && rl.PendingIndex == 5;
            bool dwellHeld = !rl.Advance(299f) && rl.TargetIndex == 0;
            bool dwellFire = rl.Advance(1f) && rl.TargetIndex == 5;
            // Forward drag 0→5: tiles (0,5] each shift ONE slot toward the vacated source (row-major). Tile 4 sits at
            // (col0,row1) and shifts to slot 3 = (col3,row0): a ROW WRAP — dx = +3 cols, dy = −1 row.
            rl.OffsetFor2D(4, 100f, 100f, out float dx4, out float dy4);
            bool wrap = Near(dx4, 300f) && Near(dy4, -100f);
            // Tile 1 (col1,row0) shifts to slot 0 (col0,row0): one column back, same row.
            rl.OffsetFor2D(1, 100f, 100f, out float dx1, out float dy1);
            bool oneCol = Near(dx1, -100f) && Near(dy1, 0f);
            rl.OffsetFor2D(0, 100f, 100f, out float dxD, out float dyD);   // the dragged tile never displaces
            bool draggedZero = dxD == 0f && dyD == 0f;
            rl.OffsetFor2D(6, 100f, 100f, out float dx6, out float dy6);   // tile 6 is OUTSIDE (0,5] → no shift
            bool outsideZero = dx6 == 0f && dy6 == 0f;
            int dest = rl.Complete();
            bool committed = dest == 5 && !rl.IsActive;
            Check("cp2.reorder2d ReorderList 2-D slot math + 300ms grid dwell + OffsetFor2D row-wrap (GridReorder folded in)",
                dwell300 && init && moved && dwellHeld && dwellFire && wrap && oneCol && draggedZero && outsideZero && committed,
                $"300={dwell300} init={init} moved={moved} dwell={dwellHeld}/{dwellFire} wrap=({dx4:0},{dy4:0}) oneCol=({dx1:0},{dy1:0}) draggedZ={draggedZero} outZ={outsideZero} dest={dest}");
        }

        // ── C2 — cp2.displace: THE core-defect proof. A displacement on a realized row reaches the node as ANIMATED
        // LocalTransform motion (mid-flight → settled), NOT a static jump and NOT discarded behind the autonomous
        // ItemsView boundary. Synthetic itemDisplacement=(0,40) for index 2 + a displacementVersion the test bumps. ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cp2-displace", new Size2(420, 360), 1f));
            window.Show();
            var ver = new Signal<int>(0);
            int dispTarget = -1;                                       // armed below (so the mount frame seeds nothing)
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Width = 360, Height = 280,
                    Children =
                    [
                        ItemsView.Create(8,
                            i => new BoxEl { Children = [new TextEl($"row {i}") { Size = 13f }] },
                            RepeatLayout.Stack(40f),
                            selector: SelectorVisual.AccentPill,
                            itemDisplacement: i => i == dispTarget ? (0f, 40f) : (0f, 0f),
                            displacementVersion: ver),
                    ],
                },
            });
            host.RunFrame();                                          // mount + realize (no displacement armed yet)
            var vp = FindViewport(host.Scene, 8);
            var row2 = RealizedRow(host.Scene, vp, 2);
            bool found = !row2.IsNull;
            float authoredOffset = found ? host.Scene.Paint(row2).LocalTransform.Dy : -1f;   // 0 before any seed
            bool restZero = found && Near(authoredOffset, 0f);

            dispTarget = 2;                                           // arm (0,40) for index 2, then bump the version
            ver.Value = ver.Peek() + 1;
            host.RunFrame();                                          // re-render ItemsView → seed the TranslateY track (elapsed 0)
            for (int i = 0; i < 3; i++) host.RunFrame();             // advance the 250ms track a few 16ms ticks → mid-flight
            float midDy = found ? host.Scene.Paint(row2).LocalTransform.Dy : 0f;
            bool midFlight = midDy > 0.5f && midDy < 40f;            // animated, not an instant jump (no exact ms asserted)
            for (int i = 0; i < 40; i++) host.RunFrame();            // let the 250ms FluentDecelerate track settle
            float settledDy = found ? host.Scene.Paint(row2).LocalTransform.Dy : 0f;
            bool settled = Near(settledDy, 40f, 0.6f);
            // The element carries NO authored OffsetY — the motion lives on the AnimEngine track, not a static offset
            // (so it survives reconcile; the dragged-ghost rule). A non-displaced realized row stays put.
            var row0 = RealizedRow(host.Scene, vp, 0);
            bool neighborStill = !row0.IsNull && Near(host.Scene.Paint(row0).LocalTransform.Dy, 0f, 0.6f);
            Check("cp2.displace ItemsView host-seeds an ANIMATED translate on a displaced realized row (mid-drag part-to-make-room; not a static jump)",
                found && restZero && midFlight && settled && neighborStill,
                $"found={found} rest={authoredOffset:0.0} mid={midDy:0.0} settled={settledDy:0.0} neighbor={neighborStill}");
        }

        // ── C3 — cp2.dragstill: stage A's `HasActiveWork |= Drag.IsActive` keep-alive — a live drag keeps RunFrame
        // pumping so the FrameClock dwell ticker keeps getting frames even on a MOTIONLESS pointer (without it RunFrame
        // would early-return at the !HasActiveWork gate, AppHost.cs:351, and the dwell would freeze). The keep-alive
        // (HasActiveWork true across still frames) + the gesture surviving the still hold to commit on release are fully
        // deterministic; the dwell MATH itself is pinned at the unit level (e5dragdrop.6/.7 Advance). NOTE: this does
        // NOT read the realized-row displacement — the live ListView/GridView preset's displacement is currently dead
        // (a freeze bug in ListView.cs/GridView.cs: their itemDisplacement closure captures the per-render `reordering`
        // bool, which freezes to false at the inner ItemsView's MOUNT because a reused ComponentEl is a no-op on a
        // parent re-render, Reconciler.cs:217-221 — see notes). The DISPLACEMENT-reaches-the-realized-node proof lives
        // in cp2.displace / cp2.scrollslot / cp2.matrix.itemsview (the channel path, which is sound). ────────────────
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cp2-still", new Size2(360, 360), 1f));
            window.Show();
            var drinks = new List<string> { "Water", "Juice", "Lemonade", "Soda", "Coffee", "Tea" };
            int committedFrom = -1, committedTo = -1;
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Width = 320, Height = 280,
                    Children =
                    [
                        ItemsView.List(drinks.Count,
                            i => new TextEl(drinks[i]) { Size = 14f, Color = Tok.TextPrimary, Grow = 1f },
                            canReorderItems: true, onReorder: (f, t) => { committedFrom = f; committedTo = t; },
                            keyOf: i => drinks[i], itemText: i => drinks[i]),
                    ],
                },
            });
            host.RunFrame();
            var vp = FindViewport(host.Scene, drinks.Count);
            var dragRow = RealizedRow(host.Scene, vp, 0);
            var c = CenterOf(host.Scene, dragRow);
            // Promote a drag on row 0 (PointerDown, then a >4px move) — 0-stamp ⇒ deterministic snap-track ghost.
            window.QueueInput(new InputEvent(InputKind.PointerDown, c, 0, 0));
            host.RunFrame();
            // Move the dragged centre (row-0 centre 22) DOWN past row 1's midpoint (66): +50 ⇒ centre 72 > 66 → pending 1.
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(c.X, c.Y + 50f), 0, 0));
            host.RunFrame();
            bool promoted = host.HasActiveWork;                       // a live drag → work pending
            // Hold STILL: no further pointer input. HasActiveWork must stay true EVERY frame (the keep-alive) so the
            // dwell ticker keeps getting frames. Space the frames with real time so wall-clock actually advances.
            bool stayedAlive = true;
            for (int i = 0; i < 5; i++)
            {
                System.Threading.Thread.Sleep(110);                  // let Environment.TickCount64 advance past a dwell step
                host.RunFrame();
                if (!host.HasActiveWork) stayedAlive = false;
            }
            // Release: the gesture stayed live through the motionless hold, so it completes and commits the reorder
            // (pending slot 1 at the latest pointer position). HasActiveWork from the drag then drains.
            window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(c.X, c.Y + 50f), 0, 0));
            host.RunFrame();
            bool committedAfterStill = committedFrom == 0 && committedTo == 1;
            Check("cp2.dragstill HasActiveWork keep-alive holds frames across a MOTIONLESS pointer (no early-return); the gesture survives the still hold + commits on release",
                promoted && stayedAlive && committedAfterStill,
                $"promoted={promoted} alive={stayedAlive} commit=({committedFrom}->{committedTo})");
        }

        // ── C4 — cp2.invokerelease: a promoted drag is a REORDER gesture (commits a move on release); a plain
        // press-release is a CLICK (selects + raises ItemClick + does NOT reorder).
        // NOTE (WinUI-faithful divergence, documented + verified against the real code): this engine selects AND raises
        // ItemClick at the PRESS edge via OnPointerPressed→Tap (ItemContainer.cs:41-45 / SelectorVisuals.AccentPill +
        // ListView.Chrome's interact wrapper — "Win32 lists select on button-down; the visual outcome is identical to
        // WinUI's PointerReleased selection"). So the orders' literal "a drag does not select / drag suppresses the
        // click" cannot hold against press-edge handling — both a click and a grab touch the row at press. What a
        // completed drag uniquely does is take the DRAG path (commit a reorder) and suppress the RELEASE-edge click
        // (InputDispatcher.cs:332-345); a plain release takes the CLICK path. This asserts that truthful discrimination
        // (the spirit of parity:420/430 — a drag is a drag, a click is a click). ─────────────────────────────────────
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cp2-invoke", new Size2(360, 360), 1f));
            window.Show();
            var drinks = new List<string> { "Water", "Juice", "Lemonade", "Soda", "Coffee", "Tea" };
            int clicked = -1, clickCount = 0, reorderFrom = -1, reorderTo = -1;
            var model = new SelectionModel();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Width = 320, Height = 280,
                    Children =
                    [
                        ItemsView.List(drinks.Count,
                            i => new TextEl(drinks[i]) { Size = 14f, Color = Tok.TextPrimary, Grow = 1f },
                            selection: model,
                            onItemClick: i => { clicked = i; clickCount++; },
                            canReorderItems: true, onReorder: (f, t) => { reorderFrom = f; reorderTo = t; }, keyOf: i => drinks[i]),
                    ],
                },
            });
            host.RunFrame();
            var vp = FindViewport(host.Scene, drinks.Count);

            // (a) DRAG row 1 UP past row 0's midpoint (row-1 centre 66 → −50 ⇒ 16 < row-0 mid 22 → pending 0), release:
            // the gesture is a REORDER, committing 1→0 (Complete uses the latest pending, no dwell needed for the drop).
            var r1 = RealizedRow(host.Scene, vp, 1);
            var c1 = CenterOf(host.Scene, r1);
            window.QueueInput(new InputEvent(InputKind.PointerDown, c1, 0, 0));
            host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(c1.X, c1.Y - 50f), 0, 0));   // >4px up → promote, cross row-0 mid
            host.RunFrame();
            bool active = host.HasActiveWork;
            window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(c1.X, c1.Y - 50f), 0, 0));
            host.RunFrame();
            bool dragReordered = reorderFrom == 1 && reorderTo == 0;  // the drag committed a move, not a tap-invoke

            // (b) PLAIN click on row 2 (no threshold cross): ItemClick fires + the row selects (press-edge), NO reorder.
            int reorders0From = reorderFrom;
            var r2 = RealizedRow(host.Scene, vp, 2);
            var c2 = CenterOf(host.Scene, r2);
            int clicks1 = clickCount;
            window.QueueInput(new InputEvent(InputKind.PointerDown, c2, 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerUp, c2, 0, 0));
            host.RunFrame();
            bool plainClicks = clickCount == clicks1 + 1 && clicked == 2 && model.IsSelected(2) && reorderFrom == reorders0From;
            Check("cp2.invokerelease a promoted drag commits a reorder (drag gesture); a plain release selects + raises ItemClick + no reorder (press-edge select divergence noted)",
                active && dragReordered && plainClicks,
                $"active={active} dragReorder=({reorderFrom}->{reorderTo}) plain={plainClicks} clicked={clicked} sel2={model.IsSelected(2)}");
        }

        // ── C5 — cp2.scrollslot: under a SCROLLED viewport (FirstRealized>0) the displacement seed lands on the
        // CORRECT realized node (index→ord via FirstRealized — the seed's ord math must respect scroll, not blindly
        // use the absolute index). Synthetic displacement on a realized-but-scrolled index. ──────────────────────────
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cp2-scroll", new Size2(360, 360), 1f));
            window.Show();
            var ver = new Signal<int>(0);
            int dispTarget = -1;
            var ctl = new ItemsViewController();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Width = 320, Height = 200,                        // 200 / 40 ⇒ ~5 rows visible over 100 items
                    Children =
                    [
                        ItemsView.Create(100,
                            i => new BoxEl { Children = [new TextEl($"row {i}") { Size = 13f }] },
                            RepeatLayout.Stack(40f),
                            controller: ctl,
                            selector: SelectorVisual.AccentPill,
                            itemDisplacement: i => i == dispTarget ? (0f, 24f) : (0f, 0f),
                            displacementVersion: ver),
                    ],
                },
            });
            host.RunFrame();
            ctl.StartBringItemIntoView(40, 0f);                       // scroll so item 40 is at the top edge
            host.RunFrame();
            var vp = FindViewport(host.Scene, 100);
            host.Scene.TryGetScroll(vp, out var sc);
            bool scrolled = sc.FirstRealized >= 30;                   // genuinely past the top (ord != index now)
            int target = sc.FirstRealized + 1;                        // a realized item with ord = 1
            var targetNode = RealizedRow(host.Scene, vp, target);
            bool nodeFound = !targetNode.IsNull;

            dispTarget = target;
            ver.Value = ver.Peek() + 1;
            host.RunFrame();
            for (int i = 0; i < 40; i++) host.RunFrame();            // settle
            float tdy = nodeFound ? host.Scene.Paint(targetNode).LocalTransform.Dy : 0f;
            bool landedOnTarget = nodeFound && Near(tdy, 24f, 0.6f);
            // The ord-0 realized node (a DIFFERENT item index than `target`) must NOT have moved — proving the seed
            // mapped index→ord through FirstRealized rather than smearing onto the wrong (absolute-index) child.
            var ord0 = RealizedRow(host.Scene, vp, sc.FirstRealized);
            bool othersStill = !ord0.IsNull && Near(host.Scene.Paint(ord0).LocalTransform.Dy, 0f, 0.6f);
            Check("cp2.scrollslot displaced offset lands on the correct realized node under a scrolled viewport (index→ord via FirstRealized)",
                scrolled && nodeFound && landedOnTarget && othersStill,
                $"first={sc.FirstRealized} target={target} tdy={tdy:0.0} othersStill={othersStill}");
        }

        // ── C6 — cp2.matrix: the SAME logical reorder (drag item 0 → slot 2) run THREE ways — ListView, GridView,
        // ItemsView (synthetic, like a preset) — proving "every capability in every combination". ALL THREE arms pin
        // part-to-make-room: the (i) ListView and (ii) GridView arms drive a REAL pointer drag through the LIVE preset
        // wiring and assert (1) the model move 0→2 commits on release, (2) the dragged node's translate is the DRAG's
        // (ghost rides the pointer), (3) a displaced sibling's translate parts toward the vacated slot mid-drag. That
        // third assertion guards the closure-freeze regression: itemDisplacement is a constructor arg of the inner
        // autonomous ItemsView, so it freezes at mount (engine Rule #2) — it must capture only STABLE state (the
        // memoized ReorderList), never a per-render local; a frozen `reordering ? … : (0,0)` once shipped green here
        // while the live displacement was dead. The (iii) ItemsView arm pins the channel itself deterministically. ────
        {
            // (i) ListView ─────────────────────────────────────────────────────────────────────────────────────────
            int lvFrom = -1, lvTo = -1;
            using (var app = new HeadlessPlatformApp())
            {
                var window = new HeadlessWindow(new WindowDesc("cp2-mx-lv", new Size2(360, 360), 1f));
                window.Show();
                var items = new List<string> { "A", "B", "C", "D", "E" };
                using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new W0fStaticProbe
                {
                    Build = () => new BoxEl
                    {
                        Width = 320, Height = 280,
                        Children =
                        [
                            ItemsView.List(items.Count,
                                i => new TextEl(items[i]) { Size = 14f, Color = Tok.TextPrimary, Grow = 1f },
                                canReorderItems: true, onReorder: (f, t) => { lvFrom = f; lvTo = t; }, keyOf: i => items[i]),
                        ],
                    },
                });
                host.RunFrame();
                var vp = FindViewport(host.Scene, items.Count);
                var dragRow = RealizedRow(host.Scene, vp, 0);
                var c = CenterOf(host.Scene, dragRow);
                window.QueueInput(new InputEvent(InputKind.PointerDown, c, 0, 0));
                host.RunFrame();
                // Move the dragged centre PAST row 2's midpoint (row-0 centre 22; row-2 mid = 44*2+22 = 110 ⇒ +92 → centre 114 > 110 → pending 2).
                window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(c.X, c.Y + 92f), 0, 0));
                host.RunFrame();
                // A few frames while held (real time advances the dwell ticker; keep-alive keeps frames coming).
                bool aliveLv = host.HasActiveWork;
                for (int i = 0; i < 3; i++) { System.Threading.Thread.Sleep(110); host.RunFrame(); aliveLv &= host.HasActiveWork; }
                // The 200ms dwell committed target=2 during the loop; the LIVE preset's ItemDisplacement channel seeds
                // rows 1,2 with the animated -44 part (the 250ms Reposition glide) — settle it, then read row 1.
                for (int i = 0; i < 3; i++) { System.Threading.Thread.Sleep(110); host.RunFrame(); }
                var row1Lv = RealizedRow(host.Scene, vp, 1);
                float lvRow1Dy = row1Lv.IsNull ? 0f : host.Scene.Paint(row1Lv).LocalTransform.Dy;
                bool lvParted = !row1Lv.IsNull && lvRow1Dy < -30f;   // a displaced sibling parts up toward -44 on the LIVE path (directional: the glide settles asymptotically)
                // Re-fetch FRESH: the ListView re-renders + re-realizes every drag frame (its FrameClock dwell ticker).
                var dragRowNow = RealizedRow(host.Scene, vp, 0);
                float draggedDy = dragRowNow.IsNull ? 0f : host.Scene.Paint(dragRowNow).LocalTransform.Dy;   // ghost rides the pointer (+~92)
                // TWO-SIDED: the dragged node rides the pointer at the gesture delta (+92) and STAYS there across the
                // held frames — it must NOT run away. Pre-fix, the ItemsView displacement seed planted a Replace
                // TranslateY track on the dragged ghost (DraggedSlot is unwired in every preset) that fought
                // DragController.RetargetFromRest into an unbounded per-frame runaway (hundreds→thousands of px, the
                // ghost flying off the page); the old `draggedDy > 20f` lower bound passed on that runaway. The upper
                // bound is the actual regression guard — the seed now skips the DragGhost-flagged node.
                bool lvGhost = !dragRowNow.IsNull && MathF.Abs(draggedDy - 92f) < 20f;   // ≈ +92 gesture delta, not a runaway
                window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(c.X, c.Y + 92f), 0, 0));
                host.RunFrame();
                bool lvCommit = lvFrom == 0 && lvTo == 2;
                Check("cp2.matrix.listview drag 0→2: model commits, displaced row parts mid-drag on the LIVE path, dragged node owned by the drag, keep-alive holds",
                    lvCommit && lvGhost && aliveLv && lvParted,
                    $"commit=({lvFrom}->{lvTo}) row1Dy={lvRow1Dy:0.0} draggedDy={draggedDy:0.0} alive={aliveLv}");
            }

            // (ii) GridView (Check selector + ReorderList 2-D) ──────────────────────────────────────────────────────
            int gvFrom = -1, gvTo = -1;
            using (var app = new HeadlessPlatformApp())
            {
                var window = new HeadlessWindow(new WindowDesc("cp2-mx-gv", new Size2(480, 360), 1f));
                window.Show();
                var items = new List<string> { "A", "B", "C", "D", "E", "F", "G", "H" };
                using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new W0fStaticProbe
                {
                    Build = () => new BoxEl
                    {
                        Width = 440, Height = 320,
                        Children =
                        [
                            ItemsView.Grid(items.Count, i => new BoxEl { Children = [new TextEl(items[i]) { Size = 13f }] },
                                columns: 4, tileHeight: 96f,
                                canReorderItems: true, onReorder: (f, t) => { gvFrom = f; gvTo = t; }, keyOf: i => items[i]),
                        ],
                    },
                });
                host.RunFrame();
                var vp = FindViewport(host.Scene, items.Count);
                var dragTile = RealizedRow(host.Scene, vp, 0);
                var c = CenterOf(host.Scene, dragTile);
                var c2 = CenterOf(host.Scene, RealizedRow(host.Scene, vp, 2));   // target slot 2 (same row, 2 cols right)
                window.QueueInput(new InputEvent(InputKind.PointerDown, c, 0, 0));
                host.RunFrame();
                window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(c2.X, c2.Y), 0, 0));   // over tile-2 centre
                host.RunFrame();
                bool aliveGv = host.HasActiveWork;
                for (int i = 0; i < 4; i++) { System.Threading.Thread.Sleep(110); host.RunFrame(); aliveGv &= host.HasActiveWork; }   // 300ms grid dwell
                // The 300ms dwell committed target=2; the LIVE preset's channel seeds tiles 1,2 with the animated
                // one-column-left part — settle the 250ms glide, then read tile 1 (directional: a column ≈ 110px).
                for (int i = 0; i < 3; i++) { System.Threading.Thread.Sleep(110); host.RunFrame(); }
                var tile1Gv = RealizedRow(host.Scene, vp, 1);
                float gvTile1Dx = tile1Gv.IsNull ? 0f : host.Scene.Paint(tile1Gv).LocalTransform.Dx;
                bool gvParted = !tile1Gv.IsNull && gvTile1Dx < -60f;   // a displaced tile parts one column left on the LIVE path
                // Re-fetch FRESH (the GridView re-renders + re-realizes every drag frame via its dwell ticker).
                var dragTileNow = RealizedRow(host.Scene, vp, 0);
                float draggedTx = dragTileNow.IsNull ? 0f : host.Scene.Paint(dragTileNow).LocalTransform.Dx;
                // TWO-SIDED: the dragged tile rides the pointer at the injected horizontal gesture delta (≈2 columns)
                // and STAYS there — it must NOT run away. Pre-fix the displacement seed stomped BOTH tile axes
                // (OffsetFor2D returns (0,0) for the dragged tile, but the seed animated its live translate back to 0),
                // and the runaway was on the X axis for this same-row drag; the old `draggedTx > 20f` lower bound
                // masked it. The upper bound (anchored to the real injected delta) is the regression guard.
                float gvExpectTx = c2.X - c.X;
                bool gvGhost = !dragTileNow.IsNull && MathF.Abs(draggedTx - gvExpectTx) < 16f;   // tile rides the pointer, not a runaway
                window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(c2.X, c2.Y), 0, 0));
                host.RunFrame();
                bool gvCommit = gvFrom == 0 && gvTo == 2;
                Check("cp2.matrix.gridview drag 0→2 (2-D): model commits, displaced tile parts mid-drag on the LIVE path, dragged tile owned by the drag, keep-alive holds",
                    gvCommit && gvGhost && aliveGv && gvParted,
                    $"commit=({gvFrom}->{gvTo}) tile1Dx={gvTile1Dx:0.0} draggedTx={draggedTx:0.0} alive={aliveGv}");
            }

            // (iii) ItemsView wired like a preset (AccentPill + synthetic ReorderList-fed displacement) ──────────────
            using (var app = new HeadlessPlatformApp())
            {
                var window = new HeadlessWindow(new WindowDesc("cp2-mx-iv", new Size2(360, 360), 1f));
                window.Show();
                var rl = new ReorderList { DwellMs = 0f };           // 0 dwell ⇒ target follows pending immediately (test-deterministic)
                var ver = new Signal<int>(0);
                int from = -1, to = -1;
                rl.OnCommit = (f, t) => { from = f; to = t; };
                using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new W0fStaticProbe
                {
                    Build = () => new BoxEl
                    {
                        Width = 320, Height = 280,
                        Children =
                        [
                            ItemsView.Create(5,
                                i => new BoxEl { Children = [new TextEl($"row {i}") { Size = 13f }] },
                                RepeatLayout.Stack(40f),
                                selector: SelectorVisual.AccentPill,
                                itemDisplacement: i => { rl.OffsetFor2D(i, 0f, 0f, out _, out _); return (0f, rl.OffsetFor(i)); },
                                displacementVersion: ver),
                        ],
                    },
                });
                host.RunFrame();
                // Drive the substrate like a preset would: Begin, Update past row 2's midpoint, commit the target.
                rl.Begin(0, 5, itemExtent: 40f);
                rl.Update(88f);                                       // dragged centre 20+88=108 > mid-2 (100) → pending 2
                rl.Advance(0f);                                       // 0 dwell ⇒ target = 2 now (OffsetFor parts rows 1,2)
                ver.Value = ver.Peek() + 1;
                host.RunFrame();
                for (int i = 0; i < 40; i++) host.RunFrame();        // settle the seeded translate
                var vp = FindViewport(host.Scene, 5);
                var row1 = RealizedRow(host.Scene, vp, 1);
                float row1Dy = row1.IsNull ? 0f : host.Scene.Paint(row1).LocalTransform.Dy;
                bool ivDisplace = !row1.IsNull && Near(row1Dy, -40f, 0.8f);   // row 1 parts up by one extent
                int dest = rl.Complete();
                bool ivCommit = dest == 2 && from == 0 && to == 2;
                Check("cp2.matrix.itemsview drag 0→2 (preset-wired): displacement parts the block + the substrate commits 0→2",
                    ivDisplace && ivCommit, $"row1Dy={row1Dy:0.0} commit=({from}->{to}) dest={dest}");
            }
        }

        // ── C7 — cp2.selectorpresets: each SelectorVisual preset builds the correct selected chrome, exercised through
        // the PUBLIC ItemsView selector switch (the SelectorVisuals builders are `internal` with no InternalsVisibleTo
        // to this project, so the public path is the only reachable seam — and it additionally proves the BCore switch
        // wiring + recyclability, since virtualized realize rejects any non-recyclable container). ───────────────────
        {
            // Build a one-item ItemsView in a given selector + selection state and return its realized container subtree.
            (SceneStore scene, NodeHandle container) BuildSel(SelectorVisual sel, ItemsSelectionMode mode, bool selected)
            {
                var model = new SelectionModel { ItemCount = 1, Mode = mode };
                if (selected) model.Select(0);
                using var app = new HeadlessPlatformApp();
                var window = new HeadlessWindow(new WindowDesc("cp2-sel", new Size2(240, 160), 1f));
                window.Show();
                var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new W0fStaticProbe
                {
                    Build = () => new BoxEl
                    {
                        Width = 200, Height = 120,
                        Children =
                        [
                            ItemsView.Create(1, i => new BoxEl { Width = 120, Height = 40 }, RepeatLayout.Stack(44f),
                                selectionMode: mode, selection: model, selector: sel),
                        ],
                    },
                });
                host.RunFrame();
                var vp = FindViewport(host.Scene, 1);
                var container = RealizedRow(host.Scene, vp, 0);
                // Detach the scene from the (disposing) host: AbsoluteRect/Paint stay valid on the retained store.
                var s = host.Scene;
                host.Dispose();
                return (s, container);
            }

            // The accent pill is uniquely identified on NodePaint by (Fill≈AccentDefault ∧ Corners.TopLeft≈1.5); laid-out
            // W/H live in _bounds (read via AbsoluteRect), not NodePaint.Size, so verify geometry separately.
            static bool IsPill(NodePaint p) => Near(p.Corners.TopLeft, 1.5f) && ColorClose(p.Fill, Tok.AccentDefault, 0.02f);

            // (a) AccentPill selected (Single) → the 3×16 r1.5 accent pill exists.
            var (pillScene, pillRoot) = BuildSel(SelectorVisual.AccentPill, ItemsSelectionMode.Single, selected: true);
            var pill = FindBox(pillScene, pillRoot, IsPill);
            var pillRect = pill.IsNull ? default : pillScene.AbsoluteRect(pill);
            bool pillOk = !pill.IsNull && Near(pillRect.W, 3f) && Near(pillRect.H, 16f);

            // (b) AccentPill selected in MULTIPLE → the pill is SUPPRESSED (checkbox-OR-pill); the inline 20×20 check
            // plate is present (the only BorderWidth≈1 ∧ Corners.TopLeft≈3 node in the AccentPill row).
            var (multiScene, multiRoot) = BuildSel(SelectorVisual.AccentPill, ItemsSelectionMode.Multiple, selected: true);
            var noPill = FindBox(multiScene, multiRoot, IsPill);
            var checkPlate = FindBox(multiScene, multiRoot, p => Near(p.BorderWidth, 1f) && Near(p.Corners.TopLeft, 3f));
            var checkRect = checkPlate.IsNull ? default : multiScene.AbsoluteRect(checkPlate);
            bool multiOk = noPill.IsNull && !checkPlate.IsNull && Near(checkRect.W, 20f) && Near(checkRect.H, 20f);

            // (c) Check selected (Single) → the 2px accent border on the plate + the inset 1px ControlSolid inner ring.
            var (checkScene, checkRoot) = BuildSel(SelectorVisual.Check, ItemsSelectionMode.Single, selected: true);
            bool plateBorder = Near(checkScene.Paint(checkRoot).BorderWidth, 2f)
                && ColorClose(checkScene.Paint(checkRoot).BorderColor, Tok.AccentDefault, 0.02f);
            var innerRing = FindBox(checkScene, checkRoot, p => Near(p.BorderWidth, 1f)
                && ColorClose(p.BorderColor, Tok.FillControlSolid, 0.02f) && Near(p.Corners.TopLeft, 3f));
            bool checkOk = plateBorder && !innerRing.IsNull;

            // (d) FullRow superset selected → NO left pill, but the selected subtle plate reads as the full-bleed fill.
            var (fullScene, fullRoot) = BuildSel(SelectorVisual.FullRow, ItemsSelectionMode.Single, selected: true);
            var fullPill = FindBox(fullScene, fullRoot, IsPill);
            bool fullOk = fullPill.IsNull && ColorClose(fullScene.Paint(fullRoot).Fill, Tok.FillSubtleSecondary, 0.02f);

            // (e) None → a bare container: no pill, no accent ring (app draws its own selection).
            var (noneScene, noneRoot) = BuildSel(SelectorVisual.None, ItemsSelectionMode.Single, selected: true);
            bool noneOk = FindBox(noneScene, noneRoot, IsPill).IsNull
                && FindBox(noneScene, noneRoot, p => Near(p.BorderWidth, 3f)).IsNull;

            Check("cp2.selectorpresets AccentPill/Check/FullRow/None build the correct selected chrome (pill 3×16, Multiple-suppression, Check dual-border, FullRow full-bleed, None bare)",
                pillOk && multiOk && checkOk && fullOk && noneOk,
                $"pill={pillOk} multiSuppress={multiOk} check={checkOk} fullRow={fullOk} none={noneOk}");
        }

        // ── C8 — cp2.treedwell: TreeView consumes the SAME reorder substrate. The dwell + 1s auto-expand advance on a
        // still drag (stage A keep-alive + BTree left the ticker intact); the realized-handle map prune (BT2) keeps the
        // realized-row count bounded across a Roots rebuild; reparent is DEFERRED (default) so a drop commits SIBLING-only. ─
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cp2-tree", new Size2(360, 400), 1f));
            window.Show();
            var rootsA = new List<TreeNode>
            {
                new("Alpha"), new("Bravo"), new("Charlie"), new("Delta"), new("Echo"),
            };
            var roots = new Signal<IReadOnlyList<TreeNode>>(rootsA);
            TreeNode? commitParent = null; int tFrom = -1, tTo = -1; bool reparent = false;
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Width = 320, Height = 360,
                    Children =
                    [
                        TreeView.Create(roots.Value, itemTemplate: null, canReorderItems: true,
                            onReorder: (parent, f, t) => { commitParent = parent; tFrom = f; tTo = t; reparent = parent is not null; }),
                    ],
                },
            });
            host.RunFrame();

            // Count realized tree rows via role scan (each TreeViewItem row carries the Button automation role).
            int RowCount() => Roles(host.Scene, AutomationRole.Button).Count;
            int rows0 = RowCount();
            bool initialRows = rows0 >= 5;                            // 5 roots realized as Button-role rows

            // Promote a drag on root 0, hold still through the dwell, assert the keep-alive + that work stays pending.
            var firstRow = Roles(host.Scene, AutomationRole.Button) is { Count: > 0 } rs ? rs[0] : NodeHandle.Null;
            bool aliveDuringDrag = true, dwellAdvanced = false;
            if (!firstRow.IsNull)
            {
                var c = CenterOf(host.Scene, firstRow);
                window.QueueInput(new InputEvent(InputKind.PointerDown, c, 0, 0));
                host.RunFrame();
                window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(c.X, c.Y + 60f), 0, 0));   // past sibling 1
                host.RunFrame();
                for (int i = 0; i < 4; i++)
                {
                    System.Threading.Thread.Sleep(110);
                    host.RunFrame();
                    if (!host.HasActiveWork) aliveDuringDrag = false;
                }
                // After the dwell, the projected sibling order moved root 0 down: the FLIP re-rendered the keyed rows,
                // so the row that is now FIRST in the realized column is no longer the originally-dragged node's slot.
                // Proxy: the dragged ghost node carries a non-zero translate (it rides the pointer) — proves the drag
                // engine + dwell are live (the keep-alive pumped frames).
                dwellAdvanced = host.HasActiveWork && MathF.Abs(host.Scene.Paint(firstRow).LocalTransform.Dy) > 0.5f;
                window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(c.X, c.Y + 60f), 0, 0));
                host.RunFrame();
            }
            bool siblingCommit = !reparent && tFrom == 0 && tTo >= 1 && commitParent is null;   // root-level sibling move

            // BT2 prune: rebuild Roots to a SMALLER set; the realized rows must shrink (no leaked handles inflate it).
            roots.Value = new List<TreeNode> { new("Alpha"), new("Bravo") };
            host.RunFrame();
            int rows1 = RowCount();
            bool prunedBounded = rows1 <= rows0 && rows1 >= 2;       // realized rows track the live projection (bounded)

            Check("cp2.treedwell TreeView reorder: dwell/keep-alive advance on a still drag, sibling-only commit (reparent deferred), realized rows stay bounded after a Roots rebuild",
                initialRows && aliveDuringDrag && dwellAdvanced && siblingCommit && prunedBounded,
                $"rows {rows0}→{rows1} alive={aliveDuringDrag} dwell={dwellAdvanced} commit=({tFrom}->{tTo},parent={(commitParent is null ? "root" : "node")},reparent={reparent})");
        }

        // ── cp2.dragalloc: the displacement SEED is edge-triggered (it fires only when DisplacementVersion changes),
        // so a steady frame with NO version bump is 0-alloc on the hot phases. (A LIVE ListView/GridView reorder is NOT
        // per-frame 0-alloc — its FrameClock dwell ticker re-renders every drag frame to advance the 200/300ms timer,
        // an inherent cost of the live-reorder timer, NOT of the displacement seed; the raw drag-move path's 0-alloc is
        // already pinned by e5dragdrop.8b. So this isolates the SEED on the ItemsView path, which has no dwell ticker:
        // once seeded and settled, an unbumped frame allocates nothing.) ────────────────────────────────────────────
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cp2-alloc", new Size2(360, 360), 1f));
            window.Show();
            var ver = new Signal<int>(0);
            int dispTarget = 2;
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Width = 320, Height = 280,
                    Children =
                    [
                        ItemsView.Create(8,
                            i => new BoxEl { Children = [new TextEl($"row {i}") { Size = 13f }] },
                            RepeatLayout.Stack(40f),
                            selector: SelectorVisual.AccentPill,
                            itemDisplacement: i => i == dispTarget ? (0f, 24f) : (0f, 0f),
                            displacementVersion: ver),
                    ],
                },
            });
            host.RunFrame();
            ver.Value = ver.Peek() + 1;          // seed the displacement track once (edge), then let it fully settle
            for (int i = 0; i < 50; i++) host.RunFrame();
            // A steady frame with NO version bump: the seed effect doesn't re-run, the AnimEngine track has settled and
            // been reclaimed, the ItemsView doesn't re-render — phases 6–13 must allocate 0.
            var warm = host.RunFrame();
            var steady = host.RunFrame();
            bool zero = steady.HotPhaseAllocBytes == 0;
            Check("cp2.dragalloc the displacement seed is edge-triggered — a steady ItemsView frame with no version bump is 0-alloc on phases 6–13",
                zero, $"{steady.HotPhaseAllocBytes} bytes (warm={warm.HotPhaseAllocBytes})");
        }
    }

    // D2 — the PasswordBox reveal eye must SURVIVE its own click. Regression: engine pointer focus moved to ANY
    // clicked node with a handler, so clicking the eye blurred the field; the blur→refocus storm re-ran the WinUI
    // OnGotFocus arm-clear (CPasswordBox::OnGotFocus, PasswordBox.cpp:572–581) and the eye unmounted until the box
    // was emptied and retyped. WinUI: the RevealButton is IsTabStop=False (PasswordBox_themeresources.xaml:193) — it
    // can never take focus, so the field stays focused and CanInvokeRevealButton (= canShow ∧ hasSpace ∧ IsFocused,
    // PasswordBox.cpp:618–626) keeps the eye visible across its own clicks.
    static void D2PasswordRevealFocusChecks(StringTable strings)
    {
        static NodeHandle TextVisual(SceneStore s, NodeHandle n)
        {
            if (n.IsNull) return NodeHandle.Null;
            if (s.Paint(n).VisualKind == VisualKind.Text) return n;
            for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
            {
                var r = TextVisual(s, c);
                if (!r.IsNull) return r;
            }
            return NodeHandle.Null;
        }

        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("d2-pw", new Size2(420, 280), 1f)); window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        NodeHandle other = default;
        var root = new W0fStaticProbe
        {
            Build = () => new BoxEl
            {
                Direction = 1, Gap = 16, Padding = Edges4.All(12),
                Children =
                [
                    PasswordBox.Create("Password", 280f),
                    // A focusable blur target (OnClick ⇒ auto-focusable). Deliberately NOT AutomationRole.Button so
                    // Roles(Button) below counts exactly the reveal eye.
                    new BoxEl { Width = 120, Height = 32, OnClick = () => { }, OnRealized = h => other = h },
                ],
            },
        };
        using var host = new AppHost(app, window, device, fonts, strings, root);
        host.RunFrame();
        var scene = host.Scene;
        var field = FindRole(scene, scene.Root, AutomationRole.Text);
        var tn = TextVisual(scene, field);

        // Focus the EMPTY box and type — the empty→non-empty transition arms + mounts the eye
        // (CPasswordBox::OnContentChanged, PasswordBox.cpp:366–377).
        ClickNode(host, window, field);
        host.RunFrame();
        foreach (char c in "ab") window.QueueInput(new InputEvent(InputKind.Char, default, 0, c));
        host.RunFrame();
        host.RunFrame();
        var btns = Roles(scene, AutomationRole.Button);
        bool mounted = btns.Count == 1 && HasGlyph(device, strings, Icons.RevealPassword);

        // cp2.a — CLICK the eye: it is not a focus target (RevealButton IsTabStop=False,
        // PasswordBox_themeresources.xaml:193), so the field never blurs, the arm flag survives, and the eye STAYS
        // mounted; the click's release re-masks (the press peeked).
        if (mounted) ClickNode(host, window, btns[0]);
        host.RunFrame();
        bool stillMounted = Roles(scene, AutomationRole.Button).Count == 1 && HasGlyph(device, strings, Icons.RevealPassword);
        bool maskedAfterClick = strings.Resolve(scene.Paint(tn).Text) == "●●";
        bool fieldFocused = (scene.Flags(field) & NodeFlags.Focused) != 0;
        Check("cp2.a — clicking the reveal eye keeps it mounted + masked, field still focused",
            mounted && stillMounted && maskedAfterClick && fieldFocused,
            $"mounted={mounted} still={stillMounted} masked={maskedAfterClick} focus={fieldFocused}");

        // cp2.b — press-and-HOLD peek: pointer-down on the eye renders the raw password mid-press; pointer-up
        // re-masks (RevealPassword on the ToggleButton press, PasswordBox.cpp:260–308).
        var eyes = Roles(scene, AutomationRole.Button);
        bool peeked = false, remasked = false, focusHeld = false;
        if (eyes.Count == 1)
        {
            var bc = CenterOf(scene, eyes[0]);
            window.QueueInput(new InputEvent(InputKind.PointerDown, bc, 0, 0, 0f, KeyModifiers.None, PointerKind.Mouse, false, 5_000));
            host.RunFrame();
            host.RunFrame();
            peeked = strings.Resolve(scene.Paint(tn).Text) == "ab";
            window.QueueInput(new InputEvent(InputKind.PointerUp, bc, 0, 0, 0f, KeyModifiers.None, PointerKind.Mouse, false, 5_100));
            host.RunFrame();
            host.RunFrame();
            remasked = strings.Resolve(scene.Paint(tn).Text) == "●●";
            focusHeld = (scene.Flags(field) & NodeFlags.Focused) != 0;
        }
        Check("cp2.b — press-and-hold on the eye reveals the raw password mid-press; release re-masks (field still focused)",
            peeked && remasked && focusHeld, $"peeked={peeked} remasked={remasked} focus={focusHeld}");

        // cp2.c — blur to ANOTHER control, refocus the populated box: the eye must NOT return (OnGotFocus arm-clear,
        // PasswordBox.cpp:572–581); typing into the populated box keeps it hidden (the arm is the empty→non-empty
        // transition ONLY, PasswordBox.cpp:366–377; cleared while empty, :430–434); emptying + retyping re-arms.
        ClickNode(host, window, other);
        host.RunFrame();
        bool blurUnmounts = Roles(scene, AutomationRole.Button).Count == 0 && (scene.Flags(field) & NodeFlags.Focused) == 0;
        ClickNode(host, window, field);
        host.RunFrame();
        bool noEyeOnRefocus = Roles(scene, AutomationRole.Button).Count == 0;
        window.QueueInput(new InputEvent(InputKind.Char, default, 0, 'c'));
        host.RunFrame();
        host.RunFrame();
        bool typingPopulatedHidden = Roles(scene, AutomationRole.Button).Count == 0;
        window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.A, 0f, KeyModifiers.Ctrl));
        window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Back));
        host.RunFrame();
        window.QueueInput(new InputEvent(InputKind.Char, default, 0, 'z'));
        host.RunFrame();
        host.RunFrame();
        bool rearmed = Roles(scene, AutomationRole.Button).Count == 1;
        Check("cp2.c — blur→refocus shows no eye (OnGotFocus arm-clear); typing populated stays hidden; empty→retype re-arms",
            blurUnmounts && noEyeOnRefocus && typingPopulatedHidden && rearmed,
            $"blur={blurUnmounts} refocus={noEyeOnRefocus} typing={typingPopulatedHidden} rearmed={rearmed}");
    }

    // ── prop-net.*: the bound-channel ownership net (Prop<T> unification W0) ─────────────────────────────────────
    // One fixture, every bindable channel: mount bound → fire the signal once → force an OWNER re-render with FRESH
    // element instances (Build() returns new records each render, so Update's ReferenceEquals shortcut cannot hide
    // the write) WITHOUT touching the bound signals → the bound value must survive the static re-assert. Fill and
    // TextColor were the live clobbers (no `*Bind is null` guard at the static write); the other channels lock the
    // already-correct contract the Prop<T> migration must preserve. prop-net.source lands with the W1 chokepoint
    // (no ImageEl decode fixture exists headlessly; its static path is already guarded + id-diffed).
    static void PropNetClobberChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("prop-net", new Size2(420, 420), 1f)); window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);

        var rr = new Signal<int>(0);                      // owner re-render trigger (read by Build)
        var fill = new Signal<ColorF>(ColorF.FromRgba(0xE8, 0x3C, 0x3C, 0xFF));
        var op = new Signal<float>(0.8f);
        var w = new Signal<float>(64f);
        var h = new Signal<float>(24f);
        var tx = new Signal<float>(12f);
        var txt = new Signal<string>("t1");
        var col = new Signal<ColorF>(ColorF.FromRgba(0x18, 0xA0, 0x57, 0xFF));
        var tint = new Signal<ColorF>(ColorF.FromRgba(0x2D, 0x7D, 0xF6, 0xFF));
        var canCol = new Signal<ColorF>(ColorF.FromRgba(0xF5, 0xC5, 0x18, 0xFF));

        NodeHandle nFill = default, nOp = default, nW = default, nH = default, nT = default,
                   wTxt = default, wCol = default, wImg = default, wCan = default;   // wrappers: OnRealized is BoxEl-only
        var root = new W0fStaticProbe
        {
            Build = () =>
            {
                int r = rr.Value;                          // subscribe: rr bump re-renders the owner
                float proof = 0.5f + 0.25f * r;            // reconcile proof: lands in paint.BorderWidth each render
                return new BoxEl
                {
                    Direction = 1, Width = 400, Height = 400, Gap = 2,
                    Children = new Element[]
                    {
                        new BoxEl { Width = 40, Height = 10, BorderWidth = proof, Fill = Prop.Of(() => fill.Value), OnRealized = nh => nFill = nh },
                        new BoxEl { Width = 40, Height = 10, BorderWidth = proof, Fill = ColorF.FromRgba(0x20, 0x20, 0x20, 0xFF), Opacity = Prop.Of(() => op.Value), OnRealized = nh => nOp = nh },
                        new BoxEl { Height = 10, BorderWidth = proof, Width = Prop.Of(() => w.Value), OnRealized = nh => nW = nh },
                        new BoxEl { Width = 40, BorderWidth = proof, Height = Prop.Of(() => h.Value), OnRealized = nh => nH = nh },
                        new BoxEl { Width = 40, Height = 10, BorderWidth = proof, TransformBind = () => Affine2D.Translation(tx.Value, 0f), OnRealized = nh => nT = nh },
                        new BoxEl { OnRealized = nh => wTxt = nh, Children = [ new TextEl("") { Underline = (r & 1) == 1, Text = Prop.Of(() => txt.Value) } ] },
                        new BoxEl { OnRealized = nh => wCol = nh, Children = [ new TextEl("c") { Underline = (r & 1) == 1, Color = Prop.Of(() => col.Value) } ] },
                        new BoxEl { OnRealized = nh => wImg = nh, Children = [ new ImageEl { Width = 24, Height = 24, Placeholder = Prop.Of(() => tint.Value) } ] },
                        // the EditableText shape, post-unification: static+bind coexistence on one channel is now a
                        // COMPILE ERROR (CS1912 duplicate initializer) — the bind owns Color outright; the disabled
                        // ramp stays on its own DisabledColor field (recorder-composited, a different channel).
                        new BoxEl { OnRealized = nh => wCan = nh, Children = [ new TextEl("canary") { DisabledColor = Tok.TextDisabled, Underline = (r & 1) == 1, Color = Prop.Of(() => canCol.Value) } ] },
                    },
                };
            },
        };
        using var host = new AppHost(app, window, device, fonts, strings, root);
        host.RunFrame();
        NodeHandle nTxt = host.Scene.FirstChild(wTxt), nCol = host.Scene.FirstChild(wCol),
                   nImg = host.Scene.FirstChild(wImg), nCan = host.Scene.FirstChild(wCan);

        // fire every bound signal ONCE (new values), no re-render in between
        fill.Value = ColorF.FromRgba(0x8B, 0x3C, 0xC9, 0xFF);
        op.Value = 0.4f; w.Value = 96f; h.Value = 48f; tx.Value = 31f;
        txt.Value = "t2"; col.Value = ColorF.FromRgba(0xE8, 0x8C, 0x1C, 0xFF);
        tint.Value = ColorF.FromRgba(0x18, 0xA0, 0xA0, 0xFF); canCol.Value = ColorF.FromRgba(0x3C, 0x8B, 0xC9, 0xFF);
        host.RunFrame();

        // OWNER re-render: fresh element records, bound signals untouched
        rr.Value = 1;
        host.RunFrame();

        bool reconciled = Near(host.Scene.Paint(nFill).BorderWidth, 0.75f, 0.001f)
                          && (host.Scene.Paint(nTxt).TextDecorations & NodePaint.UnderlineBit) != 0;
        Check("prop-net.reconciled owner re-render actually rewrote columns on the probed nodes",
            reconciled, $"bw={host.Scene.Paint(nFill).BorderWidth} deco={host.Scene.Paint(nTxt).TextDecorations}");

        Check("prop-net.fill bound Fill survives an owner re-render between signal fires",
            host.Scene.Paint(nFill).Fill == fill.Peek(), $"paint={host.Scene.Paint(nFill).Fill} want={fill.Peek()}");
        Check("prop-net.opacity bound Opacity survives an owner re-render",
            Near(host.Scene.Paint(nOp).Opacity, 0.4f, 0.001f), $"paint={host.Scene.Paint(nOp).Opacity}");
        Check("prop-net.width bound Width survives an owner re-render",
            Near(host.Scene.Layout(nW).Width, 96f, 0.001f), $"li={host.Scene.Layout(nW).Width}");
        Check("prop-net.height bound Height survives an owner re-render",
            Near(host.Scene.Layout(nH).Height, 48f, 0.001f), $"li={host.Scene.Layout(nH).Height}");
        Check("prop-net.transform bound Transform survives an owner re-render (identity-gate skips the static)",
            Near(host.Scene.Paint(nT).LocalTransform.Dx, 31f, 0.001f), $"dx={host.Scene.Paint(nT).LocalTransform.Dx}");
        Check("prop-net.text bound Text survives an owner re-render",
            host.Scene.Paint(nTxt).Text == strings.Intern("t2"), $"text-id={host.Scene.Paint(nTxt).Text}");
        Check("prop-net.textcolor bound Color survives an owner re-render between signal fires",
            host.Scene.Paint(nCol).TextColor == col.Peek(), $"paint={host.Scene.Paint(nCol).TextColor} want={col.Peek()}");
        Check("prop-net.placeholder bound Placeholder survives an owner re-render",
            host.Scene.Paint(nImg).Fill == tint.Peek(), $"paint={host.Scene.Paint(nImg).Fill}");
        Check("prop-net.canary bound Color owns the channel outright (static+bind coexistence is now a compile error)",
            host.Scene.Paint(nCan).TextColor == canCol.Peek(), $"paint={host.Scene.Paint(nCan).TextColor} want={canCol.Peek()}");
    }

    // ── prop.signal-direct + bind.mount-only: the Prop<T> union's two bind kinds (W1) ───────────────────────────
    static void PropUnionChecks(StringTable strings)
    {
        // Signal-direct: a concrete Signal<T>/FloatSignal assigned straight to the channel property — no user
        // closure; the engine effect reads sig.Value. Paint-only writes must stay compositor-only (Rendered=false).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("prop-signal-direct", new Size2(300, 300), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var op = new Signal<float>(0.6f);
            var col = new Signal<ColorF>(ColorF.FromRgba(0xE8, 0x3C, 0x3C, 0xFF));
            var wf = new FloatSignal(40f);
            var txt = new Signal<string>("sd1");
            NodeHandle box = default, wBox = default, wTxt = default;
            using var host = new AppHost(app, window, device, fonts, strings, new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Direction = 1, Width = 280, Height = 280,
                    Children = new Element[]
                    {
                        new BoxEl { Width = 40, Height = 10, Opacity = op, Fill = col, OnRealized = h => box = h },
                        new BoxEl { Height = 10, Width = wf, Fill = ColorF.FromRgba(0x20, 0x20, 0x20, 0xFF), OnRealized = h => wBox = h },
                        new BoxEl { OnRealized = h => wTxt = h, Children = [ new TextEl("") { Text = txt } ] },
                    },
                },
            });
            host.RunFrame();
            var nTxt = host.Scene.FirstChild(wTxt);
            bool initial = Near(host.Scene.Paint(box).Opacity, 0.6f, 0.001f)
                && host.Scene.Paint(box).Fill == col.Peek()
                && Near(host.Scene.Layout(wBox).Width, 40f, 0.001f)
                && host.Scene.Paint(nTxt).Text == strings.Intern("sd1");
            op.Value = 0.25f; col.Value = ColorF.FromRgba(0x18, 0xA0, 0x57, 0xFF);
            var st = host.RunFrame();
            bool paintOnly = !st.Rendered;                       // opacity/fill bind fires are compositor-only
            wf.Value = 90f; txt.Value = "sd2";
            host.RunFrame();
            Check("prop.signal-direct Signal/FloatSignal assigned straight to Opacity/Fill/Width/Text drive their channels (no closure)",
                initial && Near(host.Scene.Paint(box).Opacity, 0.25f, 0.001f) && host.Scene.Paint(box).Fill == col.Peek()
                && Near(host.Scene.Layout(wBox).Width, 90f, 0.001f) && host.Scene.Paint(nTxt).Text == strings.Intern("sd2"),
                $"initial={initial} op={host.Scene.Paint(box).Opacity} w={host.Scene.Layout(wBox).Width}");
            Check("prop.signal-direct paint-channel signal writes stay compositor-only (Rendered=false)",
                paintOnly, $"rendered={st.Rendered}");
        }

        // Mount-only wiring contract (locked, deliberate): a NEW thunk supplied on a re-render is IGNORED — the
        // mount-captured bind is immortal until unmount. Change the signal's VALUE, not the bind.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("bind-mount-only", new Size2(200, 200), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var rr = new Signal<int>(0);
            NodeHandle box = default;
            using var host = new AppHost(app, window, device, fonts, strings, new W0fStaticProbe
            {
                Build = () =>
                {
                    int r = rr.Value;                            // each render captures a FRESH r in a FRESH thunk
                    return new BoxEl { Width = 40, Height = 10, Opacity = Prop.Of(() => 0.1f + 0.2f * r), OnRealized = h => box = h };
                },
            });
            host.RunFrame();
            rr.Value = 1;                                        // re-render: new thunk (r=1) — must be IGNORED
            host.RunFrame();
            Check("bind.mount-only.stale a fresh thunk on re-render is ignored (mount-captured bind is immortal)",
                Near(host.Scene.Paint(box).Opacity, 0.1f, 0.001f), $"op={host.Scene.Paint(box).Opacity} (0.3 would mean re-wiring happened)");
        }
    }

    // ── anim.rest.*: settled/canceled-track resting values vs static re-asserts (Prop<T> B-waves) ────────────────
    static void AnimRestChecks(StringTable strings)
    {
        // B1: deactivating a ProgressRing must CancelToRest the trim channels (paint → NaN, recorder falls back to
        // the ArcSpec terminal) — a bare Cancel froze the last interpolated partial sweep in paint.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("anim-rest-ring", new Size2(240, 180), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var active = new Signal<bool>(true);
            NodeHandle arc = default;
            var parts = new TemplateParts();
            parts[ProgressRing.PartRing] = b => b with { OnRealized = h => arc = h };
            using var host = new AppHost(app, window, device, fonts, strings, new W0fStaticProbe
            {
                Build = () => new BoxEl { Width = 120, Height = 120, Children = [ProgressRing.Indeterminate(isActive: active.Value, parts: parts)] },
            });
            host.RunFrame(); host.RunFrame(); host.RunFrame();   // let the trim loop write real values into paint
            bool spinning = !arc.IsNull && host.Animation.HasTracks(arc);
            active.Value = false;
            host.RunFrame(); host.RunFrame();
            ref var p = ref host.Scene.Paint(arc);
            Check("anim.rest.progress.ring.cancel deactivation rests the trim channels at NaN (spec fallback), not a frozen partial arc",
                spinning && !host.Animation.HasTracks(arc) && float.IsNaN(p.StrokeTrimStart) && float.IsNaN(p.StrokeTrimEnd),
                $"spinning={spinning} tracks={host.Animation.HasTracks(arc)} trimS={p.StrokeTrimStart} trimE={p.StrokeTrimEnd}");
        }

        // B2: the NavPill shape — after a hide-fade settles and frees, an UNRELATED re-render must keep the node
        // hidden (the element declares the state-dependent static at the transition terminal).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("anim-rest-pill", new Size2(240, 180), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var visible = new Signal<bool>(true);
            var unrelated = new Signal<int>(0);
            using var host = new AppHost(app, window, device, fonts, strings, new PillRestProbe { Visible = visible, Unrelated = unrelated });
            host.RunFrame();
            var pill = host.Scene.FirstChild(host.Scene.Root);
            visible.Value = false;
            for (int f = 0; f < 40 && host.Animation.HasActive; f++) host.RunFrame();   // fade out + settle/free
            bool settledHidden = Near(host.Scene.Paint(pill).Opacity, 0f, 0.01f) && !host.Animation.HasTracks(pill);
            unrelated.Value = 1;                                                        // unrelated owner re-render
            host.RunFrame();
            Check("anim.rest.pill hidden-after-fade survives an unrelated re-render (state-dependent resting opacity)",
                settledHidden && Near(host.Scene.Paint(pill).Opacity, 0f, 0.01f),
                $"settledHidden={settledHidden} after={host.Scene.Paint(pill).Opacity}");
        }

        // B3: a settled transform track's terminal survives an owner re-render on an identity-declared polyline
        // (the BoxEl :1003 identity gate now applies to PolylineStrokeEl too).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("anim-rest-poly", new Size2(240, 180), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var rr = new Signal<int>(0);
            NodeHandle wrap = default;
            using var host = new AppHost(app, window, device, fonts, strings, new W0fStaticProbe
            {
                Build = () =>
                {
                    _ = rr.Value;
                    return new BoxEl
                    {
                        Width = 200, Height = 100,
                        Children = [ new BoxEl { OnRealized = h => wrap = h, Children = [ new PolylineStrokeEl
                        {
                            P0 = new Point2(0f, 0f), P1 = new Point2(24f, 24f), PointCount = 2,
                            Color = Tok.AccentDefault, Thickness = 2f, Width = 24f, Height = 24f,
                        } ] } ],
                    };
                },
            });
            host.RunFrame();
            var poly = host.Scene.FirstChild(wrap);
            host.Animation.Animate(poly, AnimChannel.TranslateX, 0f, 14f, 40f, Easing.Linear);
            for (int f = 0; f < 40 && host.Animation.HasActive; f++) host.RunFrame();
            bool settled = !host.Animation.HasTracks(poly) && Near(host.Scene.Paint(poly).LocalTransform.Dx, 14f, 0.1f);
            rr.Value = 1;
            host.RunFrame();
            Check("anim.rest.polyline.transform settled track terminal survives an owner re-render (identity gate)",
                settled && Near(host.Scene.Paint(poly).LocalTransform.Dx, 14f, 0.1f),
                $"settled={settled} dx={host.Scene.Paint(poly).LocalTransform.Dx}");
        }
    }

    static void ProgressIndeterminateLifecycleChecks(StringTable strings)
    {
        // ProgressRing: parent re-render updates isActive through context, preserving the component instance.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("progress-ring-lifecycle", new Size2(240, 180), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var active = new Signal<bool>(true);
            NodeHandle arc = default;
            var parts = new TemplateParts();
            parts[ProgressRing.PartRing] = b => b with { OnRealized = h => arc = h };
            var root = new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Width = 160, Height = 120, Padding = Edges4.All(16),
                    Children = [ProgressRing.Indeterminate(isActive: active.Value, parts: parts)],
                },
            };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var ring = host.Scene.Parent(arc);
            bool activeMount = !arc.IsNull && !ring.IsNull
                && Near(host.Scene.Paint(ring).Opacity, 1f, 0.001f)
                && host.Animation.HasTracks(ring) && host.Animation.HasTracks(arc);

            var ring0 = ring;
            var arc0 = arc;
            active.Value = false;
            host.RunFrame(); host.RunFrame();
            bool stopped = ring == ring0 && arc == arc0
                && Near(host.Scene.Paint(ring).Opacity, 0f, 0.001f)
                && !host.Animation.HasTracks(ring)
                && !host.Animation.HasTracks(arc);

            active.Value = true;
            host.RunFrame();
            bool restarted = ring == ring0 && arc == arc0 && Near(host.Scene.Paint(ring).Opacity, 1f, 0.001f)
                && host.Animation.HasTracks(ring) && host.Animation.HasTracks(arc);

            Check("progress.1 ProgressRing isActive flows through context: active spins, inactive stops, reactivation restarts without remount",
                activeMount && stopped && restarted,
                $"active={activeMount} stopped={stopped} restarted={restarted} same={ring == ring0 && arc == arc0}"
                + $" reOpacity={host.Scene.Paint(ring).Opacity:0.###} reRingTracks={host.Animation.HasTracks(ring)} reArcTracks={host.Animation.HasTracks(arc)}");
        }

        // Fresh inactive mount: no hidden compositor work should be seeded under opacity 0.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("progress-ring-inactive", new Size2(240, 180), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            NodeHandle arc = default;
            var parts = new TemplateParts();
            parts[ProgressRing.PartRing] = b => b with { OnRealized = h => arc = h };
            using var host = new AppHost(app, window, device, fonts, strings, new W0fStaticProbe
            {
                Build = () => ProgressRing.Indeterminate(isActive: false, parts: parts),
            });
            host.RunFrame(); host.RunFrame();
            var ring = host.Scene.Parent(arc);
            bool idle = !arc.IsNull && !ring.IsNull
                && Near(host.Scene.Paint(ring).Opacity, 0f, 0.001f)
                && !host.Animation.HasTracks(ring)
                && !host.Animation.HasTracks(arc);
            Check("progress.2 inactive ProgressRing mounts idle with opacity 0 and zero animation tracks",
                idle, $"arc={arc} ring={ring} tracks arc={(!arc.IsNull && host.Animation.HasTracks(arc))}");
        }

        // ProgressBar: parent re-render updates state/width through context, so the existing effect deps fire.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("progress-bar-lifecycle", new Size2(420, 180), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var state = new Signal<ProgressBarState>(ProgressBarState.Normal);
            var width = new Signal<float>(240f);
            var fills = new List<NodeHandle>(2);
            var parts = new TemplateParts();
            parts[ProgressBar.PartFill] = b => b with
            {
                OnRealized = h => { if (!fills.Contains(h)) fills.Add(h); },
            };
            using var host = new AppHost(app, window, device, fonts, strings, new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Width = 360, Height = 80, Padding = Edges4.All(16),
                    Children = [ProgressBar.Indeterminate(width.Value, state.Value, parts)],
                },
            });
            host.RunFrame();
            bool normalTracks = fills.Count == 2 && host.Animation.HasTracks(fills[0]) && host.Animation.HasTracks(fills[1]);
            var barRoot = fills.Count == 2 ? host.Scene.Parent(fills[0]) : NodeHandle.Null;

            state.Value = ProgressBarState.Paused;
            host.RunFrame(); host.RunFrame();
            bool paused = fills.Count == 2
                && Near(host.Scene.Paint(fills[0]).Opacity, 0f, 0.001f)
                && !host.Animation.HasTracks(fills[0])
                && ColorClose(host.Scene.Paint(fills[1]).Fill, Tok.SystemFillCaution, 0.004f);

            state.Value = ProgressBarState.Normal;
            host.RunFrame();
            bool resumed = fills.Count == 2
                && ColorClose(host.Scene.Paint(fills[1]).Fill, Tok.AccentDefault, 0.004f)
                && host.Animation.HasTracks(fills[0]) && host.Animation.HasTracks(fills[1]);

            width.Value = 300f;
            host.RunFrame(); host.RunFrame();
            bool resized = !barRoot.IsNull && Near(host.Scene.AbsoluteRect(barRoot).W, 300f, 0.5f);

            Check("progress.3 ProgressBar indeterminate state and width props update the preserved component",
                normalTracks && paused && resumed && resized,
                $"normal={normalTracks} paused={paused} resumed={resumed} resized={resized} width={(!barRoot.IsNull ? host.Scene.AbsoluteRect(barRoot).W : 0):0.#}");
        }

        // CheckBox: checked mark color/pressability must update through context without remounting or replaying draw-on.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("checkbox-mark-props", new Size2(260, 160), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var enabled = new Signal<bool>(true);
            using var host = new AppHost(app, window, device, fonts, strings, new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Width = 220, Height = 80, Padding = Edges4.All(16),
                    Children = [CheckBox.Create("opt", CheckState.Checked, _ => { }, isEnabled: enabled.Value)],
                },
            });
            host.RunFrame();
            var cb = FindRole(host.Scene, host.Scene.Root, AutomationRole.CheckBox);
            var mark = FindPolylineStrokeNode(host.Scene, cb);
            for (int i = 0; i < 30; i++) host.RunFrame();
            bool settled = !mark.IsNull && !host.Animation.HasTracks(mark);
            bool initialColor = !mark.IsNull && host.Scene.TryGetPolylineStroke(mark, out var before)
                && ColorClose(before.Color, Tok.TextOnAccentPrimary, 0.004f);

            enabled.Value = false;
            host.RunFrame(); host.RunFrame();
            var markAfter = FindPolylineStrokeNode(host.Scene, cb);
            bool sameNode = markAfter == mark;
            bool disabledColor = !markAfter.IsNull && host.Scene.TryGetPolylineStroke(markAfter, out var after)
                && ColorClose(after.Color, Tok.TextOnAccentDisabled, 0.004f);
            bool noReplay = !markAfter.IsNull && !host.Animation.HasTracks(markAfter);

            Check("progress.4 CheckBox mark props update through context without remounting or replaying draw-on",
                settled && initialColor && sameNode && disabledColor && noReplay,
                $"settled={settled} initial={initialColor} same={sameNode} disabled={disabledColor} replay={!noReplay}");
        }
    }

    // D3 — Expander motion (the SizeMode.Reflow contract — a DELIBERATE divergence from WinUI's layout snap): the clip
    // wrapper's LAYOUT height eases 0 → ContentHeight over 333ms (KeySpline 0,0,0,1) and back over 167ms (KeySpline
    // 1,1,0,1, the ExitDynamics leg), so the sibling below moves smoothly and MONOTONICALLY instead of snapping; the
    // Trailing anchor rides the panel's bottom edge on the reveal edge (ChildShiftY == clipH − contentExtent mid-
    // flight — the slide-from-under-the-header); the panel stays MOUNTED through the collapse and unmounts at settle
    // (WinUI's Visibility=Collapsed keyframe at t=167ms). NO opacity animation. An initiallyExpanded mount rests with
    // no motion, zero child-shift, and no presented-height residue (the gallery "content clipped at the card bottom" bug).
    static void D3ExpanderChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("expander-d3", new Size2(360, 320), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        // Host the Expander INSIDE a column wrapper with a following sibling: the scene root always fills the window,
        // so sibling Y is the observable proof that reveal height participates in parent layout.
        var root = new W0fStaticProbe
        {
            Build = () => new BoxEl
            {
                Direction = 1,
                Children =
                [
                    Embed.Comp(() => new Expander
                    {
                        Header = "Section",
                        Content = new BoxEl { Height = 60f, Children = new Element[] { new TextEl("expander-action") { Size = 14f } } },
                        InitiallyExpanded = false,
                    }),
                    new BoxEl { Height = 24f, Children = new Element[] { new TextEl("after-expander") { Size = 14f } } },
                ],
            },
        };
        using var host = new AppHost(app, window, device, fonts, strings, root);

        host.RunFrame();   // mount collapsed
        var anchor = Child(host.Scene, host.Scene.Root, 0);    // scene root IS the Build column → child 0 = component anchor
        var sibling = Child(host.Scene, host.Scene.Root, 1);   // the 24px row below the expander
        var card = host.Scene.FirstChild(anchor);              // component anchor → the card box
        var header = Child(host.Scene, card, 0);
        float headerH = host.Scene.AbsoluteRect(header).H;
        float siblingYCollapsed = host.Scene.AbsoluteRect(sibling).Y;

        // cp3.a — open click: the content mounts, but the click frame still PAINTS the old size (the reflow track's
        // JustSeeded first tick re-establishes 0 before record), so the sibling never jumps; later frames ease it down
        // MONOTONICALLY while the Trailing anchor keeps the panel's bottom edge on the reveal edge.
        ClickNode(host, window, header);
        var clip = Child(host.Scene, card, 1);           // the clip wrapper is ALWAYS mounted (the transition's host)
        var content = clip.IsNull ? NodeHandle.Null : Child(host.Scene, clip, 0);
        float cardHClick = host.Scene.AbsoluteRect(card).H;
        float clipHClick = clip.IsNull ? 0f : host.Scene.AbsoluteRect(clip).H;
        float contentH = content.IsNull ? 0f : host.Scene.AbsoluteRect(content).H;
        float contentExtent = contentH - 1f;             // the −1px border-overlap margin: panel bottom = contentH − 1
        float siblingYClick = host.Scene.AbsoluteRect(sibling).Y;
        bool monotoneOpen = true;
        float prevSibY = siblingYClick, clipHMid = 0f, shiftMid = 0f, siblingYMid = 0f;
        for (int i = 0; i < 30; i++)                     // ≥ 333ms — settle (sampled per frame for monotonicity)
        {
            host.RunFrame();
            float y = host.Scene.AbsoluteRect(sibling).Y;
            if (y < prevSibY - 0.25f) monotoneOpen = false;
            prevSibY = y;
            if (i == 2) { clipHMid = host.Scene.AbsoluteRect(clip).H; shiftMid = host.Scene.Paint(clip).ChildShiftY; siblingYMid = host.Scene.AbsoluteRect(sibling).Y; }
        }
        float clipHOpen = host.Scene.AbsoluteRect(clip).H;
        float siblingYOpen = host.Scene.AbsoluteRect(sibling).Y;
        float shiftDone = host.Scene.Paint(clip).ChildShiftY;
        bool liRestoredOpen = float.IsNaN(host.Scene.Layout(clip).Height);   // settle returned the declared NaN(auto)
        bool noClickJump = !clip.IsNull && !content.IsNull && Near(siblingYClick, siblingYCollapsed, 1.5f) && Near(cardHClick, headerH + clipHClick, 1.5f) && clipHClick < 2f;
        bool layoutRevealed = siblingYMid > siblingYClick + 4f && siblingYMid < siblingYOpen - 4f && clipHMid > 4f && clipHMid < clipHOpen - 4f;
        bool anchoredOpen = Near(shiftMid, clipHMid - contentExtent, 1.5f) && shiftMid < -4f;   // bottom edge rides the reveal edge
        Check("cp3.a — expand: sibling eases down monotonically (no click jump); the panel's bottom edge rides the reveal edge",
            noClickJump && layoutRevealed && monotoneOpen && anchoredOpen && MathF.Abs(shiftDone) < 0.01f
            && Near(clipHOpen, contentExtent, 1.5f) && liRestoredOpen,
            $"siblingY {siblingYCollapsed:0.0}→{siblingYClick:0.0}→{siblingYMid:0.0}→{siblingYOpen:0.0} clipH {clipHClick:0.0}→{clipHMid:0.0}→{clipHOpen:0.0} shift {shiftMid:0.0}→{shiftDone:0.00} liNaN={liRestoredOpen}");

        // cp3.b — close click: the content stays LIVE through the 167ms reflow while the sibling eases upward; only
        // after the reflow settles does the content unmount (the clip itself STAYS mounted at its declared 0 height).
        ClickNode(host, window, header);                 // collapse — the declared Height flips to 0; ExitDynamics leg
        float siblingYCloseClick = host.Scene.AbsoluteRect(sibling).Y;
        for (int i = 0; i < 3; i++) host.RunFrame();     // ~48ms into the 167ms reflow
        var contentEarly = Child(host.Scene, clip, 0);
        bool liveEarly = !contentEarly.IsNull && host.Scene.IsLive(contentEarly);
        float siblingYClosing = host.Scene.AbsoluteRect(sibling).Y;
        float clipHClosing = host.Scene.AbsoluteRect(clip).H;
        float shiftClosing = host.Scene.Paint(clip).ChildShiftY;
        for (int i = 0; i < 20; i++) host.RunFrame();    // settle + the collapse watcher's unmount frame
        bool unmounted = Child(host.Scene, clip, 0).IsNull && !Child(host.Scene, card, 1).IsNull;
        float closedH = host.Scene.AbsoluteRect(card).H;
        float siblingYClosed = host.Scene.AbsoluteRect(sibling).Y;
        bool liRestoredClosed = host.Scene.Layout(clip).Height == 0f;        // settle returned the declared 0
        bool noCloseJump = Near(siblingYCloseClick, siblingYOpen, 1.5f);
        bool layoutCollapsed = siblingYClosing < siblingYCloseClick - 4f && siblingYClosing > siblingYCollapsed + 4f && clipHClosing > 4f && clipHClosing < clipHOpen - 4f;
        bool anchoredClosing = Near(shiftClosing, clipHClosing - contentExtent, 1.5f) && shiftClosing < -8f;
        Check("cp3.b — collapse: content stays LIVE while the sibling eases up (anchored to the reveal edge), unmounts at settle",
            liveEarly && noCloseJump && layoutCollapsed && anchoredClosing && unmounted && Near(closedH, headerH, 1.5f)
            && Near(siblingYClosed, siblingYCollapsed, 1.5f) && liRestoredClosed,
            $"liveEarly={liveEarly} siblingY {siblingYOpen:0.0}→{siblingYCloseClick:0.0}→{siblingYClosing:0.0}→{siblingYClosed:0.0} clipHClosing={clipHClosing:0.0} shift={shiftClosing:0.0} unmounted={unmounted} li0={liRestoredClosed}");

        // cp3.e — TemplateParts: a part modifier restyles (header fill, content padding) but can NEVER break the
        // control's mechanics — the control re-asserts them after the modifier (a hostile OnClick = null is defeated;
        // the toggle still opens the card).
        {
            using var app3 = new HeadlessPlatformApp();
            var window3 = new HeadlessWindow(new WindowDesc("expander-d3p", new Size2(360, 320), 1f));
            window3.Show();
            var device3 = new HeadlessGpuDevice();
            var partFill = ColorF.FromRgba(10, 200, 30);
            var root3 = new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Direction = 1,
                    Children =
                    [
                        Embed.Comp(() => new Expander
                        {
                            Header = "Parted",
                            Content = new BoxEl { Height = 60f },
                            Parts = new()
                            {
                                [Expander.PartHeader] = b => b with { Fill = partFill, OnClick = null },   // hostile clobber attempt
                                [Expander.PartContent] = c => c with { Padding = Edges4.All(0) },
                            },
                        }),
                    ],
                },
            };
            using var host3 = new AppHost(app3, window3, device3, new HeadlessFontSystem(strings), strings, root3);
            host3.RunFrame();
            var card3 = host3.Scene.FirstChild(Child(host3.Scene, host3.Scene.Root, 0));
            var header3 = Child(host3.Scene, card3, 0);
            var clip3 = Child(host3.Scene, card3, 1);
            bool fillApplied = host3.Scene.Paint(header3).Fill.Equals(partFill);
            ClickNode(host3, window3, header3);                  // would be dead if the modifier's OnClick=null won
            for (int i = 0; i < 30; i++) host3.RunFrame();       // settle the reveal
            var content3 = Child(host3.Scene, clip3, 0);
            bool opened = !content3.IsNull && host3.Scene.AbsoluteRect(clip3).H > 40f;
            // Padding 0 via the part: the panel solves at the user content's height (60), not 60 + 2×16 default padding.
            bool padApplied = !content3.IsNull && Near(host3.Scene.AbsoluteRect(content3).H, 60f, 1.5f);
            Check("cp3.e — TemplateParts: part modifiers restyle (fill, padding) but mechanics are re-asserted (toggle survives OnClick=null)",
                fillApplied && opened && padApplied,
                $"fill={fillApplied} opened={opened} clipH={host3.Scene.AbsoluteRect(clip3).H:0} contentH={(content3.IsNull ? -1f : host3.Scene.AbsoluteRect(content3).H):0}");
        }

        // cp3.c — resting expanded mount (the gallery page mounts initiallyExpanded:true): no motion is seeded (the
        // first frame never FLIP-captures), the child-shift rests at 0, the content paints, and the LAST content
        // child's absolute bottom sits INSIDE the card's absolute bottom — the clipped "An action" gallery bug.
        using var app2 = new HeadlessPlatformApp();
        var window2 = new HeadlessWindow(new WindowDesc("expander-d3b", new Size2(360, 320), 1f));
        window2.Show();
        var device2 = new HeadlessGpuDevice();
        var root2 = new W0fStaticProbe
        {
            Build = () => new BoxEl
            {
                Direction = 1,
                Children =
                [
                    Embed.Comp(() => new Expander
                    {
                        Header = "Section",
                        Content = new BoxEl { Height = 60f, Children = new Element[] { new TextEl("expander-action") { Size = 14f } } },
                        InitiallyExpanded = true,
                    }),
                ],
            },
        };
        using var host2 = new AppHost(app2, window2, device2, new HeadlessFontSystem(strings), strings, root2);
        for (int i = 0; i < 4; i++) host2.RunFrame();
        var card2 = host2.Scene.FirstChild(Child(host2.Scene, host2.Scene.Root, 0));   // component anchor → card box
        var clip2 = Child(host2.Scene, card2, 1);
        var content2 = clip2.IsNull ? NodeHandle.Null : Child(host2.Scene, clip2, 0);
        var inner2 = content2.IsNull ? NodeHandle.Null : Child(host2.Scene, content2, 0);   // the user content row
        float tyRest = content2.IsNull ? 1f : host2.Scene.Paint(content2).LocalTransform.Dy;
        float shiftRest2 = clip2.IsNull ? 1f : host2.Scene.Paint(clip2).ChildShiftY;
        var rootR = host2.Scene.AbsoluteRect(card2);
        var innerR = inner2.IsNull ? default : host2.Scene.AbsoluteRect(inner2);
        bool contained = !inner2.IsNull && innerR.Y + innerR.H <= rootR.Y + rootR.H + 0.5f;
        bool noStaleReveal = float.IsNaN(host2.Scene.Paint(card2).PresentedH) && float.IsNaN(host2.Scene.Paint(clip2).PresentedH);
        Check("cp3.c — initiallyExpanded rests with no motion (zero shift) and the content inside the card bottom (no clipping)",
            MathF.Abs(tyRest) < 0.01f && MathF.Abs(shiftRest2) < 0.01f && contained && noStaleReveal && HasGlyph(device2, strings, "expander-action"),
            $"tyRest={tyRest:0.00} shiftRest={shiftRest2:0.00} innerBottom={(innerR.Y + innerR.H):0} cardBottom={(rootR.Y + rootR.H):0} presentedHNaN={noStaleReveal}");
    }

    // ── D4 — ScrollBar conscious anatomy + AnnotatedScrollBar template geometry ─────────────────────────────────
    // Grounded against microsoft-ui-xaml: ScrollBar_themeresources.xaml (ScrollBarSize 12 :180, thumb min length 30
    // :181, ExpandBeginTime 400ms :188 / ContractBeginTime 500ms :189, Expand/ContractDuration 167ms :173/:176,
    // OpacityChangeDuration 83ms :174, arrow cells ALWAYS in fixed grid rows :703/:711) and AnnotatedScrollBar.xaml +
    // _themeresources.xaml (MinWidth 44 :37/xaml:4, thumb 30×3 r1.5 right-aligned top-anchored xaml:84-92,
    // ScrollButtonStyle 16×16 :43-44, right-aligned labels xaml:8-19).
    static void D4ScrollBarChecks(StringTable strings)
    {
        // ── ScrollBar.Anatomy: reserved arrow cells, instant signal-bound position, debounced 167ms expand ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cp4-sb", new Size2(320, 280), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var pos = new Signal<float>(0f);
            var root = new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Start, Padding = Edges4.All(20f),
                    Children = [ScrollBar.Anatomy(0.25f, pos, p => pos.Value = p, 200f)],
                },
            };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();

            var bar = FindRole(host.Scene, host.Scene.Root, AutomationRole.ScrollBar);
            var column = Child(host.Scene, bar, 2);                 // root ZStack = [track, strip, column(, ticker)]
            var arrowUp = Child(host.Scene, column, 0);             // column = [arrowUp, thumb, grow, arrowDown]
            var thumb = Child(host.Scene, column, 1);
            var barR = host.Scene.AbsoluteRect(bar);
            var t0 = host.Scene.AbsoluteRect(thumb);
            // Length 200 − 2×12 reserved arrow cells → track 176; fraction 0.25 → thumbLen max(30, 44) = 44; travel 132.
            Check("cp4.1 — arrow cells ALWAYS reserved; collapsed thumb 2px, fill right edge inset 3 (stroke-trick math)",
                Near(host.Scene.Bounds(arrowUp).H, 12f) && Near(t0.W, 2f) && Near(t0.H, 44f)
                && Near(t0.Y, barR.Y + 12f) && Near(t0.Right, barR.Right - 3f),
                $"cellH={host.Scene.Bounds(arrowUp).H:0.#} thumb={t0.W:0.#}x{t0.H:0.#} y={t0.Y - barR.Y:0.#} rightInset={barR.Right - t0.Right:0.#}");

            // Position 0 → 0.5 while COLLAPSED: the TransformBind moves the thumb next frame — no 400ms begin-time lag.
            pos.Value = 0.5f;
            host.RunFrame();
            var t1 = host.Scene.AbsoluteRect(thumb);
            Check("cp4.2 — position 0→0.5 moves the thumb the NEXT frame (no expand-delay on position; width untouched)",
                Near(t1.Y, barR.Y + 12f + 66f, 1f) && Near(t1.W, 2f),
                $"y={t1.Y - barR.Y:0.#} (expect 78) w={t1.W:0.#}");

            // Hover the lane: nothing changes immediately (the 400ms dwell debounces the expand)…
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(barR.X + 6f, barR.Y + 100f), 0, 0));
            host.RunFrame();
            bool noInstantExpand = Near(host.Scene.AbsoluteRect(thumb).W, 2f);
            // …then ~45 frames ≈ 720ms of engine time: past the 400ms begin + the 167ms KeySpline(0,0,0,1) width tween.
            for (int i = 0; i < 45; i++) host.RunFrame();
            var t2 = host.Scene.AbsoluteRect(thumb);
            Check("cp4.3 — lane dwell 400ms then 167ms expand: thumb 2px→6px, right edge stays anchored (inset 3)",
                noInstantExpand && Near(t2.W, 6f) && Near(t2.Right, barR.Right - 3f),
                $"instantExpand={!noInstantExpand} w={t2.W:0.#} rightInset={barR.Right - t2.Right:0.#}");
            Check("cp4.4 — hovering changes neither thumb Y/length nor the track (no geometry jump from arrow cells)",
                Near(t2.Y, t1.Y) && Near(t2.H, t1.H) && Near(host.Scene.Bounds(arrowUp).H, 12f),
                $"y={t2.Y - barR.Y:0.#} len={t2.H:0.#} cellH={host.Scene.Bounds(arrowUp).H:0.#}");
            Check("cp4.5 — expanded chrome faded IN (arrow opacity 1 — the 83ms linear fade after the same begin time)",
                Near(host.Scene.Paint(arrowUp).Opacity, 1f, 0.02f),
                $"opacity={host.Scene.Paint(arrowUp).Opacity:0.00}");

            // Leave the bar: the contract begins after 500ms and plays 167ms; the chrome fades back out.
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(barR.Right + 80f, barR.Y + 100f), 0, 0));
            host.RunFrame();
            for (int i = 0; i < 55; i++) host.RunFrame();
            var t3 = host.Scene.AbsoluteRect(thumb);
            Check("cp4.6 — leave contracts to 2px after the 500ms begin; chrome fades out",
                Near(t3.W, 2f) && Near(host.Scene.Paint(arrowUp).Opacity, 0f, 0.02f),
                $"w={t3.W:0.#} opacity={host.Scene.Paint(arrowUp).Opacity:0.00}");
            Check("cp4.7 — conscious ticker unmounts: the frame loop idles once settled", !host.HasActiveWork);
        }

        // ── AnnotatedScrollBar: 44px right-rail template geometry + jump/step interactions ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cp4-asb", new Size2(360, 340), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var pos = new Signal<float>(0.2f);
            var lastKind = (AnnotatedScrollBarScrollKind)255;
            var root = new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Start, Padding = Edges4.All(20f),
                    Children =
                    [
                        AnnotatedScrollBar.Create(new[] { ("A", 0.04f), ("M", 0.5f), ("Z", 0.96f) },
                            pos, (to, kind) => { pos.Value = to; lastKind = kind; }, height: 280f),
                    ],
                },
            };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();

            var asb = FindRole(host.Scene, host.Scene.Root, AutomationRole.ScrollBar);
            var asbR = host.Scene.AbsoluteRect(asb);
            var btnUp = Child(host.Scene, asb, 0);                  // root column = [btnUp, rail, btnDown]
            var rail = Child(host.Scene, asb, 1);
            var railR = host.Scene.AbsoluteRect(rail);
            var thumb = Child(host.Scene, host.Scene.LastChild(rail), 0);   // last rail layer = the live-thumb row
            var thumbR = host.Scene.AbsoluteRect(thumb);
            // Height 280 − 2×16 button cells → rail 248; pos 0.2 → thumb Y = 0.2 × (248−3) = 49.
            Check("cp4.8 — AnnotatedScrollBar hugs the 44px LabelsGridMinWidth (no full-width panel)",
                Near(asbR.W, 44f, 0.6f), $"w={asbR.W:0.#}");
            Check("cp4.9 — 30×3 accent thumb, right edge == rail right edge, Y proportional to position",
                Near(thumbR.W, 30f) && Near(thumbR.H, 3f) && Near(thumbR.Right, railR.Right, 0.6f)
                && Near(thumbR.Y, railR.Y + 49f, 1f),
                $"thumb={thumbR.W:0.#}x{thumbR.H:0.#} rightGap={railR.Right - thumbR.Right:0.#} y={thumbR.Y - railR.Y:0.#} (expect 49)");
            var mLabel = FindTextNode(host.Scene, strings, asb, "M");
            var mR = mLabel.IsNull ? default(RectF) : host.Scene.AbsoluteRect(mLabel);
            Check("cp4.10 — label text right-aligned to the labels-column edge, up-shifted 5 (LabelTemplate margin)",
                !mLabel.IsNull && Near(mR.Right, railR.Right, 1.5f) && Near(mR.Y, railR.Y + 119f, 1.5f),
                $"rightGap={railR.Right - mR.Right:0.#} y={mR.Y - railR.Y:0.#} (expect 119)");

            // Hover the rail → the ghost preview row mounts (PART_VerticalThumbGhost).
            int layers0 = host.Scene.ChildCount(rail);
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(railR.X + 22f, railR.Y + 60f), 0, 0));
            host.RunFrame();
            Check("cp4.11 — rail hover mounts the ghost thumb preview", host.Scene.ChildCount(rail) == layers0 + 1,
                $"layers {layers0}→{host.Scene.ChildCount(rail)}");

            // Rail click-to-jump: local Y 124 of 248 → position 0.5, kind=Click; the thumb follows the same flush.
            window.QueueInput(new InputEvent(InputKind.PointerDown, new Point2(railR.X + 22f, railR.Y + 124f), 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(railR.X + 22f, railR.Y + 124f), 0, 0));
            host.RunFrame();
            thumb = Child(host.Scene, host.Scene.LastChild(rail), 0);
            var thumbR2 = host.Scene.AbsoluteRect(thumb);
            Check("cp4.12 — rail click jumps: onScroll(0.5, Click), thumb lands at 0.5 × (railH−3) the next frame",
                Near(pos.Peek(), 0.5f, 0.01f) && lastKind == AnnotatedScrollBarScrollKind.Click
                && Near(thumbR2.Y, railR.Y + 122.5f, 1f),
                $"pos={pos.Peek():0.00} kind={lastKind} y={thumbR2.Y - railR.Y:0.#} (expect 122.5)");

            // Top (increment) ScrollButton: a 16×16 right-aligned transparent cell stepping by SmallChange (0.05).
            var b = host.Scene.AbsoluteRect(btnUp);
            Check("cp4.13 — ScrollButtons are 16×16 right-aligned cells",
                Near(b.W, 16f) && Near(b.H, 16f) && Near(b.Right, asbR.Right, 0.6f),
                $"btn={b.W:0.#}x{b.H:0.#} rightGap={asbR.Right - b.Right:0.#}");
            window.QueueInput(new InputEvent(InputKind.PointerDown, CenterOf(host.Scene, btnUp), 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerUp, CenterOf(host.Scene, btnUp), 0, 0));
            host.RunFrame();
            Check("cp4.14 — increment button steps −0.05 with kind=IncrementButton",
                Near(pos.Peek(), 0.45f, 0.01f) && lastKind == AnnotatedScrollBarScrollKind.IncrementButton,
                $"pos={pos.Peek():0.00} kind={lastKind}");
        }
    }

    // D5 — editable ComboBox parity: ONE chrome-owning box (ComboBox_themeresources.xaml:571 Background spans both
    // columns; :580 TextBox part BorderBrush=Transparent), DropDownOverlay 30w/margin-4/corner-4 (:581) with the
    // 12×12 E70D glyph inset 14 painted above it (:582–587), the focused 2px accent bottom bar on the OUTER box
    // (TextControlBorderThemeThicknessFocused 1,1,1,2), and the corner-joined dropdown (rows margin 5,2,5,2 corner 3
    // padding 11,_,11,_ + the flush selection pill; surface OverlayCornerRadius 8).
    static void D5EditableComboBoxChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("d5-cmb", new Size2(420, 320), 1f)); window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var root = new ComboProbe(true);
        using var host = new AppHost(app, window, device, fonts, strings, root);
        host.RunFrame();
        var scene = host.Scene;
        void Settle() { for (int i = 0; i < 16; i++) host.RunFrame(); }

        // cp5.a — exactly ONE bordered node in the field subtree; the EditableText part is chromeless at rest.
        var combo = FindRole(scene, scene.Root, AutomationRole.ComboBox);
        var field = FindRole(scene, scene.Root, AutomationRole.Text);
        int bordered = 0;
        void CountBorders(NodeHandle n)
        {
            if (n.IsNull) return;
            if (scene.Paint(n).BorderWidth > 0f) bordered++;
            for (var c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c)) CountBorders(c);
        }
        CountBorders(combo);
        bool partChromeless = Near(scene.Paint(field).BorderWidth, 0f, 0.01f) && scene.Paint(field).Fill.A == 0f
            && Near(scene.Paint(field).Corners.TopLeft, 0f, 0.01f);
        Check("cp5.a — editable combo: ONE bordered box owns the chrome; the EditableText part paints no border/fill/corners at rest",
            bordered == 1 && Near(scene.Paint(combo).BorderWidth, 1f, 0.01f) && partChromeless,
            $"bordered={bordered} outerBw={scene.Paint(combo).BorderWidth:0.##} partBw={scene.Paint(field).BorderWidth:0.##} partFillA={scene.Paint(field).Fill.A:0.##}");

        // cp5.b — DropDownGlyph 12×12 right-inset 14 (hit-test-invisible); DropDownOverlay 30 wide, margin 4; the
        // part spans the FULL field width (ComboBoxEditableTextPadding right 38 keeps text clear of the column).
        var comboR = scene.AbsoluteRect(combo);
        var fieldR = scene.AbsoluteRect(field);
        var glyphText = FindTextNode(scene, strings, combo, Icons.ChevronDown);
        var glyphBox = glyphText.IsNull ? NodeHandle.Null : scene.Parent(glyphText);
        var glyphR = glyphBox.IsNull ? default(RectF) : scene.AbsoluteRect(glyphBox);
        var overlayBtn = FindRole(scene, combo, AutomationRole.Button);
        var ovR = overlayBtn.IsNull ? default(RectF) : scene.AbsoluteRect(overlayBtn);
        Check("cp5.b — glyph 12×12 right-inset 14; overlay button width 30 margin 4; the part spans the full field width",
            !glyphBox.IsNull && Near(glyphR.W, 12f) && Near(glyphR.H, 12f) && Near(comboR.Right - glyphR.Right, 14f)
            && !overlayBtn.IsNull && Near(ovR.W, 30f) && Near(ovR.H, comboR.H - 8f)
            && Near(comboR.Right - ovR.Right, 4f) && Near(ovR.Y - comboR.Y, 4f)
            && Near(fieldR.W, comboR.W),
            $"glyph={glyphR.W:0.#}x{glyphR.H:0.#}@right-{comboR.Right - glyphR.Right:0.#} overlay={ovR.W:0.#}x{ovR.H:0.#}@right-{comboR.Right - ovR.Right:0.#}/top+{ovR.Y - comboR.Y:0.#} fieldW={fieldR.W:0.#}/{comboR.W:0.#}");

        // cp5.c — focus the part: the 2px accent bottom bar sits on the OUTER box; the part paints input-active fill.
        ClickNode(host, window, field);
        host.RunFrame();
        combo = FindRole(scene, scene.Root, AutomationRole.ComboBox);
        field = FindRole(scene, scene.Root, AutomationRole.Text);
        comboR = scene.AbsoluteRect(combo);
        var bar = NodeHandle.Null;
        for (var c = scene.FirstChild(combo); !c.IsNull; c = scene.NextSibling(c))
            if (Near(scene.AbsoluteRect(c).H, 2f) && ColorClose(scene.Paint(c).Fill, Tok.AccentDefault, 0.004f)) bar = c;
        var barR = bar.IsNull ? default(RectF) : scene.AbsoluteRect(bar);
        bool inputActive = ColorClose(scene.Paint(field).Fill, Tok.FillControlInputActive, 0.004f);
        Check("cp5.c — focused editable combo: 2px accent bottom bar on the OUTER box (TextControlBorderThemeThicknessFocused 1,1,1,2) + input-active part fill",
            (scene.Flags(field) & NodeFlags.Focused) != 0 && !bar.IsNull
            && Near(barR.W, comboR.W) && Near(barR.Bottom, comboR.Bottom) && inputActive,
            $"focused={(scene.Flags(field) & NodeFlags.Focused) != 0} bar={!bar.IsNull} barW={barR.W:0.#}/{comboR.W:0.#} bottomGap={comboR.Bottom - barR.Bottom:0.#} inputActive={inputActive}");

        // cp5.d — clicking the overlay opens the dropdown and the FIELD keeps focus (the overlay is no focus target).
        root.Sel!.Value = 1;   // give the open list a selected row (pill)
        host.RunFrame();
        var overlay2 = FindRole(scene, FindRole(scene, scene.Root, AutomationRole.ComboBox), AutomationRole.Button);
        ClickNode(host, window, overlay2);
        host.RunFrame();
        var rows = Roles(scene, AutomationRole.MenuItem);
        field = FindRole(scene, scene.Root, AutomationRole.Text);
        Check("cp5.d — overlay click opens the dropdown; the text field stays focused",
            rows.Count == 3 && (scene.Flags(field) & NodeFlags.Focused) != 0,
            $"rows={rows.Count} fieldFocused={(scene.Flags(field) & NodeFlags.Focused) != 0}");

        // cp5.e — dropdown anatomy: rows margin 5,2,5,2 + corner 3 + content inset 11; selection pill 3×16 r1.5
        // accent FLUSH left; surface corner 8 corner-joined (popup tops squared, field bottoms squared).
        bool rowsOk = false, pillOk = false, joinOk = false;
        if (rows.Count == 3)
        {
            var r0 = scene.AbsoluteRect(rows[0]);
            var r1 = scene.AbsoluteRect(rows[1]);
            var surf = rows[0];
            while (!surf.IsNull && scene.Paint(surf).BorderWidth < 0.5f) surf = scene.Parent(surf);
            var surfR = surf.IsNull ? default(RectF) : scene.AbsoluteRect(surf);
            var label = FindTextNode(scene, strings, rows[0], "Red");
            float labelInset = label.IsNull ? -1f : scene.AbsoluteRect(label).X - r0.X;
            rowsOk = !surf.IsNull
                && Near(r0.X - surfR.X, 5f, 1f) && Near(surfR.Right - r0.Right, 5f, 1f)   // LayoutRoot Margin 5,_,5,_
                && Near(r1.Y - r0.Bottom, 4f, 0.6f)                                        // 2 + 2 vertical margins
                && Near(scene.Paint(rows[0]).Corners.TopLeft, 3f, 0.01f)                   // ComboBoxItemCornerRadius
                && Near(labelInset, 11f, 1f);                                              // ComboBoxItemThemePadding 11
            NodeHandle pill = NodeHandle.Null;
            void FindPill(NodeHandle n)
            {
                if (n.IsNull) return;
                var rr = scene.AbsoluteRect(n);
                if (Near(rr.W, 3f) && Near(rr.H, 16f)) pill = n;
                for (var c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c)) FindPill(c);
            }
            FindPill(rows[1]);
            pillOk = !pill.IsNull && ColorClose(scene.Paint(pill).Fill, Tok.AccentDefault, 0.004f)
                && Near(scene.Paint(pill).Corners.TopLeft, 1.5f, 0.01f)
                && Near(scene.AbsoluteRect(pill).X - r1.X, 0f, 0.6f);                      // ITEM pill is FLUSH (:759)
            combo = FindRole(scene, scene.Root, AutomationRole.ComboBox);
            joinOk = !surf.IsNull
                && Near(scene.Paint(surf).Corners.TopLeft, 0f, 0.01f) && Near(scene.Paint(surf).Corners.BottomLeft, Radii.Overlay, 0.01f)
                && Near(scene.Paint(combo).Corners.TopLeft, Radii.Control, 0.01f) && Near(scene.Paint(combo).Corners.BottomLeft, 0f, 0.01f);
        }
        Check("cp5.e — dropdown rows margin 5,2,5,2 corner 3 inset 11 + flush selection pill 3×16 r1.5; surface corner 8 corner-joined to the field",
            rowsOk && pillOk && joinOk, $"rows={rowsOk} pill={pillOk} join={joinOk}");

        // cp5.f — a second overlay-position click closes (toggle); the field keeps focus through the close.
        var overlay3 = FindRole(scene, FindRole(scene, scene.Root, AutomationRole.ComboBox), AutomationRole.Button);
        ClickNode(host, window, overlay3);
        Settle();
        field = FindRole(scene, scene.Root, AutomationRole.Text);
        Check("cp5.f — overlay click while open closes the dropdown; the field stays focused",
            Roles(scene, AutomationRole.MenuItem).Count == 0 && (scene.Flags(field) & NodeFlags.Focused) != 0,
            $"rows={Roles(scene, AutomationRole.MenuItem).Count} fieldFocused={(scene.Flags(field) & NodeFlags.Focused) != 0}");
    }

    // ── D67 — SplitButton/DropDownButton menu placement + per-kind flyout/menu motion (cp6.* / cp7.*) ─────────
    // WinUI grounding: SplitButton.xaml:220-222 (35 / 1px / 35 columns) + SplitButton_themeresources.xaml:101-103
    // (sizes; SplitButtonPadding 11,6,11,7); SplitButtonStyle HorizontalAlignment=Left (SplitButton.xaml:8);
    // FlyoutBase::FlyoutMargin = 4 below the anchor (FlyoutBase_partial.cpp:65 + :2630-2631). Menu open =
    // MenuPopupThemeTransition load: clip + content translate ±H·ClosedRatio(0.5) → 0 over 250ms cubic-bezier(0,0,0,1)
    // PLUS the presenter-plate ("MenuFlyoutPresenterBorder", generic.xaml:23810) ScaleY (1−ClosedRatio) → 1 pivoted at
    // the anchor-FAR edge (CenterY=openedLength when direction==AnimationDirection_Top = the DOWNWARD menu,
    // LayoutTransition_partial.cpp:441-506 + MenuFlyout_Partial.cpp:259); unload = 83ms linear fade with the clip and
    // plate FROZEN (cpp:525-544). Dropdown seam = SplitOpen/SplitClose (ThemeAnimations.cpp:596-721 / 733-857): a
    // band centred on the seam (ClipTranslateY = OffsetFromCenter immediately) growing to (0.5+|off/H|)·2 over 250ms
    // (opacity pinned 1, content translate 0) and collapsing to closedRatio 0.15 over 167ms with the fade only in the
    // last 83ms (s_OpacityChangeBeginTime = 84, SplitCloseThemeAnimation_Partial.h:16-18).
    static void D67SplitButtonFlyoutChecks(StringTable strings)
    {
        // Menu chrome (D67) splits the FlyoutSurface: acrylic+stroke+shadow live on the stretch PLATE (first child of
        // the surface); the open/close channels live on the plate's PARENT (the transparent ZStack surface).
        static NodeHandle SurfaceOf(SceneStore sc, NodeHandle n)
        {
            for (; !n.IsNull; n = sc.Parent(n))
            {
                if (sc.TryGetAcrylic(n, out _)) return n;
                for (var c = sc.FirstChild(n); !c.IsNull; c = sc.NextSibling(c))
                    if (sc.TryGetAcrylic(c, out _)) return n;
            }
            return NodeHandle.Null;
        }

        // cp6.b — SplitButton extents: primary h32 (and ≥35 wide), secondary 35×32, divider 1px, root MinHeight 32 +
        // hug-left (HorizontalAlignment=Left → AlignSelf.Start).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cp6ext", new Size2(360, 160), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            using var host = new AppHost(app, window, device, fonts, strings, new SplitButtonProbe());
            host.RunFrame();
            var btns = Roles(host.Scene, AutomationRole.Button);
            var primary = btns.Count > 0 ? btns[0] : NodeHandle.Null;
            var drop = btns.Count > 1 ? btns[1] : NodeHandle.Null;
            var outer = primary.IsNull ? NodeHandle.Null : host.Scene.Parent(primary);
            var divider = outer.IsNull ? NodeHandle.Null : Child(host.Scene, outer, 1);
            bool primH = !primary.IsNull && Near(host.Scene.Bounds(primary).H, 32f);
            bool dropExt = !drop.IsNull && Near(host.Scene.Bounds(drop).W, 35f) && Near(host.Scene.Bounds(drop).H, 32f);
            bool divExt = !divider.IsNull && Near(host.Scene.Bounds(divider).W, 1f);
            bool primMin = !primary.IsNull && host.Scene.Bounds(primary).W >= 34.5f;
            bool rootSpec = !outer.IsNull && Near(host.Scene.Layout(outer).MinH, 32f)
                            && host.Scene.Layout(outer).AlignSelf == FlexAlign.Start;   // SplitButton.xaml:8 HorizontalAlignment=Left
            Check("cp6.b — SplitButton extents: primary h32 ≥35w, secondary 35×32, divider 1px, root MinHeight 32 + hug-left",
                primH && dropExt && divExt && primMin && rootSpec,
                $"primary={(primary.IsNull ? 0 : host.Scene.Bounds(primary).W):0}×{(primary.IsNull ? 0 : host.Scene.Bounds(primary).H):0} " +
                $"drop={(drop.IsNull ? 0 : host.Scene.Bounds(drop).W):0}×{(drop.IsNull ? 0 : host.Scene.Bounds(drop).H):0} " +
                $"div={(divider.IsNull ? 0 : host.Scene.Bounds(divider).W):0} rootSpec={rootSpec}");
        }

        // cp6.a — live SplitButton: the menu wrapper lands at (anchor.X, anchor.Bottom+4) and the VISIBLE (post-clip)
        // top edge never rises above that line mid-unfold; menus do not fade at open.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cp6live", new Size2(480, 400), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var clock = new ManualFrameTimeSource();
            using var host = new AppHost(app, window, device, fonts, strings, new SplitButtonLongMenuProbe(), frameTime: clock);
            host.RunFrame();

            var halves = Roles(host.Scene, AutomationRole.Button);
            var primary = halves.Count > 0 ? halves[0] : NodeHandle.Null;
            var secondary = halves.Count > 1 ? halves[1] : NodeHandle.Null;
            var anchorRect = primary.IsNull ? default : host.Scene.AbsoluteRect(host.Scene.Parent(primary));
            if (!secondary.IsNull) ClickNode(host, window, secondary);   // open (mount + place + seed in the layout effect)
            host.RunFrame();                                             // compose the t=0 keyframes
            var surface = SurfaceOf(host.Scene, FindRole(host.Scene, host.Scene.Root, AutomationRole.MenuItem));
            var wrapper = surface.IsNull ? NodeHandle.Null : host.Scene.Parent(surface);
            float expX = anchorRect.X, expY = anchorRect.Bottom + 4f;    // FlyoutBase::FlyoutMargin = 4
            float minVisTop = float.MaxValue;
            bool opaqueAllTheWay = !surface.IsNull;
            for (int i = 0; i < 6 && !surface.IsNull; i++)
            {
                var sp = host.Scene.Paint(surface);
                float visTop = host.Scene.AbsoluteRect(surface).Y + (sp.ClipRect.IsInfinite ? 0f : sp.ClipRect.Y);
                minVisTop = MathF.Min(minVisTop, visTop);
                if (sp.Opacity < 0.99f) opaqueAllTheWay = false;
                clock.Advance(16f); host.RunFrame();
            }
            for (int i = 0; i < 24; i++) { clock.Advance(16f); host.RunFrame(); }   // > 250ms → settled
            var wr = wrapper.IsNull ? default : host.Scene.AbsoluteRect(wrapper);
            bool placed = !wrapper.IsNull && Near(wr.X, expX, 0.75f) && Near(wr.Y, expY, 0.75f);
            bool neverAbove = !surface.IsNull && minVisTop >= expY - 0.75f;
            var pEnd = surface.IsNull ? default : host.Scene.Paint(surface);
            bool settled = !surface.IsNull && pEnd.ClipRect.IsInfinite && Near(pEnd.LocalTransform.Dy, 0f, 0.1f);
            Check("cp6.a — SplitButton menu: wrapper at (anchor.X, anchor.Bottom+4); visible top never above it mid-open; no open fade",
                placed && neverAbove && opaqueAllTheWay && settled,
                $"wrapper=({wr.X:0.0},{wr.Y:0.0}) exp=({expX:0.0},{expY:0.0}) minVisTop={minVisTop:0.0} opaque={opaqueAllTheWay} settled={settled}");
        }

        // cp6.c + cp7.d/e/f/g — the OverlayHost motion paths against the OverlayProbe's 120×32 anchor at (20,20).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("d67motion", new Size2(480, 400), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new OverlayProbe();
            var clock = new ManualFrameTimeSource();
            using var host = new AppHost(app, window, device, fonts, strings, root, frameTime: clock);
            host.RunFrame();
            var svc = root.Service!;
            void Settle() { for (int i = 0; i < 40; i++) { clock.Advance(16f); host.RunFrame(); } }
            var anchorRect = host.Scene.AbsoluteRect(root.Anchor);

            // cp6.c — the EXACT open call DropDownButton/SplitButton make (BottomLeft + FocusTrap + windowed): placed
            // at (anchor.X, anchor.Bottom+4); menu-kind open channels = TranslateY<0 + ClipT>0 with ClipB resting at
            // the box bottom (edge reveal, NOT seam) and opacity pinned 1.
            {
                var hd = svc.Open(() => root.Anchor,
                    () => MenuFlyout.Build(new[] { new MenuFlyoutItem("One"), new MenuFlyoutItem("Two") }, () => svc.CloseTop()),
                    FlyoutPlacement.BottomLeft,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
                host.RunFrame();   // mount + place + seed
                host.RunFrame();   // compose t=0
                var s = SurfaceOf(host.Scene, FindRole(host.Scene, host.Scene.Root, AutomationRole.MenuItem));
                var wrapper = s.IsNull ? NodeHandle.Null : host.Scene.Parent(s);
                var wr = wrapper.IsNull ? default : host.Scene.AbsoluteRect(wrapper);
                var p0 = s.IsNull ? default : host.Scene.Paint(s);
                float sh = s.IsNull ? 0f : host.Scene.Bounds(s).H;
                bool placed = !wrapper.IsNull && Near(wr.X, anchorRect.X, 0.75f) && Near(wr.Y, anchorRect.Bottom + 4f, 0.75f);
                bool channels = !s.IsNull && p0.LocalTransform.Dy < -1f && !p0.ClipRect.IsInfinite
                    && p0.ClipRect.Y > 1f && Near(p0.ClipRect.Bottom, sh, 1f) && p0.Opacity > 0.99f;
                hd.Close();
                Settle();
                Check("cp6.c — DropDownButton-path menu: placed at (anchor.X, anchor.Bottom+4); open channels TranslateY+ClipT, opacity 1",
                    placed && channels,
                    $"wrapper=({wr.X:0.0},{wr.Y:0.0}) exp=({anchorRect.X:0.0},{anchorRect.Bottom + 4f:0.0}) dy={p0.LocalTransform.Dy:0.0} " +
                    $"clipT={p0.ClipRect.Y:0.0} clipB={p0.ClipRect.Bottom:0.0}/{sh:0.0} op={p0.Opacity:0.00}");
            }

            // cp7.d — menu plate (WinUI MenuFlyoutPresenterBorder ScaleY): mid-flight STRICTLY between (1−ratio) and 1
            // about the bottom pivot (downward menu), settled at 1 by 250ms; the surface stays opaque, TranslateY<0.
            {
                svc.Open(() => root.Anchor,
                    () => MenuFlyout.Build(new[] { new MenuFlyoutItem("One"), new MenuFlyoutItem("Two"), new MenuFlyoutItem("Three") }, () => svc.CloseTop()),
                    FlyoutPlacement.BottomLeft);
                host.RunFrame();
                host.RunFrame();   // t=0
                var s = SurfaceOf(host.Scene, FindRole(host.Scene, host.Scene.Root, AutomationRole.MenuItem));
                var plate = s.IsNull ? NodeHandle.Null : host.Scene.FirstChild(s);
                bool plateChrome = !plate.IsNull && host.Scene.TryGetAcrylic(plate, out _) && Near(host.Scene.Paint(plate).BorderWidth, 1f);
                clock.Advance(16f); host.RunFrame();   // mid-flight (E(16/250)=0.3517 → scale ≈ 0.676)
                var sp = s.IsNull ? default : host.Scene.Paint(s);
                float midScale = plate.IsNull ? 0f : host.Scene.Paint(plate).LocalTransform.M22;
                bool mid = !plate.IsNull && midScale > 0.51f && midScale < 0.99f && sp.Opacity > 0.99f && sp.LocalTransform.Dy < -1f;
                bool pivot = !plate.IsNull && Near(host.Scene.Paint(plate).OriginY, 1f, 0.01f);   // opens DOWN → CenterY=openedLength (bottom)
                for (int i = 0; i < 24; i++) { clock.Advance(16f); host.RunFrame(); }            // > 250ms
                bool settledScale = !plate.IsNull && Near(host.Scene.Paint(plate).LocalTransform.M22, 1f, 0.01f);
                svc.CloseTop();
                Settle();
                Check("cp7.d — menu plate ScaleY (1−ratio)→1 over 250ms about the bottom pivot; opacity 1 + TranslateY<0 mid-flight",
                    plateChrome && mid && pivot && settledScale,
                    $"plate={plateChrome} mid={midScale:0.000} pivot={pivot} settled={settledScale} op={sp.Opacity:0.00} dy={sp.LocalTransform.Dy:0.0}");
            }

            // cp7.e — Dropdown SEAM (SplitOpen/SplitClose around the selected-row centre): both clip edges animate, the
            // band stays centred on the seam, content TranslateY stays 0, no open fade; close collapses toward the
            // seam with the fade only in the last 83ms (begin 84ms).
            {
                NodeHandle body = NodeHandle.Null;
                svc.Open(() => root.Anchor,
                    () => new BoxEl { Width = 240f, Height = 120f, Fill = Tok.FillCardDefault, OnRealized = h => body = h },
                    FlyoutPlacement.BottomLeft,
                    new PopupOptions(Chrome: PopupChrome.Dropdown) { SeamOffsetY = 20f });
                host.RunFrame();
                host.RunFrame();   // t=0
                var s = SurfaceOf(host.Scene, body);
                float H = s.IsNull ? 0f : host.Scene.Bounds(s).H;            // body 120 + presenter padding (0,2,0,2)
                float c = H * 0.5f + 20f;
                float half0 = MathF.Max(0.25f * H, 20f);                     // ThemeAnimations.cpp:655-668
                float halfF = H * 0.5f + 20f;                                // cpp:674
                var p0 = s.IsNull ? default : host.Scene.Paint(s);
                bool t0 = !s.IsNull && !p0.ClipRect.IsInfinite
                    && Near(p0.ClipRect.Y, c - half0, 1.5f) && Near(p0.ClipRect.Bottom, c + half0, 1.5f)
                    && p0.ClipRect.Y > 0.5f && p0.ClipRect.Bottom < H - 0.5f          // seam-centred: ClipT>0 AND ClipB<H
                    && Near(p0.LocalTransform.Dy, 0f, 0.1f) && p0.Opacity > 0.99f;    // content translate 0, no fade
                clock.Advance(16f); host.RunFrame();
                var p1 = s.IsNull ? default : host.Scene.Paint(s);
                bool bothMove = !s.IsNull && !p1.ClipRect.IsInfinite
                    && p1.ClipRect.Y < p0.ClipRect.Y - 1f && p1.ClipRect.Y > 0.5f
                    && p1.ClipRect.Bottom > p0.ClipRect.Bottom + 1f
                    && Near(p1.LocalTransform.Dy, 0f, 0.1f) && p1.Opacity > 0.99f;
                for (int i = 0; i < 24; i++) { clock.Advance(16f); host.RunFrame(); }
                bool opened = !s.IsNull && host.Scene.Paint(s).ClipRect.IsInfinite;
                svc.CloseTop(); host.RunFrame();
                clock.Advance(48f); host.RunFrame();                          // t=48 < 84ms begin → still opaque
                float halfClose = MathF.Max(0.075f * H, 20f - 0.35f * H);     // cpp:798-811 (compensation inactive here)
                var c48 = s.IsNull ? default : host.Scene.Paint(s);
                float e48 = 0.7320f;                                          // E(48/167) for cubic(0,0,0,1)
                bool close48 = !s.IsNull && c48.Opacity > 0.99f
                    && Near(c48.ClipRect.Y, (c - halfClose) * e48, 2f)
                    && Near(c48.ClipRect.Bottom, H + (c + halfClose - H) * e48, 2f)
                    && (c48.ClipRect.Y + c48.ClipRect.Bottom) * 0.5f > (H * 0.5f + 5f);   // midpoint moving toward the seam
                clock.Advance(64f); host.RunFrame();                          // t=112 → fade (112−84)/83 = 0.3373
                float op112 = !s.IsNull && host.Scene.IsLive(s) ? host.Scene.Paint(s).Opacity : -1f;
                bool close112 = Near(op112, 1f - (112f - 84f) / 83f, 0.05f);
                Settle();
                Check("cp7.e — Dropdown seam: SplitOpen band centred on the seam (ClipT>0 ∧ ClipB<H, translate 0, no fade); SplitClose collapses toward it, fade after 84ms",
                    t0 && bothMove && opened && close48 && close112,
                    $"t0={t0} (clipT={p0.ClipRect.Y:0.0}≈{c - half0:0.0} clipB={p0.ClipRect.Bottom:0.0}≈{c + half0:0.0}/{H:0.0}) both={bothMove} " +
                    $"opened={opened} close48={close48} (op={c48.Opacity:0.00} T={c48.ClipRect.Y:0.0} B={c48.ClipRect.Bottom:0.0}) close112={close112} (op={op112:0.000})");
            }

            // cp7.f — plain Flyout (PopupChrome.Popup, TAS_SHOWPOPUP): opacity HELD at 0 through t=40ms, mid fade at
            // t=120ms ((120−83)/83 = 0.446), TranslateY decaying from −50 toward 0 the whole time.
            {
                NodeHandle body = NodeHandle.Null;
                svc.Open(() => root.Anchor,
                    () => new BoxEl { Width = 240f, Height = 88f, Fill = Tok.FillCardDefault, OnRealized = h => body = h },
                    FlyoutPlacement.BottomLeft, new PopupOptions(Chrome: PopupChrome.Popup));
                host.RunFrame();
                host.RunFrame();   // t=0
                var s = SurfaceOf(host.Scene, body);
                var p0 = s.IsNull ? default : host.Scene.Paint(s);
                bool t0 = !s.IsNull && Near(p0.LocalTransform.Dy, -50f, 1f) && p0.Opacity < 0.01f;
                clock.Advance(40f); host.RunFrame();
                var p40 = s.IsNull ? default : host.Scene.Paint(s);
                bool t40 = !s.IsNull && p40.Opacity < 0.01f && p40.LocalTransform.Dy > -49f && p40.LocalTransform.Dy < -1f;
                clock.Advance(80f); host.RunFrame();   // t=120
                var p120 = s.IsNull ? default : host.Scene.Paint(s);
                bool t120 = !s.IsNull && Near(p120.Opacity, (120f - 83f) / 83f, 0.05f)
                    && p120.LocalTransform.Dy > p40.LocalTransform.Dy + 0.5f && p120.LocalTransform.Dy < 0f;
                svc.CloseTop();
                Settle();
                Check("cp7.f — plain Flyout: opacity held 0 at t=40, 0<opacity<1 at t=120, TranslateY decaying from −50",
                    t0 && t40 && t120,
                    $"t0={t0} (dy={p0.LocalTransform.Dy:0.0} op={p0.Opacity:0.00}) t40={t40} (dy={p40.LocalTransform.Dy:0.0} op={p40.Opacity:0.00}) " +
                    $"t120={t120} (dy={p120.LocalTransform.Dy:0.0} op={p120.Opacity:0.000})");
            }

            // cp7.g — menu close mid-open: 83ms linear fade with the clip AND plate frozen at the interrupt offset;
            // the entry finalizes once the fade settles.
            {
                svc.Open(() => root.Anchor,
                    () => MenuFlyout.Build(new[] { new MenuFlyoutItem("One"), new MenuFlyoutItem("Two") }, () => svc.CloseTop()),
                    FlyoutPlacement.BottomLeft);
                host.RunFrame();
                host.RunFrame();                       // t=0
                clock.Advance(64f); host.RunFrame();   // mid-open
                var s = SurfaceOf(host.Scene, FindRole(host.Scene, host.Scene.Root, AutomationRole.MenuItem));
                var plate = s.IsNull ? NodeHandle.Null : host.Scene.FirstChild(s);
                svc.CloseTop(); host.RunFrame();       // freeze (cancel) the load tracks + seed the 83ms fade
                var f0 = s.IsNull ? default : host.Scene.Paint(s);
                float frozenT = f0.ClipRect.IsInfinite ? -1f : f0.ClipRect.Y;
                float frozenScale = plate.IsNull ? -1f : host.Scene.Paint(plate).LocalTransform.M22;
                clock.Advance(32f); host.RunFrame();   // 32ms into the fade → opacity ≈ 1−32/83 = 0.614
                var f1 = s.IsNull ? default : host.Scene.Paint(s);
                bool frozen = !s.IsNull && frozenT > 1f && !f1.ClipRect.IsInfinite && Near(f1.ClipRect.Y, frozenT, 0.01f)
                    && !plate.IsNull && frozenScale > 0.5f && frozenScale < 1f
                    && Near(host.Scene.Paint(plate).LocalTransform.M22, frozenScale, 0.001f);
                bool fading = !s.IsNull && Near(f1.Opacity, 1f - 32f / 83f, 0.03f);
                clock.Advance(64f); host.RunFrame();   // 96ms > 83 → fade settled
                for (int i = 0; i < 6; i++) { clock.Advance(16f); host.RunFrame(); }
                bool finalized = FindRole(host.Scene, host.Scene.Root, AutomationRole.MenuItem).IsNull;
                Check("cp7.g — menu close: 83ms linear fade with clip + plate frozen; entry finalized after settle",
                    frozen && fading && finalized,
                    $"frozenT={frozenT:0.0} clipNow={(f1.ClipRect.IsInfinite ? -1f : f1.ClipRect.Y):0.0} plate={frozenScale:0.000} op32={f1.Opacity:0.000}≈{1f - 32f / 83f:0.000} finalized={finalized}");
            }
        }
    }

    // ── Wave C — text pipeline parity: numeric FontWeight end-to-end, CharacterSpacing, LineHeight +
    // LineStackingStrategy, TextLineBounds=Tight, and the WinUI type-ramp values (TextBlock_themeresources.xaml:3-51).
    // Headless advance model (HeadlessFontSystem.cs:8-18): advance = size×(weight≥600 ? 0.62 : 0.55) +
    // size×CharSpacing/1000; natural line = 1.4×size, baseline = 1.1×size; Tight box = cap..baseline = 0.7×size.
    static void WaveCTextPipelineChecks(StringTable strings)
    {
        // (a)+(b) the numeric weight threads TextEl → TextStyle → the DrawGlyphRun op; Bold sugar resolves to 700,
        // the default to 400, and an explicit Weight beats Bold (the TextEl.ResolvedWeight rule).
        var scene = new SceneStore();
        new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
        {
            Direction = 1,
            Children =
            [
                new TextEl("semibold") { Weight = 600 },
                new TextEl("boldsugar") { Bold = true },
                new TextEl("normal"),
                new TextEl("semilight") { Weight = 350, Bold = true },
            ],
        }, null);
        new FlexLayout(scene, new HeadlessFontSystem(strings)).Run(scene.Root);
        var dl = new DrawList();
        SceneRecorder.Record(scene, dl);
        var dev = new HeadlessGpuDevice();
        dev.SubmitDrawList(dl.Bytes, dl.SortKeys, new FrameInfo(new Size2(400, 300), 1f, ColorF.Transparent));
        int w600 = -1, w700 = -1, w400 = -1, w350 = -1;
        foreach (var g in dev.LastGlyphs)
        {
            string t = strings.Resolve(g.Text);
            if (t == "semibold") w600 = g.Weight;
            else if (t == "boldsugar") w700 = g.Weight;
            else if (t == "normal") w400 = g.Weight;
            else if (t == "semilight") w350 = g.Weight;
        }
        Check("WC-TXT.a numeric Weight reaches the DrawGlyphRun op (Weight=600 → op 600)", w600 == 600, $"weight={w600}");
        Check("WC-TXT.b Bold sugar → 700; default → 400; explicit Weight beats Bold", w700 == 700 && w400 == 400 && w350 == 350,
            $"bold={w700} normal={w400} explicit={w350}");

        // (c) LineHeight changes the measured height deterministically: natural 14×1.4 = 19.6;
        // MaxHeight = max(natural, LineHeight) (BaseTextBlockStyle default, TextBlock_themeresources.xaml:16);
        // BlockLineHeight = LineHeight exactly, even below natural.
        var fonts = new HeadlessFontSystem(strings);
        var hello = strings.Intern("Hello");
        float hNat = fonts.Measure(hello, new TextStyle(default, 14f, 400)).Size.Height;
        float hMax30 = fonts.Measure(hello, new TextStyle(default, 14f, 400, LineHeight: 30f)).Size.Height;
        float hMax10 = fonts.Measure(hello, new TextStyle(default, 14f, 400, LineHeight: 10f)).Size.Height;
        float hBlock10 = fonts.Measure(hello, new TextStyle(default, 14f, 400, LineHeight: 10f, Stacking: LineStacking.BlockLineHeight)).Size.Height;
        bool lineHeightOk = Near(hNat, 19.6f, 0.01f) && Near(hMax30, 30f, 0.01f) && Near(hMax10, 19.6f, 0.01f) && Near(hBlock10, 10f, 0.01f);
        // …and through the element pipeline: a TextEl with LineHeight=30 lays out 30 DIP tall.
        var lhScene = LayoutTree(strings, new BoxEl { Direction = 1, Children = [new TextEl("line") { LineHeight = 30f }] });
        float nodeH = lhScene.AbsoluteRect(Child(lhScene, lhScene.Root, 0)).H;
        Check("WC-TXT.c LineHeight resolves per LineStackingStrategy (MaxHeight clamps up, BlockLineHeight exact)",
            lineHeightOk && Near(nodeH, 30f, 0.01f),
            $"nat={hNat:0.0} max30={hMax30:0.0} max10={hMax10:0.0} block10={hBlock10:0.0} nodeH={nodeH:0.0}");

        // (d) CharacterSpacing (1/1000 em) widens the measured advance: 5 chars × (14×0.55 + 14×100/1000) = 45.5 vs 38.5.
        float wPlain = fonts.Measure(hello, new TextStyle(default, 14f, 400)).Size.Width;
        float wSpaced = fonts.Measure(hello, new TextStyle(default, 14f, 400, CharSpacing: 100f)).Size.Width;
        Check("WC-TXT.d CharacterSpacing widens the measured advance (+size×spacing/1000 per char)",
            Near(wPlain, 38.5f, 0.01f) && Near(wSpaced, 45.5f, 0.01f), $"plain={wPlain:0.0} spaced={wSpaced:0.0}");

        // (e) TextLineBounds=Tight trims the line box to cap-height..baseline: 14×0.7 = 9.8 < 19.6 full,
        // and the baseline lands at the box bottom (= the tight height).
        var mTight = fonts.Measure(hello, new TextStyle(default, 14f, 400, LineBounds: TextLineBounds.Tight));
        Check("WC-TXT.e TextLineBounds=Tight reduces the measured height to cap..baseline",
            Near(mTight.Size.Height, 9.8f, 0.01f) && Near(mTight.Baseline, 9.8f, 0.01f),
            $"tightH={mTight.Size.Height:0.0} baseline={mTight.Baseline:0.0} fullH={hNat:0.0}");

        // (f) the ramp carries the WinUI values: sizes 12/14/14/18/20/28/40/68 (TextBlock_themeresources.xaml:3-9),
        // SemiBold 600 on BodyStrong/Subtitle/Title/TitleLarge/Display (:13 inherited; :26, :36-51), line heights
        // 16/20/20/24/28/36/52/92 (the Fluent type-ramp spec); Strong() now means SemiBold 600, not Bold 700.
        var cap = Caption("x"); var body = Body("x"); var bs = BodyStrong("x"); var bl = BodyLarge("x");
        var sub = Subtitle("x"); var ti = Title("x"); var tl = TitleLarge("x"); var di = Display("x");
        bool sizes = cap.Size == 12f && body.Size == 14f && bs.Size == 14f && bl.Size == 18f
                  && sub.Size == 20f && ti.Size == 28f && tl.Size == 40f && di.Size == 68f;
        bool lineHs = cap.LineHeight == 16f && body.LineHeight == 20f && bs.LineHeight == 20f && bl.LineHeight == 24f
                   && sub.LineHeight == 28f && ti.LineHeight == 36f && tl.LineHeight == 52f && di.LineHeight == 92f;
        bool weights = cap.ResolvedWeight == 400 && body.ResolvedWeight == 400 && bs.Weight == 600
                    && sub.Weight == 600 && ti.Weight == 600 && tl.Weight == 600 && di.Weight == 600;
        bool strong = new TextEl("x").Strong().Weight == 600 && new TextEl("x").FontWeight(350).ResolvedWeight == 350;
        Check("WC-TXT.f type ramp carries the WinUI values (sizes, line heights, SemiBold 600; Strong()=600)",
            sizes && lineHs && weights && strong, $"sizes={sizes} lineHs={lineHs} weights={weights} strong={strong}");
    }

    // Wave C — the inline-run text model (SpanTextEl, rtb-01), read-only text selection (rtb-02) and the per-control
    // SelectionHighlightColor (api-04). Headless advance model: w<600 → size×0.55/char, w≥600 → size×0.62/char;
    // line height = metricSize×1.4; baseline = ×1.1 (HeadlessFontSystem.cs).
    static void WaveCSpanTextChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);
        var scene = new SceneStore();
        var recon = new TreeReconciler(scene, strings);
        var clip = new HeadlessClipboard();
        var dispatcher = new InputDispatcher(scene) { Fonts = fonts, Clipboard = clip };
        CursorId cursor = CursorId.Arrow;
        dispatcher.OnCursorChanged = c => cursor = c;
        bool linkClicked = false;
        var gold = ColorF.FromRgba(0xFF, 0xD7, 0x00);
        var red = ColorF.FromRgba(0xFF, 0x00, 0x00);

        recon.ReconcileRoot(new BoxEl
        {
            Direction = 1, Width = 200, Height = 100,
            Children =
            [
                // [0] mixed-weight flow: "aaaa " base 400 (5 × 5.5 = 27.5) + "bbbb" 700 (4 × 6.2 = 24.8) → 52.3 one flow
                new SpanTextEl([new TextSpan("aaaa "), new TextSpan("bbbb", Weight: 700)]) { Size = 10f },
                // [1] hyperlink paragraph: link "docs" covers chars [9,13) → x [49.5, 71.5) on its line
                new SpanTextEl(
                [
                    new TextSpan("Read the "),
                    new TextSpan("docs", Color: gold, Underline: true, OnClick: () => linkClicked = true),
                    new TextSpan(" now"),
                ]) { Size = 10f },
                // [2] selectable paragraph with a per-control highlight override (api-04)
                new SpanTextEl([new TextSpan("Hello world")]) { Size = 10f, IsTextSelectionEnabled = true, SelectionHighlightColor = red },
                // [3] plain TextEl, selection opt-in (WinUI TextBlock.cpp:583), engine-default highlight brush
                new TextEl("Copy me too") { Size = 10f, IsTextSelectionEnabled = true },
            ],
        }, null);
        new FlexLayout(scene, fonts).Run(scene.Root);

        var mixed = Child(scene, scene.Root, 0);
        var linky = Child(scene, scene.Root, 1);
        var sel = Child(scene, scene.Root, 2);
        var plain = Child(scene, scene.Root, 3);

        // (a) rtb-01: the paragraph measures as ONE flow with per-span advances (NOT a uniform-weight run), wraps as
        // one unit, and reaches the draw stream as ONE glyph op carrying the span-run id.
        var mixedStyle = scene.Layout(mixed).TextStyle;
        var mUnwrapped = fonts.Measure(scene.Paint(mixed).Text, mixedStyle);
        var mWrapped = fonts.Measure(scene.Paint(mixed).Text, mixedStyle, 30f);   // "aaaa " | "bbbb" → 2 lines × 14
        var dl = new DrawList();
        SceneRecorder.Record(scene, dl);
        var dev = new HeadlessGpuDevice();
        dev.SubmitDrawList(dl.Bytes, dl.SortKeys, new FrameInfo(new Size2(200, 100), 1f, ColorF.Transparent));
        int mixedOps = 0, mixedSpanId = 0;
        foreach (var g in dev.LastGlyphs)
            if (strings.Resolve(g.Text) == "aaaa bbbb") { mixedOps++; mixedSpanId = g.SpanRunId; }
        Check("WC-SPAN.a mixed-weight spans measure as a SINGLE flow (52.3 = 5×5.5 + 4×6.2; wraps to 2 lines) and emit ONE glyph op",
            Near(mUnwrapped.Size.Width, 52.3f, 0.01f) && Near(mWrapped.Size.Height, 28f, 0.01f)
            && mixedOps == 1 && mixedSpanId != 0,
            $"w={mUnwrapped.Size.Width:0.0} wrapH={mWrapped.Size.Height:0.0} ops={mixedOps} spanId={mixedSpanId}");

        // (b) hyperlink span: Hand cursor over the span's laid rects (RichTextBlock.cpp:2995 SetCursor(MouseCursorHand)),
        // arrow elsewhere on the same node, click fires the span's OnClick; the underline bar draws in the span color.
        var lr = scene.AbsoluteRect(linky);
        var overLink = new Point2(lr.X + 55f, lr.Y + 7f);     // inside "docs" [49.5, 71.5)
        var offLink = new Point2(lr.X + 10f, lr.Y + 7f);      // inside "Read the"
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.PointerMove, overLink, 0, 0) });
        bool handOverLink = cursor == CursorId.Hand;
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.PointerMove, offLink, 0, 0) });
        bool arrowOffLink = cursor == CursorId.Arrow;
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.PointerDown, overLink, 0, 0) });
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.PointerUp, overLink, 0, 0) });
        bool underlineInLinkColor = false;
        foreach (var r in dev.LastRects)
            if (ColorClose(r.Fill, gold, 0.01f) && Near(r.Rect.H, 1f, 0.01f) && Near(r.Rect.W, 22f, 0.01f)) underlineInLinkColor = true;
        Check("WC-SPAN.b hyperlink span: Hand over its rects, arrow off them, OnClick fires, underline bar in the span color",
            handOverLink && arrowOffLink && linkClicked && underlineInLinkColor,
            $"hand={handOverLink} arrow={arrowOffLink} clicked={linkClicked} underline={underlineInLinkColor}");

        // (c) rtb-02: drag-select publishes selection rects through the editor slab; Ctrl+C copies through the
        // clipboard seam (TextSelectionManager.cpp:30-41); double-click selects the word under the press.
        var sr = scene.AbsoluteRect(sel);
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(sr.X + 1f, sr.Y + 7f), 0, 0) });
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(sr.X + 29f, sr.Y + 7f), 0, 0) });   // → "Hello" [0,5)
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(sr.X + 29f, sr.Y + 7f), 0, 0) });
        var dragRects = scene.GetTextEditSelectionRects(sel);
        bool dragRectOk = dragRects.Length == 1 && Near(dragRects[0].W, 27.5f, 0.01f);
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, 'C', Mods: KeyModifiers.Ctrl) });
        bool copiedHello = clip.TryGetText(out string copied1) && copied1 == "Hello";
        // double-click on "world" (chars [6,11) → x 33..60.5): press, release, press again inside the slop window
        var onWorld = new Point2(sr.X + 35f, sr.Y + 7f);
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.PointerDown, onWorld, 0, 0) });
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.PointerUp, onWorld, 0, 0) });
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.PointerDown, onWorld, 0, 0) });
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.PointerUp, onWorld, 0, 0) });
        bool wordSel = scene.TryGetTextSelection(sel, out int ws, out int we) && ws == 6 && we == 11;
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, 'C', Mods: KeyModifiers.Ctrl) });
        bool copiedWorld = clip.TryGetText(out string copied2) && copied2 == "world";
        Check("WC-SPAN.c drag-select publishes rects + Ctrl+C copies; double-click selects the word",
            dragRectOk && copiedHello && wordSel && copiedWorld,
            $"rects={dragRects.Length} w={(dragRects.Length > 0 ? dragRects[0].W : 0):0.0} copy1='{copied1}' word=({ws},{we}) copy2='{copied2}'");

        // (d) api-04: the per-control SelectionHighlightColor reaches the draw; a control WITHOUT the override keeps
        // the host theme brush (TextControlSelectionHighlightColor ≡ system accent, TextSelectionManager.cpp:52-56).
        var themeBlue = ColorF.FromRgba(0x00, 0x78, 0xD4);
        var te = new TextEditStyle(themeBlue, ColorF.FromRgba(0xFF, 0xFF, 0xFF), ColorF.FromRgba(0xFF, 0xFF, 0xFF));
        SceneRecorder.Record(scene, dl, textEdit: te);   // "world" still selected on the override paragraph
        dev.SubmitDrawList(dl.Bytes, dl.SortKeys, new FrameInfo(new Size2(200, 100), 1f, ColorF.Transparent));
        bool overrideReachesDraw = false, themeLeaked = false;
        foreach (var r in dev.LastRects)
        {
            if (ColorClose(r.Fill, red, 0.01f)) overrideReachesDraw = true;
            if (ColorClose(r.Fill, themeBlue, 0.01f)) themeLeaked = true;
        }
        // now select on the plain TextEl (no override) → the theme brush paints
        var pr = scene.AbsoluteRect(plain);
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(pr.X + 1f, pr.Y + 7f), 0, 0) });
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(pr.X + 23f, pr.Y + 7f), 0, 0) });  // → "Copy" [0,4)
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(pr.X + 23f, pr.Y + 7f), 0, 0) });
        SceneRecorder.Record(scene, dl, textEdit: te);
        dev.SubmitDrawList(dl.Bytes, dl.SortKeys, new FrameInfo(new Size2(200, 100), 1f, ColorF.Transparent));
        bool themeOnPlain = false;
        foreach (var r in dev.LastRects)
            if (ColorClose(r.Fill, themeBlue, 0.01f)) themeOnPlain = true;
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, 'C', Mods: KeyModifiers.Ctrl) });
        bool copiedFromTextEl = clip.TryGetText(out string copied3) && copied3 == "Copy";
        Check("WC-SPAN.d SelectionHighlightColor override reaches the draw; default keeps the theme brush; TextEl opt-in selects",
            overrideReachesDraw && !themeLeaked && themeOnPlain && copiedFromTextEl,
            $"override={overrideReachesDraw} leak={themeLeaked} theme={themeOnPlain} copy3='{copied3}'");

        // (e) steady-state: with a live selection + span paragraphs, a re-record allocates ZERO managed bytes
        // (artifacts/measure cached at layout; the recorder only reads — phases 6–13 stay clean).
        SceneRecorder.Record(scene, dl, textEdit: te);   // warm growth (DrawList buffers, rect slabs)
        SceneRecorder.Record(scene, dl, textEdit: te);
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        SceneRecorder.Record(scene, dl, textEdit: te);
        long recBytes = GC.GetAllocatedBytesForCurrentThread() - a0;
        Check("WC-SPAN.e record with span paragraphs + live selection allocates 0 bytes", recBytes == 0, $"{recBytes} bytes");
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
        ReflowChecks(strings);
        AnimRegressionChecks(strings);
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
        ScrollCrossAxisChecks(strings);
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
        PolylineStrokeChecks(strings);
        CrossfadeChecks(strings);
        WaveeSkeletonChecks(strings);

        // Signals-first model: granular re-render, the compositor bypass (slider tank), reactive control-flow.
        GranularityChecks(strings);
        SliderSignalChecks(strings);
        FlowChecks(strings);
        FlowReorderChecks(strings);

        // Wave 1 engine primitives (control-parity foundation). P1 — disabled gate; P2 — stateful text ramps;
        // P4b — stateful gradient transitions; P4a — authored clip-rect channel; P6 — focus/keyboard nav.
        DisabledChecks(strings);
        TextRampChecks(strings);
        GradientRampChecks(strings);
        ClipChannelChecks();
        FocusNavChecks(strings);
        InputVocabularyChecks(strings);
        WaveBInputChecks(strings);
        E5DragDropChecks(strings);
        FocusRingChecks(strings);
        BrushTransitionChecks(strings);
        TextServicesSeamChecks();
        EditableTextCoreChecks(strings);
        TextConsumerControlChecks(strings);
        PlacementChecks();
        OverlayFocusRestoreChecks(strings);
        ExpanderSettingsChecks(strings);

        // Wave 2 — buttons & input-state controls adopt the Wave-1 primitives.
        Wave2ControlChecks(strings);

        // Basic-input infrastructure (Part A): repeat timing, text input, anchored overlays.
        RepeatButtonChecks(strings);
        TextInputChecks(strings);
        OverlayChecks(strings);
        OverlayAnimationChecks(strings);
        E4PopupWindowingChecks(strings);
        FlyoutAcrylicChecks(strings);
        AcrylicBackdropMathChecks();
        ContentDialogChromeChecks(strings);
        TeachingTipPlacementChecks(strings);
        MenuFlyoutStyleChecks(strings);
        SplitButtonStyleChecks(strings);

        // Hierarchical NavigationView (Part B).
        NavHierarchyChecks(strings);
        PipsPagerOutputChecks(strings);

        // Basic-input controls (Part C).
        BasicInputControlChecks(strings);

        // Wave-1 control parity (Button/RepeatButton/HyperlinkButton/ToggleButton/ToggleSwitch/RadioButtons/Slider/Rating).
        W1ControlsChecks(strings);

        // E11 — unified virtualization substrate (measured seam, LinedFlow/GroupedList, repeater lifecycle,
        // SelectionModel, ItemContainer, ItemsView).
        E11VirtChecks(strings);

        // Parity wave-1 defect fixes (D1 collection-host sizing, D2 pointer-focus/PasswordBox reveal,
        // D3 Expander WinUI motion, D4 ScrollBar/AnnotatedScrollBar anatomy).
        D1CollectionHostSizingChecks(strings);
        Cp2ConsolidationChecks(strings);   // collection consolidation: ItemsView premiere + thin LV/GV presets + 2-D reorder + displacement channel
        D2PasswordRevealFocusChecks(strings);
        ProgressIndeterminateLifecycleChecks(strings);
        PropNetClobberChecks(strings);     // Prop<T> W0: bound-channel ownership net (clobber guards + contract locks)
        AnimRestChecks(strings);           // Prop<T> B1-B3: canceled/settled-track resting values vs static re-asserts
        PropUnionChecks(strings);          // Prop<T> W1: signal-direct bind kind + the locked mount-only wiring contract
        D3ExpanderChecks(strings);
        D4ScrollBarChecks(strings);

        // Parity wave-2 defect fixes (D5 editable-ComboBox unified chrome + dropdown styling, D6/D7 SplitButton
        // flyout placement + per-kind menu/dropdown motion).
        D5EditableComboBoxChecks(strings);
        D67SplitButtonFlyoutChecks(strings);

        // Wave C — text pipeline parity (numeric FontWeight, CharacterSpacing, LineHeight/LineStacking,
        // TextLineBounds=Tight, WinUI type-ramp values).
        WaveCTextPipelineChecks(strings);

        // Wave C — inline-run text model (SpanTextEl, rtb-01), read-only selection (rtb-02), SelectionHighlightColor (api-04).
        WaveCSpanTextChecks(strings);

        Console.WriteLine();
        if (s_failures == 0) { Console.WriteLine("ALL CHECKS PASSED — the vertical slice exercises every seam end-to-end."); return 0; }
        Console.WriteLine($"{s_failures} CHECK(S) FAILED."); return 1;
    }
}
