using System;
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

// THE FULL-PAGE CUSTOMIZER (plan §C4 + REVISION 2's progressive three-region amendment). Route "sidebar-customize",
// registered by ContentHost + ShellNav like any other destination, so it participates in tabs, back/forward and
// KeepAlive.
//
// ONE DOCUMENT, NO PREVIEW COPY (§C4.3). The page mounts no copy of the layout: it reads UseContext(SidebarPreferences
// .Slot) and every edit goes through prefs.Dispatch(command) → reducer → undo pre-image → LayoutVersion bump → autosave.
// The docked sidebar, this page's outline, its property panel AND its preview all re-render from that one signal in the
// same frame — there is no apply step, no dirty state and no reconciliation risk.
//
// REGIONS (visual-remediation ladder; the rule itself is the pure, unit-tested
// SidebarCustomizerLayout.Tier):
//   ≥ 1320 DIP  Palette (232) + Outline (elastic) + Inspector (320) + persistent Preview (360)
//   1000–1319   Palette + Outline + Inspector
//   820–999     Outline + Inspector; Palette and Templates move to command overflow
//   < 820       Outline only; the Inspector is a bottom sheet opened by selecting a section
//
// The Inspector is two tabs — Properties and Preview — and the Preview renders the REAL CuratedSidebar in Expanded /
// Rail / Drawer form (§C4.8), mounted from the same document signal.
//
// PROPS FREEZE AT MOUNT, so every sub-component takes THIS page (a reference-stable holder of signals + delegates) as
// its single ctor arg — the landed CuratedRowSlot(this, scope) precedent. Each sub-component re-reads
// prefs.LayoutVersion itself, so a document edit re-renders it without the page rebuilding its children.
sealed class SidebarCustomizerPage : Component
{
    const float CommandBarHeight = FluentGpu.Controls.CommandBar.CompactHeight;
    const float RegionGap = Spacing.M;

    /// <summary>The header is a two-line title lane (eyebrow over title) beside the command cluster, so it is taller than
    /// the bare 48-DIP command bar it used to be (R3.2 item 1).</summary>
    const float HeaderHeight = 64f;

    /// <summary>Width the inline Reset button + its gap take out of the command budget at the wide tiers. The pure fit
    /// table (<c>SidebarCustomizerCommandWidths</c>) describes the NATIVE bar plus Done; Reset is external to it, so it is
    /// subtracted from the available width instead of being added to that record (which would rewrite its tests for a
    /// button the table never owned).</summary>
    const float ResetReserve = 76f + Spacing.S;

    /// <summary>Width the saved-locally indicator takes out of the same budget when it is shown.</summary>
    const float SavedReserve = 108f + Spacing.S;

    // ── shared editor state (signals, so a sub-component subscribes exactly what it reads) ────────────────────────────

    /// <summary>The selected section id — the outline's highlight and the inspector's subject.</summary>
    internal readonly Signal<string?> Selected = new(null);

    /// <summary>The selected item inside the selected section (the items block's inline editors).</summary>
    internal readonly Signal<string?> SelectedItem = new(null);

    /// <summary>0 = Properties · 1 = Preview.</summary>
    internal readonly Signal<int> InspectorTab = new(0);

    /// <summary>0 = Expanded · 1 = Rail · 2 = Drawer (§C4.8).</summary>
    internal readonly Signal<int> PreviewMode = new(0);

    /// <summary>The palette's live search text (session-only, never persisted).</summary>
    internal readonly Signal<string> PaletteQuery = new("");

    /// <summary>Bumped whenever <see cref="_rejectKey"/> changes — the inline reject strip's render dep AND the
    /// auto-dismiss timer's key.</summary>
    internal readonly Signal<int> RejectEpoch = new(0);

    /// <summary>The bottom sheet's open state (Narrow tier only).</summary>
    internal readonly Signal<bool> SheetOpen = new(false);

    // The four inspector disclosure signals are GONE (R3.2 item 4): the property groups are always open, so there is no
    // per-group state to own — and the accordion headers they drove were the `[sidebar.customizer.group.*]` bug.

    /// <summary>Banner dismissals + the health re-read nudge (the store's fault properties are not signals).</summary>
    readonly Signal<int> _bannerEpoch = new(0);
    bool _corruptDismissed;
    SidebarPersistenceFault _dismissedPersistenceFault;

    /// <summary>The reject message's loc key (a PLAIN field — the epoch signal above carries the reactivity, so a render
    /// that shows it never subscribes to a string).</summary>
    string? _rejectKey;

    internal SidebarPreferences? Prefs;
    internal WaveeExtensionRegistry? Registry;
    internal ActionServices? Acts;
    internal IOverlayService? OverlaySvc;
    internal LibraryStore? Store;
    internal Action<string, string?>? Go;
    internal Action? Back;

    /// <summary>The persisted visit log used only as a deterministic fallback when this page is mounted outside the shell
    /// and therefore has no <see cref="HistoryStore.BackCtx"/> provider.</summary>
    internal HistoryStore? History;

    /// <summary>The live tier, published as a plain field for the sub-components that shape themselves by it (they render
    /// after this page, the CuratedSidebar precedent).</summary>
    internal SidebarCustomizerTier Tier = SidebarCustomizerTier.Full;

    int _lastTier = -1;
    SidebarCustomizerCommandFit? _commandFit;
    NodeHandle _commandAnchor;
    OverlayHandle? _commandOverlay;

    internal bool FocusTopBarRequested { get; }

    public SidebarCustomizerPage(string? focusTarget)
        => FocusTopBarRequested = string.Equals(
            focusTarget, SidebarLayoutMenu.TopBarFocusArg, StringComparison.Ordinal);

