#!/usr/bin/env python3
"""One-shot splitter: Program.cs -> Harness/ + Suites/ + Probes/ + thin Program.cs.

Preserves Main() check order via SuiteRegistry. Idempotent only on a monolithic Program.cs.
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
VS = ROOT / "src" / "FluentGpu.VerticalSlice"
SRC = VS / "Program.cs"

USINGS = """\
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Forms;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Media;
using FluentGpu.Pal;
using FluentGpu.Input;
using FluentGpu.Layout;
using FluentGpu.Pal.Headless;
using FluentGpu.Reconciler;
using FluentGpu.Controls;
using FluentGpu.Render;
using FluentGpu.Rhi;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using static FluentGpu.Dsl.Ui;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;
"""

# Suite tag -> ordered check method names (Main() order, partitioned).
SUITE_MAP: list[tuple[str, str, list[str]]] = [
    ("geolocation", "GeolocationSuite", ["GeolocationChecks"]),
    ("layout", "LayoutShellSuite", [
        "SidebarCollapseFlipChecks", "DeviceLostRecoveryChecks", "FlexChecks",
        "ShellDockChecks", "ShellResizeChecks", "DetailResizeFlickerChecks",
        "ButterSmoothResizeChecks", "ResponsiveResizeChecks",
        "CollapsedHeroRebakeChecks", "CollapsedHeroFocusChecks",
        "SidebarResizeSimChecks", "LayoutBoundaryMeasuredChecks",
        "ShellSidebarScrollChecks",
        "WrapChecks", "WrapGrowChecks", "ConstrainedWrapChecks",
        "GridChecks", "GridOverflowChecks", "GridStretchChecks",
        "ColumnAlignChecks", "AutoGridChecks", "VirtualGridChecks",
        "ZStackRepeaterChecks", "G3TokenChecks", "G3AspectChecks", "G3PrimitiveChecks",
    ]),
    ("hooks", "HooksSuite", [
        "HookChecks", "HookSurfaceChecks", "HookSubstrateChecks", "UnifyChecks",
        "ValidationChecks", "KeyedChecks", "ReuseGuardChecks",
        "PropsChannelChecks", "PropsGenChecks", "KeyboardChecks",
        "AsyncCommandChecks", "TimerHookChecks",
        "GranularityChecks", "SliderSignalChecks", "SliderUnifiedChecks",
        "FlowChecks", "FlowReorderChecks", "FlowShowRefreshChecks",
        "ForTypedChecks", "ResourceChecks",
        "PropNetClobberChecks", "PropUnionChecks", "G4dMigrationChecks",
    ]),
    ("anim", "AnimSuite", [
        "AnimChecks", "ExpressiveMotionChecks", "BlurPinKeyChecks",
        "SkeletonChecks", "ProjectionChecks", "EnterExitChecks",
        "SizeModeChecks", "ReflowChecks", "AnimRegressionChecks",
        "StyleChecks", "ButtonAxesChecks", "AnimValueChecks",
        "CompositorChecks", "CleanSpanReuseChecks", "SpanReuseScopingChecks",
        "AnimEngineChecks", "AnimHookChecks", "MarqueeChecks",
        "CrossfadeChecks", "WaveeSkeletonChecks", "BrushTransitionChecks",
        "AnimRestChecks",
    ]),
    ("scroll", "ScrollSuite", [
        "ScrollHoverChecks", "HoverSubtreeChecks",
        "ScrollChecks", "TwoAxisScrollChecks", "ScrollCrossAxisChecks",
        "ScrollOverlayChecks", "VirtualChecks", "VirtualBudgetChecks",
        "BoundItemsViewChecks", "ExtentTableChecks", "VariableChecks",
        "ZeroAllocScrollChecks", "ScrollParityChecks", "ScrollPerfWaveChecks",
        "ScrollV2ValidationChecks", "TouchpadFeelChecks",
        "E11VirtChecks", "ListConsolidationChecks",
        "D1CollectionHostSizingChecks", "Cp2ConsolidationChecks",
        "D4ScrollBarChecks",
    ]),
    ("touch", "TouchSuite", [
        "TouchGestureChecks", "TouchPhase2Checks",
        "ArenaCoreChecks", "ArenaConsumerChecks", "ArenaDeterminismChecks",
        "PinchZoomChecks", "TouchSnapOverscrollChecks",
        "Touch4SipChecks", "Touch4HoldWakeChecks",
    ]),
    ("image", "ImageSuite", [
        "IconChecks", "ImageCacheChecks", "ImageElChecks", "ImageFitChecks",
        "DecodeSchedulerChecks", "PixelBufferPoolChecks", "BlurHashChecks",
        "ImageTransitionChecks", "ImageEvictChecks", "ImageLifecycleChecks",
        "UseImageChecks",
    ]),
    ("controls", "ControlsSuite", [
        "NestedChecks", "ContextChecks", "HoverChecks",
        "MediaCardEngineChecks", "MediaPlayerElementChecks",
        "ControlsChecks", "RecipeChecks", "ControlBindChecks",
        "ControlKitIdiomChecks", "DisabledChecks", "TextRampChecks",
        "GradientRampChecks", "ClipChannelChecks", "FocusNavChecks",
        "InputVocabularyChecks", "WaveBInputChecks", "E5DragDropChecks",
        "FocusRingChecks", "Wave2ControlChecks", "RepeatButtonChecks",
        "BasicInputControlChecks", "W1ControlsChecks",
        "D2PasswordRevealFocusChecks", "ProgressIndeterminateLifecycleChecks",
        "D3ExpanderChecks", "D3ExpanderWrapReflowChecks",
        "D5EditableComboBoxChecks", "D67SplitButtonFlyoutChecks",
        "ExpanderSettingsChecks", "PipsPagerOutputChecks",
        "AutoFitTextChecks", "FontFamilyChecks", "GradientBorderChecks",
        "PolylineStrokeChecks", "ContextMenuChecks",
    ]),
    ("nav", "NavSuite", [
        "NavigationChecks", "PageHostChecks", "KeepAliveChecks",
        "NavRouterChecks", "GalleryChecks", "ActivationLifecycleChecks",
        "NavigationViewChecks", "NavigationViewAnimationChecks",
        "NavHierarchyChecks",
    ]),
    ("overlay", "OverlaySuite", [
        "TextServicesSeamChecks", "EditableTextCoreChecks",
        "TextConsumerControlChecks", "PlacementChecks",
        "OverlayFocusRestoreChecks", "TextInputChecks",
        "OverlayChecks", "OverlayAnimationChecks",
        "E4PopupWindowingChecks", "G5fPopupToastChecks",
        "FlyoutAcrylicChecks", "AcrylicBackdropMathChecks",
        "ContentDialogChromeChecks", "TeachingTipPlacementChecks",
        "MenuFlyoutStyleChecks", "SplitButtonStyleChecks",
    ]),
    ("text", "TextSuite", [
        "WaveCTextPipelineChecks", "WaveCSpanTextChecks",
    ]),
    ("diagnostics", "DiagnosticsSuite", [
        "DiagnosticsLeakGateChecks", "PaletteContrastChecks",
        "LocalizationKitChecks",
    ]),
]

# Shared helpers / fields that always go to Harness (by method/field name prefix match).
HARNESS_NAMES = {
    "s_failures", "s_total", "NumCtx",
    "s_arenaDeterminism", "s_arenaIntegratorSweep", "s_arenaFastpath",
    "s_arenaAllocZero", "s_arenaDispatchAllocZero",
    "Check", "Child", "HasGlyph", "GlyphColor", "CountGlyph",
    "FirstGradientC0", "ColorClose", "Near",
    "FindRole", "TextVisualNode", "CenterOf", "MaxAbsTrackX",
    "FindScrollable", "FindFillNode", "FocusedNode", "CollectRole", "Roles",
    "FindTextNode", "ChildHasAcrylic", "FindPolylineStrokeNode",
    "DrawPayloadSize", "FindFillCommand", "FindFillCommandNear",
    "ClickNode", "s_touchClockMs", "Touch", "Lerp",
    "TouchGesture", "TouchFlick", "PinchGesture",
    "AnyHovered", "AnyPressed", "ViewportWithItemCount", "LayoutTree",
    "ShellTitleH", "ShellToolbarH", "ShellPlayerH",
    "ShellSidebarRows", "ShellRowH", "ShellSidebarW",
    "ShellColumnTree", "PlainViewport", "ShellLayout",
    "PumpEffects", "CountText", "CountNodes",
    "FindScrollNode", "BoundSlotCount", "CollectScrollNodes",
    "PumpUntil", "DirectChildCount", "CountFill", "ColorApprox",
    "CountOccurrences", "ReadRepoFile", "ArenaSummarySuffix",
}

PROBE_DRIVERS = {
    "RangedTooltipFreezeProbe", "TitleBarResizeProbe", "ScrollFlickerProbe",
}


def member_name(sig: str) -> str:
    # "void Foo(" / "int s_total;" / "readonly Context..." / "const float ShellTitleH"
    sig = sig.strip()
    if sig.startswith("const "):
        # const float ShellTitleH = ...
        m = re.search(r"\b([A-Za-z_][A-Za-z0-9_]*)\s*=", sig)
        return m.group(1) if m else sig
    # field: type name;
    if "(" not in sig.split("//")[0] and ";" in sig:
        m = re.search(r"\b([A-Za-z_][A-Za-z0-9_]*)\s*[;=]", sig)
        # skip type keywords
        parts = sig.replace("public ", "").replace("static ", "").replace("readonly ", "").split()
        if len(parts) >= 2:
            return parts[1].rstrip(";=,")
    m = re.search(r"\b([A-Za-z_][A-Za-z0-9_]*)\s*\(", sig)
    if m:
        return m.group(1)
    m = re.search(r"\b([A-Za-z_][A-Za-z0-9_]*)\b", sig)
    return m.group(1) if m else sig[:40]


def extract_members(lines: list[str], class_start: int) -> list[tuple[str, str, int, int]]:
    """Return list of (kind, signature, start_line, end_line_exclusive) for indent-4 members."""
    members: list[tuple[str, str, int, int]] = []
    i = class_start + 1  # after 'static class Slice'
    # skip opening '{'
    while i < len(lines) and lines[i].strip() != "{":
        i += 1
    i += 1
    n = len(lines)
    while i < n:
        line = lines[i]
        if line.startswith("}") and line.strip() == "}":
            break
        if not line.startswith("    ") or line.startswith("        ") or line.strip() == "":
            i += 1
            continue
        if line.strip().startswith("//"):
            i += 1
            continue
        # member start at indent 4
        sig = line.strip()
        start = i
        # single-line field/const/property
        if sig.endswith(";") and "(" not in sig.split("//")[0]:
            members.append(("field", sig, start, start + 1))
            i += 1
            continue
        if sig.startswith("const ") and "=" in sig and sig.endswith(";"):
            members.append(("field", sig, start, start + 1))
            i += 1
            continue
        # multi-line const float a = 1, b = 2;
        if sig.startswith("const ") and not sig.endswith(";"):
            j = i + 1
            while j < n and not lines[j].rstrip().endswith(";"):
                j += 1
            j += 1
            members.append(("field", sig, start, j))
            i = j
            continue
        # enum / class / method — brace match
        kind = "type" if re.match(r"(sealed |partial )?(class|enum|struct|record)\b", sig) else "method"
        # find body start
        j = i
        depth = 0
        seen_brace = False
        # expression-bodied: => ...;
        joined = lines[i]
        if "=>" in lines[i] and lines[i].rstrip().endswith(";"):
            members.append((kind, sig, start, start + 1))
            i += 1
            continue
        # maybe signature spans lines until { or =>
        while j < n:
            for ch in lines[j]:
                if ch == "{":
                    depth += 1
                    seen_brace = True
                elif ch == "}":
                    depth -= 1
            if seen_brace and depth == 0:
                j += 1
                break
            # expression-bodied multi-line ending with ;
            if not seen_brace and "=>" in lines[j] and lines[j].rstrip().endswith(";"):
                j += 1
                break
            j += 1
        members.append((kind, sig, start, j))
        i = j
    return members


def check_to_suite() -> dict[str, tuple[str, str]]:
    m: dict[str, tuple[str, str]] = {}
    for tag, cls, checks in SUITE_MAP:
        for c in checks:
            m[c] = (tag, cls)
    return m


def assign_members(members: list[tuple[str, str, int, int]]) -> dict[str, list[tuple[str, str, int, int]]]:
    """Bucket members into 'harness' or suite class name."""
    cmap = check_to_suite()
    # Build forward map: for each member index, next Checks name
    names = [member_name(sig) for _, sig, _, _ in members]
    next_check: list[str | None] = [None] * len(members)
    pending: str | None = None
    for i in range(len(members) - 1, -1, -1):
        n = names[i]
        if n.endswith("Checks") or n in ("Main",) or n.endswith("Probe") and n[0].isupper() and "(" in members[i][1]:
            if n.endswith("Checks"):
                pending = n
        next_check[i] = pending

    buckets: dict[str, list] = {"harness": [], "core_main": [], "probes_extra": []}
    for tag, cls, _ in SUITE_MAP:
        buckets[cls] = []

    for idx, mem in enumerate(members):
        kind, sig, a, b = mem
        name = names[idx]
        if name == "Main":
            buckets["core_main"].append(mem)
            continue
        if name in PROBE_DRIVERS:
            buckets["core_main"].append(mem)
            continue
        if name in HARNESS_NAMES or name.startswith("s_arena"):
            buckets["harness"].append(mem)
            continue
        if name.endswith("Checks"):
            if name not in cmap:
                # Defined but not wired into Main — keep compilable, do not auto-run.
                print(f"  note: parking unwired check {name} in DiagnosticsSuite")
                buckets["DiagnosticsSuite"].append(mem)
                continue
            buckets[cmap[name][1]].append(mem)
            continue
        # nested types / local helpers: attach to next Checks' suite
        nc = next_check[idx]
        if nc and nc in cmap:
            buckets[cmap[nc][1]].append(mem)
        elif name.endswith("Probe") or kind == "type":
            buckets["probes_extra"].append(mem)
        else:
            # orphan helper -> harness
            buckets["harness"].append(mem)
    return buckets


def write(path: Path, content: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(content, encoding="utf-8", newline="\n")
    print(f"  wrote {path.relative_to(ROOT)} ({content.count(chr(10))} lines)")


def main() -> int:
    text = SRC.read_text(encoding="utf-8")
    lines = text.splitlines()
    slice_i = next(i for i, l in enumerate(lines) if l.startswith("static class Slice"))
    probe_lines = lines[:slice_i]
    members = extract_members(lines, slice_i)
    print(f"Slice members: {len(members)}")
    buckets = assign_members(members)

    # ── Harness/Gate.cs (hand-shaped — counters + Check + arena summary) ──
    assert_parts = []
    gate_skip = {
        "Check", "NumCtx", "ArenaSummarySuffix", "s_failures", "s_total", "Failures", "Total",
        "s_arenaDeterminism",
    }
    for kind, sig, a, b in buckets["harness"]:
        name = member_name(sig)
        if name in gate_skip or name.startswith("s_arena"):
            continue
        body = "\n".join(lines[a:b])
        if body.lstrip().startswith("static "):
            body = body.replace("static ", "public static ", 1)
        elif body.lstrip().startswith("const "):
            body = body.replace("const ", "public const ", 1)
        assert_parts.append(body)

    write(VS / "Harness" / "Gate.cs", USINGS + """

