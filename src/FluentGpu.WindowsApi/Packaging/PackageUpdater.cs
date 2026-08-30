using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.WindowsApi.Notifications;
using TerraFX.Interop.WinRT;
using static TerraFX.Interop.WinRT.WinRT;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.WindowsApi.Packaging;

/// <summary>
/// The real <see cref="IPackageUpdater"/>: drives a packaged app's own update through
/// <c>Windows.Management.Deployment.PackageManager</c> and reads the OS's App Installer association through
/// <c>Windows.ApplicationModel.Package</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The flow.</b> "Update now" is <c>RegisterApplicationRestart</c> (so Windows brings us back) →
/// <c>IPackageManager6.AddPackageByAppInstallerFileAsync(feedUri, ForceTargetAppShutdown, defaultVolume)</c> → poll the
/// operation while a progress callback fills in percentage → read <c>DeploymentResult</c>. On success Windows
/// terminates this process and relaunches the new build; there is no in-process "restart" API available without
/// WinAppSDK, which is exactly why the restart registration comes first. If the deploy does NOT succeed the
/// registration is undone again (<c>UnregisterApplicationRestart</c>) — otherwise an unrelated crash hours later would
/// silently relaunch the app.
/// </para>
/// <para>
/// <b>Threading.</b> WinRT deployment objects are apartment-bound and the operations are long-running, so every WinRT
/// call in this class runs on ONE dedicated background thread created with <see cref="ApartmentState.MTA"/> and shared
/// by all instances. Public methods marshal a work item onto it and complete a <see cref="TaskCompletionSource{T}"/>
/// from there; <see cref="GetAppInstallerInfo"/> is the one synchronous member and blocks the caller (briefly — it is
/// a local registry-shaped query) with a hard timeout. The <c>progress</c> callback of
/// <see cref="ApplyFromAppInstallerAsync"/> is therefore invoked on that worker thread, never on the UI thread.
/// </para>
/// <para>
/// <b>Errors.</b> Nothing here throws. Every failure becomes <see cref="PackageUpdateAvailability.Error"/> or a
/// <see cref="PackageDeploymentResult"/> carrying the HRESULT, which <see cref="PackageUpdateErrors.Classify"/> turns
/// into a user-facing category. Cancellation is reported as <c>E_ABORT</c>. A zero HRESULT means success to callers,
/// so this class never emits one on a path that did not register a package: an unreadable or absent
/// <c>ExtendedErrorCode</c> becomes <c>E_FAIL</c> with the reason in <c>ErrorText</c>.
/// </para>
/// <para>
/// <b>Manifest requirement.</b> Applying an update needs <c>&lt;rescap:Capability Name="packageManagement"/&gt;</c> in
/// the app manifest. Restricted capabilities need no approval for sideloaded packages.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows10.0.19041")]
public sealed unsafe partial class PackageUpdater : IPackageUpdater
{
    private const int E_ABORT = unchecked((int)0x80004004);
    private const int E_FAIL = unchecked((int)0x80004005);
    private const int REGDB_E_CLASSNOTREG = unchecked((int)0x80040154);

    /// <summary>Minimum OS build for <c>AddPackageByAppInstallerFileAsync</c> + <c>ExpectedDigests</c>-era deployment
    /// behaviour (Windows 10 2004). The App Installer file APIs themselves date to 16299, but 19041 is the floor the
    /// rest of this app targets and the version the deployment behaviour was validated against.</summary>
    private const int MinimumOsBuild = 19041;

    /// <summary>How often the deployment operation's status and the progress sink are sampled.</summary>
    private static readonly TimeSpan DeploymentPollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>How often the (much shorter) update-availability check is sampled.</summary>
    private static readonly TimeSpan CheckPollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>Ceiling on the one synchronous public call so a wedged OS never hangs a caller forever.</summary>
    private static readonly TimeSpan SynchronousCallTimeout = TimeSpan.FromSeconds(10);

    // ---- The single MTA worker -------------------------------------------------------------------------------

    private static readonly BlockingCollection<Action> s_work = new(new ConcurrentQueue<Action>());
    private static readonly object s_workerGate = new();
    private static Thread? s_worker;

