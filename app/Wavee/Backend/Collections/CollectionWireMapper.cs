using System.Collections.Generic;
using Google.Protobuf.Collections;
using Col = Wavee.Protocol.Collection;

namespace Wavee.Backend.Collections;

// The SpotifyLive boundary mapper: collection2v2 wire protos → the proto-free domain (CollectionDelta). Unit-tested
// against crafted protos. A full page and an incremental delta map identically (both carry items + a sync token).
public static class CollectionWireMapper
{
    public static CollectionDelta ParseDelta(string setId, Col.DeltaResponse resp)
        => new(setId, NullIfEmpty(resp.SyncToken), Map(resp.Items));

    public static CollectionDelta ParsePage(string setId, Col.PageResponse resp)
        => new(setId, NullIfEmpty(resp.SyncToken), Map(resp.Items));

    static IReadOnlyList<CollectionItem> Map(RepeatedField<Col.CollectionItem> items)
    {
        var list = new List<CollectionItem>(items.Count);
        foreach (var it in items) list.Add(new CollectionItem(it.Uri, it.IsRemoved, it.AddedAt));
        return list;
    }

    static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;
}
