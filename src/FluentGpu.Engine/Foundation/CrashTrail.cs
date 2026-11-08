using System.Runtime.CompilerServices;
using System.Text;

namespace FluentGpu.Foundation;

/// <summary>Engine events a crash report's trail names. Values are the wire (a trail block outlives the process that wrote it
/// and is decoded by another one), so a member is never renumbered; codes from <see cref="CrashTrail.AppBase"/> up belong to
/// the app.</summary>
public enum TrailEvent : ushort
{
    None = 0,
    /// <summary>The window stopped presenting: A = <c>HiddenPark</c> (1 cover/cloak, 2 minimized or hidden), B = 1 for a
    /// detached pop-out.</summary>
    WindowParked = 1,
    /// <summary>The window presents again (A, B as <see cref="WindowParked"/>).</summary>
    WindowRestored = 2,
    /// <summary>A parked window released its GPU caches: A = park, B = unpinned image textures released.</summary>
    HiddenShallow = 3,
    /// <summary>A long-parked window also parked its own images: A = park, B = textures parked.</summary>
    HiddenDeep = 4,
    /// <summary>The window came back from a hidden stage: A = the stage it left (1 shallow, 2 deep).</summary>
    HiddenRestore = 5,
    /// <summary>The D3D12 device was lost: A = recorded HRESULT, B = the device's current removed reason; text = path.</summary>
    DeviceLost = 10,
    /// <summary>A device recovery finished: A = recovery count.</summary>
    DeviceRecovered = 11,
    /// <summary>A device recovery threw: A = recovery count.</summary>
    DeviceRecoverFailed = 12,
    /// <summary>A live GPU switch began: A = the adapter it leaves, B = the adapter asked for (vendor &lt;&lt; 32 | device;
    /// B = 0 is "automatic").</summary>
    AdapterSwitch = 13,
    /// <summary>A device was created on an adapter: A = vendor &lt;&lt; 32 | device, B = 1 software adapter; text = mode.</summary>
    AdapterSelected = 14,
    /// <summary>Cold maintenance released memory: A = bit 0 pixel pool, bit 1 scene slab, bit 2 render snapshots; B = scene
    /// slots before the trim.</summary>
    ColdTrim = 20,
}

/// <summary>What a thread is doing inside native code that might fault. Values are the wire, like <see cref="TrailEvent"/>;
/// app regions start at <see cref="CrashTrail.AppBase"/>.</summary>
public enum NativeRegion : ushort
{
    None = 0,
    /// <summary><c>ID3D12GraphicsCommandList::Close</c> (third-party hooks such as overlays sit in this call).</summary>
    D3D12Close = 1,
    /// <summary>Tearing a lost device down and creating the next one.</summary>
    DeviceRecovery = 2,
    /// <summary>A pipeline state compile in the driver; text = the pipeline's name.</summary>
    PipelineCompile = 3,
    /// <summary>A WIC decode (decoder, frame, scaler, pixel copy).</summary>
    WicDecode = 4,
}

/// <summary>The crash report's memory of the last few hundred things that happened, and of what every thread was doing in
/// native code. ALWAYS ON and allocation-free on the record path: one interlocked increment and a few stores into a fixed
/// block of plain memory. The block is plain data on purpose: an app can move it onto a file mapping
/// (<see cref="Attach"/>) so a separate crash-handler process can still read it after this process died without running a
/// single hook (a fail-fast). Nothing in it is personal by construction: codes, numbers and short code-owned ASCII.
/// <para>Layout (little-endian, version 1): a 64-byte header, <see cref="RegionSlots"/> 64-byte region slots, then
/// <see cref="Capacity"/> 48-byte entries. See <see cref="Decode"/>.</para></summary>
public static unsafe class CrashTrail
{
    /// <summary>The first app-owned event / region code.</summary>
    public const ushort AppBase = 1000;

