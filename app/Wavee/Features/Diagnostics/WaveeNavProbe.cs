using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Windows;
using FluentGpu.Rhi;
using FluentGpu.Rhi.D3D12;
using Wavee.Core;

namespace Wavee;

// WAVEE_NAV_PROBE=1: a scripted navigation / scroll / theme / back-forward stress harness. Installed via
// FluentApp.DiagnosticRun, it takes over the run loop and drives REAL app navigation through WaveeShell.Probe* hooks,
// recording the per-frame PRODUCTION time of every painted frame. To measure the engine's true throughput each frame
// suppresses the swapchain frame-latency wait AND vsync (WAVEE_PROBE_VSYNC=1 keeps them — proves a present artifact).
//
// "Butter smooth" target on a 120 Hz panel = every frame's WORK under one vblank (8.33 ms). A frame over budget misses
// a vblank in real (vsync-paced) use → a visible stutter. So the headline metric is "frames over 8.33 ms" — and each is
// split into WORK spikes vs GC spikes (a Gen0+ collection fired on that frame), because the two need different fixes.
internal static class WaveeNavProbe
{
    const double BudgetMs = 1000.0 / 120.0;   // one 120 Hz vblank — the smoothness bar

    [System.Runtime.InteropServices.DllImport("user32", ExactSpelling = true)]
    static extern nint SendMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    // Heavy pages: HomePage (image cards), Liked (big track list), real playlists (40 tracks + cover). Cheap: placeholders.
    static readonly (string Key, string? Arg)[] HeavyRoutes =
    [
        ("home", null),
        ("liked", null),
        ("pl:spotify:playlist:pl0", "Playlist 0"),
        ("pl:spotify:playlist:pl1", "Playlist 1"),
        ("pl:spotify:playlist:pl2", "Playlist 2"),
        ("pl:spotify:playlist:pl3", "Playlist 3"),
        ("pl:spotify:playlist:pl4", "Playlist 4"),
        ("pl:spotify:playlist:pl5", "Playlist 5"),
    ];
    static readonly (string Key, string? Arg)[] CheapRoutes =
    [
        ("albums", null), ("artists", null), ("podcasts", null), ("local", null), ("search", null),
    ];

    public static bool TryRun(AppHost host, IPlatformWindow window, IGpuDevice device)
    {
        bool connStress = Diag.EnvFlag("WAVEE_CONN_STRESS");
        bool trackShot = Diag.EnvFlag("WAVEE_TRACKLIST_SHOT");
        bool heroShot = Diag.EnvFlag("WAVEE_HERO_SHOT");
        bool shelfShot = Diag.EnvFlag("WAVEE_SHELF_SHOT");
        bool railShot = Diag.EnvFlag("WAVEE_RAIL_SHOT");
        bool railProbe = Diag.EnvFlag("WAVEE_RAIL_PROBE");
        bool homeScroll = Diag.EnvFlag("WAVEE_HOME_SCROLL_PROBE");
        bool lyricsProbe = Diag.EnvFlag("WAVEE_LYRICS_PROBE");
        bool liveLyricsScroll = Diag.EnvFlag("WAVEE_LIVE_LYRICS_SCROLL_PROBE");
        bool advanceProbe = Diag.EnvFlag("WAVEE_LYRICS_ADVANCE_PROBE");
        if (!Diag.EnvFlag("WAVEE_NAV_PROBE") && !connStress && !trackShot && !heroShot && !shelfShot && !railShot && !railProbe && !homeScroll && !lyricsProbe && !liveLyricsScroll && !advanceProbe) return false;
        if (window is not Win32Window w || device is not D3D12Device gpu)
        {
            Console.Error.WriteLine("[wavee-nav-probe] unavailable: requires Win32Window + D3D12Device");
            return true;
        }
        // DiagnosticRun fires straight after window.Show(), BEFORE the first frame — so WaveeShell hasn't mounted yet
        // and ProbeNav is still null. Pump warmup frames until the shell wires its nav hook (fixes the mount race that
        // made earlier shot runs flakily report "nav hook not wired").
        int hookFrames = (liveLyricsScroll || advanceProbe || heroShot || shelfShot || railShot) ? Math.Max(240, EnvInt("WAVEE_PROBE_AUTH_FRAMES", 7200, 240, 36000)) : 240;
        for (int i = 0; i < hookFrames && WaveeShell.ProbeNav is null && !w.IsClosed; i++)
        {
            if (liveLyricsScroll || advanceProbe || heroShot || shelfShot || railShot)
            {
                host.RunFrame();
                w.WaitForWork(Math.Min(host.RecommendedWaitMs(), 16));
            }
            else
            {
                gpu.SuppressLatencyWaitOnce();
                gpu.SuppressVsyncOnce();
                host.RunFrame();
            }
        }
        if (WaveeShell.ProbeNav is null)
        {
            Console.Error.WriteLine("[wavee-nav-probe] nav hook not wired (WaveeShell not mounted?)");
            return true;
        }
        if (heroShot) RunHeroCollapseShot(host, w, gpu);
        else if (railProbe) RunRailProbe(host, w, gpu);
        else if (railShot) RunRailShot(host, w, gpu);
        else if (shelfShot) RunShelfFadeShot(host, w, gpu);
        else if (trackShot) RunTrackListShot(host, w, gpu);
        else if (connStress) RunConnStress(host, w, gpu);
        else if (homeScroll) RunHomeScrollProbe(host, w, gpu);
        else if (liveLyricsScroll) RunLiveLyricsScrollProbe(host, w, gpu);
        else if (advanceProbe) RunLyricsAdvanceProbe(host, w, gpu);
        else if (lyricsProbe) RunLyricsProbe(host, w, gpu);
        else Run(host, w, gpu);
        return true;
    }

    // WAVEE_TRACKLIST_SHOT=1: navigate to each surface that shows tracks and capture a PNG, so the unified track row (the
    // shared TrackRow cell) can be eyeballed for 1:1 parity — a detail playlist, the Library albums pane, an artist page
    // ("Popular"), and search results ("Songs"). Output → C:\tmp\tl_*.png.
    static void RunTrackListShot(AppHost host, Win32Window window, D3D12Device gpu)
    {
        try { System.IO.Directory.CreateDirectory(@"C:\tmp"); } catch { }
        void Frame() { if (!window.IsClosed) { gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce(); host.RunFrame(); } }
        void Nav(string k, string? a) => WaveeShell.ProbeNav!(k, a);
        void Settle(int n) { for (int i = 0; i < n && !window.IsClosed; i++) Frame(); }
        void Shot(string name)
        {
            var px = gpu.CaptureBgra(out int cw, out int ch);
            PngWriter.WriteBgra($@"C:\tmp\tl_{name}.png", px, cw, ch);
            Console.Error.WriteLine($"[tracklist-shot] wrote tl_{name}.png ({cw}x{ch})");
        }

        Nav("home", null); Settle(40); System.Threading.Thread.Sleep(700); Settle(20);   // home live + card covers decode

        // 1) Detail playlist (the canonical full list — must be unchanged by the cell extraction).
        Nav("pl:spotify:playlist:pl0", "Playlist 0"); Settle(40); System.Threading.Thread.Sleep(700); Settle(60);
        var detailVp = FindLargestScrollViewport(host.Scene);
        if (!detailVp.IsNull && host.Scene.TryGetScroll(detailVp, out var detailBefore))
        {
            var rr = host.Scene.AbsoluteRect(detailVp);
            var pt = new Point2(rr.X + rr.W * 0.5f, rr.Y + rr.H * 0.5f);
            var routed = host.Input.ScrollableUnderForAxis(pt, wantHorizontal: false);
            var hit = host.Input.DiagHitTest(pt);
            window.QueueInput(new InputEvent(InputKind.PointerMove, pt, 0, 0));
            window.QueueInput(new InputEvent(InputKind.Wheel, pt, 0, 0, 240f));
            Settle(30);
            host.Scene.TryGetScroll(detailVp, out var detailAfter);
            Console.Error.WriteLine($"[tracklist-shot] detail-scroll expected=n#{detailVp.Raw.Index} hit=n#{hit.Raw.Index} routed=n#{routed.Raw.Index} " +
                $"rect=({rr.X:0},{rr.Y:0} {rr.W:0}x{rr.H:0}) view={detailBefore.ViewportH:0} content={detailBefore.ContentH:0} " +
                $"offset={detailBefore.OffsetY:0}->{detailAfter.OffsetY:0} phase={detailAfter.Phase} pending={detailAfter.PendingTargetY:0} " +
                $"result={(MathF.Abs(detailAfter.OffsetY - detailBefore.OffsetY) > 1f ? "PASS" : "FAIL")}");
            if (routed.IsNull)
            {
                Console.Error.WriteLine("[tracklist-shot]   expected-chain:");
                for (var n = detailVp; !n.IsNull; n = host.Scene.Parent(n))
                {
                    var nr = host.Scene.AbsoluteRect(n);
                    var nf = host.Scene.Flags(n);
                    Console.Error.WriteLine($"[tracklist-shot]     n#{n.Raw.Index} type={host.Scene.ElementTypeId(n)} " +
                        $"rect=({nr.X:0},{nr.Y:0} {nr.W:0}x{nr.H:0}) flags={nf} scroll={host.Scene.HasScroll(n)}");
                }
                Console.Error.WriteLine("[tracklist-shot]   hit-chain:");
                for (var n = hit; !n.IsNull; n = host.Scene.Parent(n))
                {
                    var nr = host.Scene.AbsoluteRect(n);
                    var nf = host.Scene.Flags(n);
                    Console.Error.WriteLine($"[tracklist-shot]   hit-chain n#{n.Raw.Index} type={host.Scene.ElementTypeId(n)} " +
                        $"rect=({nr.X:0},{nr.Y:0} {nr.W:0}x{nr.H:0}) flags={nf} scroll={host.Scene.HasScroll(n)}");
                }
            }
        }
        else Console.Error.WriteLine("[tracklist-shot] detail-scroll MISSING");
        Shot("detail");

        // 2) Album detail uses an OUTER scroll viewport around its eager track rows + trailing shelves. Probe the exact
        // wheel route twice (including focus loss/reactivation): an inactive full-bleed selection overlay used to win
        // HitTestAny here even though ordinary row clicks still worked.
        var probeAlbum = Wavee.Core.FakeData.Album(120);
        Nav("album:" + probeAlbum.Uri, probeAlbum.Name); Settle(50); System.Threading.Thread.Sleep(800); Settle(80);
        var albumVp = FindLargestScrollViewport(host.Scene);
        if (!albumVp.IsNull && host.Scene.TryGetScroll(albumVp, out var albumBefore))
        {
            var rr = host.Scene.AbsoluteRect(albumVp);
            var pt = new Point2(rr.X + rr.W * 0.5f, rr.Y + MathF.Min(rr.H, albumBefore.ViewportH) * 0.5f);
            var routed = host.Input.ScrollableUnderForAxis(pt, wantHorizontal: false);
            window.QueueInput(new InputEvent(InputKind.PointerMove, pt, 0, 0));
            window.QueueInput(new InputEvent(InputKind.Wheel, pt, 0, 0, 240f));
            Settle(30);
            host.Scene.TryGetScroll(albumVp, out var albumAfter);

            window.QueueInput(new InputEvent(InputKind.WindowBlur, default, 0, 0)); Settle(2);
            window.QueueInput(new InputEvent(InputKind.WindowFocus, default, 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerMove, pt, 0, 0));
            float beforeFocusWheel = albumAfter.OffsetY;
            window.QueueInput(new InputEvent(InputKind.Wheel, pt, 0, 0, 240f));
            Settle(30);
            host.Scene.TryGetScroll(albumVp, out var albumFocused);
            var routedFocused = host.Input.ScrollableUnderForAxis(pt, wantHorizontal: false);
            Console.Error.WriteLine($"[tracklist-shot] album-scroll expected=n#{albumVp.Raw.Index} routed=n#{routed.Raw.Index}/n#{routedFocused.Raw.Index} " +
                $"rect=({rr.X:0},{rr.Y:0} {rr.W:0}x{rr.H:0}) view={albumBefore.ViewportH:0} content={albumBefore.ContentH:0} " +
                $"offset={albumBefore.OffsetY:0}->{albumAfter.OffsetY:0}->{albumFocused.OffsetY:0} " +
                $"initial={(MathF.Abs(albumAfter.OffsetY - albumBefore.OffsetY) > 1f ? "PASS" : "FAIL")} " +
                $"reactivate={(MathF.Abs(albumFocused.OffsetY - beforeFocusWheel) > 1f ? "PASS" : "FAIL")}");
        }
        else Console.Error.WriteLine("[tracklist-shot] album-scroll MISSING");
        Shot("album");

        // Bundled Spotify collection art + the intentional no-layer exception on the Liked Songs rail.
        Nav("liked", null); Settle(40); System.Threading.Thread.Sleep(500); Settle(50); Shot("liked");

        // 3) Library albums → the pane auto-selects the first album → its tracks render via the embedded TrackList.
        Nav("albums", null); Settle(50); System.Threading.Thread.Sleep(800); Settle(80); Shot("library");
        // 4) Artist page → "Popular" top-tracks (synthesized for any artist uri).
        Nav("artist:spotify:artist:ar0", "Artist"); Settle(50); System.Threading.Thread.Sleep(700); Settle(60); Shot("artist");
        Nav("artist:spotify:artist:04gDigrS5kc9YWfZHwBETP", "Maroon 5"); Settle(60); System.Threading.Thread.Sleep(2500); Settle(120); Shot("artist_maroon5");
        // 5) Search → the "Songs" rows in the All view.
        Nav("search", "the"); Settle(50); System.Threading.Thread.Sleep(700); Settle(60); Shot("search");

        Console.Error.WriteLine("[tracklist-shot] done");
    }

    // WAVEE_RAIL_SHOT=1: open the right rail in each mode (Details / Lyrics / Queue) and capture PNGs — the rail
    // background, the content↔rail seam, and the bottom corner treatment. Output → C:\tmp\rail_*.png.
    static void RunRailShot(AppHost host, Win32Window window, D3D12Device gpu)
    {
        try { System.IO.Directory.CreateDirectory(@"C:\tmp"); } catch { }
        window.SetClientSize(1700, 950);   // wide → the rail DOCKS (RailFits) — the mode the reports are about
        void Frame() { if (!window.IsClosed) { gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce(); host.RunFrame(); } }
        void Settle(int n) { for (int i = 0; i < n && !window.IsClosed; i++) Frame(); }
        void Shot(string name)
        {
            var px = gpu.CaptureBgra(out int cw, out int ch);
            PngWriter.WriteBgra($@"C:\tmp\rail_{name}.png", px, cw, ch);
            Console.Error.WriteLine($"[rail-shot] wrote rail_{name}.png ({cw}x{ch})");
        }

        WaveeShell.ProbeNav!("home", null); Settle(60); System.Threading.Thread.Sleep(2500); Settle(120);
        // Open-transition frames: the "background only shows a couple frames later" report — capture the first frames
        // of the width animation, then steady.
        WaveeShell.ProbeRail!(2 /*Details*/);
        for (int f = 2; f <= 12; f += 2) { Settle(2); Shot($"details_open{f:D2}"); }
        Settle(48); System.Threading.Thread.Sleep(800); Settle(60); Shot("details");
        WaveeShell.ProbeRail!(0 /*Lyrics*/); Settle(60); System.Threading.Thread.Sleep(800); Settle(60); Shot("lyrics");
        WaveeShell.ProbeRail!(1 /*Queue*/); Settle(60); System.Threading.Thread.Sleep(500); Settle(60); Shot("queue");
        Console.Error.WriteLine("[rail-shot] done");
    }

