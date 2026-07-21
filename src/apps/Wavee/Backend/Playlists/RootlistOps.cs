using System;

using System.Collections.Generic;

using System.Threading;

using System.Threading.Tasks;

using Wavee.Backend.Spotify;

using Wavee.Core;

using Pl = Wavee.Protocol.Playlist;



namespace Wavee.Backend.Playlists;



/// <summary>Rootlist index lookup + POST /rootlist/changes helpers (visibility, delete, follow).</summary>

public static class RootlistOps

{

    public static int FindPlaylistIndex(IReadOnlyList<RootlistEntry> entries, string playlistUri)

    {

        for (int i = 0; i < entries.Count; i++)

        {

            var e = entries[i];

            if (e.Kind == 0 && string.Equals(e.Uri, playlistUri, StringComparison.Ordinal))

                return i;

        }

        return -1;

    }



    public static async Task<byte[]?> BootstrapRootlistAsync(

        IStore store, ITransport transport, SessionContext ctx, CancellationToken ct)

    {

        var route = $"/playlist/v2/user/{ctx.Account}/rootlist?decorate=revision";

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Accept"] = "application/protobuf" };

        var r = await transport.Request(Channel.Spclient, route, ReadOnlyMemory<byte>.Empty, ct, "GET", headers).ConfigureAwait(false);

        if (!r.Ok) return store.RootlistRevision();

        return ApplyRootlistResponse(store, r.Body);

    }



    public static byte[]? ApplyRootlistResponse(IStore store, byte[] body)

    {

        var bytes = SpotifyZstd.MaybeDecompressZstd(body);

        if (bytes.Length == 0) return store.RootlistRevision();

        Pl.SelectedListContent slc;

        try { slc = Pl.SelectedListContent.Parser.ParseFrom(bytes); }

        catch { return store.RootlistRevision(); }

        var rev = PlaylistWireMapper.ResultingRevision(slc);

        if (slc.Contents is { } contents && contents.Items.Count > 0)

        {

            var uris = new List<string>(contents.Items.Count);

            foreach (var it in contents.Items) uris.Add(it.Uri);

            store.SetRootlist(RootlistTreeBuilder.EntriesFromUris(uris), rev);

        }

        else if (rev is not null)

            store.SetRootlist(store.Rootlist(), rev);

        return rev ?? store.RootlistRevision();

    }



    /// <summary>POST rootlist ops; returns false on 409 after rebasing (caller may retry).</summary>

    public static async Task<bool> TryPostRootlistOpsAsync(
        IStore store, ITransport transport, SessionContext ctx,
        IReadOnlyList<PlaylistOp> ops, string? logUri, CancellationToken ct)
        => await TryPostRootlistOpsAsync(store, transport, () => "", ctx, ops, logUri, ct).ConfigureAwait(false);

    public static async Task<bool> TryPostRootlistOpsAsync(

        IStore store, ITransport transport, Func<string> spclientBaseUrl, SessionContext ctx,

        IReadOnlyList<PlaylistOp> ops, string? logUri, CancellationToken ct)

    {

        var rev = store.RootlistRevision();

        if (rev is null) rev = await BootstrapRootlistAsync(store, transport, ctx, ct).ConfigureAwait(false);

        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var body = PlaylistWireMapper.BuildRootlistChanges(rev, ops, ctx.Account, nowMs);

        var route = $"/playlist/v2/user/{ctx.Account}/rootlist/changes";

        var r = await transport.Request(Channel.Spclient, route, body, ct, "POST",

            headers: SpotifyHeaders.PlaylistV2Mutation(ctx.Locale, spclientBaseUrl())).ConfigureAwait(false);

        if (r.Ok) { ApplyRootlistResponse(store, r.Body); return true; }

        if (r.Status == 409)

        {

            await BootstrapRootlistAsync(store, transport, ctx, ct).ConfigureAwait(false);

            if (logUri is not null) PlaylistMutationDiagnostics.RootlistConflict(logUri);

            return false;

        }

        if (logUri is not null) PlaylistMutationDiagnostics.RootlistPostFailed(logUri, r.Status, ops[0].Kind.ToString());

        throw new InvalidOperationException($"rootlist changes failed ({r.Status})");

    }



    public static async Task PostRootlistOpsAsync(

        IStore store, ITransport transport, Func<string> spclientBaseUrl, SessionContext ctx,

        IReadOnlyList<PlaylistOp> ops, CancellationToken ct, string? logUri = null)

    {

        if (!await TryPostRootlistOpsAsync(store, transport, spclientBaseUrl, ctx, ops, logUri, ct).ConfigureAwait(false))

            throw new InvalidOperationException("rootlist revision conflict (retry)");

    }



    public static IReadOnlyList<RootlistEntry>? RemovePlaylistEntry(IReadOnlyList<RootlistEntry> cur, string uri)

    {

        int found = FindPlaylistIndex(cur, uri);

        if (found < 0) return null;

        var list = new List<RootlistEntry>(cur.Count - 1);

        for (int i = 0; i < cur.Count; i++) if (i != found) list.Add(cur[i]);

        for (int i = 0; i < list.Count; i++) list[i] = list[i] with { Position = i };

        return list;

    }

}


