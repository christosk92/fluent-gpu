using System;
using System.Collections.Immutable;
using System.Threading;
using FluentGpu.Hosting.Threading;

namespace FluentGpu.Media;

/// <summary>
/// A compiled audio graph (spec §7.4) — the immutable published VALUE the RT block path reads. The master chain is a
/// flat, pre-ordered array of stateful <see cref="IDspStage"/>s (post-mix EQ + the terminal limiter); the per-voice
/// chain is a spec template each voice instantiates with its own state (a fading-out track keeps its own EQ). Coefficients
/// are precomputed OFF the RT path when this is built.
/// </summary>
public sealed class CompiledAudioGraph
{
    /// <summary>The ordered master chain (post-mix EQ … + the TERMINAL limiter), stateful, one instance.</summary>
    public readonly IDspStage[] Master;
    /// <summary>The per-voice chain spec (each voice builds its own stateful stages from it).</summary>
    public readonly ImmutableArray<EffectSpec> PerVoiceChain;
    /// <summary>The summed stage latency (samples) for the §7.6 position compensation.</summary>
    public readonly int TotalLatencySamples;
    private readonly int _channels;
    private readonly int _mixRate;

    internal CompiledAudioGraph(IDspStage[] master, ImmutableArray<EffectSpec> perVoice, int channels, int mixRate)
    {
        Master = master;
        PerVoiceChain = perVoice;
        _channels = channels;
        _mixRate = mixRate;
        int lat = 0;
        for (int i = 0; i < master.Length; i++) lat += master[i].LatencySamples;
        TotalLatencySamples = lat;
    }

    /// <summary>Run the master chain in place over <paramref name="frames"/> frames. Alloc-free.</summary>
    public void RenderMaster(Span<float> buffer, int frames, in BlockCtx ctx)
    {
        var stages = Master;
        var span = buffer[..(frames * ctx.Channels)];
        for (int i = 0; i < stages.Length; i++) stages[i].Process(span, span, frames, ctx);
    }

    /// <summary>Build a fresh per-voice DSP chain from <see cref="PerVoiceChain"/> (own state per voice). Control-thread
    /// / prepare only. Returns null when the voice needs no chain.</summary>
    public IDspStage[]? BuildVoiceChain()
    {
        if (PerVoiceChain.IsDefaultOrEmpty) return null;
        var stages = new IDspStage[PerVoiceChain.Length];
        int k = 0;
        for (int i = 0; i < PerVoiceChain.Length; i++)
        {
            var st = AudioGraphHost.BuildStage(PerVoiceChain[i], _channels, _mixRate);
            if (st is not null) stages[k++] = st;
        }
        if (k == 0) return null;
        if (k != stages.Length) Array.Resize(ref stages, k);
        return stages;
    }
}

/// <summary>
/// The graph publisher (spec §7.4). A control-thread reconcile compiles an immutable <see cref="AudioGraphSpec"/> to a
/// flat POD stage array and publishes it by a SINGLE atomic pointer swap (<see cref="Interlocked.Exchange{T}"/>); the RT
/// block path reads it via <see cref="Live"/> (<see cref="Volatile.Read{T}"/>) — no torn read mid-block. The old graph
/// keeps rendering until the swap and is freed only under consume-gated quarantine
/// (<c>ConsumeSeq + RenderInFlightDepth + 1</c>) — exactly the engine's ComPtr retire model. NEVER mutate a live graph.
/// The consume path (<see cref="MarkConsumed"/>) is alloc-free.
/// </summary>
public sealed class AudioGraphHost
{
    private CompiledAudioGraph _live;
    private readonly int _channels;
    private readonly int _mixRate;

    // Fixed-capacity SPSC retire ring (spec §7.4 quarantine) — the CONTROL thread (Publish) is the single producer, the RT
    // thread (MarkConsumed) is the single consumer. Monotonic write/read indices (each owned by exactly one thread, the
    // other read via Volatile) make enqueue/drain lock-free and torn-read-free across the M4 thread split. Length is a power
    // of two so the index maps by mask. Alloc-free.
    private readonly (CompiledAudioGraph Graph, long EligibleSeq)[] _retire = new (CompiledAudioGraph, long)[16];
    private readonly int _retireMask = 15;
    private long _retireWrite;   // producer (Publish) owns the write to this
    private long _retireRead;    // consumer (MarkConsumed) owns the write to this
    private long _consumeSeq;    // consume STEPS (one per rendered block) — Interlocked-incremented by the RT thread
    private long _retiredCount;  // graphs reclaimed past the quarantine — consumer owns the write

    /// <summary>The number of graphs actually reclaimed past the quarantine (for tests/diagnostics).</summary>
    public int RetiredCount => (int)Volatile.Read(ref _retiredCount);

