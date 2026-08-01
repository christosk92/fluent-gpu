using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Bench.Contracts;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics;

namespace WinUIBench;

internal sealed partial class BenchmarkWindow : Window
{
    private readonly BenchOptions _options;
    private readonly FrameworkElement _root;
    private readonly double[] _frameMs;
    private readonly double[] _cpuMs;
    private readonly List<BenchRowItem> _items = [];
    private readonly FrameIdMutationLog _mutationLog;
    private readonly SolidColorBrush _probeBrush = new(Windows.UI.Color.FromArgb(255, 16, 16, 16));
    private TextBlock? _localizedText;
    private TranslateTransform? _localizedTranslate;
    private ListView? _list;
    private ScrollViewer? _listScrollViewer;
    private ContentControl? _churnHost;
    private UIElement? _churnA;
    private UIElement? _churnB;
    private readonly DispatcherQueueHandler _rawStep;
    private int _rawIndex;
    private int _coldTicks;
    private long _coldFirstTickQpc;
    private int _scrollIndex;
    private int _scrollLastRow;
    private int _scrollResetPeriod = BenchWorkload.VirtualScrollResetPeriod;
    private readonly IntPtr _hwnd;
    private readonly double _dpiScale;
    private int _changeViewRejected;
    private int _offsetChanges;
    private int _offsetResets;
    private double _lastOffset = -1d;
    private double _offsetMax;
    private int _warmup;
    private int _completed;
    private long _measurementStart;
    private long _pendingFrameStart;
    private long _allocationStart;
    private double _pendingCpuMs;
    private bool _finished;

    internal BenchmarkWindow(BenchOptions options)
    {
        _options = options;
        // One cached handler instance: re-creating the delegate per iteration would charge the dispatcher hop to the
        // measured allocation total.
        _rawStep = RunRawCpuStep;
        _mutationLog = new FrameIdMutationLog(FrameIdProbe.DefaultMutationLogPath(options.OutputPath));
        _frameMs = new double[BenchScenarios.IsColdLoad(options.Scenario) ? 1 : options.Iterations];
        _cpuMs = new double[_frameMs.Length];
        Title = $"WinUI 3 benchmark - {options.Scenario}";
        // The shared 1200x720 client area is specified in DIPs (see METHODOLOGY): XAML lays out in DIPs, and the
        // FluentGPU host sizes its window in DIPs too. AppWindow.ResizeClient takes PHYSICAL pixels, so passing the
        // spec straight through gave WinUI a 1200x720 *physical* client - only ~816x490 DIPs on this 147% display.
        // WinUI was laying out (and realizing list rows for) barely half the viewport FluentGPU was.
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _dpiScale = GetDpiForWindow(_hwnd) / 96d;
        AppWindow.ResizeClient(new SizeInt32(
            (int)Math.Round(BenchWorkload.WindowWidth * _dpiScale),
            (int)Math.Round(BenchWorkload.WindowHeight * _dpiScale)));
        _root = WithFrameIdProbe(BuildScenario());
        Content = _root;
        _root.Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _root.Loaded -= OnLoaded;
        if (_list is not null)
            _listScrollViewer = FindScrollViewer(_list)
                ?? throw new InvalidOperationException("WinUI ListView did not create its ScrollViewer.");
        if (BenchScenarios.IsColdLoad(_options.Scenario) ||
            string.Equals(_options.Pass, "cadence", StringComparison.OrdinalIgnoreCase))
        {
            CompositionTarget.Rendering += OnRendering;
            return;
        }

        _root.DispatcherQueue.TryEnqueue(_rawStep);
    }

