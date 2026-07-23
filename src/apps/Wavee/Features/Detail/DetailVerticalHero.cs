using System;
using System.Collections.Generic;
using FluentGpu.Animation;
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
// orientation: fixed artwork BESIDE a flexing info column, or full-width artwork carrying its centered identity/actions
// in the lower edge fade (immersive, matching Apple Music's album/playlist hierarchy). The former morphs its retained
// identity into the shy header; immersive media instead parallax-dissolves into the page while a compact identity
// crossfades in, avoiding the full-viewport-cover-to-thumbnail shrink.
static class DetailVerticalHero
{
    public static Element Build(DetailModel m, DetailConfig cfg, DetailHandlers h, Loadable<DetailModel> full,
                                DetailHeroOrientation o, float artSize, float availW, float collapseDistance,
                                float compactLeft, float compactSearchWidth,
                                IReadSignal<bool> compactInteractive, Element toolbar)
    {
        bool side = o == DetailHeroOrientation.SideBySide;
        bool immersive = !side;
        bool editable = m.Capabilities.CanEditMetadata && m.ContextUri is { Length: > 0 };
        bool expandedInteractive = !compactInteractive.Value;

        // Bucket the available width to 8 DIP before deriving the content width, so the InlineEdit facades' width-folding
        // keys (title/description) don't churn a remount on every sub-pixel resize frame.
        float viewportW = availW > 0f ? availW : DetailVerticalLayout.FallbackW;
        float bw = MathF.Round(viewportW / 8f) * 8f;
        if (bw <= 0f) bw = DetailVerticalLayout.FallbackW;
        float pad = 2f * DetailVerticalLayout.HeroPad;
        // Cap the text column: with the "Hero" page layout the hero now renders at ANY width, and an uncapped title/
        // description would sprawl into 150-char lines on a wide window. 640 keeps the measure readable; the block stays
        // leading-aligned (the cap never affects the < 580 vertical band, where the geometry is width-limited anyway).
        float contentW = MathF.Min(640f, MathF.Max(160f, side ? bw - pad - artSize - DetailVerticalLayout.HeroGap : bw - pad));
        int descLines = DetailVerticalLayout.DescriptionMaxLines(o);
        float expandedFadeEnd = MathF.Min(collapseDistance, DetailVerticalLayout.ExpandedContentFadeDistance);
        ScrollBindDsl[] FadeExpanded() =>
        [
            new() { From = ScrollChannel.Offset, To = BindSink.Opacity,
                Range = ScrollRange.Px(0f, expandedFadeEnd), OutStart = 1f, OutEnd = 0f },
        ];
        ScrollBindDsl[] FadeMorphingPlay() =>
        [
            new() { From = ScrollChannel.Offset, To = BindSink.Opacity,
                Range = ScrollRange.Px(collapseDistance * 0.42f, collapseDistance * 0.68f), OutStart = 1f, OutEnd = 0f },
        ];
        ScrollBindDsl[] ImmersiveArtworkBinds() =>
        [
            new() { From = ScrollChannel.Offset, To = BindSink.TransY,
                Range = ScrollRange.Px(0f, collapseDistance), OutStart = 0f, OutEnd = -MathF.Min(96f, artSize * 0.16f),
                Ease = Easing.Linear },
            new() { From = ScrollChannel.Offset, To = BindSink.Opacity,
                Range = ScrollRange.Px(collapseDistance * 0.12f, collapseDistance * 0.62f), OutStart = 1f, OutEnd = 0f,
                Ease = Easing.Linear },
        ];
        ScrollBindDsl[] CompactRevealBinds() =>
        [
            new() { From = ScrollChannel.Offset, To = BindSink.Opacity,
                Range = ScrollRange.Px(collapseDistance * 0.56f, collapseDistance * 0.82f), OutStart = 0f, OutEnd = 1f,
                Ease = Easing.Linear },
            new() { From = ScrollChannel.Offset, To = BindSink.TransY,
                Range = ScrollRange.Px(collapseDistance * 0.56f, collapseDistance * 0.82f), OutStart = -6f, OutEnd = 0f,
                Ease = Easing.Linear },
        ];
        Element Fade(Element e) => new BoxEl
        {
            Direction = 1,
            HitTestVisible = expandedInteractive,
            OpacityGroup = true, ScrollBinds = FadeExpanded(),
            Children = [e],
        };

        // Artwork — a shadowed rounded box; editable playlists get the click-to-change cover facade.
        float fullArtX = side
            ? DetailVerticalLayout.HeroPad
            : 0f;
        float fullArtY = side ? DetailVerticalLayout.HeroPad : 0f;
        // Side-by-side: pin Opacity/Trans to identity and morph in place. (In-flow cover — no overlay.)
        // connected:false avoids a Hero-fly dest that can leave the slot empty if the fly handoff glitches.
        ScrollBindDsl[] SideArtworkBinds() =>
        [
            new() { From = ScrollChannel.Offset, To = BindSink.Opacity,
                Range = ScrollRange.Px(0f, 1f), OutStart = 1f, OutEnd = 1f },
            new() { From = ScrollChannel.Offset, To = BindSink.TransY,
                Range = ScrollRange.Px(0f, 1f), OutStart = 0f, OutEnd = 0f },
            new() { From = ScrollChannel.Offset, MorphLeftTo = compactLeft,
                Range = ScrollRange.Px(0f, collapseDistance), OutStart = 0f, OutEnd = 1f },
            new() { From = ScrollChannel.Offset,
                MorphTopTo = (DetailVerticalLayout.CompactIdentityHeight - DetailVerticalLayout.CompactArtworkSize) * 0.5f,
                Range = ScrollRange.Px(0f, collapseDistance), OutStart = 0f, OutEnd = 1f },
            new() { From = ScrollChannel.Offset, To = BindSink.ScaleUniform,
                Range = ScrollRange.Px(0f, collapseDistance), OutStart = 1f,
                OutEnd = DetailVerticalLayout.CompactArtworkSize / MathF.Max(1f, artSize) },
        ];
        Element artworkBox = new BoxEl
        {
            Width = artSize, Height = artSize, Shrink = 0f,
            HitTestVisible = expandedInteractive,
            Corners = CornerRadius4.All(side ? Radii.Card : 0f),
            Shadow = side ? Elevation.Card : default,
            ClipToBounds = true,
            // Apple melts the lower ~⅓ of the bitmap into the opaque page wash (longer melt = less hard plate).
            EdgeFade = immersive ? new EdgeFadeSpec(EdgeMask.Bottom, MathF.Min(260f, artSize * 0.34f)) : null,
            TransformOriginX = 0f, TransformOriginY = 0f,
            ScrollBinds = side ? SideArtworkBinds() : ImmersiveArtworkBinds(),
            Children =
            [
                editable
                    ? PlaylistInlineEdit.Cover(full, artSize, side ? Radii.Card : 0f, shadow: side)
                    // Apple oversaturates album art for a punchier look under the hero scrim — applied in both hero
                    // layouts (immersive/stacked and the wide side-by-side rail).
                    : DetailRail.HeroArtwork(m, artSize, side ? Radii.Card : 0f, connected: !side, saturation: 1.18f)
            ],
        };

        var infoKids = new List<Element>(6);

        // Identity: album/single → type/year badges + billed-artist face pile; playlist → owner/collaborators block.
        string eyebrow;
        if (cfg.Badges == BadgeStyle.TypeYear)
            eyebrow = m.BadgeType is { Length: > 0 } type && m.Year is { Length: > 0 } year
                ? type + " · " + year
                : m.BadgeType ?? m.Year ?? "";
        else if (cfg.Badges == BadgeStyle.OwnerRow) eyebrow = Loc.Get(Strings.Nav.Playlist);
        else eyebrow = Loc.Get(Strings.Nav.YourLibrary);
        // Apple's immersive hierarchy starts directly with the release title. Type/year remains useful in the wider
        // desktop adaptation, but duplicating it above the title on a phone-sized hero pushes the useful copy too high.
        if (side && eyebrow.Length > 0)
            infoKids.Add(Fade(new TextEl(eyebrow)
            {
                Size = 11f, Weight = 600, Color = Tok.TextTertiary, CharSpacing = 40f,
                MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            }));

        // Immersive type is Apple Music display hierarchy — NOT the Fluent Title ramp (Semibold + 36 LH). Short titles
        // punch larger; long titles stay Bold but step down so two lines still read as one cluster. Tracking is slight
        // negative (1/1000 em). Side-by-side keeps Wavee's desktop Title voice.
        float titleSize = immersive ? ImmersiveTitleSize(m.Title) : 32f;
        ushort titleWeight = immersive ? (ushort)700 : (ushort)600;
        Element title = editable
            ? PlaylistInlineEdit.Title(full, contentW, titleSize, titleWeight, onMedia: immersive)
            : immersive
                ? new TextEl(m.Title)
                {
                    // Display optical size when available (SF Pro Display analogue on Windows).
                    FontFamily = "Segoe UI Variable Display",
                    Size = titleSize, Weight = titleWeight,
                    LineHeight = titleSize * 1.08f, CharSpacing = titleSize >= 34f ? -28f : -16f,
                    MaxWidth = contentW,
                    Wrap = TextWrap.WrapWholeWords, MaxLines = 2, Trim = TextTrim.CharacterEllipsis,
                    Color = Tok.OnMediaPrimary,
                }
                : WaveeType.PageHero(m.Title) with
                {
                    Size = titleSize, MinSize = 18f, Weight = titleWeight, LineHeight = float.NaN,
                    Width = contentW, MaxWidth = contentW,
                    Wrap = TextWrap.WrapWholeWords, MaxLines = 3, Trim = TextTrim.CharacterEllipsis,
                    Color = Tok.TextPrimary,
                };

        Element? attribution = Attribution(m, h, contentW, immersive, full);

        // Identity copy. Side-by-side keeps the prose-first desktop order; immersive follows the Apple Music stack:
        // tight identity cluster → actions → quieter prose.
        Element? description = null;
        if (editable)
            description = Fade(PlaylistInlineEdit.Description(full, contentW, descLines, h, onMedia: immersive));
        else if (m.Description is { Length: > 0 })
            description = Fade(RichText.Expandable(m.Description, immersive ? 13f : 12f,
                immersive ? ColorF.FromRgba(255, 255, 255) with { A = 0.58f } : Tok.TextSecondary,
                immersive ? Tok.OnMediaPrimary : Tok.AccentTextPrimary,
                contentW, descLines, m.ContextUri ?? m.Title,
                u => { if (RichText.RouteForUri(u) is { } k) h.Go(k, null); }));

        Element? meta = null;
        if (m.MetaLine is { Length: > 0 })
            meta = immersive
                ? new TextEl(m.MetaLine)
                {
                    Size = 12f, Weight = 400, LineHeight = 16f,
                    Color = ColorF.FromRgba(255, 255, 255) with { A = 0.58f },
                    MaxWidth = contentW, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                }
                : WaveeType.TrackMeta(m.MetaLine) with
                {
                    MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                };

        if (side)
        {
            infoKids.Add(new BoxEl
            {
                Direction = 1, Width = contentW, HitTestVisible = expandedInteractive,
                TransformOriginX = 0f, TransformOriginY = 0f,
                ScrollBinds =
                [
                    new() { From = ScrollChannel.Offset,
                        MorphLeftTo = compactLeft + DetailVerticalLayout.CompactArtworkSize + Spacing.M,
                        Range = ScrollRange.Px(0f, collapseDistance), OutStart = 0f, OutEnd = 1f },
                    new() { From = ScrollChannel.Offset, MorphTopTo = 17f,
                        Range = ScrollRange.Px(0f, collapseDistance), OutStart = 0f, OutEnd = 1f },
                    new() { From = ScrollChannel.Offset, To = BindSink.ScaleUniform,
                        Range = ScrollRange.Px(0f, collapseDistance), OutStart = 1f, OutEnd = 14f / titleSize },
                ],
                Children = [title],
            });
            if (attribution is not null) infoKids.Add(Fade(attribution));
            if (description is not null) infoKids.Add(description);
            if (meta is not null) infoKids.Add(Fade(meta));
        }
        else
        {
            // One Fade for the whole identity: title / artist / meta sit in a tight Apple stack (not Fluent 6-DIP list gap).
            var identityKids = new List<Element>(3) { title };
            if (attribution is not null) identityKids.Add(attribution);
            if (meta is not null) identityKids.Add(meta);
            infoKids.Add(Fade(new BoxEl
            {
                Direction = 1, Gap = 3f, AlignItems = FlexAlign.Start,
                Children = identityKids.ToArray(),
            }));
        }

        // Desktop keeps Wavee's full action cluster. Immersive follows Apple's phone hierarchy exactly: a quiet circular
        // Shuffle, a dominant wide Play pill, and a quiet circular Save/Add. Share + More move to the artwork's top edge.
        // Circle fills are white-alpha "glass" so the art-derived wash tints them (Apple vibrancy) — never opaque MediaScrim.
        ColorF onAccent = ColorContrast.PickContrast(h.Accent);
        ColorF glass = ImmersiveGlass;
        ColorF glassHover = ImmersiveGlassHover;
        ColorF glassPress = ImmersiveGlassPress;
        float actionSize = side ? 32f : 40f;
        float compactPlayLeft = viewportW - compactLeft - DetailVerticalLayout.CompactArtworkSize;
        // Immersive Play is a plain white pill in the Apple 3-control row (no morph slot → no 48-DIP wrapper offset).
        // Side-by-side keeps the morphing Play that compositor-transforms into the compact header control.
        Element playButton = ActionButton(Icons.Play, Loc.Get(Strings.Detail.Play), actionSize,
            immersive ? Tok.OnMediaPrimary : h.Accent,
            immersive ? ColorF.FromRgba(0, 0, 0) : onAccent,
            h.PlayAll, pill: immersive, width: immersive ? 132f : float.NaN,
            labelSize: immersive ? 15f : 13f);
        Element expandedPlay = immersive
            ? Fade(playButton)
            : new BoxEl
            {
                Direction = 1, Height = 48f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                HitTestVisible = expandedInteractive, OpacityGroup = true,
                ScrollBinds = FadeMorphingPlay(),
                Children = [playButton],
            };
        Element compactPlayVisual = new BoxEl
        {
            Width = 48f, Height = 48f, Shrink = 0f,
            Corners = CornerRadius4.All(24f), Fill = h.Accent,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            HitTestVisible = false, OpacityGroup = true,
            ScrollBinds =
            [
                new() { From = ScrollChannel.Offset, To = BindSink.Opacity,
                    Range = ScrollRange.Px(collapseDistance * 0.52f, collapseDistance * 0.74f), OutStart = 0f, OutEnd = 1f },
            ],
            Children = [Icon(Icons.Play, 16f, onAccent)],
        };
        Element compactPlayHit = new BoxEl
        {
            Width = 48f, Height = 48f, Shrink = 0f,
            Corners = CornerRadius4.All(24f), Cursor = CursorId.Hand, Role = AutomationRole.Button,
            OnClick = h.PlayAll, HoverScale = 1.06f, PressScale = 0.94f,
        };
        Element playMorph = new BoxEl
        {
            ZStack = true, Width = side ? 76f : 88f, Height = 48f, Shrink = 0f,
            HitTestPassThrough = true, TransformOriginX = 0f, TransformOriginY = 0f,
            ScrollBinds =
            [
                new() { From = ScrollChannel.Offset, MorphLeftTo = compactPlayLeft,
                    Range = ScrollRange.Px(0f, collapseDistance), OutStart = 0f, OutEnd = 1f },
                new() { From = ScrollChannel.Offset, MorphTopTo = 10f,
                    Range = ScrollRange.Px(0f, collapseDistance), OutStart = 0f, OutEnd = 1f },
                new() { From = ScrollChannel.Offset, To = BindSink.ScaleUniform,
                    Range = ScrollRange.Px(0f, collapseDistance), OutStart = 1f, OutEnd = 0.75f },
            ],
            Children =
            [
                Flow.Show(() => !compactInteractive.Value, expandedPlay),
                compactPlayVisual,
                Flow.Show(() => compactInteractive.Value, compactPlayHit),
            ],
        };
        var actions = new List<Element>(5);
        if (immersive)
        {
            actions.Add(Fade(ActionButton(Icons.Shuffle, Loc.Get(Strings.Detail.Shuffle), actionSize,
                glass, Tok.OnMediaPrimary, h.Shuffle, iconOnly: true, pill: true,
                hoverFill: glassHover, pressedFill: glassPress, hairline: true)));
            actions.Add(expandedPlay);
        }
        else
        {
            actions.Add(playMorph);
            actions.Add(Fade(ActionButton(Icons.Shuffle, Loc.Get(Strings.Detail.Shuffle), actionSize,
                Tok.FillSubtleSecondary, Tok.TextPrimary, h.Shuffle)));
        }
        if (m.ContextUri is { Length: > 0 } saveUri && cfg.Heart != HeartMode.None)
            actions.Add(Fade(immersive
                ? Embed.Comp(() => new DetailHeroSaveButton(saveUri, m.Title, actionSize))
                    with { Key = $"vhero-save-media:{saveUri}:{(int)actionSize}" }
                : Embed.Comp(() => new SaveButton(saveUri, 16f, actionSize, m.Title))));
        if (side)
        {
            actions.Add(Fade(PlaylistInlineEdit.ShareButton(full, actionSize)));
            actions.Add(Fade(Embed.Comp(() => new DetailHeroMoreButton(full, cfg, h, actionSize))
                with { Key = $"vhero-more:{m.ContextUri}:{(int)actionSize}" }));
        }
        // Breathing room between the tight identity cluster and the control row (Apple ~12–16pt).
        if (immersive) infoKids.Add(new BoxEl { Height = 12f, HitTestVisible = false });
        infoKids.Add(new BoxEl
        {
            Direction = 0, Gap = side ? Spacing.S : 12f, AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Start,
            Children = actions.ToArray(),
        });
        if (immersive && description is not null)
        {
            infoKids.Add(new BoxEl { Height = 14f, HitTestVisible = false });
            infoKids.Add(description);
        }

        Element artworkPlaceholder = new BoxEl { Width = artSize, Height = artSize, Shrink = 0f };
        // Side-by-side: keep the cover IN FLOW (not a ZStack overlay). The overlay path left a transparent spacer when
        // compositor opacity/transform leftover from immersive, or when scroll binds failed to resolve — the classic
        // "empty hero with floating text" failure. Morph still runs on the in-flow node; layout space stays reserved.
        Element hero = side
            ? new BoxEl
            {
                Direction = 0, Gap = DetailVerticalLayout.HeroGap, AlignItems = FlexAlign.Start,
                Children =
                [
                    artworkBox,
                    new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.M, AlignItems = FlexAlign.Stretch, Children = infoKids.ToArray() },
                ],
            }
            : new BoxEl
            {
                ZStack = true, Width = viewportW, Height = artSize,
                AlignItems = FlexAlign.Center, ClipToBounds = true,
                Children =
                [
                    artworkPlaceholder,
                    new BoxEl
                    {
                        Direction = 1, Width = viewportW, Height = artSize,
                        Justify = FlexJustify.End,
                        Padding = new Edges4(DetailVerticalLayout.HeroPad, DetailVerticalLayout.HeroPad,
                            DetailVerticalLayout.HeroPad, 22f),
                        // Identity cluster owns its own 3-DIP gap; outer gap only separates major blocks.
                        Gap = 0f, AlignItems = FlexAlign.Start,
                        Children = infoKids.ToArray(),
                    },
                ],
            };