    /// <summary>
    /// An extra token appended to the command line handed to <c>RegisterApplicationRestart</c>, so the process Windows
    /// relaunches after a successful deployment can TELL that it was relaunched (Wavee passes
    /// <c>--relaunched-after-update</c>). Null (the default) keeps the historical
    /// <c>RegisterApplicationRestart(null, 0)</c> behaviour, which reuses the original command line verbatim and leaves
    /// the relaunched process indistinguishable from a plain launch.
    /// <para>The app owns the token: this library neither defines nor interprets it, and the app must simply ignore it
    /// everywhere except wherever it wants to notice the relaunch. Keep it short — the whole reconstructed line is
    /// capped at <c>RESTART_MAX_CMD_LINE</c> (1024 chars) and the original arguments are dropped to make room.</para>
    /// </summary>
    public string? RestartArgument { get; init; }

    /// <inheritdoc/>
    public bool IsSupported => PackageIdentity.IsPackaged && PackageIdentity.OsBuild >= MinimumOsBuild;

    /// <inheritdoc/>
    public AppInstallerInfo? GetAppInstallerInfo()
        => IsSupported ? RunBlocking<AppInstallerInfo?>(ReadAppInstallerInfo, null) : null;

    /// <inheritdoc/>
    public Task<PackageUpdateAvailability> CheckUpdateAvailabilityAsync(CancellationToken ct)
    {
        if (!IsSupported)
            return Task.FromResult(PackageUpdateAvailability.Unknown);
        return RunAsync(() => CheckAvailability(ct), PackageUpdateAvailability.Error);
    }

    /// <inheritdoc/>
    public Task<PackageDeploymentResult> ApplyFromAppInstallerAsync(Uri feed, Action<int> progress, CancellationToken ct)
    {
        var failed = new PackageDeploymentResult(false, E_FAIL, string.Empty);
        if (!IsSupported)
            return Task.FromResult(failed);
        if (feed is null)
            return Task.FromResult(new PackageDeploymentResult(false, unchecked((int)0x80070057), string.Empty));
        return RunAsync(() => Deploy(feed, progress, RestartArgument, ct), failed);
    }

    // ---- GetAppInstallerInfo ---------------------------------------------------------------------------------

    /// <summary>
    /// <c>RoGetActivationFactory("Windows.ApplicationModel.Package", IPackageStatics)</c> → <c>get_Current</c> → QI
    /// <c>IPackage6</c> → <c>GetAppInstallerInfo</c>. A null <c>IAppInstallerInfo</c> (with <c>S_OK</c>) is the
    /// "installed from a bare .msix, no association" answer and maps to <see langword="null"/>.
    /// </summary>
    /// <remarks><c>GetAppInstallerInfo</c> lives on <c>IPackage6</c>, not <c>IPackage8</c>
    /// (<c>windows.applicationmodel.h:13186</c>); the scalar properties are on the later <c>IAppInstallerInfo2</c>,
    /// since <c>IAppInstallerInfo</c> itself only carries <c>Uri</c>.</remarks>
    private static AppInstallerInfo? ReadAppInstallerInfo()
    {
        IPackageStatics* statics = null;
        IPackage* package = null;
        void* package6 = null;
        IAppInstallerInfo* info = null;
        void* info2 = null;
        try
        {
            using var className = new HStringHandle(DeploymentIids.RuntimeClassPackage);
            Guid iidStatics = __uuidof<IPackageStatics>();
            if (RoGetActivationFactory(className.Value, &iidStatics, (void**)&statics) < 0 || statics == null)
                return null;

            if (statics->get_Current(&package) < 0 || package == null)
                return null;

            Guid iidPackage6 = __uuidof<IPackage6>();
            if (package->QueryInterface(&iidPackage6, &package6) < 0 || package6 == null)
                return null;

            if (((IPackage6*)package6)->GetAppInstallerInfo(&info) < 0 || info == null)
                return null;   // no App Installer association.

            Uri? uri = ReadUri(info);
            DateTimeOffset lastChecked = DateTimeOffset.MinValue;
            DateTimeOffset pausedUntil = DateTimeOffset.MinValue;
            bool onLaunch = false;
            bool background = false;

            Guid iidInfo2 = __uuidof<IAppInstallerInfo2>();
            if (info->QueryInterface(&iidInfo2, &info2) >= 0 && info2 != null)
            {
                var typed = (IAppInstallerInfo2*)info2;

                byte flag;
                if (typed->get_OnLaunch(&flag) >= 0)
                    onLaunch = flag != 0;
                if (typed->get_AutomaticBackgroundTask(&flag) >= 0)
                    background = flag != 0;

                WinRTDateTime checkedAt;
                if (typed->get_LastChecked(&checkedAt) >= 0)
                    lastChecked = FromWinRtDateTime(checkedAt.UniversalTime);

                pausedUntil = ReadPausedUntil(info2);
            }

            return new AppInstallerInfo(uri, lastChecked, pausedUntil, onLaunch, background);
        }
        catch
        {
            return null;
        }
        finally
        {
            Vtbl.Release(info2);
            if (info != null) info->Release();
            Vtbl.Release(package6);
            if (package != null) package->Release();
            if (statics != null) statics->Release();
        }
    }

