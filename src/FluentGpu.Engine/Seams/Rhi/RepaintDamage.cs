using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FluentGpu.Foundation;

namespace FluentGpu.Rhi;

/// <summary>Why a frame must repaint the WHOLE target instead of its accumulated rects. The first cause a frame hits
/// wins (<see cref="RepaintDamageRegion.ForceFull"/> never overwrites an earlier reason), so a diagnostic token names
/// the source that actually gave up — which is the only way to attribute a "why is this frame full?" regression.</summary>
public enum RepaintFullReason : byte
{
    /// <summary>Not forced — the region's rects describe the repaint set.</summary>
    None = 0,
    /// <summary>A node that HAS presented before moved, but its last-presented extent could not be recovered, so the
    /// band it vacated is unknown.</summary>
    MissingPriorExtent,
    /// <summary>A node was unmounted and its last-presented extent could not be recovered.</summary>
    MissingRemovalExtent,
    /// <summary>A structural event invalidated the accumulator wholesale (removal-ledger overflow, scene rebuild).</summary>
    StructuralInvalidation,
    /// <summary>An image's CONTENT changed under byte-identical draw commands (LQIP→full-res swap, baked-blur replace).</summary>
    ImageContent,
    /// <summary>Clock-driven pixels with no dirty flag are live (image crossfades, detached fly snapshots).</summary>
    DetachedContent,
    /// <summary>The consumer missed one or more published frames and their damage could not be recovered.</summary>
    PublishGap,
    /// <summary>The render target itself is not trustworthy: first frame, swapchain resize, DPI change, clear-color
    /// change, device recovery.</summary>
    TargetInvalidated,
    /// <summary>The backend cannot honour a partial repaint for this frame's stream (blur/acrylic/unknown ops).</summary>
    BackendUnsupported,
    /// <summary>The frame claimed "nothing changed" (no rects, no forced full) but its command stream does NOT match the
    /// one the retained canvas was painted from — so a damage source is missing. The backend repaints in full and
    /// invalidates the canvas, converting what would be a PERMANENT stale-pixel ghost into one named full frame. Seeing
    /// this token in <c>dmgFullReason</c> means "find the patch that changes bytes without dirtying a node".</summary>
    EmptyDamageStreamMismatch,
}

/// <summary>Backing storage for <see cref="RepaintDamageRegion"/>'s rects — one [InlineArray] so the whole region is a
/// POD value with no heap reference and no per-frame allocation.</summary>
[InlineArray(RepaintDamageRegion.MaxRects)]
internal struct RepaintRectBuffer
{
    private RectF _e0;
}

/// <summary>
/// The <b>REPAINT set</b>: every region whose PIXELS may differ from the last presented frame, accumulated on the UI
/// thread during record and carried across the render seam BY VALUE inside <see cref="FrameInfo"/>. Implements
/// <c>gpu-renderer.md §13.1</c> / <c>architecture-spec.md</c> "Partial present": up to <see cref="MaxRects"/> merged
/// rects (world-space float DIPs — the DIP→device rounding-OUT happens at the RHI leaf), or a forced full repaint with
/// a named <see cref="RepaintFullReason"/>. An empty region with <see cref="RepaintFullReason.None"/> means <b>nothing
/// changed</b>.
/// <para>
/// <b>This is NOT <see cref="FrameInfo.Damage"/>.</b> That field is the acrylic backdrop-cache invalidation union: a
/// single bounding rect over TRANSFORM-moved nodes only, which deliberately EXCLUDES a scroll viewport's own content
/// and tracks no paint-only change at all. The two accumulators answer different questions ("what would a cached blur
/// have to re-sample?" vs "what pixels must be redrawn?") and are maintained independently — never substitute one for
/// the other, and never widen one to satisfy the other's consumer.
/// </para>
/// <para>
/// Members are kept <b>pairwise disjoint</b> (they do not even abut), which is what makes <see cref="SummedArea"/> an
/// exact area rather than an over-count.
/// </para>
/// </summary>
public struct RepaintDamageRegion : IEquatable<RepaintDamageRegion>
{
    /// <summary>Accumulator capacity (canon §13.1: "≤16 merged rects"). Past this, the pair whose union wastes the
    /// least area is merged so the newcomer always lands — the region degrades in precision, never in correctness.</summary>
    public const int MaxRects = 16;

