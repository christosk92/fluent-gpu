# Windows backends: D3D12, DirectWrite, Win32

This page is for engine contributors working **below the seams** — the three Windows leaf assemblies that turn the
portable PAL/RHI/Text contracts into real pixels: `FluentGpu.Windows` (`D3D12/` — Direct3D 12 + DXGI + DirectComposition),
`FluentGpu.Windows` (`DirectWrite/` — the DirectWrite layout/shaping backend), and `FluentGpu.Windows` (`Pal/` — the Win32 window,
message pump, DPI, theme, and Mica). It also covers the real image pipeline (`FluentGpu.Windows` (`Wic/`) + the WIC codec) and the
screenshot-capture path the fidelity loop uses.

If you are changing the engine *above* the seams (the reconciler, layout, the recorder, signals), you want
[The RHI, PAL, and Text seams](./seams-rhi-pal-text.md) and the [Contributor Map](./contributor-map.md) instead — this
page is specifically the *Windows implementation* of those seams.

> **Design authority vs as-built.** The architecture of these leaves is owned by the design corpus —
> [`pal-rhi.md`](../../../design/subsystems/pal-rhi.md) (the PAL/RHI/present plumbing),
> [`window-backdrop-mica.md`](../../../design/subsystems/window-backdrop-mica.md) (the host backdrop),
> [`com-interop.md`](../../../design/subsystems/com-interop.md) (the COM policy), and
> [`dotnet10-csharp14-zero-alloc.md`](../../../design/dotnet10-csharp14-zero-alloc.md) (the AOT/trim baseline). This page
> does **not** restate those contracts; it links to them and documents **what the source does today**, flagging where
> the as-built leaf is a step behind the design target. When a doc and the source disagree, the source wins and the doc
> is the bug.

## The Windows bootstrap (one method wires the whole stack)

Everything starts in `FluentApp.Run` (`src/FluentGpu.WindowsApp/FluentApp.cs`) — the batteries-included entry point that
constructs every Windows leaf and hands them to the portable [`AppHost`](../../../src/FluentGpu.Engine/Hosting/AppHost.cs). It
is the most useful single read for understanding how the leaves fit together, because it is the one place the concrete
types meet:

```csharp
var strings = new StringTable();
using var app = new Win32App();
var window = (Win32Window)app.CreateWindow(new WindowDesc(title, new Size2(width, height), 1f, mica, CustomFrame: customFrame));

if (Win32Theme.AccentLight2() is { } a) Theme.Accent = ColorF.FromRgba(a.R, a.G, a.B);
else if (Win32Theme.Accent() is { } b) Theme.Accent = ColorF.FromRgba(b.R, b.G, b.B);
Win32Theme.ApplyWindowMaterial(window.Handle.Value, Theme.Dark, mica, customFrame);
if (mica) Theme.WindowBackground = ColorF.Transparent;

var fonts = new DirectWriteFontSystem(strings);
IGpuDevice device = new D3D12Device(strings, composited: mica);

using var imageFetcher = new DefaultImageFetcher(diskCache: new DiskImageCache());
using var imageDecoder = new DecodeScheduler(new WicImageCodec(), imageFetcher);
var images = new ImageCache(imageDecoder);

using var host = new AppHost(app, window, device, fonts, strings, root(), images);
host.SmoothScroll = true;
window.Show();
```

The ordering is load-bearing and worth internalizing:

1. **`Win32App` + `Win32Window`** — the window, class registration, DPI awareness, and message pump
   (`FluentGpu.Windows`, `Pal/`). The `mica` flag becomes `WindowDesc.Composited`, which makes the window
   `WS_EX_NOREDIRECTIONBITMAP` so a DirectComposition swapchain can show the DWM backdrop through transparent pixels.
2. **System accent pickup** — `Win32Theme.AccentLight2()` reads the OS `AccentPalette` (the dark-theme Light2 shade WinUI
   uses); it falls back to `Win32Theme.Accent()` (the registry `AccentColorMenu`, then the DWM colorization color). The
   result is written into the engine's `Theme.Accent`.
