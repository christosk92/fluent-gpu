// Staged working-set floor probe: CoreCLR baseline -> Win32 window -> DXGI factory -> D3D12 device
// (loads the GPU driver UMD) -> command queue + flip-discard swapchain (1860x1230 BGRA8 x2, mirroring
// the real app's backbuffer geometry at 1240x820 logical / 150% DPI) -> 60 presents.
// Prints Environment.WorkingSet (the exact metric MemCensus.cs:146 reports) after each stage.
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

internal static unsafe class Probe
{
    // ---- user32 ----
    [DllImport("user32.dll", SetLastError = true)] static extern ushort RegisterClassExW(WNDCLASSEXW* wc);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern nint CreateWindowExW(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("user32.dll")] static extern nint DefWindowProcW(nint hwnd, uint msg, nint wp, nint lp);
    [DllImport("user32.dll")] static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32.dll")] static extern bool PeekMessageW(MSG* msg, nint hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] static extern bool TranslateMessage(MSG* msg);
    [DllImport("user32.dll")] static extern nint DispatchMessageW(MSG* msg);
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(nint ctx);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint GetModuleHandleW(string? name);

    // ---- dxgi / d3d12 flat exports ----
    [DllImport("dxgi.dll")] static extern int CreateDXGIFactory2(uint flags, Guid* riid, void** factory);
    [DllImport("d3d12.dll")] static extern int D3D12CreateDevice(void* adapter, int featureLevel, Guid* riid, void** device);

    [StructLayout(LayoutKind.Sequential)]
    struct WNDCLASSEXW
    {
        public uint cbSize; public uint style; public nint lpfnWndProc; public int cbClsExtra; public int cbWndExtra;
        public nint hInstance; public nint hIcon; public nint hCursor; public nint hbrBackground; public nint lpszMenuName;
        public nint lpszClassName; public nint hIconSm;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public nint hwnd; public uint message; public nint wParam; public nint lParam; public uint time; public int ptX; public int ptY; }

    [StructLayout(LayoutKind.Sequential)]
    struct DXGI_SWAP_CHAIN_DESC1
    {
        public uint Width, Height, Format; public int Stereo; public uint SampleCount, SampleQuality;
        public uint BufferUsage, BufferCount, Scaling, SwapEffect, AlphaMode, Flags;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct D3D12_COMMAND_QUEUE_DESC { public int Type, Priority; public uint Flags, NodeMask; }

    [UnmanagedCallersOnly]
    static nint WndProc(nint hwnd, uint msg, nint wp, nint lp) => DefWindowProcW(hwnd, msg, wp, lp);

    static void Pump(int ms)
    {
        var sw = Stopwatch.StartNew();
        MSG m;
        while (sw.ElapsedMilliseconds < ms)
        {
            while (PeekMessageW(&m, 0, 0, 0, 1)) { TranslateMessage(&m); DispatchMessageW(&m); }
            Thread.Sleep(10);
        }
    }

