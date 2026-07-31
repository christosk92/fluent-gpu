using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;
using Wavee.Core.Sidebar;

namespace Wavee;

// The full-page customizer's PURE model (plan §C4 + the visual-remediation amendment): the progressive four-tier
// tier ladder, the searchable section palette, the outline flattening + drag translation, and the opaque-config editing
// helpers the schema-generated property controls write through.
//
// ENGINE-FREE BY CONSTRUCTION (System + System.Text.Json + Wavee.Core + the engine-free Data/ contract types only), for
// the same reason as Features/Sidebar/Data/*: src/apps/Wavee.Tests source-includes THIS file, so SidebarCustomizerLayout
// Tests drive the REAL tier hysteresis, the REAL palette filter, the REAL drag translation and the REAL config rewriter
// rather than copies of them. Nothing here may reference Signal<T>, Element, Icons, Loc or Tok — glyph NAMES travel as
// strings (SidebarCustomizerPalette maps them app-side) and every label is a loc KEY resolved at the UI edge.

/// <summary>The customizer's region tier:
/// <see cref="Canvas"/> = Palette + Outline + Inspector + persistent Preview ·
/// <see cref="Full"/> = Palette + Outline + Inspector · <see cref="Compact"/> = Outline + Inspector (palette/templates
/// move to command overflow) · <see cref="Narrow"/> = Outline only (the inspector becomes a bottom sheet).
/// Ordered WIDEST-FIRST so "a smaller number is a wider layout" makes the hysteresis rule readable.</summary>
public enum SidebarCustomizerTier : byte { Canvas = 0, Full = 1, Compact = 2, Narrow = 3 }

/// <summary>Region geometry + the tier ladder. Pure and unit-tested: a responsive rule that lives in a render is a rule
/// nobody can pin (the <c>ShellResponsiveLayout</c> / <c>DetailLayoutBreakpoints</c> precedent).</summary>
public static class SidebarCustomizerLayout
{
    /// <summary>The fixed palette column (REVISION 2: 232 DIP).</summary>
    public const float PaletteWidth = 232f;

    /// <summary>The fixed inspector column (REVISION 2: 320 DIP).</summary>
    public const float InspectorWidth = 320f;

    /// <summary>The persistent live-preview column at the Canvas tier.</summary>
    public const float PreviewWidth = 360f;

    /// <summary>The elastic outline column never measures below this — past it the tier drops instead of squeezing.</summary>
    public const float OutlineMinWidth = 320f;

    /// <summary>≥ this ⇒ <see cref="SidebarCustomizerTier.Canvas"/> (all four regions).
    /// <para>LOWERED 1480 → 1320. These thresholds measure the PAGE CONTENT width, not the window: the docked sidebar eats
    /// ~280 DIP before this page is ever measured, so a ~1330-wide window arrived here as ~1050 and landed in Compact —
    /// which is why the reporter never saw the eyebrow, the saved-locally dot, the inline Reset or the preview column.
    /// 1320 content ⇒ roughly a 1600-wide window with the sidebar expanded.</para></summary>
    public const float CanvasEnterW = 1320f;

    /// <summary>≥ this ⇒ <see cref="SidebarCustomizerTier.Full"/> (palette + outline + inspector).
    /// <para>LOWERED 1180 → 1000 for the same reason, and it still fits by construction: Palette 232 + Inspector 320 +
    /// <see cref="OutlineMinWidth"/> 320 + two 12-DIP region gaps + 32 DIP of page padding = 928, so 1000 leaves the
    /// elastic outline 72 DIP of slack above its own minimum before the tier has to drop.</para></summary>
    public const float FullEnterW = 1000f;

    /// <summary>≥ this ⇒ <see cref="SidebarCustomizerTier.Compact"/> (outline + inspector).</summary>
    public const float CompactEnterW = 820f;

    /// <summary>Widen immediately, shrink only this far past the threshold — the shell's 24-DIP idiom, so a pane resize
    /// that lands exactly on a breakpoint cannot oscillate.</summary>
    public const float HysteresisDip = 24f;

    /// <summary>The inspector's height when it is a bottom sheet (<see cref="SidebarCustomizerTier.Narrow"/>): a share of
    /// the page height, clamped so it neither hides the outline nor collapses into a strip.</summary>
    public static float SheetHeight(float pageHeight)
    {
        if (pageHeight <= 0f) return 320f;
        float h = pageHeight * 0.55f;
        return h < 240f ? Math.Min(240f, pageHeight) : h > 520f ? 520f : h;
    }