        Element expanded = new BoxEl
        {
            Direction = 1,
            Children =
            [
                side
                    ? new BoxEl { Direction = 1, Padding = Edges4.All(DetailVerticalLayout.HeroPad), Children = [hero] }
                    : hero,
                new BoxEl
                {
                    Direction = 1,
                    Padding = new Edges4(compactLeft, DetailVerticalLayout.ExpandedToolbarTopPad,
                        compactLeft, DetailVerticalLayout.ExpandedToolbarBottomPad),
                    Children = [toolbar],
                },
            ],
        };

        // Immersive art stays a page-media overlay (parallax dissolve). Side-by-side already placed artworkBox in-flow.
        if (!immersive) return expanded;

        Element artworkLayer = new BoxEl
        {
            Direction = 1,
            Padding = new Edges4(fullArtX, fullArtY, 0f, 0f),
            HitTestPassThrough = true,
            Children = [artworkBox],
        };

        static ColorF Scrim(float alpha) => ColorF.FromRgba(0, 0, 0) with { A = alpha };
        // Mid-band contrast only: EdgeFade dissolves the bitmap into the opaque page wash; this veil sits ABOVE the
        // faded media and BELOW the copy so white identity stays readable, then releases to transparent at the page
        // seam so it never fights the melt (artist-hero pattern). Cap = 4 GradientSpec stops.
        Element copyContrast = new BoxEl
        {
            Width = viewportW, Height = artSize, HitTestPassThrough = true,
            Gradient = GradientDown(
                new GradientStop(0.50f, Scrim(0f)),
                new GradientStop(0.74f, Scrim(0.46f)),
                new GradientStop(0.90f, Scrim(0.26f)),
                new GradientStop(1f, Scrim(0f))),
            OpacityGroup = true, ScrollBinds = FadeExpanded(),
        };

