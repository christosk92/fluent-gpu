using System;
using System.IO;
using System.Runtime.CompilerServices;
using Wavee;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests.Actions;

/// <summary>
/// The MENU GRAMMAR gates (defect register D48). A right-click on the same KIND of thing must offer the same verbs on
/// every surface: before this pass a song in search results offered no Go-to-album/artist, a home mix card offered no
/// Play-next / Play-after / Add-to-playlist, a queue row offered no Add-to-playlist or Share, and a sidebar playlist
/// row could be played but never queued. The core sets and their ORDER are documented at the top of
/// <c>Actions/Menus.cs</c>; these tests pin them.
///
/// <para>Two kinds of test, because the menu builders sit at two different distances from this project. The pure
/// decision rules (<see cref="ActionRules"/>) are source-included and driven directly. <c>Menus.cs</c> is not: it is
/// engine code (FluentGpu.Controls elements, overlays, toasts), and source-including it would drag the whole engine into
/// a test project that deliberately shadows parts of it. So the composition itself is pinned by SOURCE SCAN — the same
/// technique <c>MotionSystemTests</c> uses for its "no call site may author this again" gates. A source scan cannot
/// prove a menu renders; it can prove a builder still calls the verb, which is exactly the regression class here (a
/// builder quietly growing its own subset).</para>
/// </summary>
public class MenuGrammarTests
{
    // ── the pure rule: a "Go to artist" row must have somewhere to go ────────────────────────────────────────────────

    [Fact]
    public void CanGoToArtist_RequiresAPrimaryArtistUri()
    {
        var ok = ActionTarget.ForTracks(new[] { T.Mk("a") });            // seeds spotify:artist:ar0
        Assert.True(ActionRules.CanGoToArtist(in ok));

        var noArtists = ActionTarget.ForTracks(new[] { T.Mk("a", artists: 0) });
        Assert.False(ActionRules.CanGoToArtist(in noArtists));

        // The reported shape: a projected row (Menus.TrackFromEntry) carries an artist NAME with no uri. Navigating it
        // would open the route "artist:" — a dead page. The row must be absent instead.
        var nameOnly = ActionTarget.ForTracks(new[]
        {
            new Track("z", "spotify:track:z", "Track z",
                new[] { new ArtistRef("", "", "Unknown") },
                new AlbumRef("al1", "spotify:album:al1", "Album One"), 180_000, false, null),
        });
        Assert.False(ActionRules.CanGoToArtist(in nameOnly));
    }

    [Fact]
    public void CanGoToArtist_IsSingleTargetOnly()
    {
        var multi = ActionTarget.ForTracks(new[] { T.Mk("a"), T.Mk("b") });
        Assert.False(ActionRules.CanGoToArtist(in multi));   // a selection has no single artist to go to
    }

    // ── TRACK: every track builder emits the core set ────────────────────────────────────────────────────────────────

    /// <summary>Play · Play next · Play after · Save, wherever the four are a command strip.</summary>
    [Fact]
    public void TrackTransport_IsTheSameFourVerbs_InTheStripAndAsRows()
    {
        string strip = Body(Menus(), "static AppBarCommand[] TrackTransportStrip");
        string asRows = Body(Menus(), "static void AddTrackTransportRows");
        foreach (string verb in new[] { "TrackActions.Play", "TrackActions.PlayNext", "TrackActions.AddToQueue", "TrackActions.ToggleLike" })
        {
            Assert.Contains(verb, strip, StringComparison.Ordinal);
            Assert.Contains(verb, asRows, StringComparison.Ordinal);
        }
    }

    /// <summary>The track ROWS: Add to playlist ▸ · Go to album · Go to artist(s) · Share ▸ — and the destructive verbs
    /// last.</summary>
    [Fact]
    public void TrackRows_CarryTheCoreSet()
    {
        string rows = Body(Menus(), "public static IReadOnlyList<MenuFlyoutItem> TrackRows");
        Assert.Contains("AddToPlaylistItem(in ctx)", rows, StringComparison.Ordinal);
        Assert.Contains("TrackActions.GoToAlbum", rows, StringComparison.Ordinal);
        Assert.Contains("TrackActions.GoToArtist", rows, StringComparison.Ordinal);
        Assert.Contains("GoToArtistsItem", rows, StringComparison.Ordinal);
        Assert.Contains("ShareItem(in ctx)", rows, StringComparison.Ordinal);
        Assert.Contains("TrackActions.RemoveFromThisPlaylist", rows, StringComparison.Ordinal);
    }

