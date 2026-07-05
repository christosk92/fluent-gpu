using System.Runtime.InteropServices;

namespace Wavee.SpotifyLive.Audio.Runtime;

/// <summary>Canonical on-disk layout for provisioned PlayPlay runtimes.</summary>
static class PlayPlayRuntimePaths
{
    public const string DllFileName = "Spotify.dll";
    public const string ManifestFileName = "playplay-runtime.json";

    public static string CanonicalStoreRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wavee", "playplay", "runtimes");

    public static string BundledStoreRoot =>
        Path.Combine(AppContext.BaseDirectory, "runtimes", "playplay");

    public static string RuntimeDir(string appVersion, Architecture arch) =>
        Path.Combine(CanonicalStoreRoot, appVersion, ArchFolder(arch));

    public static string InstalledSpotifyDll =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Spotify", DllFileName);

    public static string ArchFolder(Architecture arch) => arch switch
    {
        Architecture.Arm64 => "arm64",
        Architecture.X64 => "x64",
        _ => arch.ToString().ToLowerInvariant(),
    };

    public static bool IsRuntimeDirectory(string dir) =>
        File.Exists(Path.Combine(dir, DllFileName)) && File.Exists(Path.Combine(dir, ManifestFileName));

    public static string? FindManifestForDll(string dllPath)
    {
        var dir = Path.GetDirectoryName(dllPath);
        if (dir is null) return null;
        var sibling = Path.Combine(dir, ManifestFileName);
        return File.Exists(sibling) ? sibling : null;
    }
}
