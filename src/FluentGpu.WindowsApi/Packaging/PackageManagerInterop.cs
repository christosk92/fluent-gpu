using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using TerraFX.Interop.WinRT;

namespace FluentGpu.WindowsApi.Packaging;

/// <summary>
/// Hand-rolled call-OUT vtables over <c>Windows.Management.Deployment</c> (and the two generic
/// <c>Windows.Foundation</c> shapes that sit on its edges).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why by hand.</b> <c>TerraFX.Interop.Windows</c> projects <c>Windows.ApplicationModel</c> and
/// <c>Windows.Foundation</c> but NOT <c>Windows.Management.Deployment</c> — there is no <c>IPackageManager</c>,
/// <c>IDeploymentResult</c>, or <c>DeploymentProgress</c> to bind against. These follow the same call-OUT pattern as
/// <c>Notifications/ToastInterop.cs</c>'s <c>IStringMap</c>: a one-field <c>readonly struct</c> over the interface's
/// <c>lpVtbl</c>, with each method typed as a <c>delegate* unmanaged</c> at a pinned slot index. No
/// <c>ComWrappers</c>, no reflection, no generated marshalling — AOT-clean and allocation-free.
/// </para>
/// <para>
/// <b>Slot discipline.</b> A WinRT interface deriving from <c>IInspectable</c> occupies slots 0-5
/// (<c>QueryInterface</c>, <c>AddRef</c>, <c>Release</c>, <c>GetIids</c>, <c>GetRuntimeClassName</c>,
/// <c>GetTrustLevel</c>); its own methods follow in <b>declaration order</b> from slot 6. Interfaces are NOT
/// inherited across versions — <c>IPackageManager3</c> does not continue <c>IPackageManager</c>'s numbering, it
/// restarts at 6. Every slot below was read off the C ABI vtable structs in the Windows SDK 10.0.26100 header
/// <c>windows.management.deployment.h</c> / <c>windows.applicationmodel.h</c>, and the header line number is quoted on
/// each member. Getting a slot wrong does not fail — it calls a different method with the wrong arguments, which is
/// how the <c>IStringMap</c> bug silently deleted map keys and the <c>IScheduledToastView</c> bug killed the process.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows10.0.19041")]
internal static class DeploymentIids
{
    /// <summary><c>IPackageManager</c> — <c>windows.management.deployment.idl:581</c> / <c>.h:11066</c>.</summary>
    internal static readonly Guid IPackageManager = new("9a7d4b65-5e8f-4fc7-a2e5-7f6925cb8b53");

    /// <summary><c>IPackageManager3</c> (package volumes) — <c>windows.management.deployment.idl:695</c>.</summary>
    internal static readonly Guid IPackageManager3 = new("daad9948-36f1-41a7-9188-bc263e0dcb72");

    /// <summary><c>IPackageManager6</c> (App Installer file APIs) — <c>windows.management.deployment.idl:736</c>.</summary>
    internal static readonly Guid IPackageManager6 = new("0847e909-53cd-4e4f-832e-57d180f6e447");

    /// <summary><c>IDeploymentResult</c> — <c>windows.management.deployment.idl:581</c>.</summary>
    internal static readonly Guid IDeploymentResult = new("2563b9ae-b77d-4c1f-8a7b-20e6ad515ef3");

    /// <summary><c>IDeploymentResult2</c> (<c>IsRegistered</c>) — <c>windows.management.deployment.idl:591</c>.</summary>
    internal static readonly Guid IDeploymentResult2 = new("fc0e715c-5a01-4bd7-bcf1-381c8c82e04a");

    /// <summary><c>IAsyncOperationWithProgress&lt;DeploymentResult, DeploymentProgress&gt;</c> — the parameterized IID,
    /// <c>windows.management.deployment.h:1124</c>. Only needed if the operation pointer is ever re-acquired by QI;
    /// <c>AddPackageByAppInstallerFileAsync</c> already hands it back typed.</summary>
    internal static readonly Guid IAsyncOperationWithProgressDeployment = new("5a97aab7-b6ea-55ac-a5dc-d5b164d94e94");

