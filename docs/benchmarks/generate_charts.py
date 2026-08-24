#!/usr/bin/env python3
"""Generates the README benchmark charts (light + dark SVG pairs).

Edit VALUES below with per-query numbers taken from solo BenchmarkDotNet runs
(ReadBenchmark: Mean / 1000 queries; Range/Count: Mean / 100 queries), then run:

    python3 docs/benchmarks/generate_charts.py

Bar scale is linear per chart, anchored so the largest non-outlier bar is
MAX_BAR px wide. A row marked outlier=True is drawn clipped at CLIP_BAR px with
a break mark, for values that would dwarf every other bar.
"""

import os

# (label, value, unit_suffix, is_drydb, is_outlier)
VALUES = {
    "point_lookup": {
        "title": "Point lookup",
        "subtitle": "Find one value by key, 10,000 rows — time per query (lower is better)",
        "aria": "Point lookup benchmark",
        "rows": [
            ("DryDB", 17, "17 ns", True, False),
            ("LMDB (LightningDB)", 49, "49 ns", False, False),
            ("RocksDB", 242, "242 ns", False, False),
            ("SQLite (CsSqlite, prepared + immutable)", 535, "535 ns", False, False),
            ("SQLite (CsSqlite, default)", 4218, "4,218 ns", False, True),
        ],
    },
    "range_scan": {
        "title": "Range scan",
        "subtitle": "Read 100 consecutive rows by key range — time per query (lower is better)",
        "aria": "Range scan benchmark",
        "rows": [
            ("DryDB", 0.41, "0.4 µs", True, False),
            ("LMDB (LightningDB)", 1.0, "1.0 µs", False, False),
            ("SQLite (CsSqlite, prepared + immutable)", 5.7, "5.7 µs", False, False),
            ("RocksDB", 8.7, "8.7 µs", False, False),
            ("SQLite (CsSqlite, default)", 10.2, "10.2 µs", False, False),
        ],
    },
    "count_range": {
        "title": "Count by key range",
        "subtitle": "Count 8,000 rows in a key range — time per query (lower is better)",
        "aria": "Count by key range benchmark",
        "rows": [
            ("DryDB", 1.0, "1.0 µs", True, False),
            ("LMDB (LightningDB)", 74, "74 µs", False, False),
            ("SQLite (CsSqlite, prepared + immutable)", 83, "83 µs", False, False),
            ("SQLite (CsSqlite, default)", 88, "88 µs", False, False),
            ("RocksDB", 560, "560 µs", False, True),
        ],
    },
}

FOOTER = "BenchmarkDotNet · .NET 10 · Apple M-series · 10,000 rows (int64 key, 13-byte value) · 4 KB pages"

MAX_BAR = 361.22   # width of the largest non-outlier bar
CLIP_BAR = 416.0   # width of an outlier bar (drawn with a break mark)
MIN_BAR = 2.0
BAR_X = 262.0
ROW_PITCH = 37
FIRST_BAR_Y = 64

THEMES = {
    "light": dict(bg="#fcfcfb", border="#e6e5e0", title="#0b0b0b", sub="#52514e",
                  axis="#d8d7d2", label="#52514e", value="#0b0b0b",
                  drydb="#2a78d6", other="#7c7b74", footer="#8a897f"),
    "dark": dict(bg="#1a1a19", border="#33322f", title="#ffffff", sub="#c3c2b7",
                 axis="#3a3936", label="#c3c2b7", value="#ffffff",
                 drydb="#3987e5", other="#8d8c82", footer="#8a897f"),
}


def bar_path(y0: float, width: float) -> str:
    r = min(4.0, width)
    v = 22 - 2 * r
    return (f'<path d="M{BAR_X:g} {y0:g} h{width:.2f} '
            f'a{r:g} {r:g} 0 0 1 {r:g} {r:g} v{v:g} a{r:g} {r:g} 0 0 1 -{r:g} {r:g} '
            f'h-{width:.2f} Z"')


def render(name: str, chart: dict, theme: dict) -> str:
    non_outlier_max = max(v for _, v, _, _, outlier in chart["rows"] if not outlier)
    scale = MAX_BAR / non_outlier_max

    row_count = len(chart["rows"])
    axis_bottom = FIRST_BAR_Y + (row_count - 1) * ROW_PITCH + 28  # matches the original 4-row geometry
    height = axis_bottom + 39
    lines = [
        f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 800 {height}" width="800" height="{height}" '
        f'role="img" aria-label="{chart["aria"]}">',
        f'  <rect x="0.5" y="0.5" width="799" height="{height - 1}" rx="8" fill="{theme["bg"]}" stroke="{theme["border"]}"/>',
        '  <g font-family="ui-sans-serif, -apple-system, \'Segoe UI\', Helvetica, Arial, sans-serif">',
        f'    <text x="24" y="30" font-size="15" font-weight="600" fill="{theme["title"]}">{chart["title"]}</text>',
        f'    <text x="24" y="48" font-size="11.5" fill="{theme["sub"]}">{chart["subtitle"]}</text>',
        f'    <line x1="{BAR_X:g}" y1="58" x2="{BAR_X:g}" y2="{axis_bottom}" stroke="{theme["axis"]}" stroke-width="1"/>',
    ]

    for i, (label, value, value_text, is_drydb, is_outlier) in enumerate(chart["rows"]):
        y0 = FIRST_BAR_Y + i * ROW_PITCH
        cy = y0 + 11
        fill = theme["drydb"] if is_drydb else theme["other"]
        lines.append(
            f'  <text x="250" y="{cy}" text-anchor="end" dominant-baseline="central" '
            f'font-size="12" fill="{theme["label"]}">{label}</text>')

        if is_outlier:
            lines.append(f'  {bar_path(y0, CLIP_BAR)} fill="{fill}"/>')
            lines.append(f'  <path d="M606.4 {y0 - 2:g} l-6 26 h7 l6 -26 Z" fill="{theme["bg"]}"/>')
            text_x = 690.0
        else:
            width = max(MIN_BAR, value * scale)
            lines.append(f'  {bar_path(y0, width)} fill="{fill}"/>')
            text_x = BAR_X + width + min(4.0, width) + 8

        lines.append(
            f'  <text x="{text_x:g}" y="{cy}" dominant-baseline="central" font-size="12" '
            f'font-weight="600" fill="{theme["value"]}">{value_text}</text>')

    lines += [
        f'    <text x="24" y="{axis_bottom + 27}" font-size="10.5" fill="{theme["footer"]}">{FOOTER}</text>',
        '  </g>',
        '</svg>',
        '',
    ]
    return "\n".join(lines)


def main() -> None:
    out_dir = os.path.dirname(os.path.abspath(__file__))
    for name, chart in VALUES.items():
        for theme_name, theme in THEMES.items():
            path = os.path.join(out_dir, f"{name}_{theme_name}.svg")
            with open(path, "w", encoding="utf-8") as f:
                f.write(render(name, chart, theme))
            print(f"wrote {path}")


if __name__ == "__main__":
    main()
