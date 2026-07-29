using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The expanded track drawer: every version of a track, plus the audio format each one plays in.
///
/// Grouping follows the WIRE, not a guess — kind 99 associations are music videos, kind 98 are alternate audio — and
/// the thumbnail aspect carries that distinction (16:9 vs square) exactly as the payloads do, so no icon is needed.
///
/// The track itself is the FIRST entry. That is what gives its own format a home: without it, "play this track as
/// FLAC" would have nowhere to live, and the drawer would only ever be about other recordings.</summary>
sealed class TrackVersionsPanel : Component
{
    /// <param name="Indent">Where the parent row's TITLE starts, so the drawer lines up under the text rather than
    /// under the artwork. Derived per tier by the caller — the leading cluster is 36/76/112 DIP depending on which
    /// columns survive.</param>
    /// <param name="OnPlay">Takes the VERSION, not a uri: a music video and its song are the same playable, so a uri
    /// alone cannot say which of them the user asked for — which is precisely why this drawer's video row used to
    /// start the audio track. The host maps the version's kind to a <c>MediaForm</c>.</param>
    internal sealed record Model(
        Track Track,
        Action<TrackVersion> OnPlay,
        Action<string, string?> OnOpen,
        float Indent);
    internal static readonly Context<Model?> Props = new(null);

    const float VideoThumbW = 76f, VideoThumbH = 43f, AudioThumb = 43f;

    /// <summary>The connector gutter: a vertical rail descending from the row, with a stub into each version.</summary>
    const float GutterW = 20f;
    const float RailX = 7f;
    const float StubW = 9f;
    /// <summary>Where the rail sits inside the drawer's own left padding. The caller subtracts this from the artwork
    /// centre so the RAIL lands on the art, rather than the gutter's left edge landing there.</summary>
    internal const float RailOffset = RailX;
    /// <summary>Version row height — thumb + its padding + the hairline the rail's stub must meet at mid-height.</summary>
    const float RowH = AudioThumb + 2f * Spacing.XS;

    readonly Signal<TrackExpansion?> _data = new(null);
    readonly Signal<bool> _loading = new(true);

    public override Element Render()
    {
        var model = UseContext(Props);
        var svc = UseContext(Services.Slot);
        var bridge = UseContext(PlaybackBridge.Slot);
        var post = UsePost();
        // Fluent now-playing cues: subscribe so accent title + art overlay refresh when identity moves.
        string? nowUri = bridge?.Identity.Value.Track?.Uri;
        _ = bridge?.IsPlaying.Value;
        if (model is null) return new BoxEl();

        // Fetch on MOUNT — the drawer only mounts when the user expands the row, so this is the "on expand" fetch.
        UseEffect(() =>
        {
            if (svc is null) return (Action?)null;
            var cts = new CancellationTokenSource();
            _ = LoadAsync(svc, model.Track.Uri, post, cts.Token);
            return (Action?)(() => { try { cts.Cancel(); cts.Dispose(); } catch { } });
        }, DepKey.From(model.Track.Uri.GetHashCode()));

        var data = _data.Value;
        if (_loading.Value && data is null) return Loading(model.Indent);

        // Flat, in order: the track itself, then its video, then its alternate audio. NO group headings — each kind
        // yields AT MOST ONE row (the wire models `association` as singular), so the old three ALL-CAPS labels over
        // three identical cards were captions on lists of one. The thumbnail aspect already says which is which.
        var versions = new List<TrackVersion>(3) { SelfVersion(model.Track) };
        if (data is not null)
        {
            foreach (var v in data.Versions) if (v.Kind == TrackVersionKind.Video) versions.Add(v);
            foreach (var v in data.Versions) if (v.Kind == TrackVersionKind.Audio) versions.Add(v);
        }

        var rows = new List<Element>(versions.Count);
        for (int i = 0; i < versions.Count; i++)
            rows.Add(ConnectedRow(versions[i], model, svc, isSelf: i == 0, isLast: i == versions.Count - 1,
                                  waveform: i == 0 ? data?.Waveform : null,
                                  isNow: nowUri is { Length: > 0 } && versions[i].Uri == nowUri));

        return new BoxEl
        {
            Direction = 1, MinWidth = 0f,
            // Tight by design: the drawer is a continuation of the row above it, not a card of its own. Top padding is
            // 0 (the row's own bottom edge is the separation) and the bottom is one small step, so an expanded row
            // reads as one taller row rather than a panel wedged into the list.
            Padding = new Edges4(model.Indent, 0f, TrackRow.PadX, Spacing.S),
            Children = rows.ToArray(),
        };
    }

