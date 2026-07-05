using System.Runtime.InteropServices;
using Wavee;
using Wavee.Backend.Audio;

namespace Wavee.SpotifyLive.Audio.Runtime;

public enum PlayPlayRuntimeLocateSource
{
    None = 0,
    EnvironmentOverride,
    Settings,
    CanonicalStore,
    Bundled,
}

public sealed record PlayPlayRuntimeLocateCandidate(
    string RuntimeDir,
    string DllPath,
    string ManifestPath,
    PlayPlayRuntimeLocateSource Source);

/// <summary>Why <see cref="PlayPlayRuntimeLocator.Locate"/> rejected every present runtime — lets the UI distinguish
/// "nothing installed" from "a runtime is installed but unusable here" instead of collapsing both to "missing."</summary>
public enum PlayPlayRuntimeLocateReason
{
    None = 0,
    WrongArch,
    UnsupportedAlgorithm,
}

/// <summary>The best usable runtime, or (when none) the honest reason a present-but-unusable one was filtered out.</summary>
public readonly record struct PlayPlayRuntimeLocateResult(
    PlayPlayRuntimeLocateCandidate? Candidate,
    PlayPlayRuntimeLocateReason Reason)
{
    public bool Found => Candidate is not null;
}

/// <summary>Automatic runtime resolution — never reaches installed Spotify without explicit user action.</summary>
public static class PlayPlayRuntimeLocator
{
    public static IReadOnlyList<PlayPlayRuntimeLocateCandidate> EnumerateCandidates(IAppSettings? settings, Action<string>? log = null)
    {
        var list = new List<PlayPlayRuntimeLocateCandidate>();

        if (Environment.GetEnvironmentVariable("WAVEE_PLAYPLAY_SPOTIFY_DLL") is { Length: > 0 } envDll)
        {
            var dll = envDll.Trim();
            var manifest = Environment.GetEnvironmentVariable("WAVEE_PLAYPLAY_RUNTIME_MANIFEST") is { Length: > 0 } m
                ? m.Trim()
                : PlayPlayRuntimePaths.FindManifestForDll(dll);
            if (manifest is not null && File.Exists(dll))
            {
                var dir = Path.GetDirectoryName(dll)!;
                list.Add(new(dir, dll, manifest, PlayPlayRuntimeLocateSource.EnvironmentOverride));
                log?.Invoke($"locate: env override {dll}");
            }
        }

        if (settings is not null)
        {
            var path = settings.Get(WaveeSettings.PlaybackRuntimePath);
            if (!string.IsNullOrEmpty(path) && PlayPlayRuntimePaths.IsRuntimeDirectory(path))
            {
                list.Add(new(path,
                    Path.Combine(path, PlayPlayRuntimePaths.DllFileName),
                    Path.Combine(path, PlayPlayRuntimePaths.ManifestFileName),
                    PlayPlayRuntimeLocateSource.Settings));
                log?.Invoke($"locate: settings {path}");
            }
        }

        ScanStore(PlayPlayRuntimePaths.CanonicalStoreRoot, PlayPlayRuntimeLocateSource.CanonicalStore, list, log);
        ScanStore(PlayPlayRuntimePaths.BundledStoreRoot, PlayPlayRuntimeLocateSource.Bundled, list, log);

        return list;
    }

    /// <summary>Pick the best usable runtime for this process, or report why a present one was rejected.</summary>
    public static PlayPlayRuntimeLocateResult Locate(IAppSettings? settings, Action<string>? log = null)
    {
        var reason = PlayPlayRuntimeLocateReason.None;
        foreach (var c in EnumerateCandidates(settings, log))
        {
            if (!File.Exists(c.DllPath) || !File.Exists(c.ManifestPath)) continue;
            PlayPlayRuntimeManifest? manifest;
            try
            {
                var json = File.ReadAllText(c.ManifestPath);
                manifest = System.Text.Json.JsonSerializer.Deserialize(json, PlayPlayManifestJson.Default.PlayPlayRuntimeManifest);
            }
            catch { continue; }
            if (manifest is null) continue;
            if (!PlayPlayRuntimeManifest.TryParseArch(manifest.Arch, out var arch)) continue;
            if (arch != RuntimeInformation.ProcessArchitecture)
            {
                if (reason == PlayPlayRuntimeLocateReason.None) reason = PlayPlayRuntimeLocateReason.WrongArch;
                continue;
            }
            if (!PlayPlayRuntimeVerifier.SupportedAlgorithms.Contains(manifest.AlgorithmVersion, StringComparer.Ordinal))
            {
                if (reason != PlayPlayRuntimeLocateReason.WrongArch) reason = PlayPlayRuntimeLocateReason.UnsupportedAlgorithm;
                continue;
            }
            log?.Invoke($"locate: selected {c.Source} → {c.RuntimeDir}");
            return new(c, PlayPlayRuntimeLocateReason.None);
        }
        log?.Invoke($"locate: no usable candidate (reason={reason})");
        return new(null, reason);
    }

    public static PlayPlayRuntimeLocateCandidate? FindBest(IAppSettings? settings, Action<string>? log = null)
        => Locate(settings, log).Candidate;

    static void ScanStore(string root, PlayPlayRuntimeLocateSource source, List<PlayPlayRuntimeLocateCandidate> list, Action<string>? log)
    {
        if (!Directory.Exists(root)) return;
        foreach (var dll in Directory.EnumerateFiles(root, PlayPlayRuntimePaths.DllFileName, SearchOption.AllDirectories))
        {
            var dir = Path.GetDirectoryName(dll);
            if (dir is null) continue;
            var manifest = Path.Combine(dir, PlayPlayRuntimePaths.ManifestFileName);
            if (!File.Exists(manifest)) continue;
            if (list.Any(c => string.Equals(c.RuntimeDir, dir, StringComparison.OrdinalIgnoreCase))) continue;
            // Include regardless of arch/algorithm — Locate() decides suitability and reports why a
            // present-but-unusable runtime (wrong arch / unsupported algorithm) was rejected, instead of
            // silently swallowing it so the UI can't tell "nothing installed" from "wrong runtime present."
            try
            {
                var json = File.ReadAllText(manifest);
                var m = System.Text.Json.JsonSerializer.Deserialize(json, PlayPlayManifestJson.Default.PlayPlayRuntimeManifest);
                if (m is null) continue;
            }
            catch { continue; }
            list.Add(new(dir, dll, manifest, source));
            log?.Invoke($"locate: scan {source} → {dir}");
        }
    }

    /// <summary>Explicit user action only — never called from automatic resolution.</summary>
    public static PlayPlayRuntimeLocateCandidate? FromInstalledSpotify()
    {
        var dll = PlayPlayRuntimePaths.InstalledSpotifyDll;
        if (!File.Exists(dll)) return null;
        var manifest = PlayPlayRuntimePaths.FindManifestForDll(dll);
        if (manifest is null) return null;
        return new(Path.GetDirectoryName(dll)!, dll, manifest, PlayPlayRuntimeLocateSource.None);
    }
}
