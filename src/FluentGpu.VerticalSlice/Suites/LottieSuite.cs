using System;
using System.IO;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Lottie;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Render;
using FluentGpu.Rhi;
using FluentGpu.Rhi.Headless;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;

namespace FluentGpu.VerticalSlice.Harness;

/// <summary>
/// Golden gates for the Lottie player (docs/plans/wavee lottie-heroes-implementation.md §6): the three real shipped
/// assets (parse/compile correctness, alloc-zero mount, channel hygiene) plus a hand-built synthetic
/// <see cref="LottieDocument"/> exercising parent nesting, the visibility ("vis box") window, stroke-trim easing,
/// precomp clipping, the animated-bezier switch group, and every drop rule — all constructed directly against the
/// <see cref="LottieDocument"/> model (bypassing <see cref="LottieParser"/>) since that model IS the parser/compiler
/// seam (§1 architecture), exactly like <c>LottieParserTests</c>/<c>LottieCompilerTests</c> do at the unit level.
/// </summary>
static class LottieSuite
{
    private static readonly (string Id, string File, int MinDropped)[] Assets =
    [
        ("eula", "eula.json", 12),
        ("connect", "connect.json", 3),
        ("patch", "patch.json", 8),
    ];

    public static void Run(StringTable strings)
    {
        AssetsParseAndPaintCountGate();
        ParseTimeGate();
        foreach (var (id, file, _) in Assets)
        {
            MountAllocZeroGate(strings, id, file);
            MountPaintsGate(strings, id, file);
            MountAdvancesGate(strings, id, file);
        }
        ChannelsGate();
        SyntheticGates();
        EasingHoldGate();
    }

    private static string AssetPath(string file) => Path.Combine(AppContext.BaseDirectory, "Assets", "lottie", file);
    private static LottieSource LoadSource(string file) => LottieSource.FromFile(AssetPath(file));

    // gate.lottie.assets.parse[x] — the plan; gate.lottie.assets.paint-count[x] — the quality-bar informational gate
    // (FillPath/StrokePath node count per asset, > 5 each, reporting Approximations).
    private static void AssetsParseAndPaintCountGate()
    {
        foreach (var (id, file, minDropped) in Assets)
        {
            bool ok;
            string detail;
            int paints = 0;
            LottiePlan? plan = null;
            try
            {
                plan = LoadSource(file).Plan;
                bool nodesOk = plan.Nodes.Length > 0;
                bool geomOk = plan.GeometryCount > 0;
                bool everyGeomOk = true;
                foreach (LottieNode n in plan.Nodes)
                {
                    if (n.Kind == LottieNodeKind.Group) continue;
                    paints++;
                    if (n.Geometry is null || n.Geometry.VerbCount == 0) everyGeomOk = false;
                }
                bool droppedOk = plan.DroppedLayers >= minDropped;
                ok = nodesOk && geomOk && everyGeomOk && droppedOk;
                detail = $"nodes={plan.Nodes.Length} geometryCount={plan.GeometryCount} paints={paints} "
                    + $"dropped={plan.DroppedLayers} (min {minDropped}) approximations={plan.Approximations}";
            }
            catch (Exception ex) { ok = false; detail = ex.Message; }
            Check($"gate.lottie.assets.parse[{id}]", ok, detail);

            if (plan is not null)
            {
                Check($"gate.lottie.assets.paint-count[{id}]", paints > 5,
                    $"paints={paints} (>5) approximations={plan.Approximations}");
            }
        }
    }

