using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.WindowsApi.Activation;
using FluentGpu.WindowsApi.Credentials;
using FluentGpu.WindowsApi.Dialogs;
using FluentGpu.WindowsApi.Media;
using FluentGpu.WindowsApi.Network;
using FluentGpu.WindowsApi.Notifications;
using FluentGpu.WindowsApi.Packaging;
using FluentGpu.WindowsApi.Power;
// Disambiguate the flagged naming collision: this Windows-pillar page uses the OS-notification Toast. The in-app
// Controls.Toast (a card in the app window) is a different type in a different namespace (see the Toast gallery page).
using Toast = FluentGpu.WindowsApi.Notifications.Toast;
using FluentGpu.WindowsApi.Shell;
using FluentGpu.WindowsApi.Storage;
using static FluentGpu.Dsl.Ui;

// ── The "Windows APIs" gallery page ─────────────────────────────────────────────────────────────────────────────────
// A live, interactive showcase of the whole FluentGpu.WindowsApi surface — the four v1 pillars (Notifications /
// Credentials / Packaging / Activation) and the five v2 pillars (Media / Dialogs / Shell / Power / Network) — each as a
// self-contained demo card in the gallery house style (GalleryPage.Shell + ControlExample.Build). Every demo is safe to
// click repeatedly; OS round-trips run on the gallery UI thread, and the live ones (SMTC media-key presses, network
// connectivity changes, suspend/resume) surface in an on-card event log.
//
// HWND ownership: the pillars that need a top-level window (SMTC / file pickers / taskbar) take an explicit `nint hwnd`.
// The page passes FluentApp.WindowHandle — the real gallery window handle published by the host — never a handle
// invented on the Engine seam. Under --screenshot the window IS real (the shot harness runs the live D3D path), so the
// readonly cards (Packaging / Network status) render real values; the modal/interactive demos are click-driven.
//
// Cross-thread discipline: SMTC button presses, NLM ConnectivityChanged, and power broadcasts all arrive on arbitrary
// OS threads. Writing a signal off the UI thread is illegal (the reactive core is UI-thread), so each live card hops its
// OS-thread payloads onto the UI thread through the engine dispatcher (UsePost() → AppHost.Post): the post enqueues +
// wakes the loop, and the host drains it inside a reactive Batch at the top of the next frame. Crucially this does NOT
// re-render the card every frame (the earlier UseContext(FrameClock.Tick) drain did, which froze this page); the loop
// stays idle until an event actually arrives, then runs exactly one frame to apply it.

sealed class WindowsApiPage : Component
{
    public override Element Render()
    {
        // The page root owns the OS subscriptions' lifetime: on (re)mount, tear down anything a previous mount left
        // behind (navigating away → back must not leak an SMTC session / NLM Advise / keep-awake). This empty-dep mount
        // effect is the gallery's supported teardown point for a fresh page instance.
        UseEffect(() => WindowsApiLive.ResetForNewMount(), WinApiUi.MountOnce);

        return GalleryPage.ShellKeyed("windowsapi", "Windows APIs",
            "FluentGpu.WindowsApi — AOT-clean Win32/WinRT interop behind a small managed surface. Ten pillars, each a " +
            "live demo: toasts, the credential locker, package identity, protocol activation, the System Media Transport " +
            "Controls, file pickers, taskbar progress + jump lists, keep-awake/battery/suspend-resume, network " +
            "connectivity, app-data storage, and OS file/folder drop. Every button drives the real OS; the interactive " +
            "pillars are documented thread- and HWND-affine.",
            Embed.Comp(() => new NotificationsCard()),
            Embed.Comp(() => new CredentialsCard()),
            Embed.Comp(() => new PackagingCard()),
            Embed.Comp(() => new ActivationCard()),
            Embed.Comp(() => new MediaCard()),
            Embed.Comp(() => new DialogsCard()),
            Embed.Comp(() => new ShellCard()),
            Embed.Comp(() => new PowerCard()),
            Embed.Comp(() => new NetworkCard()),
            Embed.Comp(() => new StorageCard()),
            Embed.Comp(() => new FileDropCard()));
    }
}

// ── shared page helpers ─────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Process-static owner of the page's live OS subscriptions (the SMTC controls, the NLM connectivity subscription, and
/// the keep-awake/suspend-resume sessions). The gallery exposes no per-component unmount-disposable hook, so the page's
/// single mount effect calls <see cref="ResetForNewMount"/> to dispose whatever a prior mount left registered before the
/// new mount re-creates them — making navigate-away-then-back leak-free and every "start"/"stop" button idempotent.
/// </summary>
static class WindowsApiLive
{
    public static SystemMediaControls? Smtc;
    public static IDisposable? NetworkSub;
    public static IDisposable? KeepAwake;
    public static IDisposable? PowerSub;

    // PROCESS-GLOBAL event sinks. ToastNotifier.Default and PowerSession's static events live for the whole process, so
    // subscribing them per page-mount would stack handlers across navigations. Instead we attach ONE forwarder per
    // process (the *Installed flags) that calls whatever sink the CURRENT page mount installed; ResetForNewMount clears
    // the sinks so a stale (unmounted) page's log is never written. Sinks are set/cleared on the UI thread only.
    public static Action<string>? ToastActivatedSink;
    public static Action<string>? ActivationRedirectedSink;
    public static Action? SuspendingSink;
    public static Action? ResumedSink;
    private static bool s_toastInstalled;
    private static bool s_powerInstalled;
    private static bool s_activationInstalled;

    /// <summary>Idempotently attach the one process-level activation-redirect forwarder (UI-thread callback).</summary>
    public static void EnsureActivationForwarder()
    {
        if (s_activationInstalled) return;
        s_activationInstalled = true;
        WindowsApiInterop.OnActivationRedirected(uri => ActivationRedirectedSink?.Invoke(uri));
    }

    /// <summary>Idempotently attach the one process-level ToastNotifier.Activated forwarder (UI-thread callback).</summary>
    public static void EnsureToastForwarder()
    {
        if (s_toastInstalled) return;
        s_toastInstalled = true;
        ToastNotifier.Default.Activated += args => ToastActivatedSink?.Invoke(args.Argument);
    }

    /// <summary>Idempotently attach the one process-level PowerSession suspend/resume forwarders (OS-thread callbacks —
    /// the sinks themselves marshal to the UI thread).</summary>
    public static void EnsurePowerForwarder()
    {
        if (s_powerInstalled) return;
        s_powerInstalled = true;
        PowerSession.Suspending += () => SuspendingSink?.Invoke();
        PowerSession.Resumed += () => ResumedSink?.Invoke();
    }

    /// <summary>Dispose any subscriptions an earlier page mount left behind and drop its event sinks. Best-effort: a
    /// throwing Dispose on one pillar must not block the others (or the page mount).</summary>
    public static void ResetForNewMount()
    {
        ToastActivatedSink = null;
        ActivationRedirectedSink = null;
        SuspendingSink = null;
        ResumedSink = null;
        Swap(ref Smtc, null);
        Swap(ref NetworkSub, null);
        Swap(ref KeepAwake, null);
        Swap(ref PowerSub, null);
    }

