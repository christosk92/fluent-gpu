export const meta = {
  name: 'winui-parity-batch',
  description: 'Fix 7 WinUI-parity defects and adversarially verify 1:1 vs microsoft-ui-xaml',
  phases: [
    { title: 'Baseline', detail: 'gate HEAD + before-shots' },
    { title: 'Wave 1', detail: 'lists engine, focus/PasswordBox, Expander, scrollbars' },
    { title: 'Integrate 1', detail: 'snippets + build + checks + shots + commit' },
    { title: 'Wave 2', detail: 'editable ComboBox, SplitButton + flyout motion' },
    { title: 'Integrate 2', detail: 'snippets + build + checks + shots + commit' },
    { title: 'Verify', detail: 'adversarial loop until dry, max 4 rounds' },
    { title: 'Finalize', detail: 'final report' },
  ],
}

// ───────────────────────── constants ─────────────────────────
const REPO = 'C:/WAVEE/fluent-gpu'
const WINUI = 'C:/WAVEE/microsoft-ui-xaml'
const IMGS = 'C:/Users/ChristosKarapasias/.claude/image-cache/fb78fc67-c8bf-4e8c-8604-a0d926efacde'
const OUT = 'C:/WAVEE/fluent-gpu/out/parity'

const RULES = `
HARD RULES (violations void your work):
- Repo: ${REPO} (branch feat/winui-control-parity, do NOT switch branches). WinUI reference source: ${WINUI}.
- You may ONLY edit the files in YOUR manifest below. You may READ anything anywhere.
- NEVER edit src/FluentGpu.VerticalSlice/Program.cs or src/FluentGpu.WindowsApp/ShotScene.cs — return snippets in your structured result instead.
- NEVER run dotnet/msbuild/build/test commands. Another agent integrates and builds. Read-only git (log/show/diff/status) is allowed.
- ALL file edits via the Edit tool only. PowerShell -replace / Set-Content / Out-File on source files is BANNED (UTF-8-no-BOM mojibake has corrupted this repo before).
- Ground EVERY visual/motion/size value in ${WINUI} files you ACTUALLY READ in this session. Quote file+line+value in winuiEvidence. NEVER invent or recall WinUI values from memory. Check _perf2026 variants for deltas. Resolve resource indirection chains to concrete values (full ARGB including alpha).
- The root-cause analysis given below is strong but you MUST re-confirm it by reading the cited code before editing.
- If a correct fix needs a file outside your manifest (including corrections to EXISTING checks in VerticalSlice/Program.cs that encode old behavior), DO NOT edit it — add a requestedEdits entry with the exact proposed change.
- First read ${REPO}/docs/guide/control-fidelity.md (visual/motion work) and skim ${REPO}/.claude/skills/fluentgpu/SKILL.md if present.
- The user's bug screenshots are PNGs in ${IMGS}/ (2.png..7.png) — Read the ones relevant to your defect to see the exact symptom.
- Zero-allocation discipline: no per-frame managed allocations in hot paths; wire effects once at mount; no LINQ/closures in per-frame code.
`

const CHECK_RULES = `
GOLDEN CHECKS (checkSnippet, REQUIRED): write ONE self-contained C# static method named exactly as you report in methodName (pattern: D<N><Name>Checks, taking the same parameter(s) as sibling check methods, e.g. (StringTable strings) — VERIFY by reading the file) for src/FluentGpu.VerticalSlice/Program.cs that proves your fix headlessly. BEFORE writing it, read several existing check methods in that file (search "static void" and "Check(") and copy their harness idioms EXACTLY (host construction, Check(name, ok, detail), node walking, click/tick helpers). Use ONLY helpers/types that already exist. Name individual checks "cpN.x — description". The method must compile as-is when inserted before "static int Main()". callSiteLine = exactly "<methodName>(strings);" (match sibling call style).
SHOT SNIPPET (shotSnippet, optional): if your defect needs a NEW screenshot scene id, read src/FluentGpu.WindowsApp/ShotScene.cs first and return a switch-case snippet + helper component source in the same style; else null.
RESULT: your final message is consumed by an orchestrator — fill the schema fields exactly; editedFiles must list every file you modified.
`

const SEAM_CONTRACT = `
SEAM CONTRACT (pre-agreed cross-agent API, follow EXACTLY so parallel work compiles together):
- The overlay agent adds to the PopupOptions type an init-only nullable float property named SeamOffsetY, default null. Meaning: (selected-row center Y) minus (popup center Y), in popup-local pixels.
- The Dropdown open/close animation branch in OverlayHost animates BOTH ClipT and ClipB around that seam per WinUI SplitOpen/SplitClose (clip origin center, ClipTranslateY=offset immediately, band grows/shrinks both ways, 250ms cubic(0,0,0,1) open, content TranslateY stays 0, no fade). SeamOffsetY==null keeps current edge behavior.
- The ComboBox agent sets it ONLY via object-initializer syntax: ... new PopupOptions(...){ SeamOffsetY = <expr> } on the NON-editable dropdown open path (OverlapStretch carousel).
`

