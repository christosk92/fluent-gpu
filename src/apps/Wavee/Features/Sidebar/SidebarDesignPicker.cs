using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;

namespace Wavee;

// §C6.1 + §C6.2 — the SHARED three-card preview selector, used verbatim by the fresh-install chooser (Open, below) and
// by Settings → General (SettingsPage.General.cs). One file, one card mechanic, one apply path: a design switch always
// goes through SidebarPreferences.SwitchDesign, which snapshots the outgoing mode's state and reseeds the incoming one
// (locked decision 3) — a raw Design.Value write from either host would silently drop that contract.
//
// THREE THINGS THIS FILE IS CAREFUL ABOUT
//
//  1. FROZEN PROPS. The selection arrives as a Func<int>, not an int and not a mirror Signal<int>: the live truth is
//     SidebarPreferences.Design (a Signal<SidebarDesign>), and reading it through the delegate INSIDE Render subscribes
//     this component to it directly. A mirror signal would need a write-during-render (the BackwardsWriteGuard's exact
//     tripwire) to stay in step with a switch made from the sidebar's own layout menu while Settings is open.
//  2. NO LIVE SIDEBARS OR MICRO-TEXT IN THE PREVIEW. Each card's miniature is static semantic geometry, never a
//     mounted mode component. At this scale text only becomes grey noise (or leaks opaque IDs while data is warming),
//     so every content slot is represented by a clean bar/tile hierarchy.
//  3. NO IMAGE DECODES. The miniature's covers are solid tiles, not Images: at 10-16 DIP a real cover decode buys
//     nothing legible and would put three N-cover working sets behind a dialog the user sees once.
sealed class SidebarDesignPicker : Component
{
    readonly Func<int> _selected;
    readonly Action<int> _onChange;
    readonly bool _compact;
    readonly bool _allowCustom;

    /// <param name="selected">The live selection, read on every render (0 Classic · 1 Library · 2 Wavee Curated — the
    /// persisted <c>WaveeSettings.SidebarDesign</c> numbering, via <see cref="SidebarDesignGating.IndexOf"/>).</param>
    /// <param name="onChange">Applied IMMEDIATELY on click — no confirmation, no restart (§C6.1).</param>
    /// <param name="compact">The 200×168 card (Settings, where the picker shares a page column) instead of 224×196.</param>
    /// <param name="allowCustom">Temporary feature gate for the unfinished Custom design. When false, its preview is
    /// still visible for discoverability but is disabled and labelled “Coming soon”.</param>
    public SidebarDesignPicker(Func<int> selected, Action<int> onChange, bool compact = false, bool allowCustom = false)
    {
        _selected = selected; _onChange = onChange; _compact = compact; _allowCustom = allowCustom;
    }

    // ── hosts ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The card row bound to the preference service: the ONE apply path (<c>SwitchDesign</c>) and the ONE
    /// selection source (<c>Design</c>). <paramref name="settings"/> is the fallback writer for the isolated-mount case
    /// (a settings page hosted without <c>SidebarPreferences</c> — the picker still functions and still persists).</summary>
    public static Element Row(SidebarPreferences? prefs, IAppSettings? settings, bool compact = false,
                              bool allowCustom = false)
        => Embed.Comp(() => new SidebarDesignPicker(
            () => prefs is not null
                ? SidebarDesignGating.IndexOf(prefs.Design.Value)
                : SidebarDesignGating.IndexOf(SidebarDesignGating.ActiveDesign(settings)),
            value => Apply(prefs, settings, value),
            compact,
            allowCustom))
            // KEYED BY THE FROZEN LAYOUT/AVAILABILITY PROPS. `prefs`/`settings` are reference-stable for the process,
            // but compact can change at a viewport tier and the temporary availability gate will eventually flip.
            with { Key = $"sidebar.design.picker:{(compact ? "compact" : "full")}:{allowCustom}" };

    /// <summary>Apply a card's value. Goes through <c>SwitchDesign</c> (state snapshot/restore + the settings write +
    /// the design signal bump) whenever the service exists; falls back to a bare settings write when it does not, so an
    /// isolated settings page still records the choice for the next launch.</summary>
    public static void Apply(SidebarPreferences? prefs, IAppSettings? settings, int value)
    {
        var design = SidebarDesignGating.FromIndex(value);
        if (prefs is not null) { prefs.SwitchDesign(design); return; }
        settings?.Set(WaveeSettings.SidebarDesign, (int)design);
    }

