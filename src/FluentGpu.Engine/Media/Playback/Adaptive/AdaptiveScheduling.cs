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

/// <summary>Allocation-free exponentially-weighted throughput estimator. Samples use payload bits / transfer time; the
/// fast EWMA reacts to drops while the slow EWMA prevents a single burst from driving an unsafe upgrade.
/// <para>Two rules keep the estimate honest. (1) A sample smaller than <see cref="MinSampleBytes"/> or shorter than
/// <see cref="MinSampleMs"/> is round-trip/scheduler noise, not throughput, and is DISCARDED — a 12 KB range request
/// that completes in 40 ms "measures" whatever the RTT happened to be, not the link. (2) The estimator starts at
/// <see cref="DefaultSeedKbps"/> (2 Mbps) and NEVER at the bottom rung: seeding low is self-reinforcing, because a
/// 240p rung transfers so little per segment that its own samples can never justify climbing off it.</para></summary>
public sealed class ThroughputEstimator
{
    /// <summary>The startup prior: 2 Mbps. REPLACED outright (not blended) by the first accepted sample, so a fast
    /// link is not dragged down for a dozen segments by a conservative default.</summary>
    public const double DefaultSeedKbps = 2_000.0;
    /// <summary>Payload floor for an accepted sample (64 KB). Below it the measurement is RTT, not bandwidth.</summary>
    public const long MinSampleBytes = 64 * 1024;
    /// <summary>Transfer-time floor for an accepted sample (200 ms).</summary>
    public const double MinSampleMs = 200.0;

    private double _fastKbps = DefaultSeedKbps;
    private double _slowKbps = DefaultSeedKbps;
    private int _accepted;

    /// <summary>The reactive EWMA — reacts to drops.</summary>
    public double FastKbps => _fastKbps;
    /// <summary>The conservative EWMA — keeps a single burst from driving an unsafe upgrade.</summary>
    public double SlowKbps => _slowKbps;
    /// <summary>How many real samples have been accepted (0 ⇒ the estimate is still the seed).</summary>
    public int AcceptedSamples => _accepted;
    /// <summary>The conservative estimate the scheduler budgets against. Never zero — see <see cref="DefaultSeedKbps"/>.</summary>
    public double EstimateKbps => Math.Min(_fastKbps, _slowKbps);

    /// <summary>Return to the 2 Mbps prior. For a NEW source only — a SEEK must keep the history, because the network
    /// did not change when the user dragged the scrubber.</summary>
    public void Reset() { _fastKbps = DefaultSeedKbps; _slowKbps = DefaultSeedKbps; _accepted = 0; }

    /// <summary>Fold one transfer sample in. Returns false when the sample was discarded as noise.</summary>
    public bool Add(long payloadBytes, TimeSpan elapsed)
    {
        if (payloadBytes < MinSampleBytes || elapsed.TotalMilliseconds < MinSampleMs) return false;
        double kbps = payloadBytes * 8.0 / elapsed.TotalSeconds / 1000.0;
        if (!double.IsFinite(kbps) || kbps <= 0) return false;
        if (_accepted == 0) { _fastKbps = kbps; _slowKbps = kbps; }   // the seed is a prior, not a measurement
        else { _fastKbps += 0.35 * (kbps - _fastKbps); _slowKbps += 0.08 * (kbps - _slowKbps); }
        _accepted++;
        return true;
    }
}

/// <summary>Why <see cref="AdaptiveBitrateController"/> landed on the rung it did. Carried into the always-on
/// per-decision diagnostic line, so a field log explains an "Auto · 240p" without needing a repro.</summary>
public enum AbrDecisionReason : byte
{
    /// <summary>Held the current rung — nothing indicated a change.</summary>
    Hold,
    /// <summary>The throughput budget moved the rung (up past the climb gate, or down past the sustain gate).</summary>
    Throughput,
    /// <summary>The forward buffer was below <see cref="AdaptiveBitrateController.UpgradeBuffer"/>, so no climb.</summary>
    Buffer,
    /// <summary>A forced probe stepped up one rung to break the low-rendition feedback loop.</summary>
    ForcedProbe,
    /// <summary>A manual pin selected the rung.</summary>
    Pinned,
    /// <summary>Every rung exceeded the height cap; the SMALLEST rung at/above the cap was taken.</summary>
    Capped,
}