    /// <summary>The container-navigation row is ONE row routed by kind: a song offers Go-to-album, an EPISODE offers
    /// Go-to-podcast instead (it carries its SHOW in the album slot — EpisodeAsTrack, design §1.5 — so Go-to-album
    /// would open the album page of a podcast). The two are mutually exclusive by construction (else-if), which is the
    /// regression this pins: an episode row that offers both, or that keeps offering the album one.</summary>
    [Fact]
    public void TrackRows_RouteTheContainerRowByKind()
    {
        string rows = Body(Menus(), "public static IReadOnlyList<MenuFlyoutItem> TrackRows");
        Assert.Contains("ActionRules.CanGoToAlbum(in ctx.Target)", rows, StringComparison.Ordinal);
        Assert.Contains("else if", rows, StringComparison.Ordinal);
        Assert.Contains("GoToPodcastItem(ctx.S, in ctx.Target)", rows, StringComparison.Ordinal);

        // …and the podcast row navigates to the SHOW route, never to an album/episode one.
        string item = Body(Menus(), "static MenuFlyoutItem? GoToPodcastItem");
        Assert.Contains("ActionRules.CanGoToPodcast(in target)", item, StringComparison.Ordinal);
        Assert.Contains("\"show:\" + uri", item, StringComparison.Ordinal);
    }

    /// <summary>The ORDER: collection → navigation → Share → surface extras → destructive last.</summary>
    [Fact]
    public void TrackRows_FollowTheGrammarOrder()
    {
        string rows = Body(Menus(), "public static IReadOnlyList<MenuFlyoutItem> TrackRows");
        int collection = rows.IndexOf("AddToPlaylistItem(in ctx)", StringComparison.Ordinal);
        int navigation = rows.IndexOf("TrackActions.GoToAlbum", StringComparison.Ordinal);
        int share = rows.IndexOf("ShareItem(in ctx)", StringComparison.Ordinal);
        int extras = rows.IndexOf("TrackActions.ViewCredits", StringComparison.Ordinal);
        int destructive = rows.IndexOf("TrackActions.RemoveFromThisPlaylist", StringComparison.Ordinal);

        Assert.True(collection >= 0 && navigation > collection, "collection (Add to playlist) precedes navigation");
        Assert.True(share > navigation, "navigation precedes Share");
        Assert.True(extras > share, "Share precedes the surface extras");
        Assert.True(destructive > extras, "the destructive verbs are last");
    }

    /// <summary>The QUEUE row menu is the track grammar plus queue extras — not its own smaller menu (it used to offer
    /// Go-to-album/artist and a bare Copy link and nothing else).</summary>
    [Fact]
    public void QueueEntryMenu_ReusesTheTrackRows()
    {
        string queue = Body(Menus(), "public static ContextMenuModel QueueEntry");
        Assert.Contains("TrackRows(in ctx", queue, StringComparison.Ordinal);
        Assert.Contains("TrackActions.PlayNext", queue, StringComparison.Ordinal);
        Assert.Contains("TrackActions.AddToQueue", queue, StringComparison.Ordinal);
        Assert.Contains("Strings.Menu.MoveUp", queue, StringComparison.Ordinal);        // the surface extra survives
        Assert.Contains("Strings.Menu.PlayNow", queue, StringComparison.Ordinal);       // …and the queue-only verb
    }

    /// <summary>The sidebar row menu takes the same extras slot the queue uses (Move up / Move down / Remove), inserted
    /// after the entity verbs and before a trailing destructive block — drag is never the only way to reorder.</summary>
    [Fact]
    public void SidebarEntryMenu_TakesLayoutExtras()
    {
        string entry = Body(Menus(), "public static ContextMenuModel? SidebarEntry");
        Assert.Contains("layoutExtras", entry, StringComparison.Ordinal);
        Assert.Contains("WithLayoutExtras", entry, StringComparison.Ordinal);

        string extras = Body(Menus(), "public static ContextMenuModel? WithLayoutExtras");
        Assert.Contains("MenuFlyoutItem.Separator", extras, StringComparison.Ordinal);
        Assert.Contains("rows[^2].IsSeparator", extras, StringComparison.Ordinal);
    }

    /// <summary>The sidebar's feed TRACK row: rows-only, but the same core set (transport rows + the track rows).</summary>
    [Fact]
    public void SidebarTrackMenu_ReusesTheTrackGrammar()
    {
        string sidebar = Body(Menus(), "static ContextMenuModel SidebarTrackMenu");
        Assert.Contains("AddTrackTransportRows", sidebar, StringComparison.Ordinal);
        Assert.Contains("TrackRows(in ctx)", sidebar, StringComparison.Ordinal);
    }

    /// <summary>A bare-track-uri card keeps the strip, Add-to-playlist, Share and song radio (which seeds off the uri
    /// alone). Go-to-album/artist genuinely cannot be built from a uri — the surfaces that can resolve the real Track
    /// attach the full track menu instead, which is the next test.</summary>
    [Fact]
    public void TrackUriCard_CarriesEverythingAUriCanCarry()
    {
        string card = Body(Menus(), "static ContextMenuModel TrackUriCard");
        Assert.Contains("TrackTransportStrip(in ctx)", card, StringComparison.Ordinal);
        Assert.Contains("AddToPlaylistItem(in ctx)", card, StringComparison.Ordinal);
        Assert.Contains("ShareItem(in ctx)", card, StringComparison.Ordinal);
        Assert.Contains("TrackActions.GoToSongRadio", card, StringComparison.Ordinal);
    }

