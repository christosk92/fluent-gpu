using System.Linq;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Reconciler;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

// The capability gallery — a nav-driven showcase of everything fluent-gpu can do, styled to mirror the WinUI 3 Gallery:
// an integrated title bar + search, a grouped adaptive NavigationView (Expanded → Compact → Minimal), a sliding
// selection indicator, page-transition entrances, and the ControlExample pattern on every demo page.
//   dotnet run --project src/FluentGpu.WindowsApp
sealed class GalleryApp : Component
{
    // FG_HUD mounts the on-screen fps/draw-count readout; FG_DIAG is engine diag OUTPUT only (stderr) and must NOT
    // mount the HUD — the HUD's per-frame dynamic-text refresh is itself a wake source (it runs record+present forever).
    static readonly bool ShowDiagnosticsHud = Diag.EnvFlag("FG_HUD");

    // Mirrors the WinUI 3 Gallery's shape — Home, Fundamentals, Patterns, App services, Design, Controls (All + an
    // expanded Basic input group), Samples — with the engine's own capability demos grouped by purpose: Fundamentals =
    // the engine model, Patterns = motion/loading recipes on top of it, App services = engine features WinUI lacks.
    // (Accessibility is out of scope.)
    static readonly NavItem[] Items =
    {
        new("welcome", Icons.Home, "Home"),
        new("fundamentals", Icons.Document, "Fundamentals")
        {
            Children =
            [
                new("state", Icons.Refresh, "State & components"),
                new("flex", Icons.Tag, "Flexbox"),
                new("grid", Icons.Grid, "CSS Grid"),
                new("repeater", Icons.List, "ItemsRepeater"),
                new("virtualization", Icons.List, "List virtualization"),
                new("animation", Icons.Movie, "Animation"),
                new("compositor", Icons.Brush, "Compositor"),
                new("edge-fade", Icons.Brush, "Edge fade"),
                new("scrolling", Icons.Document, "Scrolling"),
            ],
        },
        new("patterns", Icons.Movie, "Patterns")
        {
            Children =
            [
                new("motion-recipes", Icons.Movie, "Motion recipes"),
                new("async-skeletons", Icons.Refresh, "Async & skeletons"),
            ],
        },
        new("app-services", Icons.Globe, "App services")
        {
            Children =
            [
                new("localization", Icons.Globe, "Localization"),
                new("validation-guide", Icons.Accept, "Validation"),
                new("windowsapi", Icons.Globe, "Windows APIs"),
            ],
        },
        new("design", Icons.Brush, "Design")
        {
            Children =
            [
                new("typography", Icons.Font, "Typography"),
                new("icons", Icons.Star, "Iconography"),
            ],
        },
        new("h-controls", "", "Controls", IsHeader: true),
        new("all", Icons.Grid, "All"),
        new("basic-input", Icons.Accept, "Basic input")
        {
            InitiallyExpanded = true,
            Children =
            [
                new("Button", Icons.Accept, "Button"),
                new("DropDownButton", Icons.More, "DropDownButton"),
                new("HyperlinkButton", Icons.Share, "HyperlinkButton"),
                new("RepeatButton", Icons.Refresh, "RepeatButton"),
                new("ToggleButton", Icons.Accept, "ToggleButton"),
                new("SplitButton", Icons.More, "SplitButton"),
                new("ToggleSplitButton", Icons.More, "ToggleSplitButton"),
                new("CheckBox", Icons.Accept, "CheckBox"),
                new("ColorPicker", Icons.Brush, "ColorPicker"),
                new("ComboBox", Icons.List, "ComboBox"),
                new("RadioButton", Icons.FavoriteStar, "RadioButton"),
                new("RatingControl", Icons.Star, "RatingControl"),
                new("Slider", Icons.Volume, "Slider"),
                new("ToggleSwitch", Icons.Settings, "ToggleSwitch"),
            ],
        },
        new("status-info", Icons.Document, "Status & info")
        {
            Children =
            [
                new("InfoBadge", Icons.Tag, "InfoBadge"),
                new("InfoBar", Icons.Document, "InfoBar"),
                new("ProgressBar", Icons.Refresh, "ProgressBar"),
                new("ProgressRing", Icons.Refresh, "ProgressRing"),
                new("ToolTip", Icons.Document, "ToolTip"),
            ],
        },
        new("layout", Icons.Grid, "Layout")
        {
            Children =
            [
                new("Expander", Icons.ChevronDown, "Expander"),
                new("SplitView", Icons.Grid, "SplitView"),
                new("Viewbox", Icons.Picture, "Viewbox"),
                new("Border", Icons.Grid, "Border"),
                new("Canvas", Icons.Grid, "Canvas"),
                new("RelativePanel", Icons.Grid, "RelativePanel"),
                new("VariableSizedWrapGrid", Icons.Grid, "VariableSizedWrapGrid"),
            ],
        },
        new("scrolling-controls", Icons.More, "Scrolling")
        {
            Children =
            [
                new("PipsPager", Icons.More, "PipsPager"),
                new("AnnotatedScrollBar", Icons.More, "AnnotatedScrollBar"),
            ],
        },
        new("navigation-cat", Icons.List, "Navigation")
        {
            Children =
            [
                new("BreadcrumbBar", Icons.List, "BreadcrumbBar"),
                new("SelectorBar", Icons.List, "SelectorBar"),
                new("TabView", Icons.Document, "TabView"),
                new("Pivot", Icons.Document, "Pivot"),
            ],
        },
        new("dialogs", Icons.More, "Dialogs & flyouts")
        {
            Children =
            [
                new("Flyout", Icons.More, "Flyout"),
                new("ContentDialog", Icons.Document, "ContentDialog"),
                new("TeachingTip", Icons.Star, "TeachingTip"),
                new("Popup", Icons.More, "Popup"),
                new("Toast", Icons.More, "Toast"),
            ],
        },
        new("text-cat", Icons.Font, "Text")
        {
            Children =
            [
                new("TextBox", Icons.Font, "TextBox"),
                new("PasswordBox", Icons.Settings, "PasswordBox"),
                new("AutoSuggestBox", Icons.List, "AutoSuggestBox"),
                new("NumberBox", Icons.Volume, "NumberBox"),
                new("TextBlock", Icons.Font, "TextBlock"),
                new("RichTextBlock", Icons.Font, "RichTextBlock"),
            ],
        },
        new("media", Icons.Picture, "Media")
        {
            Children =
            [
                new("Image", Icons.Picture, "Image"),
                new("PersonPicture", Icons.FavoriteStar, "PersonPicture"),
                new("MediaPlayerElement", Icons.Movie, "MediaPlayerElement"),
                // ONE media area: the lab's catalog navigates to per-scenario player pages, and DRM (incl. the
                // free-form DASH+PlayReady source) is a section inside it — the former Desktop Video / Protected
                // Video pages are folded in.
                new("media-lab", Icons.Movie, "Media Lab"),
            ],
        },
        new("collections", Icons.List, "Collections")
        {
            Children =
            [
                // ItemsView is the premiere collections control (it absorbed the former ListView/GridView pages as
                // its List/Grid presets); it leads the category.
                new("ItemsView", Icons.Grid, "ItemsView"),
                new("FlipView", Icons.Picture, "FlipView"),
                new("TreeView", Icons.List, "TreeView"),
            ],
        },
        new("menus", Icons.More, "Menus & toolbars")
        {
            Children =
            [
                new("MenuBar", Icons.More, "MenuBar"),
                new("AppBarButton", Icons.Accept, "AppBarButton"),
                new("AppBarToggleButton", Icons.Accept, "AppBarToggleButton"),
                new("CommandBar", Icons.More, "CommandBar"),
                new("AppBarSeparator", Icons.More, "AppBarSeparator"),
                new("CommandBarFlyout", Icons.More, "CommandBarFlyout"),
                new("Context menu", Icons.More, "ContextMenu"),
                new("SwipeControl", Icons.More, "SwipeControl"),
            ],
        },
        new("datetime", Icons.Document, "Date & time")
        {
            Children =
            [
                new("CalendarView", Icons.Grid, "CalendarView"),
                new("CalendarDatePicker", Icons.Document, "CalendarDatePicker"),
                new("DatePicker", Icons.Document, "DatePicker"),
                new("TimePicker", Icons.Document, "TimePicker"),
            ],
        },
        new("h-samples", "", "Samples", IsHeader: true),
        new("wavee", Icons.MusicNote, "Wavee skeleton"),
        new("validation", Icons.Accept, "Sign-up form"),
    };

