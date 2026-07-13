using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.WindowsApi.Dialogs;
using Wavee.Backend.Spotify;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The paged magazine shelves below the discography: on-tour banner, music videos, playlists, concerts, merch,
// gallery, and the "fans also like" / related shelves.
sealed partial class ArtistPage : Component
{
    // ── on-tour banner ───────────────────────────────────────────────────────────────────────────────────
    Element TourBannerCard(TourBanner t, Action onClick) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.L,
        Padding = new Edges4(WaveeSpace.L, WaveeSpace.L, WaveeSpace.L, WaveeSpace.L),
        Corners = CornerRadius4.All(WaveeRadius.Card), Fill = Tok.FillCardSecondary,
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, HoverFill = Tok.FillCardDefault,
        HoverScale = 1.005f, PressScale = 0.997f, OnClick = onClick,
        Children =
        [
            new BoxEl { Width = 44f, Height = 44f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = CornerRadius4.All(22f), Fill = _accent,
                Children = [ Icon(t.IsLive ? Mdl.RadioTower : Mdl.Calendar, 18f, Tok.TextOnAccentPrimary) ] },
            new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Gap = 2f,
                Children =
                [
                    new TextEl(t.Eyebrow) { Size = 11f, Weight = 700, Color = Tok.AccentTextPrimary, CharSpacing = 30f },
                    new TextEl(t.Headline) { Size = 16f, Weight = 700, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    new TextEl(t.Subline) { Size = 13f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                ] },
            Icon(Mdl.ChevronRight, 16f, Tok.TextSecondary),
        ],
    };

    // ── music videos (16:9 shelf) ────────────────────────────────────────────────────────────────────────
    Element MusicVideosShelf(IReadOnlyList<MusicVideo> videos, Action<string> play) => new BoxEl
    {
        Direction = 1,
        Children =
        [
            PagedShelf.Create(
                Math.Min(videos.Count, 16),
                cardAt: (i, w) => MediaCard.VideoCard(videos[i].Thumbnail, videos[i].Title, Dur(videos[i].DurationMs),
                    videos[i].TrackUri, () => play(videos[i].TrackUri), () => play(videos[i].TrackUri), w,
                    menu: CardMenu(videos[i].TrackUri, videos[i].Title)),
                measured: true, header: AccentHeader(Loc.Get(Strings.Artist.MusicVideos))),
        ],
    };

    // ── playlists and discovery ──────────────────────────────────────────────────────────────────────────
    Element PlaylistsShelf(IReadOnlyList<PlaylistRef> pls, Action<string, string?> go, Action<string> play) => new BoxEl
    {
        Direction = 1,
        Children =
        [
            PagedShelf.Create(
                Math.Min(pls.Count, 16),
                cardAt: (i, w) => MediaCard.Shelf(pls[i].Cover, pls[i].Name, pls[i].Subtitle, pls[i].Uri,
                    () => go("pl:" + pls[i].Uri, pls[i].Name), () => play(pls[i].Uri), w,
                    menu: CardMenu(pls[i].Uri, pls[i].Name)),
                measured: true, header: AccentHeader(Loc.Get(Strings.Artist.PlaylistsDiscovery))),
        ],
    };

    // ── upcoming concerts ────────────────────────────────────────────────────────────────────────────────
    Element ConcertsRow(IReadOnlyList<Concert> concerts) => new BoxEl
    {
        Direction = 1,
        Children =
        [
            PagedShelf.Create(
                Math.Min(concerts.Count, 12),
                cardAt: (i, w) => ConcertStub(concerts[i]),
                measured: true, header: AccentHeader(Loc.Get(Strings.Artist.UpcomingConcerts))),
        ],
    };

