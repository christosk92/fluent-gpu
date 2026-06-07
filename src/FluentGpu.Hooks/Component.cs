using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Hooks;

/// <summary>Base for stateful components. Override <see cref="Render"/>; use hooks in stable order each render.</summary>
public abstract class Component
{
    public RenderContext Context { get; internal set; } = new();

    public abstract Element Render();

    protected (T Value, Action<T> Set) UseState<T>(T initial) => Context.UseState(initial);
    protected void UseEffect(Action effect, params object[] deps) => Context.UseEffect(effect, deps);
    protected void UseLayoutEffect(Action effect, params object[] deps) => Context.UseLayoutEffect(effect, deps);
    protected (TState State, Action<TAction> Dispatch) UseReducer<TState, TAction>(Func<TState, TAction, TState> reducer, TState initial) => Context.UseReducer(reducer, initial);
    protected T UseMemo<T>(Func<T> factory, params object[] deps) => Context.UseMemo(factory, deps);
    protected Ref<T> UseRef<T>(T initial) => Context.UseRef(initial);
    protected T UseContext<T>(Context<T> context) => Context.UseContext(context);
    protected float UseAnimatedValue(float target, float durationMs = 180f) => Context.UseAnimatedValue(target, durationMs);

    // Declarative, composited animation of this component's node (no per-frame re-render):
    protected void UseSpring(AnimChannel channel, float to, SpringParams spring, params object[] deps) => Context.UseSpring(channel, to, spring, deps);
    protected void UseTransition(AnimChannel channel, float from, float to, float durationMs, Easing easing = Easing.EaseInOut, params object[] deps) => Context.UseTransition(channel, from, to, durationMs, easing, deps);
    /// <summary>Bind an async image and observe its load state (spinner / error fallback). Pair with <c>Ui.Image</c> to paint it.</summary>
    protected ImageBinding UseImage(string src, int decodePx, ImagePriority priority = ImagePriority.Visible, string? blurHash = null) => Context.UseImage(src, decodePx, priority, blurHash);
    /// <summary>Prefetch an image the UI is about to need so it's resident before it scrolls in.</summary>
    protected void PrefetchImage(string src, int decodePx) => Context.PrefetchImage(src, decodePx);
    protected void UseKeyframes(AnimChannel channel, Keyframe[] keys, float durationMs, bool loop = false, params object[] deps) => Context.UseKeyframes(channel, keys, durationMs, loop, deps);
    protected void UseDrivenAnimation(AnimChannel channel, Keyframe[] keys, Func<float> source, float min, float max, params object[] deps) => Context.UseDrivenAnimation(channel, keys, source, min, max, deps);

    /// <summary>Run one render pass with hook bookkeeping.</summary>
    public Element RenderWithHooks()
    {
        Context.BeginRender();
        var el = Render();
        Context.EndRender();
        return el;
    }

    /// <summary>Called by the reconciler when this component leaves the tree — run effect cleanups.</summary>
    public void Unmount() => Context.RunAllCleanups();
}
