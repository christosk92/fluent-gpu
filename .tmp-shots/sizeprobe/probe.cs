#:project ../../src/FluentGpu.Scene/FluentGpu.Scene.csproj
#:project ../../src/FluentGpu.Render/FluentGpu.Render.csproj
// Memory-audit probe: print the EXACT managed element sizes of every SceneStore column struct,
// DrawList command payload, and supporting type — so the per-node / per-command byte formulas in
// the audit are measured, not hand-added. Run: dotnet run probe.cs
using System.Runtime.CompilerServices;
using FluentGpu.Foundation;
using FluentGpu.Render;
using FluentGpu.Scene;
using FluentGpu.Text;

static void P<T>(string name) => Console.WriteLine($"{name} = {Unsafe.SizeOf<T>()}");

Console.WriteLine("== SceneStore SoA column element sizes (bytes) ==");
P<uint>("gen(uint)");
P<int>("topology int x7 (each)");
P<ushort>("elementTypeId(ushort)");
P<LayoutInput>("LayoutInput");
P<RectF>("RectF (bounds)");
P<NodePaint>("NodePaint");
P<DynamicTextKind>("DynamicTextKind");
P<InteractionInfo>("InteractionInfo");
P<NodeFlags>("NodeFlags");
Console.WriteLine($"delegate ref column (each) = {IntPtr.Size}");

Console.WriteLine("== sparse side-table value sizes ==");
P<ScrollState>("ScrollState");
P<TextMeasureCache>("TextMeasureCache");
P<InteractionAnim>("InteractionAnim");
P<BrushAnim>("BrushAnim");
P<TextEditState>("TextEditState");
P<TextStyle>("TextStyle");

Console.WriteLine("== DrawList command payload sizes (op int4 + payload) ==");
P<FillRoundRectCmd>("FillRoundRectCmd");
P<DrawGlyphRunCmd>("DrawGlyphRunCmd");
P<ClipCmd>("ClipCmd");
P<DrawImageCmd>("DrawImageCmd");
P<DrawRoundRectStrokeCmd>("DrawRoundRectStrokeCmd");
P<DrawShadowCmd>("DrawShadowCmd");
P<DrawGradientRectCmd>("DrawGradientRectCmd");
P<DrawGradientStrokeCmd>("DrawGradientStrokeCmd");
P<PushLayerCmd>("PushLayerCmd");
P<PopLayerCmd>("PopLayerCmd");
P<DrawArcCmd>("DrawArcCmd");
P<DrawPolylineStrokeCmd>("DrawPolylineStrokeCmd");
P<DrawTabShapeCmd>("DrawTabShapeCmd");

// Per-node SoA total: fixed columns only (the dense arrays every node pays for).
int perNode =
    Unsafe.SizeOf<uint>()              // _gen
    + Unsafe.SizeOf<int>() * 7         // _nextFree + parent/firstChild/lastChild/prevSib/nextSib/childCount
    + Unsafe.SizeOf<ushort>()          // _elementTypeId
    + Unsafe.SizeOf<LayoutInput>()
    + Unsafe.SizeOf<RectF>()
    + Unsafe.SizeOf<NodePaint>()
    + Unsafe.SizeOf<DynamicTextKind>()
    + Unsafe.SizeOf<InteractionInfo>()
    + Unsafe.SizeOf<NodeFlags>()
    + IntPtr.Size * 15;                // 15 delegate handler columns
Console.WriteLine($"== per-node dense SoA bytes = {perNode} ==");
