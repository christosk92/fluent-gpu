using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FluentGpu.Foundation;   // Point2, KeyModifiers, DropEffect
using FluentGpu.Hooks;        // InputHooks.Current
using TerraFX.Interop.Windows;
using ComCcw = FluentGpu.Text.DirectWrite.ComCcw;   // S_OK / IID_IUnknown / IID_IDropTarget (DWriteItemizer.cs)

namespace FluentGpu.Pal.Windows;

/// <summary>
/// The OLE <c>IDropTarget</c> that bridges an OS drag — files/folders (Explorer / the desktop), links and text (a
/// browser, the Spotify app) — into the engine's external-drop seam, restoring DRAG-OVER HOVER feedback (so an app can
/// light up a "Drop to Deploy"-style overlay while a file is dragged over the window) and the OS drop-effect cursor
/// ("+Copy"/"+Link"/no-drop). A data object with several formats offers every kind it has (<see cref="ReadOffer"/>).
///
/// <para><b>Why a HAND-ROLLED vtable (not <c>[GeneratedComInterface]</c>).</b> The prior source-generated IDropTarget
/// crashed on drop — the suspected cause was the by-value <c>POINTL</c> parameter under the COM source generator's
/// marshalling (an uncatchable AccessViolation). This CCW declares the exact unmanaged thunk ABI itself — <c>POINTL</c>
/// is a real 8-byte by-value struct in <c>[UnmanagedCallersOnly(CallConvMemberFunction)]</c> signatures, honored
/// identically on x64 and ARM64 — the same proven pattern as <c>DWriteItemizer.cs</c>'s CCWs. </para>
///
/// <para><b>Data handling.</b> DragEnter reads everything a target needs to decide, ONCE: the file list
/// (<c>GetData(CF_HDROP)</c> → HDROP → <c>DragQueryFileW</c>, the same enumeration the previous (working)
/// <c>WM_DROPFILES</c> path used) and the small link and text formats (<c>UniformResourceLocatorW</c>,
/// <c>text/uri-list</c>, <c>CF_UNICODETEXT</c>), so a target can refuse a payload it cannot use ("can't play .wma
/// files") or show what a drop will do ("12 songs · 2 folders") while the pointer is still hovering. DragOver reads
/// nothing. DROP reads the file list again and fills the session with it, so a source that renders <c>CF_HDROP</c>
/// only at drop (or failed to on enter) still delivers. Every thunk swallows managed exceptions and returns an HRESULT
/// — nothing crosses the COM boundary.</para>
///
/// <para><b>Threading.</b> OLE delivers the callbacks on the registering (UI) thread during the drag loop — the same
/// thread the dispatcher runs on — so the forwarded <c>InputHooks.ExternalDrag*</c> calls are synchronous and
/// single-threaded. <c>RegisterDragDrop</c> requires the thread to be STA (the gallery is <c>[STAThread]</c>).</para>
/// </summary>
internal static unsafe partial class Win32DropTarget
{
    private const ushort CF_HDROP = 15, CF_UNICODETEXT = 13;
    private const uint MK_SHIFT = 0x0004, MK_CONTROL = 0x0008;
    private const uint DROPEFFECT_NONE = 0, DROPEFFECT_COPY = 1, DROPEFFECT_MOVE = 2, DROPEFFECT_LINK = 4;
    private const int S_OK = 0, S_FALSE = 1, RPC_E_CHANGED_MODE = unchecked((int)0x80010106);

    [StructLayout(LayoutKind.Sequential)]
    internal struct PointL { public int x, y; }   // POINTL / POINT: { LONG x; LONG y } — 8 bytes by value

