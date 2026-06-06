using FluentGpu;
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
        float t = UseAnimatedValue(liked ? 1f : 0f, 200f);                 // 0→1 eased on like
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
                    Button.Accent("Save", () => { }, new ButtonStyle
                    {
                        Background = ColorF.FromRgba(0x10, 0x7C, 0x10),
                        Foreground = ColorF.FromRgba(0xFF, 0xFF, 0xFF),
                        Border = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x20),
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

// The entire app: define components, then one line to run. No PAL/RHI/AppHost/Mica/accent/loop to think about.
static class Program
{
    static void Main(string[] args)
    {
        int frames = -1;   // optional --frames N for headless/CI; omit for a normal interactive window
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--frames" && int.TryParse(args[i + 1], out int f)) frames = f;

        FluentApp.Run(() => new DemoApp(), "FluentGpu — Demo", 560, 360, frames: frames);
    }
}