const M = {
  D1: {
    title: 'ListView/ItemsView render empty (critical regression)',
    files: ['src/FluentGpu.Layout/FlexLayout.cs','src/FluentGpu.Hosting/AppHost.cs','src/FluentGpu.Reconciler/Reconciler.cs','src/FluentGpu.Reconciler/VirtualListEl.cs','src/FluentGpu.Controls/ItemsView.cs','src/FluentGpu.Controls/ListView.cs','src/FluentGpu.Controls/GridView.cs','src/FluentGpu.Scene/VirtualLayout.cs'],
    winui: ['controls/dev/CommonStyles/ListViewItem_themeresources.xaml (+_perf2026)','controls/dev/ItemContainer/ItemContainer_themeresources.xaml','controls/dev/ItemsView/* (templates)'],
    brief: `SYMPTOM: gallery ListView page (src/FluentGpu.WindowsApp/CollectionsMenusPages.cs ~line 10, ListView.Create of 8 strings in a Width=280 card) and ItemsView page (MiscPages.cs ~line 25, ItemsView.Create(items, columns:4)) render an EMPTY dark panel. Regressed in commit 4a9047b.
ROOT CAUSE (re-confirm): (1) VirtualListEl viewport measures 0 when the parent imposes no size — FlexLayout.MeasureViewport (~src/FluentGpu.Layout/FlexLayout.cs:300-320) maps NaN width/height to 0 for non-ContentSized viewports; gallery hosts give no height, and BoxEl.Direction defaults to 0 (row) so Grow cannot distribute on the missing axis. (2) Reconciler.RealizeWindow (~Reconciler.cs:506-566) runs pre-layout with viewport=Hint(ve.Height) (NaN to 1024, explicit 0 STAYS 0) and nothing re-realizes after ArrangeViewport publishes the real ViewportW/H — only scroll marks VirtualRangeDirty.
FIX (design verified, implement carefully): (a) natural-size fallback in MeasureViewport for virtual hosts with sc.ItemCount>0 and FlexGrow==0: main axis NaN -> sc.Layout.ContentExtent(count, cross) clamped by Min/Max; cross axis NaN -> availW when finite. The Grow==0 gate keeps 10k lists on the hard-viewport path (no 440000px flex bases). (b) realize-after-layout: at end of ArrangeViewport, when ItemCount>0 compute the window via sc.Layout.Window(...) and mark NodeFlags.VirtualRangeDirty if VirtualWindowing.NeedsRealize (see src/FluentGpu.Scene/VirtualLayout.cs:51-67); in AppHost.RunFrame (~AppHost.cs:385-393) add a bounded (max 2 iterations) re-realize->relayout loop after the layout phase so first paint is correct. (c) hygiene: give ItemsView/ListView root BoxEls Direction=1 where the list axis is vertical; check GridView.cs for the same shape; remove the leftover Console.Error.WriteLine debug at ~Reconciler.cs:349 if present.
CONSTRAINTS: must keep 10k-row virtualization + recycling + zero steady-state alloc intact (existing e11virt.* and check 38 must still pass). Do NOT alter ArrangeZStack semantics in FlexLayout.cs (another agent depends on current behavior). If existing checks encode the old realize timing, return corrections via requestedEdits.
CHECKS to add (cp1.*): (a) exact gallery ListView shape (Width=280 card, NO height anywhere above) realizes 8 rows with W~280; (b) ItemsView.Create(8 items, columns:4) in an auto-height Start-aligned row realizes 8 tiles with grid extent = ContentExtent; (c) 10k items with height 400 realizes <40 rows, then growing host height re-realizes to cover (proves realize-after-layout).`,
  },
  D2: {
    title: 'PasswordBox reveal button disappears after first click',
    files: ['src/FluentGpu.Input/InputDispatcher.cs','src/FluentGpu.Controls/EditableText.cs','src/FluentGpu.Controls/PasswordBox.cs'],
    winui: ['controls/dev/CommonStyles/PasswordBox_themeresources.xaml (+_perf2026) — RevealButton style, IsTabStop=False ~line 193','dxaml/xcp/core/native/text/Controls/PasswordBox.cpp — RevealPassword ~257-284, OnGotFocus arm-clear ~572-581 (search broadly if lines moved)'],
    brief: `SYMPTOM: the eye affix appears when typing, but after clicking it ONCE it vanishes and never returns until the box is emptied and retyped.
ROOT CAUSE (re-confirm): the engine moves pointer focus to ANY clicked node that has OnClick (InputDispatcher.cs ~line 275 SetFocus(up); SetFocus ~905-956 filters only Disabled; nodes with OnClick are auto-focusable per Reconciler.cs ~1105). Clicking the eye blurs the field root (self OnFocusChanged(false) fires), then PasswordBox.Remask() (~PasswordBox.cs:107-114) calls RestoreFocus -> OnFocusChanged(true) -> the focus-gain handler (~line 146) clears _canShowReveal -> AffixFor() (~91-94) returns null -> affix unmounts. The arm flag only re-arms on empty->non-empty (~155-164), so it never returns. WinUI: RevealButton is IsTabStop=False — clicking it NEVER moves focus off the PasswordBox, and OnGotFocus (the arm-clear) only runs when focus enters from outside.
FIX: (1) engine: pointer-activation focus resolves to the nearest SELF-OR-ANCESTOR carrying NodeFlags.Focusable (add a NearestFocusable helper near IsSelfOrAncestorOf ~line 960); if none, focus is UNCHANGED (WinUI: clicking non-focusable space does not move focus). Apply at both pointer-focus sites (~lines 275 and 288 — verify). (2) EditableText.cs: affix InnerButton(s) (~323-345) get TabStop=false so they are not focus targets; with (1), clicking an affix leaves the field focused and the blur/refocus storm never happens. Keep RestoreFocus calls as harmless belt-and-braces. (3) PasswordBox.cs semantics stay UNCHANGED (they are WinUI-cited and correct).
RISK SWEEP: this changes global click-focus behavior. Read the focus-dependent existing checks in VerticalSlice/Program.cs (23h/23i families and anything calling ClickNode then asserting focus) and the overlay light-dismiss catcher (Reconciler.cs ~1102-1105, TabStop=false — with your fix clicking it no longer steals focus, which is the WinUI-desired behavior per its comment). Return requestedEdits for any existing check that legitimately needs its expectation updated; do NOT silently change behavior that checks rely on without flagging it.
CHECKS to add (cp2.*): (a) focus box, type 1 char -> affix mounted; click affix -> affix STILL mounted, text masked, field still focused; (b) press-and-hold peek: pointer-down on eye -> raw password rendered mid-press; pointer-up -> masked; (c) blur to another control, refocus -> affix NOT mounted (WinUI OnGotFocus rule); typing keeps it unmounted until empty->non-empty.`,
  },
  D3: {
    title: 'Expander animation totally wrong + content clipped',
    files: ['src/FluentGpu.Controls/Expander.cs'],
    winui: ['controls/dev/Expander/Expander.xaml — ExpandDown storyboard ~62-77, CollapseUp ~78-90, ExpanderContentClip ~113, corner filtering ~35/64','controls/dev/Expander/Expander_themeresources.xaml (+_perf2026) — ExpanderMinHeight 48, header padding, chevron 32/12/margin 20,0,8,0, content padding 16, ExpanderContentDownBorderThickness 1,0,1,1'],
    brief: `SYMPTOM: open/close motion looks wrong and the expanded content ("An action" button) is CLIPPED at the card bottom (gallery page mounts initiallyExpanded:true — see src/FluentGpu.WindowsApp/StatusLayoutPages.cs ~41-43, but you may NOT edit that file; fix must be in the control).
ROOT CAUSE (re-confirm): Expander.cs ~103-131 runs a Size+Opacity reveal LayoutTransition on the content AND a BoundsT(Relayout) tween + ClipToBounds on the card root; the two transitions fight — the card tween chases the content's animating height and the final retarget can land below header+content, leaving a stale resting card height that clips content.
WINUI TRUTH (verify in Expander.xaml): expand-down = content Visibility=Visible at t=0 (card height SNAPS to full immediately), content TranslateY runs Discrete@0 = NegativeContentHeight then Spline->0 at 333ms KeySpline 0,0,0,1; collapse-up = TranslateY 0->NegativeContentHeight over 167ms KeySpline 1,1,0,1, Visibility=Collapsed at t=167ms; NO opacity animation anywhere; content slides UNDER the header behind a clip (ExpanderContentClip).
FIX: remove BOTH LayoutTransitions; card height snaps (it just contains header+content normally — verify nothing else constrains it); wrap content in a clip BoxEl (ClipToBounds=true) and capture the content node via OnRealized; on expand, after layout read the content height H and run AnimEngine TranslateY -H -> 0 over 333ms Easing equivalent of cubic(0,0,0,1) (the existing FluentPopOpen — verify the easing's control points in src/FluentGpu.Animation before assuming); on collapse run TranslateY 0 -> -H over 167ms with the KeySpline(1,1,0,1) equivalent (verify what easing exists; if none matches, the closest accelerate curve — note any delta in risks), keep mounted during the slide, unmount at settle (the chevron AnimEngine pattern at ~48-61 and the watcher idiom in PasswordBox.cs ~207-229 are the references). initiallyExpanded mount: seed TranslateY=0, no motion. Add open-state header corner filtering (top corners only when expanded) per Expander.xaml if missing. Keep the chevron rotation as-is unless it diverges from WinUI (verify duration/easing).
CHECKS to add (cp3.*): (a) open click: after one tick card height == header+content (snapped) while content TranslateY < 0 mid-flight; settled TranslateY==0 after 333ms of ticks; (b) close click: content still live during the first ~150ms with TranslateY heading to -H; after settle content unmounted and card height == header height; (c) resting expanded: absolute bottom of last content child <= card absolute bottom (the gallery clipping bug).`,
  },
  D4: {
    title: 'ScrollBar wrong + AnnotatedScrollBar broken layout',
    files: ['src/FluentGpu.Controls/ScrollBar.cs','src/FluentGpu.Controls/AnnotatedScrollBar.cs','src/FluentGpu.WindowsApp/RemainingPages.cs','src/FluentGpu.WindowsApp/StatusLayoutPages.cs'],
    winui: ['controls/dev/CommonStyles/ScrollBar_themeresources.xaml (+_perf2026) — ScrollBarSize 12, thumb min length 30, expand/contract 167ms KeySpline 0,0,0,1, ExpandBeginTime 400ms, ContractBeginTime 500ms, ContractDelay 2s, fade 83ms, arrow glyph 8, pressed scale 0.875','controls/dev/AnnotatedScrollBar/AnnotatedScrollBar.xaml + AnnotatedScrollBar_themeresources.xaml — FULL template: PART_LabelsGrid HorizontalAlignment=Center MinWidth 44, LabelTemplate right-aligned text Margin 0,-5,0,-2, thumb+ghost 30x3 r1.5 right-aligned VerticalAlignment=Top, ScrollButtonStyle MinWidth/MinHeight 16 FontSize 8 CornerRadius 4 TRANSPARENT background all states with foreground-only ramp, glyphs EDDB (up) / EDDC (down), tooltip rail 1px right-aligned, tooltip Placement=Top MaxWidth 360 MinHeight 40'],
    brief: `SYMPTOM: AnnotatedScrollBar gallery page (src/FluentGpu.WindowsApp/RemainingPages.cs ~47) renders as a FULL-WIDTH dark panel with labels (A,F,M,S,Z) horizontally centered — should be a narrow (~44-60px) control at the right with right-aligned thumb. ScrollBar visuals/motion also off.
ROOT CAUSE (re-confirm): AnnotatedScrollBar.cs (~69-226, rewritten in 4a9047b) uses AlignSelf=FlexAlign.End on ZStack children intending RIGHT-align, but ArrangeZStack (FlexLayout.cs ~521-538) places ZStack children at the LEFT edge and consumes AlignSelf as VERTICAL placement — elements land bottom-LEFT. CRITICAL ENGINE SEMANTIC you must respect (do NOT edit FlexLayout.cs): inside a ZStack, horizontal alignment requires wrapping each child in a full-width row BoxEl{Direction=0, Justify=FlexJustify.End} — never AlignSelf.
ScrollBar.cs (~117-287): (1) arrow button cells appear/disappear on hover expansion (buttons = expanded ? rail : 0 ~line 134) so track length and thumb geometry JUMP — WinUI always reserves the cells, buttons only FADE (83ms opacity). (2) the thumb's LayoutTransition(Bounds, Tween 167, Delay 400/500) puts the expand begin-delays on the thumb position FLIP too — scrolling moves the thumb only after 400ms. WinUI: position/length changes are INSTANT layout; ONLY the cross-axis 2px<->6px expand animates (167ms spline 0,0,0,1) after the 400/500ms hover debounce.
FIX: rebuild AnnotatedScrollBar 1:1 per the template you read (44px centered labels column; right-aligned 30x3 r1.5 thumb + ghost via Justify=End row wrappers, top-anchored via OffsetY; foreground-only transparent 16x16 ScrollButtons with EDDB/EDDC glyphs at FontSize 8, TabStop=false, repeat behavior; thin 1px right-aligned tooltip rail). Fix ScrollBar: always-reserved arrow cells with fade-only; instant thumb position; AnimEngine-driven cross-axis expand 167ms; 400/500ms as a state-flip debounce (DebounceTicker idiom) not LayoutTransition.DelayMs. Read src/FluentGpu.Animation/ScrollAnimator.cs to verify the engine overlay scrollbar 2s auto-hide constant vs WinUI ContractDelay — report (do not change blindly). Gallery pages: fix the AnnotatedScrollBar host in RemainingPages.cs so the control sits right-edge with a sensible height; check StatusLayoutPages.cs scrolling page hosts too.
CHECKS to add (cp4.*): (a) AnnotatedScrollBar at H=280: thumb absolute RIGHT edge == rail right edge, thumb Y proportional to position, label text right edge ~ labels-column right edge, root width hugs ~44; (b) hovering does NOT change thumb Y or track length; (c) position 0->0.5 while collapsed: thumb Y updates next layout frame (no 400ms lag).`,
  },
  D5: {
    title: 'Editable ComboBox: chevron is a separate box; dropdown styling off',
    files: ['src/FluentGpu.Controls/ComboBox.cs','src/FluentGpu.Controls/EditableText.cs'],
    winui: ['controls/dev/ComboBox/ComboBox_themeresources.xaml (+_perf2026) — editable template ~560-587 (single Background border spans both columns; TextBox part BorderBrush=Transparent, Style=ComboBoxTextBoxStyle ~770-813 with focused underline INSIDE the shared border), ComboBoxEditableTextPadding 11,5,38,6 ~342, DropDownOverlay 30 wide Margin 4 CornerRadius 4 transparent until hover (ComboBoxDropDownBackgroundPointerOver/Pressed ~538-554), DropDownGlyph E70D 12x12 Margin 0,0,14,0 IsHitTestVisible=False foreground TextFillColorSecondary ~582-587, MinHeight 32 ~327, PopupBorder Background ComboBoxDropDownBackground acrylic CornerRadius OverlayCornerRadius 8 Margin 0,-0.5,0,-1, ComboBoxDropdownContentMargin 0,4 ~339, ComboBoxItem margin 5,2,5,2 ~614 corner 3 ~345 padding 11,5,11,7 ~335 selection pill ComboBoxItemPill ~349'],
    brief: `SYMPTOM: editable ComboBox renders as TWO boxes — a full TextBox plus a separate rounded chevron button beside it; dropdown item styling off vs WinUI.
ROOT CAUSE (re-confirm): ComboBox.cs ~442-516 composes editable mode as outer bordered box -> [full-chrome EditableText(w-38) | separate 38px chevron BoxEl with own corners+hover]. EditableText.Render always paints its own complete TextBox chrome (fill, 1px elevation border, corners, focus underline ~290-312) — so a full TextBox renders INSIDE the combo.
FIX: (1) EditableText.cs: add an additive Chromeless mode (default false — TextBox/PasswordBox stay untouched) that suppresses resting fill/border/corners AND the control-owned focus underline while keeping caret/selection/IME/padding. NOTE: another agent (wave 1) already edited EditableText.cs for affix TabStop — read its current state first and preserve those changes. (2) ComboBox.cs editable branch: ONE outer box owns ALL chrome — ControlElevationBorder 1px, corners (keep existing open-state corner squaring), rest fill, focused state = input-active fill + 2px accent bottom underline ON THE OUTER BOX; children = chromeless EditableText spanning FULL width with Padding 11,5,38,6; DropDownOverlay button Width 30 Margin 4 corner 4 transparent->hover/pressed fills TabStop=false; 12x12 E70D glyph right margin 14, hit-test-invisible, painted above the overlay. (3) dropdown surface + rows: verify the editable path opens BottomStretch corner-joined (FlyoutPositioner ~263-286); surface = acrylic + corner 8 + shadow + presenter margin 0,4; rows = margin 5,2,5,2 corner 3 padding 11,5,11,7 + selection pill — fix deltas in the ComboBox list composition (~568+).
${SEAM_CONTRACT}
Your side: in the NON-editable dropdown open call, compute SeamOffsetY = (selected row center Y in popup) - (popup center Y) and pass it via the object initializer. Guard for no-selection (null).
CHECKS to add (cp5.*): (a) editable combo reconciled: exactly ONE bordered node in the field subtree; EditableText subtree root paints BorderWidth==0 and transparent fill at rest; (b) glyph node 12x12 with right inset 14; overlay button width 30 margin 4; clicking overlay toggles popup AND field root stays focused; (c) focused editable combo shows the 2px accent bottom bar on the OUTER box; (d) dropdown row margin 5,2,5,2 corner 3, surface corner 8.`,
  },
  D67: {
    title: 'SplitButton size/flyout + Flyout/MenuFlyout motion not 1:1',
    files: ['src/FluentGpu.Controls/OverlayHost.cs','src/FluentGpu.Controls/FlyoutPositioner.cs','src/FluentGpu.Controls/SplitButton.cs','src/FluentGpu.Controls/DropDownButton.cs','src/FluentGpu.Controls/ToggleSplitButton.cs','src/FluentGpu.Controls/MenuFlyout.cs','src/FluentGpu.WindowsApp/ControlGalleryPages.cs'],
    winui: ['controls/dev/SplitButton/SplitButton.xaml + SplitButton_themeresources.xaml — primary/secondary 35, padding 11,6,11,7, divider 1px, h32, chevron 12','controls/dev/CommonStyles/MenuFlyout_themeresources.xaml + FlyoutPresenter_themeresources.xaml (+_perf2026)','controls/dev/CommonStyles/Common_themeresources_any.xaml — timing tokens 250/167/83ms, KeySpline 0,0,0,1','dxaml/xcp/dxaml/lib/LayoutTransition_partial.cpp ~476-505 — MenuPopupThemeTransition presenter-plate ScaleY (1-closedRatio)->1 over the same 250ms, CenterY=openedLength when opening upward','dxaml/xcp/dxaml/lib/ThemeAnimations.cpp ~596-711 — SplitOpenThemeAnimation: clip origin (0,0.5), ClipTranslateY=OffsetFromCenter immediate, ClipScaleY 0.5->(0.5+|offset/length|)*2 over 250ms cubic(0,0,0,1), no fade','dxaml/xcp/dxaml/lib/MenuFlyoutSubItem_Partial.cpp ~741 (0.67 cascade ratio), MenuPopupThemeTransition_Partial.h ~24, FlyoutBase_partial.cpp ~68 (g_entranceThemeOffset=50) and ~2622-2646 (FlyoutMargin shift), SplitCloseThemeAnimation_Partial.h ~16-18'],
    brief: `TWO DEFECTS, one owner (shared OverlayHost.cs).
D6 SYMPTOM: SplitButton's MenuFlyout appears overlapping the button (should sit BELOW with 4px gap) and looks buggy mid-open; user also says button "wrong size". Template metrics in SplitButton.cs (~21-32) are already WinUI-exact — do NOT change them without pixel evidence. ROOT-CAUSE CANDIDATES to discriminate BY READING CODE (you cannot run builds): (1) the open animation starts the surface at TranslateY=-0.5*H relying on an animated ClipT riding the translate (OverlayHost.cs ~664-697) — if the animated rounded clip is not honored on that surface node, the un-clipped surface visibly covers the button for 250ms; (2) the windowed-popup branch partially engaging (~533-556) setting BOTH window bounds AND wrapper translate = double offset (note: PopupWindowsEnabled is headless-only per AppHost.cs ~213 — verify which branch runs in the real gallery); (3) first-frame placement computed with MeasuredH=0. Trace each path end-to-end; fix what the code actually shows; audit DropDownButton + ToggleSplitButton same paths. Check how the gallery hosts SplitButton (ControlGalleryPages.cs ~191) — it must hug content, not stretch.
D7 SYMPTOM: flyout open/close motion still not 1:1. Current state (OverlayHost.cs ~583-697 + SeedCloseIfNeeded ~301-378) is close: menus 250ms cubic(0,0,0,1) clip+translate ratio 0.5 root / 0.67 cascade, menu close 83ms linear fade frozen clip, plain Flyout 50px translate ~367ms cubic(0.1,0.9,0.2,1) + 83ms fade @83ms, dropdown close SplitClose 167ms. TWO VERIFIED DELTAS TO IMPLEMENT: (1) menus are missing the presenter-plate stretch — MenuPopupThemeTransition ALSO animates the presenter border ScaleY from (1-closedRatio)->1 over the same 250ms cubic(0,0,0,1) (CenterY at the anchor-near edge; =openedLength when opening upward): split the menu FlyoutSurface into a plate layer (gets ScaleY) and an items layer (keeps the translate), clip unchanged; (2) ComboBox dropdown must split-open around the SEAM (selected-row center), animating ClipT AND ClipB both ways per SplitOpen, content TranslateY 0, no fade; SplitClose mirrors (collapse toward seam).
${SEAM_CONTRACT}
Your side: add SeamOffsetY to PopupOptions (locate its defining file — if it is NOT one of your manifest files, you may edit THAT ONE defining file too; declare it in editedFiles), plumb to OverlayEntry, implement the seam-centered Dropdown open/close. Re-verify every constant you keep against the dxaml sources (the 367ms plain-flyout duration is flagged as needing re-verification).
CHECKS to add (cp6.* and cp7.*): (a) SplitButton OpenOnMount in live host: after settle, overlay wrapper position == (anchor.X, anchor.Bottom+4); mid-flight the VISIBLE (post-clip) top edge never rises above anchor.Bottom+4; (b) SplitButton extents: root h32 (+borders as engine counts), secondary 35, divider 1, primary >= 35; (c) DropDownButton identical placement; open channels = TranslateY+ClipT (menu kind), opacity stays 1; (d) menu open mid-flight: opacity==1, TranslateY<0, plate ScaleY strictly between (1-ratio) and 1; settled at 250ms; (e) dropdown with mid-list selection: mid-flight ClipT>0 AND ClipB<H (seam-centered), content TranslateY==0; (f) plain flyout: t=40ms opacity==0 (held), t=120ms 0<opacity<1, TranslateY decaying from -50; (g) menu close: opacity hits 0 within 83ms, clip frozen, entry finalized.`,
  },
}