    // Every control page, grouped by nav category — drives the All-controls page and the category overview pages
    // (tile image + subtitle come from the PageInfo registry).
    public static readonly (string Title, string[] Keys)[] ControlCatalog =
    {
        ("Basic input", ["Button", "DropDownButton", "HyperlinkButton", "RepeatButton", "ToggleButton", "SplitButton",
                         "ToggleSplitButton", "CheckBox", "ColorPicker", "ComboBox", "RadioButton", "RatingControl",
                         "Slider", "ToggleSwitch"]),
        ("Status & info", ["InfoBadge", "InfoBar", "ProgressBar", "ProgressRing", "ToolTip"]),
        ("Layout", ["Expander", "SplitView", "Viewbox", "Border", "Canvas", "RelativePanel", "VariableSizedWrapGrid"]),
        ("Scrolling", ["PipsPager", "AnnotatedScrollBar"]),
        ("Navigation", ["BreadcrumbBar", "SelectorBar", "TabView", "Pivot"]),
        ("Dialogs & flyouts", ["Flyout", "ContentDialog", "TeachingTip", "Popup", "Toast"]),
        ("Text", ["TextBox", "PasswordBox", "AutoSuggestBox", "NumberBox", "TextBlock", "RichTextBlock"]),
        ("Media", ["Image", "PersonPicture", "MediaPlayerElement"]),
        ("Collections", ["ItemsView", "FlipView", "TreeView"]),
        ("Menus & toolbars", ["MenuBar", "AppBarButton", "AppBarToggleButton", "CommandBar", "AppBarSeparator",
                              "CommandBarFlyout", "ContextMenu", "SwipeControl"]),
        ("Date & time", ["CalendarView", "CalendarDatePicker", "DatePicker", "TimePicker"]),
    };

