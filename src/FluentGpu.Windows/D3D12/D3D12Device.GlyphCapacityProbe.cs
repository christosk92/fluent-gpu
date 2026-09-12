namespace FluentGpu.Rhi.D3D12;

public sealed unsafe partial class D3D12Device
{
    // Read-only observations for the explicit native pixel probe. No production policy switch.
    internal (int Edge, int Epoch, int OccupiedRows, bool Pending, int Dropped) GlyphCapacityProbeState
        => _glyphs is { } glyphs
            ? (glyphs.AtlasEdge, glyphs.AtlasResetCount, glyphs.AtlasOccupiedRows,
                glyphs.AtlasResetPending, glyphs.DroppedInstances)
            : default;
}
