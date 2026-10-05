using System;
using System.IO;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Hosting.Threading;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi;
using FluentGpu.Rhi.Headless;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// The frame ledger (Hosting/FrameLedger.cs): the single-producer ring (wrap, windows), the binary dump round trip and the CSV
/// export, the zero-allocation record paths, and the host wiring — every <c>RunFrame</c> of the owning host yields one UI record
/// with ordered stamps (early-outs included, tagged with the gate that stopped them), and a headless host with the force-sync
/// render loop yields render turns that join the UI stream on the publish sequence. Serial: the ledger is process-static and
/// follows the first host that runs a frame while it is on.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class FrameLedgerTests
{
    private sealed class PaintedRoot : Component
    {
        public readonly Signal<float> W = new(200f);
        public override Element Render() => new BoxEl { Width = W.Value, Height = 100f, Fill = ColorF.FromRgba(20, 60, 120) };
    }

    private sealed class Rig : IDisposable
    {
        public readonly HeadlessPlatformApp App = new();
        public readonly StringTable Strings = new();
        public readonly HeadlessGpuDevice Device = new();
        public readonly HeadlessWindow Window;
        public readonly PaintedRoot Root = new();
        public readonly AppHost Host;

        public Rig(bool renderThread = false)
        {
            ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
            Window = new HeadlessWindow(new WindowDesc("main", new Size2(320, 240), 1f));
            Window.Show();
            Host = new AppHost(App, Window, Device, new HeadlessFontSystem(Strings), Strings, Root);
            if (renderThread) Host.InstallRenderThreadForTest();
        }

        public void Dispose()
        {
            FrameLedger.Disable();
            Host.Dispose();
            App.Dispose();
        }
    }

    [Fact]
    public void Ring_KeepsTheNewestCapacityRecords_AndCopiesAWindowFromAMark()
    {
        var ring = new LedgerRing<LedgerGpuFrame>(8);
        for (int i = 0; i < 5; i++) ring.Push(new LedgerGpuFrame { Seq = (ulong)i });
        var window = ring.CopySince(3);
        Assert.Equal(new ulong[] { 3, 4 }, Array.ConvertAll(window, r => r.Seq));
        for (int i = 5; i < 20; i++) ring.Push(new LedgerGpuFrame { Seq = (ulong)i });
        Assert.Equal(20, ring.Count);
        var all = ring.CopySince(0);
        Assert.Equal(8, all.Length);                     // wrapped: only the newest capacity survive
        Assert.Equal(12UL, all[0].Seq);
        Assert.Equal(19UL, all[^1].Seq);
        Assert.Empty(ring.CopySince(20));
    }

    [Fact]
    public void Dump_RoundTripsEveryStream_AndTheCsvHasOneRowPerRecord()
    {
        var snap = new LedgerSnapshot
        {
            QpcFrequency = 10_000_000, CyclesPerMs = 3_000_000, Processors = 12, OriginQpc = 1_000_000, EndQpc = 1_500_000,
            Ui = [new LedgerUiFrame { Seq = 0, StartQpc = 1_000_100, EndQpc = 1_010_100, UiCycles = 3_000_000, PublishSeq = 7, Exit = (byte)LedgerFrameExit.Painted },
                  new LedgerUiFrame { Seq = 1, StartQpc = 1_100_000, EndQpc = 1_100_500, Exit = (byte)LedgerFrameExit.Idle }],
            Render = [new LedgerRenderTurn { Seq = 0, StartQpc = 1_012_000, EndQpc = 1_020_000, PublishSeq = 7, SubmitSeq = 3, Kind = (byte)LedgerTurnKind.Fresh, PresentMs = 1.5f }],
            Gpu = [new LedgerGpuFrame { Seq = 0, SampleSeq = 1, SubmitSeq = 3, GpuMs = 2.25f, TileRasterMs = 1f, CompositeMs = 1.25f }],
            Memory = [new LedgerMemorySample { Seq = 0, Qpc = 1_000_000, WorkingSetBytes = 123 << 20, VramLocalBytes = 64 << 20 }],
            Audio = [new LedgerAudioSample { Seq = 0, Qpc = 1_000_000, DeviceWritesTotal = 10, PaddingMinFrames = 480, Rate = 48000 }],
        };
        var ms = new MemoryStream();
        FrameLedgerFile.Write(ms, snap);
        ms.Position = 0;
        var back = FrameLedgerFile.Read(ms);
        Assert.Equal(snap.QpcFrequency, back.QpcFrequency);
        Assert.Equal(snap.CyclesPerMs, back.CyclesPerMs);
        Assert.Equal(snap.OriginQpc, back.OriginQpc);
        Assert.Equal(snap.EndQpc, back.EndQpc);
        Assert.Equal(snap.Ui, back.Ui);
        Assert.Equal(snap.Render, back.Render);
        Assert.Equal(snap.Gpu, back.Gpu);
        Assert.Equal(snap.Memory, back.Memory);
        Assert.Equal(snap.Audio, back.Audio);

        string ui = FrameLedgerCsv.Ui(snap);
        string[] lines = ui.TrimEnd().Split('\n');
        Assert.Equal(3, lines.Length);                                   // header + 2 frames
        Assert.StartsWith("seq,startMs,", lines[0]);
        Assert.Contains(",Idle,", lines[2]);
        Assert.Equal(lines[0].Split(',').Length, lines[1].Split(',').Length);
        string[] row = lines[1].Split(',');
        int frameMs = Array.IndexOf(lines[0].Split(','), "frameMs"), cpu = Array.IndexOf(lines[0].Split(','), "uiCpuMs");
        Assert.Equal("1", row[frameMs]);                                 // 10_000 ticks at 10 MHz
        Assert.Equal("1", row[cpu]);                                     // 3e6 cycles at 3e6/ms
        Assert.Equal(2, FrameLedgerCsv.Gpu(snap).TrimEnd().Split('\n').Length);
        Assert.Contains("10,0,0,0,480,10,", FrameLedgerCsv.Audio(snap));

        // A file with a different record layout is refused, not misread.
        var bad = ms.ToArray();
        bad[8 + 4 + 8 + 8 + 4 + 8 + 8] ^= 0x7F;                          // the UI record size
        Assert.Throws<InvalidDataException>(() => FrameLedgerFile.Read(new MemoryStream(bad)));
    }

    [Fact]
    public void RecordPaths_AllocateNothing()
    {
        FrameLedger.Enable(1024);
        try
        {
            var ui = new LedgerUiFrame { StartQpc = 1 };
            var rt = new LedgerRenderTurn { StartQpc = 1 };
            var gpu = new LedgerGpuFrame { GpuMs = 1f };
            for (int i = 0; i < 64; i++) { FrameLedger.RecordUi(ref ui); FrameLedger.RecordRender(ref rt); FrameLedger.RecordGpu(ref gpu); }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 5000; i++)
            {
                FrameLedger.RecordUi(ref ui);
                FrameLedger.RecordRender(ref rt);
                FrameLedger.RecordGpu(ref gpu);
                _ = FrameLedger.ReadProcessCycles();
            }
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        finally { FrameLedger.Disable(); }
    }

    [Fact]
    public void ALedgeredFrame_AllocatesNoMoreThanAnUnledgeredOne()
    {
        using var rig = new Rig();
        for (int i = 0; i < 30; i++) { rig.Root.W.Value = 200f + (i & 1); rig.Host.RunFrame(); }   // warm every path
        long Measure()
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 200; i++)
            {
                rig.Host.RunFrame();                          // idle frames: the idle gate
                rig.Host.NoteLoopWait(1, 2, 16);
            }
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
        long off = Measure();
        FrameLedger.Enable(1024);
        FrameLedger.Attach(rig.Host);
        Measure();                                            // first ledgered pass (claims, settles)
        long on = Measure();
        Assert.True(on <= off, $"ledgered idle frames allocated {on} B vs {off} B unledgered");
        Assert.True(FrameLedger.Snapshot().Ui.Length >= 400);
    }

    [Fact]
    public void EveryRunFrame_YieldsOneRecord_WithOrderedStamps_AndItsExit()
    {
        using var rig = new Rig();
        rig.Host.RunFrame();
        FrameLedger.Enable(1024);
        var mark = FrameLedger.Mark();
        const int Frames = 40;
        for (int i = 0; i < Frames; i++)
        {
            if (i % 2 == 0) rig.Root.W.Value = 200f + i;     // even frames paint, odd ones find nothing to do
            rig.Host.RunFrame();
            rig.Host.NoteLoopWait(System.Diagnostics.Stopwatch.GetTimestamp(), System.Diagnostics.Stopwatch.GetTimestamp() + 1, 8);
        }
        var snap = FrameLedger.Snapshot(mark);
        Assert.Same(rig.Host, FrameLedger.Owner);
        Assert.Equal(Frames, snap.Ui.Length);
        long prevEnd = 0;
        int painted = 0, idle = 0;
        for (int i = 0; i < snap.Ui.Length; i++)
        {
            ref readonly var r = ref snap.Ui[i];
            Assert.Equal(snap.Ui[0].Seq + (ulong)i, r.Seq);
            Assert.True(r.StartQpc >= prevEnd, $"frame {i} starts before the previous one ended");
            Assert.True(r.PumpQpc >= r.StartQpc && r.EndQpc >= r.PumpQpc, $"frame {i}: start <= pump <= end");
            if (i > 0) Assert.NotEqual(0, r.WaitStartQpc);    // the wait noted after the previous frame rides this one
            if (r.Exit == (byte)LedgerFrameExit.Painted && r.PaintQpc != 0)
            {
                painted++;
                Assert.True(r.PaintQpc >= r.PumpQpc && r.FlushQpc >= r.PaintQpc && r.LayoutQpc >= r.FlushQpc && r.AnimQpc >= r.LayoutQpc
                    && r.RecordQpc >= r.AnimQpc && r.SubmitQpc >= r.RecordQpc && r.EndQpc >= r.SubmitQpc, $"frame {i}: paint phases out of order");
                Assert.True((r.Flags & (ushort)LedgerUiFlags.Painted) != 0);
            }
            if (r.Exit == (byte)LedgerFrameExit.Idle) { idle++; Assert.Equal(0, r.PaintQpc); }
            prevEnd = r.EndQpc;
        }
        Assert.True(painted >= Frames / 2 - 1, $"painted={painted}");
        Assert.True(idle >= Frames / 2 - 1, $"idle={idle}");
        Assert.True(FrameLedger.Snapshot().Memory.Length >= 1, "Enable takes a memory sample at once");
    }

    [Fact]
    public void RenderTurns_JoinTheUiStream_OnThePublishSequence()
    {
        using var rig = new Rig(renderThread: true);
        rig.Host.RunFrame();
        FrameLedger.Enable(1024);
        FrameLedger.Attach(rig.Host);
        var mark = FrameLedger.Mark();
        for (int i = 0; i < 12; i++) { rig.Root.W.Value = 150f + i; rig.Host.RunFrame(); }
        var snap = FrameLedger.Snapshot(mark);
        Assert.NotEmpty(snap.Render);
        int joined = 0;
        foreach (var t in snap.Render)
        {
            Assert.True(t.EndQpc >= t.StartQpc && t.StartQpc >= t.WaitStartQpc);
            if (t.Kind != (byte)LedgerTurnKind.Fresh) continue;
            Assert.NotEqual(0UL, t.PublishSeq);
            Assert.True(t.DoneQpc >= t.SlotOpenQpc && t.SlotOpenQpc >= t.StartQpc);
            if (Array.Exists(snap.Ui, u => u.PublishSeq == t.PublishSeq)) joined++;
        }
        Assert.True(joined >= 10, $"fresh turns joined to a UI frame: {joined}");
    }

    [Fact]
    public void Disabled_RecordsNothing()
    {
        using var rig = new Rig();
        FrameLedger.Disable();
        var mark = FrameLedger.Mark();
        for (int i = 0; i < 5; i++) { rig.Root.W.Value = 120f + i; rig.Host.RunFrame(); }
        Assert.Empty(FrameLedger.Snapshot(mark).Ui);
    }
}
