namespace Wavee.Core;

/// <summary>Four hard-coded Connect devices; <see cref="TransferAsync"/> flips the active one.</summary>
public sealed class FakeConnectDevices : IConnectDevices
{
    readonly SimpleSubject<IReadOnlyList<PlaybackDevice>> _changed = new();
    PlaybackDevice[] _devices =
    [
        new("this", "This PC", DeviceKind.ThisDevice, IsActive: true, VolumePercent: 70),
        new("phone", "Pixel 9", DeviceKind.Phone, false, 50),
        new("living", "Living Room", DeviceKind.Speaker, false, 35),
        new("tv", "Bedroom TV", DeviceKind.Tv, false, 60),
    ];

    public FakeConnectDevices() => _changed.OnNext(_devices);

    public IReadOnlyList<PlaybackDevice> Devices => _devices;
    public IObservable<IReadOnlyList<PlaybackDevice>> DevicesChanged => _changed;

    public Task TransferAsync(string deviceId, CancellationToken ct = default)
    {
        _devices = Array.ConvertAll(_devices, d => d with { IsActive = d.Id == deviceId });
        _changed.OnNext(_devices);
        return Task.CompletedTask;
    }
}
