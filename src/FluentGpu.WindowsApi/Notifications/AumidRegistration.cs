using System;
using System.Globalization;
using System.Runtime.InteropServices;
using FluentGpu.WindowsApi.Packaging;

namespace FluentGpu.WindowsApi.Notifications;

/// <summary>
/// The unpackaged AUMID + COM-activator registry plumbing a classic Win32 (non-MSIX) FluentGpu app needs before it can
/// show a toast: it derives a stable App User Model ID, registers the toast COM activator's CLSID and its
/// <c>LocalServer32</c> command line, and writes the app's display name / icon assets — all under
/// <c>HKEY_CURRENT_USER</c>. This is the runtime equivalent of what an MSIX manifest declares for a packaged app; for a
/// packaged process every method here is a no-op (the manifest owns the registration and the platform throws on runtime
/// writes — branch on <see cref="PackageIdentity.IsPackaged"/>).
/// </summary>
/// <remarks>
/// <para>
/// Replicates WASDK's unpackaged registration five-step (<c>dev/AppNotifications/AppNotificationManager.cpp:228-244</c>,
/// helpers in <c>AppNotificationUtility.cpp</c>) with plain <c>advapi32</c> registry writes, in the house
/// <c>[LibraryImport]</c> style (<c>FluentGpu.Windows/Pal/Win32Theme.cs</c>, sibling
/// <c>Activation/ProtocolRegistrar.cs</c>):
/// <list type="number">
/// <item><b>AUMID derivation</b> (<see cref="GetOrCreateUnpackagedAumid"/>): honor an explicitly-set process AUMID, else
/// read/create a <c>NotificationGUID</c> under <c>HKCU\Software\Classes\AppUserModelId\{exePath with \→.}</c> — that GUID
/// (braced) is the stable AUMID across runs (<c>AppNotificationUtility.cpp:43-96</c>,
/// <c>ConvertPathToKey</c> <c>AppNotificationUtility.h:22-32</c>).</item>
/// <item><b>Activator CLSID</b>: the caller supplies the toast-activator CLSID (FluentGpu defines it once and pastes the
/// same value into <c>CoRegisterClassObject</c> and, for a future packaged build, the manifest — see
/// <see cref="ToastNotifier"/>). WASDK mints/reads one under <c>CustomActivator</c>; FluentGpu lets the app own it so it
/// matches the manifest CLSID exactly (<c>AppNotificationUtility.cpp:197-270</c>).</item>
/// <item><b><c>LocalServer32</c></b> (<see cref="RegisterComServer"/>): <c>HKCU\Software\Classes\CLSID\{clsid}\LocalServer32 =
/// "exePath" ----AppNotificationActivated:</c> — so the Shell can cold-launch the exe on a toast click
/// (<c>AppNotificationUtility.cpp:114-136</c>; sentinel <c>AppNotificationUtility.h:18</c>).</item>
/// <item><b>Asset registration</b> (<see cref="RegisterAssets"/>): <c>DisplayName</c>, <c>IconUri</c> (local file only),
/// and <c>CustomActivator</c> (the braced CLSID) under <c>HKCU\Software\Classes\AppUserModelId\{AppGUID}</c>
/// (<c>AppNotificationUtility.cpp:298-328</c>).</item>
/// <item>(The fifth step, <c>CoRegisterClassObject</c>, is runtime — it lives in <see cref="ToastNotifier"/>, not here.)</item>
/// </list>
/// </para>
/// <para>
/// The <c>LocalServer32</c> argument sentinel (<see cref="ToastActivatedArgument"/>) is the SAME string the sibling
/// Activation pillar's command-line classifier matches (<c>FluentGpu.WindowsApi.Activation.ActivationArgs.ToastActivatedSentinel</c>),
/// so a cold-launched toast click classifies as a toast activation. WASDK uses the same
/// <c>"----AppNotificationActivated:"</c> token (<c>AppNotificationUtility.h:18</c>).
/// </para>
/// <para>
/// Because these are runtime registry writes (not OS-managed package state), an unpackaged app MUST call
/// <see cref="Unregister"/> on uninstall or the keys leak. Cold path — allocation is fine.
/// </para>
/// <para>
/// References:
/// <list type="bullet">
/// <item><see href="https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/send-local-toast-other-apps">Send a local toast from other unpackaged apps</see> (registry-based AUMID; no shortcut needed)</item>
/// <item><see href="https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-getcurrentprocessexplicitappusermodelid">GetCurrentProcessExplicitAppUserModelID</see></item>
/// </list>
/// </para>
/// </remarks>
public static partial class AumidRegistration
{
    // Registry layout constants (AppNotificationUtility.h:15-18). The sentinel has a leading space because it follows
    // the quoted exe path as a separate argv token: `"C:\app.exe" ----AppNotificationActivated:`.
    private const string AppUserModelIdPath = @"Software\Classes\AppUserModelId\";
    private const string ClsIdPath = @"Software\Classes\CLSID\";

