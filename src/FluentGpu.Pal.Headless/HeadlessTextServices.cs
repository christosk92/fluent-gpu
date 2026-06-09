using FluentGpu.Foundation;
using FluentGpu.Pal;

namespace FluentGpu.Pal.Headless;

/// <summary>In-memory clipboard: tests set/read it directly; the epoch bumps on every write like the OS one.</summary>
public sealed class HeadlessClipboard : IClipboard
{
    private string _text = "";
    private bool _hasText;
    public uint SequenceNumber { get; private set; }

    public void SetText(ReadOnlySpan<char> text)
    {
        _text = text.ToString();
        _hasText = true;
        SequenceNumber++;
    }

    public bool TryGetText(out string text)
    {
        text = _text;
        return _hasText;
    }

    /// <summary>Test helper: clear the clipboard (TryGetText → false), bumping the epoch.</summary>
    public void Clear() { _text = ""; _hasText = false; SequenceNumber++; }
}

/// <summary>
/// Headless IME driver: a test scripts a full composition lifecycle (<see cref="BeginComposition"/> →
/// <see cref="UpdateComposition"/>* → <see cref="Commit"/>/<see cref="Cancel"/>) and the events flow into the
/// registered sink exactly as Imm32 would deliver them. Records the editable flag + last caret rect for assertions.
/// </summary>
public sealed class HeadlessTextInput : IPlatformTextInput
{
    private ITextInputSink? _sink;
    private bool _composing;

    public bool Editable { get; private set; }
    public RectF LastCaretRectPx { get; private set; }
    public int CaretBlinkMs => 500;   // fixed + deterministic for tests

    public void SetSink(ITextInputSink? sink) => _sink = sink;
    public void SetEditable(bool editable)
    {
        Editable = editable;
        if (!editable && _composing) Cancel();   // focus left the editor mid-composition → the IME context tears down
    }
    public void SetCaretRectPx(in RectF rect) => LastCaretRectPx = rect;

    // ── test drivers ──────────────────────────────────────────────────────────────────────────────
    public void BeginComposition()
    {
        if (!Editable || _sink is null) return;
        _composing = true;
        _sink.OnCompositionStart();
    }

    public void UpdateComposition(string text, int caret, params ImeClause[] clauses)
    {
        if (!_composing || _sink is null) return;
        _sink.OnCompositionUpdate(text, caret, clauses);
    }

    public void Commit(string text)
    {
        if (!_composing || _sink is null) return;
        _sink.OnCompositionCommit(text);
        _sink.OnCompositionEnd();
        _composing = false;
    }

    public void Cancel()
    {
        if (!_composing || _sink is null) return;
        _sink.OnCompositionUpdate(ReadOnlySpan<char>.Empty, 0, ReadOnlySpan<ImeClause>.Empty);
        _sink.OnCompositionEnd();
        _composing = false;
    }
}