    /// <summary>A SEARCH track hit resolves its real <c>Track</c> out of the page's own results and gets the full track
    /// menu — the reported "search song has no Go to artist / Go to album".</summary>
    [Fact]
    public void SearchTrackHit_GetsTheFullTrackMenu()
    {
        string hitMenu = Body(SearchPage(), "static MenuAttach? HitMenu");
        Assert.Contains("SearchHitKind.Track", hitMenu, StringComparison.Ordinal);
        Assert.Contains("TrackOf(model, h.Uri)", hitMenu, StringComparison.Ordinal);
        Assert.Contains("TrackMenu(acts, overlay, t)", hitMenu, StringComparison.Ordinal);

        // …and the row actually asks for it.
        Assert.Contains("menu: HitMenu(acts, menuOverlay, model, h)", SearchPage(), StringComparison.Ordinal);
    }

    // ── CONTAINER: cards, sidebar rows ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Play · Play next · Play after · Save on the card strip — the home mix card's missing verbs.</summary>
    [Fact]
    public void ContainerStrip_CarriesTheTransportPair()
    {
        string strip = Body(Menus(), "static AppBarCommand[] ContainerStrip");
        Assert.Contains("ContainerActions.PlayContext.ToBarCommand", strip, StringComparison.Ordinal);
        Assert.Contains("ContainerActions.PlayContextNext", strip, StringComparison.Ordinal);
        Assert.Contains("ContainerActions.AddContextToQueue", strip, StringComparison.Ordinal);
        Assert.Contains("ContainerActions.SaveContext", strip, StringComparison.Ordinal);
    }

    /// <summary>Add to playlist ▸ · Open · Pin · Go to artist (album) · Share ▸ on the card rows.</summary>
    [Fact]
    public void ContainerRows_CarryTheCoreSet()
    {
        string rows = Body(Menus(), "static List<MenuFlyoutItem> ContainerRows");
        Assert.Contains("ContainerAddToPlaylistItem(in ctx)", rows, StringComparison.Ordinal);
        Assert.Contains("ContainerActions.OpenItem", rows, StringComparison.Ordinal);
        Assert.Contains("PinActions.Row(in ctx)", rows, StringComparison.Ordinal);
        Assert.Contains("ContainerActions.GoToAlbumArtist", rows, StringComparison.Ordinal);
        Assert.Contains("ShareItem(in ctx)", rows, StringComparison.Ordinal);
    }

    /// <summary>The sidebar playlist row (Classic + V3 + Curated share it) carries the same container core set.</summary>
    [Fact]
    public void SidebarPlaylistRows_CarryTheContainerCoreSet()
    {
        string rows = Body(Menus(), "static List<MenuFlyoutItem> SidebarPlaylistRows");
        foreach (string verb in new[]
                 {
                     "ContainerActions.PlayContext", "ContainerActions.PlayContextNext",
                     "ContainerActions.AddContextToQueue", "ContainerActions.SaveContext",
                     "ContainerAddToPlaylistItem(in ctx)", "ContainerActions.OpenItem",
                     "PinActions.RowForId", "ShareItem(in ctx)",
                 })
            Assert.Contains(verb, rows, StringComparison.Ordinal);

        // …and the surface extras stay where they were: the owner block and the destructive delete, last.
        Assert.True(rows.IndexOf("ContainerActions.DeletePlaylist", StringComparison.Ordinal)
                    > rows.IndexOf("ShareItem(in ctx)", StringComparison.Ordinal),
            "Delete playlist stays destructive-last");
    }

    /// <summary>Visibility ▸ reports where the playlist actually stands. The sidebar summary that feeds this menu
    /// carries no visibility at all, so the rows used to render unchecked whatever the state was; they now read the
    /// STORE header (the canonical permission state — seeded on open, flipped by a dealer push) at menu-open, and the
    /// submenu gained the Collaborative toggle that used to exist only inside the detail page's access flyout.</summary>
    [Fact]
    public void SidebarVisibilitySubmenu_ChecksTheLiveStateAndCarriesTheCollaborativeToggle()
    {
        string body = Body(Menus(), "static MenuFlyoutItem VisibilityItem(ActionServices s");

        // Read the header — never a GET, and never a guessed check mark.
        Assert.Contains("RealStore?.GetPlaylist(uri)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Async(", body, StringComparison.Ordinal);
        // The absolute pair stays absolute (each row SETS what it names) but is now checkable…
        Assert.Contains("MenuFlyoutItem.RadioItem(Loc.Get(Strings.Menu.MakePublic)", body, StringComparison.Ordinal);
        Assert.Contains("MenuFlyoutItem.RadioItem(Loc.Get(Strings.Menu.MakePrivate)", body, StringComparison.Ordinal);
        // …and no store (a fake backend / logged out) means NO check mark rather than a fabricated one.
        Assert.Contains("known && isPublic", body, StringComparison.Ordinal);
        Assert.Contains("known && !isPublic", body, StringComparison.Ordinal);
        // The third state the header carries, as a real toggle.
        Assert.Contains("MenuFlyoutItem.Toggle(", body, StringComparison.Ordinal);
        Assert.Contains("ContainerActions.SetCollaborative(s, uri, !collaborative)", body, StringComparison.Ordinal);
    }