    /// <summary>The tier a width maps to with no memory (the first measure).</summary>
    public static SidebarCustomizerTier NominalTier(float width)
        => width >= CanvasEnterW ? SidebarCustomizerTier.Canvas
         : width >= FullEnterW ? SidebarCustomizerTier.Full
         : width >= CompactEnterW ? SidebarCustomizerTier.Compact
         : SidebarCustomizerTier.Narrow;

    /// <summary>The tier for <paramref name="width"/> given the tier currently shown (<paramref name="wasTier"/>, −1 =
    /// not measured yet). Widening applies immediately; NARROWING requires <see cref="HysteresisDip"/> past the
    /// threshold, so dragging the window across a breakpoint cannot flicker two layouts.</summary>
    public static SidebarCustomizerTier Tier(float width, int wasTier)
    {
        var now = NominalTier(width);
        if (wasTier < 0 || wasTier > (int)SidebarCustomizerTier.Narrow) return now;
        var was = (SidebarCustomizerTier)wasTier;
        if (now == was) return now;
        if ((int)now < (int)was) return now;                       // widening — immediate
        return NominalTier(width + HysteresisDip) == was ? was : now;   // narrowing — only past the dip
    }

    /// <summary>Whether the palette is an inline column (Canvas/Full) or command overflow (Compact/Narrow).</summary>
    public static bool PaletteInline(SidebarCustomizerTier tier) => tier <= SidebarCustomizerTier.Full;

    /// <summary>Whether the inspector is an inline column (Canvas/Full/Compact) or a bottom sheet (Narrow).</summary>
    public static bool InspectorInline(SidebarCustomizerTier tier) => tier != SidebarCustomizerTier.Narrow;

    /// <summary>Only the Canvas tier has enough room for a persistent fourth preview region.</summary>
    public static bool PreviewInline(SidebarCustomizerTier tier) => tier == SidebarCustomizerTier.Canvas;

    /// <summary>Supporting header chrome is visible: it leaves before any command is put under pressure. Since R3.2 this
    /// gates the header's SECOND LINE (the active-template eyebrow, which replaced the literal subtitle), the
    /// saved-locally indicator and the inline Reset button — the same "chrome first" rule, one more level. The NAME is
    /// kept because it is the tested public surface of this pure table.</summary>
    public static bool SubtitleVisible(SidebarCustomizerTier tier) => tier <= SidebarCustomizerTier.Full;

    /// <summary>Width protected for the title lane before fitting commands. The title may still ellipsize.</summary>
    public static float TitleReserve(SidebarCustomizerTier tier) => tier switch
    {
        SidebarCustomizerTier.Canvas or SidebarCustomizerTier.Full => 240f,
        SidebarCustomizerTier.Compact => 120f,
        _ => 0f,
    };
}

// ── command pressure ────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The query controls a section kind owns. Kept beside the other pure customizer tables so the property panel
/// cannot grow a second, untested kind switch.</summary>
public readonly record struct SidebarQueryPanelShape(bool ShowKinds, bool ShowQualifier)
{
    public static SidebarQueryPanelShape For(SidebarSectionKind kind, bool qualifiersAvailable) => kind switch
    {
        SidebarSectionKind.PlaylistTree => new(false, true),
        SidebarSectionKind.EntityList => new(true, qualifiersAvailable),
        _ => new(false, false),
    };
}

/// <summary>The discrete-number editor's one normalization rule. The UI uses the returned integer both for dispatch and
/// for rejection snap-back; tests drive this pure seam instead of copying NumberBox behavior.</summary>
public static class SidebarNumberEdit
{
    public static int Normalize(double value, int min, int max)
        => Math.Clamp((int)Math.Round(value), min, max);
}

[Flags]
public enum SidebarCustomizerInlineCommand : byte
{
    None = 0,
    Add = 1,
    Undo = 2,
    Redo = 4,
}

/// <summary>Measured/final extents for the compact native CommandBar buttons and the external accent Done action.
/// Defaults match the controls' public compact metrics; tests can inject long-localization measurements.</summary>
public readonly record struct SidebarCustomizerCommandWidths(
    float Add,
    float Undo,
    float Redo,
    float More,
    float Done,
    float Gap)
{
    public static SidebarCustomizerCommandWidths Default => new(48f, 48f, 48f, 48f, 76f, 8f);
}