/// <summary>Production ABR controller: conservative throughput budget, immediate downshift, buffer-gated upgrade, an
/// asymmetric climb/sustain hysteresis band, a forced probe that breaks the low-rendition feedback loop, plus a manual
/// pin and bitrate/resolution caps.</summary>
public sealed class AdaptiveBitrateController : IAbrPolicy
{
    private readonly ThroughputEstimator _throughput = new();
    private int _current;
    private int _upgradeCandidate = -1;
    private byte _upgradeVotes;
    private byte _steadySegments;
    private bool _climbedOnce;
    private bool _probeInFlight;
    private byte _probeFailures;
    private AbrDecisionReason _reason = AbrDecisionReason.Hold;

    /// <summary>Automatic, or a manual pin.</summary>
    public QualitySelection Selection { get; set; } = QualitySelection.Auto;
    /// <summary>Hard bitrate ceiling (bits/s).</summary>
    public int MaxBitrate { get; set; } = int.MaxValue;
    /// <summary>Hard resolution-height ceiling. See the <c>allowed == 0</c> branch of the variant overload — a cap
    /// bounds the climb; it must never manufacture a downswitch.</summary>
    public int MaxHeight { get; set; } = int.MaxValue;
    /// <summary>Headroom required to CLIMB: a rung is affordable only at 0.85 × the estimate.</summary>
    public double UpSwitchFactor { get; set; } = 0.85;
    /// <summary>Headroom required to STAY: the current rung is abandoned only past 0.95 × the estimate. The asymmetric
    /// 0.85 / 0.95 band is what keeps the forced probe from becoming a per-second up/down oscillation.</summary>
    public double DownSwitchFactor { get; set; } = 0.95;
    /// <summary>Forward buffer required before Auto may climb. Deliberately well under the old 12 s: with the native
    /// startup burst at 4 segments (~16 s) 12 s is reachable, but 6 s makes the FIRST climb happen while the user is
    /// still watching the opening bars rather than half a minute in.</summary>
    public TimeSpan UpgradeBuffer { get; set; } = TimeSpan.FromSeconds(6);
    /// <summary>Consecutive healthy decisions after which Auto steps up one rung REGARDLESS of the estimate.</summary>
    public int ForcedProbeSegments { get; set; } = 3;

    /// <summary>The conservative throughput estimate (kbps). Never zero — the estimator is seeded.</summary>
    public double EstimatedKbps => _throughput.EstimateKbps;
    /// <summary>The reactive EWMA (kbps) — diagnostics.</summary>
    public double FastKbps => _throughput.FastKbps;
    /// <summary>The conservative EWMA (kbps) — diagnostics.</summary>
    public double SlowKbps => _throughput.SlowKbps;
    /// <summary>How many real throughput samples have been accepted.</summary>
    public int ThroughputSamples => _throughput.AcceptedSamples;
    /// <summary>Why the last <c>Choose</c> landed where it did.</summary>
    public AbrDecisionReason LastDecisionReason => _reason;
    /// <summary>The ladder position the last <c>Choose</c> settled on.</summary>
    public int CurrentIndex => _current;

    /// <summary>Fold one transfer sample in. Returns false when it was discarded as RTT noise.</summary>
    public bool RecordDownload(long payloadBytes, TimeSpan elapsed) => _throughput.Add(payloadBytes, elapsed);

    /// <summary>Full reset for a NEW source: ladder position, vote/probe state AND throughput history.</summary>
    public void Reset()
    {
        _throughput.Reset();
        ResetLadderState();
        _current = 0;
        _climbedOnce = false;
        _probeInFlight = false;
        _probeFailures = 0;
    }

    /// <summary>Reset for a SEEK. A seek does not change the network, so the throughput history is PRESERVED —
    /// clearing it dropped Auto back to the prior after every scrub and re-ran the whole slow climb.</summary>
    public void ResetForSeek() => ResetLadderState();

