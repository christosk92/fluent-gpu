using System;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

// ===== AsyncSkeletonPage — native skeleton/shimmer-while-loading, end to end =====
// The Skel.Region kit: the author defines ONE UI (the real album page); the framework DERIVES the shimmer from it, shows
// partial-known content immediately (cover + title), shimmers the still-loading region (the track list), and swaps to
// real with the blur reveal we built — then streams per-row durations in via incremental per-field Loadables. No second
// hand-authored skeleton tree, no two UIs to keep in sync.
sealed class AsyncSkeletonPage : Component
{
    public override Element Render() => GalleryPage.Shell("Async & skeletons",
        "Loading UI from ONE source. You author the real UI; wrap the async region in Skel.Region(loadable, …) and the " +
        "framework derives the shimmer from that same UI, keeps the parts you already have (the album cover + title) " +
        "real, shimmers the pending region (the tracks), and swaps to real content with the blur reveal on load. " +
        "Per-field Loadables let individual cells (track durations) stream in afterwards, shimmering just that leaf.",
        ControlExample.Build("Album page — ONE UI: known header, async track list, streaming durations",
            Embed.Comp(() => new AlbumLoadingDemo()),
            description: "The cover + title render REAL on frame 1 (they're known on click — outside any region). The track " +
                "list is a Skel.Region: it shimmers " + "derived row placeholders, then blur-reveals the real rows on load. " +
                "Each row's duration is its OWN Loadable (.Pending) — the row is real while the duration cell shimmers, then " +
                "the duration streams in. Hit Reload to replay.",
            code: """
            var tracks = UseAsyncResource(LoadTracks, seed: Array.Empty<Track>());   // Loadable<Track[]>
            return VStack(
              Header(Seed.Cover, Seed.Title),                                        // REAL frame 1 (known)
              Skel.Region(tracks, rowTemplate: AlbumRow, count: Seed.TrackCount,
                reveal: SkelReveal.StaggerRows,
                content: ts => Flow.For(() => ts.Length, i => AlbumRow(ts[i]), keyOf: i => ts[i].Id)));
            // a row's duration cell:  new TextEl("") { Text = t.Dur.Bind() }.Pending(t.Dur)
            """),
        ControlExample.Build("Load failure → onFailed branch",
            Embed.Comp(() => new SkeletonFailureDemo()),
            description: "When the loader throws, SetFailed routes through the same State signal and the region shows the " +
                "onFailed UI instead of shimmering forever. Retry re-arms the load.",
            code: """
            Skel.Region(items, rowTemplate: Row, count: 4, content: ...,
                onFailed: () => ErrorCard("Couldn't load — Retry"));
            """));
}

/// <summary>A track whose duration arrives AFTER the row (the incremental-field case): the row is real as soon as the
/// list loads; <see cref="Dur"/> is its own Loadable that shimmers in place until the metadata pass fills it.</summary>
sealed class AlbumTrack
{
    public int Number;
    public string Title = "";
    public Loadable<string> Dur = Loadable<string>.Pending("");
}

sealed class AlbumLoadingDemo : Component
{
    static readonly string[] Titles =
    [
        "One More Time", "Aerodynamic", "Digital Love", "Harder, Better, Faster, Stronger",
        "Crescendolls", "Nightvision", "Superheroes", "High Life", "Something About Us",
    ];
    static readonly string[] Durations = ["5:20", "3:27", "4:58", "3:44", "3:31", "1:44", "3:57", "3:22", "3:51"];

