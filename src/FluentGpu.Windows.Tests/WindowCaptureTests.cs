using FluentGpu.Hosting;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// The pure halves of <c>--fg shot-window</c> / <c>FluentApp.CaptureWindowPng</c>: the capture rectangle (the DWM frame
/// bounds grown by the padding, clipped to the virtual screen — negative multi-monitor coordinates included), the
/// schedule (arm at the first presented frame, capture once after the delay) and the switch parsing. No window, no GDI.
/// </summary>
public sealed class WindowCaptureTests
{
    private static readonly CaptureRect Primary = new(0, 0, 1920, 1080);
    // A second monitor LEFT of and above the primary: the virtual screen starts at negative coordinates.
    private static readonly CaptureRect TwoMonitors = new(-2560, -360, 1920, 1080);

    [Fact]
    public void An_on_screen_window_is_captured_at_its_frame_bounds()
    {
        Assert.True(WindowCapture.TryCaptureRect(new CaptureRect(100, 200, 900, 800), 0, Primary, out CaptureRect r));
        Assert.Equal(new CaptureRect(100, 200, 900, 800), r);
        Assert.Equal(800, r.Width);
        Assert.Equal(600, r.Height);
    }

    [Fact]
    public void Padding_grows_every_side()
    {
        Assert.True(WindowCapture.TryCaptureRect(new CaptureRect(100, 200, 900, 800), 16, Primary, out CaptureRect r));
        Assert.Equal(new CaptureRect(84, 184, 916, 816), r);
    }

    [Fact]
    public void Negative_padding_is_none()
    {
        Assert.True(WindowCapture.TryCaptureRect(new CaptureRect(100, 200, 900, 800), -5, Primary, out CaptureRect r));
        Assert.Equal(new CaptureRect(100, 200, 900, 800), r);
    }

    [Fact]
    public void Padding_and_a_partly_off_screen_window_clip_to_the_virtual_screen()
    {
        Assert.True(WindowCapture.TryCaptureRect(new CaptureRect(-50, 1000, 300, 1300), 8, Primary, out CaptureRect r));
        Assert.Equal(new CaptureRect(0, 992, 308, 1080), r);
    }

    [Fact]
    public void A_window_on_a_monitor_at_negative_coordinates_keeps_them()
    {
        Assert.True(WindowCapture.TryCaptureRect(new CaptureRect(-2000, -300, -1000, 400), 4, TwoMonitors, out CaptureRect r));
        Assert.Equal(new CaptureRect(-2004, -304, -996, 404), r);
        Assert.True(WindowCapture.TryCaptureRect(new CaptureRect(-2600, -400, -2000, 0), 0, TwoMonitors, out r));
        Assert.Equal(new CaptureRect(-2560, -360, -2000, 0), r);   // clipped at the far-left, top edge
    }

    [Fact]
    public void A_window_entirely_off_the_virtual_screen_or_empty_is_not_captured()
    {
        Assert.False(WindowCapture.TryCaptureRect(new CaptureRect(2000, 0, 2400, 300), 0, Primary, out _));
        Assert.False(WindowCapture.TryCaptureRect(new CaptureRect(-400, 0, -10, 300), 4, Primary, out _));   // pad 4 still short of 0
        Assert.False(WindowCapture.TryCaptureRect(new CaptureRect(100, 100, 100, 300), 0, Primary, out _));
        Assert.False(WindowCapture.TryCaptureRect(new CaptureRect(0, 0, 10, 10), 0, default, out _));
    }

    [Fact]
    public void Huge_padding_does_not_overflow()
    {
        Assert.True(WindowCapture.TryCaptureRect(new CaptureRect(int.MinValue + 1, 0, int.MaxValue - 1, 10), int.MaxValue, Primary, out CaptureRect r));
        Assert.Equal(Primary, r);
    }

    // ── black-grab detection (display off / desktop switch) ─────────────────────────────────────────────────────────

    [Fact]
    public void An_all_black_grab_is_detected_whatever_the_undefined_fourth_byte()
    {
        Assert.True(WindowCapture.IsAllBlack(new byte[16]));
        Assert.True(WindowCapture.IsAllBlack(new byte[] { 0, 0, 0, 255, 0, 0, 0, 7 }));   // BI_RGB leaves the x byte undefined
    }

    [Fact]
    public void Any_lit_channel_or_an_empty_grab_is_not_all_black()
    {
        Assert.False(WindowCapture.IsAllBlack(new byte[] { 0, 0, 0, 0, 0, 1, 0, 0 }));
        Assert.False(WindowCapture.IsAllBlack(new byte[] { 0, 0, 9, 0 }));
        Assert.False(WindowCapture.IsAllBlack(Array.Empty<byte>()));
    }

    // ── schedule ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Arms_at_the_first_presented_frame_and_captures_once_after_the_delay()
    {
        var s = new WindowShotSchedule(startMs: 1000, delayMs: 1500);
        Assert.Equal(WindowShotStep.Wait, s.Step(presented: false, 1100));
        Assert.Equal(WindowShotStep.Armed, s.Step(presented: true, 1200));
        Assert.Equal(WindowShotStep.Wait, s.Step(presented: true, 2699));
        Assert.Equal(WindowShotStep.Capture, s.Step(presented: true, 2700));
        Assert.Equal(WindowShotStep.Done, s.Step(presented: true, 9000));
    }

    [Fact]
    public void A_zero_delay_captures_on_the_first_presented_frame()
    {
        var s = new WindowShotSchedule(startMs: 0, delayMs: 0);
        Assert.Equal(WindowShotStep.Wait, s.Step(presented: false, 10));
        Assert.Equal(WindowShotStep.Capture, s.Step(presented: true, 20));
        Assert.Equal(WindowShotStep.Done, s.Step(presented: true, 30));
    }

    [Fact]
    public void With_no_present_reported_it_arms_after_the_fallback()
    {
        var s = new WindowShotSchedule(startMs: 0, delayMs: 100);
        Assert.Equal(WindowShotStep.Wait, s.Step(presented: false, WindowShotSchedule.ArmFallbackMs - 1));
        Assert.Equal(WindowShotStep.Armed, s.Step(presented: false, WindowShotSchedule.ArmFallbackMs));
        Assert.Equal(WindowShotStep.Capture, s.Step(presented: false, WindowShotSchedule.ArmFallbackMs + 100));
    }

    // ── switches ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_switches_parse_path_delay_and_exit()
    {
        string? path = EngineSwitches.ShotWindowPath;
        int delay = EngineSwitches.ShotWindowDelayMs;
        bool exit = EngineSwitches.ShotWindowExit;
        try
        {
            EngineSwitches.ApplyList(@"shot-window=C:\shots\a=b.png,shot-delay=250,shot-exit");
            Assert.Equal(@"C:\shots\a=b.png", EngineSwitches.ShotWindowPath);   // only the first '=' splits name from value
            Assert.Equal(250, EngineSwitches.ShotWindowDelayMs);
            Assert.True(EngineSwitches.ShotWindowExit);
            EngineSwitches.ApplyList("shot-delay=-3");   // rejected: the previous value stays
            Assert.Equal(250, EngineSwitches.ShotWindowDelayMs);
        }
        finally
        {
            EngineSwitches.ShotWindowPath = path;
            EngineSwitches.ShotWindowDelayMs = delay;
            EngineSwitches.ShotWindowExit = exit;
        }
    }
}
