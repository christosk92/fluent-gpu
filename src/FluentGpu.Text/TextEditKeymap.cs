using FluentGpu.Foundation;

namespace FluentGpu.Text;

/// <summary>
/// Editor commands in DOCUMENT terms. <see cref="TextEditCore.Apply"/> handles every document-order command;
/// the vertical/visual ones (Up/Down/Page*) and the seam-touching ones (Copy/Cut/Paste need the clipboard PAL,
/// Commit/Cancel are control semantics) intentionally fall through to the control layer.
/// </summary>
public enum TextEditCommand : byte
{
    None,
    InsertNewline,
    Backspace,
    Delete,
    WordBackspace,
    WordDelete,
    Left,
    Right,
    WordLeft,
    WordRight,
    Up,
    Down,
    Home,
    End,
    DocHome,
    DocEnd,
    PageUp,
    PageDown,
    SelectAll,
    Copy,
    Cut,
    Paste,
    Undo,
    Redo,
    Commit,
    Cancel,
}

/// <summary>A resolved key chord: the command plus whether Shift extends the selection while it executes.</summary>
public readonly record struct TextKeyBinding(TextEditCommand Command, bool Extend);

/// <summary>
/// The WinUI TextBox key map as one pure function — no state, so it is trivially testable and shared by every
/// editable control. <c>(None, false)</c> means "not ours": the key must BUBBLE, which is load-bearing for
/// Up/Down/PageUp/PageDown in single-line fields (AutoSuggestBox list navigation rides the unhandled key).
/// </summary>
public static class TextEditKeymap
{
    /// <summary>Resolve a virtual-key + modifier chord. <paramref name="multiLine"/> mirrors WinUI's
    /// <c>AcceptsReturn</c>-driven splits: Enter inserts a newline only there (Ctrl+Enter still commits), and
    /// vertical navigation exists only there.</summary>
    public static TextKeyBinding Map(int keyCode, KeyModifiers mods, bool multiLine)
    {
        // Alt is the menu/access-key layer and Win is the OS chord layer — never editor commands. (AltGr text
        // arrives as WM_CHAR through InsertText, not through this map.)
        if ((mods & (KeyModifiers.Alt | KeyModifiers.Win)) != 0) return default;

        bool shift = (mods & KeyModifiers.Shift) != 0;
        bool ctrl = (mods & KeyModifiers.Ctrl) != 0;

        switch (keyCode)
        {
            case Keys.Left: return new(ctrl ? TextEditCommand.WordLeft : TextEditCommand.Left, shift);
            case Keys.Right: return new(ctrl ? TextEditCommand.WordRight : TextEditCommand.Right, shift);
            case Keys.Up: return multiLine ? new(TextEditCommand.Up, shift) : default;
            case Keys.Down: return multiLine ? new(TextEditCommand.Down, shift) : default;
            case Keys.Home: return new(ctrl ? TextEditCommand.DocHome : TextEditCommand.Home, shift);
            case Keys.End: return new(ctrl ? TextEditCommand.DocEnd : TextEditCommand.End, shift);
            case Keys.PageUp: return multiLine ? new(TextEditCommand.PageUp, shift) : default;
            case Keys.PageDown: return multiLine ? new(TextEditCommand.PageDown, shift) : default;

            case Keys.Back: return new(ctrl ? TextEditCommand.WordBackspace : TextEditCommand.Backspace, false);
            case Keys.Delete:
                if (shift && !ctrl) return new(TextEditCommand.Cut, false);     // legacy Shift+Del (WinUI honors it)
                return new(ctrl ? TextEditCommand.WordDelete : TextEditCommand.Delete, false);
            case Keys.Insert:
                if (ctrl && !shift) return new(TextEditCommand.Copy, false);    // legacy Ctrl+Ins
                if (shift && !ctrl) return new(TextEditCommand.Paste, false);   // legacy Shift+Ins
                return default;

            case Keys.A: return ctrl && !shift ? new(TextEditCommand.SelectAll, false) : default;
            case Keys.C: return ctrl && !shift ? new(TextEditCommand.Copy, false) : default;
            case Keys.X: return ctrl && !shift ? new(TextEditCommand.Cut, false) : default;
            case Keys.V: return ctrl && !shift ? new(TextEditCommand.Paste, false) : default;
            case Keys.Z: return ctrl ? new(shift ? TextEditCommand.Redo : TextEditCommand.Undo, false) : default;
            case Keys.Y: return ctrl && !shift ? new(TextEditCommand.Redo, false) : default;

            case Keys.Enter: return new(multiLine && !ctrl ? TextEditCommand.InsertNewline : TextEditCommand.Commit, false);
            case Keys.Escape: return new(TextEditCommand.Cancel, false);

            default: return default;
        }
    }
}
