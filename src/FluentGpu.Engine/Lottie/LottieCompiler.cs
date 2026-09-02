using System;
using System.Collections.Generic;
using System.Linq;
using FluentGpu.Animation;
using FluentGpu.Foundation;

namespace FluentGpu.Lottie;

/// <summary>
/// <see cref="LottieDocument"/> → <see cref="LottiePlan"/> (§4): walks the layer tree (root layers, recursively
/// expanding precomps via <see cref="LottieDocument.Precomps"/>), maps every keyframe's layer-local frame number
/// into the plan's single normalized 0..1 timeline (<see cref="ToGlobalFrame"/> + <see cref="U"/>), applies the drop
/// rules, and for every surviving Shape layer calls <see cref="LottieGeometry"/> to bake its paint geometry. One
/// instance per <see cref="Compile"/> call — never shared, never touches anything UI-thread-affine (a fresh
/// <see cref="LottieGeometry"/>, itself a fresh <c>PathBuilder</c>, per compile) so <c>LottieSource.Plan</c> can run
/// this entirely off the UI thread.
/// </summary>
public static class LottieCompiler
{
    public static LottiePlan Compile(LottieDocument doc)
    {
        var c = new Compiler(doc);
        c.Run();
        return new LottiePlan
        {
            DurationMs = (doc.OutPoint - doc.InPoint) / MathF.Max(1f, doc.FrameRate) * 1000f,
            Width = doc.Width,
            Height = doc.Height,
            Nodes = c.Nodes.ToArray(),
            DroppedLayers = c.DroppedLayers,
            Approximations = c.Approximations + doc.ParseApproximations + c.Geometry.Approximations,
            GeometryCount = c.Geometry.GeometryCount,
        };
    }

    // Names whose presence (ordinal, case-insensitive) marks a layer as decorative (§4 drop table) — a manual
    // substring scan, not System.Text.RegularExpressions: RegexOptions.Compiled emits IL at runtime (Reflection.Emit),
    // which NativeAOT/PublishAot cannot do, and the plan's own "|"-separated list is just alternation over literals.
    private static readonly string[] DropNameNeedles = ["_emb", "_shdw", "shdw", "emb_msk", "Emboss", "Shadow"];

    private sealed class Compiler
    {
        private readonly LottieDocument _doc;
        public readonly List<LottieNode> Nodes = new(64);
        public readonly LottieGeometry Geometry;
        public int DroppedLayers;
        public int Approximations;

        public Compiler(LottieDocument doc)
        {
            _doc = doc;
            Geometry = new LottieGeometry(doc.FrameRate);
        }

        public void Run()
        {
            int rootIdx = AppendNode(new LottieNode
            {
                Kind = LottieNodeKind.Group,
                Parent = -1,
                Name = "root",
                W = _doc.Width,
                H = _doc.Height,
                OriginX = 0f,
                OriginY = 0f,
            });
            CompileArray(_doc.Layers, rootIdx, _doc.Width, _doc.Height, 0f);
        }

        private int AppendNode(LottieNode node)
        {
            Nodes.Add(node);
            return Nodes.Count - 1;
        }

        // ── layer-array walk (root list, or one precomp's own layer list) ───────────────────────────────────────

        private void CompileArray(LottieLayer[] layers, int parentIdx, float canvasW, float canvasH, float ancestorStSum)
        {
            var childrenOf = new Dictionary<int, List<LottieLayer>>();
            foreach (LottieLayer l in layers)
            {
                int key = l.Parent;
                if (!childrenOf.TryGetValue(key, out var list)) childrenOf[key] = list = new List<LottieLayer>(2);
                list.Add(l);
            }
            CompileSiblings(-1, layers, childrenOf, parentIdx, canvasW, canvasH, ancestorStSum);
        }

        private void CompileSiblings(int parentInd, LottieLayer[] layers, Dictionary<int, List<LottieLayer>> childrenOf,
            int parentIdx, float canvasW, float canvasH, float ancestorStSum)
        {
            if (!childrenOf.TryGetValue(parentInd, out var kids)) return;
            // §4 "siblings bottom→top (descending ind)" — AE paints ind=1 on top; our Children order is
            // painter-order (last = on top), so process HIGHEST ind first (painted first / at the bottom).
            foreach (LottieLayer layer in kids.OrderByDescending(l => l.Index))
                CompileLayer(layer, layers, childrenOf, parentIdx, canvasW, canvasH, ancestorStSum);
        }

