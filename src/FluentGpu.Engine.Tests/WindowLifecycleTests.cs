using System;
using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// The window-lifecycle seams a notification-area app needs: the close veto (<see cref="WindowCloseGate"/>), the
/// placement + visibility relay (<see cref="WindowStateRelay"/> / <c>AppHost.WindowStateChanged</c>), and a HIDDEN
/// window parking the host exactly like a minimized one. The pure rules are tested directly; the host behaviour through
/// a real headless <c>AppHost</c>, whose settable <see cref="HeadlessWindow.IsVisible"/>/<see cref="HeadlessWindow.State"/>
/// are the only seams used.
/// </summary>
public sealed class WindowCloseGateTests
{
    [Fact]
    public void A_user_close_with_no_handler_destroys_the_window()
    {
        var gate = new WindowCloseGate();
        Assert.Equal(CloseReason.User, gate.Reason);
        Assert.True(gate.ShouldDestroy(null));
    }

    [Fact]
    public void A_handler_can_keep_the_window_on_a_user_close()
    {
        var gate = new WindowCloseGate();
        CloseReason? seen = null;
        Assert.False(gate.ShouldDestroy(r => { seen = r; return true; }));
        Assert.Equal(CloseReason.User, seen);
        Assert.True(gate.ShouldDestroy(_ => false));
    }

    [Fact]
    public void A_session_end_is_reported_to_the_handler_but_never_vetoed()
    {
        var gate = new WindowCloseGate();
        gate.OnQueryEndSession();
        CloseReason? seen = null;
        Assert.True(gate.ShouldDestroy(r => { seen = r; return true; }));
        Assert.Equal(CloseReason.SessionEnding, seen);
    }

    [Fact]
    public void A_cancelled_session_end_makes_closes_ordinary_again()
    {
        var gate = new WindowCloseGate();
        gate.OnQueryEndSession();
        gate.OnEndSession(ending: false);
        Assert.Equal(CloseReason.User, gate.Reason);
        Assert.False(gate.ShouldDestroy(_ => true));

        gate.OnQueryEndSession();
        gate.OnEndSession(ending: true);
        Assert.Equal(CloseReason.SessionEnding, gate.Reason);
    }

    [Theory]
    [InlineData(CloseReason.User, false, true)]
    [InlineData(CloseReason.User, true, false)]
    [InlineData(CloseReason.SessionEnding, false, true)]
    [InlineData(CloseReason.SessionEnding, true, true)]
    public void Decide_is_destroy_unless_a_user_close_was_vetoed(CloseReason reason, bool vetoed, bool destroy)
        => Assert.Equal(destroy, WindowCloseGate.Decide(reason, vetoed));
}

public sealed class WindowStateRelayTests
{
    private static readonly WindowStatus Shown = new(WindowState.Normal, true);

    [Fact]
    public void The_first_sample_seeds_without_an_edge()
    {
        var relay = new WindowStateRelay();
        Assert.False(relay.TryAdvance(new WindowStatus(WindowState.Normal, false), out _));
        Assert.False(relay.TryAdvance(new WindowStatus(WindowState.Normal, false), out _));
    }

    [Fact]
    public void Minimize_then_restore_are_two_edges()
    {
        var relay = new WindowStateRelay();
        relay.TryAdvance(Shown, out _);

        Assert.True(relay.TryAdvance(new WindowStatus(WindowState.Minimized, true), out var minimize));
        Assert.True(minimize.Minimized);
        Assert.True(minimize.Parked);
        Assert.False(minimize.Restored || minimize.Hidden || minimize.Shown || minimize.Maximized);

        Assert.True(relay.TryAdvance(Shown, out var restore));
        Assert.True(restore.Restored);
        Assert.True(restore.Unparked);
        Assert.False(restore.Minimized);
    }

