using System.Security.Cryptography;
using Wavee;
using Wavee.Backend.Audio;

namespace Wavee.SpotifyLive.Audio.Runtime;

/// <summary>Enumerate, register, and persist the active PlayPlay runtime pointer.</summary>
public sealed class PlayPlayRuntimeStore
{
    readonly IAppSettings _settings;
    readonly Action<string>? _log;

    public PlayPlayRuntimeStore(IAppSettings settings, Action<string>? log = null)
    {
        _settings = settings;
        _log = log;
    }

    public IReadOnlyList<string> EnumerateInstalled()
    {
        var dirs = new List<string>();
        foreach (var root in new[] { PlayPlayRuntimePaths.CanonicalStoreRoot, PlayPlayRuntimePaths.BundledStoreRoot })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dll in Directory.EnumerateFiles(root, PlayPlayRuntimePaths.DllFileName, SearchOption.AllDirectories))
            {
                var dir = Path.GetDirectoryName(dll);
                if (dir is not null && PlayPlayRuntimePaths.IsRuntimeDirectory(dir))
                    dirs.Add(dir);
            }
        }
        return dirs;
    }

    public PlayPlayRuntimeVerifyResult Register(string sourceDir, bool allowUntrustedSignature)
    {
        sourceDir = Path.GetFullPath(sourceDir);
        if (File.Exists(sourceDir) && sourceDir.EndsWith(PlayPlayRuntimePaths.DllFileName, StringComparison.OrdinalIgnoreCase))
            sourceDir = Path.GetDirectoryName(sourceDir)!;

        var srcDll = Path.Combine(sourceDir, PlayPlayRuntimePaths.DllFileName);
        var srcManifest = Path.Combine(sourceDir, PlayPlayRuntimePaths.ManifestFileName);

        PlayPlayRuntimeManifest manifest;
        bool haveSourceManifest = File.Exists(srcManifest);
        if (haveSourceManifest)
        {
            // The folder already carries a manifest — verify it as-is.
            var verify = PlayPlayRuntimeVerifier.VerifyDirectory(sourceDir, allowUntrustedSignature, _log);
            if (!verify.Ok) return verify;
            manifest = verify.Manifest!;
        }
        else
        {
            // Manifest-less: recognize a KNOWN supported build by the DLL's content hash and synthesize the manifest,
            // so the user only has to supply the bare Spotify.dll (no hand-authored playplay-runtime.json).
            if (!File.Exists(srcDll))
                return PlayPlayRuntimeVerifyResult.Fail(ProvisioningOutcome.RuntimeUnavailable, "no Spotify.dll in the selected folder");
            string hashHex;
            try { hashHex = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(srcDll))); }
            catch (Exception ex) { return PlayPlayRuntimeVerifyResult.Fail(ProvisioningOutcome.RuntimeUnavailable, ex.Message); }
            if (!PlayPlayKnownPacks.TryMatch(hashHex, out manifest!))
                return PlayPlayRuntimeVerifyResult.Fail(ProvisioningOutcome.NoSupportedPack,
                    "this Spotify.dll isn’t a recognized supported build");
            _log?.Invoke($"store: matched known pack {manifest.PackId} by hash");
        }

        if (!PlayPlayRuntimeManifest.TryParseArch(manifest.Arch, out var arch))
            return PlayPlayRuntimeVerifyResult.Fail(ProvisioningOutcome.RuntimeUnavailable, "invalid arch in manifest");

        var destDir = PlayPlayRuntimePaths.RuntimeDir(manifest.AppVersion, arch);
        Directory.CreateDirectory(destDir);

        var destDll = Path.Combine(destDir, PlayPlayRuntimePaths.DllFileName);
        var destManifest = Path.Combine(destDir, PlayPlayRuntimePaths.ManifestFileName);

        AtomicCopy(srcDll, destDll);
        if (haveSourceManifest) AtomicCopy(srcManifest, destManifest);
        else WriteManifestAtomic(destManifest, manifest);   // synthesized from the known-pack table
        _log?.Invoke($"store: registered {manifest.PackId} → {destDir}");

        PersistPointer(destDir, manifest.PackId);
        return PlayPlayRuntimeVerifier.VerifyDirectory(destDir, allowUntrustedSignature, _log);
    }

    static void WriteManifestAtomic(string path, PlayPlayRuntimeManifest manifest)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(manifest, PlayPlayManifestJson.Default.PlayPlayRuntimeManifest);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(System.Text.Encoding.UTF8.GetBytes(json));
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Copy <paramref name="src"/> onto <paramref name="dest"/> atomically: stage into a sibling
    /// <c>.tmp</c> then rename over the target (same-volume <see cref="File.Move(string,string,bool)"/> is atomic), so a
    /// concurrent reader/verifier never observes a half-written file.</summary>
    static void AtomicCopy(string src, string dest)
    {
        var tmp = dest + ".tmp";
        try
        {
            File.Copy(src, tmp, overwrite: true);
            File.Move(tmp, dest, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                try { File.Delete(tmp); } catch { /* best-effort cleanup of the staging file */ }
            }
        }
    }

    public void PersistPointer(string runtimeDir, string packId)
    {
        _settings.Set(WaveeSettings.PlaybackRuntimePath, runtimeDir);
        _settings.Set(WaveeSettings.PlaybackRuntimePackId, packId);
        _log?.Invoke($"store: active pointer → {runtimeDir} ({packId})");
    }

    public void ClearPointer()
    {
        _settings.Set(WaveeSettings.PlaybackRuntimePath, "");
        _settings.Set(WaveeSettings.PlaybackRuntimePackId, "");
    }

    public string? ActivePath
    {
        get
        {
            var p = _settings.Get(WaveeSettings.PlaybackRuntimePath);
            return string.IsNullOrEmpty(p) ? null : p;
        }
    }
}
