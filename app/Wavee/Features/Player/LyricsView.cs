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
using Wavee.Backend.Lyrics;
using Wavee.Core;

namespace Wavee;

// The lyrics depth-of-field: ONE look for everyone, no tiers, no flags. Each dimmed line, by its distance from the active
// line, gets a soft self-blur — the full BetterLyrics depth-of-field (out to 6 rows, sigma up to 5, matching their
// `5 * distanceFactor`). 0 ⇒ no blur layer is emitted at all (SceneRecorder drops sigma ≤ 0.01).
static class LyricsFx
{
    public static float DofSigma(int dist) => dist <= 0 ? 0f : 5f * MathF.Min(dist / 5f, 1f);
}

sealed class LyricsView : Component
{
    internal readonly record struct FrameDiagnostics(long NowMs, long AuthMs, int ActiveLine, int VoiceLine, bool ActiveChanged, bool VoiceChanged, bool ScrollSnapped, bool Playing, int LineCount);
    internal static FrameDiagnostics LastFrameDiagnostics { get; private set; }

    // ── Probe seam (WAVEE_LYRICS_ADVANCE_PROBE) ──────────────────────────────────────────────────────────────────────
    // The redesigned lyrics-advance probe drives the media clock SYNCHRONOUSLY (one advance == the frame that records its
    // scroll settle) so the async 16 ms ticker's decoupling can't smear the correlation. ProbeSyncMode silences the timer
    // (StartTimer no-ops); ProbeStep injects the clock via OnFrame; ProbeForceSnapped skips the one-time instant-jump latch
    // so the first measured advance is a real ProgrammaticMode spring (the settle-frame path BUG1 lives on).
    internal static bool ProbeSyncMode;
    internal static LyricsView? ProbeActive;
    internal void ProbeStep(long nowMs) => OnFrame(forceVisual: true, probeNowMs: nowMs);
    internal void ProbeForceSnapped() => _scrollSnapped = true;
    internal NodeHandle ProbeViewport => _viewportNode;
    internal int ProbeActiveLine => _activeLine.Peek();
    internal int ProbeLineCount => _doc?.Lines.Count ?? 0;
    internal long ProbeLineStartMs(int i) => _doc is { } d && (uint)i < (uint)d.Lines.Count ? d.Lines[i].StartMs : 0L;
    internal NodeHandle ProbeLineNode(int i) => _lineNodes is { } ln && (uint)i < (uint)ln.Length ? ln[i] : default;
    internal NodeHandle ProbeGlowNode(int i) => _glowNodes is { } gn && (uint)i < (uint)gn.Length ? gn[i] : default;

    // The karaoke wipe advances on this cadence (the ticker period + the OnFrame throttle gate). 16 ms ≈ 60 Hz (was 33 =
    // 30 Hz). The wipe is AMBIENT motion, so the host ambient cap (Program.cs ambientFps / FG_ANIM_FPS) must ALSO allow
    // ≥60 or RecommendedWaitMs throttles the sweep back down — both were raised together once per-frame cost got cheap.
    internal const long KaraokeWipeIntervalMs = 16;

    // Eye-leads-voice anticipation: emphasis + scroll resolve this many ms AHEAD of the true clock so the line is rising
    // into focus as the first syllable lands (the karaoke wipe stays on true time). Inside the safe 100-500 ms karaoke
    // lead window; comfortably above one 16 ms frame.
    internal const long LeadMs = 140;

    // Dejittered media clock (see OnFrame): a free-running wall-clock base + an additive slew correction, instead of a
    // hard re-anchor on every laggy IPC snapshot. RebaseClock seeds all of these together.
    long _baseWall;                     // monotonic wall anchor (Environment.TickCount64)
    long _basePos;                      // playback position at _baseWall
    float _offset;                      // additive slew correction folded from IPC-snapshot disagreement
    bool _wasPlaying;                   // last frame's play state — rebase the clock on the paused→playing transition
    long _lastAuthMs = long.MinValue;   // last authoritative IPC PositionMs the ticker reacted to
    long _lastDisplay;                  // last displayed nowMs — monotonic-while-playing guard (no backward wipe/line retreat)
    long _lastWipeWallMs;
    int _lastWipeLine = -1;

    bool _scrollSnapped;
    readonly Signal<int> _activeLine = new(-1);   // emphasis + scroll target (lead-shifted)
    readonly Signal<int> _voiceLine = new(-1);    // line currently being sung (true time) — owns the karaoke wipe/glow
    readonly Signal<bool> _interlude = new(false);// active line sung out into a long instrumental gap — recede it
    readonly FloatSignal _nowMs = new(0f);

    NodeHandle _viewportNode = NodeHandle.Null;
    NodeHandle[] _lineNodes = Array.Empty<NodeHandle>();
    NodeHandle[] _glowNodes = Array.Empty<NodeHandle>();

    LyricsDocument? _doc;
    LyricsMeasuredLayout? _layout;
    PlaybackBridge? _b;

    readonly bool _large;
    readonly Func<bool>? _visible;
    float _band = 0.40f;

    // Opt-in lyrics-search debug surface (a corner button → a per-source "why no/which lyric" panel). Env-gated like the
    // FPS HUD so it never shows for normal users; set WAVEE_LYRICS_DEBUG=1 to enable.
    static readonly bool _lyricsDebug =
        Environment.GetEnvironmentVariable("WAVEE_LYRICS_DEBUG") is "1" or "true" or "TRUE";
    readonly Signal<bool> _debugOpen = new(false);

    public LyricsView(bool large = false, Func<bool>? visible = null) { _large = large; _visible = visible; }

