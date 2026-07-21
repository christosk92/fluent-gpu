using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static FluentGpu.Dsl.Ui;

// ── Status & info / Layout / Scrolling control demo pages (WinUI Gallery parity) ──────────────

[GalleryPage("ProgressBar", "ProgressBar", "Status & info", Icon = Icons.Refresh)]
sealed class ProgressBarPage : Component
{
    public override Element Render()
    {
        var (value, setValue) = UseState(0.6f);
        return GalleryPage.Shell("ProgressBar",
            "Shows the progress of an operation — determinate (a known fraction) or indeterminate (ongoing).",
            ExampleCard.Build("A determinate ProgressBar",
                VStack(16,
                    ProgressBar.Determinate(value),
                    HStack(8,
                        RepeatButton.Create("–", () => setValue(MathF.Max(0f, value - 0.05f))),
                        RepeatButton.Create("+", () => setValue(MathF.Min(1f, value + 0.05f))))),
                output: BodyStrong($"{value * 100f:0}%"),
                code: """
                var (value, setValue) = UseState(0.6f);

                VStack(16,
                    ProgressBar.Determinate(value),
                    HStack(8,
                        RepeatButton.Create("–", () => setValue(MathF.Max(0f, value - 0.05f))),
                        RepeatButton.Create("+", () => setValue(MathF.Min(1f, value + 0.05f)))))
                """),
            ExampleCard.Build("An indeterminate ProgressBar", ProgressBar.Indeterminate(),
                code: """
                // Two clipped accent indicators sweep the track on the WinUI
                // ProgressBarTemplateSettings translate keyframes (2s loop).
                ProgressBar.Indeterminate()
                """),
            ExampleCard.Build("Paused & error states (determinate)",
                VStack(12,
                    ProgressBar.Determinate(0.6f, state: ProgressBarState.Paused),
                    ProgressBar.Determinate(0.6f, state: ProgressBarState.Error)),
                code: """
                // Paused recolors the indicator to SystemFillColorCaution, Error to
                // SystemFillColorCritical (WinUI ProgressBar CommonStates).
                VStack(12,
                    ProgressBar.Determinate(0.6f, state: ProgressBarState.Paused),
                    ProgressBar.Determinate(0.6f, state: ProgressBarState.Error))
                """),
            ExampleCard.Build("Paused & error states (indeterminate)",
                VStack(12,
                    ProgressBar.Indeterminate(state: ProgressBarState.Paused),
                    ProgressBar.Indeterminate(state: ProgressBarState.Error)),
                code: """
                // IndeterminatePaused / IndeterminateError: the sweep stops and a
                // full-width caution/critical bar settles static over the track.
                VStack(12,
                    ProgressBar.Indeterminate(state: ProgressBarState.Paused),
                    ProgressBar.Indeterminate(state: ProgressBarState.Error))
                """));
    }
}

