using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Reconciler;

// Scroll-rework Wave 0.E (scroll-rework-design.md §B.4): ListRowEl is ONE scene node whose RowPaint payload draws up
// to 8 cells — the flat, one-node twin of a ~90-node/~50-bind track-row GridEl. Mirrors SpanTextEl's bound-channel
// shape exactly (Reconciler.Spans.cs): Cells : Prop<RowCells> is ONE channel, resolved through the SAME
// WriteRowCells the static WriteColumns path uses, so a recycle that rebinds value-equal cells reshapes/redecodes
// nothing new (the per-cell equality gate below). Fill/HoverFill/SelectedFill/Placeholder are separate Prop<T>
// channels bound the same BindEffect way BoxEl.Fill is (Reconciler.cs BindNode; re-wired by Reconciler.Rewire.cs).
public sealed partial class TreeReconciler
{
    /// <summary>Interns/copies <paramref name="cells"/> (≤8; extras are dropped — RowPaint's contract) into the
    /// scene-owned <see cref="RowCellRecorded"/> array, requests any Image cell's decode, and (un)sets
    /// <see cref="InteractionInfo.RowCellsBit"/> so the dispatcher only hit-tests THIS node's cells when at least one
    /// carries an <see cref="ListRowEl.OnCellClick"/>-reachable click target (a row with a click handler at all —
    /// index-resolved, like <c>SpanTextEl.OnSpanClick</c>). Zero-alloc on a steady-state re-fire: the cell buffer is
    /// stack-allocated and <see cref="SceneStore.SetRowCells"/>'s backing array is pooled (grow-only, never shrinks).</summary>
    private void WriteRowCells(NodeHandle node, ReadOnlySpan<RowCell> cells, bool placeholder, ColorF placeholderColor, bool hasClickHandler)
    {
        // Ref-counted intern bookkeeping (StringTable.cs's own warning: "a 100k-row virtual list streams unique row
        // text through the table — append-only interning is an unbounded leak"): every cell slot's Text/FontFamily id
        // is AddRef'd here and the id it REPLACES is Release'd, the same swap TextEl.WriteColumns does for one field,
        // extended per-cell over up to 8 slots (old ids come from whatever this node's slot held last write, mount or
        // recycle alike — never from a second side-table).
        bool hadOld = _scene.TryGetRowCells(node, out var oldCells, out _, out _);
        int n = System.Math.Min(cells.Length, 8);
        Span<RowCellRecorded> rec = stackalloc RowCellRecorded[8];
        for (int i = 0; i < n; i++)
        {
            ref readonly var c = ref cells[i];
            StringId oldText = hadOld && i < oldCells.Length ? oldCells[i].Text : default;
            StringId oldFam = hadOld && i < oldCells.Length ? oldCells[i].FontFamily : default;

            var textId = c.Text is { Length: > 0 } ? _strings.Intern(c.Text) : default;
            if (textId != oldText) { if (!textId.IsEmpty) _strings.AddRef(textId); if (!oldText.IsEmpty) _strings.Release(oldText); }

            var famId = c.FontFamily is { Length: > 0 } ? _strings.Intern(c.FontFamily) : default;
            if (famId != oldFam) { if (!famId.IsEmpty) _strings.AddRef(famId); if (!oldFam.IsEmpty) _strings.Release(oldFam); }

            int imageId = 0;
            if (c.Kind == RowCellKind.Image && c.ImageSource is { Length: > 0 } src && Images is not null)
            {
                // Decode target = the cell's own row-local rect — a row cell's size is fixed by the row's column
                // layout (not measured), so this is stable across recycles that keep the same column set.
                int dw = (int)c.Rect.W, dh = (int)c.Rect.H;
                ImagePriority prio = ImageRequestPriority(node);
                // Requested but NOT pinned (Images.Request/PinImageNode is a one-image-per-node contract today —
                // scroll-rework-design.md B.4 residency pinning for MULTI-image rows is left to a follow-up wave;
                // documented gap, not a silent behavior change: the image still decodes and paints, just without the
                // extra residency-priority guarantee a pinned ImageEl node gets under memory pressure).
                imageId = Images.Request(src, dw, dh, prio, blurHash: null, transition: null).Id;
            }
            rec[i] = new RowCellRecorded
            {
                Kind = c.Kind, Rect = c.Rect, Text = textId, ImageId = imageId, Color = c.Color,
                Corners = c.Corners, Trim = c.Trim, FontSize = c.FontSize, FontWeight = c.FontWeight, FontFamily = famId,
            };
        }
        // A shrunk cell count (rare — RowPaint's cell count is normally shape-stable per column-set) releases the
        // ids the dropped tail slots held.
        if (hadOld) for (int i = n; i < oldCells.Length; i++)
        {
            if (!oldCells[i].Text.IsEmpty) _strings.Release(oldCells[i].Text);
            if (!oldCells[i].FontFamily.IsEmpty) _strings.Release(oldCells[i].FontFamily);
        }
        _scene.SetRowCells(node, rec[..n], placeholder, placeholderColor);

        ref InteractionInfo ii = ref _scene.Interaction(node);
        if (hasClickHandler)
        {
            ii.HandlerMask |= InteractionInfo.RowCellsBit;
            _scene.Mark(node, NodeFlags.WantsPointer);
        }
        else ii.HandlerMask &= ~InteractionInfo.RowCellsBit;
    }

