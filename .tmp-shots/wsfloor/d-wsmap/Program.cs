// Working-set mapper: VMMap-style attribution of another process's RESIDENT pages.
// Walks committed regions via VirtualQueryEx, queries per-page residency via QueryWorkingSetEx,
// buckets by region type (image/mapped/private) and per-module, splits shared vs private residency.
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

internal static unsafe class WsMap
{
    [DllImport("kernel32.dll", SetLastError = true)] static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern nuint VirtualQueryEx(nint h, void* addr, MBI* mbi, nuint len);
    [DllImport("psapi.dll", SetLastError = true)] static extern bool QueryWorkingSetEx(nint h, void* pv, uint cb);
    [DllImport("psapi.dll", CharSet = CharSet.Unicode)] static extern uint GetMappedFileNameW(nint h, void* addr, char* name, uint size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint h);

    [StructLayout(LayoutKind.Sequential)]
    struct MBI { public ulong BaseAddress, AllocationBase; public uint AllocationProtect, __align; public ulong RegionSize; public uint State, Protect, Type, __pad; }

    [StructLayout(LayoutKind.Sequential)]
    struct WsExBlock { public ulong VirtualAddress; public ulong VirtualAttributes; }

    const uint MEM_COMMIT = 0x1000, MEM_IMAGE = 0x1000000, MEM_MAPPED = 0x40000, MEM_PRIVATE = 0x20000;

