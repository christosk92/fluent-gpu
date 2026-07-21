using System;

namespace FluentGpu.Media;

/// <summary>
/// Normalized (a0 == 1) RBJ-cookbook biquad coefficients (spec §7.8). Computed OFF the RT path (a freq/Q/gain change
/// recompiles the coefficients and cross-ramps; §7.8) — the per-sample <see cref="BiquadState.ProcessInPlace"/> only
/// multiplies+adds. A POD struct: copy only, zero-alloc.
/// </summary>
public readonly record struct BiquadCoeffs(float B0, float B1, float B2, float A1, float A2)
{
    /// <summary>The identity (pass-through) filter.</summary>
    public static BiquadCoeffs Identity => new(1f, 0f, 0f, 0f, 0f);

    /// <summary>Compute normalized RBJ coefficients (Audio EQ Cookbook, Robert Bristow-Johnson) for
    /// <paramref name="band"/> at sample rate <paramref name="sampleRate"/>. Deterministic and alloc-free.</summary>
    public static BiquadCoeffs Design(in BiquadBand band, int sampleRate)
    {
        if (sampleRate <= 0) return Identity;
        double f0 = Math.Clamp(band.FreqHz, 1f, sampleRate * 0.5f - 1f);
        double q = band.Q <= 0f ? 0.0001 : band.Q;
        double w0 = 2.0 * Math.PI * f0 / sampleRate;
        double cw = Math.Cos(w0);
        double sw = Math.Sin(w0);
        double alpha = sw / (2.0 * q);
        double a0, a1, a2, b0, b1, b2;

        switch (band.Type)
        {
            case BiquadType.Peaking:
            {
                double a = Math.Pow(10.0, band.GainDb / 40.0);
                b0 = 1 + alpha * a;
                b1 = -2 * cw;
                b2 = 1 - alpha * a;
                a0 = 1 + alpha / a;
                a1 = -2 * cw;
                a2 = 1 - alpha / a;
                break;
            }
            case BiquadType.LowShelf:
            {
                double a = Math.Pow(10.0, band.GainDb / 40.0);
                double sqrtA = Math.Sqrt(a);
                double twoSqrtAalpha = 2.0 * sqrtA * alpha;
                b0 = a * ((a + 1) - (a - 1) * cw + twoSqrtAalpha);
                b1 = 2 * a * ((a - 1) - (a + 1) * cw);
                b2 = a * ((a + 1) - (a - 1) * cw - twoSqrtAalpha);
                a0 = (a + 1) + (a - 1) * cw + twoSqrtAalpha;
                a1 = -2 * ((a - 1) + (a + 1) * cw);
                a2 = (a + 1) + (a - 1) * cw - twoSqrtAalpha;
                break;
            }
            case BiquadType.HighShelf:
            {
                double a = Math.Pow(10.0, band.GainDb / 40.0);
                double sqrtA = Math.Sqrt(a);
                double twoSqrtAalpha = 2.0 * sqrtA * alpha;
                b0 = a * ((a + 1) + (a - 1) * cw + twoSqrtAalpha);
                b1 = -2 * a * ((a - 1) + (a + 1) * cw);
                b2 = a * ((a + 1) + (a - 1) * cw - twoSqrtAalpha);
                a0 = (a + 1) - (a - 1) * cw + twoSqrtAalpha;
                a1 = 2 * ((a - 1) - (a + 1) * cw);
                a2 = (a + 1) - (a - 1) * cw - twoSqrtAalpha;
                break;
            }
            case BiquadType.LowPass:
            {
                b0 = (1 - cw) / 2;
                b1 = 1 - cw;
                b2 = (1 - cw) / 2;
                a0 = 1 + alpha;
                a1 = -2 * cw;
                a2 = 1 - alpha;
                break;
            }
            case BiquadType.HighPass:
            {
                b0 = (1 + cw) / 2;
                b1 = -(1 + cw);
                b2 = (1 + cw) / 2;
                a0 = 1 + alpha;
                a1 = -2 * cw;
                a2 = 1 - alpha;
                break;
            }
            case BiquadType.Notch:
            {
                b0 = 1;
                b1 = -2 * cw;
                b2 = 1;
                a0 = 1 + alpha;
                a1 = -2 * cw;
                a2 = 1 - alpha;
                break;
            }
            default:
                return Identity;
        }

        double inv = 1.0 / a0;
        return new BiquadCoeffs((float)(b0 * inv), (float)(b1 * inv), (float)(b2 * inv), (float)(a1 * inv), (float)(a2 * inv));
    }
}

/// <summary>
/// A single biquad's per-channel Direct-Form-I delay state (spec §7.8) — <c>x[n-1]/x[n-2]/y[n-1]/y[n-2]</c>. Held
/// per (band × channel) so a stereo cascade keeps independent state. POD; the RT path only reads/writes these four floats.
/// </summary>
public struct BiquadState
{
    private float _x1, _x2, _y1, _y2;

    /// <summary>Reset the delay line (a discontinuity/seek — declick handled by the caller).</summary>
    public void Reset() { _x1 = _x2 = _y1 = _y2 = 0f; }

    /// <summary>Filter one sample through <paramref name="c"/> (Direct Form I). Branch-free, alloc-free.</summary>
    public float Process(float x, in BiquadCoeffs c)
    {
        float y = c.B0 * x + c.B1 * _x1 + c.B2 * _x2 - c.A1 * _y1 - c.A2 * _y2;
        _x2 = _x1; _x1 = x;
        _y2 = _y1; _y1 = y;
        return y;
    }
}