    private RepaintRectBuffer _rects;
    private byte _count;
    private RepaintFullReason _full;

    /// <summary>The whole target must repaint; <see cref="FullReason"/> names why. Rects are empty when this is set.</summary>
    public readonly bool IsFull => _full != RepaintFullReason.None;

    /// <summary><see cref="RepaintFullReason.None"/> unless a full repaint was forced.</summary>
    public readonly RepaintFullReason FullReason => _full;

    /// <summary>Live rect count (0 when <see cref="IsFull"/>, and 0 also means "nothing changed" when not full).</summary>
    public readonly int Count => _count;

    /// <summary>True when nothing changed at all: no rects and no forced full.</summary>
    public readonly bool IsEmpty => _count == 0 && _full == RepaintFullReason.None;

    public readonly RectF this[int index]
    {
        get
        {
            if ((uint)index >= _count) throw new ArgumentOutOfRangeException(nameof(index));
            return _rects[index];
        }
    }

    /// <summary>The live rects, in no particular order. The span aliases this instance's storage.</summary>
    [UnscopedRef]
    public ReadOnlySpan<RectF> AsSpan()
        => MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<RepaintRectBuffer, RectF>(ref _rects), _count);

    /// <summary>Union <paramref name="r"/> into the region, preserving the pairwise-disjoint invariant. Empty rects are
    /// ignored; a forced-full region ignores everything.</summary>
    public void Add(in RectF r)
    {
        if (_full != RepaintFullReason.None) return;
        if (r.W <= 0f || r.H <= 0f) return;

        RectF n = r;
        // Fold every member the newcomer touches INTO it and RESTART the scan: a fold GROWS the rect, which can bring a
        // member that was previously clear into contact. A single pass is not a fixpoint, and a leftover overlap would
        // silently break SummedArea (double-counted area) as well as the "disjoint replay rects" contract downstream.
        for (int i = 0; i < _count; i++)
        {
            if (!Adjacent(in _rects[i], in n)) continue;
            n = Union(in _rects[i], in n);
            _rects[i] = _rects[_count - 1];
            _count--;
            i = -1;   // restart
        }

        if (_count < MaxRects) { _rects[_count++] = n; return; }

        // At capacity with a disjoint newcomer: merge the pair (over all 17) whose union adds the least dead area, then
        // re-normalize — that merge can itself create a fresh overlap with a third member.
        Span<RectF> all = stackalloc RectF[MaxRects + 1];
        for (int i = 0; i < MaxRects; i++) all[i] = _rects[i];
        all[MaxRects] = n;
        int bestA = 0, bestB = 1;
        float bestWaste = float.MaxValue;
        for (int i = 1; i <= MaxRects; i++)
            for (int j = 0; j < i; j++)
            {
                RectF u = Union(in all[i], in all[j]);
                float waste = u.W * u.H - all[i].W * all[i].H - all[j].W * all[j].H;
                if (waste < bestWaste) { bestWaste = waste; bestA = j; bestB = i; }
            }
        all[bestA] = Union(in all[bestA], in all[bestB]);
        all[bestB] = all[MaxRects];   // compact: the (possibly self-) copy of the virtual 17th slot fills the hole
        for (int i = 0; i < MaxRects; i++) _rects[i] = all[i];
        Normalize();
    }

    /// <summary>Give up on partial repaint for this frame. The FIRST cause wins — a later, less specific reason must not
    /// overwrite the one that actually surrendered.</summary>
    public void ForceFull(RepaintFullReason reason)
    {
        if (reason == RepaintFullReason.None) return;
        _count = 0;
        if (_full == RepaintFullReason.None) _full = reason;
    }

    /// <summary>Absorb <paramref name="other"/> (the publish-gap accumulation: over-inclusion is the safe direction).
    /// A forced-full <paramref name="other"/> forces this region full with the same reason.</summary>
    public void Union(in RepaintDamageRegion other)
    {
        if (other._full != RepaintFullReason.None) { ForceFull(other._full); return; }
        for (int i = 0; i < other._count; i++) Add(in other._rects[i]);
    }

    /// <summary>Total damaged area. EXACT (not an over-count) because members are pairwise disjoint. 0 when full —
    /// callers that need "full == everything" use <see cref="Coverage"/>.</summary>
    public readonly float SummedArea()
    {
        float a = 0f;
        for (int i = 0; i < _count; i++) a += _rects[i].W * _rects[i].H;
        return a;
    }

    /// <summary>Damaged fraction of a <paramref name="width"/>×<paramref name="height"/> target, clamped to 0..1.
    /// A forced-full region reads 1.</summary>
    public readonly float Coverage(float width, float height)
    {
        if (_full != RepaintFullReason.None) return 1f;
        float total = width * height;
        if (total <= 0f) return 0f;
        float c = SummedArea() / total;
        return c < 0f ? 0f : (c > 1f ? 1f : c);
    }

    // Overlap-OR-abut (closed intervals): two rects that merely share an edge or a corner are folded together, so
    // "disjoint" here means "separated by real space" and SummedArea stays exact.
    private static bool Adjacent(in RectF a, in RectF b)
        => a.X <= b.X + b.W && b.X <= a.X + a.W && a.Y <= b.Y + b.H && b.Y <= a.Y + a.H;

    private static RectF Union(in RectF a, in RectF b)
    {
        float x0 = MathF.Min(a.X, b.X), y0 = MathF.Min(a.Y, b.Y);
        float x1 = MathF.Max(a.X + a.W, b.X + b.W), y1 = MathF.Max(a.Y + a.H, b.Y + b.H);
        return new RectF(x0, y0, x1 - x0, y1 - y0);
    }

    // Restore pairwise disjointness after an in-place merge. Bounded by MaxRects, and only reached on the at-capacity
    // path, so the O(n³) worst case is a few hundred float compares.
    private void Normalize()
    {
        bool merged = true;
        while (merged)
        {
            merged = false;
            for (int i = 0; i < _count && !merged; i++)
                for (int j = i + 1; j < _count; j++)
                {
                    if (!Adjacent(in _rects[i], in _rects[j])) continue;
                    _rects[i] = Union(in _rects[i], in _rects[j]);
                    _rects[j] = _rects[_count - 1];
                    _count--;
                    merged = true;
                    break;
                }
        }
    }

    // Hand-written, NEVER the synthesized record path: an [InlineArray] field has no IEquatable of its own, so the
    // compiler-generated comparison would fall through to ValueType.Equals — a boxing, reflection-driven compare on a
    // struct that rides the render seam every frame.
    public readonly bool Equals(RepaintDamageRegion other)
    {
        if (_full != other._full || _count != other._count) return false;
        for (int i = 0; i < _count; i++)
            if (!_rects[i].Equals(other._rects[i])) return false;
        return true;
    }

    public readonly override bool Equals(object? obj) => obj is RepaintDamageRegion r && Equals(r);

    public readonly override int GetHashCode()
    {
        var h = new HashCode();
        h.Add((byte)_full);
        h.Add(_count);
        for (int i = 0; i < _count; i++)
        {
            RectF e = _rects[i];
            h.Add(e.X); h.Add(e.Y); h.Add(e.W); h.Add(e.H);
        }
        return h.ToHashCode();
    }

    public readonly override string ToString()
        => _full != RepaintFullReason.None ? $"RepaintDamage(full:{_full})" : $"RepaintDamage({_count} rects)";
}
