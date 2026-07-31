using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core.Sidebar;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// THE SECTION PALETTE (REVISION 2's M2 amendment, superseding §C4.2's flat list): searchable, grouped Navigation /
// Library / Playback / Dynamic feeds / Layout / Actions / Extensions. The TABLE and the FILTER are pure and unit-tested
// (SidebarPalette in SidebarCustomizerLayout.cs); this file is only the surface: it resolves the loc keys, maps the
// entry's glyph NAME to a real glyph, and turns a click into the ONE dispatch the page owns.
//
// The same component is the inline 232-DIP column (Full tier) and the command-bar flyout's body (Compact/Narrow) — one
// implementation, one behaviour, `inFlyout` only trims the chrome.
sealed class SidebarCustomizerPalette : Component
{
    readonly SidebarCustomizerPage _page;
    readonly bool _inFlyout;

    /// <summary>Non-null ⇒ the palette is showing the "pick a contribution" list instead of the groups. A MODE rather
    /// than a nested flyout: the palette itself is often already inside a flyout, and a popup-on-popup for a one-shot
    /// pick reads as a bug.</summary>
    readonly Signal<bool> _pickContribution = new(false);
    readonly Signal<bool> _addExpanded = new(true);
    readonly Signal<bool> _templatesExpanded = new(false);
    bool _forward = true;

    /// <summary>Reused across renders — the filter appends into it (no per-keystroke list allocation).</summary>
    readonly List<SidebarPaletteEntry> _matches = new();

    public SidebarCustomizerPalette(SidebarCustomizerPage page, bool inFlyout)
    {
        _page = page; _inFlyout = inFlyout;
    }

