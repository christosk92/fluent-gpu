using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The content-card body. Reads the route as a SIGNAL (so navigation swaps the page granularly) and returns a RAW element
// per route — never a Component with per-route constructor args (those freeze at mount; AGENTS rule 2). Phase 1 every
// route is a placeholder; real pages land later (and must read the route via signal/context too, not ctor args).
sealed class ContentHost : Component
{
    readonly Signal<Route> _route;
    public ContentHost(Signal<Route> route) => _route = route;

    public override Element Render()
    {
        var r = _route.Value;                          // subscribe → swap on navigation
        var (title, glyph) = ShellNav.Dest(r);
        return new BoxEl
        {
            Key = "page:" + r.Name,
            Grow = 1f, Direction = 1, Gap = WaveeSpace.M,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Children =
            [
                Icon(glyph, 40f, Tok.TextTertiary),
                WaveeType.PageHero(title),
                Caption("This view arrives in a later pass.").Secondary(),
            ],
        };
    }
}