    static int Main(string[] argv)
    {
        if (argv.Length < 1 || !int.TryParse(argv[0], out int pid)) { Console.WriteLine("usage: d-wsmap <pid>"); return 1; }
        nint h = OpenProcess(0x0400 | 0x0010, false, pid);   // QUERY_INFORMATION | VM_READ
        if (h == 0) { Console.WriteLine($"OpenProcess failed err={Marshal.GetLastWin32Error()}"); return 1; }

        var proc = Process.GetProcessById(pid);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"pid={pid} name={proc.ProcessName} ws={proc.WorkingSet64 / (1024.0 * 1024.0):0.0}MB priv={proc.PrivateMemorySize64 / (1024.0 * 1024.0):0.0}MB"));

        // region walk
        ulong addr = 0;
        var regions = new List<MBI>();
        MBI mbi;
        while (VirtualQueryEx(h, (void*)addr, &mbi, (nuint)sizeof(MBI)) == (nuint)sizeof(MBI))
        {
            if (mbi.State == MEM_COMMIT) regions.Add(mbi);
            ulong next = mbi.BaseAddress + mbi.RegionSize;
            if (next <= addr) break;
            addr = next;
            if (addr >= 0x7FFF_FFFF_0000UL) break;
        }

        // page residency per region (batched QueryWorkingSetEx)
        const int Batch = 8192;
        var block = new WsExBlock[Batch];
        long resImg = 0, resMap = 0, resPriv = 0, resImgShared = 0, resPrivPrivate = 0, resMapShared = 0;
        long comImg = 0, comMap = 0, comPriv = 0;
        var perAllocBase = new Dictionary<ulong, long>();          // resident bytes per AllocationBase (images+mapped)
        var privRegions = new Dictionary<ulong, long>();           // resident bytes per AllocationBase (private)
        var privProtect = new Dictionary<ulong, Dictionary<uint, long>>();   // AllocationBase -> protect -> resident bytes

        fixed (WsExBlock* pb = block)
        {
            foreach (var r in regions)
            {
                long resident = 0;
                ulong pages = r.RegionSize / 4096;
                ulong page = 0;
                while (page < pages)
                {
                    int n = (int)Math.Min((ulong)Batch, pages - page);
                    for (int i = 0; i < n; i++) { pb[i].VirtualAddress = r.BaseAddress + (page + (ulong)i) * 4096; pb[i].VirtualAttributes = 0; }
                    if (QueryWorkingSetEx(h, pb, (uint)(n * sizeof(WsExBlock))))
                    {
                        for (int i = 0; i < n; i++)
                        {
                            ulong a = pb[i].VirtualAttributes;
                            if ((a & 1) == 0) continue;            // not valid/resident
                            bool shared = (a & (1UL << 15)) != 0;
                            resident += 4096;
                            if (r.Type == MEM_IMAGE) { resImg += 4096; if (shared) resImgShared += 4096; }
                            else if (r.Type == MEM_MAPPED) { resMap += 4096; if (shared) resMapShared += 4096; }
                            else { resPriv += 4096; if (!shared) resPrivPrivate += 4096; }
                        }
                    }
                    page += (ulong)n;
                }
                if (r.Type == MEM_IMAGE) comImg += (long)r.RegionSize;
                else if (r.Type == MEM_MAPPED) comMap += (long)r.RegionSize;
                else comPriv += (long)r.RegionSize;
                if (resident > 0)
                {
                    if (r.Type == MEM_PRIVATE)
                    {
                        privRegions[r.AllocationBase] = privRegions.GetValueOrDefault(r.AllocationBase) + resident;
                        if (!privProtect.TryGetValue(r.AllocationBase, out var hist)) privProtect[r.AllocationBase] = hist = new();
                        hist[r.Protect] = hist.GetValueOrDefault(r.Protect) + resident;
                    }
                    else
                        perAllocBase[r.AllocationBase] = perAllocBase.GetValueOrDefault(r.AllocationBase) + resident;
                }
            }
        }

        static string MB(long b) => string.Create(CultureInfo.InvariantCulture, $"{b / (1024.0 * 1024.0):0.00}MB");
        Console.WriteLine($"committed:  image={MB(comImg)} mapped={MB(comMap)} private={MB(comPriv)}");
        Console.WriteLine($"resident:   image={MB(resImg)} (shared {MB(resImgShared)})  mapped={MB(resMap)} (shared {MB(resMapShared)})  private={MB(resPriv)} (private {MB(resPrivPrivate)})");
        Console.WriteLine($"resident total={MB(resImg + resMap + resPriv)}");

        // name image/mapped alloc bases via GetMappedFileName
        Console.WriteLine("top image/mapped by RESIDENT bytes:");
        char* nameBuf = stackalloc char[1024];
        foreach (var kv in perAllocBase.OrderByDescending(k => k.Value).Take(30))
        {
            string nm = "?";
            if (GetMappedFileNameW(h, (void*)kv.Key, nameBuf, 1024) > 0)
            {
                nm = new string(nameBuf);
                int slash = nm.LastIndexOf('\\'); if (slash >= 0) nm = nm[(slash + 1)..];
            }
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {nm,-40} {kv.Value / (1024.0 * 1024.0),8:0.00}MB"));
        }
        Console.WriteLine("top private allocations by RESIDENT bytes (AllocationBase, size, protect mix):");
        foreach (var kv in privRegions.OrderByDescending(k => k.Value).Take(20))
        {
            string prot = string.Join(" ", privProtect[kv.Key].OrderByDescending(p => p.Value)
                .Select(p => string.Create(CultureInfo.InvariantCulture, $"{ProtName(p.Key)}={p.Value / (1024.0 * 1024.0):0.00}MB")));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  0x{kv.Key:x12} {kv.Value / (1024.0 * 1024.0),8:0.00}MB  {prot}"));
        }

        static string ProtName(uint p) => p switch
        {
            0x04 => "RW", 0x02 => "RO", 0x08 => "WCOPY", 0x104 => "RW+G", 0x204 => "RW+WC",
            0x40 => "RWX", 0x20 => "RX", 0x10 => "X", 0x01 => "NOACC", 0x202 => "RO+WC",
            _ => string.Create(CultureInfo.InvariantCulture, $"0x{p:x}"),
        };
        CloseHandle(h);
        return 0;
    }
}
