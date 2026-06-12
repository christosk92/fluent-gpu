using System;
using System.Buffers.Binary;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.WindowsApi.Packaging;

/// <summary>
/// Runtime package-identity queries for the current process. This is the "am I identity-bearing, and if so what is
/// my package/AUMID?" probe the other WindowsApi pillars (notifications, activation) branch on; it does NOT author,
/// install, or sign MSIX — packaging itself is an app-side build concern, deliberately out of this class library
/// (see <c>FluentGpu.WindowsApi.csproj</c> and <c>docs/plans/windowsapi-implementation-research.md</c> §2.3 / appendix).
/// </summary>
/// <remarks>
/// <para>
/// Mirrors WASDK's auditable identity helpers 1:1 — <c>GetCurrentPackage{FullName,FamilyName}</c>,
/// <c>GetCurrentApplicationUserModelId</c>, <c>GetCurrentPackagePath</c>, and <c>GetCurrentPackageId</c> →
/// <c>PACKAGE_VERSION</c> (WASDK <c>dev/Common/AppModel.Identity.h:18-42,165-241</c>;
/// <c>AppModel.Identity.IsPackagedProcess.h:11-27</c>). All entry points are flat <c>kernel32</c> exports — pure
/// blittable P/Invoke, AOT/trim-clean with no COM, WinRT, or reflection. Declarations come from
/// <c>TerraFX.Interop.Windows</c>; only the plain integer status <c>#define</c>s (which TerraFX does not project)
/// are restated here.
/// </para>
/// <para>
/// Every identity API follows the OS two-call buffer-size pattern: call with a <c>null</c> buffer to learn the
/// required character count, then call again with a buffer of that size. The "is packaged?" decision keys off the
/// status of the sizing call: <see cref="ERROR_INSUFFICIENT_BUFFER"/> (a name exists but the probe buffer was empty)
/// means packaged — including a sparse / external-location identity, which reports packaged unchanged — while
/// <see cref="APPMODEL_ERROR_NO_PACKAGE"/> means unpackaged. WASDK keys "packaged" off the same
/// <c>ERROR_INSUFFICIENT_BUFFER</c> sentinel (<c>AppModel.Identity.IsPackagedProcess.h:16</c>).
/// </para>
/// <para>
/// Cold path: every property is computed once on first access and cached for the process lifetime (identity cannot
/// change after launch). Unpackaged processes return <see langword="null"/>/<see langword="false"/> uniformly rather
/// than throwing — callers branch on <see cref="IsPackaged"/>.
/// </para>
/// <para>
/// References:
/// <list type="bullet">
/// <item><see href="https://learn.microsoft.com/en-us/windows/win32/api/appmodel/nf-appmodel-getcurrentpackagefullname">GetCurrentPackageFullName</see></item>
/// <item><see href="https://learn.microsoft.com/en-us/windows/win32/api/appmodel/nf-appmodel-getcurrentapplicationusermodelid">GetCurrentApplicationUserModelId</see></item>
/// <item><see href="https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps">Grant package identity by packaging with external location</see> (sparse identity reports packaged)</item>
/// </list>
/// </para>
/// </remarks>
public static class PackageIdentity
{
    // Plain Win32 status #defines (winerror.h). TerraFX exposes the identity P/Invokes and structs but not these
    // integer constants, so they are restated from the Windows SDK headers (10.0.26100.0).
    private const int APPMODEL_ERROR_NO_PACKAGE = 15700;   // 0x3D54 — process has no package identity (unpackaged).
    private const int ERROR_INSUFFICIENT_BUFFER = 122;     // 0x7A   — a name exists; the probe buffer was too small.
    private const int ERROR_SUCCESS = 0;

    // PACKAGE_ID is pshpack1-wrapped, but `reserved`(u32) + `processorArchitecture`(u32) = 8 bytes always precede the
    // u64 `version` (PACKAGE_VERSION), so the version sits at byte offset 8 regardless of packing (appmodel.h:68-76).
    private const int PackageIdVersionOffset = 8;

    // Lazily-initialized cache. `_probed` is the "have we probed yet?" latch for the whole block; the individual
    // value slots are populated together on first probe. Identity is immutable for the process, so a non-locked
    // first-writer-wins race is harmless (every racer computes the same answer). `_probed` is volatile and written
    // LAST, so a reader that observes it true also observes the fully-populated value slots (no reordering ahead of
    // the field writes on weak memory models).
    private static volatile bool _probed;
    private static bool _isPackaged;
    private static string? _packageFullName;
    private static string? _packageFamilyName;
    private static string? _applicationUserModelId;
    private static string? _installedLocation;
    private static Version? _version;

    /// <summary><see langword="true"/> if the current process runs with package identity (MSIX, including sparse /
    /// external-location identity); <see langword="false"/> for a plain unpackaged Win32 process.</summary>
    public static bool IsPackaged
    {
        get { EnsureProbed(); return _isPackaged; }
    }

    /// <summary>The package full name (e.g. <c>Contoso.App_1.0.0.0_x64__abcdefgh12345</c>), or
    /// <see langword="null"/> when unpackaged.</summary>
    public static string? PackageFullName
    {
        get { EnsureProbed(); return _packageFullName; }
    }

    /// <summary>The package family name (e.g. <c>Contoso.App_abcdefgh12345</c>), or <see langword="null"/> when
    /// unpackaged.</summary>
    public static string? PackageFamilyName
    {
        get { EnsureProbed(); return _packageFamilyName; }
    }

