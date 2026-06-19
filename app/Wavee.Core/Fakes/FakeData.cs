namespace Wavee.Core;

/// <summary>The in-memory, deterministic catalog that drives the whole skeleton with NO network. Cover art is BUNDLED
/// locally (app/Wavee/assets/covers, copied next to the exe) so cards show real art offline; <see cref="Cover"/>
/// resolves a deterministic file path per seed.</summary>
public static class FakeData
{
    /// <summary>How many cover images are bundled under assets/covers (cover00.jpg … coverNN.jpg).</summary>
    const int CoverCount = 16;
    static readonly (string Title, string Artist)[] Seed =
    [
        ("LAST GOODBYE", "sunkis"), ("mellow pop wistful saturday late night", "Spotify"), ("\uC6B0\uC6B8\uD574", "Christos"),
        ("Iced Americano", "Christos"), ("Dalkom Cafe", "Christos"), ("Nostalgia 2000s Mix", "Spotify"),
        ("Strobe", "deadmau5"), ("Innerbloom", "RÜFÜS DU SOL"), ("Breathe", "Télépopmusik"),
        ("Lebanese Blonde", "Thievery Corporation"), ("Undo", "Björk"), ("Genesis", "Grimes"),
        ("An Ending (Ascent)", "Brian Eno"), ("Svefn-g-englar", "Sigur Rós"), ("Intro", "The xx"),
        ("Weird Fishes", "Radiohead"),
    ];

    // ARGB palette (one per seed) for the now-playing recolor; deep, saturated, art-derived feel.
    static readonly uint[] Accents =
    [
        0xFF2E6CE0, 0xFFB5532A, 0xFF1F1147, 0xFF24506B, 0xFF1E5F4F, 0xFF6A2D6A,
        0xFF7A5A2E, 0xFFB23A48, 0xFF2A6F5A, 0xFF8A6A2A, 0xFF5A2A7A, 0xFF2A7A6A,
        0xFF3A5A8A, 0xFF6A3A5A, 0xFF2A2A3A, 0xFF3A6A4A,
    ];

    public static Image Cover(int i, int px = 640)
    {
        int n = ((i % CoverCount) + CoverCount) % CoverCount;   // deterministic, handles any seed sign
        string path = Path.Combine(AppContext.BaseDirectory, "assets", "covers", $"cover{n:D2}.jpg");
        return new Image(path, px, px);
    }

    static ArtistRef ArtistRef(int i) { var s = Seed[i % Seed.Length]; return new ArtistRef($"ar{i % Seed.Length}", $"spotify:artist:ar{i % Seed.Length}", s.Artist); }

    public static Track Track(int i)
    {
        var s = Seed[i % Seed.Length];
        var album = new AlbumRef($"al{i}", $"spotify:album:al{i}", s.Title);
        long dur = 138_000 + (i * 37 % 150) * 1000L;       // 2:18 – ~4:50
        return new Track($"tr{i}", $"spotify:track:tr{i}", s.Title, [ArtistRef(i)], album, dur, i % 6 == 0, Cover(i, 64), HasVideo: i % 4 == 1);
    }

    public static Track[] Tracks(int count, int offset = 0)
    {
        var a = new Track[count];
        for (int i = 0; i < count; i++) a[i] = Track(offset + i);
        return a;
    }

    public static Palette PaletteFor(int seed)
    {
        uint accent = Accents[seed % Accents.Length];
        // Derive a dark base + tinted-dark from the accent (rough; the real PaletteExtractor lands later).
        uint Dark(uint c, float k) => 0xFF000000u
            | ((uint)(((c >> 16) & 0xFF) * k) << 16)
            | ((uint)(((c >> 8) & 0xFF) * k) << 8)
            | (uint)((c & 0xFF) * k);
        return new Palette(BackgroundDark: Dark(accent, 0.16f), TintedDark: Dark(accent, 0.28f), Light: 0xFFFFFFFF, Accent: accent);
    }

    // A deterministic release shape per index, so the catalog spans all four kinds (and the More-by/home shelves show a mix).
    static (AlbumKind Kind, int Count) AlbumShape(int i) => (i % 6) switch
    {
        0 => (AlbumKind.Single, 1),
        1 => (AlbumKind.EP, 5),
        2 => (AlbumKind.Album, 12),
        3 => (AlbumKind.Single, 2),
        4 => (AlbumKind.Compilation, 18),
        _ => (AlbumKind.Album, 10),
    };

