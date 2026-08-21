namespace Wavee;

/// <summary>Startup-only setup-wizard arming, run ONCE per install from <c>Program.Main</c> BEFORE anything constructs
/// <c>Services</c> / opens <c>library.db</c> — the exact <see cref="SidebarBootstrap"/> ordering rule, for the exact
/// same reason: <c>WaveeApp</c>'s constructor synchronously calls <c>Services.CreateReal</c>, which opens/creates
/// <c>library.db</c>, so probing after that point would make every install look "existing". <c>Program.cs</c> cannot
/// show FluentGpu UI this early (there is no window yet), so this writes settings only — the wizard itself is shown
/// later by the shell, once it has painted, gated on <c>WaveeSettings.SetupPending</c>.
///
/// Reuses <see cref="SidebarBootstrap.IsFreshInstall"/> rather than a second fresh-install detector: there is exactly
/// one answer to "is this a fresh install", shared by the sidebar chooser and the setup wizard, and a second detector
/// is how the two features end up disagreeing about the same install.</summary>
static class SetupBootstrap
{
    /// <summary>Monotonic "has the setup wizard been armed/settled for this install" guard. Bump this AND add the new
    /// work to <see cref="Run"/> if a future release needs another one-time setup-wizard startup step.</summary>
    public const int TargetVersion = 1;

    /// <param name="localAppDataOverride">Test seam only: the directory to probe instead of the real
    /// <c>%LOCALAPPDATA%</c> — forwarded verbatim to <see cref="SidebarBootstrap.IsFreshInstall"/>. Production passes
    /// null.</param>
    public static void Run(IAppSettings settings, string? localAppDataOverride = null, IWaveeLog? log = null)
    {
        if (settings.Get(WaveeSettings.SetupBootstrapVersion) >= TargetVersion) return;
        log ??= WaveeLog.Instance;

        bool fresh = SidebarBootstrap.IsFreshInstall(settings, localAppDataOverride, log);
        if (fresh)
        {
            settings.Set(WaveeSettings.SetupPending, true);
            settings.Set(WaveeSettings.SetupCompleted, false);
            // LOAD-BEARING: setup page 5 (Sidebar) *is* the sidebar-design chooser, so a fresh install must never also
            // arm SidebarDesignGating's OWN one-time chooser — showing both onboardings on one launch is exactly the
            // bug this line prevents. SidebarBootstrap already defaulted the design to Classic; this only suppresses
            // the separate popup chooser, it does not touch the chosen design.
            settings.Set(WaveeSettings.SidebarOnboardingSeen, true);
        }
        else
        {
            // Existing installs never retro-fit the wizard onto someone mid-use.
            settings.Set(WaveeSettings.SetupCompleted, true);
            settings.Set(WaveeSettings.SetupPending, false);
        }

        settings.Set(WaveeSettings.SetupBootstrapVersion, TargetVersion);
        log.Info("setup", "setup.bootstrap",
            fresh ? "Fresh install: first-run setup wizard armed." : "Existing install: first-run setup wizard suppressed.",
            WaveeLogField.Of("fresh", fresh));
    }
}
