// Dumps the Windows PVL (uxtheme "Animations" class) storyboards that WinUI's ThemeGenerator reads at runtime
// (dxaml ThemeGenerator.cpp AddTimelines: OpenThemeData(NULL, L"Animations") + GetThemeAnimationTransform +
// GetThemeTimingFunction). This is the ground truth for PopupThemeTransition (TAS_SHOWPOPUP/TAS_HIDEPOPUP) and
// FadeIn/FadeOutThemeAnimation (TAS_FADEIN/TAS_FADEOUT) timings, which are NOT in the microsoft-ui-xaml repo.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

internal static class PvlDump
{
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr OpenThemeData(IntPtr hwnd, string classList);
    [DllImport("uxtheme.dll")]
    static extern int CloseThemeData(IntPtr hTheme);
    [DllImport("uxtheme.dll")]
    static extern int GetThemeAnimationProperty(IntPtr hTheme, int storyboardId, int targetId, int property, out uint value, uint cbSize, out uint cbSizeOut);
    [DllImport("uxtheme.dll")]
    static extern int GetThemeAnimationTransform(IntPtr hTheme, int storyboardId, int targetId, uint index, IntPtr pTransform, uint cbSize, out uint cbSizeOut);
    [DllImport("uxtheme.dll")]
    static extern int GetThemeTimingFunction(IntPtr hTheme, int timingFunctionId, IntPtr pTiming, uint cbSize, out uint cbSizeOut);

    const int TAP_TRANSFORMCOUNT = 1;
    static readonly string[] TransformType = ["TRANSLATE_2D", "SCALE_2D", "OPACITY", "CLIP"];

    static void Main()
    {
        var hAnim = OpenThemeData(IntPtr.Zero, "Animations");
        var hTiming = OpenThemeData(IntPtr.Zero, "timingfunction");
        Console.WriteLine($"hAnim={hAnim} hTiming={hTiming}");
        Dump(hAnim, hTiming, "TAS_SHOWPOPUP/TARGET", 18, 1);
        Dump(hAnim, hTiming, "TAS_HIDEPOPUP/TARGET", 19, 1);
        Dump(hAnim, hTiming, "TAS_FADEIN/SHOWN", 4, 1);
        Dump(hAnim, hTiming, "TAS_FADEOUT/HIDDEN", 5, 1);
        CloseThemeData(hAnim);
        CloseThemeData(hTiming);
    }

    static void Dump(IntPtr hAnim, IntPtr hTiming, string name, int storyboard, int target)
    {
        int hr = GetThemeAnimationProperty(hAnim, storyboard, target, TAP_TRANSFORMCOUNT, out uint count, 4, out _);
        Console.WriteLine($"--- {name} (sb={storyboard}, tgt={target}) hr=0x{hr:X} transforms={count}");
        for (uint i = 0; i < count; i++)
        {
            GetThemeAnimationTransform(hAnim, storyboard, target, i, IntPtr.Zero, 0, out uint size);
            var buf = Marshal.AllocHGlobal((int)size);
            try
            {
                hr = GetThemeAnimationTransform(hAnim, storyboard, target, i, buf, size, out _);
                if (hr != 0) { Console.WriteLine($"  [{i}] hr=0x{hr:X}"); continue; }
                int type = Marshal.ReadInt32(buf, 0);
                int timingId = Marshal.ReadInt32(buf, 4);
                int start = Marshal.ReadInt32(buf, 8);
                int dur = Marshal.ReadInt32(buf, 12);
                int flags = Marshal.ReadInt32(buf, 16);
                string bezier = Timing(hTiming, timingId);
                string body = type switch
                {
                    0 or 1 => Floats(buf, 20, 6, ["x", "y", "x0", "y0", "ox", "oy"]),       // TA_TRANSFORM_2D
                    2 => Floats(buf, 20, 2, ["opacity", "opacity0"]),                      // TA_TRANSFORM_OPACITY
                    3 => Floats(buf, 20, 8, ["l", "t", "r", "b", "l0", "t0", "r0", "b0"]), // TA_TRANSFORM_CLIP
                    _ => "?",
                };
                Console.WriteLine($"  [{i}] {TransformType[type]} start={start}ms dur={dur}ms flags=0x{flags:X} timing#{timingId}={bezier} {body}");
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }

    static string Floats(IntPtr buf, int offset, int n, string[] names)
    {
        var parts = new List<string>(n);
        for (int i = 0; i < n; i++)
        {
            float f = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(buf, offset + i * 4));
            parts.Add($"{names[i]}={f:0.###}");
        }
        return string.Join(" ", parts);
    }

    static string Timing(IntPtr hTiming, int id)
    {
        GetThemeTimingFunction(hTiming, id, IntPtr.Zero, 0, out uint size);
        if (size == 0) return "(none)";
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            int hr = GetThemeTimingFunction(hTiming, id, buf, size, out _);
            if (hr != 0) return $"hr=0x{hr:X}";
            int type = Marshal.ReadInt32(buf, 0);
            if (type != 1) return $"type={type}";
            float x0 = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(buf, 4));
            float y0 = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(buf, 8));
            float x1 = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(buf, 12));
            float y1 = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(buf, 16));
            return $"cubic-bezier({x0:0.###},{y0:0.###},{x1:0.###},{y1:0.###})";
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}
