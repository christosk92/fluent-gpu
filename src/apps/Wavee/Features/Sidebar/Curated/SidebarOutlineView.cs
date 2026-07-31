using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core.Sidebar;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// THE OUTLINE (§C4.5) — the customizer's middle region: the document's sections as a flat, keyed, NON-virtualized list
// (the reducer caps the document at 40 sections, so a recycling list would buy nothing and cost the live drag).
//
// REORDER is `Reorderable("sidebar-section")` with **LiveProject = false**: the resting order holds during the drag and
// the built-in insertion line marks the pending slot. (The engine's live projection needs key-preserving children AND a
// stable pitch; this list mixes 44-DIP top-level rows with 36-DIP group children, so the insertion line is the honest
// cue and the commit still lands exactly where it is shown.) The flat-index → (parent, index) translation is the pure,
// unit-tested SidebarOutlineDrag.ToMove; LEGALITY is the reducer's alone — an illegal drop is dispatched, rejected with
// NestingTooDeep, and surfaced by the page's inline strip.
//
// KEYBOARD: the Reorderable item wrapper owns the lift keys (Space lifts, arrows place, Space drops, Escape cancels) and
// the single focus stop per row. This file CHAINS that wrapper's handlers instead of replacing them, adding
// selection-follows-focus plus F2 (rename) and Delete (remove) — so there is exactly one tab stop per row and the lift
// still works.
sealed class SidebarOutlineView : Component
{
    readonly SidebarCustomizerPage _page;
    readonly List<SidebarOutlineRow> _rows = new();

    /// <summary>The section whose title is being edited inline (null = nobody). Commit is Enter, cancel is Escape.</summary>
    readonly Signal<string?> _editing = new(null);
    readonly Signal<string> _editText = new("");

    /// <summary>Bumped by the Reorderable whenever the projection/insertion state moved — its <c>RequestRender</c>.</summary>
    readonly Signal<int> _dragEpoch = new(0);

    const string SectionDragKind = "wavee.customizer.section";

    static readonly LayoutTransition RowPlacement = new(
        TransitionChannels.Position, MotionTok.ItemPlacement.ToDynamics());

    public SidebarOutlineView(SidebarCustomizerPage page) => _page = page;

