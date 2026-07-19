using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using TerraFX.Interop.WinRT;
using static TerraFX.Interop.Windows.Windows;   // __uuidof<T>

namespace FluentGpu.WindowsApi.Media.PlayReady;

/// <summary>
/// The implemented <c>IComponentLoadFailedEventHandler</c> (IID <c>95da643c-6db9-424b-86ca-091af432081c</c>, copied from
/// <c>windows.media.protection.h</c>, IUnknown-based) passed to <c>IMediaProtectionManager.add_ComponentLoadFailed</c>.
/// It fires when the protected pipeline cannot load a required component; the handler completes the request
/// (<c>Complete(false)</c>) and signals the harness. Same source-generated-COM wiring as
/// <see cref="ServiceRequestedHandler"/>.
/// </summary>
[GeneratedComInterface]
[Guid("95da643c-6db9-424b-86ca-091af432081c")]
internal partial interface IComponentLoadFailedEventHandlerNative
{
    /// <summary><paramref name="sender"/> is the <c>IMediaProtectionManager*</c>; <paramref name="args"/> is the
    /// <c>IComponentLoadFailedEventArgs*</c>. Returns S_OK.</summary>
    [PreserveSig]
    unsafe int Invoke(void* sender, void* args);
}

/// <summary>Implementation of <see cref="IComponentLoadFailedEventHandlerNative"/>: reads the completion off the args,
/// completes it <see langword="false"/>, and raises the harness callback.</summary>
[GeneratedComClass]
[SupportedOSPlatform("windows10.0.10240.0")]
internal sealed unsafe partial class ComponentLoadFailedHandler : IComponentLoadFailedEventHandlerNative
{
    private const int S_OK = 0;
    private readonly Action _onFailed;

    public ComponentLoadFailedHandler(Action onFailed) => _onFailed = onFailed;

    /// <inheritdoc/>
    public int Invoke(void* sender, void* args)
    {
        try
        {
            try { _onFailed(); }
            catch { /* harness sink must never fail the OS callback */ }

            if (args == null)
                return S_OK;

            var argsInsp = (IInspectable*)args;
            IComponentLoadFailedEventArgs* pArgs = null;
            Guid iid = __uuidof<IComponentLoadFailedEventArgs>();
            if (argsInsp->QueryInterface(&iid, (void**)&pArgs) < 0 || pArgs == null)
                return S_OK;
            try
            {
                IMediaProtectionServiceCompletion* completion = null;
                if (pArgs->get_Completion(&completion) >= 0 && completion != null)
                {
                    completion->Complete(0);   // component could not load — fail the request
                    completion->Release();
                }
                return S_OK;
            }
            finally
            {
                pArgs->Release();
            }
        }
        catch
        {
            return S_OK;
        }
    }
}
