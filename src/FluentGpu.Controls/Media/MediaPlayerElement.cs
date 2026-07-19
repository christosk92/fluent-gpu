using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Media;
using FluentGpu.Scene;

namespace FluentGpu.Controls.Media;

/// <summary>How the video frame is scaled into the element's video area (WinUI <c>Stretch</c>).</summary>
public enum MediaStretch : byte
{
    /// <summary>Native size, centered, never scaled up (down-clamped to fit).</summary>
    None,
    /// <summary>Stretch to fill the whole area (aspect not preserved).</summary>
    Fill,
    /// <summary>Scale to fit, preserving aspect — letterbox/pillarbox (the default).</summary>
    Uniform,
    /// <summary>Scale to fill, preserving aspect — the overflow is clipped to the area.</summary>
    UniformToFill
}

/// <summary>
/// The real <c>MediaPlayerElement</c> (spec §4.3) — replaces the chrome-only mockup. Binds a headless
/// <see cref="IMediaPlayer"/> to a composited video layer and pure-FluentGpu transport chrome:
/// <list type="bullet">
/// <item>Acquires a composited video surface (<c>UseVideoSurface</c>), draws a premultiplied-0 hole at the video rect,
///   and drives the player's per-frame video pump (<see cref="IMediaPlayer.PumpVideo"/>) which binds the produced
///   DirectComposition handle and positions the child z-below the UI.</item>
/// <item><b>Degrades to audio-only chrome</b> when <see cref="IMediaPlayer.NaturalSize"/> is <c>(0,0)</c> — no hole, just
///   the poster/audio surface + transport.</item>
/// <item>The default transport is pure FluentGpu (our own GPU text + a scrub <c>Slider</c>) — there is NO OS control to
///   crash on (the WinUI <c>MediaTransportControls</c> #7702 <c>E_NOINTERFACE</c> process-crash is unreachable here).</item>
/// </list>
/// TerraFX-free: references only Engine/Controls types + the <see cref="IMediaPlayer"/>/<see cref="VideoBinding"/> seam.
/// </summary>
public sealed class MediaPlayerElement : Component
{
    /// <summary>The player this element presents (headless contract; the MF backend drives real video).</summary>
    public required IMediaPlayer Player { get; init; }
    /// <summary>Show the transport controls. Never crashes across OS versions (there is no OS control). Default true.</summary>
    public bool AreTransportControlsEnabled { get; init; } = true;
    /// <summary>How the frame scales into the video area. Default <see cref="MediaStretch.Uniform"/>.</summary>
    public MediaStretch Stretch { get; init; } = MediaStretch.Uniform;
    /// <summary>Shown over the video area until the first frame / when audio-only. Null → a default poster.</summary>
    public Element? PosterContent { get; init; }
    /// <summary>Bring-your-own transport (still reads the same bound player). Null → the default FluentGpu transport.</summary>
    public Element? TransportOverride { get; init; }

    public override Element Render()
    {
        _ = UseContext(FrameClock.Tick);              // re-render + pump every frame while mounted
        var binding = UseVideoSurface();
        float scale = UseContext(Viewport.Scale);
        var hooks = UseContext(InputHooks.Current);
        var areaRef = UseRef<NodeHandle>(default);

        var natural = Player.NaturalSize.Value;        // subscribe → re-render when the video size resolves
        var state = Player.State.Value;                // subscribe → re-render on transport state changes
        bool audioOnly = IsAudioOnly(natural);
        bool videoReady = !audioOnly && state is not (PlaybackState.Idle or PlaybackState.Opening);

        // The video area's laid-out rect (DIP, last frame). Fit the frame into it per Stretch, then pump the handoff.
        RectF area = default;
        if (hooks?.GetNodeRect is { } getRect && !areaRef.Value.IsNull)
            area = getRect(areaRef.Value);
        RectF videoRect = (audioOnly || area.W <= 0f) ? area : FitVideoRect(area, natural, Stretch);
        // Always pump: it advances state/position for audio-only too, and self-gates the (video-only) surface handoff.
        Player.PumpVideo(binding, videoRect, scale <= 0f ? 1f : scale);
        if (audioOnly) binding.SetVisible(false);      // no video child on an audio-only source

        // ── the video area: a transparent hole (z-below video shows through) with a poster until the first frame ──
        Element? overlay = videoReady ? null : (PosterContent ?? DefaultPoster());
        var videoArea = new BoxEl
        {
            Grow = 1f,
            MinHeight = 160f,
            ClipToBounds = true,
            Corners = Radii.OverlayAll,
            // Premultiplied-0 hole: on a composited window the transparent fill reveals the z-below video child.
            Fill = ColorF.Transparent,
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            OnRealized = h => areaRef.Value = h,
            Children = overlay is null ? Array.Empty<Element>() : new[] { overlay },
        };

        var children = AreTransportControlsEnabled
            ? new[] { videoArea, TransportOverride ?? BuildTransport(area.W) }
            : new[] { videoArea };

        return new BoxEl
        {
            Direction = 1,
            Grow = 1f,
            Corners = Radii.OverlayAll,
            ClipToBounds = true,
            BorderColor = Tok.StrokeFlyoutDefault,
            BorderWidth = 1f,
            Children = children,
        };
    }

