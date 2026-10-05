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

/// <summary>Allocation-free, duration-weighted exponentially-weighted throughput estimator. Samples use payload bits /
/// transfer time; the fast EWMA reacts to drops while the slow EWMA prevents a single burst from driving an unsafe upgrade.
/// <para>Each sample is weighted by its transfer duration (weight = seconds) against half-lives of
/// <see cref="FastHalfLifeSeconds"/> / <see cref="SlowHalfLifeSeconds"/> (Shaka's <c>EwmaBandwidthEstimator</c>), so a
/// 64 KB / 400 ms slice moves the estimate far less than a 5 MB / 2 s segment transfer instead of both counting as one
/// fixed-weight step. The estimate only counts as a measurement once <see cref="MinTotalBytes"/> have been folded in;
/// until then it stays the prior.</para>
/// <para>Two rules keep the estimate honest. (1) A sample smaller than <see cref="MinSampleBytes"/> or, below
/// <see cref="LargeSampleBytes"/>, shorter than <see cref="MinSampleMs"/> is round-trip/scheduler noise, not
/// throughput, and is DISCARDED — a 12 KB range request that completes in 40 ms "measures" whatever the RTT happened
/// to be, not the link. Above <see cref="LargeSampleBytes"/> the duration floor no longer applies: a multi-megabyte
/// aggregate is a measurement whatever its duration. (2) The estimator starts at <see cref="DefaultSeedKbps"/>
/// (2 Mbps) and NEVER at the bottom rung: seeding low is self-reinforcing, because a 240p rung transfers so little
/// per segment that its own samples can never justify climbing off it.</para></summary>
public sealed class ThroughputEstimator
{
    /// <summary>The startup prior: 2 Mbps. REPLACED outright (not blended) by the first accepted sample, so a fast
    /// link is not dragged down for a dozen segments by a conservative default.</summary>
    public const double DefaultSeedKbps = 2_000.0;
    /// <summary>Payload floor for an accepted sample (64 KB). Below it the measurement is RTT, not bandwidth.</summary>
    public const long MinSampleBytes = 64 * 1024;
    /// <summary>Transfer-time floor for an accepted sample (200 ms) — for payloads under <see cref="LargeSampleBytes"/>.</summary>
    public const double MinSampleMs = 200.0;
    /// <summary>Above this payload the duration floor no longer applies: a 5 MB aggregate that a CDN answered in 130 ms IS
    /// 300 Mbps — on a multi-megabyte transfer the RTT share of 130 ms is the noise, not the signal. Without this a fast
    /// link fills the whole forward buffer before any sample clears 200 ms, the estimate never leaves the seed, and Auto
    /// sits on the opening rung until the forced probe (2026-09-22: n=0 at 62 s buffered, every session).</summary>
    public const long LargeSampleBytes = 2L * 1024 * 1024;
    /// <summary>Half-life of the reactive EWMA, in seconds of transfer time.</summary>
    public const double FastHalfLifeSeconds = 2.0;
    /// <summary>Half-life of the conservative EWMA, in seconds of transfer time.</summary>
    public const double SlowHalfLifeSeconds = 5.0;
    /// <summary>Total accepted payload (128 KB) before the estimate counts as a measurement; below it the estimate is
    /// still the prior, so one marginal slice can never be the whole verdict.</summary>
    public const long MinTotalBytes = 128 * 1024;

    // The prior (2 Mbps seed or a remembered estimate) is kept apart from the measured EWMAs: the EWMAs start at zero and
    // are zero-factor corrected on read (Shaka), so the first measurement replaces the prior outright.
    private double _priorKbps = DefaultSeedKbps;
    private double _fastSum;
    private double _slowSum;
    private double _totalWeight;
    private long _totalBytes;
    private int _accepted;

