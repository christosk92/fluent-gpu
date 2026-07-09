using System;
using FluentGpu;          // FluentApp (OS theme facade + SystemColorsChanged relay)
using FluentGpu.Dsl;
using FluentGpu.Foundation;   // Diag.CompiledIn (debug-build gate for the FPS HUD)
using FluentGpu.Hooks;
using Wavee.Core;             // AuthStatus / LoginSnapshot / LoginPhase (the login gate + takeover)

namespace Wavee;

// The app root. Owns Services, provides the Services + PlaybackBridge contexts, wires the Core→Signal bridge on mount
// (and starts a fake session + playback so the shell is live), then renders the shell. The whole app blur-rises in.
sealed class WaveeApp : Component
{
    readonly Services _services;

    internal static PlaybackBridge? ProbePlayback;
    internal static Services? ProbeServices;

    // The composition root passes the settings store created early (so the theme is seeded before the first frame);
    // null in tests falls back to the store Services creates itself.
    public WaveeApp(IAppSettings? settings = null) => _services = Services.UseRealBackend ? Services.CreateReal(settings) : Services.CreateFake(settings);

    public override Element Render()
    {
        var bridge = _services.Playback;
        var libBridge = _services.LibraryBridge;
        var friendsBridge = _services.FriendsBridge;
        var notifications = _services.Notifications;
        var store = _services.LibraryStore;
        if (Diag.EnvFlag("WAVEE_LIVE_LYRICS_SCROLL_PROBE") || Diag.EnvFlag("WAVEE_LYRICS_PROBE") || Diag.EnvFlag("WAVEE_HOME_SCROLL_PROBE") || Diag.EnvFlag("WAVEE_NAV_PROBE") || Diag.EnvFlag("WAVEE_LYRICS_ADVANCE_PROBE"))
        {
            ProbePlayback = bridge;
            ProbeServices = _services;
            // Silence the async lyrics ticker BEFORE it can mount so the advance-probe alone drives OnFrame synchronously
            // (deterministic, timer-decoupling-free). Set here at the root so it is true before the rail/ticker renders.
            if (Diag.EnvFlag("WAVEE_LYRICS_ADVANCE_PROBE")) LyricsView.ProbeSyncMode = true;
        }

        // Follow the OS dark-mode / accent live WHILE the user hasn't pinned an explicit theme (mode == System). The host
        // relays WM_SETTINGCHANGE on the UI thread; we re-read the OS state, apply it, and animate the in-place re-theme.
        var requestTheme = UseContext(ThemeControl.Request);
        Context.UseEffect(() =>
        {
            FluentApp.SystemColorsChanged += () =>
            {
                if (_services.Settings.Get(WaveeSettings.ThemeMode) != 0) return;
                var kind = FluentApp.SystemUsesLightTheme() ? ThemeKind.Light : ThemeKind.Dark;
                Tok.Use(WaveeTheme.ResolvePalette(_services.Settings.Get(WaveeSettings.PaletteId)), kind);
                if (FluentApp.SystemAccent() is { } a) Tok.SetAccent(a);
                requestTheme?.Invoke(250f);
            };
        });

        var post = Context.UsePost();
        var loginSession = UseRef<System.Threading.CancellationTokenSource?>(null);
        var wasAuthed = UseRef(false);   // have we EVER authenticated this run? (fake demo: logout → takeover, but no initial-launch flash)
        var governorTimer = UseRef<System.Threading.Timer?>(null);   // rooted here so the periodic MemoryGovernor poll isn't GC-collected (the app root never unmounts)
        var volumeSaveTimer = UseRef<System.Threading.Timer?>(null); // remember-volume: debounced persist of the slider value

        // ── Simultaneous live login (device code + browser race) ─────────────────────────────────────────────────────
        // The takeover runs BOTH methods at once: RestartCode polls the device code (the two-pane's QR + pairing code), and
        // the "Log in" button fires StartBrowser to race the PKCE loopback alongside it (QUIET — it can't replace the
        // two-pane; it only surfaces success). They share ONE session CTS; the FIRST to GoLive cancels it so the loser
        // bails (the supersede check). The winning host owns an INDEPENDENT CTS, so this cancel never touches its hydration.
        // Everything runs off the UI thread (the login/dealer/AP handshake must not couple to the render loop).
        void RestartCode()
        {
            loginSession.Value?.Cancel();
            var cts = new System.Threading.CancellationTokenSource();
            loginSession.Value = cts;
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var host = await Wavee.SpotifyLive.LiveSessionHost.StartAsync(_services, new WaveeLogger(_services.Log, "connect"), cts.Token, bridge.Progress(post), uiPost: post, interactive: true, useBrowser: false).ConfigureAwait(false);
                    if (host is not null) { post(() => { if (loginSession.Value == cts) loginSession.Value = null; }); cts.Cancel(); }   // success → stop the browser sibling
                }
                catch (OperationCanceledException) { }   // superseded by a newer attempt
                catch (Exception ex)
                {
                    _services.Log.Event(WaveeLogLevel.Warning, "connect", "login.code.failed",
                        "Code login failed", ex: ex, fields: [WaveeLogField.Of("phase", bridge.Login.Peek().Phase.ToString())]);
                    post(() => { if (loginSession.Value == cts) bridge.Login.Value = new LoginSnapshot(LoginPhase.Failed, Error: "Something went wrong signing in."); });
                }
            });
        }

        // The "Log in" button: race the browser-loopback (PKCE) alongside the running device code, on the SAME session.
        void StartBrowser()
        {
            var cts = loginSession.Value;
            if (cts is null || cts.IsCancellationRequested) return;
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var host = await Wavee.SpotifyLive.LiveSessionHost.StartAsync(_services, new WaveeLogger(_services.Log, "connect"), cts.Token, bridge.Progress(post), uiPost: post, interactive: true, useBrowser: true, quietPhases: true).ConfigureAwait(false);
                    if (host is not null) { post(() => { if (loginSession.Value == cts) loginSession.Value = null; }); cts.Cancel(); }
                }
                catch { }   // a browser failure is silent — the device-code two-pane keeps going
            });
        }

        void CloseApp()
        {
            loginSession.Value?.Cancel();
            Environment.Exit(0);   // the takeover is the whole window when logged out → Close quits Wavee
        }

        // The FAKE demo has no real auth: "Log in" just connects the fake session; "Get a new code" re-seeds a demo
        // challenge. This lets the SAME two-pane takeover model the logged-out → logged-in round-trip without a real backend.
        void FakeSignIn() => _ = _services.Session.ConnectAsync();
        void SeedDemoChallenge() => bridge.Login.Value = new LoginSnapshot(LoginPhase.AwaitingApproval,
            new LoginChallenge("WAVE-DEMO", "https://spotify.com/pair", "https://spotify.com/pair?code=WAVEDEMO", DateTimeOffset.UtcNow.AddMinutes(15)));

        Context.UseEffect(() =>
        {
            // Remember-volume: seed the slider before the first frame the user sees; the live session seeds the device
            // announce/local host from the same setting (LiveSessionHost). Saved back below, debounced.
            if (_services.Settings.Get(WaveeSettings.RememberVolume))
                bridge.Volume.Value = Math.Clamp(_services.Settings.Get(WaveeSettings.SavedVolume), 0f, 1f);

            bridge.Activate(post);
            libBridge.Activate(post);
            friendsBridge.Activate(post);
            notifications.Activate(post);
            store.Activate(post);

            // Persist volume changes (local intents AND remote echoes both land on bridge.Volume) with a coarse poll —
            // Peek is a plain field read, and the registry write happens only when the value actually moved.
            volumeSaveTimer.Value ??= new System.Threading.Timer(_ =>
            {
                if (!_services.Settings.Get(WaveeSettings.RememberVolume)) return;
                float v = bridge.Volume.Peek();
                if (Math.Abs(v - _services.Settings.Get(WaveeSettings.SavedVolume)) > 0.004f)
                    _services.Settings.Set(WaveeSettings.SavedVolume, v);
            }, null, dueTime: 2_000, period: 2_000);

            // Drive the MemoryGovernor from a periodic OS-memory-pressure poll. The Timer fires on a background thread but
            // marshals Trim to the UI thread (post) so the UI-thread-affine detail caches shed safely. At rest (no pressure)
            // it sheds nothing — each cache's LRU cap already bounds steady state; under real pressure it sheds further.
            governorTimer.Value ??= new System.Threading.Timer(_ =>
            {
                var info = GC.GetGCMemoryInfo();
                double load = info.HighMemoryLoadThresholdBytes > 0 ? (double)info.MemoryLoadBytes / info.HighMemoryLoadThresholdBytes : 0.0;
                var level = load >= 1.0 ? Wavee.Backend.Residency.MemoryPressure.Critical
                          : load >= 0.85 ? Wavee.Backend.Residency.MemoryPressure.Moderate
                          : Wavee.Backend.Residency.MemoryPressure.Normal;
                post(() => _services.Residency.Trim(level));
            }, null, dueTime: 30_000, period: 30_000);

            if (Diag.EnvFlag("WAVEE_FAKE_CHALLENGE"))
            {
                // Deterministic login screenshots (no network): seed a canned pairing challenge so the takeover renders the
                // marquee hero. The gate below forces the takeover whenever this flag is set.
                bridge.Login.Value = new LoginSnapshot(LoginPhase.AwaitingApproval,
                    new LoginChallenge("WZY5-Q6TX", "https://spotify.com/pair", "https://spotify.com/pair?code=WZY5Q6TX", DateTimeOffset.UtcNow.AddSeconds(872)));
                _services.Log.Info("app", "WAVEE_FAKE_CHALLENGE: seeded a canned pairing challenge for the login takeover");
            }
            else if (Services.UseRealBackend)
            {
                // Real backend: do NOT auto-authenticate the fake. Try a SILENT resume (stored credentials only, no
                // challenge minted); with none on disk the takeover rests on Welcome until the user hits "Continue". On a
                // successful resume the bootstrap swaps the live backend in via Services.GoLive — no UI rebuild.
                _services.Log.Info("app", "Online; the takeover will start the Spotify login (silent resume → two-pane code).");
            }
            else
            {
                // Fake demo: connect the fake session instantly so the INITIAL launch lands on the shell (no takeover flash,
                // --screenshot renders the shell). Playback is NOT auto-started — local playback is unsupported, so a play
                // intent shows the "choose a remote device" toast; the bar rests at "Nothing playing". After a logout the
                // gate shows the demo two-pane instead.
                _ = _services.Session.ConnectAsync();
                _services.Log.Info("app", "Demo backend; fake session started (playback remote-only)");
            }
        });

        // (Re)start the takeover login on every Auth flip to not-authenticated: the real backend kicks the silent-resume →
        // device-code two-pane; the fake demo seeds a demo challenge — but ONLY after it has authenticated once (wasAuthed),
        // so the initial launch goes straight to the shell with no takeover flash. WAVEE_FAKE_CHALLENGE skips this entirely.
        var authState = bridge.Auth.Value;   // subscribe → re-run on the flip
        Context.UseEffect(() =>
        {
            if (Diag.EnvFlag("WAVEE_FAKE_CHALLENGE")) return;
            if (authState == AuthStatus.Authenticated) { wasAuthed.Value = true; return; }
            if (Services.UseRealBackend) RestartCode();
            else if (wasAuthed.Value) SeedDemoChallenge();   // a fake LOGOUT (not the first launch) → the demo two-pane
        }, authState);

        this.UseSoftReveal(); // app entrance (compositor-only, reduced-motion-aware)

        // ── The login GATE ───────────────────────────────────────────────────────────────────────────────────────────
        // The fake demo never shows the takeover (no real auth); the real backend shows it until Authenticated. The coarse
        // bridge.Auth drives the swap (identical for fake + live); the takeover's inner card reads the rich bridge.Login.
        // Providers stay ABOVE the gate so the bridges' subscriptions survive the takeover ↔ shell swap (and back, on logout).
        // WAVEE_FAKE_CHALLENGE forces the takeover (deterministic login screenshots, no network).
        // Authenticated → shell. Logged out → the takeover (real backend always; the fake demo only AFTER its first auth, so
        // the initial demo launch lands on the shell — but a fake LOGOUT now shows the same two-pane, re-signing-in via
        // FakeSignIn). WAVEE_FAKE_CHALLENGE forces the takeover (deterministic login screenshots, no network).
        bool authed = !Diag.EnvFlag("WAVEE_FAKE_CHALLENGE")
                   && (bridge.Auth.Value == AuthStatus.Authenticated || (!Services.UseRealBackend && !wasAuthed.Value));
        Element leaf = authed
            ? Embed.Comp(() => new WaveeShell(_services.Settings))
            : Services.UseRealBackend
                ? Embed.Comp(() => new LoginView(StartBrowser, RestartCode, CloseApp))
                : Embed.Comp(() => new LoginView(FakeSignIn, SeedDemoChallenge, CloseApp));

        var root = Ctx.Provide(Services.Slot, _services,
            Ctx.Provide(PlaybackBridge.Slot, bridge,
            Ctx.Provide(LibraryBridge.Slot, libBridge,
            Ctx.Provide(FriendsBridge.Slot, friendsBridge,
            Ctx.Provide(NotificationCenterBridge.Slot, notifications,
            Ctx.Provide(LibraryStore.Slot, store,
                leaf))))));

        // Debug-build FPS HUD on top (const-folds out of Release; subscribes to the host's per-frame stats). The HUD pill is
        // pinned top-right by a full-bleed PASS-THROUGH positioner (a PLAIN BoxEl — its HitTestPassThrough IS honoured, unlike
        // a component wrapper's mirrored-but-not-passthrough node, which would swallow every hit and silently kill scrolling).
        // ZStack carries Grow=1 to fill the window + stretch the shell exactly like the OverlayHost stack.
        // FPS HUD is OPT-IN now (hidden by default in every build); set WAVEE_FPS=1 to show it.
        if (!Diag.EnvFlag("WAVEE_FPS")) return root;
        var hud = new BoxEl
        {
            Grow = 1f, HitTestPassThrough = true,
            Direction = 1, Justify = FlexJustify.Start, AlignItems = FlexAlign.End,
            Padding = new Edges4(0f, 104f, 14f, 0f),   // clear the title bar + toolbar; pinned top-right of the content
            Children = [ Embed.Comp(() => new FpsOverlay()) ],
        };
        return Ui.ZStack(root, hud) with { Grow = 1f };
    }
}
