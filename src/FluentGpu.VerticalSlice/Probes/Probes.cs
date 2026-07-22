using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Forms;
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
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;
using FluentGpu.VerticalSlice.Harness;


// Probe components + harness fixtures used by suite modules.
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

// G5j localization: a DatePicker with no date renders its "day"/"month"/"year" column placeholders — kit-owned,
// localized text drawn INLINE in the picker's own Render (Loc.Get subscribes its render-effect), so a culture switch
// re-resolves them. The loc-kit gate mounts this and reads the rendered glyphs (neutral / pseudo / switched-back).
sealed class LocDatePickerProbe : Component
{
    readonly Signal<DateOnly?> _date = new(null);
    public override Element Render() => DatePicker.Create(_date);
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
    public override Element Render() => Ui.Text($"ctx {UseContext(Gate.NumCtx)}");
}

sealed class CtxParent : Component
{
    public override Element Render()
    {
        var (v, setV) = UseState(7);
        return VStack(4,
            Button.Accent("inc", () => setV(v + 1)),
            Ctx.Provide(Gate.NumCtx, v, Embed.Comp(() => new CtxConsumer())));
    }
}

sealed class HoverProbe : Component
{
    public override Element Render() => Button.Accent("hi", () => { });
}

// ReuseGuard probe: a control carrying caller data (Count) in a plain field that opts into the frozen-props tripwire.
// A REUSED instance whose Count changed is a frozen-prop violation (the value was frozen at mount, new one dropped).
sealed class FrozenPropProbe : Component
{
    public int Count;
    public override bool ChecksReuse => true;
    public override void DebugCheckReuse(Component next)
    {
        if (next is FrozenPropProbe n && n.Count != Count)
            ReuseGuard.Violation(this, nameof(Count), $"probe count {Count}→{n.Count}");
    }
    public override Element Render() => new BoxEl { Width = 10, Height = 10 };
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
        UseSpring(AnimChannel.ScaleX, 1.2f, SpringParams.FromResponse(0.2f, 1f), DepKey.Empty);
        return new BoxEl { Width = 20, Height = 20, Fill = ColorF.FromRgba(0, 128, 255) };
    }
}

// Diagnostic root for FG_PROBE=marquee: a fixed 150px-wide stretch column holding a Marquee with a long title.
sealed class MarqueeProbeRoot : Component
{
    public override Element Render() => new BoxEl
    {
        Width = 150f, Height = 40f, Direction = 1, AlignItems = FluentGpu.Foundation.FlexAlign.Stretch,
        Children =
        [
            Marquee.Of("This is a very long track title that should overflow and scroll",
                new Marquee.Style { FontSize = 14f, Trigger = Marquee.TriggerMode.Always }),
        ],
    };
}

// M3 gate: ping-pong marquee with no start delay so mid-scroll edge-fade is reachable quickly.
sealed class MarqueePingPongProbe : Component
{
    public override Element Render() => new BoxEl
    {
        Width = 150f, Height = 40f, Direction = 1, AlignItems = FluentGpu.Foundation.FlexAlign.Stretch,
        Children =
        [
            Marquee.Of("This is a very long track title that should overflow and scroll",
                new Marquee.Style { FontSize = 14f, StartDelayMs = 0f, Speed = 200f, Mode = Marquee.ScrollMode.PingPong, Trigger = Marquee.TriggerMode.Always }),
        ],
    };
}

// The M2 gate's root: mirrors the PlayerBar's now-playing meta column — a grow/shrink, clip-to-bounds column whose
// marquee title is bound to a REACTIVE Signal<string> (the real app uses Prop.Of(() => NowPlaying(b).Title), empty until
// a track loads). Lets the gate grow the title AFTER mount and assert the marquee then scrolls (the title-after-mount bug).
sealed class MarqueeAutostartProbe : Component
{
    public readonly Signal<string> Title = new("");
    public override Element Render()
    {
        var marquee = Marquee.Of(Prop.Of(() => Title.Value),
            new Marquee.Style { FontSize = 14f, Weight = 700, StartDelayMs = 0f, Speed = 200f, Trigger = Marquee.TriggerMode.Always });
        return new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, Gap = 2f,
            Justify = FluentGpu.Foundation.FlexJustify.Center, AlignItems = FluentGpu.Foundation.FlexAlign.Stretch, ClipToBounds = true,
            Children = [marquee],
        };
    }
}

// Production-shaped player-bar marquee: fixed left cluster, artwork + bounded metadata + like button, clickable title
// wrapper, PauseOnHover with the shared metadata hover signal, and a title that arrives after the idle 0->0 track has
// already settled. This catches lifecycle bugs hidden by the simpler Always/direct-column M2 probe.
sealed class PlayerBarMarqueeProbe : Component
{
    public readonly Signal<string> Title = new("");
    public readonly Signal<bool> Hovered = new(false);
    public NodeHandle TitleNode;
    public int Clicks;

    public override Element Render()
    {
        var title = (BoxEl)Marquee.Of(Prop.Of(() => Title.Value),
            new Marquee.Style
            {
                FontSize = 14f, Weight = 700, Speed = 18f,
                CycleMs = 10000f, EndPauseMs = 2500f,
                Mode = Marquee.ScrollMode.PingPong, Trigger = Marquee.TriggerMode.PauseOnHover,
            }, Hovered);
        title = title with
        {
            OnClick = () => Clicks++, Cursor = CursorId.Hand,
            Role = AutomationRole.Hyperlink, Focusable = true,
            OnRealized = h => TitleNode = h,
        };
        var meta = new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, Gap = 2f,
            Justify = FlexJustify.Center, ClipToBounds = true,
            Children = [title],
        };
        return new BoxEl
        {
            Width = 260f, Height = 56f, Direction = 0, AlignItems = FlexAlign.Center, Gap = 8f,
            OnContextRequested = static _ => { }, // mirrors the player cluster's attached context-menu ancestor
            Children =
            [
                new BoxEl { Width = 56f, Height = 56f },
                meta,
                new BoxEl { Width = 30f, Height = 30f },
            ],
        };
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

// ── G1a hook-surface probes (auto-tracked effects, cleanup, DepKey deps) ─────────────────────────────────────────
// Auto-tracked effect that reads sigA on run 1 and (after a branch flip) sigB on run 2 — proves runtime re-arming.
sealed class AutoEffectProbe : Component
{
    public readonly Signal<int> A = new(0), B = new(0);
    public bool UseB;
    public int Runs;
    public override Element Render()
    {
        bool useB = UseB;   // captured per render → the effect closure branches on this render's value
        UseEffect(() => { Runs++; if (useB) _ = B.Value; else _ = A.Value; });   // AUTO-TRACKED (no deps)
        return new BoxEl();
    }
}

// Deps-gated cleanup-returning effect: cleanup fires before each re-run on dep change and once at unmount.
sealed class CleanupProbe : Component
{
    public int Dep;
    public int Setup, Cleanup;
    public override Element Render()
    {
        UseEffect(() => { Setup++; return () => Cleanup++; }, DepKey.From(Dep));
        return new BoxEl();
    }
}

// Two auto-tracked cleanup-returning effects: their cleanups must run in cell order at unmount.
sealed class MultiCleanupProbe : Component
{
    public readonly List<int> Order = new();
    public override Element Render()
    {
        UseEffect(() => { return () => Order.Add(1); });
        UseEffect(() => { return () => Order.Add(2); });
        return new BoxEl();
    }
}

sealed class MountOnceProbe : Component
{
    public int Runs;
    public override Element Render() { UseEffect(() => { Runs++; }, DepKey.Empty); return new BoxEl(); }
}

sealed class StringTupleProbe : Component
{
    public string S = "a";
    public int I;
    public int Runs;
    public override Element Render() { UseEffect(() => { Runs++; }, (S, I)); return new BoxEl(); }
}

sealed class FromRefProbe : Component
{
    public object Obj = new();
    public int Runs;
    public override Element Render() { UseEffect(() => { Runs++; }, DepKey.FromRef(Obj)); return new BoxEl(); }
}

// ── G4b component unification + per-component ReactiveScope probes ────────────────────────────────────────────────
// A component whose Render() reads NO signals must render exactly once — run-once is a consequence of subscribing to
// nothing, not a class/flag (ReactiveComponent is deleted).
sealed class SignalFreeProbe : Component
{
    public int Renders;
    public override Element Render() { Renders++; return new BoxEl { Width = 10f, Height = 10f }; }
}

sealed class SignalFreeRoot : Component
{
    public readonly SignalFreeProbe Probe = new();
    public Signal<int>? Unrelated;
    public int RootRenders;
    public override Element Render()
    {
        RootRenders++;
        var u = UseSignal(0);
        Unrelated = u;
        _ = u.Value;   // the ROOT subscribes to an unrelated signal → writing it makes real render work flush
        return new BoxEl { Children = [Embed.Comp(() => Probe)] };
    }
}

// The former-ReactiveComponent idiom under the unified model: reads Tok.* DIRECTLY in render (no signal subscription)
// and holds hook state. RethemeAll must re-run it IN PLACE — new token color, SAME node, preserved hook state.
sealed class RethemeInPlaceProbe : Component
{
    public int Renders;
    public int SeenState;
    public Action<int>? SetState;
    public override Element Render()
    {
        Renders++;
        var (n, setN) = UseState(1);
        SeenState = n; SetState = setN;
        return new BoxEl { Children = [new TextEl("g4b-themed") { Color = Tok.TextPrimary, Size = 14f }] };
    }
}

sealed class ScopeCounters
{
    public int Renders;
    public int Cleanups;
    public readonly Signal<int> Dep = new(0);
    public readonly Signal<bool> Show = new(true);
}

sealed class ScopeLifetimeChild : Component
{
    readonly ScopeCounters _c;
    public ScopeLifetimeChild(ScopeCounters c) => _c = c;
    public override Element Render()
    {
        _c.Renders++;
        _ = _c.Dep.Value;                                  // render-effect subscribes to Dep
        UseEffect(() => { return () => _c.Cleanups++; });  // cleanup runs once at unmount, via the scope
        return new BoxEl();
    }
}

sealed class ScopeLifetimeRoot : Component
{
    public readonly ScopeCounters C = new();
    public override Element Render()
        => Flow.Show(() => C.Show.Value, Embed.Comp(() => new ScopeLifetimeChild(C)));
}

// KeepAlive parking must NOT dispose the scope: the page instance + hook state survive a park/reactivate, a parked page
// defers renders (no re-render even when a signal it read changes), and reactivation replays exactly once.
sealed class ScopeParkProbe : Component
{
    public Signal<string>? Route;
    public readonly Signal<int> Ping = new(0);
    public readonly Dictionary<string, int> Renders = new();
    public readonly Dictionary<string, int> Constructions = new();
    public override Element Render()
    {
        var route = UseSignal("a");
        Route = route;
        return Flow.KeepAlive(
            () => route.Value,
            key => key,
            key => Embed.Comp(() => new ScopeParkPage(key, this)),
            new KeepAliveOptions(MaxEntries: 3));
    }
}

sealed class ScopeParkPage : Component
{
    readonly string _key;
    readonly ScopeParkProbe _o;
    public ScopeParkPage(string key, ScopeParkProbe o)
    {
        _key = key; _o = o;
        o.Constructions.TryGetValue(key, out int c); o.Constructions[key] = c + 1;
    }
    public override Element Render()
    {
        _o.Renders.TryGetValue(_key, out int r); _o.Renders[_key] = r + 1;
        _ = _o.Ping.Value;              // subscribe to Ping
        var (n, _) = UseState(0);       // component-local state that must survive parking
        return new BoxEl { Width = 40f, Height = 40f, Children = [Text("park-" + _key + ":" + n)] };
    }
}

// ── G4c re-pushed live props probes ──────────────────────────────────────────────────────────────────────────────
// The immutable props record a parent RE-PUSHES to a reused child (Embed.Comp(props, factory)). Record value equality
// coalesces a fresh-but-equal re-push; an Element slot rides the same record so a slot re-push reconciles in place.
sealed record PropsPayload(int N, Element? Slot = null);

// An Equals-COUNTING props record: the reference short-circuit at the reuse seam must prevent this Equals from ever
// running when the SAME reference is re-supplied (a memoized/cached props object).
sealed record CountingProps(int N)
{
    public static int EqualsCalls;
    public bool Equals(CountingProps? other) { EqualsCalls++; return other is not null && other.N == N; }
    public override int GetHashCode() => N;
}

// A child that reads its re-pushed props with UseProps and records what it saw (render count / last value / last slot),
// so a gate can assert delivery reached THIS instance. Hosts the slot + a stateful sibling (Keeper) so a slot re-push
// proves in-place reconcile with surviving sibling state.
sealed class PropsChild : Component
{
    public int Renders;
    public int LastN = -1;
    public Element? LastSlot;
    public readonly StateKeeper Keeper = new();
    public bool HostKeeper;
    public override Element Render()
    {
        var p = UseProps<PropsPayload>();
        Renders++;
        LastN = p.N;
        LastSlot = p.Slot;
        Element[] kids = HostKeeper
            ? [p.Slot ?? (Element)new BoxEl(), Embed.Comp(() => Keeper)]
            : (p.Slot is { } s ? [s] : []);
        return new BoxEl { Children = kids };
    }
}

// A stateful sibling whose UseState must SURVIVE a parent (PropsChild) re-render driven by a slot re-push.
sealed class StateKeeper : Component
{
    public int Ticks;
    public Action? Bump;
    public override Element Render()
    {
        var (n, setN) = UseState(0);
        Ticks = n;
        Bump = () => setN(n + 1);
        return new BoxEl { Width = 5, Height = 5 };
    }
}

// Reads its props with UseProps<CountingProps> (for the ref-shortcircuit Equals-counting gate).
sealed class CountingPropsChild : Component
{
    public int Renders;
    public override Element Render() { _ = UseProps<CountingProps>(); Renders++; return new BoxEl { Width = 5, Height = 5 }; }
}

// Reads its props with UsePropsOrDefault — null when mounted propless, the value when present (no throw either way).
sealed class OptionalPropsChild : Component
{
    public bool SawNull;
    public int Renders;
    public override Element Render() { SawNull = UsePropsOrDefault<PropsPayload>() is null; Renders++; return new BoxEl { Width = 5, Height = 5 }; }
}

// A KeepAlive host whose page embeds a props child, so a gate can park a page and re-push props to the parked entry.
// ChildN is PEEKED (the host never subscribes to it), and the reactivation re-push carries its latest value — so a
// gate can drive the "latest" the reactivated page sees without churning the host.
sealed class PropsParkProbe : Component
{
    public Signal<string>? Route;
    public readonly Signal<int> ChildN = new(0);
    public readonly Dictionary<string, PropsChild> Children = new();
    public override Element Render()
    {
        var route = UseSignal("a");
        Route = route;
        return Flow.KeepAlive(
            () => route.Value,
            key => key,
            key =>
            {
                var child = Children.TryGetValue(key, out var c) ? c : (Children[key] = new PropsChild());
                return Embed.Comp(new PropsPayload(ChildN.Peek()), () => child);
            },
            new KeepAliveOptions(MaxEntries: 3));
    }
}

// A root parent that re-pushes a Driver-derived props record to a single reused child — the single-flush-coalesce case
// (delivery happens inside the parent's render-effect, mid-flush).
sealed class PropsFlushParent : Component
{
    public readonly Signal<int> Driver;
    public readonly PropsChild Child = new();
    public int Renders;
    public PropsFlushParent(Signal<int> driver) => Driver = driver;
    public override Element Render()
    {
        int n = Driver.Value;   // subscribe → this parent re-renders when Driver changes
        Renders++;
        return Embed.Comp(new PropsPayload(n), () => Child);
    }
}

// G4e — a [Props] SOURCE-GENERATED component. The PropsGenerator emits (into this partial) the Signal-backed storage,
// the subscribing getters, the AlphaProp bind accessor, the PropsData transport + Of/CurrentProps/From, and the
// IPropsHost.ApplyProps sink. Count+Label are READ in Render (their re-push re-renders the core); Alpha is BOUND to
// Opacity via AlphaProp (never read in render ⇒ compositor-only, no re-render on change); OnPing is a delegate → a
// STABLE latest-write forwarder (a fresh lambda never re-renders; the forwarder invokes the newest). Serves
// gate.props.gen.{field-level, bind-direct, partial-notify, batch-coalesce}.
[Props]
sealed partial class PropsGenProbe : Component
{
    [Prop] public partial int Count { get; }
    [Prop] public partial string Label { get; }
    [Prop] public partial float Alpha { get; }
    [Prop] public partial Action? OnPing { get; }

    public int Renders;
    public int LastCount;
    public string? LastLabel;
    public NodeHandle Box;
    public Action? Wired;   // the captured STABLE forwarder (the "wired handler invokes the newest" assertion)

