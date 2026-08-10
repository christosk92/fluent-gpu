using System;
using System.Diagnostics;
using FluentGpu.Animation;
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
/// The IMMERSIVE STAGE: the fullscreen now-playing surface, and the fullscreen twin of the rail's lyrics panel it grew
/// out of. Two regions over one drifting, baked-blur cover backdrop:
/// <list type="bullet">
///   <item>LEFT — <see cref="StageIdentity"/>: the cover, the identity, the seek, the transport, volume and the output
///     device, in a fixed bottom-anchored column (or a header row below <see cref="StageLayout.WideEnterW"/>).</item>
///   <item>RIGHT — <see cref="StagePanes"/>: the SAME <see cref="LyricsView"/> at <c>large: true</c> and the queue,
///     cross-faded in place, with the "Lyrics · Queue" pivot in the band along the bottom edge.</item>
/// </list>
///
/// <para>Mounted by <c>WaveeShell</c> as a full-bleed overlay layer while <see cref="ShellUi.ImmersiveLyrics"/> is true
/// (above the content card, below the engine's toast / teaching-tip lane). Entry is the expand button in the rail's
/// lyrics header; exit is Escape or the close button in this surface's top corner — both UNCHANGED by the stage.</para>
///
/// <para>CHROME BANDS. The surface deliberately leaves two strips of the shell live: the window caption band at the top
/// (drag + minimize/maximize/close belong to the OS chrome, and a surface that ate them would strand the user — the
/// lesson from the deleted fullscreen now-playing view, git ba43abbde) and the docked player bar at the bottom. The bar
/// stays reachable even though the stage now carries a full transport of its own: it is the app's ONE persistent
/// transport, and a surface that hid it would make closing the stage feel like losing playback control.</para>
///
/// <para>MATERIAL — THE STAGE IS SINGLE-THEME: ALWAYS ART-DARK. In both themes. The room is lit by the playing track,
/// the way every art-forward player lights it, and the whole surface (chrome AND lyrics) paints
/// <see cref="WaveeOnMedia"/>'s theme-invariant white on top of it. There is no theme branch anywhere in this file.
/// The stack is: the opaque <c>Tok.MediaStage</c> floor → the σ80 baked-blur artwork, full-bleed and drifting →
/// <see cref="StageChrome.Scrim"/>, ONE continuous vertical gradient over the whole body → <see
/// cref="StageChrome.ColumnShade"/>, ONE left-anchored layer that deepens the ground under the identity column and
/// feathers to exactly zero → content. It replaces a theme-flipping base scrim plus a patchwork of boxed per-region
/// veils, which in light theme read as a collage of dark patches with locatable edges over a white ground — and left
/// the theme-invariant white title sitting on the near-white part, invisible.</para>
/// </summary>
sealed class ImmersiveLyricsSurface : Component
{
    // ── the reading column ───────────────────────────────────────────────────────────────────────────────────────────
    // A lyric line at 36 DIP wants a bounded measure; an ultra-wide window would otherwise lay a chorus out as one
    // 2000-DIP ribbon. The column is centred horizontally and clamped; LyricsView's own RowSidePad (64 DIP at large)
    // is the gutter INSIDE it, so the text block is ~570 DIP wide — roughly the reference's line length.
    // Internal: StagePanes owns the lyrics column now and clamps against the PANE's width, not the viewport's.
    internal const float ColumnMaxW = 700f;
    internal const float ColumnGutter = 96f;   // breathing room reserved either side before the clamp bites

    // ── the animated cover backdrop ──────────────────────────────────────────────────────────────────────────────────
    // The cover is drawn OVERSIZED and re-centred so the drift can never expose an edge: 130 % of the viewport leaves a
    // 15 % margin on every side, against a ≤4 % translation + ≤2 % scale wobble.
    const float Overscale = 1.30f;
    // Blur baked ONCE per art change into a derived image (BakedBlurSpec — no scene layer, no per-frame Gaussian): after
    // the bake the backdrop is an ordinary textured quad, so every frame of the drift is a pure transform write.
    const float BackdropSigmaDip = 80f;
    const float BackdropResolutionScale = 0.5f;
    // The scrim's own numbers live in StageLayout (the pure allocator) and are spelled as GradientSpecs by StageChrome.
    // There is deliberately no alpha constant here any more, and no theme branch to pick between two of them.

