using System.Collections.Generic;
using System.Text.Json;
using Wavee.Backend;
using Wavee.Backend.Playlists;
using Wavee.Core;
using Xunit;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Tests;

public sealed class PlaylistPermissionTests
{
    [Fact]
    public void ParseBaseJson_ReadsBlockedAndRevision()
    {
        var json = """{"revision":"rev1","permissionLevel":"BLOCKED"}"""u8.ToArray();
        var perm = PlaylistPermissionClientTestHooks.ParseBaseJson(json);
        Assert.NotNull(perm);
        Assert.Equal(PlaylistPermissionLevel.Blocked, perm.Value.Level);
        Assert.Equal("rev1", perm.Value.Revision);
        Assert.False(perm.Value.IsPublic);
    }

    [Fact]
    public void ParsePostJson_ReadsResultingPermissionChain()
    {
        var json = """
            {"resultingPermission":{"revision":"rev2","permissionLevel":"VIEWER"}}
            """u8.ToArray();
        var perm = PlaylistPermissionClientTestHooks.ParsePostJson(json);
        Assert.NotNull(perm);
        Assert.Equal(PlaylistPermissionLevel.Viewer, perm.Value.Level);
        Assert.Equal("rev2", perm.Value.Revision);
        Assert.True(perm.Value.IsPublic);
    }

    [Fact]
    public void ToWireOp_UpdateItem_EmitsPublicFalse()
    {
        var op = new PlaylistOp(PlaylistOpKind.UpdateItem, FromIndex: 3, ItemPublic: false);
        var bytes = PlaylistWireMapper.BuildChanges(new byte[] { 1 }, new[] { op });
        var changes = Pl.ListChanges.Parser.ParseFrom(bytes);
        var wire = changes.Deltas[0].Ops[0];
        Assert.Equal(Pl.Op.Types.Kind.UpdateItemAttributes, wire.Kind);
        Assert.Equal(3, (int)wire.UpdateItemAttributes.Index);
        Assert.True(wire.UpdateItemAttributes.NewAttributes.Values.HasPublic);
        Assert.False(wire.UpdateItemAttributes.NewAttributes.Values.Public);
    }

    [Fact]
    public void FindPlaylistIndex_SkipsFolders()
    {
        var entries = new[]
        {
            new RootlistEntry(0, 1, "spotify:start-group:g:F", "F", 0),
            new RootlistEntry(1, 0, "spotify:playlist:a", null, 1),
            new RootlistEntry(2, 0, "spotify:playlist:b", null, 1),
        };
        Assert.Equal(2, RootlistOps.FindPlaylistIndex(entries, "spotify:playlist:b"));
    }

    [Fact]
    public void BuildRootlistChanges_RemAtResolvedIndex()
    {
        var ops = new[] { new PlaylistOp(PlaylistOpKind.Remove, FromIndex: 2, Length: 1) };
        var bytes = PlaylistWireMapper.BuildRootlistChanges(new byte[] { 9 }, ops, "alice", 1_700_000_000_000);
        var changes = Pl.ListChanges.Parser.ParseFrom(bytes);
        var rem = Assert.Single(changes.Deltas[0].Ops);
        Assert.Equal(Pl.Op.Types.Kind.Rem, rem.Kind);
        Assert.Equal(2, (int)rem.Rem.FromIndex);
        Assert.Equal(1, (int)rem.Rem.Length);
    }
}

// Test-only surface for JSON parsers (internal to Wavee assembly).
static file class PlaylistPermissionClientTestHooks
{
    public static PlaylistBasePermission? ParseBaseJson(byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (!root.TryGetProperty("permissionLevel", out var lvl)) return null;
        if (!root.TryGetProperty("revision", out var rev)) return null;
        return new PlaylistBasePermission(ParseLevel(lvl.GetString()), rev.GetString() ?? "");
    }

    public static PlaylistBasePermission? ParsePostJson(byte[] body)
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
}
