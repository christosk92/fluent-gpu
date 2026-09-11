using System.Diagnostics;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Reconciler;

internal enum ChildReconcilePhase : byte { Empty, Indexing, Matching, Ready, Committing, Committed, Cancelled, Faulted }

/// <summary>
/// Isolated child identity planning. Copies immutable element references and committed handles, then performs only
/// pure matching until Commit. A deadline may interrupt indexing/matching without touching topology, props, hooks,
/// resources or cleanup. Commit is deliberately indivisible: this is not speculative execution of user callbacks.
/// </summary>
internal sealed class ChildReconcilePlan
{
    private Element[] _old = [], _desired = [];
    private NodeHandle[] _oldNodes = [], _newNodes = [];
    private int[] _matches = [];
    private bool[] _used = [];
    private readonly Dictionary<string, int> _keys = new(StringComparer.Ordinal);
    private readonly Func<long> _timestamp;
    private int _oldCount, _newCount, _cursor, _lastMatch;
    // Unkeyed pairing state (see UnkeyedPairing): the mode (0 = undecided, 1 = ordinal, 2 = positional) and the
    // ordinal cursor into _old (the next old index the ordinal pairing may consume). Plan state, not Step locals, so a
    // budgeted caller that yields mid-matching resumes the ordinal walk exactly where it stopped.
    private int _unkeyedMode, _unkeyedCursor;
    private bool _changed;
    internal ChildReconcilePhase Phase { get; private set; }
    internal NodeHandle Parent { get; private set; }
    internal ulong Revision { get; private set; }
    internal ReadOnlySpan<NodeHandle> OriginalNodes => _oldNodes.AsSpan(0, _oldCount);
    internal ReadOnlySpan<int> Matches => _matches.AsSpan(0, _newCount);

    internal ChildReconcilePlan() : this(Stopwatch.GetTimestamp) { }
    internal ChildReconcilePlan(Func<long> timestamp) => _timestamp = timestamp;

    internal void Begin(SceneStore scene, NodeHandle parent, ReadOnlySpan<Element> desired, ReadOnlySpan<Element> old,
        ulong revision)
    {
        Reset();
        Grow(ref _old, old.Length); Grow(ref _desired, desired.Length);
        Grow(ref _oldNodes, old.Length); Grow(ref _used, old.Length);
        Grow(ref _newNodes, desired.Length); Grow(ref _matches, desired.Length);
        // These two copies are the plan's snapshot-isolation contract, not incidental cost: a budgeted caller may
        // yield Step across frames, during which the caller's own buffer (e.g. ForEl's pooled Fill buffer) can be
        // mutated or returned to a pool before Commit runs — gate.child-plan-yield-commit pins exactly this by
        // mutating the caller's array right after Begin. Element is a reference type, so both copies are a pointer
        // memmove (reused, pre-grown arrays — no allocation), not a deep copy; that cost is legitimate and stays.
        old.CopyTo(_old); desired.CopyTo(_desired);
        _oldCount = old.Length; _newCount = desired.Length;
        _oldNodes.AsSpan(0, _oldCount).Clear();
        _newNodes.AsSpan(0, _newCount).Clear();
        _used.AsSpan(0, _oldCount).Clear();
        _matches.AsSpan(0, _newCount).Fill(-1);
        var child = scene.FirstChild(parent);
        for (int i = 0; i < _oldCount && !child.IsNull; i++, child = scene.NextSibling(child)) _oldNodes[i] = child;
        Parent = parent; Revision = revision; _lastMatch = -1;
        _changed = _oldCount != _newCount;
        Phase = _oldCount > 32 ? ChildReconcilePhase.Indexing : ChildReconcilePhase.Matching;
    }

