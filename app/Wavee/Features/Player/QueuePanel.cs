using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The right-rail QUEUE panel — ground-up rework on the WaveeMusic shape (C:\WAVEE\WaveeMusic QueueControl), which the
// old anchor-list design never got right:
//
//   • The CURRENT track is a pinned, bordered NOW-PLAYING CARD above the sections — it is NEVER a row inside a list,
//     so there is no anchor index, no scroll pinning, no bucket-shift bookkeeping, and no way for it to render as a
//     dimmed history row.
//   • NO history section. The queue is forward-looking only: "Next in queue" (user queue + Clear) → "Next up"
//     (context, with the "Playing from …" breadcrumb above the card) → "Autoplay" (dimmed tail, when enabled).
//   • Sections are PLAIN KEYED ROWS inside one ScrollEl — no virtualization, no bound-slot recycling. A queue is at
//     most a few hundred cheap rows; realizing them keyed means the reconciler's Enter/Exit/FLIP transitions animate
//     every change natively: the consumed row exits, the rest slide up, the card cross-fades to the new track. The
//     old index-bound recycled list could never express motion (slots rebind in place) and its recycled subtrees were
//     the source of the frozen-bind/wrong-track corruption.
sealed class QueuePanel : Component
{
    const float QueueArt = 34f;
    const int PageSize = 100;   // rows REALIZED per visual page — the underlying queue is never truncated; the section
                                // paginates visually with an explicit "Show more (N left)" affordance per page.

    // Visual page counts per section (instance-lived; reset when the playback context changes).
    readonly Signal<int> _queuePages = new(1);
    readonly Signal<int> _upPages = new(1);
    readonly Signal<int> _autoPages = new(1);
    readonly SwipeGroup _swipeGroup = new();