    async System.Threading.Tasks.Task LoadAsync(Services svc, string uri, Action<Action> post, CancellationToken ct)
    {
        TrackExpansion? result = null;
        try { result = await svc.TrackExpansion.GetAsync(uri, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        catch { result = TrackExpansion.Empty; }
        if (ct.IsCancellationRequested) return;
        post(() => { _data.Value = result; _loading.Value = false; });
    }

    /// <summary>One version, hung off the connector rail.
    ///
    /// The rail is what makes the drawer read as belonging to the row above it instead of as a detached panel: a single
    /// vertical hairline descending from the row's baseline, with a short stub reaching into each entry. The last entry
    /// stops the rail at its own stub (an elbow), so the line terminates on content rather than trailing into space.
    ///
    /// Built from plain boxes because <c>BorderWidth</c> is a single uniform float — there are no per-side borders to
    /// make a left rule out of.</summary>
    static Element ConnectedRow(TrackVersion v, Model model, Services? svc, bool isSelf, bool isLast,
                                TrackWaveform? waveform, bool isNow) => new BoxEl
    {
        Key = "cv:" + v.Uri,
        Direction = 0, AlignItems = FlexAlign.Stretch, MinWidth = 0f,
        Children =
        [
            new BoxEl
            {
                Width = GutterW, Shrink = 0f, ZStack = true, HitTestPassThrough = true,
                Children =
                [
                    // The descending rail. On the last entry it stops at the stub; otherwise it runs the full height
                    // and meets the next row's rail with no seam.
                    new BoxEl
                    {
                        Width = 1f, Shrink = 0f,
                        Height = isLast ? RowH / 2f : float.NaN,
                        AlignSelf = FlexAlign.Start,
                        Margin = new Edges4(RailX, 0f, 0f, 0f),
                        // StrokeDividerDefault, NOT StrokeCardDefault: the card stroke is a black alpha in BOTH themes
                        // (0x19 dark / 0x0F light), so on a dark surface the connector was black-on-black and the whole
                        // spine vanished. The divider token is the theme-flipping one (white 0x15 on dark).
                        Fill = Tok.StrokeDividerDefault,
                    },
                    // The stub into this entry, at its vertical centre.
                    new BoxEl
                    {
                        Width = StubW, Height = 1f, Shrink = 0f,
                        AlignSelf = FlexAlign.Start,
                        Margin = new Edges4(RailX, RowH / 2f, 0f, 0f),
                        // StrokeDividerDefault, NOT StrokeCardDefault: the card stroke is a black alpha in BOTH themes
                        // (0x19 dark / 0x0F light), so on a dark surface the connector was black-on-black and the whole
                        // spine vanished. The divider token is the theme-flipping one (white 0x15 on dark).
                        Fill = Tok.StrokeDividerDefault,
                    },
                ],
            },
            VersionRow(v, model, svc, isSelf, waveform, isNow),
        ],
    };

    // The row's own track, projected as a version so ONE row factory renders every entry.
    static TrackVersion SelfVersion(Track t) => new(
        t.Uri, TrackVersionKind.Original, t.Title, t.Image, t.DurationMs,
        t.TempoBpm, t.MusicalKey, t.CamelotCode, t.CamelotColor);

    static Element VersionRow(TrackVersion v, Model model, Services? svc, bool isSelf, TrackWaveform? waveform, bool isNow)
    {
        bool video = v.Kind == TrackVersionKind.Video;
        float w = video ? VideoThumbW : AudioThumb;
        float h = video ? VideoThumbH : AudioThumb;
        float fab = Math.Clamp(MathF.Min(w, h) * 0.62f, 22f, 28f);

        var meta = new List<Element>(5);
        meta.Add(new TextEl(KindLabel(v, isSelf)) { Size = 12f, Color = Tok.TextTertiary });
        if (v.TempoBpm is { } bpm && bpm > 0d)
        {
            // The Camelot swatch leads the tempo, exactly as the track row's own tempo column does — the colour IS the
            // key's identity (harmonically adjacent keys are adjacent hues), so dropping it here made the drawer's own
            // track read as less informative than the row it expanded from.
            if (v.CamelotColor is { } argb)
                meta.Add(new BoxEl
                {
                    Width = 8f, Height = 8f, Corners = CornerRadius4.All(2f),
                    Fill = WaveePalette.ToColor(argb), AlignSelf = FlexAlign.Center, Shrink = 0f,
                });
            meta.Add(new TextEl(DetailFormat.Bpm(bpm)) { Size = 12f, Color = Tok.TextSecondary });
            if (KeyLabel(v) is { Length: > 0 } key)
                meta.Add(new TextEl(key) { Size = 12f, Color = Tok.TextTertiary });
        }
        if (v.DurationMs > 0)
            meta.Add(new TextEl(DetailFormat.TrackTime(v.DurationMs)) { Size = 12f, Color = Tok.TextTertiary });

        return new BoxEl
        {
            Key = "ver:" + v.Uri,
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f, Grow = 1f,
            Height = RowH,
            Padding = new Edges4(Spacing.XS, 0f, Spacing.XS, 0f),
            Corners = CornerRadius4.All(Radii.Control),
            // Rows, not cards. Three bordered pills stacked inside another pill was the card-soup look; these are
            // borderless and transparent at rest, exactly like the artist page's album drawer, so the rail and the
            // parent row's own plate carry the structure instead of six competing edges.
            Fill = isSelf ? Tok.FillSubtleSecondary : ColorF.Transparent,
            Children =
            [
                Thumb(v, w, h, video, isNow, () => model.OnPlay(v), fab),
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = 2f,
                    Children =
                    [
                        new TextEl(v.Title)
                        {
                            Size = 13.5f, Weight = 540,
                            Color = isNow ? Tok.AccentTextPrimary : Tok.TextPrimary,
                            MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                        },
                        new BoxEl { Direction = 0, Gap = Spacing.S, Children = meta.ToArray() },
                    ],
                },
                Waveform(waveform),
                // The button hands back the version it belongs to, so "play" carries which FORM was clicked.
                Embed.Comp(() => new FormatSplitButton(v.Uri, v.Title, _ => model.OnPlay(v)))
                    with { Key = "fmt:" + v.Uri },
            ],
        }.Interactive(Interaction.Subtle);
    }

    /// <summary>The track's shape, drawn as mirrored bars between the meta and the play control.
    ///
    /// Only the drawer's OWN track carries one: it is the thing the user expanded to look at, and repeating a strip for
    /// every alternate would turn a row of information into a row of decoration. Absent until kind 237 lands (or when
    /// the track has none), and the row simply closes the gap — no reserved empty rail.</summary>
    static Element Waveform(TrackWaveform? w)
    {
        if (w is null || w.IsEmpty) return new BoxEl();

        // One bar per column, at the width the strip can actually show. Bars are mirrored about the centre line, which
        // is what makes a waveform read as a waveform rather than as a bar chart.
        var bars = new Element[Math.Min(w.Peaks.Count, WaveBars)];
        for (int i = 0; i < bars.Length; i++)
        {
            // Sample across the whole set so a shorter strip still shows the WHOLE track, not its first seconds.
            float p = w.Peaks[(int)((long)i * w.Peaks.Count / bars.Length)];
            bars[i] = new BoxEl
            {
                Width = WaveBarW, Shrink = 0f,
                // A floor so silence is still a visible baseline rather than a gap in the strip.
                Height = MathF.Max(2f, p * WaveHeight),
                Corners = CornerRadius4.All(WaveBarW / 2f),
                Fill = Tok.TextTertiary,
                AlignSelf = FlexAlign.Center,
            };
        }

        return new BoxEl
        {
            Direction = 0, Gap = 1f, Height = WaveHeight, Shrink = 0f,
            AlignItems = FlexAlign.Center, HitTestPassThrough = true,
            Children = bars,
        };
    }

    const int WaveBars = 64;
    const float WaveBarW = 2f;
    const float WaveHeight = 24f;

    /// <summary>The entry's thumbnail. A video gets a 16:9 slot with a play badge over it: the cover we resolve for a
    /// music video is the TRACK's square album art (kinds 98/99 ship only DASH file ids, so the art comes from the
    /// TrackV4 payload), which center-crops into the wider slot. Without the badge a cropped square and an uncropped
    /// square are the same picture at two aspect ratios, and nothing says one of them is a video.</summary>
    static Element Thumb(TrackVersion v, float w, float h, bool video, bool isNow, Action onPlay, float fab)
    {
        var art = Surfaces.Artwork(v.Artwork, v.Uri.GetHashCode() & 0x7fffffff, w, h, Radii.Control);
        // Fluent now-playing overlay replaces the static video play badge when this version is current.
        if (isNow)
        {
            return new BoxEl
            {
                Width = w, Height = h, Shrink = 0f, ZStack = true,
                Corners = CornerRadius4.All(Radii.Control), ClipToBounds = true,
                Children =
                [
                    art,
                    Embed.Comp(() => new NowPlayingOverlay(v.Uri, onPlay, fab, cover: true, MathF.Min(w, h), centered: true))
                        .Skeletonized(false),
                ],
            };
        }
        if (!video) return art;

        return new BoxEl
        {
            Width = w, Height = h, Shrink = 0f, ZStack = true,
            Corners = CornerRadius4.All(Radii.Control), ClipToBounds = true,
            Children =
            [
                art,
                new BoxEl
                {
                    Width = w, Height = h, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    HitTestPassThrough = true,
                    Fill = ColorF.FromRgba(0, 0, 0) with { A = 0.28f },
                    Children =
                    [
                        new BoxEl
                        {
                            Width = 22f, Height = 22f, Corners = CornerRadius4.All(11f), Shrink = 0f,
                            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                            Fill = ColorF.FromRgba(255, 255, 255) with { A = 0.92f },
                            Children = [Icon(Icons.Play, 11f, ColorF.FromRgba(17, 17, 17))],
                        },
                    ],
                },
            ],
        };
    }

    /// <summary>What this entry IS, now that the group headings are gone. Carried as the first meta token so the row
    /// says "Music video · 3:44" rather than relying on a caption above a list of one.</summary>
    static string KindLabel(TrackVersion v, bool isSelf) => v.Kind switch
    {
        TrackVersionKind.Video => Loc.Get(Strings.Detail.Versions.MusicVideo),
        TrackVersionKind.Audio => Loc.Get(Strings.Detail.Versions.AlternateAudio),
        _ => Loc.Get(Strings.Detail.Versions.ThisTrack),
    };

    /// <summary>One key notation: Camelot when present, else standard MusicalKey. Mirrors TrackRow.KeyLabel so a row
    /// and its drawer never spell the same fact two ways.</summary>
    static string? KeyLabel(TrackVersion v) =>
        v.CamelotCode is { Length: > 0 } c ? c
        : v.MusicalKey is { Length: > 0 } k ? k
        : null;

    /// <summary>The loading silhouette, derived from the REAL row so the drawer does not resize when data lands. Two
    /// rows is the common case (the track plus one association); a third appearing costs one row of growth, which the
    /// clip's own reflow eases.</summary>
    static Element Loading(float indent) => new BoxEl
    {
        Direction = 1, MinWidth = 0f,
        Padding = new Edges4(indent, 0f, TrackRow.PadX, Spacing.S),
        Children = [ShimmerRow(), ShimmerRow()],
    }.Skeletonized(true);

    static Element ShimmerRow() => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Height = RowH,
        Padding = new Edges4(GutterW + Spacing.XS, 0f, Spacing.XS, 0f),
        Children =
        [
            new BoxEl { Width = AudioThumb, Height = AudioThumb, Shrink = 0f, Corners = CornerRadius4.All(Radii.Control), Fill = Tok.FillCardSecondary },
            new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = 5f,
                Children =
                [
                    new BoxEl { Width = 150f, Height = 11f, Corners = CornerRadius4.All(4f), Fill = Tok.FillCardSecondary },
                    new BoxEl { Width = 96f, Height = 9f, Corners = CornerRadius4.All(4f), Fill = Tok.FillCardSecondary },
                ],
            },
        ],
    };
}
