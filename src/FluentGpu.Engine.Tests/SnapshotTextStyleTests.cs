using System;
using System.Runtime.CompilerServices;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Render;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Text;
using Xunit;

namespace FluentGpu.Engine.Tests;

public sealed class SnapshotTextStyleTests
{
    static (SceneStore Scene, NodeHandle Leaf) Boxes(int count)
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        scene.Root = scene.CreateNode(1);
        scene.Bounds(scene.Root) = new(0, 0, 800, 600);
        NodeHandle leaf = default;
        for (int i = 0; i < count; i++)
        {
            leaf = scene.CreateNode(2);
            scene.AppendChild(scene.Root, leaf);
            scene.Bounds(leaf) = new(0, 0, 200, 40);
        }
        return (scene, leaf);
    }

    static TextStyle Style(StringId family = default) => new(family, 19, 650,
        TextWrap.WrapWholeWords, TextTrim.WordEllipsis, 3, 25, 27,
        LineStacking.BlockLineHeight, TextLineBounds.Tight, MinSizeDip: 11);

    static void SetText(SceneStore scene, NodeHandle node, TextStyle style)
    {
        scene.Paint(node).VisualKind = VisualKind.Text;
        scene.Layout(node).TextStyle = style;
        scene.NoteCaptureChanged((int)node.Raw.Index);
    }

    static void CaptureNext(SceneStore scene, SceneRecordingSnapshot snapshot, ulong baseline, bool incremental)
    {
        if (incremental) Assert.True(snapshot.CaptureIncremental(scene, [], baseline));
        else snapshot.Capture(scene);
        Assert.Equal(0, snapshot.IncrementalParityFailures);
        var full = new SceneRecordingSnapshot();
        full.Capture(scene);
        Assert.True(snapshot.EqualsForParity(full, out string mismatch), mismatch);
        full.ReleaseResources();
    }

    [Fact]
    public void SparseStyleCapacityFollowsTextRowsInsteadOfSceneHighWater()
    {
        var (small, a) = Boxes(32);
        var (large, b) = Boxes(8000);
        SetText(small, a, Style());
        SetText(large, b, Style());
        var first = new SceneRecordingSnapshot();
        var second = new SceneRecordingSnapshot();
        first.Capture(small);
        second.Capture(large);
        Assert.Equal(1, second.TextStyleRowCount);
        Assert.Equal(first.TextStyleCapacity, second.TextStyleCapacity);
        Assert.Equal((long)second.TextStyleCapacity.Values * Unsafe.SizeOf<TextStyle>(), second.TextStyleValueCapacityBytes);
        long formerDensePayload = (long)second.Capacity * Unsafe.SizeOf<LayoutInput>();
        Assert.True(second.TextStyleValueCapacityBytes * 100 < formerDensePayload);
        // Value payload arithmetic only: the capacity tuple also exposes index/free reserves,
        // and neither number claims total object size or process working-set savings.
    }

    [Fact]
    public void HeldStyleIsAValueCopyAndNonRecordingLayoutDoesNotAffectParity()
    {
        var (scene, node) = Boxes(1);
        TextStyle style = Style();
        SetText(scene, node, style);
        var held = new SceneRecordingSnapshot();
        held.Capture(scene);
        scene.Layout(node).Gap = 91;
        scene.Layout(node).Width = 400;
        var samePixels = new SceneRecordingSnapshot();
        samePixels.Capture(scene);
        Assert.True(held.EqualsForParity(samePixels, out string mismatch), mismatch);
        scene.Layout(node).TextStyle = style with { SizeDip = 31, Weight = 900 };
        var changed = new SceneRecordingSnapshot();
        changed.Capture(scene);
        Assert.Equal(style, held.RecordingTextStyle(node));
        Assert.NotEqual(held.RecordingTextStyle(node), changed.RecordingTextStyle(node));
        Assert.False(held.EqualsForParity(changed, out mismatch));
        Assert.Contains("TextStyle", mismatch);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TextKindChangesAndParkingDropAndRestoreStyles(bool incremental)
    {
        var (scene, node) = Boxes(1);
        SetText(scene, node, Style());
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        scene.NotePublished(1);
        scene.Paint(node).VisualKind = VisualKind.Box;
        scene.Layout(node).TextStyle = default;
        scene.NoteCaptureChanged((int)node.Raw.Index);
        CaptureNext(scene, snapshot, 1, incremental);
        Assert.Equal(0, snapshot.TextStyleRowCount);
        Assert.False(snapshot.TryGetTextStyle(node, out _));
        Assert.Throws<InvalidOperationException>(() => snapshot.RecordingTextStyle(node));
        scene.NotePublished(2);
        SetText(scene, node, Style() with { SizeDip = 23 });
        CaptureNext(scene, snapshot, 2, incremental);
        Assert.Equal(23, snapshot.RecordingTextStyle(node).SizeDip);
        scene.NotePublished(3);
        scene.Detach(node);
        // This fixture bypasses the reconciler's bulk topology notification. Its already-dirty
        // parent must explicitly journal the changed FirstChild for an incremental capture.
        scene.NoteCaptureChanged((int)scene.Root.Raw.Index);
        CaptureNext(scene, snapshot, 3, incremental);
        Assert.Equal(0, snapshot.TextStyleRowCount);
        Assert.False(snapshot.TryGetTextStyle(node, out _));
        scene.NotePublished(4);
        scene.AppendChild(scene.Root, node);
        scene.NoteCaptureChanged((int)scene.Root.Raw.Index);
        CaptureNext(scene, snapshot, 4, incremental);
        Assert.Equal(1, snapshot.TextStyleRowCount);
        Assert.Equal(23, snapshot.RecordingTextStyle(node).SizeDip);
    }

    [Fact]
    public void RecycledSlotRejectsTheOldGenerationEvenWhenNewOccupantIsText()
    {
        var (scene, old) = Boxes(1);
        SetText(scene, old, Style());
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        scene.NotePublished(1);
        scene.FreeSubtree(old);
        var replacement = scene.CreateNode(2);
        scene.AppendChild(scene.Root, replacement);
        scene.NoteCaptureChanged((int)scene.Root.Raw.Index);
        SetText(scene, replacement, Style() with { Weight = 800 });
        CaptureNext(scene, snapshot, 1, incremental: true);
        Assert.Equal(old.Raw.Index, replacement.Raw.Index);
        Assert.False(snapshot.TryGetTextStyle(old, out _));
        Assert.Throws<InvalidOperationException>(() => snapshot.RecordingTextStyle(old));
        Assert.Equal(800, snapshot.RecordingTextStyle(replacement).Weight);
        Assert.False(snapshot.TryGetTextStyle(default, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecorderKeepsEveryStyleFieldAndFitSizeForHeldPlainAndWipeText(bool wipe)
    {
        var (scene, node) = Boxes(1);
        var strings = new StringTable();
        TextStyle style = Style(strings.Intern("snapshot font"));
        SetText(scene, node, style);
        scene.Paint(node).Text = strings.Intern("snapshot glyphs");
        scene.MeasureCacheRef(node).Store(new TextMeasureEntry { Valid = true, MaxW = 200, FitSize = 17 });
        if (wipe) scene.SetGlyphWipe(node, new GlyphWipe(new(1, 0, 0, 1), new(0, 1, 0, 1), .375f));
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        scene.Layout(node).TextStyle = new(default, 42, 100);
        var draw = new DrawList();
        snapshot.Recording.Record(snapshot, draw);
        using var gpu = new HeadlessGpuDevice();
        gpu.SubmitDrawList(draw.Bytes, draw.SortKeys, default);
        if (wipe)
        {
            var glyph = Assert.Single(gpu.LastGlyphGradients);
            Assert.Equal(style.FontFamily, glyph.Family);
            Assert.Equal(17, glyph.FontSize);
            Assert.Equal(style.Weight, glyph.Weight);
            Assert.Equal((int)style.Wrap, glyph.Wrap);
            Assert.Equal((int)style.Trim, glyph.Trim);
            Assert.Equal(style.MaxLines, glyph.MaxLines);
            Assert.Equal(style.CharSpacing, glyph.CharSpacing);
            Assert.Equal(style.LineHeight, glyph.LineHeight);
            Assert.Equal((int)style.Stacking, glyph.LineStacking);
            Assert.Equal((int)style.LineBounds, glyph.LineBounds);
            Assert.Equal(.375f, glyph.Split);
        }
        else
        {
            var glyph = Assert.Single(gpu.LastGlyphs);
            Assert.Equal(style.FontFamily, glyph.Family);
            Assert.Equal(17, glyph.FontSize);
            Assert.Equal(style.Weight, glyph.Weight);
            Assert.Equal((int)style.Wrap, glyph.Wrap);
            Assert.Equal((int)style.Trim, glyph.Trim);
            Assert.Equal(style.MaxLines, glyph.MaxLines);
            Assert.Equal(style.CharSpacing, glyph.CharSpacing);
            Assert.Equal(style.LineHeight, glyph.LineHeight);
            Assert.Equal((int)style.Stacking, glyph.LineStacking);
            Assert.Equal((int)style.LineBounds, glyph.LineBounds);
        }
    }

    [Fact]
    public void FirstTextAndRepeatedStyleReplacementUseReservedCaptureStorage()
    {
        var (scene, node) = Boxes(100);
        var snapshot = new SceneRecordingSnapshot();
        for (int i = 0; i < 4; i++) snapshot.Capture(scene);
        var capacity = snapshot.TextStyleCapacity;
        SetText(scene, node, Style());
        long before = GC.GetAllocatedBytesForCurrentThread();
        snapshot.Capture(scene);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal(capacity, snapshot.TextStyleCapacity);
        // Warm the Debug parity auditor and incremental capture independently of the measured loop.
        for (ulong seq = 1; seq <= 4; seq++)
        {
            scene.NotePublished(seq);
            SetText(scene, node, Style() with { SizeDip = 20 + seq });
            Assert.True(snapshot.CaptureIncremental(scene, [], seq));
        }
        scene.NotePublished(5);
        SetText(scene, node, Style() with { SizeDip = 29 });
        before = GC.GetAllocatedBytesForCurrentThread();
        bool captured = snapshot.CaptureIncremental(scene, [], 5);
        TextStyle result = snapshot.RecordingTextStyle(node);
        allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(captured);
        Assert.Equal(29, result.SizeDip);
        Assert.Equal(capacity, snapshot.TextStyleCapacity);
        Assert.True(SceneRecordingSnapshot.ParityVerifyCompiledIn || allocated == 0,
            $"Release incremental style replacement allocated {allocated} bytes.");
        Assert.Equal(0, snapshot.IncrementalParityFailures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AuthoredFontAndSpanRemainPinnedForTextAndNonTextNodes(bool textNode)
    {
        var (scene, node) = Boxes(1);
        var strings = new StringTable();
        StringId family = strings.Intern("authored snapshot font");
        StringId spanFamily = strings.Intern("authored snapshot span font");
        strings.AddRef(family);
        strings.AddRef(spanFamily);
        int spanId = SpanRunTable.Shared.Create([new(0, 3, 400, 12, spanFamily, default, SpanStyle.UnderlineBit)]);
        SpanRunTable.Shared.AddRef(spanId);
        var run = SpanRunTable.Shared.Resolve(spanId)!;
        run.PublishRects(new(100, [new(new(0, 10, 30, 1), 0, SpanStyle.UnderlineBit)]));
        scene.Paint(node).VisualKind = textNode ? VisualKind.Text : VisualKind.Box;
        scene.Layout(node).TextStyle = Style(family) with { SpanRunId = spanId };
        var snapshot = new SceneRecordingSnapshot();
        try
        {
            snapshot.Capture(scene);
            snapshot.RetainStrings(strings);
            Assert.Equal(1, snapshot.TextStyleRowCount);
            Assert.True(snapshot.TryGetSpanDecorations(node, out _, out var rects));
            Assert.Single(rects.ToArray());
            // Release authored ownership; only the detached frame retains these identities now.
            scene.Layout(node).TextStyle = default;
            strings.Release(family);
            strings.Release(spanFamily);
            SpanRunTable.Shared.Release(spanId);
            for (int i = 0; i < 100; i++) strings.Tick();
            ChurnSpans();
            Assert.Equal("authored snapshot font", strings.Resolve(family));
            Assert.Equal("authored snapshot span font", strings.Resolve(spanFamily));
            Assert.NotNull(SpanRunTable.Shared.Resolve(spanId));
            Assert.Equal(spanId, snapshot.RecordingTextStyle(node).SpanRunId);
            // A safe recapture without those identities unpins them through the ordinary lifecycle.
            scene.NotePublished(1);
            scene.NoteCaptureChanged((int)node.Raw.Index);
            CaptureNext(scene, snapshot, 1, incremental: true);
            snapshot.RetainStrings(strings);
            Assert.Equal(textNode ? 1 : 0, snapshot.TextStyleRowCount);
            Assert.False(snapshot.TryGetSpanDecorations(node, out _, out _));
            for (int i = 0; i < 100; i++) strings.Tick();
            ChurnSpans();
            Assert.Empty(strings.Resolve(family));
            Assert.Empty(strings.Resolve(spanFamily));
            Assert.Null(SpanRunTable.Shared.Resolve(spanId));
        }
        finally
        {
            snapshot.ReleaseResources();
        }
    }

    static void ChurnSpans()
    {
        for (int i = 0; i < 300; i++)
        {
            int id = SpanRunTable.Shared.Create([]);
            SpanRunTable.Shared.AddRef(id);
            SpanRunTable.Shared.Release(id);
        }
    }
}
