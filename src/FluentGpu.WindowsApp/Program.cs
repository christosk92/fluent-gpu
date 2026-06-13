using FluentGpu;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static FluentGpu.Dsl.Ui;

// Shared context channel: the parent provides the live count; nested components read it via UseContext.
static class Demo
{
    public static readonly Context<int> Count = new(0);
}

// A nested, independently-rendered component that reads the count from context (no props threading).
sealed class CountDisplay : Component
{
    public override Element Render() => Heading($"Count: {UseContext(Demo.Count)}");
}

// A nested component with its OWN local state. On toggle, ONLY the icon glyph + its color change (the reconciler
// diffs and patches just that node); the color fades grey→red over 200ms via UseAnimatedValue (re-renders until settled).
sealed class LikeButton : Component
{
    public override Element Render()
    {
        var (liked, setLiked) = UseState(false);
        // bouncy scale pop on toggle — a composited spring on this button's node (no per-frame re-render);
        // the heart colour fades via the value hook. Toggle ⇒ deps change ⇒ the spring retargets (keeps velocity).
        UseSpring(AnimChannel.ScaleX, liked ? 1.12f : 1f, SpringParams.FromResponse(0.28f, 0.45f), liked);
        UseSpring(AnimChannel.ScaleY, liked ? 1.12f : 1f, SpringParams.FromResponse(0.28f, 0.45f), liked);
        float t = UseAnimatedValue(liked ? 1f : 0f, 200f);
        var fg = ColorF.Lerp(ColorF.FromRgba(0xC5, 0xC5, 0xC5), ColorF.FromRgba(0xE8, 0x11, 0x23), t);
        return Button.Standard(liked ? "♥ Liked" : "♡ Like", () => setLiked(!liked),
            Button.StandardStyle with { Foreground = fg });
    }
}

// The demo: flex layout + nested components + UseContext + accent/standard buttons + the system accent + Mica.
sealed class DemoApp : Component
{
    public override Element Render()
    {
        var (count, setCount) = UseState(0);
        // entrance: the whole UI fades + slides up on mount (composited transition on the root node).
        UseTransition(AnimChannel.Opacity, 0f, 1f, 350f, Easing.EaseOut, "enter");
        UseTransition(AnimChannel.TranslateY, 14f, 0f, 350f, Easing.EaseOut, "enter");
        return new BoxEl
        {
            Direction = 1,
            Gap = 16,
            Padding = new Edges4(28, 24, 28, 28),
            Children =
            [
                Heading("FluentGpu — Fluent demo"),

                // Nested component reads the count across the component boundary via context.
                Ctx.Provide(Demo.Count, count, Embed.Comp(() => new CountDisplay())),

                // A row of controls: accent +/-, a neutral reset, a custom-styled button, and a self-stateful Like button.
                HStack(8,
                    Button.Accent("-", () => setCount(count - 1)),
                    Button.Accent("+", () => setCount(count + 1)),
                    Button.Standard("Reset", () => setCount(0)),
                    Button.Accent("Save", () => { }, new Button.Style
                    {
                        Background = ColorF.FromRgba(0x10, 0x7C, 0x10),
                        Foreground = ColorF.FromRgba(0xFF, 0xFF, 0xFF),
                        BorderBrush = GradientSpec.Solid(ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x20)),
                        HoverBackground = ColorF.FromRgba(0x16, 0x95, 0x16),
                        PressedBackground = ColorF.FromRgba(0x0B, 0x5A, 0x0B),
                        CornerRadius = 6f,
                    }),
                    Embed.Comp(() => new LikeButton())),

                Text("Accent · standard · custom-styled · nested components · UseContext · hover/press · Mica.")
                    .Foreground(ColorF.FromRgba(0xB0, 0xB0, 0xB0)),
            ],
        };
    }
}

