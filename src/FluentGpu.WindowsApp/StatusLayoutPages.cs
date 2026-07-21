using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

// ── Status & info / Layout / Scrolling control demo pages (WinUI Gallery parity) ──────────────

[GalleryPage("ProgressBar", "ProgressBar", "Status & info", Icon = Icons.Refresh)]
sealed partial class ProgressBarPage : Component
{
    static readonly FloatSignal _value = new(0.6f);

    public override Element Render() => GalleryPage.Shell("ProgressBar",
        "Shows the progress of an operation — determinate (a known fraction) or indeterminate (ongoing).",
        ExampleCard.Show(DeterminateSample),
        ExampleCard.Show(IndeterminateSample),
        ExampleCard.Show(DeterminateStatesSample),
        ExampleCard.Show(IndeterminateStatesSample));

    [Sample("A determinate ProgressBar")]
    static Element Determinate() => VStack(16,
        ProgressBar.Determinate(_value.Value),
        HStack(8,
            RepeatButton.Create("–", () => _value.Value = MathF.Max(0f, _value.Value - 0.05f)),
            RepeatButton.Create("+", () => _value.Value = MathF.Min(1f, _value.Value + 0.05f))),
        GalleryPage.LiveText(() => $"{_value.Value * 100f:0}%"));

    [Sample("An indeterminate ProgressBar")]
    static Element Indeterminate()
    {
        // Two clipped accent indicators sweep the track on the WinUI
        // ProgressBarTemplateSettings translate keyframes (2s loop).
        return ProgressBar.Indeterminate();
    }

    [Sample("Paused & error states (determinate)")]
    static Element DeterminateStates()
    {
        // Paused recolors the indicator to SystemFillColorCaution, Error to
        // SystemFillColorCritical (WinUI ProgressBar CommonStates).
        return VStack(12,
            ProgressBar.Determinate(0.6f, state: ProgressBarState.Paused),
            ProgressBar.Determinate(0.6f, state: ProgressBarState.Error));
    }

    [Sample("Paused & error states (indeterminate)")]
    static Element IndeterminateStates()
    {
        // IndeterminatePaused / IndeterminateError: the sweep stops and a
        // full-width caution/critical bar settles static over the track.
        return VStack(12,
            ProgressBar.Indeterminate(state: ProgressBarState.Paused),
            ProgressBar.Indeterminate(state: ProgressBarState.Error));
    }
}

[GalleryPage("InfoBar", "InfoBar", "Status & info", Icon = Icons.Document)]
sealed partial class InfoBarPage : Component
{
    static readonly Signal<bool> _open = new(true);
    static readonly Signal<string> _action1 = new("—");
    static readonly Signal<string> _action2 = new("—");

    public override Element Render() => GalleryPage.Shell("InfoBar",
        "An inline notification for essential, app-wide messages — with a severity color, icon, title, and message.",
        ExampleCard.Show(InformationalSample),
        ExampleCard.Show(SuccessSample),
        ExampleCard.Show(WarningSample),
        ExampleCard.Show(ClosableSample),
        ExampleCard.Show(WithActionSample),
        ExampleCard.Show(LongMessageSample),
        ExampleCard.Show(NotClosableSample));

    [Sample("Informational")]
    static Element Informational() => InfoBar.Create(InfoBarSeverity.Informational, "Note", "This is an informational message.");

    [Sample("Success")]
    static Element Success() => InfoBar.Create(InfoBarSeverity.Success, "Success", "The operation completed successfully.");

    [Sample("Warning")]
    static Element Warning() => InfoBar.Create(InfoBarSeverity.Warning, "Warning", "Something needs your attention.");

    [Sample("Error — closable (the X closes it; the button reopens it)")]
    static Element Closable() => VStack(12,
        InfoBar.Create(InfoBarSeverity.Error, "Error", "Something went wrong.",
            isOpen: _open.Value, onClose: () => _open.Value = false),
        Button.Standard(_open.Value ? "Close InfoBar" : "Show InfoBar", () => _open.Value = !_open.Value),
        GalleryPage.LiveText(() => $"IsOpen: {_open.Value}"));

