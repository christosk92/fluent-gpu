namespace FluentGpu.WindowsApi;

/// <summary>
/// Scaffold marker for the FluentGpu Windows OS-services library. Planned surface (each in its folder):
/// <c>Notifications/</c> toast XML + activation plumbing; <c>Credentials/</c> PasswordVault / Windows Hello;
/// <c>Packaging/</c> package-identity queries (is-packaged, family name) — MSIX packaging itself stays app-side;
/// <c>Activation/</c> protocol/file activation routing into the app host.
/// </summary>
public static class WindowsApiInfo
{
    /// <summary>No runtime surface has shipped yet — this library is the reserved home, not an implementation.</summary>
    public const bool IsScaffold = true;
}
