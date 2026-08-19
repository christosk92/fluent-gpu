using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Forms;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Media;
using FluentGpu.Pal;
using FluentGpu.Input;
using FluentGpu.Layout;
using FluentGpu.Pal.Headless;
using FluentGpu.Reconciler;
using FluentGpu.Controls;
using FluentGpu.Render;
using FluentGpu.Rhi;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using static FluentGpu.Dsl.Ui;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;




static class NavSuite
{
    public static void Run(StringTable strings)
    {
        NavigationChecks();
        PageHostChecks(strings);
        KeepAliveChecks(strings);
        KeepAliveWedgedExitBackstopChecks(strings);
        SemanticZoomNavigationChecks(strings);
        ParkBeforeRenderChecks(strings);
        FreezeOnExitChecks(strings);
        UnparkReplayBudgetChecks(strings);
        NavRouterChecks(strings);
        GalleryChecks(strings);
        ActivationLifecycleChecks(strings);
        NavigationViewChecks(strings);
        NavigationViewAnimationChecks(strings);
        NavHierarchyChecks(strings);
    }

    static void NavigationChecks()
    {
        var nav = new Navigator(new Route("home"));
        bool d1 = nav.Current.Name == "home" && !nav.CanGoBack && nav.Depth == 1;
        nav.Push("playlist", "p1");
        bool d2 = nav.Current is { Name: "playlist", Arg: "p1" } && nav.CanGoBack && nav.Depth == 2;
        string ser = nav.Serialize();
        nav.Pop();
        bool d3 = nav.Current.Name == "home" && !nav.CanGoBack;
        var restored = Navigator.Deserialize(ser);
        bool d4 = restored.Depth == 2 && restored.Current is { Name: "playlist", Arg: "p1" };
        Check("49. Navigator: push/pop/depth + serialize round-trip", d1 && d2 && d3 && d4, ser);
    }

    static void PageHostChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("nav", new Size2(480, 320), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var nav = new Navigator(new Route("home"));
        Element View(Route r) => r.Name == "home"
            ? new BoxEl { Children = [new TextEl("HOME PAGE")] }
            : new BoxEl { Children = [new TextEl("PLAYLIST " + r.Arg)] };
        using var host = new AppHost(app, window, device, fonts, strings, new PageHost(nav, View));

        host.RunFrame();
        bool onHome = HasGlyph(device, strings, "HOME PAGE");
        nav.Push("playlist", "x1");
        host.RunFrame();
        bool onDetail = HasGlyph(device, strings, "PLAYLIST x1");
        nav.Pop();
        host.RunFrame();
        bool backHome = HasGlyph(device, strings, "HOME PAGE");
        Check("50. PageHost renders + navigates the back stack", onHome && onDetail && backHome, "home → playlist → back");
    }

