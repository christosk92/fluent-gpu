using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FluentGpu.Pal.Headless;

/// <summary>
/// A scriptable geolocation provider for deterministic engine and application tests. Unscripted requests resolve to
/// <see cref="GeolocationStatus.Unavailable"/>. A pending step can be completed explicitly to exercise cancellation,
/// timeout, and late-completion behavior without touching an OS permission surface.
/// </summary>
public sealed class HeadlessGeolocationProvider : IGeolocationProvider
{
    private readonly object _gate = new();
    private readonly Queue<Task<GeolocationResult>> _steps = new();

    public int RequestCount { get; private set; }
    public GeolocationRequest LastRequest { get; private set; }

    /// <summary>Append an immediately available terminal result to the request script.</summary>
    public void Enqueue(GeolocationResult result)
    {
        lock (_gate)
            _steps.Enqueue(Task.FromResult(result));
    }

    /// <summary>Append a request that remains pending until the returned completion is resolved.</summary>
    public HeadlessGeolocationCompletion EnqueuePending()
    {
        var completion = new HeadlessGeolocationCompletion();
        lock (_gate)
            _steps.Enqueue(completion.Task);
        return completion;
    }

    public ValueTask<GeolocationResult> RequestAsync(
        GeolocationRequest request,
        CancellationToken cancellationToken = default)
    {
        Task<GeolocationResult> step;
        lock (_gate)
        {
            RequestCount++;
            LastRequest = request;
            step = _steps.Count == 0
                ? Task.FromResult(GeolocationResult.Unavailable)
                : _steps.Dequeue();
        }

        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromResult(GeolocationResult.Canceled);
        if (step.IsCompletedSuccessfully)
            return ValueTask.FromResult(step.Result);
        if (request.Timeout == TimeSpan.Zero)
            return ValueTask.FromResult(GeolocationResult.TimedOut);

        return new ValueTask<GeolocationResult>(WaitAsync(step, request.Timeout, cancellationToken));
    }

    private static async Task<GeolocationResult> WaitAsync(
        Task<GeolocationResult> step,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var deadlineCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout != Timeout.InfiniteTimeSpan)
            deadlineCancellation.CancelAfter(timeout);
        Task deadline = Task.Delay(Timeout.InfiniteTimeSpan, deadlineCancellation.Token);
        Task winner = await Task.WhenAny(step, deadline).ConfigureAwait(false);

        if (winner == step)
        {
            deadlineCancellation.Cancel();
            if (cancellationToken.IsCancellationRequested)
                return GeolocationResult.Canceled;
            try
            {
                return await step.ConfigureAwait(false);
            }
            catch
            {
                return GeolocationResult.Failed;
            }
        }

        return cancellationToken.IsCancellationRequested
            ? GeolocationResult.Canceled
            : GeolocationResult.TimedOut;
    }
}

/// <summary>Controls one pending request created by <see cref="HeadlessGeolocationProvider.EnqueuePending"/>.</summary>
public sealed class HeadlessGeolocationCompletion
{
    private readonly TaskCompletionSource<GeolocationResult> _source =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task<GeolocationResult> Task => _source.Task;

    /// <summary>Resolve the pending platform operation. Returns false if it was already resolved.</summary>
    public bool Complete(GeolocationResult result) => _source.TrySetResult(result);
}