// ───────────────────────── schemas ─────────────────────────
const FIX = {
  type: 'object', additionalProperties: false,
  required: ['defectId','rootCause','winuiEvidence','editedFiles','checkSnippet','requestedEdits','risks','summary'],
  properties: {
    defectId: { type: 'string' },
    rootCause: { type: 'string', description: 'confirmed root cause after reading code' },
    winuiEvidence: { type: 'array', items: { type: 'object', required: ['property','value','sourceFile'], properties: {
      property: { type: 'string' }, value: { type: 'string' }, sourceFile: { type: 'string' }, line: { type: 'number' } } } },
    editedFiles: { type: 'array', items: { type: 'string' } },
    checkSnippet: { type: 'object', required: ['methodName','methodSource','callSiteLine'], properties: {
      methodName: { type: 'string' }, methodSource: { type: 'string' }, callSiteLine: { type: 'string' } } },
    shotSnippet: { anyOf: [ { type: 'null' }, { type: 'object', required: ['shotId','caseSource'], properties: {
      shotId: { type: 'string' }, caseSource: { type: 'string' }, helperSource: { type: 'string' } } } ] },
    requestedEdits: { type: 'array', items: { type: 'object', required: ['file','rationale','proposedChange'], properties: {
      file: { type: 'string' }, rationale: { type: 'string' }, proposedChange: { type: 'string' } } } },
    risks: { type: 'array', items: { type: 'string' } },
    summary: { type: 'string' },
  },
}
const GATE = {
  type: 'object', additionalProperties: false,
  required: ['buildClean','allChecksPassed','green','failedDefects','smokeJudgment','committed','summary'],
  properties: {
    buildClean: { type: 'boolean' }, allChecksPassed: { type: 'boolean' },
    green: { type: 'boolean', description: 'build clean AND all checks passed' },
    failedDefects: { type: 'array', items: { type: 'object', required: ['defectId','detail'], properties: {
      defectId: { type: 'string' }, detail: { type: 'string' } } } },
    shots: { type: 'array', items: { type: 'string' } },
    smokeJudgment: { type: 'string', description: 'per-defect visual verdict from reading the PNGs' },
    committed: { anyOf: [ { type: 'string' }, { type: 'null' } ] },
    summary: { type: 'string' },
  },
}
const SHOTS = {
  type: 'object', additionalProperties: false, required: ['shots','failures'],
  properties: {
    shots: { type: 'array', items: { type: 'object', required: ['id','path'], properties: {
      id: { type: 'string' }, path: { type: 'string' }, note: { type: 'string' } } } },
    failures: { type: 'array', items: { type: 'string' } },
  },
}
const VERIFY = {
  type: 'object', additionalProperties: false, required: ['control','clean','findings','screenshotsReviewed'],
  properties: {
    control: { type: 'string' }, clean: { type: 'boolean' },
    findings: { type: 'array', items: { type: 'object', required: ['severity','property','expected','actual'], properties: {
      severity: { type: 'string', enum: ['fail','warn'] }, property: { type: 'string' },
      winuiFile: { type: 'string' }, fluentgpuFile: { type: 'string' },
      expected: { type: 'string' }, actual: { type: 'string' }, suggestedFix: { type: 'string' } } } },
    screenshotsReviewed: { type: 'array', items: { type: 'string' } },
  },
}

