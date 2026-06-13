using System;
using System.Runtime.InteropServices;

namespace FluentGpu.WindowsApi.Activation;

/// <summary>
/// A single-instance election + activation-redirect gate built from plain Win32 named kernel objects and
/// <c>WM_COPYDATA</c> — the deliberately simpler hand-rolled choice over WASDK's 512-PID shared table / GUID redirection
/// queue / COM-marshaled <c>AppActivationArguments</c> (<c>dev/AppLifecycle/AppInstance.cpp</c>,
/// <c>RedirectionRequest.cpp</c>). FluentGpu only ever forwards a command-line *string* (the activation URI) to one
/// already-running window, never a marshaled argument graph, so the entire mechanism is one named mutex + one
/// <c>SendMessageTimeoutW</c> (docs/plans/windowsapi-implementation-research.md §2.2).
/// <para>
/// Why <c>WM_COPYDATA</c> rather than WASDK's named-event doorbell: it is the only <c>SendMessage</c> payload the OS
/// marshals across process boundaries (the kernel copies <c>lpData</c> into the receiver), it is delivered on the
/// receiver's UI thread inside its <c>WndProc</c> (no threadpool→UI hop — WASDK's hardest integration constraint), and a
/// sent message satisfies the engine idle pump's wake mask (<c>QS_ALLINPUT ⊇ QS_SENDMESSAGE</c>,
/// <c>MsgWaitForMultipleObjectsEx</c> in <c>Win32Platform.WaitForWork</c>), so it wakes a parked frame loop for free.
/// </para>
/// <para>
/// The receiving half lives in the Win32 PAL (<c>FluentGpu.Windows/Pal/Win32Platform.cs</c>): a <c>WM_COPYDATA</c> case
/// matched on <see cref="ActivationCopyDataCookie"/> reconstructs the string and raises
/// <c>IPlatformApp.ActivationRedirected</c> on the UI thread. The two halves cannot share a type (WindowsApi and Windows
/// are independent peers — neither references the other), so this cookie value is duplicated there with a cross-reference
/// comment; keep the two in sync.
/// </para>
/// </summary>
public sealed unsafe partial class SingleInstanceGate : IDisposable
{
    /// <summary>
    /// The <c>COPYDATASTRUCT.dwData</c> tag identifying a FluentGpu activation-redirect message. The receiver
    /// (<c>Win32Platform.cs</c>) ignores any <c>WM_COPYDATA</c> whose <c>dwData</c> is not this value, so an unrelated
    /// app sending <c>WM_COPYDATA</c> to a FluentGpu window cannot be misread as an activation. Arbitrary but stable
    /// ("FGAC" = FluentGpu ACtivation, as ASCII) — must equal the constant duplicated in the PAL receiver.
    /// </summary>
    public const nuint ActivationCopyDataCookie = 0x46474143; // 'F''G''A''C'

    // ── Win32 ABI (hand-declared; cold path) ────────────────────────────────────────────────────────────────────────
    private const uint WM_COPYDATA = 0x004A;

    // SendMessageTimeout flags (winuser.h): SMTO_NORMAL=0, SMTO_ABORTIFHUNG=0x0002 — abandon the send rather than block
    // the exiting second instance behind a mid-Paint receiver (the verification fix over bare SendMessageW).
    private const uint SMTO_NORMAL = 0x0000;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const uint RedirectTimeoutMs = 5000;

    // ERROR_ALREADY_EXISTS = 183 — CreateMutexW succeeded but the named object already existed (we are NOT the first).
    private const int ERROR_ALREADY_EXISTS = 183;

    [StructLayout(LayoutKind.Sequential)]
    private struct COPYDATASTRUCT
    {
        public nuint dwData;   // ULONG_PTR application-defined tag (we use ActivationCopyDataCookie)
        public uint cbData;    // byte count of lpData
        public nint lpData;    // pointer to the payload (UTF-16 chars here)
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateMutexW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateMutexW(nint lpMutexAttributes, [MarshalAs(UnmanagedType.Bool)] bool bInitialOwner, string lpName);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint hObject);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowW(string? lpClassName, string? lpWindowName);

    [LibraryImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hWnd);

    // ASFW_ANY = (DWORD)-1 — let any process the running instance chooses take the foreground (mirrors WASDK's
    // AllowSetForegroundWindow(targetPid) at AppInstance.cpp:256, so the running window's SetForegroundWindow is honored).
    private const uint ASFW_ANY = unchecked((uint)-1);

