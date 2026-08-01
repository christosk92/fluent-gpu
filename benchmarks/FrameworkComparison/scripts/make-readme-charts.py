# Doodle-style (xkcd) benchmark charts for benchmarks/FrameworkComparison/README.md
# Data: results/wavee1-* + results/waved-* (schema v4, 620/620 runs, zero crashes).
# Palette (validated, dataviz skill): FluentGpu #eb6834 (orange), WinUI 3 #2a78d6 (blue).
import warnings
import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt

warnings.filterwarnings("ignore")  # findfont fallbacks

OUT = r"C:\wavee\fluent-gpu\benchmarks\FrameworkComparison\assets"
FG = "#eb6834"
WINUI = "#2a78d6"
SURFACE = "#fcfcfb"
INK = "#0b0b0b"
INK2 = "#52514e"

import os

os.makedirs(OUT, exist_ok=True)

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


# 1 ── HERO: page-navigation frame time vs the 120 Hz budget ──────────────────
fig, ax = plt.subplots(figsize=(8.6, 4.6), dpi=160)
names = ["FluentGpu", "WinUI 3"]
vals = [8.37, 16.69]
bars = ax.bar(names, vals, width=0.52, color=[FG, WINUI], zorder=3)
ax.axhline(8.33, ls="--", lw=2, color=INK2, zorder=2)
ax.text(1.38, 8.33 - 0.55, "120 Hz frame budget (8.33 ms)", ha="right", va="top", fontsize=11, color=INK2)
ax.text(0, vals[0] + 0.4, "8.4 ms\nevery frame on time", ha="center", va="bottom", fontsize=12)
ax.text(1, vals[1] + 0.4, "16.7 ms\ntwo vblanks = HALF the frame rate", ha="center", va="bottom", fontsize=12)
ax.set_ylim(0, 22.5)
ax.set_ylabel("frame time p50 (ms)")
ax.set_title("Navigate a full page, every frame\n(hero + 24-card grid + 40-row list, built fresh per navigation)", fontsize=14)
strip(ax)
ax.set_yticks([0, 8.33, 16.7])
fig.text(0.5, 0.015, "same 1,000 navigations: FluentGpu 9.0 s  ·  WinUI 3 18.7 s", ha="center", fontsize=11, color=INK2)
fig.tight_layout(rect=(0, 0.045, 1, 1))
fig.savefig(os.path.join(OUT, "nav-frame-time.png"))
plt.close(fig)

# 2 ── Marginal content cost (workload delta) ─────────────────────────────────
fig, ax = plt.subplots(figsize=(8.6, 4.2), dpi=160)
rows = ["225 styled\nbuttons", "1,125 text\nblocks"]
fg_vals = [0.47, 31.4]
wu_vals = [91.2, 91.6]
y = [1.0, 0.0]
h = 0.34
ax.barh([v + h / 2 + 0.02 for v in y], wu_vals, height=h, color=WINUI, zorder=3, label="WinUI 3")
ax.barh([v - h / 2 - 0.02 for v in y], fg_vals, height=h, color=FG, zorder=3, label="FluentGpu")
ax.set_yticks(y, rows)
ax.set_xlabel("added launch cost over an empty window (ms, p50)")
ax.set_title("What the content costs — 194x cheaper on buttons", fontsize=14)
ax.text(91.2 + 1.5, 1.0 + h / 2 + 0.02, "91.2 ms", va="center", fontsize=12)
ax.text(0.47 + 1.5, 1.0 - h / 2 - 0.02, "0.47 ms  (194x less)", va="center", fontsize=12)
ax.text(91.6 + 1.5, 0.0 + h / 2 + 0.02, "91.6 ms", va="center", fontsize=12)
ax.text(31.4 + 1.5, 0.0 - h / 2 - 0.02, "31.4 ms  (2.9x less)", va="center", fontsize=12)
ax.set_xlim(0, 125)
ax.legend(loc="lower right", frameon=False, fontsize=11)
strip(ax)
fig.tight_layout()
fig.savefig(os.path.join(OUT, "content-cost.png"))
plt.close(fig)

# 3 ── Memory while navigating flat-out ───────────────────────────────────────
fig, ax = plt.subplots(figsize=(8.6, 3.9), dpi=160)
names = ["FluentGpu", "WinUI 3"]
vals = [104, 2568]
ax.barh([1, 0], vals, height=0.5, color=[FG, WINUI], zorder=3)
ax.set_yticks([1, 0], names)
ax.set_xlabel("private bytes after 1,000 un-paced navigations (MiB, p50)")
ax.set_title("Navigation memory: flat vs runaway", fontsize=14)
ax.text(104 + 40, 1, "104 MiB — flat, same as at rest", va="center", fontsize=12)
ax.text(1284, 0.42, "2,568 MiB (2.5 GiB) — 25x more", ha="center", va="bottom", fontsize=12)
ax.set_xlim(0, 2800)
strip(ax)
fig.tight_layout()
fig.savefig(os.path.join(OUT, "nav-memory.png"))
plt.close(fig)

# 4 ── Cold start, grouped ────────────────────────────────────────────────────
fig, ax = plt.subplots(figsize=(8.6, 4.4), dpi=160)
scen = ["empty window", "225 buttons", "1,125 texts"]
fg_vals = [109.0, 109.5, 140.4]
wu_vals = [109.7, 200.9, 201.3]
x = [0, 1, 2]
w = 0.34
ax.bar([v - w / 2 - 0.02 for v in x], fg_vals, width=w, color=FG, zorder=3, label="FluentGpu")
ax.bar([v + w / 2 + 0.02 for v in x], wu_vals, width=w, color=WINUI, zorder=3, label="WinUI 3")
for xi, v in zip(x, fg_vals):
    ax.text(xi - w / 2 - 0.02, v + 4, f"{v:.0f}", ha="center", fontsize=11)
