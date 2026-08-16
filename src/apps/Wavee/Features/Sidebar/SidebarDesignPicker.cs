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
//  2. NO LIVE SIDEBARS IN THE PREVIEW. Each card's miniature is static BoxEl/TextEl geometry at 7-px type, never a
//     mounted mode component — three live sidebars in a dialog is not worth the frame cost, and two of them would be
//     mounted against state the user has not chosen. Real cached content (library row names, pin names) fills the name
//     slots when it is already warm; when it is not, the slots are neutral bars. Fabricated titles are never shown.
//  3. NO IMAGE DECODES. The miniature's covers are solid tiles, not Images: at 10-16 DIP a real cover decode buys
//     nothing legible and would put three N-cover working sets behind a dialog the user sees once.
sealed class SidebarDesignPicker : Component
{
    readonly Func<int> _selected;
    readonly Action<int> _onChange;
    readonly bool _compact;

    /// <param name="selected">The live selection, read on every render (0 Classic · 1 Library · 2 Wavee Curated — the
    /// persisted <c>WaveeSettings.SidebarDesign</c> numbering, via <see cref="SidebarDesignGating.IndexOf"/>).</param>
    /// <param name="onChange">Applied IMMEDIATELY on click — no confirmation, no restart (§C6.1).</param>
    /// <param name="compact">The 200×168 card (Settings, where the picker shares a page column) instead of 224×196.</param>
    public SidebarDesignPicker(Func<int> selected, Action<int> onChange, bool compact = false)
    {
        _selected = selected; _onChange = onChange; _compact = compact;
    }

    // ── hosts ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The card row bound to the preference service: the ONE apply path (<c>SwitchDesign</c>) and the ONE
    /// selection source (<c>Design</c>). <paramref name="settings"/> is the fallback writer for the isolated-mount case
    /// (a settings page hosted without <c>SidebarPreferences</c> — the picker still functions and still persists).</summary>
    public static Element Row(SidebarPreferences? prefs, IAppSettings? settings, bool compact = false)
        => Embed.Comp(() => new SidebarDesignPicker(
            () => prefs is not null
                ? SidebarDesignGating.IndexOf(prefs.Design.Value)
                : SidebarDesignGating.IndexOf(SidebarDesignGating.ActiveDesign(settings)),
            value => Apply(prefs, settings, value),
            compact))
            // KEYED BY THE ONE FROZEN PROP. `prefs`/`settings` are reference-stable for the process, but `compact` is
            // computed from the live viewport by the chooser — and a reused ComponentEl never re-runs its factory, so a
            // resize across the threshold would otherwise keep rendering the old card size forever. A changed key is a
            // remount, which is exactly the semantics wanted here (the cards carry no state to lose).
            with { Key = compact ? "sidebar.design.picker.compact" : "sidebar.design.picker" };

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
        var prefs = UseContext(SidebarPreferences.Slot);
        var store = UseContext(LibraryStore.Slot);

        // Idempotent and already warmed by LibraryStore.WarmCheap in every real composition — this only covers a host
        // that mounted the settings page before the warm ran. In an effect, never in the render body: Ensure* starts a
        // task and completes into a signal write.
        UseEffect(() => { store?.EnsurePlaylists(); }, DepKey.Empty);

        int sel = _selected();
        var content = PreviewContent.Gather(prefs, store);
        var m = Metrics.For(_compact);

