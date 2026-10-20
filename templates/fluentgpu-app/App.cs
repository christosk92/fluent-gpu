using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static FluentGpu.Dsl.Ui;

namespace FluentGpuApp;

/// <summary>The window root: the custom-frame title bar over one scrolling page. No Fill anywhere, so the Mica backdrop
/// shows through (AppOptions.Mica). The TitleBar reports its own drag band and caption buttons to the window.</summary>
sealed class App : Component
{
    public override Element Render() => new BoxEl
    {
        Direction = 1,   // column (a BoxEl defaults to a row)
        Grow = 1f,
        Children =
        [
            TitleBar.Create(new TitleBarOptions { Title = "FluentGpuApp", IconGlyph = Icons.Home }),
            // The page region takes the rest of the window and is a layout boundary (Basis 0 + MinHeight 0 lets it
            // shrink below its content so the ScrollView scrolls instead of growing the window).
            new BoxEl
            {
                Direction = 1,
                Grow = 1f,
                Basis = 0f,
                MinHeight = 0f,
                Children = [ScrollView(Embed.Comp(() => new HomePage()))],
            },
        ],
    };
}

/// <summary>The one page. Reading <c>count</c> subscribes this component, so a click re-renders this subtree only.</summary>
sealed class HomePage : Component
{
    public override Element Render()
    {
        var (count, setCount) = UseState(0);
        return Panel(new Edges4(36f, 12f, 36f, 36f), 12f,
            Title("Hello, FluentGpu"),
            Body("This window is GPU-rendered by FluentGpu: signals-first components, NativeAOT, Direct3D 12."),
            Text(count == 0 ? "Not clicked yet." : $"Clicked {count} times."),
            HStack(8f,
                Button.Accent("Click me", () => setCount(count + 1)),
                Button.Standard("Reset", () => setCount(0))));
    }
}
