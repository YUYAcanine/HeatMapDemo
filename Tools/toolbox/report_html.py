"""Tools/ の解析の実行ファイルが共通で使う HTML レポートの部品(色・表・折れ線・スタイル)と CSV の書き出し。直接は実行しない.

色は dataviz の既定のパレット(系列の色は物体・人物ごとに固定)。明るい / 暗い表示の両方に対応する。
"""

from __future__ import annotations

import csv
import html
from pathlib import Path

import numpy as np

# 対象の色(固定の順。対象物体 → 相手の人物 の順に割り当てる)
SERIES_LIGHT = ["#2a78d6", "#eb6834", "#1baf7a", "#eda100", "#e87ba4", "#008300", "#6250d6", "#e34948"]
SERIES_DARK = ["#3987e5", "#d95926", "#199e70", "#c98500", "#d55181", "#008300", "#9085e9", "#e66767"]


def pct(v: float) -> str:
    return "—" if v is None or np.isnan(v) else f"{v:.0%}"


def num(v: float, digits: int = 1) -> str:
    return "—" if v is None or np.isnan(v) else f"{v:.{digits}f}"


def table_html(header: list[str], rows: list[list[str]], first_is_label: bool = True) -> str:
    head = "".join(f"<th>{h}</th>" for h in header)
    body = "".join("<tr>" + "".join(f"<td>{c}</td>" if i or not first_is_label else f"<th scope='row'>{c}</th>"
                                    for i, c in enumerate(row)) + "</tr>" for row in rows)
    return f"<div class='table-wrap'><table><thead><tr>{head}</tr></thead><tbody>{body}</tbody></table></div>"


def legend(names: list[str], color_of: dict[str, int]) -> str:
    return "<div class='legend'>" + "".join(
        f"<span><i style='background:var(--series-{color_of[n] + 1})'></i>{html.escape(n)}</span>" for n in names) + "</div>"


def hover_line(name: str, values: np.ndarray, unit: str, digits: int) -> dict:
    """timelineHover でマウスを乗せたときに出す値"""
    return {"name": name, "unit": unit, "digits": digits,
            "values": [None if not np.isfinite(v) else round(float(v), digits) for v in values]}


def nice_range(values: list[np.ndarray], step: float, low: float, high: float, clip: tuple[float, float]) -> tuple[float, float]:
    """値の 1〜99% が入る範囲を step の倍数に広げる(少なくとも low〜high、clip の外には出さない)"""
    finite = np.concatenate([v[np.isfinite(v)] for v in values] + [np.array([low, high])])
    lo = max(np.floor(min(np.percentile(finite, 1), low) / step) * step, clip[0])
    hi = min(np.ceil(max(np.percentile(finite, 99), high) / step) * step, clip[1])
    return float(lo), float(hi)


# 4b(注視スコア)の縦軸。7b・8b は値に合わせて決める
GAZE_SCALE = {"range": (0.0, 1.0), "ticks": (0, 0.5, 1), "digits": 2, "unit": ""}