    /// <summary>Read <c>IAppInstallerInfo.Uri</c> as a managed <see cref="Uri"/>; any failure yields
    /// <see langword="null"/> rather than an exception.</summary>
    private static Uri? ReadUri(IAppInstallerInfo* info)
    {
        IUriRuntimeClass* uri = null;
        try
        {
            if (info->get_Uri(&uri) < 0 || uri == null)
                return null;
            HSTRING text = default;
            if (uri->get_AbsoluteUri(&text) < 0)
                return null;
            try
            {
                string s = HStringHandle.ToManaged(text);
                return Uri.TryCreate(s, UriKind.Absolute, out Uri? parsed) ? parsed : null;
            }
            finally
            {
                WindowsDeleteString(text);
            }
        }
        finally
        {
            if (uri != null) uri->Release();
        }
    }

    /// <summary>Read the nullable <c>IAppInstallerInfo2.PausedUntil</c> through the hand slot (its out-parameter is an
    /// <c>IReference&lt;DateTime&gt;</c>). A null reference means "not paused".</summary>
    private static DateTimeOffset ReadPausedUntil(void* info2)
    {
        void* reference = null;
        try
        {
            if (Vtbl.AppInstallerInfoGetPausedUntil(info2, &reference) < 0 || reference == null)
                return DateTimeOffset.MinValue;
            long universalTime;
            return Vtbl.ReferenceGetValue(reference, &universalTime) < 0
                ? DateTimeOffset.MinValue
                : FromWinRtDateTime(universalTime);
        }
        finally
        {
            Vtbl.Release(reference);
        }
    }

    // ---- CheckUpdateAvailabilityAsync ------------------------------------------------------------------------

    /// <summary>
    /// <c>RoActivateInstance(PackageManager)</c> → <c>FindPackageByUserSecurityIdPackageFullName("", fullName)</c> → QI
    /// <c>IPackage6</c> → <c>CheckUpdateAvailabilityAsync</c> → poll → <c>get_Availability</c>.
    /// </summary>
    /// <remarks>The package MUST come from <c>PackageManager</c>, not from <c>Package.Current</c>: Microsoft documents
    /// that calling <c>CheckUpdateAvailabilityAsync</c> on the <c>Current</c> package fails with access denied. An
    /// empty user SID means "the current user".</remarks>
    private static PackageUpdateAvailability CheckAvailability(CancellationToken ct)
    {
        string? fullName = PackageIdentity.PackageFullName;
        if (string.IsNullOrEmpty(fullName))
            return PackageUpdateAvailability.Unknown;

        IInspectable* activated = null;
        void* manager = null;
        void* package = null;
        void* package6 = null;
        nint operation = 0;
        void* result = null;
        try
        {
            int hr = ActivatePackageManager(&activated, DeploymentIids.IPackageManager, &manager);
            if (hr < 0 || manager == null)
                return PackageUpdateAvailability.Error;

            using var emptySid = new HStringHandle(string.Empty);
            using var packageFullName = new HStringHandle(fullName);
            hr = ((IPackageManagerVtbl*)manager)->FindPackageByUserSecurityIdPackageFullName(
                emptySid.Value, packageFullName.Value, &package);
            if (hr < 0 || package == null)
                return PackageUpdateAvailability.Unknown;   // not found for this user ⇒ nothing to update.

            Guid iidPackage6 = __uuidof<IPackage6>();
            if (Vtbl.QueryInterface(package, &iidPackage6, &package6) < 0 || package6 == null)
                return PackageUpdateAvailability.Error;

            void* op = null;
            if (Vtbl.CheckUpdateAvailabilityAsync(package6, &op) < 0 || op == null)
                return PackageUpdateAvailability.Error;
            operation = (nint)op;

            (AsyncStatus status, _) = WinRtAsync.Wait(operation, null, CheckPollInterval, ct);
            if (status != AsyncStatus.Completed)
                return PackageUpdateAvailability.Error;

            if (Vtbl.AsyncOperationGetResults((void*)operation, &result) < 0 || result == null)
                return PackageUpdateAvailability.Error;

            int availability = 0;
            if (Vtbl.GetAvailability(result, &availability) < 0)
                return PackageUpdateAvailability.Error;

            // The WinRT PackageUpdateAvailability ordinals are identical to ours (Unknown=0 … Error=4).
            return availability is >= (int)PackageUpdateAvailability.Unknown and <= (int)PackageUpdateAvailability.Error
                ? (PackageUpdateAvailability)availability
                : PackageUpdateAvailability.Error;
        }
        catch
        {
            return PackageUpdateAvailability.Error;
        }
        finally
        {
            Vtbl.Release(result);
            if (operation != 0) WinRtAsync.Close(operation);
            Vtbl.Release(package6);
            Vtbl.Release(package);
            if (manager != null) ((IPackageManagerVtbl*)manager)->Release();
            if (activated != null) activated->Release();
        }
    }

