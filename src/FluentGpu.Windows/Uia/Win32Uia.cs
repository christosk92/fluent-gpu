using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace FluentGpu.Pal.Windows;

// ── Minimal UI Automation: a server-side root provider for the window + a screen-reader ANNOUNCER ─────────────────────
// The engine's full UIA tree (design/subsystems/input-a11y.md §11) is future work; this adds JUST enough to give the app a
// real "live region": (a) a server-side IRawElementProviderSimple served for the window via WM_GETOBJECT (UiaRootObjectId),
// linked to the HWND host so a UIA client sees the window as an element; and (b) UiaRaiseNotificationEvent so a screen
// reader SPEAKS status changes ("Copied", "Couldn't sign in") without the user hunting — the login takeover's announcements.
//
// Hand-vtable CCW (no ComWrappers — matches the codebase's COM discipline), modeled on Win32DropTarget. The provider serves
// VT_EMPTY for every property (it owns no custom tree — it exists only to host notifications). Gated on
// UiaClientsAreListening so it costs nothing when no assistive tech is running.
internal static unsafe partial class Win32Uia
{
    internal const uint WM_GETOBJECT = 0x003D;
    internal const int UiaRootObjectId = -25;

    // UIAutomationClient NotificationKind / NotificationProcessing.
    const int NotificationKind_Other = 4;
    const int NotificationProcessing_ImportantAll = 0;   // assertive: announce every important one, queued
    const int NotificationProcessing_All = 2;            // polite: announce all, queued behind speech in progress

