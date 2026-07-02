using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FluentGpu.Foundation;

namespace FluentGpu.Render;

/// <summary>Position-INDEPENDENT content key for the cross-frame self-blur pin cache (backdrop-effects-animation.md
/// §FA-2a). Folds σ + INTEGER device size + the subtree's painted bytes with every op's device position REBASED to the
/// layer origin and ROUNDED to an integer grid — so a pure translation (scroll) yields a byte-identical key (a HIT),
/// while any change to content, size (scale/DPI/relayout/wrap), σ, or a ≥1-unit change in an op's position RELATIVE to
/// the layer is a MISS. Rebasing is exact-under-translation because op <c>Rect</c>/<c>Bounds</c> are node-local
/// (origin 0,0 — SceneRecorder) and ALL absolute position lives in <c>Transform.Dx/Dy</c> (or a <c>ClipCmd</c> rect
/// origin); rounding to the same integer grid the composite snaps to collapses the sub-unit float wobble a raw
/// <c>fl(P+X)-X</c> rebase would otherwise leak. Scale/rotation (<c>M11..M22</c>) and every content field fold verbatim
/// — they are translation-invariant, and scale/size are legitimate CONTENT (size) misses, not a cacheable dimension.
///
/// The rebase runs in the recorder's LOGICAL (DIP) space (DeviceRect/Transform are DIP; the compositor multiplies by the
/// frame DPI scale only at composite time), so rounding here gates hit/miss + settle-remint granularity at ~1 DIP — it
/// never places a stale composite: on a HIT the compositor always positions the pin from the CURRENT layer's RegionBox,
/// so placement follows the true sub-pixel position while the KEY tolerates a sub-DIP wobble (invisible under blur).
///
/// Zero heap allocation (only stackalloc), so it is safe on the phase 6–13 record hot path. A NESTED PushLayer or any
/// UNKNOWN op returns false ⇒ "not cacheable" ⇒ render normally (never a stale pin) — the same safety contract the old
/// raw-byte walk had, so an added opcode inside a blur subtree can never desync the walk.</summary>
public static class BlurPinKey
{
    private const ulong Prime = 1099511628211UL;              // FNV-1a 64-bit prime
    private const ulong Basis = 14695981039346656037UL;       // FNV-1a 64-bit offset basis

    /// <summary>Compute the position-independent pin key for the self-blur subtree starting at <paramref name="start"/>
    /// (the first op AFTER the <see cref="PushLayerCmd"/>) up to its matching <see cref="DrawOp.PopLayer"/>. Returns
    /// false (uncacheable, render normally) on a nested <see cref="DrawOp.PushLayer"/> or any unrecognized op.
    /// <paramref name="afterPop"/> = the byte offset just past the matching PopLayer (where a HIT resumes). On success
    /// <paramref name="hash"/> is non-zero (0 is the compositor's "not cacheable" sentinel).</summary>
    public static bool TryCompute(ReadOnlySpan<byte> cmds, int start, in PushLayerCmd L, out ulong hash, out int afterPop)
    {
        hash = 0; afterPop = start;
        ulong h = Basis;
        FoldSeed(ref h, in L);                     // σ + round(W) + round(H)  (NOT the absolute X/Y)
        float ox = L.DeviceRect.X, oy = L.DeviceRect.Y;
        int pos = start;
        while (pos + sizeof(int) <= cmds.Length)
        {
            DrawOp op = (DrawOp)MemoryMarshal.Read<int>(cmds.Slice(pos));
            int bodyOff = pos + sizeof(int);
            if (op == DrawOp.PopLayer)
            {
                afterPop = bodyOff + Unsafe.SizeOf<PopLayerCmd>();   // PopLayerCmd carries the absolute layer rect — NOT folded (position)
                hash = h == 0 ? 1UL : h;
                return true;
            }
            FoldOp(ref h, (int)op);                 // fold the op code (translation-invariant) so an op-swap can't collide
            switch (op)
            {
                case DrawOp.FillRoundRect:       { var c = MemoryMarshal.Read<FillRoundRectCmd>(cmds.Slice(bodyOff));       FoldStruct(ref h, c with { Transform = Reb(c.Transform, ox, oy) }); pos = bodyOff + Unsafe.SizeOf<FillRoundRectCmd>(); break; }
                case DrawOp.DrawGlyphRun:        { var c = MemoryMarshal.Read<DrawGlyphRunCmd>(cmds.Slice(bodyOff));        FoldStruct(ref h, c with { Transform = Reb(c.Transform, ox, oy) }); pos = bodyOff + Unsafe.SizeOf<DrawGlyphRunCmd>(); break; }
                case DrawOp.DrawGlyphRunGradient:{ var c = MemoryMarshal.Read<DrawGlyphRunGradientCmd>(cmds.Slice(bodyOff));FoldStruct(ref h, c with { Transform = Reb(c.Transform, ox, oy) }); pos = bodyOff + Unsafe.SizeOf<DrawGlyphRunGradientCmd>(); break; }
                case DrawOp.DrawImage:           { var c = MemoryMarshal.Read<DrawImageCmd>(cmds.Slice(bodyOff));           FoldStruct(ref h, c with { Transform = Reb(c.Transform, ox, oy) }); pos = bodyOff + Unsafe.SizeOf<DrawImageCmd>(); break; }
                case DrawOp.DrawRoundRectStroke: { var c = MemoryMarshal.Read<DrawRoundRectStrokeCmd>(cmds.Slice(bodyOff)); FoldStruct(ref h, c with { Transform = Reb(c.Transform, ox, oy) }); pos = bodyOff + Unsafe.SizeOf<DrawRoundRectStrokeCmd>(); break; }
                case DrawOp.DrawShadow:          { var c = MemoryMarshal.Read<DrawShadowCmd>(cmds.Slice(bodyOff));          FoldStruct(ref h, c with { Transform = Reb(c.Transform, ox, oy) }); pos = bodyOff + Unsafe.SizeOf<DrawShadowCmd>(); break; }
                case DrawOp.DrawGradientRect:    { var c = MemoryMarshal.Read<DrawGradientRectCmd>(cmds.Slice(bodyOff));    FoldStruct(ref h, c with { Transform = Reb(c.Transform, ox, oy) }); pos = bodyOff + Unsafe.SizeOf<DrawGradientRectCmd>(); break; }
                case DrawOp.DrawGradientStroke:  { var c = MemoryMarshal.Read<DrawGradientStrokeCmd>(cmds.Slice(bodyOff));  FoldStruct(ref h, c with { Transform = Reb(c.Transform, ox, oy) }); pos = bodyOff + Unsafe.SizeOf<DrawGradientStrokeCmd>(); break; }
                case DrawOp.DrawArc:             { var c = MemoryMarshal.Read<DrawArcCmd>(cmds.Slice(bodyOff));             FoldStruct(ref h, c with { Transform = Reb(c.Transform, ox, oy) }); pos = bodyOff + Unsafe.SizeOf<DrawArcCmd>(); break; }
                case DrawOp.DrawPolylineStroke:  { var c = MemoryMarshal.Read<DrawPolylineStrokeCmd>(cmds.Slice(bodyOff));  FoldStruct(ref h, c with { Transform = Reb(c.Transform, ox, oy) }); pos = bodyOff + Unsafe.SizeOf<DrawPolylineStrokeCmd>(); break; }
                case DrawOp.DrawTabShape:        { var c = MemoryMarshal.Read<DrawTabShapeCmd>(cmds.Slice(bodyOff));        FoldStruct(ref h, c with { Transform = Reb(c.Transform, ox, oy) }); pos = bodyOff + Unsafe.SizeOf<DrawTabShapeCmd>(); break; }
                case DrawOp.PushClip:            { var c = MemoryMarshal.Read<ClipCmd>(cmds.Slice(bodyOff));               FoldStruct(ref h, RebClip(in c, ox, oy)); pos = bodyOff + Unsafe.SizeOf<ClipCmd>(); break; }
                case DrawOp.PopClip:             { pos = bodyOff; break; }   // no payload; the op code is already folded
                default:                         return false;               // nested PushLayer or unknown ⇒ don't cache
            }
        }
        return false;
    }