    /// <summary><c>AsyncOperationProgressHandler&lt;DeploymentResult, DeploymentProgress&gt;</c> — the delegate IID our
    /// progress sink answers to, <c>windows.management.deployment.h:1153</c>.</summary>
    internal static readonly Guid AsyncOperationProgressHandlerDeployment = new("f1b926d1-1796-597a-9bea-6c6449d03eef");

    /// <summary><c>IUnknown</c>.</summary>
    internal static readonly Guid IUnknown = new("00000000-0000-0000-c000-000000000046");

    /// <summary><c>IAgileObject</c> — the marker our progress sink also answers to, so the deployment engine never
    /// tries to marshal the callback across apartments.</summary>
    internal static readonly Guid IAgileObject = new("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90");

    /// <summary>Activatable via <c>RoActivateInstance</c> (default constructor).</summary>
    internal const string RuntimeClassPackageManager = "Windows.Management.Deployment.PackageManager";

    /// <summary>Statics-only class: <c>RoGetActivationFactory</c> + <c>IPackageStatics.get_Current</c>.</summary>
    internal const string RuntimeClassPackage = "Windows.ApplicationModel.Package";

    /// <summary>Activation factory for <c>Windows.Foundation.Uri</c> (<c>IUriRuntimeClassFactory.CreateUri</c>).</summary>
    internal const string RuntimeClassUri = "Windows.Foundation.Uri";

    /// <summary>
    /// <c>AddPackageByAppInstallerOptions.ForceTargetAppShutdown</c> = <c>0x40</c> — verified at
    /// <c>windows.management.deployment.h:9166</c> (the enum also carries <c>None</c>=0,
    /// <c>InstallAllResources</c>=0x20, <c>RequiredContentGroupOnly</c>=0x100, <c>LimitToExistingPackages</c>=0x200).
    /// This is what makes Windows terminate the running app so the new bits can register immediately; paired with
    /// <c>RegisterApplicationRestart</c> it produces the "closes and comes back updated" experience.
    /// </summary>
    internal const uint AddPackageByAppInstallerOptions_ForceTargetAppShutdown = 0x40;
}

/// <summary>
/// <c>Windows.Management.Deployment.DeploymentProgress</c> — <c>windows.management.deployment.h:9419-9423</c>.
/// Blittable 8-byte POD passed <b>by value</b> to the progress delegate.
/// </summary>
/// <remarks><c>state</c> is <c>DeploymentProgressState</c> (<c>Queued</c>=0, <c>Processing</c>=1 — header line 9216);
/// <c>percentage</c> is 0..100 and is documented as coarse.</remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct DeploymentProgress
{
    /// <summary><c>DeploymentProgressState</c>: 0 = Queued, 1 = Processing.</summary>
    public uint State;

    /// <summary>0..100.</summary>
    public uint Percentage;
}