public readonly record struct SidebarCustomizerCommandFit(
    SidebarCustomizerInlineCommand Inline,
    float NativeBarWidth,
    float TotalWidth)
{
    public bool Has(SidebarCustomizerInlineCommand command) => (Inline & command) != 0;
    internal int Richness =>
        (Has(SidebarCustomizerInlineCommand.Undo) ? 4 : 0)
        + (Has(SidebarCustomizerInlineCommand.Redo) ? 2 : 0)
        + (Has(SidebarCustomizerInlineCommand.Add) ? 1 : 0);
}

/// <summary>Pure priority fit for the customizer header. Done and More are mandatory; optional commands demote to
/// overflow immediately while narrowing and promote only with a 16-DIP reserve.</summary>
public static class SidebarCustomizerCommandLayout
{
    public const float PromotionHysteresis = 16f;

    public static SidebarCustomizerCommandFit Resolve(
        float available,
        in SidebarCustomizerCommandWidths widths,
        SidebarCustomizerTier tier,
        SidebarCustomizerCommandFit? previous = null)
    {
        available = MathF.Max(0f, available);
        var candidate = ResolveCore(available, in widths, tier);
        if (previous is not { } old || candidate.Richness <= old.Richness) return candidate;
        return ResolveCore(MathF.Max(0f, available - PromotionHysteresis), in widths, tier);
    }

    static SidebarCustomizerCommandFit ResolveCore(
        float available,
        in SidebarCustomizerCommandWidths widths,
        SidebarCustomizerTier tier)
    {
        float native = MathF.Max(0f, widths.More);
        float total = native + MathF.Max(0f, widths.Gap) + MathF.Max(0f, widths.Done);
        var inline = SidebarCustomizerInlineCommand.None;

        // UNDO/REDO ARE ALLOWED AT EVERY TIER (round-3 defect 4). This used to read `tier != Narrow`, which BANNED them
        // outright instead of letting the WIDTH decide — so a narrow window collapsed to "… Done" with no history
        // affordance at all, even though the two 48-DIP buttons fit with hundreds of DIP to spare. They are the
        // highest-frequency commands in an editor; the budget below is the only thing that should ever demote them.
        bool allowAdd = tier <= SidebarCustomizerTier.Full;

        void Add(SidebarCustomizerInlineCommand command, float width)
        {
            float next = total + MathF.Max(0f, width);
            if (next > available) return;
            total = next;
            native += MathF.Max(0f, width);
            inline |= command;
        }

        // History is more important than creation under pressure; Add remains one click away in overflow.
        Add(SidebarCustomizerInlineCommand.Undo, widths.Undo);
        Add(SidebarCustomizerInlineCommand.Redo, widths.Redo);
        if (allowAdd) Add(SidebarCustomizerInlineCommand.Add, widths.Add);

        return new SidebarCustomizerCommandFit(inline, native, total);
    }
}

// ── the palette (REVISION 2's 17 entries, grouped + searchable) ────────────────────────────────────────────────────────

/// <summary>The palette's groups, in render order (REVISION 2: Navigation / Library / Playback / Dynamic feeds / Layout /
/// Actions / Extensions).</summary>
public enum SidebarPaletteGroup : byte
{
    Navigation = 0, Library = 1, Playback = 2, DynamicFeeds = 3, Layout = 4, Actions = 5, Extensions = 6,
}

/// <summary>What the palette ADDS when clicked. Kept as data (not a switch in a render) so the palette, its search
/// filter and the tests all read one table.</summary>
public enum SidebarPaletteAdd : byte
{
    /// <summary>A plain <c>AddSection(Kind)</c>.</summary>
    Section = 0,
    /// <summary>An <c>AddSection(JumpBackIn)</c> that then flips its recents source to the play log.</summary>
    RecentlyPlayed = 1,
    /// <summary>An <c>AddSection(Extension, Extension: ref)</c> for <see cref="SidebarPaletteEntry.ContributionId"/>.</summary>
    Contribution = 2,
    /// <summary>The action picker, then <c>AddSection(StaticLinks, Item: the bound action item)</c> — ONE undo step.</summary>
    ActionShortcut = 3,
    /// <summary>The contribution picker (every registered source), then <see cref="Contribution"/>.</summary>
    AnyContribution = 4,
    /// <summary>A pre-seeded StaticLinks section containing the localized Liked Songs route.</summary>
    LikedSongsShortcut = 5,
}

/// <summary>One palette row. <paramref name="IconName"/> is a GLYPH NAME (this file is engine-free — the app-side
/// palette view maps it); the two loc keys come from <c>SidebarSectionKinds</c> wherever a kind owns them, so a section's
/// name is never spelled twice.</summary>
public sealed record SidebarPaletteEntry(
    string Id,
    SidebarPaletteGroup Group,
    SidebarSectionKind Kind,
    SidebarPaletteAdd Add,
    string NameLocKey,
    string DescriptionLocKey,
    string IconName,
    string? ContributionId = null);