    private void OnRendering(object? sender, object e)
    {
        if (_finished) return;
        long now = Stopwatch.GetTimestamp();

        if (BenchScenarios.IsColdLoad(_options.Scenario))
        {
            // The first Rendering callback fires *before* the frame it belongs to is handed to the compositor, so it is
            // strictly earlier than FluentGPU's submit-confirmed stop. Stop on the second tick: by then the first frame
            // has been handed over. The first tick is retained as the engine-ready sub-mark.
            if (_coldTicks++ == 0)
            {
                _coldFirstTickQpc = now;
                return;
            }
            _frameMs[0] = TicksToMs(now - BenchClock.ProcessStartQpc);
            _cpuMs[0] = 0d;
            _measurementStart = BenchClock.ProcessStartQpc;
            Finish(now, 0,
                "Process module initialization to the second CompositionTarget.Rendering callback - first frame handed " +
                "to the compositor, including the selected content tree. engineReadyMs is the first callback.",
                new ColdStartMarks
                {
                    EngineReadyMs = TicksToMs(_coldFirstTickQpc - BenchClock.ProcessStartQpc),
                    FirstPresentMs = TicksToMs(now - BenchClock.ProcessStartQpc),
                    DrivenFrames = 0,
                });
            return;
        }

        if (_warmup < _options.WarmupFrames)
        {
            PaintFrameId(_warmup, Stopwatch.GetTimestamp(), logMutation: false);
            Mutate(_warmup++);
            return;
        }

        if (_measurementStart == 0)
        {
            _measurementStart = now;
            _allocationStart = GC.GetAllocatedBytesForCurrentThread();
            BenchTrace.Log.PhaseStart("WinUI 3", _options.Scenario, "measure", _options.Iterations, now);
        }

        if (_pendingFrameStart != 0)
        {
            _frameMs[_completed] = TicksToMs(now - _pendingFrameStart);
            _cpuMs[_completed] = _pendingCpuMs;
            BenchTrace.Log.MutationAck("WinUI 3", _options.Scenario, _completed, now);
            _completed++;
            if (_completed == _frameMs.Length)
            {
                Finish(now, GC.GetAllocatedBytesForCurrentThread() - _allocationStart,
                    "Display-paced pass; frameMs is mutation to the next CompositionTarget.Rendering callback. PresentMon/WPR owns displayed cadence and GPU time.");
                return;
            }
        }

        long cpuStart = Stopwatch.GetTimestamp();
        BenchTrace.Log.MutationStart("WinUI 3", _options.Scenario, _completed, cpuStart);
        PaintFrameId(_completed, cpuStart, logMutation: true);
        Mutate(_completed + _options.WarmupFrames);
        _root.UpdateLayout();
        long cpuStop = Stopwatch.GetTimestamp();
        _pendingCpuMs = TicksToMs(cpuStop - cpuStart);
        _pendingFrameStart = cpuStart;
    }

    /// <summary>
    /// Exactly one warmup-or-measured iteration per dispatcher callback, then re-enqueues itself: running the whole
    /// pass inside a single callback starves the dispatcher, so asynchronous mutations (notably
    /// <see cref="ScrollViewer.ChangeView(double?, double?, float?, bool)"/>) would stack up across a thousand
    /// iterations instead of being serviced. The inter-iteration hop is outside the timed section, exactly as
    /// FluentGPU's inter-iteration wait for the published frame is.
    /// </summary>
    private void RunRawCpuStep()
    {
        if (_finished) return;

        if (_rawIndex < _options.WarmupFrames)
        {
            int warmup = _rawIndex++;
            PaintFrameId(warmup, Stopwatch.GetTimestamp(), logMutation: false);
            Mutate(warmup);
            _root.UpdateLayout();
            _root.DispatcherQueue.TryEnqueue(_rawStep);
            return;
        }

        int i = _rawIndex - _options.WarmupFrames;
        if (_measurementStart == 0)
        {
            _allocationStart = GC.GetAllocatedBytesForCurrentThread();
            _measurementStart = Stopwatch.GetTimestamp();
            BenchTrace.Log.PhaseStart("WinUI 3", _options.Scenario, "measure", _options.Iterations, _measurementStart);
        }

        long start = Stopwatch.GetTimestamp();
        BenchTrace.Log.MutationStart("WinUI 3", _options.Scenario, i, start);
        PaintFrameId(i, start, logMutation: true);
        Mutate(i + _options.WarmupFrames);
        _root.UpdateLayout();
        long stop = Stopwatch.GetTimestamp();
        BenchTrace.Log.MutationAck("WinUI 3", _options.Scenario, i, stop);
        double elapsed = TicksToMs(stop - start);
        _frameMs[i] = elapsed;
        _cpuMs[i] = elapsed;
        _rawIndex++;

        if (i + 1 == _frameMs.Length)
        {
            Finish(stop, GC.GetAllocatedBytesForCurrentThread() - _allocationStart,
                "Raw UI-thread pass; one iteration per dispatcher callback (the hop is excluded from the timed section). " +
                "cpuWorkMs is mutation plus synchronous UpdateLayout. Composition/render work is measured by ETW, not this field.");
            return;
        }
        _root.DispatcherQueue.TryEnqueue(_rawStep);
    }

