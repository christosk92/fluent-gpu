namespace FluentGpu.WindowsApi;

/// <summary>
/// Library marker for the FluentGpu Windows OS-services surface (WinAppSDK-shaped, hand-rolled interop — no
/// <c>Microsoft.WindowsAppSDK</c>, no CsWinRT; see <c>docs/plans/windowsapi-implementation-research.md</c>). The four
/// pillars, each in its folder:
/// <list type="bullet">
/// <item><c>Packaging/</c> — runtime package-identity queries (<see cref="Packaging.PackageIdentity"/>: is-packaged,
/// family/full name, AUMID, version, install location). <b>Shipped.</b></item>
/// <item><c>Credentials/</c> — secure secret storage over the Win32 Credential Manager
/// (<see cref="Credentials.CredentialStore"/>). The earlier "PasswordVault / Windows Hello" framing was aspirational;
/// the shipped design is raw CredMan (identity-free, AOT-clean, the path Microsoft recommends for full-trust desktop
/// apps — research §2.4). <b>Shipped.</b></item>
/// <item><c>Notifications/</c> — local toast XML (<see cref="Notifications.ToastBuilder"/>) + the WinRT
/// <c>IToastNotifier</c> Show path and the <c>INotificationActivationCallback</c> activator
/// (<see cref="Notifications.ToastNotifier"/>). <b>Shipped</b> (cold-launch activation owes live validation —
/// research §5 #2/#3).</item>
/// <item><c>Activation/</c> — protocol/file/startup registration (<see cref="Activation.ProtocolRegistrar"/>),
/// command-line activation classification (<see cref="Activation.ActivationArgs"/>), and single-instance redirect
/// (<see cref="Activation.SingleInstanceGate"/>) into the app host's <c>IPlatformApp.ActivationRedirected</c> seam.
/// <b>Shipped</b> (unpackaged path; packaged protocol-arg delivery owes live validation — research §5 #4).</item>
/// </list>
/// MSIX packaging itself stays app-side (publish-then-package), deliberately not in this class library.
/// </summary>
public static class WindowsApiInfo
{
    /// <summary>
    /// <see langword="false"/> now that all four pillars (<c>Packaging</c>, <c>Credentials</c>, <c>Notifications</c>,
    /// <c>Activation</c>) ship real runtime surface. Consumers branch on concrete types (e.g.
    /// <see cref="Packaging.PackageIdentity.IsPackaged"/>) rather than this flag, which is retained only so an external
    /// caller that probed it does not break (research §4 step 1).
    /// </summary>
    public const bool IsScaffold = false;
}
