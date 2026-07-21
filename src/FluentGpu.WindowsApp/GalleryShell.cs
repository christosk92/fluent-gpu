using System;
using System.Collections.Generic;
using System.Linq;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

// ── GalleryShell (WS7 W7.0 + G8b) ─────────────────────────────────────────────────────────────────────────────────
// The registry-driven capability gallery: the nav tree, search index, page factory, All-controls grid, and shot sweep
// are ALL derived from the one source of truth — the generated FluentGpu.Generated.GalleryRegistry ([GalleryPage] tags)
// — via a RouteRegistry bridge + the hand-authored GallerySections IA table. It fully replaces the former GalleryApp
// (deleted in G8b) and its three hand-synced tables (NavItem tree / ControlCatalog / search index) + the 100-arm page
// switch: the compile IS the sync check. Built on the REAL Navigator (Route.Name = page key) wired into NavigationView;
// deep-linkable via InitialPage; the SoakProbe StressNavigate seam is preserved (keys come from the registry).
//
// G8b adds: a Ctrl+K command palette (Popup over the registry search index), a TitleBar back button driven by a
// shell-owned back stack (NavigationView selects via Navigator.Replace, so the shell keeps the history and drives the
// pop through NavigateRequest — selection follows on back), and the ControlCatalog/CategoryKeys helpers derived from
// the registry (were hand-tables on GalleryApp).
sealed class GalleryShell : Component
{
    static readonly bool ShowDiagnosticsHud = Diag.EnvFlag("FG_HUD");

    // Initial nav page (default = Home). Overridable so --page / --shot page:<key> can deep-link.
    public string InitialPage = "welcome";

    // The one source of truth, bridged to the app-facing RouteRegistry (Controls) so we reuse its proven derivation
    // (BuildSectionedNavTree / BuildSearchIndex) + PageHost resolution. Built once (registration is stable).
    static readonly RouteRegistry Registry = BuildRegistry();
    static readonly NavItem[] NavItems = BuildItems();
    static readonly (string Label, string Key)[] SearchIndex = Registry.BuildSearchIndex();
    static readonly string[] SearchTitles = SearchIndex.Select(e => e.Label).Distinct().ToArray();

    // ── SoakProbe seam (moved from GalleryApp) ───────────────────────────────────────────────────────────────────
    // A static lever so the longevity/leak harness (FG_SOAK / FG_STRESS_NAV) can cycle pages by key without simulating
    // clicks. Wired in Render under the env flag; invoked between RunFrames on the UI thread.
    internal static Action<string>? StressNavigate;
    internal static string[] StressNavKeys = Array.Empty<string>();

    // ── Control catalog (derived from the registry — was a hand table on GalleryApp) ─────────────────────────────
    // The "Controls" IA section's categories, in GallerySections order; each category's keys are its registry pages
    // (ShowInNav, i.e. not Hidden/Overview), ordered by Order then registry order. Drives the All-controls page and
    // the per-category overview grids.
    internal static readonly (string Title, string[] Keys)[] ControlCatalog = BuildCatalog();

    internal static string[] CategoryKeys(string title)
    {
        foreach (var (t, keys) in ControlCatalog) if (t == title) return keys;
        return Array.Empty<string>();
    }

    static (string Title, string[] Keys)[] BuildCatalog()
    {
        string[] cats = GallerySections.Sections.FirstOrDefault(s => s.Section == "Controls").Categories ?? Array.Empty<string>();
        var list = new List<(string, string[])>(cats.Length);
        foreach (var cat in cats)
        {
            var keys = FluentGpu.Generated.GalleryRegistry.Pages
                .Where(p => p.Category == cat && !p.Hidden)
                .OrderBy(p => p.Order)   // stable: ties keep registry order
                .Select(p => p.Key)
                .ToArray();
            list.Add((cat, keys));
        }
        return list.ToArray();
    }

