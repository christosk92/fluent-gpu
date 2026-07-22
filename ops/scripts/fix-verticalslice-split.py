#!/usr/bin/env python3
"""Post-split fixups: move cross-suite helpers, fix ProbeDrivers visibility."""
from __future__ import annotations

import re
from collections import defaultdict
from pathlib import Path

VS = Path(__file__).resolve().parents[2] / "src" / "FluentGpu.VerticalSlice"


def extract_method_from_text(text: str, name: str):
    lines = text.splitlines()
    start = None
    for i, l in enumerate(lines):
        if re.search(rf"\b{re.escape(name)}\s*\(", l) and (
            "static" in l or (i > 0 and "static" in lines[i - 1])
        ):
            if l.startswith("    static") or l.startswith("    public static") or l.startswith(
                "    private static"
            ):
                start = i
                break
            if l.strip().startswith("static") or " static " in l:
                start = i
                break
    if start is None:
        for i, l in enumerate(lines):
            if re.search(rf"\b{re.escape(name)}\s*\(", l):
                start = i
                while start > 0 and "static" not in lines[start]:
                    start -= 1
                break
    if start is None:
        raise SystemExit(f"cannot find {name}")
    depth = 0
    seen = False
    j = start
    if "=>" in lines[start] and lines[start].rstrip().endswith(";"):
        return "\n".join(lines[start : start + 1]), start, start + 1
    while j < len(lines):
        for ch in lines[j]:
            if ch == "{":
                depth += 1
                seen = True
            elif ch == "}":
                depth -= 1
        j += 1
        if seen and depth == 0:
            break
        if not seen and lines[j - 1].rstrip().endswith(";") and "=>" in "".join(lines[start:j]):
            break
    return "\n".join(lines[start:j]), start, j


def remove_span(path: Path, start: int, end: int) -> None:
    lines = path.read_text(encoding="utf-8").splitlines()
    del lines[start:end]
    path.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")


def make_public(body: str) -> str:
    body = body.replace("    static ", "    public static ", 1)
    body = body.replace("    private static ", "    public static ", 1)
    return body


def insert_before_last_brace(path: Path, body: str) -> None:
    lines = path.read_text(encoding="utf-8").splitlines()
    closes = [i for i, l in enumerate(lines) if l.strip() == "}"]
    lines.insert(closes[-1], body + "\n")
    path.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")


def main() -> int:
    pd = VS / "Probes" / "ProbeDrivers.cs"
    t = pd.read_text(encoding="utf-8")
    t = t.replace("static int RangedTooltip", "public static int RangedTooltip")
    t = t.replace("static int TitleBarResize", "public static int TitleBarResize")
    t = t.replace("static int ScrollFlicker", "public static int ScrollFlicker")
    pd.write_text(t, encoding="utf-8", newline="\n")
    print("ProbeDrivers -> public")

    moves = [
        (VS / "Suites" / "LayoutShellSuite.cs", "FindFillCommand"),
        (VS / "Suites" / "LayoutShellSuite.cs", "FindFillCommandNear"),
        (VS / "Suites" / "AnimSuite.cs", "ActiveStrokeTrimEnd"),
    ]
    asserts = VS / "Harness" / "Asserts.cs"
    by_file: dict[Path, list] = defaultdict(list)
    insert_parts: list[str] = []
    for path, name in moves:
        body, s, e = extract_method_from_text(path.read_text(encoding="utf-8"), name)
        by_file[path].append((s, e, name, body))
        print(f"  extract {name} from {path.name} [{s}:{e}]")

    for path, items in by_file.items():
        items.sort(key=lambda x: -x[0])
        lines = path.read_text(encoding="utf-8").splitlines()
        for s, e, name, body in items:
            insert_parts.append(make_public(body))
            del lines[s:e]
        path.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")

    a_lines = asserts.read_text(encoding="utf-8").splitlines()
    closes = [i for i, l in enumerate(a_lines) if l.strip() == "}"]
    class_close = closes[-2]
    a_lines.insert(class_close, "\n\n".join(insert_parts))
    asserts.write_text("\n".join(a_lines) + "\n", encoding="utf-8", newline="\n")
    print("Asserts updated")

    body, s, e = extract_method_from_text(
        (VS / "Suites" / "AnimSuite.cs").read_text(encoding="utf-8"), "ScrollHoverVirtualCheck"
    )
    remove_span(VS / "Suites" / "AnimSuite.cs", s, e)
    insert_before_last_brace(VS / "Suites" / "ScrollSuite.cs", body)
    print("moved ScrollHoverVirtualCheck")

    for name in ["CondenseWinTrace", "DriveArenaScript", "FlingResolutionTrace"]:
        body, s, e = extract_method_from_text(
            (VS / "Suites" / "ImageSuite.cs").read_text(encoding="utf-8"), name
        )
        remove_span(VS / "Suites" / "ImageSuite.cs", s, e)
        insert_before_last_brace(VS / "Suites" / "TouchSuite.cs", body)
        print("moved", name, "-> TouchSuite")

    for name in ["G5fToastChecks", "KawaseChainChecks"]:
        body, s, e = extract_method_from_text(
            (VS / "Suites" / "DiagnosticsSuite.cs").read_text(encoding="utf-8"), name
        )
        remove_span(VS / "Suites" / "DiagnosticsSuite.cs", s, e)
        insert_before_last_brace(VS / "Suites" / "OverlaySuite.cs", body)
        print("moved", name, "-> OverlaySuite")

    probes = VS / "Probes" / "Probes.cs"
    pt = probes.read_text(encoding="utf-8")
    if "using FluentGpu.VerticalSlice.Harness;" not in pt:
        pt = pt.replace(
            "using static FluentGpu.VerticalSlice.Harness.Asserts;",
            "using static FluentGpu.VerticalSlice.Harness.Asserts;\nusing FluentGpu.VerticalSlice.Harness;",
        )
    pt = pt.replace("Slice.NumCtx", "Gate.NumCtx")
    probes.write_text(pt, encoding="utf-8", newline="\n")
    print("Probes Gate fix")
    print("done")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