    public static void Swap<T>(ref T? slot, T? next) where T : class, IDisposable
    {
        T? prev = slot;
        slot = next;
        if (!ReferenceEquals(prev, next))
            try { prev?.Dispose(); } catch { /* best-effort teardown */ }
    }
}

/// <summary>A small fixed-capacity, newest-first event log surfaced in a card's output panel. Mutated only on the UI
/// thread (OS-thread events arrive via the host UI dispatch, <c>UsePost()</c>); the backing signal drives the readout.</summary>
sealed class EventLog
{
    private readonly Signal<string> _text;
    private readonly System.Collections.Generic.List<string> _lines = new();
    private readonly int _cap;

    public EventLog(Signal<string> text, int capacity = 6) { _text = text; _cap = capacity; }

    public void Add(string line)
    {
        _lines.Insert(0, $"{DateTime.Now:HH:mm:ss}  {line}");
        if (_lines.Count > _cap) _lines.RemoveRange(_cap, _lines.Count - _cap);
        _text.Value = string.Join("\n", _lines);
    }

    public void Clear() { _lines.Clear(); _text.Value = "—"; }
}

/// <summary>Card-local UI atoms shared by every pillar card (kept here so the page reads as a flat catalog).</summary>
static class WinApiUi
{
    /// <summary>A stable, identity-equal empty dep array → a UseEffect carrying it runs exactly once per mount.</summary>
    public static readonly FluentGpu.Hooks.DepKey MountOnce = FluentGpu.Hooks.DepKey.Empty;

    // The accent/success/error/caution status colors WinUI uses for inline state lines.
    public static TextEl Status(string text, ColorF color) => new(text) { Size = 13f, Weight = 600, Color = color, Wrap = TextWrap.Wrap };
    public static ColorF Ok => Tok.SystemFillSuccess;
    public static ColorF Bad => Tok.SystemFillCritical;
    public static ColorF Warn => Tok.SystemFillCaution;
    public static ColorF Info => Tok.AccentDefault;

    /// <summary>A monospace log readout bound to a signal (only the text node updates as events arrive — no card re-render).</summary>
    public static Element LogView(Signal<string> text) => new BoxEl
    {
        Direction = 1, MinWidth = 240f, Padding = Edges4.All(10), Corners = Radii.ControlAll,
        Fill = Tok.FillSolidBase, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
        Children = [new TextEl("") { Size = 12f, Color = Tok.TextSecondary, FontFamily = "Cascadia Code", Wrap = TextWrap.Wrap, Text = Prop.Of(() => text.Value) }],
    };

    /// <summary>The standard right-side output panel for a card: a bold status line over a live event log.</summary>
    public static Element OutputPanel(Signal<string> status, ColorF statusColor, Signal<string> log) => new BoxEl
    {
        Direction = 1, Gap = 10f,
        Children =
        [
            new TextEl("") { Size = 13f, Weight = 600, Color = statusColor, Wrap = TextWrap.Wrap, Text = Prop.Of(() => status.Value) },
            LogView(log),
        ],
    };

    /// <summary>A read-only key/value row for the Packaging card.</summary>
    public static Element Field(string label, string value) => new BoxEl
    {
        Direction = 0, Gap = 12f, AlignItems = FlexAlign.Start,
        Children =
        [
            new BoxEl { Width = 150f, Children = [new TextEl(label) { Size = 13f, Color = Tok.TextTertiary }] },
            new BoxEl { Grow = 1f, Children = [new TextEl(value) { Size = 13f, Color = Tok.TextPrimary, FontFamily = "Cascadia Code", Wrap = TextWrap.Wrap }] },
        ],
    };
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// (1) Notifications — ToastBuilder → ToastNotifier.Show, plus a button-with-action toast whose click round-trips.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
[SupportedOSPlatform("windows10.0.10240.0")]
sealed class NotificationsCard : Component
{
    // The demo activator CLSID + AUMID display name (registered on first Show, unregistered when the page remounts via
    // the toast notifier's own lifecycle — these are process-stable like the smoke harness).
    static readonly Guid ActivatorClsid = new("3D9B4A2C-7E61-4F8A-9C22-FE00CAFEF00D");

