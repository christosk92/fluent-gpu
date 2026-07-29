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
// hero's small lower-edge token, morphs into the shy header; the full-bleed immersive media itself stays static.
static class DetailVerticalHero
{
    // The pill's EXIT is the search box's expand seen from the other side: opening search unmounts the identity pill
    // while the field grows across the same row. Running it on the field's own duration and easing keeps that one
    // motion instead of a 150ms snap racing a 260ms growth. (The same leg also covers scrolling back up, where a
    // slightly softer fade reads fine.)
    static readonly LayoutTransition CompactPillPresence = new(
        TransitionChannels.Opacity,
        TransitionDynamics.Tween(280f, Easing.FluentDecelerate),
        Enter: new EnterExit(Dy: 3f, Opacity: 0f, Active: true),
        Exit: new EnterExit(Dy: -2f, Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(TrackList.SearchExpandMs, Easing.SmoothOut),
        DelayMs: 80f,
        ExitDelayMs: 0f);

    static readonly LayoutTransition CompactToolPresence = new(
        TransitionChannels.Opacity,
        TransitionDynamics.Tween(220f, Easing.FluentDecelerate),
        Enter: new EnterExit(Dy: 2f, Opacity: 0f, Active: true),
        Exit: new EnterExit(Dy: -1f, Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(120f, Easing.FluentAccelerate),
        DelayMs: 24f,
        ExitDelayMs: 0f);

    static readonly MotionTokenDef ExpandedCrossfade =
        MotionTokenDef.Eased(210f, Easing.FluentStandard, ReducedMotionPolicy.KeepFade);

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
                                float compactLeft, IReadSignal<bool> collapsed,
                                IReadSignal<bool> compactInteractive, IReadSignal<bool> toolsVisible,
                                IReadSignal<bool> searchExpanded, IReadSignal<bool> selectionCommandsVisible,
                                string morphKey, Element toolbar, Element compactSearch, Element compactSelection)
    {
        bool side = o == DetailHeroOrientation.SideBySide;
        bool compact = o == DetailHeroOrientation.Compact;
        bool immersive = o == DetailHeroOrientation.Immersive;
        bool inlineArtwork = side || compact;
        bool minimal = compact && artSize <= DetailVerticalLayout.MinimalHeroArtworkSize;
        bool editable = m.Capabilities.CanEditMetadata && m.ContextUri is { Length: > 0 };

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
        // Side-by-side: pin Opacity/Trans to identity and morph in place. (In-flow cover — no overlay.)
        // connected:false avoids a Hero-fly dest that can leave the slot empty if the fly handoff glitches.
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
            Children =
            [
                editable
                    ? PlaylistInlineEdit.Cover(full, artSize, inlineArtwork ? Radii.Card : 0f, shadow: inlineArtwork,
                        morphKey: inlineArtwork ? morphKey : null, decodePx: heroDecodePx, preferLargest: immersive)
                    // Apple oversaturates album art for a punchier look under the hero scrim — applied in both hero
                    // layouts (immersive/stacked and the wide side-by-side rail).
                    : DetailRail.HeroArtwork(m, artSize, inlineArtwork ? Radii.Card : 0f, connected: false,
                        saturation: 1.18f, morphKey: inlineArtwork ? morphKey : null, decodePx: heroDecodePx,
                        preferLargest: immersive)
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
        float titleSize = compact
            ? artSize <= DetailVerticalLayout.MinimalHeroArtworkSize ? 18f : 22f
            : immersive ? ImmersiveTitleSize(m.Title) : 32f;
        ushort titleWeight = immersive || compact ? (ushort)700 : (ushort)600;
        Element title = editable
            ? PlaylistInlineEdit.Title(full, contentW, titleSize, titleWeight, onMedia: immersive)
            : compact
                ? new TextEl(m.Title)
                {
                    FontFamily = "Segoe UI Variable Display",
                    Size = titleSize, Weight = titleWeight,
                    LineHeight = titleSize * 1.08f, CharSpacing = -12f,
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
        // Immersive Play is a plain white pill in the Apple 3-control row (no morph slot → no 48-DIP wrapper offset).
        // Side-by-side keeps the morphing Play that compositor-transforms into the compact header control.
        Element playButton = ActionButton(Icons.Play, Loc.Get(Strings.Detail.Play), actionSize,
            immersive ? Tok.OnMediaPrimary : h.Accent,
            immersive ? ColorF.FromRgba(0, 0, 0) : onAccent,
            h.PlayAll, pill: immersive || compact,
            width: immersive ? 132f : compact ? 92f : float.NaN,
            labelSize: immersive ? 15f : 13f);
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
                glass, Tok.OnMediaPrimary, h.Shuffle, iconOnly: true, pill: true,
                hoverFill: glassHover, pressedFill: glassPress, hairline: true)));
            actions.Add(expandedPlay);
        }
        else
        {
            actions.Add(expandedPlay);
            actions.Add(Fade(ActionButton(Icons.Shuffle, Loc.Get(Strings.Detail.Shuffle), actionSize,
                Tok.FillSubtleSecondary, Tok.TextPrimary, h.Shuffle)));
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
                    Flow.Show(() => !collapsed.Value, artworkBox, artworkPlaceholder),
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
            // Keep the measured tree mounted so the virtual prefix height cannot collapse into a blank band. One
            // equality-gated compositor binding hides the whole expanded presentation at the identity edge.
            Opacity = Prop.Of(() => collapsed.Value ? 0f : 1f),
            Transition = ExpandedCrossfade,
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
            OpacityGroup = true,
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
            HitTestVisible = true,
            Opacity = Prop.Of(() => collapsed.Value ? 0f : 1f),
            Transition = ExpandedCrossfade,
            Children = utilityKids.ToArray(),
        };

