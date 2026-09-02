using System.Text;
using System.Text.Json;
using FluentGpu.Foundation;
using FluentGpu.Lottie;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// <see cref="LottieParser"/> unit gates — small inline JSON fixtures, one behaviour each (docs/plans/wavee
/// lottie-heroes-implementation.md §6). Exercises the parser in isolation, never the compiler (see
/// <see cref="LottieCompilerTests"/> for time-mapping/track-building behaviour).
/// </summary>
public sealed class LottieParserTests
{
    private static LottieDocument Parse(string json) => LottieParser.Parse(Encoding.UTF8.GetBytes(json));

    private const string Header = "\"v\":\"5.6.5\",\"fr\":60,\"ip\":0,\"op\":100,\"w\":200,\"h\":200";

    [Fact]
    public void Header_ReadsVersionFrameRateAndCanvas()
    {
        LottieDocument doc = Parse("{" + Header + ",\"layers\":[]}");
        Assert.Equal("5.6.5", doc.Version);
        Assert.Equal(60f, doc.FrameRate);
        Assert.Equal(0f, doc.InPoint);
        Assert.Equal(100f, doc.OutPoint);
        Assert.Equal(200f, doc.Width);
        Assert.Equal(200f, doc.Height);
        Assert.Empty(doc.Layers);
    }

    [Fact]
    public void StaticScalar_IsNotAnimated()
    {
        string json = "{" + Header + ",\"layers\":[{\"ty\":3,\"ind\":1,\"ip\":0,\"op\":100,\"st\":0,\"sr\":1,"
            + "\"ks\":{\"o\":{\"a\":0,\"k\":100}}}]}";
        LottieLayer layer = Parse(json).Layers[0];
        Assert.False(layer.Transform.Opacity.IsAnimated);
        Assert.Equal(100f, layer.Transform.Opacity.Static);
    }

    [Fact]
    public void AnimatedScalar_ReadsBothKeys()
    {
        string json = "{" + Header + ",\"layers\":[{\"ty\":3,\"ind\":1,\"ip\":0,\"op\":100,\"st\":0,\"sr\":1,"
            + "\"ks\":{\"r\":{\"a\":1,\"k\":["
            + "{\"i\":{\"x\":0,\"y\":1},\"o\":{\"x\":0.167,\"y\":0.167},\"t\":0,\"s\":[0]},"
            + "{\"t\":50,\"s\":[360]}]}}}]}";
        AnimScalar r = Parse(json).Layers[0].Transform.Rotation;
        Assert.True(r.IsAnimated);
        Assert.Equal(2, r.Keys!.Length);
        Assert.Equal(0f, r.Keys[0].T);
        Assert.Equal(0f, r.Keys[0].Value);
        Assert.Equal(50f, r.Keys[1].T);
        Assert.Equal(360f, r.Keys[1].Value);
    }

    [Fact]
    public void AnimatedVec2_EaseLivesOnTheDepartingKey()
    {
        string json = "{" + Header + ",\"layers\":[{\"ty\":3,\"ind\":1,\"ip\":0,\"op\":100,\"st\":0,\"sr\":1,"
            + "\"ks\":{\"p\":{\"a\":1,\"k\":["
            + "{\"i\":{\"x\":0.1,\"y\":0.2},\"o\":{\"x\":0.3,\"y\":0.4},\"t\":0,\"s\":[0,0,0]},"
            + "{\"t\":10,\"s\":[10,20,0]}]}}}]}";
        AnimVec2 p = Parse(json).Layers[0].Transform.Position;
        Assert.True(p.IsAnimated);
        Assert.Equal(2, p.Keys!.Length);
        // Lottie stores a key's own "i"/"o" describing the segment it DEPARTS on (ScalarKey/Vec2Key/PathKey.Ease
        // doc: "segment THIS -> next") — key0 here, not key1 (LottieCompiler shifts this by one when building the
        // engine's Keyframe[], whose Easing means "the segment ARRIVING at this key" instead).
        LottieEase ease = p.Keys[0].EaseX;
        Assert.Equal(0.3f, ease.OutX);
        Assert.Equal(0.4f, ease.OutY);
        Assert.Equal(0.1f, ease.InX);
        Assert.Equal(0.2f, ease.InY);
        Assert.False(ease.Hold);
        Assert.Equal(new Point2(10f, 20f), p.Keys[1].Value);
    }

