using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
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

enum LyricsFollowMode : byte { Following, DetachedActive, DetachedIdle, Resyncing }
enum FollowArmResult : byte { Unavailable, AtTarget, Armed }
enum FollowScrollIntent : byte { Normal, Resync }

sealed class LyricsView : Component
{
    internal readonly record struct FrameDiagnostics(long NowMs, long AuthMs, int ActiveLine, int VoiceLine, bool ActiveChanged, bool VoiceChanged, bool ScrollSnapped, bool Playing, int LineCount);
    internal static FrameDiagnostics LastFrameDiagnostics { get; private set; }

    // ── Probe seam (WAVEE_LYRICS_ADVANCE_PROBE) ──────────────────────────────────────────────────────────────────────
    // The redesigned lyrics-advance probe drives the media clock SYNCHRONOUSLY (one advance == the frame that records its
    // scroll settle) so the async 16 ms ticker's decoupling can't smear the correlation. ProbeSyncMode silences the ticker
    // (the LyricsTicker UseInterval stays disabled); ProbeStep injects the clock via OnFrame; ProbeForceSnapped skips the one-time instant-jump latch
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

    // Glow cross-fade (the halo must never hard-toggle): per-line alpha SIGNALS, bound as each row's glow-wrapper Opacity.
    // Bound (not a static element value) so a row re-render re-asserts the LIVE fade value instead of snapping it — the
    // reconciler skips paint writes for bound Opacity. OnFrame ramps the incoming voice line in and the outgoing one out
    // over GlowFadeMs; at rest no signal is written, so settled frames stay byte-identical (skip-submit intact).
    const float GlowFadeMs = 240f;
    const float GlowOutMs = 320f;           // end-of-line melt window (BetterLyrics ≈350 ms; clamped to line end on media clock)
    // Held-note glow (WaveeMusic LyricsAnimator "辉光（长音节）" / BetterLyrics): the halo blooms ONLY while a syllable of
    // at least this duration is being sung — a whole-line wash reads as noise; a swell on the held note reads as voice.
    const float HeldGlowMinMs = 700f;       // WaveeMusic LyricsGlowEffectLongSyllableDuration default
    const float HeldGlowRampMaxMs = 500f;   // swell-in cap; short-ish holds swell across half the note instead
    const long ResyncIdleMs = 4000L;
    const int ResyncProgressSteps = 120;       // 30 Hz-equivalent ring updates across the four-second idle window
    FloatSignal[] _glowAlpha = Array.Empty<FloatSignal>();
    int _glowInLine = -1; long _glowInStart; float _glowInFrom;
    int _glowOutLine = -1; long _glowOutStart; float _glowOutFrom;
    readonly Signal<LyricsFollowMode> _followMode = new(LyricsFollowMode.Following);
    readonly FloatSignal _resyncProgress = new(1f);
    long _resyncDeadlineWallMs;

    bool _scrollSnapped;
    readonly Signal<int> _activeLine = new(-1);   // emphasis + scroll target (lead-shifted)
    readonly Signal<int> _voiceLine = new(-1);    // line currently being sung (true time) — owns the karaoke wipe/glow
    readonly Signal<bool> _interlude = new(false);// active line sung out into a long instrumental gap — recede it
    readonly FloatSignal _nowMs = new(0f);

    // Per-line PACKED emphasis (bucket 0..6 in bits 0-2, interlude flag in bit 3). One VALUE-GATED Signal per line: the
    // reactive core propagates staleness eagerly (a Memo does NOT gate downstream re-renders by value), so the ONLY way a
    // line re-renders solely on ITS OWN emphasis change is a per-line signal whose setter no-ops when the packed value is
    // unchanged. As `_activeLine` sweeps, PushEmphasis rewrites all lines but only the ~dozen crossing a bucket boundary
    // actually notify — the rest (already at bucket 6) are no-op writes. Sized in PrepareDocument alongside `_glowAlpha`.
    Signal<int>[] _lineEmphasis = Array.Empty<Signal<int>>();
    readonly Signal<int> _emphasisFallback = new(6);   // bucket 6 (fully dim) — only used during a transient array-resize gap

    bool _docWordByWord;   // any line word-timed with syllables ⇒ the karaoke wipe needs 60 Hz; line-synced docs pace at 30 Hz
    // The lyrics ticker cadence: word-by-word karaoke needs the 16 ms (~60 Hz) sweep; line-synced docs (no per-frame wipe)
    // pace at 33 ms (~30 Hz) — the 240 ms glow fade still gets 7+ steps and the wipe block no-ops for them.
    internal long WipeIntervalMs => _docWordByWord ? 16 : 33;

    NodeHandle _viewportNode = NodeHandle.Null;
    NodeHandle[] _lineNodes = Array.Empty<NodeHandle>();
    NodeHandle[] _glowNodes = Array.Empty<NodeHandle>();
    NodeHandle[] _dofNodes = Array.Empty<NodeHandle>();

    LyricsDocument? _doc;
    LyricsDocument? _pendingUpgrade;
    Loadable<LyricsDocument?>? _docLoadable;
    LyricsMeasuredLayout? _layout;
    PlaybackBridge? _b;
    Services? _svc;

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
        _svc = svc;
        var post = UsePost();

        var track = b?.CurrentTrack.Value;
        bool open = _visible is not null ? _visible() : (ui?.RailOpen.Value ?? false);
        UseEffect(() =>
        {
            if (!open) ResetFollowState(Context.Scene);
        }, DepKey.From(open));
        // Peek, NEVER .Value: subscribing the lyrics view to the IPC position snapshot forces a full re-render every
        // tick, which re-runs the Skel.Region content delegate -> re-realizes the virtual line window -> ReconcileWindow
        // tears down + re-mounts every LyricLineView (Components are not recyclable), re-seeding each line's opacity/scale
        // spring from the fresh node's default paint (1.0) -- the all-lines-pulse bug. The position is consumed by the
        // per-frame ticker (OnFrame, via Peek) and re-anchored there.
        long posNow = b?.PositionMs.Peek() ?? 0L;
        string trackId = track?.Id ?? "";
        string artist = track is { Artists.Count: > 0 } ? track.Artists[0].Name : "";
        string fetchKey = trackId.Length == 0 ? "" : trackId + "|" + artist;

