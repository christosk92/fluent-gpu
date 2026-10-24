using System;
using System.Collections.Generic;
using System.Diagnostics;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Scene;

namespace FluentGpu.Reconciler;

// E7 (technical-plan §4, EP13) - persistent subtrees. Flow.Persist(key, build) mounts a PersistSlot component; this file is the
// reconciler table that makes its child survive the slot. The life of a key:
//   • OWNED: one live slot per key (_persistLive). Its child is ordinary content.
//   • PARKED: the owning slot is unmounted (UnmountSubtree reaches the anchor, or ReplaceComponent swaps it). Before the anchor's
//     children are torn down its child subtree is detached and parked (SetSubtreeParked: renders deferred, UseIsActive false,
//     animation/scroll tickers quiesced). Resources stay: image pins and video surfaces are untouched, so a video keeps its
//     DComp surface. A host timer (Flow.PersistParkMs, 10 s on the host clock, held while a freeze holds the timers) unmounts it.
//   • ADOPTED: a slot with the key mounts (MountComponent) while it is parked: the subtree is re-parented under the new anchor,
//     un-parked, and every component render and node binding in it is scheduled once, so context reads re-resolve against the
//     new ancestors (a re-parented subtree otherwise keeps its old provider subscriptions until something re-renders it). The
//     slot's first render then reconciles the new build() against the parked element (same root type: the state survives).
//   • WAITING: the keyed child diff mounts new children BEFORE it removes old ones, so a slot can mount while the owner is still
//     live. It renders nothing and waits; when the owner is unmounted in the same pass its subtree goes straight to the waiter.
//     A waiter still waiting after the frame (a true duplicate key) gives up and mounts its own content, as a non-owner.
// A root swap (RenderRootDiff removes the old root before it mounts the new one) is the case the theme transaction (E5) uses.
public sealed partial class TreeReconciler
{
    private sealed class ParkedPersist
    {
        public string Key = "";
        public NodeHandle Root;
        public Element? El;
        public long Seq;          // the generation its expiry timer carries (an adopted entry's timer fires into nothing)
        public double DeadlineMs; // informational (the timer owns the expiry): when it is unmounted without an adopter
    }

    private readonly Dictionary<int, string> _persistKeyByNode = new();                                    // slot anchor index → key
    private readonly Dictionary<string, NodeHandle> _persistLive = new(StringComparer.Ordinal);            // key → owning slot
    private readonly Dictionary<string, NodeHandle> _persistWaiting = new(StringComparer.Ordinal);         // key → waiting slot
    private readonly Dictionary<string, ParkedPersist> _persistParked = new(StringComparer.Ordinal);       // key → parked subtree
    private readonly List<ParkedPersist> _persistParkedScratch = new();
    private readonly List<NodeHandle> _persistWaitScratch = new();
    private long _persistSeq;
    private bool _persistTeardown;   // UnmountRoot / an expiring parked subtree: slots unmount their content like any component
    private Action<long>? _persistExpire;

    /// <summary>The host timer queue that expires a parked persistent subtree (<see cref="Flow.PersistParkMs"/>). Set by the host.
    /// Null (a bare reconciler): a parked subtree stays parked until it is adopted or the root unmounts.</summary>
    public HostTimerQueue? PersistTimers { get; set; }

    /// <summary>Persistent subtrees currently parked without a slot.</summary>
    public int ParkedPersistCount => _persistParked.Count;

    /// <summary>True while the subtree of <paramref name="key"/> is parked (its slot left the tree and no slot with the key mounted).</summary>
    public bool IsPersistParked(string key) => _persistParked.ContainsKey(key);

    /// <summary>Adoptions since this reconciler was created (a parked or departing subtree re-parented under a new slot).</summary>
    public int PersistAdoptions { get; private set; }

    private double PersistNowMs => PersistTimers?.NowMs ?? Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    private static bool IsPersistSlot(ComponentEl ce) => ce.Props is PersistProps && ce.ComponentType == typeof(PersistSlot);

    /// <summary>A reused slot whose key changed is a different persistent subtree: the reuse path replaces it (the old key parks,
    /// the new key adopts or builds).</summary>
    private static bool PersistKeyChanged(ComponentEl oldEl, ComponentEl newEl)
        => newEl.Props is PersistProps np && oldEl.Props is PersistProps op && !string.Equals(np.Key, op.Key, StringComparison.Ordinal);

    /// <summary>MountComponent, before the slot's first render: own the key, adopt its parked subtree, or wait for its live owner.</summary>
    private void MountPersistSlot(NodeHandle node, CompEntry entry, string key)
    {
        _persistKeyByNode[(int)node.Raw.Index] = key;
        if (_persistParked.Remove(key, out var parked))
        {
            if (_scene.IsLive(parked.Root))
            {
                _persistLive[key] = node;
                AdoptPersisted(node, entry, parked.Root, parked.El);
                return;
            }
        }
        if (_persistLive.TryGetValue(key, out var live) && live != node && _scene.IsLive(live))
        {
            // The owner is still in the tree. The first slot to arrive waits for it; any further one is a non-owner.
            if (_persistWaiting.TryAdd(key, node)) entry.PersistWaiting = true;
            return;
        }
        _persistLive[key] = node;
    }

