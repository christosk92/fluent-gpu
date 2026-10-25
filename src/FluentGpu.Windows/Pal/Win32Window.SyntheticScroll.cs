using System;
using System.Diagnostics;
using FluentGpu.Foundation;
using FluentGpu.Scroll.Runtime;

namespace FluentGpu.Pal.Windows;

/// <summary>A scripted stand-in for the DirectManipulation touchpad producer (<see cref="Win32DirectManipulation"/>), for
/// diagnostics only (the frame bench's synthetic touchpad). It is pumped at EXACTLY DM's call site — once per produced frame,
/// from <see cref="Win32Window.PumpScroll"/> after the display-phase gate, with that frame's <see cref="FrameClock"/> — so it
/// can hint and stamp its events by DM's own rules (lead = clamp(PresentQpc − now, 0, one refresh); stamp =
/// <see cref="ContactStamp.ForFrame"/>), and its events enter the window's own input queue, the one DM's producer feeds.
/// It never touches the OS input stream (no SendInput, no posted messages): a bench window scrolls, nothing else.</summary>
public interface ISyntheticScrollProducer
{
    /// <summary>True while a contact is engaged or due: the host then produces one frame per refresh (the same term as a
    /// live DM contact in <see cref="Win32Window.ScrollProducerLive"/>).</summary>
    bool Live { get; }

    /// <summary>One produced frame (UI thread): emit this frame's contact events through
    /// <see cref="Win32Window.EnqueueSyntheticScroll"/>.</summary>
    void Pump(in FrameClock clock, long nowQpc, Win32Window window);
}

public sealed unsafe partial class Win32Window
{
    private ISyntheticScrollProducer? _syntheticScroll;

    /// <summary>Diagnostic: a scripted touchpad producer pumped beside DirectManipulation (null = none, the default).</summary>
    public ISyntheticScrollProducer? SyntheticScrollProducer
    {
        get => _syntheticScroll;
        set => _syntheticScroll = value;
    }

    /// <summary>Diagnostic: enqueue one scripted touchpad contact event into the window's input queue, exactly as
    /// <see cref="Win32DirectManipulation"/>'s producer does (<c>IDmContactSink.OnContactEvent</c>).</summary>
    public void EnqueueSyntheticScroll(in ScrollInputEvent e)
        => _queue.Enqueue(InputEvent.ForScroll(in e, PointerKind.Touchpad, unchecked((uint)Environment.TickCount64)));

    private void PumpSyntheticScroll(in FrameClock clock)
    {
        if (_syntheticScroll is { } s) s.Pump(in clock, Stopwatch.GetTimestamp(), this);
    }
}
