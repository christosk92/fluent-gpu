using Wavee.Backend.Audio;
using Xunit;

namespace Wavee.Tests.Audio;

public class VolumeTaperTests
{
    [Fact]
    public void Amplitude_Endpoints_AreExact()
    {
        Assert.Equal(0f, VolumeTaper.Amplitude(0f));
        Assert.Equal(1f, VolumeTaper.Amplitude(1f));
    }

    [Fact]
    public void Amplitude_IsStrictlyMonotonic()
    {
        float prev = VolumeTaper.Amplitude(0f);
        for (int i = 1; i <= 100; i++)
        {
            float v = i / 100f;
            float amp = VolumeTaper.Amplitude(v);
            Assert.True(amp > prev, $"volume {v} should increase amplitude");
            prev = amp;
        }
    }

    [Fact]
    public void Amplitude_HalfSlider_IsMinus18Db()
    {
        Assert.Equal(0.125f, VolumeTaper.Amplitude(0.5f), precision: 6);
    }

    [Fact]
    public void Amplitude_ClampsOutOfRange()
    {
        Assert.Equal(0f, VolumeTaper.Amplitude(-0.5f));
        Assert.Equal(1f, VolumeTaper.Amplitude(1.5f));
    }
}
