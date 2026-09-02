using System;
using System.Collections.Generic;
using System.Diagnostics;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Pal.Windows;

/// <summary>
/// One display's TRUE mode, resolved from <c>QueryDisplayConfig</c> — the per-monitor refresh the pacer needs, as
/// opposed to <c>DwmGetCompositionTimingInfo(HWND.NULL)</c>, which is primary-monitor-global by API contract and
/// therefore silently wrong for a window dragged onto a secondary display.
/// </summary>
/// <param name="DeviceName">The GDI device name (e.g. <c>"\\.\DISPLAY1"</c>) — <see cref="TerraFX.Interop.Windows.MONITORINFOEXW.szDevice"/>,
/// the join key between a monitor and its <c>DISPLAYCONFIG_PATH_INFO</c>.</param>
/// <param name="RefreshNumerator">DISPLAYCONFIG_RATIONAL.Numerator (e.g. 59940 for 59.94 Hz).</param>
/// <param name="RefreshDenominator">DISPLAYCONFIG_RATIONAL.Denominator (e.g. 1000 for 59.94 Hz).</param>
/// <param name="RefreshPeriodQpc">The refresh period in the Stopwatch (QPC) domain — <c>Stopwatch.Frequency *
/// Denominator / Numerator</c>, computed in 64-bit integer math so 59.94 Hz never collapses to the 60 Hz a
/// float/rounded-Hz path would produce. 0 when unknown.</param>
/// <param name="Valid">False on any failed query — callers must not throw, and must fall back to their own
/// default pacing when this is false.</param>
public readonly record struct DisplayModeInfo(
    string DeviceName,
    int RefreshNumerator,
    int RefreshDenominator,
    long RefreshPeriodQpc,
    bool Valid);

/// <summary>
/// The window's TRUE per-monitor refresh rate, via <c>QueryDisplayConfig</c> — replacing
/// <c>DwmGetCompositionTimingInfo(HWND.NULL)</c> (primary-monitor-global by API contract, so it never re-derives
/// when a window is dragged to a secondary display with a different refresh rate) as the pacer's rate source.
///
/// <b>The join.</b> <c>MonitorFromWindow</c> → <c>GetMonitorInfoW</c> (as <see cref="MONITORINFOEXW"/> — the plain
/// <c>MONITORINFO</c> the rest of this file's call sites use lacks <c>szDevice</c>; TerraFX binds both, and
/// <c>MONITORINFOEXW</c> is layout-compatible with <c>MONITORINFO</c> via its <c>Base</c> field, so the existing
/// P/Invoke signature is reused unchanged) gives the monitor's GDI device name (<c>"\\.\DISPLAY1"</c>).
/// <c>GetDisplayConfigBufferSizes</c> + <c>QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS)</c> gives every active
/// source→target path; <c>DisplayConfigGetDeviceInfo(DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME)</c> resolves each
/// path's source back to the same GDI device name, and the matching path's <c>targetInfo.refreshRate</c> is the
/// answer.
///
/// <b>Not <c>GetDeviceCaps(VREFRESH)</c>.</b> It returns INTEGER Hz — 59.94 Hz reads as 59 or 60, and a pacer built
/// on that drifts about one frame every ~16s (a slow beat-frequency judder against the true panel cadence). It is
/// kept ONLY as a last-resort fallback (<see cref="DISPLAYCONFIG_RATIONAL"/> query failed) — a coarse rate beats no
/// rate at all, but call sites needing precision must check <see cref="DisplayModeInfo.RefreshDenominator"/> is not 1
/// before assuming exactness is not required.
///
/// Both public members that hit a monitor (<see cref="ForWindow"/>/<see cref="ForPrimary"/>) are cheap enough for a
/// display-change handler (a couple of P/Invokes, no per-frame use intended) but still do a live query each call —
/// callers own their own caching. <see cref="EnumerateDisplays"/> is explicitly the COLD path (walks every active
/// display+path; a "Settings &gt; About" picker calls it, never a hot loop — modeled on
/// <c>FluentGpu.Rhi.D3D12.GpuAdapterInfo.EnumerateAdapters</c>, the same "cold enumeration for a picker UI" shape).
///
/// Never throws: every failure path returns <c>Valid = false</c> (or, for the fallback, a coarse-but-valid rate).
/// </summary>
public static class DisplayInfo
{
    // ── Win32 constants not bound by TerraFX (doc-text only, not actual fields — verified against the package's
    //    shipped XML) — declared locally like this file's siblings redeclare WAIT_FAILED / MONITOR_DEFAULTTONEAREST /
    //    VREFRESH etc. for the same reason. ────────────────────────────────────────────────────────────────────────
    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;   // wingdi.h — active paths only (topology "as configured")
    private const int ERROR_SUCCESS = 0;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;       // winerror.h — the documented "topology changed, retry" signal
    private const uint MONITOR_DEFAULTTONEAREST = 2;         // WinUser.h
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;         // WinUser.h
    // GetDeviceCaps(VREFRESH) index (wingdi.h) — LAST-RESORT fallback only; see the type doc above for why this must
    // never be the primary source (integer Hz truncates 59.94 to 59/60).
    private const int VREFRESH = 116;

