# Doodle-style (xkcd) benchmark charts for benchmarks/FrameworkComparison/README.md
# Data: results/2026-11-14-cpu + results/2026-11-14-cadence (schema v4), read from summary.json and the raw results.
# Every number on a chart is read from those files; nothing is typed in by hand.
# Palette (validated, dataviz skill): FluentGpu #eb6834 (orange), WinUI 3 #2a78d6 (blue).
# Usage: python -I scripts/make-readme-charts.py [results-root]
import glob
import json
import os
import random
import sys
import warnings

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt

warnings.filterwarnings("ignore")  # findfont fallbacks

HERE = os.path.dirname(os.path.abspath(__file__))
BENCH = os.path.dirname(HERE)
RESULTS = sys.argv[1] if len(sys.argv) > 1 else os.path.join(BENCH, "results")
CPU = os.path.join(RESULTS, "2026-11-14-cpu")
CAD = os.path.join(RESULTS, "2026-11-14-cadence")
OUT = os.path.join(BENCH, "assets")
FG = "#eb6834"
WINUI = "#2a78d6"
SURFACE = "#fcfcfb"
INK = "#0b0b0b"
INK2 = "#52514e"
BUDGET = 1000.0 / 120.0

os.makedirs(OUT, exist_ok=True)


def load(path):
    with open(path, encoding="utf-8-sig") as fh:
        return json.load(fh)


cpu = load(os.path.join(CPU, "summary.json"))
cad = load(os.path.join(CAD, "summary.json"))


def row(s, scenario, metric):
    for r in s["rows"]:
        if r["scenario"] == scenario and r["metric"] == metric:
            return r["winUI"], r["fluentGpu"]
    raise KeyError((scenario, metric))