    static void Stage(string name)
    {
        long ws = Environment.WorkingSet;
        long priv = Process.GetCurrentProcess().PrivateMemorySize64;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"stage {name,-22} ws={ws / (1024.0 * 1024.0),7:0.00}MB priv={priv / (1024.0 * 1024.0),7:0.00}MB"));
    }

    static int Main()
    {
        SetProcessDpiAwarenessContext(-4);   // PER_MONITOR_AWARE_V2, same as the real PAL
        Thread.Sleep(1200);                  // let startup/tiered JIT settle
        Stage("0-clr-baseline");

        var cls = Marshal.StringToHGlobalUni("WsFloorProbe");
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)sizeof(WNDCLASSEXW),
            lpfnWndProc = (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&WndProc,
            hInstance = GetModuleHandleW(null),
            lpszClassName = cls,
        };
        if (RegisterClassExW(&wc) == 0) { Console.WriteLine("RegisterClassExW failed"); return 1; }
        // Small unobtrusive window; the swapchain buffer size below is what matters for memory.
        nint hwnd = CreateWindowExW(0, "WsFloorProbe", "ws-floor-probe", 0x10CF0000 /*WS_OVERLAPPEDWINDOW|VISIBLE*/,
                                    20, 20, 360, 240, 0, 0, GetModuleHandleW(null), 0);
        if (hwnd == 0) { Console.WriteLine("CreateWindowExW failed"); return 1; }
        ShowWindow(hwnd, 4 /*SW_SHOWNOACTIVATE*/);
        Pump(500);
        Stage("1-win32-window");

        Guid iidFactory2 = new("50c83a1c-e072-4c48-87b0-3630fa36a6d0");
        void* factory = null;
        int hr = CreateDXGIFactory2(0, &iidFactory2, &factory);
        Console.WriteLine($"CreateDXGIFactory2 hr=0x{hr:x8}");
        if (hr < 0) return 2;
        Pump(300);
        Stage("2-dxgi-factory");

        Guid iidDevice = new("189819f1-1db6-4b57-be54-1821339b85f7");
        void* device = null;
        hr = D3D12CreateDevice(null, 0xb000 /*FL 11_0*/, &iidDevice, &device);
        Console.WriteLine($"D3D12CreateDevice hr=0x{hr:x8}");
        if (hr < 0) return 3;
        Pump(300);
        Stage("3-d3d12-device");

        // ID3D12Device::CreateCommandQueue = vtable slot 8
        var queueDesc = new D3D12_COMMAND_QUEUE_DESC();
        Guid iidQueue = new("0ec870a6-5d7e-4c22-8cfc-5baae07616ed");
        void* queue = null;
        hr = ((delegate* unmanaged<void*, D3D12_COMMAND_QUEUE_DESC*, Guid*, void**, int>)(*(void***)device)[8])(device, &queueDesc, &iidQueue, &queue);
        Console.WriteLine($"CreateCommandQueue hr=0x{hr:x8}");
        if (hr < 0) return 4;

        // IDXGIFactory2::CreateSwapChainForHwnd = vtable slot 15. Real app shape: BGRA8_UNORM, 2 buffers,
        // FLIP_DISCARD, at the real app's physical backbuffer size (1240x820 logical @ 150% = 1860x1230).
        var scd = new DXGI_SWAP_CHAIN_DESC1
        {
            Width = 1860, Height = 1230, Format = 87 /*DXGI_FORMAT_B8G8R8A8_UNORM*/,
            SampleCount = 1, BufferUsage = 0x20 /*RENDER_TARGET_OUTPUT*/, BufferCount = 2,
            Scaling = 0 /*STRETCH*/, SwapEffect = 4 /*FLIP_DISCARD*/, AlphaMode = 0, Flags = 0,
        };
        void* swapchain = null;
        hr = ((delegate* unmanaged<void*, void*, nint, DXGI_SWAP_CHAIN_DESC1*, void*, void*, void**, int>)(*(void***)factory)[15])(factory, queue, hwnd, &scd, null, null, &swapchain);
        Console.WriteLine($"CreateSwapChainForHwnd hr=0x{hr:x8}");
        if (hr < 0) return 5;
        Pump(300);
        Stage("4-swapchain-2x1860x1230");

        // IDXGISwapChain::Present = vtable slot 8. 60 presents of (uncleared) buffers.
        for (int i = 0; i < 60; i++)
        {
            hr = ((delegate* unmanaged<void*, uint, uint, int>)(*(void***)swapchain)[8])(swapchain, 1, 0);
            MSG m; while (PeekMessageW(&m, 0, 0, 0, 1)) { TranslateMessage(&m); DispatchMessageW(&m); }
        }
        Pump(300);
        Stage("5-after-60-presents");

        var gc = GC.GetGCMemoryInfo();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"gcCommitted={gc.TotalCommittedBytes / (1024.0 * 1024.0):0.00}MB"));

        // Loaded-module evidence: names + image VA sizes (NOT residency), >0.9MB, descending.
        Console.WriteLine("modules (imageSize>=0.9MB, VA size not residency):");
        var mods = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
            .Where(mm => mm.ModuleMemorySize >= 900_000)
            .OrderByDescending(mm => mm.ModuleMemorySize).Take(30);
        foreach (var mm in mods)
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {mm.ModuleName,-34} {mm.ModuleMemorySize / (1024.0 * 1024.0),8:0.00}MB"));
        return 0;
    }
}
