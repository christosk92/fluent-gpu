using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Lottie;

namespace FluentGpu.Controls;

/// <summary>Playback configuration for a mounted <see cref="LottieView"/> instance — a frozen, per-mount
/// (component-props) value: change it via a <c>Key</c> remount, not a live signal (see the type doc on
/// <see cref="LottieView.Create"/>).</summary>
public readonly record struct LottieOptions
{
    /// <summary>Play the <see cref="From"/>..<see cref="To"/> window forever. False = play it once, then hold the
    /// last frame (the engine's own <c>loop:false</c> semantics — the composed value simply stops being overwritten
    /// once the track settles, so the LAST COMPOSED VALUE is the hold, not a special "hold" mechanism).</summary>
    public bool Loop { get; init; }
    /// <summary>False = mount showing the static <see cref="From"/> pose forever — no track is ever wired (used for
    /// a paused/poster frame, or as the reduced-motion SnapEnd terminal via <see cref="ReducedMotion"/> instead).</summary>
    public bool AutoPlay { get; init; }
    /// <summary>Normalized 0..1 remap window of the source timeline actually played (§4/§5): <c>Offset' =
    /// (Offset−From)/(To−From)</c>, duration scales by <c>(To−From)</c>.</summary>
    public float From { get; init; }
    public float To { get; init; }
    public ReducedMotionPolicy ReducedMotion { get; init; }
    /// <summary>Applied ONCE at mount to every fill/stroke/gradient-mid-stop color (RGB only — alpha is kept as
    /// authored). Null = no recolor.</summary>
    public Func<ColorF, ColorF>? Recolor { get; init; }
    /// <summary>Extra uniform scale about the centre of the box, still clipped to it (1 = the plain fit; 0 reads as 1).
    /// Authored scenes often carry a wide empty margin inside their own canvas (the Windows OOBE exports keep ~25%), so
    /// a host that wants the ART, not the canvas, to fill its box zooms in rather than growing the box.</summary>
    public float Zoom { get; init; }

    public static LottieOptions Default => new() { Loop = true, AutoPlay = true, From = 0f, To = 1f, Zoom = 1f };

    /// <summary>Rise Media Player's setup behaviour: the first half of the timeline, once, then hold
    /// (<c>AnimatedVisualPlayer.PlayAsync(0, 0.5, looped:false)</c>).</summary>
    public static LottieOptions RiseSetup => Default with { Loop = false, To = 0.5f };
}

/// <summary>
/// A Lottie/Bodymovin animation, compiled once per <see cref="LottieSource"/> (<see cref="LottieSource.Plan"/>) and
/// mounted as an ordinary compositor-driven element subtree (<c>BoxEl</c> groups + <c>PathEl</c> leaves +
/// <c>AnimEngine.Keyframes</c> tracks) — no renderer/scene change, no per-frame geometry (engine rule 9: every
/// <c>PathData</c> a Lottie asset needs is minted once at compile, cached on the source).
/// <para><b>Props freeze at mount</b> (component-props-contract.md): <see cref="LottieOptions"/> is read once when
/// <see cref="Create"/>'s factory runs — to change <c>Loop</c>/<c>From</c>/<c>To</c>/recolor live, remount via a
/// changed <c>Key</c> (the wizard's pattern: it remounts the hero per setup page anyway).</para>
/// </summary>
public static class LottieView
{
    /// <summary>Mount a Lottie animation at <paramref name="size"/>×<paramref name="size"/> DIP (uniform fit,
    /// centred, clipped). <paramref name="options"/> defaults to <see cref="LottieOptions.Default"/> (loop the whole
    /// timeline, autoplay).</summary>
    public static Element Create(LottieSource source, float size, LottieOptions? options = null)
        => Embed.Comp(() => new LottieViewComponent(source, size, options ?? LottieOptions.Default));

