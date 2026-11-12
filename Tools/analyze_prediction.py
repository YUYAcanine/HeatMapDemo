"""1人分のデータで「この先 --horizon 秒(既定 5 秒)以内に、どの物体へ到達するか」を予測できるかを調べ、図にする。

analyze_arrival.py と同じ特徴量・モデル(toolbox/arrival_model.py)を使い、1人に絞って次を出す:
  1 到達前の信号の時間変化 … 到達の 10 秒前から、到達した物体と他の候補の物体で
        距離・近づく速さ・体の向き・注視可能性がどう違うか。どの信号が何秒前から到達先を区別するか(効果量)
  2 何秒前から当たるか   … リアルタイムの予測(その人の他の時間で学習)で、到達の L 秒前に到達先の確率が1位だった割合
  3 実際の到達場面        … 到達の前後の入力(距離・注視可能性・体の向き)と、そこから出した「5 秒以内に到達する確率」
  4 仮説の検定            … 体の向き・視線を足すと、早い段階(到達の 3〜5 秒前)の予測が良くなるか(到達ごとの対応のある比較)と、
                            はっきりさせるのに必要な到達の数の目安
モデル: A 空間のみ / B 空間 + 視線 / C 空間 + 視線 + 体の向き / D 視線パターンのみ(analyze_arrival.py と同じ)。

書き出すもの(毎回上書き。<データの場所>Reports/Prediction_<実験の名前>_<実験対象者>_<人物>/):
  report.html / aligned_signals.csv / lead_accuracy.csv / predictions.csv / metrics.csv / hypothesis_tests.csv

使い方:
  python Tools/analyze_prediction.py
      データの場所 → 書き出し → 人物 を一覧から番号で選ぶ
  python Tools/analyze_prediction.py --data-root assets --export Oono/sora --person child --horizon 5
numpy だけで動く。
"""

from __future__ import annotations

import argparse
import html
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
from analyze_arrival import (  # noqa: E402  同じフォルダの analyze_arrival.py
    BLOCKS, EXTRA_CONFIG, EXTRA_STYLE, MODEL_COLORS, blocks_of, candidates, choose_exports, fit_rows, list_exports, load_streams,
    method_section, realtime_samples,
)
from toolbox import home_data_root  # noqa: E402
from toolbox.arrival_model import (  # noqa: E402
    ARRIVAL_MODES, MODEL_FEATURES, MODEL_LABELS, Config, PersonStream, arrival_definition, auc, candidate_definition, logistic_proba,
    matrix,
)
from toolbox.export_data import choose  # noqa: E402
from toolbox.report_html import SCRIPT, SERIES_DARK, SERIES_LIGHT, STYLE, legend, num, pct, table_html, write_csv  # noqa: E402

ALIGN = np.round(np.arange(-10.0, 1e-6, 0.5), 3)
# 信号: 列名, 名前, 「到達先らしい」向き(+1 = 大きいほど到達先らしい), 小数の桁
SIGNALS = [("dist", "物体までの距離 (m)", -1, 2), ("approach", "近づく速さ(直前2秒の距離の変化, m)", -1, 2),
           ("body", "肩の正面から物体への角度 (度)", -1, 0), ("score", "注視可能性 (0〜1)", +1, 2)]
SHORT = {"dist": "距離", "approach": "近づく速さ", "body": "体の向き", "score": "注視可能性"}
SIGNAL_COLORS = {"dist": "var(--series-1)", "approach": "var(--series-7)", "body": "var(--series-2)", "score": "var(--series-3)"}
EXAMPLES = 8


# ------------------------------------------------------------
# 1 到達前の信号
# ------------------------------------------------------------
def aligned_signals(stream: PersonStream, horizon: float) -> list[dict]:
    """到達ごとに、到達の 10 秒前〜到達の各時刻での、候補の物体ごとの信号(候補 = 到達の horizon 秒前に到達しうる物体)"""
    rows = []

    for index, (arrived, t) in enumerate(stream.arrivals, 1):
        cand = candidates(stream, arrived, t - horizon)

        if len(cand) < 2:
            continue

        for r in ALIGN:
            tt = t + r

            for name in cand:
                d_now = stream.recent_mean(stream.dist[name], tt - 0.5, tt)
                rows.append({"event": index, "arriveSec": t, "relSec": float(r), "target": name, "arrived": int(name == arrived),
                             "dist": d_now, "approach": d_now - stream.recent_mean(stream.dist[name], tt - 2.5, tt - 2.0),
                             "body": stream.recent_mean(stream.body[name], tt - 0.5, tt),
                             "score": stream.mean_score(name, tt - 0.5, tt)})

    return rows


def paired_diff(rows: list[dict], signal: str, r: float) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    """時刻 r での、到達ごとの(到達した物体の値, 他の候補の平均, 差)"""
    a, o = [], []

    for event in sorted({x["event"] for x in rows}):
        group = [x for x in rows if x["event"] == event and x["relSec"] == r]
        av = [x[signal] for x in group if x["arrived"] and np.isfinite(x[signal])]
        ov = [x[signal] for x in group if not x["arrived"] and np.isfinite(x[signal])]

        if av and ov:
            a.append(av[0])
            o.append(np.mean(ov))

    a, o = np.array(a), np.array(o)
    return a, o, a - o


