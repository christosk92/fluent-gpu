namespace FluentGpu.Dsl;

/// <summary>
/// The MATERIAL policy — one host-set value answering "may a frosted material actually be composited right now?", read
/// where acrylic is emitted rather than branched on in authoring code. The exact shape of <see cref="Motion"/>'s
/// reduced-motion value, and for the same reason: an authoring path that tested it would be a hook-order hazard and
/// would scatter the decision across every surface that wants frost.
///
/// <para>WinUI's <c>AcrylicBrush</c> stops compositing and cross-fades to its <c>FallbackColor</c> under three
/// independent conditions (AcrylicBrush.cpp / <c>MaterialHelper</c>): the user turned transparency off
/// (<c>UISettings.AdvancedEffectsEnabled</c>), the device reports the effects would be slow
/// (<c>CompositionCapabilities.AreEffectsFast()</c>), or the machine is in energy-saver mode. Each maps to one field
/// below, all defaulting to the permissive answer so a backend that cannot report one never silently disables frost.</para>
///
/// <para>The fallback branch costs nothing to author: every frosted surface in the kit already paints
/// <c>Tok.Acrylic*.Fallback</c> as its RESTING fill under the acrylic layer (FlyoutSurface authors it on both shapes it
/// builds), so "no acrylic" is literally "do not emit the PushLayer" — the plate underneath is already WinUI's
/// FallbackColor. <see cref="FluentGpu.Render.SceneRecorder"/> is the one place that consults this.</para>
///
/// <para>Plain statics, written on the UI thread from the platform layer (Win32: at startup and from every
/// <c>WM_SETTINGCHANGE</c>) and read during recording, exactly like <see cref="Motion.ReducedMotion"/>.</para>
/// </summary>
public static class Materials
{
    /// <summary>The user's Settings ▸ Personalization ▸ Colors ▸ "Transparency effects" switch — WinUI's
    /// <c>UISettings.AdvancedEffectsEnabled</c> (Win32: <c>HKCU\…\Themes\Personalize\EnableTransparency</c>). Host-set;
    /// defaults to TRUE so an unreadable value never costs the user their materials.</summary>
    public static bool AdvancedEffectsEnabled = true;

    /// <summary>The compositor's own verdict that effects on this device are fast enough to be worth it — WinUI's
    /// <c>CompositionCapabilities.AreEffectsFast()</c>. Host-set; defaults to TRUE (a backend that cannot ask says
    /// "fine", never "slow").</summary>
    public static bool EffectsAreFast = true;

    /// <summary>Energy saver is on. Host- or app-set from <c>FluentGpu.WindowsApi.Power.PowerSession.ReadPower()</c>
    /// (<c>PowerStatus.EnergySaver</c>) — that pillar sits beside the PAL rather than under it, so the composition root
    /// owns this one write (and re-writes it on the <c>PowerSession</c> suspend/resume edges). Defaults to FALSE.</summary>
    public static bool EnergySaver;

    /// <summary>The single answer the renderer reads: composite frosted materials only when the user allows them, the
    /// device can afford them, and the machine is not conserving power. False ⇒ every acrylic surface resolves to its
    /// authored FallbackColor fill and no blur is paid at all.</summary>
    public static bool AcrylicEnabled => AdvancedEffectsEnabled && EffectsAreFast && !EnergySaver;
}
