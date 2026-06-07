using System.Runtime.InteropServices;

namespace FluentGpu.Pal.Windows;

/// <summary>Windows system-theme integration: the real accent color + DWM dark titlebar / Mica backdrop.</summary>
public static partial class Win32Theme
{
    private static readonly nint HKEY_CURRENT_USER = unchecked((nint)0x80000001u);
    private const uint RRF_RT_REG_DWORD = 0x00000010;

    // DWMWINDOWATTRIBUTE
    private const uint DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const uint DWMWA_SYSTEMBACKDROP_TYPE = 38;
    // DWM_SYSTEMBACKDROP_TYPE
    private const int DWMSBT_MAINWINDOW = 2;   // Mica
    private const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic

    private const uint RRF_RT_REG_BINARY = 0x00000008;

    [LibraryImport("advapi32.dll", EntryPoint = "RegGetValueW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegGetValueW(nint hkey, string subKey, string value, uint flags, nint pdwType, out uint pvData, ref uint pcbData);

    [LibraryImport("advapi32.dll", EntryPoint = "RegGetValueW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegGetValueBinaryW(nint hkey, string subKey, string value, uint flags, nint pdwType, [Out] byte[] pvData, ref uint pcbData);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetColorizationColor(out uint color, [MarshalAs(UnmanagedType.Bool)] out bool opaqueBlend);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, uint attr, in int value, uint size);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmExtendFrameIntoClientArea(nint hwnd, in MARGINS margins);

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS { public int cxLeftWidth, cxRightWidth, cyTopHeight, cyBottomHeight; }

    /// <summary>The Settings &gt; Colors accent (registry AccentColorMenu), falling back to the DWM colorization color.</summary>
    public static (byte R, byte G, byte B)? Accent()
    {
        uint data = 0, cb = 4;
        int rc = RegGetValueW(HKEY_CURRENT_USER,
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent", "AccentColorMenu",
            RRF_RT_REG_DWORD, 0, out data, ref cb);
        if (rc == 0)
            return ((byte)(data & 0xFF), (byte)((data >> 8) & 0xFF), (byte)((data >> 16) & 0xFF));   // 0xAABBGGRR

        if (DwmGetColorizationColor(out uint argb, out _) == 0)
            return ((byte)((argb >> 16) & 0xFF), (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));    // 0xAARRGGBB

        return null;
    }

    /// <summary>
    /// The OS-derived <c>SystemAccentColorLight2</c> shade — what WinUI uses for the dark-theme accent button fill
    /// (lighter than the base accent). Read from the AccentPalette blob: 8×RGBA entries, index 1 = Light2 (bytes 4..6).
    /// </summary>
    public static (byte R, byte G, byte B)? AccentLight2()
    {
        byte[] buf = new byte[32];
        uint cb = 32;
        int rc = RegGetValueBinaryW(HKEY_CURRENT_USER,
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent", "AccentPalette",
            RRF_RT_REG_BINARY, 0, buf, ref cb);
        if (rc == 0 && cb >= 8) return (buf[4], buf[5], buf[6]);
        return null;
    }

    /// <summary>Dark titlebar + the Mica system backdrop (Windows 11). Mica shows through transparent client pixels.</summary>
    public static void ApplyWindowMaterial(nint hwnd, bool dark, bool mica = true)
    {
        int d = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, in d, sizeof(int));
        int backdrop = mica ? DWMSBT_MAINWINDOW : DWMSBT_TRANSIENTWINDOW;
        DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, in backdrop, sizeof(int));

        if (mica)
        {
            // Sheet-of-glass: extend the DWM frame across the ENTIRE client area so the Mica system backdrop composites
            // behind the transparent (DirectComposition) client pixels. Without this, the system-backdrop attribute
            // applies to the frame but the transparent client shows the opaque window surface (the white pane), because
            // DWM only fills the backdrop where the frame is extended. Margins of -1 = full-window glass.
            MARGINS m = new() { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
            DwmExtendFrameIntoClientArea(hwnd, in m);
        }
    }
}
