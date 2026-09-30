using System.Globalization;
using FluentGpu.Foundation;
using FluentGpu.Pal;
using FluentGpu.Pal.Windows;
using FluentGpu.Render;
using FluentGpu.Rhi;
using FluentGpu.Scene;

namespace FluentGpu;

internal static partial class DriverMemoryFloorProbe
{
    private sealed partial class RunState
    {
        private bool _factorial;
        private string _workload = "classic";
        private int _physicalWidth, _physicalHeight;
        private const int SyntheticImageCount = 130, SyntheticImageBase = 10_000;
        private bool _imagesStaged;

        private void ConfigureFactorial(string[] args)
        {
            _workload = Option(args, "--driver-memory-floor-workload", "classic");
            if (_workload is not ("classic" or "mixed" or "images" or "opacity" or "blur" or "edgefade" or "stencil"))
                throw new ArgumentException("Workload must be classic, mixed, images, opacity, blur, edgefade or stencil.");
            _factorial = _workload != "classic";
            if (_factorial && _arm != "baseline")
                throw new ArgumentException("Factorial runs isolate one workload; allocator controls use workload=classic.");
            if (_factorial)
            {
                _physicalWidth = Dimension(args, "--driver-memory-floor-physical-width", 1770);
                _physicalHeight = Dimension(args, "--driver-memory-floor-physical-height", 1140);
            }
        }

        private void ConfigurePhysicalTarget(IPlatformWindow window)
        {
            if (!_factorial) return;
            if (window is not Win32Window native) throw new InvalidOperationException("Factorial requires Win32 client sizing.");
            native.SetClientSize(_physicalWidth, _physicalHeight); // Existing PAL diagnostic seam; before device/swapchain.
            Pump();
            if (window.ClientSizePx != new Size2(_physicalWidth, _physicalHeight))
                throw new InvalidOperationException($"Requested physical target was not obtained: {window.ClientSizePx}.");
            Console.Error.WriteLine($"[driver-floor] FACTORIAL workload={_workload} " +
                $"physical={_physicalWidth}x{_physicalHeight} scale={window.Scale} cards=72 " +
                "images_if_enabled=130 sizes=64:77,128:30,256:20,512:3; no network/account content");
        }

        private void RunFactorialWorkload(StringTable strings, DrawList clear)
        {
            Frames("factorial-clear", clear, 3);
            var common = MakeFactorialWorkload(strings, "mixed");
            // The same mixed workload baseline precedes every family arm.
            FactorialFrames("common-mixed", common, count: 16);
            Idle("common-mixed-retired-idle");
            try
            {
                if (_workload == "images") StageSyntheticImages();
                var selected = _workload == "mixed" ? common : MakeFactorialWorkload(strings, _workload);
                FactorialFrames("selected-" + _workload, selected, 240);
                Console.Error.WriteLine("[driver-floor] selected_resources " + _device!.DiagGpuDetail);
                Frames("factorial-return-minimal", clear, 1);
                Idle("factorial-return-minimal-retired-idle");
                // Repeating an identical cohort distinguishes retained high-water from ongoing growth.
                FactorialFrames("same-family-rewarm", selected, 240);
                Frames("factorial-return-minimal-again", clear, 1);
                Idle("factorial-rewarm-retired-idle");
            }
            finally
            {
                if (_imagesStaged)
                {
                    _device!.MemoryProbeDrain();
                    for (int i = 0; i < SyntheticImageCount; i++) _device.EvictImage(SyntheticImageBase + i);
                    Sample("images-evicted-after-use-fence-before-retire-drain");
                    // Explicit diagnostic submit, counted separately: the current store drains retirements in FlushUploads.
                    Frames("image-evictions-flushed-clear", clear, 1);
                    Idle("image-evictions-retired-idle");
                }
            }
        }

