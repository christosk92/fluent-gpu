using System;
using System.Threading;
using System.Threading.Tasks;

namespace FluentGpu.WindowsApi.Packaging;

/// <summary>
/// What the OS knows about this package's <c>.appinstaller</c> association — the answer to "is Windows itself keeping
/// this app up to date, and when did it last look?". Produced by <see cref="IPackageUpdater.GetAppInstallerInfo"/>;
/// <see langword="null"/> from that call means the package has NO association at all (it was installed from a bare
/// <c>.msix</c> rather than through an <c>.appinstaller</c>), which is the single most common reason both the OS
/// background updater and <see cref="IPackageUpdater.CheckUpdateAvailabilityAsync"/> silently do nothing.
/// </summary>
/// <param name="Uri">The <c>.appinstaller</c> feed URI the OS polls, or <see langword="null"/> if it could not be read.</param>
/// <param name="LastChecked">When the OS last polled the feed. <see cref="DateTimeOffset.MinValue"/> when never/unknown.</param>
/// <param name="PausedUntil">When a user-initiated pause expires. <see cref="DateTimeOffset.MinValue"/> when not paused
/// (the underlying WinRT property is a nullable <c>DateTime</c>; absent maps to <see cref="DateTimeOffset.MinValue"/>).</param>
/// <param name="OnLaunch">The feed asks the OS to check for updates on app launch.</param>
/// <param name="AutomaticBackgroundTask">The feed asks the OS to check for updates from a background task.</param>
public readonly record struct AppInstallerInfo(
    Uri? Uri,
    DateTimeOffset LastChecked,
    DateTimeOffset PausedUntil,
    bool OnLaunch,
    bool AutomaticBackgroundTask);

/// <summary>
/// Mirrors WinRT <c>Windows.ApplicationModel.PackageUpdateAvailability</c> 1:1 (same ordinals). <see cref="Unknown"/>
/// is the meaningful one: the OS returns it when the package has no App Installer association, so it is NOT "we don't
/// know yet" — it is "this install can never be updated by the OS updater". <see cref="Error"/> also covers our own
/// call failures (a failed activation, QI, or async operation), so callers never have to catch.
/// </summary>
public enum PackageUpdateAvailability
{
    /// <summary>No App Installer association — the OS cannot tell, and never will for this install.</summary>
    Unknown = 0,

    /// <summary>The feed was reachable and carries nothing newer.</summary>
    NoUpdates = 1,

    /// <summary>A newer version is published and optional.</summary>
    Available = 2,

    /// <summary>A newer version is published and the feed marks it mandatory.</summary>
    Required = 3,

    /// <summary>The check itself failed (feed unreachable, activation/QI failure, async error).</summary>
    Error = 4,
}

/// <summary>
/// The outcome of one <see cref="IPackageUpdater.ApplyFromAppInstallerAsync"/> deployment.
/// </summary>
/// <param name="IsRegistered">
/// <c>DeploymentResult.IsRegistered</c> — <see langword="true"/> when the new package is registered and live (Windows
/// will terminate and relaunch this process); <see langword="false"/> when the payload was only staged (registration
/// deferred to the next launch) or the deployment failed.
/// </param>
/// <param name="HResult">
/// <c>DeploymentResult.ExtendedErrorCode</c>, or the HRESULT of whichever step failed first. <c>0</c> means success.
/// Feed it to <see cref="PackageUpdateErrors.Classify"/> to get a user-facing category.
/// </param>
/// <param name="ErrorText"><c>DeploymentResult.ErrorText</c> — the OS's own diagnostic string, or <see cref="string.Empty"/>.</param>
public readonly record struct PackageDeploymentResult(bool IsRegistered, int HResult, string ErrorText);

