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
                   FillBind = () => ColorF.FromRgba(30, 30, (byte)(idx.Value % 2 == 0 ? 30 : 50)),
                   Children = [new TextEl("") { Size = 12f, TextBind = () => $"row {idx.Value}" }],
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
            new NavItem("group", "G", "Group")
            {
                Children = [new NavItem("c1", "1", "ChildOne"), new NavItem("c2", "2", "ChildTwo")],
            },
            new NavItem("after", "A", "After"),
        ],
        Content = key => new BoxEl { Children = [Ui.Text("PAGE:" + key)] },
    });
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
                new TextEl("") { Size = 14f, Color = Tok.TextPrimary, TextBind = () => $"Page {selected.Value + 1} / 5" },
            ],
        };
    }
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
            expandedThumb |= r.W >= 10f && r.H >= 30f && r.H < 190f;
        }
        bool hoverSettledIdle = !host.HasActiveWork;

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
        bool noContent = Child(host.Scene, host.Scene.Root, 1).IsNull;

        // Toggle open. (a) The chevron rotation TWEENS (167ms): track peak sin θ — a tween passes through a mid-angle
        // (sin θ → ~1 near 90°), an instant snap never leaves ~0. (b) The content panel FADES in (Enter opacity 0→1):
        // track its minimum opacity — an instant appear would read 1 from the first frame.
        ClickNode(host, window, Child(host.Scene, host.Scene.Root, 0));
        float peakSin = 0f, minContentOpacity = 1f;
        for (int i = 0; i < 16; i++)
        {
            host.RunFrame();
            var ch = Child(host.Scene, Child(host.Scene, host.Scene.Root, 0), 1);
            peakSin = MathF.Max(peakSin, MathF.Abs(host.Scene.Paint(ch).LocalTransform.M12));
            var content = Child(host.Scene, host.Scene.Root, 1);
            if (!content.IsNull) minContentOpacity = MathF.Min(minContentOpacity, host.Scene.Paint(content).Opacity);
        }
        bool rotating = peakSin > 0.5f;
        bool contentFadedIn = minContentOpacity < 0.5f;

        for (int i = 0; i < 16; i++) host.RunFrame();   // settle
        var chevronDone = Child(host.Scene, Child(host.Scene, host.Scene.Root, 0), 1);
        float m11Done = host.Scene.Paint(chevronDone).LocalTransform.M11;   // cos 180° ≈ -1
        bool settled = m11Done < -0.9f;
        bool hasContent = !Child(host.Scene, host.Scene.Root, 1).IsNull && HasGlyph(device, strings, "expander-body");

        Check("W1-P3.a Expander animates chevron rotation + content reveal (mid-flight)",
            Near(m11Collapsed, 1f, 0.05f) && rotating && settled && contentFadedIn && hasContent,
            $"m11→{m11Done:0.00} peakSinθ={peakSin:0.00} minContentOpacity={minContentOpacity:0.00} content {!noContent}→{hasContent}");
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

        // E2.b/c/d — WinUI activation semantics: Enter fires on key-DOWN; Space arms (pressed visual) and fires on
        // key-UP; Escape while Space is held cancels without firing.
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
            bool enterDown = clicks == 1;

            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Space) });
            bool armedNoFire = clicks == 1 && (scene.Flags(node) & NodeFlags.Pressed) != 0;
            disp.Dispatch(new[] { new InputEvent(InputKind.KeyUp, default, 0, Keys.Space) });
            bool firedOnUp = clicks == 2 && (scene.Flags(node) & NodeFlags.Pressed) == 0;
            Check("E2.b Enter activates on key-down; Space arms pressed and fires on key-up",
                enterDown && armedNoFire && firedOnUp, $"enter={enterDown} armed={armedNoFire} up={firedOnUp}");

            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Space) });
            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Escape) });
            disp.Dispatch(new[] { new InputEvent(InputKind.KeyUp, default, 0, Keys.Space) });
            Check("E2.c Escape cancels a held Space without firing",
                clicks == 2 && (scene.Flags(node) & NodeFlags.Pressed) == 0, $"clicks={clicks}");
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

        // E2.l — hover resolves the cursor: clickable → Hand by default; explicit Cursor=IBeam overrides; empty → Arrow.
        {
            var scene = new SceneStore();
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Direction = 0, Gap = 10, Padding = Edges4.All(0),
                Children =
                [
                    new BoxEl { Key = "hand", Width = 20, Height = 20, OnClick = () => { } },
                    new BoxEl { Key = "ibeam", Width = 20, Height = 20, OnClick = () => { }, Cursor = CursorId.IBeam },
                ],
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            CursorId last = CursorId.Arrow;
            disp.OnCursorChanged = c => last = c;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(10, 10), 0, 0) });
            bool hand = last == CursorId.Hand;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(40, 10), 0, 0) });
            bool ibeam = last == CursorId.IBeam;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(200, 200), 0, 0) });
            bool arrow = last == CursorId.Arrow;
            Check("E2.l hover resolves the cursor (hand → explicit I-beam → arrow off-control)",
                hand && ibeam && arrow, $"hand={hand} ibeam={ibeam} arrow={arrow}");
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
            host.RunFrame();   // focus re-render mounts the reveal button (ButtonVisible: focused ∧ non-empty)
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
            Check("W0f.1 PasswordBox Peek: reveal (F78D, width 30, TextSecondary) shows on focus∧non-empty; press-and-hold reveals, release re-masks + keeps focus",
                masked0 && noBtn0 && shown && w30 && glyph && peeked && remasked && focusKept,
                $"masked0={masked0} noBtn0={noBtn0} shown={shown} w30={w30} glyph={glyph} peeked={peeked} remasked={remasked} focus={focusKept}");
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

        // disabled-no-key-activate: Enter activates the focused ENABLED box; the disabled box never key-activates.
        int beforeEnter = root.EnabledClicks;
        window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Enter));
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

        NodeHandle SurfaceOf(NodeHandle n) { for (; !n.IsNull; n = host.Scene.Parent(n)) if (host.Scene.TryGetAcrylic(n, out _)) return n; return NodeHandle.Null; }
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
            bool fading = !surface.IsNull && host.Scene.IsLive(surface) && closeOp < 0.99f
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
        while (!surface.IsNull && !host2.Scene.TryGetAcrylic(surface, out _)) surface = host2.Scene.Parent(surface);
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

        // Wave 1 engine primitives (control-parity foundation). P1 — disabled gate; P2 — stateful text ramps;
        // P4b — stateful gradient transitions; P4a — authored clip-rect channel; P6 — focus/keyboard nav.
        DisabledChecks(strings);
        TextRampChecks(strings);
        GradientRampChecks(strings);
        ClipChannelChecks();
        FocusNavChecks(strings);
        InputVocabularyChecks(strings);
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
        ContentDialogChromeChecks(strings);
        TeachingTipPlacementChecks(strings);
        MenuFlyoutStyleChecks(strings);
        SplitButtonStyleChecks(strings);

        // Hierarchical NavigationView (Part B).
        NavHierarchyChecks(strings);
        PipsPagerOutputChecks(strings);

        // Basic-input controls (Part C).
        BasicInputControlChecks(strings);

        Console.WriteLine();
        if (s_failures == 0) { Console.WriteLine("ALL CHECKS PASSED — the vertical slice exercises every seam end-to-end."); return 0; }
        Console.WriteLine($"{s_failures} CHECK(S) FAILED."); return 1;
    }
}