    // ── OLE / shell / user32 entry points (declared locally so the CCW passes as an opaque nint) ──
    [LibraryImport("ole32.dll")] private static partial int OleInitialize(nint pvReserved);
    [LibraryImport("ole32.dll")] private static partial void OleUninitialize();
    [LibraryImport("ole32.dll")] private static partial int RegisterDragDrop(nint hwnd, nint pDropTarget);
    [LibraryImport("ole32.dll")] private static partial int RevokeDragDrop(nint hwnd);
    [LibraryImport("ole32.dll")] private static partial void ReleaseStgMedium(STGMEDIUM* pmedium);
    [LibraryImport("shell32.dll", EntryPoint = "DragQueryFileW")] private static partial uint DragQueryFileW(nint hDrop, uint iFile, char* lpszFile, uint cch);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ScreenToClient(nint hWnd, PointL* lpPoint);
    [LibraryImport("user32.dll")] private static partial uint GetDpiForWindow(nint hWnd);
    [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", StringMarshalling = StringMarshalling.Utf16)] private static partial uint RegisterClipboardFormatW(string lpszFormat);
    [LibraryImport("kernel32.dll")] private static partial void* GlobalLock(nint hMem);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GlobalUnlock(nint hMem);
    [LibraryImport("kernel32.dll")] private static partial nuint GlobalSize(nint hMem);

    // Registered link formats (ids are per session, so registered once, lazily, on the first drag). CFSTR_INETURLW is the
    // one-URL form every browser and the shell offer for a link; text/uri-list (RFC 2483) carries several, with # comments.
    private static ushort _cfUrlW, _cfUriList;
    private static ushort CfUrlW => _cfUrlW != 0 ? _cfUrlW : _cfUrlW = (ushort)RegisterClipboardFormatW("UniformResourceLocatorW");
    private static ushort CfUriList => _cfUriList != 0 ? _cfUriList : _cfUriList = (ushort)RegisterClipboardFormatW("text/uri-list");

    /// <summary>OLE-init this STA thread + register a fresh CCW on <paramref name="hwnd"/>. Returns a token to pass to
    /// <see cref="Revoke"/>, or null when drag-drop is unavailable (non-STA thread, or RegisterDragDrop failed).
    /// <paramref name="effectiveScale"/> supplies the window's EFFECTIVE scale (OS DPI × app zoom) for the drop-point
    /// px→DIP conversion — drop coordinates enter the engine as layout DIP, and under app zoom the raw window DPI is
    /// the wrong divisor. Null falls back to the raw DPI (no zoom).</summary>
    internal static DropRegistration? Register(nint hwnd, Func<float>? effectiveScale = null)
    {
        if (hwnd == 0) return null;
        int oi = OleInitialize(0);
        if (oi == RPC_E_CHANGED_MODE) return null;   // thread is MTA → OLE drag-drop can't run here
        bool oleInited = oi == S_OK || oi == S_FALSE;

        // The CCW is unmanaged memory, so the managed provider rides as a GCHandle; freed in Destroy (via Revoke).
        nint scaleProvider = effectiveScale is null ? 0 : GCHandle.ToIntPtr(GCHandle.Alloc(effectiveScale));
        Win32DropTargetCcw* ccw = Win32DropTargetCcw.Create(hwnd, scaleProvider);
        int hr = RegisterDragDrop(hwnd, (nint)ccw);
        if (hr < 0)
        {
            Win32DropTargetCcw.Destroy(ccw);
            if (oleInited) OleUninitialize();
            return null;
        }
        return new DropRegistration(hwnd, (nint)ccw, oleInited);
    }

    /// <summary>Revoke the registration and free the CCW (mirror of <see cref="Register"/>).</summary>
    internal static void Revoke(DropRegistration? reg)
    {
        if (reg is not { } r) return;
        RevokeDragDrop(r.Hwnd);                       // releases OLE's ref → back to our single ref
        if (r.Ccw != 0) Win32DropTargetCcw.Destroy((Win32DropTargetCcw*)r.Ccw);
        if (r.OleInited) OleUninitialize();
    }

    /// <summary>
    /// In-process vtable round-trip — proves the hand-rolled CCW dispatches correctly through its function-pointer
    /// vtable, EXERCISING THE BY-VALUE <c>POINTL</c> ABI (the element that crashed the source-gen attempt) without a
    /// real drag. Calls QI(IID_IDropTarget) + all four IDropTarget methods with a NULL data object (the no-files fast
    /// path) and a synthetic by-value point; asserts no crash and sane effects. Returns true on success.
    /// </summary>
    internal static bool SelfTest(out string detail)
    {
        Win32DropTargetCcw* ccw = Win32DropTargetCcw.Create(0);
        try
        {
            void** vt = ccw->Vtbl;

            Guid iid = ComCcw.IID_IDropTarget;
            void* ppv = null;
            var qi = (delegate* unmanaged[MemberFunction]<Win32DropTargetCcw*, Guid*, void**, int>)vt[0];
            int hrQi = qi(ccw, &iid, &ppv);
            bool qiOk = hrQi == S_OK && ppv == ccw;

            var enter = (delegate* unmanaged[MemberFunction]<Win32DropTargetCcw*, void*, uint, PointL, uint*, int>)vt[3];
            var over  = (delegate* unmanaged[MemberFunction]<Win32DropTargetCcw*, uint, PointL, uint*, int>)vt[4];
            var leave = (delegate* unmanaged[MemberFunction]<Win32DropTargetCcw*, int>)vt[5];
            var drop  = (delegate* unmanaged[MemberFunction]<Win32DropTargetCcw*, void*, uint, PointL, uint*, int>)vt[6];

            var pt = new PointL { x = 123, y = 456 };           // the by-value POINTL under test

            uint eff = DROPEFFECT_COPY | DROPEFFECT_MOVE;
            int hrE = enter(ccw, null, 0, pt, &eff);             // null data → no files → effect NONE
            bool enterOk = hrE == S_OK && eff == DROPEFFECT_NONE;

            eff = DROPEFFECT_COPY;
            int hrO = over(ccw, 0, pt, &eff);
            int hrL = leave(ccw);

            eff = DROPEFFECT_COPY;
            int hrD = drop(ccw, null, 0, pt, &eff);              // null data → no paths → not accepted → NONE
            bool dropOk = hrD == S_OK && eff == DROPEFFECT_NONE;

            detail = $"qi={qiOk}(hr=0x{hrQi:X}) enter={enterOk} over=hr0x{hrO:X} leave=hr0x{hrL:X} drop={dropOk}";
            return qiOk && enterOk && hrO == S_OK && hrL == S_OK && dropOk;
        }
        finally { Win32DropTargetCcw.Destroy(ccw); }
    }

    [LibraryImport("shell32.dll")] private static partial int SHCreateDataObject(nint pidlFolder, uint cidl, nint apidl, nint pdtInner, Guid* riid, void** ppv);
    [LibraryImport("kernel32.dll")] private static partial nint GlobalAlloc(uint uFlags, nuint dwBytes);
    private static readonly Guid IID_IDataObject = new(0x0000010e, 0, 0, 0xC0, 0, 0, 0, 0, 0, 0, 0x46);

    /// <summary>
    /// The link and text formats end to end, without a real drag: builds REAL OLE data objects (<c>SHCreateDataObject</c>,
    /// the shell's own <c>IDataObject</c>) carrying what a browser or the Spotify app offers — plain text; a link
    /// (<c>UniformResourceLocatorW</c> + its text); a <c>text/uri-list</c> with comments and blank lines + text — reads
    /// each through <see cref="ReadOffer"/>, then sends the link object through the CCW's DragEnter and Drop thunks with
    /// capturing <c>InputHooks</c> (restored after) to prove the offer crosses the seam and a link drop commits through
    /// <c>ExternalDrop</c> with the Link effect. Returns true on success.
    /// </summary>
    internal static bool SelfTestFormats(out string detail)
    {
        var sb = new System.Text.StringBuilder();
        void* text = null, link = null, list = null, empty = null, files = null;
        var hooks = InputHooks.Current.Default;
        var (oldEnter, oldOver, oldLeave, oldDrop, oldDropFiles) =
            (hooks.ExternalDragEnter, hooks.ExternalDragOver, hooks.ExternalDragLeave, hooks.ExternalDrop, hooks.ExternalDropFiles);
        Win32DropTargetCcw* ccw = Win32DropTargetCcw.Create(0);
        try
        {
            const string Spotify = "spotify:track:4uLU6hMCjMI75M1A2tKUQC";
            const string Https = "https://open.spotify.com/album/1DFixLWuPkv3KT3TnV35m3";
            text = NewDataObject();
            SetUtf16(text, CF_UNICODETEXT, Spotify);
            link = NewDataObject();
            SetUtf16(link, CfUrlW, Https + "\r\n");
            SetUtf16(link, CF_UNICODETEXT, "Some album");
            list = NewDataObject();
            SetBytes(list, CfUriList, System.Text.Encoding.UTF8.GetBytes($"# dragged from a browser\r\n{Spotify}\r\n\r\n  {Https}  \r\n"));
            SetUtf16(list, CfUrlW, Https);          // ignored: the uri-list has links
            SetUtf16(list, CF_UNICODETEXT, Spotify + "\n" + Https);
            empty = NewDataObject();
            const string FileA = @"C:\Music\Coral Hours\Sunset Boulevard.flac", FolderB = @"D:\Downloads\Harbour Lights";
            files = NewDataObject();
            SetBytes(files, CF_HDROP, DropFiles(FileA, FolderB));
            SetUtf16(files, CF_UNICODETEXT, FileA);

            var t = ReadOffer(text);
            bool textOk = t is { Files: null, Uris: null, Text.Text: Spotify };
            sb.Append($"text={textOk}");
            var l = ReadOffer(link);
            bool linkOk = l is { Files: null, Uris.Uris: [Https], Text.Text: "Some album" };
            sb.Append($" link={linkOk}");
            var u = ReadOffer(list);
            bool listOk = u is { Files: null, Uris.Uris: [Spotify, Https], Text: not null };
            sb.Append($" uri-list={listOk}");
            bool emptyOk = ReadOffer(empty).IsEmpty;
            sb.Append($" empty={emptyOk}");
            // The file list is complete on enter (a target decides from it while hovering), and the drop's read agrees.
            var f = ReadOffer(files);
            bool filesOk = f is { Files.Paths: [FileA, FolderB], Uris: null, Text: not null }
                           && ReadDroppedPaths(files) is [FileA, FolderB];
            sb.Append($" hdrop={filesOk}");

            // Through the CCW: the offer reaches the seam whole, and a Link verdict reaches the OS as DROPEFFECT_LINK.
            ExternalDragOffer seen = default;
            bool committed = false;
            hooks.ExternalDragEnter = (_, offer, _) => { seen = offer; return DropEffect.Link; };
            hooks.ExternalDragOver = (_, _) => DropEffect.Link;
            hooks.ExternalDrop = (_, _) => committed = true;
            hooks.ExternalDropFiles = (_, _, _) => false;
            hooks.ExternalDragLeave = null;
            void** vt = ccw->Vtbl;
            var enter = (delegate* unmanaged[MemberFunction]<Win32DropTargetCcw*, void*, uint, PointL, uint*, int>)vt[3];
            var drop = (delegate* unmanaged[MemberFunction]<Win32DropTargetCcw*, void*, uint, PointL, uint*, int>)vt[6];
            var pt = new PointL { x = 10, y = 10 };
            uint eff = DROPEFFECT_COPY | DROPEFFECT_LINK;
            enter(ccw, link, 0, pt, &eff);
            bool enterOk = eff == DROPEFFECT_LINK && seen is { Uris.Uris: [Https], Text: not null, Files: null };
            eff = DROPEFFECT_COPY | DROPEFFECT_LINK;
            drop(ccw, link, 0, pt, &eff);
            bool dropOk = committed && eff == DROPEFFECT_LINK;
            sb.Append($" ccw.enter={enterOk} ccw.drop={dropOk}");

            detail = sb.ToString();
            return textOk && linkOk && listOk && emptyOk && filesOk && enterOk && dropOk;
        }
        finally
        {
            (hooks.ExternalDragEnter, hooks.ExternalDragOver, hooks.ExternalDragLeave, hooks.ExternalDrop, hooks.ExternalDropFiles) =
                (oldEnter, oldOver, oldLeave, oldDrop, oldDropFiles);
            Win32DropTargetCcw.Destroy(ccw);
            foreach (var p in new[] { (nint)text, (nint)link, (nint)list, (nint)empty, (nint)files })
                if (p != 0) ((IUnknown*)p)->Release();
        }
    }

    private static void* NewDataObject()
    {
        Guid iid = IID_IDataObject;
        void* pdo = null;
        int hr = SHCreateDataObject(0, 0, 0, 0, &iid, &pdo);
        if (hr < 0 || pdo == null) throw new InvalidOperationException($"SHCreateDataObject failed 0x{hr:X8}");
        return pdo;
    }

    /// <summary>A <c>CF_HDROP</c> medium as Explorer writes it: a 20-byte <c>DROPFILES</c> header (pFiles = 20, fWide)
    /// then each path NUL-terminated in UTF-16, the list closed by one more NUL.</summary>
    private static byte[] DropFiles(params string[] paths)
    {
        int chars = 1;
        foreach (var p in paths) chars += p.Length + 1;
        var bytes = new byte[20 + chars * 2];
        BitConverter.TryWriteBytes(bytes.AsSpan(0), 20);    // pFiles
        BitConverter.TryWriteBytes(bytes.AsSpan(16), 1);    // fWide
        int at = 20;
        foreach (var p in paths)
        {
            MemoryMarshal.AsBytes(p.AsSpan()).CopyTo(bytes.AsSpan(at));
            at += (p.Length + 1) * 2;
        }
        return bytes;
    }

    private static void SetUtf16(void* pdo, ushort cf, string s)
    {
        var bytes = new byte[(s.Length + 1) * 2];   // with the terminator, as CF_UNICODETEXT requires
        MemoryMarshal.AsBytes(s.AsSpan()).CopyTo(bytes);
        SetBytes(pdo, cf, bytes);
    }

    private static void SetBytes(void* pdo, ushort cf, byte[] bytes)
    {
        nint h = GlobalAlloc(0x0002 /* GMEM_MOVEABLE */, (nuint)bytes.Length);
        if (h == 0) throw new OutOfMemoryException();
        void* p = GlobalLock(h);
        bytes.AsSpan().CopyTo(new Span<byte>(p, bytes.Length));
        GlobalUnlock(h);
        FORMATETC fmt = HGlobalFormat(cf);
        STGMEDIUM medium = default;
        medium.tymed = (uint)TYMED.TYMED_HGLOBAL;
        medium.hGlobal = (HGLOBAL)(void*)h;
        int hr = ((IDataObject*)pdo)->SetData(&fmt, &medium, BOOL.TRUE);   // the data object owns the HGLOBAL now
        if (hr < 0) throw new InvalidOperationException($"IDataObject.SetData(cf {cf}) failed 0x{hr:X8}");
    }

    // ── helpers shared by the CCW thunks ───────────────────────────────────────────────────────────────────────────
    private static FORMATETC HGlobalFormat(ushort cf) => new()
    {
        cfFormat = cf,
        ptd = null,
        dwAspect = (uint)DVASPECT.DVASPECT_CONTENT,
        lindex = -1,
        tymed = (uint)TYMED.TYMED_HGLOBAL,
    };

    private static FORMATETC HdropFormat() => HGlobalFormat(CF_HDROP);

    /// <summary>True iff the data object offers a file list — a cheap format probe (NO data transfer).</summary>
    internal static bool OffersFiles(void* pDataObj)
    {
        if (pDataObj == null) return false;
        FORMATETC fmt = HdropFormat();
        return ((IDataObject*)pDataObj)->QueryGetData(&fmt) == S_OK;
    }

    /// <summary>Everything a drag carries, read ONCE on DragEnter (the engine's payload contract: parsed on enter, never
    /// per move). The file list, the links and the text are read in full: a target must see them in
    /// <c>OnEnter</c>/<c>CanAccept</c> to decide (audio files or a <c>.docx</c>, a <c>spotify:</c> link or not). A source
    /// that offers files but hands over no readable path on enter is still offered as Files, with an empty list that the
    /// drop's own read fills. Links come from <c>text/uri-list</c> when it has any, else <c>UniformResourceLocatorW</c>.</summary>
    internal static ExternalDragOffer ReadOffer(void* pDataObj)
    {
        if (pDataObj == null) return default;
        var files = OffersFiles(pDataObj) ? new FileDropData(ReadDroppedPaths(pDataObj)) : null;
        string[]? uris = null;
        if (ReadHGlobal(pDataObj, CfUriList) is { } list) uris = ParseUriList(DecodeUriList(list));
        if (uris is null or { Length: 0 } && ReadUtf16(pDataObj, CfUrlW) is { } url)
        {
            url = FirstLine(url);
            uris = url.Length == 0 ? null : [url];
        }
        string? text = ReadUtf16(pDataObj, CF_UNICODETEXT);
        return new ExternalDragOffer(
            files,
            uris is { Length: > 0 } ? new UriDropData(uris) : null,
            string.IsNullOrEmpty(text) ? null : new TextDropData(text));
    }

    /// <summary>One HGLOBAL format's bytes (null when the source does not offer it as an HGLOBAL).</summary>
    private static byte[]? ReadHGlobal(void* pDataObj, ushort cf)
    {
        if (cf == 0) return null;
        var data = (IDataObject*)pDataObj;
        FORMATETC fmt = HGlobalFormat(cf);
        if (data->QueryGetData(&fmt) != S_OK) return null;
        STGMEDIUM medium;
        if (data->GetData(&fmt, &medium) < 0) return null;
        try
        {
            nint h = (nint)(void*)medium.hGlobal;
            if (h == 0) return null;
            nuint size = GlobalSize(h);
            void* p = GlobalLock(h);
            if (p == null) return null;
            try { return new ReadOnlySpan<byte>(p, (int)Math.Min(size, (nuint)int.MaxValue)).ToArray(); }
            finally { GlobalUnlock(h); }
        }
        finally { ReleaseStgMedium(&medium); }
    }

    /// <summary>A UTF-16 HGLOBAL format up to its terminator (the HGLOBAL may be larger than the string).</summary>
    private static string? ReadUtf16(void* pDataObj, ushort cf)
    {
        if (ReadHGlobal(pDataObj, cf) is not { } bytes) return null;
        var chars = MemoryMarshal.Cast<byte, char>(bytes.AsSpan(0, bytes.Length & ~1));
        int end = chars.IndexOf('\0');
        return new string(end < 0 ? chars : chars[..end]);
    }

    /// <summary><c>text/uri-list</c> has no fixed encoding on Windows: RFC 2483 says ASCII/UTF-8, but some sources write
    /// UTF-16. A zero in the second byte (the high byte of an ASCII first character) means UTF-16LE.</summary>
    private static string DecodeUriList(byte[] bytes)
    {
        string s = bytes.Length >= 2 && bytes[1] == 0
            ? new string(MemoryMarshal.Cast<byte, char>(bytes.AsSpan(0, bytes.Length & ~1)))
            : System.Text.Encoding.UTF8.GetString(bytes);
        int end = s.IndexOf('\0');
        return end < 0 ? s : s[..end];
    }

    /// <summary>RFC 2483: one URI per line (CRLF, LF tolerated), lines starting with <c>#</c> are comments, blank lines
    /// skipped, each trimmed.</summary>
    internal static string[] ParseUriList(string list)
    {
        var lines = list.Split('\n');
        var uris = new string[lines.Length];
        int n = 0;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            uris[n++] = line;
        }
        if (n != uris.Length) Array.Resize(ref uris, n);
        return uris;
    }

