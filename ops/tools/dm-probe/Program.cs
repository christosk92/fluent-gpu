// dm-probe — Phase 0 of docs/plans/scroll-feel-rework-design.md (§4).
// Standalone evidence tool: does DirectManipulation deliver PTP two-finger pans as an event source
// (DM_POINTERHITTEST → SetContact → RUNNING/INERTIA + content-transform deltas) on THIS machine,
// and does EnableMouseInPointer(true) starve that channel?
// Cells: A = no MIP, fixed rect   B = MIP ON, fixed rect (the linchpin — our engine's world)
//        C = no MIP, window rect  D = MIP ON, no ProcessInput pumping (expected wedge)
// CoreCLR + classic [ComImport] interop on purpose — this is a throwaway probe, not engine code.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DmProbe;

internal static class Program
{
    // ---- cell config ----
    private static bool _mip, _windowRect, _pump = true;
    private static string _cell = "B";

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static StreamWriter _log;
    private static IntPtr _hwnd;

    private static IDirectManipulationManager _mgr;
    private static IDirectManipulationUpdateManager _upd;
    private static IDirectManipulationViewport _vp;
    private static IDirectManipulationContent _content;
    private static ProbeSink _sink;
    private static uint _cookie;

    // counters so the summary is machine-readable
    private static int _cHitTest, _cSetContactOk, _cSetContactFail, _cPtrWheel, _cMouseWheel,
                       _cContentUpdates, _cRunningFrames, _cInertiaFrames, _cProcessInputHandled;
    private static readonly Dictionary<string, int> _statusEdges = new();

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0) _cell = args[0].Trim().ToUpperInvariant();
        switch (_cell)
        {
            case "A": _mip = false; _windowRect = false; _pump = true; break;
            case "B": _mip = true;  _windowRect = false; _pump = true; break;
            case "C": _mip = false; _windowRect = true;  _pump = true; break;
            case "D": _mip = true;  _windowRect = false; _pump = false; break;
            default: Console.WriteLine("usage: dm-probe [A|B|C|D]"); return 2;
        }

        string logPath = Path.Combine(AppContext.BaseDirectory, $"trace-{_cell}.log");
        _log = new StreamWriter(logPath, append: false) { AutoFlush = true };
        Log($"CELL {_cell}  mip={_mip} rect={(_windowRect ? "window" : "1000x1000")} pump={_pump}  os={Environment.OSVersion} arch={RuntimeInformation.ProcessArchitecture}");

        Native.CoInitializeEx(IntPtr.Zero, Native.COINIT_APARTMENTTHREADED);
        if (_mip)
        {
            bool ok = Native.EnableMouseInPointer(true);
            Log($"EnableMouseInPointer(true) -> {ok} (gle={Marshal.GetLastWin32Error()})");
        }

        CreateProbeWindow();
        int hr = SetUpDManip();
        if (hr < 0) { Log($"FATAL dmanip setup hr=0x{hr:X8}"); return 1; }

        Log("READY — two-finger pan on the window, flick-lift at the end, then a slow pan+hold-release; ESC to finish.");
        RunLoop();

        TearDown();
        Summary();
        _log.Dispose();
        return 0;
    }

    private static void Log(string s)
    {
        string line = $"[{Clock.Elapsed.TotalMilliseconds,10:F3}] {s}";
        Console.WriteLine(line);
        _log.WriteLine(line);
    }

    // ---------------- window ----------------

    private static Native.WndProc _wndProcKeepAlive;   // root the delegate

    private static void CreateProbeWindow()
    {
        _wndProcKeepAlive = WndProc;
        var wc = new Native.WNDCLASSW
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcKeepAlive),
            hInstance = Native.GetModuleHandleW(null),
            lpszClassName = "DmProbeWnd",
            hbrBackground = (IntPtr)6, // COLOR_WINDOW+1
            hCursor = Native.LoadCursorW(IntPtr.Zero, (IntPtr)32512),
        };
        Native.RegisterClassW(ref wc);
        _hwnd = Native.CreateWindowExW(0, "DmProbeWnd", $"dm-probe cell {_cell} — two-finger pan here",
            Native.WS_OVERLAPPEDWINDOW | Native.WS_VISIBLE, 100, 100, 800, 600,
            IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        Log($"hwnd=0x{_hwnd:X}");
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Native.DM_POINTERHITTEST:
            {
                uint pointerId = (uint)(wParam.ToInt64() & 0xFFFF);
                _cHitTest++;
                int hr = _vp?.SetContact(pointerId) ?? -1;
                if (hr >= 0) _cSetContactOk++; else _cSetContactFail++;
                int st = -1; _vp?.GetStatus(out st);
                Log($"DM_POINTERHITTEST pid={pointerId} SetContact hr=0x{hr:X8} status={StatusName(st)}");
                return IntPtr.Zero;
            }
            case Native.WM_POINTERWHEEL:
            case Native.WM_POINTERHWHEEL:
            {
                short delta = unchecked((short)((wParam.ToInt64() >> 16) & 0xFFFF));
                uint pid = (uint)(wParam.ToInt64() & 0xFFFF);
                _cPtrWheel++;
                bool incontact = false; uint flags = 0;
                var pi = new Native.POINTER_INFO();
                if (Native.GetPointerInfo(pid, ref pi)) { flags = pi.pointerFlags; incontact = (flags & 0x4) != 0; }
                if (_cPtrWheel <= 400)
                    Log($"{(msg == Native.WM_POINTERWHEEL ? "WM_POINTERWHEEL " : "WM_POINTERHWHEEL")} pid={pid} delta={delta} ptype={pi.pointerType} flags=0x{flags:X} INCONTACT={incontact}");
                return IntPtr.Zero;
            }
            case Native.WM_MOUSEWHEEL:
            case Native.WM_MOUSEHWHEEL:
            {
                short delta = unchecked((short)((wParam.ToInt64() >> 16) & 0xFFFF));
                _cMouseWheel++;
                if (_cMouseWheel <= 400)
                    Log($"{(msg == Native.WM_MOUSEWHEEL ? "WM_MOUSEWHEEL  " : "WM_MOUSEHWHEEL ")} delta={delta} mods=0x{wParam.ToInt64() & 0xFFFF:X}");
                return IntPtr.Zero;
            }
            case Native.WM_KEYDOWN when wParam.ToInt64() == 0x1B: // ESC
                Native.DestroyWindow(hWnd);
                return IntPtr.Zero;
            case Native.WM_DESTROY:
                Native.PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return Native.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    // ---------------- dmanip ----------------

    private static int SetUpDManip()
    {
        try
        {
            var t = Type.GetTypeFromCLSID(new Guid("54E211B6-3650-4F75-8334-FA359598E1C5")); // CLSID_DirectManipulationManager
            _mgr = (IDirectManipulationManager)Activator.CreateInstance(t);
        }
        catch (Exception ex) { Log($"CoCreate DirectManipulationManager FAILED: {ex.Message}"); return -1; }
        Log("manager created");

        Guid iidUpd = typeof(IDirectManipulationUpdateManager).GUID;
        int hr = _mgr.GetUpdateManager(ref iidUpd, out _upd);
        Log($"GetUpdateManager hr=0x{hr:X8}"); if (hr < 0) return hr;

        Guid iidVp = typeof(IDirectManipulationViewport).GUID;
        hr = _mgr.CreateViewport(IntPtr.Zero, _hwnd, ref iidVp, out _vp);
        Log($"CreateViewport hr=0x{hr:X8}"); if (hr < 0) return hr;

        var rect = new Native.RECT { left = 0, top = 0, right = 1000, bottom = 1000 };
        if (_windowRect) { Native.GetClientRect(_hwnd, out rect); }
        hr = _vp.SetViewportRect(ref rect);
        Log($"SetViewportRect({rect.left},{rect.top},{rect.right},{rect.bottom}) hr=0x{hr:X8}");

        const int cfg = 0x1 | 0x2 | 0x4 | 0x20 | 0x10 | 0x80; // INTERACTION|TX|TY|TRANSLATION_INERTIA|SCALING|SCALING_INERTIA
        hr = _vp.AddConfiguration(cfg);       Log($"AddConfiguration(0x{cfg:X}) hr=0x{hr:X8}");
        hr = _vp.ActivateConfiguration(cfg);  Log($"ActivateConfiguration hr=0x{hr:X8}");
        hr = _vp.SetViewportOptions(0x2);     Log($"SetViewportOptions(MANUALUPDATE) hr=0x{hr:X8}");

        _sink = new ProbeSink();
        hr = _vp.AddEventHandler(_hwnd, _sink, out _cookie);
        Log($"AddEventHandler hr=0x{hr:X8} cookie={_cookie}");

        Guid iidContent = typeof(IDirectManipulationContent).GUID;
        hr = _vp.GetPrimaryContent(ref iidContent, out _content);
        Log($"GetPrimaryContent hr=0x{hr:X8}");
        if (hr >= 0)
        {
            var crect = new Native.RECT { left = 0, top = 0, right = 20000, bottom = 20000 };
            hr = _content.SetContentRect(ref crect);
            Log($"SetContentRect(20000x20000) hr=0x{hr:X8}");
        }

        hr = _vp.Enable();          Log($"viewport.Enable hr=0x{hr:X8}");
        hr = _mgr.Activate(_hwnd);  Log($"manager.Activate hr=0x{hr:X8}");
        hr = _vp.ZoomToRect(9500f, 9500f, 10500f, 10500f, false);   // after Enable — center for symmetric runway
        Log($"ZoomToRect(center) hr=0x{hr:X8}");
        return 0;
    }

    private static void RunLoop()
    {
        long lastUpdate = 0;
        while (true)
        {
            while (Native.PeekMessageW(out var m, IntPtr.Zero, 0, 0, 1 /*PM_REMOVE*/))
            {
                if (m.message == Native.WM_QUIT) return;
                if (_pump && _mgr != null)
                {
                    int hr = _mgr.ProcessInput(ref m, out bool handled);
                    if (handled)
                    {
                        _cProcessInputHandled++;
                        if (_cProcessInputHandled <= 60) Log($"ProcessInput CONSUMED msg=0x{m.message:X} hr=0x{hr:X8}");
                        continue; // DManip consumed it — don't dispatch
                    }
                    if (m.message is Native.WM_POINTERWHEEL or Native.WM_POINTERHWHEEL && _cPtrWheel < 8)
                        Log($"ProcessInput(WM_POINTERWHEEL) hr=0x{hr:X8} handled={handled}");
                }
                Native.TranslateMessage(ref m);
                Native.DispatchMessageW(ref m);
            }
            long now = Clock.ElapsedMilliseconds;
            if (now - lastUpdate >= 8)
            {
                lastUpdate = now;
                _upd?.Update(IntPtr.Zero);
            }
            Native.MsgWaitForMultipleObjectsEx(0, IntPtr.Zero, 4, 0x04FF /*QS_ALLINPUT*/, 0);
        }
    }

    private static void TearDown()
    {
        try
        {
            if (_vp != null) { _vp.Stop(); _vp.RemoveEventHandler(_cookie); _vp.Disable(); _vp.Abandon(); }
            _mgr?.Deactivate(_hwnd);
        }
        catch (Exception ex) { Log($"teardown: {ex.Message}"); }
    }

    private static void Summary()
    {
        Log("================ SUMMARY ================");
        Log($"cell={_cell} mip={_mip} rect={(_windowRect ? "window" : "fixed")} pump={_pump}");
        Log($"DM_POINTERHITTEST={_cHitTest}  SetContact ok={_cSetContactOk} fail={_cSetContactFail}");
        Log($"status edges: {string.Join(", ", _statusEdges.Select(kv => $"{kv.Key}×{kv.Value}"))}");
        Log($"content updates={_cContentUpdates} (RUNNING={_cRunningFrames}, INERTIA={_cInertiaFrames})");
        Log($"WM_POINTERWHEEL={_cPtrWheel}  WM_MOUSEWHEEL={_cMouseWheel}  ProcessInput-consumed={_cProcessInputHandled}");
        string verdict =
            _cContentUpdates > 10 && _cInertiaFrames > 0 ? "PASS — DManip delivered pan + OS momentum"
            : _cContentUpdates > 10 ? "PARTIAL — pan deltas but no INERTIA observed"
            : _cHitTest > 0 ? "ENGAGE-ONLY — hit-test fired but no content deltas (wedge?)"
            : "FAIL — DManip never engaged (no DM_POINTERHITTEST)";
        Log($"VERDICT: {verdict}");
    }

    internal static string StatusName(int s) => s switch
    {
        0 => "BUILDING", 1 => "ENABLED", 2 => "DISABLED", 3 => "RUNNING",
        4 => "INERTIA", 5 => "READY", 6 => "SUSPENDED", _ => $"?{s}"
    };

    internal static void CountEdge(string edge) { _statusEdges.TryGetValue(edge, out int n); _statusEdges[edge] = n + 1; }
    internal static void CountContent(bool inertia) { _cContentUpdates++; if (inertia) _cInertiaFrames++; else _cRunningFrames++; }

    // ---------------- the sink ----------------

    internal sealed class ProbeSink : IDirectManipulationViewportEventHandler
    {
        private float _lastTx, _lastTy, _lastScale = 1f;
        private int _status = 5; // READY
        private readonly float[] _m = new float[6];

        public int OnViewportStatusChanged(IDirectManipulationViewport viewport, int current, int previous)
        {
            string edge = $"{StatusName(previous)}->{StatusName(current)}";
            CountEdge(edge);
            _status = current;
            Log($"STATUS {edge}");
            if (current == 5 /*READY*/) { _lastTx = 0; _lastTy = 0; _lastScale = 1f; } // transform resets with gesture
            return 0;
        }

        public int OnViewportUpdated(IDirectManipulationViewport viewport) => 0;

        public int OnContentUpdated(IDirectManipulationViewport viewport, IDirectManipulationContent content)
        {
            try { return OnContentUpdatedCore(content); }
            catch (Exception ex) { Log($"sink EX: {ex.GetType().Name} {ex.Message}"); return 0; }
        }

        private int OnContentUpdatedCore(IDirectManipulationContent content)
        {
            int hr = content.GetContentTransform(_m, 6);
            if (hr < 0) { Log($"GetContentTransform hr=0x{hr:X8}"); return 0; }
            float scale = _m[0], tx = _m[4], ty = _m[5];
            float dx = tx - _lastTx, dy = ty - _lastTy;
            bool inertia = _status == 4;
            CountContent(inertia);
            if (MathF.Abs(dx) > 0.001f || MathF.Abs(dy) > 0.001f || MathF.Abs(scale - _lastScale) > 0.0001f)
                Log($"CONTENT {(inertia ? "INERTIA" : "RUN    ")} tx={tx,9:F2} ty={ty,9:F2} dx={dx,7:F2} dy={dy,7:F2} scale={scale:F4}");
            _lastTx = tx; _lastTy = ty; _lastScale = scale;
            return 0;
        }
    }
}

