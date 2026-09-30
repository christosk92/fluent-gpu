using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Render;
using FluentGpu.Render.Tiles;
using FluentGpu.Rhi;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;

namespace FluentGpu.VerticalSlice.Harness;

/// <summary>
/// Structural invariants of the retained composite that hold for EVERY scene (gpu-renderer.md §13.1e), checked over the
/// headless model's last composite. They exist because two defect classes slipped past gates that looked at the modelled
/// STREAM (which nests a child slice inside whatever its parent's stream opened) instead of at what the tile rasterizer
/// and the composite actually do with it:
/// <list type="bullet">
/// <item><b>Acrylic ⇒ Backdrop.</b> A frost exists only as a composite <see cref="CompositeKind.Backdrop"/> item placed
/// before its slice's item. An Acrylic PushLayer replayed inside a tile ERASES its plate rect expecting that backdrop
/// beneath — without it the plate is a transparent hole. So every Acrylic PushLayer in an item's segment has a Backdrop
/// item earlier in the frame covering its rect; and every visible acrylic surface either has that Backdrop or paints its
/// opaque FallbackColor plate (WinUI's no-backdrop answer).</item>
/// <item><b>No slice marker inside an inline group layer.</b> A tile replay SKIPS a child slice's marker (the child
/// composites as its own item), so an inline (folded) opacity / blur / edge-fade PushLayer wrapped around a marker wraps
/// nothing — the child composites unfaded / unfeathered / unblurred.</item>
/// </list>
/// Pure reads of recorded bytes and composite items — never production source text.
/// </summary>
static class CompositeInvariants
{
    /// <summary>Every Acrylic PushLayer decoded out of the last composite's items has a Backdrop item BEFORE its item
    /// whose rounded rect covers the layer's frosted rect (±1 device px).</summary>
    public static bool AcrylicLayersHaveBackdrops(HeadlessGpuDevice dev, out string detail)
    {
        float scale = dev.LastCompositeInfo.Scale > 0f ? dev.LastCompositeInfo.Scale : 1f;
        var items = new List<(int Index, CompositeItem Item)>();
        foreach (var r in dev.LastCompositeRecords)
            if (r.Kind == CompositeRecordKind.DrawItem) items.Add((r.ItemIndex, r.Item));
        int layers = 0, orphans = 0;
        foreach (var (itemIndex, rectDip) in dev.LastAcrylicLayerItems)
        {
            layers++;
            var px = new RectF(rectDip.X * scale, rectDip.Y * scale, rectDip.W * scale, rectDip.H * scale);
            bool backed = false;
            foreach (var (k, it) in items)
                if (k < itemIndex && it.Kind == CompositeKind.Backdrop && Covers(it.RoundClip, px, 1.01f)) { backed = true; break; }
            if (!backed) orphans++;
        }
        detail = $"acrylicLayers={layers} withoutBackdrop={orphans}";
        return orphans == 0;
    }

    /// <summary>Every VISIBLE acrylic surface of the scene (an <c>AcrylicSpec</c> whose authored fill is its FallbackColor
    /// — the FlyoutSurface / plate shape) is either frosted by a Backdrop item covering it or paints its opaque fallback
    /// plate. Never neither (a hole).</summary>
    public static bool AcrylicSurfacesFrostedOrSolid(AppHost host, HeadlessGpuDevice dev, out string detail)
    {
        float scale = dev.LastCompositeInfo.Scale > 0f ? dev.LastCompositeInfo.Scale : 1f;
        var scene = host.Scene;
        var win = new RectF(0f, 0f, dev.LastCompositeInfo.SizePx.Width / scale, dev.LastCompositeInfo.SizePx.Height / scale);
        int surfaces = 0, frosted = 0, solid = 0, holes = 0;
        if (!Materials.AcrylicEnabled) { detail = "acrylic policy off"; return true; }
        for (int i = 0; i < scene.Capacity; i++)
        {
            var h = scene.HandleAt(i);
            if (h.IsNull || !scene.IsLive(h) || !scene.TryGetAcrylic(h, out var ac)) continue;
            if (!VisibleChain(scene, h)) continue;
            ColorF fill = scene.Paint(h).Fill;
            if (!Same(fill, ac.Fallback) || ac.FeatherTop > 0f) continue;   // only the plate shape (fill == its own fallback)
            RectF r = scene.AbsoluteRect(h);
            if (r.W < 2f || r.H < 2f || !r.Overlaps(win)) continue;
            surfaces++;
            var px = new RectF(r.X * scale, r.Y * scale, r.W * scale, r.H * scale);
            bool backed = false;
            foreach (var rec in dev.LastCompositeRecords)
                if (rec.Kind == CompositeRecordKind.DrawItem && rec.Item.Kind == CompositeKind.Backdrop && Covers(rec.Item.RoundClip, px, 1.01f))
                { backed = true; break; }
            if (backed) { frosted++; continue; }
            bool plate = false;
            foreach (var fr in dev.LastRects)
                if (Same(fr.Fill, ac.Fallback) && MathF.Abs(fr.Rect.W - r.W) < 1.5f && MathF.Abs(fr.Rect.H - r.H) < 1.5f) { plate = true; break; }
            if (plate) solid++; else holes++;
        }
        detail = $"acrylicSurfaces={surfaces} frosted={frosted} solidFallback={solid} holes={holes}";
        return holes == 0;
    }

