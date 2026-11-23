"""Draws the charts of the report from lab4/results/measurements.json into lab4/report/figures.

The measurements are made by the tests (python lab4/tests/test_broker.py measure).

    python lab4/report/make_charts.py
"""
import json
import statistics
from pathlib import Path

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt

HERE = Path(__file__).resolve().parent
RESULTS = HERE.parent / "results" / "measurements.json"
FIGURES = HERE / "figures"

# The palette of labs 1 and 3: validated categorical slots (light mode), ink colours and a recessive grid.
BLUE, ORANGE = "#2a78d6", "#eb6834"
INK, INK_2, MUTED, GRID = "#0b0b0b", "#52514e", "#8a8984", "#e4e3df"
COLOR = {"http": BLUE, "grpc": ORANGE}
LABEL = {"http": "Part 1: HTTP + JSON", "grpc": "Part 2: gRPC + Protobuf"}

plt.rcParams.update({
    "font.size": 9, "axes.titlesize": 10, "axes.titleweight": "bold",
    "axes.edgecolor": MUTED, "axes.labelcolor": INK_2, "axes.titlecolor": INK,
    "xtick.color": INK_2, "ytick.color": INK_2, "axes.grid": True,
    "grid.color": GRID, "grid.linewidth": 0.6, "axes.axisbelow": True,
    "axes.spines.top": False, "axes.spines.right": False,
    "lines.linewidth": 2, "lines.markersize": 5, "savefig.dpi": 200,
})


def series(rows, kind, value):
    """For each number of publishers: the median of the repeats, and the lowest and highest."""
    counts = sorted({r["publishers"] for r in rows if r["transport"] == kind})
    medians, low, high = [], [], []
    for n in counts:
        values = [value(r) for r in rows if r["transport"] == kind and r["publishers"] == n]
        m = statistics.median(values)
        medians.append(m)
        low.append(m - min(values))
        high.append(max(values) - m)
    return counts, medians, [low, high]


def throughput(rows):
    fig, ax = plt.subplots(figsize=(3.5, 2.6))
    for kind in ("http", "grpc"):
        x, y, err = series(rows, kind, lambda r: r["publishesPerSecond"])
        ax.errorbar(x, y, yerr=err, marker="o", color=COLOR[kind], label=LABEL[kind], capsize=3)
    ax.set_xscale("log", base=2)
    ax.set_xticks([1, 2, 4, 8, 16], ["1", "2", "4", "8", "16"])
    ax.set_ylim(bottom=0)
    ax.set_xlabel("publishers at the same time (one topic)")
    ax.set_ylabel("messages published per second")
    ax.set_title("Throughput")
    ax.legend(fontsize=7, frameon=False, loc="lower right")
    fig.tight_layout()
    fig.savefig(FIGURES / "throughput.png")
    plt.close(fig)


def latency(rows):
    """From a publish to its answer. (The subscriber gets the message at the same moment, see the printed table.)"""
    fig, ax = plt.subplots(figsize=(3.5, 2.6))
    for kind in ("http", "grpc"):
        x, y, err = series(rows, kind, lambda r: r["publishMs"]["p50"])
        ax.errorbar(x, y, yerr=err, marker="o", color=COLOR[kind], label=f"{LABEL[kind].split(':')[0]}, median", capsize=3)
    for kind in ("http", "grpc"):
        x, y, _ = series(rows, kind, lambda r: r["publishMs"]["p99"])
        ax.plot(x, y, linestyle="--", marker="^", color=COLOR[kind], lw=1.2, label=f"{LABEL[kind].split(':')[0]}, 99th pct.")
    ax.set_xscale("log", base=2)
    ax.set_xticks([1, 2, 4, 8, 16], ["1", "2", "4", "8", "16"])
    ax.set_xlabel("publishers at the same time (one topic)")
    ax.set_ylabel("milliseconds")
    ax.set_ylim(bottom=0)
    ax.set_title("Publish: request to answer")
    ax.legend(fontsize=7, frameon=False, loc="upper left")
    fig.tight_layout()
    fig.savefig(FIGURES / "latency.png")
    plt.close(fig)


def main():
    rows = json.loads(RESULTS.read_text(encoding="utf-8"))
    FIGURES.mkdir(exist_ok=True)
    throughput(rows)
    latency(rows)
    for kind in ("http", "grpc"):
        for n in sorted({r["publishers"] for r in rows}):
            rs = [r for r in rows if r["transport"] == kind and r["publishers"] == n]
            print(f"{kind:4} {n:2} publishers: {statistics.median(r['publishesPerSecond'] for r in rs):7.1f} per s, "
                  f"publish p50 {statistics.median(r['publishMs']['p50'] for r in rs):6.2f} ms "
                  f"p99 {statistics.median(r['publishMs']['p99'] for r in rs):6.2f} ms, "
                  f"delivery p50 {statistics.median(r['deliveryMs']['p50'] for r in rs):6.2f} ms, "
                  f"all checks {all(r['idsUniqueAndContiguous'] and r['receivedOnceInOrder'] for r in rs)}")
    print(f"charts written to {FIGURES}")


if __name__ == "__main__":
    main()
