using System;
using FluentGpu.Dsl;                              // AccentRamp
using TerraFX.Interop.WinRT;                      // IInspectable, IUISettings3, UIColorType, Color, HSTRING, RO_INIT_TYPE
using static TerraFX.Interop.Windows.Windows;     // __uuidof<T>
using static TerraFX.Interop.WinRT.WinRT;         // RoInitialize / RoActivateInstance / WindowsCreate|DeleteString
using ColorF = FluentGpu.Foundation.ColorF;       // alias so the WinRT `Color` struct stays unambiguous

namespace FluentGpu.Pal.Windows;

public static partial class Win32Theme
{
    // combase apartment init is process-wide and idempotent; init once, tolerate the benign "already initialized" codes.
    private static bool s_accentRoInit;
    private const int S_FALSE = 1;
    private const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);

    /// <summary>
    /// The FULL OS accent ramp — the seven system shades WinUI keys its accent brushes off (<c>SystemAccentColor</c> +
    /// <c>Light1..3</c> + <c>Dark1..3</c>), read straight from <c>Windows.UI.ViewManagement.UISettings</c> via
    /// <c>IUISettings3.GetColorValue</c>. Hand-vtable WinRT interop through <c>TerraFX.Interop.WinRT</c> — zero CsWinRT,
    /// zero <c>ComWrappers</c> on the call-out path — the exact pattern proven by <c>D3D12/CompositionBackdrop.cs</c>.
    /// Returns <see langword="null"/> on any failure (old OS / activation refused); callers then fall back to
    /// <see cref="AccentLight2"/>/<see cref="Accent"/> + <see cref="AccentRamp.Derive"/>. UI-thread only.
    /// </summary>
    public static unsafe AccentRamp? ReadAccentRamp()
    {
        IInspectable* insp = null;
        IUISettings3* settings = null;
        try
        {
            if (!s_accentRoInit)
            {
                int hrInit = RoInitialize(RO_INIT_TYPE.RO_INIT_SINGLETHREADED);
                if (hrInit < 0 && hrInit != S_FALSE && hrInit != RPC_E_CHANGED_MODE) return null;
                s_accentRoInit = true;
            }

            const string cls = "Windows.UI.ViewManagement.UISettings";
            HSTRING clsId;
            fixed (char* p = cls)
                if (WindowsCreateString(p, (uint)cls.Length, &clsId) < 0) return null;
            try
            {
                if (RoActivateInstance(clsId, &insp) < 0 || insp is null) return null;
            }
            finally { WindowsDeleteString(clsId); }

            Guid iid = __uuidof<IUISettings3>();
            if (insp->QueryInterface(&iid, (void**)&settings) < 0 || settings is null) return null;

            if (!TryColor(settings, UIColorType.UIColorType_Accent, out var baseC)) return null;
            if (!TryColor(settings, UIColorType.UIColorType_AccentLight1, out var l1)) return null;
            if (!TryColor(settings, UIColorType.UIColorType_AccentLight2, out var l2)) return null;
            if (!TryColor(settings, UIColorType.UIColorType_AccentLight3, out var l3)) return null;
            if (!TryColor(settings, UIColorType.UIColorType_AccentDark1, out var d1)) return null;
            if (!TryColor(settings, UIColorType.UIColorType_AccentDark2, out var d2)) return null;
            if (!TryColor(settings, UIColorType.UIColorType_AccentDark3, out var d3)) return null;

            return new AccentRamp(baseC, l1, l2, l3, d1, d2, d3);
        }
        catch { return null; }   // never let a WinRT hiccup take down startup — the Derive fallback covers it
        finally
        {
            if (settings != null) settings->Release();
            if (insp != null) insp->Release();
        }
    }

    // The OS accent shades are opaque; map the WinRT Color (bytes A/R/G/B) to an opaque ColorF.
    private static unsafe bool TryColor(IUISettings3* s, UIColorType type, out ColorF c)
    {
        Color v;
        if (s->GetColorValue(type, &v) < 0) { c = default; return false; }
        c = ColorF.FromRgba(v.R, v.G, v.B);
        return true;
    }
}
