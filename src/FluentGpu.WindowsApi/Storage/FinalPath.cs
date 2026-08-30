using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FluentGpu.WindowsApi.Storage;

/// <summary>Where a path REALLY lands, as the file system resolves it — the one fact a packaged app cannot read off
/// the string it asked for. A packaged (MSIX) process writing under <c>%LOCALAPPDATA%</c> is normally redirected into
/// <c>Packages\&lt;family&gt;\LocalCache\Local\…</c>; the same call from an unpackaged run, or in the cases where the
/// real folder already exists and wins, goes to the literal path. Both look identical in <c>Path.Combine</c> output,
/// and the difference is a split settings/log store that only shows up as "the app forgot everything". Logging the
/// resolved path once at startup makes that visible in every log.</summary>
public static partial class FinalPath
{
    const uint FILE_NAME_NORMALIZED = 0x0;
    const uint FILE_SHARE_ALL = 0x1 | 0x2 | 0x4;           // READ | WRITE | DELETE
    const uint OPEN_EXISTING = 3;
    const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;    // required to open a DIRECTORY handle
    static readonly nint INVALID_HANDLE_VALUE = -1;

    /// <summary>The final (resolved) path of an existing file OR directory, or <see langword="null"/> when it cannot be
    /// resolved (missing, no access, or a non-Windows host). Directories are accepted on purpose: at startup the day's
    /// log file may not exist yet, but its folder does, and the folder is what the redirection applies to. Never throws.</summary>
    [SupportedOSPlatform("windows")]
    public static unsafe string? Resolve(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        nint h = INVALID_HANDLE_VALUE;
        try
        {
            fixed (char* p = path)
                h = CreateFileW(p, 0 /* metadata only */, FILE_SHARE_ALL, null, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, 0);
            if (h == INVALID_HANDLE_VALUE) return null;
            char* buf = stackalloc char[1024];
            uint n = GetFinalPathNameByHandleW(h, buf, 1024, FILE_NAME_NORMALIZED);
            if (n == 0 || n >= 1024) return null;
            var s = new string(buf, 0, (int)n);
            // The API answers with a "\\?\" (or "\\?\UNC\") prefix; strip the local-drive form for readability.
            const string UncPrefix = @"\\?\UNC\";
            const string Prefix = @"\\?\";
            if (s.StartsWith(UncPrefix, StringComparison.Ordinal)) return @"\\" + s[UncPrefix.Length..];
            return s.StartsWith(Prefix, StringComparison.Ordinal) ? s[Prefix.Length..] : s;
        }
        catch { return null; }
        finally { if (h != INVALID_HANDLE_VALUE) CloseHandle(h); }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW")]
    private static unsafe partial nint CreateFileW(char* name, uint access, uint share, void* security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint h);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW")]
    private static unsafe partial uint GetFinalPathNameByHandleW(nint hFile, char* lpszFilePath, uint cchFilePath, uint dwFlags);
}
