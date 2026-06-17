using System.Runtime.InteropServices;
using FluentGpu.Pal;

namespace FluentGpu.Pal.Windows;

/// <summary>Windows system-theme integration: the real accent color + DWM dark titlebar / Mica backdrop.</summary>
public static partial class Win32Theme
{
    private static readonly nint HKEY_CURRENT_USER = unchecked((nint)0x80000001u);
    private const uint RRF_RT_REG_DWORD = 0x00000010;

    // DWMWINDOWATTRIBUTE
    private const uint DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const uint DWMWA_SYSTEMBACKDROP_TYPE = 38;
    // DWM_SYSTEMBACKDROP_TYPE
    private const int DWMSBT_MAINWINDOW = 2;   // Mica (WinUI MicaKind.Base)
    private const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic
    private const int DWMSBT_TABBEDWINDOW = 4; // Mica Alt (WinUI MicaKind.BaseAlt — the flatter, neutral File-Explorer tint)
    private const int DWMWCP_ROUND = 2;
    private const int DWMWCP_DONOTROUND = 1;   // square window: the engine/composition draws the rounded chrome, not DWM

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

    // Undocumented user32 host-backdrop enable. Windows.UI.Composition's CreateHostBackdropBrush only samples the
    // content behind a Win32 window if that window is host-backdrop-enabled via SetWindowCompositionAttribute with an
    // ACCENT_POLICY of ACCENT_ENABLE_HOSTBACKDROP. Without it the brush is EMPTY and renders dark/transparent — which is
    // why the windowed-popup acrylic looked dark. This is the UWP-era mechanism that the lifted ContentExternalBackdropLink
    // replaced for WinAppSDK; but we use SYSTEM composition (CreateDesktopWindowTarget), where DWM already has access to
    // everything behind the window, so this is the no-WinAppSDK way to feed a CLIPPABLE host-backdrop brush.
    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_ENABLE_HOSTBACKDROP = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct ACCENT_POLICY { public int AccentState; public int AccentFlags; public uint GradientColor; public int AnimationId; }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct WINDOWCOMPOSITIONATTRIBDATA { public int Attrib; public void* pvData; public uint cbData; }

    [LibraryImport("user32.dll")]
    private static unsafe partial int SetWindowCompositionAttribute(nint hwnd, WINDOWCOMPOSITIONATTRIBDATA* data);

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

    /// <summary>Dark titlebar + the Mica system backdrop (Windows 11). Mica shows through transparent client pixels.
    /// <paramref name="customFrame"/> = the engine draws the titlebar (WindowDesc.CustomFrame): the frame extension is
    /// ZERO — the Windows Terminal Mica rule (`_useMica ? 0`). Any non-zero margin lets DWM paint caption visuals over
    /// the client (a -1 sheet-of-glass composites its OWN min/max/close — the "double caption buttons" bug; even a 1px
    /// sliver draws a DWM strip and re-anchors the Win11 snap flyout off the extended frame). The Mica backdrop fills
    /// the whole window from DWMWA_SYSTEMBACKDROP_TYPE alone — no frame extension needed.</summary>
    /// <param name="micaAlt">true = Mica <b>BaseAlt</b> (DWMSBT_TABBEDWINDOW, the flatter File-Explorer tint, matching
    /// WaveeMusic's <c>MicaBackdrop Kind="BaseAlt"</c>); false = Mica Base (DWMSBT_MAINWINDOW). Ignored when mica is false.</param>
    public static void ApplyWindowMaterial(nint hwnd, bool dark, bool mica = true, bool customFrame = false, bool micaAlt = false)
    {
        int d = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, in d, sizeof(int));
        int backdrop = mica ? (micaAlt ? DWMSBT_TABBEDWINDOW : DWMSBT_MAINWINDOW) : DWMSBT_TRANSIENTWINDOW;
        DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, in backdrop, sizeof(int));

        if (customFrame)
        {
            // All-zero margins (Terminal's Mica case): DWM owns no caption visuals; snap flyout anchors purely from
            // the engine's WM_NCHITTEST regions.
            MARGINS m = new();
            DwmExtendFrameIntoClientArea(hwnd, in m);
        }
        else if (mica)
        {
            // Sheet-of-glass: extend the DWM frame across the ENTIRE client area so the Mica system backdrop composites
            // behind the transparent (DirectComposition) client pixels. Without this, the system-backdrop attribute
            // applies to the frame but the transparent client shows the opaque window surface (the white pane), because
            // DWM only fills the backdrop where the frame is extended. Margins of -1 = full-window glass. (Safe here:
            // with the STANDARD frame the DWM-drawn caption buttons land inside the real OS titlebar, not the client.)
            MARGINS m = new() { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
            DwmExtendFrameIntoClientArea(hwnd, in m);
        }
    }

    /// <summary>Apply the OS material used by windowed popup HWNDs. WinUI MenuFlyout uses a transparent presenter over
    /// <c>AcrylicBackgroundFillColorDefaultBackdrop</c> (DesktopAcrylicBackdrop); for Win32 that maps to DWM's transient
    /// window backdrop, with the client glass-extended so transparent DirectComposition pixels reveal it.</summary>
    public static void ApplyPopupMaterial(nint hwnd, bool dark, PopupWindowMaterial material)
    {
        int d = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, in d, sizeof(int));
        if (material != PopupWindowMaterial.TransientAcrylic) return;

        // The popup HWND is a near-bare composition VIEWPORT — WinUI does the same for windowed popups
        // (popup-system-backdrop.md): border, shadow, rounded corners and acrylic are all composition visuals under one
        // animated root so they reveal WITH the menu. DWM's *rounded-window* chrome (the rounded border + its drop
        // shadow) is drawn full-size and can't follow the open clip, so we turn rounding OFF (DONOTROUND): the engine
        // draws the rounded plate + 1px border into the swapchain, and CompositionBackdrop rounds the host-acrylic
        // sprite. We KEEP the glass-extend (the host-backdrop brush samples through it) but set NO DWMSBT_* system
        // backdrop, so nothing grey fills the window.
        int corner = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, in corner, sizeof(int));
        MARGINS m = new() { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
        DwmExtendFrameIntoClientArea(hwnd, in m);

        // THE missing piece: enable host-backdrop sampling so CreateHostBackdropBrush (CompositionBackdrop) actually
        // feeds the content behind the popup instead of rendering dark/empty. Without this the whole windowed-popup
        // acrylic was inert — the frost we saw earlier was the (now-removed) DWMSBT_TRANSIENTWINDOW system backdrop.
        unsafe
        {
            ACCENT_POLICY accent = new() { AccentState = ACCENT_ENABLE_HOSTBACKDROP };
            WINDOWCOMPOSITIONATTRIBDATA data = new() { Attrib = WCA_ACCENT_POLICY, pvData = &accent, cbData = (uint)sizeof(ACCENT_POLICY) };
            SetWindowCompositionAttribute(hwnd, &data);
        }
    }
}
