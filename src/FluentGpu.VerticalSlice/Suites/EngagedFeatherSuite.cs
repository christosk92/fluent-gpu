using System;
using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Render.Tiles;
using FluentGpu.Rhi;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Scroll.Effects;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;

namespace FluentGpu.VerticalSlice.Suites;

/// <summary>
/// F(ii) of the 2026-09-25 artist-page RCA (runs with <c>--suite scroll</c>): the magazine under the artist page's compact
/// band is cut at the band line by a composite-time <c>.StickyClip(56)</c> — posed on the render thread on the tick the
/// scroll crosses the line — while the 24-DIP feather on that cut is an <c>EdgeFade</c> switched by a RE-RENDER off the
/// sentinel's <c>engaged:</c> signal (Wavee <c>Artist.Page.cs</c>, the <c>_compact</c> edge). The signal flips on the UI
/// frame whose pose crossed; the re-render lands in the NEXT publication. So the first composited frame with the clip
/// engaged has a hard cut and no feather, and a UI hitch between the two frames holds that hard cut on screen.
/// <para><c>gate.scroll.engaged-feather-composite</c>: in the FIRST composited frame whose magazine items are cut at the
/// sticky line, the cut already carries its top feather at that line — and at rest nothing is feathered. The fix is
/// <c>EdgeFadeSpec.WhileStuck</c>: the fade is always in the element tree and the composite applies it exactly while
/// the clip is engaged, on the pose that engages it.</para>
/// </summary>
static class EngagedFeatherSuite
{
    public const float HeroH = 400f, Inset = 56f, Band = 24f, ViewW = 1000f, ViewH = 800f;