        var utilityKids = new List<Element>(2);
        if (m.ShareUrl is { Length: > 0 } shareUrl)
            utilityKids.Add(ActionButton(Icons.Share, Loc.Get(Strings.Menu.Share), 36f,
                glass, Tok.OnMediaPrimary,
                () => InputHooks.Current.Default.OpenUri?.Invoke(shareUrl), iconOnly: true, pill: true,
                hoverFill: glassHover, pressedFill: glassPress, hairline: true));
        utilityKids.Add(Embed.Comp(() => new DetailHeroMoreButton(full, cfg, h, 36f, onMedia: true))
            with { Key = $"vhero-more-media:{m.ContextUri}" });
        Element immersiveUtilities = new BoxEl
        {
            Direction = 0, Width = viewportW, Height = 60f, Gap = Spacing.S,
            Padding = new Edges4(0f, 12f, 14f, 0f),
            AlignItems = FlexAlign.Start, Justify = FlexJustify.End,
            HitTestVisible = expandedInteractive, OpacityGroup = true,
            ScrollBinds = FadeExpanded(), Children = utilityKids.ToArray(),
        };

        Element compactArtwork = new BoxEl
        {
            Width = DetailVerticalLayout.CompactArtworkSize,
            Height = DetailVerticalLayout.CompactArtworkSize,
            Shrink = 0f, ClipToBounds = true,
            Corners = CornerRadius4.All(4f), HitTestVisible = false,
            Children =
            [
                DetailRail.HeroArtwork(m, DetailVerticalLayout.CompactArtworkSize, radius: 4f, connected: false)
            ],
        };
        Element compactPlay = new BoxEl
        {
            ZStack = true,
            Width = DetailVerticalLayout.CompactArtworkSize,
            Height = DetailVerticalLayout.CompactArtworkSize,
            Shrink = 0f, HitTestPassThrough = true,
            Children =
            [
                new BoxEl
                {
                    Width = DetailVerticalLayout.CompactArtworkSize,
                    Height = DetailVerticalLayout.CompactArtworkSize,
                    Corners = CornerRadius4.All(DetailVerticalLayout.CompactArtworkSize * 0.5f),
                    Fill = h.Accent, HitTestVisible = false,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [Icon(Icons.Play, 14f, onAccent)],
                },
                Flow.Show(() => compactInteractive.Value, new BoxEl
                {
                    Width = DetailVerticalLayout.CompactArtworkSize,
                    Height = DetailVerticalLayout.CompactArtworkSize,
                    Corners = CornerRadius4.All(DetailVerticalLayout.CompactArtworkSize * 0.5f),
                    Cursor = CursorId.Hand, Role = AutomationRole.Button, OnClick = h.PlayAll,
                    HoverScale = 1.06f, PressScale = 0.94f,
                }),
            ],
        };
        // CompactReveal drives opacity 0→1; do not also set a literal Opacity here — reconciler would reset it
        // each update and the scroll bind's LastWritten gate could skip rewriting the scrolled-in value.
        Element compactIdentity = new BoxEl
        {
            Direction = 0, Width = viewportW, Height = DetailVerticalLayout.CompactIdentityHeight,
            Padding = new Edges4(compactLeft, 0f, compactLeft, 0f), Gap = Spacing.M,
            AlignItems = FlexAlign.Center, HitTestPassThrough = true, OpacityGroup = true,
            ScrollBinds = CompactRevealBinds(),
            Children =
            [
                compactArtwork,
                new TextEl(m.Title)
                {
                    Grow = 1f, Basis = 0f, MinWidth = 0f,
                    Size = 14f, Weight = 600, Color = Tok.TextPrimary,
                    MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
                new BoxEl { Width = compactSearchWidth, Height = 1f, Shrink = 0f, HitTestVisible = false },
                compactPlay,
            ],
        };

        return ZStack(artworkLayer, copyContrast, expanded, immersiveUtilities, compactIdentity) with { Direction = 1 };
    }

