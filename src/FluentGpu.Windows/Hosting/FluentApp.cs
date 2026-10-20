using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Media;
using FluentGpu.Media.Codecs.Wic;
using FluentGpu.Pal;
using FluentGpu.Pal.Windows;
using FluentGpu.Rhi;
using FluentGpu.Rhi.D3D12;
using FluentGpu.Scene;
using FluentGpu.Text.DirectWrite;

namespace FluentGpu;

/// <summary>
/// Batteries-included entry point — the whole SDK in one call. <c>FluentApp.Run(() =&gt; new MyApp())</c> creates a
/// DPI-aware window, brings up D3D12, applies Mica + the real system accent, wires the font system + frame loop, and
/// renders your root component. No PAL/RHI/AppHost plumbing to think about — just write components.
/// </summary>
public static class FluentApp
{
    /// <summary>
    /// The live top-level window HWND of the currently-running app, or <see cref="nint.Zero"/> before
    /// <see cref="Run(Func{Component}, AppOptions?)"/> creates the window (and after it
    /// closes). This is the real <c>FluentGpu</c> window handle — the app-layer accessor that
    /// <c>FluentGpu.WindowsApi</c> consumers (SMTC / file pickers / taskbar) pass as their explicit <c>nint hwnd</c>
    /// parameter, so a UI page never has to invent a handle on the Engine seam. UI-thread only (the value is set on the
    /// thread that pumps the window). Single-window by design; the gallery runs exactly one top-level window.
    /// </summary>
    public static nint WindowHandle { get; private set; }

    // The LIVE window-material state, seeded from AppOptions at Run and re-writable through SetWindowMaterialAlt. It is
    // held here rather than read off the captured options record because DwmSetWindowAttribute is re-callable: both the
    // startup apply and the host's re-theme hook must see the CURRENT variant, or a live change would be reverted by the
    // next theme flip.
    private static WindowMaterial s_material = WindowMaterial.Mica;
    private static bool s_composited = true, s_customFrame, s_topmost;
    // The Mica variant SetWindowMaterialAcrylic(false) returns to (seeded from AppOptions.MicaAlt, moved by SetWindowMaterialAlt).
    private static bool s_micaAlt;

    /// <summary>The window's CURRENT system backdrop (<see cref="WindowMaterial"/>): seeded from <see cref="AppOptions.Material"/>
    /// (or the legacy <see cref="AppOptions.Mica"/>), changed by <see cref="SetWindowMaterial"/>.</summary>
    public static WindowMaterial Material => s_host?.WindowMaterial ?? s_material;

    /// <summary>True when the swapchain is composited (a DirectComposition surface bound to the window). Independent of
    /// <see cref="Material"/>; video needs it.</summary>
    public static bool Composited => s_composited;

    /// <summary>Change the LIVE window material (EP9): <see cref="WindowMaterial.None"/> removes the DWM backdrop and the glass and
    /// the frame clears to the theme's opaque ground; the others switch the backdrop. The ground and the DWM write are published
    /// together: the DWM attribute is queued to the presenting thread (after a <c>DwmFlush</c>) and lands immediately before the
    /// present that first shows the new ground, so the backdrop never changes a frame before or after the pixels. The new material
    /// sticks across theme flips. UI-thread only; before the window exists it records the value for the next run. A non-composited
    /// window keeps an opaque ground whatever the material (only its title bar can show a backdrop).</summary>
    public static void SetWindowMaterial(WindowMaterial material)
    {
        s_material = material;
        if (WindowHandle == 0 || s_host is not { } host) return;
        host.SetWindowMaterial(material, Theme.Dark, ordered: true);
    }

    // The sink the host calls (on the UI thread for an immediate apply, on the presenting thread for an ordered one): align to DWM's
    // cycle, then write the dark attribute, backdrop and frame extension together.
    private static void WriteWindowMaterial(nint hwnd, WindowMaterial material, bool dark, bool ordered)
    {
        if (ordered) Win32Theme.AlignToDwm();
        Win32Theme.ApplyWindowMaterial(hwnd, dark, material, s_composited, s_customFrame);
    }

    /// <summary>True when the window is using Mica <b>BaseAlt</b> (a stronger tint of the desktop wallpaper than base
    /// Mica) rather than base Mica. Seeded from <see cref="AppOptions.MicaAlt"/>; changed by
    /// <see cref="SetWindowMaterialAlt"/> / <see cref="SetWindowMaterial"/>.</summary>
    public static bool WindowMaterialAlt => Material == WindowMaterial.MicaAlt;

    /// <summary>Switch the LIVE window between Mica BaseAlt (<paramref name="micaAlt"/> true) and base Mica — the
    /// re-callable half of the startup <c>ApplyWindowMaterial</c>, for an app that exposes the material as a user setting.
    /// The new variant sticks: it also becomes what the host's re-theme hook re-applies on a dark/light flip. UI-thread
    /// only; before the window exists this just records the value (the next <see cref="Run(Func{Component}, AppOptions?)"/>
    /// seeds from <see cref="AppOptions.MicaAlt"/>). No-op when the window is not Mica-backed.</summary>
    public static void SetWindowMaterialAlt(bool micaAlt)
    {
        s_micaAlt = micaAlt;
        if (Material is WindowMaterial.Mica or WindowMaterial.MicaAlt)
            SetWindowMaterial(micaAlt ? WindowMaterial.MicaAlt : WindowMaterial.Mica);
    }

    /// <summary>True when the window shows the desktop-acrylic backdrop instead of Mica. Seeded from
    /// <see cref="AppOptions.Acrylic"/>; changed by <see cref="SetWindowMaterialAcrylic"/> / <see cref="SetWindowMaterial"/>.</summary>
    public static bool WindowMaterialAcrylic => Material == WindowMaterial.Acrylic;

    /// <summary>Switch the LIVE window between desktop acrylic (<paramref name="acrylic"/> true) and Mica (Base or BaseAlt, the
    /// last <see cref="SetWindowMaterialAlt"/> choice) — the acrylic sibling of <see cref="SetWindowMaterialAlt"/>, routed through
    /// <see cref="SetWindowMaterial"/> with the same stickiness across theme flips. UI-thread only; no-op when the window has no
    /// backdrop (<see cref="WindowMaterial.None"/>).</summary>
    public static void SetWindowMaterialAcrylic(bool acrylic)
    {
        if (Material is WindowMaterial.Mica or WindowMaterial.MicaAlt or WindowMaterial.Acrylic)
            SetWindowMaterial(acrylic ? WindowMaterial.Acrylic : s_micaAlt ? WindowMaterial.MicaAlt : WindowMaterial.Mica);
    }

    /// <summary>True while the main window stays above every non-topmost window. Seeded from
    /// <see cref="AppOptions.Topmost"/>; changed by <see cref="SetTopmost"/>.</summary>
    public static bool Topmost => s_topmost;

    /// <summary>Pin the main window above other windows (or release it). Never activates or moves it. UI-thread only; before
    /// the window exists this only records the value.</summary>
    public static void SetTopmost(bool topmost)
    {
        s_topmost = topmost;
        if (s_window is { IsClosed: false } w) w.SetTopmost(topmost);
    }

    /// <summary>The main window's outer rect in physical virtual-screen px (default before the window exists) — what an
    /// app persists to put the window back where the user left it, paired with <see cref="MoveWindowPx"/>.</summary>
    public static RectF WindowBoundsPx => s_window is { IsClosed: false } w ? w.OuterBoundsPx : default;

    /// <summary>Capture the main window as the user sees it — DWM's composite, so the Mica/acrylic backdrop, the DWM frame and
    /// DirectComposition children (video) are in it, unlike the <c>--screenshot</c> swapchain readback — to a PNG at
    /// <paramref name="path"/> (directory created, file overwritten). The visible frame bounds
    /// (<c>DWMWA_EXTENDED_FRAME_BOUNDS</c>, physical px) grown by <paramref name="padPx"/> and clipped to the virtual screen;
    /// a window another window covers is raised topmost for the grab and put back. False (with a <c>[shot-window]</c>
    /// stderr line) before the window exists, when it is minimized or hidden, or when the grab or the write fails.
    /// UI-thread only. The command-line form is <c>--fg shot-window=PATH</c>.</summary>
    public static bool CaptureWindowPng(string path, int padPx = 0)
        => s_window is { IsClosed: false } w && WindowCapture.CaptureToPng(w.Handle.Value, path, padPx);

    /// <summary>Move the main window's outer origin to (<paramref name="x"/>, <paramref name="y"/>) physical virtual-screen
    /// px without resizing or activating it. UI-thread only.</summary>
    public static void MoveWindowPx(float x, float y) => s_window?.MoveToPx(new Point2(x, y));

    /// <summary>Resize the main window's client area to <paramref name="widthDip"/>×<paramref name="heightDip"/> DIP (the
    /// same raw-DPI rule <see cref="AppOptions.Width"/>/<see cref="AppOptions.Height"/> use; app zoom does not scale it).
    /// Works on a window that is not <see cref="AppOptions.Resizable"/>. UI-thread only.</summary>
    public static void ResizeWindow(float widthDip, float heightDip) => s_window?.SetClientSizeDip(widthDip, heightDip);

    // Seeded from AppOptions.SizeToContent at Run; the live value is the host's (the user resizing the frame flips it).
    private static SizeToContent s_sizeToContent;

    /// <summary>Which axes the main window currently sizes to its content (<see cref="AppOptions.SizeToContent"/>). Reads
    /// <see cref="FluentGpu.Hosting.SizeToContent.Manual"/> once the user has resized the frame by hand. UI-thread only.</summary>
    public static SizeToContent SizeToContent => s_host?.SizeToContent ?? s_sizeToContent;

    /// <summary>Size the main window to its content on <paramref name="mode"/>'s axes from the next laid-out frame on, or
    /// stop (<see cref="FluentGpu.Hosting.SizeToContent.Manual"/>). The live sibling of
    /// <see cref="AppOptions.SizeToContent"/> — e.g. re-enable it after the user resized the window. UI-thread only; before
    /// the window exists this only records the value (<see cref="Run(Func{Component}, AppOptions?)"/> seeds it from
    /// <see cref="AppOptions.SizeToContent"/>).</summary>
    public static void SetSizeToContent(SizeToContent mode)
    {
        s_sizeToContent = mode;
        if (s_host is { } h) h.SizeToContent = mode;
    }

