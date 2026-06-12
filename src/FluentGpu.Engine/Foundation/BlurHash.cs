namespace FluentGpu.Foundation;

/// <summary>
/// BlurHash decoder — turns a compact ~20–30-char string into a tiny blurred preview (the LQIP placeholder used by
/// Next.js <c>blurDataURL</c>, SwiftUI/Nuke BlurHash). The engine uploads the decoded tile as an image's initial
/// texture so a recognizable blur shows instantly while the full-res art decodes off-thread. Pure math (no deps),
/// AOT-safe. Algorithm: <c>github.com/woltapp/blurhash</c>.
/// </summary>
public static class BlurHash
{
    private const string Chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz#$%*+,-.:;=?@[]^_{|}~";

    private static int Base83(string s, int from, int len)
    {
        int v = 0;
        for (int i = 0; i < len; i++)
        {
            int d = Chars.IndexOf(s[from + i]);
            if (d < 0) return -1;
            v = v * 83 + d;
        }
        return v;
    }

    private static float SrgbToLinear(int c)
    {
        float v = c / 255f;
        return v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
    }

    private static int LinearToSrgb(float v)
    {
        v = Math.Clamp(v, 0f, 1f);
        int c = v <= 0.0031308f ? (int)(v * 12.92f * 255f + 0.5f) : (int)((1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f) * 255f + 0.5f);
        return Math.Clamp(c, 0, 255);
    }

    private static float SignPow(float v, float e) => MathF.CopySign(MathF.Pow(MathF.Abs(v), e), v);

    /// <summary>Decode <paramref name="hash"/> into <paramref name="bgraPremul"/> (PREMULTIPLIED BGRA8, opaque, stride
    /// = width*4; at least width*height*4 bytes). Returns false on a malformed hash. <paramref name="width"/>×
    /// <paramref name="height"/> ≈ 32×32 is plenty (BlurHash is low-frequency; the GPU's linear sampler does the rest).</summary>
    public static bool Decode(string? hash, int width, int height, Span<byte> bgraPremul, float punch = 1f)
    {
        if (string.IsNullOrEmpty(hash) || hash.Length < 6 || width <= 0 || height <= 0) return false;
        if ((long)width * height * 4 > bgraPremul.Length) return false;

        int sizeFlag = Base83(hash, 0, 1);
        if (sizeFlag < 0) return false;
        int numX = sizeFlag % 9 + 1, numY = sizeFlag / 9 + 1;
        int total = numX * numY;
        if (hash.Length != 4 + 2 * total) return false;

        int quant = Base83(hash, 1, 1);
        if (quant < 0) return false;
        float maxValue = (quant + 1) / 166f * punch;

        Span<float> colors = total * 3 <= 256 ? stackalloc float[total * 3] : new float[total * 3];
        int dc = Base83(hash, 2, 4);
        if (dc < 0) return false;
        colors[0] = SrgbToLinear((dc >> 16) & 255);
        colors[1] = SrgbToLinear((dc >> 8) & 255);
        colors[2] = SrgbToLinear(dc & 255);
        for (int i = 1; i < total; i++)
        {
            int ac = Base83(hash, 4 + i * 2, 2);
            if (ac < 0) return false;
            float r = ac / (19 * 19), gch = (ac / 19) % 19, bch = ac % 19;
            colors[i * 3 + 0] = SignPow((r - 9) / 9f, 2f) * maxValue;
            colors[i * 3 + 1] = SignPow((gch - 9) / 9f, 2f) * maxValue;
            colors[i * 3 + 2] = SignPow((bch - 9) / 9f, 2f) * maxValue;
        }

        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float r = 0f, g = 0f, b = 0f;
                for (int j = 0; j < numY; j++)
                    for (int i = 0; i < numX; i++)
                    {
                        float basis = MathF.Cos(MathF.PI * x * i / width) * MathF.Cos(MathF.PI * y * j / height);
                        int idx = (j * numX + i) * 3;
                        r += colors[idx] * basis;
                        g += colors[idx + 1] * basis;
                        b += colors[idx + 2] * basis;
                    }
                int pi = (y * width + x) * 4;
                bgraPremul[pi + 0] = (byte)LinearToSrgb(b);   // B
                bgraPremul[pi + 1] = (byte)LinearToSrgb(g);   // G
                bgraPremul[pi + 2] = (byte)LinearToSrgb(r);   // R
                bgraPremul[pi + 3] = 255;                     // A (opaque ⇒ premultiplied == straight)
            }
        return true;
    }
}