    private static string FirstLine(string s)
    {
        int nl = s.AsSpan().IndexOfAny('\r', '\n');
        return (nl < 0 ? s : s[..nl]).Trim();
    }

    /// <summary>Pull the dragged absolute paths out of the data object's CF_HDROP medium (read on enter and again at drop).</summary>
    internal static string[] ReadDroppedPaths(void* pDataObj)
    {
        if (pDataObj == null) return Array.Empty<string>();
        var data = (IDataObject*)pDataObj;
        FORMATETC fmt = HdropFormat();
        STGMEDIUM medium;
        if (data->GetData(&fmt, &medium) < 0) return Array.Empty<string>();
        try
        {
            nint hDrop = (nint)(void*)medium.hGlobal;
            if (hDrop == 0) return Array.Empty<string>();
            uint count = DragQueryFileW(hDrop, 0xFFFFFFFF, null, 0);
            if (count == 0) return Array.Empty<string>();
            var paths = new string[count];
            int filled = 0;
            for (uint i = 0; i < count; i++)
            {
                uint need = DragQueryFileW(hDrop, i, null, 0);
                if (need == 0) continue;
                var arr = new char[need + 1];
                fixed (char* p = arr)
                {
                    uint got = DragQueryFileW(hDrop, i, p, need + 1);
                    if (got == 0) continue;
                    paths[filled++] = new string(p, 0, (int)got);
                }
            }
            if (filled != paths.Length) Array.Resize(ref paths, filled);
            return paths;
        }
        finally { ReleaseStgMedium(&medium); }
    }