    public override Element Render()
    {
        var ui = UseContext(ShellUi.Slot);
        var b = UseContext(PlaybackBridge.Slot);
        var svc = UseContext(Services.Slot);
        _b = b;

        var track = b?.CurrentTrack.Value;
        bool open = _visible is not null ? _visible() : (ui?.RailOpen.Value ?? false);
        // Peek, NEVER .Value: subscribing the lyrics view to the IPC position snapshot forces a full re-render every
        // tick, which re-runs the Skel.Region content delegate -> re-realizes the virtual line window -> ReconcileWindow
        // tears down + re-mounts every LyricLineView (Components are not recyclable), re-seeding each line's opacity/scale
        // spring from the fresh node's default paint (1.0) -- the all-lines-pulse bug. The position is consumed by the
        // per-frame ticker (OnFrame, via Peek) and re-anchored there.
        long posNow = b?.PositionMs.Peek() ?? 0L;
        string trackId = track?.Id ?? "";
        string artist = track is { Artists.Count: > 0 } ? track.Artists[0].Name : "";
        string fetchKey = trackId.Length == 0 ? "" : trackId + "|" + artist;

        var docL = UseAsyncResource(
            ct => fetchKey.Length > 0 && svc?.Lyrics is { } lp
                ? lp.GetLyricsAsync(trackId, ct)
                : Task.FromResult<LyricsDocument?>(null),
            (LyricsDocument?)null, fetchKey);

        var doc = docL.Value.Value;

        if (b is null || svc is null) return new BoxEl { Grow = 1f };
        if (track is null)
        {
            ClearDocument();
            return Message("Nothing playing");
        }

        if (_doc is not null && !StringComparer.Ordinal.Equals(_doc.TrackId, trackId))
            ClearDocument();

        if (doc is { Lines.Count: > 0 } ready)
            PrepareDocument(ready, posNow);

        Element body = Skel.Region<LyricsDocument?>(
            docL,
            shimmerSource: () => LyricsShimmer(_large),
            content: d => d is { Lines.Count: > 0 } readyDoc ? LyricsContent(readyDoc) : Message("No lyrics available"),
            reveal: SkelReveal.FadeOnly,
            onFailed: () => Message("No lyrics available"),
            isEmpty: d => d?.Lines is null || d.Lines.Count == 0,
            onEmpty: () => Message("No lyrics available"),
            style: new SkeletonStyle(Tok.FillSubtleSecondary, RowGap: _large ? 18f : 14f, BarRadius: 6f, TextRatio: 0.86f),
            smoothResize: false);

        Element? ticker = open ? Embed.Comp(() => new LyricsTicker { Owner = this }) : null;
        var stack = new BoxEl
        {
            Grow = 1f, MinHeight = 0f, ClipToBounds = true, Direction = 1,
            Children = ticker is null ? [body] : [body, ticker],
        };

        if (!_lyricsDebug) return stack;
        return new BoxEl
        {
            Grow = 1f, MinHeight = 0f, ZStack = true,
            Children = _debugOpen.Value ? [stack, DebugOverlay(trackId)] : [stack, DebugButton()],
        };
    }

    // ── Lyrics-search debug surface (WAVEE_LYRICS_DEBUG=1) ────────────────────────────────────────────────────────────
    // A corner pill opens a panel that shows, for the playing track, the request metadata the sources searched with and a
    // per-source row (outcome + timing + the breadcrumb "why") + the reranker's verdict. Data is LyricsDiagnostics, which
    // the AggregatingLyricsProvider publishes once per fetch (so the report is already there for the current track).