    static Element ConcertStub(Concert c) => new BoxEl
    {
        Direction = 0, Grow = 1f, Gap = WaveeSpace.M, AlignItems = FlexAlign.Center,
        Padding = new Edges4(WaveeSpace.M, WaveeSpace.M, WaveeSpace.M, WaveeSpace.M),
        Corners = CornerRadius4.All(WaveeRadius.Card), Fill = Tok.FillCardSecondary,
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        Children =
        [
            new BoxEl { Direction = 1, Width = 48f, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = 0f,
                Children =
                [
                    new TextEl(c.Date.ToString("MMM", CultureInfo.InvariantCulture).ToUpperInvariant()) { Size = 11f, Weight = 700, Color = Tok.AccentTextPrimary, CharSpacing = 10f },
                    new TextEl(c.Date.Day.ToString()) { Size = 24f, Weight = 800, Color = Tok.TextPrimary },
                ] },
            new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Gap = 2f,
                Children =
                [
                    new TextEl(c.Venue) { Size = 14f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    new BoxEl { Direction = 0, Gap = 4f, AlignItems = FlexAlign.Center,
                        Children = [ Icon(Mdl.MapPin, 11f, Tok.TextSecondary), new TextEl(c.City) { Size = 12f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis } ] },
                ] },
        ],
    };

    // ── merch ────────────────────────────────────────────────────────────────────────────────────────────
    Element MerchRow(IReadOnlyList<MerchItem> merch) => new BoxEl
    {
        Direction = 1,
        Children =
        [
            PagedShelf.Create(
                Math.Min(merch.Count, 12),
                cardAt: (i, w) => MerchCard(merch[i], w),
                measured: true, header: AccentHeader(Loc.Get(Strings.Artist.Merch))),
        ],
    };

    static Element MerchCard(MerchItem m, float w) => new BoxEl
    {
        Direction = 1, Gap = WaveeSpace.S, Grow = 1f, ClipToBounds = true,
        Padding = new Edges4(WaveeSpace.S, WaveeSpace.S, WaveeSpace.S, WaveeSpace.M),
        Corners = CornerRadius4.All(WaveeRadius.Card), Fill = Tok.FillCardSecondary,
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, HoverFill = Tok.FillCardDefault, HoverScale = 1.02f,
        Children =
        [
            new BoxEl { ZStack = true, ClipToBounds = true, Corners = CornerRadius4.All(WaveeRadius.Control),
                Children = [ Surfaces.ArtworkFill(m.Image, WaveeRadius.Control) ] },
            new TextEl(m.Name) { Size = 13f, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis },
            new TextEl(m.Price) { Size = 13f, Weight = 700, Color = Tok.AccentTextPrimary, MaxLines = 1 },
        ],
    };

    // ── gallery ──────────────────────────────────────────────────────────────────────────────────────────
    Element GalleryStrip(IReadOnlyList<Image> photos) => new BoxEl
    {
        Direction = 1,
        Children =
        [
            PagedShelf.Create(
                Math.Min(photos.Count, 16),
                cardAt: (i, w) => new BoxEl { Width = w, Height = w, Corners = CornerRadius4.All(WaveeRadius.Card), ClipToBounds = true,
                    OnClick = () => OpenGallery(photos, i), Cursor = CursorId.Hand, HoverScale = 1.015f, PressScale = 0.985f,
                    Children = [ Surfaces.Artwork(photos[i], i, w, w, WaveeRadius.Card, decodePx: 480) ] },
                measured: true, header: AccentHeader(Loc.Get(Strings.Artist.Gallery))),
        ],
    };

    void OpenGallery(IReadOnlyList<Image> photos, int initialIndex)
    {
        if (photos.Count == 0 || _menuOverlay is null) return;
        ContentDialog.Show(_menuOverlay, d =>
        {
            d.Title = Loc.Get(Strings.Artist.Gallery);
            d.DialogWidth = 548f;
            d.Content = Embed.Comp(() => new ArtistGalleryViewer(photos, initialIndex));
            d.CloseText = Loc.Get(Strings.Auth.Close);
            d.DefaultButton = ContentDialog.DefaultBtn.Close;
        });
    }

    // ── fans also like ───────────────────────────────────────────────────────────────────────────────────
    Element RelatedShelf(IReadOnlyList<RelatedArtist> related, Action<string, string?> go, Action<string> play) => new BoxEl
    {
        Direction = 1,
        Children =
        [
            PagedShelf.Create(
                related.Count,
                cardAt: (i, w) => MediaCard.Shelf(related[i].Image, related[i].Name, Loc.Get(Strings.Search.TypeArtist), related[i].Uri,
                    () => go("artist:" + related[i].Uri, related[i].Name), () => play(related[i].Uri), w, circular: true,
                    menu: CardMenu(related[i].Uri, related[i].Name)),
                measured: true, header: AccentHeader(Loc.Get(Strings.Detail.FansAlsoLike))),
        ],
    };