    [Sample("With an action button (inline when the message fits on one line)")]
    static Element WithAction() => VStack(8,
        InfoBar.Create(InfoBarSeverity.Informational, "Update available", "Restart to apply.",
            actionButton: Button.Standard("Restart", () => _action1.Value = "Restart")),
        GalleryPage.LiveText(() => $"Action: {_action1.Value}"));

    [Sample("A long message + action (the vertical InfoBarPanel)")]
    static Element LongMessage() => VStack(8,
        // A long message alongside an action flips the InfoBarPanel vertical:
        // title, then message, then the action button, stacked.
        new BoxEl
        {
            Width = 520f, Direction = 1,
            Children =
            [
                InfoBar.Create(InfoBarSeverity.Warning, "Storage almost full",
                    "A message that does not fit on one line next to an action button wraps under the title instead — the InfoBarPanel switches to its vertical orientation.",
                    actionButton: HyperlinkButton.Create("Manage storage", () => _action2.Value = "Manage storage")),
            ],
        },
        GalleryPage.LiveText(() => $"Action: {_action2.Value}"));

    [Sample("Not closable, icon hidden")]
    static Element NotClosable() => InfoBar.Create(InfoBarSeverity.Success, "Saved", "All changes are stored.",
        isClosable: false, isIconVisible: false);
}

[GalleryPage("InfoBadge", "InfoBadge", "Status & info", Icon = Icons.Tag)]
sealed partial class InfoBadgePage : Component
{
    static readonly Signal<int> _count = new(42);

    public override Element Render() => GalleryPage.Shell("InfoBadge",
        "A small piece of UI to add contextual information — a dot, a numeric count, or an icon.",
        ExampleCard.Show(DotBadgeSample),
        ExampleCard.Show(CountBadgeSample),
        ExampleCard.Show(IconBadgeSample),
        ExampleCard.Show(SeverityPresetsSample));

    [Sample("Dot")]
    static Element DotBadge()
    {
        // No value, no icon → the bare 4x4 accent dot (corner = height/2).
        return InfoBadge.Dot();
    }

    [Sample("Count")]
    static Element CountBadge() => VStack(12,
        // Single digits stay circular (MeasureOverride squares a too-narrow pill).
        InfoBadge.Count(_count.Value),
        HStack(8,
            RepeatButton.Create("–", () => _count.Value = Math.Max(0, _count.Value - 1)),
            RepeatButton.Create("+", () => _count.Value++)),
        GalleryPage.LiveText(() => $"Value: {_count.Value}"));

    [Sample("Icon")]
    static Element IconBadge() => InfoBadge.Icon(Icons.Accept);

    [Sample("Severity presets (the WinUI *InfoBadgeStyle set)",
        Description = "Each severity picks the WinUI default glyph and SystemFillColor background — Attention/Informational, Success, Caution, Critical.")]
    static Element SeverityPresets()
    {
        // Icon badges use the per-severity default glyph (Attention 0xEA38,
        // Informational 0xF13F, Success Accept, Caution Important, Critical Cancel).
        return VStack(12,
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
                InfoBadge.Dot(InfoBadgeSeverity.Critical)));
    }
}

[GalleryPage("Expander", "Expander", "Layout", Icon = Icons.ChevronDown)]
sealed partial class ExpanderPage : Component
{
    static readonly Signal<bool> _open = new(false);

    public override Element Render() => GalleryPage.Shell("Expander",
        "A header with a content area that the user can expand and collapse.",
        ExampleCard.Show(SimpleSample),
        ExampleCard.Show(ControlledSample),
        ExampleCard.Show(RichHeaderSample));

    [Sample("A simple Expander")]
    static Element Simple() => Expander.Create("This text is collapsible",
        VStack(8,
            Body("Hidden content, revealed when the Expander is expanded."),
            Button.Standard("An action", () => { })),
        initiallyExpanded: true);