    public override Element Render()
    {
        var title = UseSignal("FluentGpu");
        var body = UseSignal("A toast from the Windows APIs gallery page.");
        var status = UseSignal("Ready.");
        var log = UseSignal("—");
        var statusColor = UseSignal(WinApiUi.Info);

        // Bind the cross-thread toast-activation hop ONCE: the activator fires on a COM thread, so marshal to the UI
        // thread before touching the log signal. AppHost already delivers AppHost.ActivationRedirected on the UI thread,
        // but ToastNotifier.Activated is the in-proc callback — route it through the dispatch queue to be safe.
        var post = UsePost();   // engine UI-thread marshal (no per-frame re-render — replaces the FrameClock.Tick drain)
        var lg = UseRef<EventLog?>(null);
        lg.Value ??= new EventLog(log);

        // Install this mount's toast-activation sink (the one process forwarder calls it). The activation fires on a COM
        // thread → post hops it to the UI thread before touching the log signal.
        UseEffect(() =>
        {
            var l = lg.Value!;
            WindowsApiLive.ToastActivatedSink = arg => post(() => l.Add($"activated: {arg}"));
            WindowsApiLive.EnsureToastForwarder();
        }, WinApiUi.MountOnce);

        Action show = () =>
        {
            try
            {
                if (!ToastNotifier.IsSupported) { status.Value = "Toasts disabled (process is elevated)."; statusColor.Value = WinApiUi.Warn; return; }
                ToastNotifier.Default.Register(ActivatorClsid, "FluentGpu Gallery");
                bool ok = Toast.Create()
                    .Title(title.Peek())
                    .Body(body.Peek())
                    .Argument("source", "gallery")
                    .Tag("gallery-basic")
                    .ShowVia(ToastNotifier.Default);
                status.Value = ok ? "Toast shown (S_OK; it auto-expires)." : "Show returned false.";
                statusColor.Value = ok ? WinApiUi.Ok : WinApiUi.Bad;
            }
            catch (Exception ex) { status.Value = "Show failed: " + ex.Message; statusColor.Value = WinApiUi.Bad; }
        };

        Action showActionToast = () =>
        {
            try
            {
                if (!ToastNotifier.IsSupported) { status.Value = "Toasts disabled (process is elevated)."; statusColor.Value = WinApiUi.Warn; return; }
                ToastNotifier.Default.Register(ActivatorClsid, "FluentGpu Gallery");
                bool ok = Toast.Create()
                    .Title("Action toast")
                    .Body("Click a button — its argument round-trips to the Activated event below.")
                    .Argument("source", "gallery")
                    .Button("Play", b => b.Argument("action", "play").Success())
                    .Button("Skip", b => b.Argument("action", "skip"))
                    .DismissButton()
                    .ButtonStyles()
                    .Tag("gallery-action")
                    .ShowVia(ToastNotifier.Default);
                status.Value = ok ? "Action toast shown — click a button." : "Show returned false.";
                statusColor.Value = ok ? WinApiUi.Ok : WinApiUi.Bad;
            }
            catch (Exception ex) { status.Value = "Show failed: " + ex.Message; statusColor.Value = WinApiUi.Bad; }
        };

        var form = new BoxEl
        {
            Direction = 1, Gap = 12f,
            Children =
            [
                TextBox.Create(title, options: new TextBox.TextBoxOptions { Header = "Title", Width = 320f }),
                TextBox.Create(body, options: new TextBox.TextBoxOptions { Header = "Body", Width = 320f }),
                new BoxEl
                {
                    Direction = 0, Gap = 8f, Wrap = true, AlignItems = FlexAlign.Center,
                    Children = [Button.Accent("Show toast", show), Button.Standard("Action toast", showActionToast)],
                },
            ],
        };

        return ControlExample.Build("Toast notifications",
            form,
            description: "Toast.Create() is a fluent builder — Title/Body/Button/Tag and no XML in sight: ShowVia() builds the ToastGeneric payload, carries the tag, and Show()s it. Buttons take a ToastButton config (icon, Success/Critical style, Dismiss, context-menu). The action toast's button argument round-trips to the Activated event (click a button on the banner). BuildXml() stays the raw escape hatch.",
            output: WinApiUi.OutputPanel(status, WinApiUi.Info, log),
            code: """
            ToastNotifier.Default.Register(activatorClsid, "FluentGpu Gallery");
            ToastNotifier.Default.Activated += args => Log(args.Argument);

            Toast.Create()
                 .Title(title).Body(body)
                 .Argument("source", "gallery")
                 .Button("Play", b => b.Argument("action", "play").Success())
                 .Button("Skip", b => b.Argument("action", "skip"))
                 .DismissButton()
                 .ButtonStyles()
                 .Tag("gallery-action")
                 .ShowVia(ToastNotifier.Default);   // no XML; raw BuildXml() still available
            """);
    }
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// (2) Credentials — Store / Retrieve / Delete against the live Credential Locker (demo-prefixed target).
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
[SupportedOSPlatform("windows")]
sealed class CredentialsCard : Component
{
    public override Element Render()
    {
        var target = UseSignal("FluentGpu.Gallery.Demo");
        var user = UseSignal("demo-user");
        var secret = UseSignal("s3cr3t-é❤");
        var status = UseSignal("Ready — uses a demo-prefixed target, reversible.");
        var statusColor = UseSignal(WinApiUi.Info);

        Action store = () =>
        {
            try
            {
                CredentialStore.Store(target.Peek(), user.Peek(),
                    System.Text.Encoding.UTF8.GetBytes(secret.Peek()), CredentialScope.LocalMachine);
                status.Value = $"Stored \"{target.Peek()}\".";
                statusColor.Value = WinApiUi.Ok;
            }
            catch (Exception ex) { status.Value = "Store failed: " + ex.Message; statusColor.Value = WinApiUi.Bad; }
        };

        Action retrieve = () =>
        {
            try
            {
                if (CredentialStore.TryRetrieve(target.Peek(), out string u, out byte[] secretBytes))
                {
                    status.Value = $"Retrieved: user=\"{u}\", secret=\"{System.Text.Encoding.UTF8.GetString(secretBytes)}\".";
                    statusColor.Value = WinApiUi.Ok;
                }
                else { status.Value = "No credential stored for that target."; statusColor.Value = WinApiUi.Warn; }
            }
            catch (Exception ex) { status.Value = "Retrieve failed: " + ex.Message; statusColor.Value = WinApiUi.Bad; }
        };

        Action delete = () =>
        {
            try
            {
                bool removed = CredentialStore.Delete(target.Peek());
                status.Value = removed ? "Deleted." : "Nothing to delete (already gone).";
                statusColor.Value = removed ? WinApiUi.Ok : WinApiUi.Warn;
            }
            catch (Exception ex) { status.Value = "Delete failed: " + ex.Message; statusColor.Value = WinApiUi.Bad; }
        };

        var form = new BoxEl
        {
            Direction = 1, Gap = 12f,
            Children =
            [
                TextBox.Create(target, options: new TextBox.TextBoxOptions { Header = "Target", Width = 320f }),
                TextBox.Create(user, options: new TextBox.TextBoxOptions { Header = "User name", Width = 320f }),
                TextBox.Create(secret, options: new TextBox.TextBoxOptions { Header = "Secret", Width = 320f }),
                new BoxEl
                {
                    Direction = 0, Gap = 8f, Wrap = true, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        Button.Accent("Store", store),
                        Button.Standard("Retrieve", retrieve),
                        Button.Standard("Delete", delete),
                    ],
                },
            ],
        };

        return ControlExample.Build("Credential locker",
            form,
            description: "CredentialStore wraps the Win32 Credential Manager (CredWrite/CredRead/CredDelete). The secret round-trips byte-exact; the demo target is namespaced so it never collides with real app data.",
            output: new BoxEl { Direction = 1, MinWidth = 240f, Children = [new TextEl("") { Size = 13f, Weight = 600, Wrap = TextWrap.Wrap, Color = WinApiUi.Info, Text = Prop.Of(() => status.Value), }] },
            code: """
            CredentialStore.Store(target, user, Encoding.UTF8.GetBytes(secret), CredentialScope.LocalMachine);

            if (CredentialStore.TryRetrieve(target, out string user, out byte[] secret)) { /* … */ }

            CredentialStore.Delete(target);
            """);
    }
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// (3) Packaging — read-only identity: IsPackaged / AUMID / version / install location.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
[SupportedOSPlatform("windows")]
sealed class PackagingCard : Component
{
    public override Element Render()
    {
        // Read once on mount (the identity is process-stable). Running under dotnet/the bare exe ⇒ unpackaged ⇒ nulls.
        bool packaged = PackageIdentity.IsPackaged;
        string Show(string? s) => s ?? "(unpackaged)";

        var card = new BoxEl
        {
            Direction = 1, Gap = 12f,
            Children =
            [
                WinApiUi.Field("IsPackaged", packaged ? "true" : "false"),
                WinApiUi.Field("Package full name", Show(PackageIdentity.PackageFullName)),
                WinApiUi.Field("Package family", Show(PackageIdentity.PackageFamilyName)),
                WinApiUi.Field("AUMID", Show(PackageIdentity.ApplicationUserModelId)),
                WinApiUi.Field("Version", Show(PackageIdentity.Version?.ToString())),
                WinApiUi.Field("Install location", Show(PackageIdentity.InstalledLocation)),
            ],
        };

        return ControlExample.Build("Package identity",
            card,
            description: "PackageIdentity queries the modern-app runtime identity (GetCurrentPackageFullName + GetCurrentApplicationUserModelId). Under dotnet run / the bare exe this process is unpackaged, so the getters return null — exactly as designed.",
            code: """
            bool packaged = PackageIdentity.IsPackaged;
            string? aumid = PackageIdentity.ApplicationUserModelId;
            Version? version = PackageIdentity.Version;
            string? install = PackageIdentity.InstalledLocation;
            """);
    }
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// (4) Activation — register/unregister the fluentgpu-demo: protocol, launch it, watch the single-instance round-trip.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
[SupportedOSPlatform("windows")]
sealed class ActivationCard : Component
{
    const string Scheme = "fluentgpu-demo";

