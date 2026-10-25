using System;
using System.Diagnostics;
using System.Threading;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Pal.Windows;

/// <summary>
/// One monitor's own vblank as a signal: a dedicated background thread blocked in <c>IDXGIOutput::WaitForVBlank</c> on the DXGI
/// output whose <c>DXGI_OUTPUT_DESC.Monitor</c> is the window's monitor, stamping each return with QPC and setting
/// <see cref="Event"/>. <see cref="Win32CompositorClock"/> uses it instead of the DComp compositor clock when the window's panel
/// runs at a different rate than that clock (a 100 Hz external display beside a 120 Hz laptop panel: the DComp clock ticks at
/// 120 Hz whichever panel the window is on, so the window would produce on the wrong beat and phase).
/// <para><b>Never joined.</b> <c>WaitForVBlank</c> has no timeout: a panel that stops producing vblanks (sleep, unplug) can hold the
/// thread inside it. The clock never waits on this thread — it waits on <see cref="Event"/> with its own liveness timeout and
/// abandons a pump that went quiet (<see cref="Dispose"/> only asks the thread to stop; a stuck one is a background thread that
/// dies with the process). Every DXGI object is created, used and released on the pump's own thread.</para>
/// </summary>
internal sealed unsafe class OutputVBlankPump : IDisposable
{
    private readonly nint _monitor;
    private readonly AutoResetEvent _event = new(false);
    private volatile bool _stop;
    private long _lastQpc, _count;
    private volatile int _state;   // 0 starting, 1 running, 2 failed (no output for the monitor, or WaitForVBlank failed)
    private volatile bool _paused;
    private readonly AutoResetEvent _resume = new(false);

    public OutputVBlankPump(nint monitor)
    {
        _monitor = monitor;
        var t = new Thread(Run) { IsBackground = true, Name = "fgpu-output-vblank", Priority = ThreadPriority.AboveNormal };
        t.Start();
    }

    /// <summary>The monitor this pump waits on.</summary>
    public nint Monitor => _monitor;
    /// <summary>Set once per vblank (auto-reset).</summary>
    public WaitHandle Event => _event;
    /// <summary>QPC of the latest vblank return (stamped right after <c>WaitForVBlank</c> returned).</summary>
    public long LastQpc => Volatile.Read(ref _lastQpc);
    /// <summary>Vblanks delivered so far.</summary>
    public long Count => Volatile.Read(ref _count);
    /// <summary>True once the pump could not find the output or a wait failed: the clock falls back to the DComp clock.</summary>
    public bool Failed => _state == 2;

    private void Run()
    {
        IDXGIFactory4* factory = null;
        IDXGIOutput* output = null;
        try
        {
            if ((int)CreateDXGIFactory2(0, __uuidof<IDXGIFactory4>(), (void**)&factory) < 0 || factory == null) { _state = 2; return; }
            for (uint i = 0; output == null; i++)
            {
                IDXGIAdapter1* a = null;
                if ((int)factory->EnumAdapters1(i, &a) < 0 || a == null) break;   // every adapter: the panel may hang off another one
                for (uint j = 0; ; j++)
                {
                    IDXGIOutput* o = null;
                    if ((int)a->EnumOutputs(j, &o) < 0 || o == null) break;
                    DXGI_OUTPUT_DESC d = default;
                    if ((int)o->GetDesc(&d) >= 0 && (nint)d.Monitor.Value == _monitor) { output = o; break; }
                    o->Release();
                }
                a->Release();
            }
            if (output == null) { _state = 2; return; }
            _state = 1;
            while (!_stop)
            {
                if (_paused) { _resume.WaitOne(); continue; }   // the clock is parked (idle app): no vblank wakes at all
                if ((int)output->WaitForVBlank() < 0) { _state = 2; break; }
                Volatile.Write(ref _lastQpc, Stopwatch.GetTimestamp());
                Interlocked.Increment(ref _count);
                _event.Set();
            }
        }
        catch (Exception) { _state = 2; }   // a background thread's exception would end the process: fail over instead
        finally
        {
            if (output != null) output->Release();
            if (factory != null) factory->Release();
            if (_state == 2) _event.Set();   // wake the clock so it notices the failure at once
        }
    }

    /// <summary>Stop waiting on vblanks until <see cref="Resume"/> (the clock parks while the app is idle).</summary>
    public void Pause() => _paused = true;

    /// <summary>Wait on vblanks again.</summary>
    public void Resume()
    {
        if (!_paused) return;
        _paused = false;
        _resume.Set();
    }

    /// <summary>Ask the pump to stop after its current wait. Never blocks (see the class remarks).</summary>
    public void Dispose()
    {
        _stop = true;
        _resume.Set();
    }
}