    public override Element Render()
    {
        var b = UseContext(PlaybackBridge.Slot);
        var lib = UseContext(LibraryBridge.Slot);
        var svc = UseContext(Services.Slot);
        var go = UseContext(HistoryStore.NavCtx);
        var acts = UseContext(ActionServices.Slot);     // queue-row context menus (Menus.QueueEntry)
        var menuOverlay = UseContext(Overlay.Service);

        var serverQueue = b?.Queue.Value ?? Array.Empty<QueueEntry>();
        var display = UseSignal<IReadOnlyList<QueueEntry>>(serverQueue);
        UseSignalEffect(() =>
        {
            if (b is null) return;
            display.Value = b.Queue.Value;
        });
        var (autoplay, setAutoplay) = UseState(svc?.Settings.Get(WaveeSettings.AutoplayEnabled) ?? true);
        int prefsEpoch = PlaybackPrefs.Epoch.Value;
        UseEffect(() => setAutoplay(svc?.Settings.Get(WaveeSettings.AutoplayEnabled) ?? true), prefsEpoch);

        string ctxUri = b?.CurrentContext.Value ?? "";
        var ctxName = UseAsyncResource(ct => ResolveContextNameAsync(svc, ctxUri, ct), (string?)null, ctxUri);
        var uiLogSig = UseRef<string?>(null);
        // New context ⇒ collapse the visual pagination back to the first page of each section.
        UseEffect(() => { _queuePages.Value = 1; _upPages.Value = 1; _autoPages.Value = 1; }, ctxUri);

        if (b is null) return new BoxEl();

        void ToggleAutoplay()
        {
            if (svc is null) return;
            svc.Settings.Set(WaveeSettings.AutoplayEnabled, !autoplay);
            PlaybackPrefs.Bump();
        }

        var track = b.CurrentTrack.Value;
        var accent = b.TrackPalette.Value is { } p ? WaveePalette.Accent(p) : Tok.AccentDefault;

        // ── bucket split: forward-looking only (History and the NowPlaying entry are NOT rows here) ──
        var queue = display.Value;
        var userQueue = new List<QueueEntry>();
        var ctxUp = new List<QueueEntry>();
        var autoUp = new List<QueueEntry>();
        string? curUri = track?.Uri;
        foreach (var e in queue)
        {
            if (curUri is { Length: > 0 } && e.Track.Uri == curUri) continue;   // never show the current track as a row
            switch (e.Bucket)
            {
                case QueueBucket.UserQueue: userQueue.Add(e); break;
                case QueueBucket.NextUp: (e.Provider == QueueProvider.Autoplay ? autoUp : ctxUp).Add(e); break;
            }
        }

        // Copy-paste diagnostics: what the panel actually shows. Diff against queue.snapshot / bridge.ui.push-state.
        var sigRef = uiLogSig.Value;
        Backend.PlaybackBucketDiagnostics.UiIfChanged(ref sigRef, "queue.panel.rows",
            PanelDump(curUri, userQueue, ctxUp, autoUp, autoplay));
        uiLogSig.Value = sigRef;

        bool viewer = PlayerBarContent.RemoteDevice(b) is not null;
        string? source = ctxName.Value.Value is { Length: > 0 } rn ? rn : ImmediateContextName(ctxUri);
        string? sourceHref = source is { Length: > 0 } ? RichText.RouteForUri(ctxUri) : null;

        var content = new List<Element>(8);
        if (source is { Length: > 0 })
            content.Add(PlayingFrom(source, sourceHref, go));
        if (track is { } t)
            content.Add(NowPlayingCard(b, lib, t, go));
        if (userQueue.Count > 0)
        {
            content.Add(SectionHeader(Loc.Get(Strings.Player.NextInQueue), userQueue.Count,
                viewer ? null : () => ClearUserQueue(b, display)));
            content.Add(Rows("q", userQueue, b, lib, go, display, removable: !viewer, dim: false, _queuePages, acts, menuOverlay, _swipeGroup));
        }
        if (ctxUp.Count > 0)
        {
            content.Add(SectionHeader(Loc.Get(Strings.Player.NextUp), ctxUp.Count, null));
            content.Add(Rows("u", ctxUp, b, lib, go, display, removable: !viewer, dim: false, _upPages, acts, menuOverlay, _swipeGroup));
        }
        if (autoplay && autoUp.Count > 0)
        {
            content.Add(SectionHeader(Loc.Get(Strings.Player.Autoplay), autoUp.Count, null, sub: Loc.Get(Strings.Player.AutoplayHint)));
            content.Add(Rows("a", autoUp, b, lib, go, display, removable: false, dim: true, _autoPages, acts, menuOverlay, _swipeGroup));
        }
        if (track is null && userQueue.Count == 0 && ctxUp.Count == 0)
            content.Add(new BoxEl
            {
                Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Padding = Edges4.All(22f),
                Children = [new TextEl(Loc.Get(Strings.Player.NothingPlaying)) { Size = 13f, Color = Tok.TextSecondary }],
            });

        return new BoxEl
        {
            Direction = 1, Grow = 1f, MinHeight = 0f, ClipToBounds = true,
            Padding = new Edges4(14f, 4f, 14f, 0f),
            Children =
            [
                Pills(b, accent, autoplay, ToggleAutoplay),
                new ScrollEl
                {
                    Grow = 1f, MinHeight = 0f,
                    AutoEdgeFade = true,
                    ScrollKey = "queuepanel",
                    OnScrollGeometryChanged = (g => _swipeGroup.AnyOpen ? BitConverter.SingleToInt32Bits(g.OffsetY) : 0L, _ => _swipeGroup.Close()),
                    Content = new BoxEl
                    {
                        Direction = 1, MinHeight = 0f,
                        Padding = new Edges4(0f, 0f, 0f, 14f),
                        Children = content.ToArray(),
                    },
                },
            ],
        };
    }

