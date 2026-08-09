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
// orientation: fixed artwork BESIDE a flexing info column, full-width artwork carrying its identity/actions in the
// lower edge fade, or an ultra-compact 96/64-DIP thumbnail row. The inline cover (desktop or compact), or the immersive
// hero's small lower-edge token, fades out over the final collapse band while compact identity fades/slides in. The
// handoff is scroll-driven and reversible—no threshold snap or attention-grabbing shared-element flight.
static class DetailVerticalHero
{
    static readonly LayoutTransition HeroGeometryMotion = new(
        TransitionChannels.Bounds,
        TransitionDynamics.Tween(280f, Easing.SmoothOut),
        SizeMode.ScaleCorrect);

    static readonly LayoutTransition HeroReflowMotion = new(
        TransitionChannels.Position | TransitionChannels.Size,
        TransitionDynamics.Tween(280f, Easing.SmoothOut),
        SizeMode.Reveal);

    public static Element Build(DetailModel m, DetailConfig cfg, DetailHandlers h, Loadable<DetailModel> full,
                                DetailHeroOrientation o, float artSize, float availW,
                                float compactLeft, float collapseDistance,
                                IReadSignal<bool> compactInteractive,
                                IReadSignal<bool> searchExpanded, IReadSignal<bool> selectionCommandsVisible,
                                Element toolbar, Element compactSearch, Element compactSelection,
                                ActionServices? acts = null)
    {
        bool side = o == DetailHeroOrientation.SideBySide;
        bool compact = o == DetailHeroOrientation.Compact;
        bool immersive = o == DetailHeroOrientation.Immersive;
        bool inlineArtwork = side || compact;
        bool minimal = compact && artSize <= DetailVerticalLayout.MinimalHeroArtworkSize;
        bool editable = m.Capabilities.CanEditMetadata && m.ContextUri is { Length: > 0 };
        bool compactCanHit = compactInteractive.Value;

        // Bucket the available width to 8 DIP before deriving the content width, so the InlineEdit facades' width-folding
        // keys (title/description) don't churn a remount on every sub-pixel resize frame.
        float viewportW = availW > 0f ? availW : DetailVerticalLayout.FallbackW;
        float bw = MathF.Round(viewportW / 8f) * 8f;
        if (bw <= 0f) bw = DetailVerticalLayout.FallbackW;
        float heroPad = compact ? DetailVerticalLayout.CompactHeroPad : DetailVerticalLayout.HeroPad;
        float heroGap = compact ? DetailVerticalLayout.CompactHeroGap : DetailVerticalLayout.HeroGap;
        float pad = 2f * heroPad;
        // Cap the text column: with the "Hero" page layout the hero now renders at ANY width, and an uncapped title/
        // description would sprawl into 150-char lines on a wide window. 640 keeps the measure readable; the block stays
        // leading-aligned (the cap never affects the < 580 vertical band, where the geometry is width-limited anyway).
        float contentW = MathF.Min(640f, MathF.Max(compact ? 80f : 160f,
            inlineArtwork ? bw - pad - artSize - heroGap : bw - pad));
        int descLines = DetailVerticalLayout.DescriptionMaxLines(o);
        Element Fade(Element e) => new BoxEl
        {
            Direction = 1,
            HitTestVisible = true,
            Children = [e],
        };

        // Artwork — a shadowed rounded box; editable playlists get the click-to-change cover facade.
        float fullArtX = inlineArtwork
            ? DetailVerticalLayout.HeroPad
            : 0f;
        float fullArtY = inlineArtwork ? DetailVerticalLayout.HeroPad : 0f;
        // Side-by-side cover stays in flow. No morph key: scroll owns one quiet whole-presentation crossfade.
        int heroDecodePx = immersive ? DetailVerticalLayout.ImmersiveArtworkDecodePx(artSize) : 256;
        Element artworkBox = new BoxEl
        {
            Width = artSize, Height = artSize, Shrink = 0f,
            HitTestVisible = true,
            Corners = CornerRadius4.All(inlineArtwork ? Radii.Card : 0f),
            Shadow = inlineArtwork ? Elevation.Card : default,
            ClipToBounds = true,
            Animate = HeroGeometryMotion,
            // Apple melts the lower ~⅓ of the bitmap into the opaque page wash (longer melt = less hard plate).
            EdgeFade = immersive ? new EdgeFadeSpec(EdgeMask.Bottom, MathF.Min(260f, artSize * 0.34f)) : null,
            TransformOriginX = 0f, TransformOriginY = 0f,
            // The cover drags the whole entity this page is about. On the framing box, not on the editable
            // cover inside it, so that cover's FILE drop target is untouched (see WaveeDetailDrag.Hero).
            Draggable = WaveeDetailDrag.Hero(m, acts),
            Children =
            [
                editable
                    ? PlaylistInlineEdit.Cover(full, artSize, inlineArtwork ? Radii.Card : 0f, shadow: inlineArtwork,
                        morphKey: null, decodePx: heroDecodePx, preferLargest: immersive)
                    // Apple oversaturates album art for a punchier look under the hero scrim — applied in both hero
                    // layouts (immersive/stacked and the wide side-by-side rail).
                    : DetailRail.HeroArtwork(m, artSize, inlineArtwork ? Radii.Card : 0f, connected: false,
                        saturation: 1.18f, morphKey: null, decodePx: heroDecodePx,
                        preferLargest: immersive)
            ],
        };

        var infoKids = new List<Element>(6);

        // Identity: album/single → the type/year eyebrow + billed-artist face pile; playlist → owner/collaborators block.
        // The STRING and the RUN both come from DetailRail (EyebrowText / EyebrowRun) — the rail, the narrow header and
        // this hero must never word or style the same release two ways across a layout cross. Composition below is
        // unchanged: only the fact's authorship moved.
        string eyebrow = DetailRail.EyebrowText(m, cfg);
        // Apple's immersive hierarchy starts directly with the release title. Type/year remains useful in the wider
        // desktop adaptation, but duplicating it above the title on a phone-sized hero pushes the useful copy too high.
        if (side && eyebrow.Length > 0)
            infoKids.Add(Fade(DetailRail.EyebrowRun(eyebrow)));

        // Every arm is a RUNG of the type ramp — the display face and the negative tracking carry the "Apple Music hero"
        // voice, the metrics do not get to invent their own. Compact: BodyLarge 18/24 at the minimal artwork size, else
        // Subtitle 20/28 (was an off-ramp 22). Immersive: TitleLarge 40/52 with one Title 28/36 step for very long
        // names (see ImmersiveTitleSize). Side-by-side: Title 28/36 (was an off-ramp 32).
        float titleSize = compact
            ? artSize <= DetailVerticalLayout.MinimalHeroArtworkSize ? 18f : 20f
            : immersive ? ImmersiveTitleSize(m.Title) : 28f;
        float titleLineHeight = titleSize switch { >= 40f => 52f, >= 28f => 36f, >= 20f => 28f, _ => 24f };
        // 600 everywhere: the ramp publishes 400 and 600 only, and 700 lives exclusively behind the WaveeType display
        // aliases. The display FACE (below) is what keeps the immersive/compact hero from reading as a UI label.
        const ushort titleWeight = 600;
        Element title = editable
            ? PlaylistInlineEdit.Title(full, contentW, titleSize, titleWeight, onMedia: immersive, lineHeight: titleLineHeight)
            : compact
                ? new TextEl(m.Title)
                {
                    FontFamily = "Segoe UI Variable Display",
                    Size = titleSize, Weight = titleWeight,
                    LineHeight = titleLineHeight, CharSpacing = -12f,
                    MaxWidth = contentW,
                    Wrap = TextWrap.WrapWholeWords, MaxLines = 2, Trim = TextTrim.CharacterEllipsis,
                    Color = Tok.TextPrimary,
                }
                : immersive
                ? new TextEl(m.Title)
                {
                    // Display optical size when available (SF Pro Display analogue on Windows).
                    FontFamily = "Segoe UI Variable Display",
                    Size = titleSize, Weight = titleWeight,
                    LineHeight = titleLineHeight, CharSpacing = titleSize >= 34f ? -28f : -16f,
                    MaxWidth = contentW,
                    Wrap = TextWrap.WrapWholeWords, MaxLines = 2, Trim = TextTrim.CharacterEllipsis,
                    Color = Tok.OnMediaPrimary,
                }
                : WaveeType.PageHero(m.Title) with
                {
                    Size = titleSize, MinSize = 18f, Weight = titleWeight, LineHeight = titleLineHeight,
                    Width = contentW, MaxWidth = contentW,
                    Wrap = TextWrap.WrapWholeWords, MaxLines = 3, Trim = TextTrim.CharacterEllipsis,
                    Color = Tok.TextPrimary,
                };
        Element expandedTitle = new BoxEl
        {
            Direction = 1,
            Children = [title],
        };

        Element? attribution = minimal ? null : Attribution(m, h, contentW, immersive, full);

        // Identity copy. Side-by-side keeps the prose-first desktop order; immersive follows the Apple Music stack:
        // tight identity cluster → actions → quieter prose.
        Element? description = null;
        if (!compact && editable)
            description = Fade(PlaylistInlineEdit.Description(full, contentW, descLines, h, onMedia: immersive));
        else if (!compact && m.Description is { Length: > 0 })
            description = Fade(RichText.Expandable(m.Description, immersive ? 13f : 12f,
                immersive ? Tok.OnMediaSecondary : Tok.TextSecondary,
                immersive ? Tok.OnMediaPrimary : Tok.AccentTextPrimary,
                contentW, descLines, m.ContextUri ?? m.Title,
                u => { if (RichText.RouteForUri(u) is { } k) h.Go(k, null); }));

        Element? meta = null;
        if (m.MetaLine is { Length: > 0 })
            meta = immersive
                ? new TextEl(m.MetaLine)
                {
                    Size = 12f, Weight = 400, LineHeight = 16f,
                    Color = Tok.OnMediaSecondary,
                    MaxWidth = contentW, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                }
                : WaveeType.TrackMeta(m.MetaLine) with
                {
                    MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                };

        if (inlineArtwork)
        {
            infoKids.Add(new BoxEl
            {
                Direction = 1, Width = contentW, HitTestVisible = true,
                TransformOriginX = 0f, TransformOriginY = 0f,
                Children = [expandedTitle],
            });
            if (attribution is not null) infoKids.Add(Fade(attribution));
            if (side && description is not null) infoKids.Add(description);
            if (meta is not null) infoKids.Add(Fade(meta));
        }
        else
        {
            // One Fade for the whole identity: title / artist / meta sit in a tight Apple stack (not Fluent 6-DIP list gap).
            var identityKids = new List<Element>(3) { expandedTitle };
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
        float actionSize = compact ? 36f : side ? 32f : 40f;
        // ONE labeled Play across all three orientations: the shared WaveeCta media pill (Button internals — focus ring,
        // automation role, 83ms brush — wearing the capsule, the hand cursor and the scale cue) with only the color axis
        // swapped: the immersive white-on-media pair, or the page's artwork accent plus its resolved ink. The immersive
        // pill declares a transparent stroke: the on-accent elevation ramp is white-alpha and would be invisible on a
        // white fill regardless. Each orientation keeps its own slot (width/height), so the surrounding layout is
        // unchanged — minHeight must be passed too, because Style.MinHeight is a FLOOR that would otherwise raise the
        // 32-DIP side-by-side slot to the pill default.
        Element playButton = WaveeCta.Pill(Loc.Get(Strings.Detail.Play), h.PlayAll,
            palette: immersive
                ? WaveeCta.Palette(Tok.OnMediaPrimary, ColorF.FromRgba(0, 0, 0), GradientSpec.Solid(ColorF.Transparent))
                : WaveeCta.Palette(h.Accent, onAccent),
            glyph: Icons.Play, minHeight: actionSize) with
        {
            Height = actionSize, Shrink = 0f,
            Width = immersive ? 132f : compact ? 92f : float.NaN,
        };
        Element expandedPlay = Fade(playButton);
        var actions = new List<Element>(5);
        if (compact)
        {
            actions.Add(expandedPlay);
            actions.Add(Fade(Embed.Comp(() => new DetailHeroMoreButton(full, cfg, h, actionSize))
                with { Key = $"vhero-more-compact:{m.ContextUri}:{(int)actionSize}" }));
        }
        else if (immersive)
        {
            actions.Add(Fade(ActionButton(Icons.Shuffle, Loc.Get(Strings.Detail.Shuffle), actionSize,
                glass, Tok.OnMediaPrimary, h.Shuffle,
                hoverFill: glassHover, pressedFill: glassPress, hairline: true)));
            actions.Add(expandedPlay);
        }
        else
        {
            actions.Add(expandedPlay);
            // Labeled neutral secondary action → the media pill on the stock Standard ramp verbatim (nothing here is
            // artwork-derived, so there is no palette to preserve): FillControlDefault + ControlElevationBorder, capsule
            // geometry so it ladders with the Play pill beside it rather than reading as a utility rectangle.
            actions.Add(Fade(WaveeCta.Pill(Loc.Get(Strings.Detail.Shuffle), h.Shuffle, ButtonAppearance.Standard,
                    glyph: Icons.Shuffle, minHeight: actionSize)
                with { Height = actionSize, Shrink = 0f }));
        }
        if (!compact && m.ContextUri is { Length: > 0 } saveUri && cfg.Heart != HeartMode.None)
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
            Direction = 0, Gap = compact ? Spacing.S : side ? Spacing.S : 12f, AlignItems = FlexAlign.Center,
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
        Element hero = inlineArtwork
            ? new BoxEl
            {
                Direction = 0, Gap = heroGap, AlignItems = FlexAlign.Start,
                Animate = HeroReflowMotion,
                Children =
                [
                    artworkBox,
                    new BoxEl
                    {
                        Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f,
                        Gap = compact ? Spacing.S : Spacing.M, AlignItems = FlexAlign.Stretch,
                        Children = infoKids.ToArray(),
                    },
                ],
            }
            : new BoxEl
            {
                ZStack = true, Width = viewportW, Height = artSize,
                AlignItems = FlexAlign.Center, ClipToBounds = true,
                Animate = HeroReflowMotion,
                Children =
                [
                    artworkPlaceholder,
                    new BoxEl
                    {
                        Direction = 1, Width = viewportW, Height = artSize,
                        Justify = FlexJustify.End,
                        Padding = new Edges4(
                            DetailVerticalLayout.HeroPad + DetailVerticalLayout.ImmersiveIdentityTokenSize + Spacing.M,
                            DetailVerticalLayout.HeroPad, DetailVerticalLayout.HeroPad, DetailVerticalLayout.HeroPad),
                        // Identity cluster owns its own 3-DIP gap; outer gap only separates major blocks.
                        Gap = 0f, AlignItems = FlexAlign.Start,
                        Children = infoKids.ToArray(),
                    },
                ],
            };

        Element expanded = new BoxEl
        {
            Direction = 1,
            Animate = HeroReflowMotion,
            Children =
            [
                inlineArtwork
                    ? new BoxEl
                    {
                        Direction = 1,
                        Padding = new Edges4(heroPad, heroPad,
                            heroPad, side ? DetailVerticalLayout.SideHeroBottomPad : heroPad),
                        Animate = HeroReflowMotion,
                        Children = [hero],
                    }
                    : hero,
                new BoxEl
                {
                    Direction = 1,
                    Padding = new Edges4(compactLeft,
                        side ? DetailVerticalLayout.SideToolbarTopPad
                            : compact ? 0f : DetailVerticalLayout.ExpandedToolbarTopPad,
                        compactLeft, DetailVerticalLayout.ExpandedToolbarBottomPad),
                    Children = [toolbar],
                },
            ],
        };

        // Immersive art stays a static page-media overlay. Side-by-side already placed artworkBox in-flow.
        Element? artworkLayer = immersive ? new BoxEl
        {
            Direction = 1,
            Padding = new Edges4(fullArtX, fullArtY, 0f, 0f),
            HitTestPassThrough = true,
            Children = [artworkBox],
        } : null;

        static ColorF Scrim(float alpha) => ColorF.FromRgba(0, 0, 0) with { A = alpha };
        // Contrast veil: EdgeFade dissolves the bitmap into the page wash while this layer stays above the artwork and
        // below the copy. It enters before the identity block, reaches full support behind the controls, and retains a
        // quieter lower veil through the seam so bright artwork cannot erase metadata. Cap = 4 GradientSpec stops.
        Element copyContrast = new BoxEl
        {
            Width = viewportW, Height = artSize, HitTestPassThrough = true,
            Gradient = GradientDown(
                new GradientStop(0.38f, Scrim(0f)),
                new GradientStop(0.60f, Scrim(0.58f)),
                new GradientStop(0.82f, Scrim(0.68f)),
                new GradientStop(1f, Scrim(0.52f))),
            OpacityGroup = true,
        };

        var utilityKids = new List<Element>(2);
        if (m.ShareUrl is { Length: > 0 } shareUrl)
            utilityKids.Add(ActionButton(Icons.Share, Loc.Get(Strings.Menu.Share), 36f,
                glass, Tok.OnMediaPrimary,
                () => InputHooks.Current.Default.OpenUri?.Invoke(shareUrl),
                hoverFill: glassHover, pressedFill: glassPress, hairline: true));
        utilityKids.Add(Embed.Comp(() => new DetailHeroMoreButton(full, cfg, h, 36f, onMedia: true))
            with { Key = $"vhero-more-media:{m.ContextUri}" });
        Element immersiveUtilities = new BoxEl
        {
            Direction = 0, Width = viewportW, Height = 60f, Gap = Spacing.S,
            Padding = new Edges4(0f, 12f, 14f, 0f),
            AlignItems = FlexAlign.Start, Justify = FlexJustify.End,
            HitTestVisible = true,
            Children = utilityKids.ToArray(),
        };

        Element immersiveToken = new BoxEl
        {
            Width = DetailVerticalLayout.ImmersiveIdentityTokenSize,
            Height = DetailVerticalLayout.ImmersiveIdentityTokenSize,
            Shrink = 0f, ClipToBounds = true,
            // A 44-DIP art token: Radii.Control (4), not an off-ramp 6. The stroke is the app's card hairline token —
            // a hand-mixed white@0.20 is the same value the token already resolves to on media, minus the theme.
            Corners = Radii.ControlAll, Shadow = Elevation.Card,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            HitTestVisible = false,
            Children =
            [
                DetailRail.HeroArtwork(m, DetailVerticalLayout.ImmersiveIdentityTokenSize,
                    radius: Radii.Control, connected: false, morphKey: null, decodePx: 256)
            ],
        };
        Element immersiveTokenLayer = new BoxEl
        {
            Direction = 1, Width = viewportW, Height = artSize,
            Padding = new Edges4(DetailVerticalLayout.HeroPad, 0f, 0f, DetailVerticalLayout.HeroPad),
            AlignItems = FlexAlign.Start, Justify = FlexJustify.End,
            HitTestPassThrough = true,
            Children = [immersiveToken],
        };

        Element compactArtwork = new BoxEl
        {
            Width = DetailVerticalLayout.CompactArtworkSize,
            Height = DetailVerticalLayout.CompactArtworkSize,
            Shrink = 0f, ClipToBounds = true,
            Corners = CornerRadius4.All(4f), HitTestVisible = false,
            Children =
            [
                DetailRail.HeroArtwork(m, DetailVerticalLayout.CompactArtworkSize, radius: 4f,
                    connected: false, morphKey: null, decodePx: 256)
            ],
        };
        Element compactPlay = new BoxEl
        {
            ZStack = true,
            Width = DetailVerticalLayout.CompactPlaySize,
            Height = DetailVerticalLayout.CompactPlaySize,
            Shrink = 0f, HitTestPassThrough = true,
            Children =
            [
                new BoxEl
                {
                    Width = DetailVerticalLayout.CompactPlaySize,
                    Height = DetailVerticalLayout.CompactPlaySize,
                    Corners = CornerRadius4.All(DetailVerticalLayout.CompactPlaySize * 0.5f),
                    Fill = h.Accent, HitTestVisible = false,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [Icon(Icons.Play, 14f, onAccent)],
                },
                new BoxEl
                {
                    Width = DetailVerticalLayout.CompactPlaySize,
                    Height = DetailVerticalLayout.CompactPlaySize,
                    Corners = CornerRadius4.All(DetailVerticalLayout.CompactPlaySize * 0.5f),
                    HitTestVisible = compactCanHit,
                    Cursor = CursorId.Hand, Role = AutomationRole.Button, OnClick = h.PlayAll,
                    HoverScale = WaveeMotion.ScaleEmphatic.Hover, PressScale = WaveeMotion.ScaleEmphatic.Press,
                },
            ],
        };
        // Search presence swaps this pill in-place; the scroll-bound compact wrapper below owns its reveal and 4-DIP slide.
        string compactMeta = m.OwnerName ?? m.MetaLine ?? eyebrow;
        float compactPillMax = DetailVerticalLayout.CompactPillWidthCap(viewportW);
        float compactTextMax = MathF.Max(80f,
            compactPillMax - DetailVerticalLayout.CompactArtworkSize - Spacing.S - 16f);
        ColorF compactPillFill = ColorF.Lerp(
            Tok.FillSolidSecondary, h.Accent, Tok.Theme == ThemeKind.Dark ? 0.14f : 0.08f);
        Element compactPill = new BoxEl
        {
            Direction = 0, MinWidth = 0f, MaxWidth = compactPillMax,
            Height = DetailVerticalLayout.CompactPillHeight, Shrink = 1f,
            Padding = new Edges4(4f, 4f, 12f, 4f), Gap = Spacing.S,
            AlignItems = FlexAlign.Center,
            Corners = CornerRadius4.All(DetailVerticalLayout.CompactPillHeight * 0.5f),
            Fill = compactPillFill, Shadow = Elevation.Card,
            BorderWidth = 1f, BorderColor = Tok.StrokeSurfaceDefault,
            // Scroll owns the 4-DIP handoff; this house recipe applies only when search replaces the pill.
            HitTestVisible = false, Animate = MotionRecipes.PageFade,
            TransformOriginX = 0f, TransformOriginY = 0.5f,
            Children =
            [
                compactArtwork,
                new BoxEl
                {
                    Direction = 1, MinWidth = 0f, MaxWidth = compactTextMax, Shrink = 1f, Gap = 0f,
                    Children =
                    [
                        // BodyStrong (14/20/600) over Caption (12/16/400) — the same title/meta pair every track row in
                        // the app uses. Was 13/650 over 10/450: three values, none of them on the ramp.
                        Ui.BodyStrong(m.Title) with
                        {
                            MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        },
                        Ui.Caption(compactMeta) with
                        {
                            Color = Tok.TextTertiary,
                            MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        },
                    ],
                },
            ],
        };
        Element compactSearchHost = new BoxEl
        {
            Shrink = 0f,
            Children = [compactSearch],
        };
        Element compactTools = new BoxEl
        {
            Direction = 0, Shrink = 0f, Gap = Spacing.M,
            AlignItems = FlexAlign.Center,
            Children = [compactSearchHost, compactPlay],
        };
        Element normalCompactIdentity = new BoxEl
        {
            Direction = 0, Width = viewportW, Height = DetailVerticalLayout.CompactIdentityHeight,
            Padding = new Edges4(compactLeft, 0f, compactLeft, 0f), Gap = Spacing.M,
            AlignItems = FlexAlign.Center, HitTestPassThrough = true,
            Children =
            [
                Flow.Show(() => !searchExpanded.Value, compactPill),
                new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f, Height = 1f, HitTestVisible = false },
                compactTools,
            ],
        };
        Element selectionCompactIdentity = new BoxEl
        {
            Direction = 1,
            Width = viewportW,
            Height = DetailVerticalLayout.CompactIdentityHeight,
            Padding = new Edges4(compactLeft, 4f, compactLeft, 4f),
            Justify = FlexJustify.Center,
            Children = [compactSelection],
        };
        Element compactIdentityContent = new BoxEl
        {
            ZStack = true,
            Width = viewportW,
            Height = DetailVerticalLayout.CompactIdentityHeight,
            Children =
            [
                Flow.Show(() => !selectionCommandsVisible.Value, normalCompactIdentity),
                Flow.Show(() => selectionCommandsVisible.Value, selectionCompactIdentity),
            ],
        };
        Element compactIdentity = new BoxEl
        {
            ZStack = true, Width = viewportW, Height = DetailVerticalLayout.CompactIdentityHeight,
            HitTestVisible = compactCanHit, HitTestPassThrough = true,
            ScrollBinds =
            [
                new() { From = ScrollChannel.Offset, To = BindSink.Opacity,
                    Range = ScrollRange.Px(DetailVerticalLayout.CompactRevealStart(collapseDistance), collapseDistance),
                    OutStart = 0f, OutEnd = 1f, Ease = Easing.Linear },
                new() { From = ScrollChannel.Offset, To = BindSink.TransY,
                    Range = ScrollRange.Px(DetailVerticalLayout.CompactRevealStart(collapseDistance), collapseDistance),
                    OutStart = Spacing.XS, OutEnd = 0f, Ease = Easing.Linear },
            ],
            Children =
            [
                compactIdentityContent,
            ],
        };

        Element expandedPresentation = (immersive
            ? ZStack(artworkLayer!, copyContrast, expanded, immersiveUtilities, immersiveTokenLayer)
            : ZStack(expanded)) with
        {
            Direction = 1,
            HitTestVisible = !compactCanHit,
            ScrollBinds =
            [
                new() { From = ScrollChannel.Offset, To = BindSink.TransY,
                    Range = ScrollRange.Px(0f, collapseDistance),
                    OutStart = 0f, OutEnd = -collapseDistance, Ease = Easing.Linear },
                new() { From = ScrollChannel.Offset, To = BindSink.Opacity,
                    Range = ScrollRange.Px(DetailVerticalLayout.ExpandedFadeStart(collapseDistance), collapseDistance),
                    OutStart = 1f, OutEnd = 0f, Ease = Easing.Linear },
            ],
        };
        return ZStack(expandedPresentation, compactIdentity) with { Direction = 1 };
    }

