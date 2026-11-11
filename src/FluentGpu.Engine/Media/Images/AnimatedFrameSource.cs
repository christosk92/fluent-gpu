using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using FluentGpu.Foundation;

namespace FluentGpu.Media;

/// <summary>
/// The decode side of ONE playing animated image (<c>ImageEl.Animate</c>): a dedicated worker thread that fetches the file
/// once, opens it through <see cref="IAnimatedImageCodec"/> and decodes its frames IN ORDER into a small ring of pixel blocks,
/// <see cref="Depth"/> frames ahead of what the UI has taken. Never all frames: a 1440x810 frame is 4.7 MB, so the ring is the
/// whole CPU cost (depth x frame) however long the file is. The blocks are rentals from the host's shared
/// <see cref="PixelBufferPool"/>; the UI hands each one to the image upload path as a loan
/// (<see cref="DecodeScheduler.TryTakeDecodeBuffer"/>), the render thread gives it back to the pool after staging, and the
/// worker rents it again for a later frame: steady playback allocates nothing anywhere.
/// <para><b>Lifetime.</b> The UI <see cref="Touch"/>es the source every frame it is shown. A source not touched for
/// <see cref="ReleaseAfterMs"/> (the element scrolled away, the window minimised or covered and no longer painting) releases
/// itself on its own thread: ring blocks back to the pool, the decoder and the file's bytes freed, the thread ended
/// (<see cref="SourceState.Released"/>), so an idle animation costs nothing. <see cref="Dispose"/> does the same at once.
/// Nothing blocks the UI: Dispose only flags the worker, which finishes its current frame (one decode) and cleans up.</para>
/// <para><b>Threads.</b> The ring is guarded by one lock (the UI takes one frame per shown frame; the worker pushes one per
/// decode): uncontended, allocation-free. Everything native is created, used and released on the worker.</para>
/// </summary>
public sealed unsafe class AnimatedFrameSource : IDisposable
{
    public enum SourceState { Opening = 0, Playing = 1, NotAnimated = 2, Failed = 3, Ended = 4, Released = 5 }

    /// <summary>One decoded frame on loan from the ring. The taker owns <see cref="Block"/> (returns it to the pool, or hands it
    /// to the upload path that does).</summary>
    public struct Frame
    {
        public PixelBlock Block;
        public int ByteLen, W, H, Index, DurationMs;
    }

    /// <summary>Frames decoded ahead of the UI. 60 fps leaves 16.7 ms a frame. Measured on a Snapdragon X Elite through WIC, one
    /// decoder in order: real 1440x810 app footage (Pillow/libwebp lossy q75, no alpha) p50 2.9 ms, p90 7.2 ms, max 13.9 ms a
    /// frame (255 fps); a synthetic every-pixel-moves 1440x810 test p50 10 ms, max 23 ms. Three frames (50 ms) of lead absorb a
    /// run of the worst of either without a frame shown late. Memory: Depth x W x H x 4 (14 MB at 1440x810), plus the one or two
    /// frames in the upload path.</summary>
    public const int Depth = 3;

    /// <summary>Untouched this long, the source frees itself (see the class remarks). Long enough that a quick scroll away
    /// and back does not re-open the file (opening a 700-frame WebP costs 100-200 ms) and restart the animation.</summary>
    public const int ReleaseAfterMs = 5000;

    private readonly string _source;
    private readonly IImageFetcher _fetcher;
    private readonly IAnimatedImageCodec _codec;
    private readonly PixelBufferPool _pool;
    private readonly Action? _wake;
    private readonly object _gate = new();
    private readonly Frame[] _ring = new Frame[Depth];
    private int _head, _count;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _stop;
    private int _uiWaiting;
    private long _touchedTicks = Stopwatch.GetTimestamp();
    private volatile SourceState _state;

