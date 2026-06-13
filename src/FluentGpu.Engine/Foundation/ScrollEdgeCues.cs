namespace FluentGpu.Foundation;

/// <summary>
/// Per-scroll-surface edge-cue mode (controls.md §8.3). A scrolling viewport draws a surface-colour gradient fade at any
/// edge that has MORE content past it, so a clipped list signals there is more below the fold — the affordance macOS's
/// hidden scrollbars and flush clipping remove ("a clipped list looks finished"). <see cref="Auto"/> resolves to
/// <see cref="ScrollEdgeCuesDefaults.Default"/> (ON, fade-only) so one assignment flips the whole app; <see cref="None"/>
/// opts a surface out; <see cref="FadeAndChevron"/> adds a small directional chevron centred in the band.
/// </summary>
public enum ScrollEdgeCues : byte
{
    /// <summary>Use the app default (<see cref="ScrollEdgeCuesDefaults.Default"/>). The init-default for every surface.</summary>
    Auto = 0,
    /// <summary>No edge cue on this surface.</summary>
    None = 1,
    /// <summary>The surface-colour gradient fade only (no chevron).</summary>
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