    public override Element Render()
    {
        string query = _page.PaletteQuery.Value;
        bool picking = _pickContribution.Value;
        _ = _page.Prefs?.LayoutVersion.Value ?? 0;

        var rows = new List<Element>(12);
        if (picking) AppendContributions(rows);
        else
        {
            SidebarPalette.Filter(query, static e => Loc.Get(e.NameLocKey), static e => Loc.Get(e.DescriptionLocKey),
                _matches);
            AppendGroups(rows, query);
        }

        var page = new BoxEl
        {
            Key = picking ? "palette-page:contributions" : "palette-page:sections",
            Animate = _forward ? MotionRecipes.PageSlideForward : MotionRecipes.PageSlideBack,
            Direction = 1, Grow = 1f, Shrink = 1f, MinHeight = 0f, Gap = Spacing.XS,
            Children =
            [
                picking ? BackRow() : SearchBox(),
                ScrollView(new BoxEl
                {
                    Direction = 1, Gap = 2f,
                    Padding = new Edges4(0f, Spacing.XS, 0f, Spacing.XS),
                    Children = [.. rows],
                }) with
                {
                    Grow = 1f, Shrink = 1f, MinHeight = 0f, AutoEdgeFade = true,
                    ScrollKey = picking ? "customizer.palette.contributions" : "customizer.palette.sections",
                },
            ],
        };

        Element addContent = new BoxEl
        {
            Direction = 1, Height = FlyoutBodyHeight, MinHeight = 0f, ClipToBounds = true,
            Children = [page],
        };
        Element templatesContent = ScrollView(
            Embed.Comp(() => new SidebarTemplateList(_page, showHeading: false))) with
        {
            Height = FlyoutBodyHeight, MinHeight = 0f, Shrink = 0f, AutoEdgeFade = true,
            ScrollKey = "customizer.palette.templates",
        };
        var parts = AccordionParts();
        Element add = Embed.Comp(
            new Expander.ExpanderSlots(AddHeaderContent(), addContent, parts),
            () => new Expander { IsExpanded = _addExpanded, OnChange = AddChanged }) with
        { Key = "palette-accordion:add" };
        Element templates = Embed.Comp(
            new Expander.ExpanderSlots(TemplatesHeaderContent(), templatesContent, parts),
            () => new Expander { IsExpanded = _templatesExpanded, OnChange = TemplatesChanged }) with
        { Key = "palette-accordion:templates" };

        return new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, MinHeight = 0f, Gap = Spacing.S,
            Padding = _inFlyout ? Edges4.All(Spacing.S) : default,
            Children = [add, templates],
        };
    }


    /// <summary>The flyout's scroll-body height. Budgeted against <c>SidebarCustomizerPage.PaletteFlyout</c>'s 520-DIP
    /// MaxHeight — head (≈15 caption + 4 gap + 30 search + 8 padding ≈ 60) + 4 gap + this ≈ 424 — with the slack left
    /// deliberately: the flyout opens <c>ConstrainToRootBounds</c>, so on a short window the overlay clamps its own height
    /// and a body budgeted right up to 520 would be the part that gets clipped.</summary>
    const float FlyoutBodyHeight = 360f;

    static TemplateParts AccordionParts() => new()
    {
        [Expander.PartHeader] = static h => h with
        {
            MinHeight = 44f,
            Padding = new Edges4(Spacing.M, 0f, 0f, 0f),
            Fill = Tok.FillCardSecondary,
            HoverFill = Tok.FillSubtleSecondary,
            BorderColor = Tok.StrokeCardDefault,
            Corners = Radii.ControlAll,
        },
        [Expander.PartChevron] = static c => c with
        {
            Width = 28f, Height = 28f,
            Margin = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
        },
        [Expander.PartContent] = static c => c with
        {
            Padding = Edges4.All(Spacing.S), MinHeight = 0f,
            Fill = Tok.FillCardDefault, BorderColor = Tok.StrokeCardDefault,
        },
    };

    void AddChanged(bool open)
    {
        if (open) _templatesExpanded.Value = false;
        else if (!_templatesExpanded.Peek()) _templatesExpanded.Value = true;
    }

    void TemplatesChanged(bool open)
    {
        if (open) _addExpanded.Value = false;
        else if (!_addExpanded.Peek()) _addExpanded.Value = true;
    }

    Element AddHeaderContent()
    {
        int used = _page.Prefs?.Layout.SectionCount ?? 0;
        bool full = used >= SidebarLayoutReducer.MaxSections;
        return new BoxEl
        {
            Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f, Gap = Spacing.S,
            AlignItems = FlexAlign.Center,
            Children =
            [
                new TextEl(Loc.Get(CzLoc.AddSection))
                {
                    Size = 13f, Weight = 600, Color = Tok.TextPrimary,
                    Grow = 1f, Shrink = 1f, MinWidth = 0f, MaxLines = 1,
                    Trim = TextTrim.CharacterEllipsis,
                },
                new TextEl(Loc.Format(CzLoc.SectionCount,
                    ("used", used), ("max", SidebarLayoutReducer.MaxSections)))
                {
                    Size = 11f, Weight = 600, Shrink = 0f, MaxLines = 1,
                    Color = full ? Tok.SystemFillCritical : Tok.TextTertiary,
                },
            ],
        };
    }

    static Element TemplatesHeaderContent() => new TextEl(Loc.Get(CzLoc.Templates))
    {
        Size = 13f, Weight = 600, Color = Tok.TextPrimary,
        MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
    };

    // â”€â”€ rows â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    void AppendGroups(List<Element> into, string query)
    {
        var groups = SidebarPalette.Groups;
        for (int g = 0; g < groups.Length; g++)
        {
            var group = groups[g];
            int before = into.Count;
            for (int i = 0; i < _matches.Count; i++)
            {
                var e = _matches[i];
                if (e.Group != group) continue;
                if (into.Count == before) into.Add(GroupHeader(group));
                into.Add(EntryRow(e));
            }
        }
        // Only a SEARCH can empty the palette (the table is static), so the empty state names the query — the same
        // "No results for X" line the library list uses.
        if (into.Count == 0 && query.Length > 0)
            into.Add(new TextEl(Loc.Format("sidebar.v3.empty.search", ("query", query)))
            {
                Size = 12f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MaxLines = 2,
                Margin = new Edges4(Spacing.XS, Spacing.S, Spacing.XS, 0f),
            });
    }

    Element EntryRow(SidebarPaletteEntry entry)
    {
        string label = Loc.Get(entry.NameLocKey);
        string sub = Loc.Get(entry.DescriptionLocKey);
        var page = _page;
        var add = entry.Add;
        var kind = entry.Kind;
        string? contribution = entry.ContributionId;
        var picking = _pickContribution;

        void Click()
        {
            switch (add)
            {
                case SidebarPaletteAdd.Contribution when contribution is { Length: > 0 }:
                    page.AddContribution(contribution);
                    break;
                case SidebarPaletteAdd.RecentlyPlayed:
                    page.AddRecentlyPlayed();
                    break;
                case SidebarPaletteAdd.ActionShortcut:
                    page.AddActionShortcut();
                    break;
                case SidebarPaletteAdd.LikedSongsShortcut:
                    page.AddLikedSongsShortcut();
                    break;
                case SidebarPaletteAdd.AnyContribution:
                    _forward = true;
                    picking.Value = true;
                    break;
                default:
                    page.AddSectionOfKind(kind);
                    break;
            }
        }

        return Row(CzGlyphs.ForName(entry.IconName), label, sub, Click);
    }

    void AppendContributions(List<Element> into)
    {
        var registry = _page.Registry;
        var sources = registry?.Sources;
        if (sources is null || sources.Count == 0)
        {
            into.Add(new TextEl(Loc.Get(CzLoc.ExtensionManage))
            {
                Size = 12f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MaxLines = 2,
            });
            return;
        }
        var page = _page;
        var picking = _pickContribution;
        for (int i = 0; i < sources.Count; i++)
        {
            string id = sources[i].Id;
            // No manifest NAME exists for a contribution in M2 (it arrives with the external SDK in M3/M4), so the row
            // shows the stable id rather than inventing a label the extension never declared.
            into.Add(Row(Icons.Code, SidebarContributions.ContributionOf(id), id, () =>
            {
                picking.Value = false;
                page.AddContribution(id);
            }));
        }
    }

    Element BackRow() => new BoxEl
    {
        Direction = 0, Height = 28f, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.XS,
        Corners = Radii.ControlAll, Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button,
        OnClick = () => { _forward = false; _pickContribution.Value = false; },
        Children =
        [
            Icon(Icons.ChevronLeft, 12f, Tok.TextSecondary),
            new TextEl(Loc.Get(CzLoc.AddSection)) { Size = 12f, Weight = 600, Color = Tok.TextSecondary, MaxLines = 1 },
        ],
    }.Interactive(Interaction.Subtle);

    static Element Row(string glyph, string label, string? sub, Action onClick)
    {
        var lines = new List<Element>(2)
        {
            new TextEl(label) { Size = 13f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
        };
        if (sub is { Length: > 0 })
            lines.Add(new TextEl(sub)
            {
                Size = 11f, Color = Tok.TextTertiary, MaxLines = 2, Wrap = TextWrap.Wrap,
            });

        return new BoxEl
        {
            Direction = 0, Shrink = 0f, Gap = Spacing.S, AlignItems = FlexAlign.Center,
            Padding = new Edges4(Spacing.XS, 6f, Spacing.XS, 6f), Corners = Radii.ControlAll,
            Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button, OnClick = onClick,
            Children =
            [
                new BoxEl
                {
                    Width = 24f, Height = 24f, Shrink = 0f, Corners = Radii.ControlAll,
                    Fill = Tok.FillSubtleSecondary,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
                    Children = [Icon(glyph, 13f, Tok.TextSecondary)],
                },
                new BoxEl { Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, Children = [.. lines] },
                AddChip(),
            ],
        }.Interactive(Interaction.ListRow);
    }

    /// <summary>The trailing "+" (R3.2 item 3). It is an AFFORDANCE, not a second button: the whole row already adds, so
    /// the chip is hit-test-transparent and merely says out loud what a click will do. It brightens on ROW hover — the
    /// engine cascades a container's hover to a descendant only for an opacity/scale REVEAL (<c>AnimScheduler.Hover</c>),
    /// which is exactly (and only) what this uses; a HoverFill here would never be reached.</summary>
    static Element AddChip() => new BoxEl
    {
        Width = 24f, Height = 24f, Shrink = 0f, Corners = Radii.FullAll,
        Fill = Tok.FillSubtleSecondary,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
        Opacity = 0.45f, HoverOpacity = 1f, HoverDurationMs = Motion.ControlFaster,
        Children = [Icon(Icons.Add, 12f, Tok.TextSecondary)],
    };
    static Element GroupHeader(SidebarPaletteGroup group) => Header(Loc.Get(SidebarPalette.GroupLocKey(group)));

    static Element Header(string text) => new TextEl(text.ToUpperInvariant())
    {
        Size = 11f, Weight = 600, Color = Tok.TextTertiary, MaxLines = 1, CharSpacing = 40f,
        Trim = TextTrim.CharacterEllipsis,
        Margin = new Edges4(Spacing.XS, Spacing.S, Spacing.XS, 2f),
    };

    Element SearchBox()
    {
        var q = _page.PaletteQuery;
        return TextBox.Create(q, null, new TextBox.TextBoxOptions
        {
            Placeholder = Loc.Get(CzLoc.PaletteSearch),
            // The inline palette now lives INSIDE the region card (R3.2 item 6), which insets Spacing.S a side — a
            // full-232 box would push the card's own padding out.
            Width = _inFlyout ? 284f : SidebarCustomizerLayout.PaletteWidth - Spacing.S * 2f,
            Height = 30f,
        });
    }

}