    private void Finish(long stop, long allocated, string notes, ColdStartMarks? coldStart = null)
    {
        if (_finished) return;
        _finished = true;
        notes += " " + ClientAreaNote();
        if (BenchScenarios.IsVirtualScroll(_options.Scenario)) notes += " " + ScrollValidityNote();
        CompositionTarget.Rendering -= OnRendering;
        _mutationLog.Dispose();
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        string version = typeof(Application).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "3.0.0-dev";
        BenchResult result = BenchResult.Create(
            "WinUI 3", version, _options, _measurementStart, stop, _frameMs, _cpuMs, allocated,
            process.WorkingSet64, process.PrivateMemorySize64, notes, coldStart);
        result.Write(_options.OutputPath);
        BenchTrace.Log.PhaseStop("WinUI 3", _options.Scenario, "measure", _frameMs.Length, stop);
        BenchTrace.Log.ResultWritten("WinUI 3", _options.Scenario, _options.OutputPath);
        DispatcherQueue.TryEnqueue(Close);
    }

    /// <summary>Runtime proof of the client area actually laid out, in both physical pixels and DIPs.</summary>
    private string ClientAreaNote()
    {
        GetClientRect(_hwnd, out RECT client);
        return string.Create(CultureInfo.InvariantCulture,
            $"clientPx={client.Right - client.Left}x{client.Bottom - client.Top} " +
            $"clientDip={(client.Right - client.Left) / _dpiScale:0}x{(client.Bottom - client.Top) / _dpiScale:0} " +
            $"dpiScale={_dpiScale:0.###}.");
    }

    /// <summary>Runtime proof that the viewport really traversed the list rather than sitting still or pinning.</summary>
    private string ScrollValidityNote() => string.Create(CultureInfo.InvariantCulture,
        $"scroll: offsetChanges={_offsetChanges} wraps={_offsetResets} maxOffsetPx={_offsetMax:0} " +
        $"changeViewRejected={_changeViewRejected}.");