    [LibraryImport("UIAutomationCore.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UiaClientsAreListening();

    [LibraryImport("UIAutomationCore.dll")]
    internal static partial nint UiaReturnRawElementProvider(nint hwnd, nint wParam, nint lParam, void* el);

    [LibraryImport("UIAutomationCore.dll")]
    internal static partial int UiaHostProviderFromHwnd(nint hwnd, void** ppProvider);

    [LibraryImport("UIAutomationCore.dll")]
    internal static partial int UiaRaiseNotificationEvent(void* provider, int kind, int processing, char* displayString, char* activityId);

    [LibraryImport("oleaut32.dll")] internal static partial char* SysAllocString(char* psz);
    [LibraryImport("oleaut32.dll")] internal static partial void SysFreeString(char* bstr);

    /// <summary>WM_GETOBJECT for the UIA root: hand back the provider so a UIA client attaches. Non-UIA object ids (MSAA
    /// OBJID_CLIENT etc.) return false → DefWindowProc.</summary>
    internal static bool HandleGetObject(nint hwnd, nint wParam, nint lParam, UiaProviderCcw* provider, out nint result)
    {
        result = 0;
        if (provider == null || (long)lParam != UiaRootObjectId) return false;
        try { result = UiaReturnRawElementProvider(hwnd, wParam, lParam, provider); return true; }
        catch { return false; }
    }

    /// <summary>Raise a screen-reader notification (the live-region). No-op when no AT is listening or the text is empty.
    /// <paramref name="assertive"/> = interrupt/important (errors); else polite (status/"Copied").</summary>
    internal static void Announce(UiaProviderCcw* provider, string? text, bool assertive)
    {
        if (provider == null || string.IsNullOrEmpty(text)) return;
        try
        {
            if (!UiaClientsAreListening()) return;
            fixed (char* p = text)
            {
                char* bstr = SysAllocString(p);
                if (bstr == null) return;
                try { UiaRaiseNotificationEvent(provider, NotificationKind_Other, assertive ? NotificationProcessing_ImportantAll : NotificationProcessing_All, bstr, null); }
                finally { SysFreeString(bstr); }
            }
        }
        catch { /* a11y is best-effort — never crash the app over an announce */ }
    }

    /// <summary>In-process vtable round-trip — proves the hand-rolled CCW dispatches through its function-pointer vtable
    /// (QI + the four IRawElementProviderSimple methods) WITHOUT a real UIA client. Mirrors Win32DropTarget.SelfTest.</summary>
    internal static bool SelfTest(out string detail)
    {
        UiaProviderCcw* ccw = UiaProviderCcw.Create(0);
        try
        {
            void** vt = ccw->Vtbl;
            Guid iid = UiaProviderCcw.IID_IRawElementProviderSimple;
            void* ppv = null;
            var qi = (delegate* unmanaged[MemberFunction]<UiaProviderCcw*, Guid*, void**, int>)vt[0];
            int hrQi = qi(ccw, &iid, &ppv);
            bool qiOk = hrQi == 0 && ppv == ccw;

            int opts = -1;
            var getOpts = (delegate* unmanaged[MemberFunction]<UiaProviderCcw*, int*, int>)vt[3];
            int hrO = getOpts(ccw, &opts);

            void* pat = (void*)123;
            var getPat = (delegate* unmanaged[MemberFunction]<UiaProviderCcw*, int, void**, int>)vt[4];
            int hrP = getPat(ccw, 10000, &pat);

            long* var16 = stackalloc long[2]; var16[0] = 0x1111; var16[1] = 0x2222;   // a dirty 16-byte VARIANT
            var getVal = (delegate* unmanaged[MemberFunction]<UiaProviderCcw*, int, void*, int>)vt[5];
            int hrV = getVal(ccw, 30000, var16);
            bool valEmpty = var16[0] == 0 && var16[1] == 0;   // GetPropertyValue must zero it → VT_EMPTY

            detail = $"qi={qiOk}(0x{hrQi:X}) opts={opts}(hr0x{hrO:X}) patNull={pat == null}(hr0x{hrP:X}) valEmpty={valEmpty}(hr0x{hrV:X})";
            return qiOk && hrO == 0 && opts == 1 && hrP == 0 && pat == null && hrV == 0 && valEmpty;
        }
        finally { UiaProviderCcw.Destroy(ccw); }
    }
}

/// <summary>The hand-rolled <c>IRawElementProviderSimple</c> CCW (vtable + refcount + owner HWND) — modeled on
/// <c>Win32DropTargetCcw</c>. Serves <c>VT_EMPTY</c> for every property and links to the HWND host provider; it exists
/// only to host <c>UiaRaiseNotificationEvent</c>.</summary>
internal unsafe struct UiaProviderCcw
{
    public void** Vtbl;     // MUST be first (the COM "this" vptr)
    public int Rc;
    public nint Hwnd;

    // IRawElementProviderSimple {D6DD68D1-86FD-4332-8666-9ABEDEA2D24C}
    internal static readonly Guid IID_IRawElementProviderSimple = new(0xD6DD68D1, 0x86FD, 0x4332, 0x86, 0x66, 0x9A, 0xBE, 0xDE, 0xA2, 0xD2, 0x4C);
    static readonly Guid IID_IUnknown = new(0x00000000, 0x0000, 0x0000, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46);
    const int S_OK = 0, E_POINTER = unchecked((int)0x80004003), E_NOINTERFACE = unchecked((int)0x80004002);
    const int ProviderOptions_ServerSideProvider = 1;

    static readonly void** _vtbl = Build();

    static void** Build()
    {
        void** v = (void**)NativeMemory.Alloc(7, (nuint)sizeof(void*));
        v[0] = (delegate* unmanaged[MemberFunction]<UiaProviderCcw*, Guid*, void**, int>)&QueryInterface;
        v[1] = (delegate* unmanaged[MemberFunction]<UiaProviderCcw*, uint>)&AddRef;
        v[2] = (delegate* unmanaged[MemberFunction]<UiaProviderCcw*, uint>)&Release;
        v[3] = (delegate* unmanaged[MemberFunction]<UiaProviderCcw*, int*, int>)&GetProviderOptions;
        v[4] = (delegate* unmanaged[MemberFunction]<UiaProviderCcw*, int, void**, int>)&GetPatternProvider;
        v[5] = (delegate* unmanaged[MemberFunction]<UiaProviderCcw*, int, void*, int>)&GetPropertyValue;
        v[6] = (delegate* unmanaged[MemberFunction]<UiaProviderCcw*, void**, int>)&GetHostRawElementProvider;
        return v;
    }

    public static UiaProviderCcw* Create(nint hwnd)
    {
        var p = (UiaProviderCcw*)NativeMemory.Alloc((nuint)sizeof(UiaProviderCcw));
        p->Vtbl = _vtbl; p->Rc = 1; p->Hwnd = hwnd;
        return p;
    }

    public static void Destroy(UiaProviderCcw* p) => NativeMemory.Free(p);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    static int QueryInterface(UiaProviderCcw* self, Guid* riid, void** ppv)
    {
        if (ppv == null) return E_POINTER;
        if (*riid == IID_IUnknown || *riid == IID_IRawElementProviderSimple)
        { Interlocked.Increment(ref self->Rc); *ppv = self; return S_OK; }
        *ppv = null; return E_NOINTERFACE;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    static uint AddRef(UiaProviderCcw* self) => (uint)Interlocked.Increment(ref self->Rc);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    static uint Release(UiaProviderCcw* self) => (uint)Interlocked.Decrement(ref self->Rc);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    static int GetProviderOptions(UiaProviderCcw* self, int* pRetVal)
    { if (pRetVal != null) *pRetVal = ProviderOptions_ServerSideProvider; return S_OK; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    static int GetPatternProvider(UiaProviderCcw* self, int patternId, void** pRetVal)
    { if (pRetVal != null) *pRetVal = null; return S_OK; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    static int GetPropertyValue(UiaProviderCcw* self, int propertyId, void* pRetVal)
    { if (pRetVal != null) { ((long*)pRetVal)[0] = 0; ((long*)pRetVal)[1] = 0; } return S_OK; }   // VT_EMPTY (16-byte VARIANT)

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    static int GetHostRawElementProvider(UiaProviderCcw* self, void** pRetVal)
    {
        if (pRetVal == null) return S_OK;
        *pRetVal = null;
        try { return Win32Uia.UiaHostProviderFromHwnd(self->Hwnd, pRetVal); }
        catch { return S_OK; }
    }
}
