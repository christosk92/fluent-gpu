using System;
using System.Collections.Generic;
using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Features.Concerts;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>Shared concert compositions built exclusively from existing FluentGpu primitives. None of these factories
/// owns routing or playback; callers provide the one navigation action explicitly.</summary>
public static class ConcertUi
{
    public static Element DateBlock(DateTimeOffset date, bool compact = false)
    {
        var culture = CultureInfo.CurrentCulture;
        float width = compact ? 52f : 64f;
        float height = compact ? 56f : 64f;
        string dayName = date.ToString("ddd", culture).ToUpper(culture);
        string day = date.ToString("dd", culture);
        string month = date.ToString("MMM", culture).ToUpper(culture);

        return new BoxEl
        {
            Width = width, Height = height, Shrink = 0f,
            Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(WaveeRadius.Control), Fill = Tok.AccentDefault,
            Children =
            [
                Caption(dayName) with { Color = Tok.TextOnAccentSecondary, MaxLines = 1 },
                BodyStrong(day) with { Color = Tok.TextOnAccentPrimary, MaxLines = 1 },
                Caption(month) with { Color = Tok.TextOnAccentSecondary, MaxLines = 1 },
            ],
        };
    }

    public static Element ScheduleRow(Concert concert, Action onClick, bool narrow = false) =>
        EventCard(concert, onClick, narrow ? 96f : 88f, compactDate: narrow, showArtwork: false, maxTitleLines: narrow ? 2 : 1);

    public static Element GridCard(Concert concert, Action onClick) =>
        EventCard(concert, onClick, 112f, compactDate: false, showArtwork: false, maxTitleLines: 2);

    public static Element ShelfCard(Concert concert, Action onClick) =>
        EventCard(concert, onClick, 124f, compactDate: false, showArtwork: concert.Image is not null, maxTitleLines: 2);