    /// <summary>Warm a source's compiled <see cref="LottiePlan"/> off the UI thread (parse + compile is a pure,
    /// thread-safe computation — see <see cref="LottieSource"/>). ~1-2ms for a ~50KB asset; a mount-time nicety, not
    /// a requirement (a cold <see cref="Create"/> compiles inline on first use regardless).</summary>
    public static Task Preload(LottieSource source) => Task.Run(() => { _ = source.Plan; });
}

/// <summary>The component behind <see cref="LottieView.Create"/>. One <see cref="Action{NodeHandle}"/> capture
/// closure per tracked node is built ONCE in the constructor (never per render — engine rule 9) so
/// <see cref="Element.OnRealized"/>/<c>PathEl.OnRealized</c> never allocates on a re-render (this component never
/// re-renders anyway: it reads no signal, so <c>Render()</c> runs exactly once per the "run-once inferred" rule).</summary>
internal sealed class LottieViewComponent : Component
{
    private readonly LottiePlan _plan;
    private readonly float _size;
    private readonly LottieOptions _o;
    private readonly NodeHandle[] _handles;
    private readonly Action<NodeHandle>[] _capture;
    private readonly List<int>[] _childrenOf;
    private readonly LottiePose _restPose;
    private readonly float _fit;

    public LottieViewComponent(LottieSource source, float size, LottieOptions options)
    {
        _plan = source.Plan;
        _size = size;
        _o = options;

        int n = _plan.Nodes.Length;
        _handles = new NodeHandle[n];
        _capture = new Action<NodeHandle>[n];
        for (int i = 0; i < n; i++)
        {
            int idx = i;   // one closure per node, built once here — never inside Render/BuildElement re-entry
            _capture[i] = h => _handles[idx] = h;
        }

        _childrenOf = new List<int>[n];
        for (int i = 0; i < n; i++) _childrenOf[i] = new List<int>(2);
        for (int i = 0; i < n; i++)
        {
            int p = _plan.Nodes[i].Parent;
            if (p >= 0) _childrenOf[p].Add(i);
        }

        // The rest pose is what shows before the slab first ticks (and forever, when nothing gets wired below):
        // SnapEnd reduced motion jumps straight to the terminal frame; everything else starts at From.
        _restPose = _plan.SampleAt(ClampU(IsReducedSnapAll() ? _o.To : _o.From));
        _fit = _plan.Width > 0f && _plan.Height > 0f ? MathF.Min(size / _plan.Width, size / _plan.Height) : 1f;
        if (_o.Zoom > 0f) _fit *= _o.Zoom;   // zoom about the centre: the offset math below centres the scaled canvas either way
    }

    // Reduced motion is resolved as a VALUE at mount (never a hook branch — Motion.ReducedMotion is a mutable
    // global the OS-follow flips; the wizard remounts its hero per page anyway, so a mount-time read is correct).
    private bool IsReducedSnapAll() => FluentGpu.Dsl.Motion.ReducedMotion && _o.ReducedMotion == ReducedMotionPolicy.SnapEnd;
    private bool ReducedSnapsChannel(AnimChannel ch)
        => FluentGpu.Dsl.Motion.ReducedMotion
           && _o.ReducedMotion != ReducedMotionPolicy.Exempt
           && !(_o.ReducedMotion == ReducedMotionPolicy.KeepFade && ch == AnimChannel.Opacity);

    private float ClampU(float u) => u < 0f ? 0f : u > 1f ? 1f : u;