/// <summary>
/// The packaged-app self-update seam: read the OS's App Installer association, ask whether the feed carries something
/// newer, and apply an update from the feed in-process (download → stage → register → OS relaunches us).
/// </summary>
/// <remarks>
/// <para>
/// Implemented by <see cref="PackageUpdater"/> on Windows 10 2004 (build 19041) and later when the process is packaged;
/// otherwise compose <see cref="NullPackageUpdater"/>, whose <see cref="IsSupported"/> is <see langword="false"/> and
/// whose every call is inert. Callers branch on <see cref="IsSupported"/> and fall back to opening the release page —
/// there is deliberately no throwing path, because "this build cannot self-update" is a normal, expected state (an
/// unpackaged <c>dotnet run</c>, a dev box, a bare-<c>.msix</c> install).
/// </para>
/// <para>
/// Every method is safe to call from any thread; the implementation confines all WinRT work to one dedicated MTA
/// worker thread of its own.
/// </para>
/// </remarks>
public interface IPackageUpdater
{
    /// <summary>Whether this process can actually drive a package update (packaged identity + a new-enough OS).
    /// When <see langword="false"/>, every other member returns its inert value without touching the OS.</summary>
    bool IsSupported { get; }

    /// <summary>The OS's App Installer association for this package, or <see langword="null"/> when there is none
    /// (bare-<c>.msix</c> install, unpackaged process, or the query failed). Cheap, local, and synchronous.</summary>
    AppInstallerInfo? GetAppInstallerInfo();

    /// <summary>Asks the OS whether the associated feed carries a newer version. Returns
    /// <see cref="PackageUpdateAvailability.Unknown"/> when there is no association and
    /// <see cref="PackageUpdateAvailability.Error"/> on any failure, cancellation included — it never throws and never
    /// returns a faulted or cancelled task.</summary>
    Task<PackageUpdateAvailability> CheckUpdateAvailabilityAsync(CancellationToken ct);

    /// <summary>
    /// Downloads, stages and registers the update described by <paramref name="feed"/> (an <c>.appinstaller</c> URI),
    /// forcing this app to shut down so the new bits can register. Registers this process for restart first, so
    /// Windows relaunches it afterwards.
    /// </summary>
    /// <param name="feed">The <c>.appinstaller</c> feed URI.</param>
    /// <param name="progress">Invoked with 0..100 whenever the OS-reported percentage changes. May be called from a
    /// background thread; callers must marshal to their own thread.</param>
    /// <param name="ct">Cancels the deployment (the OS operation is cancelled and <c>E_ABORT</c> is reported).</param>
    Task<PackageDeploymentResult> ApplyFromAppInstallerAsync(Uri feed, Action<int> progress, CancellationToken ct);
}

/// <summary>
/// The inert <see cref="IPackageUpdater"/> for every host that cannot self-update — an unpackaged process, a
/// pre-19041 OS, or a non-Windows build. Composition roots pick this over <see cref="PackageUpdater"/> so the rest of
/// the app never branches on platform.
/// </summary>
public sealed class NullPackageUpdater : IPackageUpdater
{
    /// <inheritdoc/>
    public bool IsSupported => false;

    /// <inheritdoc/>
    public AppInstallerInfo? GetAppInstallerInfo() => null;

    /// <inheritdoc/>
    public Task<PackageUpdateAvailability> CheckUpdateAvailabilityAsync(CancellationToken ct)
        => Task.FromResult(PackageUpdateAvailability.Unknown);

    /// <inheritdoc/>
    public Task<PackageDeploymentResult> ApplyFromAppInstallerAsync(Uri feed, Action<int> progress, CancellationToken ct)
        => Task.FromResult(new PackageDeploymentResult(false, PackageUpdateErrors.EAbort, string.Empty));
}

/// <summary>What went wrong, in terms a user-facing message can be written against.</summary>
public enum PackageUpdateFailureKind
{
    /// <summary>The feed or the payload could not be fetched (WinINet/WinHTTP-shaped failures, or a host that does not
    /// serve <c>Content-Length</c>).</summary>
    Network,

    /// <summary>Blocked because the connection is metered (raised by the caller's own policy, never by the OS).</summary>
    Metered,

