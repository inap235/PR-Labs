"""Draws the charts of the report from lab1/results/task*.csv into lab1/report/figures.

Task 1 uses the runs of TASK1_DATE (the three repetitions behind the tables of the report),
Task 2 uses the runs with the variant TASK2_VARIANT (cold cache, the volume dismounted before
every run). Every chart shows the median of the selected runs.

    python make_charts.py
"""
import csv
import statistics
from collections import defaultdict
from pathlib import Path

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt

HERE = Path(__file__).resolve().parent
RESULTS = HERE.parent / "results"
FIGURES = HERE / "figures"

TASK1_DATE = "2026-10-04"
TASK2_VARIANT = "usb-cold"
PHYSICAL_CORES, LOGICAL_PROCESSORS = 8, 16

# Validated categorical slots 1-3 (light mode), ink colours and a recessive grid.
BLUE, ORANGE, AQUA = "#2a78d6", "#eb6834", "#1baf7a"
INK, INK_2, MUTED, GRID = "#0b0b0b", "#52514e", "#8a8984", "#e4e3df"

plt.rcParams.update({
    "font.size": 9, "axes.titlesize": 10, "axes.titleweight": "bold",
    "axes.edgecolor": MUTED, "axes.labelcolor": INK_2, "axes.titlecolor": INK,
    "xtick.color": INK_2, "ytick.color": INK_2, "axes.grid": True,
    "grid.color": GRID, "grid.linewidth": 0.6, "axes.axisbelow": True,
    "axes.spines.top": False, "axes.spines.right": False,
    "lines.linewidth": 2, "lines.markersize": 5, "savefig.dpi": 200,
})


def load(task):
    with open(RESULTS / f"task{task}.csv", newline="") as f:
        return list(csv.DictReader(f))


def medians(rows, key):
    """Median of `key` per thread count."""
    by_threads = defaultdict(list)
    for r in rows:
        by_threads[int(r["threads"])].append(float(r[key]))
    return {t: statistics.median(v) for t, v in sorted(by_threads.items())}


def notes(row):
    return dict(p.split("=") for p in row["notes"].split(";") if "=" in p)


def thread_axis(ax, threads, refs=True):
    ax.set_xscale("log", base=2)
    ax.set_xticks(threads)
    ax.set_xticklabels([str(t) for t in threads])
    ax.minorticks_off()
    ax.set_xlabel("threads")
    if refs:
        for x, name, ha in ((PHYSICAL_CORES, "8 cores ", "right"),
                            (LOGICAL_PROCESSORS, " 16 logical", "left")):
            ax.axvline(x, color=MUTED, linestyle="--", linewidth=1)
            ax.text(x, 1.0, name, transform=ax.get_xaxis_transform(), ha=ha, va="bottom",
                    fontsize=7.5, color=INK_2)


def label_points(ax, xs, ys, fmt, color=INK_2, offset=6):
    for x, y in zip(xs, ys):
        ax.annotate(fmt(y), (x, y), textcoords="offset points", xytext=(0, offset),
                    ha="center", fontsize=7.5, color=color)


def save(fig, name):
    FIGURES.mkdir(exist_ok=True)
    fig.tight_layout()
    fig.savefig(FIGURES / name, bbox_inches="tight", facecolor="white")
    plt.close(fig)
    print("written", FIGURES / name)


def task1a(rows):
    t = medians(rows, "time_ms")
    mem = medians(rows, "peak_private_mb")
    xs = list(t)
    speed = [t[1] / t[x] for x in xs]
    fig, axes = plt.subplots(1, 3, figsize=(11, 3.3))
    ax = axes[0]
    ax.plot(xs, [t[x] for x in xs], marker="o", color=BLUE)
    label_points(ax, xs, [t[x] for x in xs], lambda v: f"{v:.0f}")
    ax.set_title("Execution time (median)", pad=14)
    ax.set_ylabel("ms")
    ax.set_ylim(0, max(t.values()) * 1.15)
    ax = axes[1]
    ax.plot(xs, xs, color=MUTED, linewidth=1, linestyle=":")
    ax.text(4, 4.6, "ideal", fontsize=7.5, color=INK_2, rotation=0)
    ax.plot(xs, speed, marker="o", color=BLUE)
    label_points(ax, xs, speed, lambda v: f"{v:.2f}")
    ax.set_title("Speed-up against 1 thread", pad=14)
    ax.set_ylim(0, 10)
    ax = axes[2]
    ax.plot(xs, [mem[x] for x in xs], marker="o", color=BLUE)
    label_points(ax, xs, [mem[x] for x in xs], lambda v: f"{v:.0f}")
    ax.set_title("Peak private memory", pad=14)
    ax.set_ylabel("MB")
    ax.set_ylim(0, max(mem.values()) * 1.18)
    for ax in axes:
        thread_axis(ax, xs)
    save(fig, "task1a.png")
    return t


