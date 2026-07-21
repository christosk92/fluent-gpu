using System;
using FluentGpu.Foundation;

namespace FluentGpu.Media;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// The preroll state machine (spec §8.4) — the seek-invalidated `PreparedSlot` + the join decision. This is where the named
// "crossfade-prepared-next" scar is fixed: a Seek/queue-edit BUMPS `Epoch`, INVALIDATES the slot, and re-prepares; a late
// Prepare completion whose Epoch mismatches is DROPPED and never reaches the mixer. The scheduler is driven off the SAMPLE
// clock (the mixer's ConsumeSeq domain) — no wall-clock, no timers — so it is fully deterministic. It only performs
// CONTROL-thread mixer edits (envelope retarget + AddVoice), never RT-block work.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The preroll lifecycle of the next item (spec §8.4).</summary>
public enum PrepState : byte
{
    /// <summary>Nothing pre-rolled.</summary>
    Idle,
    /// <summary>An async prepare is in flight (byte-source open + key resolve + decoder prime + prefill).</summary>
    Preparing,
    /// <summary>Pre-rolled and safe to consume at the join.</summary>
    Ready,
    /// <summary>Consumed — the transition committed and the voice is live in the mixer.</summary>
    Active,
    /// <summary>The prepare failed (the join degrades; never truncates the outgoing track).</summary>
    Failed
}

/// <summary>The prepared-next slot (spec §8.4). <see cref="Epoch"/> is bumped on Seek/queue-edit so a stale late prepare is
/// unambiguous (the named scar). A POD carrier — the scheduler owns exactly one.</summary>
public struct PreparedSlot
{
    /// <summary>The lifecycle state.</summary>
    public PrepState State;
    /// <summary>The prepared audio voice (null for a cross-backend / non-audio preroll).</summary>
    public IAudioSource? Source;
    /// <summary>The mixer-domain frame the voice is scheduled to begin at (resolved at commit).</summary>
    public long TargetStartFrame;
    /// <summary>The epoch this slot was prepared under (must equal the scheduler's live epoch to be honored).</summary>
    public uint Epoch;
    /// <summary>The prepared voice's sample-accurate trim (spec §8.3).</summary>
    public GaplessInfo Gapless;
    /// <summary>The prepared voice's loudness for the per-source ReplayGain scalar (spec §7.7).</summary>
    public ReplayGainInfo Loudness;
    /// <summary>The prepared item's backend kind (drives same-kind crossfade vs cross-backend hard-cut).</summary>
    public MediaKind Kind;
    /// <summary>The backend prepared handle (for disposal / a cross-backend hand-off).</summary>
    public IPreparedItem? Item;
}

/// <summary>What a join commit actually did (spec §8.4 — the mixer starts a crossfade ONLY if Ready by the join; otherwise
/// it degrades, NEVER truncating the outgoing track or starting the incoming one mid-fill).</summary>
public enum TransitionOutcome : byte
{
    /// <summary>No commit yet.</summary>
    None,
    /// <summary>Two voices overlapped through crossfade envelopes.</summary>
    Crossfaded,
    /// <summary>Sample-accurate butt-join (overlap 0), no discontinuity.</summary>
    Gapless,
    /// <summary>Wanted a crossfade but wasn't Ready by the crossfade start — butt-joined at the outgoing track's natural end.</summary>
    DegradedGapless,
    /// <summary>Wasn't Ready until after the outgoing track ended — a bounded, declicked micro-gap.</summary>
    DegradedMicroGap,
    /// <summary>A cross-backend (or explicit) hard cut — the outgoing audio is declicked out and the coordinator swaps engines.</summary>
    HardCut
}

/// <summary>
/// The voice preroll + join scheduler (spec §8.4). It arms the active track's join (<see cref="BeginActive"/>), reports when
/// the <c>ending-soon</c> preroll window has opened (<see cref="NeedsPrepare"/>), accepts an epoch-guarded prepared voice
/// (<see cref="SubmitPrepared"/>), and commits the transition at the join (<see cref="Commit"/>) — crossfade / gapless /
/// degraded / hard-cut — with declick everywhere a discontinuity can occur. A Seek/queue-edit calls <see cref="Invalidate"/>
/// which bumps the epoch and drops the slot (defeating the scar). Pure control-thread logic; deterministic off the sample clock.
/// </summary>
public sealed class VoiceScheduler
{
    private readonly int _mixRate;
    private readonly int _channels;
    private readonly int _declickFrames;
    private readonly Func<IDspStage[]?>? _voiceChainFactory;