    public const int Capacity = 256, RegionSlots = 32;
    public const int HeaderBytes = 64, RegionBytes = 64, EntryBytes = 48, EntryTextBytes = 16, RegionTextBytes = 32;
    public const int TotalBytes = HeaderBytes + RegionSlots * RegionBytes + Capacity * EntryBytes;
    const uint Magic = 0x4C525457;   // "WTRL"
    const int Version = 1;

    // header: 0 magic, 4 version, 8 capacity, 12 region slots, 16 next (interlocked), 20 pid, 24 start tick ms
    // region: 0 tid (0 = free), 4 kind, 6 text length, 7 depth, 8 since tick ms, 16 arg, 24 text[32]
    // entry:  0 seq (0 = empty / being written), 4 code, 6 text length, 8 tick ms, 16 a, 24 b, 32 text[16]
    static byte* s_block = Allocate();
    static readonly object s_attachGate = new();

    [ThreadStatic] static uint t_tid;
    [ThreadStatic] static int t_slot;    // 1-based while this thread holds a region slot

    static byte* Allocate()
    {
        byte* b = (byte*)System.Runtime.InteropServices.NativeMemory.AllocZeroed((nuint)TotalBytes);
        Init(b);
        return b;
    }

    static void Init(byte* b)
    {
        *(uint*)b = Magic;
        *(int*)(b + 4) = Version;
        *(int*)(b + 8) = Capacity;
        *(int*)(b + 12) = RegionSlots;
        *(int*)(b + 20) = Environment.ProcessId;
        *(long*)(b + 24) = Environment.TickCount64;
    }

    /// <summary>The OS thread id of the calling thread (the platform installs it; Windows: <c>GetCurrentThreadId</c>). Region
    /// labels are keyed by it, because a crash hook names the faulting thread by its OS id. Unset, regions record nothing.</summary>
    public static Func<uint>? ThreadIdSource { get; set; }

    /// <summary>The live block (<see cref="TotalBytes"/> bytes). Read it with <see cref="Decode"/>.</summary>
    public static ReadOnlySpan<byte> Block => new(s_block, TotalBytes);

    /// <summary>Moves the trail onto <paramref name="block"/> (at least <see cref="TotalBytes"/> bytes, zeroed or not), copying
    /// what was recorded so far. The old block is never freed (a racing writer may still hold it), and a thread inside a region
    /// at this moment keeps its slot in the old block until it leaves. Call once, early.</summary>
    public static bool Attach(byte* block, int bytes)
    {
        if (block == null || bytes < TotalBytes) return false;
        lock (s_attachGate)
        {
            byte* old = s_block;
            Buffer.MemoryCopy(old, block, TotalBytes, TotalBytes);
            Init(block);
            Volatile.Write(ref *(int*)(block + 16), Volatile.Read(ref *(int*)(old + 16)));
            s_block = block;
        }
        return true;
    }

    // ── recording ──────────────────────────────────────────────────────────────────────────────────────────────────────

    public static void Record(TrailEvent e, long a = 0, long b = 0) => Record((ushort)e, a, b, default);

    public static void Record(TrailEvent e, long a, long b, ReadOnlySpan<char> text) => Record((ushort)e, a, b, text);

    /// <summary>Appends one entry. Allocation-free; safe from any thread. <paramref name="text"/> keeps its first
    /// <see cref="EntryTextBytes"/> printable ASCII characters (anything else becomes '?'): pass code-owned names only.</summary>
    public static void Record(ushort code, long a, long b, ReadOnlySpan<char> text)
    {
        byte* block = s_block;
        int seq = Interlocked.Increment(ref *(int*)(block + 16));
        if (seq <= 0) return;   // wrapped after 2^31 events: stop rather than write sequence numbers a reader misorders
        byte* e = block + HeaderBytes + RegionSlots * RegionBytes + ((seq - 1) & (Capacity - 1)) * EntryBytes;
        Volatile.Write(ref *(int*)e, 0);
        *(ushort*)(e + 4) = code;
        *(e + 6) = (byte)CopyAscii(text, e + 32, EntryTextBytes);
        *(long*)(e + 8) = Environment.TickCount64;
        *(long*)(e + 16) = a;
        *(long*)(e + 24) = b;
        Volatile.Write(ref *(int*)e, seq);
    }

