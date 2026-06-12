# The RHI, PAL, and Text seams

This page is for engine contributors working **at or below the swap line** — adding a platform backend, changing the
GPU device contract, extending the text seam, or writing a test against the headless backends. If you are building an
app, you never touch these interfaces; start at [Building apps with FluentGpu](../app-authors/index.md).

The seams are the line that makes "from-scratch GPU renderer" tractable: **everything above the line sees only POD
descriptors, generational handles, spans, and an opaque `NativeHandle`; everything below it is OS- and GPU-specific and
lives in a leaf assembly that nothing but `FluentGpu.Hosting` is allowed to reference.** Swap the leaf — a real Windows
backend for a headless test double, D3D12 for Metal later — and nothing above the line recompiles. That is not an
aspiration; it is the property the entire `~60`-check verification harness exploits to run with **no GPU and no
window** ([the harness](#verifying-textservicesseamchecks-and-the-headless-discipline) at the end).

> The architecture authority for these contracts is the design corpus, not this page. The PAL/RHI interface
> vocabulary and the Win32/D3D12 reference impl are owned by [`subsystems/pal-rhi.md`](../../../design/subsystems/pal-rhi.md);
> the text seam by [`subsystems/text.md`](../../../design/subsystems/text.md); the threading model by
> [`subsystems/threading-render-seam.md`](../../../design/subsystems/threading-render-seam.md). This page is the
> task-oriented working view: where the seams live in `src/`, what a backend must implement, and how to verify. Where
> a doc and the source disagree, the source wins.

## Why three swappable seams (PAL platform, RHI GPU, Text)

There are three distinct OS dependencies a UI engine has, and they fail to be swappable for different reasons, so they
are three separate interfaces, each in its own interface-only assembly:

- **PAL — the platform/OS seam** (`FluentGpu.Pal`): windows, the message pump/app loop, input, the clipboard, IME,
  system colors, backdrop (Mica), video presentation, virtual memory, image decode. This is *coupled to the OS message
  thread* — `WndProc`, DPI, and the modal move/resize loop live here.
- **RHI — the render-hardware seam** (`FluentGpu.Rhi`): the GPU device, the swapchain, and the one hot call that turns
  the POD DrawList into GPU work. This is *coupled to COM/D3D12 and the render thread* — every `ComPtr` lives below it.
- **Text — the DirectWrite seam** (`FluentGpu.Text`): font resolution, itemize/shape/raster/atlas, layout, hit-test,
  and the Yoga measure bridge. This is *coupled to DirectWrite and Unicode*, and it is the one seam the layout pass
  calls synchronously during measure.

Splitting them three ways is what lets the **headless** path exist: a test can take the real reconciler, layout, scene,
and recorder and run them against `HeadlessPlatformApp` + `HeadlessGpuDevice` + `HeadlessFontSystem` with deterministic
behavior. It is also the macOS plan — `Pal.Cocoa` / `Rhi.Metal` / `Text.CoreText` implement the *same* interfaces
([`macos-debt-ledger.md`](../../../design/macos-debt-ledger.md)).

The assembly rule is binding and the canon gate enforces it ([`foundations.md` §7](../../../design/foundations.md)):

1. No interface assembly references an impl (`FluentGpu.Render` references `FluentGpu.Rhi`, **never** `Rhi.D3D12`).
2. Impl assemblies (`*.Windows`, `*.D3D12`, `*.DirectWrite`, and the `*.Headless` test doubles) are **leaves**,
   referenced **only by `FluentGpu.Hosting`** — the composition root that binds an impl to each seam.
3. `FluentGpu.Foundation` (handles, allocators, `ColorF`/geometry, `StringTable`, signals) is the root with no deps;
   the seam interfaces depend only on it.

Where each seam lives in `src/`:

| Seam | Interface assembly (edit the contract here) | Real leaf | Headless leaf (tests/CI) |
|---|---|---|---|
| PAL | `src/FluentGpu.Pal/Pal.cs` (+ `Clipboard.cs`, `TextInput.cs`) | `FluentGpu.Pal.Windows` | `FluentGpu.Pal.Headless` |
| RHI | `src/FluentGpu.Rhi/Rhi.cs` | `FluentGpu.Rhi.D3D12` | `FluentGpu.Rhi.Headless` |
| Text | `src/FluentGpu.Text/Text.cs` | `FluentGpu.Text.DirectWrite` | `FluentGpu.Text.Headless` |
| Composition root | — | `src/FluentGpu.Hosting/AppHost.cs` binds all three | same `AppHost`, headless impls |

> **Honesty note on the as-built RHI.** The as-shipped `IGpuDevice` in `src/FluentGpu.Rhi/Rhi.cs` is **slimmer** than
> the full hardened surface in [`pal-rhi.md` §2](../../../design/subsystems/pal-rhi.md) (which adds an `ICommandEncoder`,
> `CreatePipeline`/`CreateBuffer`/`CreateTexture`, `CopyBufferToTexture`, device-lost tokens, and the multi-visual
> DComp present-tree). The slim surface is the *current code*; the richer surface is the *design of record* the
> backend grows into. When you extend the RHI, the design doc is where the target shape is pinned — do not invent a
> third shape. This page documents the code that exists and links the design for the rest.

## The PAL seam — `IPlatformApp` / `IPlatformWindow` / the input ring

`FluentGpu.Pal` contains **zero** Windows or COM types: `Size2`, `Point2`, `RectF`, the opaque `NativeHandle`, and POD
`InputEvent` are all an engine layer ever sees. Two interfaces carry the core.

**`IPlatformApp`** (`src/FluentGpu.Pal/Pal.cs`) — app/process lifetime and the window factory:

```csharp
public interface IPlatformApp : IDisposable
{
    IPlatformWindow CreateWindow(in WindowDesc desc);
    IClipboard Clipboard { get; }                              // UI-thread-only system clipboard
    void OpenUri(string uri);                                  // launch in the OS default handler (HyperlinkButton)
    RectF GetWorkArea(Point2 screenPointPx)                    // monitor work area for windowed-popup placement
        => RectF.Infinite;
    IPlatformPopupWindow? CreatePopupWindow(in PopupWindowDesc desc) => null;  // out-of-bounds flyout surface
}
```

**`IPlatformWindow`** — one window: client size, DPI scale, the input pump, the cursor/title/IME, and the
custom-titlebar seam (defaulted so a standard-frame backend ignores it):

```csharp
public interface IPlatformWindow : IDisposable
{
    NativeHandle Handle { get; }                 // HWND on Win; opaque to the engine
    Size2 ClientSizePx { get; }
    float Scale { get; }                         // effective post-WM_DPICHANGED DPI
    int  PumpInto(InputEventRing ring);          // drain OS input/window events into the ring, once per frame
    void WaitForWork(int timeoutMs);             // block until work or timeout (negative = forever); idle without busy-spin
    Action? PaintRequested { get; set; }         // OS demands a repaint outside the loop (modal move/resize)
    void SetCursor(CursorId id);
    void SetTitle(StringId title);
    void Show();
    IPlatformTextInput TextInput { get; }        // per-window IME/text-services seam
    // ── custom-titlebar (WindowDesc.CustomFrame); defaults are no-ops ──
    void SetTitleBarRegions(ReadOnlySpan<TitleBarRegion> regions) { }
    WindowState State => WindowState.Normal;     // pull side; change signaled via InputKind.WindowStateChanged
    bool IsActive => true;                       // pull side; change signaled via WindowFocus/WindowBlur
    void Minimize() { } void ToggleMaximize() { } void CloseWindow() { }
}
```

`WindowDesc` is the POD creation descriptor:

```csharp
public readonly record struct WindowDesc(string Title, Size2 SizePx, float Scale,
                                         bool Composited = false, bool CustomFrame = false);
```

`Composited` opts into per-pixel alpha (so a DirectComposition swapchain can show the DWM Mica backdrop through
transparent pixels); `CustomFrame` opts into engine-drawn titlebar chrome (WinUI's "extend content into titlebar" — the
platform strips the OS caption via `WM_NCCALCSIZE` and answers `WM_NCHITTEST` from the engine-reported
`TitleBarRegion`s).

### Input crosses as a POD ring, never C# events

The single most important PAL decision: **input does not cross the seam as `event Action<InputEvent>`** — it crosses as
a **host-owned POD ring** the window writes into and the host drains once per frame. This is what keeps the input edge
allocation-free under the `>1 kHz` `WM_POINTER`/`WM_INPUT` flood and what makes a test able to *synthesize* input
deterministically.

```csharp
public readonly record struct InputEvent(
    InputKind Kind, Point2 PositionPx, int Button, int KeyCode, float ScrollDelta = 0f,
    KeyModifiers Mods = KeyModifiers.None, PointerKind Pointer = PointerKind.Mouse,
    bool IsRepeat = false, uint TimestampMs = 0);

public enum InputKind : byte
{
    PointerMove = 1, PointerDown = 2, PointerUp = 3, Key = 4, Wheel = 5, Char = 6, KeyUp = 7,
    PointerCancel = 8, WindowBlur = 9, WindowFocus = 10, WindowStateChanged = 11,
}
```

`InputEventRing` is a grow-only `InputEvent[]` with `Write` / `Drain` / `Clear`. A backend's `PumpInto` move-coalesces
the OS flood and writes POD into the ring; the host `Drain()`s a `ReadOnlySpan<InputEvent>` in the input-dispatch phase.
`ScrollDelta` is in DIP, sign-oriented so positive scrolls toward the content end; `TimestampMs` drives
double/triple-click detection in `FluentGpu.Input`.

### The extended PAL seams (system colors, backdrop, video, memory, image codec)

Beyond the core window/loop/input, the PAL grows the seams the driving app needs. These are **owned by the design
corpus** ([`pal-rhi.md` §7–§8](../../../design/subsystems/pal-rhi.md)) and are pinned there, not restated here:
`ISystemColors` (accent + high-contrast + a versioned `Epoch`), `IBackdropSource` (window Mica/Acrylic via
`DwmSetWindowAttribute` / a DComp sibling visual), `IVideoPresenter` (a DComp child visual the app's `MediaPlayer`
renders into — FluentGpu never touches video pixels), `IVirtualMemory` (reserve/commit backing the `ChunkedArena`), and
`IImageCodec` (worker-thread WIC decode → CPU bytes). The clipboard and IME seams *are* in the code today and you will
hit them in the verification section: `IClipboard` (`Clipboard.cs`, with the `SequenceNumber` OS-epoch idiom) and
`IPlatformTextInput` / `ITextInputSink` (`TextInput.cs`, the IME composition lifecycle).

## The RHI seam — `IGpuDevice` and what a backend must implement

`FluentGpu.Rhi` is graphics-first and **zero-COM across the seam**: generational handles, POD descriptors, and spans
only. The as-built contract (`src/FluentGpu.Rhi/Rhi.cs`) is deliberately small — `SubmitDrawList` is the one hot path,
and image residency rides two explicit calls:

```csharp
public interface IGpuDevice : IDisposable
{
    string BackendName { get; }
    ISwapchain CreateSwapchain(in SwapchainDesc desc);

    // PRIMARY hot path: record + batch + submit the per-frame DrawList POD command stream.
    void SubmitDrawList(ReadOnlySpan<byte> drawList, ReadOnlySpan<ulong> sortKeys, in FrameInfo ctx);

    // Hand decoded PREMULTIPLIED BGRA8 pixels for an image id; the backend stages+uploads, keyed by id.
    void UploadImage(int imageId, ReadOnlySpan<byte> pbgra8, int w, int h);
    void EvictImage(int imageId) { }                          // residency manager freed it (deferred behind the fence)
}

public interface ISwapchain : IDisposable
{
    Size2 SizePx { get; }
    void Resize(Size2 px);
    void Present();
}
```

The two POD records that cross:

```csharp
public readonly record struct FrameInfo(Size2 SizePx, float Scale, ColorF Clear);   // per-frame context at submit
public readonly record struct SwapchainDesc(NativeHandle PresentTarget, Size2 SizePx);
```

**What a backend must do, concretely**, reading `SubmitDrawList`: walk the POD opcode stream (the `DrawOp` byte tag
followed by a fixed-size `*Cmd` payload per opcode — `FillRoundRect`, `DrawGlyphRun`, `PushClip`/`PopClip`, `DrawImage`,
`DrawRoundRectStroke`, `DrawShadow`, `DrawArc`, `DrawPolylineStroke`, `DrawGradientRect`/`DrawGradientStroke`,
`PushLayer`/`PopLayer`, `DrawTabShape`) with **concrete, devirtualized** types — no per-draw interface dispatch — and
turn each into GPU work (or, headless, into an inspectable list). The opcode set and their `*Cmd` shapes are owned by
[`gpu-renderer.md`](../../../design/subsystems/gpu-renderer.md) and `FluentGpu.Render`; a backend *consumes* them, it
does not define them. The `HeadlessGpuDevice` decode loop below is the exact, minimal reference for "how to read the
stream."

The richer device surface a D3D12-class backend needs — an `ICommandEncoder`, immutable PSO creation, buffer/texture
creation, `CopyBufferToTexture` + the texture-staging ring, device-lost tokens, and the DXGI flip-model + multi-visual
DComp present-tree — is the design of record in [`pal-rhi.md` §2–§5](../../../design/subsystems/pal-rhi.md). Build
toward that shape; do not improvise a parallel one.

## The Text seam — itemize/shape/raster/atlas/layout and the Yoga measure bridge

`FluentGpu.Text` is portable (interface + POD; zero DirectWrite types). The as-built seam the layout and editor paths
call is `IFontSystem` (`src/FluentGpu.Text/Text.cs`) — measurement plus the editor queries, all answered from **the
same layout pipeline** so hit-testing matches rendering exactly:

```csharp
public interface IFontSystem
{
    // Measure a string under a style → intrinsic content size (+ baseline + decoration metrics). Feeds layout.
    TextMetrics Measure(StringId text, in TextStyle style, float maxWidth = float.PositiveInfinity);

    // Editor queries (default: throw — the experimental GDI path cannot host editable text):
    int  HitTestText(ReadOnlySpan<char> text, in TextStyle style, float maxWidth, Point2 point, out bool trailing);
    void GetCaret(ReadOnlySpan<char> text, in TextStyle style, float maxWidth, int charIndex,
                  out float x, out float lineTop, out float lineHeight, out int lineIndex);
    int  GetRangeRects(ReadOnlySpan<char> text, in TextStyle style, float maxWidth, int start, int end, Span<RectF> rects);
}
```

`TextStyle` is the measure-cache key — *record equality over every field is load-bearing*, so every layout-affecting
input (family, size, weight, wrap, trim, max-lines, character spacing, line height, line-bounds, and the inline-run
`SpanRunId`) lives in it:

```csharp
public readonly record struct TextStyle(StringId FontFamily, float SizeDip, ushort Weight,
    TextWrap Wrap = TextWrap.NoWrap, TextTrim Trim = TextTrim.None, int MaxLines = 0,
    float CharSpacing = 0f, float LineHeight = float.NaN,
    LineStacking Stacking = LineStacking.MaxHeight, TextLineBounds LineBounds = TextLineBounds.Full,
    int SpanRunId = 0);

public readonly record struct TextMetrics(Size2 Size, float Baseline,
    float UnderlineY = 0f, float UnderlineThickness = 0f, float StrikeY = 0f);
```

**The Yoga measure bridge.** Layout (`FluentGpu.Layout`) does not know about glyphs; a text node's intrinsic size comes
from calling `IFontSystem.Measure` during the measure descent. The design contract is that this happens through **one
`static readonly` measure function plus a generational handle in the node's user-data slot — never a captured closure**
([`text.md` §0/§8](../../../design/subsystems/text.md)), so the layout hot path stays allocation-free. There is also a
process-default fallback for engine layers constructed before a font system is wired (the input dispatcher's read-only
text-selection/hyperlink gestures):

```csharp
public static class TextSeam { public static IFontSystem? Default; }   // last-constructed backend wins
```

The full text pipeline below the seam — itemization (BiDi/script/line-break/fallback), shaping (glyph ids + advances +
clusters), the `GlyphKey`/`PackedGlyph`/`GlyphRunRealization` realization model, the R8 + BGRA glyph atlas with its
epoch/eviction discipline, and `DrawGlyphRunCmd` emission — is owned end-to-end by
[`subsystems/text.md`](../../../design/subsystems/text.md). The DirectWrite leaf (`FluentGpu.Text.DirectWrite`) is the
Windows impl; it is render-thread-confined COM and is covered under [Windows backends](./windows-backends.md).

## The headless backends and the deterministic advance model

The headless leaves are the reason the harness can run anywhere. Each implements its seam with **deterministic,
inspectable** behavior and **no GPU/window/OS**.

**`HeadlessPlatformApp` / `HeadlessWindow`** (`src/FluentGpu.Pal.Headless/HeadlessPlatform.cs`) — the synthetic window
exposes a `QueueInput` test seam that the harness uses to *synthesize* input; `PumpInto` drains the queue into the ring.
Window state (size, scale, activation, placement) is **settable** so a test can simulate a resize, a per-monitor DPI hop
(`WM_DPICHANGED`), or a focus change; flipping `IsActive` even emits the matching `WindowFocus`/`WindowBlur` event, and
`ToggleMaximize` emits `WindowStateChanged`, exactly like the Win32 backend's `WM_ACTIVATE`/`WM_SIZE` transitions. The
clipboard records text, the IME records the composition lifecycle, and `OpenUri` records the URI instead of launching.

**`HeadlessGpuDevice`** (`src/FluentGpu.Rhi.Headless/HeadlessGpuDevice.cs`) — the CPU/null backend. It **decodes the POD
DrawList into reusable per-opcode lists** so a test asserts *what was drawn* without pixels, and it keeps capacity so
there is **no per-frame managed allocation once warmed**. The inspectable surface (a sample):

```csharp
public string BackendName => "Headless";
public ColorF LastClear { get; private set; }
public IReadOnlyList<FillRoundRectCmd> LastRects   => _rects;
public IReadOnlyList<DrawGlyphRunCmd>  LastGlyphs  => _glyphs;
public IReadOnlyList<ClipCmd>          LastClips   => _clips;
public IReadOnlyList<DrawImageCmd>     LastImages  => _imageDraws;
public IReadOnlyList<PushLayerCmd>     LastLayers  => _layers;     // acrylic (Kind 0) + opacity groups (Kind 1)
public int ClipBalance { get; private set; }                       // must be 0 at end of a well-formed frame
public int LayerBalance { get; private set; }                      // push/pop balance check
public IReadOnlyDictionary<int,(int w,int h)> ResidentImages => _resident;   // residency assertions
```

The decode loop is the reference for any backend reading the stream — clear the lists (retaining capacity), then walk
the bytes opcode-by-opcode:

```csharp
public void SubmitDrawList(ReadOnlySpan<byte> drawList, ReadOnlySpan<ulong> sortKeys, in FrameInfo ctx)
{
    _rects.Clear();  _glyphs.Clear();  _clips.Clear();  /* … retains capacity → 0 alloc after warmup … */
    LastClear = ctx.Clear;  FrameCount++;
    int pos = 0;
    while (pos + sizeof(int) <= drawList.Length)
    {
        int op = MemoryMarshal.Read<int>(drawList.Slice(pos));  pos += sizeof(int);
        switch ((DrawOp)op)
        {
            case DrawOp.FillRoundRect:
                _rects.Add(MemoryMarshal.Read<FillRoundRectCmd>(drawList.Slice(pos)));
                pos += Unsafe.SizeOf<FillRoundRectCmd>();  break;
            case DrawOp.DrawGlyphRun:
                _glyphs.Add(MemoryMarshal.Read<DrawGlyphRunCmd>(drawList.Slice(pos)));
                pos += Unsafe.SizeOf<DrawGlyphRunCmd>();   break;
            // … PushClip/PopClip (balance++/--), DrawImage, strokes, shadows, arcs, gradients,
            //    PushLayer/PopLayer (layerBalance++/--), DrawTabShape …
            default: return;   // unknown opcode → stop (corrupt-stream guard)
        }
    }
}
```

`UploadImage` appends to a never-cleared `Uploads` log (one entry per decode completion, so the log is the history) and
marks the id resident; `EvictImage` removes it and records the eviction — both directly assertable.

**`HeadlessFontSystem`** and **the deterministic advance model** (`src/FluentGpu.Text.Headless/HeadlessFontSystem.cs`) —
this is the keystone that makes text-layout goldens **exact math** instead of font-dependent. It replaces DirectWrite
with uniform, reproducible metrics. The headless checks are written against these exact constants:

- **advance per char** = `SizeDip × 0.55` (weight `< 600`) or `SizeDip × 0.62` (weight `≥ 600`), plus
  `SizeDip × CharSpacing / 1000`. Every UTF-16 unit is one cell (spaces and control chars included). The old bool-`Bold`
  model maps exactly: `Bold ≡ 700 → 0.62`, default `≡ 400 → 0.55`.
- **line height** = `SizeDip × 1.4`; **baseline** = `SizeDip × 1.1` (down from the line top). `TextLineBounds.Tight`
  trims to `SizeDip × 0.7`.
- **underline** at `baseline + 1` (thickness 1); **strikethrough** at `SizeDip × 0.8`.
- **wrap** = deterministic greedy word-wrap on `' '` runs, engaged only when the style wraps ∧ `maxWidth` is finite ∧
  the single-line width exceeds it; `MaxLines` caps the count and drops the over-cap remainder (WinUI clips whole lines).

So at `FontSize 14`, advance is `14 × 0.55 = 7.7` DIP/char and line height is `14 × 1.4 = 19.6` DIP — the numbers the
editor checks assert against literally (see `EditableTextCoreChecks` in the harness:
`const float Adv = 14f * 0.55f;`). The critical invariant: `Measure`, `HitTestText`, `GetCaret`, and `GetRangeRects` all
run the **same** internal line walk (`LayoutLines`), so a headless hit-test agrees with headless layout to the pixel —
the same "queries come from the layout pipeline" guarantee the real DirectWrite leaf must honor.

Wiring all four into a host is one constructor (this is exactly the harness's setup — see
[getting-started → run headless](../../guide/getting-started.md#run-headless-tests--ci--agents)):

```csharp
var strings = new StringTable();
using var app = new HeadlessPlatformApp();
var window   = new HeadlessWindow(new WindowDesc("test", new Size2(1280, 800), 1f));
window.Show();
var device   = new HeadlessGpuDevice();
var fonts    = new HeadlessFontSystem(strings);
using var host = new AppHost(app, window, device, fonts, strings, new App());

host.RunFrame();                                                       // drive frames deterministically
window.QueueInput(new InputEvent(InputKind.PointerDown, pt, 0, 0));    // synthesize input
host.RunFrame();
// assert on host.Scene (post-layout bounds/flags) and device.LastGlyphs/LastRects (what was recorded)
```

The `AppHost` constructor that ties the seams together (`src/FluentGpu.Hosting/AppHost.cs`):

```csharp
public AppHost(IPlatformApp app, IPlatformWindow window, IGpuDevice device, IFontSystem fonts,
               StringTable strings, Component root, ImageCache? images = null, IFrameTimeSource? frameTime = null)
```

`AppHost` is the **only** type that takes all three seam interfaces at once — it is the composition root. The real
Windows app substitutes `Win32Platform` / `D3D12Device` / `DirectWriteFontSystem` for the same parameters
(`src/FluentGpu.WindowsApp/FluentApp.cs`); nothing else in the frame loop changes.

## The render-thread seam and threading model (the render thread owns every ComPtr)

The seams are also a **threading** boundary, and this is the single rule that makes "COM under NativeAOT" safe by
construction rather than by audit:

> **The render thread is the sole owner of every `ComPtr`** — the RHI handle table, the PSO cache, the upload/staging
> rings, the GPU fence, the deferred-delete ring, and the swapchain/DComp visuals. **The UI thread touches zero COM,
> ever.** It cannot acquire a backbuffer, map a buffer, or call `Present`. Refcount races are not audited; they are
> *impossible*, because exactly one thread can `AddRef`/`Release`/`Dispose`.

Concretely, across the seam:

- The **PAL window + message pump run on the UI thread** (`WndProc` and DPI are coupled to input and to the OS modal
  loops). The PAL system-state reads (`ISystemColors`, locale) are UI-thread reads of OS state — no GPU device touch.
- The **RHI device, swapchain, DComp tree, and `Present` run on the render thread**. `SubmitDrawList` is render-thread
  work.
- The **text seam splits**: itemize/shape/wrap/measure + the caches are UI-thread (called during the layout phase);
  glyph raster + atlas GPU upload are render-thread (they touch `ComPtr` and the RHI). In v1 the DirectWrite leaf calls
  are render-thread-confined and serialized — the DWrite factory is a shared process-lifetime root, but every *call*
  through it is thread-confined.

The hand-off between UI and render is an **immutable POD `SceneFrame` snapshot** (a triple-buffered slot, published with
one release-store / acquire-load happens-before) plus a few cross-thread single-aligned words (device-lost reason,
present-ack seq, resize request). `SceneFrame` transfers *handles* (POD), **never a `ComPtr` by reference**. This is
the deep design owned by [`threading-render-seam.md`](../../../design/subsystems/threading-render-seam.md) — the
`SceneFramePublisher`, the consume-gated quarantine (`Quarantine = RenderInFlightDepth + 1`), the retire-fence
handshake, and the device-lost rendezvous all live there.

The crucial **build-order** honesty: **v1 ships single-thread-correct first**. The UI thread both produces and consumes
the snapshot (`Quarantine = 0`), so "the render thread owns every `ComPtr`" is satisfied trivially by there being one
thread, and `AppHost.RunFrame()` runs every phase inline. The interfaces and ownership boundaries above are authored so
the render-thread split is a *thread move, not a redesign*. Nothing on this page requires parallelism to be correct —
and the harness validates the single-thread topology that ships today. (This is the corpus's "**decoupled, not
invincible**" line: a sustained GPU stall still bounds back to the UI thread.)

## Verifying: `TextServicesSeamChecks` and the headless discipline

The rule that outranks the others for engine work: **verify with the headless harness, never by eye.** The
`FluentGpu.VerticalSlice` is the source of truth — `~60` cross-seam golden checks, no GPU and no window, in seconds:

```bash
dotnet build src/FluentGpu.VerticalSlice                 # clean build first
dotnet run   --project src/FluentGpu.VerticalSlice       # expect: ALL CHECKS PASSED
```

The harness *is* a seam test: every check substitutes the three headless backends for the real ones and asserts on the
recorded DrawList (`device.LastGlyphs`/`LastRects`/…), the post-layout scene (`host.Scene`), and `FrameStats`. When you
change a seam, that is where you prove it.

**`TextServicesSeamChecks()`** (`src/FluentGpu.VerticalSlice/Program.cs`, ~line 5229) is the dedicated PAL text-services
seam test — it exercises `IClipboard` and `IPlatformTextInput` / `ITextInputSink` end-to-end against the headless impls,
touching no OS. It asserts two things:

```csharp
static void TextServicesSeamChecks()
{
    using var app = new HeadlessPlatformApp();
    var clip = app.Clipboard;
    uint seq0 = clip.SequenceNumber;
    clip.SetText("héllo ✂");
    bool roundTrip = clip.TryGetText(out var read) && read == "héllo ✂" && clip.SequenceNumber == seq0 + 1;
    ((HeadlessClipboard)clip).Clear();
    bool cleared = !clip.TryGetText(out _) && clip.SequenceNumber == seq0 + 2;
    Check("W0a.1 clipboard seam: unicode round-trip + epoch bumps + clear", roundTrip && cleared, /* … */);

    var window = new HeadlessWindow(new WindowDesc("ime", new Size2(100, 100), 1f));
    var ti = (HeadlessTextInput)window.TextInput;
    var sink = new RecordingSink();
    ti.SetSink(sink);
    ti.BeginComposition();          // not editable yet → must no-op
    ti.SetEditable(true);
    ti.BeginComposition();
    ti.UpdateComposition("にほ", 2, new ImeClause(0, 2, ImeClauseKind.Input));
    ti.UpdateComposition("日本", 2, new ImeClause(0, 2, ImeClauseKind.TargetConverted));
    ti.Commit("日本");
    // asserts the sink saw exactly: start → upd:にほ → upd:日本 → commit:日本 → end, and cancel-on-blur.
}
```

- **The clipboard seam**: a Unicode round-trip (`SetText` → `TryGetText`) and the `SequenceNumber` OS-epoch bumping on
  each write and on `Clear` — the versioned-external-store idiom a context menu uses to gate its Paste item without
  polling content.
- **The IME seam**: the composition lifecycle through `ITextInputSink` — that `BeginComposition` **no-ops until
  `SetEditable(true)`** (the IME never composes over a button), the full `start → update* → commit → end` sequence with
  clause segmentation, and cancel-on-blur (focus leaving mid-composition emits an empty update + end).

**Adding a seam check.** Write a `Check("…", condition, detail)` in `Program.cs` and call it from `Main`. For a backend
behavior, assert on the headless impl's recorded state directly (`device.LastRects`, `device.ResidentImages`,
`window.LastTitleBarRegions`, the `RecordingSink` log). For an interaction you changed, assert on `FrameStats`:
`Rendered` (did a reconcile/layout run — for a compositor-only path you want `false`), `ComponentsRendered`
(the granularity proof), and `HotPhaseAllocBytes` (**must be 0** on steady frames — the near-zero-allocation guarantee,
asserted, not assumed).

> What the harness does **not** assert is on-screen D3D12 pixels — those are a separate, manual "needs-pixels" pass on
> the real Windows path. "Harness green" means the logic and the recorded DrawList are correct across the seam; it does
> not by itself mean a backend renders correctly on screen. Both bars matter; keep them distinct.

**The seam race gate** is the heavier validation that protects the *threading* seam once the render-thread split is
wired: a soak build (under the `FGGUARD` define) that pauses the render thread for several publishes and asserts the
consume-gated quarantine holds — no `SceneStore` slot is reclaimed until `_lastConsumedSeq` advances — sweeping
channel-capacity and reader-stall as fuzz parameters. It is owned by
[`validation.md`](../../../design/subsystems/validation.md) and
[`threading-render-seam.md` §5/§11/§18](../../../design/subsystems/threading-render-seam.md); since the as-built engine
ships single-thread (`Quarantine = 0`), the gate's relationship-under-stall is the contract it must keep when the split
lands, not a daily run today.

## Canon links

- **[`subsystems/pal-rhi.md`](../../../design/subsystems/pal-rhi.md)** — authoritative for the PAL/RHI interface
  vocabulary, the Win32 reference impl, the D3D12 backend (device/queue/fence/heaps, `SubmitDrawList`,
  `CopyBufferToTexture` + the staging ring + the per-bucket texture pool), the DXGI flip-model + multi-visual DComp
  present-tree, device-lost handling, and the extended PAL seams (`ISystemColors`/`IBackdropSource`/`IVideoPresenter`/
  `IVirtualMemory`/`IImageCodec`).
- **[`subsystems/text.md`](../../../design/subsystems/text.md)** — authoritative for the text seam interfaces, itemize/
  shape/raster, `GlyphKey`/`PackedGlyph`/`GlyphRunRealization`, the two-level shaped/wrap cache, the glyph atlas
  epoch/eviction discipline, `DrawGlyphRunCmd` emission, the DirectWrite leaf, and the Yoga measure bridge.
- **[`subsystems/threading-render-seam.md`](../../../design/subsystems/threading-render-seam.md)** — authoritative for
  the 3-thread topology, `SceneFramePublisher`, the consume-gated quarantine, the retire-fence handshake, and the
  device-lost rendezvous (the render-thread seam, design-of-record).
- **[`foundations.md` §4/§7](../../../design/foundations.md)** — the shared PAL/RHI/Text seam vocabulary and the acyclic
  assembly graph (seams are interface-only; impls are leaves referenced only by `Hosting`).
- **[`SPEC-INDEX.md`](../../../design/SPEC-INDEX.md)** — the precedence authority; the threading-model, COM-ruling, and
  handle-layout rows are the canonical values behind this page.

**Sibling pages:** [Windows backends: D3D12, DirectWrite, Win32](./windows-backends.md) (the real leaves) ·
[Render pipeline and SceneRecorder](./render-pipeline-and-scenerecorder.md) (what fills the DrawList the RHI consumes) ·
[Frame pipeline and the verification harness](./frame-pipeline-and-verification-harness.md) (the loop and the full check
suite) · [getting-started → run headless](../../guide/getting-started.md#run-headless-tests--ci--agents) (the exact
wiring).