// ───────────────────────── prompt builders ─────────────────────────
function fixPrompt(key, extra) {
  const m = M[key]
  return `You are fixing defect ${key} — ${m.title} — in the FluentGpu engine (WinUI 1:1 parity work).
${RULES}
YOUR MANIFEST (the ONLY files you may edit):
${m.files.map(f => '- ' + REPO + '/' + f).join('\n')}
WINUI GROUNDING FILES under ${WINUI} (read FIRST, end-to-end for templates/storyboards; quote exact values in winuiEvidence BEFORE editing):
${m.winui.map(f => '- ' + f).join('\n')}
DEFECT BRIEF AND DESIGN:
${m.brief}
${CHECK_RULES}
${extra || ''}`
}

const WAVE1_SHOTS = `gallery:ListView | gallery:ItemsView | gallery:PasswordBox | gallery:Expander | expander-open (also --frames 8, 15, 25, 45 as separate PNGs) | gallery:AnnotatedScrollBar | gallery:scrolling-controls`
const WAVE2_SHOTS = `gallery:ComboBox | combobox-open | gallery:SplitButton | split-open (also --frames 8, 20, 40) | togglesplit-open | dropdown-open (also --frames 8, 20, 40) | flyout-open (also --frames 8, 20, 40) | flyout-closing --frames 45 | menubar-open`
const ALL_SHOTS = WAVE1_SHOTS + ' | ' + WAVE2_SHOTS

