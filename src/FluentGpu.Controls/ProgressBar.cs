using FluentGpu.Foundation;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Animation;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>The drawing state of a <see cref="ProgressBar"/> — mirrors WinUI's CommonStates (Determinate / Paused /
/// Error and their Indeterminate counterparts), selected from IsIndeterminate + ShowPaused + ShowError.</summary>
public enum ProgressBarState : byte { Normal = 0, Paused = 1, Error = 2 }

/// <summary>How an indeterminate <see cref="ProgressBar"/> says "busy" (<see cref="ProgressBar.Style.IndeterminateForm"/>).
/// <see cref="Sweep"/> is WinUI's: two accent indicators sweeping the track. <see cref="Dots"/> is the Metro train: a few small
/// dots that rush in from the left, slow to a drift through the middle and rush out on the right, one after the other.</summary>
public enum ProgressBarIndeterminateForm : byte { Sweep = 0, Dots = 1 }

/// <summary>
/// A WinUI ProgressBar (1:1). A 3px-min band (<c>ProgressBarMinHeight</c>) holding a 1px track
/// (<c>ProgressBarTrackHeight</c>, <c>ControlStrongStrokeColorDefault</c>) under an accent indicator
/// (<c>AccentFillColorDefaultBrush</c>) with a 1.5px corner. <see cref="Determinate"/> fills the indicator to a 0..1
/// value; <see cref="Indeterminate"/> sweeps the two clipped accent indicators across the track on the looping
/// translate keyframes WinUI binds from <c>ProgressBarTemplateSettings</c> (Container/Container2 positions). Paused/Error
/// stop the sweep and settle a full-width bar at <c>ContainerAnimationMidPosition</c>, recolored to
/// <c>SystemFillColorCaution</c> / <c>SystemFillColorCritical</c> (instant recolor — no fill-color anim channel).
/// </summary>
public static class ProgressBar
{
    // Template parts (see TemplateParts). Each part's doc lists the props the control OWNS (re-asserted after any
    // modifier — a Parts customization cannot win those).
    /// <summary>The 1px under-track (WinUI ProgressBarTrack). Owned: none — its Opacity-0 in the indeterminate
    /// states is stock per-render styling a modifier may override.</summary>
    public const string PartTrack = "Track";
    /// <summary>The foreground indicator (WinUI DeterminateProgressBarIndicator, and BOTH
    /// IndeterminateProgressBarIndicator/2 — the modifier runs on each sweeping indicator). The value-driven Width
    /// and the Paused/Error recolor are stock per-render styling (override-able). Owned: OnRealized (the
    /// indeterminate sweep refs, chained with any modifier-supplied handler).</summary>
    public const string PartFill = "Fill";

    // ── WinUI sizes/corners (ProgressBar_themeresources.xaml) ──────────────────────────────────────
    const float MinHeight = 3f;          // ProgressBarMinHeight
    const float TrackHeight = 1f;         // ProgressBarTrackHeight
    const float IndicatorRadius = 1.5f;   // ProgressBarCornerRadius (indicator)
    const float TrackRadius = 0.5f;       // ProgressBarTrackCornerRadius
    const float DefaultWidth = 240f;

    /// <summary>The measured-width quantum for a stretched (<c>width: float.NaN</c>) indeterminate bar — see
    /// <see cref="Indeterminate"/>. Sub-quantum layout wobble (e.g. a settling flex pass) is absorbed at the
    /// measured-width signal, so it never re-renders the bar or re-arms the sweep.</summary>
    const float StretchWidthQuantum = 4f;

    // Indeterminate indicator widths (ProgressBar.cpp SetProgressBarIndicatorWidth): 40% / 60% of the track width.
    const float Indicator1Frac = 0.40f;
    const float Indicator2Frac = 0.60f;

    // Indeterminate loop = 2.0s (ProgressBar.xaml Indeterminate storyboard, RepeatBehavior="Forever").
    const float LoopMs = 2000f;
    // The shared indeterminate translate easing = KeySpline 0.4,0.0,0.6,1.0 (FastOutSlowIn-ish ease).
    static readonly EasingSpec IndetEase = EasingSpec.CubicBezier(0.4f, 0.0f, 0.6f, 1.0f);

    // WinUI's TemplateSettings.ContainerAnimationMidPosition (ProgressBar.cpp: always 0). IndeterminatePaused/Error
    // settle indicator2's TranslateX here so the full-width caution/critical bar sits STATIC over the track.
    const float ContainerAnimationMidPosition = 0f;