    /// <summary>The reactive EWMA — reacts to drops.</summary>
    public double FastKbps => IsMeasured ? Corrected(_fastSum, FastHalfLifeSeconds) : _priorKbps;
    /// <summary>The conservative EWMA — keeps a single burst from driving an unsafe upgrade.</summary>
    public double SlowKbps => IsMeasured ? Corrected(_slowSum, SlowHalfLifeSeconds) : _priorKbps;
    /// <summary>How many samples have been accepted. Not the same as measured: see <see cref="IsMeasured"/>.</summary>
    public int AcceptedSamples => _accepted;
    /// <summary>True once <see cref="MinTotalBytes"/> of accepted payload have been folded in; false ⇒ the estimate is
    /// still the prior.</summary>
    public bool IsMeasured => _totalBytes >= MinTotalBytes;
    /// <summary>The conservative estimate the scheduler budgets against. Never zero — see <see cref="DefaultSeedKbps"/>.</summary>
    public double EstimateKbps => Math.Min(FastKbps, SlowKbps);

    /// <summary>Return to the 2 Mbps prior. For a NEW source only — a SEEK must keep the history, because the network
    /// did not change when the user dragged the scrubber.</summary>
    public void Reset()
    {
        _priorKbps = DefaultSeedKbps;
        _fastSum = 0; _slowSum = 0; _totalWeight = 0; _totalBytes = 0; _accepted = 0;
    }

    /// <summary>Fold one transfer sample in. Returns false when the sample was discarded as noise.</summary>
    public bool Add(long payloadBytes, TimeSpan elapsed)
    {
        if (payloadBytes < MinSampleBytes) return false;
        if (elapsed.TotalMilliseconds < MinSampleMs && payloadBytes < LargeSampleBytes) return false;
        double seconds = Math.Max(elapsed.TotalMilliseconds, 1.0) / 1000.0;
        double kbps = payloadBytes * 8.0 / seconds / 1000.0;
        if (!double.IsFinite(kbps) || kbps <= 0) return false;
        _fastSum = Fold(_fastSum, kbps, seconds, FastHalfLifeSeconds);
        _slowSum = Fold(_slowSum, kbps, seconds, SlowHalfLifeSeconds);
        _totalWeight += seconds;
        _totalBytes += payloadBytes;
        _accepted++;
        return true;
    }

    /// <summary>One duration-weighted EWMA step: a sample of <paramref name="weightSeconds"/> decays the history by
    /// 0.5^(weight / half-life).</summary>
    private static double Fold(double sum, double kbps, double weightSeconds, double halfLifeSeconds)
    {
        double decay = Math.Pow(0.5, weightSeconds / halfLifeSeconds);
        return kbps * (1.0 - decay) + decay * sum;
    }

    /// <summary>Zero-factor correction: the EWMA starts at 0, so divide by the weight actually accumulated.</summary>
    private double Corrected(double sum, double halfLifeSeconds)
    {
        double zeroFactor = 1.0 - Math.Pow(0.5, _totalWeight / halfLifeSeconds);
        return zeroFactor > 0 ? sum / zeroFactor : _priorKbps;
    }

    /// <summary>Replace the 2 Mbps startup prior with a remembered estimate (the app's last measurement on this machine).
    /// Still a PRIOR: <see cref="IsMeasured"/> stays false and the first measurement replaces it outright. Ignored once
    /// anything has been measured.</summary>
    public void Seed(double kbps)
    {
        if (!double.IsFinite(kbps) || kbps <= 0 || IsMeasured) return;
        _priorKbps = kbps;
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
    /// <summary>The cap dropped below the current rung; the best allowed rung that costs no more than the current one was
    /// taken at once (never the bottom of the ladder).</summary>
    CapDownswitch,
    /// <summary>The throughput budget indicated a lower rung but the forward buffer held at least
    /// <see cref="AdaptiveBitrateController.MaxBufferForQualityDecrease"/>, so the rung was kept: a dip is ridden out on
    /// the buffer already downloaded instead of being paid for with a switch.</summary>
    DeferredDecrease,
}

/// <summary>Production ABR controller: conservative throughput budget, a downshift that is immediate once the forward
/// buffer is thin (and deferred while it holds <see cref="MaxBufferForQualityDecrease"/>), buffer-gated upgrade, an
/// asymmetric climb/sustain hysteresis band, a timed forced probe that breaks the low-rendition feedback loop, plus a
/// manual pin and bitrate/resolution caps. It acts only on measurements; a prior holds. The forced probe is the only move
/// off a prior — see <see cref="EstimateIsPrior"/> and the 4-param <c>Choose</c> overload. Pacing between switches
/// (minimum interval, probe evidence) is the caller's, in <see cref="AbrSwitchGate"/>.</summary>
public sealed class AdaptiveBitrateController : IAbrPolicy
{
    private readonly ThroughputEstimator _throughput = new();
    private int _current;
    private int _upgradeCandidate = -1;
    private byte _upgradeVotes;
    private long _steadySinceMs = -1;   // when rule 4 was first reached without anything resetting the ladder; -1 = not steady
    private bool _climbedOnce;
    private bool _probeInFlight;
    private byte _probeFailures;
    private AbrDecisionReason _reason = AbrDecisionReason.Hold;