        private void CompileLayer(LottieLayer layer, LottieLayer[] siblingArray, Dictionary<int, List<LottieLayer>> childrenOf,
            int parentIdx, float canvasW, float canvasH, float ancestorStSum)
        {
            bool dropped = IsDropped(layer);
            if (dropped) DroppedLayers++;
            bool hasKids = childrenOf.ContainsKey(layer.Index);
            if (dropped && !hasKids) return;   // fully omitted — no scaffold node needed for a childless decoration

            float boxW = layer.Type == LottieLayerType.Precomp && layer.Width > 0f ? layer.Width : canvasW;
            float boxH = layer.Type == LottieLayerType.Precomp && layer.Height > 0f ? layer.Height : canvasH;

            int xformIdx = EmitLayerWithVis(layer, parentIdx, boxW, boxH, ancestorStSum);

            if (!dropped)
            {
                if (layer.Type == LottieLayerType.Shape)
                {
                    EmitPaintChildren(layer, xformIdx, boxW, boxH, ancestorStSum);
                }
                else if (layer.Type == LottieLayerType.Precomp && layer.RefId is { } refId && _doc.Precomps.TryGetValue(refId, out var childLayers))
                {
                    CompileArray(childLayers, xformIdx, boxW, boxH, ancestorStSum + layer.StartTime);
                }
            }

            // This layer's OWN same-array children share ITS canvas (not boxW/boxH, which only differs for a
            // precomp's OWN referenced content) — they were authored in the SAME containing comp as this layer.
            CompileSiblings(layer.Index, siblingArray, childrenOf, xformIdx, canvasW, canvasH, ancestorStSum);
        }

        private static bool IsDropped(LottieLayer l)
            => l.IsMatteSource || l.HasTrackMatte || l.Hidden || l.HasDropEffect
               || l.Type is LottieLayerType.Solid or LottieLayerType.Image or LottieLayerType.Text or LottieLayerType.Unknown
               || (!string.IsNullOrEmpty(l.Name) && NameMatchesDrop(l.Name));

