using System;
using System.Collections.Generic;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Backend.Playlists;

// The SpotifyLive boundary mapper: playlist4 wire protos → the proto-free domain (PlaylistMember / PlaylistOp). Kept in
// the Backend (not SpotifyLive) so it is unit-tested against crafted protos, exactly like ExtendedMetadataSource.
public static class PlaylistWireMapper
{
    /// <summary>Project a SelectedListContent into ordered membership + the opaque revision bytes.</summary>
    public static (IReadOnlyList<PlaylistMember> Members, byte[]? Revision) ParseContents(Pl.SelectedListContent slc)
    {
        byte[]? rev = slc.HasRevision ? slc.Revision.ToByteArray() : null;
        var members = new List<PlaylistMember>();
        if (slc.Contents is { } contents)
            foreach (var item in contents.Items)
                members.Add(ToMember(item));
        return (members, rev);
    }

    static PlaylistMember ToMember(Pl.Item item)
    {
        string itemId = "";
        string? addedBy = null;
        long addedAt = 0;
        if (item.Attributes is { } a)
        {
            if (a.HasItemId) itemId = Convert.ToHexStringLower(a.ItemId.Span);   // stable per-row key (survives reorder)
            if (a.HasAddedBy) addedBy = a.AddedBy;
            if (a.HasTimestamp) addedAt = a.Timestamp;
        }
        return new PlaylistMember(itemId, item.Uri, addedBy, addedAt);
    }

    /// <summary>Map the playlist4 Ops onto the domain ops the applier understands.</summary>
    public static IReadOnlyList<PlaylistOp> MapOps(IEnumerable<Pl.Op> ops)
    {
        var list = new List<PlaylistOp>();
        foreach (var op in ops)
        {
            switch (op.Kind)
            {
                case Pl.Op.Types.Kind.Add when op.Add is { } add:
                    list.Add(new PlaylistOp(PlaylistOpKind.Add, FromIndex: add.FromIndex, AddFirst: add.AddFirst, AddLast: add.AddLast, Items: ToMembers(add.Items)));
                    break;
                case Pl.Op.Types.Kind.Rem when op.Rem is { } rem:
                    list.Add(rem.ItemsAsKey
                        ? new PlaylistOp(PlaylistOpKind.Remove, Items: ToMembers(rem.Items), ItemsAsKey: true)
                        : new PlaylistOp(PlaylistOpKind.Remove, FromIndex: rem.FromIndex, Length: rem.Length,
                            Items: rem.Items.Count > 0 ? ToMembers(rem.Items) : null));
                    break;
                case Pl.Op.Types.Kind.Mov when op.Mov is { } mov:
                    list.Add(new PlaylistOp(PlaylistOpKind.Move, FromIndex: mov.FromIndex, Length: mov.Length, ToIndex: mov.ToIndex));
                    break;
                case Pl.Op.Types.Kind.UpdateItemAttributes when op.UpdateItemAttributes is { } u:
                    list.Add(new PlaylistOp(PlaylistOpKind.UpdateItem, FromIndex: u.Index,
                        ItemPublic: u.NewAttributes?.Values is { HasPublic: true } v ? v.Public : null));
                    break;
                case Pl.Op.Types.Kind.UpdateListAttributes when op.UpdateListAttributes is { } u:
                    list.Add(new PlaylistOp(PlaylistOpKind.UpdateList, ListPatch: PatchOf(u.NewAttributes)));
                    break;
                case Pl.Op.Types.Kind.UpdateListAttributes:
                    list.Add(new PlaylistOp(PlaylistOpKind.UpdateList));
                    break;
            }
        }
        return list;
    }

    static IReadOnlyList<PlaylistMember> ToMembers(RepeatedField<Pl.Item> items)
    {
        var list = new List<PlaylistMember>(items.Count);
        foreach (var i in items) list.Add(ToMember(i));
        return list;
    }

