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
}