    // The LIVE app-zoom state, seeded from AppOptions at Run and re-writable through SetZoom. Held here rather than
    // read off the captured options record because SetZoom is re-callable at runtime (Ctrl+= / Ctrl+- / Ctrl+wheel):
    // every step must see the CURRENT factor, and the window seam (IPlatformWindow.SetZoom) needs a live target.
    private static float s_zoom = 1f;
    private static IPlatformWindow? s_zoomWindow;

    /// <summary>The current app-zoom factor (browser-style Ctrl+= zoom; 1.0 = none). Seeded from
    /// <see cref="AppOptions.Zoom"/>; changed by <see cref="SetZoom"/>. UI-thread only (set on the thread that pumps
    /// the window, like <see cref="WindowHandle"/>).</summary>
    public static float Zoom => s_zoom;

    /// <summary>Raised after <see cref="SetZoom"/> commits a new factor (already <see cref="ZoomLadder"/>-clamped and
    /// de-duplicated) — the persistence hook: subscribe to write the level into settings so the next launch seeds
    /// <see cref="AppOptions.Zoom"/> with it. Invoked on the UI thread.</summary>
    public static event Action<float>? ZoomChanged;

    /// <summary>Set the LIVE app-zoom factor — the re-callable half of the startup <see cref="AppOptions.Zoom"/> seed,
    /// for the app's zoom chords (Ctrl+= / Ctrl+- / Ctrl+0 / Ctrl+wheel) and any settings UI. Clamped to the
    /// <see cref="ZoomLadder"/> range; a no-change write is dropped (no window poke, no event). Pushes the factor into
    /// the live window (<c>IPlatformWindow.SetZoom</c> re-derives the effective Scale and relays out), then raises
    /// <see cref="ZoomChanged"/>. UI-thread only; before the window exists this just records the value (the next
    /// <see cref="Run(Func{Component}, AppOptions?)"/> seeds from <see cref="AppOptions.Zoom"/>).</summary>
    public static void SetZoom(float zoom)
    {
        zoom = ZoomLadder.Clamp(zoom);
        if (zoom == s_zoom) return;
        s_zoom = zoom;
        s_zoomWindow?.SetZoom(zoom);
        ZoomChanged?.Invoke(zoom);
    }

    /// <summary>
    /// Relay of the host's single-instance activation-redirect event (a second app launch's deep-link payload forwarded
    /// to this running instance). Forwarded from <c>AppHost.ActivationRedirected</c> while a run is active and delivered
    /// on the UI thread, so handlers may write signals that re-render. App-layer relay (not an Engine-seam accessor) so
    /// page/app code can subscribe without holding the <c>AppHost</c> instance.
    /// </summary>
    public static event Action<string>? ActivationRedirected;

    /// <summary>
    /// Relay of the host's taskbar thumbnail-toolbar click event (button id). Forwarded from
    /// <c>AppHost.ThumbButtonClicked</c> while a run is active and delivered on the UI thread, so handlers may write
    /// signals that re-render. App-layer relay so page/app code can subscribe without holding the <c>AppHost</c>.
    /// </summary>
    public static event Action<int>? ThumbButtonClicked;

    /// <summary>
    /// Relay of the host's app-navigation command — a mouse's side buttons (XButton1/2) or a keyboard Back/Forward key.
    /// Payload is <c>0 = Back</c>, <c>1 = Forward</c>. Forwarded from <c>AppHost.AppNavigationCommand</c> while a run is
    /// active and delivered on the UI thread, so handlers may navigate and write signals directly.
    /// </summary>
    public static event Action<int>? AppNavigationCommand;

    /// <summary>
    /// Relay of the host's <c>TaskbarButtonCreated</c> event (explorer created or re-created this window's taskbar
    /// button). Forwarded from <c>AppHost.TaskbarButtonCreated</c> on the UI thread. Thumbnail-toolbar callers re-add
    /// buttons here after an explorer restart.
    /// </summary>
    public static event Action? TaskbarButtonCreated;

    /// <summary>
    /// Relay of the host's OS color-settings-change event (Windows app dark/light flip or accent change), delivered on the
    /// UI thread at the top of a frame. App-layer relay (not an Engine-seam accessor) so page/app code can react —
    /// typically re-reading <see cref="SystemIsDark"/> while it follows the OS — without holding the <c>AppHost</c>.
    /// </summary>
    public static event Action? SystemColorsChanged;
    /// <summary>The host's per-rendered-frame stats (phase times, fps), relayed on the UI thread. See
    /// <see cref="AppHost.FrameCompleted"/>. Handlers must be cheap: they run inside the frame.</summary>
    public static event Action<FrameStats>? FrameCompleted;

    /// <summary>True when the OS "app" theme is Light (Settings ▸ Colors). The app-layer facade over the Win32 reader so
    /// composition-root code (e.g. seeding the initial theme from a "System" preference) stays free of PAL imports.
    /// Defaults to FALSE (dark) when unreadable, matching the engine default.</summary>
    public static bool SystemUsesLightTheme() => Win32Theme.SystemUsesLightTheme();

    /// <summary>True when the TASKBAR is light — Settings ▸ Colors ▸ "Choose your default Windows mode" (registry
    /// <c>SystemUsesLightTheme</c>), which is NOT the app mode <see cref="SystemUsesLightTheme"/> reads: the Windows 11
    /// "Custom" combination pairs a dark taskbar with light apps. Anything drawn on the taskbar (a notification-area
    /// glyph) must follow this one. Defaults to FALSE (dark) when unreadable — the taskbar was always dark before the
    /// value existed. A change arrives with <see cref="SystemColorsChanged"/> (the same <c>ImmersiveColorSet</c>
    /// broadcast) and, while the window is hidden, as <c>NotifyIconEvent.ShellChanged</c> on a notification icon.</summary>
    public static bool TaskbarUsesLightTheme() => Win32Theme.TaskbarUsesLightTheme();

    // ── window lifecycle (close veto, hide/show, state relay) ───────────────────────────────────────────────────────
    private static Win32Window? s_window;

    /// <summary>
    /// Asked on the UI thread before the main window closes — the caption close button, Alt+F4, the system menu's Close,
    /// <see cref="CloseWindow"/>. Return <see langword="true"/> to keep the window (you handled the request: typically
    /// <see cref="SetWindowVisible"/>(false) to live on in the notification area); <see langword="false"/> or no handler
    /// lets it close and the run end. A <see cref="CloseReason.SessionEnding"/> request (logoff, shutdown, the Restart
    /// Manager) is reported but cannot be vetoed. Settable before or during <see cref="Run(Func{Component}, AppOptions?)"/>.
    /// An explicit "Quit" verb records its intent (a latch the handler reads) BEFORE calling <see cref="CloseWindow"/>, so
    /// the handler lets that one close through instead of hiding.
    /// </summary>
    public static Func<CloseReason, bool>? CloseRequested { get; set; }

    /// <summary>Ask the main window to close — the same request the caption close button makes, so it goes through
    /// <see cref="CloseRequested"/> first (posted: returns at once, the close lands on the next pump). A no-op before the
    /// window exists or after it closed. Callable from any thread.</summary>
    public static void CloseWindow() => s_window?.CloseWindow();

    /// <summary>True while the main window is shown (false before it exists, after it closed, or while hidden by
    /// <see cref="SetWindowVisible"/> / <see cref="AppOptions.StartHidden"/>). Live read of the window. UI-thread.</summary>
    public static bool WindowVisible => s_window is { IsClosed: false } w && w.IsVisible;

    /// <summary>Show or hide the main window without closing it. Hidden, the window leaves the screen, the taskbar and
    /// Alt+Tab; the frame loop parks exactly as if minimized (no reconcile, layout or present; <c>UseIsActive</c> false)
    /// while playback, timers on other threads, posts and OS events keep running. Showing brings it back in the placement
    /// it had (a window hidden while minimized comes back minimized — restore it yourself). Idempotent; a no-op without
    /// a window. UI-thread only. An explicit call takes visibility over from <see cref="SizeToContent"/>'s show-when-fitted
    /// (a window the app hid during mount stays hidden).</summary>
    public static void SetWindowVisible(bool visible)
    {
        // Before the no-op early-out: hiding a window that is still hidden waiting for its first fit must cancel that show.
        s_host?.CancelShowWhenFitted();
        if (s_window is not { IsClosed: false } w || w.IsVisible == visible) return;
        if (visible)
        {
            // A show-from-hidden under RevealOnFirstPresent repaints the frame into the hidden window and shows it on its present
            // (re-armed reveal exemption), instead of showing whatever the swapchain last held. Idempotent while one is pending.
            if (s_reveal is { } gate && s_host is { } host) { if (!host.IsRevealPending) host.BeginRevealGate(gate); }
            else w.Show();
        }
        else w.Hide();
    }

    /// <summary>Relay of <c>AppHost.WindowStateChanged</c>: the main window was minimized, restored, maximized, hidden or
    /// shown (the <see cref="WindowStateChange"/> flags say which; several can be true at once). Raised on the UI thread
    /// once per frame that observed a change, parked frames included — the minimize-to-tray edge. The first frame only
    /// seeds; read <see cref="WindowVisible"/> for the current state.</summary>
    public static event Action<WindowStateChange>? WindowStateChanged;

    /// <summary>The current OS accent color (Settings ▸ Colors), preferring the <c>Light2</c> shade WinUI uses for the
    /// dark-theme accent fill, else the base accent; null when unreadable. The app-layer facade over the Win32 reader.</summary>
    public static ColorF? SystemAccent()
        => Win32Theme.AccentLight2() is { } a ? ColorF.FromRgba(a.R, a.G, a.B)
         : Win32Theme.Accent() is { } b ? ColorF.FromRgba(b.R, b.G, b.B)
         : null;