namespace FluentGpu.VerticalSlice.Harness;

/// <summary>Shared check counter + arena summary for the headless golden harness.</summary>
public static class Gate
{
    public static int Failures;
    public static int Total;
    public static readonly Context<int> NumCtx = new(0);

    // Phase-3 (GestureArena) headline-gate evidence for the final summary line.
    public static string? s_arenaDeterminism, s_arenaIntegratorSweep, s_arenaFastpath, s_arenaAllocZero, s_arenaDispatchAllocZero;

    public static void Check(string name, bool ok, string? detail = null)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail is null ? "" : $"  ({detail})")}");
        Total++;
        if (!ok) Failures++;
    }

    public static string ArenaSummarySuffix()
    {
        if (s_arenaDeterminism is null && s_arenaIntegratorSweep is null && s_arenaFastpath is null && s_arenaAllocZero is null && s_arenaDispatchAllocZero is null)
            return "";
        return $" ({Total} checks; gate.arena.determinism PASS [{s_arenaDeterminism}], "
             + $"gate.arena.determinism.integrator-sweep PASS [{s_arenaIntegratorSweep}], "
             + $"gate.arena.fastpath-sync PASS [{s_arenaFastpath}], "
             + $"gate.arena.alloc-zero PASS [{s_arenaAllocZero}], "
             + $"gate.arena.dispatch-alloc-zero PASS [{s_arenaDispatchAllocZero}].)";
    }
}
""")

    write(VS / "Harness" / "Asserts.cs", f"""\
{USINGS}

