namespace FluentGpu.Hosting;

/// <summary>M5 (adreno-hang-fixes.md): when to call <c>ImageCache.EvictToVramPressure</c>, decided independently of the
/// cache itself so the decision is unit-testable with no engine/GPU dependency. Engine-free on purpose — the eviction
/// call is one line in <see cref="AppHost"/>, but WHEN it fires is the whole defect: on a part whose LOCAL budget really is
/// about 128 MB, <c>used &gt; 0.90·budget</c> is true on essentially every frame (the swapchain alone is 66 MB), and
/// <c>D3D12Device.PublishVideoMemorySnapshot</c> only refreshes its sample every 10 presents — so re-acting on the SAME
/// stale (used, budget) pair re-sheds the identical overage every frame in between, evicting unpinned ring entries a
/// scroll immediately re-<c>Request</c>s, which re-decodes, re-uploads, re-crossfades, and gets evicted again.
/// <para>Hysteresis (arm high / disarm low) stops the sample noise right at the threshold from chattering; the cooldown
/// (longer than the device's own sample cadence) stops a single eviction pass from firing again before its effect could
/// possibly show up in a fresh sample; the same-sample suppression is the actual fix for the loop above.</para>
/// <para><b>UMA premise (F251).</b> The 128 MB figure is the dedicated carve-out an Adreno / iGPU reports, NOT what this
/// policy sees: on UMA DXGI's LOCAL <c>Budget</c> is the OS residency budget of the shared pool (about 15 GB), so the policy
/// stays disarmed in normal operation and arms only when that budget shrinks under system memory pressure. That is the
/// intended outcome (the OS, not a fixed number, says when memory is short), so the budget is deliberately not derived from
/// <c>DedicatedVideoMemory</c>, which would re-create the per-frame shed this class documents.</para></summary>
internal struct VramShedPolicy
{
    public const float ArmRatio = 0.90f, DisarmRatio = 0.80f;
    public const int CooldownFrames = 30;      // ≈250 ms at 120 Hz — longer than the device's 10-present sample cadence

    private long _lastUsed, _lastBudget;
    private bool _armed;
    private int _cooldown;

    /// <summary>True once usage has crossed <see cref="ArmRatio"/> and not yet fallen back below <see cref="DisarmRatio"/>.</summary>
    public bool Armed => _armed;

    /// <summary>Call once per frame with the latest sample. Returns true exactly on the frames that should evict:
    /// armed, a genuinely new (used, budget) pair since the last call, and the cooldown from the last shed has expired.</summary>
    public bool Decide(long used, long budget)
    {
        if (budget <= 0) return false;
        bool newSample = used != _lastUsed || budget != _lastBudget;   // re-acting on a stale pair re-sheds the same overage every frame
        _lastUsed = used; _lastBudget = budget;
        if (!_armed && used > budget * ArmRatio) _armed = true;
        else if (_armed && used < budget * DisarmRatio) _armed = false;
        if (_cooldown > 0) _cooldown--;
        return _armed && newSample && _cooldown == 0;
    }

    /// <summary>Report the result of an eviction pass this frame ran (0 = nothing was freed, so no cooldown is owed —
    /// an empty pass means the cache had nothing left to give, and gating the NEXT sample behind a cooldown would only
    /// delay a shed that is still needed).</summary>
    public void NoteShed(long freed) { if (freed > 0) _cooldown = CooldownFrames; }
}