    public override Element Render()
    {
        Prefs = UseContext(SidebarPreferences.Slot);
        Acts = UseContext(ActionServices.Slot);
        OverlaySvc = UseContext(Overlay.Service);
        Store = UseContext(LibraryStore.Slot);
        Registry = UseContext(WaveeExtensionRegistry.Slot) ?? Acts?.Extensions;
        Go = UseContext(HistoryStore.NavCtx);
        Back = UseContext(HistoryStore.BackCtx);
        History = UseContext(HistoryStore.Slot);

        var widthSig = UseMeasuredWidth(4f);
        var boundsSig = UseMeasuredBounds();

        float width = widthSig.Value;
        var tier = SidebarCustomizerLayout.Tier(width > 1f ? width : SidebarCustomizerLayout.FullEnterW, _lastTier);
        _lastTier = (int)tier;
        Tier = tier;

        // Every accepted command bumps this: the command bar's undo/redo enablement, the empty state and the banners all
        // read the document, so the page itself must subscribe it too.
        int layoutVersion = Prefs?.LayoutVersion.Value ?? 0;
        _ = layoutVersion;
        _ = _bannerEpoch.Value;
        int rejectEpoch = RejectEpoch.Value;

        // Open on a useful editing state. A stale/deleted selection falls back to the first authored section; the Blank
        // template remains unselected. This runs after render, so it never writes a signal from the render computation,
        // and it does not auto-open the Narrow bottom sheet.
        UseEffect(EnsureUsefulSelection, DepKey.From(layoutVersion));

        // The inline reject strip auto-dismisses after 4 s (§C4.5). Keyed on the epoch, so each new rejection re-arms it.
        UseTimeout(ClearReject, 4000f, DepKey.From(rejectEpoch));

        // Selecting a section at the Narrow tier opens the sheet; widening past it closes the sheet (the inspector is a
        // column again). A layout effect, never a render-time signal write.
        bool inlineInspector = SidebarCustomizerLayout.InspectorInline(tier);
        UseLayoutEffect(() => { if (inlineInspector) SheetOpen.SetIfChanged(false); }, DepKey.From(inlineInspector));

        float sheetHeight = SidebarCustomizerLayout.SheetHeight(boundsSig.Value.H);

        return new BoxEl
        {
            Key = "customizer", Grow = 1f, Shrink = 1f, Direction = 1, MinWidth = 0f, MinHeight = 0f,
            ClipToBounds = true,
            Children =
            [
                HeaderBar(tier, width > 1f ? width : SidebarCustomizerLayout.FullEnterW),
                Divider(),
                Banners(),
                Body(tier),
                Sheet(tier, sheetHeight),
            ],
        };
    }

    // ── the command bar (§C4.4) ──────────────────────────────────────────────────────────────────────────────────────

    Element HeaderBar(SidebarCustomizerTier tier, float pageWidth)
    {
        var prefs = Prefs;
        bool canUndo = prefs?.CanUndo ?? false;
        bool canRedo = prefs?.CanRedo ?? false;
        string undoTip = canUndo && prefs?.UndoLabel is { Length: > 0 } u
            ? Loc.Format(CzLoc.UndoOf, ("action", Loc.Get(u)))
            : Loc.Get(CzLoc.Undo);
        string redoTip = canRedo && prefs?.RedoLabel is { Length: > 0 } r
            ? Loc.Format(CzLoc.RedoOf, ("action", Loc.Get(r)))
            : Loc.Get(CzLoc.Redo);

        // Reset is INLINE (a secondary button beside Done) only where the title lane is already generous; below that it
        // stays in the command-bar overflow, where it has always been. The saved indicator follows the same rule as the
        // rest of the supporting chrome.
        bool wide = SidebarCustomizerLayout.SubtitleVisible(tier);
        var persistence = prefs?.PersistenceHealth.Value ?? SidebarWriteResult.Healthy;
        bool showSaved = wide && persistence.Success;

        var widths = SidebarCustomizerCommandWidths.Default;
        float available = MathF.Max(0f,
            pageWidth - Spacing.L * 2f - SidebarCustomizerLayout.TitleReserve(tier)
            - BackReserve
            - (wide ? ResetReserve : 0f)
            - (showSaved ? SavedReserve : 0f));
        var fit = SidebarCustomizerCommandLayout.Resolve(available, in widths, tier, _commandFit);
        _commandFit = fit;

        var primary = new System.Collections.Generic.List<AppBarCommand>(3);
        if (fit.Has(SidebarCustomizerInlineCommand.Add))
            primary.Add(new AppBarCommand(Icons.Add, Loc.Get(CzLoc.AddSection), OpenPaletteCommand));
        if (fit.Has(SidebarCustomizerInlineCommand.Undo))
            primary.Add(new AppBarCommand(Icons.Undo, undoTip, UndoStep, Enabled: canUndo));
        if (fit.Has(SidebarCustomizerInlineCommand.Redo))
            primary.Add(new AppBarCommand(Icons.Redo, redoTip, RedoStep, Enabled: canRedo));

        var secondary = new System.Collections.Generic.List<AppBarCommand>(8);
        if (!fit.Has(SidebarCustomizerInlineCommand.Add))
            secondary.Add(new AppBarCommand(Icons.Add, Loc.Get(CzLoc.AddSection), OpenPaletteCommand));
        if (!fit.Has(SidebarCustomizerInlineCommand.Undo))
            secondary.Add(new AppBarCommand(Icons.Undo, undoTip, UndoStep, Enabled: canUndo));
        if (!fit.Has(SidebarCustomizerInlineCommand.Redo))
            secondary.Add(new AppBarCommand(Icons.Redo, redoTip, RedoStep, Enabled: canRedo));
        secondary.Add(new AppBarCommand(Icons.Grid, Loc.Get(CzLoc.Templates), OpenTemplatesCommand));
        if (!SidebarCustomizerLayout.PreviewInline(tier))
            secondary.Add(new AppBarCommand(Icons.SplitView, Loc.Get(CzLoc.Preview), ShowPreview));
        if (!wide)
        {
            secondary.Add(AppBarCommand.Separator);
            secondary.Add(new AppBarCommand(Icons.Refresh, Loc.Get(CzLoc.Reset), ConfirmReset));
        }

        string commandKey = "native-commands:" + (int)tier + ":" + (int)fit.Inline + ":"
            + (wide ? "w" : "-") + (canUndo ? "u" : "-") + (canRedo ? "r" : "-");
        Element nativeBar = Embed.Comp(() => new FluentGpu.Controls.CommandBar
        {
            PrimaryCommands = primary,
            SecondaryCommands = secondary,
            ClosedDisplayMode = CommandBarDisplayMode.Compact,
            LabelsOnOpen = false,
        }) with { Key = commandKey };

        var kids = new System.Collections.Generic.List<Element>(6)
        {
            BackButton(),
            new BoxEl
            {
                Direction = 1,
                Grow = 1f,
                Basis = 0f,
                Shrink = 1f,
                MinWidth = 0f,
                Justify = FlexJustify.Center,
                Children =
                [
                    // THE EYEBROW (R3.2 item 1): the ACTIVE template, which is the one piece of context the title cannot
                    // carry — "Customize sidebar" is true of every document, "WAVEE CURATED" says which one this is.
                    wide
                        ? new TextEl(Loc.Get(SidebarTemplates.NameLocKey(
                              prefs?.Layout.TemplateId ?? SidebarTemplates.Curated)).ToUpperInvariant())
                        {
                            Size = 10f, Weight = 600, Color = Tok.AccentTextPrimary, CharSpacing = 60f,
                            MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        }
                        : new BoxEl { Height = 0f },
                    new TextEl(Loc.Get(CzLoc.Title))
                    {
                        Size = 16f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1,
                        Trim = TextTrim.CharacterEllipsis,
                    },
                ],
            },
        };

        // THE COMMAND CLUSTER is its own centred row (round-3 defect 4): the overflow bar, Reset and Done used to be
        // loose children of the header, so the 48-DIP CommandBar sat beside ~28-DIP buttons with the header's 8-DIP gap
        // between them and read as a cramped, mismatched strip. Grouping them lets the cluster centre as ONE unit with its
        // own tighter gap, and keeps the header's gap for the title↔cluster separation only.
        var cluster = new System.Collections.Generic.List<Element>(4)
        {
            new BoxEl
            {
                Width = fit.NativeBarWidth,
                Height = CommandBarHeight,
                Shrink = 0f,
                MinWidth = 0f,
                ClipToBounds = true,
                Children = [nativeBar],
            },
        };

        if (wide)
            cluster.Add(Button.Create(Loc.Get(CzLoc.Reset), ConfirmReset, ButtonAppearance.Subtle, ControlSize.Small)
                with { Shrink = 0f });

        cluster.Add(Button.Create(Loc.Get(CzLoc.Done), Done, ButtonAppearance.Accent, ControlSize.Small)
            with { Shrink = 0f });

        if (showSaved) kids.Add(SavedIndicator());

        kids.Add(new BoxEl
        {
            Direction = 0,
            Shrink = 0f,
            Gap = Spacing.XS,
            AlignItems = FlexAlign.Center,
            Children = [.. cluster],
        });

        return new BoxEl
        {
            Key = "cmdbar:" + (int)tier,
            Direction = 0,
            Height = HeaderHeight,
            Shrink = 0f,
            Gap = Spacing.S,
            AlignItems = FlexAlign.Center,
            // No Wrap: the title lane is the ONLY flexible child (Grow 1 · Basis 0 · Shrink 1 · MinWidth 0), so under
            // pressure the title ellipsizes and the command cluster holds its width — the header can never reflow to a
            // second line at any tier.
            Padding = new Edges4(Spacing.S, 0f, Spacing.L, 0f),
            OnRealized = h => _commandAnchor = h,
            Children = [.. kids],
        };
    }

