using System;
using System.Runtime.InteropServices;
using FluentGpu.Foundation;

namespace FluentGpu.Hooks;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
//  DepKey — the canonical pure-scalar, blittable 16-byte hook-dependency key (SPEC-INDEX §2; owner reconciler-hooks).
//
//  Net-new: hooks today compare deps via `params object[]` (boxes value-type deps + allocates the array every render —
//  the worst authoring-path GC offender, dossier Rank 2). A `DepKey` packs up to four 4-byte scalars (or two longs) by
//  value and compares by content — zero alloc, no boxing. GC-REF deps do NOT live here; they go through a side
//  GcDepTable (object?[] reset per render, compared by ReferenceEquals) — a [FieldOffset] GC-ref/scalar union is illegal
//  CLR layout. The motion hooks (UseSpringValue/UseAnimatedValue) key on this; the >4-scalar overflow ([InlineArray]) is
//  a follow-up. WIRED: RenderContext + Component expose DepKey overloads of
// UseEffect/UseLayoutEffect/UseMemo + the retained-anim hooks (UseSpring/UseTransition/UseKeyframes/UseDrivenAnimation)
// that compare a stored DepKey by value — no per-render object[] box (finding #9).
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

    public bool Equals(DepKey o) => _a == o._a && _b == o._b;
    public override bool Equals(object? o) => o is DepKey k && Equals(k);
    public override int GetHashCode() => HashCode.Combine(_a, _b);
    public static bool operator ==(DepKey x, DepKey y) => x.Equals(y);
    public static bool operator !=(DepKey x, DepKey y) => !x.Equals(y);
}