namespace FluentGpu.VerticalSlice.Harness;

/// <summary>Scene / draw-list / input helpers shared by suite modules.</summary>
public static class Asserts
{{
{chr(10).join(assert_parts)}
}}
""")

    write(VS / "Harness" / "HeadlessFixture.cs", f"""\
{USINGS}

namespace FluentGpu.VerticalSlice.Harness;

/// <summary>Using-friendly headless AppHost bootstrap for suite checks.</summary>
public sealed class HeadlessFixture : IDisposable
{{
    public HeadlessPlatformApp App {{ get; }}
    public HeadlessWindow Window {{ get; }}
    public HeadlessGpuDevice Device {{ get; }}
    public HeadlessFontSystem Fonts {{ get; }}
    public StringTable Strings {{ get; }}
    public AppHost Host {{ get; }}

    public HeadlessFixture(StringTable strings, Component root, string title = "FluentGpu slice", float w = 480, float h = 320)
    {{
        Strings = strings;
        App = new HeadlessPlatformApp();
        Window = new HeadlessWindow(new WindowDesc(title, new Size2(w, h), 1f));
        Window.Show();
        Device = new HeadlessGpuDevice();
        Fonts = new HeadlessFontSystem(strings);
        Host = new AppHost(App, Window, Device, Fonts, strings, root);
    }}

    public void Dispose()
    {{
        Host.Dispose();
        App.Dispose();
    }}
}}
""")

    # ── Probes ──
    # Strip trailing blank lines; rewrite Slice.NumCtx -> Gate.NumCtx
    probe_text = "\n".join(probe_lines).rstrip() + "\n"
    probe_text = probe_text.replace("Slice.NumCtx", "Gate.NumCtx")
    # Remove original usings from probe blob — we'll add ours
    probe_body_lines = []
    skipping_usings = True
    for l in probe_text.splitlines():
        if skipping_usings:
            if l.startswith("using ") or l.strip() == "":
                continue
            skipping_usings = False
        probe_body_lines.append(l)
    # Also dump Slice-nested probes_extra
    extra = []
    for kind, sig, a, b in buckets["probes_extra"]:
        extra.append("\n".join(lines[a:b]))
    extra_txt = "\n\n".join(extra)
    if extra_txt:
        extra_txt = "\n\n// ── Moved from Slice nested types ──\n" + extra_txt

    write(VS / "Probes" / "Probes.cs", f"""\
{USINGS}

