using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using FluentGpu.Hosting;

namespace FluentGpu.Rhi.D3D12;

/// <summary>
/// <c>--fg sub-split</c>: where a composite submit's CPU time goes, phase by phase (the ledger times only the plan build, the
/// glyph prepare and the tile replay; the rest of <c>subMs</c> was one lump). Every <see cref="SsWindow"/> composite submits one
/// <c>[sub.split]</c> line on stderr: the mean microseconds per submit of each phase, and the mean item / placement / raster
/// counts of those submits. Off by default: a mark is one predicted branch.
/// </summary>
public sealed unsafe partial class D3D12Device
{
    private const int SsWindow = 256;
    private const int SsOpen = 0, SsMap = 1, SsRecPre = 2, SsRecReset = 3, SsRecUploads = 4, SsFrameBegin = 5, SsRaster = 6, SsPrepTop = 7,
        SsDirty = 8, SsComposite = 9, SsTail = 10, SsEndPre = 11, SsClose = 12, SsExecute = 13, SsSignal = 14, SsEndTail = 15, SsPhases = 16;
    private static readonly string[] s_ssNames =
    [
        "open", "map+glyphPrep", "rec.collect", "rec.reset", "rec.uploads", "frameBegin", "rasterTiles", "prepareTop", "ppDirty",
        "composite", "tail", "end.query", "end.close", "end.execute", "end.signal", "end.publish",
    ];
    private const int SdGroup = 0, SdBackdrop = 1, SdDirect = 2, SdTiles = 3, SdOcclude = 4, SdCompBind = 5, SdCompCover = 6, SdCompDraw = 7,
        SdPresPre = 8, SdPresCall = 9, SdPresSignal = 10, SdPresMem = 11, SdCount = 12;
    private static readonly string[] s_sdNames =
    [
        "prep.group", "prep.backdrop", "prep.direct", "prep.tiles", "prep.occlude", "comp.passBind", "comp.cover", "comp.draw",
        "pres.pre", "pres.call", "pres.signal", "pres.vram",
    ];
    // Spans inside the phases above (and the present, which runs after the submit): summed over the same window.
    private readonly long[] _sdTicks = new long[SdCount];
    private readonly long[] _ssTicks = new long[SsPhases];
    private long _ssT, _ssItems, _ssPlacements, _ssRasters;
    private int _ssN;
    private bool _ssLive;

    private void SsBegin(in CompositeFrame frame)
    {
        if (!EngineSwitches.SubmitSplit) return;
        _ssLive = true;
        _ssItems += frame.Items.Length; _ssPlacements += frame.Placements.Length; _ssRasters += frame.Rasters.Length;
        _ssT = Stopwatch.GetTimestamp();
    }

    /// <summary>Close the phase that ran since the previous mark.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Ss(int phase)
    {
        if (!_ssLive) return;
        long now = Stopwatch.GetTimestamp();
        _ssTicks[phase] += now - _ssT;
        _ssT = now;
    }

    /// <summary>A span's start (0 while the switch is off, which makes <see cref="Sd"/> a no-op).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long SdNow() => EngineSwitches.SubmitSplit ? Stopwatch.GetTimestamp() : 0;

    /// <summary>Add the time since <paramref name="t"/> to a span and restart it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Sd(int span, ref long t)
    {
        if (t == 0) return;
        long now = Stopwatch.GetTimestamp();
        _sdTicks[span] += now - t;
        t = now;
    }

    private void SsEnd()
    {
        if (!_ssLive) return;
        _ssLive = false;
        if (++_ssN < SsWindow) return;
        double us = 1e6 / Stopwatch.Frequency / _ssN;
        long total = 0;
        for (int i = 0; i < SsPhases; i++) total += _ssTicks[i];
        var sb = new StringBuilder(512);
        var c = CultureInfo.InvariantCulture;
        sb.Append("[sub.split] n=").Append(_ssN).Append(" totalUs=").Append((total * us).ToString("0.0", c))
          .Append(" items=").Append((_ssItems / (double)_ssN).ToString("0.0", c))
          .Append(" placements=").Append((_ssPlacements / (double)_ssN).ToString("0.0", c))
          .Append(" rasters=").Append((_ssRasters / (double)_ssN).ToString("0.00", c)).Append(" |");
        for (int i = 0; i < SsPhases; i++)
            sb.Append(' ').Append(s_ssNames[i]).Append('=').Append((_ssTicks[i] * us).ToString("0.0", c));
        sb.Append(" ||");
        for (int i = 0; i < SdCount; i++)
            sb.Append(' ').Append(s_sdNames[i]).Append('=').Append((_sdTicks[i] * us).ToString("0.0", c));
        sb.Append(" ||");
        for (int i = 0; i < SplitSpans.Count; i++)
            sb.Append(' ').Append(SplitSpans.Names[i]).Append('=').Append((SplitSpans.Ticks[i] * us).ToString("0.0", c));
        Console.Error.WriteLine(sb.ToString());
        Array.Clear(_ssTicks);
        Array.Clear(_sdTicks);
        Array.Clear(SplitSpans.Ticks);
        _ssN = 0; _ssItems = _ssPlacements = _ssRasters = 0;
    }
}