    [Fact]
    public void Hide_and_show_are_visibility_edges()
    {
        var relay = new WindowStateRelay();
        relay.TryAdvance(Shown, out _);

        Assert.True(relay.TryAdvance(new WindowStatus(WindowState.Normal, false), out var hide));
        Assert.True(hide.Hidden);
        Assert.True(hide.Parked);
        Assert.False(hide.Minimized);

        Assert.True(relay.TryAdvance(Shown, out var show));
        Assert.True(show.Shown);
        Assert.True(show.Unparked);
    }

    [Fact]
    public void Hiding_a_minimized_window_is_a_hide_but_not_a_new_park()
    {
        var relay = new WindowStateRelay();
        relay.TryAdvance(new WindowStatus(WindowState.Minimized, true), out _);
        Assert.True(relay.TryAdvance(new WindowStatus(WindowState.Minimized, false), out var change));
        Assert.True(change.Hidden);
        Assert.False(change.Parked);     // it was already parked
        Assert.False(change.Minimized);  // and already minimized
    }

    [Fact]
    public void One_sample_can_carry_several_edges()
    {
        var relay = new WindowStateRelay();
        relay.TryAdvance(new WindowStatus(WindowState.Minimized, false), out _);
        Assert.True(relay.TryAdvance(new WindowStatus(WindowState.Maximized, true), out var change));
        Assert.True(change.Restored);
        Assert.True(change.Maximized);
        Assert.True(change.Shown);
        Assert.True(change.Unparked);
    }

    [Theory]
    [InlineData(WindowState.Normal, true, false)]
    [InlineData(WindowState.Maximized, true, false)]
    [InlineData(WindowState.Minimized, true, true)]
    [InlineData(WindowState.Normal, false, true)]
    [InlineData(WindowState.Minimized, false, true)]
    public void Parked_is_minimized_or_hidden(WindowState placement, bool visible, bool parked)
        => Assert.Equal(parked, new WindowStatus(placement, visible).Parked);
}

[Collection(SerialTestCollection.Name)]   // HostDispatch.Current is process-static (see SerialTestCollection)
public sealed class HiddenWindowHostTests
{
    /// <summary>A platform app that can raise the stashed OS events the headless PAL never produces.</summary>
    private sealed class EventfulApp : IPlatformApp
    {
        private readonly HeadlessPlatformApp _inner = new();
        private Action<string>? _redirected;
        private Action<int>? _thumb;
        private Action? _colors;

        public IPlatformWindow CreateWindow(in WindowDesc desc) => _inner.CreateWindow(desc);
        public IClipboard Clipboard => _inner.Clipboard;
        public void OpenUri(string uri) => _inner.OpenUri(uri);
        public void Dispose() => _inner.Dispose();

        public event Action<string>? ActivationRedirected { add => _redirected += value; remove => _redirected -= value; }
        public event Action<int>? ThumbButtonClicked { add => _thumb += value; remove => _thumb -= value; }
        public event Action? SystemColorsChanged { add => _colors += value; remove => _colors -= value; }

        public void Redirect(string uri) => _redirected?.Invoke(uri);
        public void ClickThumb(int id) => _thumb?.Invoke(id);
        public void ChangeColors() => _colors?.Invoke();
    }

    private sealed class ActivationProbe : Component
    {
        public int Activated, Deactivated;
        public override Element Render()
        {
            UseActivation(() => Activated++, () => Deactivated++);
            return Ui.VStack(0);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public EventfulApp App { get; } = new();
        public HeadlessWindow Window { get; }
        public ActivationProbe Probe { get; } = new();
        public AppHost Host { get; }
        public List<WindowStateChange> Changes { get; } = new();

        public Fixture()
        {
            var strings = new StringTable();
            Window = new HeadlessWindow(new WindowDesc("hidden-window", new Size2(320, 240), 1f));
            Window.Show();
            Host = new AppHost(App, Window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, Probe);
            Host.WindowStateChanged += Changes.Add;
            for (int i = 0; i < 8; i++) Host.RunFrame();
        }

        public void Dispose()
        {
            Host.Dispose();
            App.Dispose();
        }
    }

