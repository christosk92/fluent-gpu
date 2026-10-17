using System;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using FluentGpu.Foundation;
using FluentGpu.Render;
using FluentGpu.Rhi;
using FluentGpu.Rhi.D3D12;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// The frame freeze's D3D12 half (E2, technical-plan §4 E10 / EP10; threading-render-seam.md §10.1): the snapshot is the presented
/// frame with its alpha, a dissolve present is <c>(1 - t)·S + t·N</c> on all four premultiplied channels (FrameDissolve.Blend, not
/// src-over), alpha-0 holes in both frames stay holes, the snapshot is identical before and after the dissolve, a dissolve present
/// and the present after it are whole, a held present draws and flips nothing, and the snapshot's texture is released on dispose.
/// Pixels are read back from a real D3D12 swapchain on the WARP adapter (the same result on every machine); skipped where WARP or
/// a presentable window is unavailable.
/// </summary>
public sealed unsafe partial class FrameDissolveTests
{
    private const int W = 64, H = 48;

    // S: opaque red left, a half-transparent green band, a hole (transparent clear) on the right. N: opaque blue right half,
    // a different translucent band, the same hole region kept transparent. Both are drawn over a TRANSPARENT clear, as a Mica window is.
    private static readonly RectF Hole = new(48, 4, 12, 12);

    private static DrawList FrameS()
    {
        var d = new DrawList();
        d.FillRoundRect(new RectF(0, 0, 32, 48), default, new ColorF(0.9f, 0.1f, 0.1f, 1f), Affine2D.Identity, 1f);
        d.FillRoundRect(new RectF(0, 20, 44, 10), default, new ColorF(0.1f, 0.4f, 0.1f, 0.5f), Affine2D.Identity, 1f);
        return d;
    }

    private static DrawList FrameN()
    {
        var d = new DrawList();
        d.FillRoundRect(new RectF(16, 0, 28, 48), default, new ColorF(0.1f, 0.2f, 0.9f, 1f), Affine2D.Identity, 1f);
        d.FillRoundRect(new RectF(0, 30, 44, 12), default, new ColorF(0.3f, 0.3f, 0.0f, 0.6f), Affine2D.Identity, 1f);
        d.FillRoundRect(new RectF(0, 44, 64, 4), default, new ColorF(1f, 1f, 1f, 1f), Affine2D.Identity, 1f);
        return d;
    }