function integratePrompt(tag, fixResultsJson, shotList, commitMsg) {
  return `You are the INTEGRATOR for wave "${tag}" of a WinUI-parity fix batch in ${REPO} (branch feat/winui-control-parity). You are the ONLY agent allowed to run dotnet and to edit src/FluentGpu.VerticalSlice/Program.cs and src/FluentGpu.WindowsApp/ShotScene.cs. All edits via the Edit tool (PowerShell -replace/Set-Content on source is BANNED).
FIX RESULTS FROM THE WAVE AGENTS (JSON):
${fixResultsJson}
DO, IN ORDER:
1. Run "git -C ${REPO} status --porcelain" and compare changed files against the union of editedFiles. Report (do not auto-revert) any out-of-manifest surprises in your summary.
2. Apply each checkSnippet to ${REPO}/src/FluentGpu.VerticalSlice/Program.cs: read the region around "static int Main(" — insert each methodSource before it; find the LAST existing check-method call inside Main (e.g. "E11VirtChecks(strings);") and insert each callSiteLine after it, in defect order. Verify method signatures match how sibling methods are declared/called; adapt mechanically if needed.
3. Apply each non-null shotSnippet to ${REPO}/src/FluentGpu.WindowsApp/ShotScene.cs in the style of existing cases.
4. Review requestedEdits: apply the ones that are minimal and necessary (compile prerequisites, existing-check expectation updates that the new behavior legitimately requires — but NEVER weaken a check just to make it pass; an expectation update must be justified by WinUI semantics). Record every applied/rejected decision in your summary.
5. Build: "dotnet build ${REPO}/src/FluentGpu.VerticalSlice". Fix compile errors with MINIMAL edits preserving fixer intent (errors inside check methods: fix freely; errors in engine/control code: smallest possible correction, note it).
6. Run: "dotnet run --project ${REPO}/src/FluentGpu.VerticalSlice". Required output: "ALL CHECKS PASSED". If a NEW cp* check fails: decide check-bug vs behavior-bug by investigation; fix check-bugs; for behavior-bugs apply an obvious minimal fix (<10 lines) if clear, else record in failedDefects with the full check output. If a PRE-EXISTING check fails: investigate which wave edit caused it and either minimally fix or record in failedDefects. NEVER delete or weaken a check to go green. Iterate build+run until green or genuinely stuck.
7. Screenshots: ensure ${OUT}/${tag} exists; for each shot id in [${shotList}] run "dotnet run --project ${REPO}/src/FluentGpu.WindowsApp -- --screenshot ${OUT}/${tag}/<safe-name>.png --shot <id>" (append "--frames N" for the frame-sweep variants; gallery:X ids deep-link gallery pages). Read EVERY produced PNG and smoke-judge per defect: does the symptom look fixed? Gross visual breakage (empty list, missing control, flyout overlapping its anchor at settle) goes into failedDefects with detail.
8. If green (build+checks): "git -C ${REPO} add -A" then commit with message: "${commitMsg}" — end the message body with the line: Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>   (use a here-string or -m with embedded newlines carefully). Report the commit hash in committed. If NOT green: do not commit; committed=null.
Return the GateResult schema fields. green = buildClean AND allChecksPassed. Be brutally honest in smokeJudgment.`
}

