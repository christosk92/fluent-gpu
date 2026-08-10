using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core;

namespace Wavee;

/// <summary>
/// The stage's LEFT region: identity + transport, bottom-anchored in a fixed 352-DIP column on the wide stage and folded
/// into a single header ROW below <see cref="StageLayout.WideEnterW"/>.
///
/// <para><b>One structure, one reflow flag.</b> Both shapes are this one component reading
/// <see cref="StageLayout.Wide"/> — the detail hero's rule. Nothing here re-derives a breakpoint; every size comes off
/// the layout struct, and the folded-control set is <see cref="StageLayout.Folded"/> (shuffle, repeat, volume and the
/// output-device line move into the compact header's "…" — they are never lost).</para>
///
/// <para><b>Everything is a reuse.</b> The seek is the bar's own <c>SeekBar</c> (its Slider derivation is untouched —
/// the stage does not fork it), the times are the bar's <c>TimeText</c> in on-media ink, the volume is
/// <c>Slider.Create</c> bound to the same signal, the device line reads the same
/// <c>PlayerBarContent.RemoteDevice</c>/<c>LocalOutputs</c> pair <c>RemoteDeviceLine</c> does and opens the same
/// <c>DevicePickerMenu</c>, and every transport intent is the bridge command the bar already calls.</para>
///
/// <para><b>The quality badge is absent, deliberately.</b> The stage's times row was specified with a centre badge
/// naming the playing stream ("LOSSLESS" / "320"). The pipeline computes exactly that — <c>PlaybackEvent</c> carries
/// <c>SelectedBitrateKbps</c> + <c>AudioFormatName</c> — but nothing PUBLISHES it: the only subscriber is Spotify
/// telemetry (<c>RawCoreStreamProjection</c>), and neither <c>IPlaybackState</c> nor <c>PlaybackBridge</c> carries a
/// format/bitrate signal. The alternatives reachable from here are the user's quality PREFERENCE (not what is playing)
/// and <c>ITrackExpansionService</c>'s per-track format LADDER (what the track HAS, async, and its
/// <c>SetFormatOverride</c> is dead-ended). Both would be a badge that says something other than what it claims, so the
/// slot mounts NOTHING rather than a plausible lie.</para>
/// </summary>
sealed class StageIdentity : Component
{
    readonly IReadSignal<StageLayout> _layout;

    /// <summary>The stage title's rung: 22 / 28 in the DISPLAY face at 650. It is a masthead, not a UI label — the same
    /// argument <c>WaveeType</c> makes for its three display-face 700s — and it is authored here rather than as a
    /// <c>WaveeType</c> alias because exactly one surface in the app speaks it.</summary>
    const float TitleSize = 22f, TitleLine = 28f;
    const ushort TitleWeight = 650;
    const string DisplayFace = "Segoe UI Variable Display";

    const float CompactTitleSize = 16f, CompactTitleLine = 22f;

    /// <summary>The column's internal gutter: 352 − 2 × 24 = 304, which carries the 300 cover with a hairline to spare.</summary>
    const float ColumnPadX = 24f;
    const float ColumnPadBottom = 28f;
    const float StackGap = 18f;
    const float TransportGap = 6f;
    const float VolumeThickness = 20f;
    const float VolumeGlyph = 15f;

    public StageIdentity(IReadSignal<StageLayout> layout) => _layout = layout;