    /// <summary>The FULL OS accent ramp (<c>SystemAccentColor</c> + <c>Light1..3</c> + <c>Dark1..3</c>) read via
    /// <c>IUISettings3.GetColorValue</c>, so accent brushes resolve THEME-AWARE (the WinUI Dark1 shade in light, Light2
    /// in dark) instead of one flat color reused in both themes. Null when unreadable — callers then fall back to
    /// <see cref="SystemAccent"/> + <c>Tok.SetAccent</c> (which derives an approximate ramp). App-layer facade over the
    /// Win32/WinRT reader.</summary>
    public static AccentRamp? SystemAccentRamp() => Win32Theme.ReadAccentRamp();

    /// <summary>
    /// Optional diagnostic-harness hook. When it is set and returns <see langword="true"/>, it has taken over the run
    /// (e.g. a soak / stress longevity probe) and the normal interactive frame loop below is skipped. Kept as a generic
    /// seam so the engine entry point carries no dependency on any app-specific harness: the gallery installs its
    /// <c>SoakProbe</c> here, selected by its <c>--soak</c> / <c>--stress-*</c> / <c>--wake-audit</c> arguments. Left <see langword="null"/>
    /// for normal apps. UI-thread only.
    /// </summary>
    public static Func<AppHost, IPlatformWindow, IGpuDevice, bool>? DiagnosticRun;

    /// <summary>Run the app: create a DPI-aware window, bring up D3D12 + Mica + the real OS accent, wire the font system
    /// and frame loop, and render <paramref name="root"/>. Pass <paramref name="options"/> to set the window title/size,
    /// Mica variant, custom frame, ambient-fps throttle, and warm-cadence hold; omit it for the defaults.</summary>
    public static void Run(Func<Component> root, AppOptions? options = null)
        => RunCore(root, options ?? new AppOptions(), new HarnessOptions());

    /// <summary><c>FluentApp.Run&lt;MyApp&gt;()</c> — same, for a parameterless root component.</summary>
    public static void Run<T>(AppOptions? options = null) where T : Component, new()
        => Run(() => new T(), options);

    // The single implementation. Public entry points (Run) and the diagnostic harness (FluentAppHarness.Run) both route
    // here; splitting the interactive options (AppOptions) from the test/diagnostic knobs (HarnessOptions: frames,
    // screenshot, frame-wait) keeps the everyday surface a one-liner while the harness owns the deterministic controls.
    //
    // THE UI THREAD IS A DEDICATED THREAD WITH A REAL STACK, not the process main thread. The frame loop records the
    // scene on the UI thread (AppHost.RunFrame → SceneRecorder.Record), and that walk is recursive with a large frame
    // (~21.5 KB optimized; a Debug JIT frame is several times that). On the apphost's default 1.5 MB main-thread stack
    // the recorder's stack-headroom guard tripped at ~68 levels in Release and far shallower in Debug — and it degrades by
    // NOT PAINTING the deepest subtree: a Debug build showed every detail page's row skins with no row content, no hero,
    // no top tracks, for a whole session, silently. The main thread's reserve is baked into the apphost/PE header and is
    // not reachable from managed code (no runtimeconfig knob; DOTNET_DefaultStackSize only shapes threads WE create), so
    // the loop runs on a thread we create with a 32 MB reserve: reserve only (committed on demand ⇒ no cost), ~1,500
    // levels at the Release frame size, hundreds in Debug — the guard becomes the last-resort net it was meant to be
    // (SceneRecordStats.DepthAborts stays 0). Same path in Debug, Release and NativeAOT.
    //
    // STA: Wavee's Main is [STAThread] because file pickers / SMTC / taskbar are STA-only coclasses — the thread that
    // owns the window and the pump must be STA too, so we set it before Start. Everything the callers do BEFORE Run
    // (single-instance gate, protocol registration, the ActivationRedirected subscription) stays on Main; ThreadGuard
    // binds roles per thread at runtime, so nothing in the engine assumes the process main thread. An exception on the
    // UI thread is captured and rethrown on the caller so unhandled-exception logging / exit codes behave as before.
    internal const int UiThreadStackBytes = 32 * 1024 * 1024;

    /// <summary>The main window's <see cref="WindowDesc"/> for <paramref name="o"/> — the one AppOptions → platform mapping
    /// (title, size, composition (EP9: <see cref="AppOptions.Composited"/>, else the legacy Mica rule), custom frame, min size,
    /// zoom and the overlay shape Topmost / ToolWindow / Resizable).
    /// Factored out of <see cref="RunCore"/> so the mapping lives in one place.</summary>
    internal static WindowDesc DescribeWindow(AppOptions o, bool composited) => new(
        o.Title,
        new Size2(o.Width, o.Height),
        1f,
        composited,
        CustomFrame: o.CustomFrame,
        MinClientSizeDip: new Size2(o.MinWidth, o.MinHeight),
        Zoom: ZoomLadder.Clamp(o.Zoom),
        Topmost: o.Topmost,
        ToolWindow: o.ToolWindow,
        Resizable: o.Resizable);

