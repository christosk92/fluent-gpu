using System;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Reconciler;
using FluentGpu.Render;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using static FluentGpu.Dsl.Ui;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;

// The 2026-09 visual-continuity audit (frame-by-frame review of a screen recording): every gate here pins one "the
// picture jumped" finding at its engine mechanism.
//   • continuity.brush-fade-out   — a BrushTransition TO transparent is drawn until it lands (was a one-frame snap), and
//                                   a same-colour hop never blinks (the next fade starts from what was on screen).
//   • continuity.image-*          — a displayed picture is never replaced by a placeholder: a DIFFERENT picture dissolves
//                                   from the held texture; a remounted cover shows a resident rendition of its source.
//   • continuity.anchor-*         — a list resting at offset 0 does not anchor (CSS overflow-anchor / Gecko).
//   • continuity.keepalive-first  — a KeepAlive boundary's first activation into a live window gets its entrance.
//   • continuity.pending-start    — an entrance seeded in a long commit frame starts at t≈0 on its first presented frame.
static class VisualContinuityChecks
{
    public static void Run(StringTable strings)
    {
        BrushFadeToTransparentChecks(strings);
        ImageSwapChecks(strings);
        ImageStandInChecks(strings);
        ScrollAnchorChecks(strings);
        KeepAliveFirstActivationChecks(strings);
        PendingStartChecks();
    }

    static readonly ColorF Tint = ColorF.FromRgba(0x20, 0x60, 0xC0, 0x80);

    static FillRoundRectCmd? TintRect(HeadlessGpuDevice dev)
    {
        foreach (var r in dev.LastRects) if (Near(r.Rect.W, 77f) && Near(r.Rect.H, 33f)) return r;
        return null;
    }

    static void BrushFadeToTransparentChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("continuity-brush", new Size2(300, 200), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var tinted = new Signal<bool>(true);
        using var host = new AppHost(app, window, device, new HeadlessFontSystem(strings), strings, new W0fStaticProbe
        {
            // The shell-tint shape: one flat layer, always mounted, Fill = tint ?? Transparent, BrushTransitionMs set.
            Build = () => new BoxEl
            {
                Width = 300, Height = 200,
                Children = [new BoxEl { Width = 77, Height = 33, Fill = tinted.Value ? Tint : ColorF.Transparent, BrushTransitionMs = 83f }],
            },
        });
        host.RunFrame();
        bool resting = TintRect(device) is { } r0 && ColorClose(r0.Fill, Tint, 0.004f);

        tinted.Value = false;   // → "no tint"
        host.RunFrame();        // one fixed dt into an 83 ms fade
        var mid = TintRect(device);
        bool fadingOut = mid is { } m && m.Fill.A > 0.05f && m.Fill.A < Tint.A - 0.02f && m.Fill.B > m.Fill.R;   // the tint's hue, partly gone
        for (int i = 0; i < 10; i++) host.RunFrame();
        bool landed = TintRect(device) is null && !host.HasActiveWork;   // settled transparent: nothing drawn, loop idle

        Check("continuity.brush-fade-out a BrushTransition TO transparent keeps drawing the interpolated colour until it lands, then stops drawing",
            resting && fadingOut && landed,
            $"resting={resting} mid={(mid is { } mm ? $"a={mm.Fill.A:0.00}" : "absent")} landed={landed}");

        // Same-colour hop (a page swap clearing then re-publishing the same tint): tint → none → tint on consecutive
        // frames must never lose the rect, and the return must start from the colour actually on screen.
        tinted.Value = true;
        for (int i = 0; i < 12; i++) host.RunFrame();
        tinted.Value = false;
        host.RunFrame();
        float a1 = TintRect(device)?.Fill.A ?? 0f;
        tinted.Value = true;
        host.RunFrame();
        float a2 = TintRect(device)?.Fill.A ?? 0f;
        Check("continuity.brush-fade-hop a tint → none → tint hop never blinks: every frame draws, and the return eases from the displayed colour",
            a1 > 0.05f && a2 >= a1 - 0.001f && a2 <= Tint.A + 0.001f, $"a1={a1:0.000} a2={a2:0.000}");
    }