/// <summary>The palette table + its pure search filter.</summary>
public static class SidebarPalette
{
    /// <summary>REVISION 2's palette, in group order. Seventeen ENTRIES in the spec's wording — eighteen rows here,
    /// because "Queue/Now Playing" names two distinct first-party contributions (<c>wavee.queue</c> and
    /// <c>wavee.nowPlaying</c>) with their own loc keys, and offering them as one row would make the second
    /// unreachable.</summary>
    public static readonly SidebarPaletteEntry[] All =
    [
        // Navigation
        new("pinned", SidebarPaletteGroup.Navigation, SidebarSectionKind.Pinned, SidebarPaletteAdd.Section,
            "sidebar.section.pinned", "sidebar.section.pinnedSub", "Pin"),
        new("shortcuts", SidebarPaletteGroup.Navigation, SidebarSectionKind.CollectionShortcuts,
            SidebarPaletteAdd.Section, "sidebar.section.shortcuts", "sidebar.section.shortcutsSub", "Heart"),
        new("likedSongs", SidebarPaletteGroup.Navigation, SidebarSectionKind.StaticLinks,
            SidebarPaletteAdd.LikedSongsShortcut, "nav.likedSongs", "sidebar.customizer.likedSongsSub", "Heart"),
        new("staticLinks", SidebarPaletteGroup.Navigation, SidebarSectionKind.StaticLinks, SidebarPaletteAdd.Section,
            "sidebar.section.staticLinks", "sidebar.section.staticLinksSub", "Link"),

        // Library
        new("playlistTree", SidebarPaletteGroup.Library, SidebarSectionKind.PlaylistTree, SidebarPaletteAdd.Section,
            "sidebar.section.playlistTree", "sidebar.section.playlistTreeSub", "Folder"),
        new("entityList", SidebarPaletteGroup.Library, SidebarSectionKind.EntityList, SidebarPaletteAdd.Section,
            "sidebar.section.entityList", "sidebar.section.entityListSub", "Filter"),
        new("entityEmbed", SidebarPaletteGroup.Library, SidebarSectionKind.EntityEmbed, SidebarPaletteAdd.Section,
            "sidebar.section.entityEmbed", "sidebar.section.entityEmbedSub", "FavoriteStar"),

        // Playback
        new("recentlyPlayed", SidebarPaletteGroup.Playback, SidebarSectionKind.JumpBackIn,
            SidebarPaletteAdd.RecentlyPlayed, "sidebar.section.recentlyPlayed", "sidebar.section.recentlyPlayedSub",
            "Headphones"),
        new("queue", SidebarPaletteGroup.Playback, SidebarSectionKind.Extension, SidebarPaletteAdd.Contribution,
            "sidebar.section.queue", "sidebar.section.queueSub", "Queue", SidebarContributions.Queue),
        new("nowPlaying", SidebarPaletteGroup.Playback, SidebarSectionKind.Extension, SidebarPaletteAdd.Contribution,
            "sidebar.section.nowPlaying", "sidebar.section.nowPlayingSub", "Play", SidebarContributions.NowPlaying),

        // Dynamic feeds
        new("jumpBackIn", SidebarPaletteGroup.DynamicFeeds, SidebarSectionKind.JumpBackIn, SidebarPaletteAdd.Section,
            "sidebar.section.jumpBackIn", "sidebar.section.jumpBackInSub", "Clock"),
        new("artistTopTracks", SidebarPaletteGroup.DynamicFeeds, SidebarSectionKind.Extension,
            SidebarPaletteAdd.Contribution, "sidebar.section.artistTopTracks", "sidebar.section.artistTopTracksSub",
            "Contact", SidebarContributions.ArtistTopTracks),
        new("newReleases", SidebarPaletteGroup.DynamicFeeds, SidebarSectionKind.NewReleases, SidebarPaletteAdd.Section,
            "sidebar.section.newReleases", "sidebar.section.newReleasesSub", "Album"),
        new("concerts", SidebarPaletteGroup.DynamicFeeds, SidebarSectionKind.Concerts, SidebarPaletteAdd.Section,
            "sidebar.section.concerts", "sidebar.section.concertsSub", "Calendar"),

        // Layout
        new("group", SidebarPaletteGroup.Layout, SidebarSectionKind.CustomGroup, SidebarPaletteAdd.Section,
            "sidebar.section.group", "sidebar.section.groupSub", "Grid"),
        new("header", SidebarPaletteGroup.Layout, SidebarSectionKind.Header, SidebarPaletteAdd.Section,
            "sidebar.section.header", "sidebar.section.headerSub", "Font"),
        new("divider", SidebarPaletteGroup.Layout, SidebarSectionKind.Divider, SidebarPaletteAdd.Section,
            "sidebar.section.divider", "sidebar.section.dividerSub", "Remove"),

        // Actions
        new("actionShortcut", SidebarPaletteGroup.Actions, SidebarSectionKind.StaticLinks,
            SidebarPaletteAdd.ActionShortcut, "sidebar.customizer.itemAction", "sidebar.customizer.itemActionSub",
            "RefineSparkle"),

        // Extensions
        new("extension", SidebarPaletteGroup.Extensions, SidebarSectionKind.Extension,
            SidebarPaletteAdd.AnyContribution, "sidebar.section.extension", "sidebar.section.extensionSub", "Code"),
    ];