    // ---- ApplyFromAppInstallerAsync --------------------------------------------------------------------------

    /// <summary>
    /// The full "Update now" deployment. Registers for restart, builds a WinRT <c>Uri</c>, activates a
    /// <c>PackageManager</c>, starts <c>AddPackageByAppInstallerFileAsync</c> with
    /// <c>ForceTargetAppShutdown</c>, installs the progress sink, and polls to completion.
    /// <para>Every exit that did not register a package unregisters the restart request again and carries a non-zero
    /// HRESULT, so a caller can never read a failed or cancelled deploy as success.</para>
    /// </summary>
    private static PackageDeploymentResult Deploy(Uri feed, Action<int> progress, string? restartArgument, CancellationToken ct)
    {
        // Do this FIRST: once ForceTargetAppShutdown lands, Windows terminates us with no further notice, and the
        // restart registration is the only thing that brings the app back. Microsoft requires it for non-UWP apps.
        // Note the OS rule that a process must have been up ~60 s for a restart registration to be honoured.
        bool restartRegistered = RegisterForRestart(restartArgument);

        // FIX: the registration used to survive a failed/cancelled deploy, so ANY later unrelated crash would have
        // silently relaunched Wavee. It is dropped again on every non-success path (see the finally); only the success
        // path — where Windows is about to terminate us on purpose — leaves it in place.
        bool keepRestartRegistration = false;

        IUriRuntimeClass* uri = null;
        IInspectable* activated = null;
        void* manager6 = null;
        void* manager3 = null;
        void* volume = null;
        nint operation = 0;
        void* result = null;
        void* result2 = null;
        var sink = default(DeploymentProgressSink);
        try
        {
            uri = CreateWinRtUri(feed);
            if (uri == null)
                return new PackageDeploymentResult(false, REGDB_E_CLASSNOTREG, string.Empty);

            int hr = ActivatePackageManager(&activated, DeploymentIids.IPackageManager6, &manager6);
            if (hr < 0 || manager6 == null)
                return new PackageDeploymentResult(false, hr < 0 ? hr : REGDB_E_CLASSNOTREG, string.Empty);

            // The target volume is optional — a failure here just means "use the system default", which is what a
            // null pointer already asks for, so it is deliberately not fatal.
            Guid iidManager3 = DeploymentIids.IPackageManager3;
            if (Vtbl.QueryInterface(activated, &iidManager3, &manager3) >= 0 && manager3 != null)
                _ = ((IPackageManager3Vtbl*)manager3)->GetDefaultPackageVolume(&volume);

            sink = DeploymentProgressSink.Create();

            void* op = null;
            hr = ((IPackageManager6Vtbl*)manager6)->AddPackageByAppInstallerFileAsync(
                uri,
                DeploymentIids.AddPackageByAppInstallerOptions_ForceTargetAppShutdown,
                volume,
                &op);
            if (hr < 0 || op == null)
                return new PackageDeploymentResult(false, hr < 0 ? hr : E_FAIL, string.Empty);
            operation = (nint)op;

            // Best effort: without the sink we still deploy, we just never report a percentage.
            _ = ((IAsyncOpDeploymentVtbl*)op)->put_Progress(sink.Handler);

            // Capture the sink by address, not by value: a closure cannot hold a pointer-typed variable, and this
            // keeps the poll callback free of any managed state the deployment thread could race on.
            nint sinkAddress = sink.Address;
            Action<int>? report = progress;
            int last = -1;
            void OnPoll()
            {
                int percent = DeploymentProgressSink.PercentOf(sinkAddress);
                if (percent == last)
                    return;
                last = percent;
                report?.Invoke(percent);
            }

            (AsyncStatus status, int errorCode) = WinRtAsync.Wait(operation, OnPoll, DeploymentPollInterval, ct);
            if (status == AsyncStatus.Canceled)
                return new PackageDeploymentResult(false, E_ABORT, string.Empty);
            if (status != AsyncStatus.Completed)
                return new PackageDeploymentResult(false, errorCode != 0 ? errorCode : E_FAIL, string.Empty);

            hr = ((IAsyncOpDeploymentVtbl*)operation)->GetResults(&result);
            if (hr < 0 || result == null)
                return new PackageDeploymentResult(false, hr < 0 ? hr : E_FAIL, string.Empty);

            var typed = (IDeploymentResultVtbl*)result;

            // FIX: the HRESULT of the get itself used to be discarded, so a failed read left `extended` at 0 and the
            // caller saw (IsRegistered:false, HResult:0) — indistinguishable from success. Fail closed instead.
            int extended = 0;
            bool extendedUnreadable = typed->get_ExtendedErrorCode(&extended) < 0;
            if (extendedUnreadable)
                extended = E_FAIL;

            string errorText = string.Empty;
            HSTRING text = default;
            if (typed->get_ErrorText(&text) >= 0)
            {
                try
                {
                    errorText = HStringHandle.ToManaged(text);
                }
                finally
                {
                    WindowsDeleteString(text);
                }
            }

            bool isRegistered = false;
            Guid iidResult2 = DeploymentIids.IDeploymentResult2;
            if (typed->QueryInterface(&iidResult2, &result2) >= 0 && result2 != null)
            {
                byte flag;
                if (((IDeploymentResult2Vtbl*)result2)->get_IsRegistered(&flag) >= 0)
                    isRegistered = flag != 0;
            }

            if (extendedUnreadable)
            {
                // Say so rather than letting E_FAIL look like the OS's own verdict.
                const string note = "the deployment result's extended error code could not be read";
                errorText = errorText.Length == 0 ? note : errorText + " (" + note + ")";
            }
            else if (!isRegistered && extended == 0)
            {
                // FIX: a Completed operation that registered nothing and reported no error must never surface as
                // (false, 0, "") — every caller reads a zero HRESULT as success. Fail closed with a reason.
                extended = E_FAIL;
                if (errorText.Length == 0)
                    errorText = "deployment completed without registering the package";
            }

            if (isRegistered && last != 100)
                report?.Invoke(100);

            // Only a registered package means Windows is about to shut us down and relaunch the new build.
            keepRestartRegistration = isRegistered;
            return new PackageDeploymentResult(isRegistered, extended, errorText);
        }
        catch (Exception ex)
        {
            return new PackageDeploymentResult(false, ex.HResult != 0 ? ex.HResult : E_FAIL, string.Empty);
        }
        finally
        {
            // Failure, cancellation, or a throw: drop the restart registration so a later crash cannot relaunch us.
            if (restartRegistered && !keepRestartRegistration)
                UnregisterForRestart();

            Vtbl.Release(result2);
            Vtbl.Release(result);
            if (operation != 0) WinRtAsync.Close(operation);
            sink.Dispose();
            Vtbl.Release(volume);
            if (manager3 != null) ((IPackageManager3Vtbl*)manager3)->Release();
            if (manager6 != null) ((IPackageManager6Vtbl*)manager6)->Release();
            if (activated != null) activated->Release();
            if (uri != null) uri->Release();
        }
    }