    static PlaylistListAttributePatch? PatchOf(Pl.ListAttributesPartialState? s)
    {
        if (s is null) return null;
        string? name = null, desc = null;
        byte[]? picture = null;
        bool? collab = null;
        bool clearPic = false, clearName = false, clearDesc = false;
        if (s.Values is { } values)
        {
            if (values.HasName) name = values.Name;
            if (values.HasDescription) desc = values.Description;
            if (values.Picture.Length > 0) picture = values.Picture.ToByteArray();
            if (values.HasCollaborative) collab = values.Collaborative;
        }
        foreach (var nv in s.NoValue)
        {
            switch (nv)
            {
                case Pl.ListAttributeKind.ListPicture: clearPic = true; break;
                case Pl.ListAttributeKind.ListName: clearName = true; break;
                case Pl.ListAttributeKind.ListDescription: clearDesc = true; break;
                case Pl.ListAttributeKind.ListCollaborative: collab = false; break;
            }
        }
        if (name is null && desc is null && picture is null && collab is null && !clearPic && !clearName && !clearDesc)
            return new PlaylistListAttributePatch();
        return new PlaylistListAttributePatch(name, desc, picture, clearPic, collab, clearName, clearDesc);
    }

    static Pl.ListAttributesPartialState PartialOf(PlaylistListAttributePatch patch)
    {
        var partial = new Pl.ListAttributesPartialState();
        var hasValues = patch.Name is not null || patch.Description is not null || patch.PictureBytes is { Length: > 0 }
            || patch.Collaborative is not null;
        if (hasValues)
        {
            var v = new Pl.ListAttributes();
            if (patch.Name is not null) v.Name = patch.Name;
            if (patch.Description is not null) v.Description = patch.Description;
            if (patch.PictureBytes is { Length: > 0 }) v.Picture = ByteString.CopyFrom(patch.PictureBytes);
            if (patch.Collaborative is not null) v.Collaborative = patch.Collaborative.Value;
            partial.Values = v;
        }
        if (patch.ClearPicture) partial.NoValue.Add(Pl.ListAttributeKind.ListPicture);
        if (patch.ClearName) partial.NoValue.Add(Pl.ListAttributeKind.ListName);
        if (patch.ClearDescription) partial.NoValue.Add(Pl.ListAttributeKind.ListDescription);
        // Collaborative=false travels as values.collaborative=false (HasCollaborative round-trips it) — never ALSO as
        // a no_value entry; emitting both made emit/parse asymmetric.
        return partial;
    }

    static IReadOnlyList<PlaylistMember> AttrMember(Pl.ItemAttributesPartialState s)
    {
        var a = s.Values;
        string? addedBy = a.HasAddedBy ? a.AddedBy : null;
        long addedAt = a.HasTimestamp ? a.Timestamp : 0;
        return new[] { new PlaylistMember("", "", addedBy, addedAt) };   // carries only the changed attributes for UPDATE_ITEM
    }

    /// <summary>Serialize a create-empty-playlist body for POST <c>/playlist/v2/playlist</c>.</summary>
    public static byte[] BuildCreateListRequest(string name, string username, long nowMs)
    {
        var req = new Pl.ListUpdateRequest
        {
            Attributes = new Pl.ListAttributes { Name = name },
            Info = new Pl.ChangeInfo { User = username, Timestamp = nowMs },
        };
        return req.ToByteArray();
    }

    // ── write direction: domain ops → the ListChanges body POSTed to /playlist/v2/{path}/changes ──
    /// <summary>Serialize an edit (ops against a base revision) into the ListChanges wire body.</summary>
    public static byte[] BuildChanges(byte[]? baseRev, IReadOnlyList<PlaylistOp> ops)
        => BuildChanges(baseRev, ops, "", 0);