    private uint _epoch = 1;
    private long _nextVoiceId = 1_000_000;   // scheduler-issued ids stay clear of a session's own voice id(s)
    private PreparedSlot _slot;

    // The armed active track's join parameters (mixer domain).
    private long _activeVoiceId;
    private long _activeStart;
    private long _activeTrimmedLen = long.MaxValue;
    private int _overlapFrames;
    private int _latencyMargin;
    private ScheduledTransition _transition = ScheduledTransition.Gapless;
    private NormMode _norm = NormMode.Album;
    private float _refLufs = -14f;

    private bool _committed;
    private bool _crossDecided;            // the crossfade-vs-degrade decision was taken at the crossfade start
    private bool _degrade;                 // decided to degrade (not Ready by the crossfade start)
    private TransitionOutcome _outcome;
    private long _incomingVoiceId;
    private VoiceInstaller? _installer;    // optional RT-safe voice installer (ring-wraps on the feed path); null = bare mixer

    /// <summary>Create a scheduler in the fixed mix format. <paramref name="declickMs"/> is the ramp applied at any
    /// discontinuity (spec §8.4: 2–5 ms). <paramref name="voiceChainFactory"/> builds the incoming voice's per-voice DSP
    /// chain (EQ etc.) — null for a bare mixer.</summary>
    public VoiceScheduler(int mixRate, int channels, float declickMs = 3f, Func<IDspStage[]?>? voiceChainFactory = null)
    {
        _mixRate = mixRate <= 0 ? 48000 : mixRate;
        _channels = Math.Max(1, channels);
        _declickFrames = Math.Max(1, (int)(declickMs * 0.001f * _mixRate));
        _voiceChainFactory = voiceChainFactory;
    }

    /// <summary>The live epoch (bumped by <see cref="Invalidate"/>).</summary>
    public uint Epoch => _epoch;
    /// <summary>The prepared-slot state.</summary>
    public PrepState State => _slot.State;
    /// <summary>The last commit outcome.</summary>
    public TransitionOutcome Outcome => _outcome;
    /// <summary>True once the join committed.</summary>
    public bool IsCommitted => _committed;
    /// <summary>The mixer-domain voice id of the incoming (committed) voice, or 0.</summary>
    public long IncomingVoiceId => _incomingVoiceId;
    /// <summary>The declick ramp length in frames.</summary>
    public int DeclickFrames => _declickFrames;

    /// <summary>Installs the incoming crossfade/gapless voice into the live graph. On the M4 RT feed path the coordinator
    /// wires this to <see cref="PcmAudioSession.AddCrossfadeVoice"/>, so the incoming source is wrapped in its OWN decode↔RT
    /// firewall ring (the worker decodes ahead, the RT thread mixes copy-only) — never decoding on the RT block path.</summary>
    public delegate void VoiceInstaller(IAudioSource src, GainEnvelope env, long startFrame, float replayGain, IDspStage[]? chain, long id);

    /// <summary>Route the incoming-voice install through <paramref name="installer"/> instead of a bare <c>mixer.AddVoice</c>
    /// (control thread; set once at open). The <see cref="QueuePlaybackCoordinator"/> wires it to
    /// <see cref="PcmAudioSession.AddCrossfadeVoice"/> so a crossfade committed on an RT-fed session ring-wraps the incoming
    /// voice (RT-safe — spec §7.9/§8). When null the incoming voice is added directly (the single-thread pull path).</summary>
    public void SetVoiceInstaller(VoiceInstaller installer) => _installer = installer;

    /// <summary>The outgoing track's natural end (mixer frame).</summary>
    public long JoinEndFrame => _activeStart >= 0 && _activeTrimmedLen != long.MaxValue ? _activeStart + _activeTrimmedLen : long.MaxValue;
    /// <summary>The crossfade start (mixer frame) — <see cref="JoinEndFrame"/> minus the overlap.</summary>
    public long CrossfadeStartFrame => JoinEndFrame == long.MaxValue ? long.MaxValue : JoinEndFrame - _overlapFrames;
    /// <summary>The <c>ending-soon</c> frame (spec §8.4): <c>overlap + worst-case latency margin</c> before the join.</summary>
    public long EndingSoonFrame => CrossfadeStartFrame == long.MaxValue ? long.MaxValue : CrossfadeStartFrame - _latencyMargin;