    // White-alpha plates over the art-derived wash — Apple's "accent-aware" circle controls (vibrancy without blur).
    static ColorF ImmersiveGlass => DetailHeroImmersiveGlass.Fill;
    static ColorF ImmersiveGlassHover => DetailHeroImmersiveGlass.Hover;
    static ColorF ImmersiveGlassPress => DetailHeroImmersiveGlass.Press;
    static ColorF ImmersiveGlassStroke => DetailHeroImmersiveGlass.Stroke;

    /// <summary>Apple Music scales the immersive title with string length — short punches (SOS/GUTS) sit near display
    /// size; long album names step down so a 2-line wrap still feels like one title, not a Fluent Title block.</summary>
    static float ImmersiveTitleSize(string title)
    {
        int n = title.Length;
        if (n <= 6) return 42f;
        if (n <= 14) return 34f;
        if (n <= 28) return 28f;
        return 24f;
    }

    static Element? Attribution(DetailModel m, DetailHandlers h, float maxWidth, bool onMedia, Loadable<DetailModel>? full = null)
    {
        // Collaborative playlists get the stacked-avatar facepile here too — the rail renders it via PlaylistOwnerBlock,
        // but the vertical/Hero system replaced the rail, silently dropping the collaborator overlays at every width
        // (user report 2026-07-23). Same predicate as the rail; plain owner text remains the single-owner fallback.
        if (DetailRail.ShowCollaborators(m))
            return Embed.Comp(() => new CollaboratorFacePile(m, maxWidth, full));
        // Immersive artist/owner: Regular/Medium white — clearly below the Bold title, never Semibold competing with it.
        if (m.OwnerName is { Length: > 0 } owner)
            return new TextEl(owner)
            {
                FontFamily = onMedia ? "Segoe UI Variable Text" : null,
                Size = onMedia ? 17f : 12f, Weight = onMedia ? (ushort)400 : (ushort)600,
                LineHeight = onMedia ? 22f : float.NaN,
                Color = onMedia ? Tok.OnMediaSecondary : Tok.TextSecondary,
                MaxWidth = maxWidth, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            };
        if (m.Artists.Count == 0) return null;

        var spans = new TextSpan[m.Artists.Count * 2 - 1];
        int at = 0;
        for (int i = 0; i < m.Artists.Count; i++)
        {
            if (i > 0) spans[at++] = new TextSpan(", ");
            var artist = m.Artists[i];
            spans[at++] = new TextSpan(artist.Name, Weight: onMedia ? (ushort)400 : (ushort)600,
                Color: onMedia ? Tok.OnMediaSecondary : Tok.AccentTextPrimary,
                OnClick: () => h.Go("artist:" + artist.Uri, artist.Name));
        }
        return new SpanTextEl(spans)
        {
            FontFamily = onMedia ? "Segoe UI Variable Text" : null,
            Size = onMedia ? 17f : 12f,
            LineHeight = onMedia ? 22f : float.NaN,
            Color = onMedia ? Tok.OnMediaSecondary : Tok.TextSecondary,
            MaxWidth = maxWidth,
            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
        };
    }