    /// <summary>Serialize a playlist edit with captured <see cref="Pl.ChangeInfo"/> and want-flags.</summary>
    public static byte[] BuildChanges(byte[]? baseRev, IReadOnlyList<PlaylistOp> ops, string username, long nowMs)
    {
        // Match the desktop client's /changes envelope: both result flags plus a positive request nonce. The sync result
        // makes the accepted edit authoritative on the response path; the nonce prevents a replayed request from being
        // applied twice by Spotify's playlist service.
        var changes = new Pl.ListChanges { WantResultingRevisions = true, WantSyncResult = true };
        var delta = new Pl.Delta();
        if (!string.IsNullOrEmpty(username) || nowMs > 0)
            delta.Info = new Pl.ChangeInfo { User = username, Timestamp = nowMs, Admin = true, Undo = true, Merge = true };
        if (baseRev is not null)
        {
            var rev = ByteString.CopyFrom(baseRev);
            changes.BaseRevision = rev;
            delta.BaseVersion = rev;
        }
        for (int i = 0; i < ops.Count; i++) delta.Ops.Add(ToWireOp(ops[i]));
        changes.Deltas.Add(delta);
        changes.Nonces.Add(System.Random.Shared.NextInt64(1, int.MaxValue));
        return changes.ToByteArray();
    }

    // ── §2.5/§2.7 — the rootlist ListChanges body (follow = ADD, unfollow = REM) ──
    /// <summary>Serialize a rootlist edit into the ListChanges wire body. Extends <see cref="BuildChanges"/> with
    /// <c>Delta.Info { User, Timestamp }</c>, <c>want_resulting_revisions</c> / <c>want_sync_result</c>, one random nonce,
    /// and <c>public=true</c> ItemAttributes on ADD items (the rootlist path; the timestamp rides the member's AddedAt).</summary>
    public static byte[] BuildRootlistChanges(byte[]? baseRev, IReadOnlyList<PlaylistOp> ops, string username, long nowMs)
    {
        var changes = new Pl.ListChanges { WantResultingRevisions = true, WantSyncResult = true };
        var delta = new Pl.Delta { Info = new Pl.ChangeInfo { User = username, Timestamp = nowMs } };
        if (baseRev is not null)
        {
            var rev = ByteString.CopyFrom(baseRev);
            changes.BaseRevision = rev;
            delta.BaseVersion = rev;
        }
        for (int i = 0; i < ops.Count; i++) delta.Ops.Add(ToWireOp(ops[i], rootlistPublic: true));
        changes.Deltas.Add(delta);
        changes.Nonces.Add(System.Random.Shared.NextInt64(1, int.MaxValue));   // positive int64 dedup nonce
        return changes.ToByteArray();
    }

    /// <summary>The resulting revision of a /changes (or bootstrap) response: the top-level <c>revision</c> when present,
    /// else the first <c>resulting_revisions</c> entry (§2.5 step 5 / §2.7).</summary>
    public static byte[]? ResultingRevision(Pl.SelectedListContent slc)
    {
        if (slc.HasRevision) return slc.Revision.ToByteArray();
        if (slc.ResultingRevisions.Count > 0) return slc.ResultingRevisions[0].ToByteArray();
        return null;
    }

    static Pl.Op ToWireOp(PlaylistOp op, bool rootlistPublic = false) => op.Kind switch
    {
        PlaylistOpKind.Add => new Pl.Op { Kind = Pl.Op.Types.Kind.Add, Add = BuildAdd(op, rootlistPublic) },
        PlaylistOpKind.Remove => new Pl.Op { Kind = Pl.Op.Types.Kind.Rem, Rem = BuildRem(op) },
        PlaylistOpKind.Move => new Pl.Op { Kind = Pl.Op.Types.Kind.Mov, Mov = new Pl.Mov { FromIndex = op.FromIndex, Length = op.Length, ToIndex = op.ToIndex } },
        PlaylistOpKind.UpdateList => new Pl.Op
        {
            Kind = Pl.Op.Types.Kind.UpdateListAttributes,
            UpdateListAttributes = op.ListPatch is { } patch
                ? new Pl.UpdateListAttributes { NewAttributes = PartialOf(patch) }
                : null,
        },
        PlaylistOpKind.UpdateItem => new Pl.Op
        {
            Kind = Pl.Op.Types.Kind.UpdateItemAttributes,
            UpdateItemAttributes = new Pl.UpdateItemAttributes
            {
                Index = op.FromIndex,
                NewAttributes = new Pl.ItemAttributesPartialState
                {
                    Values = new Pl.ItemAttributes { Public = op.ItemPublic ?? false },
                },
            },
        },
        _ => new Pl.Op { Kind = Pl.Op.Types.Kind.Unknown },
    };

