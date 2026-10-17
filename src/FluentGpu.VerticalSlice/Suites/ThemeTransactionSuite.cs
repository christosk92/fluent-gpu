using System;
using System.Collections.Generic;
using System.Globalization;
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
using static FluentGpu.VerticalSlice.Harness.Gate;

namespace FluentGpu.VerticalSlice.Suites;

/// <summary>
/// EP14 / E5 - the theme transaction, the plan's R2-B2 gate: 100 transactions alternating two definitions with different geometry
/// (spacing, radii) and two root component types, with a ticker running throughout (the lyrics / visualizer stand-in). Per
/// transaction: it commits; its settle never times out; every present between the capture and the thaw is held; every dissolve
/// present is <c>(1-t)·S + t·N</c> with the S captured at the freeze and the N the settled frame; at thaw start the settled scene
/// carries no animation row and no brush fade (snap scope); the latency (request → thaw start) is recorded. Then a template that
/// throws rolls back with only S ever shown. The matrix (cuts, coalescing, DWM order, pop-outs) is Engine.Tests/ThemeTransactionTests.
/// </summary>
static class ThemeTransactionSuite
{
    const int Transactions = 100;

    static readonly ThemeDefinition DefA = ThemeDefinition.FluentDefault;
    static readonly ThemeDefinition DefB = ThemeDefinition.FluentDefault with
    {
        Id = "tx-suite-b",
        Spacing = new SpacingScale { S = 14f, M = 22f, L = 30f },
        Radii = new RadiiScale { Control = 0f, Card = 2f, Overlay = 2f },
    };