    // ---- WinRT plumbing --------------------------------------------------------------------------------------

    /// <summary><c>RoActivateInstance("Windows.Management.Deployment.PackageManager")</c> then QI to
    /// <paramref name="iid"/>. The caller Releases both <paramref name="activated"/> and
    /// <paramref name="typed"/>.</summary>
    private static int ActivatePackageManager(IInspectable** activated, Guid iid, void** typed)
    {
        *activated = null;
        *typed = null;
        using var className = new HStringHandle(DeploymentIids.RuntimeClassPackageManager);
        int hr = RoActivateInstance(className.Value, activated);
        if (hr < 0 || *activated == null)
            return hr < 0 ? hr : REGDB_E_CLASSNOTREG;
        Guid local = iid;
        hr = (*activated)->QueryInterface(&local, typed);
        return hr < 0 ? hr : *typed == null ? REGDB_E_CLASSNOTREG : 0;
    }

    /// <summary>Build a <c>Windows.Foundation.Uri</c> from a managed <see cref="Uri"/> via
    /// <c>IUriRuntimeClassFactory.CreateUri</c>. Returns <see langword="null"/> on any failure; the caller
    /// Releases the result.</summary>
    private static IUriRuntimeClass* CreateWinRtUri(Uri feed)
    {
        IUriRuntimeClassFactory* factory = null;
        try
        {
            using var className = new HStringHandle(DeploymentIids.RuntimeClassUri);
            Guid iid = __uuidof<IUriRuntimeClassFactory>();
            if (RoGetActivationFactory(className.Value, &iid, (void**)&factory) < 0 || factory == null)
                return null;
            using var text = new HStringHandle(feed.AbsoluteUri);
            IUriRuntimeClass* uri = null;
            if (factory->CreateUri(text.Value, &uri) < 0)
                return null;
            return uri;
        }
        finally
        {
            if (factory != null) factory->Release();
        }
    }

