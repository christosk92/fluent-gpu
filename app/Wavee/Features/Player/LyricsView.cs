using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core;

namespace Wavee;

// ── Phase 1 — line-level synced lyrics view ──────────────────────────────────────────────────────────────────────────
//
// Renders a vertical column of lyric lines and auto-follows playback: the active line is parked at a focal band and
// emphasised (brighter + slightly larger via per-line springs); neighbours dim with distance. The column scroll is a
// pure compositor Transform bound to _scrollY, advanced per-frame by a GATED ticker (mounted only while the rail is open)
// that reads the smooth playback clock (the SeekBar idiom), resolves the active line, and eases _scrollY so the active
// line sits at the band. Click a line to seek.
//
// Cost discipline (signals-first, like SeekBar): Render re-runs only on LOW-frequency state (track / load / active-line).
// The hot path is the ticker writing VALUE-GATED signals (_scrollY, _activeLine) the compositor binds read — an unmoved
// frame is a true no-op, so the host skip-submit gate idles the GPU at rest. No per-frame managed allocation.
sealed class LyricsView : Component
{
    // Smooth-clock anchors (re-anchored on each ~1 Hz position tick) — SeekBar.cs:56-101 idiom.
    long _tickWallMs;
    long _tickPosMs;

    // Scroll follow: bound to the column Transform; eased toward the active line each frame by the ticker.
    readonly FloatSignal _scrollY = new(0f);
    bool _scrollSnapped;   // false → the next valid frame JUMPS the scroll to the active line (open mid-playback / new track), then eases
    // Resolver output — drives per-line emphasis (each line subscribes). -1 = before the first line.
    readonly Signal<int> _activeLine = new(-1);
    // Smooth playback ms — published every frame by the ticker; the ACTIVE word-by-word line reads it to advance its
    // karaoke wipe split (so only that one line re-renders per frame; the wipe rides DrawGlyphRunGradient → no reshape).
    readonly FloatSignal _nowMs = new(0f);

    // Live layout handles read by the ticker (the active line's arranged rect vs the viewport's → the scroll target).
    // The viewport is this component's HostNode (read in OnFrame), not a reported handle.
    NodeHandle[] _lineNodes = Array.Empty<NodeHandle>();
    NodeHandle[] _glowNodes = Array.Empty<NodeHandle>();   // parallel: the active wbw line's wiped-glow run (split advanced in lockstep)

    LyricsDocument? _doc;     // the built doc the ticker resolves against (peek-only off the UI thread-of-record)
    PlaybackBridge? _b;

    readonly bool _large;            // fullscreen surface (large, centered) vs the compact rail
    readonly Func<bool>? _visible;   // mount-the-ticker gate; null => use ShellUi.RailOpen (the rail case)
    float _band = 0.40f;             // focal band (fraction of viewport height) the active line parks at

    public LyricsView(bool large = false, Func<bool>? visible = null) { _large = large; _visible = visible; }