    static int CopyAscii(ReadOnlySpan<char> text, byte* dst, int max)
    {
        int n = Math.Min(text.Length, max);
        for (int i = 0; i < n; i++)
        {
            char c = text[i];
            dst[i] = c is >= ' ' and <= '~' ? (byte)c : (byte)'?';
        }
        return n;
    }

    // ── native regions ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Labels the calling thread as inside <paramref name="kind"/> until the returned scope is disposed:
    /// <c>using (CrashTrail.Enter(NativeRegion.D3D12Close)) hr = list-&gt;Close();</c>. A crash hook that sees a fault on this
    /// thread names the region (<see cref="TryRegionFor"/>). Allocation-free; a nested region replaces the outer label until it
    /// leaves (the outer kind and arg come back, its text does not).</summary>
    public static RegionScope Enter(NativeRegion kind, ReadOnlySpan<char> text = default, long arg = 0) => Enter((ushort)kind, text, arg);

    public static RegionScope Enter(ushort kind, ReadOnlySpan<char> text = default, long arg = 0)
    {
        byte* slot = SlotForThisThread();
        if (slot == null) return default;
        var scope = new RegionScope(t_slot, *(ushort*)(slot + 4), *(long*)(slot + 16), *(long*)(slot + 8));
        *(ushort*)(slot + 4) = kind;
        *(slot + 6) = (byte)CopyAscii(text, slot + 24, RegionTextBytes);
        *(long*)(slot + 8) = Environment.TickCount64;
        *(long*)(slot + 16) = arg;
        (*(slot + 7))++;
        return scope;
    }

    static byte* SlotForThisThread()
    {
        byte* block = s_block;
        int held = t_slot;
        if (held > 0) return block + HeaderBytes + (held - 1) * RegionBytes;
        uint tid = t_tid;
        if (tid == 0)
        {
            if (ThreadIdSource is not { } source) return null;
            t_tid = tid = source();
            if (tid == 0) return null;
        }
        for (int i = 0; i < RegionSlots; i++)
        {
            byte* s = block + HeaderBytes + i * RegionBytes;
            if (Interlocked.CompareExchange(ref *(uint*)s, tid, 0) != 0) continue;
            *(s + 7) = 0;
            t_slot = i + 1;
            return s;
        }
        return null;   // 32 threads inside native regions at once: the 33rd goes unlabeled
    }

    /// <summary>The scope <see cref="Enter(NativeRegion, ReadOnlySpan{char}, long)"/> returns.</summary>
    public readonly ref struct RegionScope
    {
        readonly int _slot;   // 1-based; 0 = the scope recorded nothing
        readonly ushort _prevKind;
        readonly long _prevArg, _prevSince;

        internal RegionScope(int slot, ushort prevKind, long prevArg, long prevSince)
        {
            _slot = slot; _prevKind = prevKind; _prevArg = prevArg; _prevSince = prevSince;
        }

        public void Dispose()
        {
            if (_slot <= 0) return;
            // By index into the CURRENT block: an Attach between Enter and here copied the slot along with its owner.
            byte* s = s_block + HeaderBytes + (_slot - 1) * RegionBytes;
            byte depth = *(s + 7);
            if (depth > 1)
            {
                *(s + 7) = (byte)(depth - 1);
                *(ushort*)(s + 4) = _prevKind;
                *(long*)(s + 16) = _prevArg;
                *(long*)(s + 8) = _prevSince;
                *(s + 6) = 0;
                return;
            }
            *(s + 7) = 0;
            *(ushort*)(s + 4) = 0;
            *(s + 6) = 0;
            t_slot = 0;
            Volatile.Write(ref *(uint*)s, 0u);   // release last: the slot is free for another thread
        }
    }

