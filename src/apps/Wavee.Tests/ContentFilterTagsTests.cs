using System;
using System.Collections.Generic;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

public class ContentFilterTagsTests
{
    static Track T(string id, params string[] tags) => new(
        id, "spotify:track:" + id, id,
        Array.Empty<ArtistRef>(), new AlbumRef("", "", ""),
        180_000, false, null,
        Tags: tags.Length == 0 ? null : tags);

    static IReadOnlyList<Track> Many(int n, params string[] tags)
    {
        var list = new List<Track>(n);
        for (int i = 0; i < n; i++) list.Add(T("t" + i, tags));
        return list;
    }

    [Fact]
    public void NoTags_YieldsNoChips()
    {
        Assert.Empty(ContentFilterTags.Derive(Many(10)));
        Assert.Empty(ContentFilterTags.Derive(Array.Empty<Track>()));
    }

    [Fact]
    public void RareTag_DoesNotEarnAChip()
    {
        // Two carriers is below the floor: a chip nobody can usefully tap is worse than no chip.
        var tracks = new List<Track> { T("a", "K-Pop"), T("b", "K-Pop") };
        Assert.Empty(ContentFilterTags.Derive(tracks));
    }

    [Fact]
    public void CommonTag_EarnsAChip_AtTheFloor()
    {
        Assert.Equal(["K-Pop"], ContentFilterTags.Derive(Many(3, "K-Pop")));
    }

    [Fact]
    public void ChipsAreOrderedByCarrierCountDescending()
    {
        var tracks = new List<Track>();
        for (int i = 0; i < 3; i++) tracks.Add(T("rare" + i, "Chill"));
        for (int i = 0; i < 9; i++) tracks.Add(T("common" + i, "Pop"));
        for (int i = 0; i < 5; i++) tracks.Add(T("mid" + i, "Dance"));

        Assert.Equal(["Pop", "Dance", "Chill"], ContentFilterTags.Derive(tracks));
    }

    [Fact]
    public void CasingVariantsCollapseToOneChip()
    {
        // display_name is absent on some descriptors, so the lowercase wire token arrives instead — same concept.
        var tracks = new List<Track> { T("a", "K-Pop"), T("b", "k-pop"), T("c", "K-POP") };
        Assert.Single(ContentFilterTags.Derive(tracks));
    }

    [Fact]
    public void ChipCountIsCapped()
    {
        var tracks = new List<Track>();
        for (int tag = 0; tag < 30; tag++)
            for (int i = 0; i < 3 + tag; i++)   // distinct counts so the ordering is total, not tie-broken
                tracks.Add(T($"t{tag}_{i}", "tag" + tag));

        var chips = ContentFilterTags.Derive(tracks);
        Assert.Equal(10, chips.Count);
        Assert.Equal("tag29", chips[0]);   // the most-carried tag leads
    }

    [Fact]
    public void OrderIsStableAcrossEqualCounts()
    {
        var tracks = new List<Track>();
        foreach (var tag in new[] { "Zeta", "Alpha", "Mid" })
            for (int i = 0; i < 4; i++) tracks.Add(T(tag + i, tag));

        // Equal counts fall back to name order, so an enrichment pass cannot visibly shuffle the bar.
        Assert.Equal(["Alpha", "Mid", "Zeta"], ContentFilterTags.Derive(tracks));
    }
}