/// <summary>
/// Call-OUT vtable over <c>Windows.Management.Deployment.IPackageManager</c>
/// (<c>windows.management.deployment.h:10911</c>).
/// </summary>
/// <remarks>
/// Full slot map, IInspectable 0-5 then declaration order: 6 <c>AddPackageAsync</c>, 7 <c>UpdatePackageAsync</c>,
/// 8 <c>RemovePackageAsync</c>, 9 <c>StagePackageAsync</c>, 10 <c>RegisterPackageAsync</c>, 11 <c>FindPackages</c>,
/// 12 <c>FindPackagesByUserSecurityId</c>, 13 <c>FindPackagesByNamePublisher</c>,
/// 14 <c>FindPackagesByUserSecurityIdNamePublisher</c>, 15 <c>FindUsers</c>, 16 <c>SetPackageState</c>,
/// 17 <c>FindPackageByPackageFullName</c>, 18 <c>CleanupPackageForUserAsync</c>,
/// 19 <c>FindPackagesByPackageFamilyName</c>, 20 <c>FindPackagesByUserSecurityIdPackageFamilyName</c>,
/// <b>21 <c>FindPackageByUserSecurityIdPackageFullName</c></b>.
/// </remarks>
[SupportedOSPlatform("windows10.0.19041")]
internal readonly unsafe struct IPackageManagerVtbl
{
#pragma warning disable CS0649
    private readonly void** _lpVtbl;
#pragma warning restore CS0649

    /// <summary><c>HRESULT FindPackageByUserSecurityIdPackageFullName(HSTRING userSecurityId, HSTRING packageFullName,
    /// IPackage** packageInformation)</c> — vtable slot <b>21</b> (header line 10983).
    /// <para>An empty <paramref name="userSecurityId"/> means "the current user". This is the documented way to reach an
    /// <c>IPackage6</c> that <c>CheckUpdateAvailabilityAsync</c> will actually accept — calling it on the package from
    /// <c>Package.Current</c> fails with access denied.</para>
    /// <para>Returns <c>S_OK</c> with a null <c>packageInformation</c> when nothing matches.</para></summary>
    internal int FindPackageByUserSecurityIdPackageFullName(HSTRING userSecurityId, HSTRING packageFullName, void** package)
        => ((delegate* unmanaged<IPackageManagerVtbl*, HSTRING, HSTRING, void**, int>)_lpVtbl[21])(
            Vtbl.Self(in this), userSecurityId, packageFullName, package);

    /// <summary><c>IUnknown::Release</c> (slot 2).</summary>
    internal uint Release()
        => ((delegate* unmanaged<IPackageManagerVtbl*, uint>)_lpVtbl[2])(Vtbl.Self(in this));
}

/// <summary>
/// Call-OUT vtable over <c>Windows.Management.Deployment.IPackageManager3</c>
/// (<c>windows.management.deployment.h:11453</c>).
/// </summary>
/// <remarks>
/// Slot map from 6, declaration order: 6 <c>AddPackageVolumeAsync</c>, 7 <c>AddPackageToVolumeAsync</c>,
/// 8 <c>ClearPackageStatus</c>, 9 <c>RegisterPackageWithAppDataVolumeAsync</c>, 10 <c>FindPackageVolumeByName</c>,
/// 11 <c>FindPackageVolumes</c>, <b>12 <c>GetDefaultPackageVolume</c></b>, 13 <c>MovePackageToVolumeAsync</c>,
/// 14 <c>RemovePackageVolumeAsync</c>, 15 <c>SetDefaultPackageVolume</c>, 16 <c>SetPackageStatus</c>,
/// 17 <c>SetPackageVolumeOfflineAsync</c>, 18 <c>SetPackageVolumeOnlineAsync</c>, 19 <c>StagePackageToVolumeAsync</c>,
/// 20 <c>StageUserDataWithOptionsAsync</c>.
/// </remarks>
[SupportedOSPlatform("windows10.0.19041")]
internal readonly unsafe struct IPackageManager3Vtbl
{
#pragma warning disable CS0649
    private readonly void** _lpVtbl;
#pragma warning restore CS0649

    /// <summary><c>HRESULT GetDefaultPackageVolume(IPackageVolume** volume)</c> — vtable slot <b>12</b>
    /// (header line 11492). The volume is optional for <c>AddPackageByAppInstallerFileAsync</c>; a null pointer means
    /// "the system default", which is the same volume this returns, so a failure here is recoverable.</summary>
    internal int GetDefaultPackageVolume(void** volume)
        => ((delegate* unmanaged<IPackageManager3Vtbl*, void**, int>)_lpVtbl[12])(Vtbl.Self(in this), volume);

    /// <summary><c>IUnknown::Release</c> (slot 2).</summary>
    internal uint Release()
        => ((delegate* unmanaged<IPackageManager3Vtbl*, uint>)_lpVtbl[2])(Vtbl.Self(in this));
}