    /// <summary>The bar's look, read at RENDER through the control style table (<see cref="ControlStyles.ProgressBarStyle"/>).
    /// Every default is the ProgressBar_themeresources value the bar has always used.</summary>
    public sealed record Style
    {
        /// <summary>The band height and the indicator's thickness (ProgressBarMinHeight 3).</summary>
        public float Height { get; init; } = MinHeight;
        /// <summary>The under-track's thickness (ProgressBarTrackHeight 1), centred in the band.</summary>
        public float TrackHeight { get; init; } = ProgressBar.TrackHeight;
        /// <summary>The indicator radius (ProgressBarCornerRadius 1.5) and the track radius (ProgressBarTrackCornerRadius 0.5).</summary>
        public float IndicatorRadius { get; init; } = ProgressBar.IndicatorRadius;
        public float TrackRadius { get; init; } = ProgressBar.TrackRadius;
        /// <summary>The track colour (ControlStrongStrokeColorDefault).</summary>
        public ColorF TrackFill { get; init; } = Tok.StrokeControlStrongDefault;
        /// <summary>The indicator per state: Normal (AccentFillColorDefault), Paused (SystemFillColorCaution), Error (SystemFillColorCritical).</summary>
        public ColorF Foreground { get; init; } = Tok.AccentDefault;
        public ColorF PausedForeground { get; init; } = Tok.SystemFillCaution;
        public ColorF ErrorForeground { get; init; } = Tok.SystemFillCritical;
        /// <summary>Whether the track shows under the indeterminate sweep (WinUI hides it: false).</summary>
        public bool IndeterminateTrack { get; init; }
        /// <summary>How the indeterminate bar moves: WinUI's sweep (the default), or the train of dots.</summary>
        public ProgressBarIndeterminateForm IndeterminateForm { get; init; }
        /// <summary>The <see cref="ProgressBarIndeterminateForm.Dots"/> train: how many dots, a dot's side and its corner radius
        /// (2 on a side of 4 is a round dot, 0 a square one). The band keeps <see cref="Height"/>, so the bar takes the room the sweep
        /// takes; the train rides centred on it and a dot is drawn whole where the band is thinner than a dot.</summary>
        public int DotCount { get; init; } = 5;
        public float DotSize { get; init; } = 4f;
        public float DotRadius { get; init; } = 2f;

        /// <summary>The indicator foreground for <paramref name="state"/> (ProgressBarForeground / ...PausedForegroundColor / ...ErrorForegroundColor).</summary>
        public ColorF ForegroundFor(ProgressBarState state) => state switch
        {
            ProgressBarState.Paused => PausedForeground,
            ProgressBarState.Error => ErrorForeground,
            _ => Foreground,
        };
    }

    /// <summary>The bar style now: the active theme's (<see cref="ControlStyles.ProgressBarStyle"/>), else <see cref="FluentStyle"/>.</summary>
    public static Style DefaultStyle => ControlStyles.Resolve(ControlStyles.ProgressBarStyle, FluentStyle);

    /// <summary>The built-in Fluent default (what a theme table without a ProgressBar entry resolves to). Reads the live Tok on every call.</summary>
    public static Style FluentStyle() => new();

    // ── Create (the one canonical factory) ───────────────────────────────────────────────────────────
    /// <summary>The ONE canonical ProgressBar factory. <paramref name="value"/> = a caller-owned 0..1
    /// <see cref="FloatSignal"/> tracked compositor-live (the determinate indicator's width binds to it, so a scrub
    /// updates the bar with no re-render); <c>null</c> = the indeterminate sweep. <paramref name="state"/> selects the
    /// indicator color (Normal accent / Paused caution / Error critical). <see cref="Determinate"/> (static value) and
    /// <see cref="Indeterminate"/> are one-line forwarders onto this.</summary>
    public static Element Create(FloatSignal? value = null, float width = DefaultWidth,
                                 ProgressBarState state = ProgressBarState.Normal, TemplateParts? parts = null)
        => value is null
            ? DefaultStyle.IndeterminateForm == ProgressBarIndeterminateForm.Dots
                // The style's train of dots (one component for a fixed and a stretched width).
                ? Embed.Comp(new Props(width, state, parts), () => new IndeterminateDots())
            : (float.IsNaN(width)
                // width: float.NaN ⇒ stretch to the parent-offered width (see StretchIndeterminateBar) — a
                // separate component so the fixed-width path below stays byte-identical (no measured-width hook,
                // no extra wrapper node) for every caller that doesn't ask for stretch.
                ? Embed.Comp(new Props(width, state, parts), () => new StretchIndeterminateBar())
                : Embed.Comp(new Props(width, state, parts), () => new IndeterminateBar()))
            // Determinate tracking the signal: the indicator width is a bound Func (compositor/relayout, no re-render).
            : DeterminateView((Prop<float>)(Func<float>)(() =>
              {
                  float v = value.Value;   // subscribe the width bind → scrub the bar live
                  return (v < 0f ? 0f : v > 1f ? 1f : v) * width;
              }), width, state, parts);

