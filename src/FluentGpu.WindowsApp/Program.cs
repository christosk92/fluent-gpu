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
    static void Main(string[] args)
    {
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