    // Drift: two INCOMMENSURATE sinusoids (37 s and 53 s never re-phase inside a listening session), so the motion has
    // no discernible loop and — deliberately — no relation to the beat. Amplitude is a fraction of the viewport so the
    // felt speed is the same on a laptop and a 4K panel. ~30 Hz is plenty for a 4 %-amplitude, 37-second sweep.
    const float DriftPeriodASec = 37f;
    const float DriftPeriodBSec = 53f;
    const float DriftAmpFrac = 0.04f;
    const float DriftScaleAmp = 0.02f;
    const float DriftIntervalMs = 33f;
    // Write gates. The transform folds into no cache key here (the bake is already done), but a write still dirties
    // paint, so ticks whose delta is invisible are dropped — which is most of them near each sinusoid's turning point.
    const float DriftWriteEps = 0.15f;     // DIP
    const float DriftScaleEps = 0.0004f;   // ≈0.5 DIP of edge travel on a 1300-DIP viewport
    const float Tau = 6.2831853f;

    // The drift carrier's node — a plain BoxEl that declares NO transform of its own, so nothing else ever stomps the
    // LocalTransform this component writes (the same reconciler rule LyricsView.WriteCascade documents: LocalTransform
    // is re-asserted on a re-render only for the declared-static → declared-identity transition). The oversize +
    // re-centring lives on its PARENT as a DECLARED, viewport-bound transform, so a window resize re-centres the
    // backdrop for free even when no ticker is running (setting off / reduced motion).
    NodeHandle _driftNode;
    long _driftOriginQpc;                 // QPC origin (Stopwatch.GetTimestamp — never TickCount64, which quantises to ~15.6 ms)
    Action? _driftTick;                   // the interval callback, allocated once (never per render)
    IReadSignal<Size2>? _viewport;        // Peeked by the tick — never subscribed from it

    /// <summary>The surface's mount/unmount terminals, read by <c>WaveeShell</c> where it authors the Flow.Show
    /// branch. A fade always; the slight scale-in only when the OS is not asking for reduced motion (read as a VALUE at
    /// the point of consumption — never a hook branch).</summary>
    internal static EnterExit EnterTerminal => Motion.ReducedMotion
        ? new EnterExit(Opacity: 0f, Active: true)
        : new EnterExit(Sx: 1.03f, Sy: 1.03f, Opacity: 0f, Active: true);

    /// <inheritdoc cref="EnterTerminal"/>
    internal static EnterExit ExitTerminal => Motion.ReducedMotion
        ? new EnterExit(Opacity: 0f, Active: true)
        : new EnterExit(Sx: 1.02f, Sy: 1.02f, Opacity: 0f, Active: true);

    Action DriftTickAction => _driftTick ??= DriftTick;