    public override Element Render()
    {
        // Hooks FIRST (stable order, rule #7) — before any early return.
        var ui = UseContext(ShellUi.Slot);
        var b = UseContext(PlaybackBridge.Slot);
        var svc = UseContext(Services.Slot);
        _b = b;

        var track = b?.CurrentTrack.Value;                 // subscribe → reload on track change
        bool playing = b?.IsPlaying.Value ?? false;        // subscribe (kept for future gating)
        bool open = _visible is not null ? _visible() : (ui?.RailOpen.Value ?? false);   // gate the ticker + fetch
        long posTick = b?.PositionMs.Value ?? 0L;          // subscribe → re-anchor the smooth clock each tick
        string trackId = track?.Id ?? "";
        string artist = track is { Artists.Count: > 0 } ? track.Artists[0].Name : "";

        // Fetch only while the pane is shown, and re-fetch when: the track changes, the pane is (re)opened — so opening
        // mid-playback loads immediately and a transient miss retries — or async enrichment fills in the initially-thin
        // cluster track's artist (so the metadata sources get a real artist to match on).
        string fetchKey = open ? trackId + "|" + artist : "";
        var docL = UseAsyncResource(
            ct => open && svc?.Lyrics is { } lp ? lp.GetLyricsAsync(trackId, ct) : Task.FromResult<LyricsDocument?>(null),
            (LyricsDocument?)null, fetchKey);

        UseEffect(() =>
        {
            if (b is null) return;
            _tickWallMs = Environment.TickCount64;
            _tickPosMs = b.PositionMs.Peek();
        }, posTick);

        byte loadState = docL.State.Value;                 // subscribe to load state
        var doc = docL.Value.Value;                        // subscribe to the value

        // ── guards (after all hooks) ──
        if (b is null || svc is null) return new BoxEl { Grow = 1f };
        if (track is null) return Message("Nothing playing");
        if (loadState == (byte)LoadState.Pending) return Message("Loading lyrics…");
        var lines = doc?.Lines;
        if (lines is null || lines.Count == 0) return Message("No lyrics available");

        // Rebuild geometry bookkeeping when the doc identity changes (new track).
        if (!ReferenceEquals(_doc, doc))
        {
            _doc = doc;
            _lineNodes = new NodeHandle[lines.Count];      // reconcile-edge alloc (once per track) — fine
            _glowNodes = new NodeHandle[lines.Count];
            _activeLine.Value = -1;
            _scrollY.Value = 0f;
            _scrollSnapped = false;
        }

        _ = _activeLine.Value;   // subscribe so a new active line re-renders the column (per-line emphasis re-seeds)

        // Surface style: the compact left-aligned rail vs the large, centered fullscreen view.
        float fontSz = _large ? 34f : 21f;
        float lineHt = _large ? 44f : 27f;
        bool centered = _large;
        _band = _large ? 0.42f : 0.40f;
        float padX = _large ? 48f : 18f;
        float padY = _large ? 340f : 240f;

        var lineKids = new Element[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            int idx = i;
            var line = lines[i];
            lineKids[i] = Embed.Comp(() => new LyricLineView(idx, line, _activeLine, _nowMs, fontSz, lineHt, centered, ReportLineNode, ReportGlowNode, () => SeekToLine(idx)))
                with { Key = "ll" + idx };
        }

        // The scrolling content column — Transform is a compositor bind (no re-render on scroll). Generous top/bottom
        // padding so the first/last lines can still reach the focal band.
        // Shrink=0 keeps the column at content height so it can overflow + scroll; as the viewport's cross-axis child it
        // stretches to full width, so AlignItems=Center actually centers the (no-wrap) lines on the fullscreen surface.
        var column = new BoxEl
        {
            Direction = 1, Shrink = 0f, Gap = _large ? 14f : 8f, Padding = new Edges4(padX, padY, padX, padY),
            AlignItems = centered ? FlexAlign.Center : FlexAlign.Stretch,
            Transform = Prop.Of(() => Affine2D.Translation(0f, _scrollY.Value)),
            Children = lineKids,
        };

        Element? ticker = open ? Embed.Comp(() => new LyricsTicker { Owner = this }) : null;

        return new BoxEl
        {
            Grow = 1f, MinHeight = 0f, ClipToBounds = true, Direction = 1,
            Children = ticker is null ? [column] : [column, ticker],
        };
    }

    void ReportLineNode(int index, NodeHandle h)
    {
        if ((uint)index < (uint)_lineNodes.Length) _lineNodes[index] = h;
    }

    void ReportGlowNode(int index, NodeHandle h)
    {
        if ((uint)index < (uint)_glowNodes.Length) _glowNodes[index] = h;
    }

    void SeekToLine(int index)
    {
        var b = _b; var doc = _doc;
        if (b is null || doc is null || (uint)index >= (uint)doc.Lines.Count) return;
        long ms = doc.Lines[index].StartMs;
        b.PositionMs.Value = ms;
        _tickWallMs = Environment.TickCount64;
        _tickPosMs = ms;
        _ = b.Player.SeekAsync(ms);
    }