    public AnimatedFrameSource(string source, IImageFetcher fetcher, IAnimatedImageCodec codec, PixelBufferPool pool, Action? wake)
    {
        _source = source;
        _fetcher = fetcher;
        _codec = codec;
        _pool = pool;
        _wake = wake;
        var t = new Thread(Run) { IsBackground = true, Name = "fgpu-anim-image", Priority = ThreadPriority.AboveNormal };
        t.Start();
    }

    public SourceState State => _state;
    public string Source => _source;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int FrameCount { get; private set; }
    public int LoopCount { get; private set; }

    // Worker-written decode statistics (read by the UI for its summary lines; plain fields, a torn read only misreports a log).
    /// <summary>Frames decoded since open.</summary>
    public int Decoded;
    /// <summary>Sum and maximum of the decode times (ms x 100).</summary>
    public long DecodeSumCs, DecodeMaxCs;
    /// <summary>Decodes slower than one 60 fps frame (16.7 ms): the ring's lead is what keeps them from showing late.</summary>
    public int DecodeOverBudget;

    // UI-written playback statistics (ImageCache.TickAnimations), read by the worker's periodic summary line, so the UI
    // thread formats nothing while it plays.
    public int UiShown, UiOnTime, UiLate, UiLateDecode, UiLateTick, UiMaxLateCs;

    /// <summary>UI: this source is still shown; keeps it from releasing itself.</summary>
    public void Touch() => Volatile.Write(ref _touchedTicks, Stopwatch.GetTimestamp());

    /// <summary>UI: frames decoded and waiting.</summary>
    public int Buffered { get { lock (_gate) return _count; } }

    /// <summary>UI: take the next frame in order. False when the worker has not decoded it yet: the source then wakes the
    /// host (the completion wake) the moment it has, so a frame waiting on its decode costs no polling.</summary>
    public bool TryTake(out Frame frame)
    {
        lock (_gate)
        {
            if (_count > 0)
            {
                frame = _ring[_head];
                _ring[_head] = default;
                _head = (_head + 1) % Depth;
                _count--;
                _signal.Release();   // a ring slot is free: the worker decodes the next frame
                return true;
            }
        }
        Volatile.Write(ref _uiWaiting, 1);
        frame = default;
        return false;
    }

    public void Dispose()
    {
        if (_stop) return;
        _stop = true;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        _signal.Release();
        // The frames already decoded go back now (a worker mid-decode sees _stop under the lock and returns its own block).
        DrainRing();
    }

    private void DrainRing()
    {
        lock (_gate)
        {
            while (_count > 0)
            {
                ref Frame f = ref _ring[_head];
                if (f.Block is not null) _pool.Return(f.Block);
                f = default;
                _head = (_head + 1) % Depth;
                _count--;
            }
        }
    }

    private bool Untouched()
        => (Stopwatch.GetTimestamp() - Volatile.Read(ref _touchedTicks)) * 1000 / Stopwatch.Frequency > ReleaseAfterMs;

