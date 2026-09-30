using System;
using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Scroll.Runtime;

namespace FluentGpu.ScrollLab.Surfaces;

/// <summary>The lab's measurement surfaces (phase 1; Shelves/Sticky/SnapPager land in phase 3).</summary>
public enum ScrollSurfaceKind : byte
{
    FixedList100k = 0,
    MeasuredList = 1,
    EdgeCases = 2,
}

/// <summary>The surface catalog: display names + one factory per kind, every surface driven by a caller-owned
/// <see cref="ScrollHandle"/> (the lab reads its offset/motion signals and filters the probe by its viewport).</summary>
public static class ScrollSurfaces
{
    public static IReadOnlyList<(ScrollSurfaceKind Kind, string Name, string Slug)> All { get; } = new[]
    {
        (ScrollSurfaceKind.FixedList100k, "Fixed 100k", "fixed100k"),
        (ScrollSurfaceKind.MeasuredList, "Measured", "measured"),
        (ScrollSurfaceKind.EdgeCases, "Edge cases", "edge"),
    };

    public static string NameOf(ScrollSurfaceKind kind) => All[(int)kind].Name;

    /// <summary>A file-name-safe short id (session folder names).</summary>
    public static string SlugOf(ScrollSurfaceKind kind) => All[(int)kind].Slug;

    public static Element Create(ScrollSurfaceKind kind, ScrollHandle handle) => kind switch
    {
        ScrollSurfaceKind.FixedList100k => FixedList100k.Create(handle),
        ScrollSurfaceKind.MeasuredList => MeasuredList.Create(handle),
        ScrollSurfaceKind.EdgeCases => EdgeCases.Create(handle),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
