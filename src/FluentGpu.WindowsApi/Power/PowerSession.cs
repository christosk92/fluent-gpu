using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.WindowsApi.Power;

/// <summary>
/// The Power pillar: two cold, flat-Win32 facilities a desktop media app needs — a scope-bounded
/// <see cref="KeepAwake"/> (so long playback / casting is not interrupted by sleep or display-off) and a
/// <see cref="Subscribe"/> to system <see cref="Suspending"/>/<see cref="Resumed"/> transitions (so the app can flush
/// state / drop the render loop before suspend and re-acquire devices after resume).
/// </summary>
/// <remarks>
/// <para>
/// <b>Surface.</b> <see cref="KeepAwake(bool)"/> returns an <see cref="IDisposable"/> whose lifetime holds the
/// power-availability request; <see cref="Subscribe()"/> returns a subscription whose <c>Dispose</c> unhooks the
/// suspend/resume callback. Both are flat <c>kernel32</c>/<c>user32</c> P/Invoke — no COM, WinRT, or reflection — so
/// they are AOT/trim-clean (<c>IsAotCompatible=true</c>); the suspend/resume callback is delivered to an
/// <c>[UnmanagedCallersOnly]</c> static thunk, the only AOT-correct way to hand the OS a function pointer here.
/// </para>
/// <para>
/// <b>Thread affinity (honest).</b> <c>SetThreadExecutionState</c> sets a <i>per-thread</i> flag: the
/// <c>ES_CONTINUOUS</c> request lives only as long as the <i>calling thread</i> runs, and only the calling thread can
/// clear it. <see cref="KeepAwake"/> therefore records the OS request on whatever thread calls it and, on
/// <c>Dispose</c>, restores <c>ES_CONTINUOUS</c> <i>from the disposing thread</i> — so <b>create and dispose the
/// handle on the same, stable thread</b> (e.g. the UI thread). A handle disposed on a different thread clears that
/// other thread's flag, not the one that set the request, and leaks the original request until its thread exits. We do
/// not marshal the call onto a captured thread (that would need a host dispatcher this pillar does not own); the
/// contract is documented instead of hidden (Win32 <c>SetThreadExecutionState</c> docs:
/// <see href="https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-setthreadexecutionstate"/>).
/// </para>
/// <para>
/// <b>Suspend/resume callbacks arrive on a system thread.</b> <c>RegisterSuspendResumeNotification</c> with
/// <c>DEVICE_NOTIFY_CALLBACK</c> invokes the callback on an arbitrary OS-owned thread (a power-broadcast worker), not
/// the registering thread, and may be re-entrant across rapid suspend/resume. The static thunk therefore does the
/// minimum — translate the <c>PBT_*</c> code and raise the matching event — and must not block; subscribers should
/// hop to their own thread before doing real work. (Win32
/// <see href="https://learn.microsoft.com/en-us/windows/win32/api/powerbase/nf-powerbase-powerregistersuspendresumenotification"/>;
/// callback signature <c>DEVICE_NOTIFY_CALLBACK_ROUTINE</c>,
/// <see href="https://learn.microsoft.com/en-us/windows/win32/api/winuser/nc-winuser-device_notify_callback_routine"/>.)
/// </para>
/// <para>
/// <b>Why not the WinRT power APIs.</b> <c>Windows.System.Power.PowerManager</c> / <c>SystemMediaTransportControls</c>
/// battery surface and the UWP <c>ExtendedExecutionSession</c> are activation-heavy and/or packaged-identity-sensitive;
/// the flat Win32 primitives here are identity-free, work packaged and unpackaged identically, and are the smallest
/// dependency. Declarations come from <c>TerraFX.Interop.Windows</c> (<c>SetThreadExecutionState</c>,
/// <c>RegisterSuspendResumeNotification</c>, <c>UnregisterSuspendResumeNotification</c>, <c>HPOWERNOTIFY</c>); only the
/// plain integer <c>#define</c>s and the <c>DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS</c> struct — which TerraFX does not
/// project — are restated locally from the Windows SDK headers (<c>winbase.h</c>/<c>winuser.h</c>, 10.0.26100.0),
/// the same house pattern as <see cref="FluentGpu.WindowsApi.Credentials.CredentialStore"/>.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows8.0")]   // RegisterSuspendResumeNotification (user-mode) shipped in Windows 8.
public static unsafe class PowerSession
{
    // ── winbase.h EXECUTION_STATE flags (not projected by TerraFX; restated from the SDK header). ─────────────────────
    private const uint ES_CONTINUOUS = 0x80000000;        // request stays in effect until the next call.
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;   // forestall system sleep.
    private const uint ES_DISPLAY_REQUIRED = 0x00000002;  // also forestall display-off.