    // Deterministic biased grid-round for EVERY rounded fold (rebased positions + seed W/H). A layout value at exactly
    // N.5 (plausible: a centred rail with TransformOriginX 0.5) straddles the integer boundary by ~1 ULP across ease
    // frames under MathF.Round (half-to-even), flipping the key every other frame ⇒ a sustained double-miss for that
    // strip. Constraint: a boundary value must not straddle under sub-unit float wobble — the +1/512 bias rounds a hair
    // above .5 so a wobble below 1/512 never crosses. Used consistently everywhere the key rounds, so hit/miss
    // granularity stays coherent between the seed size and the rebased positions.
    private static float RoundGrid(float x) => MathF.Floor(x + 0.5f + (1f / 512f));

    // Rebase an op's absolute device transform to the layer origin + round to the integer grid the composite snaps to.
    // M11/M12/M21/M22 (scale/rotation) fold verbatim — they are translation-invariant and are legitimate size misses.
    private static Affine2D Reb(in Affine2D t, float ox, float oy) =>
        t with { Dx = RoundGrid(t.Dx - ox), Dy = RoundGrid(t.Dy - oy) };

    // A ClipCmd carries device-space rect ORIGINS (position); rebase both rects' X/Y, keep W/H + corner radius verbatim.
    private static ClipCmd RebClip(in ClipCmd c, float ox, float oy) => c with
    {
        DeviceRect  = new RectF(RoundGrid(c.DeviceRect.X  - ox), RoundGrid(c.DeviceRect.Y  - oy), c.DeviceRect.W,  c.DeviceRect.H),
        RoundedRect = new RectF(RoundGrid(c.RoundedRect.X - ox), RoundGrid(c.RoundedRect.Y - oy), c.RoundedRect.W, c.RoundedRect.H),
    };

    private static void FoldSeed(ref ulong h, in PushLayerCmd L)
    {
        Fold4(ref h, BitConverter.SingleToUInt32Bits(L.BlurSigma));
        Fold4(ref h, (uint)(int)RoundGrid(L.DeviceRect.W));
        Fold4(ref h, (uint)(int)RoundGrid(L.DeviceRect.H));
    }

    private static void FoldOp(ref ulong h, int op) => Fold4(ref h, (uint)op);

    // Fold the raw bytes of one POD command payload. All *Cmd payloads are PAD-FREE (every field is a 4-byte
    // float/int/StringId, naturally aligned), so re-serializing the struct is byte-deterministic frame-to-frame; a
    // future cmd with a non-4-byte field would need field-wise folding instead. The stackalloc frees on return (a
    // per-op call, not a growing loop-local), so this stays allocation-free.
    private static void FoldStruct<T>(ref ulong h, in T v) where T : unmanaged
    {
        Span<T> s = stackalloc T[1];
        s[0] = v;
        ReadOnlySpan<byte> b = MemoryMarshal.AsBytes(s);
        for (int i = 0; i < b.Length; i++) { h ^= b[i]; h *= Prime; }
    }

    private static void Fold4(ref ulong h, uint v)
    {
        for (int i = 0; i < 4; i++) { h ^= (byte)(v >> (i * 8)); h *= Prime; }
    }
}