    /// <summary>Width the back affordance takes out of the command budget.</summary>
    const float BackReserve = 32f + Spacing.S;

    /// <summary>The customizer's back arrow invokes the shell's real browser-style Back callback. A standalone/headless
    /// mount falls back to the newest non-customizer visit without changing production navigation history.</summary>
    /// <remarks>The extra wrapper box is LOAD-BEARING (round-3 defect 3): <c>ToolTip</c>'s own root declares
    /// <c>AlignSelf = FlexAlign.Start</c> (ToolTip.cs:352-354), which OPTS OUT of the header row's
    /// <c>AlignItems = Center</c> and pinned the arrow to the TOP of the 64-DIP header while the two-line title lane stayed
    /// centred — reading as an arrow stacked above the title. This plain box has no AlignSelf of its own, so the header
    /// centres IT, and inside it the tooltip's Start is a no-op because the box hugs the button's own height.</remarks>
    Element BackButton() => new BoxEl
    {
        Shrink = 0f,
        Children =
        [
            ToolTip.Wrap(
                IconButton.Create(Icons.Back, GoBack, size: ControlSize.Small) with { Shrink = 0f },
                Loc.Get(CzLoc.Back)),
        ],
    };

    void GoBack()
    {
        Prefs?.Flush();
        if (Back is { } back)
        {
            back();
            return;
        }
        if (History?.Entries is { Count: > 0 } log)
        {
            for (int i = log.Count - 1; i >= 0; i--)
            {
                var route = log[i].Route;
                if (string.Equals(route.Name, SidebarLayoutMenu.CustomizeRoute, StringComparison.Ordinal)) continue;
                Go?.Invoke(route.Name, route.Arg);
                return;
            }
        }
        Go?.Invoke("home", null);
    }