    private void ResetLadderState() { _upgradeCandidate = -1; _upgradeVotes = 0; _steadySegments = 0; }

    /// <summary>Seed the representation already opened by the backend (for example a conservative 480p startup rung).</summary>
    public void SeedCurrent(int index) { _current = Math.Max(0, index); ResetLadderState(); }

    /// <inheritdoc/>
    public int Choose(ReadOnlySpan<int> variantBitrates, TimeSpan forwardBuffered, double measuredKbps)
    {
        if (variantBitrates.IsEmpty) { _reason = AbrDecisionReason.Hold; return 0; }
        if (!Selection.IsAuto && Selection.VariantId is { } id && int.TryParse(id, out int pinned))
        { _reason = AbrDecisionReason.Pinned; return _current = Math.Clamp(pinned, 0, variantBitrates.Length - 1); }

        double estimateBps = (measuredKbps > 0 ? measuredKbps : EstimatedKbps) * 1000.0;
        double climbBudget = estimateBps * Math.Clamp(UpSwitchFactor, 0.25, 0.99);
        double sustainBudget = estimateBps * Math.Clamp(DownSwitchFactor, 0.30, 1.00);

        // Pick by BITRATE, not by index position, so an unsorted ladder cannot invert the decision.
        int climb = -1, sustain = -1, cheapest = 0;
        for (int i = 0; i < variantBitrates.Length; i++)
        {
            int br = variantBitrates[i];
            if (br < variantBitrates[cheapest]) cheapest = i;
            if (br > MaxBitrate) continue;
            if (br <= sustainBudget && (sustain < 0 || br > variantBitrates[sustain])) sustain = i;
            if (br <= climbBudget && (climb < 0 || br > variantBitrates[climb])) climb = i;
        }
        if (sustain < 0) sustain = cheapest;   // nothing is affordable — a floor beats no video
        if (climb < 0) climb = sustain;

        _current = Math.Clamp(_current, 0, variantBitrates.Length - 1);
        int currentBitrate = variantBitrates[_current];

        // 1. Downswitch is immediate and unconditional — a stall is worse than any resolution.
        if (variantBitrates[sustain] < currentBitrate)
        {
            // A probe the very next decision reverts is an OSCILLATION, and every reversal costs a real
            // representation switch (which is what visibly freezes the frame). Back the probe cadence off
            // exponentially so a link that genuinely cannot hold the next rung is retried at 3, 6, 12, 24, 48
            // decisions instead of every 3 forever.
            if (_probeInFlight && _probeFailures < 4) _probeFailures++;
            _probeInFlight = false;
            ResetLadderState();
            _reason = AbrDecisionReason.Throughput;
            return _current = sustain;
        }

        // 2. Any climb (throughput-justified or a probe) needs a healthy forward buffer. This branch is NEUTRAL for
        //    the probe verdict: a probe is judged by whether it survives, not by a momentary buffer dip.
        if (forwardBuffered < UpgradeBuffer)
        {
            ResetLadderState();
            _reason = AbrDecisionReason.Buffer;
            return _current;
        }

        // The probe survived a full decision with the buffer healthy and no downswitch indicated — it stuck, so the
        // next probe may come at the base cadence again.
        if (_probeInFlight) { _probeInFlight = false; _probeFailures = 0; }

        // 3. Throughput-justified climb. The FIRST climb after startup needs ONE vote, not two: the two-votes-at-1s
        //    gate exists to damp steady-state oscillation, and applying it at startup is what left Auto parked on the
        //    opening rung for seconds after the estimate had already justified moving off it.
        if (variantBitrates[climb] > currentBitrate)
        {
            if (_upgradeCandidate != climb) { _upgradeCandidate = climb; _upgradeVotes = 1; }
            else if (_upgradeVotes < byte.MaxValue) _upgradeVotes++;
            if (_upgradeVotes >= (_climbedOnce ? 2 : 1))
            {
                ResetLadderState();
                _climbedOnce = true;
                _reason = AbrDecisionReason.Throughput;
                return _current = climb;
            }
            _reason = AbrDecisionReason.Hold;
            return _current;
        }

        // 4. FORCED PROBE. The estimate is derived from what the CURRENT rung downloads, so a low rendition
        //    self-reinforces: it never transfers enough per segment to justify climbing off itself, and Auto sits on
        //    240p over a gigabit link forever. With the buffer at/above target and no downswitch indicated for
        //    ForcedProbeSegments consecutive decisions, step up ONE rung regardless of the estimate and let the next
        //    segment's real sample confirm or refute it. The 0.85 / 0.95 hysteresis band above is what stops that
        //    probe turning into a per-second oscillation.
        _upgradeCandidate = -1;
        _upgradeVotes = 0;
        if (_steadySegments < byte.MaxValue) _steadySegments++;
        int probe = -1;
        for (int i = 0; i < variantBitrates.Length; i++)
        {
            int br = variantBitrates[i];
            if (br <= currentBitrate || br > MaxBitrate) continue;
            if (probe < 0 || br < variantBitrates[probe]) probe = i;
        }
        int probeAfter = Math.Max(1, ForcedProbeSegments) << _probeFailures;   // 3, 6, 12, 24, 48 decisions
        if (probe >= 0 && _steadySegments >= probeAfter)
        {
            _steadySegments = 0;
            _climbedOnce = true;
            _probeInFlight = true;
            _reason = AbrDecisionReason.ForcedProbe;
            return _current = probe;
        }
        _reason = AbrDecisionReason.Hold;
        return _current;
    }