// Probe components + harness fixtures used by suite modules.
{chr(10).join(probe_body_lines)}
{extra_txt}
""")

    # ── Suites ──
    for tag, cls, checks in SUITE_MAP:
        parts = []
        for kind, sig, a, b in buckets[cls]:
            body = "\n".join(lines[a:b])
            # methods become private static inside suite; types stay as-is but nested? 
            # Keep as file-local top-level by emitting outside class for types
            parts.append((kind, body))
        type_parts = [b for k, b in parts if k == "type"]
        method_parts = [b for k, b in parts if k != "type"]
        # Make check methods private static (already static)
        # Build Run()
        run_calls = []
        for c in checks:
            if c.endswith("Checks"):
                # detect signature: with or without strings
                # search in method_parts
                has_strings = any(f"void {c}(StringTable" in p or f"void {c}(StringTable strings)" in p for p in method_parts)
                # also void Foo()
                no_arg = any(re.search(rf"void {c}\(\)", p) for p in method_parts)
                if no_arg and not has_strings:
                    run_calls.append(f"        {c}();")
                else:
                    run_calls.append(f"        {c}(strings);")

        types_block = "\n\n".join(type_parts)
        methods_block = "\n\n".join(method_parts)
        # Replace Slice.NumCtx if any
        methods_block = methods_block.replace("Slice.NumCtx", "Gate.NumCtx")
        types_block = types_block.replace("Slice.NumCtx", "Gate.NumCtx")
        methods_block = methods_block.replace("s_failures", "Failures").replace("s_total", "Total")
        # Arena fields live on Gate; using static Gate imports them.

        write(VS / "Suites" / f"{cls}.cs", f"""\
{USINGS}