    // ── reading ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One decoded entry.</summary>
    public readonly record struct Entry(int Seq, ushort Code, long TickMs, long A, long B, string Text);

    /// <summary>One thread inside a native region.</summary>
    public readonly record struct Region(uint ThreadId, ushort Kind, long SinceTickMs, long Arg, string Text);

    /// <summary>A decoded block: the entries oldest first, the regions in use, and the writer's pid and start tick.</summary>
    public sealed record Snapshot(int Pid, long StartTickMs, Entry[] Entries, Region[] Regions);

    /// <summary>Decodes a block (this process's <see cref="Block"/>, or bytes another process read from a mapping). Null when
    /// it is not a version-1 trail block. Allocates; never call it on a hot path. A torn entry (rewritten while it was read)
    /// is skipped.</summary>
    public static Snapshot? Decode(ReadOnlySpan<byte> block)
    {
        if (block.Length < TotalBytes) return null;
        fixed (byte* b = block)
        {
            if (*(uint*)b != Magic || *(int*)(b + 4) != Version || *(int*)(b + 8) != Capacity || *(int*)(b + 12) != RegionSlots) return null;
            int next = Volatile.Read(ref *(int*)(b + 16));
            var entries = new List<Entry>(Capacity);
            for (int i = 0; i < Capacity; i++)
            {
                byte* e = b + HeaderBytes + RegionSlots * RegionBytes + i * EntryBytes;
                int seq = Volatile.Read(ref *(int*)e);
                if (seq <= 0 || seq > next || seq <= next - Capacity) continue;
                var entry = new Entry(seq, *(ushort*)(e + 4), *(long*)(e + 8), *(long*)(e + 16), *(long*)(e + 24),
                    Ascii(e + 32, Math.Min((int)*(e + 6), EntryTextBytes)));
                if (Volatile.Read(ref *(int*)e) != seq) continue;
                entries.Add(entry);
            }
            entries.Sort(static (x, y) => x.Seq.CompareTo(y.Seq));
            var regions = new List<Region>();
            for (int i = 0; i < RegionSlots; i++)
            {
                byte* s = b + HeaderBytes + i * RegionBytes;
                uint tid = Volatile.Read(ref *(uint*)s);
                ushort kind = *(ushort*)(s + 4);
                if (tid == 0 || kind == 0) continue;
                regions.Add(new Region(tid, kind, *(long*)(s + 8), *(long*)(s + 16), Ascii(s + 24, Math.Min((int)*(s + 6), RegionTextBytes))));
            }
            return new Snapshot(*(int*)(b + 20), *(long*)(b + 24), entries.ToArray(), regions.ToArray());
        }
    }

    /// <summary>The region <paramref name="threadId"/> is inside in this process right now, if any (a crash hook's question
    /// about the faulting thread, asked from another thread while the faulting one is parked).</summary>
    public static bool TryRegionFor(uint threadId, out Region region)
    {
        region = default;
        if (threadId == 0) return false;
        byte* b = s_block;
        for (int i = 0; i < RegionSlots; i++)
        {
            byte* s = b + HeaderBytes + i * RegionBytes;
            if (Volatile.Read(ref *(uint*)s) != threadId) continue;
            ushort kind = *(ushort*)(s + 4);
            if (kind == 0) return false;
            region = new Region(threadId, kind, *(long*)(s + 8), *(long*)(s + 16), Ascii(s + 24, Math.Min((int)*(s + 6), RegionTextBytes)));
            return true;
        }
        return false;
    }

    static string Ascii(byte* p, int n) => n <= 0 ? "" : Encoding.ASCII.GetString(p, n);

    /// <summary>A packed adapter id for <see cref="TrailEvent.AdapterSwitch"/> / <see cref="TrailEvent.AdapterSelected"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long AdapterId(uint vendorId, uint deviceId) => ((long)vendorId << 32) | deviceId;
}
