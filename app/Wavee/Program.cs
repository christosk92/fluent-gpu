using System.IO;
using FluentGpu;             // FluentApp
using FluentGpu.Dsl;         // Theme (startup theme seed)
using FluentGpu.Foundation;  // Diag

namespace Wavee;

// Wavee Music — composition root. Installs observability + the global crash net BEFORE the window comes up, then runs
// the engine. FluentApp.Run (FluentGpu.Windows/Hosting/FluentApp.cs) brings up the DPI-aware window, the D3D12 device,
// Mica + the real OS accent, DirectWrite, and the full image pipeline + smooth scroll.
static class Program
{
    // [STAThread]: the GUI thread must be an STA apartment — file pickers / SMTC / taskbar are STA-only coclasses.
    [STAThread]
    static void Main(string[] args)
    {
        // ── Observability ───────────────────────────────────────────────────────────────────────────────────────────
        string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wavee", "logs");
        WaveeLog.Instance.Configure(crashLogPath: Path.Combine(logDir, "wavee.log"), echo: DebugEcho());
        Diag.Sink = WaveeLog.DiagSink;                 // fold engine diagnostics (FG_DIAG) into the app log stream
        WaveeLog.Instance.Info("app", "Wavee starting");

        // ── Global crash net (the two process-level handlers; the UI-thread one lives in the engine loop) ─────────────
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WaveeLog.Instance.Critical("crash", $"Unhandled exception (terminating={e.IsTerminating})", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WaveeLog.Instance.Error("crash", "Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        int frames = -1;
        string? screenshot = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--frames" && i + 1 < args.Length && int.TryParse(args[i + 1], out int f)) frames = f;
            if (args[i] == "--screenshot" && i + 1 < args.Length) screenshot = args[i + 1];
        }

        // Seed the theme BEFORE the window comes up (no startup flash): honor the persisted preference, falling back to
        // the live OS theme for a fresh install (mode == System). FluentApp.Run then applies the matching Mica material
        // and the in-app surfaces mount with the right tokens; the store is reused by the app so there's one instance.
        var settings = AppDataSettings.ForUnpackaged("Wavee", "Wavee");
        int themeMode = settings.Get(WaveeSettings.ThemeMode);
        Theme.Dark = themeMode switch { 1 => false, 2 => true, _ => !FluentApp.SystemUsesLightTheme() };

        try
        {
            FluentApp.DiagnosticRun = WaveeResizeProbe.TryRun;
            // customFrame:true → the in-app TitleBar (WaveeShell) draws the Mica-extended caption buttons + drag region.
            // micaAlt:true → Mica BaseAlt (the flatter File-Explorer tint), matching WaveeMusic's MicaBackdrop Kind="BaseAlt".
            FluentApp.Run(() => new WaveeApp(settings), "Wavee Music", 1180, 760,
                          frames: frames, screenshot: screenshot, customFrame: true, micaAlt: true);
        }
        catch (Exception ex)
        {
            WaveeLog.Instance.Critical("app", "Fatal error in the app loop", ex);
            throw;
        }
        WaveeLog.Instance.Info("app", "Wavee exiting");
    }

    static Action<string>? DebugEcho()
    {
#if DEBUG
        return Console.Error.WriteLine;
#else
        return null;
#endif
    }
}
