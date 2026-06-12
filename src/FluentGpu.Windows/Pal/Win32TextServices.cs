using System.Runtime.InteropServices;
using FluentGpu.Foundation;
using FluentGpu.Pal;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Pal.Windows;

/// <summary>Win32 clipboard (CF_UNICODETEXT). UI-thread only; opens are retried briefly (another app may hold it).</summary>
public sealed unsafe class Win32Clipboard : IClipboard
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    public uint SequenceNumber => GetClipboardSequenceNumber();

    public void SetText(ReadOnlySpan<char> text)
    {
        if (!TryOpen()) return;
        try
        {
            EmptyClipboard();
            nuint bytes = (nuint)(text.Length + 1) * 2;
            HGLOBAL h = GlobalAlloc(GMEM_MOVEABLE, bytes);
            if (h == HGLOBAL.NULL) return;
            char* dst = (char*)GlobalLock(h);
            if (dst is null) { GlobalFree(h); return; }
            text.CopyTo(new Span<char>(dst, text.Length));
            dst[text.Length] = '\0';
            GlobalUnlock(h);
            // The system owns the handle after a successful SetClipboardData; free it only on failure.
            if (SetClipboardData(CF_UNICODETEXT, (HANDLE)(void*)h) == HANDLE.NULL) GlobalFree(h);
        }
        finally { CloseClipboard(); }
    }

    public bool TryGetText(out string text)
    {
        text = "";
        if (!TryOpen()) return false;
        try
        {
            HANDLE h = GetClipboardData(CF_UNICODETEXT);
            if (h == HANDLE.NULL) return false;
            char* src = (char*)GlobalLock((HGLOBAL)(void*)h);
            if (src is null) return false;
            text = new string(src);   // reads to the terminating NUL
            GlobalUnlock((HGLOBAL)(void*)h);
            return true;
        }
        finally { CloseClipboard(); }
    }

    private static bool TryOpen()
    {
        for (int i = 0; i < 4; i++)
        {
            if (OpenClipboard(HWND.NULL)) return true;
            Thread.Sleep(1);   // another process holds the clipboard — brief retry (user-gesture cold path)
        }
        return false;
    }
}

/// <summary>
/// Imm32 IME plumbing for one window: suppresses the system composition window, reads the composition string +
/// caret + clause attributes on WM_IME_COMPOSITION, forwards them to the registered sink, and positions the
/// candidate window at the caret (CFS_EXCLUDE). Reused buffers — the typing edge allocates only event strings.
/// </summary>
public sealed unsafe partial class Win32TextInput : IPlatformTextInput
{
    // Imm32 ABI (stable; local constants like the rest of the Win32 PAL).
    private const uint GCS_COMPSTR = 0x0008, GCS_COMPATTR = 0x0010, GCS_CURSORPOS = 0x0080, GCS_RESULTSTR = 0x0800;
    private const byte ATTR_INPUT = 0, ATTR_TARGET_CONVERTED = 1, ATTR_CONVERTED = 2, ATTR_TARGET_NOTCONVERTED = 3;
    private const uint CFS_POINT = 0x0002, CFS_EXCLUDE = 0x0080;

    [StructLayout(LayoutKind.Sequential)]
    private struct COMPOSITIONFORM { public uint dwStyle; public POINT ptCurrentPos; public RECT rcArea; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CANDIDATEFORM { public uint dwIndex; public uint dwStyle; public POINT ptCurrentPos; public RECT rcArea; }

    [LibraryImport("imm32.dll")] private static partial nint ImmGetContext(nint hwnd);
    [LibraryImport("imm32.dll")] private static partial int ImmReleaseContext(nint hwnd, nint himc);
    [LibraryImport("imm32.dll")] private static partial nint ImmAssociateContext(nint hwnd, nint himc);
    [LibraryImport("imm32.dll")] private static partial int ImmGetCompositionStringW(nint himc, uint index, void* buf, uint bufLen);
    [LibraryImport("imm32.dll")] private static partial int ImmSetCandidateWindow(nint himc, void* candidateForm);
    [LibraryImport("imm32.dll")] private static partial int ImmSetCompositionWindow(nint himc, void* compositionForm);

    private readonly nint _hwnd;
    private ITextInputSink? _sink;
    private nint _storedContext;     // the default IME context while disabled (ImmAssociateContext(NULL) parks it here)
    private bool _editable = true;
    private char[] _textBuf = new char[64];
    private byte[] _attrBuf = new byte[64];
    private ImeClause[] _clauseBuf = new ImeClause[8];
    private RectF _caretPx;

    public Win32TextInput(nint hwnd) => _hwnd = hwnd;

    public int CaretBlinkMs
    {
        get { uint t = GetCaretBlinkTime(); return t == 0 || t == uint.MaxValue ? 500 : (int)t; }
    }

