using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Text.DirectWrite;

/// <summary>
/// DirectWrite itemizer (text.md §4.4): segments text into BiDi × script runs and computes UAX #14 line-break
/// opportunities via <c>IDWriteTextAnalyzer</c>. DirectWrite calls back into the hand-authored callee CCWs
/// <see cref="TextAnalysisSourceCcw"/> (supplies the text) and <see cref="TextAnalysisSinkCcw"/> (collects the runs).
/// COM bound via TerraFX raw pointers; the CCW vtables mirror the ComputeSharp PixelShaderEffect pattern.
/// Single-thread-confined: <see cref="Itemize"/> calls AnalyzeBidi/Script/LineBreakpoints serially on the caller thread.
/// </summary>
public sealed unsafe class DWriteItemizer : ITextItemizer, IDisposable
{
    private IDWriteFactory* _dw;
    private IDWriteTextAnalyzer* _analyzer;
    private TextAnalysisSourceCcw* _src;
    private TextAnalysisSinkCcw* _sink;
    private readonly ItemizeResults _results = new();
    private GCHandle _resultsHandle;

    public DWriteItemizer()
    {
        IDWriteFactory* f;
        Check(DWriteCreateFactory(DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_SHARED, __uuidof<IDWriteFactory>(), (IUnknown**)&f), "DWriteCreateFactory");
        _dw = f;
        IDWriteTextAnalyzer* a;
        Check(_dw->CreateTextAnalyzer(&a), "CreateTextAnalyzer");
        _analyzer = a;
        _resultsHandle = GCHandle.Alloc(_results);
        _src = TextAnalysisSourceCcw.Create();
        _sink = TextAnalysisSinkCcw.Create(_resultsHandle);
    }

    public void Itemize(ReadOnlySpan<char> text, List<ItemRun> runs, List<BreakOpp> breaks)
    {
        runs.Clear(); breaks.Clear();
        int n = text.Length;
        if (n == 0) return;
        _results.Ensure(n);

        fixed (char* p = text)
        {
            _src->Text = p; _src->Len = (uint)n;
            var srcI = (IDWriteTextAnalysisSource*)_src;
            var sinkI = (IDWriteTextAnalysisSink*)_sink;
            // BiDi + script + line-break analysis over the whole paragraph. The sink writes per-position into _results.
            Check(_analyzer->AnalyzeBidi(srcI, 0, (uint)n, sinkI), "AnalyzeBidi");
            Check(_analyzer->AnalyzeScript(srcI, 0, (uint)n, sinkI), "AnalyzeScript");
            Check(_analyzer->AnalyzeLineBreakpoints(srcI, 0, (uint)n, sinkI), "AnalyzeLineBreakpoints");
            _src->Text = null; _src->Len = 0;
        }

        for (int i = 0; i < n; i++)
            breaks.Add(new BreakOpp(_results.BrkBefore[i], _results.BrkAfter[i], _results.Ws[i]));

        // Merge per-position BiDi × script into maximal runs.
        int start = 0;
        for (int i = 1; i <= n; i++)
        {
            bool boundary = i == n
                || _results.Bidi[i] != _results.Bidi[start]
                || _results.Script[i] != _results.Script[start]
                || _results.Shapes[i] != _results.Shapes[start];
            if (boundary)
            {
                runs.Add(new ItemRun(start, i - start, _results.Bidi[start], _results.Script[start], _results.Shapes[start]));
                start = i;
            }
        }
    }

    /// <summary>Smoke test on the real DirectWrite analyzer (Windows only): itemize a mixed LTR/RTL/CJK string and print
    /// the runs + break opportunities. Proves the CCW round-trip works without an access violation.</summary>
    public static void SelfTest()
    {
        using var it = new DWriteItemizer();
        var runs = new List<ItemRun>();
        var brk = new List<BreakOpp>();
        string s = "Hello עברית 123 你好 world";   // Latin + Hebrew(RTL) + digits + CJK
        it.Itemize(s.AsSpan(), runs, brk);
        Console.WriteLine($"[itemize] \"{s}\" -> {runs.Count} runs, {brk.Count} break-opps");
        foreach (var r in runs)
            Console.WriteLine($"  run @{r.Start}+{r.Length} bidi={r.BidiLevel} rtl={r.IsRightToLeft} script={r.ScriptId} shapes={r.ScriptShapes}");
        for (int i = 0; i < s.Length; i++)
            if (brk[i].IsWhitespace || brk[i].BreakBefore == BreakOpp.CanBreak)
                Console.WriteLine($"  brk @{i} '{(s[i] == ' ' ? "SP" : s[i].ToString())}' before={brk[i].BreakBefore} after={brk[i].BreakAfter} ws={brk[i].IsWhitespace}");
    }