    /// <summary>The SAVED-LOCALLY indicator (R3.2 item 1): a 6-DIP success dot plus the label, shown only while
    /// <c>PersistenceHealth</c> is healthy. It deliberately says nothing on a fault — the error <c>InfoBar</c> in
    /// <see cref="Banners"/> owns failures, and two voices telling the same story is how a UI starts lying.</summary>
    static Element SavedIndicator() => new BoxEl
    {
        Direction = 0, Shrink = 0f, Gap = Spacing.XS, AlignItems = FlexAlign.Center,
        Margin = new Edges4(0f, 0f, Spacing.XS, 0f),
        Children =
        [
            new BoxEl
            {
                Width = 6f, Height = 6f, Shrink = 0f, Corners = Radii.Circle(6f),
                Fill = Tok.SystemFillSuccess, HitTestVisible = false,
            },
            new TextEl(Loc.Get(CzLoc.SavedLocally))
            {
                Size = 11f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
        ],
    };

    // ── banners: corruption (§C4.7), save fault, inline rejects ──────────────────────────────────────────────────────

    Element Banners()
    {
        var kids = new System.Collections.Generic.List<Element>(3);
        var prefs = Prefs;
        var persistence = prefs?.PersistenceHealth.Value ?? SidebarWriteResult.Healthy;
        if (persistence.Success) _dismissedPersistenceFault = SidebarPersistenceFault.None;

        if (prefs is { Fault: not SidebarLoadFault.None } && !_corruptDismissed)
        {
            string path = prefs.FaultDetail ?? "";
            kids.Add(InfoBar.Create(
                InfoBarSeverity.Warning,
                Loc.Get(CzLoc.Corrupt),
                Loc.Format(CzLoc.CorruptSub, ("path", path)),
                onClose: () => { _corruptDismissed = true; BumpBanner(); },
                actionButton: new BoxEl
                {
                    Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        Button.Create(Loc.Get(CzLoc.CopyPath), () => CopyPath(path), ButtonAppearance.Subtle,
                            ControlSize.Small),
                        Button.Create(Loc.Get(CzLoc.FaultDiscard), DiscardCorrupt, ButtonAppearance.Standard,
                            ControlSize.Small),
                    ],
                }));
        }

        // The BYTE-BUDGET fault: Commit silently no-ops while over budget, so the customizer is the one place that can
        // tell the user their edits are not reaching disk — and which section to shrink.
        bool saveFault = !persistence.Success
            && persistence.Fault is SidebarPersistenceFault.ConfigTooLarge
                or SidebarPersistenceFault.DocumentTooLarge
                or SidebarPersistenceFault.IoFailure;
        if (saveFault && persistence.Fault != _dismissedPersistenceFault)
            kids.Add(InfoBar.Create(
                InfoBarSeverity.Error,
                Loc.Get(CzLoc.SaveFault),
                persistence.SafeDetail is { Length: > 0 } d
                    ? Loc.Get(CzLoc.SaveFaultSub) + "  (" + d + ")"
                    : Loc.Get(CzLoc.SaveFaultSub),
                onClose: () => { _dismissedPersistenceFault = persistence.Fault; BumpBanner(); }));

        if (_rejectKey is { Length: > 0 } key)
            kids.Add(InfoBar.Create(InfoBarSeverity.Informational, Loc.Get(key), "", onClose: ClearReject));

        if (kids.Count == 0) return new BoxEl { Key = "banners", Height = 0f, Shrink = 0f };
        return new BoxEl
        {
            Key = "banners", Direction = 1, Shrink = 0f, Gap = Spacing.XS,
            Padding = new Edges4(Spacing.L, Spacing.S, Spacing.L, 0f),
            Children = [.. kids],
        };
    }

    // ── the regions ──────────────────────────────────────────────────────────────────────────────────────────────────

    Element Body(SidebarCustomizerTier tier)
    {
        var kids = new System.Collections.Generic.List<Element>(4);

        if (SidebarCustomizerLayout.PaletteInline(tier))
            kids.Add(Region("palette:" + (int)tier, SidebarCustomizerLayout.PaletteWidth,
                Embed.Comp(() => new SidebarCustomizerPalette(this, inFlyout: false)),
                pad: new Edges4(Spacing.S, Spacing.S, Spacing.S, Spacing.S)));

        kids.Add(Region("outline:" + (int)tier, null,
            Embed.Comp(() => new SidebarOutlineView(this)) with { Key = "outline-view:" + (int)tier }));

        if (SidebarCustomizerLayout.InspectorInline(tier))
            kids.Add(Region("inspector:" + (int)tier, SidebarCustomizerLayout.InspectorWidth,
                Embed.Comp(() => new SidebarInspector(this, sheet: false))
                    with { Key = "inspector-view:" + (int)tier }));

        if (SidebarCustomizerLayout.PreviewInline(tier))
            kids.Add(Region("preview:" + (int)tier, SidebarCustomizerLayout.PreviewWidth,
                Embed.Comp(() => new SidebarLivePreview(this)) with { Key = "preview-view:" + (int)tier },
                // Spacing.S, not M: the Drawer form is 360 DIP wide — the same as this column — so every DIP of padding is
                // a DIP the preview well has to clip off the pane it is showing.
                pad: new Edges4(Spacing.S, Spacing.S, Spacing.S, Spacing.S)));

        return new BoxEl
        {
            Key = "body:" + (int)tier, Direction = 0, Grow = 1f, Shrink = 1f, MinHeight = 0f, MinWidth = 0f,
            Gap = RegionGap,
            // NO page-level Fill: the surface under these cards is the shell's own content pane
            // (WaveeColors.ContentSurface), and painting a second layer over it just repaints the same rung. The
            // Settings page paints no background either; its cards float on the same surface.
            Padding = new Edges4(Spacing.L, Spacing.M, Spacing.L, Spacing.M),
            Children = [.. kids],
        };
    }

    /// <summary>One REGION as a distinct card (R3.2 item 6): the four columns used to be flat, undelimited stacks, so a
    /// screenshot showed one undifferentiated field of controls. Fixed-width regions do not shrink; the outline
    /// (<paramref name="width"/> null) is the elastic one.</summary>
    static Element Region(string key, float? width, Element child, Edges4 pad = default) => new BoxEl
    {
        Key = key,
        Direction = 1,
        Grow = width is null ? 1f : 0f,
        Shrink = width is null ? 1f : 0f,
        Width = width ?? float.NaN,     // NaN = auto (the engine's "unset" sentinel), so the elastic region measures
        MinWidth = 0f,
        MinHeight = 0f,
        Corners = Radii.CardAll,
        Fill = Tok.FillCardDefault,
        BorderWidth = 1f,
        BorderColor = Tok.StrokeCardDefault,
        ClipToBounds = true,
        Padding = pad,
        Children = [child],
    };

