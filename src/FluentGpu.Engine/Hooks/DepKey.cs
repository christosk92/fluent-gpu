using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FluentGpu.Foundation;

namespace FluentGpu.Hooks;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
//  DepKey — the canonical pure-scalar, blittable 16-byte hook-dependency key (SPEC-INDEX §2; owner reconciler-hooks).
//
//  The single dep shape for the whole hook surface: UseEffect/UseLayoutEffect/UseMemo + the retained-anim hooks
//  (UseSpring/UseTransition/UseKeyframes/UseDrivenAnimation) + UseResource compare a stored DepKey by value —
//  no per-render `object[]` box, no value-type boxing. It packs up to four 4-byte scalars (or two longs) by value.
//  Scalars and short tuples are EXACT; string / >4-scalar / reference keys hash (documented per-member tradeoff).
//  `default`/`Empty` = a mount-once key (never changes across renders). GC-REF deps do NOT live inline (a
//  [FieldOffset] GC-ref/scalar union is illegal CLR layout) — they go through <see cref="FromRef(object?)"/>
//  (identity-hash + tag; GcDepTable is the exact upgrade if a collision ever bites, GEN-02).
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

[StructLayout(LayoutKind.Sequential)]
public readonly struct DepKey : IEquatable<DepKey>
{
    private readonly long _a;
    private readonly long _b;
    private DepKey(long a, long b) { _a = a; _b = b; }

    public static DepKey Empty => default;

    private static long Pack(float lo, float hi)
        => ((long)(uint)BitConverter.SingleToInt32Bits(lo)) | ((long)BitConverter.SingleToInt32Bits(hi) << 32);
    private static long Pack(int lo, int hi)
        => ((long)(uint)lo) | ((long)hi << 32);

    public static DepKey From(int a) => new(a, 0);
    public static DepKey From(int a, int b) => new(Pack(a, b), 0);
    public static DepKey From(int a, int b, int c, int d) => new(Pack(a, b), Pack(c, d));
    public static DepKey From(long a) => new(a, 0);
    public static DepKey From(long a, long b) => new(a, b);
    public static DepKey From(float a) => new(BitConverter.SingleToInt32Bits(a) & 0xffffffffL, 0);
    public static DepKey From(float a, float b) => new(Pack(a, b), 0);
    public static DepKey From(float a, float b, float c, float d) => new(Pack(a, b), Pack(c, d));
    public static DepKey From(bool a) => new(a ? 1 : 0, 0);
    public static DepKey From(NodeHandle h) => new((long)(uint)h.Raw.Index, 0);
    public static DepKey From(float a, int b) => new(Pack(BitConverter.SingleToInt32Bits(a), b), 0);

    // ── Ergonomic implicit conversions ───────────────────────────────────────────────────────────────────────────
    // A single scalar dep needs no ceremony: `UseEffect(fn, count)` / `UseEffect(fn, open)` bind straight through.
    public static implicit operator DepKey(int v) => new(v, 0);
    public static implicit operator DepKey(long v) => new(v, 0);
    public static implicit operator DepKey(float v) => new(BitConverter.SingleToInt32Bits(v) & 0xffffffffL, 0);
    public static implicit operator DepKey(double v) => new(BitConverter.DoubleToInt64Bits(v), 0);
    public static implicit operator DepKey(bool v) => new(v ? 1 : 0, 0);
    public static implicit operator DepKey(string? v) => new(HashString(v), 0);
    public static implicit operator DepKey(NodeHandle h) => new((long)(uint)h.Raw.Index, 0);
    // Finite tuple set (exact for the scalar lanes; strings hash their lane).
    public static implicit operator DepKey((int, int) t) => new(Pack(t.Item1, t.Item2), 0);
    public static implicit operator DepKey((int, int, int, int) t) => new(Pack(t.Item1, t.Item2), Pack(t.Item3, t.Item4));
    public static implicit operator DepKey((float, float) t) => new(Pack(t.Item1, t.Item2), 0);
    public static implicit operator DepKey((float, float, float, float) t) => new(Pack(t.Item1, t.Item2), Pack(t.Item3, t.Item4));
    public static implicit operator DepKey((long, long) t) => new(t.Item1, t.Item2);
    public static implicit operator DepKey((float, int) t) => new(Pack(BitConverter.SingleToInt32Bits(t.Item1), t.Item2), 0);
    public static implicit operator DepKey((int, bool) t) => new(Pack(t.Item1, t.Item2 ? 1 : 0), 0);
    public static implicit operator DepKey((bool, bool) t) => new(Pack(t.Item1 ? 1 : 0, t.Item2 ? 1 : 0), 0);
    public static implicit operator DepKey((string?, int) t) => new(HashString(t.Item1), t.Item2);
    public static implicit operator DepKey((string?, string?) t) => new(HashString(t.Item1), HashString(t.Item2));
    public static implicit operator DepKey((string?, bool) t) => new(HashString(t.Item1), t.Item2 ? 1 : 0);

    /// <summary>Fold two keys into one (a 128-bit avalanche mix) — the escape hatch for &gt;4 scalar deps: build sub-keys
    /// and combine them. Probabilistic at ~2^-64 collision (like any hash), acceptable for hook re-run gating.</summary>
    public static DepKey Combine(DepKey a, DepKey b)
    {
        unchecked
        {
            ulong h1 = Mix((ulong)a._a, (ulong)a._b);
            ulong h2 = Mix((ulong)b._a, (ulong)b._b);
            return new((long)Mix(h1, h2 ^ 0x9E3779B97F4A7C15UL), (long)Mix(h2, h1 ^ 0xC2B2AE3D27D4EB4FUL));
        }
    }

    // Reference/identity keys occupy a distinct _b band (RefTag) so an identity key never collides with a scalar key.
    // The payload is RuntimeHelpers.GetHashCode (object identity, NOT Equals) — so an in-place mutation of the SAME
    // instance does not change the key (no re-run), while swapping to a different instance does. NOTE: a FRESH lambda
    // each render is a new identity, so FromRef over one re-runs the effect every render (an analyzer flags this later).
    private const long RefTag = unchecked((long)0xF00DFACE00000000UL);
    public static DepKey FromRef(object? o) => new(o is null ? 0 : (uint)RuntimeHelpers.GetHashCode(o), RefTag);
    public static DepKey FromRef(object? a, object? b)
        => new(((long)(uint)(a is null ? 0 : RuntimeHelpers.GetHashCode(a))) | ((long)(uint)(b is null ? 0 : RuntimeHelpers.GetHashCode(b)) << 32), RefTag);

    public bool Equals(DepKey o) => _a == o._a && _b == o._b;
    public override bool Equals(object? o) => o is DepKey k && Equals(k);
    public override int GetHashCode() => HashCode.Combine(_a, _b);
    public static bool operator ==(DepKey x, DepKey y) => x.Equals(y);
    public static bool operator !=(DepKey x, DepKey y) => !x.Equals(y);

    // ── XxHash64 over the string's UTF-16 code units (zero-alloc, no package dependency) ─────────────────────────────
    // Result XORed with (length<<56). PROBABILISTIC: two distinct strings collide (and a keyed effect misses a re-run)
    // at ~2^-64 — acceptable for dep keys; the GcDepTable exact-compare (GEN-02) is the upgrade path if it ever bites.
    // null → 0. The hash need only be STABLE within a process (dep comparison), so LE reads are fine on any arch.
    internal static long HashString(string? s)
    {
        if (s is null) return 0;
        unchecked
        {
            ReadOnlySpan<byte> data = MemoryMarshal.AsBytes(s.AsSpan());
            const ulong P1 = 0x9E3779B185EBCA87UL, P2 = 0xC2B2AE3D27D4EB4FUL, P3 = 0x165667B19E3779F9UL,
                        P4 = 0x85EBCA77C2B2AE63UL, P5 = 0x27D4EB2F165667C5UL;
            int len = data.Length, i = 0;
            ulong h;
            if (len >= 32)
            {
                ulong v1 = P1 + P2, v2 = P2, v3 = 0, v4 = 0UL - P1;
                int limit = len - 32;
                while (i <= limit)
                {
                    v1 = Round(v1, R64(data, i)); i += 8;
                    v2 = Round(v2, R64(data, i)); i += 8;
                    v3 = Round(v3, R64(data, i)); i += 8;
                    v4 = Round(v4, R64(data, i)); i += 8;
                }
                h = Rol(v1, 1) + Rol(v2, 7) + Rol(v3, 12) + Rol(v4, 18);
                h = Merge(h, v1); h = Merge(h, v2); h = Merge(h, v3); h = Merge(h, v4);
            }
            else h = P5;
            h += (ulong)len;
            while (i + 8 <= len) { h ^= Round(0, R64(data, i)); h = Rol(h, 27) * P1 + P4; i += 8; }
            if (i + 4 <= len) { h ^= R32(data, i) * P1; h = Rol(h, 23) * P2 + P3; i += 4; }
            while (i < len) { h ^= data[i] * P5; h = Rol(h, 11) * P1; i++; }
            h ^= h >> 33; h *= P2; h ^= h >> 29; h *= P3; h ^= h >> 32;
            return (long)h ^ ((long)s.Length << 56);

            static ulong Round(ulong acc, ulong input) { acc += input * P2; acc = Rol(acc, 31); return acc * P1; }
            static ulong Merge(ulong acc, ulong val) { acc ^= Round(0, val); return acc * P1 + P4; }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Rol(ulong x, int r) => (x << r) | (x >> (64 - r));
    private static ulong Mix(ulong a, ulong b)
    {
        unchecked
        {
            ulong h = a * 0x9E3779B97F4A7C15UL;
            h ^= Rol(b + 0x165667B19E3779F9UL, 31);
            h *= 0xC2B2AE3D27D4EB4FUL;
            h ^= h >> 29;
            return h;
        }
    }
    private static ulong R64(ReadOnlySpan<byte> d, int i) => BinaryPrimitives.ReadUInt64LittleEndian(d.Slice(i, 8));
    private static ulong R32(ReadOnlySpan<byte> d, int i) => BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(i, 4));
}
