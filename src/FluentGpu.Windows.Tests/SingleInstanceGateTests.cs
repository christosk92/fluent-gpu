using System;
using System.Runtime.InteropServices;
using FluentGpu.Pal;
using FluentGpu.WindowsApi.Activation;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// The profile-scoped single-instance redirect (docs/plans/evidence-diagnostics-implementation.md §A.7): a secondary launch
/// hands its activation to the window TAGGED with its own instance id, falls back to an untagged window (a primary built
/// before the tag), and never to a window of ANOTHER instance — so a verify profile runs beside the user's own Wavee and
/// neither swallows the other's launches. The redirect test uses real message-only windows of a test-unique class.
/// </summary>
public sealed partial class SingleInstanceGateTests
{
    [Fact]
    public void ChooseRedirectTarget_PrefersItsOwnTag_ThenAnUntaggedWindow_NeverAnotherInstance()
    {
        uint mine = InstanceIdentity.TagOf("Wavee"), other = InstanceIdentity.TagOf("Wavee.1234abcd");
        Assert.Equal(1, SingleInstanceGate.ChooseRedirectTarget([other, mine, 0u], mine));
        Assert.Equal(1, SingleInstanceGate.ChooseRedirectTarget([other, 0u], mine));
        Assert.Equal(-1, SingleInstanceGate.ChooseRedirectTarget([other], mine));
        Assert.Equal(-1, SingleInstanceGate.ChooseRedirectTarget([], mine));
    }

    [Fact]
    public void TagOf_IsStableNonZeroAndDistinguishesProfiles()
    {
        Assert.Equal(InstanceIdentity.TagOf("Wavee"), InstanceIdentity.TagOf("Wavee"));
        Assert.NotEqual(InstanceIdentity.TagOf("Wavee"), InstanceIdentity.TagOf("Wavee.0f0f0f0f"));
        Assert.NotEqual(0u, InstanceIdentity.TagOf(""));
    }

    [Fact]
    public void RedirectTargetsTheWindowOfItsOwnInstance()
    {
        string cls = "FgInstanceGateTest." + Guid.NewGuid().ToString("N");
        nint defProc = NativeLibrary.GetExport(NativeLibrary.Load("user32.dll"), "DefWindowProcW");
        nint hinst = GetModuleHandleW(null);
        nint clsName = Marshal.StringToHGlobalUni(cls);
        nint a = 0, b = 0, c = 0;
        try
        {
            var wc = new WNDCLASSEXW { cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(), lpfnWndProc = defProc, hInstance = hinst, lpszClassName = clsName };
            Assert.NotEqual(0, RegisterClassExW(in wc));
            a = CreateWindowExW(0, cls, "owner", 0, 0, 0, 0, 0, HwndMessage, 0, hinst, 0);
            b = CreateWindowExW(0, cls, "verify", 0, 0, 0, 0, 0, HwndMessage, 0, hinst, 0);
            c = CreateWindowExW(0, cls, "legacy", 0, 0, 0, 0, 0, HwndMessage, 0, hinst, 0);
            Assert.True(a != 0 && b != 0 && c != 0);
            Assert.True(SetPropW(a, InstanceIdentity.WindowPropertyName, (nint)InstanceIdentity.TagOf("Wavee")));
            Assert.True(SetPropW(b, InstanceIdentity.WindowPropertyName, (nint)InstanceIdentity.TagOf("Wavee.verify")));

            Assert.Equal(a, SingleInstanceGate.FindRedirectTarget(cls, "Wavee", HwndMessage));
            Assert.Equal(b, SingleInstanceGate.FindRedirectTarget(cls, "Wavee.verify", HwndMessage));
            // an id no window carries: the untagged (pre-tag) window, never a window of another instance
            Assert.Equal(c, SingleInstanceGate.FindRedirectTarget(cls, "Wavee.someone-else", HwndMessage));
            DestroyWindow(c); c = 0;
            Assert.Equal(0, SingleInstanceGate.FindRedirectTarget(cls, "Wavee.someone-else", HwndMessage));
        }
        finally
        {
            if (a != 0) { RemovePropW(a, InstanceIdentity.WindowPropertyName); DestroyWindow(a); }
            if (b != 0) { RemovePropW(b, InstanceIdentity.WindowPropertyName); DestroyWindow(b); }
            if (c != 0) DestroyWindow(c);
            UnregisterClassW(cls, hinst);
            Marshal.FreeHGlobal(clsName);
        }
    }

    // ── Win32 (message-only windows; test-local declarations) ───────────────────────────────────────────────────────

    private static readonly nint HwndMessage = -3;

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public nint lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground, lpszMenuName, lpszClassName, hIconSm;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? name);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW")]
    private static partial ushort RegisterClassExW(in WNDCLASSEXW wc);

    [LibraryImport("user32.dll", EntryPoint = "UnregisterClassW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterClassW(string cls, nint hinst);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h,
        nint parent, nint menu, nint hinst, nint param);

    [LibraryImport("user32.dll", EntryPoint = "DestroyWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "SetPropW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetPropW(nint hwnd, string name, nint data);

    [LibraryImport("user32.dll", EntryPoint = "RemovePropW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint RemovePropW(nint hwnd, string name);
}
