using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FluentGpu.Foundation;
using FluentGpu.Render;
using FluentGpu.Render.Tiles;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using Gen = FluentGpu.Interop.Generated;

using ColorF = FluentGpu.Foundation.ColorF;
using RectF = FluentGpu.Foundation.RectF;

namespace FluentGpu.Rhi.D3D12;

// The retained-tile RASTERIZER (docs/plans/scroll-gpu-retained-tiles-implementation.md §A.4/§B): replay ONE slice segment
// into ONE target — a tile surface, a degraded segment's region scratch, an inline group's scratch — inside its own render
// pass, through the SAME streaming decoder every primitive already goes through, with:
//  • a CANONICAL shifted viewport: TopLeft = the target's offset into slice px, a fixed 16384 px extent, and the logical
//    viewport 16384/scale — so a primitive's device position is computed identically for every tile and every direct
//    region (only the integer TopLeft differs), which is what makes a composited tile bit-identical to a direct raster;
//    a target the canonical window cannot reach (negative slice px, past 16 k px) is brought into it by an integer
//    device-px shift applied to every op (DrawOpTranslate), never by a fractional one;
//  • the segment's scope PREFIX reconstructed first (the clips / stencil clips / inline layers its arena opened before
//    the segment's first byte — a segment can start inside the viewport clip its parent opened before a marker);
//  • per-tile culling: the slice span index skips every clean subtree whose bounds miss the tile, and the decode-time
//    cull drops every primitive whose AABB (+ per-kind halo) misses it — the per-frame instance banks never pay for the
//    whole slice once per tile;
//  • inline FOLDED layers (an effect past the slice budget records inline): an opacity / blur / edge-fade group renders
//    into a scratch surface of the target's size (+ the blur halo) and composites back at its alpha / feather / blur; an
//    acrylic PushLayer erases the frosted rect from what the slice drew before it (the composite draws the backdrop
//    beneath the slice's tiles).
public sealed unsafe partial class D3D12Device
{
    /// <summary>The canonical replay viewport extent (device px) and logical size basis — see the file header.</summary>
    internal const int CanonicalViewport = 16384;

    private readonly byte[] _replayOp = new byte[sizeof(int) + 1024];
    private D3D12_CPU_DESCRIPTOR_HANDLE _replayRtv;
    private int _replayW, _replayH, _replayTlx, _replayTly;
    private int _frameTilesRastered, _frameRenderPasses, _frameInlineGroups, _frameDirectRegions;

    private struct InlineGroup
    {
        public PushLayerCmd L;
        public int Scratch;          // −1 = drawn flat (acrylic erase / no scratch)
        public int Halo;
        public D3D12_CPU_DESCRIPTOR_HANDLE ParentRtv;
        public int ParentW, ParentH, ParentTlx, ParentTly;
    }
    private readonly List<InlineGroup> _inlineGroups = new(8);
    private readonly int[] _prefixOpen = new int[64];

    // ── render passes ─────────────────────────────────────────────────────────────────────────────────────────────

    private enum PassLoad : byte { Clear, Discard, Preserve }

    /// <summary>Open a render pass on <paramref name="rtv"/> (the resource must already be in RENDER_TARGET): CLEAR (to
    /// <paramref name="clear"/>), DISCARD or PRESERVE on entry, PRESERVE (store) on exit. Any open pass ends first.</summary>
    private void BeginPass(D3D12_CPU_DESCRIPTOR_HANDLE rtv, int w, int h, PassLoad load, in ColorF clear = default)
    {
        EndPassIfOpen();
        D3D12_RENDER_PASS_RENDER_TARGET_DESC rt = default;
        rt.cpuDescriptor = rtv;
        rt.BeginningAccess.Type = load switch
        {
            PassLoad.Clear => D3D12_RENDER_PASS_BEGINNING_ACCESS_TYPE.D3D12_RENDER_PASS_BEGINNING_ACCESS_TYPE_CLEAR,
            PassLoad.Discard => D3D12_RENDER_PASS_BEGINNING_ACCESS_TYPE.D3D12_RENDER_PASS_BEGINNING_ACCESS_TYPE_DISCARD,
            _ => D3D12_RENDER_PASS_BEGINNING_ACCESS_TYPE.D3D12_RENDER_PASS_BEGINNING_ACCESS_TYPE_PRESERVE,
        };
        if (load == PassLoad.Clear)
        {
            ref D3D12_CLEAR_VALUE cv = ref rt.BeginningAccess.Anonymous.Clear.ClearValue;
            cv.Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
            cv.Anonymous.Color[0] = clear.R; cv.Anonymous.Color[1] = clear.G;
            cv.Anonymous.Color[2] = clear.B; cv.Anonymous.Color[3] = clear.A;
        }
        rt.EndingAccess.Type = D3D12_RENDER_PASS_ENDING_ACCESS_TYPE.D3D12_RENDER_PASS_ENDING_ACCESS_TYPE_PRESERVE;
        Gen.ID3D12GraphicsCommandList4Vtbl.BeginRenderPass(_list4, 1, &rt, null, D3D12_RENDER_PASS_FLAGS.D3D12_RENDER_PASS_FLAG_NONE);
        Rec(RecordedOp.RenderPassBegin, (uint)rtv.ptr, (uint)load);
        _inRenderPass = true;
        _passRtv = rtv;
        _passRtvW = w; _passRtvH = h;
        _frameRenderPasses++;
        _f!.StencilDsvBound = false;
    }