    /// <summary>No child slice marker (<c>DrawOp.CompositeSlice</c>) lies inside an open inline opacity / blur / edge-fade
    /// PushLayer in any live slice arena (an acrylic layer is not a group: a marker inside it is fine).</summary>
    public static bool NoMarkerInsideInlineLayer(AppHost host, out string detail)
    {
        var sl = host.UiSlices;
        Span<(int NodeIndex, uint Gen, SliceRole Role, SliceKind Kind)> order = stackalloc (int, uint, SliceRole, SliceKind)[256];
        int n = Math.Min(sl.CopySliceOrder(order), order.Length);
        int violations = 0, slices = 0;
        var stack = new Stack<int>();
        for (int s = 0; s < n; s++)
        {
            ReadOnlySpan<byte> bytes = sl.SliceBytes(order[s].NodeIndex, order[s].Gen, order[s].Role);
            if (bytes.IsEmpty) continue;
            slices++;
            stack.Clear();
            int groups = 0, pos = 0;
            while (pos + sizeof(int) <= bytes.Length)
            {
                var op = (DrawOp)MemoryMarshal.Read<int>(bytes[pos..]);
                pos += sizeof(int);
                if (!RepaintStreamSafety.TryBodySize(op, out int body) || pos + body > bytes.Length) break;
                switch (op)
                {
                    case DrawOp.PushLayer:
                    {
                        int kind = MemoryMarshal.Read<PushLayerCmd>(bytes[pos..]).Kind;
                        stack.Push(kind);
                        if (kind != (int)LayerKind.Acrylic) groups++;
                        break;
                    }
                    case DrawOp.PopLayer:
                        if (stack.Count > 0 && stack.Pop() != (int)LayerKind.Acrylic) groups--;
                        break;
                    case DrawOp.CompositeSlice:
                        if (groups > 0) violations++;
                        break;
                }
                pos += body;
            }
        }
        detail = $"slices={slices} markersInsideInlineGroups={violations}";
        return violations == 0;
    }

    /// <summary>The last composite holds a <see cref="CompositeKind.Backdrop"/> item whose rounded rect covers
    /// <paramref name="rectDip"/> (window DIP) — the surface is FROSTED (±1 device px).</summary>
    public static bool HasBackdropCovering(HeadlessGpuDevice dev, RectF rectDip)
    {
        float scale = dev.LastCompositeInfo.Scale > 0f ? dev.LastCompositeInfo.Scale : 1f;
        var px = new RectF(rectDip.X * scale, rectDip.Y * scale, rectDip.W * scale, rectDip.H * scale);
        foreach (var rec in dev.LastCompositeRecords)
            if (rec.Kind == CompositeRecordKind.DrawItem && rec.Item.Kind == CompositeKind.Backdrop && Covers(rec.Item.RoundClip, px, 1.01f))
                return true;
        return false;
    }

    static bool VisibleChain(SceneStore s, NodeHandle h)
    {
        for (var n = h; !n.IsNull; n = s.Parent(n))
            if ((s.Flags(n) & NodeFlags.Visible) == 0) return false;
        return true;
    }

    static bool Same(ColorF a, ColorF b)
        => MathF.Abs(a.R - b.R) < 0.004f && MathF.Abs(a.G - b.G) < 0.004f && MathF.Abs(a.B - b.B) < 0.004f && MathF.Abs(a.A - b.A) < 0.004f;

    static bool Covers(RectF outer, RectF inner, float tol)
        => outer.W > 0f && outer.H > 0f
           && outer.X <= inner.X + tol && outer.Y <= inner.Y + tol
           && outer.Right >= inner.Right - tol && outer.Bottom >= inner.Bottom - tol;
}