    /// <summary>Group order == the enum order; the palette renders headers in exactly this sequence.</summary>
    public static readonly SidebarPaletteGroup[] Groups =
    [
        SidebarPaletteGroup.Navigation, SidebarPaletteGroup.Library, SidebarPaletteGroup.Playback,
        SidebarPaletteGroup.DynamicFeeds, SidebarPaletteGroup.Layout, SidebarPaletteGroup.Actions,
        SidebarPaletteGroup.Extensions,
    ];

    public static string GroupLocKey(SidebarPaletteGroup group) => group switch
    {
        SidebarPaletteGroup.Navigation => "sidebar.palette.navigation",
        SidebarPaletteGroup.Library => "sidebar.palette.library",
        SidebarPaletteGroup.Playback => "sidebar.palette.playback",
        SidebarPaletteGroup.DynamicFeeds => "sidebar.palette.dynamic",
        SidebarPaletteGroup.Layout => "sidebar.palette.layout",
        SidebarPaletteGroup.Actions => "sidebar.palette.actions",
        _ => "sidebar.palette.extensions",
    };

    /// <summary>Trim + lowercase, "" for nothing typed. Normalized ONCE per keystroke, never per row.</summary>
    public static string NormalizeQuery(string? query)
        => string.IsNullOrWhiteSpace(query) ? "" : query!.Trim().ToLowerInvariant();