    [Fact]
    public void DissolveFrames_EqualTheFormula_OnColourAndAlpha_HolesStayHoles_AndTheSnapshotIsUnchanged() => OnWarpTarget((device, sc) =>
    {
        Submit(device, sc, FrameS(), 1);
        IFrameSnapshot? captured = sc.CaptureSnapshot();
        Assert.NotNull(captured);
        using IFrameSnapshot snap = captured;
        Assert.Equal(new Size2(W, H), snap.SizePx);
        Assert.True(snap.PremultipliedAlpha);
        sc.Present();
        Assert.False(sc.LastPresentDissolved);
        byte[] presentedS = device.CaptureBgra(out _, out _);
        byte[] s = device.ReadFrameSnapshotBgra(snap);
        Assert.Equal(presentedS, s);                              // the snapshot is the presented frame, alpha included
        AssertHole(s, "S");
        Assert.Contains(s, b => b != 0 && b != 255);              // the translucent band: alpha strictly between 0 and 1

        ulong seq = 2;
        foreach (float t in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
        {
            Submit(device, sc, FrameN(), seq++);
            byte[] n = device.CaptureBgra(out _, out _);
            AssertHole(n, "N");
            sc.DissolveNextPresent(snap, t);
            sc.Present();
            Assert.False(sc.LastPresentStoodDown);
            Assert.True(sc.LastPresentDissolved);
            Assert.True(sc.PpNeedFullPresent, "the present after a dissolve must be whole");
            Assert.Equal(0, sc.PpPresentCount);
            byte[] o = device.CaptureBgra(out _, out _);
            AssertFormula(s, n, o, t);
            if (t is > 0f and < 1f) { Assert.NotEqual(n, o); Assert.NotEqual(s, o); }
            AssertHole(o, $"dissolve t={t}");
        }

        // Snapshot pixels are identical across the dissolve: it is read, never written.
        Assert.Equal(s, device.ReadFrameSnapshotBgra(snap));

        // The present after the last dissolve is an ordinary one (no arm): not a dissolve, and N reaches the glass as drawn.
        Submit(device, sc, FrameN(), seq);
        byte[] last = device.CaptureBgra(out _, out _);
        sc.Present();
        Assert.False(sc.LastPresentDissolved);
        Assert.Equal(last, device.CaptureBgra(out _, out _));
    });

    [Fact]
    public void AHeldPresent_ConsumesTheArmedDissolve_WithoutDrawingOrFlipping() => OnWarpTarget((device, sc) =>
    {
        Submit(device, sc, FrameS(), 1);
        using IFrameSnapshot snap = sc.CaptureSnapshot()!;
        sc.Present();
        long dissolvedBefore = device.DissolvedPresentCount;

        Submit(device, sc, FrameN(), 2);
        byte[] n = device.CaptureBgra(out _, out _);
        sc.HoldNextPresent();
        sc.DissolveNextPresent(snap, 0.5f);
        sc.Present();
        Assert.True(sc.LastPresentHeld);
        Assert.False(sc.LastPresentDissolved);
        Assert.Equal(n, device.CaptureBgra(out _, out _));          // nothing was lerped into the held frame
        Assert.Equal(dissolvedBefore, device.DissolvedPresentCount);

        sc.Present();                                                 // the armed dissolve was one-shot: this one is plain
        Assert.False(sc.LastPresentHeld);
        Assert.False(sc.LastPresentDissolved);
        Assert.Equal(n, device.CaptureBgra(out _, out _));
    });

    [Fact]
    public void AReappliedDissolve_OverTheSameSubmit_DoesNotLerpTwice() => OnWarpTarget((device, sc) =>
    {
        // A refused non-blocking present keeps the lerped frame in the back buffer and the host re-presents it, re-arming the same
        // dissolve: the back buffer must stay (1 - t)·S + t·N, not become a dissolve of the dissolve.
        Submit(device, sc, FrameS(), 1);
        using IFrameSnapshot snap = sc.CaptureSnapshot()!;
        byte[] s = device.ReadFrameSnapshotBgra(snap);
        sc.Present();
        Submit(device, sc, FrameN(), 2);
        byte[] n = device.CaptureBgra(out _, out _);
        var d = (D3D12FrameSnapshot)snap;
        Assert.True(device.ApplyFrameDissolve(sc, d, 0.4f));
        Assert.True(device.ApplyFrameDissolve(sc, d, 0.4f));
        AssertFormula(s, n, device.CaptureBgra(out _, out _), 0.4f);
    });

    [Fact]
    public void ASnapshotOfAnotherSize_OrADisposedOne_PresentsTheFrameAsIs_AndDisposeReleasesTheTexture() => OnWarpTarget((device, sc) =>
    {
        Submit(device, sc, FrameS(), 1);
        IFrameSnapshot snap = sc.CaptureSnapshot()!;
        sc.Present();
        Assert.Equal(1, device.FrameSnapshotsHeld);

        sc.Resize(new Size2(W + 8, H));
        Submit(device, sc, FrameN(), 2, W + 8);
        byte[] n = device.CaptureBgra(out _, out _);
        sc.DissolveNextPresent(snap, 0.5f);
        sc.Present();
        Assert.False(sc.LastPresentDissolved);
        Assert.Equal(n, device.CaptureBgra(out _, out _));

        snap.Dispose();
        device.WaitForGpu();
        sc.Present();                                                // drains anything still waiting on its fence
        Assert.Equal(0, device.FrameSnapshotsHeld);
        Submit(device, sc, FrameN(), 3, W + 8);
        sc.DissolveNextPresent(snap, 0.5f);                          // a disposed snapshot: the frame presents as it is
        sc.Present();
        Assert.False(sc.LastPresentDissolved);
    });

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private static void Submit(D3D12Device device, D3D12Swapchain sc, DrawList draw, ulong seq, int w = W)
    {
        RepaintDamageRegion damage = default;
        damage.ForceFull(RepaintFullReason.TargetInvalidated);
        var info = new FrameInfo(new Size2(w, H), 1f, default, RepaintDamage: damage, PublishSequence: seq);   // a transparent clear
        device.SubmitDrawList(draw.Bytes, draw.SortKeys, in info, sc);
    }

    /// <summary>Every channel of every pixel within one 8-bit step of <c>(1 - t)·S + t·N</c> (FrameDissolve.Blend).</summary>
    private static void AssertFormula(byte[] s, byte[] n, byte[] o, float t)
    {
        Assert.Equal(s.Length, o.Length);
        int worst = 0, at = -1;
        for (int i = 0; i < o.Length; i++)
        {
            var c = FrameDissolve.Blend(new ColorF(s[i] / 255f, 0, 0, 0), new ColorF(n[i] / 255f, 0, 0, 0), t);
            int expect = (int)MathF.Round(c.R * 255f);
            int err = Math.Abs(expect - o[i]);
            if (err > worst) { worst = err; at = i; }
        }
        if (worst > 1) Assert.Fail($"t={t}: channel {at % 4} of pixel {at / 4} is {o[at]}, the formula gives (1-t)·{s[at]} + t·{n[at]}");
    }

    private static void AssertHole(byte[] px, string what)
    {
        for (int y = (int)Hole.Y; y < (int)(Hole.Y + Hole.H); y++)
            for (int x = (int)Hole.X; x < (int)(Hole.X + Hole.W); x++)
            {
                int i = (y * W + x) * 4;
                Assert.True(px[i] == 0 && px[i + 1] == 0 && px[i + 2] == 0 && px[i + 3] == 0, $"{what}: hole pixel ({x},{y}) is not transparent");
            }
    }

    private static void OnWarpTarget(Action<D3D12Device, D3D12Swapchain> body)
    {
        ExceptionDispatchInfo? failure = null;
        string? skip = null;
        var thread = new Thread(() =>
        {
            nint hwnd = 0;
            D3D12Device? device = null;
            try
            {
                // A visible, unactivated tool window far off every monitor: presentable (not iconic, hidden or cloaked), never seen.
                hwnd = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, "STATIC", "", WS_POPUP, -30000, -30000, W, H, 0, 0, GetModuleHandleW(null), 0);
                if (hwnd == 0) { skip = "no window"; return; }
                ShowWindow(hwnd, SW_SHOWNOACTIVATE);
                device = new D3D12Device(new StringTable()) { ForceWarpAdapter = true };
                D3D12Swapchain sc;
                try
                {
                    device.EnsureDeviceCreated();
                    if (!device.BackendName.EndsWith("(WARP)", StringComparison.Ordinal)) { skip = "no WARP adapter"; return; }
                    sc = (D3D12Swapchain)device.CreateSwapchain(new SwapchainDesc(new NativeHandle(hwnd, NativeHandleKind.Hwnd), new Size2(W, H)));
                }
                catch (InvalidOperationException e) { skip = "no D3D12 device: " + e.Message; return; }
                // The first present proves the window is presentable here (a stood-down present would make every check vacuous).
                Submit(device, sc, new DrawList(), 0);
                sc.Present();
                if (sc.LastPresentStoodDown) { skip = "the test window cannot present"; return; }
                Assert.True(sc.SupportsFrameDissolve);
                body(device, sc);
            }
            catch (Exception e) { failure = ExceptionDispatchInfo.Capture(e); }
            finally
            {
                device?.Dispose();
                if (hwnd != 0) DestroyWindow(hwnd);
            }
        });
        thread.Start();
        thread.Join();
        failure?.Throw();
        if (skip is not null) Assert.Skip(skip);
    }

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const int SW_SHOWNOACTIVATE = 4;

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? name);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h,
        nint parent, nint menu, nint hinst, nint param);

    [LibraryImport("user32.dll", EntryPoint = "ShowWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hwnd, int cmd);

    [LibraryImport("user32.dll", EntryPoint = "DestroyWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint hwnd);
}
