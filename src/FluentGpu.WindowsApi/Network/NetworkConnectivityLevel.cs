namespace FluentGpu.WindowsApi.Network;

/// <summary>
/// The coarse internet-reachability level reported by <see cref="NetworkStatus.GetConnectivity"/>, distilled from the
/// native <c>NLM_CONNECTIVITY</c> bit-flags (<c>netlistmgr.h</c>) into the three states an app UI actually branches on.
/// This deliberately collapses the IPv4/IPv6 split and the "disconnected" vs "no traffic" nuance of the raw enum —
/// callers that need the full bitmask are not the target of this pillar (it mirrors WinRT's
/// <c>NetworkConnectivityLevel</c>, which makes the same simplification).
/// </summary>
/// <remarks>
/// Mapping from <c>NLM_CONNECTIVITY</c> (the raw flags are OR-combined across IPv4 and IPv6):
/// <list type="bullet">
/// <item><see cref="None"/> ⇐ <c>NLM_CONNECTIVITY_DISCONNECTED</c> (0), i.e. no <c>*_CONNECTED</c> bit set — no
/// network at all.</item>
/// <item><see cref="LocalAccess"/> ⇐ any <c>*_IPV4_LOCALNETWORK</c> / <c>*_IPV6_LOCALNETWORK</c> bit but no
/// <c>*_INTERNET</c> bit — attached to a network with no internet route (the classic "connected, no internet").</item>
/// <item><see cref="InternetAccess"/> ⇐ any <c>*_IPV4_INTERNET</c> / <c>*_IPV6_INTERNET</c> bit — a route to the
/// public internet exists.</item>
/// </list>
/// <para>
/// <b>Honest note.</b> <see cref="InternetAccess"/> means the OS's Network Connectivity Status Indicator (NCSI) believes
/// a route to the internet exists; it is not a guarantee a specific host is reachable (a captive portal, a DNS outage, or
/// a firewall can still block the app's traffic). Treat it as "the OS thinks we're online", which is exactly what
/// <see cref="NetworkStatus.IsOnline"/> reports via <c>get_IsConnectedToInternet</c>.
/// </para>
/// </remarks>
public enum NetworkConnectivityLevel
{
    /// <summary>No network connectivity (<c>NLM_CONNECTIVITY_DISCONNECTED</c>).</summary>
    None = 0,

    /// <summary>Attached to a local network, but with no route to the internet
    /// (an <c>NLM_CONNECTIVITY_*_LOCALNETWORK</c> bit is set and no <c>*_INTERNET</c> bit).</summary>
    LocalAccess = 1,

    /// <summary>A route to the public internet exists (an <c>NLM_CONNECTIVITY_*_INTERNET</c> bit is set).</summary>
    InternetAccess = 2,
}