    /// <summary>Close the open render pass, if any.</summary>
    private void EndPassIfOpen()
    {
        if (!_inRenderPass) return;
        Gen.ID3D12GraphicsCommandList4Vtbl.EndRenderPass(_list4);
        Rec(RecordedOp.RenderPassEnd);
        _inRenderPass = false;
    }

    /// <summary>Re-open a pass on the last pass's target, keeping its contents (after a suspension for a barrier, an
    /// upload or a legacy-bound stencil scope).</summary>
    private void ResumePass() => BeginPass(_passRtv, _passRtvW, _passRtvH, PassLoad.Preserve);

    private bool _inRenderPass;
    private D3D12_CPU_DESCRIPTOR_HANDLE _passRtv;
    private int _passRtvW, _passRtvH;
    private void* _list4;

    // ── replay targets ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Choose the integer device-px shift that brings a target at slice-px offset <paramref name="d"/> (target px =
    /// slice px + d) of <paramref name="extent"/> px into the canonical viewport: 0 whenever the canonical window already
    /// reaches it (the bit-exact case), else exactly −d.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ChooseShift(int d, int extent, out int shiftPx, out int topLeft)
    {
        if (d <= 0 && d >= -(CanonicalViewport - 1) && d + CanonicalViewport >= extent) { shiftPx = 0; topLeft = d; }
        else { shiftPx = -d; topLeft = 0; }
    }

    /// <summary>Point the decoder at a <paramref name="w"/>×<paramref name="h"/> target whose pixel 0 is replay px
    /// −<paramref name="tlx"/>: the canonical viewport at TopLeft (tlx, tly), the scissor chokepoint's origin/extent.</summary>
    private void SetReplayTarget(int tlx, int tly, int w, int h)
    {
        _replayTlx = tlx; _replayTly = tly; _replayW = w; _replayH = h;
        _targetOriginX = -tlx; _targetOriginY = -tly;
        _targetWidth = w; _targetHeight = h;
        D3D12_VIEWPORT vp = new() { TopLeftX = tlx, TopLeftY = tly, Width = CanonicalViewport, Height = CanonicalViewport, MinDepth = 0, MaxDepth = 1 };
        _cmdList->RSSetViewports(1, &vp);
        RecCoalesced(RecordedOp.Viewport, (uint)w, (uint)h);
        _scissorValid = false;
        _desiredScissorValid = false;
        float s = _frameScale <= 0f ? 1f : _frameScale;
        // The target in the replay's DIP space, padded by the scissor's round-out slack.
        _cullRect = new RectF(-tlx / s - CullSafetyDip, -tly / s - CullSafetyDip, w / s + 2f * CullSafetyDip, h / s + 2f * CullSafetyDip);
        if (_replayClampOn)   // a partial raster: nothing outside its damage (replay px) is drawn, so nothing outside it is decoded
        {
            var c = new RectF(_replayClamp.left / s - CullSafetyDip, _replayClamp.top / s - CullSafetyDip,
                (_replayClamp.right - _replayClamp.left) / s + 2f * CullSafetyDip, (_replayClamp.bottom - _replayClamp.top) / s + 2f * CullSafetyDip);
            RectF k = _cullRect.Intersect(c);
            _cullRect = k.IsEmpty ? new RectF(c.X, c.Y, 0f, 0f) : k;
        }
    }

