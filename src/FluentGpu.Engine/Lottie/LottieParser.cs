using System;
using System.Collections.Generic;
using System.Text.Json;
using FluentGpu.Foundation;

namespace FluentGpu.Lottie;

/// <summary>
/// Bodymovin/Lottie JSON → <see cref="LottieDocument"/>: a forward <see cref="Utf8JsonReader"/> walk, the same
/// posture as <see cref="FluentGpu.Localization.JsonResourceReader"/> — no DOM (<c>JsonDocument</c>), no reflection,
/// no source-generated serializer context, so it is NativeAOT/<c>TrimMode full</c>-safe and can run on any thread
/// (called from <c>LottieSource.Plan</c>, possibly off the UI thread via <c>LottieView.Preload</c>).
///
/// <para><b>Convention used throughout this file</b> (keeps ~30 mutually-recursive object/array readers consistent):
/// a <c>ReadX(ref Utf8JsonReader reader)</c> is called with the reader ALREADY positioned on X's first token
/// (<c>StartObject</c>/<c>StartArray</c>/a scalar). It consumes exactly X — for a container, by looping
/// <c>reader.Read()</c> over its children until the matching <c>EndObject</c>/<c>EndArray</c> — and returns with the
/// reader still on X's OWN last token (that closing token, or the scalar itself). The caller then calls
/// <c>reader.Read()</c> once more to step onto whatever follows. An unknown property value (any shape) is discarded
/// with <c>reader.Skip()</c> — a no-op on a scalar, a full subtree skip on a container — so new/unsupported Lottie
/// keys are silently ignored rather than breaking the walk (only structurally malformed JSON throws, via the
/// underlying <see cref="Utf8JsonReader"/>, which is the loud "authored content, bad file" failure the plan
/// specifies). A few dispatch points (which property letter a given key means depends on the ENCLOSING shape's own
/// <c>"ty"</c> — Lottie reuses single letters like <c>s</c>/<c>e</c>/<c>p</c>/<c>r</c> across shape kinds) peek ahead
/// with a cheap <see cref="Utf8JsonReader"/> STRUCT COPY (`var peek = reader;`) rather than rewinding — the copy
/// shares the same input span, so scanning it forward costs nothing but a stack copy and never disturbs the real
/// reader the caller keeps walking.</para>
/// </summary>
public static class LottieParser
{
    public static LottieDocument Parse(ReadOnlySpan<byte> utf8Json)
    {
        var doc = new LottieDocument();
        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });

        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Lottie document root must be a JSON object.");

        reader.Read();
        while (reader.TokenType != JsonTokenType.EndObject)
        {
            string prop = reader.GetString()!;
            reader.Read();
            switch (prop)
            {
                case "v": doc.Version = reader.GetString() ?? ""; break;
                case "fr": doc.FrameRate = (float)reader.GetDouble(); break;
                case "ip": doc.InPoint = (float)reader.GetDouble(); break;
                case "op": doc.OutPoint = (float)reader.GetDouble(); break;
                case "w": doc.Width = (float)reader.GetDouble(); break;
                case "h": doc.Height = (float)reader.GetDouble(); break;
                case "assets":
                    reader.Read();
                    while (reader.TokenType != JsonTokenType.EndArray)
                    {
                        ReadAsset(ref reader, doc);
                        reader.Read();
                    }
                    break;
                case "layers": doc.Layers = ReadLayersArray(ref reader, doc); break;
                default: reader.Skip(); break;   // nm, ddd, markers, fonts, chars, meta — not modeled
            }
            reader.Read();
        }
        return doc;
    }

    // ── assets / layers ──────────────────────────────────────────────────────────────────────────────────────

    private static void ReadAsset(ref Utf8JsonReader reader, LottieDocument doc)
    {
        string id = "";
        LottieLayer[]? layers = null;
        reader.Read();
        while (reader.TokenType != JsonTokenType.EndObject)
        {
            string prop = reader.GetString()!;
            reader.Read();
            switch (prop)
            {
                case "id": id = reader.GetString() ?? ""; break;
                case "layers": layers = ReadLayersArray(ref reader, doc); break;
                default: reader.Skip(); break;   // w/h/u/p/e (image asset fields), nm
            }
            reader.Read();
        }
        if (layers is not null) doc.Precomps[id] = layers;
        else doc.UnsupportedFeatures++;   // an asset with no layers[] is an image (or other unmodeled) asset
    }

    private static LottieLayer[] ReadLayersArray(ref Utf8JsonReader reader, LottieDocument doc)
    {
        var list = new List<LottieLayer>(4);
        reader.Read();
        while (reader.TokenType != JsonTokenType.EndArray)
        {
            list.Add(ReadLayer(ref reader, doc));
            reader.Read();
        }
        return list.ToArray();
    }

    private static LottieLayer ReadLayer(ref Utf8JsonReader reader, LottieDocument doc)
    {
        var layer = new LottieLayer();
        reader.Read();
        while (reader.TokenType != JsonTokenType.EndObject)
        {
            string prop = reader.GetString()!;
            reader.Read();
            switch (prop)
            {
                case "ty": layer.Type = MapLayerType(reader.GetInt32()); break;
                case "ind": layer.Index = reader.GetInt32(); break;
                case "parent": layer.Parent = reader.GetInt32(); break;
                case "nm": layer.Name = reader.GetString() ?? ""; break;
                case "ip": layer.InPoint = (float)reader.GetDouble(); break;
                case "op": layer.OutPoint = (float)reader.GetDouble(); break;
                case "st": layer.StartTime = (float)reader.GetDouble(); break;
                case "sr": layer.Stretch = (float)reader.GetDouble(); break;
                case "hd": layer.Hidden = reader.TokenType == JsonTokenType.True; break;
                case "td":
                    layer.IsMatteSource = reader.TokenType == JsonTokenType.Number
                        ? reader.GetInt32() != 0 : reader.TokenType == JsonTokenType.True;
                    break;
                case "tt": layer.HasTrackMatte = true; break;   // any value ⇒ this layer USES a matte
                case "refId": layer.RefId = reader.GetString(); break;
                case "w": layer.Width = (float)reader.GetDouble(); break;
                case "h": layer.Height = (float)reader.GetDouble(); break;
                case "ks": layer.Transform = ReadTransformObj(ref reader); break;
                case "shapes": layer.Shapes = ReadShapesArray(ref reader, doc); break;
                case "ef":
                    if (reader.TokenType == JsonTokenType.StartArray)
                    {
                        reader.Read();
                        while (reader.TokenType != JsonTokenType.EndArray)
                        {
                            int effTy = PeekEffectType(reader);
                            if (effTy is 29 or 21) layer.HasDropEffect = true;   // Gaussian Blur / Fill — decorative emboss/shadow
                            reader.Skip();
                            reader.Read();
                        }
                    }
                    break;
                case "masksProperties":
                    if (reader.TokenType == JsonTokenType.StartArray && !IsEmptyArray(reader)) doc.UnsupportedFeatures++;
                    reader.Skip();
                    break;
                case "t": reader.Skip(); break;   // text data — no Text-layer support (not present in the shipped assets)
                default: reader.Skip(); break;    // ao, bm, ddd, sc, cix, ln, cl, …
            }
            reader.Read();
        }
        return layer;
    }

    private static int PeekEffectType(Utf8JsonReader reader)   // by-value copy — a pure lookahead
    {
        if (reader.TokenType != JsonTokenType.StartObject) return -1;
        reader.Read();
        while (reader.TokenType != JsonTokenType.EndObject)
        {
            string prop = reader.GetString()!;
            reader.Read();
            if (prop == "ty") return reader.GetInt32();
            reader.Skip();
            reader.Read();
        }
        return -1;
    }

    private static bool IsEmptyArray(Utf8JsonReader reader)   // by-value copy
    {
        reader.Read();
        return reader.TokenType == JsonTokenType.EndArray;
    }

    private static LottieLayerType MapLayerType(int ty) => ty switch
    {
        0 => LottieLayerType.Precomp,
        1 => LottieLayerType.Solid,
        2 => LottieLayerType.Image,
        3 => LottieLayerType.Null,
        4 => LottieLayerType.Shape,
        5 => LottieLayerType.Text,
        _ => LottieLayerType.Unknown,
    };

    // ── shapes ────────────────────────────────────────────────────────────────────────────────────────────────

    private static LottieShape[] ReadShapesArray(ref Utf8JsonReader reader, LottieDocument doc)
    {
        var list = new List<LottieShape>(4);
        reader.Read();
        while (reader.TokenType != JsonTokenType.EndArray)
        {
            list.Add(ReadShapeItem(ref reader, doc));
            reader.Read();
        }
        return list.ToArray();
    }

    /// <summary>A group's <c>it</c> array: every item is a normal shape EXCEPT the one item whose <c>ty=="tr"</c>,
    /// which is the group's OWN transform (Lottie convention — not a paintable/nestable child), pulled out into
    /// <paramref name="groupTransform"/> instead of the returned item list.</summary>
    private static LottieShape[] ReadGroupItems(ref Utf8JsonReader reader, LottieDocument doc, out LottieTransform? groupTransform)
    {
        groupTransform = null;
        var list = new List<LottieShape>(4);
        reader.Read();
        while (reader.TokenType != JsonTokenType.EndArray)
        {
            string ty = PeekShapeType(reader);
            if (ty == "tr") groupTransform = ReadTransformObj(ref reader);
            else list.Add(ReadShapeItem(ref reader, doc));
            reader.Read();
        }
        return list.ToArray();
    }

    private static string PeekShapeType(Utf8JsonReader reader)   // by-value copy
    {
        if (reader.TokenType != JsonTokenType.StartObject) return "";
        reader.Read();
        while (reader.TokenType != JsonTokenType.EndObject)
        {
            string prop = reader.GetString()!;
            reader.Read();
            if (prop == "ty") return reader.GetString() ?? "";
            reader.Skip();
            reader.Read();
        }
        return "";
    }

    private static LottieShape ReadShapeItem(ref Utf8JsonReader reader, LottieDocument doc)
    {
        string ty = PeekShapeType(reader);
        var shape = new LottieShape { Type = MapShapeType(ty) };
        reader.Read();
        while (reader.TokenType != JsonTokenType.EndObject)
        {
            string prop = reader.GetString()!;
            reader.Read();
            switch (prop)
            {
                case "nm": shape.Name = reader.GetString(); break;
                case "hd": shape.Hidden = reader.TokenType == JsonTokenType.True; break;
                case "it": shape.Items = ReadGroupItems(ref reader, doc, out shape.GroupTransform); break;
                case "ks" when ty == "sh": shape.Path = ReadAnimPath(ref reader); break;
                case "p" when ty is "rc" or "el": shape.Center = ReadAnimVec2(ref reader); break;
                case "s" when ty is "rc" or "el": shape.Size = ReadAnimVec2(ref reader); break;
                case "s" when ty == "tm": shape.TrimStart = ReadAnimScalar(ref reader); break;
                case "s" when ty is "gf" or "gs": (shape.Gradient ??= new LottieGradient()).Start = ReadAnimVec2(ref reader).Static; break;
                case "e" when ty == "tm": shape.TrimEnd = ReadAnimScalar(ref reader); break;
                case "e" when ty is "gf" or "gs": (shape.Gradient ??= new LottieGradient()).End = ReadAnimVec2(ref reader).Static; break;
                case "o" when ty == "tm": shape.TrimOffset = ReadAnimScalar(ref reader); break;
                case "o": shape.Opacity = ReadAnimScalar(ref reader); break;
                case "m" when ty == "tm": shape.TrimMode = (byte)reader.GetInt32(); break;
                case "r" when ty == "rc": shape.Radius = ReadAnimScalar(ref reader); break;
                case "r" when ty is "fl" or "gf": shape.Rule = reader.GetInt32() == 2 ? FillRule.EvenOdd : FillRule.NonZero; break;
                case "d" when ty is "rc" or "el": shape.Direction = reader.GetInt32(); break;
                case "c" when ty is "fl" or "st": shape.Color = ReadColorField(ref reader, doc); break;
                case "w" when ty is "st" or "gs": shape.Width = ReadAnimScalar(ref reader); break;
                case "lc": shape.Cap = MapCap(reader.GetInt32()); break;
                case "lj": shape.Join = MapJoin(reader.GetInt32()); break;
                case "t" when ty is "gf" or "gs": (shape.Gradient ??= new LottieGradient()).Radial = reader.GetInt32() == 2; break;
                case "g" when ty is "gf" or "gs": ReadGradientStops(ref reader, shape.Gradient ??= new LottieGradient()); break;
                default: reader.Skip(); break;   // ind, ix, mn, np, cix, bm, ml (miter limit), d (dash array on st), …
            }
            reader.Read();
        }
        return shape;
    }

    private static LottieShapeType MapShapeType(string ty) => ty switch
    {
        "gr" => LottieShapeType.Group,
        "sh" => LottieShapeType.Path,
        "rc" => LottieShapeType.Rect,
        "el" => LottieShapeType.Ellipse,
        "fl" => LottieShapeType.Fill,
        "gf" => LottieShapeType.GradientFill,
        "st" => LottieShapeType.Stroke,
        "gs" => LottieShapeType.GradientStroke,
        "tm" => LottieShapeType.Trim,
        "tr" => LottieShapeType.Transform,
        "mm" => LottieShapeType.Merge,
        "rp" => LottieShapeType.Repeater,
        _ => LottieShapeType.Unknown,
    };

    private static LineCap MapCap(int lc) => (LineCap)Math.Clamp(lc - 1, 0, 2);
    private static LineJoin MapJoin(int lj) => (LineJoin)Math.Clamp(lj - 1, 0, 2);

    private static void ReadGradientStops(ref Utf8JsonReader reader, LottieGradient g)
    {
        int stopCount = 0;
        float[] flat = [];
        reader.Read();
        while (reader.TokenType != JsonTokenType.EndObject)
        {
            string prop = reader.GetString()!;
            reader.Read();
            switch (prop)
            {
                case "p": stopCount = reader.GetInt32(); break;
                case "k":
                    if (reader.TokenType == JsonTokenType.StartObject)
                    {
                        reader.Read();
                        while (reader.TokenType != JsonTokenType.EndObject)
                        {
                            string p2 = reader.GetString()!;
                            reader.Read();
                            if (p2 == "k") flat = ReadNumberArray(ref reader);
                            else reader.Skip();
                            reader.Read();
                        }
                    }
                    break;
                default: reader.Skip(); break;
            }
            reader.Read();
        }

        int colorFloats = stopCount * 4;
        var stops = new (float Offset, ColorF Color)[Math.Max(stopCount, 0)];
        for (int i = 0; i < stops.Length; i++)
        {
            int b = i * 4;
            if (b + 3 >= flat.Length) break;
            stops[i] = (flat[b], new ColorF(flat[b + 1], flat[b + 2], flat[b + 3], 1f));
        }
        int alphaCount = Math.Max(0, (flat.Length - colorFloats) / 2);
        for (int i = 0; i < alphaCount; i++)
        {
            int b = colorFloats + i * 2;
            float off = flat[b], alpha = flat[b + 1];
            int nearest = NearestStopIndex(stops, off);
            if (nearest >= 0) stops[nearest] = (stops[nearest].Offset, stops[nearest].Color with { A = alpha });
        }
        g.Stops = stops;
        g.MidStop = MidStopOf(stops);
    }

    private static int NearestStopIndex((float Offset, ColorF Color)[] stops, float off)
    {
        if (stops.Length == 0) return -1;
        int best = 0; float bestD = MathF.Abs(stops[0].Offset - off);
        for (int i = 1; i < stops.Length; i++)
        {
            float d = MathF.Abs(stops[i].Offset - off);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    private static ColorF MidStopOf((float Offset, ColorF Color)[] stops)
    {
        if (stops.Length == 0) return default;
        if (stops.Length == 1) return stops[0].Color;
        for (int i = 0; i < stops.Length; i++)
            if (MathF.Abs(stops[i].Offset - 0.5f) < 1e-3f) return stops[i].Color;
        for (int i = 0; i < stops.Length - 1; i++)
        {
            if (stops[i].Offset <= 0.5f && stops[i + 1].Offset >= 0.5f)
            {
                float span = stops[i + 1].Offset - stops[i].Offset;
                float t = span <= 0f ? 0f : (0.5f - stops[i].Offset) / span;
                return ColorF.Lerp(stops[i].Color, stops[i + 1].Color, t);
            }
        }
        return stops[^1].Color;
    }

    private static ColorF ReadColorField(ref Utf8JsonReader reader, LottieDocument doc)
    {
        ColorF result = default;
        bool animated = false;
        reader.Read();
        while (reader.TokenType != JsonTokenType.EndObject)
        {
            string prop = reader.GetString()!;
            reader.Read();
            switch (prop)
            {
                case "a":
                    animated = reader.TokenType == JsonTokenType.Number ? reader.GetInt32() != 0 : reader.TokenType == JsonTokenType.True;
                    break;
                case "k":
                    if (reader.TokenType == JsonTokenType.StartArray)
                    {
                        var peek = reader;
                        peek.Read();
                        if (peek.TokenType == JsonTokenType.StartObject)
                        {
                            reader.Read();
                            bool taken = false;
                            while (reader.TokenType != JsonTokenType.EndArray)
                            {
                                var raw = ReadRawKey(ref reader);
                                if (!taken) { result = MakeColor(raw.S); taken = true; }
                                reader.Read();
                            }
                        }
                        else
                        {
                            result = MakeColor(ReadNumberArray(ref reader));
                        }
                    }
                    break;
                default: reader.Skip(); break;
            }
            reader.Read();
        }
        if (animated) doc.ParseApproximations++;
        return result;
    }

    private static ColorF MakeColor(float[] c)
    {
        if (c.Length < 3) return default;
        float r = c[0], g = c[1], b = c[2], a = c.Length > 3 ? c[3] : 1f;
        if (r > 1f || g > 1f || b > 1f || a > 1f) { r /= 255f; g /= 255f; b /= 255f; a /= 255f; }
        return new ColorF(r, g, b, a);
    }

    // ── transforms (shared by a layer's "ks" and a group's "tr" — identical field shape) ───────────────────────

    private static LottieTransform ReadTransformObj(ref Utf8JsonReader reader)
    {
        var t = new LottieTransform();
        bool skewOrAxis = false;
        reader.Read();
        while (reader.TokenType != JsonTokenType.EndObject)
        {
            string prop = reader.GetString()!;
            reader.Read();
            switch (prop)
            {
                case "a": t.Anchor = ReadAnimVec2(ref reader); break;
                case "p": ReadPositionField(ref reader, t); break;
                case "s": t.Scale = ReadAnimVec2(ref reader); break;
                case "r": t.Rotation = ReadAnimScalar(ref reader); break;
                case "o": t.Opacity = ReadAnimScalar(ref reader); break;
                case "sk": skewOrAxis |= NonZeroScalar(ref reader); break;
                case "sa": skewOrAxis |= NonZeroScalar(ref reader); break;
                case "rx": skewOrAxis |= NonZeroScalar(ref reader); break;
                case "ry": skewOrAxis |= NonZeroScalar(ref reader); break;
                case "rz": skewOrAxis |= NonZeroScalar(ref reader); break;
                default: reader.Skip(); break;   // nm, ix, np, cix, bm, mn, or/h (highlight)
            }
            reader.Read();
        }
        t.HasUnsupportedSkewOrAxis = skewOrAxis;
        return t;
    }

    private static bool NonZeroScalar(ref Utf8JsonReader reader)
    {
        var s = ReadAnimScalar(ref reader);
        return s.IsAnimated || MathF.Abs(s.Static) > 1e-4f;
    }

    private static void ReadPositionField(ref Utf8JsonReader reader, LottieTransform t)
    {
        bool split = ObjectHasTrueFlag(reader, "s");
        if (!split) { t.Position = ReadAnimVec2(ref reader); return; }

        reader.Read();
        AnimScalar px = AnimScalar.Of(0f), py = AnimScalar.Of(0f);
        while (reader.TokenType != JsonTokenType.EndObject)
        {
            string prop = reader.GetString()!;
            reader.Read();
            switch (prop)
            {
                case "x": px = ReadAnimScalar(ref reader); break;
                case "y": py = ReadAnimScalar(ref reader); break;
                default: reader.Skip(); break;
            }
            reader.Read();
        }
        t.PositionX = px;
        t.PositionY = py;
        t.Position = AnimVec2.Of(new Point2(px.Static, py.Static));
    }

    private static bool ObjectHasTrueFlag(Utf8JsonReader reader, string propName)   // by-value copy
    {
        if (reader.TokenType != JsonTokenType.StartObject) return false;
        reader.Read();
        while (reader.TokenType != JsonTokenType.EndObject)
        {
            string prop = reader.GetString()!;
            reader.Read();
            if (prop == propName)
                return reader.TokenType == JsonTokenType.True || (reader.TokenType == JsonTokenType.Number && reader.GetInt32() != 0);
            reader.Skip();
            reader.Read();
        }
        return false;
    }

    // ── animatable scalar / vec2 / path (the {a,k} triad) ────────────────────────────────────────────────────

    private static AnimScalar ReadAnimScalar(ref Utf8JsonReader reader)
    {
        var result = new AnimScalar();
        bool animated = false;
        List<ScalarKey>? keys = null;
        reader.Read();
        while (reader.TokenType != JsonTokenType.EndObject)
        {
            string prop = reader.GetString()!;
            reader.Read();
            switch (prop)
            {
                case "a":
                    animated = reader.TokenType == JsonTokenType.Number ? reader.GetInt32() != 0 : reader.TokenType == JsonTokenType.True;
                    break;
                case "k":
                    if (reader.TokenType == JsonTokenType.StartArray)
                    {
                        var peek = reader;
                        peek.Read();
                        if (peek.TokenType == JsonTokenType.StartObject)
                        {
                            keys = new List<ScalarKey>(4);
                            reader.Read();
                            while (reader.TokenType != JsonTokenType.EndArray)
                            {
                                var raw = ReadRawKey(ref reader);
                                float value = raw.S.Length > 0 ? raw.S[0] : (keys.Count > 0 ? keys[^1].Value : 0f);
                                keys.Add(new ScalarKey { T = raw.T, Value = value, Ease = EaseFrom(raw, 0) });
                                reader.Read();
                            }
                        }
                        else
                        {
                            var arr = ReadNumberArray(ref reader);
                            result.Static = arr.Length > 0 ? arr[0] : 0f;
                        }
                    }
                    else
                    {
                        result.Static = (float)reader.GetDouble();
                    }
                    break;
                default: reader.Skip(); break;
            }
            reader.Read();
        }
        if (animated && keys is { Count: >= 2 }) result.Keys = keys.ToArray();
        else if (animated && keys is { Count: 1 }) result.Static = keys[0].Value;
        return result;
    }

    private static AnimVec2 ReadAnimVec2(ref Utf8JsonReader reader)
    {
        var result = new AnimVec2();
        bool animated = false;
        List<Vec2Key>? keys = null;
        reader.Read();
        while (reader.TokenType != JsonTokenType.EndObject)
        {
            string prop = reader.GetString()!;
            reader.Read();
            switch (prop)
            {
                case "a":
                    animated = reader.TokenType == JsonTokenType.Number ? reader.GetInt32() != 0 : reader.TokenType == JsonTokenType.True;
                    break;
                case "k":
                    if (reader.TokenType == JsonTokenType.StartArray)
                    {
                        var peek = reader;
                        peek.Read();
                        if (peek.TokenType == JsonTokenType.StartObject)
                        {
                            keys = new List<Vec2Key>(4);
                            reader.Read();
                            while (reader.TokenType != JsonTokenType.EndArray)
                            {
                                var raw = ReadRawKey(ref reader);
                                Point2 value = raw.S.Length >= 2 ? new Point2(raw.S[0], raw.S[1])
                                    : keys.Count > 0 ? keys[^1].Value : default;
                                Point2 to = raw.To is { Length: >= 2 } ? new Point2(raw.To[0], raw.To[1]) : default;
                                Point2 ti = raw.Ti is { Length: >= 2 } ? new Point2(raw.Ti[0], raw.Ti[1]) : default;
                                keys.Add(new Vec2Key
                                {
                                    T = raw.T, Value = value, To = to, Ti = ti,
                                    EaseX = EaseFrom(raw, 0), EaseY = EaseFrom(raw, 1),
                                });
                                reader.Read();
                            }
                        }
                        else
                        {
                            var arr = ReadNumberArray(ref reader);
                            result.Static = arr.Length >= 2 ? new Point2(arr[0], arr[1]) : arr.Length == 1 ? new Point2(arr[0], arr[0]) : default;
                        }
                    }
                    break;
                default: reader.Skip(); break;
            }
            reader.Read();
        }
        if (animated && keys is { Count: >= 2 }) result.Keys = keys.ToArray();
        else if (animated && keys is { Count: 1 }) result.Static = keys[0].Value;
        return result;
    }

    private static AnimPath ReadAnimPath(ref Utf8JsonReader reader)
    {
        var result = new AnimPath();
        bool animated = false;
        List<PathKey>? keys = null;
        reader.Read();
        while (reader.TokenType != JsonTokenType.EndObject)
        {
            string prop = reader.GetString()!;
            reader.Read();
            switch (prop)
            {
                case "a":
                    animated = reader.TokenType == JsonTokenType.Number ? reader.GetInt32() != 0 : reader.TokenType == JsonTokenType.True;
                    break;
                case "k":
                    if (reader.TokenType == JsonTokenType.StartObject)
                    {
                        result.Static = ReadLottieBezier(ref reader);
                    }
                    else if (reader.TokenType == JsonTokenType.StartArray)
                    {
                        keys = new List<PathKey>(4);
                        reader.Read();
                        while (reader.TokenType != JsonTokenType.EndArray)
                        {
                            var (t, shapeOrNull, ease) = ReadPathKeyRaw(ref reader);
                            var shape = shapeOrNull ?? (keys.Count > 0 ? keys[^1].Shape : new LottieBezier());
                            keys.Add(new PathKey { T = t, Shape = shape, Ease = ease });
                            reader.Read();
                        }
                    }
                    break;
                default: reader.Skip(); break;
            }
            reader.Read();
        }
        if (animated && keys is { Count: >= 2 }) result.Keys = keys.ToArray();
        else if (animated && keys is { Count: 1 }) result.Static = keys[0].Shape;
        return result;
    }

    private static (float T, LottieBezier? Shape, LottieEase Ease) ReadPathKeyRaw(ref Utf8JsonReader reader)
    {
        float t = 0f;
        LottieBezier? shape = null;
        bool hold = false;
        float[]? outX = null, outY = null, inX = null, inY = null;
        reader.Read();
        while (reader.TokenType != JsonTokenType.EndObject)
        {
            string prop = reader.GetString()!;
            reader.Read();
            switch (prop)
            {
                case "t": t = (float)reader.GetDouble(); break;
                case "h": hold = reader.TokenType == JsonTokenType.Number ? reader.GetInt32() != 0 : reader.TokenType == JsonTokenType.True; break;
                case "i": ReadEaseObj(ref reader, out inX, out inY); break;
                case "o": ReadEaseObj(ref reader, out outX, out outY); break;
                case "s":
                    reader.Read();   // into the wrapping array — Lottie wraps the single shape object in a 1-element array
                    if (reader.TokenType == JsonTokenType.StartObject) shape = ReadLottieBezier(ref reader);
                    reader.Read();
                    while (reader.TokenType != JsonTokenType.EndArray) { reader.Skip(); reader.Read(); }
                    break;
                default: reader.Skip(); break;   // e (redundant with next key's s)
            }
            reader.Read();
        }
        LottieEase ease = hold ? new LottieEase(0, 0, 0, 0, true) : new LottieEase(AxisAt(outX, 0), AxisAt(outY, 0), AxisAt(inX, 0), AxisAt(inY, 0), false);
        return (t, shape, ease);
    }

    private static LottieBezier ReadLottieBezier(ref Utf8JsonReader reader)
    {
        var shape = new LottieBezier();
        Point2[] i = [], o = [], v = [];
        bool closed = false;
        reader.Read();
        while (reader.TokenType != JsonTokenType.EndObject)
        {
            string prop = reader.GetString()!;
            reader.Read();
            switch (prop)
            {
                case "i": i = ReadPoint2Array(ref reader); break;
                case "o": o = ReadPoint2Array(ref reader); break;
                case "v": v = ReadPoint2Array(ref reader); break;
                case "c": closed = reader.TokenType == JsonTokenType.True; break;
                default: reader.Skip(); break;
            }
            reader.Read();
        }
        shape.I = i; shape.O = o; shape.V = v; shape.Closed = closed;
        return shape;
    }

    private static Point2[] ReadPoint2Array(ref Utf8JsonReader reader)
    {
        var list = new List<Point2>(8);
        reader.Read();
        while (reader.TokenType != JsonTokenType.EndArray)
        {
            reader.Read();   // into the inner [x,y(,z)] pair
            float x = 0f, y = 0f;
            int i = 0;
            while (reader.TokenType != JsonTokenType.EndArray)
            {
                double v = reader.GetDouble();
                if (i == 0) x = (float)v; else if (i == 1) y = (float)v;
                i++;
                reader.Read();
            }
            list.Add(new Point2(x, y));
            reader.Read();
        }
        return list.ToArray();
    }

    // ── keyframe primitives (raw scalar/vec2 keys share this shape) ─────────────────────────────────────────

    private sealed class RawKey
    {
        public float T;
        public float[] S = [];
        public bool Hold;
        public float[]? OutX, OutY, InX, InY;
        public float[]? To, Ti;
    }

    private static RawKey ReadRawKey(ref Utf8JsonReader reader)
    {
        var key = new RawKey();
        reader.Read();
        while (reader.TokenType != JsonTokenType.EndObject)
        {
            string prop = reader.GetString()!;
            reader.Read();
            switch (prop)
            {
                case "t": key.T = (float)reader.GetDouble(); break;
                case "s": key.S = ReadNumberArray(ref reader); break;
                case "h": key.Hold = reader.TokenType == JsonTokenType.Number ? reader.GetInt32() != 0 : reader.TokenType == JsonTokenType.True; break;
                case "i": ReadEaseObj(ref reader, out key.InX, out key.InY); break;
                case "o": ReadEaseObj(ref reader, out key.OutX, out key.OutY); break;
                case "to": key.To = ReadNumberArray(ref reader); break;
                case "ti": key.Ti = ReadNumberArray(ref reader); break;
                default: reader.Skip(); break;   // e (redundant with next key's s)
            }
            reader.Read();
        }
        return key;
    }

    private static LottieEase EaseFrom(RawKey raw, int dim)
        => raw.Hold ? new LottieEase(0, 0, 0, 0, true)
                    : new LottieEase(AxisAt(raw.OutX, dim), AxisAt(raw.OutY, dim), AxisAt(raw.InX, dim), AxisAt(raw.InY, dim), false);

    private static float AxisAt(float[]? arr, int d) => arr is { Length: > 0 } ? arr[Math.Min(d, arr.Length - 1)] : 0f;

    private static void ReadEaseObj(ref Utf8JsonReader reader, out float[]? x, out float[]? y)
    {
        x = null; y = null;
        reader.Read();
        while (reader.TokenType != JsonTokenType.EndObject)
        {
            string prop = reader.GetString()!;
            reader.Read();
            if (prop == "x") x = ReadNumberArray(ref reader);
            else if (prop == "y") y = ReadNumberArray(ref reader);
            else reader.Skip();
            reader.Read();
        }
    }

    private static float[] ReadNumberArray(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) return [(float)reader.GetDouble()];
        var list = new List<float>(4);
        reader.Read();
        while (reader.TokenType != JsonTokenType.EndArray)
        {
            list.Add((float)reader.GetDouble());
            reader.Read();
        }
        return list.Count == 0 ? [] : list.ToArray();
    }
}
