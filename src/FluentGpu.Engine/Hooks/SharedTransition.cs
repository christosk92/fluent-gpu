namespace FluentGpu.Hooks;

/// <summary>
/// Ambient access to the host's connected-animation / shared-element capture (backdrop-effects-animation.md §5.4).
/// A component reads it with <c>UseContext(SharedTransition.Begin)</c> and calls <c>begin("album:"+uri)</c> from a
/// navigation click handler, JUST BEFORE writing the route — that snapshots the live source node tagged with the
/// same key (its <see cref="FluentGpu.Dsl.Element.MorphId"/>) so its art flies to the like-tagged dest that mounts on
/// the new route. The reverse direction (Back) needs no call: a tagged node captures itself on park/unmount.
/// <para>Default (no host / headless tests) is <see langword="null"/>; callers null-check before invoking.</para>
/// </summary>
public static class SharedTransition
{
    /// <summary>Host-provided forward-capture trigger: <c>begin(key)</c> snapshots the live source tagged with
    /// <c>key</c> for an imminent navigation. Null when no host published one (headless/test).</summary>
    public static readonly Context<System.Action<string>?> Begin = new(null);

    /// <summary>Host-provided setter for the connected-animation fly MOTION token (spring OR an eased curve + duration),
    /// so an app can surface a LIVE switcher to A/B the shared-element feel. Null when no host published one.</summary>
    public static readonly Context<System.Action<FluentGpu.Animation.ConnectedMotion>?> SetMotion = new(null);
}