def bootstrap(values: np.ndarray, stat=np.mean, n: int = 2000) -> tuple[float, float]:
    if len(values) < 3:
        return np.nan, np.nan
    rng = np.random.default_rng(0)
    samples = [stat(values[rng.integers(0, len(values), len(values))]) for _ in range(n)]
    return float(np.percentile(samples, 2.5)), float(np.percentile(samples, 97.5))


def effect(d: np.ndarray, sign: int) -> float:
    """対応のある効果量 d_z = 差の平均 / 差の標準偏差(到達先らしい向きを + にする)"""
    return float(sign * d.mean() / d.std(ddof=1)) if len(d) >= 3 and d.std(ddof=1) > 1e-9 else np.nan


# ------------------------------------------------------------
# 2 リアルタイムの予測
# ------------------------------------------------------------
def oof_predictions(samples: list[dict]) -> dict[str, np.ndarray]:
    """モデルごとに、その人の時間を BLOCKS 個に分け、1 区間ずつ残りで学習して予測した確率"""
    block = blocks_of(samples)
    out = {}

    for model in MODEL_FEATURES:
        prob = np.full(len(samples), np.nan)

        for b in range(BLOCKS):
            test = np.flatnonzero(block == b)
            train = [samples[i] for i in np.flatnonzero(block != b)]

            if test.size and sum(r["label"] for r in train):
                scaler, w = fit_rows(train, model)
                prob[test] = logistic_proba(scaler(matrix([samples[i] for i in test], MODEL_FEATURES[model])), w)

        out[model] = prob

    return out


def average_precision(prob: np.ndarray, labels: np.ndarray) -> float:
    ok = np.isfinite(prob)
    p, y = prob[ok], labels[ok].astype(bool)

    if y.sum() == 0:
        return np.nan

    order = np.argsort(-p, kind="mergesort")
    hits = np.cumsum(y[order])
    return float(np.mean(hits[y[order]] / (np.flatnonzero(y[order]) + 1)))


def lead_table(stream: PersonStream, samples: list[dict], probs: dict[str, np.ndarray], leads: np.ndarray, step: float) -> dict:
    """到達の L 秒前の予測で、到達した物体の確率が候補の中で1位だったか(到達ごと・モデルごと)。
    返す値: {model: {L: [(event, hit, 候補の数, 到達した物体の確率 / 候補の確率の合計)]}}"""
    by_time: dict[float, dict[str, int]] = {}

    for i, r in enumerate(samples):
        by_time.setdefault(round(r["timeSec"], 3), {})[r["target"]] = i

    times = np.array(sorted(by_time))
    result = {m: {float(L): [] for L in leads} for m in MODEL_FEATURES}

    for index, (arrived, t) in enumerate(stream.arrivals, 1):
        for L in leads:
            k = np.searchsorted(times, t - L + 1e-6) - 1

            if k < 0 or t - L - times[k] > step:
                continue

            rows = by_time[times[k]]

            if arrived not in rows or len(rows) < 2:
                continue

            for m, prob in probs.items():
                p = {n: prob[i] for n, i in rows.items()}

                if not all(np.isfinite(v) for v in p.values()):
                    continue

                total = sum(p.values())
                result[m][float(L)].append((index, max(p, key=p.get) == arrived, len(p), p[arrived] / total if total > 0 else np.nan))

    return result


def wilson(k: int, n: int) -> tuple[float, float]:
    if n == 0:
        return np.nan, np.nan
    z, p = 1.96, k / n
    centre = (p + z * z / (2 * n)) / (1 + z * z / n)
    half = z * np.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / (1 + z * z / n)
    return centre - half, centre + half


