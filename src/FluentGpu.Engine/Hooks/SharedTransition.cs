namespace FluentGpu.Hooks;

/// <summary>
/// Ambient access to the host's connected-animation / shared-element capture (backdrop-effects-animation.md §5.4).
/// A component reads it with <c>UseContext(SharedTransition.Begin)</c> and calls <c>begin("album:"+uri)</c> from a
/// navigation click handler, JUST BEFORE writing the route — that snapshots the live source node tagged with the
/// same key (its <see cref="FluentGpu.Dsl.Element.MorphId"/>) so its art flies to the like-tagged dest that mounts on
/// the new route. Page navigation (a card to its page and Back) uses <see cref="BeginNav"/> instead.
/// <para>Default (no host / headless tests) is <see langword="null"/>; callers null-check before invoking.</para>
/// </summary>
public static class SharedTransition
{
    /// <summary>Host-provided forward-capture trigger: <c>begin(key)</c> snapshots the live source tagged with
    /// <c>key</c> for an imminent navigation. Null when no host published one (headless/test).</summary>
    public static readonly Context<System.Action<string>?> Begin = new(null);

    /// <summary>Host-provided request-local capture trigger. Use this for an in-page shared-layout morph that needs its
    /// own curve and interruption policy without changing the app-wide route-fly token.</summary>
    public static readonly Context<System.Action<FluentGpu.Animation.ConnectedTransitionRequest>?> BeginConfigured = new(null);

    /// <summary>Host-provided setter for the connected-animation fly MOTION token (spring OR an eased curve + duration),
    /// so an app can surface a LIVE switcher to A/B the shared-element feel. Null when no host published one.</summary>
    public static readonly Context<System.Action<FluentGpu.Animation.ConnectedMotion>?> SetMotion = new(null);

    /// <summary>Host-provided connected NAVIGATION flight (<see cref="FluentGpu.Animation.ConnectedAnimation.BeginNav"/>): the shell calls it on
    /// every route commit, before the route is written, with the shared-element key of the page being opened (or left, for Back), an
    /// empty key when it has none. True when a flyer is up (a card's cover flies into the page's hero, or back): the shell then
    /// plays a quiet page swap under it. Null when no host published one (headless/test).</summary>
    public static readonly Context<System.Func<FluentGpu.Animation.ConnectedNavRequest, bool>?> BeginNav = new(null);

    /// <summary>Host-provided origin of a navigation that flies nothing (<see cref="FluentGpu.Animation.ConnectedAnimation.NavOriginRect"/>): the
    /// rect, window DIP, of the element the user activated (remembered under the page's key), or of the element remembered for a key (Back,
    /// mouse Forward); empty for none. The shell grows the page out of it. Null when no host published one (headless/test).</summary>
    public static readonly Context<System.Func<FluentGpu.Animation.ConnectedOriginRequest, FluentGpu.Foundation.RectF>?> NavOriginRect = new(null);
}
