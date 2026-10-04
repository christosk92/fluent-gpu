namespace FluentGpu.Media.Windows;

/// <summary>
/// Whether Media Foundation's answers describe a LIVE source — a pure function so the decision is testable without an
/// engine (the fakes mirror the published <c>LiveSource</c> flag and cannot exercise the real latch).
/// <para><c>IMFMediaEngine::GetDuration</c> is NaN while no media data is available (before metadata, and after a
/// detach), exactly like HTML <c>video.duration</c>, so NaN means "not known yet" and never "live"; an unbounded source
/// reports +Infinity. Nothing is judged before <c>LOADEDMETADATA</c> AND <c>HAVE_METADATA</c> (the event bit is set on an
/// MF worker, the ready state is read synchronously — requiring both closes a late event from the previous source).
/// Post-metadata NaN is a deliberate policy choice: NOT live (the engine logs it once per source to learn whether any
/// MF HLS build answers that way; <c>MFMEDIASOURCE_IS_LIVE</c> still catches those that set the characteristic).</para>
/// </summary>
internal static class EngineLivenessRule
{
    /// <summary>MF_MEDIA_ENGINE_READY_HAVE_METADATA.</summary>
    internal const uint HaveMetadata = 1;

    internal static bool IsLive(bool metadataLoaded, uint readyState, double duration, bool charIsLive)
    {
        if (!metadataLoaded || readyState < HaveMetadata) return false;
        return charIsLive || double.IsPositiveInfinity(duration);
    }
}
