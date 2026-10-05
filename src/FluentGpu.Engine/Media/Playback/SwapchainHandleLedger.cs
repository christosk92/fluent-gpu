using System;

namespace FluentGpu.Media;

/// <summary>
/// Owns the NT handles a media engine hands out for its composition swap chain (<c>IMFMediaEngineEx::GetVideoSwapchainHandle</c>
/// returns a FRESH handle on every call, and the caller closes it: the MS sample's <c>wil::unique_handle</c>, Chromium's
/// <c>DuplicateHandle(..., DUPLICATE_CLOSE_SOURCE)</c> and Firefox's <c>CloseHandle(mHandle)</c> all do). The presenter's
/// <c>CreateSurfaceFromHandle</c> takes its own reference and never owns the value, so every handle the engine queried and
/// then replaced was a leaked kernel handle (and the composition surface behind it) per source switch, format change and
/// resource loss (F198).
/// <para><b>One-deep delay.</b> The render thread consumes a handle some turns after the engine publishes it, and a device
/// recovery binds the CURRENT value again, so a handle is never closed while it is current. A superseded handle is
/// <i>retired</i>, not closed: it survives one more replacement (the render thread has long since bound its successor's
/// predecessor) or <see cref="DefaultGraceMs"/>, whichever comes first, via <see cref="Sweep"/>. At most two handles are
/// open at once (the current one and one retired).</para>
/// <para>Engine-thread-only, like the COM calls that produce the handles; the closer is injected so the policy runs headlessly
/// against a recording fake.</para>
/// </summary>
public sealed class SwapchainHandleLedger
{
    /// <summary>How long a retired handle waits for the render thread before <see cref="Sweep"/> closes it: several UI frames
    /// plus the registry's bind retry backoff.</summary>
    public const long DefaultGraceMs = 2000;

    private readonly Action<nuint> _close;
    private readonly long _graceMs;
    private nuint _current, _retired;
    private long _retiredAtMs;

    /// <param name="close">Closes one handle (<c>CloseHandle</c> in production). Called exactly once per handle taken in.</param>
    /// <param name="graceMs">How long a retired handle is kept before <see cref="Sweep"/> closes it.</param>
    public SwapchainHandleLedger(Action<nuint> close, long graceMs = DefaultGraceMs)
    {
        _close = close;
        _graceMs = graceMs;
    }

    /// <summary>The handle in force (0 = none): the one the engine publishes and a presenter may bind.</summary>
    public nuint Current => _current;

    /// <summary>The handle waiting out its grace (0 = none).</summary>
    public nuint Retired => _retired;

    /// <summary>How many handles this ledger holds open right now (0..2).</summary>
    public int OpenCount => (_current != 0 ? 1 : 0) + (_retired != 0 ? 1 : 0);

    /// <summary>Take ownership of a handle the engine just queried and make it current. The previous current handle is retired
    /// (and the one retired before it closed). A repeat of the current value is ignored — it is the same handle, not a second
    /// reference; a repeat of the retired value reinstates it.</summary>
    public void Adopt(nuint handle, long nowMs)
    {
        if (handle == 0 || handle == _current) return;
        if (handle == _retired) _retired = 0;   // MF handed back the identical handle: it was never superseded
        Retire(nowMs);
        _current = handle;
    }

    /// <summary>The current handle no longer describes anything (a source change, a detach): retire it. The handle retired
    /// before it is closed now; this one waits out one more replacement or the grace.</summary>
    public void Retire(long nowMs)
    {
        if (_current == 0) return;
        CloseRetired();
        _retired = _current;
        _retiredAtMs = nowMs;
        _current = 0;
    }

    /// <summary>Close the retired handle once it has waited <c>graceMs</c>. Cheap; called every engine turn.</summary>
    public void Sweep(long nowMs)
    {
        if (_retired != 0 && nowMs - _retiredAtMs >= _graceMs) CloseRetired();
    }

    /// <summary>Close everything this ledger holds (the engine is being disposed).</summary>
    public void CloseAll()
    {
        CloseRetired();
        nuint current = _current;
        _current = 0;
        if (current != 0) _close(current);
    }

    private void CloseRetired()
    {
        nuint retired = _retired;
        _retired = 0;
        if (retired != 0) _close(retired);
    }
}
