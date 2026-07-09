using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;
using Xunit;

// The app-update seam: the Null impl is permanently inert, and a scripted fake walking every state maps to an
// AppUpdateNotification that the aggregation pins (unread) — while None contributes nothing.
public class AppUpdateSeamTests
{
    [Fact]
    public async Task Null_IsInert()
    {
        var svc = new NullAppUpdateService();
        Assert.Equal(AppUpdateState.None, svc.Current);
        Assert.Null(svc.Version);
        Assert.Null(svc.ReleaseNotesUrl);
        Assert.Null(svc.Error);
        await svc.CheckAsync(CancellationToken.None);
        await svc.DownloadAsync(CancellationToken.None);
        svc.RestartToApply();
        svc.Acknowledge();   // no throw
    }

    [Fact]
    public void None_ContributesNoNotification()
    {
        var (items, unread) = NotificationMerge.Build(UpdateNotification(new FakeAppUpdateService()),
            Array.Empty<SocialNotification>(), 0, Array.Empty<NewReleaseNotification>(), 0, Array.Empty<ActivityEntry>());
        Assert.Empty(items);
        Assert.Equal(0, unread);
    }

    [Theory]
    [InlineData(AppUpdateState.Available)]
    [InlineData(AppUpdateState.Downloaded)]
    [InlineData(AppUpdateState.Completed)]
    [InlineData(AppUpdateState.Failed)]
    public void EachState_MapsToPinnedUnreadNotification(AppUpdateState state)
    {
        var fake = new FakeAppUpdateService();
        int changes = 0;
        using var sub = fake.Changed.Subscribe(Obs<int>(_ => Interlocked.Increment(ref changes)));
        fake.Set(state, "9.9.9", "https://notes.example/9.9.9", state == AppUpdateState.Failed ? "network" : null);
        Assert.True(changes >= 1);

        var (items, unread) = NotificationMerge.Build(UpdateNotification(fake),
            Array.Empty<SocialNotification>(), 0, Array.Empty<NewReleaseNotification>(), 0, Array.Empty<ActivityEntry>());
        var n = Assert.IsType<AppUpdateNotification>(Assert.Single(items));
        Assert.Equal(state, n.State);
        Assert.Equal("9.9.9", n.Version);
        Assert.True(n.IsUnread);
        Assert.Equal(1, unread);
    }

    static AppUpdateNotification? UpdateNotification(IAppUpdateService svc)
        => svc.Current == AppUpdateState.None
            ? null
            : new AppUpdateNotification(long.MaxValue, true, svc.Current, svc.Version, svc.ReleaseNotesUrl, svc.Error);

    static IObserver<T> Obs<T>(Action<T> onNext) => new Ob<T>(onNext);
    sealed class Ob<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnNext(T value) => onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    // A scripted updater walking states — the shape a real updater would satisfy.
    sealed class FakeAppUpdateService : IAppUpdateService
    {
        readonly SimpleEvent<int> _changed = new();
        int _rev;
        public AppUpdateState Current { get; private set; } = AppUpdateState.None;
        public string? Version { get; private set; }
        public string? ReleaseNotesUrl { get; private set; }
        public string? Error { get; private set; }
        public IObservable<int> Changed => _changed;

        public void Set(AppUpdateState state, string? version = null, string? notes = null, string? error = null)
        {
            Current = state; Version = version; ReleaseNotesUrl = notes; Error = error;
            _changed.OnNext(Interlocked.Increment(ref _rev));
        }

        public Task CheckAsync(CancellationToken ct) { Set(AppUpdateState.Available, "9.9.9"); return Task.CompletedTask; }
        public Task DownloadAsync(CancellationToken ct) { Set(AppUpdateState.Downloaded, Version); return Task.CompletedTask; }
        public void RestartToApply() => Set(AppUpdateState.Completed, Version);
        public void Acknowledge() => Set(AppUpdateState.None);
    }
}