        var docL = UseResource(
            ct => fetchKey.Length > 0 && svc?.Lyrics is { } lp
                ? lp.GetLyricsAsync(trackId, ct)
                : Task.FromResult<LyricsDocument?>(null),
            (LyricsDocument?)null, fetchKey).Loadable;
        _docLoadable = docL;
        UseSignalEffect(() =>
        {
            string currentTrackId = _b?.CurrentTrack.Value?.Id ?? "";
            if (currentTrackId.Length == 0 || _svc?.Lyrics is not IUpgradingLyricsProvider up) return;

            var sub = up.LyricsUpgraded.Subscribe(new LyricsUpgradeObserver(upgrade =>
            {
                if (!StringComparer.Ordinal.Equals(upgrade.TrackId, currentTrackId)) return;
                post(() =>
                {
                    string liveTrackId = _b?.CurrentTrack.Peek()?.Id ?? "";
                    if (StringComparer.Ordinal.Equals(liveTrackId, upgrade.TrackId))
                        ReceiveUpgrade(upgrade);
                });
            }));
            Reactive.OnCleanup(() => sub.Dispose());
        });

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

        bool timedLyrics = doc is { Lines.Count: > 0 } d && IsTimed(d);
        Element? ticker = open && timedLyrics ? Embed.Comp(() => new LyricsTicker { Owner = this }) : null;
        Element resync = ResyncOverlay();
        var stack = new BoxEl
        {
            Grow = 1f, MinHeight = 0f, ClipToBounds = true, ZStack = true,
            Children = ticker is null ? [body, resync] : [body, ticker, resync],
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

    Element ResyncOverlay()
    {
        string label = Loc.Get(Strings.Player.ResyncLyrics);
        return new BoxEl
        {
            // Keep the FULL-BLEED pass-through node OUTSIDE Flow.Show. Control-flow/component wrappers mirror layout
            // participation but not HitTestPassThrough; returning this positioner through Flow.Show would therefore
            // leave a full-viewport hittable wrapper above the list and silently kill wheel/touch scrolling.
            Grow = 1f, MinHeight = 0f, HitTestPassThrough = true,
            Direction = 1, Justify = FlexJustify.End, AlignItems = FlexAlign.Center,
            Padding = new Edges4(0f, 0f, 0f, _large ? 28f : 18f),
            Children =
            [
                Flow.Show(
                    () => _followMode.Value is LyricsFollowMode.DetachedActive or LyricsFollowMode.DetachedIdle,
                    new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center, Gap = 8f,
                        Padding = new Edges4(11f, 7f, 13f, 7f), Corners = CornerRadius4.All(16f),
                        Fill = Tok.FillSolidBase with { A = 0.92f }, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                        HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
                        PressScale = 0.98f, Cursor = CursorId.Hand,
                        OnClick = () => BeginResync(Context.Scene),
                        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
                        Enter = new EnterExit(Dy: 4f, Opacity: 0f, Active: true),
                        Exit = new EnterExit(Dy: 4f, Opacity: 0f, Active: true),
                        Layout = LayoutTransition.Fade,
                        Children =
                        [
                            ProgressRing.Create(_resyncProgress, size: 18f, foreground: Tok.AccentDefault,
                                track: Tok.StrokeControlDefault with { A = 0.55f }),
                            new TextEl(label) { Size = 12f, Weight = 650, Color = Tok.TextPrimary },
                        ],
                    }),
            ],
        };
    }

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
                Fill = Tok.FillSolidBase with { A = 0.90f }, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
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
            Grow = 1f, MinHeight = 0f, Direction = 1, Fill = Tok.FillSolidBase with { A = 0.97f },
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

