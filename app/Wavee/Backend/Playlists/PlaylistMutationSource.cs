using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Spotify;
using Wavee.Core;

namespace Wavee.Backend.Playlists;

/// <summary>Real Spotify playlist editing backed by <see cref="MutationEngine.Edit"/> and direct HTTP for cover/permission.</summary>
public sealed class PlaylistMutationSource : IPlaylistMutationSource
{
    readonly MutationEngine _mut;
    readonly ITransport _transport;
    readonly IStore? _store;
    readonly PlaylistPermissionClient _permissions;
    IHttpExchange _http;
    readonly Func<SessionContext> _ctx;
    readonly Func<string> _spclientBaseUrl;
    readonly UserPlaylistSource _local;

    /// <summary>Set at go-live (§6): routes post-write drains through LibrarySync (same as <see cref="EngineMutationSource.ScheduleDrain"/>).</summary>
    public Action? ScheduleDrain { get; set; }

    public PlaylistMutationSource(
        MutationEngine mut, ITransport transport, IHttpExchange http, Func<SessionContext> ctx,
        Func<string> spclientBaseUrl, UserPlaylistSource local, IStore? store = null)
        => (_mut, _transport, _http, _ctx, _spclientBaseUrl, _local, _store, _permissions) =
            (mut, transport, http, ctx, spclientBaseUrl, local, store, new PlaylistPermissionClient(transport));

    public void SetHttp(IHttpExchange http) => _http = http;

    public Task AddTracksAsync(string playlistUri, IReadOnlyList<Track> tracks, CancellationToken ct = default)
    {
        if (IsLocal(playlistUri)) { foreach (var t in tracks) _local.AddTrack(playlistUri, t); return Task.CompletedTask; }
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var members = new List<PlaylistMember>(tracks.Count);
        for (int i = 0; i < tracks.Count; i++)
            members.Add(new PlaylistMember("", tracks[i].Uri, _ctx().Account, now));
        EnqueueEdit(playlistUri, new PlaylistOp(PlaylistOpKind.Add, AddLast: true, Items: members));
        return DrainAsync(ct);
    }

    public Task RemoveRowsAsync(string playlistUri, IReadOnlyList<PlaylistRowRef> rows, CancellationToken ct = default)
    {
        if (IsLocal(playlistUri)) throw new NotSupportedException($"Local playlist row removal is not implemented (uri={playlistUri}).");
        var ops = new List<PlaylistOp>(rows.Count);
        for (int i = rows.Count - 1; i >= 0; i--)
        {
            var r = rows[i];
            ops.Add(new PlaylistOp(PlaylistOpKind.Remove, FromIndex: r.Index, Length: 1,
                Items: new[] { new PlaylistMember(r.ItemId, r.Uri, null, 0) }));
        }
        EnqueueEdit(playlistUri, ops.ToArray());
        return DrainAsync(ct);
    }

    public Task MoveRowsAsync(string playlistUri, IReadOnlyList<PlaylistRowRef> rows, int toIndex, CancellationToken ct = default)
    {
        if (IsLocal(playlistUri)) throw new NotSupportedException($"Local playlist row reordering is not implemented (uri={playlistUri}).");
        if (rows.Count == 0) return Task.CompletedTask;
        var ops = BuildMoveOps(rows, toIndex);
        if (ops.Count == 0) return Task.CompletedTask;
        EnqueueEdit(playlistUri, ops);
        return DrainAsync(ct);
    }

    /// <summary>Decomposes a (possibly non-contiguous) selection move into sequential MOV ops (one per contiguous run),
    /// each computed against the list state AFTER the preceding ops — matching <c>PlaylistDiffApplier.ApplyMove</c>'s
    /// sequential semantics. <paramref name="toIndex"/> is the pre-move insertion index ("insert before the row currently
    /// at this index"; list length = append). A single-op fast path would silently move the WRONG rows for gapped
    /// selections, so we simulate: build the desired final order over a virtual prefix, then emit first-mismatch moves.</summary>
    public static IReadOnlyList<PlaylistOp> BuildMoveOps(IReadOnlyList<PlaylistRowRef> rows, int toIndex)
    {
        var selected = new SortedSet<int>();
        foreach (var r in rows)
        {
            if (r.Index < 0) throw new ArgumentOutOfRangeException(nameof(rows), $"negative row index {r.Index}");
            selected.Add(r.Index);
        }
        if (toIndex < 0) throw new ArgumentOutOfRangeException(nameof(toIndex));

        int maxSel = 0; foreach (int i in selected) maxSel = Math.Max(maxSel, i);
        int len = Math.Max(maxSel + 1, toIndex);

        var final = new int[len];
        int w = 0;
        for (int i = 0; i < len && w < len; i++)
            if (!selected.Contains(i) && i < toIndex) final[w++] = i;
        foreach (int i in selected) final[w++] = i;
        for (int i = 0; i < len && w < len; i++)
            if (!selected.Contains(i) && i >= toIndex) final[w++] = i;

        var sim = new List<int>(len);
        for (int i = 0; i < len; i++) sim.Add(i);
        var ops = new List<PlaylistOp>();
        for (int pos = 0; pos < len; pos++)
        {
            if (sim[pos] == final[pos]) continue;
            int cur = sim.IndexOf(final[pos]);
            int runLen = 1;
            while (pos + runLen < len && cur + runLen < len && sim[cur + runLen] == final[pos + runLen]) runLen++;
            ops.Add(new PlaylistOp(PlaylistOpKind.Move, FromIndex: cur, Length: runLen, ToIndex: pos));
            var moved = sim.GetRange(cur, runLen);
            sim.RemoveRange(cur, runLen);
            sim.InsertRange(pos, moved);
        }
        return ops;
    }