    public static string[] CategoryKeys(string title)
    {
        foreach (var (t, keys) in ControlCatalog) if (t == title) return keys;
        return [];
    }

    // Initial nav page (default = Home). Overridable so the --screenshot harness can deep-link a control page.
    public string InitialPage = "welcome";

    // Titlebar chrome → NavigationView seams (bump/request signals; instance-lifetime, wired into both components).
    readonly Signal<int> _paneToggleReq = new(0);
    readonly Signal<string> _navigateReq = new("");
    readonly Signal<string> _searchText = new("");

    // Diagnostics seam (SoakProbe — FG_SOAK / FG_STRESS_NAV): a static lever so the longevity/leak harness can cycle
    // pages by key without simulating clicks. Wired in Render under the env flag; invoked between RunFrames on the UI thread.
    internal static System.Action<string>? StressNavigate;
    internal static string[] StressNavKeys = System.Array.Empty<string>();

    // The titlebar search corpus: every selectable nav entry (groups + leaves); AutoSuggestBox substring-filters it.
    static readonly (string Label, string Key)[] SearchIndex = BuildSearchIndex();
    static readonly string[] SearchTitles = SearchIndex.Select(e => e.Label).Distinct().ToArray();

    static (string Label, string Key)[] BuildSearchIndex()
    {
        var list = new List<(string Label, string Key)>();
        void Walk(NavItem[] items)
        {
            foreach (var it in items)
            {
                if (!it.IsHeader && !it.IsSeparator) list.Add((it.Label, it.Key));
                if (it.Children is { Length: > 0 } kids) Walk(kids);
            }
        }
        Walk(Items);
        return list.ToArray();
    }

    // Search commit (Enter or suggestion choice) → resolve the typed/chosen title to a nav key and navigate.
    void NavigateToTitle(string query)
    {
        string q = query.Trim();
        if (q.Length == 0) return;
        string? key = null;
        foreach (var (label, k) in SearchIndex)
            if (string.Equals(label, q, StringComparison.OrdinalIgnoreCase)) { key = k; break; }
        if (key is null)
            foreach (var (label, k) in SearchIndex)
                if (label.Contains(q, StringComparison.OrdinalIgnoreCase)) { key = k; break; }
        if (key is null) return;
        // A ""-reset first so re-searching the SAME page after navigating elsewhere still changes the signal value
        // (request signals are equality-gated).
        _navigateReq.Value = "";
        _navigateReq.Value = key;
    }