    /// <summary>OLE screen POINTL (by-value) → window-DIP point, via ScreenToClient + the window's EFFECTIVE scale
    /// (OS DPI × app zoom, supplied by the owning platform through the CCW's scale-provider GCHandle). Drop points
    /// enter the engine as layout DIP — under app zoom the raw <c>GetDpiForWindow</c> read would land drops at the
    /// wrong place — so the raw DPI is only the no-provider fallback (SelfTest, a provider-less registration).</summary>
    internal static Point2 ToDip(nint hwnd, PointL pt, nint scaleProvider)
    {
        ScreenToClient(hwnd, &pt);
        float s = 0f;
        if (scaleProvider != 0 && GCHandle.FromIntPtr(scaleProvider).Target is Func<float> f) s = f();
        if (s <= 0f)
        {
            uint dpi = GetDpiForWindow(hwnd);
            s = dpi == 0 ? 1f : dpi / 96f;
        }
        return new Point2(pt.x / s, pt.y / s);
    }

    internal static KeyModifiers Mods(uint grfKeyState)
    {
        KeyModifiers m = KeyModifiers.None;
        if ((grfKeyState & MK_CONTROL) != 0) m |= KeyModifiers.Ctrl;
        if ((grfKeyState & MK_SHIFT) != 0) m |= KeyModifiers.Shift;
        return m;
    }