/// <summary>
/// Call-OUT vtable over <c>Windows.Management.Deployment.IPackageManager6</c>
/// (<c>windows.management.deployment.h:11786</c>).
/// </summary>
/// <remarks>
/// Slot map from 6, declaration order: 6 <c>ProvisionPackageForAllUsersAsync</c>,
/// <b>7 <c>AddPackageByAppInstallerFileAsync</c></b>, 8 <c>RequestAddPackageByAppInstallerFileAsync</c>,
/// 9 <c>AddPackageToVolumeAndRelatedSetAsync</c>, 10 <c>StagePackageToVolumeAndRelatedSetAsync</c>,
/// 11 <c>RequestAddPackageAsync</c>. (The design note in the plan guessed slot 10 by assuming <c>AddPackageByUriAsync</c>
/// and <c>StagePackageByUriAsync</c> lived here — they are on <c>IPackageManager9</c>, so slot 7 is correct.)
/// </remarks>
[SupportedOSPlatform("windows10.0.19041")]
internal readonly unsafe struct IPackageManager6Vtbl
{
#pragma warning disable CS0649
    private readonly void** _lpVtbl;
#pragma warning restore CS0649

    /// <summary><c>HRESULT AddPackageByAppInstallerFileAsync(IUriRuntimeClass* appInstallerFileUri,
    /// AddPackageByAppInstallerOptions options, IPackageVolume* targetVolume,
    /// IAsyncOperationWithProgress&lt;DeploymentResult, DeploymentProgress&gt;** operation)</c> — vtable slot <b>7</b>
    /// (header lines 11805-11809). <paramref name="targetVolume"/> may be null.</summary>
    internal int AddPackageByAppInstallerFileAsync(void* appInstallerFileUri, uint options, void* targetVolume, void** operation)
        => ((delegate* unmanaged<IPackageManager6Vtbl*, void*, uint, void*, void**, int>)_lpVtbl[7])(
            Vtbl.Self(in this), appInstallerFileUri, options, targetVolume, operation);

    /// <summary><c>IUnknown::Release</c> (slot 2).</summary>
    internal uint Release()
        => ((delegate* unmanaged<IPackageManager6Vtbl*, uint>)_lpVtbl[2])(Vtbl.Self(in this));
}

/// <summary>
/// Call-OUT vtable over <c>IAsyncOperationWithProgress&lt;DeploymentResult, DeploymentProgress&gt;</c>
/// (<c>windows.management.deployment.h:6659</c>).
/// </summary>
/// <remarks>
/// Slot map from 6: <b>6 <c>put_Progress</c></b>, 7 <c>get_Progress</c>, 8 <c>put_Completed</c>,
/// 9 <c>get_Completed</c>, <b>10 <c>GetResults</c></b>.
/// <para><b>Note on <c>get_Progress</c> (slot 7):</b> it returns the registered <i>handler</i>
/// (<c>IAsyncOperationProgressHandler**</c>), NOT a <c>DeploymentProgress</c> value — there is no pollable progress
/// property anywhere on this interface. Percentage is therefore only obtainable by installing a callback with
/// <c>put_Progress</c>, which is what <see cref="DeploymentProgressSink"/> exists for.</para>
/// </remarks>
[SupportedOSPlatform("windows10.0.19041")]
internal readonly unsafe struct IAsyncOpDeploymentVtbl
{
#pragma warning disable CS0649
    private readonly void** _lpVtbl;
#pragma warning restore CS0649

    /// <summary><c>HRESULT put_Progress(AsyncOperationProgressHandler&lt;DeploymentResult, DeploymentProgress&gt;* handler)</c>
    /// — vtable slot <b>6</b> (header line 6675). The operation AddRefs the handler and Releases it on completion.</summary>
    internal int put_Progress(void* handler)
        => ((delegate* unmanaged<IAsyncOpDeploymentVtbl*, void*, int>)_lpVtbl[6])(Vtbl.Self(in this), handler);

    /// <summary><c>HRESULT GetResults(IDeploymentResult** result)</c> — vtable slot <b>10</b> (header line 6683).
    /// Legal only once the operation has reached <c>AsyncStatus.Completed</c>.</summary>
    internal int GetResults(void** result)
        => ((delegate* unmanaged<IAsyncOpDeploymentVtbl*, void**, int>)_lpVtbl[10])(Vtbl.Self(in this), result);

    /// <summary><c>IUnknown::Release</c> (slot 2).</summary>
    internal uint Release()
        => ((delegate* unmanaged<IAsyncOpDeploymentVtbl*, uint>)_lpVtbl[2])(Vtbl.Self(in this));
}