    /// <summary>Every sidebar entity menu (projected row, folder, route, embed card) passes the pane's layout extras
    /// through the same <c>SidebarEntry</c> extras slot — a row that can move by drag can also move from the menu.</summary>
    [Fact]
    public void SidebarPaneSlot_WiresLayoutExtrasOntoEveryEntityMenu()
    {
        string slot = File.ReadAllText(Path.Combine(AppRoot(), "Features", "Sidebar", "Pane", "SidebarPaneSlot.cs"));
        Assert.Contains("layoutExtras: NavExtras", slot, StringComparison.Ordinal);
        Assert.Contains("MoveRowByKey", slot, StringComparison.Ordinal);
        Assert.Contains("Strings.Menu.MoveUp", slot, StringComparison.Ordinal);
        Assert.Contains("SidebarPaneLoc.ItemRemove", slot, StringComparison.Ordinal);
    }

    /// <summary>The container track-set verbs resolve through the ONE seam drag &amp; drop deposits with — never a
    /// second reader path that could disagree about what an album's tracks are.</summary>
    [Fact]
    public void ContainerTrackSet_ResolvesThroughTheDragDropSeam()
    {
        string container = File.ReadAllText(Path.Combine(AppRoot(), "Actions", "ContainerActions.cs"));
        Assert.Contains("WaveeResourceDragPayload.ResolverFor", container, StringComparison.Ordinal);
        Assert.Contains("DetailQueueActions.PlayNext", container, StringComparison.Ordinal);
        Assert.Contains("DetailQueueActions.AddToEnd", container, StringComparison.Ordinal);
        Assert.Contains("lib.AddTracksAsync", container, StringComparison.Ordinal);
    }

    // ── the raw-HTML header defect ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Entity subtitles arrive as HTML FRAGMENTS ("Song • &lt;a href=…&gt;Name&lt;/a&gt;") because the row
    /// renderers parse them into clickable links. A menu header is a plain text element, so the reported defect was a
    /// header rendering the raw tag. The flattening is pinned at the ONE header constructor rather than per producer —
    /// so this test also pins that there IS only one.</summary>
    [Fact]
    public void EveryMenuHeader_IsFlattenedToPlainText()
    {
        string menus = Menus();
        Assert.Equal(1, Count(menus, "new ContextMenuHeader("));

        string header = Body(menus, "static ContextMenuHeader Header(");
        Assert.Contains("PlainHeaderText(title)", header, StringComparison.Ordinal);
        Assert.Contains("PlainHeaderText(subtitle)", header, StringComparison.Ordinal);

        // Tags stripped THEN entities decoded — the order RichText itself uses (decoding first would turn an escaped
        // &lt;a&gt; into a live tag the stripper then eats).
        Assert.Contains("SpotifyExportMapper.HtmlText(SpotifyExportMapper.ToPlainText(text))", menus, StringComparison.Ordinal);
    }

    /// <summary>The flattener the header rides is the shared one, and it does what the header needs: a link becomes its
    /// NAME. (The walk itself is <c>SpotifyExportMapper.ToPlainText</c>, covered by ExportMapperTextTests; this pins the
    /// one shape the menu header defect was reported with.)</summary>
    [Fact]
    public void TheFlattener_TurnsAnArtistLinkIntoItsName()
    {
        const string wire = "Song • <a href=\"spotify:artist:2Q3eZMfDc\">Kali Uchis</a>";
        string? plain = SpotifyExportMapper.ToPlainText(wire);
        Assert.Equal("Song • Kali Uchis", plain);
        Assert.DoesNotContain("<a ", plain, StringComparison.Ordinal);
    }

    // ── the deposit paths must be HONEST ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Every add-to-playlist path AWAITS its write and maps the failure. Both of these used to fire the mutation
    /// and toast "Added to {name}" unconditionally, so an add that failed (offline, revoked permissions, a rejected
    /// revision) reported SUCCESS and the only trace was an entry silently flipped to Failed in the notification panel.
    /// Reporting a mutation that did not happen is the worst failure mode this flow has.</summary>
    [Theory]
    [InlineData("Actions", "Menus.cs", "static void AddTo(ActionServices s")]
    [InlineData("Features/Detail", "PlaylistPicker.cs", "void AddTo(string uri, string name)")]
    // …and the three EDIT paths the same defect class was found on (plan P1.8): remove was fire-and-forget, and both
    // reorder commits discarded the move task outright.
    [InlineData("Actions", "TrackActions.cs", "internal static void RemoveRows(ActionServices s")]
    [InlineData("Features/DragDrop", "WaveeResourceDrag.cs", "public static Task<bool> DepositTracksAsync(")]
    [InlineData("Features/Detail", "DetailTracks.cs", "bool TryBlockMove(int delta)")]
    public void EveryAddToPlaylistPath_AwaitsAndMapsItsFailure(string dir, string file, string signature)
    {
        string body = Body(Source(dir, file), signature);

        Assert.Contains("await ", body, StringComparison.Ordinal);
        Assert.Contains("catch (Exception ex)", body, StringComparison.Ordinal);
        // `Toast(ex` rather than `Toast(ex)`: the edit paths pass a VERB so a lost reorder race gets its own sentence.
        Assert.Contains("PlaylistEditErrors.Toast(ex", body, StringComparison.Ordinal);
        // The fire-and-forget shapes, verbatim.
        Assert.DoesNotContain("_ = lib.AddTracksAsync", body, StringComparison.Ordinal);
        Assert.DoesNotContain("_ = lib.MovePlaylistRowsAsync", body, StringComparison.Ordinal);
        Assert.DoesNotContain("_ = lib.RemovePlaylistRowsAsync", body, StringComparison.Ordinal);
    }

