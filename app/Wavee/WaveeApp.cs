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

    // The composition root passes the settings store created early (so the theme is seeded before the first frame);
    // null in tests falls back to the store Services creates itself.
    public WaveeApp(IAppSettings? settings = null) => _services = Services.UseRealBackend ? Services.CreateReal(settings) : Services.CreateFake(settings);

    public override Element Render()
    {
        var bridge = _services.Playback;
        var libBridge = _services.LibraryBridge;
        var store = _services.LibraryStore;

        // Follow the OS dark-mode / accent live WHILE the user hasn't pinned an explicit theme (mode == System). The host
        // relays WM_SETTINGCHANGE on the UI thread; we re-read the OS state, apply it, and animate the in-place re-theme.
        var requestTheme = UseContext(ThemeControl.Request);
        Context.UseEffect(() =>
        {
            FluentApp.SystemColorsChanged += () =>
            {
                if (_services.Settings.Get(WaveeSettings.ThemeMode) != 0) return;   // explicit Light/Dark pinned → ignore OS
                Theme.Dark = !FluentApp.SystemUsesLightTheme();
                if (FluentApp.SystemAccent() is { } a) Tok.SetAccent(a);
                requestTheme?.Invoke(250f);
            };
        });

        var post = Context.UsePost();
        var loginSession = UseRef<System.Threading.CancellationTokenSource?>(null);
        var wasAuthed = UseRef(false);   // have we EVER authenticated this run? (fake demo: logout → takeover, but no initial-launch flash)

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
                    var host = await Wavee.SpotifyLive.LiveSessionHost.StartAsync(_services, m => _services.Log.Info("connect", m), cts.Token, bridge.Progress(post), interactive: true, useBrowser: false).ConfigureAwait(false);
                    if (host is not null) { post(() => { if (loginSession.Value == cts) loginSession.Value = null; }); cts.Cancel(); }   // success → stop the browser sibling
                }
                catch (OperationCanceledException) { }   // superseded by a newer attempt
                catch (Exception ex)
                {
                    _services.Log.Info("connect", "code login failed: " + ex.Message);
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
                    var host = await Wavee.SpotifyLive.LiveSessionHost.StartAsync(_services, m => _services.Log.Info("connect", m), cts.Token, bridge.Progress(post), interactive: true, useBrowser: true, quietPhases: true).ConfigureAwait(false);
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
            bridge.Activate(post);
            libBridge.Activate(post);
            store.Activate(post);
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
                // Fake demo: connect instantly + start in-memory playback so the INITIAL launch lands on the shell (no
                // takeover flash, --screenshot renders the shell). After a logout the gate shows the demo two-pane instead.
                _ = _services.Session.ConnectAsync();
                _ = _services.Player.ResumeAsync();
                _services.Log.Info("app", "Demo backend; fake session + playback started");
            }
            // Diagnostic: open the full now-playing view on launch (for --screenshot visual diffing of that surface).
            if (Diag.EnvFlag("WAVEE_NOWPLAYING_OPEN")) _services.Playback.Expanded.Value = true;
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
            Ctx.Provide(LibraryStore.Slot, store,
                leaf))));

        // Debug-build FPS HUD on top (const-folds out of Release; subscribes to the host's per-frame stats). The HUD pill is
        // pinned top-right by a full-bleed PASS-THROUGH positioner (a PLAIN BoxEl — its HitTestPassThrough IS honoured, unlike
        // a component wrapper's mirrored-but-not-passthrough node, which would swallow every hit and silently kill scrolling).
        // ZStack carries Grow=1 to fill the window + stretch the shell exactly like the OverlayHost stack.
        if (!Diag.CompiledIn || Diag.EnvFlag("WAVEE_NO_FPS")) return root;
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