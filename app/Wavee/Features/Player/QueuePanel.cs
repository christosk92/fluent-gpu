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

// The right-rail QUEUE panel (Apple-Music shape, §10.1): one scrolling bound-slot list holding — in order — History
// (ABOVE the anchor, revealed by scrolling UP), the Now-Playing anchor row, "Next in queue", "Next up", and the Autoplay
// section. The panel opens scrolled to the Now-Playing anchor (History off-screen above); every non-empty bucket renders
// with its own titled header, empty buckets render nothing. A tap plays immediately (no lingering selection); the list
// reconciles in place via stable row keys (QueueItemId / section hdr keys) — never a revision-driven remount (§10.2).
sealed class QueuePanel : Component
{
    const float RowExtent = 44f;
    const float QueueArt = 34f;

    // The Clear affordance a section header carries (none / drop the user queue / drop the local history).
    enum QueueClear : byte { None, Queue, History }

    readonly record struct QRow(
        QueueEntry? Entry = null,
        bool IsHistory = false,
        bool IsNowPlaying = false,
        int TrackIndex = -1,
        string? Header = null,
        string? Sub = null,
        string? SubHref = null,
        int Count = -1,
        QueueClear Clear = QueueClear.None,
        bool AutoplaySection = false)
    {
        // Now-playing is the anchor, never a skip target and never removable; every other track row (history/queue/upcoming) is.
        public bool CanRemove => Entry is not null && !IsNowPlaying;
    }

    // Instance-lived so it survives re-renders — StartBringItemIntoView (open-at-anchor / re-pin) + ScrollBy anchoring.
    readonly ItemsViewController _listCtl = new();

    public override Element Render()
    {
        var b = UseContext(PlaybackBridge.Slot);
        var lib = UseContext(LibraryBridge.Slot);
        var svc = UseContext(Services.Slot);

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

        var rowsSig = UseSignal<IReadOnlyList<QRow>>(Array.Empty<QRow>());
        var rowCountSig = UseSignal(0);
        var lastAnchor = UseRef(-1);
        var post = UsePost();

        UseSignalEffect(() =>
        {
            if (b is null) return;
            bool viewer = PlayerBarContent.RemoteDevice(b) is not null;
            var rows = BuildRows(display.Value, b.CurrentContext.Value, ctxName.Value.Value, autoplay, viewer, out int anchor);
            rowsSig.Value = rows;
            rowCountSig.Value = rows.Count;
            if (lastAnchor.Value != anchor)
            {
                lastAnchor.Value = anchor;
                int idx = anchor;
                post(() => _listCtl.StartBringItemIntoView(idx, alignmentRatio: 0f));
            }
        });

        if (b is null) return new BoxEl();

        var track = b.CurrentTrack.Value;
        var accent = b.TrackPalette.Value is { } p ? WaveePalette.Accent(p) : Tok.AccentDefault;

        if (track is null)
            return new BoxEl
            {
                Direction = 1, Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Padding = Edges4.All(22f),
                Children = [new TextEl(Loc.Get(Strings.Player.NothingPlaying)) { Size = 13f, Color = Tok.TextSecondary }],
            };

        var list = ItemsView.CreateBound(
            rowCountSig.Value,
            scope => Embed.Comp(() => new QueueRowSlot(scope, rowsSig, display, accent)),
            RepeatLayout.Stack(RowExtent),
            selectionMode: ItemsSelectionMode.None,
            isItemInvokedEnabled: true,
            itemInvoked: i =>
            {
                var rows = rowsSig.Peek();
                if ((uint)i >= (uint)rows.Count) return;
                var r = rows[i];
                if (r.Entry is { } entry && !r.IsNowPlaying) PlayQueueEntry(b, entry);
            },
            itemText: i =>
            {
                var rows = rowsSig.Peek();
                return (uint)i < (uint)rows.Count ? rows[i].Entry?.Track.Title ?? rows[i].Header ?? "" : "";
            },
            isItemEnabled: i =>
            {
                var rows = rowsSig.Peek();
                return (uint)i < (uint)rows.Count && rows[i].Entry is not null && !rows[i].IsNowPlaying;
            },
            controller: _listCtl,
            autoEdgeFade: true,
            grow: 1f,
            scrollKey: "queuepanel",
            itemCountSignal: rowCountSig);

        return new BoxEl
        {
            Direction = 1, Grow = 1f, MinHeight = 0f, ClipToBounds = true,
            Padding = new Edges4(14f, 4f, 14f, 14f),
            Children =
            [
                Pills(b, accent),
                new BoxEl { Grow = 1f, MinHeight = 0f, Children = [list] },
            ],
        };
    }

