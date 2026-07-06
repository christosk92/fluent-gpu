using System;
using System.Text;
using Wavee.Backend.Playlists;
using Wavee.Backend.Sync;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Backend.Realtime;

// ── The single dealer firehose router (decode → enqueue) ──────────────────────────────────────────────────────────────
// ONE ITransport.Events("hm://") subscription carries both arms; this decodes the topic + protos and enqueues typed
// commands onto the LibrarySync loop — it NO LONGER writes the store itself (single-writer: the in-place apply / mark-dirty
// / refetch policy all live in the loop). Playlist pushes → PlaylistPush; the rootlist topic → RootlistPush; collection
// pushes pass the RAW payload through so the loop can interpret it. Unit-tested via StubTransport.PushEvent + a real loop.
public sealed class DealerRouter : IDisposable
{
    readonly LibrarySync _sync;
    readonly IDisposable _sub;

    public DealerRouter(ITransport transport, LibrarySync sync)
    {
        _sync = sync;
        _sub = transport.Events("hm://").Subscribe(new Observer(this));
    }

    void OnEvent(WireEvent e)
    {
        if (e.Topic.StartsWith("hm://playlist/", StringComparison.Ordinal)) OnPlaylist(e);
        else if (e.Topic.StartsWith("hm://collection/", StringComparison.Ordinal)) OnCollection(e);
    }

    void OnPlaylist(WireEvent e)
    {
        // Rootlist branch: hm://playlist/user/{u}/rootlist or hm://playlist/v2/user/{u}/rootlist. The RootlistModificationInfo
        // shape ({new_revision(1), parent_revision(2), ops(3)}) is a sibling of PlaylistModificationInfo.
        if (e.Topic.EndsWith("/rootlist", StringComparison.Ordinal))
        {
            Pl.RootlistModificationInfo rinfo;
            try { rinfo = Pl.RootlistModificationInfo.Parser.ParseFrom(e.Payload); }
            catch { return; }   // unparseable push → ignore
            _sync.Enqueue(new SyncCommand(SyncKind.RootlistPush,
                ParentRev: rinfo.HasParentRevision ? rinfo.ParentRevision.ToByteArray() : null,
                NewRev: rinfo.HasNewRevision ? rinfo.NewRevision.ToByteArray() : null,
                Ops: PlaylistWireMapper.MapOps(rinfo.Ops)));
            return;
        }

        Pl.PlaylistModificationInfo info;
        try { info = Pl.PlaylistModificationInfo.Parser.ParseFrom(e.Payload); }
        catch { return; }   // unparseable push → ignore

        string uri = info.HasUri ? Encoding.UTF8.GetString(info.Uri.Span) : PlaylistUriFromTopic(e.Topic);
        if (uri.Length == 0) return;
        _sync.Enqueue(new SyncCommand(SyncKind.PlaylistPush, uri,
            ParentRev: info.HasParentRevision ? info.ParentRevision.ToByteArray() : null,
            NewRev: info.HasNewRevision ? info.NewRevision.ToByteArray() : null,
            Ops: PlaylistWireMapper.MapOps(info.Ops)));
    }

    // Pass the RAW payload through — the loop attempts the PubSubUpdate parse (Phase 3); router stays parse-only for playlist4.
    void OnCollection(WireEvent e)
        => _sync.Enqueue(new SyncCommand(SyncKind.CollectionPush, CollectionSetFromTopic(e.Topic), Payload: e.Payload));

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

    sealed class Observer(DealerRouter owner) : IObserver<WireEvent>
    {
        public void OnNext(WireEvent e) => owner.OnEvent(e);
        public void OnCompleted() { }
        public void OnError(Exception e) { }
    }

    public void Dispose() => _sub.Dispose();
}