def task1b(rows):
    order = ["local", "unsync", "interlocked", "lock"]
    names = {"local": "local", "unsync": "unsync\n(WRONG)", "interlocked": "interlocked",
             "lock": "lock"}
    by = defaultdict(list)
    mem = defaultdict(list)
    for r in rows:
        by[r["variant"]].append(float(r["time_ms"]))
        mem[r["variant"]].append(float(r["peak_ws_mb"]))
    times = [statistics.median(by[v]) for v in order]
    mems = [statistics.median(mem[v]) for v in order]
    fig, axes = plt.subplots(1, 2, figsize=(10, 3.3))
    ax = axes[0]
    bars = ax.bar([names[v] for v in order], times, color=BLUE, width=0.55)
    ax.set_yscale("log")
    ax.set_ylim(10, 40000)
    ax.set_title("Execution time (median, log scale)")
    ax.set_ylabel("ms")
    for b, v in zip(bars, times):
        ax.annotate(f"{v:,.0f} ms", (b.get_x() + b.get_width() / 2, v), textcoords="offset points",
                    xytext=(0, 3), ha="center", fontsize=8, color=INK)
    ax = axes[1]
    bars = ax.bar([names[v] for v in order], mems, color=BLUE, width=0.55)
    ax.set_title("Peak working set (median)")
    ax.set_ylabel("MB")
    ax.set_ylim(0, 260)
    for b, v in zip(bars, mems):
        ax.annotate(f"{v:.0f} MB", (b.get_x() + b.get_width() / 2, v), textcoords="offset points",
                    xytext=(0, 3), ha="center", fontsize=8, color=INK)
    for ax in axes:
        ax.grid(axis="x", visible=False)
    save(fig, "task1b.png")


def task1c(rows):
    t = medians(rows, "time_ms")
    xs = list(t)
    speed = [t[1] / t[x] for x in xs]
    fig, axes = plt.subplots(1, 2, figsize=(9, 3.3))
    ax = axes[0]
    ax.plot(xs, [t[x] for x in xs], marker="o", color=ORANGE)
    label_points(ax, xs, [t[x] for x in xs], lambda v: f"{v:,.0f}")
    ax.set_title("Execution time (median)", pad=14)
    ax.set_ylabel("ms")
    ax.set_ylim(0, max(t.values()) * 1.15)
    ax = axes[1]
    ax.axhline(1, color=MUTED, linewidth=1, linestyle=":")
    ax.plot(xs, speed, marker="o", color=ORANGE)
    label_points(ax, xs, speed, lambda v: f"{v:.2f}")
    ax.set_title("Speed-up against 1 worker", pad=14)
    ax.set_ylim(0, 1.4)
    for ax in axes:
        thread_axis(ax, xs)
        ax.set_xlabel("workers")
    save(fig, "task1c.png")
    return t


def compare(t1a, t1c):
    xs = list(t1a)
    fig, ax = plt.subplots(figsize=(6.5, 3.6))
    ax.plot(xs, [t1a[x] for x in xs], marker="o", color=BLUE, label="Task 1a (threads)")
    ax.plot(xs, [t1c[x] for x in xs], marker="s", color=ORANGE, label="Task 1c (workers)")
    ax.text(xs[-1] * 1.08, t1a[xs[-1]], "1a", color=INK, va="center", fontsize=8.5)
    ax.text(xs[-1] * 1.08, t1c[xs[-1]], "1c", color=INK, va="center", fontsize=8.5)
    ax.set_ylabel("ms (median)")
    ax.set_ylim(0, max(t1c.values()) * 1.1)
    ax.set_title("Task 1a against Task 1c", pad=14)
    thread_axis(ax, xs)
    ax.legend(frameon=False, loc="upper left", fontsize=8)
    save(fig, "task1a_vs_1c.png")


