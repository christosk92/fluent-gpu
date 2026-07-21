using FluentGpu.Signals;

namespace Wavee;

/// <summary>Cross-surface appearance preference epoch. Settings writes bump this once so mounted and KeepAlive-cached
/// player/detail/artist surfaces re-read their persisted appearance flags immediately.</summary>
static class AppearancePrefs
{
    public static readonly Signal<int> Epoch = new(0);
    public static void Bump() => Epoch.Value = Epoch.Peek() + 1;
}
