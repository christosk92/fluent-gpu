using Bench.Contracts;
using Microsoft.UI.Xaml;

namespace WinUIBench;

public partial class App : Application
{
    private BenchmarkWindow? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        BenchOptions options = BenchOptions.Parse(Environment.GetCommandLineArgs()[1..]);
        BenchTrace.Log.ProcessReady("WinUI 3", options.Scenario, options.RunId);
        _window = new BenchmarkWindow(options);
        _window.Activate();
    }
}