    [LibraryImport("user32.dll", EntryPoint = "AllowSetForegroundWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint dwProcessId);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static partial nint SendMessageTimeoutW(
        nint hWnd, uint Msg, nuint wParam, in COPYDATASTRUCT lParam,
        uint fuFlags, uint uTimeout, out nuint lpdwResult);

    private nint _mutex;
    private bool _owns;

    /// <summary>True when this process won the election (it is the primary/only instance) — set by
    /// <see cref="TryAcquire"/>. A secondary instance (<c>false</c>) has already forwarded its payload and should exit.</summary>
    public bool IsPrimary => _owns;

    /// <summary>
    /// Attempt to become the single instance identified by <paramref name="instanceId"/>. Returns <c>true</c> when this
    /// process is the first (caller proceeds to create its window); returns <c>false</c> when another instance already
    /// owns the name — in which case this method has already located the running window (class
    /// <paramref name="windowClass"/>) and forwarded <paramref name="activationPayload"/> to it via <c>WM_COPYDATA</c>,
    /// and the caller MUST exit without creating a window. Idempotent per instance; call once, before window creation.
    /// </summary>
    /// <param name="instanceId">
    /// The mutex name shared by all instances of this app — e.g. <c>"Local\\WAVEE.SingleInstance"</c>. Use a
    /// <c>Local\</c> (per-session) name unless cross-session single-instancing is wanted. Must match across instances
    /// (derive it from a stable app id, not the exe path, if the exe can move).
    /// </param>
    /// <param name="windowClass">
    /// The Win32 window class of the running instance's main window — <c>"FluentGpuWindow"</c> for a FluentApp host
    /// (<c>Win32Platform.cs</c> registers this class). Used as the <c>FindWindowW</c> class filter.
    /// </param>
    /// <param name="activationPayload">
    /// The string handed to the running instance (typically the activation URI, e.g. <c>"wavee://callback?..."</c>). It
    /// arrives there as <c>IPlatformApp.ActivationRedirected</c>. May be empty (a bare second launch that should just
    /// focus the primary window).
    /// </param>
    public bool TryAcquire(string instanceId, string windowClass, string activationPayload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(activationPayload);

        // Election: CreateMutexW with bInitialOwner=true. If the object already existed we are a secondary instance.
        _mutex = CreateMutexW(0, true, instanceId);
        int err = Marshal.GetLastPInvokeError();
        if (_mutex != 0 && err != ERROR_ALREADY_EXISTS)
        {
            _owns = true;
            return true;   // we are the primary — keep the mutex alive for this process lifetime
        }

        // Secondary: forward the activation to the primary window, then signal the caller to exit.
        if (_mutex != 0) { CloseHandle(_mutex); _mutex = 0; }   // we don't own it; release our handle
        _owns = false;
        Redirect(windowClass, activationPayload);
        return false;
    }

    /// <summary>Send <paramref name="payload"/> as a <c>WM_COPYDATA</c> to the first window of class
    /// <paramref name="windowClass"/>, after granting it foreground rights. Best-effort: if no window is found yet (the
    /// primary is mid-startup) the redirect is silently dropped — the caller still exits, matching WASDK's "redirect or
    /// give up" posture for a racing launch.</summary>
    private void Redirect(string windowClass, string payload)
    {
        nint target = FindWindowW(windowClass, null);
        if (target == 0) return;   // primary not up yet — nothing to forward to

        AllowSetForegroundWindow(ASFW_ANY);   // let the primary's SetForegroundWindow win (mirror AppInstance.cpp:256)

        // Pin the UTF-16 payload and copy it across via WM_COPYDATA. cbData is the byte count (chars * 2); the receiver
        // reconstructs new string((char*)lpData, 0, cbData/2). An empty payload sends a zero-length buffer (focus-only).
        ReadOnlySpan<char> chars = payload;
        fixed (char* p = chars)
        {
            var cds = new COPYDATASTRUCT
            {
                dwData = ActivationCopyDataCookie,
                cbData = (uint)(chars.Length * sizeof(char)),
                lpData = (nint)p,
            };
            // SMTO_ABORTIFHUNG: if the primary is hung (e.g. blocked mid-Paint), abandon rather than wedge our exit.
            // OPEN-QUESTION(#5): the receiver's idle frame loop parks in MsgWaitForMultipleObjectsEx(QS_ALLINPUT); the
            // API contract guarantees a sent WM_COPYDATA satisfies that wake mask (QS_ALLINPUT ⊇ QS_SENDMESSAGE), but the
            // wall-clock promptness + no-deadlock-behind-a-long-Paint behavior should be confirmed end-to-end on the
            // target build. SMTO_ABORTIFHUNG is the mitigation already in place
            // (docs/plans/windowsapi-implementation-research.md §5 #5).
            SendMessageTimeoutW(target, WM_COPYDATA, 0, in cds,
                SMTO_NORMAL | SMTO_ABORTIFHUNG, RedirectTimeoutMs, out _);
        }
    }

    /// <summary>Release the mutex handle (only meaningful for the primary; a secondary already closed its handle in
    /// <see cref="TryAcquire"/>). The kernel object disappears when the last handle closes, freeing the name for the next
    /// launch.</summary>
    public void Dispose()
    {
        if (_mutex != 0) { CloseHandle(_mutex); _mutex = 0; }
        _owns = false;
    }
}