for xi, v in zip(x, wu_vals):
    ax.text(xi + w / 2 + 0.02, v + 4, f"{v:.0f}", ha="center", fontsize=11)
ax.set_xticks(x, scen)
ax.set_ylabel("launch to first frame (ms, p50)")
ax.set_title("Cold start: launch cost doesn't grow with content", fontsize=14)
ax.legend(loc="upper left", frameon=False, fontsize=11)
ax.set_ylim(0, 245)
strip(ax)
fig.tight_layout()
fig.savefig(os.path.join(OUT, "cold-start.png"))
plt.close(fig)

# 5 ── Every single navigation frame (raw frameMs, wavee1-cadence) ────────────
import glob
import json
import random

random.seed(7)
RAW = r"C:\wavee\fluent-gpu\benchmarks\FrameworkComparison\results\wavee1-cadence\raw\cadence\page-navigation"


def load_frames(prefix):
    frames = []
    for f in sorted(glob.glob(os.path.join(RAW, prefix + "-*.json"))):
        with open(f, encoding="utf-8-sig") as fh:
            frames.extend(json.load(fh)["frameMs"])
    return frames


fg_frames = load_frames("fluentgpu")
wu_frames = load_frames("winui")

fig, ax = plt.subplots(figsize=(8.6, 4.6), dpi=160)
ax.axvline(8.33, ls="--", lw=2, color=INK2, zorder=1)
ax.scatter(wu_frames, [1 + random.uniform(-0.16, 0.16) for _ in wu_frames], s=7, color=WINUI, alpha=0.18, lw=0, zorder=3)
ax.scatter(fg_frames, [0 + random.uniform(-0.16, 0.16) for _ in fg_frames], s=7, color=FG, alpha=0.18, lw=0, zorder=3)
ax.set_yticks([0, 1], ["FluentGpu", "WinUI 3"])
ax.set_xlabel("frame time (ms) — every dot is one real navigation frame")
ax.set_title("All 5,000 frames of each, no averaging", fontsize=14)
ax.text(8.33 + 0.4, 1.62, "8.33 ms budget", fontsize=11, color=INK2)
ax.text(8.9, -0.34, "one tight band, on budget", fontsize=11, ha="left")
ax.text(17.4, 0.62, "double the budget, spikes past 40 ms", fontsize=11, ha="left")
ax.set_xlim(5, 48)
ax.set_ylim(-0.55, 1.85)
strip(ax)
fig.tight_layout()
fig.savefig(os.path.join(OUT, "nav-every-frame.png"))
plt.close(fig)

# 6 ── The worst moment (un-paced navigation CPU, wavee1-cpu) ─────────────────
fig, ax = plt.subplots(figsize=(8.6, 4.2), dpi=160)
labels = ["typical (p50)", "bad (p99)", "worst (max)"]
wu = [6.05, 31.9, 463.5]
fg = [1.09, 2.16, 3.46]
y = [2, 1, 0]
h = 0.32
ax.barh([v + h / 2 + 0.02 for v in y], wu, height=h, color=WINUI, zorder=3, label="WinUI 3")
ax.barh([v - h / 2 - 0.02 for v in y], fg, height=h, color=FG, zorder=3, label="FluentGpu")
ax.set_yticks(y, labels)
for yi, v in zip(y, wu):
    ax.text(v + 6, yi + h / 2 + 0.02, f"{v:g} ms", va="center", fontsize=11)
for yi, v in zip(y, fg):
    ax.text(v + 6, yi - h / 2 - 0.02, f"{v:g} ms", va="center", fontsize=11)
ax.text(240, 0.62, "half a second of frozen UI", fontsize=12)
ax.text(60, -0.36, "you would never notice", fontsize=12)
ax.set_xlabel("CPU per navigation, driven flat-out (ms)")
ax.set_title("The worst moment out of 5,000 navigations", fontsize=14)
ax.legend(loc="center right", frameon=False, fontsize=11)
ax.set_xlim(0, 530)
strip(ax)
fig.tight_layout()
fig.savefig(os.path.join(OUT, "nav-worst-moment.png"))
plt.close(fig)

# 7 ── 10x the rows, flat cost (cadence scroll CPU) ───────────────────────────
fig, ax = plt.subplots(figsize=(8.6, 4.0), dpi=160)
x = [0, 1]
w = 0.34
fg_vals = [0.34, 0.31]
wu_vals = [0.92, 0.98]
ax.bar([v - w / 2 - 0.02 for v in x], fg_vals, width=w, color=FG, zorder=3, label="FluentGpu")
ax.bar([v + w / 2 + 0.02 for v in x], wu_vals, width=w, color=WINUI, zorder=3, label="WinUI 3")
for xi, v in zip(x, fg_vals):
    ax.text(xi - w / 2 - 0.02, v + 0.03, f"{v:.2f}", ha="center", fontsize=11)
for xi, v in zip(x, wu_vals):
    ax.text(xi + w / 2 + 0.02, v + 0.03, f"{v:.2f}", ha="center", fontsize=11)
ax.set_xticks(x, ["1,000 rows", "10,000 rows"])
ax.set_ylabel("CPU per scrolled frame (ms, p50)")
ax.set_title("10x the rows, flat cost - and 3x less of it", fontsize=14)
ax.legend(loc="upper left", frameon=False, fontsize=11)
ax.set_ylim(0, 1.35)
strip(ax)
fig.tight_layout()
fig.savefig(os.path.join(OUT, "scroll-flat-cost.png"))
plt.close(fig)

print("charts written to", OUT)