        Element immersiveToken = new BoxEl
        {
            Width = DetailVerticalLayout.ImmersiveIdentityTokenSize,
            Height = DetailVerticalLayout.ImmersiveIdentityTokenSize,
            Shrink = 0f, ClipToBounds = true,
            Corners = CornerRadius4.All(6f), Shadow = Elevation.Card,
            BorderWidth = 1f, BorderColor = ColorF.FromRgba(255, 255, 255) with { A = 0.20f },
            HitTestVisible = false,
            Children =
            [
                DetailRail.HeroArtwork(m, DetailVerticalLayout.ImmersiveIdentityTokenSize,
                    radius: 6f, connected: false, morphKey: morphKey, decodePx: 256)
            ],
        };
        Element immersiveTokenPlaceholder = new BoxEl
        {
            Width = DetailVerticalLayout.ImmersiveIdentityTokenSize,
            Height = DetailVerticalLayout.ImmersiveIdentityTokenSize,
            Shrink = 0f, HitTestVisible = false,
        };
        Element immersiveTokenLayer = new BoxEl
        {
            Direction = 1, Width = viewportW, Height = artSize,
            Padding = new Edges4(DetailVerticalLayout.HeroPad, 0f, 0f, DetailVerticalLayout.HeroPad),
            AlignItems = FlexAlign.Start, Justify = FlexJustify.End,
            HitTestPassThrough = true,
            Children = [Flow.Show(() => !collapsed.Value, immersiveToken, immersiveTokenPlaceholder)],
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
                    connected: false, morphKey: morphKey, decodePx: 256)
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
                Flow.Show(() => compactInteractive.Value, new BoxEl
                {
                    Width = DetailVerticalLayout.CompactPlaySize,
                    Height = DetailVerticalLayout.CompactPlaySize,
                    Corners = CornerRadius4.All(DetailVerticalLayout.CompactPlaySize * 0.5f),
                    Cursor = CursorId.Hand, Role = AutomationRole.Button, OnClick = h.PlayAll,
                    HoverScale = 1.06f, PressScale = 0.94f,
                }),
            ],
        };
        // Presence owns the pill fade/offset; the connected overlay independently carries the artwork between endpoints.
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
            HitTestVisible = false, Animate = CompactPillPresence,
            TransformOriginX = 0f, TransformOriginY = 0.5f,
            Children =
            [
                compactArtwork,
                new BoxEl
                {
                    Direction = 1, MinWidth = 0f, MaxWidth = compactTextMax, Shrink = 1f, Gap = 0f,
                    Children =
                    [
                        new TextEl(m.Title)
                        {
                            Size = 13f, Weight = 650, Color = Tok.TextPrimary,
                            MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        },
                        new TextEl(compactMeta)
                        {
                            Size = 10f, Weight = 450, Color = Tok.TextTertiary,
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
            AlignItems = FlexAlign.Center, Animate = CompactToolPresence,
            Children = [compactSearchHost, compactPlay],
        };
        Element normalCompactIdentity = new BoxEl
        {
            Direction = 0, Width = viewportW, Height = DetailVerticalLayout.CompactIdentityHeight,
            Padding = new Edges4(compactLeft, 0f, compactLeft, 0f), Gap = Spacing.M,
            AlignItems = FlexAlign.Center, HitTestPassThrough = true,
            Children =
            [
                Flow.Show(() => collapsed.Value && !searchExpanded.Value, compactPill),
                new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f, Height = 1f, HitTestVisible = false },
                Flow.Show(() => toolsVisible.Value, compactTools),
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
            HitTestPassThrough = true,
            Children =
            [
                // Once compact, swallow clicks in the transparent gaps so the still-mounted faded controls cannot
                // receive input. This node remains inside the scroller, so wheel/touch routing still reaches it.
                Flow.Show(() => collapsed.Value, new BoxEl
                {
                    Width = viewportW, Height = DetailVerticalLayout.CompactIdentityHeight,
                }),
                compactIdentityContent,
            ],
        };

        return immersive
            ? ZStack(artworkLayer!, copyContrast, expanded,
                immersiveUtilities, immersiveTokenLayer, compactIdentity) with { Direction = 1 }
            : ZStack(expanded, compactIdentity) with { Direction = 1 };
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