    /// <summary>The Narrow tier's inspector: an IN-PAGE bottom sheet rather than an <c>Overlay</c> popup — the overlay
    /// service is anchor-relative (there is no full-width sheet presenter today), and an in-page sheet keeps the live
    /// document, the tab order and the outline visible behind it. Always mounted (height 0 when closed) so opening it
    /// never remounts the inspector and loses its tab.</summary>
    Element Sheet(SidebarCustomizerTier tier, float height)
    {
        bool open = !SidebarCustomizerLayout.InspectorInline(tier) && SheetOpen.Value && Selected.Value is not null;
        if (!open) return new BoxEl { Key = "sheet:" + (int)tier, Height = 0f, Shrink = 0f };
        return new BoxEl
        {
            Key = "sheet:" + (int)tier, Direction = 1, Shrink = 0f, Height = height, ClipToBounds = true,
            Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Corners = new CornerRadius4(Radii.Card, Radii.Card, 0f, 0f),
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Height = 36f, Shrink = 0f, AlignItems = FlexAlign.Center,
                    Padding = new Edges4(Spacing.M, 0f, Spacing.S, 0f),
                    Children =
                    [
                        new TextEl(Loc.Get(CzLoc.Properties))
                        {
                            Size = 13f, Weight = 600, Color = Tok.TextSecondary, Grow = 1f, MaxLines = 1,
                        },
                        ToolTip.Wrap(
                            IconButton.Create(Icons.ChromeClose, () => SheetOpen.Value = false, size: ControlSize.Small),
                            Loc.Get(CzLoc.Done)),
                    ],
                },
                Divider(),
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Shrink = 1f, MinHeight = 0f,
                    Children = [Embed.Comp(() => new SidebarInspector(this, sheet: true))],
                },
            ],
        };
    }

    // ── flyout content (Compact/Narrow) ──────────────────────────────────────────────────────────────────────────────

    Element PaletteFlyout() => new BoxEl
    {
        Direction = 1, Width = 300f, MaxHeight = 520f, MinHeight = 0f, ClipToBounds = true,
        Children = [Embed.Comp(() => new SidebarCustomizerPalette(this, inFlyout: true))],
    };

    Element TemplatesFlyout() => new BoxEl
    {
        Direction = 1, Width = 300f, MaxHeight = 520f, MinHeight = 0f, ClipToBounds = true,
        Children = [Embed.Comp(() => new SidebarTemplateList(this))],
    };

    /// <summary>Open the "add a section" palette anchored to the element that ASKED for it (round-2 defect 5).
    /// <para>It used to always anchor to <see cref="_commandAnchor"/> — the header row's node — so clicking the outline's
    /// "Add a section" tail, several hundred DIP away, popped the flyout up at the top-right of the page looking detached
    /// from anything. <see cref="OpenPaletteAt"/> takes the clicking element's realized node so the flyout lands on it;
    /// the parameterless form keeps the header's right-aligned placement, because that IS where its button lives.</para></summary>
    /// <remarks>Parameterless (NOT an optional-argument overload) because <c>AppBarCommand</c> takes a plain
    /// <see cref="Action"/> and a method with optional parameters does not convert to one.</remarks>
    internal void OpenPaletteCommand()
        => OpenCommandSurface(PaletteFlyout, null, FlyoutPlacement.BottomEdgeAlignedRight);

    /// <inheritdoc cref="OpenPaletteCommand"/>
    internal void OpenPaletteAt(NodeHandle anchor)
        => OpenCommandSurface(PaletteFlyout, anchor, FlyoutPlacement.BottomEdgeAlignedLeft);

    void OpenTemplatesCommand() => OpenCommandSurface(TemplatesFlyout, null, FlyoutPlacement.BottomEdgeAlignedRight);

    void OpenCommandSurface(Func<Element> content, NodeHandle? anchor, FlyoutPlacement placement)
    {
        if (OverlaySvc is null) return;
        if (_commandOverlay is { IsOpen: true } open) { open.Close(); return; }
        var at = anchor ?? _commandAnchor;
        _commandOverlay = OverlaySvc.Open(
            () => at,
            content,
            placement,
            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss,
                             Chrome: PopupChrome.Popup) { ConstrainToRootBounds = true });
        _commandOverlay.ClosedAction = () => _commandOverlay = null;
    }

    void ShowPreview()
    {
        InspectorTab.SetIfChanged(1);
        if (!SidebarCustomizerLayout.InspectorInline(Tier) && Selected.Peek() is not null)
            SheetOpen.SetIfChanged(true);
    }

    // ── the editor's ONE mutation path ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Dispatch through <c>SidebarPreferences</c> (reducer → undo → autosave) and surface the rejection inline.
    /// The only mutation path in the whole page, so a rejected command can never look like an applied one.</summary>
    internal SidebarRejectReason Dispatch(SidebarCommand command) => Dispatch(command, topBar: false);

    /// <summary>The same mutation path with the top-bar rejection vocabulary enabled.</summary>
    internal SidebarRejectReason DispatchTopBar(SidebarCommand command) => Dispatch(command, topBar: true);

    SidebarRejectReason Dispatch(SidebarCommand command, bool topBar)
    {
        if (Prefs is not { } prefs) return SidebarRejectReason.None;
        var reason = prefs.Dispatch(command);
        if (reason == SidebarRejectReason.None)
        {
            if (_rejectKey is not null) ClearReject();
            return reason;
        }
        string? key = RejectLocKey(reason, topBar);
        if (key is null) return reason;
        _rejectKey = key;
        RejectEpoch.Value = RejectEpoch.Peek() + 1;
        return reason;
    }

    /// <summary>Which rejections have something honest to say inline. The rest (a no-op edit, an unknown id, a duplicate
    /// item, the 40-section cap, a non-whitelisted icon) have no key in the catalog — surfacing "[key]" would be worse
    /// than staying quiet, so they stay silent and are recorded in the wave's HANDOFF.</summary>
    static string? RejectLocKey(SidebarRejectReason reason, bool topBar) => reason switch
    {
        SidebarRejectReason.SectionCapReached when topBar => SidebarNavBandLoc.CapReached,
        SidebarRejectReason.DuplicateItem when topBar => CzLoc.TopBarDuplicate,
        SidebarRejectReason.InvalidIcon when topBar => CzLoc.TopBarInvalidIcon,
        SidebarRejectReason.UnknownItem when topBar => CzLoc.TopBarUnknownItem,
        SidebarRejectReason.NoChange when topBar => CzLoc.TopBarNoChange,
        SidebarRejectReason.NestingTooDeep or SidebarRejectReason.KindNotNestable => CzLoc.RejectNesting,
        SidebarRejectReason.ConfigTooLarge => CzLoc.RejectConfigTooLarge,
        SidebarRejectReason.ExtensionRefMissing => CzLoc.RejectExtensionRefMissing,
        _ => null,
    };

    void ClearReject()
    {
        if (_rejectKey is null) return;
        _rejectKey = null;
        RejectEpoch.Value = RejectEpoch.Peek() + 1;
    }

    void BumpBanner() => _bannerEpoch.Value = _bannerEpoch.Peek() + 1;

    void UndoStep()
    {
        Prefs?.Undo();
        ClearReject();
    }

    void RedoStep()
    {
        Prefs?.Redo();
        ClearReject();
    }

    void DiscardCorrupt()
    {
        Prefs?.DiscardCorruptDocument();
        _corruptDismissed = false;
        BumpBanner();
    }

    void CopyPath(string path)
    {
        if (path.Length == 0) return;
        Acts?.Clipboard?.SetText(path);
    }

    /// <summary>Done: flush any coalesced document write and leave. The customizer is an ordinary route, so the toolbar's
    /// Back button is the real "return where I came from"; this button navigates HOME because a page has no reachable
    /// seam onto the shell's back stack (wiring one means editing WaveeShell — outside this wave's ownership).</summary>
    void Done()
    {
        Prefs?.Flush();
        Go?.Invoke("home", null);
    }

    // ── selection + section adds (shared with the palette / outline / inspector) ──────────────────────────────────────

    internal void Select(string? sectionId)
    {
        Selected.SetIfChanged(sectionId);
        SelectedItem.SetIfChanged(null);
        if (sectionId is not null && !SidebarCustomizerLayout.InspectorInline(Tier)) SheetOpen.Value = true;
    }

    /// <summary>Top-level section count — where the palette appends.</summary>
    internal int TopLevelCount => Prefs?.Layout.Sections.Count ?? 0;

    /// <summary>Add a plain section of <paramref name="kind"/> at the end and select it.</summary>
    internal void AddSectionOfKind(SidebarSectionKind kind)
    {
        int at = TopLevelCount;
        if (Dispatch(new AddSection(kind, at)) != SidebarRejectReason.None) return;
        SelectAt(at);
    }

    /// <summary>Add a contributed (Extension) section for one contribution id, seeded with its schema defaults so the
    /// feed is bounded before the inspector is touched.</summary>
    internal void AddContribution(string contributionId)
    {
        if (string.IsNullOrEmpty(contributionId)) return;
        var config = SidebarJson.EmptyObject;
        int schemaVersion = 1;
        if (Registry is { } reg && reg.TryGetSource(contributionId, out var source))
        {
            config = SidebarConfigJson.Defaults(source.ConfigSchema);
            schemaVersion = source.ConfigSchema.Version;
        }
        int at = TopLevelCount;
        var xref = new SidebarExtensionRef(SidebarContributions.WaveeExtensionId, contributionId, schemaVersion, config);
        if (Dispatch(new AddSection(SidebarSectionKind.Extension, at, Extension: xref)) != SidebarRejectReason.None)
            return;
        SelectAt(at);
    }

    /// <summary>"Recently played": a JumpBackIn section flipped to the play log. Two commands (and therefore two undo
    /// steps) because <c>AddSection</c> seeds the kind's DEFAULT display and carries no display override — an honest
    /// deviation from "one palette click = one undo step".</summary>
    internal void AddRecentlyPlayed()
    {
        int at = TopLevelCount;
        if (Dispatch(new AddSection(SidebarSectionKind.JumpBackIn, at)) != SidebarRejectReason.None) return;
        string? id = IdAt(at);
        if (id is not null)
            Dispatch(new SetDisplayOption(id, SidebarDisplayField.RecentsSource, (int)SidebarRecentsSource.Played));
        SelectAt(at);
    }

    /// <summary>An action shortcut: the picker first, then ONE <c>AddSection(StaticLinks, Item: the bound action)</c>, so
    /// the whole gesture is a single undo step.</summary>
    internal void AddActionShortcut()
    {
        SidebarActionPicker.Open(this, null, binding =>
        {
            int at = TopLevelCount;
            var item = new SidebarItemSpec(SidebarIds.NewItem(), SidebarItemTarget.Action,
                Key: binding.ActionKey, Action: binding);
            if (Dispatch(new AddSection(SidebarSectionKind.StaticLinks, at, Item: item)) != SidebarRejectReason.None)
                return;
            SelectAt(at);
        });
    }

    /// <summary>Liked Songs as one pre-seeded shortcut section: one reducer command, one undo entry, no frozen label.</summary>
    internal void AddLikedSongsShortcut()
    {
        int at = TopLevelCount;
        var item = new SidebarItemSpec(SidebarIds.NewItem(), SidebarItemTarget.Route, "liked",
            IconOverride: "Heart");
        if (Dispatch(new AddSection(SidebarSectionKind.StaticLinks, at, Item: item)) != SidebarRejectReason.None)
            return;
        SelectAt(at);
    }

    void SelectAt(int index)
    {
        if (IdAt(index) is { } id) Select(id);
    }

    string? IdAt(int index)
    {
        var sections = Prefs?.Layout.Sections;
        return sections is not null && (uint)index < (uint)sections.Count ? sections[index].Id : null;
    }

    void EnsureUsefulSelection()
    {
        var sections = Prefs?.Layout.Sections;
        if (sections is null || sections.Count == 0)
        {
            Selected.SetIfChanged(null);
            SelectedItem.SetIfChanged(null);
            return;
        }

        string? current = Selected.Peek();
        if (current is not null && ContainsSection(sections, current)) return;

        string? next = null;
        for (int i = 0; i < sections.Count; i++)
        {
            if (sections[i].Kind != SidebarSectionKind.Divider) { next = sections[i].Id; break; }
        }
        next ??= sections[0].Id;
        Selected.SetIfChanged(next);
        SelectedItem.SetIfChanged(null);
    }

    static bool ContainsSection(System.Collections.Generic.IReadOnlyList<SidebarSectionSpec> sections, string id)
    {
        for (int i = 0; i < sections.Count; i++)
        {
            var section = sections[i];
            if (string.Equals(section.Id, id, StringComparison.Ordinal)) return true;
            if (ContainsSection(section.ChildList, id)) return true;
        }
        return false;
    }

    // ── templates (§C4.7 confirmations) ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Apply a template. The confirmation is SKIPPED when the current document still equals a freshly built copy
    /// of its own template (modulo ids) — there is nothing to lose. Both paths dispatch exactly one command, so both cost
    /// exactly one undo slot and the dialog can honestly say "you can undo this".</summary>
    internal void ApplyTemplate(string templateId)
    {
        if (Prefs is not { } prefs) return;
        string name = Loc.Get(SidebarTemplates.NameLocKey(templateId));
        bool pristine = SidebarLayoutCompare.EqualTemplateSectionsIgnoringIds(
            prefs.Layout, SidebarTemplates.Build(prefs.Layout.TemplateId));
        if (pristine)
        {
            Dispatch(new Wavee.Core.Sidebar.ApplyTemplate(templateId));
            Select(null);
            return;
        }
        ShowTemplateConfirmation(
            Loc.Format(CzLoc.ApplyTemplateTitle, ("template", name)),
            Loc.Get(CzLoc.ApplyTemplateBody),
            Loc.Get(CzLoc.ApplyTemplateConfirm),
            SidebarTemplates.Build(templateId),
            () => { Dispatch(new Wavee.Core.Sidebar.ApplyTemplate(templateId)); Select(null); });
    }

    void ConfirmReset()
    {
        if (Prefs is not { } prefs) return;
        string name = Loc.Get(SidebarTemplates.NameLocKey(prefs.Layout.TemplateId));
        var target = SidebarTemplates.Build(prefs.Layout.TemplateId);
        if (SidebarLayoutCompare.EqualTemplateSectionsIgnoringIds(prefs.Layout, target))
        {
            Dispatch(new ResetLayout());
            Select(null);
            return;
        }
        ShowTemplateConfirmation(
            Loc.Get(CzLoc.ResetTitle),
            Loc.Format(CzLoc.ResetBody, ("template", name)),
            Loc.Get(CzLoc.ResetConfirm),
            target,
            () => { Dispatch(new ResetLayout()); Select(null); });
    }

    void ShowTemplateConfirmation(string title, string body, string primary,
        SidebarCustomLayout target, Action confirm)
    {
        if (OverlaySvc is not { } overlay) { confirm(); return; }
        ContentDialog.Show(overlay, dialog =>
        {
            dialog.Title = title;
            dialog.PrimaryText = primary;
            dialog.CloseText = Loc.Get(Strings.Auth.Cancel);
            dialog.DefaultButton = ContentDialog.DefaultBtn.Primary;
            dialog.DialogWidth = SidebarPickers.DialogW;
            dialog.Content = new BoxEl
            {
                Direction = 1, Gap = Spacing.M, MinWidth = SidebarPickers.BodyW,
                Children =
                [
                    new TextEl(body)
                    {
                        Size = 13f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 3,
                    },
                    SidebarMiniature.Template(target),
                ],
            };
            dialog.PrimaryClick = confirm;
        });
    }
}