    /// <summary>The <c>LocalServer32</c> argument the Shell passes when cold-launching the app for a toast click. Equal
    /// to <c>FluentGpu.WindowsApi.Activation.ActivationArgs.ToastActivatedSentinel</c> (the sibling pillar matches this
    /// token on the command line) and to WASDK's <c>c_notificationActivatedArgument</c> minus its leading space
    /// (<c>AppNotificationUtility.h:18</c>). Kept as its own constant so the two pillars stay in sync without a type
    /// dependency.</summary>
    public const string ToastActivatedArgument = "----AppNotificationActivated:";

    /// <summary>
    /// The AUMID to attribute this process's toasts to. For a packaged process this is the manifest AUMID
    /// (<see cref="PackageIdentity.ApplicationUserModelId"/>); for an unpackaged process it is the explicitly-set
    /// process AUMID, or a per-exe <c>NotificationGUID</c> derived (and created on first call) under HKCU.
    /// <see cref="GetOrCreateUnpackagedAumid"/> for the unpackaged derivation; this property routes to it on the
    /// unpackaged branch. Mirrors WASDK's <c>RetrieveNotificationAppId</c> (<c>AppNotificationUtility.cpp:98-112</c>).
    /// </summary>
    public static string GetNotificationAumid()
    {
        if (PackageIdentity.IsPackaged)
        {
            // A packaged process's AUMID comes from the package graph (GetCurrentApplicationUserModelId via
            // PackageIdentity). Should always be present for a packaged process; fall back defensively.
            return PackageIdentity.ApplicationUserModelId ?? string.Empty;
        }
        return GetOrCreateUnpackagedAumid();
    }

    /// <summary>
    /// Derive the stable unpackaged AUMID: if <c>SetCurrentProcessExplicitAppUserModelID</c> was called, honor that;
    /// otherwise read (or create on first run) a <c>NotificationGUID</c> REG_SZ under
    /// <c>HKCU\Software\Classes\AppUserModelId\{exePath with \→.}</c> and return the braced GUID. Idempotent — once the
    /// GUID exists it is returned unchanged on every subsequent run (<c>AppNotificationUtility.cpp:43-96</c>).
    /// </summary>
    public static unsafe string GetOrCreateUnpackagedAumid()
    {
        // Honor an explicitly-set AUMID first (the app may already have called SetCurrentProcessExplicitAppUserModelID).
        if (TryGetExplicitProcessAumid(out string explicitAumid))
            return explicitAumid;

        string exePath = CurrentProcessPath();
        string subKey = AppUserModelIdPath + ConvertPathToKey(exePath);

        if (RegCreateKeyExW(HKEY_CURRENT_USER, subKey, 0, null, REG_OPTION_NON_VOLATILE,
                KEY_ALL_ACCESS, 0, out nint hKey, out _) != ERROR_SUCCESS)
            throw new InvalidOperationException($"RegCreateKeyExW(HKCU\\{subKey}) failed deriving the notification AUMID.");

        try
        {
            string? existing = ReadRegSz(hKey, "NotificationGUID");
            if (!string.IsNullOrEmpty(existing))
                return existing;

            // First run for this exe path: mint a GUID and persist it braced (StringFromCLSID format, e.g.
            // "{6B29FC40-CA47-1067-B31D-00DD010662DA}"). That braced string IS the AUMID.
            Guid guid;
            int hr = CoCreateGuid(&guid);
            if (hr < 0)
                throw new InvalidOperationException($"CoCreateGuid failed (0x{hr:X8}) deriving the notification AUMID.");
            string guidString = guid.ToString("B", CultureInfo.InvariantCulture).ToUpperInvariant();

            WriteRegSz(hKey, "NotificationGUID", guidString, REG_SZ);
            return guidString;
        }
        finally
        {
            RegCloseKey(hKey);
        }
    }

