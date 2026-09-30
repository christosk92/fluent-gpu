using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using FluentGpu.ScrollLab.Record;
using FluentGpu.Scroll.Diag;
using FluentGpu.Scroll.Motion;

namespace FluentGpu.ScrollLab.Tuning;

/// <summary>One pickable feel: a built-in <see cref="FeelProfiles"/> entry or a saved JSON preset.</summary>
public sealed record FeelPreset(string Name, MotionFeel Feel, bool BuiltIn, string? Path);

/// <summary>
/// Feel persistence (scroll-lab plan §3 Tuning): the built-in <see cref="FeelProfiles.All"/> plus saved presets under
/// <c>%LOCALAPPDATA%\FluentGpu\ScrollLab\presets\*.json</c>, each exactly <c>ScrollTunables.ToJson</c>'s
/// shape (NativeAOT-safe hand-written JSON — <see cref="ScrollTunables.TryFromJson"/> reads it back). The list is cached
/// and re-read only by <see cref="Refresh"/> (after a save/import), so bound readouts never touch the disk per frame.
/// </summary>
public static class FeelStore
{
    public static string PresetsDir => Path.Combine(SessionWriter.Root, "presets");

    private static IReadOnlyList<FeelPreset>? s_cache;

    /// <summary>Built-ins first (fixed order), then saved presets by name.</summary>
    public static IReadOnlyList<FeelPreset> List() => s_cache ??= Load();

    public static void Refresh() => s_cache = null;

    /// <summary>The named preset's feel, or null when no such preset exists.</summary>
    public static MotionFeel? PresetFeel(string name)
    {
        var list = List();
        for (int i = 0; i < list.Count; i++) if (list[i].Name == name) return list[i].Feel;
        return null;
    }

    /// <summary>Saves <paramref name="feel"/> as preset <paramref name="name"/> (file-name-sanitized; a built-in name is
    /// suffixed so it never shadows the profile). Returns the stored name.</summary>
    public static string SaveAs(string name, in MotionFeel feel)
    {
        string clean = Sanitize(name);
        foreach (var (builtIn, _) in FeelProfiles.All)
            if (string.Equals(builtIn, clean, StringComparison.OrdinalIgnoreCase)) { clean += "-custom"; break; }
        Directory.CreateDirectory(PresetsDir);
        File.WriteAllText(Path.Combine(PresetsDir, clean + ".json"), ScrollTunables.ToJson(feel), new UTF8Encoding(false));
        Refresh();
        return clean;
    }

    public static void Export(string path, in MotionFeel feel)
        => File.WriteAllText(path, ScrollTunables.ToJson(feel), new UTF8Encoding(false));

    /// <summary>Reads a feel JSON file; false on a missing/malformed file (never throws).</summary>
    public static bool TryImport(string path, out MotionFeel feel)
    {
        feel = FeelProfiles.Standard;
        try
        {
            return File.Exists(path) && ScrollTunables.TryFromJson(File.ReadAllText(path), out feel);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static IReadOnlyList<FeelPreset> Load()
    {
        var list = new List<FeelPreset>();
        foreach (var (n, f) in FeelProfiles.All) list.Add(new FeelPreset(n, f, true, null));
        try
        {
            if (Directory.Exists(PresetsDir))
            {
                var files = Directory.GetFiles(PresetsDir, "*.json");
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                foreach (var file in files)
                    if (TryImport(file, out var feel))
                        list.Add(new FeelPreset(Path.GetFileNameWithoutExtension(file), feel, false, file));
            }
        }
        catch (Exception)
        {
            // An unreadable presets folder leaves the built-ins.
        }
        return list;
    }

    private static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        var bad = Path.GetInvalidFileNameChars();
        foreach (char c in name.Trim()) sb.Append(Array.IndexOf(bad, c) >= 0 ? '_' : c);
        return sb.Length == 0 ? "feel" : sb.ToString();
    }
}
