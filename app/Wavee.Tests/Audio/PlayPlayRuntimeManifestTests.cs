using System.Runtime.InteropServices;
using System.Text.Json;
using Wavee.Backend.Audio;
using Xunit;

namespace Wavee.Tests.Audio;

public class PlayPlayRuntimeManifestTests
{
    [Fact]
    public void Parse_And_ToPlayPlayConfig_MapsHexFields()
    {
        var m = PlayPlayRuntimeTestFixtures.SampleManifest();
        var json = JsonSerializer.Serialize(m, PlayPlayManifestJson.Default.PlayPlayRuntimeManifest);
        var parsed = JsonSerializer.Deserialize(json, PlayPlayManifestJson.Default.PlayPlayRuntimeManifest);
        Assert.NotNull(parsed);
        Assert.Null(parsed!.Validate());
        var cfg = parsed.ToPlayPlayConfig(Convert.FromHexString(parsed.DllSha256));
        Assert.Equal(PlayPlayRuntimeTestFixtures.ProcessArchLabel switch
        {
            "Arm64" => Architecture.Arm64,
            _ => Architecture.X64,
        }, cfg.Arch);
        Assert.Equal(16, cfg.PlayPlayToken.Length);
        Assert.IsType<AesKeyExtraction.OutputBufferSlice>(cfg.AesKey);
    }

    [Fact]
    public void Validate_RejectsMismatchedPackIdArch()
    {
        var m = PlayPlayRuntimeTestFixtures.SampleManifest(arch: "Arm64") with { PackId = "129300667-x64" };
        Assert.Contains("packId", m.Validate()!, StringComparison.Ordinal);
    }
}