3. **`Win32Theme.ApplyWindowMaterial`** — sets the dark caption + the DWM system-backdrop type, and (for the right frame
   mode) extends the frame so Mica fills the client area. When `mica` is on, the engine's `Theme.WindowBackground` is
   forced to `ColorF.Transparent` so the root clears transparent and the backdrop shows through.
4. **`DirectWriteFontSystem`** — the text backend, constructed before the device because layout (phase 6, UI thread)
   needs it and the recorder's glyph path reads the *same* design advances it measured with.
5. **`D3D12Device(strings, composited: mica)`** — the RHI leaf. `composited` selects the
   `CreateSwapChainForComposition` + DComp path over the plain HWND path.
6. **The real image pipeline** — a disk-cached HTTP/2 `DefaultImageFetcher`, a worker-pool `DecodeScheduler` over the
   `WicImageCodec`, behind an `ImageCache`. (`FakeImageDecoder` is the headless stand-in; this is the live one.)

The frame loop is the tail of `Run`: `host.RunFrame()` until `window.IsClosed`, pacing with `window.WaitForWork(...)` —
`0` while there is active work (`host.HasActiveWork`), `-1` (block) when idle, and a fixed `8` ms in screenshot mode so
time-driven animations advance deterministically.

## The D3D12 RHI leaf