    internal static void RunCore(Func<Component> root, AppOptions o, HarnessOptions h)
    {
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--audio-host") >= 0)
            throw new InvalidOperationException("FluentApp.Run must not be reached in --audio-host child mode.");

        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        var ui = new Thread(() =>
        {
            try { RunCoreOnUiThread(root, o, h); }
            catch (Exception ex) { failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex); }
        }, UiThreadStackBytes)
        {
            Name = "fgpu-ui",
            IsBackground = false,   // the process lives exactly as long as the UI loop
        };
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        ui.Join();
        failure?.Throw();
    }

    // The frame-loop body proper — everything from DPI awareness through the message pump — on the dedicated UI thread.
    private static void RunCoreOnUiThread(Func<Component> root, AppOptions o, HarnessOptions h)
    {
        try { RunLifetime.Run(lifetime => RunCoreOnUiThreadBody(root, o, h, lifetime), ClearRunState); }
        finally { ClearRunState(); }   // safety if entering the lifetime itself failed
    }

    private static void ClearRunState()
    {
        WindowHandle = 0;
        s_zoomWindow = null; s_zoom = 1f;
        s_window = null;
        s_host = null;
        s_reveal = null;
        s_gpuDevice = null;
    }

    private static void RunCoreOnUiThreadBody(Func<Component> root, AppOptions o, HarnessOptions h, RunLifetime lifetime)
    {
        // The engine's diagnostic switches come from THIS process's command line (`--fg name,...`, EngineSwitches) —
        // never from the environment — and are applied once, before anything below reads them.
        EngineSwitches.Apply(Environment.GetCommandLineArgs());

        // Diagnostic A/B only (`--fg opaque`): remove the DWM-Mica / premultiplied-composition path as ONE variable. This
        // creates the ordinary opaque HWND flip-model swapchain, letting PresentMon tell us whether DWM composition is
        // the cadence bottleneck on the current build. It is not a product backdrop decision.
        if (EngineSwitches.OpaqueWindow)
        {
            o = o with { Mica = false, Material = WindowMaterial.None, Composited = false };
            Console.Error.WriteLine("[window] --fg opaque — DWM Mica disabled; using opaque HWND swapchain");
        }

        bool consoleDiagnostics = EngineSwitches.DiagConsole;
        if (consoleDiagnostics)
            Diag.Sink = Console.Error.WriteLine;   // engine diagnostics -> console (Debug/FLUENTGPU_DIAG only)

        // `--fg diag` cold-start attribution: phase deltas to stderr, runtime-gated so the published Release binary can
        // report its own bring-up. "sinceStart" anchors at OS process creation (includes CreateProcess + runtime init).
        long bootPrev = System.Diagnostics.Stopwatch.GetTimestamp();
        void BootStamp(string label)
        {
            if (!consoleDiagnostics) return;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            Console.Error.WriteLine($"[boot] {label}: +{(now - bootPrev) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F1}ms");
            bootPrev = now;
        }
        if (consoleDiagnostics)
        {
            using var proc = System.Diagnostics.Process.GetCurrentProcess();
            Console.Error.WriteLine($"[boot] runcore-entry: sinceProcessStart={(DateTime.Now - proc.StartTime).TotalMilliseconds:F1}ms");
        }

        Win32ThreadCycles.Install();   // the engine's per-thread cycle seam (UI-gap decomposition, render worst-present split)
        // The platform media backends as the engine's DEFAULTS: MediaPlayer.Create()/Build() and UseVideo then play clear video
        // with no registration of their own (an explicit WithBackend still wins; the MF backend is built on the first open).
        MediaRouter.SetDefaultRegistrar(static router =>
            router.RegisterDefault(MediaKind.MfVideoOrFile, static () => new FluentGpu.Media.Windows.MfMediaPlayer()));
        var strings = new StringTable();
        var app = lifetime.Own(new Win32App());
        // customFrame: the app draws its own WinUI TitleBar (caption stripped, engine caption buttons, snap layouts) —
        // an explicit opt-in (the gallery): apps without a TitleBar keep the standard OS frame.
        // EP9: material and composition are two decisions. The material is the explicit Material or the legacy Mica/MicaAlt/Acrylic flags;
        // composited defaults to the legacy rule (on with Mica) unless the app says otherwise - a composited window with no
        // backdrop (WindowMaterial.None + an opaque ground) is the supported way to keep video without Mica.
        WindowMaterial material = o.Material ?? WindowMaterialPolicy.FromLegacy(o.Mica, o.MicaAlt, o.Acrylic);
        bool composited = o.Composited ?? o.Mica;
        var plan = WindowMaterialPolicy.Resolve(material, composited, o.CustomFrame);
        if (!plan.VideoCapable) Console.Error.WriteLine("[window] composited = false: video playback is unavailable (it needs a composited swapchain)");
        var window = (Win32Window)app.CreateWindow(DescribeWindow(o, composited));
        // The screenshot harness's capture surface: the host lays out and rasterises at SurfaceSizeDip × SurfaceScale px
        // whatever the monitor allows (before the host exists, so the first frame and the swapchain are already that size).
        if (h.Screenshot is not null && h.SurfaceSizeDip is { Width: > 0f, Height: > 0f } surface)
        {
            float s = h.SurfaceScale > 0f ? h.SurfaceScale : 1f;
            window.PinSurface(new Size2(surface.Width * s, surface.Height * s), s);
            Console.Error.WriteLine($"[window] capture surface pinned at {surface.Width}x{surface.Height} DIP, scale {s}");
        }
        BootStamp("create-window");
        // Publish the real top-level HWND so app-layer callers (the Windows-APIs page: SMTC / pickers / taskbar) can pass
        // it as their explicit nint hwnd — the host accessor, not an Engine-seam invention. Cleared when the run ends.
        WindowHandle = window.Handle.Value;
        // Seed the live material state (see SetWindowMaterialAlt) — every later apply reads these, not the options record.
        s_material = material; s_composited = composited; s_customFrame = o.CustomFrame; s_micaAlt = o.MicaAlt; s_topmost = o.Topmost;
        // Seed the live app-zoom state (see SetZoom): the window was CREATED at the clamped factor above, so the first
        // frame already lays out at the user's persisted level — keep the statics in agreement with it.
        s_zoom = ZoomLadder.Clamp(o.Zoom);
        s_zoomWindow = window;
        // The close veto reads the LIVE static on every request, so a handler set before Run or during it both apply.
        s_window = window;
        window.CloseRequested = static reason => CloseRequested is { } veto && veto(reason);

        // Prefer the exact OS ramp (theme-aware accent fills); fall back to the base accent (Tok.SetAccent derives a ramp).
        if (Win32Theme.ReadAccentRamp() is { } ramp) Tok.SetAccent(in ramp);
        else if (Win32Theme.AccentLight2() is { } a) Theme.Accent = ColorF.FromRgba(a.R, a.G, a.B);
        else if (Win32Theme.Accent() is { } b) Theme.Accent = ColorF.FromRgba(b.R, b.G, b.B);
        Win32Theme.ApplyWindowMaterial(window.Handle.Value, Theme.Dark, s_material, s_composited, s_customFrame);
        if (o.ToolWindow) Win32Theme.ApplyRoundCorners(window.Handle.Value);
        if (plan.TransparentGround) Theme.WindowBackground = ColorF.Transparent;
        BootStamp("accent+material");

        // Text measurement runs through DirectWrite (the same design advances + line-break math the D3D12 GlyphRenderer
        // uses to render), so measured wrap/height matches rendered wrap/height exactly. (GDI measure is retired here.)
        var fonts = new DirectWriteFontSystem(strings);
        BootStamp("directwrite-fonts");
        IGpuDevice device = new D3D12Device(strings, composited: composited, debugLayer: o.D3D12DebugLayer, dred: o.D3D12Dred);
        BootStamp("d3d12device-ctor");
        // Bring the ADAPTER up here, not at the first CreateSwapchain inside the AppHost constructor below. The three
        // budgets on the next few lines are captured once and read GpuProfile.Tier, which only InitDevice publishes —
        // so until this call existed they all ran while Tier == Unknown, which the contract defines as NOT weak, and
        // every one of them took the discrete branch on a UMA machine. The ctor above allocates nothing and proves
        // nothing about the adapter; this is the line that makes "the device exists" true. Idempotent — CreateSwapchain
        // skips it, and device-loss recovery still re-runs InitDevice.
        (device as D3D12Device)?.EnsureDeviceCreated();
        BootStamp("d3d12device-init");
        // The LOCAL segment sample is only meaningful once EnsureDeviceCreated has forced the adapter (and its first
        // PublishVideoMemorySnapshot) up, same reasoning as the Tier read above — read here, not before. 0 (device
        // does not support the query, or the sample truly is not ready) falls back to the flat weak default inside
        // GpuMemoryBudgets.For; it never blocks image-pipeline construction.
        device.TryGetVramUsage(out _, out long localBudgetBytes);
        var budgets = GpuMemoryBudgets.For(GpuProfile.IsWeak, localBudgetBytes);

        // Real image pipeline: WIC constrained decode on a worker pool, behind a disk-cached HTTP/2 fetcher.
        if (o.ImageCacheDirectory is { Length: > 0 }) SweepLegacyImageCache();
        var imageFetcher = lifetime.Own(new DefaultImageFetcher(
            http: o.ImageHttpHandler is { } wrap ? DefaultImageFetcher.CreateClient(wrap) : null,
            diskCache: new DiskImageCache(o.ImageCacheDirectory)));
        // ONE bounded CPU pixel pool for the whole pipeline: decode BGRA buffers (workers) + async-upload copies (UI)
        // share one budget (media-pipeline.md §3 staging blocks, as built).
        var pixelPool = new PixelBufferPool(budgets.PixelPool);
        var imageDecoder = lifetime.Own(new DecodeScheduler(new WicImageCodec(), imageFetcher,
            new DecodeOptions { PixelPool = pixelPool }));
        var images = new ImageCache(imageDecoder, ImageCacheBudgetBytes(budgets.ImageCache, GpuProfile.IsWeak, o.ImageCacheMegabytes),
            budgets.Derived, weak: GpuProfile.IsWeak);
        BootStamp("image-pipeline");

        var host = lifetime.Own(new AppHost(app, window, device, fonts, strings, root(), images,
            initialSceneCapacity: o.InitialSceneCapacity));
        BootStamp("apphost-ctor");
        host.PixelPool = pixelPool;   // before the first RunFrame
        // Adaptive GPU pacing (default on): pace continuous motion to a sustainable rate when MEASURED on-GPU
        // execution proves the panel rate is out of reach at this size. Independent of cadence — it is evidence, not policy.
        host.AdaptiveGpuPacing = o.AdaptiveGpuPacing;
        // Post-input warm-cadence hold (G1b): keep rendering ~WarmCadenceMs after the last input so a follow-up
        // interaction pays no cold-start ramp. 0 disables the hold (see AppHost.WarmCadenceHoldMs).
        host.WarmCadenceHoldMs = o.WarmCadenceMs;
        host.RenderCensus = o.RenderCensus;
        s_sizeToContent = o.SizeToContent;
        host.SizeToContent = o.SizeToContent;   // before the first frame: its layout is the first fit
        s_host = host;

        // Relay the host's UI-thread single-instance redirect to the app-layer static event (the Windows-APIs page
        // subscribes there). Forwarding the payload, not the handler chain — handlers attach to FluentApp.ActivationRedirected.
        Action<string> forwardActivation = uri => ActivationRedirected?.Invoke(uri);
        host.ActivationRedirected += forwardActivation;
        Action<int> forwardThumbClick = id => ThumbButtonClicked?.Invoke(id);
        host.ThumbButtonClicked += forwardThumbClick;
        Action<int> forwardAppNav = which => AppNavigationCommand?.Invoke(which);
        host.AppNavigationCommand += forwardAppNav;
        Action forwardTaskbarCreated = () => TaskbarButtonCreated?.Invoke();
        host.TaskbarButtonCreated += forwardTaskbarCreated;
        Action<WindowStateChange> forwardWindowState = change => WindowStateChanged?.Invoke(change);
        host.WindowStateChanged += forwardWindowState;

        // Live re-theme: on every theme change the host re-applies the OS window material so DWM's immersive-dark titlebar
        // and the Mica system backdrop flip to the new theme's variant (instant — the OS can't cross-fade its backdrop;
        // the in-app content cross-fades). Mirrors the one-shot startup ApplyWindowMaterial above. Reads the host's LIVE material
        // (SetWindowMaterial, SetWindowMaterialAlt and a theme transaction all move it) so a change survives every later theme flip.
        host.OnApplyThemeMaterial = dark => Win32Theme.ApplyWindowMaterial(window.Handle.Value, dark, host.WindowMaterial, s_composited, s_customFrame);
        // EP9: the ordered writer SetWindowMaterial uses (queued to the presenting thread, DWM-aligned).
        host.SeedWindowMaterial(material);
        host.WindowMaterialSink = (m, dark) => WriteWindowMaterial(window.Handle.Value, m, dark, ordered: true);
        // Relay the host's UI-thread OS color-settings-change to the app-layer static event (the app subscribes to follow
        // the OS dark-mode/accent live while its theme mode is "System").
        Action forwardSystemColors = () => SystemColorsChanged?.Invoke();
        host.SystemColorsChanged += forwardSystemColors;
        Action<FrameStats> forwardFrame = stats => FrameCompleted?.Invoke(stats);
        host.FrameCompleted += forwardFrame;
        if (EngineSwitches.GpuPassTiming) host.GpuPassTimingEnabled = true;   // `--fg gpu-timing`

        // `--fg alloc-types`: bring up the per-type allocation profiler (process-global EventListener; the host drives
        // its once-per-second report on the frame cadence). Stopped in the finally so headless/short runs don't leak it.
        bool allocTypes = EngineSwitches.AllocTypes;
        if (allocTypes) AllocTypeProfiler.Start();

        // `--fg mem` GPU residency hooks: surface tracked D3D12 resource totals + a glyph/texture-store summary
        // (no-op unless the census is also enabled; headless devices leave these null).
        // Also the [fps] line's latW/opgrp source below: both counters live on the device and are not carried in FrameStats.
        D3D12Device? gpuDev = device as D3D12Device;
        if (gpuDev is { } gpu)
        {
            host.GpuResources = () => gpu.DiagResourceTotals;
            host.GpuDetail = () => gpu.DiagGpuDetail;
            s_gpuDevice = gpu;
        }
        // The frame ledger's platform seams (process cycles, process memory, VRAM, glyph atlas) — installed always, read only while
        // it records; `--fg ledger[=PATH]` turns it on from the first frame (FrameLedger.cs).
        Win32LedgerSampler.Install(gpuDev);
        if (EngineSwitches.Ledger) FrameLedger.Enable();

        // StartHidden: the window exists (HWND, swapchain, host, the mounted tree) but is never shown, so the first frame
        // is already parked — the loop blocks on messages until SetWindowVisible(true), and that show edge paints.
        // RevealOnFirstPresent (EP8): the window stays hidden until its first frame has presented with the app's fonts ready (or the
        // watchdog fires) - no white/black flash, no fallback-glyph first frame. It needs a composited swapchain (a non-composited
        // target stands a hidden window's presents down), so a non-Mica window keeps today's show-first behaviour.
        s_reveal = null;
        if (o.RevealOnFirstPresent && composited)
            s_reveal = new RevealGateOptions { FirstFrameReady = o.FirstFrameReady, TimeoutMs = o.RevealTimeoutMs };
        else if (o.RevealOnFirstPresent)
            Console.Error.WriteLine("[reveal] RevealOnFirstPresent ignored: the window is not composited (a hidden window's presents are stood down)");
        // SizeToContent (not Manual) owns the first show instead: the window stays hidden until the first fit landed
        // (AppHost.ShowWhenFitted) so it never flashes at the AppOptions size; with Manual ShowWhenFitted shows at once.
        if (o.RevealOnFirstPresent && s_reveal is not null && o.SizeToContent != SizeToContent.Manual)
        {
            s_reveal = null;
            Console.Error.WriteLine("[reveal] RevealOnFirstPresent ignored: SizeToContent shows the window after its first fit");
        }
        if (o.StartHidden) { }
        else if (s_reveal is { } gate) host.BeginRevealGate(gate);
        else host.ShowWhenFitted();
        BootStamp(o.StartHidden ? "window-start-hidden" : s_reveal is not null ? "window-reveal-gate" : "window-show");

        // Optional diagnostic-harness takeover (the gallery's SoakProbe longevity / leak-hunt + targeted-stress modes,
        // selected by the gallery's command line). Installed via FluentApp.DiagnosticRun; when it handles the run it
        // returns true and we skip the interactive loop, returning to the clean shutdown below. Null for normal apps.
        // Pair with `--fg d3d-mem` for the per-resource [d3d-mem] create/release trace.
        if (DiagnosticRun is { } diag && diag(host, window, device)) return;

        bool fpsLog = EngineSwitches.FpsLog;   // `--fg fps`: periodic [fps] readout to stderr (frame-rate / frame-ms diagnosis)
        int n = 0;
        // FrameMs/Fps time the UI loop. PresentFps comes from the host's successful-swapchain-present counter in every
        // submit mode, so async coalescing and inline mode are both represented truthfully.
        // Present-path diagnosis (maximize → 60fps): watch the swapchain size + window state so a resize emits a one-shot
        // [fps resize] marker (WxH, scale, state, panel Hz, wait-kind), and every [fps] line carries the wait-kind/ms the
        // loop paced by (Ambient = software 60 cap; DisplayRate/Pace = panel rate → a lock is downstream in Present/GPU).
        float lastLoggedW = -1f, lastLoggedH = -1f;
        int cachedHz = fpsLog ? window.CurrentRefreshHz() : 0;
        int spikeCluster = 0;
        bool prevSpike = false;
        // Deltas, not levels: PresentedSequence/FramesSkippedSubmit/PublishSequence are monotonic counters, and the
        // question a cadence investigation asks is always "how many since the last line".
        ulong prevPresentSeq = 0, prevPublishSeq = 0, prevGpuPassLogSeq = 0;
        var gpuPasses = new FluentGpu.Rhi.GpuPassTiming[FluentGpu.Rhi.GpuPassTimeline.MaxPasses];   // once; the per-line copy is zero-alloc
        long prevSkipped = 0, prevDeclined = 0, prevStoodDown = 0;
        long prevFpsLineQpc = System.Diagnostics.Stopwatch.GetTimestamp();
        FluentGpu.Rhi.D3D12.DamageCensus prevDamage = gpuDev?.LastDamageCensus ?? default;
        var prevInputPacing = window.InputPacingSnapshot;
        static string WaitTok(FluentGpu.Hosting.HostWaitKind k) => k switch
        {
            FluentGpu.Hosting.HostWaitKind.Idle => "idle",
            FluentGpu.Hosting.HostWaitKind.Hud => "hud",
            FluentGpu.Hosting.HostWaitKind.Baked => "baked",
            FluentGpu.Hosting.HostWaitKind.Cadence => "cadence",
            FluentGpu.Hosting.HostWaitKind.AdaptiveGpu => "adaptive-gpu",
            FluentGpu.Hosting.HostWaitKind.PowerCap => "power-cap",
            FluentGpu.Hosting.HostWaitKind.DisplayTick => "tick",
            FluentGpu.Hosting.HostWaitKind.SoftwarePace => "swpace",
            FluentGpu.Hosting.HostWaitKind.DisplayRate => "display",
            _ => "?",
        };
        static string RectSubmitTok(AppHost host)
        {
            Span<RectSubmittedAreaItem> top = stackalloc RectSubmittedAreaItem[8];
            if (!host.TryCopyRectSubmittedAreaSample(top, out RectSubmittedAreaSample area)) return "";
            // rq + sequence are always available on D3D. --fg render adds rareaMp/top-N. Area is submitted nominal
            // transformed physical megapixels, NOT coverage: clipping/overlap are not removed. rareaSeq identifies the
            // one coherent TARGET submit for every token here; btop entries are ordinal:areaMp:alpha:localWxH:flagsHex.
            var sb = new System.Text.StringBuilder(224);
            sb.Append(System.FormattableString.Invariant($" rq{area.OpaqueInstances}/{area.BlendedInstances}"));
            if (area.HasArea)
                sb.Append(System.FormattableString.Invariant(
                    $" rareaMp={area.OpaquePx2 / 1_000_000.0:0.###}/{area.BlendedPx2 / 1_000_000.0:0.###}"));
            sb.Append(System.FormattableString.Invariant($" rareaSeq={area.Sequence}"));
            int n = area.HasArea ? area.TopCount : 0;
            if (n > 0) sb.Append(" btop=");
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append(',');
                RectSubmittedAreaItem x = top[i];
                sb.Append(System.FormattableString.Invariant(
                    $"{x.Ordinal}:{x.AreaPx2 / 1_000_000.0:0.###}:{x.EffectiveAlpha:0.###}:{x.LocalW:0.#}x{x.LocalH:0.#}:{(byte)x.Flags:X}"));
            }
            return sb.ToString();
        }
        // `--fg shot-window=PATH`: the composed-window capture, armed at the first presented frame (null = not asked for).
        using WindowShot? windowShot = WindowShot.FromSwitches(window, window.Handle.Value);
        while (!window.IsClosed)
        {
            host.RunFrame();
            host.TickDetachedHosts();   // pop-out video windows: one frame each on this same UI+render thread
            n++;
            if (fpsLog)
            {
                var s = host.LastStats;
                double gpuMs = host.LastGpuFenceWaitMs;
                var szpx = window.ClientSizePx;
                if (fpsLog && (szpx.Width != lastLoggedW || szpx.Height != lastLoggedH))
                {
                    lastLoggedW = szpx.Width; lastLoggedH = szpx.Height;
                    cachedHz = window.CurrentRefreshHz();   // once per size change, not per frame
                    Console.Error.WriteLine($"[fps resize] {szpx.Width}x{szpx.Height} scale {window.Scale:0.##} state {window.State} panel {cachedHz}Hz wait {WaitTok(host.LastWaitKind)}{host.LastWaitMs} (f{n})");
                }
                bool workSpike = (s.FlushMs + s.LayoutMs + s.RecordMs) > 11.0;
                // gpuMs (LastGpuFenceWaitMs) goes stale when submits are elided (skip-submit / pace-skip), so gate the
                // render-side spike on the frame actually presenting; scale the threshold with refresh so ordinary
                // vsync-pacing waits at 120Hz (~8.33ms → 12.5ms trip) aren't flagged, staying 11ms at 60Hz.
                double vsyncMs = cachedHz > 0 ? 1000.0 / cachedHz : 8.33;
                double gpuThreshold = Math.Max(11.0, vsyncMs * 1.5);
                bool spike = workSpike || (s.Presented && gpuMs > gpuThreshold);   // UI work OR a real render-thread GPU stall on a presented frame
                if (spike)
                {
                    spikeCluster = prevSpike ? spikeCluster + 1 : 1;
                    prevSpike = true;
                }
                else
                {
                    spikeCluster = 0;
                    prevSpike = false;
                }
                // Emit on every SCROLL-ACTIVE frame, not just every 30th: a fixed frame stride samples a 10-second
                // gesture about 20 times, which cannot support a percentile and will miss a one-frame stall entirely.
                // The line is built and written outside RunFrame, so its allocation never touches the hot phases.
                if (fpsLog && (spike || s.ScrollActive || n % 30 == 0))
                {
                    // latW splits the always-printed `gpu` number (LastGpuFenceWaitMs conflates the frame fence with the
                    // swapchain latency waitable), so it is ungated exactly like `gpu`; opgrp counts the full-window layer
                    // composites, printed with the pass timeline (host.GpuPassTimingEnabled — a runtime toggle).
                    double latWaitMs = gpuDev?.LastLatencyWaitMs ?? 0.0;
                    string gpuExecutionTok = host.TryGetGpuRenderSample(out GpuRenderSample gpuExecutionSample)
                        ? System.FormattableString.Invariant(
                            $" gexec {gpuExecutionSample.ExecutionMs:0.0}ms#{gpuExecutionSample.Sequence} gexecAge={gpuExecutionSample.SubmitAge}")
                        : "";
                    string gpuRenderTok = "";
                    if (host.GpuPassTimingEnabled)
                    {
                        int passCount = host.CopyGpuPassTimeline(gpuPasses, out FluentGpu.Rhi.GpuPassFrameSummary passSummary);
                        if (passCount > 0 && passSummary.Sequence != prevGpuPassLogSeq)
                        {
                            prevGpuPassLogSeq = passSummary.Sequence;
                            var pb = new System.Text.StringBuilder(64 + passCount * 24);
                            pb.Append(System.FormattableString.Invariant($" gpass {passSummary.WholeMs:0.00}ms/{passCount}p("));
                            for (int pi = 0; pi < passCount; pi++)
                            {
                                ref readonly var gp = ref gpuPasses[pi];
                                if (pi > 0) pb.Append(',');
                                pb.Append(gp.Kind).Append('@').Append(gp.TargetWidthPx).Append('x').Append(gp.TargetHeightPx)
                                  .Append(':').Append(gp.Ms.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
                            }
                            pb.Append(System.FormattableString.Invariant($") bb{passSummary.BackBufferTransitions}"));
                            gpuRenderTok = pb.ToString();
                        }
                    }
                    // rq + optional submitted-area/top-N are one target-owned coherent snapshot. Async logger repeats
                    // carry the same rareaSeq and downstream summaries dedupe them as one backend submit.
                    string rectSubmitTok = RectSubmitTok(host);
                    // tiles r<rastered>/s<scheduled> e<exposedMissing> dg<degraded> res<MiB>/<budget MiB> — the retained-tile
                    // census of the latest composite turn (always-on counters): how many tiles re-rastered and why a frame
                    // cost what it did, plus the one number that must stay 0 (a visible tile that composited nothing).
                    // dmg <coverage%>/<rects> — the repaint set the host published (what Present1's dirty rects carry).
                    // fx<effect slices>/f<folded> ac<acrylic slices>/fb<acrylic fallbacks> grp<rendered>/h<cache hits> — the
                    // slice partition (an acrylic fallback means a surface lost its frost) and the group-surface cache.
                    var tc = host.LastTileCensus;
                    string tilesTok = System.FormattableString.Invariant(
                        $" tiles r{tc.Rastered}/s{tc.Scheduled} e{tc.ExposedMissing} st{tc.StaleTiles} dg{tc.DegradedSlices} res{tc.ResidentBytes / 1048576.0:0.0}/{tc.BudgetBytes / 1048576.0:0}MiB need{tc.VisibleNeedBytes / 1048576.0:0.0} fx{tc.EffectSlices}/f{tc.Folded} ac{tc.AcrylicSlices}/fb{tc.AcrylicFallbacks} grp{tc.GroupSurfaces}/h{tc.GroupCacheHits}");
                    string dmgTok = s.RepaintFullReason != FluentGpu.Rhi.RepaintFullReason.None
                        ? $" dmg F:{s.RepaintFullReason}"
                        : (s.RepaintRectCount > 0 ? $" dmg {s.RepaintCoverage * 100f:0.0}%/{s.RepaintRectCount}" : "");
                    string clusterTok = spike && spikeCluster > 0 ? $" cluster={spikeCluster}" : "";
                    // dpx — the damage census since the previous line, per composite (kpx): r<tile px rastered>/<whole-tile
                    // equivalent> (partial/whole raster counts), c<back-buffer px recomposited>, p<Present1 dirty px>,
                    // d<this turn's own dirty px> (c − d is the buffer-age union), full<whole-frame composites>/<composites>,
                    // and with --fg damage-validate v<checked>/<mismatched>/h<honesty breaches> (cumulative); with --fg
                    // present-validate pv<composites checked>/m<Present1 presents checked against the screen model>/c<composite
                    // mismatches outside the repaint region>/u<under-reports>/s<stale screens>/k<skipped>/w<unverified frames:
                    // the shadow disagreed inside the repaint region>. The composite check shares the frame's prepared inputs (tiles, group / blur / backdrop surfaces): it validates the
                    // repaint set and the Present1 rects, not a wrong retained-surface key hit, a "no raster needed" table
                    // decision or a same-frame descriptor fault.
                    string damageTok = "";
                    if (gpuDev is not null)
                    {
                        var dc = gpuDev.LastDamageCensus;
                        long dn = dc.Frames - prevDamage.Frames;
                        double per = dn > 0 ? 1.0 / (dn * 1000.0) : 0.0;
                        damageTok = System.FormattableString.Invariant(
                            $" dpx r{(dc.RasterPx - prevDamage.RasterPx) * per:0.0}/{(dc.RasterWholePx - prevDamage.RasterWholePx) * per:0.0}k({dc.PartialRasters - prevDamage.PartialRasters}p/{dc.WholeRasters - prevDamage.WholeRasters}w) c{(dc.CompositePx - prevDamage.CompositePx) * per:0.0}k p{(dc.PresentPx - prevDamage.PresentPx) * per:0.0}k d{(dc.DirtyPx - prevDamage.DirtyPx) * per:0.0}k full{dc.FullFrames - prevDamage.FullFrames}/{dn}")
                            + (FluentGpu.Render.Tiles.TileDamage.Validate ? $" v{dc.Validated}/{dc.Mismatches}/h{dc.HonestyBreaches}" : "")
                            + (FluentGpu.Render.Tiles.TileDamage.PresentValidate && gpuDev.LastPresentCensus is var pc
                                ? $" pv{pc.Checked}/m{pc.ModelChecked}/c{pc.CompositeMismatches}/u{pc.UnderReports}/s{pc.StaleScreens}/k{pc.Skipped}/w{pc.ShadowDiverged}" : "");
                        prevDamage = dc;
                    }
                    // layout X.X(fx A eff B conn C rf D) — the four passengers of the layout bucket (they sum to it):
                    // fx = the flex solve, eff = DrainLayoutEffects, conn = ConnectedAnimation.Tick65, rf = enter/exit
                    // reflow seeding. Printed only when the bucket is worth splitting (≥0.1 ms), so quiet frames stay short.
                    string layoutSplitTok = s.LayoutMs >= 0.1
                        ? $"(fx{s.LayoutSolveMs:0.0} eff{s.LayoutEffectsMs:0.0} conn{s.ConnectedTickMs:0.0} rf{s.ReflowSeedMs:0.0})"
                        : "";
                    var sm = s.SpanReuseMisses;
                    string spanMissTok = sm != default
                        ? $" smiss=gd{sm.GlobalDisabled}/sb{sm.ScopedBlocked}/ed{sm.ExactDirty}/ek{sm.ExactKey}/ec{sm.ExactClip}/cap{sm.ExactCapacity}"
                        : "";
                    string hitchTok =
                        $" | hitch comps={s.ComponentsRendered} nodes={s.NodesVisited}/{s.DrawNodeCount} " +
                        $"pump={s.ImagePumpMs:0.0}ms apply={s.ImageApplyCount}/{s.ImageApplyBytes / 1024}KB realize={s.RealizeCatchupMs:0.0}ms " +
                        $"escapes={s.RootRelayoutEscapes} escLoc={s.LocalRelayoutResolves} " +
                        $"spans={s.SpansReused}/{s.SpansReRecorded} slices={s.Slices.Slices}(w{s.Slices.Walked}/k{s.Slices.Kept}/{s.Slices.BytesRecorded / 1024}KB) " +
                        $"reasons=0x{((uint)s.SpanReuseDisabledReasons):X}{spanMissTok} gc0=+{s.Gc0Delta} gc1=+{s.Gc1Delta} gc2=+{s.Gc2Delta}";
                    // Read the LIVE host properties, not the FrameStats copies: five early-out paths in RunFrame
                    // construct `new FrameStats(0, ..., Rendered: false)` and leave both of these at 0, which is the
                    // mechanical reason idle/minimized stretches have always printed "present 0fps seq=0" — a
                    // construction artifact that reads exactly like a total present stall.
                    ulong presentSeq = host.PresentedSequence, publishSeq = host.PublishSequence;
                    ulong consumedSeq = host.ConsumedSequence;
                    long skipped = host.FramesSkippedSubmit;
                    long stoodDown = host.FramesStoodDown;   // covered/cloaked Present skips — kept OUT of skipD
                    // Saturating, because these are UNSIGNED counters that do not track each other exactly: a present
                    // can happen with nothing newly acquired (the previous frame stays on screen), so presentSeq may
                    // legitimately run ahead of publishSeq. An unguarded subtraction would wrap to ~1.8e19 and read as
                    // a catastrophic backlog.
                    static ulong Behind(ulong ahead, ulong behind) => ahead > behind ? ahead - behind : 0UL;
                    // declD = RunFrames that dispatched input but produced no frame because one was already produced
                    // for the current compositor tick (production is one frame per tick). In steady scrolling coal
                    // should sit at ~0: every produced frame is presented.
                    long declined = host.ProductionDeclines;
                    long fpsLineQpc = System.Diagnostics.Stopwatch.GetTimestamp();
                    double fpsLineSec = Math.Max(0.000001,
                        (fpsLineQpc - prevFpsLineQpc) / (double)System.Diagnostics.Stopwatch.Frequency);
                    ulong presentDelta = Behind(presentSeq, prevPresentSeq);
                    double presentNow = presentDelta / fpsLineSec;
                    var inputPacing = window.InputPacingSnapshot;
                    string seamTok =
                        $" presentD={presentDelta} pubD={Behind(publishSeq, prevPublishSeq)} " +
                        $"coal={Behind(publishSeq, presentSeq)} lag={Behind(publishSeq, consumedSeq)} " +
                        $"ack={host.RenderPresentSeq} skipD={skipped - prevSkipped} sdD={stoodDown - prevStoodDown} declD={declined - prevDeclined}";
                    string inputPaceTok =
                        $" | motion msgD={inputPacing.MotionMessages - prevInputPacing.MotionMessages}" +
                        $" moveD={inputPacing.MoveEvents - prevInputPacing.MoveEvents}" +
                        $" coalD={inputPacing.CoalescedMoveEvents - prevInputPacing.CoalescedMoveEvents}" +
                        $" deadlineD={inputPacing.DeadlineWakes - prevInputPacing.DeadlineWakes}" +
                        $" urgentD={inputPacing.UrgentBreaks - prevInputPacing.UrgentBreaks}";
                    prevPresentSeq = presentSeq; prevPublishSeq = publishSeq; prevSkipped = skipped; prevDeclined = declined; prevStoodDown = stoodDown;
                    prevFpsLineQpc = fpsLineQpc; prevInputPacing = inputPacing;
                    Console.Error.WriteLine(
                        $"[fps] tMs={System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0.000}{(spike ? " SPIKE" : "")}{clusterTok}" +
                        $"{(s.ScrollActive ? " scroll" : "")} loop {s.Fps:0}fps {s.FrameMs:0.0}ms " +
                        $"(flush{s.FlushMs:0.0} rx{s.ReactiveFlushMs:0.0}/vr{s.VirtualRealizeMs:0.0} layout{s.LayoutMs:0.0}{layoutSplitTok} " +
                        $"anim{s.AnimMs:0.0} record{s.RecordMs:0.0} submit{s.SubmitMs:0.0}) | presentNow {presentNow:0}fps present1s {host.PresentFps:0}fps seq={presentSeq}{seamTok} " +
                        $"gpu {gpuMs:0.0}ms latW{latWaitMs:0.0}{gpuExecutionTok}{gpuRenderTok}{rectSubmitTok}{tilesTok}{dmgTok}{damageTok} | wait {WaitTok(host.LastWaitKind)}{host.LastWaitMs} " +
                        $"{szpx.Width}x{szpx.Height}@{cachedHz}Hz (f{n}){hitchTok}{inputPaceTok}");
                }
            }
            if (windowShot is not null && windowShot.Tick(host.PresentedSequence)) break;   // `shot-exit`: captured, end the run
            if (h.Frames > 0 && n >= h.Frames) break;
            if (h.Screenshot != null)
                window.WaitForWork(h.FrameWaitMs);   // deterministic ~8ms/frame so time-driven animations advance (and never block)
            else
            {
                // Low-rate wake pacing: idle/minimized block until a message (0% CPU); a HUD-only readout throttles to
                // ~10 Hz; real animation/scroll/decode paces at the display rate. WaitForWork returns early on input,
                // so responsiveness is identical at every timeout. (See AppHost.RecommendedWaitMs.) Folded across any
                // detached video windows, so a playing pop-out keeps the loop live even while the main window is idle.
                PlatformWaitRequest wait = host.WaitRequestWithDetached();
                int waitMs = wait.TimeoutMs;
                long waitStart = System.Diagnostics.Stopwatch.GetTimestamp();
                window.WaitForWork(in wait);
                // The UI-gap decomposition's wait segment: wall time inside the wait vs the timeout it asked for
                // (AppHost.UiGap.cs) — a wait that overran its own timeout is a thread that was not scheduled.
                host.NoteLoopWait(waitStart, System.Diagnostics.Stopwatch.GetTimestamp(), waitMs);
            }
        }

        if (allocTypes) AllocTypeProfiler.Stop();   // tear down the EventListener (no leak past the run)
        if (EngineSwitches.Ledger) FrameLedger.DumpIfRequested();   // `--fg ledger=PATH`: the binary + one CSV per stream
        Win32LedgerSampler.Uninstall(gpuDev);

        // --screenshot: read the last-rendered back buffer back to CPU and write a PNG for visual fidelity diffing.
        if (h.Screenshot is { } shotPath && device is D3D12Device d3d)
        {
            host.QuiesceRenderThread();   // async (the default): stop the render thread so CaptureBgra (a UI-thread GPU op) is the sole GPU owner
            var px = d3d.CaptureBgra(out int cw, out int ch);
            // The back buffer is PREMULTIPLIED and a Mica window's chrome and layer fills are see-through: dropping the alpha
            // byte composited them over black (the grey "dim" every capture showed). Flatten over the theme's solid base, the
            // material's own fallback, which is what the window shows wherever DWM's backdrop does not reach.
            PngWriter.WriteBgra(shotPath, px, cw, ch, backdrop: Tok.T.WindowBackground);
            Console.Error.WriteLine($"screenshot: wrote {shotPath} ({cw}x{ch})");
        }

    }

    private static AppHost? s_host;
    private static RevealGateOptions? s_reveal;   // non-null while RevealOnFirstPresent gates every show-from-hidden
    private static D3D12Device? s_gpuDevice;

    /// <summary>The engine's live-object census (scene nodes, strings, decoded-image bytes, components, bindings,
    /// animation tracks, pixel pool) captured NOW — passive O(1) reads, UI thread only. Null before the window is up
    /// or after it closed. An app's memory sampler pairs it with the process working set and its own owners to
    /// attribute a heap.</summary>
    public static CensusSnapshot? EngineCensus() => s_host is { } h ? CensusSnapshot.Capture(h) : null;

    /// <summary>Tracked D3D12 resource residency (bytes, resource count) — the GPU half the engine census excludes.
    /// Null headless or before the window is up.</summary>
    public static (long Bytes, int Count)? GpuResidency() => s_host?.GpuResources is { } f ? f() : null;

    /// <summary>The live image cache, so an app's memory governor can register it as a sheddable arena. Trimming it
    /// drops unpinned (off-screen) images and keeps every pinned one, which makes it the cheapest thing an app can
    /// give back under pressure. Null before the window is up or after it closed.</summary>
    public static ImageCache? EngineImages => s_host?.Images;

    /// <summary>The running host's <see cref="AppHost.IsInteractionQuiet"/> (true when no host runs). UI thread.</summary>
    public static bool IsInteractionQuiet => s_host?.IsInteractionQuiet ?? true;

    /// <summary>The running host's <see cref="AppHost.IsSettledForPause"/> (true when no host runs). UI thread.</summary>
    public static bool IsSettledForPause(long interactionQuietMs) => s_host?.IsSettledForPause(interactionQuietMs) ?? true;

    /// <summary>Compact per-class GPU residency fragment (top tracked-resource classes, render-target pool
    /// occupancy, upload-arena counters) — see <see cref="D3D12Device.DiagGpuCensusLine"/>. Pairs with <see
    /// cref="GpuResidency"/> in an app's memory sampler: `gpu bytes=… resources=…` plus this fragment appended
    /// verbatim. Null headless or before the window is up.</summary>
    public static string? GpuCensusLine() => s_gpuDevice?.DiagGpuCensusLine;


    /// <summary>One best-effort delete of the engine's legacy default image cache (<c>%TEMP%\fluent-gpu\imgcache</c>),
    /// run only when the app supplied its own <see cref="AppOptions.ImageCacheDirectory"/>. An app that moved its cache
    /// under its own data root would otherwise leave the TEMP copy behind forever — bytes it no longer reads, does not
    /// account for in its storage page, and cannot clear from its own UI. Failure is silence: the directory may be in
    /// use by another FluentGpu process that has NOT moved its cache, and losing a cache is never worth a crash.</summary>
    private static void SweepLegacyImageCache()
    {
        try
        {
            string legacy = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fluent-gpu", "imgcache");
            if (System.IO.Directory.Exists(legacy)) System.IO.Directory.Delete(legacy, recursive: true);
        }
        catch { /* locked / partially removed / read-only — the stale cache is harmless, a throw here is not */ }
    }

    /// <summary>The tier's image-cache cap (<see cref="GpuMemoryBudgets"/> owns the tier decision), with the
    /// discrete-only host override (<see cref="AppOptions.ImageCacheMegabytes"/>) applied on top. The override must
    /// never be able to raise a weak adapter back over the cap the
    /// Adreno hang work put there. <paramref name="weak"/> is the same tier flag <see cref="GpuMemoryBudgets.For"/>
    /// was called with — checked explicitly rather than by comparing <paramref name="tierBudget"/> against the flat
    /// <see cref="GpuMemoryBudgets.ImageCacheWeak"/> constant, now that a weak tier's cap can also legitimately land
    /// at 32 or 64 MB once it is derived from the LOCAL segment.</summary>
    private static long ImageCacheBudgetBytes(long tierBudget, bool weak, int overrideMb)
    {
        if (weak) return tierBudget;
        if (overrideMb is >= 16 and <= 1024) return (long)overrideMb * 1024 * 1024;
        return tierBudget;
    }
}

