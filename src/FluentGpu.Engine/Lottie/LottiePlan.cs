using FluentGpu.Animation;
using FluentGpu.Foundation;

namespace FluentGpu.Lottie;

/// <summary>What kind of engine element a compiled <see cref="LottieNode"/> becomes (§4 "Layer → boxes" /
/// "Shapes"): a transform/clip/vis <c>BoxEl</c> group, or a leaf paint (<c>PathEl</c> fill or stroke).</summary>
public enum LottieNodeKind : byte { Group, FillPath, StrokePath }

/// <summary>One compiled animation track on a <see cref="LottieNode"/>: the engine channel it drives plus the
/// already-time-mapped, already-normalized (Offset in 0..1 of <see cref="LottiePlan.DurationMs"/>) keyframe array —
/// exactly the shape <c>AnimEngine.Keyframes</c> takes, so <see cref="LottieView"/> wires it verbatim.</summary>
public readonly record struct LottieTrack(AnimChannel Channel, Keyframe[] Keys);

/// <summary>One compiled scene node — a pre-order flattening of the Lottie layer/shape tree (§4). <see cref="Parent"/>
/// is always &lt; this node's own index (node 0 is the root, parent -1); a reader can rebuild the tree with one
/// forward pass. All pose fields (<see cref="OffsetX"/>.. <see cref="TrimEnd"/>) are the REST/baseline value for a
/// channel that has NO <see cref="Tracks"/> entry — when a channel IS tracked, its baseline is never read directly
/// (see <see cref="LottiePlan.SampleAt"/>): the track always wins.</summary>
public sealed class LottieNode
{
    public LottieNodeKind Kind;
    public int Parent = -1;
    public string Name = "";

    // Group only: box size + clip.
    public float W, H;
    public bool Clip;

    // Rest-pose transform/paint (baseline for any channel without a Track — see type doc).
    public float OffsetX, OffsetY;
    public float ScaleX = 1f, ScaleY = 1f;
    public float Rotation;
    public float Opacity = 1f;
    public float OriginX = 0.5f, OriginY = 0.5f;

    // FillPath/StrokePath only.
    public PathData? Geometry;
    public ColorF Color;
    public FillRule Rule = FillRule.NonZero;
    public StrokeStyle Stroke;
    public float TrimStart;
    public float TrimEnd = 1f;
    public byte TrimMode;

    public LottieTrack[] Tracks = [];
}

/// <summary>A fully-evaluated snapshot of every node's pose at one normalized time <c>u</c> — what
/// <see cref="LottiePlan.SampleAt"/> returns, parallel to <see cref="LottiePlan.Nodes"/>. Used for the very first
/// frame (before the slab has ticked), <c>AutoPlay=false</c>, and reduced motion.</summary>
public sealed class LottiePose
{
    public float[] OffsetX = [], OffsetY = [];
    public float[] ScaleX = [], ScaleY = [];
    public float[] Rotation = [];
    public float[] Opacity = [];
    public float[] TrimStart = [], TrimEnd = [];
}

/// <summary>
/// The compiled, immutable output of <see cref="LottieCompiler.Compile"/>: a flat pre-order node list (engine
/// element shape) plus their animation tracks, cached per <see cref="LottieSource"/>. Never mutated after compile —
/// <see cref="LottieView"/> mounts it read-only, minting each node's <see cref="PathData"/> content epoch exactly
/// once (no per-frame geometry).
/// </summary>
public sealed class LottiePlan
{
    public float DurationMs;
    public float Width;
    public float Height;
    /// <summary>Pre-order; <see cref="LottieNode.Parent"/> &lt; its own index; node 0 is the 552x552 root group.</summary>
    public LottieNode[] Nodes = [];

    public int DroppedLayers;
    public int Approximations;
    /// <summary>Distinct <see cref="PathData"/> instances minted for this plan (one per paint's baked geometry, or
    /// one per animated-bezier switch sample) — the informational per-asset geometry count.</summary>
    public int GeometryCount;

    /// <summary>Evaluate every node's channels at normalized time <paramref name="u"/> (0..1, clamped): a tracked
    /// channel is sampled from its <see cref="LottieTrack.Keys"/> (per-segment easing, Hold-aware — the identical
    /// math <c>AnimEngine</c> uses so a static sample and a live tick agree at the same <c>u</c>); an untracked
    /// channel keeps its node's rest-pose baseline. Allocates one <see cref="LottiePose"/> (float arrays sized to
    /// <see cref="Nodes"/>) — a cold-path convenience (mount / reduced-motion / preview), never called per frame.</summary>
    public LottiePose SampleAt(float u)
    {
        u = u < 0f ? 0f : u > 1f ? 1f : u;
        int n = Nodes.Length;
        var pose = new LottiePose
        {
            OffsetX = new float[n], OffsetY = new float[n],
            ScaleX = new float[n], ScaleY = new float[n],
            Rotation = new float[n], Opacity = new float[n],
            TrimStart = new float[n], TrimEnd = new float[n],
        };
        for (int i = 0; i < n; i++)
        {
            LottieNode node = Nodes[i];
            float offX = node.OffsetX, offY = node.OffsetY;
            float scaleX = node.ScaleX, scaleY = node.ScaleY;
            float rotation = node.Rotation, opacity = node.Opacity;
            float trimStart = node.TrimStart, trimEnd = node.TrimEnd;

            foreach (LottieTrack track in node.Tracks)
            {
                float v = SampleTrack(track.Keys, u);
                switch (track.Channel)
                {
                    case AnimChannel.TranslateX: offX = v; break;
                    case AnimChannel.TranslateY: offY = v; break;
                    case AnimChannel.ScaleX: scaleX = v; break;
                    case AnimChannel.ScaleY: scaleY = v; break;
                    case AnimChannel.Rotation: rotation = v; break;
                    case AnimChannel.Opacity: opacity = v; break;
                    case AnimChannel.StrokeTrimStart: trimStart = v; break;
                    case AnimChannel.StrokeTrimEnd: trimEnd = v; break;
                }
            }

            pose.OffsetX[i] = offX; pose.OffsetY[i] = offY;
            pose.ScaleX[i] = scaleX; pose.ScaleY[i] = scaleY;
            pose.Rotation[i] = rotation; pose.Opacity[i] = opacity;
            pose.TrimStart[i] = trimStart; pose.TrimEnd[i] = trimEnd;
        }
        return pose;
    }

    /// <summary>Sample a multi-keyframe track at progress u (0..1), per-segment easing — the same semantics as
    /// <c>AnimEngine</c>'s internal timeline sampler (offsets ascending, easing on the arriving key), duplicated here
    /// (not shared) because that sampler is a private implementation detail of the slab scheduler, not a public
    /// utility; both read the identical <see cref="Keyframe"/> shape so they can never disagree in practice.</summary>
    internal static float SampleTrack(Keyframe[] keys, float u)
    {
        if (keys.Length == 0) return 0f;
        if (keys.Length == 1 || u <= keys[0].Offset) return keys[0].Value;
        if (u >= keys[^1].Offset) return keys[^1].Value;
        int i = 0;
        while (i < keys.Length - 1 && keys[i + 1].Offset < u) i++;
        Keyframe a = keys[i], b = keys[i + 1];
        float span = b.Offset - a.Offset;
        float local = span <= 0f ? 1f : (u - a.Offset) / span;
        return a.Value + (b.Value - a.Value) * Easings.Ease(b.Easing, local);
    }
}
