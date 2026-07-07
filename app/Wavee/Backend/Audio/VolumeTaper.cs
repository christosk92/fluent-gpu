namespace Wavee.Backend.Audio;

/// <summary>Slider position (0..1) → linear amplitude. Cubic audio taper: perceived loudness tracks
/// the slider (0.5 ≈ −18 dB, 0.1 ≈ −60 dB), exact at 0 and 1. The Connect wire, the persisted
/// setting, and every UI surface keep the untapered slider position — this runs ONLY at the
/// amplitude boundary (WasapiRenderer).</summary>
public static class VolumeTaper
{
    public static float Amplitude(float slider01)
    {
        float v = Math.Clamp(slider01, 0f, 1f);
        return v * v * v;
    }
}
