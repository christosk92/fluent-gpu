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
using FluentGpu.Text;
using Wavee.Backend.Lyrics;
using Wavee.Core;

namespace Wavee;

// The lyrics depth-of-field: ONE look for everyone, no tiers, no flags. Each dimmed line, by its distance from the active
// line, gets a soft self-blur. The ladder is a LOOKUP measured off the Apple Music reference capture (lyrics-parity
// campaign, 2026-08-03 — edge energy 27 / 3.7 / ~0 across the first three rings: ring 1 is only mildly soft, ring 3 is
// already dissolved), NOT the old linear `5 * min(dist/5, 1)` ramp, which held ring 1 far too crisp and stopped short at
// the far end. 0 ⇒ no blur layer is emitted at all (SceneRecorder drops sigma ≤ 0.01).
static class LyricsFx
{
    public static float DofSigma(int dist) => dist switch
    {
        <= 0 => 0f,
        1 => 1.25f,
        2 => 2.5f,
        3 => 4f,
        4 => 5.5f,
        _ => 6.5f,
    };
}

enum LyricsFollowMode : byte { Following, DetachedActive, DetachedIdle, Resyncing }
enum FollowArmResult : byte { Unavailable, AtTarget, Armed }
enum FollowScrollIntent : byte { Normal, Resync }

sealed class LyricsView : Component
{
    readonly record struct TrackIdentity(string Id, string Artist);

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

    // Per-line PACKED emphasis (bucket 0..6 in bits 0-2, interlude flag in bit 3, PAST flag in bit 4). One VALUE-GATED Signal per line: the
    // reactive core propagates staleness eagerly (a Memo does NOT gate downstream re-renders by value), so the ONLY way a
    // line re-renders solely on ITS OWN emphasis change is a per-line signal whose setter no-ops when the packed value is
    // unchanged. As `_activeLine` sweeps, PushEmphasis rewrites all lines but only the ~dozen crossing a bucket boundary
    // actually notify — the rest (already at bucket 6) are no-op writes. Sized in PrepareDocument alongside `_glowAlpha`.
    Signal<int>[] _lineEmphasis = Array.Empty<Signal<int>>();
    readonly Signal<int> _emphasisFallback = new(6);   // bucket 6 (fully dim) — only used during a transient array-resize gap

    // Per-line READING-ORDER run length in DIP: the sum of the widths of the wrapped visual-line fragments, laid
    // end-to-end over inked glyph edges — which is EXACTLY the extent GlyphWipe.Split/Softness are fractions of. The
    // node width (AbsoluteRect.W) is NOT that extent: it is the wrap box, so a half-full last line would make the same
    // DIP feather resolve to two different fractions. Filled LAZILY (once per line per width epoch, never per frame)
    // by RunLengthOf through the text seam; NaN = unmeasured. Sized in PrepareDocument alongside `_glowAlpha`.
    float[] _lineRunLen = Array.Empty<float>();
    float _runLenWrapW = float.NaN;   // the wrap width every cached entry was measured at; a ≥0.5 DIP move invalidates ALL

    bool _docWordByWord;   // any line word-timed with syllables ⇒ the karaoke wipe needs 60 Hz; line-synced docs pace at 30 Hz
    // The lyrics ticker cadence: word-by-word karaoke needs the 16 ms (~60 Hz) sweep; line-synced docs (no per-frame wipe)
    // pace at 33 ms (~30 Hz) — the 240 ms glow fade still gets 7+ steps and the wipe block no-ops for them.
    internal long WipeIntervalMs => _docWordByWord ? 16 : 33;

    NodeHandle _viewportNode = NodeHandle.Null;
    NodeHandle[] _lineNodes = Array.Empty<NodeHandle>();
    NodeHandle[] _glowNodes = Array.Empty<NodeHandle>();
    NodeHandle[] _dofNodes = Array.Empty<NodeHandle>();

    // ── Directional depth-of-field σ model ───────────────────────────────────────────────────────────────────────────
    // `_dofCurrent[i]` is the σ line i is being DRIVEN to — the single source of truth for that node's BlurSigma. The
    // TARGET is whatever DofForLine (+ suppression) resolves to, and the two are NOT reached symmetrically: the reference
    // front-loads the outgoing line's softening (it is essentially fully blurred inside the first ~100 ms of its exit)
    // while the incoming line sharpens gradually THROUGH the flight. So an INCREASE snaps and a DECREASE eases over
    // ~200 ms (DriveDofRamp). NaN = never driven ⇒ adopt the target on the first visit (that is what the element mounted
    // with). Sized in PrepareDocument alongside `_glowAlpha`; the element reads it back through DofDeclaredFor so a
    // re-render mid-ramp re-asserts the live σ instead of snapping the node to the rest target.
    float[] _dofCurrent = Array.Empty<float>();
    bool _dofRampPending = true;   // a target may have moved (emphasis / suppression / realization) — run a ramp pass
    long _dofRampWallMs;           // wall stamp of the last ramp pass (dt source; 0 = none yet)

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