    /// <summary>
    /// Replay slice segment <paramref name="row"/> into the target bound by the OPEN render pass on
    /// <paramref name="rtv"/> (<paramref name="w"/>×<paramref name="h"/>, target px = slice px + (<paramref name="dx"/>,
    /// <paramref name="dy"/>)). <paramref name="cullRelOrigin"/> = the target in the slice's px relative to its origin (the
    /// span index's space). <paramref name="clamp"/> = a PARTIAL raster: every scissor (the decoder's, an inline group's
    /// composite) is cut to <paramref name="damage"/> (target px) and the decode-time cull to it, so only the damage is
    /// written. Leaves the pass open or suspended; the caller ends it.
    /// </summary>
    private void ReplaySegment(in CompositeFrame frame, in SliceRow row, int dx, int dy, int w, int h,
        D3D12_CPU_DESCRIPTOR_HANDLE rtv, in RectF cullRelOrigin, bool clamp = false, PixelRect damage = default)
    {
        float s = _frameScale <= 0f ? 1f : _frameScale;
        ChooseShift(dx, w, out int spx, out int tlx);
        ChooseShift(dy, h, out int spy, out int tly);
        float sx = -spx / s, sy = -spy / s;
        // target px = replay px + tlx (SetScissorRect's arithmetic): the clamp lives in replay px, the one space a tile
        // and the inline-group scratches it opens share.
        _replayClampOn = clamp;
        if (clamp) _replayClamp = new RECT { left = damage.Left - tlx, top = damage.Top - tly, right = damage.Right - tlx, bottom = damage.Bottom - tly };
        _replayRtv = rtv;
        _streamLw = _streamLh = CanonicalViewport / s;
        ClearInsts();
        _clipStack.Clear();
        _roundedClipStack.Clear();
        SetReplayTarget(tlx, tly, w, h);
        _cullActive = true;
        ResetDesiredScissor();

        int baseGroups = _inlineGroups.Count;
        _blendAdditive = _blendBase;   // every replay starts at its floor; the prefix re-opens an additive bracket the cut fell inside
        ReplayPrefix(frame.PrefixOf(in row), sx, sy);
        ReadOnlySpan<SliceSpan> spans = row.SpanIndexCount > 0 && row.SpanIndexStart + row.SpanIndexCount <= frame.SliceSpans.Length
            ? frame.SliceSpans.Slice(row.SpanIndexStart, row.SpanIndexCount) : default;
        // A span's recorded bounds are its subtree's boxes and halos, not the antialiasing a primitive spills past its
        // edge: the span cull reaches that much further (a rect ending exactly on a tile's edge still paints the tile's
        // first column).
        float aa = RepaintCull.AaHaloDip * s;
        var spanCull = new RectF(cullRelOrigin.X - aa, cullRelOrigin.Y - aa, cullRelOrigin.W + 2f * aa, cullRelOrigin.H + 2f * aa);
        ReplayStream(frame.StreamOf(in row), sx, sy, spans, in spanCull);
        FlushSegment(_streamLw, _streamLh);
        while (_inlineGroups.Count > baseGroups) CloseInlineLayer();
        EndStencilScopesAtSubmitEnd(_replayRtv);
        _clipStack.Clear();
        _roundedClipStack.Clear();
        _cullActive = false;
        _replayClampOn = false;
    }

    /// <summary>Re-open the scopes the segment's arena left open before its first byte (in stream order), and the paint
    /// blend the arena's last <see cref="DrawOp.SetBlend"/> before it left set.</summary>
    private void ReplayPrefix(ReadOnlySpan<byte> prefix, float sx, float sy)
    {
        if (prefix.IsEmpty) return;
        int open = 0, pos = 0;
        bool additive = false;
        while (pos + sizeof(int) <= prefix.Length)
        {
            var op = (DrawOp)MemoryMarshal.Read<int>(prefix[pos..]);
            if (!RepaintStreamSafety.TryBodySize(op, out int body) || pos + sizeof(int) + body > prefix.Length) break;
            switch (op)
            {
                case DrawOp.PushClip:
                case DrawOp.PushStencilClip:
                case DrawOp.PushLayer:
                    if (open < _prefixOpen.Length) _prefixOpen[open] = pos;
                    open++;
                    break;
                case DrawOp.PopClip:
                case DrawOp.PopStencilClip:
                case DrawOp.PopLayer:
                    if (open > 0) open--;
                    break;
                case DrawOp.SetBlend:
                    additive = MemoryMarshal.Read<SetBlendCmd>(prefix[(pos + sizeof(int))..]).Mode == (int)PaintBlend.Additive;
                    break;
            }
            pos += sizeof(int) + body;
        }
        _blendAdditive = _blendBase || additive;
        open = Math.Min(open, _prefixOpen.Length);
        for (int i = 0; i < open; i++)
        {
            int at = _prefixOpen[i];
            var op = (DrawOp)MemoryMarshal.Read<int>(prefix[at..]);
            RepaintStreamSafety.TryBodySize(op, out int body);
            ReadOnlySpan<byte> rec = Moved(op, prefix.Slice(at + sizeof(int), body), sx, sy);
            ApplyScopeOp(op, rec);
        }
    }

