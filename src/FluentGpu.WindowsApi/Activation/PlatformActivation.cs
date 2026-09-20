using System;
using FluentGpu.WindowsApi.Notifications;
using TerraFX.Interop.WinRT;
using static TerraFX.Interop.Windows.Windows;
using static TerraFX.Interop.WinRT.WinRT;
// The WinRT enum shares its name with this namespace's ActivationKind, which would win the lookup.
using WinRTActivationKind = TerraFX.Interop.WinRT.ActivationKind;

namespace FluentGpu.WindowsApi.Activation;

/// <summary>
/// The platform's own record of how a PACKAGED process was activated: <c>Windows.ApplicationModel.AppInstance
/// .GetActivatedEventArgs()</c> (UniversalApiContract 6, Windows 10 1803) → <c>IActivatedEventArgs.Kind</c>, plus the
/// <c>IStartupTaskActivatedEventArgs.TaskId</c> when the kind is <c>StartupTask</c>. The WASDK precedent
/// (<c>dev/AppLifecycle/AppInstance.cpp:485-491</c>) without the WASDK runtime: flat call-out WinRT through TerraFX's
/// vtable structs — no CsWinRT, no <c>ComWrappers</c>, no reflection. Cold, once per process start.
/// </summary>
/// <remarks>Fail-soft by construction: any HRESULT failure, a null args object (an unpackaged or non-activated
/// process), or an exception returns false and the caller keeps its command-line classification. The calling thread's
/// apartment is left exactly as found — if it had no WinRT/COM apartment, one is initialized for the call and
/// uninitialized after it.</remarks>
internal static unsafe class PlatformActivation
{
    private const string RuntimeClassAppInstance = "Windows.ApplicationModel.AppInstance";
    private const int CO_E_NOTINITIALIZED = unchecked((int)0x800401F0);

    public static bool TryRead(out int kind, out string taskId)
    {
        kind = 0;
        taskId = string.Empty;
        try
        {
            int hr = TryReadCore(out kind, out taskId);
            if (hr != CO_E_NOTINITIALIZED) return hr >= 0;

            // No apartment on this thread (a console-style Main): join the MTA for this one call, then leave it.
            if (RoInitialize(RO_INIT_TYPE.RO_INIT_MULTITHREADED) < 0) return false;
            try { return TryReadCore(out kind, out taskId) >= 0; }
            finally { RoUninitialize(); }
        }
        catch
        {
            kind = 0;
            taskId = string.Empty;
            return false;
        }
    }

    private static int TryReadCore(out int kind, out string taskId)
    {
        kind = 0;
        taskId = string.Empty;
        IAppInstanceStatics* statics = null;
        IActivatedEventArgs* args = null;
        IStartupTaskActivatedEventArgs* startup = null;
        try
        {
            using var className = new HStringHandle(RuntimeClassAppInstance);
            Guid iidStatics = __uuidof<IAppInstanceStatics>();
            int hr = RoGetActivationFactory(className.Value, &iidStatics, (void**)&statics);
            if (hr < 0 || statics == null) return hr < 0 ? hr : -1;

            hr = statics->GetActivatedEventArgs(&args);
            if (hr < 0 || args == null) return hr < 0 ? hr : -1;

            WinRTActivationKind platformKind;
            hr = args->get_Kind(&platformKind);
            if (hr < 0) return hr;
            kind = (int)platformKind;

            if (kind == ActivationArgs.PlatformStartupTask)
            {
                Guid iidStartup = __uuidof<IStartupTaskActivatedEventArgs>();
                if (args->QueryInterface(&iidStartup, (void**)&startup) >= 0 && startup != null)
                {
                    HSTRING id = default;
                    if (startup->get_TaskId(&id) >= 0)
                    {
                        try { taskId = HStringHandle.ToManaged(id); }
                        finally { WindowsDeleteString(id); }
                    }
                }
            }
            return 0;
        }
        finally
        {
            if (startup != null) startup->Release();
            if (args != null) args->Release();
            if (statics != null) statics->Release();
        }
    }
}