`D3D12Device` (`src/FluentGpu.Windows/D3D12/D3D12Device.cs`) is the reference Windows RHI backend — it implements
`IGpuDevice`/`ISwapchain` (the seam owned by [`pal-rhi.md`](../../../design/subsystems/pal-rhi.md) §2) over a real
hardware device. The COM is hand-vtable through **TerraFX.Interop.Windows** (`ID3D12Device*`, `IDXGISwapChain3*`,
`IDCompositionDevice*`, …), which is the as-built realization of the "no `ComWrappers` on the hot path" ruling (see
[the COM policy](#the-tiered-com-policy) below).

**Where to change what** in this leaf:

| If you're changing… | Edit |
|---|---|
| Device/queue/fence/heaps bring-up, swapchain creation, the per-frame submit/present, capture, resize | `src/FluentGpu.Windows/D3D12/D3D12Device.cs` |
| The SDF rounded-rect / stroke / tab-shape pipeline | `src/FluentGpu.Windows/D3D12/RoundRectPipeline.cs` |
| Drop shadows, arcs, polyline strokes, gradients | `…/ShadowPipeline.cs`, `ArcPipeline.cs`, `PolylineStrokePipeline.cs`, `GradientPipeline.cs` |
| The DirectWrite glyph atlas + run cache (the GPU text path) | `…/GlyphRenderer.cs` |
| Image textures (atlas + per-bucket pool, staging) and the image draw pipeline | `…/ImageTextureStore.cs`, `ImagePipeline.cs` |
| In-app acrylic / opacity-group layers | `…/AcrylicCompositor.cs`, `OpacityLayerCompositor.cs` |
| Live D3D12 resource accounting (the MemCensus feed) | `…/D3D12MemoryDiagnostics.cs` |

### Device + queue + fences + heaps

`InitDevice()` creates the device via the flat `[LibraryImport]` export `D3D12CreateDevice` (feature level 11_0). The
as-built adapter pick is the **default adapter with a WARP fallback** — on a VM / RDP / GPU-less box,
`_factory->EnumWarpAdapter` provides the software device and `BackendName` becomes `D3D12 (WARP)`. (The
`IDXGIFactory6` high-performance enumeration that [`pal-rhi.md`](../../../design/subsystems/pal-rhi.md) §3 describes is
the design target; the current leaf does not yet pick by power preference.)

It then builds the rest of the steady-state machinery, all keyed off a single `const uint FRAME_COUNT = 2` (the
`FLIP_DISCARD` canon from [`budgets.md`](../../../design/budgets.md), configurable 2–3 by one constant): one DIRECT
`ID3D12CommandQueue`, per-frame `ID3D12CommandAllocator`s, one `ID3D12GraphicsCommandList` (created closed, `Reset` each
frame), an `ID3D12Fence` + event for CPU/GPU sync, and an RTV descriptor heap sized to `FRAME_COUNT`. The debug layer is
opt-in behind `FG_D3D12_DEBUG` in `DEBUG` builds.

### The DXGI flip-model swapchain + DComp present

`InitSwapChain()` is the [`pal-rhi.md`](../../../design/subsystems/pal-rhi.md) §5.1 base path made concrete. The color
contract is exactly the one pinned in [`SPEC-INDEX.md`](../../../design/SPEC-INDEX.md): the swapchain buffer is
`DXGI_FORMAT_B8G8R8A8_UNORM`, `BufferCount = 2`, `SwapEffect = FLIP_DISCARD`, created
`FRAME_LATENCY_WAITABLE_OBJECT` (and `ALLOW_TEARING` on the non-composited, tearing-capable path). The composited path
takes `DXGI_ALPHA_MODE_PREMULTIPLIED` (mandatory for DWM show-through); the opaque path takes `ALPHA_MODE_IGNORE`.

Two creation paths, selected by the `composited` ctor flag:

- **Composited (Mica) path** — `CreateSwapChainForComposition(queue, …)`, then the DirectComposition tree:
  `DCompositionCreateDevice` → `CreateTargetForHwnd` → `CreateVisual` → `visual.SetContent(swapchain)` →
  `target.SetRoot(visual)` → `Commit()`. The swapchain composes onto the HWND so the DWM backdrop shows through wherever
  the engine presents transparent pixels.
- **Opaque path** — `CreateSwapChainForHwnd(queue, hwnd, …)`, no DComp tree.

After creation it QIs `IDXGISwapChain3`, calls `SetMaximumFrameLatency(FRAME_COUNT - 1)`, and grabs the
`GetFrameLatencyWaitableObject()` — the handle `WaitForLatency()` blocks on at the top of each frame to bound queued-frame
latency.

> **Single-visual today.** The leaf composes exactly **one** swapchain visual. The multi-visual present-tree (a video
> child visual z-below the UI swapchain, with the transparent premultiplied-0 hole-punch for `DrawVideoCmd.Dst`) in
> [`pal-rhi.md`](../../../design/subsystems/pal-rhi.md) §5.2 is the design target for video and is not yet wired.

### The per-frame submit path

`SubmitDrawList(ReadOnlySpan<byte> drawList, ReadOnlySpan<ulong> sortKeys, in FrameInfo ctx)` is the hot path
(phase 10–11, [`pal-rhi.md`](../../../design/subsystems/pal-rhi.md) §3.1). Each frame it:

1. `WaitForLatency()` (bound queued frames), pick the back-buffer index, `WaitForFrame(idx)` on that buffer's last fence,
   `Reset` the allocator + command list, and flush any pending image uploads (`_imageTextures.FlushUploads`).
2. Barrier the back buffer `PRESENT → RENDER_TARGET`. DPI is handled by rendering at **physical** resolution while
   mapping coordinates through a **logical** viewport (`physical / scale`), so glyphs rasterize crisp at native px while
   layout stays in DIPs.
3. Walk the POD opcode stream. The flat (no-layer) path clears the RTV and calls `SubmitStreaming`, which decodes opcodes
   (`MemoryMarshal.Read<TCmd>`) into per-pipeline instance lists, batching consecutive same-kind ops into one draw in
   **stream (painter) order** so a shadow correctly sits over the background drawn before it; glyphs always render last.
   `PushClip`/`PopClip` flush the current segment and set the scissor. The layered path (`SubmitWithLayers`, when the
   stream contains a `PushLayer`) renders into an engine canvas and runs acrylic/opacity passes before blitting back.
4. Barrier `RENDER_TARGET → PRESENT`, `Close`, `ExecuteCommandLists`, `SignalFrame` (bump the fence, record the per-frame
   value). The `Diag.Set("d3d12", …)` counters here (`rects`, `glyphInstances`, `images`, `imagesSkipped`,
   `cachedRuns`/`runsShaped`, …) are your instrument panel for the GPU path.

`Present()` issues `swapChain->Present(syncInterval, flags)` — interval 1 when vsync'd, interval 0 +
`DXGI_PRESENT_ALLOW_TEARING` when unsynced on a tearing-capable swapchain.

> **Note on the clip opcode.** `DrawOp.PushClip` is consumed by the decoder but the GPU scissor wiring is a
> needs-pixels follow-up (`TODO(step-27)` in `DecodeOne`): the headless path is the verified source of truth for clip
> *semantics*; the D3D12 leaf keeps the stream well-formed and relies on layout bounding scroll content. The
> `SubmitStreaming`/`PushScissor` path does apply `RSSetScissorRects` between clip-broken segments — read both to see the
> current state.

### Device-lost, resize, capture

- **Resize** (`Resize(w, h)`) — `WaitForGpu`, release the old back buffers, `IDXGISwapChain3.ResizeBuffers` (preserving
  `_swapChainFlags`), re-create RTVs. (The design target in
  [`pal-rhi.md`](../../../design/subsystems/pal-rhi.md) §5.4 narrows this to waiting only the in-flight fences that
  referenced *this* swapchain; the current leaf uses the simpler full-`WaitForGpu`.)
- **Device-lost** — the render-thread synchronous-HRESULT detection + Volatile-word marshal + rebuild-from-CPU-state
  recovery is the design contract in [`pal-rhi.md`](../../../design/subsystems/pal-rhi.md) §6 and
  [`com-interop.md`](../../../design/subsystems/com-interop.md) §11. Single-thread v1 ships first
  ([`pal-rhi.md`](../../../design/subsystems/pal-rhi.md) §0), so device-lost recovery is a forward item, not yet a code
  path in this leaf — `Check()` throws on a failed HRESULT today.

## DirectWrite text

`DirectWriteFontSystem` (`src/FluentGpu.Windows/DirectWrite/DirectWriteFontSystem.cs`) implements `IFontSystem` — the text
seam owned by `FluentGpu.Text`. It is a thin wrapper over `TextLayoutEngine`, and its whole reason for existing is one
invariant: **measure ≡ hit-test ≡ render.** Every member (measurement on the UI thread at phase-6 layout, *and* the
editor queries at edit/drag time) funnels through one private `LayoutFor` call running the full pipeline — itemize →
shape → wrap/trim → position (kerning, ligatures, complex script, UAX #14 line breaking, CJK/emoji fallback) — so the
measured box, the hit-tested caret, and the selection rects all come from the *same* layout the GPU `GlyphRenderer`
draws from.

This parity is not cosmetic: it is the fix for the "bold everywhere" class of bug. When the renderer shapes but
measurement is per-character, shaped glyphs overlap and read as bold — see the memory note on text shaping. The shared
`TextLayoutEngine` is what keeps the two in lockstep.

**Where to change what:**

| If you're changing… | Edit |
|---|---|
| The `IFontSystem` surface (measure + the caret/hit-test/range-rect queries) | `src/FluentGpu.Windows/DirectWrite/DirectWriteFontSystem.cs` |
| Itemization (BiDi/script segmentation, the callee CCWs) | `…/DWriteItemizer.cs` (`--itemtest` self-test) |
| Shaping (glyph runs, advances, clusters) | `…/DWriteTextShaper.cs` (`--shapetest` self-test) |
| The full layout engine (wrap, trim, line stacking, range rects) | `…/TextLayoutEngine.cs` (`--layouttest` self-test) |
| The GPU rasterization of that layout (atlas + run cache) | `src/FluentGpu.Windows/D3D12/GlyphRenderer.cs` |

The two threads never share a layout engine instance: the UI thread owns this `DirectWriteFontSystem`'s engine for
measurement/queries, and the render thread's `GlyphRenderer` owns a separate one — so an edit-time query can never
corrupt render-side buffers. Query results live in the engine's reused grow-only tables, so a keystroke or pointer-move
is 0 steady-state allocation. The `GlyphRenderer` consumes the recorded `DrawGlyphRunCmd` and runs `LayoutRun` against
that same engine to produce instances; the `text.run` diagnostics (`runsShaped` should be ~0 in steady state) tell you
whether the run cache is holding.

The three `--*test` self-tests (wired in the gallery's `Program.cs`) are fast, headless smoke checks for the text path
specifically — run them when you touch itemization/shaping/layout.

## Win32 windowing and theme

`Win32App` + `Win32Window` (`src/FluentGpu.Windows/Pal/Win32Platform.cs`) are the real Win32 PAL: `RegisterClassExW`
once, `CreateWindowExW`, a single static `[UnmanagedCallersOnly]` `WndProc` thunk, and a `PeekMessage` pump that drains
`WM_*` into the POD `InputEventRing` ([`pal-rhi.md`](../../../design/subsystems/pal-rhi.md) §1.2). Per-window dispatch is
a `GCHandle` stored in `GWLP_USERDATA` (set in `WM_NCCREATE`, freed on dispose) — no managed allocation per message.

**Where to change what:**

| If you're changing… | Edit |
|---|---|
| Window class, creation, the `WndProc`, the pump, DPI handling | `src/FluentGpu.Windows/Pal/Win32Platform.cs` (`Win32App`, `Win32Window`) |
| The accent pickup + DWM dark caption + Mica/Acrylic backdrop | `src/FluentGpu.Windows/Pal/Win32Theme.cs` |
| Clipboard, popup windows, text input/IME | `…/Win32Clipboard.cs`, `Win32PopupWindow.cs`, `Win32TextInput.cs` |

### DPI

The process is `PER_MONITOR_AWARE_V2` (set programmatically in the `Win32App` ctor — manifest-free, AOT-friendly), so the
OS never bitmap-stretches. `Scale` is the window's effective DPI / 96. `WM_DPICHANGED` updates `_scale` *first*, then
adopts the OS-suggested rect (which raises `WM_SIZE` → the host re-lays-out in DIPs and re-rasterizes glyphs at the new
scale), keeping the window's apparent DIP size across monitors. Pointer coordinates are converted physical→DIP at the
pump boundary so they match the DIP scene bounds.

### The custom frame (`WindowDesc.CustomFrame`)

`CustomFrame` is the WinUI `ExtendsContentIntoTitleBar` analogue — opt-in (the gallery sets `AppOptions.CustomFrame = true`; the
basic demos keep the standard OS frame). When set, the leaf strips the OS caption via `WM_NCCALCSIZE` (restoring the top
inset to reclaim the caption strip as client, while keeping the thin L/R/B resize frame — the Windows Terminal recipe so
the DWM shadow, Win11 rounded corners, and resize borders stay system-handled), answers `WM_NCHITTEST` from the
engine-reported `TitleBarRegion`s (`SetTitleBarRegions`), and **synthesizes pointer input** for the engine-drawn caption
buttons: `WM_NCMOUSEMOVE`/`WM_NCLBUTTONDOWN`/`WM_NCLBUTTONUP` become `PointerMove`/`PointerDown`/`PointerUp` at the
button's center, driving the engine button's hover/press ramps exactly like a real pointer. `Minimize`/`ToggleMaximize`/
`CloseWindow` post the matching `WM_SYSCOMMAND`. This is intricate — read the `WM_NC*` cases in `Handle32` before
touching it; the comments document the Fitts-corner and double-click edge cases.

### Mica via `DwmExtendFrameIntoClientArea` + accent pickup

`Win32Theme` (`src/FluentGpu.Windows/Pal/Win32Theme.cs`) is the **sole owner** of the DWM backdrop calls — all flat C
exports via `[LibraryImport]`, all on the UI thread, none touching a `ComPtr` or the render thread (consistent with
[`window-backdrop-mica.md`](../../../design/subsystems/window-backdrop-mica.md): DWM rasterizes, we just present
transparent pixels). `ApplyWindowMaterial(hwnd, dark, mica, customFrame)` does three things:

- **Dark caption** — `DwmSetWindowAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE)`.
- **Backdrop type** — `DwmSetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE)` with `DWMSBT_MAINWINDOW` (Mica) or
  `DWMSBT_TRANSIENTWINDOW` (Acrylic).
- **Frame extension** — and this is the subtle one:

```csharp
if (customFrame)
{
    // All-zero margins (Terminal's Mica case): DWM owns no caption visuals;
    // snap flyout anchors purely from the engine's WM_NCHITTEST regions.
    MARGINS m = new();
    DwmExtendFrameIntoClientArea(hwnd, in m);
}
else if (mica)
{
    // Sheet-of-glass: extend the DWM frame across the ENTIRE client area so Mica
    // composites behind the transparent (DirectComposition) client pixels.
    MARGINS m = new() { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
    DwmExtendFrameIntoClientArea(hwnd, in m);
}
```

The margin is a known footgun documented right in the method (and in the memory note on Mica): with a custom frame the
extension must be **all-zero**, because any non-zero margin lets DWM paint its own caption visuals over the client — a
`-1` sheet-of-glass composites DWM's *own* min/max/close buttons (the "double caption buttons" bug), and even a 1px
sliver re-anchors the Win11 snap flyout off the extended frame. With the **standard** frame, `-1` full-window glass is
correct (the DWM caption buttons land in the real OS titlebar, not the client). Get this wrong and you will see either
doubled caption buttons or an opaque white pane instead of Mica.

Accent pickup lives here too: `AccentLight2()` reads the `AccentPalette` registry blob (8×RGBA, index 1 = Light2 — the
dark-theme accent fill WinUI uses), and `Accent()` reads `AccentColorMenu` with a `DwmGetColorizationColor` fallback.
`FluentApp.Run` calls these to seed `Theme.Accent` before the first frame.

## The tiered COM policy

The full policy is owned by [`com-interop.md`](../../../design/subsystems/com-interop.md) and
[`dotnet10-csharp14-zero-alloc.md`](../../../design/dotnet10-csharp14-zero-alloc.md) §4 — do not restate it; the rule
in one line is **hand-vtable `calli` on the per-frame hot path, `[GeneratedComInterface]`/`[GeneratedComClass]`
everywhere cold, `[LibraryImport]` for flat C exports, and no `ComWrappers` on the hot path.** What you see in the
Windows leaves today:

- **Hot-path consume** (D3D12 command list/queue/fence, swapchain `Present`, DComp `Commit`/visuals, the DirectWrite
  glyph path) is hand-vtable `calli` via **TerraFX.Interop.Windows** (`src/FluentGpu.Windows/D3D12/*.cs`,
  `…/DirectWrite/*.cs`). The design target ([`com-interop.md`](../../../design/subsystems/com-interop.md) §2–3) is
  to *generate* these bindings from a harvested, runtime-self-checked `*.comabi.json` so no human ever types a vtable
  slot; as-built, TerraFX is the vendored binding surface and the generator pipeline is forward work. Either way the
  call site is the same `calli` through `T** lpVtbl`.
- **Flat C exports** (`D3D12CreateDevice`, `CreateDXGIFactory2`, `DCompositionCreateDevice`, and the DWM/registry/Win32
  user32 calls) are `[LibraryImport]` — blittable, no-marshal, AOT-clean. See the `[LibraryImport]` partials in
  `Win32Theme.cs` and `Win32Platform.cs`.
- **WIC** (the image codec, [below](#the-real-image-pipeline)) creates and uses all its COM objects **entirely on the
  calling worker thread** (a `[ThreadStatic]` per-worker factory) and never crosses the scheduler seam — only POD pixels
  do. That is the per-worker confinement domain from
  [`com-interop.md`](../../../design/subsystems/com-interop.md) §1.2.

The confinement contract that matters when you edit these leaves: **the render thread owns every `ComPtr`** (single-thread
v1 satisfies this trivially — one thread). Do not introduce a path where a COM pointer is touched from two threads; the
WIC codec is the model for "own it on one thread, hand off POD."

## The real image pipeline

`Ui.Image` is backed by an `ImageCache` over an `IImageDecoder`. The live (non-headless) chain that `FluentApp.Run`
builds is three pieces, each a separate leaf so the codec stays swappable:

- **`DefaultImageFetcher`** (`src/FluentGpu.Engine/Media/DefaultImageFetcher.cs`) — `IImageFetcher`. One pooled
  `SocketsHttpHandler` (never `new HttpClient()` per request), HTTP/2 with bounded `MaxConnectionsPerServer`,
  `PooledConnectionLifetime` for DNS/CDN-edge rotation, automatic decompression, per-request deadline via the token, and
  a disk-first `DiskImageCache`. Bodies stream into an `ArrayPool` buffer — no per-fetch `byte[]`.
- **`DecodeScheduler`** (`src/FluentGpu.Engine/Media/DecodeScheduler.cs`) — `IImageDecoder`. A worker pool draining three
  priority lanes (Visible > Overscan > Prefetch). `Begin` is a non-blocking UI-thread enqueue; workers fetch+decode
  concurrently; `Pump` drains finished results on the UI thread and uploads pixels (returning the pooled decode buffer).
  `Prioritize` promotes a prefetch that scrolled into view; `Cancel` drops a recycled row's decode. Under backpressure
  the lowest off-screen lane is dropped — never Visible.
- **`WicImageCodec`** (`src/FluentGpu.Windows/Wic/WicImageCodec.cs`) — `IImageCodec`. Windows Imaging Component
  **constrained** decode: it scales straight to the target bucket via `IWICBitmapScaler` (a 3000px cover never
  materializes full-res in CPU memory) and converts to `32bppPBGRA` (premultiplied BGRA — the engine's blend posture and
  the GPU texture format). All WIC COM is worker-thread-confined as described above.

On the GPU side those decoded pixels land in `ImageTextureStore` (atlas pages for thumbnails ≤128px, a per-bucket pool
for 256/512 art) and draw through `ImagePipeline` — see `src/FluentGpu.Windows/D3D12/ImageTextureStore.cs` /
`ImagePipeline.cs`. `D3D12Device.UploadImage`/`EvictImage` are the seam between the decode completion and the resident
texture. The decode-pipeline architecture is owned by `media-pipeline.md`; this is its Windows realization.

## Screenshot capture (`CaptureBgra` → `PngWriter`) for fidelity diffs

The harness verifies engine *logic* headlessly; on-screen D3D12 pixels are a separate, manual **needs-pixels** pass (see
the [Engine Contributors index](./index.md#the-golden-rule-verify-with-the-headless-harness-before-claiming-done)). The
screenshot path is the tooling for that visual loop:

- `FluentAppHarness.Run(root, null, new HarnessOptions { Screenshot = "<path>" })` runs a fixed number of settle frames (the gallery's `Program.cs` uses 6),
  then, if the device is a `D3D12Device`, reads the last back buffer back to CPU and writes a PNG:

  ```csharp
  var px = d3d.CaptureBgra(out int cw, out int ch);
  PngWriter.WriteBgra(screenshot, px, cw, ch);
  ```

- **`D3D12Device.CaptureBgra(out w, out h)`** (`src/FluentGpu.Windows/D3D12/D3D12Device.cs`) — `WaitForGpu`, then a
  `COPY_SOURCE` → READBACK-heap `CopyTextureRegion`, mapped and re-packed on the CPU to tight top-down BGRA8 (the readback
  `RowPitch` is 256-aligned, so the per-row copy strips the padding). It is explicitly **not** a hot-path method — it
  stalls the GPU. With `FLIP_DISCARD` the just-presented buffer at `_frameIndex` is still intact until reused.
- **`PngWriter.WriteBgra(path, bgra, w, h)`** (`src/FluentGpu.Engine/Foundation/PngWriter.cs`) — a minimal pure-managed PNG
  encoder (no WIC / `System.Drawing`, AOT-safe, zero new deps) emitting an opaque RGB PNG from that buffer.

The gallery exposes this end-to-end: `WindowsApp --screenshot out.png --shot <id> [--mica] [--w N --h H]` renders a
deterministic `ShotScene` (opaque by default; `--mica` reproduces the composited path) and exits. Width matters — wrap
and clip bugs are width-dependent, so reproduce the reported geometry.

## The NativeAOT/trim build baseline and the native link

The language/GC baseline from [`dotnet10-csharp14-zero-alloc.md`](../../../design/dotnet10-csharp14-zero-alloc.md) §1 is
set solution-wide in `src/Directory.Build.props`: `net10.0`, `LangVersion 14`, `Nullable`/`AllowUnsafeBlocks`,
`InvariantGlobalization`, `Deterministic`, and **Workstation + Concurrent GC** (`ServerGarbageCollection=false`,
`ConcurrentGarbageCollection=true`). `GenerateDocumentationFile` is on (with `CS1591` suppressed) so `docfx metadata` has
XML to read.

> **As-built vs the AOT target.** The props comment says it plainly: *"Zero-alloc / perf posture (AOT/trim engage at
> publish; harmless at build)."* The full `PublishAot` / `TrimMode full` / `[assembly: DisableRuntimeMarshalling]`
> assembly-split baseline in [`dotnet10-csharp14-zero-alloc.md`](../../../design/dotnet10-csharp14-zero-alloc.md) §1 is
> the **publish target**; the committed solution does not set `PublishAot` in `Directory.Build.props` today, and the
> Windows leaves use the vendored TerraFX bindings rather than the `DisableRuntimeMarshalling`-proven generated surface.
> Treat the AOT baseline as the contract a `PublishAot` configuration must satisfy, not as something every `dotnet
> build` already enforces.

**The native link.** The Windows leaves bind real system DLLs through **TerraFX.Interop.Windows 10.0.26100.6**
(`PackageReference` in `src/FluentGpu.Windows/FluentGpu.Windows.csproj`; TerraFX is the one `PackageReference` in that project); a plain `dotnet build`/`dotnet run` of the
gallery (`src/FluentGpu.WindowsApp`) needs only the .NET 10 SDK and resolves d3d12/dxgi/dcomp/dwrite/dwmapi at runtime —
no extra toolchain. The native link step that *does* need the MSVC linker is a **`PublishAot`** publish: NativeAOT shells
out to `link.exe`, so a publish must run from a **Visual Studio Developer Command Prompt / Developer PowerShell** (or
otherwise have `vcvars` on `PATH`) for the C++ Build Tools' linker and Windows SDK libs to be found. A normal debug
iteration does not.

## Verifying

These are backend leaves, so the headless golden harness does **not** exercise them directly — it runs on
`FluentGpu.Engine/Headless/Rhi/` / `FluentGpu.Engine/Headless/Pal/` / `FluentGpu.Engine/Headless/Text/`. Two distinct bars, kept separate:

- **Logic / DrawList correctness** — still the headless harness. After *any* engine change, including one that touches a
  Windows leaf's shared contract, run it and confirm `ALL CHECKS PASSED`:

  ```bash
  dotnet build src/FluentGpu.VerticalSlice                 # clean build first
  dotnet run   --project src/FluentGpu.VerticalSlice       # expect: ALL CHECKS PASSED
  ```

- **On-screen pixels** — the needs-pixels pass, which is what these leaves actually produce. Build and run the gallery on
  the real Windows path, or capture a deterministic shot:

  ```bash
  dotnet run --project src/FluentGpu.WindowsApp                                   # the live gallery (custom frame + Mica)
  dotnet run --project src/FluentGpu.WindowsApp -- --screenshot shot.png --mica   # one composited frame → PNG, then exit
  ```

  The DirectWrite path also has focused self-tests: `--itemtest`, `--shapetest`, `--layouttest` (each runs and exits).

A green harness means the logic and the recorded DrawList are correct; it does **not** by itself mean a control looks
right on screen. When you change a Windows leaf, you almost always owe a needs-pixels check in addition to the harness.

## Canon links

- [`pal-rhi.md`](../../../design/subsystems/pal-rhi.md) — the authoritative PAL/RHI/windowing/present design: the seam
  vocabulary, the D3D12 backend, the DXGI flip + DComp present-tree, device-lost, resize, and the new PAL seams.
- [`window-backdrop-mica.md`](../../../design/subsystems/window-backdrop-mica.md) — the host-window backdrop (Mica via
  the DWM system-backdrop attribute, the fallback policy, the passthrough contract).
- [`com-interop.md`](../../../design/subsystems/com-interop.md) — the tiered COM policy: the ABI harvest pipeline, the
  two generators, `ComPtr<T>` confinement + Move-only, and the CI gates.
- [`dotnet10-csharp14-zero-alloc.md`](../../../design/dotnet10-csharp14-zero-alloc.md) — the .NET 10 / C# 14 AOT, trim,
  GC, and interop baseline these leaves are built to.
- [`SPEC-INDEX.md`](../../../design/SPEC-INDEX.md) — the precedence authority (the color contract, the handle layout, the
  threading model, the COM ruling).

Sibling site pages: [The RHI, PAL, and Text seams](./seams-rhi-pal-text.md) (the portable contracts these leaves
implement), [Render Pipeline and SceneRecorder](./render-pipeline-and-scenerecorder.md) (what produces the DrawList these
leaves consume), and the [Contributor Map](./contributor-map.md) (the full where-to-change-what routing table).
