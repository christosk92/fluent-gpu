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

    // ── SIP (touch keyboard) recorder: count show/hide requests and re-fire the OccludedRect callback on a test cue ──
    /// <summary>How many times <see cref="TryShowTouchKeyboard"/> has been called (the WinUI InputPane2.TryShow). The
    /// SIP-trigger gate asserts a touch-focus on an editable field requests show exactly once and a mouse focus zero.</summary>
    public int ShowCount { get; private set; }
    /// <summary>How many times <see cref="TryHideTouchKeyboard"/> has been called (InputPane2.TryHide).</summary>
    public int HideCount { get; private set; }
    /// <summary>True while the simulated panel is shown (a TryShow not yet followed by a TryHide / a Hiding cue).</summary>
    public bool TouchKeyboardShown { get; private set; }

    public event Action<RectF>? OccludedRectChanged;

    public bool TryShowTouchKeyboard() { ShowCount++; TouchKeyboardShown = true; return true; }
    public bool TryHideTouchKeyboard()
    {
        HideCount++;
        bool was = TouchKeyboardShown;
        TouchKeyboardShown = false;
        FireOccludedRect(default);   // a real InputPane raises Hiding (empty OccludedRect) when dismissed
        return was;
    }

    /// <summary>Test driver: simulate the OS <c>InputPane.Showing</c>/<c>Hiding</c> by raising the occluded-rect callback
    /// with <paramref name="dipRect"/> (CLIENT DIP; <c>default</c> = hidden). The host's reflow subscriber scrolls the
    /// focused editor's caret above it — the gate asserts the viewport offset moved to expose the caret.</summary>
    public void FireOccludedRect(in RectF dipRect)
    {
        TouchKeyboardShown = !dipRect.IsEmpty;
        OccludedRectChanged?.Invoke(dipRect);
    }

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
