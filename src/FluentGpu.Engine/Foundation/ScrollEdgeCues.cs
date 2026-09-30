namespace FluentGpu.Foundation;

/// <summary>
/// Per-scroll-surface edge-cue mode (controls.md §8.3). A scrolling viewport FEATHERS any edge that has more content past
/// it, so a clipped list signals there is more below the fold — the affordance macOS's hidden scrollbars and flush
/// clipping remove ("a clipped list looks finished"). The fade IS the viewport's analytic edge feather (the
/// <c>AutoEdgeFade</c> route, gpu-renderer.md §13.1e): the content dissolves into whatever lies behind the viewport, at the
/// edge's true position — there is no painted band and no guessed surface colour. <see cref="Auto"/> resolves to
/// <see cref="ScrollEdgeCuesDefaults.Default"/> (ON, fade-only) so one assignment flips the whole app; <see cref="None"/>
/// opts a surface out; <see cref="FadeAndChevron"/> adds a small directional chevron in the band.
/// </summary>
public enum ScrollEdgeCues : byte
{
    /// <summary>Use the app default (<see cref="ScrollEdgeCuesDefaults.Default"/>). The init-default for every surface.</summary>
    Auto = 0,
    /// <summary>No edge cue on this surface.</summary>
    None = 1,
    /// <summary>The analytic edge feather only (no chevron).</summary>
    Fade = 2,
    /// <summary>The fade plus a small directional chevron centred in the band.</summary>
    FadeAndChevron = 3,
}

/// <summary>
/// The app-wide default that <see cref="ScrollEdgeCues.Auto"/> resolves to. Defaults to <see cref="ScrollEdgeCues.Fade"/>
/// (cues ON, fade-only) — so scrolling surfaces get the affordance with no wiring. Assign once at startup to change the
/// default for every <see cref="ScrollEdgeCues.Auto"/> surface at once.
/// </summary>
public static class ScrollEdgeCuesDefaults
{
    public static ScrollEdgeCues Default = ScrollEdgeCues.Fade;
}

/// <summary>What a scroller's edge props resolve to (<see cref="ScrollEdgeCueResolver.Resolve"/>): whether its edges are
/// feathered (and how wide), and whether it draws the edge chevrons.</summary>
public readonly record struct ScrollEdgeResolution(bool AutoEdgeFade, float AutoEdgeFadeBand, bool Chevron);

/// <summary>
/// The ONE resolution of a <c>ScrollEl</c> / <c>VirtualListEl</c>'s <c>EdgeCues</c> + <c>AutoEdgeFade</c> +
/// <c>AutoEdgeFadeBand</c> + authored <c>EdgeFade</c> props (the reconciler calls it; pure, so it is unit-tested). An edge
/// cue's fade is the analytic edge feather: <see cref="ScrollEdgeCues.Fade"/> (and <see cref="ScrollEdgeCues.Auto"/> while
/// the app default is Fade) turns <c>AutoEdgeFade</c> on with the standard <see cref="DefaultBandDip"/> band — unless the
/// element authored a fade of its own (an explicit <c>EdgeFade</c> spec wins). <see cref="ScrollEdgeCues.None"/> leaves an
/// explicit <c>AutoEdgeFade = true</c> in place.
/// </summary>
public static class ScrollEdgeCueResolver
{
    /// <summary>The standard auto-edge-fade feather width (DIP): every <c>AutoEdgeFade</c> surface, and every Fade edge cue,
    /// that declares no band of its own.</summary>
    public const float DefaultBandDip = 40f;

    public static ScrollEdgeResolution Resolve(ScrollEdgeCues cues, bool authoredEdgeFade, bool autoEdgeFade, float autoEdgeFadeBand)
    {
        ScrollEdgeCues eff = cues == ScrollEdgeCues.Auto ? ScrollEdgeCuesDefaults.Default : cues;
        bool cueFade = eff is ScrollEdgeCues.Fade or ScrollEdgeCues.FadeAndChevron;
        bool fade = autoEdgeFade || (cueFade && !authoredEdgeFade);
        float band = fade ? (autoEdgeFadeBand > 0f ? autoEdgeFadeBand : DefaultBandDip) : 0f;
        return new ScrollEdgeResolution(fade, band, eff == ScrollEdgeCues.FadeAndChevron);
    }
}