    // White-alpha plates over the art-derived wash — Apple's "accent-aware" circle controls (vibrancy without blur).
    static ColorF ImmersiveGlass => DetailHeroImmersiveGlass.Fill;
    static ColorF ImmersiveGlassHover => DetailHeroImmersiveGlass.Hover;
    static ColorF ImmersiveGlassPress => DetailHeroImmersiveGlass.Press;
    static ColorF ImmersiveGlassStroke => DetailHeroImmersiveGlass.Stroke;

    // Past this many characters even a 2-line 40px block overruns the hero on a narrow window.
    const int ImmersiveTitleStepDownChars = 40;

    /// <summary>The immersive hero title's rung. It is TitleLarge (40/52) — one size, so every album's hero opens at the
    /// same typographic weight instead of at one of four length-derived off-ramp sizes (42/34/28/24, none of which was a
    /// rung of anything). The wrap cap (2 lines) + character ellipsis on the run is what keeps a long name from
    /// swallowing the hero, which is the job the old step-down was doing badly.
    /// <para>ONE fallback survives: a very long name steps to Title (28/36) — the ADJACENT rung, not a new size.</para></summary>
    static float ImmersiveTitleSize(string title) =>
        title.Length <= ImmersiveTitleStepDownChars ? 40f : 28f;

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