    private FrameworkElement WithFrameIdProbe(FrameworkElement content)
    {
        var probe = new Border
        {
            Width = FrameIdProbe.SizePx,
            Height = FrameIdProbe.SizePx,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(FrameIdProbe.ClientX, FrameIdProbe.ClientY, 0, 0),
            Background = _probeBrush,
            IsHitTestVisible = false,
        };
        var grid = new Grid
        {
            Width = BenchWorkload.WindowWidth,
            Height = BenchWorkload.WindowHeight,
            // Same window background as the FluentGPU host, so both hosts rasterize a comparable opaque surface
            // instead of one of them compositing against an unpainted black window.
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x24, 0x21, 0x1C)),
        };
        grid.Children.Add(content);
        grid.Children.Add(probe);
        return grid;
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(IntPtr hwnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private void PaintFrameId(int measureIteration, long qpc, bool logMutation)
    {
        FrameIdProbe.Encode(measureIteration, out byte r, out byte g, out byte b);
        _probeBrush.Color = Windows.UI.Color.FromArgb(255, r, g, b);
        if (logMutation) _mutationLog.Write(measureIteration, qpc, r, g, b);
    }

    private FrameworkElement BuildScenario() => _options.Scenario switch
    {
        BenchScenarios.Startup => BuildStartup(),
        BenchScenarios.Buttons225 => BuildButtons(),
        BenchScenarios.Text1125 => BuildTextCanvas(),
        BenchScenarios.VirtualScroll1K or BenchScenarios.VirtualScroll10K => BuildVirtualList(),
        BenchScenarios.LocalizedTransform => BuildLocalizedTree(transform: true),
        BenchScenarios.LocalizedText => BuildLocalizedTree(transform: false),
        BenchScenarios.TreeChurn => BuildChurnHost(),
        BenchScenarios.PageNavigation => BuildNavigationHost(),
        _ => throw new InvalidOperationException(_options.Scenario),
    };

    private static FrameworkElement BuildStartup() => new Grid
    {
        Width = BenchWorkload.WindowWidth,
        Height = BenchWorkload.WindowHeight,
        Children = { new TextBlock { Text = "WinUI 3 framework benchmark", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } },
    };

    private static FrameworkElement BuildButtons()
    {
        var grid = new Grid { Width = BenchWorkload.WindowWidth, Height = BenchWorkload.WindowHeight, Padding = new Thickness(8) };
        for (int i = 0; i < BenchWorkload.ButtonColumns; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < BenchWorkload.ButtonRows; i++) grid.RowDefinitions.Add(new RowDefinition());
        int n = 0;
        for (int y = 0; y < BenchWorkload.ButtonRows; y++)
        for (int x = 0; x < BenchWorkload.ButtonColumns; x++, n++)
        {
            var button = new Button
            {
                Content = $"Button {n + 1}",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(2),
            };
            Grid.SetColumn(button, x);
            Grid.SetRow(button, y);
            grid.Children.Add(button);
        }
        return grid;
    }

    private static FrameworkElement BuildTextCanvas()
    {
        var canvas = new Canvas { Width = BenchWorkload.WindowWidth, Height = BenchWorkload.WindowHeight };
        int n = 0;
        for (int y = 0; y < BenchWorkload.TextRows; y++)
        for (int x = 0; x < BenchWorkload.TextColumns; x++, n++)
        {
            var text = new TextBlock { Text = $"T{n:0000}", FontSize = 11 };
            Canvas.SetLeft(text, x * 47d);
            Canvas.SetTop(text, y * 15.5d);
            canvas.Children.Add(text);
        }
        return canvas;
    }

    private FrameworkElement BuildVirtualList()
    {
        int rows = BenchWorkload.RowsFor(_options.Scenario);
        _scrollLastRow = rows - 1;
        _scrollResetPeriod = BenchWorkload.ScrollResetPeriodFor(_options.Scenario);
        for (int i = 0; i < rows; i++) _items.Add(new BenchRowItem($"Row {i:00000}"));
        _list = new ListView
        {
            Width = BenchWorkload.WindowWidth,
            Height = BenchWorkload.WindowHeight,
            ItemsSource = _items,
            SelectionMode = ListViewSelectionMode.None,
            IsItemClickEnabled = false,
            ItemTemplate = Resource<DataTemplate>("BenchRowTemplate"),
            ItemContainerStyle = Resource<Style>("BenchListViewItemStyle"),
        };
        return _list;
    }

    /// <summary>
    /// Reads a XAML resource. A plain cast is not enough under NativeAOT: values read out of the WinRT resource map
    /// come back as the base <c>DependencyObject</c> RCW - the concrete runtime class is not recovered - so
    /// <c>(DataTemplate)Application.Current.Resources[key]</c> throws <see cref="InvalidCastException"/> inside the
    /// XAML callback, which surfaces as the stowed-exception exit code 0xC000027B. QueryInterface for the projected
    /// type instead.
    /// </summary>
    private static T Resource<T>(string key) where T : class
        => Project<T>(Application.Current.Resources[key])
           ?? throw new InvalidOperationException($"Resource '{key}' is not a {typeof(T).Name}.");

    /// <summary>QueryInterface-based "as": see <see cref="Resource{T}"/> for why a C# cast is not sufficient.</summary>
    private static T? Project<T>(object? value) where T : class
    {
        if (value is null) return null;
        if (value is T typed) return typed;
        try { return WinRT.CastExtensions.As<T>(value); }
        catch (InvalidCastException) { return null; }
        catch (System.Runtime.InteropServices.COMException) { return null; }
    }

    private FrameworkElement BuildLocalizedTree(bool transform)
    {
        var canvas = new Canvas { Width = BenchWorkload.WindowWidth, Height = BenchWorkload.WindowHeight };
        int target = (BenchWorkload.LocalizedNodeCount - 1) / 2;
        for (int i = 0; i < BenchWorkload.LocalizedNodeCount - 1; i++)
        {
            FrameworkElement node;
            if (i == target && !transform)
            {
                _localizedText = new TextBlock { Text = "value 0000", Width = 78, Height = 18, FontSize = 11 };
                node = _localizedText;
            }
            else
            {
                var border = new Border
                {
                    Width = 18,
                    Height = 18,
                    Background = new SolidColorBrush(i == target ? Windows.UI.Color.FromArgb(255, 0, 120, 212) :
                        Windows.UI.Color.FromArgb(255, (byte)(40 + i % 30), (byte)(40 + i % 30), (byte)(40 + i % 30))),
                };
                if (i == target && transform)
                {
                    // A RenderTransform goes through the XAML property system and the render walk, which is the analog
                    // of FluentGPU's Transform prop through its dirty tracking. Setting a hand-obtained composition
                    // Visual.Offset instead would bypass XAML entirely and measure the compositor, not the framework.
                    _localizedTranslate = new TranslateTransform();
                    border.RenderTransform = _localizedTranslate;
                }
                node = border;
            }
            Canvas.SetLeft(node, (i % 55) * 21d);
            Canvas.SetTop(node, (i / 55) * 21d);
            canvas.Children.Add(node);
        }
        return canvas;
    }

    private FrameworkElement BuildChurnHost()
    {
        _churnA = BuildChurnTree(Windows.UI.Color.FromArgb(255, 32, 120, 212));
        _churnB = BuildChurnTree(Windows.UI.Color.FromArgb(255, 16, 124, 16));
        _churnHost = new ContentControl
        {
            Width = BenchWorkload.WindowWidth,
            Height = BenchWorkload.WindowHeight,
            Content = _churnA,
        };
        return _churnHost;
    }

    private static UIElement BuildChurnTree(Windows.UI.Color color)
    {
        var canvas = new Canvas();
        var brush = new SolidColorBrush(color);
        for (int i = 0; i < BenchWorkload.ChurnSubtreeNodes - 1; i++)
        {
            var border = new Border { Width = 24, Height = 24, Background = brush, Opacity = 0.5 + (i % 5) * 0.1 };
            Canvas.SetLeft(border, (i % 40) * 27d);
            Canvas.SetTop(border, (i / 40) * 27d);
            canvas.Children.Add(border);
        }
        return canvas;
    }

    private void Mutate(int iteration)
    {
        switch (_options.Scenario)
        {
            case BenchScenarios.VirtualScroll1K:
            case BenchScenarios.VirtualScroll10K:
                // Scenario-validity instrumentation: sample the offset the PREVIOUS ChangeView actually produced (the
                // call is asynchronous, so reading it straight afterwards would read the stale value). A run whose
                // offset never moves, or moves once and then pins, is not a scroll benchmark no matter what it times.
                double observed = _listScrollViewer!.VerticalOffset;
                if (observed != _lastOffset)
                {
                    _offsetChanges++;
                    if (observed < _lastOffset) _offsetResets++;
                    _lastOffset = observed;
                    if (observed > _offsetMax) _offsetMax = observed;
                }
                _scrollIndex = iteration > 0 && iteration % _scrollResetPeriod == 0
                    ? 0
                    : Math.Min(_scrollLastRow, _scrollIndex + BenchWorkload.VirtualScrollRowsPerOperation);
                // ScrollIntoView on every raw-pass mutation would force a WinUI selection/container transition on top
                // of the scroll. Drive the ListView's realized viewport directly instead: the list stays virtualized
                // and the workload stays five rows per operation, matching the FluentGPU side.
                if (!_listScrollViewer.ChangeView(
                        null, _scrollIndex * (double)BenchWorkload.VirtualRowHeight, null, disableAnimation: true))
                    _changeViewRejected++;
                break;
            case BenchScenarios.LocalizedTransform:
                _localizedTranslate!.X = (iteration & 1) == 0 ? 12d : 0d;
                break;
            case BenchScenarios.LocalizedText:
                _localizedText!.Text = $"value {iteration:0000}";
                break;
            case BenchScenarios.TreeChurn:
                _churnHost!.Content = (iteration & 1) == 0 ? _churnA : _churnB;
                break;
            case BenchScenarios.PageNavigation:
                // A navigation, not a cached-tree swap: the destination page is CONSTRUCTED here, inside the timed
                // section, and the outgoing page is dropped. See NavigationPages.cs for why this host does not use a
                // Frame (page cache + navigation transition).
                _navHost!.Content = BuildNavigationPage(iteration);
                break;
        }
    }

    private static double TicksToMs(long ticks) => ticks * 1000d / Stopwatch.Frequency;

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (Project<ScrollViewer>(root) is { } viewer) return viewer;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
}

/// <summary>Strongly typed row item for the compiled ListView template; runtime <c>{Binding}</c> is not AOT-safe.</summary>
public sealed record BenchRowItem(string Label);