    Element DebugButton() => new BoxEl
    {
        // Full-bleed PASS-THROUGH positioner (its HitTestPassThrough is honoured — see FpsOverlay) pinning the pill
        // bottom-right; only the pill is hittable, so the lyrics underneath stay scrollable/clickable.
        Grow = 1f, MinHeight = 0f, HitTestPassThrough = true,
        Direction = 1, Justify = FlexJustify.End, AlignItems = FlexAlign.End,
        Padding = new Edges4(0f, 0f, 12f, 12f),
        Children =
        [
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = 5f,
                Padding = new Edges4(9f, 5f, 9f, 5f), Corners = CornerRadius4.All(7f),
                Fill = new ColorF(0f, 0f, 0f, 0.55f), BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                Cursor = CursorId.Hand, OnClick = () => _debugOpen.Value = true,
                Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
                Children = [new TextEl("lyrics debug") { Size = 11f, Weight = 600, Color = Tok.TextSecondary }],
            },
        ],
    };

    Element DebugOverlay(string trackId)
    {
        var report = LyricsDiagnostics.ForTrack(trackId);
        var rows = new List<Element>
        {
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = 8f,
                Children =
                [
                    new TextEl("Lyrics search") { Size = 15f, Weight = 700, Color = Tok.TextPrimary, Grow = 1f },
                    new BoxEl
                    {
                        Padding = new Edges4(9f, 4f, 9f, 4f), Corners = CornerRadius4.All(6f), Fill = Tok.FillSubtleSecondary,
                        Cursor = CursorId.Hand, OnClick = () => _debugOpen.Value = false,
                        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
                        Children = [new TextEl("close ✕") { Size = 11f, Weight = 600, Color = Tok.TextSecondary }],
                    },
                ],
            },
        };

        if (report is null)
        {
            rows.Add(new TextEl("No search recorded for this track yet — it may be a local/fake track, or the fetch is still in flight. Close and reopen to refresh.")
            { Size = 12f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, LineHeight = 17f });
        }
        else
        {
            rows.Add(new TextEl(report.Summary) { Size = 12.5f, Weight = 600, Color = Tok.AccentTextPrimary, Wrap = TextWrap.Wrap, LineHeight = 18f });
            rows.Add(new TextEl($"“{report.Title}” — {(report.Artist.Length > 0 ? report.Artist : "(no artist)")}")
            { Size = 12f, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, LineHeight = 16f });
            rows.Add(new TextEl($"album: {(report.Album.Length > 0 ? report.Album : "—")}   ·   {report.DurationMs / 1000}s   ·   ISRC: {report.Isrc ?? "—"}")
            { Size = 11f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, LineHeight = 15f });
            rows.Add(new BoxEl { Height = 1f, Fill = Tok.StrokeCardDefault });
            foreach (var t in report.Sources) rows.Add(SourceRow(t));
            if (report.Sources.Count == 0)
                rows.Add(new TextEl("(no sources ran)") { Size = 12f, Color = Tok.TextSecondary });
        }

        return new BoxEl
        {
            Grow = 1f, MinHeight = 0f, Direction = 1, Fill = new ColorF(0.04f, 0.04f, 0.06f, 0.97f),
            Children =
            [
                new ScrollEl
                {
                    Grow = 1f, MinHeight = 0f,
                    Content = new BoxEl { Direction = 1, Gap = 9f, Padding = new Edges4(18f, 16f, 18f, 18f), Children = rows.ToArray() },
                },
            ],
        };
    }

    static Element SourceRow(LyricsSourceTrace t)
    {
        ColorF dot = t.Outcome switch
        {
            LyricsOutcome.Hit => new ColorF(0.30f, 0.78f, 0.45f, 1f),     // green
            LyricsOutcome.Timeout => new ColorF(0.92f, 0.70f, 0.25f, 1f), // amber
            LyricsOutcome.Error => new ColorF(0.90f, 0.35f, 0.38f, 1f),   // red
            LyricsOutcome.Skipped => new ColorF(0.40f, 0.42f, 0.50f, 1f), // dim — didn't run (a faster match won)
            _ => new ColorF(0.55f, 0.57f, 0.62f, 1f),                     // grey (Miss)
        };

        var lines = new List<Element>
        {
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = 7f,
                Children =
                [
                    new BoxEl { Width = 8f, Height = 8f, Corners = CornerRadius4.All(4f), Fill = dot },
                    new TextEl(t.SourceId) { Size = 12.5f, Weight = 700, Color = t.Winner ? Tok.AccentTextPrimary : Tok.TextPrimary },
                    new TextEl($"{t.Outcome.ToString().ToUpperInvariant()} · {t.ElapsedMs}ms{(t.Winner ? "  ★ winner" : "")}")
                    { Size = 11f, Weight = 600, Color = Tok.TextSecondary, Grow = 1f },
                ],
            },
        };
        if (t.Detail.Length > 0)
            lines.Add(new TextEl(t.Detail) { Size = 11f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, LineHeight = 15f });
        if (t.Outcome == LyricsOutcome.Hit && t.RerankReason.Length > 0)
            lines.Add(new TextEl($"rerank score {t.Score:F2}  ·  {t.RerankReason}") { Size = 10.5f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, LineHeight = 14f });

        return new BoxEl { Direction = 1, Gap = 3f, Children = lines.ToArray() };
    }

    void PrepareDocument(LyricsDocument doc, long posMs)
    {
        if (ReferenceEquals(_doc, doc)) return;

        _doc = doc;
        _lineNodes = new NodeHandle[doc.Lines.Count];
        _glowNodes = new NodeHandle[doc.Lines.Count];
        _activeLine.Value = ResolveLine(doc.Lines, posMs);
        _nowMs.Value = posMs;
        _scrollSnapped = false;
        ResetWipeThrottle();
        RebaseClock(posMs);   // seed the dejittered clock anchor for the freshly loaded doc
    }

    void ClearDocument()
    {
        if (_doc is null && _lineNodes.Length == 0) return;
        _doc = null;
        _lineNodes = Array.Empty<NodeHandle>();
        _glowNodes = Array.Empty<NodeHandle>();
        _activeLine.Value = -1;
        _nowMs.Value = 0f;
        _scrollSnapped = false;
        ResetWipeThrottle();
        _lastAuthMs = long.MinValue;   // reset so a freshly loaded doc re-anchors on the next snapshot
        _lastDisplay = 0L;
    }

    // Seed every dejittered-clock field from an authoritative position so all re-anchor sites (doc load, click-seek) agree.
    // Sets _lastDisplay so the monotonic guard adopts the new (possibly backward) position immediately, and _lastAuthMs so
    // the next OnFrame doesn't re-treat the same value as a snapshot disagreement. Does NOT touch _wasPlaying — the
    // paused→playing transition still rebases on resume so a pause gap never leaks into the wall delta.
    void RebaseClock(long pos)
    {
        _baseWall = Environment.TickCount64;
        _basePos = pos;
        _offset = 0f;
        _lastAuthMs = pos;
        _lastDisplay = pos;
    }

    Element LyricsContent(LyricsDocument doc)
    {
        var lines = doc.Lines;
        // Bigger type (rail 20 -> 26) and a tighter rhythm. Rows are CONTENT-FIT (variable height) via the measured
        // layout below, so a one-line lyric is short and a two-line lyric is tall — no dead space, no mid-word clipping.
        float fontSz = _large ? 36f : 26f;
        float lineHt = _large ? 46f : 33f;          // ~1.27x (was 1.4x) — denser block
        float rowPad = _large ? 9f : 7f;            // vertical padding per row; inter-line gap = 2*rowPad
        float sidePad = _large ? 48f : 22f;         // keep text off the rail edges without making the narrow panel cramped
        float rowEst = lineHt + 2f * rowPad;        // measured-layout seed = a single-line row's height
        bool centered = _large;
        _band = _large ? 0.42f : 0.40f;

        if (_layout is null || MathF.Abs(_layout.Band - _band) > 0.001f || MathF.Abs(_layout.Estimate - rowEst) > 0.5f)
            _layout = new LyricsMeasuredLayout(rowEst, _band);

        var layout = _layout;
        return Virtual.Custom(
            lines.Count,
            layout,
            i =>
            {
                var idx = i;
                return Embed.Comp(() => new LyricLineView(
                    idx, lines[idx], _activeLine, _voiceLine, _interlude, _nowMs, fontSz, lineHt, rowPad, sidePad, centered,
                    ReportLineNode, ReportGlowNode, () => SeekToLine(idx))) with { Key = "ll" + idx };
            },
            keyOf: i => "ll" + i,
            overscan: _large ? 8 : 7) with
        {
            Grow = 1f,
            MinHeight = 0f,
            AutoEdgeFade = true,
            SuppressScrollBar = true,
            OnRealized = h => _viewportNode = h,
        };
    }

    static Element LyricsShimmer(bool large)
    {
        float padX = large ? 48f : 22f;
        float padTop = large ? 150f : 110f;
        float rowH = large ? 32f : 22f;
        float gap = large ? 24f : 18f;
        float[] widths = large ? [0.82f, 0.66f, 0.74f, 0.58f, 0.70f, 0.50f] : [0.86f, 0.72f, 0.80f, 0.62f, 0.76f, 0.58f];
        var rows = new Element[widths.Length];

        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = new BoxEl
            {
                Width = large ? 520f * widths[i] : 255f * widths[i],
                Height = rowH,
                Corners = CornerRadius4.All(6f),
                Fill = Tok.FillSubtleSecondary,
                AlignSelf = large ? FlexAlign.Center : FlexAlign.Start,
            };
        }

        return new BoxEl
        {
            Grow = 1f,
            MinHeight = 0f,
            Direction = 1,
            Gap = gap,
            Padding = new Edges4(padX, padTop, padX, 0f),
            Children = rows,
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
        RebaseClock(ms);    // seed all clock fields; _lastAuthMs=ms keeps OnFrame from re-treating our own jump as a seek
        _scrollSnapped = false;
        ResetWipeThrottle();
        _ = b.Player.SeekAsync(ms);
    }

    internal void OnFrame(bool forceVisual = false, long probeNowMs = long.MinValue)
    {
        var b = _b; var doc = _doc;
        if (b is null || doc is null || doc.Lines.Count == 0) return;

        // Dejittered media clock. The authoritative IPC PositionMs is itself a coarse ~1 Hz extrapolation; the old code
        // HARD re-anchored on every snapshot, so a delayed/corrected one snapped nowMs — and since BOTH the active-line
        // resolve and the karaoke wipe read nowMs, the line jumped (even backward) and the fill lurched. Instead: DEADBAND
        // tiny disagreements (IPC jitter), gently SLEW small ones into an additive offset (no visible jump), SNAP only a
        // true seek, plus a MONOTONIC-while-playing guard so the wipe/line never tick backward except on a real seek. Peek
        // only (no .Value subscribe ⇒ no re-render ⇒ no all-lines-pulse); pure scalar math, zero per-frame alloc. With the
        // deadband, steady-state extrapolation stays byte-identical to before, so the swap timing + skip-submit are intact.
        long auth = b.PositionMs.Peek();
        long wallMs = Environment.TickCount64;
        bool playing = b.IsPlaying.Peek();
        long nowMs;
        if (probeNowMs != long.MinValue)
        {
            // Probe sync-advance (WAVEE_LYRICS_ADVANCE_PROBE): the probe owns the media clock, so a line advance and the
            // RunFrame that records the resulting scroll SETTLE are the same frame (the async 16 ms Timer is silenced by
            // ProbeSyncMode). Deterministic + free of the ticker decoupling — the basis of the trustworthy re-probe.
            nowMs = probeNowMs; auth = probeNowMs; playing = true;
            _baseWall = wallMs; _basePos = probeNowMs; _offset = 0f; _lastAuthMs = probeNowMs; _lastDisplay = probeNowMs;
        }
        else if (!playing)
        {
            // Paused: the snapshot is the truth. Pin the base to it so a later RESUME doesn't leak the pause gap into the
            // wall delta, and a paused scrub follows immediately.
            nowMs = auth;
            _baseWall = wallMs; _basePos = auth; _offset = 0f; _lastAuthMs = auth; _lastDisplay = auth;
        }
        else
        {
            if (!_wasPlaying)
            {
                // Just resumed: rebase clean so the (untimed) pause duration doesn't appear as a forward jump.
                _baseWall = wallMs; _basePos = auth; _offset = 0f; _lastAuthMs = auth; _lastDisplay = auth;
            }
            else if (auth != _lastAuthMs)
            {
                _lastAuthMs = auth;
                long predicted = _basePos + (wallMs - _baseWall);
                long err = auth - (long)(predicted + _offset);
                long ae = err < 0 ? -err : err;
                if (ae <= 12) { /* deadband: ignore IPC jitter < ~12 ms */ }
                else if (ae <= 250) { _offset += err * 0.5f; }   // slew: absorb ~half per snapshot (closes in 1-2)
                else                                              // snap: a real seek / device transfer
                {
                    _baseWall = wallMs; _basePos = auth; _offset = 0f;
                    _lastDisplay = auth;        // bypass the monotonic guard for a legitimate (possibly backward) seek
                    _scrollSnapped = false;     // next ScrollActiveIntoView does the INSTANT-jump latch, not an ease across the song
                    ResetWipeThrottle();        // re-evaluate the wipe at the new position
                }
            }
            long nowRaw = (long)(_basePos + (wallMs - _baseWall) + _offset);
            nowMs = Math.Max(nowRaw, _lastDisplay);   // monotonic while playing
            _lastDisplay = nowMs;
        }
        _wasPlaying = playing;

        // Eye-leads-voice: emphasis + scroll resolve against a LEAD-shifted clock (~140 ms early) so the line is rising
        // into focus as the first syllable lands, while the karaoke wipe/glow stay on the TRUE audio clock (voiceLine +
        // raw nowMs) so the fill matches the voice. Two indices — one must NOT drive the other (lead-shifting a single
        // index would retarget the wipe to the not-yet-singing line, killing the fill on the line you are hearing).
        int active = ResolveLine(doc.Lines, nowMs + LeadMs);   // emphasis + scroll (anticipates)
        int voiceLine = ResolveLine(doc.Lines, nowMs);          // wipe + glow (on true time)
        bool activeChanged = active != _activeLine.Peek();
        if (activeChanged) _activeLine.Value = active;
        int prevVoiceLine = _voiceLine.Peek();
        bool voiceChanged = voiceLine != prevVoiceLine;
        if (voiceChanged) _voiceLine.Value = voiceLine;
        _nowMs.Value = nowMs;

        var scene = Context.Scene;
        if (scene is null) return;
        if (voiceChanged) ClearGlowNode(scene, prevVoiceLine);
        if (active < 0 || (uint)active >= (uint)doc.Lines.Count) return;

        // Instrumental-gap (interlude) state — word-by-word only. During a gap the lead keeps `active` on the just-sung
        // line N until 140 ms before N+1; if N is sung out and a long (>=4 s) gap precedes N+1, recede N instead of
        // leaving it frozen-fully-lit. It auto-ends when the lead flips active to N+1 (whose syllables aren't sung yet ⇒
        // not sung-out). Line-synced lyrics have no syllable end (EndMs == next StartMs ⇒ gap delta 0), so interlude stays
        // false — correct: there is no word timing to recede against, so don't fake it.
        var al = doc.Lines[active];
        long sungOutPoint = al.IsWordByWord && al.Syllables.Count > 0 ? al.Syllables[^1].EndMs : al.EndMs.GetValueOrDefault();
        long nextStartMs = active + 1 < doc.Lines.Count ? doc.Lines[active + 1].StartMs : long.MaxValue;
        bool interlude = nowMs >= sungOutPoint && (nextStartMs - sungOutPoint) >= 4000;
        if (interlude != _interlude.Peek()) _interlude.Value = interlude;

        if (!_scrollSnapped || activeChanged || forceVisual)
            ScrollActiveIntoView(scene, active);
        LastFrameDiagnostics = new(nowMs, auth, active, voiceLine, activeChanged, voiceChanged, _scrollSnapped, playing, doc.Lines.Count);

        // The karaoke wipe/glow live on the VOICE line (true time), which trails the emphasis line during the lead window.
        if ((uint)voiceLine >= (uint)_lineNodes.Length) return;
        var line = _lineNodes[voiceLine];
        if (line.IsNull || !scene.IsLive(line)) return;

        if (scene.TryGetGlyphWipe(line, out var w))
        {
            float split = LyricLineView.ComputeSplit(doc.Lines[voiceLine], nowMs);
            // Small POSITIVE wipe lead: nudge the bright boundary a few % ahead of the strictly-played fraction so the edge
            // reads as anticipating the voice. Keep it tiny — the GlyphWipe band is centered (the leading feather already
            // sits ~Fade/2 ahead); a large lead would render glyphs fully-sung ahead of the voice. Body stays linear.
            const float WipeLead = 0.04f;
            if (split > 0f && split < 1f) split = Math.Clamp(split + WipeLead, 0f, 1f);
            // Pixel-quantize the wipe boundary (mirror SeekBar): snapping Split to the line's on-screen pixel width makes
            // sub-pixel ticks produce byte-identical DrawGlyphRunGradient bytes, so the host skip-submit hash gate elides
            // the karaoke ticks that don't move the wipe a whole pixel (most of them, during slow syllables).
            float runW = scene.AbsoluteRect(line).W;
            if (runW > 1f) split = MathF.Round(split * runW) / runW;
            bool forceWipe = forceVisual || voiceLine != _lastWipeLine || _lastWipeWallMs == 0L;
            if (!forceWipe && wallMs - _lastWipeWallMs < KaraokeWipeIntervalMs) return;

            _lastWipeWallMs = wallMs;
            _lastWipeLine = voiceLine;

            bool splitMoved = MathF.Abs(split - w.Split) > 0.0008f;
            if (splitMoved)
            {
                scene.SetGlyphWipe(line, w with { Split = split });
                scene.Mark(line, NodeFlags.PaintDirty);
            }

            // Voice-line glow node: keep its wipe split in sync (when it moved) AND drive the held-syllable bloom —
            // a SUSTAINED syllable (>= BetterLyrics' 700 ms LongDurationSyllable gate) swells the glow's blur, then
            // releases. The bloom is independent of the split delta (a long held note barely advances the split but must
            // still swell), so it is NOT gated by splitMoved. The bloom sigma is quantized to a 0.25 grid
            // (ComputeHeldGlowSigma), so a slow/held note keeps byte-identical bytes and DOES skip-submit.
            if ((uint)voiceLine < (uint)_glowNodes.Length)
            {
                var g = _glowNodes[voiceLine];
                if (!g.IsNull && scene.IsLive(g))
                {
                    bool glowDirty = false;
                    if (splitMoved && scene.TryGetGlyphWipe(g, out var gw))
                    {
                        scene.SetGlyphWipe(g, gw with { Split = split });
                        glowDirty = true;
                    }
                    float baseSigma = _large ? 6f : 4f;                  // the active line's constant soft glow
                    float peakExtra = (_large ? 46f : 33f) * 0.09f;      // held-syllable bloom amplitude — a gentle swell (~base*1.6 at peak), not a ballooning halo that reads as straining in/out breathing
                    float sigma = LyricLineView.ComputeHeldGlowSigma(doc.Lines[voiceLine], nowMs, baseSigma, peakExtra);
                    ref var gp = ref scene.Paint(g);
                    if (gp.BlurCachePolicy != BlurCachePolicy.HoldOrSkipOnMiss) { gp.BlurCachePolicy = BlurCachePolicy.HoldOrSkipOnMiss; glowDirty = true; }
                    if (MathF.Abs(gp.BlurSigma - sigma) > 0.05f) { gp.BlurSigma = sigma; glowDirty = true; }
                    if (glowDirty) scene.Mark(g, NodeFlags.PaintDirty);
                }
            }
        }
    }

    void ClearGlowNode(SceneStore scene, int line)
    {
        if ((uint)line >= (uint)_glowNodes.Length) return;
        var g = _glowNodes[line];
        if (g.IsNull || !scene.IsLive(g)) return;
        ref var gp = ref scene.Paint(g);
        bool dirty = false;
        if (gp.BlurSigma != 0f) { gp.BlurSigma = 0f; dirty = true; }
        if (gp.BlurCachePolicy != BlurCachePolicy.Normal) { gp.BlurCachePolicy = BlurCachePolicy.Normal; dirty = true; }
        if (dirty) scene.Mark(g, NodeFlags.PaintDirty);
    }

    void ScrollActiveIntoView(SceneStore scene, int active)
    {
        var viewport = _viewportNode;
        var layout = _layout;
        if (layout is null || viewport.IsNull || !scene.IsLive(viewport) || !scene.HasScroll(viewport))
            return;

        ref ScrollState sc = ref scene.ScrollRef(viewport);
        if (sc.ViewportH <= 0.5f || sc.ContentH <= 0.5f) return;

        // Keep the measured layout's focal top/bottom pad in sync with the live viewport. The engine's measured arrange
        // path does NOT push the viewport to the layout (unlike the fixed path), so the app does it here; the value is an
        // instance field read by every geometry call, so one push per frame keeps ContentExtent/Window/ItemRect correct.
        layout.SetViewport(sc.ViewportH, sc.ViewportW);

        RectF item = layout.ItemRect(active, sc.ViewportW);
        float target = item.Y + item.H * 0.5f - sc.ViewportH * _band;
        target = Math.Clamp(target, 0f, MathF.Max(0f, sc.ContentH - sc.ViewportH));

        if (!_scrollSnapped)
        {
            _scrollSnapped = true;
            sc.Phase = ScrollIntegrator.Idle;
            sc.PhaseFlags = 0;
            sc.FlingVelocity = 0f;
            sc.FlingRetargeted = false;
            sc.FlingSnapTarget = float.NaN;
            sc.PendingTargetY = float.NaN;
            sc.OffsetY = target;
            sc.TargetY = target;
            ApplyScrollTransform(scene, in sc, target);
            // Instant jump to the active line WITHOUT a LyricsView re-render: latch the offset as a scroll-restore and
            // mark LAYOUT, so FlexLayout.ArrangeViewport re-asserts the offset + content transform + re-realizes the
            // virtual window (reuseOverlap — existing rows kept). Context.RequestRerender() would instead re-run the
            // Skel.Region content delegate, rebuild the VirtualListEl, and remount every line node, re-seeding each
            // line's springs from default paint (1.0) — the "all lines flash active for a frame" bug (on open / seek).
            sc.RestoreX = sc.OffsetX;
            sc.RestoreY = target;
            sc.RestorePending = true;
            scene.Mark(viewport, NodeFlags.LayoutDirty | NodeFlags.VirtualRangeDirty);
            return;
        }

        // Already chasing (PendingTargetY set) — or idle at — this target ⇒ nothing to do.
        if (MathF.Abs((float.IsNaN(sc.PendingTargetY) ? sc.OffsetY : sc.PendingTargetY) - target) <= 0.5f) return;

        // Velocity-continuous re-target: only zero the carried spring velocity on the FIRST entry into a Programmatic
        // WheelAnimating chase. A re-target while ALREADY easing (dense lyric sections, lines ~200-300 ms apart) KEEPS the
        // velocity so the engine spring chains smoothly to the new target instead of restarting a decelerating chase (the
        // "list trails the song" defect). The engine ScrollIntegrator integrates the critically-damped spring (no overshoot).
        bool alreadyProgrammatic = sc.Phase == ScrollIntegrator.WheelAnimating && (sc.PhaseFlags & ScrollState.PhaseProgrammatic) != 0;
        if (!alreadyProgrammatic)
        {
            sc.Phase = ScrollIntegrator.WheelAnimating;
            sc.PhaseFlags = ScrollState.PhaseProgrammatic;
            sc.FlingVelocity = 0f;
        }
        sc.FlingRetargeted = false;
        sc.FlingSnapTarget = float.NaN;
        sc.PendingTargetY = target;
        Context.ArmScroll?.Invoke(viewport);
        // No Context.RequestRerender(): ArmScroll drives the smooth scroll and the engine ScrollIntegrator re-realizes the
        // virtual window on each offset move (reuseOverlap), while the active-line emphasis re-renders the line components
        // IN PLACE via the _activeLine signal (same node ⇒ springs retarget by rebase). Re-rendering LyricsView here would
        // rebuild the virtual window and remount every line, re-seeding its springs from default paint — every line would
        // flash "active" for a frame on each line change (the reported swap-flash).
    }

    static void ApplyScrollTransform(SceneStore scene, in ScrollState sc, float target)
    {
        var contentNode = sc.ContentNode;
        if (contentNode.IsNull || !scene.IsLive(contentNode)) return;

        scene.Paint(contentNode).LocalTransform = Affine2D.Translation(0f, -target);
        scene.Mark(contentNode, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
    }

    internal void ResetScrollSnap()
    {
        _scrollSnapped = false;
        ResetWipeThrottle();
    }

    void ResetWipeThrottle()
    {
        _lastWipeWallMs = 0L;
        _lastWipeLine = -1;
    }

    static int ResolveLine(IReadOnlyList<LyricLine> lines, long nowMs)
    {
        if (lines.Count == 0 || nowMs < lines[0].StartMs) return -1;
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
        Padding = new Edges4(22f, 0f, 22f, 0f),
        Children = [new TextEl(msg) { Size = 13f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap }],
    };
}