    private static void Check(HRESULT hr, string what)
    {
        if ((int)hr < 0) throw new InvalidOperationException($"{what} failed: 0x{(uint)hr:X8}");
    }

    public void Dispose()
    {
        if (_sink != null) { TextAnalysisSinkCcw.Destroy(_sink); _sink = null; }
        if (_src != null) { TextAnalysisSourceCcw.Destroy(_src); _src = null; }
        if (_resultsHandle.IsAllocated) _resultsHandle.Free();
        if (_analyzer != null) { _analyzer->Release(); _analyzer = null; }
        if (_dw != null) { _dw->Release(); _dw = null; }
    }
}

/// <summary>Mutable per-position itemization output, recovered by the sink CCW via a GCHandle. Buffers are grown and reused.</summary>
internal sealed class ItemizeResults
{
    public int Len;
    public byte[] Bidi = [];
    public ushort[] Script = [];
    public byte[] Shapes = [];
    public byte[] BrkBefore = [];
    public byte[] BrkAfter = [];
    public bool[] Ws = [];

    public void Ensure(int n)
    {
        if (Bidi.Length < n)
        {
            Bidi = new byte[n]; Script = new ushort[n]; Shapes = new byte[n];
            BrkBefore = new byte[n]; BrkAfter = new byte[n]; Ws = new bool[n];
        }
        Len = n;
        Array.Clear(Bidi, 0, n); Array.Clear(Script, 0, n); Array.Clear(Shapes, 0, n);
        Array.Clear(BrkBefore, 0, n); Array.Clear(BrkAfter, 0, n); Array.Clear(Ws, 0, n);
    }
}

// ── Callee CCWs (DirectWrite calls back into these during Analyze*). Hand-authored static vtables of
//    [UnmanagedCallersOnly(CallConvMemberFunction)] thunks; first field MUST be the vtable pointer. ───────────────────

internal static unsafe class ComCcw
{
    public const int S_OK = 0;
    public const int E_POINTER = unchecked((int)0x80004003);
    public const int E_NOINTERFACE = unchecked((int)0x80004002);
    public const int E_FAIL = unchecked((int)0x80004005);
    public static readonly Guid IID_IUnknown = new(0x00000000, 0x0000, 0x0000, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46);
    public static readonly Guid IID_IDWriteTextAnalysisSource = new(0x688E1A58, 0x5094, 0x47C8, 0xAD, 0xC8, 0xFB, 0xCE, 0xA6, 0x0A, 0xE9, 0x2B);
    public static readonly Guid IID_IDWriteTextAnalysisSink = new(0x5810CD44, 0x0CA0, 0x4701, 0xB3, 0xFA, 0xBE, 0xC5, 0x18, 0x2A, 0xE4, 0xF6);
}

/// <summary>CCW implementing <c>IDWriteTextAnalysisSource</c> — supplies the (pinned) source text to DirectWrite.</summary>
internal unsafe struct TextAnalysisSourceCcw
{
    public void** Vtbl;     // MUST be the first field (COM "this" vptr)
    public int Rc;
    public char* Text;
    public uint Len;

    private static readonly void** _vtbl = Build();

