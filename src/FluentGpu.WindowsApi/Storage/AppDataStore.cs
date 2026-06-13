using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace FluentGpu.WindowsApi.Storage;

/// <summary>
/// The Storage pillar: durable per-user application data for an UNPACKAGED FluentGpu app — the modern, TYPED replacement
/// for WASDK / UWP <c>ApplicationData.Current.LocalSettings</c> (a stringly-typed <c>IPropertySet</c> that needs package
/// identity). A settings container backed by a flat <c>HKCU\Software\&lt;publisher&gt;\&lt;product&gt;\Settings</c> registry
/// key (scalar REG types: string→REG_SZ, bool/int→REG_DWORD, long/double→REG_QWORD, byte[]→REG_BINARY) plus the
/// well-known per-user folders (<see cref="LocalFolder"/>/<see cref="CacheFolder"/>/<see cref="TempFolder"/>).
///
/// <para>Identity-free: works packaged and unpackaged identically (a packaged process just gets HKCU/LocalAppData
/// transparently virtualized into its package). No COM/WinRT/reflection — flat <c>advapi32</c> registry + BCL folder
/// paths, AOT/trim-clean. The registry ABI is the house pattern reused verbatim from
/// <see cref="FluentGpu.WindowsApi.Activation.ProtocolRegistrar"/> plus one new <c>RegEnumValueW</c> for <see cref="Keys"/>.</para>
///
/// <para><b>Type fidelity.</b> Each typed <c>Get*</c> reads the stored REG type back and returns the fallback on a type
/// MISMATCH (e.g. <c>SetInt</c> then <c>GetDouble</c> → fallback), never a reinterpreted garbage value. For a reactive,
/// signal-backed view of the same store, use <see cref="SettingsStore"/>.</para>
///
/// <para><b>Concurrency (honest).</b> Registry value writes are atomic per-value (last-writer-wins across processes);
/// there is no change watcher in v1 — a second process changing a value does not notify this one. Re-read on demand.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class AppDataStore
{
    private readonly string _settingsKey;   // Software\<pub>\<prod>\Settings
    private readonly string _publisher, _product;

    private AppDataStore(string publisher, string product)
    {
        _publisher = publisher;
        _product = product;
        _settingsKey = $@"Software\{publisher}\{product}\Settings";
    }

    /// <summary>A store rooted at <c>HKCU\Software\{publisher}\{product}</c> (and the matching LocalAppData/Temp folders).</summary>
    public static AppDataStore ForUnpackaged(string publisher, string product)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publisher);
        ArgumentException.ThrowIfNullOrWhiteSpace(product);
        return new AppDataStore(publisher, product);
    }

    // ── well-known folders (BCL; created on first access, idempotent) ────────────────────────────────────────────────
    /// <summary><c>%LOCALAPPDATA%\&lt;publisher&gt;\&lt;product&gt;</c> — durable per-user app data (created on first read).</summary>
    public string LocalFolder => EnsureDir(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), _publisher, _product));
    /// <summary><c>%LOCALAPPDATA%\&lt;publisher&gt;\&lt;product&gt;\Cache</c> — evictable cache data.</summary>
    public string CacheFolder => EnsureDir(Path.Combine(LocalFolder, "Cache"));
    /// <summary><c>%TEMP%\&lt;publisher&gt;\&lt;product&gt;</c> — scratch space.</summary>
    public string TempFolder => EnsureDir(Path.Combine(Path.GetTempPath(), _publisher, _product));

    // ── typed get/set ────────────────────────────────────────────────────────────────────────────────────────────────
    public void SetString(string key, string value) => Write(key, REG_SZ, StringToRegSz(value));
    public string? GetString(string key, string? fallback = null) { var b = Read(key, REG_SZ); return b is null ? fallback : RegSzToString(b); }

    public void SetBool(string key, bool value) => Write(key, REG_DWORD, BitConverter.GetBytes(value ? 1u : 0u));
    public bool GetBool(string key, bool fallback = false) => ReadDword(key) is uint v ? v != 0 : fallback;

    public void SetInt(string key, int value) => Write(key, REG_DWORD, BitConverter.GetBytes(unchecked((uint)value)));
    public int GetInt(string key, int fallback = 0) => ReadDword(key) is uint v ? unchecked((int)v) : fallback;

    public void SetLong(string key, long value) => Write(key, REG_QWORD, BitConverter.GetBytes(unchecked((ulong)value)));
    public long GetLong(string key, long fallback = 0) => ReadQword(key) is ulong v ? unchecked((long)v) : fallback;

    public void SetDouble(string key, double value) => Write(key, REG_QWORD, BitConverter.GetBytes(unchecked((ulong)BitConverter.DoubleToInt64Bits(value))));
    public double GetDouble(string key, double fallback = 0) => ReadQword(key) is ulong v ? BitConverter.Int64BitsToDouble(unchecked((long)v)) : fallback;

    public void SetBytes(string key, byte[] value) => Write(key, REG_BINARY, value ?? Array.Empty<byte>());
    public byte[]? GetBytes(string key) => Read(key, REG_BINARY);

    // ── container ops ────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>True iff a value with this name exists (of any type).</summary>
    public bool Contains(string key) => QueryType(key) is not null;

    /// <summary>Delete a single value (a missing value is treated as success).</summary>
    public void Remove(string key)
    {
        if (RegOpenKeyExW(HKEY_CURRENT_USER, _settingsKey, 0, KEY_WRITE, out nint hk) != ERROR_SUCCESS) return;
        try { int rc = RegDeleteValueW(hk, key); if (rc is not (ERROR_SUCCESS or ERROR_FILE_NOT_FOUND)) throw Fail("RegDeleteValueW", key, rc); }
        finally { RegCloseKey(hk); }
    }

    /// <summary>Delete the whole settings subtree (a missing key is treated as success).</summary>
    public void Clear()
    {
        int rc = RegDeleteTreeW(HKEY_CURRENT_USER, _settingsKey);
        if (rc is not (ERROR_SUCCESS or ERROR_FILE_NOT_FOUND)) throw Fail("RegDeleteTreeW", _settingsKey, rc);
    }

    /// <summary>Enumerate the value names currently in the container.</summary>
    public IReadOnlyList<string> Keys()
    {
        if (RegOpenKeyExW(HKEY_CURRENT_USER, _settingsKey, 0, KEY_READ, out nint hk) != ERROR_SUCCESS)
            return Array.Empty<string>();
        var names = new List<string>();
        try
        {
            // Registry value names are at most 16383 chars; lpcchValueName is IN(buffer chars incl null) / OUT(chars excl null).
            char[] name = new char[16384];
            for (uint i = 0; ; i++)
            {
                uint len = (uint)name.Length;
                int e = RegEnumValueW(hk, i, name, ref len, 0, out _, 0, 0);
                if (e is ERROR_NO_MORE_ITEMS or ERROR_FILE_NOT_FOUND) break;
                if (e != ERROR_SUCCESS) break;   // tolerate a mid-enumeration failure rather than throw on a read
                names.Add(new string(name, 0, (int)len));
            }
        }
        finally { RegCloseKey(hk); }
        return names;
    }

    // ── registry helpers ─────────────────────────────────────────────────────────────────────────────────────────────
    private void Write(string key, uint type, byte[] data)
    {
        int rc = RegCreateKeyExW(HKEY_CURRENT_USER, _settingsKey, 0, null, 0 /*REG_OPTION_NON_VOLATILE*/, KEY_WRITE, 0, out nint hk, out _);
        if (rc != ERROR_SUCCESS) throw Fail("RegCreateKeyExW", _settingsKey, rc);
        try { int s = RegSetValueExW(hk, key, 0, type, data, (uint)data.Length); if (s != ERROR_SUCCESS) throw Fail("RegSetValueExW", key, s); }
        finally { RegCloseKey(hk); }
    }

    /// <summary>Read the raw bytes for a value, but ONLY if its stored type matches <paramref name="expectedType"/>
    /// (a type mismatch or a missing key/value → null so the typed Get returns its fallback).</summary>
    private byte[]? Read(string key, uint expectedType)
    {
        if (RegOpenKeyExW(HKEY_CURRENT_USER, _settingsKey, 0, KEY_READ, out nint hk) != ERROR_SUCCESS) return null;
        try
        {
            uint cb = 0;
            int q = RegQueryValueExW(hk, key, 0, out uint type, null, ref cb);
            if (q != ERROR_SUCCESS || type != expectedType) return null;
            byte[] buf = new byte[cb];
            if (cb == 0) return buf;
            q = RegQueryValueExW(hk, key, 0, out type, buf, ref cb);
            return q == ERROR_SUCCESS ? buf : null;
        }
        finally { RegCloseKey(hk); }
    }

    private uint? ReadDword(string key) { var b = Read(key, REG_DWORD); return b is { Length: >= 4 } ? BitConverter.ToUInt32(b, 0) : null; }
    private ulong? ReadQword(string key) { var b = Read(key, REG_QWORD); return b is { Length: >= 8 } ? BitConverter.ToUInt64(b, 0) : null; }

    private uint? QueryType(string key)
    {
        if (RegOpenKeyExW(HKEY_CURRENT_USER, _settingsKey, 0, KEY_READ, out nint hk) != ERROR_SUCCESS) return null;
        try { uint cb = 0; return RegQueryValueExW(hk, key, 0, out uint type, null, ref cb) == ERROR_SUCCESS ? type : null; }
        finally { RegCloseKey(hk); }
    }

    private static string EnsureDir(string p) { Directory.CreateDirectory(p); return p; }
    private static InvalidOperationException Fail(string call, string what, int rc) => new($"{call}(HKCU\\...\\{what}) failed: 0x{rc:X8}");
    private static byte[] StringToRegSz(string s) { byte[] body = Encoding.Unicode.GetBytes(s); byte[] buf = new byte[body.Length + 2]; Array.Copy(body, buf, body.Length); return buf; }
    private static string RegSzToString(byte[] b) { int len = b.Length; if (len >= 2 && b[len - 1] == 0 && b[len - 2] == 0) len -= 2; return Encoding.Unicode.GetString(b, 0, len); }

    // ── advapi32 ABI (house pattern; HKCU + scalar REG types; RegEnumValueW is the one import ProtocolRegistrar lacked) ──
    private static readonly nint HKEY_CURRENT_USER = unchecked((nint)0x80000001u);
    private const uint KEY_READ = 0x20019, KEY_WRITE = 0x20006;
    private const uint REG_SZ = 1, REG_BINARY = 3, REG_DWORD = 4, REG_QWORD = 11;
    private const int ERROR_SUCCESS = 0, ERROR_FILE_NOT_FOUND = 2, ERROR_NO_MORE_ITEMS = 259;

    [LibraryImport("advapi32.dll", EntryPoint = "RegCreateKeyExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegCreateKeyExW(nint hKey, string lpSubKey, uint reserved, string? lpClass, uint dwOptions, uint samDesired, nint lpSecurityAttributes, out nint phkResult, out uint lpdwDisposition);

    [LibraryImport("advapi32.dll", EntryPoint = "RegOpenKeyExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegOpenKeyExW(nint hKey, string lpSubKey, uint ulOptions, uint samDesired, out nint phkResult);

    [LibraryImport("advapi32.dll", EntryPoint = "RegSetValueExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegSetValueExW(nint hKey, string? lpValueName, uint reserved, uint dwType, byte[] lpData, uint cbData);

    [LibraryImport("advapi32.dll", EntryPoint = "RegQueryValueExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegQueryValueExW(nint hKey, string? lpValueName, nint reserved, out uint lpType, byte[]? lpData, ref uint lpcbData);

    [LibraryImport("advapi32.dll", EntryPoint = "RegEnumValueW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegEnumValueW(nint hKey, uint dwIndex, char[] lpValueName, ref uint lpcchValueName, nint lpReserved, out uint lpType, nint lpData, nint lpcbData);

    [LibraryImport("advapi32.dll", EntryPoint = "RegDeleteValueW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegDeleteValueW(nint hKey, string? lpValueName);

    [LibraryImport("advapi32.dll", EntryPoint = "RegDeleteTreeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegDeleteTreeW(nint hKey, string? lpSubKey);

    [LibraryImport("advapi32.dll", EntryPoint = "RegCloseKey")]
    private static partial int RegCloseKey(nint hKey);
}