    // Per-frame stepper (gated mount): smooth clock → active line → ease scroll. Zero managed allocation.
    internal void OnFrame()
    {
        var b = _b; var doc = _doc;
        if (b is null || doc is null || doc.Lines.Count == 0) return;

        long nowMs = b.IsPlaying.Peek()
            ? _tickPosMs + (Environment.TickCount64 - _tickWallMs)
            : b.PositionMs.Peek();

        int active = ResolveLine(doc.Lines, nowMs);
        if (active != _activeLine.Peek()) _activeLine.Value = active;   // value-gated → re-render lines (emphasis)

        // Publish the smooth ms every frame so the active word-by-word line advances its karaoke wipe (only that line reads it).
        _nowMs.Value = nowMs;

        var scene = Context.Scene;
        // The scroll viewport is THIS component's own rendered root (the clipped column container). OnRealized does NOT
        // fire on a Component's ROOT element, so a self-reported handle stays null — read the engine-provided HostNode.
        var viewport = Context.HostNode;
        if (scene is null || active < 0 || (uint)active >= (uint)_lineNodes.Length) return;
        var line = _lineNodes[active];
        if (line.IsNull || !scene.IsLive(line)) return;

        // Auto-scroll: park the active line at the focal band. SNAP on the first valid frame (so opening the pane mid-
        // playback — or a track change — lands on the current line instantly), then EASE. AbsoluteRect is the arranged
        // (layout) rect; scroll is a compositor Transform, so it's scroll-invariant → target is an absolute translation.
        // Gated separately from the wipe below so a viewport hiccup never freezes the karaoke.
        if (!viewport.IsNull && scene.IsLive(viewport))
        {
            RectF vp = scene.AbsoluteRect(viewport);
            RectF lr = scene.AbsoluteRect(line);
            // AbsoluteRect REFLECTS the live scroll (the column's compositor Transform), so `delta` is the REMAINING
            // distance from the active line to the focal band — applied INCREMENTALLY to the current scroll (not as an
            // absolute target). Wait for a real layout (lr.H > 0) so the first frame doesn't snap against a 0-height rect.
            if (lr.H > 0.5f && vp.H > 0.5f)
            {
                float delta = (vp.Y + vp.H * _band) - (lr.Y + lr.H * 0.5f);
                float cur = _scrollY.Peek();
                float next = _scrollSnapped ? cur + delta * 0.18f : cur + delta;   // first valid frame snaps to the line; then eases
                _scrollSnapped = true;
                if (MathF.Abs(next - cur) < 0.05f) next = cur;   // settled → true no-op (idle the loop)
                if (next != cur) _scrollY.Value = next;
            }
        }

        // Advance the ACTIVE line's karaoke wipe split directly on the scene side-table — this re-records JUST this one
        // glyph run (no component re-render, no reshape), the SeekBar-thumb discipline applied to the wipe. (TryGetGlyphWipe
        // is false for non-word-by-word active lines, which set no wipe.) Independent of the scroll above.
        if (scene.TryGetGlyphWipe(line, out var w))
        {
            float split = LyricLineView.ComputeSplit(doc.Lines[active], nowMs);
            if (MathF.Abs(split - w.Split) > 0.0008f)
            {
                scene.SetGlyphWipe(line, w with { Split = split });
                scene.Mark(line, NodeFlags.PaintDirty);
                // Advance the wiped glow run in lockstep (same split, its own bright→transparent colors) so the bloom
                // tracks the sung words exactly.
                if ((uint)active < (uint)_glowNodes.Length)
                {
                    var g = _glowNodes[active];
                    if (!g.IsNull && scene.IsLive(g) && scene.TryGetGlyphWipe(g, out var gw))
                    {
                        scene.SetGlyphWipe(g, gw with { Split = split });
                        scene.Mark(g, NodeFlags.PaintDirty);
                    }
                }
            }
        }
    }

    /// <summary>Re-arm the scroll snap so the next valid frame JUMPS to the active line (no ease-in from the top) — called
    /// when the ticker (re)mounts, i.e. the pane is opened, so opening mid-playback lands on the current line instantly.</summary>
    internal void ResetScrollSnap() => _scrollSnapped = false;