    // ── default transport (pure FluentGpu: play/pause, scrub Slider, GPU time text, mute) ────────────────────────────

    private Element BuildTransport(float areaWidth)
    {
        bool playing = Player.IsPlaying.Value;                 // subscribe
        var pos = Player.Position.Value;                       // subscribe (updated each frame)
        var dur = Player.Duration.Value;
        bool muted = Player.Muted.Value;

        float frac = dur > TimeSpan.Zero ? (float)Math.Clamp(pos.TotalSeconds / dur.TotalSeconds, 0.0, 1.0) : 0f;
        float seekWidth = MathF.Max(80f, areaWidth - 300f);

        void Seek(float f)
        {
            if (dur > TimeSpan.Zero) _ = Player.SeekAsync(dur * Math.Clamp(f, 0f, 1f));
        }

        var playPause = IconButton(playing ? Icons.Pause : Icons.Play, () =>
        {
            if (playing) _ = Player.PauseAsync(); else _ = Player.PlayAsync();
        });

        var muteBtn = IconButton(muted ? Icons.Mute : Icons.Volume, () => Player.SetMuted(!muted));

        var time = new TextEl($"{FormatTime(pos)} / {FormatTime(dur)}")
        {
            Size = 12f, Color = Tok.TextSecondary,
        };

        return new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Gap = 10f,
            Padding = new Edges4(12, 8, 12, 8),
            Fill = Tok.FillLayerDefault,
            BorderColor = Tok.StrokeFlyoutDefault,
            BorderWidth = 1f,
            Children = new Element[]
            {
                playPause,
                Slider.Create(frac, Seek, width: seekWidth, height: 24f),
                time,
                muteBtn,
            },
        };
    }

    private static BoxEl IconButton(string glyph, Action onClick) => new()
    {
        Width = 40f, Height = 40f, Corners = Radii.ControlAll,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
        Role = AutomationRole.Button,
        OnClick = onClick,
        Children = new Element[] { new TextEl(glyph) { Size = 16f, Color = Tok.TextPrimary, FontFamily = Theme.IconFont } },
    };

    private Element DefaultPoster() => new BoxEl
    {
        Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Fill = ColorF.FromRgba(0x0A, 0x0A, 0x0A),
        Children = new Element[]
        {
            new BoxEl
            {
                Width = 56f, Height = 56f, Corners = Radii.Circle(56f),
                Fill = ColorF.FromRgba(0, 0, 0, 0x80),
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = new Element[]
                {
                    new TextEl(Icons.Play) { Size = 24f, Color = ColorF.FromRgba(0xFF, 0xFF, 0xFF), FontFamily = Theme.IconFont },
                },
            },
        },
    };

    // ── pure helpers (unit-tested) ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Degrade decision: audio-only (no hole-punch) iff the source has no video.</summary>
    internal static bool IsAudioOnly(SizeI natural) => natural.IsEmpty;

    /// <summary>Fit a <paramref name="natural"/>-sized frame into <paramref name="area"/> (DIP) per <paramref name="stretch"/>.
    /// Returns the placed video rect (DIP), centered; never larger than the area for <see cref="MediaStretch.UniformToFill"/>
    /// (the overflow axis is clipped to the area — true center-crop via an MF source-rect is a later refinement).</summary>
    internal static RectF FitVideoRect(RectF area, SizeI natural, MediaStretch stretch)
    {
        if (area.W <= 0f || area.H <= 0f || natural.IsEmpty) return area;
        float aw = area.W, ah = area.H;
        float vw = natural.Width, vh = natural.Height;
        switch (stretch)
        {
            case MediaStretch.Fill:
                return area;
            case MediaStretch.None:
            {
                float w = MathF.Min(vw, aw), h = MathF.Min(vh, ah);
                return Center(area, w, h);
            }
            case MediaStretch.UniformToFill:
            {
                float s = MathF.Max(aw / vw, ah / vh);
                float w = MathF.Min(vw * s, aw), h = MathF.Min(vh * s, ah);
                return Center(area, w, h);
            }
            case MediaStretch.Uniform:
            default:
            {
                float s = MathF.Min(aw / vw, ah / vh);
                return Center(area, vw * s, vh * s);
            }
        }
    }

    private static RectF Center(RectF area, float w, float h)
        => new(area.X + (area.W - w) * 0.5f, area.Y + (area.H - h) * 0.5f, w, h);

    /// <summary>DIP→device-px rect (the hole-punch device rect the presenter places at).</summary>
    internal static RectF ToDeviceRect(RectF dip, float scale)
    {
        float s = scale <= 0f ? 1f : scale;
        return new RectF(dip.X * s, dip.Y * s, dip.W * s, dip.H * s);
    }

    /// <summary><c>m:ss</c> (or <c>h:mm:ss</c> past an hour); a negative/unknown span reads <c>0:00</c>.</summary>
    internal static string FormatTime(TimeSpan t)
    {
        if (t < TimeSpan.Zero || t == TimeSpan.MinValue) t = TimeSpan.Zero;
        int total = (int)t.TotalSeconds;
        int h = total / 3600, m = (total % 3600) / 60, s = total % 60;
        return h > 0 ? $"{h}:{m:D2}:{s:D2}" : $"{m}:{s:D2}";
    }
}