    /// <summary>
    /// Perform the full unpackaged registration for a toast-capable app: write the <c>LocalServer32</c> COM-server
    /// command line for <paramref name="activatorClsid"/>, then the AUMID assets (<paramref name="displayName"/>,
    /// optional local <paramref name="iconPath"/>, and the <c>CustomActivator</c> = the braced CLSID). No-op when
    /// packaged. Returns the AUMID the toasts will be attributed to.
    /// </summary>
    /// <param name="activatorClsid">The toast-activator CLSID — the SAME GUID passed to <c>CoRegisterClassObject</c> in
    /// <see cref="ToastNotifier"/> (and, for a packaged build, declared in the manifest).</param>
    /// <param name="displayName">The app name the Action Center shows; defaults to the process file name when null.</param>
    /// <param name="iconPath">An optional LOCAL icon file path (<c>http(s)://</c> is not allowed by the Shell for the
    /// AUMID icon); null/empty clears any previously-registered icon so the Shell renders a default.</param>
    /// <returns>The derived unpackaged AUMID (or the packaged AUMID, unchanged, when packaged).</returns>
    public static string Register(Guid activatorClsid, string? displayName = null, string? iconPath = null)
    {
        if (PackageIdentity.IsPackaged)
            return GetNotificationAumid();   // packaged: manifest owns registration — nothing to write.

        string aumid = GetOrCreateUnpackagedAumid();
        string clsid = activatorClsid.ToString("B", CultureInfo.InvariantCulture).ToUpperInvariant();

        RegisterComServer(clsid);
        RegisterAssets(aumid, clsid, displayName ?? DefaultDisplayName(), iconPath);

        // OPEN-QUESTION(#3): WASDK's RegisterUnpackagedApp additionally calls
        // PushNotifications_RegisterFullTrustApplication(appId, GUID_NULL) UNCONDITIONALLY right here
        // (AppNotificationManager.cpp:230). FluentGpu deliberately omits it — that API is an unlinkable OS FrameworkUDK
        // export, and the legacy DesktopNotificationManagerCompat local-toast path proves a non-push toast needs only the
        // AUMID + CLSID + LocalServer32 registry writes above + CoRegisterClassObject (in ToastNotifier). Whether omitting
        // it degrades any local-toast edge case (a Show()+click round-tripping with ONLY this registry path) is still owed
        // end-to-end validation on the target OS build (docs/plans/windowsapi-implementation-research.md §5 #3). The
        // --windowsapi-smoke harness exercises Show() returning S_OK; the click leg remains a manual check.
        return aumid;
    }

