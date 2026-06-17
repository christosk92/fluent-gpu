using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Forms;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Hooks;

/// <summary>Base for stateful components. Override <see cref="Render"/>; use hooks in stable order each render.</summary>
public abstract class Component
{
    public RenderContext Context { get; internal set; } = new();

    public abstract Element Render();

    protected (T Value, Action<T> Set) UseState<T>(T initial) => Context.UseState(initial);
    /// <summary>A persistent reactive cell you own (read <c>.Value</c> in render to subscribe, or bind it to a node).</summary>
    protected Signal<T> UseSignal<T>(T initial) => Context.UseSignal(initial);
    /// <summary>A persistent scalar signal for the hot path; bind it to a node channel for a render-free update.</summary>
    protected FluentGpu.Signals.FloatSignal UseFloatSignal(float initial = 0f) => Context.UseFloatSignal(initial);
    /// <summary>A derived reactive value recomputed from the signals it reads (the Solid <c>createMemo</c>).</summary>
    protected Memo<T> UseComputed<T>(Func<T> compute) => Context.UseComputed(compute);
    protected void UseEffect(Action effect, params object[] deps) => Context.UseEffect(effect, deps);
    protected void UseLayoutEffect(Action effect, params object[] deps) => Context.UseLayoutEffect(effect, deps);
    protected (TState State, Action<TAction> Dispatch) UseReducer<TState, TAction>(Func<TState, TAction, TState> reducer, TState initial) => Context.UseReducer(reducer, initial);
    protected T UseMemo<T>(Func<T> factory, params object[] deps) => Context.UseMemo(factory, deps);
    protected Ref<T> UseRef<T>(T initial) => Context.UseRef(initial);
    protected T UseContext<T>(Context<T> context) => Context.UseContext(context);
    /// <summary>Read the nearest context as a signal without subscribing this component render.</summary>
    protected IReadSignal<T> UseContextSignal<T>(Context<T> context) => Context.UseContextSignal(context);
    /// <summary>Mount a signal-tracked effect owned by this component.</summary>
    protected void UseSignalEffect(Action effect) => Context.UseSignalEffect(effect);
    /// <summary>The host UI-thread poster (<see cref="HostDispatch.Post"/>): run an action on the UI thread next frame
    /// from any thread. Use for off-thread data instead of <c>UseContext(FrameClock.Tick)</c> + a per-frame drain.</summary>
    protected Action<Action> UsePost() => Context.UsePost();
    /// <summary>Reactive snapshot of the live drag (in-app <c>DragSource</c> or OS file drag) — re-renders on drag
    /// begin/move/end. Render a cursor-following custom preview (see <c>DragPreviewLayer</c>) from it.</summary>
    protected DragState UseDragState() => Context.UseDragState();
    /// <summary>A persistent per-field async value (Pending|Ready|Failed) — the skeleton-loading spine; flip with SetReady/SetFailed.</summary>
    protected Loadable<T> UseLoadable<T>(Loadable<T>? initial = null) => Context.UseLoadable(initial);
    /// <summary>Kick an async loader once at mount; returns a Loadable&lt;T&gt; (Pending→Ready/Failed via UsePost; cancels on unmount).</summary>
    protected Loadable<T> UseAsyncResource<T>(Func<System.Threading.CancellationToken, System.Threading.Tasks.Task<T>> loader, T seed = default!) => Context.UseAsyncResource(loader, seed);

    /// <summary>Create a reactive validation field over a caller-owned value signal (form-validation.md): pass the
    /// resulting <see cref="Field{T}"/> to a control's <c>Field</c> prop. Cross-field/conditional rules that read a
    /// sibling signal re-validate automatically.</summary>
    protected Field<T> UseField<T>(Signal<T> value, params Validator<T>[] rules) => Context.UseField(value, rules);
    /// <summary>As <see cref="UseField{T}(Signal{T}, Validator{T}[])"/> with timing/async/compound/explicit-form options.</summary>
    protected Field<T> UseField<T>(Signal<T> value, FieldOptions<T> options, params Validator<T>[] rules) => Context.UseField(value, options, rules);
    /// <summary>Establish a <see cref="FormScope"/> for this component (submit gating + focus-first-error); the
    /// <c>UseField</c> calls that follow in this render auto-join it.</summary>
    protected FormScope UseForm() => Context.UseForm();

    /// <summary>Bind a localized string into a text node with no re-render (<see cref="RenderContext.L"/>):
    /// <c>new TextEl("") { Text = L("app.title") }</c>. A culture switch re-resolves only the bound node.</summary>
    protected FluentGpu.Signals.Prop<string> L(string key) => Context.L(key);
    /// <summary>Bind a localized, formatted string (named placeholders / ICU plural-select) into a text node with no
    /// re-render (<see cref="RenderContext.Lf"/>): <c>Text = Lf("player.added", ("name", track))</c>.</summary>
    protected FluentGpu.Signals.Prop<string> Lf(string key, params (string Name, object Value)[] args) => Context.Lf(key, args);
    /// <summary>The active culture + a setter, re-rendering this component on a culture change
    /// (<see cref="RenderContext.UseLocale"/>) — for render that branches on the culture (a language picker).</summary>
    protected (string Culture, Action<string> SetCulture) UseLocale() => Context.UseLocale();
    protected float UseAnimatedValue(float target, float durationMs = 180f, Easing easing = Easing.EaseInOut) => Context.UseAnimatedValue(target, durationMs, easing);

