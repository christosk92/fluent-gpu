using System.Threading.Tasks;
using Wavee.Backend;
using Xunit;

namespace Wavee.Tests;

// The revision-gated freshness: a SnapshotRevision/RevisionDelta entry is served resident-fresh and only revalidates when
// the dealer route marks it dirty (MarkStale) — the anti-herd. (Replaces the old always-stale behaviour.)
public class ResourceFreshnessTests
{
    static SessionContext Ctx() => SessionContext.LoggedOut;

    [Fact]
    public async Task SnapshotRevision_NotStaleUntilMarked_ThenRevalidatesOnce()
    {
        int fetches = 0;
        var res = new Resource<string, int>((k, _) => { fetches++; return Task.FromResult(42); },
            new FreshnessPolicy.SnapshotRevision(), Ctx);

        await res.Revalidate("p");                 // initial load
        Assert.Equal(1, fetches);
        Assert.False(res.Peek("p").IsStale);       // resident-fresh — no eager re-fetch

        res.MarkStale("p");                        // a dealer push for this key
        Assert.True(res.Peek("p").IsStale);
        await res.Revalidate("p");
        Assert.Equal(2, fetches);
        Assert.False(res.Peek("p").IsStale);       // cleared after the refetch
    }

    [Fact]
    public async Task RevisionDelta_Use_DoesNotHerd()
    {
        int fetches = 0;
        var res = new Resource<string, int>((k, _) => { fetches++; return Task.FromResult(1); },
            new FreshnessPolicy.RevisionDelta(), Ctx);

        await res.Revalidate("liked");
        Assert.Equal(1, fetches);

        res.Use("liked");                          // served resident; must NOT trigger another fetch
        res.Use("liked");
        await Task.Yield();
        Assert.Equal(1, fetches);                  // no herd — untouched keys never eager-refetch
    }

    [Fact]
    public void MarkStale_OnUnknownKey_CreatesDirtyEntry()
    {
        var res = new Resource<string, int>((k, _) => Task.FromResult(0), new FreshnessPolicy.SnapshotRevision(), Ctx);
        res.MarkStale("never-seen");
        Assert.True(res.Peek("never-seen").IsLoading);   // no value yet → Use will fetch it
    }
}