    static RouteRegistry BuildRegistry()
    {
        var r = new RouteRegistry();
        foreach (var p in FluentGpu.Generated.GalleryRegistry.Pages)
        {
            var page = p;   // capture per iteration
            r.Add(new RouteDef(page.Key, _ => page.Create())
            {
                Title = page.Title, Icon = page.Icon, Category = page.Category, Order = page.Order,
                ShowInNav = !page.Hidden, SearchTerms = page.Keywords,
            });
        }
        r.Fallback = _ => FluentGpu.Generated.GalleryRegistry.Create("welcome") ?? new BoxEl();
        return r;
    }

    static NavItem[] BuildItems()
    {
        // "Home" is a top-level leaf; the rest are the sectioned tree (categories nested under the 8 IA sections).
        var sections = Registry.BuildSectionedNavTree(GallerySections.Sections);
        var items = new NavItem[sections.Length + 1];
        items[0] = new NavItem("welcome", Icons.Home, "Home");
        Array.Copy(sections, 0, items, 1, sections.Length);
        return items;
    }

    static Element Page(string key) => FluentGpu.Generated.GalleryRegistry.Create(key)
        ?? FluentGpu.Generated.GalleryRegistry.Create("welcome") ?? new BoxEl();

    readonly Signal<int> _paneToggleReq = new(0);
    readonly Signal<string> _navigateReq = new("");
    readonly Signal<string> _searchText = new("");
    readonly Signal<bool> _paletteOpen = new(false);
    // The shell-owned back stack. NavigationView.Select uses Navigator.Replace (top-level nav is a replace, never a
    // push), so the Navigator's own stack stays depth-1; the shell records history here and drives a pop through
    // NavigateRequest. _current tracks the displayed page so a back-driven select is not re-recorded as forward.
    readonly Signal<bool> _canGoBack = new(false);
    readonly List<string> _history = new();
    string _current = "";
    // Seeded lazily in Render so it picks up InitialPage (set via object-initializer AFTER the ctor); NavigationView
    // uses Navigator.Current as the initial selection, so seeding it wrong would defeat deep-linking.
    Navigator? _nav;

    // The one forward-navigation entry point (search commit / palette pick). Drives the NavigationView selection; its
    // OnSelect records history. A ""-reset first so re-navigating to the SAME page still changes the gated signal.
    void Navigate(string key)
    {
        if (key.Length == 0) return;
        _navigateReq.Value = "";
        _navigateReq.Value = key;
    }

    // Records every navigation (nav click / search / palette / back all funnel through NavigationView.Select→OnSelect).
    // Back-driven selects arrive with key == _current (GoBack sets it first), so they do not re-push.
    void RecordNav(string key)
    {
        if (key == _current) return;
        if (_current.Length > 0) _history.Add(_current);
        _current = key;
        _canGoBack.Value = _history.Count > 0;
    }

    void GoBack()
    {
        if (_history.Count == 0) return;
        string prev = _history[^1];
        _history.RemoveAt(_history.Count - 1);
        _current = prev;                       // set BEFORE driving so OnSelect(prev) is recognised as the back target
        _canGoBack.Value = _history.Count > 0;
        _navigateReq.Value = "";               // drive the NavigationView selection to the popped page
        _navigateReq.Value = prev;
    }