    // Declarative, composited animation of this component's node (no per-frame re-render):
    protected void UseSpring(AnimChannel channel, float to, SpringParams spring, params object[] deps) => Context.UseSpring(channel, to, spring, deps);
    protected void UseTransition(AnimChannel channel, float from, float to, float durationMs, Easing easing = Easing.EaseInOut, params object[] deps) => Context.UseTransition(channel, from, to, durationMs, easing, deps);
    /// <summary>Bind an async image and observe its load state (spinner / error fallback). Pair with <c>Ui.Image</c> to paint it.</summary>
    protected ImageBinding UseImage(string src, int decodePx, ImagePriority priority = ImagePriority.Visible, string? blurHash = null) => Context.UseImage(src, decodePx, priority, blurHash);
    /// <summary>Prefetch an image the UI is about to need so it's resident before it scrolls in.</summary>
    protected void PrefetchImage(string src, int decodePx) => Context.PrefetchImage(src, decodePx);
    protected void UseKeyframes(AnimChannel channel, Keyframe[] keys, float durationMs, bool loop = false, params object[] deps) => Context.UseKeyframes(channel, keys, durationMs, loop, deps);
    protected void UseDrivenAnimation(AnimChannel channel, Keyframe[] keys, Func<float> source, float min, float max, params object[] deps) => Context.UseDrivenAnimation(channel, keys, source, min, max, deps);
    /// <summary>Declare a gesture handler on this component's node (input-a11y.md §13): config-only, enrolls a
    /// gesture-arena member and routes the winner's Tap/Hold/Pan event to <paramref name="handler"/>. No re-render.</summary>
    protected void UseGesture(GestureType kind, Action<GestureEventArgs> handler) => Context.UseGesture(kind, handler);

    /// <summary>
    /// True for run-once (signals-native) components: the reconciler runs their body untracked, so the render-effect
    /// never re-subscribes and never re-renders — reactivity comes purely from the bindings/For/Show inside. False for
    /// classic <see cref="Component"/>s, whose render-effect re-runs on their own state/context changes (granular).
    /// </summary>
    public virtual bool RunsOnce => false;

    /// <summary>Run one render pass with hook bookkeeping. In DEBUG / FLUENTGPU_DIAG builds the render duration is fed to
    /// the <see cref="FluentGpu.Hosting.RenderBudget"/> tripwire (slow-render + every-frame-re-render detection); the
    /// timing guard folds away entirely in release (<c>CompiledIn</c> is a const <c>false</c>), so the shipping path is
    /// exactly <c>BeginRender(); Render(); EndRender();</c> at zero cost.</summary>
    public Element RenderWithHooks()
    {
        Context.BeginRender();
        Element el;
        if (FluentGpu.Hosting.RenderBudget.CompiledIn)
        {
            long rbStart = FluentGpu.Hosting.RenderBudget.Begin();
            el = Render();
            FluentGpu.Hosting.RenderBudget.End(this, rbStart);
        }
        else el = Render();
        Context.EndRender();
        return el;
    }

    /// <summary>Called by the reconciler when this component leaves the tree — run effect cleanups.</summary>
    public void Unmount() => Context.RunAllCleanups();
}

/// <summary>
/// A signals-native component: <see cref="Setup"/> runs ONCE at mount (the SolidJS model) — create signals, wire
/// bindings (<c>TransformBind</c>/<c>For</c>/<c>Show</c>) and return the tree. The component never re-renders;
/// fine-grained bindings update exactly the nodes that read a changed signal. Use this for the highest-performance UI;
/// use plain <see cref="Component"/> when you prefer the re-render model (still granular).
/// </summary>
public abstract class ReactiveComponent : Component
{
    private Element? _tree;
    public sealed override bool RunsOnce => true;

    /// <summary>Build the tree once. Reads of signals here do NOT subscribe a re-render — bind them instead.</summary>
    public abstract Element Setup();

    public sealed override Element Render() => _tree ??= Setup();

    /// <summary>Drop the cached tree so the next render re-runs <see cref="Setup"/>. Used by the host's live re-theme
    /// (<c>Reconciler.RethemeAll</c>) to refresh construction-resolved token colors (e.g. a <c>Tok.*</c> read directly in
    /// <c>Setup</c>) without a remount: the render-effect re-runs <c>Setup</c> against the SAME <see cref="Component.Context"/>,
    /// so positional hook cells (state signals, effects, refs) are reused — state and signal identity survive, exactly like
    /// a plain <see cref="Component"/> re-render. Safe when <c>Setup</c>'s hook call-order and root element type are
    /// theme-invariant (no hook call gated on a token value), which holds for all in-repo ReactiveComponents.</summary>
    public void InvalidateTree() => _tree = null;
}