    // Current line = the last line whose StartMs <= now (BetterLyrics' next-StartMs boundary). -1 before the first line.
    static int ResolveLine(IReadOnlyList<LyricLine> lines, long nowMs)
    {
        if (nowMs < lines[0].StartMs) return -1;
        int lo = 0, hi = lines.Count - 1, ans = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (lines[mid].StartMs <= nowMs) { ans = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return ans;
    }

    static Element Message(string msg) => new BoxEl
    {
        Grow = 1f, MinHeight = 0f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(18f, 0f, 18f, 0f),
        Children = [new TextEl(msg) { Size = 13f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap }],
    };
}

// One lyric line. Its OWN component so it can emphasise itself with springs (seeded on its distance-from-active) without
// re-rendering siblings beyond the cheap re-render the active-line change already triggers. Reports its node up so the
// view's ticker can read the active line's arranged rect for the scroll follow.
sealed class LyricLineView : Component
{
    readonly int _index;
    readonly LyricLine _line;
    readonly Signal<int> _activeLine;
    readonly FloatSignal _nowMs;
    readonly float _fontSz;
    readonly float _lineHt;
    readonly bool _centered;
    readonly Action<int, NodeHandle> _reportNode;
    readonly Action<int, NodeHandle> _reportGlow;
    readonly Action _onSeek;

    public LyricLineView(int index, LyricLine line, Signal<int> activeLine, FloatSignal nowMs,
        float fontSz, float lineHt, bool centered, Action<int, NodeHandle> reportNode, Action<int, NodeHandle> reportGlow, Action onSeek)
    {
        _index = index; _line = line; _activeLine = activeLine; _nowMs = nowMs;
        _fontSz = fontSz; _lineHt = lineHt; _centered = centered;
        _reportNode = reportNode; _reportGlow = reportGlow; _onSeek = onSeek;
    }

    public override Element Render()
    {
        int active = _activeLine.Value;                    // subscribe → re-render (re-seed springs) on active change
        bool isActive = _index == active;
        int dist = active < 0 ? 6 : Math.Abs(_index - active);

        // Emphasis — the exact BetterLyrics values (LyricsAnimator.cs): a continuous distance factor (0 at the active line
        // → 1 at the viewport edge, ≈5 lines out) drives scale 1.0→0.75 (_highlightedScale→_defaultScale), Gaussian blur
        // 0→5, and a linear opacity fade. The active line reads full-size/sharp/opaque; neighbours shrink, blur and recede.
        // All ride springs that re-seed only when the distance bucket changes (deps) then settle on the compositor.
        float f = MathF.Min(dist / 5f, 1f);
        float scale = isActive ? 1f : 1f - 0.25f * f;
        // Steeper opacity falloff than BetterLyrics' default so ONE line clearly dominates (was reading as "3 lines at once"):
        // active 1.0, then ~0.44 / 0.33 / 0.22 for the next lines.
        float opacity = isActive ? 1f : MathF.Max(0.16f, 0.55f * (1f - f));
        float blur = (isActive || dist > 6) ? 0f : 5f * f;   // DoF: Gaussian blur 0→5 with distance (BetterLyrics cap)

        UseSpring(AnimChannel.ScaleX, scale, SpringParams.Default, dist);
        UseSpring(AnimChannel.ScaleY, scale, SpringParams.Default, dist);
        UseSpring(AnimChannel.Opacity, opacity, SpringParams.Default, dist);
        UseSpring(AnimChannel.BlurSigma, blur, SpringParams.Default, dist);

        var wrap = _centered ? TextWrap.NoWrap : TextWrap.Wrap;
        Element textEl;
        if (isActive && _line.IsWordByWord && _line.Syllables.Count > 0)
        {
            // Karaoke wipe (A1) via the generic GlyphWipe primitive. The INITIAL split is set here (Peek → no per-frame
            // re-render); the ticker advances it each frame on the scene side-table keyed by THIS run's node (reported via
            // OnRealized) — so the wipe flows without re-rendering or reshaping.
            float split = ComputeSplit(_line, (long)_nowMs.Peek());
            var wipe = new TextEl(_line.Text)
            {
                Size = _fontSz, Weight = 700, Wrap = wrap, LineHeight = _lineHt, Color = Tok.TextSecondary,
                Wipe = new GlyphWipe(Before: Tok.TextPrimary, After: Tok.TextPrimary with { A = 0.4f }, Split: split, Softness: 0.025f, Lift: _lineHt * 0.1f),
                OnRealized = h => _reportNode(_index, h),
            };
            // The glow FOLLOWS the sung portion: a wiped bright→transparent run, blurred — so only sung words bloom while
            // the unsung stay clean and dim (the BetterLyrics karaoke glow). Its split rides in lockstep with the main wipe.
            var glow = new BoxEl
            {
                Blur = _centered ? 13f : 9f, HitTestVisible = false,
                Children = [new TextEl(_line.Text)
                {
                    Size = _fontSz, Weight = 700, Wrap = wrap, LineHeight = _lineHt, Color = Tok.TextPrimary,
                    Wipe = new GlyphWipe(Before: Tok.TextPrimary, After: Tok.TextPrimary with { A = 0f }, Split: split, Softness: 0.14f),
                    OnRealized = h => _reportGlow(_index, h),
                }],
            };
            textEl = new BoxEl { ZStack = true, Children = [glow, wipe] };
        }
        else if (isActive)
        {
            // Active but line-synced (no per-word timing): bright + the same glow, no wipe.
            var crisp = new TextEl(_line.Text)
            {
                Size = _fontSz, Weight = 700, Wrap = wrap, LineHeight = _lineHt, Color = Tok.TextPrimary,
                OnRealized = h => _reportNode(_index, h),
            };
            var glow = new BoxEl
            {
                Blur = _centered ? 13f : 9f, HitTestVisible = false,
                Children = [new TextEl(_line.Text) { Size = _fontSz, Weight = 700, Wrap = wrap, LineHeight = _lineHt, Color = Tok.TextPrimary with { A = 0.4f } }],
            };
            textEl = new BoxEl { ZStack = true, Children = [glow, crisp] };
        }
        else
        {
            textEl = new TextEl(_line.Text)
            {
                Size = _fontSz, Weight = 700, Wrap = wrap, LineHeight = _lineHt, Color = Tok.TextSecondary,
                OnRealized = h => _reportNode(_index, h),
            };
        }

        return new BoxEl
        {
            Direction = 1, Padding = new Edges4(2f, 5f, 2f, 5f),
            TransformOriginX = _centered ? 0.5f : 0f, TransformOriginY = 0.5f,   // scale about center (fullscreen) or left (rail)
            Cursor = CursorId.Hand, OnClick = _onSeek,
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
            Children = [textEl],
        };
    }