    static void KeepAliveChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("keepalive", new Size2(260, 220), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var probe = new KeepAliveProbe { MaxEntries = 2 };
        var imageCache = new ImageCache(new FakeImageDecoder(), budgetBytes: 24 * 24 * 4);
        using var host = new AppHost(app, window, device, fonts, strings, probe, images: imageCache);

        host.RunFrame();
        var imgA = host.Images.Request("keepalive-a", 24, 24);
        bool initial = HasGlyph(device, strings, "a:0") && host.Images.RefsOf(imgA) == 1;

        var buttonA = FindRole(host.Scene, host.Scene.Root, AutomationRole.Button);
        ClickNode(host, window, buttonA);
        bool clicked = HasGlyph(device, strings, "a:1") && !FocusedNode(host.Scene, host.Scene.Root).IsNull;

        var scrollA = FindScrollable(host.Scene, host.Scene.Root);
        var sr = host.Scene.AbsoluteRect(scrollA);
        window.QueueInput(new InputEvent(InputKind.Wheel, new Point2(sr.X + 20f, sr.Y + 20f), 0, 0, 120f));
        host.RunFrame();
        host.Scene.TryGetScroll(scrollA, out var scA);
        float offsetA = scA.OffsetY;

        probe.Route!.Value = "b";
        host.RunFrame();
        bool detached = HasGlyph(device, strings, "b:0") && !HasGlyph(device, strings, "a:1")
                        && FocusedNode(host.Scene, host.Scene.Root).IsNull
                        && host.Images.RefsOf(imgA) == 0;

        var pressure = host.Images.Request("keepalive-pressure", 64, 64);
        host.Images.Pump();   // evicts inactive A's decoded payload while its retained scene node still holds ImageId
        bool inactiveImageEvicted = host.Images.StateOf(imgA) == ImageState.None
                                    && host.Images.StateOf(pressure) == ImageState.None;

        probe.Route.Value = "a";
        host.RunFrame();
        var scrollA2 = FindScrollable(host.Scene, host.Scene.Root);
        host.Scene.TryGetScroll(scrollA2, out var scA2);
        bool restored = HasGlyph(device, strings, "a:1") && scA2.OffsetY > offsetA - 0.5f
                        && host.Images.RefsOf(imgA) == 1 && host.Images.StateOf(imgA) == ImageState.Ready;

        probe.Route.Value = "b"; host.RunFrame();
        probe.Route.Value = "c"; host.RunFrame();   // with MaxEntries=2, inactive A is the LRU victim
        probe.Route.Value = "a"; host.RunFrame();
        bool evictedFresh = HasGlyph(device, strings, "a:0") && !HasGlyph(device, strings, "a:1");

        Check("50a. KeepAlive opt-in caches page state/scroll, detaches inactive input/draw, releases image pins, and LRU-evicts inactive pages",
            initial && clicked && offsetA > 1f && detached && inactiveImageEvicted && restored && evictedFresh,
            $"initial={initial} clicked={clicked} off={offsetA:0.#}->{scA2.OffsetY:0.#} detached={detached} imgEvicted={inactiveImageEvicted} restored={restored} evictedFresh={evictedFresh} refsA={host.Images.RefsOf(imgA)} stateA={host.Images.StateOf(imgA)}");

        using var presenceApp = new HeadlessPlatformApp();
        var presenceWindow = new HeadlessWindow(new WindowDesc("keepalive-presence", new Size2(260, 220), 1f));
        presenceWindow.Show();
        var presenceDevice = new HeadlessGpuDevice();
        var presenceFonts = new HeadlessFontSystem(strings);
        var presenceProbe = new KeepAlivePresenceProbe();
        using var presenceHost = new AppHost(presenceApp, presenceWindow, presenceDevice, presenceFonts, strings, presenceProbe);

        presenceHost.RunFrame();
        bool presenceA = HasGlyph(presenceDevice, strings, "presence-a");
        presenceProbe.Route!.Value = "b";
        presenceHost.RunFrame();
        bool parkedExitHardRemoved = HasGlyph(presenceDevice, strings, "presence-b")
                                     && !HasGlyph(presenceDevice, strings, "presence-a")
                                     && presenceHost.Scene.OrphanCount == 0;
        Check("50a2. animated presence removal inside a parked KeepAlive page is hard-removed (no cross-route orphan)",
            presenceA && parkedExitHardRemoved,
            $"initial={presenceA} switched={parkedExitHardRemoved} orphans={presenceHost.Scene.OrphanCount}");

        using var nestedApp = new HeadlessPlatformApp();
        var nestedWindow = new HeadlessWindow(new WindowDesc("nested-hit-visibility", new Size2(260, 220), 1f));
        nestedWindow.Show();
        var nestedDevice = new HeadlessGpuDevice();
        var nestedFonts = new HeadlessFontSystem(strings);
        var nestedProbe = new NestedHitVisibilityProbe();
        using var nestedHost = new AppHost(nestedApp, nestedWindow, nestedDevice, nestedFonts, strings, nestedProbe);
        nestedHost.RunFrame();
        nestedProbe.Live.Value = true;
        nestedHost.RunFrame();
        var nestedScroll = FindScrollable(nestedHost.Scene, nestedHost.Scene.Root);
        var nestedRect = nestedHost.Scene.AbsoluteRect(nestedScroll);
        var nestedPoint = new Point2(nestedRect.X + 30f, nestedRect.Y + 30f);
        var nestedRouted = nestedHost.Input.ScrollableUnderForAxis(nestedPoint, wantHorizontal: false);
        nestedWindow.QueueInput(new InputEvent(InputKind.Wheel, nestedPoint, 0, 0, 120f));
        nestedHost.RunFrame();
        nestedHost.Scene.TryGetScroll(nestedScroll, out var nestedState);
        Check("50a3. nested transparent component boundaries remain input-traversable when an inner branch becomes hit-testable",
            !nestedScroll.IsNull && nestedRouted == nestedScroll && nestedState.OffsetY > 1f,
            $"scroll=n#{nestedScroll.Raw.Index} routed=n#{nestedRouted.Raw.Index} offset={nestedState.OffsetY:0.#}");

        // A retained slot can intentionally serve several route TOKENS (album→album / artist→artist) under one
        // cache key. The page must stay mounted, but TransitionFor still owns that navigation edge and seeds an entrance
        // on the updated root; otherwise those in-place navigations silently lose all page motion.
        {
            var scene = new SceneStore();
            var anim = new AnimEngine(scene);
            var recon = new TreeReconciler(scene, strings) { Anim = anim };
            var token = new Signal<int>(1);
            recon.ReconcileRoot(
                Flow.KeepAlive(
                    () => token.Value,
                    _ => "shared-detail-slot",
                    n => new BoxEl { Width = 240f, Height = 120f, Children = [Text("detail-" + n)] },
                    new KeepAliveOptions(TransitionFor: static (_, _) => MotionRecipes.PageSlideForward with { Exit = default })),
                null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var retainedRoot = scene.FirstChild(scene.Root);

            token.Value = 2;
            recon.Runtime.Flush();
            new FlexLayout(scene, fonts).Run(scene.Root);
            anim.Tick(0f);
            var afterRoot = scene.FirstChild(scene.Root);
            bool entrance = anim.TryGetTrackValue(afterRoot, AnimChannel.Opacity, out float op) && op < 0.2f;

            Check("50a4. KeepAlive same-key token change preserves the retained root and still replays TransitionFor entrance",
                afterRoot == retainedRoot && entrance,
                $"sameRoot={afterRoot == retainedRoot} entrance={entrance} opacity={op:0.00}");
        }

        // A page-navigation KeepAlive opts out of descendant layout motion for two commits: the activation itself and
        // the next-frame OnBoundsChanged correction used by Responsive/PagedShelf-style controls. Reactivating a cached
        // page also lands a parked structural row instead of resuming it from an arbitrary mid-navigation sample.
        {
            using var motionApp = new HeadlessPlatformApp();
            var motionWindow = new HeadlessWindow(new WindowDesc("keepalive-motion-suppression", new Size2(260, 180), 1f));
            motionWindow.Show();
            var motionDevice = new HeadlessGpuDevice();
            var motionFonts = new HeadlessFontSystem(strings);
            var motionProbe = new KeepAliveMotionSuppressionProbe();
            using var motionHost = new AppHost(motionApp, motionWindow, motionDevice, motionFonts, strings, motionProbe);

            motionHost.RunFrame();
            motionProbe.Route!.Value = "measured";
            motionHost.RunFrame();   // proxy width 80; layout publishes the real width
            motionHost.RunFrame();   // measured width 200; activation policy must snap CardRefit

            NodeHandle card = motionProbe.AnimatedNode;
            RectF cardBounds = motionHost.Scene.AbsoluteRect(card);
            bool measuredLanded = !card.IsNull && MathF.Abs(cardBounds.W - 200f) < 0.5f;
            bool correctionSnapped = !HasStructuralTrack(motionHost.Animation, card);

            motionProbe.Route.Value = "idle";
            motionHost.RunFrame();
            motionHost.Animation.Animate(card, AnimChannel.TranslateX, 32f, 0f, 1000f, Easing.Linear);
            motionHost.Animation.SetNodeParked(card, true);   // mirrors a row retained halfway through its finite track
            bool parkedTrackSeeded = motionHost.Animation.TryGetTrackValue(card, AnimChannel.TranslateX, out _);
            motionProbe.Route.Value = "measured";
            motionHost.RunFrame();
            bool cachedTrackLanded = !HasStructuralTrack(motionHost.Animation, card);

            Check("50a5. KeepAlive navigation suppression lands first-measure CardRefit and cached structural tracks without random resize motion",
                measuredLanded && correctionSnapped && parkedTrackSeeded && cachedTrackLanded,
                $"width={cardBounds.W:0.#} correctionSnapped={correctionSnapped} parkedSeeded={parkedTrackSeeded} cachedLanded={cachedTrackLanded}");
        }

        static bool HasStructuralTrack(AnimEngine anim, NodeHandle node)
            => anim.TryGetTrackValue(node, AnimChannel.TranslateX, out _)
               || anim.TryGetTrackValue(node, AnimChannel.TranslateY, out _)
               || anim.TryGetTrackValue(node, AnimChannel.ScaleX, out _)
               || anim.TryGetTrackValue(node, AnimChannel.ScaleY, out _)
               || anim.TryGetTrackValue(node, AnimChannel.SizeW, out _)
               || anim.TryGetTrackValue(node, AnimChannel.SizeH, out _)
               || anim.TryGetTrackValue(node, AnimChannel.LayoutW, out _)
               || anim.TryGetTrackValue(node, AnimChannel.LayoutH, out _);
    }

    // gate.reconciler.keepalive-exit-backstop — FinalizeKeepAliveTransitions' deadline mirrors the orphan path's own
    // hard deadline (ExitMaxAgeMs) + wall-clock guard, because HasTracks is index-only: it matches ANY row on the
    // outgoing root, including a Parked one PASS1/PASS2 will never advance. Without the backstop a wedged exit pins
    // ExitingKey — and the boundary's ZStack overlay — forever.
    static void KeepAliveWedgedExitBackstopChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("keepalive-wedge", new Size2(260, 160), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var probe = new KeepAliveWedgeProbe();
        using var host = new AppHost(app, window, device, fonts, strings, probe);

        host.RunFrame();
        // KeepAlive's View() returns Embed.Comp(...) (KeepAliveWedgePage), so probe.RootA (OnRealized on the page's
        // OWN BoxEl) is one level BELOW the KeepAlive-managed entry.Root — the ComponentEl anchor SeedExit/HasTracks
        // actually target. Walk up to it; its parent is the boundary (state.Boundary).
        var aRoot = host.Scene.Parent(probe.RootA);
        var boundary = host.Scene.Parent(aRoot);
        bool mountedA = !aRoot.IsNull && !boundary.IsNull && HasGlyph(device, strings, "wedge-a");

        probe.Route!.Value = "b";
        host.RunFrame();   // BeginKeepAliveExit: seeds the 250ms PageSlideForward exit, marks the boundary ZStack
        bool overlapLive = (host.Scene.Flags(boundary) & NodeFlags.ZStack) != 0
                            && host.Scene.IsLive(aRoot) && host.Animation.HasTracks(aRoot)
                            && HasGlyph(device, strings, "wedge-b");

        // Wedge: freeze A's exit rows mid-flight — the same SetNodeParked idiom 50a5 uses to plant a frozen row. PASS1/
        // PASS2 skip Parked rows, so HasTracks(aRoot) never goes false on its own from here.
        host.Animation.SetNodeParked(aRoot, true);
        // Parking A quiesces HasActive on the exit tracks, so the host would idle-skip (AnimClockMs stops). Seed a
        // 0-alloc dummy on B so frames — and the anim-clock deadline — keep advancing without a component re-render.
        host.Animation.SeedEased(probe.RootB, AnimChannel.ScaleX, 1f, 1.01f, 4000f, Easing.Linear);

        for (int i = 0; i < 15; i++) host.RunFrame();   // ~240ms of anim-clock — under the ~350ms (250+0+100) deadline
        bool stillHeldBeforeDeadline = (host.Scene.Flags(boundary) & NodeFlags.ZStack) != 0
                                        && !host.Scene.Parent(aRoot).IsNull;

        bool anyAlloc = false;
        for (int i = 0; i < 20; i++)                    // past the deadline — must force-finish, and stay alloc-clean
        {
            var stats = host.RunFrame();
            if (stats.HotPhaseAllocBytes != 0) anyAlloc = true;
        }
        bool forceFinished = (host.Scene.Flags(boundary) & NodeFlags.ZStack) == 0
                              && host.Scene.Parent(aRoot).IsNull
                              && HasGlyph(device, strings, "wedge-b");

        Check("gate.reconciler.keepalive-exit-backstop a wedged (parked mid-flight) KeepAlive exit track is force-finished at its own anim-clock deadline instead of pinning the ZStack overlay forever",
            mountedA && overlapLive && stillHeldBeforeDeadline && forceFinished && !anyAlloc,
            $"mountedA={mountedA} overlapLive={overlapLive} heldBeforeDeadline={stillHeldBeforeDeadline} forceFinished={forceFinished} alloc={anyAlloc}");
    }

    static void SemanticZoomNavigationChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("semantic-zoom-anchor", new Size2(280, 180), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var probe = new SemanticZoomItemsProbe();
        using var host = new AppHost(app, window, device, fonts, strings, probe);
        host.RunFrame();
        var focusedIn = FindRole(host.Scene, host.Scene.Root, AutomationRole.Button);
        host.Input.SetFocus(focusedIn, visual: true);

        probe.Zoom.ZoomOutTo(70);
        bool startedBeforeCommit = probe.Started.Count == 1 && probe.Completed.Count == 0
            && probe.Started[0] is { SourceIndex: 70, DestinationIndex: 70 };
        bool oldFrameIntact = HasGlyph(device, strings, "zoom-in-0") && !HasGlyph(device, strings, "zoom-out-70");
        host.RunFrame();
        float firstPresentationOffset = probe.OutItems.ScrollOffset;
        bool parkedOnFirstPresentation = MathF.Abs(firstPresentationOffset
            - 70f * SemanticZoomItemsProbe.RowExtent) < 0.5f && host.Animation.HasActive;
        for (int i = 0; i < 3; i++) host.RunFrame();
        bool anchored = parkedOnFirstPresentation && HasGlyph(device, strings, "zoom-out-70")
            && probe.OutItems.ScrollOffset >= 70f * SemanticZoomItemsProbe.RowExtent - 1f
            && probe.Completed.Count == 1
            && probe.Completed[0].OperationId == probe.Started[0].OperationId;
        bool focusRestored = !host.Input.Focused.IsNull;

        // Escape bubbles to the stable SemanticZoom root and anchors the detail view before presenting it.
        window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Escape));
        for (int i = 0; i < 4; i++) host.RunFrame();
        bool escaped = HasGlyph(device, strings, "zoom-in-70")
            && probe.Completed.Count == 2
            && probe.Completed[^1].To == SemanticZoomViewKind.ZoomedIn;

