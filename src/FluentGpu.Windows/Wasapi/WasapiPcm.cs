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
    /// <summary>Probe the default render endpoint's shared-mode mix format (rate/stereo), or a 48k/stereo fallback if no
    /// device is available.</summary>
    public static MixFormat ProbeFormat()
    {
        using var probe = new WasapiAudioDevice(new MixFormat(48000, 2));
        return probe.IsReady ? probe.Format : new MixFormat(48000, 2);
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
        if (!useRtFeed)
            return new PcmAudioPlayer(format, fmt => new WasapiAudioDevice(fmt), effects, maxBlock, driveWithOwnThread: true,
                decoderFactory: decoderFactory);

        return new PcmAudioPlayer(
            format,
            endpointFactory: fmt => new WasapiAudioDevice(fmt),
            effects: effects,
            maxBlock: maxBlock,
            driveWithOwnThread: false,              // the RT feed drives — NOT the M2 single feeder
            decoderFactory: decoderFactory,
            onSessionCreated: session =>
            {
                // Attach the RT feed (MMCSS Pro-Audio) BEFORE SetVoice so the voice is decode↔RT ring-wrapped (spec §7.9).
                var feed = new AudioFeedThread(session, blockFrames: 480, rt: new MmcssProAudio());
                var watcher = new MmDeviceWatcher();
                var controller = new AudioDeviceController(session, () => new WasapiAudioDevice(format), watcher, feed);
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