    public override Element Render()
    {
        var (reload, setReload) = UseState(0);
        var tracks = UseLoadable<AlbumTrack[]>(Loadable<AlbumTrack[]>.Pending(Array.Empty<AlbumTrack>()));
        var gen = UseRef(0);
        var post = UsePost();

        // (Re)load on mount and whenever Reload is hit. A generation guard drops a stale in-flight load's results so an
        // overlapping reload never applies the wrong data (UseEffect has no cleanup hook here, so we guard by generation).
        UseEffect(() =>
        {
            int my = ++gen.Value;
            tracks.SetPending();
            _ = Run();

            async Task Run()
            {
                try
                {
                    await Task.Delay(1400).ConfigureAwait(false);                 // simulate the track-list API
                    post(() =>
                    {
                        if (gen.Value != my) return;                              // a newer reload superseded this one
                        var arr = new AlbumTrack[Titles.Length];
                        for (int i = 0; i < arr.Length; i++) arr[i] = new AlbumTrack { Number = i + 1, Title = Titles[i] };
                        tracks.SetReady(arr);

                        _ = Durations1();                                         // the metadata pass: durations stream in
                        async Task Durations1()
                        {
                            for (int i = 0; i < arr.Length; i++)
                            {
                                await Task.Delay(220).ConfigureAwait(false);
                                int idx = i;
                                post(() => { if (gen.Value == my) arr[idx].Dur.SetReady(Durations[idx]); });
                            }
                        }
                    });
                }
                catch (Exception) { /* demo loader never throws */ }
            }
        }, reload);

        return new BoxEl
        {
            Direction = 1, Gap = 16f, Width = 420f,
            Children =
            [
                // HEADER — REAL on frame 1 (cover + title are "known on click"; nothing here is wrapped in a region).
                new BoxEl
                {
                    Direction = 0, Gap = 16f, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        new BoxEl { Width = 96f, Height = 96f, Corners = Radii.OverlayAll, Fill = Tok.AccentDefault },
                        new BoxEl { Direction = 1, Gap = 4f, Grow = 1f, Children =
                        [
                            new TextEl("Discovery") { Size = 24f, Bold = true, Color = Tok.TextPrimary },
                            new TextEl("Daft Punk · 2001") { Size = 14f, Color = Tok.TextSecondary },
                        ]},
                    ],
                },

                // TRACK LIST — the shimmering region. ONE source: AlbumRow (real + skeleton are the same shape).
                Skel.Region(tracks, AlbumRowSkeleton, count: Titles.Length,
                    reveal: SkelReveal.StaggerRows,
                    content: ts => Flow.For(() => ts.Length, i => AlbumRowReal(ts[i]), keyOf: i => ts[i].Number.ToString())),

                Button.Standard("Reload", () => setReload(reload + 1)),
            ],
        };
    }

    // The ONE row shape. Real rows bind the title + the per-row duration Loadable (.Pending shimmers the cell until it
    // streams in). The skeleton template builds the SAME shape with empty fields so the deriver matches it 1:1.
    static Element AlbumRowReal(AlbumTrack t) => Row(
        t.Number.ToString(),
        new TextEl(t.Title) { Size = 14f, Grow = 1f, Color = Tok.TextPrimary },
        new TextEl("") { Text = t.Dur.Bind(), Size = 13f, Width = 44f, Color = Tok.TextTertiary }.Pending(t.Dur));

    static Element AlbumRowSkeleton(AlbumTrack? t) => Row(
        "",
        new TextEl("") { Size = 14f, Grow = 1f },
        new TextEl("") { Size = 13f, Width = 44f });

    static Element Row(string num, Element title, Element duration) => new BoxEl
    {
        Direction = 0, Gap = 12f, Padding = new Edges4(0, 7f, 0, 7f), AlignItems = FlexAlign.Center,
        Children = [new TextEl(num) { Size = 13f, Width = 28f, Color = Tok.TextTertiary }, title, duration],
    };
}

sealed class SkeletonFailureDemo : Component
{
    public override Element Render()
    {
        var (retry, setRetry) = UseState(0);
        var items = UseLoadable<string[]>(Loadable<string[]>.Pending(Array.Empty<string>()));
        var gen = UseRef(0);
        var post = UsePost();

        UseEffect(() =>
        {
            int my = ++gen.Value;
            items.SetPending();
            _ = Run();
            async Task Run()
            {
                try { await Task.Delay(1200).ConfigureAwait(false); }
                catch (Exception) { }
                post(() => { if (gen.Value == my) items.SetFailed(new InvalidOperationException("network unreachable")); });
            }
        }, retry);

        return new BoxEl
        {
            Direction = 1, Gap = 12f, Width = 360f,
            Children =
            [
                Skel.Region(items, _ => RowBar(), count: 4,
                    content: xs => Flow.For(() => xs.Length, i => new TextEl(xs[i]) { Size = 14f }, keyOf: i => i.ToString()),
                    onFailed: () => new BoxEl
                    {
                        Direction = 1, Gap = 8f, Padding = Edges4.All(16),
                        Corners = Radii.OverlayAll, Fill = Tok.SystemFillCautionBackground,
                        Children =
                        [
                            new TextEl("Couldn't load this section.") { Size = 14f, Bold = true, Color = Tok.TextPrimary },
                            new TextEl("network unreachable") { Size = 12.5f, Color = Tok.TextSecondary },
                        ],
                    }),
                Button.Standard("Retry", () => setRetry(retry + 1)),
            ],
        };
    }

    static Element RowBar() => new BoxEl { Direction = 0, Padding = new Edges4(0, 6f, 0, 6f), Children = [new TextEl("Loading row") { Size = 14f, Grow = 1f }] };
}