    /// <summary>The Application User Model ID (<c>{PackageFamilyName}!{AppId}</c>) of the current process, or
    /// <see langword="null"/> when unpackaged. This is the notification-attribution AUMID the Notifications pillar
    /// uses on the packaged branch.</summary>
    public static string? ApplicationUserModelId
    {
        get { EnsureProbed(); return _applicationUserModelId; }
    }

    /// <summary>The on-disk install location of the current package, or <see langword="null"/> when unpackaged.</summary>
    public static string? InstalledLocation
    {
        get { EnsureProbed(); return _installedLocation; }
    }

    /// <summary>The package version (from <c>PACKAGE_ID.version</c>), mapped to <see cref="System.Version"/> as
    /// Major.Minor.Build.Revision; <see langword="null"/> when unpackaged.</summary>
    public static Version? Version
    {
        get { EnsureProbed(); return _version; }
    }

    private static unsafe void EnsureProbed()
    {
        if (_probed)
            return;

        // GetCurrentPackageFullName is the canonical identity probe (WASDK IsPackagedProcess). Size-call with a null
        // buffer: ERROR_INSUFFICIENT_BUFFER ⇒ packaged; APPMODEL_ERROR_NO_PACKAGE ⇒ unpackaged. The full-name query
        // both yields the value and decides identity, so every other query is skipped when unpackaged.
        _packageFullName = QueryString(&GetCurrentPackageFullName, out bool packaged);
        _isPackaged = packaged;

        if (_isPackaged)
        {
            _packageFamilyName = QueryString(&GetCurrentPackageFamilyName, out _);
            _applicationUserModelId = QueryString(&GetCurrentApplicationUserModelId, out _);
            _installedLocation = QueryString(&GetCurrentPackagePath, out _);
            _version = QueryVersion();
        }

        _probed = true;
    }

    /// <summary>
    /// Two-call buffer-size pattern over one of the OS <c>(uint* length, char* buffer)</c> identity exports
    /// (<c>GetCurrentPackageFullName</c> and friends share this exact signature, so the TerraFX P/Invoke is passed
    /// directly as a function pointer — no delegate, allocation, or reflection). On the sizing call
    /// <paramref name="packaged"/> is set from the status sentinel; on success the buffer is realized into a managed
    /// string (the returned length COUNTS the terminating null, so the string is trimmed by one).
    /// </summary>
    private static unsafe string? QueryString(delegate*<uint*, char*, int> api, out bool packaged)
    {
        uint length = 0;
        int rc = api(&length, null);

        if (rc == APPMODEL_ERROR_NO_PACKAGE)
        {
            packaged = false;
            return null;
        }

        // A present-but-empty value returns ERROR_SUCCESS with length 0; otherwise the OS asks for a buffer via
        // ERROR_INSUFFICIENT_BUFFER. Either way an identity is present ⇒ packaged.
        packaged = true;

        if (rc == ERROR_SUCCESS || length == 0)
            return string.Empty;
        if (rc != ERROR_INSUFFICIENT_BUFFER)
            return null;   // unexpected failure — surface as "no value" rather than throwing on this cold probe.

        char[] buffer = new char[length];
        fixed (char* p = buffer)
        {
            rc = api(&length, p);
            if (rc != ERROR_SUCCESS)
                return null;
            // `length` is the character count including the null terminator.
            return new string(p, 0, (int)length - 1);
        }
    }

    /// <summary>
    /// Reads <c>PACKAGE_ID.version</c> via <c>GetCurrentPackageId</c> (two-call, into a raw byte buffer) and unpacks
    /// the <c>PACKAGE_VERSION</c> u64 at <see cref="PackageIdVersionOffset"/> into Major.Minor.Build.Revision. Reading
    /// the version from the binary <c>PACKAGE_ID</c> avoids parsing the full-name string and needs no embedded-pointer
    /// marshaling (only the fixed-offset scalar is touched).
    /// </summary>
    private static unsafe Version? QueryVersion()
    {
        uint bufferSize = 0;
        int rc = GetCurrentPackageId(&bufferSize, null);
        if (rc != ERROR_INSUFFICIENT_BUFFER || bufferSize < PackageIdVersionOffset + sizeof(ulong))
            return null;

        byte[] buffer = new byte[bufferSize];
        fixed (byte* p = buffer)
        {
            rc = GetCurrentPackageId(&bufferSize, p);
            if (rc != ERROR_SUCCESS)
                return null;
        }

        // PACKAGE_VERSION packs the four 16-bit parts into a u64 as (Major<<48 | Minor<<32 | Build<<16 | Revision),
        // i.e. Revision is the low word (appmodel.h PACKAGE_VERSION union). Windows is little-endian on every
        // supported architecture, so the OS-written u64 reads back with ReadUInt64LittleEndian.
        ulong packed = BinaryPrimitives.ReadUInt64LittleEndian(
            new ReadOnlySpan<byte>(buffer, PackageIdVersionOffset, sizeof(ulong)));

        int revision = (int)(packed & 0xFFFF);
        int build = (int)((packed >> 16) & 0xFFFF);
        int minor = (int)((packed >> 32) & 0xFFFF);
        int major = (int)((packed >> 48) & 0xFFFF);
        return new Version(major, minor, build, revision);
    }
}