// ---------------- COM interop (vtable order verified against directmanipulation.h 10.0.26100.0) ----------------

[ComImport, Guid("FBF5D3B4-70C7-4163-9322-5A6F660D6FBC"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDirectManipulationManager
{
    [PreserveSig] int Activate(IntPtr hwnd);
    [PreserveSig] int Deactivate(IntPtr hwnd);
    [PreserveSig] int RegisterHitTestTarget(IntPtr hwnd, IntPtr hitTestHwnd, int hitTestType);
    [PreserveSig] int ProcessInput(ref Native.MSG msg, [MarshalAs(UnmanagedType.Bool)] out bool handled);
    [PreserveSig] int GetUpdateManager(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDirectManipulationUpdateManager obj);
    [PreserveSig] int CreateViewport(IntPtr frameInfo, IntPtr hwnd, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDirectManipulationViewport obj);
    [PreserveSig] int CreateContent(IntPtr frameInfo, ref Guid clsid, ref Guid riid, out IntPtr obj);
}

[ComImport, Guid("28B85A3D-60A0-48BD-9BA1-5CE8D9EA3A6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDirectManipulationViewport
{
    [PreserveSig] int Enable();
    [PreserveSig] int Disable();
    [PreserveSig] int SetContact(uint pointerId);
    [PreserveSig] int ReleaseContact(uint pointerId);
    [PreserveSig] int ReleaseAllContacts();
    [PreserveSig] int GetStatus(out int status);
    [PreserveSig] int GetTag(ref Guid riid, out IntPtr obj, out uint id);
    [PreserveSig] int SetTag(IntPtr obj, uint id);
    [PreserveSig] int GetViewportRect(out Native.RECT rect);
    [PreserveSig] int SetViewportRect(ref Native.RECT rect);
    [PreserveSig] int ZoomToRect(float left, float top, float right, float bottom, [MarshalAs(UnmanagedType.Bool)] bool animate);
    [PreserveSig] int SetViewportTransform([MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] float[] matrix, uint pointCount);
    [PreserveSig] int SyncDisplayTransform([MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] float[] matrix, uint pointCount);
    [PreserveSig] int GetPrimaryContent(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDirectManipulationContent content);
    [PreserveSig] int AddContent(IntPtr content);
    [PreserveSig] int RemoveContent(IntPtr content);
    [PreserveSig] int SetViewportOptions(int options);
    [PreserveSig] int AddConfiguration(int configuration);
    [PreserveSig] int RemoveConfiguration(int configuration);
    [PreserveSig] int ActivateConfiguration(int configuration);
    [PreserveSig] int SetManualGesture(int gesture);
    [PreserveSig] int SetChaining(int enabledTypes);
    [PreserveSig] int AddEventHandler(IntPtr hwnd, [MarshalAs(UnmanagedType.Interface)] IDirectManipulationViewportEventHandler handler, out uint cookie);
    [PreserveSig] int RemoveEventHandler(uint cookie);
    [PreserveSig] int SetInputMode(int mode);
    [PreserveSig] int SetUpdateMode(int mode);
    [PreserveSig] int Stop();
    [PreserveSig] int Abandon();
}

[ComImport, Guid("952121DA-D69F-45F9-B0F9-F23944321A6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDirectManipulationViewportEventHandler
{
    [PreserveSig] int OnViewportStatusChanged([MarshalAs(UnmanagedType.Interface)] IDirectManipulationViewport viewport, int current, int previous);
    [PreserveSig] int OnViewportUpdated([MarshalAs(UnmanagedType.Interface)] IDirectManipulationViewport viewport);
    [PreserveSig] int OnContentUpdated([MarshalAs(UnmanagedType.Interface)] IDirectManipulationViewport viewport, [MarshalAs(UnmanagedType.Interface)] IDirectManipulationContent content);
}

[ComImport, Guid("B89962CB-3D89-442B-BB58-5098FA0F9F16"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDirectManipulationContent
{
    [PreserveSig] int GetContentRect(out Native.RECT rect);
    [PreserveSig] int SetContentRect(ref Native.RECT rect);
    [PreserveSig] int GetViewport(ref Guid riid, out IntPtr viewport);
    [PreserveSig] int GetTag(ref Guid riid, out IntPtr obj, out uint id);
    [PreserveSig] int SetTag(IntPtr obj, uint id);
    [PreserveSig] int GetOutputTransform([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] float[] matrix, uint pointCount);
    [PreserveSig] int GetContentTransform([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] float[] matrix, uint pointCount);
    [PreserveSig] int SyncContentTransform([MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] float[] matrix, uint pointCount);
}

[ComImport, Guid("B0AE62FD-BE34-46E7-9CAA-D361FACBB9CC"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDirectManipulationUpdateManager
{
    [PreserveSig] int RegisterWaitHandleCallback(IntPtr handle, IntPtr eventHandler, out uint cookie);
    [PreserveSig] int UnregisterWaitHandleCallback(uint cookie);
    [PreserveSig] int Update(IntPtr frameInfo);
}

internal static class Native
{
    public const uint WS_OVERLAPPEDWINDOW = 0x00CF0000, WS_VISIBLE = 0x10000000;
    public const uint WM_DESTROY = 0x0002, WM_KEYDOWN = 0x0100, WM_QUIT = 0x0012;
    public const uint WM_MOUSEWHEEL = 0x020A, WM_MOUSEHWHEEL = 0x020E;
    public const uint WM_POINTERWHEEL = 0x024E, WM_POINTERHWHEEL = 0x024F;
    public const uint DM_POINTERHITTEST = 0x0250;
    public const uint COINIT_APARTMENTTHREADED = 0x2;

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINTER_INFO
    {
        public uint pointerType, pointerId, frameId, pointerFlags;
        public IntPtr sourceDevice, hwndTarget;
        public POINT ptPixelLocation, ptHimetricLocation, ptPixelLocationRaw, ptHimetricLocationRaw;
        public uint dwTime;
        public uint historyCount;
        public int inputData;
        public uint dwKeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSW
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [DllImport("ole32")] public static extern int CoInitializeEx(IntPtr r, uint flags);
    [DllImport("user32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool EnableMouseInPointer([MarshalAs(UnmanagedType.Bool)] bool enable);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern ushort RegisterClassW(ref WNDCLASSW wc);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern IntPtr CreateWindowExW(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32")] public static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool PeekMessageW(out MSG m, IntPtr h, uint min, uint max, uint remove);
    [DllImport("user32")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32")] public static extern IntPtr DispatchMessageW(ref MSG m);
    [DllImport("user32")] public static extern void PostQuitMessage(int code);
    [DllImport("user32")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetPointerInfo(uint id, ref POINTER_INFO pi);
    [DllImport("user32")] public static extern uint MsgWaitForMultipleObjectsEx(uint count, IntPtr handles, uint ms, uint wakeMask, uint flags);
    [DllImport("user32")] public static extern IntPtr LoadCursorW(IntPtr inst, IntPtr name);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandleW(string name);
}