    /// <summary>
    /// Remove this app's unpackaged toast registration: delete the activator CLSID subtree and the AUMID key. No-op when
    /// packaged or when nothing was registered. Call on uninstall (and optionally on a "disable notifications" toggle).
    /// </summary>
    public static void Unregister(Guid activatorClsid)
    {
        if (PackageIdentity.IsPackaged)
            return;

        string clsid = activatorClsid.ToString("B", CultureInfo.InvariantCulture).ToUpperInvariant();
        DeleteTree(ClsIdPath + clsid);

        // Two AppUserModelId keys are written for an unpackaged registration and BOTH must be removed or assets leak:
        //   • the per-exe DERIVATION key  AppUserModelId\{exePath with \→.}  — holds the NotificationGUID, and
        //   • the ASSET key              AppUserModelId\{aumid}             — holds DisplayName/IconUri/CustomActivator,
        //     where {aumid} is the braced NotificationGUID that derivation key stored (RegisterAssets writes it there).
        // The asset key is named by the GUID, not the path, so deleting only the derivation key (the prior behavior)
        // left the asset key behind — and because deleting the derivation key makes the next run mint a fresh GUID, each
        // run leaked a new asset key. Read the AUMID off the derivation key first, delete the asset key, then the
        // derivation key. An explicitly-set process AUMID is left untouched (the app owns it through another mechanism).
        if (!TryGetExplicitProcessAumid(out _))
        {
            string derivationKey = AppUserModelIdPath + ConvertPathToKey(CurrentProcessPath());
            string? aumid = ReadDerivedAumid(derivationKey);
            if (!string.IsNullOrEmpty(aumid))
                DeleteTree(AppUserModelIdPath + aumid);   // the asset key (AppUserModelId\{guid})
            DeleteTree(derivationKey);                    // the derivation key (AppUserModelId\{exe.path})
        }
    }

    /// <summary>Read the <c>NotificationGUID</c> (the braced AUMID) stored under the per-exe derivation key, WITHOUT
    /// creating the key if it is absent (a plain open, so <see cref="Unregister"/> does not resurrect a key it is about
    /// to delete). Returns null when the key or value does not exist.</summary>
    private static string? ReadDerivedAumid(string derivationSubKey)
    {
        if (RegOpenKeyExW(HKEY_CURRENT_USER, derivationSubKey, 0, KEY_READ, out nint hKey) != ERROR_SUCCESS)
            return null;
        try { return ReadRegSz(hKey, "NotificationGUID"); }
        finally { RegCloseKey(hKey); }
    }

    /// <summary>Write <c>HKCU\Software\Classes\CLSID\{clsid}\LocalServer32 = "exePath" ----AppNotificationActivated:</c>
    /// (<c>AppNotificationUtility.cpp:114-136</c>). The value is the quoted exe path so paths with spaces work, plus the
    /// activation sentinel as a second argv token.</summary>
    private static void RegisterComServer(string clsid)
    {
        string subKey = $@"{ClsIdPath}{clsid}\LocalServer32";
        string command = $"\"{CurrentProcessPath()}\" {ToastActivatedArgument}";
        SetValue(subKey, null, command, REG_SZ);
    }

    /// <summary>Write the AUMID assets — <c>DisplayName</c> (REG_EXPAND_SZ), <c>IconUri</c> (REG_EXPAND_SZ, only when a
    /// local icon path is given; otherwise the value is cleared), and <c>CustomActivator</c> (REG_SZ = the braced CLSID)
    /// — under <c>HKCU\Software\Classes\AppUserModelId\{aumid}</c> (<c>AppNotificationUtility.cpp:298-328</c>).</summary>
    private static void RegisterAssets(string aumid, string clsid, string displayName, string? iconPath)
    {
        string subKey = AppUserModelIdPath + aumid;

        if (RegCreateKeyExW(HKEY_CURRENT_USER, subKey, 0, null, REG_OPTION_NON_VOLATILE,
                KEY_ALL_ACCESS, 0, out nint hKey, out _) != ERROR_SUCCESS)
            throw new InvalidOperationException($"RegCreateKeyExW(HKCU\\{subKey}) failed writing toast assets.");
        try
        {
            WriteRegSz(hKey, "DisplayName", displayName, REG_EXPAND_SZ);

            if (!string.IsNullOrEmpty(iconPath))
                WriteRegSz(hKey, "IconUri", iconPath, REG_EXPAND_SZ);
            else
                RegDeleteValueW(hKey, "IconUri");   // best-effort clear of a stale icon from a prior registration

            WriteRegSz(hKey, "CustomActivator", clsid, REG_SZ);
        }
        finally
        {
            RegCloseKey(hKey);
        }
    }