function verifyPrompt(key, control, shotsJson) {
  const m = M[key === 'D6' || key === 'D7' ? 'D67' : key]
  return `ADVERSARIAL READ-ONLY VERIFIER for ${control} in ${REPO} vs the WinUI 3 source at ${WINUI}. You must NOT edit any file. Assume the fix agent MIS-MAPPED tokens and timings — your job is to refute the claim that this control is 1:1.
SCOPE: defect ${key} — ${m.title}.
IMPLEMENTATION FILES to audit: ${m.files.map(f => REPO + '/' + f).join(', ')} (plus src/FluentGpu.Dsl/Tokens.cs for token->ARGB resolution and src/FluentGpu.Animation for easing curves).
WINUI GROUND TRUTH (re-resolve YOURSELF, do not trust prior agents): ${m.winui.map(f => WINUI + '/' + f).join(' ; ')} — resolve every resource indirection chain to the concrete LIGHT+DARK theme values (full ARGB including ALPHA), every duration, every KeySpline (including zero-duration setters and Opacity=0 invisible parts), and diff the _perf2026 variants.
FRESH SCREENSHOTS (Read these PNGs and judge pixels, including mid-animation frame sweeps for timing/easing claims): ${shotsJson}
ALSO read the user's original bug screenshots in ${IMGS}/ to confirm the reported symptom is actually gone.
CHECKS: confirm the cp* checks for this defect exist in src/FluentGpu.VerticalSlice/Program.cs and genuinely assert the WinUI values (a check asserting a wrong constant is itself a finding).
Report EVERY discrepancy as a finding: severity "fail" (visibly wrong / wrong value) or "warn" (subtle delta, missing state, suspicious mapping) — warns are treated as must-fix by this project, so do not suppress them. clean=true ONLY with zero findings. Include winuiFile/fluentgpuFile and concrete expected/actual values per finding.`
}