// Variable-height (measured) virtual layout for the lyrics list. Each row is CONTENT-FIT — a one-line lyric is short, a
// two-line lyric is tall — so there is no dead space and nothing clips/ellipsizes; a top/bottom focal pad still lets the
// FIRST and LAST lines scroll to the focal band. Implements the engine's measured seam (estimate-then-correct over an
// ExtentTable; the engine measures each realized row and feeds SetMeasured) AND the viewport seam (for the focal pad).
// The app also pushes the viewport (LyricsView.ScrollActiveIntoView) because the engine's measured arrange path, unlike
// the fixed-geometry path, does not call SetViewport.
sealed class LyricsMeasuredLayout : IMeasuredVirtualLayout, IViewportVirtualLayout
{
    public readonly float Estimate;   // per-row height seed for not-yet-measured rows (a single-line row)
    public readonly float Band;
    float _viewport;
    ExtentTable? _table;

    public LyricsMeasuredLayout(float estimate, float band)
    {
        Estimate = MathF.Max(1f, estimate);
        Band = Math.Clamp(band, 0.05f, 0.95f);
    }

    ExtentTable Ensure(int n)
    {
        if (_table is null) _table = new ExtentTable(n, Estimate);
        else if (_table.Count != n) _table.Reset(n, Estimate);
        return _table;
    }