    /// <summary>WinRT <c>DateTime.UniversalTime</c> (100 ns ticks since 1601-01-01 UTC — the FILETIME epoch) to
    /// <see cref="DateTimeOffset"/>. Out-of-range or unset values become <see cref="DateTimeOffset.MinValue"/>.</summary>
    private static DateTimeOffset FromWinRtDateTime(long universalTime)
    {
        if (universalTime <= 0)
            return DateTimeOffset.MinValue;
        try
        {
            return new DateTimeOffset(DateTime.FromFileTimeUtc(universalTime));
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.MinValue;
        }
    }

    /// <summary>
    /// <c>RegisterApplicationRestart</c> — ask Windows to relaunch this executable after the update terminates it.
    /// Failure is non-fatal: the update still applies, the user just has to start the app again.
    /// </summary>
    /// <param name="extraArgument">An optional token to APPEND to the restart command line (see
    /// <see cref="RestartArgument"/>). When null/empty the OS is passed <c>null</c> and reuses this process's original
    /// command line verbatim — which is indistinguishable from a normal launch, so the relaunched process cannot tell
    /// it came back after an update.</param>
    /// <returns><see langword="true"/> only if the OS accepted the registration, i.e. only if there is something for
    /// <see cref="UnregisterForRestart"/> to undo on a failed deploy.</returns>
    private static bool RegisterForRestart(string? extraArgument)
    {
        try
        {
            string? commandLine = BuildRestartCommandLine(extraArgument);
            if (commandLine is null)
                return RegisterApplicationRestart(null, 0) >= 0;
            fixed (char* p = commandLine)
                return RegisterApplicationRestart(p, 0) >= 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// This process's own arguments (argv[1..], re-quoted) plus <paramref name="extraArgument"/>, or
    /// <see langword="null"/> to mean "let the OS reuse the original command line".
    /// </summary>
    /// <remarks>
    /// <para><b>No exe name.</b> <c>RegisterApplicationRestart</c> documents that the command line must NOT contain the
    /// executable — the OS prepends it — so argv[0] is dropped.</para>
    /// <para><b>The length cap.</b> <c>RESTART_MAX_CMD_LINE</c> is 1024 characters, and the OS rejects anything longer.
    /// If the reconstructed line does not fit, the original arguments are dropped and only
    /// <paramref name="extraArgument"/> is registered: losing the old arguments on a relaunch is a far smaller loss
    /// than losing the relaunch (or the ability to tell that it was one).</para>
    /// </remarks>
    private static string? BuildRestartCommandLine(string? extraArgument)
    {
        if (string.IsNullOrEmpty(extraArgument))
            return null;

        string[] argv;
        try { argv = Environment.GetCommandLineArgs(); }
        catch { argv = Array.Empty<string>(); }

        var sb = new System.Text.StringBuilder(256);
        for (int i = 1; i < argv.Length; i++)
        {
            AppendQuoted(sb, argv[i]);
            sb.Append(' ');
        }
        sb.Append(extraArgument);

        return sb.Length <= RestartMaxCommandLine ? sb.ToString() : extraArgument;
    }

    /// <summary>Append one argument in the form <c>CommandLineToArgvW</c> parses back (quote only when needed; a run of
    /// backslashes before a quote is doubled).</summary>
    private static void AppendQuoted(System.Text.StringBuilder sb, string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(s_needsQuoting) < 0)
        {
            sb.Append(arg);
            return;
        }

        sb.Append('"');
        int backslashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') { sb.Append('\\', backslashes * 2 + 1).Append('"'); backslashes = 0; continue; }
            if (backslashes > 0) { sb.Append('\\', backslashes); backslashes = 0; }
            sb.Append(c);
        }
        sb.Append('\\', backslashes * 2).Append('"');
    }