function shotsPrompt(round) {
  return `Capture the verification screenshot set for round ${round} in ${REPO}. You may run dotnet. Ensure ${OUT}/r${round} exists. For each shot id in [${ALL_SHOTS}] run: "dotnet run --project ${REPO}/src/FluentGpu.WindowsApp -- --screenshot ${OUT}/r${round}/<safe-name>.png --shot <id>" (use --frames N for the sweep variants; encode frames in the filename, e.g. expander-open-f15.png; gallery:X ids are gallery deep-links — replace ':' with '-' in filenames). If a shot id is unknown to the app, record it in failures and continue. Do NOT edit any source file. Return every produced path.`
}

// ───────────────────────── flow ─────────────────────────
const FIXER_OF = { D1: 'D1', D2: 'D2', D3: 'D3', D4: 'D4', D5: 'D5', D6: 'D67', D7: 'D67' }
const VERIFIERS = [
  { key: 'D1', control: 'ListView + ItemsView (+ item realization)' },
  { key: 'D2', control: 'PasswordBox (reveal affix + focus semantics)' },
  { key: 'D3', control: 'Expander (motion + clipping + chrome)' },
  { key: 'D4', control: 'ScrollBar + AnnotatedScrollBar' },
  { key: 'D5', control: 'ComboBox (editable composition + dropdown styling)' },
  { key: 'D6', control: 'SplitButton + DropDownButton + ToggleSplitButton (size + flyout placement)' },
  { key: 'D7', control: 'Flyout/MenuFlyout/dropdown open-close motion' },
]
const commits = []
const unresolved = []

// Phase 0 — baseline
const baseline = await agent(
  `Baseline gate for ${REPO} (branch feat/winui-control-parity, expected clean tree at HEAD). 1) "git -C ${REPO} status --porcelain" + "git -C ${REPO} log --oneline -3" — report. 2) "dotnet build ${REPO}/src/FluentGpu.VerticalSlice" then "dotnet run --project ${REPO}/src/FluentGpu.VerticalSlice" — must print "ALL CHECKS PASSED". 3) Capture BEFORE-evidence shots to ${OUT}/before/: ids [${ALL_SHOTS} — plain ids only, skip the --frames sweep variants] via "dotnet run --project ${REPO}/src/FluentGpu.WindowsApp -- --screenshot ${OUT}/before/<safe-name>.png --shot <id>" (create the directory first; unknown ids: note and continue). Read gallery:ListView and gallery:ItemsView PNGs and confirm the empty-list symptom is visible. Do NOT edit anything, do NOT commit. green = build clean AND checks passed on HEAD.`,
  { label: 'baseline-gate', phase: 'Baseline', schema: GATE })

if (!baseline || !baseline.green) {
  return { aborted: 'Baseline gate on HEAD is not green — fix HEAD first', baseline }
}
log('Baseline green. Before-shots captured. Starting Wave 1.')

// helper: run a wave then integrate-with-repair-loop
async function integrateUntilGreen(tag, fixResults, shotList, commitMsg, maxRepairs) {
  let results = fixResults.filter(Boolean)
  let gate = await agent(integratePrompt(tag, JSON.stringify(results), shotList, commitMsg),
    { label: `integrate-${tag}`, phase: tag.startsWith('wave-1') ? 'Integrate 1' : (tag.startsWith('wave-2') ? 'Integrate 2' : 'Verify'), schema: GATE })
  let repairs = 0
  while (gate && !gate.green && gate.failedDefects.length > 0 && repairs < maxRepairs) {
    repairs++
    log(`Gate ${tag} red (${gate.failedDefects.map(f => f.defectId).join(', ')}) — repair round ${repairs}`)
    const groups = {}
    for (const f of gate.failedDefects) {
      const owner = FIXER_OF[f.defectId] || f.defectId
      groups[owner] = (groups[owner] || []).concat(f.detail)
    }
    const repaired = await parallel(Object.keys(groups).filter(k => M[k]).map(k => () =>
      agent(fixPrompt(k, `REPAIR TURN: the integration gate FAILED for your defect. Failure detail:\n${groups[k].join('\n')}\nRe-diagnose and fix. Your previous edits are already in the working tree — build on them, do not revert blindly. Return updated snippets ONLY if the check method itself must change (else return the same checkSnippet).`),
        { label: `repair-${k}-${tag}`, phase: tag.startsWith('wave-1') ? 'Integrate 1' : (tag.startsWith('wave-2') ? 'Integrate 2' : 'Verify'), schema: FIX })))
    results = results.concat(repaired.filter(Boolean))
    gate = await agent(integratePrompt(tag + `-repair${repairs}`, JSON.stringify(repaired.filter(Boolean)), shotList, commitMsg),
      { label: `integrate-${tag}-r${repairs}`, phase: tag.startsWith('wave-1') ? 'Integrate 1' : (tag.startsWith('wave-2') ? 'Integrate 2' : 'Verify'), schema: GATE })
  }
  if (gate && gate.committed) commits.push(gate.committed)
  if (gate && !gate.green) unresolved.push({ tag, failedDefects: gate.failedDefects })
  return gate
}