    public void SetSink(ITextInputSink? sink) => _sink = sink;

    public void SetEditable(bool editable)
    {
        if (_editable == editable) return;
        _editable = editable;
        if (!editable) _storedContext = ImmAssociateContext(_hwnd, 0);
        else if (_storedContext != 0) { ImmAssociateContext(_hwnd, _storedContext); _storedContext = 0; }
    }

    public void SetCaretRectPx(in RectF rect)
    {
        _caretPx = rect;
        nint himc = ImmGetContext(_hwnd);
        if (himc == 0) return;
        var rc = new RECT { left = (int)rect.X, top = (int)rect.Y, right = (int)rect.Right, bottom = (int)rect.Bottom };
        var cand = new CANDIDATEFORM { dwIndex = 0, dwStyle = CFS_EXCLUDE, ptCurrentPos = new POINT { x = rc.left, y = rc.bottom }, rcArea = rc };
        ImmSetCandidateWindow(himc, &cand);
        var comp = new COMPOSITIONFORM { dwStyle = CFS_POINT, ptCurrentPos = new POINT { x = rc.left, y = rc.top } };
        ImmSetCompositionWindow(himc, &comp);
        ImmReleaseContext(_hwnd, himc);
    }

    // ── WndProc hooks (called by Win32Window.Handle32 on the UI thread) ──────────────────────────
    public void OnStartComposition() => _sink?.OnCompositionStart();

    public void OnEndComposition() => _sink?.OnCompositionEnd();

    /// <summary>WM_IME_COMPOSITION: lParam says which pieces are valid. A single message can both COMMIT the previous
    /// composition (GCS_RESULTSTR) and open the next one (GCS_COMPSTR) — deliver in that order, like Imm32 does.</summary>
    public void OnComposition(long lParam)
    {
        if (_sink is null) return;
        nint himc = ImmGetContext(_hwnd);
        if (himc == 0) return;
        try
        {
            if ((lParam & GCS_RESULTSTR) != 0)
            {
                int len = ReadString(himc, GCS_RESULTSTR);
                if (len >= 0) _sink.OnCompositionCommit(_textBuf.AsSpan(0, len));
            }
            if ((lParam & GCS_COMPSTR) != 0)
            {
                int len = ReadString(himc, GCS_COMPSTR);
                if (len < 0) return;
                int caret = (lParam & GCS_CURSORPOS) != 0
                    ? Math.Clamp(ImmGetCompositionStringW(himc, GCS_CURSORPOS, null, 0), 0, len)
                    : len;
                int clauses = ReadClauses(himc, len);
                _sink.OnCompositionUpdate(_textBuf.AsSpan(0, len), caret, _clauseBuf.AsSpan(0, clauses));
            }
        }
        finally { ImmReleaseContext(_hwnd, himc); }
    }

    private int ReadString(nint himc, uint index)
    {
        int bytes = ImmGetCompositionStringW(himc, index, null, 0);
        if (bytes < 0) return -1;
        int chars = bytes / 2;
        if (_textBuf.Length < chars) _textBuf = new char[Math.Max(chars, _textBuf.Length * 2)];
        if (chars > 0)
            fixed (char* p = _textBuf) ImmGetCompositionStringW(himc, index, p, (uint)bytes);
        return chars;
    }

    /// <summary>Group the per-char GCS_COMPATTR bytes into clauses (runs of equal attribute).</summary>
    private int ReadClauses(nint himc, int textLen)
    {
        if (textLen <= 0) return 0;
        int attrBytes = ImmGetCompositionStringW(himc, GCS_COMPATTR, null, 0);
        int n = Math.Min(attrBytes, textLen);
        if (n <= 0)
        {
            _clauseBuf[0] = new ImeClause(0, textLen, ImeClauseKind.Input);
            return 1;
        }
        if (_attrBuf.Length < n) _attrBuf = new byte[Math.Max(n, _attrBuf.Length * 2)];
        fixed (byte* p = _attrBuf) ImmGetCompositionStringW(himc, GCS_COMPATTR, p, (uint)n);

        int count = 0, start = 0;
        for (int i = 1; i <= n; i++)
        {
            if (i == n || _attrBuf[i] != _attrBuf[start])
            {
                if (count == _clauseBuf.Length) Array.Resize(ref _clauseBuf, _clauseBuf.Length * 2);
                _clauseBuf[count++] = new ImeClause(start, i - start, KindOf(_attrBuf[start]));
                start = i;
            }
        }
        return count;
    }

    private static ImeClauseKind KindOf(byte attr) => attr switch
    {
        ATTR_TARGET_CONVERTED => ImeClauseKind.TargetConverted,
        ATTR_CONVERTED => ImeClauseKind.Converted,
        ATTR_TARGET_NOTCONVERTED => ImeClauseKind.TargetNotConverted,
        _ => ImeClauseKind.Input,
    };
}
