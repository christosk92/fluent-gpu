using System;
using System.Threading;
using FluentGpu.Text.DirectWrite;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>Runs alone: the stress fact below saturates several cores, which must not starve the timing-sensitive
/// media/present tests that run in parallel collections.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TextLayoutStressCollection
{
    public const string Name = "text-layout-stress";
}

/// <summary>
/// Each TextLayoutEngine owns its shape cache, so concurrent instances (the UI measure engine and the render-thread
/// glyph engines) must also own their LRU eviction scratch: a shared static scratch let one engine's Clear/Add mutate
/// another's in-flight foreach and throw "Collection was modified" out of Layout. Real DirectWrite, no GPU.
/// </summary>
[Collection(TextLayoutStressCollection.Name)]
public sealed class TextLayoutEvictScratchTests
{
    [Fact]
    public void Concurrent_engines_evicting_their_own_caches_do_not_interfere()
    {
        // Every Layout is a unique string, so each engine fills its 2048-entry cache and then runs EvictLru about once
        // per 512 misses; across a handful of threads the eviction passes overlap within a few hundred milliseconds.
        int threads = Math.Clamp(Environment.ProcessorCount, 4, 8);
        const int PerThread = 20_000;
        Exception? failure = null;
        var workers = new Thread[threads];
        using var start = new ManualResetEventSlim(false);
        for (int t = 0; t < threads; t++)
        {
            int id = t;
            workers[t] = new Thread(() =>
            {
                try
                {
                    using var engine = new TextLayoutEngine();
                    Span<char> buf = stackalloc char[16];
                    start.Wait();
                    for (int i = 0; i < PerThread && Volatile.Read(ref failure) is null; i++)
                    {
                        i.TryFormat(buf, out int n);
                        buf[n] = (char)('a' + id);
                        engine.Layout(buf[..(n + 1)], "Segoe UI", 400, 14f, float.PositiveInfinity, 0, 0, 0);
                    }
                }
                catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); }
            }) { IsBackground = true };
            workers[t].Start();
        }
        start.Set();
        foreach (var w in workers) w.Join();
        Assert.Null(failure);
    }
}
