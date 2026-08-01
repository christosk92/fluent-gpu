using Wavee;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// The two engine-free drag decisions the Wave-4B surface rollout turns on.
///
/// <para><b>The kind map.</b> Every card surface names its entity with a different enum, and a payload that mislabels
/// its kind fails silently three layers away — an album that says "route" resolves no tracks, a playlist that says
/// "album" cannot be filed into a sidebar folder. These are the mappings twenty call sites now share instead of
/// re-typing a switch each.</para>
///
/// <para><b>The refusal table.</b> Wave 4 made every playlist drop gate answer LIVE, which turned four silent
/// "nothing happens" failures into real refusals — but a refusing drop target is transparent to the engine, so the
/// only thing the user ever sees is the caption chosen from this table. Accept and refuse are one function precisely
/// so a refusal can never be explained with a reason the gate did not use.</para>
/// </summary>
public class WaveeDragRulesTests
{
    // ── kind map ────────────────────────────────────────────────────────────────────────────────────────────────
    // These read as tables inside [Fact] bodies rather than as [Theory] rows because WaveeResourceKind is internal —
    // it cannot appear in a public test method's signature (CS0051).
    [Fact]
    public void HomeCardKind_MapsToTheResourceItRepresents()
    {
        Assert.Equal(WaveeResourceKind.Album, WaveeDragKindMap.Of(HomeCardKind.Album));
        Assert.Equal(WaveeResourceKind.Artist, WaveeDragKindMap.Of(HomeCardKind.Artist));
        Assert.Equal(WaveeResourceKind.Track, WaveeDragKindMap.Of(HomeCardKind.Track));
        Assert.Equal(WaveeResourceKind.Playlist, WaveeDragKindMap.Of(HomeCardKind.Playlist));
        // Liked Songs is a pseudo-playlist: it navigates, pins and resolves its tracks exactly like one.
        Assert.Equal(WaveeResourceKind.Playlist, WaveeDragKindMap.Of(HomeCardKind.Liked));
    }

    [Fact]
    public void SearchHitKind_MapsToTheResourceItRepresents()
    {
        Assert.Equal(WaveeResourceKind.Track, WaveeDragKindMap.Of(SearchHitKind.Track));
        Assert.Equal(WaveeResourceKind.Artist, WaveeDragKindMap.Of(SearchHitKind.Artist));
        Assert.Equal(WaveeResourceKind.Album, WaveeDragKindMap.Of(SearchHitKind.Album));
        Assert.Equal(WaveeResourceKind.Playlist, WaveeDragKindMap.Of(SearchHitKind.Playlist));
        Assert.Equal(WaveeResourceKind.Show, WaveeDragKindMap.Of(SearchHitKind.Podcast));
        // An audiobook is a show in every way this payload cares about (pins as one, resolves no tracks).
        Assert.Equal(WaveeResourceKind.Show, WaveeDragKindMap.Of(SearchHitKind.Audiobook));
        Assert.Equal(WaveeResourceKind.Episode, WaveeDragKindMap.Of(SearchHitKind.Episode));
        // People with no Wavee resource behind them fall to Route: pinnable, inert everywhere else.
        Assert.Equal(WaveeResourceKind.Route, WaveeDragKindMap.Of(SearchHitKind.Author));
        Assert.Equal(WaveeResourceKind.Route, WaveeDragKindMap.Of(SearchHitKind.User));
        Assert.Equal(WaveeResourceKind.Route, WaveeDragKindMap.Of(SearchHitKind.Unknown));
    }

    [Fact]
    public void Uri_NamesTheEntityForCardsThatCarryNothingElse()
    {
        Assert.Equal(WaveeResourceKind.Playlist, WaveeDragKindMap.OfUri("spotify:playlist:37i9"));
        Assert.Equal(WaveeResourceKind.Album, WaveeDragKindMap.OfUri("spotify:album:1DFix"));
        Assert.Equal(WaveeResourceKind.Artist, WaveeDragKindMap.OfUri("spotify:artist:0TnOY"));
        Assert.Equal(WaveeResourceKind.Show, WaveeDragKindMap.OfUri("spotify:show:4rOoJ"));
        Assert.Equal(WaveeResourceKind.Episode, WaveeDragKindMap.OfUri("spotify:episode:512ojh"));
        Assert.Equal(WaveeResourceKind.Track, WaveeDragKindMap.OfUri("spotify:track:4uLU6h"));
        // Liked Songs' collection uri is the one non-":playlist:" playlist.
        Assert.Equal(WaveeResourceKind.Playlist, WaveeDragKindMap.OfUri("spotify:collection:tracks"));
        // Unknown shapes read as a Route: pinnable, never depositable — the safe reading of "we don't know".
        Assert.Equal(WaveeResourceKind.Route, WaveeDragKindMap.OfUri(""));
        Assert.Equal(WaveeResourceKind.Route, WaveeDragKindMap.OfUri("spotify:user:someone"));
    }

