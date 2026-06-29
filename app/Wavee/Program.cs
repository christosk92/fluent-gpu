using System.IO;
using FluentGpu;             // FluentApp
using FluentGpu.Dsl;         // Theme (startup theme seed)
using FluentGpu.Foundation;  // Diag
using FluentGpu.Localization; // Loc tables (assets/loc)

namespace Wavee;

// Wavee Music — composition root. Installs observability + the global crash net BEFORE the window comes up, then runs
// the engine. FluentApp.Run (FluentGpu.Windows/Hosting/FluentApp.cs) brings up the DPI-aware window, the D3D12 device,
// Mica + the real OS accent, DirectWrite, and the full image pipeline + smooth scroll.
static class Program
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static extern bool AttachConsole(int dwProcessId);

    // A WinExe (GUI subsystem) gets NO console on a bare terminal launch, so headless-CLI output prints to nowhere. Attach
    // the PARENT terminal's console + re-point Console.Out/Error at it, so --spotify-metadata / --spotify-login output —
    // including the interactive device-code prompt — is visible. No-op (returns false) on a normal GUI launch.
    static void AttachParentConsole()
    {
        if (!AttachConsole(-1)) return;   // -1 = ATTACH_PARENT_PROCESS
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
    }

    // [STAThread]: the GUI thread must be an STA apartment — file pickers / SMTC / taskbar are STA-only coclasses.
    [STAThread]
    static void Main(string[] args)
    {
        // CLI flags below print to the console; a WinExe has none on a bare terminal launch, so attach the parent's first
        // (otherwise a --spotify-* run — incl. the device-code prompt — runs invisibly and looks "stuck").
        if (args.Length > 0 && OperatingSystem.IsWindows()) AttachParentConsole();

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

        // Headless backend-engine self-test (no window): exercises the five backend engines and exits 0 (pass) / 1 (fail).
        if (Array.IndexOf(args, "--backend-selftest") >= 0)
        {
            int code = Wavee.Backend.BackendSelfTest.Run(Console.Error.WriteLine);
            Environment.Exit(code);
        }

        // QR diagnostic: encode [text] with the REAL Qr encoder → ASCII + a crisp PNG (isolates the encoder from the GUI
        // renderer). Usage: --qr-dump [text] [outpath.png]
        int qrIdx = Array.IndexOf(args, "--qr-dump");
        if (qrIdx >= 0)
        {
            string qtext = qrIdx + 1 < args.Length && !args[qrIdx + 1].StartsWith("--") ? args[qrIdx + 1] : "https://spotify.com/pair";
            string qpath = qrIdx + 2 < args.Length && !args[qrIdx + 2].StartsWith("--") ? args[qrIdx + 2] : "qr.png";
            Environment.Exit(QrDump.Run(qtext, qpath, Console.Error.WriteLine));
        }

        // Headless LIVE Spotify login (real network): OAuth device-code → AP handshake + login → APWelcome.
        if (Array.IndexOf(args, "--spotify-login") >= 0)
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(8));
            int code = Wavee.SpotifyLive.SpotifyLiveLogin.RunAsync(Console.Error.WriteLine, cts.Token).GetAwaiter().GetResult();
            Environment.Exit(code);
        }

        // LIVE metadata round-trip: login -> login5 -> client-token -> spclient extended-metadata for one URI -> print.
        // Usage: --spotify-metadata [spotify:track:... | spotify:album:... | spotify:artist:...] (defaults to a known track).
        int metaIdx = Array.IndexOf(args, "--spotify-metadata");
        if (metaIdx >= 0)
        {
            string uri = metaIdx + 1 < args.Length && !args[metaIdx + 1].StartsWith("--")
                ? args[metaIdx + 1]
                : "spotify:track:4uLU6hMCjMI75M1A2tKUQC";
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(8));
            int code = Wavee.SpotifyLive.SpotifyMetadataProbe.RunAsync(uri, Console.Error.WriteLine, cts.Token).GetAwaiter().GetResult();
            Environment.Exit(code);
        }

        // LIVE playlist membership round-trip: login -> GET /playlist/v2 -> thin header + ordered membership -> hydrate -> print.
        // Usage: --spotify-playlist spotify:playlist:<id>
        int plIdx = Array.IndexOf(args, "--spotify-playlist");
        if (plIdx >= 0)
        {
            string uri = plIdx + 1 < args.Length && !args[plIdx + 1].StartsWith("--") ? args[plIdx + 1] : "";
            if (uri.Length == 0) { Console.Error.WriteLine("usage: --spotify-playlist spotify:playlist:<id>"); Environment.Exit(2); }
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(8));
            int code = Wavee.SpotifyLive.SpotifyLibraryProbe.RunPlaylistAsync(uri, Console.Error.WriteLine, cts.Token).GetAwaiter().GetResult();
            Environment.Exit(code);
        }

        // LIVE rootlist round-trip: login -> GET /playlist/v2/user/{me}/rootlist -> the folder/playlist tree -> print.
        if (Array.IndexOf(args, "--spotify-rootlist") >= 0)
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(8));
            int code = Wavee.SpotifyLive.SpotifyLibraryProbe.RunRootlistAsync(Console.Error.WriteLine, cts.Token).GetAwaiter().GetResult();
            Environment.Exit(code);
        }

        // LIVE collection round-trip: login -> POST /collection/v2/{paging|delta} -> set membership -> hydrate -> print.
        // Usage: --spotify-collection [liked|albums|artists|shows|episodes] (defaults to liked).
        int colIdx = Array.IndexOf(args, "--spotify-collection");
        if (colIdx >= 0)
        {
            string setId = colIdx + 1 < args.Length && !args[colIdx + 1].StartsWith("--") ? args[colIdx + 1] : "liked";
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(8));
            int code = Wavee.SpotifyLive.SpotifyLibraryProbe.RunCollectionAsync(setId, Console.Error.WriteLine, cts.Token).GetAwaiter().GetResult();
            Environment.Exit(code);
        }

        // LIVE full library sync into the REAL persistent store (rootlist + all collections + hydrate + the dealer firehose).
        // After this, `--real-backend` reads the library offline from disk. Usage: --spotify-sync
        if (Array.IndexOf(args, "--spotify-sync") >= 0)
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(10));
            int code = Wavee.SpotifyLive.SpotifyLibrarySync.RunAsync(Console.Error.WriteLine, cts.Token).GetAwaiter().GetResult();
            Environment.Exit(code);
        }

        // LIVE Connect session bring-up demo: login -> dealer + AP channel -> swap the live playback backend into a REAL
        // Services (svc.GoLive) and log the now-playing the UI bridge sees through the switchable. Usage: --connect-live
        if (Array.IndexOf(args, "--connect-live") >= 0)
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(3));
            int code = Wavee.SpotifyLive.LiveSessionHost.RunAsync(Console.Error.WriteLine, cts.Token).GetAwaiter().GetResult();
            Environment.Exit(code);
        }

        // Seed the theme BEFORE the window comes up (no startup flash): honor the persisted preference, falling back to
        // the live OS theme for a fresh install (mode == System). FluentApp.Run then applies the matching Mica material
        // and the in-app surfaces mount with the right tokens; the store is reused by the app so there's one instance.
        var settings = AppDataSettings.ForUnpackaged("Wavee", "Wavee");
        int themeMode = settings.Get(WaveeSettings.ThemeMode);
        Theme.Dark = themeMode switch { 1 => false, 2 => true, _ => !FluentApp.SystemUsesLightTheme() };

        // ── Localization: load the bundled culture tables (assets/loc/*.json, copied next to the exe) before the first
        // frame, so every Loc.Get(Strings.*) resolves. en-US is the base + terminal fallback; more cultures drop in later.
        Localization.DefaultCulture = "en-US";
        Localization.LoadFolder(Path.Combine(AppContext.BaseDirectory, "assets", "loc"));

        // Real (live Spotify) backend is the DEFAULT: the persistent Store-backed catalog + durable mutations, hydrated by
        // the live session (login → spclient fetchers → the hm:// dealer) that the login takeover starts on launch. Pass
        // --fake for the offline FakeData demo (populated UI with no login/network — used by --screenshot and UI iteration).
        Services.UseRealBackend = Array.IndexOf(args, "--fake") < 0;

        // Premium-only gate: Wavee requires a Spotify Premium account for now. A Free account is refused OUTRIGHT — we do
        // NOT bring up the window; we show a nice warning and exit. (No real login yet, so this defaults to Premium; pass
        // --free or set WAVEE_FORCE_FREE=1 to exercise the refusal.)
        if (!Wavee.Backend.SessionGate.IsAllowed(ResolveAccountTier(args)))
        {
            WaveeLog.Instance.Info("auth", "Refusing to launch on a Spotify Free account (Wavee requires Premium).");
            PremiumGate.ShowWarning();
            return;
        }

        try
        {
            // Diagnostic harness chain (each gated by its own env flag; all return false in a normal run): the nav/scroll
            // FPS stress probe (WAVEE_NAV_PROBE) first, then the resize probe (WAVEE_RESIZE_PROBE).
            FluentApp.DiagnosticRun = (h, w, d) => WaveeNavProbe.TryRun(h, w, d) || WaveeResizeProbe.TryRun(h, w, d);
            // customFrame:true → the in-app TitleBar (WaveeShell) draws the Mica-extended caption buttons + drag region.
            // micaAlt:true → Mica BaseAlt (the flatter File-Explorer tint), matching WaveeMusic's MicaBackdrop Kind="BaseAlt".
            // ambientFps: pace PERPETUAL ambient motion (the always-playing seek playhead, the now-playing equalizer,
            // skeleton shimmer, buffering spinner) to 30 Hz instead of the panel's full refresh. Wavee auto-starts
            // playback, so the seek bar's per-frame playhead ticker would otherwise free-run the whole record+present
            // pipeline at 120 Hz forever for a bar that moves ~3 px/s. Latency-sensitive input (scroll/hover/drag) is
            // exempt and still runs at the display rate; FG_ANIM_FPS overrides this for diagnostics.
            FluentApp.Run(() => new WaveeApp(settings), "Wavee Music", 1180, 760,
                          frames: frames, screenshot: screenshot, customFrame: true, micaAlt: true, ambientFps: 30);
        }
        catch (Exception ex)
        {
            WaveeLog.Instance.Critical("app", "Fatal error in the app loop", ex);
            throw;
        }
        WaveeLog.Instance.Info("app", "Wavee exiting");
    }

    // For now there is no real login, so the account tier defaults to Premium (the app launches normally). The refusal
    // path is exercisable via --free or WAVEE_FORCE_FREE=1, and wires to the real session tier when login lands.
    static Wavee.Backend.Tier ResolveAccountTier(string[] args)
    {
        if (Array.IndexOf(args, "--free") >= 0) return Wavee.Backend.Tier.Free;
        if (Environment.GetEnvironmentVariable("WAVEE_FORCE_FREE") == "1") return Wavee.Backend.Tier.Free;
        return Wavee.Backend.Tier.Premium;
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
