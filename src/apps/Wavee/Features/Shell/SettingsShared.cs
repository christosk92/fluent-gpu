using System;
using System.IO;
using FluentGpu.Controls;
using FluentGpu.Localization;

namespace Wavee;

/// <summary>Cross-tab settings helpers shared by <see cref="SettingsPage"/> and <see cref="DiagnosticsPanel"/>.</summary>
static class SettingsShared
{
    public static string AppDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wavee");

    public static void OpenFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path)) path = Path.GetDirectoryName(path) ?? path;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + path + "\"")
            { UseShellExecute = false });
        }
        catch { /* best-effort — a missing Explorer/path must not throw into the UI */ }
    }

    public static void Confirm(IOverlayService? overlay, string title, string body, string primaryText, Action onConfirm)
    {
        if (overlay is null) { onConfirm(); return; }
        ContentDialog.Show(overlay, d =>
        {
            d.Title = title;
            d.Message = body;
            d.PrimaryText = primaryText;
            d.CloseText = Loc.Get(Strings.Auth.Cancel);
            d.DefaultButton = ContentDialog.DefaultBtn.Close;
            d.PrimaryClick = onConfirm;
        });
    }
}
