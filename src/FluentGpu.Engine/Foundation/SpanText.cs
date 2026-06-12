using System.Threading;

namespace FluentGpu.Foundation;

/// <summary>
/// One inline run of a rich-text paragraph (the WinUI <c>TextBlock.Inlines</c> Run/Bold/Hyperlink model, rtb-01).
/// A <c>SpanTextEl</c> paragraph is an array of these; the WHOLE paragraph shapes as ONE flow (spans concatenated,
/// wrapped together — exactly how WinUI lays a paragraph's inline collection, RichTextBlock.g.h:63 get_Blocks).
/// <para><see cref="Weight"/> 0 / <see cref="Size"/> null / <see cref="FontFamily"/> null / <see cref="Color"/> A==0
/// all mean "inherit the paragraph base". <see cref="OnClick"/> non-null makes this a HYPERLINK span: the engine
/// resolves the Hand cursor over the span's laid-out rects and fires the action on click/tap — WinUI's inline
/// Hyperlink (SetCursor(MouseCursorHand) on hyperlink hover, RichTextBlock.cpp:2995 / TextBlock.cpp:3488). Accent
/// foreground + underline styling is the CALLER's job (HyperlinkForeground theming, generic.xaml:1120-1122).</para>
/// <para>No italic axis: the shaper resolves faces by (family, weight) only — DWRITE_FONT_STYLE_NORMAL is fixed
/// (TextLayoutEngine face resolution); FontStyle arrives with the italics workstream (plan §Text display).</para>
/// </summary>
public readonly record struct TextSpan(
    string Text,
    ushort Weight = 0,
    ColorF Color = default,
    bool Underline = false,
    bool Strikethrough = false,
    float? Size = null,
    string? FontFamily = null,
    Action? OnClick = null);

/// <summary>
/// The POD shaping overlay for one span of a span run: the UTF-16 char range [<see cref="Start"/>, <see cref="End"/>)
/// of the CONCATENATED paragraph text plus the style deltas the text seam applies over the base
/// <c>TextStyle</c>. Weight 0 / SizeDip 0 / FontFamily empty / Color A==0 = inherit the base. Built by the
/// reconciler from <see cref="TextSpan"/>s; resolved by both text backends and the glyph renderer through
/// <see cref="SpanRunTable.Shared"/>, so measure ≡ hit-test ≡ render for span runs too.
/// </summary>
public readonly record struct SpanStyle(int Start, int End, ushort Weight, float SizeDip, StringId FontFamily, ColorF Color, byte Flags)
{
    /// <summary>Draw the face-metric underline bar under this span (WinUI TextDecorations.Underline, per wrapped line).</summary>
    public const byte UnderlineBit = 1;
    /// <summary>Draw the face-metric strikethrough bar through this span.</summary>
    public const byte StrikethroughBit = 2;
    /// <summary>Hyperlink span: Hand cursor over its laid rects; click fires the element's TextSpan.OnClick
    /// (RichTextBlock.cpp:2995 SetCursor(MouseCursorHand)).</summary>
    public const byte LinkBit = 4;
}

/// <summary>One laid-out rect artifact of a span run (paragraph-node-local DIP): the line-fragment geometry of a
/// decorated/link span. <see cref="Kind"/> is the single <see cref="SpanStyle"/> flag bit it serves — Link rects are
/// the FULL line-fragment band (hit-testing, Hand cursor); Underline/Strikethrough rects are the positioned BARS
/// (the recorder fills them verbatim, no font-seam touch at record time).</summary>
public readonly record struct SpanRect(RectF Rect, int Span, byte Kind);

/// <summary>An immutable snapshot of a span run's laid-out rect artifacts at one wrap width. Published whole by the
/// text seam on a measure-cache miss (content/width change) and read lock-free by the recorder (decoration bars) and
/// the input dispatcher (hyperlink cursor/click) — steady frames never re-touch the font seam.</summary>
public sealed class SpanRunRects(float maxWidth, SpanRect[] rects)
{
    /// <summary>The wrap width the rects were laid at (the same maxWidth the measure used).</summary>
    public readonly float MaxWidth = maxWidth;
    public readonly SpanRect[] Rects = rects;
}

/// <summary>A registered span run: the immutable per-span shaping overlays plus the seam-published layout artifacts.
/// Lives in <see cref="SpanRunTable.Shared"/>; referenced from <c>TextStyle.SpanRunId</c> (the measure-cache /
/// shaped-run-cache key), so a style change ⇒ a fresh run id ⇒ every downstream cache self-invalidates.</summary>
public sealed class SpanRun(SpanStyle[] spans)
{
    public readonly SpanStyle[] Spans = spans;
    private SpanRunRects? _rects;

    /// <summary>The latest seam-published rect artifacts (null until the first measure). Volatile read — the UI thread
    /// publishes during layout while the recorder may read the previous frame's snapshot.</summary>
    public SpanRunRects? Rects => Volatile.Read(ref _rects);

    /// <summary>Publish a fresh artifact snapshot (text seam, measure-miss path; UI thread).</summary>
    public void PublishRects(SpanRunRects rects) => Volatile.Write(ref _rects, rects);
}