    // ── winuser.h device-notification flags (not projected by TerraFX). ──────────────────────────────────────────────
    private const uint DEVICE_NOTIFY_CALLBACK = 0x00000002; // hRecipient is a DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS*.

    // ── winuser.h power-broadcast event codes delivered to the callback's wParam-equivalent (uType). ─────────────────
    private const uint PBT_APMSUSPEND = 0x0004;            // system is suspending.
    private const uint PBT_APMRESUMESUSPEND = 0x0007;      // resume from a user-initiated suspend.
    private const uint PBT_APMRESUMEAUTOMATIC = 0x0012;    // resume from any suspend (always delivered on resume).

    /// <summary>
    /// <c>DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS</c> (winuser.h) — the callback + context pair passed (by pointer, as the
    /// <c>HANDLE hRecipient</c>) to <c>RegisterSuspendResumeNotification</c> when the flag is
    /// <c>DEVICE_NOTIFY_CALLBACK</c>. Not projected by TerraFX, so it is restated here. The struct must outlive the
    /// registration; <see cref="PowerSubscription"/> pins it for the registration's lifetime.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS
    {
        /// <summary><c>PDEVICE_NOTIFY_CALLBACK_ROUTINE</c> — our <c>[UnmanagedCallersOnly]</c> thunk.</summary>
        public delegate* unmanaged[Stdcall]<void*, uint, void*, uint> Callback;
        /// <summary>Caller context handed back to the callback; unused (we key off the static event), kept null.</summary>
        public void* Context;
    }

    /// <summary>
    /// Request that the system (and optionally the display) stay awake for as long as the returned handle is alive.
    /// Issues <c>SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED [| ES_DISPLAY_REQUIRED])</c>; disposing the
    /// handle issues <c>SetThreadExecutionState(ES_CONTINUOUS)</c>, dropping the request.
    /// </summary>
    /// <param name="keepDisplayOn"><see langword="true"/> to also keep the display on (adds <c>ES_DISPLAY_REQUIRED</c>);
    /// <see langword="false"/> (default) keeps only the system awake while letting the screen dim/turn off — the right
    /// choice for background audio.</param>
    /// <returns>A handle whose <c>Dispose</c> releases the keep-awake request. <b>Dispose it on the same thread that
    /// created it</b> (the request is a per-thread flag — see the type remarks).</returns>
    /// <remarks>Thread affinity: the request is bound to the calling thread; call from a stable thread (e.g. the UI
    /// thread) and dispose from that same thread. Idempotent disposal (a second <c>Dispose</c> is a no-op).</remarks>
    public static IDisposable KeepAwake(bool keepDisplayOn = false)
    {
        uint flags = ES_CONTINUOUS | ES_SYSTEM_REQUIRED | (keepDisplayOn ? ES_DISPLAY_REQUIRED : 0u);
        // A zero return means the call failed (e.g. an invalid flag combination) — surface it rather than silently
        // believing the machine will stay awake.
        if (SetThreadExecutionState(flags) == 0)
            throw new InvalidOperationException(
                $"SetThreadExecutionState(0x{flags:X8}) failed to set the keep-awake request.");
        return new KeepAwakeHandle();
    }

    /// <summary>Raised when the system is about to suspend (<c>PBT_APMSUSPEND</c>). Delivered on an OS power-broadcast
    /// thread — do not block; hop to your own thread for real work. Only fires while at least one
    /// <see cref="PowerSubscription"/> from <see cref="Subscribe"/> is alive.</summary>
    public static event Action? Suspending;

    /// <summary>Raised when the system has resumed (<c>PBT_APMRESUMEAUTOMATIC</c>, the code the OS guarantees on every
    /// resume). Delivered on an OS power-broadcast thread — do not block. Only fires while at least one
    /// <see cref="PowerSubscription"/> from <see cref="Subscribe"/> is alive.</summary>
    public static event Action? Resumed;

    /// <summary>
    /// Begin receiving system suspend/resume notifications, raising <see cref="Suspending"/>/<see cref="Resumed"/>.
    /// Returns a subscription whose <c>Dispose</c> unregisters the OS callback. Multiple subscriptions are independent
    /// (each holds its own OS registration); the static events fan out to all current subscribers.
    /// </summary>
    /// <returns>A subscription object; dispose it to stop notifications. May be disposed from any thread.</returns>
    /// <remarks>Callbacks arrive on an arbitrary system thread (see the type remarks). Subscribe BEFORE relying on the
    /// events; a handler attached to <see cref="Suspending"/>/<see cref="Resumed"/> after registration is also invoked
    /// (the events are process-wide).</remarks>
    public static IDisposable Subscribe() => new PowerSubscription();

