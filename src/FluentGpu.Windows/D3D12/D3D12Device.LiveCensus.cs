using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Rhi.D3D12;

/// <summary>
/// The byte-exact GPU census (debug layer only): every object the D3D12 runtime still holds for this device, from
/// <c>ID3D12DebugDevice::ReportLiveDeviceObjects</c>, set against <see cref="D3D12MemoryDiagnostics"/>' ledger.
/// <para>The tracked total and the adapter's own usage (<c>QueryVideoMemoryInfo</c>) disagree by an "untracked" figure the
/// <c>mem.sample</c> line can only report, never explain. Here each live resource and heap falls in one of three rows:
/// <b>tracked</b> (in the ledger), <b>leaked</b> (named by <see cref="D3D12MemoryDiagnostics.Track"/>, so the engine made
/// it, but no longer in the ledger: released from the books while a reference still keeps it alive) and <b>untracked</b>
/// (never named: created outside the ledger). Leaked and untracked rows are described from the live object
/// (<c>GetDesc</c>, <c>GetHeapProperties</c>, <c>GetResourceAllocationInfo</c>), so they carry exact sizes. What the
/// adapter reports beyond all resources and heaps is the driver's own (pipelines, command allocators, descriptor heaps).</para>
/// <para>Render thread (the submit thread), because it reads and clears the info queue that
/// <see cref="DrainDebugLayerMessages"/> also drains. Diagnostics only: it allocates and walks every live object.</para>
/// </summary>
public sealed unsafe partial class D3D12Device
{
    private static readonly Regex LiveLine = new(@"Live (\w+) at 0x([0-9A-Fa-f]+), (?:Name: (.*?), )?Refcount: (\d+)", RegexOptions.Compiled);