def task2(rows):
    t = medians(rows, "time_ms")
    ws = medians(rows, "peak_ws_mb")
    xs = list(t)
    speed = [t[1] / t[x] for x in xs]
    fig, axes = plt.subplots(1, 3, figsize=(11, 3.3))
    ax = axes[0]
    ax.plot(xs, [t[x] / 1000 for x in xs], marker="o", color=AQUA)
    label_points(ax, xs, [t[x] / 1000 for x in xs], lambda v: f"{v:.2f} s")
    ax.set_title("Execution time (median)")
    ax.set_ylabel("s")
    ax.set_ylim(0, 14)
    ax.set_xlim(0.8, 5)
    ax = axes[1]
    ax.plot(xs, xs, color=MUTED, linewidth=1, linestyle=":")
    ax.text(2.6, 3.0, "ideal", fontsize=7.5, color=INK_2)
    ax.plot(xs, speed, marker="o", color=AQUA)
    label_points(ax, xs, speed, lambda v: f"{v:.2f}", offset=-12)
    ax.set_title("Speed-up against 1 thread")
    ax.set_ylim(0, 4.5)
    ax = axes[2]
    ax.plot(xs, [ws[x] for x in xs], marker="o", color=AQUA)
    label_points(ax, xs, [ws[x] for x in xs], lambda v: f"{v:.0f}")
    ax.set_title("Peak working set (median)")
    ax.set_ylabel("MB")
    ax.set_ylim(0, 450)
    for ax in axes:
        thread_axis(ax, xs, refs=False)
    save(fig, "task2.png")


def breakdown(rows2, t1a_single):
    one = [r for r in rows2 if int(r["threads"]) == 1]
    read = statistics.median(float(notes(r)["read_ms_sum"]) for r in one)
    parse = statistics.median(float(notes(r)["parse_ms_sum"]) for r in one)
    fig, ax = plt.subplots(figsize=(8, 2.4))
    y_usb, y_1a = 1, 0
    ax.barh(y_usb, read, color=AQUA, height=0.5, edgecolor="white", linewidth=2,
            label="reading from the USB drive")
    ax.barh(y_usb, parse, left=read, color=BLUE, height=0.5, edgecolor="white", linewidth=2,
            label="parsing")
    ax.barh(y_1a, t1a_single, color=BLUE, height=0.5, edgecolor="white", linewidth=2)
    ax.text(read / 2, y_usb, f"reading  {read:,.0f} ms", ha="center", va="center", color="white",
            fontsize=8.5, fontweight="bold")
    ax.text(read + parse + 150, y_usb, f"parsing {parse:,.0f} ms", va="center", color=INK,
            fontsize=8.5)
    ax.text(t1a_single + 150, y_1a, f"{t1a_single:,.0f} ms (cached file, reading + parsing)",
            va="center", color=INK, fontsize=8.5)
    ax.set_yticks([y_1a, y_usb])
    ax.set_yticklabels(["Task 1a, 1 thread", "Task 2, 1 thread"])
    ax.set_xlabel("ms (median)")
    ax.set_xlim(0, 13500)
    ax.grid(axis="y", visible=False)
    ax.legend(frameon=False, loc="lower right", fontsize=8)
    save(fig, "task2_breakdown.png")


def main():
    t1 = {k: [r for r in load(k) if r["timestamp"].startswith(TASK1_DATE)] for k in ("1a", "1b", "1c")}
    rows2 = [r for r in load("2") if r["variant"] == TASK2_VARIANT]
    t1a = task1a(t1["1a"])
    task1b(t1["1b"])
    t1c = task1c(t1["1c"])
    compare(t1a, t1c)
    task2(rows2)
    breakdown(rows2, t1a[1])


if __name__ == "__main__":
    main()
