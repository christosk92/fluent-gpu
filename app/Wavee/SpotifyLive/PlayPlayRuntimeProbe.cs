using System.Text.Json;
using Wavee.Backend.Audio;
using Wavee.SpotifyLive.Audio;
using Wavee.SpotifyLive.Audio.Runtime;

namespace Wavee.SpotifyLive;

static class PlayPlayRuntimeProbe
{
    public static int RunStatus(string[] args, Action<string> log)
    {
        var settings = AppDataSettings.ForUnpackaged("Wavee", "Wavee");
        var candidates = PlayPlayRuntimeLocator.EnumerateCandidates(settings, log);
        log($"candidates: {candidates.Count}");
        foreach (var c in candidates)
        {
            log($"  [{c.Source}] {c.RuntimeDir}");
            var verify = PlayPlayRuntimeVerifier.Verify(c.DllPath, c.ManifestPath, allowUntrustedSignature: false, log);
            log($"    → {verify.Outcome} trusted={verify.SignatureTrust}");
        }
        var best = PlayPlayRuntimeLocator.FindBest(settings, log);
        log(best is null ? "active: none" : $"active: {best.RuntimeDir}");
        return 0;
    }

    public static int RunRegister(string dir, Action<string> log)
    {
        var settings = AppDataSettings.ForUnpackaged("Wavee", "Wavee");
        var status = new AudioRuntimeStatusService();
        var provisioner = new PlayPlayRuntimeProvisioner(settings, status, log);
        return provisioner.TryRegisterRuntime(dir) ? 0 : 1;
    }

#if WAVEE_PLAYPLAY_LOCAL
    public static int RunCheck(string[] args, Action<string> log)
    {
        var settings = AppDataSettings.ForUnpackaged("Wavee", "Wavee");
        var status = new AudioRuntimeStatusService();
        var provisioner = new PlayPlayRuntimeProvisioner(settings, status, log);
        var asset = provisioner.EnsureRuntime();
        if (asset is null)
            return 1;

        if (!Wavee.PlayPlay.PlayPlayRuntime.TryCreate(asset, out var runtime, log) || runtime is null)
            return 1;

        using (runtime)
        {
            log("PlayPlay runtime: " + runtime.AlgorithmId);
            log("Spotify.dll: " + runtime.Asset.PackPath);

            var idx = Array.IndexOf(args, "--playplay-runtime-check");
            if (idx < 0 || idx + 2 >= args.Length || args[idx + 1].StartsWith("--") || args[idx + 2].StartsWith("--"))
            {
                log("usage: --playplay-runtime-check <fileIdHex40> <obfuscatedKeyHex32> [auxHex] [licenseRawBase64] [requestBase64]");
                return 0;
            }

            var fileId = Convert.FromHexString(args[idx + 1]);
            var obfuscated = Convert.FromHexString(args[idx + 2]);
            var aux = idx + 3 < args.Length && !args[idx + 3].StartsWith("--")
                ? Convert.FromHexString(args[idx + 3])
                : Array.Empty<byte>();
            var raw = idx + 4 < args.Length && !args[idx + 4].StartsWith("--")
                ? Convert.FromBase64String(args[idx + 4])
                : Array.Empty<byte>();
            var request = idx + 5 < args.Length && !args[idx + 5].StartsWith("--")
                ? Convert.FromBase64String(args[idx + 5])
                : Array.Empty<byte>();

            if (fileId.Length != 20 || obfuscated.Length != 16)
            {
                log("file id must be 20 bytes and obfuscated key must be 16 bytes");
                return 2;
            }

            var result = runtime.Derive(
                obfuscated,
                fileId.AsMemory(0, 16),
                Convert.ToHexStringLower(fileId),
                aux,
                raw,
                request);

            log("derive: " + (result.Ok ? "ok" : "failed " + result.Reason));
            if (!string.IsNullOrWhiteSpace(result.Detail))
                log("detail: " + result.Detail);
            if (!result.Key.IsEmpty)
                log("aes: " + result.Key.Length + "B redacted");
            if (!result.NativeCdnSeed.IsEmpty)
                log("native-cdn-seed: " + result.NativeCdnSeed.Length + "B redacted");

            return result.Ok ? 0 : 3;
        }
    }
#endif
}