    /// <summary>Token-wise contains: EVERY whitespace-separated token of the (already normalized) query must appear in
    /// the label or the description, so "top art" finds "Artist top tracks". An empty query matches everything.</summary>
    public static bool Matches(string normalizedQuery, string? label, string? description)
    {
        if (normalizedQuery.Length == 0) return true;
        int i = 0;
        while (i < normalizedQuery.Length)
        {
            while (i < normalizedQuery.Length && normalizedQuery[i] == ' ') i++;
            int start = i;
            while (i < normalizedQuery.Length && normalizedQuery[i] != ' ') i++;
            if (i == start) break;
            var token = normalizedQuery.AsSpan(start, i - start);
            bool hit = Contains(label, token) || Contains(description, token);
            if (!hit) return false;
        }
        return true;

        static bool Contains(string? haystack, ReadOnlySpan<char> token)
            => haystack is { Length: > 0 } && haystack.AsSpan().Contains(token, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Append the entries matching <paramref name="query"/> (in table order) to <paramref name="into"/>.
    /// The two text projections are delegates because the localized strings live at the UI edge — this file never calls
    /// <c>Loc</c>. Returns how many were appended.</summary>
    public static int Filter(string? query, Func<SidebarPaletteEntry, string> labelOf,
                            Func<SidebarPaletteEntry, string?>? descriptionOf, List<SidebarPaletteEntry> into)
    {
        ArgumentNullException.ThrowIfNull(labelOf);
        ArgumentNullException.ThrowIfNull(into);
        into.Clear();
        string q = NormalizeQuery(query);
        for (int i = 0; i < All.Length; i++)
        {
            var e = All[i];
            if (!Matches(q, labelOf(e), descriptionOf?.Invoke(e))) continue;
            into.Add(e);
        }
        return into.Count;
    }
}

// ── display options: the property panel's row order + the int projection its generated controls bind ───────────────────

/// <summary>The display-option half of the property panel, kept pure so the panel is a RENDERER of this table rather
/// than a hand-written per-kind form (the drift <c>SidebarSectionKinds.AllowsDisplayField</c> exists to prevent).</summary>
public static class SidebarDisplayValues
{
    /// <summary>Row order in the property panel. Which of these a KIND actually shows is
    /// <c>SidebarSectionKinds.AllowsDisplayField</c>'s answer — never a second table.</summary>
    public static readonly SidebarDisplayField[] Order =
    [
        SidebarDisplayField.Density,
        SidebarDisplayField.Presentation,
        SidebarDisplayField.GridColumns,
        SidebarDisplayField.Artwork,
        SidebarDisplayField.Subtitles,
        SidebarDisplayField.CountBadges,
        SidebarDisplayField.InlineControls,
        SidebarDisplayField.PlayButton,
        SidebarDisplayField.RecentsSource,
        SidebarDisplayField.MaxItems,
        SidebarDisplayField.EmptyBehavior,
        SidebarDisplayField.CollapsedByDefault,
        SidebarDisplayField.ShowInRail,
    ];

    /// <summary>The field's current value as the int <c>SetDisplayOption</c> carries (bools encode 0/1) — the exact
    /// inverse of <c>SidebarLayoutReducer.WithField</c>.</summary>
    public static int Read(SidebarDisplayOptions? options, SidebarDisplayField field)
    {
        var o = options ?? SidebarDisplayOptions.Default;
        return field switch
        {
            SidebarDisplayField.Density => (int)o.Density,
            SidebarDisplayField.Presentation => (int)o.Presentation,
            SidebarDisplayField.Artwork => o.Artwork ? 1 : 0,
            SidebarDisplayField.Subtitles => o.Subtitles ? 1 : 0,
            SidebarDisplayField.CountBadges => o.CountBadges ? 1 : 0,
            SidebarDisplayField.CollapsedByDefault => o.CollapsedByDefault ? 1 : 0,
            SidebarDisplayField.ShowInRail => o.ShowInRail ? 1 : 0,
            SidebarDisplayField.MaxItems => o.MaxItems,
            SidebarDisplayField.GridColumns => o.GridColumns,
            SidebarDisplayField.InlineControls => o.InlineControls ? 1 : 0,
            SidebarDisplayField.PlayButton => o.PlayButton ? 1 : 0,
            SidebarDisplayField.RecentsSource => (int)o.Recents,
            SidebarDisplayField.EmptyBehavior => (int)o.EmptyBehavior,
            _ => 0,
        };
    }

    /// <summary>True for the fields the panel renders as a <c>ToggleSwitch</c> (everything that encodes 0/1).</summary>
    public static bool IsFlag(SidebarDisplayField field) => field is SidebarDisplayField.Artwork
        or SidebarDisplayField.Subtitles or SidebarDisplayField.CountBadges
        or SidebarDisplayField.CollapsedByDefault or SidebarDisplayField.ShowInRail
        or SidebarDisplayField.InlineControls or SidebarDisplayField.PlayButton;

    /// <summary>The row's label loc key (the catalog's <c>sidebar.option.*</c> family).</summary>
    public static string LabelLocKey(SidebarDisplayField field) => field switch
    {
        SidebarDisplayField.Density => "sidebar.option.density",
        SidebarDisplayField.Presentation => "sidebar.option.presentation",
        SidebarDisplayField.Artwork => "sidebar.option.artwork",
        SidebarDisplayField.Subtitles => "sidebar.option.subtitles",
        SidebarDisplayField.CountBadges => "sidebar.option.countBadges",
        SidebarDisplayField.CollapsedByDefault => "sidebar.option.collapsedByDefault",
        SidebarDisplayField.ShowInRail => "sidebar.option.showInRail",
        SidebarDisplayField.MaxItems => "sidebar.option.maxItems",
        SidebarDisplayField.GridColumns => "sidebar.option.gridColumns",
        SidebarDisplayField.InlineControls => "sidebar.a11y.sortView",          // the inline filter/sort row
        SidebarDisplayField.PlayButton => "detail.play",
        SidebarDisplayField.RecentsSource => "sidebar.option.sortRecents",
        SidebarDisplayField.EmptyBehavior => "sidebar.option.emptyBehavior",
        _ => "",
    };

    /// <summary>The choice labels for the ENUM fields (empty for flags and numbers). The panel picks the control from these
    /// labels at render time — Segmented when they are few and short, a ComboBox otherwise (<c>CzRow.Choice</c>) — so the
    /// ORDER here is load-bearing: index i must be enum value i, which
    /// <c>DisplayValues_EveryFieldRoundTripsEveryChoiceThePanelCanOffer</c> pins.</summary>
    public static string[] ChoiceLocKeys(SidebarDisplayField field) => field switch
    {
        SidebarDisplayField.Density =>
            ["sidebar.option.densityCompact", "sidebar.option.densityCozy", "sidebar.option.densityComfortable"],
        SidebarDisplayField.Presentation =>
            ["sidebar.option.presentationList", "sidebar.option.presentationGrid"],
        SidebarDisplayField.RecentsSource =>
            ["sidebar.recents.sourceVisited", "sidebar.recents.sourcePlayed"],
        SidebarDisplayField.EmptyBehavior =>
            [
                "sidebar.option.emptyDefault", "sidebar.option.emptyHide",
                "sidebar.option.emptyCompact", "sidebar.option.emptyAction",
            ],
        _ => Array.Empty<string>(),
    };
}

// ── the outline (flattened document + the drag translation) ───────────────────────────────────────────────────────────

/// <summary>One outline row: a top-level section (<see cref="Depth"/> 0) or a <c>CustomGroup</c> child (depth 1). Flat,
/// because the outline IS a flat reorderable list and <c>Reorderable</c> works in slot indices.</summary>
public readonly record struct SidebarOutlineRow(
    string SectionId,
    string? ParentId,
    SidebarSectionKind Kind,
    int Depth,
    int IndexInParent,
    bool Hidden,
    bool IsGroup,
    int ChildCount)
{
    /// <summary>52 DIP top level / 44 DIP child (R3.2 item 2 — a top-level row is a CARD carrying a 24-DIP kind chip, a
    /// title and a kind subtitle; a depth-1 child is one line) — also the <c>Reorderable.ExtentOf</c> answer.</summary>
    public float Height => Depth == 0 ? 52f : 44f;
}

/// <summary>Document → outline rows. Pure, so the flattening (and therefore every index the drag translation works in)
/// is unit-tested.</summary>
public static class SidebarOutlineRows
{
    /// <summary>Rebuild <paramref name="into"/> (caller-owned — the outline reuses one list per mount).</summary>
    public static int Build(SidebarCustomLayout? layout, List<SidebarOutlineRow> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        into.Clear();
        if (layout is null) return 0;

        var sections = layout.Sections;
        for (int i = 0; i < sections.Count; i++)
        {
            var s = sections[i];
            bool group = s.Kind == SidebarSectionKind.CustomGroup;
            var kids = s.ChildList;
            into.Add(new SidebarOutlineRow(s.Id, null, s.Kind, 0, i, s.Hidden, group, kids.Count));
            for (int j = 0; j < kids.Count; j++)
            {
                var k = kids[j];
                into.Add(new SidebarOutlineRow(k.Id, s.Id, k.Kind, 1, j, k.Hidden,
                    k.Kind == SidebarSectionKind.CustomGroup, k.ChildList.Count));
            }
        }
        return into.Count;
    }

    /// <summary>Index of a section id in the flat rows (−1 when absent).</summary>
    public static int IndexOf(IReadOnlyList<SidebarOutlineRow> rows, string? sectionId)
    {
        if (string.IsNullOrEmpty(sectionId)) return -1;
        for (int i = 0; i < rows.Count; i++)
            if (string.Equals(rows[i].SectionId, sectionId, StringComparison.Ordinal)) return i;
        return -1;
    }
}

/// <summary>Flat outline indices → a <c>MoveSection</c> command (§C4.5's <c>OutlineDrag.ToMove</c>).
///
/// <para>The drop lands in the PARENT OF THE ROW NOW AT <c>to</c>, at that row's own index — so dragging a section onto
/// a group's child takes that child's slot inside the group, and dragging onto a top-level row stays top level.
/// <c>NewIndex</c> is interpreted AFTER the removal (the <c>Reorderable.OnReorder</c> / reducer contract), which is
/// exactly what "take the target's slot" means for both directions.</para>
///
/// <para>An ILLEGAL move (a group into a group, a section into its own child) is NOT filtered here: the command is built
/// and the REDUCER rejects it with <c>NestingTooDeep</c>, which is what drives the customizer's inline reject strip. One
/// authority for legality, never two.</para></summary>
public static class SidebarOutlineDrag
{
    public static MoveSection? ToMove(IReadOnlyList<SidebarOutlineRow> rows, int from, int to)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0) return null;
        if ((uint)from >= (uint)rows.Count) return null;
        if (from == to) return null;

        var moving = rows[from];

        // Past the end ⇒ append after the last row's parent chain (a drop below every row).
        if (to >= rows.Count)
        {
            var last = rows[rows.Count - 1];
            return new MoveSection(moving.SectionId, last.ParentId,
                last.ParentId is null || string.Equals(last.ParentId, moving.ParentId, StringComparison.Ordinal)
                    ? last.IndexInParent + 1
                    : last.IndexInParent + 1);
        }
        if (to < 0) return new MoveSection(moving.SectionId, null, 0);

        var target = rows[to];
        // Dropping ONTO a collapsed/expanded group's own header row from outside it: land INSIDE the group at child 0
        // (§C4.5). A group can never nest, so a moving group keeps the group's own slot instead.
        if (target.IsGroup && moving.Kind != SidebarSectionKind.CustomGroup && target.Depth == 0
            && !string.Equals(target.SectionId, moving.ParentId, StringComparison.Ordinal))
            return new MoveSection(moving.SectionId, target.SectionId, 0);

        return new MoveSection(moving.SectionId, target.ParentId, target.IndexInParent);
    }
}