    static Element ActionButton(string glyph, string label, float height, ColorF fill, ColorF fg, Action onClick,
        bool iconOnly = false, bool pill = false, float width = float.NaN,
        ColorF? hoverFill = null, ColorF? pressedFill = null, bool hairline = false, float labelSize = 13f)
    {
        bool subtleBorder = hairline || fill == Tok.FillSubtleSecondary;
        BoxEl button = new()
        {
            Direction = 0, Width = float.IsNaN(width) ? (iconOnly ? height : float.NaN) : width, Height = height,
            Padding = iconOnly ? Edges4.All(0f) : new Edges4(14f, 0f, 16f, 0f), Gap = 6f,
            Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(pill || iconOnly ? height * 0.5f : Radii.Control), Fill = fill,
            HoverFill = hoverFill ?? ColorF.Transparent,
            PressedFill = pressedFill ?? ColorF.Transparent,
            BrushTransitionMs = hoverFill.HasValue ? 100f : 0f,
            BorderWidth = subtleBorder ? 1f : 0f,
            BorderColor = hairline ? ImmersiveGlassStroke : Tok.StrokeControlDefault,
            Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button, OnClick = onClick,
            HoverScale = 1.03f, PressScale = 0.97f,
            Children = iconOnly
                ? [Icon(glyph, 14f, fg)]
                : [Icon(glyph, labelSize + 1f, fg), new TextEl(label) { Size = labelSize, Weight = 600, Color = fg, MaxLines = 1 }],
        };
        return iconOnly ? ToolTip.Wrap(button, label) : button;
    }
}

