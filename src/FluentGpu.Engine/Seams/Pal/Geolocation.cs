using System;
using System.Threading;
using System.Threading.Tasks;

namespace FluentGpu.Pal;

/// <summary>The terminal state of a one-shot geolocation request.</summary>
public enum GeolocationStatus : byte
{
    // Keep the zero/default value non-successful so default(GeolocationResult) is always safe.
    Failed,
    Success,
    PermissionDenied,
    Unavailable,
    TimedOut,
    Canceled,
}

/// <summary>A provider-neutral accuracy preference. Providers may treat this as a best-effort hint.</summary>
public enum GeolocationAccuracy : byte
{
    Default,
    High,
}

/// <summary>A geographic fix returned by <see cref="IGeolocationProvider"/>.</summary>
/// <param name="Latitude">Latitude in degrees, in the inclusive range -90 through 90.</param>
/// <param name="Longitude">Longitude in degrees, in the inclusive range -180 through 180.</param>
/// <param name="AccuracyMeters">Horizontal accuracy radius in metres.</param>
/// <param name="Timestamp">Time at which the provider produced the fix.</param>
public readonly record struct GeolocationPosition(
    double Latitude,
    double Longitude,
    double AccuracyMeters,
    DateTimeOffset Timestamp)
{
    /// <summary>Whether all numeric fields form a usable geographic fix.</summary>
    public bool IsValid =>
        double.IsFinite(Latitude) && Latitude is >= -90d and <= 90d &&
        double.IsFinite(Longitude) && Longitude is >= -180d and <= 180d &&
        double.IsFinite(AccuracyMeters) && AccuracyMeters >= 0d &&
        Timestamp != default;
}

/// <summary>
/// Options for one location fix. <see cref="Timeout"/> covers the complete operation, including any platform consent
/// flow; <see cref="MaximumAge"/> allows a provider to satisfy the request from a recent cached fix.
/// </summary>
public readonly record struct GeolocationRequest
{
    public static GeolocationRequest Default { get; } = new(TimeSpan.FromSeconds(15));

    public GeolocationRequest(
        TimeSpan timeout,
        TimeSpan maximumAge = default,
        GeolocationAccuracy accuracy = GeolocationAccuracy.Default)
    {
        if (timeout < TimeSpan.Zero && timeout != System.Threading.Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be non-negative or infinite.");
        if (maximumAge < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumAge), "Maximum age must be non-negative.");
        if (!Enum.IsDefined(accuracy))
            throw new ArgumentOutOfRangeException(nameof(accuracy));

        Timeout = timeout;
        MaximumAge = maximumAge;
        Accuracy = accuracy;
    }

    public TimeSpan Timeout { get; }
    public TimeSpan MaximumAge { get; }
    public GeolocationAccuracy Accuracy { get; }
}

/// <summary>A non-throwing, provider-neutral result for one geolocation request.</summary>
public readonly record struct GeolocationResult
{
    private GeolocationResult(GeolocationStatus status, GeolocationPosition position)
    {
        Status = status;
        Position = position;
    }

    public GeolocationStatus Status { get; }
    public GeolocationPosition Position { get; }
    public bool IsSuccess => Status == GeolocationStatus.Success;

    public static GeolocationResult Success(GeolocationPosition position)
    {
        if (!position.IsValid)
            throw new ArgumentOutOfRangeException(nameof(position), "The position must contain valid coordinates and accuracy.");
        return new GeolocationResult(GeolocationStatus.Success, position);
    }

    public static GeolocationResult PermissionDenied => new(GeolocationStatus.PermissionDenied, default);
    public static GeolocationResult Unavailable => new(GeolocationStatus.Unavailable, default);
    public static GeolocationResult TimedOut => new(GeolocationStatus.TimedOut, default);
    public static GeolocationResult Canceled => new(GeolocationStatus.Canceled, default);
    public static GeolocationResult Failed => new(GeolocationStatus.Failed, default);
}

/// <summary>
/// Provides one geographic fix. Implementations return terminal states instead of leaking platform exceptions or
/// platform-specific types. A provider must ignore a platform completion that arrives after timeout or cancellation.
/// </summary>
public interface IGeolocationProvider
{
    ValueTask<GeolocationResult> RequestAsync(
        GeolocationRequest request,
        CancellationToken cancellationToken = default);
}