    public override Element Render()
    {
        var ui = UseContext(ShellUi.Slot);
        var b = UseContext(PlaybackBridge.Slot);
        var svc = UseContext(Services.Slot);
        var hooks = UseContext(InputHooks.Current);
        var vpSig = UseContextSignal(Viewport.Size);
        _viewport = vpSig;

        // ── the stage's ONE reflow flag ──────────────────────────────────────────────────────────────────────────────
        // A COARSE band signal, resolved in an effect (the PlayerBar idiom): the surface re-renders on a wide⇄compact
        // FLIP, never on a resize pixel, and the hysteresis in StageLayout.Resolve means dragging the window edge across
        // the boundary flips it exactly once per crossing instead of thrashing a mounted LyricsView.
        var stage = UseSignal(StageLayout.FromWidth(vpSig.Peek().Width));
        var stageSeeded = UseRef(vpSig.Peek().Width > 0f);
        UseSignalEffect(() =>
        {
            var prev = stage.Peek();
            float w = vpSig.Value.Width;
            var next = StageLayout.Resolve(w, stageSeeded.Value ? prev : null);
            if (w > 0f) stageSeeded.Value = true;
            if (!next.Equals(prev)) stage.Value = next;
        });

        // The appearance epoch is what makes the Settings toggle apply LIVE to an already-open surface (the
        // DisableColorWashes / DisableMarquee idiom): the writer bumps it, this re-renders, the interval re-gates.
        _ = AppearancePrefs.Epoch.Value;
        bool animated = svc?.Settings.Get(WaveeSettings.LyricsAnimatedBackdrop) ?? true;
        // Reduced motion is a VALUE the PAL republishes on WM_SETTINGCHANGE — read where it is consumed, exactly like
        // LyricsView's three consumption points. It vetoes the drift independently of the app setting.
        bool drift = animated && !Motion.ReducedMotion;

        // The secondary-line toggle's state. Both reads are SUBSCRIPTIONS, for the same reasons the rail header
        // documents: Available so the button appears when a document with a translation/romanization lands, Epoch so a
        // write from here, from the rail, or from the Settings picker re-reads the mode on the same frame.
        int secondaryAvailable = LyricsPrefs.Available.Value;
        _ = LyricsPrefs.Epoch.Value;
        int secondary = LyricsPrefs.Clamp(svc?.Settings.Get(WaveeSettings.LyricsSecondaryLine) ?? LyricsPrefs.None);

        var track = b?.CurrentTrack.Value;
        string art = track?.Image?.Url is { Length: > 0 } u ? ImageSource.Normalize(u) ?? "" : "";
        string? blurHash = track?.Image?.BlurHash;

        // Escape routes to the FOCUSED node and bubbles up its ancestors, so the surface takes focus once at mount.
        // The root stays focusable (and does NOT set AllowFocusOnInteraction=false) so a click on the surface's own
        // background lands focus back here rather than clearing it — Escape keeps working after any interaction.
        UseLayoutEffect(() =>
        {
            if (!Context.HostNode.IsNull) hooks.FocusNode?.Invoke(Context.HostNode, false);
        }, DepKey.Empty);

        // A drift turned OFF mid-flight (settings flip, or the OS reduced-motion preference arriving) must not strand
        // the carrier at its last offset — put it back on the exact centre the declared parent transform establishes.
        UseEffect(() => { if (!drift) ResetDrift(); }, DepKey.From(drift));

        // OFF ⇒ no ticker at all (not a ticking no-op): a still backdrop must cost literally nothing per frame.
        UseInterval(DriftTickAction, DriftIntervalMs, enabled: drift && art.Length > 0);

        return new BoxEl
        {
            Grow = 1f, Direction = 1,
            // Full-bleed POSITIONER: only the body band below is hittable, so the window caption strip and the docked
            // player bar keep receiving input through this layer.
            HitTestPassThrough = true,
            Focusable = true,
            OnKeyDown = e =>
            {
                if (e.Handled || e.KeyCode != Keys.Escape) return;
                e.Handled = true;
                Close(ui);
            },
            Children =
            [
                new BoxEl { Height = TitleBar.ExpandedHeight, Shrink = 0f, HitTestPassThrough = true },
                new BoxEl
                {
                    Grow = 1f, Shrink = 1f, MinHeight = 0f, ZStack = true, ClipToBounds = true,
                    // The opaque floor under the (possibly missing / still-decoding) cover: the surface must never let
                    // the page it covers show through. Bound so a live re-theme re-fires it in place.
                    Fill = Prop.Of(() => Tok.FillSolidBase),
                    Children =
                    [
                        Backdrop(vpSig, stage, art, blurHash),
                        new BoxEl
                        {
                            Grow = 1f, Direction = 1, MinHeight = 0f, MinWidth = 0f,
                            Children =
                            [
                                TopBar(track, ui, svc?.Settings, secondary, secondaryAvailable),
                                StageBody(stage, vpSig),
                            ],
                        },
                    ],
                },
                new BoxEl { Height = WaveeSize.PlayerBarH, Shrink = 0f, HitTestPassThrough = true },
            ],
        };
    }