    public override Element Render()
    {
        var prefs = _page.Prefs;
        _ = prefs?.LayoutVersion.Value ?? 0;     // THE dep: the outline is a projection of the document
        bool curatedActive = prefs?.Design.Value == SidebarDesign.Curated;
        _ = _dragEpoch.Value;                    // re-render while a lift moves the pending slot
        string? selected = _page.Selected.Value;
        string? editing = _editing.Value;

        SidebarOutlineRows.Build(prefs?.Layout, _rows);

        // Its OWN drag kind: an outline row is a document SECTION, and it must never be accepted by (or accept) the
        // sidebar's entity lists — a section is not a pinnable entity.
        var ro = UseMemo(static () => new Reorderable(SectionDragKind)
        {
            // See the file header: a mixed-height, keyed outline over a pending-slot cue, not a live projection.
            LiveProject = false,
            ShowInsertionLine = true,
        }, DepKey.Empty);

        ConfigureReorder(ro);

        Element body = _rows.Count == 0
            ? EmptyBody()
            : ScrollView(new BoxEl
            {
                Direction = 1,
                Padding = new Edges4(Spacing.S, Spacing.S, Spacing.S, Spacing.M),
                Children =
                [
                    ro.List(new BoxEl
                    {
                        Direction = 1, Gap = RowGap,
                        Children = [.. Rows(ro, selected, editing)],
                    }),
                    AddTail(),
                ],
            }) with
            {
                Grow = 1f, Shrink = 1f, MinHeight = 0f, AutoEdgeFade = true, ScrollKey = "customizer.outline",
            };

        return new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, MinHeight = 0f, MinWidth = 0f, ClipToBounds = true,
            // The region CARD is the page's job now (SidebarCustomizerPage.Body wraps every region in one plate), so this
            // view draws no surface of its own — two nested cards read as a seam, not as depth.
            // Delete/F2 act on the selected row. The row wrapper's own handler runs FIRST (the lift keys) and this only
            // sees what it left unhandled — keys bubble focused-node → ancestors until Handled.
            OnKeyDown = OnListKey,
            Children =
            [
                Embed.Comp(() => new SidebarTopBarCard(_page)) with { Key = "topbar-card" },
                Divider(),
                Head(curatedActive),
                Divider(),
                body,
            ],
        };
    }

    // ── chrome ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Gap between section cards. Cards need air the old 2-DIP flat rows did not; it is ALSO
    /// <c>Reorderable.Spacing</c>, so the drop pitch stays exactly what the eye measures.</summary>
    const float RowGap = Spacing.XS;

    Element Head(bool curatedActive)
    {
        int visible = VisibleCount();
        return new BoxEl
        {
            Direction = 1, Shrink = 0f, Padding = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.S), Gap = 2f,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Shrink = 0f, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        new TextEl(Loc.Get(CzLoc.CuratedLayout))
                        {
                            Size = 13f, Weight = 600, Color = Tok.TextPrimary, Grow = 1f, Shrink = 1f, MinWidth = 0f,
                            MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        },
                        // "n visible" counts what the LIVE pane will actually draw — authored sections minus the hidden
                        // ones — which is the number the user is really editing toward.
                        new TextEl(Loc.Format(CzLoc.VisibleCount, ("count", visible)))
                        {
                            Size = 11f, Color = Tok.TextTertiary, Shrink = 0f, MaxLines = 1,
                        },
                    ],
                },
                // The lift hint is PERSISTENT text, not an announcement: the engine has no automation-name/live-region
                // channel today, so visible text is the accessible surface (§C4.5's honest note).
                new TextEl(Loc.Get(curatedActive ? CzLoc.LiftHint : CzLoc.CuratedInactive))
                {
                    Size = 11f, Color = Tok.TextTertiary, MaxLines = 2, Wrap = TextWrap.Wrap,
                },
            ],
        };
    }

    int VisibleCount()
    {
        int n = 0;
        for (int i = 0; i < _rows.Count; i++)
            if (!_rows[i].Hidden) n++;
        return n;
    }

    Element EmptyBody() => new BoxEl
    {
        Grow = 1f, Shrink = 1f, Direction = 1, MinHeight = 0f,
        Children =
        [
            EmptyState.Build(
                Loc.Get(CzLoc.Empty),
                Loc.Get(CzLoc.EmptySub),
                Icons.SplitView,
                Loc.Get(CzLoc.StartFromTemplate),
                // The palette is the "add a section" path, but at the narrow tiers it lives behind a command-bar flyout
                // this card has no anchor for — so the empty state's one-click recovery is the template instead.
                () => _page.ApplyTemplate(SidebarTemplates.Curated)),
        ],
    };

    /// <summary>The realized node of the tail slot, so the palette flyout can anchor to the thing that was CLICKED
    /// (round-2 defect 5) instead of the header's command anchor across the page.</summary>
    NodeHandle _addTailAnchor;

    Element AddTail() => new BoxEl
    {
        Direction = 0,
        Height = 44f,
        Shrink = 0f,
        Gap = Spacing.S,
        AlignItems = FlexAlign.Center,
        Justify = FlexJustify.Center,
        // An EMPTY SLOT in the card rhythm, not a row: the same radius and a hairline, no plate at rest, and an
        // accent-tinted wash on hover so it reads as "a card goes here" (round-2 defect 9).
        Margin = new Edges4(0f, RowGap, 0f, 0f),
        Padding = new Edges4(Spacing.M, 0f, Spacing.M, 0f),
        Corners = Radii.ControlAll,
        BorderWidth = 1f,
        BorderColor = Tok.StrokeDividerDefault,
        Focusable = true,
        Role = AutomationRole.Button,
        Cursor = CursorId.Hand,
        OnRealized = h => _addTailAnchor = h,
        OnClick = () => _page.OpenPaletteAt(_addTailAnchor),
        Children =
        [
            Icon(Icons.Add, 14f, Tok.TextSecondary),
            new TextEl(Loc.Get(CzLoc.AddSection))
            {
                Size = 12f, Weight = 600, Color = Tok.TextSecondary, MaxLines = 1,
                Trim = TextTrim.CharacterEllipsis,
            },
        ],
    }.Interactive(Interaction.AccentGhost);

    // ── rows ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    List<Element> Rows(Reorderable ro, string? selected, string? editing)
    {
        var list = new List<Element>(_rows.Count);
        var prefs = _page.Prefs;
        var layout = prefs?.Layout;
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            var spec = layout?.Find(row.SectionId);
            if (spec is null) continue;
            bool isSelected = string.Equals(selected, row.SectionId, StringComparison.Ordinal);
            bool isEditing = string.Equals(editing, row.SectionId, StringComparison.Ordinal);
            Element content = RowContent(spec, row, isSelected, isEditing, ro);

            // Chain the Reorderable's own wrapper: keep its drag/lift/focus wiring, add selection-follows-focus.
            var wrapped = ro.Item(i, content, key: row.SectionId, transition: RowPlacement);
            if (wrapped is BoxEl box)
            {
                var baseFocus = box.OnFocusChanged;
                string id = row.SectionId;
                wrapped = box with
                {
                    OnFocusChanged = got =>
                    {
                        baseFocus?.Invoke(got);
                        if (got) _page.Select(id);
                    },
                };
            }
            list.Add(wrapped);
        }
        return list;
    }

    Element RowContent(SidebarSectionSpec spec, in SidebarOutlineRow row, bool selected, bool editing, Reorderable ro)
    {
        if (spec.Kind == SidebarSectionKind.Divider)
            return DividerContent(spec, row, selected);

        string id = spec.Id;
        bool top = row.Depth == 0;
        var kids = new List<Element>(7)
        {
            SelectionIndicator(selected),
            HoverChrome(Icon(Icons.GripperBar, 12f, Tok.TextTertiary), selected, 16f),
            KindChip(spec.Kind, selected),
        };

        if (editing)
        {
            kids.Add(new BoxEl
            {
                Grow = 1f, Shrink = 1f, MinWidth = 0f,
                Children =
                [
                    TextBox.Create(_editText, null, new TextBox.TextBoxOptions
                    {
                        Width = 180f, Height = 26f, MaxLength = 60,
                        Placeholder = CzGlyphs.TitleOf(spec),
                        OnCommit = text => CommitRename(id, text),
                        OnCancel = CancelRename,
                    }),
                ],
            });
        }
        else
        {
            var lines = new List<Element>(2)
            {
                new TextEl(CzGlyphs.TitleOf(spec))
                {
                    Size = 13f, Weight = 600,
                    Color = spec.Hidden ? Tok.TextTertiary : Tok.TextPrimary,
                    MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
            };
            // The KIND subtitle: a section card that only shows a renamed title cannot say WHAT it is, and the kind is the
            // one thing the user cannot change after adding. Depth-1 children stay one line — a 44-DIP row has no room,
            // and a group's children are read in the group's context anyway.
            if (top && KindSub(spec) is { Length: > 0 } sub)
                lines.Add(new TextEl(sub)
                {
                    Size = 11f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                });

            kids.Add(new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, Justify = FlexJustify.Center,
                Children = [.. lines],
            });
        }

        // A lifted row NAMES its live position in place of nothing (the Classic pin precedent): the engine has no
        // announcement channel, so the visible caption is the only a11y surface for a keyboard reorder.
        if (ro.IsKeyboardLifted && ro.LiftedIndex == SidebarOutlineRows.IndexOf(_rows, id))
            kids.Add(Tag(Loc.Format(CzLoc.Position, ("index", ro.TargetIndex + 1), ("count", _rows.Count))));

        kids.Add(Trailing(spec, row, selected));

        return new BoxEl
        {
            Direction = 0, Height = row.Height, Gap = Spacing.S, AlignItems = FlexAlign.Center,
            // FILL THE COLUMN (round-3 defect 1). `Reorderable.Item` wraps its content in a BoxEl that leaves `Direction`
            // at its default 0 = ROW (Reorderable.cs:221-237), so the card sits on that wrapper's MAIN axis and — with no
            // Grow — measured to its own CONTENT width. That is why "Pinned" was wide, "Your Library" narrower and
            // "Playlists" narrower still, while the AddTail (a direct child of the Direction=1 list body, whose CROSS axis
            // stretches by default) was the one full-width row. MinWidth 0 keeps a long title eliding rather than pushing
            // the card past the column.
            Grow = 1f, Shrink = 1f, MinWidth = 0f,
            // 2 DIP so the accent bar sits just INSIDE the card's hairline rather than on top of it.
            Padding = new Edges4(2f + row.Depth * Spacing.L, 0f, Spacing.XS, 0f),
            Corners = Radii.ControlAll,
            // The section CARD (R3.2 item 2): an opaque card plate at rest, and the SELECTED card swaps to the subtle
            // plate plus its 3-DIP accent bar — the same selection-aware ramp the sidebar rows use, so the app reads as
            // one system (§C4.5).
            Fill = selected ? Tok.FillSubtleSecondary : Tok.FillCardDefault,
            HoverFill = selected ? Tok.FillSubtleTertiary : Tok.FillCardSecondary,
            PressedFill = selected ? Tok.FillSubtleSecondary : Tok.FillSubtleTertiary,
            BorderWidth = 1f,
            BorderColor = selected ? Tok.AccentSubtle : Tok.StrokeCardDefault,
            BrushTransitionMs = Motion.ControlFaster,
            Opacity = spec.Hidden ? 0.6f : 1f,
            Cursor = CursorId.Hand, Role = AutomationRole.NavigationItem,
            OnPointerReleased = args =>
            {
                _page.Select(id);
                if (args.ClickCount >= 2) BeginRename(id);
            },
            Children = [.. kids],
        };
    }

    /// <summary>The kind mark as a 24-DIP rounded chip — the card's anchor, and the reason a renamed section is still
    /// identifiable at a glance.</summary>
    static Element KindChip(SidebarSectionKind kind, bool selected) => new BoxEl
    {
        Width = 24f, Height = 24f, Shrink = 0f, Corners = Radii.ControlAll,
        Fill = selected ? Tok.AccentSubtle : Tok.FillSubtleSecondary,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
        Children = [Icon(CzGlyphs.ForKind(kind), 13f, selected ? Tok.AccentTextPrimary : Tok.TextSecondary)],
    };

    /// <summary>The kind's one-line description (the catalog's <c>sidebar.section.*Sub</c> family, owned by
    /// <c>SidebarSectionKinds</c> so a kind's words are never spelled twice). Null for a kind with no palette entry.</summary>
    static string? KindSub(SidebarSectionSpec spec)
    {
        string? key = spec.IsExtension
            ? "sidebar.section.extensionSub"
            : SidebarSectionKinds.PaletteDescriptionLocKey(spec.Kind);
        return key is { Length: > 0 } ? Loc.Get(key) : null;
    }

    /// <summary>The card's trailing affordances (R3.2 item 2): visibility · move up · move down · remove, revealed on
    /// hover (persistent on the selected card), plus the "…" menu that has always been there.
    /// <para>The affordances are POINTER-ONLY (<c>Focusable = false</c>): the <c>Reorderable</c> item wrapper owns the
    /// row's focus stop and its lift keys, and four more tab stops per row would bury the keyboard reorder. Every one of
    /// these commands is also in the "…" menu, which IS focusable — so nothing here is keyboard-unreachable.</para></summary>
    Element Trailing(SidebarSectionSpec spec, in SidebarOutlineRow row, bool selected)
    {
        string id = spec.Id;
        // The MOVE bounds come from the live document (the same Locate the row menu uses), never from the flattened row:
        // a flat index is not a sibling index. With no store the arrows fall back to "at the edge" and disable.
        var layout = _page.Prefs?.Layout;
        int within = row.IndexInParent;
        int siblings = within + 1;
        string? parentId = row.ParentId;
        if (layout is not null)
        {
            var loc = layout.Locate(id);
            within = loc.Index;
            parentId = loc.Parent?.Id;
            siblings = loc.Parent is null ? layout.Sections.Count : loc.Parent.ChildList.Count;
        }

        var kids = new List<Element>(5)
        {
            // ONE eye glyph for both states: the engine's whitelisted icon font carries no struck-through eye, so the
            // STATE is carried by the tint (and by the row's own dimming + "Hidden" tag), never by a second glyph.
            Affordance(Icons.RevealPassword, Loc.Get(spec.Hidden ? CzLoc.Show : CzLoc.Hide),
                () => _page.Dispatch(new SetSectionHidden(id, !spec.Hidden)),
                spec.Hidden ? Tok.AccentTextPrimary : Tok.TextSecondary),
            Affordance(Icons.ChevronUp, Loc.Get(CzLoc.MoveUp),
                within > 0 ? () => _page.Dispatch(new MoveSection(id, parentId, within - 1)) : null),
            Affordance(Icons.ChevronDown, Loc.Get(CzLoc.MoveDown),
                within < siblings - 1 ? () => _page.Dispatch(new MoveSection(id, parentId, within + 1)) : null),
            Affordance(Icons.ChromeClose, Loc.Get(CzLoc.RemoveSection), () => Remove(id), Tok.TextSecondary),
            Embed.Comp(() => new CzMenuButton(Icons.More, () => RowMenu(id))) with { Key = "menu:" + id },
        };

        return new BoxEl
        {
            Direction = 0, Shrink = 0f, Gap = 0f, AlignItems = FlexAlign.Center,
            Opacity = selected ? 1f : 0f,
            HoverOpacity = 1f,
            HoverDurationMs = Motion.ControlFaster,
            Children = [.. kids],
        };
    }

    /// <summary>One 24-DIP pointer affordance. A null <paramref name="onClick"/> renders the glyph disabled (the edge of a
    /// list is a FACT, not a hidden control — hiding the arrow would make the cluster jump width per row).</summary>
    static Element Affordance(string glyph, string tip, Action? onClick, ColorF? tint = null)
    {
        var box = new BoxEl
        {
            Width = 24f, Height = 24f, Shrink = 0f, Corners = Radii.ControlAll,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Cursor = onClick is null ? CursorId.Arrow : CursorId.Hand,
            OnClick = onClick,
            Children = [Icon(glyph, 12f, onClick is null ? Tok.TextDisabled : tint ?? Tok.TextSecondary)],
        };
        return onClick is null ? box : ToolTip.Wrap(box.Interactive(Interaction.Subtle), tip);
    }

    Element DividerContent(SidebarSectionSpec spec, in SidebarOutlineRow row, bool selected)
    {
        string id = spec.Id;
        return new BoxEl
        {
            Direction = 0,
            Height = row.Height,
            Gap = Spacing.S,
            AlignItems = FlexAlign.Center,
            // Fills the column for the same reason as a section card — see RowContent (round-3 defect 1).
            Grow = 1f, Shrink = 1f, MinWidth = 0f,
            Padding = new Edges4(Spacing.S + row.Depth * Spacing.L, 0f, Spacing.XS, 0f),
            Corners = Radii.ControlAll,
            // A divider is CHROME, not content: it keeps the flat, plateless treatment so the section cards beside it read
            // as the things that actually hold content.
            Fill = selected ? Tok.FillSubtleSecondary : ColorF.Transparent,
            HoverFill = selected ? Tok.FillSubtleTertiary : Tok.FillSubtleSecondary,
            PressedFill = selected ? Tok.FillSubtleSecondary : Tok.FillSubtleTertiary,
            Opacity = spec.Hidden ? 0.6f : 1f,
            Cursor = CursorId.Hand,
            Role = AutomationRole.NavigationItem,
            OnPointerReleased = _ => _page.Select(id),
            Children =
            [
                SelectionIndicator(selected),
                HoverChrome(Icon(Icons.GripperBar, 12f, Tok.TextTertiary), selected, 16f),
                new BoxEl { Height = 1f, Grow = 1f, Fill = Tok.StrokeDividerDefault },
                new TextEl(CzGlyphs.TitleOf(spec))
                {
                    Size = 11f, Weight = 600, Color = Tok.TextTertiary, MaxLines = 1,
                },
                new BoxEl { Height = 1f, Grow = 1f, Fill = Tok.StrokeDividerDefault },
                Trailing(spec, row, selected),
            ],
        };
    }

    /// <summary>The 3-DIP accent left bar. Taller than the sidebar's own <c>SidebarSelectionPill.PillH</c> because it now
    /// marks a 52-DIP CARD, not a 32-DIP row — the width (and therefore the app's selection language) is unchanged.</summary>
    static Element SelectionIndicator(bool selected, float height = SidebarSelectionPill.PillH + 8f) => new BoxEl
    {
        Width = 3f,
        Height = height,
        Shrink = 0f,
        Corners = CornerRadius4.All(1.5f),
        Fill = Tok.AccentDefault,
        Opacity = selected ? 1f : 0f,
        HitTestVisible = false,
    };

    static Element HoverChrome(Element child, bool persistent, float box = 24f) => new BoxEl
    {
        Width = box,
        Height = box,
        Shrink = 0f,
        AlignItems = FlexAlign.Center,
        Justify = FlexJustify.Center,
        Opacity = persistent ? 0.78f : 0f,
        HoverOpacity = 1f,
        HoverDurationMs = Motion.ControlFaster,
        Children = [child],
    };

    static Element Tag(string text) => new BoxEl
    {
        Shrink = 0f, Padding = new Edges4(6f, 1f, 6f, 2f), Corners = CornerRadius4.All(Radii.Full),
        Fill = Tok.FillSubtleSecondary,
        Children = [new TextEl(text) { Size = 10f, Weight = 600, Color = Tok.TextSecondary, MaxLines = 1 }],
    };

    // ── the row menu (§C4.5) ─────────────────────────────────────────────────────────────────────────────────────────

    IReadOnlyList<MenuFlyoutItem> RowMenu(string sectionId)
    {
        var prefs = _page.Prefs;
        var spec = prefs?.Layout.Find(sectionId);
        if (spec is null) return Array.Empty<MenuFlyoutItem>();

        var loc = prefs!.Layout.Locate(sectionId);
        int within = loc.Index;
        int siblings = loc.Parent is null ? prefs.Layout.Sections.Count : loc.Parent.ChildList.Count;
        string? parentId = loc.Parent?.Id;

        return new List<MenuFlyoutItem>(8)
        {
            new(Loc.Get(CzLoc.Rename), ActionIcons.Resolve(ActionIcons.Rename), true, () => BeginRename(sectionId)),
            new(Loc.Get(CzLoc.Duplicate), default, true, () => _page.Dispatch(new DuplicateSection(sectionId,
                Loc.Format(CzLoc.DuplicateSuffix, ("name", CzGlyphs.TitleOf(spec)))))),
            new(Loc.Get(spec.Hidden ? CzLoc.Show : CzLoc.Hide), default, true,
                () => _page.Dispatch(new SetSectionHidden(sectionId, !spec.Hidden))),
            MenuFlyoutItem.Separator,
            new(Loc.Get(CzLoc.MoveUp), default, within > 0,
                () => _page.Dispatch(new MoveSection(sectionId, parentId, within - 1))),
            new(Loc.Get(CzLoc.MoveDown), default, within < siblings - 1,
                () => _page.Dispatch(new MoveSection(sectionId, parentId, within + 1))),
            MenuFlyoutItem.Separator,
            new(Loc.Get(CzLoc.RemoveSection), default, true, () => Remove(sectionId)),
        };
    }

    void Remove(string sectionId)
    {
        if (_page.Dispatch(new RemoveSection(sectionId)) != SidebarRejectReason.None) return;
        if (string.Equals(_page.Selected.Peek(), sectionId, StringComparison.Ordinal)) _page.Select(null);
    }

    // ── rename ───────────────────────────────────────────────────────────────────────────────────────────────────────

    void BeginRename(string sectionId)
    {
        var spec = _page.Prefs?.Layout.Find(sectionId);
        _editText.Value = spec?.Title ?? "";
        _editing.Value = sectionId;
    }

    void CommitRename(string sectionId, string text)
    {
        _editing.Value = null;
        _page.Dispatch(new RenameSection(sectionId, text));
    }

    void CancelRename() => _editing.Value = null;

    // ── keys ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    void OnListKey(KeyEventArgs e)
    {
        if (_editing.Peek() is not null) return;                     // the inline editor owns its keys
        if (_page.Selected.Peek() is not { Length: > 0 } id) return;
        if (e.KeyCode == Keys.F2) { BeginRename(id); e.Handled = true; return; }
        if (e.KeyCode == Keys.Delete) { Remove(id); e.Handled = true; }
    }

    // ── reorder wiring ───────────────────────────────────────────────────────────────────────────────────────────────

    void ConfigureReorder(Reorderable ro)
    {
        ro.Scene = Context.Scene;
        ro.RequestRender = Bump;
        ro.ItemCount = _rows.Count;
        // The card metrics (R3.2 item 2: 52 top-level / 44 child) ARE the drop pitch — extent + spacing must match what
        // the eye measures or the insertion line lands a row off.
        ro.ItemExtent = 52f;
        ro.Spacing = RowGap;
        ro.ExtentOf = ExtentOf;
        ro.ItemOf = null;                       // the outline never leaves its own list (no cross-list payload)
        ro.OnReorder = Commit;
    }

    float ExtentOf(int index) => (uint)index < (uint)_rows.Count ? _rows[index].Height : 52f;

    void Bump()
    {
        _dragEpoch.Value = _dragEpoch.Peek() + 1;
        Context.RequestRerender();
    }

    void Commit(int from, int to)
    {
        if (SidebarOutlineDrag.ToMove(_rows, from, to) is { } move) _page.Dispatch(move);
    }
}