/// <summary>
/// The everyday window/app options for <see cref="FluentApp.Run(Func{Component}, AppOptions?)"/>: window title + size,
/// Mica material, custom frame, the ambient-fps power throttle, and the post-input warm-cadence hold. Every field has a
/// flagship default, so <c>new AppOptions { Title = "…" }</c> overrides only what it names.
/// </summary>
public sealed record AppOptions
{
    /// <summary>Window title (caption / taskbar).</summary>
    public string Title { get; init; } = "FluentGpu";
    /// <summary>Initial client width (DIP).</summary>
    public int Width { get; init; } = 800;
    /// <summary>Initial client height (DIP).</summary>
    public int Height { get; init; } = 600;
    /// <summary>Optional minimum client width while interactively resizing, in DIP. 0 keeps the platform default.</summary>
    public int MinWidth { get; init; }
    /// <summary>Optional minimum client height while interactively resizing, in DIP. 0 keeps the platform default.</summary>
    public int MinHeight { get; init; }
    /// <summary>Apply the DWM Mica system backdrop (window becomes transparent to it). False = an opaque window.</summary>
    public bool Mica { get; init; } = true;
    /// <summary>Use Mica BaseAlt (the flatter File-Explorer tint) instead of the default Mica Base.</summary>
    public bool MicaAlt { get; init; }