def retro_chart(points: np.ndarray, series: list[dict], aria: str, scale: dict = GAZE_SCALE) -> str:
    """到達からさかのぼった値の折れ線(縦軸は scale の range。4b は 0〜1)。
    series: name / values / color(CSS の色)/ emphasized(太線・大きい点)/ dashed / label(線の右端に出す名前)"""
    width, height = 470, 230
    left, right, top, bottom = 40, 118, 14, 32
    plot_w, plot_h = width - left - right, height - top - bottom
    x0, x1 = float(points[0]), float(points[-1])
    y_lo, y_hi = scale["range"]
    x = lambda r: left + (r - x0) / max(x1 - x0, 1e-6) * plot_w
    y = lambda v: top + (y_hi - min(max(v, y_lo), y_hi)) / max(y_hi - y_lo, 1e-6) * plot_h
    parts = [f"<svg viewBox='0 0 {width} {height}' class='chart retro' role='img' aria-label='{html.escape(aria)}'>"]

    for v in scale["ticks"]:
        cls = "axis" if v == 0 and y_lo < 0 else "grid"
        parts.append(f"<line x1='{left}' x2='{left + plot_w}' y1='{y(v):.1f}' y2='{y(v):.1f}' class='{cls}'/>")
        parts.append(f"<text x='{left - 6}' y='{y(v) + 4:.1f}' class='tick' text-anchor='end'>{v:g}</text>")

    for r in points:
        parts.append(f"<text x='{x(r):.1f}' y='{top + plot_h + 18}' class='tick' text-anchor='middle'>{'到達' if r == 0 else f'{r:g}s'}</text>")

    parts.append(f"<line x1='{x(0):.1f}' x2='{x(0):.1f}' y1='{top}' y2='{top + plot_h}' class='axis' stroke-dasharray='3 3'/>")

    # 強調する線を最後に描いて前に出す
    labels = []

    for s in sorted(series, key=lambda s: s.get("emphasized", False)):
        values = s["values"]
        width_px = 3 if s.get("emphasized") else 1.5
        dash = " stroke-dasharray='5 4'" if s.get("dashed") else ""
        segment = []

        for r, v in list(zip(points, values)) + [(None, np.nan)]:
            if r is not None and np.isfinite(v):
                segment.append(f"{x(r):.1f},{y(v):.1f}")
                continue

            if len(segment) > 1:
                parts.append(f"<polyline points='{' '.join(segment)}' fill='none' stroke='{s['color']}' stroke-width='{width_px}' "
                             f"stroke-linejoin='round' stroke-linecap='round'{dash}/>")
            segment = []

        for r, v in zip(points, values):
            if np.isfinite(v):
                radius = 4.5 if s.get("emphasized") else 3.5
                parts.append(f"<circle cx='{x(r):.1f}' cy='{y(v):.1f}' r='{radius}' fill='{s['color']}' stroke='var(--surface)' stroke-width='2'>"
                             f"<title>{html.escape(s['name'])} {'到達' if r == 0 else f'{r:g} 秒'}: "
                             f"{v:.{scale['digits']}f}{scale['unit']}</title></circle>")

        last = next((v for v in reversed(values) if np.isfinite(v)), None)

        if last is not None:
            labels.append([y(last), s.get("label", s["name"]), s.get("emphasized", False)])

    # 線の右端の名前(重ならないように 13px 以上離す)
    labels.sort(key=lambda item: item[0])

    for i in range(1, len(labels)):
        labels[i][0] = max(labels[i][0], labels[i - 1][0] + 13)

    # 下にはみ出したら、グラフの下端から上へ詰め直す(目盛りの「到達」と重ならないように)
    for i in range(len(labels) - 1, -1, -1):
        limit = top + plot_h - 4 if i == len(labels) - 1 else labels[i + 1][0] - 13
        labels[i][0] = min(labels[i][0], limit)

    for label_y, text, emphasized in labels:
        weight = " font-weight='600'" if emphasized else ""
        parts.append(f"<text x='{left + plot_w + 8}' y='{label_y + 4:.1f}' class='label'{weight}>{html.escape(text)}</text>")

    parts.append("</svg>")
    return "".join(parts)