        var previous = _doc;
        if (previous is null || !SameLineShape(previous, doc)) _layout = null;
        _doc = doc;
        _lineNodes = new NodeHandle[doc.Lines.Count];
        _glowNodes = new NodeHandle[doc.Lines.Count];
        _dofNodes = new NodeHandle[doc.Lines.Count];
        _glowAlpha = new FloatSignal[doc.Lines.Count];
        for (int i = 0; i < _glowAlpha.Length; i++) _glowAlpha[i] = new FloatSignal(0f);
        _lineEmphasis = new Signal<int>[doc.Lines.Count];
        for (int i = 0; i < _lineEmphasis.Length; i++) _lineEmphasis[i] = new Signal<int>(6);   // seed = fully dim
        _docWordByWord = IsWordByWordDoc(doc);
        _glowInLine = -1; _glowOutLine = -1;
        if (IsTimed(doc))
        {
            _activeLine.Value = ResolveLine(doc.Lines, posMs);
        }
        else
        {
            _activeLine.Value = -1;
            _voiceLine.Value = -1;
            _interlude.Value = false;
        }
        PushEmphasis();   // seed per-line emphasis for the freshly loaded doc (before OnFrame drives it)
        _nowMs.Value = posMs;
        _scrollSnapped = false;
        ResetWipeThrottle();
        RebaseClock(posMs);   // seed the dejittered clock anchor for the freshly loaded doc
    }

    static bool SameLineShape(LyricsDocument a, LyricsDocument b)
    {
        if (!StringComparer.Ordinal.Equals(a.TrackId, b.TrackId) || a.Lines.Count != b.Lines.Count) return false;
        for (int i = 0; i < a.Lines.Count; i++)
            if (!StringComparer.Ordinal.Equals(a.Lines[i].Text, b.Lines[i].Text)) return false;
        return true;
    }

    static bool IsWordByWordDoc(LyricsDocument doc)
    {
        foreach (var l in doc.Lines)
            if (l.IsWordByWord && l.Syllables.Count > 0)
                return true;
        return false;
    }

    // Packed per-line emphasis: bucket (distance from active, clamped 0..6) in bits 0-2, interlude flag in bit 3. Clamp 6
    // is exact for the look — the DoF falloff saturates at dist/5 and the glyph-mount `near` threshold is dist ≤ 2, so any
    // line ≥6 away is visually identical; clamping lets far lines share bucket 6 and skip the re-render as active sweeps.
    static int PackEmphasis(int index, int active, bool interlude)
    {
        int bucket = active < 0 ? 6 : Math.Min(Math.Abs(index - active), 6);
        int e = bucket;
        if (interlude && index == active) e |= 8;   // interlude recede applies only to the active line itself
        return e;
    }

    // Rewrite every line's emphasis signal from the current active/interlude. Value-gated: only lines that actually change
    // bucket notify their subscriber, so a boundary re-renders ~a dozen rows instead of the whole realized document.
    void PushEmphasis()
    {
        var em = _lineEmphasis;
        if (em.Length == 0) return;
        int active = _activeLine.Peek();
        bool interlude = _interlude.Peek();
        for (int i = 0; i < em.Length; i++) em[i].Value = PackEmphasis(i, active, interlude);
    }

    void ClearDocument()
    {
        ResetFollowState(Context.Scene);
        _layout = null;
        _viewportNode = NodeHandle.Null;
        if (_doc is null && _lineNodes.Length == 0)
        {
            _pendingUpgrade = null;
            return;
        }
        _doc = null;
        _pendingUpgrade = null;
        _lineNodes = Array.Empty<NodeHandle>();
        _glowNodes = Array.Empty<NodeHandle>();
        _dofNodes = Array.Empty<NodeHandle>();
        _glowAlpha = Array.Empty<FloatSignal>();
        _lineEmphasis = Array.Empty<Signal<int>>();
        _glowInLine = -1; _glowOutLine = -1;
        _activeLine.Value = -1;
        _voiceLine.Value = -1;
        _interlude.Value = false;
        _nowMs.Value = 0f;
        _scrollSnapped = false;
        ResetWipeThrottle();
        _lastAuthMs = long.MinValue;   // reset so a freshly loaded doc re-anchors on the next snapshot
        _lastDisplay = 0L;
    }

    void ReceiveUpgrade(LyricsDocument upgrade)
    {
        if (upgrade.Lines.Count == 0) return;
        var current = _docLoadable?.Value.Peek() ?? _doc;
        if (current is not null && !StringComparer.Ordinal.Equals(current.TrackId, upgrade.TrackId)) return;
        if (current is not null && !IsRicherLyrics(upgrade, current)) return;

        bool playing = _b?.IsPlaying.Peek() == true;
        if (!playing || _doc is null || _activeLine.Peek() < 0)
            ApplyLyricsUpgrade(upgrade, _b?.PositionMs.Peek() ?? 0L);
        else
            _pendingUpgrade = upgrade;
    }

    void ApplyLyricsUpgrade(LyricsDocument upgrade, long posMs)
    {
        _pendingUpgrade = null;
        _docLoadable?.SetReady(upgrade);
        PrepareDocument(upgrade, posMs);
    }

    static bool IsRicherLyrics(LyricsDocument next, LyricsDocument current)
    {
        int nr = Richness(next), cr = Richness(current);
        if (nr != cr) return nr > cr;
        if (nr < 3) return false;
        return SyllableCount(next) > SyllableCount(current);
    }

    static int Richness(LyricsDocument doc)
    {
        foreach (var l in doc.Lines)
            if (l.IsWordByWord && l.Syllables.Count > 0)
                return 3;
        return doc.Sync switch
        {
            LyricsSyncKind.Syllable => 3,
            LyricsSyncKind.Line => 2,
            LyricsSyncKind.Unsynced => 1,
            _ => 0,
        };
    }

    static int SyllableCount(LyricsDocument doc)
    {
        int n = 0;
        foreach (var l in doc.Lines) n += l.Syllables.Count;
        return n;
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
        if (!IsTimed(doc)) return UnsyncedLyricsContent(doc);

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
                    idx, lines[idx],
                    (uint)idx < (uint)_lineEmphasis.Length ? _lineEmphasis[idx] : _emphasisFallback, _nowMs, _followMode,
                    idx < _glowAlpha.Length ? _glowAlpha[idx] : null,
                    fontSz, lineHt, rowPad, sidePad, centered,
                    ReportLineNode, ReportGlowNode, ReportDofNode, () => SeekToLine(idx))) with { Key = "ll" + idx };
            },
            keyOf: i => "ll" + i,
            // Realize the WHOLE document (a lyrics doc is at most a few hundred cheap rows): with a 4-5 row overscan,
            // lines cold-mounted mid-auto-scroll — text shaping + DoF layer popping in as the spring passed them (the
            // "future lines flicker in" report). Realized-but-offscreen lines cost nothing per frame (clip-culled).
            overscan: Math.Min(lines.Count, 400)) with
        {
            Grow = 1f,
            MinHeight = 0f,
            // E4 normally mounts visible-only and warms overscan at 12 rows/frame. Lyrics deliberately request the
            // entire bounded document: every row must be measured before follow geometry is trusted, and no upcoming
            // line may materialize seconds later as the budget catches up.
            RealizeOverscanImmediately = true,
            AutoEdgeFade = true,
            SuppressScrollBar = true,
            OnScrollGeometryChanged = (
                static g => g.UserScrollActive ? 1L : 0L,
                g => OnLyricsScrollActivity(g.UserScrollActive, Environment.TickCount64)),
            OnRealized = h => _viewportNode = h,
        };
    }

    Element UnsyncedLyricsContent(LyricsDocument doc)
    {
        float fontSz = _large ? 34f : 24f;
        float lineHt = _large ? 44f : 32f;
        float rowPad = _large ? 8f : 6f;
        float sidePad = _large ? 48f : 22f;
        bool centered = _large;
        var rows = new Element[doc.Lines.Count];

        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = new BoxEl
            {
                Direction = 1,
                Shrink = 0f,
                Padding = new Edges4(sidePad, rowPad, sidePad, rowPad),
                AlignItems = centered ? FlexAlign.Center : FlexAlign.Stretch,
                Children =
                [
                    new TextEl(doc.Lines[i].Text)
                    {
                        Size = fontSz,
                        Weight = 700,
                        Wrap = centered ? TextWrap.NoWrap : TextWrap.Wrap,
                        LineHeight = lineHt,
                        Color = Tok.TextPrimary with { A = 0.88f },
                        MaxLines = centered ? 1 : 0,
                        Trim = centered ? TextTrim.CharacterEllipsis : TextTrim.None,
                    },
                ],
            };
        }

        return new ScrollEl
        {
            Grow = 1f,
            MinHeight = 0f,
            AutoEdgeFade = true,
            SuppressScrollBar = true,
            ScrollKey = "lyrics:unsynced:" + doc.TrackId,
            Content = new BoxEl
            {
                Direction = 1,
                Padding = new Edges4(0f, _large ? 44f : 26f, 0f, _large ? 44f : 26f),
                Children = rows,
            },
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

    void ReportDofNode(int index, NodeHandle h)
    {
        if ((uint)index < (uint)_dofNodes.Length) _dofNodes[index] = h;
    }

    internal LyricsFollowMode FollowModeValue => _followMode.Value;   // LyricsTicker-only subscription; parent Render never reads it

    static bool SuppressesDof(LyricsFollowMode mode) => mode != LyricsFollowMode.Following;

    float DofForLine(int index)
    {
        int active = _activeLine.Peek();
        if (active < 0) return LyricsFx.DofSigma(6);
        int dist = Math.Min(Math.Abs(index - active), 6);
        if (_interlude.Peek() && index == active) return LyricsFx.DofSigma(1);
        return LyricsFx.DofSigma(dist);
    }

    void ApplyDofSuppression(SceneStore? scene, bool suppress)
    {
        if (scene is null) return;
        for (int i = 0; i < _dofNodes.Length; i++)
        {
            var h = _dofNodes[i];
            if (h.IsNull || !scene.IsLive(h)) continue;
            float blur = suppress ? 0f : DofForLine(i);
            ref NodePaint p = ref scene.Paint(h);
            if (MathF.Abs(p.BlurSigma - blur) <= 0.001f) continue;
            p.BlurSigma = blur;
            scene.Mark(h, NodeFlags.PaintDirty);
        }
    }

    void SetFollowMode(LyricsFollowMode next, SceneStore? scene)
    {
        var previous = _followMode.Peek();
        if (previous == next) return;
        bool wasSuppressed = SuppressesDof(previous);
        bool nowSuppressed = SuppressesDof(next);
        _followMode.Value = next;
        if (wasSuppressed != nowSuppressed) ApplyDofSuppression(scene, nowSuppressed);
    }

    void ResetFollowState(SceneStore? scene)
    {
        _resyncDeadlineWallMs = 0L;
        _resyncProgress.Value = 1f;
        SetFollowMode(LyricsFollowMode.Following, scene);
    }

    void OnLyricsScrollActivity(bool userScrollActive, long wallMs)
    {
        if (userScrollActive)
        {
            _resyncDeadlineWallMs = 0L;
            _resyncProgress.Value = 1f;
            SetFollowMode(LyricsFollowMode.DetachedActive, Context.Scene);
            return;
        }

        if (_followMode.Peek() != LyricsFollowMode.DetachedActive) return;
        _resyncDeadlineWallMs = wallMs + ResyncIdleMs;
        _resyncProgress.Value = 1f;
        SetFollowMode(LyricsFollowMode.DetachedIdle, Context.Scene);
    }

    void TickFollowState(SceneStore scene, long wallMs)
    {
        var mode = _followMode.Peek();
        if (mode == LyricsFollowMode.DetachedIdle)
        {
            float left = Math.Clamp((_resyncDeadlineWallMs - wallMs) / (float)ResyncIdleMs, 0f, 1f);
            float shown = MathF.Ceiling(left * ResyncProgressSteps) / ResyncProgressSteps;
            if (MathF.Abs(_resyncProgress.Peek() - shown) > 0.0001f) _resyncProgress.Value = shown;
            if (left <= 0f)
            {
                BeginResync(scene);
                return;
            }
        }

        if (mode == LyricsFollowMode.Resyncing) DriveResync(scene);
    }

    void BeginResync(SceneStore? scene)
    {
        _resyncDeadlineWallMs = 0L;
        _resyncProgress.Value = 1f;
        SetFollowMode(LyricsFollowMode.Resyncing, scene);
        if (scene is not null) DriveResync(scene);
    }

    void DriveResync(SceneStore scene)
    {
        int active = _activeLine.Peek();
        if (active < 0)
        {
            CompleteResync(scene);
            return;
        }

        if (ScrollActiveIntoView(scene, active, FollowScrollIntent.Resync) == FollowArmResult.AtTarget)
            CompleteResync(scene);
    }

    void CompleteResync(SceneStore scene)
    {
        _resyncDeadlineWallMs = 0L;
        _resyncProgress.Value = 1f;
        SetFollowMode(LyricsFollowMode.Following, scene);   // DoF returns only after the programmatic spring has landed
    }

    void SeekToLine(int index)
    {
        var b = _b; var doc = _doc;
        if (b is null || doc is null || (uint)index >= (uint)doc.Lines.Count) return;
        ResetFollowState(Context.Scene);   // a deliberate lyric click returns to live before the new active index resolves
        long ms = doc.Lines[index].StartMs;
        b.NoteSeek(ms);     // arm the seek latch: suppress stale pre-seek position ticks (#2)
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
        if (!IsTimed(doc)) return;

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
        // A line stops being the VOICE at its own SUNG-OUT point, not when the next line starts: ResolveLine keeps
        // returning the previous line through the whole inter-line gap, which left it fully lit + glowing NEXT TO the
        // already-risen (lead) new active line — the "previous line is still fully active on the next line" double
        // emphasis. Between sung-out and the next start nothing is being sung ⇒ no voice.
        if (voiceLine >= 0)
        {
            var vl = doc.Lines[voiceLine];
            long vEnd = vl.IsWordByWord && vl.Syllables.Count > 0 ? vl.Syllables[^1].EndMs
                : vl.EndMs ?? (voiceLine + 1 < doc.Lines.Count ? doc.Lines[voiceLine + 1].StartMs : long.MaxValue);
            if (nowMs >= vEnd) voiceLine = -1;
        }
        bool activeChanged = active != _activeLine.Peek();
        if (activeChanged) _activeLine.Value = active;
        bool emphasisChanged = activeChanged;
        int prevVoiceLine = _voiceLine.Peek();
        bool voiceChanged = voiceLine != prevVoiceLine;
        if (voiceChanged) _voiceLine.Value = voiceLine;
        _nowMs.Value = nowMs;
        if (activeChanged && _pendingUpgrade is { } upgrade)
        {
            ApplyLyricsUpgrade(upgrade, nowMs);
            return;
        }

        // Main-content scroll tightens the shared GPU budget — defer the lyrics GLOW only (its per-frame σ/split writes
        // invalidate the scroll blur-pin and force re-Gaussians). The READABLE karaoke wipe is a cheap single-line
        // gradient write and keeps tracking the voice: freezing it visibly desynced the fill during any page scroll,
        // then lurched it forward on scroll end. (Core lane below is never gated.)
        bool deferHeavy = Context.PeekMainScrollBusy?.Invoke() == true;
        bool runGlow = !deferHeavy || activeChanged || voiceChanged || forceVisual;

        var scene = Context.Scene;
        if (scene is null) return;
        TickFollowState(scene, wallMs);

        // ── Core lane (always): interlude + programmatic scroll follow ──
        if (active >= 0 && (uint)active < (uint)doc.Lines.Count)
        {
            var al = doc.Lines[active];
            bool wordTimed = al.IsWordByWord && al.Syllables.Count > 0;
            long sungOutPoint = wordTimed ? al.Syllables[^1].EndMs : 0L;
            long nextStartMs = active + 1 < doc.Lines.Count ? doc.Lines[active + 1].StartMs : long.MaxValue;
            bool interlude = wordTimed && nowMs >= sungOutPoint && (nextStartMs - sungOutPoint) >= 4000;
            if (interlude != _interlude.Peek()) { _interlude.Value = interlude; emphasisChanged = true; }

            if (!_scrollSnapped || activeChanged || forceVisual)
                ScrollActiveIntoView(scene, active, FollowScrollIntent.Normal);
        }
        if (emphasisChanged) PushEmphasis();
        LastFrameDiagnostics = new(nowMs, auth, active, voiceLine, activeChanged, voiceChanged, _scrollSnapped, playing, doc.Lines.Count);

        // ── Visual lane: glow cross-fade (budget-deferred during a main scroll) + karaoke wipe (never deferred) ──
        if (runGlow)
        {
            // Voice handoff: CROSS-FADE the halos (incoming line ramps in, outgoing ramps out) instead of a hard clear —
            // the old instant σ/content toggle popped the glow on and off in one frame at every line change.
            if (voiceChanged) BeginGlowFades(scene, prevVoiceLine, voiceLine, wallMs);
            DriveGlowFades(scene, wallMs);
            // Voice-line glow envelope (FIX B): monotone min(inFade, outFade) on the media clock — no hard gate against the
            // in-cross-fade, so short chorus lines compress to a smooth triangle instead of snapping. Authoritative for the
            // live voice row; the cross-fade above only handles the OUTGOING line (and seek handoffs routed through ease()).
            if (voiceLine >= 0 && (uint)voiceLine < (uint)doc.Lines.Count)
                ApplyVoiceGlowEnvelope(scene, doc, voiceLine, nowMs);
        }

        // The karaoke wipe/glow live on the VOICE line (true time), trailing the emphasis line during the lead window. Drive
        // the READABLE main text wipe (the visible reveal) as the primary; the glow wipe (sung-only bloom) rides the same
        // split. Gate on the MAIN node's wipe — it is present on every word-by-word line (line-synced main has no wipe, so
        // this whole block correctly no-ops for line-synced, whose glow is a static child-blur, not an OnFrame-driven node).
        if ((uint)voiceLine >= (uint)_lineNodes.Length) return;
        var mainNode = _lineNodes[voiceLine];
        var glowNode = (uint)voiceLine < (uint)_glowNodes.Length ? _glowNodes[voiceLine] : NodeHandle.Null;
        if (mainNode.IsNull || !scene.IsLive(mainNode)) return;

        if (scene.TryGetGlyphWipe(mainNode, out var mw))
        {
            float split = LyricLineView.ComputeSplit(doc.Lines[voiceLine], nowMs);
            if (split > 0f && split < 1f) split = Math.Clamp(split + LyricLineView.WipeLeadFrac, 0f, 1f);
            // Pixel-quantize the boundary so sub-pixel ticks produce byte-identical gradient bytes ⇒ the host skip-submit hash
            // elides them (and the blur pin-cache HITS). main and glow share this run width (same text/size/wrap).
            float runW = scene.AbsoluteRect(mainNode).W;
            if (runW > 1f) split = MathF.Round(split * runW) / runW;
            bool forceWipe = forceVisual || voiceLine != _lastWipeLine || _lastWipeWallMs == 0L;
            if (!forceWipe && wallMs - _lastWipeWallMs < KaraokeWipeIntervalMs) return;

            _lastWipeWallMs = wallMs;
            _lastWipeLine = voiceLine;

            // Readable main wipe — the karaoke reveal the user sees (S2).
            if (MathF.Abs(split - mw.Split) > 0.0008f)
            {
                scene.SetGlyphWipe(mainNode, mw with { Split = split });
                scene.Mark(mainNode, NodeFlags.PaintDirty);
            }

            // Glow bloom — same split; σ co-decays with the bound glow alpha (FIX B melt) so the halo tightens as it
            // dims. Deferred with the glow lane during a main scroll: a per-frame split/σ write here would invalidate
            // the scroll blur-pin every frame — the halo holds its last frame instead (barely visible under the fill).
            if (runGlow && !glowNode.IsNull && scene.IsLive(glowNode) && scene.TryGetGlyphWipe(glowNode, out var w))
            {
                bool glowDirty = MathF.Abs(split - w.Split) > 0.0008f;
                if (glowDirty) scene.SetGlyphWipe(glowNode, w with { Split = split });
                float baseSigma = _large ? 6f : 4f;
                float glowA = GlowAlphaOf(voiceLine);
                float sigma = baseSigma * glowA;
                ref var gp = ref scene.Paint(glowNode);
                if (MathF.Abs(gp.BlurSigma - sigma) > 0.01f) { gp.BlurSigma = sigma; glowDirty = true; }
                if (glowDirty) scene.Mark(glowNode, NodeFlags.PaintDirty);
            }
        }
    }

    // Arm the halo cross-fade at a voice handoff. Each fade remembers its FROM alpha so a handoff landing mid-fade (rapid
    // line runs, or scrubbing back onto a fading line) continues from the current value instead of jumping to 0/1. If a
    // THIRD line's out-fade is still in flight, finish it instantly — at most two halos ever animate.
    void BeginGlowFades(SceneStore scene, int prev, int next, long wallMs)
    {
        if (_glowOutLine >= 0 && _glowOutLine != prev && _glowOutLine != next) FinishGlowOut(scene, _glowOutLine);
        _glowOutLine = prev;
        _glowOutStart = wallMs;
        // From the LIVE alpha (not an assumed 1): the end-of-line pre-fade usually already took it near 0, so a normal
        // handoff finishes the out-fade almost instantly instead of re-airing a 240 ms halo tail over the new line.
        _glowOutFrom = GlowAlphaOf(prev);
        _glowInLine = next;
        _glowInStart = wallMs;
        _glowInFrom = GlowAlphaOf(next);
    }

    // Step halo cross-fades (called every OnFrame tick). Sine-Out eased (FIX B). The live voice line's envelope is
    // ApplyVoiceGlowEnvelope — this handles OUTGOING lines only; the in-ramp is the envelope's min(in,out) form.
    void DriveGlowFades(SceneStore scene, long wallMs)
    {
        if (_glowOutLine >= 0)
        {
            float t = EaseOutSine(Math.Clamp((wallMs - _glowOutStart) / GlowFadeMs, 0f, 1f));
            float a = _glowOutFrom * (1f - t);
            if (a <= 0f) { FinishGlowOut(scene, _glowOutLine); _glowOutLine = -1; }
            else SetGlowAlpha(_glowOutLine, a);
        }
    }

    // Monotone voice-line glow envelope: min(eased in-ramp, eased out-ramp) on the media clock + optional bloom taper.
    void ApplyVoiceGlowEnvelope(SceneStore scene, LyricsDocument doc, int voiceLine, long nowMs)
    {
        var line = doc.Lines[voiceLine];
        long lineStart = line.StartMs;
        long lineEnd = line.IsWordByWord && line.Syllables.Count > 0 ? line.Syllables[^1].EndMs
            : line.EndMs ?? (voiceLine + 1 < doc.Lines.Count ? doc.Lines[voiceLine + 1].StartMs : long.MaxValue);
        float alphaOut = EaseOutSine(Math.Clamp((lineEnd - nowMs) / GlowOutMs, 0f, 1f));
        float alpha;
        if (line.IsWordByWord && line.Syllables.Count > 0)
        {
            // WaveeMusic/BetterLyrics rule (LyricsAnimator "辉光（长音节）", scope = LongDurationSyllable): the halo
            // blooms ONLY while a ≥ HeldGlowMinMs syllable is being HELD — swell in across the hold, melt out into its
            // end. Short syllables get no glow at all; the old whole-line wash is gone.
            alpha = MathF.Min(HeldSyllableGlow(line, nowMs), alphaOut);
        }
        else
        {
            // Line-synced (no syllable timing): keep the gentle whole-line envelope — there is no "held note" signal.
            float inFade = EaseOutSine(Math.Clamp((nowMs - lineStart) / GlowFadeMs, 0f, 1f));
            alpha = MathF.Min(inFade, alphaOut);
        }
        alpha = MathF.Max(0f, alpha);
        SetGlowAlpha(voiceLine, alpha);
        // Envelope owns the live voice row — retire any redundant in-cross-fade arm.
        if (_glowInLine == voiceLine) _glowInLine = -1;
    }

    // The held-note bloom: 0 unless nowMs sits inside a syllable of at least HeldGlowMinMs; inside one, swell in over
    // min(HeldGlowRampMaxMs, half the note) and melt over GlowOutMs into the note's end — min(up, down) is the same
    // monotone triangle form the line envelope used, so a short-held note compresses smoothly instead of snapping.
    static float HeldSyllableGlow(LyricLine line, long nowMs)
    {
        var syls = line.Syllables;
        for (int i = 0; i < syls.Count; i++)
        {
            var s = syls[i];
            if (nowMs < s.StartMs) break;        // syllables are time-ordered — nothing later can contain nowMs
            if (nowMs >= s.EndMs) continue;
            long dur = s.EndMs - s.StartMs;
            if (dur < HeldGlowMinMs) return 0f;  // short syllable: never glows
            float rampIn = MathF.Min(HeldGlowRampMaxMs, dur * 0.5f);
            float up = EaseOutSine(Math.Clamp((nowMs - s.StartMs) / rampIn, 0f, 1f));
            float down = EaseOutSine(Math.Clamp((s.EndMs - nowMs) / GlowOutMs, 0f, 1f));
            return MathF.Min(up, down);
        }
        return 0f;
    }

    static float EaseOutSine(float t) => MathF.Sin(t * MathF.PI * 0.5f);

    static float ComputeSplit(LyricLine line, long nowMs) => LyricLineView.ComputeSplit(line, nowMs);

    float GlowAlphaOf(int line) => (uint)line < (uint)_glowAlpha.Length ? _glowAlpha[line].Peek() : 0f;

    void SetGlowAlpha(int line, float a)
    {
        if ((uint)line < (uint)_glowAlpha.Length) _glowAlpha[line].Value = a;
    }

    void FinishGlowOut(SceneStore scene, int line)
    {
        SetGlowAlpha(line, 0f);
        // Word-by-word glow σ is paint-driven (element Blur = 0) — return it to rest once invisible so the halo layer
        // costs nothing. Line-synced glow σ is a constant element Blur: leave it (alpha 0 hides it; settled bytes pin-hit).
        if (_doc is { } d && (uint)line < (uint)d.Lines.Count && d.Lines[line].IsWordByWord)
            ClearGlowNode(scene, line);
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

    FollowArmResult ScrollActiveIntoView(SceneStore scene, int active, FollowScrollIntent intent)
    {
        var viewport = _viewportNode;
        var layout = _layout;
        if (layout is null || viewport.IsNull || !scene.IsLive(viewport) || !scene.HasScroll(viewport))
            return FollowArmResult.Unavailable;

        ref ScrollState sc = ref scene.ScrollRef(viewport);
        if (sc.ViewportH <= 0.5f || sc.ContentH <= 0.5f) return FollowArmResult.Unavailable;

        if (intent == FollowScrollIntent.Normal)
        {
            if (_followMode.Peek() != LyricsFollowMode.Following) return FollowArmResult.Unavailable;
            if (sc.UserScrollActive)
            {
                OnLyricsScrollActivity(true, Environment.TickCount64);
                return FollowArmResult.Unavailable;
            }
        }

        // ArrangeVirtualMeasured now owns the engine's SetViewport-before-geometry contract. Refresh it here too because
        // this target calculation runs outside layout and should use the newest published viewport immediately.
        layout.SetViewport(sc.ViewportH, sc.ViewportW);

        RectF item = layout.ItemRect(active, sc.ViewportW);
        float target = item.Y + item.H * 0.5f - sc.ViewportH * _band;
        target = Math.Clamp(target, 0f, MathF.Max(0f, sc.ContentH - sc.ViewportH));

        if (!_scrollSnapped && intent == FollowScrollIntent.Normal)
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
            return FollowArmResult.AtTarget;
        }
        if (intent == FollowScrollIntent.Resync) _scrollSnapped = true;   // Resync is always a spring, never the open latch

        // Velocity-continuous re-target: only zero the carried spring velocity on the FIRST entry into a Programmatic
        // WheelAnimating chase. A re-target while ALREADY easing (dense lyric sections, lines ~200-300 ms apart) KEEPS the
        // velocity so the engine spring chains smoothly to the new target instead of restarting a decelerating chase (the
        // "list trails the song" defect).
        bool alreadyProgrammatic = sc.Phase == ScrollIntegrator.WheelAnimating && (sc.PhaseFlags & ScrollState.PhaseProgrammatic) != 0;
        if (alreadyProgrammatic && !float.IsNaN(sc.PendingTargetY) && MathF.Abs(sc.PendingTargetY - target) <= 0.5f)
            return FollowArmResult.Armed;
        if (!alreadyProgrammatic && MathF.Abs(sc.OffsetY - target) <= 0.5f)
            return FollowArmResult.AtTarget;

        // AMLL posY: m=.9/d=15/k=90 ⇒ ζ≈.833, ω0=10. The per-viewport 4 DIP/s landing gate prevents the global
        // 16 DIP/s wheel threshold from truncating this soft spring around 450 ms; ordinary line steps land ~.5-.7 s.
        sc.ProgrammaticZeta = 0.833f;
        sc.ProgrammaticOmega = 10f;
        sc.ProgrammaticSettleVelocity = 4f;
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
        return FollowArmResult.Armed;
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

    static bool IsTimed(LyricsDocument doc) => doc.Sync is LyricsSyncKind.Line or LyricsSyncKind.Syllable;

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
    readonly Signal<int> _emphasis;   // packed per-line emphasis (bucket + interlude bit) — value-gated by LyricsView
    readonly FloatSignal _nowMs;
    readonly Signal<LyricsFollowMode> _followMode; // stable parent signal; Peek only so a mode flip never fans out row renders
    readonly FloatSignal? _glowFade;   // per-line halo alpha (owned + ramped by LyricsView); bound as the glow wrapper's Opacity
    readonly float _fontSz;
    readonly float _lineHt;
    readonly float _rowPad;
    readonly float _sidePad;
    readonly bool _centered;
    readonly Action<int, NodeHandle> _reportNode;
    readonly Action<int, NodeHandle> _reportGlow;
    readonly Action<int, NodeHandle> _reportDof;
    readonly Action _onSeek;

    public LyricLineView(int index, LyricLine line, Signal<int> emphasis, FloatSignal nowMs, Signal<LyricsFollowMode> followMode,
        FloatSignal? glowFade,
        float fontSz, float lineHt, float rowPad, float sidePad, bool centered, Action<int, NodeHandle> reportNode,
        Action<int, NodeHandle> reportGlow, Action<int, NodeHandle> reportDof, Action onSeek)
    {
        _index = index; _line = line; _emphasis = emphasis; _nowMs = nowMs;
        _followMode = followMode; _glowFade = glowFade;
        _fontSz = fontSz; _lineHt = lineHt; _rowPad = rowPad; _sidePad = sidePad; _centered = centered;
        _reportNode = reportNode; _reportGlow = reportGlow; _reportDof = reportDof; _onSeek = onSeek;
    }

    // The halo wrapper's opacity: BOUND to the per-line fade signal so a row re-render re-asserts the live fade value
    // (the reconciler skips bound Opacity) — a static value here would snap the halo at exactly the re-render moments
    // (active/voice flips) the fade exists to smooth.
    Prop<float> GlowOpacity() => _glowFade is { } s ? (Prop<float>)s : 0f;

    public override Element Render()
    {
        // Read ONLY this line's packed emphasis — a value-gated signal LyricsView rewrites as the active line moves, so
        // reading `.Value` here re-renders the row solely when ITS OWN bucket/interlude changes (not on every boundary).
        int e = _emphasis.Value;
        int dist = e & 7;                        // bucket 0..6 (clamped distance from the active line)
        bool interlude = (e & 8) != 0;           // active line sung out into a long instrumental gap — recede it
        bool isActive = dist == 0;               // bucket 0 ⇔ this is the active line

        float f = MathF.Min(dist / 5f, 1f);
        // Emphasis targets. Active line: full focus (scale 1 / opacity 1 / crisp). During an instrumental interlude the
        // still-active sung-out line recedes to a calm look instead of sitting frozen-fully-lit. Dimmed lines fall off by
        // distance in opacity, scale AND DoF blur. Voice only drives the karaoke wipe and glow during the lead split, so
        // depth never disagrees with emphasis.
        float scale = interlude ? 0.92f : isActive ? 1f : 1f - 0.25f * f;
        // Row emphasis follows ACTIVE only — voice keeps the karaoke wipe/glow but must not hold full brightness once
        // focus moves (the lead window used to leave the previous line white for its entire sung tail).
        float opacity = interlude ? 0.55f : isActive ? 1f : MathF.Max(0.16f, 0.55f * (1f - f));
        float dofBlur = interlude ? LyricsFx.DofSigma(1) : isActive ? 0f : LyricsFx.DofSigma(dist);
        float blur = _followMode.Peek() == LyricsFollowMode.Following ? dofBlur : 0f;

        // AMLL scale in BOTH directions; opacity is critical/no-bounce but calibrated to the same visible settle window.
        // Cold mounts still begin at the element rest targets below, so the soft inactive spring cannot flash a new row.
        var key = DepKey.From(dist, (interlude ? 1 : 0) | (isActive ? 2 : 0));
        var scaleSpring = new SpringParams(100f, 25f, 2f);             // AMLL m=2,d=25,k=100
        var opacitySpring = SpringParams.FromResponse(0.889f, 1.0f);   // AMLL scale's visible settle, critical/no bounce
        UseSpring(AnimChannel.Opacity, opacity, opacitySpring, key);
        UseSpring(AnimChannel.ScaleX, scale, scaleSpring, key);
        UseSpring(AnimChannel.ScaleY, scale, scaleSpring, key);

        var wrap = _centered ? TextWrap.NoWrap : TextWrap.Wrap;
        int maxLines = _centered ? 1 : 0;
        Element textEl;

        // The karaoke wipe sub-tree renders on the active line AND the voice line — during the ~140 ms lead the voice line
        // (still being sung) trails the emphasis line, but its fill must keep running. Emphasis (scale/opacity) follows
        // `active`; the wipe split follows true time via _nowMs.
        // Word-by-word line: ALWAYS a two-child ZStack [glow, main], in EVERY state (active / voice / dimmed). The two
        // nodes mount ONCE and only their PROPERTIES toggle, so a line LEAVING the voice slot (the row just above active
        // during the ~140 ms lead) is an in-place update — NOT the BoxEl↔TextEl child-type swap that forced a Remove+Mount,
        // re-shaped the glyph run + missed the blur cache = the one-frame flicker on the lines above active. The main
        // text's glyphs never re-shape on that transition (its string is unchanged), and because both nodes persist,
        // OnRealized (which fires only on mount) keeps the wipe/glow node reports (_reportNode/_reportGlow, read by
        // OnFrame) valid across every transition WITHOUT a remount. (Line-synced lines use the same persistent ZStack below.)
        if (_line.IsWordByWord && _line.Syllables.Count > 0)
        {
            // Karaoke split for THIS line on the true clock: 0 = upcoming (unsung), advancing = being sung, 1 = passed.
            // Apply the SAME small lead the OnFrame driver uses so the reconcile re-render seeds a value consistent with the
            // per-frame writer (kills the ~4% boundary snap-back on the handoff frame — S3-4).
            float split = ComputeSplit(_line, (long)_nowMs.Peek());
            if (split > 0f && split < 1f) split = Math.Clamp(split + WipeLeadFrac, 0f, 1f);
            // MAIN = the readable foreground, and it CARRIES THE WIPE — this is the progressive reveal the user SEES:
            // sung glyphs full-bright Primary (Before), unsung glyphs dim-but-readable (After = Primary @ 0.45). The row
            // group opacity spring (active-only emphasis) dims the whole row once focus moves; this wipe does sung/unsung.
            Element main = LineText(_line.Text, Tok.TextPrimary) with
            {
                Wipe = new GlyphWipe(Before: Tok.TextPrimary, After: Tok.TextPrimary with { A = 0.45f }, Split: split, Softness: 0.14f),
                OnRealized = h => _reportNode(_index, h),
            };
            // GLOW = a soft blurred bloom UNDER the main, on the SUNG glyphs only (After.A = 0 ⇒ unsung glyphs fully
            // transparent). Its glyphs mount once the row is NEAR the focus (dist ≤ 2 — still dim + blurred, so the
            // content swap itself can never pop on the focal row); a peripheral line pays no second glyph run. OnFrame
            // drives its split (same value as main) + its constant σ; its VISIBILITY is the cross-fade wrapper below.
            bool near = dist <= 2;
            Element glowText = (near
                ? LineText(_line.Text, Tok.TextPrimary) with
                  {
                      Wipe = new GlyphWipe(Before: Tok.TextPrimary, After: Tok.TextPrimary with { A = 0f }, Split: split, Softness: 0.14f),
                  }
                : LineText("", Tok.TextPrimary)) with { OnRealized = h => _reportGlow(_index, h) };
            // The wrapper's bound opacity is the per-line glow-fade signal: OnFrame ramps it in over ~240 ms as this line
            // becomes the voice and out as it leaves — the halo never appears/vanishes in one frame (the old handoff pop).
            Element glow = new BoxEl { Opacity = GlowOpacity(), HitTestVisible = false, Children = [glowText] };
            textEl = new BoxEl { ZStack = true, Children = [glow, main] };
        }
        else
        {
            // Line-level lyrics (no syllables ⇒ no karaoke wipe / no held-note bloom). PERSISTENT 2-child ZStack in EVERY
            // state — same shape whether karaoke-live or dimmed — so a line handoff is an in-place property toggle, NOT the
            // BoxEl↔TextEl child-TYPE swap that forced a Remove+Mount (glyph reshape + blur-cache miss + OnRealized re-fire)
            // on the text subtree every activation (L1 / the line-synced handoff flicker). Text color tracks active emphasis;
            // voice-only rows keep the glow cross-fade but recede to Secondary so the eye stays on the rising active line.
            bool lit = isActive;
            bool near = dist <= 2;
            Element glow = new BoxEl
            {
                // Constant σ halo while NEAR the focus (dist ≤ 2); its VISIBILITY is the bound glow-fade signal, ramped by
                // OnFrame at the voice handoff — the halo cross-fades instead of the old one-frame σ 0↔9 + text swap pop.
                // Glyphs + the blur layer mount/step while the row is still dim + blurred (never on the focal row), and a
                // peripheral line pays neither a second glyph run nor a blur layer.
                Blur = near ? (_centered ? 13f : 9f) : 0f,
                Opacity = GlowOpacity(),
                HitTestVisible = false,
                Children = [LineText(near ? _line.Text : "", Tok.TextPrimary with { A = 0.4f })],
            };
            Element main = LineText(_line.Text, lit ? Tok.TextPrimary : Tok.TextSecondary) with
            {
                // ~150 ms brush cross-fade so the Primary↔Secondary color flip at the handoff never snaps in one frame.
                BrushTransitionMs = 150f,
                OnRealized = h => _reportNode(_index, h),
            };
            textEl = new BoxEl { ZStack = true, Children = [glow, main] };
        }

        // Own DoF on a persistent INNER content wrapper, separate from the outer scale/opacity track owner. Ancestor
        // scale still composes normally (the text should scale); this separation removes the row-padding blur layer and
        // lets LyricsView suppress/restore static σ by direct node write without touching the line component.
        Element dofContent = new BoxEl
        {
            Direction = 1,
            Blur = blur,
            BlurCachePolicy = BlurCachePolicy.Normal,
            OnRealized = h => _reportDof(_index, h),
            Children = [textEl],
        };

        return new BoxEl
        {
            Direction = 1,
            // Element-side values are the scale/opacity springs' REST targets. The slab owns in-flight values; at settle
            // both agree, so a later emphasis render cannot snap the row or flash a newly realised overscan item.
            ScaleX = scale,
            ScaleY = scale,
            Opacity = opacity,   // element rest target — reconciler re-asserts the dim value (not 1), not bound (no mount flash)
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
            Children = [dofContent],
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

    // Small POSITIVE wipe lead: nudge the bright boundary a few % ahead of the strictly-played fraction so the edge reads
    // as anticipating the voice. Shared by the element seed (LyricLineView.Render) and the per-frame driver (OnFrame) so
    // the reconcile re-render and the OnFrame writer agree on the boundary (no snap-back — S3-4).
    internal const float WipeLeadFrac = 0.04f;

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

sealed class LyricsUpgradeObserver(Action<LyricsDocument> onNext) : IObserver<LyricsDocument>
{
    public void OnCompleted() { }
    public void OnError(Exception error) { }
    public void OnNext(LyricsDocument value) => onNext(value);
}

sealed class LyricsTicker : Component
{
    public required LyricsView Owner;

    public override Element Render()
    {
        var bridge = UseContextSignal(PlaybackBridge.Slot);

        // Once per mount (re-keyed on a track change): reset scroll-snap + mark this the probe-active instance.
        UseEffect(() =>
        {
            Owner.ResetScrollSnap();
            LyricsView.ProbeActive = Owner;   // probe hook (harmless otherwise): the live instance the advance-probe drives
        }, DepKey.Empty);

        var b = bridge.Value;                                  // subscribe → re-render when the bridge arrives
        bool playing = b is not null && b.IsPlaying.Value;     // subscribe IsPlaying → re-gate the interval on play/pause
        var followMode = Owner.FollowModeValue;                 // isolated subscription: never re-renders LyricsView/rows

        // Play-start edge → one immediate advance (matches the old dueTime:0 ticker); paused → subscribe PositionMs so a
        // scrub while paused re-wipes to the new spot. Re-runs on any bridge/IsPlaying change.
        UseSignalEffect(() =>
        {
            var bb = bridge.Value;
            if (bb is null) return;
            if (bb.IsPlaying.Value)
            {
                if (!LyricsView.ProbeSyncMode) Owner.OnFrame();
            }
            else
            {
                _ = bb.PositionMs.Value;
                Owner.OnFrame(forceVisual: true);
            }
        });

        // Playing: advance the karaoke wipe every WipeIntervalMs on the frame clock — AUTO-PAUSES while parked/minimized
        // (idle quiesce), while the wall-clock throttle inside OnFrame still governs the real wipe cadence. ProbeSyncMode
        // drives OnFrame synchronously (ProbeStep), so the interval stays disabled under the probe. Replaces the old
        // System.Threading.Timer + generation guard + UsePost marshal.
        // Detached/resync work must continue while playback is paused (countdown + programmatic settle); Following at
        // pause remains completely quiescent and still wakes only for PositionMs changes through the effect above.
        bool needsTicks = playing || followMode != LyricsFollowMode.Following;
        UseInterval(() => Owner.OnFrame(), Owner.WipeIntervalMs, enabled: needsTicks && !LyricsView.ProbeSyncMode);
        return new BoxEl { HitTestVisible = false, Width = 0f, Height = 0f };
    }
}
