namespace FluentGpu.Foundation;

/// <summary>
/// The placeholder→image reveal transition (the cross-fade) — spec'd with the engine's shared motion vocabulary (a
/// duration in ms + an <see cref="Easing"/> curve) so image reveals animate exactly like every other control. Override
/// per-image via <c>ImageEl.Transition</c>, or change the app-wide <see cref="Default"/> once at startup. Set
/// <see cref="DurationMs"/> = 0 (or use <see cref="None"/>) to DISABLE the fade — the image then appears instantly.
/// </summary>
public readonly record struct ImageTransition(float DurationMs, Easing Easing)
{
    /// <summary>App-wide default reveal (settable at startup) — a WinUI-style decelerate fade (~220ms).</summary>
    public static ImageTransition Default { get; set; } = new(220f, Easing.FluentDecelerate);

    /// <summary>No transition: the image snaps in the instant its texture is resident.</summary>
    public static ImageTransition None => new(0f, Easing.Linear);

    /// <summary>A custom fade: <paramref name="durationMs"/> over <paramref name="easing"/>.</summary>
    public static ImageTransition Fade(float durationMs, Easing easing = Easing.FluentDecelerate) => new(durationMs, easing);

    public bool Enabled => DurationMs > 0f;

    /// <summary>Eased reveal progress 0..1 for an elapsed time since the texture appeared.</summary>
    public float Progress(float elapsedMs)
    {
        if (!Enabled) return 1f;
        float t = elapsedMs / DurationMs;
        if (t <= 0f) return 0f;
        if (t >= 1f) return 1f;
        return Easings.Ease(Easing, t);
    }
}