    /// <summary>The window's system backdrop (EP9). Null (the default) keeps the legacy flags: <see cref="Mica"/> /
    /// <see cref="MicaAlt"/> / <see cref="Acrylic"/> on = Mica / Mica Alt / Acrylic, <see cref="Mica"/> off = <see cref="WindowMaterial.None"/>. Set it for
    /// <see cref="WindowMaterial.Acrylic"/> or to say <see cref="WindowMaterial.None"/> while keeping a composited swapchain
    /// (an opaque ground, no DWM backdrop, video still works). Change it live with <see cref="FluentApp.SetWindowMaterial"/>.</summary>
    public WindowMaterial? Material { get; init; }

    /// <summary>Whether the swapchain is composited (a DirectComposition surface bound to the window). Null (the default) keeps
    /// the legacy rule: composited iff <see cref="Mica"/>. Independent of the material: <c>Composited = true</c> with
    /// <see cref="WindowMaterial.None"/> is allowed; video playback needs a composited window (logged when it is off); a
    /// non-composited window has an opaque ground whatever the material and no <see cref="RevealOnFirstPresent"/>.</summary>
    public bool? Composited { get; init; }
    /// <summary>Show the desktop-acrylic backdrop (a blur of whatever is behind the window) instead of Mica (the legacy-flag
    /// form of <c>Material = WindowMaterial.Acrylic</c>; ignored when <see cref="Material"/> is set). Needs <see cref="Mica"/>
    /// (the composited backdrop path); changed live by <see cref="FluentApp.SetWindowMaterialAcrylic"/>.</summary>
    public bool Acrylic { get; init; }
    /// <summary>The app draws its own title bar (OS caption stripped; engine caption buttons + snap layouts).</summary>
    public bool CustomFrame { get; init; }
    /// <summary>Keep the window above other windows from creation on (an overlay); changed live by
    /// <see cref="FluentApp.SetTopmost"/>.</summary>
    public bool Topmost { get; init; }
    /// <summary>No taskbar button and no Alt+Tab entry (a desktop widget that lives in the notification area). The
    /// window keeps the Win11 rounded corners.</summary>
    public bool ToolWindow { get; init; }
    /// <summary>False = the user cannot resize or maximize the window (code still can, through
    /// <see cref="FluentApp.ResizeWindow"/>). Default true.</summary>
    public bool Resizable { get; init; } = true;
    /// <summary>Size the window to its content on these axes (WPF <c>SizeToContent</c>): after every frame whose layout
    /// ran, the client area follows the content's natural size (DPI and app zoom included), clamped to
    /// <see cref="MinWidth"/>/<see cref="MinHeight"/> and the monitor work area. <see cref="Width"/>/<see cref="Height"/>
    /// are then only the size the first layout happens at; the window is shown after its first fit (unless
    /// <see cref="StartHidden"/>), so it never flashes at that size. The user resizing the frame switches it to
    /// <see cref="FluentGpu.Hosting.SizeToContent.Manual"/>; changed live by <see cref="FluentApp.SetSizeToContent"/>.
    /// Default Manual.</summary>
    public SizeToContent SizeToContent { get; init; }
    /// <summary>Adaptive GPU pacing (default <c>true</c>): when MEASURED whole-frame on-GPU execution proves the panel
    /// rate is unsustainable at the current window size, pace continuous motion to a steady sustainable cadence rather
    /// than thrashing into vblank misses. Self-releasing, and it never paces a genuine interaction. Set <c>false</c> to
    /// remove the governor (a capture that must see the raw cadence). Maps to <see cref="AppHost.AdaptiveGpuPacing"/>.</summary>
    public bool AdaptiveGpuPacing { get; init; } = true;
    /// <summary>Post-input warm-cadence hold (ms): after the last input, keep rendering this long before allowing full
    /// quiesce so a follow-up interaction pays no cold-start ramp (G1b / research #10). 0 disables the hold. Maps to
    /// <see cref="AppHost.WarmCadenceHoldMs"/>.</summary>
    public float WarmCadenceMs { get; init; } = 1000f;
    /// <summary>Keep the engine's per-component render census on for the whole session so every frame whose flush
    /// exceeds the panel's refresh interval arrives at <see cref="FluentApp.FrameCompleted"/> with
    /// <c>FrameStats.Census</c> populated (top components by time and by bytes). One dictionary op per component
    /// render while on. Maps to <see cref="AppHost.RenderCensus"/>.</summary>
    public bool RenderCensus { get; init; }
    /// <summary>Where the disk image cache (decoded-once album art / remote images) lives. Null keeps the engine
    /// default, <c>%TEMP%\fluent-gpu\imgcache</c>, which is fine for a sample but wrong for a shipping app: it is
    /// outside the app's own data root, so it survives an uninstall, escapes the app's storage accounting, and can be
    /// swept by disk-cleanup mid-session. An app that owns a data folder should point this at it (Wavee uses
    /// <c>%LOCALAPPDATA%\Wavee\cache\images</c>). When set, the engine deletes the legacy TEMP directory once,
    /// best-effort, so an upgrading install does not leave the old cache stranded.</summary>
    public string? ImageCacheDirectory { get; init; }
    /// <summary>Wraps the image fetcher's transport — the seam an app uses to OBSERVE every image request (a logging
    /// <see cref="System.Net.Http.DelegatingHandler"/>). It receives the engine's own configured handler and returns
    /// the one the client is built on; null keeps the engine's handler as is. The engine logs nothing itself.</summary>
    public Func<System.Net.Http.HttpMessageHandler, System.Net.Http.HttpMessageHandler>? ImageHttpHandler { get; init; }
    /// <summary>Initial app-zoom factor (browser-style Ctrl+= zoom; 1.0 = none). Seed it from persisted settings so the
    /// FIRST frame lays out at the user's level — no visible re-zoom after startup. Clamped to the
    /// <see cref="ZoomLadder"/> range before reaching the window; live changes go through
    /// <see cref="FluentApp.SetZoom"/>.</summary>
    public float Zoom { get; init; } = 1f;
    /// <summary>Create the window but do not show it: the run starts parked (no frame is produced, the tree is mounted
    /// and its effects run) until <see cref="FluentApp.SetWindowVisible"/>(true). For a launch the user did not click —
    /// the sign-in start of an app that lives in the notification area. Deciding WHEN to start hidden is the app's
    /// (only the startup activation, never a Start-menu click); the engine only honours it.</summary>
    public bool StartHidden { get; init; }

