# FluentGpu — macOS / Cross-Platform Debt Ledger

> **Why this exists.** The PAL/RHI/Text seam is designed for a macOS (Metal + CoreText + Cocoa) backend
> "dropping in later," but the Windows-specific decisions that create that debt were scattered across
> 20+ docs. This is the single ledger: every Windows-specific item, its macOS plan (if any), and a
> **Status** — **Designed** (concrete macOS equivalent described), **Deferred** (named future work, no
> design), or **Unaddressed** (Windows-only, no macOS mention). Owning docs stay authoritative; this is
> the index. Canonical contracts: [`SPEC-INDEX.md`](./SPEC-INDEX.md). Harvested: **2026-06-06**.
>
> **Tally: Designed 33 · Deferred 9 · Unaddressed 6.** The Unaddressed bucket is the load-bearing risk —
> the entire COM/`calli`/winmd-harvest stack and the flat-C `[LibraryImport]` surface have **no macOS
> analog** because macOS has no COM; that machinery is pure Windows debt.

## 1. Ledger

| Subsystem | Windows-specific item | macOS equivalent / plan | Status | Source |
|---|---|---|---|---|
| Architecture/stack | Locked stack: D3D12 + DirectWrite + DComp behind PAL/RHI/Text seam | Metal + CoreText + Cocoa backend "drops in later" (seam-level) | Designed | README.md:34 |
| Architecture/stack | Windows backend = the one `FluentGpu.Windows` library (folders D3D12/, Pal/, DirectWrite/, Uia/, Wic/, Interop/) | future `FluentGpu.Windows.Mac` (folders Metal/, Cocoa/, CoreText/, NSAccessibility/, CG/) — a **whole-backend swap**, not a 5-leaf 1:1 mirror; the PAL/RHI/Text seam stayed interface-level (Engine `Seams/`), so swappability is unchanged and `FluentGpu.Engine` never references either backend | Designed | README.md; architecture-spec.md §1.2/§3 |
| Architecture/stack | Hand-vtable COM / CCW via `[UnmanagedCallersOnly]`+`GCHandle` (consume `lpVtbl[n]`) | No COM on macOS (Obj-C runtime); the COM machinery is Windows-only with no analog | **Unaddressed** | README.md:81; architecture-spec.md:64,1118 |
| Architecture/stack | COM bindings harvested from winmd (`AbiHarvest` winmd × ClangSharp → `*.comabi.json`) | No macOS ABI-source plan given | **Unaddressed** | hardened-v1-plan.md:119; pal-rhi.md:290 |
| Architecture/stack | HLSL → DXC → DXIL embedded `byte[]` shaders | `-spirv` → SPIRV-Cross → MSL; same HLSL source authored once | Designed | README.md:148,227; gpu-renderer.md:710 |
| Architecture/stack | `NativeHandle{Kind=Hwnd}` present target | `NativeHandle{Kind=NsView}`; Metal switches on Kind (POD seam) | Designed | architecture-spec.md:412; pal-rhi.md:80 |
| Architecture/stack | BiDi/line-break via DWrite `Analyze*` in v1 | portable `FluentGpu.Text.Unicode` (UAX #9/#14/#24) at CoreText milestone — tables not built | Deferred | README.md:227; text.md:832; architecture-spec.md:672 |
| Animation/Effects | `IEffectRunner` = `Effects.D2D1` (GaussianBlur/Shadow + transpiled noise) | `Effects.Metal` = `MPSImageGaussianBlur` + kernels behind same seam | Designed | backdrop-effects-animation.md:88,305 |
| Animation/Effects | `Supports(Transform3D)` true on D2D1 (3D lyrics fan) | MPS "likely false → flat 2D degrade" (documented, not built) | Deferred | backdrop-effects-animation.md:316,580 |
| Animation/Effects | reduced-motion via `SPI_GETCLIENTAREAANIMATION` | `NSWorkspace.accessibilityDisplayShouldReduceMotion` behind same seam | Designed | backdrop-effects-animation.md:551,680 |
| Animation/Effects | D2D straight-vs-premul alpha trap (`PremulInput` wrap) | Metal MPS path must re-validate alpha (no detail) | **Unaddressed** | backdrop-effects-animation.md:307,748 |
| Backdrop/Mica | Window Mica/Acrylic via `DwmSetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE)` / DComp sibling | `NSVisualEffectView` (`.material`, `blendingMode=.behindWindow`) behind layer-backed `CAMetalLayer` | Designed | window-backdrop-mica.md:46; backdrop-effects-animation.md:122 |
| Backdrop/Mica | `DwmExtendFrameIntoClientArea`, immersive-dark, legacy Mica 1029, `BackdropCaps` build-probe (RDP/Win10) | macOS `Mica→.sidebar`, `Acrylic→.hudWindow`; version/RDP/fallback logic is Windows-only | Deferred | window-backdrop-mica.md:42,102,129 |
| GPU renderer | erf shadow / SDF rect family (HLSL, `fwidth`) | portable shader math, HLSL→SPIR-V→MSL | Designed | gpu-renderer.md:330,804 |
| GPU renderer | D2D `IPrimitiveFallback` (path goldens + per-corner shadows) | "Windows-only crutch — per-primitive Metal-milestone debt list" (no Metal impl) | Deferred | gpu-renderer.md:386,817; hardened-v1-plan.md:127 |
| GPU renderer | multi-visual DComp tree + `Present1` partial-present + premul-0 hole-punch | `CALayer` tree: `CAMetalLayer` + sibling layers; hole-punch identical | Designed | gpu-renderer.md:143,809; pal-rhi.md:392 |
| GPU renderer | color: `BGRA8_UNORM` + `_UNORM_SRGB` RTV + DComp PREMULTIPLIED | `CAMetalLayer.colorspace` (linear); atlas + linear-blend map cleanly | Designed | gpu-renderer.md:516; pal-rhi.md:642 |
| GPU renderer | one shared root signature baked into PSOs | maps to Metal argument buffers | Designed | gpu-renderer.md:736; architecture-spec.md:1152 |
| GPU renderer | optional `Effects.D2D1` leaf reuses C#→HLSL transpiler | no Metal transpiler equivalent named (effects leaf per-OS) | Deferred | gpu-renderer.md:73,816 |
| PAL/RHI | Win32 window `RegisterClassExW`/static `WndProc` via `GCHandle` in `GWLP_USERDATA` | `NSWindow`/`NSView`, `NSEvent`→POD ring (same `IPlatformWindow`) | Designed | pal-rhi.md:128; architecture-spec.md:523 |
| PAL/RHI | `WM_*` pump `MsgWaitForMultipleObjectsEx`; modal size/move loop | `CVDisplayLink` callback; pump→ring schema portable | Designed | pal-rhi.md:140,154 |
| PAL/RHI | `PER_MONITOR_AWARE_V2` DPI (`WM_DPICHANGED`, `GetDpiForWindow`) | `backingScaleFactor`→`Scale` (POD across seam) | Designed | pal-rhi.md:130; architecture-spec.md:524 |
| PAL/RHI | D3D12 device/queue/fence/heaps/D3D12MA + graphics fork; `IDXGIFactory6` + WARP | `MTLDevice/CommandQueue/RenderPipelineState`, `MTLHeap` (1:1 mapping given) | Designed | pal-rhi.md:265; architecture-spec.md:1152 |
| PAL/RHI | DXGI flip swapchain + DComp `Target`/`Visual`/`Commit` | `CAMetalLayer` ≙ DComp visual; `id<CAMetalDrawable>` ≙ `CurrentBackBuffer` | Designed | pal-rhi.md:381,432; architecture-spec.md:533 |
| PAL/RHI | `CopyBufferToTexture` (256B pitch) + UPLOAD-heap staging ring | Metal `MTLBlitCommandEncoder copyFromBuffer:toTexture:` | Designed | pal-rhi.md:328; media-pipeline.md:649 |
| PAL/RHI | device-lost: `DXGI_ERROR_DEVICE_REMOVED` + `RegisterWaitForSingleObject(fence→Max)` | recovery invariant portable, but **Metal loss-detection path unspecified** | **Unaddressed** | pal-rhi.md:450; architecture-spec.md:558 |
| PAL/RHI | `IVirtualMemory` → `VirtualAlloc`/`VirtualFree` backing ChunkedArena | `mmap(PROT_NONE)`+`MAP_FIXED`/`madvise(MADV_FREE)` behind same seam | Designed | pal-rhi.md:549,640 |
| PAL/RHI | `IImageCodec` → WIC | ImageIO/`CGImageSource` behind same seam | Designed | pal-rhi.md:567; media-pipeline.md:222 |
| PAL/RHI | `[LibraryImport]` flat-C (`D3D12CreateDevice`, `DWriteCreateFactory`, `RegisterClassExW`…) | Windows-only P/Invoke surface; no macOS enumeration | **Unaddressed** | pal-rhi.md:146 |
| Input/A11y | UIA over retained tree (`IRawElementProvider*`, `UiaClientsAreListening` gate) | `NSAccessibility`-conforming objects reading same `A11yInfo`/topology; role map | Designed | input-a11y.md:451; architecture-spec.md:1155 |
| Input/A11y | `GetRuntimeId`; `UiaRaiseNotificationEvent` for `UseAnnounce` | NSAccessibility reuses `A11yInfo`+RuntimeId; Notification-event analog unspecified | Deferred | input-a11y.md:494,500 |
| Input/A11y | IME v1 = Imm32 (`WM_IME_*`) behind `IImeSession` | `NSTextInputClient` behind same seam | Designed | input-a11y.md:429; architecture-spec.md:965 |
| Input/A11y | IME TSF (`ITextStoreACP2`) = v2 | macOS uses `NSTextInputClient` (no TSF analog); TSF deferred | Deferred | input-a11y.md:429,447 |
| Input/A11y | OLE clipboard/drag-drop (`IDataObject`/`DoDragDrop`) | `NSPasteboard` / `NSDraggingSource`/`Destination` behind same seams | Designed | input-a11y.md:510; architecture-spec.md:1157 |
| Input/A11y | Win32 input map (`WM_POINTER*`, `WM_CHAR`, `WM_INPUT` coalescing) | `NSEvent`→`InputEvent` POD; ring + coalescing portable | Designed | input-a11y.md:65 |
| Input/A11y | focus-rect HC color via `ISystemColors` (Windows HC tokens) | macOS appearance colors via same seam | Designed | input-a11y.md:375 |
| Text | DirectWrite/ backend (`FluentGpu.Windows`; ~25 vtbl structs, callee CCW, `CreateAlphaTexture`, `MapCharacters`, color emoji) | `FluentGpu.Windows.Mac` CoreText/: `CTFontCreateForString`, `CTFontGetGlyphsForCharacters` (+HarfBuzz), `CTFontDrawGlyphs`→A8 — ~70% portable | Designed | text.md:351,810; architecture-spec.md:693 |
| Text | itemize (BiDi/script/line-break) via DWrite props in v1 | portable `FluentGpu.Text.Unicode` at CoreText milestone — not built | Deferred | text.md:164,832 |
| Text | ClearType reserved v2; v1 grayscale + DWrite-gamma | CoreText "grayscale-only anyway" — unifies; ClearType moot | Designed | text.md:469,821 |
| Text | DWrite color glyphs (COLR/CPAL/SVG/CBDT) | `kCTFontColorGlyphsAttribute`/`CTFontDrawGlyphs` | Designed | text.md:465,820 |
| Text | editable text / caret / IME-edit = v1 follow-up (display-only) | cross-platform editing inherits the deferral; no macOS-specific editing design | Deferred | text.md:80,707 |
| Theming | `ISystemColors`: `DwmGetColorizationColor`, `AppsUseLightTheme`, `SPI_GETHIGHCONTRAST`, `WM_SETTINGCHANGE` | `NSColor.controlAccentColor`, `effectiveAppearance`, `shouldIncreaseContrast`, KVO → Epoch++ | Designed | theming.md:241,701; pal-rhi.md:530 |
| Media | video present `IVideoPresenter` → DComp child visual; app binds `MediaPlayer` | `AVPlayerLayer`/`CALayer` sibling under `CAMetalLayer`; hole-punch identical | Designed | media-pipeline.md:460; pal-rhi.md:477 |
| Media | WIC codec backend (`FluentGpu.Windows` Wic/) | `FluentGpu.Windows.Mac` CG/ (`CGImageSource`) behind `IImageCodec` | Designed | media-pipeline.md:222 |
| Media | D3D12 placed-resource staging ring | Metal `MTLBlitCommandEncoder` | Designed | media-pipeline.md:301 |
| Media | `IPlaybackClock` playback-ms source | app-provided seam; portable (no Windows binding) | Designed | media-pipeline.md:560 |

## 2. The 6 Unaddressed items (the real cross-platform risk)

1. **COM / `ComWrappers` / hand-vtable machinery** — no COM on macOS; the entire consume `calli` + CCW stack is Windows-only debt with no analog.
2. **winmd-harvested ABI source** (`AbiHarvest`) — no macOS ABI-source plan.
3. **flat-C `[LibraryImport]` surface** — Windows P/Invoke only; macOS would use the Obj-C runtime.
4. **D2D premultiplied-alpha trap re-validation** for the Metal MPS effects path.
5. **Metal device-lost detection** — the recovery *invariant* is portable, but Metal's loss-detection path is unspecified.
6. *(plus the largest Deferred clusters:)* portable `FluentGpu.Text.Unicode` BiDi/line-break tables, and the per-primitive D2D-fallback Metal-milestone debt list.