    public static void Run(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);
        EngagedFeatherChecks(strings, fonts);
    }

    /// <summary>The artist page, reduced: a hero, a 0-height <c>.Sticky(56, engaged:)</c> sentinel (kept: its engaged edge
    /// is the band's input switch in the app), and the magazine column — <c>.StickyClip(56)</c> with an
    /// <c>EdgeFade(Top, 24) { WhileStuck = true }</c>: the feather is present in the element tree ALWAYS and the composite
    /// applies it exactly while the clip is engaged (the fix; before it the app switched the fade by a re-render off the
    /// sentinel's engaged signal, Artist.Page.cs 502-533, which lands at least one publication late).</summary>
    sealed class RerenderFeatherProbe : Component
    {
        public readonly Signal<bool> Engaged = new(false) { DebugName = "probe.compact" };

        public override Element Render()
        {
            var rows = new Element[30];
            for (int i = 0; i < rows.Length; i++)
                rows[i] = new BoxEl { Height = 80f, Fill = ColorF.FromRgba(30, (byte)(40 + i * 3), (byte)(60 + i * 4)) };
            Element sentinel = new BoxEl { Height = 0f, HitTestVisible = false }.Sticky(Inset, engaged: Engaged);
            Element magazine = new BoxEl
            {
                Direction = 1,
                EdgeFade = new EdgeFadeSpec(EdgeMask.Top, Band) { WhileStuck = true },
                Children = rows,
            }.StickyClip(Inset);
            return new ScrollEl
            {
                Width = ViewW, Height = ViewH,
                Content = new BoxEl
                {
                    Direction = 1, MinWidth = 0f,
                    Children = [new BoxEl { Height = HeroH, Fill = ColorF.FromRgba(80, 40, 40) }, sentinel, magazine],
                },
            };
        }
    }

    static (HeadlessPlatformApp App, HeadlessGpuDevice Device, AppHost Host) Host(StringTable strings, HeadlessFontSystem fonts,
        Component root)
    {
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("engaged-feather", new Size2(ViewW, ViewH), 1f));
        window.Show();
        var dev = new HeadlessGpuDevice();
        return (app, dev, new AppHost(app, window, dev, fonts, strings, root));
    }

    static NodeHandle PageScroller(SceneStore s, NodeHandle n)
    {
        if (n.IsNull) return NodeHandle.Null;
        if (s.HasScroll(n)) return n;
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
        {
            var found = PageScroller(s, c);
            if (!found.IsNull) return found;
        }
        return NodeHandle.Null;
    }

    /// <summary>Is this composite item cut at the sticky line (its clip's top within a device px of it)?</summary>
    static bool CutAtLine(in CompositeItem it, float linePx)
        => it.Clip.W > 0f && it.Clip.H > 0f && MathF.Abs(it.Clip.Y - linePx) <= 1f;

    /// <summary>Does this item carry a TOP feather (either feather slot)? <paramref name="rectY"/> = its rect's top (device
    /// px) for the detail line — where the feather starts (at the sticky line, or at the column's own top).</summary>
    static bool TopFeather(in CompositeItem it, out float rectY)
    {
        rectY = float.NaN;
        if (!it.Feather.IsNone && it.Feather.BandTop > 0f) { rectY = it.Feather.Rect.Y; return true; }
        if (!it.Feather2.IsNone && it.Feather2.BandTop > 0f) { rectY = it.Feather2.Rect.Y; return true; }
        return false;
    }

    static void EngagedFeatherChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        var probe = new RerenderFeatherProbe();
        var (app, dev, host) = Host(strings, fonts, probe);
        using var _a = app; using var _h = host;
        for (int i = 0; i < 20; i++) host.RunFrame();

        NodeHandle page = PageScroller(host.Scene, host.Scene.Root);
        var handle = page.IsNull ? null : host.TryGetScrollHandle(page);
        float linePx = Inset;   // the scroller sits at the window origin, scale 1

        // Rest: nothing engaged, nothing cut — and nothing softened (the magazine's own top, at the hero's bottom, carries no
        // feather while its clip is released).
        bool restCut = false, restFeathered = false;
        foreach (var op in dev.LastCompositeRecords)
        {
            if (op.Kind != CompositeRecordKind.DrawItem) continue;
            if (CutAtLine(op.Item, linePx)) restCut = true;
            if (TopFeather(op.Item, out float ry) && MathF.Abs(ry - HeroH) <= 1f) restFeathered = true;
        }

        // One immediate move across the engage offset (sentinel at HeroH: engaged once offset + 56 > 400).
        handle?.ScrollTo(HeroH - Inset + 16.0, ScrollMove.Immediate);
        int firstCutFrame = -1, cutItems = 0, featheredCutItems = 0, frameOrdinal = 0, laterFeatherFrame = -1;
        float featherRectY = float.NaN, otherFeatherY = float.NaN;
        for (int f = 0; f < 6; f++)
        {
            int seen = dev.CompositeFrameCount;
            host.RunFrame();
            if (dev.CompositeFrameCount == seen) continue;
            frameOrdinal++;
            int cut = 0, feathered = 0;
            foreach (var op in dev.LastCompositeRecords)
            {
                if (op.Kind != CompositeRecordKind.DrawItem || !CutAtLine(op.Item, linePx)) continue;
                cut++;
                // The cut's feather is the one whose rect starts AT the sticky line (a feather elsewhere — the column's
                // own top, a viewport edge — is not this cut's).
                if (TopFeather(op.Item, out float fy) && MathF.Abs(fy - linePx) <= 1f) { feathered++; if (float.IsNaN(featherRectY)) featherRectY = fy; }
                else if (TopFeather(op.Item, out float other) && float.IsNaN(otherFeatherY)) otherFeatherY = other;
            }
            if (cut == 0) continue;
            if (firstCutFrame < 0) { firstCutFrame = frameOrdinal; cutItems = cut; featheredCutItems = feathered; }
            else if (laterFeatherFrame < 0 && feathered > 0) laterFeatherFrame = frameOrdinal;
        }

        Check("gate.scroll.engaged-feather-composite a WhileStuck edge fade feathers nothing at rest and, in the FIRST composited frame whose magazine is cut at the sticky band line, already feathers that cut (a top feather at the line on every cut item) — the feather engages on the same pose as the clip, never a UI re-render later",
            !page.IsNull && !restCut && !restFeathered && firstCutFrame > 0 && cutItems > 0 && featheredCutItems == cutItems,
            $"page={(page.IsNull ? "none" : "ok")} restCut={restCut} restFeathered={restFeathered} firstCutFrame={firstCutFrame} cutItems={cutItems} featheredAtFirst={featheredCutItems} featherFirstSeenAtFrame={(featheredCutItems > 0 ? firstCutFrame : laterFeatherFrame)} featherRectY={featherRectY} otherFeatherY={otherFeatherY} linePx={linePx} engagedSignal={probe.Engaged.Peek()}");
    }
}