    /// <summary>Keep the window hidden until its first frame has PRESENTED with <see cref="FirstFrameReady"/> true, then show it
    /// (EP8): the first thing the user sees is the final frame, with the OS material and dark attribute already applied. The same
    /// gate re-arms for every later <see cref="FluentApp.SetWindowVisible"/>(true) (tray restore): the frame is repainted into
    /// the hidden window and shown on its present. Requires a composited window (<see cref="Mica"/>); ignored (and logged)
    /// otherwise. A watchdog (<see cref="RevealTimeoutMs"/>) shows the window anyway. Default false (show first, as before).</summary>
    public bool RevealOnFirstPresent { get; init; }

    /// <summary>The fonts-ready predicate of <see cref="RevealOnFirstPresent"/>: true once the first screen's fonts are loaded
    /// (pass the font registry's readiness). Evaluated on the UI thread every frame while the reveal is pending. Null = ready
    /// at once.</summary>
    public Func<bool>? FirstFrameReady { get; init; }

    /// <summary>The watchdog of <see cref="RevealOnFirstPresent"/> (ms, default 750): the window is shown anyway when a ready
    /// frame has not presented by then.</summary>
    public int RevealTimeoutMs { get; init; } = 750;

    /// <summary>Item E: the scene store's (and the render seam's snapshot slots') starting node capacity, and the
    /// floor cold maintenance will never trim it back below. 0 (the default) keeps the engine's own small default (64,
    /// no floor) — fine for a sample, but an app whose first REAL scene is known to be large should size this up
    /// front: measured cold-start churn on a first scroll/navigation otherwise doubles the column arrays repeatedly
    /// (2048→4096→…→8192 nodes), landing on the LOH and driving gen2 collections during exactly the frames a user's
    /// first impression is formed. Maps to the internal <c>AppHost</c> ctor's <c>initialSceneCapacity</c> parameter.</summary>
    public int InitialSceneCapacity { get; init; }

