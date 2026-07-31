using FluentGpu.Controls;
using FluentGpu.Localization;
using Xunit;

// ── shims (the ActionsTestShims / VirtualCollectionSignalShim precedent) ───────────────────────────────────────────────
// ShellNav.cs is source-included here so the route→(title, glyph) table is pinned against PRODUCTION code. Its only two
// dependencies outside FluentGpu.Engine (referenced transitively via FluentGpu.WindowsApi) are Icons and Route, both from
// FluentGpu.Controls — an assembly this lean test project deliberately does not reference. These stand in for exactly the
// members ShellNav touches. If a real FluentGpu.Controls reference is ever added, delete this region (the source-declared
// types would otherwise shadow the referenced ones with CS0436).
namespace FluentGpu.Controls
{
    internal static class Icons
    {
        public const string MusicNote = "glyph:MusicNote";
        public const string Album = "glyph:Album";
        public const string Contact = "glyph:Contact";
        public const string Calendar = "glyph:Calendar";
        public const string Home = "glyph:Home";
        public const string Search = "glyph:Search";
        public const string Heart = "glyph:Heart";
        public const string RadioTower = "glyph:RadioTower";
        public const string Folder = "glyph:Folder";
        public const string Clock = "glyph:Clock";
        public const string Settings = "glyph:Settings";
        public const string Code = "glyph:Code";
        /// <summary>Added with the "sidebar-customize" arm (Wave 4a): the customizer's destination glyph.</summary>
        public const string Edit = "glyph:Edit";
        public const string ExploreContent = "glyph:ExploreContent";
    }

    /// <summary>Mirror of <c>FluentGpu.Controls.Navigation.Route</c> (name + optional display arg).</summary>
    internal sealed record Route(string Name, string? Arg = null);
}

namespace Wavee.Tests
{
    // The route→destination table (ShellNav.Dest). The regression this class exists for is locked decision 13: a "show:"
    // route renders the shared detail surface exactly like album/artist, so it must carry its own label + podcast glyph —
    // and without that arm it fell past every prefix check into the exact-match switch and hit the "Your Library"
    // DEFAULT, which is what the podcast tabs, the history rows and (now) the pinned podcast rows displayed.
    public class ShellNavDestTests
    {
        static (string Title, string Glyph) Dest(string key, string? arg = null) => ShellNav.Dest(key, arg);

        [Fact]
        public void Show_HasItsOwnTitleAndPodcastGlyph()
        {
            var (title, glyph) = Dest("show:spotify:show:4rOoJ6Egrf8K2IrywzwOMk", "My Podcast");
            Assert.Equal("My Podcast", title);
            Assert.Equal(Icons.RadioTower, glyph);
            Assert.Equal(Dest("podcasts").Glyph, glyph);                    // the SAME glyph the podcasts route uses
        }

        [Fact]
        public void Show_WithNoArg_UsesTheLocalizedShowLabel()
        {
            var (title, glyph) = Dest("show:spotify:show:x");
            Assert.Equal(Loc.Get(Strings.Nav.Show), title);
            Assert.Equal(Icons.RadioTower, glyph);
        }

        [Fact]
        public void Show_DoesNotFallThroughToTheYourLibraryDefault()
        {
            var fallback = Dest("some-unregistered-route");
            var show = Dest("show:spotify:show:x");
            Assert.NotEqual(fallback.Title, show.Title);
            Assert.NotEqual(fallback.Glyph, show.Glyph);
        }

        [Fact]
        public void Prerelease_WearsAlbumLabel()
        {
            var (title, glyph) = Dest("prerelease:spotify:album:z", "Upcoming Album");
            Assert.Equal("Upcoming Album", title);
            Assert.Equal(Icons.Album, glyph);
            Assert.Equal(Loc.Get(Strings.Nav.Album), Dest("prerelease:spotify:album:z").Title);
        }

        [Fact]
        public void Playlist_Album_Artist_UseTheirArgs()
        {
            Assert.Equal(("Peaceful Piano", Icons.MusicNote), Dest("pl:spotify:playlist:1", "Peaceful Piano"));
            Assert.Equal(("Discovery", Icons.Album), Dest("album:spotify:album:1", "Discovery"));
            Assert.Equal(("Daft Punk", Icons.Contact), Dest("artist:spotify:artist:1", "Daft Punk"));

            // …and fall back to their localized kind label when the route carries no display arg.
            Assert.Equal(Loc.Get(Strings.Nav.Playlist), Dest("pl:spotify:playlist:1").Title);
            Assert.Equal(Loc.Get(Strings.Nav.Album), Dest("album:spotify:album:1").Title);
            Assert.Equal(Loc.Get(Strings.Nav.Artist), Dest("artist:spotify:artist:1").Title);
        }

        [Fact]
        public void BrowseCategory_UsesItsPageTitleAndExploreGlyph()
        {
            Assert.Equal(("Music", Icons.ExploreContent), Dest("browse:spotify:page:music", "Music"));
            Assert.Equal(Loc.Get(Strings.Browse.Title), Dest("browse:spotify:page:music").Title);
        }

        [Fact]
        public void UnknownRoute_FallsBackToYourLibrary()
        {
            var (title, glyph) = Dest("no-such-route");
            Assert.Equal(Loc.Get(Strings.Nav.YourLibrary), title);
            Assert.Equal(Icons.MusicNote, glyph);
        }

        [Fact]
        public void FixedRoutes_KeepTheirOwnLabelsAndGlyphs()
        {
            Assert.Equal((Loc.Get(Strings.Nav.Home), Icons.Home), Dest("home"));
            Assert.Equal((Loc.Get(Strings.Nav.Albums), Icons.Album), Dest("albums"));
            Assert.Equal((Loc.Get(Strings.Nav.Artists), Icons.Contact), Dest("artists"));
            Assert.Equal((Loc.Get(Strings.Nav.LikedSongs), Icons.Heart), Dest("liked"));
            Assert.Equal((Loc.Get(Strings.Nav.Podcasts), Icons.RadioTower), Dest("podcasts"));
            Assert.Equal((Loc.Get(Strings.Nav.LocalFiles), Icons.Folder), Dest("local"));
            Assert.Equal((Loc.Get(Strings.Nav.History.Title), Icons.Clock), Dest("history"));
        }

        [Fact]
        public void Search_UsesItsQueryWhenPresent()
        {
            Assert.Equal(("daft punk", Icons.Search), Dest("search", "daft punk"));
            Assert.Equal((Loc.Get(Strings.Nav.Search), Icons.Search), Dest("search"));
        }

        [Fact]
        public void RouteOverload_ForwardsNameAndArg()
        {
            Assert.Equal(Dest("show:spotify:show:x", "The Daily"),
                         ShellNav.Dest(new Route("show:spotify:show:x", "The Daily")));
        }

        // Every pinnable route (F.5.4's closed allow-list) must resolve to a real destination, because a pinned row
        // renders its label and glyph THROUGH ShellNav.Dest — a route that fell to the default would paint "Your Library".
        [Fact]
        public void EveryPinnableRoute_ResolvesToSomethingOtherThanTheDefault()
        {
            var fallback = Dest("route-that-does-not-exist");
            foreach (var route in SidebarPinId.PinnableRoutes)
                Assert.NotEqual(fallback.Title, Dest(route).Title);
        }
    }
}