    // WAVEE_SHELF_SHOT=1: navigate home and force a horizontal PagedShelf strip to a MID-PAGE offset (what a touchpad
    // free-pan leaves behind), then capture PNGs — the LEFT edge fade must appear once content extends past the left
    // edge, and the left chevron must re-enable (the page state re-syncs from the settled offset). Output → C:\tmp\shelf_*.png.
    static void RunShelfFadeShot(AppHost host, Win32Window window, D3D12Device gpu)
    {
        string shotDir = Path.Combine(Environment.CurrentDirectory, ".tmp", "shelf-shot-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        try { Directory.CreateDirectory(shotDir); } catch { }
        window.SetClientSize(1280, 900);
        FrameStats lastFrame = default;
        int maxEdgeFadeGroups = 0;
        void Frame()
        {
            if (window.IsClosed) return;
            gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce();
            lastFrame = host.RunFrame();
            maxEdgeFadeGroups = Math.Max(maxEdgeFadeGroups, lastFrame.EdgeFadeGroupCount);
        }
        void Settle(int n) { for (int i = 0; i < n && !window.IsClosed; i++) Frame(); }
        void Shot(string name)
        {
            var px = gpu.CaptureBgra(out int cw, out int ch);
            string path = Path.Combine(shotDir, $"shelf_{name}.png");
            PngWriter.WriteBgra(path, px, cw, ch);
            Console.Error.WriteLine($"[shelf-shot] wrote {path} ({cw}x{ch}); edgeFadeGroups={lastFrame.EdgeFadeGroupCount}");
        }

        WaveeShell.ProbeNav!("home", null); Settle(60); System.Threading.Thread.Sleep(1500); Settle(60);
        WaveeShell.ProbeNav!("home", null); Settle(60); System.Threading.Thread.Sleep(2000); Settle(120);

        var scene = host.Scene;
        // Drive the REAL input/integrator path. The old diagnostic mutated ScrollState directly, bypassed the dispatcher
        // transform writer, and captured a stale back buffer — it could claim the offset moved while the shelf stayed
        // off-screen. First wheel the Home viewport until a shelf is actually visible.
        var homeVp = FindScrollViewportByKey(scene, "home");
        if (homeVp.IsNull || !scene.TryGetScroll(homeVp, out var homeBefore))
        { Console.Error.WriteLine("[shelf-shot] Home viewport missing — aborting"); return; }
        var homeRect = scene.AbsoluteRect(homeVp);
        var pagePoint = new Point2(homeRect.X + homeRect.W * 0.5f, homeRect.Y + homeBefore.ViewportH * 0.5f);
        window.QueueInput(new InputEvent(InputKind.PointerMove, pagePoint, 0, 0));
        window.QueueInput(new InputEvent(InputKind.Wheel, pagePoint, 0, 0, 520f));
        for (int i = 0; i < 90 && !window.IsClosed; i++) { System.Threading.Thread.Sleep(4); Frame(); }
        scene.TryGetScroll(homeVp, out var homeAfter);
        Console.Error.WriteLine($"[shelf-shot] Home offset {homeBefore.OffsetY:0}->{homeAfter.OffsetY:0}");

        NodeHandle shelfVp = NodeHandle.Null;
        float bestVisibleY = float.MaxValue;
        var st = new Stack<NodeHandle>(); if (!scene.Root.IsNull) st.Push(scene.Root);
        while (st.Count > 0)
        {
            var n = st.Pop();
            if (n.IsNull || !scene.IsLive(n)) continue;
            if (scene.HasScroll(n) && scene.TryGetScroll(n, out var s) && s.Orientation == 1
                && s.ContentW > s.ViewportW + 40f && s.AutoEdgeFade)
            {
                var r = scene.AbsoluteRect(n);
                bool visible = r.Bottom > homeRect.Y + 20f && r.Y < homeRect.Bottom - 20f && r.W > 400f;
                Console.Error.WriteLine($"[shelf-shot]   h-scroller rect=({r.X:0},{r.Y:0} {r.W:0}x{r.H:0}) viewW={s.ViewportW:0} contentW={s.ContentW:0} offX={s.OffsetX:0} visible={visible}");
                if (visible && MathF.Abs(r.Y - (homeRect.Y + 180f)) < bestVisibleY)
                { bestVisibleY = MathF.Abs(r.Y - (homeRect.Y + 180f)); shelfVp = n; }
            }
            for (var c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c)) st.Push(c);
        }
        if (shelfVp.IsNull || !scene.TryGetScroll(shelfVp, out var shelfBefore))
        { Console.Error.WriteLine("[shelf-shot] no visible horizontal shelf found — aborting"); return; }

        var shelfRect = scene.AbsoluteRect(shelfVp);
        var shelfPoint = new Point2(shelfRect.X + shelfRect.W * 0.5f, shelfRect.Y + MathF.Min(shelfRect.H, shelfBefore.ViewportH) * 0.5f);
        var routedBefore = host.Input.ScrollableUnderForAxis(shelfPoint, wantHorizontal: true);
        Shot("before");                         // start edge: right fade only
        maxEdgeFadeGroups = 0;
        window.QueueInput(new InputEvent(InputKind.PointerMove, shelfPoint, 0, 0));
        window.QueueInput(new InputEvent(InputKind.Wheel, shelfPoint, 0, 0, ScrollDeltaX: MathF.Min(300f, shelfBefore.ContentW - shelfBefore.ViewportW)));
        for (int i = 0; i < 90 && !window.IsClosed; i++) { System.Threading.Thread.Sleep(4); Frame(); }
        scene.TryGetScroll(shelfVp, out var shelfAfter);
        var routedAfter = host.Input.ScrollableUnderForAxis(shelfPoint, wantHorizontal: true);
        Shot("after");                          // mid strip: left + right fades
        Console.Error.WriteLine($"[shelf-shot] shelf expected=n#{shelfVp.Raw.Index} routed=n#{routedBefore.Raw.Index}/n#{routedAfter.Raw.Index} " +
            $"offX={shelfBefore.OffsetX:0}->{shelfAfter.OffsetX:0} maxEdgeFadeGroups={maxEdgeFadeGroups} " +
            $"result={(shelfAfter.OffsetX > shelfBefore.OffsetX + 1f && maxEdgeFadeGroups > 0 ? "PASS" : "FAIL")}");

        // Artist page: the measured-virtual shelves ("Appears on" / "Fans also like" / concerts / merch / gallery) —
        // the probe-stuck regression left them as blank reserved bands. Scroll deep and capture the shelf zone.
        WaveeShell.ProbeNav!("artist:spotify:artist:04gDigrS5kc9YWfZHwBETP", "Maroon 5");
        Settle(60); System.Threading.Thread.Sleep(2500); Settle(120);
        NodeHandle pageVp = NodeHandle.Null; float bestC = 0f;
        var st2 = new Stack<NodeHandle>(); if (!scene.Root.IsNull) st2.Push(scene.Root);
        while (st2.Count > 0)
        {
            var n = st2.Pop();
            if (n.IsNull || !scene.IsLive(n)) continue;
            if (scene.HasScroll(n) && scene.TryGetScroll(n, out var s) && s.Orientation != 1 && s.ContentH > s.ViewportH + 1f && s.ContentH > bestC)
            { bestC = s.ContentH; pageVp = n; }
            for (var c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c)) st2.Push(c);
        }
        if (!pageVp.IsNull)
        {
            for (float frac = 0.30f; frac < 1.01f; frac += 0.10f)
            {
                ref FluentGpu.Scene.ScrollState sc = ref scene.ScrollRef(pageVp);
                float t = MathF.Max(0f, (sc.ContentH - sc.ViewportH) * frac);
                sc.OffsetY = t; sc.TargetY = t;
                NodeHandle content = sc.ContentNode;
                if (!content.IsNull && scene.IsLive(content))
                {
                    scene.Paint(content).LocalTransform = FluentGpu.Foundation.Affine2D.Translation(0f, -t);
                    scene.Mark(content, FluentGpu.Foundation.NodeFlags.TransformDirty | FluentGpu.Foundation.NodeFlags.PaintDirty);
                }
                Settle(30);
                Shot($"artist_{(int)(frac * 100):D2}");
            }
        }
        Console.Error.WriteLine("[shelf-shot] done");
    }

    // WAVEE_HERO_SHOT=1: navigate to an artist page and capture the collapsing hero at several scroll offsets, dumping the
    // collapse geometry (pin shift / presented height / child shift / parent height) so it can be verified numerically.
    // Output → C:\tmp\hero_{offset}.png + a stderr [hero-geom] dump.
    static void RunHeroCollapseShot(AppHost host, Win32Window window, D3D12Device gpu)
    {
        try { System.IO.Directory.CreateDirectory(@"C:\tmp"); } catch { }
        window.SetClientSize(1280, 900);
        void Frame() { if (!window.IsClosed) { gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce(); host.RunFrame(); } }
        void Settle(int n) { for (int i = 0; i < n && !window.IsClosed; i++) Frame(); }
        void Shot(int off)
        {
            var px = gpu.CaptureBgra(out int cw, out int ch);
            PngWriter.WriteBgra($@"C:\tmp\hero_{off:D3}.png", px, cw, ch);
            Console.Error.WriteLine($"[hero-shot] wrote hero_{off:D3}.png ({cw}x{ch})");
        }

        WaveeShell.ProbeNav!("home", null); Settle(40); System.Threading.Thread.Sleep(700); Settle(20);
        WaveeShell.ProbeNav!("artist:spotify:artist:04gDigrS5kc9YWfZHwBETP", "Maroon 5");
        Settle(50); System.Threading.Thread.Sleep(2500); Settle(120);

        // List every scroller so a too-strict pick is obvious, then take the most-scrollable by content extent (lenient).
        NodeHandle vp = NodeHandle.Null; float bestContent = 0f;
        {
            var st = new Stack<NodeHandle>(); if (!host.Scene.Root.IsNull) st.Push(host.Scene.Root);
            while (st.Count > 0)
            {
                var n = st.Pop();
                if (n.IsNull || !host.Scene.IsLive(n)) continue;
                if (host.Scene.HasScroll(n) && host.Scene.TryGetScroll(n, out var s))
                {
                    var r = host.Scene.AbsoluteRect(n);
                    Console.Error.WriteLine($"[hero-shot]   scroller rect=({r.X:0},{r.Y:0} {r.W:0}x{r.H:0}) viewH={s.ViewportH:0} contentH={s.ContentH:0}");
                    if (s.ContentH > s.ViewportH + 1f && s.ContentH > bestContent) { bestContent = s.ContentH; vp = n; }
                }
                for (var c = host.Scene.FirstChild(n); !c.IsNull; c = host.Scene.NextSibling(c)) st.Push(c);
            }
        }
        if (vp.IsNull) { Console.Error.WriteLine("[hero-shot] no scroll viewport — aborting"); return; }
        var vr = host.Scene.AbsoluteRect(vp);
        host.Scene.TryGetScroll(vp, out var s0);
        float vh = s0.ViewportH > 20f ? s0.ViewportH : vr.H;
        var pos = new Point2(vr.X + vr.W * 0.5f, vr.Y + vh * 0.5f);
        window.QueueInput(new InputEvent(InputKind.PointerMove, pos, 0, 0));
        Settle(2);
        Console.Error.WriteLine($"[hero-shot] viewport content {s0.ContentH:0} viewH {s0.ViewportH:0} @ ({pos.X:0},{pos.Y:0})");

        foreach (int target in new[] { 0, 90, 150, 210, 300, 410 })
        {
            // Approach gently: one small notch, let it settle (velocity ~0) before the next, so fling momentum doesn't
            // overshoot the low sample points.
            for (int i = 0; i < 200 && !window.IsClosed; i++)
            {
                host.Scene.TryGetScroll(vp, out var st);
                if (st.OffsetY >= target - 2f) break;
                window.QueueInput(new InputEvent(InputKind.Wheel, pos, 0, 0, MathF.Min(30f, target - st.OffsetY)));
                for (int k = 0; k < 5 && !window.IsClosed; k++) Frame();
            }
            Settle(6);
            host.Scene.TryGetScroll(vp, out var stNow);
            DumpCollapse(host.Scene, vp, stNow.OffsetY);
            Shot((int)stNow.OffsetY);
        }

        // Focus-regain regression (user report): with the hero fully collapsed, send REAL WM_ACTIVATE deactivate →
        // reactivate through the wndproc (flips Win32Window._active → the Mica re-theme + chrome epoch path) and
        // capture the steady frames after each — the collapsed hero band must NOT re-appear.
        host.Scene.TryGetScroll(vp, out var stC);
        DumpCollapse(host.Scene, vp, stC.OffsetY);
        Shot(900);   // baseline: collapsed, focused
        nint hwnd = window.Handle.Value;
        SendMessageW(hwnd, 0x0006 /*WM_ACTIVATE*/, 0 /*WA_INACTIVE*/, 0);
        Settle(20);
        host.Scene.TryGetScroll(vp, out var stB);
        DumpCollapse(host.Scene, vp, stB.OffsetY);
        Shot(901);   // blurred steady
        SendMessageW(hwnd, 0x0006 /*WM_ACTIVATE*/, 1 /*WA_ACTIVE*/, 0);
        Settle(3);
        Shot(902);   // regain +3 frames
        Settle(20);
        host.Scene.TryGetScroll(vp, out var stF);
        DumpCollapse(host.Scene, vp, stF.OffsetY);
        Shot(903);   // regain steady
        Console.Error.WriteLine("[hero-shot] done");
    }

    // Walk the scene under the scroll content and print any node carrying collapse state (a PresentedH override or a
    // ChildShiftY), with its pin shift (LocalTransform.Dy), its laid-out height, and its PARENT's height — the parent
    // height is the sticky containing-block clamp, so a parent that tightly equals the node's height means the pin can't
    // engage. Also prints the node's siblings so the gap between the collapsed hero and the following content is visible.
    static void DumpCollapse(FluentGpu.Scene.SceneStore scene, NodeHandle vp, float offset)
    {
        scene.TryGetScroll(vp, out var sc);
        var content = sc.ContentNode;
        NodeHandle collapse = NodeHandle.Null;
        var stack = new Stack<NodeHandle>();
        if (!content.IsNull) stack.Push(content);
        while (stack.Count > 0 && collapse.IsNull)
        {
            var n = stack.Pop();
            if (n.IsNull || !scene.IsLive(n)) continue;
            ref var p = ref scene.Paint(n);
            if (!float.IsNaN(p.PresentedH) || MathF.Abs(p.ChildShiftY) > 0.01f) { collapse = n; break; }
            for (var c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c)) stack.Push(c);
        }
        if (collapse.IsNull) { Console.Error.WriteLine($"[hero-geom] off={offset:0}: no collapse node (scroll-away mode)"); return; }
        ref var cp = ref scene.Paint(collapse);
        var cr = scene.AbsoluteRect(collapse);
        var par = scene.Parent(collapse);
        float parH = par.IsNull ? -1f : scene.Bounds(par).H;
        Console.Error.WriteLine(
            $"[hero-geom] off={offset:0}: collapse node rectY={cr.Y:0} boundsH={scene.Bounds(collapse).H:0} " +
            $"presentedH={cp.PresentedH:0} childShiftY={cp.ChildShiftY:0} pinShift(Dy)={cp.LocalTransform.Dy:0} " +
            $"| parentH={parH:0} (==boundsH ⇒ pin clamp=0)");
        if (!par.IsNull)
            for (var c = scene.FirstChild(par); !c.IsNull; c = scene.NextSibling(c))
            {
                var rc = scene.AbsoluteRect(c);
                Console.Error.WriteLine($"           sibling rectY={rc.Y:0} H={scene.Bounds(c).H:0} bottom={rc.Bottom:0}");
            }
    }

    // WAVEE_CONN_STRESS=1 (+ optional WAVEE_STRESS_N=100, WAVEE_STRESS_NOPACE=1): the user's scenario — SLOWLY click a Home
    // card (connected-animation fly to detail), WAIT for the nav to settle, go BACK (reverse fly), wait, repeat N times. For
    // every frame it captures BOTH the loop's RecommendedWaitMs (0 = display-rate, >0 = throttled to the 30Hz ambient cap)
    // AND the pure per-frame WORK cost (vsync suppressed). Headline metric = "await-dest throttle": with the
    // AnimIsAmbient(_connected) fix, EVERY in-flight (fly/mount) frame must be display-rate (wait==0) — any wait>0 there is
    // the stall that made connected animations "sometimes laggy". Cycles 1..K (K = distinct home cards) are COLD/uncached.
    sealed class Seg
    {
        public int Frames, Active, Over8, Over16, Gen0, MaxAwaitWait;
        public double MaxWorkMs;
        public long Alloc;
        public readonly List<int> FirstWaits = new(24);
        public readonly List<double> ActiveWork = new(64);
    }

    static void RunConnStress(AppHost host, Win32Window window, D3D12Device gpu)
    {
        int N = int.TryParse(Environment.GetEnvironmentVariable("WAVEE_STRESS_N"), out var nn) && nn > 0 ? nn : 100;
        bool realPace = !Diag.EnvFlag("WAVEE_STRESS_NOPACE");
        if (WaveeShell.ProbeCardNav is null) { Console.Error.WriteLine("[conn-stress] ProbeCardNav not wired — aborting"); return; }
        Console.Error.WriteLine($"[conn-stress] {N} cycles (click->settle->back->settle); realistic pacing={realPace}");

        void Spin(int frames) { for (int f = 0; f < frames && !window.IsClosed; f++) { gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce(); host.RunFrame(); } }

        // Warmup: home live + card covers decoded (the fly needs an image source), then a clean GC baseline.
        WaveeShell.ProbeNav!("home", null); Spin(60);
        System.Threading.Thread.Sleep(900);
        Spin(20);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

        var keys = new List<string>(); host.CollectMorphKeys(keys);
        if (keys.Count == 0) { Console.Error.WriteLine("[conn-stress] no home-card morph keys — aborting"); return; }
        Console.Error.WriteLine($"[conn-stress] {keys.Count} distinct home-card keys (cycles 1..{keys.Count} are COLD/uncached)");

        // Drive one navigation to rest: capture per-frame RecommendedWaitMs (the perceived cadence) + pure WORK (vsync
        // removed). "Settled" = the transient fly+mount stops driving display-rate frames (4 consecutive non-display frames).
        Seg Drive()
        {
            var seg = new Seg();
            long a0 = GC.GetAllocatedBytesForCurrentThread(); int g0 = GC.CollectionCount(0);
            int consecIdle = 0;
            for (int f = 0; f < 90 && !window.IsClosed; f++)
            {
                int wait = host.RecommendedWaitMs();      // the pace the real loop would apply now (also feeds Fix 1's resync)
                if (seg.FirstWaits.Count < 24) seg.FirstWaits.Add(wait);
                if (f < 10 && wait > seg.MaxAwaitWait) seg.MaxAwaitWait = wait;   // throttle during the await-dest/early-fly window
                bool display = wait == 0;                 // a live fly/mount frame runs at display rate
                if (display) consecIdle = 0; else consecIdle++;
                if (realPace && wait > 0) System.Threading.Thread.Sleep(Math.Min(wait, 40));
                gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce();
                var s = host.RunFrame();
                seg.Frames++;
                if (display)
                {
                    seg.Active++;
                    seg.ActiveWork.Add(s.FrameMs);
                    if (s.FrameMs > seg.MaxWorkMs) seg.MaxWorkMs = s.FrameMs;
                    if (s.FrameMs > 8.33) seg.Over8++;
                    if (s.FrameMs > 16.7) seg.Over16++;
                }
                if (consecIdle >= 4) break;               // transient fly+mount done — only ambient/idle remains
            }
            seg.Alloc = GC.GetAllocatedBytesForCurrentThread() - a0;
            seg.Gen0 = GC.CollectionCount(0) - g0;
            return seg;
        }

        var fwd = new List<Seg>(N); var back = new List<Seg>(N);
        for (int i = 0; i < N && !window.IsClosed; i++)
        {
            WaveeShell.ProbeNav!("home", null); Spin(realPace ? 18 : 12);          // home = the fly's source card; let it settle
            WaveeShell.ProbeCardNav!(keys[i % keys.Count], null, true);            // CLICK -> connected-animation fly to detail
            fwd.Add(Drive());
            WaveeShell.ProbeBack?.Invoke();                                        // BACK -> reverse fly
            back.Add(Drive());
            if ((i + 1) % 10 == 0) Console.Error.WriteLine($"[conn-stress] {i + 1}/{N}");
        }

        var sb = new StringBuilder(8192);
        sb.AppendLine();
        sb.AppendLine($"=== WAVEE CONNECTED-ANIM STRESS — {N} cycles (click->settle->back->settle) ===");
        sb.AppendLine("await-throttle = an in-flight (fly/mount) frame the loop would THROTTLE to the 30Hz ambient cap — MUST be 0");
        sb.AppendLine("(that 30Hz stall was the 'connected anim sometimes laggy'); work = pure per-frame cost, vsync removed.");
        ReportSeg(sb, "FORWARD click->detail", fwd, keys.Count);
        ReportSeg(sb, "BACK    ->home", back, keys.Count);
        string rep = sb.ToString();
        Console.Error.Write(rep);
        try { Directory.CreateDirectory(@"C:\tmp"); File.WriteAllText(@"C:\tmp\wavee-conn-stress.txt", rep); Console.Error.WriteLine("[conn-stress] wrote C:\\tmp\\wavee-conn-stress.txt"); } catch { }
    }

    static void ReportSeg(StringBuilder sb, string title, List<Seg> segs, int coldCount)
    {
        sb.AppendLine();
        sb.AppendLine($"-- {title} — {segs.Count} cycles --");
        if (segs.Count == 0) { sb.AppendLine("  (none)"); return; }
        int throttledCycles = 0, maxAwait = 0;
        foreach (var s in segs) { if (s.MaxAwaitWait > 0) throttledCycles++; if (s.MaxAwaitWait > maxAwait) maxAwait = s.MaxAwaitWait; }
        sb.AppendLine($"  >> await-dest THROTTLE: {throttledCycles}/{segs.Count} cycles had a throttled in-flight frame (max wait {maxAwait}ms) — want 0/{segs.Count}");
        var work = new List<double>(8192); foreach (var s in segs) work.AddRange(s.ActiveWork);
        var wa = work.ToArray(); Array.Sort(wa);
        int over8 = 0, over16 = 0; foreach (var s in segs) { over8 += s.Over8; over16 += s.Over16; }
        sb.AppendLine($"  in-flight frames: {wa.Length} | WORK ms p50 {Pct(wa,50):0.00} p90 {Pct(wa,90):0.00} p99 {Pct(wa,99):0.00} max {(wa.Length>0?wa[^1]:0):0.00} | >8.33ms={over8} >16.7ms={over16}");
        var maxw = new double[segs.Count]; var settle = new double[segs.Count]; var alloc = new double[segs.Count];
        long totAlloc = 0; int totGc = 0;
        for (int i = 0; i < segs.Count; i++) { maxw[i] = segs[i].MaxWorkMs; settle[i] = segs[i].Frames; alloc[i] = segs[i].Alloc / 1024.0; totAlloc += segs[i].Alloc; totGc += segs[i].Gen0; }
        Array.Sort(maxw); Array.Sort(settle); Array.Sort(alloc);
        sb.AppendLine($"  per-cycle worst-frame ms: p50 {Pct(maxw,50):0.00} p90 {Pct(maxw,90):0.00} max {maxw[^1]:0.00}");
        sb.AppendLine($"  per-cycle frames-to-settle: p50 {Pct(settle,50):0} p90 {Pct(settle,90):0} max {settle[^1]:0}");
        sb.AppendLine($"  alloc/cycle KB: p50 {Pct(alloc,50):0} p90 {Pct(alloc,90):0} max {alloc[^1]:0} | total {totAlloc/1024/1024}MB Gen0={totGc}");
        if (segs.Count > coldCount && coldCount > 0)
        {
            double coldMax = 0, warmMax = 0; int cOver16 = 0, wOver16 = 0;
            for (int i = 0; i < segs.Count; i++)
            {
                bool cold = i < coldCount;
                foreach (var v in segs[i].ActiveWork) if (v > 16.7) { if (cold) cOver16++; else wOver16++; }
                if (cold) coldMax = Math.Max(coldMax, segs[i].MaxWorkMs); else warmMax = Math.Max(warmMax, segs[i].MaxWorkMs);
            }
            sb.AppendLine($"  COLD (1..{coldCount}) worst {coldMax:0.00}ms >16.7ms={cOver16}  |  WARM worst {warmMax:0.00}ms >16.7ms={wOver16}");
        }
        var w0 = segs[0].FirstWaits; var hd = new StringBuilder("  cycle#1 first waits(ms, 0=display rate): ");
        foreach (var x in w0) hd.Append(x).Append(' ');
        sb.AppendLine(hd.ToString());
    }

    sealed class Phase(string name)
    {
        public readonly string Name = name;
        public readonly List<double> Ms = new(4096);
        public int OverBudget;       // frames whose WORK exceeded one vblank (would drop a frame in vsync-paced use)
        public int OverBudgetGc;     // ...of those, frames on which a Gen0+ GC fired (a GC spike, not a work spike)
    }

    // WAVEE_HOME_SCROLL_PROBE=1: the screenshot repro path, isolated from the full nav stress suite. It warms Home,
    // toggles compact/expanded sidebar state, simulates a drag-resize sequence through the same shell signals, then
    // scrolls the Home viewport with real wheel input. Output defaults to .wavee-diagnostics under the current dir; override
    // with WAVEE_PROBE_OUT.
    static void RunHomeScrollProbe(AppHost host, Win32Window window, D3D12Device gpu)
    {
        string? outDir = ProbeOutputDir();
        string? csvPath = outDir is null ? null : Path.Combine(outDir, "wavee-home-scroll-probe.csv");
        string? summaryPath = outDir is null ? null : Path.Combine(outDir, "wavee-home-scroll-probe-summary.txt");
        var csv = new StringBuilder(1 << 15);
        csv.AppendLine("phase,frame,label,frameMs,flushMs,layoutMs,animMs,recordMs,submitMs,gen0,gen1,comps,nodes,draws,overBudget");
        bool keepVsync = Diag.EnvFlag("WAVEE_PROBE_VSYNC");

        FrameStats Measure(Phase phase, string label)
        {
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1);
            if (!keepVsync) { gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce(); }
            var s = host.RunFrame();
            int dg0 = GC.CollectionCount(0) - g0, dg1 = GC.CollectionCount(1) - g1;
            if (s.Rendered || s.DrawCommandCount > 0)
            {
                phase.Ms.Add(s.FrameMs);
                bool over = s.FrameMs > BudgetMs;
                if (over) { phase.OverBudget++; if (dg0 > 0) phase.OverBudgetGc++; }
                static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
                csv.Append(phase.Name).Append(',').Append(phase.Ms.Count.ToString(CultureInfo.InvariantCulture)).Append(',')
                   .Append(label).Append(',')
                   .Append(s.FrameMs.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                   .Append(F(s.FlushMs)).Append(',').Append(F(s.LayoutMs)).Append(',').Append(F(s.AnimMs)).Append(',')
                   .Append(F(s.RecordMs)).Append(',').Append(F(s.SubmitMs)).Append(',')
                   .Append(dg0).Append(',').Append(dg1).Append(',')
                   .Append(s.ComponentsRendered).Append(',').Append(s.NodesVisited).Append(',').Append(s.DrawCommandCount).Append(',')
                   .Append(over ? '1' : '0').AppendLine();
            }
            return s;
        }

        void Nav(string key, string? arg) => WaveeShell.ProbeNav!(key, arg);
        void Settle(int n) { for (int i = 0; i < n && !window.IsClosed; i++) host.RunFrame(); }

        Console.Error.WriteLine("[home-scroll-probe] warmup");
        for (int i = 0; i < 80 && !window.IsClosed; i++) { gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce(); host.RunFrame(); }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

        var sidebar = new Phase("sidebar-home");
        var scroll = new Phase("home-scroll");

        Nav("home", null);
        Settle(40);
        System.Threading.Thread.Sleep(700);     // let the Home feed and visible art settle before the sidebar scenario
        Settle(30);

        if (WaveeShell.ProbeSidebarCompact is null || WaveeShell.ProbeSidebarDragBegin is null ||
            WaveeShell.ProbeSidebarDragWidth is null || WaveeShell.ProbeSidebarDragEnd is null)
        {
            Console.Error.WriteLine("[home-scroll-probe] sidebar hooks unavailable; skipping sidebar state phase");
        }
        else
        {
            Console.Error.WriteLine("[home-scroll-probe] sidebar compact -> expanded -> drag-resize");
            WaveeShell.ProbeSidebarCompact(true);
            for (int f = 0; f < 18 && !window.IsClosed; f++) Measure(sidebar, "compact");
            WaveeShell.ProbeSidebarCompact(false);
            for (int f = 0; f < 26 && !window.IsClosed; f++) Measure(sidebar, "expand");

            WaveeShell.ProbeSidebarDragBegin();
            foreach (float w in new[] { 300f, 340f, 380f, 420f, 360f, 300f, 260f, 330f })
            {
                WaveeShell.ProbeSidebarDragWidth(w);
                Measure(sidebar, "drag");
            }
            WaveeShell.ProbeSidebarDragEnd();
            for (int f = 0; f < 12 && !window.IsClosed; f++) Measure(sidebar, "drag-end");
        }

        // Select the actual Home ScrollEl, not merely the largest vertical extent. The latter can select a nested or
        // retained viewport and produced a false PASS for the real page-scroll regression.
        var viewport = FindScrollViewportByKey(host.Scene, "home");
        if (viewport.IsNull)
        {
            Console.Error.WriteLine("[home-scroll-probe] no Home scroll viewport found");
        }
        else
        {
            var vr = host.Scene.AbsoluteRect(viewport);
            host.Scene.TryGetScroll(viewport, out var s0);
            float vh = s0.ViewportH > 20f ? s0.ViewportH : (vr.H > 20f ? vr.H : 400f);
            // Reproduce the reported dead strip exactly: point at the collapsed 2-DIP thumb on the trailing edge. The
            // retained closed-right-rail overlay used to win HitAny here even though the page remained clickable to its
            // left, so a centre-point probe gave a false PASS.
            var pos = new Point2(vr.X + vr.W - 2f, vr.Y + vh * 0.5f);
            window.QueueInput(new InputEvent(InputKind.PointerMove, pos, 0, 0));
            Settle(2);
            var routed0 = host.Input.ScrollableUnderForAxis(pos, wantHorizontal: false);
            Console.Error.WriteLine($"[home-scroll-probe] wheel @ ({pos.X:0},{pos.Y:0}) home=n#{viewport.Raw.Index} routed=n#{routed0.Raw.Index} " +
                $"viewport x={vr.X:0} w={vr.W:0} content {s0.ContentH:0} viewH {s0.ViewportH:0} suppressors={s0.LoadingBarSuppressors}");

            for (int i = 0; i < 180 && !window.IsClosed; i++)
            {
                window.QueueInput(new InputEvent(InputKind.Wheel, pos, 0, 0, ((i / 30) & 1) == 0 ? +60f : -60f));
                Measure(scroll, "wheel");
            }
            for (int rep = 0; rep < 4 && !window.IsClosed; rep++)
            {
                for (int k = 0; k < 6 && !window.IsClosed; k++) { window.QueueInput(new InputEvent(InputKind.Wheel, pos, 0, 0, +60f)); Measure(scroll, "flick"); }
                for (int st = 0; st < 16 && !window.IsClosed; st++) Measure(scroll, "coast");
            }
            host.Scene.TryGetScroll(viewport, out var s1);
            bool moved = MathF.Abs(s1.OffsetY - s0.OffsetY) > 1f;
            Console.Error.WriteLine($"[home-scroll-probe] endOff {s1.OffsetY:0} - wheel input {(moved ? "took effect" : "did not move")}");

            // Exact regression: a working scrollbar auto-hides, the window deactivates/reactivates, then BOTH wheel
            // routing and the conscious indicator must restart on the same viewport. Real-time idle frames are used so
            // the 2s WinUI contract delay actually expires (tight diagnostic frames have near-zero dt).
            window.QueueInput(new InputEvent(InputKind.WindowBlur, default, 0, 0));
            host.RunFrame();
            for (int i = 0; i < 145 && !window.IsClosed; i++)
            {
                System.Threading.Thread.Sleep(16);
                host.RunFrame();
            }
            host.Scene.TryGetScroll(viewport, out var hidden);
            Console.Error.WriteLine($"[home-scroll-reactivate] hidden fade={hidden.FadeT:0.00} pointerOver={hidden.PointerOver} off={hidden.OffsetY:0}");

            window.QueueInput(new InputEvent(InputKind.WindowFocus, default, 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerMove, pos, 0, 0));
            for (int i = 0; i < 8 && !window.IsClosed; i++) { System.Threading.Thread.Sleep(16); host.RunFrame(); }
            host.Scene.TryGetScroll(viewport, out var focused);
            var routedAfterFocus = host.Input.ScrollableUnderForAxis(pos, wantHorizontal: false);
            float beforeReactivatedWheel = focused.OffsetY;
            float maxReactivated = MathF.Max(0f, focused.ContentH - focused.ViewportH);
            float reactivatedDelta = beforeReactivatedWheel > maxReactivated * 0.5f ? -60f : 60f;
            for (int i = 0; i < 24 && !window.IsClosed; i++)
            {
                window.QueueInput(new InputEvent(InputKind.Wheel, pos, 0, 0, reactivatedDelta));
                System.Threading.Thread.Sleep(8);
                host.RunFrame();
            }
            host.Scene.TryGetScroll(viewport, out var reactivated);
            bool reactivatedMoved = MathF.Abs(reactivated.OffsetY - beforeReactivatedWheel) > 1f;
            bool reactivatedBar = reactivated.FadeT > 0.5f;
            Console.Error.WriteLine($"[home-scroll-reactivate] home=n#{viewport.Raw.Index} routed=n#{routedAfterFocus.Raw.Index} fade={reactivated.FadeT:0.00} pointerOver={reactivated.PointerOver} " +
                $"off={beforeReactivatedWheel:0}->{reactivated.OffsetY:0} wheel={(reactivatedMoved ? "PASS" : "FAIL")} bar={(reactivatedBar ? "PASS" : "FAIL")}");

            // Real app-loop cadence: present at vsync and ask RecommendedWaitMs before each frame so accidental ambient
            // throttling during scroll shows up as wait>0.
            var iv = new List<double>(220);
            long prev = Stopwatch.GetTimestamp();
            int throttled = 0, maxWait = 0;
            for (int i = 0; i < 200 && !window.IsClosed; i++)
            {
                window.QueueInput(new InputEvent(InputKind.Wheel, pos, 0, 0, ((i / 20) & 1) == 0 ? +60f : -60f));
                int wait = host.RecommendedWaitMs();
                if (wait > 0) { throttled++; if (wait > maxWait) maxWait = wait; System.Threading.Thread.Sleep(Math.Min(wait, 40)); }
                host.RunFrame();
                long now = Stopwatch.GetTimestamp();
                iv.Add((now - prev) * 1000.0 / Stopwatch.Frequency);
                prev = now;
            }
            var a = iv.ToArray();
            double tot = 0, worst = 0;
            foreach (var v in a) { tot += v; if (v > worst) worst = v; }
            double mean = a.Length > 0 ? tot / a.Length : 0;
            int o16 = 0;
            foreach (var v in a) if (v > 16.7) o16++;
            Console.Error.WriteLine($"[home-scroll-fps] REAL app-loop wheel scroll: {a.Length} frames mean {mean:0.0}ms ({(mean > 0 ? 1000.0 / mean : 0):0} fps) worst {worst:0.0}ms ({(worst > 0 ? 1000.0 / worst : 0):0} fps) >16.7ms(<60fps)={o16} throttledWaitFrames={throttled} maxWait={maxWait}ms");
        }

        var phases = new[] { sidebar, scroll };
        var all = new Phase("ALL");
        foreach (var p in phases) { all.Ms.AddRange(p.Ms); all.OverBudget += p.OverBudget; all.OverBudgetGc += p.OverBudgetGc; }

        var sb = new StringBuilder(4096);
        sb.AppendLine();
        sb.AppendLine("=== WAVEE HOME SCROLL PROBE - per-frame production time (ms); target < 8.33 ms at 120 Hz ===");
        sb.AppendLine(keepVsync ? "(WAVEE_PROBE_VSYNC: vblank-paced)" : "(vsync/latency throttle removed -> pure work cost)");
        sb.AppendLine();
        sb.AppendLine($"{"phase",-14} {"n",5} {"p50",6} {"p90",6} {"p99",7} {"p99.9",7} {"max",8}   {"over8.3ms",10} {"(ofwhich GC)",12}");
        foreach (var p in phases) sb.AppendLine(Format(p));
        sb.AppendLine(new string('-', 96));
        sb.AppendLine(Format(all));

        var rows = new List<(string Tag, double Ms, double Flush, double Layout, double Anim, double Record, double Submit, int G0)>();
        foreach (var line in csv.ToString().Split('\n'))
        {
            var c = line.Split(',');
            if (c.Length < 15 || c[0] == "phase") continue;
            double D(int i) => double.TryParse(c[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
            int.TryParse(c[9], out int g0);
            rows.Add((c[0] + ":" + c[2], D(3), D(4), D(5), D(6), D(7), D(8), g0));
        }
        rows.Sort((a, b) => b.Ms.CompareTo(a.Ms));
        sb.AppendLine();
        sb.AppendLine($"Worst 12 frames - {"total",7} = {"flush",6} + {"layout",6} + {"anim",5} + {"record",6} + {"submit",6}  (gc=Gen0)  transition");
        for (int i = 0; i < Math.Min(12, rows.Count); i++)
        {
            var r = rows[i];
            sb.AppendLine($"  {r.Ms,7:0.00} = {r.Flush,6:0.00} + {r.Layout,6:0.00} + {r.Anim,5:0.00} + {r.Record,6:0.00} + {r.Submit,6:0.00}  gc={r.G0}  {r.Tag}");
        }

        string report = sb.ToString();
        Console.Error.Write(report);
        if (csvPath is not null) WriteProbeFile(csvPath, csv.ToString(), "home-scroll-probe");
        if (summaryPath is not null) WriteProbeFile(summaryPath, report, "home-scroll-probe");
    }

    // WAVEE_RAIL_PROBE=1 (projected-motion P0): the dedicated rail/sidebar transition perf harness. Wide-window (rail
    // DOCKS) scenario driving (a) rail open→settle→close→settle ×3, (b) rapid rail reversal (toggle mid-flight), (c)
    // sidebar compact toggle ×3, (d) a synthesized sidebar-grip drag sweep. Per-frame CSV rows carry the segment ms PLUS
    // the FlexLayout measure/arrange/text-reshape counters (need FG_LAYOUT_DIAG=1 to be non-zero) and span
    // reused/rebased/re-recorded from FrameStats — so a Reflow-per-tick regression shows up as non-zero measure/arrange on
    // every animation tick, and the projected (Reveal/FLIP) fix as ~0 on ticks (only the commit frame is large).
    // WAVEE_RAIL_BASELINE=1 selects the pre-fix Reflow path from the same build for an A/B. Output → C:\tmp\projected-motion
    // (override with WAVEE_PROBE_OUT); writes wavee-rail-probe.csv + -summary.txt + rail_*.png first/mid/final captures.
    static void RunRailProbe(AppHost host, Win32Window window, D3D12Device gpu)
    {
        string outDir = Environment.GetEnvironmentVariable("WAVEE_PROBE_OUT") ?? "";
        if (string.IsNullOrWhiteSpace(outDir)) outDir = @"C:\tmp\projected-motion";
        try { Directory.CreateDirectory(outDir); } catch { }
        string csvPath = Path.Combine(outDir, "wavee-rail-probe.csv");
        string summaryPath = Path.Combine(outDir, "wavee-rail-probe-summary.txt");
        bool baseline = Diag.EnvFlag("WAVEE_RAIL_BASELINE");
        bool keepVsync = Diag.EnvFlag("WAVEE_PROBE_VSYNC");
        var csv = new StringBuilder(1 << 16);
        csv.AppendLine("phase,frame,label,frameMs,flushMs,layoutMs,animMs,recordMs,submitMs,measure,arrange,textMiss,spansReused,spansRebased,spansRerec,gen0,comps,nodes,draws,overBudget");

        window.SetClientSize(1700, 950);   // wide → the rail DOCKS (RailFits) — inline reserved width, the reported mode

        FrameStats Measure(Phase phase, string label)
        {
            int g0 = GC.CollectionCount(0);
            if (!keepVsync) { gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce(); }
            var s = host.RunFrame();
            int dg0 = GC.CollectionCount(0) - g0;
            if (s.Rendered || s.DrawCommandCount > 0)
            {
                phase.Ms.Add(s.FrameMs);
                bool over = s.FrameMs > BudgetMs;
                if (over) { phase.OverBudget++; if (dg0 > 0) phase.OverBudgetGc++; }
                static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
                csv.Append(phase.Name).Append(',').Append(phase.Ms.Count.ToString(CultureInfo.InvariantCulture)).Append(',')
                   .Append(label).Append(',')
                   .Append(s.FrameMs.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                   .Append(F(s.FlushMs)).Append(',').Append(F(s.LayoutMs)).Append(',').Append(F(s.AnimMs)).Append(',')
                   .Append(F(s.RecordMs)).Append(',').Append(F(s.SubmitMs)).Append(',')
                   .Append(s.MeasureCount).Append(',').Append(s.ArrangeCount).Append(',').Append(s.TextShapeMisses).Append(',')
                   .Append(s.SpansReused).Append(',').Append(s.SpansRebased).Append(',').Append(s.SpansReRecorded).Append(',')
                   .Append(dg0).Append(',').Append(s.ComponentsRendered).Append(',').Append(s.NodesVisited).Append(',').Append(s.DrawCommandCount).Append(',')
                   .Append(over ? '1' : '0').AppendLine();
            }
            return s;
        }

        void Nav(string k, string? a) => WaveeShell.ProbeNav!(k, a);
        void Settle(int n) { for (int i = 0; i < n && !window.IsClosed; i++) host.RunFrame(); }
        void Shot(string name)
        {
            var px = gpu.CaptureBgra(out int cw, out int ch);
            PngWriter.WriteBgra(Path.Combine(outDir, $"rail_{name}.png"), px, cw, ch);
            Console.Error.WriteLine($"[rail-probe] wrote rail_{name}.png ({cw}x{ch})");
        }

        Console.Error.WriteLine($"[rail-probe] {(baseline ? "BASELINE (SizeMode.Reflow)" : "P1 (Reveal/FLIP)")} out={outDir}");
        for (int i = 0; i < 80 && !window.IsClosed; i++) { gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce(); host.RunFrame(); }
        Nav("home", null); Settle(40); System.Threading.Thread.Sleep(700); Settle(30);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

        if (WaveeShell.ProbeRailOpen is null || WaveeShell.ProbeSidebarCompact is null)
        {
            Console.Error.WriteLine("[rail-probe] rail/sidebar hooks not wired — aborting");
            return;
        }

        var railOC = new Phase("rail-open-close");
        var railRev = new Phase("rail-reversal");
        var sbToggle = new Phase("sidebar-toggle");
        var sbDrag = new Phase("sidebar-drag");

        // (a) rail open→settle→close→settle ×3 (first cycle captures first/mid/final PNGs of both legs).
        Console.Error.WriteLine("[rail-probe] (a) rail open/close x3");
        for (int rep = 0; rep < 3 && !window.IsClosed; rep++)
        {
            WaveeShell.ProbeRailOpen!(true);
            for (int f = 0; f < 40 && !window.IsClosed; f++)
            {
                Measure(railOC, "open");
                if (rep == 0 && f == 1) Shot("open_first");
                if (rep == 0 && f == 8) Shot("open_mid");
            }
            if (rep == 0) Shot("open_final");
            WaveeShell.ProbeRailOpen!(false);
            for (int f = 0; f < 40 && !window.IsClosed; f++)
            {
                Measure(railOC, "close");
                if (rep == 0 && f == 8) Shot("close_mid");
            }
            if (rep == 0) Shot("close_final");
        }

        // (b) rapid reversal — flip the target every 6 frames so each toggle interrupts an in-flight transition.
        Console.Error.WriteLine("[rail-probe] (b) rapid rail reversal");
        bool open = false;
        for (int i = 0; i < 120 && !window.IsClosed; i++)
        {
            if (i % 6 == 0) { open = !open; WaveeShell.ProbeRailOpen!(open); }
            Measure(railRev, open ? "toward-open" : "toward-close");
        }
        WaveeShell.ProbeRailOpen!(false); Settle(40);

        // (c) sidebar compact toggle ×3 (56↔expanded).
        Console.Error.WriteLine("[rail-probe] (c) sidebar compact toggle x3");
        for (int rep = 0; rep < 3 && !window.IsClosed; rep++)
        {
            WaveeShell.ProbeSidebarCompact!(true);
            for (int f = 0; f < 30 && !window.IsClosed; f++)
            {
                Measure(sbToggle, "compact");
                if (rep == 0 && f == 1) Shot("sb_compact_first");
                if (rep == 0 && f == 8) Shot("sb_compact_mid");
            }
            if (rep == 0) Shot("sb_compact_final");
            WaveeShell.ProbeSidebarCompact!(false);
            for (int f = 0; f < 30 && !window.IsClosed; f++)
            {
                Measure(sbToggle, "expand");
                if (rep == 0 && f == 1) Shot("sb_expand_first");
                if (rep == 0 && f == 8) Shot("sb_expand_mid");
            }
            if (rep == 0) Shot("sb_expand_final");
        }

        // (d) sidebar-grip drag sweep — synthesize a pointer drag across ~40 steps (out to 460 and back to 240). This
        // exercises the suppressed path (SnapStructuralToLayout): with Reveal transitions the drag must still track 1:1.
        if (WaveeShell.ProbeSidebarDragBegin is not null && WaveeShell.ProbeSidebarDragWidth is not null && WaveeShell.ProbeSidebarDragEnd is not null)
        {
            Console.Error.WriteLine("[rail-probe] (d) sidebar drag sweep");
            WaveeShell.ProbeSidebarDragBegin!();
            for (int i = 0; i < 40 && !window.IsClosed; i++)
            {
                float t = i / 39f;
                float w = 240f + (460f - 240f) * (0.5f - 0.5f * MathF.Cos(t * MathF.PI * 2f));   // ease out to max and back
                WaveeShell.ProbeSidebarDragWidth!(w);
                Measure(sbDrag, "drag");
            }
            WaveeShell.ProbeSidebarDragEnd!();
            for (int f = 0; f < 12 && !window.IsClosed; f++) Measure(sbDrag, "drag-end");
        }

        // ── report ──
        var phases = new[] { railOC, railRev, sbToggle, sbDrag };
        var all = new Phase("ALL");
        foreach (var p in phases) { all.Ms.AddRange(p.Ms); all.OverBudget += p.OverBudget; all.OverBudgetGc += p.OverBudgetGc; }

        var sb = new StringBuilder(8192);
        sb.AppendLine();
        sb.AppendLine($"=== WAVEE RAIL PROBE — projected-motion {(baseline ? "BASELINE (SizeMode.Reflow)" : "P1 (Reveal/FLIP)")} — per-frame ms; target < 8.33 ms at 120 Hz ===");
        sb.AppendLine(keepVsync ? "(WAVEE_PROBE_VSYNC: vblank-paced)" : "(vsync/latency throttle removed → pure work cost)");
        sb.AppendLine();
        sb.AppendLine($"{"phase",-16} {"n",5} {"p50",6} {"p90",6} {"p99",7} {"p99.9",7} {"max",8}   {"over8.3ms",10} {"(ofwhich GC)",12}");
        foreach (var p in phases) sb.AppendLine(Format(p));
        sb.AppendLine(new string('-', 98));
        sb.AppendLine(Format(all));

        // Per-phase layout-cost aggregate (the projected-motion headline): on a Reveal/FLIP toggle only the COMMIT frame
        // should carry measure/arrange; every anim tick must be ~0. Baseline (Reflow) re-measures on EVERY tick.
        var perPhase = new Dictionary<string, (int frames, int nonZeroMeasure, long sumMeasure, int maxMeasure, int maxArrange, int maxTextMiss)>();
        long anyMeasure = 0;
        foreach (var line in csv.ToString().Split('\n'))
        {
            var c = line.Split(',');
            if (c.Length < 20 || c[0] == "phase") continue;
            int.TryParse(c[9], out int meas); int.TryParse(c[10], out int arr); int.TryParse(c[11], out int tmiss);
            anyMeasure += meas;
            perPhase.TryGetValue(c[0], out var agg);
            agg.frames++;
            if (meas > 0) agg.nonZeroMeasure++;
            agg.sumMeasure += meas;
            if (meas > agg.maxMeasure) agg.maxMeasure = meas;
            if (arr > agg.maxArrange) agg.maxArrange = arr;
            if (tmiss > agg.maxTextMiss) agg.maxTextMiss = tmiss;
            perPhase[c[0]] = agg;
        }
        sb.AppendLine();
        sb.AppendLine("layout cost per phase (FG_LAYOUT_DIAG): frames with measure>0 / total, avg measure/frame, max measure/arrange/textMiss");
        foreach (var p in phases)
        {
            if (!perPhase.TryGetValue(p.Name, out var a) || a.frames == 0) continue;
            sb.AppendLine($"  {p.Name,-16} measureFrames={a.nonZeroMeasure}/{a.frames}  avgMeasure={(double)a.sumMeasure / a.frames:0.0}  maxMeasure={a.maxMeasure}  maxArrange={a.maxArrange}  maxTextMiss={a.maxTextMiss}");
        }
        if (anyMeasure == 0)
            sb.AppendLine("  NOTE: all measure counts are 0 — re-run with FG_LAYOUT_DIAG=1 to populate the layout-cost columns.");

        // Worst 12 frames overall, segment-broken-down.
        var rows = new List<(string Tag, double Ms, double Flush, double Layout, double Anim, double Record, double Submit, int Meas, int Arr, int G0)>();
        foreach (var line in csv.ToString().Split('\n'))
        {
            var c = line.Split(',');
            if (c.Length < 20 || c[0] == "phase") continue;
            double D(int i) => double.TryParse(c[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
            int.TryParse(c[9], out int meas); int.TryParse(c[10], out int arr); int.TryParse(c[15], out int g0);
            rows.Add((c[0] + ":" + c[2], D(3), D(4), D(5), D(6), D(7), D(8), meas, arr, g0));
        }
        rows.Sort((a, b) => b.Ms.CompareTo(a.Ms));
        sb.AppendLine();
        sb.AppendLine($"Worst 12 frames — {"total",7} = {"flush",6} + {"layout",6} + {"anim",5} + {"record",6} + {"submit",6}  (meas/arr, gc)  transition");
        for (int i = 0; i < Math.Min(12, rows.Count); i++)
        {
            var r = rows[i];
            sb.AppendLine($"  {r.Ms,7:0.00} = {r.Flush,6:0.00} + {r.Layout,6:0.00} + {r.Anim,5:0.00} + {r.Record,6:0.00} + {r.Submit,6:0.00}  (m={r.Meas} a={r.Arr}, gc={r.G0})  {r.Tag}");
        }

        string report = sb.ToString();
        Console.Error.Write(report);
        WriteProbeFile(csvPath, csv.ToString(), "rail-probe");
        WriteProbeFile(summaryPath, report, "rail-probe");
    }

    // WAVEE_LIVE_LYRICS_SCROLL_PROBE=1: REAL-backend, long-duration repro for "the rest of the app scrolls poorly while
    // lyrics are open". This intentionally refuses the fake backend and waits for live authenticated playback with an
    // advancing position before measuring. Output defaults to .wavee-diagnostics; tune with:
    //   WAVEE_PROBE_REAL_FRAMES (default 1800), WAVEE_PROBE_WORK_FRAMES (default 600),
    //   WAVEE_PROBE_PLAYBACK_FRAMES / WAVEE_PROBE_LYRICS_FRAMES for readiness waits.
    static void RunLiveLyricsScrollProbe(AppHost host, Win32Window window, D3D12Device gpu)
    {
        string? outDir = ProbeOutputDir();
        string? csvPath = outDir is null ? null : Path.Combine(outDir, "wavee-live-lyrics-scroll-probe.csv");
        string? summaryPath = outDir is null ? null : Path.Combine(outDir, "wavee-live-lyrics-scroll-probe-summary.txt");
        var csv = new StringBuilder(1 << 18);
        csv.AppendLine("phase,frame,label,intervalMs,frameMs,flushMs,layoutMs,animMs,recordMs,submitMs,fenceWaitMs,presentMs,gen0,gen1,comps,nodes,draws,blurCandidates,blurLayers,blurSuppressed,blurHoldCandidates,edgeFadeGroups,d3dBlurHit,d3dBlurMiss,d3dBlurHoldHit,d3dBlurHoldFallback,d3dOpacityGroups,lyricsNowMs,lyricsAuthMs,lyricsActiveLine,lyricsVoiceLine,lyricsActiveChanged,lyricsScrollSnapped,trackMs,isPlaying,mainOff,mainTarget,mainMode,mainTransformDirty,lyricsOff,lyricsTarget,lyricsMode,lyricsTransformDirty,track");

        int realFrames = EnvInt("WAVEE_PROBE_REAL_FRAMES", 1800, 120, 20000);
        int workFrames = EnvInt("WAVEE_PROBE_WORK_FRAMES", 600, 0, 20000);
        int playbackFrames = EnvInt("WAVEE_PROBE_PLAYBACK_FRAMES", 5400, 120, 36000);
        int lyricsFrames = EnvInt("WAVEE_PROBE_LYRICS_FRAMES", 3600, 60, 36000);

        void FrameLive()
        {
            if (window.IsClosed) return;
            host.RunFrame();
            int wait = host.RecommendedWaitMs();
            if (wait > 0) window.WaitForWork(Math.Min(wait, 16));
        }
        void SettleLive(int n) { for (int i = 0; i < n && !window.IsClosed; i++) FrameLive(); }

        bool WaitFor(string label, int frames, Func<bool> ready)
        {
            for (int i = 0; i < frames && !window.IsClosed; i++)
            {
                if (ready()) return true;
                FrameLive();
            }
            Console.Error.WriteLine($"[live-lyrics-scroll] timed out waiting for {label}");
            return false;
        }

        if (!Services.UseRealBackend)
        {
            Console.Error.WriteLine("[live-lyrics-scroll] refusing to run: app is using --fake; run without --fake / with --real-backend");
            return;
        }
        if (WaveeApp.ProbePlayback is null)
        {
            Console.Error.WriteLine("[live-lyrics-scroll] no playback bridge exposed; shell/app did not mount");
            return;
        }

        long lastPos = -1;
        int advances = 0;
        bool PlaybackReady()
        {
            var b = WaveeApp.ProbePlayback;
            if (b is null) return false;
            long pos = b.PositionMs.Peek();
            if (pos > lastPos) advances++;
            lastPos = pos;
            return b.Auth.Peek() == AuthStatus.Authenticated
                && b.CurrentTrack.Peek() is not null
                && b.IsPlaying.Peek()
                && advances >= 2;
        }

        Console.Error.WriteLine("[live-lyrics-scroll] waiting for REAL authenticated playback with advancing position");
        if (!WaitFor("live playing track", playbackFrames, PlaybackReady))
        {
            var b = WaveeApp.ProbePlayback;
            Console.Error.WriteLine($"[live-lyrics-scroll] playback state: auth={b?.Auth.Peek()} playing={b?.IsPlaying.Peek()} track={TrackLabel(b)} pos={b?.PositionMs.Peek() ?? 0}");
            return;
        }

        Console.Error.WriteLine($"[live-lyrics-scroll] track: {TrackLabel(WaveeApp.ProbePlayback)}");
        NodeHandle lyrics = default;
        bool LyricsReady()
        {
            lyrics = FindLyricsViewport(host.Scene);
            if (lyrics.IsNull) return false;
            var root = host.Scene.Root.IsNull ? default : host.Scene.AbsoluteRect(host.Scene.Root);
            var lr = host.Scene.AbsoluteRect(lyrics);
            return root.W <= 0f || lr.X >= root.W * 0.55f;
        }

        Console.Error.WriteLine("[live-lyrics-scroll] waiting for lyrics viewport");
        if (!WaitFor("lyrics viewport", lyricsFrames, LyricsReady))
        {
            DumpScrollers(host.Scene);
            return;
        }

        void Nav(string key, string? arg) => WaveeShell.ProbeNav!(key, arg);
        var routes = BuildLiveLyricsRoutes(WaveeApp.ProbePlayback);
        int routeSettleFrames = EnvInt("WAVEE_PROBE_ROUTE_SETTLE_FRAMES", 150, 12, 3600);
        int extraRoutes = EnvInt("WAVEE_PROBE_EXTRA_ROUTES", 8, 0, 40);
        if (extraRoutes > 0)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in routes) seen.Add(r.Key + "\u001F" + (r.Arg ?? ""));

            Nav("home", null);
            SettleLive(routeSettleFrames);
            var homeKeys = new List<string>();
            host.CollectMorphKeys(homeKeys);
            int added = 0, playlistN = 0, albumN = 0;
            foreach (var key in homeKeys)
            {
                if (added >= extraRoutes) break;
                bool playlist = key.StartsWith("pl:", StringComparison.Ordinal);
                bool album = key.StartsWith("album:", StringComparison.Ordinal);
                if (!playlist && !album) continue;
                string sig = key + "\u001F";
                if (!seen.Add(sig)) continue;
                string label = playlist ? "playlist-" + (++playlistN).ToString(CultureInfo.InvariantCulture) : "album-" + (++albumN).ToString(CultureInfo.InvariantCulture);
                routes.Add(new ProbeRoute(key, null, SafeRouteLabel(label)));
                added++;
            }
            Console.Error.WriteLine($"[live-lyrics-scroll] added {added} home playlist/album routes");
        }
        int perRouteRealFrames = EnvInt("WAVEE_PROBE_ROUTE_FRAMES", Math.Max(60, realFrames / Math.Max(1, routes.Count)), 0, 20000);
        int perRouteWorkFrames = EnvInt("WAVEE_PROBE_WORK_ROUTE_FRAMES", workFrames / Math.Max(1, routes.Count), 0, 20000);

        var allIntervals = new List<double>(Math.Max(realFrames, perRouteRealFrames * routes.Count));
        var intervalGroups = new List<(string Label, List<double> Values)>();
        var phases = new List<Phase>(routes.Count * 2);
        var routeReports = new List<string>(routes.Count);
        int throttled = 0, maxWait = 0, measuredRoutes = 0, skippedRoutes = 0;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

        foreach (var route in routes)
        {
            if (window.IsClosed) break;
            Console.Error.WriteLine($"[live-lyrics-scroll] route {route.Label}: {route.Key}" + (route.Arg is null ? "" : $" ({route.Arg})"));
            Nav(route.Key, route.Arg);
            SettleLive(routeSettleFrames);

            lyrics = FindLyricsViewport(host.Scene);
            if (lyrics.IsNull)
            {
                Console.Error.WriteLine($"[live-lyrics-scroll] route {route.Label}: lyrics viewport missing after navigation");
                skippedRoutes++;
                continue;
            }

            NodeHandle main = FindMainScrollViewport(host.Scene, lyrics);
            if (main.IsNull)
            {
                Console.Error.WriteLine($"[live-lyrics-scroll] route {route.Label}: no main page scroller; skipped");
                skippedRoutes++;
                continue;
            }

            var mainRect = host.Scene.AbsoluteRect(main);
            host.Scene.TryGetScroll(main, out var mainScroll0);
            host.Scene.TryGetScroll(lyrics, out var lyricsScroll0);
            float mainMin = mainScroll0.OffsetY, mainMax = mainScroll0.OffsetY;
            float lyricsMin = lyricsScroll0.OffsetY, lyricsMax = lyricsScroll0.OffsetY;
            void TrackRouteOffsets()
            {
                if (host.Scene.TryGetScroll(main, out var ms))
                {
                    mainMin = MathF.Min(mainMin, ms.OffsetY);
                    mainMax = MathF.Max(mainMax, ms.OffsetY);
                }
                if (host.Scene.TryGetScroll(lyrics, out var ls))
                {
                    lyricsMin = MathF.Min(lyricsMin, ls.OffsetY);
                    lyricsMax = MathF.Max(lyricsMax, ls.OffsetY);
                }
            }
            float vh = mainScroll0.ViewportH > 20f ? mainScroll0.ViewportH : (mainRect.H > 20f ? mainRect.H : 400f);
            var posPt = new Point2(mainRect.X + mainRect.W * 0.5f, mainRect.Y + vh * 0.5f);
            window.QueueInput(new InputEvent(InputKind.PointerMove, posPt, 0, 0));
            SettleLive(4);
            Console.Error.WriteLine($"[live-lyrics-scroll] route {route.Label}: wheel @ ({posPt.X:0},{posPt.Y:0}) main content={mainScroll0.ContentH:0} viewH={mainScroll0.ViewportH:0}; lyrics content={lyricsScroll0.ContentH:0} viewH={lyricsScroll0.ViewportH:0}");

            var routeIntervals = new List<double>(perRouteRealFrames);
            var realPhase = new Phase("real:" + route.Label);
            phases.Add(realPhase);
            int activeChangeFrames = 0, blurHeldFrames = 0, zeroBlurWithCandidates = 0;
            int maxBlurHeld = 0, maxBlurCandidates = 0, maxBlurLayers = 0, maxBlurCacheMiss = 0;
            int minBlurLayers = int.MaxValue;
            void TrackLyricsPipeline(FrameStats s)
            {
                var ld = LyricsView.LastFrameDiagnostics;
                if (ld.ActiveChanged) activeChangeFrames++;
                if (s.BlurCandidateCount > 0)
                {
                    minBlurLayers = Math.Min(minBlurLayers, s.BlurGroupCount);
                    if (s.BlurGroupCount == 0) zeroBlurWithCandidates++;
                }
                // During this MAIN-page wheel the lyrics sibling self-blur HOLDS under its cache policy (served by its
                // position-independent pin), counted by BlurHoldCandidateCount; 0 held frames across a sustained scroll =
                // the sibling-defer path is dead (the metric that replaced the retired BlurSuppressedByScrollCount).
                if (s.BlurHoldCandidateCount > 0) blurHeldFrames++;
                maxBlurHeld = Math.Max(maxBlurHeld, s.BlurHoldCandidateCount);
                maxBlurCandidates = Math.Max(maxBlurCandidates, s.BlurCandidateCount);
                maxBlurLayers = Math.Max(maxBlurLayers, s.BlurGroupCount);
                maxBlurCacheMiss = Math.Max(maxBlurCacheMiss, gpu.LastBlurCacheMiss);
            }
            long prevTick = Stopwatch.GetTimestamp();
            for (int i = 0; i < perRouteRealFrames && !window.IsClosed; i++)
            {
                float wheel = ((i / 90) & 1) == 0 ? +60f : -60f;
                window.QueueInput(new InputEvent(InputKind.Wheel, posPt, 0, 0, wheel));
                int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1);
                var s = host.RunFrame();
                long now = Stopwatch.GetTimestamp();
                double intervalMs = (now - prevTick) * 1000.0 / Stopwatch.Frequency;
                prevTick = now;
                routeIntervals.Add(intervalMs);
                allIntervals.Add(intervalMs);
                int dg0 = GC.CollectionCount(0) - g0, dg1 = GC.CollectionCount(1) - g1;
                if (s.Rendered || s.DrawCommandCount > 0)
                {
                    realPhase.Ms.Add(s.FrameMs);
                    if (s.FrameMs > BudgetMs) { realPhase.OverBudget++; if (dg0 > 0) realPhase.OverBudgetGc++; }
                }
                AppendLiveLyricsRow(csv, "real:" + route.Label, i, wheel > 0 ? "wheel-down" : "wheel-up", intervalMs, s, dg0, dg1, host.Scene, main, lyrics, WaveeApp.ProbePlayback, gpu);
                TrackLyricsPipeline(s);
                TrackRouteOffsets();
                int wait = host.RecommendedWaitMs();
                if (wait > 0)
                {
                    throttled++;
                    if (wait > maxWait) maxWait = wait;
                    window.WaitForWork(Math.Min(wait, 16));
                }
            }
            intervalGroups.Add((route.Label, routeIntervals));

            var workPhase = new Phase("work:" + route.Label);
            if (perRouteWorkFrames > 0) phases.Add(workPhase);
            for (int i = 0; i < perRouteWorkFrames && !window.IsClosed; i++)
            {
                float wheel = ((i / 90) & 1) == 0 ? +60f : -60f;
                window.QueueInput(new InputEvent(InputKind.Wheel, posPt, 0, 0, wheel));
                int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1);
                gpu.SuppressLatencyWaitOnce();
                gpu.SuppressVsyncOnce();
                var s = host.RunFrame();
                int dg0 = GC.CollectionCount(0) - g0, dg1 = GC.CollectionCount(1) - g1;
                if (s.Rendered || s.DrawCommandCount > 0)
                {
                    workPhase.Ms.Add(s.FrameMs);
                    if (s.FrameMs > BudgetMs) { workPhase.OverBudget++; if (dg0 > 0) workPhase.OverBudgetGc++; }
                }
                AppendLiveLyricsRow(csv, "work:" + route.Label, i, wheel > 0 ? "wheel-down" : "wheel-up", 0, s, dg0, dg1, host.Scene, main, lyrics, WaveeApp.ProbePlayback, gpu);
                TrackLyricsPipeline(s);
                TrackRouteOffsets();
            }

            host.Scene.TryGetScroll(main, out var mainScroll1);
            host.Scene.TryGetScroll(lyrics, out var lyricsScroll1);
            bool mainMoved = mainMax - mainMin > 1f;
            bool lyricsMoved = lyricsMax - lyricsMin > 1f;
            string minBlur = minBlurLayers == int.MaxValue ? "-" : minBlurLayers.ToString(CultureInfo.InvariantCulture);
            routeReports.Add($"{route.Label}: main offset {mainScroll0.OffsetY:0}->{mainScroll1.OffsetY:0} range {mainMin:0}-{mainMax:0} {(mainMoved ? "moved" : "NO-MOVE")}; lyrics offset {lyricsScroll0.OffsetY:0}->{lyricsScroll1.OffsetY:0} range {lyricsMin:0}-{lyricsMax:0} {(lyricsMoved ? "moved" : "still")}; frames real={routeIntervals.Count} work={workPhase.Ms.Count}; activeChanges={activeChangeFrames}; blurLayers min/max={minBlur}/{maxBlurLayers}; blurCandidates max={maxBlurCandidates}; blurHeld frames/max={blurHeldFrames}/{maxBlurHeld} (sibling DoF deferred during the main scroll; 0 across a moving scroll = regression); zeroBlurWithCandidates={zeroBlurWithCandidates}; maxBlurCacheMiss={maxBlurCacheMiss}");
            measuredRoutes++;
        }

        if (measuredRoutes == 0)
        {
            Console.Error.WriteLine("[live-lyrics-scroll] no routes with usable main scrollers found");
            DumpScrollers(host.Scene);
            return;
        }

        var sb = new StringBuilder(4096);
        sb.AppendLine();
        sb.AppendLine("=== WAVEE LIVE LYRICS SCROLL PROBE - REAL backend, lyrics rail open, multi-page main wheel scroll ===");
        sb.AppendLine($"track: {TrackLabel(WaveeApp.ProbePlayback)}");
        sb.AppendLine($"routes measured={measuredRoutes}; skipped={skippedRoutes}; route frames real={perRouteRealFrames}; work={perRouteWorkFrames}; throttledWaitFrames={throttled}; maxWait={maxWait}ms");
        AppendIntervalSummary(sb, "all real app-loop intervals", allIntervals);
        foreach (var group in intervalGroups) AppendIntervalSummary(sb, "real intervals " + group.Label, group.Values);
        sb.AppendLine();
        sb.AppendLine($"{"phase",-14} {"n",5} {"p50",6} {"p90",6} {"p99",7} {"p99.9",7} {"max",8}   {"over8.3ms",10} {"(ofwhich GC)",12}");
        foreach (var phase in phases) sb.AppendLine(Format(phase));
        sb.AppendLine();
        foreach (var line in routeReports) sb.AppendLine(line);
        sb.AppendLine("CSV columns include playback, LyricsView active/voice line diagnostics, recorder blur counts, D3D blur cache counts, and main/lyrics scroll state per frame.");

        string report = sb.ToString();
        Console.Error.Write(report);
        if (csvPath is not null) WriteProbeFile(csvPath, csv.ToString(), "live-lyrics-scroll");
        if (summaryPath is not null) WriteProbeFile(summaryPath, report, "live-lyrics-scroll");
    }

    // WAVEE_LYRICS_ADVANCE_PROBE=1: the REDESIGNED, trustworthy lyrics probe. It drives the media clock SYNCHRONOUSLY
    // (LyricsView.ProbeStep, with the async ticker silenced by ProbeSyncMode) so a line advance and the RunFrame that
    // records the resulting scroll SETTLE are the SAME frame — killing the timer-decoupling artifact that made the prior
    // probe's "blur-drop ≠ line-change" claim meaningless. Reads the DoF-defer inputs (ScrollMode / UserScrollActive /
    // content-TransformDirty) captured at RECORD time (before ClearTransformDirty), plus the whole-frame blur counters.
    //   P1 stationary-advance : the real BUG1 scenario — advance lyrics while NOTHING else scrolls. On the fixed engine,
    //                           blur is NEVER suppressed (assert). On the old engine it spiked on every settle frame.
    //   P2 sibling-isolation  : wheel-scroll the MAIN page while lyrics sit still — the lyrics viewport must stay
    //                           mode 0 / not-user-scrolling / not-content-dirty (a sibling scroll can't cascade its DoF).
    //   P3 skip-submit (BUG3) : stationary, no input — count frames force-PRESENTED despite a static scene (a loop anim
    //                           marking TransformDirty defeats skip-submit → the app-wide vsync-paced present cost).
    static void RunLyricsAdvanceProbe(AppHost host, Win32Window window, D3D12Device gpu)
    {
        string? outDir = ProbeOutputDir();
        string? csvPath = outDir is null ? null : Path.Combine(outDir, "wavee-lyrics-advance-probe.csv");
        string? summaryPath = outDir is null ? null : Path.Combine(outDir, "wavee-lyrics-advance-probe-summary.txt");
        var csv = new StringBuilder(1 << 16);
        csv.AppendLine("phase,line,frame,label,injectedNowMs,activeLine,voiceLine,activeChanged,voiceChanged,lyMode,lyPrevMode,lyUserScroll,lyContentDirty,lyOff,lyTgt,blurCandidates,blurGroups,blurSuppressed,blurHoldCandidates,d3dBlurMiss,d3dBlurHoldHit,d3dBlurHoldFallback,frameMs,recordMs,submitMs,fenceWaitMs,presentMs,presented,animMs,mainMode,mainContentDirty,track");

        void Paced() { if (window.IsClosed) return; host.RunFrame(); window.WaitForWork(16); }   // ~16 ms dt so the spring eases realistically
        void FrameLive() { if (window.IsClosed) return; host.RunFrame(); int wt = host.RecommendedWaitMs(); if (wt > 0) window.WaitForWork(Math.Min(wt, 16)); }
        bool WaitFor(string label, int frames, Func<bool> ready)
        {
            for (int i = 0; i < frames && !window.IsClosed; i++) { if (ready()) return true; FrameLive(); }
            Console.Error.WriteLine($"[lyrics-advance] timed out waiting for {label}");
            return false;
        }

        if (!Services.UseRealBackend) { Console.Error.WriteLine("[lyrics-advance] refusing to run under --fake; launch the real backend, log in, and play a word-synced track"); return; }
        if (WaveeApp.ProbePlayback is null) { Console.Error.WriteLine("[lyrics-advance] no playback bridge exposed"); return; }

        long lastPos = -1; int adv = 0;
        bool PlaybackReady()
        {
            var b = WaveeApp.ProbePlayback; if (b is null) return false;
            long p = b.PositionMs.Peek(); if (p > lastPos) adv++; lastPos = p;
            return b.Auth.Peek() == AuthStatus.Authenticated && b.CurrentTrack.Peek() is not null;
        }
        int playbackFrames = EnvInt("WAVEE_PROBE_PLAYBACK_FRAMES", 5400, 120, 36000);
        Console.Error.WriteLine("[lyrics-advance] waiting for REAL authenticated playback");
        if (!WaitFor("authenticated track", playbackFrames, PlaybackReady)) return;

        int lyricsFrames = EnvInt("WAVEE_PROBE_LYRICS_FRAMES", 3600, 60, 36000);
        NodeHandle lyricsVp = default;
        bool LyricsReady()
        {
            var lv = LyricsView.ProbeActive; if (lv is null) return false;
            lyricsVp = lv.ProbeViewport;
            return !lyricsVp.IsNull && host.Scene.IsLive(lyricsVp) && lv.ProbeLineCount >= 3;
        }
        Console.Error.WriteLine("[lyrics-advance] waiting for the lyrics view + viewport + a >=3-line synced doc");
        if (!WaitFor("lyrics view/viewport/doc", lyricsFrames, LyricsReady)) { DumpScrollers(host.Scene); return; }

        var view = LyricsView.ProbeActive!;
        int lineCount = view.ProbeLineCount;
        host.ProbeLyricsViewport = lyricsVp;
        NodeHandle mainVp = FindMainScrollViewport(host.Scene, lyricsVp);
        host.ProbeMainViewport = mainVp;
        Console.Error.WriteLine($"[lyrics-advance] track: {TrackLabel(WaveeApp.ProbePlayback)}; lines={lineCount}; sync-driving (ticker silenced)");

        // Pre-settle onto line 0 so the ONE-TIME instant-jump latch fires there, then force snapped so every measured
        // advance takes the ProgrammaticMode spring (whose SETTLE frame is where BUG1 dropped the blur).
        view.ProbeStep(view.ProbeLineStartMs(0));
        for (int i = 0; i < 10 && !window.IsClosed; i++) Paced();
        view.ProbeForceSnapped();

        int settleFrames = EnvInt("WAVEE_PROBE_SETTLE_FRAMES", 26, 6, 240);
        int startLine = Math.Min(1, lineCount - 1);
        int endLine = Math.Min(lineCount - 1, startLine + EnvInt("WAVEE_PROBE_ADVANCES", 20, 1, 4096));

        // ── P1: stationary line-advance ────────────────────────────────────────────────────────────────────────────
        int p1Frames = 0, p1BlurAbsentFrames = 0, p1SettleFrames = 0;
        int prevLyMode = 0;
        for (int li = startLine; li <= endLine && !window.IsClosed; li++)
        {
            view.ProbeStep(view.ProbeLineStartMs(li));   // advance emphasis onto line li → arms a programmatic bring-into-view
            for (int f = 0; f < settleFrames && !window.IsClosed; f++)
            {
                var s = host.RunFrame();
                window.WaitForWork(16);
                p1Frames++;
                bool settleEdge = prevLyMode == FluentGpu.Animation.ScrollIntegrator.WheelAnimating && s.LyricsScrollMode == 0;
                if (settleEdge) p1SettleFrames++;
                // BUG1 signature under the pin-cache pipeline: the recorder no longer DROPS a stationary blur (the retired
                // BlurSuppressedByScrollCount is always 0), so the observable regression is the DoF VANISHING — a
                // content-advance frame (content transform written, NOT a user scroll) that records zero blur candidates
                // means the lyric depth-of-field went absent instead of being served by its pin.
                if (!s.LyricsUserScrollActive && s.LyricsContentDirtyAtRecord && s.BlurCandidateCount == 0) p1BlurAbsentFrames++;
                AppendAdvanceRow(csv, "P1-stationary", li, f, settleEdge ? "settle" : "", s, prevLyMode, host.Scene, lyricsVp, mainVp, gpu);
                prevLyMode = s.LyricsScrollMode;
            }
        }

        // ── P2: wheel the MAIN page while lyrics sit still (sibling isolation) ──────────────────────────────────────
        int p2Frames = 0, p2LyricsTouched = 0, p2LyricsHeld = 0;
        if (!mainVp.IsNull && host.Scene.IsLive(mainVp))
        {
            var mr = host.Scene.AbsoluteRect(mainVp);
            host.Scene.TryGetScroll(mainVp, out var msc0);
            float vh = msc0.ViewportH > 20f ? msc0.ViewportH : (mr.H > 20f ? mr.H : 400f);
            var posPt = new Point2(mr.X + mr.W * 0.5f, mr.Y + vh * 0.5f);
            window.QueueInput(new InputEvent(InputKind.PointerMove, posPt, 0, 0));
            int p2Total = EnvInt("WAVEE_PROBE_P2_FRAMES", 180, 0, 4096);
            for (int f = 0; f < p2Total && !window.IsClosed; f++)
            {
                window.QueueInput(new InputEvent(InputKind.Wheel, posPt, 0, 0, ((f / 45) & 1) == 0 ? +60f : -60f));
                var s = host.RunFrame();
                window.WaitForWork(16);
                p2Frames++;
                if (s.LyricsUserScrollActive || s.LyricsContentDirtyAtRecord) p2LyricsTouched++;   // must stay 0 — lyrics untouched by a main scroll
                // Sibling isolation now MEANS the stationary lyrics DoF is HELD (served by its position-independent pin),
                // not dropped, while the main page scrolls — BlurHoldCandidateCount > 0 with the lyrics themselves untouched.
                if (s.BlurHoldCandidateCount > 0) p2LyricsHeld++;
                AppendAdvanceRow(csv, "P2-mainscroll", -1, f, "", s, prevLyMode, host.Scene, lyricsVp, mainVp, gpu);
                prevLyMode = s.LyricsScrollMode;
            }
        }

        // ── P3: skip-submit / present cost with the rail open, no input (BUG3 mechanism) ───────────────────────────
        int p3Frames = 0, p3Presented = 0, p3StaticButPresented = 0; double p3AnimMs = 0, p3FenceMs = 0;
        int p3Total = EnvInt("WAVEE_PROBE_P3_FRAMES", 240, 0, 4096);
        for (int f = 0; f < p3Total && !window.IsClosed; f++)
        {
            var s = host.RunFrame();
            window.WaitForWork(16);
            p3Frames++;
            if (s.Presented) p3Presented++;
            p3AnimMs += s.AnimMs; p3FenceMs += s.FenceWaitMs;
            bool stationary = s.LyricsScrollMode == 0 && !s.LyricsContentDirtyAtRecord && s.MainScrollMode == 0 && !s.MainContentDirtyAtRecord;
            if (s.Presented && stationary) p3StaticButPresented++;   // force-present despite a static scene → skip-submit defeated
            AppendAdvanceRow(csv, "P3-idle", -1, f, "", s, prevLyMode, host.Scene, lyricsVp, mainVp, gpu);
            prevLyMode = s.LyricsScrollMode;
        }

        // ── P4: BUG2 — voice-transition REMOUNT + wipe/glow integrity ─────────────────────────────────────────────
        // Step the clock finely across interior line boundaries so VOICE crosses li-1→li while ACTIVE (the lead) stays
        // ~stationary near li — this is the exact frame the row just above active leaves the voice slot. On the fixed
        // (stable two-child) tree its node identity must NOT change (no remount → no re-bake flicker), and the karaoke
        // wipe/glow must still be present on every voice line (guards against the restructure breaking the feature).
        int p4Frames = 0, p4VoiceTransitions = 0, p4Remounts = 0, p4WipeMissing = 0, p4GlowDead = 0;
        if (lineCount >= 4)
        {
            view.ProbeForceSnapped();
            int b0 = Math.Max(2, lineCount / 3);
            int bEnd = Math.Min(lineCount - 2, b0 + EnvInt("WAVEE_PROBE_BUG2_BOUNDARIES", 8, 1, 4096));
            int prevVoice = -1;
            NodeHandle prevVoiceHandle = default;
            for (int li = b0; li <= bEnd && !window.IsClosed; li++)
            {
                long ls = view.ProbeLineStartMs(li);
                for (long dt = -80; dt <= 80 && !window.IsClosed; dt += 20)
                {
                    view.ProbeStep(ls + dt);
                    var s = host.RunFrame();
                    window.WaitForWork(16);
                    p4Frames++;
                    var ld = LyricsView.LastFrameDiagnostics;
                    int vln = ld.VoiceLine;
                    if (ld.VoiceChanged && prevVoice >= 0 && prevVoice != vln)
                    {
                        p4VoiceTransitions++;
                        var leaving = view.ProbeLineNode(prevVoice);   // the line that just left the voice slot (now dimmed)
                        if (!prevVoiceHandle.IsNull && !leaving.IsNull && !leaving.Equals(prevVoiceHandle)) p4Remounts++;
                    }
                    if (!ld.VoiceChanged && vln >= 0)   // integrity on a STABLE voice frame (the enter frame adds the wipe one frame later)
                    {
                        var wn = view.ProbeLineNode(vln);
                        if (wn.IsNull || !host.Scene.IsLive(wn) || !host.Scene.TryGetGlyphWipe(wn, out _)) p4WipeMissing++;
                        var gn = view.ProbeGlowNode(vln);
                        if (gn.IsNull || !host.Scene.IsLive(gn)) p4GlowDead++;
                    }
                    AppendAdvanceRow(csv, "P4-bug2", li, (int)dt, ld.VoiceChanged ? "voiceChg" : "", s, prevLyMode, host.Scene, lyricsVp, mainVp, gpu);
                    prevLyMode = s.LyricsScrollMode;
                    prevVoice = vln;
                    prevVoiceHandle = vln >= 0 ? view.ProbeLineNode(vln) : default;
                }
            }
        }

        // ── report ─────────────────────────────────────────────────────────────────────────────────────────────────
        bool bug1Fixed = p1BlurAbsentFrames == 0;
        var sb = new StringBuilder(2048);
        sb.AppendLine();
        sb.AppendLine("=== WAVEE LYRICS ADVANCE PROBE — synchronous, timer-decoupling-free ===");
        sb.AppendLine($"track: {TrackLabel(WaveeApp.ProbePlayback)}; lines={lineCount}; advances={Math.Max(0, endLine - startLine + 1)}; settleFrames/advance={settleFrames}");
        sb.AppendLine();
        sb.AppendLine("P1 stationary line-advance (the BUG1 scenario):");
        sb.AppendLine($"  frames={p1Frames}; programmatic-settle frames seen={p1SettleFrames}; DoF-absent frames (contentDirty & !userScroll & zero blur candidates)={p1BlurAbsentFrames}");
        sb.AppendLine($"  >>> BUG1 {(bug1Fixed ? "FIXED" : "PRESENT")} — lyric DoF was {(bug1Fixed ? "NEVER" : "STILL")} dropped while stationary-advancing (expect a settle edge each advance with lyMode 2->0, lyContentDirty=1, lyUserScroll=0, and a live blur candidate every content frame).");
        sb.AppendLine();
        sb.AppendLine("P2 main-scroll sibling isolation:");
        sb.AppendLine($"  frames={p2Frames}; frames where lyrics were user-scrolling/content-dirty during a MAIN scroll={p2LyricsTouched} (expect 0); frames the stationary lyrics DoF was HELD (served by its pin)={p2LyricsHeld} (expect >0 while the main page moves; 0 = the sibling-defer path is dead)");
        sb.AppendLine();
        sb.AppendLine("P3 skip-submit / present (BUG3 mechanism):");
        string p3rate = p3Frames > 0 ? $"{100.0 * p3Presented / p3Frames:0}%" : "n/a";
        string p3staticRate = p3Frames > 0 ? $"{100.0 * p3StaticButPresented / p3Frames:0}%" : "n/a";
        sb.AppendLine($"  frames={p3Frames}; presented={p3Presented} ({p3rate}); STATIC-scene-but-presented={p3StaticButPresented} ({p3staticRate} — these are skip-submit defeats from loop anims); meanAnimMs={(p3Frames > 0 ? p3AnimMs / p3Frames : 0):0.00}; meanFenceWaitMs={(p3Frames > 0 ? p3FenceMs / p3Frames : 0):0.00}");
        sb.AppendLine();
        bool wipeGlowIntact = p4WipeMissing == 0 && p4GlowDead == 0;
        sb.AppendLine("P4 BUG2 voice-transition (remount + wipe/glow integrity):");
        sb.AppendLine($"  frames={p4Frames}; voice transitions={p4VoiceTransitions}; leaving-line REMOUNTS={p4Remounts} (expect 0); voice-frames missing wipe={p4WipeMissing}; voice-frames dead glow={p4GlowDead}");
        sb.AppendLine($"  >>> BUG2 remount {(p4Remounts == 0 ? "GONE" : "STILL PRESENT")}; karaoke wipe/glow {(wipeGlowIntact ? "INTACT" : "BROKEN — REGRESSION, revert the LyricLineView restructure")}.");
        sb.AppendLine();
        sb.AppendLine("CSV per-frame columns: lyMode/lyPrevMode/lyUserScroll/lyContentDirty (record-time DoF-defer inputs), blurCandidates/blurGroups/blurSuppressed, presented, per-phase timing.");

        string report = sb.ToString();
        Console.Error.Write(report);
        if (csvPath is not null) WriteProbeFile(csvPath, csv.ToString(), "lyrics-advance");
        if (summaryPath is not null) WriteProbeFile(summaryPath, report, "lyrics-advance");
    }

    static void AppendAdvanceRow(StringBuilder csv, string phase, int line, int frame, string label, FrameStats s, int prevLyMode,
        FluentGpu.Scene.SceneStore scene, NodeHandle lyricsVp, NodeHandle mainVp, D3D12Device gpu)
    {
        var ld = LyricsView.LastFrameDiagnostics;
        float lyOff = 0f, lyTgt = 0f;
        if (!lyricsVp.IsNull && scene.IsLive(lyricsVp) && scene.TryGetScroll(lyricsVp, out var lsc)) { lyOff = lsc.OffsetY; lyTgt = lsc.TargetY; }
        csv.Append(phase).Append(',')
           .Append(line.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(frame.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(label).Append(',')
           .Append(ld.NowMs.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(ld.ActiveLine.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(ld.VoiceLine.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(ld.ActiveChanged ? '1' : '0').Append(',')
           .Append(ld.VoiceChanged ? '1' : '0').Append(',')
           .Append(s.LyricsScrollMode.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(prevLyMode.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(s.LyricsUserScrollActive ? '1' : '0').Append(',')
           .Append(s.LyricsContentDirtyAtRecord ? '1' : '0').Append(',')
           .Append(F(lyOff)).Append(',')
           .Append(F(lyTgt)).Append(',')
           .Append(s.BlurCandidateCount.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(s.BlurGroupCount.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(s.BlurSuppressedByScrollCount.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(s.BlurHoldCandidateCount.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(gpu.LastBlurCacheMiss.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(gpu.LastBlurHoldHit.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(gpu.LastBlurHoldFallback.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(F(s.FrameMs)).Append(',')
           .Append(F(s.RecordMs)).Append(',')
           .Append(F(s.SubmitMs)).Append(',')
           .Append(F(s.FenceWaitMs)).Append(',')
           .Append(F(s.PresentMs)).Append(',')
           .Append(s.Presented ? '1' : '0').Append(',')
           .Append(F(s.AnimMs)).Append(',')
           .Append(s.MainScrollMode.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(s.MainContentDirtyAtRecord ? '1' : '0').Append(',')
           .Append(CsvCell(TrackLabel(WaveeApp.ProbePlayback))).AppendLine();
    }

    static int EnvInt(string name, int fallback, int min, int max)
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
            return fallback;
        return Math.Clamp(v, min, max);
    }

    readonly record struct ProbeRoute(string Key, string? Arg, string Label);

    static List<ProbeRoute> BuildLiveLyricsRoutes(PlaybackBridge? b)
    {
        var routes = new List<ProbeRoute>(10);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string? key, string? arg, string label)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            string sig = key + "\u001F" + (arg ?? "");
            if (!seen.Add(sig)) return;
            routes.Add(new ProbeRoute(key, arg, SafeRouteLabel(label)));
        }

        var track = b?.CurrentTrack.Peek();
        Add("home", null, "home");
        Add("liked", null, "liked");
        Add(RichText.RouteForUri(b?.CurrentContext.Peek()), null, "current-context");
        Add(RichText.RouteForUri(track?.Album.Uri), null, "current-album");
        Add(RichText.RouteForUri(track?.Artists.Count > 0 ? track.Artists[0].Uri : null), null, "current-artist");
        Add("albums", null, "albums");
        Add("artists", null, "artists");
        Add("podcasts", null, "podcasts");
        Add("search", track?.Artists.Count > 0 ? track.Artists[0].Name : "the", "search");
        return routes;
    }

    static string SafeRouteLabel(string label)
    {
        var sb = new StringBuilder(label.Length);
        foreach (char ch in label)
            sb.Append(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '-');
        return sb.Length == 0 ? "route" : sb.ToString();
    }

    static string TrackLabel(PlaybackBridge? b)
    {
        var t = b?.CurrentTrack.Peek();
        if (t is null) return "(none)";
        string artist = t.Artists.Count > 0 ? t.Artists[0].Name : "";
        return artist.Length > 0 ? $"{t.Title} - {artist} [{t.Uri}]" : $"{t.Title} [{t.Uri}]";
    }

    static string CsvCell(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";

    static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    static void AppendLiveLyricsRow(StringBuilder csv, string phase, int frame, string label, double intervalMs, FrameStats s,
        int gen0, int gen1, FluentGpu.Scene.SceneStore scene, NodeHandle main, NodeHandle lyrics, PlaybackBridge? b, D3D12Device gpu)
    {
        var ld = LyricsView.LastFrameDiagnostics;
        csv.Append(phase).Append(',')
           .Append(frame.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(label).Append(',')
           .Append(F(intervalMs)).Append(',')
           .Append(F(s.FrameMs)).Append(',')
           .Append(F(s.FlushMs)).Append(',')
           .Append(F(s.LayoutMs)).Append(',')
           .Append(F(s.AnimMs)).Append(',')
           .Append(F(s.RecordMs)).Append(',')
           .Append(F(s.SubmitMs)).Append(',')
           .Append(F(s.FenceWaitMs)).Append(',')
           .Append(F(s.PresentMs)).Append(',')
           .Append(gen0.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(gen1.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(s.ComponentsRendered.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(s.NodesVisited.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(s.DrawCommandCount.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(s.BlurCandidateCount.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(s.BlurGroupCount.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(s.BlurSuppressedByScrollCount.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(s.BlurHoldCandidateCount.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(s.EdgeFadeGroupCount.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(gpu.LastBlurCacheHit.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(gpu.LastBlurCacheMiss.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(gpu.LastBlurHoldHit.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(gpu.LastBlurHoldFallback.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(gpu.LastOpacityGroups.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(ld.NowMs.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(ld.AuthMs.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(ld.ActiveLine.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(ld.VoiceLine.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(ld.ActiveChanged ? '1' : '0').Append(',')
           .Append(ld.ScrollSnapped ? '1' : '0').Append(',')
           .Append((b?.PositionMs.Peek() ?? 0).ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append((b?.IsPlaying.Peek() ?? false) ? '1' : '0');
        AppendScrollCells(csv, scene, main);
        AppendScrollCells(csv, scene, lyrics);
        csv.Append(',').Append(CsvCell(TrackLabel(b))).AppendLine();
    }

    static void AppendScrollCells(StringBuilder csv, FluentGpu.Scene.SceneStore scene, NodeHandle node)
    {
        if (node.IsNull || !scene.IsLive(node) || !scene.HasScroll(node) || !scene.TryGetScroll(node, out var sc))
        {
            csv.Append(",,,,");
            return;
        }
        int transformDirty = 0;
        if (!sc.ContentNode.IsNull && scene.IsLive(sc.ContentNode) && (scene.Flags(sc.ContentNode) & NodeFlags.TransformDirty) != 0)
            transformDirty = 1;
        csv.Append(',').Append(F(sc.OffsetY))
           .Append(',').Append(F(sc.TargetY))
           .Append(',').Append(sc.Phase.ToString(CultureInfo.InvariantCulture))
           .Append(',').Append(transformDirty.ToString(CultureInfo.InvariantCulture));
    }

    static void AppendIntervalSummary(StringBuilder sb, string label, List<double> intervals)
    {
        if (intervals.Count == 0)
        {
            sb.AppendLine($"{label}: no frames");
            return;
        }
        var a = intervals.ToArray();
        Array.Sort(a);
        double total = 0, worst = 0;
        int over16 = 0, over33 = 0;
        foreach (double v in intervals)
        {
            total += v;
            if (v > worst) worst = v;
            if (v > 16.7) over16++;
            if (v > 33.3) over33++;
        }
        double mean = total / intervals.Count;
        sb.AppendLine($"{label}: n={intervals.Count} mean={mean:0.0}ms ({(mean > 0 ? 1000.0 / mean : 0):0}fps) p50={Pct(a, 50):0.0} p90={Pct(a, 90):0.0} p99={Pct(a, 99):0.0} worst={worst:0.0}ms >16.7={over16} >33.3={over33}");
    }

    static NodeHandle FindMainScrollViewport(FluentGpu.Scene.SceneStore scene, NodeHandle lyrics)
    {
        var best = default(NodeHandle);
        float bestScore = -1f;
        RectF lyricsRect = default;
        if (!lyrics.IsNull && scene.IsLive(lyrics)) lyricsRect = scene.AbsoluteRect(lyrics);
        float rootW = scene.Root.IsNull ? 0f : scene.AbsoluteRect(scene.Root).W;

        var stack = new Stack<NodeHandle>();
        if (!scene.Root.IsNull) stack.Push(scene.Root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n.IsNull || !scene.IsLive(n)) continue;
            if (n != lyrics && scene.HasScroll(n) && scene.TryGetScroll(n, out var sc) && sc.ViewportH > 50f && sc.ContentH > sc.ViewportH + 1f)
            {
                var r = scene.AbsoluteRect(n);
                if (!lyrics.IsNull && lyricsRect.W > 0f && r.X >= lyricsRect.X - 4f)
                {
                    for (var c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c)) stack.Push(c);
                    continue;
                }
                if (rootW > 0f && r.X > rootW * 0.72f)
                {
                    for (var c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c)) stack.Push(c);
                    continue;
                }
                float w = sc.ViewportW > 20f ? sc.ViewportW : r.W;
                float h = sc.ViewportH > 20f ? sc.ViewportH : r.H;
                float score = w * h + MathF.Min(sc.ContentH - sc.ViewportH, 5000f);
                if (score > bestScore) { bestScore = score; best = n; }
            }
            for (var c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c)) stack.Push(c);
        }
        return best;
    }

    static string? ProbeOutputDir()
    {
        string? dir = Environment.GetEnvironmentVariable("WAVEE_PROBE_OUT");
        if (string.IsNullOrWhiteSpace(dir))
            dir = Path.Combine(Environment.CurrentDirectory, ".wavee-diagnostics");
        try
        {
            Directory.CreateDirectory(dir);
            return dir;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[home-scroll-probe] file output disabled: cannot create {dir}: {ex.Message}");
            return null;
        }
    }

    static void WriteProbeFile(string path, string text, string tag)
    {
        try
        {
            File.WriteAllText(path, text);
            Console.Error.WriteLine($"[{tag}] wrote {path}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{tag}] failed to write {path}: {ex.Message}");
        }
    }

    // WAVEE_LYRICS_PROBE=1 (run with WAVEE_LYRICS_OPEN=1 to open the rail and --fake for the 40-line synced doc): measure
    // the lyrics surface, which renders ~10-13 per-line depth-of-field self-blur layers every frame. Reports the perceived
    // FPS at REAL vsync (GPU stalls included) AND the vsync-suppressed CPU work breakdown (flush/layout/anim/record/submit)
    // + the rendered-frame count (the idle-spin signal), for an IDLE (paused) hold and a wheel SCROLL through the lines.
    static void RunLyricsProbe(AppHost host, Win32Window window, D3D12Device gpu)
    {
        void FrameFree() { if (!window.IsClosed) { gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce(); host.RunFrame(); } }
        void Settle(int n) { for (int i = 0; i < n && !window.IsClosed; i++) FrameFree(); }

        // Optional resolution stress (WAVEE_PROBE_W/H): the layered-path submit + blit scale with the canvas pixel area,
        // so a high-DPI / large window is where the lyrics blur actually drops frames — reproduce it here.
        int pw = int.TryParse(Environment.GetEnvironmentVariable("WAVEE_PROBE_W"), out var w0) ? w0 : 0;
        int ph = int.TryParse(Environment.GetEnvironmentVariable("WAVEE_PROBE_H"), out var h0) ? h0 : 0;
        if (pw > 200 && ph > 200) { window.SetClientSize(pw, ph); Console.Error.WriteLine($"[lyrics-probe] window -> {pw}x{ph}"); }

        Console.Error.WriteLine("[lyrics-probe] warmup (rail opens via WAVEE_LYRICS_OPEN; lyrics doc loads)");
        Settle(60);
        System.Threading.Thread.Sleep(500);     // FakeData.GetLyricsAsync Task.Delay(150) + paint settle
        Settle(80);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

        var vp = FindLyricsViewport(host.Scene);
        if (vp.IsNull)
        {
            Console.Error.WriteLine("[lyrics-probe] NO lyrics viewport found (rail not open / no current track / doc empty). Scrollers seen:");
            DumpScrollers(host.Scene);
            return;
        }
        var vr = host.Scene.AbsoluteRect(vp);
        host.Scene.TryGetScroll(vp, out var s0);
        Console.Error.WriteLine($"[lyrics-probe] lyrics viewport rect=({vr.X:0},{vr.Y:0} {vr.W:0}x{vr.H:0}) content={s0.ContentH:0} viewH={s0.ViewportH:0}");

        MeasureLyrics("IDLE  ", host, window, gpu, null);

        float vh = s0.ViewportH > 20f ? s0.ViewportH : vr.H;
        var pos = new Point2(vr.X + vr.W * 0.5f, vr.Y + vh * 0.5f);
        window.QueueInput(new InputEvent(InputKind.PointerMove, pos, 0, 0));
        Settle(2);
        MeasureLyrics("SCROLL", host, window, gpu, i => window.QueueInput(new InputEvent(InputKind.Wheel, pos, 0, 0, ((i / 18) & 1) == 0 ? +50f : -50f)));

        Console.Error.WriteLine("[lyrics-probe] done");
    }

    static void MeasureLyrics(string tag, AppHost host, Win32Window window, D3D12Device gpu, Action<int>? inject)
    {
        // (1) perceived FPS at TRUE vsync cadence (present waits for the vblank → a GPU-bound frame stretches the interval).
        var iv = new List<double>(260);
        long prev = Stopwatch.GetTimestamp();
        for (int i = 0; i < 220 && !window.IsClosed; i++)
        {
            inject?.Invoke(i);
            host.RunFrame();
            long now = Stopwatch.GetTimestamp();
            iv.Add((now - prev) * 1000.0 / Stopwatch.Frequency); prev = now;
        }
        var a = iv.ToArray(); double tot = 0, worst = 0; foreach (var v in a) { tot += v; if (v > worst) worst = v; }
        double mean = a.Length > 0 ? tot / a.Length : 0; int o16 = 0, o33 = 0; foreach (var v in a) { if (v > 16.7) o16++; if (v > 33.3) o33++; }

        // (2) vsync-suppressed CPU work breakdown + rendered-frame count (IDLE should render ~0 — a spin renders ~all).
        double sFrame = 0, sFlush = 0, sLayout = 0, sAnim = 0, sRecord = 0, sSubmit = 0, wWorst = 0;
        int rendered = 0, total = 0, g0 = GC.CollectionCount(0); long alloc0 = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 160 && !window.IsClosed; i++)
        {
            inject?.Invoke(i);
            gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce();
            var s = host.RunFrame();
            total++;
            if (s.Rendered || s.DrawCommandCount > 0)
            {
                rendered++;
                sFrame += s.FrameMs; sFlush += s.FlushMs; sLayout += s.LayoutMs; sAnim += s.AnimMs; sRecord += s.RecordMs; sSubmit += s.SubmitMs;
                if (s.FrameMs > wWorst) wWorst = s.FrameMs;
            }
        }
        int dg0 = GC.CollectionCount(0) - g0;
        int den = Math.Max(1, rendered);
        double allocKb = (GC.GetAllocatedBytesForCurrentThread() - alloc0) / 1024.0 / den;
        Console.Error.WriteLine($"[lyrics-fps] {tag} perceived(vsync): {a.Length}f mean {mean:0.0}ms ({(mean > 0 ? 1000.0 / mean : 0):0}fps) worst {worst:0.0}ms | <60fps={o16} <30fps={o33}");
        Console.Error.WriteLine($"             work(no-vsync): rendered {rendered}/{total} | frame {sFrame / den:0.00}ms (worst {wWorst:0.00}) = flush {sFlush / den:0.00} + layout {sLayout / den:0.00} + anim {sAnim / den:0.00} + record {sRecord / den:0.00} + submit {sSubmit / den:0.00} | alloc {allocKb:0.0}KB/f gen0={dg0}");
    }

    static NodeHandle FindLyricsViewport(FluentGpu.Scene.SceneStore scene)
    {
        var best = default(NodeHandle); float bestX = -1f;
        var stack = new Stack<NodeHandle>(); if (!scene.Root.IsNull) stack.Push(scene.Root);
        while (stack.Count > 0)
        {
            var n = stack.Pop(); if (n.IsNull || !scene.IsLive(n)) continue;
            if (scene.HasScroll(n) && scene.TryGetScroll(n, out var sc) && sc.ViewportH > 50f && sc.ContentH > sc.ViewportH + 1f)
            {
                var r = scene.AbsoluteRect(n);
                if (r.X > bestX) { bestX = r.X; best = n; }   // rightmost content scroller = the lyrics rail (right side)
            }
            for (var c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c)) stack.Push(c);
        }
        return best;
    }

    static void DumpScrollers(FluentGpu.Scene.SceneStore scene)
    {
        var stack = new Stack<NodeHandle>(); if (!scene.Root.IsNull) stack.Push(scene.Root);
        while (stack.Count > 0)
        {
            var n = stack.Pop(); if (n.IsNull || !scene.IsLive(n)) continue;
            if (scene.HasScroll(n) && scene.TryGetScroll(n, out var sc))
            { var r = scene.AbsoluteRect(n); Console.Error.WriteLine($"  scroller rect=({r.X:0},{r.Y:0} {r.W:0}x{r.H:0}) viewH={sc.ViewportH:0} contentH={sc.ContentH:0}"); }
            for (var c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c)) stack.Push(c);
        }
    }

    static void Run(AppHost host, Win32Window window, D3D12Device gpu)
    {
        Directory.CreateDirectory(@"C:\tmp");
        var csv = new StringBuilder(1 << 16);
        csv.AppendLine("phase,frame,label,frameMs,flushMs,layoutMs,animMs,recordMs,submitMs,gen0,gen1,comps,nodes,draws,overBudget");
        bool keepVsync = Diag.EnvFlag("WAVEE_PROBE_VSYNC");

        // One measured frame under `phase`, tagged `label`. Strips the throttle (unless WAVEE_PROBE_VSYNC) so FrameMs is
        // pure production cost; captures the Gen0/Gen1 collection deltas so a GC spike is distinguishable from a work spike.
        FrameStats Measure(Phase phase, string label)
        {
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1);
            if (!keepVsync) { gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce(); }
            var s = host.RunFrame();
            int dg0 = GC.CollectionCount(0) - g0, dg1 = GC.CollectionCount(1) - g1;
            if (s.Rendered || s.DrawCommandCount > 0)
            {
                phase.Ms.Add(s.FrameMs);
                bool over = s.FrameMs > BudgetMs;
                if (over) { phase.OverBudget++; if (dg0 > 0) phase.OverBudgetGc++; }
                static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
                csv.Append(phase.Name).Append(',').Append(phase.Ms.Count.ToString(CultureInfo.InvariantCulture)).Append(',')
                   .Append(label).Append(',')
                   .Append(s.FrameMs.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                   .Append(F(s.FlushMs)).Append(',').Append(F(s.LayoutMs)).Append(',').Append(F(s.AnimMs)).Append(',')
                   .Append(F(s.RecordMs)).Append(',').Append(F(s.SubmitMs)).Append(',')
                   .Append(dg0).Append(',').Append(dg1).Append(',')
                   .Append(s.ComponentsRendered).Append(',').Append(s.NodesVisited).Append(',').Append(s.DrawCommandCount).Append(',')
                   .Append(over ? '1' : '0').AppendLine();
            }
            return s;
        }

        void Nav(string key, string? arg) => WaveeShell.ProbeNav!(key, arg);
        void Settle(int n) { for (int i = 0; i < n && !window.IsClosed; i++) host.RunFrame(); }

        // ── Warmup (not measured); collect once so the baseline starts from a known GC state. ──
        Console.Error.WriteLine("[wavee-nav-probe] warmup");
        for (int i = 0; i < 80 && !window.IsClosed; i++) { gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce(); host.RunFrame(); }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

        // ── Phase 0: HOME ↔ DETAIL (the named case). Bounce home → a fresh playlist → home → next playlist …, holding a
        //    few settle frames each so the cold-mount transition frame AND its settle are captured + labelled by route. ──
        var homeDetail = new Phase("home<->detail");
        Console.Error.WriteLine("[wavee-nav-probe] home<->detail (the named case)");
        bool capture = Diag.EnvFlag("WAVEE_PB_CAPTURE");   // dump the first home→detail transition frame-by-frame to PNG

        // Player-bar-glitch repro: the EXACT Home-card click path (preview → DetailShell preview-path mount + Hero fly),
        // which the sidebar-style ProbeNav never exercises. Home must be mounted first (the fly's source card).
        if (capture && WaveeShell.ProbeCardNav is not null)
        {
            Console.Error.WriteLine("[wavee-nav-probe] card-click capture (home→detail via preview + Hero fly)");
            Nav("home", null);
            for (int f = 0; f < 40 && !window.IsClosed; f++) host.RunFrame();
            System.Threading.Thread.Sleep(700);                         // let card covers decode (async) so the fly has an image source
            for (int f = 0; f < 20 && !window.IsClosed; f++) host.RunFrame();
            string? flyKey = host.FirstMorphKey;                        // a REAL home-card key → the morph actually FLIES
            Console.Error.WriteLine($"[wavee-nav-probe] fly key = {flyKey ?? "(none found)"}");
            WaveeShell.ProbeCardNav(flyKey ?? "pl:spotify:playlist:pl0", null, true);
            for (int f = 0; f < 12 && !window.IsClosed; f++)
            {
                gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce();
                host.RunFrame();
                var px = gpu.CaptureBgra(out int cw, out int ch); PngWriter.WriteBgra($@"C:\tmp\card_{f:D2}.png", px, cw, ch);
            }
        }
        for (int rep = 0; rep < 6 && !window.IsClosed; rep++)
        {
            Nav("home", null);
            for (int f = 0; f < 8 && !window.IsClosed; f++) Measure(homeDetail, "home");
            var (k, a) = HeavyRoutes[2 + rep % 6];   // pl0..pl5 (fresh each bounce → real cold mount)
            Nav(k, a);
            for (int f = 0; f < 8 && !window.IsClosed; f++)
            {
                Measure(homeDetail, k);
                if (capture && rep == 0) { var px = gpu.CaptureBgra(out int cw, out int ch); PngWriter.WriteBgra($@"C:\tmp\pb_{f:D2}.png", px, cw, ch); }
            }
        }

        // ── Phase 0b: ALBUM → ALBUM (the reported related-albums hitch). Two sub-phases over DISTINCT heavy album pages
        //    (cover + tracks + related/similar/merch trailing shelves): SKELETON = today's bare related-card nav (no preview
        //    → DetailPage.Skel.Region full-page remount on every hop); PREVIEW = the fix (a related card now opens via
        //    DetailNav.OpenAlbum, stashing the card's partial model → DetailPage reuses the mounted shell in place). Same
        //    routes, same settle frames, started from album land (a reused detail slot) — so the delta IS the remount the
        //    fix removes. Distinct album indices per sub-phase so the second isn't warmed by the first's data/image cache. ──
        var albumSkel = new Phase("album skel");
        var albumPrev = new Phase("album preview");
        if (WaveeShell.ProbeOpenAlbum is not null)
        {
            const int AlbumSettle = 12;
            var skelAlbums = new List<Wavee.Core.Album>(8);
            var prevAlbums = new List<Wavee.Core.Album>(8);
            for (int i = 0; i < 8; i++) { skelAlbums.Add(Wavee.Core.FakeData.Album(120 + i)); prevAlbums.Add(Wavee.Core.FakeData.Album(140 + i)); }
            Console.Error.WriteLine("[wavee-nav-probe] album→album (skeleton vs preview, 8 hops each)");

            // OLD path: enter album land, then hop album→album via BARE nav (no preview) — skeleton remount each hop.
            Nav("home", null); Settle(10);
            Nav("album:" + skelAlbums[0].Uri, skelAlbums[0].Name); Settle(AlbumSettle);
            for (int i = 1; i < skelAlbums.Count && !window.IsClosed; i++)
            {
                Nav("album:" + skelAlbums[i].Uri, skelAlbums[i].Name);   // bare GoNav — no preview → Skel.Region remount
                for (int f = 0; f < AlbumSettle && !window.IsClosed; f++) Measure(albumSkel, skelAlbums[i].Uri);
            }

            // FIXED path: same shape, but each hop opens via DetailNav.OpenAlbum (stash preview + nav) — reused-shell fast path.
            Nav("home", null); Settle(10);
            WaveeShell.ProbeOpenAlbum(prevAlbums[0]); Settle(AlbumSettle);
            for (int i = 1; i < prevAlbums.Count && !window.IsClosed; i++)
            {
                WaveeShell.ProbeOpenAlbum(prevAlbums[i]);                // the fixed related-card path
                for (int f = 0; f < AlbumSettle && !window.IsClosed; f++) Measure(albumPrev, prevAlbums[i].Uri);
            }
        }

        // ── Phase 1: NAV-SETTLE across all route types (2 sweeps). ──
        var navSettle = new Phase("nav-settle");
        const int SettleFrames = 14;
        var sweep = new List<(string, string?)>();
        for (int s = 0; s < HeavyRoutes.Length; s++) { sweep.Add(HeavyRoutes[s]); if (s < CheapRoutes.Length) sweep.Add(CheapRoutes[s]); }
        Console.Error.WriteLine($"[wavee-nav-probe] nav-settle ({sweep.Count} routes x 2 sweeps)");
        for (int rep = 0; rep < 2 && !window.IsClosed; rep++)
            foreach (var (key, arg) in sweep)
            {
                if (window.IsClosed) break;
                Nav(key, arg);
                for (int f = 0; f < SettleFrames && !window.IsClosed; f++) Measure(navSettle, key);
            }

        // ── Phase 2: NAV-HAMMER (route swap every frame — worst-case churn). ──
        var hammer = new Phase("nav-hammer");
        Console.Error.WriteLine("[wavee-nav-probe] nav-hammer (400 frames)");
        for (int i = 0; i < 400 && !window.IsClosed; i++)
        {
            var (key, arg) = HeavyRoutes[i % HeavyRoutes.Length];
            Nav(key, arg);
            Measure(hammer, key);
        }

        // ── Phase 3: BACK / FORWARD (KeepAlive page transition; connected cover animation is disabled in Wavee). ──
        var backfwd = new Phase("back-forward");
        for (int i = 0; i < HeavyRoutes.Length && !window.IsClosed; i++) { var (k, a) = HeavyRoutes[i]; Nav(k, a); Settle(3); }
        Console.Error.WriteLine("[wavee-nav-probe] back-forward (240 frames)");
        for (int i = 0; i < 240 && !window.IsClosed; i++)
        {
            if ((i / 8) % 2 == 0) WaveeShell.ProbeBack?.Invoke(); else WaveeShell.ProbeForward?.Invoke();
            Measure(backfwd, "nav");
            // Capture the FIRST back/forward transition (the connected-animation Hero-fly path) frame-by-frame.
            if (capture && i < 16) { var px = gpu.CaptureBgra(out int cw, out int ch); PngWriter.WriteBgra($@"C:\tmp\bf_{i:D2}.png", px, cw, ch); }
        }

        // ── Phase 4: SCROLL via REAL mouse-wheel INPUT. NOT WriteScrollOffset (that bypasses input): here every notch is an
        //    InputKind.Wheel event injected through window.QueueInput → PumpInto → the dispatcher ring → DispatchWheel/ScrollAt
        //    (hit-test the cursor pos, route to the nearest VERTICAL scroller), and SmoothScroll eases each +60 DIP notch via
        //    the ScrollIntegrator (inertia). So this measures the full real path a mouse drives: hit-test + wheel routing + the
        //    eased per-frame re-layout / virtualization re-realize / re-record, including the inertial COAST after a flick. ──
        var scroll = new Phase("scroll (real wheel)");
        if (Diag.EnvFlag("WAVEE_LIKED_SHOT")) { window.SetClientSize(1456, 820); Settle(12); Nav("liked", null); Settle(50); var px0 = gpu.CaptureBgra(out int cw0, out int ch0); PngWriter.WriteBgra(@"C:\tmp\liked_header.png", px0, cw0, ch0); }
        // Navigate to a TWO-COLUMN playlist: its track list cross-stretches to the rail height → a real non-zero viewport that
        // renders rows the wheel can hit-test. (The single-column Liked list measures a 0-height viewport — a pre-existing bug
        // that leaves it empty + un-hit-testable; not what we want to audit scroll feel on.)
        Nav("home", null); Settle(20);
        System.Threading.Thread.Sleep(600);
        for (int f = 0; f < 20 && !window.IsClosed; f++) host.RunFrame();
        var skeys = new List<string>(); host.CollectMorphKeys(skeys);
        Nav(skeys.Count > 0 ? skeys[0] : "pl:local:1", null); Settle(40);
        var viewport = FindLargestScrollViewport(host.Scene);
        if (viewport.IsNull) Console.Error.WriteLine("[wavee-nav-probe] scroll: no scroll viewport — skipped");
        else
        {
            var vr = host.Scene.AbsoluteRect(viewport);
            host.Scene.TryGetScroll(viewport, out var s0);
            // Cursor over the viewport centre (layout-DIP, the hit-test space). The scroll node's own box height can be 0, so
            // use the ScrollState ViewportH to place the point over a REAL row (so HitTestAny descends to it and routes the wheel).
            float vh = s0.ViewportH > 20f ? s0.ViewportH : (vr.H > 20f ? vr.H : 400f);
            var pos = new Point2(vr.X + vr.W * 0.5f, vr.Y + vh * 0.5f);
            window.QueueInput(new InputEvent(InputKind.PointerMove, pos, 0, 0));   // a real mouse hovers first → routes the wheel here + reveals the auto-hide scrollbar
            Settle(2);
            Console.Error.WriteLine($"[wavee-nav-probe] scroll: REAL wheel @ ({pos.X:0},{pos.Y:0}) viewport content {s0.ContentH:0} viewH {s0.ViewportH:0}");

            // Brisk continuous spin — ~1 notch/frame DOWN then UP. The offset crosses item boundaries continuously, so the
            // virtualized list recycles/re-realizes rows every frame (the heaviest steady-scroll case). Inject + measure each frame.
            for (int i = 0; i < 200 && !window.IsClosed; i++)
            {
                window.QueueInput(new InputEvent(InputKind.Wheel, pos, 0, 0, i < 100 ? +60f : -60f));
                Measure(scroll, i < 100 ? "wheel-down" : "wheel-up");
            }
            // Flick-and-coast: a 6-notch burst, then STOP — the ScrollIntegrator eases the residual to rest. These COAST frames
            // are exactly what a programmatic offset-set can never see (it lands instantly); a real flick animates for ~15 frames.
            for (int rep = 0; rep < 6 && !window.IsClosed; rep++)
            {
                for (int k = 0; k < 6 && !window.IsClosed; k++) { window.QueueInput(new InputEvent(InputKind.Wheel, pos, 0, 0, +60f)); Measure(scroll, "flick"); }
                for (int st = 0; st < 16 && !window.IsClosed; st++) Measure(scroll, "coast");
            }
            host.Scene.TryGetScroll(viewport, out var s1);
            bool moved = Math.Abs(s1.OffsetY - s0.OffsetY) > 1f;
            Console.Error.WriteLine($"[wavee-nav-probe] scroll: endOff {s1.OffsetY:0} — wheel input {(moved ? "TOOK EFFECT (offset moved by dispatch+ease)" : "NO-OP — routing FAILED")}");

            // REAL-vsync wheel-scroll FPS: drive notches at TRUE present cadence (no vsync suppression) and time the wall-clock
            // frame interval — the FPS a user actually perceives while spinning the wheel. Oscillate so the list never sits clamped.
            var iv = new List<double>(220);
            long prev = Stopwatch.GetTimestamp();
            for (int i = 0; i < 200 && !window.IsClosed; i++)
            {
                window.QueueInput(new InputEvent(InputKind.Wheel, pos, 0, 0, ((i / 20) % 2 == 0) ? +60f : -60f));   // 20 down / 20 up → always moving
                host.RunFrame();
                long now = Stopwatch.GetTimestamp(); iv.Add((now - prev) * 1000.0 / Stopwatch.Frequency); prev = now;
            }
            var a = iv.ToArray(); double tot = 0, worst = 0; foreach (var v in a) { tot += v; if (v > worst) worst = v; }
            double mean = a.Length > 0 ? tot / a.Length : 0; int o16 = 0; foreach (var v in a) if (v > 16.7) o16++;
            Console.Error.WriteLine($"[scroll-fps] REAL wheel scroll at vsync: {a.Length} frames mean {mean:0.0}ms ({(mean > 0 ? 1000.0 / mean : 0):0} fps) worst {worst:0.0}ms ({(worst > 0 ? 1000.0 / worst : 0):0} fps) >16.7ms(<60fps)={o16}");
        }

        // ── Phase 5: THEME TOGGLE (RethemeAll re-renders every component + arms the cross-fade). ──
        var theme = new Phase("theme-toggle");
        Nav("home", null); Settle(6);
        Console.Error.WriteLine("[wavee-nav-probe] theme-toggle (240 frames)");
        for (int i = 0; i < 240 && !window.IsClosed; i++)
        {
            if (i % 20 == 0) WaveeShell.ProbeTheme?.Invoke();
            Measure(theme, i % 20 == 0 ? "flip" : "settle");
        }

        // ── Phase 6: IDLE (steady-state floor). ──
        var idle = new Phase("idle");
        Nav("home", null); Settle(10);
        Console.Error.WriteLine("[wavee-nav-probe] idle (180 frames)");
        for (int i = 0; i < 180 && !window.IsClosed; i++) Measure(idle, "idle");

        // ── Phase: CONNECTED ANIMATION (Hero cover fly) cost. Trigger REAL flies (home→detail→back) and measure the
        //    per-frame production cost WHILE a fly is in flight, vs the SAME navigation on the SAME path WITHOUT the fly
        //    (doMorph=false) — so the delta is purely the connected-animation overhead. Needs card covers loaded first. ──
        var connFly = new Phase("conn-anim FLY");
        var connNo = new Phase("conn-anim no-fly");
        if (WaveeShell.ProbeCardNav is not null)
        {
            Nav("home", null);
            for (int f = 0; f < 40 && !window.IsClosed; f++) host.RunFrame();
            System.Threading.Thread.Sleep(800);                        // let card covers decode (the fly needs an image source)
            for (int f = 0; f < 20 && !window.IsClosed; f++) host.RunFrame();
            string? flyKey = host.FirstMorphKey;
            Console.Error.WriteLine($"[wavee-nav-probe] conn-anim fly key = {flyKey ?? "(none)"}");
            if (flyKey is not null)
            {
                var keys = new List<string>(); host.CollectMorphKeys(keys);
                // FRESH flies (DISTINCT cards → cold mount + load + fly) WITH per-segment capture, to pin where the
                // ~9-10ms/frame that drops the fly to 60fps actually goes (flush=realize, record=image crossfades, …).
                for (int ki = 0; ki < keys.Count && ki < 12 && !window.IsClosed; ki++)
                {
                    Nav("home", null);
                    for (int f = 0; f < 16 && !window.IsClosed; f++) host.RunFrame();
                    WaveeShell.ProbeCardNav(keys[ki], null, true);
                    for (int f = 0; f < 16 && !window.IsClosed; f++) Measure(connFly, "fresh");
                }
                for (int rep = 0; rep < 14 && !window.IsClosed; rep++)   // SAME nav, NO fly (preview path, no morph)
                {
                    WaveeShell.ProbeCardNav(flyKey, null, false);
                    for (int f = 0; f < 14 && !window.IsClosed; f++) Measure(connNo, "fwd");
                    WaveeShell.ProbeBack?.Invoke();
                    for (int f = 0; f < 14 && !window.IsClosed; f++) Measure(connNo, "back");
                }

                // ── REAL FPS of a COMPLETE Hero fly: present at TRUE vsync cadence (do NOT suppress vsync/latency) and time
                //    the WALL-CLOCK frame interval click→settle, so this is the FPS actually perceived during the fly. Fly to
                //    DISTINCT home-card pages so each is a FRESH (uncached) cold mount + fly — the realistic worst case. ──
                host.CollectMorphKeys(keys);   // reuse the list collected above
                Console.Error.WriteLine($"[fly-fps] complete connected animations at REAL vsync cadence — {keys.Count} fresh home-card keys (interval ms / fps):");
                for (int fly = 0; fly < keys.Count && fly < 6 && !window.IsClosed; fly++)
                {
                    Nav("home", null);
                    for (int f = 0; f < 18 && !window.IsClosed; f++) host.RunFrame();   // home live (the fly's source card) — not measured
                    var iv = new List<double>(96);
                    double sFlush = 0, sLayout = 0, sAnim = 0, sRecord = 0, sSubmit = 0; int sN = 0;
                    WaveeShell.ProbeCardNav(keys[fly], null, true);                       // CLICK a FRESH card — fly + cold mount start
                    long prev = Stopwatch.GetTimestamp();
                    for (int f = 0; f < 60 && !window.IsClosed; f++)
                    {
                        var s = host.RunFrame();                                         // vsync NOT suppressed → real present pacing
                        long now = Stopwatch.GetTimestamp();
                        double dms = (now - prev) * 1000.0 / Stopwatch.Frequency;
                        iv.Add(dms); prev = now;
                        if (f < 20) { sFlush += s.FlushMs; sLayout += s.LayoutMs; sAnim += s.AnimMs; sRecord += s.RecordMs; sSubmit += s.SubmitMs; sN++; }
                    }
                    var arr = iv.ToArray();
                    double tot = 0, worst = 0; foreach (var v in arr) { tot += v; if (v > worst) worst = v; }
                    double mean = tot / arr.Length;
                    int over16 = 0, over33 = 0; foreach (var v in arr) { if (v > 16.7) over16++; if (v > 33.3) over33++; }
                    Console.Error.WriteLine($"[fly-fps] fresh fly#{fly}: {arr.Length} frames {tot:0}ms | mean {mean:0.0}ms ({1000.0/mean:0} fps) | worst {worst:0.0}ms ({1000.0/worst:0} fps) | >16.7ms(<60fps)={over16} >33ms(<30fps)={over33}");
                    if (sN > 0) Console.Error.WriteLine($"           REAL-vsync segs(first {sN}): flush {sFlush/sN:0.0} layout {sLayout/sN:0.0} anim {sAnim/sN:0.0} record {sRecord/sN:0.0} submit {sSubmit/sN:0.0}");
                    var head = new StringBuilder("           first 18 intervals(ms):");
                    for (int i = 0; i < Math.Min(18, arr.Length); i++) head.Append(' ').Append(arr[i].ToString("0", CultureInfo.InvariantCulture));
                    Console.Error.WriteLine(head.ToString());
                }
            }
        }

        // ── Report ──
        var phases = new[] { idle, scroll, backfwd, connNo, connFly, albumSkel, albumPrev, homeDetail, navSettle, hammer, theme };
        var all = new Phase("ALL");
        foreach (var p in phases) { all.Ms.AddRange(p.Ms); all.OverBudget += p.OverBudget; all.OverBudgetGc += p.OverBudgetGc; }

        var sb = new StringBuilder(8192);
        sb.AppendLine();
        sb.AppendLine("=== WAVEE STRESS — per-frame PRODUCTION time (ms); target = every frame < 8.33 ms (one 120 Hz vblank) ===");
        sb.AppendLine(keepVsync ? "(WAVEE_PROBE_VSYNC: vblank-paced — FrameMs includes the present wait)" : "(vsync/latency throttle removed → pure work cost)");
        sb.AppendLine();
        sb.AppendLine($"{"phase",-14} {"n",5} {"p50",6} {"p90",6} {"p99",7} {"p99.9",7} {"max",8}   {"over8.3ms",10} {"(ofwhich GC)",12}");
        foreach (var p in phases) sb.AppendLine(Format(p));
        sb.AppendLine(new string('-', 96));
        sb.AppendLine(Format(all));
        sb.AppendLine();
        sb.AppendLine("over8.3ms = frames whose WORK would miss a vblank (the stutters). (ofwhich GC) = those coinciding with a Gen0+ collection.");

        // Worst 15 frames overall, broken down by segment — the exact spikes to remedy.
        var rows = new List<(string Tag, double Ms, double Flush, double Layout, double Anim, double Record, double Submit, int G0)>();
        foreach (var line in csv.ToString().Split('\n'))
        {
            var c = line.Split(',');
            if (c.Length < 15 || c[0] == "phase") continue;
            double D(int i) => double.TryParse(c[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
            int.TryParse(c[9], out int g0);
            rows.Add((c[0] + ":" + c[2], D(3), D(4), D(5), D(6), D(7), D(8), g0));
        }
        rows.Sort((a, b) => b.Ms.CompareTo(a.Ms));
        sb.AppendLine();
        sb.AppendLine($"Worst 15 frames — {"total",7} = {"flush",6} + {"layout",6} + {"anim",5} + {"record",6} + {"submit",6}  (gc=Gen0)  transition");
        for (int i = 0; i < Math.Min(15, rows.Count); i++)
        {
            var r = rows[i];
            sb.AppendLine($"  {r.Ms,7:0.00} = {r.Flush,6:0.00} + {r.Layout,6:0.00} + {r.Anim,5:0.00} + {r.Record,6:0.00} + {r.Submit,6:0.00}  gc={r.G0}  {r.Tag}");
        }

        string report = sb.ToString();
        Console.Error.Write(report);
        File.WriteAllText(@"C:\tmp\wavee-nav-probe.csv", csv.ToString());
        File.WriteAllText(@"C:\tmp\wavee-nav-probe-summary.txt", report);
        Console.Error.WriteLine("[wavee-nav-probe] wrote C:\\tmp\\wavee-nav-probe.csv + -summary.txt");
    }

    static string Format(Phase p)
    {
        if (p.Ms.Count == 0) return $"{p.Name,-14} {0,5}   (no frames)";
        var a = p.Ms.ToArray();
        Array.Sort(a);
        return $"{p.Name,-14} {a.Length,5} {Pct(a, 50),6:0.00} {Pct(a, 90),6:0.00} {Pct(a, 99),7:0.00} {Pct(a, 99.9),7:0.00} {a[^1],8:0.00}   {p.OverBudget,10} {p.OverBudgetGc,12}";
    }

    static double Pct(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        if (sorted.Length == 1) return sorted[0];
        double rank = p / 100.0 * (sorted.Length - 1);
        int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
        return sorted[lo] * (1 - (rank - lo)) + sorted[hi] * (rank - lo);
    }

    // The most-scrollable viewport by VERTICAL CONTENT extent. Ranking by box area fails here: a virtualized list's scroll
    // node reports a laid-out box HEIGHT of 0 (its extent lives in ScrollState.ContentH, not the rect), so a tall-but-narrow
    // sidebar would win on area. ContentH picks the real content list and ignores horizontal-only scrollers.
    static NodeHandle FindLargestScrollViewport(FluentGpu.Scene.SceneStore scene)
    {
        var best = default(NodeHandle);
        float bestContent = 0f;
        var stack = new Stack<NodeHandle>();
        if (!scene.Root.IsNull) stack.Push(scene.Root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n.IsNull || !scene.IsLive(n)) continue;
            if (scene.HasScroll(n) && scene.TryGetScroll(n, out var sc))
            {
                var r = scene.AbsoluteRect(n);
                // Real, hit-testable CONTENT scroller: a non-zero viewport (a 0-height viewport stops hit-test descent),
                // genuinely scrollable, and past the sidebar (x>100) so we never pick the nav rail. Ranked by content extent.
                if (sc.ViewportH > 50f && sc.ContentH > sc.ViewportH + 1f && r.X > 100f && sc.ContentH > bestContent)
                { bestContent = sc.ContentH; best = n; }
            }
            for (var c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c)) stack.Push(c);
        }
        return best;
    }

    static NodeHandle FindScrollViewportByKey(FluentGpu.Scene.SceneStore scene, string key)
    {
        var stack = new Stack<NodeHandle>();
        if (!scene.Root.IsNull) stack.Push(scene.Root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n.IsNull || !scene.IsLive(n)) continue;
            if (scene.HasScroll(n) && scene.TryGetScroll(n, out var sc)
                && string.Equals(sc.ScrollKey, key, StringComparison.Ordinal))
                return n;
            for (var c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c)) stack.Push(c);
        }
        return NodeHandle.Null;
    }
}
