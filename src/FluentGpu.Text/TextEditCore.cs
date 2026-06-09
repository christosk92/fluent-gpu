using System.Globalization;

namespace FluentGpu.Text;

/// <summary>
/// The portable text-editing state machine: an <see cref="EditDocument"/> + caret/selection + bounded undo, with
/// WinUI TextBox semantics made exact. Pure C# over the BCL — no PAL/Scene/Render types — so the same core drives
/// TextBox/PasswordBox/NumberBox/AutoSuggestBox/ComboBox on Windows AND headless checks.
///
/// Contracts every mutation upholds: caret positions are grapheme boundaries (never inside a surrogate pair, ZWJ
/// emoji, or combining sequence); <see cref="Anchor"/>/<see cref="Active"/> stay in [0, Length]; every document
/// change bumps <c>Doc.Version</c>; undo/redo restore the EXACT before/after caret+anchor pairs. Caret movement and
/// selection ops allocate nothing — strings materialize only at the user-gesture edge (copy, undo capture, paste
/// normalization).
/// </summary>
public sealed class TextEditCore
{
    private readonly TextUndoStack _undo = new();

    /// <summary>The owned document. Callers may read it freely; all writes must go through this core so caret,
    /// undo, and grapheme invariants hold (the raw document does not know about any of them).</summary>
    public EditDocument Doc { get; } = new();

    /// <summary>The fixed end of the selection (where Shift-selection started). Equals <see cref="Active"/> when
    /// the selection is empty.</summary>
    public int Anchor { get; private set; }

    /// <summary>The moving end of the selection — the caret.</summary>
    public int Active { get; private set; }

    public bool HasSelection => Anchor != Active;

    /// <summary>The selection normalized to (min, length) regardless of drag direction.</summary>
    public (int Start, int Length) Selection
        => Anchor <= Active ? (Anchor, Active - Anchor) : (Active, Anchor - Active);

    /// <summary>The selected text ("" when collapsed). Allocates — user-gesture edge only.</summary>
    public string SelectedText
    {
        get
        {
            var (start, length) = Selection;
            return length == 0 ? string.Empty : Doc.GetText(start, length);
        }
    }

    /// <summary>
    /// The Up/Down goal column in layout DIPs. The CONTROL owns it (only the control has visual metrics): it reads
    /// the value when applying Up/Down and writes it back after the vertical move. The core's only obligation is to
    /// reset it to NaN on every horizontal move and edit, so a stale column never teleports the caret.
    /// </summary>
    public float StickyX = float.NaN;

    /// <summary>Maximum length in UTF-16 code units; 0 = unlimited (WinUI <c>MaxLength</c>). Enforced on insert and
    /// paste by clamping the INSERTED text (snapped down to a grapheme boundary so a clamp can never split a
    /// cluster); existing content is never truncated by setting this.</summary>
    public int MaxLength { get; set; }

    /// <summary>When true, all mutations no-op (including undo/redo) but caret movement, selection, and copy keep
    /// working — WinUI's read-only TextBox.</summary>
    public bool IsReadOnly { get; set; }

    /// <summary>When false (single-line), <see cref="InsertText"/> keeps only the FIRST line of incoming text
    /// (WinUI's paste flattening) and <see cref="TextEditCommand.InsertNewline"/> no-ops.</summary>
    public bool AcceptsReturn { get; set; }

    public bool CanUndo => _undo.CanUndo;
    public bool CanRedo => _undo.CanRedo;

    /// <summary>Observable undo depth (headless coalescing checks: typing "abc" leaves exactly 1).</summary>
    public int UndoDepth => _undo.UndoDepth;

    public void ClearUndoHistory() => _undo.Clear();

    // ---------------------------------------------------------------------------------------------------------------
    // Caret & selection.
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Place the caret (pointer click / programmatic). Clamps to [0, Length] and snaps DOWN to a grapheme boundary.
    /// <paramref name="extend"/> keeps <see cref="Anchor"/> (Shift+click); otherwise the selection collapses.
    /// Caret relocation seals the undo run (typing after a click is a new undo step) and resets <see cref="StickyX"/>.
    /// </summary>
    public void SetCaret(int pos, bool extend = false)
    {
        pos = Doc.SnapToGrapheme(pos);   // SnapToGrapheme clamps internally
        if (!extend) Anchor = pos;
        Active = pos;
        _undo.SealRun();
        StickyX = float.NaN;
    }