    [Fact]
    public void PrereleaseUri_ReadsAsAnAlbum_BeforeTheAlbumScheme()
    {
        // The route mapper resolves :prerelease: to the album's own page; the drag payload must agree with it, and the
        // more specific scheme has to win even though a prerelease uri contains neither ":album:" nor ":playlist:".
        Assert.Equal(WaveeResourceKind.Album, WaveeDragKindMap.OfUri("spotify:prerelease:6cIvT"));
    }

    [Fact]
    public void SidebarEntryKind_MapsFolderAndTrackThatOnlyTheSidebarHas()
    {
        Assert.Equal(WaveeResourceKind.Folder, WaveeDragKindMap.Of(SidebarEntryKind.Folder));
        Assert.Equal(WaveeResourceKind.Track, WaveeDragKindMap.Of(SidebarEntryKind.Track));
        Assert.Equal(WaveeResourceKind.Playlist, WaveeDragKindMap.Of(SidebarEntryKind.Playlist));
        Assert.Equal(WaveeResourceKind.Route, WaveeDragKindMap.Of(SidebarEntryKind.AppRoute));
    }

    // ── playlist drop refusals ──────────────────────────────────────────────────────────────────────────────────
    static PlaylistDropRefusal Verdict(bool editable = true, bool loading = false, bool hasTracks = true,
                                       bool sameList = false, bool naturalOrder = true, bool filtered = false)
        => PlaylistDropRefusalRules.Evaluate(editable, loading, hasTracks, sameList, naturalOrder, filtered);

    [Fact]
    public void AForeignCopyIntoAnEditablePlaylist_IsAllowed()
        => Assert.Equal(PlaylistDropRefusal.None, Verdict());

    [Fact]
    public void ANonEditablePlaylist_RefusesEverything_AndSaysSo()
    {
        // Cause 1 of the four "cannot drop in this mode" reports: a daylist / editorial / someone else's playlist.
        Assert.Equal(PlaylistDropRefusal.NotEditable, Verdict(editable: false));
        // …and no other state can rescue it, which is why it is tested first.
        Assert.Equal(PlaylistDropRefusal.NotEditable, Verdict(editable: false, sameList: true, hasTracks: true));
    }

    [Fact]
    public void AStillLoadingPlaylist_RefusesRatherThanSwallowingTheDrop()
    {
        // Cause 2: an empty list legitimately accepts at slot 0, and a PENDING one looks identical to it — which is
        // how a drop onto a shimmering page used to be discarded in silence.
        Assert.Equal(PlaylistDropRefusal.Loading, Verdict(loading: true));
    }

    [Fact]
    public void APayloadWithNoTracks_IsRefused_NotGuessedAt()
    {
        // The locked product decision behind the artist case: an artist has no single obvious track set, so we refuse
        // and cue rather than silently depositing some guess.
        Assert.Equal(PlaylistDropRefusal.NoTracks, Verdict(hasTracks: false));
    }

    [Fact]
    public void ASameListMove_NeedsTheDisplayToBeTheMembershipOrder()
    {
        // Cause 4: a same-list move addresses ORIGINAL membership rows through DISPLAY positions, so it is only
        // unambiguous while the two coincide. Sorting is reported before filtering because it is the coarser fix.
        Assert.Equal(PlaylistDropRefusal.Sorted, Verdict(sameList: true, naturalOrder: false));
        Assert.Equal(PlaylistDropRefusal.Filtered, Verdict(sameList: true, filtered: true));
        Assert.Equal(PlaylistDropRefusal.Sorted, Verdict(sameList: true, naturalOrder: false, filtered: true));
        Assert.Equal(PlaylistDropRefusal.None, Verdict(sameList: true));
    }

    [Fact]
    public void AForeignCopy_IsUnaffectedByTheHostsSortOrFilter()
    {
        // A copy inserts by DISPLAY position and never has to name an existing membership row, so the ambiguity that
        // blocks a same-list move simply does not arise. Gating it too would refuse a perfectly well-defined drop.
        Assert.Equal(PlaylistDropRefusal.None, Verdict(naturalOrder: false));
        Assert.Equal(PlaylistDropRefusal.None, Verdict(filtered: true));
        Assert.Equal(PlaylistDropRefusal.None, Verdict(naturalOrder: false, filtered: true));
    }

    [Fact]
    public void AcceptsAgreesWithEvaluate_ForEveryCombination()
    {
        // The point of the pair: the target's CanAccept and its refusal caption read one verdict. If these two ever
        // disagreed, a drop could be refused with no reason — or cued with a reason while still succeeding.
        for (int bits = 0; bits < 64; bits++)
        {
            bool editable = (bits & 1) != 0, loading = (bits & 2) != 0, hasTracks = (bits & 4) != 0;
            bool sameList = (bits & 8) != 0, natural = (bits & 16) != 0, filtered = (bits & 32) != 0;
            var verdict = PlaylistDropRefusalRules.Evaluate(editable, loading, hasTracks, sameList, natural, filtered);
            bool accepts = PlaylistDropRefusalRules.Accepts(editable, loading, hasTracks, sameList, natural, filtered);
            Assert.Equal(verdict == PlaylistDropRefusal.None, accepts);
        }
    }
}