    public override Element Render()
    {
        var status = UseSignal("Ready.");
        var statusColor = UseSignal(WinApiUi.Info);
        var log = UseSignal("—");

        // The single-instance redirect arrives on the UI thread (AppHost re-raises ActivationRedirected from Paint), so
        // the log write is UI-thread-safe without a marshal. Install this mount's sink; the process forwarder calls it.
        var lg = UseRef<EventLog?>(null);
        lg.Value ??= new EventLog(log);
        UseEffect(() =>
        {
            var l = lg.Value!;
            WindowsApiLive.ActivationRedirectedSink = uri => l.Add($"redirected: {uri}");
            WindowsApiLive.EnsureActivationForwarder();
        }, WinApiUi.MountOnce);

        Action register = () =>
        {
            try
            {
                string exe = Environment.ProcessPath ?? "";
                ProtocolRegistrar.RegisterProtocol(Scheme, exe, "FluentGpu Demo Protocol");
                status.Value = $"Registered {Scheme}:// → this exe.";
                statusColor.Value = WinApiUi.Ok;
            }
            catch (Exception ex) { status.Value = "Register failed: " + ex.Message; statusColor.Value = WinApiUi.Bad; }
        };

        Action unregister = () =>
        {
            try { ProtocolRegistrar.UnregisterProtocol(Scheme); status.Value = "Unregistered."; statusColor.Value = WinApiUi.Ok; }
            catch (Exception ex) { status.Value = "Unregister failed: " + ex.Message; statusColor.Value = WinApiUi.Bad; }
        };

        Action launch = () =>
        {
            try
            {
                // ShellExecute the registered scheme: a second instance launches, the SingleInstanceGate sees it is not
                // first, forwards the deep link via WM_COPYDATA, and exits — the redirect lands in the log live.
                Process.Start(new ProcessStartInfo($"{Scheme}:hello?from=gallery") { UseShellExecute = true });
                status.Value = "Launched fluentgpu-demo:hello — watch the log for the redirect.";
                statusColor.Value = WinApiUi.Info;
            }
            catch (Exception ex) { status.Value = "Launch failed (register first?): " + ex.Message; statusColor.Value = WinApiUi.Bad; }
        };

        var form = new BoxEl
        {
            Direction = 1, Gap = 12f,
            Children =
            [
                Body($"Register the {Scheme}:// URI scheme to this executable, then launch a deep link. Because the gallery is single-instance, the second launch redirects its activation back to this window — visible live in the log.").Secondary() with { MaxWidth = 460f },
                new BoxEl
                {
                    Direction = 0, Gap = 8f, Wrap = true, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        Button.Standard("Register protocol", register),
                        Button.Standard("Unregister", unregister),
                        Button.Accent("Launch fluentgpu-demo:hello", launch),
                    ],
                },
            ],
        };

        return ControlExample.Build("Protocol activation + single instance",
            form,
            output: WinApiUi.OutputPanel(status, WinApiUi.Info, log),
            code: """
            ProtocolRegistrar.RegisterProtocol("fluentgpu-demo", Environment.ProcessPath, "FluentGpu Demo Protocol");

            // Launching the scheme re-enters the single-instance gate; the redirect arrives on AppHost.ActivationRedirected:
            Process.Start(new ProcessStartInfo("fluentgpu-demo:hello?from=gallery") { UseShellExecute = true });
            """);
    }
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// (5) Media — SystemMediaTransportControls: now-playing fields, Playing/Paused, and a live media-key event log.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
[SupportedOSPlatform("windows8.0")]
sealed class MediaCard : Component
{
    public override Element Render()
    {
        var trackTitle = UseSignal("Midnight City");
        var artist = UseSignal("The Wanderers");
        var status = UseSignal("Not connected. Click Enable SMTC.");
        var statusColor = UseSignal(WinApiUi.Info);
        var log = UseSignal("—");
        var posSec = UseSignal(60.0);   // F4 timeline scrub position (seconds into a 210s track)

        // The SMTC ButtonPressed callback arrives on an OS worker thread → post hops it to the UI thread (engine marshal;
        // wakes the loop, drains next frame, no per-frame re-render).
        var post = UsePost();
        var lg = UseRef<EventLog?>(null);
        lg.Value ??= new EventLog(log);

        Action enable = () =>
        {
            try
            {
                nint hwnd = WindowsApiInterop.WindowHandle;
                if (hwnd == 0) { status.Value = "No window handle yet."; statusColor.Value = WinApiUi.Bad; return; }

                // Idempotent: dispose any prior session (re-clicking Enable rebinds cleanly) then acquire + wire.
                var smtc = SystemMediaControls.GetForWindow(hwnd);
                smtc.ButtonDispatcher = raise => post(raise);   // OS-thread → UI-thread hop (engine dispatcher)
                smtc.ButtonPressed += btn => lg.Value!.Add($"button: {btn}");
                smtc.SetEnabledButtons(play: true, pause: true, next: true, previous: true);
                smtc.UpdateDisplay(trackTitle.Peek(), artist.Peek(), albumTitle: "Gallery Sessions");
                smtc.SetPlaybackStatus(MediaPlaybackStatus.Playing);
                smtc.IsEnabled = true;
                WindowsApiLive.Swap(ref WindowsApiLive.Smtc, smtc);

                status.Value = "SMTC enabled — press the hardware media keys (▶ ⏸ ⏭ ⏮).";
                statusColor.Value = WinApiUi.Ok;
            }
            catch (Exception ex) { status.Value = "Enable failed: " + ex.Message; statusColor.Value = WinApiUi.Bad; }
        };

        void SetState(MediaPlaybackStatus s, string label)
        {
            try
            {
                var smtc = WindowsApiLive.Smtc;
                if (smtc is null) { status.Value = "Enable SMTC first."; statusColor.Value = WinApiUi.Warn; return; }
                smtc.UpdateDisplay(trackTitle.Peek(), artist.Peek(), albumTitle: "Gallery Sessions");
                smtc.SetPlaybackStatus(s);
                status.Value = $"Playback: {label}.";
                statusColor.Value = WinApiUi.Ok;
            }
            catch (Exception ex) { status.Value = label + " failed: " + ex.Message; statusColor.Value = WinApiUi.Bad; }
        }

        // F4 — push the timeline (position + track length) so the now-playing flyout / lock screen shows a SCRUB BAR.
        void PushTimeline()
        {
            try
            {
                var smtc = WindowsApiLive.Smtc;
                if (smtc is null) { status.Value = "Enable SMTC first."; statusColor.Value = WinApiUi.Warn; return; }
                var pos = TimeSpan.FromSeconds(posSec.Peek());
                var end = TimeSpan.FromSeconds(210);
                smtc.UpdateTimeline(pos, end);
                status.Value = $"Timeline pushed — {pos:m\\:ss} / {end:m\\:ss} (scrub bar on the flyout).";
                statusColor.Value = WinApiUi.Ok;
                lg.Value!.Add($"timeline: {pos:m\\:ss}/{end:m\\:ss}");
            }
            catch (Exception ex) { status.Value = "UpdateTimeline failed: " + ex.Message; statusColor.Value = WinApiUi.Bad; }
        }

        var form = new BoxEl
        {
            Direction = 1, Gap = 12f,
            Children =
            [
                TextBox.Create(trackTitle, options: new TextBox.TextBoxOptions { Header = "Track title", Width = 320f }),
                TextBox.Create(artist, options: new TextBox.TextBoxOptions { Header = "Artist", Width = 320f }),
                new BoxEl
                {
                    Direction = 0, Gap = 8f, Wrap = true, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        Button.Accent("Enable SMTC", enable),
                        Button.Standard("Playing", () => SetState(MediaPlaybackStatus.Playing, "Playing")),
                        Button.Standard("Paused", () => SetState(MediaPlaybackStatus.Paused, "Paused")),
                        Button.Standard("Push timeline", PushTimeline),
                        Button.Standard("+30s", () => { posSec.Value = Math.Min(210, posSec.Peek() + 30); PushTimeline(); }),
                    ],
                },
            ],
        };