[GalleryPage("InfoBar", "InfoBar", "Status & info", Icon = Icons.Document)]
sealed class InfoBarPage : Component
{
    public override Element Render()
    {
        var (open, setOpen) = UseState(true);
        var (action1, setAction1) = UseState("—");
        var (action2, setAction2) = UseState("—");
        return GalleryPage.Shell("InfoBar",
            "An inline notification for essential, app-wide messages — with a severity color, icon, title, and message.",
            ExampleCard.Build("Informational", InfoBar.Create(InfoBarSeverity.Informational, "Note", "This is an informational message."),
                code: """
                InfoBar.Create(InfoBarSeverity.Informational, "Note", "This is an informational message.")
                """),
            ExampleCard.Build("Success", InfoBar.Create(InfoBarSeverity.Success, "Success", "The operation completed successfully."),
                code: """
                InfoBar.Create(InfoBarSeverity.Success, "Success", "The operation completed successfully.")
                """),
            ExampleCard.Build("Warning", InfoBar.Create(InfoBarSeverity.Warning, "Warning", "Something needs your attention."),
                code: """
                InfoBar.Create(InfoBarSeverity.Warning, "Warning", "Something needs your attention.")
                """),
            ExampleCard.Build("Error — closable (the X closes it; the button reopens it)",
                VStack(12,
                    InfoBar.Create(InfoBarSeverity.Error, "Error", "Something went wrong.", isOpen: open, onClose: () => setOpen(false)),
                    Button.Standard(open ? "Close InfoBar" : "Show InfoBar", () => setOpen(!open))),
                output: BodyStrong($"IsOpen: {open}"),
                code: """
                var (open, setOpen) = UseState(true);

                VStack(12,
                    InfoBar.Create(InfoBarSeverity.Error, "Error", "Something went wrong.",
                        isOpen: open, onClose: () => setOpen(false)),
                    Button.Standard(open ? "Close InfoBar" : "Show InfoBar", () => setOpen(!open)))
                """),
            ExampleCard.Build("With an action button (inline when the message fits on one line)",
                InfoBar.Create(InfoBarSeverity.Informational, "Update available", "Restart to apply.",
                    actionButton: Button.Standard("Restart", () => setAction1("Restart"))),
                output: BodyStrong($"Action: {action1}"),
                code: """
                InfoBar.Create(InfoBarSeverity.Informational, "Update available", "Restart to apply.",
                    actionButton: Button.Standard("Restart", () => setAction("Restart")))
                """),
            ExampleCard.Build("A long message + action (the vertical InfoBarPanel)",
                new BoxEl
                {
                    Width = 520f, Direction = 1,
                    Children =
                    [
                        InfoBar.Create(InfoBarSeverity.Warning, "Storage almost full",
                            "A message that does not fit on one line next to an action button wraps under the title instead — the InfoBarPanel switches to its vertical orientation.",
                            actionButton: HyperlinkButton.Create("Manage storage", () => setAction2("Manage storage"))),
                    ],
                },
                output: BodyStrong($"Action: {action2}"),
                code: """
                // A long message alongside an action flips the InfoBarPanel vertical:
                // title, then message, then the action button, stacked.
                InfoBar.Create(InfoBarSeverity.Warning, "Storage almost full",
                    "A message that does not fit on one line next to an action button wraps under the title instead — the InfoBarPanel switches to its vertical orientation.",
                    actionButton: HyperlinkButton.Create("Manage storage", () => setAction("Manage storage")))
                """),
            ExampleCard.Build("Not closable, icon hidden",
                InfoBar.Create(InfoBarSeverity.Success, "Saved", "All changes are stored.", isClosable: false, isIconVisible: false),
                code: """
                InfoBar.Create(InfoBarSeverity.Success, "Saved", "All changes are stored.",
                    isClosable: false, isIconVisible: false)
                """));
    }
}

[GalleryPage("InfoBadge", "InfoBadge", "Status & info", Icon = Icons.Tag)]
sealed class InfoBadgePage : Component
{
    public override Element Render()
    {
        var (count, setCount) = UseState(42);
        return GalleryPage.Shell("InfoBadge",
            "A small piece of UI to add contextual information — a dot, a numeric count, or an icon.",
            ExampleCard.Build("Dot", InfoBadge.Dot(),
                code: """
                // No value, no icon → the bare 4x4 accent dot (corner = height/2).
                InfoBadge.Dot()
                """),
            ExampleCard.Build("Count",
                VStack(12,
                    InfoBadge.Count(count),
                    HStack(8,
                        RepeatButton.Create("–", () => setCount(Math.Max(0, count - 1))),
                        RepeatButton.Create("+", () => setCount(count + 1)))),
                output: BodyStrong($"Value: {count}"),
                code: """
                var (count, setCount) = UseState(42);

                // Single digits stay circular (MeasureOverride squares a too-narrow pill).
                VStack(12,
                    InfoBadge.Count(count),
                    HStack(8,
                        RepeatButton.Create("–", () => setCount(Math.Max(0, count - 1))),
                        RepeatButton.Create("+", () => setCount(count + 1))))
                """),
            ExampleCard.Build("Icon", InfoBadge.Icon(Icons.Accept),
                code: """
                InfoBadge.Icon(Icons.Accept)
                """),
            ExampleCard.Build("Severity presets (the WinUI *InfoBadgeStyle set)",
                VStack(12,
                    HStack(12,
                        InfoBadge.Icon(InfoBadgeSeverity.Attention),
                        InfoBadge.Icon(InfoBadgeSeverity.Informational),
                        InfoBadge.Icon(InfoBadgeSeverity.Success),
                        InfoBadge.Icon(InfoBadgeSeverity.Caution),
                        InfoBadge.Icon(InfoBadgeSeverity.Critical)),
                    HStack(12,
                        InfoBadge.Count(99, InfoBadgeSeverity.Critical),
                        InfoBadge.Count(3, InfoBadgeSeverity.Success),
                        InfoBadge.Count(12, InfoBadgeSeverity.Informational)),
                    HStack(12,
                        InfoBadge.Dot(InfoBadgeSeverity.Attention),
                        InfoBadge.Dot(InfoBadgeSeverity.Caution),
                        InfoBadge.Dot(InfoBadgeSeverity.Critical))),
                description: "Each severity picks the WinUI default glyph and SystemFillColor background — Attention/Informational, Success, Caution, Critical.",
                code: """
                // Icon badges use the per-severity default glyph (Attention 0xEA38,
                // Informational 0xF13F, Success Accept, Caution Important, Critical Cancel).
                VStack(12,
                    HStack(12,
                        InfoBadge.Icon(InfoBadgeSeverity.Attention),
                        InfoBadge.Icon(InfoBadgeSeverity.Informational),
                        InfoBadge.Icon(InfoBadgeSeverity.Success),
                        InfoBadge.Icon(InfoBadgeSeverity.Caution),
                        InfoBadge.Icon(InfoBadgeSeverity.Critical)),
                    HStack(12,
                        InfoBadge.Count(99, InfoBadgeSeverity.Critical),
                        InfoBadge.Count(3, InfoBadgeSeverity.Success),
                        InfoBadge.Count(12, InfoBadgeSeverity.Informational)),
                    HStack(12,
                        InfoBadge.Dot(InfoBadgeSeverity.Attention),
                        InfoBadge.Dot(InfoBadgeSeverity.Caution),
                        InfoBadge.Dot(InfoBadgeSeverity.Critical)))
                """));
    }
}