    /// <summary>The slot at <paramref name="anchor"/> is leaving the tree (UnmountSubtree, or ReplaceComponent before it tears the
    /// children down). The owner's subtree goes to a waiting slot, or is parked; a non-owner's and a waiter's unmount normally.</summary>
    private void ReleasePersistSlot(NodeHandle anchor, string key)
    {
        if (_persistWaiting.TryGetValue(key, out var waiting) && waiting == anchor) { _persistWaiting.Remove(key); return; }
        if (!_persistLive.TryGetValue(key, out var owner) || owner != anchor) return;
        _persistLive.Remove(key);
        var content = _scene.FirstChild(anchor);
        if (_persistTeardown || content.IsNull || !_comps.TryGetValue(anchor, out var entry)) return;

        OnSubtreeDeactivated?.Invoke(content);   // hover/press/focus hygiene against the departing tree, as a KeepAlive park does
        SetSubtreeParked(content, parked: true);
        _scene.Detach(content);
        _reconciled = true;
        var el = entry.Rendered;
        entry.Rendered = null;

        if (_persistWaiting.Remove(key, out var waiter) && _scene.IsLive(waiter) && _comps.TryGetValue(waiter, out var we))
        {
            // The keyed diff mounted the new slot first: hand the subtree over now, in the same pass.
            we.PersistWaiting = false;
            _persistLive[key] = waiter;
            AdoptPersisted(waiter, we, content, el);
            we.Effect?.Schedule();   // its first render (skipped while it waited) reconciles build() against the adopted subtree
            return;
        }

        var p = new ParkedPersist { Key = key, Root = content, El = el, Seq = ++_persistSeq, DeadlineMs = PersistNowMs + Flow.PersistParkMs };
        _persistParked[key] = p;
        PersistTimers?.Schedule(p.DeadlineMs, p.Seq, _persistExpire ??= ExpireParkedPersist, typeof(PersistSlot));
    }

    /// <summary>Re-parent <paramref name="root"/> under the slot at <paramref name="node"/>, un-park it (unless the slot itself mounted
    /// parked) and render every component and binding in it once against the new ancestors.</summary>
    private void AdoptPersisted(NodeHandle node, CompEntry entry, NodeHandle root, Element? el)
    {
        _scene.Detach(root);
        _scene.AppendChild(node, root);
        entry.Rendered = el;
        SetSubtreeParked(root, parked: entry.Parked);
        RenderPersistedOnce(root);
        _scene.Mark(root, NodeFlags.LayoutDirty);   // its constraints are the new parent's
        MarkLayoutShape(node);
        _reconciled = true;
        PersistAdoptions++;
    }

    /// <summary>Schedule every component render and node binding (Show/For/skeleton boundaries, bound props) in the subtree, so each
    /// re-reads its context through the new ancestors - the forced render on adoption. A parked component (inside a parked
    /// KeepAlive page, or under a parked slot) owes it instead, and replays it when it un-parks; the KeepAlive pages detached
    /// inside the subtree are walked too, so they come back against the new providers.</summary>
    private void RenderPersistedOnce(NodeHandle node)
    {
        if (!_scene.IsLive(node)) return;
        if (_comps.TryGetValue(node, out var e))
        {
            if (e.Parked || e.ExitFrozen) e.DeferredRender = true;
            else e.Effect?.Schedule();
        }
        int idx = (int)node.Raw.Index;
        if (_nodeBindings.TryGetValue(idx, out var binds))
            for (var c = binds; c is not null; c = c.NextNodeBinding) c.Schedule();
        if (_keepAliveState.TryGetValue(idx, out var kas))
            foreach (var ka in kas.Entries.Values)
                if (!ka.Attached) RenderPersistedOnce(ka.Root);
        for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c)) RenderPersistedOnce(c);
    }

    /// <summary>The host timer of one parked subtree fired: unmount it if it is still parked under that generation.</summary>
    private void ExpireParkedPersist(long seq)
    {
        ParkedPersist? hit = null;
        foreach (var p in _persistParked.Values)
            if (p.Seq == seq) { hit = p; break; }
        if (hit is null) return;   // adopted (or replaced) since
        _persistParked.Remove(hit.Key);
        FreeParkedPersist(hit);
    }

    private void FreeParkedPersist(ParkedPersist p)
    {
        if (!_scene.IsLive(p.Root)) return;
        bool prev = _persistTeardown;
        _persistTeardown = true;   // a slot nested in the expiring subtree goes with it
        UnmountSubtree(p.Root);    // component scopes dispose: video surfaces are released, image pins dropped
        _scene.FreeSubtree(p.Root);
        _persistTeardown = prev;
        _reconciled = true;
    }

    /// <summary>Host, once per paint after the flush: a slot still waiting for a live owner has a true duplicate key. It stops
    /// waiting and mounts its own content (as a non-owner: its content unmounts normally when it leaves).</summary>
    public void SettlePersistWaiters()
    {
        if (_persistWaiting.Count == 0) return;
        _persistWaitScratch.Clear();
        foreach (var w in _persistWaiting.Values) _persistWaitScratch.Add(w);
        _persistWaiting.Clear();
        for (int i = 0; i < _persistWaitScratch.Count; i++)
        {
            var node = _persistWaitScratch[i];
            if (!_scene.IsLive(node) || !_comps.TryGetValue(node, out var e) || !e.PersistWaiting) continue;
            e.PersistWaiting = false;
            if (Diag.Enabled && _persistKeyByNode.TryGetValue((int)node.Raw.Index, out var key))
                Diag.Event("persist", $"duplicate Flow.Persist key '{key}': the second slot mounts its own content");
            e.Effect?.Schedule();
        }
        _persistWaitScratch.Clear();
    }

    /// <summary>UnmountRoot: every slot unmounts its content and every parked subtree is unmounted.</summary>
    private void TearDownPersisted()
    {
        _persistParkedScratch.Clear();
        foreach (var p in _persistParked.Values) _persistParkedScratch.Add(p);
        _persistParked.Clear();
        for (int i = 0; i < _persistParkedScratch.Count; i++) FreeParkedPersist(_persistParkedScratch[i]);
        _persistParkedScratch.Clear();
        _persistLive.Clear();
        _persistWaiting.Clear();
        _persistKeyByNode.Clear();
    }
}