    /// <summary>No reorder commit anywhere still discards its move task. The two that did (the same-list drag deposit
    /// and Alt+Up/Alt+Down) both rolled the membership back on refusal — the rows visibly snapped home — and said
    /// nothing at all.</summary>
    [Fact]
    public void NoReorderPath_DiscardsItsMoveTask()
    {
        Assert.Equal(0, Count(Source("Features/Detail", "DetailTracks.cs"), "_ = lib.MovePlaylistRowsAsync"));
        Assert.Equal(0, Count(Source("Features/DragDrop", "WaveeResourceDrag.cs"), "_ = lib.MovePlaylistRowsAsync"));
    }

    /// <summary>A completed REMOVE offers Undo, exactly as a completed deposit does: it is the edit most often made by
    /// accident, and the only way back used to be the notification panel.</summary>
    [Fact]
    public void ACompletedRemoveOffersUndo()
    {
        string toast = Body(Menus(), "internal static void ToastRemoved(");
        Assert.Contains("Strings.Notifications.Undo", toast, StringComparison.Ordinal);
        Assert.Contains("UndoByIdAsync", toast, StringComparison.Ordinal);

        string remove = Body(Source("Actions", "TrackActions.cs"), "internal static void RemoveRows(ActionServices s");
        Assert.Contains("RemovePlaylistRowsTrackedAsync", remove, StringComparison.Ordinal);
        Assert.Contains("Menus.ToastRemoved", remove, StringComparison.Ordinal);
    }

    /// <summary>No deposit path leaks a raw <c>ex.Message</c> — engine prose is not a sentence a listener can act on, and
    /// <c>PlaylistEditErrors</c> exists to map each failure to one that is.</summary>
    [Fact]
    public void NoDepositPathToastsARawExceptionMessage()
    {
        Assert.Equal(0, Count(Menus(), "Toast.Show(ex.Message"));
        Assert.Equal(0, Count(Source("Features/Detail", "PlaylistPicker.cs"), "Toast.Show(ex.Message"));
        // The MAP itself: PlaylistEditErrors used to end in `_ => ex.Message`, so every failure its string sniffing did
        // not recognise was shown to the user as engine prose. Neither file may mention ex.Message in any form now.
        Assert.Equal(0, Count(Source("Features/Detail", "PlaylistEditErrors.cs"), "ex.Message"));
        Assert.Equal(0, Count(Source("Features/Detail", "PlaylistEditErrorKinds.cs"), "ex.Message"));
    }

    /// <summary>A completed deposit offers <b>Undo</b>, not "Open": the user is mid-flow on the page they filed from and
    /// rarely wants to leave, whereas the recoverable mistake — wrong playlist, wrong row, a forgotten multi-selection —
    /// is common and was previously only recoverable by hunting down the notification panel. A CREATE-then-add is the
    /// deliberate exception (a new playlist needs a name), so it keeps Open.</summary>
    [Fact]
    public void ACompletedDepositOffersUndo_AndACreateOffersOpen()
    {
        string toast = Body(Menus(), "internal static void ToastDeposited(");
        Assert.Contains("Strings.Notifications.Undo", toast, StringComparison.Ordinal);
        Assert.Contains("UndoByIdAsync", toast, StringComparison.Ordinal);

        string create = Body(Menus(), "static void CreateAndAdd(ActionServices s");
        Assert.Contains("Strings.Detail.GoToPlaylist", create, StringComparison.Ordinal);
    }

    /// <summary>The submenu, the picker and the tab drop rules all route through the ONE eligibility + ordering function.
    /// Three hand-written copies of the filter is where this started, each with a comment warning about the other two.</summary>
    [Fact]
    public void EveryDepositSurface_RoutesThroughPlaylistDepositTargets()
    {
        Assert.Contains("PlaylistDepositTargets.Order(", Body(Menus(), "static MenuFlyoutItem PlaylistDepositItem("),
            StringComparison.Ordinal);
        Assert.Contains("PlaylistDepositTargets.Order(", Source("Features/Detail", "PlaylistPicker.cs"),
            StringComparison.Ordinal);
        Assert.Contains("PlaylistDepositTargets.IsDepositable", Source("Features/DragDrop", "WaveeDragRules.cs"),
            StringComparison.Ordinal);
        // The old inline copies are gone, not merely bypassed.
        Assert.Equal(0, Count(Source("Features/Detail", "PlaylistPicker.cs"), "static bool IsRealPlaylist"));
        Assert.Equal(0, Count(Menus(), "p.Uri.StartsWith(\"spotify:playlist:\""));
    }