    /// <summary>Run the census now (render thread). Without the debug layer it says so and returns.</summary>
    public string LiveObjectCensus()
    {
        if (_device == null) return "gpu-census: no device";
        if (_infoQueue == null) return "gpu-census: needs the D3D12 debug layer (AppOptions.D3D12DebugLayer and the Windows 'Graphics Tools' feature)";
        ID3D12DebugDevice* dbg;
        if ((int)_device->QueryInterface(__uuidof<ID3D12DebugDevice>(), (void**)&dbg) < 0 || dbg == null) return "gpu-census: no ID3D12DebugDevice";

        DrainDebugLayerMessages();   // whatever is queued belongs to the frames, not to the census
        _infoQueue->PushEmptyStorageFilter();
        var lines = new List<string>(4096);
        try
        {
            dbg->ReportLiveDeviceObjects(D3D12_RLDO_FLAGS.D3D12_RLDO_DETAIL | D3D12_RLDO_FLAGS.D3D12_RLDO_IGNORE_INTERNAL);
            ulong n = _infoQueue->GetNumStoredMessages();
            byte[] scratch = new byte[64 * 1024];
            for (ulong i = 0; i < n; i++)
            {
                nuint len = 0;
                if ((int)_infoQueue->GetMessage(i, null, &len) < 0 || len == 0 || len > (nuint)scratch.Length) continue;
                fixed (byte* buf = scratch)
                {
                    var msg = (D3D12_MESSAGE*)buf;
                    if ((int)_infoQueue->GetMessage(i, msg, &len) < 0) continue;
                    int chars = msg->DescriptionByteLength > 0 ? (int)msg->DescriptionByteLength - 1 : 0;
                    if (chars > 0) lines.Add(Marshal.PtrToStringAnsi((nint)msg->pDescription, chars) ?? "");
                }
            }
            _infoQueue->ClearStoredMessages();
        }
        finally
        {
            _infoQueue->PopStorageFilter();
            dbg->Release();
        }

        // type → (count); resources/heaps → per row and per description key: (count, bytes)
        var types = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var rows = new Dictionary<string, (int Count, ulong Bytes)>(StringComparer.Ordinal);
        var detail = new Dictionary<string, (int Count, ulong Bytes)>(StringComparer.Ordinal);
        foreach (string line in lines)
        {
            var m = LiveLine.Match(line);
            if (!m.Success) continue;
            string type = m.Groups[1].Value;
            types[type] = types.GetValueOrDefault(type) + 1;
            if (type is not ("ID3D12Resource" or "ID3D12Heap")) continue;
            nuint p = (nuint)ulong.Parse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            string name = m.Groups[3].Success ? m.Groups[3].Value : "";
            bool named = name.Length > 0 && name != "NULL" && !name.StartsWith("<", StringComparison.Ordinal);
            string row = D3D12MemoryDiagnostics.IsTracked(p) ? "tracked" : named ? "leaked" : "untracked";
            ulong bytes;
            string key;
            if (type == "ID3D12Resource")
            {
                var res = (ID3D12Resource*)p;
                res->AddRef();
                var desc = res->GetDesc();
                D3D12_HEAP_PROPERTIES hp = default; D3D12_HEAP_FLAGS hf = default;
                bool committed = (int)res->GetHeapProperties(&hp, &hf) >= 0;
                bytes = desc.Dimension == D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_BUFFER
                    ? desc.Width : D3D12MemoryDiagnostics.AllocationBytes(_device, &desc);
                if (!committed) bytes = 0;   // placed: its heap owns the memory (the heap is its own row)
                res->Release();
                key = desc.Dimension == D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_BUFFER
                    ? $"buffer {(committed ? hp.Type.ToString().Replace("D3D12_HEAP_TYPE_", "") : "placed")}"
                    : $"tex {desc.Width}x{desc.Height} {desc.Format.ToString().Replace("DXGI_FORMAT_", "")} {(committed ? hp.Type.ToString().Replace("D3D12_HEAP_TYPE_", "") : "placed")} flags={(int)desc.Flags}";
            }
            else
            {
                var heap = (ID3D12Heap*)p;
                heap->AddRef();
                var hd = heap->GetDesc();
                heap->Release();
                bytes = hd.SizeInBytes;
                key = $"heap {hd.Properties.Type.ToString().Replace("D3D12_HEAP_TYPE_", "")}";
            }
            if (named) key += " \"" + name + "\"";
            var r = rows.GetValueOrDefault(row); rows[row] = (r.Count + 1, r.Bytes + bytes);
            string dk = row + " | " + key;
            var d = detail.GetValueOrDefault(dk); detail[dk] = (d.Count + 1, d.Bytes + bytes);
        }

        const double MiB = 1024.0 * 1024.0;
        var sb = new StringBuilder(4096).Append("gpu-census live objects:");
        foreach (var kv in types) sb.Append(' ').Append(kv.Key.Replace("ID3D12", "")).Append('=').Append(kv.Value);
        sb.AppendLine();
        foreach (var kv in rows.OrderByDescending(k => k.Value.Bytes))
            sb.Append("  ").Append(kv.Key).Append(": ").Append(kv.Value.Count).Append(" objects, ").Append((kv.Value.Bytes / MiB).ToString("0.00", CultureInfo.InvariantCulture)).AppendLine(" MiB");
        if (D3D12MemoryDiagnostics.LastVideoMemory is { Valid: true } vm)
            sb.Append("  adapter local usage: ").Append((vm.LocalCurrentUsage / MiB).ToString("0.00", CultureInfo.InvariantCulture)).Append(" MiB (beyond resources and heaps = driver-internal)").AppendLine();
        foreach (var kv in detail.Where(k => !k.Key.StartsWith("tracked", StringComparison.Ordinal)).OrderByDescending(k => k.Value.Bytes).Take(60))
            sb.Append("    ").Append((kv.Value.Bytes / MiB).ToString("0.00", CultureInfo.InvariantCulture).PadLeft(8)).Append(" MiB ").Append(kv.Value.Count.ToString(CultureInfo.InvariantCulture).PadLeft(5)).Append("  ").AppendLine(kv.Key);
        sb.AppendLine("  tracked, by class:");
        foreach (var kv in detail.Where(k => k.Key.StartsWith("tracked", StringComparison.Ordinal)).OrderByDescending(k => k.Value.Bytes).Take(40))
            sb.Append("    ").Append((kv.Value.Bytes / MiB).ToString("0.00", CultureInfo.InvariantCulture).PadLeft(8)).Append(" MiB ").Append(kv.Value.Count.ToString(CultureInfo.InvariantCulture).PadLeft(5)).Append("  ").AppendLine(kv.Key);
        return sb.ToString();
    }
}
