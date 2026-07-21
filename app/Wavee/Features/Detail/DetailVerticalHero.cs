using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Features.Detail;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The Apple-Music-inspired hero for the VERTICAL (narrow) track-detail layout — virtual item 0 of the track list. Built
// per-render from live values (BuildHeader's pattern → the hero re-derives on every re-render, so no frozen-prop hazard
// for the plain elements; Embed.Comp children freeze exactly as BuildHeader's do). Composition adapts to the resolved
// orientation: artwork BESIDE a flexing info column (side-by-side) or a big CENTERED cover above centered text (stacked).
// A Play/Shuffle-prominent action hierarchy with one unified More menu replaces the utilitarian context-action cluster.
static class DetailVerticalHero
{
    public static Element Build(DetailModel m, DetailConfig cfg, DetailHandlers h, Loadable<DetailModel> full,
                                DetailHeroOrientation o, float artSize, float availW)
    {
        bool side = o == DetailHeroOrientation.SideBySide;
        bool editable = m.Capabilities.CanEditMetadata && m.ContextUri is { Length: > 0 };

        // Bucket the available width to 8 DIP before deriving the content width, so the InlineEdit facades' width-folding
        // keys (title/description) don't churn a remount on every sub-pixel resize frame.
        float bw = MathF.Round(MathF.Max(0f, availW) / 8f) * 8f;
        if (bw <= 0f) bw = DetailVerticalLayout.FallbackW;
        float pad = 2f * DetailVerticalLayout.HeroPad;
        // Cap the text column: with the "Stacked" page layout the hero now renders at ANY width, and an uncapped title/
        // description would sprawl into 150-char lines on a wide window. 640 keeps the measure readable; the block stays
        // leading-aligned (the cap never affects the < 580 vertical band, where the geometry is width-limited anyway).
        float contentW = MathF.Min(640f, MathF.Max(160f, side ? bw - pad - artSize - DetailVerticalLayout.HeroGap : bw - pad));
        int descLines = DetailVerticalLayout.DescriptionMaxLines(o);

        // Artwork — a shadowed rounded box; editable playlists get the click-to-change cover facade.
        Element artworkBox = new BoxEl
        {
            Width = artSize, Height = artSize, Shrink = 0f,
            Corners = CornerRadius4.All(WaveeRadius.Card), Shadow = Elevation.Card, ClipToBounds = true,
            AlignSelf = side ? FlexAlign.Start : FlexAlign.Center,
            Children = [editable ? PlaylistInlineEdit.Cover(full, artSize) : DetailRail.HeroArtwork(m, artSize)],
        };

        var infoKids = new List<Element>(6);

        // Identity: album/single → type/year badges + billed-artist face pile; playlist → owner/collaborators block.
        if (cfg.Badges == BadgeStyle.TypeYear)
        {
            var idKids = new List<Element>(2);
            var pills = new List<Element>(2);
            if (m.BadgeType is { Length: > 0 }) pills.Add(DetailRail.BadgePill(m.BadgeType));
            if (m.Year is { Length: > 0 }) pills.Add(DetailRail.BadgePill(m.Year));
            if (pills.Count > 0) idKids.Add(new BoxEl { Direction = 0, Gap = WaveeSpace.S, Children = pills.ToArray() });
            if (m.Artists.Count > 0) idKids.Add(Embed.Comp(() => new ArtistFacePile(m, contentW, h)));
            if (idKids.Count > 0)
                infoKids.Add(new BoxEl { Direction = 1, Gap = WaveeSpace.XS, AlignItems = side ? FlexAlign.Start : FlexAlign.Center, Children = idKids.ToArray() });
        }
        else if (cfg.Badges == BadgeStyle.OwnerRow && m.OwnerName is { Length: > 0 })
        {
            infoKids.Add(DetailRail.PlaylistOwnerBlock(m, contentW, full));
        }

        // Title — the heavy hero run. Editable playlists get the inline-edit title facade (which floors its font at 18px
        // internally; acceptable for the 32→24 hero range). The read-only title stretches to the info column and wraps.
        infoKids.Add(editable
            ? PlaylistInlineEdit.Title(full, contentW, 32f)
            : WaveeType.PageHero(m.Title) with
            {
                Size = 32f, MinSize = 24f, Weight = 900, LineHeight = float.NaN,
                Wrap = TextWrap.WrapWholeWords, MaxLines = 3, Trim = TextTrim.CharacterEllipsis,
            });

        // Meta line for ALL kinds (albums' MetaLine carries songs·duration·year; playlists/liked carry songs·duration).
        if (m.MetaLine is { Length: > 0 })
            infoKids.Add(WaveeType.TrackMeta(m.MetaLine) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis });

        // Actions row 1 — the ONLY two prominent buttons: Play (solid accent) + Shuffle (accent-tinted fill). This
        // deliberately reverses the two-column rail's "Shuffle lives in the toolbar" decision for the vertical layout.
        ColorF onAccent = WaveePalette.OnAccent(h.Accent);
        infoKids.Add(new BoxEl
        {
            Direction = 0, Gap = WaveeSpace.M, AlignItems = FlexAlign.Center,
            Children =
            [
                ActionPill(Icons.Play, Loc.Get(Strings.Detail.Play), h.Accent, onAccent, h.PlayAll),
                ActionPill(Icons.Shuffle, Loc.Get(Strings.Detail.Shuffle), h.Accent with { A = 0.16f }, Tok.AccentTextPrimary, h.Shuffle),
            ],
        });

        // Actions row 2 — quiet 40-DIP icons: Save/Follow · Share · the unified More menu. No download action.
        var quiet = new List<Element>(3);
        if (m.ContextUri is { Length: > 0 } saveUri && cfg.Heart != HeartMode.None)
            quiet.Add(Embed.Comp(() => new SaveButton(saveUri, 16f, 40f, m.Title)));
        quiet.Add(PlaylistInlineEdit.ShareButton(full));
        quiet.Add(Embed.Comp(() => new DetailHeroMoreButton(full, cfg, h)) with { Key = "vhero-more:" + m.ContextUri });
        infoKids.Add(new BoxEl { Direction = 0, Gap = WaveeSpace.S, AlignItems = FlexAlign.Center, Children = quiet.ToArray() });

        // Description — the release/playlist blurb, capped to the orientation's line count.
        if (editable)
            infoKids.Add(PlaylistInlineEdit.Description(full, contentW, descLines, h));
        else if (m.Description is { Length: > 0 })
            infoKids.Add(RichText.Of(m.Description!, 12f, Tok.TextSecondary, Tok.AccentTextPrimary, contentW, descLines,
                u => { if (RichText.RouteForUri(u) is { } k) h.Go(k, null); }));

        Element hero = side
            ? new BoxEl
            {
                Direction = 0, Gap = DetailVerticalLayout.HeroGap, AlignItems = FlexAlign.Start,
                Children =
                [
                    artworkBox,
                    new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = WaveeSpace.M, AlignItems = FlexAlign.Stretch, Children = infoKids.ToArray() },
                ],
            }
            : new BoxEl
            {
                Direction = 1, Gap = WaveeSpace.L, AlignItems = FlexAlign.Center,
                Children =
                [
                    artworkBox,
                    new BoxEl { Direction = 1, Width = contentW, Gap = WaveeSpace.M, AlignItems = FlexAlign.Center, Children = infoKids.ToArray() },
                ],
            };

        return new BoxEl
        {
            Direction = 1, Padding = Edges4.All(DetailVerticalLayout.HeroPad),
            Children = [hero],
        };
    }

    // The artist-hero CTA skin adapted to a balanced pair: 48-DIP, 24 radius, 16-DIP glyph and 15-DIP bold label.
    static Element ActionPill(string glyph, string label, ColorF fill, ColorF fg, Action onClick)
        => HeroCta.Pill(glyph, label, fill, fg, onClick, balanced: true);
}

