using System;
using System.Runtime.InteropServices;

namespace FluentGpu.WindowsApi.Activation;

/// <summary>
/// Per-user (<c>HKEY_CURRENT_USER</c>) registration of a custom URI scheme, a file extension, and logon-startup for an
/// unpackaged FluentGpu app — the runtime equivalent of what an MSIX manifest declares. Mirrors the surface WASDK's
/// <c>ActivationRegistrationManager</c> exposes, but writes the classic, self-owned registry shape rather than WASDK's
/// ProgId-indirection + <c>std::hash(exePath)</c> appId (which is implementation-defined and non-portable,
/// <c>dev/AppLifecycle/Association.cpp:32-33</c>; FluentGpu owns both writer and reader, so it has no reason to byte-match
/// it — docs/plans/windowsapi-implementation-research.md §2.2).
/// <para>
/// All writes target <c>HKCU\Software\Classes</c> (and <c>HKCU\…\CurrentVersion\Run</c> for startup), exactly the
/// per-user root WASDK hardcodes (<c>GetRegistrationRoot()</c>, <c>Association.cpp:8-11</c>) — there is no all-users/HKLM
/// path in v1 (WASDK spec: "the platform will support only per-user registration"). Every mutation ends with
/// <c>SHChangeNotify(SHCNE_ASSOCCHANGED)</c> so the shell refreshes its association cache
/// (<c>Association.cpp:436-439</c>). MSIX/packaged builds must NOT call these — the platform throws
/// <c>E_ILLEGAL_METHOD_CALL</c> from a packaged process; the caller should branch on
/// <see cref="FluentGpu.WindowsApi.Packaging.PackageIdentity.IsPackaged"/> (registration is unpackaged-only).
/// Because these are runtime registry writes (not OS-managed package state), an unpackaged app MUST call
/// <see cref="UnregisterProtocol"/>/<see cref="UnregisterFileType"/>/<see cref="UnregisterStartup"/> on uninstall or the
/// keys leak.
/// </para>
/// <para>
/// Registry shape written for scheme <c>wavee</c> (Learn: "Registering an Application to a URI Scheme",
/// <c>HKEY_CLASSES_ROOT\&lt;scheme&gt;</c> with a <c>URL Protocol</c> empty value + <c>shell\open\command</c>):
/// <code>
/// HKCU\Software\Classes\wavee\(default)            = "URL:wavee Protocol"   (or the supplied display name)
/// HKCU\Software\Classes\wavee\URL Protocol         = ""   (the empty value marking it a protocol)
/// HKCU\Software\Classes\wavee\DefaultIcon\(default) = "\"C:\path\app.exe\",0"   (optional)
/// HKCU\Software\Classes\wavee\shell\open\command\(default) = "\"C:\path\app.exe\" \"%1\""
/// </code>
/// The shell substitutes the full activation URI for <c>%1</c>; <see cref="ActivationArgs.Classify"/> reads it back.
/// </para>
/// </summary>
public static partial class ProtocolRegistrar
{
    // ── advapi32 registry ABI (hand-declared; cold path, AOT-clean LibraryImport in the house Win32Theme.cs style) ─────
    private static readonly nint HKEY_CURRENT_USER = unchecked((nint)0x80000001u);

    private const uint KEY_WRITE = 0x20006;
    private const uint REG_SZ = 1;
    private const int ERROR_SUCCESS = 0;
    private const int ERROR_FILE_NOT_FOUND = 2;

