using Bench.Contracts;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace FluentGpuBench;

internal static class FluentBenchState
{
    internal static BenchOptions Options { get; private set; } = null!;
    internal static readonly FloatSignal Transform = new(0f);
    internal static readonly Signal<string> Text = new("value 0000");
    internal static readonly Signal<int> Tree = new(0);
    internal static readonly Signal<int> Nav = new(0);
    internal static readonly Signal<int> ProbeId = new(0);
    internal static readonly ItemsViewController Scroll = new();
    private static string[] _rowLabels = [];
    private static int _scrollResetPeriod = BenchWorkload.VirtualScrollResetPeriod;
    private static int _offsetChanges;
    private static int _offsetResets;
    private static float _lastOffset = -1f;
    private static float _offsetMax;
    private static FrameIdMutationLog? _mutationLog;

    private static readonly Element ChurnA = BuildChurnTree("a", ColorF.FromRgba(0x20, 0x78, 0xD4));
    private static readonly Element ChurnB = BuildChurnTree("b", ColorF.FromRgba(0x10, 0x7C, 0x10));

    internal static void Initialize(BenchOptions options)
    {
        Options = options;
        if (BenchScenarios.IsVirtualScroll(options.Scenario))
        {
            // Sized to the scenario, and resolved before any measured iteration: the row-label table is part of the
            // scenario's resident set, and looking the row count up per mutation would put a string compare in the hot
            // path.
            _rowLabels = BuildRowLabels(BenchWorkload.RowsFor(options.Scenario));
            _scrollResetPeriod = BenchWorkload.ScrollResetPeriodFor(options.Scenario);
        }
        _mutationLog?.Dispose();
        _mutationLog = new FrameIdMutationLog(FrameIdProbe.DefaultMutationLogPath(options.OutputPath));
    }

    /// <summary>Paints the desktop frame-ID probe and optionally appends a mutation marker for capture join.</summary>
    internal static void PaintFrameId(int measureIteration, long qpc, bool logMutation)
    {
        ProbeId.Value = measureIteration;
        FrameIdProbe.Encode(measureIteration, out byte r, out byte g, out byte b);
        if (logMutation) _mutationLog?.Write(measureIteration, qpc, r, g, b);
    }

    internal static void Mutate(int iteration)
    {
        switch (Options.Scenario)
        {
            case BenchScenarios.VirtualScroll1K:
            case BenchScenarios.VirtualScroll10K:
                // Scenario-validity instrumentation, mirroring the WinUI host: scalar-only, so it allocates nothing.
                float observed = Scroll.ScrollOffset;
                if (observed != _lastOffset)
                {
                    _offsetChanges++;
                    if (observed < _lastOffset) _offsetResets++;
                    _lastOffset = observed;
                    if (observed > _offsetMax) _offsetMax = observed;
                }
                if (iteration > 0 && iteration % _scrollResetPeriod == 0) Scroll.StartBringItemIntoView(0, 0f);
                else Scroll.ScrollBy(BenchWorkload.VirtualRowHeight * BenchWorkload.VirtualScrollRowsPerOperation);
                break;
            case BenchScenarios.LocalizedTransform:
                Transform.Value = (iteration & 1) == 0 ? 12f : 0f;
                break;
            case BenchScenarios.LocalizedText:
                Text.Value = $"value {iteration:0000}";
                break;
            case BenchScenarios.TreeChurn:
                Tree.Value = iteration & 1;
                break;
            case BenchScenarios.PageNavigation:
                // The whole iteration number, not just its parity: the destination page alternates with the low bit
                // while every string on it is stamped with the iteration, so no navigation can be served from a
                // previous navigation's text cache. NavSlot reads this signal, so the assignment is what triggers the
                // rebuild of the destination tree inside the measured section.
                Nav.Value = iteration;
                break;
        }
    }

