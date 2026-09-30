using System.Threading;

namespace FluentGpu.Pal;

/// <summary>
/// The single-instance identity of THIS process (docs/plans/evidence-diagnostics-implementation.md §A.7). The primary of a
/// single-instance app (<c>FluentGpu.WindowsApi.Activation.SingleInstanceGate</c>) stamps its instance id here when it wins
/// the election; the Windows PAL tags the main window it then creates with <see cref="TagOf"/> of it (a window property,
/// <see cref="WindowPropertyName"/>), and a secondary launch of the SAME instance id redirects only to a window carrying that
/// tag — so two instances of one app with different ids (a verify profile beside the user's own) never hand each other
/// their activations. The two peers (WindowsApi, Windows) cannot reference each other; both reference the engine, so the
/// id and its tag function live here, once.
/// </summary>
public static class InstanceIdentity
{
    /// <summary>The window property name carrying the tag (a <c>SetPropW</c> handle-sized value).</summary>
    public const string WindowPropertyName = "FluentGpu.InstanceId";

    private static string? s_current;

    /// <summary>The instance id this process won, or null (no gate, or a secondary).</summary>
    public static string? Current
    {
        get => Volatile.Read(ref s_current);
        set => Volatile.Write(ref s_current, value);
    }

    /// <summary>The non-zero tag a window property carries for <paramref name="instanceId"/>: FNV-1a 32 over its UTF-16
    /// code units (ordinal, as the kernel compares the mutex name). 0 is never returned — 0 is "no property".</summary>
    public static uint TagOf(string instanceId)
    {
        uint h = 2166136261u;
        foreach (char c in instanceId)
        {
            h ^= c;
            h *= 16777619u;
        }
        return h == 0 ? 1u : h;
    }
}