    public override Element Render()
    {
        Renders++;
        LastCount = Count;      // subscribing getter → the render-effect depends on Count
        LastLabel = Label;      // subscribing getter → the render-effect depends on Label
        Wired = OnPing;         // capture the stable forwarder (delegates have no signal — no subscription)
        return new BoxEl
        {
            Width = 20f, Height = 20f,
            Opacity = Prop.Bind(AlphaProp),          // bind the field signal directly → a compositor-only bind effect
            OnRealized = h => Box = h,
            Children = [new TextEl(LastLabel + LastCount) { Size = 10f }],
        };
    }
}

// ── G4a call-site-keyed hook substrate probes ────────────────────────────────────────────────────────────────────
// A CONDITIONAL hook (Middle) between two unconditional neighbours. With the positional cursor this desynced the
// neighbours' cells; with the keyed substrate each hook keeps its own call-site cell regardless of the conditional.
sealed class ConditionalHookProbe : Component
{
    public bool IncludeMiddle = true;
    public (int Value, Action<int> Set) Top, Middle, Bottom;
    public override Element Render()
    {
        Top = UseState(10);
        if (IncludeMiddle) Middle = UseState(20);   // CONDITIONAL hook — legal under the keyed substrate
        Bottom = UseState(30);
        return _box;
    }
    static readonly BoxEl _box = new();
}

// Hooks in a loop keyed per-ordinal: per-iteration state survives a count change (append/remove at the END).
sealed class LoopHookProbe : Component
{
    public int Count = 3;
    public readonly List<Signal<int>> Sigs = new();
    public override Element Render()
    {
        Sigs.Clear();
        for (int i = 0; i < Count; i++) Sigs.Add(UseSignal(i * 100));   // same source line, ordinals 0..N-1
        return _box;
    }
    static readonly BoxEl _box = new();
}

// Steady-state re-render over capture-free keyed hooks — the keyed lookup itself must allocate nothing.
sealed class SteadyHookProbe : Component
{
    static readonly Func<int> Memo42 = static () => 42;
    static readonly Action Noop = static () => { };
    static readonly BoxEl _box = new();
    public override Element Render()
    {
        var b = UseSignal(0);
        var f = UseFloatSignal(0f);
        var r = UseRef(0);
        var m = UseMemo(Memo42, DepKey.Empty);
        UseEffect(Noop);   // AUTO-tracked (deps-gated EffectKey has a pre-existing DEBUG-only closure alloc, unrelated to the lookup)
        _ = b; _ = f; _ = r; _ = m;
        return _box;
    }
}

// Form-validation probe: two fields (a Required email, a cross-field confirm) optionally joined to a UseForm scope.
sealed class ValidationProbe : Component
{
    public readonly Signal<string> Email = new("");
    public readonly Signal<string> Pwd = new("");
    public readonly Signal<string> Confirm = new("");
    public bool WithForm;
    public Field<string>? EmailField, ConfirmField;
    public FormScope? Form;

