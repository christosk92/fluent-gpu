using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Pal;
using TerraFX.Interop.WinRT;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.WinRT.WinRT;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.WindowsApi.Location;

/// <summary>
/// One-shot Windows geolocation through <c>Windows.Devices.Geolocation</c>. The WinRT ABI is consumed directly via
/// TerraFX, preserving the engine's NativeAOT-friendly, zero-CsWinRT platform boundary.
/// </summary>
/// <remarks>
/// The first call can display the Windows location consent prompt. Microsoft requires that consent request to start on
/// the foreground UI thread, so callers must invoke <see cref="RequestAsync"/> there. No prompt occurs merely by
/// constructing this provider. Packaged applications must declare the <c>location</c> device capability; unpackaged
/// full-trust applications do not require a manifest capability but still require user consent.
/// </remarks>
[SupportedOSPlatform("windows10.0.10240.0")]
public sealed class WindowsGeolocationProvider : IGeolocationProvider
{
    private const string RuntimeClassGeolocator = "Windows.Devices.Geolocation.Geolocator";
    private const int S_FALSE = 1;
    private const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);
    private const int E_ACCESSDENIED = unchecked((int)0x80070005);
    private const int E_ABORT = unchecked((int)0x80004004);
    private const int HRESULT_ERROR_TIMEOUT = unchecked((int)0x800705B4);
    private const int REGDB_E_CLASSNOTREG = unchecked((int)0x80040154);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);

    [ThreadStatic]
    private static bool t_roInitialized;

    public ValueTask<GeolocationResult> RequestAsync(
        GeolocationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromResult(GeolocationResult.Canceled);
        if (request.Timeout == TimeSpan.Zero)
            return ValueTask.FromResult(GeolocationResult.TimedOut);

        long startedAt = Stopwatch.GetTimestamp();
        int startHr;
        nint accessOperation;
        nint geolocator;
        try
        {
            startHr = TryStartAccess(out accessOperation, out geolocator);
        }
        catch
        {
            return ValueTask.FromResult(GeolocationResult.Failed);
        }
        if (startHr < 0)
            return ValueTask.FromResult(MapStartFailure(startHr));

        return new ValueTask<GeolocationResult>(CompleteAsync(
            accessOperation,
            geolocator,
            request,
            startedAt,
            cancellationToken));
    }

    private static async Task<GeolocationResult> CompleteAsync(
        nint accessOperation,
        nint geolocator,
        GeolocationRequest request,
        long startedAt,
        CancellationToken cancellationToken)
    {
        try
        {
            NativeAsyncResult accessWait = await WaitForAsync(
                accessOperation,
                Remaining(request.Timeout, startedAt),
                cancellationToken).ConfigureAwait(false);

            GeolocationResult? accessFailure = MapWaitFailure(accessWait, geolocator);
            if (accessFailure is { } failure)
            {
                if (accessWait.State is NativeAsyncState.Canceled or NativeAsyncState.Error)
                    CloseOperation(accessOperation);
                return failure;
            }

            int accessHr = GetAccessResult(accessOperation, out GeolocationAccessStatus access);
            CloseOperation(accessOperation);
            if (accessHr < 0)
                return MapNativeFailure(accessHr, geolocator);
            if (access == GeolocationAccessStatus.GeolocationAccessStatus_Denied)
                return GeolocationResult.PermissionDenied;
            if (access != GeolocationAccessStatus.GeolocationAccessStatus_Allowed)
                return GeolocationResult.Unavailable;

            TimeSpan remaining = Remaining(request.Timeout, startedAt);
            if (remaining == TimeSpan.Zero)
                return GeolocationResult.TimedOut;

            int positionHr = TryStartPosition(geolocator, request, remaining, out nint positionOperation);
            if (positionHr < 0)
                return MapNativeFailure(positionHr, geolocator);

            try
            {
                NativeAsyncResult positionWait = await WaitForAsync(
                    positionOperation,
                    remaining,
                    cancellationToken).ConfigureAwait(false);

                GeolocationResult? positionFailure = MapWaitFailure(positionWait, geolocator);
                if (positionFailure is { } failed)
                {
                    if (positionWait.State is NativeAsyncState.Canceled or NativeAsyncState.Error)
                        CloseOperation(positionOperation);
                    return failed;
                }

                int resultHr = ReadPosition(positionOperation, out GeolocationPosition position);
                CloseOperation(positionOperation);
                return resultHr >= 0 && position.IsValid
                    ? GeolocationResult.Success(position)
                    : MapNativeFailure(resultHr, geolocator);
            }
            finally
            {
                Release(positionOperation);
            }
        }
        catch
        {
            return cancellationToken.IsCancellationRequested
                ? GeolocationResult.Canceled
                : GeolocationResult.Failed;
        }
        finally
        {
            Release(accessOperation);
            Release(geolocator);
        }
    }

    private static async Task<NativeAsyncResult> WaitForAsync(
        nint operation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        int queryHr = QueryAsyncInfo(operation, out nint asyncInfo);
        if (queryHr < 0)
            return new NativeAsyncResult(NativeAsyncState.Failed, queryHr);

        long startedAt = Stopwatch.GetTimestamp();
        try
        {
            while (true)
            {
                int statusHr = ReadAsyncStatus(asyncInfo, out AsyncStatus status, out int errorCode);
                if (statusHr < 0)
                    return new NativeAsyncResult(NativeAsyncState.Failed, statusHr);

                switch (status)
                {
                    case AsyncStatus.Completed:
                        return new NativeAsyncResult(NativeAsyncState.Completed, 0);
                    case AsyncStatus.Canceled:
                        return new NativeAsyncResult(NativeAsyncState.Canceled, E_ABORT);
                    case AsyncStatus.Error:
                        return new NativeAsyncResult(NativeAsyncState.Error, errorCode);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    CancelAsyncInfo(asyncInfo);
                    return new NativeAsyncResult(NativeAsyncState.Canceled, E_ABORT);
                }
                if (Remaining(timeout, startedAt) == TimeSpan.Zero)
                {
                    CancelAsyncInfo(asyncInfo);
                    return new NativeAsyncResult(NativeAsyncState.TimedOut, HRESULT_ERROR_TIMEOUT);
                }

                TimeSpan delay = Remaining(timeout, startedAt);
                if (delay == Timeout.InfiniteTimeSpan || delay > PollInterval)
                    delay = PollInterval;
                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    CancelAsyncInfo(asyncInfo);
                    return new NativeAsyncResult(NativeAsyncState.Canceled, E_ABORT);
                }
            }
        }
        finally
        {
            Release(asyncInfo);
        }
    }

    private static GeolocationResult? MapWaitFailure(NativeAsyncResult result, nint geolocator) => result.State switch
    {
        NativeAsyncState.Completed => null,
        NativeAsyncState.Canceled => GeolocationResult.Canceled,
        NativeAsyncState.TimedOut => GeolocationResult.TimedOut,
        NativeAsyncState.Error or NativeAsyncState.Failed => MapNativeFailure(result.ErrorCode, geolocator),
        _ => GeolocationResult.Failed,
    };

    private static GeolocationResult MapStartFailure(int hr) => hr switch
    {
        E_ACCESSDENIED => GeolocationResult.PermissionDenied,
        REGDB_E_CLASSNOTREG => GeolocationResult.Unavailable,
        _ => GeolocationResult.Failed,
    };

    private static GeolocationResult MapNativeFailure(int hr, nint geolocator)
    {
        if (hr == E_ACCESSDENIED)
            return GeolocationResult.PermissionDenied;
        if (hr == HRESULT_ERROR_TIMEOUT)
            return GeolocationResult.TimedOut;
        if (hr == E_ABORT)
            return GeolocationResult.Canceled;

        PositionStatus status = ReadLocationStatus(geolocator);
        return status switch
        {
            PositionStatus.PositionStatus_Disabled => GeolocationResult.PermissionDenied,
            PositionStatus.PositionStatus_NoData or
            PositionStatus.PositionStatus_NotAvailable => GeolocationResult.Unavailable,
            _ => GeolocationResult.Failed,
        };
    }

    private static TimeSpan Remaining(TimeSpan timeout, long startedAt)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
            return timeout;
        TimeSpan elapsed = Stopwatch.GetElapsedTime(startedAt);
        return elapsed >= timeout ? TimeSpan.Zero : timeout - elapsed;
    }

    private static unsafe int TryStartAccess(out nint accessOperation, out nint geolocator)
    {
        accessOperation = 0;
        geolocator = 0;

        int initHr = EnsureRoInitialized();
        if (initHr < 0)
            return initHr;

        IGeolocatorStatics* statics = null;
        IInspectable* inspectable = null;
        IGeolocator* locator = null;
        try
        {
            using var className = new HStringHandle(RuntimeClassGeolocator);
            Guid iidStatics = __uuidof<IGeolocatorStatics>();
            int hr = RoGetActivationFactory(className.Value, &iidStatics, (void**)&statics);
            if (hr < 0 || statics == null)
                return hr < 0 ? hr : REGDB_E_CLASSNOTREG;

            hr = RoActivateInstance(className.Value, &inspectable);
            if (hr < 0 || inspectable == null)
                return hr < 0 ? hr : REGDB_E_CLASSNOTREG;

            Guid iidLocator = __uuidof<IGeolocator>();
            hr = inspectable->QueryInterface(&iidLocator, (void**)&locator);
            if (hr < 0 || locator == null)
                return hr < 0 ? hr : REGDB_E_CLASSNOTREG;

            IAsyncOperation<GeolocationAccessStatus>* access = null;
            hr = statics->RequestAccessAsync(&access);
            if (hr < 0 || access == null)
                return hr < 0 ? hr : E_ABORT;

            accessOperation = (nint)access;
            geolocator = (nint)locator;
            locator = null;
            return 0;
        }
        finally
        {
            if (locator != null) locator->Release();
            if (inspectable != null) inspectable->Release();
            if (statics != null) statics->Release();
        }
    }

    private static unsafe int TryStartPosition(
        nint geolocator,
        GeolocationRequest request,
        TimeSpan remaining,
        out nint positionOperation)
    {
        positionOperation = 0;
        var locator = (IGeolocator*)geolocator;
        PositionAccuracy accuracy = request.Accuracy == GeolocationAccuracy.High
            ? PositionAccuracy.PositionAccuracy_High
            : PositionAccuracy.PositionAccuracy_Default;
        int hr = locator->put_DesiredAccuracy(accuracy);
        if (hr < 0)
            return hr;

        IAsyncOperation<Pointer<IGeoposition>>* operation = null;
        hr = locator->GetGeopositionAsyncWithAgeAndTimeout(request.MaximumAge, remaining, &operation);
        if (hr >= 0 && operation != null)
            positionOperation = (nint)operation;
        return hr < 0 ? hr : operation == null ? E_ABORT : 0;
    }

    private static unsafe int GetAccessResult(nint operation, out GeolocationAccessStatus access)
    {
        GeolocationAccessStatus value = GeolocationAccessStatus.GeolocationAccessStatus_Unspecified;
        int hr = ((IAsyncOperation<GeolocationAccessStatus>*)operation)->GetResults(&value);
        access = value;
        return hr;
    }

    private static unsafe int ReadPosition(nint operation, out GeolocationPosition position)
    {
        position = default;
        IGeoposition* nativePosition = null;
        int hr = ((IAsyncOperation<Pointer<IGeoposition>>*)operation)
            ->GetResults((Pointer<IGeoposition>*)&nativePosition);
        if (hr < 0 || nativePosition == null)
            return hr < 0 ? hr : E_ABORT;

        IGeocoordinate* coordinate = null;
        IGeocoordinateWithPoint* coordinateWithPoint = null;
        IGeopoint* point = null;
        try
        {
            hr = nativePosition->get_Coordinate(&coordinate);
            if (hr < 0 || coordinate == null)
                return hr < 0 ? hr : E_ABORT;

            Guid iidWithPoint = __uuidof<IGeocoordinateWithPoint>();
            hr = coordinate->QueryInterface(&iidWithPoint, (void**)&coordinateWithPoint);
            if (hr < 0 || coordinateWithPoint == null)
                return hr < 0 ? hr : E_ABORT;
            hr = coordinateWithPoint->get_Point(&point);
            if (hr < 0 || point == null)
                return hr < 0 ? hr : E_ABORT;

            BasicGeoposition basic;
            double accuracy;
            if ((hr = point->get_Position(&basic)) < 0 ||
                (hr = coordinate->get_Accuracy(&accuracy)) < 0)
                return hr;

            DateTimeOffset timestamp = DateTimeOffset.UtcNow;
            WinRTDateTime nativeTimestamp;
            if (coordinate->get_Timestamp(&nativeTimestamp) >= 0 && nativeTimestamp.UniversalTime > 0)
            {
                try
                {
                    timestamp = new DateTimeOffset(DateTime.FromFileTimeUtc(nativeTimestamp.UniversalTime));
                }
                catch (ArgumentOutOfRangeException)
                {
                    // A malformed optional timestamp must not discard otherwise valid coordinates.
                }
            }

            position = new GeolocationPosition(basic.Latitude, basic.Longitude, accuracy, timestamp);
            return 0;
        }
        finally
        {
            if (point != null) point->Release();
            if (coordinateWithPoint != null) coordinateWithPoint->Release();
            if (coordinate != null) coordinate->Release();
            nativePosition->Release();
        }
    }

    private static unsafe int QueryAsyncInfo(nint operation, out nint asyncInfo)
    {
        asyncInfo = 0;
        IAsyncInfo* info = null;
        Guid iid = __uuidof<IAsyncInfo>();
        int hr = ((IInspectable*)operation)->QueryInterface(&iid, (void**)&info);
        if (hr >= 0 && info != null)
            asyncInfo = (nint)info;
        return hr < 0 ? hr : info == null ? E_ABORT : 0;
    }

    private static unsafe int ReadAsyncStatus(
        nint asyncInfo,
        out AsyncStatus status,
        out int errorCode)
    {
        AsyncStatus value = AsyncStatus.Started;
        errorCode = 0;
        var info = (IAsyncInfo*)asyncInfo;
        int hr = info->get_Status(&value);
        status = value;
        if (hr < 0)
            return hr;
        if (status == AsyncStatus.Error)
        {
            HRESULT error;
            hr = info->get_ErrorCode(&error);
            errorCode = error.Value;
        }
        return hr;
    }

    private static unsafe PositionStatus ReadLocationStatus(nint geolocator)
    {
        if (geolocator == 0)
            return PositionStatus.PositionStatus_NotAvailable;
        PositionStatus status;
        return ((IGeolocator*)geolocator)->get_LocationStatus(&status) >= 0
            ? status
            : PositionStatus.PositionStatus_NotAvailable;
    }

    private static unsafe void CancelAsyncInfo(nint asyncInfo) => ((IAsyncInfo*)asyncInfo)->Cancel();
    private static unsafe void CloseAsyncInfo(nint asyncInfo) => ((IAsyncInfo*)asyncInfo)->Close();
    private static void CloseOperation(nint operation)
    {
        if (QueryAsyncInfo(operation, out nint asyncInfo) < 0)
            return;
        try
        {
            CloseAsyncInfo(asyncInfo);
        }
        finally
        {
            Release(asyncInfo);
        }
    }
    private static unsafe void Release(nint value)
    {
        if (value != 0)
            ((IInspectable*)value)->Release();
    }

    private static int EnsureRoInitialized()
    {
        if (t_roInitialized)
            return 0;
        int hr = RoInitialize(RO_INIT_TYPE.RO_INIT_SINGLETHREADED);
        if (hr >= 0 || hr == S_FALSE || hr == RPC_E_CHANGED_MODE)
        {
            t_roInitialized = true;
            return 0;
        }
        return hr;
    }

    private enum NativeAsyncState : byte
    {
        Completed,
        Canceled,
        TimedOut,
        Error,
        Failed,
    }

    private readonly record struct NativeAsyncResult(NativeAsyncState State, int ErrorCode);

    private readonly unsafe struct HStringHandle : IDisposable
    {
        public HStringHandle(string value)
        {
            HSTRING handle;
            fixed (char* chars = value)
            {
                int hr = WindowsCreateString(chars, (uint)value.Length, &handle);
                if (hr < 0)
                    throw new InvalidOperationException($"WindowsCreateString failed (0x{hr:X8}).");
            }
            Value = handle;
        }

        public HSTRING Value { get; }
        public void Dispose() => WindowsDeleteString(Value);
    }
}