        bool open = _visible is not null ? _visible() : (ui?.RailOpen.Value ?? false);
        UseEffect(() =>
        {
            if (!open) ResetFollowState(Context.Scene);
        }, DepKey.From(open));
        // Subscribe only to the bridge's atomic match identity. Metadata-only CurrentTrack refreshes no longer rebuild
        // this chrome host, and track/context cannot be observed in a transient mismatched pair.
        var live = b?.Identity.Value.Track;
        var identity = new TrackIdentity(
            live?.Id ?? "",
            live is { Artists.Count: > 0 } ? live.Artists[0].Name : "");
        // Peek, NEVER .Value: subscribing the lyrics view to the IPC position snapshot forces a full re-render every
        // tick. The position is consumed by the per-frame ticker (OnFrame, via Peek) and re-anchored there.
        long posNow = b?.PositionMs.Peek() ?? 0L;
        string trackId = identity.Id;
        UseSignalEffect(() =>
        {
            string currentTrackId = _b?.Identity.Value.Track?.Id ?? "";
            if (currentTrackId.Length == 0 || _svc?.Lyrics is not IUpgradingLyricsProvider up) return;

            var sub = up.LyricsUpgraded.Subscribe(new LyricsUpgradeObserver(upgrade =>
            {
                if (!StringComparer.Ordinal.Equals(upgrade.TrackId, currentTrackId)) return;
                post(() =>
                {
                    string liveTrackId = _b?.Identity.Peek().Track?.Id ?? "";
                    if (StringComparer.Ordinal.Equals(liveTrackId, upgrade.TrackId))
                        ReceiveUpgrade(upgrade);
                });
            }));
            Reactive.OnCleanup(() => sub.Dispose());
        });

        if (b is null || svc is null) return new BoxEl { Grow = 1f };
        if (trackId.Length == 0)
        {
            ClearDocument();
            return Message("Nothing playing");
        }

        if (_doc is not null && !StringComparer.Ordinal.Equals(_doc.TrackId, trackId))
            ClearDocument();

        // The document + Virtual.Custom live under a track-id keyed component boundary. Rail/debug/chrome rerenders now
        // reconcile this one ComponentEl in place instead of rebuilding the virtual element and remounting every line.
        Element body = Embed.Comp(() => new LyricsDocHost(this, trackId, identity.Artist, posNow))
            with { Key = "lyrics-doc:" + trackId };

        // The ticker self-no-ops until a timed document exists. Key it by track so its once-per-mount scroll-snap reset
        // still follows track identity now that parent chrome rerenders no longer remount the document subtree.
        Element? ticker = open
            ? Embed.Comp(() => new LyricsTicker { Owner = this }) with { Key = "lyrics-ticker:" + trackId }
            : null;
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

    sealed class LyricsDocHost(LyricsView owner, string trackId, string artist, long initialPositionMs) : Component
    {
        public override Element Render()
        {
            var svc = UseContext(Services.Slot);
            string fetchKey = trackId + "|" + artist;
            var docL = UseResource(
                ct => svc?.Lyrics is { } provider
                    ? provider.GetLyricsAsync(trackId, ct)
                    : Task.FromResult<LyricsDocument?>(null),
                (LyricsDocument?)null, fetchKey).Loadable;
            owner._docLoadable = docL;

            var doc = docL.Value.Value;
            if (doc is { Lines.Count: > 0 } ready)
                owner.PrepareDocument(ready, owner._b?.PositionMs.Peek() ?? initialPositionMs);

            return Skel.Region<LyricsDocument?>(
                docL,
                shimmerSource: () => LyricsShimmer(owner._large),
                content: d => d is { Lines.Count: > 0 } readyDoc ? owner.LyricsContent(readyDoc) : Message("No lyrics available"),
                reveal: SkelReveal.FadeOnly,
                onFailed: () => Message("No lyrics available"),
                isEmpty: d => d?.Lines is null || d.Lines.Count == 0,
                onEmpty: () => Message("No lyrics available"),
                style: new SkeletonStyle(Tok.FillSubtleSecondary, RowGap: owner._large ? 18f : 14f, BarRadius: 6f, TextRatio: 0.86f),
                smoothResize: false);
        }
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
        // Run lengths start unmeasured (NaN) and the width epoch starts unknown: the first RunLengthOf per line pays
        // one seam layout, every later call is an array read. A new doc for the SAME width still re-measures — the
        // strings changed, so the cached fragment sums are meaningless.
        _lineRunLen = new float[doc.Lines.Count];
        Array.Fill(_lineRunLen, float.NaN);
        _runLenWrapW = float.NaN;
        // σ starts undriven (NaN): the first ramp pass adopts each line's target, which is exactly the value the element
        // declared at mount, so a fresh doc lands on its ladder without a visible settle.
        _dofCurrent = new float[doc.Lines.Count];
        Array.Fill(_dofCurrent, float.NaN);
        _dofRampPending = true;
        _dofRampWallMs = 0L;
        // Seed each line at its REAL bucket for the doc's opening active line — NOT a constant. PrepareDocument runs
        // inside Render (LyricsDocHost), so the PushEmphasis below is a signal write during a render pass: seeding
        // "fully dim" made that write move every line whose true bucket wasn't 6, fanning the whole document out at
        // once on each new doc. Seeded with the computed value, PushEmphasis has nothing left to change and the write
        // pass is silent. The steady sweep is untouched — it still goes through PushEmphasis, the one chokepoint.
        bool timed = IsTimed(doc);
        int seedActive = timed ? ResolveLine(doc.Lines, posMs) : -1;
        bool seedInterlude = timed && _interlude.Peek();   // the timed branch below leaves _interlude as-is; the other clears it
        _lineEmphasis = new Signal<int>[doc.Lines.Count];
        for (int i = 0; i < _lineEmphasis.Length; i++) _lineEmphasis[i] = new Signal<int>(PackEmphasis(i, seedActive, seedInterlude));
        _docWordByWord = IsWordByWordDoc(doc);
        _glowInLine = -1; _glowOutLine = -1;
        if (timed)
        {
            _activeLine.Value = seedActive;
        }
        else
        {
            _activeLine.Value = -1;
            _voiceLine.Value = -1;
            _interlude.Value = false;
        }
        PushEmphasis();   // per-line emphasis for the freshly loaded doc (before OnFrame drives it) — a no-op after the seed above
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