        return ControlExample.Build("System Media Transport Controls",
            form,
            description: "SystemMediaControls.GetForWindow wires this window to the OS media surface (now-playing flyout, lock screen, hardware media keys). Enable it, then press the media keys on your keyboard or headset — each press lands in the log (marshalled off the SMTC worker thread). \"Push timeline\" sends the position + track length so the flyout shows a working scrub bar; \"+30s\" advances it.",
            output: WinApiUi.OutputPanel(status, WinApiUi.Info, log),
            code: """
            var smtc = SystemMediaControls.GetForWindow(hwnd);
            smtc.ButtonDispatcher = raise => PostToUiThread(raise);     // ButtonPressed fires on an OS thread
            smtc.ButtonPressed += btn => Log($"button: {btn}");
            smtc.SetEnabledButtons(play: true, pause: true, next: true, previous: true);
            smtc.UpdateDisplay("Midnight City", "The Wanderers", albumTitle: "Gallery Sessions");
            smtc.SetPlaybackStatus(MediaPlaybackStatus.Playing);
            smtc.UpdateTimeline(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(210));  // scrub bar
            smtc.IsEnabled = true;
            """);
    }
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// (6) Dialogs — Open file / Save file / Pick folder (modal, owner = the gallery window).
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
[SupportedOSPlatform("windows6.0.6000")]
sealed class DialogsCard : Component
{
    static readonly (string Name, string Spec)[] AudioFilters =
    {
        ("Audio", "*.mp3;*.flac;*.wav;*.m4a"), ("All files", "*.*"),
    };