    /// <summary>The refresh mode of the display the window is currently on (nearest monitor, matching
    /// <c>WM_DISPLAYCHANGE</c>/<c>WM_EXITSIZEMOVE</c> re-derivation semantics). <see cref="DisplayModeInfo.Valid"/>
    /// is false only if BOTH the QueryDisplayConfig join and the GetDeviceCaps fallback fail (no monitor, or the OS
    /// query itself failed) — never throws.</summary>
    public static unsafe DisplayModeInfo ForWindow(nint hwnd)
    {
        HMONITOR mon = MonitorFromWindow((HWND)hwnd, MONITOR_DEFAULTTONEAREST);
        return ForMonitorOrFallback(mon, hwnd);
    }

    /// <summary>The refresh mode of the PRIMARY display — used before any window/monitor is known (e.g. process
    /// start-up pacing) or as the general "what is this machine's main panel doing" query.</summary>
    public static unsafe DisplayModeInfo ForPrimary()
    {
        // hwnd=NULL + MONITOR_DEFAULTTOPRIMARY is the documented way to resolve the primary monitor's HMONITOR
        // without owning a window; GetDC(0) below (the fallback path) is likewise the documented "screen DC" form.
        HMONITOR mon = MonitorFromWindow(HWND.NULL, MONITOR_DEFAULTTOPRIMARY);
        return ForMonitorOrFallback(mon, 0);
    }

    /// <summary>Enumerate every ACTIVE display's mode (COLD path — a settings picker, never per-frame). Skips any
    /// path whose source-name resolution or refresh rate comes back invalid rather than surfacing a half-built
    /// entry; a picker would rather show N-1 real rows than one garbage row.</summary>
    public static unsafe IReadOnlyList<DisplayModeInfo> EnumerateDisplays()
    {
        var list = new List<DisplayModeInfo>(4);
        if (!TryQueryActivePaths(out DISPLAYCONFIG_PATH_INFO[]? paths, out int count) || paths is null) return list;
        for (int i = 0; i < count; i++)
        {
            ref readonly DISPLAYCONFIG_PATH_INFO path = ref paths[i];
            if (!TryGetSourceDeviceName(path.sourceInfo.adapterId, path.sourceInfo.id, out DISPLAYCONFIG_SOURCE_DEVICE_NAME name))
                continue;
            string deviceName = FixedBufferToString(name.viewGdiDeviceName);
            DisplayModeInfo info = BuildFromRational(deviceName, path.targetInfo.refreshRate);
            if (info.Valid) list.Add(info);
        }
        return list;
    }

    /// <summary>Shared join: resolve the monitor's GDI device name, match it against an active
    /// <c>DISPLAYCONFIG_PATH_INFO</c>'s source name, and read the target's exact refresh rate. Falls back to
    /// <c>GetDeviceCaps(VREFRESH)</c> (integer Hz — see the type doc) when the monitor handle is null, the
    /// GetMonitorInfoW call fails, or no path's source matches.</summary>
    private static unsafe DisplayModeInfo ForMonitorOrFallback(HMONITOR mon, nint hwndForFallback)
    {
        if (mon == HMONITOR.NULL) return FallbackFromDeviceCaps(hwndForFallback, "");

        MONITORINFOEXW mi = default;
        mi.Base.cbSize = (uint)sizeof(MONITORINFOEXW);
        if (!GetMonitorInfoW(mon, (MONITORINFO*)&mi)) return FallbackFromDeviceCaps(hwndForFallback, "");

        string deviceName = FixedBufferToString(mi.szDevice);
        if (TryFindRefreshRate(deviceName, out DISPLAYCONFIG_RATIONAL rr))
        {
            DisplayModeInfo built = BuildFromRational(deviceName, rr);
            if (built.Valid) return built;
        }
        return FallbackFromDeviceCaps(hwndForFallback, deviceName);
    }

    /// <summary>Walk the active-path table looking for the source whose GDI device name matches
    /// <paramref name="deviceName"/>, returning its target's refresh rate.</summary>
    private static unsafe bool TryFindRefreshRate(string deviceName, out DISPLAYCONFIG_RATIONAL refreshRate)
    {
        refreshRate = default;
        if (!TryQueryActivePaths(out DISPLAYCONFIG_PATH_INFO[]? paths, out int count) || paths is null) return false;
        for (int i = 0; i < count; i++)
        {
            ref readonly DISPLAYCONFIG_PATH_INFO path = ref paths[i];
            if (!TryGetSourceDeviceName(path.sourceInfo.adapterId, path.sourceInfo.id, out DISPLAYCONFIG_SOURCE_DEVICE_NAME name))
                continue;
            if (!FixedBufferEquals(name.viewGdiDeviceName, deviceName)) continue;
            refreshRate = path.targetInfo.refreshRate;
            return true;
        }
        return false;
    }