    // Pad so a centered active line can sit at the focal band even at the very top/bottom of the list. Estimate*0.5 is the
    // half-height correction (the band centers a row's MIDDLE); exact enough since the active row is always measured.
    float TopPad => _viewport <= 0f ? 0f : MathF.Max(0f, _viewport * Band - Estimate * 0.5f);
    float BottomPad => _viewport <= 0f ? 0f : MathF.Max(0f, _viewport * (1f - Band) - Estimate * 0.5f);

    public void SetViewport(float mainExtent, float crossSize) => _viewport = MathF.Max(0f, mainExtent);

    public float ContentExtent(int itemCount, float crossSize)
        => itemCount <= 0 ? 0f : TopPad + (float)Ensure(itemCount).Total + BottomPad;

    public void Window(int itemCount, float crossSize, float viewportExtent, float scrollOffset, int overscan, out int first, out int last)
    {
        if (itemCount <= 0) { first = last = 0; return; }
        var t = Ensure(itemCount);
        float o = scrollOffset - TopPad;
        first = Math.Max(0, t.IndexAt(MathF.Max(0f, o)) - overscan);
        last = Math.Min(itemCount, t.IndexAt(MathF.Max(0f, o + viewportExtent)) + 1 + overscan);
        if (last < first) last = first;
    }

