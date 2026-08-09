using System;
using FluentGpu.Controls;   // Route
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core.Sidebar;

namespace Wavee;

// MODE C / "Wavee Curated" (spec §C5) — the section-based sidebar the user authors in the full-page customizer.
// Mounted only by SidebarHost, under the "sidebar.curated" key (and a SECOND, independent mount for the narrow drawer);
// also mounted by the customizer's live-preview column, which is why the CTOR SIGNATURE IS FROZEN.
//
// R3.0.4 — this used to BE the renderer (750 lines of plan/epoch/reorder/rail machinery plus a 900-line row slot). Full
// unification moved all of it to `Features/Sidebar/Pane/` (`SidebarPane` + `SidebarPaneSlot` + `SidebarPaneRail` + the
// text/inline-control helpers), so Curated is now what every mode is: a DOCUMENT + a `SidebarPaneConfig`. Its document is
// the persisted one (`SidebarPreferences.Layout`), it is EDITABLE, and nothing else distinguishes it from Classic.
//
// LIVE EDITS still work exactly as before: every mutation goes through `SidebarPreferences.Dispatch(command)` → reducer →
// undo pre-image → `LayoutVersion` bump → autosave, and the pane subscribes `LayoutVersion` — so the customizer never
// talks to the renderer, it dispatches, and both the live pane and its own preview re-plan from the one document.
sealed class CuratedSidebar : Component
{
    readonly Signal<Route> _route;
    readonly Action<string, string?> _go;
    readonly Signal<bool> _compact;
    readonly Signal<float> _expandedWidth;
    readonly bool _inDrawer;

    SidebarPreferences? _prefs;
    LibraryBridge? _lib;

    public CuratedSidebar(Signal<Route> route, Action<string, string?> go, Signal<bool> compact,
                          Signal<float> expandedWidth, bool inDrawer = false)
    {
        _route = route; _go = go; _compact = compact; _expandedWidth = expandedWidth; _inDrawer = inDrawer;
    }

    public override Element Render()
    {
        // Refreshed each render, read by the config's delegates (the landed `WaveeSidebar._acts` pattern): a service
        // instance is reference-stable, so this never churns and the frozen config always sees the live one.
        _prefs = UseContext(SidebarPreferences.Slot);
        _lib = UseContext(LibraryBridge.Slot);

        // A probe / isolated harness mount has no preference service and therefore no document: fall back to the built-in
        // Curated template ONCE (Build mints fresh ids per call, so it must be memoized) and let every dynamic section
        // resolve to skeletons. That is the honest shape — never a blank pane.
        var fallback = UseMemo(static () => SidebarLayoutDefaults.CuratedLayout(), DepKey.Empty);

        // The config is built ONCE and frozen into the pane (the component-props contract). Every member is a delegate, so
        // it reads live state at the pane's render time.
        var config = UseMemo(() => new SidebarPaneConfig
        {
            Design = SidebarDesign.Curated,
            ScrollKeyPrefix = "sidebar.curated",
            Document = () => _prefs?.Layout ?? fallback,
            // The document's own version is already in the pane's plan DepKey, so Curated needs no extra mode epoch.
            SetSectionCollapsed = (id, collapsed) => _prefs?.Dispatch(new SetSectionCollapsed(id, collapsed)),
            ReadOnly = false,
            SearchHead = true,
            // O3 — the customizable shortcut band, at its new render site. Set IDENTICALLY by all three modes: it is the
            // app's navigation band (one global list on the layout document), not a Curated affordance — and it is NOT
            // part of the document's sections, so the customizer's Top bar card remains its only structural editor.
            NavBand = () => SidebarNavBand.Head(_prefs, _route, _go),
            RailHead = () => SidebarNavBand.RailHead(_prefs, _route, _go),
            OnCustomize = OpenCustomizer,
            OnCreatePlaylist = CreatePlaylist,
        }, DepKey.Empty);

        return Embed.Comp(() => new SidebarPane(config, _route, _go, _compact, _expandedWidth, _inDrawer));
    }

    void OpenCustomizer()
    {
        _prefs?.SwitchDesign(SidebarDesign.Curated);   // no-op when already Curated
        _go(SidebarLayoutMenu.CustomizeRoute, null);
    }

    /// <summary>The PlaylistTree section's create affordance (§C5.1's <c>CreateAction</c> row). POST a real empty playlist,
    /// then navigate to it — the landed Classic behaviour, verbatim.</summary>
    void CreatePlaylist()
    {
        if (_lib is not { } lib) return;
        _ = Run();
        async System.Threading.Tasks.Task Run()
        {
            try
            {
                string uri = await lib.CreatePlaylistAsync(Loc.Get(Strings.Sidebar.NewPlaylist)).ConfigureAwait(false);
                _go("pl:" + uri, null);
            }
            catch (Exception ex)
            {
                WaveeLog.Instance.Error("sidebar", "sidebar.action.failed", "Could not create playlist", ex,
                    WaveeLogField.Of("action", "createPlaylist"),
                    WaveeLogField.Of("design", "curated"));
                Toast.Show(Loc.Get(Strings.Common.ErrorTitle), new ToastOptions { Severity = InfoBarSeverity.Error });
            }
        }
    }
}