    /// <summary>Resumes only pure work; false means remaining debt. The initial bulk input copy is not preempted.
    /// <paramref name="deadlineTicks"/> of <see cref="long.MaxValue"/> means "run to completion, unbudgeted" — the
    /// ONLY form the production caller (<c>TreeReconciler.ReconcilePlannedChildren</c>) ever passes. That sentinel
    /// short-circuits the loop condition below so <see cref="_timestamp"/> (production: <c>Stopwatch.GetTimestamp</c>)
    /// is never sampled — previously it ran once per child, inside the UI reactive phase, for a call that never
    /// actually needed a deadline. A future budgeted caller (a real, non-MaxValue deadline) still gets the clock
    /// check every child, preserving resumability.</summary>
    internal bool Step(long deadlineTicks)
    {
        if (Phase == ChildReconcilePhase.Ready) return true;
        if (Phase is not (ChildReconcilePhase.Indexing or ChildReconcilePhase.Matching))
            throw new InvalidOperationException("Only a pending child plan may advance.");
        while (deadlineTicks == long.MaxValue || _timestamp() < deadlineTicks)
        {
            if (Phase == ChildReconcilePhase.Indexing)
            {
                if (_cursor < _oldCount)
                {
                    if (_old[_cursor].Key is { } key) _keys.TryAdd(key, _cursor);
                    _cursor++;
                    continue;
                }
                Phase = ChildReconcilePhase.Matching; _cursor = 0;
            }
            if (_cursor == _newCount) { Phase = ChildReconcilePhase.Ready; return true; }
            int index = _cursor++;
            Element desired = _desired[index];
            int match = -1;
            if (desired.Key is { } desiredKey)
            {
                if (_oldCount > 32)
                {
                    if (_keys.TryGetValue(desiredKey, out int found) && !_used[found]
                        && _old[found].ElementTypeId == desired.ElementTypeId) match = found;
                }
                else
                    for (int i = 0; i < _oldCount; i++)
                        if (!_used[i] && _old[i].Key == desiredKey && _old[i].ElementTypeId == desired.ElementTypeId)
                        { match = i; break; }
            }
            else
            {
                // The SAME unkeyed rule as TreeReconciler.ReconcileChildrenCore (full reasoning there), so the >128-child
                // path and the stack path pair identically: ORDINAL among unkeyed siblings when the unkeyed population is
                // unchanged, the former same-index rule when an unkeyed child was added/removed; reuse iff the element
                // type matches, a mismatch consumes the ordinal/slot. The mode's counting pass runs once, at the first
                // unkeyed desired child (pure-keyed lists never pay it) — one pure O(old+new) read over the snapshot.
                if (_unkeyedMode == 0)
                    _unkeyedMode = UnkeyedPairing.Ordinal(_old.AsSpan(0, _oldCount), _desired.AsSpan(0, _newCount)) ? 1 : 2;
                if (_unkeyedMode == 1)
                {
                    while (_unkeyedCursor < _oldCount && _old[_unkeyedCursor].Key is not null) _unkeyedCursor++;
                    if (_unkeyedCursor < _oldCount)
                    {
                        int ordinal = _unkeyedCursor++;
                        if (!_used[ordinal] && _old[ordinal].ElementTypeId == desired.ElementTypeId) match = ordinal;
                    }
                }
                else if (index < _oldCount && !_used[index] && _old[index].Key is null
                    && _old[index].ElementTypeId == desired.ElementTypeId) match = index;
            }
            _matches[index] = match;
            if (match < 0) { _changed = true; continue; }
            _used[match] = true;
            if (match < _lastMatch) _changed = true; else _lastMatch = match;
        }
        return false;
    }

