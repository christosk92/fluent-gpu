using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FluentGpu.Foundation;
using FluentGpu.Pal;
using FluentGpu.Pal.Windows;
using FluentGpu.Render;
using FluentGpu.Rhi;
using FluentGpu.Rhi.D3D12;

namespace FluentGpu;

/// <summary>
/// Explicit native renderer experiment, not a production trim policy. Run each arm in a fresh process with the
/// same window pixels, DPI, driver and build. The portable command counts below are NOT native D3D command counts.
/// Startup still eagerly builds the production pipelines; separate callbacks expose that cost, not lazy startup.
/// </summary>
internal static partial class DriverMemoryFloorProbe
{
    public static int Run(string[] args)
    {
        var priorCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            return new RunState(args).Run();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[driver-floor] FAIL {ex}");
            return 1;
        }
        finally { CultureInfo.CurrentCulture = priorCulture; }
    }

    private sealed partial class RunState
    {
        private readonly string _arm;
        private readonly int _width, _height;
        private readonly bool _composited;
        private readonly InputEventRing _input = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private Process? _process;
        private IPlatformWindow? _window;
        private D3D12Device? _device;
        private D3D12Device.MemoryProbeSampler? _sampler;
        private ISwapchain? _swapchain;
        private Size2 _size;
        private float _scale;
        private ulong _submits, _presents;
        private int _stageFrames, _portableOps;
        private double _lastCpuMs, _lastSampleMs;

        internal RunState(string[] args)
        {
            _arm = Option(args, "--driver-memory-floor-arm", "baseline");
            if (_arm is not ("baseline" or "reset-list" or "allocators" or "list"))
                throw new ArgumentException("Driver floor arm must be baseline, reset-list, allocators or list.");
            _width = Dimension(args, "--driver-memory-floor-width", 1280);
            _height = Dimension(args, "--driver-memory-floor-height", 800);
            _composited = Array.IndexOf(args, "--driver-memory-floor-opaque") < 0;
            ConfigureFactorial(args);
        }

        internal int Run()
        {
            using var process = Process.GetCurrentProcess();
            _process = process;
            // Warm the logger/process queries before the baseline. No forced collection or working-set trimming.
            process.Refresh();
            Console.Error.WriteLine(
                $"[driver-floor] START pid={process.Id} arm={_arm} arch={RuntimeInformation.ProcessArchitecture} " +
                $"dynamic_code={RuntimeFeature.IsDynamicCodeSupported} requested_px={_width}x{_height} " +
                $"composited={_composited} buffers=3 latency=1 clear_frames={(_factorial ? 3 : 1000)} workload_frames=240 idle_ms=5000");
            Console.Error.WriteLine("[driver-floor] scope=renderer-only; no AppHost/scene/network/audio; " +
                "WS includes shared pages; private_bytes is process private commit; DXGI is not additive to either. " +
                "Sampler retains one adapter reference through device teardown; native command volume is not measured.");
            Sample("process-before-window");
            using var app = new Win32App();
            using var window = app.CreateWindow(new WindowDesc("FluentGpu - driver memory floor", new(_width, _height),
                1f, Composited: _composited, CustomFrame: true));
            _window = window;
            Win32Theme.ApplyWindowMaterial(window.Handle.Value, true, _composited, true, false);
            window.Show();
            Pump();
            ConfigurePhysicalTarget(window);
            _size = window.ClientSizePx;
            _scale = window.Scale;
            Sample("window-before-device");
            var strings = new StringTable();
            _device = new D3D12Device(strings, composited: _composited);
            try
            {
                _device.SetMemoryProbeObserver(stage =>
                {
                    _sampler ??= _device.CreateMemoryProbeSampler();
                    Sample(stage);
                });
                _device.EnsureDeviceCreated();
                _swapchain = _device.CreateSwapchain(new(window.Handle, _size, Composited: _composited));
                var clear = new DrawList();
                if (_factorial) RunFactorialWorkload(strings, clear);
                else
                {
                Frames("clear", clear, 1000);
                var primitives = MakeWorkload(strings, withText: false);
                Frames("primitives", primitives, 240);
                var text = MakeWorkload(strings, withText: true);
                Frames("text", text, 240);
                Frames("return-minimal", clear, 1);
                _device.MemoryProbeDrain();
                Sample("minimal-retired");
                Idle("before-intervention-idle");

                // Every arm reaches identical workload/fence/idle state before its one diagnostic intervention.
                // Allocator replacement must reset the existing closed list to detach recording references;
                // reset-list is the explicit confound control. It resets the list three times without new banks.
                if (_arm == "allocators")
                    for (int bank = 0; bank < 3; bank++)
                    {
                        _device.MemoryProbeReplaceAllocator(bank);
                        Sample($"allocator-{bank}-replaced");
                    }
                else if (_arm == "reset-list")
                    for (int bank = 0; bank < 3; bank++)
                    {
                        _device.MemoryProbeResetCommandList(bank);
                        Sample($"list-reset-control-{bank}");
                    }
                else if (_arm == "list")
                {
                    _device.MemoryProbeReplaceCommandList();
                    Sample("command-list-replaced");
                }
                else Sample("baseline-no-replacement");
                Idle("after-intervention-idle");
                Frames("same-text-rewarm", text, 240);
                Frames("return-minimal-again", clear, 1);
                _device.MemoryProbeDrain();
                Idle("rewarm-retired-idle");
                }

                // Swapchain owns a queue reference: replacing only our queue pointer would not test queue release.
                // These teardown cuts establish broad target/device ownership, never allocator ownership.
                _swapchain.Dispose();
                _swapchain = null;
                Sample("swapchain-disposed");
                Idle("swapchain-disposed-idle");
                var retiring = _device;
                _device = null; // Dispose is not idempotent; never retry it from finally on an exception.
                retiring.Dispose();
                Sample("device-disposed-adapter-observer-retained");
                Idle("device-disposed-idle");
                Console.Error.WriteLine("[driver-floor] PASS completed all stages; memory deltas require external interpretation, not a threshold pass.");
                return 0;
            }
            finally
            {
                try { _swapchain?.Dispose(); }
                finally
                {
                    try { _device?.Dispose(); }
                    finally { _sampler?.Dispose(); }
                }
            }
        }

        private DrawList MakeWorkload(StringTable strings, bool withText)
        {
            var draw = new DrawList(32768);
            StringId family = withText ? strings.Intern("Segoe UI") : default;
            StringId label = withText ? strings.Intern("Wavee 0123456789 - repeatable text workload") : default;
            float width = _size.Width / _scale, height = _size.Height / _scale;
            for (int row = 0; row < 12; row++)
                for (int column = 0; column < 6; column++)
                {
                    var rect = new RectF(column * width / 6 + 4, row * height / 12 + 4, width / 6 - 8, height / 12 - 8);
                    draw.FillRoundRect(rect, new(6, 6, 6, 6), ColorF.FromRgba(35, 45, 60), Affine2D.Identity, 1f);
                    if (withText)
                        draw.DrawGlyphRun(rect, ColorF.FromRgba(235, 235, 240), label, family, 13, 400,
                            0, 0, 1, 0, 0, 0, 0, Affine2D.Identity, 1f);
                }
            return draw;
        }

        private void Frames(string stage, DrawList draw, int count)
        {
            _stageFrames = 0;
            _portableOps = draw.CommandCount;
            Sample(stage + "-before");
            RepaintDamageRegion damage = default;
            damage.ForceFull(RepaintFullReason.TargetInvalidated);
            for (int frame = 0; frame < count; frame++)
            {
                Pump();
                if (_window!.ClientSizePx != _size || _window.Scale != _scale)
                    throw new InvalidOperationException("Window pixels/DPI changed; discard this comparison run.");
                // Production latency semaphore + vsync Present pace every frame. No suppressed waits/unbounded submit.
                var info = new FrameInfo(_size, _scale, ColorF.FromRgba(18, 18, 22),
                    FrameEpoch: _submits + 1, RepaintDamage: damage, PublishSequence: _submits + 1);
                _device!.SubmitDrawList(draw.Bytes, draw.SortKeys, info);
                _submits++;
                if (stage == "clear" && frame == 0) Sample("first-clear-submitted-before-present");
                _swapchain!.Present();
                if (_device.LastPresentStoodDown)
                    throw new InvalidOperationException("Present stood down/occluded; discard this comparison run.");
                _presents++;
                _stageFrames++;
                if (_stageFrames is 1 or 10 or 100 || _stageFrames == count)
                    Sample(stage + "-presented");
            }
            _device!.MemoryProbeDrain();
            Sample(stage + "-retired");
        }

        private void Idle(string stage)
        {
            _stageFrames = 0;
            _portableOps = 0;
            double until = _clock.Elapsed.TotalMilliseconds + 5000;
            Sample(stage + "-begin");
            while (_clock.Elapsed.TotalMilliseconds < until)
            {
                Pump();
                _window!.WaitForWork((int)Math.Clamp(Math.Ceiling(until - _clock.Elapsed.TotalMilliseconds), 1, 100));
            }
            Sample(stage + "-end");
        }

        private void Pump()
        {
            _window!.PumpInto(_input);
            _input.Clear();
            if (_window.IsClosed) throw new OperationCanceledException("Probe window closed before completion.");
        }

        private void Sample(string stage)
        {
            var memory = _sampler?.Read() ?? default;
            var tracked = D3D12Device.MemoryProbeTrackedTotals;
            _process!.Refresh();
            double now = _clock.Elapsed.TotalMilliseconds, cpu = _process.TotalProcessorTime.TotalMilliseconds;
            var gc = GC.GetGCMemoryInfo();
            // Byte units avoid rounding away small cutpoint changes. Fresh DXGI validity is per segment, not inferred.
            Console.Error.WriteLine(
                $"[driver-floor] SAMPLE stage={stage} arm={_arm} qpc={Stopwatch.GetTimestamp()} elapsed_ms={now:F3} " +
                $"interval_ms={now - _lastSampleMs:F3} cpu_ms={cpu - _lastCpuMs:F3} ws_bytes={_process.WorkingSet64} " +
                $"private_bytes={_process.PrivateMemorySize64} dxgi_local_valid={memory.LocalValid} " +
                $"dxgi_local_bytes={memory.LocalUsage} dxgi_local_budget={memory.LocalBudget} " +
                $"dxgi_nonlocal_valid={memory.NonLocalValid} dxgi_nonlocal_bytes={memory.NonLocalUsage} " +
                $"dxgi_nonlocal_budget={memory.NonLocalBudget} tracked_bytes={tracked.bytes} tracked_resources={tracked.count} " +
                $"gc_last_committed_bytes={gc.TotalCommittedBytes} gc_last_heap_bytes={gc.HeapSizeBytes} " +
                $"gc_last_fragmented_bytes={gc.FragmentedBytes} gc_index={gc.Index} submits={_submits} presents={_presents} " +
                $"stage_frames={_stageFrames} portable_ops_per_frame={_portableOps} pixels={_size.Width}x{_size.Height} scale={_scale}" +
                FactorialSampleSuffix());
            _lastSampleMs = now;
            _lastCpuMs = cpu;
        }

        private static string Option(string[] args, string name, string fallback)
        {
            string prefix = name + "=";
            foreach (string arg in args)
                if (arg.StartsWith(prefix, StringComparison.Ordinal)) return arg[prefix.Length..];
            return fallback;
        }

        private static int Dimension(string[] args, string name, int fallback)
        {
            string value = Option(args, name, fallback.ToString(CultureInfo.InvariantCulture));
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int dimension) || dimension < 320 || dimension > 4096)
                throw new ArgumentException($"{name} must be between 320 and 4096 physical pixels.");
            return dimension;
        }
    }
}