    public Task UpdateDetailsAsync(string playlistUri, string? name, string? description, bool? collaborative, CancellationToken ct = default)
    {
        if (IsLocal(playlistUri)) throw new NotSupportedException($"Local playlist metadata editing is not implemented (uri={playlistUri}).");
        var patch = new PlaylistListAttributePatch(Name: name, Description: description, Collaborative: collaborative);
        EnqueueEdit(playlistUri, new PlaylistOp(PlaylistOpKind.UpdateList, ListPatch: patch));
        return DrainAsync(ct);
    }

    public async Task SetCoverJpegAsync(string playlistUri, byte[] jpeg, CancellationToken ct = default)
    {
        if (IsLocal(playlistUri)) throw new NotSupportedException($"Local playlist covers are not implemented (uri={playlistUri}).");
        var id = IdOf(playlistUri);
        var uploadUrl = "https://image-upload.spotify.com/v4/playlist";
        var uploadHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "image/jpeg",
            ["Accept"] = "application/json",
        };
        using var uploadResp = await _http.SendAsync(new HttpReq("POST", uploadUrl, uploadHeaders, jpeg), ct).ConfigureAwait(false);
        if (uploadResp.Status is < 200 or >= 300) throw new InvalidOperationException($"cover upload failed ({uploadResp.Status})");
        using var uploadMs = new System.IO.MemoryStream();
        await uploadResp.Body.CopyToAsync(uploadMs, ct).ConfigureAwait(false);
        var uploadJson = JsonDocument.Parse(uploadMs.ToArray());
        var uploadToken = uploadJson.RootElement.GetProperty("uploadToken").GetString()
            ?? throw new InvalidOperationException("cover upload missing uploadToken");