/// <summary>A command-bar button that opens a bounded flyout (the Compact/Narrow palette + template lists). Its own
/// component because it needs the overlay service, an anchor node and the open handle — the landed
/// <c>SidebarLayoutMenuButton</c> shape.</summary>
sealed class CzFlyoutButton : Component
{
    readonly string _glyph;
    readonly string _labelKey;
    readonly Func<Element> _content;

    public CzFlyoutButton(SidebarCustomizerPage page, string glyph, string labelKey, Func<Element> content)
    {
        _ = page;   // the content closure already owns the page; the button itself only needs the overlay service
        _glyph = glyph; _labelKey = labelKey; _content = content;
    }

    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var svc = UseContext(Overlay.Service);

        void Toggle()
        {
            if (svc is null) return;
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            handle.Value = svc.Open(
                () => anchor.Value,
                _content,
                FlyoutPlacement.BottomEdgeAlignedLeft,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss,
                                 Chrome: PopupChrome.Popup) { ConstrainToRootBounds = false });
            handle.Value.ClosedAction = () => handle.Value = null;
        }

        var button = Button.Create(Loc.Get(_labelKey), Toggle, ButtonAppearance.Subtle, ControlSize.Small,
            glyph: _glyph) with
        {
            OnRealized = h => anchor.Value = h,
        };
        return button;
    }
}

