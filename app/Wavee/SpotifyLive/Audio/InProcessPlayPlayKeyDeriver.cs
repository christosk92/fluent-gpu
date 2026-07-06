#if WAVEE_PLAYPLAY_LOCAL
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.PlayPlay;

namespace Wavee.SpotifyLive.Audio;

public sealed class InProcessPlayPlayKeyDeriver : IPlayPlayKeyDeriver, IPlayPlayCdnDecryptorFactory, IDisposable
{
    readonly PlayPlayRuntime _runtime;
    readonly AudioRuntimeStatusService _status;
    readonly Action<string>? _log;

    InProcessPlayPlayKeyDeriver(PlayPlayRuntime runtime, AudioRuntimeStatusService status, Action<string>? log)
    {
        _runtime = runtime;
        _status = status;
        _log = log;
    }

    public RuntimeAsset Asset => _runtime.Asset;

    public static InProcessPlayPlayKeyDeriver? TryCreate(RuntimeAsset asset, AudioRuntimeStatusService status, Action<string>? log = null)
    {
        log?.Invoke($"PlayPlay deriver init start pack={asset.PackId} version={asset.Config.Version} arch={asset.Config.Arch} dll={asset.PackPath}");
        if (!PlayPlayRuntime.TryCreate(asset, out var runtime, log) || runtime is null)
        {
            status.SetProvisioning(ProvisioningOutcome.RuntimeUnavailable, "PlayPlay emulator init failed");
            log?.Invoke($"PlayPlay deriver init FAILED pack={asset.PackId} version={asset.Config.Version} arch={asset.Config.Arch}");
            return null;
        }

        status.SetProvisioning(ProvisioningOutcome.Ready);
        log?.Invoke($"PlayPlay deriver ready pack={asset.PackId} algorithm={runtime.AlgorithmId}");
        return new InProcessPlayPlayKeyDeriver(runtime, status, log);
    }

    public Task<PlayPlayDeriveResult> DeriveAsync(ReadOnlyMemory<byte> obfuscatedKey, ReadOnlyMemory<byte> contentId,
        PlayPlayConfig config, string spotifyDllPath, string correlationId, CancellationToken ct = default)
        => DeriveAsync(obfuscatedKey, contentId, config, spotifyDllPath, correlationId, default, default, default, ct);

    public Task<PlayPlayDeriveResult> DeriveAsync(ReadOnlyMemory<byte> obfuscatedKey, ReadOnlyMemory<byte> contentId,
        PlayPlayConfig config, string spotifyDllPath, string correlationId, ReadOnlyMemory<byte> playPlayAux,
        ReadOnlyMemory<byte> licenseRaw = default, ReadOnlyMemory<byte> licenseRequest = default, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _log?.Invoke($"PlayPlay derive start file={correlationId} obf={obfuscatedKey.Length}B contentId={contentId.Length}B aux={playPlayAux.Length}B raw={licenseRaw.Length}B request={licenseRequest.Length}B");
        var result = _runtime.Derive(obfuscatedKey, contentId, correlationId, playPlayAux, licenseRaw, licenseRequest);
        if (!result.Ok && result.Reason != AudioKeyFailureReason.None)
        {
            _status.SetKeyFailure(result.Reason, result.Detail);
            _log?.Invoke($"PlayPlay derive FAILED file={correlationId} reason={result.Reason} detail={result.Detail ?? ""}");
        }
        else
        {
            _log?.Invoke($"PlayPlay derive OK file={correlationId} aes={result.Key.Length}B redacted nativeSeed={result.NativeCdnSeed.Length}B slab={result.DerivedSlab.Length}B");
        }
        return Task.FromResult(result);
    }

    public CdnDecryptor? CreateCdnDecryptor(ReadOnlyMemory<byte> nativeCdnSeed)
    {
        var decryptor = _runtime.CreateCdnDecryptor(nativeCdnSeed);
        _log?.Invoke(decryptor is null
            ? $"PlayPlay CDN decryptor unavailable seed={nativeCdnSeed.Length}B"
            : $"PlayPlay CDN decryptor ready seed={nativeCdnSeed.Length}B redacted");
        return decryptor;
    }

    public void Dispose() => _runtime.Dispose();
}
#endif