    /// <summary>Map the engine effect to a DROPEFFECT, intersected with what the source allows (proper drop etiquette).</summary>
    internal static uint MapEffect(DropEffect want, uint allowed)
    {
        uint w = want switch
        {
            DropEffect.Copy => DROPEFFECT_COPY,
            DropEffect.Move => DROPEFFECT_MOVE,
            DropEffect.Link => DROPEFFECT_LINK,
            _ => DROPEFFECT_NONE,
        };
        uint hit = w & allowed;
        return hit != 0 ? hit : (w == DROPEFFECT_NONE ? DROPEFFECT_NONE : (allowed & DROPEFFECT_COPY));
    }
}

/// <summary>A live OS-drop registration token (the CCW + its OLE init state) the window holds for its lifetime.</summary>
internal readonly record struct DropRegistration(nint Hwnd, nint Ccw, bool OleInited);

/// <summary>The hand-rolled <c>IDropTarget</c> CCW (vtable + refcount + owner HWND) — modeled on
/// <c>DWriteItemizer.cs</c>'s CCWs. The thunks forward to the engine's <c>InputHooks.ExternalDrag*</c> seam.</summary>
internal unsafe struct Win32DropTargetCcw
{
    public void** Vtbl;     // MUST be first (COM "this" vptr)
    public int Rc;
    public nint Hwnd;       // owner window — for ScreenToClient + the DPI scale
    public nint ScaleProvider;   // GCHandle→Func<float>: the EFFECTIVE scale (DPI × zoom); 0 = raw-DPI fallback

