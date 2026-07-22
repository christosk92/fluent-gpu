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