    static void Close(ShellUi? ui)
    {
        if (ui is not null) ui.ImmersiveLyrics.Value = false;
    }

    // ── the two regions ──────────────────────────────────────────────────────────────────────────────────────────────
    // ONE tree, one reflow flag (StageLayout.Wide): the row direction is the only thing the band changes here, and the
    // two children keep their KEYS across the flip so neither the identity column nor the mounted LyricsView/queue is
    // ever remounted by a resize.
    //
    // GRADIENT-GLYPH BUDGET (the lyrics pane). Since Wave A's settled-split fast path, only the line whose wipe is
    // mid-sweep emits a gradient glyph run at all (Split >= 1 and Split <= 0 both fall into the cheap plain-glyph
    // batch). Even at a 1044-DIP viewport the stage shows ~20 rows and exactly ONE of them is wiping, so a worst-case
    // ~40 gradient glyphs sits two orders of magnitude inside MaxGradGlyphs = 1024 — no cap change is warranted.
    static Element StageBody(IReadSignal<StageLayout> stage, IReadSignal<Size2> vpSig)
    {
        var L = stage.Value;
        return new BoxEl
        {
            Direction = (byte)(L.Wide ? 0 : 1),
            Grow = 1f, Shrink = 1f, MinHeight = 0f, MinWidth = 0f,
            AlignItems = FlexAlign.Stretch,
            Children =
            [
                Embed.Comp(() => new StageIdentity(stage)) with { Key = "stage:identity" },
                new BoxEl
                {
                    Key = "stage:panes",
                    Grow = 1f, Shrink = 1f, MinHeight = 0f, MinWidth = 0f,
                    Children = [Embed.Comp(() => new StagePanes(stage, vpSig))],
                },
            ],
        };
    }