{types_block}

static class {cls}
{{
    public static void Run(StringTable strings)
    {{
{chr(10).join(run_calls)}
    }}

{methods_block}
}}
""")

    # ── CoreSuite from Main body (checks 1-9) + FG_PROBE drivers ──
    # Extract Main and probe drivers from core_main bucket
    main_mem = next((m for m in buckets["core_main"] if member_name(m[1]) == "Main"), None)
    if not main_mem:
        raise SystemExit("Main not found")
    _, _, ma, mb = main_mem
    main_body = "\n".join(lines[ma:mb])

    # FG_PROBE methods
    probe_drivers = []
    for kind, sig, a, b in buckets["core_main"]:
        n = member_name(sig)
        if n != "Main":
            probe_drivers.append("\n".join(lines[a:b]))

    # Build SuiteRegistry
    registry_entries = []
    for tag, cls, _ in SUITE_MAP:
        registry_entries.append(f'        new("{tag}", "{tag}", {cls}.Run),')

    write(VS / "Harness" / "SuiteRegistry.cs", f"""\
{USINGS}

namespace FluentGpu.VerticalSlice.Harness;

public readonly record struct SuiteEntry(string Id, string Tag, Action<StringTable> Run);

/// <summary>Explicit ordered suite registry — no reflection (AOT-safe).</summary>
public static class SuiteRegistry
{{
    public static readonly SuiteEntry[] All =
    [
{chr(10).join(registry_entries)}
    ];

    public static IEnumerable<SuiteEntry> Filter(string? suiteSpec)
    {{
        if (string.IsNullOrWhiteSpace(suiteSpec) || suiteSpec.Equals("all", StringComparison.OrdinalIgnoreCase))
            return All;

        var tags = suiteSpec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var set = new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase);
        if (set.Contains("all")) return All;

        var known = new HashSet<string>(All.Select(e => e.Tag), StringComparer.OrdinalIgnoreCase);
        known.Add("core");
        known.Add("all");
        foreach (var tag in set)
        {{
            if (!known.Contains(tag))
                throw new ArgumentException("Unknown suite '" + tag + "'. Known: " + KnownSuitesHelp());
        }}

        // core is handled by Program (checks 1-9); filter returns matching registry entries only.
        return All.Where(e => set.Contains(e.Tag));
    }}