        // ONE radio group, not three independent tab stops — which is what §C6.1's "group semantics are an engine
        // follow-up" note was waiting for. WaveePicker.Strip delegates to FluentGpu.Controls.RadioButtons: a single tab
        // stop that lands on the active design, arrow-key roving between the cards, selection following focus, and the
        // wrap the row needs when three cards do not fit. The apply path is unchanged — Strip's onChange is _onChange,
        // so every selection still goes through SwitchDesign.
        return WaveePicker.Strip(3, sel, (i, on) => Card(SidebarDesignGating.FromIndex(i), on, in m, in content), _onChange);
    }

    Element Card(SidebarDesign design, bool on, in Metrics m, in PreviewContent content)
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
            Fill = on ? Tok.AccentDefault with { A = 0.08f } : Tok.FillSubtleTertiary with { A = 0.45f },
            Children = design switch
            {
                SidebarDesign.LibraryV3 => LibraryPreview(in m, in content, ink.Block, ink.Faint),
                SidebarDesign.Curated => CuratedPreview(in m, in content, ink.Block, ink.Faint),
                _ => ClassicPreview(in m, in content, ink.Block, ink.Faint),
            },
        };

        var title = WaveePicker.Label(Loc.Get(SidebarDesignGating.TitleKey(design)), on, m.TitleSize);
        Element titleRow = new BoxEl
        {
            Direction = 0, Gap = 6f, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Stretch,
            // A11y honesty (§C6.1): the selected card is distinguishable by the "Active" tag as well as by colour, so
            // the choice survives a colour-blind read.
            Children = on ? [title with { Shrink = 1f }, ActiveTag(m)] : [title],
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
    static Element ActiveTag(in Metrics m) => new BoxEl
    {
        Height = 16f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(6f, 0f, 6f, 0f),
        Corners = Radii.PillAll, Fill = Tok.AccentDefault,
        Children = [new TextEl(Loc.Get(Strings.Sidebar.Design.Active))
            { Size = m.TagSize, Weight = 600, Color = Tok.TextOnAccentPrimary, MaxLines = 1 }],
    };

    // ── the three miniatures ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Classic: the Library icon shortcuts, a divider, then the flat playlist list (§C6.1).</summary>
    static Element[] ClassicPreview(in Metrics m, in PreviewContent content, ColorF block, ColorF faint)
    {
        int icons = m.Compact ? 4 : 5;
        int arts = m.Compact ? 2 : 3;
        var kids = new List<Element>(icons + arts + 1);
        for (int i = 0; i < icons; i++) kids.Add(IconRow(IconBarW(i), block, faint));
        kids.Add(Hairline(faint));
        for (int i = 0; i < arts; i++) kids.Add(ArtRow(content.Name(i), m, block, faint));
        return kids.ToArray();
    }

    /// <summary>Library V3: the filter chip strip, the sort pill, then the unified list (§C6.1).</summary>
    static Element[] LibraryPreview(in Metrics m, in PreviewContent content, ColorF block, ColorF faint)
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
        for (int i = 0; i < arts; i++) kids.Add(ArtRow(content.Name(i), m, block, faint));
        return kids.ToArray();
    }

    /// <summary>Wavee Curated: two pin tiles, a divider, the 2-up "Jump back in" grid, the app-route links, then a
    /// library section (§C6.1). The pin tiles carry the user's REAL first two pins when they have any.</summary>
    static Element[] CuratedPreview(in Metrics m, in PreviewContent content, ColorF block, ColorF faint)
    {
        int icons = m.Compact ? 2 : 3;
        int arts = m.Compact ? 1 : 2;
        var kids = new List<Element>(icons + arts + 3)
        {
            new BoxEl
            {
                Direction = 0, Gap = 5f, Shrink = 0f,
                Children = [PinTile(content.Pin(0), m, faint), PinTile(content.Pin(1), m, faint)],
            },
            Hairline(faint),
            new BoxEl
            {
                Direction = 0, Gap = 5f, Shrink = 0f,
                Children = [GridCell(block, faint), GridCell(block, faint)],
            },
        };
        for (int i = 0; i < icons; i++) kids.Add(IconRow(IconBarW(i), block, faint));
        for (int i = 0; i < arts; i++) kids.Add(ArtRow(content.Name(i), m, block, faint));
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

    /// <summary>One list row: a cover tile plus the entity's REAL name at 7 px — or a neutral bar when nothing is
    /// cached. Never a fabricated title.</summary>
    static Element ArtRow(string name, in Metrics m, ColorF block, ColorF faint)
    {
        float h = m.RowH;
        Element label = name.Length > 0
            ? new TextEl(name)
            {
                Size = m.MicroSize, Color = Tok.TextSecondary, Grow = 1f, Shrink = 1f,
                MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            }
            : Bar(m.Compact ? 52f : 62f, 4f, faint);

        return new BoxEl
        {
            Direction = 0, Height = h, Shrink = 0f, Gap = 5f, AlignItems = FlexAlign.Center,
            Children =
            [
                new BoxEl { Width = h, Height = h, Shrink = 0f, Corners = CornerRadius4.All(2.5f), Fill = block },
                label,
            ],
        };
    }

    /// <summary>A Pinned tile carrying the user's REAL pin name when they have one. Deliberately the FAINT fill rather
    /// than the solid block: a pin name has to be legible on it in both themes and in both selected states, and
    /// text-on-accent only works over the solid accent.</summary>
    static Element PinTile(string name, in Metrics m, ColorF faint) => new BoxEl
    {
        Grow = 1f, Shrink = 1f, Height = 16f, MinWidth = 0f,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Start,
        Padding = new Edges4(4f, 0f, 4f, 0f), ClipToBounds = true,
        Corners = CornerRadius4.All(4f), Fill = faint,
        Children = name.Length > 0
            ? [new TextEl(name) { Size = m.MicroSize, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }]
            : [],
    };

    static Element GridCell(ColorF block, ColorF faint) => SidebarMiniature.GridCell(block, faint);

    // ── metrics + real content ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>What this picker owns: the MINIATURE's proportions and its type ramp. The card's own footprint (width,
    /// resting inset, child gap) is <see cref="WaveePicker.Shell"/>'s — shared with the Settings wireframe pickers, so a
    /// change to the selected-border mechanic lands in one place.</summary>
    readonly record struct Metrics(bool Compact, WaveePicker.Shell Shell, float PreviewH, float Gap, float RowH,
                                   float TitleSize, float SubSize, float MicroSize, float TagSize)
    {
        public static Metrics For(bool compact) => compact
            ? new Metrics(true, WaveePicker.PaneCompact, 96f, 3f, 10f, 12f, 10.5f, 6.5f, 9f)
            : new Metrics(false, WaveePicker.Pane, 116f, 3f, 11f, 13f, 11f, 7f, 9.5f);
    }

    /// <summary>The real cached content the miniatures fill their name slots from, resolved ONCE per render. Preference
    /// order: the sidebar's own projected entries (already the exact rows the real pane shows), then the library's warm
    /// playlist cell, then nothing — in which case the slots render as neutral bars (never invented titles).</summary>
    readonly struct PreviewContent
    {
        readonly string[] _names;
        readonly string[] _pins;

        PreviewContent(string[] names, string[] pins) { _names = names; _pins = pins; }

        // "" for an unfilled slot AND for a default(PreviewContent) — the shape callers branch on ("has a real name?"),
        // so it must never be null and never throw on an out-of-range slot.
        public string Name(int i) => _names is { } a && (uint)i < (uint)a.Length ? a[i] : "";
        public string Pin(int i) => _pins is { } a && (uint)i < (uint)a.Length ? a[i] : "";

        public static PreviewContent Gather(SidebarPreferences? prefs, LibraryStore? store)
        {
            var names = new string[4];
            var pins = new string[2];
            Array.Fill(names, "");
            Array.Fill(pins, "");

            if (prefs is not null)
            {
                _ = prefs.Entries.Version.Value;   // subscribe: a projection rebuild refreshes the miniatures
                _ = prefs.PinsVersion.Value;

                var entries = prefs.Entries.Current;
                int n = 0;
                for (int i = 0; i < entries.Count && n < names.Length; i++)
                {
                    var e = entries[i];
                    // Routes/folders/tracks are not "library rows" in the miniature's sense (a route name is chrome, a
                    // folder has no cover, a track is not pinnable) — the art rows want entities.
                    if (e.Kind is SidebarEntryKind.AppRoute or SidebarEntryKind.Folder or SidebarEntryKind.Track) continue;
                    if (e.Name.Length == 0) continue;
                    names[n++] = e.Name;
                }

                var pinStore = prefs.Pins;
                for (int i = 0, p = 0; i < pinStore.Count && p < pins.Length; i++)
                    if (pinStore[i].Name is { Length: > 0 } pinName) pins[p++] = pinName;
            }

            if (names[0].Length == 0 && store is not null)
            {
                _ = store.Playlists.State.Value;   // subscribe to the load edge, not just the value
                var list = store.Playlists.Value.Value;
                for (int i = 0, n = 0; i < list.Count && n < names.Length; i++)
                    if (list[i].Name is { Length: > 0 } plName) names[n++] = plName;
            }

            return new PreviewContent(names, pins);
        }
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
