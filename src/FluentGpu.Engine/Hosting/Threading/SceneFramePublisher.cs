using System;
using System.Threading;
using FluentGpu.Rhi;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Animation;

namespace FluentGpu.Hosting.Threading;

/// <summary>
/// Single-producer/single-consumer frame mailbox. Each slot owns a pinned command/key arena.
/// The consumer atomically claims a generation BEFORE reading its header or arena, and retains
/// that claim until adopting another publication. A header recheck cannot provide that ownership.
/// Publication is last-writer-wins; skipped publications carry their repaint damage forward.
/// Hosted scenes are captured on UI and recorded by the consumer. A consumer can retain and
/// reread a claimed frame without admitting UI writes to its storage, including animation-only turns.
/// <para>A skipped publication is NORMAL, not an error: the UI publishes at its own rate and the renderer adopts the
/// newest. Nothing about a gap invalidates a scene publication — the host holds the scene's record-dirty bits and
/// removal ledger until <see cref="LastConsumedSeq"/> catches up, so every snapshot describes the delta from the last
/// CONSUMED publication, and the span table's freshness test counts RECORD frames, not publications. The two ledgers
/// that are per-publication rather than per-scene — the structural-cancel damage rects and the repaint region — are
/// carried across the gap here.</para>
/// </summary>
public sealed class SceneFramePublisher
{
    private const long Free = 0, Writing = 1, Published = 2, Reading = 3;
    private readonly RenderFrame[] _slots = new RenderFrame[3];
    private readonly byte[][] _cmds = new byte[3][];
    private readonly ulong[][] _sort = new ulong[3][];
    private readonly long[] _slotStates = new long[3];
    private readonly SceneRenderFrame?[] _scenes = new SceneRenderFrame?[3];
    // P8: the publication each slot's snapshot currently describes. It is the baseline of that slot's NEXT incremental
    // capture, and the MINIMUM across all three is how long the store must retain its ledgers - a slot that has not
    // published for two frames still needs every delta since ITS baseline, not since the last consumed publication.
    private readonly ulong[] _sceneCaptureSeq = new ulong[3];
    private readonly SceneCapacityReclaimPolicy[] _capacityPolicy = new SceneCapacityReclaimPolicy[3];
    private readonly bool _reverse;
    private long _publishedToken; // generation << 2 | slot; zero means no publication
    private int _consumeIdx = -1; // consumer-private retained claim
    private ulong _publishSeq;
    private ulong _lastConsumedSeq;
    private long _targetEpoch;
    private long _lastConsumedTargetEpoch;
    private RepaintDamageRegion _pendingRepaint;
    private ulong _pendingRepaintSeq;
    private ulong _pendingCarriedFrom;
    // Structural-cancel damage (AnimEngine.PendingStructuralDamage) named by publications the consumer SKIPPED. Those
    // rects live in the publication's OWN slot, and a skipped slot is recycled, so without this carry they would never
    // reach a recorder — the band a cancelled FLIP vacated would keep last frame's pixels. Bounded: past the cap the
    // newcomer folds into the last member (over-inclusion is the safe direction for damage). Grown once, then reused.
    private const int MaxCarriedSceneDamage = 32;
    private RectF[] _carriedSceneDamage = [];
    private int _carriedSceneDamageCount;

    public SceneFramePublisher(int cmdCap = 1 << 16, int sortCap = 1 << 12, bool reverse = false)
    {
        _reverse = reverse;
        for (int i = 0; i < 3; i++)
        {
            _cmds[i] = GC.AllocateUninitializedArray<byte>(Math.Max(1, cmdCap), pinned: true);
            _sort[i] = GC.AllocateUninitializedArray<ulong>(Math.Max(1, sortCap), pinned: true);
        }
    }

    /// <summary>Latest adopted publication, also the consume-gated quarantine clock.</summary>
    public ulong LastConsumedSeq => Volatile.Read(ref _lastConsumedSeq);