    // Played fraction (0..1) along the line from the sung syllables + the smooth clock — the karaoke wipe split.
    internal static float ComputeSplit(LyricLine line, long now)
    {
        var syl = line.Syllables;
        int total = 0;
        for (int i = 0; i < syl.Count; i++) total += Math.Max(1, syl[i].Text.Length);
        if (total == 0) return 0f;
        float played = 0f;
        for (int i = 0; i < syl.Count; i++)
        {
            int len = Math.Max(1, syl[i].Text.Length);
            long s = syl[i].StartMs, e = syl[i].EndMs;
            if (now >= e) { played += len; continue; }
            if (now >= s) played += len * Math.Clamp((float)(now - s) / Math.Max(1L, e - s), 0f, 1f);
            break;
        }
        return Math.Clamp(played / total, 0f, 1f);
    }
}

// Per-frame stepper for LyricsView (the SeekTicker idiom): mounted only while the rail is open, it subscribes to the host
// frame clock and advances the owner's scroll/active-line each frame. Never re-renders the owner (it only writes the
// value-gated signals the compositor binds read). Unmounts when the rail closes, idling the frame loop.
sealed class LyricsTicker : ReactiveComponent
{
    public required LyricsView Owner;

    public override Element Setup()
    {
        Owner.ResetScrollSnap();   // (re)opening the pane → the first frame snaps to the current line, no scroll-in from the top
        var tick = UseContextSignal(FrameClock.Tick);
        UseSignalEffect(() =>
        {
            _ = tick.Value;
            Owner.OnFrame();
        });
        return new BoxEl { HitTestVisible = false, Width = 0f, Height = 0f };
    }
}