    /// <summary>
    /// The single commit adapter is called only after the complete plan validates. No mount/update callback or
    /// departing cleanup can run while planning is yielded. Callback exceptions remain exceptions, not rollback.
    /// </summary>
    internal bool Commit(IChildReconcileCommitter target)
    {
        if (Phase != ChildReconcilePhase.Ready) throw new InvalidOperationException("A child plan must be complete before commit.");
        if (!target.Validate(Parent, Revision, OriginalNodes)) { Phase = ChildReconcilePhase.Cancelled; return false; }
        Phase = ChildReconcilePhase.Committing;
        try
        {
            for (int i = 0; i < _newCount; i++)
            {
                int match = _matches[i];
                if (match < 0) continue;
                _newNodes[i] = _oldNodes[match];
                target.Update(_oldNodes[match], _desired[i], _old[match]);
            }
            // Preserve scroll before incoming mounts consume the memory entry. Lifecycle order is deliberate:
            // matched updates, departing scroll capture, incoming mounts, departing cleanup, then final child order.
            for (int i = 0; i < _oldCount; i++) if (!_used[i]) target.SaveDeparting(_oldNodes[i]);
            for (int i = 0; i < _newCount; i++)
                if (_matches[i] < 0) _newNodes[i] = target.Mount(Parent, _desired[i]);
            for (int i = 0; i < _oldCount; i++)
                if (!_used[i]) { target.Remove(_oldNodes[i]); _changed = true; }
            target.PublishOrder(Parent, _newNodes.AsSpan(0, _newCount), _changed);
            Phase = ChildReconcilePhase.Committed;
            return true;
        }
        catch { Phase = ChildReconcilePhase.Faulted; throw; }
    }

    internal void Cancel()
    {
        if (Phase == ChildReconcilePhase.Committing) throw new InvalidOperationException("Commit is indivisible.");
        Reset(); Phase = ChildReconcilePhase.Cancelled;
    }

    internal void Reset()
    {
        Array.Clear(_old, 0, _oldCount); Array.Clear(_desired, 0, _newCount);
        _keys.Clear(); _oldCount = _newCount = _cursor = _unkeyedMode = _unkeyedCursor = 0;
        Parent = default; Revision = 0; Phase = ChildReconcilePhase.Empty;
    }

    private static void Grow<T>(ref T[] values, int length)
    {
        if (values.Length < length) Array.Resize(ref values, Math.Max(length, Math.Max(16, values.Length * 2)));
    }
}

/// <summary>
/// The unkeyed-identity mode decision shared by BOTH child-reconcile paths (<c>TreeReconciler.ReconcileChildrenCore</c>,
/// ≤128 children on the stack, and <see cref="ChildReconcilePlan"/>, >128) so they can never disagree. Keyed children
/// always match by <c>Key</c>; this only decides how UNKEYED children find their old counterpart.
/// </summary>
internal static class UnkeyedPairing
{
    /// <summary>True ⇒ ORDINAL pairing (the k-th unkeyed new child ↔ the k-th unkeyed old child, keyed siblings
    /// skipped): the unkeyed COUNT is unchanged, so the lists differ by keyed churn (plus in-place unkeyed changes), and
    /// keyed churn must not shift unkeyed identity. False ⇒ an unkeyed child was added or removed ⇒ the former same-index
    /// pairing, which is what an unkeyed insert/remove means positionally and which keeps a keyed⇄unkeyed flip at one
    /// slot from shifting its unkeyed siblings. One pure pass over both spans; no allocation.</summary>
    internal static bool Ordinal(ReadOnlySpan<Element> old, ReadOnlySpan<Element> desired)
    {
        int balance = 0;
        foreach (var e in old) if (e.Key is null) balance++;
        foreach (var e in desired) if (e.Key is null) balance--;
        return balance == 0;
    }
}

internal interface IChildReconcileCommitter
{
    bool Validate(NodeHandle parent, ulong revision, ReadOnlySpan<NodeHandle> originalNodes);
    void Update(NodeHandle node, Element desired, Element previous);
    void SaveDeparting(NodeHandle node);
    NodeHandle Mount(NodeHandle parent, Element desired);
    void Remove(NodeHandle node);
    void PublishOrder(NodeHandle parent, ReadOnlySpan<NodeHandle> children, bool changed);
}
