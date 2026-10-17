using FluentGpu.Signals;

namespace FluentGpu.Hooks;

internal enum TierSourceKind : byte { ViewportWidth, ViewportHeight, Measured, Estimate }

/// <summary>
/// Where <see cref="RenderContext.UseTier"/> reads the value it resolves against its <c>TierLadder</c>:
/// <list type="bullet">
/// <item><description><see cref="ViewportWidth"/> / <see cref="ViewportHeight"/> — the ambient <see cref="Viewport.Size"/>
/// (same-frame: known before the first layout; the aspect rule sees width / height).</description></item>
/// <item><description><see cref="Measured"/> — a measured slot signal (<c>UseMeasuredWidth</c>, a bounds mirror); optionally
/// seeded by an estimate while the slot still reads 0 (not measured yet), so the first frame is already in its tier.</description></item>
/// <item><description><see cref="Estimate"/> — a supplied thunk (reads signals: it is tracked, so the tier follows them).</description></item>
/// </list>
/// A plain value type; building one allocates nothing (the delegates and signals are the caller's).
/// </summary>
public readonly struct TierSource
{
    internal readonly TierSourceKind Kind;
    internal readonly IReadSignal<float>? Slot;
    internal readonly Func<float>? Thunk;

    private TierSource(TierSourceKind kind, IReadSignal<float>? slot, Func<float>? thunk)
    {
        Kind = kind;
        Slot = slot;
        Thunk = thunk;
    }

    /// <summary>The viewport width in DIP (<see cref="Viewport.Size"/>).</summary>
    public static TierSource ViewportWidth => new(TierSourceKind.ViewportWidth, null, null);

    /// <summary>The viewport height in DIP (<see cref="Viewport.Size"/>).</summary>
    public static TierSource ViewportHeight => new(TierSourceKind.ViewportHeight, null, null);

    /// <summary>A measured slot. While the slot reads 0.5 or less (not measured yet) <paramref name="seed"/> supplies the
    /// value, so a first mount resolves in its own frame instead of building at a fixed fallback.</summary>
    public static TierSource Measured(IReadSignal<float> slot, Func<float>? seed = null) => new(TierSourceKind.Measured, slot, seed);

    /// <summary>A supplied value; the thunk is evaluated tracked, so signals it reads re-resolve the tier.</summary>
    public static TierSource Estimate(Func<float> estimate) => new(TierSourceKind.Estimate, null, estimate);
}