    static void ImageSwapChecks(StringTable strings)
    {
        // A DIFFERENT picture replacing a drawn one (the player bar's next-track cover, a detail page whose loaded model
        // names other art): hold-last-good keeps the old texture on screen while the new one decodes, then the settle
        // dissolves FROM it — two draws for the window, never a placeholder under the new picture — and releases the
        // outgoing pin. (A synchronous cache hit still cuts: that is the path recycled virtual rows rebind through.)
        var dec = new SelectiveIdDecoder();
        var cache = new ImageCache(dec);
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("continuity-swap", new Size2(200, 200), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var src = new Signal<string>("continuity/a");
        using var host = new AppHost(app, window, device, new HeadlessFontSystem(strings), strings, new W0fStaticProbe
        {
            Build = () => new BoxEl { Width = 120, Height = 120, Children = [new ImageEl { Source = src.Value, Width = 64, Height = 64 }] },
        }, cache);

        host.RunFrame();
        int idA = dec.LastBeginId;
        dec.Release(idA);
        for (int i = 0; i < 20; i++) host.RunFrame();   // A lands and its reveal settles

        src.Value = "continuity/b";
        host.RunFrame();
        int idB = dec.LastBeginId;
        bool held = idB != idA && device.LastImages.Count == 1 && device.LastImages[0].ImageId == idA
            && device.LastImages[0].Ready == 1;

        dec.Release(idB);
        host.RunFrame();
        var imgs = device.LastImages;
        bool dissolving = imgs.Count == 2
            && imgs[0].ImageId == idA && imgs[0].Ready == 1 && imgs[0].FadeEasing == ImageCache.SwapOutgoingEasing
            && imgs[1].ImageId == idB && imgs[1].Ready == 1 && imgs[1].Placeholder.A == 0f
            && Near(imgs[1].FadeDurationMs, ImageCache.SwapCrossfadeMs, 0.01f);
        float outgoingMid = ImageCache.ResolveFade(cache.ClockMs, imgs.Count == 2 ? imgs[0].FadeStartMs : float.NaN,
            ImageCache.SwapCrossfadeMs, ImageCache.SwapOutgoingEasing);
        for (int i = 0; i < 24; i++) host.RunFrame();
        bool released = device.LastImages.Count == 1 && device.LastImages[0].ImageId == idB
            && cache.RefsOf(new ImageHandle(idA)) == 0 && cache.RefsOf(new ImageHandle(idB)) == 1;
        Check("continuity.image-dissolve a different picture settling after hold-last-good dissolves from the held texture (opaque for the window, the new one over it, no placeholder), then the outgoing pin is released",
            held && dissolving && outgoingMid >= 1f && released,
            $"held={held} dissolving={dissolving} outgoingMid={outgoingMid:0.00} released={released} draws={device.LastImages.Count} idA={idA} idB={idB}");
    }

    static void ImageStandInChecks(StringTable strings)
    {
        // A cover REMOUNTED by a structural change (a new keyed wrapper) asking for a new decode bucket: hold-last-good
        // has no old node to hold, so the resident rendition of the same source stands in — the picture, never a grey
        // tile — and the exact decode hard-cuts in (same picture, sharper), the stand-in backing it for the swap window.
        var dec = new SelectiveIdDecoder();
        var cache = new ImageCache(dec);
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("continuity-standin", new Size2(300, 300), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var gen = new Signal<int>(0);
        using var host = new AppHost(app, window, device, new HeadlessFontSystem(strings), strings, new W0fStaticProbe
        {
            Build = () => new BoxEl
            {
                Width = 300, Height = 300,
                Children =
                [
                    new BoxEl
                    {
                        Key = "cover-slot-" + gen.Value,
                        Children = [new ImageEl { Source = "continuity/art", Width = gen.Value == 0 ? 64 : 128, Height = gen.Value == 0 ? 64 : 128 }],
                    },
                ],
            },
        }, cache);

        host.RunFrame();
        int id64 = dec.LastBeginId;
        dec.Release(id64);
        for (int i = 0; i < 20; i++) host.RunFrame();

        gen.Value = 1;      // remount at a new decode size
        host.RunFrame();
        int id128 = dec.LastBeginId;
        var imgs = device.LastImages;
        bool standIn = id128 != id64 && cache.StateOf(new ImageHandle(id128)) == ImageState.Pending
            && imgs.Count == 1 && imgs[0].ImageId == id64 && imgs[0].Ready == 1;

        dec.Release(id128);
        host.RunFrame();
        var cut = device.LastImages;
        bool hardCut = cut.Count == 2
            && cut[0].ImageId == id64 && cut[0].Ready == 1 && cut[0].FadeEasing == ImageCache.SwapOutgoingEasing
            && cut[1].ImageId == id128 && cut[1].Ready == 1 && cut[1].Placeholder.A == 0f
            && (float.IsNaN(cut[1].FadeStartMs) || cut[1].FadeDurationMs <= 0f)
            && cache.CrossFadeOf(new ImageHandle(id128)) >= 0.999f;
        int cutDraws = cut.Count;
        for (int i = 0; i < 24; i++) host.RunFrame();
        bool released = device.LastImages.Count == 1 && device.LastImages[0].ImageId == id128
            && cache.RefsOf(new ImageHandle(id64)) == 0;
        Check("continuity.image-standin a remounted cover shows the resident rendition of its source while the new decode size lands, then hard-cuts to it with the stand-in under it for the swap window",
            standIn && hardCut && released,
            $"standIn={standIn} hardCut={hardCut} released={released} id64={id64} id128={id128} draws={imgs.Count}/{cutDraws}");
    }

    static void ScrollAnchorChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("continuity-anchor", new Size2(320, 240), 1f));
        window.Show();
        var ctl = new ItemsViewController();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new W0fStaticProbe
        {
            Build = () => ItemsView.Create(100,
                i => new BoxEl { Height = 40f, Children = [new TextEl($"row {i}") { Size = 13f }] },
                RepeatLayout.Stack(40f),
                new ListOptions { Controller = ctl, Selector = SelectorVisual.None }),
        });
        host.RunFrame();
        host.RunFrame();

