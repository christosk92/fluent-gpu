using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FluentGpu;

/// <summary>
/// App-layer glue the "Windows APIs" gallery page uses to reach the live host without inventing accessors on the Engine
/// seam: the real top-level window <see cref="WindowHandle"/>, a thread-safe <see cref="WakeWindow"/> that nudges the
/// window's message pump (so an OS-thread callback's queued UI work drains promptly even while idle), and a relay of the
/// host's single-instance <see cref="OnActivationRedirected"/> event (raised by <c>AppHost</c> on the UI thread).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="WindowHandle"/> simply mirrors <see cref="FluentApp.WindowHandle"/> — the handle the host publishes when it
/// creates the window — so page code has a single import surface. <see cref="WakeWindow"/> posts <c>WM_NULL</c> to that
/// HWND; <c>Win32Window.WaitForWork</c> blocks in <c>MsgWaitForMultipleObjectsEx(QS_ALLINPUT)</c>, which returns on any
/// posted message, so a posted no-op wakes a frame. It is safe to call from any thread (<c>PostMessage</c> is
/// thread-safe) and a no-op before the window exists / after it closes (HWND 0).
/// </para>
/// <para>
/// <see cref="OnActivationRedirected"/> wires onto the per-run <c>AppHost.ActivationRedirected</c> event through
/// <see cref="FluentApp"/>; the host delivers it on the UI thread (PAL <c>WM_COPYDATA</c> → UI-thread WndProc), so a
/// subscriber may write signals directly. The page subscribes on mount; subscriptions accumulate across page remounts but
/// are harmless (idempotent log writes) and bounded by the gallery's lifetime.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows5.0")]
internal static partial class WindowsApiInterop
{
    private const uint WM_NULL = 0x0000;

    /// <summary>The live top-level window handle (the FluentGpu gallery window), or 0 before the window exists.</summary>
    public static nint WindowHandle => FluentApp.WindowHandle;

    /// <summary>Nudge the window's message pump so a queued UI-thread action (drained on the frame clock) runs promptly.
    /// Thread-safe; a no-op when there is no window. Posts a benign <c>WM_NULL</c> the pump discards after waking.</summary>
    public static void WakeWindow()
    {
        nint hwnd = FluentApp.WindowHandle;
        if (hwnd != 0)
            PostMessageW(hwnd, WM_NULL, 0, 0);
    }

    /// <summary>Subscribe to the host's single-instance activation-redirect event (a second launch's deep link forwarded
    /// to this instance). Delivered on the UI thread. Forwarded from <c>AppHost.ActivationRedirected</c> by the host.</summary>
    public static void OnActivationRedirected(Action<string> handler) => FluentApp.ActivationRedirected += handler;

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint hWnd, uint msg, nuint wParam, nint lParam);

    // ── OS UI-culture detection (the localization engine's host-injected provider) ───────────────────────────────────
    // The portable engine cannot read CultureInfo.CurrentUICulture (InvariantGlobalization=true zeroes it to invariant)
    // and must stay free of Win32, so it exposes Localization.OsCultureProvider — a Func<string?> the app wires here to
    // GetUserDefaultLocaleName (kernel32), which returns the user's preferred UI locale as a BCP-47 name ("en-US"). This
    // is the documented invariant-globalization workaround. macOS would wire CFLocale here instead — the engine seam is
    // identical.

    private const int LOCALE_NAME_MAX_LENGTH = 85;   // w, per the Win32 NLS contract (incl. the null terminator)

    /// <summary>The user's default UI locale name (BCP-47, e.g. <c>"en-US"</c>) via <c>GetUserDefaultLocaleName</c>, or
    /// an empty string on failure. Wired into <see cref="FluentGpu.Localization.Localization.OsCultureProvider"/> at
    /// startup so the engine can pick the OS culture without a <c>CultureInfo</c> dependency.</summary>
    public static string GetOsUiCultureName()
    {
        Span<char> buffer = stackalloc char[LOCALE_NAME_MAX_LENGTH];
        int len;
        unsafe
        {
            fixed (char* p = buffer)
                len = GetUserDefaultLocaleName(p, LOCALE_NAME_MAX_LENGTH);
        }
        // The API returns the count INCLUDING the null terminator; trim it. 0 = failure.
        return len > 1 ? new string(buffer.Slice(0, len - 1)) : string.Empty;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetUserDefaultLocaleName")]
    private static unsafe partial int GetUserDefaultLocaleName(char* lpLocaleName, int cchLocaleName);
}