    Element FansShelf(IReadOnlyList<Artist> fans, Action<string, string?> go, Action<string> play) => new BoxEl
    {
        Direction = 1,
        Children =
        [
            PagedShelf.Create(
                fans.Count,
                cardAt: (i, w) => MediaCard.Shelf(fans[i].Image, fans[i].Name, Loc.Get(Strings.Search.TypeArtist), fans[i].Uri,
                    () => go("artist:" + fans[i].Uri, fans[i].Name), () => play(fans[i].Uri), w, circular: true,
                    menu: CardMenu(fans[i].Uri, fans[i].Name)),
                measured: true, header: AccentHeader(Loc.Get(Strings.Detail.FansAlsoLike))),
        ],
    };
}

/// <summary>Modal artist-gallery viewer. FlipView owns keyboard, wheel, touch-drag and adjacent slide navigation; the
/// export action downloads the selected image's original CDN bytes through the native Save As picker.</summary>
sealed class ArtistGalleryViewer : Component
{
    readonly IReadOnlyList<Image> _photos;
    readonly int _initialIndex;
    readonly Signal<bool> _saving = new(false);

    public ArtistGalleryViewer(IReadOnlyList<Image> photos, int initialIndex)
    {
        _photos = photos;
        _initialIndex = Math.Clamp(initialIndex, 0, Math.Max(0, photos.Count - 1));
    }

    public override Element Render()
    {
        var (selected, setSelected) = UseState(_initialIndex);
        var post = UsePost();
        int current = Math.Clamp(selected, 0, Math.Max(0, _photos.Count - 1));
        var pages = new Element[_photos.Count];
        for (int i = 0; i < pages.Length; i++)
        {
            var photo = _photos[i];
            pages[i] = new BoxEl
            {
                Width = 500f, Height = 500f, Fill = Tok.FillSolidBase,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children =
                [
                    new ImageEl
                    {
                        Key = "gallery-image:" + photo.Url,
                        Source = photo.Url, Width = 500f, Height = 500f,
                        Fit = ImageFit.Contain, Placeholder = Tok.FillSolidBase,
                        Transition = ImageTransition.Fade(140f),
                    },
                ],
            };
        }

        void Export() => ExportImage(_photos[current], current, post);

        return new BoxEl
        {
            Direction = 1, Gap = WaveeSpace.M,
            Children =
            [
                FlipView.Create(pages, 500f, 500f, current, setSelected),
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M,
                    Children =
                    [
                        new TextEl($"{current + 1} / {_photos.Count}") { Grow = 1f, Size = 12f, Color = Tok.TextSecondary },
                        Button.Accent(_saving.Value ? "Exporting…" : "Export image", Export, isEnabled: !_saving.Value),
                    ],
                },
            ],
        };
    }

    async void ExportImage(Image photo, int index, Action<Action> post)
    {
        if (_saving.Peek() || photo.Url.Length == 0) return;
        string ext = ExtensionOf(photo.Url);
        string? path = FilePicker.SaveFile(FluentApp.WindowHandle, "Export gallery image",
            $"artist-gallery-{index + 1:D2}{ext}",
            ("Images", "*.jpg;*.jpeg;*.png;*.webp"), ("All files", "*.*"));
        if (path is null) return;

        _saving.Value = true;
        try
        {
            using var response = await HttpPools.Get(HttpPool.Cdn).GetAsync(photo.Url).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            byte[] bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
            post(() =>
            {
                _saving.Value = false;
                Toasts.Show("Image exported", ToastSeverity.Success);
            });
        }
        catch (Exception ex)
        {
            post(() =>
            {
                _saving.Value = false;
                Toasts.Show("Image export failed: " + ex.Message, ToastSeverity.Critical);
            });
        }
    }

    static string ExtensionOf(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            string ext = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
            if (ext is ".jpg" or ".jpeg" or ".png" or ".webp") return ext;
        }
        return ".jpg";
    }
}