    private static readonly void** _vtbl = Build();

    private static void** Build()
    {
        void** v = (void**)NativeMemory.Alloc(7, (nuint)sizeof(void*));
        v[0] = (delegate* unmanaged[MemberFunction]<Win32DropTargetCcw*, Guid*, void**, int>)&QueryInterface;
        v[1] = (delegate* unmanaged[MemberFunction]<Win32DropTargetCcw*, uint>)&AddRef;
        v[2] = (delegate* unmanaged[MemberFunction]<Win32DropTargetCcw*, uint>)&Release;
        v[3] = (delegate* unmanaged[MemberFunction]<Win32DropTargetCcw*, void*, uint, Win32DropTarget.PointL, uint*, int>)&DragEnter;
        v[4] = (delegate* unmanaged[MemberFunction]<Win32DropTargetCcw*, uint, Win32DropTarget.PointL, uint*, int>)&DragOver;
        v[5] = (delegate* unmanaged[MemberFunction]<Win32DropTargetCcw*, int>)&DragLeave;
        v[6] = (delegate* unmanaged[MemberFunction]<Win32DropTargetCcw*, void*, uint, Win32DropTarget.PointL, uint*, int>)&Drop;
        return v;
    }

    public static Win32DropTargetCcw* Create(nint hwnd, nint scaleProvider = 0)
    {
        var p = (Win32DropTargetCcw*)NativeMemory.Alloc((nuint)sizeof(Win32DropTargetCcw));
        p->Vtbl = _vtbl; p->Rc = 1; p->Hwnd = hwnd; p->ScaleProvider = scaleProvider;
        return p;
    }