// Wave 1
phase('Wave 1')
const wave1 = await parallel([
  () => agent(fixPrompt('D1'), { label: 'fix-D1-lists', phase: 'Wave 1', schema: FIX }),
  () => agent(fixPrompt('D2'), { label: 'fix-D2-passwordbox', phase: 'Wave 1', schema: FIX }),
  () => agent(fixPrompt('D3'), { label: 'fix-D3-expander', phase: 'Wave 1', schema: FIX }),
  () => agent(fixPrompt('D4'), { label: 'fix-D4-scrollbars', phase: 'Wave 1', schema: FIX }),
])
const gate1 = await integrateUntilGreen('wave-1', wave1,
  WAVE1_SHOTS,
  'fix: parity wave-1 — list realization (empty ListView/ItemsView), pointer-focus rule + PasswordBox reveal, Expander WinUI motion, ScrollBar/AnnotatedScrollBar rebuild', 2)
if (!gate1 || !gate1.green) {
  return { aborted: 'Wave 1 could not reach green after repairs — stopping before Wave 2', gate1, commits, unresolved }
}
log('Wave 1 green and committed. Starting Wave 2.')

// Wave 2
phase('Wave 2')
const wave2 = await parallel([
  () => agent(fixPrompt('D5'), { label: 'fix-D5-combobox', phase: 'Wave 2', schema: FIX }),
  () => agent(fixPrompt('D67'), { label: 'fix-D67-flyouts', phase: 'Wave 2', schema: FIX }),
])
const gate2 = await integrateUntilGreen('wave-2', wave2,
  WAVE1_SHOTS + ' | ' + WAVE2_SHOTS,
  'fix: parity wave-2 — editable ComboBox unified chrome + dropdown styling, SplitButton/DropDownButton flyout placement, menu plate-stretch + seam-centered dropdown motion', 2)
if (!gate2 || !gate2.green) {
  return { aborted: 'Wave 2 could not reach green after repairs', gate1, gate2, commits, unresolved }
}
log('Wave 2 green and committed. Entering adversarial verify loop.')

// Phase 3 — adversarial verify loop, all 7 every round, until zero findings
phase('Verify')
let lastFindings = []
let dry = false
for (let round = 1; round <= 4 && !dry; round++) {
  const shots = await agent(shotsPrompt(round), { label: `shots-r${round}`, phase: 'Verify', schema: SHOTS })
  const shotsJson = JSON.stringify(shots ? shots.shots : [])
  const verdicts = (await parallel(VERIFIERS.map(v => () =>
    agent(verifyPrompt(v.key, v.control, shotsJson), { label: `verify-${v.key}-r${round}`, phase: 'Verify', schema: VERIFY })
  ))).filter(Boolean)
  const dirty = verdicts.filter(v => !v.clean)
  const total = dirty.reduce((n, v) => n + v.findings.length, 0)
  log(`Verify round ${round}: ${total} finding(s) across ${dirty.length} control(s)`)
  if (dirty.length === 0) { dry = true; lastFindings = []; break }
  lastFindings = dirty
  if (round === 4) break // out of fix rounds — report residuals
  // group findings by owning fixer
  const byFixer = {}
  for (const v of dirty) {
    const key = VERIFIERS.find(x => x.control === v.control)
    const owner = key ? FIXER_OF[key.key] : null
    if (!owner) continue
    byFixer[owner] = (byFixer[owner] || []).concat(v.findings)
  }
  const refixes = await parallel(Object.keys(byFixer).map(k => () =>
    agent(fixPrompt(k, `VERIFICATION ROUND ${round} FINDINGS — fix EVERY one (warns included; this project forbids known residuals):\n${JSON.stringify(byFixer[k], null, 1)}\nYour earlier edits are in the tree and committed; build on them. Return updated checkSnippet ONLY if checks must change (else resubmit the previous one unchanged is fine — the integrator will detect duplicates and skip re-inserting).`),
      { label: `refix-${k}-r${round}`, phase: 'Verify', schema: FIX })))
  const gateR = await integrateUntilGreen(`verify-r${round}`, refixes,
    ALL_SHOTS,
    `fix: parity verify round ${round} — resolve adversarial findings (${Object.keys(byFixer).join(', ')})`, 2)
  if (!gateR || !gateR.green) break
}

// Finalize
phase('Finalize')
const finalGate = await agent(
  `Final confirmation for ${REPO}: run "dotnet build ${REPO}/src/FluentGpu.VerticalSlice" and "dotnet run --project ${REPO}/src/FluentGpu.VerticalSlice" (must print ALL CHECKS PASSED), then "git -C ${REPO} status --porcelain" (must be clean — everything committed) and "git -C ${REPO} log --oneline -8". If the tree is dirty but green, commit it with message "fix: parity batch — final integration" ending with the line: Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>. Do not edit source. Report.`,
  { label: 'final-gate', phase: 'Finalize', schema: GATE })
if (finalGate && finalGate.committed) commits.push(finalGate.committed)

return {
  dry,
  residualFindings: lastFindings,
  commits,
  unresolved,
  baselineSummary: baseline.summary,
  wave1: gate1 && gate1.summary,
  wave2: gate2 && gate2.summary,
  final: finalGate && finalGate.summary,
}