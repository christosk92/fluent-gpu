using System.Net.Http;
using Wavee.Backend.Audio;
using Wavee.SpotifyLive.Audio.Runtime;

namespace Wavee.SpotifyLive.Audio;

/// <summary>Tracked runtime provisioner — locate, verify, register, and surface status to the UI.</summary>
public sealed class PlayPlayRuntimeProvisioner
{
    readonly IAppSettings _settings;
    readonly AudioRuntimeStatusService _status;
    readonly PlayPlayRuntimeStore _store;
    readonly PlayPlayRuntimeDownloader _downloader;
    readonly Action<string>? _log;
    readonly IWaveeLog? _structuredLog;
    PlaybackRuntimeStatus _snapshot = PlaybackRuntimeStatus.NotApplicable;
    RuntimeAsset? _asset;

    public PlayPlayRuntimeProvisioner(IAppSettings settings, AudioRuntimeStatusService status, Action<string>? log = null, HttpClient? http = null,
        IWaveeLog? structuredLog = null)
    {
        _settings = settings;
        _status = status;
        _store = new PlayPlayRuntimeStore(settings, log);
        _downloader = new PlayPlayRuntimeDownloader(http, log);
        _log = log;
        _structuredLog = structuredLog;
    }

    public PlayPlayRuntimeStore Store => _store;
    public RuntimeAsset? CurrentAsset => _asset;
    public PlaybackRuntimeStatus Snapshot => _snapshot;