    /// <summary>Arm (or re-arm after a Seek) the active track's join. <paramref name="trimmedLenFrames"/> is A's trimmed
    /// length (mixer domain), <paramref name="latencyMarginFrames"/> the worst-case decode+decrypt+seek preroll margin. Does
    /// NOT touch the prepared slot (call <see cref="Invalidate"/> for that on a Seek).</summary>
    public void BeginActive(long activeVoiceId, long activeStartFrame, long trimmedLenFrames,
        ScheduledTransition transition, int latencyMarginFrames, NormMode norm, float referenceLufs)
    {
        _activeVoiceId = activeVoiceId;
        _activeStart = activeStartFrame;
        _activeTrimmedLen = trimmedLenFrames < 0 ? long.MaxValue : trimmedLenFrames;
        _transition = transition;
        _latencyMargin = Math.Max(0, latencyMarginFrames);
        _norm = norm;
        _refLufs = referenceLufs;
        _overlapFrames = transition.Kind switch
        {
            TransitionKind.Crossfade => Math.Max(0, (int)(transition.Overlap.TotalSeconds * _mixRate)),
            TransitionKind.HardCut => _declickFrames,   // a same-engine hard cut is a declick-length cross
            _ => 0
        };
        _committed = false;
        _crossDecided = false;
        _degrade = false;
        _outcome = TransitionOutcome.None;
        _incomingVoiceId = 0;
    }

    /// <summary>True when the preroll window has opened (<c>clock ≥ ending-soon</c>) and nothing is pre-rolled yet — the
    /// driver should kick off the async <see cref="IPreparableBackend.PrepareAsync"/> now.</summary>
    public bool NeedsPrepare(long clock)
        => !_committed && _slot.State == PrepState.Idle && clock >= EndingSoonFrame;

    /// <summary>Mark the slot as Preparing and return the epoch token the async prepare must carry back (spec §8.4).</summary>
    public uint MarkPreparing()
    {
        _slot.State = PrepState.Preparing;
        _slot.Epoch = _epoch;
        return _epoch;
    }

    /// <summary>Submit a completed prepare under the epoch it was issued with (spec §8.4). If <paramref name="epoch"/> does
    /// not match the live epoch (a Seek/queue-edit bumped it) the prepare is DROPPED — the item is disposed and the mixer is
    /// never touched (the scar fix). Returns true when the slot became Ready.</summary>
    public bool SubmitPrepared(uint epoch, IPreparedItem item)
    {
        if (epoch != _epoch || _committed)
        {
            _ = item.DisposeAsync();   // stale (or too late) — drop, never corrupt the mixer
            return false;
        }
        if (!item.IsReady)
        {
            _slot.State = PrepState.Failed;
            _slot.Item = item;
            return false;
        }
        _slot.State = PrepState.Ready;
        _slot.Source = item.AudioVoice;
        _slot.Gapless = item.Gapless;
        _slot.Loudness = item.Loudness;
        _slot.Kind = item.Kind;
        _slot.Item = item;
        _slot.Epoch = epoch;
        return true;
    }

    /// <summary>Invalidate the prepared slot on a Seek/queue-edit (spec §8.4 — the scar fix): bump the epoch (defeating any
    /// in-flight prepare) and drop the slot. Returns the NEW epoch. Re-arm the join via <see cref="BeginActive"/> afterwards.</summary>
    public uint Invalidate()
    {
        if (_slot.Item is not null) _ = _slot.Item.DisposeAsync();
        else (_slot.Source as IDisposable)?.Dispose();
        _slot = default;
        _committed = false;
        _crossDecided = false;
        _degrade = false;
        _outcome = TransitionOutcome.None;
        return unchecked(++_epoch);
    }

