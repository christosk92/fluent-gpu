using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using TerraFX.Interop.WinRT;
using static TerraFX.Interop.WinRT.WinRT;

namespace FluentGpu.WindowsApi.Notifications;

/// <summary>
/// A scope-bounded owner of a WinRT <c>HSTRING</c> created with <c>WindowsCreateString</c> — the create/delete RAII the
/// toast Show chain uses for every runtime-class name, the AUMID, and the XML payload. You own every <c>HSTRING</c> you
/// create; this disposes it with <c>WindowsDeleteString</c> in a <c>finally</c> (via <c>using</c>). Because the WinRT ABI
/// consumes HSTRINGs by copy, the handle only needs to outlive the single call it is passed to (spike guidance,
/// docs/plans/windowsapi-implementation-research.md §2.1).
/// </summary>
/// <remarks>
/// <para>
/// <c>WindowsCreateString</c> takes a <c>char*</c> (TerraFX signature) over a <c>fixed</c> pin of the managed string;
/// the resulting <c>HSTRING</c> is an independent copy, so the pin is released as soon as creation returns.
/// <c>WindowsDeleteString</c> tolerates a null/default <c>HSTRING</c> (the WinRT contract returns S_OK for a NULL
/// string), so <see cref="Dispose"/> deletes unconditionally without poking the handle's internals.
/// </para>
/// <para>An empty/null source string creates a NULL <c>HSTRING</c> (length 0), which the ABI accepts as the empty
/// string — used for an empty AUMID/payload defensively.</para>
/// </remarks>
[SupportedOSPlatform("windows8.0")]
internal readonly unsafe struct HStringHandle : IDisposable
{
    /// <summary>The created handle, passed by value into the consuming ABI call.</summary>
    public HSTRING Value { get; }

    /// <summary>Create an <c>HSTRING</c> copy of <paramref name="s"/>. Throws if <c>WindowsCreateString</c> fails.</summary>
    public HStringHandle(string? s)
    {
        s ??= string.Empty;
        HSTRING h;
        fixed (char* p = s)
        {
            int hr = WindowsCreateString(p, (uint)s.Length, &h);
            if (hr < 0)
                throw new InvalidOperationException($"WindowsCreateString failed (0x{(uint)hr:X8}).");
        }
        Value = h;
    }

    /// <summary>Delete the handle (no-op for a NULL handle).</summary>
    public void Dispose() => WindowsDeleteString(Value);
}

/// <summary>
/// Reports whether the current process is running elevated. Toasts genuinely do not work in an elevated process, so
/// <see cref="ToastNotifier.IsSupported"/> is <c>!IsElevated()</c> (mirroring WASDK's
/// <c>IsSupported == !IsElevated</c>, <c>dev/AppNotifications/AppNotificationManager.cpp:107</c>). Pure
/// <c>advapi32</c> token query — AOT-clean flat P/Invoke.
/// </summary>
internal static partial class ProcessElevation
{
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenElevation = 20;   // TOKEN_INFORMATION_CLASS.TokenElevation

    /// <summary>True when the process token is elevated. Any failure querying the token is treated as "not elevated"
    /// (the conservative default — it keeps toasts enabled rather than wrongly disabling them).</summary>
    public static unsafe bool IsElevated()
    {
        nint process = GetCurrentProcess();   // pseudo-handle; must NOT be closed
        if (!OpenProcessToken(process, TOKEN_QUERY, out nint token))
            return false;
        try
        {
            uint elevation;   // TOKEN_ELEVATION { DWORD TokenIsElevated; }
            uint returnLength;
            if (!GetTokenInformation(token, TokenElevation, &elevation, sizeof(uint), &returnLength))
                return false;
            return elevation != 0;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentProcess")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("advapi32.dll", EntryPoint = "OpenProcessToken", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint ProcessHandle, uint DesiredAccess, out nint TokenHandle);

    [LibraryImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetTokenInformation(
        nint TokenHandle, int TokenInformationClass, void* TokenInformation,
        int TokenInformationLength, uint* ReturnLength);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint hObject);
}