STYLE = """
:root { color-scheme: light;
  --surface: #fcfcfb; --page: #f9f9f7; --text: #0b0b0b; --text-2: #52514e; --muted: #898781;
  --grid: #e1e0d9; --axis: #c3c2b7; --border: rgba(11,11,11,0.10); %LIGHT% }
@media (prefers-color-scheme: dark) { :root:not([data-theme="light"]) { color-scheme: dark;
  --surface: #1a1a19; --page: #0d0d0d; --text: #f0efec; --text-2: #c3c2b7; --muted: #898781;
  --grid: #2c2c2a; --axis: #383835; --border: rgba(255,255,255,0.10); %DARK% } }
:root[data-theme="dark"] { color-scheme: dark;
  --surface: #1a1a19; --page: #0d0d0d; --text: #f0efec; --text-2: #c3c2b7; --muted: #898781;
  --grid: #2c2c2a; --axis: #383835; --border: rgba(255,255,255,0.10); %DARK% }
body { margin: 0; background: var(--page); color: var(--text); font-family: system-ui, -apple-system, "Segoe UI", sans-serif; line-height: 1.5; }
main { max-width: 1000px; margin: 0 auto; padding: 24px 16px 64px; }
h1 { font-size: 1.4rem; margin: 0 0 4px; } h2 { font-size: 1.15rem; margin: 40px 0 8px; border-top: 1px solid var(--grid); padding-top: 24px; }
h3 { font-size: 1rem; margin: 24px 0 6px; } p.note, .meta { color: var(--text-2); font-size: 0.9rem; margin: 4px 0 10px; }
.card { background: var(--surface); border: 1px solid var(--border); border-radius: 8px; padding: 12px 14px; margin: 8px 0; position: relative; }
.table-wrap { overflow-x: auto; } table { border-collapse: collapse; font-size: 0.88rem; font-variant-numeric: tabular-nums; }
th, td { padding: 4px 10px; text-align: right; border-bottom: 1px solid var(--grid); white-space: nowrap; }
thead th { color: var(--text-2); font-weight: 600; } tbody th { text-align: left; font-weight: 500; }
.chart { width: 100%; height: auto; display: block; } .chart text { font-size: 12px; }
.label { fill: var(--text); } .value { fill: var(--text-2); } .tick { fill: var(--muted); }
.grid { stroke: var(--grid); stroke-width: 1; } .axis { stroke: var(--axis); stroke-width: 1; } .cross { stroke: var(--text-2); stroke-width: 1; }
h4 { font-size: 0.92rem; margin: 16px 0 4px; color: var(--text-2); }
.chart.retro { max-width: 520px; }
.multiples { display: grid; grid-template-columns: repeat(auto-fill, minmax(min(100%, 340px), 1fr)); gap: 10px; }
.multiples .card { margin: 0; } .card-title { font-size: 0.85rem; font-weight: 600; margin-bottom: 4px; }
.multiples .card.mean { border-color: var(--axis); border-width: 2px; }
.legend { display: flex; flex-wrap: wrap; gap: 14px; font-size: 0.85rem; color: var(--text-2); margin: 4px 0 6px; }
.legend i { display: inline-block; width: 10px; height: 10px; border-radius: 2px; margin-right: 5px; vertical-align: -1px; }
.tooltip { position: absolute; pointer-events: none; background: var(--surface); border: 1px solid var(--border); border-radius: 6px;
  padding: 6px 8px; font-size: 0.82rem; box-shadow: 0 2px 8px rgba(0,0,0,0.12); white-space: nowrap; }
"""


SCRIPT = """
function timelineHover(id, d) {
  const svg = document.getElementById(id), tip = document.getElementById(id + '-tip');
  const cross = svg.querySelector('.cross'), hit = svg.querySelector('.hit');
  const show = (ev) => {
    const box = svg.getBoundingClientRect(), sx = (ev.clientX - box.left) / box.width * d.width;
    const t = d.t0 + (sx - d.left) / d.plotW * (d.t1 - d.t0);
    let i = 0, lo = 0, hi = d.time.length - 1;
    while (lo <= hi) { const m = (lo + hi) >> 1; if (d.time[m] <= t) { i = m; lo = m + 1; } else hi = m - 1; }
    cross.setAttribute('x1', sx); cross.setAttribute('x2', sx); cross.setAttribute('visibility', 'visible');
    let text = `<b>${d.time[i].toFixed(1)} 秒</b><br>注視: ${d.present[i] ? d.target[i] : '映っていない'}`;
    for (const l of d.lines) text += `<br>${l.name}: ${l.values[i] == null ? '—' : l.values[i].toFixed(l.digits) + ' ' + l.unit}`;
    tip.innerHTML = text; tip.hidden = false;
    const card = svg.parentElement.getBoundingClientRect();
    let left = ev.clientX - card.left + 14; if (left + tip.offsetWidth > card.width) left = ev.clientX - card.left - tip.offsetWidth - 14;
    tip.style.left = left + 'px'; tip.style.top = (ev.clientY - card.top + 10) + 'px';
  };
  hit.addEventListener('mousemove', show);
  hit.addEventListener('mouseleave', () => { tip.hidden = true; cross.setAttribute('visibility', 'hidden'); });
}
"""


def write_csv(path: Path, rows: list[dict]) -> None:
    with path.open("w", encoding="utf-8-sig", newline="") as f:
        if not rows:
            f.write("")
            return

        writer = csv.DictWriter(f, fieldnames=list(rows[0]))
        writer.writeheader()

        for row in rows:
            writer.writerow({k: (f"{v:.4f}" if isinstance(v, float) and np.isfinite(v) else ("" if isinstance(v, float) else v))
                             for k, v in row.items()})