/// <summary>The customizer's loc KEYS as literals, in one place — the landed <c>CuratedLoc</c> precedent (this wave must
/// not edit <c>assets/loc/*.json</c>, and a typo renders loudly as <c>[key]</c> instead of silently). As of R3.2 EVERY key
/// below resolves: the four <c>group.*</c> keys and the seven that were marked NEW have all landed in
/// <c>assets/loc/en-US.json</c> (nl/ko inherit through the en-US fallback link of the resolution chain).</summary>
static class CzLoc
{
    public const string Title = "sidebar.customizer.title";

    /// <summary>UNUSED since R3.2: the header's second line is now the active-template eyebrow, and the preview's footer is
    /// <see cref="PreviewHint"/>. Kept so the catalog entry has a named owner if a caller wants it back.</summary>
    public const string Subtitle = "sidebar.customizer.subtitle";
    public const string Templates = "sidebar.customizer.templates";
    public const string AddSection = "sidebar.customizer.addSection";
    public const string Outline = "sidebar.customizer.outline";
    public const string Properties = "sidebar.customizer.properties";
    public const string GroupGeneral = "sidebar.customizer.group.general";
    public const string GroupContent = "sidebar.customizer.group.content";
    public const string GroupAppearance = "sidebar.customizer.group.appearance";
    public const string GroupBehavior = "sidebar.customizer.group.behavior";
    public const string NoSelection = "sidebar.customizer.noSelection";
    public const string Preview = "sidebar.customizer.preview";
    public const string PreviewExpanded = "sidebar.customizer.previewExpanded";
    public const string PreviewRail = "sidebar.customizer.previewRail";
    public const string PreviewDrawer = "sidebar.customizer.previewDrawer";
    public const string Undo = "sidebar.customizer.undo";
    public const string Redo = "sidebar.customizer.redo";
    public const string UndoOf = "sidebar.customizer.undoOf";
    public const string RedoOf = "sidebar.customizer.redoOf";
    public const string Reset = "sidebar.customizer.reset";
    public const string Done = "sidebar.customizer.done";
    public const string Empty = "sidebar.customizer.empty";
    public const string EmptySub = "sidebar.customizer.emptySub";
    public const string AddFirst = "sidebar.customizer.addFirst";
    public const string StartFromTemplate = "sidebar.customizer.startFromTemplate";
    public const string RenameHint = "sidebar.customizer.renameHint";
    public const string LiftHint = "sidebar.customizer.liftHint";
    public const string Hidden = "sidebar.customizer.hidden";
    public const string RejectNesting = "sidebar.customizer.rejectNesting";
    public const string RejectConfigTooLarge = "sidebar.customizer.rejectConfigTooLarge";
    public const string RejectExtensionRefMissing = "sidebar.customizer.rejectExtensionRefMissing";
    public const string DuplicateSuffix = "sidebar.customizer.duplicateSuffix";
    public const string ItemLabel = "sidebar.customizer.itemLabel";
    public const string ItemLabelPlaceholder = "sidebar.customizer.itemLabelPlaceholder";
    public const string ItemIcon = "sidebar.customizer.itemIcon";
    public const string ItemAdd = "sidebar.customizer.itemAdd";
    public const string ItemRemove = "sidebar.customizer.itemRemove";
    public const string MoveUp = "sidebar.customizer.moveUp";
    public const string MoveDown = "sidebar.customizer.moveDown";
    public const string MissingEntity = "sidebar.customizer.missingEntity";
    public const string MissingEntitySub = "sidebar.customizer.missingEntitySub";
    public const string Corrupt = "sidebar.customizer.corrupt";
    public const string CorruptSub = "sidebar.customizer.corruptSub";
    public const string CopyPath = "sidebar.customizer.copyPath";
    public const string FaultDiscard = "sidebar.layoutFault.discard";
    public const string SaveFault = "sidebar.customizer.saveFault";
    public const string SaveFaultSub = "sidebar.customizer.saveFaultSub";
    public const string ApplyTemplateTitle = "sidebar.customizer.applyTemplateTitle";
    public const string ApplyTemplateBody = "sidebar.customizer.applyTemplateBody";
    public const string ApplyTemplateConfirm = "sidebar.customizer.applyTemplateConfirm";
    public const string ResetTitle = "sidebar.customizer.resetTitle";
    public const string ResetBody = "sidebar.customizer.resetBody";
    public const string ResetConfirm = "sidebar.customizer.resetConfirm";
    /// <summary>The LIVE collapse row's label ("Collapse section") — it doubles as the undo label for the same command,
    /// which is exactly right: the control and the history entry should name one action.</summary>
    public const string Collapse = "sidebar.customizer.undo.collapseSection";