    /// <summary>The op's payload moved by the replay shift (a scratch copy — only when the shift is non-zero).</summary>
    private ReadOnlySpan<byte> Moved(DrawOp op, ReadOnlySpan<byte> payload, float sx, float sy)
    {
        if ((sx == 0f && sy == 0f) || payload.Length > _replayOp.Length - sizeof(int)) return payload;
        Span<byte> dst = _replayOp.AsSpan(sizeof(int), payload.Length);
        payload.CopyTo(dst);
        DrawOpTranslate.Apply(op, dst, sx, sy);
        return dst;
    }

    private void ApplyScopeOp(DrawOp op, ReadOnlySpan<byte> payload)
    {
        switch (op)
        {
            case DrawOp.PushClip:
            {
                var clip = MemoryMarshal.Read<ClipCmd>(payload);
                _frameClipOps++;
                PushScissor(in clip);
                EnsureDesiredScissor(CurrentScissorRect(), _streamLw, _streamLh);
                break;
            }
            case DrawOp.PopClip:
                _frameClipOps++;
                PopScissor();
                EnsureDesiredScissor(CurrentScissorRect(), _streamLw, _streamLh);
                break;
            case DrawOp.PushStencilClip:
            {
                var c = MemoryMarshal.Read<PushStencilClipCmd>(payload);
                _frameClipOps++;
                BeginStencilScope(in c, _streamLw, _streamLh, _replayRtv);
                break;
            }
            case DrawOp.PopStencilClip:
            {
                var c = MemoryMarshal.Read<PopStencilClipCmd>(payload);
                _frameClipOps++;
                EndStencilScope(in c, _streamLw, _streamLh, _replayRtv);
                break;
            }
            case DrawOp.PushLayer:
                OpenInlineLayer(MemoryMarshal.Read<PushLayerCmd>(payload));
                break;
            case DrawOp.PopLayer:
                if (_inlineGroups.Count > 0) CloseInlineLayer();
                break;
        }
    }

    private void ReplayStream(ReadOnlySpan<byte> stream, float sx, float sy, ReadOnlySpan<SliceSpan> spans, in RectF cullRelOrigin)
    {
        bool moved = sx != 0f || sy != 0f;
        bool cull = !spans.IsEmpty;
        int pos = 0, e = 0;
        while (pos + sizeof(int) <= stream.Length)
        {
            if (cull)
            {
                while (e < spans.Length && spans[e].ByteStart < pos) e++;
                bool skipped = false;
                while (e < spans.Length && spans[e].ByteStart == pos)
                {
                    ref readonly SliceSpan en = ref spans[e];
                    if (!en.HasMarker && en.ByteLength > 0 && !en.Bounds.Overlaps(cullRelOrigin))
                    {
                        pos = en.ByteStart + en.ByteLength;
                        skipped = true;
                        break;
                    }
                    e++;
                }
                if (skipped) continue;
            }
            var op = (DrawOp)MemoryMarshal.Read<int>(stream[pos..]);
            if (!RepaintStreamSafety.TryBodySize(op, out int body) || pos + sizeof(int) + body > stream.Length) break;
            ReadOnlySpan<byte> payload = stream.Slice(pos + sizeof(int), body);
            switch (op)
            {
                case DrawOp.PushClip:
                case DrawOp.PopClip:
                case DrawOp.PushStencilClip:
                case DrawOp.PopStencilClip:
                case DrawOp.PushLayer:
                case DrawOp.PopLayer:
                    ApplyScopeOp(op, moved ? Moved(op, payload, sx, sy) : payload);
                    break;
                case DrawOp.CompositeSlice:
                    break;   // a child slice composites as its own item
                default:
                    if (moved && body <= _replayOp.Length - sizeof(int))
                    {
                        MemoryMarshal.Write(_replayOp.AsSpan(), (int)op);
                        Moved(op, payload, sx, sy);
                        DecodeOne(_replayOp.AsSpan(0, sizeof(int) + body), 0);
                    }
                    else DecodeOne(stream, pos);
                    break;
            }
            pos += sizeof(int) + body;
        }
    }