    private static void ParseTimeGate()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var (_, file, _) in Assets) _ = LottieSource.FromFile(AssetPath(file)).Plan;
        sw.Stop();
        Check("gate.lottie.assets.parse-time", sw.Elapsed.TotalMilliseconds < 50.0,
            $"{sw.Elapsed.TotalMilliseconds:0.###}ms for {Assets.Length} assets (informational)");
    }

    private static void MountAllocZeroGate(StringTable strings, string id, string file)
    {
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc($"lottie-alloc-{id}", new Size2(220, 220), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var probe = new LottieProbe(LoadSource(file));
        using var host = new AppHost(app, window, device, fonts, strings, probe);

        // Warm past the ENTIRE played window (RiseSetup plays [0, 0.5] of the 3.5s timeline once = 1750ms; the
        // headless FixedFrameTimeSource steps 16ms/frame) so every animated-bezier switch sample gets its one-time
        // tessellation during warmup, not mid-measurement — a later sample becoming visible for the first time
        // inside the measurement window would tessellate then, which is correct engine behaviour, not an alloc leak.
        for (int i = 0; i < 200; i++) host.RunFrame();

        int tessBefore = PathRealizationCache.Shared.TessellationCount;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 60; i++) host.RunFrame();
        long after = GC.GetAllocatedBytesForCurrentThread();
        int tessAfter = PathRealizationCache.Shared.TessellationCount;

        Check($"gate.lottie.mount.alloc-zero[{id}]", after - before == 0 && tessAfter == tessBefore,
            $"allocDelta={after - before}B tessBefore={tessBefore} tessAfter={tessAfter}");
        app.Dispose();
    }

    // gate.lottie.mount.paints[x] — the recorded DrawList actually carries real, VISIBLE painted content after
    // mount: >=5 FillPath/StrokePath commands whose composed Opacity > 0.05 and whose world-space bounds
    // (Transform.TransformBounds(Rect)) intersect the 192x192 hero box, AND the realization cache's tessellation
    // count strictly INCREASED for this asset's own geometries (compared against a snapshot taken BEFORE this
    // asset mounted — not merely "> 0", since PathRealizationCache.Shared is process-global and an earlier gate
    // may have already primed it; "> 0" alone would pass even if THIS asset painted nothing at all).
    //
    // This is the gate that would have caught the "hero rail renders nothing" regression: a Lottie NULL layer's
    // own `o` (opacity) is inert in Lottie/AE's rendering model (Null layers are never themselves painted — only
    // their transform is inherited), but the shipped Eula asset carries a static o:0 on its root Null wrapper
    // ("Null 76"). The compiler used to bake that field into the wrapper's compiled Group opacity like any other
    // layer; since the engine composes nested BoxEl opacity multiplicatively down the tree (Element.cs "per-node
    // multiplied opacity"), that single inert field silently zeroed the ENTIRE subtree's composed opacity — every
    // fill/stroke recorded fine (never culled — see LottieGeometry.cs / gate.lottie.mount.channels), but every one
    // of them painted at effective alpha 0. alloc-zero/channels/parse never look at a painted VALUE, only at
    // allocation deltas and the compiled channel SET, so none of them could have caught it. Fixed in
    // LottieCompiler.EmitXformNode (`opacityApplies = layer.Type != LottieLayerType.Null`).
    private static void MountPaintsGate(StringTable strings, string id, string file)
    {
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc($"lottie-paints-{id}", new Size2(220, 220), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var probe = new LottieProbe(LoadSource(file));
        using var host = new AppHost(app, window, device, fonts, strings, probe);

        int tessBefore = PathRealizationCache.Shared.TessellationCount;
        for (int i = 0; i < 3; i++) host.RunFrame();   // first real paint
        int tessAfterFirstPaint = PathRealizationCache.Shared.TessellationCount;

        // RiseSetup fades several layers in over its played [0, 0.5] window (~1750ms @16ms/frame, ~109 frames) —
        // warm past the WHOLE window so the "is anything actually visible" check isn't racing a still-fading-in
        // layer (Patch's content in particular doesn't clear 5 visible paints until well past frame 40).
        for (int i = 3; i < 150; i++) host.RunFrame();

        var box = new RectF(0f, 0f, 192f, 192f);
        int visible = 0;
        foreach (FillPathCmd f in device.LastFillPaths)
            if (f.Opacity > 0.05f && Intersects(f.Transform.TransformBounds(f.Rect), box)) visible++;
        foreach (StrokePathCmd st in device.LastStrokePaths)
            if (st.Opacity > 0.05f && Intersects(st.Transform.TransformBounds(st.Rect), box)) visible++;

        bool tessellated = tessAfterFirstPaint > tessBefore;
        Check($"gate.lottie.mount.paints[{id}]", visible >= 5 && tessellated,
            $"visiblePaints={visible} (>=5) fills={device.LastFillPaths.Count} strokes={device.LastStrokePaths.Count} "
            + $"tessBefore={tessBefore} tessAfterFirstPaint={tessAfterFirstPaint} tessellated={tessellated}");
        app.Dispose();
    }

    private static bool Intersects(RectF a, RectF b) => a.X < b.Right && a.Right > b.X && a.Y < b.Bottom && a.Bottom > b.Y;

    // gate.lottie.mount.advances[x] — proves the compiled tracks are actually LIVE (wired and ticking), not just
    // present in the plan: the aggregate painted opacity (sum across every recorded FillPath/StrokePath — several
    // layers fade in/step over RiseSetup's played window) differs between frame 2 and frame 40 (something is
    // moving), then is stable between frame 200 and frame 260 — well past RiseSetup's ~1750ms played window
    // (loop:false holds the last composed value once its track settles — AnimScheduler.cs PASS2/PASS3, see the
    // engine doc on AnimEngine.Keyframes). A wiring-order bug (UseLayoutEffect racing OnRealized, wiring against
    // still-null NodeHandles) would show flat aggregates the whole run — this is the gate that would catch it.
    private static void MountAdvancesGate(StringTable strings, string id, string file)
    {
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc($"lottie-advances-{id}", new Size2(220, 220), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var probe = new LottieProbe(LoadSource(file));
        using var host = new AppHost(app, window, device, fonts, strings, probe);

        for (int i = 0; i < 2; i++) host.RunFrame();
        float at2 = SnapshotOpacitySum(device);
        for (int i = 2; i < 40; i++) host.RunFrame();
        float at40 = SnapshotOpacitySum(device);
        for (int i = 40; i < 200; i++) host.RunFrame();
        float at200 = SnapshotOpacitySum(device);
        for (int i = 200; i < 260; i++) host.RunFrame();
        float at260 = SnapshotOpacitySum(device);

        bool advanced = MathF.Abs(at40 - at2) > 0.01f;
        bool held = MathF.Abs(at260 - at200) < 1e-4f;
        Check($"gate.lottie.mount.advances[{id}]", advanced && held,
            $"opacitySum@2={at2:0.###} @40={at40:0.###} @200={at200:0.###} @260={at260:0.###} advanced={advanced} held={held}");
        app.Dispose();
    }

    private static float SnapshotOpacitySum(HeadlessGpuDevice device)
    {
        float sum = 0f;
        foreach (FillPathCmd f in device.LastFillPaths) sum += f.Opacity;
        foreach (StrokePathCmd st in device.LastStrokePaths) sum += st.Opacity;
        return sum;
    }

    private sealed class LottieProbe : Component
    {
        private readonly LottieSource _source;
        public LottieProbe(LottieSource source) => _source = source;
        public override Element Render() => LottieView.Create(_source, 192f, LottieOptions.RiseSetup);
    }

    // gate.lottie.mount.channels[x] — every compiled track, across all three real assets, is a compositor channel
    // (Translate*/Scale*/Rotation/Opacity/StrokeTrim*) — never LayoutW/H/SizeW/H — and at least one node carries a
    // track (a static-only compile would be a silent bug). Checked directly against the compiled LottiePlan (the
    // channel set is a compile-time fact — AnimEngine.Keyframes is a pure pass-through of it, see LottieView.Render),
    // which sidesteps needing package-internal access to LottieViewComponent's NodeHandle[] from a live mount.
    private static void ChannelsGate()
    {
        foreach (var (id, file, _) in Assets)
        {
            LottiePlan plan = LoadSource(file).Plan;
            bool anyTracked = false;
            bool anyLayoutish = false;
            foreach (LottieNode n in plan.Nodes)
            {
                if (n.Tracks.Length > 0) anyTracked = true;
                foreach (LottieTrack t in n.Tracks)
                    if (t.Channel is AnimChannel.LayoutW or AnimChannel.LayoutH or AnimChannel.SizeW or AnimChannel.SizeH)
                        anyLayoutish = true;
            }
            Check($"gate.lottie.mount.channels[{id}]", anyTracked && !anyLayoutish, $"anyTracked={anyTracked} anyLayoutish={anyLayoutish}");
        }
    }

    // gate.lottie.easing.hold — Easing.Hold is step-end.
    private static void EasingHoldGate()
    {
        float a = Easings.Ease(Easing.Hold, 0.999f);
        float b = Easings.Ease(Easing.Hold, 1f);
        Check("gate.lottie.easing.hold", a == 0f && b == 1f, $"Ease(Hold,0.999)={a} Ease(Hold,1)={b}");
    }

    // ── synthetic doc: parent / visibility / trim / precomp / drop-rules ────────────────────────────────────────

    private static void SyntheticGates()
    {
        LottieDocument doc = BuildSyntheticDocument();
        LottiePlan plan = LottieCompiler.Compile(doc);

        int aIdx = FindByName(plan, "A");
        int bIdx = FindByName(plan, "B");
        int bVisIdx = FindByName(plan, "B (vis)");
        int cIdx = FindByName(plan, "C");
        int childIdx = FindByName(plan, "ChildShape");

        Check("gate.lottie.synthetic.parent",
            aIdx >= 0 && bIdx >= 0 && IsAncestor(plan, aIdx, bIdx),
            $"aIdx={aIdx} bIdx={bIdx} ancestor={(aIdx >= 0 && bIdx >= 0 && IsAncestor(plan, aIdx, bIdx))}");

        bool translateOk = false;
        string translateDetail = "missing";
        if (aIdx >= 0)
        {
            foreach (LottieTrack t in plan.Nodes[aIdx].Tracks)
            {
                if (t.Channel != AnimChannel.TranslateX) continue;
                Keyframe[] k = t.Keys;
                translateOk = k.Length == 2
                    && Close(k[0].Offset, 0f) && Close(k[0].Value, -10f)
                    && Close(k[1].Offset, 1f) && Close(k[1].Value, 90f);
                translateDetail = k.Length == 2 ? $"[({k[0].Offset:0.###},{k[0].Value:0.###}),({k[1].Offset:0.###},{k[1].Value:0.###})]" : $"len={k.Length}";
            }
        }
        Check("gate.lottie.synthetic.parent.translate", translateOk, translateDetail);

        bool visOk = false;
        string visDetail = "missing";
        if (bVisIdx >= 0)
        {
            foreach (LottieTrack t in plan.Nodes[bVisIdx].Tracks)
            {
                if (t.Channel != AnimChannel.Opacity) continue;
                Keyframe[] k = t.Keys;
                visOk = k.Length == 3
                    && Close(k[0].Offset, 0f) && Close(k[0].Value, 0f)
                    && Close(k[1].Offset, 0.5f) && Close(k[1].Value, 1f) && IsHold(k[1].Easing)
                    && Close(k[2].Offset, 0.9f) && Close(k[2].Value, 0f) && IsHold(k[2].Easing);
                visDetail = $"len={k.Length}";
            }
        }
        Check("gate.lottie.synthetic.visibility", visOk, visDetail);

        bool trimOk = false;
        if (bIdx >= 0)
        {
            foreach (int i in DescendantsOf(plan, bIdx))
            {
                LottieNode n = plan.Nodes[i];
                if (n.Kind != LottieNodeKind.StrokePath) continue;
                foreach (LottieTrack t in n.Tracks)
                {
                    if (t.Channel != AnimChannel.StrokeTrimEnd) continue;
                    Keyframe[] k = t.Keys;
                    if (k.Length != 2) continue;
                    float got = Easings.Ease(k[1].Easing, 0.5f);
                    float want = ReferenceCubicBezier(0.5f, 0.2f, 0f, 0.8f, 1f);
                    trimOk = Close(k[0].Offset, 0f) && Close(k[1].Offset, 1f) && MathF.Abs(got - want) < 1e-3f;
                }
            }
        }
        Check("gate.lottie.synthetic.trim", trimOk, $"trimOk={trimOk}");

        bool precompOk = cIdx >= 0 && plan.Nodes[cIdx].Clip && Close(plan.Nodes[cIdx].W, 50f) && Close(plan.Nodes[cIdx].H, 50f);
        Check("gate.lottie.synthetic.precomp", precompOk, $"cIdx={cIdx} clip={(cIdx >= 0 ? plan.Nodes[cIdx].Clip : (bool?)null)}");

        int switchCount = 0;
        int distinctEpochs = 0;
        bool firstBoundaryInRange = false;
        if (childIdx >= 0)
        {
            var epochs = new System.Collections.Generic.HashSet<ulong>();
            for (int i = 0; i < plan.Nodes.Length; i++)
            {
                LottieNode n = plan.Nodes[i];
                if (n.Parent != childIdx || n.Kind != LottieNodeKind.FillPath) continue;
                switchCount++;
                if (n.Geometry is not null) epochs.Add(n.Geometry.Epoch.Value);
                foreach (LottieTrack t in n.Tracks)
                {
                    if (t.Channel != AnimChannel.Opacity) continue;
                    foreach (Keyframe k in t.Keys)
                        if (k.Offset > 0f && k.Offset < 1f) firstBoundaryInRange = true;
                }
            }
            distinctEpochs = epochs.Count;
        }
        Check("gate.lottie.synthetic.switch-group", switchCount >= 2 && distinctEpochs == switchCount && firstBoundaryInRange,
            $"switchCount={switchCount} distinctEpochs={distinctEpochs} firstBoundaryInRange={firstBoundaryInRange}");

        Check("gate.lottie.synthetic.drop-rules", plan.DroppedLayers == 4, $"DroppedLayers={plan.DroppedLayers} (want 4)");
    }

    private static int FindByName(LottiePlan plan, string name)
    {
        for (int i = 0; i < plan.Nodes.Length; i++)
            if (plan.Nodes[i].Name == name) return i;
        return -1;
    }

    private static bool IsAncestor(LottiePlan plan, int ancestor, int node)
    {
        int p = plan.Nodes[node].Parent;
        while (p >= 0)
        {
            if (p == ancestor) return true;
            p = plan.Nodes[p].Parent;
        }
        return false;
    }

    private static System.Collections.Generic.IEnumerable<int> DescendantsOf(LottiePlan plan, int root)
    {
        for (int i = root + 1; i < plan.Nodes.Length; i++)
            if (IsAncestor(plan, root, i)) yield return i;
    }

    private static bool Close(float a, float b) => MathF.Abs(a - b) < 1e-3f;
    private static bool IsHold(EasingSpec e) => Easings.Ease(e, 0.999f) == 0f && Easings.Ease(e, 1f) == 1f;

    /// <summary>A standalone reference re-implementation of the CSS/WinUI cubic-bezier evaluator (the engine's own
    /// <c>Easings.CubicBezier</c> is <c>internal</c> to FluentGpu.Engine and not visible here) — used ONLY to verify
    /// that <see cref="LottieEase.ToSpec"/> actually produces the segment's authored control points end to end, the
    /// same differential-cross-check posture <c>PathSuite</c> uses for the tessellator.</summary>
    private static float ReferenceCubicBezier(float t, float x1, float y1, float x2, float y2)
    {
        static float Cx(float s, float x1, float x2) { float u = 1f - s; return 3f * u * u * s * x1 + 3f * u * s * s * x2 + s * s * s; }
        static float Cy(float s, float y1, float y2) { float u = 1f - s; return 3f * u * u * s * y1 + 3f * u * s * s * y2 + s * s * s; }
        float lo = 0f, hi = 1f, guess = t;
        for (int i = 0; i < 30; i++)
        {
            guess = (lo + hi) / 2f;
            if (Cx(guess, x1, x2) < t) lo = guess; else hi = guess;
        }
        return Cy(guess, y1, y2);
    }

    private static LottieDocument BuildSyntheticDocument()
    {
        var aPosition = new AnimVec2
        {
            Keys =
            [
                new Vec2Key { T = 0f, Value = new Point2(0f, 0f) },
                new Vec2Key { T = 100f, Value = new Point2(100f, 0f) },
            ],
        };
        var layerA = new LottieLayer
        {
            Index = 1, Parent = -1, Name = "A", Type = LottieLayerType.Null,
            InPoint = 0f, OutPoint = 100f, StartTime = 0f, Stretch = 1f,
            Transform = new LottieTransform { Anchor = AnimVec2.Of(new Point2(10f, 0f)), Position = aPosition },
        };

        // Ease lives on the DEPARTING key (Lottie convention — ScalarKey.Ease doc: "segment THIS -> next"); the
        // compiler shifts it onto the engine's ARRIVING Keyframe when building the track.
        var trimEnd = new AnimScalar
        {
            Keys =
            [
                new ScalarKey { T = 0f, Value = 0f, Ease = new LottieEase(0.2f, 0f, 0.8f, 1f, false) },
                new ScalarKey { T = 100f, Value = 100f },
            ],
        };
        var strokeGeometry = new LottieShape
        {
            Type = LottieShapeType.Path,
            Path = new AnimPath { Static = new LottieBezier { V = [new Point2(0, 0), new Point2(10, 0)], I = [default, default], O = [default, default], Closed = false } },
        };
        var strokePaint = new LottieShape { Type = LottieShapeType.Stroke, Color = new ColorF(1f, 0f, 0f, 1f), Width = AnimScalar.Of(2f) };
        var trimShape = new LottieShape { Type = LottieShapeType.Trim, TrimStart = AnimScalar.Of(0f), TrimEnd = trimEnd, TrimMode = 1 };
        var layerB = new LottieLayer
        {
            Index = 2, Parent = 1, Name = "B", Type = LottieLayerType.Shape,
            InPoint = 50f, OutPoint = 90f, StartTime = 0f, Stretch = 1f,
            Transform = new LottieTransform(),
            Shapes = [strokeGeometry, strokePaint, trimShape],
        };

        var bez0 = new LottieBezier { V = [new Point2(0, 0), new Point2(10, 0), new Point2(10, 10)], I = [default, default, default], O = [default, default, default], Closed = true };
        var bez1 = new LottieBezier { V = [new Point2(0, 0), new Point2(20, 0), new Point2(20, 20)], I = [default, default, default], O = [default, default, default], Closed = true };
        var childPath = new LottieShape { Type = LottieShapeType.Path, Path = new AnimPath { Keys = [new PathKey { T = 0f, Shape = bez0 }, new PathKey { T = 50f, Shape = bez1 }] } };
        var childFill = new LottieShape { Type = LottieShapeType.Fill, Color = new ColorF(0f, 1f, 0f, 1f), Opacity = AnimScalar.Of(100f) };
        var childLayer = new LottieLayer
        {
            Index = 1, Parent = -1, Name = "ChildShape", Type = LottieLayerType.Shape,
            InPoint = 0f, OutPoint = 100f, StartTime = 0f, Stretch = 1f,
            Transform = new LottieTransform(),
            Shapes = [childPath, childFill],
        };
        var layerC = new LottieLayer
        {
            Index = 3, Parent = -1, Name = "C", Type = LottieLayerType.Precomp,
            InPoint = 0f, OutPoint = 100f, StartTime = 20f, Stretch = 1f,
            RefId = "childComp", Width = 50f, Height = 50f,
            Transform = new LottieTransform(),
        };

        var layerD = new LottieLayer { Index = 4, Parent = -1, Name = "x_shdw", Type = LottieLayerType.Null, InPoint = 0f, OutPoint = 100f, Transform = new LottieTransform() };
        var layerE = new LottieLayer { Index = 5, Parent = -1, Name = "MatteUser", Type = LottieLayerType.Null, HasTrackMatte = true, InPoint = 0f, OutPoint = 100f, Transform = new LottieTransform() };
        var layerF = new LottieLayer { Index = 6, Parent = -1, Name = "MatteSource", Type = LottieLayerType.Null, IsMatteSource = true, InPoint = 0f, OutPoint = 100f, Transform = new LottieTransform() };
        var layerG = new LottieLayer { Index = 7, Parent = -1, Name = "BlurLayer", Type = LottieLayerType.Null, HasDropEffect = true, InPoint = 0f, OutPoint = 100f, Transform = new LottieTransform() };

        return new LottieDocument
        {
            FrameRate = 60f, InPoint = 0f, OutPoint = 100f, Width = 100f, Height = 100f, Version = "synthetic",
            Layers = [layerA, layerB, layerC, layerD, layerE, layerF, layerG],
            Precomps = new System.Collections.Generic.Dictionary<string, LottieLayer[]>(StringComparer.Ordinal) { ["childComp"] = [childLayer] },
        };
    }
}
