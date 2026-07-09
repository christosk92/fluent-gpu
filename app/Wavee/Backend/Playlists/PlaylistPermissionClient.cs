using System;

using System.Collections.Concurrent;

using System.Collections.Generic;

using System.Text;

using System.Text.Json;

using System.Threading;

using System.Threading.Tasks;

using Wavee.Core;



namespace Wavee.Backend.Playlists;



/// <summary>JSON GET/POST for <c>/playlist-permission/v1/playlist/{id}/permission/base</c> (revision-chained).</summary>

public sealed class PlaylistPermissionClient

{

    public const string DefaultRevisionSentinel = "ZGVmYXVsdA=="; // base64("default")



    readonly ITransport _transport;

    readonly ConcurrentDictionary<string, string> _revisionByUri = new(StringComparer.Ordinal);



    public PlaylistPermissionClient(ITransport transport) => _transport = transport;



    static Dictionary<string, string> JsonHeaders => new(StringComparer.OrdinalIgnoreCase)

    {

        ["Content-Type"] = "application/json",

        ["Accept"] = "application/json",

    };



    public async Task<PlaylistBasePermission?> GetBasePermissionAsync(string playlistUri, CancellationToken ct = default)

    {

        var id = IdOf(playlistUri);

        var r = await _transport.Request(Channel.Spclient,

            $"/playlist-permission/v1/playlist/{id}/permission/base",

            ReadOnlyMemory<byte>.Empty, ct, "GET", JsonHeaders).ConfigureAwait(false);

        if (!r.Ok)

        {

            PlaylistMutationDiagnostics.PermissionGetFailed(playlistUri, r.Status);

            return null;

        }

        var parsed = ParseBaseJson(r.Body);

        if (parsed is { } p) _revisionByUri[playlistUri] = p.Revision;

        return parsed;

    }



    public async Task<PlaylistBasePermission> SetBasePermissionAsync(

        string playlistUri, PlaylistPermissionLevel level, string? revision, CancellationToken ct = default)

    {

        var id = IdOf(playlistUri);

        revision ??= _revisionByUri.TryGetValue(playlistUri, out var known) ? known : DefaultRevisionSentinel;

        var body = Encoding.UTF8.GetBytes(

            $"{{\"revision\":\"{revision}\",\"permissionLevel\":\"{LevelWire(level)}\"}}");

        var r = await _transport.Request(Channel.Spclient,

            $"/playlist-permission/v1/playlist/{id}/permission/base",

            body, ct, "POST", JsonHeaders).ConfigureAwait(false);

        if (r.Status == 409)

        {

            PlaylistMutationDiagnostics.PermissionConflict(playlistUri);

            var fresh = await GetBasePermissionAsync(playlistUri, ct).ConfigureAwait(false);

            if (fresh is null) throw new InvalidOperationException($"permission base conflict ({r.Status})");

            return await SetBasePermissionAsync(playlistUri, level, fresh.Value.Revision, ct).ConfigureAwait(false);

        }

        if (!r.Ok)

        {

            PlaylistMutationDiagnostics.PermissionSetFailed(playlistUri, r.Status, level);

            throw new InvalidOperationException($"permission base failed ({r.Status})");

        }

        var result = ParsePostJson(r.Body) ?? throw new InvalidOperationException("permission base missing resultingPermission");

        _revisionByUri[playlistUri] = result.Revision;

        return result;

    }



    public void SeedRevision(string playlistUri, string revision) => _revisionByUri[playlistUri] = revision;



    static PlaylistBasePermission? ParseBaseJson(byte[] body)

    {

        using var doc = JsonDocument.Parse(body);

        var root = doc.RootElement;

        if (!root.TryGetProperty("permissionLevel", out var lvl)) return null;

        if (!root.TryGetProperty("revision", out var rev)) return null;

        return new PlaylistBasePermission(ParseLevel(lvl.GetString()), rev.GetString() ?? "");

    }



    static PlaylistBasePermission? ParsePostJson(byte[] body)

    {

        using var doc = JsonDocument.Parse(body);

        if (!doc.RootElement.TryGetProperty("resultingPermission", out var rp)) return null;

        if (!rp.TryGetProperty("permissionLevel", out var lvl)) return null;

        if (!rp.TryGetProperty("revision", out var rev)) return null;

        return new PlaylistBasePermission(ParseLevel(lvl.GetString()), rev.GetString() ?? "");

    }



    static PlaylistPermissionLevel ParseLevel(string? wire) => wire switch

    {

        "BLOCKED" => PlaylistPermissionLevel.Blocked,

        "VIEWER" => PlaylistPermissionLevel.Viewer,

        "CONTRIBUTOR" => PlaylistPermissionLevel.Contributor,

        _ => PlaylistPermissionLevel.Viewer,

    };



    static string LevelWire(PlaylistPermissionLevel level) => level switch

    {

        PlaylistPermissionLevel.Blocked => "BLOCKED",

        PlaylistPermissionLevel.Viewer => "VIEWER",

        PlaylistPermissionLevel.Contributor => "CONTRIBUTOR",

        _ => "VIEWER",

    };



    static string IdOf(string uri) { int i = uri.LastIndexOf(':'); return i >= 0 ? uri[(i + 1)..] : uri; }

}