    /// <summary>Size, allocate, query — retried ONCE on <c>ERROR_INSUFFICIENT_BUFFER</c>, the documented signal that
    /// the display topology changed between the size call and the query call (a monitor plugged/unplugged mid-race).
    /// A second failure is treated as a genuine query failure, not retried again (never a spin).</summary>
    private static unsafe bool TryQueryActivePaths(out DISPLAYCONFIG_PATH_INFO[]? paths, out int pathCount)
    {
        paths = null;
        pathCount = 0;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            uint numPaths = 0, numModes = 0;
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, &numPaths, &numModes) != ERROR_SUCCESS) return false;
            if (numPaths == 0) return false;

            var pathArray = new DISPLAYCONFIG_PATH_INFO[numPaths];
            var modeArray = new DISPLAYCONFIG_MODE_INFO[numModes];
            int result;
            fixed (DISPLAYCONFIG_PATH_INFO* pPaths = pathArray)
            fixed (DISPLAYCONFIG_MODE_INFO* pModes = modeArray)
            {
                // pCurrentTopologyId MUST be NULL under QDC_ONLY_ACTIVE_PATHS (documented — it is only legal with
                // QDC_DATABASE_CURRENT). modeArray is required buffer sizing only: this seam never reads it back.
                result = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, &numPaths, pPaths, &numModes, pModes, null);
            }
            if (result == ERROR_SUCCESS)
            {
                paths = pathArray;
                pathCount = (int)numPaths;
                return true;
            }
            if (result != ERROR_INSUFFICIENT_BUFFER) return false;
            // Topology changed since GetDisplayConfigBufferSizes — loop once more with fresh sizes.
        }
        return false;
    }

    /// <summary>Resolve one source's GDI device name via <c>DisplayConfigGetDeviceInfo</c>.</summary>
    private static unsafe bool TryGetSourceDeviceName(LUID adapterId, uint id, out DISPLAYCONFIG_SOURCE_DEVICE_NAME name)
    {
        // Filled through a LOCAL, not the out param: an `out` is a managed reference, so `&name` is an unfixed
        // expression the compiler rejects (CS0212). A stack local is directly addressable, and the copy-out is free.
        DISPLAYCONFIG_SOURCE_DEVICE_NAME local = default;
        local.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
        local.header.size = (uint)sizeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME);
        local.header.adapterId = adapterId;
        local.header.id = id;
        bool ok = DisplayConfigGetDeviceInfo((DISPLAYCONFIG_DEVICE_INFO_HEADER*)&local) == ERROR_SUCCESS;
        name = local;
        return ok;
    }

    /// <summary>Exact QPC-domain period from a DISPLAYCONFIG_RATIONAL: <c>Stopwatch.Frequency * Denominator /
    /// Numerator</c> in 64-bit integer math (no float — the whole point is 59.94 Hz must not collapse to 60).
    /// Invalid (0/0, or either half 0) yields <c>Valid = false</c> rather than a divide-by-zero.</summary>
    private static DisplayModeInfo BuildFromRational(string deviceName, DISPLAYCONFIG_RATIONAL rr)
    {
        if (rr.Numerator == 0 || rr.Denominator == 0) return default;
        long periodQpc = Stopwatch.Frequency * (long)rr.Denominator / (long)rr.Numerator;
        return new DisplayModeInfo(deviceName, (int)rr.Numerator, (int)rr.Denominator, periodQpc, true);
    }

    /// <summary>Last-resort fallback: <c>GetDeviceCaps(VREFRESH)</c> on the window (or screen, hwnd=0) DC. Integer
    /// Hz only — reported as Numerator=Hz/Denominator=1 so a caller reading <see cref="DisplayModeInfo.RefreshDenominator"/>
    /// can tell an exact rational from this coarse one. 0/1 ("device default") is genuinely unknown, not a Valid=true 0 Hz.</summary>
    private static unsafe DisplayModeInfo FallbackFromDeviceCaps(nint hwnd, string deviceName)
    {
        HDC hdc = GetDC((HWND)hwnd);
        if (hdc == HDC.NULL) return default;
        int hz;
        try { hz = GetDeviceCaps(hdc, VREFRESH); }
        finally { ReleaseDC((HWND)hwnd, hdc); }
        if (hz <= 1) return default;
        return new DisplayModeInfo(deviceName, hz, 1, Stopwatch.Frequency / hz, true);
    }

    /// <summary>NUL-terminated fixed WCHAR buffer → string (TerraFX inline-array fields convert implicitly to
    /// <see cref="ReadOnlySpan{Char}"/>, the same pattern <c>GpuAdapterInfo.EnumerateAdapters</c> uses for
    /// <c>DXGI_ADAPTER_DESC1.Description</c>).</summary>
    private static string FixedBufferToString(ReadOnlySpan<char> buffer)
    {
        int nul = buffer.IndexOf('\0');
        return new string(nul >= 0 ? buffer[..nul] : buffer);
    }

    private static bool FixedBufferEquals(ReadOnlySpan<char> buffer, ReadOnlySpan<char> value)
    {
        int nul = buffer.IndexOf('\0');
        return (nul >= 0 ? buffer[..nul] : buffer).SequenceEqual(value);
    }
}