    /// <summary>The <see cref="RenderFrame.TargetEpoch"/> of the latest adopted publication — the epoch the pixels the
    /// consumer last worked from belong to. A scene publication whose epoch differs from this one cannot trust those
    /// pixels as a partial-repaint base and repaints in full. Starts at the initial epoch: the FIRST frame's full
    /// repaint is the host's <c>_repaintTargetValid</c> latch, not a seam concern.</summary>
    public long LastConsumedTargetEpoch => Volatile.Read(ref _lastConsumedTargetEpoch);

    /// <summary>UI-private latest publication counter.</summary>
    public ulong PublishSeq => _publishSeq;

    /// <summary>True when <see cref="TryAcquire"/> would adopt a frame — the SAME gate it uses (published token vs
    /// last-consumed seq), so the two can never disagree. Consumer-side peek: the render loop asks this BEFORE paying
    /// the present-slot wait, so a bare wake (no publish) never blocks on a present slot it will not spend.
    /// Not a claim — a newer frame may land during the wait, which is precisely the point of waiting first.</summary>
    public bool HasPendingFrame
    {
        get
        {
            ulong seq = (ulong)Volatile.Read(ref _publishedToken) >> 2;
            return seq != 0 && seq != Volatile.Read(ref _lastConsumedSeq);
        }
    }

    /// <summary>Copy a completed frame into a claimed write slot and publish. Zero steady allocation.</summary>
    public ulong Publish(ReadOnlySpan<byte> cmds, ReadOnlySpan<ulong> sort, in FrameInfo submit,
                         bool suppressVsync = false, bool interactivePresent = false)
    {
        if (_reverse) ThreadGuard.AssertRender(); else ThreadGuard.AssertUi();
        ulong seq = _publishSeq + 1;
        int free = ClaimWriteSlot(seq);
        if (cmds.Length > _cmds[free].Length)
            _cmds[free] = GC.AllocateUninitializedArray<byte>(NextCap(_cmds[free].Length, cmds.Length), pinned: true);
        if (sort.Length > _sort[free].Length)
            _sort[free] = GC.AllocateUninitializedArray<ulong>(NextCap(_sort[free].Length, sort.Length), pinned: true);
        cmds.CopyTo(_cmds[free]);
        sort.CopyTo(_sort[free]);

        var region = submit.RepaintDamage;
        ulong carriedFrom = seq;
        if (_pendingRepaintSeq != 0 && Volatile.Read(ref _lastConsumedSeq) < _pendingRepaintSeq)
        {
            region.Union(in _pendingRepaint);
            carriedFrom = _pendingCarriedFrom;
        }
        _pendingRepaint = region;
        _pendingRepaintSeq = seq;
        _pendingCarriedFrom = carriedFrom;
        _publishSeq = seq;
        _slots[free] = new RenderFrame
        {
            TargetEpoch = Volatile.Read(ref _targetEpoch),
            PublishSeq = seq,
            ArenaIndex = free,
            ByteLen = cmds.Length,
            SortLen = sort.Length,
            Submit = submit with { RepaintDamage = region, PublishSequence = seq, CarriedFromSeq = carriedFrom },
            SuppressVsync = suppressVsync,
            InteractivePresent = interactivePresent,
        };
        Volatile.Write(ref _slotStates[free], ((long)seq << 2) | Published);
        Volatile.Write(ref _publishedToken, ((long)seq << 2) | (uint)free);
        return seq;
    }

    /// <summary>
    /// Claim the newest generation before copying any bytes. Bare wakes preserve the existing
    /// claim and return false. On success the previous frame is released and must no longer be read.
    /// </summary>
    public bool TryAcquire(out RenderFrame frame)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            long token = Volatile.Read(ref _publishedToken);
            ulong seq = (ulong)token >> 2;
            if (seq == 0 || seq == Volatile.Read(ref _lastConsumedSeq)) break;
            int idx = (int)(token & 3);
            long expected = (token & ~3L) | Published;
            if (Interlocked.CompareExchange(ref _slotStates[idx], (token & ~3L) | Reading, expected) != expected)
                continue;