    static Element EventCard(Concert concert, Action onClick, float height, bool compactDate, bool showArtwork, int maxTitleLines)
    {
        string title = string.IsNullOrWhiteSpace(concert.Title) ? "Concert" : concert.Title!;
        string place = PlaceLine(concert);
        var text = new List<Element>(3)
        {
            WaveeType.TrackTitle(title) with
            {
                Grow = 1f, Basis = 0f, MinWidth = 0f, Wrap = TextWrap.Wrap, MaxLines = maxTitleLines,
                Trim = TextTrim.CharacterEllipsis,
            },
        };
        if (place.Length > 0)
            text.Add(WaveeType.TrackMeta(place) with
            {
                Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            });
        if (concert.IsNearUser) text.Add(StatusPill("Near you"));

        Element leading = showArtwork && concert.Image is { } image
            ? Surfaces.Artwork(image, StableSeed(concert.Uri), 76f, 76f, WaveeRadius.Control, decodePx: 152)
            : DateBlock(concert.Date, compactDate);

        return new BoxEl
        {
            Key = concert.Uri,
            Direction = 0, Height = height, MinWidth = 0f,
            AlignItems = FlexAlign.Center, Gap = WaveeSpace.M,
            Padding = new Edges4(WaveeSpace.M, WaveeSpace.S, WaveeSpace.M, WaveeSpace.S),
            Corners = CornerRadius4.All(WaveeRadius.Card),
            Fill = Tok.FillCardDefault, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, ClipToBounds = true,
            Role = AutomationRole.Button, Focusable = true, FocusVisualMargin = new Edges4(2f, 2f, 2f, 2f),
            Cursor = CursorId.Hand, OnClick = onClick,
            Children =
            [
                leading,
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = WaveeSpace.XS,
                    Justify = FlexJustify.Center, Children = text.ToArray(),
                },
                Icon(Mdl.ChevronRight, 16f, Tok.TextSecondary) with { Shrink = 0f },
            ],
        };
    }

    public static Element LocationButton(string label, Action onClick) => new BoxEl
    {
        Direction = 0, MinHeight = WaveeSize.ControlH, AlignItems = FlexAlign.Center, Gap = WaveeSpace.S,
        Padding = new Edges4(WaveeSpace.M, WaveeSpace.XS, WaveeSpace.M, WaveeSpace.XS),
        Corners = CornerRadius4.All(WaveeRadius.Control),
        Fill = Tok.FillControlDefault, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
        BorderWidth = 1f, BorderBrush = Tok.ControlElevationBorder,
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick,
        Children =
        [
            Icon(Mdl.MapPin, 16f, Tok.TextSecondary),
            Body(label) with { Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            Icon(Mdl.ChevronDown, 12f, Tok.TextSecondary),
        ],
    };

    public static Element WideEditorialDestination(
        Image? artwork,
        string eyebrow,
        string title,
        string subtitle,
        string actionLabel,
        Action onClick,
        float fallbackWidth = 1000f) =>
        Responsive.Of(width => BuildWideEditorial(
            artwork, eyebrow, title, subtitle, actionLabel, onClick,
            width > 0f ? width : fallbackWidth), fallback: fallbackWidth);

    static Element BuildWideEditorial(
        Image? artwork,
        string eyebrow,
        string title,
        string subtitle,
        string actionLabel,
        Action onClick,
        float width)
    {
        var metrics = ConcertLayout.WideEditorial(width);
        float artWidth = metrics.ArtworkWidth(width);
        float copyWidth = MathF.Max(180f, width - artWidth + MathF.Min(96f, artWidth * 0.42f));
        Element art = artwork?.Url is { Length: > 0 } url
            ? new ImageEl
            {
                Source = url, Width = artWidth, Height = metrics.Height, Fit = ImageFit.Cover,
                Placeholder = Tok.FillCardSecondary,
            }
            : new BoxEl { Width = artWidth, Height = metrics.Height, Fill = Tok.FillCardSecondary };

        return new BoxEl
        {
            Height = metrics.Height, MinWidth = 0f, ZStack = true, ClipToBounds = true,
            Corners = CornerRadius4.All(WaveeRadius.Card),
            Fill = Tok.FillCardDefault, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, Shadow = Elevation.Card,
            Role = AutomationRole.Button, Focusable = true, FocusVisualMargin = new Edges4(2f, 2f, 2f, 2f),
            Cursor = CursorId.Hand, OnClick = onClick,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Justify = FlexJustify.End,
                    Children = [ new BoxEl { Grow = 1f }, art ],
                },
                new BoxEl
                {
                    Gradient = LinearGradient(0f,
                        new GradientStop(0f, Tok.FillCardDefault),
                        new GradientStop(0.52f, Tok.FillCardDefault),
                        new GradientStop(1f, Tok.FillCardDefault with { A = 0f })),
                },
                new BoxEl
                {
                    Direction = 1, Width = copyWidth, Padding = new Edges4(metrics.Padding, metrics.Padding, metrics.Padding, metrics.Padding),
                    Gap = WaveeSpace.S, Justify = FlexJustify.Center,
                    Children =
                    [
                        Caption(eyebrow.ToUpper(CultureInfo.CurrentCulture)) with
                        {
                            Color = Tok.AccentTextPrimary, Weight = 700, CharSpacing = 40f, MaxLines = 1,
                            Trim = TextTrim.CharacterEllipsis,
                        },
                        WaveeType.PageHero(title) with
                        {
                            Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis,
                        },
                        Body(subtitle) with
                        {
                            Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = metrics.SubtitleLines,
                            Trim = TextTrim.CharacterEllipsis,
                        },
                        new BoxEl
                        {
                            Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.S,
                            Children =
                            [
                                BodyStrong(actionLabel) with { Color = Tok.AccentTextPrimary },
                                Icon(Mdl.ChevronRight, 14f, Tok.AccentTextPrimary),
                            ],
                        },
                    ],
                },
            ],
        };
    }

    static Element StatusPill(string text) => new BoxEl
    {
        AlignSelf = FlexAlign.Start, Padding = new Edges4(WaveeSpace.S, 2f, WaveeSpace.S, 2f),
        Corners = CornerRadius4.All(WaveeRadius.Pill), Fill = Tok.AccentSubtle,
        Children = [ Caption(text) with { Color = Tok.AccentTextPrimary, MaxLines = 1 } ],
    };

    static string PlaceLine(Concert concert)
    {
        if (concert.Venue.Length == 0) return concert.City;
        if (concert.City.Length == 0) return concert.Venue;
        return concert.Venue + " - " + concert.City;
    }

    static int StableSeed(string value)
    {
        int hash = 17;
        for (int i = 0; i < value.Length; i++) hash = unchecked(hash * 31 + value[i]);
        return hash & 0x7fffffff;
    }
}