    public static void Destroy(Win32DropTargetCcw* p)
    {
        if (p->ScaleProvider != 0) GCHandle.FromIntPtr(p->ScaleProvider).Free();
        NativeMemory.Free(p);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static int QueryInterface(Win32DropTargetCcw* self, Guid* riid, void** ppv)
    {
        if (ppv == null) return unchecked((int)0x80004003);   // E_POINTER
        if (*riid == ComCcw.IID_IUnknown || *riid == ComCcw.IID_IDropTarget)
        { Interlocked.Increment(ref self->Rc); *ppv = self; return ComCcw.S_OK; }
        *ppv = null; return unchecked((int)0x80004002);       // E_NOINTERFACE
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static uint AddRef(Win32DropTargetCcw* self) => (uint)Interlocked.Increment(ref self->Rc);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static uint Release(Win32DropTargetCcw* self) => (uint)Interlocked.Decrement(ref self->Rc);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static int DragEnter(Win32DropTargetCcw* self, void* pDataObj, uint grfKeyState, Win32DropTarget.PointL pt, uint* pdwEffect)
    {
        try
        {
            // Read once, here: the file list, links and text in full (the drop re-reads the file list).
            var offer = Win32DropTarget.ReadOffer(pDataObj);
            if (offer.IsEmpty) { if (pdwEffect != null) *pdwEffect = 0; return ComCcw.S_OK; }
            var fn = InputHooks.Current.Default.ExternalDragEnter;
            DropEffect want = fn?.Invoke(Win32DropTarget.ToDip(self->Hwnd, pt, self->ScaleProvider), offer, Win32DropTarget.Mods(grfKeyState)) ?? DropEffect.None;
            if (pdwEffect != null) *pdwEffect = Win32DropTarget.MapEffect(want, *pdwEffect);
        }
        catch { if (pdwEffect != null) *pdwEffect = 0; }
        return ComCcw.S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static int DragOver(Win32DropTargetCcw* self, uint grfKeyState, Win32DropTarget.PointL pt, uint* pdwEffect)
    {
        try
        {
            var fn = InputHooks.Current.Default.ExternalDragOver;
            DropEffect want = fn?.Invoke(Win32DropTarget.ToDip(self->Hwnd, pt, self->ScaleProvider), Win32DropTarget.Mods(grfKeyState)) ?? DropEffect.None;
            if (pdwEffect != null) *pdwEffect = Win32DropTarget.MapEffect(want, *pdwEffect);
        }
        catch { if (pdwEffect != null) *pdwEffect = 0; }
        return ComCcw.S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static int DragLeave(Win32DropTargetCcw* self)
    {
        try { InputHooks.Current.Default.ExternalDragLeave?.Invoke(); }
        catch { /* swallow across the COM boundary */ }
        return ComCcw.S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static int Drop(Win32DropTargetCcw* self, void* pDataObj, uint grfKeyState, Win32DropTarget.PointL pt, uint* pdwEffect)
    {
        try
        {
            var dip = Win32DropTarget.ToDip(self->Hwnd, pt, self->ScaleProvider);
            var mods = Win32DropTarget.Mods(grfKeyState);
            var hooks = InputHooks.Current.Default;
            bool accepted;
            DropEffect effect = DropEffect.Copy;
            string[] paths = Win32DropTarget.ReadDroppedPaths(pDataObj);   // the once-per-drop file read
            if (paths.Length > 0)
                accepted = hooks.ExternalDropFiles?.Invoke(dip, paths, mods) ?? false;
            else if (Win32DropTarget.OffersFiles(pDataObj))
            {
                accepted = false;                                   // files offered but none readable: no drop, as before
                hooks.ExternalDragLeave?.Invoke();                  // ...and end the session OLE will not DragLeave
            }
            else
            {
                // A link or text drop: its payloads were read on enter and live in the session; commit them. The effect
                // is the session's own (Link over a target that took a link), read before the commit ends the session.
                effect = hooks.ExternalDragOver?.Invoke(dip, mods) ?? DropEffect.None;
                accepted = effect != DropEffect.None && (hooks.ExternalDrop?.Invoke(dip, mods) ?? false);
                if (!accepted) hooks.ExternalDragLeave?.Invoke();   // OLE sends no DragLeave after Drop: end the session
            }
            if (pdwEffect != null) *pdwEffect = accepted ? Win32DropTarget.MapEffect(effect, *pdwEffect) : 0;
        }
        catch { if (pdwEffect != null) *pdwEffect = 0; }
        return ComCcw.S_OK;
    }
}