    public override Element Render()
    {
        var L = _layout.Value;                       // coarse band signal — not a per-pixel viewport subscription
        var b = UseContext(PlaybackBridge.Slot);
        var lib = UseContext(LibraryBridge.Slot);
        var go = UseContext(HistoryStore.NavCtx);
        var acts = UseContext(ActionServices.Slot);
        var menuOverlay = UseContext(Overlay.Service);

        if (b is null) return new BoxEl { Width = L.LayoutWidth, HitTestVisible = false };

        var track = b.CurrentTrack.Value;
        bool playing = b.IsPlaying.Value;
        bool loading = b.IsLoading.Value;
        string? err = b.Error.Value;
        bool shuffle = b.IsShuffle.Value;
        var repeat = b.Repeat.Value;
        bool saved = track is not null && (lib?.IsSaved(track.Uri) ?? false);
        bool canTransport = track is not null && err is null;
        bool primaryEnabled = track is not null && !loading;
        var accent = StageChrome.AccentFor(track);

        Action? like = track is { Uri.Length: > 0 } lt && lib is not null
            ? () => lib.ToggleSaved(lt.Uri, lt.Title)
            : null;

        Element body = L.Wide
            ? WideColumn(L, b, track, playing, canTransport, primaryEnabled, shuffle, repeat, saved, like, accent, go)
            : CompactHeader(L, b, track, playing, canTransport, primaryEnabled, shuffle, repeat, saved, like, accent, go);

        // Right-click / Menu key anywhere on the identity region — and the "⋯" button's ClickRequestsContext — raise the
        // SAME now-playing menu the player bar's cluster does. The factory Peeks at open time so it never serves the
        // render-time track capture.
        if (acts is { } a && menuOverlay is { } svc && body is BoxEl box)
            body = box.WithContextMenu(svc, () =>
                b.CurrentTrack.Peek() is { } now ? Menus.NowPlaying(a, now) : (ContextMenuModel?)null);

        return body;
    }

    // ── WIDE: the fixed, bottom-anchored column ──────────────────────────────────────────────────────────────────────

    BoxEl WideColumn(in StageLayout L, PlaybackBridge b, Track? track, bool playing, bool canTransport,
                     bool primaryEnabled, bool shuffle, RepeatMode repeat, bool saved, Action? like, ColorF accent,
                     Action<string, string?>? go)
    {
        // The cascade index. The COVER is deliberately not on it: a 300-square that rises and fades reads as the whole
        // surface arriving late, where the type and the controls arriving in sequence reads as the surface composing
        // itself. (WaveeEntrance.Row is capped and reduced-motion-safe by construction.)
        int rung = 0;
        var kids = new List<Element>(7)
        {
            new BoxEl
            {
                Key = "stage:art",
                Width = L.ArtSize, Height = L.ArtSize, Shrink = 0f,
                Corners = Radii.CardAll,
                Shadow = Elevation.Dialog,
                Margin = new Edges4(0f, 0f, 0f, StackGap),
                Children = [Surfaces.Artwork(track?.Image, SeedOf(track), L.ArtSize, L.ArtSize, Radii.Card, decodePx: 512)],
            },
            new BoxEl
            {
                Key = "stage:identity-row", Animate = WaveeEntrance.Row(rung++),
                Children = [IdentityRow(track, saved, like, accent, go, wide: true)],
            },
            new BoxEl
            {
                Key = "stage:seek", Animate = WaveeEntrance.Row(rung++),
                Margin = new Edges4(0f, StackGap, 0f, 0f),
                Children = [SeekBlock(b)],
            },
            new BoxEl
            {
                Key = "stage:transport", Animate = WaveeEntrance.Row(rung++),
                Margin = new Edges4(0f, Spacing.S, 0f, 0f),
                Children = [TransportRow(in L, b, playing, canTransport, primaryEnabled, shuffle, repeat, accent)],
            },
        };
        if (L.ShowVolume)
            kids.Add(new BoxEl
            {
                Key = "stage:volume", Animate = WaveeEntrance.Row(rung++),
                Margin = new Edges4(0f, StackGap, 0f, 0f),
                Children = [VolumeRow(b)],
            });
        if (L.ShowDeviceLine)
            kids.Add(new BoxEl
            {
                Key = "stage:device", Animate = WaveeEntrance.Row(rung),
                Margin = new Edges4(0f, Spacing.S, 0f, 0f),
                Children = [Embed.Comp(() => new StageDeviceLine(b))],
            });

        return new BoxEl
        {
            // The BOX is the designed column plus its veil falloff; the padding puts the content back inside the
            // designed 352 so the gradient has somewhere to fade that is not on top of the type.
            Width = L.LayoutWidth, Shrink = 0f,
            Direction = 1, Justify = FlexJustify.End, MinHeight = 0f,
            Padding = new Edges4(ColumnPadX, 0f, L.ColumnFalloff + ColumnPadX, ColumnPadBottom),
            Gradient = StageChrome.ColumnVeil(L.VeilHoldStop),
            Children = kids.ToArray(),
        };
    }

