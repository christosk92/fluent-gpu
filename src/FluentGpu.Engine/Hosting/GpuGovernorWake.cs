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
}
