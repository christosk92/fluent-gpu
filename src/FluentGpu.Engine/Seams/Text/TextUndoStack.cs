namespace FluentGpu.Text;

/// <summary>
/// One reversible edit against an <see cref="EditDocument"/>: the op replaced <see cref="Removed"/> with
/// <see cref="Inserted"/> at <see cref="Start"/>. Undo = remove <c>Inserted.Length</c> at <c>Start</c>, re-insert
/// <c>Removed</c>, restore <see cref="AnchorBefore"/>/<see cref="ActiveBefore"/>; redo is the inverse with the
/// After pair — so undo/redo restore the EXACT caret/selection the user saw, not a recomputed approximation.
/// </summary>
public readonly struct TextUndoOp
{
    /// <summary>Run kinds drive coalescing: only same-kind, position-contiguous ops merge (see <see cref="TextUndoStack.Push"/>).</summary>
    public const byte KindOther = 0, KindTyping = 1, KindBackspace = 2, KindDelete = 3;

    public readonly int Start;
    public readonly string Removed;
    public readonly string Inserted;
    public readonly int AnchorBefore;
    public readonly int ActiveBefore;
    public readonly int AnchorAfter;
    public readonly int ActiveAfter;
    public readonly byte RunKind;

    public TextUndoOp(int start, string removed, string inserted,
        int anchorBefore, int activeBefore, int anchorAfter, int activeAfter, byte runKind)
    {
        Start = start;
        Removed = removed;
        Inserted = inserted;
        AnchorBefore = anchorBefore;
        ActiveBefore = activeBefore;
        AnchorAfter = anchorAfter;
        ActiveAfter = activeAfter;
        RunKind = runKind;
    }
}

/// <summary>
/// Bounded undo/redo for the text editor (cap 512 ops, oldest dropped — a hard memory ceiling per field, like
/// RichEdit). Coalescing makes one undo step out of a typing burst: a new op merges into the open top op when the
/// kinds match AND the positions are contiguous (typing extends forward from the previous insert's end; backspace
/// walks backward, forward-delete stays in place) AND no selection was replaced AND the caller did not seal.
/// Paste, IME commit, selection-replacing edits, and any caret relocation seal the run (<see cref="SealRun"/> or
/// <c>sealRun: true</c> on <see cref="Push"/>) so they become their own undo steps — the WinUI/RichEdit feel:
/// "abc" undoes in one step, a paste in its own step.
/// </summary>
public sealed class TextUndoStack
{
    /// <summary>Maximum retained undo ops; pushing past it drops the oldest.</summary>
    public const int MaxOps = 512;

    private readonly List<TextUndoOp> _undo = new();
    private readonly List<TextUndoOp> _redo = new();
    private bool _runOpen;   // may the top undo op still coalesce?

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Observable op counts (headless checks assert coalescing — e.g. typing "abc" leaves depth 1).</summary>
    public int UndoDepth => _undo.Count;
    public int RedoDepth => _redo.Count;

    /// <summary>
    /// Record an applied edit. Clears the redo stack (a fresh edit forks history). When <paramref name="sealRun"/>
    /// is false the op may coalesce into the open top op per the kind rules above; when true it neither merges nor
    /// accepts future merges (paste/IME-commit/selection-replace semantics).
    /// </summary>
    public void Push(in TextUndoOp op, bool sealRun = false)
    {
        _redo.Clear();
        if (!sealRun && _runOpen && _undo.Count > 0 && TryCoalesce(_undo[^1], in op, out TextUndoOp merged))
        {
            _undo[^1] = merged;
            return;
        }
        if (_undo.Count >= MaxOps) _undo.RemoveAt(0);   // O(n) shift only at the cap — cold, bounded
        _undo.Add(op);
        _runOpen = !sealRun;
    }

    /// <summary>Pop the newest op for the caller to reverse; it moves to the redo stack. The surviving top can no
    /// longer coalesce (undoing then typing must not mutate history that was already stepped over).</summary>
    public bool TryUndo(out TextUndoOp op)
    {
        if (_undo.Count == 0) { op = default; return false; }
        op = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(op);
        _runOpen = false;
        return true;
    }

    /// <summary>Pop the newest undone op for the caller to re-apply; it moves back to the undo stack (sealed).</summary>
    public bool TryRedo(out TextUndoOp op)
    {
        if (_redo.Count == 0) { op = default; return false; }
        op = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(op);
        _runOpen = false;
        return true;
    }

    /// <summary>End the current coalescing run: the next push starts a fresh undo step. Called on caret moves,
    /// selection changes, focus changes, and around paste/IME commits.</summary>
    public void SealRun() => _runOpen = false;

    /// <summary>Drop all history (programmatic text reset — WinUI clears undo on a <c>Text</c> set).</summary>
    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        _runOpen = false;
    }

    private static bool TryCoalesce(in TextUndoOp prev, in TextUndoOp next, out TextUndoOp merged)
    {
        merged = default;
        if (prev.RunKind != next.RunKind) return false;
        switch (next.RunKind)
        {
            case TextUndoOp.KindTyping:
                // Forward-contiguous insert at the previous insert's end, replacing nothing.
                if (next.Removed.Length != 0) return false;
                if (prev.Start + prev.Inserted.Length != next.Start) return false;
                merged = new TextUndoOp(prev.Start, prev.Removed, prev.Inserted + next.Inserted,
                    prev.AnchorBefore, prev.ActiveBefore, next.AnchorAfter, next.ActiveAfter, prev.RunKind);
                return true;

            case TextUndoOp.KindBackspace:
                // Positions walk backward: the new removal ends exactly where the previous one started.
                if (next.Inserted.Length != 0 || prev.Inserted.Length != 0) return false;
                if (next.Start + next.Removed.Length != prev.Start) return false;
                merged = new TextUndoOp(next.Start, next.Removed + prev.Removed, string.Empty,
                    prev.AnchorBefore, prev.ActiveBefore, next.AnchorAfter, next.ActiveAfter, prev.RunKind);
                return true;

            case TextUndoOp.KindDelete:
                // Forward delete stays in place: same start, removals concatenate in document order.
                if (next.Inserted.Length != 0 || prev.Inserted.Length != 0) return false;
                if (next.Start != prev.Start) return false;
                merged = new TextUndoOp(prev.Start, prev.Removed + next.Removed, string.Empty,
                    prev.AnchorBefore, prev.ActiveBefore, next.AnchorAfter, next.ActiveAfter, prev.RunKind);
                return true;

            default:
                return false;   // KindOther never coalesces
        }
    }
}