    // ── COMPACT: the header row + a full-width seek under it ─────────────────────────────────────────────────────────
    // The seek is NOT on the fold list (StageLayout.CompactFold folds shuffle, repeat, volume and the device line, and
    // nothing else): a now-playing surface you cannot scrub is not a now-playing surface.
    BoxEl CompactHeader(in StageLayout L, PlaybackBridge b, Track? track, bool playing, bool canTransport,
                        bool primaryEnabled, bool shuffle, RepeatMode repeat, bool saved, Action? like, ColorF accent,
                        Action<string, string?>? go)
    {
        var row = new List<Element>(7)
        {
            new BoxEl
            {
                Key = "stage:art",
                Width = L.ArtSize, Height = L.ArtSize, Shrink = 0f,
                Corners = Radii.ControlAll, Shadow = Elevation.Card,
                Children = [Surfaces.Artwork(track?.Image, SeedOf(track), L.ArtSize, L.ArtSize, Radii.Control, decodePx: 192)],
            },
            IdentityRow(track, saved, like, accent, go, wide: false) with { Key = "stage:identity-row" },
            StageChrome.Glyph(Icons.Previous, () => { _ = b.Player.PreviousAsync(); }, L.StepBox, 15f, canTransport)
                with { Key = "stage:prev" },
            StageChrome.Play(playing ? Icons.Pause : Icons.Play, () => PlayerBarContent.TogglePlayPause(b),
                primaryEnabled, L.PlayBox, 17f) with { Key = "stage:play" },
            StageChrome.Glyph(Icons.Next, () => { _ = b.Player.NextAsync(); }, L.StepBox, 15f, canTransport)
                with { Key = "stage:next" },
        };
        if (L.ShowOverflow)
            row.Add(Embed.Comp(() => new StageOverflowButton(b, _layout)) with { Key = "stage:overflow" });

        return new BoxEl
        {
            Direction = 1, Shrink = 0f,
            Padding = new Edges4(Spacing.L, Spacing.S, Spacing.L, Spacing.S),
            Gradient = StageChrome.HeaderVeil(),
            Children =
            [
                new BoxEl
                {
                    Key = "stage:header-row", Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                    Animate = WaveeEntrance.Row(0),
                    Children = row.ToArray(),
                },
                new BoxEl
                {
                    Key = "stage:seek", Animate = WaveeEntrance.Row(1),
                    Margin = new Edges4(0f, Spacing.XS, 0f, 0f),
                    Children = [SeekBlock(b)],
                },
            ],
        };
    }

    // ── the identity row: title · artist — album · heart · "…" ───────────────────────────────────────────────────────

    Element IdentityRow(Track? track, bool saved, Action? like, ColorF accent, Action<string, string?>? go, bool wide)
    {
        string title = track is { Title.Length: > 0 } t && t.Title != t.Uri ? t.Title : Loc.Get(Strings.Player.NothingPlaying);

        var meta = new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center, Gap = Spacing.XXS,
            ClipToBounds = true,
            Children =
            [
                new TextEl(title)
                {
                    Size = wide ? TitleSize : CompactTitleSize,
                    LineHeight = wide ? TitleLine : CompactTitleLine,
                    Weight = TitleWeight, FontFamily = DisplayFace,
                    Color = WaveeOnMedia.Ink,
                    Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                },
                Embed.Comp(() => new StageMetaLink(go)),
            ],
        };