    static Track[] AlbumTracks(int count, int offset, ArtistRef artist, bool various)
    {
        var a = new Track[count];
        for (int i = 0; i < count; i++)
        {
            var t = Track(offset + i);
            long plays = 60_000_000L / (i + 1) + ((offset * 131 % 37) + 1) * 250_000L;   // descending → track 1 is the "hit"
            // A compilation is various-artists (each track keeps its own artist); a single/EP/album is one artist.
            a[i] = t with { PlayCount = plays, Artists = various ? t.Artists : [artist] };
        }
        return a;
    }

    public static Album Album(int i)
    {
        var s = Seed[i % Seed.Length];
        var (kind, count) = AlbumShape(i);
        var artist = ArtistRef(i);
        var tracks = AlbumTracks(count, i * 10, artist, kind == AlbumKind.Compilation);
        // A single leads with its official video → the detail page surfaces the "Watch the official video" card.
        if (kind == AlbumKind.Single && tracks.Length > 0) tracks[0] = tracks[0] with { HasVideo = true };
        return new Album($"al{i}", $"spotify:album:al{i}", s.Title, Cover(i, 300), [artist], 2014 + (i % 11), tracks.Length, tracks, kind);
    }

    public static Artist Artist(int i)
    {
        var s = Seed[i % Seed.Length];
        var albums = new Album[6];
        for (int k = 0; k < albums.Length; k++) albums[k] = Album(i * 6 + k);
        return new Artist($"ar{i % Seed.Length}", $"spotify:artist:ar{i % Seed.Length}", s.Artist, Cover(i, 300), albums);
    }

    public static Playlist Playlist(int i, int trackCount = 40)
    {
        var s = Seed[i % Seed.Length];
        // Editorial/made-for-you playlists (a "Spotify"-billed seed) carry NO per-track added-at/added-by — the detail
        // page then hides those columns. A user playlist stamps a descending "added at" (newest first); some are
        // collaborative (≥2 contributors), which is what makes the page reveal the Added-By column.
        bool curated = s.Artist == "Spotify";
        string owner = curated ? "Spotify" : "Christos";
        var tracks = Tracks(trackCount, i * 100);
        if (!curated)
        {
            bool collab = i % 3 == 1;
            string[] people = collab ? ["Christos", "Alex", "Mia"] : ["Christos"];
            var now = DateTimeOffset.Now;
            var stamped = new Track[tracks.Length];
            for (int t = 0; t < tracks.Length; t++)
                stamped[t] = tracks[t] with { AddedAt = now.AddDays(-(t * 2L + (i % 5))), AddedBy = people[t % people.Length] };
            tracks = stamped;
        }
        return new Playlist($"pl{i}", $"spotify:playlist:pl{i}", $"{s.Title} Mix", $"The best of {s.Artist} and friends.",
            owner, Cover(i + 100, 300), tracks.Length, tracks);
    }

    /// <summary>The big list (Liked Songs) — generated on demand so 50k stays cheap.</summary>
    public static Track[] LikedSongs(int count = 5000) => Tracks(count, 1000);

    // ── Sidebar IA: the "Your Library" counts + the (folder-capable) playlist tree ───────────────────────────────────
    /// <summary>The Your-Library badge counts shown in the sidebar.</summary>
    public static LibraryStats LibraryStats() => new(Albums: 13, Artists: 12, LikedSongs: 161, Podcasts: 7);

    // (Name, Owner, TrackCount, Seed) — Seed is the FakeData index, so the URI `spotify:playlist:pl{Seed}` round-trips
    // through GetPlaylistAsync (IndexFromUri reads the trailing digits). Seeds are distinct → no URI collisions.
    static readonly (string Name, string Owner, int Count, int Seed)[] PlaylistSeed =
    [
        ("mellow pop wistful saturday late night", "Spotify", 50, 1),
        ("My Playlist #6", "Christos", 4, 6),
        ("우울해", "Christos", 22, 2),
        ("Iced Americano", "Christos", 15, 3),
        ("Dalkom Cafe", "Christos", 50, 4),
        ("Nostalgia 2000s Mix", "Spotify", 30, 5),
        ("Henry Moodie Mix", "Spotify", 50, 7),
    ];

