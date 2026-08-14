using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Wavee.Core;
using Wavee.Core.Home;
using Xunit;

namespace Wavee.Tests;

// Home layout: reducer (hide/reorder/cap), DTO round-trip, unknown-field/kind carry, and store fail-soft
// (corrupt file is never overwritten until the first successful save after DiscardCorrupt).
public sealed class HomeLayoutTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "wavee-home-layout-tests", Guid.NewGuid().ToString("n"));
    readonly string _path;

    public HomeLayoutTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "home-layout.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { }
    }

    HomeLayoutStore Store() => new(_path);

    static HomeLayoutDocDto Envelope(HomeLayoutDoc layout, HomeLayoutWireCarry? carry = null)
        => HomeLayoutWire.Write(layout, carry);

    void CommitAndWait(HomeLayoutStore store, HomeLayoutDocDto doc)
    {
        store.Commit(doc);
        Assert.True(store.WaitForWrites(10_000), "the pool write did not finish inside 10 s");
    }

    static HomeCard Card(string id) => new(
        "spotify:playlist:" + id, id, null, null, HomeCardKind.Playlist);

    static HomeFeed FeedWith(params HomeGroup[] groups) => new("", groups);

    // ── reducer ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Hide_OmitsKindFromVisibleOrder_AndIsNoChangeWhenAlreadyHidden()
    {
        var layout = HomeLayoutDoc.Default;
        var hidden = HomeLayoutReducer.Apply(layout, new SetHomeModuleHidden(HomeGroupKind.Hero, true));
        Assert.True(hidden.Changed);
        Assert.True(hidden.Layout.IsHidden(HomeGroupKind.Hero));
        Assert.DoesNotContain(HomeGroupKind.Hero, hidden.Layout.VisibleFixedModules());

        var again = HomeLayoutReducer.Apply(hidden.Layout, new SetHomeModuleHidden(HomeGroupKind.Hero, true));
        Assert.False(again.Changed);
        Assert.Equal(HomeLayoutRejectReason.NoChange, again.Reason);
    }

    [Fact]
    public void Reorder_UsesPostRemovalIndex_AndRejectsANoOp()
    {
        var layout = HomeLayoutDoc.Default;
        int from = layout.IndexOf(HomeGroupKind.Hero);
        var moved = HomeLayoutReducer.Apply(layout, new MoveHomeModule(from, 3));
        Assert.True(moved.Changed);
        Assert.Equal(HomeGroupKind.Hero, moved.Layout.Modules[3].Kind);

        var noop = HomeLayoutReducer.Apply(layout, new MoveHomeModule(from, from));
        Assert.False(noop.Changed);
        Assert.Equal(HomeLayoutRejectReason.NoChange, noop.Reason);
    }

    [Fact]
    public void Move_UnknownIndex_IsRejected()
    {
        var result = HomeLayoutReducer.Apply(HomeLayoutDoc.Default, new MoveHomeModule(99, 0));
        Assert.False(result.Changed);
        Assert.Equal(HomeLayoutRejectReason.UnknownModule, result.Reason);
    }

    [Fact]
    public void Cap_RejectsGrowingPastMaxModules()
    {
        var extras = new HomeModuleSpec[HomeLayoutReducer.MaxModules];
        for (int i = 0; i < extras.Length; i++) extras[i] = new HomeModuleSpec(HomeGroupKind.Hero);
        var full = new HomeLayoutDoc(extras);

        var result = HomeLayoutReducer.Apply(full, new SetHomeModuleHidden(HomeGroupKind.WeeklyPair, true));
        Assert.False(result.Changed);
        Assert.Equal(HomeLayoutRejectReason.CapReached, result.Reason);
    }

    [Fact]
    public void Reset_RestoresDefaultOrderAndVisibility()
    {
        var edited = HomeLayoutReducer.Apply(HomeLayoutDoc.Default, new SetHomeModuleHidden(HomeGroupKind.Hero, true)).Layout;
        edited = HomeLayoutReducer.Apply(edited, new MoveHomeModule(0, 4)).Layout;
        var reset = HomeLayoutReducer.Apply(edited, new ResetHomeLayout());
        Assert.True(reset.Changed);
        Assert.Equal(HomeLayoutModules.DefaultOrder, KindsOf(reset.Layout));
        Assert.False(reset.Layout.IsHidden(HomeGroupKind.Hero));
    }

    static HomeGroupKind[] KindsOf(HomeLayoutDoc doc)
    {
        var kinds = new HomeGroupKind[doc.Modules.Count];
        for (int i = 0; i < kinds.Length; i++) kinds[i] = doc.Modules[i].Kind;
        return kinds;
    }

    // ── DTO round-trip + unknown carry ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Dto_RoundTripsModulesDeckOrderAndHidden()
    {
        var layout = new HomeLayoutDoc(
            [
                new HomeModuleSpec(HomeGroupKind.MixBand, Hidden: true),
                new HomeModuleSpec(HomeGroupKind.Hero),
            ],
            DeckOrder: ["spotify:section:a", "spotify:section:b"]);

        var dto = HomeLayoutWire.Write(layout, null);
        dto.Version = HomeLayoutStore.CurrentVersion;
        string json = JsonSerializer.Serialize(dto, HomeLayoutJsonCtx.Default.HomeLayoutDocDto);
        var parsed = JsonSerializer.Deserialize(json, HomeLayoutJsonCtx.Default.HomeLayoutDocDto);
        var read = HomeLayoutWire.Read(parsed);

        Assert.True(read.Layout.IsHidden(HomeGroupKind.MixBand));
        Assert.Equal(HomeGroupKind.MixBand, read.Layout.Modules[0].Kind);
        Assert.Equal(HomeGroupKind.Hero, read.Layout.Modules[1].Kind);
        Assert.Equal(["spotify:section:a", "spotify:section:b"], read.Layout.DeckList);
        // Missing fixed kinds are appended visible so a new module cannot vanish.
        Assert.Contains(read.Layout.Modules, m => m.Kind == HomeGroupKind.QuickGrid && !m.Hidden);
    }

    [Fact]
    public void UnknownKindAndUnknownFields_SurviveRoundTrip()
    {
        const string json = """
            {
              "version": 1,
              "futureTop": "keep-me",
              "deckOrder": ["sec:later"],
              "modules": [
                { "kind": "hero", "hidden": false, "futureFlag": true },
                { "kind": "futureModule", "hidden": true, "payload": { "n": 1 } }
              ]
            }
            """;

        var parsed = JsonSerializer.Deserialize(json, HomeLayoutJsonCtx.Default.HomeLayoutDocDto)!;
        Assert.NotNull(parsed.Extra);
        Assert.True(parsed.Extra!.ContainsKey("futureTop"));

        var read = HomeLayoutWire.Read(parsed);
        read.Carry.CaptureDoc(parsed);
        Assert.Equal(1, read.Carry.UnknownModuleCount);
        Assert.False(read.Layout.IsHidden(HomeGroupKind.Hero));

        var written = HomeLayoutWire.Write(read.Layout, read.Carry);
        read.Carry.ReattachDoc(written);
        string back = JsonSerializer.Serialize(written, HomeLayoutJsonCtx.Default.HomeLayoutDocDto);
        Assert.Contains("futureTop", back, StringComparison.Ordinal);
        Assert.Contains("keep-me", back, StringComparison.Ordinal);
        Assert.Contains("futureModule", back, StringComparison.Ordinal);
        Assert.Contains("futureFlag", back, StringComparison.Ordinal);
        Assert.Contains("sec:later", back, StringComparison.Ordinal);
    }

    // ── store ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FirstRun_IsNotAFault()
    {
        var load = Store().Load();
        Assert.Null(load.Doc);
        Assert.Equal(HomeLayoutLoadFault.None, load.Fault);
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void Store_RoundTrip_StampsVersion()
    {
        var store = Store();
        var dto = Envelope(HomeLayoutDoc.Default);
        dto.Version = 0;
        CommitAndWait(store, dto);

        var back = store.Load();
        Assert.Equal(HomeLayoutLoadFault.None, back.Fault);
        Assert.Equal(HomeLayoutStore.CurrentVersion, back.Doc!.Version);
        Assert.True(back.Doc.UpdatedAtMs > 0);
        Assert.Equal(HomeLayoutModules.DefaultOrder.Length, HomeLayoutWire.Read(back.Doc).Layout.ModuleCount);
    }

    [Fact]
    public void CorruptFile_FailSoft_DoesNotOverwriteUntilDiscard()
    {
        byte[] payload = Encoding.UTF8.GetBytes("{ \"version\": 1, \"modules\": [");
        File.WriteAllBytes(_path, payload);
        byte[] before = File.ReadAllBytes(_path);

        var store = Store();
        var load = store.Load();
        Assert.Null(load.Doc);
        Assert.Equal(HomeLayoutLoadFault.Corrupt, load.Fault);
        Assert.True(store.WritesBlocked);
        Assert.Equal(before, File.ReadAllBytes(_path));

        store.Commit(Envelope(HomeLayoutDoc.Default));
        store.WaitForWrites(2000);
        Assert.Equal(before, File.ReadAllBytes(_path));

        store.DiscardCorrupt();
        Assert.False(store.WritesBlocked);
        Assert.True(File.Exists(store.CorruptPath));
        CommitAndWait(store, Envelope(HomeLayoutDoc.Default));
        Assert.True(File.Exists(_path));
        Assert.Equal(HomeLayoutLoadFault.None, Store().Load().Fault);
    }

    [Fact]
    public void TooNew_BlocksWrites_AndKeepsFile()
    {
        string payload = """{ "version": 99, "modules": [] }""";
        File.WriteAllText(_path, payload);
        byte[] before = File.ReadAllBytes(_path);

        var store = Store();
        var load = store.Load();
        Assert.Equal(HomeLayoutLoadFault.TooNew, load.Fault);
        Assert.True(store.WritesBlocked);
        store.Commit(Envelope(HomeLayoutDoc.Default));
        store.WaitForWrites(2000);
        Assert.Equal(before, File.ReadAllBytes(_path));
    }

    // ── projection applies layout BEFORE row synthesis ────────────────────────────────────────────────────────────────

    [Fact]
    public void Projection_HiddenHero_OmitsRow_AndDoesNotLeaveAHole()
    {
        var feed = FeedWith(new HomeGroup(HomeGroupKind.Hero, null, [Card("daylist")]));
        var hidden = HomeLayoutReducer.Apply(HomeLayoutDoc.Default, new SetHomeModuleHidden(HomeGroupKind.Hero, true)).Layout;

        var landing = HomeLandingProjection.Project(feed, HomeModuleTitles.Default, hidden);
        Assert.Null(landing.Get(HomeGroupKind.Hero));
        Assert.DoesNotContain(HomeRow.Hero, landing.Rows);
        Assert.Equal(HomeRow.Chips, landing.Rows[0]);
        Assert.Equal(HomeRow.Tail, landing.Rows[^1]);
    }

    [Fact]
    public void Projection_Reorder_IsVisibleInRows()
    {
        var feed = FeedWith(
            new HomeGroup(HomeGroupKind.Hero, null, [Card("hero")]),
            new HomeGroup(HomeGroupKind.MixBand, "Made for you", [Card("mix")]));
        int from = HomeLayoutDoc.Default.IndexOf(HomeGroupKind.MixBand);
        var moved = HomeLayoutReducer.Apply(HomeLayoutDoc.Default, new MoveHomeModule(from, 0)).Layout;

        var landing = HomeLandingProjection.Project(feed, HomeModuleTitles.Default, moved);
        int mix = IndexOf(landing.Rows, HomeRow.MixBand);
        int hero = IndexOf(landing.Rows, HomeRow.Hero);
        Assert.True(mix >= 0 && hero >= 0);
        Assert.True(mix < hero);
    }

    [Fact]
    public void Projection_DefaultLayout_MatchesDesignedRowTable()
    {
        var landing = HomeLandingProjection.Project(HomeFeed.Empty, HomeModuleTitles.Default, HomeLayoutDoc.Default);
        Assert.Equal(HomeLandingProjection.DefaultRows, landing.Rows);
    }

    static int IndexOf(IReadOnlyList<HomeRow> rows, HomeRow row)
    {
        for (int i = 0; i < rows.Count; i++)
            if (rows[i] == row) return i;
        return -1;
    }
}
