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
    static WaveeLogger CliLog(string category) => new(WaveeLog.Instance, category);
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
        // Settings are hoisted above Configure so the persisted log-level overrides seed it (env still wins inside
        // Configure). One instance, reused for theme/backend below.
        var settings = AppDataSettings.ForUnpackaged("Wavee", "Wavee");
        // Launch-scoped by design: the Settings picker persists a new value, and the next process applies it atomically
        // to UI strings, Spotify metadata requests, and locale-partitioned caches.
        AppLocale appLocale = AppLocaleBootstrap.Initialize(settings);
        string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wavee", "logs");
        string logPath = Path.Combine(logDir, "wavee.log");
#if DEBUG
        // Dev default: the in-memory ring keeps full Debug detail for the in-app viewer, but the FILE defaults to Info so a
        // dev run doesn't bloat wavee.log with the demoted verbose flow. The settings-backed level UI (or WAVEE_LOG_FILE_LEVEL)
        // can raise the file level to Debug/Trace on demand.
        WaveeLogLevel defaultLevel = WaveeLogLevel.Debug;
        WaveeLogLevel defaultFileLevel = WaveeLogLevel.Info;
#else
        WaveeLogLevel defaultLevel = WaveeLogLevel.Info;
        WaveeLogLevel defaultFileLevel = WaveeLogLevel.Info;
#endif
        int minSetting = settings.Get(WaveeSettings.LogMinLevel);
        int fileSetting = settings.Get(WaveeSettings.LogFileMinLevel);
        // dailyRolling: the main app log splits into wavee-yyyyMMdd.log per calendar day (the WaveeMusic scheme) —
        // the old single ever-growing wavee.log is migrated into the dated set on first launch.
        WaveeLog.Instance.Configure(crashLogPath: logPath, echo: DebugEcho(),
            minLevel: minSetting >= 0 ? (WaveeLogLevel)minSetting : defaultLevel,
            fileMinLevel: fileSetting >= 0 ? (WaveeLogLevel)fileSetting : defaultFileLevel,
            dailyRolling: true);
        Diag.Sink = WaveeLog.DiagSink;                 // fold engine diagnostics (FG_DIAG) into the app log stream
        WaveeLog.Instance.Info("app", "startup", "Wavee starting",
            WaveeLogField.Of("pid", Environment.ProcessId),
            WaveeLogField.Of("args", args.Length),
            WaveeLogField.Of("log", logPath),
            WaveeLogField.Of("framework", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription),
            WaveeLogField.Of("os", System.Runtime.InteropServices.RuntimeInformation.OSDescription));

        // ── Global crash net (the two process-level handlers; the UI-thread one lives in the engine loop) ─────────────
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            WaveeLog.Instance.Critical("crash", $"Unhandled exception (terminating={e.IsTerminating})", e.ExceptionObject as Exception);
            WaveeLog.Instance.Flush();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WaveeLog.Instance.Error("crash", "Unobserved task exception", e.Exception);
            WaveeLog.Instance.Flush();
            e.SetObserved();
        };

        if (Array.IndexOf(args, "--perf-bench") >= 0)
            Environment.SetEnvironmentVariable("WAVEE_PERF_BENCH", "1");

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
            int code = Wavee.Backend.BackendSelfTest.Run(CliLog("probe"));
            Environment.Exit(code);
        }

        // QR diagnostic: encode [text] with the REAL Qr encoder → ASCII + a crisp PNG (isolates the encoder from the GUI
        // renderer). Usage: --qr-dump [text] [outpath.png]
        int qrIdx = Array.IndexOf(args, "--qr-dump");
        if (qrIdx >= 0)
        {
            string qtext = qrIdx + 1 < args.Length && !args[qrIdx + 1].StartsWith("--") ? args[qrIdx + 1] : "https://spotify.com/pair";
            string qpath = qrIdx + 2 < args.Length && !args[qrIdx + 2].StartsWith("--") ? args[qrIdx + 2] : "qr.png";
            Environment.Exit(QrDump.Run(qtext, qpath, CliLog("probe")));
        }

        // Headless LIVE Spotify login (real network): OAuth device-code → AP handshake + login → APWelcome.
        if (Array.IndexOf(args, "--spotify-login") >= 0)
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(8));
            int code = Wavee.SpotifyLive.SpotifyLiveLogin.RunAsync(CliLog("auth"), cts.Token).GetAwaiter().GetResult();
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
            int code = Wavee.SpotifyLive.SpotifyMetadataProbe.RunAsync(uri, CliLog("probe"), cts.Token, appLocale.SpotifyLanguage).GetAwaiter().GetResult();
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
            int code = Wavee.SpotifyLive.SpotifyLibraryProbe.RunPlaylistAsync(uri, CliLog("probe"), cts.Token, appLocale.SpotifyLanguage).GetAwaiter().GetResult();
            Environment.Exit(code);
        }

        // LIVE rootlist round-trip: login -> GET /playlist/v2/user/{me}/rootlist -> the folder/playlist tree -> print.
        if (Array.IndexOf(args, "--spotify-rootlist") >= 0)
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(8));
            int code = Wavee.SpotifyLive.SpotifyLibraryProbe.RunRootlistAsync(CliLog("probe"), cts.Token, appLocale.SpotifyLanguage).GetAwaiter().GetResult();
            Environment.Exit(code);
        }

        // LIVE collection round-trip: login -> POST /collection/v2/{paging|delta} -> set membership -> hydrate -> print.
        // Usage: --spotify-collection [liked|albums|artists|shows|episodes] (defaults to liked).
        int colIdx = Array.IndexOf(args, "--spotify-collection");
        if (colIdx >= 0)
        {
            string setId = colIdx + 1 < args.Length && !args[colIdx + 1].StartsWith("--") ? args[colIdx + 1] : "liked";
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(8));
            int code = Wavee.SpotifyLive.SpotifyLibraryProbe.RunCollectionAsync(setId, CliLog("probe"), cts.Token, appLocale.SpotifyLanguage).GetAwaiter().GetResult();
            Environment.Exit(code);
        }

        // LIVE full library sync into the REAL persistent store (rootlist + all collections + hydrate + the dealer firehose).
        // After this, `--real-backend` reads the library offline from disk. Usage: --spotify-sync
        if (Array.IndexOf(args, "--spotify-sync") >= 0)
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(10));
            int code = Wavee.SpotifyLive.SpotifyLibrarySync.RunAsync(CliLog("sync"), cts.Token, appLocale.SpotifyLanguage).GetAwaiter().GetResult();
            Environment.Exit(code);
        }