    // ── minimal top chrome: the way out ──────────────────────────────────────────────────────────────────────────────
    // The identity moved into the stage's left column, so this band is now only the surface's OWN controls, pushed to
    // the right edge under the caption veil. The secondary-line toggle sits immediately left of the close button — the
    // same relative position it takes in the rail header — and is shown only when a second line is actually available
    // AND the lyrics pane is the one on screen (a translation toggle over a queue is chrome for a pane you cannot see).
    static Element TopBar(Track? track, ShellUi? ui, IAppSettings? settings, int secondary, int secondaryAvailable)
    {
        bool lyricsPane = StagePane.Current.Value == StagePane.Lyrics;
        var accent = StageChrome.AccentFor(track);
        var kids = new System.Collections.Generic.List<Element>(3)
        {
            new BoxEl { Grow = 1f, MinWidth = 0f, HitTestVisible = false },
        };
        if (secondaryAvailable != 0 && lyricsPane)
            // `active` = a second line is actually on screen (not merely "the mode is non-zero") — see the same
            // note in RightRail.LyricsHeaderKids.
            kids.Add(GlyphButton(Icons.Globe, LyricsPrefs.Tooltip(secondary),
                () => LyricsPrefs.Set(settings, LyricsPrefs.Next(secondary, secondaryAvailable)), accent,
                active: (secondaryAvailable & LyricsPrefs.BitFor(secondary)) != 0));
        kids.Add(CloseButton(() => Close(ui), accent));

        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Start, Gap = Spacing.S, Shrink = 0f,
            Height = StageChrome.TopBandH,
            Padding = new Edges4(Spacing.L, Spacing.M, Spacing.M, Spacing.M),
            // NO veil of its own. The scrim's top deepening already resolves under this band, across a feather many
            // times its height — a boxed gradient here is exactly the dark band across the top that this wave removed.
            Children = kids.ToArray(),
        };
    }

    // One shape for the two buttons in this band, so the secondary-line toggle and the close button read as a pair.
    // GEOMETRY: row 1 of WaveeCta's icon-button table (32 × 32, Radii.Control, 16-DIP glyph) — the same control the rail
    // header uses, so the pair does not change size when the user promotes the panel to fullscreen. INK is the on-media
    // ladder (StageChrome.Glyph), because this band sits on the caption veil rather than on the theme scrim.
    static Element GlyphButton(string glyph, string tip, Action onClick, ColorF accent, bool active = false) => ToolTip.Wrap(
        active
            ? StageChrome.Satellite(glyph, onClick, enabled: true, latched: true, accent: accent,
                box: WaveeCta.IconButtonSize, glyphSize: RightRail.HeaderGlyph)
            : StageChrome.Glyph(glyph, onClick, WaveeCta.IconButtonSize, RightRail.HeaderGlyph),
        tip);

    // The "leave fullscreen" glyph — the deliberate counterpart of the rail header's Icons.FullScreen expand button.
    static Element CloseButton(Action onClick, ColorF accent) =>
        GlyphButton(Icons.BackToWindow, Loc.Get(Strings.Player.CloseLyrics), onClick, accent);

    // ── the backdrop stack ───────────────────────────────────────────────────────────────────────────────────────────
    // The backdrop fills the BODY band, not the whole window — the caption strip and the player bar are left to the
    // shell — so every geometry below is derived from the body size, which is the viewport less those two fixed rows.
    static float BodyW(float vpW) => MathF.Max(1f, vpW);
    static float BodyH(float vpH) => MathF.Max(1f, vpH - TitleBar.ExpandedHeight - WaveeSize.PlayerBarH);

    Element Backdrop(IReadSignal<Size2> vpSig, IReadSignal<StageLayout> stage, string art, string? blurHash)
    {
        // One delegate per axis, shared by the frame's size AND its centring transform (cold path; no per-frame cost).
        Func<float> frameW = () => BodyW(vpSig.Value.Width) * Overscale;
        Func<float> frameH = () => BodyH(vpSig.Value.Height) * Overscale;

        Element cover = art.Length > 0
            ? Ui.Image(art, ImageFit.Cover, aspect: float.NaN, decodePx: 512f, corners: 0f,
                placeholder: Surfaces.PlaceholderFor(art), blurHash: blurHash,
                transition: ImageTransition.Fade(220f)) with
                {
                    AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                    BakedBlur = new BakedBlurSpec(BackdropSigmaDip, BackdropResolutionScale),
                }
            : new BoxEl
            {
                AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                Fill = Surfaces.PlaceholderFor(null),
            };

        return new BoxEl
        {
            Grow = 1f, ZStack = true, ClipToBounds = true, HitTestVisible = false,
            Children =
            [
                new BoxEl
                {
                    // The OVERSIZE + RE-CENTRE frame. A ZStack lays an explicitly-sized child out at its own origin, so
                    // this one starts flush top-left and the transform pulls it back by half the overhang. That
                    // transform is DECLARED (engine-owned, viewport-bound), which is what keeps the STATIC backdrop
                    // resize-correct with no ticker running; the drift carrier below declares none, so the two never
                    // fight over one LocalTransform.
                    ZStack = true, HitTestVisible = false, Shrink = 0f,
                    Width = Prop.Of(frameW),
                    Height = Prop.Of(frameH),
                    Transform = Prop.Of(() => Affine2D.Translation(
                        -(Overscale - 1f) * 0.5f * BodyW(vpSig.Value.Width),
                        -(Overscale - 1f) * 0.5f * BodyH(vpSig.Value.Height))),
                    Children =
                    [
                        new BoxEl
                        {
                            ZStack = true, HitTestVisible = false,
                            AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                            OnRealized = h => { _driftNode = h; },
                            Children = [cover],
                        },
                    ],
                },
                // ── the scrim system: TWO full-bleed paint layers, and that is the whole of it ────────────────────────
                // One continuous vertical gradient over the entire body (deep top, flat middle, deep bottom), then one
                // left-anchored layer that deepens the ground under the identity column and feathers to exactly zero.
                // Both are theme-invariant. Every chrome region's own boxed veil is GONE — the two layers below are
                // what the caption cluster, the column and the pivot band now sit on.
                new BoxEl { Grow = 1f, HitTestVisible = false, Gradient = StageChrome.Scrim() },
                new BoxEl
                {
                    // A PAINT layer, not a layout one: it is inside the full-bleed backdrop stack, so its 612-DIP width
                    // costs the pane region nothing (the column BOX is still StageLayout.LayoutWidth). Bound rather than
                    // read at render time so the wide⇄compact flip re-solves it without re-rendering the surface; 0 in
                    // the compact shape, where there is no column to deepen under.
                    Width = Prop.Of(() => stage.Value.Wide ? StageLayout.ColumnShadeW : 0f),
                    AlignSelf = FlexAlign.Stretch,
                    Shrink = 0f, HitTestVisible = false,
                    Gradient = StageChrome.ColumnShade(),
                },
            ],
        };
    }

    // ── the drift ticker ─────────────────────────────────────────────────────────────────────────────────────────────
    // ZERO managed allocation per tick: Peek only (never .Value — a subscription here would re-render the surface at
    // 30 Hz), scalar float math into one stack Affine2D, one ref write into the paint column.
    void DriftTick()
    {
        var scene = Context.Scene;
        var h = _driftNode;
        if (scene is null || h.IsNull || !scene.IsLive(h) || _viewport is null) return;

        long qpc = Stopwatch.GetTimestamp();
        if (_driftOriginQpc == 0L) _driftOriginQpc = qpc;   // t = 0 on the first tick ⇒ the surface opens with no jump
        float t = (float)((qpc - _driftOriginQpc) / (double)Stopwatch.Frequency);

        var vp = _viewport.Peek();
        float vw = BodyW(vp.Width), vh = BodyH(vp.Height);
        float sinA = MathF.Sin(Tau * t / DriftPeriodASec);
        float sinB = MathF.Sin(Tau * t / DriftPeriodBSec);
        float dx = DriftAmpFrac * vw * sinA;
        float dy = DriftAmpFrac * vh * sinB;
        // The scale wobble rides the SAME two sinusoids, so it inherits their incommensurability instead of adding a
        // third period that could beat against them. Range: exactly ±DriftScaleAmp.
        float s = 1f + DriftScaleAmp * 0.5f * (sinB - sinA);
        // Scale about the carrier's own centre, then translate: T(dx,dy) ∘ T(c) ∘ S(s) ∘ T(-c).
        float cx = vw * Overscale * 0.5f, cy = vh * Overscale * 0.5f;
        var next = new Affine2D(s, 0f, 0f, s, cx * (1f - s) + dx, cy * (1f - s) + dy);

        ref NodePaint p = ref scene.Paint(h);
        var cur = p.LocalTransform;
        if (MathF.Abs(cur.Dx - next.Dx) < DriftWriteEps &&
            MathF.Abs(cur.Dy - next.Dy) < DriftWriteEps &&
            MathF.Abs(cur.M11 - next.M11) < DriftScaleEps) return;
        p.LocalTransform = next;
        scene.Mark(h, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
    }

    void ResetDrift()
    {
        _driftOriginQpc = 0L;
        var scene = Context.Scene;
        var h = _driftNode;
        if (scene is null || h.IsNull || !scene.IsLive(h)) return;
        ref NodePaint p = ref scene.Paint(h);
        if (p.LocalTransform.IsIdentity) return;
        p.LocalTransform = Affine2D.Identity;
        scene.Mark(h, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
    }
}
