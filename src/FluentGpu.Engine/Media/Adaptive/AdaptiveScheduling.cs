using System;
using System.Collections.Generic;

namespace FluentGpu.Media.Adaptive;

public enum AdaptiveRequestKind : byte { Initialization, Media, Partial }

/// <summary>One scheduler decision. Track and representation identity remain explicit so audio/video/text requests
/// can run concurrently without losing timeline or discontinuity ownership.</summary>
public readonly record struct AdaptiveSegmentRequest(
    string TrackId, string VariantId, AdaptiveRequestKind Kind, AdaptiveSegment Segment, Uri Uri);

/// <summary>Pure adaptive scheduler shared by DASH and HLS. It plans only the missing forward window, includes each
/// selected representation's init segment once, skips declared gaps, and never schedules before the seek position.</summary>
public static class AdaptiveSegmentScheduler
{
    public static IReadOnlyList<AdaptiveSegmentRequest> Plan(
        AdaptiveManifest manifest, TimeSpan position, TimeSpan bufferedEnd, BufferPolicy policy,
        Func<AdaptiveTrackGroup, AdaptiveRepresentation?> select, ISet<string>? initialized = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(select);
        var requests = new List<AdaptiveSegmentRequest>();
        TimeSpan from = bufferedEnd > position ? bufferedEnd : position;
        TimeSpan target = from + policy.TargetForward;

        foreach (var group in manifest.TrackGroups)
        {
            AdaptiveRepresentation? rep = select(group);
            if (rep is null) continue;
            string initKey = group.Id + "\n" + rep.Quality.Id;
            if (rep.Initialization is { } init && (initialized is null || !initialized.Contains(initKey)))
            {
                var initSegment = new AdaptiveSegment(init, -1, TimeSpan.Zero, TimeSpan.Zero);
                requests.Add(new AdaptiveSegmentRequest(group.Id, rep.Quality.Id, AdaptiveRequestKind.Initialization, initSegment, init));
            }

            foreach (var segment in rep.Segments)
            {
                TimeSpan end = segment.Start + segment.Duration;
                if (segment.IsGap || end <= from) continue;
                if (segment.Start >= target) break;
                requests.Add(new AdaptiveSegmentRequest(group.Id, rep.Quality.Id,
                    segment.IsPartial ? AdaptiveRequestKind.Partial : AdaptiveRequestKind.Media, segment, segment.Uri));
            }
        }
        return requests;
    }

    public static TimelineInfo Timeline(AdaptiveManifest manifest, TimeSpan position)
    {
        TimeSpan start = TimeSpan.MaxValue, edge = TimeSpan.Zero;
        foreach (var group in manifest.TrackGroups)
        foreach (var rep in group.Representations)
        foreach (var segment in rep.Segments)
        {
            if (segment.Start < start) start = segment.Start;
            TimeSpan end = segment.Start + segment.Duration;
            if (end > edge) edge = end;
        }
        if (start == TimeSpan.MaxValue) start = TimeSpan.Zero;
        TimeSpan liveOffset = manifest.IsLive && edge > position ? edge - position : TimeSpan.Zero;
        TimeSpan tolerance = manifest.IsLowLatency ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(3);
        return new TimelineInfo(manifest.IsLive, start, edge, edge, liveOffset,
            manifest.IsLive && liveOffset <= tolerance, Array.Empty<MediaChapter>());
    }

    public static BufferingInfo Buffering(BufferingReason reason, TimeSpan ahead, BufferPolicy policy)
    {
        TimeSpan target = reason == BufferingReason.Initial ? policy.InitialPlayback : policy.ResumePlayback;
        double percent = target > TimeSpan.Zero ? Math.Clamp(ahead.TotalSeconds / target.TotalSeconds, 0, 1) : 1;
        return new BufferingInfo(reason, percent, ahead, target, ahead >= target);
    }
}

/// <summary>Allocation-free exponentially-weighted throughput estimator. Samples use payload bits / transfer time;
/// the fast EWMA reacts to drops while the slow EWMA prevents a single burst from driving an unsafe upgrade.</summary>
public sealed class ThroughputEstimator
{
    private double _fastKbps;
    private double _slowKbps;
    public double EstimateKbps => _fastKbps <= 0 ? _slowKbps : _slowKbps <= 0 ? _fastKbps : Math.Min(_fastKbps, _slowKbps);