    /// <summary>Create a host at <paramref name="channels"/>/<paramref name="mixRate"/> with an initial passthrough graph.</summary>
    public AudioGraphHost(int channels, int mixRate)
    {
        _channels = channels;
        _mixRate = mixRate;
        _live = Compile(AudioGraphSpec.Passthrough);
    }

    /// <summary>The live compiled graph — read once per block on the RT path (atomic, never torn).</summary>
    public CompiledAudioGraph Live => Volatile.Read(ref _live);

    /// <summary>The consume-step counter driving quarantine (increments once per rendered block).</summary>
    public long ConsumeSeq => Volatile.Read(ref _consumeSeq);

    /// <summary>Pending (quarantined, not-yet-reclaimed) old graphs.</summary>
    public int PendingRetire => (int)(Volatile.Read(ref _retireWrite) - Volatile.Read(ref _retireRead));

    /// <summary>CONTROL thread (single producer): compile <paramref name="spec"/> and publish it by atomic swap; quarantine
    /// the old graph for <c>RenderInFlightDepth + 1</c> consume steps. Compilation (coefficients) happens here, OFF the RT
    /// path. Lock-free against the RT thread's <see cref="Live"/> read + <see cref="MarkConsumed"/> drain.</summary>
    public void Publish(AudioGraphSpec spec)
    {
        var compiled = Compile(spec);
        var old = Interlocked.Exchange(ref _live, compiled);
        if (old is not null) Enqueue(old, ConsumeSeq + QuarantinePolicy.Quarantine);
    }

    /// <summary>RT block path (single consumer): record that one block was consumed and reclaim any graph whose quarantine
    /// has elapsed. Alloc-free, lock-free. Call exactly once per rendered block, after reading <see cref="Live"/>.</summary>
    public void MarkConsumed()
    {
        long seq = Interlocked.Increment(ref _consumeSeq);
        long r = _retireRead;                          // only this thread writes _retireRead
        long w = Volatile.Read(ref _retireWrite);      // acquire the producer's published entries
        while (r < w)
        {
            int slot = (int)(r & _retireMask);
            if (seq < _retire[slot].EligibleSeq) break;   // still in flight — FIFO, so nothing later is eligible either
            _retire[slot] = default;                      // release the reference for GC
            r++;
            Volatile.Write(ref _retireRead, r);           // release the slot back to the producer
            Volatile.Write(ref _retiredCount, Volatile.Read(ref _retiredCount) + 1);
        }
    }

    private void Enqueue(CompiledAudioGraph graph, long eligibleSeq)
    {
        long w = _retireWrite;                         // only this thread writes _retireWrite
        long r = Volatile.Read(ref _retireRead);       // acquire the consumer's progress
        if (w - r >= _retire.Length) return;           // ring full: sized to max in-flight publishes; a sizing bug otherwise
        _retire[(int)(w & _retireMask)] = (graph, eligibleSeq);
        Volatile.Write(ref _retireWrite, w + 1);       // publish the entry
    }

    private CompiledAudioGraph Compile(AudioGraphSpec spec)
    {
        // Master chain: post-mix effects then the TERMINAL limiter (always present).
        int cap = (spec.MasterChain.IsDefaultOrEmpty ? 0 : spec.MasterChain.Length) + 1;
        var master = new IDspStage[cap];
        int k = 0;
        if (!spec.MasterChain.IsDefaultOrEmpty)
            for (int i = 0; i < spec.MasterChain.Length; i++)
            {
                var st = BuildStage(spec.MasterChain[i], _channels, _mixRate);
                if (st is not null) master[k++] = st;
            }
        master[k++] = new LimiterStage(spec.Limiter.CeilingDbTp, spec.Limiter.ReleaseMs, _mixRate);
        if (k != master.Length) Array.Resize(ref master, k);
        return new CompiledAudioGraph(master, spec.PerVoiceChain, _channels, _mixRate);
    }

    /// <summary>Build one stateful stage from an <see cref="EffectSpec"/> (control-thread / compile only).</summary>
    internal static IDspStage? BuildStage(EffectSpec spec, int channels, int mixRate)
    {
        switch (spec)
        {
            case EqSpec eq:
            {
                var st = new EqStage(channels) { Bypassed = eq.Bypassed };
                st.SetBands(eq.Bands.IsDefaultOrEmpty ? ReadOnlySpan<BiquadBand>.Empty : eq.Bands.AsSpan(), mixRate);
                return st;
            }
            case GainSpec g:
                return new GainStage(g.LinearGain) { Bypassed = g.Bypassed };
            case ChannelSpec c:
                return new ChannelStage(c.Balance, c.Mono) { Bypassed = c.Bypassed };
            case LimiterSpec lim:
                return new LimiterStage(lim.CeilingDbTp, lim.ReleaseMs, mixRate) { Bypassed = lim.Bypassed };
            default:
                return null;   // ResampleSpec is handled at the SRC edge, not as an in-place master node
        }
    }
}