        Check("gate.semantic-zoom.anchor the view-change callback leads the swap, the incoming layout effect parks the mapped item before its first animation tick, then completion publishes; focus survives and Escape reverses",
            startedBeforeCommit && oldFrameIntact && anchored && focusRestored && escaped,
            $"started={startedBeforeCommit} oldFrame={oldFrameIntact} anchored={anchored} "
            + $"firstOff={firstPresentationOffset:0.#} outOff={probe.OutItems.ScrollOffset:0.#} "
            + $"focus={focusRestored} escape={escaped}");

        // KeepAlive retains each viewport. Invalid maps intentionally skip StartBringItemIntoView, so a warm return
        // must reveal the overview at its previous scroll offset rather than resetting it.
        using var preserveApp = new HeadlessPlatformApp();
        var preserveWindow = new HeadlessWindow(new WindowDesc("semantic-zoom-preserve", new Size2(280, 180), 1f));
        preserveWindow.Show();
        var preserveDevice = new HeadlessGpuDevice();
        var preserveProbe = new SemanticZoomItemsProbe(noAnchor: true);
        using var preserveHost = new AppHost(preserveApp, preserveWindow, preserveDevice,
            new HeadlessFontSystem(strings), strings, preserveProbe);
        preserveHost.RunFrame();
        preserveProbe.Zoom.ZoomOutTo(10);
        for (int i = 0; i < 3; i++) preserveHost.RunFrame();
        preserveProbe.OutItems.ScrollBy(420f);
        preserveHost.RunFrame();
        float before = preserveProbe.OutItems.ScrollOffset;
        preserveProbe.Zoom.ZoomInTo(10);
        for (int i = 0; i < 3; i++) preserveHost.RunFrame();
        preserveProbe.Zoom.ZoomOutTo(10);
        for (int i = 0; i < 3; i++) preserveHost.RunFrame();
        float after = preserveProbe.OutItems.ScrollOffset;
        bool preserved = before > 1f && MathF.Abs(after - before) < 0.5f
            && preserveProbe.Started.All(static c => c.DestinationIndex == -1);
        Check("gate.semantic-zoom.scroll-preservation both KeepAlive viewports stay warm and an unanchored return preserves the overview offset",
            preserved, $"offset={before:0.#}->{after:0.#} changes={preserveProbe.Started.Count}");

