using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Wavee.Backend.Audio;

namespace Wavee.AudioHost.PlayPlay;

/// <summary>
/// Direct-call PlayPlay AES-key unwrapper. Loads <c>Spotify.dll</c> via
/// <c>LoadLibrary</c>, dispatches on the matching
/// <see cref="PlayPlayConfig"/> selected by SHA-256, and calls
/// <c>vm_runtime_init</c> + <c>vm_object_transform</c> as ordinary native
/// function pointers. No CPU emulator.
/// </summary>
/// <remarks>
/// <para>The host process arch must match the loaded DLL's arch:</para>
/// <list type="bullet">
///   <item>x64 v1.2.88.483 → x64 host (works on ARM64 hosts via OS x64 emulation)</item>
///   <item>ARM64 v1.2.89.539 → ARM64 host (no cross-arch fallback possible)</item>
/// </list>
/// <para>Memory layout per call: <c>vm_obj</c> (config-sized, opaque cipher
/// state), <c>rt_context</c> (16 B), <c>init_value</c> (16 B from config),
/// <c>obf_key</c> (16 B per request), <c>derived_key</c> (24 B scratch).</para>
/// <para>AES-key extraction is arch/version-specific via
/// <see cref="AesKeyExtraction"/>:</para>
/// <list type="bullet">
///   <item><see cref="AesKeyExtraction.TriggerRipBreakpoint"/> — x64 v1.2.88
///     route. The key transits the configured CPU register at TRIGGER_RIP
///     inside <c>vm_object_transform</c>. We patch a single <c>0xCC</c>
///     there and snapshot the register from a vectored exception handler.</item>
///   <item><see cref="AesKeyExtraction.OutputBufferSlice"/> — derived_key
///     contains the AES key as a contiguous slice. Read directly post-call.</item>
///   <item><see cref="AesKeyExtraction.PostProcessCall"/> — ARM64 v1.2.89
///     route. After <c>vm_object_transform</c> returns, a follow-on function
///     finalizes the key into a session-shaped buffer.
///     <strong>Signature pending Step 0 RE</strong> (see
///     <c>tools/playplay_arm64_capture.windbg</c>) — currently throws until
///     the WinDBG capture confirms argument layout.</item>
/// </list>
/// <para>This class is NOT thread-safe — derivations must be serialised by
/// the caller. The dispatcher in <c>AudioHostService</c> already enforces
/// one-pipe-message-at-a-time semantics.</para>
/// </remarks>
public sealed unsafe class PlayPlayKeyEmulator : IDisposable
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryExW(string lpFileName, IntPtr hFile, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr AddVectoredExceptionHandler(uint first, IntPtr handler);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr RemoveVectoredExceptionHandler(IntPtr handler);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualProtect(IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

    private const uint PAGE_EXECUTE_READWRITE = 0x40;
    private const uint EXCEPTION_BREAKPOINT = 0x80000003;
    private const int EXCEPTION_CONTINUE_EXECUTION = -1;
    private const int EXCEPTION_CONTINUE_SEARCH = 0;
    private const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x00000008;

    private const int CONTEXT_RIP_OFFSET = 0xF8; // x64 CONTEXT.Rip

    private readonly Action<string> _log;
    private readonly PlayPlayConfig _config;

    private readonly IntPtr _moduleBase;
    private readonly byte[] _vmObjSnapshot;
    private readonly IntPtr _vmObj, _rtContext, _initValue, _obfKey, _derivedKey;

    private readonly delegate* unmanaged[Cdecl]<IntPtr, IntPtr, ulong, void> _vmRuntimeInit;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void> _vmObjectTransform;

    // TriggerRipBreakpoint state — used only when AesKey is that variant.
    // The vectored exception handler is *not* persistently installed; it gets
    // registered immediately before each derivation and removed immediately
    // after, so a process snapshot at any other time sees no installed handler.
    private readonly IntPtr _aesKeyTriggerRip;
    private byte _origTriggerByte;
    private readonly byte[] _capturedKey;
    private bool _capturedFlag;
    private readonly int _captureRegOffset;
    private delegate int VectoredHandlerDelegate(IntPtr exceptionPointers);
    private readonly VectoredHandlerDelegate? _handlerDelegate;
    private readonly IntPtr _handlerPtr;

    // PostProcessCall state.
    private readonly IntPtr _postProcessFn;
    private readonly IntPtr _postProcessSession;
    private readonly int _postProcessOutputOffset;
    private const int PostProcessSessionSize = 256; // generous; covers offset 0xa8 + tail metadata

    private bool _disposed;

    /// <summary>
    /// Loads <paramref name="spotifyDllPath"/>, verifies its SHA-256 against
    /// <paramref name="config"/>, hot-patches <c>fill_random_bytes</c> with an
    /// arch-appropriate NOP-return, prepares the AES-key extraction strategy,
    /// and runs <c>vm_runtime_init</c> once to capture a vm_obj snapshot for
    /// fast per-call reset.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// SHA-256 didn't match the supplied config, host arch mismatches the
    /// config arch, LoadLibrary failed, or VirtualProtect denied write access.
    /// </exception>
    /// <exception cref="FileNotFoundException">DLL path doesn't exist.</exception>
    public PlayPlayKeyEmulator(string spotifyDllPath, PlayPlayConfig config, Action<string> log)
    {
        _log = log;
        _config = config ?? throw new ArgumentNullException(nameof(config));

        if (!File.Exists(spotifyDllPath))
            throw new FileNotFoundException($"Spotify.dll not found at {spotifyDllPath}", spotifyDllPath);

        var dllBytes = File.ReadAllBytes(spotifyDllPath);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(dllBytes, hash);

        if (!hash.SequenceEqual(_config.Sha256))
        {
            throw new InvalidOperationException(
                $"Spotify.dll at {spotifyDllPath} (SHA-256 {Convert.ToHexString(hash)}) does not match " +
                $"the supplied PlayPlayConfig (expected {Convert.ToHexString(_config.Sha256)}, v{_config.Version}).");
        }

        if (RuntimeInformation.ProcessArchitecture != _config.Arch)
        {
            throw new InvalidOperationException(
                $"Architecture mismatch: Spotify.dll is {_config.Arch} (v{_config.Version}) but this " +
                $"AudioHost process is {RuntimeInformation.ProcessArchitecture}. Spawn the matching " +
                "AudioHost variant (AudioProcessManager should pick this for you).");
        }

        // Use LoadLibraryExW with LOAD_WITH_ALTERED_SEARCH_PATH so the loader
        // searches the DLL's own directory for any dependencies first, rather
        // than the standard search order. Removes a DLL-search-order-hijack
        // heuristic some EDR products fire on for binaries loaded out of
        // %LOCALAPPDATA%.
        var module = LoadLibraryExW(spotifyDllPath, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH);
        if (module == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"LoadLibraryEx failed for {spotifyDllPath} (Win32 error {err})");
        }
        _moduleBase = module;

        _vmRuntimeInit = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, ulong, void>)
            Rebase(_config.VmRuntimeInitVa);
        _vmObjectTransform = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>)
            Rebase(_config.VmObjectTransformVa);

        _capturedKey = new byte[_config.KeySize];

        // Stub fill_random_bytes so derivation is deterministic. Without this,
        // every call would produce a different AES key.
        NoopFillRandomBytes();

        // Per-strategy setup.
        switch (_config.AesKey)
        {
            case AesKeyExtraction.TriggerRipBreakpoint trb:
                _aesKeyTriggerRip = Rebase(trb.RipVa);
                _captureRegOffset = trb.ContextRegOffset;
                _origTriggerByte = Marshal.ReadByte(_aesKeyTriggerRip);
                WriteCode(_aesKeyTriggerRip, [0xCC]);
                // Store the function pointer for the handler delegate, but
                // DON'T install it persistently. Each DeriveAesKey call adds
                // the handler, runs the transform, removes the handler.
                _handlerDelegate = OnVectoredException;
                _handlerPtr = Marshal.GetFunctionPointerForDelegate(_handlerDelegate);
                break;

            case AesKeyExtraction.OutputBufferSlice obs:
                if (obs.OffsetBytes < 0 || obs.LengthBytes != _config.KeySize ||
                    obs.OffsetBytes + obs.LengthBytes > _config.DerivedKeySize)
                {
                    throw new InvalidOperationException(
                        $"OutputBufferSlice {obs.OffsetBytes}..+{obs.LengthBytes} doesn't fit in " +
                        $"derived_key ({_config.DerivedKeySize}B) for v{_config.Version}");
                }
                break;

            case AesKeyExtraction.PostProcessCall ppc:
                _postProcessFn = Rebase(ppc.FunctionVa);
                _postProcessSession = Marshal.AllocHGlobal(PostProcessSessionSize);
                _postProcessOutputOffset = ppc.OutputOffsetBytes;
                if (_postProcessOutputOffset < 0 ||
                    _postProcessOutputOffset + _config.KeySize > PostProcessSessionSize)
                {
                    throw new InvalidOperationException(
                        $"PostProcessCall output offset 0x{_postProcessOutputOffset:X} + {_config.KeySize}B " +
                        $"exceeds session buffer size {PostProcessSessionSize}");
                }
                break;

            default:
                throw new InvalidOperationException(
                    $"Unknown AesKeyExtraction strategy {_config.AesKey.GetType().Name}");
        }

        _vmObj = Marshal.AllocHGlobal(_config.VmObjectSize);
        _rtContext = Marshal.AllocHGlobal(_config.RtContextSize);
        _initValue = Marshal.AllocHGlobal(_config.InitValueSize);
        _obfKey = Marshal.AllocHGlobal(_config.ObfuscatedKeySize);
        _derivedKey = Marshal.AllocHGlobal(_config.DerivedKeySize);

        Span<byte> rtBytes = stackalloc byte[_config.RtContextSize];
        rtBytes.Clear();
        var ctxVa = (long)Rebase(_config.RuntimeContextVa);
        BinaryPrimitives.WriteInt64LittleEndian(rtBytes[8..], ctxVa);
        Marshal.Copy(rtBytes.ToArray(), 0, _rtContext, rtBytes.Length);

        Marshal.Copy(_config.VmInitValue, 0, _initValue, _config.InitValueSize);

        _vmRuntimeInit(_vmObj, _rtContext, 1);

        _vmObjSnapshot = new byte[_config.VmObjectSize];
        Marshal.Copy(_vmObj, _vmObjSnapshot, 0, _vmObjSnapshot.Length);

        _log($"PlayPlayKeyEmulator initialised (Spotify.dll v{_config.Version} {_config.Arch}, strategy={_config.AesKey.GetType().Name}, module=0x{_moduleBase.ToInt64():X})");
    }

    /// <summary>
    /// Derives a 16-byte AES audio-decryption key from a 16-byte obfuscated
    /// key. <paramref name="contentId16"/> is currently unused by Spotify's
    /// PlayPlay v5 cipher but is kept on the API surface in case a future
    /// version reintroduces it.
    /// </summary>
    public byte[] DeriveAesKey(ReadOnlySpan<byte> obfuscatedKey16, ReadOnlySpan<byte> contentId16)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (obfuscatedKey16.Length != _config.ObfuscatedKeySize)
            throw new ArgumentException($"obfuscated key must be {_config.ObfuscatedKeySize} bytes", nameof(obfuscatedKey16));
        if (contentId16.Length != _config.ContentIdSize)
            throw new ArgumentException($"content id must be {_config.ContentIdSize} bytes", nameof(contentId16));

        Marshal.Copy(_vmObjSnapshot, 0, _vmObj, _vmObjSnapshot.Length);
        var obfArr = obfuscatedKey16.ToArray();
        Marshal.Copy(obfArr, 0, _obfKey, _config.ObfuscatedKeySize);

        switch (_config.AesKey)
        {
            case AesKeyExtraction.TriggerRipBreakpoint:
                // Re-patch with int3 (the previous derive's vectored handler restored
                // the original byte) and reset capture state.
                WriteCode(_aesKeyTriggerRip, [0xCC]);
                _capturedFlag = false;
                Array.Clear(_capturedKey);

                // Install the vectored handler for the duration of the call only.
                // Persistent VEH on EXCEPTION_BREAKPOINT looks like an anti-debug
                // hook to AV/EDR; tightening to ~µs of presence per playback
                // makes the process look clean during snapshot scans.
                var vehHandle = AddVectoredExceptionHandler(1, _handlerPtr);
                if (vehHandle == IntPtr.Zero)
                    throw new InvalidOperationException(
                        $"AddVectoredExceptionHandler failed (Win32 {Marshal.GetLastWin32Error()})");
                try
                {
                    _vmObjectTransform(_vmObj, _obfKey, _derivedKey, _initValue);
                }
                finally
                {
                    RemoveVectoredExceptionHandler(vehHandle);
                }

                if (!_capturedFlag)
                    throw new InvalidOperationException(
                        $"AES key capture did not fire — TRIGGER_RIP 0x{_aesKeyTriggerRip.ToInt64():X} " +
                        $"never reached. Spotify.dll v{_config.Version} RVAs may have drifted.");

                return _capturedKey.AsSpan(0, _config.KeySize).ToArray();

            case AesKeyExtraction.OutputBufferSlice obs:
                _vmObjectTransform(_vmObj, _obfKey, _derivedKey, _initValue);
                var slice = new byte[_config.KeySize];
                Marshal.Copy(_derivedKey + obs.OffsetBytes, slice, 0, _config.KeySize);
                return slice;

            case AesKeyExtraction.PostProcessCall:
                _vmObjectTransform(_vmObj, _obfKey, _derivedKey, _initValue);
                return InvokePostProcess();

            default:
                throw new InvalidOperationException(
                    $"Unknown AesKeyExtraction strategy {_config.AesKey.GetType().Name}");
        }
    }

    /// <summary>
    /// PostProcessCall executor: invoke the finalisation function and read
    /// the AES key out of the session-style buffer at the configured offset.
    /// </summary>
    /// <remarks>
    /// The exact ARM64 calling convention for FUN_180CF6210 is pending the
    /// Step 0 WinDBG capture (<c>tools/playplay_arm64_capture.windbg</c>).
    /// Once confirmed, replace the <c>throw</c> below with the right
    /// <c>delegate* unmanaged</c> signature and argument pattern. Likely
    /// shape based on the partial RE in
    /// <c>another-unplayplay/fixtures/arm64_v1.2.89.539_consts.json:108-125</c>:
    /// the function takes the 24-byte derived_key and writes the 16-byte AES
    /// key into a session struct at <c>+OutputOffsetBytes</c>. Until that's
    /// nailed down, ARM64 v1.2.89.539 derivation will throw rather than
    /// silently return wrong bytes.
    /// </remarks>
    private byte[] InvokePostProcess()
    {
        // Zero the session buffer so we can detect "function didn't write the
        // expected bytes" failure mode.
        for (int i = 0; i < PostProcessSessionSize; i++)
            Marshal.WriteByte(_postProcessSession, i, 0);

        // TODO(step-0): replace this throw with the actual call once Step 0
        // confirms FUN_180CF6210's signature (number of args, what each arg
        // points to, whether the session struct needs prior initialisation
        // beyond zeroing). Pseudocode of the expected shape:
        //
        //   var fn = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)_postProcessFn;
        //   fn(_derivedKey, _postProcessSession);
        //   var key = new byte[_config.KeySize];
        //   Marshal.Copy(_postProcessSession + _postProcessOutputOffset, key, 0, key.Length);
        //   return key;

        throw new NotImplementedException(
            $"PostProcessCall execution for v{_config.Version} {_config.Arch} is pending Step 0 " +
            "RE — run tools/playplay_arm64_capture.windbg against a live ARM64 Spotify session " +
            "to capture FUN_180CF6210's argument layout, then wire it in here. The captured " +
            "derived_key is the 24-byte buffer at _derivedKey; the session-style output buffer " +
            $"is _postProcessSession (ZEROED, {PostProcessSessionSize} B); read AES from " +
            $"_postProcessSession+0x{_postProcessOutputOffset:X}.");
    }

    private void NoopFillRandomBytes()
    {
        var addr = Rebase(_config.FillRandomBytesVa);

        // Arch-conditional NOP-return instruction sequence:
        //   x64:   xor eax, eax  ; ret           → 31 C0 C3                (3 B)
        //   ARM64: mov w0, #0    ; ret           → 00 00 80 52 C0 03 5F D6 (8 B)
        // The ARM64 encoding is little-endian: instruction 1 = 0x52800000,
        // instruction 2 = 0xD65F03C0.
        ReadOnlySpan<byte> noop = _config.Arch switch
        {
            Architecture.X64 => [0x31, 0xC0, 0xC3],
            Architecture.Arm64 => [0x00, 0x00, 0x80, 0x52, 0xC0, 0x03, 0x5F, 0xD6],
            _ => throw new PlatformNotSupportedException($"NoopFillRandomBytes: unsupported arch {_config.Arch}"),
        };
        WriteCode(addr, noop);
    }

    private static void WriteCode(IntPtr addr, ReadOnlySpan<byte> bytes)
    {
        if (!VirtualProtect(addr, (UIntPtr)bytes.Length, PAGE_EXECUTE_READWRITE, out var oldProt))
            throw new InvalidOperationException(
                $"VirtualProtect failed at {addr.ToInt64():X} (Win32 {Marshal.GetLastWin32Error()})");
        for (int i = 0; i < bytes.Length; i++)
            Marshal.WriteByte(addr, i, bytes[i]);
        VirtualProtect(addr, (UIntPtr)bytes.Length, oldProt, out _);
    }

    private int OnVectoredException(IntPtr ep)
    {
        var rec = Marshal.ReadIntPtr(ep, 0);
        var ctx = Marshal.ReadIntPtr(ep, IntPtr.Size);

        var code = (uint)Marshal.ReadInt32(rec, 0);
        if (code != EXCEPTION_BREAKPOINT) return EXCEPTION_CONTINUE_SEARCH;

        var faultAddr = Marshal.ReadIntPtr(rec, 16);
        if (faultAddr != _aesKeyTriggerRip) return EXCEPTION_CONTINUE_SEARCH;

        var keyPtr = (IntPtr)Marshal.ReadInt64(ctx, _captureRegOffset);
        Marshal.Copy(keyPtr, _capturedKey, 0, _config.KeySize);
        _capturedFlag = true;

        WriteCode(_aesKeyTriggerRip, [_origTriggerByte]);
        var rip = Marshal.ReadInt64(ctx, CONTEXT_RIP_OFFSET);
        Marshal.WriteInt64(ctx, CONTEXT_RIP_OFFSET, rip - 1);

        return EXCEPTION_CONTINUE_EXECUTION;
    }

    private IntPtr Rebase(ulong va)
    {
        var rva = va - _config.AnalysisBase;
        return (IntPtr)((ulong)_moduleBase + rva);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // No persistent VEH to remove — it's added/removed per derivation.
        if (_aesKeyTriggerRip != IntPtr.Zero)
        {
            try { WriteCode(_aesKeyTriggerRip, [_origTriggerByte]); } catch { }
        }

        if (_vmObj != IntPtr.Zero) Marshal.FreeHGlobal(_vmObj);
        if (_rtContext != IntPtr.Zero) Marshal.FreeHGlobal(_rtContext);
        if (_initValue != IntPtr.Zero) Marshal.FreeHGlobal(_initValue);
        if (_obfKey != IntPtr.Zero) Marshal.FreeHGlobal(_obfKey);
        if (_derivedKey != IntPtr.Zero) Marshal.FreeHGlobal(_derivedKey);
        if (_postProcessSession != IntPtr.Zero) Marshal.FreeHGlobal(_postProcessSession);
    }
}