/// <summary>The customizer's glyph resolution, in ONE place: palette entry NAMES (the pure table is engine-free, so it
/// carries names) and section KINDS (the outline, the inspector header and the property panel must agree on a kind's
/// mark). An unknown value degrades to the neutral section mark, never a blank box.</summary>
static class CzGlyphs
{
    public static string ForName(string? name) => name switch
    {
        "Pin" => Icons.Pin,
        "Heart" => Icons.Heart,
        "Link" => Icons.Link,
        "Folder" => Icons.Folder,
        "Filter" => Icons.Filter,
        "FavoriteStar" => Icons.FavoriteStar,
        "Headphones" => Icons.Headphones,
        "Queue" => Icons.Queue,
        "Play" => Icons.Play,
        "Clock" => Icons.Clock,
        "Contact" => Icons.Contact,
        "Album" => Icons.Album,
        "Calendar" => Icons.Calendar,
        "Grid" => Icons.Grid,
        "Font" => Icons.Font,
        "Remove" => Icons.Remove,
        "RefineSparkle" => Icons.RefineSparkle,
        "Code" => Icons.Code,
        _ => Icons.MusicNote,
    };

    public static string ForKind(SidebarSectionKind kind) => kind switch
    {
        SidebarSectionKind.Pinned => Icons.Pin,
        SidebarSectionKind.JumpBackIn => Icons.Clock,
        SidebarSectionKind.CollectionShortcuts => Icons.Heart,
        SidebarSectionKind.PlaylistTree => Icons.Folder,
        SidebarSectionKind.EntityList => Icons.Filter,
        SidebarSectionKind.StaticLinks => Icons.Link,
        SidebarSectionKind.CustomGroup => Icons.Grid,
        SidebarSectionKind.Header => Icons.Font,
        SidebarSectionKind.Divider => Icons.Remove,
        SidebarSectionKind.EntityEmbed => Icons.FavoriteStar,
        SidebarSectionKind.NewReleases => Icons.Album,
        SidebarSectionKind.Concerts => Icons.Calendar,
        SidebarSectionKind.Extension => Icons.Code,
        _ => Icons.MusicNote,
    };