    // ── Determinate ────────────────────────────────────────────────────────────────────────────────
    /// <summary>Determinate progress with a STATIC value; <paramref name="value"/> is clamped to 0..1, indicator width
    /// = value * width. A one-line forwarder onto <see cref="Create"/>'s shared builder. <paramref name="state"/>
    /// selects the indicator color (Normal accent / Paused caution / Error critical). <paramref name="parts"/> =
    /// per-part styling keyed by <see cref="PartTrack"/>/<see cref="PartFill"/>. For a LIVE value use <see cref="Create"/>
    /// with a <see cref="FloatSignal"/>.</summary>
    public static BoxEl Determinate(float value, float width = DefaultWidth, ProgressBarState state = ProgressBarState.Normal,
                                    TemplateParts? parts = null)
    {
        float v = value < 0f ? 0f : value > 1f ? 1f : value;
        return DeterminateView((Prop<float>)(v * width), width, state, parts);
    }

    /// <summary>Shared determinate builder: the ZStack track + indicator, with the indicator's width supplied as either
    /// a static value (<see cref="Determinate"/>) or a signal-bound <c>Func</c> (<see cref="Create"/>). The value-driven
    /// Width + state recolor are stock (a PartFill modifier sees and may override them).</summary>
    static BoxEl DeterminateView(Prop<float> indicatorWidth, float width, ProgressBarState state, TemplateParts? parts)
    {
        var st = DefaultStyle;   // read now (the factory runs in its caller's render)
        return new BoxEl
        {
            ZStack = true,
            Width = width,
            Height = st.Height,
            Role = AutomationRole.ProgressBar,
            Children =
            [
                // ProgressBarTrack: 1px, ControlStrongStrokeColorDefault, 0.5px corner, vertically centered in the band.
                parts.Apply(PartTrack, new BoxEl
                {
                    Width = width,
                    Height = st.TrackHeight,
                    OffsetY = (st.Height - st.TrackHeight) / 2f,
                    Corners = CornerRadius4.All(st.TrackRadius),
                    Fill = st.TrackFill,
                }),
                // DeterminateProgressBarIndicator: foreground fill, 1.5px corner, left-aligned, width = value * track width.
                parts.Apply(PartFill, new BoxEl
                {
                    Width = indicatorWidth,
                    Height = st.Height,
                    Corners = CornerRadius4.All(st.IndicatorRadius),
                    Fill = st.ForegroundFor(state),
                }),
            ],
        };
    }

    // ── Indeterminate ────────────────────────────────────────────────────────────────────────────
    /// <summary>Indeterminate progress: the two clipped accent indicators sweeping across the track on the WinUI
    /// ProgressBarTemplateSettings translate keyframes. In Paused/Error, the track hides and only indicator2 shows,
    /// recolored to caution/critical (matching WinUI's IndeterminatePaused / IndeterminateError visual states).
    /// <paramref name="width"/> = <c>float.NaN</c> STRETCHES the bar to the parent-offered width instead of a fixed
    /// DIP value (the standard "facet-switch busy bar" pattern: full-content-width, pinned under a dimmed page) —
    /// the resolved width is read back via <c>UseMeasuredWidth</c> (quantum 4) and the sweep re-arms at the new
    /// extent whenever the parent resizes. A finite width is unaffected: no measurement, no extra wrapper node.
    /// <paramref name="parts"/> = per-part styling keyed by <see cref="PartTrack"/>/<see cref="PartFill"/> (the
    /// PartFill modifier runs on BOTH sweeping indicators; PartTrack is opacity-0 by default in every indeterminate
    /// state — override it, e.g. <c>b => b with { Opacity = 1f }</c>, for a visible 1px track under the sweep).</summary>
    public static Element Indeterminate(float width = DefaultWidth, ProgressBarState state = ProgressBarState.Normal,
                                        TemplateParts? parts = null)
        => Create(null, width, state, parts);

