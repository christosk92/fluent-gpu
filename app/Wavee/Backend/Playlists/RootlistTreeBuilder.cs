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
}