    // Packed per-line emphasis: bucket (distance from active, clamped 0..6) in bits 0-2, interlude flag in bit 3, PAST
    // flag in bit 4. Clamp 6 is exact for the look — the DoF ladder saturates at ring 5 and the glyph-mount `near`
    // threshold is dist ≤ 2, so any line ≥6 away is visually identical; clamping lets far lines share bucket 6 and skip
    // the re-render as active sweeps.
    //
    // Bit 4 (past) is the reference's past/future ASYMMETRY: a line the song has already passed settles dimmer than an
    // upcoming line the same distance away (measured 118 vs 133 luma), so the two directions ride two different opacity
    // ladders (LyricLineView.OpacityOf). It is deliberately NOT set at the saturated bucket 6: both ladders bottom out at
    // the same 0.10/σ 6.5 there, so tagging far lines would be visually identical while breaking the far-line no-op —
    // every line above the active one would re-render on a seek instead of sitting silent at bucket 6.
    static int PackEmphasis(int index, int active, bool interlude)
    {
        int bucket = active < 0 ? 6 : Math.Min(Math.Abs(index - active), 6);
        int e = bucket;
        if (interlude && index == active) e |= 8;              // interlude recede applies only to the active line itself
        if (active >= 0 && index < active && bucket < 6) e |= 16;   // already sung ⇒ the dimmer of the two ladders
        return e;
    }

    // Rewrite every line's emphasis signal from the current active/interlude. Value-gated: only lines whose PACKED value
    // actually changes notify their subscriber, so a boundary re-renders ~a dozen rows instead of the whole realized
    // document. The past bit rides along for free: the one line that crosses future→past at a handoff is already a
    // bucket-crosser (dist 0 → 1), so it re-renders exactly once — and takes the past ladder as it does.
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
        _lineRunLen = Array.Empty<float>();
        _runLenWrapW = float.NaN;
        _dofCurrent = Array.Empty<float>();
        _dofRampPending = true;
        _dofRampWallMs = 0L;
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

    // The TIMED-row type metrics, as properties rather than LyricsContent locals: RunLengthOf has to rebuild the exact
    // TextStyle a row's TextEl renders with (and SoftnessOfLine has to know the side padding to derive the wrap width),
    // and it runs outside the content pass — a second copy of the ladder here is precisely the drift that would make the
    // measured run length disagree with the rendered wrap.
    float RowFontSize => _large ? 36f : 26f;
    float RowLineHeight => _large ? 46f : 33f;   // ~1.27x (was 1.4x) — denser block
    float RowSidePad => _large ? 48f : 22f;      // keep text off the rail edges without making the narrow panel cramped