        private void StageSyntheticImages()
        {
            _portableOps = 0;
            _stageFrames = 0;
            Sample("images-before-cpu-staging");
            _imagesStaged = true; // Also releases the successfully accepted prefix if a later stage rejects.
            long payloadBytes = 0;
            for (int i = 0; i < SyntheticImageCount; i++)
            {
                int edge = i < 77 ? 64 : i < 107 ? 128 : i < 127 ? 256 : 512;
                var pixels = new byte[edge * edge * 4];
                for (int y = 0; y < edge; y++)
                    for (int x = 0; x < edge; x++)
                    {
                        int p = (y * edge + x) * 4;
                        pixels[p] = (byte)((x * 3 + i * 13) & 255);
                        pixels[p + 1] = (byte)((y * 5 + i * 7) & 255);
                        pixels[p + 2] = (byte)(((x ^ y) + i * 11) & 255);
                        pixels[p + 3] = 255;
                    }
                ImageUploadResult result = _device!.TryUploadImage(SyntheticImageBase + i, pixels, edge, edge);
                if (result != ImageUploadResult.Accepted)
                    throw new InvalidOperationException($"Synthetic image {i} ({edge}) rejected: {result}.");
                payloadBytes += pixels.Length;
            }
            Console.Error.WriteLine($"[driver-floor] images_cpu_staged count={SyntheticImageCount} payload_bytes={payloadBytes} " +
                "source_arrays_not_cached=true; CPU staging may allocate driver backing; GPU activation/copies not yet submitted");
            Sample("images-after-cpu-staging-before-first-gpu-submit");
        }

        private DrawList MakeFactorialWorkload(StringTable strings, string family)
        {
            var draw = new DrawList(65536);
            float width = _size.Width / _scale, height = _size.Height / _scale;
            var whole = new RectF(0, 0, width, height);
            draw.FillRoundRect(whole, default, ColorF.FromRgba(18, 18, 22), Affine2D.Identity, 1);
            int layerStart = draw.Bytes.Length;
            if (family == "opacity")
            {
                // Two nested full-window groups reproduce the first Wavee sample's two full-target pool resources.
                draw.PushOpacityLayer(whole, default, .92f);
                draw.PushOpacityLayer(whole, default, .92f);
            }
            else if (family == "blur")
                draw.PushBlurLayer(whole, default, 8, 1, compositeClip: whole);
            else if (family == "edgefade")
                draw.PushEdgeFadeLayer(whole, whole, default, 1, 15, 24, 24, 24, 24, 0, 1);

            // "stencil" wraps the WHOLE card grid in ONE tier-3 stencil-path clip (a real tessellated rounded-rect
            // mask, not the plain rectangular scissor PushClip gives every other family) — the structural analogue of
            // how opacity/blur/edgefade wrap the same 72 cards in one full-window layer above. A per-card stencil
            // scope (72 pushes/pops of tiny masks) would mostly measure PathRealizationCache slab growth instead of
            // isolating the driver-side stencil DSV/target resource the other families' single full-window RT
            // isolates; one full-window scope keeps the resource footprint comparable across families. It does not
            // nest inside a layer, so this family owns the "no layer" slot the other three occupy with their own
            // single full-window wrapper.
            PathRef stencilClipRef = default;
            if (family == "stencil")
            {
                PathData stencilPath = BuildRoundedRectPath(whole, MathF.Min(24f, MathF.Min(whole.W, whole.H) * .5f));
                if (!PathRealizationCache.Shared.TryRealizeFill(stencilPath, FillRule.NonZero, _scale, out stencilClipRef)
                    || stencilClipRef.VtxCount == 0)
                    throw new InvalidOperationException("Stencil family: whole-window rounded-rect mask failed to realize.");
                draw.PushStencilClip(whole, stencilClipRef, (byte)FillRule.NonZero, Affine2D.Identity);
            }

            StringId font = strings.Intern("Segoe UI");
            for (int i = 0; i < 72; i++)
            {
                int row = i / 6, column = i % 6;
                var rect = new RectF(column * width / 6 + 4, row * height / 12 + 4, width / 6 - 8, height / 12 - 8);
                draw.PushClip(rect);
                draw.FillRoundRect(rect, new(6, 6, 6, 6), ColorF.FromRgba(35, 45, 60), Affine2D.Identity, 1);
                if (family == "images")
                {
                    var art = new RectF(rect.X + 2, rect.Y + 2, MathF.Min(40, rect.W / 3), MathF.Min(40, rect.H - 4));
                    // Stride covers small and larger resource buckets, instead of drawing only the first 64px cohort.
                    int id = SyntheticImageBase + i * 37 % SyntheticImageCount;
                    draw.DrawImage(art, new(4, 4, 4, 4), id, true, default, Affine2D.Identity, 1, new(0, 0, 1, 1));
                }
                string label = "Track " + i.ToString("D3", CultureInfo.InvariantCulture) + " Artist 03:45";
                // Identical labels/positions in every matched arm; images add draws, not a different text layout.
                var text = new RectF(rect.X + 44, rect.Y + 4, MathF.Max(1, rect.W - 48), MathF.Max(1, rect.H - 8));
                draw.DrawGlyphRun(text, ColorF.FromRgba(235, 235, 240), strings.Intern(label), font,
                    13, i % 2 == 0 ? 400 : 600, 0, 0, 1, 0, 0, 0, 0, Affine2D.Identity, 1);
                draw.PopClip();
            }
            if (family == "opacity")
            {
                draw.PopLayer(whole);
                draw.PopLayer(whole);
                draw.PatchOpacityLayerExtent(layerStart, whole);
            }
            else if (family is "blur" or "edgefade") draw.PopLayer(whole);
            else if (family == "stencil") draw.PopStencilClip(whole, stencilClipRef, Affine2D.Identity);
            return draw;
        }

