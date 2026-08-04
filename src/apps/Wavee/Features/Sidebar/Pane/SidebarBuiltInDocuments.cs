using System.Collections.Generic;
using Wavee.Core.Sidebar;

namespace Wavee;

// R3.0.2 — CLASSIC AS A LOCKED BUILT-IN DOCUMENT.
//
// Classic used to be a hand-built pane body (`WaveeSidebar.ExpandedBody` + `CompactBody` + private row/section builders +
// its own pinned section, drop zone and rail). That is the whole reason four left insets, two count-badge styles, two
// height ladders and two selection mechanisms existed in one app. Classic is now a DOCUMENT rendered by the ONE
// `SidebarPane`, so its metrics ARE the pane's metrics by construction.
//
// LOCKED, not editable: the document is rebuilt from code on every read (never persisted, never reachable by a customizer
// command), and its only mutable state is the three per-section collapse flags Classic has always persisted in
// `SidebarPreferences` (`ClassicPinnedOpen` / `ClassicLibraryOpen` / `ClassicPlaylistsOpen`). The pane's
// `SetSectionCollapsed` seam routes a header click to those flags instead of to the Curated document's undoable command —
// which is why a locked document can still collapse.
//
// WHY THESE DISPLAY OPTIONS (the pixel contract). The section list below is today's Classic IA verbatim, transcribed from
// the retired `WaveeSidebar.ExpandedBody`: Pinned · a leading rule · Your Library (albums · artists · liked · podcasts ·
// local, with counts) · a rule · Playlists (artwork + song-count subtitle + the create row) · a rule · the DevTools
// entry ("API Console", header-less). The Display options are chosen so the ONE shared height ladder
// (`SidebarRowMetrics.HeightFor`) reproduces Classic's 44-DIP rows exactly:
//   • Pinned / Playlists → Cozy + Subtitles ⇒ 44 with 32-DIP artwork (Classic's pinned + playlist rows).
//   • Your Library / DevTools → glyph rows with NO subtitle, so Cozy would be 40. Comfortable + Subtitles:false is 44 —
//     the same number Classic's `LibRow`/`LocalRow`/`DevToolsRow` hard-coded. (Artwork:false keeps them 16-DIP glyph rows,
//     so Comfortable's larger ART size is never reached.)
// A section's height is deliberately its SUBTITLE INTENT, not per row, because a Reorderable's slot pitch and the
// virtualizing host's extent both assume one height per section (see SidebarPaneMetrics.RowHeight).
public static class SidebarBuiltInDocuments
{
    /// <summary>The stable template id Classic's document reports. It is NOT one of <c>SidebarTemplates.All</c>: Classic is
    /// not offered in the customizer's template palette, and a Curated document must never claim this id.</summary>
    public const string ClassicId = "classic.builtin";

    /// <summary>Classic's DevTools entry — the route the retired <c>WaveeSidebar.DevToolsRow</c> pointed at.</summary>
    public const string DevToolsRoute = "api-console";

    /// <summary>Build Classic's locked document with the three section-collapse flags applied.
    ///
    /// <para>Section IDS ARE STABLE STRINGS (not <c>SidebarIds.NewSection()</c>), unlike the Curated templates: the pane
    /// keys its reorder bands, its collapse routing and its scroll/section identity off them, and a fresh id per rebuild
    /// would reset all three on every toggle. They are also how <see cref="ClassicSectionOf"/> maps a header click back to
    /// the right preference flag without a lookup table.</para></summary>
    public static SidebarCustomLayout Classic(bool pinnedOpen, bool libraryOpen, bool playlistsOpen)
        => new(ClassicId,
        [
            // The FIRST group, so it keeps Classic's `rule: false` (no leading divider). Its header hosts the quick layout
            // menu (the pane picks the first header automatically) and never disappears — even with zero pins — so that
            // entry point is always reachable (§3.1.7).
            new SidebarSectionSpec(PinnedId, SidebarSectionKind.Pinned,
                Title: null, TitleLocKey: "sidebar.pinned",
                Hidden: false, Collapsed: !pinnedOpen,
                Display: SidebarDisplayOptions.Entities with { ShowInRail = true }),

            Divider(DividerLibraryId),

            new SidebarSectionSpec(LibraryId, SidebarSectionKind.CollectionShortcuts,
                Title: null, TitleLocKey: "sidebar.yourLibrary",
                Hidden: false, Collapsed: !libraryOpen,
                // Comfortable + no subtitle ⇒ 44, Classic's landed shortcut-row height. CountBadges on (the quiet
                // SidebarCounts number now, never the accent pill).
                Display: SidebarDisplayOptions.Shortcuts with { Density = SidebarDensity.Comfortable },
                Items:
                [
                    Route(LibraryId + ":albums", "albums", "Album"),
                    Route(LibraryId + ":artists", "artists", "Contact"),
                    Route(LibraryId + ":liked", "liked", "Heart"),
                    Route(LibraryId + ":podcasts", "podcasts", "RadioTower"),
                    Route(LibraryId + ":local", "local", "Folder"),
                ]),

            Divider(DividerPlaylistsId),

            // Artwork + the song-count subtitle ⇒ Cozy 44 with 32-DIP covers, and the planner appends the create row.
            new SidebarSectionSpec(PlaylistsId, SidebarSectionKind.PlaylistTree,
                Title: null, TitleLocKey: "sidebar.playlists",
                Hidden: false, Collapsed: !playlistsOpen,
                Display: SidebarDisplayOptions.Entities),

            Divider(DividerToolsId),

            // Classic's flat DevTools row — deliberately header-less (a StaticLinks section with no title plans no
            // SectionHeader row), exactly as `DevToolsRow` rendered outside every section.
            new SidebarSectionSpec(ToolsId, SidebarSectionKind.StaticLinks,
                Title: null, TitleLocKey: null,
                Hidden: false, Collapsed: false,
                // ShowInRail off: the 56-DIP rail is a shortcut strip for places you NAVIGATE to, and a bare
                // Icons.Code glyph reads there as a stray "{}" tile with no label to rescue it. The planner drops the
                // preceding rail divider with it (a pending divider only materialises once a tile follows), and the
                // EXPANDED sidebar keeps the API Console row untouched. Doc-authored default: SidebarLayoutDoc
                // serialises display options as a DIFF against it, so a user who never touched this option simply
                // picks up the new default, and one who did keeps their override.
                Display: SidebarDisplayOptions.Shortcuts with
                {
                    Density = SidebarDensity.Comfortable, CountBadges = false, ShowInRail = false,
                },
                Items: [Route(ToolsId + ":devtools", DevToolsRoute, "Code")]),
        ]);