/// <summary>
/// Call-OUT vtable over <c>Windows.Management.Deployment.IDeploymentResult</c>
/// (<c>windows.management.deployment.h:10531</c>). Slot map from 6: <b>6 <c>get_ErrorText</c></b>,
/// 7 <c>get_ActivityId</c>, <b>8 <c>get_ExtendedErrorCode</c></b>.
/// </summary>
[SupportedOSPlatform("windows10.0.19041")]
internal readonly unsafe struct IDeploymentResultVtbl
{
#pragma warning disable CS0649
    private readonly void** _lpVtbl;
#pragma warning restore CS0649

    /// <summary><c>HRESULT get_ErrorText(HSTRING* value)</c> — vtable slot <b>6</b> (header line 10547). The returned
    /// <c>HSTRING</c> is owned by the caller and must be deleted with <c>WindowsDeleteString</c>.</summary>
    internal int get_ErrorText(HSTRING* value)
        => ((delegate* unmanaged<IDeploymentResultVtbl*, HSTRING*, int>)_lpVtbl[6])(Vtbl.Self(in this), value);

    /// <summary><c>HRESULT get_ExtendedErrorCode(HRESULT* value)</c> — vtable slot <b>8</b> (header line 10551).
    /// <para>Slot 7 is <c>get_ActivityId(GUID*)</c>; calling it here would write 16 bytes through a 4-byte pointer.</para></summary>
    internal int get_ExtendedErrorCode(int* value)
        => ((delegate* unmanaged<IDeploymentResultVtbl*, int*, int>)_lpVtbl[8])(Vtbl.Self(in this), value);

    /// <summary><c>IUnknown::QueryInterface</c> (slot 0) — used to reach <c>IDeploymentResult2</c>.</summary>
    internal int QueryInterface(Guid* iid, void** ppv)
        => ((delegate* unmanaged<IDeploymentResultVtbl*, Guid*, void**, int>)_lpVtbl[0])(Vtbl.Self(in this), iid, ppv);

    /// <summary><c>IUnknown::Release</c> (slot 2).</summary>
    internal uint Release()
        => ((delegate* unmanaged<IDeploymentResultVtbl*, uint>)_lpVtbl[2])(Vtbl.Self(in this));
}

/// <summary>
/// Call-OUT vtable over <c>Windows.Management.Deployment.IDeploymentResult2</c>
/// (<c>windows.management.deployment.h:10610</c>). Slot map from 6: <b>6 <c>get_IsRegistered</c></b> (the only method).
/// </summary>
[SupportedOSPlatform("windows10.0.19041")]
internal readonly unsafe struct IDeploymentResult2Vtbl
{
#pragma warning disable CS0649
    private readonly void** _lpVtbl;
#pragma warning restore CS0649

    /// <summary><c>HRESULT get_IsRegistered(boolean* value)</c> — vtable slot <b>6</b> (header line 10626). WinRT
    /// <c>boolean</c> is a single byte.</summary>
    internal int get_IsRegistered(byte* value)
        => ((delegate* unmanaged<IDeploymentResult2Vtbl*, byte*, int>)_lpVtbl[6])(Vtbl.Self(in this), value);

    /// <summary><c>IUnknown::Release</c> (slot 2).</summary>
    internal uint Release()
        => ((delegate* unmanaged<IDeploymentResult2Vtbl*, uint>)_lpVtbl[2])(Vtbl.Self(in this));
}

