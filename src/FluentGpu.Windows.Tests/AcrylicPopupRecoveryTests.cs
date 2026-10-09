using System;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using FluentGpu.Foundation;
using FluentGpu.Rhi;
using FluentGpu.Rhi.D3D12;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// A device-loss recovery rebuilds every swapchain in place. A desktop-acrylic popup is Composited, but its content is hosted by
/// a Windows.UI.Composition backdrop on its HWND, never by DirectComposition. The recovery used to rebind a DComp target for
/// every Composited swapchain, so an open acrylic menu got a second, topmost DComp copy of its content over the WUC tree (no
/// rounded clip, no open/close motion), or the bind threw out of the recovery and left the device marked lost. Device-backed:
/// a real D3D12 device on hidden popup HWNDs; skipped where D3D12 or the compositor is unavailable.
/// </summary>
public sealed unsafe partial class AcrylicPopupRecoveryTests
{
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_NOREDIRECTIONBITMAP = 0x00200000;

    [Fact]
    public void Recovery_RebuildsAnAcrylicPopupsBackdrop_WithoutADCompTarget() => OnOwnThread(() =>
    {
        // Own thread: the WUC compositor binds a DispatcherQueue to the thread that first creates it (one acrylic fact per process).
        nint hwnd = NewPopupHwnd();
        var device = new D3D12Device(new StringTable());
        try
        {
            var desc = new SwapchainDesc(new NativeHandle(hwnd, NativeHandleKind.Hwnd), new Size2(64, 48),
                Composited: true, DesktopAcrylic: true, AcrylicTint: new ColorF(0.1f, 0.1f, 0.1f, 0.6f), CornerRadiusPx: 8f);
            var popup = CreateOrSkip(device, desc);
            Assert.NotNull(popup.Backdrop);
            Assert.True(popup.DcompTarget == null);

            device.RecoverDevice();   // bound a topmost DComp target over the WUC tree (or threw) before the fix

            Assert.NotNull(popup.Backdrop);
            Assert.True(popup.DcompTarget == null, "an acrylic popup must not get a DirectComposition target on recovery");
            Assert.True(popup.DcompVisual == null);
            Assert.Equal(0, device.PollDeviceLost());
        }
        finally { device.Dispose(); DestroyWindow(hwnd); }
    });

    [Fact]
    public void Recovery_StillRebindsAPlainCompositedSwapchain() => OnOwnThread(() =>
    {
        nint hwnd = NewPopupHwnd();
        var device = new D3D12Device(new StringTable());
        try
        {
            var sc = CreateOrSkip(device, new SwapchainDesc(new NativeHandle(hwnd, NativeHandleKind.Hwnd), new Size2(64, 48), Composited: true));
            Assert.True(sc.DcompBindPending);   // deferred to the first Present

            device.RecoverDevice();

            Assert.True(sc.DcompTarget != null, "a composited window is rebound eagerly on recovery");
            Assert.False(sc.DcompBindPending);
            Assert.Null(sc.Backdrop);
            Assert.Equal(0, device.PollDeviceLost());
        }
        finally { device.Dispose(); DestroyWindow(hwnd); }
    });

    private static D3D12Swapchain CreateOrSkip(D3D12Device device, in SwapchainDesc desc)
    {
        try
        {
            device.PrepareSwapchainCreate(desc);
            return (D3D12Swapchain)device.CreateSwapchain(desc);
        }
        catch (InvalidOperationException e)
        {
            Assert.Skip($"no D3D12 device / compositor on this machine: {e.Message}");
            throw;
        }
    }

    private static nint NewPopupHwnd()
    {
        nint hwnd = CreateWindowExW(WS_EX_NOREDIRECTIONBITMAP | WS_EX_TOOLWINDOW, "STATIC", "", WS_POPUP, 0, 0, 64, 48, 0, 0, GetModuleHandleW(null), 0);
        Assert.NotEqual(0, hwnd);
        return hwnd;
    }

    private static void OnOwnThread(Action body)
    {
        ExceptionDispatchInfo? failure = null;
        var t = new Thread(() => { try { body(); } catch (Exception e) { failure = ExceptionDispatchInfo.Capture(e); } });
        t.Start();
        t.Join();
        failure?.Throw();
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? name);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h,
        nint parent, nint menu, nint hinst, nint param);

    [LibraryImport("user32.dll", EntryPoint = "DestroyWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint hwnd);
}