    // Search commit (Enter or suggestion choice) → resolve the typed/chosen title to a page key and navigate.
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
        Navigate(key);
    }

    public override Element Render()
    {
        _nav ??= new Navigator(new Route(InitialPage.Length > 0 ? InitialPage : "welcome"));
        if (_current.Length == 0) _current = _nav.Current.Name;

        if (Diag.EnvFlag("FG_SOAK") || Diag.EnvFlag("FG_STRESS_NAV") || Diag.EnvFlag("FG_WAKE_AUDIT"))
        {
            StressNavigate = key => { _navigateReq.Value = ""; _navigateReq.Value = key; };
            if (StressNavKeys.Length == 0)
                StressNavKeys = SearchIndex.Select(e => e.Key).Distinct().ToArray();
        }

        var shell = VStack(0,
            Embed.Comp(() =>
            {
                var tb = new TitleBar
                {
                    Title = "FluentGpu Gallery",
                    IconGlyph = Icons.Grid,
                    ShowBackButton = true,               // G8b: registry-driven back stack (shell-owned history)
                    BackEnabledSignal = _canGoBack,      // live enable/disable as history grows/shrinks
                    OnBack = GoBack,
                    ShowPaneToggle = true,
                    OnPaneToggle = () => _paneToggleReq.Value = _paneToggleReq.Peek() + 1,
                    ShowCaptionButtons = true,
                };
                tb.Content = avail => avail < 140f
                    ? new BoxEl()
                    : AutoSuggestBox.Create(
                        suggestions: SearchTitles,
                        placeholder: "Search controls and samples...   (Ctrl+K)",
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
                Items = NavItems,
                Content = Page,
                Navigator = _nav,
                OnSelect = RecordNav,
                ShowPaneToggle = false,
                PaneToggleRequest = _paneToggleReq,
                NavigateRequest = _navigateReq,
            })
        ) with { Grow = 1 };

        // Ctrl+K opens the command palette (a global KeyAccelerator invokes this invisible node's OnClick from anywhere);
        // the palette overlay + accelerator ride a ZStack lane over the shell (both inside the OverlayHost so the popup
        // has its service). Hit-test transparent so it never intercepts page input.
        var paletteLane = ZStack(
            shell,
            CommandPalette.Overlay(SearchIndex, Navigate, _paletteOpen),
            new BoxEl
            {
                Width = 0f, Height = 0f, HitTestVisible = false,
                Accelerator = new KeyAccelerator(Keys.K, KeyModifiers.Ctrl),
                OnClick = () => _paletteOpen.Value = !_paletteOpen.Peek(),
            }
        ) with { Grow = 1 };

        var content = ShowDiagnosticsHud ? ZStack(paletteLane, DiagnosticsOverlay()) with { Grow = 1 } : paletteLane;
#pragma warning disable FGRP001
        return Embed.Comp(() => new OverlayHost { Child = content });
#pragma warning restore FGRP001
    }

    static Element DiagnosticsOverlay() => new BoxEl
    {
        Direction = 0, Justify = FlexJustify.End, AlignItems = FlexAlign.Start, HitTestVisible = false,
        Padding = new Edges4(12, 56, 152, 0),
        Children = [Embed.Comp(() => new FrameDiagnosticsHud())],
    };
}

// FG_HUD on-screen fps/draw-count readout (moved from GalleryApp). FG_DIAG is engine diag OUTPUT only (stderr) and must
// NOT mount the HUD — the HUD's per-frame dynamic-text refresh is itself a wake source (it records+presents forever).
sealed class FrameDiagnosticsHud : Component
{
    public override Element Render() => new BoxEl
    {
        Direction = 0, Gap = 10, AlignItems = FlexAlign.Center, Padding = new Edges4(10, 5, 10, 5), MinHeight = 30,
        Fill = ColorF.FromRgba(0x13, 0x15, 0x1A, 0xCC), BorderColor = Tok.StrokeSurfaceDefault, BorderWidth = 1f,
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

    static Element Metric(string label, string placeholder, DynamicTextKind dynamicText, ColorF valueColor) => new BoxEl
    {
        Direction = 0, Gap = 4, AlignItems = FlexAlign.Center,
        Children =
        [
            new TextEl(label) { Size = 11f, Color = Tok.TextTertiary, FontFamily = "Cascadia Code" },
            new TextEl(placeholder) { Size = 12f, Bold = true, Color = valueColor, FontFamily = "Cascadia Code", DynamicText = dynamicText },
        ],
    };
}