    // ── stable section ids ───────────────────────────────────────────────────────────────────────────────────────────
    public const string PinnedId = "classic.pinned";
    public const string LibraryId = "classic.library";
    public const string PlaylistsId = "classic.playlists";
    public const string ToolsId = "classic.tools";
    const string DividerLibraryId = "classic.rule.library";
    const string DividerPlaylistsId = "classic.rule.playlists";
    const string DividerToolsId = "classic.rule.tools";

    /// <summary>Map one of Classic's collapsible section ids onto the preference flag that owns its state. Returns null for
    /// a section that is not collapsible (the dividers, the header-less tools links), so a stray toggle is a no-op rather
    /// than a mis-write.</summary>
    public static ClassicSection? ClassicSectionOf(string sectionId) => sectionId switch
    {
        PinnedId => ClassicSection.Pinned,
        LibraryId => ClassicSection.Library,
        PlaylistsId => ClassicSection.Playlists,
        _ => null,
    };

    static SidebarSectionSpec Divider(string id) => new(id, SidebarSectionKind.Divider);

    static SidebarItemSpec Route(string id, string routeKey, string iconName)
        => new(id, SidebarItemTarget.Route, routeKey, IconOverride: iconName);
}

/// <summary>
/// One mode component's cache of <see cref="SidebarBuiltInDocuments.Classic"/>, keyed on the only state that document
/// is a function of — the three collapse flags, folded into the SAME bitmask the pane's mode epoch uses.
///
/// <para><b>Why this is load-bearing and not a micro-optimization.</b> <c>SidebarPane.PublishStage</c> decides between a
/// per-row epoch diff and a whole-window re-skin with <c>!ReferenceEquals(stage.Document, Doc)</c> — a wholesale test
/// that is CORRECT (a new document changes section titles, empty-state copy and inline controls without necessarily
/// changing a row record) but that a freshly-minted document defeats unconditionally. Classic rebuilt its document on
/// every pane render, so every publish took the wholesale arm and the landed <c>SidebarRowDiff</c> was dead code:
/// Slot×52 + Pill×44 + Chevron×6 re-rendered on every track boundary and every navigation. Library V3 already caches its
/// synthesized document this way (<c>LibraryV3Sidebar.BuildDocument</c>) and Curated serves the persisted instance,
/// which the reducer only replaces on a real change — so this makes all three modes agree.</para>
///
/// <para>Per MODE-COMPONENT INSTANCE, never static: the docked pane and the narrow drawer are two independent mounts and
/// must not share mutable state. The cached document is immutable, so handing the same instance to both is safe.</para>
/// </summary>
public sealed class ClassicDocumentCache
{
    SidebarCustomLayout? _doc;
    int _flags = -1;

    /// <summary>Classic's collapse flags folded into one int — bit 0 pinned, bit 1 library, bit 2 playlists. This IS the
    /// mode epoch's bitmask; the two must not drift, or a toggle would re-plan without rebuilding the document.</summary>
    public static int FlagsOf(bool pinnedOpen, bool libraryOpen, bool playlistsOpen)
        => (pinnedOpen ? 1 : 0) | (libraryOpen ? 2 : 0) | (playlistsOpen ? 4 : 0);

    /// <summary>The document for these flags: the SAME instance while they are unchanged, a fresh one on any flip.</summary>
    public SidebarCustomLayout Get(bool pinnedOpen, bool libraryOpen, bool playlistsOpen)
    {
        int flags = FlagsOf(pinnedOpen, libraryOpen, playlistsOpen);
        if (_doc is { } cached && _flags == flags) return cached;
        _flags = flags;
        _doc = SidebarBuiltInDocuments.Classic(pinnedOpen, libraryOpen, playlistsOpen);
        return _doc;
    }
}
