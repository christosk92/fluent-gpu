using System;
using FluentGpu.Scroll.Motion;
using FluentGpu.Scroll.Runtime;

namespace FluentGpu.Pal.Windows;

/// <summary>Where <see cref="DmContactStream"/> delivers the contact events it decides on (the producer turns each into a
/// stamped <see cref="ScrollInputEvent"/>). An interface, not a delegate: the producer implements it once, and a call
/// allocates nothing.</summary>
internal interface IDmContactSink
{
    /// <summary>One contact event: <paramref name="dipX"/>/<paramref name="dipY"/> are the DIP delta of a
    /// <see cref="ScrollGesture.Sample"/> (0 otherwise); <paramref name="release"/> is an
    /// <see cref="ScrollGesture.End"/>'s verdict (<see cref="ContactRelease.Unknown"/> otherwise).</summary>
    void OnContactEvent(ScrollGesture phase, float dipX, float dipY, ContactRelease release);
}

/// <summary>What the producer must do to its DirectManipulation viewport after a status edge
/// (<see cref="DmContactStream.OnStatus"/>) — COM calls stay in the producer, the decision stays pure.</summary>
[Flags]
internal enum DmStatusEffects : byte
{
    None = 0,
    /// <summary>Stop the viewport at the next pump (never inside the COM sink callback): DM went RUNNING→INERTIA, and the
    /// coast is the engine's (<c>ScrollHandle.ContactEnd</c>), never DM's.</summary>
    StopAtNextPump = 1,
    /// <summary>The viewport went READY: recenter its content to identity.</summary>
    ResetViewport = 2,
}

/// <summary>
/// The DirectManipulation touchpad producer's DECISION, engine-free and COM-free (scroll.md §4.3): which viewport
/// callbacks are contact motion, when the contact begins and ends, and what the End says about the lift. The producer
/// (<see cref="Win32DirectManipulation"/>) forwards DM's callbacks here and brackets every
/// <c>IDirectManipulationUpdateManager::Update</c> with <see cref="OnUpdateReturned"/>; this class tells it what to emit.
/// <para><b>An Update that ends in a non-RUNNING status carries no motion.</b> DM stops producing content at the physical
/// lift and reports the status edge one to five frames later; within that last Update it first raises
/// <c>OnContentUpdated</c> with its whole-pixel snap (a sub-pixel delta) and only then <c>OnViewportStatusChanged</c>.
/// A content update is therefore BUFFERED, and delivered as one <see cref="ScrollGesture.Sample"/> when the Update that
/// produced it returns — only if the viewport is still RUNNING. The Update that ends the contact (READY, INERTIA,
/// SUSPENDED, DISABLED) delivers its End and nothing else: its content is the snap or DM's own first inertia frame, never
/// the fingers. This is ordering, not a delta-size threshold.</para>
/// <para><b>DM decides moving vs stopped.</b> The viewport is configured with <c>TRANSLATION_INERTIA</c>, so DM's own
/// release decision is visible: RUNNING→INERTIA = released moving (<see cref="ContactRelease.Moving"/>), RUNNING→READY =
/// released at rest (<see cref="ContactRelease.Stopped"/>), anything else <see cref="ContactRelease.Unknown"/>. The
/// verdict rides the End; an INERTIA edge also asks the producer to stop DM at the next pump
/// (<see cref="DmStatusEffects.StopAtNextPump"/>), so DM never owns the coast — the engine's fling does.</para>
/// <para>Begin: the RUNNING edge emits <see cref="ScrollGesture.Begin"/> and drops the baseline; the first content update
/// after it captures the baseline and emits nothing (unchanged from the producer this was extracted from).</para>
/// </summary>
internal sealed class DmContactStream
{
    // DIRECTMANIPULATION_STATUS (directmanipulation.h) — the same values Win32DirectManipulation names.
    internal const int Building = 0, Enabled = 1, Disabled = 2, Running = 3, Inertia = 4, Ready = 5, Suspended = 6;