    // ── "Playing from {source}" breadcrumb — borderless single line above the card (WaveeMusic ContextCard). ──
    static Element PlayingFrom(string source, string? href, Action<string, string?>? go) => new BoxEl
    {
        Key = "qp:ctx",
        Direction = 0, AlignItems = FlexAlign.Center, Gap = 6f, MinHeight = 26f,
        Padding = new Edges4(6f, 2f, 6f, 6f),
        Corners = CornerRadius4.All(4f),
        HoverFill = href is { Length: > 0 } ? WaveeColors.RowHover : ColorF.Transparent,
        Cursor = href is { Length: > 0 } ? CursorId.Hand : CursorId.Arrow,
        OnClick = href is { Length: > 0 } && go is not null ? () => go(href, source) : null,
        Children =
        [
            new SpanTextEl(
            [
                new TextSpan(Strings.Player.PlayingFrom(""), Color: Tok.TextSecondary),
                new TextSpan(source, Color: Tok.TextPrimary, Weight: 700),
            ]) { Size = 12f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Grow = 1f, MinWidth = 0f },
            new TextEl(Icons.ChevronRightMed) { Size = 10f, FontFamily = Theme.IconFont, Color = Tok.TextSecondary },
        ],
    };

    // ── The pinned NOW-PLAYING CARD: a bordered card, keyed by track uri (a track change remounts it with an Enter
    // fade — the cross-fade the old anchor row could never do). The EQ overlay toggles play/pause. ──
    static Element NowPlayingCard(PlaybackBridge b, LibraryBridge? lib, Track t, Action<string, string?>? go)
    {
        var st = TrackRow.StateOf(b, lib, t);
        Action? like = t.Uri.Length > 0 && lib is not null ? () => lib.ToggleSaved(t.Uri, t.Title) : null;
        return new BoxEl
        {
            Key = "np:" + t.Uri,
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 12f, MinHeight = 64f,
            Margin = new Edges4(0f, 0f, 0f, 10f),
            Padding = Edges4.All(10f),
            Corners = CornerRadius4.All(WaveeRadius.Card),
            Fill = Tok.FillCardDefault,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Enter = new EnterExit(Dy: 6f, Opacity: 0f, Active: true),
            Layout = LayoutTransition.Slide,
            Children =
            [
                new BoxEl
                {
                    Width = 44f, Height = 44f, Shrink = 0f, ZStack = true, ClipToBounds = true,
                    Corners = CornerRadius4.All(5f),
                    Children =
                    [
                        Surfaces.Artwork(t.Image, t.Id.GetHashCode() & 0x7fffffff, 44f, 44f, 5f, decodePx: 96),
                        Embed.Comp(() => new NowPlayingOverlay(t.Uri, () => { }, 28f, cover: true, 44f, centered: true))
                            .Skeletonized(false),
                    ],
                },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center, Gap = 1f,
                    Children =
                    [
                        new TextEl(t.Title)
                        {
                            Size = 14f, Weight = 700, Color = Tok.TextPrimary,
                            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                        },
                        go is null
                            ? new TextEl(DetailFormat.ArtistNames(t.Artists))
                            { Size = 12f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }
                            : TrackRow.ArtistLinks(t.Artists, (r, n) => go(r, n)),
                    ],
                },
                new BoxEl
                {
                    Width = 30f, Height = 44f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [TrackRow.Heart(st.Saved, like)],
                },
            ],
        };
    }

    // ── Section header: caps title + optional count + optional Clear, optional hint sub-line. ──
    static Element SectionHeader(string title, int count, Action? clear, string? sub = null)
    {
        var top = new List<Element>(4)
        {
            new TextEl(title.ToUpperInvariant())
            {
                Size = 11f, Weight = 700, Color = Tok.TextTertiary, CharSpacing = 120f,
                MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
        };
        if (count >= 0) top.Add(new TextEl(count.ToString()) { Size = 11f, Weight = 600, Color = Tok.TextTertiary });
        top.Add(new BoxEl { Grow = 1f, MinWidth = 0f });
        if (clear is not null)
            top.Add(new BoxEl
            {
                Padding = new Edges4(6f, 2f, 6f, 2f), Corners = CornerRadius4.All(4f),
                HoverFill = WaveeColors.RowHover, PressedFill = WaveeColors.RowPressed,
                Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, OnClick = clear,
                Children = [new TextEl(Loc.Get(Strings.Player.Clear)) { Size = 11f, Weight = 600, Color = Tok.TextSecondary, HoverColor = Tok.TextPrimary }],
            });

        var kids = new List<Element>(2) { new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = 6f, Children = top.ToArray() } };
        if (sub is { Length: > 0 })
            kids.Add(new TextEl(sub) { Size = 11f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });

        return new BoxEl
        {
            Key = "hdr:" + title,
            Direction = 1, Gap = 1f,
            Padding = new Edges4(6f, 12f, 6f, 4f),
            Layout = LayoutTransition.Slide,
            Children = kids.ToArray(),
        };
    }

    // ── A section's keyed rows: plain children — the reconciler's keyed diff + Enter/Exit/FLIP does all the motion.
    // Visual pagination only: PageSize rows per revealed page, then an explicit full-width "Show more (N left)" row —
    // the UNDERLYING queue is untouched; every hidden track is one click away and the remaining count is always visible. ──
    static Element Rows(string sectionTag, List<QueueEntry> entries, PlaybackBridge b, LibraryBridge? lib,
        Action<string, string?>? go, Signal<IReadOnlyList<QueueEntry>> display, bool removable, bool dim, Signal<int> pages,
        ActionServices? acts = null, IOverlayService? menuOverlay = null, SwipeGroup? swipeGroup = null)
    {
        int n = Math.Min(entries.Count, Math.Max(1, pages.Value) * PageSize);
        var kids = new List<Element>(n + 1);
        for (int i = 0; i < n; i++)
            kids.Add(QueueRow(b, lib, go, display, entries[i], zebra: (i & 1) != 0, removable, dim, acts, menuOverlay, swipeGroup));
        if (entries.Count > n)
            kids.Add(ShowMore(sectionTag, Math.Min(PageSize, entries.Count - n), entries.Count - n,
                () => pages.Value = pages.Peek() + 1));
        return new BoxEl { Key = "sec:" + sectionTag, Direction = 1, Children = kids.ToArray() };
    }

    // Full-width load-more affordance: "⌄ Show next 100 · 63 more" — the pagination is explicit, never a silent cut.
    static Element ShowMore(string sectionTag, int nextPage, int remaining, Action more) => new BoxEl
    {
        Key = "more:" + sectionTag,
        Direction = 0, MinHeight = 38f, Gap = 7f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Margin = new Edges4(0f, 4f, 0f, 2f),
        Corners = CornerRadius4.All(6f),
        Fill = Tok.FillCardSecondary,
        HoverFill = WaveeColors.RowHover, PressedFill = WaveeColors.RowPressed,
        Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, OnClick = more,
        Layout = LayoutTransition.Slide,
        Children =
        [
            new TextEl(Icons.ChevronDown) { Size = 10f, FontFamily = Theme.IconFont, Color = Tok.TextSecondary },
            new TextEl($"Show next {nextPage}") { Size = 12f, Weight = 600, Color = Tok.TextPrimary },
            new TextEl($"·  {remaining} more") { Size = 12f, Color = Tok.TextTertiary },
        ],
    };

    static Element QueueRow(PlaybackBridge b, LibraryBridge? lib, Action<string, string?>? go,
        Signal<IReadOnlyList<QueueEntry>> display, QueueEntry entry, bool zebra, bool removable, bool dim,
        ActionServices? acts = null, IOverlayService? menuOverlay = null, SwipeGroup? swipeGroup = null)
    {
        var t = entry.Track;
        var st = TrackRow.StateOf(b, lib, t);
        Action? like = t.Uri.Length > 0 && lib is not null ? () => lib.ToggleSaved(t.Uri, t.Title) : null;

        void Remove()
        {
            _ = b.Player.RemoveQueueItemAsync(entry.ItemId);
            var cur = new List<QueueEntry>(display.Peek());
            cur.RemoveAll(x => entry.ItemId.IsNone ? x.EntryId == entry.EntryId : x.ItemId == entry.ItemId);
            display.Value = cur;
        }

        var row = new BoxEl
        {
            Key = RowKey(entry),
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 8f, MinHeight = 44f,
            Padding = new Edges4(6f, 0f, 4f, 0f),
            Corners = CornerRadius4.All(6f),
            Fill = zebra ? WaveeColors.RowZebra : ColorF.Transparent,
            HoverFill = zebra ? WaveeColors.RowHoverZebra : WaveeColors.RowHover,
            PressedFill = zebra ? WaveeColors.RowPressedZebra : WaveeColors.RowPressed,
            PressScale = 0.985f,
            Opacity = dim ? 0.72f : 1f,
            Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true,
            OnClick = () => PlayQueueEntry(b, entry),
            Enter = new EnterExit(Dy: 6f, Opacity: 0f, Active: true),
            Exit = new EnterExit(Dy: -4f, Opacity: 0f, Active: true),
            Layout = LayoutTransition.Slide,
            Children =
            [
                new BoxEl
                {
                    Width = 26f, Height = 44f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [TrackRow.Heart(st.Saved, like)],
                },
                new BoxEl
                {
                    Width = QueueArt, Height = QueueArt, Shrink = 0f, ClipToBounds = true,
                    Corners = CornerRadius4.All(5f),
                    Children = [Surfaces.Artwork(t.Image, t.Id.GetHashCode() & 0x7fffffff, QueueArt, QueueArt, 5f, decodePx: 72)],
                },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center,
                    Children =
                    [
                        new TextEl(t.Title)
                        {
                            Size = 13.5f, Weight = 600, Color = Tok.TextPrimary,
                            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                        },
                        go is null
                            ? new TextEl(DetailFormat.ArtistNames(t.Artists))
                            { Size = 12f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }
                            : TrackRow.ArtistLinks(t.Artists, (r, n) => go(r, n)),
                    ],
                },
                // Hover-revealed "…" overflow beside the ✕ (kept): opens the SAME queue-entry menu the row shows on
                // right-click, anchored at the button — the engine's ClickRequestsContext re-enters the context-request
                // funnel here and the walk finds the row's OnContextRequested (the WithContextMenu attach below). Only
                // rendered when a menu is actually attachable; sized to the queue's 26px action density.
                acts is not null && menuOverlay is not null
                    ? new BoxEl
                    {
                        Opacity = 0f, HoverOpacity = 1f, Shrink = 0f,
                        Children =
                        [
                            new BoxEl
                            {
                                Width = 26f, Height = 26f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                                Corners = CornerRadius4.All(13f),
                                HoverFill = WaveeColors.RowPressed,
                                Role = AutomationRole.Button, Cursor = CursorId.Hand,
                                ClickRequestsContext = true,
                                Children = [new TextEl(Mdl.More) { Size = 14f, FontFamily = Theme.IconFont, Color = Tok.TextTertiary, HoverColor = Tok.TextPrimary }],
                            },
                        ],
                    }
                    : new BoxEl { Width = 0f, Shrink = 0f },
                removable && !entry.ItemId.IsNone
                    ? new BoxEl
                    {
                        Width = 26f, Height = 26f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        Corners = CornerRadius4.All(13f),
                        HoverFill = WaveeColors.RowPressed,
                        Role = AutomationRole.Button, Cursor = CursorId.Hand,
                        OnClick = Remove,
                        Children = [new TextEl(Icons.ChromeClose) { Size = 9f, FontFamily = Theme.IconFont, Color = Tok.TextTertiary, HoverColor = Tok.TextPrimary }],
                    }
                    : new BoxEl { Width = 26f, Shrink = 0f },
            ],
        };
        bool canRemove = removable && !entry.ItemId.IsNone;
        // Right-click / long-press: the queue-entry menu. "Play now" = the row's own skip-in-place; "Remove from
        // queue" reuses the exact Remove() closure above (player call + optimistic display update) — a viewer (remote
        // device) gets it disabled, mirroring the hidden inline ✕.
        Element rowEl = row;
        if (acts is { } a && menuOverlay is { } menuSvc)
            rowEl = row.WithContextMenu(menuSvc, () => Menus.QueueEntry(
                a, entry, canRemove ? (Action)Remove : null, () => PlayQueueEntry(b, entry)));
        // Touch swipe-to-action (Phase D): swipe LEFT to remove (destructive red, reusing the Remove() closure via the
        // action target), swipe RIGHT to like. Eager KEYED rows ⇒ no resetKey (each entry mounts its own control; a
        // queue edit remounts by RowKey). The context menu is attached to the row BENEATH the wrapper, so the touch
        // long-press still finds the row's ContextBit ancestor.
        if (acts is { } sa)
        {
            var ctx = new ActionContext(ActionTarget.ForQueueEntry(entry, canRemove ? (Action)Remove : null), sa);
            rowEl = RowSwipe.Wrap(rowEl, ctx,
                group: swipeGroup,
                leading: TrackActions.ToggleLike,
                trailing: canRemove ? TrackActions.RemoveFromQueue : null);
        }
        return rowEl;
    }

    static string RowKey(in QueueEntry e) => e.ItemId.IsNone ? "e" + e.EntryId : "i" + e.ItemId.Value;

    static void ClearUserQueue(PlaybackBridge b, Signal<IReadOnlyList<QueueEntry>> display)
    {
        _ = b.Player.ClearQueueAsync();
        display.Value = display.Peek().Where(x => x.Bucket != QueueBucket.UserQueue).ToList();
    }

    static Element Pills(PlaybackBridge b, ColorF accent, bool autoplayOn, Action toggleAutoplay) => new BoxEl
    {
        Direction = 0, Gap = 8f, AlignItems = FlexAlign.Center,
        Padding = new Edges4(0f, 4f, 0f, 10f),
        Children =
        [
            SegmentPill(Loc.Get(Strings.Player.Shuffle), Icons.Shuffle, b.IsShuffle.Value,
                () => PlayerBarContent.ToggleShuffle(b), accent),
            SegmentPill(Loc.Get(Strings.Player.Repeat), b.Repeat.Value == RepeatMode.Track ? Icons.RepeatOne : Icons.RepeatAll,
                b.Repeat.Value != RepeatMode.Off, () => PlayerBarContent.CycleRepeat(b), accent),
            AutoplayPill(autoplayOn, toggleAutoplay, accent),
        ],
    };

    static Element AutoplayPill(bool on, Action click, ColorF accent) => new BoxEl
    {
        Direction = 0, Height = 32f, Gap = 6f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(12f, 0f, 12f, 0f),
        Corners = CornerRadius4.All(16f),
        Fill = on ? accent : Tok.FillCardSecondary,
        HoverFill = on ? accent with { A = 0.88f } : Tok.FillSubtleSecondary,
        PressedFill = on ? accent with { A = 0.78f } : Tok.FillSubtleTertiary,
        Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, OnClick = click,
        Children =
        [
            new TextEl("∞") { Size = 15f, Weight = 800, Color = on ? Tok.TextOnAccentPrimary : Tok.TextSecondary },
            new TextEl(Loc.Get(Strings.Player.Autoplay)) { Size = 12f, Weight = 600, Color = on ? Tok.TextOnAccentPrimary : Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
        ],
    };

    static Element SegmentPill(string label, string glyph, bool on, Action click, ColorF accent) => new BoxEl
    {
        Direction = 0, Height = 32f, Gap = 6f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(12f, 0f, 12f, 0f),
        Corners = CornerRadius4.All(16f),
        Fill = on ? accent : Tok.FillCardSecondary,
        HoverFill = on ? accent with { A = 0.88f } : Tok.FillSubtleSecondary,
        PressedFill = on ? accent with { A = 0.78f } : Tok.FillSubtleTertiary,
        Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, OnClick = click,
        Children =
        [
            new TextEl(glyph) { Size = 13f, FontFamily = Theme.IconFont, Color = on ? Tok.TextOnAccentPrimary : Tok.TextSecondary },
            new TextEl(label) { Size = 12f, Weight = 600, Color = on ? Tok.TextOnAccentPrimary : Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
        ],
    };

    static string? ImmediateContextName(string uri)
    {
        if (uri.Length == 0) return null;
        if (uri.Contains(":collection", StringComparison.Ordinal)) return Loc.Get(Strings.Player.LikedSongs);
        return null;
    }

    static async Task<string?> ResolveContextNameAsync(Services? svc, string uri, CancellationToken ct)
    {
        if (svc is null || uri.Length == 0) return null;
        try
        {
            if (uri.Contains(":collection", StringComparison.Ordinal)) return Loc.Get(Strings.Player.LikedSongs);
            if (uri.Contains(":playlist:", StringComparison.Ordinal)) return (await svc.Library.GetPlaylistAsync(uri, ct).ConfigureAwait(false))?.Name;
            if (uri.Contains(":album:", StringComparison.Ordinal)) return (await svc.Library.GetAlbumAsync(uri, ct).ConfigureAwait(false))?.Name;
            if (uri.Contains(":artist:", StringComparison.Ordinal)) return (await svc.Library.GetArtistAsync(uri, ct).ConfigureAwait(false))?.Name;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }
        return null;
    }

    // Skip-in-place to the clicked row within the live session by its stable id (F1): a cursor move, never a rebuild.
    // The PlayTrackAsync fallback fires only when the row carries no stable id (ItemId.IsNone — a degenerate snapshot).
    static void PlayQueueEntry(PlaybackBridge b, QueueEntry entry)
    {
        if (entry.ItemId.IsNone)
        {
            TrackRow.Invoke(b, entry.Track, () => b.Player.PlayTrackAsync(entry.Track));
            return;
        }
        TrackRow.Invoke(b, entry.Track, () => b.Player.SkipToQueueItemAsync(entry.ItemId));
    }

    // The panel-side truth dump (queue.panel.rows): what the panel actually shows, per section, with row keys —
    // diff against queue.snapshot / bridge.ui.push-state to split bad DATA from bad RENDERING at a glance.
    static string PanelDump(string? currentUri, List<QueueEntry> userQueue, List<QueueEntry> ctxUp,
        List<QueueEntry> autoUp, bool autoplayOn)
    {
        var sb = new System.Text.StringBuilder(96 + (userQueue.Count + ctxUp.Count + autoUp.Count) * 40);
        sb.Append("card=").Append(currentUri ?? "-")
          .Append(" queue=").Append(userQueue.Count)
          .Append(" nextUp=").Append(ctxUp.Count)
          .Append(" autoplay=").Append(autoplayOn ? autoUp.Count.ToString() : "off")
          .Append(" rows=[");
        AppendSection(sb, "Q", userQueue);
        AppendSection(sb, "U", ctxUp);
        if (autoplayOn) AppendSection(sb, "A", autoUp);
        sb.Append(']');
        return sb.ToString();

        static void AppendSection(System.Text.StringBuilder sb, string tag, List<QueueEntry> list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (sb[^1] != '[') sb.Append("; ");
                sb.Append(tag).Append(i).Append(" key=").Append(RowKey(list[i]))
                  .Append(" \"").Append(list[i].Track.Title).Append('"');
            }
        }
    }
}

/// <summary>Cross-surface playback preference epoch (autoplay): bumped when <see cref="WaveeSettings.AutoplayEnabled"/>
/// changes from the Settings toggle, the queue-panel pill, or the autoplay footer, so every surface stays in sync.</summary>
static class PlaybackPrefs
{
    public static readonly Signal<int> Epoch = new(0);
    public static void Bump() => PlaybackPrefs.Epoch.Value = Epoch.Peek() + 1;
}