    private static void** Build()
    {
        void** v = (void**)NativeMemory.Alloc(8, (nuint)sizeof(void*));
        v[0] = (delegate* unmanaged[MemberFunction]<TextAnalysisSourceCcw*, Guid*, void**, int>)&QueryInterface;
        v[1] = (delegate* unmanaged[MemberFunction]<TextAnalysisSourceCcw*, uint>)&AddRef;
        v[2] = (delegate* unmanaged[MemberFunction]<TextAnalysisSourceCcw*, uint>)&Release;
        v[3] = (delegate* unmanaged[MemberFunction]<TextAnalysisSourceCcw*, uint, char**, uint*, int>)&GetTextAtPosition;
        v[4] = (delegate* unmanaged[MemberFunction]<TextAnalysisSourceCcw*, uint, char**, uint*, int>)&GetTextBeforePosition;
        v[5] = (delegate* unmanaged[MemberFunction]<TextAnalysisSourceCcw*, DWRITE_READING_DIRECTION>)&GetParagraphReadingDirection;
        v[6] = (delegate* unmanaged[MemberFunction]<TextAnalysisSourceCcw*, uint, uint*, char**, int>)&GetLocaleName;
        v[7] = (delegate* unmanaged[MemberFunction]<TextAnalysisSourceCcw*, uint, uint*, IDWriteNumberSubstitution**, int>)&GetNumberSubstitution;
        return v;
    }

    public static TextAnalysisSourceCcw* Create()
    {
        var p = (TextAnalysisSourceCcw*)NativeMemory.Alloc((nuint)sizeof(TextAnalysisSourceCcw));
        p->Vtbl = _vtbl; p->Rc = 1; p->Text = null; p->Len = 0;
        return p;
    }