    public override Element Render()
    {
        var picked = UseSignal("—");
        var statusColor = UseSignal(WinApiUi.Info);

        Action open = () =>
        {
            try
            {
                string? path = FilePicker.OpenFile(WindowsApiInterop.WindowHandle, "Open audio", AudioFilters);
                picked.Value = path ?? "(cancelled)";
                statusColor.Value = path is null ? WinApiUi.Warn : WinApiUi.Ok;
            }
            catch (Exception ex) { picked.Value = "OpenFile failed: " + ex.Message; statusColor.Value = WinApiUi.Bad; }
        };

        Action save = () =>
        {
            try
            {
                string? path = FilePicker.SaveFile(WindowsApiInterop.WindowHandle, "Save playlist", "playlist.m3u",
                    ("Playlist", "*.m3u"), ("All files", "*.*"));
                picked.Value = path ?? "(cancelled)";
                statusColor.Value = path is null ? WinApiUi.Warn : WinApiUi.Ok;
            }
            catch (Exception ex) { picked.Value = "SaveFile failed: " + ex.Message; statusColor.Value = WinApiUi.Bad; }
        };

        Action folder = () =>
        {
            try
            {
                string? path = FilePicker.PickFolder(WindowsApiInterop.WindowHandle, "Pick a music folder");
                picked.Value = path ?? "(cancelled)";
                statusColor.Value = path is null ? WinApiUi.Warn : WinApiUi.Ok;
            }
            catch (Exception ex) { picked.Value = "PickFolder failed: " + ex.Message; statusColor.Value = WinApiUi.Bad; }
        };

        var form = new BoxEl
        {
            Direction = 0, Gap = 8f, Wrap = true, AlignItems = FlexAlign.Center,
            Children =
            [
                Button.Accent("Open file…", open),
                Button.Standard("Save file…", save),
                Button.Standard("Pick folder…", folder),
            ],
        };

        return ControlExample.Build("File pickers",
            form,
            description: "FilePicker wraps the modern IFileOpenDialog / IFileSaveDialog common-item dialog. Each picker is modal to the gallery window (owner HWND passed explicitly) and returns the chosen filesystem path, or null on cancel.",
            output: new BoxEl
            {
                Direction = 1, MinWidth = 240f, Gap = 6f,
                Children =
                [
                    Caption("Picked path").Tertiary(),
                    new TextEl("") { Size = 13f, Weight = 600, Wrap = TextWrap.Wrap, Color = WinApiUi.Info, Text = Prop.Of(() => picked.Value) },
                ],
            },
            code: """
            string? path = FilePicker.OpenFile(ownerHwnd, "Open audio", ("Audio", "*.mp3;*.flac"), ("All files", "*.*"));
            string? save = FilePicker.SaveFile(ownerHwnd, "Save playlist", "playlist.m3u", ("Playlist", "*.m3u"));
            string? dir  = FilePicker.PickFolder(ownerHwnd, "Pick a music folder");
            """);
    }
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// (7) Shell — taskbar progress (slider) + state buttons + overlay toggle + jump list.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
[SupportedOSPlatform("windows6.1")]
sealed class ShellCard : Component
{
    public override Element Render()
    {
        var progress = UseFloatSignal(0.4f);
        var overlayOn = UseSignal(false);
        var status = UseSignal("Drag the slider to drive the taskbar button progress.");
        var statusColor = UseSignal(WinApiUi.Info);

        void Apply(float v)
        {
            progress.Value = v;
            try
            {
                nint hwnd = WindowsApiInterop.WindowHandle;
                TaskbarManager.SetProgressState(hwnd, TaskbarProgressState.Normal);
                TaskbarManager.SetProgress(hwnd, (ulong)(v * 100f), 100);
                status.Value = $"Taskbar progress: {(int)(v * 100f)}%.";
                statusColor.Value = WinApiUi.Ok;
            }
            catch (Exception ex) { status.Value = "SetProgress failed: " + ex.Message; statusColor.Value = WinApiUi.Bad; }
        }

        void State(TaskbarProgressState s, string label)
        {
            try
            {
                TaskbarManager.SetProgressState(WindowsApiInterop.WindowHandle, s);
                status.Value = $"Progress state: {label}.";
                statusColor.Value = WinApiUi.Ok;
            }
            catch (Exception ex) { status.Value = label + " failed: " + ex.Message; statusColor.Value = WinApiUi.Bad; }
        }

        Action clear = () =>
        {
            try { TaskbarManager.ClearProgress(WindowsApiInterop.WindowHandle); status.Value = "Progress cleared."; statusColor.Value = WinApiUi.Info; }
            catch (Exception ex) { status.Value = "Clear failed: " + ex.Message; statusColor.Value = WinApiUi.Bad; }
        };

        Action toggleOverlay = () =>
        {
            try
            {
                bool next = !overlayOn.Peek();
                // A null icon path clears the overlay; a real .ico would set it. The app ships no demo .ico, so this
                // demonstrates the set→clear cycle (the description notes the icon-path requirement).
                TaskbarManager.SetOverlayIcon(WindowsApiInterop.WindowHandle, next ? null : null, next ? "Active" : "");
                overlayOn.Value = next;
                status.Value = next ? "Overlay icon set (no demo .ico bundled → cleared badge)." : "Overlay icon removed.";
                statusColor.Value = WinApiUi.Info;
            }
            catch (Exception ex) { status.Value = "Overlay failed: " + ex.Message; statusColor.Value = WinApiUi.Bad; }
        };

        Action setJumpList = () =>
        {
            try
            {
                string exe = Environment.ProcessPath ?? "";
                JumpList.SetTasks(null,
                    new JumpTask("Play liked songs", exe, "fluentgpu-demo:play?list=liked"),
                    new JumpTask("Open gallery", exe, "fluentgpu-demo:hello?from=jumplist"));
                status.Value = "Jump list set — right-click the taskbar button.";
                statusColor.Value = WinApiUi.Ok;
            }
            catch (Exception ex) { status.Value = "SetTasks failed: " + ex.Message; statusColor.Value = WinApiUi.Bad; }
        };

        Action clearJumpList = () =>
        {
            try { JumpList.Clear(); status.Value = "Jump list cleared."; statusColor.Value = WinApiUi.Info; }
            catch (Exception ex) { status.Value = "Clear jump list failed: " + ex.Message; statusColor.Value = WinApiUi.Bad; }
        };

        var form = new BoxEl
        {
            Direction = 1, Gap = 12f,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Gap = 14f, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        new BoxEl { Width = 64f, Children = [new TextEl("Progress") { Size = 13f, Color = Tok.TextSecondary }] },
                        // Signal-bound: the thumb rides `progress` on the compositor fast path (a scrub writes the signal,
                        // no card re-render). Apply() runs the taskbar side-effect; its progress.Value write coalesces
                        // (the slider already wrote the same value) and the status text below re-renders on that change.
                        Slider.Create(progress, Apply, length: 240f),
                    ],
                },
                new BoxEl
                {
                    Direction = 0, Gap = 8f, Wrap = true, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        Button.Standard("Normal", () => State(TaskbarProgressState.Normal, "Normal")),
                        Button.Standard("Paused", () => State(TaskbarProgressState.Paused, "Paused")),
                        Button.Standard("Error", () => State(TaskbarProgressState.Error, "Error")),
                        Button.Standard("Indeterminate", () => State(TaskbarProgressState.Indeterminate, "Indeterminate")),
                        Button.Standard("Clear", clear),
                    ],
                },
                new BoxEl
                {
                    Direction = 0, Gap = 8f, Wrap = true, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        Button.Standard("Toggle overlay icon", toggleOverlay),
                        Button.Accent("Set jump list", setJumpList),
                        Button.Standard("Clear jump list", clearJumpList),
                    ],
                },
            ],
        };

