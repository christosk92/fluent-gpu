namespace FluentGpu.Pal;

/// <summary>
/// The system-clipboard seam (plain Unicode text, the TextBox cut/copy/paste contract). UI-thread only.
/// <see cref="SequenceNumber"/> is the OS clipboard epoch (Win32 GetClipboardSequenceNumber) — context menus gate
/// their Paste item on it changing instead of polling content (the ISystemColors versioned-external-store idiom).
/// </summary>
public interface IClipboard
{
    /// <summary>Replace the clipboard content with plain Unicode text.</summary>
    void SetText(ReadOnlySpan<char> text);

    /// <summary>Read the clipboard as plain Unicode text. False when empty or not text-convertible.</summary>
    bool TryGetText(out string text);

    /// <summary>The OS clipboard change epoch — bumps on every clipboard write (by anyone).</summary>
    uint SequenceNumber { get; }
}
