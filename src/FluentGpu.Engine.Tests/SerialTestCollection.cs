using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// Test classes that must not run beside the rest of the suite. xUnit runs a collection with
/// <c>DisableParallelization</c> on its own, after the parallel ones, so these see a quiet process:
/// <list type="bullet">
/// <item><description><see cref="UiPostDrainTests"/> counts the posts pending on ITS host, but
/// <c>HostDispatch.Current</c> is process-static — the most recently constructed <c>AppHost</c> in the process is the
/// poster every non-component service uses — so a host built by a concurrently running class can land a post in this
/// test's queue.</description></item>
/// <item><description><see cref="AudioGraphTests"/> drives a real decode producer thread to Ended under a bounded
/// wait; on a saturated machine (the whole suite in parallel) that wait is what runs out, not the graph.</description></item>
/// <item><description><see cref="ScrollProbeTests"/> reads the process-static probe rings, which every render-thread
/// present writes a Turn row into — a render thread in a concurrently running class would land rows in its burst.</description></item>
/// </list>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialTestCollection
{
    public const string Name = "serial";
}