    /// <summary>Attempt to commit the transition at the join (spec §8.4). Idempotent — returns the same outcome once
    /// committed. Performs only control-thread mixer edits (envelope retarget + AddVoice). A cross-backend join returns
    /// <see cref="TransitionOutcome.HardCut"/> (the coordinator swaps engines); no second voice is mixed.</summary>
    public TransitionOutcome Commit(long clock, CrossfadeMixer mixer)
    {
        if (_committed) return _outcome;
        if (_activeTrimmedLen == long.MaxValue) return TransitionOutcome.None;   // unbounded active — no scheduled join

        long joinEnd = JoinEndFrame;
        bool crossBackend = _slot.State == PrepState.Ready && _slot.Kind != MediaKind.PcmAudio;

        // ── cross-backend hard cut: declick the outgoing audio; the coordinator swaps to the other engine ────────────
        if (crossBackend || (_transition.Kind == TransitionKind.HardCut && _slot.Source is null && _slot.State == PrepState.Ready))
        {
            if (clock < joinEnd - _declickFrames) return TransitionOutcome.None;
            long fadeStart = Math.Max(_activeStart, clock);
            mixer.TrySetVoiceEnvelope(_activeVoiceId, GainEnvelope.Fade(FadeKind.Out, fadeStart, _declickFrames, CrossCurve.Linear));
            _committed = true;
            _slot.State = PrepState.Active;
            _outcome = TransitionOutcome.HardCut;
            return _outcome;
        }

        bool wantCross = _overlapFrames > 0;   // crossfade or same-engine hard-cut (declick-length overlap)
        long crossStart = joinEnd - _overlapFrames;

        // ── the crossfade decision is taken ONCE, exactly at the crossfade start (spec §8.4) ─────────────────────────
        if (wantCross && !_crossDecided)
        {
            if (clock < crossStart) return TransitionOutcome.None;   // preroll window still running
            _crossDecided = true;
            _degrade = _slot.State != PrepState.Ready || _slot.Source is null;
            if (!_degrade)
            {
                CommitCrossfade(mixer, crossStart);
                _committed = true;
                _slot.State = PrepState.Active;
                _outcome = _transition.Kind == TransitionKind.HardCut ? TransitionOutcome.HardCut : TransitionOutcome.Crossfaded;
                return _outcome;
            }
            // else: NOT Ready by the join → fall through to a degraded gapless/micro-gap at A's natural end.
        }

        // ── gapless butt-join (planned gapless, or a degraded crossfade). Never truncate A, never start B mid-fill. ──
        if (_slot.State != PrepState.Ready || _slot.Source is null) return TransitionOutcome.None;   // still waiting

        long startAt = Math.Max(joinEnd, clock);       // butt-join at A's end, or a bounded micro-gap if already past it
        bool microGap = startAt > joinEnd;
        // A planned, sample-accurate gapless butt-join gets NO fade-in (exact join); a micro-gap gets a declick fade-in.
        var env = microGap
            ? GainEnvelope.Fade(FadeKind.In, startAt, _declickFrames, CrossCurve.Linear)
            : GainEnvelope.Constant;
        AddIncoming(mixer, startAt, env);
        _committed = true;
        _slot.State = PrepState.Active;
        _outcome = _degrade ? (microGap ? TransitionOutcome.DegradedMicroGap : TransitionOutcome.DegradedGapless) : TransitionOutcome.Gapless;
        return _outcome;
    }

    private void CommitCrossfade(CrossfadeMixer mixer, long crossStart)
    {
        var curve = MapCurve(_transition.Curve);
        // Outgoing A: fade out over the overlap starting at the crossfade start.
        mixer.TrySetVoiceEnvelope(_activeVoiceId, GainEnvelope.Fade(FadeKind.Out, crossStart, _overlapFrames, curve));
        // Incoming B: fade in over the same window (equal-power keeps constant power for uncorrelated material).
        AddIncoming(mixer, crossStart, GainEnvelope.Fade(FadeKind.In, crossStart, _overlapFrames, curve));
    }

    private void AddIncoming(CrossfadeMixer mixer, long startFrame, GainEnvelope env)
    {
        _incomingVoiceId = _nextVoiceId++;
        _slot.TargetStartFrame = startFrame;
        float rg = ReplayGain.ScalarLinear(_slot.Loudness, _norm, _refLufs);
        var chain = _voiceChainFactory?.Invoke();
        if (_installer is not null)
        {
            // RT-safe path: the session ring-wraps the incoming source so the worker (not the RT thread) decodes it.
            _installer(_slot.Source!, env, startFrame, rg, chain, _incomingVoiceId);
        }
        else
        {
            mixer.AddVoice(new MixVoice
            {
                Id = _incomingVoiceId,
                Src = _slot.Source!,
                Env = env,
                StartFrame = startFrame,
                ReplayGainScalar = rg,
                Chain = chain,
            });
        }
    }

    private static CrossCurve MapCurve(Easing easing) => easing == Easing.Linear ? CrossCurve.Linear : CrossCurve.EqualPower;
}