# ------------------------------------------------------------
# HTML
# ------------------------------------------------------------
def xy_chart(xs: np.ndarray, series: list[dict], aria: str, x_label: str, y_label: str, y_range: tuple[float, float],
             width: int = 470, x_fmt=lambda v: f"{v:g}", zero: bool = False, invert: bool = False) -> str:
    """折れ線(band があれば 95% 区間の帯)。series: name / values / color / band(lo, hi)/ dashed / emphasized / label。
    invert のときは縦軸を上下逆にする(y_range の lo が上)"""
    height, left, right, top, bottom = 240, 46, 120, 16, 40
    plot_w, plot_h = width - left - right, height - top - bottom
    lo, hi = y_range
    x = lambda v: left + (v - xs[0]) / max(xs[-1] - xs[0], 1e-9) * plot_w
    if invert:
        y = lambda v: top + (min(max(v, lo), hi) - lo) / (hi - lo) * plot_h
    else:
        y = lambda v: top + (hi - min(max(v, lo), hi)) / (hi - lo) * plot_h
    parts = [f"<svg viewBox='0 0 {width} {height}' class='chart retro' role='img' aria-label='{html.escape(aria)}'>"]

    for v in np.linspace(lo, hi, 3):
        parts.append(f"<line x1='{left}' x2='{left + plot_w}' y1='{y(v):.1f}' y2='{y(v):.1f}' class='grid'/>"
                     f"<text x='{left - 6}' y='{y(v) + 4:.1f}' class='tick' text-anchor='end'>{v:.3g}</text>")

    if zero and lo < 0 < hi:
        parts.append(f"<line x1='{left}' x2='{left + plot_w}' y1='{y(0):.1f}' y2='{y(0):.1f}' class='axis'/>")

    ticks = xs if len(xs) <= 11 else xs[::max(1, len(xs) // 10)]
    for v in ticks:
        parts.append(f"<text x='{x(v):.1f}' y='{top + plot_h + 16}' class='tick' text-anchor='middle'>{x_fmt(v)}</text>")

    parts.append(f"<text x='{left + plot_w / 2:.1f}' y='{height - 6}' class='tick' text-anchor='middle'>{html.escape(x_label)}</text>")
    parts.append(f"<text x='{left}' y='{top - 5}' class='tick'>{html.escape(y_label)}</text>")
    labels = []

    for s in series:
        vals = np.asarray(s["values"], dtype=float)

        if "band" in s:
            blo, bhi = (np.asarray(b, dtype=float) for b in s["band"])
            ok = np.isfinite(blo) & np.isfinite(bhi)
            if ok.sum() > 1:
                upper = [f"{x(a):.1f},{y(b):.1f}" for a, b in zip(xs[ok], bhi[ok])]
                lower = [f"{x(a):.1f},{y(b):.1f}" for a, b in zip(xs[ok][::-1], blo[ok][::-1])]
                parts.append(f"<polygon points='{' '.join(upper + lower)}' fill='{s['color']}' fill-opacity='0.15' stroke='none'/>")

        pts = [(x(a), y(b)) for a, b in zip(xs, vals) if np.isfinite(b)]
        dash = " stroke-dasharray='5 4'" if s.get("dashed") else ""
        if len(pts) > 1:
            parts.append(f"<polyline points='{' '.join(f'{a:.1f},{b:.1f}' for a, b in pts)}' fill='none' stroke='{s['color']}' "
                         f"stroke-width='{2.6 if s.get('emphasized') else 1.7}' stroke-linejoin='round'{dash}/>")
        if pts:
            labels.append([pts[-1][1], s.get("label", s["name"])])

    labels.sort()
    for i in range(1, len(labels)):
        labels[i][0] = max(labels[i][0], labels[i - 1][0] + 13)
    for i in range(len(labels) - 1, -1, -1):
        labels[i][0] = min(labels[i][0], (top + plot_h if i == len(labels) - 1 else labels[i + 1][0] - 13))
    for ly, text in labels:
        parts.append(f"<text x='{left + plot_w + 8}' y='{ly + 4:.1f}' class='label'>{html.escape(text)}</text>")

    parts.append("</svg>")
    return "".join(parts)


def schematic(horizon: float, window: float) -> str:
    """予測のしくみ: 時刻 t で、過去 window 秒のデータから、この先 horizon 秒以内の到達を当てる"""
    width, height = 720, 120
    left, right = 30, 30
    total = window + horizon + 2
    x = lambda s: left + (s + window + 1) / total * (width - left - right)
    parts = [f"<svg viewBox='0 0 {width} {height}' class='chart' role='img' aria-label='予測のしくみ'>",
             f"<rect x='{x(-window):.1f}' y='30' width='{x(0) - x(-window):.1f}' height='34' rx='4' fill='var(--series-1)' fill-opacity='0.18'/>",
             f"<text x='{(x(-window) + x(0)) / 2:.1f}' y='52' class='label' text-anchor='middle'>使うデータ: 過去 {window:g} 秒の視線・体の向き・距離</text>",
             f"<rect x='{x(0):.1f}' y='30' width='{x(horizon) - x(0):.1f}' height='34' rx='4' fill='var(--series-2)' fill-opacity='0.22'/>",
             f"<text x='{(x(0) + x(horizon)) / 2:.1f}' y='52' class='label' text-anchor='middle'>当てる範囲: {horizon:g} 秒以内</text>",
             f"<line x1='{x(-window - 1):.1f}' x2='{x(horizon + 1):.1f}' y1='80' y2='80' class='axis'/>",
             f"<line x1='{x(0):.1f}' x2='{x(0):.1f}' y1='22' y2='86' stroke='var(--text)' stroke-width='2'/>",
             f"<text x='{x(0):.1f}' y='102' class='label' text-anchor='middle' font-weight='600'>今 (t)</text>",
             f"<text x='{x(-window):.1f}' y='100' class='tick' text-anchor='middle'>t − {window:g} 秒</text>",
             f"<text x='{x(horizon):.1f}' y='100' class='tick' text-anchor='middle'>t + {horizon:g} 秒</text>",
             f"<text x='{x(0):.1f}' y='16' class='tick' text-anchor='middle'>0.5 秒ごとに、物体ごとの「{horizon:g} 秒以内に到達する確率」を出す</text></svg>"]
    return "".join(parts)


def episode_chart(stream: PersonStream, arrived: str, t: float, cand: list[str], samples: list[dict], probs: dict[str, np.ndarray],
                  horizon: float) -> str:
    """1回の到達の前後: 距離・注視可能性・体の向き(入力)と、そこから出した horizon 秒以内に到達する確率(出力)"""
    cfg = stream.cfg
    t0, t1 = t - horizon - cfg.window - 1, t + 2
    width, left, right, panel_h, gap = 720, 58, 110, 78, 22
    plot_w = width - left - right
    x = lambda v: left + (v - t0) / (t1 - t0) * plot_w
    panels = [("距離 (m。上 = 物体のそば)", "dist", (0.0, 3.0)), ("注視可能性", "score", (0.0, 1.0)),
              ("体の向き: 肩の正面から (度。上 = 物体を向いている)", "body", (0.0, 180.0)),
              (f"{horizon:g} 秒以内に到達する確率(実線 C / 破線 A)", "prob", (0.0, 1.0))]
    height = 18 + len(panels) * (panel_h + gap) + 20
    t_pred = t - horizon
    parts = [f"<svg viewBox='0 0 {width} {height}' class='chart' role='img' aria-label='到達の前後の入力と予測'>",
             f"<rect x='{x(t_pred - cfg.window):.1f}' y='14' width='{x(t_pred) - x(t_pred - cfg.window):.1f}' height='{height - 34}' "
             f"fill='var(--series-1)' fill-opacity='0.07'/>",
             f"<rect x='{x(t_pred):.1f}' y='14' width='{x(t) - x(t_pred):.1f}' height='{height - 34}' fill='var(--series-2)' fill-opacity='0.09'/>",
             f"<text x='{x(t_pred - cfg.window / 2):.1f}' y='10' class='tick' text-anchor='middle'>この {cfg.window:g} 秒のデータで予測</text>",
             f"<text x='{x(t_pred + horizon / 2):.1f}' y='10' class='tick' text-anchor='middle'>この {horizon:g} 秒に到達するか</text>"]
    color = {n: f"var(--series-{stream.targets.index(n) + 1})" for n in cand}
    idx = {(round(r["timeSec"], 3), r["target"]): i for i, r in enumerate(samples)}
    sample_times = np.array(sorted({round(r["timeSec"], 3) for r in samples}))
    times = sample_times[(sample_times >= t0) & (sample_times <= t1)]
    grid = np.arange(t0, t1 + 1e-6, 0.25)

    for k, (title, key, (lo, hi)) in enumerate(panels):
        top = 18 + k * (panel_h + gap)
        # 距離と体の向きは 0(物体のそば・物体を向いている)を上にする
        if key in ("dist", "body"):
            y = lambda v, top=top, lo=lo, hi=hi: top + (min(max(v, lo), hi) - lo) / (hi - lo) * panel_h
        else:
            y = lambda v, top=top, lo=lo, hi=hi: top + (hi - min(max(v, lo), hi)) / (hi - lo) * panel_h
        parts.append(f"<text x='{left}' y='{top - 4}' class='tick'>{html.escape(title)}</text>")
        parts.append(f"<line x1='{left}' x2='{left + plot_w}' y1='{y(lo):.1f}' y2='{y(lo):.1f}' class='axis'/>"
                     f"<line x1='{left}' x2='{left + plot_w}' y1='{y(hi):.1f}' y2='{y(hi):.1f}' class='grid'/>"
                     f"<text x='{left - 6}' y='{y(lo) + 4:.1f}' class='tick' text-anchor='end'>{lo:g}</text>"
                     f"<text x='{left - 6}' y='{y(hi) + 4:.1f}' class='tick' text-anchor='end'>{hi:g}</text>")
        end_values = []

        for name in cand:
            if key == "prob":
                for model, dash, w in (("A", " stroke-dasharray='4 3'", 1.3), ("C", "", 2.2)):
                    pts = [(x(tt), y(probs[model][idx[(tt, name)]])) for tt in times
                           if (tt, name) in idx and np.isfinite(probs[model][idx[(tt, name)]])]
                    if len(pts) > 1:
                        parts.append(f"<polyline points='{' '.join(f'{a:.1f},{b:.1f}' for a, b in pts)}' fill='none' "
                                     f"stroke='{color[name]}' stroke-width='{w}'{dash}/>")
                at = [tt for tt in times if tt <= t_pred + 1e-6 and (tt, name) in idx]
                if at:
                    v = probs["C"][idx[(at[-1], name)]]
                    if np.isfinite(v):
                        parts.append(f"<circle cx='{x(at[-1]):.1f}' cy='{y(v):.1f}' r='4' fill='{color[name]}' stroke='var(--surface)' stroke-width='1.5'/>")
                        end_values.append(f"{name} {v:.2f}")
                continue

            if key == "score":
                vals = [stream.mean_score(name, g - 0.25, g) for g in grid]
            else:
                series = stream.dist[name] if key == "dist" else stream.body[name]
                vals = [stream.recent_mean(series, g - 0.25, g) for g in grid]

            seg = []
            for g, v in list(zip(grid, vals)) + [(None, np.nan)]:
                if g is not None and np.isfinite(v):
                    seg.append(f"{x(g):.1f},{y(v):.1f}")
                    continue
                if len(seg) > 1:
                    parts.append(f"<polyline points='{' '.join(seg)}' fill='none' stroke='{color[name]}' stroke-width='{2.2 if name == arrived else 1.3}'/>")
                seg = []

        if key == "prob" and end_values:
            parts.append(f"<text x='{left + plot_w + 6}' y='{top + 12}' class='tick'>到達の {horizon:g} 秒前の</text>"
                         f"<text x='{left + plot_w + 6}' y='{top + 26}' class='tick'>予測(C):</text>")
            for i, text in enumerate(end_values):
                parts.append(f"<text x='{left + plot_w + 6}' y='{top + 42 + i * 14}' class='label'>{html.escape(text)}</text>")

    bottom = 18 + len(panels) * (panel_h + gap) - gap
    parts.append(f"<line x1='{x(t):.1f}' x2='{x(t):.1f}' y1='14' y2='{bottom}' stroke='var(--text)' stroke-width='1.5'/>"
                 f"<text x='{x(t):.1f}' y='{bottom + 14}' class='label' text-anchor='middle'>到達</text>"
                 f"<line x1='{x(t_pred):.1f}' x2='{x(t_pred):.1f}' y1='14' y2='{bottom}' stroke='var(--text-2)' stroke-dasharray='3 3'/>"
                 f"<text x='{x(t_pred):.1f}' y='{bottom + 14}' class='tick' text-anchor='middle'>{horizon:g} 秒前</text>")
    for s in range(-int(horizon + cfg.window), 3, 5):
        if t0 <= t + s <= t1 and s not in (0, -horizon):
            parts.append(f"<text x='{x(t + s):.1f}' y='{bottom + 14}' class='tick' text-anchor='middle'>{s:+d}s</text>")
    parts.append("</svg>")
    return "".join(parts)


# ------------------------------------------------------------
# main
# ------------------------------------------------------------
def main() -> None:
    defaults = Config()
    parser = argparse.ArgumentParser(description="1人分のデータで、この先数秒以内にどの物体へ到達するかを予測できるかを調べる")
    parser.add_argument("--export", help="使う書き出し(実験/実験対象者[/骨格]。省略すると一覧から選ぶ)")
    parser.add_argument("--person", help="人物(省略すると一覧から選ぶ)")
    parser.add_argument("--horizon", type=float, default=5.0, help="この先何秒以内の到達を当てるか")
    for name in ("sigma", "window", "high", "near", "leave", "step", "smooth"):
        parser.add_argument(f"--{name}", type=float, default=getattr(defaults, name))
    parser.add_argument("--arrival", choices=ARRIVAL_MODES, default=defaults.arrival,
                        help="到達の決め方: approach 近づいた / destination 終着点(近づいてとどまり、向くか見る)/ touch 手で触れた")
    for name in ("dwell", "face", "look", "touch_margin"):
        parser.add_argument(f"--{name.replace('_', '-')}", dest=name, type=float, default=getattr(defaults, name))
    home_data_root.add_argument(parser)
    args = parser.parse_args()

    root = home_data_root.choose_data_root(args.data_root)
    if root is None:
        return

    found = list_exports(root)
    if args.export:
        exports = [f for f in found if "/".join(f).startswith(args.export.strip("/"))][:1]
    else:
        chosen = choose_exports(found)
        exports = chosen[:1] if chosen else []
    if not exports:
        print("書き出しが選ばれていません。")
        return

    cfg = Config(horizon=args.horizon, **{k: getattr(args, k) for k in ("sigma", "window", "high", "near", "leave", "step", "smooth", *EXTRA_CONFIG)})
    print("\n読み込み:")
    streams = load_streams(root, exports, cfg, [args.person] if args.person else None)
    if not args.person and len(streams) > 1:
        key = choose("人物", [s.key for s in streams])
        streams = [s for s in streams if s.key == key]
    if not streams or not streams[0].arrivals:
        print("到達のある人物がいません。")
        return

    stream = streams[0]
    horizon = cfg.horizon
    suffix = "" if cfg.arrival == "approach" else f"_{cfg.arrival}"
    out_dir = home_data_root.report_root(root) / f"Prediction_{stream.experiment}_{stream.key.replace('/', '_')}{suffix}"
    out_dir.mkdir(exist_ok=True)

    # 1 到達前の信号
    aligned = aligned_signals(stream, horizon)
    n_events = len({r["event"] for r in aligned})
    signal_stats = {}
    for key, label, sign, _ in SIGNALS:
        stats = []
        for r in ALIGN:
            a, o, d = paired_diff(aligned, key, r)
            stats.append({"a": a.mean() if len(a) else np.nan, "o": o.mean() if len(o) else np.nan,
                          "a_ci": bootstrap(a), "o_ci": bootstrap(o), "dz": effect(d, sign),
                          "dz_ci": bootstrap(d, lambda v, s=sign: effect(v, s)), "n": len(d)})
        signal_stats[key] = stats

    # 2 予測
    print("予測 …")
    samples = realtime_samples(stream)
    probs = oof_predictions(samples)
    labels = np.array([r["label"] for r in samples])
    leads = np.round(np.arange(cfg.step, horizon + 1e-6, cfg.step), 3)
    leads_table = lead_table(stream, samples, probs, leads, cfg.step)

    metrics = []
    for m in MODEL_FEATURES:
        at_h = leads_table[m][float(leads[-1])]
        k, n = sum(h for _, h, _, _ in at_h), len(at_h)
        lo, hi = wilson(k, n)
        # 到達ごとに、到達の何秒前から(それより後はずっと)到達先が1位だったか
        early = []
        for event in sorted({e for L in leads for e, *_ in leads_table[m][float(L)]}):
            hits = {L: h for L in leads for e, h, *_ in leads_table[m][float(L)] if e == event}
            span = 0.0
            for L in leads:
                if hits.get(L):
                    span = L
                else:
                    break
            early.append(span)
        metrics.append({"model": m, "auc": auc(probs[m][np.isfinite(probs[m])], labels[np.isfinite(probs[m])]),
                        "ap": average_precision(probs[m], labels), "baseRate": float(labels.mean()),
                        "top1AtHorizon": k / n if n else np.nan, "top1Lo": lo, "top1Hi": hi, "events": n,
                        "chanceAtHorizon": float(np.mean([1 / c for _, _, c, _ in at_h])) if at_h else np.nan,
                        "earlyMedianSec": float(np.median(early)) if early else np.nan})

    # 4 仮説: 到達ごとの「到達した物体の確率の割合」を A と比べる(到達の 3〜5 秒前の平均)
    tests = []
    early_leads = [L for L in leads if L >= horizon - 2 - 1e-6]
    def share_by_event(m):
        out = {}
        for L in early_leads:
            for e, _, _, s in leads_table[m][float(L)]:
                out.setdefault(e, []).append(s)
        return {e: np.nanmean(v) for e, v in out.items()}
    base = share_by_event("A")
    for m in ("B", "C", "D"):
        other = share_by_event(m)
        common = sorted(set(base) & set(other))
        d = np.array([other[e] - base[e] for e in common])
        lo, hi = bootstrap(d) if len(d) else (np.nan, np.nan)
        sd = d.std(ddof=1) if len(d) > 2 else np.nan
        need = ((1.96 + 0.84) * sd / abs(d.mean())) ** 2 if len(d) > 2 and abs(d.mean()) > 1e-9 else np.nan
        tests.append({"model": m, "events": len(d), "meanDiff": float(d.mean()) if len(d) else np.nan, "ciLo": lo, "ciHi": hi,
                      "betterRate": float(np.mean(d > 0)) if len(d) else np.nan, "eventsNeeded": need})

    # ---- HTML
    targets = stream.targets
    colors = {n: k for k, n in enumerate(targets)}
    mC = next(r for r in metrics if r["model"] == "C")
    mA = next(r for r in metrics if r["model"] == "A")
    key_cards = (
        "<div class='key'>"
        f"<div class='card'><span>到達の {horizon:g} 秒前に到達先を1位にできた割合(C)</span><b>{pct(mC['top1AtHorizon'])}</b>"
        f"<span>95% 区間 {pct(mC['top1Lo'])}〜{pct(mC['top1Hi'])} / 当て推量 {pct(mC['chanceAtHorizon'])} / {mC['events']} 回</span></div>"
        f"<div class='card'><span>同じ(A 空間のみ)</span><b>{pct(mA['top1AtHorizon'])}</b>"
        f"<span>95% 区間 {pct(mA['top1Lo'])}〜{pct(mA['top1Hi'])}</span></div>"
        f"<div class='card'><span>{horizon:g} 秒以内の到達の予測 AUC(C / A)</span><b>{num(mC['auc'], 2)} / {num(mA['auc'], 2)}</b>"
        f"<span>0.5 = 当て推量 / 1 = 完全</span></div>"
        f"<div class='card'><span>到達先を指し続けた時間の中央値(C / A)</span><b>{num(mC['earlyMedianSec'], 1)} / {num(mA['earlyMedianSec'], 1)} 秒前</b>"
        f"<span>それ以降、到達まで1位のまま</span></div></div>")

    sec = [f"<h2>予測のしくみ</h2><p class='note'>リアルタイムの予測と同じ。{cfg.step:g} 秒ごとに、到達しうる物体"
           f"({candidate_definition(cfg)})それぞれについて、過去 {cfg.window:g} 秒のデータから"
           f"「この先 {horizon:g} 秒以内に到達する確率」を出す。学習はこの人の他の時間({BLOCKS} 分割)だけで、予測は学習に使っていない時間。</p>",
           f"<div class='card'>{schematic(horizon, cfg.window)}</div>"]

    sec.append(f"<h2>1 到達前の信号の時間変化</h2><p class='note'>到達 {n_events} 回(到達の {horizon:g} 秒前に候補が 2 つ以上あった回)を到達の時刻でそろえ、"
               "到達した物体(太線)と、他の候補の物体の平均(破線)を比べる。帯は 95% 区間(到達を選び直すブートストラップ)。各点はその時刻までの 0.5 秒の平均。</p>")
    sec.append("<div class='multiples'>")
    for key, label, sign, digits in SIGNALS:
        st = signal_stats[key]
        a = np.array([s["a"] for s in st])
        o = np.array([s["o"] for s in st])
        allv = np.concatenate([a[np.isfinite(a)], o[np.isfinite(o)]] + [np.array([v for s in st for v in s["a_ci"] + s["o_ci"] if np.isfinite(v)])])
        lo, hi = (float(np.floor(allv.min() * 10) / 10), float(np.ceil(allv.max() * 10) / 10)) if len(allv) else (0, 1)
        if key == "body":
            lo, hi = float(np.floor(allv.min() / 10) * 10), float(np.ceil(allv.max() / 10) * 10)
        sec.append(f"<div class='card'><div class='card-title'>{html.escape(label)}</div>" + xy_chart(ALIGN, [
            {"name": "到達した物体", "values": a, "band": ([s["a_ci"][0] for s in st], [s["a_ci"][1] for s in st]),
             "color": "var(--text)", "emphasized": True},
            {"name": "他の候補", "values": o, "band": ([s["o_ci"][0] for s in st], [s["o_ci"][1] for s in st]),
             "color": "var(--muted)", "dashed": True}],
            f"{label}の到達前の時間変化", "到達からの時刻 (秒)", "", (lo, hi), zero=key == "approach") + "</div>")
    sec.append("</div>")

    sec.append("<h3>どの信号が何秒前から到達先を区別するか</h3>"
               "<p class='note'>効果量 d_z = 到達ごとの(到達した物体 − 他の候補の平均)の平均 / その標準偏差。到達先らしい向き"
               "(近い・近づいている・体が向いている・よく見ている)を + にした。0.5 で中くらい、0.8 で大きい。帯は 95% 区間で、帯が 0 より上なら区別できている。</p>")
    dz_all = [v for k, *_ in SIGNALS for s in signal_stats[k] for v in (s["dz"], *s["dz_ci"]) if np.isfinite(v)]
    dz_lo, dz_hi = (min(-0.5, np.floor(min(dz_all) * 2) / 2), max(1.0, np.ceil(max(dz_all) * 2) / 2)) if dz_all else (-0.5, 1.0)
    sec.append("<div class='card'>" + xy_chart(ALIGN, [
        {"name": label, "label": SHORT[key], "values": [s["dz"] for s in signal_stats[key]],
         "band": ([s["dz_ci"][0] for s in signal_stats[key]], [s["dz_ci"][1] for s in signal_stats[key]]),
         "color": SIGNAL_COLORS[key], "emphasized": True} for key, label, *_ in SIGNALS],
        "信号ごとの、到達先を区別する強さの時間変化", "到達からの時刻 (秒)", "効果量 d_z", (dz_lo, dz_hi), width=720, zero=True) + "</div>")
    first = []
    for key, label, *_ in SIGNALS:
        sig = [r for r, s in zip(ALIGN, signal_stats[key]) if np.isfinite(s["dz_ci"][0]) and s["dz_ci"][0] > 0]
        first.append([html.escape(label), (f"{-min(sig):g} 秒前から" if min(sig) < 0 else "到達時のみ") if sig else "区別できない",
                      num(next((s["dz"] for r, s in zip(ALIGN, signal_stats[key]) if r == -horizon), np.nan), 2),
                      num(signal_stats[key][-1]["dz"], 2)])
    sec.append(table_html(["信号", "区別できる最も早い時刻(95% 区間が 0 より上)", f"{horizon:g} 秒前の効果量", "到達時の効果量"], first))

    sec.append("<h2>2 何秒前から当たるか</h2>"
               f"<p class='note'>到達の L 秒前の予測で、到達した物体の確率が候補の中で1位だった割合(L = {cfg.step:g}〜{horizon:g} 秒)。"
               "当て推量 = 1 / 候補の数。</p>")
    lead_series = []
    for m in MODEL_FEATURES:
        vals = [np.mean([h for _, h, _, _ in leads_table[m][float(L)]]) if leads_table[m][float(L)] else np.nan for L in leads]
        lead_series.append({"name": MODEL_LABELS[m], "label": m, "values": vals, "color": MODEL_COLORS[m], "emphasized": m in ("A", "C")})
    lead_series.append({"name": "当て推量", "values": [np.mean([1 / c for _, _, c, _ in leads_table["A"][float(L)]]) if leads_table["A"][float(L)] else np.nan
                                                      for L in leads], "color": "var(--muted)", "dashed": True, "label": "当て推量"})
    sec.append("<div class='card'>" + xy_chart(-leads[::-1], [{**s, "values": s["values"][::-1]} for s in lead_series],
                                                 "到達の何秒前から当たるか", "到達からの時刻 (秒)", "到達先を1位にできた割合", (0.0, 1.0), width=640) + "</div>")
    sec.append("<div class='legend'>" + "".join(f"<span><i style='background:{MODEL_COLORS[m]}'></i>{html.escape(MODEL_LABELS[m])}</span>"
                                                for m in MODEL_FEATURES) + "</div>")
    sec.append(table_html(["モデル", f"{horizon:g} 秒前に1位", "95% 区間", "当て推量", "AUC", "AP", "陽性の割合", "到達先を指し続けた時間(中央値)"],
                          [[html.escape(MODEL_LABELS[r["model"]]), pct(r["top1AtHorizon"]), f"{pct(r['top1Lo'])}〜{pct(r['top1Hi'])}",
                            pct(r["chanceAtHorizon"]), num(r["auc"], 2), num(r["ap"], 2), pct(r["baseRate"]), f"{num(r['earlyMedianSec'], 1)} 秒"]
                           for r in metrics]))
    sec.append(f"<p class='note'>AUC = 「{horizon:g} 秒以内に到達する物体・時刻」と「しない物体・時刻」を1つずつ選んだとき、到達する方の確率が高い割合。"
               "AP(平均適合率)= 確率の高い順に警告を出したときの当たりやすさ。陽性の割合(当て推量の AP)と比べる。</p>")

    sec.append(f"<h2>3 実際の到達場面</h2><p class='note'>上の 3 段が入力(太線が到達した物体)、一番下がそこから出した「{horizon:g} 秒以内に到達する確率」"
               f"(実線 = C 空間 + 視線 + 体の向き / 破線 = A 空間のみ)。青い帯のデータから、オレンジの帯の間に到達するかを予測する。"
               f"右の数字は、到達の {horizon:g} 秒前(破線の縦線)の時点での C の予測。"
               f"予測に使う {cfg.window + horizon:g} 秒のうち 7 割以上で視線が取れていた到達から、最初の {EXAMPLES} 回を出す。</p>")
    sec.append(legend(targets, colors))
    shown = 0
    for index, (arrived, t) in enumerate(stream.arrivals, 1):
        cand = candidates(stream, arrived, t - horizon)
        i0, i1 = np.searchsorted(stream.time, [t - horizon - cfg.window, t])
        if len(cand) < 2 or (stream.cum_cov[i1] - stream.cum_cov[i0]) < 0.7 * (cfg.window + horizon):
            continue
        sec.append(f"<h4>{index}回目: {html.escape(arrived)} に到達({t:.1f} 秒)。候補 {html.escape(', '.join(cand))}</h4>"
                   f"<div class='card'>{episode_chart(stream, arrived, t, cand, samples, probs, horizon)}</div>")
        shown += 1
        if shown >= EXAMPLES:
            break

    sec.append("<h2>4 仮説: 体の向きと視線を足すと、早い段階の予測が良くなるか</h2>"
               f"<p class='note'>到達ごとに、到達の {horizon - 2:g}〜{horizon:g} 秒前の予測で「到達した物体の確率 / 候補の確率の合計」を平均し、"
               "A(空間のみ)との差を出す。+ ならそのモデルの方が到達先に確率を寄せている。必要な到達の数 = この差を"
               "検出力 80%・有意水準 5%(両側)で確かめるのに必要な到達の数の目安(差の平均と標準偏差がこのままとして)。</p>")
    sec.append(table_html(["モデル", "到達 (回)", "A との差(平均)", "95% 区間", "A より良かった到達", "必要な到達の数の目安"],
                          [[html.escape(MODEL_LABELS[r["model"]]), str(r["events"]), num(r["meanDiff"], 3), f"[{num(r['ciLo'], 3)}, {num(r['ciHi'], 3)}]",
                            pct(r["betterRate"]), f"約 {r['eventsNeeded']:.0f} 回" if np.isfinite(r["eventsNeeded"]) else "—"] for r in tests]))

    sec.append(method_section(cfg))

    light_vars = " ".join(f"--series-{i + 1}: {c};" for i, c in enumerate(SERIES_LIGHT))
    dark_vars = " ".join(f"--series-{i + 1}: {c};" for i, c in enumerate(SERIES_DARK))
    page = (f"<!doctype html><html lang='ja'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width, initial-scale=1'>"
            f"<title>到達の予測</title><style>{STYLE.replace('%LIGHT%', light_vars).replace('%DARK%', dark_vars)}{EXTRA_STYLE}"
            ".chart.retro { max-width: 720px; }</style>"
            f"<script>{SCRIPT}</script></head><body><main>"
            f"<h1>{html.escape(stream.key)}: {horizon:g} 秒以内にどの物体へ到達するかを予測できるか</h1>"
            f"<p class='meta'>{html.escape('/'.join(exports[0]))} / {stream.time[-1] - stream.time[0]:.0f} 秒 / 到達 {len(stream.arrivals)} 回"
            f"(物体ごと: {html.escape(', '.join(f'{n} {sum(1 for a, _ in stream.arrivals if a == n)}' for n in targets))})</p>"
            + f"<p class='meta'>{html.escape(arrival_definition(cfg))}</p>" + key_cards + "".join(sec) + "</main></body></html>")
    (out_dir / "report.html").write_text(page, encoding="utf-8")

    write_csv(out_dir / "aligned_signals.csv", aligned)
    write_csv(out_dir / "lead_accuracy.csv", [{"model": m, "leadSec": L, "event": e, "top1": int(h), "candidates": c, "shareArrived": s}
                                               for m in MODEL_FEATURES for L in leads for e, h, c, s in leads_table[m][float(L)]])
    write_csv(out_dir / "predictions.csv", [{**{k: r[k] for k in ("timeSec", "target", "label")}, **{f"prob{m}": float(probs[m][i]) for m in MODEL_FEATURES}}
                                            for i, r in enumerate(samples)])
    write_csv(out_dir / "metrics.csv", metrics)
    write_csv(out_dir / "hypothesis_tests.csv", tests)

    print(f"\n到達の {horizon:g} 秒前に到達先を1位にできた割合:")
    for r in metrics:
        print(f"  {r['model']}  {pct(r['top1AtHorizon']):>4s}  ({pct(r['top1Lo'])}〜{pct(r['top1Hi'])}, 当て推量 {pct(r['chanceAtHorizon'])})  "
              f"AUC {num(r['auc'], 2)}  AP {num(r['ap'], 2)} (陽性 {pct(r['baseRate'])})")
    print(f"\n→ {out_dir / 'report.html'}")


if __name__ == "__main__":
    main()
