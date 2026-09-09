using System;

namespace FluentGpu.Rhi;

/// <summary>
/// The SIZING + GROWTH decision for a per-frame-in-flight CPU-write upload arena, with no COM and no D3D12 in sight —
/// so the rule that governs real GPU memory is exercisable in the headless harness (the backend's
/// <c>FluentGpu.Rhi.D3D12.UploadArena</c> owns the <c>ID3D12Resource</c>s and asks this class WHAT to do).
///
/// <para><b>Why an arena at all.</b> Every draw pipeline used to own a private, fixed, worst-case instance ring, three
/// deep (one bank per frame-in-flight). Nine such rings sized for "the biggest frame this pipeline could ever record"
/// cost the SUM of nine independent worst cases even though a real frame never hits more than one or two of them — and
/// each committed UPLOAD buffer is rounded up to the 64 KiB resource-placement granularity, so the small rings paid
/// several times their own size. One arena per bank costs the worst case of the FRAME (the sum of what was actually
/// recorded), in ONE committed resource, and can be right-sized to measured demand instead of to a static guess.</para>
///
/// <para><b>The lifetime rule (do not weaken).</b> A bank is written by the CPU for frame N and read by the GPU until
/// frame N retires. Growth therefore REPLACES a bank's buffer only at <see cref="BeginFrame"/>, which the device calls
/// after the frame fence proved that bank's last submit retired — never mid-frame, because a live GPU virtual address
/// handed out earlier in the same frame must stay valid until that frame's LAST flush executes (the backend flushes
/// several times per submit: one per layer/segment boundary). Consequently a frame whose demand exceeds the bank it
/// landed on cannot be served by growing right there: it refuses the reservation, records the demand
/// (<see cref="WantBytes"/>), and each bank picks the larger size up at ITS next <see cref="BeginFrame"/> — so a
/// growth episode heals within <see cref="Banks"/> frames. The device turns a refusal into one more full, un-skippable
/// repaint (the same contract a glyph-bank overflow uses), so the frame that dropped content is re-recorded once the
/// banks are big enough.</para>
///
/// <para><b>Worst case.</b> <paramref name="maxBytes"/> must be at least the sum of every pipeline's own per-frame cap
/// (their <c>MaxInstances × stride</c>, plus the path lane's vertex+index block), otherwise a frame that legitimately
/// fills every pipeline could never be served no matter how often it grew. A SINGLE reservation larger than
/// <see cref="MaxBytes"/> can never succeed — which is why the cap is derived from those pipeline caps rather than
/// picked freehand.</para>
/// </summary>
public sealed class UploadArenaPolicy
{
    /// <summary>Reservation alignment: <c>D3D12_RAW_UAV_SRV_BYTE_ALIGNMENT</c>, the alignment a root
    /// <c>StructuredBuffer</c> SRV address requires (and 4-byte index / vertex buffer views are covered by it too).
    /// Every instance stride in the backend is itself a multiple of 16, so consecutive same-stride reservations pack
    /// with ZERO padding — exactly as densely as the private per-pipeline rings they replace.</summary>
    public const uint Alignment = 16;

    /// <summary>Per-bank starting size the reference backend creates its banks at, chosen from MEASURED demand rather
    /// than a static worst case: a Wavee shell frame records a few hundred rects plus a few dozen images and a handful
    /// of gradients/shadows (≈130 KiB), and the path lane's vertex+index block is another ≈96 KiB when a frame draws
    /// any path at all — so ≈230 KiB is a realistic BUSY frame and this is ~1.7× that. Anything beyond grows (one
    /// dropped-and-repainted frame), so the cost of being wrong here is a rare hitch, never a wrong pixel. Exactly six
    /// 64 KiB resource-placement granules, so nothing is wasted on rounding.</summary>
    public const uint DefaultInitialBytes = 384 * 1024;

    /// <summary>Per-bank ceiling the reference backend uses. Must stay ≥ <see cref="PipelineWorstCaseBytes"/> or a
    /// legitimately maximal frame could never be served no matter how often the arena grew (the headless
    /// <c>arena.worst-case</c> gate asserts exactly that).</summary>
    public const uint DefaultMaxBytes = 2 * 1024 * 1024;

    /// <summary>The FULL-FRAME worst case of every pipeline that shares the arena, i.e. the sum of their own per-frame
    /// caps: round-rect 4096×144 B + shadow 1024×80 B + arc 1024×80 B + polyline 1024×96 B + gradient 512×192 B +
    /// image 1024×176 B + path (16384×16 B vertices, 32768×4 B indices, 512×64 B instances) = 1,556,480 B ≈ 1.48 MiB.
    /// This is the number "do not shrink below what a full frame can need" is measured against; the arena starts far
    /// below it and grows into it on demand instead of reserving it unconditionally three times over.</summary>
    public const uint PipelineWorstCaseBytes = 4096 * 144 + 1024 * 80 + 1024 * 80 + 1024 * 96 + 512 * 192
                                            + 1024 * 176 + 16384 * 16 + 32768 * 4 + 512 * 64;

    private readonly uint[] _bankBytes;   // bytes actually ALLOCATED per bank (0 = not allocated yet)
    private uint _cursor;                 // bump offset into the active bank, reset per frame
    private uint _want;                   // size every bank grows to at its next BeginFrame
    private uint _peak;                   // largest cursor any frame reached (census: real demand vs capacity)
    private int _bank;
    private long _refusals;
    private bool _refusedThisFrame;

    public UploadArenaPolicy(int banks, uint initialBytes, uint maxBytes)
    {
        if (banks <= 0) throw new ArgumentOutOfRangeException(nameof(banks));
        if (initialBytes == 0) throw new ArgumentOutOfRangeException(nameof(initialBytes));
        if (maxBytes < initialBytes) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        _bankBytes = new uint[banks];
        InitialBytes = AlignUp(initialBytes);
        MaxBytes = AlignUp(maxBytes);
        _want = InitialBytes;
    }