// The vertical hero's unified overflow ("More") menu. A 40-DIP round ⋯ Fab whose flyout is built lazily at open from the
// LIVE model: Add/Copy to playlist (the searchable picker) · Play next · Add to queue · (owner-only) Invite / Delete.
// Every item uses the new IconRef { Glyph, Font } form. Keyed per context at the call site so its frozen ctor args
// (cfg/h) stay coherent for THIS page.
sealed class DetailHeroMoreButton : Component
{
    readonly Loadable<DetailModel> _full;
    readonly DetailConfig _cfg;
    readonly DetailHandlers _h;

    public DetailHeroMoreButton(Loadable<DetailModel> full, DetailConfig cfg, DetailHandlers h)
    { _full = full; _cfg = cfg; _h = h; }

    public override Element Render()
    {
        var overlay = UseContext(Overlay.Service);
        var lib = UseContext(LibraryBridge.Slot);
        var svc = UseContext(Services.Slot);
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var pickerHandle = UseRef<OverlayHandle?>(null);
        var accessHandle = UseRef<OverlayHandle?>(null);

        void Toggle()
        {
            if (overlay is null) return;
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            var m = _full.Value.Peek();
            // Read-only contexts (followed playlists, Liked) COPY to a playlist; an editable playlist / album ADDS.
            bool copy = _cfg.Heart == HeartMode.Follow || LikedSongsArtwork.IsLikedUri(m.ContextUri);
            var items = new List<MenuFlyoutItem>
            {
                new(Loc.Get(copy ? Strings.Detail.CopyToPlaylist : Strings.Detail.AddToPlaylist),
                    new IconRef { Glyph = Mdl.Add, Font = null },
                    Invoke: () => PlaylistPickerLauncher.OpenFlyout(overlay, () => anchor.Value, () => _full.Value.Peek().Tracks, pickerHandle)),
                new(Loc.Get(Strings.Detail.PlayNext), new IconRef { Glyph = WaveeIcons.PlayNext, Font = WaveeIcons.Font }, Invoke: _h.PlayNext),
                new(Loc.Get(Strings.Detail.AddToQueue), new IconRef { Glyph = WaveeIcons.PlayAfter, Font = WaveeIcons.Font }, Invoke: _h.AddToQueue),
            };
            // Owner-only Invite / Delete (capability-gated inside AppendOwnerItems), behind a separator.
            var ownerItems = new List<MenuFlyoutItem>();
            PlaylistInlineEdit.AppendOwnerItems(ownerItems, overlay, lib, svc, _full, _h, () => anchor.Value, accessHandle);
            if (ownerItems.Count > 0)
            {
                items.Add(MenuFlyoutItem.Separator);
                items.AddRange(ownerItems);
            }
            handle.Value = overlay.Open(
                () => anchor.Value,
                () => MenuFlyout.Create(items, () => handle.Value?.Close()),
                FlyoutPlacement.BottomEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
            handle.Value.ClosedAction = () => handle.Value = null;
        }

        return new BoxEl
        {
            Width = 40f, Height = 40f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(20f),
            HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            HoverScale = 1.06f, PressScale = 0.94f,
            Cursor = CursorId.Hand, Role = AutomationRole.Button,
            OnClick = Toggle,
            OnRealized = h => anchor.Value = h,
            Children = [Icon(Mdl.More, 16f, Tok.TextSecondary)],
        };
    }
}

// Cross-surface page-layout preference epoch: bumped when the Settings → Appearance "Track page layout" row changes,
// so any mounted (incl. KeepAlive-parked) DetailShell re-resolves rail-vs-hero live. (PlayerBarPrefs pattern.)
static class DetailHeroPrefs
{
    public static readonly Signal<int> Epoch = new(0);
    public static void Bump() => Epoch.Value = Epoch.Peek() + 1;
}