    public override Element Render()
    {
        if (Diag.EnvFlag("FG_SOAK") || Diag.EnvFlag("FG_STRESS_NAV") || Diag.EnvFlag("FG_WAKE_AUDIT"))
        {
            StressNavigate = key => { _navigateReq.Value = ""; _navigateReq.Value = key; };
            if (StressNavKeys.Length == 0) StressNavKeys = SearchIndex.Select(e => e.Key).Distinct().ToArray();
        }

        var shell = VStack(0,
            // The WinUI 3 Gallery titlebar: hamburger + accent icon + title + the centered AutoSuggestBox +
            // engine-drawn min/max/close on the custom frame. Back is COLLAPSED, not disabled — WinUI binds
            // IsBackButtonVisible to rootFrame.CanGoBack, and this gallery has no back stack yet (flip
            // ShowBackButton=true + BackEnabled when a Navigator lands).
            Embed.Comp(() =>
            {
                var tb = new TitleBar
                {
                    Title = "FluentGpu Gallery",
                    IconGlyph = Icons.Grid,
                    ShowBackButton = false,
                    ShowPaneToggle = true,
                    OnPaneToggle = () => _paneToggleReq.Value = _paneToggleReq.Peek() + 1,
                    ShowCaptionButtons = true,
                };
                // WinUI sizing: 580 is the MAX — the search gives way as the window narrows (caption buttons never
                // move) and collapses entirely below a usable floor. The avail ARGUMENT picks the shape per render
                // (collapse vs box); the LIVE width must flow as a signal — the mounted AutoSuggestBox's plain
                // fields froze at mount, so a lambda-recomputed width: could never resize it again.
                tb.Content = avail => avail < 140f
                    ? new BoxEl()
                    : AutoSuggestBox.Create(
                        suggestions: SearchTitles,
                        placeholder: "Search controls and samples...",
                        width: 580f,
                        widthSignal: tb.ContentAvail,
                        text: _searchText,
                        onSuggestionChosen: NavigateToTitle,
                        onQuerySubmitted: NavigateToTitle);
                return tb;
            }),
            Embed.Comp(() => new NavigationView
            {
                Header = "fluent-gpu",
                Initial = InitialPage,
                Items = Items,
                Content = Page,   // each page is a distinct component type → the reconciler remounts it on navigation
                ShowPaneToggle = false,            // the titlebar owns the hamburger (the WinUI-gallery shape)
                PaneToggleRequest = _paneToggleReq,
                NavigateRequest = _navigateReq,
            })
        ) with { Grow = 1 };

        var content = ShowDiagnosticsHud ? ZStack(shell, DiagnosticsOverlay()) with { Grow = 1 } : shell;
        // Host the overlay layer at the top so anchored flyouts (ComboBox/DropDownButton/SplitButton/ColorPicker) work app-wide.
        // FGRP001: OverlayHost.Child is the app composition root — mounted once; all reactivity lives in child components.
#pragma warning disable FGRP001
        return Embed.Comp(() => new OverlayHost { Child = content });
#pragma warning restore FGRP001
    }

    static Element DiagnosticsOverlay() => new BoxEl
    {
        Direction = 0,
        Justify = FlexJustify.End,
        AlignItems = FlexAlign.Start,
        HitTestVisible = false,
        Padding = new Edges4(12, 56, 152, 0),
        Children = [Embed.Comp(() => new FrameDiagnosticsHud())],
    };

    // WS3 P8: pages are registered by the RouteTableGenerator from their [Route] attributes
    // (FluentGpu.Generated.Routes.RegisterAll), replacing the former hand-synced ~100-arm switch. The NavItem tree +
    // search index above stay hand-authored this phase; the registry is the page FACTORY (the full registry-derived
    // shell is G8/WS7).
    static readonly RouteRegistry _routes = BuildRoutes();

    static RouteRegistry BuildRoutes()
    {
        var r = new RouteRegistry();
        FluentGpu.Generated.Routes.RegisterAll(r);
        // Deep-link aliases the old switch folded onto one class (Media Lab hosts the former Desktop/Protected video pages).
        if (r.Resolve("media-lab") is { } lab)
        {
            r.Add(new RouteDef("desktop-video", lab.Factory));
            r.Add(new RouteDef("playready-video", lab.Factory));
        }
        r.Fallback = _ => Embed.Comp(() => new WelcomePage());   // unknown key -> Home (the old switch's default arm)
        return r;
    }

    static Element Page(string key) => (_routes.Resolve(key)?.Factory ?? _routes.Fallback!)(new Route(key));
}

sealed class FrameDiagnosticsHud : Component
{
    public override Element Render()
    {
        return new BoxEl
        {
            Direction = 0,
            Gap = 10,
            AlignItems = FlexAlign.Center,
            Padding = new Edges4(10, 5, 10, 5),
            MinHeight = 30,
            Fill = ColorF.FromRgba(0x13, 0x15, 0x1A, 0xCC),
            BorderColor = Tok.StrokeSurfaceDefault,
            BorderWidth = 1f,
            Corners = Radii.ControlAll,
            Children =
            [
                Metric("fps", "000", DynamicTextKind.FrameFps, Tok.AccentDefault),
                Metric("cmd", "0000", DynamicTextKind.FrameCommandCount, Tok.TextPrimary),
                Metric("draw", "0000", DynamicTextKind.FrameDrawCount, Tok.TextPrimary),
                Metric("cull", "0000", DynamicTextKind.FrameCullCount, Tok.TextPrimary),
                Metric("ms", "000.0", DynamicTextKind.FrameMs, Tok.TextSecondary),
            ],
        };
    }

    static Element Metric(string label, string placeholder, DynamicTextKind dynamicText, ColorF valueColor) => new BoxEl
    {
        Direction = 0,
        Gap = 4,
        AlignItems = FlexAlign.Center,
        Children =
        [
            new TextEl(label) { Size = 11f, Color = Tok.TextTertiary, FontFamily = "Cascadia Code" },
            new TextEl(placeholder) { Size = 12f, Bold = true, Color = valueColor, FontFamily = "Cascadia Code", DynamicText = dynamicText },
        ],
    };
}
