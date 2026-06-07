using FluentGpu.Scene;

namespace FluentGpu.Media;

/// <summary>The outcome of a fetch attempt. On success it hands back a buffer RENTED FROM
/// <see cref="System.Buffers.ArrayPool{Byte}.Shared"/> plus the valid length — the scheduler returns it to the pool
/// after decode, so a fetch costs no net managed allocation. On failure it carries a classified
/// <see cref="ImageFailureKind"/> the scheduler uses to pick transient-retry vs permanent-fail.</summary>
public readonly struct FetchResult
{
    /// <summary>Pooled buffer (rent from <c>ArrayPool&lt;byte&gt;.Shared</c>); the SCHEDULER returns it. May be larger than <see cref="Length"/>.</summary>
    public readonly byte[]? Buffer;
    public readonly int Length;
    public readonly ImageFailureKind Failure;   // None on success
    private FetchResult(byte[]? buffer, int length, ImageFailureKind f) { Buffer = buffer; Length = length; Failure = f; }
    public bool Ok => Buffer != null && Failure == ImageFailureKind.None;
    public ReadOnlySpan<byte> Span => Buffer is null ? default : Buffer.AsSpan(0, Length);
    /// <summary><paramref name="buffer"/> MUST be rented from <c>ArrayPool&lt;byte&gt;.Shared</c>; the scheduler returns it.</summary>
    public static FetchResult Pooled(byte[] buffer, int length) => new(buffer, length, ImageFailureKind.None);
    public static FetchResult Fail(ImageFailureKind f) => new(null, 0, f);
}

/// <summary>
/// Portable fetch seam (media-pipeline.md §3 "Fetch"): turn a source URI into encoded bytes off the UI thread. Maps
/// transport/HTTP-status errors to <see cref="ImageFailureKind"/> so the scheduler can distinguish transient (network,
/// timeout, 5xx → retried) from permanent (404, other 4xx → not retried). Honors the cancellation token (per-attempt
/// timeout + shutdown). HTTP(S) + local file by default (<see cref="DefaultImageFetcher"/>).
/// </summary>
public interface IImageFetcher
{
    System.Threading.Tasks.Task<FetchResult> FetchAsync(string source, System.Threading.CancellationToken ct);
}

/// <summary>Decode/fetch robustness policy. Defaults tuned for album art over flaky links (slow internet, transient 5xx).</summary>
public sealed class DecodeOptions
{
    /// <summary>Worker (parallel decode) count. 0 ⇒ clamp(ProcessorCount-2, 2..6) per the design's WORKER pool.</summary>
    public int MaxConcurrency { get; init; }
    /// <summary>Total fetch attempts for a TRANSIENT failure (1 try + N-1 retries). Permanent failures never retry.</summary>
    public int MaxAttempts { get; init; } = 3;
    /// <summary>Per-attempt deadline — bounds a slow/stalled download so it fails over to a retry instead of hanging.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>Exponential backoff between transient retries: <c>BackoffBase * 2^(attempt-1)</c>, capped at <see cref="BackoffMax"/>.</summary>
    public TimeSpan BackoffBase { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan BackoffMax { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Bounded request-channel capacity; a saturated channel drops the newest enqueue (fast-scroll backpressure).</summary>
    public int QueueCapacity { get; init; } = 512;
}