    /// <summary><c>ERROR_PACKAGES_IN_USE</c> — a process of this package (usually an out-of-proc child) was still
    /// running, so the old version re-registered.</summary>
    PackagesInUse,

    /// <summary>The candidate is not a legal successor: a downgrade without <c>ForceUpdateFromAnyVersion</c>, or the
    /// same version with different bits.</summary>
    VersionConflict,

    /// <summary>Sideloading/developer-mode policy refused the install.</summary>
    SideloadPolicy,

    /// <summary><c>E_INVALIDARG</c> from the deployment engine — in practice a broken App Installer build (1.27.350.0
    /// regressed <c>.appinstaller</c> updates this way) or a malformed feed.</summary>
    AppInstallerOutdated,

    /// <summary>This install has no App Installer association, so there is nothing to update from.</summary>
    NotAssociated,

    /// <summary>Anything else — show the raw HRESULT and the OS's <c>ErrorText</c>.</summary>
    Unknown,
}

/// <summary>
/// Maps deployment HRESULTs to <see cref="PackageUpdateFailureKind"/>. Pure and total: every input maps to something,
/// so callers never need a catch-all of their own.
/// </summary>
/// <remarks>
/// The MSIX codes come from the Windows app-packaging troubleshooting table
/// (<see href="https://learn.microsoft.com/en-us/windows/win32/appxpkg/troubleshooting"/>); the <c>0x80072Exx</c>
/// family is WinINet/WinHTTP.
/// </remarks>
public static class PackageUpdateErrors
{
    /// <summary><c>E_ABORT</c> — the value reported for a cancelled or never-started deployment.</summary>
    public const int EAbort = unchecked((int)0x80004004);

    // MSIX deployment failures (appxpkg troubleshooting table).
    private const int ERROR_PACKAGES_IN_USE = unchecked((int)0x80073D02);
    private const int ERROR_INSTALL_PACKAGE_DOWNGRADE = unchecked((int)0x80073D06);
    private const int ERROR_INSTALL_PACKAGE_ALREADY_EXISTS = unchecked((int)0x80073CFB);
    private const int ERROR_INSTALL_POLICY_FAILURE = unchecked((int)0x80073CFF);

    // WinINet / WinHTTP transport failures surfaced through the deployment engine.
    private const int ERROR_INTERNET_INVALID_RESPONSE_LENGTH = unchecked((int)0x80072F76);
    private const int ERROR_INTERNET_TIMEOUT = unchecked((int)0x80072EE2);
    private const int ERROR_INTERNET_NAME_NOT_RESOLVED = unchecked((int)0x80072EE7);
    private const int ERROR_INTERNET_CANNOT_CONNECT = unchecked((int)0x80072EFD);

    private const int E_INVALIDARG = unchecked((int)0x80070057);

    /// <summary>Classify a deployment HRESULT. Unrecognised values (including <c>0</c>) map to
    /// <see cref="PackageUpdateFailureKind.Unknown"/> — callers only classify a value they already know is a failure.</summary>
    public static PackageUpdateFailureKind Classify(int hresult) => hresult switch
    {
        ERROR_PACKAGES_IN_USE => PackageUpdateFailureKind.PackagesInUse,
        ERROR_INSTALL_PACKAGE_DOWNGRADE or ERROR_INSTALL_PACKAGE_ALREADY_EXISTS => PackageUpdateFailureKind.VersionConflict,
        ERROR_INSTALL_POLICY_FAILURE => PackageUpdateFailureKind.SideloadPolicy,
        ERROR_INTERNET_INVALID_RESPONSE_LENGTH or ERROR_INTERNET_TIMEOUT or
        ERROR_INTERNET_NAME_NOT_RESOLVED or ERROR_INTERNET_CANNOT_CONNECT => PackageUpdateFailureKind.Network,
        E_INVALIDARG => PackageUpdateFailureKind.AppInstallerOutdated,
        _ => PackageUpdateFailureKind.Unknown,
    };
}
