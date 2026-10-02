using System;
using FluentGpu.Media;

namespace FluentGpu.Windows.Wasapi;

/// <summary>
/// The Windows composition-root helper (spec §7, §13) that wires the portable <see cref="PcmAudioPlayer"/> to the WASAPI
/// device leaf: it probes the default endpoint's mix format so the graph runs at the DEVICE rate (fixed <c>f32</c>/stereo),
/// then builds a backend whose endpoint factory opens a <see cref="WasapiAudioDevice"/> per session and whose single
/// control-thread feeder pumps it (M2 single-thread-correct — the M4 flip swaps that feeder for the MMCSS RT thread).
/// Register the returned backend on the router for <see cref="MediaKind.PcmAudio"/>. On-box only.
/// </summary>
public static class WasapiPcm
{
    /// <summary>Read active render endpoints and the current console default from Windows, off the audio thread.
    /// An unavailable audio service returns an empty list; this never substitutes a remembered preference.</summary>
    public static WasapiEndpointInfo[] EnumerateEndpoints() => WasapiEndpoints.Enumerate();

    /// <summary>Probe the default render endpoint's shared-mode mix format (rate/stereo), or a 48k/stereo fallback if no
    /// device is available.</summary>
    public static MixFormat ProbeFormat()
    {
        using var probe = new WasapiAudioDevice(new MixFormat(48000, 2));
        if (probe.IsReady) return probe.Format;
        // The device never came up, so we can't know its clock — fall back to 48k, but say so loudly: a wrong guess here is
        // a second route to a decoder/hardware rate divergence (slowed/pitched playback). DiagSink is optional (dev-only).
        WasapiAudioDevice.DiagSink?.Invoke("probe FAILED, falling back to 48000 - device rate unknown");
        return new MixFormat(48000, 2);
    }

    /// <summary>Build the WASAPI-backed PCM backend (spec §7). <paramref name="effects"/> supplies the live
    /// EQ/normalization signals. When <paramref name="useRtFeed"/> (M4, spec §7.9) the render/consume moves onto an MMCSS
    /// "Pro Audio" RT feed thread (decode on a worker via a lock-free ring, clock poll + Position on a non-RT tick), and a
    /// follow-default <see cref="AudioDeviceController"/> rebuilds ONLY the sink on a device change; otherwise it drives the
    /// M2 single control-thread feeder. On-box only.</summary>
    public static PcmAudioPlayer CreateBackend(IAudioEffects? effects = null, int maxBlock = 1024, bool useRtFeed = true,
        Func<MixFormat, IAudioDecoder>? decoderFactory = null)
    {
        var format = ProbeFormat();
        PowerThrottling.OptOut();   // F4 — before any producer/clock thread exists; covers the !useRtFeed path too. Idempotent, once per process

        // ONE characteristics INSTANCE and ONE RingSizing for the whole backend (no statics — V-PE34): the feed registers its RT
        // thread (Pro Audio) and its clock/producer threads (Audio) through `rt`, and PcmAudioPlayer hands the same two objects to
        // every prepared/seek ring it builds, so the live voice and a prepared voice hold the same time-domain cushion (D8).
        var rt = new MmcssProAudio();
        var sizing = new RingSizing(BlockMs: 10.0, AheadMs: 2000.0, RingMs: 4000.0, KeepBehindMs: 1000.0);   // D8: 2 s ahead + 1 s kept behind

        if (!useRtFeed)
            return new PcmAudioPlayer(format, fmt => new WasapiAudioDevice(fmt), effects, maxBlock, driveWithOwnThread: true,
                decoderFactory: decoderFactory, rt: rt, ringSizing: sizing);

        return new PcmAudioPlayer(
            format,
            endpointFactory: fmt => new WasapiAudioDevice(fmt),
            effects: effects,
            maxBlock: maxBlock,
            driveWithOwnThread: false,              // the RT feed drives — NOT the M2 single feeder
            decoderFactory: decoderFactory,
            rt: rt,
            ringSizing: sizing,
            onSessionCreated: session =>
            {
                // Attach the RT feed (MMCSS Pro-Audio) BEFORE SetVoice so the voice is decode↔RT ring-wrapped (spec §7.9).
                // Sized in TIME, not frames, against the OPENED endpoint rate: a fixed frame count silently shrinks the
                // decode-ahead cushion at higher rates (4096 frames is 85 ms at 48 kHz but only 21 ms at 192 kHz — well
                // under the ~100 ms WASAPI device buffer a stall relies on). Named `sampleRate:`/`sizing:` to pick the
                // RingSizing ctor unambiguously (the ms-sized and frame-sized overloads also accept a bare (session, int) call).
                var feed = new AudioFeedThread(session, sampleRate: session.Format.SampleRate, rt: rt, sizing: sizing);
                var watcher = new MmDeviceWatcher();
                // The endpoint factory reads session.Format at EACH rebuild, not the probe format captured once above
                // (spec §7.9 Fix 3): after a soft reload/RebuildSink the session's live rate can differ from the format
                // this backend was originally probed at, and a follow-default rebuild opening the fresh default device
                // should hand it the CURRENT rate, not a stale one from process start.
                var controller = new AudioDeviceController(session, () => new WasapiAudioDevice(session.Format), watcher, feed);
                session.RegisterDisposable(controller);
                session.RegisterDisposable(watcher);
                controller.MarkRunning();
                controller.Start();
                feed.Start();
            });
    }

    /// <summary>Create the follow-default device watcher (spec §7.9).</summary>
    public static MmDeviceWatcher CreateDeviceWatcher() => new();
}
