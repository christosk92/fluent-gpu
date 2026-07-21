using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Audio;
using Wavee.SpotifyLive.Audio.Runtime;

namespace Wavee.SpotifyLive.Audio;

/// <summary>Seam over the PlayPlay runtime provisioner so the UI + live-session host compile with the concrete client
/// physically absent. The real PlayPlayRuntimeProvisioner implements it when the private client is present;
/// otherwise <see cref="NullPlayPlayProvisioner"/> reports "unavailable" for every operation.</summary>
public interface IPlayPlayProvisioner
{
    PlaybackRuntimeStatus GetSnapshot();
    RuntimeAsset? CurrentAsset { get; }
    Task<RuntimeAsset?> EnsureRuntimeAsync(CancellationToken ct = default, bool allowUntrustedSignature = false);
    bool TryRegisterRuntime(string sourceDir, bool allowUntrustedSignature = false);
    Task<PlayPlayRuntimeCatalog?> FetchCatalogAsync(CancellationToken ct = default);
    (PlayPlayRuntimeCatalogEntry? Best, bool AnyForOtherArch) SelectBest(PlayPlayRuntimeCatalog catalog);
    IReadOnlyList<PlayPlayRuntimeCatalogEntry> SupportedPacks(PlayPlayRuntimeCatalog catalog);
    Task<PlayPlayRuntimeVerifyResult> DownloadAndInstallAsync(
        PlayPlayRuntimeCatalogEntry entry,
        bool allowUntrustedSignature,
        IProgress<PlayPlayDownloadProgress>? progress,
        CancellationToken ct = default);
    void ClearActivePointer();
}

/// <summary>The "client absent" provisioner: every operation reports the runtime as unavailable. Used when Wavee is built
/// without the local PlayPlay client (<c>WAVEE_PLAYPLAY_LOCAL</c> undefined).</summary>
public sealed class NullPlayPlayProvisioner : IPlayPlayProvisioner
{
    public static readonly NullPlayPlayProvisioner Instance = new();

    NullPlayPlayProvisioner() { }

    public PlaybackRuntimeStatus GetSnapshot() => new(ProvisioningOutcome.RuntimeUnavailable);
    public RuntimeAsset? CurrentAsset => null;
    public Task<RuntimeAsset?> EnsureRuntimeAsync(CancellationToken ct = default, bool allowUntrustedSignature = false)
        => Task.FromResult<RuntimeAsset?>(null);
    public bool TryRegisterRuntime(string sourceDir, bool allowUntrustedSignature = false) => false;
    public Task<PlayPlayRuntimeCatalog?> FetchCatalogAsync(CancellationToken ct = default)
        => Task.FromResult<PlayPlayRuntimeCatalog?>(null);
    public (PlayPlayRuntimeCatalogEntry? Best, bool AnyForOtherArch) SelectBest(PlayPlayRuntimeCatalog catalog)
        => (null, false);
    public IReadOnlyList<PlayPlayRuntimeCatalogEntry> SupportedPacks(PlayPlayRuntimeCatalog catalog)
        => Array.Empty<PlayPlayRuntimeCatalogEntry>();
    public Task<PlayPlayRuntimeVerifyResult> DownloadAndInstallAsync(
        PlayPlayRuntimeCatalogEntry entry,
        bool allowUntrustedSignature,
        IProgress<PlayPlayDownloadProgress>? progress,
        CancellationToken ct = default)
        => Task.FromResult(PlayPlayRuntimeVerifyResult.Fail(
            ProvisioningOutcome.RuntimeUnavailable, "install the local PlayPlay package"));
    public void ClearActivePointer() { }
}
