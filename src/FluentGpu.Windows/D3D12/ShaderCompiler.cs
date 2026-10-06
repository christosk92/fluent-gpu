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

    // `--fg diag` cold-start attribution: per-compile ms to stderr. Runtime-gated (not Diag.CompiledIn) so the published
    // Release bench can attribute its own bring-up.
    private static bool s_bootDiag => FluentGpu.Hosting.EngineSwitches.DiagConsole;

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

    /// <summary>Returns a blob holding the cached DXBC, or <c>null</c> for miss/corrupt/unreadable (⇒ fresh compile).
    /// The blob is the engine's own <see cref="CachedBlob"/>, not <c>D3DCreateBlob</c>: a warm start then never loads
    /// d3dcompiler_47.dll at all (only a cache miss does, for <c>D3DCompile</c>).</summary>
    private static ID3DBlob* TryLoad(string path)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch { return null; }   // missing, locked by a concurrent writer, unreadable — all just a miss

        if (bytes.Length < 4 || bytes[0] != (byte)'D' || bytes[1] != (byte)'X' || bytes[2] != (byte)'B' || bytes[3] != (byte)'C')
            return null;

        return CachedBlob.Create(bytes);
    }

    /// <summary>A minimal native <c>ID3DBlob</c> (IUnknown + GetBufferPointer/GetBufferSize) over one unmanaged block:
    /// vtable pointer, reference count, size, then the bytes. Callers use it exactly like a D3DCompile blob (read the
    /// bytecode, <c>Release</c> once); the last Release frees the block.</summary>
    internal static class CachedBlob
    {
        private static readonly void** s_vtbl = CreateVtbl();

        private static void** CreateVtbl()
        {
            void** v = (void**)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)(5 * sizeof(void*)));
            v[0] = (delegate* unmanaged<void*, Guid*, void**, int>)&QueryInterface;
            v[1] = (delegate* unmanaged<void*, uint>)&AddRef;
            v[2] = (delegate* unmanaged<void*, uint>)&Release;
            v[3] = (delegate* unmanaged<void*, void*>)&GetBufferPointer;
            v[4] = (delegate* unmanaged<void*, nuint>)&GetBufferSize;
            return v;
        }

        // [0] vtbl  [1] refcount (low 32 bits)  [2] size  [3..] bytes — 8-byte aligned, so the bytecode is too.
        private const int HeaderBytes = 3 * 8;

        public static ID3DBlob* Create(byte[] bytes)
        {
            byte* block = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)(HeaderBytes + bytes.Length));
            ((void***)block)[0] = s_vtbl;
            ((long*)block)[1] = 1;
            ((ulong*)block)[2] = (ulong)bytes.Length;
            fixed (byte* p = bytes) Buffer.MemoryCopy(p, block + HeaderBytes, bytes.Length, bytes.Length);
            return (ID3DBlob*)block;
        }

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static int QueryInterface(void* self, Guid* riid, void** ppv)
        {
            if (ppv == null) return unchecked((int)0x80004003);   // E_POINTER
            // IUnknown and ID3D10Blob (= ID3DBlob, 8BA5FB08-5195-40E2-AC58-0D989C3A0102) only.
            if (*riid == new Guid(0x00000000, 0x0000, 0x0000, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46)
                || *riid == new Guid(0x8BA5FB08, 0x5195, 0x40E2, 0xAC, 0x58, 0x0D, 0x98, 0x9C, 0x3A, 0x01, 0x02))
            {
                Interlocked.Increment(ref ((long*)self)[1]);
                *ppv = self;
                return 0;
            }
            *ppv = null;
            return unchecked((int)0x80004002);                     // E_NOINTERFACE
        }

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static uint AddRef(void* self) => (uint)Interlocked.Increment(ref ((long*)self)[1]);

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static uint Release(void* self)
        {
            long n = Interlocked.Decrement(ref ((long*)self)[1]);
            if (n == 0) System.Runtime.InteropServices.NativeMemory.Free(self);
            return (uint)n;
        }

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void* GetBufferPointer(void* self) => (byte*)self + HeaderBytes;

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static nuint GetBufferSize(void* self) => (nuint)((ulong*)self)[2];
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
