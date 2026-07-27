using System;
using System.Diagnostics;
using System.IO;

namespace Wavee;

static class ShellOpen
{
    public static void OpenFolderOf(string path)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(dir)) return;
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch { }
    }

    /// <summary>Open Explorer with <paramref name="path"/> SELECTED (the Win11 "Show in folder" affordance), falling back
    /// to just opening the containing folder when the file is gone. Best-effort by design: a missing Explorer, a denied
    /// path or an offline share must never throw into the UI thread that invoked a menu row.</summary>
    public static void RevealInExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path))
            {
                // /select, takes ONE argument and it must be quoted as a whole — the comma is part of the switch.
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = false });
                return;
            }
        }
        catch { /* fall through to the folder open below */ }
        OpenFolderOf(path);
    }
}