        var kids = new List<Element>(3) { meta, StageChrome.Heart(saved, like, accent) };
        if (wide)
            kids.Add(new BoxEl
            {
                Width = WaveeCta.IconButtonSize, Height = WaveeCta.IconButtonSize, Shrink = 0f,
                Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = Radii.ControlAll,
                Fill = WaveeOnMedia.GlassRest, HoverFill = WaveeOnMedia.GlassHover, PressedFill = WaveeOnMedia.GlassPressed,
                BrushTransitionMs = WaveeMotion.Faster,
                Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, AllowFocusOnInteraction = false,
                // The engine's declarative re-entry into the context funnel: the ancestor's WithContextMenu opens
                // byte-identically to a right-click (no node capture, no second menu model).
                ClickRequestsContext = true,
                Children = [new TextEl(Icons.More) { Size = 16f, FontFamily = Theme.IconFont, Color = WaveeOnMedia.InkSecondary, HoverColor = WaveeOnMedia.Ink }],
            });

        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f,
            Grow = wide ? 0f : 1f, Basis = wide ? float.NaN : 0f, Shrink = 1f,
            Children = kids.ToArray(),
        };
    }

    // ── the seek block: the bar's own SeekBar + the elapsed/remaining pair, in on-media ink ───────────────────────────
    // The times row's CENTRE is empty by design — see the class header on the quality badge.

    static Element SeekBlock(PlaybackBridge b) => new BoxEl
    {
        Direction = 1, Gap = Spacing.XXS, MinWidth = 0f,
        Children =
        [
            Embed.Comp(() => new SeekBar(b)),
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Children =
                [
                    Embed.Comp(() => new TimeText(b, remaining: false, ink: WaveeOnMedia.InkTertiary)),
                    new BoxEl { Grow = 1f, MinWidth = 0f, HitTestVisible = false },
                    Embed.Comp(() => new TimeText(b, remaining: true, ink: WaveeOnMedia.InkTertiary)),
                ],
            },
        ],
    };

    // ── the transport row: 32 · 40 · 56 · 40 · 32, centred ───────────────────────────────────────────────────────────

    static Element TransportRow(in StageLayout L, PlaybackBridge b, bool playing, bool canTransport, bool primaryEnabled,
                                bool shuffle, RepeatMode repeat, ColorF accent)
    {
        var kids = new List<Element>(5);
        if (L.ShowSatellites)
            kids.Add(ToolTip.Wrap(
                StageChrome.Satellite(Icons.Shuffle, () => PlayerBarContent.ToggleShuffle(b), canTransport, shuffle,
                    accent, L.SatelliteBox, 14f),
                Loc.Get(Strings.Player.Shuffle)) with { Key = "tp:shuffle" });
        kids.Add(ToolTip.Wrap(
            StageChrome.Glyph(Icons.Previous, () => { _ = b.Player.PreviousAsync(); }, L.StepBox, 17f, canTransport),
            Loc.Get(Strings.Player.Previous)) with { Key = "tp:prev" });
        kids.Add(StageChrome.Play(playing ? Icons.Pause : Icons.Play, () => PlayerBarContent.TogglePlayPause(b),
            primaryEnabled, L.PlayBox, 22f) with { Key = "tp:play" });
        kids.Add(ToolTip.Wrap(
            StageChrome.Glyph(Icons.Next, () => { _ = b.Player.NextAsync(); }, L.StepBox, 17f, canTransport),
            Loc.Get(Strings.Player.Next)) with { Key = "tp:next" });
        if (L.ShowSatellites)
            kids.Add(ToolTip.Wrap(
                StageChrome.Satellite(repeat == RepeatMode.Track ? Icons.RepeatOne : Icons.RepeatAll,
                    () => PlayerBarContent.CycleRepeat(b), canTransport, repeat != RepeatMode.Off, accent,
                    L.SatelliteBox, 14f),
                Loc.Get(Strings.Player.Repeat)) with { Key = "tp:repeat" });

        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = TransportGap,
            Children = kids.ToArray(),
        };
    }

    // ── the volume row: the STOCK slider, recoloured onto the on-media ladder ────────────────────────────────────────

    static Element VolumeRow(PlaybackBridge b) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f,
        Children =
        [
            Embed.Comp(() => new StageVolumeGlyph(b)),
            new BoxEl
            {
                Grow = 1f, Shrink = 1f, MinWidth = 0f,
                Children =
                [
                    Slider.Create(b.Volume, v => { _ = b.Player.SetVolumeAsync(v); },
                        options: VolumeOptions, length: float.NaN, thickness: VolumeThickness, style: OnMediaSlider()),
                ],
            },
        ],
    };

    static readonly Slider.SliderOptions VolumeOptions = new()
    {
        ThumbToolTipValueConverter = static value => $"{Math.Clamp((int)MathF.Round(value * 100f), 0, 100)}%",
    };

    /// <summary>The stock <see cref="Slider.DefaultStyle"/> with its ink moved onto the on-media ladder — a RECOLOUR,
    /// not a fork: every metric (track height, thumb ring, the inner-dot storyboard scales) stays exactly where WinUI
    /// put it, and the seek bar's own derivation is untouched.</summary>
    static Slider.Style OnMediaSlider()
    {
        var s = Slider.DefaultStyle;
        ColorF ink = WaveeOnMedia.Ink;
        return s with
        {
            RailFill = ink with { A = 0.26f },
            RailFillDisabled = ink with { A = 0.14f },
            ValueFill = ink,
            ValueFillPointerOver = ink with { A = 0.90f },
            ValueFillPressed = ink with { A = 0.80f },
            ValueFillDisabled = ink with { A = 0.32f },
            ThumbRing = ink,
            ThumbFill = ink,
            ThumbFillPointerOver = ink with { A = 0.90f },
            ThumbFillPressed = ink with { A = 0.80f },
            ThumbFillDisabled = ink with { A = 0.32f },
            ThumbBorder = GradientSpec.Solid(WaveeOnMedia.Stroke),
        };
    }

    static int SeedOf(Track? t) => t is null ? 11 : Math.Abs((t.Uri ?? t.Id).Length * 7 + t.Title.Length);

    /// <summary>The mute glyph beside the volume rail. Its OWN component so the 0-crossing glyph swap does not
    /// re-render the whole column during a volume drag (the rail beside it is compositor-only).</summary>
    sealed class StageVolumeGlyph : Component
    {
        readonly PlaybackBridge _b;
        public StageVolumeGlyph(PlaybackBridge b) => _b = b;

        public override Element Render()
        {
            float v = _b.Volume.Value;
            bool muted = _b.OutputMuted.Value || v <= 0.001f;
            return ToolTip.Wrap(
                StageChrome.Glyph(muted ? Icons.Mute : Icons.Volume, () => VolumeButton.ToggleMute(_b),
                    WaveeCta.IconButtonSize, VolumeGlyph),
                Loc.Get(muted ? Strings.Player.Unmute : Strings.Player.Mute));
        }
    }

    /// <summary>"Artist — Album" on one line: a hover-UNDERLINED link to the artist page. Its own component because the
    /// hover has to scope to this word rather than to the identity row (the hover-container trap), and because the
    /// underline is a per-render <c>TextEl.Underline</c> rather than a bound channel.</summary>
    sealed class StageMetaLink : Component
    {
        readonly Action<string, string?>? _go;
        public StageMetaLink(Action<string, string?>? go) => _go = go;

        public override Element Render()
        {
            var b = UseContext(PlaybackBridge.Slot);
            var hover = UseSignal(false);
            var track = b?.CurrentTrack.Value;
            if (track is null) return new BoxEl { Height = 0f, HitTestVisible = false };

            string artists = track.Artists.Count > 0 ? DetailFormat.ArtistNames(track.Artists) : "";
            string album = track.Album is { Name.Length: > 0 } al ? al.Name : "";
            string line = artists.Length > 0 && album.Length > 0 ? artists + " — " + album
                        : artists.Length > 0 ? artists
                        : album;
            if (line.Length == 0) return new BoxEl { Height = 0f, HitTestVisible = false };

            // Resolve the route at INVOKE time: this node outlives every track change.
            bool enabled = track.Artists.Count > 0 && track.Artists[0].Uri.Length > 0;
            void Nav()
            {
                if (b?.CurrentTrack.Peek() is { Artists.Count: > 0 } now && now.Artists[0].Uri.Length > 0)
                    _go?.Invoke("artist:" + now.Artists[0].Uri, now.Artists[0].Name);
            }

            return new BoxEl
            {
                MinWidth = 0f, Shrink = 1f, ClipToBounds = true,
                Cursor = enabled ? CursorId.Hand : (CursorId?)null,
                OnClick = enabled ? Nav : null,
                OnHoverMove = enabled ? _ => { if (!hover.Peek()) hover.Value = true; } : null,
                OnPointerExit = enabled ? () => { if (hover.Peek()) hover.Value = false; } : null,
                Role = enabled ? AutomationRole.Hyperlink : AutomationRole.Text,
                Focusable = enabled, AllowFocusOnInteraction = false,
                Children =
                [
                    new TextEl(line)
                    {
                        Size = 14f, LineHeight = 20f, Weight = 400,
                        Color = enabled && hover.Value ? WaveeOnMedia.Ink : WaveeOnMedia.InkSecondary,
                        Underline = enabled && hover.Value,
                        Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    },
                ],
            };
        }
    }

    /// <summary>The output-device line: a speaker glyph plus the name of whatever is actually playing this — a Connect
    /// device when playback is remote (the SAME <c>PlayerBarContent.RemoteDevice</c> source <c>RemoteDeviceLine</c>
    /// reads, so the two can never disagree), otherwise the selected local endpoint or the system default. Clicking it
    /// opens the SAME two-section <c>DevicePickerMenu</c> the bar's Devices button opens.</summary>
    sealed class StageDeviceLine : Component
    {
        readonly PlaybackBridge _b;
        public StageDeviceLine(PlaybackBridge b) => _b = b;

        public override Element Render()
        {
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var svc = UseContext(Overlay.Service);

            var remote = PlayerBarContent.RemoteDevice(_b);           // subscribes to Devices + ActiveDeviceId
            string? selectedLocal = _b.LocalOutputs?.SelectedOutputId.Value;
            var localRoster = _b.LocalOutputs?.Devices.Value;

            string name;
            string glyph;
            if (remote is not null)
            {
                name = Strings.Player.PlayingOn(remote.Name);
                glyph = Icons.Devices;
            }
            else
            {
                LocalAudioDevice? sel = null;
                if (selectedLocal is { Length: > 0 } id && localRoster is not null)
                    foreach (var d in localRoster) { if (d.Id == id) { sel = d; break; } }
                name = sel?.Name ?? Loc.Get(Strings.Player.SystemDefault);
                glyph = sel?.Kind switch
                {
                    LocalAudioDeviceKind.Headphones or LocalAudioDeviceKind.Headset => Icons.Headphones,
                    LocalAudioDeviceKind.Hdmi => Icons.TvMonitor,
                    _ => Icons.Speakers,
                };
            }

            void Toggle()
            {
                if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
                handle.Value = svc.Open(
                    () => anchor.Value,
                    () => Embed.Comp(() => new DevicePickerMenu(_b, () => handle.Value?.Close())),
                    FlyoutPlacement.TopEdgeAlignedLeft,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
                handle.Value.ClosedAction = () => handle.Value = null;
            }

            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinHeight = 24f, MinWidth = 0f,
                Padding = new Edges4(Spacing.XS, 0f, Spacing.S, 0f),
                Corners = Radii.ControlAll,
                Fill = WaveeOnMedia.GlassRest, HoverFill = WaveeOnMedia.GlassHover, PressedFill = WaveeOnMedia.GlassPressed,
                BrushTransitionMs = WaveeMotion.Faster,
                ClipToBounds = true,
                Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
                Cursor = CursorId.Hand, OnClick = Toggle, OnRealized = h => anchor.Value = h,
                Children =
                [
                    new TextEl(glyph)
                    {
                        Size = 12f, FontFamily = Theme.IconFont,
                        Color = remote is null ? WaveeOnMedia.InkTertiary : WaveeOnMedia.InkSecondary,
                        HoverColor = WaveeOnMedia.Ink,
                    },
                    new TextEl(name)
                    {
                        Size = 12f, LineHeight = 16f, Weight = 400,
                        Color = remote is null ? WaveeOnMedia.InkTertiary : WaveeOnMedia.InkSecondary,
                        HoverColor = WaveeOnMedia.Ink,
                        MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
                    },
                ],
            };
        }
    }

    /// <summary>The compact header's "…": the folded controls, live. It is not a decoration — shuffle, repeat, mute and
    /// the whole device picker are all here, so <see cref="StageLayout.CompactFold"/> means "moved address", not "lost".</summary>
    sealed class StageOverflowButton : Component
    {
        readonly PlaybackBridge _b;
        readonly IReadSignal<StageLayout> _layout;
        public StageOverflowButton(PlaybackBridge b, IReadSignal<StageLayout> layout) { _b = b; _layout = layout; }

        public override Element Render()
        {
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var svc = UseContext(Overlay.Service);
            var L = _layout.Value;

            void Toggle()
            {
                if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
                // Built at OPEN time so the rows read the live state without subscribing the header to hot signals.
                var items = new List<MenuFlyoutItem>(8);
                if (!L.Shows(StageControl.Shuffle))
                    items.Add(MenuFlyoutItem.Toggle(Loc.Get(Strings.Player.Shuffle), _b.IsShuffle.Peek(),
                        () => PlayerBarContent.ToggleShuffle(_b), Icons.Shuffle));
                if (!L.Shows(StageControl.Repeat))
                {
                    var r = _b.Repeat.Peek();
                    items.Add(MenuFlyoutItem.Toggle(Loc.Get(Strings.Player.Repeat), r != RepeatMode.Off,
                        () => PlayerBarContent.CycleRepeat(_b),
                        r == RepeatMode.Track ? Icons.RepeatOne : Icons.RepeatAll));
                }
                if (!L.Shows(StageControl.Volume))
                {
                    bool muted = _b.OutputMuted.Peek() || _b.Volume.Peek() <= 0.001f;
                    items.Add(new MenuFlyoutItem(Loc.Get(muted ? Strings.Player.Unmute : Strings.Player.Mute),
                        muted ? Icons.Volume : Icons.Mute, true, () => VolumeButton.ToggleMute(_b)));
                }
                if (!L.Shows(StageControl.OutputDevice))
                {
                    if (items.Count > 0) items.Add(MenuFlyoutItem.Separator);
                    items.AddRange(PlayerBarContent.DevicePickerItems(_b));
                }
                if (items.Count == 0) return;

                handle.Value = svc.Open(
                    () => anchor.Value,
                    () => MenuFlyout.Create(items, () => handle.Value?.Close()),
                    FlyoutPlacement.BottomEdgeAlignedRight,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
                handle.Value.ClosedAction = () => handle.Value = null;
            }

            return ToolTip.Wrap(
                StageChrome.Glyph(Icons.More, Toggle, WaveeCta.IconButtonSize, 16f, onRealized: h => anchor.Value = h),
                Loc.Get(Strings.Player.NowPlaying));
        }
    }
}
