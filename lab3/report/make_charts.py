"""Draws the charts of the report from lab3/results/measurements.json into lab3/report/figures.

The measurements are made by the tests (python lab3/tests/test_chat.py measure).

    python lab3/report/make_charts.py
"""
import json
from pathlib import Path

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt

HERE = Path(__file__).resolve().parent
RESULTS = HERE.parent / "results" / "measurements.json"
FIGURES = HERE / "figures"

ALIVE_EVERY, GONE_AFTER, CHECK_EVERY = 5.0, 15.0, 0.2
ETHERNET_PAYLOAD = 1472  # 1500-byte MTU - 20 (IPv4 header) - 8 (UDP header)
MAX_DATAGRAM = 4096

# The palette of lab 1: validated categorical slots (light mode), ink colours and a recessive grid.
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


def killed_peer(data):
    """When a killed peer is noticed: a timeline of one case, and the measured delays."""
    fig, (timeline, measured) = plt.subplots(1, 2, figsize=(7.2, 2.6), gridspec_kw={"width_ratios": [1.6, 1]})

    # One case: alives at 0, 5, 10; killed at 12.5; noticed at 10 + 15 = 25, 12.5 s after the kill.
    last_alive, kill = 10.0, 12.5
    for t in (0, 5, 10):
        timeline.annotate("", xy=(t, 0.55), xytext=(t, 0.15),
                          arrowprops=dict(arrowstyle="-|>", color=BLUE, lw=1.4))
    timeline.text(0, 0.62, "alive", ha="center", color=BLUE, fontsize=8)
    timeline.axvline(kill, color=ORANGE, lw=1.4)
    timeline.text(kill, 0.92, "killed", ha="center", color=ORANGE, fontsize=8)
    timeline.axvspan(last_alive, last_alive + GONE_AFTER, ymin=0.05, ymax=0.12, color=GRID)
    timeline.text(kill + (last_alive + GONE_AFTER - kill) / 2, 0.16, "15 s without any datagram", ha="center",
                  color=INK_2, fontsize=7.5)
    timeline.axvline(last_alive + GONE_AFTER, color=AQUA, lw=1.4)
    timeline.text(last_alive + GONE_AFTER, 0.92, "noticed", ha="center", color=AQUA, fontsize=8)
    timeline.annotate("", xy=(last_alive + GONE_AFTER, 0.78), xytext=(kill, 0.78),
                      arrowprops=dict(arrowstyle="<->", color=INK_2, lw=1))
    timeline.text((kill + last_alive + GONE_AFTER) / 2, 0.82, f"{last_alive + GONE_AFTER - kill:.1f} s", ha="center",
                  color=INK, fontsize=8)
    timeline.set_xlim(-1, 27)
    timeline.set_ylim(0, 1)
    timeline.set_yticks([])
    timeline.set_xlabel("time (s)")
    timeline.set_title("One case: killed 2.5 s after its last alive")
    timeline.grid(False)

    delays = sorted(data["killed_detected_s"])
    measured.axhspan(GONE_AFTER - ALIVE_EVERY, GONE_AFTER + CHECK_EVERY, color=GRID, label="possible: 10 to 15.2 s")
    measured.plot(range(1, len(delays) + 1), delays, "o", color=ORANGE, label="measured")
    measured.set_ylim(8, 17)
    measured.set_xticks(range(1, len(delays) + 1))
    measured.set_xlabel("killed peer (sorted)")
    measured.set_ylabel("noticed after the kill (s)")
    measured.set_title(f"{len(delays)} kills at random moments")
    measured.legend(loc="lower right", fontsize=7, frameon=False)

    fig.tight_layout()
    fig.savefig(FIGURES / "killed_peer.png")
    plt.close(fig)


def datagram_sizes(data):
    """The size of every kind of datagram, against one Ethernet frame and the limit of the chat."""
    rows = [(f"{kind}", size, BLUE) for kind, size in sorted(data["size_by_kind"].items(), key=lambda kv: kv[1])]
    for alphabet, sizes in data["size_500_chars"].items():
        rows.append((f"chat, 500 {alphabet}", sizes["general"], AQUA))
        rows.append((f"private, 500 {alphabet}", sizes["private"], ORANGE))
    labels, values, colors = zip(*rows)

    fig, ax = plt.subplots(figsize=(7.2, 3.4))
    bars = ax.barh(range(len(rows)), values, color=colors, height=0.65)
    for bar, value in zip(bars, values):
        ax.text(value + 25, bar.get_y() + bar.get_height() / 2, f"{value}", va="center", fontsize=7.5, color=INK)
    ax.axvline(ETHERNET_PAYLOAD, color=INK_2, lw=1.2, ls="--")
    ax.text(ETHERNET_PAYLOAD + 25, -1, "1472: one Ethernet frame", fontsize=7.5, color=INK_2, va="center")
    ax.axvline(MAX_DATAGRAM, color=MUTED, lw=1.2, ls=":")
    ax.text(MAX_DATAGRAM - 25, -1, "4096: dropped above", fontsize=7.5, color=MUTED, ha="right", va="center")
    ax.set_yticks(range(len(rows)))
    ax.set_yticklabels(labels, fontsize=8)
    ax.set_ylim(len(rows) - 0.5, -1.6)  # top to bottom, with a row above the bars for the two labels
    ax.set_xlim(0, MAX_DATAGRAM + 100)
    ax.set_xlabel("UDP payload (bytes); sender name 'Ana'")
    ax.set_title("Size of the datagrams")
    ax.grid(axis="y", visible=False)
    fig.tight_layout()
    fig.savefig(FIGURES / "datagram_sizes.png")
    plt.close(fig)


def main():
    FIGURES.mkdir(parents=True, exist_ok=True)
    data = json.loads(RESULTS.read_text(encoding="utf-8"))
    killed_peer(data)
    datagram_sizes(data)
    print(f"charts written to {FIGURES}")


if __name__ == "__main__":
    main()
