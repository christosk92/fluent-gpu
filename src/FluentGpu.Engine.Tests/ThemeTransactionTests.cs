using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using FluentGpu.Theming;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// E5 (EP14) - the theme transaction on a headless host: prewarm, freeze, the apply flush under the frozen frame (a root of another
/// type, a definition with other geometry), settle, thaw with the dissolve; the settled scene carries no motion at thaw start;
/// a throwing template or apply rolls back under the same freeze with no visible change; a resize or the watchdog cuts and keeps
/// the change; waiting requests coalesce; a window that shows nothing applies at once; the DWM write lands at the edge the
/// material pair asks for and the live path's writer stays silent. The VerticalSlice <c>theme-transaction</c> suite runs the
/// 100-transaction gate. Serial: it swaps the process-wide token set and constructs hosts.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class ThemeTransactionTests
{
    private static readonly ThemeDefinition DefA = ThemeDefinition.FluentDefault;
    private static readonly ThemeDefinition DefB = ThemeDefinition.FluentDefault with
    {
        Id = "tx-b",
        Spacing = new SpacingScale { S = 14f, M = 22f, L = 30f },
        Radii = new RadiiScale { Control = 0f, Card = 2f, Overlay = 2f },
    };

    /// <summary>The "theme": a page whose component TYPE follows <see cref="Gen"/> (a switch remounts it) and whose geometry reads
    /// the definition, a brush fade, an Enter, and a ticker that keeps running (the lyrics / visualizer stand-in).</summary>
    private sealed class Root : Component
    {
        public readonly Signal<int> Gen = new(0);
        public readonly Signal<bool> Bomb = new(false);
        public int Ticks;

        public override Element Render()
        {
            UseInterval(() => Ticks++, 16f);
            Element page = (Gen.Value & 1) == 0 ? Embed.Comp(static () => new PageA()) : Embed.Comp(static () => new PageB());
            return new BoxEl
            {
                Width = 400f, Height = 300f, Direction = 1,
                Children = Bomb.Value ? [page, Embed.Comp(static () => new Bomb())] : [page],
            };
        }
    }

    private abstract class Page : Component
    {
        protected abstract ColorF Ink { get; }

        public override Element Render() => new BoxEl
        {
            Direction = 1, Padding = Edges4.All(Tok.Definition.Spacing.M), Gap = Tok.Definition.Spacing.S,
            Children =
            [
                new BoxEl { Key = "fill", Width = 120f, Height = 30f, Fill = Ink, BrushTransitionMs = 250f, Corners = CornerRadius4.All(Tok.Definition.Radii.Card) },
                new BoxEl { Key = "enter", Width = 80f, Height = 20f, Fill = Ink, Enter = new EnterExit(Opacity: 0f, Active: true) },
            ],
        };
    }

    private sealed class PageA : Page { protected override ColorF Ink => ColorF.FromRgba(200, 40, 40); }
    private sealed class PageB : Page { protected override ColorF Ink => ColorF.FromRgba(40, 40, 200); }

    private sealed class Bomb : Component
    {
        public override Element Render() => throw new InvalidOperationException("template of the new theme threw");
    }

    private sealed class Rig : IDisposable
    {
        public readonly HeadlessPlatformApp App = new();
        public readonly StringTable Strings = new();
        public readonly HeadlessGpuDevice Device = new();
        public readonly HeadlessWindow Window;
        public readonly Root Root = new();
        public readonly AppHost Host;
        public readonly HeadlessSwapchain Sc;

        public Rig()
        {
            Tok.Use(DefA, ThemeKind.Dark);
            Window = new HeadlessWindow(new WindowDesc("main", new Size2(400, 300), 1f, Composited: true));
            Window.Show();
            Host = new AppHost(App, Window, Device, new HeadlessFontSystem(Strings), Strings, Root);
            Sc = Device.PrimarySwapchain!;
            Paint(30);   // the Enter of the first mount has finished
            Sc.RecordPresents = true;
        }

        public void Paint(int n = 1) { for (int i = 0; i < n; i++) Host.Paint(0); }

        /// <summary>Drive the transaction through the host's own loop (<see cref="AppHost.RunFrame"/>, which paints only when a wake
        /// reason asks for it), so every step must request its own frame.</summary>
        public ThemeTransactionResult Run(Task<ThemeTransactionResult> t, int max = 200)
        {
            for (int i = 0; i < max && !t.IsCompleted; i++) Host.RunFrame();
            Assert.True(t.IsCompleted, "the transaction completed");
            return t.Result;
        }

        /// <summary>Paint until the dissolve is over.</summary>
        public void Drain() { for (int i = 0; i < 80 && Host.FreezeState != FreezePhase.None; i++) Paint(); Paint(2); }

        public ThemeTransactionRequest Switch(ThemeDefinition next, int gen, ThemeKind kind = ThemeKind.Dark) => new(next, kind, () => Root.Gen.Value = gen)
        {
            Rollback = () => Root.Gen.Value = gen - 1,
            Label = "test",
        };

        public void Dispose()
        {
            Host.Dispose();
            App.Dispose();
            Tok.Use(DefA, ThemeKind.Dark);
        }
    }

    private static bool AnyMotion(AppHost host, out string which)
    {
        var scene = host.Scene;
        for (int i = 0; i < scene.Capacity; i++)
        {
            var n = scene.HandleAt(i);
            if (!scene.IsLive(n)) continue;
            if (host.Animation.HasTracks(n)) { which = "tracks@" + i; return true; }
            if (scene.TryGetBrushAnim(n, out _)) { which = "brush@" + i; return true; }
        }
        which = "";
        return false;
    }

    [Fact]
    public void ASwitch_FreezesApplies_UnderTheFrozenFrame_SettlesAndThawsWithTheDissolve_AndTheSettledSceneHasNoMotion()
    {
        using var rig = new Rig();
        var before = rig.Device.SummarizeLastFrame();
        int log0 = rig.Sc.PresentLog.Count;
        bool motionAtThaw = true;
        string which = "?";
        bool snappingAtCommit = true;
        var req = rig.Switch(DefB, 1) with
        {
            OnCommitted = () => { motionAtThaw = AnyMotion(rig.Host, out which); snappingAtCommit = rig.Host.IsSnapping; },
        };

        var task = rig.Host.RunThemeTransaction(req);
        Assert.True(rig.Host.IsThemeTransactionRunning);
        Assert.Same(DefA, Tok.Definition);   // nothing is applied before the freeze
        var r = rig.Run(task);
        int ticksAtThaw = rig.Root.Ticks;
        rig.Drain();

        Assert.Equal(ThemeTransactionOutcome.Committed, r.Outcome);
        Assert.NotNull(r.Settle);
        Assert.False(r.Settle!.TimedOut);
        Assert.False(r.Settle.Cut);
        Assert.True(r.LatencyMs > 0);
        Assert.Same(DefB, Tok.Definition);
        Assert.False(motionAtThaw, "motion at thaw start: " + which);
        Assert.False(snappingAtCommit, "the snap scope ended at the thaw");
        Assert.True(rig.Root.Ticks > ticksAtThaw, "the ticker runs again after the thaw");

        // The glass: the capture (S, normal), held frames (nothing shown), the dissolve from S to N, then N.
        var log = rig.Sc.PresentLog.GetRange(log0, rig.Sc.PresentLog.Count - log0);
        int i = 0;
        Assert.Equal(HeadlessPresentKind.Normal, log[i].Kind);
        Assert.Equal(before.Hash, log[i].Shown.Hash);
        i++;
        int held = 0;
        while (i < log.Count && log[i].Kind == HeadlessPresentKind.Held) { held++; i++; }
        Assert.True(held >= 1, "the apply and settle frames were held");
        var after = rig.Device.SummarizeLastFrame();
        Assert.NotEqual(before.Hash, after.Hash);
        int dissolves = 0;
        for (; i < log.Count && log[i].Kind == HeadlessPresentKind.Dissolve; i++)
        {
            dissolves++;
            Assert.Equal(before.Hash, log[i].From.Hash);   // every dissolve frame is (1-t)·S + t·N with these S and N
            Assert.Equal(after.Hash, log[i].Shown.Hash);
        }
        Assert.True(dissolves >= 3, $"dissolve frames: {dissolves}");
        for (; i < log.Count; i++) Assert.Equal(HeadlessPresentKind.Normal, log[i].Kind);
        Assert.Equal(after.Hash, log[^1].Shown.Hash);
        Assert.Equal(0, rig.Sc.LiveSnapshots);
        Assert.False(rig.Host.IsThemeTransactionRunning);
    }

    [Fact]
    public void AThrowingTemplate_RollsBackUnderTheSameFreeze_AndNothingOfTheFailedFrameIsShown()
    {
        using var rig = new Rig();
        var before = rig.Device.SummarizeLastFrame();
        int log0 = rig.Sc.PresentLog.Count;
        Exception? failed = null;
        var req = new ThemeTransactionRequest(DefB, ThemeKind.Dark, () => { rig.Root.Gen.Value = 1; rig.Root.Bomb.Value = true; })
        {
            Rollback = () => { rig.Root.Gen.Value = 0; rig.Root.Bomb.Value = false; },
            OnFailed = ex => failed = ex,
        };

        var r = rig.Run(rig.Host.RunThemeTransaction(req));
        rig.Drain();

        Assert.Equal(ThemeTransactionOutcome.RolledBack, r.Outcome);
        Assert.IsType<InvalidOperationException>(r.Error);
        Assert.Same(r.Error, failed);
        Assert.Same(DefA, Tok.Definition);
        Assert.Equal(FreezePhase.None, rig.Host.FreezeState);
        foreach (var p in rig.Sc.PresentLog.GetRange(log0, rig.Sc.PresentLog.Count - log0))
        {
            Assert.NotEqual(HeadlessPresentKind.Dissolve, p.Kind);                       // a cut, never a dissolve
            if (p.Kind == HeadlessPresentKind.Normal) Assert.Equal(before.Hash, p.Shown.Hash);   // only S was ever shown
        }

        // The host is healthy afterwards: the next transaction commits.
        var again = rig.Run(rig.Host.RunThemeTransaction(rig.Switch(DefB, 1)));
        Assert.Equal(ThemeTransactionOutcome.Committed, again.Outcome);
    }

    [Fact]
    public void AThrowingApply_RollsBack_AndCallsOnFailed()
    {
        using var rig = new Rig();
        Exception? failed = null;
        bool rolledBack = false;
        var req = new ThemeTransactionRequest(DefB, ThemeKind.Dark, () => throw new InvalidOperationException("apply"))
        {
            Rollback = () => rolledBack = true,
            OnFailed = ex => failed = ex,
        };
        var r = rig.Run(rig.Host.RunThemeTransaction(req));
        rig.Drain();
        Assert.Equal(ThemeTransactionOutcome.RolledBack, r.Outcome);
        Assert.True(rolledBack);
        Assert.Equal("apply", failed?.Message);
        Assert.Same(DefA, Tok.Definition);
        Assert.Equal(FreezePhase.None, rig.Host.FreezeState);
    }

    [Fact]
    public void AResizeUnderTheSettle_CutsTheFreeze_AndKeepsTheChange()
    {
        using var rig = new Rig();
        var req = rig.Switch(DefB, 1) with { Settle = new SettleSpec { FontsReady = static () => false, FontWaitMs = 1000 } };
        var task = rig.Host.RunThemeTransaction(req);
        for (int i = 0; i < 6; i++) rig.Paint();
        Assert.True(rig.Host.IsSettling, "the settle waits for fonts that never come");
        rig.Window.ClientSizePx = new Size2(420, 300);
        var r = rig.Run(task);
        rig.Drain();
        Assert.Equal(ThemeTransactionOutcome.Cut, r.Outcome);
        Assert.Same(DefB, Tok.Definition);
        Assert.Equal(1, rig.Root.Gen.Peek());
        Assert.Equal(FreezePhase.None, rig.Host.FreezeState);
        Assert.False(rig.Host.IsSnapping);
    }

    [Fact]
    public void TheWatchdog_CutsAStuckTransaction_AndKeepsTheChange()
    {
        using var rig = new Rig();
        rig.Host.FreezeWatchdogMs = 100;
        var req = rig.Switch(DefB, 1) with { Settle = new SettleSpec { FontsReady = static () => false, FontWaitMs = 5000 } };
        var r = rig.Run(rig.Host.RunThemeTransaction(req), max: 400);
        rig.Drain();
        Assert.Equal(ThemeTransactionOutcome.Cut, r.Outcome);
        Assert.Same(DefB, Tok.Definition);
        Assert.Equal(FreezePhase.None, rig.Host.FreezeState);
    }

    [Fact]
    public void RequestsWhileOneRuns_Coalesce_OnlyTheLatestWaitingOneRuns()
    {
        using var rig = new Rig();
        var first = rig.Host.RunThemeTransaction(rig.Switch(DefB, 1));
        var second = rig.Host.RunThemeTransaction(rig.Switch(DefA, 2));
        var third = rig.Host.RunThemeTransaction(rig.Switch(DefA, 4));
        Assert.True(second.IsCompleted);
        Assert.Equal(ThemeTransactionOutcome.Superseded, second.Result.Outcome);
        var r1 = rig.Run(first);
        var r3 = rig.Run(third);
        rig.Drain();
        Assert.Equal(ThemeTransactionOutcome.Committed, r1.Outcome);
        Assert.Equal(ThemeTransactionOutcome.Committed, r3.Outcome);
        Assert.Equal(4, rig.Root.Gen.Peek());
        Assert.Same(DefA, Tok.Definition);
    }

    [Fact]
    public void AWindowThatShowsNothing_AppliesAtOnce_WithoutAFreeze()
    {
        using var rig = new Rig();
        rig.Window.Hide();
        bool committed = false;
        var task = rig.Host.RunThemeTransaction(rig.Switch(DefB, 1) with { OnCommitted = () => committed = true });
        Assert.True(task.IsCompleted);
        Assert.Equal(ThemeTransactionOutcome.Immediate, task.Result.Outcome);
        Assert.True(committed);
        Assert.Same(DefB, Tok.Definition);
        Assert.Equal(FreezePhase.None, rig.Host.FreezeState);
    }

    [Fact]
    public void TheHooksSeam_IsWiredOnTheMainWindow()
    {
        using var rig = new Rig();
        InputHooks? hooks = null;
        rig.Host.Dispose();
        var probe = new HooksProbe();
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("probe", new Size2(100, 100), 1f));
        window.Show();
        using (var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(rig.Strings), rig.Strings, probe))
        {
            host.Paint(0);
            hooks = probe.Hooks;
            Assert.NotNull(hooks?.RunThemeTransaction);
            Assert.NotNull(hooks?.ThemeTransactionBusy);
            Assert.NotNull(hooks?.IsThemeTransactionRunning);
            var r = hooks!.RunThemeTransaction!(new ThemeTransactionRequest(DefB, ThemeKind.Dark, static () => { }));
            Assert.True(hooks.IsThemeTransactionRunning!());
            for (int i = 0; i < 100 && !r.IsCompleted; i++) host.Paint(0);
            Assert.False(hooks.IsThemeTransactionRunning!());
            Assert.Equal(ThemeTransactionOutcome.Committed, r.Result.Outcome);
        }
    }

    private sealed class HooksProbe : Component
    {
        public InputHooks? Hooks;
        public override Element Render()
        {
            Hooks = UseContext(InputHooks.Current);
            return new BoxEl { Width = 10f, Height = 10f };
        }
    }

    // ── DWM order (E2's table, executed by the transaction) ─────────────────────────────────────────────────────────────

    private static ThemeDefinition WithMaterial(WindowMaterial? m) => ThemeDefinition.FluentDefault with
    {
        Id = "mat-" + (m?.ToString() ?? "none-set"),
        Spacing = new SpacingScale { S = 10f },
        Materials = new MaterialPolicy { Light = m, Dark = m },
    };

    /// <summary>Run one transaction from <paramref name="from"/> to <paramref name="to"/> and return, per DWM write, the kind of the
    /// present it immediately preceded, plus how often the live path's writer ran.</summary>
    private static (List<HeadlessPresentKind?> Writes, int LiveWrites, WindowMaterial After) DwmRun(WindowMaterial from, WindowMaterial to)
    {
        using var rig = new Rig();
        rig.Host.SeedWindowMaterial(from);
        int live = 0;
        rig.Host.OnApplyThemeMaterial = _ => live++;
        var pendingWrites = new List<int>();
        rig.Host.WindowMaterialSink = (m, dark) => pendingWrites.Add(rig.Sc.PresentLog.Count);
        var r = rig.Run(rig.Host.RunThemeTransaction(new ThemeTransactionRequest(WithMaterial(to), ThemeKind.Dark, static () => { }) { Dissolve = new DissolveSpec(80f) }));
        rig.Drain();
        Assert.Equal(ThemeTransactionOutcome.Committed, r.Outcome);
        var writes = new List<HeadlessPresentKind?>();
        foreach (int at in pendingWrites) writes.Add(at < rig.Sc.PresentLog.Count ? rig.Sc.PresentLog[at].Kind : null);
        return (writes, live, rig.Host.WindowMaterial);
    }

    [Fact]
    public void Dwm_TranslucentToOpaque_ChangesAtThawEnd_InThePresentWhereTheNewFrameCoversTheWindow()
    {
        var (writes, live, after) = DwmRun(WindowMaterial.Mica, WindowMaterial.None);
        Assert.Equal([HeadlessPresentKind.Normal], writes);   // the write precedes the Release present (N), after every dissolve frame
        Assert.Equal(0, live);
        Assert.Equal(WindowMaterial.None, after);
    }

    [Fact]
    public void Dwm_OpaqueToTranslucent_ChangesAtThawStart_InTheFirstDissolvePresent()
    {
        var (writes, live, after) = DwmRun(WindowMaterial.None, WindowMaterial.Mica);
        Assert.Equal([HeadlessPresentKind.Dissolve], writes);
        Assert.Equal(0, live);
        Assert.Equal(WindowMaterial.Mica, after);
    }

    [Fact]
    public void Dwm_TranslucentToAnotherTranslucent_ChangesAtThawStart()
    {
        var (writes, _, after) = DwmRun(WindowMaterial.Mica, WindowMaterial.MicaAlt);
        Assert.Equal([HeadlessPresentKind.Dissolve], writes);
        Assert.Equal(WindowMaterial.MicaAlt, after);
    }

    [Fact]
    public void Dwm_TheSameMaterialAndFlag_WritesNothing()
    {
        var (writes, live, after) = DwmRun(WindowMaterial.Mica, WindowMaterial.Mica);
        Assert.Empty(writes);
        Assert.Equal(0, live);
        Assert.Equal(WindowMaterial.Mica, after);
    }
    // ── pop-outs (plan M9: thawed in the same frame as the main window) ─────────────────────────────────────────────────

    [Fact]
    public void APopOut_FreezesWithTheTransaction_ReRendersUnderTheNewDefinition_AndThawsInTheSameCall()
    {
        Tok.Use(DefA, ThemeKind.Dark);
        using var app = new HeadlessPlatformApp();
        var strings = new StringTable();
        var device = new HeadlessGpuDevice();
        FluentGpu.Hosting.Threading.ThreadGuard.BindCurrent(FluentGpu.Hosting.Threading.ThreadGuard.ThreadRole.Ui);
        var window = new HeadlessWindow(new WindowDesc("main", new Size2(400, 300), 1f));
        window.Show();
        var root = new Root();
        using var host = new AppHost(app, window, device, new HeadlessFontSystem(strings), strings, root);
        var thread = host.InstallRenderThreadForTest();
        var childWindow = new HeadlessWindow(new WindowDesc("pop-out", new Size2(320, 180), 1f, Composited: true, CustomFrame: true));
        childWindow.Show();
        var childRoot = new Root();
        var child = new AppHost(app, childWindow, device, new HeadlessFontSystem(strings), strings, childRoot,
            images: null, frameTime: null, compositeSwapchain: true, isDetachedChild: true, parentRenderThread: thread);
        host.AdoptDetachedChild(child);
        host.AttachChildRenderSourceForTest(child);
        try
        {
            for (int i = 0; i < 30; i++) { host.Paint(0); child.RunFrame(); }
            var childSc = device.CreatedSwapchains[^1];
            childSc.RecordPresents = true;

            bool childFrozenAtApply = false, childThawingAtCommit = false;
            var req = new ThemeTransactionRequest(DefB, ThemeKind.Dark, () =>
            {
                childFrozenAtApply = child.FreezeState == FreezePhase.Frozen;
                root.Gen.Value = 1;
                childRoot.Gen.Value = 1;
            })
            { OnCommitted = () => childThawingAtCommit = child.FreezeState == FreezePhase.Dissolving };
            var task = host.RunThemeTransaction(req);
            for (int i = 0; i < 200 && !task.IsCompleted; i++) { host.Paint(0); child.RunFrame(); }
            for (int i = 0; i < 80 && (host.FreezeState != FreezePhase.None || child.FreezeState != FreezePhase.None); i++) { host.Paint(0); child.RunFrame(); }

            Assert.Equal(ThemeTransactionOutcome.Committed, task.Result.Outcome);
            Assert.True(childFrozenAtApply, "the pop-out was frozen when the change applied");
            Assert.True(childThawingAtCommit, "the pop-out thawed in the same call as the main window");
            Assert.True(childSc.HeldPresentCount > 0, "the pop-out's presents were held while the main window settled");
            Assert.Equal(FreezePhase.None, child.FreezeState);
            Assert.Equal(0, childSc.LiveSnapshots);
        }
        finally { Tok.Use(DefA, ThemeKind.Dark); }
    }
}