    public override Element Render()
    {
        if (WithForm) Form = UseForm();
        EmailField = UseField(Email, new FieldOptions<string> { Timing = ValidationTiming.OnTouched }, Rules.Required("err.req"));
        ConfirmField = UseField(Confirm, new FieldOptions<string> { Timing = ValidationTiming.OnChange }, Rules.Equals(Pwd, "err.match"));
        return new BoxEl();
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

// A vertical scroller of CLICKABLE rows (OnClick ⇒ ClickBit ⇒ handler-gated HitTest returns the row + it carries
// NodeFlags.Hovered): a wheel-scroll under a STATIONARY pointer must move Hovered from the old row to the new row that
// slid under the cursor (gate.scroll.hover-follows-content). 20 × 40px rows in a 200px viewport (matches ScrollProbe).
sealed class ScrollHoverProbe : Component
{
    public override Element Render()
    {
        var rows = new Element[20];
        for (int i = 0; i < rows.Length; i++)
            rows[i] = new BoxEl { Width = 180, Height = 40, Fill = ColorF.FromRgba(40, 40, 40), OnClick = static () => { } };
        return new ScrollEl
        {
            Width = 200, Height = 200, Fill = ColorF.FromRgba(20, 20, 20),
            Content = new BoxEl { Direction = 1, Children = rows },
        };
    }
}

// A ToolTip.Wrap-shaped wrapper (OnHoverMove + OnPointerExit, no click) around an INTERACTIVE child that wins the hit
// (OnClick ⇒ ClickBit ⇒ deepest leaf). Gates the subtree-scoped hover delivery (input-a11y: WinUI PointerEntered/Exited
// parity): hovering the CHILD must fire the wrapper's enter; moving within the child fires nothing more; moving from the
// wrapper's own padding onto the child must NOT fire the wrapper's exit; leaving the subtree fires exit exactly once.
sealed class HoverSubtreeProbe : Component
{
    public int WrapperEnter, WrapperExit;
    public override Element Render() => new BoxEl
    {
        Width = 300, Height = 300, Fill = ColorF.FromRgba(20, 20, 20), Padding = new Edges4(50f, 50f, 50f, 50f),
        Children =
        [
            new BoxEl
            {
                Width = 100, Height = 100, Padding = new Edges4(20f, 20f, 20f, 20f), Fill = ColorF.FromRgba(30, 30, 30),
                OnHoverMove = _ => WrapperEnter++,
                OnPointerExit = () => WrapperExit++,
                Children = [new BoxEl { Width = 60, Height = 60, Fill = ColorF.FromRgba(60, 60, 60), OnClick = static () => { } }],
            },
        ],
    };
}

// The RECYCLED-SLOT variant of gate.scroll.hover-follows-content: a BOUND virtual list (Virtual.ListBound) recycles slot
// HANDLES — a one-row boundary-crossing scroll keeps the SAME child handle under a fixed screen point but rebinds it to
// the next item, and the reconciler's rebind Unmark clears that handle's Hovered flag. Because the handle is unchanged,
// SetState early-outs and (pre-fix) leaves it un-hovered — the exact case the non-virtual ScrollHoverProbe cannot
// reproduce (there HitTest returns a genuinely different handle). Rows carry OnClick ⇒ ClickBit so handler-gated HitTest
// resolves the row and it carries NodeFlags.Hovered. 1000 × 40px rows in a 200px viewport.
sealed class ScrollHoverVirtualProbe : Component
{
    public override Element Render()
        => Virtual.ListBound(1000, 40f, idx => new BoxEl
           {
               Width = 180, Height = 40,
               Fill = Prop.Of(() => ColorF.FromRgba(40, 40, (byte)(idx.Value % 2 == 0 ? 40 : 60))),
               OnClick = static () => { },
           })
           with { Width = 200, Height = 200, Fill = ColorF.FromRgba(20, 20, 20) };
}

// [Validatable] sample (form-validation.md): the ValidatorGenerator emits SignupRules.Validators.{Email,Password,Age}.
[Validatable]
partial record SignupRules
{
    [Required, RegexMatch(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", "err.email")] public string Email { get; init; } = "";
    [Required, MinLength(8)] public string Password { get; init; } = "";
    [Range(13, 120)] public double Age { get; init; }
}

// A VERTICAL page scroller containing a nested HORIZONTAL scroller at its top (the gallery "code box" scenario) over a
// tall filler → proves wheel-axis routing: a vertical wheel scrolls the PAGE (climbing past the horizontal box), a
// horizontal wheel scrolls the inner BOX (never the page vertically).
sealed class NestedScrollProbe : Component
{
    public override Element Render() => new ScrollEl
    {
        Width = 200, Height = 200, Fill = ColorF.FromRgba(20, 20, 20),
        Content = new BoxEl
        {
            Direction = 1,
            Children =
            [
                new ScrollEl
                {
                    Horizontal = true, Width = 180, Height = 60, Fill = ColorF.FromRgba(30, 30, 30),
                    Content = new BoxEl { Direction = 0, Children = [new BoxEl { Width = 600, Height = 40, Fill = ColorF.FromRgba(50, 50, 50) }] },
                },
                new BoxEl { Width = 180, Height = 800, Fill = ColorF.FromRgba(40, 40, 40) },
            ],
        },
    };
}

// A 10,000-row virtualized list (40px uniform rows) in a 400px viewport → proves windowing + recycle at scale.
sealed class PagedShelfMeasuredProbe : Component
{
    public static readonly ColorF FirstFill = ColorF.FromRgba(220, 40, 40);
    public static readonly ColorF OtherFill = ColorF.FromRgba(40, 90, 220);

    public readonly Signal<float> Width = new(320f);
    public ShelfPagerContext Pager;

    public override Element Render() => new BoxEl
    {
        Width = Width.Value,
        Direction = 1,
        AlignItems = FlexAlign.Stretch,
        Children =
        [
            PagedShelf.Create(10,
                (i, w) =>
                {
                    ColorF fill = i == 0 ? FirstFill : OtherFill;
                    return new BoxEl
                    {
                        Width = w, Height = 44f,
                        Fill = fill, HoverFill = fill,
                        OnClick = static () => { },
                    };
                },
                pager: ShelfPager.None,
                customPager: ctx => { Pager = ctx; return new BoxEl { Width = 0f, Height = 0f }; },
                minCardW: 100f,
                maxCardW: 100f,
                gap: 10f,
                fixedCardW: 100f,
                headerGap: 0f,
                edgeFade: 24f,
                keyOf: i => "shelf-probe-" + i,
                measured: true),
        ],
    };
}

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

// A deliberately stateful/custom layout that keeps returning its OLD upper window bound after ItemCount shrinks.
// Real layouts can briefly have the same stale cached geometry; the reconciler seam must constrain it to the current
// count before using layout-produced values as Math.Clamp bounds.
sealed class StaleCountVirtualLayout(int cachedCount) : IVirtualLayout
{
    const float Extent = 40f;

    public float ContentExtent(int itemCount, float crossSize) => Math.Max(0, itemCount) * Extent;

    public void Window(int itemCount, float crossSize, float viewportExtent, float scrollOffset, int overscan,
                       out int first, out int last)
    {
        first = 0;
        last = itemCount < cachedCount
            ? cachedCount
            : Math.Min(itemCount, Math.Max(1, (int)MathF.Ceiling(viewportExtent / Extent) + overscan));
    }

    public RectF ItemRect(int index, float crossSize) => new(0f, index * Extent, crossSize, Extent);
}

sealed class VirtualCountShrinkProbe(int initialCount) : Component
{
    public readonly Signal<int> Count = new(initialCount);
    readonly IVirtualLayout _layout = new StaleCountVirtualLayout(initialCount);
    public int MaxRenderedIndex = -1;

    public override Element Render()
        => Virtual.Custom(Count.Value, _layout,
               i => { MaxRenderedIndex = Math.Max(MaxRenderedIndex, i); return new BoxEl { Height = 40f }; },
               keyOf: i => "stale-" + i)
           with { Width = 300, Height = 400 };
}

// Scroll-position restoration: a 1000-row uniform virtual list in a 200px viewport, whose ScrollKey (content identity)
// and presence are signal-driven so a test can simulate a cold remount (Mounted off→on) or a content swap (Key change)
// and assert the saved offset is seeded BEFORE the first realize (no scroll-to-top flash), with a new key starting at top.
sealed class ScrollRestoreProbe : Component
{
    public readonly Signal<bool> Mounted = new(true);
    public readonly Signal<string> Key = new("A");
    public override Element Render()
    {
        Element list = Mounted.Value
            ? Virtual.List(1000, 20f, i => new BoxEl { Height = 20, Fill = ColorF.FromRgba(30, 30, 30) }, keyOf: i => "r" + i)
                  with { ScrollKey = Key.Value, Width = 300, Height = 200 }
            : new BoxEl { Width = 300, Height = 200 };
        return new BoxEl { Width = 320, Height = 220, Children = [list] };
    }
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

// A component-backed bound row deliberately mixes a component snapshot with an index subscription, mirroring Wavee's
// TrackRow. Used to prove a stable published virtual range still reports progress when its slot signals need rebinding.
sealed class StableRangeBoundRow(IReadSignal<int> index) : Component
{
    public IReadSignal<int> Index { get; } = index;
    public int LastRendered = -1;

    public override Element Render()
    {
        LastRendered = Index.Value;
        return new TextEl("row " + LastRendered);
    }
}

// Wave 4 (E4/E5/E6) budget probes. FIVE stacked uniform virtual lists — used to prove the scene-owned dirty queue
// realizes ONLY the marked viewports (no _virtuals dictionary scan).
sealed class MultiVirtualProbe : Component
{
    public const int Lists = 5;
    public override Element Render()
    {
        var lists = new Element[Lists];
        for (int k = 0; k < Lists; k++)
        {
            int kk = k;
            lists[k] = Virtual.List(200, 40f, i => new BoxEl { Height = 40f, Fill = ColorF.FromRgba(30, 30, 30) }, keyOf: i => $"l{kk}r{i}")
                       with { Width = 300, Height = 120 };
        }
        return VStack(0, lists) with { Width = 320, Height = 640 };
    }
}

// A HORIZONTAL virtual rail whose presence toggles on a signal — mounting it (as a nested rail scrolls into view does)
// must realize the VISIBLE cards only, then trickle the overscan halo in over subsequent frames via the row budget.
sealed class NestedRailProbe : Component
{
    public readonly Signal<bool> Show = new(false);
    public override Element Render()
    {
        Element rail = Show.Value
            ? Virtual.List(400, 60f, i => new BoxEl { Width = 60f, Height = 100f, Fill = ColorF.FromRgba(40, 40, 40) }, keyOf: i => "c" + i)
                  with { Horizontal = true, Width = 300, Height = 120, Overscan = 6 }
            : new BoxEl { Width = 300, Height = 120 };
        return new BoxEl { Width = 320, Height = 140, Children = [rail] };
    }
}

// The ItemsView BOUND row path (CreateBound): rows are persistent slots, so selection flips a bound pill OPACITY and
// Generic async-command primitive: a SINGLE fire-on-demand command. The probe exposes the AsyncCommand so the test can
// drive a TaskCompletionSource-backed op and assert the IsRunning lifecycle / re-entry guard / cancel.
sealed class AsyncCommandProbe : Component
{
    public AsyncCommand Cmd = null!;
    public override Element Render() { Cmd = UseAsyncCommand(); _ = Cmd.IsRunning; return new BoxEl(); }
}

// The KEYED variant — independent per-key in-progress state.
sealed class AsyncCommandsProbe : Component
{
    public AsyncCommandSet<int> Cmds = null!;
    public override Element Render() { Cmds = UseAsyncCommands<int>(); _ = Cmds.AnyRunning(); return new BoxEl(); }
}

// ── Timing-hook probes (gate.timer.*) ────────────────────────────────────────────────────────────────────────────
// Each exposes the hook result + a writable source so the gate can drive it deterministically over the headless frame
// clock (host.Paint(0) advances the FixedFrameTimeSource step per frame). The render reads no signal → mounts once.
sealed class DebounceProbe : Component
{
    private readonly float _ms;
    public DebounceProbe(float ms) => _ms = ms;
    public readonly Signal<string> Source = new("");
    public IReadSignal<string> Debounced = null!;
    public DebounceHandle Handle;
    public override Element Render() { Debounced = UseDebouncedValue<string>(Source, _ms, out var h); Handle = h; return new BoxEl(); }
}

sealed class ThrottleProbe : Component
{
    private readonly float _ms;
    public ThrottleProbe(float ms) => _ms = ms;
    public readonly Signal<int> Source = new(0);
    public IReadSignal<int> Throttled = null!;
    public override Element Render() { Throttled = UseThrottledValue<int>(Source, _ms); return new BoxEl(); }
}

sealed class TimeoutProbe : Component
{
    private readonly float _ms;
    public TimeoutProbe(float ms) => _ms = ms;
    public int Fires;
    public TimerHandle Handle;
    public override Element Render() { Handle = UseTimeout(() => Fires++, _ms); return new BoxEl(); }
}

sealed class IntervalProbe : Component
{
    private readonly float _ms;
    public IntervalProbe(float ms) => _ms = ms;
    public int Ticks;
    public override Element Render() { UseInterval(() => Ticks++, _ms); return new BoxEl(); }
}

// A parent that conditionally mounts a timeout-owning child so the gate can UNMOUNT it (Flow.Show flip) before the fire.
sealed class TimeoutUnmountParent : Component
{
    public readonly Signal<bool> Show = new(true);
    public int Fires;
    public override Element Render() => Flow.Show(() => Show.Value, Embed.Comp(() => new TimeoutUnmountChild(this)));
}

sealed class TimeoutUnmountChild : Component
{
    private readonly TimeoutUnmountParent _p;
    public TimeoutUnmountChild(TimeoutUnmountParent p) => _p = p;
    public override Element Render() { UseTimeout(() => _p.Fires++, 200f); return new BoxEl(); }
}

// A trivial inert root for the warm-cadence gate (no interaction → any post-input activity is warm-cadence alone).
sealed class InertBoxProbe : Component
{
    public override Element Render() => new BoxEl { Width = 100, Height = 100 };
}

// now-playing recolours a bound TEXT WITHOUT re-running the row template (the anti-flash proof — a re-run would mean a
// rebuild/remount + Enter replay). Content carries no per-row `$"…"` so the re-skin frames stay 0-alloc on the paint phases.
sealed class BoundItemsViewProbe : Component
{
    public const int N = 2_000;
    public const float RowH = 40f;
    public readonly SelectionModel Selection = new();
    public readonly Signal<int> NowPlaying = new(-1);   // the "current track" index the title-colour bind compares against
    public int TemplateCalls;                            // bound rowTemplate invocations (slot mounts) — must stay flat on a re-skin
    public override Element Render()
        => new BoxEl
        {
            Width = 360, Height = 400,
            Children =
            [
                ItemsView.CreateBound(N, scope =>
                {
                    TemplateCalls++;
                    var idx = scope.Index;
                    var now = NowPlaying;
                    Element content = new TextEl("row")
                    {
                        Size = 13f,
                        Color = Prop.Of(() => now.Value == idx.Value
                            ? ColorF.FromRgba(0x4C, 0xC2, 0xFF) : ColorF.FromRgba(0xE0, 0xE0, 0xE0)),
                    };
                    return SelectorVisualsBound.AccentPill(scope, content);
                }, RepeatLayout.Stack(RowH), new ListOptions { SelectionMode = ItemsSelectionMode.Multiple, Selection = Selection }),
            ],
        };
}

readonly record struct BoundAtomicItem(string Title, string Artist, string Album, int DurationSeconds);

// Same-count source replacement is the failure mode that used to expose mount-owned row captures: the slot and its
// nodes stay alive, while every independently bound cell must advance to one coherent current item snapshot.
sealed class BoundAtomicItemsProbe : Component
{
    static readonly BoundAtomicItem Fallback = new("", "", "", 0);
    public readonly Signal<IReadOnlyList<BoundAtomicItem>> Items = new(new BoundAtomicItem[]
    {
        new("old-title", "old-artist", "old-album", 101),
        new("other-title", "other-artist", "other-album", 202),
    });
    public BoundItemsSource<BoundAtomicItem> Source = null!;
    public int TemplateCalls;

    public override Element Render()
    {
        Source = UseMemo(() => BoundItems.From(Items, Fallback), DepKey.Empty);
        return new BoxEl
        {
            Width = 420f, Height = 160f,
            Children =
            [
                ItemsView.CreateBound(Source, scope =>
                {
                    TemplateCalls++;
                    var item = scope.Item;
                    return new BoxEl
                    {
                        Direction = 0, MinHeight = 40f,
                        Children =
                        [
                            new TextEl(Prop.Of(() => item.Value.Title)),
                            new TextEl(Prop.Of(() => item.Value.Artist)),
                            new TextEl(Prop.Of(() => item.Value.Album)),
                            new TextEl(Prop.Of(() => item.Value.DurationSeconds.ToString())),
                        ],
                    };
                }, RepeatLayout.Stack(40f)),
            ],
        };
    }
}

sealed class BoundCountSignalProbe : Component
{
    public readonly Signal<int> Count = new(4);

    public override Element Render()
        => new BoxEl
        {
            Width = 240,
            Height = 400,
            Children =
            [
                ItemsView.CreateBound(4, scope => new BoxEl
                {
                    MinHeight = 40f,
                    Fill = ColorF.FromRgba(30, 30, 30),
                    Children = [new TextEl("") { Size = 12f, Text = Prop.Of(() => "row " + scope.Index.Value) }],
                }, RepeatLayout.Stack(40f), new ListOptions { CountSignal = Count }),
            ],
        };
}

// Detail-resize-flicker Fix-1 gate: a tier-keyed remount with cold stagger OFF realizes the full viewport window in
// ONE frame (mirrors TrackList's !_listRealizedOnce stagger gate after the first Ready mount).
sealed class ColdStaggerRemountProbe : Component
{
    public readonly Signal<int> Tier = new(0);
    public int TemplateCalls;
    bool _listRealizedOnce;

    public override Element Render()
    {
        int tier = Tier.Value;
        bool staggerCold = !_listRealizedOnce;
        _listRealizedOnce = true;
        return new BoxEl
        {
            Width = 400, Height = 320, Direction = 1,
            Children =
            [
                new BoxEl
                {
                    Key = "tier:" + tier, Grow = 1f,
                    Children =
                    [
                        ItemsView.CreateBound(80, scope =>
                        {
                            TemplateCalls++;
                            return new BoxEl { MinHeight = 40f, Children = [new TextEl("row") { Size = 13f }] };
                        }, RepeatLayout.Stack(40f), new ListOptions { Entrance = new EntranceOptions { StaggerColdRealize = staggerCold } }),
                    ],
                },
            ],
        };
    }
}

// Detail-resize-flicker Fix-2 gate: a warming staggered list must keep refilling during modal-loop keep-alive paints
// even when ambient loop animation is the only other wake reason.
sealed class ModalWarmProbe : Component
{
    public override Element Render() => new BoxEl
    {
        Width = 400, Height = 320,
        Children =
        [
            ItemsView.CreateBound(200, _ => new BoxEl { MinHeight = 40f, Fill = ColorF.FromRgba(30, 30, 30) },
                RepeatLayout.Stack(40f), new ListOptions { Entrance = new EntranceOptions { StaggerColdRealize = true } }),
        ],
    };
}

// G5h (WS3 P7) — a flexible ItemsView probe for the list-consolidation gates: drives Create (RenderItem) or CreateBound
// (bound slots) through the new ListOptions surface, with a rowBind/template BUILD counter (fresh builds/rebuilds only —
// never a cheap rebind) and an optional capture of item 0's slot index-signal (to observe keep-alive park vs recycle).
sealed class ListOptProbe : Component
{
    public int Count = 100;
    public float Extent = 40f;
    public float Vw = 360f, Vh = 240f;
    public int Overscan = 2;
    public bool Bound = true;
    public bool CaptureSig0;
    public ListOptions? Options;
    public RepeatLayout? ExplicitLayout;      // null ⇒ Stack(Extent)
    public Func<int, float>? RowHeightOf;     // per-index content height (measured layouts); null ⇒ Extent
    public int Builds;                        // template/rowBind invocations (fresh build/rebuild — NOT a rebind)
    public IReadSignal<int>? Sig0;

    public override Element Render()
    {
        var layout = ExplicitLayout ?? RepeatLayout.Stack(Extent);
        var opts = Options ?? new ListOptions { Overscan = Overscan, Grow = 1f };
        Element list = Bound
            ? ItemsView.CreateBound(Count, scope =>
              {
                  Builds++;
                  if (CaptureSig0 && Sig0 is null && scope.Index.Peek() == 0) Sig0 = scope.Index;
                  var idx = scope.Index;
                  return new BoxEl
                  {
                      MinHeight = Extent,
                      Children = [new TextEl("") { Size = 12f, Text = Prop.Of(() => "row " + idx.Value) }],
                  };
              }, layout, opts)
            : ItemsView.Create(Count, i =>
              {
                  Builds++;
                  return new BoxEl
                  {
                      MinHeight = RowHeightOf?.Invoke(i) ?? Extent,
                      Children = [new TextEl("row") { Size = 12f }],
                  };
              }, layout, opts);
        return new BoxEl { Width = Vw, Height = Vh, Children = [list] };
    }
}

// Reproduces the Wavee bound-row SHAPE exactly: a ZStack skin (OnPointerPressed = the row tap) whose content is a
// COMPONENT (mirrors BoundRowContent) wrapping a GridEl that holds a CLICKABLE child (the heart) + an inline-link
// SpanTextEl (the album link). The question: does a click on the child reach the child, or fall through to the row?
sealed class RowButtonHitProbe : Component
{
    public int RowPress, HeartClick, LinkClick;
    public override Element Render()
    {
        Element rowContent = Embed.Comp(() => new RowGridComp(this));
        Element skin = new BoxEl
        {
            ZStack = true, MinHeight = 48f, ClipToBounds = true, Focusable = false,
            OnPointerPressed = _ => RowPress++,
            OnPointerExit = static () => { },
            Children =
            [
                new BoxEl { Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center, Children = [rowContent] },
                new BoxEl { Key = "pill", Width = 3f, Height = 16f, HitTestVisible = false, Opacity = 0f, AlignSelf = FlexAlign.Center },
            ],
        };
        return new BoxEl { Width = 400f, Height = 60f, Children = [skin] };
    }

    sealed class RowGridComp : Component
    {
        readonly RowButtonHitProbe _o;
        public RowGridComp(RowButtonHitProbe o) { _o = o; }
        public override Element Render() => new GridEl
        {
            Columns = [TrackSize.Px(40f), TrackSize.Px(40f), TrackSize.Star(), TrackSize.Px(120f)], Grow = 1f, RowHeight = 48f,
            Children =
            [
                new BoxEl { AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [new TextEl("1")] },
                new BoxEl { AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [new BoxEl { Width = 28f, Height = 28f, OnClick = () => _o.HeartClick++,
                                            HoverFill = ColorF.FromRgba(40, 40, 40), Children = [new TextEl("H")] }] },
                new TextEl("title"),
                new BoxEl { AlignItems = FlexAlign.Center, Children = [
                    new SpanTextEl([new TextSpan("album", OnClick: () => _o.LinkClick++)]) { Size = 13f } ] },
            ],
        };
    }
}

// A LARGE bound list whose rows bind ONLY a struct channel (Fill) — no per-row `$"…"` interpolation, so a cross-boundary
// recycle re-runs the row's bind WITHOUT minting a never-before-seen string into the interner. This isolates the fling
// integrator + virtual re-realize + slot-rebind MACHINERY (which must be 0-alloc) from the user-template string churn that
// any unique-text-per-row list streams through StringTable.Intern (a 100k-row list's documented bounded-Gen0 reconcile edge).
// The VIRTUALIZED bound path: a clickable child inside a CreateBound slot row. Tests that tapping an in-row button on a
// realized bound SLOT (RealizeBoundWindow + the scroll content transform) reaches the child, not the row's tap handler.
sealed class BoundListHitProbe : Component
{
    public int RowPress, ChildClick;
    public readonly SelectionModel Selection = new();
    public readonly Signal<int> Now = new(-1);   // a "now-playing" signal the row CONTENT re-renders on (like BoundRowContent)
    public override Element Render()
        => new BoxEl
        {
            Width = 360, Height = 400,
            Children =
            [
                ItemsView.CreateBound(50, scope =>
                {
                    var onInteraction = scope.OnInteraction;
                    return new BoxEl
                    {
                        ZStack = true, MinHeight = 40f, Focusable = false, ClipToBounds = true,
                        // The Wavee skin's once-per-slot reveal — a faithful repro detail.
                        Animate = new LayoutTransition(TransitionChannels.Opacity,
                            TransitionDynamics.Tween(280f, Easing.FluentDecelerate),
                            Enter: new EnterExit(Opacity: 0f, Active: true)),
                        OnPointerPressed = args => { RowPress++; onInteraction(
                            args.ClickCount >= 2 ? ItemContainerTrigger.DoubleTap : ItemContainerTrigger.Tap, args.Mods); },
                        OnPointerExit = static () => { },
                        Children = [new BoxEl { Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center,
                                                Children = [Embed.Comp(() => new HitRowContent(this, scope.Index))] }],
                    };
                }, RepeatLayout.Stack(40f), new ListOptions { Selection = Selection }),
            ],
        };

    // Mirrors BoundRowContent: re-renders on the index signal + the Now signal, rebuilding the clickable child each time.
    sealed class HitRowContent : Component
    {
        readonly BoundListHitProbe _o; readonly IReadSignal<int> _idx;
        public HitRowContent(BoundListHitProbe o, IReadSignal<int> idx) { _o = o; _idx = idx; }
        public override Element Render()
        {
            int i = _idx.Value;                 // recycle → re-render
            bool now = _o.Now.Value == i;       // now-playing → re-render (rebuilds the child + handler)
            // Mirror the Wavee # cell: a fixed-width ZStack of a HoverOpacity reveal layer + a Grow=1f click-catcher.
            return new BoxEl
            {
                Width = 40f, ZStack = true,
                Children =
                [
                    new BoxEl { Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                                Opacity = 0f, HoverOpacity = 1f, Children = [new TextEl(now ? "P" : "H")] },
                    new BoxEl { Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                                OnClick = () => _o.ChildClick++ },
                ],
            };
        }
    }
}

sealed class BoundVirtualFillOnlyProbe : Component
{
    public const int N = 10_000;
    public override Element Render()
        => Virtual.ListBound(N, 40f, idx => new BoxEl
           {
               Height = 40,
               Fill = Prop.Of(() => ColorF.FromRgba(30, 30, (byte)(idx.Value % 2 == 0 ? 30 : 50))),
           })
           with { Width = 300, Height = 400 };
}

// A SMALL bound list (few rows ⇒ the content end is reachable, so a touch fling decays then settles at the clamp within a
// bounded number of frames — the WinUI ScrollPresenter default 0.95/s decay is near-frictionless). Viewport = Scene.Root.
sealed class TouchFlingSettleProbe : Component
{
    public const int N = 80;   // 80 × 40 = 3200px content, 400px viewport ⇒ 2800px max offset (reachable by a flick)
    public override Element Render()
        => Virtual.ListBound(N, 40f, idx => new BoxEl
           {
               Height = 40,
               Fill = Prop.Of(() => ColorF.FromRgba(30, 30, (byte)(idx.Value % 2 == 0 ? 30 : 50))),
           })
           with { Width = 300, Height = 400 };
}

// A bound virtual list sized so a touch flick lands MID-LIST (the clamp is far away) — the snap-fling probe. The test
// sets ScrollState.SnapInterval = RowH on the viewport after mount (the reconciler patches Orientation/ItemCount but
// never touches the snap fields, so a post-mount SnapInterval survives every reconcile). A flick then retargets its
// friction decay to land EXACTLY on a RowH multiple (ScrollSnap + ScrollIntegrator). Large content keeps the snap target
// interior (never clamp-bounded), so the landing is purely the snap math. Viewport = Scene.Root.
sealed class SnapFlingProbe : Component
{
    public const int N = 400;          // 400 × 50 = 20000px content, 400px viewport ⇒ 19600px max offset (snap target stays interior)
    public const float RowH = 50f;
    public override Element Render()
        => Virtual.ListBound(N, RowH, idx => new BoxEl
           {
               Height = RowH,
               Fill = Prop.Of(() => ColorF.FromRgba(30, 30, (byte)(idx.Value % 2 == 0 ? 30 : 50))),
           })
           with { Width = 300, Height = 400 };
}

// Perf plan W2-P2.2 (MotionSuppressionSource.Scroll) probe: a wheel-scrollable viewport NEXT TO a FLIP-animated box
// whose slot a signal-driven reconcile moves (the spacer above it flips 20↔150px). The gate drives a real wheel notch
// through the dispatcher, then reconciles on the post-offset-write frame (scroll-coincident ⇒ snap) vs a still frame
// (⇒ the structural FLIP track seeds normally).
sealed class ScrollFlipProbe : Component
{
    public readonly FluentGpu.Signals.Signal<bool> Moved = new(false);
    public NodeHandle Box;
    static readonly LayoutTransition Slide = new(TransitionChannels.Position, TransitionDynamics.Spring(1.0f, 1.0f));
    public override Element Render()
    {
        bool moved = Moved.Value;   // subscribe → re-render on the flip
        var rows = new Element[20];   // 20 × 40 = 800px content in a 300px viewport ⇒ wheel-scrollable
        for (int i = 0; i < rows.Length; i++) rows[i] = new BoxEl { Height = 40f, Fill = ColorF.FromRgba(30, 30, (byte)(i % 2 == 0 ? 30 : 60)) };
        return new BoxEl
        {
            Direction = 0, Grow = 1f,
            Children =
            [
                Ui.ScrollView(new BoxEl { Direction = 1, Children = rows }) with { Width = 200, Height = 300 },
                new BoxEl
                {
                    Direction = 1, Width = 120f,
                    Children =
                    [
                        new BoxEl { Width = 50f, Height = moved ? 150f : 20f },                     // the slot-moving spacer
                        new BoxEl { Width = 50f, Height = 20f, Fill = ColorF.FromRgba(200, 80, 40), Animate = Slide, OnRealized = h => Box = h },
                    ],
                },
            ],
        };
    }
}

// scroll-feel-rework-v2 §8 HeadlessScrollProducer: scripts all six input kinds (contact begin/update/end, OS momentum,
// discrete wheel notch, pointer-down-cancel) with SYNTHETIC timestamps into the headless Pal ring, and drives frames
// while feeding the phase-7 integrator a synthetic frame clock (FrameQpcSec) so the §4.1 frame-time resampler runs
// deterministically headless (the AppHost only wires the wall clock on real backends). Packet stamps live on the ms
// domain (QpcTicks=0 ⇒ ContactSampleSec falls back to TimestampMs/1000), and FrameMs drives FrameQpcSec on the SAME
// domain, so the resampler targets FrameMs−5ms honestly. No wall clock; 0-alloc per event (record-struct queue only).
sealed class HeadlessScrollProducer
{
    readonly HeadlessWindow _win;
    readonly AppHost _host;
    readonly Point2 _at;
    byte _seq;
    public uint Ms;          // per-packet stamp clock (ms)
    public double FrameMs;   // frame-present clock → FrameQpcSec (ms)
    public byte Device = (byte)ScrollDeviceClass.WheelHiResFallback;   // fallback ⇒ a hard lift self-flings (§4.3)
    public uint PointerId = 9;

    public HeadlessScrollProducer(HeadlessWindow win, AppHost host, Point2 at, uint startMs = 5000)
    { _win = win; _host = host; _at = at; Ms = startMs; FrameMs = startMs; }

    InputEvent Ph(InputKind k, float dyDip) => new(k, _at, 0, 0, ScrollDelta: dyDip,
        Pointer: PointerKind.Touchpad, TimestampMs: Ms, PointerId: PointerId, ScrollPhaseSeq: _seq++, DeviceClassRaw: Device);

    public void ContactBegin(float dyDip = 0f) => _win.QueueInput(Ph(InputKind.ScrollBegin, dyDip));
    public void ContactUpdate(float dyDip) => _win.QueueInput(Ph(InputKind.ScrollUpdate, dyDip));
    public void ContactEnd() => _win.QueueInput(Ph(InputKind.ScrollEnd, 0f));
    public void MomentumBegin() => _win.QueueInput(Ph(InputKind.MomentumBegin, 0f));
    public void MomentumUpdate(float dyDip) => _win.QueueInput(Ph(InputKind.MomentumUpdate, dyDip));
    public void MomentumEnd() => _win.QueueInput(Ph(InputKind.MomentumEnd, 0f));
    public void WheelNotch(float notches) => _win.QueueInput(new InputEvent(InputKind.Wheel, _at, 0, 0,
        Pointer: PointerKind.Mouse, WheelNotch: notches, TimestampMs: Ms));
    public void PointerDownAt(Point2 p, PointerKind kind = PointerKind.Mouse, uint id = 0) =>
        _win.QueueInput(new InputEvent(InputKind.PointerDown, p, 0, 0, Pointer: kind, TimestampMs: Ms, PointerId: id));

    /// <summary>Present one frame: set the synthetic FrameQpcSec (headless never overwrites it), run, then advance the
    /// frame clock by <paramref name="dtMs"/>. Folds this frame's offset-write count into the single-writer audit.</summary>
    public FrameStats Frame(float dtMs)
    {
        _host.ScrollIntegratorForTest.FrameQpcSec = FrameMs / 1000.0;
        var f = _host.RunFrame();
        FluentGpu.Foundation.ScrollTrace.AuditResetFrame();
        FrameMs += dtMs;
        return f;
    }

    /// <summary>Present one frame AND advance the packet clock in lockstep (Ms == FrameMs) for gates that don't script
    /// sub-frame packet cadence.</summary>
    public FrameStats Step(float dtMs) { var f = Frame(dtMs); Ms = (uint)Math.Round(FrameMs); return f; }
}

// A virtualized list whose rows are clickable (the tap target). A below-slop touch down→up over a row TAPS it (the row's
// OnClick fires); a touch drag over the list claims the pan, cancels the row's press, and never clicks. Row 0's press +
// click are counted on the probe so a tap and a pan are distinguishable. Viewport = Scene.Root.
sealed class TouchTapPanProbe : Component
{
    public int Row0Pressed, Row0Clicked;
    public override Element Render()
        => Virtual.List(2000, 60f, i => new BoxEl
           {
               Height = 60, Fill = ColorF.FromRgba(30, 30, 30),
               OnPointerPressed = i == 0 ? _ => Row0Pressed++ : null,
               OnClick = i == 0 ? () => Row0Clicked++ : () => { },
           }, keyOf: i => "r" + i)
           with { Width = 300, Height = 400 };
}

// An EditableText sitting INSIDE a vertical ScrollView (tall filler below it makes the viewport scroll): the touch
// single-recognizer arbitration probe. A touch drag that STARTS on the text must extend the editor's selection (the
// OnDrag implicit-capture wins, content does NOT pan); a touch drag that starts on the non-text filler below still pans
// the scroller. Exposes the live EditableText instance so the test reads SelectionStart/Length after a touch drag.
sealed class TouchEditInScrollerProbe : Component
{
    public Signal<string>? Text;
    public EditableText? Edit;
    public override Element Render()
    {
        var t = UseSignal("hello world");
        Text = t;
        return Ui.ScrollView(new BoxEl
        {
            Direction = 1,
            Children =
            [
                Embed.Comp(() =>
                {
                    var e = new EditableText { Text = t, Width = 160f, Height = 32f };
                    Edit = e;
                    return e;
                }),
                new BoxEl { Width = 160f, Height = 1600f, Fill = ColorF.FromRgba(30, 30, 30) },   // tall filler ⇒ scrollable
            ],
        }) with { Width = 200f, Height = 240f };
    }
}

// The SIP (touch keyboard) reflow probe (gate.touch4.sip.reflow): an EditableText sitting BELOW a filler band inside a
// vertical ScrollView, with a tall filler below it (so the viewport overflows on Y and the field can be lifted up). The
// test touch-focuses the field (near the viewport bottom), fires a simulated InputPane Showing OccludedRect that covers
// the field, and asserts the scroller scrolled the field's caret above the pane (the WinUI EnsureFocusedElementInView).
sealed class TouchSipReflowProbe : Component
{
    public EditableText? Edit;
    public override Element Render()
    {
        var t = UseSignal("type here");
        return Ui.ScrollView(new BoxEl
        {
            Direction = 1,
            Children =
            [
                new BoxEl { Width = 160f, Height = 180f, Fill = ColorF.FromRgba(28, 28, 28) },   // filler ABOVE → the field sits lower
                Embed.Comp(() =>
                {
                    var e = new EditableText { Text = t, Width = 160f, Height = 32f };
                    Edit = e;
                    return e;
                }),
                new BoxEl { Width = 160f, Height = 1600f, Fill = ColorF.FromRgba(30, 30, 30) },   // filler BELOW ⇒ headroom to scroll
            ],
        }) with { Width = 200f, Height = 240f };
    }
}

// A HORIZONTAL strip of CanDrag items (their parent is Direction=0 ⇒ item-drag axis is X) inside a VERTICAL scroller
// (tall filler below ⇒ the viewport overflows on Y). The §7A drag-reorder-vs-pan arena probe: a horizontal touch drag on
// item 0 runs ALONG the item axis ⇒ DragReorder wins (the item lifts/translates, OnDragStarted fires, the list does NOT
// scroll); a vertical touch drag is CROSS-axis ⇒ Pan wins (the scroller scrolls, no drag starts). Both via the arena's
// axis-locked votes — DragController.YieldsToPan is bypassed on the touch path.
sealed class ArenaReorderInScrollerProbe : Component
{
    public int StartedA, DeltaA, CompletedA;
    public override Element Render()
        => Ui.ScrollView(new BoxEl
        {
            Direction = 1,
            Children =
            [
                // The draggable strip: Direction=0 ⇒ each child's reorder axis is horizontal.
                new BoxEl
                {
                    Direction = 0, Gap = 8, Height = 80,
                    Children =
                    [
                        new BoxEl
                        {
                            Key = "a", Width = 120, Height = 80, Fill = ColorF.FromRgba(50, 40, 40), CanDrag = true,
                            OnDragStarted = _ => StartedA++, OnDragDelta = _ => DeltaA++, OnDragCompleted = _ => CompletedA++,
                        },
                        new BoxEl { Key = "b", Width = 120, Height = 80, Fill = ColorF.FromRgba(40, 50, 40), CanDrag = true },
                        new BoxEl { Key = "c", Width = 120, Height = 80, Fill = ColorF.FromRgba(40, 40, 50), CanDrag = true },
                    ],
                },
                new BoxEl { Width = 300, Height = 1600f, Fill = ColorF.FromRgba(28, 28, 28) },   // tall filler ⇒ vertical overflow
            ],
        }) with { Width = 320f, Height = 300f };
}

// A component that declares UseGesture(Tap): a tap on its node routes the §13 gesture event to the handler with the
// gesture position. Exposes the captured count + position so the gate can assert correctness end-to-end.
sealed class UseGestureTapProbe : Component
{
    public int Taps;
    public Point2 LastPos;
    public override Element Render()
    {
        // Declare the gesture on THIS component's node (HostNode = the returned BoxEl). The box is NOT otherwise
        // clickable — the arena enrolls a Tap member purely from the UseGesture declaration, proving the §13 enrollment.
        UseGesture(GestureType.Tap, e => { Taps++; LastPos = e.Position; });
        return new BoxEl { Width = 200, Height = 120, Fill = ColorF.FromRgba(40, 40, 60) };
    }
}

// A SwipeControl row (right reveal actions) at the top of a VERTICAL scroller with a tall filler (vertical overflow).
// The canonical §7A arena race: a horizontal touch swipe along the row axis must reveal the actions (the swipe's
// cross-axis Drag eager-wins the arena, the list does NOT scroll), while a vertical drag on the SAME row must scroll the
// list (the scroll-axis-locked Pan eager-wins, the swipe stays closed) — deterministic, via the axis-locked Drag-vs-Pan
// votes (DragYieldsToPan). Exposes the Delete action's invoke count so the gate can also confirm a revealed item taps.
sealed class SwipeInScrollerProbe : Component
{
    public int Deleted;
    public override Element Render()
        => Ui.ScrollView(new BoxEl
        {
            Direction = 1,
            Children =
            [
                SwipeControl.Create("Quarterly report.docx", new[]
                {
                    new SwipeAction(Icons.Cancel, "Delete", ColorF.FromRgba(0xC4, 0x2B, 0x1C)) { OnInvoked = () => Deleted++ },
                }),
                new BoxEl { Width = 300, Height = 1600f, Fill = ColorF.FromRgba(28, 28, 28) },   // tall filler ⇒ vertical overflow
            ],
        }) with { Width = 360f, Height = 300f };
}

// A SwipeControl wrapping a CLICKABLE inner row (OnClick, NO drag handler of its own) at the top of a vertical scroller
// — the Phase-D §7A.1 route-walk case: the hit row advertises no OnDrag, so the dispatcher must WALK to the wrapper's
// DragYieldsToPan Drag member for a swipe to arm at all. touchOnly ⇒ a mouse never pans. Exposes the inner row's click
// count so the gate proves a horizontal swipe does NOT click the row while a below-slop tap DOES.
sealed class SwipeWrappedRowProbe : Component
{
    public int RowClicks;
    public override Element Render()
        => Ui.ScrollView(new BoxEl
        {
            Direction = 1,
            Children =
            [
                SwipeControl.Create(
                    new BoxEl
                    {
                        MinWidth = 320f, MinHeight = 56f, Padding = new Edges4(16, 14, 16, 14),
                        Fill = ColorF.FromRgba(40, 40, 40), AlignItems = FlexAlign.Center,
                        Role = AutomationRole.Button, OnClick = () => RowClicks++,
                        Children = [new TextEl("Quarterly report.docx") { Size = 14f, Color = ColorF.FromRgba(230, 230, 230) }],
                    },
                    rightActions: new[] { new SwipeAction(Icons.Cancel, "Delete", ColorF.FromRgba(0xC4, 0x2B, 0x1C)) },
                    rightMode: SwipeMode.Reveal,
                    touchOnly: true),
                new BoxEl { Width = 300, Height = 1600f, Fill = ColorF.FromRgba(28, 28, 28) },   // tall filler ⇒ vertical overflow
            ],
        }) with { Width = 360f, Height = 300f };
}

// A SwipeControl fed a ResetKey signal — the bound-slot RECYCLE contract: bumping the key must snap the (open) row
// closed with no animation, exactly as a scrolled-off slot rebinds to a new track and must present it closed.
sealed class SwipeResetProbe : Component
{
    public readonly Signal<int> ResetKey = new(0);
    public override Element Render()
        => Ui.ScrollView(new BoxEl
        {
            Direction = 1,
            Children =
            [
                SwipeControl.Create(
                    new BoxEl
                    {
                        MinWidth = 320f, MinHeight = 56f, Padding = new Edges4(16, 14, 16, 14),
                        Fill = ColorF.FromRgba(40, 40, 40), AlignItems = FlexAlign.Center,
                        Role = AutomationRole.Button, OnClick = static () => { },
                        Children = [new TextEl("Quarterly report.docx") { Size = 14f, Color = ColorF.FromRgba(230, 230, 230) }],
                    },
                    rightActions: new[] { new SwipeAction(Icons.Cancel, "Delete", ColorF.FromRgba(0xC4, 0x2B, 0x1C)) },
                    rightMode: SwipeMode.Reveal, resetKey: ResetKey, touchOnly: true),
                new BoxEl { Width = 300, Height = 1600f, Fill = ColorF.FromRgba(28, 28, 28) },
            ],
        }) with { Width = 360f, Height = 300f };
}

// A FlipView (UseTouchAnimationsForAllNavigation default true) over three string pages — exposes the live selected index
// so the gate can drive a touch flick / slow drag and assert the velocity-aware MandatorySingle commit (a flick navigates
// even short of 50%; a slow drag under 50% springs back to the same page).
sealed class FlipFlickProbe : Component
{
    public int Selected;
    public override Element Render()
    {
        var sel = UseSignal(0);
        return FlipView.Create(new[] { "Page A", "Page B", "Page C" }, width: 400f, height: 240f,
                           selectedIndex: sel, onChange: i => Selected = i);
    }
}

// A box that requests a context menu (the touch Hold target — touch has no right button, so a long-press is the only
// path to a context request). The arena enrolls a Hold member; the long-press timer (TickGestureArenas) promotes it to
// EagerAccept ≥500ms, which wins. Used by the §12.6 determinism replay (the "hold" leg of the scripted sequence).
sealed class ArenaHoldProbe : Component
{
    public int Contexts;
    public override Element Render()
        => new BoxEl { Width = 240, Height = 160, Fill = ColorF.FromRgba(40, 40, 50), OnContextRequested = _ => Contexts++ };
}

// The touch-Hold EXECUTION probe (gate.touch4.hold-fires-context): a virtualized list whose ROW 0 is BOTH clickable AND
// context-requesting, inside a scroller (the rows overflow ⇒ a pan is available). The three Hold scenarios all play out
// on row 0: a STATIONARY long-press (≥500ms) fires the context request (the touch long-press → flyout, the same action a
// right-click fires); a SUB-500ms release taps it (OnClick, no context — the Hold timer never promoted); a MOVE-PAST-SLOP
// claims the scroller's Pan (the arena sweeps the Hold AND the Tap — the list scrolls, no context, no click). Exposes the
// per-channel counters + the live row-0 handle so the gate can drive each scenario and read the outcome.
sealed class TouchHoldContextProbe : Component
{
    public int Contexts, Clicks;
    public NodeHandle Row0;
    public override Element Render()
        => Virtual.List(2000, 60f, i => new BoxEl
           {
               Height = 60, Fill = ColorF.FromRgba(30, 30, 30),
               OnRealized = i == 0 ? h => Row0 = h : null,
               OnClick = i == 0 ? () => Clicks++ : () => { },
               OnContextRequested = i == 0 ? _ => Contexts++ : null,
           }, keyOf: i => "h" + i)
           with { Width = 300, Height = 300 };
}

// ContextMenu attach-layer probe (gate.ctx.*): an OverlayHost over two context-menu-attached rows (A near the top, B
// lower) plus inert background. Each row's factory is lazy (invoked at open) and the build counters prove it. ReturnNull
// / AllDisabled flip row A's factory to the no-open cases. Row B always yields a real menu (the dismiss-reopen target).
sealed class ContextMenuProbe : Component
{
    public IOverlayService? Service;
    public NodeHandle RowA, RowB;
    /// <summary>Row B's "…" ClickRequestsContext button (+ a disabled twin) — the gate.ctx.invoke-* targets: a left
    /// click on it must re-enter the context-request funnel and open row B's menu anchored at the BUTTON rect.</summary>
    public NodeHandle MoreB, MoreBDisabled;
    public int BuildsA, BuildsB;
    public bool ReturnNull, AllDisabled;
    /// <summary>Value-copied from the last ContextRequestEventArgs row B's own handler saw (the attach chains the
    /// element handler FIRST) — gate.ctx.invoke-source-field asserts Trigger/Node/Source per trigger kind.</summary>
    public ContextRequestTrigger LastTrigger;
    public NodeHandle LastNode, LastSource;
    /// <summary>Bump to force ContextMenuProbeInner to re-render (it reads this). Used by gate.ctx.re-render-anchor to
    /// prove the menu anchors from the live <c>ContextRequestEventArgs.Node</c>, not a stale OnRealized capture.</summary>
    public readonly Signal<int> Rev = new(0);
    public override Element Render() => Embed.Comp(() => new OverlayHost { Child = Embed.Comp(() => new ContextMenuProbeInner(this)) });
}

sealed class ContextMenuProbeInner : Component
{
    readonly ContextMenuProbe _p;
    public ContextMenuProbeInner(ContextMenuProbe p) => _p = p;

    ContextMenuModel? BuildA()
    {
        _p.BuildsA++;
        if (_p.ReturnNull) return null;
        if (_p.AllDisabled)
            return new ContextMenuModel(new[] { new MenuFlyoutItem("Disabled", Enabled: false), MenuFlyoutItem.Separator });
        return new ContextMenuModel(new[] { new MenuFlyoutItem("A1", Invoke: () => { }), new MenuFlyoutItem("A2", Invoke: () => { }) });
    }
    ContextMenuModel? BuildB()
    {
        _p.BuildsB++;
        return new ContextMenuModel(new[] { new MenuFlyoutItem("B1", Invoke: () => { }), new MenuFlyoutItem("B2", Invoke: () => { }) });
    }

    public override Element Render()
    {
        _p.Service = UseContext(Overlay.Service);
        _ = _p.Rev.Value;   // SUBSCRIBE: bumping Rev re-renders this inner (rebuilds rowA + its context-menu attach)
        var svc = _p.Service!;
        var rowA = new BoxEl
        {
            Width = 120, Height = 32, Role = AutomationRole.Button, OnClick = () => { },
            OnRealized = h => _p.RowA = h, Children = [Text("A")],
        }.WithContextMenu(svc, BuildA);
        var rowB = new BoxEl
        {
            Width = 120, Height = 32, Direction = 0, AlignItems = FlexAlign.Center, Gap = 8f,
            Role = AutomationRole.Button, OnClick = () => { },
            OnRealized = h => _p.RowB = h,
            // Value-copy the reused args (gate.ctx.invoke-source-field): the attach chains this handler FIRST.
            OnContextRequested = e => { _p.LastTrigger = e.Trigger; _p.LastNode = e.Node; _p.LastSource = e.Source; },
            Children =
            [
                Text("B"),
                // The "…" context-invoker (gate.ctx.invoke-*): a left click / Space-Enter on it re-enters the
                // context-request funnel here — the walk finds row B's OnContextRequested (the attach below).
                new BoxEl { Width = 20, Height = 20, Role = AutomationRole.Button, ClickRequestsContext = true, OnRealized = h => _p.MoreB = h },
                // Its disabled twin (gate.ctx.invoke-disabled): disabled nodes don't hit-test, so a click opens nothing.
                new BoxEl { Width = 20, Height = 20, Role = AutomationRole.Button, ClickRequestsContext = true, IsEnabled = false, OnRealized = h => _p.MoreBDisabled = h },
            ],
        }.WithContextMenu(svc, BuildB);
        return new BoxEl { Width = 480, Height = 400, Direction = 1, Padding = Edges4.All(20), Gap = 200f, Children = [rowA, rowB] };
    }
}

// ThemedIcon probe (gate.icon.record/retheme/alloc): a root component that mounts one layered ThemedIcon so the
// reconciler realizes VisualKind.IconLayer nodes and the recorder emits DrawIconMask into the headless device.
sealed class IconProbe : Component
{
    public string Name = "Copy";
    public FluentGpu.Controls.IconMode Mode = FluentGpu.Controls.IconMode.Layered;
    public override Element Render()
        => new BoxEl { Width = 200, Height = 200, Children = [ThemedIcon.Create(Name, 16f, mode: Mode)] };
}

// Wheel-blocking probe (gate.ctx.scrim-blocks-wheel): a scroller with overflowing content, a context-menu row above it.
// With a menu open the full-bleed scrim is the topmost hit → the ancestor-only wheel walk finds no scrollable → the list
// stays put; with the menu closed the same wheel scrolls it (proving the wheel is real and the scrim was blocking).
sealed class ContextWheelProbe : Component
{
    public IOverlayService? Service;
    public NodeHandle Row;
    public override Element Render() => Embed.Comp(() => new OverlayHost { Child = Embed.Comp(() => new ContextWheelInner(this)) });
}

sealed class ContextWheelInner : Component
{
    readonly ContextWheelProbe _p;
    public ContextWheelInner(ContextWheelProbe p) => _p = p;
    ContextMenuModel? Build() => new ContextMenuModel(new[] { new MenuFlyoutItem("W1", Invoke: () => { }), new MenuFlyoutItem("W2", Invoke: () => { }) });
    public override Element Render()
    {
        _p.Service = UseContext(Overlay.Service);
        var svc = _p.Service!;
        var row = new BoxEl
        {
            Width = 120, Height = 32, Role = AutomationRole.Button, OnClick = () => { },
            OnRealized = h => _p.Row = h, Children = [Text("wrow")],
        }.WithContextMenu(svc, Build);
        var list = Virtual.List(400, 40f, i => new BoxEl { Height = 40, Fill = ColorF.FromRgba(30, 30, 30) }, keyOf: i => "w" + i)
            with { Width = 300, Height = 300 };
        return new BoxEl { Width = 480, Height = 400, Direction = 1, Children = [row, list] };
    }
}

// A 5-star interactive RatingControl (gate.touch4.rating-tap): a touch tap on the 4th star sets the rating to 4 (the
// OnPointerDown=Sweep sets the preview at the tapped X, the OnClick=Commit on release applies it — touch tap-to-rate,
// RatingControl.cpp commit-on-release). Exposes the live value so the gate asserts the touch tap rated it.
sealed class RatingTapProbe : Component
{
    public FloatSignal? Val;
    public override Element Render()
    {
        var v = UseFloatSignal(0f);
        Val = v;
        return RatingControl.Create(v, max: 5);
    }
}

// A lone Slider (the §7A.5 single-recognizer fast-path target): one OnDrag recognizer, nothing competing. A touch drag
// must capture SYNCHRONOUSLY and scrub the value the same frame (capture is not tentative for a lone member). Exposes
// the live value so gate.arena.fastpath-sync can assert the first move moved it.
sealed class FastPathSliderProbe : Component
{
    public float Val;
    public override Element Render()
    {
        var v = UseFloatSignal(0f);
        Val = v.Peek();
        return Slider.Create(v, x => Val = x, length: 220f, thickness: 28f);
    }
}

// Two sibling virtualized lists in a row — distinct item counts so each viewport is locatable by ItemCount. Two
// concurrent touch contacts must pan them independently (per-PointerId capture), and a per-id cancel must stop only its
// own contact.
sealed class TwoListProbe : Component
{
    public const int LeftN = 1500, RightN = 2500;
    public override Element Render() => new BoxEl
    {
        Direction = 0, Gap = 0,
        Children =
        [
            Virtual.List(LeftN, 40f, _ => new BoxEl { Height = 40, Fill = ColorF.FromRgba(40, 30, 30) }, keyOf: i => "l" + i)
                with { Width = 200, Height = 300, Grow = 0f },
            Virtual.List(RightN, 40f, _ => new BoxEl { Height = 40, Fill = ColorF.FromRgba(30, 30, 40) }, keyOf: i => "r" + i)
                with { Width = 200, Height = 300, Grow = 0f },
        ],
    };
}

// A ZOOMABLE ScrollEl viewport (Phase-4 pinch): a 200×200 vertical scroller over a 200×800 content box, opted into
// pinch-zoom (Zoomable, defaults Min 0.1 / Max 10.0). NON-virtual (no ItemCount) so a pinch move is a pure transform
// frame (Rendered == false ⇒ no relayout — the no-LayoutDirty gate). The content node carries the composed scale+offset
// LocalTransform; its model Bounds (800px) never change under zoom.
sealed class PinchZoomProbe : Component
{
    public override Element Render() => new ScrollEl
    {
        Width = 200, Height = 200, Fill = ColorF.FromRgba(20, 20, 20),
        Zoomable = true,
        Content = new BoxEl { Width = 200, Height = 800, Fill = ColorF.FromRgba(40, 40, 40) },
    };
}

// Three side-by-side clickable buttons (Phase 2 pressed-no-hover): each carries a press/hover-callback counter so a touch
// TAP can be shown to drive the Pressed visual (+ PressT via the InteractionAnimator) with ZERO hover delivery, and a
// contact moving across the row by sequential taps never fires a hover callback (touch has no cursor — WinUI shows
// Pressed on touch-down, never PointerOver). Non-scrollable buttons ⇒ a pure tap, no pan-claim. PressedFill makes the
// pressed easing a real visual transition (the recorder lerps Fill→PressedFill on PressT).
sealed class TouchButtonRowProbe : Component
{
    public const int N = 3;
    public readonly int[] Pressed = new int[N];   // OnPointerPressed delivered (the press the down edge fires)
    public readonly int[] Clicked = new int[N];   // OnClick delivered (the tap's release edge)
    public int HoverCallbacks;                     // ANY OnHoverMove / OnPointerExit delivery (must stay 0 for taps)

    public override Element Render()
    {
        var kids = new Element[N];
        for (int i = 0; i < N; i++)
        {
            int idx = i;
            kids[i] = new BoxEl
            {
                Width = 80, Height = 48, Role = AutomationRole.Button,
                Fill = ColorF.FromRgba(40, 40, 40),
                HoverFill = ColorF.FromRgba(60, 60, 60),
                PressedFill = ColorF.FromRgba(80, 80, 80),
                OnPointerPressed = _ => Pressed[idx]++,
                OnClick = () => Clicked[idx]++,
                // OnHoverMove / OnPointerExit make the node hover-deliverable: if touch ever set hover on this node these
                // would fire. A faithful touch path delivers neither for a tap (and clears any transient move-hover on up).
                OnHoverMove = _ => HoverCallbacks++,
                OnPointerExit = () => HoverCallbacks++,
            };
        }
        return new BoxEl { Direction = 0, Gap = 8, Padding = Edges4.All(8), Children = kids };
    }
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

sealed class BakedImageProbe : Component
{
    public override Element Render() => new BoxEl
    {
        Width = 120, Height = 120, Padding = Edges4.All(10),
        Children =
        [
            Ui.Image("album/baked.jpg", 80, 80, 6f) with
            {
                BakedBlur = new BakedBlurSpec(26f, 0.5f),
                ColorOverlay = ColorF.FromRgba(8, 8, 10) with { A = 0.42f },
                Mask = new ImageMaskSpec(EdgeMask.Top, 24f),
            },
        ],
    };
}

// Two image nodes initially share one cache handle. Rebinding the second image must unpin its owner without canceling
// the still-visible first image's decode.
sealed class SharedImageSwapProbe : Component
{
    public readonly Signal<string> SecondSource = new("album/shared");

    public override Element Render() => new BoxEl
    {
        Direction = 0, Gap = 8f,
        Children =
        [
            Ui.Image("album/shared", 40, 40),
            new ImageEl { Source = Prop.Of(() => SecondSource.Value), Width = 40, Height = 40 },
        ],
    };
}

// A responsive (aspect-ratio) image tile inside a fixed-width card: no fixed extent — it fills the card's content width
// and derives a square height. CardWidth varies per host to prove the art scales with the cell (the overflow fix).
sealed class AspectTileProbe : Component
{
    public static float CardWidth = 200f;
    public override Element Render() => new BoxEl
    {
        Direction = 1,   // outer fills the window; the card is a child so its explicit Width is honored (not forced to window size)
        Children =
        [
            new BoxEl
            {
                Width = CardWidth, Direction = 1, Gap = 8f, Padding = Edges4.All(12),
                Children =
                [
                    Ui.Image("album/aspect", ImageFit.Cover, aspect: 1f, decodePx: 64f, corners: 8f),
                    new TextEl("t") { Size = 14f },
                ],
            },
        ],
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
        var sv = UseFloatSignal(0f);
        var on = UseSignal(false);
        var (sp, setSp) = UseState(0f);
        SliderVal = sv.Peek(); Toggled = on.Value; ScrollPos = sp;
        return new BoxEl
        {
            Direction = 1, Gap = 0,
            Children =
            [
                Slider.Create(sv, x => SliderVal = x, length: 200f, thickness: 24f),
                ToggleButton.Create("Shuffle", on),
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

// G3: BoxEl.AspectRatio derive-missing-dimension (CSS aspect-ratio via Ui.AspectRatio). child0 = width-constrained
// (Width 320, 16:9 -> derives H 180); child1 = height-constrained (Height 90, 16:9 -> derives W 160). AlignSelf.Start
// so the column's cross-stretch never overrides the derived cross size.
sealed class AspectProbe : Component
{
    public override Element Render() => new BoxEl
    {
        Direction = 1,
        Children =
        [
            new BoxEl { Width = 320f, AspectRatio = 16f / 9f, AlignSelf = FlexAlign.Start },
            new BoxEl { Height = 90f, AspectRatio = 16f / 9f, AlignSelf = FlexAlign.Start },
        ],
    };
}

// G3: Ui.Spacer / Spacer(px) / Wrap / Center primitives. Row0: a growing Spacer pushes the trailing box to the far edge
// (300 - 40 - 60 = 200 slack eaten -> box2.X == 240). Row1: a fixed Spacer(24) holds a rigid gap (box2.X == 64). Wrap:
// three 120-wide boxes + gap 10 in a 260-wide panel -> the third wraps to a new line. Center: 40x40 centered in 200x200.
sealed class PrimitivesProbe : Component
{
    static BoxEl Box(float w, float h) => new() { Width = w, Height = h };
    public override Element Render() => new BoxEl
    {
        Direction = 1, Gap = 8f, AlignItems = FlexAlign.Start,   // never stretch children's width past their explicit size
        Children =
        [
            new BoxEl { Direction = 0, Width = 300f, Children = [ Box(40f, 20f), Ui.Spacer(), Box(60f, 20f) ] },
            new BoxEl { Direction = 0, Width = 300f, Children = [ Box(40f, 20f), Ui.Spacer(24f), Box(60f, 20f) ] },
            Ui.Wrap(10f, Box(120f, 30f), Box(120f, 30f), Box(120f, 30f)) with { Width = 260f },
            new BoxEl { Width = 200f, Height = 200f, Children = [ Ui.Center(Box(40f, 40f)) ] },
        ],
    };
}

// A grid whose FIXED columns (100+100+60 = 260) exceed its definite width (120) with a Star track present — the
// overflow case that used to spill cells past the edge + overlap. The engine must shrink the fixed tracks to fit.
sealed class GridOverflowProbe : Component
{
    static Element Cell() => new BoxEl { Fill = ColorF.FromRgba(40, 40, 40) };
    public override Element Render()
        => Ui.Grid([TrackSize.Px(100), TrackSize.Px(100), TrackSize.Star(), TrackSize.Px(60)], 0f, 0f, 40f,
                   Cell(), Cell(), Cell(), Cell()) with { Width = 120f, Height = 80f };
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

// Repro of the Wavee detail-page table: a column header grid (chrome Padding=PadX, grid no-padding) above a "row"
// (RowSkin: ZStack + Margin=RowInset, a Direction=0 lane with Grow, the grid Grow=1 + Padding=PadX-RowInset). Both grids
// share the SAME track list (the alignment invariant). At EQUAL container width the columns must land at identical X —
// this isolates a grid-structure bug from an ItemsView width mismatch.
sealed class ColumnAlignProbe : Component
{
    const float PadX = 16f, RowInset = 8f, ColGap = 12f;
    static TrackSize[] Tracks() => [TrackSize.Px(36f), TrackSize.Px(36f), TrackSize.Star(), TrackSize.Px(180f), TrackSize.Px(108f), TrackSize.Px(40f), TrackSize.Px(64f)];
    static Element Cell() => new BoxEl { Fill = ColorF.FromRgba(40, 40, 40) };
    static Element[] Cells() { var a = new Element[7]; for (int i = 0; i < 7; i++) a[i] = Cell(); return a; }
    static Element RowGrid() => new GridEl
    {
        Columns = Tracks(), ColGap = ColGap, RowHeight = 48f, Grow = 1f,
        Padding = new Edges4(PadX - RowInset, 0f, PadX - RowInset, 0f), Children = Cells(),
    };
    static BoxEl RowSkinLike(Element content) => new BoxEl
    {
        ZStack = true, MinHeight = 48f, Margin = new Edges4(RowInset, 0f, RowInset, 0f),
        Children = [ new BoxEl { Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center, Children = [content] } ],
    };
    public override Element Render() => new BoxEl
    {
        Direction = 1, Width = 900f, Height = 400f,
        Children =
        [
            new BoxEl   // chrome (fixed header, outside the scroller) — matches DetailTracks.Render
            {
                Direction = 1, Padding = new Edges4(PadX, 8f, PadX, 0f),
                Children = [ new BoxEl { Direction = 1, Children = [
                    new GridEl { Columns = Tracks(), ColGap = ColGap, RowHeight = 36f, Children = Cells() },
                    new BoxEl { Height = 1f } ] } ],
            },
            // The rows go through a real ItemsView (40 items → overflow → scrollbar), wrapped by a RowSkin-like
            // container — the actual app path. This is what the bare-grid probe omitted.
            ItemsView.Create(40, i => RowGrid(), RepeatLayout.Stack(48f),
                new ListOptions { ContainerFactory = (i, content, st, oi, of) => RowSkinLike(content), Grow = 1f }),
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
        var seek = UseFloatSignal(0.3f);
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
                PlayerBar(playing, setPlaying, seek),
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

    Element PlayerBar(bool playing, Action<bool> setPlaying, FloatSignal seek) => new BoxEl
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
            Slider.Create(seek, length: 220f),
            ToggleButton.Create("Shuffle"),
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
        return Slider.Create(sig, onChange: null, length: 200f, thickness: 24f);
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
                new IndexForEl(() => count.Value, i => new BoxEl { Width = 40, Height = 12, Children = [Text("row" + i)] }, i => "r" + i),
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
                Flow.For<string>(() => items.Value, s => s,
                    (s, i) => new BoxEl { Width = 40, Height = 12, Children = [Text(s)] }),
            ],
        };
    }
}

// A Show boundary whose branch element is built from PARENT render state (the label is read in Render, so a change
// re-renders the parent). The parent re-render must refresh the branch IN PLACE — new text, same scene node — instead
// of freezing the child captured at first mount (the shy-header frozen-accent bug).
sealed class FlowShowRefreshProbe : Component
{
    public static int Renders;
    public Signal<bool>? Show;
    public Signal<string>? Label;
    public override Element Render()
    {
        Renders++;
        var show = UseSignal(true);
        var label = UseSignal("alpha");
        Show = show; Label = label;
        string text = label.Value;   // read in the PARENT: a change re-renders the parent and rebuilds the Show child
        return new BoxEl
        {
            Direction = 1,
            Children =
            [
                Flow.Show(() => show.Value, new BoxEl { Width = 40, Height = 12, Children = [Text(text)] }),
            ],
        };
    }
}

// ── Flow.For<T> probes (gate.for.*) — typed, mandatory-key reactive lists ──────────────────────────────────────────
// A keyed list over a collection signal (signal-direct overload). Row identity must survive insert/remove/reorder by key.
sealed class ForKeyedDiffProbe : Component
{
    public readonly Signal<IReadOnlyList<string>> Items = new(new[] { "a", "b", "c" });
    public override Element Render() => new BoxEl
    {
        Direction = 1,
        Children = [Flow.For<string>(Items, s => s, (s, i) => new BoxEl { Width = 30, Height = 10, Children = [Text(s)] })],
    };
}

// Instrumented items thunk: the boundary run must read the source EXACTLY ONCE per structural change (not N+1 like the
// old Count()+ItemAt(i) shape that re-read the signal per row).
sealed class ForSnapshotProbe : Component
{
    public int ItemsReads;
    public readonly Signal<IReadOnlyList<int>> Items = new(new[] { 1, 2, 3, 4, 5 });
    public override Element Render() => new BoxEl
    {
        Children = [Flow.For<int>(() => { ItemsReads++; return Items.Value; }, n => "k" + n, (n, i) => new BoxEl { Width = 8, Height = 8 })],
    };
}

// A For whose Row closure captures PARENT render state (the prefix). A parent re-render must re-point the closures
// (UpdateFor — the Show-parity fix) so rows reflect the NEW state in place; pre-fix (ForEl.Update no-op) they froze.
sealed class ForUpdateProbe : Component
{
    public static int Renders;
    public Signal<string>? Prefix;
    public readonly Signal<IReadOnlyList<int>> Items = new(new[] { 1, 2, 3 });
    public override Element Render()
    {
        Renders++;
        var prefix = UseSignal("A");
        Prefix = prefix;
        string p = prefix.Value;   // read in the PARENT → a change re-renders the parent and rebuilds the For with a fresh Row closure
        return new BoxEl
        {
            Direction = 1,
            Children = [Flow.For<int>(Items, n => "k" + n, (n, i) => new BoxEl { Width = 40, Height = 12, Children = [Text(p + n)] })],
        };
    }
}

// Two rows collapsing to the SAME key — the DEBUG duplicate-key tripwire must throw inside Fill (structural change).
sealed class ForDupKeyProbe : Component
{
    public override Element Render() => new BoxEl
    {
        Children = [Flow.For<int>(() => new[] { 1, 2 }, n => "dup", (n, i) => new BoxEl { Width = 8, Height = 8 })],
    };
}

// A settled For over a stable list — a quiet frame must add 0 bytes to the hot phase (the effect runs only on change).
sealed class ForAllocProbe : Component
{
    public readonly Signal<IReadOnlyList<int>> Items = new(new[] { 1, 2, 3, 4 });
    public override Element Render() => new BoxEl
    {
        Width = 200, Height = 200, Direction = 1,
        Children = [Flow.For<int>(Items, n => "k" + n, (n, i) => new BoxEl { Width = 40, Height = 12 })],
    };
}

// ── UseResource probe (gate.resource.*) — one probe drives every SWR gate ───────────────────────────────────────────
// The deps signal (Key) re-keys the resource; the loader parks a controllable TaskCompletionSource per load so the gate
// completes them in any order (epoch-ordering) or with an exception (refresh-failure). ObserveCancellation=false makes a
// superseded load's completion still arrive (so the EPOCH guard — not the token — is what drops it).
sealed class ResourceProbe : Component
{
    public readonly Signal<int> Key = new(0);
    public ResourceOptions Options = ResourceOptions.Default;
    public bool ObserveCancellation = true;
    public readonly List<TaskCompletionSource<string>> Gates = new();
    public readonly List<int> StartedKeys = new();
    public Resource<string> Res;

    public override Element Render()
    {
        int k = Key.Value;   // subscribe → a deps change re-renders and reloads the resource on the new key
        Res = UseResource(async ct =>
        {
            var tcs = new TaskCompletionSource<string>();
            lock (Gates) { Gates.Add(tcs); StartedKeys.Add(k); }
            System.Threading.CancellationTokenRegistration reg = default;
            if (ObserveCancellation) reg = ct.Register(() => tcs.TrySetCanceled());
            try { return await tcs.Task.ConfigureAwait(false); }
            finally { reg.Dispose(); }
        }, seed: "", deps: k, options: Options);
        return new BoxEl();
    }
}

// ── Basic-input infrastructure probes (overlay / text input / repeat) ─────────────
// ── §WS3 P8 registry-driven router probes (gate.nav.*) ──────────────────────────────────────────────────────────
// A [Route]-tagged page the RouteTableGenerator picks up (metadata + a parameterless-ctor factory).
[Route("vs.route-gen.plain", Title = "Plain Page", Icon = "P", Category = "RouteGen", Order = 7, KeepAlive = true)]
sealed class RouteGenPlainPage : Component
{
    public override Element Render() => new BoxEl { Children = [Text("VSGEN-PLAIN")] };
}

// A [Route]-tagged page with a (string) ctor — the generated factory threads route.Arg into it.
[Route("vs.route-gen.arg")]
sealed class RouteGenArgPage : Component
{
    readonly string _arg;
    public RouteGenArgPage(string arg) { _arg = arg; }
    public override Element Render() => new BoxEl { Children = [Text("VSGEN-ARG:" + _arg)] };
}

// A router page with a clickable counter — the PageHost.Create gate checks resolve/fallback/keepalive-restore.
sealed class RouterProbePage : Component
{
    readonly string _label;
    public RouterProbePage(string label) { _label = label; }
    public override Element Render()
    {
        var (count, setCount) = UseState(0);
        return new BoxEl
        {
            Direction = 1, Width = 200, Height = 120,
            Children =
            [
                new BoxEl
                {
                    Width = 140, Height = 32, Role = AutomationRole.Button,
                    Fill = new ColorF(0.2f, 0.2f, 0.2f, 1f),
                    OnClick = () => setCount(count + 1),
                    Children = [Text(_label + ":" + count)],
                },
            ],
        };
    }
}

sealed class KeepAliveProbe : Component
{
    public Signal<string>? Route;
    public int MaxEntries = 2;
    public readonly Dictionary<string, int> PageRenders = new();

    public override Element Render()
    {
        var route = UseSignal("a");
        Route = route;
        return Flow.KeepAlive(
            () => route.Value,
            key => key,
            key => Embed.Comp(() => new KeepAlivePage(key, this)),
            new KeepAliveOptions(MaxEntries));
    }
}

sealed class KeepAlivePage : Component
{
    readonly string _key;
    readonly KeepAliveProbe _owner;
    public KeepAlivePage(string key, KeepAliveProbe owner) { _key = key; _owner = owner; }

    public override Element Render()
    {
        _owner.PageRenders.TryGetValue(_key, out int renders);
        _owner.PageRenders[_key] = renders + 1;

        var (count, setCount) = UseState(0);
        Element[] rows = new Element[18];
        for (int i = 0; i < rows.Length; i++)
            rows[i] = new BoxEl { Height = 24, Children = [Text(_key + "-row-" + i)] };

        return new BoxEl
        {
            Direction = 1, Gap = 4, Width = 220, Height = 170,
            Children =
            [
                new BoxEl
                {
                    Width = 120, Height = 30, Role = AutomationRole.Button,
                    Fill = new ColorF(0.2f, 0.2f, 0.2f, 1f),
                    OnClick = () => setCount(count + 1),
                    Children = [Text(_key + ":" + count)],
                },
                new ScrollEl
                {
                    Width = 180, Height = 64,
                    Content = new BoxEl { Direction = 1, Children = rows },
                },
                Image("keepalive-" + _key, 24, 24, 2f, ColorF.FromRgba(0x33, 0x33, 0x33)),
            ],
        };
    }
}

// Reproduces a page-local animated overlay whose presence follows UseIsActive. Parking flips the signal after the page
// is detached; removing the animated child must not create a globally drawn exit orphan above the destination page.
sealed class KeepAlivePresenceProbe : Component
{
    public Signal<string>? Route;

    public override Element Render()
    {
        var route = UseSignal("a");
        Route = route;
        return Flow.KeepAlive(
            () => route.Value,
            key => key,
            key => Embed.Comp(() => new KeepAlivePresencePage(key)),
            new KeepAliveOptions(MaxEntries: 2));
    }
}

sealed class KeepAlivePresencePage : Component
{
    readonly string _key;
    public KeepAlivePresencePage(string key) { _key = key; }

    static readonly LayoutTransition Exit = new(
        TransitionChannels.Opacity,
        TransitionDynamics.Tween(400f, Easing.SmoothOut),
        Exit: new EnterExit(Opacity: 0f, Active: true));

    public override Element Render()
    {
        var active = UseIsActive();
        return Flow.Show(
            () => active.Value,
            new BoxEl
            {
                Width = 120f, Height = 32f, Animate = Exit,
                Children = [Text("presence-" + _key)],
            });
    }
}

// Transparent-boundary input probe: the outer component does not subscribe to Live; only the nested component swaps
// from a non-hit-testable placeholder to a real scroller. A transparent ancestor must remain traversable without
// relying on the inner HitTestVisible bit being synchronously copied back through every component boundary.
sealed class NestedHitVisibilityProbe : Component
{
    public readonly Signal<bool> Live = new(false);
    public override Element Render() => Embed.Comp(() => new NestedHitVisibilityInner(Live));
}

sealed class NestedHitVisibilityInner : Component
{
    readonly Signal<bool> _live;
    public NestedHitVisibilityInner(Signal<bool> live) { _live = live; }

    public override Element Render()
    {
        if (!_live.Value)
            return new BoxEl { Width = 220f, Height = 170f, HitTestVisible = false };

        var rows = new Element[16];
        for (int i = 0; i < rows.Length; i++) rows[i] = new BoxEl { Height = 28f, Children = [Text("nested-row-" + i)] };
        return new ScrollEl
        {
            Width = 220f, Height = 170f,
            Content = new BoxEl { Direction = 1, Children = rows },
        };
    }
}

// Activation-lifecycle probe (UseIsActive / UseActivation + Layer-D auto-quiesce): a KeepAlive boundary whose pages
// record their activation transitions and (except the "blank" page) seed a LOOPING animation, so a test can assert
// (a) UseActivation fires exactly once per park/minimize edge and never at mount, and (b) parking a page quiesces its
// looping animation so AnimEngine.HasActive (the Anim wake reason) drops.
sealed class ActivationProbe : Component
{
    public Signal<string>? Route;
    public int MaxEntries = 4;
    public readonly Dictionary<string, int> On = new();
    public readonly Dictionary<string, int> Off = new();

    public override Element Render()
    {
        var route = UseSignal("a");
        Route = route;
        return Flow.KeepAlive(
            () => route.Value,
            key => key,
            key => Embed.Comp(() => new ActivationPage(key, this)),
            new KeepAliveOptions(MaxEntries));
    }
}

sealed class ActivationPage : Component
{
    readonly string _key;
    readonly ActivationProbe _owner;
    public ActivationPage(string key, ActivationProbe owner) { _key = key; _owner = owner; }

    public override Element Render()
    {
        UseActivation(
            onActivated:   () => { _owner.On.TryGetValue(_key, out int n);  _owner.On[_key] = n + 1; },
            onDeactivated: () => { _owner.Off.TryGetValue(_key, out int n); _owner.Off[_key] = n + 1; });
        // A persistent looping animation on every page EXCEPT "blank" — _key is instance-constant, so this conditional
        // hook keeps a stable call order for any given page. Quiesced when the page parks (Layer D).
        if (_key != "blank")
            UseKeyframes(AnimChannel.Opacity, [new Keyframe(0f, 0.4f), new Keyframe(1f, 1f)], 600f, loop: true, DepKey.Empty);
        return new BoxEl { Width = 100, Height = 40, Children = [Text("ap-" + _key)] };
    }
}

// Probe for TextEl auto-fit (check 51): four wrapping boxes (each sizes to its text's height) — a long + a short title,
// once WITH a MinSize floor (auto-fit) and once WITHOUT (authored size, capped+trimmed). The check reads box heights.
sealed class AutoFitProbe : Component
{
    public NodeHandle LongFit, LongFixed, ShortFit, ShortFixed;
    const string Long = "mellow pop wistful sunset late night drive";
    const string Short = "Hits";
    const float W = 180f;

    static Element Cell(string txt, float min, Action<NodeHandle> cap) => new BoxEl
    {
        OnRealized = cap,
        Children =
        [
            new TextEl(txt)
            {
                Size = 40f, MinSize = min, Width = W, Weight = 700,
                Wrap = TextWrap.Wrap, MaxLines = 3, Trim = TextTrim.CharacterEllipsis,
            },
        ],
    };

    public override Element Render() => new BoxEl
    {
        Direction = 1, Gap = 8f,
        Children =
        [
            Cell(Long,  16f,        h => LongFit = h),
            Cell(Long,  float.NaN,  h => LongFixed = h),
            Cell(Short, 16f,        h => ShortFit = h),
            Cell(Short, float.NaN,  h => ShortFixed = h),
        ],
    };
}

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
    public readonly List<double> Changes = new();
    public override Element Render()
    {
        var v = UseSignal(Initial); Val = v;
        var t = UseSignal(""); Txt = t;
        return Embed.Comp(() => new OverlayHost
        {
            Child = NumberBox.Create(value: v, onChange: n => Changes.Add(n),
                options: new NumberBox.NumberBoxOptions { Minimum = 0, Maximum = 10, SmallChange = 1, LargeChange = 5, SpinButtonPlacementMode = Mode, Text = t }),
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
    public int BackgroundClicks;
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
            Width = 200, Height = 120, Padding = Edges4.All(20), TabStop = false,
            OnClick = () => _p.BackgroundClicks++,
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

// ── G5g — MediaPlayerElement host probe: mounts the player under a REAL OverlayHost so its chrome can consult the
// overlay service (PinsAnchor auto-hide gate). Exposes the service for the gate to open a pinning picker. ───────────
sealed class MediaPlayerHostProbe : Component
{
    public IOverlayService? Service;
    public required IMediaPlayer Player;
    public float HideMs = 200f;
    public override Element Render()
        => Embed.Comp(() => new OverlayHost { Child = Embed.Comp(() => new MediaPlayerHostInner(this)) });
}

sealed class MediaPlayerHostInner : Component
{
    readonly MediaPlayerHostProbe _p;
    public MediaPlayerHostInner(MediaPlayerHostProbe p) => _p = p;
    public override Element Render()
    {
        _p.Service = UseContext(Overlay.Service);
        return new BoxEl
        {
            Width = 480, Height = 300,
            Children = [Embed.Comp(() => new FluentGpu.Controls.Media.MediaPlayerElement
            {
                Player = _p.Player, TransportControlsHideDelayMs = _p.HideMs, AutoHideTransportControls = true,
            })],
        };
    }
}

// ── G5f — controlled Popup + Flyout.Attach probes ────────────────────────────────────────────────────────────────
sealed class PopupCtlProbe : Component
{
    public IOverlayService? Service;
    public readonly Signal<bool> Open = new(false);
    public int OpenChanges;
    public bool LastChanged;
    public bool AnchorLow;   // dock the anchor at the bottom edge → the popup flips ABOVE (positioner flip test)
    public NodeHandle Anchor;
    public override Element Render() => Embed.Comp(() => new OverlayHost { Child = Embed.Comp(() => new PopupCtlInner(this)) });
}

sealed class PopupCtlInner : Component
{
    readonly PopupCtlProbe _p;
    public PopupCtlInner(PopupCtlProbe p) => _p = p;
    public override Element Render()
    {
        _p.Service = UseContext(Overlay.Service);
        var anchor = new BoxEl
        {
            Width = 120, Height = 32, Role = AutomationRole.Button, OnClick = () => { },
            OnRealized = h => _p.Anchor = h, Children = [Ui.Text("anchor")],
        };
        var pop = Popup.Create(
            anchor,
            () => new BoxEl { Width = 120, Height = 80, Fill = Tok.FillCardDefault, Children = [Ui.Text("popup-body")] },
            _p.Open,
            b => { _p.OpenChanges++; _p.LastChanged = b; },
            FlyoutPlacement.BottomLeft);
        return new BoxEl
        {
            Width = 240, Grow = 1, Direction = 1,   // fill the window height so AnchorLow docks at the true bottom edge (flip test)
            Justify = _p.AnchorLow ? FlexJustify.End : FlexJustify.Start,
            Padding = Edges4.All(8),
            Children = [pop],
        };
    }
}

sealed class FlyoutAttachProbe : Component
{
    public IOverlayService? Service;
    public int AnchorClicks;
    public NodeHandle Anchor;
    public override Element Render() => Embed.Comp(() => new OverlayHost { Child = Embed.Comp(() => new FlyoutAttachInner(this)) });
}

sealed class FlyoutAttachInner : Component
{
    readonly FlyoutAttachProbe _p;
    public FlyoutAttachInner(FlyoutAttachProbe p) => _p = p;
    public override Element Render()
    {
        var svc = UseContext(Overlay.Service);
        _p.Service = svc;
        // An anchor with its OWN OnClick; Flyout.Attach must chain (keep both) it.
        var anchor = new BoxEl
        {
            Width = 120, Height = 32, Role = AutomationRole.Button,
            OnClick = () => _p.AnchorClicks++,
            OnRealized = h => _p.Anchor = h,
            Children = [Ui.Text("trigger")],
        };
        var attached = Flyout.Attach(anchor, svc,
            () => new BoxEl { Width = 120, Height = 60, Fill = Tok.FillCardDefault, Children = [Ui.Text("flyout")] });
        return new BoxEl { Width = 240, Height = 160, Padding = Edges4.All(8), Children = [attached] };
    }
}

// Windowed-popup exit-routing probe: swapping the keyed child keeps the outgoing text alive for one frame. Its orphan
// must be recorded by the popup subtree at the former parent (not by the main window's global fallback pass).
sealed class PopupExitProbeBody : Component
{
    static readonly LayoutTransition SwapTransition = new(
        TransitionChannels.Opacity,
        TransitionDynamics.Tween(300f, Easing.Linear),
        Exit: new EnterExit(Opacity: 0f, Active: true));

    public readonly Signal<bool> Swap = new(false);

    public override Element Render()
    {
        bool next = Swap.Value;
        return new BoxEl
        {
            Width = 180, Height = 80, ClipToBounds = true,
            Children =
            [
                new BoxEl
                {
                    Key = next ? "popup-exit-new" : "popup-exit-old",
                    Animate = SwapTransition,
                    Children = [new TextEl(next ? "popup-exit-new" : "popup-exit-old") { Size = 12f }],
                },
            ],
        };
    }
}

// Touch light-dismiss probe (Phase 2): an OverlayHost over a full-bleed clickable surface (the "behind" content) plus
// a small anchor. A flyout opened over the anchor drops the light-dismiss scrim on TOP of the behind-surface; a touch
// tap "outside" the popup lands on the scrim — so it must DISMISS and the behind-surface must NEVER see the tap
// (the scrim consumes it exactly like a mouse click; WinUI CPopupRoot::OnPointerPressed sets Handled=didClose,
// popup.cpp:5206). BehindClicks counts any tap that leaked through the scrim to the content beneath.
sealed class TouchDismissProbe : Component
{
    public IOverlayService? Service;
    public NodeHandle Anchor;
    public int BehindClicks;
    public override Element Render() => Embed.Comp(() => new OverlayHost { Child = Embed.Comp(() => new TouchDismissInner(this)) });
}

sealed class TouchDismissInner : Component
{
    readonly TouchDismissProbe _p;
    public TouchDismissInner(TouchDismissProbe p) => _p = p;
    public override Element Render()
    {
        _p.Service = UseContext(Overlay.Service);
        return new BoxEl
        {
            Width = 480, Height = 360, ZStack = true,
            Children =
            [
                new BoxEl { Grow = 1, Width = 480, Height = 360, OnClick = () => _p.BehindClicks++ },   // full-bleed behind content
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

// e4popup.7b — the tooltip-over-a-scroller probe: the wrapped target sits in a tall vertical ScrollView, so the
// gate can prove an OPEN bubble never eats wheel input (neither over the bubble nor anywhere else in the window).
sealed class E4ToolTipWheelProbe : Component
{
    public override Element Render() => Embed.Comp(() => new OverlayHost
    {
        Child = ScrollView(new BoxEl
        {
            Direction = 1,
            Children =
            [
                new BoxEl { Height = 100f },
                ToolTip.Wrap(new BoxEl { Width = 120, Height = 32, Fill = ColorF.FromRgba(40, 40, 40) }, "tip-wheel"),
                new BoxEl { Height = 1200f },
            ],
        }) with { Grow = 1f },
    });
}

sealed class CheckBoxProbe : Component
{
    public CheckState State;
    public override Element Render()
    {
        var st = UseSignal(CheckState.Unchecked);
        State = st.Value;
        return CheckBox.Create("opt", st);
    }
}

sealed class RadioProbe : Component
{
    public int Selected = -1;
    public override Element Render()
    {
        var sel = UseSignal(-1);
        Selected = sel.Value;
        return RadioButton.Group(new[] { "A", "B", "C" }, sel);
    }
}

sealed class ToggleSwitchProbe : Component
{
    public bool On;
    public override Element Render()
    {
        var on = UseSignal(false);
        On = on.Value;
        return ToggleSwitch.Create(on);
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
        var v = UseFloatSignal(0f);
        Val = v.Peek();
        return Slider.Create(v, x => Val = x, new Slider.SliderOptions { Min = 0f, Max = 100f, Step = 10f, TickFrequency = 20f }, length: 200f, thickness: 32f);
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
            Children = [ToggleButton.Create("w1-tb", on)],
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
        var sel = UseSignal(0);
        Selected = sel.Value;
        return new BoxEl
        {
            Padding = Edges4.All(10),
            Children =
            [
                RadioButtons.Create(new[] { "A", "B", "C", "D", "E" }, sel,
                    onChange: i => { SelectCalls++; }, header: "w1-group", maxColumns: 2),
            ],
        };
    }
}

// Slider.Create over 0..200 with a header — exercises the AUTO step sizes (SmallChange 0 → range/100 = 2,
// LargeChange 0 → range/10 = 20; WinUI's absolute defaults 1/10 on its 0–100 range, Slider_Partial.h:13-15).
sealed class W1SliderKeysProbe : Component
{
    public float Val;
    public override Element Render()
    {
        var v = UseFloatSignal(0f);
        Val = v.Peek();
        return new BoxEl
        {
            Padding = Edges4.All(20),
            Children = [Slider.Create(v, x => Val = x, new Slider.SliderOptions { Min = 0f, Max = 200f, Header = "w1-vol" }, length: 200f, thickness: 32f)],
        };
    }
}

// Slider.Create inside an OverlayHost (the thumb value tooltip needs a real overlay service) + inline ticks; a leading
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
                Slider.Create(new FloatSignal(0f), v => Val = v, new Slider.SliderOptions { Min = 0f, Max = 200f, TickFrequency = 50f }, length: 200f, thickness: 32f),
            ],
        },
    });
}

// FG_PROBE=ranged-tooltip: the W1 probe shape with a switchable IsThumbToolTipEnabled (the triangulation lever).
// The thumb follows the scrub via the compositor bind regardless of onChange — one code path (the unified Slider.Create).
sealed class RangedTooltipProbeRoot : Component
{
    public bool TooltipEnabled = true;
    public override Element Render() => Embed.Comp(() => new OverlayHost
    {
        Child = Embed.Comp(() => new RangedTooltipProbeBody { TooltipEnabled = TooltipEnabled }),
    });
}

sealed class RangedTooltipProbeBody : Component
{
    public bool TooltipEnabled = true;

    public override Element Render()
    {
        // Signal-bound: the thumb follows the scrub via the compositor bind regardless of onChange (one code path).
        var value = UseFloatSignal(0f);
        return new BoxEl
        {
            Direction = 1, Gap = 12, Padding = Edges4.All(20),
            Children =
            [
                new BoxEl { Width = 40, Height = 20, OnClick = () => { } },
                Slider.Create(value, NoOp, new Slider.SliderOptions { Min = 0f, Max = 200f, IsThumbToolTipEnabled = TooltipEnabled }, length: 200f, thickness: 32f),
            ],
        };
    }

    static void NoOp(float _) { }
}

// WS3 P3 — the unified Slider.Create under an OverlayHost (so the signal-bound tooltip has a real overlay service).
// Caller == null exercises value auto-materialization; onChange records the reported value for the gates.
sealed class SliderUnifiedProbe : Component
{
    public float Val = float.NaN;
    public FloatSignal? Caller;                 // null ⇒ the control materializes its own signal
    public Slider.SliderOptions? Opts;          // null ⇒ 0..1, tooltip enabled
    public override Element Render() => Embed.Comp(() => new OverlayHost
    {
        Child = new BoxEl
        {
            Padding = Edges4.All(20),
            Children = [Slider.Create(Caller, x => Val = x, Opts, length: 200f, thickness: 32f)],
        },
    });
}

/// <summary>FG_PROBE=titlebar-resize root — the gallery's titlebar wiring (pane toggle + icon + title + the
/// signal-width AutoSuggestBox + engine caption buttons) over a filler page, for the resize-down regression probe.</summary>
sealed class TitleBarResizeProbeRoot : Component
{
    public override Element Render() => new BoxEl
    {
        Direction = 1, Grow = 1,
        Children =
        [
            Embed.Comp(() =>
            {
                var tb = new TitleBar
                {
                    Title = "FluentGpu Gallery",
                    IconGlyph = Icons.Grid,
                    ShowPaneToggle = true,
                    ShowCaptionButtons = true,
                };
                // The gallery wiring shape: the avail argument picks collapse-vs-box; the LIVE width flows as the
                // measured-slot signal (a mounted component's plain width field freezes — see TitleBar.ContentAvail).
                tb.Content = avail => avail < 140f
                    ? new BoxEl()
                    : AutoSuggestBox.Create(
                        suggestions: ["Slider", "Button", "CheckBox"],
                        placeholder: "Search controls and samples...",
                        width: 580f,
                        widthSignal: tb.ContentAvail);
                return tb;
            }),
            new BoxEl { Grow = 1 },
        ],
    };
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
                PipsPager.Create(5, selected),
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
        var layout = UseMemo(static () => new MeasuredStackVirtualLayout(Estimate), DepKey.Empty);
        Layout = layout;
        return Virtual.Measured(N, layout,
                   renderItem: i => new BoxEl { Height = H(i), Fill = ColorF.FromRgba(30, 30, 30) },
                   keyOf: i => "m" + i)
               with { Width = 300, Height = 300 };
    }
}

// gate.scroll.anchor-repin-under-gesture probe: a MEASURED virtual list whose rows deliberately measure LARGER (64px)
// than their 40px estimate, so budgeted realize corrects extents mid-scroll and the virtualization anchor re-pin
// (FlexLayout.RecordAnchorShift) fires with non-zero deltas DURING a touchpad contact gesture. Viewport = Scene.Root.
sealed class AnchorRepinProbe : Component
{
    public const int N = 800;
    public const float Estimate = 40f;
    public const float Real = 64f;   // every row measures LARGER than the estimate (+24) → corrections extend content above the moving anchor
    public MeasuredStackVirtualLayout? Layout;
    public override Element Render()
    {
        var layout = UseMemo(static () => new MeasuredStackVirtualLayout(Estimate), DepKey.Empty);
        Layout = layout;
        return Virtual.Measured(N, layout,
                   renderItem: _ => new BoxEl { Height = Real, Fill = ColorF.FromRgba(30, 30, 30) },
                   keyOf: i => "ar" + i)
               with { Width = 300, Height = 300 };
    }
}

// gate.scroll.wheel-chase-extent-shrink probe: a fixed-height virtual list whose row COUNT is signal-driven, so the test
// can shrink the published content extent (and thus maxOff) far below an in-flight WheelAnimating chase target mid-chase.
// Fixed-geometry (Virtual.List) publishes ContentH = N·RowH deterministically — no estimate/correct drift. Viewport = Scene.Root.
sealed class ShrinkChaseProbe : Component
{
    public readonly FluentGpu.Signals.Signal<int> Count = new(400);
    public const float RowH = 60f;
    public override Element Render()
    {
        int n = Count.Value;   // subscribe → re-render (and republish the shrunk content extent) when Count changes
        return Virtual.List(n, RowH, _ => new BoxEl { Height = RowH, Fill = ColorF.FromRgba(30, 30, 30) }, keyOf: i => "sc" + i)
               with { Width = 300, Height = 300 };
    }
}

// e11virt.elevate-cell — the PagedShelf cell shape: a row of NON-interactive flagged cells, each wrapping an
// interactive card. The card wins the hit; the cell must still receive HoverWithin (the HoverElevatePaintBit joined
// UpdateHoverWithin's ancestor mask) so the recorder can defer the whole cell above its sibling cells.
sealed class ElevateCellProbe : Component
{
    public override Element Render() => new BoxEl
    {
        Direction = 0,
        Children = [ Cell(ColorF.FromRgba(220, 40, 40)), Cell(ColorF.FromRgba(40, 90, 220)) ],
    };
    // HoverFill = Fill (identity) so the gate's exact-color probe still matches the hovered card's drawn command.
    internal static Element Cell(ColorF fill) => new BoxEl
    {
        Direction = 1, Width = 100f, HoverElevatePaint = true,
        Children = [ new BoxEl { Width = 100f, Height = 100f, Fill = fill, HoverFill = fill, OnClick = static () => { } } ],
    };
}

// e11virt.elevate-escape — the full shelf shape WITH a HoverElevateClipRoot: a clipping viewport wraps the cell row;
// the hovered cell must HOIST out of the viewport's clip (recorded after its PopClip, against the outer clip) while
// the resting sibling stays inside it.
sealed class ElevateEscapeProbe : Component
{
    public override Element Render() => new BoxEl
    {
        Width = 150f, Height = 100f, ClipToBounds = true, HoverElevateClipRoot = true,
        Children =
        [
            new BoxEl
            {
                Direction = 0,
                Children = [ ElevateCellProbe.Cell(ColorF.FromRgba(220, 40, 40)), ElevateCellProbe.Cell(ColorF.FromRgba(40, 90, 220)) ],
            },
        ],
    };
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

// Reconcile-window in-place diff (the Home "like-flash" class of bug): a parent re-render rebuilds the VirtualListEl
// and re-runs RenderItem for every realized slot (Update → RealizeWindow, reuseOverlap:false). Same-slot rows with
// matching type+key must UPDATE IN PLACE so a component hosted inside a row keeps its instance/state instead of
// taking the mount+remove path (fresh instance, state reset, first-frame self-measure).
sealed class WindowInPlaceDiffProbe : Component
{
    public const int N = 50;
    public readonly Signal<int> Rev = new(0);
    public int ParentRenders;
    public int RowConstructions;                                   // component MOUNTS (each mount runs the factory once)
    public readonly Dictionary<int, WindowInPlaceRowComp> Rows = new();
    public override Element Render()
    {
        ParentRenders++;
        _ = Rev.Value;   // SUBSCRIBE: bumping Rev re-renders this parent → a NEW VirtualListEl for the same window
        return Virtual.List(N, 40f,
                   renderItem: i => new BoxEl
                   {
                       Height = 40f,
                       Children = [Embed.Comp(() => new WindowInPlaceRowComp(this, i))],
                   },
                   keyOf: i => "ip" + i)
               with { Width = 300, Height = 200 };
    }
}

sealed class WindowInPlaceRowComp : Component
{
    readonly WindowInPlaceDiffProbe _p;
    public readonly Signal<int> Local = new(0);   // per-instance state that must survive the parent re-render
    public WindowInPlaceRowComp(WindowInPlaceDiffProbe p, int index)
    {
        _p = p;
        p.RowConstructions++;    // safe mount proxy: ChecksReuse is false, so the ReuseGuard never builds a throwaway
        p.Rows[index] = this;
    }
    public override Element Render()
    {
        _ = Local.Value;         // subscribe — a state write re-renders THIS row only (granular)
        return new BoxEl { Height = 40f, Fill = ColorF.FromRgba(24, 24, 24) };
    }
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
                    options: new ListOptions
                    {
                        IsItemInvokedEnabled = true,
                        OnInvoked = i => { InvokedCount++; LastInvoked = i; },
                        ItemText = NameOf,
                        Controller = Controller,
                    }),
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
            Children = [ItemsView.Create(N, i => new BoxEl(), RepeatLayout.Grid(4, 72f, 8f), new ListOptions { Controller = Controller })],
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
                    new ListOptions
                    {
                        SelectionMode = ItemsSelectionMode.Extended,
                        OnChange = () => SelectionChangedCount++,
                        Controller = Controller,
                    }),
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
                    new ListOptions { SelectionMode = ItemsSelectionMode.Multiple, Controller = Controller }),
            ],
        };
}

// Frozen-shell resize probe (check S3): mirrors WaveeShell's frozen frame. A Component that reads NO signal renders ONCE
// and never re-renders; it returns Embed.Comp(() => new OverlayHost { Child = the shell column }). OverlayHost pins its
// root ZStack to Viewport.Size (OverlayHost.cs:885 — Width=vp.Width, Height=vp.Height). On a window resize OverlayHost
// re-renders (it reads Viewport.Size) and updates ITS ZStack node — but this frozen shell does NOT re-render, so
// MirrorParticipation never propagates the new size up to the scene root (Reconciler.cs:385-393 runs only on a
// component's own reconcile). FlexLayout.Run(root, window) then reads the root's STALE explicit li.Width/Height
// (FlexLayout.cs:39-40) and lays the whole app out at the PRE-resize size. This probe drives that exact path through the
// live AppHost so the bug is reproducible headlessly and guarded against regression.
sealed class ResizeShellProbe : Component
{
    public NodeHandle Bar, Middle;

    public override Element Render() => Embed.Comp(() => new OverlayHost { Child = BuildColumn() });

    Element BuildColumn()
    {
        var rows = new Element[30];
        for (int i = 0; i < rows.Length; i++) rows[i] = new BoxEl { Height = 44f, Fill = ColorF.FromRgba(40, 40, 40) };
        var tall = new BoxEl { Direction = 1, Children = rows };   // ~1320px sidebar content (taller than any viewport here)

        var sidebar = new BoxEl
        {
            Direction = 1, Shrink = 0f, Width = 240f, ClipToBounds = true,
            Children = [ Ui.ScrollView(tall) with { Grow = 1f, AutoEdgeFade = true } ],   // matches WaveeSidebar.cs:121
        };
        var content = new BoxEl { Grow = 1f };
        // The FIX form (Grow=1 + Shrink=1). The resize bug is independent of this — it is the stale-root-size mirror —
        // so this guard intentionally uses the fixed middle to prove the resize fault survives the Shrink fix.
        var middle = Ui.ZStack(new BoxEl { Direction = 0, Grow = 1f, Children = [sidebar, content] })
            with { Grow = 1f, Shrink = 1f, OnRealized = h => Middle = h };

        return new BoxEl
        {
            Direction = 1, Grow = 1f,
            Children =
            [
                new BoxEl { Height = 48f },                              // titlebar chrome
                new BoxEl { Height = 48f },                              // toolbar chrome
                middle,                                                 // bounded fill
                new BoxEl { Height = 56f, OnRealized = h => Bar = h },   // player bar dock
            ],
        };
    }
}

// Responsive-breakpoint resize probe. Mirrors the ArtistPage TopBand: a Responsive.Of band that flips Direction
// row→column at w<760 (so it grows TALLER when narrow), followed by a sibling section, all inside a vertical ScrollView.
// Resizing wide→narrow must reflow the column so the sibling moves BELOW the now-taller band — the reported overlap bug.
sealed class RespResizeProbe : Component
{
    public NodeHandle Band, Below;
    public override Element Render() => Embed.Comp(() => new OverlayHost { Child = Build() });

    static Element Col() => new BoxEl { Direction = 1, Children = [ new BoxEl { Height = 24f }, new BoxEl { Height = 300f } ] };  // header + tall content

    Element Build()
    {
        var band = Responsive.Of(w =>
        {
            bool wide = w >= 760f;
            return new BoxEl
            {
                Direction = (byte)(wide ? 0 : 1), Gap = 20f,
                Children =
                [
                    new BoxEl { Direction = 1, Grow = wide ? 2f : 1f, Basis = 0f, Children = [Col()] },
                    new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Children = [Col()] },
                ],
            };
        }, fallback: 900f);
        var col = new BoxEl
        {
            Direction = 1, Gap = 16f,
            Children =
            [
                new BoxEl { Direction = 1, OnRealized = h => Band = h, Children = [band] },   // wrapper to track the band's laid-out box
                new BoxEl { Height = 100f, OnRealized = h => Below = h },                     // the section below (must not be overlapped)
            ],
        };
        return Ui.ScrollView(col) with { Grow = 1f };
    }
}

// Collapsed-hero scroll-bind re-bake probe. Mirrors the artist hero's direct sticky owner: a tall scroll-content child
// owns both PinTop and PresentedHTrailing. Changing HeroHeight while already scrolled beyond the hero forces the bind rows
// to re-bake; the node must keep its collapsed PresentedH until the frame's continuous scroll pass re-applies the offset.
sealed class CollapsedHeroRebakeProbe : Component
{
    public readonly Signal<float> HeroHeight = new(200f);
    public NodeHandle Hero;
    public NodeHandle Media;   // scroll-bound Opacity (the hero photo proxy) — must stay 0 across a re-bake/re-theme

    // OverlayHost freezes its Child at mount (Embed.Comp contract) — the signal-reactive body must be its OWN component
    // inside, or HeroHeight changes silently never re-render (and the "re-bake" this probe exists to exercise never runs).
    public override Element Render() => Embed.Comp(() => new OverlayHost { Child = Embed.Comp(() => new Body(this)) });

    sealed class Body(CollapsedHeroRebakeProbe owner) : Component
    {
        public override Element Render()
        {
            float h = owner.HeroHeight.Value;
            var hero = new BoxEl
            {
                Height = h, ClipToBounds = true, Fill = ColorF.FromRgba(60, 80, 120),
                OnRealized = n => owner.Hero = n,
                ScrollBinds =
                [
                    new() { PinTop = 0f },
                    new() { From = ScrollChannel.Offset, To = BindSink.PresentedHTrailing, Range = ScrollRange.Px(0f, h), OutStart = h, OutEnd = 0f },
                ],
                Children =
                [
                    new BoxEl
                    {
                        Height = 32f, Fill = ColorF.FromRgba(220, 200, 120),
                        OnRealized = n => owner.Media = n,
                        // The artist-page hero photo's dissolve: opacity rides the collapse to 0. A re-theme re-render
                        // re-bakes this row; the fresh row's first eval must RE-WRITE the 0 (LastWritten seeds NaN) or
                        // the reconciled literal (1) pops the photo back over the collapsed band.
                        ScrollBinds =
                        [
                            new() { From = ScrollChannel.Offset, To = BindSink.Opacity, Range = ScrollRange.Px(h * 0.16f, h * 0.66f), OutStart = 1f, OutEnd = 0f, Ease = Easing.Linear },
                        ],
                    },
                ],
            };
            var content = new BoxEl
            {
                Direction = 1,
                Children =
                [
                    hero,
                    new BoxEl { Height = 700f, Fill = ColorF.FromRgba(30, 30, 34) },
                ],
            };
            return Ui.ScrollView(content) with { Grow = 1f };
        }
    }
}

// Sidebar drag-resize simulation probe. Mirrors WaveeShell's resizable sidebar: a width-BOUND pane (the width is a
// live FloatSignal binding, like _sidebarWidth) holding a tall, NON-virtual ScrollView of wrapping text rows (the
// playlist list), beside a Grow=1 content card — all under a frozen OverlayHost (WaveeShell builds the frame once).
// Driving Width over frames reproduces a grip drag's downstream: signal → binding → scoped relayout → pane width.
sealed class ResizeSidebarProbe : Component
{
    public readonly FluentGpu.Signals.FloatSignal WidthSig = new(400f);
    public readonly FluentGpu.Signals.Signal<bool> Dragging = new(false);
    public NodeHandle Pane;
    // IDENTICAL to WaveeShell.SidebarReflow — the pane's collapse spring (SizeMode.Reflow, 0.30s). The drag is meant to
    // SNAP it via the global reduced-motion gate; reproducing that exact path is what makes a "subsequent drag" state
    // regression observable headlessly (a stale/un-restored reflow track, or the gate not re-arming after drag #1).
    static readonly LayoutTransition SidebarReflow = new(
        TransitionChannels.Size, TransitionDynamics.Tween(Motion.ControlFast, Easing.SmoothOut), SizeMode.Reflow);

    public void BeginDrag()
    {
        FluentGpu.Dsl.Motion.ReducedMotion = true;
        Dragging.Value = true;
    }

    public void EndDrag() => Dragging.Value = false;

    public override Element Render()
    {
        bool dragging = Dragging.Value;
        // Exactly WaveeShell's gate: flip the global reduced-motion ON while dragging so the pane's reflow spring snaps,
        // restore it on release (OS default = false). A bug here makes drag #1 snap but later drags ease (= the lag).
        UseEffect(() => FluentGpu.Dsl.Motion.ReducedMotion = dragging, dragging);
        return Embed.Comp(() => new OverlayHost { Child = BuildRow() });
    }

    Element BuildRow()
    {
        var rows = new Element[24];
        for (int i = 0; i < rows.Length; i++)
            rows[i] = new BoxEl
            {
                Direction = 0, Height = 44f, AlignItems = FlexAlign.Center, Gap = 12f,
                Children =
                [
                    new BoxEl { Width = 32f, Height = 32f },                                                       // artwork
                    new TextEl("mellow pop wistful saturday late night " + i) { Size = 14f, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis, Grow = 1f },
                    new BoxEl { Width = 22f, Height = 16f },                                                       // count badge
                ],
            };
        var pane = new BoxEl
        {
            Direction = 1, Shrink = 0f, ClipToBounds = true, OnRealized = h => Pane = h,
            Width = Prop.Of(() => WidthSig.Value),
            Animate = SidebarReflow,                                   // the real shell carries the spring; the gate must snap it
            Children = [ Ui.ScrollView(new BoxEl { Direction = 1, Children = rows }) with { Grow = 1f } ],
        };
        return new BoxEl { Direction = 0, Grow = 1f, Children = [ pane, new BoxEl { Grow = 1f } ] };
    }
}

// Faithful headless mirror of the WaveeShell projected collapse toggle (SidebarCollapseFlipChecks) — a component that
// re-renders on the compact signal (like WaveeShell), a bound-width pane carrying the exact Reveal+Suppress transition,
// a stable ROW frame (MorphId), and a Grow content card carrying the Position Reveal. Mode picks the card-FLIP anchor.
sealed class CollapseToggleProbe : Component
{
    public readonly FluentGpu.Signals.Signal<bool> CompactSig = new(false);
    public NodeHandle Pane, Card, Content;
    public static int Mode = 0;   // 0=card(current bug) 1=region 2=card+RelativeTo(row)
    static readonly EasingSpec Ease = EasingSpec.CubicBezier(0f, 0.35f, 0.15f, 1f);
    static readonly LayoutTransition PaneAnim = new(
        TransitionChannels.Size | TransitionChannels.Position,
        TransitionDynamics.Tween(300f, Ease), SizeMode.Reveal,
        ExitDynamics: TransitionDynamics.Tween(300f, Ease),
        SuppressDescendantTransitions: true);
    static readonly LayoutTransition CardAnim = new(
        TransitionChannels.Position,
        TransitionDynamics.Tween(300f, Ease), SizeMode.Reveal,
        ExitDynamics: TransitionDynamics.Tween(300f, Ease),
        SuppressDescendantTransitions: true);

    public override Element Render()
    {
        _ = CompactSig.Value;   // subscribe → re-render on toggle (mirrors WaveeShell.cs:244)
        return Embed.Comp(() => new OverlayHost { Child = BuildRow() });
    }

    Element BuildRow()
    {
        var rows = new Element[16];
        for (int i = 0; i < rows.Length; i++) rows[i] = new BoxEl { Height = 40f, Fill = ColorF.FromRgba(30, 30, 30) };
        var pane = new BoxEl
        {
            Direction = 1, Shrink = 0f, ClipToBounds = true, OnRealized = h => Pane = h,
            Fill = ColorF.FromRgba(27, 32, 40),
            Width = Prop.Of(() => CompactSig.Value ? 56f : 360f),
            Animate = PaneAnim,
            Children = [ new BoxEl { Direction = 1, Grow = 1f, Children = [ Ui.ScrollView(new BoxEl { Direction = 1, Children = rows }) with { Grow = 1f } ] } ],
        };
        var card = new BoxEl
        {
            Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, ClipToBounds = true, IsolateLayout = true, OnRealized = h => Card = h,
            Fill = ColorF.FromRgba(57, 61, 69),
            Animate = Mode == 1 ? null : CardAnim,
            RelativeTo = Mode == 2 ? "shellrow" : null,
            Children = [ new BoxEl { Grow = 1f } ],
        };
        var content = new BoxEl { Direction = 1, ZStack = true, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Basis = 0f, OnRealized = h => Content = h, Children = [ card ], Animate = Mode == 1 ? CardAnim : null };
        return new BoxEl { Direction = 0, Grow = 1f, ClipToBounds = true, MorphId = "shellrow", Children = [ pane, content ] };
    }
}

// A long-lived text field and bound card border used to prove that a retheme updates retained controls in place.
sealed class LiveThemeDefaultsProbe : Component
{
    public int FieldConstructions;

    public override Element Render() => new BoxEl
    {
        Width = 180f,
        Height = 40f,
        BorderWidth = 1f,
        BorderColor = Prop.Of(static () => Tok.StrokeCardDefault),
        Children =
        [
            Embed.Comp(() =>
            {
                FieldConstructions++;
                return new EditableText { Initial = "typed", Width = 160f };
            }),
        ],
    };
}

// ── G1c: LayoutBoundary (.Boundary) + relayout-escape counter + UseMeasuredBounds/Width probes ──────────────────────

// A deep-nested subtree with a re-rendering leaf, wrapped in a container that is EITHER a `.Boundary()` (layout firewall)
// or a plain flex box. Flipping the leaf signal marks the deep node LayoutDirty; the boundary decides whether the scoped
// relayout stops at the container (escapes = 0) or walks all the way to the scene root (escapes >= 1).
sealed class BoundaryEscapeProbe : Component
{
    public static bool UseBoundary;
    public readonly FluentGpu.Signals.Signal<int> Deep = new(0);

    public override Element Render()
    {
        var inner = new BoxEl { Grow = 1f, Direction = 1, Children = [ Embed.Comp(() => new BoundaryDeepLeaf { Sig = Deep }) ] };
        var container = new BoxEl { Grow = 1f, Direction = 1, Children = [ inner ] };
        if (UseBoundary) container = container.Boundary();
        return new BoxEl { Grow = 1f, Direction = 1, Children = [ container ] };
    }
}

sealed class BoundaryDeepLeaf : Component
{
    public FluentGpu.Signals.Signal<int> Sig = null!;
    public override Element Render()
    {
        int v = Sig.Value;   // subscribe → re-render (and change size, marking LayoutDirty) on flip
        return new BoxEl { Width = 100f, Height = 20f + v };
    }
}

// Hosts a nested component so the measured component's HostNode is its OWN rendered box (NOT the scene root, which is what
// the top-level component's HostNode is forced to). The child's box width is a bound FloatSignal (a compositor/layout
// bind — no re-render), driven per frame to reproduce a resize.
sealed class MeasureHost : Component
{
    readonly Component _child;
    public MeasureHost(Component child) => _child = child;
    public override Element Render() => new BoxEl { Grow = 1f, Direction = 0, Children = [ Embed.Comp(() => _child) ] };
}

sealed class MeasuredWidthProbe : Component
{
    public readonly FluentGpu.Signals.FloatSignal DriveWidth = new(100f);
    public float Quantum;
    public int RenderCount;
    public float LastSeen = -1f;

    public override Element Render()
    {
        RenderCount++;
        var w = UseMeasuredWidth(Quantum);
        LastSeen = w.Value;   // subscribe → re-render when the (quantized) measured width changes
        return new BoxEl { Grow = 0f, Shrink = 0f, Height = 50f, Width = Prop.Of(() => DriveWidth.Value) };
    }
}

sealed class ComposeBoundsProbe : Component
{
    public readonly FluentGpu.Signals.FloatSignal DriveWidth = new(100f);
    public int AuthorFires;
    public int RenderCount;
    public float LastSeenW = -1f;

    public override Element Render()
    {
        RenderCount++;
        var bounds = UseMeasuredBounds();
        LastSeenW = bounds.Value.W;   // subscribe
        return new BoxEl
        {
            Grow = 0f, Shrink = 0f, Height = 50f,
            Width = Prop.Of(() => DriveWidth.Value),
            OnBoundsChanged = _ => AuthorFires++,   // the element author's own handler — must still fire alongside the hook
        };
    }
}

