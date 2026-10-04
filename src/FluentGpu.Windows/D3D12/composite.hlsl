// composite.hlsl — the retained-tile SLICE COMPOSITOR (docs/plans/scroll-gpu-retained-tiles-implementation.md §A.5,
// gpu-renderer.md §3.1). One root signature for every pass: 56 root constants (K[0..13]) + one SRV table (t0) + a point
// (s0) and a linear (s1) clamp sampler. The engine's analytic edge feather — EdgeFeatherMask.Hlsl, the ONE source of
// truth shared with the C# evaluator the headless reference runs — is prepended to this file at compile time, so
// `edgeFeather(p, rect, band, corner, misc)` below is that function verbatim.
//
// Every quad pass: K[0] = destination rect in TARGET px (x0, y0, x1, y1); K[1].xy = target size px.
//   quad passes:   K[1].zw = source texel origin (Load: texel = pos − dst.xy + K[1].zw)
//                  K[2]    = sample map (uv = (pos − K[2].xy) · K[2].zw)
//                  K[3]    = (alpha, featherOn, roundRadius, roundOn); K[4] = rounded-clip rect px
//                  K[5..8] = the packed edge feather (rect, band, corner, misc); K[9] = fill colour (premultiplied)
//                  featherOn = -1 (the video-hole erase): K[5] = a rounded rect px, K[6] = its radii (tl, tr, br, bl)
//                  instead of a feather
//                  K[10..13] = a SECOND packed edge feather (a distributed ancestor fade — the coverage is the exact
//                  product); its misc.y (intensity) = 0 disables it
//   blur passes:   K[2] = (srcTexel.xy, dir.xy); K[4..7] = bilinear-folded taps (offset, weight) pairs; K[8].x = count
//   kawase passes: K[2] = (srcTexel.xy, offset, 0); K[3] = (dst→src uv scale.xy, max uv.xy)
//   acrylic:       K[2] = sample map; K[3] = (alpha, topFeatherFrac, radius, 0); K[4] = surface rect px;
//                  K[5] = tint; K[6] = fallback; K[7] = (tintOpacity, luminosityOpacity, noiseOpacity, 0);
//                  K[8] = (max uv.xy, min uv.xy)

cbuffer C : register(b0) { float4 K[14]; };
Texture2D gSrc : register(t0);
SamplerState gPoint : register(s0);
SamplerState gLinear : register(s1);

struct V { float4 pos : SV_Position; };

V VSQuad(uint id : SV_VertexID)
{
    float2 c = float2(id & 1, (id >> 1) & 1);
    float2 px = lerp(K[0].xy, K[0].zw, c);
    V o;
    o.pos = float4(px.x / K[1].x * 2.0 - 1.0, 1.0 - px.y / K[1].y * 2.0, 0.0, 1.0);
    return o;
}

float sdRoundRect(float2 p, float4 r, float rad)
{
    float2 c = (r.xy + r.zw) * 0.5;
    float2 h = max((r.zw - r.xy) * 0.5, 0.0);
    rad = min(rad, min(h.x, h.y));
    float2 q = abs(p - c) - (h - rad);
    return min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - rad;
}

// A rounded rect with a radius per corner: r = (x0, y0, x1, y1) px, rad = (topLeft, topRight, bottomRight, bottomLeft).
float sdRoundRect4(float2 p, float4 r, float4 rad)
{
    float2 c = (r.xy + r.zw) * 0.5;
    float2 h = max((r.zw - r.xy) * 0.5, 0.0);
    float2 q = p - c;
    float rr = q.x < 0.0 ? (q.y < 0.0 ? rad.x : rad.w) : (q.y < 0.0 ? rad.y : rad.z);
    rr = min(rr, min(h.x, h.y));
    float2 d = abs(q) - (h - rr);
    return min(max(d.x, d.y), 0.0) + length(max(d, 0.0)) - rr;
}

float Coverage(float2 p)
{
    float a = K[3].x;
    if (K[3].y > 0.5) a *= edgeFeather(p, K[5], K[6], K[7], K[8]);
    else if (K[3].y < -0.5) a *= saturate(0.5 - sdRoundRect4(p, K[5], K[6]));
    if (K[13].y > 0.0) a *= edgeFeather(p, K[10], K[11], K[12], K[13]);
    if (K[3].w > 0.5) a *= saturate(0.5 - sdRoundRect(p, K[4], K[3].z));
    return a;
}

// A retained tile / region surface placed at a whole-pixel offset: an exact texel fetch (never a filtered sample — a
// 1:1 bilinear Sample is not bit-exact at high-contrast edges), times alpha · feather · rounded clip.
float4 PSLoad(V i) : SV_Target
{
    int2 t = int2(floor(i.pos.xy - K[0].xy + K[1].zw));
    return gSrc.Load(int3(t, 0)) * Coverage(i.pos.xy);
}