    // ── the fresh-install chooser (§C6.2) ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Open the one-time chooser. A custom modal overlay rather than <c>ContentDialog</c> for one measured
    /// reason: the card row is three 224-DIP cards plus padding (744), and <c>ContentDialog</c>'s plate is hard-clamped
    /// to 548 — the picker would be squeezed into one column. Everything else mirrors the dialog exactly: the same
    /// <c>PopupChrome.Modal</c> scrim + open/close motion, the same focus trap, no light dismiss, Escape closes.
    ///
    /// <para>EVERY close path burns the marker: <see cref="OverlayHandle.ClosedAction"/> calls
    /// <c>SidebarDesignGating.MarkChooserSeen</c>, so "Use this layout", "Not now", the Escape key and a shutdown-time
    /// close all land there — and the buttons do not need to remember to. Whatever design is applied at that moment is
    /// the answer (Curated unless the user clicked another card); the dialog never writes the design itself.</para>
    ///
    /// Returns null (opening nothing) when the overlay/preference/settings seam is absent — the caller's gate has
    /// already decided WHETHER to open; this only decides whether it CAN.</summary>
    public static OverlayHandle? Open(IOverlayService? overlay, SidebarPreferences? prefs, IAppSettings? settings,
                                      Action<string, string?>? go)
    {
        if (overlay is null || prefs is null || settings is null) return null;

        var box = new OverlayHandle?[1];   // boxed: the body's close callback runs after Open() returns
        var handle = overlay.Open(
            static () => NodeHandle.Null,
            () => Embed.Comp(() => new SidebarChooserCard(prefs, settings, go, () => box[0])),
            FlyoutPlacement.BottomCenter,
            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.Modal, Chrome: PopupChrome.Modal));
        box[0] = handle;
        handle.ClosedAction = () => SidebarDesignGating.MarkChooserSeen(settings);
        return handle;
    }

    // ── render ────────────────────────────────────────────────────────────────────────────────────────────────────────

    public override Element Render()
    {
        int sel = _selected();
        var m = Metrics.For(_compact);

        // The two available choices remain one real radio group. Custom is kept OUTSIDE it while unavailable so it is
        // absent from arrow-key selection and cannot be invoked accidentally; its disabled card stays in the same
        // wrapping row to communicate that the design exists without pretending it is usable.
        if (!_allowCustom)
        {
            var unavailable = Card(SidebarDesign.Curated, sel == (int)SidebarDesign.Curated, in m, comingSoon: true);
            return new BoxEl
            {
                Direction = 0, Wrap = true, Gap = Spacing.M, AlignItems = FlexAlign.Start,
                Children =
                [
                    WaveePicker.Strip(2, sel < 2 ? sel : -1,
                        (i, on) => Card(SidebarDesignGating.FromIndex(i), on, in m), _onChange),
                    unavailable with
                    {
                        IsEnabled = false, Focusable = false, TabStop = false,
                        Role = AutomationRole.RadioButton, Cursor = CursorId.No,
                        Opacity = 0.62f, HoverScale = 1f, PressScale = 1f,
                    },
                ],
            };
        }

        return WaveePicker.Strip(3, sel, (i, on) => Card(SidebarDesignGating.FromIndex(i), on, in m), _onChange);
    }

    BoxEl Card(SidebarDesign design, bool on, in Metrics m, bool comingSoon = false)
    {
        // WaveePicker owns the card shell, the accent ink pair and the selected-label treatment — the same three things
        // the Settings density/page-layout/palette pickers were each carrying their own copy of.
        var ink = WaveePicker.Ink.For(on);

        Element preview = new BoxEl
        {
            Height = m.PreviewH, AlignSelf = FlexAlign.Stretch, Shrink = 0f,
            Direction = 1, Gap = m.Gap, ClipToBounds = true,
            Padding = new Edges4(8f, 7f, 8f, 0f),   // no bottom pad: the miniature CONTINUES past the fold, like a pane
            Corners = CornerRadius4.All(6f),
            Fill = on ? Tok.AccentSubtle : Tok.FillLayerDefault,
            BorderWidth = 1f,
            BorderColor = on ? Tok.AccentDefault : Tok.StrokeCardDefault,
            Children = design switch
            {
                SidebarDesign.LibraryV3 => LibraryPreview(in m, ink.Block, ink.Faint),
                SidebarDesign.Curated => CuratedPreview(in m, ink.Block, ink.Faint),
                _ => ClassicPreview(in m, ink.Block, ink.Faint),
            },
        };

        var title = WaveePicker.Label(Loc.Get(SidebarDesignGating.TitleKey(design)), on, m.TitleSize);
        Element titleRow = new BoxEl
        {
            Direction = 0, Gap = 6f, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Stretch,
            // A11y honesty (§C6.1): the selected card is distinguishable by the "Active" tag as well as by colour, so
            // the choice survives a colour-blind read.
            Children = comingSoon
                ? [title with { Shrink = 1f }, StatusTag(Loc.Get(Strings.Sidebar.Design.ComingSoon), m, active: false)]
                : on ? [title with { Shrink = 1f }, StatusTag(Loc.Get(Strings.Sidebar.Design.Active), m, active: true)] : [title],
        };

        return WaveePicker.Card(on, m.Shell,
            preview,
            titleRow,
            new TextEl(Loc.Get(SidebarDesignGating.SubtitleKey(design)))
            {
                Size = m.SubSize, Color = Tok.TextTertiary,
                Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.WordEllipsis,
                AlignSelf = FlexAlign.Stretch,
            }) with { Key = SidebarDesignInfo.Slug(design) };
    }

    /// <summary>The selected card's persistent "Active" pill. It rides the TITLE row rather than the preview's top-right
    /// corner (the spec's sketch): the engine has no absolute positioning, and reserving an overlay row inside the
    /// 116-DIP preview would cost the Curated miniature — five stacked bands — the space it needs. Being a tag rather
    /// than a colour is the point (it survives a colour-blind read).</summary>
    static Element StatusTag(string text, in Metrics m, bool active) => new BoxEl
    {
        Height = Spacing.L, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
        Corners = Radii.PillAll, Fill = active ? Tok.AccentDefault : Tok.FillControlDefault,
        Children = [new TextEl(text)
            { Size = m.TagSize, Weight = 600, Color = active ? Tok.TextOnAccentPrimary : Tok.TextSecondary, MaxLines = 1 }],
    };

    // ── the three miniatures ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Classic: the Library icon shortcuts, a divider, then the flat playlist list (§C6.1).</summary>
    static Element[] ClassicPreview(in Metrics m, ColorF block, ColorF faint)
    {
        int icons = m.Compact ? 4 : 5;
        int arts = m.Compact ? 2 : 3;
        var kids = new List<Element>(icons + arts + 1);
        for (int i = 0; i < icons; i++) kids.Add(IconRow(IconBarW(i), block, faint));
        kids.Add(Hairline(faint));
        for (int i = 0; i < arts; i++) kids.Add(ArtRow(m, block, faint));
        return kids.ToArray();
    }

    /// <summary>Library V3: the filter chip strip, the sort pill, then the unified list (§C6.1).</summary>
    static Element[] LibraryPreview(in Metrics m, ColorF block, ColorF faint)
    {
        int arts = m.Compact ? 3 : 4;
        var kids = new List<Element>(arts + 2)
        {
            new BoxEl
            {
                Direction = 0, Gap = 4f, Shrink = 0f,
                Children = [Pill(26f, 9f, block), Pill(20f, 9f, faint), Pill(24f, 9f, faint), Pill(18f, 9f, faint)],
            },
            new BoxEl
            {
                Direction = 0, Gap = 4f, Shrink = 0f, AlignItems = FlexAlign.Center,
                Children = [Pill(34f, 8f, faint), new BoxEl { Grow = 1f, HitTestVisible = false }],
            },
        };
        for (int i = 0; i < arts; i++) kids.Add(ArtRow(m, block, faint));
        return kids.ToArray();
    }

    /// <summary>Wavee Curated: two pin tiles, a divider, the 2-up "Jump back in" grid, the app-route links, then a
    /// library section (§C6.1).</summary>
    static Element[] CuratedPreview(in Metrics m, ColorF block, ColorF faint)
    {
        int icons = m.Compact ? 2 : 3;
        int arts = m.Compact ? 1 : 2;
        var kids = new List<Element>(icons + arts + 3)
        {
            new BoxEl
            {
                Direction = 0, Gap = 5f, Shrink = 0f,
                Children = [PinTile(faint), PinTile(faint)],
            },
            Hairline(faint),
            new BoxEl
            {
                Direction = 0, Gap = 5f, Shrink = 0f,
                Children = [GridCell(block, faint), GridCell(block, faint)],
            },
        };
        for (int i = 0; i < icons; i++) kids.Add(IconRow(IconBarW(i), block, faint));
        for (int i = 0; i < arts; i++) kids.Add(ArtRow(m, block, faint));
        return kids.ToArray();
    }

    // ── miniature primitives (SidebarSkeletons' shape language at 1/4 scale) ──────────────────────────────────────────

    static float IconBarW(int i) => i switch { 0 => 46f, 1 => 38f, 2 => 42f, 3 => 34f, _ => 40f };

    static Element Bar(float w, float h, ColorF fill) =>
        SidebarMiniature.Bar(w, h, fill);

    static Element Pill(float w, float h, ColorF fill) => SidebarMiniature.Pill(w, h, fill);

    static Element Hairline(ColorF fill) => SidebarMiniature.Hairline(fill);

    static Element IconRow(float barW, ColorF block, ColorF faint)
        => SidebarMiniature.IconRow(barW, block, faint);

    /// <summary>One text-free list row: a cover tile plus two geometric metadata bars.</summary>
    static Element ArtRow(in Metrics m, ColorF block, ColorF faint)
    {
        float h = m.RowH;

        return new BoxEl
        {
            Direction = 0, Height = h, Shrink = 0f, Gap = 5f, AlignItems = FlexAlign.Center,
            Children =
            [
                new BoxEl { Width = h, Height = h, Shrink = 0f, Corners = CornerRadius4.All(2.5f), Fill = block },
                new BoxEl
                {
                    Direction = 1, Gap = Spacing.XXS, Grow = 1f, Basis = 0f, MinWidth = 0f,
                    Children =
                    [
                        Bar(m.Compact ? 52f : 62f, Spacing.XS, faint),
                        Bar(m.Compact ? 34f : 42f, Spacing.XXS, faint),
                    ],
                },
            ],
        };
    }

    /// <summary>A text-free pin tile. The short inner bar communicates content without pretending to be legible copy.</summary>
    static Element PinTile(ColorF faint) => new BoxEl
    {
        Grow = 1f, Shrink = 1f, Height = 16f, MinWidth = 0f,
        Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center,
        Padding = new Edges4(Spacing.XS, 0f, Spacing.XS, 0f), ClipToBounds = true,
        Corners = Radii.ControlAll, Fill = faint,
        Children =
        [
            new BoxEl
            {
                Width = Spacing.XL,
                Height = Spacing.XXS,
                Corners = Radii.PillAll,
                Fill = Tok.AccentDefault with { A = 0.58f },
            },
        ],
    };

    static Element GridCell(ColorF block, ColorF faint) => SidebarMiniature.GridCell(block, faint);

    // ── metrics ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>What this picker owns: the MINIATURE's proportions and its type ramp. The card's own footprint (width,
    /// resting inset, child gap) is <see cref="WaveePicker.Shell"/>'s — shared with the Settings wireframe pickers, so a
    /// change to the selected-border mechanic lands in one place.</summary>
    readonly record struct Metrics(bool Compact, WaveePicker.Shell Shell, float PreviewH, float Gap, float RowH,
                                   float TitleSize, float SubSize, float TagSize)
    {
        public static Metrics For(bool compact) => compact
            ? new Metrics(true, WaveePicker.PaneCompact, 96f, 3f, 10f, 12f, 10.5f, 9f)
            : new Metrics(false, WaveePicker.Pane, 116f, 3f, 11f, 13f, 11f, 9.5f);
    }

}