[GalleryPage("Expander", "Expander", "Layout", Icon = Icons.ChevronDown)]
sealed class ExpanderPage : Component
{
    public override Element Render()
    {
        var open = UseSignal(false);
        return GalleryPage.Shell("Expander",
            "A header with a content area that the user can expand and collapse.",
            ExampleCard.Build("A simple Expander", Expander.Create("This text is collapsible",
                    VStack(8, Body("Hidden content, revealed when the Expander is expanded."), Button.Standard("An action", () => { })),
                    initiallyExpanded: true),
                code: """
                Expander.Create("This text is collapsible",
                    VStack(8,
                        Body("Hidden content, revealed when the Expander is expanded."),
                        Button.Standard("An action", () => { })),
                    initiallyExpanded: true)
                """),
            ExampleCard.Build("Controlled from outside (the IsExpanded signal)",
                VStack(12,
                    Button.Standard(open.Value ? "Collapse" : "Expand", () => open.Value = !open.Value),
                    Embed.Comp(() => new Expander
                    {
                        Header = "Controlled Expander",
                        Content = Body("Any write to the signal opens or closes the panel with the full motion."),
                        IsExpanded = open,
                    })),
                output: GalleryPage.LiveText(() => open.Value ? "Expanded" : "Collapsed"),
                code: """
                var open = UseSignal(false);

                VStack(12,
                    Button.Standard(open.Value ? "Collapse" : "Expand", () => open.Value = !open.Value),
                    Embed.Comp(() => new Expander
                    {
                        Header = "Controlled Expander",
                        Content = Body("Any write to the signal opens or closes the panel with the full motion."),
                        IsExpanded = open,
                    }))
                """),
            ExampleCard.Build("With rich header content (title + caption)",
                Embed.Comp(() => new Expander
                {
                    HeaderContent = new BoxEl
                    {
                        Direction = 1, Gap = 2f, Grow = 1f, Justify = FlexJustify.Center,
                        Children = [BodyStrong("Notifications"), Caption("Choose how and when alerts appear")],
                    },
                    Content = Body("Notification settings content goes here."),
                }),
                code: """
                // HeaderContent replaces the default header label (WinUI Expander.Header
                // is object content); the chevron button stays.
                Embed.Comp(() => new Expander
                {
                    HeaderContent = new BoxEl
                    {
                        Direction = 1, Gap = 2f, Grow = 1f, Justify = FlexJustify.Center,
                        Children = [BodyStrong("Notifications"), Caption("Choose how and when alerts appear")],
                    },
                    Content = Body("Notification settings content goes here."),
                })
                """));
    }
}

[GalleryPage("PipsPager", "PipsPager", "Scrolling", Icon = Icons.More)]
sealed class PipsPagerPage : Component
{
    static readonly string[] Slides = { "Mountains", "Coastline", "Forest", "Desert", "City lights" };