// ── opaque extension config: the writer behind the schema-generated property controls ─────────────────────────────────

/// <summary>Rewrites an <c>SidebarExtensionRef.Config</c> object one field at a time — the ONE place the customizer turns
/// a generated control's value back into JSON. Every write COPIES the untouched members through verbatim, so a config
/// member this build's schema does not know (a newer extension, a hand-edited document) survives an edit (the layout's
/// round-trip-untouched policy). Never throws: a non-object config is replaced by a fresh object.</summary>
public static class SidebarConfigJson
{
    /// <summary>The config a freshly added contributed section starts from: <c>{}</c> plus every schema field that
    /// declares a <c>DefaultJson</c>, so a queue/top-tracks section is bounded before the inspector is touched.</summary>
    public static JsonElement Defaults(SidebarConfigSchema? schema)
    {
        if (schema is null || schema.Fields.Count == 0) return SidebarJson.EmptyObject;
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            var fields = schema.Fields;
            for (int i = 0; i < fields.Count; i++)
            {
                var f = fields[i];
                if (f.DefaultJson is not { Length: > 0 } raw) continue;
                if (!TryWriteRaw(w, f.Key, raw)) continue;
            }
            w.WriteEndObject();
        }
        return Parse(buffer);
    }

    public static JsonElement WithString(JsonElement config, string key, string? value)
        => Rewrite(config, key, value is null ? null : w => w.WriteStringValue(value));

    public static JsonElement WithInt(JsonElement config, string key, int value)
        => Rewrite(config, key, w => w.WriteNumberValue(value));

    public static JsonElement WithBool(JsonElement config, string key, bool value)
        => Rewrite(config, key, w => w.WriteBooleanValue(value));

    /// <summary>A string array (the <c>UriList</c> field kind). A null/empty list REMOVES the member rather than storing
    /// <c>[]</c> — the same "empty normalizes to absent" rule the query's uri sets follow.</summary>
    public static JsonElement WithStrings(JsonElement config, string key, IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0) return Rewrite(config, key, null);
        return Rewrite(config, key, w =>
        {
            w.WriteStartArray();
            for (int i = 0; i < values.Count; i++)
            {
                string one = values[i]?.Trim() ?? "";
                if (one.Length == 0) continue;
                w.WriteStringValue(one);
            }
            w.WriteEndArray();
        });
    }

    /// <summary>Copy every member except <paramref name="key"/>, then write <paramref name="write"/> under it (null
    /// <paramref name="write"/> = remove the member).</summary>
    public static JsonElement Rewrite(JsonElement config, string key, Action<Utf8JsonWriter>? write)
    {
        if (string.IsNullOrEmpty(key)) return SidebarJson.Own(config);

        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            try
            {
                if (config.ValueKind == JsonValueKind.Object)
                    foreach (var prop in config.EnumerateObject())
                    {
                        if (string.Equals(prop.Name, key, StringComparison.Ordinal)) continue;
                        prop.WriteTo(w);
                    }
            }
            catch (Exception) { /* a disposed/mangled element degrades to "just the edited member" */ }

            if (write is not null)
            {
                w.WritePropertyName(key);
                write(w);
            }
            w.WriteEndObject();
        }
        return Parse(buffer);
    }

    static bool TryWriteRaw(Utf8JsonWriter w, string key, string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            w.WritePropertyName(key);
            doc.RootElement.WriteTo(w);
            return true;
        }
        catch (Exception) { return false; }
    }

    static JsonElement Parse(ArrayBufferWriter<byte> buffer)
    {
        try
        {
            using var doc = JsonDocument.Parse(buffer.WrittenMemory);
            return doc.RootElement.Clone();
        }
        catch (Exception) { return SidebarJson.EmptyObject; }
    }
}