        private static bool NameMatchesDrop(string name)
        {
            foreach (string needle in DropNameNeedles)
                if (name.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ── one layer: optional vis-box wrapper + its own transform node ────────────────────────────────────────

        private int EmitLayerWithVis(LottieLayer layer, int parentIdx, float boxW, float boxH, float ancestorStSum)
        {
            float globalIp = ToGlobalFrame(layer.InPoint, layer, ancestorStSum);
            float globalOp = ToGlobalFrame(layer.OutPoint, layer, ancestorStSum);
            bool needsVis = globalIp > _doc.InPoint + 0.5f || globalOp < _doc.OutPoint - 0.5f;
            int attachParent = parentIdx;

            if (needsVis)
            {
                float uIp = Math.Clamp(U(globalIp), 0f, 1f);
                float uOp = Math.Clamp(U(globalOp), 0f, 1f);
                bool visibleAtStart = globalIp <= _doc.InPoint + 0.5f;
                var keys = new List<Keyframe>(3);
                if (!visibleAtStart) keys.Add(new Keyframe(0f, 0f));
                keys.Add(new Keyframe(uIp, 1f, Easing.Hold));
                if (uOp < 1f - 1e-4f) keys.Add(new Keyframe(uOp, 0f, Easing.Hold));

                int visIdx = AppendNode(new LottieNode
                {
                    Kind = LottieNodeKind.Group,
                    Parent = parentIdx,
                    Name = layer.Name + " (vis)",
                    W = boxW,
                    H = boxH,
                    Opacity = visibleAtStart ? 1f : 0f,
                    Tracks = [new LottieTrack(AnimChannel.Opacity, keys.ToArray())],
                });
                attachParent = visIdx;
            }

            return EmitXformNode(layer, attachParent, boxW, boxH, ancestorStSum);
        }

        private int EmitXformNode(LottieLayer layer, int parentIdx, float boxW, float boxH, float ancestorStSum)
        {
            LottieTransform tr = layer.Transform;
            Point2 anchor = LottieGeometry.StaticOf(tr.Anchor);
            Point2 position = tr.PositionX is not null
                ? new Point2(LottieGeometry.StaticOf(tr.PositionX), LottieGeometry.StaticOf(tr.PositionY!))
                : LottieGeometry.StaticOf(tr.Position);
            Point2 scale = LottieGeometry.StaticOf(tr.Scale);
            float rotation = LottieGeometry.StaticOf(tr.Rotation);
            // A Lottie NULL layer is never itself rendered — After Effects never composites a Null's own "o"
            // opacity into anything (it's a pure transform/parenting node; AE leaves the field at whatever value
            // an export happened to carry, including 0 — verified against the shipped assets: Eula's "Null 76",
            // the transform ancestor of nearly the whole tree, carries a static o:0). Our engine's Group opacity
            // multiplies down the visual tree like any nested-opacity compositor (Element.cs "per-node multiplied
            // opacity"), so applying that field here would blank out every descendant — exactly the "hero rail is
            // blank" bug. Force it inert for Null layers; every other type (Shape/Precomp) keeps real semantics.
            bool opacityApplies = layer.Type != LottieLayerType.Null;
            float opacity = opacityApplies ? LottieGeometry.StaticOf(tr.Opacity) / 100f : 1f;

            if (tr.Anchor.IsAnimated) Approximations++;          // static first-key anchor only (§4)
            if (tr.HasUnsupportedSkewOrAxis) Approximations++;   // skew/3D-axis dropped (§3 ReadTransform)

            var node = new LottieNode
            {
                Kind = LottieNodeKind.Group,
                Parent = parentIdx,
                Name = layer.Name,
                W = boxW,
                H = boxH,
                Clip = layer.Type == LottieLayerType.Precomp,
                OffsetX = position.X - anchor.X,
                OffsetY = position.Y - anchor.Y,
                ScaleX = scale.X / 100f,
                ScaleY = scale.Y / 100f,
                Rotation = rotation,
                Opacity = Math.Clamp(opacity, 0f, 1f),
                OriginX = boxW > 0f ? anchor.X / boxW : 0.5f,
                OriginY = boxH > 0f ? anchor.Y / boxH : 0.5f,
            };

            var tracks = new List<LottieTrack>(4);
            if (tr.PositionX is { IsAnimated: true } px)
                tracks.Add(BuildScalarTrack(px, AnimChannel.TranslateX, layer, ancestorStSum, v => v - anchor.X));
            if (tr.PositionY is { IsAnimated: true } py)
                tracks.Add(BuildScalarTrack(py, AnimChannel.TranslateY, layer, ancestorStSum, v => v - anchor.Y));
            if (tr.PositionX is null && tr.Position.IsAnimated)
            {
                // Spatial bezier tangents (to/ti) are NOT arc-length sub-sampled (a deliberate scope reduction from
                // the plan's 32-entry-LUT description — none of the shipped assets' GATES depend on it): every
                // animated position track falls back to a plain per-axis eased interpolation between keys, same as
                // a non-spatial track, and counts one approximation when a segment actually carries a tangent.
                if (tr.Position.Keys!.Any(k => k.To != default || k.Ti != default)) Approximations++;
                tracks.Add(BuildVec2AxisTrack(tr.Position, isX: true, AnimChannel.TranslateX, layer, ancestorStSum, v => v - anchor.X));
                tracks.Add(BuildVec2AxisTrack(tr.Position, isX: false, AnimChannel.TranslateY, layer, ancestorStSum, v => v - anchor.Y));
            }
            if (tr.Scale.IsAnimated)
            {
                tracks.Add(BuildVec2AxisTrack(tr.Scale, isX: true, AnimChannel.ScaleX, layer, ancestorStSum, v => v / 100f));
                tracks.Add(BuildVec2AxisTrack(tr.Scale, isX: false, AnimChannel.ScaleY, layer, ancestorStSum, v => v / 100f));
            }
            if (tr.Rotation.IsAnimated)
                tracks.Add(BuildScalarTrack(tr.Rotation, AnimChannel.Rotation, layer, ancestorStSum, v => v));
            if (opacityApplies && tr.Opacity.IsAnimated)
                tracks.Add(BuildScalarTrack(tr.Opacity, AnimChannel.Opacity, layer, ancestorStSum, v => Math.Clamp(v / 100f, 0f, 1f)));

            node.Tracks = tracks.ToArray();
            return AppendNode(node);
        }

        // ── Shape-layer paint children ───────────────────────────────────────────────────────────────────────────

        private void EmitPaintChildren(LottieLayer layer, int parentIdx, float boxW, float boxH, float ancestorStSum)
        {
            List<LottiePaintDescriptor> paints = Geometry.Build(layer.Shapes);
            foreach (LottiePaintDescriptor desc in paints)
            {
                if (desc.SwitchGeometries is { } geoms)
                {
                    for (int i = 0; i < geoms.Length; i++)
                    {
                        var node = MakePaintNode(desc, geoms[i], boxW, boxH, parentIdx);
                        node.Opacity = i == 0 ? 1f : 0f;
                        node.Tracks = [BuildSwitchOpacityTrack(desc.SwitchFrames!, i, layer, ancestorStSum)];
                        AppendNode(node);
                    }
                    if (desc.OpacityTrack is not null) Approximations++;   // channel conflict with the switch — not exercised by the shipped assets
                }
                else
                {
                    var node = MakePaintNode(desc, desc.Geometry, boxW, boxH, parentIdx);
                    var tracks = new List<LottieTrack>(2);
                    if (desc.OpacityTrack is { } ot)
                        tracks.Add(BuildScalarTrack(ot, AnimChannel.Opacity, layer, ancestorStSum, v => Math.Clamp(v / 100f, 0f, 1f)));
                    if (desc.WidthTrack is { } wt)
                    {
                        float last = wt.Keys![^1].Value;
                        float denom = MathF.Abs(last) > 1e-4f ? last : 1f;
                        tracks.Add(BuildScalarTrack(wt, AnimChannel.Opacity, layer, ancestorStSum, v => Math.Clamp(v / denom, 0f, 1f)));
                    }
                    if (desc.TrimStartTrack is { } tst)
                        tracks.Add(BuildScalarTrack(tst, AnimChannel.StrokeTrimStart, layer, ancestorStSum, v => v / 100f));
                    if (desc.TrimEndTrack is { } tet)
                        tracks.Add(BuildScalarTrack(tet, AnimChannel.StrokeTrimEnd, layer, ancestorStSum, v => v / 100f));
                    node.Tracks = tracks.ToArray();
                    AppendNode(node);
                }
            }
        }

        private static LottieNode MakePaintNode(LottiePaintDescriptor desc, PathData? geometry, float boxW, float boxH, int parentIdx)
            => new()
            {
                Kind = desc.IsStroke ? LottieNodeKind.StrokePath : LottieNodeKind.FillPath,
                Parent = parentIdx,
                Name = desc.Name,
                W = boxW,
                H = boxH,
                Geometry = geometry,
                Color = desc.Color,
                Rule = desc.Rule,
                Opacity = desc.StaticOpacity,
                Stroke = desc.IsStroke ? new StrokeStyle(desc.StaticWidth, desc.Cap, desc.Join) : default,
                TrimStart = desc.HasTrim ? desc.StaticTrimStart : 0f,
                TrimEnd = desc.HasTrim ? desc.StaticTrimEnd : 1f,
                TrimMode = desc.TrimMode,
            };

        // ── keyframe track builders ──────────────────────────────────────────────────────────────────────────────

        // NOTE the ease-index shift: Lottie stores a key's "i"/"o" (and "h") on the DEPARTING key — ScalarKey/
        // Vec2Key/PathKey.Ease is documented "segment THIS -> next" — while an engine Keyframe's Easing is "the
        // segment leading INTO this key" (the arriving key). So engine Keyframe[i] takes source key[i-1]'s Ease;
        // Keyframe[0] has no incoming segment and gets the default (AnimEngine.Sample never reads it anyway).
        private LottieTrack BuildScalarTrack(AnimScalar scalar, AnimChannel channel, LottieLayer layer, float ancestorStSum, Func<float, float> map)
        {
            ScalarKey[] src = scalar.Keys!;
            var keys = new Keyframe[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                float u = Math.Clamp(U(ToGlobalFrame(src[i].T, layer, ancestorStSum)), 0f, 1f);
                EasingSpec ease = i == 0 ? EasingSpec.Default : src[i - 1].Ease.ToSpec();
                keys[i] = new Keyframe(u, map(src[i].Value), ease);
            }
            return new LottieTrack(channel, keys);
        }

        private LottieTrack BuildVec2AxisTrack(AnimVec2 vec, bool isX, AnimChannel channel, LottieLayer layer, float ancestorStSum, Func<float, float> map)
        {
            Vec2Key[] src = vec.Keys!;
            var keys = new Keyframe[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                float u = Math.Clamp(U(ToGlobalFrame(src[i].T, layer, ancestorStSum)), 0f, 1f);
                float raw = isX ? src[i].Value.X : src[i].Value.Y;
                EasingSpec ease = i == 0 ? EasingSpec.Default : (isX ? src[i - 1].EaseX : src[i - 1].EaseY).ToSpec();
                keys[i] = new Keyframe(u, map(raw), ease);
            }
            return new LottieTrack(channel, keys);
        }

        private LottieTrack BuildSwitchOpacityTrack(float[] switchFrames, int sampleIndex, LottieLayer layer, float ancestorStSum)
        {
            int n = switchFrames.Length;
            var list = new List<Keyframe>(n + 1);
            float u0 = Math.Clamp(U(ToGlobalFrame(switchFrames[0], layer, ancestorStSum)), 0f, 1f);
            if (u0 > 1e-4f) list.Add(new Keyframe(0f, sampleIndex == 0 ? 1f : 0f, Easing.Hold));
            for (int j = 0; j < n; j++)
            {
                float u = Math.Clamp(U(ToGlobalFrame(switchFrames[j], layer, ancestorStSum)), 0f, 1f);
                list.Add(new Keyframe(u, sampleIndex == j ? 1f : 0f, Easing.Hold));
            }
            return new LottieTrack(AnimChannel.Opacity, list.ToArray());
        }

        // ── time mapping (§4) ────────────────────────────────────────────────────────────────────────────────────

        private static float ToGlobalFrame(float localFrame, LottieLayer layer, float ancestorStSum)
            => localFrame * layer.Stretch + layer.StartTime + ancestorStSum;

        private float U(float globalFrame) => (globalFrame - _doc.InPoint) / MathF.Max(1e-6f, _doc.OutPoint - _doc.InPoint);
    }
}
