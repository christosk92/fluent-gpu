using System.Text.Json;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Xunit;

namespace Wavee.Tests.Audio;

public class PreparedIpcContractTests
{
    [Fact]
    public void ContractV5_PrepareAndTransition_RoundTripTokensAndEligibility()
    {
        Assert.Equal(5, AudioIpcContract.Version);
        var command = new PrepareNextCommand
        {
            Generation = 42,
            CorrelationId = "corr",
            Token = "p1-a",
            TrackUri = "spotify:track:next",
            FileIdHex = "abcd",
            Format = "OggVorbis320",
            DurationMs = 123_000,
            NormalizationGainDb = -3.5f,
            HeadBytesBase64 = "AQID",
            AllowOverlap = true,
        };

        string json = JsonSerializer.Serialize(command, AudioIpcJsonContext.Default.PrepareNextCommand);
        var parsed = JsonSerializer.Deserialize(json, AudioIpcJsonContext.Default.PrepareNextCommand);

        Assert.NotNull(parsed);
        Assert.Equal(command.Token, parsed!.Token);
        Assert.Equal(command.Generation, parsed.Generation);
        Assert.True(parsed.AllowOverlap);

        var transition = new PreparedTransitionMessage
        {
            Generation = 42,
            Kind = (int)AudioTransitionKind.Started,
            Token = command.Token,
            TrackUri = command.TrackUri,
            PositionMs = 0,
            EffectiveFadeMs = 5000,
        };
        string transitionJson = JsonSerializer.Serialize(transition, AudioIpcJsonContext.Default.PreparedTransitionMessage);
        var parsedTransition = JsonSerializer.Deserialize(transitionJson, AudioIpcJsonContext.Default.PreparedTransitionMessage);
        Assert.Equal(command.Token, parsedTransition!.Token);
        Assert.Equal(5000, parsedTransition.EffectiveFadeMs);
    }
}