    /// <summary>A one-click "New playlist" is named "{base} #N", never another playlist literally called "New playlist"
    /// (three of those in a sidebar are indistinguishable) — and it is named that in exactly ONE place. Every surface
    /// that creates a playlist now routes through <c>PlaylistCreateFlow.Create</c>: the two menu deposits, the picker's
    /// inline row, the sidebar's drop-to-create target and all three sidebar designs. The three DESIGNS are the reason
    /// this test grew: each one used to POST an unnumbered <c>Strings.Sidebar.NewPlaylist</c> of its own and await the
    /// round trip before navigating.</summary>
    [Theory]
    [InlineData("Actions", "Menus.cs", "static void CreateAndAdd(ActionServices s")]
    [InlineData("Actions", "Menus.cs", "static void CreateAndDeposit(ActionServices s")]
    [InlineData("Actions", "Menus.cs", "static void CreateAndMove(ActionServices s")]
    [InlineData("Features/Detail", "PlaylistPicker.cs", "void CreateAndAdd()")]
    [InlineData("Features/Sidebar/Pane", "SidebarPane.cs", "internal void CreatePlaylistFromDrag(")]
    [InlineData("Features/Sidebar", "WaveeSidebar.cs", "void CreatePlaylist()")]
    [InlineData("Features/Sidebar/Modes", "CuratedSidebar.cs", "void CreatePlaylist()")]
    [InlineData("Features/Sidebar/Modes/LibraryV3", "LibraryV3Session.cs", "public void CreatePlaylist()")]
    public void EveryCreatePath_UsesTheNumberedDefaultName(string dir, string file, string signature)
    {
        string body = Body(Source(dir, file), signature);
        Assert.Contains("PlaylistCreateFlow.Create(", body, StringComparison.Ordinal);
        // The shapes this replaced: the awaited transitional bridge, and the unnumbered per-design default name.
        Assert.DoesNotContain("CreatePlaylistAsync", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Loc.Get(Strings.Sidebar.NewPlaylist)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Loc.Get(Strings.Detail.NewPlaylist)", body, StringComparison.Ordinal);

        // …and the ONE path is what mints the numbered name.
        Assert.Contains("Menus.NextPlaylistName(s)", Source("Actions", "PlaylistCreateFlow.cs"), StringComparison.Ordinal);
    }

    /// <summary>The transitional awaited bridge is GONE, not merely bypassed — a second create path is exactly how the
    /// six copies this pass deleted came to exist.</summary>
    [Fact]
    public void NoCreatePathAwaitsTheOldBridge()
    {
        Assert.Equal(0, Count(Source("App", "LibraryBridge.cs"), "CreatePlaylistAsync"));
        Assert.Equal(0, Count(Menus(), "CreatePlaylistAsync"));
    }

    /// <summary>The create is observed, never awaited: the page is navigated to on the SYNCHRONOUS seam result, and
    /// <c>Completion</c> decides afterwards whether it became real. A rejected create gets one mapped sentence and a
    /// Retry (a NEW id, the same name) — never the raw engine text — and the open page learns through the bridge, so
    /// its notice strip outlives the toast.</summary>
    [Fact]
    public void EveryCreatePath_ObservesCompletionAndMapsItsFailure()
    {
        string flow = Source("Actions", "PlaylistCreateFlow.cs");

        string observe = Body(flow, "static void Observe(ActionServices s");
        Assert.Contains("await completion", observe, StringComparison.Ordinal);
        Assert.Contains("catch (Exception ex)", observe, StringComparison.Ordinal);
        Assert.Contains("Failed(", observe, StringComparison.Ordinal);

        string failed = Body(flow, "static void Failed(ActionServices s");
        Assert.Contains("SettleCreate(uri, ok: false)", failed, StringComparison.Ordinal);
        Assert.Contains("Strings.Detail.Edit.CreateFailed", failed, StringComparison.Ordinal);
        Assert.Contains("Strings.Common.Retry", failed, StringComparison.Ordinal);
        Assert.Equal(0, Count(flow, "ex.Message"));

        // The page's notice rule is fed by the bridge, so CreateFailed is reachable at all (it used to be hard-coded off).
        string page = Source("Features/Detail", "DetailPage.cs");
        Assert.Contains("IsCreatePending(", page, StringComparison.Ordinal);
        Assert.Contains("IsCreateFailed(", page, StringComparison.Ordinal);
        Assert.Equal(0, Count(page, "isCreatePending: false)"));
    }

    /// <summary>The folder row menu, shared by Classic / Library V3 / Curated through one builder. ORDER: the disclosure
    /// and creation verbs first, then the management block (Pin · Rename · Move out of {parent}), then Delete folder,
    /// destructive-LAST exactly as a playlist's Delete is.
    ///
    /// <para>These rows used to be [Expand/Collapse · Pin] and nothing else, with a comment saying folder CRUD must not
    /// appear "not even disabled" (the old locked decision 9). The wire landed with P3, so the lock is lifted and the
    /// verbs are real commands — which is what <c>FolderActions.</c> being present here proves.</para></summary>
    [Fact]
    public void SidebarFolderRows_CarryTheFolderVerbs()
    {
        string rows = Body(Menus(), "static List<MenuFlyoutItem> SidebarFolderRows");
        foreach (string verb in new[]
                 {
                     "Strings.Sidebar.Item.CollapseFolder", "Strings.Sidebar.NewPlaylistHere",
                     "Strings.Sidebar.NewFolderInside", "PinActions.RowForEntry",
                     "Strings.Sidebar.RenameFolder", "Strings.Menu.MoveOutOf", "Strings.Sidebar.DeleteFolder",
                 })
            Assert.Contains(verb, rows, StringComparison.Ordinal);

        // Real commands, not promises.
        Assert.Contains("FolderActions.", rows, StringComparison.Ordinal);

        int rename = rows.IndexOf("Strings.Sidebar.RenameFolder", StringComparison.Ordinal);
        int delete = rows.IndexOf("Strings.Sidebar.DeleteFolder", StringComparison.Ordinal);
        Assert.True(rename >= 0 && delete > rename, "Rename comes before Delete");
        // …and nothing is added after Delete: destructive-last, with no verb trailing it.
        Assert.DoesNotContain("rows.Add(", rows[delete..], StringComparison.Ordinal);

        // The create row keeps CLICK = new playlist and gains the folder verb as a menu.
        string createRow = Body(Source("Features/Sidebar/Pane", "SidebarPaneSlot.cs"), "Element CreateRow(SidebarSectionSpec section)");
        Assert.Contains("Strings.Sidebar.CreateFolder", createRow, StringComparison.Ordinal);
        Assert.Contains("FolderActions.NewFolder(", createRow, StringComparison.Ordinal);
        Assert.Contains("OnClick = click", createRow, StringComparison.Ordinal);

        // The LOCK CLAIM is gone everywhere it was stated (the number may still be named, as the thing that was lifted).
        Assert.Equal(0, Count(Menus(), "NO folder CRUD"));
        Assert.Equal(0, Count(Menus(), "must not appear, not even disabled"));
        Assert.Equal(0, Count(Source("Features/Sidebar/Pane", "SidebarPaneSlot.cs"), "Folder creation is deliberately absent"));
        Assert.Equal(0, Count(Source("Features/Sidebar/Modes/LibraryV3", "LibraryV3View.cs"), "a tree it cannot write"));
        Assert.Contains("<b>LIFTED</b>", Menus(), StringComparison.Ordinal);
    }

    /// <summary>A NESTED playlist row gets "Move out of {folder}" too — same verb, same command, one folder level up —
    /// and a top-level row does not get it at all (absent, never a disabled promise).</summary>
    [Fact]
    public void NestedSidebarRows_OfferMoveOutOfTheirFolder()
    {
        string rows = Body(Menus(), "static List<MenuFlyoutItem> SidebarPlaylistRows");
        Assert.Contains("parentFolderId.Length > 0", rows, StringComparison.Ordinal);
        Assert.Contains("Strings.Menu.MoveOutOf(parentFolderName)", rows, StringComparison.Ordinal);
        Assert.Contains("FolderActions.MoveOut(", rows, StringComparison.Ordinal);

        // …fed by the projection's parent-folder facts, through the one SidebarEntry arm.
        string entry = Body(Menus(), "public static ContextMenuModel? SidebarEntry");
        Assert.Contains("e.ParentFolderId, e.ParentFolderName", entry, StringComparison.Ordinal);
    }

    /// <summary>A rootlist TREE row's menu gains the organisation verbs — <b>Move up · Move down · Move to folder…</b> —
    /// through the same additive <c>layoutExtras</c> slot the navbar-customization verbs use, so the entity grammar
    /// above them is untouched. Reordering the rootlist used to be a drag and nothing else (D12).
    ///
    /// <para>"Move out of {parent}" is deliberately NOT among them: it already lives in the entity rows themselves, on
    /// every surface that shows the row (a pinned playlist row is the same rootlist member as its tree row), and a
    /// second copy in the extras would show one verb twice on the one row that gets both.</para></summary>
    [Fact]
    public void SidebarTreeRows_GainTheRootlistMoveVerbs_Additively()
    {
        string extras = Body(Source("Features/Sidebar/Pane", "SidebarPaneSlot.cs"),
                             "IReadOnlyList<MenuFlyoutItem>? NavExtras(");
        foreach (string verb in new[] { "Strings.Menu.MoveUp", "Strings.Menu.MoveDown", "Strings.Menu.MoveToFolder" })
            Assert.Contains(verb, extras, StringComparison.Ordinal);

        int up = extras.IndexOf("FolderActions.MoveUp(", StringComparison.Ordinal);
        int down = extras.IndexOf("FolderActions.MoveDown(", StringComparison.Ordinal);
        int to = extras.IndexOf("FolderActions.MoveTo(", StringComparison.Ordinal);
        Assert.True(up >= 0 && down > up && to > down, "Move up · Move down · Move to folder…, in that order");

        Assert.DoesNotContain("Strings.Menu.MoveOutOf", extras, StringComparison.Ordinal);
        Assert.Contains("Strings.Menu.MoveOutOf", Body(Menus(), "static List<MenuFlyoutItem> SidebarPlaylistRows"),
                        StringComparison.Ordinal);
        Assert.Contains("Strings.Menu.MoveOutOf", Body(Menus(), "static List<MenuFlyoutItem> SidebarFolderRows"),
                        StringComparison.Ordinal);
    }

    /// <summary>Every rootlist organisation verb — the menu's three, the Alt+↑/↓ accelerator and "Move out of" — commits
    /// through the ONE seam a DROP uses. That is what makes each of them await its mutation, map a failure by VERB
    /// (<c>PlaylistEditVerb.Reorder</c>, never raw exception text), announce the result and offer Undo: the discipline is
    /// written once, at the seam, rather than five times at five call sites.</summary>
    [Fact]
    public void EveryRootlistMoveVerb_AwaitsItsMutation_AndMapsTheFailureByVerb()
    {
        string folder = Source("Actions", "FolderActions.cs");
        Assert.Equal(1, Count(folder, "WaveeResourceDrop.MoveRootlist("));       // exactly one commit path
        Assert.Equal(0, Count(folder, "ex.Message"));
        // The verbs reach it through the one chokepoint, and never call the seam themselves.
        Assert.Equal(0, Count(Body(folder, "public static void Move(ActionServices s, string entryId, int delta)"),
                              "MoveRootlistItemAsync"));
        Assert.Equal(0, Count(Body(folder, "public static void MoveOut(ActionServices s, string entryId)"),
                              "MoveRootlistItemAsync"));
        // The picker's commit is the same chokepoint (its rows are destinations, not a second mutation path).
        Assert.Contains("FolderActions.Commit(", Source("Features/Sidebar", "RootlistFolderPicker.cs"),
                        StringComparison.Ordinal);

        string seam = Body(Source("Features/DragDrop", "WaveeResourceDrag.cs"),
                           "public static void MoveRootlist(ActionServices acts");
        Assert.Contains("await lib.MoveRootlistItemAsync", seam, StringComparison.Ordinal);
        Assert.Contains("PlaylistEditVerb.Reorder", seam, StringComparison.Ordinal);
        Assert.Contains("Confirm(acts", seam, StringComparison.Ordinal);
    }

    // ── source-scan plumbing ─────────────────────────────────────────────────────────────────────────────────────────

    static string Menus() => File.ReadAllText(Path.Combine(AppRoot(), "Actions", "Menus.cs"));

    /// <summary>Any app source file, by forward-slash-separated directory + name.</summary>
    static string Source(string dir, string file)
    {
        string path = Path.Combine(AppRoot(), Path.Combine(dir.Split('/')), file);
        Assert.True(File.Exists(path), $"source not found (was it moved?): {path}");
        return File.ReadAllText(path);
    }
    static string SearchPage() => File.ReadAllText(Path.Combine(AppRoot(), "Features", "Search", "SearchPage.cs"));

    /// <summary>The body of the member whose declaration contains <paramref name="signature"/>: from the declaration to
    /// the first line that closes a member at file scope — a 4-space <c>}</c> (a block member) or a 4-space <c>];</c>
    /// (a collection-expression member). Members in these files are all one level deep, so those terminators are exact;
    /// a signature that no longer exists fails loudly rather than matching nothing.</summary>
    static string Body(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"member not found (was it renamed?): {signature}");
        int block = source.IndexOf("\n    }", at, StringComparison.Ordinal);
        int expr = source.IndexOf("\n    ];", at, StringComparison.Ordinal);
        int end = block < 0 ? expr : expr < 0 ? block : Math.Min(block, expr);
        Assert.True(end > at, $"could not delimit the body of: {signature}");
        return source[at..end];
    }

    static int Count(string source, string needle)
    {
        int n = 0;
        for (int i = source.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = source.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    /// <summary>The app's source root, resolved from THIS file's compile-time path (the MotionSystemTests precedent) —
    /// no build-output copying, no working-directory assumption.</summary>
    static string AppRoot([CallerFilePath] string here = "")
    {
        string actionsDir = Path.GetDirectoryName(here)!;                 // …/Wavee.Tests/Actions
        string tests = Path.GetDirectoryName(actionsDir)!;                // …/Wavee.Tests
        string app = Path.Combine(Path.GetDirectoryName(tests)!, "Wavee");
        Assert.True(Directory.Exists(app), $"app source root not found: {app}");
        return app;
    }
}
