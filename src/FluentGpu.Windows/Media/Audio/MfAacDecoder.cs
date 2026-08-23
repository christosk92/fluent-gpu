using System.Runtime.InteropServices;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Media.Windows;

/// <summary>
/// The AAC decode leaf: Microsoft's in-box AAC Decoder MFT (<c>CLSID_MSAACDecMFT</c>) driven directly as an
/// <see cref="IMFTransform"/>, frame in / PCM out. This is the ONE piece of live-radio decoding the app cannot do in
/// managed code — every AAC station (and every <c>audio/aacp</c> stream) needs it, and shipping a managed AAC decoder
/// is neither legally nor practically on the table.
///
/// <para><b>Why a bare MFT and not the Media Engine / Source Reader.</b> Both of those want a byte-stream they can seek
/// and probe. A live ICY socket is neither. Feeding the transform one ADTS frame at a time keeps the app's own
/// transport (ring buffer, reconnect, ICY demux) in charge and leaves the MFT doing exactly one job.</para>
///
/// <para><b>ADTS is the input framing</b> (<c>MF_MT_AAC_PAYLOAD_TYPE = 1</c>): the 7/9-byte header stays ON the frame
/// handed to <see cref="TryPushFrame"/>. The configuration the MFT still needs up front — channel count, CORE sample
/// rate, and a 2-byte AudioSpecificConfig appended to a 12-byte <c>HEAACWAVEINFO</c> tail in <c>MF_MT_USER_DATA</c> —
/// comes from the caller, which parsed the first frame's header.</para>
///
/// <para><b>HE-AAC is implicit.</b> With SBR/PS signalled in-band rather than in a container, the transform accepts the
/// core-rate input type, then answers the first <c>ProcessOutput</c> with <c>MF_E_TRANSFORM_STREAM_CHANGE</c> and a
/// DOUBLED output rate. That is normal, not a fault: the decoder renegotiates its output type, raises
/// <see cref="FormatChanged"/>, and the caller is expected to PRIME (decode a few frames and discard) before it
/// publishes <see cref="OutputSampleRate"/>/<see cref="OutputChannels"/> to the mixer.</para>
///
/// <para>Not thread-safe: one instance belongs to one decode thread, which is how the audio graph pulls anyway.</para>
/// </summary>
public sealed unsafe class MfAacDecoder : IDisposable
{
    const int S_OK = 0;
    const int S_FALSE = 1;
    const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);
    const uint COINIT_MULTITHREADED = 0x0;
    const uint MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x00000100;
    const int MinOutputBufferBytes = 64 * 1024;

    /// <summary>The HEAACWAVEINFO tail that follows a WAVEFORMATEX: <c>wPayloadType</c>,
    /// <c>wAudioProfileLevelIndication</c>, <c>wStructType</c>, <c>wReserved1</c>, <c>dwReserved2</c>.</summary>
    const int HeaacWaveInfoTailBytes = 12;

    static int _availability;   // 0 = unknown, 1 = yes, 2 = no

    IMFTransform* _transform;
    IMFSample* _outSample;
    IMFMediaBuffer* _outBuffer;
    int _outBufferBytes;
    bool _mfStarted;
    bool _coInitialized;
    bool _disposed;

    float[] _pending = [];
    int _pendingStart;
    int _pendingCount;

    /// <summary>Create and configure the decoder for a stream whose first ADTS frame declared
    /// <paramref name="coreSampleRate"/> / <paramref name="channels"/> and the given AudioSpecificConfig.</summary>
    /// <param name="coreSampleRate">The ADTS header's sample rate. With HE-AAC the OUTPUT rate ends up double this.</param>
    /// <param name="channels">The ADTS channel configuration (0 is treated as stereo).</param>
    /// <param name="audioSpecificConfig">The 2-byte MPEG-4 AudioSpecificConfig for this stream.</param>
    /// <param name="profileLevelIndication">HEAACWAVEINFO's <c>wAudioProfileLevelIndication</c>; 0x29 (LC, level 2) is
    /// the value every broadcast AAC stream works with and is what the caller should leave alone.</param>
    public MfAacDecoder(int coreSampleRate, int channels, ReadOnlySpan<byte> audioSpecificConfig, ushort profileLevelIndication = 0x29)
    {
        if (coreSampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(coreSampleRate));
        int ch = channels <= 0 ? 2 : channels;

        int hr = CoInitializeEx(null, COINIT_MULTITHREADED);
        // S_FALSE = already initialised on this thread; RPC_E_CHANGED_MODE = someone else picked the apartment. Both are
        // fine for an MFT: it is free-threaded, and we must NOT uninitialise an apartment we did not create.
        if (hr == S_OK) _coInitialized = true;
        else if (hr != S_FALSE && hr != RPC_E_CHANGED_MODE) throw Fail("CoInitializeEx", hr);

        try
        {
            hr = MFStartup((uint)MF.MF_VERSION, (uint)MFSTARTUP_NOSOCKET);
            if (hr < 0) throw Fail("MFStartup", hr);
            _mfStarted = true;

            Guid clsid = CLSID.CLSID_MSAACDecMFT;
            Guid iid = IID.IID_IMFTransform;
            IMFTransform* transform;
            hr = CoCreateInstance(&clsid, null, (uint)CLSCTX.CLSCTX_INPROC_SERVER, &iid, (void**)&transform);
            if (hr < 0 || transform == null) throw Fail("CoCreateInstance(CLSID_MSAACDecMFT)", hr);
            _transform = transform;

            SetInputType(coreSampleRate, ch, audioSpecificConfig, profileLevelIndication);
            NegotiateOutputType();
            AllocateOutputSample();

            hr = _transform->ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, (nuint)0);
            if (hr < 0) throw Fail("ProcessMessage(NOTIFY_BEGIN_STREAMING)", hr);
            hr = _transform->ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_START_OF_STREAM, (nuint)0);
            if (hr < 0) throw Fail("ProcessMessage(NOTIFY_START_OF_STREAM)", hr);
        }
        catch
        {
            ReleaseAll();
            throw;
        }
    }

    /// <summary>The rate the transform is currently PRODUCING (double the core rate once SBR kicks in).</summary>
    public int OutputSampleRate { get; private set; }

    /// <summary>The channel count the transform is currently producing.</summary>
    public int OutputChannels { get; private set; }

    /// <summary>True when output samples are 32-bit float; false means 16-bit PCM (already scaled on the way out).</summary>
    public bool OutputIsFloat { get; private set; }

    /// <summary>Set when the transform renegotiated its output format. Expected exactly once, during priming (implicit
    /// HE-AAC); after that it means the station changed encoding mid-stream, which the caller should treat as a fault.</summary>
    public bool FormatChanged { get; private set; }

    /// <summary>Clear <see cref="FormatChanged"/> after the caller has absorbed the new format.</summary>
    public void ClearFormatChanged() => FormatChanged = false;

    /// <summary>Whether the in-box AAC decoder is present. Windows "N" editions ship without the media feature pack, so
    /// this is a real runtime condition, not a formality — the host maps false to a typed <c>ArchUnsupported</c> fault.</summary>
    public static bool IsAvailable()
    {
        int cached = Volatile.Read(ref _availability);
        if (cached != 0) return cached == 1;

        bool ok = false;
        int hr = CoInitializeEx(null, COINIT_MULTITHREADED);
        bool owned = hr == S_OK;
        if (hr == S_OK || hr == S_FALSE || hr == RPC_E_CHANGED_MODE)
        {
            Guid clsid = CLSID.CLSID_MSAACDecMFT;
            Guid iid = IID.IID_IMFTransform;
            IMFTransform* probe;
            if (CoCreateInstance(&clsid, null, (uint)CLSCTX.CLSCTX_INPROC_SERVER, &iid, (void**)&probe) >= 0 && probe != null)
            {
                probe->Release();
                ok = true;
            }
            if (owned) CoUninitialize();
        }
        Volatile.Write(ref _availability, ok ? 1 : 2);
        return ok;
    }

    /// <summary>Hand one complete ADTS frame (header included) to the transform. Returns false when the transform is not
    /// accepting input — drain it with <see cref="ReadSamples"/> and retry.</summary>
    public bool TryPushFrame(ReadOnlySpan<byte> adtsFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (adtsFrame.IsEmpty) return true;

        IMFMediaBuffer* buffer = null;
        IMFSample* sample = null;
        try
        {
            int hr = MFCreateMemoryBuffer((uint)adtsFrame.Length, &buffer);
            if (hr < 0) throw Fail("MFCreateMemoryBuffer", hr);

            byte* dst;
            uint max, cur;
            hr = buffer->Lock(&dst, &max, &cur);
            if (hr < 0) throw Fail("IMFMediaBuffer::Lock", hr);
            adtsFrame.CopyTo(new Span<byte>(dst, (int)max));
            buffer->Unlock();
            hr = buffer->SetCurrentLength((uint)adtsFrame.Length);
            if (hr < 0) throw Fail("IMFMediaBuffer::SetCurrentLength", hr);

            hr = MFCreateSample(&sample);
            if (hr < 0) throw Fail("MFCreateSample", hr);
            hr = sample->AddBuffer(buffer);
            if (hr < 0) throw Fail("IMFSample::AddBuffer", hr);

            hr = _transform->ProcessInput(0, sample, 0);
            if (hr == MF.MF_E_NOTACCEPTING) return false;
            if (hr < 0) throw Fail("IMFTransform::ProcessInput", hr);
            return true;
        }
        finally
        {
            if (sample != null) sample->Release();
            if (buffer != null) buffer->Release();
        }
    }

    /// <summary>Drain decoded interleaved float samples. Returns 0 when the transform needs more input (or has just
    /// renegotiated its output format — check <see cref="FormatChanged"/>); call again after another
    /// <see cref="TryPushFrame"/>.</summary>
    public int ReadSamples(Span<float> dst)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (dst.IsEmpty) return 0;

        if (_pendingCount == 0 && !ProcessOutputOnce()) return 0;
        if (_pendingCount == 0) return 0;

        int n = Math.Min(dst.Length, _pendingCount);
        _pending.AsSpan(_pendingStart, n).CopyTo(dst);
        _pendingStart += n;
        _pendingCount -= n;
        if (_pendingCount == 0) _pendingStart = 0;
        return n;
    }

    /// <summary>Drop everything the transform is holding (after a splice / before a reconnect resync).</summary>
    public void Flush()
    {
        if (_disposed || _transform == null) return;
        _transform->ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_COMMAND_FLUSH, (nuint)0);
        _pendingStart = 0;
        _pendingCount = 0;
    }

    // ── configuration ────────────────────────────────────────────────────────────────────────────────────────────────

    void SetInputType(int coreSampleRate, int channels, ReadOnlySpan<byte> asc, ushort profileLevel)
    {
        IMFMediaType* type = null;
        try
        {
            int hr = MFCreateMediaType(&type);
            if (hr < 0) throw Fail("MFCreateMediaType(input)", hr);

            Guid major = MF.MF_MT_MAJOR_TYPE, audio = MFMediaType_Audio;
            if ((hr = type->SetGUID(&major, &audio)) < 0) throw Fail("input MF_MT_MAJOR_TYPE", hr);
            Guid subtypeKey = MF.MF_MT_SUBTYPE, aac = MFAudioFormat.MFAudioFormat_AAC;
            if ((hr = type->SetGUID(&subtypeKey, &aac)) < 0) throw Fail("input MF_MT_SUBTYPE", hr);

            SetU32(type, MF.MF_MT_AUDIO_NUM_CHANNELS, (uint)channels);
            SetU32(type, MF.MF_MT_AUDIO_SAMPLES_PER_SECOND, (uint)coreSampleRate);
            SetU32(type, MF.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
            SetU32(type, MF.MF_MT_AAC_PAYLOAD_TYPE, 1);                                  // 1 = ADTS framing
            SetU32(type, MF.MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, profileLevel);

            // MF_MT_USER_DATA = the HEAACWAVEINFO bytes that follow WAVEFORMATEX, then the AudioSpecificConfig.
            Span<byte> userData = stackalloc byte[HeaacWaveInfoTailBytes + 8];
            userData.Clear();
            BitConverter.TryWriteBytes(userData[..2], (ushort)1);              // wPayloadType = ADTS
            BitConverter.TryWriteBytes(userData.Slice(2, 2), profileLevel);    // wAudioProfileLevelIndication
            // wStructType, wReserved1, dwReserved2 stay zero.
            int ascLength = Math.Min(asc.Length, userData.Length - HeaacWaveInfoTailBytes);
            if (ascLength > 0) asc[..ascLength].CopyTo(userData[HeaacWaveInfoTailBytes..]);
            int userDataLength = HeaacWaveInfoTailBytes + ascLength;

            Guid userDataKey = MF.MF_MT_USER_DATA;
            fixed (byte* p = userData)
                if ((hr = type->SetBlob(&userDataKey, p, (uint)userDataLength)) < 0) throw Fail("input MF_MT_USER_DATA", hr);

            if ((hr = _transform->SetInputType(0, type, 0)) < 0) throw Fail("IMFTransform::SetInputType", hr);
        }
        finally
        {
            if (type != null) type->Release();
        }
    }

    /// <summary>Ask the transform what it is willing to produce and take the best of it — float when offered (no
    /// conversion loss and no scaling), 16-bit PCM otherwise. Building a type by hand instead would risk disagreeing
    /// with the rate the transform actually chose, which is exactly what HE-AAC changes underneath us.</summary>
    void NegotiateOutputType()
    {
        IMFMediaType* chosen = null;
        bool chosenIsFloat = false;
        try
        {
            for (uint i = 0; ; i++)
            {
                IMFMediaType* candidate;
                int hr = _transform->GetOutputAvailableType(0, i, &candidate);
                if (hr < 0 || candidate == null) break;

                Guid subtypeKey = MF.MF_MT_SUBTYPE;
                Guid subtype;
                bool isFloat = false, isPcm = false;
                if (candidate->GetGUID(&subtypeKey, &subtype) >= 0)
                {
                    isFloat = subtype == MFAudioFormat.MFAudioFormat_Float;
                    isPcm = subtype == MFAudioFormat.MFAudioFormat_PCM;
                }

                if (isFloat && !chosenIsFloat)
                {
                    if (chosen != null) chosen->Release();
                    chosen = candidate;
                    chosenIsFloat = true;
                    continue;   // float is the best answer available; keep scanning only to drain the enumerator cheaply
                }
                if (isPcm && chosen == null)
                {
                    chosen = candidate;
                    continue;
                }
                candidate->Release();
            }

            if (chosen == null) throw new NotSupportedException("the AAC decoder MFT offered no PCM/float output type");

            int set = _transform->SetOutputType(0, chosen, 0);
            if (set < 0) throw Fail("IMFTransform::SetOutputType", set);
            OutputIsFloat = chosenIsFloat;
            ReadCurrentOutputFormat();
        }
        finally
        {
            if (chosen != null) chosen->Release();
        }
    }

    void ReadCurrentOutputFormat()
    {
        IMFMediaType* current = null;
        try
        {
            if (_transform->GetOutputCurrentType(0, &current) < 0 || current == null) return;
            OutputSampleRate = (int)GetU32(current, MF.MF_MT_AUDIO_SAMPLES_PER_SECOND, 0);
            OutputChannels = (int)GetU32(current, MF.MF_MT_AUDIO_NUM_CHANNELS, 2);
            Guid subtypeKey = MF.MF_MT_SUBTYPE;
            Guid subtype;
            if (current->GetGUID(&subtypeKey, &subtype) >= 0) OutputIsFloat = subtype == MFAudioFormat.MFAudioFormat_Float;
        }
        finally
        {
            if (current != null) current->Release();
        }
    }

    void AllocateOutputSample()
    {
        MFT_OUTPUT_STREAM_INFO info;
        int hr = _transform->GetOutputStreamInfo(0, &info);
        if (hr < 0) throw Fail("IMFTransform::GetOutputStreamInfo", hr);
        if ((info.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0)
            throw new NotSupportedException("the AAC decoder MFT unexpectedly allocates its own output samples");

        int needed = Math.Max((int)info.cbSize, MinOutputBufferBytes);
        if (_outSample != null && needed <= _outBufferBytes) return;

        ReleaseOutputSample();
        IMFMediaBuffer* buffer;
        if ((hr = MFCreateMemoryBuffer((uint)needed, &buffer)) < 0) throw Fail("MFCreateMemoryBuffer(output)", hr);
        IMFSample* sample;
        if ((hr = MFCreateSample(&sample)) < 0) { buffer->Release(); throw Fail("MFCreateSample(output)", hr); }
        if ((hr = sample->AddBuffer(buffer)) < 0) { sample->Release(); buffer->Release(); throw Fail("AddBuffer(output)", hr); }
        _outBuffer = buffer;
        _outSample = sample;
        _outBufferBytes = needed;
    }

    // ── the decode pump ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One <c>ProcessOutput</c>. Returns true when samples landed in <see cref="_pending"/>.</summary>
    bool ProcessOutputOnce()
    {
        _outBuffer->SetCurrentLength(0);

        MFT_OUTPUT_DATA_BUFFER output = default;
        output.dwStreamID = 0;
        output.pSample = _outSample;
        uint status = 0;
        int hr = _transform->ProcessOutput(0, 1, &output, &status);
        if (output.pEvents != null) { output.pEvents->Release(); output.pEvents = null; }

        if (hr == MF.MF_E_TRANSFORM_NEED_MORE_INPUT) return false;
        if (hr == MF.MF_E_TRANSFORM_STREAM_CHANGE)
        {
            // Implicit HE-AAC: the transform has decided on its real (doubled) output rate. Re-negotiate and tell the
            // caller — priming absorbs this; a change AFTER priming is a genuine mid-stream encoding change.
            NegotiateOutputType();
            AllocateOutputSample();
            FormatChanged = true;
            return false;
        }
        if (hr < 0) throw Fail("IMFTransform::ProcessOutput", hr);

        IMFMediaBuffer* buffer = null;
        try
        {
            if ((hr = _outSample->GetBufferByIndex(0, &buffer)) < 0 || buffer == null) throw Fail("GetBufferByIndex", hr);
            byte* src;
            uint max, cur;
            if ((hr = buffer->Lock(&src, &max, &cur)) < 0) throw Fail("output IMFMediaBuffer::Lock", hr);
            try
            {
                if (cur == 0) return false;
                int count = OutputIsFloat ? (int)(cur / sizeof(float)) : (int)(cur / sizeof(short));
                EnsurePending(count);
                var dst = _pending.AsSpan(0, count);
                if (OutputIsFloat) new ReadOnlySpan<float>((float*)src, count).CopyTo(dst);
                else
                {
                    var pcm = new ReadOnlySpan<short>((short*)src, count);
                    for (int i = 0; i < count; i++) dst[i] = pcm[i] * (1f / 32768f);
                }
                _pendingStart = 0;
                _pendingCount = count;
                return true;
            }
            finally { buffer->Unlock(); }
        }
        finally
        {
            if (buffer != null) buffer->Release();
        }
    }

    void EnsurePending(int count)
    {
        if (_pending.Length < count) _pending = new float[Math.Max(count, 8192)];
    }

    // ── plumbing ─────────────────────────────────────────────────────────────────────────────────────────────────────

    static void SetU32(IMFMediaType* type, Guid key, uint value)
    {
        int hr = type->SetUINT32(&key, value);
        if (hr < 0) throw Fail("IMFMediaType::SetUINT32", hr);
    }

    static uint GetU32(IMFMediaType* type, Guid key, uint fallback)
    {
        uint value;
        return type->GetUINT32(&key, &value) >= 0 ? value : fallback;
    }

    static InvalidOperationException Fail(string what, int hr)
        => new($"AAC decoder: {what} failed (0x{hr:X8})", Marshal.GetExceptionForHR(hr));

    void ReleaseOutputSample()
    {
        if (_outSample != null) { _outSample->Release(); _outSample = null; }
        if (_outBuffer != null) { _outBuffer->Release(); _outBuffer = null; }
        _outBufferBytes = 0;
    }

    void ReleaseAll()
    {
        ReleaseOutputSample();
        if (_transform != null)
        {
            _transform->ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_OF_STREAM, (nuint)0);
            _transform->ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_STREAMING, (nuint)0);
            _transform->Release();
            _transform = null;
        }
        if (_mfStarted) { MFShutdown(); _mfStarted = false; }
        if (_coInitialized) { CoUninitialize(); _coInitialized = false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseAll();
    }
}