        // At the top (the initial load): three rows inserted above the first visible one must NOT scroll the list.
        bool suppressed = !ctl.PreserveAnchor(3 * 40f);
        host.RunFrame();
        host.RunFrame();
        float atTop = ctl.ScrollOffset;

        ctl.ScrollBy(200f);
        host.RunFrame();
        host.RunFrame();
        float scrolled = ctl.ScrollOffset;
        bool applied = ctl.PreserveAnchor(3 * 40f);
        host.RunFrame();
        host.RunFrame();
        float anchored = ctl.ScrollOffset;

        Check("continuity.anchor-start-edge an anchor correction is suppressed while the list rests at offset 0, and applied once it is scrolled",
            suppressed && Near(atTop, 0f, 0.01f) && Near(scrolled, 200f, 0.5f) && applied && Near(anchored, 320f, 0.5f),
            $"suppressed={suppressed} atTop={atTop:0.#} scrolled={scrolled:0.#} applied={applied} anchored={anchored:0.#}");
    }

    static void KeepAliveFirstActivationChecks(StringTable strings)
    {
        static Element Page(string k) => new BoxEl { Width = 240f, Height = 120f, Children = [Text("first-" + k)] };

        // A boundary mounted into a LIVE window (frames already presented — FrameEpoch > 1): TransitionFor owns the
        // first activation too, with the FirstActivation sentinel as the old token, and its Enter is seeded.
        {
            var scene = new SceneStore();
            var anim = new AnimEngine(scene);
            var recon = new TreeReconciler(scene, strings) { Anim = anim, FrameEpoch = 5 };
            object? seenOld = null;
            recon.ReconcileRoot(Flow.KeepAlive(() => "artist", k => k, Page,
                new KeepAliveOptions(TransitionFor: (old, _) => { seenOld = old; return MotionRecipes.PageSlideForward with { Exit = default }; })),
                null);
            anim.Tick(0f);
            var root = scene.FirstChild(scene.Root);
            bool entrance = anim.TryGetTrackValue(root, AnimChannel.Opacity, out float op) && op < 0.2f;
            bool sentinel = ReferenceEquals(seenOld, KeepAliveOptions.FirstActivation);
            Check("continuity.keepalive-first a KeepAlive boundary's first activation into a live window asks TransitionFor (old = FirstActivation) and seeds its entrance",
                entrance && sentinel, $"entrance={entrance} opacity={op:0.00} sentinel={sentinel}");
        }

        // The launch page (the very first paint — FrameEpoch ≤ 1): nothing was on screen, so no entrance and no ask.
        {
            var scene = new SceneStore();
            var anim = new AnimEngine(scene);
            var recon = new TreeReconciler(scene, strings) { Anim = anim };
            bool asked = false;
            recon.ReconcileRoot(Flow.KeepAlive(() => "home", k => k, Page,
                new KeepAliveOptions(TransitionFor: (_, _) => { asked = true; return MotionRecipes.PageSlideForward; })),
                null);
            anim.Tick(0f);
            var root = scene.FirstChild(scene.Root);
            Check("continuity.keepalive-launch the launch page mounts without an entrance (TransitionFor is not asked before the first frame)",
                !asked && !anim.HasTracks(root), $"asked={asked} tracks={anim.HasTracks(root)}");
        }
    }

    static void PendingStartChecks()
    {
        // An entrance seeded in a commit frame, then a LONG frame (the page mount): the first presented advance is one
        // steady frame (≈16.7 ms of a 200 ms linear fade), not the long frame's 60 ms. A plain Animate keeps raw-dt
        // semantics (injected-dt replays), and a fixed-dt sequence is unchanged.
        var spec = new LayoutTransition(TransitionChannels.Opacity, TransitionDynamics.Tween(200f, Easing.Linear),
            Enter: new EnterExit(Opacity: 0f, Active: true));

        float EnterAfter(float holdMs, float stepMs, bool structural)
        {
            var scene = new SceneStore();
            var node = scene.CreateNode(1);
            scene.Root = node;
            var anim = new AnimEngine(scene);
            if (structural) anim.SeedEnter(node, spec.Enter, spec);
            else anim.Animate(node, AnimChannel.Opacity, 0f, 1f, 200f, Easing.Linear);
            anim.Tick(holdMs);   // the commit frame: held at t=0 (presented)
            anim.Tick(stepMs);   // the next frame
            return scene.Paint(node).Opacity;
        }

        float longFrame = EnterAfter(16.67f, 60f, structural: true);
        float steady = EnterAfter(16.67f, 16.67f, structural: true);
        float plain = EnterAfter(16.67f, 60f, structural: false);
        Check("continuity.pending-start an entrance whose seed frame ran long starts at its beginning on the first presented frame (one steady step, not the long frame)",
            Near(longFrame, 16.67f / 200f, 0.01f) && Near(steady, 16.67f / 200f, 0.01f) && Near(plain, 60f / 200f, 0.01f),
            $"longFrame={longFrame:0.000} steady={steady:0.000} plainAnimate={plain:0.000}");
    }
}
