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
    readonly FloatSignal _viewportW = new(0f);   // live viewport width — lets the centered (fullscreen) column fill + center
    // Resolver output — drives per-line emphasis (each line subscribes). -1 = before the first line.
    readonly Signal<int> _activeLine = new(-1);
    // Sung-syllable (word) count for the ACTIVE line — drives the word-by-word highlight. Only the active line reads it.
    readonly Signal<int> _playedWords = new(0);

    // Live layout handles read by the ticker (the active line's arranged rect vs the viewport's → the scroll target).
    NodeHandle _viewport;
    NodeHandle[] _lineNodes = Array.Empty<NodeHandle>();

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
        bool open = _visible is not null ? _visible() : (ui?.RailOpen.Value ?? false);   // gate the ticker (rail uses RailOpen)
        long posTick = b?.PositionMs.Value ?? 0L;          // subscribe → re-anchor the smooth clock each tick
        string trackId = track?.Id ?? "";

        var docL = UseAsyncResource(
            ct => svc?.Lyrics is { } lp ? lp.GetLyricsAsync(trackId, ct) : Task.FromResult<LyricsDocument?>(null),
            (LyricsDocument?)null, trackId);

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
            _activeLine.Value = -1;
            _scrollY.Value = 0f;
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
            lineKids[i] = Embed.Comp(() => new LyricLineView(idx, line, _activeLine, _playedWords, fontSz, lineHt, centered, ReportLineNode, () => SeekToLine(idx)))
                with { Key = "ll" + idx };
        }

        // The scrolling content column — Transform is a compositor bind (no re-render on scroll). Generous top/bottom
        // padding so the first/last lines can still reach the focal band.
        var column = new BoxEl
        {
            Direction = 1, Gap = _large ? 14f : 8f, Padding = new Edges4(padX, padY, padX, padY),
            AlignItems = centered ? FlexAlign.Center : FlexAlign.Stretch,
            Width = Prop.Of(() => _large ? _viewportW.Value : float.NaN),   // fullscreen: fill width so centering works
            Transform = Prop.Of(() => Affine2D.Translation(0f, _scrollY.Value)),
            Children = lineKids,
        };

        Element? ticker = open ? Embed.Comp(() => new LyricsTicker { Owner = this }) : null;

        return new BoxEl
        {
            Grow = 1f, MinHeight = 0f, ClipToBounds = true, ZStack = true,
            OnRealized = h => _viewport = h,
            OnBoundsChanged = r => { if (r.W != _viewportW.Peek()) _viewportW.Value = r.W; },
            Children = ticker is null ? [column] : [column, ticker],
        };
    }

    void ReportLineNode(int index, NodeHandle h)
    {
        if ((uint)index < (uint)_lineNodes.Length) _lineNodes[index] = h;
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

        // Word-by-word: how many syllables of the active line have started (only the active line subscribes to this).
        int played = 0;
        if (active >= 0)
        {
            var syl = doc.Lines[active].Syllables;
            for (int i = 0; i < syl.Count; i++) { if (syl[i].StartMs <= nowMs) played = i + 1; else break; }
        }
        if (played != _playedWords.Peek()) _playedWords.Value = played;

        var scene = Context.Scene;
        if (scene is null || active < 0 || (uint)active >= (uint)_lineNodes.Length) return;
        var line = _lineNodes[active];
        if (_viewport.IsNull || line.IsNull || !scene.IsLive(_viewport) || !scene.IsLive(line)) return;

        // AbsoluteRect is the arranged (layout) rect — scroll is a compositor Transform, so these are scroll-invariant.
        // On-screen Y of the line = layoutY + scrollY; we want that at viewport.Y + band ⇒ targetScroll = band-anchor - layoutY.
        RectF vp = scene.AbsoluteRect(_viewport);
        RectF lr = scene.AbsoluteRect(line);
        float target = (vp.Y + vp.H * _band) - (lr.Y + lr.H * 0.5f);
        float cur = _scrollY.Peek();
        float next = cur + (target - cur) * 0.16f;          // exponential ease toward the focal target
        if (MathF.Abs(next - cur) < 0.05f) next = target;
        if (next != cur) _scrollY.Value = next;             // value-gated: an unmoved scroll is a true no-op
    }

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
    readonly Signal<int> _playedWords;
    readonly float _fontSz;
    readonly float _lineHt;
    readonly bool _centered;
    readonly Action<int, NodeHandle> _reportNode;
    readonly Action _onSeek;

    public LyricLineView(int index, LyricLine line, Signal<int> activeLine, Signal<int> playedWords,
        float fontSz, float lineHt, bool centered, Action<int, NodeHandle> reportNode, Action onSeek)
    {
        _index = index; _line = line; _activeLine = activeLine; _playedWords = playedWords;
        _fontSz = fontSz; _lineHt = lineHt; _centered = centered;
        _reportNode = reportNode; _onSeek = onSeek;
    }

    public override Element Render()
    {
        int active = _activeLine.Value;                    // subscribe → re-render (re-seed springs) on active change
        bool isActive = _index == active;
        int dist = active < 0 ? 6 : Math.Abs(_index - active);

        float scale = isActive ? 1f : 0.94f;
        float opacity = isActive ? 1f : dist == 1 ? 0.6f : dist == 2 ? 0.42f : dist == 3 ? 0.3f : 0.22f;

        // Smooth the emphasis: springs re-seed only when the distance bucket changes (deps), then ride the compositor.
        UseSpring(AnimChannel.ScaleX, scale, SpringParams.Default, dist);
        UseSpring(AnimChannel.ScaleY, scale, SpringParams.Default, dist);
        UseSpring(AnimChannel.Opacity, opacity, SpringParams.Default, dist);

        Element textEl;
        if (isActive && _line.IsWordByWord && _line.Syllables.Count > 0)
        {
            // Discrete word-by-word karaoke: played words bright, unplayed dim — re-mints only the active line on each
            // word boundary (subscribing to _playedWords HERE keeps non-active lines off the word cadence). The soft
            // sweeping gradient wipe is the A1 engine upgrade (DrawGlyphRunGradient).
            int played = _playedWords.Value;
            var syl = _line.Syllables;
            var spans = new TextSpan[syl.Count];
            for (int i = 0; i < syl.Count; i++)
                spans[i] = new TextSpan(syl[i].Text, Weight: 700, Color: i < played ? Tok.TextPrimary : Tok.TextSecondary);
            textEl = new SpanTextEl(spans) { Size = _fontSz, Weight = 700, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, LineHeight = _lineHt };
        }
        else
        {
            ColorF color = isActive ? Tok.TextPrimary : Tok.TextSecondary;
            textEl = new TextEl(_line.Text) { Size = _fontSz, Weight = 700, Color = color, Wrap = TextWrap.Wrap, LineHeight = _lineHt };
        }

        return new BoxEl
        {
            Direction = 1, Padding = new Edges4(2f, 5f, 2f, 5f),
            TransformOriginX = _centered ? 0.5f : 0f, TransformOriginY = 0.5f,   // scale about center (fullscreen) or left (rail)
            Cursor = CursorId.Hand, OnClick = _onSeek,
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
            OnRealized = h => _reportNode(_index, h),
            Children = [textEl],
        };
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
        var tick = UseContextSignal(FrameClock.Tick);
        UseSignalEffect(() =>
        {
            _ = tick.Value;
            Owner.OnFrame();
        });
        return new BoxEl { HitTestVisible = false, Width = 0f, Height = 0f };
    }
}