        bool oldReduced = Motion.ReducedMotion;
        try
        {
            Motion.ReducedMotion = true;
            preserveProbe.Zoom.ZoomInTo(10);
            bool stillOldBeforeFrame = preserveDevice.LastGlyphs.Any(
                g => strings.Resolve(g.Text).StartsWith("zoom-out-", StringComparison.Ordinal));
            for (int i = 0; i < 3; i++) preserveHost.RunFrame();
            bool reducedCommitted = preserveProbe.Completed[^1].To == SemanticZoomViewKind.ZoomedIn
                && !preserveHost.Animation.HasActive;
            Check("gate.semantic-zoom.reduced-motion keeps callback/anchoring semantics and snaps transform motion",
                stillOldBeforeFrame && reducedCommitted,
                $"held={stillOldBeforeFrame} committed={reducedCommitted} tracks={preserveHost.Animation.HasActive}");
        }
        finally { Motion.ReducedMotion = oldReduced; }
    }

    // gate.reconciler.park-before-render — the flush ORDERING guarantee (ReactiveCore: structural queue before normal).
    //
    // The hazard: a KeepAlive boundary and the page mounted inside it both subscribe to ONE route signal. The boundary
    // is what PARKS the outgoing page (ReconcileKeepAlive → DeactivateKeepAliveEntry → SetSubtreeParked), and a parked
    // component's render is skipped (Reconciler.RunComponent). Before the structural split, a flush ran effects in
    // schedule order = REVERSE subscription order (Signal.NotifySubscribers walks its list backwards), so whichever of
    // the two happened to be subscribed LAST ran FIRST. With the natural mount order (boundary subscribes first, page
    // second) the page therefore ran BEFORE the boundary and rendered once against the INCOMING route — an artist page
    // deriving an album uri, a detail page re-keying its whole track list — and only then got parked.
    //
    // Both subscription orders are exercised, and the boundary effect must lead in BOTH:
    //   order A (natural mount)     — subs [boundary, page] ⇒ the page is scheduled first. This is the defect case.
    //   order B (boundary re-armed) — a write to an unrelated signal the boundary also reads re-runs it, so it unlinks
    //                                 and re-subscribes at the TAIL ⇒ subs [page, boundary], the boundary is scheduled
    //                                 first. Correct even before the fix; it must stay correct after it.
    static void ParkBeforeRenderChecks(StringTable strings)
    {
        var (okA, detailA) = RunParkOrderCase(strings, boundarySubscribesLast: false);
        var (okB, detailB) = RunParkOrderCase(strings, boundarySubscribesLast: true);
        Check("gate.reconciler.park-before-render a cross-slot KeepAlive route write parks the outgoing page BEFORE its render effect can run against the incoming route — for either subscription order (structural boundary effects flush ahead of component render effects)",
            okA && okB, $"orderA(boundary-first-sub) [{detailA}] · orderB(page-first-sub) [{detailB}]");
    }

    static (bool Ok, string Detail) RunParkOrderCase(StringTable strings, bool boundarySubscribesLast)
    {
        var scene = new SceneStore();
        var recon = new TreeReconciler(scene, strings);
        var route = new Signal<string>("a");
        var rearm = new Signal<int>(0);     // read by the boundary ONLY — used to re-order its route subscription
        var log = new ParkOrderLog();

        recon.ReconcileRoot(
            Flow.KeepAlive(
                () => { _ = rearm.Value; log.Entries.Add("B@" + route.Peek()); return route.Value; },
                k => k,
                k => Embed.Comp(() => new ParkOrderPage(k, route, log)),
                new KeepAliveOptions(MaxEntries: 2)),
            null);

        if (boundarySubscribesLast)
        {
            // Re-run the boundary alone (same key ⇒ the retained page is reused, NOT re-rendered — Reconciler.Update on
            // a props-less ComponentEl is a no-op), so only the boundary re-links its sources and lands at the tail.
            rearm.Value = 1;
            recon.Runtime.Flush();
        }

        int before = log.Entries.Count;
        route.Value = "b";              // the cross-slot navigation: park "a", mount + render "b"
        recon.Runtime.Flush();

        bool staleRender = false, boundaryLed = false, incomingRendered = false;
        for (int i = before; i < log.Entries.Count; i++)
        {
            string e = log.Entries[i];
            if (i == before) boundaryLed = e.StartsWith("B@", StringComparison.Ordinal);
            if (e == "a@b") staleRender = true;             // THE defect: the outgoing page rendered on the new route
            if (e == "b@b") incomingRendered = true;        // the destination really did render (no vacuous pass)
        }
        string trace = string.Join(",", log.Entries.Skip(before));
        return (!staleRender && boundaryLed && incomingRendered,
            $"trace={trace} stale={staleRender} boundaryLed={boundaryLed} incoming={incomingRendered}");
    }

    // gate.reconciler.freeze-on-exit — BeginKeepAliveExit render-freezes the outgoing page (orthogonal to park).
    // The page stays attached so the exit track paints; component renders do not run against the incoming route
    // or against unrelated signal writes mid-exit; UseActivation does not fire at freeze (only at park/un-park);
    // a mid-exit reclaim replays exactly one deferred render.
    static void FreezeOnExitChecks(StringTable strings)
    {
        var scene = new SceneStore();
        var anim = new AnimEngine(scene);
        var recon = new TreeReconciler(scene, strings) { Anim = anim };
        var route = new Signal<string>("a");
        var bump = new Signal<int>(0);
        var log = new ParkOrderLog();
        var act = new FreezeExitActivation();

        recon.ReconcileRoot(
            Flow.KeepAlive(
                () => route.Value,
                k => k,
                k => Embed.Comp(() => new FreezeExitPage(k, route, bump, log, act)),
                new KeepAliveOptions(MaxEntries: 2,
                    TransitionFor: static (_, _) => MotionRecipes.PageSlideForward)),
            null);

        int afterMount = log.Entries.Count;
        int onA0 = act.On.GetValueOrDefault("a");
        int offA0 = act.Off.GetValueOrDefault("a");

        route.Value = "b";
        recon.Runtime.Flush();

        bool staleRender = false, incomingRendered = false;
        int outgoingRenders = 0;
        for (int i = afterMount; i < log.Entries.Count; i++)
        {
            string e = log.Entries[i];
            if (e == "a@b") staleRender = true;
            if (e == "b@b") incomingRendered = true;
            if (e.StartsWith("a@", StringComparison.Ordinal)) outgoingRenders++;
        }
        bool freezeNoActivation = act.On.GetValueOrDefault("a") == onA0 && act.Off.GetValueOrDefault("a") == offA0;

        int beforeBump = log.Entries.Count;
        bump.Value++;
        recon.Runtime.Flush();
        bool bumpRenderedOutgoing = false;
        for (int i = beforeBump; i < log.Entries.Count; i++)
            if (log.Entries[i].StartsWith("a@", StringComparison.Ordinal)) bumpRenderedOutgoing = true;

        int beforeReclaim = log.Entries.Count;
        route.Value = "a";
        recon.Runtime.Flush();
        int reclaimRenders = 0;
        for (int i = beforeReclaim; i < log.Entries.Count; i++)
            if (log.Entries[i].StartsWith("a@", StringComparison.Ordinal)) reclaimRenders++;

        bool activationUnchanged = act.On.GetValueOrDefault("a") == onA0 && act.Off.GetValueOrDefault("a") == offA0;

        Check("gate.reconciler.freeze-on-exit an exit-Active KeepAlive swap freeze-renders the outgoing page: no render against the incoming route or mid-exit unrelated writes; reclaim replays exactly one deferred render; UseActivation fires on park/un-park only",
            !staleRender && incomingRendered && outgoingRenders == 0 && !bumpRenderedOutgoing
            && freezeNoActivation && reclaimRenders == 1 && activationUnchanged,
            $"stale={staleRender} incoming={incomingRendered} outRenders={outgoingRenders} bumpOut={bumpRenderedOutgoing} freezeAct={freezeNoActivation} reclaim={reclaimRenders} onA={act.On.GetValueOrDefault("a")} offA={act.Off.GetValueOrDefault("a")}");
    }

    // gate.reconciler.unpark-replay-budget — the un-park replay is BUDGETED, not dumped into one flush.
    //
    // While a KeepAlive page is parked its components skip their render-effects and bank the debt (RunComponent sets
    // DeferredRender). Un-parking used to release ALL of it into the activation flush — measured 143 component renders
    // in a single 13.6 ms paint on an artist-page return — which janks the page-enter animation. SetSubtreeParked's
    // budgetReplays path now schedules at most K debtors (tree order) and queues the rest to drip one batch per frame,
    // drained from BeginRenderCensus (the host's per-paint reconciler tick, which runs before the frame's flush).
    //
    // Four properties, all load-bearing:
    //   (1) the activation flush renders ≤ K debtors (the jank fix),
    //   (2) every debtor still renders — exactly once, within a bounded number of frames (no lost render, no double),
    //   (3) an invalidation reaching a QUEUED debtor renders it IMMEDIATELY and cancels its queue slot: the drip may
    //       only carry components whose sole reason to run is the park debt,
    //   (4) once the drip has run, the page is fully live again — a signal write re-renders every debtor.
    //
    // (3) uses the IMPERATIVE route (RenderContext.RequestRerender == the entry's effect.Schedule) rather than a signal
    // write, because a signal write CANNOT reach a queued debtor: Computation.RunComputation unlinks its sources before
    // invoking the body, and the while-parked run returns early out of RunComponent having read nothing — so a parked
    // component ends up subscribed to NOTHING and stops being invalidated at all. That is the point of parking, and it
    // is exactly why the DeferredRender debt flag (not a subscription) is what guarantees fresh content on return; (4)
    // pins the other half — the replay re-tracks the sources, so the page is never left permanently deaf.
    static void UnparkReplayBudgetChecks(StringTable strings)
    {
        const int K = 24;        // mirrors Reconciler.UnparkReplaysPerFrame (private const — this gate keeps it honest)
        const int N = 70;        // > 2K so the drip genuinely spans ≥3 batches
        const int MaxFrames = 8; // bound: ceil(N/K) batches + slack. Never "eventually" — the drip must terminate.

        var scene = new SceneStore();
        var recon = new TreeReconciler(scene, strings);
        var route = new Signal<string>("a");
        var shared = new Signal<int>(0);                  // read by EVERY debtor — one write banks the whole debt
        var log = new List<int>();
        var instances = new List<UnparkDebtor>();         // mount order == tree order, so [N-1] is the LAST to be dripped

        recon.ReconcileRoot(
            Flow.KeepAlive(() => route.Value, k => k,
                k => k == "a"
                    ? Embed.Comp(() => new UnparkDebtorPage(shared, N, log, instances))
                    : (Element)new BoxEl { Width = 10f, Height = 10f },
                new KeepAliveOptions(MaxEntries: 2)),
            null);
        recon.Runtime.Flush();
        int mounted = log.Count;                          // N first renders at mount

        route.Value = "b";                                // park page "a" (it stays cached + mounted)
        recon.Runtime.Flush();
        shared.Value = 1;                                 // reaches all N parked debtors ⇒ N banked DeferredRender debts
        recon.Runtime.Flush();
        int whileParked = log.Count - mounted;            // must be 0 — parked components don't render

        log.Clear();
        route.Value = "a";                                // the un-park: budgeted replay
        recon.Runtime.Flush();
        int firstFlush = log.Count;

        // (3) An invalidation on a debtor still sitting in the queue must NOT wait its turn. Pick the LAST one — deepest
        // in the drip — and re-render it imperatively, with no frame tick, so no drip could possibly explain the render.
        int probe = N - 1;
        bool probeWasQueued = instances.Count == N && instances[probe].Index == probe && !log.Contains(probe);
        instances[probe].Context.RequestRerender();
        recon.Runtime.Flush();
        bool probeRanNow = log.Contains(probe);

        // (2) Tick frames the way AppHost does — FrameEpoch++ then BeginRenderCensus (drains) then the flush.
        int frames = 0;
        while (recon.HasDeferredReplays && frames < MaxFrames)
        {
            frames++;
            recon.FrameEpoch++;
            recon.BeginRenderCensus();
            recon.Runtime.Flush();
        }

        var seen = new HashSet<int>(log);
        int totalRenders = log.Count;
        bool everyDebtorRendered = seen.Count == N;
        bool renderedOnce = totalRenders == N;            // no double-render: the probe's live run CANCELLED its queue slot
        bool spread = firstFlush <= K && frames >= 2;     // budgeted, and it really did take multiple frames
        bool drained = !recon.HasDeferredReplays;

        // (4) The replayed page is fully live again: the replay re-tracked every debtor's sources, so one shared write
        // re-renders all N. (Before the replay the same write reaches nobody — see the header.)
        log.Clear();
        shared.Value = 2;
        recon.Runtime.Flush();
        bool liveAgain = log.Count == N;

        Check("gate.reconciler.unpark-replay-budget un-parking a KeepAlive page with N=70 banked render debts replays ≤24 in the activation flush and drips the rest one batch per frame until all 70 have rendered exactly once; an imperative re-render of a still-queued debtor runs immediately (cancelling its queue slot), and the replayed page is signal-live again",
            mounted == N && whileParked == 0 && spread && everyDebtorRendered && renderedOnce && drained
                && probeWasQueued && probeRanNow && liveAgain,
            $"mounted={mounted} whileParked={whileParked} firstFlush={firstFlush} (K={K}) dripFrames={frames} " +
            $"totalRenders={totalRenders} distinct={seen.Count}/{N} drained={drained} probeQueued={probeWasQueued} probeRanNow={probeRanNow} liveAgain={liveAgain}");
    }

    static void GalleryChecks(StringTable strings)
    {
        // gate.gallery.registry — resolve + the two-level (section → category → page) nav derivation the shell uses.
        {
            var reg = new RouteRegistry();
            reg.Add(new RouteDef("Button", _ => new BoxEl()) { Title = "Button", Category = "Basic input", Order = 1 });
            reg.Add(new RouteDef("Slider", _ => new BoxEl()) { Title = "Slider", Category = "Basic input", Order = 2 });
            reg.Add(new RouteDef("Image", _ => new BoxEl()) { Title = "Image", Category = "Media" });
            reg.Add(new RouteDef("flex", _ => new BoxEl()) { Title = "Flexbox", Category = "Fundamentals" });
            reg.Add(new RouteDef("state", _ => new BoxEl()) { Title = "State", Category = "Fundamentals" });

            var tree = reg.BuildSectionedNavTree(
                ("Controls", "IC", new[] { "Basic input", "Media" }),
                ("Fundamentals", "IF", new[] { "Fundamentals" }));

            // Controls section → two category subgroups (Basic input {Button,Slider by Order}, Media {Image}).
            bool controls = tree.Length == 2 && tree[0].Key == "Controls"
                && tree[0].Children is { Length: 2 } cc
                && cc[0].Key == "Basic input" && cc[0].Children is { Length: 2 } bi && bi[0].Key == "Button" && bi[1].Key == "Slider"
                && cc[1].Key == "Media" && cc[1].Children is { Length: 1 } md && md[0].Key == "Image";
            // Fundamentals section holds a same-named category → its pages FLATTEN as direct leaves (sorted by title).
            bool fundFlat = tree[1].Key == "Fundamentals" && tree[1].Children is { Length: 2 } fc
                && fc[0].Key == "flex" && fc[1].Key == "state";
            bool resolve = reg.Resolve("Button")?.Title == "Button" && reg.Resolve("zzz") is null;

            Check("gate.gallery.registry resolve + sectioned nav-tree derivation (categories nest under sections; flat section flattens)",
                controls && fundFlat && resolve, $"controls={controls} fundFlat={fundFlat} resolve={resolve}");
        }

        // gate.gallery.codeblock — the CodeBlock control renders tinted C# and RE-COLORS a keyword on a live theme swap.
        {
            ThemeKind saved = Tok.Theme;
            try
            {
                Tok.Use(ThemeKind.Light);
                using var app = new HeadlessPlatformApp();
                var window = new HeadlessWindow(new WindowDesc("codeblock", new Size2(420, 200), 1f));
                window.Show();
                var device = new HeadlessGpuDevice();
                var fonts = new HeadlessFontSystem(strings);
                using var host = new AppHost(app, window, device, fonts, strings, new CodeBlock { Code = "using x = 1;", Copyable = false });
                host.RunFrame();
                ColorF lightKw = GlyphColor(device, strings, "using");

                Tok.Use(ThemeKind.Dark);
                host.Reconciler.RethemeAll();
                host.RunFrame();
                ColorF darkKw = GlyphColor(device, strings, "using");

                bool rendered = lightKw != default(ColorF) && darkKw != default(ColorF);
                bool recolored = !ColorClose(lightKw, darkKw, 0.01f);
                Check("gate.gallery.codeblock renders tinted C# + re-colors keyword on theme swap",
                    rendered && recolored, $"rendered={rendered} recolored={recolored} light={lightKw} dark={darkKw}");
            }
            finally { Tok.Use(saved); }
        }
    }

    static void NavRouterChecks(StringTable strings)
    {
        // gate.nav.registry — pure: Add/Resolve/Fallback/duplicate-throw/BuildNavTree grouping/BuildSearchIndex.
        {
            var reg = new RouteRegistry();
            reg.Add(new RouteDef("a", _ => new BoxEl()) { Title = "Alpha", Icon = "IA", Category = "Group1", Order = 2 });
            reg.Add("b", "Beta", "IB", () => new BoxEl());                          // convenience overload; uncategorized
            reg.Add(new RouteDef("c", _ => new BoxEl()) { Title = "Gamma", Icon = "IC", Category = "Group1", Order = 1 });
            reg.Add(new RouteDef("d", _ => new BoxEl()) { Title = "Delta", Icon = "ID", Category = "Group2" });
            reg.Add(new RouteDef("hid", _ => new BoxEl()) { Title = "Hidden", ShowInNav = false });

            bool resolve = reg.Resolve("a")?.Title == "Alpha" && reg.Resolve("zzz") is null && reg.All.Count == 5;

            bool threw = false;
            try { reg.Add(new RouteDef("a", _ => new BoxEl())); } catch (InvalidOperationException) { threw = true; }

            reg.Fallback = r => new TextEl("FB:" + r.Name);
            bool fallbackSettable = reg.Fallback is not null;

            var tree = reg.BuildNavTree(("Group1", "G1I"), ("Group2", "G2I"));
            // Group1 first (children sorted by Order: c(1) then a(2)); Group2 (d); then top-level b. "hid" is excluded.
            bool g1 = tree.Length == 3 && tree[0].Key == "Group1" && tree[0].Glyph == "G1I"
                      && tree[0].Children is { Length: 2 } k1 && k1[0].Key == "c" && k1[1].Key == "a";
            bool g2 = tree[1].Key == "Group2" && tree[1].Children is { Length: 1 } k2 && k2[0].Key == "d";
            bool top = tree[2].Key == "b" && tree[2].Children is null;
            bool hiddenOut = true;
            foreach (var t in tree)
            {
                if (t.Key == "hid") hiddenOut = false;
                if (t.Children is { } ch) foreach (var c in ch) if (c.Key == "hid") hiddenOut = false;
            }

            var idx = reg.BuildSearchIndex();
            bool hasAlpha = false, hasHidden = false;
            foreach (var (label, key) in idx) { if (key == "a" && label == "Alpha") hasAlpha = true; if (key == "hid") hasHidden = true; }
            bool search = hasAlpha && !hasHidden;

            Check("gate.nav.registry Add/Resolve/Fallback/duplicate-throw/BuildNavTree/BuildSearchIndex",
                resolve && threw && fallbackSettable && g1 && g2 && top && hiddenOut && search,
                $"resolve={resolve} threw={threw} g1={g1} g2={g2} top={top} hiddenOut={hiddenOut} search={search}");
        }

        // gate.nav.route-gen — the generated Routes.RegisterAll registers a [Route] page with correct metadata, and an
        // argful ([string] ctor) page threads route.Arg through PageHost.
        {
            var reg = new RouteRegistry();
            FluentGpu.Generated.Routes.RegisterAll(reg);
            var plain = reg.Resolve("vs.route-gen.plain");
            bool meta = plain is { Title: "Plain Page", Icon: "P", Category: "RouteGen", Order: 7, KeepAlive: true };

            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("route-gen", new Size2(320, 240), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var nav = new Navigator(new Route("vs.route-gen.arg", "ZZZ"));
            using var host = new AppHost(app, window, device, fonts, strings, new PageHost(nav, reg));
            host.RunFrame();
            bool argRoutes = HasGlyph(device, strings, "VSGEN-ARG:ZZZ");

            Check("gate.nav.route-gen generated RegisterAll: page metadata + argful ctor threads route.Arg",
                meta && argRoutes, $"meta={meta} argRoutes={argRoutes}");
        }

        // gate.nav.pagehost-v2 — PageHost.Create resolves by key; unknown → Fallback; a KeepAlive route restores its
        // state on return, a non-KeepAlive route remounts fresh.
        {
            var reg = new RouteRegistry();
            reg.Add(new RouteDef("home", _ => Embed.Comp(() => new RouterProbePage("HOME"))));
            reg.Add(new RouteDef("ka", _ => Embed.Comp(() => new RouterProbePage("KA"))) { KeepAlive = true });
            reg.Add(new RouteDef("plain", _ => Embed.Comp(() => new RouterProbePage("PLAIN"))));
            reg.Fallback = r => new BoxEl { Children = [Text("FALLBACK:" + r.Name)] };

            bool createShape = PageHost.Create(new Navigator(new Route("home")), reg) is ComponentEl ce && ce.ComponentType == typeof(PageHost);

            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("pagehost-v2", new Size2(320, 240), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var nav = new Navigator(new Route("home"));
            using var host = new AppHost(app, window, device, fonts, strings, new PageHost(nav, reg));

            host.RunFrame();
            bool onHome = HasGlyph(device, strings, "HOME:0");

            nav.Replace(new Route("ka")); host.RunFrame();
            bool onKa = HasGlyph(device, strings, "KA:0");
            ClickNode(host, window, FindRole(host.Scene, host.Scene.Root, AutomationRole.Button));
            bool kaClicked = HasGlyph(device, strings, "KA:1");

            nav.Replace(new Route("home")); host.RunFrame();
            bool backHome = HasGlyph(device, strings, "HOME:0") && !HasGlyph(device, strings, "KA:1");

            nav.Replace(new Route("ka")); host.RunFrame();
            bool kaRestored = HasGlyph(device, strings, "KA:1");                    // KeepAlive kept the counter

            nav.Replace(new Route("plain")); host.RunFrame();
            ClickNode(host, window, FindRole(host.Scene, host.Scene.Root, AutomationRole.Button));
            bool plainClicked = HasGlyph(device, strings, "PLAIN:1");
            nav.Replace(new Route("home")); host.RunFrame();
            nav.Replace(new Route("plain")); host.RunFrame();
            bool plainFresh = HasGlyph(device, strings, "PLAIN:0") && !HasGlyph(device, strings, "PLAIN:1");   // non-KeepAlive remounts fresh

            nav.Replace(new Route("nope")); host.RunFrame();
            bool fallback = HasGlyph(device, strings, "FALLBACK:nope");

            Check("gate.nav.pagehost-v2 resolve-by-key/fallback/keepalive-restore/non-keepalive-fresh",
                createShape && onHome && onKa && kaClicked && backHome && kaRestored && plainClicked && plainFresh && fallback,
                $"create={createShape} home={onHome} ka={onKa} kaClick={kaClicked} back={backHome} kaRestored={kaRestored} plainFresh={plainFresh} fallback={fallback}");
        }

        // gate.nav.transition — an Entrance route gets Enter tokens on its root; Default too; None snaps (author owns motion).
        {
            var entrance = PageHost.WithTransition(new BoxEl(), NavTransition.Entrance);
            var standard = PageHost.WithTransition(new BoxEl(), NavTransition.Default);
            var none = PageHost.WithTransition(new BoxEl(), NavTransition.None);
            bool entranceEnter = entrance.Enter is { Active: true } && entrance.Transition is not null;
            bool standardEnter = standard.Enter is { Active: true } && standard.Transition is not null;
            bool noneBare = none.Enter is null && none.Transition is null;
            Check("gate.nav.transition Entrance/Default seed Enter tokens on the page root; None snaps",
                entranceEnter && standardEnter && noneBare,
                $"entrance={entranceEnter} standard={standardEnter} none={noneBare}");
        }
    }

    static void ActivationLifecycleChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("activation", new Size2(260, 220), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var probe = new ActivationProbe { MaxEntries = 4 };
        using var host = new AppHost(app, window, device, fonts, strings, probe);

        host.RunFrame();
        // Mount = active: neither callback fires; the page's loop animation keeps the Anim wake reason set.
        probe.On.TryGetValue("a", out int aOn0); probe.Off.TryGetValue("a", out int aOff0);
        bool mountSilent = aOn0 == 0 && aOff0 == 0;
        bool animAwake = (host.CurrentWakeReasons & WakeReasons.Anim) != 0;

        // Switch away → "a" parks → onDeactivated fires once (and its loop track is quiesced — see auto-quiesce below).
        probe.Route!.Value = "b"; host.RunFrame();
        probe.On.TryGetValue("a", out int aOn1); probe.Off.TryGetValue("a", out int aOff1);
        bool parkedFires = aOn1 == 0 && aOff1 == 1;

        // Switch back → "a" reactivates (onActivated); "b" parks (its onDeactivated).
        probe.Route.Value = "a"; host.RunFrame();
        probe.On.TryGetValue("a", out int aOn2); probe.Off.TryGetValue("a", out int aOff2);
        probe.Off.TryGetValue("b", out int bOff2);
        bool reactivateFires = aOn2 == 1 && aOff2 == 1 && bOff2 == 1;

        // Window minimize → the ACTIVE (un-parked) page goes inactive too (onDeactivated), then restore → onActivated.
        window.State = FluentGpu.Pal.WindowState.Minimized; host.RunFrame();
        probe.Off.TryGetValue("a", out int aOff3);
        bool minimizeFires = aOff3 == 2;
        window.State = FluentGpu.Pal.WindowState.Normal; host.RunFrame();
        probe.On.TryGetValue("a", out int aOn4);
        bool restoreFires = aOn4 == 2;

        Check("50b. UseActivation fires once per park/minimize transition, silent at mount (parked OR minimized → inactive)",
            mountSilent && animAwake && parkedFires && reactivateFires && minimizeFires && restoreFires,
            $"mountSilent={mountSilent} parked(off={aOff1}) reactivate(on={aOn2},bOff={bOff2}) min(off={aOff3}) restore(on={aOn4})");

        // Auto-quiesce: with the only live page being non-animated ("blank"), the parked pages' loop tracks no longer
        // keep the app awake (HasActive excludes parked tracks) → the Anim wake reason clears; it resumes on return.
        probe.Route.Value = "blank"; host.RunFrame();
        bool quiesced = (host.CurrentWakeReasons & WakeReasons.Anim) == 0;
        probe.Route.Value = "a"; host.RunFrame();
        bool resumed = (host.CurrentWakeReasons & WakeReasons.Anim) != 0;
        Check("50c. Auto-quiesce: a parked subtree's looping animation drops AnimEngine.HasActive (idle wake-stop), resumes on return",
            animAwake && quiesced && resumed, $"animAwake={animAwake} quiesced={quiesced} resumed={resumed}");
    }

    static (bool label, bool content, float rootW) NavAt(StringTable strings, int width, float scale = 1f)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("nav", new Size2(width, 700), scale));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new NavProbe());
        host.RunFrame();
        return (HasGlyph(device, strings, "Home"), HasGlyph(device, strings, "PAGE:home"), host.Scene.AbsoluteRect(host.Scene.Root).W);
    }

    static void NavigationViewChecks(StringTable strings)
    {
        var exp = NavAt(strings, 1200);   // ≥1008 → Expanded (labels visible)
        var comp = NavAt(strings, 760);   // 641..1008 → Compact (icon rail, no labels)
        var min = NavAt(strings, 520);    // <641 → Minimal (hamburger, no rail labels)
        var dpiComp = NavAt(strings, 1200, 1.5f);
        bool modes = exp.label && !comp.label && !min.label;
        bool content = exp.content && comp.content && min.content;
        Check("54. NavigationView adapts Expanded/Compact/Minimal by width", modes && content,
            $"labels exp={exp.label} comp={comp.label} min={min.label}; content={content}");
        Check("54a. AppHost lays out scaled windows in DIPs", !dpiComp.label && Near(dpiComp.rootW, 800f),
            $"rootW={dpiComp.rootW:0.#} label={dpiComp.label}");

        // 54c — a per-monitor DPI hop MID-SESSION (the WM_DPICHANGED path): EnsureSize watches scale as well as px
        // size, so a scale-only change re-lays-out in the new DIP viewport, and the suggested-rect resize restores it.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("dpihop", new Size2(1200, 700), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            using var host = new AppHost(app, window, device, fonts, strings, new NavProbe());
            host.RunFrame();
            bool labels1 = HasGlyph(device, strings, "Home");
            float w1 = host.Scene.AbsoluteRect(host.Scene.Root).W;          // 1200 DIP @1x → Expanded

            window.Scale = 1.5f;                                            // monitor hop, px not yet adjusted
            host.RunFrame();
            bool labels2 = HasGlyph(device, strings, "Home");
            float w2 = host.Scene.AbsoluteRect(host.Scene.Root).W;          // 800 DIP @1.5x → Compact

            window.ClientSizePx = new Size2(1800, 1050);                    // the OS-suggested rect at the new DPI
            host.RunFrame();
            bool labels3 = HasGlyph(device, strings, "Home");
            float w3 = host.Scene.AbsoluteRect(host.Scene.Root).W;          // 1200 DIP again → Expanded restored

            Check("54c. mid-session DPI change re-lays-out in the new DIP viewport (scale-only, then the suggested-rect resize)",
                labels1 && Near(w1, 1200f) && !labels2 && Near(w2, 800f) && labels3 && Near(w3, 1200f),
                $"w {w1:0}@1x → {w2:0}@1.5x (labels={labels2}) → {w3:0}@1.5x/1800px (labels={labels3})");
        }
    }

    static void NavigationViewAnimationChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("navanim", new Size2(1200, 700), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new NavProbe());
        host.RunFrame();

        NodeHandle FindTopLeftButton()
        {
            NodeHandle best = default;
            void Visit(NodeHandle n)
            {
                if (n.IsNull || !best.IsNull) return;
                var role = host.Scene.Interaction(n).Role;
                var r = host.Scene.AbsoluteRect(n);
                if (role == AutomationRole.Button && r.X < 64f && r.Y < 64f && r.W >= 36f && r.W <= 52f && r.H >= 36f && r.H <= 52f)
                {
                    best = n;
                    return;
                }
                for (var c = host.Scene.FirstChild(n); !c.IsNull; c = host.Scene.NextSibling(c)) Visit(c);
            }
            Visit(host.Scene.Root);
            return best;
        }

        // The content frame's presented LEFT edge slides 320 → 48 as the pane collapses. AbsoluteRect includes the
        // in-flight LocalTransform, so this reads the ANIMATING value (the model x snaps; the projection animates it).
        float ContentLeft()
        {
            float best = 1e9f;
            void Visit(NodeHandle n)
            {
                if (n.IsNull) return;
                var r = host.Scene.AbsoluteRect(n);
                if (r.W > 400f && r.H > 600f && r.X > 30f && r.X < 340f) best = MathF.Min(best, r.X);
                for (var c = host.Scene.FirstChild(n); !c.IsNull; c = host.Scene.NextSibling(c)) Visit(c);
            }
            Visit(host.Scene.Root);
            return best > 1e8f ? -1f : best;
        }

        float x0 = ContentLeft();                 // expanded: content frame at ~320
        var toggle = FindTopLeftButton();
        var tr = host.Scene.AbsoluteRect(toggle);
        var center = new Point2(tr.X + tr.W * 0.5f, tr.Y + tr.H * 0.5f);
        window.QueueInput(new InputEvent(InputKind.PointerDown, center, 0, 0));
        window.QueueInput(new InputEvent(InputKind.PointerUp, center, 0, 0));
        host.RunFrame();                          // reconcile: collapse → seed the content slide + label exits
        var compositorFrame = host.RunFrame();    // next frame advances the springs with NO reconcile / NO relayout
        bool compositorOnly = !compositorFrame.Rendered && host.Animation.HasActive;
        float x1 = ContentLeft();                 // mid-slide: strictly between 48 and 320
        for (int i = 0; i < 30; i++) host.RunFrame();
        float x2 = ContentLeft();                 // settled: ~48

        Check("54b. NavigationView collapse slides content via compositor-only projection (no re-render ticks)",
            !toggle.IsNull && x0 > 300f && x1 < x0 - 4f && x1 > 48f && Near(x2, 48f, 3f) && compositorOnly,
            $"contentX={x0:0}->{x1:0}->{x2:0} compositorOnly={compositorOnly}");
    }

    static void NavHierarchyChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("navhier", new Size2(1200, 700), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new NavHierarchyProbe());
        host.RunFrame();

        bool HasAccentPillBeside(NodeHandle row)
        {
            if (row.IsNull) return false;
            var rr = host.Scene.AbsoluteRect(row);
            bool found = false;
            void Visit(NodeHandle n)
            {
                if (n.IsNull || found) return;
                ref var p = ref host.Scene.Paint(n);
                var r = host.Scene.AbsoluteRect(n);
                if (ColorClose(p.Fill, Tok.AccentDefault, 0.02f)
                    && Near(r.W, 3f, 0.75f)
                    && Near(r.H, 16f, 0.75f)
                    && MathF.Abs((r.Y + r.H * 0.5f) - (rr.Y + rr.H * 0.5f)) < 4f
                    && r.X >= rr.X
                    && r.X <= rr.X + 14f)
                {
                    found = true;
                    return;
                }

                for (var c = host.Scene.FirstChild(n); !c.IsNull; c = host.Scene.NextSibling(c))
                    Visit(c);
            }

            Visit(host.Scene.Root);
            return found;
        }

        var items = new List<NodeHandle>();
        CollectRole(host.Scene, host.Scene.Root, AutomationRole.NavigationItem, items);
        int collapsedCount = items.Count;   // home, group (children hidden — group starts collapsed)

        // Click the group → its children appear.
        var groupCenter = CenterOf(host.Scene, items[1]);
        window.QueueInput(new InputEvent(InputKind.PointerDown, groupCenter, 0, 0));
        window.QueueInput(new InputEvent(InputKind.PointerUp, groupCenter, 0, 0));
        host.RunFrame();
        items.Clear();
        CollectRole(host.Scene, host.Scene.Root, AutomationRole.NavigationItem, items);
        int expandedCount = items.Count;    // home, group, c1, c2
        bool childrenAppeared = expandedCount == collapsedCount + 2;

        // Select the first child → content updates.
        bool childSelected = false;
        if (childrenAppeared)
        {
            // Settle the expand REFLOW, not just the ≤48ms enter stagger: the group-expand springs the later "After"
            // row from y=108 to y=180 (ItemReflowTransition, ~340ms critically damped). Hit-testing is transform-aware
            // (a real pointer hits what is visually under it), so clicking ChildOne's resting center while After is
            // still mid-flight hands the click to After (depth-first last-sibling-wins). ~24 frames ≈ 384ms clears it.
            for (int i = 0; i < 24; i++) host.RunFrame();
            ClickNode(host, window, items[2]);
            childSelected = HasGlyph(device, strings, "PAGE:c1");
        }

        // Collapse the expanded pane to the icon rail while a child is selected. WinUI keeps the hierarchical child
        // selection in the model, but the closed compact rail shows only top-level containers and paints the selected
        // child indication on the visible parent chain.
        bool compactRailRootOnly = false;
        bool compactRailParentChrome = false;
        bool compactRailKeepsChildPage = false;
        bool reopenedStillExpanded = false;
        var buttons = Roles(host.Scene, AutomationRole.Button);
        if (childSelected && buttons.Count > 0)
        {
            ClickNode(host, window, buttons[0]);
            for (int i = 0; i < 24; i++) host.RunFrame();

            items.Clear();
            CollectRole(host.Scene, host.Scene.Root, AutomationRole.NavigationItem, items);
            compactRailRootOnly = items.Count == collapsedCount;
            compactRailKeepsChildPage = HasGlyph(device, strings, "PAGE:c1");
            if (items.Count > 1)
            {
                compactRailParentChrome = ColorClose(host.Scene.Paint(items[1]).Fill, Tok.FillSubtleSecondary, 0.02f)
                    && HasAccentPillBeside(items[1]);
            }

            buttons = Roles(host.Scene, AutomationRole.Button);
            if (buttons.Count > 0)
            {
                ClickNode(host, window, buttons[0]);
                for (int i = 0; i < 4; i++) host.RunFrame();
                items.Clear();
                CollectRole(host.Scene, host.Scene.Root, AutomationRole.NavigationItem, items);
                reopenedStillExpanded = items.Count == expandedCount;
            }
        }

        // Click the group again → it collapses (children disappear).
        items.Clear();
        CollectRole(host.Scene, host.Scene.Root, AutomationRole.NavigationItem, items);
        var g2 = CenterOf(host.Scene, items[1]);
        window.QueueInput(new InputEvent(InputKind.PointerDown, g2, 0, 0));
        window.QueueInput(new InputEvent(InputKind.PointerUp, g2, 0, 0));
        host.RunFrame();
        items.Clear();
        CollectRole(host.Scene, host.Scene.Root, AutomationRole.NavigationItem, items);
        bool collapsedAgain = items.Count == collapsedCount;
        var afterText = FindTextNode(host.Scene, strings, host.Scene.Root, "After");
        var afterLabel = afterText.IsNull ? NodeHandle.Null : host.Scene.Parent(afterText);
        var afterRow = afterLabel.IsNull ? NodeHandle.Null : host.Scene.Parent(afterLabel);
        float afterLabelDy = afterLabel.IsNull ? 0f : host.Scene.Paint(afterLabel).LocalTransform.Dy;
        bool labelNotProjected = !afterLabel.IsNull && MathF.Abs(afterLabelDy) < 0.01f;
        bool rowOwnsMotion = !afterRow.IsNull && host.Animation.HasTracks(afterRow);

        Check("65. NavigationView: group expands/collapses + child selection updates content",
            collapsedCount == 3 && childrenAppeared && childSelected && collapsedAgain,
            $"collapsed={collapsedCount} expanded={expandedCount} childPage={childSelected} recollapsed={collapsedAgain}");
        Check("65a. NavigationView: hierarchy reflow motion is owned by the whole row, not the label",
            collapsedAgain && rowOwnsMotion && labelNotProjected,
            $"rowTracks={rowOwnsMotion} labelDy={afterLabelDy:0.###}");
        Check("65a2. NavigationView: closed icon rail hides child rows and maps child selection chrome to parent",
            compactRailRootOnly && compactRailParentChrome && compactRailKeepsChildPage && reopenedStillExpanded,
            $"rootOnly={compactRailRootOnly} parentChrome={compactRailParentChrome} childPage={compactRailKeepsChildPage} reopenExpanded={reopenedStillExpanded}");
    }
}