/// <summary>Presentational location-picker content for an anchored overlay. Query/results/loading/error are live signals,
/// so the panel mounts once per open and never depends on changing constructor values.</summary>
public sealed class ConcertLocationPickerPanel : Component
{
    public required Signal<string> Query;
    public required IReadSignal<IReadOnlyList<ConcertPlace>> Results;
    public required IReadSignal<bool> Loading;
    public required IReadSignal<string?> Error;
    public required Action<ConcertPlace> Select;
    public required Action UseMyLocation;
    public string SearchPlaceholder = "Search cities";
    public string UseMyLocationLabel = "Use my location";
    public string NoResultsLabel = "No locations found";

    public override Element Render()
    {
        var results = Results.Value;
        bool loading = Loading.Value;
        string? error = Error.Value;
        var rows = new Element[results.Count];
        for (int i = 0; i < results.Count; i++)
        {
            var place = results[i];
            rows[i] = LocationRow(place, () => Select(place));
        }

        Element body = rows.Length > 0
            ? new ScrollEl
            {
                ContentSized = true, MaxHeight = 320f,
                Content = new BoxEl { Direction = 1, Gap = 2f, Children = rows },
            }
            : new BoxEl
            {
                Height = 48f, AlignItems = FlexAlign.Center,
                Children = [ WaveeType.TrackMeta(loading ? "Searching..." : NoResultsLabel) ],
            };

        var children = new List<Element>(5)
        {
            Embed.Comp(() => new EditableText
            {
                Placeholder = SearchPlaceholder, Width = 340f, Height = WaveeSize.ControlH, Text = Query,
            }),
            Button.Standard(loading ? "Locating..." : UseMyLocationLabel, UseMyLocation, isEnabled: !loading),
        };
        if (!string.IsNullOrWhiteSpace(error))
            children.Add(Body(error!) with { Color = Tok.SystemFillCritical, Wrap = TextWrap.Wrap, MaxLines = 3 });
        children.Add(body);

        return new BoxEl
        {
            Direction = 1, Width = 360f, Gap = WaveeSpace.S,
            Padding = new Edges4(WaveeSpace.M, WaveeSpace.M, WaveeSpace.M, WaveeSpace.M),
            Children = children.ToArray(),
        };
    }

    static Element LocationRow(ConcertPlace place, Action onClick)
    {
        string detail = !string.IsNullOrWhiteSpace(place.Region) && !string.IsNullOrWhiteSpace(place.Country)
            ? place.Region + " - " + place.Country
            : place.Region ?? place.Country ?? "";
        var text = detail.Length > 0
            ? new Element[] { WaveeType.TrackTitle(place.Name), WaveeType.TrackMeta(detail) }
            : new Element[] { WaveeType.TrackTitle(place.Name) };
        return new BoxEl
        {
            Key = place.Id, Direction = 0, MinHeight = 48f, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M,
            Padding = new Edges4(WaveeSpace.S, WaveeSpace.XS, WaveeSpace.S, WaveeSpace.XS),
            Corners = CornerRadius4.All(WaveeRadius.Control),
            HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick,
            Children =
            [
                Icon(Mdl.MapPin, 18f, Tok.TextSecondary),
                new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = 1f, Children = text },
            ],
        };
    }
}