    /// <summary>Controlled props RE-PUSHED to the stateful core (<c>Embed.Comp(props, …)</c>): a reused ComponentEl
    /// never re-runs its factory, so runtime-changeable props are delivered live (equality-gated); the core reads them
    /// with <c>UseProps</c>.</summary>
    internal sealed record Props(float Width, ProgressBarState State, TemplateParts? Parts);

    // ── the train of dots (ProgressBarIndeterminateForm.Dots) ───────────────────────────────────────────────────────
    // One dot's lap, on a 4 s loop: it waits off the left edge, covers the first third in 0.5 s slowing down, drifts over the
    // middle third in 1.5 s, leaves over the last third in 0.5 s speeding up, and waits off the right edge for the rest of the
    // loop. Each dot starts 0.2 s after the one before, so the train bunches up in the middle and strings out at both ends.
    // The two curves meet the drift at its own speed (their slope there is 1/3, the ratio of the two speeds), so a dot never
    // kinks where it changes pace.
    const float DotsLoopMs = 4000f;
    const float DotsStaggerMs = 200f;
    const float DotsEnterEnd = 0.125f, DotsDriftEnd = 0.5f, DotsExitEnd = 0.625f;   // 0.5 s, 2.0 s and 2.5 s of the loop
    static readonly EasingSpec DotsEnterEase = EasingSpec.CubicBezier(0.1f, 0.5f, 0.4f, 0.8f);
    static readonly EasingSpec DotsExitEase = EasingSpec.CubicBezier(0.6f, 0.2f, 0.9f, 0.5f);

    /// <summary>A dot's left edge over one lap of a track <paramref name="width"/> wide (the dot is <paramref name="dot"/> across):
    /// off the left edge, a third in, two thirds in, off the right edge, and held there to the end of the loop.</summary>
    internal static Keyframe[] DotLap(float width, float dot) =>
    [
        new(0f, -dot, Easing.Linear),
        new(DotsEnterEnd, width / 3f - dot / 2f, DotsEnterEase),
        new(DotsDriftEnd, width * 2f / 3f - dot / 2f, Easing.Linear),
        new(DotsExitEnd, width, DotsExitEase),
        new(1f, width, Easing.Linear),
    ];

    /// <summary>Where dot <paramref name="index"/> of <paramref name="count"/> stands while the bar is Paused or in Error: the train
    /// at rest in the middle of the track, a dot's width apart.</summary>
    internal static float DotRest(float width, float dot, int index, int count)
        => (width - (2 * count - 1) * dot) / 2f + index * 2f * dot;

    /// <summary>The indeterminate bar as a train of dots (see the constants above). A fixed width is taken as given; a stretched
    /// one (<c>width: float.NaN</c>) is read back from layout exactly as <see cref="StretchIndeterminateBar"/> does, and a new
    /// width starts the train again over the new track. Paused and Error stop the train at rest in the middle, in the state's
    /// colour. <see cref="PartFill"/> runs on every dot; <see cref="PartTrack"/> is hidden unless the style shows it.</summary>
    private sealed class IndeterminateDots : Component
    {
        NodeHandle[] _dots = [];
        Action<NodeHandle>[] _capture = [];