    static PlaylistSummary PlaylistSummary(int k)
    {
        var (name, owner, count, seed) = PlaylistSeed[k];
        return new PlaylistSummary($"spotify:playlist:pl{seed}", name, owner, count, Cover(seed + 100, 300));
    }

    /// <summary>The sidebar playlist tree: loose playlists + one collapsible folder (WaveeMusic's hierarchical shape).</summary>
    public static IReadOnlyList<PlaylistNode> PlaylistTree() =>
    [
        new PlaylistLeaf(PlaylistSummary(0)),
        new PlaylistLeaf(PlaylistSummary(1)),
        new PlaylistFolder("folder:cafe", "Cafe & chill", [PlaylistSummary(2), PlaylistSummary(4), PlaylistSummary(3)]),
        new PlaylistLeaf(PlaylistSummary(5)),
        new PlaylistLeaf(PlaylistSummary(6)),
    ];

    /// <summary>Flattened playlist summaries (folders expanded) — used to seed the "+ New playlist" growth list.</summary>
    public static IReadOnlyList<PlaylistSummary> UserPlaylists()
    {
        var list = new List<PlaylistSummary>();
        foreach (var node in PlaylistTree())
        {
            if (node is PlaylistLeaf leaf) list.Add(leaf.Playlist);
            else if (node is PlaylistFolder f) list.AddRange(f.Items);
        }
        return list;
    }

    public static LibraryItem[] Library()
    {
        var items = new List<LibraryItem>();
        items.Add(new LibraryItem("spotify:collection:tracks", "Liked Songs", "Playlist · 5,000 songs", Cover(900, 80), LibraryItemKind.Playlist));
        for (int i = 0; i < 8; i++) { var p = Playlist(i); items.Add(new LibraryItem(p.Uri, p.Name, $"Playlist · {p.TrackCount} songs", p.Cover, LibraryItemKind.Playlist)); }
        for (int i = 0; i < 6; i++) { var a = Album(i + 20); items.Add(new LibraryItem(a.Uri, a.Name, $"Album · {a.Artists[0].Name}", a.Cover, LibraryItemKind.Album)); }
        for (int i = 0; i < 4; i++) { var ar = Artist(i + 30); items.Add(new LibraryItem(ar.Uri, ar.Name, "Artist", ar.Image, LibraryItemKind.Artist)); }
        return items.ToArray();
    }

    public static SearchResults Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return new SearchResults([], [], [], []);
        var tracks = Tracks(8, query.Length * 3);
        var albums = new[] { Album(query.Length), Album(query.Length + 1), Album(query.Length + 2) };
        var artists = new[] { Artist(query.Length), Artist(query.Length + 1) };
        var playlists = new[] { Playlist(query.Length), Playlist(query.Length + 1) };
        return new SearchResults(tracks, albums, artists, playlists);
    }

    public static LyricsDocument Lyrics(string trackId)
    {
        string[] lines = ["Driving in the city lights", "Watching as the world goes by", "Every heartbeat keeps the time", "Holding on, we're almost there"];
        var doc = new List<LyricLine>();
        long t = 0;
        foreach (var line in lines)
        {
            var words = line.Split(' ');
            var syl = new List<LyricSyllable>();
            long w = t;
            foreach (var word in words) { syl.Add(new LyricSyllable(w, w + 400, word + " ")); w += 450; }
            doc.Add(new LyricLine(t, line, syl));
            t += 3600;
        }
        return new LyricsDocument(trackId, IsSynced: true, doc);
    }

    public static QueueEntry[] DefaultQueue()
    {
        var q = new List<QueueEntry>();
        q.Add(new QueueEntry("q0", Track(0), QueueBucket.NowPlaying, false));
        for (int i = 1; i <= 3; i++) q.Add(new QueueEntry("q" + i, Track(i), QueueBucket.UserQueue, false));
        for (int i = 4; i <= 11; i++) q.Add(new QueueEntry("q" + i, Track(i), QueueBucket.NextUp, i > 8));
        return q.ToArray();
    }
}