    /// <summary>Banks (frames in flight) the arena is spread over.</summary>
    public int Banks => _bankBytes.Length;

    /// <summary>Size each bank is created at.</summary>
    public uint InitialBytes { get; }

    /// <summary>Hard per-bank ceiling; growth never exceeds it.</summary>
    public uint MaxBytes { get; }

    /// <summary>The bank the current frame is writing.</summary>
    public int ActiveBank => _bank;

    /// <summary>Bytes currently allocated for <paramref name="bank"/> (0 before its first allocation).</summary>
    public uint BytesOf(int bank) => (uint)bank < (uint)_bankBytes.Length ? _bankBytes[bank] : 0u;

    /// <summary>Total bytes the arena holds across every bank — the figure the GPU-memory census reports.</summary>
    public long LiveBytes
    {
        get { long n = 0; for (int i = 0; i < _bankBytes.Length; i++) n += _bankBytes[i]; return n; }
    }

    /// <summary>The size every bank converges to at its next <see cref="BeginFrame"/>.</summary>
    public uint WantBytes => _want;

    /// <summary>Bytes reserved so far in the current frame.</summary>
    public uint Cursor => _cursor;

    /// <summary>Largest single-frame demand seen this session (census/diagnostics — this is what "right-sized" means:
    /// capacity should track this, not a static worst case).</summary>
    public uint PeakBytes => _peak;

    /// <summary>Reservations refused for want of room, this session. Non-zero ⇒ a frame dropped content and the arena
    /// grew; a number that keeps climbing means <see cref="MaxBytes"/> is genuinely too small.</summary>
    public long Refusals => _refusals;

    /// <summary>Did THIS frame refuse a reservation? The device arms one more full, un-skippable repaint when it did.</summary>
    public bool RefusedThisFrame => _refusedThisFrame;

    /// <summary>
    /// Select the frame's bank and reset the bump cursor. Returns true when the caller must (re)allocate
    /// <paramref name="bank"/> to <paramref name="growTo"/> BEFORE any reservation — safe exactly here, because the
    /// device fenced this bank's last submit before calling (see the type doc's lifetime rule).
    /// </summary>
    public bool BeginFrame(int frameIndex, out int bank, out uint growTo)
    {
        int n = _bankBytes.Length;
        _bank = ((frameIndex % n) + n) % n;
        _cursor = 0;
        _refusedThisFrame = false;
        bank = _bank;
        growTo = _want;
        return _bankBytes[_bank] < _want;
    }

    /// <summary>Record that <paramref name="bank"/> now holds <paramref name="bytes"/> (called after the real
    /// allocation succeeded, so the policy never over-reports memory that does not exist).</summary>
    public void NoteGrown(int bank, uint bytes)
    {
        if ((uint)bank >= (uint)_bankBytes.Length) return;
        _bankBytes[bank] = bytes;
        if (bytes > _want) _want = bytes;   // an over-allocating backend is honest, not a bug
    }

    /// <summary>Record that <paramref name="bank"/> lost its buffer (device loss / dispose).</summary>
    public void NoteReleased(int bank)
    {
        if ((uint)bank < (uint)_bankBytes.Length) _bankBytes[bank] = 0;
    }

    /// <summary>
    /// Bump-reserve <paramref name="bytes"/> from the active bank. On success <paramref name="offset"/> is the
    /// 16-byte-aligned byte offset to write at (and to derive the GPU virtual address from). On failure NOTHING is
    /// consumed — the caller drops its run exactly as it dropped an over-cap run before — and the demand is folded into
    /// <see cref="WantBytes"/> for the next <see cref="BeginFrame"/> of each bank.
    /// </summary>
    public bool TryReserve(uint bytes, out uint offset)
    {
        offset = 0;
        if (bytes == 0) return false;
        uint start = AlignUp(_cursor);
        uint end = start + AlignUp(bytes);
        uint cap = _bankBytes[_bank];
        if (end > cap || end < start)   // `end < start` = the (unreachable in practice) 4 GiB wrap
        {
            _refusals++;
            _refusedThisFrame = true;
            Demand(end);
            return false;
        }
        offset = start;
        _cursor = end;
        if (end > _peak) _peak = end;
        return true;
    }

    /// <summary>Fold a (possibly refused) byte demand into the growth target: at least double the current bank so a
    /// pathological page converges in a couple of episodes instead of one refusal per added row, rounded up to a whole
    /// 64 KiB resource-placement granule (the allocator's real unit — asking for less buys nothing), capped at
    /// <see cref="MaxBytes"/>.</summary>
    private void Demand(uint bytes)
    {
        uint cur = _bankBytes[_bank] == 0 ? InitialBytes : _bankBytes[_bank];
        uint next = cur <= MaxBytes / 2 ? cur * 2 : MaxBytes;
        uint target = bytes > next ? bytes : next;
        target = Granule(target);
        if (target > MaxBytes) target = MaxBytes;
        if (target > _want) _want = target;
    }

    /// <summary>Round up to the 64 KiB D3D12 resource-placement granule (saturating at <see cref="MaxBytes"/>).</summary>
    private uint Granule(uint bytes)
    {
        const uint G = 64 * 1024;
        if (bytes >= MaxBytes) return MaxBytes;
        uint r = bytes % G;
        return r == 0 ? bytes : bytes + (G - r);
    }

    private static uint AlignUp(uint bytes)
    {
        uint r = bytes % Alignment;
        return r == 0 ? bytes : bytes + (Alignment - r);
    }
}