    /// <summary>Arm the D3D12 debug layer and mirror its validation messages (errors/warnings/corruption) to stderr — a
    /// host option set in code (the gallery maps its <c>--d3d12-debug</c> argument here). Needs the Windows "Graphics
    /// Tools" optional feature; the device says so out loud when the layer is unavailable. Off by default.</summary>
    public bool D3D12DebugLayer { get; init; }
    /// <summary>Force DRED auto-breadcrumbs + page-fault reporting on (device-removed forensics). Off by default.</summary>
    public bool D3D12Dred { get; init; }
    /// <summary>Discrete-adapter image-cache cap override in MiB (16..1024; 0 keeps the tier budget). Ignored on a weak
    /// adapter, whose cap is the Adreno hang-work budget.</summary>
    public int ImageCacheMegabytes { get; init; }
}

/// <summary>
/// The deterministic diagnostic knobs for <see cref="FluentAppHarness.Run"/> (test / screenshot / visual-diff loops):
/// a fixed frame count, a screenshot output path, and the per-frame wait. Separate from <see cref="AppOptions"/> so the
/// everyday <see cref="FluentApp.Run(Func{Component}, AppOptions?)"/> surface never sees them.
/// </summary>
public sealed record HarnessOptions
{
    /// <summary>Stop after this many frames (&gt; 0); -1 (the default) runs interactively until the window closes.</summary>
    public int Frames { get; init; } = -1;
    /// <summary>When set, read the last-rendered back buffer to a PNG at this path after the run (visual-diff). The frame
    /// loop then paces at <see cref="FrameWaitMs"/> so time-driven animations advance deterministically.</summary>
    public string? Screenshot { get; init; }
    /// <summary>Per-frame wait (ms) used while a <see cref="Screenshot"/> is pending — deterministic settle pacing.</summary>
    public int FrameWaitMs { get; init; } = 8;
    /// <summary>With <see cref="Screenshot"/>: the capture surface's size in DIP. Set, the host lays out and renders at this
    /// size (× <see cref="SurfaceScale"/> px) whatever the window and the monitor allow, so a review capture can show a
    /// 2560- or 3840-DIP layout on a smaller screen. The window on screen shows the surface's top-left corner; input is not
    /// mapped to it. Empty (the default) keeps the window's own size.</summary>
    public Size2 SurfaceSizeDip { get; init; }
    /// <summary>The capture surface's DPI scale (px per DIP before app zoom); default 1. Read only with <see cref="SurfaceSizeDip"/>.</summary>
    public float SurfaceScale { get; init; } = 1f;
}

/// <summary>
/// The diagnostic / test entry point: <see cref="FluentApp.Run(Func{Component}, AppOptions?)"/> with the deterministic
/// controls (frame count, screenshot, frame-wait) exposed via <see cref="HarnessOptions"/>. The gallery's
/// <c>--frames</c> / <c>--screenshot</c> arms and the screenshot visual-diff loop route through here; everyday apps use
/// <see cref="FluentApp.Run(Func{Component}, AppOptions?)"/>.
/// </summary>
public static class FluentAppHarness
{
    /// <summary>Run <paramref name="root"/> with the given window <paramref name="options"/> and diagnostic
    /// <paramref name="harness"/> controls.</summary>
    public static void Run(Func<Component> root, AppOptions? options = null, HarnessOptions? harness = null)
        => FluentApp.RunCore(root, options ?? new AppOptions(), harness ?? new HarnessOptions());
}