// A surface sampled through a scale (a blurred / downsampled result).
float4 PSSample(V i) : SV_Target
{
    float2 uv = (i.pos.xy - K[2].xy) * K[2].zw;
    return gSrc.SampleLevel(gLinear, uv, 0) * Coverage(i.pos.xy);
}

// A solid premultiplied fill (the background; with the DestOut blend, a video-hole / acrylic erase).
float4 PSFill(V i) : SV_Target
{
    return K[9] * Coverage(i.pos.xy);
}

// One separable Gaussian pass (bilinear-folded taps). The source surface is the same size as the target and was
// cleared whole, so a tap past the content reads transparent — the correct edge for a self-blur.
float4 PSBlur(V i) : SV_Target
{
    float2 uv = i.pos.xy * K[2].xy;
    float2 d = K[2].xy * K[2].zw;
    float4 s = gSrc.SampleLevel(gLinear, uv, 0) * K[4].y;
    int n = (int)K[8].x;
    [unroll] for (int t = 1; t < 8; t++)
    {
        if (t >= n) break;
        float4 tw = K[4 + (t >> 1)];
        float off = (t & 1) != 0 ? tw.z : tw.x;
        float w = (t & 1) != 0 ? tw.w : tw.y;
        s += (gSrc.SampleLevel(gLinear, uv + d * off, 0) + gSrc.SampleLevel(gLinear, uv - d * off, 0)) * w;
    }
    return s;
}

// A 2× box downsample: the bilinear sample at the centre of each 2×2 source block is exactly their average.
float4 PSDown2(V i) : SV_Target
{
    return gSrc.SampleLevel(gLinear, i.pos.xy * 2.0 * K[2].xy, 0);
}

// The dual-Kawase chain (ARM SIGGRAPH 2015 dual filter; AcrylicKawaseMath): 5-tap down, 8-tap tent up.
float4 KS(float2 uv) { return gSrc.SampleLevel(gLinear, clamp(uv, K[2].xy * 0.5, K[3].zw), 0); }

float4 PSKawaseDown(V i) : SV_Target
{
    float2 uv = i.pos.xy * K[3].xy;
    float2 h = K[2].xy * 0.5 * K[2].z;
    float4 s = KS(uv) * 4.0;
    s += KS(uv + float2(h.x, h.y));
    s += KS(uv + float2(-h.x, -h.y));
    s += KS(uv + float2(h.x, -h.y));
    s += KS(uv + float2(-h.x, h.y));
    return s / 8.0;
}

float4 PSKawaseUp(V i) : SV_Target
{
    float2 uv = i.pos.xy * K[3].xy;
    float2 h = K[2].xy * 0.5 * K[2].z;
    float4 s = KS(uv + float2(-h.x * 2.0, 0.0));
    s += KS(uv + float2(-h.x, h.y)) * 2.0;
    s += KS(uv + float2(0.0, h.y * 2.0));
    s += KS(uv + float2(h.x, h.y)) * 2.0;
    s += KS(uv + float2(h.x * 2.0, 0.0));
    s += KS(uv + float2(h.x, -h.y)) * 2.0;
    s += KS(uv + float2(0.0, -h.y * 2.0));
    s += KS(uv + float2(-h.x, -h.y)) * 2.0;
    return s / 12.0;
}

// The WinUI AcrylicBrush recipe over the blurred mini-composite of everything beneath the surface
// (AcrylicBrush.cpp:500-548): backdrop SourceOver the opaque fallback → luminosity blend → tint/colour blend → noise.
float Lum(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }
float3 ClipColor(float3 c)
{
    float l = Lum(c);
    float n = min(c.r, min(c.g, c.b));
    float x = max(c.r, max(c.g, c.b));
    if (n < 0.0) c = l + (c - l) * l / max(l - n, 1e-5);
    if (x > 1.0) c = l + (c - l) * (1.0 - l) / max(x - l, 1e-5);
    return saturate(c);
}
float3 SetLum(float3 c, float l) { return ClipColor(c + (l - Lum(c))); }

float4 PSAcrylic(V i) : SV_Target
{
    float2 p = i.pos.xy;
    float cov = saturate(0.5 - sdRoundRect(p, K[4], K[3].z));
    if (K[3].y > 0.0)
    {
        float t = (p.y - K[4].y) / max(K[4].w - K[4].y, 1e-4);
        cov *= smoothstep(0.0, K[3].y, t);
    }
    cov *= saturate(K[3].x);
    float2 uv = clamp((p - K[2].xy) * K[2].zw, K[8].zw, K[8].xy);
    float4 src = gSrc.SampleLevel(gLinear, uv, 0);
    float3 B = src.rgb + K[6].rgb * saturate(1.0 - src.a);
    float3 lumBlend = lerp(B, SetLum(B, Lum(K[5].rgb)), K[7].y);
    float3 colorBlend = SetLum(K[5].rgb, Lum(lumBlend));
    float3 res = lerp(lumBlend, colorBlend, K[7].x);
    float nz = frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453);
    res += (nz - 0.5) * K[7].z;
    return float4(res * cov, cov);
}