    /// <summary>Runtime proof that the viewport really traversed the list; null for non-scroll scenarios.</summary>
    internal static string? ScrollValidityNote() => BenchScenarios.IsVirtualScroll(Options.Scenario)
        ? string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"scroll: offsetChanges={_offsetChanges} wraps={_offsetResets} maxOffsetPx={_offsetMax:0}.")
        : null;

    internal static Element BuildScenario()
    {
        Element content = Options.Scenario switch
        {
            BenchScenarios.Startup => Center(Text("FluentGpu framework benchmark")),
            BenchScenarios.Buttons225 => BuildButtons(),
            BenchScenarios.Text1125 => BuildTextCanvas(),
            BenchScenarios.VirtualScroll1K or BenchScenarios.VirtualScroll10K => BuildVirtualList(),
            BenchScenarios.LocalizedTransform => BuildLocalizedTree(transform: true),
            BenchScenarios.LocalizedText => BuildLocalizedTree(transform: false),
            BenchScenarios.TreeChurn => BuildChurnHost(),
            BenchScenarios.PageNavigation => BuildNavigationHost(),
            _ => throw new InvalidOperationException(Options.Scenario),
        };
        return WithFrameIdProbe(content);
    }

    private static Element WithFrameIdProbe(Element content) => Canvas.Create(
        BenchWorkload.WindowWidth,
        BenchWorkload.WindowHeight,
        [
            new CanvasChild(0f, 0f, content),
            new CanvasChild(FrameIdProbe.ClientX, FrameIdProbe.ClientY, new BoxEl
            {
                Width = FrameIdProbe.SizePx,
                Height = FrameIdProbe.SizePx,
                Shrink = 0f,
                Fill = Prop.Of(static () =>
                {
                    FrameIdProbe.Encode(ProbeId.Value, out byte r, out byte g, out byte b);
                    return ColorF.FromRgba(r, g, b);
                }),
            }),
        ]);

    private static Element BuildButtons()
    {
        var rows = new Element[BenchWorkload.ButtonRows];
        int n = 0;
        for (int y = 0; y < rows.Length; y++)
        {
            var buttons = new Element[BenchWorkload.ButtonColumns];
            for (int x = 0; x < buttons.Length; x++, n++)
                buttons[x] = Button.Standard($"Button {n + 1}", Noop) with { Grow = 1f, Basis = 0f };
            rows[y] = HStack(4f, buttons) with { Grow = 1f, Basis = 0f };
        }
        return new BoxEl
        {
            Direction = 1,
            Gap = 4f,
            Padding = Edges4.All(8f),
            Children = rows,
        };
    }

    private static Element BuildTextCanvas()
    {
        var children = new CanvasChild[BenchWorkload.TextColumns * BenchWorkload.TextRows];
        int n = 0;
        for (int y = 0; y < BenchWorkload.TextRows; y++)
        for (int x = 0; x < BenchWorkload.TextColumns; x++, n++)
            children[n] = new CanvasChild(x * 47f, y * 15.5f, new TextEl($"T{n:0000}") { Size = 11f });
        return Canvas.Create(BenchWorkload.WindowWidth, BenchWorkload.WindowHeight, children);
    }

    private static Element BuildVirtualList() => ItemsView.CreateBound(
        BenchWorkload.RowsFor(Options.Scenario),
        BuildVirtualRow,
        RepeatLayout.Stack(BenchWorkload.VirtualRowHeight),
        new ListOptions
        {
            Controller = Scroll,
            SelectionMode = ItemsSelectionMode.None,
            Selector = SelectorVisual.None,
            Overscan = 4,
            RepaintBoundary = true,
        });

    private static Element BuildVirtualRow(RowScope scope)
    {
        var row = new BoxEl
        {
            Direction = 0,
            Height = BenchWorkload.VirtualRowHeight,
            Gap = 8f,
            Padding = new Edges4(8f, 4f, 8f, 4f),
            AlignItems = FlexAlign.Center,
            Children =
            [
                new BoxEl { Width = 28f, Height = 28f, Fill = Tok.FillControlSecondary, Shrink = 0f },
                new TextEl(Prop.Of(() => _rowLabels[scope.Index.Value])) { Width = 120f },
                new TextEl("Field A") { Width = 150f },
                new TextEl("Field B") { Width = 150f },
                new TextEl("Field C") { Width = 150f },
            ],
        };
        return SelectorVisualsBound.None(scope, row);
    }

    private static string[] BuildRowLabels(int rows)
    {
        var labels = new string[rows];
        for (int i = 0; i < labels.Length; i++) labels[i] = $"Row {i:00000}";
        return labels;
    }

    private static Element BuildLocalizedTree(bool transform)
    {
        var nodes = new Element[BenchWorkload.LocalizedNodeCount - 1];
        for (int i = 0; i < nodes.Length; i++)
        {
            if (i == nodes.Length / 2 && transform)
            {
                nodes[i] = new BoxEl
                {
                    Width = 18f,
                    Height = 18f,
                    Fill = Tok.AccentDefault,
                    Transform = Prop.Of(() => Affine2D.Translation(Transform.Value, 0f)),
                };
            }
            else if (i == nodes.Length / 2)
            {
                nodes[i] = new TextEl(Text) { Width = 78f, Height = 18f, Size = 11f };
            }
            else
            {
                nodes[i] = new BoxEl
                {
                    Width = 18f,
                    Height = 18f,
                    Fill = (i & 1) == 0 ? Tok.FillControlDefault : Tok.FillControlSecondary,
                };
            }
        }
        return new BoxEl
        {
            Direction = 0,
            Wrap = true,
            Gap = 2f,
            Width = BenchWorkload.WindowWidth,
            Height = BenchWorkload.WindowHeight,
            ClipToBounds = true,
            Children = nodes,
        };
    }

    private static Element BuildChurnHost() => new BoxEl
    {
        Width = BenchWorkload.WindowWidth,
        Height = BenchWorkload.WindowHeight,
        ClipToBounds = true,
        Children = [Embed.Comp(() => new ChurnSlot())],
    };

    private static Element BuildChurnTree(string key, ColorF color)
    {
        var leaves = new Element[BenchWorkload.ChurnSubtreeNodes - 1];
        for (int i = 0; i < leaves.Length; i++)
            leaves[i] = new BoxEl { Width = 24f, Height = 24f, Fill = color, Opacity = 0.5f + (i % 5) * 0.1f };
        return new BoxEl
        {
            Key = key,
            Direction = 0,
            Wrap = true,
            Gap = 2f,
            Children = leaves,
        };
    }

    private static Element BuildNavigationHost() => new BoxEl
    {
        Width = BenchWorkload.WindowWidth,
        Height = BenchWorkload.WindowHeight,
        ClipToBounds = true,
        Children = [Embed.Comp(() => new NavSlot())],
    };

    private static void Noop() { }

    private sealed class ChurnSlot : FluentGpu.Hooks.Component
    {
        public override Element Render() => Tree.Value == 0 ? ChurnA : ChurnB;
    }

    /// <summary>
    /// The navigation host. The iteration is read from <see cref="Nav"/> <em>inside</em> Render, which is what makes a
    /// navigation happen at all: component props freeze at mount (see
    /// <c>docs/design/subsystems/component-props-contract.md</c>), so an iteration passed as a field would be captured
    /// once and never change. Reading the signal here re-runs Render on every assignment, and Render builds the
    /// destination page from scratch - the page-construction cost this scenario exists to measure.
    /// </summary>
    private sealed class NavSlot : FluentGpu.Hooks.Component
    {
        public override Element Render()
        {
            int iteration = Nav.Value;
            return BenchWorkload.NavIsDetailPage(iteration)
                ? FluentNavigationPages.BuildDetail(iteration)
                : FluentNavigationPages.BuildLibrary(iteration);
        }
    }
}

internal sealed class FluentBenchApp : FluentGpu.Hooks.Component
{
    public override Element Render() => FluentBenchState.BuildScenario();
}