    public static void Destroy(TextAnalysisSourceCcw* p) => NativeMemory.Free(p);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static int QueryInterface(TextAnalysisSourceCcw* self, Guid* riid, void** ppv)
    {
        if (ppv == null) return ComCcw.E_POINTER;
        if (*riid == ComCcw.IID_IUnknown || *riid == ComCcw.IID_IDWriteTextAnalysisSource)
        { Interlocked.Increment(ref self->Rc); *ppv = self; return ComCcw.S_OK; }
        *ppv = null; return ComCcw.E_NOINTERFACE;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static uint AddRef(TextAnalysisSourceCcw* self) => (uint)Interlocked.Increment(ref self->Rc);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static uint Release(TextAnalysisSourceCcw* self) => (uint)Interlocked.Decrement(ref self->Rc);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static int GetTextAtPosition(TextAnalysisSourceCcw* self, uint pos, char** outText, uint* outLen)
    {
        if (pos >= self->Len) { *outText = null; *outLen = 0; }
        else { *outText = self->Text + pos; *outLen = self->Len - pos; }
        return ComCcw.S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static int GetTextBeforePosition(TextAnalysisSourceCcw* self, uint pos, char** outText, uint* outLen)
    {
        if (pos == 0 || pos > self->Len) { *outText = null; *outLen = 0; }
        else { *outText = self->Text; *outLen = pos; }
        return ComCcw.S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static DWRITE_READING_DIRECTION GetParagraphReadingDirection(TextAnalysisSourceCcw* self)
        => DWRITE_READING_DIRECTION.DWRITE_READING_DIRECTION_LEFT_TO_RIGHT;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static int GetLocaleName(TextAnalysisSourceCcw* self, uint pos, uint* outLen, char** outLocale)
    { *outLocale = null; *outLen = pos >= self->Len ? 0 : self->Len - pos; return ComCcw.S_OK; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static int GetNumberSubstitution(TextAnalysisSourceCcw* self, uint pos, uint* outLen, IDWriteNumberSubstitution** outNs)
    { *outNs = null; *outLen = pos >= self->Len ? 0 : self->Len - pos; return ComCcw.S_OK; }
}

/// <summary>CCW implementing <c>IDWriteTextAnalysisSink</c> — collects BiDi levels, script runs, and line-break opportunities
/// per position into the GCHandle-recovered <see cref="ItemizeResults"/>.</summary>
internal unsafe struct TextAnalysisSinkCcw
{
    public void** Vtbl;     // MUST be the first field
    public int Rc;
    public GCHandle Results;

    private static readonly void** _vtbl = Build();

    private static void** Build()
    {
        void** v = (void**)NativeMemory.Alloc(7, (nuint)sizeof(void*));
        v[0] = (delegate* unmanaged[MemberFunction]<TextAnalysisSinkCcw*, Guid*, void**, int>)&QueryInterface;
        v[1] = (delegate* unmanaged[MemberFunction]<TextAnalysisSinkCcw*, uint>)&AddRef;
        v[2] = (delegate* unmanaged[MemberFunction]<TextAnalysisSinkCcw*, uint>)&Release;
        v[3] = (delegate* unmanaged[MemberFunction]<TextAnalysisSinkCcw*, uint, uint, DWRITE_SCRIPT_ANALYSIS*, int>)&SetScriptAnalysis;
        v[4] = (delegate* unmanaged[MemberFunction]<TextAnalysisSinkCcw*, uint, uint, DWRITE_LINE_BREAKPOINT*, int>)&SetLineBreakpoints;
        v[5] = (delegate* unmanaged[MemberFunction]<TextAnalysisSinkCcw*, uint, uint, byte, byte, int>)&SetBidiLevel;
        v[6] = (delegate* unmanaged[MemberFunction]<TextAnalysisSinkCcw*, uint, uint, IDWriteNumberSubstitution*, int>)&SetNumberSubstitution;
        return v;
    }

    public static TextAnalysisSinkCcw* Create(GCHandle results)
    {
        var p = (TextAnalysisSinkCcw*)NativeMemory.Alloc((nuint)sizeof(TextAnalysisSinkCcw));
        p->Vtbl = _vtbl; p->Rc = 1; p->Results = results;
        return p;
    }

    public static void Destroy(TextAnalysisSinkCcw* p) => NativeMemory.Free(p);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static int QueryInterface(TextAnalysisSinkCcw* self, Guid* riid, void** ppv)
    {
        if (ppv == null) return ComCcw.E_POINTER;
        if (*riid == ComCcw.IID_IUnknown || *riid == ComCcw.IID_IDWriteTextAnalysisSink)
        { Interlocked.Increment(ref self->Rc); *ppv = self; return ComCcw.S_OK; }
        *ppv = null; return ComCcw.E_NOINTERFACE;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static uint AddRef(TextAnalysisSinkCcw* self) => (uint)Interlocked.Increment(ref self->Rc);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static uint Release(TextAnalysisSinkCcw* self) => (uint)Interlocked.Decrement(ref self->Rc);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static int SetScriptAnalysis(TextAnalysisSinkCcw* self, uint pos, uint len, DWRITE_SCRIPT_ANALYSIS* sa)
    {
        try
        {
            var r = Unsafe.As<ItemizeResults>(self->Results.Target!);
            ushort script = sa->script; byte shapes = (byte)sa->shapes;
            for (uint i = pos; i < pos + len && i < r.Len; i++) { r.Script[i] = script; r.Shapes[i] = shapes; }
            return ComCcw.S_OK;
        }
        catch { return ComCcw.E_FAIL; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static int SetLineBreakpoints(TextAnalysisSinkCcw* self, uint pos, uint len, DWRITE_LINE_BREAKPOINT* bp)
    {
        try
        {
            var r = Unsafe.As<ItemizeResults>(self->Results.Target!);
            for (uint i = 0; i < len && pos + i < r.Len; i++)
            {
                var b = bp[i];
                r.BrkBefore[pos + i] = b.breakConditionBefore;
                r.BrkAfter[pos + i] = b.breakConditionAfter;
                r.Ws[pos + i] = b.isWhitespace != 0;
            }
            return ComCcw.S_OK;
        }
        catch { return ComCcw.E_FAIL; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static int SetBidiLevel(TextAnalysisSinkCcw* self, uint pos, uint len, byte explicitLevel, byte resolvedLevel)
    {
        try
        {
            var r = Unsafe.As<ItemizeResults>(self->Results.Target!);
            for (uint i = pos; i < pos + len && i < r.Len; i++) r.Bidi[i] = resolvedLevel;
            return ComCcw.S_OK;
        }
        catch { return ComCcw.E_FAIL; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static int SetNumberSubstitution(TextAnalysisSinkCcw* self, uint pos, uint len, IDWriteNumberSubstitution* ns)
        => ComCcw.S_OK;
}