    sealed class Root : Component
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
                Width = 480f, Height = 320f, Direction = 1,
                Children = Bomb.Value ? [page, Embed.Comp(static () => new Bomb())] : [page],
            };
        }
    }

    abstract class Page : Component
    {
        protected abstract ColorF Ink { get; }

        public override Element Render()
        {
            var kids = new Element[6];
            for (int i = 0; i < kids.Length; i++)
                kids[i] = new BoxEl
                {
                    Key = "row" + i, Height = 18f + Tok.Definition.Spacing.S, Fill = Ink, BrushTransitionMs = 250f,
                    Corners = CornerRadius4.All(Tok.Definition.Radii.Card), Enter = new EnterExit(Opacity: 0f, Active: true),
                };
            return new BoxEl { Direction = 1, Padding = Edges4.All(Tok.Definition.Spacing.M), Gap = Tok.Definition.Spacing.S, Children = kids };
        }
    }

    sealed class PageA : Page { protected override ColorF Ink => ColorF.FromRgba(190, 50, 50); }
    sealed class PageB : Page { protected override ColorF Ink => ColorF.FromRgba(50, 50, 190); }

    sealed class Bomb : Component
    {
        public override Element Render() => throw new InvalidOperationException("theme-transaction suite: a template that throws");
    }

    public static void Run(StringTable strings)
    {
        Tok.Use(DefA, ThemeKind.Dark);
        try
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("theme-tx", new Size2(480, 320), 1f, Composited: true));
            window.Show();
            var device = new HeadlessGpuDevice();
            var root = new Root();
            using var host = new AppHost(app, window, device, new HeadlessFontSystem(strings), strings, root);
            for (int i = 0; i < 30; i++) host.Paint(0);
            var sc = device.PrimarySwapchain!;
            sc.RecordPresents = true;

            int committed = 0, timeouts = 0, badGlass = 0, motion = 0, maxPasses = 0, ticksFrozen = 0;
            var latencies = new List<double>(Transactions);
            string firstProblem = "";
            for (int n = 1; n <= Transactions; n++)
            {
                var before = device.SummarizeLastFrame();
                int log0 = sc.PresentLog.Count;
                int gen = n;
                bool moving = false;
                int ticksAtApply = -1, ticksAtCommit = -1;
                var req = new ThemeTransactionRequest((n & 1) == 1 ? DefB : DefA, ThemeKind.Dark, () => { ticksAtApply = root.Ticks; root.Gen.Value = gen; })
                {
                    Rollback = () => root.Gen.Value = gen - 1,
                    OnCommitted = () => { moving = AnyMotion(host); ticksAtCommit = root.Ticks; },
                    Label = "suite#" + n.ToString(CultureInfo.InvariantCulture),
                };
                Task<ThemeTransactionResult> t = host.RunThemeTransaction(req);
                for (int i = 0; i < 200 && !t.IsCompleted; i++) host.Paint(0);
                for (int i = 0; i < 80 && host.FreezeState != FreezePhase.None; i++) host.Paint(0);
                host.Paint(0);
                var r = t.IsCompletedSuccessfully ? t.Result : null;
                var after = device.SummarizeLastFrame();

                if (r?.Outcome == ThemeTransactionOutcome.Committed) committed++;
                else if (firstProblem.Length == 0) firstProblem = $"#{n} outcome={r?.Outcome.ToString() ?? "pending"}";
                if (r?.Settle is { } s) { if (s.TimedOut) timeouts++; maxPasses = Math.Max(maxPasses, s.Passes); }
                if (r is not null) latencies.Add(r.LatencyMs);
                if (moving) motion++;
                if (ticksAtApply != ticksAtCommit) ticksFrozen++;   // the ticker stood still from the apply to the thaw (snap scope)
                if (!GlassIsSHeldDissolveN(sc.PresentLog, log0, before.Hash, after.Hash, out string why))
                {
                    badGlass++;
                    if (firstProblem.Length == 0) firstProblem = $"#{n} {why}";
                }
            }
            latencies.Sort();
            double p50 = latencies.Count > 0 ? latencies[latencies.Count / 2] : 0, max = latencies.Count > 0 ? latencies[^1] : 0;

            Check($"gate.theme-tx.hundred {Transactions} transactions alternating two definitions (other spacing and radii) and two root types, a ticker running: all commit, 0 settle-timeout (R2-B2), latency recorded",
                committed == Transactions && timeouts == 0 && latencies.Count == Transactions,
                string.Create(CultureInfo.InvariantCulture, $"committed={committed} timeouts={timeouts} maxPasses={maxPasses} latencyMs p50={p50:F2} max={max:F2} {firstProblem}"));
            Check("gate.theme-tx.frames-s-n-formula every present of every transaction is S (the capture), held (nothing shown) until the thaw, then (1-t)·S + t·N with the settled N, then N",
                badGlass == 0, $"bad={badGlass} {firstProblem}");
            Check("gate.theme-tx.settled-no-motion at thaw start the settled scene has no animation row and no brush fade (the snap scope landed every Enter and fade), and the ticker stood still under the freeze",
                motion == 0 && ticksFrozen == 0, $"withMotion={motion} tickerMoved={ticksFrozen}");

            // A template that throws: rolled back under the freeze, only S is ever shown.
            var s0 = device.SummarizeLastFrame();
            int logR = sc.PresentLog.Count;
            int g = root.Gen.Peek();
            Exception? failed = null;
            var bomb = host.RunThemeTransaction(new ThemeTransactionRequest(DefB, ThemeKind.Dark, () => { root.Gen.Value = g + 1; root.Bomb.Value = true; })
            {
                Rollback = () => { root.Gen.Value = g; root.Bomb.Value = false; },
                OnFailed = ex => failed = ex,
            });
            for (int i = 0; i < 200 && !bomb.IsCompleted; i++) host.Paint(0);
            for (int i = 0; i < 20; i++) host.Paint(0);
            bool onlyS = true;
            for (int i = logR; i < sc.PresentLog.Count; i++)
            {
                var p = sc.PresentLog[i];
                if (p.Kind == HeadlessPresentKind.Dissolve || (p.Kind == HeadlessPresentKind.Normal && p.Shown.Hash != s0.Hash)) onlyS = false;
            }
            var rb = bomb.IsCompletedSuccessfully ? bomb.Result : null;
            Check("gate.theme-tx.rollback a template that throws rolls the transaction back under the same freeze: the previous definition is back, OnFailed is told, and no present ever showed anything but S",
                rb?.Outcome == ThemeTransactionOutcome.RolledBack && failed is not null && onlyS && ReferenceEquals(Tok.Definition, g % 2 == 1 ? DefB : DefA)
                && host.FreezeState == FreezePhase.None,
                $"outcome={rb?.Outcome} failed={failed?.Message} onlyS={onlyS}");
        }
        finally
        {
            Tok.Use(DefA, ThemeKind.Dark);
        }
    }

    static bool AnyMotion(AppHost host)
    {
        var scene = host.Scene;
        for (int i = 0; i < scene.Capacity; i++)
        {
            var node = scene.HandleAt(i);
            if (!scene.IsLive(node)) continue;
            if (host.Animation.HasTracks(node) || scene.TryGetBrushAnim(node, out _)) return true;
        }
        return false;
    }

    /// <summary>The glass of one transaction: the capture shows S, then only held presents, then dissolves from S to N, then N.</summary>
    static bool GlassIsSHeldDissolveN(List<HeadlessPresentRecord> log, int from, ulong s, ulong n, out string why)
    {
        int i = from;
        if (i >= log.Count || log[i].Kind != HeadlessPresentKind.Normal || log[i].Shown.Hash != s) { why = "the capture did not show S"; return false; }
        i++;
        int held = 0;
        while (i < log.Count && log[i].Kind == HeadlessPresentKind.Held) { held++; i++; }
        if (held == 0) { why = "no held frame"; return false; }
        int dissolves = 0;
        for (; i < log.Count && log[i].Kind == HeadlessPresentKind.Dissolve; i++)
        {
            if (log[i].From.Hash != s || log[i].Shown.Hash != n || log[i].T <= 0f || log[i].T >= 1f) { why = "a dissolve frame was not (1-t)·S + t·N"; return false; }
            dissolves++;
        }
        if (dissolves == 0) { why = "no dissolve"; return false; }
        for (; i < log.Count; i++)
            if (log[i].Kind != HeadlessPresentKind.Normal || log[i].Shown.Hash != n) { why = "a frame after the thaw was not N"; return false; }
        why = "";
        return true;
    }
}
