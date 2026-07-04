using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Audio;

namespace Wavee.SpotifyLive.Audio;

/// <summary>Derives a plaintext AES key from an obfuscated PlayPlay key in the x64 AudioHost child process.</summary>
public interface IPlayPlayKeyDeriver
{
    Task<PlayPlayDeriveResult> DeriveAsync(
        ReadOnlyMemory<byte> obfuscatedKey,
        ReadOnlyMemory<byte> contentId,
        PlayPlayConfig config,
        string spotifyDllPath,
        string correlationId,
        CancellationToken ct = default);
}

/// <summary>Headless stub: always reports NeverProvisioned.</summary>
public sealed class NullPlayPlayKeyDeriver : IPlayPlayKeyDeriver
{
    public Task<PlayPlayDeriveResult> DeriveAsync(ReadOnlyMemory<byte> obfuscatedKey, ReadOnlyMemory<byte> contentId,
        PlayPlayConfig config, string spotifyDllPath, string correlationId, CancellationToken ct = default)
        => Task.FromResult(new PlayPlayDeriveResult(default, AudioKeyFailureReason.NeverProvisioned));
}