    public override Element Render()
    {
        var sel = UseSignal(0);
        var page = UseSignal(0);
        return GalleryPage.Shell("PipsPager",
            "A glyph-based pager for navigating a small, fixed number of pages.",
            ExampleCard.Build("A PipsPager", PipsPager.Create(5, sel),
                output: GalleryPage.LiveText(() => $"Page {sel.Value + 1} / 5"),
                code: """
                var sel = UseSignal(0);

                PipsPager.Create(5, sel)
                """),
            ExampleCard.Build("Paging content (the FlipView pairing)",
                new BoxEl
                {
                    Direction = 1, Gap = 8f, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        new BoxEl
                        {
                            Width = 280f, Height = 120f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                            Corners = Radii.OverlayAll, Fill = Tok.FillCardDefault, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
                            Children = [BodyStrong(Slides[page.Value])],
                        },
                        PipsPager.Create(Slides.Length, page),
                    ],
                },
                output: GalleryPage.LiveText(() => Slides[page.Value]),
                code: """
                static readonly string[] Slides = { "Mountains", "Coastline", "Forest", "Desert", "City lights" };
                var page = UseSignal(0);

                VStack(8,
                    new BoxEl
                    {
                        Width = 280f, Height = 120f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        Corners = Radii.OverlayAll, Fill = Tok.FillCardDefault, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
                        Children = [BodyStrong(Slides[page.Value])],
                    },
                    PipsPager.Create(Slides.Length, page))
                """));
    }
}

// Category overview pages (the expandable group keys land here when selected) — WinUI tile grids per category.
[GalleryPage("status-info", "Status & info", "Overview", Hidden = true)]
sealed class StatusInfoOverviewPage : Component
{
    public override Element Render()
    {
        var navigate = UseContext(NavigationView.Nav);
        return GalleryPage.Shell("Status & info", "Controls that surface state and notifications.",
            GalleryPage.CategoryGrid("Status & info", navigate));
    }
}

[GalleryPage("layout", "Layout", "Overview", Hidden = true)]
sealed class LayoutOverviewPage : Component
{
    public override Element Render()
    {
        var navigate = UseContext(NavigationView.Nav);
        return GalleryPage.Shell("Layout", "Controls and panels for arranging and revealing content.",
            GalleryPage.CategoryGrid("Layout", navigate));
    }
}

[GalleryPage("scrolling-controls", "Scrolling", "Overview", Hidden = true)]
sealed class ScrollingOverviewPage : Component
{
    public override Element Render()
    {
        // The standalone WinUI mouse ScrollBar (ScrollBar.Anatomy): a 12px rail at the content's right edge.
        // Hover the rail and dwell 400ms → the thumb expands 2px→6px over 167ms and the arrows/track fade in 83ms;
        // leave → it contracts after 500ms (ScrollBar_themeresources.xaml begin times). Position is signal-bound.
        var pos = UseFloatSignal(0.3f);
        var navigate = UseContext(NavigationView.Nav);
        return GalleryPage.Shell("Scrolling",
            "Controls for paging and scrolling content: PipsPager, ScrollBar, AnnotatedScrollBar.",
            GalleryPage.CategoryGrid("Scrolling", navigate),
            new BoxEl { Height = 12f },
            ExampleCard.Build("The standalone ScrollBar (hover the rail to expand; arrows page by a small change)",
                new BoxEl
                {
                    Direction = 0, Height = 200f, Gap = 12f,
                    Children =
                    [
                        new BoxEl { Width = 320f, Corners = Radii.ControlAll, Fill = Tok.FillCardSecondary },   // content stand-in
                        ScrollBar.Create(0.25f, pos, p => pos.Value = p, length: 200f),
                    ],
                },
                output: GalleryPage.LiveText(() => "position " + pos.Value.ToString("0.00")),
                code: """
                var pos = UseFloatSignal(0.3f);

                new BoxEl
                {
                    Direction = 0, Height = 200f, Gap = 12f,
                    Children =
                    [
                        new BoxEl { Width = 320f, Corners = Radii.ControlAll, Fill = Tok.FillCardSecondary },   // content stand-in
                        ScrollBar.Create(0.25f, pos, p => pos.Value = p, length: 200f),
                    ],
                }
                """));
    }
}