    // ── inline (folded) layers ────────────────────────────────────────────────────────────────────────────────────

    private void OpenInlineLayer(in PushLayerCmd L)
    {
        _frameLayerOps++;
        if (L.Kind == (int)LayerKind.Acrylic)
        {
            // The slice composites ABOVE its frosted backdrop: remove what the slice drew before the surface under its
            // rounded rect (its own shadow, a plate) so the frost reads through exactly as it did when the backdrop
            // composited at push time. A feathered surface keeps what is above its feather.
            if (L.FeatherFrac <= 0f && L.GroupAlpha > 0f
                && !Cull(L.DeviceRect.X, L.DeviceRect.Y, L.DeviceRect.W, L.DeviceRect.H, 1f, 0f, 0f, 1f, 0f, 0f, RepaintCull.AaHaloDip))
            {
                CoverPendingText(L.DeviceRect.X, L.DeviceRect.Y, L.DeviceRect.W, L.DeviceRect.H, 1f, 0f, 0f, 1f, 0f, 0f, RepaintCull.AaHaloDip);
                var inst = new RectInstance
                {
                    PosX = L.DeviceRect.X, PosY = L.DeviceRect.Y, W = L.DeviceRect.W, H = L.DeviceRect.H,
                    RTL = L.Radii.TopLeft, RTR = L.Radii.TopRight, RBR = L.Radii.BottomRight, RBL = L.Radii.BottomLeft,
                    R = 0f, G = 0f, B = 0f, A = 1f,
                    M11 = 1f, M12 = 0f, M21 = 0f, M22 = 1f, Dx = 0f, Dy = 0f, Opacity = L.GroupAlpha,
                };
                ApplyRoundedClip(ref inst);
                _rectInsts.Add(inst);
                _frameRectCount++;
                PushRun(PrimKind.VideoHole);
            }
            _inlineGroups.Add(new InlineGroup { L = L, Scratch = -1 });
            return;
        }
        FlushSegment(_streamLw, _streamLh);
        // The scratch's origin stays on the parent target's pixel-quad / blur-phase grid: the reach is a multiple of the
        // blur's downsample factor, rounded up to even for the factor-1 case (TileGrid.OriginGrid).
        int halo = (L.Kind == (int)LayerKind.Blur || L.Kind == (int)LayerKind.EdgeFade) && L.BlurSigma > 0f
            ? (SelfBlurRegion.TapRadius(L.BlurSigma) + 1) & ~1 : 0;
        int gw = _replayW + 2 * halo, gh = _replayH + 2 * halo;
        int scratch = _surfaces!.AcquireScratch(gw, gh, _fenceValue + 1);
        if (scratch < 0) { _inlineGroups.Add(new InlineGroup { L = L, Scratch = -1 }); return; }
        _frameInlineGroups++;
        var g = new InlineGroup
        {
            L = L, Scratch = scratch, Halo = halo,
            ParentRtv = _replayRtv, ParentW = _replayW, ParentH = _replayH, ParentTlx = _replayTlx, ParentTly = _replayTly,
        };
        _inlineGroups.Add(g);
        EndPassIfOpen();
        ScratchBarrier(scratch, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
        var rtv = _surfaces.ScratchRtv(scratch);
        BeginPass(rtv, gw, gh, PassLoad.Clear);
        _replayRtv = rtv;
        SetReplayTarget(g.ParentTlx + halo, g.ParentTly + halo, gw, gh);
        InvalidateCmdState();
    }

    private void CloseInlineLayer()
    {
        var g = _inlineGroups[^1];
        _inlineGroups.RemoveAt(_inlineGroups.Count - 1);
        if (g.Scratch < 0) return;
        FlushSegment(_streamLw, _streamLh);
        EndPassIfOpen();
        ScratchBarrier(g.Scratch, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
        int gw = g.ParentW + 2 * g.Halo, gh = g.ParentH + 2 * g.Halo;
        int src = g.Scratch, down = 1;
        if (g.Halo > 0) src = BlurSurface(g.Scratch, gw, gh, g.L.BlurSigma, out down);

        // back onto the parent target
        BeginPass(g.ParentRtv, g.ParentW, g.ParentH, PassLoad.Preserve);
        BindCompositor(g.ParentW, g.ParentH);
        // the group's composite clip (its drawn extent / inherited clip), in parent target px
        float s = _frameScale <= 0f ? 1f : _frameScale;
        RECT sc = ClampToReplay(CurrentScissorRect());
        int l = sc.left + g.ParentTlx, t = sc.top + g.ParentTly, r = sc.right + g.ParentTlx, b = sc.bottom + g.ParentTly;
        if (!g.L.CompositeClip.IsEmpty && !g.L.CompositeClip.IsInfinite)
        {
            RectF cc = g.L.CompositeClip;
            l = Math.Max(l, (int)MathF.Floor(cc.X * s) + g.ParentTlx); t = Math.Max(t, (int)MathF.Floor(cc.Y * s) + g.ParentTly);
            r = Math.Min(r, (int)MathF.Ceiling(cc.Right * s) + g.ParentTlx); b = Math.Min(b, (int)MathF.Ceiling(cc.Bottom * s) + g.ParentTly);
        }
        _compositor!.Scissor(_cmdList, l, t, r, b);
        _compositor.Begin(0f, 0f, g.ParentW, g.ParentH);
        _compositor.Alpha(g.L.GroupAlpha);
        if (g.L.Kind == (int)LayerKind.EdgeFade && (_frameKnockouts & GpuKnockouts.EdgeFadesOff) == 0)
        {
            RectF fr = g.L.DeviceRect;
            var feather = new EdgeFeather(new RectF(fr.X * s + g.ParentTlx, fr.Y * s + g.ParentTly, fr.W * s, fr.H * s),
                g.L.FadeBandL * s, g.L.FadeBandT * s, g.L.FadeBandR * s, g.L.FadeBandB * s,
                new CornerRadius4(g.L.Radii.TopLeft * s, g.L.Radii.TopRight * s, g.L.Radii.BottomRight * s, g.L.Radii.BottomLeft * s),
                (FadeFalloff)g.L.FadeFalloff, g.L.FadeIntensity);
            _compositor.Feather(in feather);
        }
        if (down == 1)
        {
            _compositor.SourceOrigin(g.Halo, g.Halo);
            _compositor.Draw(_cmdList, SliceCompositor.Pso.Load, _surfaces!.ScratchSrv(src));
        }
        else
        {
            _compositor.SampleMap(-g.Halo, -g.Halo, 1f / (down * _surfaces!.ScratchW(src)), 1f / (down * _surfaces.ScratchH(src)));
            _compositor.Draw(_cmdList, SliceCompositor.Pso.Sample, _surfaces.ScratchSrv(src));
        }
        if (src != g.Scratch) _surfaces.ReleaseScratch(src);
        _surfaces.ReleaseScratch(g.Scratch);
        // hand the target back to the decoder
        InvalidateCmdState();
        _replayRtv = g.ParentRtv;
        SetReplayTarget(g.ParentTlx, g.ParentTly, g.ParentW, g.ParentH);
    }

    // ── barriers ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Transition scratch <paramref name="i"/> to <paramref name="to"/> (tracked; no-op when already there).
    /// <paramref name="i"/> &lt; 0 is a no-op (a call site that names no surface).</summary>
    private void ScratchBarrier(int i, D3D12_RESOURCE_STATES to)
    {
        if (i < 0) return;
        var from = _surfaces!.ScratchState(i);
        if (from == to) return;
        bool reopen = _inRenderPass;
        if (reopen) EndPassIfOpen();
        Barrier(_surfaces.ScratchResource(i), from, to);
        _surfaces.SetScratchState(i, to);
        if (reopen) ResumePass();
    }

    // ── blur ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Blur scratch <paramref name="src"/> (its top-left <paramref name="w"/>×<paramref name="h"/> px used, cleared
    /// around, in PIXEL_SHADER_RESOURCE) by a Gaussian of <paramref name="sigma"/> device px: a 2× box downsample chain to
    /// <see cref="AcrylicBackdropMath.DownsampleFactor"/> (so the kernel stays ≤ 4 texels σ), then one horizontal and one
    /// vertical bilinear-folded pass (<see cref="AcrylicBackdropMath.BuildKernel"/>). Returns the scratch holding the
    /// result (a new lease; <paramref name="src"/> is untouched) at 1/<paramref name="down"/> resolution. No pass is left
    /// open.
    /// </summary>
    private int BlurSurface(int src, int w, int h, float sigma, out int down)
    {
        down = AcrylicBackdropMath.DownsampleFactor(sigma, 1f);
        float texelSigma = AcrylicBackdropMath.EffectiveTexelSigma(sigma, 1f, down);
        int cur = src, cw = w, ch = h;
        ulong fence = _fenceValue + 1;
        for (int d = 1; d < down; d <<= 1)
        {
            int nw = Math.Max(1, (cw + 1) / 2), nh = Math.Max(1, (ch + 1) / 2);
            int dst = _surfaces!.AcquireScratch(nw, nh, fence);
            if (dst < 0) { down = d; break; }
            ScratchBarrier(dst, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
            BeginPass(_surfaces.ScratchRtv(dst), _surfaces.ScratchW(dst), _surfaces.ScratchH(dst), PassLoad.Clear);
            BindCompositor(_surfaces.ScratchW(dst), _surfaces.ScratchH(dst));
            _compositor!.Scissor(_cmdList, 0, 0, nw, nh);
            _compositor.Begin(0f, 0f, nw, nh);
            _compositor.K[8] = 1f / _surfaces.ScratchW(cur); _compositor.K[9] = 1f / _surfaces.ScratchH(cur);
            _compositor.Draw(_cmdList, SliceCompositor.Pso.Down2, _surfaces.ScratchSrv(cur));
            EndPassIfOpen();
            ScratchBarrier(dst, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
            if (cur != src) _surfaces.ReleaseScratch(cur);
            cur = dst; cw = nw; ch = nh;
        }
        Span<float> off = stackalloc float[8], wt = stackalloc float[8];
        int taps = Math.Min(8, AcrylicBackdropMath.BuildKernel(texelSigma, off, wt));
        int a = _surfaces!.AcquireScratch(cw, ch, fence);
        int b = _surfaces.AcquireScratch(cw, ch, fence);
        if (a < 0 || b < 0)
        {
            if (a >= 0) _surfaces.ReleaseScratch(a);
            if (b >= 0) _surfaces.ReleaseScratch(b);
            if (cur == src) { down = 1; }
            return cur;   // out of scratch: an unblurred (still correct in placement) result
        }
        BlurPass(cur, a, cw, ch, 1f, 0f, off, wt, taps);
        BlurPass(a, b, cw, ch, 0f, 1f, off, wt, taps);
        _surfaces.ReleaseScratch(a);
        if (cur != src) _surfaces.ReleaseScratch(cur);
        return b;
    }

    private void BlurPass(int src, int dst, int w, int h, float dirX, float dirY, ReadOnlySpan<float> off, ReadOnlySpan<float> wt, int taps)
    {
        ScratchBarrier(dst, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
        BeginPass(_surfaces!.ScratchRtv(dst), _surfaces.ScratchW(dst), _surfaces.ScratchH(dst), PassLoad.Clear);
        BindCompositor(_surfaces.ScratchW(dst), _surfaces.ScratchH(dst));
        _compositor!.Scissor(_cmdList, 0, 0, w, h);
        _compositor.Begin(0f, 0f, w, h);
        var k = _compositor.K;
        k[8] = 1f / _surfaces.ScratchW(src); k[9] = 1f / _surfaces.ScratchH(src); k[10] = dirX; k[11] = dirY;
        for (int t = 0; t < 8; t++)
        {
            k[16 + 2 * t] = t < taps ? off[t] : 0f;
            k[17 + 2 * t] = t < taps ? wt[t] : 0f;
        }
        k[32] = taps;
        _compositor.Draw(_cmdList, SliceCompositor.Pso.Blur, _surfaces.ScratchSrv(src));
        EndPassIfOpen();
        ScratchBarrier(dst, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
    }

    /// <summary>Bind the compositor for the target bound by the open pass and make every decoder pipe rebind after it.</summary>
    private void BindCompositor(int w, int h)
    {
        _compositor!.Bind(_cmdList, _surfaces!.SrvHeap, w, h);
        InvalidateCmdState();
    }
}