    [Fact]
    public void HoldKeyframe_SetsEaseHold()
    {
        string json = "{" + Header + ",\"layers\":[{\"ty\":3,\"ind\":1,\"ip\":0,\"op\":100,\"st\":0,\"sr\":1,"
            + "\"ks\":{\"o\":{\"a\":1,\"k\":["
            + "{\"h\":1,\"t\":0,\"s\":[0]},"
            + "{\"t\":10,\"s\":[100]}]}}}]}";
        AnimScalar o = Parse(json).Layers[0].Transform.Opacity;
        Assert.True(o.Keys![0].Ease.Hold);   // h:1 lives on the departing key, same as i/o
    }

    [Fact]
    public void TrailingTOnlyKey_CopiesThePreviousValue()
    {
        string json = "{" + Header + ",\"layers\":[{\"ty\":3,\"ind\":1,\"ip\":0,\"op\":100,\"st\":0,\"sr\":1,"
            + "\"ks\":{\"o\":{\"a\":1,\"k\":["
            + "{\"t\":0,\"s\":[42]},"
            + "{\"t\":10}]}}}]}";
        AnimScalar o = Parse(json).Layers[0].Transform.Opacity;
        Assert.Equal(42f, o.Keys![1].Value);
    }

    [Fact]
    public void SpatialTangents_ToAndTi_AreRead()
    {
        string json = "{" + Header + ",\"layers\":[{\"ty\":3,\"ind\":1,\"ip\":0,\"op\":100,\"st\":0,\"sr\":1,"
            + "\"ks\":{\"p\":{\"a\":1,\"k\":["
            + "{\"t\":0,\"s\":[0,0,0],\"to\":[1,2,0],\"ti\":[3,4,0]},"
            + "{\"t\":10,\"s\":[10,10,0]}]}}}]}";
        Vec2Key key0 = Parse(json).Layers[0].Transform.Position.Keys![0];
        Assert.Equal(new Point2(1f, 2f), key0.To);
        Assert.Equal(new Point2(3f, 4f), key0.Ti);
    }

    [Fact]
    public void Color_255Scale_IsDividedDown()
    {
        LottieShape fl = ParseFirstShape("\"ty\":\"fl\",\"c\":{\"a\":0,\"k\":[255,0,0,255]},\"o\":{\"a\":0,\"k\":100}");
        Assert.Equal(1f, fl.Color.R, 3);
        Assert.Equal(0f, fl.Color.G, 3);
        Assert.Equal(0f, fl.Color.B, 3);
        Assert.Equal(1f, fl.Color.A, 3);
    }

    [Fact]
    public void Color_ThreeComponent_DefaultsAlphaToOne()
    {
        LottieShape fl = ParseFirstShape("\"ty\":\"fl\",\"c\":{\"a\":0,\"k\":[0,0.5,1]},\"o\":{\"a\":0,\"k\":50}");
        Assert.Equal(1f, fl.Color.A);
        // Opacity is a SEPARATE channel from color alpha in this model (LottieCompiler bakes o/100 into the node's
        // own rest-pose Opacity, not into ColorF.A) — see LottieCompilerTests for the composed 0.5 result.
        Assert.Equal(50f, fl.Opacity.Static);
    }

    [Fact]
    public void DropFlags_AreCapturedVerbatim()
    {
        string json = "{" + Header + ",\"layers\":["
            + "{\"ty\":3,\"ind\":1,\"nm\":\"m\",\"ip\":0,\"op\":100,\"st\":0,\"sr\":1,\"td\":1},"
            + "{\"ty\":3,\"ind\":2,\"nm\":\"u\",\"ip\":0,\"op\":100,\"st\":0,\"sr\":1,\"tt\":2},"
            + "{\"ty\":3,\"ind\":3,\"nm\":\"blur\",\"ip\":0,\"op\":100,\"st\":0,\"sr\":1,\"ef\":[{\"ty\":29}]}"
            + "]}";
        LottieLayer[] layers = Parse(json).Layers;
        Assert.True(layers[0].IsMatteSource);
        Assert.True(layers[1].HasTrackMatte);
        Assert.True(layers[2].HasDropEffect);
    }

    [Fact]
    public void Shape_Rect_ReadsCenterSizeAndRadius()
    {
        LottieShape rc = ParseFirstShape("\"ty\":\"rc\",\"p\":{\"a\":0,\"k\":[10,20]},\"s\":{\"a\":0,\"k\":[30,40]},\"r\":{\"a\":0,\"k\":5}");
        Assert.Equal(LottieShapeType.Rect, rc.Type);
        Assert.Equal(new Point2(10f, 20f), rc.Center!.Static);
        Assert.Equal(new Point2(30f, 40f), rc.Size!.Static);
        Assert.Equal(5f, rc.Radius!.Static);
    }