        public override Element Render()
        {
            var props = UseProps<Props>();
            bool stretch = float.IsNaN(props.Width);
            float width = stretch ? MathF.Max(0f, UseMeasuredWidth(StretchWidthQuantum).Value) : props.Width;
            var state = props.State;
            var st = DefaultStyle;   // read now: a theme swap re-renders the bar in the new look
            int count = Math.Max(1, st.DotCount);
            float dot = st.DotSize;
            bool rest = state != ProgressBarState.Normal;
            if (_dots.Length != count)
            {
                _dots = new NodeHandle[count];
                _capture = new Action<NodeHandle>[count];
                for (int i = 0; i < count; i++)
                {
                    int at = i;
                    _capture[i] = h => _dots[at] = h;
                }
            }

            UseEffect(() =>
            {
                var anim = Context.Anim;
                var scene = Context.Scene;
                if (anim is null || scene is null || width <= 0f) return;
                Keyframe[]? lap = rest ? null : DotLap(width, dot);
                for (int i = 0; i < _dots.Length; i++)
                {
                    NodeHandle h = _dots[i];
                    if (h.IsNull || !scene.IsLive(h)) continue;
                    if (lap is not null)
                    {
                        anim.Keyframes(h, AnimChannel.TranslateX, lap, DotsLoopMs, loop: true, delayMs: i * DotsStaggerMs, cadence: Cadence.Display);
                        continue;
                    }
                    // Paused / Error: the train stops where it rests. A one-shot track onto the rest position ends at once and
                    // leaves no row behind (a running lap would otherwise park the dot wherever it was).
                    float at = DotRest(width, dot, i, _dots.Length);
                    anim.Keyframes(h, AnimChannel.TranslateX, [new(0f, at, Easing.Linear), new(1f, at, Easing.Linear)], 1f);
                }
            }, DepKey.From(HashCode.Combine(width, state, count, dot)));

            ColorF fg = st.ForegroundFor(state);
            var track = props.Parts.Apply(PartTrack, new BoxEl
            {
                Width = width,
                Height = st.TrackHeight,
                OffsetY = (st.Height - st.TrackHeight) / 2f,
                Corners = CornerRadius4.All(st.TrackRadius),
                Fill = st.TrackFill,
                Opacity = st.IndeterminateTrack ? 1f : 0f,
            });
            var kids = new Element[count];
            for (int i = 0; i < count; i++)
            {
                var d = new BoxEl
                {
                    Width = dot,
                    Height = dot,
                    // Before the first lap is seeded a dot waits where its lap starts (off the left edge), or stands at rest.
                    OffsetX = rest ? DotRest(width, dot, i, count) : -dot,
                    Corners = CornerRadius4.All(st.DotRadius),
                    Fill = fg,
                    OnRealized = _capture[i],
                };
                if (props.Parts is { } p)
                {
                    var m = p.Apply(PartFill, d);
                    d = m with { OnRealized = TemplateParts.Chain(_capture[i], m.OnRealized) };
                }
                kids[i] = d;
            }

            // The band is the bar's own height (the room the sweep takes). The train rides centred on it in a box a dot tall that
            // cuts it at the track's two ends only, so a dot is whole even where the band is thinner than a dot.
            var train = new BoxEl
            {
                ZStack = true,
                Width = width,
                Height = dot,
                Shrink = 0f,
                OffsetY = (st.Height - dot) / 2f,
                ClipToBounds = true,
                Children = kids,
            };
            var bar = new BoxEl
            {
                ZStack = true,
                Width = width,
                Height = st.Height,
                Role = AutomationRole.ProgressBar,
                Children = [track, train],
            };
            if (!stretch) return bar;
            return new BoxEl
            {
                Direction = 1, MinWidth = 0f, Grow = 1f, Basis = 0f, AlignSelf = FlexAlign.Stretch,
                Role = AutomationRole.None,
                Children = [bar],
            };
        }
    }

    /// <summary>The computed translate positions WinUI binds from ProgressBarTemplateSettings into the indeterminate
    /// storyboards (ProgressBar.cpp UpdateWidthBasedTemplateSettings). Indicator widths follow SetProgressBarIndicatorWidth.</summary>
    public readonly record struct ProgressBarTemplateSettings(
        float Indicator1Width, float Indicator2Width,
        float ContainerAnimationStartPosition, float ContainerAnimationEndPosition,
        float Container2AnimationStartPosition, float Container2AnimationEndPosition)
    {
        public static ProgressBarTemplateSettings For(float width)
        {
            // SetProgressBarIndicatorWidth: indicator1 = 40% width, indicator2 = 60% width.
            float w1 = width * Indicator1Frac;
            float w2 = width * Indicator2Frac;
            return new ProgressBarTemplateSettings(
                Indicator1Width: w1,
                Indicator2Width: w2,
                // UpdateWidthBasedTemplateSettings (operates on the 40%/60% indicator widths):
                ContainerAnimationStartPosition: w1 * -1.0f,    // -100% of indicator1  (= -40% width)
                ContainerAnimationEndPosition: w1 * 3.0f,       // +300% of indicator1  (= +120% width)
                Container2AnimationStartPosition: w2 * -1.5f,   // -150% of indicator2  (= -90% width)
                Container2AnimationEndPosition: w2 * 1.66f);    // +166% of indicator2  (= +99.6% width)
        }
    }