    [LibraryImport("advapi32.dll", EntryPoint = "RegCreateKeyExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegCreateKeyExW(
        nint hKey, string lpSubKey, uint reserved, string? lpClass, uint dwOptions,
        uint samDesired, nint lpSecurityAttributes, out nint phkResult, out uint lpdwDisposition);

    [LibraryImport("advapi32.dll", EntryPoint = "RegSetValueExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegSetValueExW(
        nint hKey, string? lpValueName, uint reserved, uint dwType, byte[] lpData, uint cbData);

    [LibraryImport("advapi32.dll", EntryPoint = "RegCloseKey")]
    private static partial int RegCloseKey(nint hKey);

    // RegDeleteTreeW deletes a key and all its subkeys/values — the clean-uninstall primitive the doc names.
    [LibraryImport("advapi32.dll", EntryPoint = "RegDeleteTreeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegDeleteTreeW(nint hKey, string? lpSubKey);

    [LibraryImport("advapi32.dll", EntryPoint = "RegDeleteKeyValueW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegDeleteKeyValueW(nint hKey, string lpSubKey, string? lpValueName);

    // SHCNE_ASSOCCHANGED = 0x08000000; SHCNF_IDLIST = 0 — tell the shell associations changed (Association.cpp:436-439).
    private const uint SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;

    [LibraryImport("shell32.dll", EntryPoint = "SHChangeNotify")]
    private static partial void SHChangeNotify(uint wEventId, uint uFlags, nint dwItem1, nint dwItem2);

    /// <summary>
    /// Register <paramref name="scheme"/> (e.g. <c>"wavee"</c>) as a URI-protocol handler for <paramref name="exePath"/>.
    /// Writes the <c>URL Protocol</c> marker + <c>shell\open\command</c> = <c>"exePath" "%1"</c> under
    /// <c>HKCU\Software\Classes\&lt;scheme&gt;</c>, then fires <c>SHChangeNotify</c>. Idempotent (re-registration overwrites).
    /// </summary>
    /// <param name="scheme">The URI scheme without the colon (lowercased per RFC 3986 §3.1).</param>
    /// <param name="exePath">Absolute path to the handler exe — typically <see cref="Environment.ProcessPath"/>.</param>
    /// <param name="displayName">Shown by the shell's "open with" UI; defaults to <c>"URL:&lt;scheme&gt; Protocol"</c>.</param>
    /// <param name="iconPath">Optional <c>DefaultIcon</c> source (defaults to the exe's icon index 0 when null).</param>
    public static void RegisterProtocol(string scheme, string exePath, string? displayName = null, string? iconPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        scheme = scheme.ToLowerInvariant();

        string root = $@"Software\Classes\{scheme}";
        SetValue(root, null, displayName ?? $"URL:{scheme} Protocol");
        SetValue(root, "URL Protocol", string.Empty);   // the empty marker value that makes it a protocol handler
        SetValue($@"{root}\DefaultIcon", null, $"\"{iconPath ?? exePath}\",0");
        SetValue($@"{root}\shell\open\command", null, $"\"{exePath}\" \"%1\"");

        NotifyAssocChanged();
    }

    /// <summary>Delete the entire <c>HKCU\Software\Classes\&lt;scheme&gt;</c> subtree and refresh the shell. Safe to call
    /// when the scheme was never registered (a missing key is treated as success).</summary>
    public static void UnregisterProtocol(string scheme)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);
        scheme = scheme.ToLowerInvariant();
        DeleteTree($@"Software\Classes\{scheme}");
        NotifyAssocChanged();
    }

    /// <summary>
    /// Register a file extension (e.g. <c>".wavee"</c>) so double-clicking it launches <paramref name="exePath"/> with the
    /// file path as <c>%1</c>. Uses a self-owned ProgId (<c>FluentGpu.&lt;ext&gt;</c>) carrying the <c>shell\open\command</c>,
    /// linked from the extension key — the standard two-key file-association shape.
    /// </summary>
    public static void RegisterFileType(string extension, string exePath, string? displayName = null, string? iconPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        if (extension[0] != '.') extension = "." + extension;
        extension = extension.ToLowerInvariant();

        string progId = "FluentGpu" + extension;   // e.g. "FluentGpu.wavee" — our handler ProgId
        // Extension → ProgId link.
        SetValue($@"Software\Classes\{extension}", null, progId);
        // ProgId → open command.
        SetValue($@"Software\Classes\{progId}", null, displayName ?? $"{extension.TrimStart('.')} file");
        SetValue($@"Software\Classes\{progId}\DefaultIcon", null, $"\"{iconPath ?? exePath}\",0");
        SetValue($@"Software\Classes\{progId}\shell\open\command", null, $"\"{exePath}\" \"%1\"");

        NotifyAssocChanged();
    }

    /// <summary>Remove the file-extension registration (both the <c>.ext</c> link and the <c>FluentGpu.ext</c> ProgId).</summary>
    public static void UnregisterFileType(string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        if (extension[0] != '.') extension = "." + extension;
        extension = extension.ToLowerInvariant();
        DeleteTree($@"Software\Classes\{extension}");
        DeleteTree($@"Software\Classes\FluentGpu{extension}");
        NotifyAssocChanged();
    }

    /// <summary>
    /// Register the app to auto-start at user logon by writing <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run\&lt;taskId&gt;</c>
    /// = the (quoted) exe path — the same plain <c>Run</c>-key mechanism WASDK uses for <c>StartupTask</c>
    /// (<c>ActivationRegistrationManager.h:15</c>; not Task Scheduler). The value is the quoted exe so paths with spaces work.
    /// </summary>
    /// <param name="taskId">A stable per-app value name (e.g. <c>"WAVEE"</c>).</param>
    public static void RegisterStartup(string taskId, string exePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        SetValue(@"Software\Microsoft\Windows\CurrentVersion\Run", taskId, $"\"{exePath}\"");
        // Startup is not a file association — no SHChangeNotify needed.
    }

    /// <summary>Remove the logon-startup <c>Run</c> value. Safe when it was never set.</summary>
    public static void UnregisterStartup(string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        int rc = RegDeleteKeyValueW(HKEY_CURRENT_USER, @"Software\Microsoft\Windows\CurrentVersion\Run", taskId);
        if (rc is not (ERROR_SUCCESS or ERROR_FILE_NOT_FOUND))
            throw new InvalidOperationException($"RegDeleteKeyValueW(Run\\{taskId}) failed: 0x{rc:X8}");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Create-or-open <paramref name="subKey"/> under HKCU and write a REG_SZ value (null name = the default value).</summary>
    private static void SetValue(string subKey, string? valueName, string data)
    {
        int rc = RegCreateKeyExW(HKEY_CURRENT_USER, subKey, 0, null, 0 /*REG_OPTION_NON_VOLATILE*/,
            KEY_WRITE, 0, out nint hKey, out _);
        if (rc != ERROR_SUCCESS)
            throw new InvalidOperationException($"RegCreateKeyExW(HKCU\\{subKey}) failed: 0x{rc:X8}");
        try
        {
            // REG_SZ data is a null-terminated UTF-16 string; cbData counts the terminator.
            byte[] bytes = StringToRegSz(data);
            int set = RegSetValueExW(hKey, valueName, 0, REG_SZ, bytes, (uint)bytes.Length);
            if (set != ERROR_SUCCESS)
                throw new InvalidOperationException($"RegSetValueExW(HKCU\\{subKey}\\{valueName ?? "(default)"}) failed: 0x{set:X8}");
        }
        finally { RegCloseKey(hKey); }
    }

    private static void DeleteTree(string subKey)
    {
        int rc = RegDeleteTreeW(HKEY_CURRENT_USER, subKey);
        if (rc is not (ERROR_SUCCESS or ERROR_FILE_NOT_FOUND))
            throw new InvalidOperationException($"RegDeleteTreeW(HKCU\\{subKey}) failed: 0x{rc:X8}");
    }

    private static void NotifyAssocChanged() => SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, 0, 0);

    /// <summary>UTF-16LE bytes + a 2-byte null terminator (the REG_SZ wire format RegSetValueExW expects).</summary>
    private static byte[] StringToRegSz(string s)
    {
        byte[] body = System.Text.Encoding.Unicode.GetBytes(s);
        byte[] buf = new byte[body.Length + 2];   // +2 for the L'\0' terminator
        Array.Copy(body, buf, body.Length);
        return buf;
    }
}