    [Fact]
    public void Shape_Ellipse_ReadsCenterAndSize()
    {
        LottieShape el = ParseFirstShape("\"ty\":\"el\",\"p\":{\"a\":0,\"k\":[1,2]},\"s\":{\"a\":0,\"k\":[3,4]}");
        Assert.Equal(LottieShapeType.Ellipse, el.Type);
        Assert.Equal(new Point2(1f, 2f), el.Center!.Static);
        Assert.Equal(new Point2(3f, 4f), el.Size!.Static);
    }

    [Fact]
    public void Shape_Trim_ReadsModeTwo()
    {
        LottieShape tm = ParseFirstShape("\"ty\":\"tm\",\"s\":{\"a\":0,\"k\":0},\"e\":{\"a\":0,\"k\":50},\"o\":{\"a\":0,\"k\":0},\"m\":2");
        Assert.Equal(LottieShapeType.Trim, tm.Type);
        Assert.Equal(2, tm.TrimMode);
        Assert.Equal(0f, tm.TrimStart!.Static);
        Assert.Equal(50f, tm.TrimEnd!.Static);
    }

    [Fact]
    public void Shape_GradientFill_MidStopIsTheFiftyPercentStop()
    {
        // Two stops: offset 0 -> red, offset 1 -> blue. Mid-stop (0.5) should land exactly between them.
        LottieShape gf = ParseFirstShape(
            "\"ty\":\"gf\",\"o\":{\"a\":0,\"k\":100},\"r\":1,\"t\":1,"
            + "\"s\":{\"a\":0,\"k\":[0,0]},\"e\":{\"a\":0,\"k\":[10,0]},"
            + "\"g\":{\"p\":2,\"k\":{\"a\":0,\"k\":[0,1,0,0, 1,0,0,1]}}");
        Assert.Equal(LottieShapeType.GradientFill, gf.Type);
        Assert.NotNull(gf.Gradient);
        Assert.Equal(0.5f, gf.Gradient!.MidStop.R, 2);
        Assert.Equal(0f, gf.Gradient.MidStop.G, 2);
        Assert.Equal(0.5f, gf.Gradient.MidStop.B, 2);
    }

    [Fact]
    public void Shape_Path_ReadsVertexInOutAndClosed()
    {
        LottieShape sh = ParseFirstShape(
            "\"ty\":\"sh\",\"ks\":{\"a\":0,\"k\":{"
            + "\"i\":[[0,0],[1,1]],\"o\":[[2,2],[3,3]],\"v\":[[10,10],[20,20]],\"c\":true}}");
        Assert.Equal(LottieShapeType.Path, sh.Type);
        LottieBezier b = sh.Path!.Static;
        Assert.True(b.Closed);
        Assert.Equal(2, b.V.Length);
        Assert.Equal(new Point2(10f, 10f), b.V[0]);
        Assert.Equal(new Point2(20f, 20f), b.V[1]);
        Assert.Equal(new Point2(2f, 2f), b.O[0]);
        Assert.Equal(new Point2(1f, 1f), b.I[1]);
    }

    [Fact]
    public void UnknownShapeType_IsSkippedNotThrown()
    {
        string json = "{" + Header + ",\"layers\":[{\"ty\":4,\"ind\":1,\"ip\":0,\"op\":100,\"st\":0,\"sr\":1,\"shapes\":["
            + "{\"ty\":\"zz\",\"foo\":{\"bar\":[1,2,3]}},"
            + "{\"ty\":\"fl\",\"c\":{\"a\":0,\"k\":[1,0,0,1]},\"o\":{\"a\":0,\"k\":100}}"
            + "]}]}";
        LottieShape[] shapes = Parse(json).Layers[0].Shapes;
        Assert.Equal(2, shapes.Length);
        Assert.Equal(LottieShapeType.Unknown, shapes[0].Type);
        Assert.Equal(LottieShapeType.Fill, shapes[1].Type);
    }

    [Fact]
    public void MalformedJson_ThrowsJsonException()
    {
        Assert.ThrowsAny<JsonException>(() => LottieParser.Parse(Encoding.UTF8.GetBytes("{ this is not json")));
    }

    private static LottieShape ParseFirstShape(string shapeBody)
    {
        string json = "{" + Header + ",\"layers\":[{\"ty\":4,\"ind\":1,\"ip\":0,\"op\":100,\"st\":0,\"sr\":1,\"shapes\":[{"
            + shapeBody + "}]}]}";
        return Parse(json).Layers[0].Shapes[0];
    }
}
