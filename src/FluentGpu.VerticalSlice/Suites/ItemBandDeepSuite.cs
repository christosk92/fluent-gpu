using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Render;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;

namespace FluentGpu.VerticalSlice.Suites;

/// <summary>
/// G of the 2026-09-25 Wavee RCA (runs with <c>--suite scroll</c>): the Q-top 1500 playlist in the vertical detail-row
/// layout went BLANK between the sticky chrome and the middle of the viewport after a fast thumb drag (any jump past
/// <see cref="Virtualizer.MaxLocalExtent"/>). The layout is a virtual list with a pinned persistent prefix (hero + chrome)
/// and the recyclable suffix clipped by ONE viewport-fixed item band (<c>ItemClipTopInset</c>). Evidence (verify bundle
/// 20260925-031425-G-deep-now): offset 52000, realized [1072,1092), arrange origin row 1082; the layout rects of rows
/// 1072–1081 are correct (keyed.tsv), yet the pixel queries find NO tile of the list's band slice above the origin row's
/// posed top, and the composite's coverage census is clean (coverageClamps=0, exposedMissing=0) — the rows were never
/// recorded. The band slice (<c>SliceRole.Band</c>, ParamsUp) carries its viewport-fixed clip on its MARKER, applied at
/// composite time at the live pose; the rows were still walked against that same clip expressed in the record frame,
/// so every row whose content-local top sat above the band line (the rows above the arrange origin, once the origin
/// re-centres) was culled out of the recording.
/// <para><c>gate.scroll.item-band-deep-rows</c>: after an immediate jump deep into a prefixed, item-band-clipped list,
/// the composited recyclable rows cover the whole band [viewport top + inset, viewport bottom) with no gap.</para>
/// </summary>
static class ItemBandDeepSuite
{
    public const int N = 1000;
    public const float Row = 40f, Inset = 80f, Fade = 22f, ViewW = 640f, ViewH = 480f;
    static ColorF RowFill => ColorF.FromRgba(38, 44, 52);

    public static void Run(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);
        DeepJumpChecks(strings, fonts);
    }

    /// <summary>The vertical-arm shape, reduced: items 0/1 are a sticky hero and chrome (the persistent prefix), the rest
    /// plain 40-DIP rows, the suffix under one item band at 80 DIP with a 22-DIP feather.</summary>
    sealed class PrefixedBandList : Component
    {
        public override Element Render()
            => new BoxEl
            {
                Width = ViewW, Height = ViewH,
                Children =
                [
                    ItemsView.CreateBound(N,
                        scope =>
                        {
                            int initial = scope.Index.Peek();
                            return new BoxEl
                            {
                                Height = Row,
                                Fill = initial == 0 ? ColorF.FromRgba(142, 48, 190) : initial == 1 ? ColorF.FromRgba(42, 156, 196) : RowFill,
                                ScrollEffects = initial == 0 ? [new(FluentGpu.Scroll.Effects.ScrollEffect.Sticky(0f))]
                                    : initial == 1 ? [new(FluentGpu.Scroll.Effects.ScrollEffect.Sticky(Row))] : [],
                            };
                        },
                        RepeatLayout.Stack(Row),
                        new ListOptions
                        {
                            PersistentPrefixCount = 2,
                            Grow = 1f,
                            Scroll = new ScrollOptions { ItemClipTopInset = Inset, ItemClipTopFadeBand = Fade },
                        }),
                ],
            };
    }

    static NodeHandle FindScroll(SceneStore s, NodeHandle n)
    {
        if (n.IsNull) return NodeHandle.Null;
        if (s.HasScroll(n) && s.TryGetScroll(n, out var sc) && sc.ItemCount > 0) return n;
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
        {
            var found = FindScroll(s, c);
            if (!found.IsNull) return found;
        }
        return NodeHandle.Null;
    }

    static bool IsRowFill(ColorF c)
        => MathF.Abs(c.R - RowFill.R) < 0.006f && MathF.Abs(c.G - RowFill.G) < 0.006f
        && MathF.Abs(c.B - RowFill.B) < 0.006f && MathF.Abs(c.A - RowFill.A) < 0.006f;

    /// <summary>The largest vertical span of [<paramref name="lo"/>, <paramref name="hi"/>) that no composited row rect
    /// covers, and where it starts.</summary>
    static float LargestGap(HeadlessGpuDevice dev, float lo, float hi, out float gapAt, out int rows)
    {
        var spans = new List<(float Y0, float Y1)>();
        for (int i = 0; i < dev.LastRects.Count; i++)
        {
            var r = dev.LastRects[i];
            if (!IsRowFill(r.Fill)) continue;
            RectF w = r.Transform.TransformBounds(r.Rect);
            float y0 = MathF.Max(lo, w.Y), y1 = MathF.Min(hi, w.Bottom);
            if (y1 > y0) spans.Add((y0, y1));
        }
        rows = spans.Count;
        spans.Sort((a, b) => a.Y0.CompareTo(b.Y0));
        float at = lo, worst = 0f;
        gapAt = float.NaN;
        foreach (var (y0, y1) in spans)
        {
            if (y0 - at > worst) { worst = y0 - at; gapAt = at; }
            if (y1 > at) at = y1;
        }
        if (hi - at > worst) { worst = hi - at; gapAt = at; }
        return worst;
    }

    static void DeepJumpChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("item-band-deep", new Size2(ViewW, ViewH), 1f));
        window.Show();
        var dev = new HeadlessGpuDevice();
        using var host = new AppHost(app, window, dev, fonts, strings, new PrefixedBandList());
        for (int i = 0; i < 10; i++) host.RunFrame();

        var vp = FindScroll(host.Scene, host.Scene.Root);
        var handle = vp.IsNull ? null : host.TryGetScrollHandle(vp);
        RectF vpRect = vp.IsNull ? default : host.Scene.AbsoluteRect(vp);
        float bandLo = vpRect.Y + Inset, bandHi = vpRect.Bottom;
        float restGap = LargestGap(dev, bandLo, bandHi, out float restAt, out int restRows);

        // One immediate jump far past MaxLocalExtent (the thumb drag's shape): the arrange origin re-centres on the window.
        const double Deep = 30000.0;
        handle?.ScrollTo(Deep, ScrollMove.Immediate);
        for (int i = 0; i < 30; i++) host.RunFrame();
        host.Scene.TryGetScroll(vp, out var sc);
        float deepGap = LargestGap(dev, bandLo, bandHi, out float deepAt, out int deepRows);
        bool recentred = sc.WindowOriginIndex > sc.FirstRealized;

        Check("gate.scroll.item-band-deep-rows after an immediate jump deep into a prefixed virtual list clipped by one viewport-fixed item band (the arrange origin re-centred, rows above it at negative local offsets), the composited rows cover the whole band below the inset — no row is culled at record time by the band clip the composite applies at the live pose",
            !vp.IsNull && handle is not null && restGap <= 1f && recentred && Math.Abs(sc.Offset - Deep) < 1.0 && deepGap <= 1f,
            $"vp={(vp.IsNull ? "none" : "ok")} band=[{bandLo},{bandHi}) rest(gap={restGap:0.#} at={restAt:0.#} rows={restRows}) " +
            $"deep(offset={sc.Offset:0.#} realized=[{sc.FirstRealized},{sc.LastRealized}) originIndex={sc.WindowOriginIndex} origin={sc.WindowOrigin:0.#} " +
            $"gap={deepGap:0.#} at={deepAt:0.#} rows={deepRows})");
    }
}