    public void Reset() { _fastKbps = 0; _slowKbps = 0; }

    public void Add(long payloadBytes, TimeSpan elapsed)
    {
        if (payloadBytes <= 0 || elapsed <= TimeSpan.Zero) return;
        double kbps = payloadBytes * 8.0 / elapsed.TotalSeconds / 1000.0;
        if (!double.IsFinite(kbps) || kbps <= 0) return;
        _fastKbps = _fastKbps <= 0 ? kbps : _fastKbps + 0.35 * (kbps - _fastKbps);
        _slowKbps = _slowKbps <= 0 ? kbps : _slowKbps + 0.08 * (kbps - _slowKbps);
    }
}

/// <summary>Production ABR controller: conservative throughput budget, immediate downshift, buffer-gated upgrade,
/// two-decision upgrade hysteresis, manual pin and bitrate/resolution caps. It preserves the current variant when an
/// upgrade is unsafe instead of oscillating around a bandwidth boundary.</summary>
public sealed class AdaptiveBitrateController : IAbrPolicy
{
    private readonly ThroughputEstimator _throughput = new();
    private int _current;
    private int _upgradeCandidate = -1;
    private byte _upgradeVotes;

    public QualitySelection Selection { get; set; } = QualitySelection.Auto;
    public int MaxBitrate { get; set; } = int.MaxValue;
    public int MaxHeight { get; set; } = int.MaxValue;
    public double SafetyFactor { get; set; } = 0.78;
    public TimeSpan UpgradeBuffer { get; set; } = TimeSpan.FromSeconds(12);
    public double EstimatedKbps => _throughput.EstimateKbps;

    public void RecordDownload(long payloadBytes, TimeSpan elapsed) => _throughput.Add(payloadBytes, elapsed);
    public void Reset() { _throughput.Reset(); _current = 0; _upgradeCandidate = -1; _upgradeVotes = 0; }

    public int Choose(ReadOnlySpan<int> variantBitrates, TimeSpan forwardBuffered, double measuredKbps)
    {
        if (variantBitrates.IsEmpty) return 0;
        if (!Selection.IsAuto && Selection.VariantId is { } id && int.TryParse(id, out int pinned))
            return _current = Math.Clamp(pinned, 0, variantBitrates.Length - 1);

        double estimate = measuredKbps > 0 ? measuredKbps : EstimatedKbps;
        double budgetBps = estimate > 0 ? estimate * 1000.0 * Math.Clamp(SafetyFactor, 0.25, 0.98) : variantBitrates[0];
        int best = 0;
        for (int i = 0; i < variantBitrates.Length; i++)
            if (variantBitrates[i] <= budgetBps && variantBitrates[i] <= MaxBitrate) best = i;

        _current = Math.Clamp(_current, 0, variantBitrates.Length - 1);
        if (best < _current) { _upgradeCandidate = -1; _upgradeVotes = 0; return _current = best; }
        if (best == _current || forwardBuffered < UpgradeBuffer)
        { _upgradeCandidate = -1; _upgradeVotes = 0; return _current; }
        if (_upgradeCandidate != best) { _upgradeCandidate = best; _upgradeVotes = 1; return _current; }
        if (++_upgradeVotes < 2) return _current;
        _upgradeCandidate = -1; _upgradeVotes = 0;
        return _current = best;
    }

    public int Choose(IReadOnlyList<QualityVariant> variants, TimeSpan forwardBuffered)
    {
        if (variants.Count == 0) return 0;
        Span<int> bitrates = variants.Count <= 64 ? stackalloc int[variants.Count] : new int[variants.Count];
        Span<int> indices = variants.Count <= 64 ? stackalloc int[variants.Count] : new int[variants.Count];
        int allowed = 0;
        for (int i = 0; i < variants.Count; i++)
        {
            if (variants[i].Resolution.Height > MaxHeight) continue;
            bitrates[allowed++] = variants[i].Bitrate;
            indices[allowed - 1] = i;
        }
        if (allowed == 0) return 0;
        int selected = Choose(bitrates[..allowed], forwardBuffered, EstimatedKbps);
        return indices[Math.Clamp(selected, 0, allowed - 1)];
    }
}