/// <summary>The one-time chooser's card (§C6.2). A Component because it owns the confirm→"Customize now" follow-up
/// phase and adapts its plate width to the live viewport — a fresh install can be running in a 300-DIP window, and a
/// fixed 744-DIP plate would hang off the edge of it.
///
/// <para>It deliberately does NOT write the seen-marker itself: <c>SidebarDesignPicker.Open</c> hangs that on the
/// handle's ClosedAction, so the marker cannot be forgotten on a path this class does not know about.</para></summary>
sealed class SidebarChooserCard : Component
{
    const float Pad = 24f;
    const float CardGap = 12f;

    readonly SidebarPreferences _prefs;
    readonly IAppSettings _settings;
    readonly Action<string, string?>? _go;
    readonly Func<OverlayHandle?> _handle;
    readonly Signal<bool> _followUp = new(false);

    public SidebarChooserCard(SidebarPreferences prefs, IAppSettings settings, Action<string, string?>? go,
                              Func<OverlayHandle?> handle)
    {
        _prefs = prefs; _settings = settings; _go = go; _handle = handle;
    }

    public override Element Render()
    {
        var viewport = UseContextSignal(Viewport.Size);
        float vw = viewport.Value.Width;

        // Three full-size cards + their gaps + the plate padding, sized OFF the shared shells rather than off restated
        // literals — a card that grows must move this plate with it. When the window cannot hold three full cards, fall
        // back to the compact card; the row wraps below that again, so the dialog degrades to two columns and then one.
        static float RowWidth(in WaveePicker.Shell s) => 3f * s.Width + 2f * CardGap;
        bool compact = vw > 0f && vw < RowWidth(WaveePicker.Pane) + 2f * Pad + 32f;
        float want = RowWidth(compact ? WaveePicker.PaneCompact : WaveePicker.Pane) + 2f * Pad;
        float plateW = vw > 0f ? Math.Clamp(want, 300f, Math.Max(300f, vw - 32f)) : want;

        bool follow = _followUp.Value;

        var head = new BoxEl
        {
            Direction = 1, Gap = CardGap, Padding = Edges4.All(Pad), Fill = Tok.FillLayerAlt,
            Children =
            [
                new TextEl(Loc.Get(Strings.Sidebar.Chooser.Title))
                {
                    Size = 20f, Weight = 600, Color = Tok.TextPrimary,
                    Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.WordEllipsis,
                },
                new TextEl(Loc.Get(Strings.Sidebar.Chooser.Subtitle))
                {
                    Size = 14f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap,
                },
                // The picker applies LIVE: the pane behind the scrim visibly changes on every card click, which is the
                // whole point of choosing here rather than in a static illustration.
                SidebarDesignPicker.Row(_prefs, _settings, compact),
            ],
        };

        return new BoxEl
        {
            Direction = 1,
            Width = plateW, MaxWidth = plateW, MinHeight = 184f,
            Corners = Radii.OverlayAll,
            Fill = Tok.FillSolidBase,
            BorderWidth = 1f, BorderColor = Tok.StrokeSurfaceDefault,
            Shadow = Elevation.Dialog,
            ClipToBounds = true,
            Children =
            [
                head,
                new BoxEl { Height = 1f, AlignSelf = FlexAlign.Stretch, Fill = Tok.StrokeCardDefault },
                follow ? FollowUpRow() : CommandRow(),
            ],
        };
    }