// Apple's compact add control uses a +/check state rather than Wavee's usual heart. It remains backed by the same
// LibraryBridge mutation, so this is only an immersive visual adaptation—not a second save behavior.
sealed class DetailHeroSaveButton : Component
{
    readonly string _uri;
    readonly string? _name;
    readonly float _size;

    public DetailHeroSaveButton(string uri, string? name, float size)
    { _uri = uri; _name = name; _size = size; }

    public override Element Render()
    {
        var lib = UseContext(LibraryBridge.Slot);
        if (lib is null) return new BoxEl();
        bool saved = lib.IsSaved(_uri);
        string label = Loc.Get(saved ? Strings.Menu.Saved : Strings.Menu.Save);
        Element button = new BoxEl
        {
            Width = _size, Height = _size,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(_size * 0.5f),
            Fill = DetailHeroImmersiveGlass.Fill,
            HoverFill = DetailHeroImmersiveGlass.Hover,
            PressedFill = DetailHeroImmersiveGlass.Press,
            BorderWidth = 1f, BorderColor = DetailHeroImmersiveGlass.Stroke,
            BrushTransitionMs = 100f,
            Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button,
            OnClick = () => lib.ToggleSaved(_uri, _name),
            HoverScale = 1.06f, PressScale = 0.92f,
            Children = [Icon(saved ? Icons.Accept : Icons.Add, 15f, Tok.OnMediaPrimary)],
        };
        return ToolTip.Wrap(button, label);
    }
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
    readonly float _size;
    readonly bool _onMedia;

