namespace FluentGpu.Foundation;

/// <summary>What one row cell paints (scroll-rework Wave 0.E, scroll-rework-design.md §B.4). No type switch on
/// placeholder mode: a Text/Image/Glyph cell keeps its <see cref="RowCellKind"/> and its own rect — only the
/// recorder's DRAW differs (a rounded grey bar/box at the SAME rect). Lives in Foundation (not Dsl) so both the
/// authoring layer (<c>FluentGpu.Dsl.ListRowEl</c>/<c>RowCell</c>) and the scene/recorder layer
/// (<c>FluentGpu.Scene.SceneStore</c>/<c>FluentGpu.Render.SceneRecorder</c>) can reference it without Render taking a
/// dependency on Dsl — the same layering <see cref="SpanStyle"/>/<see cref="SpanRect"/> already follow.</summary>
public enum RowCellKind : byte { Text = 0, Image = 1, Glyph = 2, Rect = 3 }

/// <summary>The scene-owned, already-interned twin of <c>FluentGpu.Dsl.RowCell</c> (the reconciler's
/// <c>WriteRowCells</c> copies element-facing <c>string?</c> fields into <see cref="StringId"/>s here, exactly once
/// per write — never in the recorder, and never re-requested per frame for Image cells: <see cref="ImageId"/> is the
/// resolved <c>ImageHandle.Id</c> from the write-time <c>ImageCache.Request</c> call). One node's whole payload
/// (≤8 cells + Placeholder) is the pooled slot <c>SceneStore</c>/<c>SceneRecordingSnapshot</c> store per node index
/// (the same grow-only-capacity shape as the span-text side tables).</summary>
public readonly record struct RowCellRecorded
{
    public RowCellKind Kind { get; init; }
    public RectF Rect { get; init; }
    public StringId Text { get; init; }
    /// <summary>Kind == Image only — see the type doc comment.</summary>
    public int ImageId { get; init; }
    public ColorF Color { get; init; }
    public CornerRadius4 Corners { get; init; }
    public TextTrim Trim { get; init; }
    public float FontSize { get; init; }
    public ushort FontWeight { get; init; }
    public StringId FontFamily { get; init; }
}