        return ControlExample.Build("Taskbar progress & jump list",
            form,
            description: "TaskbarManager drives the taskbar button's progress bar and overlay icon via ITaskbarList3; JumpList builds the right-click task list via ICustomDestinationList. The jump-list tasks carry fluentgpu-demo:// deep links — clicking one relaunches and round-trips through the activation pillar.",
            output: new BoxEl { Direction = 1, MinWidth = 240f, Children = [new TextEl("") { Size = 13f, Weight = 600, Wrap = TextWrap.Wrap, Color = WinApiUi.Info, Text = Prop.Of(() => status.Value), }] },
            code: """
            TaskbarManager.SetProgressState(hwnd, TaskbarProgressState.Normal);
            TaskbarManager.SetProgress(hwnd, completed: 40, total: 100);

            JumpList.SetTasks(aumid: null,
                new JumpTask("Play liked songs", exe, "fluentgpu-demo:play?list=liked"),
                new JumpTask("Open gallery", exe, "fluentgpu-demo:hello?from=jumplist"));
            """);
    }
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// (8) Power — KeepAwake toggle (status shows active) + suspend/resume event log.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
[SupportedOSPlatform("windows8.0")]
sealed class PowerCard : Component
{
    public override Element Render()
    {
        var awake = UseSignal(false);
        var status = UseSignal("System sleep is allowed.");
        var statusColor = UseSignal(WinApiUi.Info);
        var log = UseSignal("—");

        // Suspend/resume broadcasts arrive on an OS power-broadcast thread → post hops them to the UI thread.
        var post = UsePost();
        var lg = UseRef<EventLog?>(null);
        lg.Value ??= new EventLog(log);

        // Install this mount's suspend/resume sinks (the process forwarder calls them) and hold ONE NLM-style power
        // registration via WindowsApiLive so it's torn down on remount. Broadcasts arrive on an OS thread → post marshals.
        UseEffect(() =>
        {
            var l = lg.Value!;
            WindowsApiLive.SuspendingSink = () => post(() => l.Add("suspending"));
            WindowsApiLive.ResumedSink = () => post(() => l.Add("resumed"));
            WindowsApiLive.EnsurePowerForwarder();
            WindowsApiLive.Swap(ref WindowsApiLive.PowerSub, PowerSession.Subscribe());
        }, WinApiUi.MountOnce);

        // The ToggleSwitch owns the `awake` signal (writes it before onChange), so this only performs the side effect
        // for the NEW value — it must NOT flip `awake` again.
        Action<bool> toggle = on =>
        {
            try
            {
                if (!on)
                {
                    WindowsApiLive.Swap(ref WindowsApiLive.KeepAwake, null);
                    status.Value = "System sleep is allowed.";
                    statusColor.Value = WinApiUi.Info;
                }
                else
                {
                    WindowsApiLive.Swap(ref WindowsApiLive.KeepAwake, (System.IDisposable)PowerSession.KeepAwake(keepDisplayOn: false));
                    status.Value = "Keep-awake ACTIVE — the system will not sleep (display may still dim).";
                    statusColor.Value = WinApiUi.Ok;
                }
            }
            catch (Exception ex) { status.Value = "KeepAwake failed: " + ex.Message; statusColor.Value = WinApiUi.Bad; }
        };

        // F1 — a one-shot power snapshot (GetSystemPowerStatus): AC/DC, battery %, charging, energy saver, est. runtime.
        Action readPower = () =>
        {
            try
            {
                PowerStatus p = PowerSession.ReadPower();
                string batt = p.HasBattery
                    ? $"{(p.BatteryPercent is int pct ? pct + "%" : "?%")}{(p.IsCharging ? " charging" : "")}" +
                      (p.RemainingDischarge is { } rd ? $", ~{rd:h\\:mm} left" : "")
                    : "no battery";
                status.Value = $"{p.Source} · {batt}{(p.EnergySaverOn ? " · Energy Saver ON" : "")}";
                statusColor.Value = p.EnergySaverOn ? WinApiUi.Warn : WinApiUi.Ok;
                lg.Value!.Add($"power: {p.Source} batt={(p.BatteryPercent?.ToString() ?? "—")} saver={p.EnergySaverOn}");
            }
            catch (Exception ex) { status.Value = "ReadPower failed: " + ex.Message; statusColor.Value = WinApiUi.Bad; }
        };

        var form = new BoxEl
        {
            Direction = 1, Gap = 12f,
            Children =
            [
                // .Value (NOT .Peek): ToggleSwitch.Create is CONTROLLED — it paints exactly the bool passed each render.
                // Reading .Value subscribes this card so toggle()'s awake.Value write re-renders it (granular). With .Peek
                // the card didn't re-render after the FrameClock.Tick drain was removed, so the switch froze OFF while the
                // status text (a bound Prop) read ACTIVE.
                ToggleSwitch.Create(awake, onChange: toggle, header: "Keep system awake"),
                Body("Holds a power-availability request (SetThreadExecutionState) for as long as it is on. Suspend/resume broadcasts appear in the log — try sleeping and waking the machine.").Secondary() with { MaxWidth = 460f },
                new BoxEl { Direction = 0, Gap = 8f, AlignItems = FlexAlign.Center, Children = [Button.Standard("Read power status", readPower)] },
            ],
        };

        return ControlExample.Build("Power — keep awake, battery snapshot & suspend/resume",
            form,
            output: WinApiUi.OutputPanel(status, WinApiUi.Info, log),
            code: """
            // Hold a keep-awake request for the lifetime of the returned handle:
            IDisposable awake = PowerSession.KeepAwake(keepDisplayOn: false);
            // … later: awake.Dispose();  // allow sleep again

            // A one-shot battery / AC-DC / energy-saver snapshot:
            PowerStatus p = PowerSession.ReadPower();
            if (p.HasBattery) Log($"{p.Source} {p.BatteryPercent}%{(p.IsCharging ? " charging" : "")}");

            PowerSession.Suspending += () => Log("suspending");
            PowerSession.Resumed    += () => Log("resumed");
            using var sub = PowerSession.Subscribe();
            """);
    }
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// (9) Network — live IsOnline / connectivity readout + ConnectivityChanged event log.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
[SupportedOSPlatform("windows6.0.6000")]
sealed class NetworkCard : Component
{
    public override Element Render()
    {
        // OPTIMISTIC seed — NEVER read NLM in render. NetworkStatus.IsOnline / GetConnectivity each create the NLM COM
        // object and consult NCSI, which can BLOCK ~1s; doing that in the render body (the old bug) froze navigation, and
        // because the card also re-rendered every frame (UseContext(FrameClock.Tick) to drain) it re-froze every frame.
        // The real values are read OFF-THREAD on mount and posted back, so the page paints instantly.
        var online = UseSignal(true);
        var level = UseSignal(NetworkConnectivityLevel.InternetAccess);
        var status = UseSignal("—");
        var statusColor = UseSignal(WinApiUi.Info);
        var log = UseSignal("—");

        var post = UsePost();   // engine UI-thread marshal (replaces the hand-rolled PageDispatch + FrameClock.Tick drain)
        var lg = UseRef<EventLog?>(null);
        lg.Value ??= new EventLog(log);

        // Initial read off the UI thread + the change subscription, once on mount. NLM reads can block (~1s NCSI round
        // trip), so they NEVER run in render and NEVER on the UI thread — NetworkStatus.ReadAsync() runs them on the pillar's
        // dedicated long-lived MTA reader (apartment-correct: the agile NLM object lives in the process MTA, no pump, no
        // STA flip of a pooled thread) and we post the result back to the UI thread (post = UsePost(), drained next frame).
        UseEffect(() =>
        {
            var l = lg.Value!;
            _ = NetworkStatus.ReadAsync().ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully) { var snap = t.Result; post(() => { online.Value = snap.Online; level.Value = snap.Level; }); }
            }, System.Threading.Tasks.TaskScheduler.Default);
            WindowsApiLive.Swap(ref WindowsApiLive.NetworkSub, NetworkStatus.Subscribe(isOnline =>
            {
                // ConnectivityChanged hands us online/offline directly; only the level needs an off-thread refresh.
                post(() => { online.Value = isOnline; l.Add($"changed: {(isOnline ? "online" : "offline")}"); });
                _ = NetworkStatus.ReadConnectivityAsync().ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully) { var lvl = t.Result; post(() => level.Value = lvl); }
                }, System.Threading.Tasks.TaskScheduler.Default);
            }));
        }, WinApiUi.MountOnce);

        Action refresh = () =>
        {
            status.Value = "Refreshing…"; statusColor.Value = WinApiUi.Info;
            _ = NetworkStatus.ReadAsync().ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully) { var snap = t.Result; post(() => { online.Value = snap.Online; level.Value = snap.Level; status.Value = "Refreshed."; }); }
            }, System.Threading.Tasks.TaskScheduler.Default);
        };

        var readout = new BoxEl
        {
            Direction = 1, Gap = 10f,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Gap = 10f, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        // Segoe Fluent: Wifi (E701) when online, IncidentTriangle (E814) when offline (raw codepoints —
                        // Icons.cs curates only a subset, and these two are not in it).
                        new TextEl("") { Size = 28f, FontFamily = Theme.IconFont, Color = Prop.Of(() => online.Value ? Tok.AccentDefault : Tok.SystemFillCaution), Text = Prop.Of(() => online.Value ? "" : "") },
                        new TextEl("") { Size = 18f, Weight = 600, Color = Tok.TextPrimary, Text = Prop.Of(() => online.Value ? "Online" : "Offline") },
                    ],
                },
                new TextEl("") { Size = 13f, Color = Tok.TextSecondary, Text = Prop.Of(() => $"Connectivity: {level.Value}") },
                Button.Standard("Refresh", refresh),
                Body("Toggle Wi-Fi / unplug the network and watch the log — NetworkStatus.Subscribe raises ConnectivityChanged off the Network List Manager (marshalled to the UI thread).").Secondary() with { MaxWidth = 460f },
            ],
        };

        return ControlExample.Build("Network connectivity",
            readout,
            output: WinApiUi.OutputPanel(status, WinApiUi.Info, log),
            code: """
            bool online = NetworkStatus.IsOnline;
            NetworkConnectivityLevel level = NetworkStatus.GetConnectivity();

            using var sub = NetworkStatus.Subscribe(isOnline => PostToUiThread(() => Update(isOnline)));
            """);
    }
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// (10) Storage — AppDataStore typed Get/Set persisted under HKCU + folders; SettingsStore is the reactive write-through wrap.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
[SupportedOSPlatform("windows")]
sealed class StorageCard : Component
{
    public override Element Render()
    {
        var store = UseRef<AppDataStore?>(null);
        store.Value ??= AppDataStore.ForUnpackaged("FluentGpu", "Gallery");
        var note = UseSignal("");
        var status = UseSignal("Type a note, Save, then Reload (or restart the app) — it persists in HKCU.");
        var log = UseSignal("—");

        // Load the saved value once on mount (one-shot read; the card seeds the textbox from disk).
        UseEffect(() => { note.Value = store.Value!.GetString("note", ""); }, WinApiUi.MountOnce);

        Action save = () => { try { store.Value!.SetString("note", note.Peek()); status.Value = "Saved to HKCU."; } catch (Exception ex) { status.Value = "Save failed: " + ex.Message; } };
        Action reload = () => { try { note.Value = store.Value!.GetString("note", ""); status.Value = "Reloaded from disk."; } catch (Exception ex) { status.Value = "Reload failed: " + ex.Message; } };
        Action clear = () => { try { store.Value!.Remove("note"); note.Value = ""; status.Value = "Cleared."; } catch (Exception ex) { status.Value = "Clear failed: " + ex.Message; } };

        var form = new BoxEl
        {
            Direction = 1, Gap = 12f,
            Children =
            [
                TextBox.Create(note, options: new TextBox.TextBoxOptions { Header = "Persisted note", Width = 360f }),
                new BoxEl { Direction = 0, Gap = 8f, Wrap = true, AlignItems = FlexAlign.Center, Children = [Button.Accent("Save", save), Button.Standard("Reload", reload), Button.Standard("Clear", clear)] },
                WinApiUi.Field("Local folder", store.Value!.LocalFolder),
            ],
        };

        return ControlExample.Build("App data — settings & folders",
            form,
            description: "AppDataStore is the unpackaged ApplicationData analogue: typed Get/Set (String/Bool/Int/Long/Double/Bytes) persisted under HKCU\\Software\\{publisher}\\{product}, plus Local/Cache/Temp folders. SettingsStore wraps it as write-through Signal<T> for one-line persisted bindings.",
            output: WinApiUi.OutputPanel(status, WinApiUi.Info, log),
            code: """
            var store = AppDataStore.ForUnpackaged("FluentGpu", "Gallery");
            store.SetString("note", text);            // REG_SZ under HKCU
            string saved = store.GetString("note", "");

            // Or reactive write-through (persists on the next flush — no Save button):
            var settings = SettingsStore.ForUnpackaged("FluentGpu", "Gallery", runtime);
            Signal<bool> muted = settings.Bool("muted");   // bind a toggle straight to it
            """);
    }
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// (11) File drop — the cross-cutting OS file/folder DROP ZONE. The DropZone control reveals a dashed accent ring + a
//      "Drop it." overlay WHILE a file hovers (the hand-rolled OLE IDropTarget restores live DragEnter/Over/Leave +
//      the "+Copy" cursor); the drop reads the paths once. DropKinds.Files / FileDropData.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
sealed class FileDropCard : Component
{
    public override Element Render()
    {
        var files = UseSignal("Drag files or folders from Explorer onto the zone.");
        var status = UseSignal("Nothing dropped yet.");
        var count = UseSignal(0);

        // The resting content under the zone (icon + hint). DropZone overlays the dashed "Drop it." cue while a
        // compatible drag hovers — for OS file drags AND in-app drags (both flow through the shared DragDropContext).
        var content = new BoxEl
        {
            Direction = 1, Gap = 8f, MinWidth = 380f, Height = 150f,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Padding = Edges4.All(16), Corners = Radii.ControlAll,
            Fill = Tok.FillSolidBase,   // the DropZone owns the (reactive) border — no static one here
            Children =
            [
                new TextEl(Icons.Download) { Size = 30f, FontFamily = Theme.IconFont, Color = Tok.TextTertiary },
                new TextEl("Drop files / folders here") { Size = 14f, Weight = 600, Color = Tok.TextPrimary },
                new TextEl("") { Size = 12f, Color = Tok.TextSecondary, Text = Prop.Of(() => $"{count.Value} item(s)") },
            ],
        };

        var zone = DropZone.Create(
            accept: new[] { DropKinds.Files },
            onDrop: s =>
            {
                if (s.Payload is FileDropData d)
                {
                    count.Value = d.Count;
                    files.Value = d.Count == 0 ? "(empty)" : string.Join("\n", d.Paths);
                    status.Value = d.Count == 0 ? "Nothing dropped." : $"Dropped {d.Count} path(s)" + (d.AllFolders ? " (all folders)." : ".");
                }
            },
            content: content);

        return ControlExample.Build("File & folder drop — styled DropZone",
            zone,
            description: "DropZone announces droppability by restyling the zone itself — a soft dashed accent ring + glow fade in while a compatible drag is live, and brighten when it hovers (no second labelled panel, so the content's own text never doubles). OS file drags deliver live hover: the Windows backend registers a hand-rolled OLE IDropTarget (DragEnter/Over/Leave → the engine external-drop seam) which also drives the OS \"+Copy\" cursor; the file list is read once, at drop. Lights up for in-app drags too. DropKinds.Files / FileDropData.",
            output: WinApiUi.OutputPanel(status, WinApiUi.Info, files),
            code: """
            DropZone.Create(
                accept: new[] { DropKinds.Files },
                onDrop: s => { if (s.Payload is FileDropData d) Handle(d.Paths); },
                content: body,
                message: "Drop it.");
            // The Windows backend registers a hand-rolled OLE IDropTarget → live hover + the +Copy cursor; the
            // drop reads the file list once. For a custom drag PREVIEW, set DragSource.Style + a DragPreviewLayer.
            """);
    }
}