// ── gate.reconciler.unpark-replay-budget ────────────────────────────────────────────────────────────────────────────
// A KeepAlive page holding many independently-reactive children — the shape that banks a large render debt while parked
// (an artist page: a per-frame overlay, cover shimmers, chart rows, shelves). Every debtor tracks ONE shared channel, so
// a single write while parked leaves all N owing a render. Each registers itself at construction, in mount (== tree)
// order, so the gate can reach the LAST one — the deepest in the drip queue — and re-render it imperatively.
sealed class SemanticZoomItemsProbe : Component
{
    public const float RowExtent = 24f;

    public readonly SemanticZoomController Zoom = new();
    public readonly ItemsViewController InItems = new();
    public readonly ItemsViewController OutItems = new();
    public readonly List<SemanticZoomViewChange> Started = [];
    public readonly List<SemanticZoomViewChange> Completed = [];
    private readonly bool _noAnchor;

    public SemanticZoomItemsProbe(bool noAnchor = false) => _noAnchor = noAnchor;

    public override Element Render()
    {
        Element List(string prefix, ItemsViewController controller, string scrollKey)
            => ItemsView.Create(
                160,
                i => new BoxEl
                {
                    Height = RowExtent,
                    Children = [new TextEl(prefix + i)],
                },
                RepeatLayout.Stack(RowExtent),
                new ListOptions
                {
                    SelectionMode = ItemsSelectionMode.None,
                    Selector = SelectorVisual.None,
                    Controller = controller,
                    Scroll = new ScrollOptions { ScrollKey = scrollKey, SuppressScrollBar = true },
                });

        Func<int, int>? map = _noAnchor ? static _ => -1 : null;
        return SemanticZoom.Create(
            new SemanticZoomSlots(
                new SemanticZoomView(List("zoom-in-", InItems, "semantic-gate-in"), InItems),
                new SemanticZoomView(List("zoom-out-", OutItems, "semantic-gate-out"), OutItems)),
            new SemanticZoomOptions
            {
                MapInToOut = map,
                MapOutToIn = map,
                ViewChangeStarted = Started.Add,
                ViewChangeCompleted = Completed.Add,
                Controller = Zoom,
            });
    }
}