        /// <summary>A rounded rect built as a real cubic-bezier path (not the RectF+CornerRadius4 primitive the
        /// FillRoundRect/PushClipRounded SDF ops take) — the stencil family needs an actual tessellated
        /// <see cref="PathRef"/> to push through <see cref="DrawList.PushStencilClip"/>, which exists to mask
        /// arbitrary silhouettes, not axis-aligned boxes. Kappa is the standard circular-arc cubic-bezier
        /// approximation constant (four symmetric corner arcs, clockwise from top-left).</summary>
        private static PathData BuildRoundedRectPath(in RectF rect, float radius)
        {
            const float kappa = 0.5522847498f;
            float r = MathF.Max(0f, radius);
            float k = r * kappa;
            float x0 = rect.X, y0 = rect.Y, x1 = rect.X + rect.W, y1 = rect.Y + rect.H;
            var b = new PathBuilder();
            b.MoveTo(x0 + r, y0);
            b.LineTo(x1 - r, y0);
            b.CubicTo(x1 - r + k, y0, x1, y0 + r - k, x1, y0 + r);
            b.LineTo(x1, y1 - r);
            b.CubicTo(x1, y1 - r + k, x1 - r + k, y1, x1 - r, y1);
            b.LineTo(x0 + r, y1);
            b.CubicTo(x0 + r - k, y1, x0, y1 - r + k, x0, y1 - r);
            b.LineTo(x0, y0 + r);
            b.CubicTo(x0, y0 + r - k, x0 + r - k, y0, x0 + r, y0);
            b.Close();
            return b.Finish(PathContentEpoch.Mint(), FillRule.NonZero);
        }

        private void FactorialFrames(string stage, DrawList draw, int count)
        {
            _portableOps = draw.CommandCount;
            _stageFrames = 0;
            Console.Error.WriteLine($"[driver-floor] COHORT stage={stage} route=direct " +
                $"bytes={draw.Bytes.Length} sort_keys={draw.SortKeys.Length} portable_ops={draw.CommandCount} opcodes=[{draw.OpcodeStats}]");
            Sample(stage + "-before");
            for (int frame = 0; frame < count; frame++)
            {
                Pump();
                if (_window!.ClientSizePx != _size || _window.Scale != _scale)
                    throw new InvalidOperationException("Factorial target changed; discard this run.");
                RepaintDamageRegion damage = default;
                damage.ForceFull(RepaintFullReason.TargetInvalidated);
                ulong seq = _submits + 1;
                var info = new FrameInfo(_size, _scale, ColorF.FromRgba(18, 18, 22),
                    RepaintDamage: damage, PublishSequence: seq, CarriedFromSeq: seq);
                _device!.SubmitDrawList(draw.Bytes, draw.SortKeys, info);
                _submits++;
                _stageFrames++;
                if (frame == 0) Sample(stage + "-first-submitted-before-present");
                var observed = _device.MemoryProbeWorkload;
                if (observed.DroppedInstances != 0 || observed.ImagesSkipped != 0)
                    throw new InvalidOperationException($"Factorial coverage mismatch: actual={observed}.");
                _swapchain!.Present();
                if (_device.LastPresentStoodDown) throw new InvalidOperationException("Factorial present stood down; discard run.");
                _presents++;
                if (_stageFrames is 1 or 2 or 3 or 10 or 100 || _stageFrames == count) Sample(stage + "-presented");
            }
            _device!.MemoryProbeDrain();
            Sample(stage + "-retired");
        }

        private string FactorialSampleSuffix()
            => !_factorial || _device is null || _submits == 0 ? ""
                : $" family={_workload} last_submit=[{_device.MemoryProbeWorkload}]";
    }
}