    public static string KnownSuitesHelp() =>
        "core, all, " + string.Join(", ", All.Select(e => e.Tag).Distinct());
}}
""")

    # Thin Program.cs
    write(VS / "Program.cs", f"""\
{USINGS}
using FluentGpu.VerticalSlice.Harness;

// FG_PROBE drivers (isolated repros — run INSTEAD of the check suite).
{chr(10).join(probe_drivers) if probe_drivers else ""}

static class Program
{{
    static int Main(string[] args)
    {{
        // FG_PROBE — isolated repro drivers (run INSTEAD of suite)
        var probe = Environment.GetEnvironmentVariable("FG_PROBE");
        if (probe == "ranged-tooltip")
            return RangedTooltipFreezeProbe();
        if (probe == "titlebar-resize")
            return TitleBarResizeProbe();
        if (probe == "scroll-flicker")
            return ScrollFlickerProbe();

        // Suite filter: --suite X wins over FG_SUITE=X
        string? suiteSpec = null;
        for (int i = 0; i < args.Length; i++)
        {{
            if (args[i] == "--suite" && i + 1 < args.Length) {{ suiteSpec = args[i + 1]; break; }}
            if (args[i].StartsWith("--suite=", StringComparison.Ordinal)) {{ suiteSpec = args[i]["--suite=".Length..]; break; }}
        }}
        suiteSpec ??= Environment.GetEnvironmentVariable("FG_SUITE");

        bool runCore = true;
        IEnumerable<SuiteEntry> suites;
        try
        {{
            if (!string.IsNullOrWhiteSpace(suiteSpec)
                && !suiteSpec.Equals("all", StringComparison.OrdinalIgnoreCase))
            {{
                var tags = suiteSpec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var set = new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase);
                runCore = set.Contains("core") || set.Count == 0;
                // If only non-core tags, skip core; if "core" alone, skip registry suites.
                if (set.Contains("core") && set.Count == 1)
                    suites = Array.Empty<SuiteEntry>();
                else
                {{
                    runCore = set.Contains("core") || !set.Overlaps(SuiteRegistry.All.Select(e => e.Tag));
                    // Standard: core runs only when requested or when running all.
                    // For tagged suites without "core", skip checks 1-9.
                    runCore = set.Contains("core");
                    suites = SuiteRegistry.Filter(suiteSpec).ToArray();
                    // Filter throws on unknown; if suite is only "core", Filter returns empty (no tags match).
                    if (set.Contains("core") && set.Count == 1)
                        suites = Array.Empty<SuiteEntry>();
                    else if (!set.Contains("core"))
                        runCore = false;
                }}
            }}
            else
            {{
                suites = SuiteRegistry.All;
                runCore = true;
            }}
        }}
        catch (ArgumentException ex)
        {{
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("Known suites: " + SuiteRegistry.KnownSuitesHelp());
            return 2;
        }}

        Console.WriteLine("FluentGpu — minimum vertical slice (headless RHI/PAL/Text)\\n");

        var strings = new StringTable();

        if (runCore)
            CoreSuite.Run(strings);

        foreach (var s in suites)
            s.Run(strings);

        Console.WriteLine();
        bool filtered = !string.IsNullOrWhiteSpace(suiteSpec)
            && !suiteSpec.Equals("all", StringComparison.OrdinalIgnoreCase);
        if (Gate.Failures == 0)
        {{
            if (filtered)
                Console.WriteLine($"ALL CHECKS PASSED (suite={{suiteSpec}}, {{Gate.Total}} checks)");
            else
                Console.WriteLine($"ALL CHECKS PASSED — the vertical slice exercises every seam end-to-end.{{Gate.ArenaSummarySuffix()}}");
            return 0;
        }}
        Console.WriteLine($"{{Gate.Failures}} CHECK(S) FAILED.");
        return 1;
    }}
}}
""")

    # CoreSuite — extract checks 1-9 from old Main
    # Parse old Main for the Counter fixture block
    old_main = main_body
    # Find from "Console.WriteLine(\"FluentGpu" through Localization... no, through check 9 and before GeolocationChecks
    # We'll rewrite CoreSuite manually from the known block.

    # Extract between Check 1 setup and before GeolocationChecks call
    m = re.search(
        r'Console\.WriteLine\("FluentGpu.*?GeolocationChecks\(\);',
        old_main,
        re.S,
    )
    if not m:
        # fallback: build from known structure in lines
        core_src = extract_core_from_main(lines, ma, mb)
    else:
        block = m.group(0)
        # strip trailing GeolocationChecks();
        block = block[: block.rfind("GeolocationChecks")].rstrip()
        # strip leading Console.WriteLine
        idx = block.find("var strings")
        if idx < 0:
            idx = block.find("using var app")
        core_body = block[idx:] if idx >= 0 else block
        # CoreSuite provides its own strings param
        core_body = re.sub(r"var strings = new StringTable\(\);\s*", "", core_body, count=1)
        core_src = core_body

    write(VS / "Suites" / "CoreSuite.cs", f"""\
{USINGS}

