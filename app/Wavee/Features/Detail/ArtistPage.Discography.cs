using System;
using System.Collections.Generic;
using System.Linq;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// Discography: the responsive album/single grids and the "Appears on" measured shelf.
sealed partial class ArtistPage : Component
{
    // ── discography (responsive grids) ───────────────────────────────────────────────────────────────────
    static Element DiscographyGrid(string title, IReadOnlyList<Album> albums, Action<string, string?> go, Action<string> play)
    {
        var cells = albums.Take(24).Select(al => MediaCard.GridCard(al.Cover, al.Name,
            al.Year > 0 ? al.Year + " · " + KindLabel(al.Kind) : KindLabel(al.Kind), al.Uri,
            () => go("album:" + al.Uri, al.Name), () => play(al.Uri))).ToArray();
        return SectionN(title, albums.Count, AutoGrid(180f, WaveeSpace.M, float.NaN, cells));
    }

    static Element AppearsOnShelf(IReadOnlyList<Album> albums, Action<string, string?> go, Action<string> play) => new BoxEl
    {
        Direction = 1,
        Children =
        [
            PagedShelf.Create(
                albums.Count,
                cardAt: (i, w) => MediaCard.Shelf(albums[i].Cover, albums[i].Name,
                    albums[i].Year > 0 ? albums[i].Year.ToString() : KindLabel(albums[i].Kind), albums[i].Uri,
                    () => go("album:" + albums[i].Uri, albums[i].Name), () => play(albums[i].Uri), w),
                measured: true, header: AccentHeader(Loc.Get(Strings.Artist.AppearsOn))),
        ],
    };
}
