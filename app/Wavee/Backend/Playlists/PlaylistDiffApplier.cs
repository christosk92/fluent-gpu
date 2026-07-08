using System;
using System.Collections.Generic;
using Wavee.Core;

namespace Wavee.Backend.Playlists;

// ── Incremental playlist sync — the pure ordered-membership op model + applier ────────────────────────────────────────
// Proto-free domain logic: the SpotifyLive layer maps the playlist4 `Op` protos onto these, so this applier stays unit-
// testable without a wire. A playlist's membership is an ordered list keyed by a stable per-row ItemId (a uri may repeat
// and positions drift), which is why remove/reorder rebase keys on ItemId, never on raw indices.

/// <summary>One ordered membership row: the stable per-row <see cref="ItemId"/>, the referenced entity
/// <see cref="ItemUri"/> (joined to the shared Store entity at read), and the per-membership add facts.</summary>
public readonly record struct PlaylistMember(string ItemId, string ItemUri, string? AddedBy, long AddedAt);

/// <summary>List-level attribute patch carried by <see cref="PlaylistOpKind.UpdateList"/> — name, description, cover,
/// collaborative toggle, or per-field clears.</summary>
public sealed record PlaylistListAttributePatch(
    string? Name = null, string? Description = null,
    byte[]? PictureBytes = null, bool ClearPicture = false, bool? Collaborative = null,
    bool ClearName = false, bool ClearDescription = false);

public enum PlaylistOpKind { Add, Remove, Move, UpdateItem, UpdateList }

/// <summary>A single change to the ordered membership. Index fields are interpreted against the list state produced by
/// all PRECEDING ops in the same batch (the reference /diff semantics).</summary>
public sealed record PlaylistOp(
    PlaylistOpKind Kind,
    int FromIndex = 0,                          // ADD insertion index (when not add_first/add_last) / REM start / MOV source / UPDATE_ITEM index
    int Length = 0,                             // REM count / MOV count
    int ToIndex = 0,                            // MOV destination: pre-removal wire index
    bool AddFirst = false,                      // ADD at head
    bool AddLast = false,                       // ADD at tail
    IReadOnlyList<PlaylistMember>? Items = null,    // ADD payload / UPDATE_ITEM attribute carrier / keyed-REM uris
    bool ItemsAsKey = false,                    // REM by Items' uris instead of index (order-independent — the rootlist unfollow shape)
    PlaylistListAttributePatch? ListPatch = null);   // UPDATE_LIST metadata payload

public static class PlaylistDiffApplier
{
    /// <summary>Applies <paramref name="ops"/> in order to <paramref name="list"/> in place. Throws
    /// <see cref="ArgumentOutOfRangeException"/> on any positional op that doesn't fit the current list — the caller
    /// treats that as a torn apply and falls back to a full re-fetch (the reference's behavior).</summary>
    public static void Apply(List<PlaylistMember> list, IReadOnlyList<PlaylistOp> ops)
    {
        for (int i = 0; i < ops.Count; i++)
        {
            var op = ops[i];
            switch (op.Kind)
            {
                case PlaylistOpKind.Add: ApplyAdd(list, op); break;
                case PlaylistOpKind.Remove: ApplyRemove(list, op); break;
                case PlaylistOpKind.Move: ApplyMove(list, op); break;
                case PlaylistOpKind.UpdateItem: ApplyUpdateItem(list, op); break;
                case PlaylistOpKind.UpdateList: break;   // list-level attrs (name/description) belong to the header, not the membership
            }
        }
    }

    static void ApplyAdd(List<PlaylistMember> list, PlaylistOp op)
    {
        var items = op.Items ?? Array.Empty<PlaylistMember>();
        int at = op.AddFirst ? 0 : op.AddLast ? list.Count : op.FromIndex;   // add_first > add_last > from_index
        if (at < 0 || at > list.Count) throw new ArgumentOutOfRangeException(nameof(op), $"ADD index {at} out of range [0,{list.Count}]");
        list.InsertRange(at, items);
    }

    static void ApplyRemove(List<PlaylistMember> list, PlaylistOp op)
    {
        // Keyed REM (items_as_key): remove the first matching row per item by uri, absent = no-op. Built for the outbound
        // rootlist-unfollow wire body; handled here too so the applier stays total if a keyed REM ever arrives in a diff.
        if (op.ItemsAsKey)
        {
            if (op.Items is not { } keys) return;
            for (int k = 0; k < keys.Count; k++)
                for (int i = 0; i < list.Count; i++)
                    if (list[i].ItemUri == keys[k].ItemUri) { list.RemoveAt(i); break; }
            return;
        }
        if (op.FromIndex < 0 || op.Length < 0 || op.FromIndex + op.Length > list.Count)
            throw new ArgumentOutOfRangeException(nameof(op), $"REM [{op.FromIndex},+{op.Length}] out of range (count {list.Count})");
        if (op.Items is { Count: > 0 } items && items.Count == op.Length)
        {
            for (int k = 0; k < items.Count; k++)
                if (!SameMember(list[op.FromIndex + k], items[k]))
                    throw new ArgumentOutOfRangeException(nameof(op), $"REM item mismatch at {op.FromIndex + k}");
        }
        list.RemoveRange(op.FromIndex, op.Length);
    }

    static void ApplyMove(List<PlaylistMember> list, PlaylistOp op)
    {
        if (op.FromIndex < 0 || op.Length < 0 || op.FromIndex + op.Length > list.Count)
            throw new ArgumentOutOfRangeException(nameof(op), $"MOV source [{op.FromIndex},+{op.Length}] out of range (count {list.Count})");
        if (op.ToIndex < 0 || op.ToIndex > list.Count)
            throw new ArgumentOutOfRangeException(nameof(op), $"MOV dest {op.ToIndex} out of range [0,{list.Count}]");
        var moved = list.GetRange(op.FromIndex, op.Length);
        list.RemoveRange(op.FromIndex, op.Length);
        int at = op.ToIndex > op.FromIndex ? op.ToIndex - op.Length : op.ToIndex;
        if (at < 0)
            throw new ArgumentOutOfRangeException(nameof(op), $"MOV dest {op.ToIndex} inside moved range");
        list.InsertRange(at, moved);
    }

    static bool SameMember(PlaylistMember actual, PlaylistMember expected)
        => !string.IsNullOrEmpty(actual.ItemId) && !string.IsNullOrEmpty(expected.ItemId)
            ? string.Equals(actual.ItemId, expected.ItemId, StringComparison.Ordinal)
            : string.Equals(actual.ItemUri, expected.ItemUri, StringComparison.Ordinal);

    static void ApplyUpdateItem(List<PlaylistMember> list, PlaylistOp op)
    {
        if (op.FromIndex < 0 || op.FromIndex >= list.Count) throw new ArgumentOutOfRangeException(nameof(op), $"UPDATE_ITEM index {op.FromIndex} out of range (count {list.Count})");
        if (op.Items is { Count: > 0 } items)
        {
            var updated = items[0];
            list[op.FromIndex] = list[op.FromIndex] with { AddedBy = updated.AddedBy, AddedAt = updated.AddedAt };
        }
    }
}