    static Element Pills(PlaybackBridge b, ColorF accent) => new BoxEl
    {
        Direction = 0, Gap = 8f, AlignItems = FlexAlign.Center,
        Padding = new Edges4(0f, 4f, 0f, 10f),
        Children =
        [
            SegmentPill(Loc.Get(Strings.Player.Shuffle), Icons.Shuffle, b.IsShuffle.Value,
                () => PlayerBarContent.ToggleShuffle(b), accent),
            SegmentPill(Loc.Get(Strings.Player.Repeat), b.Repeat.Value == RepeatMode.Track ? Icons.RepeatOne : Icons.RepeatAll,
                b.Repeat.Value != RepeatMode.Off, () => PlayerBarContent.CycleRepeat(b), accent),
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

    // §10.1 layout: History (above the anchor) → Now-Playing anchor → Next in queue → Next up → Autoplay (always). Buckets
    // split off QueueEntry.Provider/Bucket (no string heuristics); every non-empty bucket carries its own header, and the
    // Autoplay section header always renders (the toggle affordance) even with no rows. `anchorIndex` is the Now-Playing row.
    static List<QRow> BuildRows(IReadOnlyList<QueueEntry> queue, string ctxUri, string? resolvedName,
        bool autoplayOn, bool viewer, out int anchorIndex)
    {
        var rows = new List<QRow>(queue.Count + 6);
        int trackIndex = 0;

        QueueEntry? current = null;
        var history = new List<QueueEntry>();
        var userQueue = new List<QueueEntry>();
        var ctxUp = new List<QueueEntry>();
        var autoUp = new List<QueueEntry>();
        foreach (var e in queue)
            switch (e.Bucket)
            {
                case QueueBucket.NowPlaying: current = e; break;
                case QueueBucket.History: history.Add(e); break;
                case QueueBucket.UserQueue: userQueue.Add(e); break;
                case QueueBucket.NextUp: (e.Provider == QueueProvider.Autoplay ? autoUp : ctxUp).Add(e); break;
            }

        // 0 — History ABOVE the anchor: oldest first, newest adjacent to Now Playing; rows dimmed + cursor-back on tap.
        if (history.Count > 0)
        {
            rows.Add(new QRow(Header: Loc.Get(Strings.Player.History), Clear: viewer ? QueueClear.None : QueueClear.History));
            foreach (var e in history) rows.Add(new QRow(Entry: e, IsHistory: true, TrackIndex: trackIndex++));
        }

        // 1 — Now Playing anchor (the initial scroll target).
        anchorIndex = rows.Count;
        if (current is { } cur) rows.Add(new QRow(Entry: cur, IsNowPlaying: true, TrackIndex: trackIndex++));

        // 2 — Next in queue: title + count + Clear (Clear hidden while a remote device is active).
        if (userQueue.Count > 0)
        {
            rows.Add(new QRow(Header: Loc.Get(Strings.Player.NextInQueue), Count: userQueue.Count,
                Clear: viewer ? QueueClear.None : QueueClear.Queue));
            foreach (var e in userQueue) rows.Add(new QRow(Entry: e, TrackIndex: trackIndex++));
        }

        // 3 — Next up: "Playing from {source}" (source clickable).
        if (ctxUp.Count > 0)
        {
            string? source = resolvedName is { Length: > 0 } ? resolvedName : ImmediateContextName(ctxUri);
            string? sub = source is { Length: > 0 } ? Strings.Player.PlayingFrom(source) : null;
            string? href = source is { Length: > 0 } ? RichText.RouteForUri(ctxUri) : null;
            rows.Add(new QRow(Header: Loc.Get(Strings.Player.NextUp), Sub: sub, SubHref: href));
            foreach (var e in ctxUp) rows.Add(new QRow(Entry: e, TrackIndex: trackIndex++));
        }

        // 4 — Autoplay: always a real section (the toggle lives in its header); rows list beneath only when ON.
        rows.Add(new QRow(AutoplaySection: true));
        if (autoplayOn)
            foreach (var e in autoUp) rows.Add(new QRow(Entry: e, TrackIndex: trackIndex++));

        return rows;
    }

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
    // History rows are cursor-back, user-queue rows drain predecessors, upcoming rows jump — all routed server-side. The
    // PlayTrackAsync fallback fires only when the row carries no stable id (ItemId.IsNone — a legacy/degenerate snapshot).
    static void PlayQueueEntry(PlaybackBridge b, QueueEntry entry)
    {
        if (entry.ItemId.IsNone)
        {
            TrackRow.Invoke(b, entry.Track, () => b.Player.PlayTrackAsync(entry.Track));
            return;
        }
        TrackRow.Invoke(b, entry.Track, () => b.Player.SkipToQueueItemAsync(entry.ItemId));
    }

    static string RowKey(in QRow row) =>
        row.Entry is { } e && !e.ItemId.IsNone ? "i" + e.ItemId.Value
        : row.AutoplaySection ? "hdr:autoplay"
        : row.Header is { Length: > 0 } h ? "hdr:" + h + (row.Clear != QueueClear.None ? ":" + row.Clear : "")
        : "row";

    // One persistent bound row slot — recycling re-renders THIS mounted component with a new Index (no per-scroll cold
    // remount, so text/art mutate in place). Renders section header / autoplay section / track by the live QRow at its index.
    sealed class QueueRowSlot : Component
    {
        readonly RowScope _scope;
        readonly Signal<IReadOnlyList<QRow>> _rowsSig;
        readonly Signal<IReadOnlyList<QueueEntry>> _display;
        readonly ColorF _accent;

        public QueueRowSlot(RowScope scope, Signal<IReadOnlyList<QRow>> rowsSig,
            Signal<IReadOnlyList<QueueEntry>> display, ColorF accent)
        {
            _scope = scope;
            _rowsSig = rowsSig;
            _display = display;
            _accent = accent;
        }

        public override Element Render()
        {
            var b = UseContext(PlaybackBridge.Slot);
            var lib = UseContext(LibraryBridge.Slot);
            var svc = UseContext(Services.Slot);
            var go = UseContext(HistoryStore.NavCtx);
            var overlay = UseContext(Overlay.Service);
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);

            int i = _scope.Index.Value;
            var rows = _rowsSig.Value;
            if ((uint)i >= (uint)rows.Count) return new BoxEl();
            var row = rows[i];

            if (row.AutoplaySection)
                return AutoplaySection(svc);

            if (row.Header is { Length: > 0 })
                return HeaderRow(row, b, go);

            if (row.Entry is not { } entry || b is null) return new BoxEl();
            var t = entry.Track;
            var st = TrackRow.StateOf(b, lib, t);

            void Remove()
            {
                _ = b.Player.RemoveQueueItemAsync(entry.ItemId);
                var cur = new List<QueueEntry>(_display.Peek());
                cur.RemoveAll(x => entry.ItemId.IsNone ? x.EntryId == entry.EntryId : x.ItemId == entry.ItemId);
                _display.Value = cur;
                handle.Value?.Close();
            }

            void OpenMenu(Point2 _)
            {
                if (!row.CanRemove || overlay is null) return;
                if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
                var items = new[] { new MenuFlyoutItem(Loc.Get(Strings.Player.RemoveFromQueue), Icons.ChromeClose, Invoke: Remove) };
                handle.Value = overlay.Open(
                    () => anchor.Value,
                    () => MenuFlyout.Build(items, () => handle.Value?.Close()),
                    FlyoutPlacement.TopEdgeAlignedRight,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
                handle.Value.ClosedAction = () => handle.Value = null;
            }

            Element content = QueueTrackCell(t, st, lib, go,
                play: () => PlayQueueEntry(b, entry));

            return QueueRowSkin(_scope, content, row.TrackIndex, row.IsHistory, RowKey(row),
                row.CanRemove ? OpenMenu : null,
                row.CanRemove ? h => anchor.Value = h : null);
        }

        // A titled section header — caps title + optional row count on the left, an optional Clear text button on the right,
        // and an optional "Playing from {source}" sub-line (the source clickable when a route exists).
        Element HeaderRow(in QRow row, PlaybackBridge? b, Action<string, string?>? go)
        {
            var topKids = new List<Element>(3)
            {
                new TextEl(row.Header!.ToUpperInvariant())
                {
                    Size = 11f, Weight = 700, Color = Tok.TextTertiary, CharSpacing = 120f,
                    MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
            };
            if (row.Count >= 0)
                topKids.Add(new TextEl(row.Count.ToString()) { Size = 11f, Weight = 600, Color = Tok.TextTertiary });
            topKids.Add(new BoxEl { Grow = 1f, MinWidth = 0f });   // spacer → Clear pins right
            if (row.Clear != QueueClear.None && b is not null)
                topKids.Add(ClearButton(row.Clear, b));

            Element sub;
            if (row.Sub is not { Length: > 0 } subText) sub = new BoxEl();
            else if (row.SubHref is { Length: > 0 } href && go is not null)
                sub = new SpanTextEl([new TextSpan(subText, OnClick: () => go(href, null))])
                { Size = 11f, Weight = 400, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis };
            else
                sub = new TextEl(subText) { Size = 11f, Weight = 400, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis };

            return new BoxEl
            {
                Direction = 1, Justify = FlexJustify.Center, Height = RowExtent, Padding = new Edges4(8f, 7f, 8f, 0f),
                Children =
                [
                    new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = 6f, Children = topKids.ToArray() },
                    sub,
                ],
            };
        }

        Element ClearButton(QueueClear which, PlaybackBridge b)
        {
            void Clear()
            {
                if (which == QueueClear.History)
                {
                    _ = b.Player.ClearHistoryAsync();
                    _display.Value = _display.Peek().Where(x => x.Bucket != QueueBucket.History).ToList();
                }
                else
                {
                    _ = b.Player.ClearQueueAsync();
                    _display.Value = _display.Peek().Where(x => x.Bucket != QueueBucket.UserQueue).ToList();
                }
            }
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f,
                Padding = new Edges4(8f, 3f, 8f, 3f), Corners = CornerRadius4.All(6f),
                Fill = ColorF.Transparent, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
                Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, OnClick = Clear,
                Children = [new TextEl(Loc.Get(Strings.Player.Clear)) { Size = 11f, Weight = 700, Color = Tok.TextSecondary }],
            };
        }

        static Element QueueTrackCell(Track t, in TrackRow.State st, LibraryBridge? lib,
            Action<string, string?>? go, Action play)
        {
            Action? like = t.Uri.Length > 0 && lib is not null ? () => lib.ToggleSaved(t.Uri) : null;
            void Go(string route, string? title) => go?.Invoke(route, title);
            Element artist = go is null
                ? new TextEl(DetailFormat.ArtistNames(t.Artists))
                {
                    Size = 12f, Color = Tok.TextSecondary, MaxLines = 1,
                    Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                }
                : TrackRow.ArtistLinks(t.Artists, Go);

            return new BoxEl
            {
                Direction = 0,
                AlignItems = FlexAlign.Center,
                Grow = 1f,
                MinWidth = 0f,
                Gap = 8f,
                Padding = new Edges4(6f, 0f, 8f, 0f),
                Children =
                [
                    new BoxEl
                    {
                        Width = 30f, Height = RowExtent, Shrink = 0f,
                        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        Children = [TrackRow.Heart(st.Saved, like)],
                    },
                    new BoxEl
                    {
                        Width = QueueArt,
                        Height = QueueArt,
                        Shrink = 0f,
                        ZStack = true,
                        ClipToBounds = true,
                        Corners = CornerRadius4.All(5f),
                        Children =
                        [
                            Surfaces.Artwork(t.Image, t.Id.GetHashCode() & 0x7fffffff, QueueArt, QueueArt, 5f, decodePx: 72),
                            st.IsBuffering
                                ? new BoxEl
                                {
                                    Width = QueueArt, Height = QueueArt, AlignItems = FlexAlign.Center,
                                    Justify = FlexJustify.Center, Fill = ColorF.FromRgba(0, 0, 0, 110),
                                    Children = [TrackRow.Spinner()],
                                }
                                : Embed.Comp(() => new NowPlayingOverlay(t.Uri, play, 28f, cover: true, QueueArt, centered: true)).Skeletonized(false),
                        ],
                    },
                    new BoxEl
                    {
                        Direction = 1,
                        Grow = 1f,
                        Basis = 0f,
                        MinWidth = 0f,
                        Gap = 0f,
                        Justify = FlexJustify.Center,
                        Children =
                        [
                            new TextEl(t.Title)
                            {
                                Size = 13.5f, Weight = 600,
                                Color = st.IsNow ? Tok.AccentTextPrimary : Tok.TextPrimary,
                                Wrap = TextWrap.NoWrap, MaxLines = 1,
                                Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                            },
                            artist,
                        ],
                    },
                ],
            };
        }

        static BoxEl QueueRowSkin(RowScope scope, Element content, int trackIndex, bool history, string rowKey,
            Action<Point2>? contextMenu, Action<NodeHandle>? realized)
        {
            var index = scope.Index;
            var isEnabled = scope.IsEnabled;
            var interact = scope.OnInteraction;
            var focusChanged = scope.OnFocusChanged;
            bool Odd() => Math.Max(0, trackIndex) % 2 != 0;

            return new BoxEl
            {
                Key = rowKey,
                ZStack = true,
                MinHeight = RowExtent,
                ClipToBounds = true,
                Margin = new Edges4(TrackRow.RowInset, 0f, TrackRow.RowInset, 0f),
                Corners = CornerRadius4.All(6f),
                Fill = Prop.Of(() => Odd() ? WaveeColors.RowZebra : ColorF.Transparent),
                HoverFill = Prop.Of(() => Odd() ? WaveeColors.RowHoverZebra : WaveeColors.RowHover),
                PressedFill = Prop.Of(() => Odd() ? WaveeColors.RowPressedZebra : WaveeColors.RowPressed),
                PressScale = 0.985f,
                BorderWidth = 1f,
                BorderColor = ColorF.Transparent,
                HoverBorderColor = Tok.StrokeCardDefault,
                FocusVisualMargin = Edges4.All(1f),
                Focusable = false,
                Role = AutomationRole.Button,
                Opacity = Prop.Of(() => (isEnabled() ? 1f : ItemContainer.DisabledOpacity) * (history ? 0.62f : 1f)),
                Enter = new EnterExit(Dy: 6f, Opacity: 0f, Active: true),
                Exit = new EnterExit(Dy: -4f, Opacity: 0f, Active: true),
                Layout = LayoutTransition.Slide,
                OnPointerPressed = args => interact(
                    args.ClickCount >= 2 ? ItemContainerTrigger.DoubleTap : ItemContainerTrigger.Tap, args.Mods),
                OnKeyDown = args =>
                {
                    if (args.KeyCode == Keys.Enter) { interact(ItemContainerTrigger.EnterKey, args.Mods); args.Handled = true; }
                    else if (args.KeyCode == Keys.Space && !args.IsRepeat) { interact(ItemContainerTrigger.SpaceKey, args.Mods); args.Handled = true; }
                },
                OnFocusChanged = focusChanged,
                OnPointerExit = static () => { },
                OnContextRequested = contextMenu,
                OnRealized = realized,
                Children = [new BoxEl { Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center, Children = [content] }],
            };
        }

        // The Autoplay section header (§10.1 row 4): ∞ · Autoplay · "Similar music will keep playing" + the on/off toggle.
        // Always rendered (even OFF/empty) as the toggle affordance — the autoplay rows list beneath it when ON.
        Element AutoplaySection(Services? svc)
        {
            int prefsEpoch = PlaybackPrefs.Epoch.Value;   // subscribe → re-render on a toggle from either surface
            _ = prefsEpoch;
            bool on = svc?.Settings.Get(WaveeSettings.AutoplayEnabled) ?? true;
            void Toggle()
            {
                if (svc is null) return;
                svc.Settings.Set(WaveeSettings.AutoplayEnabled, !on);
                PlaybackPrefs.Bump();
            }
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = 12f, Height = RowExtent,
                Padding = new Edges4(6f, 4f, 6f, 4f), Corners = CornerRadius4.All(8f),
                Fill = ColorF.Transparent, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = Toggle,
                Children =
                [
                    new BoxEl
                    {
                        Width = 32f, Height = 32f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        Corners = CornerRadius4.All(8f), Fill = on ? _accent : Tok.FillCardSecondary,
                        Children = [new TextEl("∞") { Size = 17f, Weight = 800, Color = on ? Tok.TextOnAccentPrimary : Tok.TextSecondary }],
                    },
                    new BoxEl
                    {
                        Direction = 1, Grow = 1f, MinWidth = 0f, Gap = 1f,
                        Children =
                        [
                            new TextEl(Loc.Get(Strings.Player.Autoplay)) { Size = 13f, Weight = 700, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                            new TextEl(Loc.Get(Strings.Player.AutoplayHint)) { Size = 11f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        ],
                    },
                ],
            };
        }
    }
}

/// <summary>Cross-surface playback preference epoch (autoplay): bumped when <see cref="WaveeSettings.AutoplayEnabled"/>
/// changes from the Settings toggle, the queue-panel pill, or the autoplay footer, so every surface stays in sync.</summary>
static class PlaybackPrefs
{
    public static readonly Signal<int> Epoch = new(0);
    public static void Bump() => Epoch.Value = Epoch.Peek() + 1;
}