    private sealed class IndeterminateBar : Component
    {
        public override Element Render()
        {
            var props = UseProps<Props>();
            float Width = props.Width;
            var State = props.State;
            var Parts = props.Parts;
            var ts = ProgressBarTemplateSettings.For(Width);
            bool nonNormal = State != ProgressBarState.Normal;   // Paused / Error: hide track + indicator1, recolor indicator2
            var st = DefaultStyle;   // read now: a theme swap re-renders the bar in the new look
            ColorF fg = st.ForegroundFor(State);

            // Capture the two indicators so the looping translate tracks drive each independently (UseKeyframes targets the
            // component's HostNode only; per-child translate needs direct AnimEngine.Keyframes on each captured handle).
            var ind1Ref = UseRef<NodeHandle>(default);
            var ind2Ref = UseRef<NodeHandle>(default);

            // IndeterminateProgressBarIndicator storyboard (2s loop):
            //   KeyTime 0    → ContainerAnimationStartPosition          (discrete)
            //   KeyTime 1.5s → ContainerAnimationEndPosition            (spline 0.4,0,0.6,1)
            //   KeyTime 2.0s → ContainerAnimationEndPosition            (discrete hold)
            // In Paused/Error this indicator is hidden (Opacity 0), so we only drive it in the Normal state.
            // IndeterminateProgressBarIndicator2 storyboard (2s loop):
            //   KeyTime 0    → Container2AnimationStartPosition         (discrete)
            //   KeyTime 0.75s→ Container2AnimationStartPosition         (discrete hold)
            //   KeyTime 2.0s → Container2AnimationEndPosition           (spline 0.4,0,0.6,1)
            UseEffect(() =>
            {
                var anim = Context.Anim;
                var scene = Context.Scene;
                if (anim is null || scene is null) return;

                if (!ind1Ref.Value.IsNull && scene.IsLive(ind1Ref.Value))
                {
                    if (nonNormal)
                        anim.Cancel(ind1Ref.Value, AnimChannel.TranslateX);   // hidden indicator1: no sweep in Paused/Error
                    else
                        anim.Keyframes(ind1Ref.Value, AnimChannel.TranslateX, new Keyframe[]
                        {
                            new(0.00f, ts.ContainerAnimationStartPosition, Easing.Linear),
                            new(0.75f, ts.ContainerAnimationEndPosition, IndetEase),   // 1.5s of 2.0s
                            new(1.00f, ts.ContainerAnimationEndPosition, Easing.Linear),
                        }, LoopMs, loop: true, cadence: Cadence.Display);   // TRANSIENT loop: it must sweep smoothly (the default anyway; kept explicit)
                }

                if (!ind2Ref.Value.IsNull && scene.IsLive(ind2Ref.Value))
                {
                    if (nonNormal)
                    {
                        // IndeterminatePaused / IndeterminateError: NOT a sweep — settle the full-width bar STATIC over the
                        // track at ContainerAnimationMidPosition (=0). Cancel the loop, then hold TranslateX at 0 so a prior
                        // Normal-state sweep can't leave it parked mid-track.
                        anim.Cancel(ind2Ref.Value, AnimChannel.TranslateX);
                        anim.Keyframes(ind2Ref.Value, AnimChannel.TranslateX, new Keyframe[]
                        {
                            new(0.00f, ContainerAnimationMidPosition, Easing.Linear),
                            new(1.00f, ContainerAnimationMidPosition, Easing.Linear),
                        }, LoopMs, loop: true);
                    }
                    else
                    {
                        anim.Keyframes(ind2Ref.Value, AnimChannel.TranslateX, new Keyframe[]
                        {
                            new(0.000f, ts.Container2AnimationStartPosition, Easing.Linear),
                            new(0.375f, ts.Container2AnimationStartPosition, Easing.Linear),   // 0.75s hold
                            new(1.000f, ts.Container2AnimationEndPosition, IndetEase),         // → 2.0s
                        }, LoopMs, loop: true, cadence: Cadence.Display);   // TRANSIENT loop: it must sweep smoothly (the default anyway; kept explicit)
                    }
                }
            }, DepKey.From(HashCode.Combine(Width, State)));

            // Indicator2 spans the full track in Paused/Error (SetProgressBarIndicatorWidth: 100%), else 60%.
            float ind2Width = nonNormal ? Width : ts.Indicator2Width;

            Action<NodeHandle> ind1Capture = h => ind1Ref.Value = h;
            Action<NodeHandle> ind2Capture = h => ind2Ref.Value = h;

            // ProgressBarTrack — hidden (Opacity 0) in every indeterminate state in WinUI (stock state styling a
            // PartTrack modifier sees and may override).
            var track = Parts.Apply(PartTrack, new BoxEl
            {
                Width = Width,
                Height = st.TrackHeight,
                OffsetY = (st.Height - st.TrackHeight) / 2f,
                Corners = CornerRadius4.All(st.TrackRadius),
                Fill = st.TrackFill,
                Opacity = st.IndeterminateTrack ? 1f : 0f,
            });

            // IndeterminateProgressBarIndicator (40% width) — visible only in the Normal indeterminate state.
            var ind1 = new BoxEl
            {
                Width = ts.Indicator1Width,
                Height = st.Height,
                Corners = CornerRadius4.All(st.IndicatorRadius),
                Fill = fg,
                Opacity = nonNormal ? 0f : 1f,
                OnRealized = ind1Capture,
            };
            // IndeterminateProgressBarIndicator2 (60% width, or 100% in Paused/Error) — always visible indeterminate.
            var ind2 = new BoxEl
            {
                Width = ind2Width,
                Height = st.Height,
                Corners = CornerRadius4.All(st.IndicatorRadius),
                Fill = fg,
                OnRealized = ind2Capture,
            };
            if (Parts is { } p)   // the PartFill modifier runs on BOTH indicators; the sweep refs always win (chained)
            {
                var m1 = p.Apply(PartFill, ind1);
                ind1 = m1 with { OnRealized = TemplateParts.Chain(ind1Capture, m1.OnRealized) };
                var m2 = p.Apply(PartFill, ind2);
                ind2 = m2 with { OnRealized = TemplateParts.Chain(ind2Capture, m2.OnRealized) };
            }

            return new BoxEl
            {
                ZStack = true,
                Width = Width,
                Height = st.Height,
                ClipToBounds = true,                 // Border Clip="...ClipRect" — the sweep is clipped to the track bounds
                Role = AutomationRole.ProgressBar,
                Children = [track, ind1, ind2],
            };
        }
    }