/// <summary>
/// Call-OUT vtable over the two <c>Windows.Foundation</c> generic shapes TerraFX projects only as open generics
/// (<c>IAsyncOperation&lt;T&gt;</c>, <c>IReference&lt;T&gt;</c>) plus <c>IPackage6</c>'s generic-returning method.
/// Hand-rolling them keeps the call sites free of generic pointer gymnastics.
/// </summary>
/// <remarks>
/// <c>IAsyncOperation&lt;T&gt;</c> slot map from 6 (<c>windows.applicationmodel.h:7560</c>): 6 <c>put_Completed</c>,
/// 7 <c>get_Completed</c>, <b>8 <c>GetResults</c></b>.
/// <c>IReference&lt;T&gt;</c> slot map from 6 (<c>windows.applicationmodel.activation.h:7144</c>):
/// <b>6 <c>get_Value</c></b>.
/// <c>IPackage6</c> slot map from 6 (<c>windows.applicationmodel.h:13170</c>): 6 <c>GetAppInstallerInfo</c>,
/// <b>7 <c>CheckUpdateAvailabilityAsync</c></b>.
/// <c>IPackageUpdateAvailabilityResult</c> slot map from 6: <b>6 <c>get_Availability</c></b>, 7 <c>get_ExtendedError</c>.
/// </remarks>
[SupportedOSPlatform("windows10.0.19041")]
internal static unsafe class Vtbl
{
    /// <summary>Recover the <c>this</c> pointer of a by-<c>in</c> vtable struct without copying it (the
    /// <c>IStringMap</c> idiom).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static T* Self<T>(in T self) where T : unmanaged
        => (T*)Unsafe.AsPointer(ref Unsafe.AsRef(in self));

    /// <summary><c>HRESULT IPackage6::CheckUpdateAvailabilityAsync(IAsyncOperation&lt;PackageUpdateAvailabilityResult&gt;** operation)</c>
    /// — vtable slot <b>7</b> (<c>windows.applicationmodel.h:13188</c>). Called through <c>void*</c> so the
    /// parameterized generic never has to be named in C#.</summary>
    internal static int CheckUpdateAvailabilityAsync(void* package6, void** operation)
        => ((delegate* unmanaged<void*, void**, int>)(*(void***)package6)[7])(package6, operation);

    /// <summary><c>HRESULT IAsyncOperation&lt;T&gt;::GetResults(T* result)</c> — vtable slot <b>8</b>.</summary>
    internal static int AsyncOperationGetResults(void* operation, void** result)
        => ((delegate* unmanaged<void*, void**, int>)(*(void***)operation)[8])(operation, result);

    /// <summary><c>HRESULT IPackageUpdateAvailabilityResult::get_Availability(PackageUpdateAvailability* value)</c> —
    /// vtable slot <b>6</b>. The WinRT enum ordinals match <see cref="PackageUpdateAvailability"/> exactly.</summary>
    internal static int GetAvailability(void* result, int* value)
        => ((delegate* unmanaged<void*, int*, int>)(*(void***)result)[6])(result, value);

    /// <summary><c>HRESULT IReference&lt;DateTime&gt;::get_Value(DateTime* value)</c> — vtable slot <b>6</b>. WinRT
    /// <c>DateTime</c> is a bare <c>INT64 UniversalTime</c> (100 ns ticks since 1601-01-01 UTC).</summary>
    internal static int ReferenceGetValue(void* reference, long* value)
        => ((delegate* unmanaged<void*, long*, int>)(*(void***)reference)[6])(reference, value);

    /// <summary>
    /// <c>HRESULT IAppInstallerInfo2::get_PausedUntil(IReference&lt;DateTime&gt;** value)</c> — vtable slot <b>15</b>
    /// (<c>windows.applicationmodel.h:11617</c>). Reached by hand because the out-parameter is a parameterized generic.
    /// <para>Slot map from 6: 6 <c>get_OnLaunch</c>, 7 <c>get_HoursBetweenUpdateChecks</c>, 8 <c>get_ShowPrompt</c>,
    /// 9 <c>get_UpdateBlocksActivation</c>, 10 <c>get_AutomaticBackgroundTask</c>, 11 <c>get_ForceUpdateFromAnyVersion</c>,
    /// 12 <c>get_IsAutoRepairEnabled</c>, 13 <c>get_Version</c>, 14 <c>get_LastChecked</c>,
    /// <b>15 <c>get_PausedUntil</c></b>, 16 <c>get_UpdateUris</c>, 17 <c>get_RepairUris</c>,
    /// 18 <c>get_DependencyPackageUris</c>, 19 <c>get_OptionalPackageUris</c>, 20 <c>get_PolicySource</c>.</para>
    /// <para>A null out-pointer with <c>S_OK</c> means "not paused" (the property is a nullable <c>DateTime</c>).</para>
    /// </summary>
    internal static int AppInstallerInfoGetPausedUntil(void* appInstallerInfo2, void** reference)
        => ((delegate* unmanaged<void*, void**, int>)(*(void***)appInstallerInfo2)[15])(appInstallerInfo2, reference);