static class Program
{
    // [STAThread]: the gallery UI thread must be a single-threaded apartment, the conventional GUI-app apartment
    // (WinForms/WPF are STA for exactly this reason). The shell common-item dialog (IFileOpenDialog/IFileSaveDialog →
    // IModalWindow.Show, FilePicker.cs) is an STA-only (ThreadingModel=Apartment) coclass: its Show() does OLE init /
    // RegisterDragDrop and runs a nested modal loop with STA pump semantics, so invoking it from this thread (which owns
    // the composited window and runs the message pump in FluentApp.Run) requires STA. Without this attribute the thread
    // defaults to MTA and Show() wedges (the Dialogs-pillar crash). STA is also what the Shell pillar already assumes —
    // TaskbarManager/JumpList CoInitializeEx(APARTMENTTHREADED) on this thread and merely tolerate the MTA-degraded path
    // today. The Media (SMTC RoInitialize SINGLETHREADED), Notifications (toast activator has an E_INVALIDARG→non-AGILE
    // CoRegisterClassObject fallback that works in any apartment), and Network pillars are apartment-agnostic or run on a
    // separate MTA reader thread (NetworkStatus.MtaReader), so the MTA→STA flip is safe across the whole WindowsApi surface.
    [STAThread]
    static void Main(string[] args)
    {
        // ── FluentGpu.WindowsApi validation harness (runs BEFORE the window/GPU stack spins up). ──────────────────────
        // The spawned single-instance child mode MUST be first: it is a second process that should never touch the
        // window path — it acquires the gate, forwards its activation payload via WM_COPYDATA, and exits with a code the
        // parent asserts on.
        int childIdx = Array.IndexOf(args, "--windowsapi-smoke-child");
        if (childIdx >= 0 && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
        {
            string payload = childIdx + 1 < args.Length ? args[childIdx + 1] : string.Empty;
            Environment.Exit(WindowsApiSmoke.RunChild(payload));
            return;
        }
        // The parent smoke: headless console-style [PASS]/[FAIL] checks over all four pillars; exit code = failure count.
        if (Array.IndexOf(args, "--windowsapi-smoke") >= 0 && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
        {
            Environment.Exit(WindowsApiSmoke.Run());
            return;
        }
        // Headless NLM-off-thread deadlock probe (regression gate for the "Windows APIs" page hang). Runs the exact
        // NetworkCard pattern — Task.Run(() => { _ = NetworkStatus.IsOnline; _ = NetworkStatus.GetConnectivity(); }) — on
        // pool threads with a hard per-read timeout, NO window/GPU. exit 0 = no hang (fixed); exit 2 = hung (reproduced).
        if (Array.IndexOf(args, "--nlm-deadlock-probe") >= 0 && OperatingSystem.IsWindowsVersionAtLeast(6, 0))
        {
            Environment.Exit(NlmDeadlockProbe.Run(args));
            return;
        }
        // Headless dispatcher freeze probe (regression gate for the UsePost() stranding bug): drives a real headless
        // AppHost, posts from a worker thread, asserts the post applies. exit 0 = applied (fixed); exit 2 = stranded.
        if (Array.IndexOf(args, "--post-freeze-probe") >= 0)
        {
            Environment.Exit(PostFreezeProbe.Run(args));
            return;
        }
        // File-picker crash probe (regression gate for the Dialogs-pillar crash): opens Open/Save/PickFolder against a
        // real owner window and auto-dismisses each via a watchdog (no human). exit 0 = opened+dismissed, no crash
        // (fixed); a process fail-fast at IModalWindow.Show on an MTA thread (the bug) is seen as a non-zero crash exit
        // by the parent launcher. Add --sta to run the pickers on STA workers (the post-fix shape).
        if (Array.IndexOf(args, "--filepicker-probe") >= 0 && OperatingSystem.IsWindowsVersionAtLeast(6, 0, 6000))
        {
            Environment.Exit(FilePickerProbe.Run(args));
            return;
        }
        // FAITHFUL variant: drive the REAL gallery window and run the picker on the UI thread mid-loop (the exact
        // DialogsCard reentrancy condition). exit 0 = ran + returned, no crash; a Show() fail-fast is a crash exit.
        if (Array.IndexOf(args, "--filepicker-auto") >= 0 && OperatingSystem.IsWindowsVersionAtLeast(6, 0, 6000))
        {
            Environment.Exit(FilePickerProbe.RunAuto(args));
            return;
        }
        // Packaging identity readback probe (the MSIX/package-identity prototype gate): reads PackageIdentity for the
        // CURRENT process and writes {IsPackaged, PackageFullName, PackageFamilyName, ApplicationUserModelId, Version,
        // InstalledLocation} to a fixed non-virtualized file (+ %TEMP% + Console), then exits — BEFORE any window/GPU
        // spins up. Launched under package identity via the AppExecutionAlias (fluentgpu-gallery.exe --packaging-probe),
        // IsPackaged reports True and a real PackageFullName/AUMID is read back from outside the process.
        if (Array.IndexOf(args, "--packaging-probe") >= 0)
        {
            Environment.Exit(PackagingProbe.Run(args));
            return;
        }

        int frames = -1;   // optional --frames N for headless/CI; omit for a normal interactive window
        string demo = "default";
        string? screenshot = null;   // --screenshot <path> renders a deterministic scene and writes a PNG (visual diff loop)
        string shot = "menu";        // --shot <id> selects the ShotScene
        string page = "welcome";     // --page <id> deep-links a gallery page (perf/diagnosis automation)
        bool micaShot = false;       // --mica reproduces the composited path the live app uses
        for (int i = 0; i < args.Length; i++)
        {
            if (i < args.Length - 1 && args[i] == "--frames" && int.TryParse(args[i + 1], out int f)) frames = f;
            if (i < args.Length - 1 && args[i] == "--demo") demo = args[i + 1];
            if (i < args.Length - 1 && args[i] == "--screenshot") screenshot = args[i + 1];
            if (i < args.Length - 1 && args[i] == "--shot") shot = args[i + 1];
            if (i < args.Length - 1 && args[i] == "--page") page = args[i + 1];
            if (args[i] == "--mica") micaShot = true;
        }

        // DirectWrite itemizer smoke test (BiDi/script/line-break via the callee CCWs) then exit.
        if (Array.IndexOf(args, "--itemtest") >= 0)
        {
            FluentGpu.Text.DirectWrite.DWriteItemizer.SelfTest();
            return;
        }
        if (Array.IndexOf(args, "--shapetest") >= 0)
        {
            FluentGpu.Text.DirectWrite.DWriteTextShaper.SelfTest();
            return;
        }
        if (Array.IndexOf(args, "--layouttest") >= 0)
        {
            FluentGpu.Text.DirectWrite.TextLayoutEngine.SelfTest();
            return;
        }

        // Screenshot mode: render a single deterministic scene then exit. Opaque by default; --mica for the composited path.
        if (screenshot != null)
        {
            int sf = frames > 0 ? frames : 6;   // a few frames to settle layout + glyph upload
            int sw = 900, sh = 640;             // --w/--h: reproduce a reported window geometry (wrap/clip bugs are width-dependent)
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--w" && int.TryParse(args[i + 1], out int w)) sw = w;
                if (args[i] == "--h" && int.TryParse(args[i + 1], out int h)) sh = h;
            }
            FluentApp.Run(() => new ShotScene(shot), "FluentGpu — Shot", sw, sh, mica: micaShot, frames: sf, screenshot: screenshot);
            return;
        }

        // Default = the capability gallery (everything). `--demo wavee` = the Wavee skeleton; `--demo list` = the
        // virtualized track list; `--demo basic` = the original minimal demo.
        if (demo == "wavee")
            FluentApp.Run(() => new WaveeShell(), "FluentGpu — Wavee skeleton", 1180, 760, frames: frames);
        else if (demo == "list")
            FluentApp.Run(() => new TrackListDemo(), "FluentGpu — Virtualized List", 520, 640, frames: frames);
        else if (demo == "basic")
            FluentApp.Run(() => new DemoApp(), "FluentGpu — Demo", 560, 360, frames: frames);
        else
            FluentApp.Run(() => new GalleryApp { InitialPage = page }, "FluentGpu — Capability Gallery", 1240, 820,
                          frames: frames, customFrame: true);   // the gallery draws the WinUI TitleBar (engine caption buttons)
    }
}
