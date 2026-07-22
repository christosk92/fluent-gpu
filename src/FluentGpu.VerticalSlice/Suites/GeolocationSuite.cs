using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Forms;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Media;
using FluentGpu.Pal;
using FluentGpu.Input;
using FluentGpu.Layout;
using FluentGpu.Pal.Headless;
using FluentGpu.Reconciler;
using FluentGpu.Controls;
using FluentGpu.Render;
using FluentGpu.Rhi;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using static FluentGpu.Dsl.Ui;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;




static class GeolocationSuite
{
    public static void Run(StringTable strings)
    {
        GeolocationChecks();
    }

    static void GeolocationChecks()
    {
        var provider = new HeadlessGeolocationProvider();
        var expectedPosition = new GeolocationPosition(
            52.3676,
            4.9041,
            18.5,
            new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero));
        var request = new GeolocationRequest(
            TimeSpan.FromSeconds(3),
            TimeSpan.FromMinutes(2),
            GeolocationAccuracy.High);

        provider.Enqueue(GeolocationResult.Success(expectedPosition));
        GeolocationResult success = provider.RequestAsync(request).AsTask().GetAwaiter().GetResult();
        Check("geo.contract success carries a valid provider-neutral fix",
            success.IsSuccess && success.Position == expectedPosition &&
            provider.RequestCount == 1 && provider.LastRequest == request,
            $"status={success.Status} lat={success.Position.Latitude:0.####} requests={provider.RequestCount}");

        provider.Enqueue(GeolocationResult.PermissionDenied);
        GeolocationResult denied = provider.RequestAsync(request).AsTask().GetAwaiter().GetResult();
        Check("geo.contract permission denial is distinct", denied.Status == GeolocationStatus.PermissionDenied,
            $"status={denied.Status}");

        provider.Enqueue(GeolocationResult.Unavailable);
        GeolocationResult unavailable = provider.RequestAsync(request).AsTask().GetAwaiter().GetResult();
        Check("geo.contract unavailable is distinct", unavailable.Status == GeolocationStatus.Unavailable,
            $"status={unavailable.Status}");

        provider.Enqueue(GeolocationResult.Failed);
        GeolocationResult failed = provider.RequestAsync(request).AsTask().GetAwaiter().GetResult();
        Check("geo.contract platform failure is distinct", failed.Status == GeolocationStatus.Failed,
            $"status={failed.Status}");

        HeadlessGeolocationCompletion lateCompletion = provider.EnqueuePending();
        GeolocationResult timedOut = provider.RequestAsync(new GeolocationRequest(TimeSpan.Zero))
            .AsTask().GetAwaiter().GetResult();
        bool lateWasAcceptedByPlatform = lateCompletion.Complete(GeolocationResult.Success(expectedPosition));
        Check("geo.headless timeout wins and a late platform completion cannot replace it",
            timedOut.Status == GeolocationStatus.TimedOut && lateWasAcceptedByPlatform,
            $"status={timedOut.Status} lateAccepted={lateWasAcceptedByPlatform}");

        provider.EnqueuePending();
        using var cancellation = new CancellationTokenSource();
        Task<GeolocationResult> canceledTask = provider.RequestAsync(request, cancellation.Token).AsTask();
        cancellation.Cancel();
        GeolocationResult canceled = canceledTask.GetAwaiter().GetResult();
        Check("geo.headless caller cancellation is distinct", canceled.Status == GeolocationStatus.Canceled,
            $"status={canceled.Status}");

        var unscripted = new HeadlessGeolocationProvider();
        GeolocationResult fallback = unscripted.RequestAsync(request).AsTask().GetAwaiter().GetResult();
        Check("geo.headless unscripted requests deterministically report unavailable",
            fallback.Status == GeolocationStatus.Unavailable,
            $"status={fallback.Status}");
    }
}