    public DetailHeroMoreButton(Loadable<DetailModel> full, DetailConfig cfg, DetailHandlers h, float size,
                                bool onMedia = false)
    { _full = full; _cfg = cfg; _h = h; _size = size; _onMedia = onMedia; }

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
                    new IconRef { Glyph = Icons.Add, Font = null },
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

        BoxEl button = new()
        {
            Width = _size, Height = _size, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(_size * 0.5f),
            Fill = _onMedia ? DetailHeroImmersiveGlass.Fill : ColorF.Transparent,
            HoverFill = _onMedia ? DetailHeroImmersiveGlass.Hover : ColorF.Transparent,
            PressedFill = _onMedia ? DetailHeroImmersiveGlass.Press : ColorF.Transparent,
            BorderWidth = _onMedia ? 1f : 0f,
            BorderColor = _onMedia ? DetailHeroImmersiveGlass.Stroke : ColorF.Transparent,
            BrushTransitionMs = _onMedia ? 100f : 0f,
            HoverScale = 1.06f, PressScale = 0.94f,
            Cursor = CursorId.Hand, Role = AutomationRole.Button,
            OnClick = Toggle,
            OnRealized = h => anchor.Value = h,
            Children = [Icon(Icons.More, 16f, _onMedia ? Tok.OnMediaPrimary : Tok.TextSecondary)],
        };
        return _onMedia ? button : button.Interactive(Interaction.Subtle);
    }
}

// Cross-surface page-layout preference epoch: bumped when the Settings → Appearance "Track page layout" row changes,
// so any mounted (incl. KeepAlive-parked) DetailShell re-resolves rail-vs-hero live. (PlayerBarPrefs pattern.)
static class DetailHeroPrefs
{
    public static readonly Signal<int> Epoch = new(0);
    public static void Bump() => Epoch.Value = Epoch.Peek() + 1;
}

/// <summary>Immersive hero circle-control fills: white-alpha over the art wash so buttons read accent-aware
/// (Apple Music vibrancy look) without a backdrop-blur material.</summary>
file static class DetailHeroImmersiveGlass
{
    public static ColorF Fill => ColorF.FromRgba(255, 255, 255) with { A = 0.18f };
    public static ColorF Hover => ColorF.FromRgba(255, 255, 255) with { A = 0.28f };
    public static ColorF Press => ColorF.FromRgba(255, 255, 255) with { A = 0.12f };
    public static ColorF Stroke => ColorF.FromRgba(255, 255, 255) with { A = 0.16f };
}
