using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FluentGpu.Media;

/// <summary>
/// One decoded-pixel buffer in NATIVE memory (<see cref="NativeMemory"/>), owned by a <see cref="PixelBufferPool"/>.
/// <para>Why not a <c>byte[]</c>: a cover is 0.25-4 MiB, so every decode buffer was a large-object-heap array, and the
/// pool's idle trim handed them to the GC. Wavee keeps the GC out of blocking (compacting) collections while audio plays,
/// so the LOH never closed those holes: a session with music on sat at 65 MB of free space in its managed heap
/// (2026-10-21, real-profile soak), on top of what it actually held. A native block leaves no hole behind: freed, a block
/// of 512 KiB and more goes straight back to the OS (the CRT heap serves it with its own <c>VirtualAlloc</c>), and a
/// smaller one back to a heap segment the next small block reuses.</para>
/// <para>Ownership is the pool contract the byte[] had: one renter at a time, returned exactly once, never touched after
/// <see cref="PixelBufferPool.Return"/>. A block whose owner dropped it without returning it is still freed by its
/// finalizer, so a lost block costs a late free, never a leak.</para>
/// </summary>
public sealed unsafe class PixelBlock
{
    private byte* _ptr;

    internal PixelBlock(int capacity)
    {
        Capacity = capacity;
        _ptr = (byte*)NativeMemory.Alloc((nuint)capacity);
    }

    /// <summary>Usable bytes. A renter asked for at most this many; only the first <c>byteLen</c> it wrote are pixels.</summary>
    public int Capacity { get; }

    /// <summary>The whole block.</summary>
    public Span<byte> Span => new(Ptr, Capacity);

    /// <summary><paramref name="length"/> bytes from <paramref name="start"/> (bounds-checked).</summary>
    public Span<byte> AsSpan(int start, int length)
    {
        if ((uint)start > (uint)Capacity || (uint)length > (uint)(Capacity - start)) throw new ArgumentOutOfRangeException(nameof(length));
        return new Span<byte>(Ptr + start, length);
    }

    /// <summary>Whether <paramref name="span"/> starts at this block's first byte (the decode-buffer loan check).</summary>
    public bool StartsAt(ReadOnlySpan<byte> span)
        => _ptr != null && Unsafe.AsPointer(ref MemoryMarshal.GetReference(span)) == _ptr;

    private byte* Ptr => _ptr != null ? _ptr : throw new ObjectDisposedException(nameof(PixelBlock), "pixel block used after its pool freed it");

    /// <summary>Pool only: give the memory back now.</summary>
    internal void Free()
    {
        byte* p = _ptr;
        if (p == null) return;
        _ptr = null;
        NativeMemory.Free(p);
        GC.SuppressFinalize(this);
    }

    ~PixelBlock()
    {
        if (_ptr != null) NativeMemory.Free(_ptr);
    }
}