    /// <summary><c>IUnknown::Release</c> through an untyped pointer (slot 2). Null-tolerant.</summary>
    internal static void Release(void* unknown)
    {
        if (unknown != null)
            ((delegate* unmanaged<void*, uint>)(*(void***)unknown)[2])(unknown);
    }

    /// <summary><c>IUnknown::QueryInterface</c> through an untyped pointer (slot 0).</summary>
    internal static int QueryInterface(void* unknown, Guid* iid, void** ppv)
        => ((delegate* unmanaged<void*, Guid*, void**, int>)(*(void***)unknown)[0])(unknown, iid, ppv);
}

/// <summary>
/// A hand-rolled COM callable wrapper (call-IN) implementing
/// <c>AsyncOperationProgressHandler&lt;DeploymentResult, DeploymentProgress&gt;</c> so the deployment engine can report
/// download/install percentage.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> <c>IAsyncOperationWithProgress.get_Progress</c> returns the registered handler, not a
/// progress value — there is nothing to poll. The only way to observe percentage is to hand WinRT a callback object,
/// so the "poll <c>get_Progress</c> every 100 ms" shape in the design note is not implementable.
/// </para>
/// <para>
/// <b>Why not <c>ComWrappers</c>.</b> Repo rule: no <c>ComWrappers</c> subclassing, no reflection. This is instead the
/// mirror image of the call-OUT vtables above — a native block whose first field is a pointer to a process-lifetime
/// vtable of four <c>[UnmanagedCallersOnly]</c> statics (WinRT delegates derive from <c>IUnknown</c>, so the vtable is
/// <c>QueryInterface</c>, <c>AddRef</c>, <c>Release</c>, <c>Invoke</c> — header line 6745).
/// </para>
/// <para>
/// <b>Why the state lives in native memory.</b> The callback fires on an arbitrary deployment thread. Keeping the
/// last-reported percentage in the native block itself means the thunk touches no managed object, needs no
/// <c>GCHandle</c>, and cannot allocate or block — the poll loop just reads the field with
/// <see cref="Volatile.Read(ref int)"/>. The sink also answers <c>IAgileObject</c>, so the engine never attempts to
/// marshal it across apartments.
/// </para>
/// <para>Lifetime: created with one reference; the operation takes its own on <c>put_Progress</c>. Call
/// <see cref="Dispose"/> to drop ours — the block is freed when the last reference goes.</para>
/// </remarks>
[SupportedOSPlatform("windows10.0.19041")]
internal readonly unsafe struct DeploymentProgressSink : IDisposable
{
    private const int E_NOINTERFACE = unchecked((int)0x80004002);
    private const int E_POINTER = unchecked((int)0x80004003);

    /// <summary>The native object handed to WinRT. <c>Vtbl</c> must be the first field (COM object layout).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Block
    {
        public void** Vtbl;
        public int RefCount;
        public int Percent;
        public uint State;
    }

    // Process-lifetime vtable, built once. Never freed — it is four function pointers.
    // Held as nint so it can be published with Volatile.Write (there is no pointer overload).
    private static nint s_vtbl;
    private static readonly object s_vtblGate = new();

    private readonly Block* _block;

    private DeploymentProgressSink(Block* block) => _block = block;

    /// <summary>The pointer to pass to <c>put_Progress</c>, or <see langword="null"/> if creation failed.</summary>
    internal void* Handler => _block;

    /// <summary>The sink's address as an <see cref="nint"/>. Poll loops capture THIS rather than the struct itself —
    /// a lambda may not close over a variable of pointer type, and an <see cref="nint"/> sidesteps the question.</summary>
    internal nint Address => (nint)_block;

    /// <summary>The most recent percentage the OS reported (0..100), read without tearing.</summary>
    internal int Percent => _block == null ? 0 : Volatile.Read(ref _block->Percent);

    /// <summary>The most recent percentage for a sink identified by <see cref="Address"/>. Returns 0 for a null
    /// address, so a sink that failed to install still polls harmlessly.</summary>
    internal static int PercentOf(nint address)
        => address == 0 ? 0 : Volatile.Read(ref ((Block*)address)->Percent);

    /// <summary>Allocate a sink with one reference held by the caller.</summary>
    internal static DeploymentProgressSink Create()
    {
        void** vtbl = EnsureVtbl();
        var block = (Block*)NativeMemory.AllocZeroed((nuint)sizeof(Block));
        block->Vtbl = vtbl;
        block->RefCount = 1;
        return new DeploymentProgressSink(block);
    }

    /// <summary>Drop the caller's reference. Safe to call on a default (null) sink.</summary>
    public void Dispose()
    {
        if (_block != null)
            ReleaseImpl(_block);
    }

    private static void** EnsureVtbl()
    {
        nint existing = Volatile.Read(ref s_vtbl);
        if (existing != 0)
            return (void**)existing;
        lock (s_vtblGate)
        {
            if (s_vtbl == 0)
            {
                var v = (void**)NativeMemory.Alloc((nuint)4, (nuint)sizeof(nint));
                v[0] = (void*)(delegate* unmanaged[Stdcall]<Block*, Guid*, void**, int>)&QueryInterfaceThunk;
                v[1] = (void*)(delegate* unmanaged[Stdcall]<Block*, uint>)&AddRefThunk;
                v[2] = (void*)(delegate* unmanaged[Stdcall]<Block*, uint>)&ReleaseThunk;
                v[3] = (void*)(delegate* unmanaged[Stdcall]<Block*, void*, DeploymentProgress, int>)&InvokeThunk;
                Volatile.Write(ref s_vtbl, (nint)v);
            }
        }
        return (void**)Volatile.Read(ref s_vtbl);
    }

    private static uint ReleaseImpl(Block* block)
    {
        int remaining = Interlocked.Decrement(ref block->RefCount);
        if (remaining <= 0)
        {
            NativeMemory.Free(block);
            return 0;
        }
        return (uint)remaining;
    }

    /// <summary><c>IUnknown::QueryInterface</c>. Answers <c>IUnknown</c>, <c>IAgileObject</c>, and the progress-handler
    /// delegate IID with the same pointer; everything else is <c>E_NOINTERFACE</c>.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int QueryInterfaceThunk(Block* self, Guid* iid, void** ppv)
    {
        if (ppv == null)
            return E_POINTER;
        if (self == null || iid == null)
        {
            *ppv = null;
            return E_POINTER;
        }
        if (*iid == DeploymentIids.IUnknown ||
            *iid == DeploymentIids.IAgileObject ||
            *iid == DeploymentIids.AsyncOperationProgressHandlerDeployment)
        {
            Interlocked.Increment(ref self->RefCount);
            *ppv = self;
            return 0;
        }
        *ppv = null;
        return E_NOINTERFACE;
    }

    /// <summary><c>IUnknown::AddRef</c>.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static uint AddRefThunk(Block* self)
        => self == null ? 0 : (uint)Interlocked.Increment(ref self->RefCount);

    /// <summary><c>IUnknown::Release</c>.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static uint ReleaseThunk(Block* self)
        => self == null ? 0 : ReleaseImpl(self);

    /// <summary><c>HRESULT Invoke(IAsyncOperationWithProgress* asyncInfo, DeploymentProgress progressInfo)</c> —
    /// vtable slot 3 (header lines 6754-6756). <paramref name="progressInfo"/> arrives <b>by value</b> (an 8-byte POD).
    /// Stores the report and returns; it must never allocate, block, or call back into WinRT.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int InvokeThunk(Block* self, void* asyncInfo, DeploymentProgress progressInfo)
    {
        if (self != null)
        {
            uint pct = progressInfo.Percentage > 100 ? 100 : progressInfo.Percentage;
            Volatile.Write(ref self->State, progressInfo.State);
            Volatile.Write(ref self->Percent, (int)pct);
        }
        return 0;
    }
}
