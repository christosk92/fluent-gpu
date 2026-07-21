using System;
using System.Collections.Generic;
using Wavee.Backend.Persistence;
using Wavee.Core;

namespace Wavee.Backend.Playlists;

// Turns the flat rootlist marker stream (persisted as ordered ColdRootlistEntry rows) into the sidebar PlaylistNode tree.
// Markers: kind 0 = a playlist uri, kind 1 = start-group, kind 2 = end-group. The playlist header (name/cover/owner) is
// resolved from the shared Store via the injected resolver, so this stays pure + unit-testable.
public static class RootlistTreeBuilder
{
    public static IReadOnlyList<PlaylistNode> Build(IReadOnlyList<ColdRootlistEntry> entries, Func<string, PlaylistSummary> resolve)
    {
        var top = new List<PlaylistNode>();
        var open = new Stack<(string Id, string Name, List<PlaylistSummary> Items)>();

        foreach (var e in entries)
        {
            switch (e.Kind)
            {
                case 1:   // start-group
                    open.Push((GroupId(e.Uri), e.GroupName ?? "", new List<PlaylistSummary>()));
                    break;

                case 2:   // end-group
                    if (open.Count > 0)
                    {
                        var f = open.Pop();
                        // Sidebar's folder model is one level deep: a nested folder's playlists flatten into its parent.
                        if (open.Count > 0) open.Peek().Items.AddRange(f.Items);
                        else top.Add(new PlaylistFolder(f.Id, f.Name, f.Items));
                    }
                    break;

                default:  // a playlist (or any item) uri
                    if (e.Uri.StartsWith("spotify:playlist:", StringComparison.Ordinal))
                    {
                        var ps = resolve(e.Uri);
                        if (open.Count > 0) open.Peek().Items.Add(ps);
                        else top.Add(new PlaylistLeaf(ps));
                    }
                    break;
            }
        }

        // Unbalanced markers (a missing end-group) must not swallow the folder + its children — flush what's still open.
        while (open.Count > 0)
        {
            var f = open.Pop();
            top.Add(new PlaylistFolder(f.Id, f.Name, f.Items));
        }
        return top;
    }

    // "spotify:start-group:{id}:{name}" / "spotify:end-group:{id}" → the {id} segment.
    static string GroupId(string uri)
    {
        var parts = uri.Split(':');
        return parts.Length >= 3 ? parts[2] : uri;
    }

    // ── the ONE home for the flat rootlist marker → ordered RootlistEntry parse ──
    // A rootlist is a playlist whose items are playlist-uri rows interleaved with start-group / end-group markers. Both the
    // full-fetch path (PlaylistFetcher) and the in-place / write-response paths (LibrarySync, RootlistFollowStrategy) build
    // the same ordered rows from a bare uri sequence — so the marker parsing lives here once (kind 0=item, 1=start, 2=end;
    // depth tracks nesting; Position is the flat item index, which the rootlist-changes REM op indexes against).
    public static IReadOnlyList<RootlistEntry> EntriesFromUris(IEnumerable<string> uris)
    {
        var entries = new List<RootlistEntry>();
        int pos = 0, depth = 0;
        foreach (var uri in uris)
        {
            if (uri.StartsWith("spotify:start-group:", StringComparison.Ordinal)) { entries.Add(new RootlistEntry(pos++, 1, uri, GroupNameOf(uri), depth)); depth++; }
            else if (uri.StartsWith("spotify:end-group:", StringComparison.Ordinal)) { depth = Math.Max(0, depth - 1); entries.Add(new RootlistEntry(pos++, 2, uri, null, depth)); }
            else entries.Add(new RootlistEntry(pos++, 0, uri, null, depth));
        }
        return entries;
    }

    // "spotify:start-group:{id}:{name}" → the (url-decoded) {name} segment.
    static string? GroupNameOf(string uri) { var p = uri.Split(':'); return p.Length >= 4 ? Uri.UnescapeDataString(p[3]) : null; }
}