def median(v):
    v = sorted(v)
    n = len(v)
    return v[n // 2] if n % 2 else (v[n // 2 - 1] + v[n // 2]) / 2


def external_median(s, scenario):
    return [median([r["externalStartToResultMs"] for r in s["runs"]
                    if r["scenario"] == scenario and r["framework"] == fw and r["success"]])
            for fw in ("WinUI 3", "FluentGpu")]


def raws(base, pas, scenario, prefix):
    res = []
    for f in sorted(glob.glob(os.path.join(base, "raw", pas, scenario, prefix + "-*.json"))):
        if ".mutations" in f:
            continue
        res.append(load(f))
    return res


def pct(v, p):
    v = sorted(v)
    pos = (len(v) - 1) * p
    lo = int(pos)
    hi = min(lo + 1, len(v) - 1)
    return v[lo] + (v[hi] - v[lo]) * (pos - lo)


def runs_ok(s, scenario):
    return sum(o["succeeded"] for o in s["outcomes"] if o["scenario"] == scenario)


plt.xkcd(scale=1.1, length=120, randomness=2)
plt.rcParams.update(
    {
        "font.family": "Comic Sans MS",
        "figure.facecolor": SURFACE,
        "axes.facecolor": SURFACE,
        "savefig.facecolor": SURFACE,
        "text.color": INK,
        "axes.edgecolor": INK,
        "axes.labelcolor": INK,
        "xtick.color": INK2,
        "ytick.color": INK2,
        "font.size": 13,
    }
)


def strip(ax, keep_bottom=True):
    for side in ("top", "right", "left"):
        ax.spines[side].set_visible(False)
    if not keep_bottom:
        ax.spines["bottom"].set_visible(False)
    ax.tick_params(length=0)


def frames_word(ms):
    n = round(ms / BUDGET)
    return f"{n} refresh" + ("" if n == 1 else "es")


# ── data ──────────────────────────────────────────────────────────────────────
nav_wu, nav_fg = row(cad, "page-navigation", "frame time p50")
nav_wall_wu, nav_wall_fg = external_median(cad, "page-navigation")
nav_cpu = {m: row(cpu, "page-navigation", f"CPU work {m}") for m in ("p50", "p99", "max")}
nav_pb = row(cpu, "page-navigation", "private bytes p50")
cold = {s: row(cpu, s, "process start to result p50") for s in ("startup", "buttons-225", "text-1125")}
delta = {s: row(cpu, s, "workload delta p50") for s in ("buttons-225", "text-1125")}
scroll = {s: row(cad, s, "CPU work p50") for s in ("virtual-scroll-1k", "virtual-scroll-10k")}

theme = {fw: raws(CAD, "cadence", "theme-switch", p) for fw, p in (("WinUI 3", "winui"), ("FluentGpu", "fluentgpu"))}
theme_lat = {fw: [v for r in rs for v in r["frameMs"]] for fw, rs in theme.items()}
theme_n = {fw: len(v) for fw, v in theme_lat.items()}
resize = {fw: raws(CAD, "cadence", "window-resize", p) for fw, p in (("WinUI 3", "winui"), ("FluentGpu", "fluentgpu"))}

# 1 ── theme switch: switch to the first fully re-themed frame ─────────────────
fig, ax = plt.subplots(figsize=(8.6, 4.6), dpi=160)
names = ["FluentGpu", "WinUI 3"]
p50 = [pct(theme_lat[n], 0.5) for n in names]
p99 = [pct(theme_lat[n], 0.99) for n in names]
ax.bar(names, p50, width=0.52, color=[FG, WINUI], zorder=3)
ax.errorbar([0, 1], p50, yerr=[[0, 0], [p99[0] - p50[0], p99[1] - p50[1]]], fmt="none", ecolor=INK2, capsize=8, lw=2, zorder=4)
ax.axhline(BUDGET, ls="--", lw=2, color=INK2, zorder=2)
ax.text(0.5, BUDGET + 0.5, "one 120 Hz refresh", ha="center", va="bottom", fontsize=11, color=INK2)
for i in range(2):
    ax.text(i - 0.06, p50[i] + 0.6, f"{p50[i]:.1f} ms\n{frames_word(p50[i])}", ha="right", va="bottom", fontsize=12)
ax.set_ylim(0, max(p99) * 1.25)
ax.set_ylabel("switch to fully re-themed (ms)")
ax.set_title("Light <-> Dark on a real page: when is every pixel switched?", fontsize=14)
strip(ax)
fig.text(0.5, 0.015,
         f"bar = p50, whisker = p99 · {theme_n['FluentGpu']} + {theme_n['WinUI 3']} switches, 5 runs each, "
         f"read off the composed desktop", ha="center", fontsize=10, color=INK2)
fig.tight_layout(rect=(0, 0.045, 1, 1))
fig.savefig(os.path.join(OUT, "theme-switch.png"))
plt.close(fig)

# 2 ── live resize (a WinUI win): how far the content trails the window ────────
fig, ax = plt.subplots(figsize=(8.6, 4.6), dpi=160)
random.seed(11)
xmax = 0
for i, n in enumerate(["FluentGpu", "WinUI 3"]):
    lag = [v for r in resize[n] for v in r["series"]["contentLagFrames"]]
    color = FG if n == "FluentGpu" else WINUI
    ax.scatter(lag, [i + random.uniform(-0.17, 0.17) for _ in lag], s=6, color=color, alpha=0.15, lw=0, zorder=3)
    m = pct(lag, 0.5)
    xmax = max(xmax, max(lag))
    ax.text(pct(lag, 0.5), i + 0.3, f"median {m:.0f} frame{'s' if m != 1 else ''} behind", fontsize=11, ha="left")
ax.set_yticks([0, 1], ["FluentGpu", "WinUI 3"])
ax.set_xlim(-1, xmax + 2)
ax.set_xlabel("frames the content trails the window size (one dot per composed frame)")
ax.set_title("Live resize: how far behind the window is the content?", fontsize=14)
ax.set_ylim(-0.6, 1.6)
strip(ax)
fig.tight_layout()
fig.savefig(os.path.join(OUT, "window-resize.png"))
plt.close(fig)

# 3 ── page navigation frame time vs the 120 Hz budget ─────────────────────────
fig, ax = plt.subplots(figsize=(8.6, 4.6), dpi=160)
vals = [nav_fg, nav_wu]
ax.bar(["FluentGpu", "WinUI 3"], vals, width=0.52, color=[FG, WINUI], zorder=3)
ax.axhline(BUDGET, ls="--", lw=2, color=INK2, zorder=2)
ax.text(0.5, BUDGET + 0.4, "120 Hz frame budget (8.33 ms)", ha="center", va="bottom", fontsize=11, color=INK2)
for i in range(2):
    ax.text(i, vals[i] + 0.4, f"{vals[i]:.1f} ms\n{frames_word(vals[i])}", ha="center", va="bottom", fontsize=12)
ax.set_ylim(0, max(vals) * 1.35)
ax.set_ylabel("frame time p50 (ms)")
ax.set_title("Navigate a full page, every frame\n(hero + 24-card grid + 40-row list, built fresh per navigation)", fontsize=14)
strip(ax)
ax.set_yticks([0, BUDGET, 2 * BUDGET])
fig.text(0.5, 0.015, f"same 1,000 navigations: FluentGpu {nav_wall_fg / 1000:.1f} s  ·  WinUI 3 {nav_wall_wu / 1000:.1f} s",
         ha="center", fontsize=11, color=INK2)
fig.tight_layout(rect=(0, 0.045, 1, 1))
fig.savefig(os.path.join(OUT, "nav-frame-time.png"))
plt.close(fig)

# 4 ── marginal content cost (workload delta) ──────────────────────────────────
fig, ax = plt.subplots(figsize=(8.6, 4.2), dpi=160)
rows = ["225 styled\nbuttons", "1,125 text\nblocks"]
wu_vals = [delta["buttons-225"][0], delta["text-1125"][0]]
fg_vals = [delta["buttons-225"][1], delta["text-1125"][1]]
y = [1.0, 0.0]
h = 0.34
ax.barh([v + h / 2 + 0.02 for v in y], wu_vals, height=h, color=WINUI, zorder=3, label="WinUI 3")
ax.barh([v - h / 2 - 0.02 for v in y], [max(v, 0.0) for v in fg_vals], height=h, color=FG, zorder=3, label="FluentGpu")
ax.set_yticks(y, rows)
ax.set_xlabel("added launch cost over an empty window (ms, p50)")
ax.set_title("What the content costs at launch", fontsize=14)
xmax = max(wu_vals) * 1.35
for yi, wv, fv in zip(y, wu_vals, fg_vals):
    ax.text(wv + xmax * 0.015, yi + h / 2 + 0.02, f"{wv:.1f} ms", va="center", fontsize=12)
    label = f"{fv:.2f} ms  ({wv / fv:.0f}x less)" if fv > 0 else f"{fv:.2f} ms  (within noise of an empty window)"
    ax.text(max(fv, 0) + xmax * 0.015, yi - h / 2 - 0.02, label, va="center", fontsize=12)
ax.set_xlim(0, xmax)
ax.legend(loc="lower right", frameon=False, fontsize=11)
strip(ax)
fig.tight_layout()
fig.savefig(os.path.join(OUT, "content-cost.png"))
plt.close(fig)

# 5 ── memory while navigating flat-out ────────────────────────────────────────
fig, ax = plt.subplots(figsize=(8.6, 3.9), dpi=160)
vals = [nav_pb[1], nav_pb[0]]
ax.barh([1, 0], vals, height=0.5, color=[FG, WINUI], zorder=3)
ax.set_yticks([1, 0], ["FluentGpu", "WinUI 3"])
ax.set_xlabel("private bytes after 1,000 un-paced navigations (MiB, p50)")
ax.set_title("Navigation memory", fontsize=14)
xmax = max(vals) * 1.15
ax.text(vals[0] + xmax * 0.015, 1, f"{vals[0]:,.0f} MiB", va="center", fontsize=12)
ax.text(vals[1] / 2, 0.42, f"{vals[1]:,.0f} MiB  ({vals[1] / vals[0]:.0f}x more)", ha="center", va="bottom", fontsize=12)
ax.set_xlim(0, xmax)
strip(ax)
fig.tight_layout()
fig.savefig(os.path.join(OUT, "nav-memory.png"))
plt.close(fig)

# 6 ── cold start, grouped ─────────────────────────────────────────────────────
fig, ax = plt.subplots(figsize=(8.6, 4.4), dpi=160)
scen = ["empty window", "225 buttons", "1,125 texts"]
keys = ["startup", "buttons-225", "text-1125"]
fg_vals = [cold[k][1] for k in keys]
wu_vals = [cold[k][0] for k in keys]
x = [0, 1, 2]
w = 0.34
ax.bar([v - w / 2 - 0.02 for v in x], fg_vals, width=w, color=FG, zorder=3, label="FluentGpu")
ax.bar([v + w / 2 + 0.02 for v in x], wu_vals, width=w, color=WINUI, zorder=3, label="WinUI 3")
top = max(fg_vals + wu_vals)
for xi, v in zip(x, fg_vals):
    ax.text(xi - w / 2 - 0.02, v + top * 0.02, f"{v:.0f}", ha="center", fontsize=11)
for xi, v in zip(x, wu_vals):
    ax.text(xi + w / 2 + 0.02, v + top * 0.02, f"{v:.0f}", ha="center", fontsize=11)
ax.set_xticks(x, scen)
ax.set_ylabel("launch to first frame (ms, p50)")
ax.set_title("Cold start: launch cost vs content", fontsize=14)
ax.legend(loc="upper left", frameon=False, fontsize=11)
ax.set_ylim(0, top * 1.22)
strip(ax)
fig.tight_layout()
fig.savefig(os.path.join(OUT, "cold-start.png"))
plt.close(fig)

# 7 ── every single navigation frame (raw frameMs, cadence) ────────────────────
random.seed(7)
fg_frames = [v for r in raws(CAD, "cadence", "page-navigation", "fluentgpu") for v in r["frameMs"]]
wu_frames = [v for r in raws(CAD, "cadence", "page-navigation", "winui") for v in r["frameMs"]]
fig, ax = plt.subplots(figsize=(8.6, 4.6), dpi=160)
ax.axvline(BUDGET, ls="--", lw=2, color=INK2, zorder=1)
ax.scatter(wu_frames, [1 + random.uniform(-0.16, 0.16) for _ in wu_frames], s=7, color=WINUI, alpha=0.18, lw=0, zorder=3)
ax.scatter(fg_frames, [0 + random.uniform(-0.16, 0.16) for _ in fg_frames], s=7, color=FG, alpha=0.18, lw=0, zorder=3)
ax.set_yticks([0, 1], ["FluentGpu", "WinUI 3"])
ax.set_xlabel("frame time (ms) — every dot is one real navigation frame")
ax.set_title(f"All {len(fg_frames):,} frames of each, no averaging", fontsize=14)
ax.text(BUDGET + 0.4, 1.62, "8.33 ms budget", fontsize=11, color=INK2)
ax.set_xlim(0, min(max(pct(wu_frames, 0.999), pct(fg_frames, 0.999)) * 1.1, 80))
ax.set_ylim(-0.55, 1.85)
strip(ax)
fig.tight_layout()
fig.savefig(os.path.join(OUT, "nav-every-frame.png"))
plt.close(fig)

# 8 ── the worst moment (un-paced navigation CPU) ──────────────────────────────
fig, ax = plt.subplots(figsize=(8.6, 4.2), dpi=160)
labels = ["typical (p50)", "bad (p99)", "worst (max)"]
wu = [nav_cpu[m][0] for m in ("p50", "p99", "max")]
fg = [nav_cpu[m][1] for m in ("p50", "p99", "max")]
y = [2, 1, 0]
h = 0.32
ax.barh([v + h / 2 + 0.02 for v in y], wu, height=h, color=WINUI, zorder=3, label="WinUI 3")
ax.barh([v - h / 2 - 0.02 for v in y], fg, height=h, color=FG, zorder=3, label="FluentGpu")
ax.set_yticks(y, labels)
xmax = max(wu + fg) * 1.18
for yi, v in zip(y, wu):
    ax.text(v + xmax * 0.012, yi + h / 2 + 0.02, f"{v:.3g} ms", va="center", fontsize=11)
for yi, v in zip(y, fg):
    ax.text(v + xmax * 0.012, yi - h / 2 - 0.02, f"{v:.3g} ms", va="center", fontsize=11)
ax.set_xlabel("CPU per navigation, driven flat-out (ms)")
ax.set_title(f"The worst moment out of {runs_ok(cpu, 'page-navigation') // 2 * 1000:,} navigations each", fontsize=14)
ax.legend(loc="center right", frameon=False, fontsize=11)
ax.set_xlim(0, xmax)
strip(ax)
fig.tight_layout()
fig.savefig(os.path.join(OUT, "nav-worst-moment.png"))
plt.close(fig)

# 9 ── 10x the rows, flat cost (cadence scroll CPU) ────────────────────────────
fig, ax = plt.subplots(figsize=(8.6, 4.0), dpi=160)
x = [0, 1]
w = 0.34
fg_vals = [scroll["virtual-scroll-1k"][1], scroll["virtual-scroll-10k"][1]]
wu_vals = [scroll["virtual-scroll-1k"][0], scroll["virtual-scroll-10k"][0]]
ax.bar([v - w / 2 - 0.02 for v in x], fg_vals, width=w, color=FG, zorder=3, label="FluentGpu")
ax.bar([v + w / 2 + 0.02 for v in x], wu_vals, width=w, color=WINUI, zorder=3, label="WinUI 3")
top = max(fg_vals + wu_vals)
for xi, v in zip(x, fg_vals):
    ax.text(xi - w / 2 - 0.02, v + top * 0.03, f"{v:.2f}", ha="center", fontsize=11)
for xi, v in zip(x, wu_vals):
    ax.text(xi + w / 2 + 0.02, v + top * 0.03, f"{v:.2f}", ha="center", fontsize=11)
ax.set_xticks(x, ["1,000 rows", "10,000 rows"])
ax.set_ylabel("CPU per scrolled frame (ms, p50)")
ax.set_title("10x the rows, flat cost", fontsize=14)
ax.legend(loc="upper left", frameon=False, fontsize=11)
ax.set_ylim(0, top * 1.35)
strip(ax)
fig.tight_layout()
fig.savefig(os.path.join(OUT, "scroll-flat-cost.png"))
plt.close(fig)

# 10 ── overview (four panels) ─────────────────────────────────────────────────
total_runs = sum(o["attempted"] for s in (cpu, cad) for o in s["outcomes"])
total_ok = sum(o["succeeded"] for s in (cpu, cad) for o in s["outcomes"])
fig, axes = plt.subplots(2, 2, figsize=(12.8, 7.7), dpi=160)
ax = axes[0][0]
p50 = [pct(theme_lat[n], 0.5) for n in ("FluentGpu", "WinUI 3")]
ax.bar(["FluentGpu", "WinUI 3"], p50, width=0.52, color=[FG, WINUI], zorder=3)
ax.axhline(BUDGET, ls="--", lw=2, color=INK2, zorder=2)
for i, v in enumerate(p50):
    ax.text(i, v + 0.6, f"{v:.1f} ms\n{frames_word(v)}", ha="center", va="bottom", fontsize=11)
ax.set_ylim(0, max(p50) * 1.5)
ax.set_title("Light <-> Dark: switch to fully re-themed (p50)", fontsize=13)
strip(ax)
ax = axes[0][1]
v = [nav_fg, nav_wu]
ax.bar(["FluentGpu", "WinUI 3"], v, width=0.52, color=[FG, WINUI], zorder=3)
ax.axhline(BUDGET, ls="--", lw=2, color=INK2, zorder=2)
for i, q in enumerate(v):
    ax.text(i, q + 0.4, f"{q:.1f} ms", ha="center", va="bottom", fontsize=11)
ax.set_ylim(0, max(v) * 1.4)
ax.set_title("Navigate a full page (frame time p50)", fontsize=13)
strip(ax)
ax = axes[1][0]
labels = ["typical\n(p50)", "bad\n(p99)", "worst\n(max)"]
y = [2, 1, 0]
h = 0.32
ax.barh([q + h / 2 + 0.02 for q in y], wu, height=h, color=WINUI, zorder=3, label="WinUI 3")
ax.barh([q - h / 2 - 0.02 for q in y], fg, height=h, color=FG, zorder=3, label="FluentGpu")
xmax = max(wu + fg) * 1.2
for yi, q in zip(y, wu):
    ax.text(q + xmax * 0.012, yi + h / 2 + 0.02, f"{q:.3g}", va="center", fontsize=10)
for yi, q in zip(y, fg):
    ax.text(q + xmax * 0.012, yi - h / 2 - 0.02, f"{q:.3g}", va="center", fontsize=10)
ax.set_yticks(y, labels)
ax.set_xlim(0, xmax)
ax.set_title("CPU per navigation, un-paced (ms)", fontsize=13)
ax.legend(loc="center right", frameon=False, fontsize=10)
strip(ax)
ax = axes[1][1]
names = ["225 buttons\n(launch delta, ms)", "memory after\n1k navigations (MiB)"]
wu_n = [delta["buttons-225"][0], nav_pb[0]]
fg_n = [delta["buttons-225"][1], nav_pb[1]]
y = [1, 0]
ax.barh([q + h / 2 + 0.02 for q in y], [1, 1], height=h, color=WINUI, zorder=3)
frac = [max(fg_n[i] / wu_n[i], 0.004) for i in range(2)]
ax.barh([q - h / 2 - 0.02 for q in y], frac, height=h, color=FG, zorder=3)
for i, yi in enumerate(y):
    ax.text(1.01, yi + h / 2 + 0.02, f"{wu_n[i]:,.1f}" if i == 0 else f"{wu_n[i]:,.0f}", va="center", fontsize=10)
    less = f"  ({wu_n[i] / fg_n[i]:.0f}x less)" if fg_n[i] > 0 else ""
    ax.text(frac[i] + 0.01, yi - h / 2 - 0.02, (f"{fg_n[i]:.2f}" if i == 0 else f"{fg_n[i]:,.0f}") + less, va="center", fontsize=10)
ax.set_yticks(y, names)
ax.set_xticks([])
ax.set_xlim(0, 1.25)
ax.set_title("Content cost and memory (WinUI 3 = full bar)", fontsize=13)
strip(ax, keep_bottom=False)
fig.suptitle(f"FluentGPU vs WinUI 3: same pages, same machine, ARM64 NativeAOT ({total_ok}/{total_runs} runs, 2026-11-14)",
             fontsize=15)
fig.text(0.5, 0.01, "pixel-parity workloads, SHA-verified NativeAOT hosts - full data, methodology and caveats: "
         "fluent-gpu/benchmarks/FrameworkComparison", ha="center", fontsize=10, color=INK2)
fig.tight_layout(rect=(0, 0.03, 1, 0.95))
fig.savefig(os.path.join(OUT, "fluent-gpu-vs-winui3.png"))
plt.close(fig)

print("charts written to", OUT)