    /// <summary>Honor an explicitly-set process AUMID (<c>GetCurrentProcessExplicitAppUserModelID</c>). Returns false
    /// when none was set (the common case for a fresh process). The OS allocates the string with <c>CoTaskMemAlloc</c>;
    /// we copy it and free it.</summary>
    private static unsafe bool TryGetExplicitProcessAumid(out string aumid)
    {
        aumid = string.Empty;
        char* p = null;
        int hr = GetCurrentProcessExplicitAppUserModelID(&p);
        if (hr < 0 || p == null)
        {
            if (p != null) CoTaskMemFree(p);
            return false;
        }
        try
        {
            aumid = new string(p);
            return aumid.Length != 0;
        }
        finally
        {
            CoTaskMemFree(p);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Replace every <c>\</c> with <c>.</c> to turn an exe path into a registry-key-safe leaf (backslashes are
    /// the key separator), matching WASDK's <c>ConvertPathToKey</c> (<c>AppNotificationUtility.h:22-32</c>).</summary>
    private static string ConvertPathToKey(string path) => path.Replace('\\', '.');

    /// <summary>The current process's exe path. <see cref="Environment.ProcessPath"/> is the AOT-clean way to get it
    /// (no reflection); it is the same value WASDK reads via <c>GetCurrentProcessPath</c>.</summary>
    private static string CurrentProcessPath() =>
        Environment.ProcessPath ?? throw new InvalidOperationException("Environment.ProcessPath is unavailable.");

    private static string DefaultDisplayName()
    {
        string exe = CurrentProcessPath();
        string name = System.IO.Path.GetFileNameWithoutExtension(exe);
        return string.IsNullOrEmpty(name) ? "FluentGpu App" : name;
    }

    /// <summary>Create-or-open <paramref name="subKey"/> under HKCU and write a REG_SZ/REG_EXPAND_SZ value.</summary>
    private static void SetValue(string subKey, string? valueName, string data, uint type)
    {
        if (RegCreateKeyExW(HKEY_CURRENT_USER, subKey, 0, null, REG_OPTION_NON_VOLATILE,
                KEY_ALL_ACCESS, 0, out nint hKey, out _) != ERROR_SUCCESS)
            throw new InvalidOperationException($"RegCreateKeyExW(HKCU\\{subKey}) failed.");
        try { WriteRegSz(hKey, valueName, data, type); }
        finally { RegCloseKey(hKey); }
    }

    private static void WriteRegSz(nint hKey, string? valueName, string data, uint type)
    {
        byte[] bytes = StringToRegSz(data);
        int rc = RegSetValueExW(hKey, valueName, 0, type, bytes, (uint)bytes.Length);
        if (rc != ERROR_SUCCESS)
            throw new InvalidOperationException($"RegSetValueExW({valueName ?? "(default)"}) failed: 0x{rc:X8}");
    }

    /// <summary>Read a REG_SZ string value, or null when absent. Two-call sizing then a buffer read.</summary>
    private static unsafe string? ReadRegSz(nint hKey, string valueName)
    {
        uint type = 0;
        uint cb = 0;
        int rc = RegQueryValueExW(hKey, valueName, 0, &type, null, &cb);
        if (rc == ERROR_FILE_NOT_FOUND || cb == 0)
            return null;
        if (rc != ERROR_SUCCESS && rc != ERROR_MORE_DATA)
            return null;

        byte[] buffer = new byte[cb];
        fixed (byte* p = buffer)
        {
            rc = RegQueryValueExW(hKey, valueName, 0, &type, p, &cb);
            if (rc != ERROR_SUCCESS)
                return null;
        }
        // REG_SZ is UTF-16; strip the trailing null terminator if present.
        int chars = (int)(cb / sizeof(char));
        var s = System.Text.Encoding.Unicode.GetString(buffer, 0, chars * sizeof(char));
        int nul = s.IndexOf('\0');
        return nul >= 0 ? s[..nul] : s;
    }

    private static void DeleteTree(string subKey)
    {
        int rc = RegDeleteTreeW(HKEY_CURRENT_USER, subKey);
        if (rc is not (ERROR_SUCCESS or ERROR_FILE_NOT_FOUND))
            throw new InvalidOperationException($"RegDeleteTreeW(HKCU\\{subKey}) failed: 0x{rc:X8}");
    }

    /// <summary>UTF-16LE bytes + a 2-byte null terminator (the REG_SZ/REG_EXPAND_SZ wire format).</summary>
    private static byte[] StringToRegSz(string s)
    {
        byte[] body = System.Text.Encoding.Unicode.GetBytes(s);
        byte[] buf = new byte[body.Length + 2];
        Array.Copy(body, buf, body.Length);
        return buf;
    }

    // ── Win32 ABI (hand-declared LibraryImport, house Win32Theme.cs / ProtocolRegistrar.cs style) ───────────────────────
    private static readonly nint HKEY_CURRENT_USER = unchecked((nint)0x80000001u);

    private const uint KEY_ALL_ACCESS = 0xF003F;
    private const uint KEY_READ = 0x20019;   // STANDARD_RIGHTS_READ | KEY_QUERY_VALUE | KEY_ENUMERATE_SUB_KEYS | KEY_NOTIFY
    private const uint REG_OPTION_NON_VOLATILE = 0;
    private const uint REG_SZ = 1;
    private const uint REG_EXPAND_SZ = 2;
    private const int ERROR_SUCCESS = 0;
    private const int ERROR_FILE_NOT_FOUND = 2;
    private const int ERROR_MORE_DATA = 234;

    [LibraryImport("advapi32.dll", EntryPoint = "RegCreateKeyExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegCreateKeyExW(
        nint hKey, string lpSubKey, uint reserved, string? lpClass, uint dwOptions,
        uint samDesired, nint lpSecurityAttributes, out nint phkResult, out uint lpdwDisposition);

    [LibraryImport("advapi32.dll", EntryPoint = "RegOpenKeyExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegOpenKeyExW(
        nint hKey, string lpSubKey, uint ulOptions, uint samDesired, out nint phkResult);

    [LibraryImport("advapi32.dll", EntryPoint = "RegSetValueExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegSetValueExW(
        nint hKey, string? lpValueName, uint reserved, uint dwType, byte[] lpData, uint cbData);

    [LibraryImport("advapi32.dll", EntryPoint = "RegQueryValueExW", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int RegQueryValueExW(
        nint hKey, string lpValueName, uint reserved, uint* lpType, byte* lpData, uint* lpcbData);

    [LibraryImport("advapi32.dll", EntryPoint = "RegDeleteValueW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegDeleteValueW(nint hKey, string? lpValueName);

    [LibraryImport("advapi32.dll", EntryPoint = "RegDeleteTreeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegDeleteTreeW(nint hKey, string? lpSubKey);

    [LibraryImport("advapi32.dll", EntryPoint = "RegCloseKey")]
    private static partial int RegCloseKey(nint hKey);

    // ole32: CoCreateGuid mints the NotificationGUID; CoTaskMemFree frees the explicit-AUMID string.
    [LibraryImport("ole32.dll", EntryPoint = "CoCreateGuid")]
    private static unsafe partial int CoCreateGuid(Guid* pguid);

    [LibraryImport("ole32.dll", EntryPoint = "CoTaskMemFree")]
    private static unsafe partial void CoTaskMemFree(void* pv);

    // shell32: GetCurrentProcessExplicitAppUserModelID returns S_FALSE/E_FAIL when none is set (we treat any failure as
    // "no explicit AUMID"). The out string is CoTaskMem-allocated and must be freed by the caller.
    [LibraryImport("shell32.dll", EntryPoint = "GetCurrentProcessExplicitAppUserModelID")]
    private static unsafe partial int GetCurrentProcessExplicitAppUserModelID(char** AppID);
}
