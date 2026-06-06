namespace FluentGpu.Pal.Windows;

/// <summary>
/// SCAFFOLD (the reference Windows PAL — see design/subsystems/pal-rhi.md §5.1). Will implement
/// <c>FluentGpu.Pal.IPlatformApp/IPlatformWindow</c> over pure Win32: <c>RegisterClassExW</c>, a static
/// <c>[UnmanagedCallersOnly]</c> <c>WndProc</c> dispatched via a <c>GCHandle</c>, PerMonitorV2 DPI, the WM_* → POD
/// <c>InputEventRing</c> pump, <c>SetCursor</c>, and the DComp present target. Headless PAL backs the slice today.
/// </summary>
public static class WindowsBackend;