/// <summary>Minimum vertical slice acceptance (checks 1–9).</summary>
static class CoreSuite
{{
    public static void Run(StringTable strings)
    {{
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("FluentGpu slice", new Size2(480, 320), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var root = new Counter();
        using var host = new AppHost(app, window, device, fonts, strings, root);

        // Frame 1 — mount: window→clear→two button rects (SDF) + three text runs, flex-laid-out.
        var f1 = host.RunFrame();
        Check("1. window + GPU clear + present", device.FrameCount == 1, $"backend={{device.BackendName}}, clear=#{{device.LastClear.R:0.0}},{{device.LastClear.G:0.0}},{{device.LastClear.B:0.0}}");
        Check("2. rounded-rect primitives (2 accent buttons × fill + gradient elevation border)", device.LastRects.Count == 2 && device.LastGradientStrokes.Count == 2, $"rects={{device.LastRects.Count}} gradBorders={{device.LastGradientStrokes.Count}}");
        Check("3. text runs (heading + 2 labels)", device.LastGlyphs.Count == 3, $"glyphs={{device.LastGlyphs.Count}}");
        Check("4. flex layout produced bounds", host.Scene.AbsoluteRect(host.Scene.Root).W > 0, $"rootW={{host.Scene.AbsoluteRect(host.Scene.Root).W:0.#}}");
        Check("5. reconciler + UseState (initial render)", f1.Rendered && HasGlyph(device, strings, "Count: 0"));

        // Locate the "+" button (VStack → [Heading, HStack] → HStack.[ '-', '+' ]) and click its center.
        var hstack = Child(host.Scene, host.Scene.Root, 1);
        var plus = Child(host.Scene, hstack, 1);
        var r = host.Scene.AbsoluteRect(plus);
        var center = new Point2(r.X + r.W / 2f, r.Y + r.H / 2f);
        window.QueueInput(new InputEvent(InputKind.PointerDown, center, 0, 0));
        window.QueueInput(new InputEvent(InputKind.PointerUp, center, 0, 0));

        // Frame 2 — the click→setState→repaint round-trip.
        var f2 = host.RunFrame();
        Check("6. clickable Button → OnClick fired", f2.ClicksHandled == 1, $"hit @ ({{center.X:0.#}},{{center.Y:0.#}})");
        Check("7. setState re-rendered the label", f2.Rendered && HasGlyph(device, strings, "Count: 1"), "Count: 0 → Count: 1");

        // Warm, then assert the steady paint half (phases 6–13) is zero managed allocation.
        for (int i = 0; i < 6; i++) host.RunFrame();
        var steady = host.RunFrame();
        Check("8. steady frame does no work (memoized)", !steady.Rendered);
        Check("9. ZERO managed alloc on the paint half (phases 6–11)", steady.HotPhaseAllocBytes == 0, $"{{steady.HotPhaseAllocBytes}} bytes");
    }}
}}
""")

    # Fix Program.cs suite filter logic — rewrite more cleanly
    write(VS / "Program.cs", f"""\
{USINGS}
using FluentGpu.VerticalSlice.Harness;

{chr(10).join(probe_drivers) if probe_drivers else "// (no FG_PROBE drivers found in Slice)"}

static class Program
{{
    static int Main(string[] args)
    {{
        var probe = Environment.GetEnvironmentVariable("FG_PROBE");
        if (probe == "ranged-tooltip") return RangedTooltipFreezeProbe();
        if (probe == "titlebar-resize") return TitleBarResizeProbe();
        if (probe == "scroll-flicker") return ScrollFlickerProbe();

        string? suiteSpec = null;
        for (int i = 0; i < args.Length; i++)
        {{
            if (args[i] == "--suite" && i + 1 < args.Length) {{ suiteSpec = args[i + 1]; break; }}
            if (args[i].StartsWith("--suite=", StringComparison.Ordinal))
            {{ suiteSpec = args[i]["--suite=".Length..]; break; }}
        }}
        suiteSpec ??= Environment.GetEnvironmentVariable("FG_SUITE");

        bool fullRun = string.IsNullOrWhiteSpace(suiteSpec)
            || suiteSpec.Equals("all", StringComparison.OrdinalIgnoreCase);
        bool runCore;
        SuiteEntry[] suites;
        try
        {{
            if (fullRun)
            {{
                runCore = true;
                suites = SuiteRegistry.All;
            }}
            else
            {{
                var tags = new HashSet<string>(
                    suiteSpec!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    StringComparer.OrdinalIgnoreCase);
                runCore = tags.Contains("core");
                // Validate unknown tags (core/all are pseudo-tags).
                var known = new HashSet<string>(SuiteRegistry.All.Select(e => e.Tag), StringComparer.OrdinalIgnoreCase);
                known.Add("core");
                known.Add("all");
                foreach (var t in tags)
                {{
                    if (!known.Contains(t))
                        throw new ArgumentException($"Unknown suite '{{t}}'.");
                }}
                suites = SuiteRegistry.All.Where(e => tags.Contains(e.Tag)).ToArray();
            }}
        }}
        catch (ArgumentException ex)
        {{
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("Known suites: " + SuiteRegistry.KnownSuitesHelp());
            return 2;
        }}

        Console.WriteLine("FluentGpu — minimum vertical slice (headless RHI/PAL/Text)\\n");
        var strings = new StringTable();

        if (runCore)
            CoreSuite.Run(strings);
        foreach (var s in suites)
            s.Run(strings);

        Console.WriteLine();
        if (Gate.Failures == 0)
        {{
            if (!fullRun)
                Console.WriteLine($"ALL CHECKS PASSED (suite={{suiteSpec}}, {{Gate.Total}} checks)");
            else
                Console.WriteLine($"ALL CHECKS PASSED — the vertical slice exercises every seam end-to-end.{{Gate.ArenaSummarySuffix()}}");
            return 0;
        }}
        Console.WriteLine($"{{Gate.Failures}} CHECK(S) FAILED.");
        return 1;
    }}
}}
""")

    # Verify all checks mapped
    all_checks = set()
    for _, _, cs in SUITE_MAP:
        all_checks.update(cs)
    print(f"Mapped checks: {len(all_checks)}")
    print("Done.")
    return 0


def extract_core_from_main(lines, ma, mb):
    return ""


if __name__ == "__main__":
    sys.exit(main())
