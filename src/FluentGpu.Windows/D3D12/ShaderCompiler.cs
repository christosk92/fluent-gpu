using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;

namespace FluentGpu.Rhi.D3D12;

/// <summary>
/// Runtime HLSL → DXBC (sm5.1) compile via D3DCompile, the ONE compile chokepoint for every D3D12 pipeline (the spec's
/// eventual path is DXC → DXIL offline). Backed by an unconditional content-addressed DXBC disk cache: a cold start
/// pays ~22 D3DCompile calls once, every later start reads the bytecode back from
/// <c>%TEMP%\fluent-gpu\shadercache</c> (same location family as the engine's <c>DiskImageCache</c>). The cache is
/// pure acceleration — every failure mode (read-only FS, corrupt entry, concurrent writer) silently falls through to
/// a fresh compile.
/// </summary>
internal static unsafe class ShaderCompiler
{
    /// <summary>Bump to invalidate every cached entry (it is hashed into the key, so old files simply stop matching
    /// and age out via the 30-day sweep).</summary>
    private const int CacheFormatVersion = 1;

    private static readonly TimeSpan CacheMaxAge = TimeSpan.FromDays(30);

    // FG_DIAG cold-start attribution: per-compile ms to stderr. Runtime-gated (not Diag.CompiledIn) so the published
    // Release bench can attribute its own bring-up.
    private static readonly bool s_bootDiag = FluentGpu.Foundation.Diag.EnvFlag("FG_DIAG");

    private static readonly string s_cacheDir = Path.Combine(Path.GetTempPath(), "fluent-gpu", "shadercache");

    // 0 = not probed yet, 1 = directory ready, 2 = disabled (create failed ⇒ read-only FS).
    private static int s_cacheState;
    private static int s_swept;

    public static ID3DBlob* Compile(string source, string entry, string target, string? label = null)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();

        string? path = CacheEnabled() ? Path.Combine(s_cacheDir, HashKey(source, entry, target) + ".dxbc") : null;
        if (path is not null)
        {
            ID3DBlob* cached = TryLoad(path);
            if (cached != null)
            {
                if (s_bootDiag)
                {
                    double hitMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    Console.Error.WriteLine($"[boot.shader] {entry} ({target}): cache-hit {hitMs:F1}ms dxbc={(nuint)cached->GetBufferSize()}B");
                }
                return cached;
            }
        }

        byte[] src = Encoding.ASCII.GetBytes(source);
        byte[] ent = Encoding.ASCII.GetBytes(entry + "\0");
        byte[] tgt = Encoding.ASCII.GetBytes(target + "\0");
        ID3DBlob* code = null; ID3DBlob* err = null;
        fixed (byte* ps = src) fixed (byte* pe = ent) fixed (byte* pt = tgt)
        {
            HRESULT hr = D3DCompile(ps, (nuint)src.Length, null, null, null, (sbyte*)pe, (sbyte*)pt, 0, 0, &code, &err);
            if ((int)hr < 0)
            {
                string msg = err != null ? Marshal.PtrToStringAnsi((nint)err->GetBufferPointer()) ?? "" : "";
                if (err != null) err->Release();
                string what = label is null ? "shader" : label + " shader";
                throw new InvalidOperationException($"{what} {entry} ({target}) failed: {msg}");
            }
        }
        if (err != null) err->Release();   // warnings blob on an otherwise-successful compile

        if (path is not null)
        {
            TryStore(path, code);
            SweepOnce();
        }

        if (s_bootDiag)
        {
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Console.Error.WriteLine($"[boot.shader] {entry} ({target}): {ms:F1}ms src={src.Length}B dxbc={(nuint)code->GetBufferSize()}B");
        }
        return code;
    }

    /// <summary>SHA256 over UTF8(source) ‖ 0 ‖ entry ‖ 0 ‖ target ‖ 0 ‖ <see cref="CacheFormatVersion"/>.</summary>
    private static string HashKey(string source, string entry, string target)
    {
        int cap = Encoding.UTF8.GetMaxByteCount(source.Length + entry.Length + target.Length) + 3 + sizeof(int);
        byte[] buf = new byte[cap];
        int o = Encoding.UTF8.GetBytes(source, buf);
        buf[o++] = 0;
        o += Encoding.UTF8.GetBytes(entry, buf.AsSpan(o));
        buf[o++] = 0;
        o += Encoding.UTF8.GetBytes(target, buf.AsSpan(o));
        buf[o++] = 0;
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(o), CacheFormatVersion);
        o += sizeof(int);

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(buf.AsSpan(0, o), hash);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Lazily creates the cache directory once; a failure permanently disables the cache for this process.
    /// Concurrent callers race benignly (<c>CreateDirectory</c> is idempotent).</summary>
    private static bool CacheEnabled()
    {
        int state = Volatile.Read(ref s_cacheState);
        if (state != 0) return state == 1;
        try
        {
            Directory.CreateDirectory(s_cacheDir);
            Volatile.Write(ref s_cacheState, 1);
            return true;
        }
        catch   // read-only FS / denied ⇒ cache silently disabled
        {
            Volatile.Write(ref s_cacheState, 2);
            return false;
        }
    }

    /// <summary>Returns a blob holding the cached DXBC, or <c>null</c> for miss/corrupt/unreadable (⇒ fresh compile).</summary>
    private static ID3DBlob* TryLoad(string path)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch { return null; }   // missing, locked by a concurrent writer, unreadable — all just a miss

        if (bytes.Length < 4 || bytes[0] != (byte)'D' || bytes[1] != (byte)'X' || bytes[2] != (byte)'B' || bytes[3] != (byte)'C')
            return null;

        ID3DBlob* blob = null;
        if ((int)D3DCreateBlob((nuint)bytes.Length, &blob) < 0 || blob == null) return null;
        fixed (byte* p = bytes)
            Buffer.MemoryCopy(p, blob->GetBufferPointer(), bytes.Length, bytes.Length);
        return blob;
    }

    /// <summary>Best-effort atomic publish (unique temp + overwrite move). A move race loser throws IOException and is
    /// ignored — by construction the winner's bytes are identical.</summary>
    private static void TryStore(string path, ID3DBlob* code)
    {
        nuint size = code->GetBufferSize();
        if (size == 0 || size > int.MaxValue) return;

        string tmp = $"{path}.{Environment.ProcessId:x}-{Environment.CurrentManagedThreadId:x}.tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                fs.Write(new ReadOnlySpan<byte>(code->GetBufferPointer(), (int)size));
            File.Move(tmp, path, overwrite: true);
        }
        catch (IOException) { TryDelete(tmp); }
        catch (UnauthorizedAccessException) { TryDelete(tmp); }
    }

    /// <summary>On the first compile miss of the process, drop entries older than 30 days (stale shader revisions).</summary>
    private static void SweepOnce()
    {
        if (Interlocked.Exchange(ref s_swept, 1) != 0) return;
        try
        {
            DateTime cutoff = DateTime.UtcNow - CacheMaxAge;
            foreach (var f in new DirectoryInfo(s_cacheDir).GetFiles())
            {
                try { if (f.LastWriteTimeUtc < cutoff) f.Delete(); } catch { }
            }
        }
        catch { }
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
}