    [Fact]
    public void A_hidden_window_parks_the_host_and_blocks_the_loop()
    {
        using var f = new Fixture();
        Assert.False(f.Host.IsParked);

        f.Window.Hide();
        Assert.True(f.Host.IsParked);
        Assert.Equal(0, f.Host.RecommendedWaitMs());   // the unconsumed park edge runs its frame at once

        var edge = f.Host.RunFrame();
        Assert.False(edge.Rendered);
        Assert.Equal(-1, f.Host.RecommendedWaitMs());  // then the loop blocks on messages, exactly as minimized
        Assert.Equal(1, f.Probe.Deactivated);          // UseActivation / UseIsActive saw the hide on the edge frame

        for (int i = 0; i < 4; i++) Assert.False(f.Host.RunFrame().Rendered);
    }

    [Fact]
    public void Showing_again_unparks_and_reactivates()
    {
        using var f = new Fixture();
        f.Window.Hide();
        f.Host.RunFrame();

        f.Window.Show();
        Assert.False(f.Host.IsParked);
        Assert.Equal(0, f.Host.RecommendedWaitMs());   // the un-park edge is produced immediately too
        f.Host.RunFrame();
        Assert.Equal(1, f.Probe.Activated);
    }

    [Fact]
    public void The_relay_reports_hide_and_show_but_seeds_silently()
    {
        using var f = new Fixture();
        Assert.Empty(f.Changes);                        // eight quiet frames: the first only seeded

        f.Window.Hide();
        f.Host.RunFrame();
        f.Window.Show();
        f.Host.RunFrame();

        Assert.Equal(2, f.Changes.Count);
        Assert.True(f.Changes[0].Hidden);
        Assert.True(f.Changes[1].Shown);
    }

    [Fact]
    public void A_minimize_handler_can_hide_the_window_in_the_same_frame()
    {
        using var f = new Fixture();
        f.Host.WindowStateChanged += change => { if (change.Minimized) f.Window.Hide(); };

        f.Window.State = WindowState.Minimized;
        f.Host.RunFrame();
        Assert.True(f.Changes[0].Minimized);
        Assert.True(f.Host.IsParked);

        f.Window.State = WindowState.Normal;            // restored while still hidden: still parked
        f.Host.RunFrame();
        Assert.True(f.Host.IsParked);
        Assert.Contains(f.Changes, c => c.Hidden);
    }

    [Fact]
    public void A_second_launch_redirect_reaches_a_hidden_app()
    {
        using var f = new Fixture();
        string? delivered = null;
        f.Host.ActivationRedirected += uri => delivered = uri;
        f.Window.Hide();
        f.Host.RunFrame();

        f.App.Redirect("app://open");
        f.Host.RunFrame();                              // parked: Paint never runs, the parked branch delivers it
        Assert.Equal("app://open", delivered);
    }

    [Fact]
    public void A_thumbnail_click_reaches_a_minimized_app()
    {
        using var f = new Fixture();
        int clicked = -1;
        f.Host.ThumbButtonClicked += id => clicked = id;
        f.Window.State = WindowState.Minimized;
        f.Host.RunFrame();

        f.App.ClickThumb(2);
        f.Host.RunFrame();
        Assert.Equal(2, clicked);
    }

    [Fact]
    public void An_os_colour_change_waits_for_the_unpark_frame()
    {
        using var f = new Fixture();
        int colours = 0;
        f.Host.SystemColorsChanged += () => colours++;
        f.Window.Hide();
        f.Host.RunFrame();

        f.App.ChangeColors();
        f.Host.RunFrame();
        Assert.Equal(0, colours);                       // theme detection is Paint's; nothing paints while hidden

        f.Window.Show();
        f.Host.RunFrame();
        Assert.Equal(1, colours);
    }
}