    /// <summary>A section's display TITLE: the user rename wins, else the localized kind/template key, else the kind's
    /// palette name — so an outline row is never blank (a Divider legitimately has no title of its own).</summary>
    public static string TitleOf(SidebarSectionSpec section)
    {
        if (section.Title is { Length: > 0 } t) return t;
        if (section.TitleLocKey is { Length: > 0 } k) return Loc.Get(k);
        if (SidebarSectionKinds.PaletteNameLocKey(section.Kind) is { Length: > 0 } p) return Loc.Get(p);
        return "";
    }
}

/// <summary>The five template cards (§C4.4's column 1 / the Compact-tier "Templates" flyout). Radio semantics: the
/// document's own <c>TemplateId</c> is the checked one; picking another routes through the page's confirmation.</summary>
sealed class SidebarTemplateList : Component
{
    readonly SidebarCustomizerPage _page;
    readonly bool _showHeading;

    public SidebarTemplateList(SidebarCustomizerPage page, bool showHeading = true)
    {
        _page = page; _showHeading = showHeading;
    }

    public override Element Render()
    {
        var prefs = _page.Prefs;
        _ = prefs?.LayoutVersion.Value ?? 0;      // the active template changes with the document
        string active = prefs?.Layout.TemplateId ?? SidebarTemplates.Curated;

        var ids = SidebarTemplates.All;
        var kids = new List<Element>(ids.Length + (_showHeading ? 1 : 0));
        if (_showHeading)
            kids.Add(new TextEl(Loc.Get(CzLoc.Templates).ToUpperInvariant())
            {
                Size = 11f, Weight = 600, Color = Tok.TextTertiary, MaxLines = 1, CharSpacing = 40f,
                Trim = TextTrim.CharacterEllipsis,
                Margin = new Edges4(Spacing.XS, Spacing.S, Spacing.XS, 2f),
            });
        for (int i = 0; i < ids.Length; i++)
        {
            string id = ids[i];
            bool on = string.Equals(id, active, StringComparison.Ordinal);
            kids.Add(Card(id, on, () => _page.ApplyTemplate(id)));
        }

        return new BoxEl
        {
            Direction = 1, Shrink = 0f, Gap = 2f,
            Padding = new Edges4(0f, Spacing.S, 0f, 0f),
            Children = [.. kids],
        };
    }

