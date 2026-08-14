namespace FluentGpu.WindowsApi.Network;

/// <summary>
/// The coarse connection-cost kind distilled from <c>NLM_CONNECTION_COST</c> (the cost-type bits
/// <c>UNKNOWN</c>/<c>UNRESTRICTED</c>/<c>FIXED</c>/<c>VARIABLE</c> are mutually exclusive on a healthy OS).
/// </summary>
public enum NetworkCostKind
{
    /// <summary>The OS could not report a cost (no adapter, COM failure, or <c>NLM_CONNECTION_COST_UNKNOWN</c>).</summary>
    Unknown = 0,

    /// <summary>Unrestricted / unmetered (<c>NLM_CONNECTION_COST_UNRESTRICTED</c> 0x1) — treat as unlimited.</summary>
    Unrestricted = 1,

    /// <summary>Fixed / capped plan (<c>NLM_CONNECTION_COST_FIXED</c> 0x2) — a data cap applies; treat as metered.</summary>
    Fixed = 2,

    /// <summary>Variable / pay-as-you-go (<c>NLM_CONNECTION_COST_VARIABLE</c> 0x4) — treat as metered.</summary>
    Variable = 3,
}

/// <summary>
/// A snapshot of the current connection's cost flags from <c>INetworkCostManager::GetCost</c>
/// (<c>netlistmgr.h</c>). Produced by <see cref="NetworkStatus.ReadCostAsync"/>. Fail-soft: a COM failure yields
/// <see cref="Unknown"/> (unmetered-conservative — do not throttle the user on a probe failure).
/// </summary>
/// <param name="Kind">Unrestricted / Fixed / Variable / Unknown.</param>
/// <param name="OverDataLimit">The plan is past its cap (<c>NLM_CONNECTION_COST_OVERDATALIMIT</c> 0x10000).</param>
/// <param name="ApproachingDataLimit">The plan is near its cap (<c>NLM_CONNECTION_COST_APPROACHINGDATALIMIT</c> 0x80000).</param>
/// <param name="Roaming">The connection is roaming (<c>NLM_CONNECTION_COST_ROAMING</c> 0x40000).</param>
public readonly record struct NetworkCost(
    NetworkCostKind Kind,
    bool OverDataLimit,
    bool ApproachingDataLimit,
    bool Roaming)
{
    /// <summary>The fail-soft default: unknown kind, no limit/roaming bits. <see cref="IsMetered"/> is false.</summary>
    public static NetworkCost Unknown { get; } = new(NetworkCostKind.Unknown, false, false, false);

    /// <summary><see langword="true"/> when the connection is treated as metered — <see cref="Kind"/> is
    /// <see cref="NetworkCostKind.Fixed"/> or <see cref="NetworkCostKind.Variable"/>.</summary>
    public bool IsMetered => Kind is NetworkCostKind.Fixed or NetworkCostKind.Variable;
}