    /// <summary>"Not now" · "Use this layout". Both keep whatever design is currently applied — the cards already
    /// applied it — so neither writes a design; the difference is only whether the Curated follow-up is offered.</summary>
    Element CommandRow() => Commands(
        null,
        Button.Standard(Loc.Get(Strings.Sidebar.Chooser.Keep), Close),
        Button.Accent(Loc.Get(Strings.Sidebar.Chooser.Confirm), Confirm));

    /// <summary>Confirming Wavee Curated replaces the command row in place (§C6.2) — a second dialog for "want to
    /// customize it?" would be a modal on a modal.</summary>
    Element FollowUpRow() => Commands(
        new TextEl(Loc.Get(Strings.Settings.Sidebar.CustomizeSub))
        {
            Size = 12f, Color = Tok.TextSecondary, Grow = 1f, Shrink = 1f,
            Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.WordEllipsis,
        },
        Button.Standard(Loc.Get(Strings.Sidebar.Chooser.Later), Close),
        Button.Accent(Loc.Get(Strings.Sidebar.Chooser.CustomizeNow), CustomizeNow));

    /// <summary>The command space: an optional leading caption, then secondary + primary. TabIndex ranks the accent
    /// button FIRST so the focus trap's initial focus lands on it (the ContentDialog contract — a modal that opens with
    /// nothing focused is a keyboard dead end).</summary>
    static Element Commands(Element? lead, BoxEl secondary, BoxEl primary) => new BoxEl
    {
        Direction = 0, Gap = 8f, Padding = Edges4.All(Pad), Fill = Tok.FillSolidBase,
        AlignItems = FlexAlign.Center, Wrap = true,
        Children =
        [
            lead ?? new BoxEl { Grow = 1f, HitTestVisible = false },
            secondary with { MinWidth = 120f, Height = 32f, MinHeight = 32f, Justify = FlexJustify.Center, TabIndex = 2 },
            primary with { MinWidth = 130f, Height = 32f, MinHeight = 32f, Justify = FlexJustify.Center, TabIndex = 1 },
        ],
    };

    /// <summary>"Use this layout". Curated gets the customize offer inside the same overlay; Classic and Library close
    /// immediately — there is nothing further to ask them.</summary>
    void Confirm()
    {
        if (SidebarDesignGating.OffersCustomize(_prefs.Design.Peek())) { _followUp.Value = true; return; }
        Close();
    }

    void CustomizeNow()
    {
        Close();
        _go?.Invoke(SidebarLayoutMenu.CustomizeRoute, null);
    }

    /// <summary>Close. The marker is written by the handle's ClosedAction (see <c>SidebarDesignPicker.Open</c>) — every
    /// path, including the ones this class never sees.</summary>
    void Close() => _handle()?.Close();
}
