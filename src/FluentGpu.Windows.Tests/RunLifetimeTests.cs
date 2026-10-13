using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using FluentGpu.Foundation;
using Xunit;

namespace FluentGpu.Windows.Tests;

[CollectionDefinition("Run lifetime", DisableParallelization = true)]
public sealed class RunLifetimeCollection { }

[Collection("Run lifetime")]
public sealed class RunLifetimeTests
{
    private sealed class Owner(Action dispose) : IDisposable { public void Dispose() => dispose(); }
    private sealed class ThrowingDataException : Exception
    {
        public override System.Collections.IDictionary Data => throw new InvalidOperationException("custom Data getter failed");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowPrimary(Exception primary) => throw primary;

    [Fact]
    public void BodyFailure_IsPreservedWithItsOriginalStack_WhenCleanupAlsoThrows()
    {
        var primary = new InvalidOperationException("original submission error");
        var cleanup = new InvalidOperationException("cleanup error");
        var order = new List<int>();
        var thrown = Assert.Throws<InvalidOperationException>(() => RunLifetime.Run(lifetime =>
        {
            lifetime.Own(new Owner(() => order.Add(1)));
            lifetime.Own(new Owner(() => { order.Add(2); throw cleanup; }));
            ThrowPrimary(primary);
        }));
        Assert.Same(primary, thrown);
        Assert.Contains(nameof(ThrowPrimary), thrown.StackTrace);
        Assert.Equal(new[] { 2, 1 }, order);
        Assert.Equal(new[] { cleanup }, Assert.IsType<Exception[]>(thrown.Data["FluentGpu.CleanupFailures"]));
    }

    [Fact]
    public void NormalExit_ThrowsFirstCleanupFailure_AndRetainsLaterFailures()
    {
        var first = new InvalidOperationException("host drain failure");
        var later = new InvalidOperationException("decoder shutdown failure");
        var order = new List<int>();
        var thrown = Assert.Throws<InvalidOperationException>(() => RunLifetime.Run(lifetime =>
        {
            lifetime.Own(new Owner(() => order.Add(1)));
            lifetime.Own(new Owner(() => { order.Add(2); throw later; }));
            lifetime.Own(new Owner(() => { order.Add(3); throw first; }));
        }));
        Assert.Same(first, thrown);
        Assert.Equal(new[] { 3, 2, 1 }, order);
        Assert.Equal(new[] { later }, Assert.IsType<Exception[]>(thrown.Data["FluentGpu.CleanupFailures"]));
    }

    [Fact]
    public void SuccessfulRun_RetainsReverseOwnershipOrder()
    {
        var order = new List<int>();
        RunLifetime.Run(lifetime =>
        {
            lifetime.Own(new Owner(() => order.Add(1)));
            lifetime.Own(new Owner(() => order.Add(2)));
            lifetime.Own(new Owner(() => order.Add(3)));
        });
        Assert.Equal(new[] { 3, 2, 1 }, order);
    }

    [Fact]
    public void ThrowingDiagnosticSink_CannotReplaceTheFirstFailure()
    {
        var savedSink = Diag.Sink;
        var primary = new InvalidOperationException("original error");
        try
        {
            Diag.Sink = _ => throw new InvalidOperationException("log sink failure");
            Assert.Same(primary, Assert.Throws<InvalidOperationException>(() => RunLifetime.Run(lifetime =>
            {
                lifetime.Own(new Owner(() => throw new InvalidOperationException("cleanup failure")));
                ThrowPrimary(primary);
            })));
        }
        finally { Diag.Sink = savedSink; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RunState_IsClearedBeforeAnyOwnerDisposes(bool bodyFails)
    {
        bool live = true;
        var order = new List<string>();
        var primary = new InvalidOperationException("body failure");
        void Body(RunLifetime lifetime)
        {
            order.Add("body");
            lifetime.Own(new Owner(() => { Assert.False(live); order.Add("dispose"); }));
            if (bodyFails) ThrowPrimary(primary);
        }
        void Clear() { live = false; order.Add("clear"); }
        if (bodyFails) Assert.Same(primary, Assert.Throws<InvalidOperationException>(() => RunLifetime.Run(Body, Clear)));
        else RunLifetime.Run(Body, Clear);
        Assert.Equal(new[] { "body", "clear", "dispose" }, order);
    }

    [Fact]
    public void ThrowingExceptionData_CannotReplaceTheBodyFailureWhenCleanupAlsoFails()
    {
        var primary = new ThrowingDataException();
        var thrown = Assert.Throws<ThrowingDataException>(() => RunLifetime.Run(lifetime =>
        {
            lifetime.Own(new Owner(() => throw new InvalidOperationException("cleanup failure")));
            ThrowPrimary(primary);
        }));
        Assert.Same(primary, thrown);
        Assert.Contains(nameof(ThrowPrimary), thrown.StackTrace);
    }
}