        var registerBody = Encoding.UTF8.GetBytes($"{{\"uploadToken\":\"{uploadToken}\"}}");
        var registerHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/json",
            ["Accept"] = "application/json",
        };
        var reg = await _transport.Request(Channel.SpclientWg, $"/playlist/v2/playlist/{id}/register-image", registerBody, ct, "POST", registerHeaders).ConfigureAwait(false);
        if (!reg.Ok) throw new InvalidOperationException($"register-image failed ({reg.Status})");
        var regJson = JsonDocument.Parse(reg.Body);
        var pictureB64 = regJson.RootElement.GetProperty("picture").GetString() ?? "";
        var pictureBytes = Convert.FromBase64String(pictureB64);
        EnqueueEdit(playlistUri, new PlaylistOp(PlaylistOpKind.UpdateList, ListPatch: new PlaylistListAttributePatch(PictureBytes: pictureBytes)));
        await DrainAsync(ct).ConfigureAwait(false);
    }

    public Task ClearCoverAsync(string playlistUri, CancellationToken ct = default)
    {
        if (IsLocal(playlistUri)) throw new NotSupportedException($"Local playlist covers are not implemented (uri={playlistUri}).");
        EnqueueEdit(playlistUri, new PlaylistOp(PlaylistOpKind.UpdateList, ListPatch: new PlaylistListAttributePatch(ClearPicture: true)));
        return DrainAsync(ct);
    }

    public async Task SetBasePermissionAsync(string playlistUri, PlaylistPermissionLevel level, CancellationToken ct = default)
    {
        if (IsLocal(playlistUri)) throw new NotSupportedException($"Local playlists have no permissions (uri={playlistUri}).");
        await _permissions.SetBasePermissionAsync(playlistUri, level, revision: null, ct).ConfigureAwait(false);
    }

    public Task<PlaylistBasePermission?> GetBasePermissionAsync(string playlistUri, CancellationToken ct = default)
    {
        if (IsLocal(playlistUri)) return Task.FromResult<PlaylistBasePermission?>(null);
        return _permissions.GetBasePermissionAsync(playlistUri, ct);
    }

    public async Task SetPlaylistVisibilityAsync(string playlistUri, bool isPublic, CancellationToken ct = default)
    {
        if (IsLocal(playlistUri)) throw new NotSupportedException($"Local playlists have no visibility (uri={playlistUri}).");
        RequireStore();
        var level = isPublic ? PlaylistPermissionLevel.Viewer : PlaylistPermissionLevel.Blocked;
        var resulting = await _permissions.SetBasePermissionAsync(playlistUri, level, revision: null, ct).ConfigureAwait(false);
        PatchPlaylistVisibility(playlistUri, isPublic, resulting.Revision);
        try { await TryRootlistPublicFlagAsync(playlistUri, isPublic, ct).ConfigureAwait(false); }
        catch { /* best-effort — permission already committed */ }
    }

    public async Task DeletePlaylistAsync(string playlistUri, CancellationToken ct = default)
    {
        if (IsLocal(playlistUri)) throw new NotSupportedException($"Local playlist delete is not implemented (uri={playlistUri}).");
        RequireStore();
        var store = _store!;
        var ctx = _ctx();
        int index = RootlistOps.FindPlaylistIndex(store.Rootlist(), playlistUri);
        if (index < 0)
        {
            await RootlistOps.BootstrapRootlistAsync(store, _transport, ctx, ct).ConfigureAwait(false);
            index = RootlistOps.FindPlaylistIndex(store.Rootlist(), playlistUri);
        }
        if (index < 0) throw new InvalidOperationException($"Playlist '{playlistUri}' is not in the user's rootlist.");
        var rem = new PlaylistOp(PlaylistOpKind.Remove, FromIndex: index, Length: 1);
        try
        {
            await RootlistOps.PostRootlistOpsAsync(store, _transport, _spclientBaseUrl, ctx, new[] { rem }, ct, playlistUri)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PlaylistMutationDiagnostics.DeleteFailed(playlistUri, ex);
            throw;
        }
        store.SetSaved("playlists", playlistUri, false, SyncState.Confirmed);
        store.Bump("rootlist", CollectionKind.Playlists);
    }

    public async Task<string> CreateContributorInviteAsync(string playlistUri, CancellationToken ct = default)
    {
        if (IsLocal(playlistUri)) throw new NotSupportedException($"Local playlists have no invites (uri={playlistUri}).");
        await EnsureCollaborativeForInviteAsync(playlistUri, ct).ConfigureAwait(false);
        var id = IdOf(playlistUri);
        // permission-grant wants permissionLevel NESTED under "permission" (not flat) — a flat body is rejected 400 (empty).
        var json = Encoding.UTF8.GetBytes("{\"permission\":{\"permissionLevel\":\"CONTRIBUTOR\"},\"ttlMs\":604800000}");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/json",
            ["Accept"] = "application/json",
        };
        var r = await _transport.Request(Channel.Spclient, $"/playlist-permission/v1/playlist/{id}/permission-grant", json, ct, "POST", headers).ConfigureAwait(false);
        if (!r.Ok)
        {
            PlaylistMutationDiagnostics.PermissionGrantFailed(playlistUri, r.Status);
            throw new InvalidOperationException($"permission grant failed ({r.Status})");
        }
        var doc = JsonDocument.Parse(r.Body);
        return doc.RootElement.GetProperty("token").GetString() ?? "";
    }

    async Task EnsureCollaborativeForInviteAsync(string playlistUri, CancellationToken ct)
    {
        bool needsCollab = _store?.GetPlaylist(playlistUri) is not { Capabilities.IsCollaborative: true };
        if (!needsCollab) return;
        await UpdateDetailsAsync(playlistUri, null, null, true, ct).ConfigureAwait(false);
    }

    void PatchPlaylistVisibility(string playlistUri, bool isPublic, string revision)
    {
        if (_store?.GetPlaylist(playlistUri) is not { } header) return;
        _store.UpsertPlaylist(header with { IsPublic = isPublic, BasePermissionRevision = revision });
    }

    async Task TryRootlistPublicFlagAsync(string playlistUri, bool isPublic, CancellationToken ct)
    {
        var store = _store!;
        var ctx = _ctx();
        int index = RootlistOps.FindPlaylistIndex(store.Rootlist(), playlistUri);
        if (index < 0)
        {
            await RootlistOps.BootstrapRootlistAsync(store, _transport, ctx, ct).ConfigureAwait(false);
            index = RootlistOps.FindPlaylistIndex(store.Rootlist(), playlistUri);
        }
        if (index < 0) return;
        var op = new PlaylistOp(PlaylistOpKind.UpdateItem, FromIndex: index, ItemPublic: isPublic);
        await RootlistOps.PostRootlistOpsAsync(store, _transport, _spclientBaseUrl, ctx, new[] { op }, ct, playlistUri).ConfigureAwait(false);
    }

    void RequireStore()
    {
        if (_store is null) throw new InvalidOperationException("Playlist visibility/delete requires the persistent store.");
    }

    void EnqueueEdit(string playlistUri, params PlaylistOp[] ops) => _mut.Edit(playlistUri, ops);
    void EnqueueEdit(string playlistUri, IReadOnlyList<PlaylistOp> ops) => _mut.Edit(playlistUri, ops);

    async Task DrainAsync(CancellationToken ct)
    {
        if (ScheduleDrain is { } viaLoop) { viaLoop(); return; }
        await _mut.Drain(_transport, _ctx(), ct).ConfigureAwait(false);
    }

    static bool IsLocal(string uri) => uri.StartsWith("wavee:playlist:", StringComparison.Ordinal);
    static string IdOf(string uri) { int i = uri.LastIndexOf(':'); return i >= 0 ? uri[(i + 1)..] : uri; }
}