    /// <summary>Automatic, or a manual pin.</summary>
    public QualitySelection Selection { get; set; } = QualitySelection.Auto;
    /// <summary>Hard bitrate ceiling (bits/s).</summary>
    public int MaxBitrate { get; set; } = int.MaxValue;
    /// <summary>Hard resolution-height ceiling from POLICY (the user's pin, a metered link). Independent of
    /// <see cref="ViewportMaxHeight"/>: the effective cap is the smaller of the two, computed in <c>Choose</c>, so a
    /// viewport never overwrites a policy and a policy write never discards a viewport.</summary>
    public int PolicyMaxHeight { get; set; } = int.MaxValue;
    /// <summary>Resolution-height ceiling from the laid-out video surface. Cleared by <see cref="ResetForNewSource"/>:
    /// the previous source's surface is not this one's.</summary>
    public int ViewportMaxHeight { get; set; } = int.MaxValue;
    /// <summary>Compat property for callers that treat the cap as one number: set writes <see cref="PolicyMaxHeight"/>,
    /// get returns the EFFECTIVE cap, <c>min(policy, viewport)</c>. See the <c>allowed == 0</c> branch of the variant
    /// overload — a cap bounds the climb; it must never manufacture a downswitch to the floor.</summary>
    public int MaxHeight
    {
        get => Math.Min(PolicyMaxHeight, ViewportMaxHeight);
        set => PolicyMaxHeight = value;
    }
    /// <summary>Headroom required to CLIMB: a rung is affordable only at 0.85 × the estimate.</summary>
    public double UpSwitchFactor { get; set; } = 0.85;
    /// <summary>Headroom required to STAY: the current rung is abandoned only past 0.95 × the estimate. The asymmetric
    /// 0.85 / 0.95 band is what keeps the forced probe from becoming a per-second up/down oscillation.</summary>
    public double DownSwitchFactor { get; set; } = 0.95;
    /// <summary>Forward buffer required before Auto may climb. Deliberately well under the old 12 s: with the native
    /// startup burst at 4 segments (~16 s) 12 s is reachable, but 6 s makes the FIRST climb happen while the user is
    /// still watching the opening bars rather than half a minute in.</summary>
    public TimeSpan UpgradeBuffer { get; set; } = TimeSpan.FromSeconds(6);
    /// <summary>How long Auto must sit steady (buffer healthy, no change indicated) before it steps up one rung
    /// REGARDLESS of the estimate. In TIME, not decisions: the decision cadence is the caller's (1 Hz in the protected
    /// session, which made the old 3-decision budget a probe every 3 s). A failed probe doubles it, up to 16x.</summary>
    public TimeSpan ForcedProbeInterval { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>Forward buffer at or above which a measured downswitch is DEFERRED (Media3's
    /// <c>maxDurationForQualityDecreaseMs</c>): the buffer already downloaded rides out the dip, and the rung is left only
    /// when the buffer drops below it. Below it the downswitch stays immediate.</summary>
    public TimeSpan MaxBufferForQualityDecrease { get; set; } = TimeSpan.FromSeconds(25);
    /// <summary>The controller's clock (milliseconds, monotonic) for the probe interval; replaced in tests so the
    /// cadence is deterministic.</summary>
    public Func<long> NowMs { get; set; } = static () => Environment.TickCount64;

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

    /// <summary>See <see cref="ThroughputEstimator.Seed"/>.</summary>
    public void SeedEstimate(double kbps) => _throughput.Seed(kbps);
    /// <summary>True until the first real sample: the estimate is a prior and must never move the ladder by itself.</summary>
    public bool EstimateIsPrior => !_throughput.IsMeasured;

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

    /// <summary>Reset for a NEW source on a SHARED controller: ladder position, vote/probe state, the climb flag and the
    /// viewport cap go; the throughput history and the policy cap stay (the link did not change, and the policy is the
    /// app's, not the previous source's). Without it a probe backoff, <c>_climbedOnce</c> and the last surface's viewport
    /// cap leak into an unrelated video.</summary>
    public void ResetForNewSource()
    {
        ResetLadderState();
        _current = 0;
        _climbedOnce = false;
        _probeInFlight = false;
        _probeFailures = 0;
        _reason = AbrDecisionReason.Hold;
        ViewportMaxHeight = int.MaxValue;
    }

    /// <summary>Reset for a SEEK. A seek does not change the network, so the throughput history is PRESERVED —
    /// clearing it dropped Auto back to the prior after every scrub and re-ran the whole slow climb.</summary>
    public void ResetForSeek() => ResetLadderState();

    private void ResetLadderState() { _upgradeCandidate = -1; _upgradeVotes = 0; _steadySinceMs = -1; }

    /// <summary>Seed the representation already opened by the backend (for example a conservative 480p startup rung).</summary>
    public void SeedCurrent(int index) { _current = Math.Max(0, index); ResetLadderState(); }

    /// <summary>The caller declined the last decision (its switch gate said no, or the switch never landed): re-seed the
    /// ladder at the rung that is really in use. A forced probe that never left is neither a success nor a failure, so
    /// the probe flag clears WITHOUT backing the cadence off.</summary>
    public void DeclineLastDecision(int index)
    {
        _probeInFlight = false;
        SeedCurrent(index);
    }

    /// <inheritdoc/> — the IAbrPolicy seam: a caller that HANDS a measured kbps is by contract measured; the prior rule
    /// applies only to the controller's OWN estimate (the variant overload).
    public int Choose(ReadOnlySpan<int> variantBitrates, TimeSpan forwardBuffered, double measuredKbps)
        => Choose(variantBitrates, forwardBuffered, measuredKbps, estimateIsPrior: false);

    /// <summary>As the 3-param overload, but <paramref name="estimateIsPrior"/> says whether <paramref name="measuredKbps"/>
    /// is still the seed or a remembered value rather than a real measurement: rules 1 and 3 are skipped; 2 and 4 still apply.</summary>
    public int Choose(ReadOnlySpan<int> variantBitrates, TimeSpan forwardBuffered, double measuredKbps, bool estimateIsPrior)
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

        // 1. Downswitch is immediate ON A MEASUREMENT, once the forward buffer is thin. A prior (the 2 Mbps seed, or a
        //    remembered estimate) must never move the ladder off the rung the backend deliberately opened: the seed cannot
        //    "afford" the ≤480p opening rung, and acting on it put every cold start through 480p → 320p → 480p before the
        //    first frame (two swap chains; 2026-09-22). The opening rung is the app's decision; the first real sample
        //    corrects it.
        if (!estimateIsPrior && variantBitrates[sustain] < currentBitrate)
        {
            // With MaxBufferForQualityDecrease (25 s) already downloaded, one low sample is not worth a switch: the buffer
            // rides the dip out and a real slump drains it below the line within seconds. Returns BEFORE rule 4, so a
            // forced probe never fires while a decrease is indicated; a probe in flight stays in flight.
            if (forwardBuffered >= MaxBufferForQualityDecrease)
            {
                ResetLadderState();
                _reason = AbrDecisionReason.DeferredDecrease;
                return _current;
            }
            // A probe the very next decision reverts is an OSCILLATION, and every reversal costs a real
            // representation switch (which is what visibly freezes the frame). Back the probe cadence off
            // exponentially so a link that genuinely cannot hold the next rung is retried at 10, 20, 40, 80, 160 s
            // instead of every 10 forever.
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

        // 3. Throughput-justified climb — on a measurement only (a prior-driven climb is the same blindness, upward).
        //    The FIRST climb after startup needs ONE vote, not two: the two-votes-at-1s gate exists to damp
        //    steady-state oscillation, and applying it at startup is what left Auto parked on the opening rung for
        //    seconds after the estimate had already justified moving off it.
        if (!estimateIsPrior && variantBitrates[climb] > currentBitrate)
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
        //    ForcedProbeInterval of wall time, step up ONE rung regardless of the estimate and let the next segment's
        //    real sample confirm or refute it. The 0.85 / 0.95 hysteresis band above is what stops that probe turning
        //    into a per-second oscillation.
        _upgradeCandidate = -1;
        _upgradeVotes = 0;
        long nowMs = NowMs();
        if (_steadySinceMs < 0) _steadySinceMs = nowMs;
        int probe = -1;
        for (int i = 0; i < variantBitrates.Length; i++)
        {
            int br = variantBitrates[i];
            if (br <= currentBitrate || br > MaxBitrate) continue;
            if (probe < 0 || br < variantBitrates[probe]) probe = i;
        }
        long probeAfterMs = Math.Max(1L, (long)ForcedProbeInterval.TotalMilliseconds) << _probeFailures;   // 10, 20, 40, 80, 160 s
        if (probe >= 0 && nowMs - _steadySinceMs >= probeAfterMs)
        {
            _steadySinceMs = -1;
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
        // A ladder that shrank across sources must not leave _current out of range (FULL-list coordinates).
        _current = Math.Clamp(_current, 0, variants.Count - 1);
        int maxHeight = MaxHeight;   // the effective cap, read once so a concurrent writer cannot split one decision
        Span<int> bitrates = variants.Count <= 64 ? stackalloc int[variants.Count] : new int[variants.Count];
        Span<int> indices = variants.Count <= 64 ? stackalloc int[variants.Count] : new int[variants.Count];
        int allowed = 0;
        for (int i = 0; i < variants.Count; i++)
        {
            if (variants[i].Resolution.Height > maxHeight) continue;
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
        int localCurrent = -1;
        for (int i = 0; i < allowed; i++)
            if (indices[i] == _current) { localCurrent = i; break; }
        if (localCurrent < 0)
        {
            // The current rung is ABOVE the new cap. Falling back to local index 0 here silently became the cheapest rung
            // (rules 1-3 then all return it) — a quality crash on every fullscreen exit. A cap is not a throughput
            // verdict: take the allowed rung that costs the most without exceeding the current one (ties: taller), or,
            // on a non-monotonic ladder where nothing allowed is cheaper, the tallest allowed rung. No votes and no probe
            // accounting; a probe the cap cut short is simply over, not failed.
            int currentBitrate = variants[_current].Bitrate;
            int target = -1, tallest = 0;
            for (int i = 0; i < allowed; i++)
            {
                int height = variants[indices[i]].Resolution.Height;
                if (height > variants[indices[tallest]].Resolution.Height) tallest = i;
                if (bitrates[i] > currentBitrate) continue;
                if (target < 0 || bitrates[i] > bitrates[target]
                    || (bitrates[i] == bitrates[target] && height > variants[indices[target]].Resolution.Height))
                    target = i;
            }
            if (target < 0) target = tallest;
            ResetLadderState();
            _probeInFlight = false;
            _reason = AbrDecisionReason.CapDownswitch;
            return _current = indices[target];
        }
        _current = localCurrent;
        int selected = Choose(bitrates[..allowed], forwardBuffered, EstimatedKbps, EstimateIsPrior);
        return _current = indices[Math.Clamp(selected, 0, allowed - 1)];
    }
}
