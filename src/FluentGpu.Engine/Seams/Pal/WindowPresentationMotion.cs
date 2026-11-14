namespace FluentGpu.Pal;

/// <summary>The curve of one channel of a <see cref="WindowPresentationMotion"/>.</summary>
public enum PresentationEase : byte
{
    /// <summary>Cubic ease-out (3u − 3u² + u³): leaves at speed and lands gently. An arrival.</summary>
    Decelerate,
    /// <summary>Smoothstep (3u² − 2u³): starts and ends at rest. A departure, a fade that must neither start nor stop hard.</summary>
    Smooth,
}

/// <summary>A WINDOW-LEVEL presentation motion: the whole composited window (its UI swap chain and its video layer beneath)
/// fades and scales about an origin ON THE COMPOSITOR, from one pose to another, while the host keeps laying out and presenting
/// at the window's real size. Nothing relayouts, nothing moves the window: the compositor interpolates (DirectComposition for the
/// UI target, Windows.UI.Composition for the video target), so a frame the app is late with never stalls the motion.
/// <para>Durations are in the engine's motion time (<c>EngineSwitches.AnimScale</c> stretches them like every engine animation).
/// A channel with a duration of 0 is set to its <c>To</c> value at once: a motion with both durations 0 is a static pose (an
/// entrance's starting hold, applied before the window is first shown). <see cref="Identity"/> removes the effect.</para>
/// <para>Reduced motion is the caller's VALUE, never an early return: pass equal scales and a shorter fade.</para>
/// <para>What the system draws around a window (its DWM frame, shadow and Mica / acrylic backdrop) is not in the app's composition
/// tree and keeps the system's own show and hide transitions.</para></summary>
/// <param name="OriginX">The scale origin, as a fraction of the window's client width (0 = left edge).</param>
/// <param name="OriginY">The scale origin, as a fraction of the window's client height (0 = top edge).</param>
public readonly record struct WindowPresentationMotion(
    float FromOpacity, float ToOpacity, float OpacityMs, PresentationEase OpacityEase,
    float FromScale, float ToScale, float ScaleMs, PresentationEase ScaleEase,
    float OriginX = 0.5f, float OriginY = 0.5f, float DelayMs = 0f)
{
    /// <summary>No effect: full opacity, no transform (the compositor drops the effect and the transform).</summary>
    public static WindowPresentationMotion Identity => new(1f, 1f, 0f, PresentationEase.Smooth, 1f, 1f, 0f, PresentationEase.Smooth);

    /// <summary>A static pose (no motion): what an entrance starts from, set before the window is first shown.</summary>
    public static WindowPresentationMotion Hold(float opacity, float scale, float originX, float originY)
        => new(opacity, opacity, 0f, PresentationEase.Smooth, scale, scale, 0f, PresentationEase.Smooth, originX, originY);

    /// <summary>True for <see cref="Identity"/> (and any motion that ends there at once).</summary>
    public bool IsIdentity => ToOpacity >= 1f && ToScale == 1f && OpacityMs <= 0f && ScaleMs <= 0f;

    /// <summary>When the motion has landed (ms of motion time from its start, delay included).</summary>
    public float EndMs => DelayMs + MathF.Max(OpacityMs, ScaleMs);

    /// <summary>The channel's value <paramref name="ms"/> into the motion (delay included): what the compositor shows then.</summary>
    public static float Sample(float from, float to, float durationMs, PresentationEase ease, float delayMs, float ms)
    {
        if (durationMs <= 0f) return to;
        float u = Math.Clamp((ms - delayMs) / durationMs, 0f, 1f);
        float e = ease == PresentationEase.Decelerate ? 1f - (1f - u) * (1f - u) * (1f - u) : u * u * (3f - 2f * u);
        return from + (to - from) * e;
    }
}
