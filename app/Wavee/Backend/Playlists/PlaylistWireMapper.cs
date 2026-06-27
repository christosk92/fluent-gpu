using System;
using System.Collections.Generic;
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
                    list.Add(new PlaylistOp(PlaylistOpKind.Remove, FromIndex: rem.FromIndex, Length: rem.Length));
                    break;
                case Pl.Op.Types.Kind.Mov when op.Mov is { } mov:
                    list.Add(new PlaylistOp(PlaylistOpKind.Move, FromIndex: mov.FromIndex, Length: mov.Length, ToIndex: mov.ToIndex));
                    break;
                case Pl.Op.Types.Kind.UpdateItemAttributes when op.UpdateItemAttributes is { } u:
                    list.Add(new PlaylistOp(PlaylistOpKind.UpdateItem, FromIndex: u.Index, Items: AttrMember(u.NewAttributes)));
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

    static IReadOnlyList<PlaylistMember> AttrMember(Pl.ItemAttributesPartialState s)
    {
        var a = s.Values;
        string? addedBy = a.HasAddedBy ? a.AddedBy : null;
        long addedAt = a.HasTimestamp ? a.Timestamp : 0;
        return new[] { new PlaylistMember("", "", addedBy, addedAt) };   // carries only the changed attributes for UPDATE_ITEM
    }
}
