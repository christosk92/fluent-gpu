using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using FluentGpu.WindowsApi.Packaging;
using Wavee.Core;

namespace Wavee;

/// <summary>
/// The real <see cref="IAppUpdateService"/>: it reads the SAME per-arch <c>.appinstaller</c> feed the OS uses for
/// packaged auto-update (<c>ops/build/Wavee.AppInstaller.template.xml</c>), so the in-app prompt and Windows never
/// disagree about what "available" means.
/// <para>
/// Check = one GET of the feed (GitHub redirects <c>releases/latest/download/…</c> to the current tag; HttpClient
/// follows that by default) and one attribute read of the root <c>&lt;AppInstaller Version="…"&gt;</c>. Parsing goes
/// through <see cref="XmlReader"/> rather than an object mapper: no reflection, AOT-clean, and it stops at the root
/// element instead of materializing a document.
/// </para>
/// <para>
/// Applying an update is the platform's job, not ours. When packaged, <see cref="DownloadAsync"/> hands the feed to the
/// App Installer via <c>ms-appinstaller:?source=…</c> and Windows does the download/stage/restart; when unpackaged
/// (a loose NativeAOT publish) there is nothing to hand it to, so we open the release page and let the user pick a
/// build. Either way this service never downloads or replaces a binary itself.
/// </para>
/// <para>Errors are terminal for the attempt, never for the process: every failure lands in
/// <see cref="AppUpdateState.Failed"/> with a human message in <see cref="Error"/>, is logged, and is swallowed.</para>
/// </summary>
sealed class AppInstallerUpdateService : IAppUpdateService
{
    const string RepoUrl = "https://github.com/christosk92/fluent-gpu";
    const string LogCategory = "update";

    readonly SimpleEvent<int> _changed = new();
    readonly IAppSettings _settings;
    readonly HttpClient _http;
    readonly IWaveeLog _log;
    readonly string _currentVersion;
    readonly string _feedUrl;
    int _rev;

    /// <summary>The process-wide updater instance, published by the composition root's construction of it.
    /// <see cref="IAppUpdateService"/> is app-scoped by contract (one updater per process, a plain field on
    /// <c>Services</c> with no switchable wrapper), so surfaces that cannot reach the service bag —
    /// <c>PlaybackBridge.Activate</c>, which starts the poll — resolve it here. Null until <c>Services</c> is built.</summary>
    public static AppInstallerUpdateService? Instance { get; private set; }

    public AppUpdateState Current { get; private set; } = AppUpdateState.None;
    public string? Version { get; private set; }
    public string? ReleaseNotesUrl { get; private set; }
    public string? Error { get; private set; }
    public IObservable<int> Changed => _changed;

    /// <summary>The per-arch update feed this build watches. Also what <see cref="DownloadAsync"/> hands to App Installer.</summary>
    public string FeedUrl => _feedUrl;

    public AppInstallerUpdateService(IAppSettings settings, HttpClient http, string currentVersion, string arch, IWaveeLog log)
    {
        _settings = settings;
        _http = http;
        _log = log;
        _currentVersion = currentVersion;
        _feedUrl = RepoUrl + "/releases/latest/download/Wavee." + arch + ".appinstaller";

        // "You were updated" is decided ONCE, here, by comparing the version that last ran with the one now running —
        // the only moment where the previous run's value is still on disk. A first-ever launch is not an update.
        string lastRun = settings.Get(WaveeSettings.LastRunVersion);
        if (AppUpdateVersion.IsFirstRunAfterUpdate(lastRun, currentVersion))
        {
            Current = AppUpdateState.Completed;
            Version = currentVersion;
            ReleaseNotesUrl = ReleaseNotesFor(currentVersion);
            _log.Info(LogCategory, "updated: " + lastRun + " -> " + currentVersion);
        }
        settings.Set(WaveeSettings.LastRunVersion, currentVersion);
        Instance = this;
    }

    public async Task CheckAsync(CancellationToken ct)
    {
        try
        {
            string? remote = await ReadFeedVersionAsync(ct).ConfigureAwait(false);
            _settings.Set(WaveeSettings.UpdateLastCheckedMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            if (string.IsNullOrEmpty(remote))
            {
                Publish(AppUpdateState.Failed, null, null, "The update feed did not carry a version.");
                _log.Warn(LogCategory, "update feed had no Version attribute: " + _feedUrl);
                return;
            }

            if (AppUpdateVersion.IsNewer(remote, _currentVersion))
            {
                _log.Info(LogCategory, "update available: " + remote + " (running " + _currentVersion + ")");
                Publish(AppUpdateState.Available, remote, ReleaseNotesFor(remote), null);
            }
            else
            {
                _log.Info(LogCategory, "up to date: feed " + remote + ", running " + _currentVersion);
                // "Up to date" must not silently eat the "you were updated" notice the ctor raised — the poll runs 30 s
                // after launch, long before the user has necessarily looked at the notification centre. Only
                // Acknowledge() clears a Completed.
                if (Current != AppUpdateState.Completed) Publish(AppUpdateState.None, null, null, null);
            }
        }
        catch (OperationCanceledException)
        {
            // A cancelled check is not a failure — leave whatever state we already had.
        }
        catch (Exception ex)
        {
            Publish(AppUpdateState.Failed, Version, ReleaseNotesUrl, ex.Message);
            _log.Warn(LogCategory, "update check failed", ex);
        }
    }

    public Task DownloadAsync(CancellationToken ct)
    {
        try
        {
            // Packaged: App Installer owns download + stage + relaunch. Unpackaged: there is no installer to drive, so
            // the honest action is to show the release page rather than pretend an in-place update happened.
            string target = PackageIdentity.IsPackaged
                ? "ms-appinstaller:?source=" + _feedUrl
                : ReleaseNotesUrl ?? (RepoUrl + "/releases/latest");
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            Publish(AppUpdateState.Downloaded, Version, ReleaseNotesUrl, null);
            _log.Info(LogCategory, "handed the update to the shell: " + target);
        }
        catch (Exception ex)
        {
            Publish(AppUpdateState.Failed, Version, ReleaseNotesUrl, ex.Message);
            _log.Warn(LogCategory, "could not start the update", ex);
        }
        return Task.CompletedTask;
    }

    /// <summary>Same action as <see cref="DownloadAsync"/>: App Installer is what restarts Wavee, so "apply" and
    /// "download" are one gesture — we hand the feed over and the platform takes it from there.</summary>
    public void RestartToApply() => _ = DownloadAsync(CancellationToken.None);

    public void Acknowledge() => Publish(AppUpdateState.None, null, null, null);

    async Task<string?> ReadFeedVersionAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, _feedUrl);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,   // a feed is untrusted input: no DTDs, no external entities
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            Async = true,
        };
        using var reader = XmlReader.Create(stream, settings);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (!string.Equals(reader.LocalName, "AppInstaller", StringComparison.Ordinal)) return null;
            return reader.GetAttribute("Version");
        }
        return null;
    }

    static string ReleaseNotesFor(string version)
        => RepoUrl + "/releases/tag/wavee-v" + AppUpdateVersion.ReleaseTagVersion(version);

    void Publish(AppUpdateState state, string? version, string? notes, string? error)
    {
        Current = state;
        Version = version;
        ReleaseNotesUrl = notes;
        Error = error;
        _changed.OnNext(Interlocked.Increment(ref _rev));
    }
}
