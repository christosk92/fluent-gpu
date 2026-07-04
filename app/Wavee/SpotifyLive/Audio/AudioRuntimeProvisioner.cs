using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Backend.Spotify;

namespace Wavee.SpotifyLive.Audio;

/// <summary>Tiered provisioning: signed manifest → verified pack on disk. Off the startup path.</summary>
public sealed class AudioRuntimeProvisioner
{
    const string DefaultManifestUrl = "https://cproducts.dev/r/manifest.json";
    readonly IHttpExchange _http;
    readonly string _packRoot;
    readonly AudioRuntimeStatusService _status;
    readonly Action<string>? _log;
    RuntimeAsset? _asset;

    public AudioRuntimeProvisioner(IHttpExchange http, AudioRuntimeStatusService status, Action<string>? log = null,
        string? packRoot = null)
    {
        _http = http;
        _status = status;
        _log = log;
        _packRoot = packRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Wavee", "packs");
    }

    public RuntimeAsset? Current => _asset;

    public async Task<RuntimeAsset?> ProvisionAsync(CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(_packRoot);
            var manifest = await FetchManifestAsync(ct).ConfigureAwait(false);
            if (manifest?.Packs is not { Length: > 0 })
            {
                _status.SetProvisioning(ProvisioningOutcome.ManifestUnavailable);
                return null;
            }

            bool sawArchMatch = false;
            foreach (var pack in manifest.Packs)
            {
                if (!pack.Arch.Equals("X64", StringComparison.OrdinalIgnoreCase)) continue;
                sawArchMatch = true;
                var asset = await TryProvisionPackAsync(pack, ct).ConfigureAwait(false);
                if (asset is not null)
                {
                    _asset = asset;
                    _status.SetProvisioning(ProvisioningOutcome.Ready);
                    return asset;
                }
            }
            // Don't clobber a specific failure (e.g. HashMismatch) set inside TryProvisionPackAsync. Only classify the
            // "no usable pack" tail: no X64 pack at all → ArchUnsupported; attempted but none worked (no specific reason) →
            // PackDownloadFailed.
            if (!sawArchMatch)
                _status.SetProvisioning(ProvisioningOutcome.ArchUnsupported);
            else if (_status.Provisioning is ProvisioningOutcome.Ready or ProvisioningOutcome.NeverAttempted)
                _status.SetProvisioning(ProvisioningOutcome.PackDownloadFailed);
            return null;
        }
        catch (Exception ex)
        {
            _log?.Invoke("provision failed: " + ex.Message);
            _status.SetProvisioning(ProvisioningOutcome.PackDownloadFailed);
            return null;
        }
    }

    async Task<RuntimeManifest?> FetchManifestAsync(CancellationToken ct)
    {
        var resp = await _http.SendAsync(new HttpReq("GET", DefaultManifestUrl, new Dictionary<string, string>()), ct).ConfigureAwait(false);
        using (resp)
        {
            if (resp.Status != 200) return null;
            using var ms = new MemoryStream();
            await resp.Body.CopyToAsync(ms, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize(ms.ToArray(), RuntimeManifestJsonContext.Default.RuntimeManifest);
        }
    }

    async Task<RuntimeAsset?> TryProvisionPackAsync(RuntimeManifestPack pack, CancellationToken ct)
    {
        var id = SanitiseId(pack.Id);
        var dir = Path.Combine(_packRoot, id);
        var dllPath = Path.Combine(dir, "Spotify.dll");
        if (File.Exists(dllPath))
        {
            var existing = File.ReadAllBytes(dllPath);
            if (SHA256.HashData(existing).AsSpan().SequenceEqual(pack.ToConfig().Sha256))
                return new RuntimeAsset(dllPath, pack.ToConfig(), id);
        }

        var url = pack.Url;
        if (string.IsNullOrEmpty(url)) return null;
        var resp = await _http.SendAsync(new HttpReq("GET", url, new Dictionary<string, string>()), ct).ConfigureAwait(false);
        using (resp)
        {
            if (resp.Status != 200) { _status.SetProvisioning(ProvisioningOutcome.PackDownloadFailed); return null; }
            using var ms = new MemoryStream();
            await resp.Body.CopyToAsync(ms, ct).ConfigureAwait(false);
            var raw = ms.ToArray();
            var bytes = Decompress(pack.Compression, raw);
            if (!SHA256.HashData(bytes).AsSpan().SequenceEqual(Convert.FromHexString(pack.Sha256Hex)))
            {
                _status.SetProvisioning(ProvisioningOutcome.HashMismatch);
                return null;
            }
            Directory.CreateDirectory(dir);
            var tmp = dllPath + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
            File.Move(tmp, dllPath, overwrite: true);
            return new RuntimeAsset(dllPath, pack.ToConfig(), id);
        }
    }

    static byte[] Decompress(string? compression, byte[] raw) =>
        compression?.Equals("brotli", StringComparison.OrdinalIgnoreCase) == true
            ? DecompressBrotli(raw) : raw;

    static byte[] DecompressBrotli(byte[] raw)
    {
        using var input = new MemoryStream(raw);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        brotli.CopyTo(output);
        return output.ToArray();
    }

    static string SanitiseId(string id)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            id = id.Replace(c, '_');
        return id.Replace("..", "_");
    }
}
