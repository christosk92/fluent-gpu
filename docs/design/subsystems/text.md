# FluentGpu — Subsystem Design: **Text & Glyph (DirectWrite seam + synced lyrics)**

Assemblies: **`FluentGpu.Text`** (portable seam, interface + POD only) · **`FluentGpu.Windows` (DirectWrite/ folder)**
(Windows leaf, all COM) · **`FluentGpu.Media.LyricsLayoutEngine`** (portable lyrics composition, in
`FluentGpu.Media`). Future leaf **`FluentGpu.Text.CoreText`** (macOS) and optional portable
**`FluentGpu.Text.Unicode`** (UAX #9/#14/#24 tables, deferred to the CoreText milestone).

This document is the authority for the text seam interfaces, `GlyphKey`/`PackedGlyph`/`ShapedRunBuilder`/
`GlyphRunRealization`, the two-level shaped/wrap cache, the R8 + BGRA glyph atlas and its epoch/eviction
discipline, `DrawGlyphRunCmd` emission, the DWrite leaf (RCW hot-path + thread-confined callee CCWs), the
`UseSyncedLyrics` hook + per-instance lyric glyph color, and the CoreText boundary. **As of the gap-analysis
fold (L1 / L3-readside / L9-format / L14-inline) it is additionally the authority for** the text **selection
model** (`SelectionState` anchor/extent/affinity — semantics here, column storage in `scene-memory.md`), the
**selection-highlight geometry** that drives `DrawSelectionRectCmd` (built on the existing `GetSelectionRects`),
the **editable transactional buffer seam** (an `ITextStoreACP2`-shaped commit-lock — designed FULLY for both
single-line and multi-line editing, **not** "v2"), the **selection→clipboard binding** via `IClipboard`, the
**text read-side that backs BOTH on-screen selection AND `ITextRangeProvider`** (input-a11y owns the UIA
wrapper; this doc owns the read-side it calls), **edge localization** (format number/date/currency/plural at
the edge into already-formatted `StringId`, preserving no-string-on-paint-path), and **inline-flow rich text**
(object-replacement characters in the itemizer so inline elements flow with text). It is **decisive** and
**designed to the AUTHORITATIVE cross-subsystem contracts** — it references, never redefines, them:

- **Threading / 13-phase / publish / quarantine** → `hardened-v1-plan.md` §2 (canonical). The render thread
  owns every ComPtr; the UI thread touches zero COM; `SceneFrame` is an immutable POD snapshot.
- **COM ruling** → `dotnet10-csharp14-zero-alloc.md` §4 + `hardened-v1-plan.md` §4.2. No human-typed vtable
  slots; hot-path bindings generated from a harvested, runtime-self-checked `*.comabi.json`; ComPtr
  render-thread-confined + Move-only; `[GeneratedComInterface]`/`[GeneratedComClass]` for all cold/warm COM;
  hand-vtable `calli` only on generated hot-path consume + in-loop CCWs.
- **Memory / handles** → `foundations.md` (generational `Handle{u32 index,u32 gen}`, `SlabAllocator<T>`,
  `HandleTable<TResource>`, `StringId`, `ChunkedArena` superseding the single-buffer arena, native
  high-water counter gating chunk growth, `ArrayPool<Element>` for virtualization windows).
- **Scene / DrawList / clean-span** → `architecture-spec.md` §4.4/§4.5/§4.6 + `hardened-v1-plan.md` §4.6.
- **RHI / PAL** → `architecture-spec.md` §4.7/§5.1.
- **Color** → swapchain BGRA8_UNORM, RTV BGRA8_UNORM_SRGB, blend+resolve in linear, output premultiplied;
  **text is a deliberate gamma exception** (`architecture-spec.md` §5.2/§5.3).
- **GPU renderer integration** → `subsystems/gpu-renderer.md` §10 (glyph PSO, instance, batch-by-page).

### Verified grounding (read from source, unchanged and still load-bearing)
- ComputeSharp ships **no DirectWrite bindings** (`ComputeSharp.Win32.D2D1` = D2D1/DXGI/D3D only) ⇒ **we
  author 100% of DWrite interop ourselves**, but per the §4.2 ruling the consume direction is now
  **generator-emitted from a harvested `dwrite.comabi.json`**, not hand-typed `[VtblIndex]`.
- The Yoga leaf-sizing seam = `YogaSize Measure(node, float availW, YogaMeasureMode wMode, float availH,
  YogaMeasureMode hMode)`, modes `{Undefined, Exactly, AtMost}`, with an **8-entry `CachedMeasurement` ring**.
  The text `MeasureFunc` satisfies this contract via **one `static readonly YogaMeasureFunc` + a generational
  `TextLayoutHandle` in the node user-data slot** (never a captured closure — `architecture-spec.md` §5.3).

---

## 0. Scope & responsibilities

```
        ┌───────────────────── FluentGpu.Text (seam: interface + POD, portable, ZERO COM) ─────────────────────┐
 USER ─►│ Element(Text "...") ──► TextBlockPayload (holds char[] at the EDGE; stable ReadOnlySpan<char>)        │
        │                                                                                                        │
 LAYOUT►│ MeasureText (Yoga MeasureFunc, UI thread, phase 6) ──► ITextLayoutEngine                              │
 P6     │   itemize(BiDi+script+linebreak) ► font-fallback segment ► shape ► wrap ► align ► trim ► metrics       │
        │                         │ (zero-alloc, span-filled, ChunkedArena scratch)                              │
        │                         ▼                                                                              │
        │                L1 shaped-run cache (constraint-free)  ◄── ITextShaper (DWrite analyzer, UI thread v1)  │
        │                L2 layout/wrap cache (constraint-bearing)                                               │
        │                         │                                                                              │
 RECORD►│ Emit ──► GlyphRunTable: SlabAllocator<GlyphRunRealization> (content-epoch) ──► DrawGlyphRunCmd (POD)   │
 P8     │   (RENDER thread reads only Committed runs; positions are FINAL device-space dest rects, VISUAL order) │
 BATCH ►│ IGlyphAtlas (R8 + BGRA pages) ◄── IGlyphRasterizer (DWrite GlyphRunAnalysis → A8 / color tile)         │
 P9     │   batch-time UV resolve by GlyphKey → one instanced draw per (page, AaMode, clip)                      │
        └───────────────────────────────────────── leaf: FluentGpu.Windows (DirectWrite/ folder, Windows, COM) ─────┘

 FluentGpu.Media.LyricsLayoutEngine (portable): UseSyncedLyrics → line layout over ITextShaper → cached
   GlyphRunRealization; PER-INSTANCE syllable color written by the phase-7 AnimTrack on the PLAYBACK clock.
```

This subsystem owns, end to end:
1. **Font system** — collection enumeration, family/face resolution, fallback, design-unit metrics.
2. **Itemization** — BiDi level runs (UAX #9), script runs (UAX #24), line-break opportunities (UAX #14),
   font-fallback coverage segmentation; **classification via `SearchValues<char>`** (break/space/control).
3. **Shaping** — per-segment complex-script shaping, ligatures/kerning/OpenType features, glyph IDs +
   advances + offsets + cluster map (one shaped run = single face + single script + single direction).
4. **Layout above shaping** — wrap, align, trim/ellipsis, multi-run line composition, hit-testing,
   measurement that feeds the Yoga `MeasureFunc`. **Auto-fit (`TextEl.MinSize` → `TextStyle.MinSizeDip`, opt-in):**
   when a floor is set with `MaxLines` and a wrapping style, the measure pass binary-searches the largest size in
   `[MinSize, Size]` whose run wraps to ≤ `MaxLines` at the available width (a Viewbox-for-text; WinUI has no analogue),
   shaping each probe through the SAME `Measure`. The chosen size is stored on the node's `TextMeasureCache.FitSize`
   and the recorder shapes the glyph run at it (0 ⇒ the authored size). Off by default (`MinSizeDip == 0`), so all
   other text is byte-identical; runs only on a cache miss of an opt-in node. As-built: `FlexLayout.FitTextSize`,
   `SceneRecorder` Text case, `TextMeasureCache.FitSize`.
5. **Rasterization** — glyph → bitmap (R8 grayscale; BGRA color/emoji), subpixel positioning, gamma policy.
6. **Glyph atlas** — packing, multi-page, frame-start LRU eviction, the realization-cache key, the
   `GlyphRunRealization` content-epoch, and the by-handle `DrawGlyphRunCmd` reference.
7. **Caching** — L1 shaped-run (constraint-free) + L2 wrap (constraint-bearing); zero-alloc measurement.
8. **Synced lyrics** — `LyricsLayoutEngine` line layout + per-instance syllable color on the playback clock.
9. **Selection model** (`SelectionState` anchor/extent/affinity) + the **selection-highlight geometry** read-side
   (over `GetSelectionRects`) that drives `DrawSelectionRectCmd`, and **selection→clipboard** binding (§15).
10. **Editable transactional buffer seam** — an `ITextStoreACP2`-shaped commit-lock document (`ITextDocument` +
    `ITextEditSink`) backing single-line **and** multi-line editing, IME composition, and undo/redo (§16).
11. **The text read-side that backs BOTH on-screen selection AND `ITextRangeProvider`** — one
    `ITextReadSide` consumed by the highlight emitter (this doc) and the UIA range provider (input-a11y) (§17).
12. **Edge localization** — `ILocaleFormatter` formats number/date/currency/plural at the edge into an
    already-formatted `StringId`, preserving the no-string-on-paint-path invariant (§18).
13. **Inline-flow rich text** — object-replacement codepoints (U+FFFC) in the itemizer so inline elements
    (`Image`/`Button`/badge) flow with text; the itemizer emits `InlineObjectRun`s the layout engine reserves
    advance for and the layout doc places (§19).

It does **not** own: the quad batcher / glyph PSO (Render owns — `gpu-renderer.md` §10; we hand it instance
data), the RHI texture-upload mechanics (we call `ICommandEncoder.CopyBufferToTexture` via the seam,
`architecture-spec.md` §4.7), font *file* I/O policy (PAL/OS), the `DrawSelectionRectCmd` payload STRUCT SHAPE +
raster (→ `gpu-renderer.md` §3.1/§10), the `SelectionState` SoA **column storage** + `DrawOp` registration (→
`scene-memory.md` §2.2/§4.1), the `ITextRangeProvider`/`ITextProvider` **UIA wrapper** + `IClipboard` OLE
mechanism + IME `IImeSession` event pump (→ `input-a11y.md` §10/§11.4/§12), the inline-object **placement
geometry** + `FlowDirection` resolution (→ `layout.md` §4.1/§10), or font/locale data tables (PAL/OS).

---

## 1. Assembly placement & dependency rules

```
FluentGpu.Foundation   (Handle, SlabAllocator, HandleTable, StringId, ChunkedArena, ArrayPool helpers,
                        Affine2D, Size2, RectF, ColorF, GlyphRunTable column home — see foundations.md)
        ▲
        │ (interfaces + POD only — GPU/OS-agnostic; NO ComPtr, NO HWND, NO DWrite type)
FluentGpu.Text   ── references ──►  FluentGpu.Rhi (iface: ICommandEncoder/ITexture/BufferHandle for atlas upload)
        ▲                                              FluentGpu.Pal (iface: locale/DPI/system-colors only)
        │
FluentGpu.Render  (owns DrawGlyphRunCmd emission + glyph PSO; calls IGlyphAtlas/ITextLayoutEngine via seam)
FluentGpu.Layout  (calls ITextLayoutEngine.Measure via the static YogaMeasureFunc — §8)
FluentGpu.Media   (LyricsLayoutEngine: composes ITextShaper + GlyphRunRealization; owns UseSyncedLyrics)
        ▲
FluentGpu.Windows/DirectWrite/ (LEAF; referenced ONLY by Hosting; ALL ComPtr<IDWrite*>, RENDER-THREAD CONFINED)
        └─ references: FluentGpu.Text (implements seam), FluentGpu.Rhi (iface), Foundation
FluentGpu.SourceGen + FluentGpu.Interop.SourceGen  (build-time: emit DWrite RCW hot-path bindings from
                        dwrite.comabi.json + the thread-confined callee CCW vtables; FGCOM analyzer rules)
```

**Invariants honored.** `FluentGpu.Text` contains **zero** Windows/COM types. Every `ComPtr<IDWrite*>`,
`IDWriteTextAnalyzer`, `IDWriteGlyphRunAnalysis` lives only in `FluentGpu.Windows` (DirectWrite/ folder), and per
`hardened-v1-plan.md` §2 **that leaf executes on the render thread** (the sole ComPtr owner). The
`FluentGpu.Text → FluentGpu.Rhi` (interface) edge is explicit so the atlas can own `TextureHandle` upload
through `ICommandEncoder.CopyBufferToTexture` (no cycle; `Rhi` is a pure-interface root). All assemblies are
acyclic; the DirectWrite leaf is referenced only by `Hosting`.

**Threading placement of the seam impls (the keystone confinement):**
- `ITextLayoutEngine` (itemize/shape/wrap/measure/hit-test) and the **L1/L2 caches** run on the **UI thread**
  (phase 6 layout, phase 8 record-emit). In v1 the *shaper* (`ITextShaper`) is invoked here too, so the
  callee-side DWrite CCW (`IDWriteTextAnalysisSource/Sink`) is **confined to the UI thread** — DWrite is
  called synchronously, single-threaded, during measure. The DWrite **factory is created SHARED** (a
  process-lifetime root) but every *call* through it is thread-confined and serialized (§4.2 ruling).
- `IGlyphRasterizer` + `IGlyphAtlas` GPU page upload run on the **render thread** (phase 9 batch / atlas
  `EndFrame`), because they touch `ComPtr<IDWriteGlyphRunAnalysis>` and the RHI `ICommandEncoder`.
  **CPU rasterization** (DWrite `CreateAlphaTexture` into a staging block) is render-thread work in v1; the
  build order (`hardened-v1-plan.md` §6 step 6) moves **glyph raster off-thread to workers only after the
  seam + quarantine ≥2 are proven** — and that re-architects atlas to `probe → raster → pack → upload`
  (§5.6). v1 ships the synchronous render-thread raster.

> **Single-thread-first (build order, `hardened-v1-plan.md` §6).** In step 1 the UI thread both produces and
> consumes (quarantine=0): measure/shape/wrap, record-emit, raster, and atlas upload all run on the one
> thread. The confinement boundaries above are the *target* topology the seam flips into behind the green
> race gate; nothing in this doc requires parallelism to be correct.

---

## 2. The text seam — interfaces (portable, in `FluentGpu.Text`)

All interfaces are **span-filling** (caller owns buffers), **handle-returning**, **no `string` on the paint
path**, **no `params object[]`**, **no method allocates on a hot path**. Spans point into a `ChunkedArena`
block owned by the caller (`foundations.md`: reserve-then-commit, O(1) add-chunk, no LOH cliff).

```csharp
namespace FluentGpu.Text;

// ───────────────────────────── Font system ─────────────────────────────
public interface IFontSystem
{
    FontFaceHandle ResolveFace(in FontRequest request);              // → FontFaceTable (HandleKind.FontFace)
    int  GetFamilyNames(Span<StringId> dst);                         // zero-alloc enumerate; returns count
    ref readonly FontMetrics GetFaceMetrics(FontFaceHandle face);    // design-unit metrics, cached
    FontFaceHandle FallbackFor(FontFaceHandle baseFace, uint codepoint, StringId locale); // Invalid ⇒ .notdef
    void AddRef(FontFaceHandle face);                                // pin across frames (DWrite face lifetime)
    void Release(FontFaceHandle face);
}

// Immutable selection key, 24 B, blittable, custom GetHashCode.
public readonly struct FontRequest : IEquatable<FontRequest>
{
    public readonly StringId Family;     // 4   interned "Segoe UI Variable"
    public readonly float    SizeDip;    // 4   point/dip size (face is size-agnostic; size enters at shape/raster)
    public readonly ushort   Weight;     // 2   100..950
    public readonly byte     Style;      // 1   Normal/Oblique/Italic
    public readonly byte     Stretch;    // 1   1..9
    public readonly StringId Locale;     // 4   BCP-47 interned ("en-us")
    public readonly ushort   FeatureSet; // 2   index into FeatureTable (FrozenDictionary, build-once)
    public readonly ushort   _pad;       // 2
}

// ───────────────────────────── Itemization (NEW, explicit) ─────────────────────────────
public interface ITextItemizer
{
    // Fill caller-owned arena spans with minimal runs = intersection of BiDi ∧ script ∧ style ∧ fallback.
    // v1 Windows: backed by DWrite Analyze* (callee CCW). CoreText milestone: portable FluentGpu.Text.Unicode.
    int Itemize(in ItemizeInput input, Span<ItemRun> dstRuns);       // returns run count; -needed if too small
    int FindBreakOpportunities(ReadOnlySpan<char> text, StringId locale, Span<BreakOpp> dst); // UAX #14
    // INLINE-FLOW (L14): caller pre-marks U+FFFC OBJECT REPLACEMENT positions; the itemizer treats each as a
    // single zero-script, neutral-BiDi cluster boundary and emits an InlineObjectRun whose advance is RESERVED
    // (filled by layout from the measured inline element size). Object runs never merge with glyph runs.
    int CollectInlineObjects(in ItemizeInput input, Span<InlineObjectRun> dst);  // one per U+FFFC; -needed if small
}

// One inline object-replacement slot (the itemizer's view; layout.md §10 owns placement geometry).
public readonly struct InlineObjectRun
{
    public readonly int    TextPos;        // UTF-16 index of the U+FFFC code unit
    public readonly int    ObjectId;       // index into the caller's inline-element table (NodeHandle.Index)
    public readonly float  AdvanceDip;     // measured inline width (RESERVED until layout fills it)
    public readonly float  AscentDip;      // above-baseline extent (for line-height contribution)
    public readonly float  DescentDip;     // below-baseline extent
    public readonly byte   BaselineAlign;  // InlineBaseline { TextBaseline, Top, Center, Bottom }
}

// ───────────────────────────── Editable transactional buffer seam (L1 — single AND multi-line) ─────────────────────────────
// ITextStoreACP2-SHAPED commit-lock document. Owned here (portable, COM-free); input-a11y.md §10 hosts the
// IImeSession event pump + the cold-COM ITextStoreACP2 TSF CCW that forwards into this seam. NOT "v2".
public interface ITextDocument
{
    int  Length { get; }
    bool IsMultiLine { get; }                                          // single-line collapses \n; multi-line keeps
    ReadOnlySpan<char> Snapshot();                                     // stable, pinnable view for the frame
    SelectionState Selection { get; }                                  // current anchor/extent/affinity (§14)
    // ── lock arbitration (the ITextStoreACP2 write-lock contract — no mutation outside a held write lock) ──
    bool TryLock(LockKind kind, out EditLock token);                   // ReadOnly | ReadWrite; fails if conflicting
    void Unlock(in EditLock token);                                    // flushes pending sinks, bumps ContentVersion
    // ── transactional mutation (only valid under a ReadWrite lock; coalesced into ONE undo unit per lock) ──
    EditResult Replace(in EditLock token, int start, int len, ReadOnlySpan<char> insert);
    void SetSelection(in EditLock token, in SelectionState sel);
    // ── IME composition span (Imm32 GCS_COMPSTR underline / TSF composition; committed via Replace) ──
    void SetComposition(in EditLock token, int start, int len, ReadOnlySpan<char> text, ReadOnlySpan<CompUnderline> u);
    void EndComposition(in EditLock token, bool commit);
    // ── undo/redo (transaction stack; one entry per Unlock that mutated) ──
    bool Undo(); bool Redo();
    // ── change notification (drives layout re-shape, UIA TextChanged, ITextEditSink, caret rect) ──
    void AdviseSink(ITextEditSink sink);  void UnadviseSink(ITextEditSink sink);
}
public interface ITextEditSink
{
    void OnTextChanged(int start, int oldLen, int newLen);             // → re-shape the affected lines only (§16)
    void OnSelectionChanged(in SelectionState sel);                    // → re-emit highlight + caret rect (§15)
    void OnCompositionChanged(int start, int len);                     // → re-emit composition underline overlay
}

// ───────────────────────────── Shaping ─────────────────────────────
public interface ITextShaper
{
    // Shape ONE itemized run (single script + single BiDi level + single face + single locale).
    // Fills caller-owned spans in ShapedRunBuilder. false ⇒ dst too small (grow from arena, retry — §6).
    bool ShapeRun(in ShapeRunInput input, ref ShapedRunBuilder dst);
}

// ───────────────────────────── Rasterization ─────────────────────────────
public interface IGlyphRasterizer
{
    bool RasterizeGlyph(in GlyphKey key, Span<byte> dst, out GlyphBitmapInfo info); // A8 or BGRA color tile
    GlyphBitmapInfo MeasureGlyphBitmap(in GlyphKey key);             // dims WITHOUT rasterizing (atlas alloc)
    // Oversized-glyph safety valve: outline → tessellated path (uses the geometry-sink CCW, §4.7 fallback).
    bool TryGetOutline(in GlyphKey key, ref PathSinkBuilder sink);
}

// ───────────────────────────── Atlas ─────────────────────────────
public interface IGlyphAtlas
{
    ref readonly PackedGlyph GetOrAdd(in GlyphKey key);              // resident hit ⇒ dict probe; miss ⇒ raster+pack (or overflow region)
    bool TryGet(in GlyphKey key, out PackedGlyph glyph);             // probe-only (no raster), for prepass / liveness scan
    void BeginFrame(ulong frameIndex);                              // advance LRU clock; eviction sweep here (frame START)
    void EndFrame(ICommandEncoder enc);                             // batched dirty-region CopyBufferToTexture to GPU
    void Pin(in GlyphKey key);                                      // live this frame ⇒ ineligible for eviction
    TextureHandle PageTexture(int page);                            // RHI page texture (batcher binding)
    GlyphAtlasFormat PageFormat(int page);                          // R8 (grayscale) | BGRA8 (color)
    uint Epoch { get; }                                             // bumps on any repack/compaction (§5.3)
    int PageCount { get; }
}

// ───────────────────────────── Layout (above shaping) ─────────────────────────────
public interface ITextLayoutEngine
{
    TextMeasure Measure(in TextLayoutRequest req, in MeasureConstraints c);   // pure, cached, zero-alloc
    TextLayoutHandle Layout(in TextLayoutRequest req, in LayoutConstraints c);// retained slot for paint+hit-test
    // RECORD phase: emit FINAL device-space glyph runs (VISUAL order) into the DrawList.
    void Emit(TextLayoutHandle layout, in Affine2D transform, BrushHandle fill, ref DrawListWriter w);
    HitTestResult  HitTestPoint(TextLayoutHandle layout, float x, float y);
    HitTestMetrics HitTestTextPosition(TextLayoutHandle layout, int textPos, bool trailing);
    int GetSelectionRects(TextLayoutHandle layout, int start, int len, Span<RectF> dst); // BiDi ⇒ multiple
    // RECORD phase: emit the selection HIGHLIGHT (one DrawSelectionRectCmd per visual fragment) UNDER the glyphs,
    // and the caret as a thin fill; both at FINAL device space. (§15; gpu-renderer.md owns the cmd shape.)
    void EmitSelection(TextLayoutHandle layout, in SelectionState sel, in Affine2D transform,
                       BrushHandle highlight, BrushHandle caret, ref DrawListWriter w);
    // Read-side accessor that backs BOTH on-screen selection and ITextRangeProvider (§17; one source of truth).
    ITextReadSide ReadSide(TextLayoutHandle layout);
    void Release(TextLayoutHandle layout);
}

// ───────────────────────────── Read-side (backs on-screen selection AND ITextRangeProvider) ─────────────────────────────
// Owned here; input-a11y.md §11.4 wraps it in the UIA ITextProvider/ITextRangeProvider CCW. ZERO COM, span-filling.
public interface ITextReadSide
{
    int  Length { get; }                                                 // total UTF-16 code units in the layout
    // navigation by UAX #29 grapheme/word/line/paragraph boundaries (Narrator read-by-unit; selection extend)
    int  MoveBoundary(int pos, TextUnit unit, int count, out int actualMoved);   // returns new pos
    int  ExpandToEnclosing(int pos, TextUnit unit, out int rangeStart);          // returns rangeEnd
    // geometry for a logical range (BiDi ⇒ multiple visual rects). Shared by highlight emit + RangeFromRect.
    int  GetRangeRects(int start, int len, Span<RectF> dst);             // returns frag count; -needed if too small
    int  RangeFromPoint(float x, float y, out bool trailing);            // device pt → text pos (UIA RangeFromPoint)
    // attributes at a position (UIA GetAttributeValue): font/size/weight/fg/bg/underline/bidi-level/lang.
    bool GetTextAttribute(int pos, TextAttributeId attr, out TextAttributeValue val);
    // copy the plain text of a range into a caller-owned span (UIA GetText / clipboard / Narrator). No string alloc.
    int  CopyText(int start, int len, Span<char> dst);                   // returns chars copied; -needed if too small
    ulong ContentVersion { get; }                                        // bumps on any edit (UIA range staleness)
}

// ───────────────────────────── Edge localization (format → StringId; no string on paint path) ─────────────────────────────
public interface ILocaleFormatter
{
    // Format at the EDGE (reconcile/render phase 4, UI thread) into the StringTable; returns an interned id.
    // The PAINT path then sees ONLY the already-formatted StringId — the no-string-on-paint-path invariant holds.
    StringId FormatNumber(double value, NumberStyle style, StringId locale);
    StringId FormatCurrency(decimal value, StringId currencyCode, StringId locale);
    StringId FormatDate(long unixMs, DateStyle style, StringId locale);
    StringId FormatPlural(StringId messageTemplate, double count, StringId locale,
                          ReadOnlySpan<(StringId name, StringId value)> args);   // ICU MessageFormat select/plural
    PluralCategory ResolvePlural(double count, StringId locale);                 // zero/one/two/few/many/other
}
```

**Why these shapes**
- `ResolveFace` returns a *handle*, not a COM ptr — face lifetime lives in a `HandleTable<FaceSlot>` inside
  the DirectWrite leaf (the one place COM/GPU ownership meets handles, `foundations.md`).
- A `FontFaceHandle` is **size-agnostic** (DWrite `IDWriteFontFace` has no size); size enters at shape/raster,
  so one face backs many sizes and one realization survives `AddRef` churn (`GlyphKey` keys by stable
  `FontFaceId`, not the gen-versioned handle).
- **Itemization is its own seam** above the shaper. v1 Windows backs it with DWrite `Analyze*` (one callee
  CCW); the CoreText milestone swaps in portable `FluentGpu.Text.Unicode`. The shaper only ever sees an
  already-itemized run — single face, single script, single direction (`architecture-spec.md` §5.3).
- `Emit` writes **FINAL device-space dest rects in VISUAL order** — BiDi reorder, cluster mapping, mark
  positioning, and subpixel phase are all resolved here; the renderer treats glyphs as opaque positioned
  quads (`architecture-spec.md` §4.6 contract clause).

---

## 3. Core POD data structures & memory layouts

### 3.1 `GlyphKey` — realization-cache key (24 B; `architecture-spec.md` §4.6 pins 24 B)

```
GlyphKey  (24 B, [StructLayout(Sequential, Pack=2)], IEquatable, xxHash GetHashCode over raw bytes)
┌────────────┬──────┬───────────────────────────────────────────────────────────┐
│ FontFaceId │  4 B │ stable per-face id (FontFaceTable index; NOT the gen-handle)│
│ GlyphId    │  2 B │ ushort DWRITE glyph index (POST-shaping)                    │
│ SizePx     │  2 B │ ushort fixed 10.6 px (size after DPI scale, QUANTIZED)      │
│ SubpxX     │  1 B │ horizontal subpixel bucket (0..N-1, N=4 → 1/4 px phases)    │
│ SubpxY     │  1 B │ vertical subpixel bucket (1 = none for horizontal Latin)    │
│ AaMode     │  1 B │ 0=Aliased 1=GrayscaleA8 2=ClearTypeRGB(v2) 3=ColorBGRA(COLR)│
│ Synthesis  │  1 B │ faux-bold / faux-italic slant / embolden strength bits      │
│ RenderMode │  1 B │ DWRITE_RENDERING_MODE (natural-symmetric/natural/aliased)   │
│ GridFit    │  1 B │ DWRITE_GRID_FIT_MODE                                        │
│ Gamma      │  1 B │ per-target text gamma/contrast bucket (the gamma exception) │
│ _pad       │  1 B │                                                             │
│ Variation  │  4 B │ packed variable-font axis hash (wght/wdth/opsz), or 0       │
│ _pad2      │  2 B │ → total 24 B                                                │
└────────────┴──────┴───────────────────────────────────────────────────────────┘
```
Everything that changes the *pixels* is in the key — including the **`Gamma` bucket** (the deliberate
gamma exception, §4.5/§9). `SizePx` is quantized fixed-point so 14.0 and 14.001 share a realization (defeats
animation cache blow-up). `SubpxX` N=4 is the classic accuracy/footprint tradeoff (4 horizontal phases ≈
visually exact for Latin, ≤4× glyph copies, not ∞).

### 3.2 `PackedGlyph` — atlas placement (the value the batcher needs)

```
PackedGlyph (28 B)
┌──────────┬──────┬──────────────────────────────────────────────┐
│ Page     │  2 B │ atlas page index (→ IGlyphAtlas.PageTexture)  │
│ Format   │  1 B │ R8 / BGRA8(color)                             │
│ Flags    │  1 B │ resident / pending / overflow / evicted       │
│ U0,V0    │  4 B │ ushort×2 texel coords (top-left)              │
│ U1,V1    │  4 B │ ushort×2 texel coords (bottom-right)          │
│ BearingX │  2 B │ short fixed 10.6 — left side bearing px       │
│ BearingY │  2 B │ short fixed 10.6 — top bearing (ascent dir)   │
│ Width    │  2 B │ ushort px                                     │
│ Height   │  2 B │ ushort px                                     │
│ LruClock │  4 B │ uint last-used frame index (eviction)         │
│ Advance  │  2 B │ ushort fixed 10.6 (cached for fast re-emit)   │
│ _pad     │  2 B │                                               │
└──────────┴──────┴──────────────────────────────────────────────┘
```

### 3.3 `ShapedRunBuilder` — zero-alloc shaper output (ref struct; `architecture-spec.md` §4.6)

```csharp
public ref struct ShapedRunBuilder
{
    // ALL spans point into a caller-owned ChunkedArena block. No GC arrays ever escape.
    public Span<ushort>     GlyphIds;    // [glyphCount]
    public Span<float>      Advances;    // [glyphCount]  advance widths (px, post-size)
    public Span<float>      OffsetsX;    // [glyphCount]  glyph placement dx
    public Span<float>      OffsetsY;    // [glyphCount]  glyph placement dy
    public Span<ushort>     Clusters;    // [glyphCount]  glyph → first source UTF-16 index in run
    public Span<GlyphProps> Props;       // [glyphCount]  cluster-start / diacritic / zero-width / can-break / justify
    public int   GlyphCount;             // written count
    public int   TextLength;             // source UTF-16 length consumed
    public float TotalAdvance;           // Σ Advances (fast path for max-content measure)
    public sbyte BidiLevel;              // even=LTR odd=RTL (affects visual emission order)
}
```
`GlyphProps` (1 byte) distils DWrite `DWRITE_SHAPING_GLYPH_PROPERTIES` to `IsClusterStart`, `IsDiacritic`,
`IsZeroWidth`, `CanBreakShapingAfter`, `Justification` (4 bits) — portable; DWrite and CoreText both provide
it. Needed for hit-test cluster boundaries and justified text.

### 3.4 `GlyphRunRealization` — the retained render-bridge slot (content-epoch stamped)

`GlyphRunTable : SlabAllocator<GlyphRunRealization>` lives in `Foundation`/`Scene`-adjacent (`foundations.md`
line ~291, `architecture-spec.md` §4.4). `DrawGlyphRunCmd` references it by `GlyphRunHandle` and **never
bakes atlas UVs** (UVs are volatile across eviction/compaction; resolved at batch time — §5.5).

```csharp
public struct GlyphRunRealization {
    public int   GlyphStart, GlyphCount;   // range into a shared GlyphInstanceStore (positions + GlyphKey ids)
    public int   FontFaceId;
    public ushort SizePx;                  // fixed 10.6
    public sbyte  BidiLevel;
    public byte   AaMode;                  // grayscale (v1) / color
    public uint   ContentEpoch;            // bumped on atlas repack/compaction OR re-shape → invalidates clean spans
    public uint   GenerationCount;         // layout generation that produced it (ShapedRunCache→Render bridge)
    public bool   Committed;               // set ONLY at CommitBounds; render thread reads only committed runs
    public uint   LruClock;
    public int    RefCount;                // live retained TextLayoutSlots; quarantined when referenced by spans
}
// per-glyph instance (16 B): { ulong GlyphKeyId (compact key→id), short PenX(10.6), short PenY(10.6), uint ColorOrZero }
```
The `ContentEpoch` clause is the §4.6/§4.5 contract: **clean-span reuse is valid IFF every handle `IsLive`
AND, for `GlyphRunRef` handles, the realization `ContentEpoch` is unchanged AND its baked-geometry hash is
unchanged** (`hardened-v1-plan.md` §4.4 — the validator captures the dest-rect/baseline geometry, not just
handle+epoch). The `Committed` bit closes the §5.3 "render reads a half-built run" race: the run is invisible
to the render thread until `CommitBounds`.

### 3.5 Retained text layout result (`TextLayoutHandle` → `TextLayoutSlot`)

`TextLayoutSlot` (in a `SlabAllocator<TextLayoutSlot>` inside the layout engine) holds **offsets into shared
ChunkedArenas**, not arrays:

```
TextLayoutSlot
  ├─ ContentHash : ulong      (L1-key-set ⊕ constraints — L2 cache identity, §7)
  ├─ Width, Height, Baseline : float
  ├─ MinWidth, MaxWidth : float   (intrinsic min/max-content for Yoga, §8)
  ├─ LineRange   : (int start,int count) → LineArena (LineInfo[])
  ├─ RunRange    : (int start,int count) → RunArena  (ShapedRunRef[], shared, ref-counted)
  ├─ GlyphRange  : (int start,int count) → GlyphArena (flattened glyphs for paint, VISUAL order)
  ├─ Trimmed     : bool, EllipsisRunRef
  └─ Gen, LruClock, Committed
LineInfo (32 B): {TextStart, TextLen, RunStart, RunCount, X, BaselineY, Width, Height, Ascent, Descent, IsTrimmed}
ShapedRunRef    : {ShapedRunCacheId, GlyphStart, GlyphCount, FaceId, SizePx, BidiLevel, PenXStart,
                   Fill (BrushHandle override or default), PerInstanceColorTable (lyrics: 0 or arena ref)}
```

---

## 4. DirectWrite leaf implementation (`FluentGpu.Windows` DirectWrite/ folder)

### 4.1 COM binding strategy — generated, not hand-typed (the §4.2 ruling)

**The original "hand-author TerraFX-style `[VtblIndex]` structs" plan is SUPERSEDED.** Per
`dotnet10-csharp14-zero-alloc.md` §4 + `hardened-v1-plan.md` §4.2:

- **Hot-path consume (RCW)** — the per-frame/per-measure DWrite calls (`GetGlyphs`,
  `GetGlyphPlacements`, `CreateGlyphRunAnalysis`, `GetAlphaTextureBounds`, `CreateAlphaTexture`) are bound by
  **hand-vtable `calli`**, but the slot integers and call convention are **generated by
  `FluentGpu.Interop.SourceGen` from a harvested, runtime-self-checked `dwrite.comabi.json`** (winmd ⨯
  ClangSharp, triple-oracle, plus a CI-RELEASE live-object QI-roundtrip gate). **No human types a vtable
  slot.** `delegate* unmanaged[MemberFunction]` for every call → AOT-friendly, inlinable, no marshalling
  stub. IID storage is `ReadOnlySpan<byte>` RVA literals (zero alloc).
- **Cold/warm COM (setup)** — `IDWriteFactory` creation, `IDWriteFontCollection`/`Family`/`Font`/`FontFace`
  resolution, `IDWriteFontFallback`(+`Builder`), `IDWriteRenderingParams`, `IDWriteNumberSubstitution`,
  variable-font/COLR `FontFace2/4/5`, `IDWriteColorGlyphRunEnumerator1` — all via
  **`[GeneratedComInterface]`** (the Microsoft AOT/trim-clean path), housed with the rest of the cold COM in
  `PlatformIntegration` (no `DisableRuntimeMarshalling` there — the generator uses `in`/`out`).
- **Callee-side CCW** (DWrite calling **back** into our `IDWriteTextAnalysisSource`/`Sink` during
  `AnalyzeScript`/`AnalyzeBidi`, and the `GetGlyphRunOutline` geometry sink) — there is **no ComWrappers
  template** for this, so it is a **generated static vtable of `[UnmanagedCallersOnly(CallConvMemberFunction)]`
  methods** emitted by `FluentGpu.Interop.SourceGen`, plus a **pooled pinned `GCHandle`** for the analyze-call
  duration (recovered via `GCHandle.FromIntPtr`, from an `ObjectPool` cap-32 — edge only).

**Interfaces in the closure (v1):** factory(+2/4) · font collection/family/font/face(+2/4/5) · text analyzer
(+analyzer1 for justification) · text-analysis source/sink (CCW) · glyph-run analysis · rendering params ·
font fallback(+builder) · number substitution · color-glyph-run enumerator1. Adopting portable itemization
(CoreText milestone) trims the bound surface to the shaping + raster closure (~12 interfaces) plus the
Unicode data tables.

### 4.2 COM thread-confinement (the keystone, per `hardened-v1-plan.md` §2 + §4.2)

> **The render thread owns every ComPtr.** ComPtr is **render-thread-confined + Move-only across the seam**;
> `SceneFrame` never shares a ComPtr by reference. A new FGCOM analyzer rule pins each COM object to one
> thread. **The DWrite factory is created SHARED** (a single process-lifetime root), but **every shaping CCW
> is thread-confined and DWrite is called serialized** on the confining thread, validated by a concurrent
> conformance test — *or off-thread raster is descoped* (it is, in v1: raster stays on the render thread).

Concrete CCW hardening (folded from `architecture-spec.md` §5.3):
- During `Analyze*`, **pin both** the source-state struct **and** the source UTF-16 `char[]`
  (`GetTextAtPosition` returns a live `WCHAR*` that DWrite reads synchronously).
- `QueryInterface` returns `S_OK` only for `IUnknown` + `IDWriteTextAnalysisSource`, else `E_NOINTERFACE`.
- `AddRef`/`Release` no-op return 1 (synchronous, single-threaded, DWrite does not retain).
- Prefer the `IDWriteGlyphRunAnalysis::CreateAlphaTexture` path to **minimize callbacks** entirely.

```csharp
// Generated callee CCW — illustrative slot (FluentGpu.Interop.SourceGen emits the vtable + the [UnmanagedCallersOnly]):
[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
static int GetTextAtPosition(void* self, uint textPos, ushort** outText, uint* outLen)
{   // recover thread-confined state via GCHandle.FromIntPtr((nint)((CcwBox*)self)->StateHandle);
    // return a live WCHAR* into the pinned source char[]; assert confining-thread ownership in DEBUG. }
```

### 4.3 Font system impl (`DWriteFontSystem : IFontSystem`)

```
ResolveFace(req):
  collection = systemCollection (process-lifetime ComPtr root, render-thread-confined)
  collection.FindFamilyName(Interner.Resolve(req.Family)) → familyIndex  (fallback "Segoe UI Variable")
  family.GetFirstMatchingFont(weight, stretch, style)     → IDWriteFont
  font.CreateFontFace()                                   → IDWriteFontFace (CACHED by (familyIdx,w,s,st))
  store ComPtr<IDWriteFontFace> in HandleTable<FaceSlot>  → FontFaceHandle{index, gen, Kind=FontFace}
  FaceSlot caches: unitsPerEm, ascent/descent/lineGap/capHeight/xHeight (DWRITE_FONT_METRICS),
                   a ushort[256] cmap fast-path (ASCII/Latin-1 cp→gid), variable-font axes.
```
- **System collection** is a process-lifetime ComPtr root, re-acquired on the PAL `WM_FONTCHANGE` event →
  bump `FontGeneration` → lazily invalidate L1/L2 caches and the face table on next probe.
- **Glyph-id BMP cache** (`ushort[256]` per face): `GetGlyphIndices` is COM-call-per-batch; caching cmap for
  Latin UI text means the overwhelming common path never crosses COM for cmap during shaping.

### 4.4 Itemization & shaping pipeline (`DWriteItemizer`, `DWriteTextShaper`)

```
Itemize(input):                              // v1 Windows backs ITextItemizer with DWrite Analyze*
  AnalyzeBidi   → BiDi level runs        (callee CCW source)
  AnalyzeScript → script runs            (callee CCW source + sink)
  FindBreakOpportunities: SearchValues<char> classify break/space/control + DWRITE line-break props (UAX #14)
  styleRuns (face/size from RunStyle) ∩ fallback coverage runs (MapCharacters, §4.6)
  → intersect all four → minimal ItemRun list into a ChunkedArena Span<ItemRun>

ShapeRun(input, ref dst):                    // input = ONE itemized run (script+bidi+face+locale)
  analyzer (cached IDWriteTextAnalyzer)
  textProps  = arena<DWRITE_SHAPING_TEXT_PROPERTIES>[textLen]
  clusterMap = arena<ushort>[textLen]
  glyphProps = arena<DWRITE_SHAPING_GLYPH_PROPERTIES>[maxGlyphs]      // maxGlyphs = 3*textLen + 16
  features   = FeatureTable.Resolve(input.FeatureSet)                 // DWRITE_TYPOGRAPHIC_FEATURES* (liga/kern/tnum…)
  hr = GetGlyphs(text, textLen, face, isSideways, isRTL, &scriptAnalysis, locale, numberSub,
                 &features, featureLen, maxGlyphs, clusterMap, textProps, dst.GlyphIds, glyphProps, &actual)
  if hr == E_NOT_SUFFICIENT_BUFFER: return false                      // caller doubles maxGlyphs in arena, retries (§6)
  GetGlyphPlacements(text, clusterMap, textProps, textLen, dst.GlyphIds, glyphProps, glyphCount,
                     face, sizePx, isSideways, isRTL, &scriptAnalysis, locale, &features, featureLen,
                     dst.Advances, glyphOffsets)
  splat glyphOffsets → dst.OffsetsX/Y ; distill glyphProps → dst.Props ; dst.Clusters = clusterMap
  dst.TotalAdvance = Σ Advances ; dst.GlyphCount = actual ; dst.BidiLevel = input.BidiLevel
```
- `SearchValues<char>` (`dotnet10` §3): `static readonly SearchValues<char>` for break/space/control sets,
  driving `FindBreakOpportunities` and `ShapedRunBuilder` boundary detection via `IndexOfAny` (SIMD, 0 alloc).
- `numberSub` (`IDWriteNumberSubstitution`) handles locale digit shaping (Arabic-Indic etc.).
- The `E_NOT_SUFFICIENT_BUFFER` retry is the only growth, and it stays in the arena (double `maxGlyphs`,
  re-bump, retry; bounded 2 iterations; on repeated failure split the run). No GC array materializes.

### 4.5 Rasterizer impl (`DWriteGlyphRasterizer : IGlyphRasterizer`) — render thread

```
RasterizeGlyph(key, dst, out info):
  face = FontFaceTable[key.FontFaceId].ComFace
  run  = DWRITE_GLYPH_RUN { face, fontEmSize=key.SizePx_dip, glyphCount=1, &key.GlyphId, &zeroAdvance, null, isSideways=false, bidiLevel=0 }
  txf  = subpixel-shift( key.SubpxX/4, key.SubpxY/N )                 // bake fractional offset into transform
  factory.CreateGlyphRunAnalysis(&run, &txf, key.RenderMode, measuringMode, key.GridFit, AA, 0,0, &analysis)
  analysis.GetAlphaTextureBounds(ALIASED_1x1, &rect)                 // R8 grayscale path (v1 default)
  info = { rect.w, rect.h, stride, format=R8, bearingX=rect.left, bearingY=rect.top }
  if dst.Length < required: return false                            // caller sized from MeasureGlyphBitmap first
  analysis.CreateAlphaTexture(ALIASED_1x1, &rect, dst, dst.Length)  // writes A8 coverage
  // COLR/CPAL color glyphs (emoji): FontFace4 + IDWriteColorGlyphRunEnumerator1 → per-layer runs → raster each
  //   layer → composite into a BGRA tile (format=BGRA8). Bitmap emoji (CBDT) via GetGlyphImageData.
```

**Grayscale-only v1 (DECIDED, matches `architecture-spec.md` §5.2/§5.3, `gpu-renderer.md` §10 / OQ-3).**
- **Grayscale A8** is the default: one coverage byte/texel, atlas page `R8_UNORM`, single-source SrcOver blend
  in the glyph PSO, works with the instanced-quad batcher directly **and** composites correctly over
  transparent/DComp/Mica/Acrylic surfaces.
- **ClearType (RGB subpixel)** needs a **second dual-source PSO** (`SV_Target1`, `BLEND{SrcColor=ONE,
  DstColor=INV_SRC1_COLOR}`), is **opaque-background-only** (fringes over transparency), and **breaks under
  transforms**. **Deferred to v2**: the `GlyphAaMode` flag bit is *reserved* in the key and exactly **one**
  glyph PSO is provisioned in v1 (`gpu-renderer.md` §5.2). macOS/CoreText is grayscale-only anyway, so this
  also unifies the cross-platform story.
- **Text gamma is the deliberate color exception.** Coverage is **not** blended naively in linear (thin stems
  go too thin). The glyph PS applies a DWrite-style gamma/enhanced-contrast curve to coverage **before** the
  linear blend; the per-target gamma/contrast lives in the `GlyphKey.Gamma` bucket and is A/B-validated
  against native DWrite/WinUI (`architecture-spec.md` §5.2 "Text blend").

### 4.6 Fallback (`FallbackFor`)

`IDWriteFontFallback::MapCharacters` (the *system* fallback chain Windows uses — correct emoji/CJK/complex
coverage for free). The itemizer drives it: walk a run; when the resolved face lacks coverage for `cp`
(`GetGlyphIndices`→0), call `MapCharacters` to split at the coverage boundary and assign the substitute face.
Cache `(faceId, scriptTag, cp-block) → substituteFaceId`. **Run boundaries align to grapheme clusters (UAX
#29) before fallback splitting** so combining marks never split across a fallback boundary.

### 4.7 Subpixel positioning, hinting, oversized-glyph valve

- **Horizontal subpixel:** `SubpxX` bucket N=4; pen X snapped to 1/4 px, the residual baked into the raster
  transform (4.5). Preserves inter-glyph spacing fidelity at ≤4× glyph storage.
- **Vertical:** `SubpxY`=1 for horizontal Latin/Cyrillic/Greek (baseline grid-snapped per line); vertical/
  sideways CJK may use N=4.
- **Hinting/grid-fit:** `NATURAL_SYMMETRIC` + `GRID_FIT_DEFAULT` (smooth, position-accurate, animation- and
  transform-friendly — favors GPU composability over maximal small-size sharpness); GDI-classic only behind an
  explicit "crisp small UI text" opt-in (forces integer positioning + grid-fit + disables subpixel X).
- **Glyph larger than a page** (huge font size): bypass the atlas — `TryGetOutline` →
  `IDWriteFontFace::GetGlyphRunOutline` through the **geometry-sink CCW** → tessellated `FillPathCmd`
  (`gpu-renderer.md` §5). AA handled by the path AA-fringe, not the atlas. The same CCW machinery (§4.2)
  serves this sink. This is the unbounded-size safety valve.

---

## 5. Glyph atlas design

### 5.1 Storage & pages

- Pages are RHI `ITexture` (`architecture-spec.md` §4.7 seam): **`R8_UNORM` grayscale** (default) and a
  **separate `BGRA8_UNORM` color page** for COLR/CPAL/SVG/CBDT emoji (the R8 atlas is monochrome-only).
  Default page **1024×1024** (1 MiB grayscale / 4 MiB color). Grow by adding pages, never resizing
  (resize invalidates all UVs). 1–3 pages typical for a UI app.
- **`GetOrAdd` routes by `key.AaMode`** — grayscale and color pages never mix (different format and sampling).

### 5.2 Packing — skyline bottom-left

Per page a **skyline bottom-left packer** (Jylänki) over a `Span<SkyNode>` (`architecture-spec.md` §5.3).
Glyphs are small and size-clustered → ~90%+ occupancy, O(pageWidthSegments) insert, 1 px transparent gutter
prevents bilinear bleed. Fixed-function struct, zero alloc:

```csharp
struct SkylinePacker { Span<SkyNode> nodes; int count; int W, H;
    bool TryInsert(int w, int h, out int x, out int y); /* bottom-left fit; merges nodes */ }
```

### 5.3 Eviction — frame-START, live-pinned, epoch-bumping (the §4.6 contract discipline)

This is the **authoritative glyph-atlas epoch/eviction discipline**:

- **Eviction runs only at frame START** (`IGlyphAtlas.BeginFrame`, phase 1 on the render thread). **Any glyph
  referenced by a live command — dirty OR clean — this frame is ineligible.** The liveness/pin set is
  computed from the snapshot's command stream (`hardened-v1-plan.md` §4.1 render-frame ordering: DRAIN
  workers → atlas eviction → record). `IGlyphAtlas.Pin(key)` marks a glyph live; `LruClock = frameIndex` on
  any touch.
- **Eviction is lazy & batched**, triggered when an insert fails (page full): scan the page's resident set
  for `LruClock < frameIndex - EvictThreshold` (≈120 frames ≈ 2 s), free skyline regions, **compact if
  fragmentation > 30%** (re-raster survivors to a fresh page, atomic swap). Either path **bumps
  `IGlyphAtlas.Epoch`** and the affected `GlyphRunRealization.ContentEpoch`, forcing re-record of any clean
  span that referenced the run. If still full after eviction → add a page.
- **Batch-time UV-resolve miss never faults**: it rasterizes into a **reserved overflow region** of the page
  (`architecture-spec.md` §4.6) rather than dropping the glyph.
- **Epoch validation is render-thread-LOCAL** (`hardened-v1-plan.md` §4.1): the epoch travels with the cached
  span recorded into the render thread's own back arena; the render thread compares the live epoch against
  the per-span epoch — zero cross-thread epoch staleness.
- **Glyph-run slots referenced by retained spans are refcounted/quarantined** — the slot is not reused until
  no retained span references it AND `_lastConsumedSeq > freedSeq` (consume-gated quarantine,
  `hardened-v1-plan.md` §2.3).

### 5.4 Upload to GPU — deferred, batched, via the texture-staging path

- CPU rasterization writes into a **render-thread staging slab** (`SlabAllocator<StagingBlock>`); the atlas
  accumulates a per-page dirty-rect list. `EndFrame(enc)` issues **batched** copies — one
  `ICommandEncoder.CopyBufferToTexture(stagingBuffer, pageTexture, region)` per dirty page region
  (`architecture-spec.md` §4.7 RHI delta; the dedicated **texture-staging ring** is fence-gated and **not**
  the instance `UploadRing`). New glyphs discovered during record are uploaded **before** the glyph draws
  submit (phase 9 batch boundary; `architecture-spec.md` §4.8 frame loop).
- **No `CreateTexture` in phases 6–13.** Atlas pages come from a **startup per-bucket texture pool**
  (`architecture-spec.md` §4.7); page growth is cold-path pool growth only.
- **Async upload** (return `PackedGlyph.Flags=Pending`, draw a 1-frame-late/overflow glyph) is reserved for
  pathological first-paint of large CJK walls; v1 uses synchronous batched upload.

### 5.5 How `DrawGlyphRunCmd` references the atlas (by-handle, UVs late)

The DrawList stores **no UVs** (`architecture-spec.md` §4.5):

```
DrawGlyphRunCmd (POD; architecture-spec §4.5 / §5.3 / gpu-renderer §3.1):
  Run       : GlyphRunHandle   // → GlyphRunTable[GlyphRunRealization]
  Color     : BrushHandle      // default run fill (overridden per-instance for lyrics — §11)
  Clip      : ClipHandle
  Transform : Affine2D         // node WorldTransform (positions are already FINAL device-space, VISUAL order)
  Flags     : byte             // AaMode (grayscale v1), color-run bit
```

At **batch time** (phase 9, render thread, `gpu-renderer.md` §10) the batcher walks the run, calls
`IGlyphAtlas.GetOrAdd(key)` (resident ⇒ cheap dict probe), reads `PackedGlyph.{Page, U0V0U1V1, Bearing}`, and
emits one instanced quad per glyph into the quad batch keyed by `(Page, AaMode, Clip)` → all grayscale glyphs
from one page coalesce into **one `DrawIndexedInstanced`**. UVs are resolved **late** precisely so
eviction/compaction (which bump `Epoch`/`ContentEpoch`) stay transparent to the retained command stream —
handles stay valid, UVs recompute. The atlas packer colocates a run's glyphs on one page so a run = 1 batch.

```
GlyphInstance (gpu-renderer §10, 48 B): { float4 destRectDev, ushort2 uvMin, ushort2 uvMax, uint colorPremulLinear, uint pageOrFlags }
glyph.vs/ps.hlsl: pull dest+uv; sample atlas (R8 coverage); coverage·gamma·premulColor; SrcOver. No SDF.
```

---

## 6. Zero-alloc strategy — the through-line (`foundations.md` / `dotnet10` §3)

| Concern | Mechanism |
|---|---|
| Shaper scratch (textProps, clusterMap, glyphProps, offsets) | per-frame `ChunkedArena` blocks; O(1) reset; native high-water counter gates chunk growth (NOT the GC tripwire) |
| Shaper output spans | caller-owned arena via `ShapedRunBuilder` ref struct (no arrays escape) |
| Buffer-too-small growth | retry in the same arena (double, re-bump); never a GC array |
| Itemization run list / break opps | arena `Span<ItemRun>` / `Span<BreakOpp>`; `SearchValues<char>` classification (SIMD) |
| Layout lines/runs | `LineArena`/`RunArena` (retained in `SlabAllocator` slots) |
| Glyph instances for paint | shared `GlyphInstanceStore` (double/triple-buffered with the DrawList per `hardened-v1-plan.md` §2.3) |
| Measurement | pure: L2 cache-hit returns `TextMeasure` by value — **zero arena touches** (§8) |
| Strings | `StringId` everywhere; source UTF-16 owned by `TextBlockPayload` at the EDGE, exposed as a stable, pinnable `ReadOnlySpan<char>` for the frame; **no `string` on the paint path** |
| Feature/family tables | `FrozenDictionary` built once at load; `GetAlternateLookup<ReadOnlySpan<char>>()` to probe without `ToString()` |
| COM callback state | pooled pinned `GCHandle` (`ObjectPool` cap-32, edge) for the analyze-call duration only |
| Hashing | `GlyphKey`/`ContentHash` via xxHash over raw bytes — no boxing |
| Small fixed buffers | `[InlineArray(N)]` (e.g. subpixel-phase scratch, per-line run rings) |
| DrawList walk | `Walk<TSink>(...) where TSink : IDrawSink, allows ref struct` (devirtualized, no box) |

**Source-text ownership (the one legitimate edge GC ref).** The user supplies `string` at the `Element`
edge; `TextBlockPayload` holds the `char[]` and exposes a stable, pinnable `ReadOnlySpan<char>` to the layout
engine across the frame (no copy). All internal indices are `int` UTF-16 offsets into that span. GC ref at
the edge only (`architecture-spec.md` §5.3, `foundations.md` edge rule).

---

## 7. Caching architecture (two-level)

```
L1: SHAPED-RUN cache (constraint-FREE — avoids re-running the DWrite analyzer)
  key = ShapeCacheKey { TextHash:ulong(xxhash of the run's UTF-16 bytes), ResolvedPostFallbackFaceId,
                        SizeQ, ScriptTag:uint, BidiLevelParity:sbyte, Locale:StringId, FeatureSet }   (32 B)
        // includes itemization context: a node's text may produce MULTIPLE visual runs (BiDi-split),
        // NOT one run per StringId (architecture-spec §5.3).
  val = ShapedRunCacheSlot { GlyphStart..Count → persistent GlyphStore (SlabAllocator), TotalAdvance,
                             RefCount, LruClock, FontGeneration, GenerationCount, Committed }
  store: open-addressing (Robin Hood) over arena; refcounted by live TextLayoutSlots.
  invalidate: FontGeneration bump (WM_FONTCHANGE) drops stale entries lazily on probe.

L2: LAYOUT/WRAP cache (constraint-BEARING — avoids re-line-breaking)
  key = ContentHash { L1-key-set-hash ⊕ MaxWidthQ ⊕ Alignment ⊕ WrapMode ⊕ TrimMode ⊕ MaxLines }
  val = TextLayoutSlot (§3.5)
```

- **One SHAPE per content; re-WRAP on constraint change** (`architecture-spec.md` §5.3 honest claim).
  Shaping is constraint-independent (same glyphs regardless of wrap width); wrapping is constraint-dependent.
  Resize re-wraps without re-shaping — the common drag-resize path is L1-hit/L2-miss = O(glyphs) re-wrap,
  never re-shape, never a COM round-trip.
- **`MaxWidth` is quantized UP to an integer device-pixel grid BEFORE it enters Yoga** so Yoga's 8-entry ring
  sees stable keys (defeats sub-pixel width jitter on continuous resize/animation — same trick as `SizePx`
  snapping in `GlyphKey`).
- Both caches are **bounded LRU** on the same frame clock as the atlas (a scrolled-away paragraph ages out
  shaping/layout/glyphs together), eviction frees arena ranges via free-lists, and entries carry
  `GenerationCount` + a **`Committed`** bit set only at `CommitBounds` (the render thread reads only committed
  runs — the §5.3 ShapedRunCache→Render bridge).

---

## 8. Text layout above shaping (the Yoga bridge)

### 8.1 Pipeline (UI thread, phase 6)

```
TextLayoutRequest { ReadOnlySpan<char> text, FontRequest baseFont, ReadOnlySpan<RunStyle> styleRuns,
                    StringId locale, FlowDirection, TextAlignment, WrapMode, TrimMode, MaxLines }

Layout/Measure(req, constraints):
 1. ITEMIZE   : ITextItemizer.Itemize → minimal ItemRun list (BiDi ∧ script ∧ style ∧ fallback). (arena)
 2. SHAPE     : per ItemRun → L1 probe → ITextShaper.ShapeRun on miss. Yields glyphs+advances.
 3. LINEBREAK : UAX #14 break opportunities (FindBreakOpportunities; v1 DWrite props + SearchValues).
                NOT greedy-break on raw advances — break at cluster + BiDi-run boundaries (architecture-spec §5.3).
 4. WRAP      : line fill to constraints.MaxWidthQ using cumulative advances + break opps; visual reorder per
                line (UAX #9 L2: reverse level runs).
 5. ALIGN     : leading/center/trailing/justify (justify uses GlyphProps.Justification + TextAnalyzer1).
 6. TRIM      : overflow & TrimMode≠None ⇒ ellipsis run ("…" shaped once, cached), replace tail glyphs,
                char/word granularity.
 7. METRICS   : Width = max line width, Height = Σ line heights, Baseline = first-line ascent,
                MinWidth = widest unbreakable segment, MaxWidth = Σ TotalAdvance (single-line).
 8. COMMIT    : CommitBounds sets the Committed bit (render thread may now read the runs).
```

### 8.2 Measurement & the Yoga `MeasureFunc` bridge

The bridge satisfies the verified Yoga signature **without a captured closure** (`architecture-spec.md`
§5.3): **one `static readonly YogaMeasureFunc s_textMeasure = MeasureTextStatic`** set once at node creation;
`MeasureTextStatic` recovers the `TextLayoutHandle` via a generational handle in the `LayoutNode` user-data
slot.

```csharp
static YogaSize MeasureTextStatic(LayoutNode node, float availW, YogaMeasureMode wMode,
                                  float availH, YogaMeasureMode hMode)
{
    var layoutH = node.UserData<TextLayoutHandle>();              // generational handle, never a closure
    float maxW  = wMode switch { Exactly => availW, AtMost => availW, Undefined => float.PositiveInfinity };
    var m = s_engine.Measure(layoutH, new MeasureConstraints(QuantizeUp(maxW), wMode, node.MaxLines));
    float width  = wMode == Exactly ? availW : MathF.Min(maxW, m.Width);
    float height = hMode == Exactly ? availH : m.Height;
    return new YogaSize(width, height);
}
// TextMeasure { float MinWidth, MaxWidth, Width, Height, Baseline; }  — MinWidth/MaxWidth ARE the intrinsic
//   min/max-content sizes flex layout needs, computed without a second pass (advances + break opps).
```

**8-cache-ring synergy.** Yoga re-invokes `MeasureFunc` up to 8× with different `(availW, mode)` probes and
caches each. Our **L2 cache** turns each probe into a hash hit after the first, so the 8-entry ring × L2 cache
⇒ a text node is fully laid out **once** per content/constraint set, reused across measure passes and the
final arrange. The arrange-phase `Bounds` (LOCAL space, phase 8) then feeds `Emit`.

### 8.3 Hit-testing (caret/selection) — over the retained slot, no re-shape, no COM

- `HitTestPoint(x,y)` → binary-search lines by `BaselineY`, walk runs in visual/BiDi order accumulating
  advances to the cluster, split within cluster by half-advance for leading/trailing caret edge → `{textPos,
  isTrailingHit, isInside}`.
- `HitTestTextPosition(pos, trailing)` → inverse: locate run/cluster, sum advances, account for BiDi (caret X
  in visual space ≠ logical order) → caret `{X, Y, Height}`.
- `GetSelectionRects(start, len, dst)` → one `RectF` per **visual** fragment (a logical range maps to multiple
  disjoint rects under BiDi); bounded by `dst.Length` (zero-alloc; caller sizes). **This is the single
  read-side geometry primitive that backs BOTH the on-screen selection highlight (§15) and
  `ITextRangeProvider` (§17)** — there is no second path.
- All operate on `TextLayoutSlot` arenas. The caret/selection geometry **is** the `ITextReadSide` (§17) the UIA
  range provider and the highlight emitter share; **UAX #29 grapheme/word navigation, mutation, IME, and
  undo/redo are CORE** via the `ITextDocument` editable seam (§16) — no longer deferred.

---

## 9. Color & the text gamma exception (cross-ref §4.5, `architecture-spec.md` §5.2)

- Glyph atlas pages store **coverage** (R8) or **premultiplied BGRA** (color emoji). Shape AA is resolved by
  DWrite at raster time.
- The renderer blends in **linear** everywhere *except text*: glyph coverage is blended with a **DWrite-style
  gamma + enhanced-contrast curve** in a gamma/perceptual space (the `GlyphKey.Gamma` bucket), A/B-validated
  against native DWrite/WinUI. This is the one sanctioned departure from the linear-blend invariant. Run color
  arrives **premultiplied linear** in the `GlyphInstance` (the per-instance color path, §11).
- **Grayscale only in v1** (ClearType deferred — §4.5); color emoji pages composite as premultiplied BGRA.

---

## 10. NativeAOT implications (`dotnet10` §4, `hardened-v1-plan.md` §4.2)

- **No `ComWrappers` on the hot path; no IL emit; no runtime reflection.** Hot-path DWrite RCWs are
  generator-emitted `delegate* unmanaged[MemberFunction]` `calli` from `dwrite.comabi.json` (slots
  runtime-self-checked against the loaded system DLL); cold/warm setup is `[GeneratedComInterface]`; callee
  CCWs are generated static `[UnmanagedCallersOnly(CallConvMemberFunction)]` vtables. Fully trimmable.
- **IID storage** as `ReadOnlySpan<byte>` RVA literals.
- **Footprint:** DWrite is an OS DLL (zero binary cost). The bound surface validated against a spike before a
  figure is pinned (`architecture-spec.md` footprint note). Portable itemization (CoreText milestone) trims to
  ~12 interfaces + the Unicode tables (~100 KB+ trimmed, UAX #9/#14/#24 conformance-tested).
- **No `params object[]`; deps are `ReadOnlySpan<DepKey>`** (16 B scalar-blittable; reference deps → the
  parallel `GcDepTable` compared by `ReferenceEquals` — never a `[FieldOffset]` GC/scalar union, which is
  illegal CLR layout). Relevant to `UseSyncedLyrics`/`UseImage` hook deps (§11).
- **Pinning:** text spans handed to DWrite are pinned for the call duration only; no GC handles held across
  frames except the process-lifetime factory/collection roots (render-thread-confined).

---

## 11. Synced lyrics — `LyricsLayoutEngine` in `FluentGpu.Media`

**Requirement** (`app-requirements-waveemusic.md` §3.4): DirectWrite line layout with **per-syllable color
animated by playback ms** (NOT the frame clock), line scroll, CJK furigana/romanization, blurred-art backdrop,
optional 3D fan. The hard constraint: per-frame lyric color must be **per-INSTANCE glyph data, not a
`BrushHandle` re-bake** (which would mint a new handle every tick and break the clean-span invariant) and not a
gradient-atlas row lerp (which needs the missing texture-upload path).

### 11.1 Design

`LyricsLayoutEngine` (portable, `FluentGpu.Media`, deps `Foundation` + Text iface) composes the existing seam:

```csharp
// FluentGpu.Media
public sealed class LyricsLayoutEngine
{
    // Lay out one lyric line via the text seam; cache the GlyphRunRealization (SHAPING is cached).
    public LyricLineHandle LayoutLine(in LyricLine line, FontRequest font, in LayoutConstraints c);
    // Map a per-character timing model + the playback clock to per-instance glyph colors.
    public void ApplyClock(LyricLineHandle line, float playbackMs, ColorF sung, ColorF unsung, ColorF active);
}

public readonly struct LyricLine { public ReadOnlySpan<char> Text; public ReadOnlySpan<SyllableTiming> Timings; }
public readonly struct SyllableTiming { public int CharStart, CharLen; public float StartMs, EndMs; }
```

**Hook (the Wavee dev writes):**
```csharp
LyricsBinding UseSyncedLyrics(in LyricsModel lyrics, IPlaybackClock clock);
// composes UseMemo(layout, deps:(lyrics,font)) + an AnimTrack.DrivenClock bound to clock — phase 7.
```

### 11.2 Per-instance color path (the load-bearing decision)

- **Shaping/layout is cached** as a normal `GlyphRunRealization` (one shape per lyric line; `Committed`).
- **Color is per-INSTANCE.** The per-glyph instance record (§3.4, the `uint ColorOrZero` field) carries the
  syllable color. The **phase-7 `AnimTrack.DrivenClock`** (`app-requirements-waveemusic.md` §3.4) — sourced by
  the `IPlaybackClock` playback-ms `ref float`, **never** the frame clock — writes the per-instance color
  directly into the instance data the batcher reads. `ApplyClock` maps `playbackMs` against each
  `SyllableTiming` to choose sung/active/unsung color (and an interpolated edge for the active syllable's
  wipe).
- The **active line is `PaintDirty` and re-records its single glyph run every frame** (trivially within
  budget — one tiny node). Per the contract we **drop the "clean-span reuse for the active line" framing**:
  clean-span reuse applies to *shaping*, not to per-frame instance emission. Inactive lines stay clean
  (cached, memcpy'd).
- **No `BrushHandle` churn, no atlas re-bake, no texture upload per tick** — color lives in the instance
  stream the batcher already emits.

### 11.3 Lyrics extras

- **Line scroll** = `LocalTransform` translate-Y on the lyrics container (TransformDirty, phase 7) — never
  LayoutDirty.
- **CJK furigana/romanization** = a secondary annotation run laid out above the base run (smaller
  `FontRequest`, its own `GlyphRunRealization`), composed by `LyricsLayoutEngine`.
- **Backdrop** = `UseImageBackdrop` (Theme/Media subsystem, not this doc).
- **3D fan** = `PushLayer{Effect=Transform3D}` via the optional effects leaf (perspective is out of the
  core 2.5D scope; this subsystem only supplies the flat glyph runs).

### 11.4 Lyrics 13-phase placement (UI/render split per `hardened-v1-plan.md` §2.2)

- **P4 render** (UI): `UseSyncedLyrics` memoizes line layout (shape on miss via the seam on the UI thread).
- **P7 animation** (UI): `AnimTrack.DrivenClock` reads `IPlaybackClock` ms, `ApplyClock` writes per-instance
  colors into the active line's instance data; sets the active line `PaintDirty`.
- **P8 record** (RENDER): `Emit` the active line (re-records its one run) + clean lines memcpy.
- **P9 batch** (RENDER): glyph instances (with per-instance color) coalesce by atlas page.

---

## 12. Cross-platform seam boundary — Windows vs portable vs macOS (CoreText)

| Concern | Portable (`FluentGpu.Text`) | Windows (`FluentGpu.Windows` DirectWrite/) | macOS (`Text.CoreText`) |
|---|---|---|---|
| BiDi (UAX #9) | seam `ITextItemizer`; v1 = DWrite; CoreText milestone = portable `Text.Unicode` | `AnalyzeBidi` (CCW) | portable `Text.Unicode` (shared) |
| Script (UAX #24) | seam; v1 = DWrite; milestone = portable | `AnalyzeScript` (CCW) | portable `Text.Unicode` |
| Line break (UAX #14) | `SearchValues` + portable DFA at milestone | DWrite props (v1) | portable `Text.Unicode` |
| Font enum/fallback | seam `IFontSystem` | `IDWriteFontCollection` / `IDWriteFontFallback::MapCharacters` | `CTFontCollection` / `CTFontCreateForString` |
| Shaping (glyphs/placements) | seam `ITextShaper` | `IDWriteTextAnalyzer` | `CTFontGetGlyphsForCharacters` (+ HarfBuzz for full OT) |
| Glyph raster | seam `IGlyphRasterizer` | `IDWriteGlyphRunAnalysis::CreateAlphaTexture` | `CTFontDrawGlyphs` → `CGBitmapContext` (A8) |
| Color glyphs | seam (Format=BGRA) | `FontFace4` + `IDWriteColorGlyphRunEnumerator1` (COLR/CPAL/SVG/CBDT) | `kCTFontColorGlyphsAttribute` / `CTFontDrawGlyphs` |
| Subpixel AA | grayscale portable | ClearType (v2, opaque-only) | grayscale only (macOS dropped subpixel) |
| Atlas, packing, LRU, key, caches | **100% portable** | — | — (reused as-is) |
| Layout (wrap/align/trim/hit-test) | **100% portable** | — | — (reused as-is) |
| Lyrics (`LyricsLayoutEngine`) | **100% portable** (consumes the seam) | — | — (reused as-is) |
| Emission (`DrawGlyphRunCmd`, per-instance color) | **100% portable** | — | — (reused as-is) |

**Net macOS contract** (`architecture-spec.md` §5.3 / §10.6): CoreText implements exactly three method
families — `IFontSystem` (`CTFontCollection`/`CTFontCreateForString`/metrics/fallback), `ITextShaper`
(`CTFontGetGlyphsForCharacters` + advances, or HarfBuzz for full OpenType), `IGlyphRasterizer`
(`CTFontDrawGlyphs` → `CGBitmapContext` grayscale A8). The **atlas, both caches, all layout, hit-testing,
lyrics composition, and the `DrawGlyphRun` emission are shared/portable** (~70% of LOC). CoreText is *simpler*
— grayscale-only ⇒ no dual-source PSO. **BiDi/script/line-break move into portable `FluentGpu.Text.Unicode` at
this milestone** (deferred until now precisely so DWrite's `Analyze*` carries v1 and the v1 callee-CCW surface
stays minimal — `architecture-spec.md` §10.6).

---

## 13. Failure & edge cases

- **Missing glyph / no fallback** → draw `.notdef` (tofu) or a box-with-hex-U+XXXX overlay; never crash, never
  silently skip.
- **Atlas page full + nothing evictable** (a giant CJK wall, all live this frame) → add a page; if the page
  budget is exceeded → batch-time UV-miss rasterizes into the reserved **overflow region** (never faults),
  log a perf counter.
- **Glyph larger than a page** → bypass atlas; tessellated `FillPathCmd` via `GetGlyphRunOutline` (§4.7).
- **ClearType requested in v1** → force grayscale (only one glyph PSO is provisioned; `GlyphAaMode` reserved).
- **Variable-font animation** (wght/opsz tween) → each axis value = distinct `GlyphKey.Variation`; mitigate by
  quantizing axis values (e.g. wght to nearest 25) + aggressive LRU.
- **Mixed-direction selection / BiDi caret** → `GetSelectionRects` returns visual-fragment rects; affinity via
  `isTrailingHit`.
- **`E_NOT_SUFFICIENT_BUFFER`** → arena-grow retry (§4.4), capped; on repeat, split the run.
- **Combining marks / clusters spanning a fallback boundary** → itemizer aligns run boundaries to grapheme
  clusters (UAX #29) before fallback splitting.
- **Surrogate pairs / astral codepoints** → clusterMap indices are UTF-16 code-unit based (matches DWrite);
  hit-testing converts code-unit ↔ code-point/grapheme at the public API.
- **Font hot-swap (`WM_FONTCHANGE`)** → bump `FontGeneration`; lazily invalidate L1/L2 + face table on next
  probe; in-flight retained layouts keep working until re-laid-out.
- **Zero-size / empty text** → short-circuit to `TextMeasure.Zero`, no shaping, no layout slot.
- **Extremely long single line (no break opps)** → min-content can exceed available width; produce correct
  (overflowing) metrics so scroll/clip works (Yoga handles overflow).
- **Lyrics with no timing data** → treat the whole line as one syllable; static color, no per-instance clock.
- **Playback clock jumps backward (seek)** → `ApplyClock` is idempotent on `playbackMs`; recompute colors from
  the absolute ms, no accumulation state.
- **Stale glyph-run after eviction mid-frame** → impossible: eviction is frame-START + live-pin + epoch bump;
  the §4.6 contract makes a clean span referencing a repacked run re-record, not read stale UVs.
- **Empty / collapsed selection** (`anchor == extent`) → no `DrawSelectionRectCmd`; emit only the caret fill (§15).
- **Selection spanning a soft-wrap boundary** → `GetRangeRects` returns one fragment per line; a trailing-edge
  rect extends to the line's content box right edge (LTR) / left edge (RTL) to read as "wraps to next line."
- **Edit grows a single-line field past width** → re-shape produces overflow metrics; the editable control
  scrolls horizontally (caret-follow), not a re-wrap (single-line ignores break opps; §16).
- **IME composition then app loses focus** → `EndComposition(commit:false)` discards the composing span; the
  document `Snapshot()` excludes uncommitted composition text (composition is an overlay, not buffer state).
- **Undo across an IME commit** → one undo unit per `Unlock` that mutated; the committed IME run is one unit.
- **Concurrent UIA read + edit** → `ITextDocument.TryLock(ReadOnly)` is granted while no ReadWrite lock is held;
  a UIA `GetText`/range walk takes a read lock; the contract forbids mutation under a foreign read lock.
- **`RangeFromPoint` outside any glyph** → snap to the nearest line's start/end position; never returns -1.
- **Localized string with no ICU data** (`InvariantGlobalization` build) → `ILocaleFormatter` falls back to
  invariant formatting + an `other`-only plural ruleset; logs once (§18 build config).
- **Inline object with no measured size** → itemizer reserves `AdvanceDip = 0`; layout fills it at arrange; if
  still 0 (unmeasured element) the object collapses to a zero-width cluster (never splits a grapheme).
- **U+FFFC with no backing inline element** → treated as `.notdef` tofu (object id sentinel −1), never crashes.

---

## 14. Selection model — `SelectionState` (semantics; column storage in `scene-memory.md`)

The selection is a **value-type model** owned here; its **SoA column storage** (a `SelectionState` column keyed
by `NodeHandle.Index`) and the `DrawOp.DrawSelectionRect` **registration** live in `scene-memory.md` §2.2/§4.1
(this doc owns the **semantics**, that doc owns the **bytes** — they reference each other). A node has a
selection iff it carries a live `TextLayoutHandle` and `SelectionState.HasContent`.

```csharp
// 12 B, blittable POD; stored in the scene SelectionState column (scene-memory.md owns the column).
public readonly struct SelectionState : IEquatable<SelectionState>
{
    public readonly int  Anchor;     // 4  the FIXED end (where the drag/shift-select started), UTF-16 index
    public readonly int  Extent;     // 4  the MOVING end (the caret), UTF-16 index
    public readonly byte Affinity;   // 1  CaretAffinity { Downstream=0, Upstream=1 } — line-wrap boundary side
    public readonly byte Flags;      // 1  bit0 HasContent (Anchor!=Extent), bit1 IsBlockSelection (col-mode), bit2 Active
    public readonly ushort _pad;     // 2
    public int Start => Math.Min(Anchor, Extent);
    public int Len   => Math.Abs(Extent - Anchor);
    public bool IsCollapsed => Anchor == Extent;     // caret only, no highlight
}
```

- **Anchor/extent (not start/len)** so shift-extend and drag-select are directional: the *extent* is the caret
  the user moves; the *anchor* is pinned. `Start`/`Len` derive the normalized range the read-side consumes.
- **Affinity** disambiguates the soft-wrap boundary (a position at the end of line N == start of line N+1): the
  caret renders on the line implied by `Downstream`/`Upstream`. Mirrors the existing `isTrailingHit` plumbing
  in §8.3 (the read-side already produces affinity from hit-testing).
- **Mutation chokepoint:** the selection is written ONLY via `ITextDocument.SetSelection` under an edit lock, or
  by the selection-drag gesture (input-a11y arena, L2) calling `SetSelection` on the focused document. The
  reconciler writes the resulting `SelectionState` into the scene column at phase 5 `WriteLayout` (same
  single-writer discipline as every other column). `SelectionState` equality drives a `PaintDirty` (not
  `LayoutDirty`) bump — a selection change re-emits the highlight overlay, never re-shapes.
- **macOS boundary:** `SelectionState` is portable POD; on macOS the same column feeds `NSTextInputClient`
  selected-range reporting and `NSAccessibility` selection. No Windows type appears in the model.

## 15. Selection-highlight + caret geometry → `DrawSelectionRectCmd` (this doc owns the read-side; `gpu-renderer.md` owns the cmd shape + raster)

`EmitSelection` (§2 seam) is the RECORD-phase (phase 8, render thread) emitter. It reads the layout's
`ITextReadSide.GetRangeRects(Start, Len, dst)` — the **same** primitive `GetSelectionRects` already exposes —
and writes one `DrawSelectionRectCmd` per visual fragment **before** the run's `DrawGlyphRun` (so the highlight
sits under the glyphs and the gamma-corrected text composites on top), then a thin caret fill at the extent.

- **The opcode:** `DrawSelectionRectCmd` is a **NEW** `DrawOp` (`gpu-renderer.md` §3.1 owns the payload struct
  shape + the raster; `scene-memory.md` §4.1 owns the `DrawOp.DrawSelectionRect` enum registration). Its payload
  is the union of `{ RectF Rect; BrushHandle Fill; ClipHandle Clip; byte Flags }` — the same shape family as
  `FillRoundRectCmd`; it rasters as a plain premultiplied-linear quad (NOT the text gamma path — highlight is a
  solid fill, not coverage). The caret reuses `FillRoundRectCmd` (a 1-dip rounded rect), so **only one new
  opcode is introduced** by this doc.
- **Geometry source of truth:** fragment rects come exclusively from the read-side (BiDi ⇒ multiple disjoint
  rects, the §8.3 / §13 wrap-boundary rule). There is no second selection-geometry path; `ITextRangeProvider`
  (§17) consumes the identical `GetRangeRects`.
- **Clean-span discipline:** the highlight `DrawSelectionRectCmd` references `BrushHandle`/`ClipHandle` only
  (no `GlyphRunHandle`), so it follows the ordinary clean-span rule (valid IFF handles `IsLive`); a
  selection-only change bumps `PaintDirty` on the text node and re-records its highlight span + glyph run —
  inactive text stays clean (memcpy'd). The selection overlay does **not** invalidate the `GlyphRunRealization`
  content-epoch (selection is not a pixel property of glyphs).
- **Composition underline** (IME) reuses this emitter family: a `FillRoundRectCmd` underline + the existing
  transient `DrawGlyphRun` for the composing run (input-a11y.md §10 already specifies the underline overlay; we
  supply the geometry via the read-side).

**Selection → clipboard binding.** The L1 "the `IClipboard` mechanism exists; it has nothing to read" gap is
closed here: a Copy/Cut command (from the input route / `UseCommand`) calls `ITextReadSide.CopyText(sel.Start,
sel.Len, scratch)` (§17) to fill a caller-owned `Span<char>`, then `IClipboard.SetText(span)` (the OLE mechanism
owned by `input-a11y.md` §12 — referenced, not redefined). **Cut** additionally takes an `ITextDocument` write
lock and `Replace(sel.Start, sel.Len, default)` (editable docs only; §16). **Paste** = `IClipboard.TryGetText`
(an edge `string`, input-a11y's named edge alloc) → `Replace(sel.Start, sel.Len, pasted)`. The clipboard string
is an edge allocation (input-a11y §12), never on the paint path; the read-side copy is zero-alloc into the
caller's span.

## 16. Editable transactional buffer — `ITextDocument` (single AND multi-line; FULLY core, not v2)

The editable seam (§2) is an **`ITextStoreACP2`-shaped commit-lock document**. It is **portable + COM-free**;
`input-a11y.md` §10 hosts (a) the `IImeSession` Imm32 event pump and (b) — when TSF lands — the cold-COM
`ITextStoreACP2` CCW that forwards OS lock/edit callbacks into this seam. The buffer model is designed FULLY
here for v1; nothing about editing is deferred.

### 16.1 Buffer representation (zero-alloc steady state)

```
TextDocument (in a SlabAllocator<DocSlot>; one per editable control instance)
  ├─ Buffer     : a PIECE-TABLE over two ChunkedArena spans (original + add), so an edit is O(pieces),
  │               never an O(n) array shift; Snapshot() flattens lazily into a pinned char[] cache (re-flattened
  │               only on ContentVersion change — the paint/measure path reads the flattened span, no per-edit copy).
  ├─ Selection  : SelectionState (§14)
  ├─ Composition: { int Start, Len; CompUnderline[] underlines } or empty (overlay, NOT buffer content)
  ├─ Undo       : ChunkedArena ring of UndoUnit { int start, oldLen, newLen, AddSpanRef, prevSelection } — one per Unlock
  ├─ ContentVersion : ulong (bumps on every committed Replace; drives ITextReadSide.ContentVersion + UIA staleness)
  ├─ Lock       : { LockKind held; uint lockGen } — the ITextStoreACP2 write-lock state machine
  └─ Sinks      : ITextEditSink[] (small inline array; layout re-shape + UIA TextChanged + caret-rect)
```

- **Piece-table** (not a gap buffer) so multi-line documents and large paste are O(pieces); `ChunkedArena` keeps
  added text native-backed (no LOH cliff). `Snapshot()` returns a stable pinnable `ReadOnlySpan<char>` for the
  frame (the §6 source-text edge rule — the one legitimate edge GC ref is the flattened cache, re-flattened only
  on version change).
- **Single vs multi-line:** `IsMultiLine=false` collapses `\r\n`/`\n` on insert (Enter commits/moves focus per
  the control), ignores break opps in wrap, and scrolls horizontally on overflow; `IsMultiLine=true` keeps
  newlines as hard paragraph breaks and wraps within the content box. **Both are core; the seam is identical.**

### 16.2 The commit-lock contract (the `ITextStoreACP2` write-lock state machine)

- **No mutation outside a held `ReadWrite` lock.** `TryLock(ReadWrite)` fails (returns false) if any lock is
  held; `TryLock(ReadOnly)` succeeds while no ReadWrite is held (UIA reads + Narrator walks take read locks).
  This is exactly the TSF document-lock arbitration `input-a11y.md` §10 names as the v2 TSF workstream — but the
  **seam and the buffer state machine are core now**, so Imm32 (v1) and TSF (when it lands) both drive the same
  `ITextDocument`.
- **One transaction per lock:** all `Replace`/`SetSelection`/`SetComposition` calls between `TryLock` and
  `Unlock` coalesce into ONE `UndoUnit`; `Unlock` flushes sinks (`OnTextChanged`/`OnSelectionChanged`), bumps
  `ContentVersion`, and is the single point that makes an edit visible to layout/UIA — closing the
  "read a half-built edit" race the same way `Committed` closes it for shaping (§3.4).
- **Thread:** edits run on the **UI thread** (input arrives there; phase 2 input → phase 4/5 reconcile). The
  document is UI-thread-confined; the render thread only ever reads the *committed* `TextLayoutSlot` + the
  scene `SelectionState` column (never the live document). No ComPtr touches the document.

### 16.3 Edit → re-shape (incremental, via the L1/L2 caches)

`OnTextChanged(start, oldLen, newLen)` invalidates only the **affected lines'** L2 wrap entries and the
**affected runs'** L1 shaped entries (line-granular: a single-char insert re-shapes one line, re-wraps from that
line down). The two-level cache (§7) already keys shaping by run content, so unchanged runs L1-hit. Caret-rect
for `IImeSession.SetCompositionRect` comes from `HitTestTextPosition(extent)` (§8.3) projected by input-a11y's
shared transform helper.

### 16.4 Undo/redo + IME

- **Undo/redo** walk the `UndoUnit` ring: `Undo` re-`Replace`s the inverse span and restores `prevSelection`
  (each is itself a transaction under an internal lock, so a redo of an undo is symmetric). Bounded ring (LRU
  drop oldest); coalescing rule = consecutive single-char typing folds into one unit until a caret jump/IME/paste.
- **IME composition** is an **overlay, not buffer content**: `SetComposition` stamps the composing span +
  underlines (drives the §15 underline overlay + the composing `DrawGlyphRun`); `EndComposition(commit:true)`
  turns it into a real `Replace` (one undo unit); `EndComposition(commit:false)` discards. `Snapshot()` never
  includes composing text, so measure/paint/UIA see a consistent committed buffer.

## 17. The shared text read-side — `ITextReadSide` (backs on-screen selection AND `ITextRangeProvider`)

**One read-side, two consumers.** `ITextReadSide` (§2) is the single source of truth for text geometry,
navigation, attributes, and plain-text extraction over a retained `TextLayoutSlot`. It is consumed by:
1. **On-screen selection** — `EmitSelection` (§15) calls `GetRangeRects` for the highlight.
2. **`ITextRangeProvider`/`ITextProvider`** — `input-a11y.md` §11.4 owns the **UIA CCW wrapper** (cold COM,
   `[GeneratedComInterface]`/`[GeneratedComClass]`, `UseComThreading`); it is a thin adapter that forwards every
   UIA call into `ITextReadSide`:

| UIA member (input-a11y owns the CCW) | `ITextReadSide` call (this doc owns) |
|---|---|
| `ITextProvider.GetSelection` / `RangeFromPoint` | `Selection` (§14) / `RangeFromPoint(x,y)` |
| `ITextRangeProvider.GetBoundingRectangles` | `GetRangeRects(start,len,dst)` (same as §15 highlight) |
| `ITextRangeProvider.Move`/`MoveEndpointByUnit` | `MoveBoundary(pos, TextUnit, count, …)` (UAX #29) |
| `ITextRangeProvider.ExpandToEnclosingUnit` | `ExpandToEnclosing(pos, TextUnit, …)` |
| `ITextRangeProvider.GetText` | `CopyText(start, len, dst)` (no string alloc inside; edge `ToString` in the CCW) |
| `ITextRangeProvider.GetAttributeValue` | `GetTextAttribute(pos, TextAttributeId, …)` |
| range staleness (re-validate on text change) | `ContentVersion` |

- **`TextUnit`** mirrors UIA: `Character (grapheme, UAX #29)`, `Format`, `Word`, `Line`, `Paragraph`, `Page`,
  `Document`. `MoveBoundary` operates on the retained slot — **no re-shape, no COM** (UAX #29 grapheme/word
  segmentation runs over the cluster map + `SearchValues`-classified word boundaries; the portable
  `FluentGpu.Text.Unicode` tables carry the full UAX #29 DFA at the CoreText milestone, DWrite/cluster-map
  approximation in v1).
- **Narrator read-by-line/word + caret tracking + selection announce** all resolve through this one read-side;
  the screen reader's *only* document-text path (the L3 gap) is now fully backed.
- **macOS:** `ITextReadSide` is portable; the `NSAccessibility` text protocol (`accessibilityString(for:)`,
  `accessibilityRange(for:)`) wraps the identical seam — `input-a11y.md` §11.7 boundary unchanged.

## 18. Edge localization — `ILocaleFormatter` (format → `StringId`; no string on the paint path)

The L9 gap: shaping does locale *digit* substitution (DWrite `IDWriteNumberSubstitution`, §4.4) but there is no
locale *value* formatting. `ILocaleFormatter` (§2 seam) closes it **at the edge**, preserving the
no-string-on-paint-path invariant:

- **Where it runs:** phase 4 (render/reconcile, UI thread) when a component builds its `Element` tree. The app
  writes `Text(fmt.FormatCurrency(amount, usd, locale))`; the formatter produces a **`StringId`** interned in
  the `StringTable` (`scene-memory.md` owns the table). The `TextBlockPayload` thereafter holds the
  already-formatted `char[]`; **the paint/measure path sees only the `StringId`** — zero string work in phases
  6–13, the invariant intact.
- **Backing data:** ICU via .NET globalization. This **reverses the `InvariantGlobalization=true`** default for
  the shipping app: `dsl-aot.md` owns the `Directory.Build.props` localization build config (ICU app-local or
  OS ICU); this doc owns the **formatter seam + the edge contract**. When ICU is absent (a deliberate
  invariant-only build), the formatter degrades to invariant formatting + `other`-only plurals (§13 edge case).
- **Plural/select:** `FormatPlural` is an ICU-MessageFormat-style `select`/`plural` over a `StringId` template +
  named args; `ResolvePlural` exposes the CLDR plural category. The template is parsed once and cached in a
  `FrozenDictionary` (build-once, §6) keyed by `(StringId template, StringId locale)` so a repeated format is a
  dictionary probe + a span fill, not a re-parse.
- **RTL icon/image mirroring** (the other L9 half) is an `AutoMirror` flag on `Image`/`Path` resolved at
  `layout.md` §4.1's `FlowDirection` boundary — **not owned here** (referenced: `layout.md` owns the mirror
  geometry; this doc owns only the text/number formatting).
- **macOS:** ICU is the same cross-platform library; the seam is portable, the formatter implementation shared.

## 19. Inline-flow rich text — object-replacement characters in the itemizer (L14, core)

The L14 gap: text is an opaque leaf; inline elements (an avatar in a sentence, an inline `Button`, a count
badge) cannot flow with text. The fix is **object-replacement codepoints (U+FFFC) in the itemizer**:

- **Itemizer treats each U+FFFC as a single, zero-script, neutral-BiDi cluster boundary** and emits an
  `InlineObjectRun` (§2) instead of a glyph run; `CollectInlineObjects` returns one per U+FFFC. The object run
  **never merges** with adjacent glyph runs (it is its own item), and its boundaries align to grapheme clusters
  (the §4.6 UAX #29 rule) so it never splits a combining sequence.
- **Advance is RESERVED, filled by layout:** the itemizer/layout reserves `AdvanceDip`/`AscentDip`/`DescentDip`
  for the object; the **measured inline-element size comes from `layout.md` §10** (the inline element is a
  real layout node measured by the flex/grid pass). This doc owns the *itemizer seam* (where the hole is in the
  text stream); **`layout.md` owns the *placement geometry*** (where the node lands on the line, baseline
  alignment, line-height contribution) — they reference each other. The line-fill (§8 WRAP) treats the reserved
  advance like a glyph cluster's advance, so wrap/align/justify all just work.
- **Hit-testing + selection over inline objects:** the U+FFFC occupies exactly one UTF-16 position; selecting
  "through" an inline object includes one position (the object is one selectable atom). `ITextReadSide`
  navigation treats it as one `Character` unit; `GetRangeRects` returns the object's reserved box as a fragment.
- **Emission:** the text engine emits the surrounding glyph runs as usual; the inline element draws via its own
  node's normal record path at the position layout assigned — the text engine does **not** rasterize inline
  content, it only reserves the flow gap (the same separation as the oversized-glyph valve §4.7, but for whole
  elements). No new opcode is needed for inline objects.
- **macOS:** U+FFFC handling is portable (CoreText `CTRunDelegate` is the analogous mechanism, wrapped behind
  the same `ITextItemizer` seam at the CoreText milestone); v1 Windows DWrite itemization pre-marks U+FFFC
  before `Analyze*`.

---

## 20. Where each piece lands in the 13-phase loop (and on which thread)

| Phase | Work | Thread | Assembly |
|---|---|---|---|
| 1 pump | atlas `BeginFrame` (LRU clock advance + frame-START eviction sweep, epoch bump) | RENDER | FluentGpu.Windows DirectWrite/ / Text |
| 2 input | selection-drag / caret moves arrive (input-a11y arena, L2) → `ITextDocument.SetSelection`; edge-autoscroll (L11) writes `ScrollOffset` | UI | Input / Text |
| 4 render | `UseSyncedLyrics` memoizes line layout (shape-on-miss); **`ILocaleFormatter` formats values → `StringId`** at the edge (§18) | UI | Media / Text |
| 5 reconcile | `WriteLayout` writes the `SelectionState` column (§14) + inline-object reserved advances (§19); edit `OnTextChanged` invalidates affected L1/L2 entries (§16) | UI | Reconciler / Text |
| 6 layout | `MeasureText` via static `YogaMeasureFunc` → itemize (incl. U+FFFC inline objects) → shape (L1) → wrap (L2) → metrics; `CommitBounds` sets `Committed` | UI | Layout / Text |
| 7 animation | lyric per-instance color via `AnimTrack.DrivenClock` (playback ms); line-scroll `LocalTransform` | UI | Media |
| 8 record | `Emit` FINAL device-space glyph runs (VISUAL order) → `DrawGlyphRunCmd`; **`EmitSelection` → `DrawSelectionRectCmd` + caret** UNDER the glyphs (§15); clean spans memcpy (epoch+geometry-hash validated) | RENDER | Render / Text |
| 9 batch | resolve `GlyphKey`→`PackedGlyph` UVs; coalesce by `(page, AaMode, clip)`; selection quads coalesce as fills; atlas `EndFrame` → batched `CopyBufferToTexture` | RENDER | Render / FluentGpu.Windows DirectWrite/ |
| 10 submit | glyph PSO draws inside the submitted command list | RENDER | FluentGpu.Windows D3D12/ |
| 12 passive | UIA `TextChanged`/`SelectionChanged`/caret events for seq ≤ last-presented (`ITextEditSink` → input-a11y `ITextRangeProvider` notifications) | UI | Input / Text |

ComPtr touches are confined to phases 1/9/10 on the render thread (the sole COM owner). The UI-thread shaping
call in phase 6 uses the thread-confined callee CCW (§4.2); editing (§16), selection (§14), the read-side
(§17), and localization (§18) are **all UI-thread + COM-free** in this seam (the render thread reads only the
committed `TextLayoutSlot` + the scene `SelectionState` column, never the live document). The build order ships
single-thread-first (quarantine=0) before the UI/render split is flipped behind the green race gate.

---

## 21. Summary of load-bearing decisions

1. **Seam is portable + COM-free**: `IFontSystem / ITextItemizer / ITextShaper / IGlyphRasterizer /
   IGlyphAtlas / ITextLayoutEngine`, span-filling, handle-returning, no `string` on the paint path.
2. **DWrite bound by GENERATOR** from a harvested, runtime-self-checked `dwrite.comabi.json` (hot-path RCW
   `calli`) + `[GeneratedComInterface]` (cold setup) + generated thread-confined callee CCW vtables — **no
   human-typed slots, no ComWrappers on the hot path**.
3. **Render thread owns every ComPtr; DWrite factory SHARED + shaping CCW thread-confined**; single-thread-
   first build order.
4. **Itemize → fallback → shape pipeline** (one run = single face/script/direction; a node yields multiple
   visual BiDi runs); **`SearchValues<char>`** classification.
5. **Two-level cache**: L1 shaped-run (constraint-free) + L2 wrap (constraint-bearing) — one shape per
   content, re-wrap on constraint change; `MaxWidth` quantized UP before Yoga; `SizePx` quantized in the key.
6. **`GlyphKey` (24 B)** captures everything pixel-affecting incl. the **gamma bucket**;
   **`GlyphRunRealization` carries a content-epoch** and a **`Committed`** bit.
7. **Atlas**: `R8` grayscale + separate `BGRA8` color page; skyline packer; **frame-START, live-pinned,
   epoch-bumping eviction**; UVs resolved LATE at batch time so eviction/compaction stay transparent to the
   retained DrawList; upload via `CopyBufferToTexture` from a dedicated staging path (no `CreateTexture` in
   6–13).
8. **Grayscale-only v1; text gamma is the deliberate color exception**; ClearType (2nd dual-source PSO)
   deferred to v2 with the `GlyphAaMode` flag reserved.
9. **MeasureFunc** = one static `YogaMeasureFunc` + generational handle (no closure); intrinsic min/max-content
   in one pass; Yoga's 8-ring × L2 cache ⇒ one layout per content/constraint set.
10. **Synced lyrics** (`LyricsLayoutEngine`, `FluentGpu.Media`): cached shaping, **per-INSTANCE syllable color
    on the phase-7 playback-driven clock** (not `BrushHandle` re-bake), active line re-records one run/frame.
11. **Selection is CORE** (not display-only): a 12 B `SelectionState{Anchor,Extent,Affinity}` model (semantics
    here; SoA column in `scene-memory.md`); highlight via the **new `DrawSelectionRectCmd`** opcode emitted
    UNDER the glyphs from the **same `GetSelectionRects`** read-side; `PaintDirty` (never `LayoutDirty`).
12. **Editing is CORE** (not v2): an `ITextStoreACP2`-shaped **commit-lock `ITextDocument`** over a piece-table,
    single-line **and** multi-line, with IME composition (overlay, not buffer), undo/redo, and a write-lock
    state machine; UI-thread-confined + COM-free (the render thread reads only the committed slot + column).
13. **One read-side, two consumers** (`ITextReadSide`): the identical geometry/navigation/attributes/text seam
    backs the on-screen selection highlight (§15) **and** `ITextRangeProvider` (input-a11y owns the UIA CCW) —
    closing both L1 (selection) and L3 (text a11y) against one source of truth.
14. **Edge localization** (`ILocaleFormatter`): number/date/currency/plural formatted at phase 4 into an
    interned `StringId`, so the paint path still sees no `string` — the no-string-on-paint-path invariant holds.
15. **Inline-flow rich text** (L14): U+FFFC object-replacement codepoints in the itemizer emit `InlineObjectRun`s
    whose advance is reserved (placement geometry owned by `layout.md`); inline elements flow with text.
16. **~70% portable**; macOS/CoreText implements 3 method families and is simpler (grayscale-only); selection,
    editing, the read-side, and localization are all portable seams (CoreText/`NSTextInputClient`/ICU leaves).

---

## Implemented from the gap analysis

These `core-fundamentals-gap-analysis.md` rows are now **fully-specified, buildable CORE design** in this doc
(no "v2", no "defer", no "display-only"):

| Gap item | What it was | Folded into CORE — where |
|---|---|---|
| **L1** — text selection + editable seam | "display-only v1; `SelectionState`/highlight/clipboard/editable absent; editing stays v2" | §0 (scope 9–10), §2 (`SelectionState`, `ITextDocument`, `ITextEditSink`, `EmitSelection`), **§14** selection model, **§15** highlight→`DrawSelectionRectCmd`, **§16** editable commit-lock buffer (single+multi-line, IME, undo/redo — **NOT v2**), **§15** clipboard binding (see also §17 `CopyText`), §13 edge cases, §20 phase table |
| **L3 (read-side)** — text-selection a11y read-side backing `ITextRangeProvider` | "only the UIA Text *bit*; zero range-provider design" | §2 (`ITextReadSide`), **§17** the one read-side feeding BOTH on-screen selection AND `ITextRangeProvider` (UIA CCW owned by input-a11y, mapping table inline) |
| **L9 (format)** — edge localization (number/date/currency/plural) | "`InvariantGlobalization=true`; no locale value formatting" | §2 (`ILocaleFormatter`), **§18** format-at-the-edge → `StringId` (no-string-on-paint-path preserved); build config referenced to `dsl-aot.md` |
| **L14 (inline)** — inline-flow rich text | "text is an opaque leaf; reserve the object-replacement seam only" | §0 (scope 13), §2 (`ITextItemizer.CollectInlineObjects`, `InlineObjectRun`), **§19** U+FFFC object-replacement in the itemizer; placement geometry referenced to `layout.md` |

**New artifacts this doc now defines (owned here):** the `SelectionState` POD model (semantics), the
`ITextReadSide` seam, the `ITextDocument`/`ITextEditSink` editable commit-lock seam, the `ILocaleFormatter`
seam, the `InlineObjectRun` shape + `CollectInlineObjects` itemizer method, and the `EmitSelection`/`ReadSide`
additions to `ITextLayoutEngine`. **Referenced (owned elsewhere):** `DrawSelectionRectCmd` payload + raster
(`gpu-renderer.md`), the `SelectionState` **column** + `DrawOp.DrawSelectionRect` registration
(`scene-memory.md`), the `ITextProvider`/`ITextRangeProvider` UIA CCW + `IClipboard` + `IImeSession`/TSF
(`input-a11y.md`), inline-object **placement geometry** + `FlowDirection`/`AutoMirror` (`layout.md`), the
localization **build config** (`dsl-aot.md`).

---

## Changed vs the original synthesis

Folded the following amendments from the authoritative cross-cutting docs into the prior `text.md`:

- **COM binding strategy SUPERSEDED.** The original hand-authored `[VtblIndex]` TerraFX-style structs are
  replaced by the §4.2 ruling: hot-path RCW `calli` **generated** from a harvested, runtime-self-checked
  `dwrite.comabi.json` (no human-typed slots); `[GeneratedComInterface]` for cold/warm DWrite setup;
  **generated** callee CCW vtables. Removed the FAR framing (the contracts are now authoritative, not
  "requests").
- **Thread model made explicit.** Added the `hardened-v1-plan.md` §2 confinement: the render thread owns every
  ComPtr; the **DWrite factory is SHARED + shaping CCW thread-confined**; per-phase UI/render thread placement
  table (§20); single-thread-first build order; consume-gated quarantine + refcounted glyph-run slots.
- **Itemization promoted to its own seam** (`ITextItemizer`) above the shaper, with the explicit **itemize
  (BiDi+script+linebreak) → fallback → shape** pipeline and the BiDi-multiple-visual-runs clause; line-break
  is **not** greedy-on-advances; **`SearchValues<char>`** for break/space/control classification.
- **`GlyphKey` gained the `Gamma` bucket** (the deliberate text-gamma exception) and AaMode color value;
  reconciled to the `architecture-spec.md` §4.6 24 B key fields.
- **`GlyphRunRealization` content-epoch + `Committed` bit + refcount/quarantine** added as the
  ShapedRunCache→Render bridge and the §4.5 clean-span rule (valid IFF `IsLive` ∧ `ContentEpoch` unchanged ∧
  baked-geometry hash unchanged); epoch validation render-thread-LOCAL.
- **Atlas eviction discipline rewritten** to the §4.6 contract: **frame-START, live-pinned (dirty OR clean),
  epoch-bumping**, batch-time UV-miss → reserved overflow region (never faults); upload via the new RHI
  **`CopyBufferToTexture` + dedicated texture-staging ring** (not the instance `UploadRing`) with a **startup
  per-bucket texture pool** (no `CreateTexture` in phases 6–13). Added the **separate `BGRA8` color/emoji
  page** + `IDWriteColorGlyphRunEnumerator1`.
- **Grayscale-only v1 made the decision** (was a recommendation): one glyph PSO provisioned, `GlyphAaMode`
  reserved, ClearType deferred to v2; the **text-gamma exception** stated against the linear-blend invariant.
- **MeasureFunc bridge** uses **one static `YogaMeasureFunc` + a generational handle in the node user-data
  slot** (never a closure) per §5.3; `MaxWidth` quantized UP before Yoga.
- **`ChunkedArena`** (reserve-then-commit, native-backed, native high-water gate) replaces the single-buffer
  per-frame arena throughout the zero-alloc story; added `FrozenDictionary`/`[InlineArray]`/`allows ref
  struct`/`ReadOnlySpan<DepKey>` (+ the illegal `[FieldOffset]` GC/scalar-union note) per `dotnet10` §3/§6.
- **Synced lyrics ADDED** (`LyricsLayoutEngine` in `FluentGpu.Media`): cached shaping + **per-INSTANCE
  syllable color written by the phase-7 `AnimTrack.DrivenClock` on the playback clock** (not a `BrushHandle`
  re-bake, not a gradient-atlas lerp), active line re-records one run/frame; the `UseSyncedLyrics` hook;
  CJK furigana/line-scroll/backdrop/3D-fan boundaries.
- **Display-only v1 SUPERSEDED → selection + editing are CORE** (gap fold L1/L3-readside). <!-- canon-allow: names the superseded display-only framing -->
  Added the `SelectionState` model (§14), the `DrawSelectionRectCmd` highlight emitter + caret + clipboard
  binding (§15), the `ITextStoreACP2`-shaped editable `ITextDocument` commit-lock buffer (single+multi-line,
  piece-table, IME-as-overlay, undo/redo — §16), and the shared `ITextReadSide` that backs BOTH on-screen
  selection AND `ITextRangeProvider` (§17). The old "editing is a v2 follow-up" framing is removed.
- **Edge localization ADDED** (gap fold L9): the `ILocaleFormatter` seam formats number/date/currency/plural at
  the edge into an interned `StringId` (§18) — no `string` on the paint path; build config referenced to
  `dsl-aot.md`.
- **Inline-flow rich text ADDED** (gap fold L14): U+FFFC object-replacement codepoints in the itemizer emit
  `InlineObjectRun`s with reserved advance (§19); placement geometry referenced to `layout.md`.
- **macOS/CoreText boundary** updated to the 3-method-family contract and the `FluentGpu.Text.Unicode`
  portable-itemization milestone (deferred so DWrite `Analyze*` carries v1); the new selection/editing/read-side/
  localization seams are all portable (CoreText / `NSTextInputClient` / `NSAccessibility` / ICU leaves).
