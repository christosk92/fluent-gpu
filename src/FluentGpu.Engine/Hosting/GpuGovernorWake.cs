namespace FluentGpu.Hosting;

/// <summary>
/// Which frames the adaptive-GPU governor may pace (<c>AppHost.RecommendedWaitMsCore</c>'s AdaptiveGpu branch). The
/// governor is MEASUREMENT, not policy: when sampled on-GPU execution says the panel rate is unsustainable it paces
/// ambient work to a steady cadence — but a frame carrying any bit below is a genuine interaction and is never paced
/// (pacing it would add input/scroll latency).
/// </summary>
internal static class GpuGovernorWake
{
    /// <summary>The governor NEVER paces these: genuine interactions + an explicit UI frame-clock poller (for example the
    /// compositor-bound playback playhead). It DOES pace art-reveal crossfades / one-shot transitions / loops when
    /// GPU-bound (a 60Hz crossfade is imperceptible, and the GPU can't do better than ~60 at that size anyway) — the
    /// Image* bits are deliberately absent so the governor reliably engages during maximized playback where they stay
    /// set.
    /// <para>NOT PopupAnim. A popup open/close is the single most expensive frame class the engine produces (an acrylic
    /// plate inside a fading opacity group = a guaranteed backdrop-surface miss every frame of the fade). Exempting it
    /// from the GPU governor is backwards: that is exactly
    /// the frame class the governor exists to pace when the GPU cannot sustain the panel rate. Its own fade rows carry
    /// their cadence (Cadence.Display for a popup reveal), which is what keeps the fade crisp.</para>
    /// <para>ScrollProducer: a frame-aligned scroll producer is live (DirectManipulation engaged/pending, or a hi-res
    /// wheel gesture). Those frames carry NO ScrollAnim bit while the finger rests or before the first plan moves — the
    /// touchpad engage window — and pacing them to the governor's cadence delays the pump of the very next contact
    /// sample by up to a governor period.</para></summary>
    internal const WakeReasons NeverPace =
        WakeReasons.ScrollAnim | WakeReasons.ScrollProducer | WakeReasons.Repeat |
        WakeReasons.DragActive | WakeReasons.DragDropWork | WakeReasons.GestureHold | WakeReasons.TouchPress |
        WakeReasons.FrameClockPoller;

    /// <summary>May the governor pace a frame whose wake mask is <paramref name="reasons"/>?</summary>
    internal static bool MayPace(WakeReasons reasons) => (reasons & NeverPace) == 0;

    /// <summary>Engage when the smoothed GPU execution reaches this fraction of the display period (0.9: a frame that costs
    /// 90 % of a refresh is one scheduling hiccup from missing every vblank). Period-relative like
    /// <c>PresentQueueDepthPolicy</c> - an absolute millisecond figure is blind to the panel: 10 ms is 0.6 of a 60 Hz period
    /// but 1.2 of a 120 Hz one.</summary>
    internal const double EngageFraction = 0.9;
    /// <summary>Release when it falls to this fraction (hysteresis: a frame hovering at the threshold must not chatter).</summary>
    internal const double ReleaseFraction = 0.7;
    /// <summary>Engage fraction on a weak GPU tier while a video surface is live. The sample is the UI command list's own
    /// timestamp pair, so the MF video processor and DWM composition that share the same UMA GPU never enter the EMA; the
    /// same reading therefore sits further below the true load there, and the threshold is lowered to compensate.</summary>
    internal const double WeakVideoEngageFraction = 0.75;
    /// <summary>Release fraction paired with <see cref="WeakVideoEngageFraction"/>.</summary>
    internal const double WeakVideoReleaseFraction = 0.55;

    /// <summary>The governor's live engage/release thresholds (ms) for a display of <paramref name="refreshMs"/>:
    /// <see cref="EngageFraction"/> / <see cref="ReleaseFraction"/> of the period (the weak-with-video pair when
    /// <paramref name="weakWithLiveVideo"/>), each capped at the absolute ceiling the governor always had
    /// (<paramref name="engageCeilingMs"/> / <paramref name="releaseCeilingMs"/>, 10 / 8 ms). The ceiling is what keeps this
    /// strictly MORE responsive than the fixed figures: a 60 Hz period (16.7 ms) would otherwise move the engage point from
    /// 10 ms to 15 ms and stop pacing the ~14 ms maximised-window frames the governor was built for, while a 120 Hz period
    /// (8.33 ms) engages at 7.5 ms instead of never. An unknown period (<c>refreshMs</c> not positive) returns the ceilings.
    /// Release stays strictly below engage in every case.</summary>
    internal static void Thresholds(double refreshMs, bool weakWithLiveVideo, double engageCeilingMs, double releaseCeilingMs,
                                    out double engageMs, out double releaseMs)
    {
        engageMs = engageCeilingMs;
        releaseMs = releaseCeilingMs;
        if (!(refreshMs > 0.0)) return;
        double engage = refreshMs * (weakWithLiveVideo ? WeakVideoEngageFraction : EngageFraction);
        double release = refreshMs * (weakWithLiveVideo ? WeakVideoReleaseFraction : ReleaseFraction);
        if (engage < engageMs) engageMs = engage;
        if (release < releaseMs) releaseMs = release;
    }

    /// <summary><see cref="Thresholds(double, bool, double, double, out double, out double)"/> with the governor's standard
    /// ceilings (<c>AppHost.GpuGovernorEngageMs</c> / <c>GpuGovernorReleaseMs</c>).</summary>
    internal static void Thresholds(double refreshMs, bool weakWithLiveVideo, out double engageMs, out double releaseMs)
        => Thresholds(refreshMs, weakWithLiveVideo, AppHost.GpuGovernorEngageMs, AppHost.GpuGovernorReleaseMs,
                      out engageMs, out releaseMs);
}
