namespace FluentGpu.Text;

/// <summary>Glyph upload reserve sizing. Cold first paint keeps a generous bank; a warm, clean atlas returns it
/// after the backend's submitted-frame idle window. Resource replacement still requires that bank's fence.</summary>
public static class GlyphStagingPolicy
{
    public const int InitialRows = 1024;
    public const int WarmRows = 256;
    public const int MaxRows = 2048;

    public static int AfterIdle(int rows, int cleanFrames, int idleThreshold)
        => cleanFrames >= idleThreshold && rows > WarmRows ? WarmRows : rows;

    public static int ForDemand(int rows, int wantedRows)
    {
        while (rows < wantedRows && rows < MaxRows) rows <<= 1;
        return rows > MaxRows ? MaxRows : rows;
    }
}