            // Generation checking is part of the claim, so a recycled index cannot satisfy an old token.
            frame = _slots[idx];
            int previous = _consumeIdx;
            _consumeIdx = idx;
            if (previous >= 0) Volatile.Write(ref _slotStates[previous], Free);
            Volatile.Write(ref _lastConsumedTargetEpoch, frame.TargetEpoch);
            Volatile.Write(ref _lastConsumedSeq, frame.PublishSeq);
            return true;
        }
        frame = default;
        return false;
    }

    public ReadOnlySpan<byte> Bytes(in RenderFrame rf) => _cmds[rf.ArenaIndex].AsSpan(0, rf.ByteLen);
    public ReadOnlySpan<ulong> SortKeys(in RenderFrame rf) => _sort[rf.ArenaIndex].AsSpan(0, rf.SortLen);

    internal ulong PublishScene(SceneStore scene, ImageCache images, StringTable strings, in SceneRecordOptions options,
        ReadOnlySpan<NodeHandle> skip, ReadOnlySpan<NodeHandle> reuseBlock, ReadOnlySpan<RectF> damage,
        DetachedAnimSlab detached, IReadOnlyList<PopupWindowSlot> popups, AnimEngine animation, in FrameInfo submit,
        bool suppressVsync, bool interactivePresent)
    {
        ThreadGuard.AssertUi();
        ulong seq = _publishSeq + 1;
        int slot = ClaimWriteSlot(seq);
        var frame = _scenes[slot] ??= new SceneRenderFrame();
        // A publication the consumer never adopted was NOT a delta it saw. The host answers that by holding the scene's
        // record-dirty bits and removal ledger until LastConsumedSeq catches up, so the snapshot below carries the UNION
        // of everything that changed since the last CONSUMED publication — which is exactly what span reuse validates
        // against. Everything else a skipped publication named has to ride forward explicitly, and there are two such
        // ledgers: the structural-cancel damage rects (carried here) and the repaint region (carried below).
        bool previousSkipped = _pendingRepaintSeq != 0 && Volatile.Read(ref _lastConsumedSeq) < _pendingRepaintSeq;
        if (!previousSkipped) _carriedSceneDamageCount = 0;
        CarrySceneDamage(damage);
        frame.Capture(scene, images, strings, options, skip, reuseBlock,
            _carriedSceneDamage.AsSpan(0, _carriedSceneDamageCount), detached, popups, animation, seq,
            _sceneCaptureSeq[slot]);
        _sceneCaptureSeq[slot] = seq;
        _capacityPolicy[slot].Observe(frame.Scene.Capacity, frame.Scene.RequiredNodeCapacity, Environment.TickCount64);
        LastCapturedNodeCount = frame.Scene.CopiedNodeCount;
        LastCaptureWasIncremental = frame.Scene.LastCaptureWasIncremental;
        var repaint = submit.RepaintDamage;
        ulong carriedFrom = seq;
        if (previousSkipped)
        {
            repaint.Union(in _pendingRepaint);
            carriedFrom = _pendingCarriedFrom;
        }
        // The ONLY full-repaint force the seam itself owns. The consumer's retained pixels belong to the epoch of the
        // frame it last adopted; an InvalidateTarget since then (first frame after a rebuild, resize, DPI, device
        // recovery) means those pixels are not a base this publication may paint deltas onto. Every other cause — a
        // clear-color change, image content under a byte-identical stream, live crossfades — is the host's, and rides
        // in through submit.RepaintDamage. A publication GAP is NOT such a cause: gaps are the steady state under
        // scroll, and forcing full on one made the renderer do its most expensive frame exactly when it was behind.
        if (Volatile.Read(ref _targetEpoch) != Volatile.Read(ref _lastConsumedTargetEpoch))
            repaint.ForceFull(RepaintFullReason.TargetInvalidated);
        _pendingRepaint = repaint;
        _pendingRepaintSeq = seq;
        _pendingCarriedFrom = carriedFrom;
        _slots[slot] = new RenderFrame
        {
            HasScene = true, PublishSeq = seq, ArenaIndex = slot,
            TargetEpoch = Volatile.Read(ref _targetEpoch),
            Submit = submit with { PublishSequence = seq, CarriedFromSeq = carriedFrom, RepaintDamage = repaint },
            SuppressVsync = suppressVsync, InteractivePresent = interactivePresent,
        };
        _publishSeq = seq;
        Volatile.Write(ref _slotStates[slot], ((long)seq << 2) | Published);
        Volatile.Write(ref _publishedToken, ((long)seq << 2) | (uint)slot);
        return seq;
    }

    /// <summary>Nodes whose columns the most recent <see cref="PublishScene"/> capture actually COPIED
    /// (<c>SceneRecordingSnapshot.CopiedNodeCount</c>) - every reachable node on a full capture, only the changed ones
    /// on an incremental one. Surfaced into <c>FrameStats.CapturedNodes</c>.</summary>
    internal int LastCapturedNodeCount { get; private set; }

    /// <summary>Whether the most recent <see cref="PublishScene"/> took the P8 incremental path (diagnostics).</summary>
    internal bool LastCaptureWasIncremental { get; private set; }

    internal int CapacityReclaims { get; private set; }
    // Cumulative released indexed-array payload, not GC committed/working-set reduction. Other replaced frame
    // capacities are intentionally excluded: dictionaries, sparse payloads and object graphs need separate accounting.
    internal long ReclaimedIndexedCapacityBytes { get; private set; }

    /// <summary>UI-only next eligible FREE-slot deadline. A reader is never a reason to poll or a trim candidate.</summary>
    internal long NextCapacityMaintenanceMs
    {
        get
        {
            ThreadGuard.AssertUi();
            long due = long.MaxValue;
            long announced = Volatile.Read(ref _publishedToken);
            for (int slot = 0; slot < _scenes.Length; slot++)
            {
                if (_scenes[slot] is not { } frame || Volatile.Read(ref _slotStates[slot]) != Free
                    || (announced != 0 && (int)(announced & 3) == slot)) continue;
                due = Math.Min(due, _capacityPolicy[slot].NextDeadlineMs(frame.Scene.Capacity));
            }
            return due;
        }
    }

    /// <summary>UI-only owner capacities. No renderer-owned scratch, dictionary enumeration, or process-memory estimate.</summary>
    internal (int initializedSlots, long indexedBytes, long textStyleBytes, long totalCapacity, long highestRequired) SceneCapacityCensus
    {
        get
        {
            ThreadGuard.AssertUi();
            int slots = 0;
            long indexed = 0, styles = 0, capacity = 0, required = 0;
            foreach (var frame in _scenes)
            {
                if (frame is null) continue;
                slots++;
                indexed += frame.Scene.IndexedCapacityBytes;
                styles += frame.Scene.TextStyleValueCapacityBytes;
                capacity += frame.Scene.Capacity;
                required += frame.Scene.RequiredNodeCapacity;
            }
            return (slots, indexed, styles, capacity, required);
        }
    }

    /// <summary>
    /// Cold UI preflight BEFORE protected frame phases, against the last committed scene. Reclaims at most one
    /// FREE slot per call. GPU idleness is neither checked nor accepted as a substitute for its CPU Writing claim.
    /// No publication/token/consumption sequence changes; unchanged next capture is fully prewarmed.
    /// The optional timestamp uses Environment.TickCount64's monotonic millisecond domain (deterministic tests).
    /// </summary>
    internal bool TryReclaimSceneCapacity(SceneStore source, ImageCache images, StringTable strings,
        DetachedAnimSlab detached, IReadOnlyList<PopupWindowSlot> popups, AnimEngine animation, long? nowMs = null)
    {
        ThreadGuard.AssertUi();
        long now = nowMs ?? Environment.TickCount64;
        for (int slot = 0; slot < _scenes.Length; slot++)
        {
            var previous = _scenes[slot];
            if (previous is null || !_capacityPolicy[slot].TryTarget(previous.Scene.Capacity, now, out int target)) continue;
            long announced = Volatile.Read(ref _publishedToken);
            if (announced != 0 && (int)(announced & 3) == slot) continue;
            if (Interlocked.CompareExchange(ref _slotStates[slot], Writing, Free) != Free) continue;
            try
            {
                _capacityPolicy[slot].NoteAttempt(now);
                var replacement = previous.PrepareCapacityReplacement(source, images, strings, detached, popups, animation, target);
                long released = previous.Scene.IndexedCapacityBytes - replacement.Scene.IndexedCapacityBytes;
                // A scene changed after the low-water observations. Keep the old frame if current preflight demand
                // defeats the saving; the cooldown prevents repeated allocate/discard work on every playback tick.
                if (replacement.Scene.Capacity > previous.Scene.Capacity / 2 || released <= 0)
                {
                    replacement.ReleaseResources();
                    return false;
                }
                previous.ReleaseResources(); // New pins are already held; no unpinned gap or premature span reuse.
                _scenes[slot] = replacement;
                _sceneCaptureSeq[slot] = 0; // Preflight is not a publication baseline; next capture MUST be full.
                _slots[slot] = default;
                CapacityReclaims++;
                ReclaimedIndexedCapacityBytes += released;
                return true;
            }
            finally { Volatile.Write(ref _slotStates[slot], Free); }
        }
        return false;
    }

    /// <summary>The OLDEST publication any slot's snapshot still describes — the sequence the scene's record-dirty /
    /// pending-removal / capture ledgers must be retained through, because that slot's next incremental refresh needs
    /// every delta since ITS baseline.
    /// <para>Slots that have never captured are EXCLUDED, not counted as 0. In the steady state only two of the three
    /// slots rotate (the third is claimed only during a consumer handover). Once initialized, the oldest writable
    /// snapshot is refreshed first. Counting a never-written slot as "describes publication 0" would pin every
    /// ledger open forever — record-dirty bits would never clear and span reuse would never fire again.</para>
    /// <para>0 (retain everything) only before the very first scene publication; after one, at least one slot has a
    /// baseline.</para></summary>
    internal ulong OldestSlotCaptureSeq
    {
        get
        {
            ulong oldest = ulong.MaxValue;
            for (int i = 0; i < _sceneCaptureSeq.Length; i++)
                if (_sceneCaptureSeq[i] != 0 && _sceneCaptureSeq[i] < oldest) oldest = _sceneCaptureSeq[i];
            return oldest == ulong.MaxValue ? 0UL : oldest;
        }
    }

    internal SceneRenderFrame Scene(in RenderFrame frame) => _scenes[frame.ArenaIndex]!;

    internal bool IsCurrentTarget(in RenderFrame frame) => frame.TargetEpoch == Volatile.Read(ref _targetEpoch);

    /// <summary>UI lifecycle edge while the consumer is parked: old target snapshots cannot be presented again.</summary>
    internal void InvalidateTarget()
    {
        Interlocked.Increment(ref _targetEpoch);
        Volatile.Write(ref _publishedToken, 0);
    }

    /// <summary>UI teardown only, after the consumer has stopped and its GPU work has retired.</summary>
    internal void ReleaseSceneResources()
    {
        foreach (var scene in _scenes) scene?.ReleaseResources();
        Array.Clear(_sceneCaptureSeq);   // no slot describes a publication any more
    }

    private int ClaimWriteSlot(ulong seq)
    {
        // Withdraw the announcement while claiming/writing. A reader that already loaded the old token can still
        // complete ONE generation-checked acquisition, but cannot hop through multiple slots during this bounded
        // sweep. That guarantees a writable slot without a retry/spin or a false exhaustion exception. An already
        // claimed scene remains valid and renderable throughout capture; zero only means "no new publication yet".
        long token = Interlocked.Exchange(ref _publishedToken, 0);
        int published = token == 0 ? -1 : (int)(token & 3);
        // Refresh the oldest writable SNAPSHOT first. First-fit can abandon a third slot after a handover,
        // pinning OldestSlotCaptureSeq (and every scene dirty/removal ledger) at that old publication forever.
        // Prefer initialized slots so the ordinary two-slot exchange does not allocate a needless third snapshot.
        int start = 0;
        ulong oldest = ulong.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            // A cold-prepared replacement has a zero baseline but fully warmed storage. Prefer it over allocating
            // an unused third frame; zero remains excluded from OldestSlotCaptureSeq because its next copy is full.
            if (i == published || _scenes[i] is null || _sceneCaptureSeq[i] >= oldest) continue;
            long state = Volatile.Read(ref _slotStates[i]);
            if ((state & 3) is not (Free or Published)) continue;
            oldest = _sceneCaptureSeq[i];
            start = i;
        }
        for (int offset = 0; offset < 3; offset++)
        {
            int i = (start + offset) % 3;
            if (i == published) continue;
            long state = Volatile.Read(ref _slotStates[i]);
            if ((state & 3) is not (Free or Published)) continue;
            if (Interlocked.CompareExchange(ref _slotStates[i], ((long)seq << 2) | Writing, state) == state)
                return i;
        }
        // The consumer may have claimed a previous publication just before this writer published
        // the latest one. During that handover two OLD slots can briefly be pinned. Supersede the
        // unclaimed latest slot in that case: its generation claim still prevents any reader from
        // seeing its bytes while writing, and repaint damage is carried forward above.
        if (published >= 0)
        {
            long state = Volatile.Read(ref _slotStates[published]);
            if ((state & 3) == Published &&
                Interlocked.CompareExchange(ref _slotStates[published], ((long)seq << 2) | Writing, state) == state)
                return published;
        }
        throw new InvalidOperationException("Scene frame publisher requires one producer and one consumer.");
    }

    /// <summary>Append this publication's structural-cancel rects to the carried set (see
    /// <see cref="_carriedSceneDamage"/>). At the cap the newcomer is folded into the last member: the set degrades in
    /// precision, never in coverage. Allocation-free once the buffer has grown.</summary>
    private void CarrySceneDamage(ReadOnlySpan<RectF> damage)
    {
        for (int i = 0; i < damage.Length; i++)
        {
            RectF r = damage[i];
            if (r.W <= 0f || r.H <= 0f) continue;
            if (_carriedSceneDamageCount >= MaxCarriedSceneDamage)
            {
                ref RectF last = ref _carriedSceneDamage[MaxCarriedSceneDamage - 1];
                last = Union(in last, in r);
                continue;
            }
            if (_carriedSceneDamageCount == _carriedSceneDamage.Length)
                Array.Resize(ref _carriedSceneDamage,
                    Math.Min(MaxCarriedSceneDamage, Math.Max(4, _carriedSceneDamage.Length * 2)));
            _carriedSceneDamage[_carriedSceneDamageCount++] = r;
        }
    }

    private static RectF Union(in RectF a, in RectF b)
    {
        float x0 = MathF.Min(a.X, b.X), y0 = MathF.Min(a.Y, b.Y);
        float x1 = MathF.Max(a.X + a.W, b.X + b.W), y1 = MathF.Max(a.Y + a.H, b.Y + b.H);
        return new RectF(x0, y0, x1 - x0, y1 - y0);
    }

    private static int NextCap(int current, int need)
    {
        int cap = Math.Max(1, current);
        while (cap < need) cap = checked(cap * 2);
        return cap;
    }
}