    // Keyed REM (items_as_key, the rootlist-unfollow shape): remove by uri, order-independent — no FromIndex/Length, so the
    // server resolves the row regardless of local position drift from the optimistic edit. Index REM stays the edit path.
    static Pl.Rem BuildRem(PlaylistOp op)
    {
        if (!op.ItemsAsKey)
        {
            var rem = new Pl.Rem { FromIndex = op.FromIndex, Length = op.Length };
            if (op.Items is { Count: > 0 } items)
                for (int i = 0; i < items.Count; i++)
                    rem.Items.Add(new Pl.Item
                    {
                        Uri = items[i].ItemUri,
                        Attributes = string.IsNullOrEmpty(items[i].ItemId) ? null
                            : new Pl.ItemAttributes { ItemId = ByteString.CopyFrom(Convert.FromHexString(items[i].ItemId)) },
                    });
            return rem;
        }
        var keyed = new Pl.Rem { ItemsAsKey = true };
        if (op.Items is { } keyedItems)
            for (int i = 0; i < keyedItems.Count; i++) keyed.Items.Add(new Pl.Item { Uri = keyedItems[i].ItemUri });
        return keyed;
    }

    // ADD items carry ItemAttributes when the member supplies add facts (timestamp/added_by) — the reference sends a
    // timestamp on playlist-edit ADDs too — and additionally public=true on the rootlist path. A bare uri-only member
    // (AddedAt==0, AddedBy==null, non-rootlist) still emits a uri-only Item, preserving the plain BuildChanges behavior.
    static Pl.Add BuildAdd(PlaylistOp op, bool rootlistPublic = false)
    {
        var add = new Pl.Add { FromIndex = op.FromIndex, AddFirst = op.AddFirst, AddLast = op.AddLast };
        if (op.Items is { } items)
            for (int i = 0; i < items.Count; i++)
            {
                var m = items[i];
                var item = new Pl.Item { Uri = m.ItemUri };
                Pl.ItemAttributes? attrs = null;
                if (m.AddedAt > 0) (attrs ??= new Pl.ItemAttributes()).Timestamp = m.AddedAt;
                if (!string.IsNullOrEmpty(m.AddedBy)) (attrs ??= new Pl.ItemAttributes()).AddedBy = m.AddedBy;
                if (rootlistPublic) (attrs ??= new Pl.ItemAttributes()).Public = true;
                if (attrs is not null) item.Attributes = attrs;
                add.Items.Add(item);
            }
        return add;
    }

    /// <summary>Inverse of <see cref="BuildChanges"/> — parse a persisted ListChanges body back into (base revision, ops),
    /// for reloading a durable outbox edit.</summary>
    public static (byte[]? BaseRev, IReadOnlyList<PlaylistOp> Ops) ParseChanges(byte[] blob)
    {
        var changes = Pl.ListChanges.Parser.ParseFrom(blob);
        byte[]? baseRev = changes.HasBaseRevision ? changes.BaseRevision.ToByteArray() : null;
        var ops = new List<PlaylistOp>();
        foreach (var delta in changes.Deltas) ops.AddRange(MapOps(delta.Ops));
        return (baseRev, ops);
    }
}