    public RectF ItemRect(int index, float crossSize)
    {
        float pos = _table?.OffsetOf(index) ?? index * Estimate;
        float ext = _table?.ExtentAt(index) ?? Estimate;
        return new RectF(0f, TopPad + pos, crossSize, ext);
    }

    public void SetMeasured(int index, float mainExtent, float crossSize) => _table?.SetExtent(index, mainExtent);
    public float OffsetOf(int index, float crossSize) => TopPad + (_table?.OffsetOf(index) ?? index * Estimate);
    public int IndexAt(float offset, float crossSize) => _table?.IndexAt(MathF.Max(0f, offset - TopPad)) ?? 0;
}

sealed class LyricLineView : Component
{
    readonly int _index;
    readonly LyricLine _line;
    readonly Signal<int> _activeLine;
    readonly Signal<int> _voiceLine;
    readonly Signal<bool> _interlude;
    readonly FloatSignal _nowMs;
    readonly float _fontSz;
    readonly float _lineHt;
    readonly float _rowPad;
    readonly float _sidePad;
    readonly bool _centered;
    readonly Action<int, NodeHandle> _reportNode;
    readonly Action<int, NodeHandle> _reportGlow;
    readonly Action _onSeek;

    public LyricLineView(int index, LyricLine line, Signal<int> activeLine, Signal<int> voiceLine, Signal<bool> interlude, FloatSignal nowMs,
        float fontSz, float lineHt, float rowPad, float sidePad, bool centered, Action<int, NodeHandle> reportNode, Action<int, NodeHandle> reportGlow, Action onSeek)
    {
        _index = index; _line = line; _activeLine = activeLine; _voiceLine = voiceLine; _interlude = interlude; _nowMs = nowMs;
        _fontSz = fontSz; _lineHt = lineHt; _rowPad = rowPad; _sidePad = sidePad; _centered = centered;
        _reportNode = reportNode; _reportGlow = reportGlow; _onSeek = onSeek;
    }

