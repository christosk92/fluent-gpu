using System.Runtime.CompilerServices;
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

    // NOTE: cell-backed hooks thread [CallerFilePath]/[CallerLineNumber] so the KEYED substrate resolves each cell to
    // the USER's call site (not this forwarder's location). Non-cell hooks (UseContext/UsePost/L/Lf/…) need no key.
    protected (T Value, Action<T> Set) UseState<T>(T initial, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) => Context.UseState(initial, __hf, __hl);
    /// <summary>A persistent reactive cell you own (read <c>.Value</c> in render to subscribe, or bind it to a node).</summary>
    protected Signal<T> UseSignal<T>(T initial, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) => Context.UseSignal(initial, __hf, __hl);
    /// <summary>A persistent scalar signal for the hot path; bind it to a node channel for a render-free update.</summary>
    protected FluentGpu.Signals.FloatSignal UseFloatSignal(float initial = 0f, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) => Context.UseFloatSignal(initial, __hf, __hl);
    /// <summary>A derived reactive value recomputed from the signals it reads (the Solid <c>createMemo</c>).</summary>
    protected Memo<T> UseComputed<T>(Func<T> compute, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) => Context.UseComputed(compute, __hf, __hl);
    /// <summary>Auto-tracked effect (the default): the body runs with signal-read tracking and re-runs when a signal it
    /// read changes (re-armed every run); a body that reads no signal runs once. Runs in the passive-effect drain.</summary>
    protected void UseEffect(Action effect, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) => Context.UseEffect(effect, __hf, __hl);
    /// <summary>Auto-tracked effect returning a CLEANUP (run before each re-run and once at unmount).</summary>
    protected void UseEffect(Func<Action?> effect, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) => Context.UseEffect(effect, __hf, __hl);
    /// <summary>Auto-tracked layout effect (phase 6.5 — Bounds valid) — see <see cref="UseEffect(Action)"/>.</summary>
    protected void UseLayoutEffect(Action effect, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) => Context.UseLayoutEffect(effect, __hf, __hl);
    /// <inheritdoc cref="UseLayoutEffect(Action)"/>
    protected void UseLayoutEffect(Func<Action?> effect, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) => Context.UseLayoutEffect(effect, __hf, __hl);
    // Deps-gated (explicit opt-in, no tracking): runs when the DepKey changes; DepKey.Empty/default = mount-once.
    // [OverloadResolutionPriority(1)] so a bare-string deps still binds here, not to the auto overload's CallerFilePath.
    [OverloadResolutionPriority(1)] protected void UseEffect(Action effect, DepKey deps, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) => Context.UseEffect(effect, deps, __hf, __hl);
    [OverloadResolutionPriority(1)] protected void UseEffect(Func<Action?> effect, DepKey deps, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) => Context.UseEffect(effect, deps, __hf, __hl);
    [OverloadResolutionPriority(1)] protected void UseLayoutEffect(Action effect, DepKey deps, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) => Context.UseLayoutEffect(effect, deps, __hf, __hl);
    [OverloadResolutionPriority(1)] protected void UseLayoutEffect(Func<Action?> effect, DepKey deps, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) => Context.UseLayoutEffect(effect, deps, __hf, __hl);
    protected (TState State, Action<TAction> Dispatch) UseReducer<TState, TAction>(Func<TState, TAction, TState> reducer, TState initial, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) => Context.UseReducer(reducer, initial, __hf, __hl);
    protected T UseMemo<T>(Func<T> factory, DepKey deps, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) => Context.UseMemo(factory, deps, __hf, __hl);
    protected Ref<T> UseRef<T>(T initial, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) => Context.UseRef(initial, __hf, __hl);
    protected T UseContext<T>(Context<T> context) => Context.UseContext(context);
    /// <summary>Read this component's RE-PUSHED props (<c>Embed.Comp(props, () =&gt; new This())</c>) as
    /// <typeparamref name="T"/> — the parent→child data channel. Subscribes this component's render-effect so a changed
    /// re-push re-renders it in place (no remount, node identity + hook state preserved). Throws (naming this type) when
    /// mounted without props; use <see cref="UsePropsOrDefault{T}"/> for a component usable both ways. See
    /// <see cref="RenderContext.UseProps{T}"/>.</summary>
    protected T UseProps<T>() where T : class => Context.UseProps<T>(GetType());
    /// <summary>Read this component's re-pushed props, or <c>null</c> when mounted propless — for a component usable
    /// both with props and standalone. See <see cref="RenderContext.UsePropsOrDefault{T}"/>.</summary>
    protected T? UsePropsOrDefault<T>() where T : class => Context.UsePropsOrDefault<T>();
    /// <summary>Like <see cref="UseContext{T}"/> but MANDATORY: throws (naming the context type) when no provider is in
    /// scope or it carries null — for a dependency the component cannot render without. See <see cref="RenderContext.UseRequiredContext"/>.</summary>
    protected T UseRequiredContext<T>(Context<T> context) => Context.UseRequiredContext(context);
    /// <summary>Read the nearest context as a signal without subscribing this component render.</summary>
    protected IReadSignal<T> UseContextSignal<T>(Context<T> context, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) => Context.UseContextSignal(context, __hf, __hl);
    /// <summary>Mount a signal-tracked effect owned by this component.</summary>
    protected void UseSignalEffect(Action effect, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) => Context.UseSignalEffect(effect, __hf, __hl);
    /// <summary>This component's activation state as a reactive signal — <c>false</c> when parked by <c>Flow.KeepAlive</c>
    /// (a backgrounded tab) OR the window is minimized/suspended. Read <c>.Value</c> in render to gate a live-only
    /// affordance; for pause/resume callbacks use <see cref="UseActivation"/>.</summary>
    protected IReadSignal<bool> UseIsActive([CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) => Context.UseIsActive(__hf, __hl);
    /// <summary>Notify-only pause/resume lifecycle: <paramref name="onDeactivated"/> runs when this component goes
    /// inactive (page parked OR window minimized/suspended), <paramref name="onActivated"/> when it returns. Transitions
    /// only — never at mount (start work in <c>UseEffect</c>) or unmount (use its cleanup). Pause your own background
    /// work (poll/timer/OS subscription) here.</summary>
    protected void UseActivation(Action? onActivated = null, Action? onDeactivated = null, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) => Context.UseActivation(onActivated, onDeactivated, __hf, __hl);
    /// <summary>The host UI-thread poster (<see cref="HostDispatch.Post"/>): run an action on the UI thread next frame
    /// from any thread. Use for off-thread data instead of <c>UseContext(FrameClock.Tick)</c> + a per-frame drain.</summary>
    protected Action<Action> UsePost() => Context.UsePost();
    /// <summary>Reactive snapshot of the live drag (in-app <c>DragSource</c> or OS file drag) — re-renders on drag
    /// begin/move/end. Render a cursor-following custom preview (see <c>DragPreviewLayer</c>) from it.</summary>
    protected DragState UseDragState() => Context.UseDragState();

    // ── Timing hooks (frame-clock HostTimerQueue; never the media clock) ────────────────────────────────────────────
    /// <summary>A read signal that follows <paramref name="source"/> after <paramref name="ms"/> of quiet (trailing-edge
    /// debounce; zero re-render). See <see cref="RenderContext.UseDebouncedValue{T}(IReadSignal{T}, float)"/>.</summary>
    protected IReadSignal<T> UseDebouncedValue<T>(IReadSignal<T> source, float ms, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) => Context.UseDebouncedValue(source, ms, __hf, __hl);
    /// <inheritdoc cref="RenderContext.UseDebouncedValue{T}(IReadSignal{T}, float, out DebounceHandle)"/>
    protected IReadSignal<T> UseDebouncedValue<T>(IReadSignal<T> source, float ms, out DebounceHandle handle, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) => Context.UseDebouncedValue(source, ms, out handle, __hf, __hl);
    /// <summary>Thunk form of debounce — <paramref name="source"/> is a getter over the signals to watch.</summary>
    protected IReadSignal<T> UseDebouncedValue<T>(Func<T> source, float ms, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) => Context.UseDebouncedValue(source, ms, __hf, __hl);
    /// <inheritdoc cref="RenderContext.UseDebouncedValue{T}(Func{T}, float, out DebounceHandle)"/>
    protected IReadSignal<T> UseDebouncedValue<T>(Func<T> source, float ms, out DebounceHandle handle, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) => Context.UseDebouncedValue(source, ms, out handle, __hf, __hl);
    /// <summary>A read signal that follows <paramref name="source"/> at most once per <paramref name="ms"/> — leading
    /// edge + trailing sample (zero re-render). See <see cref="RenderContext.UseThrottledValue{T}(IReadSignal{T}, float)"/>.</summary>
    protected IReadSignal<T> UseThrottledValue<T>(IReadSignal<T> source, float ms, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) => Context.UseThrottledValue(source, ms, __hf, __hl);
    /// <summary>Thunk form of throttle.</summary>
    protected IReadSignal<T> UseThrottledValue<T>(Func<T> source, float ms, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) => Context.UseThrottledValue(source, ms, __hf, __hl);
    /// <summary>Fire <paramref name="callback"/> once <paramref name="ms"/> from now, restarting on
    /// <paramref name="deps"/> change (default = once from mount); a due fire after unmount is a no-op. See
    /// <see cref="RenderContext.UseTimeout"/>.</summary>
    protected TimerHandle UseTimeout(Action callback, float ms, DepKey deps = default, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) => Context.UseTimeout(callback, ms, deps, __hf, __hl);
    /// <summary>Fire <paramref name="tick"/> every <paramref name="ms"/> while <paramref name="enabled"/> and the
    /// component is active — auto-pauses while parked/minimized, resumes cleanly. See <see cref="RenderContext.UseInterval"/>.</summary>
    protected void UseInterval(Action tick, float ms, bool enabled = true, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) => Context.UseInterval(tick, ms, enabled, __hf, __hl);
    /// <summary>The arranged LOCAL bounds of this component's rendered root (<see cref="RenderContext.HostNode"/>) as a
    /// read signal. Written during layout ⇒ consumers re-render NEXT frame. See <see cref="RenderContext.UseMeasuredBounds"/>.</summary>
    protected IReadSignal<FluentGpu.Foundation.RectF> UseMeasuredBounds([CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) => Context.UseMeasuredBounds(__hf, __hl);
    /// <summary>The arranged width of this component's rendered root as a read signal; <paramref name="quantum"/> &gt; 0
    /// rounds the value to that grid before the write (kills sub-quantum churn). See <see cref="RenderContext.UseMeasuredWidth"/>.</summary>
    protected IReadSignal<float> UseMeasuredWidth(float quantum = 0f, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) => Context.UseMeasuredWidth(quantum, __hf, __hl);
    /// <summary>A persistent per-field async value (Pending|Ready|Failed) — the skeleton-loading spine; flip with SetReady/SetFailed.</summary>
    protected Loadable<T> UseLoadable<T>(Loadable<T>? initial = null, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) => Context.UseLoadable(initial, __hf, __hl);
    /// <summary>A stale-while-revalidate async resource: kicks the loader at mount, RELOADS when <paramref name="deps"/>
    /// change (value-equality — a page reused across navigation whose id changes), and returns a <see cref="Resource{T}"/>
    /// with a Loadable spine + IsFetching/IsStale + Refresh()/Mutate(). Every load is epoch-stamped (an older slower fetch
    /// can never overwrite a newer one); the in-flight load + stale timer cancel on unmount. See <see cref="RenderContext.UseResource"/>.</summary>
    protected Resource<T> UseResource<T>(Func<System.Threading.CancellationToken, System.Threading.Tasks.Task<T>> loader, T seed = default!, DepKey deps = default, ResourceOptions? options = null, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) => Context.UseResource(loader, seed, deps, options, __hf, __hl);
    /// <summary>A fire-on-demand async command with a reactive IsRunning state (spinner/disable + re-entry guard + cancel).</summary>
    protected AsyncCommand UseAsyncCommand(bool cancelOnUnmount = false, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) => Context.UseAsyncCommand(cancelOnUnmount, __hf, __hl);
    /// <summary>A KEYED set of fire-on-demand async commands (per-item busy state, e.g. per-row play/like).</summary>
    protected AsyncCommandSet<TKey> UseAsyncCommands<TKey>(bool cancelOnUnmount = false, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) where TKey : notnull => Context.UseAsyncCommands<TKey>(cancelOnUnmount, __hf, __hl);

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
    protected float UseAnimatedValue(float target, float durationMs = 180f, Easing easing = Easing.EaseInOut, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) => Context.UseAnimatedValue(target, durationMs, easing, __hf, __hl);

    // Declarative, composited animation of this component's node (no per-frame re-render); DepKey-gated (Empty = seed once):
    protected void UseSpring(AnimChannel channel, float to, SpringParams spring, DepKey deps) => Context.UseSpring(channel, to, spring, deps);
    protected void UseTransition(AnimChannel channel, float from, float to, float durationMs, Easing easing, DepKey deps) => Context.UseTransition(channel, from, to, durationMs, easing, deps);
    /// <summary>Bind an async image and observe its load state (spinner / error fallback). Pair with <c>Ui.Image</c> to paint it.</summary>
    protected ImageBinding UseImage(string src, int decodePx, ImagePriority priority = ImagePriority.Visible, string? blurHash = null) => Context.UseImage(src, decodePx, priority, blurHash);
    /// <summary>As <see cref="UseImage(string,int,ImagePriority,string)"/> but with a non-square decode target — shares the exact cache handle of a non-square displayed image instead of forking a second decode.</summary>
    protected ImageBinding UseImage(string src, int decodeW, int decodeH, ImagePriority priority = ImagePriority.Visible, string? blurHash = null) => Context.UseImage(src, decodeW, decodeH, priority, blurHash);
    /// <summary>Prefetch an image the UI is about to need so it's resident before it scrolls in.</summary>
    protected void PrefetchImage(string src, int decodePx) => Context.PrefetchImage(src, decodePx);
    /// <summary>Acquire a composited video surface for this component (released automatically on unmount). A media player
    /// writes placement + a bound DirectComposition handle through the returned binding; draw a transparent hole at the
    /// same rect for the video to show through. A safe no-op binding when video compositing is unavailable.</summary>
    protected FluentGpu.Media.VideoBinding UseVideoSurface([CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) => Context.UseVideoSurface(__hf, __hl);
    /// <summary>Create a component-lifetime-owned disposable once at mount; it is disposed automatically on unmount.</summary>
    protected T? UseDisposable<T>(Func<T?> factory, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) where T : class, IDisposable => Context.UseDisposable(factory, __hf, __hl);
    /// <summary>A <see cref="FluentGpu.Media.MediaPlayer"/> whose lifetime is bound to this component; auto-disposed
    /// (async) on unmount. <paramref name="configure"/> runs once at mount against the Layer-2 builder.</summary>
    protected FluentGpu.Media.MediaPlayer UseMediaPlayer(Action<FluentGpu.Media.MediaPlayerBuilder>? configure = null, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) => Context.UseMediaPlayer(configure, __hf, __hl);
    /// <summary>A <see cref="FluentGpu.Media.MediaPlayer"/> pointed at a source that re-loads when the <paramref name="source"/>
    /// thunk yields a different value (auto SMTC/buffering/default tracks; auto-disposed on unmount).</summary>
    protected FluentGpu.Media.MediaPlayer UseVideo(Func<FluentGpu.Media.MediaSource> source) => Context.UseVideo(source);
    protected void UseKeyframes(AnimChannel channel, Keyframe[] keys, float durationMs, bool loop, DepKey deps) => Context.UseKeyframes(channel, keys, durationMs, loop, deps);
    protected void UseDrivenAnimation(AnimChannel channel, Keyframe[] keys, Func<float> source, float min, float max, DepKey deps) => Context.UseDrivenAnimation(channel, keys, source, min, max, deps);
    /// <summary>Declare a gesture handler on this component's node (input-a11y.md §13): config-only, enrolls a
    /// gesture-arena member and routes the winner's Tap/Hold/Pan event to <paramref name="handler"/>. No re-render.</summary>
    protected void UseGesture(GestureType kind, Action<GestureEventArgs> handler) => Context.UseGesture(kind, handler);

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

    /// <summary>DEBUG-only (<see cref="ReuseGuard"/>): opt into the frozen-props tripwire. A control that carries
    /// caller data in scalar fields (count / label / IsEnabled …) returns <c>true</c> so the reconciler builds the
    /// would-be replacement and calls <see cref="DebugCheckReuse"/> on reuse. Default <c>false</c> (no probe alloc).</summary>
    public virtual bool ChecksReuse => false;

    /// <summary>DEBUG-only (<see cref="ReuseGuard"/>): the reconciler passes <paramref name="next"/> — a throwaway
    /// instance built by the NEW (discarded) factory — when it REUSES <c>this</c>. Compare the frozen field(s) that
    /// carry caller data; call <see cref="ReuseGuard.Violation"/> when one changed (it was frozen at mount and the new
    /// value is being silently dropped). Only invoked when <see cref="ChecksReuse"/> is true and the guard is enabled;
    /// never called in release. Deliver such data reactively (Signal / Ctx.Provide) or remount with a Key instead.</summary>
    public virtual void DebugCheckReuse(Component next) { }
}
