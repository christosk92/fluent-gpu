using System;
using System.Collections.Generic;
using System.Text;
using Wavee.Backend.Playlists;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Backend.Realtime;

// ── The single dealer firehose router (the real-time tier policy) ─────────────────────────────────────────────────────
// ONE ITransport.Events("hm://") subscription carries BOTH arms; this decodes the topic and applies the two-step policy:
//   1. parent-rev gate — if the playlist is resident AND its stored revision byte-equals the push's parent_revision, the
//      ops apply IN PLACE (zero network); the revision advances.
//   2. anti-herd — otherwise (not resident, or a parent-rev mismatch, or a torn apply) the entry is only marked dirty;
//      it revalidates lazily on the next open via the revision-gated /diff. A push for a COLD playlist never fetches.
// The "mark dirty" actions are injected (Resource.MarkStale for the playlist/collection resources) so this stays decoupled
// and unit-testable via StubTransport.PushEvent.
public sealed class DealerRouter : IDisposable
{
    readonly IStore _store;
    readonly Action<string> _markPlaylistStale;
    readonly Action<string> _markCollectionStale;
    readonly IDisposable _sub;

    public DealerRouter(ITransport transport, IStore store, Action<string> markPlaylistStale, Action<string> markCollectionStale)
    {
        _store = store;
        _markPlaylistStale = markPlaylistStale;
        _markCollectionStale = markCollectionStale;
        _sub = transport.Events("hm://").Subscribe(new Observer(this));
    }

    void OnEvent(WireEvent e)
    {
        if (e.Topic.StartsWith("hm://playlist/", StringComparison.Ordinal)) OnPlaylist(e);
        else if (e.Topic.StartsWith("hm://collection/", StringComparison.Ordinal)) OnCollection(e);
    }

    void OnPlaylist(WireEvent e)
    {
        Pl.PlaylistModificationInfo info;
        try { info = Pl.PlaylistModificationInfo.Parser.ParseFrom(e.Payload); }
        catch { return; }   // unparseable push → ignore

        string uri = info.HasUri ? Encoding.UTF8.GetString(info.Uri.Span) : PlaylistUriFromTopic(e.Topic);
        if (uri.Length == 0) return;

        var membership = _store.Membership(uri);
        var stored = _store.PlaylistRevision(uri);
        var parent = info.HasParentRevision ? info.ParentRevision.ToByteArray() : null;

        // parent-rev gate: resident baseline + byte-equal parent → apply ops in place, zero network.
        if (membership.Count > 0 && stored is not null && parent is not null && BytesEqual(stored, parent))
        {
            var list = new List<PlaylistMember>(membership);
            try { PlaylistDiffApplier.Apply(list, PlaylistWireMapper.MapOps(info.Ops)); }
            catch (ArgumentOutOfRangeException) { _markPlaylistStale(uri); return; }   // torn apply → fall back to lazy /diff
            var newRev = info.HasNewRevision ? info.NewRevision.ToByteArray() : stored;
            _store.SetMembership(uri, list, newRev);
            return;
        }

        _markPlaylistStale(uri);   // not resident / mismatch → dirty only (anti-herd)
    }

    void OnCollection(WireEvent e)
        // A collection push invalidates freshness; the affected set revalidates lazily via /delta on next read.
        => _markCollectionStale(CollectionSetFromTopic(e.Topic));

    // "hm://playlist/v2/playlist/{base62}" → "spotify:playlist:{base62}".
    static string PlaylistUriFromTopic(string topic)
    {
        int i = topic.LastIndexOf('/');
        return i >= 0 && i + 1 < topic.Length ? "spotify:playlist:" + topic[(i + 1)..] : "";
    }

    // "hm://collection/{set}/{user}[/json]" → a best-effort set hint for the freshness invalidation.
    static string CollectionSetFromTopic(string topic)
    {
        var rest = topic.StartsWith("hm://collection/", StringComparison.Ordinal) ? topic["hm://collection/".Length..] : topic;
        int slash = rest.IndexOf('/');
        return slash > 0 ? rest[..slash] : rest;
    }

    static bool BytesEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    sealed class Observer(DealerRouter owner) : IObserver<WireEvent>
    {
        public void OnNext(WireEvent e) => owner.OnEvent(e);
        public void OnCompleted() { }
        public void OnError(Exception e) { }
    }

    public void Dispose() => _sub.Dispose();
}