    public const string Rename = "sidebar.customizer.undo.renameSection";
    public const string Duplicate = "sidebar.customizer.undo.duplicateSection";
    public const string RemoveSection = "sidebar.customizer.undo.removeSection";
    public const string Show = "sidebar.customizer.undo.showSection";
    public const string Hide = "sidebar.customizer.undo.hideSection";
    public const string ExtensionManage = "sidebar.extension.manage";
    public const string ItemCount = "sidebar.v3.itemCount";
    public const string Position = "sidebar.pin.position";
    public const string SearchPlaceholder = "sidebar.v3.searchPlaceholder";

    // R3.2's chrome captions. Verified present in assets/loc/en-US.json (the orchestrator landed them with the four
    // group.* keys that used to render as "[key]" — the bug this wave removes).
    public const string SavedLocally = "sidebar.customizer.savedLocally";
    public const string VisibleCount = "sidebar.customizer.visibleCount";
    public const string SectionCount = "sidebar.customizer.sectionCount";
    public const string PreviewHint = "sidebar.customizer.previewHint";
    public const string TopBar = "sidebar.customizer.topBar";
    public const string TopBarGlobal = "sidebar.customizer.topBarGlobal";
    public const string TopBarEmpty = "sidebar.customizer.topBarEmpty";
    public const string TopBarAddItem = "sidebar.customizer.topBarAddItem";
    public const string TopBarAddTrack = "sidebar.customizer.topBarAddTrack";
    public const string TopBarDuplicate = "sidebar.customizer.topBarDuplicate";
    public const string TopBarInvalidIcon = "sidebar.customizer.topBarInvalidIcon";
    public const string TopBarUnknownItem = "sidebar.customizer.topBarUnknownItem";
    public const string TopBarNoChange = "sidebar.customizer.topBarNoChange";
    public const string CuratedLayout = "sidebar.customizer.curatedLayout";
    public const string CuratedInactive = "sidebar.customizer.curatedInactive";

    /// <summary>The back affordance's tooltip. REUSED from the auth flow rather than invented: it is the bare word "Back"
    /// in all three locales, and a customizer-specific key would be a fourth spelling of one word (see the HANDOFF — a
    /// shared <c>common.back</c> would be the tidy fix).</summary>
    public const string Back = "auth.back";

    public const string PaletteSearch = "sidebar.customizer.paletteSearch";
    public const string ItemAction = "sidebar.customizer.itemAction";
    public const string ItemActionSub = "sidebar.customizer.itemActionSub";
    public const string TargetLabel = "sidebar.customizer.targetLabel";
    public const string TargetNone = "sidebar.customizer.targetNone";
    public const string TargetEntity = "sidebar.customizer.targetEntity";
    public const string TargetTrack = "sidebar.customizer.targetTrack";
    public const string TargetRoute = "sidebar.customizer.targetRoute";
    public const string TargetNowPlaying = "sidebar.section.nowPlaying";   // reused, not new
}