    private static readonly char[] s_needsQuoting = { ' ', '\t', '"', '\n', '\v' };

    /// <summary><c>RESTART_MAX_CMD_LINE</c> (winbase.h) — the OS caps the restart command line at 1024 characters.</summary>
    private const int RestartMaxCommandLine = 1024;

    /// <summary>
    /// <c>UnregisterApplicationRestart()</c> — undo <see cref="RegisterForRestart"/>. Called on every non-success exit
    /// from <see cref="Deploy"/>: a registration left behind after a failed or cancelled update would make Windows
    /// silently relaunch the app after any later crash, which reads as a ghost restart to the user.
    /// </summary>
    private static void UnregisterForRestart()
    {
        try
        {
            _ = UnregisterApplicationRestart();
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    /// <summary><c>HRESULT RegisterApplicationRestart(PCWSTR pwzCommandline, DWORD dwFlags)</c> (kernel32). A null
    /// command line reuses this process's own. Flags 0 = restart for every termination reason.</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "RegisterApplicationRestart")]
    private static unsafe partial int RegisterApplicationRestart(char* pwzCommandline, uint dwFlags);

    /// <summary><c>HRESULT UnregisterApplicationRestart(void)</c> (kernel32). Removes this process's restart
    /// registration; <c>S_OK</c> even when none was registered.</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "UnregisterApplicationRestart")]
    private static partial int UnregisterApplicationRestart();

    // ---- Worker-thread dispatch ------------------------------------------------------------------------------

    /// <summary>Start (once) the dedicated MTA thread that owns every WinRT call this class makes.</summary>
    private static void EnsureWorker()
    {
        if (Volatile.Read(ref s_worker) is not null)
            return;
        lock (s_workerGate)
        {
            if (s_worker is not null)
                return;
            var thread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "FluentGpu.PackageUpdater",
            };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            Volatile.Write(ref s_worker, thread);
        }
    }

    private static void WorkerLoop()
    {
        // The thread is already MTA via SetApartmentState; this makes the WinRT side of that explicit and tolerates
        // the S_FALSE / RPC_E_CHANGED_MODE "already initialized" answers.
        _ = WinRtAsync.EnsureRoInitializedMultiThreaded();

        foreach (Action work in s_work.GetConsumingEnumerable())
        {
            try
            {
                work();
            }
            catch
            {
                // Each work item completes its own TaskCompletionSource; a throw that escapes must never kill the
                // shared worker.
            }
        }
    }

    /// <summary>Queue <paramref name="work"/> onto the MTA thread and complete a task with its result;
    /// <paramref name="fallback"/> is used if the work item throws.</summary>
    private static Task<T> RunAsync<T>(Func<T> work, T fallback)
    {
        EnsureWorker();
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        s_work.Add(() =>
        {
            try
            {
                tcs.TrySetResult(work());
            }
            catch
            {
                tcs.TrySetResult(fallback);
            }
        });
        return tcs.Task;
    }

    /// <summary>Queue <paramref name="work"/> onto the MTA thread and block for it, bounded by
    /// <see cref="SynchronousCallTimeout"/>. Used only by <see cref="GetAppInstallerInfo"/>, whose OS call is local
    /// and fast.</summary>
    private static T RunBlocking<T>(Func<T> work, T fallback)
    {
        Task<T> task = RunAsync(work, fallback);
        try
        {
            return task.Wait(SynchronousCallTimeout) ? task.Result : fallback;
        }
        catch
        {
            return fallback;
        }
    }
}