    public override Element Render()
    {
        int active = _activeLine.Value;
        int voice = _voiceLine.Value;
        bool isActive = _index == active;
        bool isKaraokeLive = isActive || _index == voice;
        bool isVoice = _index == voice;   // currently being sung — owns the karaoke wipe (trails emphasis during the lead)
        bool interlude = isActive && _interlude.Value;   // active line sung out into a long instrumental gap — recede it
        int dist = active < 0 ? 6 : Math.Abs(_index - active);

        float f = MathF.Min(dist / 5f, 1f);
        // Emphasis targets. Active line: full focus (scale 1 / opacity 1 / crisp). During an instrumental interlude the
        // still-active sung-out line recedes to a calm look instead of sitting frozen-fully-lit. Dimmed lines fall off by
        // distance. Active is the single visual focus for scale, opacity, and blur; voice only drives the karaoke wipe
        // and glow during the lead split, so depth never disagrees with emphasis.
        float scale = interlude ? 0.92f : isActive ? 1f : 1f - 0.25f * f;
        float opacity = interlude ? 0.55f : isActive ? 1f : MathF.Max(0.16f, 0.55f * (1f - f));
        float blur = interlude ? LyricsFx.DofSigma(1) : isKaraokeLive ? 0f : LyricsFx.DofSigma(dist);

        // Emphasis scale AND the DoF blur are STATIC per-dist STEPS (not springs); only opacity is a spring. A scale
        // spring animates the row's world scale for ~0.55 s on every line advance — but the self-blur subtree shares this
        // node, so an animating scale changes its device size + world transform every frame ⇒ a cross-frame pin cache
        // CONTENT-miss every frame (backdrop-effects-animation.md §FA-2a), forcing ~12 Gaussians/frame during the
        // auto-scroll ease. Stepping scale (co-stepped with the DofSigma step below, same integer `dist`) makes each row
        // POSITION-ONLY during the ease ⇒ the pin cache HITS (~12 cheap composites instead of ~12 Gaussians/frame). The
        // one-frame size step coincides with the σ step + the wipe jump on the line-change frame (the eye is on the moving
        // active line). Opacity STAYS a spring: it drives only the composite GroupAlpha (cache-neutral, not in the pin
        // key), so the fade-into-focus stays smooth for free. DepKey folds in the interlude bit so opacity retargets on
        // interlude entry/exit. Same persistent-node path — no remount, no all-lines-pulse.
        var key = DepKey.From(dist, interlude ? 1 : 0);
        UseSpring(AnimChannel.Opacity, opacity, SpringParams.FromResponse(0.55f, 1.0f), key);
        // The DoF blur is a pure STEP of integer distance (DofSigma): a spring would manufacture intermediate sigmas every
        // settle frame — each marking the node TransformDirty (defeating skip-submit) and stepping the heavy Gaussian
        // kernel frame-to-frame = the during-a-line DoF breathing. The bucketed sigma is set STATICALLY as the row's Blur
        // element property (see the BoxEl below); scale is set STATICALLY alongside it (ScaleX/ScaleY). A settled line is
        // then byte-identical frame-to-frame, and a line change jumps both one bucket in a single frame. On a change the
        // leaving/entering line re-renders via _activeLine/_voiceLine, so the reconciler re-asserts the new static values.

        var wrap = _centered ? TextWrap.NoWrap : TextWrap.Wrap;
        int maxLines = _centered ? 1 : 2;
        Element textEl;

        // ~half-glyph wipe feather (BetterLyrics LyricsLineRendererBase: fadeBand ≈ 0.5/charCount) — softer than a flat edge.
        float soft = Math.Clamp(0.5f / Math.Max(1, _line.Text.Length), 0.03f, 0.08f);
        // The karaoke wipe sub-tree renders on the active line AND the voice line — during the ~140 ms lead the voice line
        // (still being sung) trails the emphasis line, but its fill must keep running. Emphasis (scale/opacity) follows
        // `active`; the wipe split follows true time via _nowMs.
        // Word-by-word line: ALWAYS a two-child ZStack [glow, main], in EVERY state (active / voice / dimmed). The two
        // nodes mount ONCE and only their PROPERTIES toggle, so a line LEAVING the voice slot (the row just above active
        // during the ~140 ms lead) is an in-place update — NOT the BoxEl↔TextEl child-type swap that forced a Remove+Mount,
        // re-shaped the glyph run + missed the blur cache = the one-frame flicker on the lines above active. The main
        // text's glyphs never re-shape on that transition (its string is unchanged), and because both nodes persist,
        // OnRealized (which fires only on mount) keeps the wipe/glow node reports (_reportNode/_reportGlow, read by
        // OnFrame) valid across every transition WITHOUT a remount. (Line-synced lines keep the isActive/else shapes below.)
        if (_line.IsWordByWord && _line.Syllables.Count > 0)
        {
            bool lit = isKaraokeLive;
            float split = lit ? ComputeSplit(_line, (long)_nowMs.Peek()) : 0f;
            // Soft glow = a blurred copy of the played glyphs UNDER the main text; OnFrame drives its NodePaint.BlurSigma
            // (base + held-syllable bloom) while it is the voice line. Empty string when dimmed so a peripheral line pays
            // no second glyph run — it re-shapes once as it ENTERS the voice slot (one line, entering focus), never on the
            // line leaving it (the flicker case).
            Element glow = lit
                ? LineText(_line.Text, Tok.TextPrimary) with
                  {
                      Wipe = new GlyphWipe(Before: Tok.TextPrimary, After: Tok.TextPrimary with { A = 0f }, Split: split, Softness: 0.14f),
                      OnRealized = h => _reportGlow(_index, h),
                  }
                : LineText("", Tok.TextPrimary) with { OnRealized = h => _reportGlow(_index, h) };
            // Main text: base Secondary; when lit the wipe reveals Primary up to Split (Lift cut to 0.03·lineHeight — a
            // subtle lift-into-focus, BetterLyrics' transient per-glyph rise). When dimmed it is the plain Secondary text
            // (no wipe) — the SAME node updated in place, so its glyphs do not re-bake as it leaves the voice slot.
            Element main = lit
                ? LineText(_line.Text, Tok.TextSecondary) with
                  {
                      Wipe = new GlyphWipe(Before: Tok.TextPrimary, After: Tok.TextPrimary with { A = 0.4f }, Split: split, Softness: soft, Lift: _lineHt * 0.03f),
                      OnRealized = h => _reportNode(_index, h),
                  }
                : LineText(_line.Text, Tok.TextSecondary) with { OnRealized = h => _reportNode(_index, h) };
            textEl = new BoxEl { ZStack = true, Children = [glow, main] };
        }
        else if (isActive)
        {
            // Line-level active line (no syllables ⇒ no held-note bloom): a static soft glow halo under the crisp text.
            var crisp = LineText(_line.Text, Tok.TextPrimary) with { OnRealized = h => _reportNode(_index, h) };
            var glow = new BoxEl
            {
                Blur = _centered ? 13f : 9f, HitTestVisible = false,
                Children = [LineText(_line.Text, Tok.TextPrimary with { A = 0.4f })],
            };
            textEl = new BoxEl { ZStack = true, Children = [glow, crisp] };
        }
        else
        {
            textEl = LineText(_line.Text, Tok.TextSecondary) with { OnRealized = h => _reportNode(_index, h) };
        }

        return new BoxEl
        {
            Direction = 1,
            // Static depth-of-field: the dimmed-line sigma is a Blur element property (DofSigma step), not a per-frame
            // spring — so a settled line's bytes never change and the kernel never breathes. Active line ⇒ blur 0 ⇒ no
            // self-blur layer (SceneRecorder drops sigma ≤ 0.01); its soft glow is a child blur group instead.
            Blur = blur,
            // Emphasis scale is a STATIC per-dist step (co-stepped with Blur), pivoting on TransformOriginX/Y below — see
            // the comment above: a scale spring here would content-miss the self-blur pin cache every ease frame.
            ScaleX = scale,
            ScaleY = scale,
            // Normal (not HoldIfCached): DofSigma STEPS one bucket on every line switch, so the blur-subtree hash always
            // misses the cache that frame — and HoldIfCached's miss fallback draws the subtree CRISP for one frame (the
            // whole panel flashing sharp on switch). Normal re-blurs synchronously on a miss; a settled row's stable hash
            // still pin-hits and skip-submits, so byte-identical settled frames are preserved.
            BlurCachePolicy = BlurCachePolicy.Normal,
            // No fixed Height — the row sizes to its text (1 line short, 2 lines tall); the measured layout reads that
            // natural height so there is no dead space. Vertical padding (_rowPad) is the inter-line gap.
            Shrink = 0f,
            Padding = new Edges4(_sidePad, _rowPad, _sidePad, _rowPad),
            Justify = FlexJustify.Center,
            AlignItems = _centered ? FlexAlign.Center : FlexAlign.Stretch,
            TransformOriginX = _centered ? 0.5f : 0f,
            TransformOriginY = 0.5f,
            Cursor = CursorId.Hand,
            OnClick = _onSeek,
            Role = AutomationRole.Button,
            Focusable = true,
            AllowFocusOnInteraction = false,
            Children = [textEl],
        };

        TextEl LineText(string text, ColorF color) => new(text)
        {
            Size = _fontSz,
            Weight = 700,
            Wrap = wrap,
            LineHeight = _lineHt,
            Color = color,
            MaxLines = maxLines,
            // Rail: no ellipsis — a long line wraps cleanly (up to MaxLines) instead of the confusing mid-word "lif.."
            // trim. Fullscreen stays single-line (NoWrap) so it keeps the ellipsis.
            Trim = _centered ? TextTrim.CharacterEllipsis : TextTrim.None,
        };
    }

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

