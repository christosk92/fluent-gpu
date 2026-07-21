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

// ── GalleryShell (WS7 W7.0) ───────────────────────────────────────────────────────────────────────────────────────
// The registry-driven capability gallery: the nav tree, search index, and page factory are ALL derived from the one
// source of truth — the generated FluentGpu.Generated.GalleryRegistry ([GalleryPage] tags) — via a RouteRegistry bridge
// + the hand-authored GallerySections IA table. It replaces GalleryApp's three hand-synced tables (NavItem tree /
// ControlCatalog / search index) and the 100-arm page switch: the compile IS the sync check. Built on the REAL
// Navigator (Route.Name = page key) wired into NavigationView; deep-linkable via InitialPage; the SoakProbe
// StressNavigate seam is preserved (keys now come from the registry).
//
// NOTE (G8b remainder): the Ctrl+K command palette and the CanGoBack/Pop TitleBar back button are deferred — the shell
// mirrors GalleryApp's proven TitleBar+NavigationView+OverlayHost wiring (no back stack yet). GalleryApp is kept intact
// as the "gallery-legacy" fallback until GalleryShell is verified in the running app.
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
    // Seeded lazily in Render so it picks up InitialPage (set via object-initializer AFTER the ctor); NavigationView
    // uses Navigator.Current as the initial selection, so seeding it wrong would defeat deep-linking.
    Navigator? _nav;

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
        _navigateReq.Value = "";        // ""-reset so re-searching the same page still changes the equality-gated signal
        _navigateReq.Value = key;
    }

    public override Element Render()
    {
        _nav ??= new Navigator(new Route(InitialPage.Length > 0 ? InitialPage : "welcome"));

        if (Diag.EnvFlag("FG_SOAK") || Diag.EnvFlag("FG_STRESS_NAV") || Diag.EnvFlag("FG_WAKE_AUDIT"))
        {
            // Reuse GalleryApp's static seam so SoakProbe needs no change; keys now come from the registry.
            GalleryApp.StressNavigate = key => { _navigateReq.Value = ""; _navigateReq.Value = key; };
            if (GalleryApp.StressNavKeys.Length == 0)
                GalleryApp.StressNavKeys = SearchIndex.Select(e => e.Key).Distinct().ToArray();
        }

        var shell = VStack(0,
            Embed.Comp(() =>
            {
                var tb = new TitleBar
                {
                    Title = "FluentGpu Gallery",
                    IconGlyph = Icons.Grid,
                    ShowBackButton = false,          // registry-driven back stack (CanGoBack/Pop) lands in G8b
                    ShowPaneToggle = true,
                    OnPaneToggle = () => _paneToggleReq.Value = _paneToggleReq.Peek() + 1,
                    ShowCaptionButtons = true,
                };
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
                Items = NavItems,
                Content = Page,
                Navigator = _nav,
                ShowPaneToggle = false,
                PaneToggleRequest = _paneToggleReq,
                NavigateRequest = _navigateReq,
            })
        ) with { Grow = 1 };

        var content = ShowDiagnosticsHud ? ZStack(shell, DiagnosticsOverlay()) with { Grow = 1 } : shell;
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