    /// <summary>Bound <c>Cells</c>/<c>Fill</c>/<c>HoverFill</c>/<c>SelectedFill</c>/<c>Placeholder</c> wiring — called
    /// from BindNode for a ListRowEl with any bound channel. Each channel is its own equality-gated BindEffect, the same
    /// shape as every other element's bound Prop&lt;T&gt; (Reconciler.cs's BoxEl.Fill block): wired at mount, re-wired in
    /// place when a re-render/recycle binds a new thunk/signal (Reconciler.Rewire.cs). Static companions (the placeholder
    /// color, the click flag, a static Placeholder) are read off <c>fx.El</c> — the element last reconciled — per fire.</summary>
    private void BindListRowCells(NodeHandle node, ListRowEl lr)
    {
        if (lr.Cells.IsBound)
        {
            var fx = new BindEffect<RowCells>(Runtime, lr, static e => e is ListRowEl x ? x.Cells : default);
            AddBinding(node, fx.Start(() =>
            {
                NodeBindingFireCount++;
                if (!_scene.IsLive(node)) return;
                RowCells current = fx.Read();
                var row = (ListRowEl)fx.El;
                // Current() = the static value, or the bound placeholder's thunk/signal read (which subscribes this
                // effect to it, exactly as before).
                WriteRowCells(node, current.AsSpan(), row.Placeholder.Current(), row.PlaceholderColor, row.OnCellClick is not null);
                NodeBindingWriteCount++;
                _scene.Mark(node, NodeFlags.PaintDirty);
            }));
        }

        if (lr.Fill.IsBound)
        {
            var fx = new BindEffect<ColorF>(Runtime, lr, static e => e is ListRowEl x ? x.Fill : default);
            AddBinding(node, fx.Start(() =>
            {
                NodeBindingFireCount++;
                if (!_scene.IsLive(node)) return;
                ColorF next = fx.Read();
                ref var paint = ref _scene.Paint(node);
                if (paint.Fill == next) return;
                paint.Fill = next;
                NodeBindingWriteCount++;
                _scene.Mark(node, NodeFlags.PaintDirty);
            }));
        }
        if (lr.HoverFill.IsBound)
        {
            var fx = new BindEffect<ColorF>(Runtime, lr, static e => e is ListRowEl x ? x.HoverFill : default);
            AddBinding(node, fx.Start(() =>
            {
                NodeBindingFireCount++;
                if (!_scene.IsLive(node)) return;
                ColorF next = fx.Read();
                ref var paint = ref _scene.Paint(node);
                if (paint.HoverFill == next) return;
                paint.HoverFill = next;
                NodeBindingWriteCount++;
                _scene.Mark(node, NodeFlags.PaintDirty);
            }));
        }
        if (lr.SelectedFill.IsBound)
        {
            // Rides the generic PressedFill channel/state (ListRowEl.SelectedFill doc comment) — a row has no
            // separate pressed visual, so Selected repurposes the third paint-fill slot every element already has.
            var fx = new BindEffect<ColorF>(Runtime, lr, static e => e is ListRowEl x ? x.SelectedFill : default);
            AddBinding(node, fx.Start(() =>
            {
                NodeBindingFireCount++;
                if (!_scene.IsLive(node)) return;
                ColorF next = fx.Read();
                ref var paint = ref _scene.Paint(node);
                if (paint.PressedFill == next) return;
                paint.PressedFill = next;
                NodeBindingWriteCount++;
                _scene.Mark(node, NodeFlags.PaintDirty);
            }));
        }
        if (lr.Placeholder.IsBound)
        {
            var fx = new BindEffect<bool>(Runtime, lr, static e => e is ListRowEl x ? x.Placeholder : default);
            AddBinding(node, fx.Start(() =>
            {
                NodeBindingFireCount++;
                if (!_scene.IsLive(node)) return;
                bool next = fx.Read();
                _scene.SetRowPlaceholder(node, next, ((ListRowEl)fx.El).PlaceholderColor);
                NodeBindingWriteCount++;
                _scene.Mark(node, NodeFlags.PaintDirty);
            }));
        }
    }
}