    // Held / long-syllable glow bloom (BetterLyrics LongDurationSyllable scope). A syllable sustained >= 700 ms (the
    // BetterLyrics LyricsGlowEffectLongSyllableDuration default) swells the active line's glow; a short syllable gets
    // none. Envelope = a soft trapezoid over the syllable (rise ~22%, hold, fall ~22%), the smooth analogue of
    // BetterLyrics' Keyframe(target,in)->Keyframe(0,out). Amplitude <paramref name="peakExtra"/> (~lineHeight*0.2, its
    // auto glow target) is added over the constant <paramref name="baseSigma"/>. Returns baseSigma outside any held note.
    internal const long HeldSyllableMs = 700;
    internal static float ComputeHeldGlowSigma(LyricLine line, long now, float baseSigma, float peakExtra)
    {
        if (peakExtra <= 0f) return baseSigma;
        var syl = line.Syllables;
        for (int i = 0; i < syl.Count; i++)
        {
            long s = syl[i].StartMs, e = syl[i].EndMs;
            if (now < s) break;            // playhead hasn't reached this syllable yet
            if (now >= e) continue;        // already past it
            long dur = e - s;
            if (dur < HeldSyllableMs) return baseSigma;   // short syllable: no bloom
            float t = Math.Clamp((float)(now - s) / dur, 0f, 1f);
            // Wide shoulders (slow rise/fall) plus a floor so the envelope never collapses to 0 mid-note: consecutive
            // sustained syllables then hold a steady elevated glow that only swells gently toward peak, instead of
            // dipping to base at every syllable boundary (the ~1 Hz in/out breathing the eye reads as straining).
            float env = MathF.Max(0.45f, Smooth01(0f, 0.35f, t) * (1f - Smooth01(0.65f, 1f, t)));
            // Snap the bloom to a 0.25 grid: under the 16 ms timer's jitter (15/16/17/coalesced) the raw envelope steps
            // irregularly, and the call site only delta-gates at 0.05 — so the held note's glow sigma keeps creeping,
            // marking PaintDirty on a self-blur node every tick (the sustained-note breathing). Quantized, a held/slow
            // note yields byte-identical sigma frame-to-frame, so consecutive frames stay byte-stable and the skip-submit
            // hash elides them. The glow analogue of the wipe's pixel-quantize. baseSigma (4/6) is already on the grid,
            // so the resting (no-bloom) glow is unchanged.
            return MathF.Round((baseSigma + peakExtra * env) * 4f) / 4f;
        }
        return baseSigma;
    }

    static float Smooth01(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / MathF.Max(1e-5f, b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

}

sealed class LyricsTicker : ReactiveComponent
{
    public required LyricsView Owner;
    Timer? _timer;
    int _timerGeneration;

    public override Element Setup()
    {
        Owner.ResetScrollSnap();
        LyricsView.ProbeActive = Owner;   // probe hook (harmless otherwise): the live instance the advance-probe drives
        var bridge = UseContextSignal(PlaybackBridge.Slot);
        var post = UsePost();
        UseSignalEffect(() =>
        {
            Reactive.OnCleanup(StopTimer);
            var b = bridge.Value;
            if (b is null)
            {
                StopTimer();
                return;
            }
            if (!b.IsPlaying.Value)
            {
                StopTimer();
                _ = b.PositionMs.Value;
                Owner.OnFrame(forceVisual: true);
                return;
            }
            StartTimer(post);
        });
        return new BoxEl { HitTestVisible = false, Width = 0f, Height = 0f };
    }

    void StartTimer(Action<Action> post)
    {
        if (LyricsView.ProbeSyncMode) return;   // probe drives OnFrame synchronously via ProbeStep — no async ticker
        if (_timer is not null) return;
        int generation = Interlocked.Increment(ref _timerGeneration);
        _timer = new Timer(_ => post(() =>
        {
            if (Volatile.Read(ref _timerGeneration) == generation) Owner.OnFrame();
        }), null, 0, LyricsView.KaraokeWipeIntervalMs);
    }

    void StopTimer()
    {
        var timer = _timer;
        if (timer is null) return;
        _timer = null;
        Interlocked.Increment(ref _timerGeneration);
        timer.Dispose();
    }
}