    static Element Card(string templateId, bool active, Action onClick) => new BoxEl
    {
        Direction = 0, Shrink = 0f, Gap = Spacing.S, AlignItems = FlexAlign.Center,
        Padding = new Edges4(Spacing.S, 6f, Spacing.S, 6f), Corners = Radii.ControlAll,
        // Selection-aware fills set EXPLICITLY: `.Interactive(...)` overwrites all three from its recipe, which would
        // erase the active card's plate — so this row styles its own ramp.
        Fill = active ? Tok.FillSubtleSecondary : ColorF.Transparent,
        HoverFill = active ? Tok.FillSubtleTertiary : Tok.FillSubtleSecondary,
        PressedFill = active ? Tok.FillSubtleSecondary : Tok.FillSubtleTertiary,
        BorderWidth = active ? 1f : 0f, BorderColor = active ? Tok.AccentDefault : ColorF.Transparent,
        Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.RadioButton, OnClick = onClick,
        Children =
        [
            Icon(active ? Icons.RadioBullet : Icons.Grid, 14f, active ? Tok.AccentDefault : Tok.TextSecondary),
            new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f,
                Children =
                [
                    new TextEl(Loc.Get(SidebarTemplates.NameLocKey(templateId)))
                    {
                        Size = 13f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1,
                        Trim = TextTrim.CharacterEllipsis,
                    },
                    new TextEl(Loc.Get(SidebarTemplates.DescriptionLocKey(templateId)))
                    {
                        Size = 11f, Color = Tok.TextTertiary, MaxLines = 2, Wrap = TextWrap.Wrap,
                    },
                ],
            },
            // The ACTIVE checkmark (R3.2 item 3). The card already carries an accent plate + border, but a plate reads as
            // "hovered" to a user who has not moved the pointer — the tick is the unambiguous "this one is applied".
            active
                ? (Element)(Icon(Icons.Accept, 14f, Tok.AccentTextPrimary) with { Margin = new Edges4(0f, 0f, 2f, 0f) })
                : new BoxEl { Width = 16f, Shrink = 0f },
        ],
    };
}