    /// <summary>Transform-unit delta below which an Update's content change is a no-op (skips zero-delta spam).</summary>
    internal const float MinTransformDelta = 0.01f;

    /// <summary>Content-transform units are physical px (the viewport rect is physical); DIP = px / window scale.</summary>
    private const float DipPerTransformUnit = 1.0f;

    private readonly IDmContactSink _sink;
    private int _status = Ready;
    private bool _haveBaseline;
    private float _lastX, _lastY;
    private float _pendX, _pendY;   // content-space motion of the current Update, delivered when it returns
    private bool _pending;

    internal DmContactStream(IDmContactSink sink) => _sink = sink;

    /// <summary>The viewport status as of the latest status callback.</summary>
    internal int Status => _status;

    /// <summary>A viewport status edge (<c>OnViewportStatusChanged</c>). Emits Begin on RUNNING and End (with the
    /// release verdict) when a RUNNING contact leaves RUNNING; any motion buffered in this Update is dropped with the
    /// End. Returns what the producer must do to the viewport.</summary>
    internal DmStatusEffects OnStatus(int current)
    {
        int prior = _status;
        _status = current;
        var effects = DmStatusEffects.None;
        if (current == Running)
        {
            if (prior != Running)
            {
                ClearPending();
                _sink.OnContactEvent(ScrollGesture.Begin, 0f, 0f, ContactRelease.Unknown);
            }
            _haveBaseline = false;   // the first content update captures the baseline, emits nothing
        }
        else if (prior == Running && (current == Ready || current == Inertia || current == Suspended || current == Disabled))
        {
            ClearPending();   // the Update that ends the contact carries no motion (the READY snap / DM's first inertia frame)
            var release = current switch
            {
                Inertia => ContactRelease.Moving,
                Ready => ContactRelease.Stopped,
                _ => ContactRelease.Unknown,
            };
            _sink.OnContactEvent(ScrollGesture.End, 0f, 0f, release);
            if (current == Inertia) effects |= DmStatusEffects.StopAtNextPump;
        }
        if (current == Ready)
        {
            effects |= DmStatusEffects.ResetViewport;
            _haveBaseline = false;
        }
        return effects;
    }

    /// <summary>A content update (<c>OnContentUpdated</c>) with the content transform's scale and translation. Outside
    /// RUNNING, or before a baseline exists, it only re-baselines; while RUNNING its content-space delta is BUFFERED until
    /// the Update that produced it returns (<see cref="OnUpdateReturned"/>).</summary>
    internal void OnContent(float scale, float tx, float ty)
    {
        float invS = scale > 0.001f ? 1f / scale : 1f;
        float px = -tx * invS, py = -ty * invS;   // content-space position under the viewport origin
        if (_status != Running || !_haveBaseline)
        {
            _lastX = px; _lastY = py; _haveBaseline = true;
            return;
        }
        // Fingers up ⇒ content advances toward its end (offset increases): the content-space diff already carries it.
        _pendX += px - _lastX;
        _pendY += py - _lastY;
        _pending = true;
        _lastX = px; _lastY = py;
    }

    /// <summary>The Update returned: its buffered motion is ONE <see cref="ScrollGesture.Sample"/> (DIP, at
    /// <paramref name="windowScale"/>) iff the viewport is still RUNNING and the motion is not a no-op.</summary>
    internal void OnUpdateReturned(float windowScale)
    {
        if (!_pending) return;
        float dx = _pendX, dy = _pendY;
        ClearPending();
        if (_status != Running) return;
        if (MathF.Abs(dx) < MinTransformDelta && MathF.Abs(dy) < MinTransformDelta) return;
        if (!(windowScale > 0f)) windowScale = 1f;
        _sink.OnContactEvent(ScrollGesture.Sample, dx * DipPerTransformUnit / windowScale, dy * DipPerTransformUnit / windowScale,
            ContactRelease.Unknown);
    }

    private void ClearPending()
    {
        _pendX = 0f; _pendY = 0f; _pending = false;
    }
}