    /// <summary>The <c>width: float.NaN</c> path of <see cref="Indeterminate"/>: same visual/motion contract as
    /// <see cref="IndeterminateBar"/>, but the bar's own width is read back from layout instead of taken as a
    /// fixed DIP value. A SEPARATE component (not a branch inside <see cref="IndeterminateBar"/>) so the fixed-width
    /// path stays byte-identical — no measured-width hook installed, no extra wrapper node — for every caller that
    /// doesn't opt into stretch.
    ///
    /// Shape: an outer wrapper (<c>Grow=1, Basis=0, AlignSelf=Stretch</c> — the DensityPlot "stretch to the offered
    /// width" idiom, <c>DensityPlot.cs</c>) is this component's HostNode, so <c>UseMeasuredWidth</c> reports what the
    /// PARENT actually offered; the real ZStack bar (track + two sweeping indicators, <see cref="AutomationRole.ProgressBar"/>)
    /// is its single child, drawn at that resolved width. The first frame has no measured width yet (0, sweep parked);
    /// the layout effect seeds it next frame and the bar (re-render → scoped relayout, NOT a per-frame cost) settles at
    /// the real extent. A later parent resize repeats exactly that: the quantized width signal changes, the component
    /// re-renders, and the same <c>UseEffect</c> below (keyed on Width+State, like <see cref="IndeterminateBar"/>)
    /// re-plans the translate keyframes for the new extent — the "re-arm on resize" contract.</summary>
    private sealed class StretchIndeterminateBar : Component
    {
        public override Element Render()
        {
            var props = UseProps<Props>();
            var State = props.State;
            var Parts = props.Parts;

            // The arranged width of THIS component's rendered root (the stretch wrapper below) — quantum 4 absorbs
            // sub-pixel layout jitter so a settling flex pass doesn't re-render this component every frame.
            float Width = MathF.Max(0f, UseMeasuredWidth(StretchWidthQuantum).Value);

            var ts = ProgressBarTemplateSettings.For(Width);
            bool nonNormal = State != ProgressBarState.Normal;
            var st = DefaultStyle;   // read now: a theme swap re-renders the bar in the new look
            ColorF fg = st.ForegroundFor(State);

            var ind1Ref = UseRef<NodeHandle>(default);
            var ind2Ref = UseRef<NodeHandle>(default);

            // Same storyboard as IndeterminateBar's effect (see its comments for the WinUI KeyTime breakdown) — the
            // only difference is Width comes from measurement instead of a prop, so a resize re-runs this exactly
            // like a width-prop change would. Width == 0 (not yet measured, or a collapsed parent) skips arming: no
            // point sweeping a zero-extent track, and it avoids seeding a degenerate 0-length translate keyframe.
            UseEffect(() =>
            {
                var anim = Context.Anim;
                var scene = Context.Scene;
                if (anim is null || scene is null || Width <= 0f) return;

                if (!ind1Ref.Value.IsNull && scene.IsLive(ind1Ref.Value))
                {
                    if (nonNormal)
                        anim.Cancel(ind1Ref.Value, AnimChannel.TranslateX);
                    else
                        anim.Keyframes(ind1Ref.Value, AnimChannel.TranslateX, new Keyframe[]
                        {
                            new(0.00f, ts.ContainerAnimationStartPosition, Easing.Linear),
                            new(0.75f, ts.ContainerAnimationEndPosition, IndetEase),
                            new(1.00f, ts.ContainerAnimationEndPosition, Easing.Linear),
                        }, LoopMs, loop: true, cadence: Cadence.Display);
                }

                if (!ind2Ref.Value.IsNull && scene.IsLive(ind2Ref.Value))
                {
                    if (nonNormal)
                    {
                        anim.Cancel(ind2Ref.Value, AnimChannel.TranslateX);
                        anim.Keyframes(ind2Ref.Value, AnimChannel.TranslateX, new Keyframe[]
                        {
                            new(0.00f, ContainerAnimationMidPosition, Easing.Linear),
                            new(1.00f, ContainerAnimationMidPosition, Easing.Linear),
                        }, LoopMs, loop: true);
                    }
                    else
                    {
                        anim.Keyframes(ind2Ref.Value, AnimChannel.TranslateX, new Keyframe[]
                        {
                            new(0.000f, ts.Container2AnimationStartPosition, Easing.Linear),
                            new(0.375f, ts.Container2AnimationStartPosition, Easing.Linear),
                            new(1.000f, ts.Container2AnimationEndPosition, IndetEase),
                        }, LoopMs, loop: true, cadence: Cadence.Display);
                    }
                }
            }, DepKey.From(HashCode.Combine(Width, State)));

            float ind2Width = nonNormal ? Width : ts.Indicator2Width;

            Action<NodeHandle> ind1Capture = h => ind1Ref.Value = h;
            Action<NodeHandle> ind2Capture = h => ind2Ref.Value = h;

            var track = Parts.Apply(PartTrack, new BoxEl
            {
                Width = Width,
                Height = st.TrackHeight,
                OffsetY = (st.Height - st.TrackHeight) / 2f,
                Corners = CornerRadius4.All(st.TrackRadius),
                Fill = st.TrackFill,
                Opacity = st.IndeterminateTrack ? 1f : 0f,
            });

            var ind1 = new BoxEl
            {
                Width = ts.Indicator1Width,
                Height = st.Height,
                Corners = CornerRadius4.All(st.IndicatorRadius),
                Fill = fg,
                Opacity = nonNormal ? 0f : 1f,
                OnRealized = ind1Capture,
            };
            var ind2 = new BoxEl
            {
                Width = ind2Width,
                Height = st.Height,
                Corners = CornerRadius4.All(st.IndicatorRadius),
                Fill = fg,
                OnRealized = ind2Capture,
            };
            if (Parts is { } p)
            {
                var m1 = p.Apply(PartFill, ind1);
                ind1 = m1 with { OnRealized = TemplateParts.Chain(ind1Capture, m1.OnRealized) };
                var m2 = p.Apply(PartFill, ind2);
                ind2 = m2 with { OnRealized = TemplateParts.Chain(ind2Capture, m2.OnRealized) };
            }

            var bar = new BoxEl
            {
                ZStack = true,
                Width = Width,
                Height = st.Height,
                ClipToBounds = true,
                Role = AutomationRole.ProgressBar,
                Children = [track, ind1, ind2],
            };

            // Stretch wrapper (this component's HostNode — what UseMeasuredWidth reports): Grow=1/Basis=0 claims the
            // full main-axis span if the parent is a row, AlignSelf=Stretch claims the full cross-axis span if the
            // parent is a column — the same two-axis idiom DensityPlot uses for "stretch to the width the parent
            // offers" regardless of which kind of parent it's dropped into.
            return new BoxEl
            {
                Direction = 1, MinWidth = 0f, Grow = 1f, Basis = 0f, AlignSelf = FlexAlign.Stretch,
                Role = AutomationRole.None,
                Children = [bar],
            };
        }
    }
}