#if WAVEE_PLAYPLAY_LOCAL
        if (Array.IndexOf(args, "--playplay-runtime-status") >= 0)
        {
            int code = Wavee.SpotifyLive.PlayPlayRuntimeProbe.RunStatus(args, CliLog("audio"));
            Environment.Exit(code);
        }

        int regIdx = Array.IndexOf(args, "--playplay-runtime-register");
        if (regIdx >= 0)
        {
            string dir = regIdx + 1 < args.Length && !args[regIdx + 1].StartsWith("--") ? args[regIdx + 1] : "";
            if (dir.Length == 0) { Console.Error.WriteLine("usage: --playplay-runtime-register <dir>"); Environment.Exit(2); }
            int code = Wavee.SpotifyLive.PlayPlayRuntimeProbe.RunRegister(dir, CliLog("audio"));
            Environment.Exit(code);
        }

        if (Array.IndexOf(args, "--playplay-runtime-check") >= 0)
        {
            int code = Wavee.SpotifyLive.PlayPlayRuntimeProbe.RunCheck(args, CliLog("audio"));
            Environment.Exit(code);
        }
#endif

        // LIVE Connect session bring-up demo: login -> dealer + AP channel -> swap the live playback backend into a REAL
        // Services (svc.GoLive) and log the now-playing the UI bridge sees through the switchable. Usage: --connect-live
        if (Array.IndexOf(args, "--connect-live") >= 0)
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(3));
            int code = Wavee.SpotifyLive.LiveSessionHost.RunAsync(CliLog("connect"), cts.Token, appLocale.SpotifyLanguage).GetAwaiter().GetResult();
            Environment.Exit(code);
        }

        if (StartupPreflight.TryGetBlockingIssue(out var startupIssueTitle, out var startupIssueBody))
        {
            WaveeLog.Instance.Critical("app", "startup preflight failed: " + startupIssueBody);
            StartupNotice.Error(startupIssueTitle, startupIssueBody + Environment.NewLine + Environment.NewLine + "Log: " + logPath);
            return;
        }

        // Seed the theme BEFORE the window comes up (no startup flash): honor the persisted preference, falling back to
        // the live OS theme for a fresh install (mode == System). FluentApp.Run then applies the matching Mica material
        // and the in-app surfaces mount with the right tokens; the store is reused by the app so there's one instance.
        CrashDumpProbe.LogPendingCrashDump(settings, WaveeLog.Instance);
        int themeMode = settings.Get(WaveeSettings.ThemeMode);
        var themeKind = themeMode switch { 1 => ThemeKind.Light, 2 => ThemeKind.Dark, _ => FluentApp.SystemUsesLightTheme() ? ThemeKind.Light : ThemeKind.Dark };
        Tok.Use(WaveeTheme.ResolvePalette(settings.Get(WaveeSettings.PaletteId)), themeKind);

        // ── Localization: load the bundled culture tables (assets/loc/*.json, copied next to the exe) before the first
        // frame, so every Loc.Get(Strings.*) resolves. en-US is the base + terminal fallback; more cultures drop in later.
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
            // FPS stress probe (WAVEE_NAV_PROBE) first, then the resize probe (WAVEE_RESIZE_PROBE). DiagnosticRun is invoked
            // ONCE per launch (before the loop) regardless of flags, so it doubles as the app's hook to compose the
            // entity-store census (Services.MemCensusHook) into the engine's FG_MEM_DIAG [memcensus] GpuDetail line — the
            // only app-reachable point that holds the AppHost. The hook builds its string lazily (at census cadence).
            FluentApp.DiagnosticRun = (h, w, d) =>
            {
                if (d is FluentGpu.Rhi.D3D12.D3D12Device gpuDev)
                    h.GpuDetail = () =>
                    {
                        string gpu = gpuDev.DiagGpuDetail;
                        string app = Services.MemCensusHook?.Invoke() ?? "";
                        return app.Length == 0 ? gpu : gpu.Length == 0 ? app : gpu + " | " + app;
                    };
                return WaveePerfBench.TryRun(h, w, d) || WaveeNavProbe.TryRun(h, w, d) || WaveeResizeProbe.TryRun(h, w, d) || WaveeMemSoak.TryRun(h, w, d);
            };
            // customFrame:true → the in-app TitleBar (WaveeShell) draws the Mica-extended caption buttons + drag region.
            // micaAlt:true → Mica BaseAlt (the flatter File-Explorer tint), matching WaveeMusic's MicaBackdrop Kind="BaseAlt".
            // ambientFps: pace PERPETUAL ambient motion (the always-playing seek playhead, the now-playing equalizer,
            // skeleton shimmer, buffering spinner, the karaoke lyrics wipe) to 60 Hz instead of the panel's full refresh.
            // Wavee auto-starts playback, so the seek bar's per-frame playhead ticker would otherwise free-run the whole
            // record+present pipeline at the full refresh forever for a bar that moves ~3 px/s. 60 (raised from 30) now
            // that the per-frame cost is cheap (rect-bounded + linear-sampled blur + back-buffer-direct layers) — the
            // lyrics wipe/scroll read smooth without quadrupling the idle pipeline. Latency-sensitive input
            // (scroll/hover/drag) is exempt and always runs at the display rate; FG_ANIM_FPS overrides this (=30 to
            // revert to the old cadence, =0 for uncapped / full display rate).
            FluentAppHarness.Run(() => new WaveeApp(settings, appLocale),
                new AppOptions { Title = "Wavee Music", Width = 1180, Height = 760, CustomFrame = true, MicaAlt = true, AmbientFps = 60 },
                new HarnessOptions { Frames = frames, Screenshot = screenshot });
        }
        catch (Exception ex)
        {
            WaveeLog.Instance.Critical("app", "Fatal error in the app loop", ex);
            WaveeLog.Instance.Flush();
            string reportPath = "";
            try { reportPath = CrashReport.Write(ex, WaveeLog.Instance.FilePath); }
            catch { }
            try
            {
                var body = "Wavee crashed.\n\n"
                         + (reportPath.Length > 0 ? "Crash report: " + reportPath + "\n" : "")
                         + (WaveeLog.Instance.FilePath is { Length: > 0 } lp ? "Log: " + lp + "\n" : "")
                         + "\nOpen the report folder now?";
                if (StartupNotice.ErrorYesNo("Oops! Wavee crashed", body) && reportPath.Length > 0)
                    ShellOpen.OpenFolderOf(reportPath);
            }
            catch { }
            throw;
        }
        WaveeLog.Instance.Info("app", "Wavee exiting");
        WaveeLog.Instance.Flush();
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
        return Console.Out.WriteLine;
#else
        return null;
#endif
    }
}
