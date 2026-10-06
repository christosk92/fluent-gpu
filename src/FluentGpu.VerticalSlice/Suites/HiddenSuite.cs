using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;

namespace FluentGpu.VerticalSlice.Suites;

/// <summary>
/// The hidden-window Shallow stage (docs: a minimized / tray-hidden window releases what no visible frame needs): after the park
/// delay the host holds no retained tile and no image texture nothing pins or holds, keeps the pinned cover, and the first frame
/// back rebuilds every tile with none exposed without a surface. A hide shorter than the delay releases nothing.
/// </summary>
static class HiddenSuite
{
    sealed class PinnedArt : Component
    {
        public override Element Render() => new ImageEl { Source = "art://pinned", Width = 40f, Height = 40f };
    }

    public static void Run(StringTable strings)
    {
        HiddenMemoryBudget.Reset();
        try { ShallowReleasesEverythingUnneeded(strings); }
        finally { HiddenMemoryBudget.Reset(); }
    }

    // ── gate.hidden.shallow-releases-everything-unneeded ─────────────────────────────────────────────────────────
    static void ShallowReleasesEverythingUnneeded(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var device = new HeadlessGpuDevice();
        var window = new HeadlessWindow(new WindowDesc("hidden", new Size2(320, 240), 1f));
        window.Show();
        using var host = new AppHost(app, window, device, new HeadlessFontSystem(strings), strings, new PinnedArt());
        for (int i = 0; i < 8; i++) host.RunFrame();
        var loose = host.Images.Request("art://loose", 64, 64);
        for (int i = 0; i < 3; i++) host.RunFrame();
        int tiles = host.UiSliceTable.ResidentTiles;
        int resident = device.ResidentImages.Count;
        bool looseResident = device.ResidentImages.ContainsKey(loose.Id);

        window.State = WindowState.Minimized;
        host.RunFrame();
        host.AdvanceFrameClockForTest(HiddenMemoryBudget.DefaultShallowDelayMs - 100);
        host.RunFrame();
        Check("gate.hidden.shallow-releases-everything-unneeded a hide shorter than the delay releases nothing",
            host.HiddenStageForTest == HiddenStage.Visible && device.HiddenReleases.Count == 0 && host.UiSliceTable.ResidentTiles == tiles);

        host.AdvanceFrameClockForTest(200);
        host.RunFrame();
        Check("gate.hidden.shallow-releases-everything-unneeded the stage is reached after the delay and the device was told twice (before and after the evict drain)",
            host.HiddenStageForTest == HiddenStage.Shallow && device.HiddenReleases.Count == 2);
        Check("gate.hidden.shallow-releases-everything-unneeded no retained tile stays resident and every tile texture was handed back",
            tiles > 0 && host.UiSliceTable.ResidentTiles == 0 && device.TrimmedTileSlots == tiles,
            $"tiles={tiles} resident={host.UiSliceTable.ResidentTiles} trimmed={device.TrimmedTileSlots}");
        Check("gate.hidden.shallow-releases-everything-unneeded the unpinned image texture is released and the pinned cover is kept",
            looseResident && !device.ResidentImages.ContainsKey(loose.Id) && device.ResidentImages.Count == resident - 1,
            $"before={resident} after={device.ResidentImages.Count}");

        window.State = WindowState.Normal;
        host.RunFrame();
        Check("gate.hidden.shallow-releases-everything-unneeded the restore frame lifts the stage, rebuilds every tile and exposes none without a surface",
            host.HiddenStageForTest == HiddenStage.Visible && device.HiddenReleases[^1] == HiddenStage.Visible
            && host.UiSliceTable.ResidentTiles == tiles && host.UiSliceTable.CountExposedMissing() == 0,
            $"tiles={host.UiSliceTable.ResidentTiles}/{tiles} exposedMissing={host.UiSliceTable.CountExposedMissing()}");
    }
}