/// <summary>
/// Process-wide registry of span runs — the interner-style side channel that lets the multi-run style overlay ride an
/// <c>int</c> through the POD pipeline (<c>TextStyle.SpanRunId</c> → DrawList <c>DrawGlyphRunCmd.SpanRunId</c>) the way
/// strings ride <see cref="StringId"/> through <see cref="StringTable"/>.
///
/// <para><b>Seam-safe (threading-render-seam.md §9, the StringTable discipline):</b> chunked backing store — chunks are
/// allocated once and never moved; a release-store of the count publishes each new slot, the reader acquire-loads the
/// count before indexing. The UI thread is the single writer (reconciler, at element-change time — never in frame
/// phases 6–13); the render-side glyph path resolves the previous frame's ids concurrently.</para>
///
/// <para><b>Static <see cref="Shared"/> (deliberate):</b> unlike <see cref="StringTable"/> (host-instanced and threaded
/// through every constructor), the render-side consumer reaches a span run only through the POD id baked in the draw
/// stream — there is no host seam to hand an instance through. One process = one UI thread = one writer, ids burn
/// monotonically and are never reused, so a process-global table is safe by the same argument that makes the ids safe.</para>
///
/// <para><b>Reclamation:</b> owners (the scene's span-text side-table) pair <see cref="AddRef"/>/<see cref="Release"/>;
/// a run whose count hits 0 has its slot cleared after a creation-distance quarantine (≥ <see cref="QuarantineDistance"/>
/// newer runs registered — far beyond the in-flight frame depth at any realistic churn), so a draw list recorded just
/// before the release still resolves. A cleared id resolves null → consumers fall back to the base style. Ids are
/// never reused.</para>
/// </summary>
public sealed class SpanRunTable
{
    public static readonly SpanRunTable Shared = new();

    private const int ChunkBits = 10;
    private const int ChunkSize = 1 << ChunkBits;   // 1024 runs/chunk
    private const int ChunkMask = ChunkSize - 1;
    private const int QuarantineDistance = 256;

    private SpanRun?[]?[] _chunks = new SpanRun?[4][];
    private int[]?[] _refs = new int[4][];
    private int _count = 1;   // id 0 = "no span run" (the plain single-style path)
    private readonly Queue<(int Id, int CountAtRelease)> _pendingClear = new();

    /// <summary>Register a span run (UI thread). The spans array is taken by reference and must not be mutated after.</summary>
    public int Create(SpanStyle[] spans)
    {
        int id = _count;
        int ci = id >> ChunkBits, off = id & ChunkMask;
        if (ci >= _chunks.Length) Grow(ci);
        if (_chunks[ci] is null)
        {
            Volatile.Write(ref _chunks[ci], new SpanRun?[ChunkSize]);
            _refs[ci] = new int[ChunkSize];
        }
        _chunks[ci]![off] = new SpanRun(spans);
        _refs[ci]![off] = 0;
        Volatile.Write(ref _count, id + 1);   // release: a reader that sees the count sees the slot
        DrainQuarantine();
        return id;
    }

    /// <summary>Take an ownership reference (UI thread; the scene's span-text side-table is the owner).</summary>
    public void AddRef(int id)
    {
        if (id <= 0 || id >= _count) return;
        _refs[id >> ChunkBits]![id & ChunkMask]++;
    }

    /// <summary>Drop an ownership reference (UI thread). The last release schedules the slot clear behind the
    /// creation-distance quarantine. Releasing id 0 / an unknown id is a no-op.</summary>
    public void Release(int id)
    {
        if (id <= 0 || id >= _count) return;
        int ci = id >> ChunkBits, off = id & ChunkMask;
        int[]? refs = _refs[ci];
        if (refs is null || refs[off] == 0) return;
        if (--refs[off] > 0) return;
        _pendingClear.Enqueue((id, _count));
    }

    /// <summary>Resolve an id to its run, or null (id 0 / reclaimed). Safe from the render thread concurrently with
    /// <see cref="Create"/> (acquire on count, chunks never move).</summary>
    public SpanRun? Resolve(int id)
    {
        if (id <= 0 || id >= Volatile.Read(ref _count)) return null;
        var chunks = Volatile.Read(ref _chunks);
        var chunk = Volatile.Read(ref chunks[id >> ChunkBits]);
        return chunk?[id & ChunkMask];
    }

    private void DrainQuarantine()
    {
        while (_pendingClear.Count > 0)
        {
            var (id, at) = _pendingClear.Peek();
            if (_count - at <= QuarantineDistance) break;
            _pendingClear.Dequeue();
            int ci = id >> ChunkBits, off = id & ChunkMask;
            if (_refs[ci]![off] != 0) continue;   // resurrected by a later AddRef — keep it
            _chunks[ci]![off] = null;             // atomic ref store — a concurrent Resolve sees null → base style
        }
    }

    private void Grow(int neededChunkIndex)
    {
        int newLen = Math.Max(_chunks.Length * 2, neededChunkIndex + 1);
        var bigger = new SpanRun?[newLen][];
        Array.Copy(_chunks, bigger, _chunks.Length);
        var biggerRefs = new int[newLen][];
        Array.Copy(_refs, biggerRefs, _refs.Length);
        _refs = biggerRefs;
        Volatile.Write(ref _chunks, bigger);
    }
}