    /// <summary>Pick a variant honouring the resolution cap. The returned index addresses <paramref name="variants"/>.</summary>
    public int Choose(IReadOnlyList<QualityVariant> variants, TimeSpan forwardBuffered)
    {
        if (variants.Count == 0) { _reason = AbrDecisionReason.Hold; return 0; }
        if (!Selection.IsAuto && Selection.VariantId is { } pinnedId)
        {
            for (int i = 0; i < variants.Count; i++)
                if (string.Equals(variants[i].Id, pinnedId, StringComparison.Ordinal))
                { _reason = AbrDecisionReason.Pinned; return _current = i; }
        }
        Span<int> bitrates = variants.Count <= 64 ? stackalloc int[variants.Count] : new int[variants.Count];
        Span<int> indices = variants.Count <= 64 ? stackalloc int[variants.Count] : new int[variants.Count];
        int allowed = 0;
        for (int i = 0; i < variants.Count; i++)
        {
            if (variants[i].Resolution.Height > MaxHeight) continue;
            bitrates[allowed] = variants[i].Bitrate;
            indices[allowed] = i;
            allowed++;
        }
        if (allowed == 0)
        {
            // A CAP MUST NEVER STARVE THE LADDER TO ITS FLOOR — this is half of the "Auto · 240p" bug. Real video
            // viewports sit far BELOW every rung a manifest offers (191 DIP docked rail, 202 DIP pop-out, 135 DIP
            // pop-out minimum), so every variant fails the MaxHeight filter and this branch is the COMMON case, not a
            // corner case. Returning index 0 here pinned Auto to the bottom rung on any bandwidth, and the
            // representation switch that forced is what froze the frame. Take the SMALLEST rung at/above the cap
            // instead: a cap bounds the climb, it never manufactures a downswitch. (ProtectedMediaSession also floors
            // the viewport cap at 720 before it reaches here — belt and braces, for the same bug.)
            int smallest = 0;
            for (int i = 1; i < variants.Count; i++)
                if (variants[i].Resolution.Height < variants[smallest].Resolution.Height) smallest = i;
            _reason = AbrDecisionReason.Capped;
            return _current = smallest;
        }

        // The span overload reasons in FILTERED coordinates; _current is kept in FULL-list coordinates (that is what
        // SeedCurrent publishes), so map in and back out around the call.
        int localCurrent = 0;
        for (int i = 0; i < allowed; i++)
            if (indices[i] == _current) { localCurrent = i; break; }
        _current = localCurrent;
        int selected = Choose(bitrates[..allowed], forwardBuffered, EstimatedKbps);
        return _current = indices[Math.Clamp(selected, 0, allowed - 1)];
    }
}