    [Sample("Controlled from outside (the IsExpanded signal)")]
    static Element Controlled() => VStack(12,
        Button.Standard(_open.Value ? "Collapse" : "Expand", () => _open.Value = !_open.Value),
        Embed.Comp(() => new Expander
        {
            Header = "Controlled Expander",
            Content = Body("Any write to the signal opens or closes the panel with the full motion."),
            IsExpanded = _open,
        }),
        GalleryPage.LiveText(() => _open.Value ? "Expanded" : "Collapsed"));

    [Sample("With rich header content (title + caption)")]
    static Element RichHeader()
    {
        // HeaderContent replaces the default header label (WinUI Expander.Header
        // is object content); the chevron button stays.
        return Embed.Comp(() => new Expander
        {
            HeaderContent = new BoxEl
            {
                Direction = 1, Gap = 2f, Grow = 1f, Justify = FlexJustify.Center,
                Children = [BodyStrong("Notifications"), Caption("Choose how and when alerts appear")],
            },
            Content = Body("Notification settings content goes here."),
        });
    }
}

[GalleryPage("PipsPager", "PipsPager", "Scrolling", Icon = Icons.More)]
sealed partial class PipsPagerPage : Component
{
    static readonly string[] Slides = { "Mountains", "Coastline", "Forest", "Desert", "City lights" };
    static readonly Signal<int> _sel = new(0);
    static readonly Signal<int> _page = new(0);

    public override Element Render() => GalleryPage.Shell("PipsPager",
        "A glyph-based pager for navigating a small, fixed number of pages.",
        ExampleCard.Show(BasicSample),
        ExampleCard.Show(PagingSample));

    [Sample("A PipsPager")]
    static Element Basic() => VStack(8,
        PipsPager.Create(5, _sel),
        GalleryPage.LiveText(() => $"Page {_sel.Value + 1} / 5"));

    [Sample("Paging content (the FlipView pairing)")]
    static Element Paging() => new BoxEl
    {
        Direction = 1, Gap = 8f, AlignItems = FlexAlign.Center,
        Children =
        [
            new BoxEl
            {
                Width = 280f, Height = 120f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = Radii.OverlayAll, Fill = Tok.FillCardDefault, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
                Children = [GalleryPage.LiveText(() => Slides[_page.Value])],
            },
            PipsPager.Create(Slides.Length, _page),
        ],
    };
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
sealed partial class ScrollingOverviewPage : Component
{
    // The standalone WinUI mouse ScrollBar (ScrollBar.Anatomy): a 12px rail at the content's right edge.
    // Hover the rail and dwell 400ms → the thumb expands 2px→6px over 167ms and the arrows/track fade in 83ms;
    // leave → it contracts after 500ms (ScrollBar_themeresources.xaml begin times). Position is signal-bound.
    static readonly FloatSignal _pos = new(0.3f);

    public override Element Render()
    {
        var navigate = UseContext(NavigationView.Nav);
        return GalleryPage.Shell("Scrolling",
            "Controls for paging and scrolling content: PipsPager, ScrollBar, AnnotatedScrollBar.",
            GalleryPage.CategoryGrid("Scrolling", navigate),
            new BoxEl { Height = 12f },
            ExampleCard.Show(StandaloneScrollBarSample));
    }

    [Sample("The standalone ScrollBar (hover the rail to expand; arrows page by a small change)")]
    static Element StandaloneScrollBar() => VStack(8,
        new BoxEl
        {
            Direction = 0, Height = 200f, Gap = 12f,
            Children =
            [
                new BoxEl { Width = 320f, Corners = Radii.ControlAll, Fill = Tok.FillCardSecondary },   // content stand-in
                ScrollBar.Create(0.25f, _pos, p => _pos.Value = p, length: 200f),
            ],
        },
        GalleryPage.LiveText(() => "position " + _pos.Value.ToString("0.00")));
}