    private void Run()
    {
        byte* encoded = null;
        IAnimatedImageReader? reader = null;
        bool keepRing = false;   // Ended: the frames already decoded stay for the UI to show
        try
        {
            FetchResult fetched;
            try { fetched = _fetcher.FetchAsync(_source, _cts.Token).GetAwaiter().GetResult(); }
            catch (Exception) { fetched = FetchResult.Fail(FluentGpu.Scene.ImageFailureKind.Network); }
            if (!fetched.Ok || fetched.Length <= 0)
            {
                if (fetched.Buffer is { } b) _fetcher.ReturnBuffer(b);
                _state = SourceState.Failed;
                return;
            }
            int length = fetched.Length;
            // The decoder reads the bytes for as long as it lives: copy them into memory the GC never moves.
            encoded = (byte*)NativeMemory.Alloc((nuint)length);
            fetched.Span.CopyTo(new Span<byte>(encoded, length));
            _fetcher.ReturnBuffer(fetched.Buffer!);

            if (_stop) return;
            reader = _codec.OpenAnimated(encoded, length);
            if (reader is null) { _state = SourceState.NotAnimated; return; }
            Width = reader.Width; Height = reader.Height; FrameCount = reader.FrameCount; LoopCount = reader.LoopCount;
            _state = SourceState.Playing;
            Diag.Line($"[image.anim] open src={TailOf(_source)} {Width}x{Height} frames={FrameCount} loops={LoopCount} depth={Depth} " +
                $"ringBytes={(long)Depth * Width * Height * 4}");

            int byteLen = Width * Height * 4;
            int index = 0, loopsDone = 0;
            long nextLog = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
            while (!_stop)
            {
                long nowTicks = Stopwatch.GetTimestamp();
                if (nowTicks >= nextLog)
                {
                    nextLog = nowTicks + 5 * Stopwatch.Frequency;
                    if ((nowTicks - Volatile.Read(ref _touchedTicks)) < Stopwatch.Frequency)   // shown within the last second
                        Diag.Line($"[image.anim] playing src={TailOf(_source)} shown={UiShown} onTime={UiOnTime} late={UiLate} (decode={UiLateDecode} tick={UiLateTick}) " +
                            $"maxLateMs={UiMaxLateCs / 100f:F1} decoded={Decoded} decodeAvgMs={(Decoded == 0 ? 0 : DecodeSumCs / 100f / Decoded):F2} " +
                            $"decodeMaxMs={DecodeMaxCs / 100f:F2} decodeOver16ms={DecodeOverBudget} buffered={Buffered}/{Depth} " +
                            $"poolLiveBytes={_pool.LiveBytes}");
                }
                bool room;
                lock (_gate) room = _count < Depth;
                if (!room)
                {
                    // Full: wait for the UI to take one. The timeout only exists to notice an untouched source.
                    _signal.Wait(500);
                    if (Untouched()) { _state = SourceState.Released; return; }
                    continue;
                }
                if (Untouched()) { _state = SourceState.Released; return; }

                PixelBlock block = _pool.Rent(byteLen);
                long t0 = Stopwatch.GetTimestamp();
                bool ok;
                int duration;
                try { ok = reader.TryDecode(index, block.AsSpan(0, byteLen), out duration); }
                catch (Exception) { ok = false; duration = 0; }
                long cs = (Stopwatch.GetTimestamp() - t0) * 100_000 / Stopwatch.Frequency;
                if (!ok)
                {
                    _pool.Return(block);
                    _state = SourceState.Failed;
                    return;
                }
                Decoded++;
                DecodeSumCs += cs;
                if (cs > DecodeMaxCs) DecodeMaxCs = cs;
                if (cs > 1667) DecodeOverBudget++;

                lock (_gate)
                {
                    if (_stop) { _pool.Return(block); break; }
                    int tail = (_head + _count) % Depth;
                    _ring[tail] = new Frame { Block = block, ByteLen = byteLen, W = Width, H = Height, Index = index, DurationMs = duration };
                    _count++;
                }
                if (Interlocked.Exchange(ref _uiWaiting, 0) == 1) _wake?.Invoke();

                if (++index >= FrameCount)
                {
                    index = 0;
                    if (LoopCount > 0 && ++loopsDone >= LoopCount) { _state = SourceState.Ended; keepRing = true; return; }
                }
            }
        }
        catch (Exception ex)
        {
            _state = SourceState.Failed;
            Diag.Line($"[image.anim] failed src={TailOf(_source)}: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { reader?.Dispose(); } catch (Exception) { }
            if (encoded != null) NativeMemory.Free(encoded);
            if (!keepRing || _stop) DrainRing();
            if (_wake is not null && _state != SourceState.Playing) _wake();   // the UI learns the terminal state on its next frame
            _cts.Dispose();
        }
    }

    /// <summary>The file name of a source, for log lines.</summary>
    public static string TailOf(string source)
    {
        int slash = Math.Max(source.LastIndexOf('/'), source.LastIndexOf('\\'));
        return slash >= 0 && slash + 1 < source.Length ? source[(slash + 1)..] : source;
    }
}