    Element LyricsContent(LyricsDocument doc)
    {
        if (!IsTimed(doc)) return UnsyncedLyricsContent(doc);

        var lines = doc.Lines;
        // Bigger type (rail 20 -> 26) and a tighter rhythm. Rows are CONTENT-FIT (variable height) via the measured
        // layout below, so a one-line lyric is short and a two-line lyric is tall — no dead space, no mid-word clipping.
        float fontSz = RowFontSize;
        float lineHt = RowLineHeight;
        float rowPad = _large ? 9f : 7f;            // vertical padding per row; inter-line gap = 2*rowPad
        float sidePad = RowSidePad;
        float rowEst = lineHt + 2f * rowPad;        // measured-layout seed = a single-line row's height
        bool centered = _large;
        float wipeLift = LyricLineView.WipeLiftFor(_large);
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
                    fontSz, lineHt, rowPad, sidePad, wipeLift, centered,
                    ReportLineNode, ReportGlowNode, ReportDofNode, SoftnessOfLine, DofDeclaredFor, () => SeekToLine(idx))) with { Key = "ll" + idx };
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
        _dofRampPending = true;   // a freshly realized row may still be NaN in the σ model — let one pass adopt its target
    }

    // ── Wipe feather geometry (DIP → the engine's reading-order fraction) ─────────────────────────────────────────────
    // GlyphWipe.Softness is a FRACTION of the run's reading-order length, but the feather is a fixed on-screen band, so
    // the DIP constant has to be divided by that length PER LINE. Everything below serves that one conversion.

    // The width every timed row's text lays out into: the scroll viewport less the row's own side padding (rows are
    // Stretch-aligned, so this IS the wrap box the seam must be queried with). One width for the whole document ⇒ one
    // epoch. NaN until the viewport exists (first frames / rail closed) — RunLengthOf treats that as "not measurable yet"
    // and returns a conservative estimate; OnFrame's self-heal replaces the resulting seed once geometry lands.
    float CurrentWrapWidth()
    {
        var scene = Context.Scene;
        var viewport = _viewportNode;
        if (scene is null || viewport.IsNull || !scene.IsLive(viewport) || !scene.HasScroll(viewport)) return float.NaN;
        float w = scene.ScrollRef(viewport).ViewportW - 2f * RowSidePad;
        return w > 1f ? w : float.NaN;
    }

    // The seeded/self-healed Softness for one line. Clamped: under 0.01 the band collapses to a hard per-pixel cut on a
    // long line, over 0.10 a very short line returns to the mushy full-word wash the frame evidence refuted.
    float SoftnessOfLine(int index)
    {
        float runLen = RunLengthOf(index, CurrentWrapWidth());
        return Math.Clamp(LyricLineView.WipeSoftnessDipFor(_large) / runLen,
            LyricLineView.WipeSoftnessMin, LyricLineView.WipeSoftnessMax);
    }

    // Lazily measured reading-order run length for one line, cached per line per width epoch. The lazy fill costs one
    // text-seam layout per line per resize and NOTHING thereafter, so the per-frame callers (OnFrame's self-heal) only
    // ever read the array. A wrap-width move of ≥0.5 DIP retires every entry at once — the rail is fixed-width, but the
    // fullscreen surface resizes with the window.
    float RunLengthOf(int index, float wrapWidth)
    {
        var runLen = _lineRunLen;
        if (float.IsNaN(wrapWidth))
        {
            // No trustworthy wrap width yet (pre-layout, or the rail is closed): answer from the last live epoch, else a
            // plausible ~12-em line. Deliberately does NOT poison the cache — the seed this produces is exactly what
            // OnFrame's self-heal replaces the moment real geometry lands.
            float last = _runLenWrapW;
            return last > 1f ? last : RowFontSize * 12f;
        }
        if (float.IsNaN(_runLenWrapW) || MathF.Abs(_runLenWrapW - wrapWidth) >= 0.5f)
        {
            _runLenWrapW = wrapWidth;
            Array.Fill(runLen, float.NaN);
        }
        if ((uint)index >= (uint)runLen.Length) return wrapWidth;
        float cached = runLen[index];
        if (!float.IsNaN(cached)) return cached;
        float measured = MeasureRunLength(index, wrapWidth);
        runLen[index] = measured;
        return measured;
    }

    // One seam query per line per epoch: GetRangeRects over the WHOLE string returns one rect per wrapped visual-line
    // fragment, and the sum of their widths is the reading-order extent the wipe sweeps (the replay lays those same
    // fragments end-to-end). Queried under the TextStyle LyricLineView.LineText builds, through the SAME layout pipeline
    // that renders it, so the fragments are the rendered wrap — not an approximation of it.
    float MeasureRunLength(int index, float wrapWidth)
    {
        var doc = _doc;
        if (doc is null || (uint)index >= (uint)doc.Lines.Count) return wrapWidth;
        string text = doc.Lines[index].Text;
        if (text.Length == 0 || TextSeam.Default is not { } fonts) return wrapWidth;

        bool centered = _large;
        var style = new TextStyle(default, RowFontSize, 700,
            centered ? TextWrap.NoWrap : TextWrap.Wrap,
            centered ? TextTrim.CharacterEllipsis : TextTrim.None,
            centered ? 1 : 0,
            CharSpacing: 0f, LineHeight: RowLineHeight);
        // A wrapped lyric line is 1-3 visual lines; 8 is generous headroom and the seam DROPS the excess rather than
        // failing, so a pathological line would under-sum. Treat a full span as possibly truncated and fall back to the
        // wrap-box estimate for those fragments — an over-long run only tightens the feather, never widens it.
        Span<RectF> fragments = stackalloc RectF[8];
        int n = fonts.GetRangeRects(text, in style, wrapWidth, 0, text.Length, fragments);
        float sum = 0f;
        for (int i = 0; i < n; i++) sum += fragments[i].W;
        if (n == fragments.Length) sum = MathF.Max(sum, n * wrapWidth);
        return sum > 1f ? sum : wrapWidth;
    }

    internal LyricsFollowMode FollowModeValue => _followMode.Value;   // LyricsTicker-only subscription; parent Render never reads it

    static bool SuppressesDof(LyricsFollowMode mode) => mode != LyricsFollowMode.Following;

    float DofForLine(int index) => DofSigmaFor(index, _activeLine.Peek(), _interlude.Peek());

    // The σ ladder rung for one line against a given active/interlude. Split out so the per-frame ramp can hoist the two
    // signal Peeks out of its whole-document loop without forking the ladder into a second definition.
    static float DofSigmaFor(int index, int active, bool interlude)
    {
        if (active < 0) return LyricsFx.DofSigma(6);
        if (interlude && index == active) return LyricsFx.DofSigma(1);
        return LyricsFx.DofSigma(Math.Min(Math.Abs(index - active), 6));
    }

    // Suppression is a TARGET move, not a write of its own: DriveDofRamp owns every σ the nodes ever see, so engaging
    // suppression (FollowMode leaves Following) EASES the ladder out — each line's target drops to 0, a decrease — and
    // releasing it SNAPS the ladder back (an increase), through the exact directional model the emphasis ladder uses.
    // The snap-back on release is deliberate and shares the front-loaded-recede rationale; if it reads harsh in the
    // feel-test, that is a tuning call on DriveDofRamp's direction test, not a special case here.
    void ApplyDofSuppression(SceneStore? scene)
    {
        _dofRampPending = true;
        // Resolve it now if a scene is at hand (the mode can flip from a scroll callback, outside the ticker); otherwise
        // the next OnFrame picks it up — LyricsTicker keeps ticking for the whole detached/resyncing window.
        if (scene is not null) DriveDofRamp(scene, Environment.TickCount64);
    }

    // Exponential time constant for a σ DECREASE: ~95% of the way in 200 ms (1 - e^(-200/65) = 0.954), matching the
    // reference's incoming line, which de-blurs progressively DURING the move and is crisp as it lands.
    const float DofRampTauMs = 65f;
    // σ write gate. The recorder buckets σ at 0.5 in the blur pin key, so a finer gate mints no extra pins but still
    // dirties paint every single frame of the ramp; 0.1 keeps the ramp visually smooth (~15 writes across 200 ms, ≤3 pin
    // buckets crossed) and bounded. The LANDING write is exempt so the node ends EXACTLY on the target, never a hair off.
    const float DofWriteEps = 0.1f;

    void DriveDofRamp(SceneStore scene, long wallMs)
    {
        var cur = _dofCurrent;
        float dt = _dofRampWallMs == 0L ? KaraokeWipeIntervalMs : Math.Clamp((float)(wallMs - _dofRampWallMs), 0f, 100f);
        _dofRampWallMs = wallMs;
        if (!_dofRampPending || cur.Length == 0) return;

        bool suppress = SuppressesDof(_followMode.Peek());
        int active = _activeLine.Peek();
        bool interlude = _interlude.Peek();
        float k = 1f - MathF.Exp(-dt / DofRampTauMs);
        bool moving = false;
        for (int i = 0; i < cur.Length; i++)
        {
            float target = suppress ? 0f : DofSigmaFor(i, active, interlude);
            float c = cur[i];
            bool landed = true;
            if (float.IsNaN(c) || target >= c)
            {
                c = target;                              // first visit, or an INCREASE ⇒ snap (front-loaded recede)
            }
            else
            {
                c += (target - c) * k;                   // DECREASE ⇒ ease in (incoming line sharpening)
                if (c - target <= 0.01f) c = target;     // land exactly — no asymptote residue
                else { landed = false; moving = true; }
            }
            cur[i] = c;

            var h = (uint)i < (uint)_dofNodes.Length ? _dofNodes[i] : NodeHandle.Null;
            if (h.IsNull || !scene.IsLive(h)) continue;
            ref NodePaint p = ref scene.Paint(h);
            if (MathF.Abs(p.BlurSigma - c) < (landed ? 0.001f : DofWriteEps)) continue;
            p.BlurSigma = c;
            scene.Mark(h, NodeFlags.PaintDirty);
        }
        _dofRampPending = moving;
    }

    // The σ the ELEMENT declares for line i (LyricLineView's `Blur`): the ramp model's LIVE value, so a re-render landing
    // mid-ramp re-asserts the in-flight σ instead of stomping the node back to the rest target — the same element-vs-
    // driver agreement the scale/opacity springs document at the bottom of LyricLineView.Render. NaN (never driven) falls
    // back to the rest target, which is what an undriven node should mount at.
    float DofDeclaredFor(int index)
    {
        float cur = (uint)index < (uint)_dofCurrent.Length ? _dofCurrent[index] : float.NaN;
        if (!float.IsNaN(cur)) return cur;
        return SuppressesDof(_followMode.Peek()) ? 0f : DofForLine(index);
    }

    void SetFollowMode(LyricsFollowMode next, SceneStore? scene)
    {
        var previous = _followMode.Peek();
        if (previous == next) return;
        bool wasSuppressed = SuppressesDof(previous);
        bool nowSuppressed = SuppressesDof(next);
        _followMode.Value = next;
        if (wasSuppressed != nowSuppressed) ApplyDofSuppression(scene);
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
        if (emphasisChanged) { PushEmphasis(); _dofRampPending = true; }
        // Directional DoF ramp — core lane, never budget-deferred: it is bounded (it self-quiesces the pass after every
        // line lands) and the ladder is INFORMATION, not decoration. It must run AFTER PushEmphasis so the rows that are
        // about to re-render this frame read a σ model that already reflects the new active line.
        DriveDofRamp(scene, wallMs);
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
            // elides them (and the blur pin-cache HITS). main and glow share this run width (same text/size/wrap). The
            // SETTLED endpoints are exempt: they must stay EXACTLY 0/1, because that is the condition the replay's
            // settled-split fast path tests to drop the run into the cheap plain glyph batch — and a fractional runW
            // would land a rounded 1 at 1.0017 (Round(295.5)/295.5) instead. ComputeSplit already returns exact 1 on a
            // sung-out line (played == total) and exact 0 before the first syllable, and the lead-clamp above preserves
            // both (it is skipped at 0, and Clamp caps at exactly 1), so the endpoints really are exact here.
            float runW = scene.AbsoluteRect(mainNode).W;
            if (runW > 1f && split > 0f && split < 1f) split = MathF.Round(split * runW) / runW;
            bool forceWipe = forceVisual || voiceLine != _lastWipeLine || _lastWipeWallMs == 0L;
            if (!forceWipe && wallMs - _lastWipeWallMs < KaraokeWipeIntervalMs) return;

            _lastWipeWallMs = wallMs;
            _lastWipeLine = voiceLine;

            // The feather the seed baked in can be STALE — the seed for a line realized before the viewport had geometry
            // (or before a fullscreen resize) resolved against a fallback run length. Self-heal it into the same write
            // instead of a second one: at the ≥0.002 fraction gate this fires at most once per line per width epoch.
            float softness = SoftnessOfLine(voiceLine);

            // SETTLED ⇒ STOP WRITING. The wipe's Split/Softness/Lift fold verbatim into the recorder's BlurPinKey, so
            // re-writing a line that is ALREADY fully sung misses the blur pin on exactly the frames the outgoing line is
            // being blurred+dimmed away (it stays the voice line for the whole ~LeadMs window after focus has moved, and
            // WipeLeadFrac drives Split to 1 a few % before that). Freezing the byte-identical value keeps the pin
            // cache hitting AND keeps the run in the replay's cheap plain glyph batch instead of the gradient batch.
            bool settled = (split >= 1f && mw.Split >= 1f) || (split <= 0f && mw.Split <= 0f);

            // Readable main wipe — the karaoke reveal the user sees (S2).
            if (!settled && (MathF.Abs(split - mw.Split) > 0.0008f || MathF.Abs(softness - mw.Softness) > 0.002f))
            {
                scene.SetGlyphWipe(mainNode, mw with { Split = split, Softness = softness });
                scene.Mark(mainNode, NodeFlags.PaintDirty);
            }

            // Glow bloom — same split; σ co-decays with the bound glow alpha (FIX B melt) so the halo tightens as it
            // dims. Deferred with the glow lane during a main scroll: a per-frame split/σ write here would invalidate
            // the scroll blur-pin every frame — the halo holds its last frame instead (barely visible under the fill).
            if (runGlow && !glowNode.IsNull && scene.IsLive(glowNode) && scene.TryGetGlyphWipe(glowNode, out var w))
            {
                // The halo must track the main layer's geometry EXACTLY (same split, same feather, same lift) or the
                // bloom drifts out from under the crisp text; it gets the same settled write-stop for the same reason.
                bool glowSettled = (split >= 1f && w.Split >= 1f) || (split <= 0f && w.Split <= 0f);
                bool glowDirty = !glowSettled && (MathF.Abs(split - w.Split) > 0.0008f || MathF.Abs(softness - w.Softness) > 0.002f);
                if (glowDirty) scene.SetGlyphWipe(glowNode, w with { Split = split, Softness = softness });
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
    readonly Signal<int> _emphasis;   // packed per-line emphasis (bucket + interlude bit + past bit) — value-gated by LyricsView
    readonly FloatSignal _nowMs;
    readonly Signal<LyricsFollowMode> _followMode; // stable parent signal; Peek only so a mode flip never fans out row renders
    readonly FloatSignal? _glowFade;   // per-line halo alpha (owned + ramped by LyricsView); bound as the glow wrapper's Opacity
    readonly float _fontSz;
    readonly float _lineHt;
    readonly float _rowPad;
    readonly float _sidePad;
    readonly float _wipeLift;   // per-word rise in DIP (GlyphWipe.Lift) — surface-scaled by LyricsView, see WipeLiftFor
    readonly bool _centered;
    readonly Action<int, NodeHandle> _reportNode;
    readonly Action<int, NodeHandle> _reportGlow;
    readonly Action<int, NodeHandle> _reportDof;
    readonly Func<int, float> _softnessOf;   // DIP feather → this line's reading-order fraction (LyricsView.SoftnessOfLine)
    readonly Func<int, float> _dofSigmaOf;   // the σ ramp model's LIVE value for this line (LyricsView.DofDeclaredFor)
    readonly Action _onSeek;

    // The packed emphasis this row last rendered with, -1 before its first render. The ONLY thing it exists for is the
    // DIRECTION of the opacity spring (see Render): a row re-renders exactly when its packed emphasis changes, so the
    // previous value is a free, exact proxy for "which way is this row's opacity about to travel?".
    int _prevEmphasis = -1;

    public LyricLineView(int index, LyricLine line, Signal<int> emphasis, FloatSignal nowMs, Signal<LyricsFollowMode> followMode,
        FloatSignal? glowFade,
        float fontSz, float lineHt, float rowPad, float sidePad, float wipeLift, bool centered, Action<int, NodeHandle> reportNode,
        Action<int, NodeHandle> reportGlow, Action<int, NodeHandle> reportDof, Func<int, float> softnessOf,
        Func<int, float> dofSigmaOf, Action onSeek)
    {
        _index = index; _line = line; _emphasis = emphasis; _nowMs = nowMs;
        _followMode = followMode; _glowFade = glowFade;
        _fontSz = fontSz; _lineHt = lineHt; _rowPad = rowPad; _sidePad = sidePad; _wipeLift = wipeLift; _centered = centered;
        _reportNode = reportNode; _reportGlow = reportGlow; _reportDof = reportDof; _softnessOf = softnessOf;
        _dofSigmaOf = dofSigmaOf; _onSeek = onSeek;
    }

    // The halo wrapper's opacity: BOUND to the per-line fade signal so a row re-render re-asserts the live fade value
    // (the reconciler skips bound Opacity) — a static value here would snap the halo at exactly the re-render moments
    // (active/voice flips) the fade exists to smooth.
    Prop<float> GlowOpacity() => _glowFade is { } s ? (Prop<float>)s : 0f;

    public override Element Render()
    {
        // Read ONLY this line's packed emphasis — a value-gated signal LyricsView rewrites as the active line moves, so
        // reading `.Value` here re-renders the row solely when ITS OWN bucket/interlude/past class changes (not on every
        // boundary).
        int e = _emphasis.Value;
        int dist = e & 7;                        // bucket 0..6 (clamped distance from the active line)
        bool interlude = (e & 8) != 0;           // active line sung out into a long instrumental gap — recede it
        bool past = (e & 16) != 0;               // already sung — rides the dimmer of the two opacity ladders
        bool isActive = dist == 0;               // bucket 0 ⇔ this is the active line

        // Emphasis targets. Active line: full focus (scale 1 / opacity 1 / crisp). During an instrumental interlude the
        // still-active sung-out line recedes to a calm look instead of sitting frozen-fully-lit. In the reference the
        // DISTANCE hierarchy is carried almost entirely by OPACITY + DoF BLUR: scale is a flat, barely-there 0.98 on
        // every inactive row (the old 1 - 0.25*f ramp shrank far rows to 0.75, which the frame evidence refutes). The
        // shrink is LEFT-anchored (TransformOriginX 0 in the non-centered branch below), so rows stay flush to the
        // margin instead of breathing about their middle. Voice only drives the karaoke wipe and glow during the lead
        // split, so depth never disagrees with emphasis.
        float scale = interlude ? 0.97f : isActive ? 1f : 0.98f;
        // Row emphasis follows ACTIVE only — voice keeps the karaoke wipe/glow but must not hold full brightness once
        // focus moves (the lead window used to leave the previous line white for its entire sung tail).
        float opacity = OpacityOf(e);
        // DoF σ comes from LyricsView's ramp model, not from `dist` directly: the model owns the in-flight value and
        // agrees with this ladder at rest (see LyricsView.DofDeclaredFor / DriveDofRamp).
        float blur = _followMode.Peek() == LyricsFollowMode.Following ? _dofSigmaOf(_index) : 0f;

        // AMLL scale in BOTH directions; opacity is critical/no-bounce and DIRECTIONAL.
        // Cold mounts still begin at the element rest targets below, so the soft inactive spring cannot flash a new row.
        var key = DepKey.From(dist, (interlude ? 1 : 0) | (isActive ? 2 : 0) | (past ? 4 : 0));
        var scaleSpring = new SpringParams(100f, 25f, 2f);             // AMLL m=2,d=25,k=100
        // Front-loaded outgoing dim: measured, the exiting line falls 252 → 180 luma inside the first ~100 ms of its
        // flight, while the incoming line brightens across the WHOLE handoff. So the opacity spring is ~3× faster when
        // the row is DIMMING. The component cannot see the slab's live value, so direction is read off the emphasis
        // TRANSITION — this row re-renders exactly when its packed emphasis changes, so "was the previous packed value's
        // opacity higher than this one's?" is that same question, asked where the answer is free. (Both the target and
        // the response are pure functions of `e`, and `key` carries all of `e`, so the retarget and the params change
        // together in the one UseSpring re-arm.)
        int prevPacked = _prevEmphasis;
        _prevEmphasis = e;
        bool dimming = prevPacked >= 0 && opacity < OpacityOf(prevPacked) - 0.0005f;
        var opacitySpring = SpringParams.FromResponse(dimming ? 0.30f : 0.889f, 1.0f);
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
            // The feather is authored in DIP but the engine takes it as a fraction of THIS line's reading-order length,
            // so LyricsView converts per line (and OnFrame re-heals the value if the line was seeded before the viewport
            // had geometry). main and glow MUST share it — see the glow comment below.
            float softness = _softnessOf(_index);
            // MAIN = the readable foreground, and it CARRIES THE WIPE — this is the progressive reveal the user SEES:
            // sung glyphs full-bright Primary (Before), unsung glyphs dim-but-readable (After = Primary @ UnsungAlpha),
            // sitting _wipeLift DIP low and rising as the feather sweeps them. The row group opacity spring (active-only
            // emphasis) dims the whole row once focus moves; this wipe does sung/unsung.
            Element main = LineText(_line.Text, Tok.TextPrimary) with
            {
                Wipe = new GlyphWipe(Before: Tok.TextPrimary, After: Tok.TextPrimary with { A = UnsungAlpha },
                    Split: split, Softness: softness, Lift: _wipeLift),
                OnRealized = h => _reportNode(_index, h),
            };
            // GLOW = a soft blurred bloom UNDER the main, on the SUNG glyphs only (After.A = 0 ⇒ unsung glyphs fully
            // transparent). Its glyphs mount once the row is NEAR the focus (dist ≤ 2 — still dim + blurred, so the
            // content swap itself can never pop on the focal row); a peripheral line pays no second glyph run. OnFrame
            // drives its split (same value as main) + its constant σ; its VISIBILITY is the cross-fade wrapper below.
            // Softness AND Lift match the main layer exactly: the halo is the same glyphs at the same geometry, so any
            // disagreement would let the bloom float out from under the crisp text at the boundary.
            bool near = dist <= 2;
            Element glowText = (near
                ? LineText(_line.Text, Tok.TextPrimary) with
                  {
                      Wipe = new GlyphWipe(Before: Tok.TextPrimary, After: Tok.TextPrimary with { A = 0f },
                          Split: split, Softness: softness, Lift: _wipeLift),
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
                // Scroll motion translates this stationary glyph subtree every frame. Reuse its retained blur when it
                // exists; otherwise render crisp for the moving frame and rebuild the full halo after settling.
                BlurCachePolicy = BlurCachePolicy.HoldIfCached,
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
            // The σ ramp model's live value (LyricsView owns the in-flight σ by direct node write): declaring the REST
            // target here instead would stomp an in-flight incoming de-blur back to crisp on the very re-render that
            // starts it. At settle the two agree, exactly like the scale/opacity springs' rest targets below.
            Blur = blur,
            // The depth-of-field bands are static at rest but scroll with the lyrics viewport. Holding a compatible
            // cached blur avoids a fresh Gaussian for every scroll submit; a cache miss stays crisp until the viewport
            // settles, when the normal recorder path refreshes it.
            BlurCachePolicy = BlurCachePolicy.HoldIfCached,
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

    // ── Emphasis ladder ──────────────────────────────────────────────────────────────────────────────────────────────
    // The row-group opacity for a PACKED emphasis value — the one place the ladder lives, so the direction test above can
    // ask it the same question about the previous packed value. A switch, not a table: no allocation, no indirection.
    //
    // Measured off the reference capture: peak luma per ring is 252 (active) / 133 / 113 / 101 / 89 / 72, and a line the
    // song has ALREADY passed settles DIMMER than an upcoming line the same distance away (118 vs 133) — the past/future
    // asymmetry the old single `max(0.16, 0.55*(1-f))` ramp could not express. The past rows sit far lower in ALPHA than
    // that luma gap suggests because their TEXT is the sung white (Primary at full alpha behind a settled wipe) where an
    // upcoming row's is the unsung gray: the same row alpha would read markedly brighter on a past line.
    internal const float InterludeOpacity = 0.55f;
    internal static float OpacityOf(int packed)
    {
        if ((packed & 8) != 0) return InterludeOpacity;   // interlude recede — its own value, not a rung of either ladder
        int dist = packed & 7;
        if (dist == 0) return 1f;                         // active line: full focus
        return (packed & 16) != 0
            ? dist switch { 1 => 0.19f, 2 => 0.13f, _ => 0.10f }                                 // past (sung, white-base)
            : dist switch { 1 => 0.45f, 2 => 0.24f, 3 => 0.14f, 4 => 0.11f, _ => 0.10f };        // future (unsung, gray-base)
    }

    // ── Wipe texture ─────────────────────────────────────────────────────────────────────────────────────────────────
    // Every constant below is measured off the frame-by-frame Apple Music reference capture (lyrics-parity campaign,
    // 2026-08-03; the video is 480 px wide at cap-height ≈30 px, so source px × 0.62 ⇒ rail DIP at font 26).

    // Small POSITIVE wipe lead: nudge the bright boundary a few % ahead of the strictly-played fraction so the edge reads
    // as anticipating the voice. Shared by the element seed (LyricLineView.Render) and the per-frame driver (OnFrame) so
    // the reconcile re-render and the OnFrame writer agree on the boundary (no snap-back — S3-4).
    internal const float WipeLeadFrac = 0.04f;

    // Unsung glyph alpha (GlyphWipe.After). Reference: crossed text holds 250-255 luma while unsung sits at 183-192 over
    // a ~90 background ⇒ α ≈ 0.59 of the sung white. The 0.45 shipped before read a whole step too dim/too far away.
    internal const float UnsungAlpha = 0.58f;

    // Feather width of the sung/unsung boundary, IN DIP. Reference: a narrow 5-12 source-px band ≈ 2-3% of the run width
    // — the fixed Softness 0.14 shipped before was 4-9× too wide (a mushy word-sized wash, not a defined edge). DIP is
    // the authored unit because GlyphWipe.Softness is a FRACTION of the run's READING-ORDER length, so the same on-screen
    // band is a different fraction on every line; LyricsView.SoftnessOfLine does the per-line division.
    internal const float WipeSoftnessDip = 5f;
    internal const float WipeSoftnessDipLarge = 7f;
    internal static float WipeSoftnessDipFor(bool large) => large ? WipeSoftnessDipLarge : WipeSoftnessDip;
    // Clamps on the converted fraction: below 0.01 the band collapses to a hard per-pixel cut on a long line; above 0.10
    // a very short line would be back in the refuted wash.
    internal const float WipeSoftnessMin = 0.01f;
    internal const float WipeSoftnessMax = 0.10f;

    // Per-word vertical rise (GlyphWipe.Lift — dy only, no scale, since the engine's char pop was refuted and removed).
    // Reference: unsung words sit ~2 source px low at cap-height 30 px ⇒ ≈0.048 em ⇒ 1.25 DIP at font 26.
    internal const float WipeLiftDip = 1.25f;
    internal const float WipeLiftDipLarge = 1.75f;
    internal static float WipeLiftFor(bool large) => large ? WipeLiftDipLarge : WipeLiftDip;

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
