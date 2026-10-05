using FluentGpu.Controls;
using FluentGpu.Controls.Media;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Media;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

// The page the `--video-e2e` probe (Probes/VideoE2EProbe.cs) drives. HIDDEN: it resolves by key for the probe's
// GalleryShell.StressNavigate hook but never shows in the nav tree or search. One surface carries every scenario's
// main-window workload: a 100k-row bound-recycler list the probe scrolls every frame, a popup anchor the probe opens
// and closes (S3), and a video stage whose size/position the probe animates (S5). All probe-owned state is static so the
// probe (which owns the players and the frame loop) can write it between frames.

/// <summary>Probe-owned state shared with the hidden page and the pop-out child content.</summary>
static class VideoE2EState
{
    /// <summary>The popup the page's anchor opens (S3). The probe writes it; the page never does.</summary>
    public static readonly Signal<bool> PopupOpen = new(false);
    /// <summary>Whether the main-window video stage is mounted (S5).</summary>
    public static readonly Signal<bool> StageOn = new(false);
    /// <summary>Stage size / x offset, DIP (S5 animates these every frame).</summary>
    public static readonly Signal<float> StageW = new(480f), StageH = new(270f), StageX = new(0f);
    /// <summary>Bumped by the probe every UI turn while the pop-out is shown: the child repaints every frame, like a pop-out
    /// with a ticking seek bar (the F090 shape; the MF picture itself is composited outside the swapchain, so without a
    /// ticking element the child presents almost nothing).</summary>
    public static readonly Signal<int> ChildTick = new(0);
    /// <summary>The player the main-window stage presents.</summary>
    public static IMediaPlayer? MainPlayer;
    /// <summary>The player the detached pop-out presents.</summary>
    public static IMediaPlayer? PopPlayer;
}

[GalleryPage("video-e2e", "Video end-to-end probe", "Media", Hidden = true, ShotMode = ShotMode.Skip)]
sealed class VideoE2EPage : Component
{
    static readonly ColorF RowEven = ColorF.FromRgba(255, 255, 255, 8);
    static readonly ColorF RowOdd = ColorF.FromRgba(255, 255, 255, 18);

    static Element Row(IReadSignal<int> idx) => new BoxEl
    {
        Direction = 0, Height = 48f, Gap = 12f, AlignItems = FlexAlign.Center, Padding = new Edges4(16, 0, 16, 0),
        Fill = Prop.Of(() => (idx.Value % 2 == 0) ? RowEven : RowOdd),
        Children =
        [
            new BoxEl { Width = 64f, Children = [new TextEl("") { Size = 13f, Text = Prop.Of(() => $"{idx.Value + 1}") }] },
            new TextEl("") { Size = 14f, Color = Theme.WindowText, Text = Prop.Of(() => $"Item {idx.Value}") },
        ],
    };

    public override Element Render() => new BoxEl
    {
        Direction = 1, Grow = 1f, Gap = 8f, Padding = Edges4.All(16f),
        Children =
        [
            new BoxEl
            {
                Direction = 0, Gap = 12f, AlignItems = FlexAlign.Center,
                Children =
                [
                    Popup.Create(Button.Standard("Popup anchor", () => { }), () => new BoxEl
                    {
                        Padding = Edges4.All(16f), Width = 220f, Height = 90f,
                        Children = [new TextEl("Video e2e popup") { Size = 14f }],
                    }, VideoE2EState.PopupOpen),
                    new TextEl("Video end-to-end probe surface (hidden page)") { Size = 12f },
                ],
            },
            Embed.Comp(() => new VideoE2EStage()),
            Virtual.ListBound(100000, 48f, Row) with { Grow = 1f },
        ],
    };
}

/// <summary>The main-window video stage: zero height until the probe turns it on, then a MediaPlayerElement whose size
/// and offset follow the probe's signals (the S5 placement-churn workload).</summary>
sealed class VideoE2EStage : Component
{
    public override Element Render()
    {
        var player = VideoE2EState.MainPlayer;
        if (!VideoE2EState.StageOn.Value || player is null) return new BoxEl { Height = 0f };
        return new BoxEl
        {
            Direction = 0, Height = 340f, Padding = new Edges4(VideoE2EState.StageX.Value + 100f, 0, 0, 0),
            Children =
            [
                new BoxEl
                {
                    Width = VideoE2EState.StageW.Value, Height = VideoE2EState.StageH.Value,
                    Children = [Embed.Comp(() => new MediaPlayerElement { Player = player, AreTransportControlsEnabled = false })],
                },
            ],
        };
    }
}

/// <summary>The pop-out child's content: one MediaPlayerElement over the probe's pop-out player filling the window, plus a
/// thin ticking bar (a stand-in for the seek bar) that makes the child repaint every UI turn.</summary>
sealed class VideoE2EChild : Component
{
    public override Element Render() => new BoxEl
    {
        Direction = 1, Grow = 1f,
        Children =
        [
            new BoxEl
            {
                Direction = 1, Grow = 1f,
                Children = [Embed.Comp(() => new MediaPlayerElement { Player = VideoE2EState.PopPlayer!, AreTransportControlsEnabled = false })],
            },
            Embed.Comp(() => new VideoE2ETickBar()),
        ],
    };
}

sealed class VideoE2ETickBar : Component
{
    public override Element Render() => new BoxEl
    {
        Height = 4f, Width = 40f + VideoE2EState.ChildTick.Value % 200, Fill = ColorF.FromRgba(90, 160, 255, 255),
    };
}
