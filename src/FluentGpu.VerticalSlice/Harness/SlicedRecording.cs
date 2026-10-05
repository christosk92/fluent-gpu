using FluentGpu.Foundation;
using FluentGpu.Render;
using FluentGpu.Render.Tiles;
using FluentGpu.Rhi;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;

namespace FluentGpu.VerticalSlice.Harness;

/// <summary>
/// A standalone scene recorded the way the HOST records it — into retained slice arenas (a pose is a composite parameter,
/// a clean slice is kept whole) — and composited by the headless model: <see cref="SliceRecorder.BuildComposite"/> over
/// its own <see cref="SliceTable"/>, <see cref="HeadlessGpuDevice.SubmitComposite"/>, then the modelled frame's single
/// painter-ordered stream (<see cref="HeadlessGpuDevice.LastComposedStream"/>) copied into the caller's DrawList, so a
/// gate that decodes bytes reads the COMPOSITED result. One instance per scene (the arenas pair with one span table).
/// </summary>
sealed class SlicedRecording
{
    public readonly SliceRecorder Slices = new();
    public readonly SliceTable Tiles = new(256, 64, 64);
    public readonly HeadlessGpuDevice Device = new();
    // The composite route draws into the device's PRIMARY swapchain (the first one created): this recording owns a device of its
    // own, so it creates that one target up front and presents every composite against it.
    readonly ISwapchain _target;

    public SlicedRecording() => _target = Device.CreateSwapchain(new SwapchainDesc(default, new Size2(1920f, 1080f)));

    public SceneRecordStats Record(SceneStore scene, DrawList dl, SpanTable? spans = null, float width = 1920f, float height = 1080f)
    {
        var stats = SceneRecorder.Record(scene, dl, spans: spans, slices: Slices);
        var snapshot = scene.Recording.InlineSnapshot;
        if (snapshot is null) return stats;
        var info = new FrameInfo(new Size2(width, height), 1f, default, RepaintDamage: stats.RepaintDamage);
        var frame = Slices.BuildComposite(Tiles, snapshot, in info, 0, stats.RepaintDamage, withStreams: true);
        Device.SubmitComposite(in frame, _target);
        Slices.EndComposite(Tiles, frame.RasterDone);
        dl.Reset();
        var composed = Device.LastComposedStream;
        dl.AppendRaw(composed.Bytes, composed.SortKeys, composed.CommandCount, composed.OpcodeStats);
        return stats;
    }
}