    /// <summary>An ICON-ONLY circular affordance over media (the immersive Shuffle, the artwork-edge Share) — round
    /// geometry plus the scale cue is the whole point of these, so they stay hand-rolled and tooltip-labelled. Every
    /// LABELED CTA on this surface goes through the shared <see cref="WaveeCta"/> pill (stock Button internals) instead
    /// — see <c>playButton</c>.</summary>
    static Element ActionButton(string glyph, string label, float size, ColorF fill, ColorF fg, Action onClick,
        ColorF? hoverFill = null, ColorF? pressedFill = null, bool hairline = false)
    {
        bool subtleBorder = hairline || fill == Tok.FillSubtleSecondary;
        BoxEl button = new()
        {
            Direction = 0, Width = size, Height = size,
            Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(size * 0.5f), Fill = fill,
            HoverFill = hoverFill ?? ColorF.Transparent,
            PressedFill = pressedFill ?? ColorF.Transparent,
            BrushTransitionMs = hoverFill.HasValue ? WaveeMotion.Faster : 0f,
            BorderWidth = subtleBorder ? 1f : 0f,
            BorderColor = hairline ? ImmersiveGlassStroke : Tok.StrokeControlDefault,
            Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button, OnClick = onClick,
            HoverScale = WaveeMotion.ScaleStandard.Hover, PressScale = WaveeMotion.ScaleStandard.Press,
            Children = [Icon(glyph, 14f, fg)],
        };
        // Static helper — WrapStable needs a mount-stable Func; callers that churn rebuild this tree each render.
        return ToolTip.Wrap(button, label);
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
        var libRef = UseRef(lib);
        var savedRef = UseRef(false);
        libRef.Value = lib;
        var factory = UseMemo(() => (Func<Element>)(() => new BoxEl
        {
            Width = _size, Height = _size,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(_size * 0.5f),
            Fill = DetailHeroImmersiveGlass.Fill,
            HoverFill = DetailHeroImmersiveGlass.Hover,
            PressedFill = DetailHeroImmersiveGlass.Press,
            BorderWidth = 1f, BorderColor = DetailHeroImmersiveGlass.Stroke,
            BrushTransitionMs = WaveeMotion.Faster,
            Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button,
            OnClick = () => libRef.Value?.ToggleSaved(_uri, _name),
            HoverScale = WaveeMotion.ScaleEmphatic.Hover, PressScale = WaveeMotion.ScaleEmphatic.Press,
            Children = [Icon(savedRef.Value ? Icons.Accept : Icons.Add, 15f, Tok.OnMediaPrimary)],
        }), DepKey.Empty);
        if (lib is null) return new BoxEl();
        bool saved = lib.IsSaved(_uri);
        savedRef.Value = saved;
        string label = Loc.Get(saved ? Strings.Menu.Saved : Strings.Menu.Save);
        return ToolTip.WrapStable(factory, label);
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
            BrushTransitionMs = _onMedia ? WaveeMotion.Faster : 0f,
            HoverScale = WaveeMotion.ScaleEmphatic.Hover, PressScale = WaveeMotion.ScaleEmphatic.Press,
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