    public Task<RuntimeAsset?> EnsureRuntimeAsync(CancellationToken ct = default, bool allowUntrustedSignature = false)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(EnsureRuntime(allowUntrustedSignature));
    }

    public RuntimeAsset? EnsureRuntime(bool allowUntrustedSignature = false)
    {
        Event(WaveeLogLevel.Debug, "runtime.ensure.start", "Ensuring PlayPlay runtime",
            WaveeLogField.Of("allowUntrusted", allowUntrustedSignature));
        var located = PlayPlayRuntimeLocator.Locate(_settings, _log);
        var candidate = located.Candidate;
        if (candidate is null)
        {
            var (outcome, detail) = located.Reason switch
            {
                PlayPlayRuntimeLocateReason.WrongArch => (ProvisioningOutcome.ArchUnsupported, "installed runtime is for a different architecture"),
                PlayPlayRuntimeLocateReason.UnsupportedAlgorithm => (ProvisioningOutcome.RuntimeUnavailable, "installed runtime uses an unsupported algorithm"),
                _ => (ProvisioningOutcome.RuntimeUnavailable, "no runtime candidate"),
            };
            SetSnapshot(outcome, detail);
            Event(WaveeLogLevel.Warning, "runtime.locate.none", "No usable PlayPlay runtime candidate found",
                WaveeLogField.Of("reason", located.Reason.ToString()),
                WaveeLogField.Of("outcome", outcome.ToString()),
                WaveeLogField.Of("detail", detail));
            return null;
        }
        Event(WaveeLogLevel.Info, "runtime.locate.selected", "Selected PlayPlay runtime candidate",
            WaveeLogField.Of("source", candidate.Source.ToString()),
            WaveeLogField.Of("dir", candidate.RuntimeDir));

        var verify = PlayPlayRuntimeVerifier.Verify(candidate.DllPath, candidate.ManifestPath, allowUntrustedSignature, _log);
        if (!verify.Ok)
        {
            SetSnapshot(verify.Outcome, verify.Detail, verify.Manifest, verify.RuntimeDir, verify.SignatureTrust,
                verify.NeedsUntrustedConfirmation, verify.SignatureInfo, verify.TrustedByPinnedFingerprint);
            Event(WaveeLogLevel.Warning, "runtime.verify.failed", "PlayPlay runtime verification failed",
                WaveeLogField.Of("outcome", verify.Outcome.ToString()),
                WaveeLogField.Of("detail", verify.Detail ?? ""),
                WaveeLogField.Of("runtimeDir", verify.RuntimeDir ?? ""),
                WaveeLogField.Of("signature", verify.SignatureTrust.ToString()),
                WaveeLogField.Of("needsUntrusted", verify.NeedsUntrustedConfirmation));
            return null;
        }

        _asset = PlayPlayRuntimeVerifier.ToAsset(verify);
        SpotifyRuntimeIdentityHost.ApplyFromManifest(verify.Manifest!);
        SpotifyRuntimeIdentityHost.ApplyRuntimeArchitecture(_asset.Config.Arch);
        SetSnapshot(ProvisioningOutcome.Ready, manifest: verify.Manifest, runtimeDir: verify.RuntimeDir,
            sig: verify.SignatureTrust, signatureInfo: verify.SignatureInfo,
            trustedByPinnedFingerprint: verify.TrustedByPinnedFingerprint);
        Event(WaveeLogLevel.Info, "runtime.verify.ready", "PlayPlay runtime verified",
            WaveeLogField.Of("pack", _asset.PackId),
            WaveeLogField.Of("version", _asset.Config.Version),
            WaveeLogField.Of("arch", _asset.Config.Arch.ToString()),
            WaveeLogField.Of("signature", verify.SignatureTrust.ToString()),
            WaveeLogField.Of("signer", verify.SignatureInfo?.Subject ?? ""),
            WaveeLogField.Of("pinned", verify.TrustedByPinnedFingerprint));
        return _asset;
    }

    public bool TryRegisterRuntime(string sourceDir, bool allowUntrustedSignature = false)
    {
        Event(WaveeLogLevel.Info, "runtime.register.start", "Registering PlayPlay runtime",
            WaveeLogField.Of("sourceDir", sourceDir),
            WaveeLogField.Of("allowUntrusted", allowUntrustedSignature));
        var verify = _store.Register(sourceDir, allowUntrustedSignature);
        if (!verify.Ok)
        {
            SetSnapshot(verify.Outcome, verify.Detail, verify.Manifest, verify.RuntimeDir, verify.SignatureTrust,
                verify.NeedsUntrustedConfirmation, verify.SignatureInfo, verify.TrustedByPinnedFingerprint);
            Event(WaveeLogLevel.Warning, "runtime.register.failed", "PlayPlay runtime registration failed",
                WaveeLogField.Of("outcome", verify.Outcome.ToString()),
                WaveeLogField.Of("detail", verify.Detail ?? ""),
                WaveeLogField.Of("runtimeDir", verify.RuntimeDir ?? ""),
                WaveeLogField.Of("needsUntrusted", verify.NeedsUntrustedConfirmation));
            return false;
        }
        _asset = PlayPlayRuntimeVerifier.ToAsset(verify);
        SpotifyRuntimeIdentityHost.ApplyFromManifest(verify.Manifest!);
        SpotifyRuntimeIdentityHost.ApplyRuntimeArchitecture(_asset.Config.Arch);
        SetSnapshot(ProvisioningOutcome.Ready, manifest: verify.Manifest, runtimeDir: verify.RuntimeDir,
            sig: verify.SignatureTrust, signatureInfo: verify.SignatureInfo,
            trustedByPinnedFingerprint: verify.TrustedByPinnedFingerprint);
        Event(WaveeLogLevel.Info, "runtime.register.ready", "PlayPlay runtime registered",
            WaveeLogField.Of("pack", _asset.PackId),
            WaveeLogField.Of("version", _asset.Config.Version),
            WaveeLogField.Of("arch", _asset.Config.Arch.ToString()),
            WaveeLogField.Of("runtimeDir", verify.RuntimeDir ?? ""),
            WaveeLogField.Of("signer", verify.SignatureInfo?.Subject ?? ""),
            WaveeLogField.Of("pinned", verify.TrustedByPinnedFingerprint));
        return true;
    }

    /// <summary>Fetch the runtime catalog (never automatic — always behind an explicit user action in the UI).</summary>
    public Task<PlayPlayRuntimeCatalog?> FetchCatalogAsync(CancellationToken ct = default)
    {
        var url = PlayPlayRuntimeDownloader.ResolveCatalogUrl(_settings);
        Event(WaveeLogLevel.Info, "runtime.catalog.fetch", "Fetching PlayPlay runtime catalog",
            WaveeLogField.Of("host", WaveeLogRedaction.UrlHost(url)));
        return _downloader.FetchCatalogAsync(url, ct);
    }

    /// <summary>Newest supported pack for this device + whether the catalog only offers other-arch packs.</summary>
    public (PlayPlayRuntimeCatalogEntry? Best, bool AnyForOtherArch) SelectBest(PlayPlayRuntimeCatalog catalog)
        => PlayPlayRuntimeDownloader.SelectBest(catalog);

    /// <summary>All packs installable on this device, newest first — the Advanced version picker.</summary>
    public IReadOnlyList<PlayPlayRuntimeCatalogEntry> SupportedPacks(PlayPlayRuntimeCatalog catalog)
        => PlayPlayRuntimeDownloader.SupportedPacks(catalog);

    /// <summary>Download + verify + install <paramref name="entry"/>, then (on success) bind identity/arch, persist the
    /// active pointer, and publish a Ready snapshot. Any failure publishes the honest terminal snapshot.</summary>
    public async Task<PlayPlayRuntimeVerifyResult> DownloadAndInstallAsync(
        PlayPlayRuntimeCatalogEntry entry,
        bool allowUntrustedSignature,
        IProgress<PlayPlayDownloadProgress>? progress,
        CancellationToken ct = default)
    {
        Event(WaveeLogLevel.Info, "runtime.download.start", "Downloading PlayPlay runtime pack",
            WaveeLogField.Of("pack", entry.PackId),
            WaveeLogField.Of("version", entry.SpotifyVersion),
            WaveeLogField.Of("arch", entry.Arch),
            WaveeLogField.Of("urls", entry.Urls.Length));
        if (!PlayPlayRuntimeManifest.TryParseArch(entry.Arch, out var arch))
        {
            SetSnapshot(ProvisioningOutcome.NoSupportedPack, "pack has an unsupported architecture");
            Event(WaveeLogLevel.Warning, "runtime.download.bad_arch", "PlayPlay runtime pack has unsupported architecture",
                WaveeLogField.Of("pack", entry.PackId),
                WaveeLogField.Of("arch", entry.Arch));
            return PlayPlayRuntimeVerifyResult.Fail(ProvisioningOutcome.NoSupportedPack, "pack has an unsupported architecture");
        }

        var destDir = PlayPlayRuntimePaths.RuntimeDir(entry.AppVersion, arch);
        var verify = await _downloader.DownloadAndInstallAsync(entry, destDir, allowUntrustedSignature, progress, ct).ConfigureAwait(false);

        if (verify.Ok)
        {
            _store.PersistPointer(destDir, entry.PackId);
            _asset = PlayPlayRuntimeVerifier.ToAsset(verify);
            SpotifyRuntimeIdentityHost.ApplyFromManifest(verify.Manifest!);
            SpotifyRuntimeIdentityHost.ApplyRuntimeArchitecture(_asset.Config.Arch);
            SetSnapshot(ProvisioningOutcome.Ready, manifest: verify.Manifest, runtimeDir: verify.RuntimeDir,
                sig: verify.SignatureTrust, signatureInfo: verify.SignatureInfo,
                trustedByPinnedFingerprint: verify.TrustedByPinnedFingerprint);
            Event(WaveeLogLevel.Info, "runtime.download.ready", "Downloaded PlayPlay runtime verified and installed",
                WaveeLogField.Of("pack", entry.PackId),
                WaveeLogField.Of("runtimeDir", verify.RuntimeDir ?? ""),
                WaveeLogField.Of("signature", verify.SignatureTrust.ToString()),
                WaveeLogField.Of("signer", verify.SignatureInfo?.Subject ?? ""),
                WaveeLogField.Of("pinned", verify.TrustedByPinnedFingerprint));
        }
        else
        {
            SetSnapshot(verify.Outcome, verify.Detail, verify.Manifest, verify.RuntimeDir, verify.SignatureTrust,
                verify.NeedsUntrustedConfirmation, verify.SignatureInfo, verify.TrustedByPinnedFingerprint);
            Event(WaveeLogLevel.Warning, "runtime.download.failed", "Downloaded PlayPlay runtime failed verification",
                WaveeLogField.Of("pack", entry.PackId),
                WaveeLogField.Of("outcome", verify.Outcome.ToString()),
                WaveeLogField.Of("detail", verify.Detail ?? ""),
                WaveeLogField.Of("needsUntrusted", verify.NeedsUntrustedConfirmation));
        }
        return verify;
    }

    public PlaybackRuntimeStatus GetSnapshot() => _snapshot;

    void SetSnapshot(
        ProvisioningOutcome outcome,
        string? detail = null,
        PlayPlayRuntimeManifest? manifest = null,
        string? runtimeDir = null,
        SignatureTrust sig = SignatureTrust.Unknown,
        bool needsUntrusted = false,
        DigitalSignatureInfo? signatureInfo = null,
        bool trustedByPinnedFingerprint = false)
    {
        var packId = manifest?.PackId;
        if (string.IsNullOrEmpty(packId))
        {
            var stored = _settings.Get(WaveeSettings.PlaybackRuntimePackId);
            packId = string.IsNullOrEmpty(stored) ? null : stored;
        }
        _snapshot = new PlaybackRuntimeStatus(
            outcome,
            packId,
            manifest?.SpotifyVersion,
            manifest is not null && PlayPlayRuntimeManifest.TryParseArch(manifest.Arch, out var a) ? a : null,
            runtimeDir ?? _store.ActivePath,
            sig,
            needsUntrusted,
            signatureInfo,
            trustedByPinnedFingerprint);
        _status.SetProvisioning(outcome, detail);
        Event(WaveeLogLevel.Debug, "runtime.status", "PlayPlay runtime status changed",
            WaveeLogField.Of("outcome", outcome.ToString()),
            WaveeLogField.Of("pack", packId ?? ""),
            WaveeLogField.Of("version", manifest?.SpotifyVersion ?? ""),
            WaveeLogField.Of("arch", manifest?.Arch ?? ""),
            WaveeLogField.Of("signature", sig.ToString()),
            WaveeLogField.Of("signer", signatureInfo?.Subject ?? ""),
            WaveeLogField.Of("pinned", trustedByPinnedFingerprint),
            WaveeLogField.Of("needsUntrusted", needsUntrusted),
            WaveeLogField.Of("detail", detail ?? ""));
    }

    void Event(WaveeLogLevel level, string eventId, string message, params WaveeLogField[] fields) =>
        _structuredLog?.Event(level, "audio", eventId, message, fields: fields);
}