    /// <summary>Programmatic selection (WinUI <c>Select(start, length)</c>): both ends clamped and grapheme-snapped;
    /// the caret lands at the selection end. Seals the undo run.</summary>
    public void Select(int start, int length)
    {
        int len = Doc.Length;
        if (start < 0) start = 0;
        if (start > len) start = len;
        if (length < 0) length = 0;
        long end = (long)start + length;
        if (end > len) end = len;
        Anchor = Doc.SnapToGrapheme(start);
        Active = Doc.SnapToGrapheme((int)end);
        _undo.SealRun();
        StickyX = float.NaN;
    }

    public void SelectAll()
    {
        Anchor = 0;
        Active = Doc.Length;
        _undo.SealRun();
        StickyX = float.NaN;
    }

    /// <summary>
    /// Programmatic external write (the <c>Text</c> property setter): replaces all content (newlines normalized to
    /// '\r'), CLEARS the undo history (WinUI does — a programmatic set is not a user edit to step back over), and
    /// collapses the selection to min(old caret, new length), grapheme-snapped.
    /// </summary>
    public void ResetText(ReadOnlySpan<char> text)
    {
        if (text.IndexOf('\n') >= 0)
            Doc.Reset(EditDocument.NormalizeNewlines(text));
        else
            Doc.Reset(text);
        int caret = Doc.SnapToGrapheme(Math.Min(Active, Doc.Length));
        Anchor = caret;
        Active = caret;
        _undo.Clear();
        StickyX = float.NaN;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Text input — the ONE insertion path (typing, paste, IME commit).
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Insert at the caret, replacing any selection as a single undo op. Respects <see cref="IsReadOnly"/>;
    /// flattens to the first line when <see cref="AcceptsReturn"/> is false (so pasting multi-line text into a
    /// single-line field keeps only line one, exactly like WinUI — note an empty flattened paste still replaces
    /// the selection); clamps against <see cref="MaxLength"/> at a grapheme boundary. Single-grapheme inserts at a
    /// collapsed caret push coalescable Typing ops (a typing burst undoes in one step); everything else seals.
    /// Returns true iff the document changed.
    /// </summary>
    public bool InsertText(ReadOnlySpan<char> text)
    {
        if (IsReadOnly) return false;

        if (!AcceptsReturn)
            text = FirstLine(text);
        else if (text.IndexOf('\n') >= 0)
            text = EditDocument.NormalizeNewlines(text);   // cold paste edge: one normalized string alloc

        var (selStart, selLen) = Selection;

        if (MaxLength > 0 && text.Length > 0)
        {
            int room = MaxLength - (Doc.Length - selLen);
            if (room < 0) room = 0;
            if (text.Length > room) text = text.Slice(0, GraphemeClamp(text, room));
        }

        if (text.IsEmpty && selLen == 0) return false;

        string removed = selLen > 0 ? Doc.GetText(selStart, selLen) : string.Empty;
        string inserted = text.IsEmpty ? string.Empty : new string(text);
        int anchorBefore = Anchor, activeBefore = Active;

        if (selLen > 0) Doc.Remove(selStart, selLen);
        if (!text.IsEmpty) Doc.Insert(selStart, text);

        int caret = selStart + text.Length;
        Anchor = caret;
        Active = caret;
        StickyX = float.NaN;

        bool typingRun = selLen == 0 && IsSingleGrapheme(text);
        _undo.Push(new TextUndoOp(selStart, removed, inserted, anchorBefore, activeBefore, caret, caret,
            typingRun ? TextUndoOp.KindTyping : TextUndoOp.KindOther), sealRun: !typingRun);
        return true;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Commands.
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Execute a document-order command; returns true iff caret/selection/document state changed. Up/Down/PageUp/
    /// PageDown/Copy/Cut/Paste/Commit/Cancel always return false — the CONTROL layer handles them (they need visual
    /// metrics or the clipboard seam). WinUI arrow contract: Left/Right with a selection and no Shift COLLAPSE the
    /// caret to the selection's edge WITHOUT moving it; only a collapsed caret moves by grapheme.
    /// </summary>
    public bool Apply(TextEditCommand cmd, bool extend = false)
    {
        switch (cmd)
        {
            case TextEditCommand.Left:
                if (HasSelection && !extend) return MoveCaret(Math.Min(Anchor, Active), extend: false);
                return MoveCaret(Doc.PrevGrapheme(Active), extend);
            case TextEditCommand.Right:
                if (HasSelection && !extend) return MoveCaret(Math.Max(Anchor, Active), extend: false);
                return MoveCaret(Doc.NextGrapheme(Active), extend);
            case TextEditCommand.WordLeft:
                return MoveCaret(Doc.PrevWord(Active), extend);
            case TextEditCommand.WordRight:
                return MoveCaret(Doc.NextWord(Active), extend);
            case TextEditCommand.Home:
                return MoveCaret(Doc.LineStart(Active), extend);
            case TextEditCommand.End:
                return MoveCaret(Doc.LineEnd(Active), extend);
            case TextEditCommand.DocHome:
                return MoveCaret(0, extend);
            case TextEditCommand.DocEnd:
                return MoveCaret(Doc.Length, extend);

            case TextEditCommand.Backspace:
            case TextEditCommand.Delete:
            case TextEditCommand.WordBackspace:
            case TextEditCommand.WordDelete:
                return DeleteAdjacent(cmd);

            case TextEditCommand.SelectAll:
            {
                bool changed = Anchor != 0 || Active != Doc.Length;
                SelectAll();
                return changed;
            }

            case TextEditCommand.Undo:
                return UndoOnce();
            case TextEditCommand.Redo:
                return RedoOnce();

            case TextEditCommand.InsertNewline:
                return AcceptsReturn && InsertText("\r");

            default:
                // None, Up/Down/PageUp/PageDown (need line metrics), Copy/Cut/Paste (need the clipboard seam),
                // Commit/Cancel (control semantics): not ours.
                return false;
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Clipboard-adjacent ops (the control supplies the actual clipboard via the PAL seam).
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>The text to copy, or null when nothing is selected. Copy works in read-only fields.</summary>
    public string? CopySelection()
    {
        var (start, length) = Selection;
        return length == 0 ? null : Doc.GetText(start, length);
    }

    /// <summary>Copy + remove the selection through the undo path (its own sealed undo step). Null when nothing is
    /// selected or the field is read-only (a read-only cut must not even return the text — WinUI treats it as copy
    /// via the Copy command instead).</summary>
    public string? CutSelection()
    {
        if (IsReadOnly) return null;
        var (start, length) = Selection;
        if (length == 0) return null;
        string text = Doc.GetText(start, length);
        _undo.SealRun();
        RemoveRange(start, length, TextUndoOp.KindOther, coalescable: false);
        return text;
    }

    /// <summary>Paste = seal + <see cref="InsertText"/> + seal: the paste is always its own undo step, and later
    /// typing never merges into it — even a one-character paste.</summary>
    public bool Paste(ReadOnlySpan<char> text)
    {
        _undo.SealRun();
        bool changed = InsertText(text);
        _undo.SealRun();
        return changed;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Internals.
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Caret move primitive: clamps, moves <see cref="Active"/> (and <see cref="Anchor"/> unless
    /// extending), and — only when something actually changed — seals the undo run and resets the goal column.
    /// Allocation-free.</summary>
    private bool MoveCaret(int pos, bool extend)
    {
        if (pos < 0) pos = 0;
        int len = Doc.Length;
        if (pos > len) pos = len;
        int oldAnchor = Anchor, oldActive = Active;
        Active = pos;
        if (!extend) Anchor = pos;
        if (Anchor == oldAnchor && Active == oldActive) return false;
        _undo.SealRun();
        StickyX = float.NaN;
        return true;
    }

    /// <summary>Backspace/Delete and their word variants. A selection wins over the directional range and removes
    /// as one sealed op. Plain backspace/delete remove one grapheme and coalesce into runs; the word variants are
    /// each their own undo step (RichEdit semantics).</summary>
    private bool DeleteAdjacent(TextEditCommand cmd)
    {
        if (IsReadOnly) return false;
        if (HasSelection)
        {
            var (selStart, selLen) = Selection;
            return RemoveRange(selStart, selLen, TextUndoOp.KindOther, coalescable: false);
        }

        int start, end;
        byte kind;
        bool coalesce;
        switch (cmd)
        {
            case TextEditCommand.Backspace:
                start = Doc.PrevGrapheme(Active); end = Active; kind = TextUndoOp.KindBackspace; coalesce = true;
                break;
            case TextEditCommand.Delete:
                start = Active; end = Doc.NextGrapheme(Active); kind = TextUndoOp.KindDelete; coalesce = true;
                break;
            case TextEditCommand.WordBackspace:
                start = Doc.PrevWord(Active); end = Active; kind = TextUndoOp.KindBackspace; coalesce = false;
                break;
            default:   // WordDelete
                start = Active; end = Doc.NextWord(Active); kind = TextUndoOp.KindDelete; coalesce = false;
                break;
        }
        return start != end && RemoveRange(start, end - start, kind, coalesce);
    }

    private bool RemoveRange(int start, int length, byte kind, bool coalescable)
    {
        if (length <= 0) return false;
        string removed = Doc.GetText(start, length);
        int anchorBefore = Anchor, activeBefore = Active;
        Doc.Remove(start, length);
        Anchor = start;
        Active = start;
        StickyX = float.NaN;
        _undo.Push(new TextUndoOp(start, removed, string.Empty, anchorBefore, activeBefore, start, start, kind),
            sealRun: !coalescable);
        return true;
    }

    private bool UndoOnce()
    {
        if (IsReadOnly) return false;
        if (!_undo.TryUndo(out TextUndoOp op)) return false;
        if (op.Inserted.Length > 0) Doc.Remove(op.Start, op.Inserted.Length);
        if (op.Removed.Length > 0) Doc.Insert(op.Start, op.Removed);
        Anchor = ClampPos(op.AnchorBefore);
        Active = ClampPos(op.ActiveBefore);
        StickyX = float.NaN;
        return true;
    }

    private bool RedoOnce()
    {
        if (IsReadOnly) return false;
        if (!_undo.TryRedo(out TextUndoOp op)) return false;
        if (op.Removed.Length > 0) Doc.Remove(op.Start, op.Removed.Length);
        if (op.Inserted.Length > 0) Doc.Insert(op.Start, op.Inserted);
        Anchor = ClampPos(op.AnchorAfter);
        Active = ClampPos(op.ActiveAfter);
        StickyX = float.NaN;
        return true;
    }

    private int ClampPos(int pos) => Math.Clamp(pos, 0, Doc.Length);

    /// <summary>Single-line flattening: everything up to (excluding) the first '\r' or '\n'.</summary>
    private static ReadOnlySpan<char> FirstLine(ReadOnlySpan<char> text)
    {
        int i = text.IndexOfAny('\r', '\n');
        return i < 0 ? text : text.Slice(0, i);
    }

    /// <summary>The largest grapheme-boundary length ≤ <paramref name="max"/> — a MaxLength clamp must never split
    /// a surrogate pair or cluster.</summary>
    private static int GraphemeClamp(ReadOnlySpan<char> text, int max)
    {
        if (max <= 0) return 0;
        if (max >= text.Length) return text.Length;
        int i = 0;
        while (i < text.Length)
        {
            int step = StringInfo.GetNextTextElementLength(text.Slice(i));
            if (step <= 0) step = 1;
            if (i + step > max) return i;
            i += step;
        }
        return i;
    }

    private static bool IsSingleGrapheme(ReadOnlySpan<char> text)
        => !text.IsEmpty && StringInfo.GetNextTextElementLength(text) == text.Length;
}