    /// <summary>Raise the matching static event for a translated power-broadcast code. Invoked (only) by the
    /// <c>[UnmanagedCallersOnly]</c> thunk. Swallows handler exceptions so none crosses back into the OS callback.</summary>
    private static void Raise(uint pbtEvent)
    {
        try
        {
            switch (pbtEvent)
            {
                case PBT_APMSUSPEND:
                    Suspending?.Invoke();
                    break;
                case PBT_APMRESUMEAUTOMATIC:
                    Resumed?.Invoke();
                    break;
                // PBT_APMRESUMESUSPEND is the paired "resume from a user-initiated suspend"; PBT_APMRESUMEAUTOMATIC is
                // always delivered on resume, so Resumed keys off AUTOMATIC alone to avoid a double-raise. Other codes
                // (e.g. PBT_POWERSETTINGCHANGE) are not requested here and are ignored.
                default:
                    break;
            }
        }
        catch
        {
            // A managed handler exception must not propagate across the unmanaged callback boundary.
        }
    }

    /// <summary>
    /// The <c>DEVICE_NOTIFY_CALLBACK_ROUTINE</c> the OS invokes on suspend/resume. The signature is
    /// <c>ULONG Routine(PVOID Context, ULONG Type, PVOID Setting)</c>; <paramref name="type"/> carries the
    /// <c>PBT_*</c> code. Returns S_OK (0). <c>[UnmanagedCallersOnly]</c> so NativeAOT exposes a real function pointer
    /// (no delegate, no marshalling, no GC handle to a managed delegate) — the AOT-correct callback registration.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static uint SuspendResumeThunk(void* context, uint type, void* setting)
    {
        Raise(type);
        return 0; // S_OK — the routine "handled" the broadcast.
    }

    /// <summary>The scope object returned by <see cref="KeepAwake"/>; restores <c>ES_CONTINUOUS</c> exactly once.</summary>
    private sealed class KeepAwakeHandle : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            // Idempotent: only the first Dispose clears the request. Clearing from a different thread than KeepAwake
            // was called on clears THAT thread's flag (documented caveat on KeepAwake) — still single-shot here.
            if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            SetThreadExecutionState(ES_CONTINUOUS);
        }
    }

    /// <summary>
    /// An active suspend/resume registration. Holds a pinned <see cref="DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS"/> (so the
    /// callback pointer the OS retains stays valid) and the <c>HPOWERNOTIFY</c> cookie; <c>Dispose</c> calls
    /// <c>UnregisterSuspendResumeNotification</c> and releases the pin.
    /// </summary>
    private sealed class PowerSubscription : IDisposable
    {
        private GCHandle _paramsPin;     // pins the subscribe-params struct for the registration's lifetime
        private HPOWERNOTIFY _cookie;
        private int _disposed;

        public PowerSubscription()
        {
            // Box the subscribe-params struct on the heap and pin it for the registration's lifetime. The OS is
            // documented to copy the callback+context out of this struct during the register call (like RegisterClass
            // with WNDCLASS), so the pin is defensive rather than strictly required — but it is a cold, one-per-
            // subscription cost and removes any ambiguity about the struct's address staying valid. The callback it
            // carries is the static [UnmanagedCallersOnly] thunk (a fixed native entry point; nothing to pin for it).
            var p = new DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS
            {
                Callback = &SuspendResumeThunk,
                Context = null,
            };
            _paramsPin = GCHandle.Alloc(p, GCHandleType.Pinned);

            HPOWERNOTIFY cookie = RegisterSuspendResumeNotification(
                (HANDLE)_paramsPin.AddrOfPinnedObject(), DEVICE_NOTIFY_CALLBACK);

            if (cookie == default)
            {
                // TerraFX declares this export without SetLastError, so GetLastPInvokeError() is not reliable here; the
                // NULL return is the documented failure signal. Surface that without claiming a (possibly stale) errno.
                _paramsPin.Free();
                throw new InvalidOperationException(
                    "RegisterSuspendResumeNotification(DEVICE_NOTIFY_CALLBACK) returned NULL (registration failed).");
            }
            _cookie = cookie;
        }

        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            if (_cookie != default)
            {
                UnregisterSuspendResumeNotification(_cookie);
                _cookie = default;
            }
            if (_paramsPin.IsAllocated)
                _paramsPin.Free();
        }
    }
}
