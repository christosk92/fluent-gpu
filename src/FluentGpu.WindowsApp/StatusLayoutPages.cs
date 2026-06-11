using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static FluentGpu.Dsl.Ui;

// ── Status & info / Layout / Scrolling control demo pages (WinUI Gallery parity) ──────────────

sealed class ProgressBarPage : Component
{
    public override Element Render() => GalleryPage.Shell("ProgressBar",
        "Shows the progress of an operation — determinate (a known fraction) or indeterminate (ongoing).",
        ControlExample.Build("A determinate ProgressBar (60%)", ProgressBar.Determinate(0.6f), output: GalleryPage.LiveText(() => "60%")),
        ControlExample.Build("An indeterminate ProgressBar", ProgressBar.Indeterminate()));
}

sealed class InfoBarPage : Component
{
    public override Element Render() => GalleryPage.Shell("InfoBar",
        "An inline notification for essential, app-wide messages — with a severity color, icon, title, and message.",
        ControlExample.Build("Informational", InfoBar.Create(InfoBarSeverity.Informational, "Note", "This is an informational message.")),
        ControlExample.Build("Success", InfoBar.Create(InfoBarSeverity.Success, "Success", "The operation completed successfully.")),
        ControlExample.Build("Warning", InfoBar.Create(InfoBarSeverity.Warning, "Warning", "Something needs your attention.")),
        ControlExample.Build("Error (closable)", InfoBar.Create(InfoBarSeverity.Error, "Error", "Something went wrong.", onClose: () => { })));
}

sealed class InfoBadgePage : Component
{
    public override Element Render() => GalleryPage.Shell("InfoBadge",
        "A small piece of UI to add contextual information — a dot, a numeric count, or an icon.",
        ControlExample.Build("Dot", InfoBadge.Dot()),
        ControlExample.Build("Count", InfoBadge.Count(42)),
        ControlExample.Build("Icon", InfoBadge.Icon(Icons.Accept)));
}

sealed class ExpanderPage : Component
{
    public override Element Render() => GalleryPage.Shell("Expander",
        "A header with a content area that the user can expand and collapse.",
        ControlExample.Build("A simple Expander", Expander.Create("This text is collapsible",
            VStack(8, Body("Hidden content, revealed when the Expander is expanded."), Button.Standard("An action", () => { })),
            initiallyExpanded: true)));
}

sealed class PipsPagerPage : Component
{
    public override Element Render()
    {
        var sel = UseSignal(0);
        return GalleryPage.Shell("PipsPager",
            "A glyph-based pager for navigating a small, fixed number of pages.",
            ControlExample.Build("A PipsPager", PipsPager.Create(5, sel.Value, i => sel.Value = i), output: GalleryPage.LiveText(() => $"Page {sel.Value + 1} / 5")));
    }
}

// Category overview pages (the expandable group keys land here when selected) — WinUI tile grids per category.
sealed class StatusInfoOverviewPage : Component
{
    public override Element Render()
    {
        var navigate = UseContext(NavigationView.Nav);
        return GalleryPage.Shell("Status & info", "Controls that surface state and notifications.",
            GalleryPage.CategoryGrid("Status & info", navigate));
    }
}

sealed class LayoutOverviewPage : Component
{
    public override Element Render()
    {
        var navigate = UseContext(NavigationView.Nav);
        return GalleryPage.Shell("Layout", "Controls and panels for arranging and revealing content.",
            GalleryPage.CategoryGrid("Layout", navigate));
    }
}

sealed class ScrollingOverviewPage : Component
{
    public override Element Render()
    {
        // The standalone WinUI mouse ScrollBar (ScrollBar.Anatomy): a 12px rail at the content's right edge.
        // Hover the rail and dwell 400ms → the thumb expands 2px→6px over 167ms and the arrows/track fade in 83ms;
        // leave → it contracts after 500ms (ScrollBar_themeresources.xaml begin times). Position is signal-bound.
        var pos = UseSignal(0.3f);
        var navigate = UseContext(NavigationView.Nav);
        return GalleryPage.Shell("Scrolling",
            "Controls for paging and scrolling content: PipsPager, ScrollBar, AnnotatedScrollBar.",
            GalleryPage.CategoryGrid("Scrolling", navigate),
            new BoxEl { Height = 12f },
            ControlExample.Build("The standalone ScrollBar (hover the rail to expand; arrows page by a small change)",
                new BoxEl
                {
                    Direction = 0, Height = 200f, Gap = 12f,
                    Children =
                    [
                        new BoxEl { Width = 320f, Corners = Radii.ControlAll, Fill = Tok.FillCardSecondary },   // content stand-in
                        ScrollBar.Anatomy(0.25f, pos, p => pos.Value = p, length: 200f),
                    ],
                },
                output: GalleryPage.LiveText(() => "position " + pos.Value.ToString("0.00"))));
    }
}