sealed class UnparkDebtorPage : Component
{
    readonly Signal<int> _shared;
    readonly int _count;
    readonly List<int> _log;
    readonly List<UnparkDebtor> _instances;
    public UnparkDebtorPage(Signal<int> shared, int count, List<int> log, List<UnparkDebtor> instances)
    { _shared = shared; _count = count; _log = log; _instances = instances; }

    public override Element Render()
    {
        var kids = new Element[_count];
        for (int i = 0; i < _count; i++)
        {
            int idx = i;   // capture per child — Embed.Comp freezes it at mount
            kids[i] = Embed.Comp(() => new UnparkDebtor(idx, _shared, _log, _instances));
        }
        return new BoxEl { Children = kids };
    }
}

sealed class UnparkDebtor : Component
{
    readonly int _index;
    readonly Signal<int> _shared;
    readonly List<int> _log;
    public int Index => _index;
    public UnparkDebtor(int index, Signal<int> shared, List<int> log, List<UnparkDebtor> instances)
    { _index = index; _shared = shared; _log = log; instances.Add(this); }

    public override Element Render()
    {
        _ = _shared.Value;   // the channel that banks the park debt for the whole page (and is re-tracked by the replay)
        _log.Add(_index);
        return new BoxEl { Width = 8f, Height = 8f };
    }
}