    public override Element Render()
    {
        UseLayoutEffect(() =>
        {
            if (!_o.AutoPlay) return;   // static pose only — the rest pose sampled at From (or To, for SnapEnd) in the ctor
            if (Context.Anim is not { } anim || Context.Scene is not { } scene) return;

            float span = MathF.Max(1e-4f, _o.To - _o.From);
            float durationMs = _plan.DurationMs * span;
            bool loop = _o.Loop;

            for (int i = 0; i < _plan.Nodes.Length; i++)
            {
                NodeHandle h = _handles[i];
                if (h.IsNull || !scene.IsLive(h)) continue;
                LottieTrack[] tracks = _plan.Nodes[i].Tracks;
                for (int t = 0; t < tracks.Length; t++)
                {
                    LottieTrack track = tracks[t];
                    if (ReducedSnapsChannel(track.Channel)) continue;   // stays at the ctor's rest-pose baseline
                    // CADENCE: a Bodymovin composition HAS a native frame rate (the document's "fr"), and that is
                    // exactly what Cadence.At(fps) is for — but the compiler folds "fr" into LottiePlan.DurationMs
                    // and does not carry it onto the plan, so there is nothing to read here. Left at the default
                    // (display rate, one-shot or loop). Surface LottiePlan.FrameRate in
                    // LottieCompiler/LottiePlan and this becomes `cadence: Cadence.At(_plan.FrameRate)`.
                    anim.Keyframes(h, track.Channel, RemapKeys(track.Keys, span), durationMs, loop: loop);
                }
            }
        });

        return BuildRoot();
    }

    private Keyframe[] RemapKeys(Keyframe[] src, float span)
    {
        if (_o.From == 0f && _o.To == 1f) return src;
        var dst = new Keyframe[src.Length];
        for (int i = 0; i < src.Length; i++)
            dst[i] = new Keyframe((src[i].Offset - _o.From) / span, src[i].Value, src[i].Easing);
        return dst;
    }

    private Element BuildRoot()
    {
        Element inner = BuildElement(0);
        if (inner is BoxEl box)
        {
            float leftoverX = _size - _plan.Width * _fit;
            float leftoverY = _size - _plan.Height * _fit;
            inner = box with { ScaleX = _fit, ScaleY = _fit, OffsetX = leftoverX / 2f, OffsetY = leftoverY / 2f };
        }
        return new BoxEl { ZStack = true, Width = _size, Height = _size, ClipToBounds = true, Children = [inner] };
    }

    private Element BuildElement(int i)
    {
        LottieNode node = _plan.Nodes[i];
        float offX = _restPose.OffsetX[i], offY = _restPose.OffsetY[i];
        float scaleX = _restPose.ScaleX[i], scaleY = _restPose.ScaleY[i];
        float rotation = _restPose.Rotation[i], opacity = _restPose.Opacity[i];

        if (node.Kind == LottieNodeKind.Group)
        {
            List<int> kids = _childrenOf[i];
            var children = new Element[kids.Count];
            for (int k = 0; k < kids.Count; k++) children[k] = BuildElement(kids[k]);
            return new BoxEl
            {
                ZStack = true,
                Width = node.W,
                Height = node.H,
                ClipToBounds = node.Clip,
                OffsetX = offX,
                OffsetY = offY,
                ScaleX = scaleX,
                ScaleY = scaleY,
                Rotation = rotation,
                Opacity = opacity,
                TransformOriginX = node.OriginX,
                TransformOriginY = node.OriginY,
                Children = children,
                OnRealized = _capture[i],
            };
        }

        bool isStroke = node.Kind == LottieNodeKind.StrokePath;
        ColorF color = _o.Recolor is { } recolor ? recolor(node.Color) : node.Color;
        return new PathEl
        {
            Width = node.W,
            Height = node.H,
            Geometry = node.Geometry,
            Fill = isStroke ? default : color,
            Rule = node.Rule,
            StrokeColor = isStroke ? color : default,
            Stroke = node.Stroke,
            TrimStart = _restPose.TrimStart[i],
            TrimEnd = _restPose.TrimEnd[i],
            TrimMode = node.TrimMode,
            OffsetX = offX,
            OffsetY = offY,
            ScaleX = scaleX,
            ScaleY = scaleY,
            Rotation = rotation,
            Opacity = opacity,
            TransformOriginX = node.OriginX,
            TransformOriginY = node.OriginY,
            OnRealized = _capture[i],
        };
    }
}
