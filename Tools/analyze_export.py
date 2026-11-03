"""シーン5(5GazeExport)で書き出した注視と距離の CSV から、人物ごとに簡単な集計をして HTML にまとめる。

読むもの(Skeleton/<実験対象者>/Analysis/Export/<骨格>/):
  <人物>_gaze.csv          … フレームごとの注視対象(対象物体だけ)
  <人物>_gaze_persons.csv  … フレームごとの注視対象(相手の人物も含める。2人以上のときだけ)
  <人物>_distance.csv      … フレームごとの対象物体・相手までの水平距離
  export_params.json       … 書き出しに使った設定
  Skeleton/<実験対象者>/light_log.json … あれば、ライトの点灯と比べる(対象物体の名前が light1 / Light1 などのとき)

出すもの(人物ごと):
  1 注視の集計     … 対象ごとに、見ていた時間・映っていた時間に対する割合・回数(--merge-gap 秒以下の途切れはつなげ、
                     --min-episode 秒以上続いたものを数える)・平均/最長・最初に見た時刻
  2 距離の集計     … 対象ごとに、距離の平均・中央値・最小、--near m 以内にいた時間、接近の回数
                     (--leave m より離れていたのを見た後に --near m 以内に入ったら1回。映っていない時間が1秒を超えたら数え直す)
  3 距離と注視     … 対象までの距離の区間ごとに、その対象を見ていた割合(近いと見るのか、遠くから見ているのか)
  4 接近の前の注視 … 接近の --before 秒前から着くまでに、その対象を見ていた割合と、ふだん(映っていた全時間)の割合
  4b 到達前の注視の時間変化 … 到達(4 の接近)の --retro-window 秒前から到達まで、--retro-step 秒ごとに、
                     物体ごとの「注視スコア」= その時刻までの --retro-step 秒間にその物体を見ていた割合(0〜1)を折れ線にする
                     (旧シーン7の到達からさかのぼる分析と同じ形。書き出しにはコーンの外の物体の角度が無いので、
                      シーン7の注視可能性(角度のガウス)ではなく、注視対象になっていた時間の割合を使う)。
                     全部の到達の平均(到達した物体 / 到達しなかった物体の平均)と、
                     到達した物体ごとに、その物体に到達した回の平均・1回ごとのグラフを並べる
  5 ライト         … light_log.json があれば、ライトごとに 点灯中 / 消灯中 の注視の割合と距離の平均
  6 2人のとき      … 同じ対象を同時に見ていた時間(gaze.csv)、相手を見ていた時間・お互いを見ていた時間(gaze_persons.csv)、
                     2人の距離
  時系列の図       … 注視対象の帯と、対象物体までの距離の折れ線(マウスを乗せると時刻と値)

書き出すもの(毎回上書き。Skeleton/<実験対象者>/Analysis/Export/<骨格>/report/):
  report.html           … 上の集計と図(ブラウザで開く)
  gaze_summary.csv      … 1 の表(人物 × 対象)
  distance_summary.csv  … 2 の表(人物 × 対象)
  episodes.csv          … 注視の1回ごと(人物・対象・開始・終了)
  approaches.csv        … 接近の1回ごと(人物・対象・着いた時刻・接近の前に見ていた割合)
  retrospective.csv     … 4b の値(人物・到達の番号・到達した物体・物体・到達した物体か・到達からの時刻・注視スコア)

使い方:
  python Tools/analyze_export.py
      データの場所 (Assets / D:\\HomeExperiment) → 実験の名前 → 実験対象者 の順に一覧から番号で選ぶ
      (シーン5の書き出しが2つ以上あるときは、骨格データも選ぶ)
  python Tools/analyze_export.py <実験の名前> <実験対象者> --data-root assets --source filtered_HeadJoints
      指定したものは選ばずにそのまま使う(--data-root は assets / D:\\HomeExperiment / 任意のフォルダ)
  --near 0.4 --leave 1.0 --before 5 … 接近の定義(m, m, 秒。--near はシーン6/7(0.6)より近くした。--leave / --before はシーン6/7と同じ)
  --merge-gap 0.3 --min-episode 0.5 … 注視の1回の定義(秒。旧シーン5と同じ既定値)
  --retro-window 10 --retro-step 2  … 4b で到達の何秒前から見るか・何秒ごとに点を打つか
numpy が必要(Tools/Backnumber/GazePipeline の .venv にも入っている)。
"""

from __future__ import annotations

import argparse
import csv
import html
import json
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
from toolbox import home_data_root  # noqa: E402  Tools/toolbox/home_data_root.py

NO_HIT = "None"
NO_DATA = "NoData"
DISTANCE_BINS = [0.0, 0.5, 1.0, 1.5, 2.0, 3.0, float("inf")]
# 映っていない時間がこれを超えたら、接近を数え直す(秒。シーン6と同じ)
TRACK_GAP = 1.0

# 対象の色(固定の順。対象物体 → 相手の人物 の順に割り当てる)
SERIES_LIGHT = ["#2a78d6", "#eb6834", "#1baf7a", "#eda100", "#e87ba4", "#008300", "#6250d6", "#e34948"]
SERIES_DARK = ["#3987e5", "#d95926", "#199e70", "#c98500", "#d55181", "#008300", "#9085e9", "#e66767"]


# ------------------------------------------------------------
# 読み込み
# ------------------------------------------------------------
def read_csv(path: Path) -> dict[str, np.ndarray]:
    with path.open(encoding="utf-8-sig", newline="") as f:
        rows = list(csv.reader(f))

    header, body = rows[0], rows[1:]
    columns = {}

    for i, name in enumerate(header):
        values = [row[i] if i < len(row) else "" for row in body]

        try:
            columns[name] = np.array([float(v) if v != "" else np.nan for v in values])
        except ValueError:
            columns[name] = np.array(values, dtype=object)

    return columns


class Person:
    def __init__(self, folder: Path, name: str):
        self.name = name
        self.gaze = read_csv(folder / f"{name}_gaze.csv")
        persons_path = folder / f"{name}_gaze_persons.csv"
        self.gaze_persons = read_csv(persons_path) if persons_path.exists() else None
        self.distance = read_csv(folder / f"{name}_distance.csv")
        self.time = self.gaze["timeSec"]
        self.dt = self.gaze["durationSec"]
        self.present = self.gaze["present"] == 1
        self.target = self.gaze["target"]


def find_sources(export_dir: Path) -> list[str]:
    return sorted(p.name for p in export_dir.iterdir() if p.is_dir() and any(p.glob("*_gaze.csv"))) if export_dir.is_dir() else []


def choose(label: str, options: list[str], notes: dict[str, str] | None = None) -> str | None:
    """一覧から番号で選ぶ(Tools/Backnumber の各スクリプトと同じ)。notes は名前の後ろに出す補足"""
    if not options:
        print(f"{label} がありません。")
        return None

    print()
    for i, name in enumerate(options, 1):
        print(f"  {i}. {name}{(notes or {}).get(name, '')}")

    try:
        answer = input(f"{label} の番号: ").strip()
    except EOFError:
        return None

    if answer.isdigit() and 1 <= int(answer) <= len(options):
        return options[int(answer) - 1]

    print("番号が正しくありません。")
    return None


def subdirs(path: Path) -> list[str]:
    return sorted(p.name for p in path.iterdir() if p.is_dir() and not p.name.endswith("~")) if path.exists() else []


def load_light_log(path: Path) -> tuple[np.ndarray, dict[str, np.ndarray]] | None:
    if not path.exists():
        return None

    frames = json.loads(path.read_text(encoding="utf-8")).get("frames", [])

    if not frames:
        return None

    times = np.array([f["unityTime"] for f in frames])
    lights = {k: np.array([bool(f[k]) for f in frames]) for k in frames[0] if k != "unityTime"}
    return times, lights


def state_at(log_times: np.ndarray, states: np.ndarray, times: np.ndarray) -> np.ndarray:
    """各時刻の直前の記録の状態(記録より前は消灯とする)"""
    index = np.searchsorted(log_times, times, side="right") - 1
    return np.where(index >= 0, states[np.clip(index, 0, None)], False)


# ------------------------------------------------------------
# 集計
# ------------------------------------------------------------
def episodes(mask: np.ndarray, time: np.ndarray, dt: np.ndarray, merge_gap: float) -> list[tuple[float, float]]:
    """mask が続いている区間 (開始, 終了)。merge_gap 秒以下の途切れはつなげる"""
    result: list[list[float]] = []

    for i in np.flatnonzero(mask):
        start, end = time[i], time[i] + dt[i]

        if result and start - result[-1][1] <= merge_gap:
            result[-1][1] = max(result[-1][1], end)
        else:
            result.append([start, end])

    return [(s, e) for s, e in result]


def gaze_rows(person: Person, target_column: np.ndarray, names: list[str], args) -> tuple[list[dict], list[dict]]:
    present_seconds = person.dt[person.present].sum()
    rows, episode_rows = [], []

    for name in names:
        mask = person.present & (target_column == name)
        seconds = person.dt[mask].sum()
        eps = episodes(mask, person.time, person.dt, args.merge_gap)
        counted = [e for e in eps if e[1] - e[0] >= args.min_episode]
        lengths = [e[1] - e[0] for e in counted]
        rows.append({
            "person": person.name, "target": name, "seconds": seconds,
            "ratio": seconds / present_seconds if present_seconds > 0 else np.nan,
            "episodes": len(counted),
            "meanEpisodeSec": float(np.mean(lengths)) if lengths else np.nan,
            "longestEpisodeSec": max(lengths) if lengths else np.nan,
            "firstTimeSec": eps[0][0] if eps else np.nan,
        })
        episode_rows += [{"person": person.name, "target": name, "startSec": s, "endSec": e, "durationSec": e - s,
                          "counted": int(e - s >= args.min_episode)} for s, e in eps]

    return rows, episode_rows


def approaches(person: Person, distance: np.ndarray, args) -> list[float]:
    """接近の時刻(--leave m より離れていたのを見た後に、--near m 以内に入った時刻)"""
    result = []
    armed = False
    last_seen = -np.inf

    for t, present, d in zip(person.time, person.present, distance):
        if not present or np.isnan(d):
            continue

        if t - last_seen > TRACK_GAP:
            armed = False

        last_seen = t

        if d > args.leave:
            armed = True
        elif d <= args.near and armed:
            result.append(t)
            armed = False

    return result


def distance_rows(person: Person, targets: list[str], args) -> tuple[list[dict], list[dict]]:
    rows, approach_rows = [], []
    looking_ratio_base = {}

    for name in targets:
        d = person.distance.get(f"dist_{name}")

        if d is None:
            continue

        valid = person.present & ~np.isnan(d)
        times = approaches(person, d, args)
        looking = person.present & (person.target == name)
        base = person.dt[looking].sum() / person.dt[person.present].sum() if person.present.any() else np.nan
        looking_ratio_base[name] = base

        for t in times:
            window = person.present & (person.time >= t - args.before) & (person.time < t)
            seen = person.dt[window].sum()
            ratio = person.dt[window & looking].sum() / seen if seen > 0 else np.nan
            approach_rows.append({"person": person.name, "target": name, "arriveSec": t,
                                  "lookRatioBefore": ratio, "lookRatioUsual": base, "presentSecondsBefore": seen})

        rows.append({
            "person": person.name, "target": name,
            "meanM": float(np.mean(d[valid])) if valid.any() else np.nan,
            "medianM": float(np.median(d[valid])) if valid.any() else np.nan,
            "minM": float(np.min(d[valid])) if valid.any() else np.nan,
            "nearSeconds": person.dt[valid & (d <= args.near)].sum(),
            "approaches": len(times),
        })

    return rows, approach_rows


def distance_bin_table(person: Person, targets: list[str]) -> list[list[str]]:
    """対象までの距離の区間ごとに、その対象を見ていた割合(その区間にいた時間)"""
    table = []

    for name in targets:
        d = person.distance.get(f"dist_{name}")

        if d is None:
            continue

        row = [name]

        for low, high in zip(DISTANCE_BINS[:-1], DISTANCE_BINS[1:]):
            inside = person.present & (d >= low) & (d < high)
            seconds = person.dt[inside].sum()
            looked = person.dt[inside & (person.target == name)].sum()
            row.append(f"{looked / seconds:.0%}<br><small>{seconds:.0f} 秒</small>" if seconds > 0 else "—")

        table.append(row)

    return table


def light_table(person: Person, targets: list[str], light_log) -> list[list[str]]:
    if light_log is None:
        return []

    log_times, lights = light_log
    by_lower = {k.lower(): k for k in lights}
    table = []

    for name in targets:
        key = by_lower.get(name.lower())

        if key is None:
            continue

        on = state_at(log_times, lights[key], person.time)
        d = person.distance.get(f"dist_{name}")
        looking = person.target == name
        row = [name]

        for state in (True, False):
            mask = person.present & (on == state)
            seconds = person.dt[mask].sum()
            ratio = person.dt[mask & looking].sum() / seconds if seconds > 0 else np.nan
            mean_d = np.nanmean(d[mask]) if d is not None and mask.any() else np.nan
            row += [f"{seconds:.0f}", pct(ratio), num(mean_d, 2)]

        table.append(row)

    return table


def pair_rows(a: Person, b: Person) -> list[list[str]]:
    both = a.present & b.present
    both_seconds = a.dt[both].sum()
    joint = both & (a.target == b.target) & ~np.isin(a.target, [NO_HIT, NO_DATA])
    rows = [["2人が同時に映っていた", f"{both_seconds:.1f} 秒", ""],
            ["同じ対象物体を同時に見ていた", f"{a.dt[joint].sum():.1f} 秒", pct(a.dt[joint].sum() / both_seconds if both_seconds else np.nan)]]

    if a.gaze_persons is not None and b.gaze_persons is not None:
        a_looks = both & (a.gaze_persons["target"] == b.name)
        b_looks = both & (b.gaze_persons["target"] == a.name)
        mutual = a_looks & b_looks
        rows += [[f"{a.name} が {b.name} を見ていた", f"{a.dt[a_looks].sum():.1f} 秒", pct(a.dt[a_looks].sum() / both_seconds if both_seconds else np.nan)],
                 [f"{b.name} が {a.name} を見ていた", f"{a.dt[b_looks].sum():.1f} 秒", pct(a.dt[b_looks].sum() / both_seconds if both_seconds else np.nan)],
                 ["お互いを見ていた", f"{a.dt[mutual].sum():.1f} 秒", pct(a.dt[mutual].sum() / both_seconds if both_seconds else np.nan)]]

    d = a.distance.get(f"dist_{b.name}")

    if d is not None and (both & ~np.isnan(d)).any():
        v = d[both & ~np.isnan(d)]
        rows.append(["2人の距離(平均 / 中央値 / 最小)", f"{v.mean():.2f} / {np.median(v):.2f} / {v.min():.2f} m", ""])

    return rows


# ------------------------------------------------------------
# HTML
# ------------------------------------------------------------
def pct(v: float) -> str:
    return "—" if v is None or np.isnan(v) else f"{v:.0%}"


def num(v: float, digits: int = 1) -> str:
    return "—" if v is None or np.isnan(v) else f"{v:.{digits}f}"


def table_html(header: list[str], rows: list[list[str]], first_is_label: bool = True) -> str:
    head = "".join(f"<th>{h}</th>" for h in header)
    body = "".join("<tr>" + "".join(f"<td>{c}</td>" if i or not first_is_label else f"<th scope='row'>{c}</th>"
                                    for i, c in enumerate(row)) + "</tr>" for row in rows)
    return f"<div class='table-wrap'><table><thead><tr>{head}</tr></thead><tbody>{body}</tbody></table></div>"


def gaze_bars(rows: list[dict], color_of: dict[str, int]) -> str:
    """対象ごとの注視の割合の横棒(1本ずつ値のラベル付き)"""
    if not rows:
        return ""

    top = max(max((r["ratio"] for r in rows if not np.isnan(r["ratio"])), default=0), 0.05)
    width, bar_h, gap, label_w = 640, 18, 10, 120
    plot_w = width - label_w - 60
    height = len(rows) * (bar_h + gap) + 24
    parts = [f"<svg viewBox='0 0 {width} {height}' class='chart' role='img' aria-label='対象ごとの注視の割合'>"]

    for i, r in enumerate(rows):
        y = i * (bar_h + gap)
        w = 0 if np.isnan(r["ratio"]) else max(r["ratio"] / top * plot_w, 0)
        slot = color_of.get(r["target"], 0)
        parts.append(f"<text x='{label_w - 8}' y='{y + bar_h * 0.72}' class='label' text-anchor='end'>{html.escape(r['target'])}</text>")
        parts.append(f"<g class='mark'><title>{html.escape(r['target'])}: {pct(r['ratio'])} ({r['seconds']:.1f} 秒, {r['episodes']} 回)</title>"
                     f"<rect x='{label_w}' y='{y}' width='{max(w, 0.5):.1f}' height='{bar_h}' rx='3' fill='var(--series-{slot + 1})'/></g>")
        parts.append(f"<text x='{label_w + w + 6:.1f}' y='{y + bar_h * 0.72}' class='value'>{pct(r['ratio'])}</text>")

    axis_y = len(rows) * (bar_h + gap)
    parts.append(f"<line x1='{label_w}' x2='{label_w}' y1='0' y2='{axis_y - gap}' class='axis'/>")
    parts.append(f"<text x='{label_w}' y='{axis_y + 14}' class='tick'>映っていた時間に対する割合</text></svg>")
    return "".join(parts)


def timeline(person: Person, targets: list[str], others: list[str], color_of: dict[str, int], chart_id: str) -> str:
    """注視対象の帯(上)と、対象物体までの距離の折れ線(下)。同じ時間軸"""
    t0, t1 = float(np.nanmin(person.time)), float(np.nanmax(person.time + person.dt))
    width, left, right = 960, 60, 16
    plot_w = width - left - right
    band_h, band_gap, plot_h = 22, 26, 200
    x = lambda t: left + (t - t0) / max(t1 - t0, 1e-6) * plot_w

    names = targets + others
    target_col = person.gaze_persons["target"] if person.gaze_persons is not None else person.target
    band_rows = names
    bands_h = len(band_rows) * band_h
    top = 8
    plot_top = top + bands_h + band_gap
    height = plot_top + plot_h + 34

    parts = [f"<svg viewBox='0 0 {width} {height}' class='chart timeline' id='{chart_id}' role='img' aria-label='注視対象と距離の時系列'>"]

    for i, name in enumerate(band_rows):
        y = top + i * band_h
        slot = color_of.get(name, 0)
        parts.append(f"<text x='{left - 6}' y='{y + band_h * 0.68}' class='label' text-anchor='end'>{html.escape(name)}</text>")
        parts.append(f"<line x1='{left}' x2='{width - right}' y1='{y + band_h - 1}' y2='{y + band_h - 1}' class='grid'/>")

        for s, e in episodes(person.present & (target_col == name), person.time, person.dt, 0.0):
            parts.append(f"<rect x='{x(s):.1f}' y='{y + 3}' width='{max(x(e) - x(s), 0.8):.1f}' height='{band_h - 6}' rx='2' "
                         f"fill='var(--series-{slot + 1})'><title>{html.escape(name)} {s:.1f}〜{e:.1f} 秒</title></rect>")

    # 距離
    series = {name: person.distance.get(f"dist_{name}") for name in targets}
    series = {k: v for k, v in series.items() if v is not None}
    d_max = max([float(np.nanmax(v)) for v in series.values() if np.isfinite(v).any()] + [1.0])
    d_max = float(np.ceil(d_max))
    y = lambda d: plot_top + plot_h - d / d_max * plot_h

    for tick in np.arange(0, d_max + 0.01, 1.0 if d_max <= 6 else 2.0):
        parts.append(f"<line x1='{left}' x2='{width - right}' y1='{y(tick):.1f}' y2='{y(tick):.1f}' class='grid'/>")
        parts.append(f"<text x='{left - 6}' y='{y(tick) + 4:.1f}' class='tick' text-anchor='end'>{tick:g}</text>")

    parts.append(f"<text x='{left - 44}' y='{plot_top - 8}' class='tick'>距離 (m)</text>")
    step = max(1, len(person.time) // 1500)

    for name, d in series.items():
        slot = color_of.get(name, 0)
        segments, current = [], []

        for i in range(0, len(person.time), step):
            if person.present[i] and np.isfinite(d[i]):
                current.append(f"{x(person.time[i]):.1f},{y(d[i]):.1f}")
            elif current:
                segments.append(current)
                current = []

        if current:
            segments.append(current)

        for seg in segments:
            parts.append(f"<polyline points='{' '.join(seg)}' fill='none' stroke='var(--series-{slot + 1})' stroke-width='2' stroke-linejoin='round'/>")

    for tick in np.linspace(t0, t1, 7):
        parts.append(f"<text x='{x(tick):.1f}' y='{plot_top + plot_h + 18}' class='tick' text-anchor='middle'>{tick:.0f}</text>")

    parts.append(f"<text x='{width - right}' y='{plot_top + plot_h + 32}' class='tick' text-anchor='end'>時刻 (秒)</text>")
    parts.append(f"<line class='cross' x1='0' x2='0' y1='{top}' y2='{plot_top + plot_h}' visibility='hidden'/>")
    parts.append(f"<rect class='hit' x='{left}' y='{top}' width='{plot_w}' height='{plot_top + plot_h - top}' fill='transparent'/></svg>")
    parts.append(f"<div class='tooltip' id='{chart_id}-tip' hidden></div>")

    data = {"t0": t0, "t1": t1, "left": left, "plotW": plot_w, "width": width,
            "time": [round(float(v), 3) for v in person.time[::step]],
            "target": [str(v) for v in target_col[::step]],
            "present": [bool(v) for v in person.present[::step]],
            "dist": {k: [None if not np.isfinite(v) else round(float(v), 2) for v in d[::step]] for k, d in series.items()}}
    parts.append(f"<script>timelineHover('{chart_id}', {json.dumps(data, ensure_ascii=False)});</script>")
    return "".join(parts)


def legend(names: list[str], color_of: dict[str, int]) -> str:
    return "<div class='legend'>" + "".join(
        f"<span><i style='background:var(--series-{color_of[n] + 1})'></i>{html.escape(n)}</span>" for n in names) + "</div>"


def retro_points(args) -> np.ndarray:
    """4b で点を打つ、到達からの時刻(-window, ..., -step, 0)"""
    return np.round(np.arange(-args.retro_window, 1e-6, args.retro_step), 6)


def retro_curves(person: Person, targets: list[str], arrivals: list[tuple[str, float]], args) -> list[dict]:
    """到達ごとに、物体ごとの注視スコア(各時刻までの retro_step 秒間に、その物体を見ていた割合)"""
    points = retro_points(args)
    curves = []

    for index, (arrived, t) in enumerate(arrivals, 1):
        values = {}

        for name in targets:
            looking = person.target == name
            row = []

            for r in points:
                window = person.present & (person.time > t + r - args.retro_step) & (person.time <= t + r)
                seen = person.dt[window].sum()
                row.append(person.dt[window & looking].sum() / seen if seen > 0 else np.nan)

            values[name] = row

        curves.append({"index": index, "target": arrived, "arriveSec": t, "values": values})

    return curves


def retro_chart(points: np.ndarray, series: list[dict], aria: str) -> str:
    """到達からさかのぼった注視スコアの折れ線(y は 0〜1 で固定)。
    series: name / values / color(CSS の色)/ emphasized(太線・大きい点)/ dashed / label(線の右端に出す名前)"""
    width, height = 470, 230
    left, right, top, bottom = 40, 118, 14, 32
    plot_w, plot_h = width - left - right, height - top - bottom
    x0, x1 = float(points[0]), float(points[-1])
    x = lambda r: left + (r - x0) / max(x1 - x0, 1e-6) * plot_w
    y = lambda v: top + (1 - v) * plot_h
    parts = [f"<svg viewBox='0 0 {width} {height}' class='chart retro' role='img' aria-label='{html.escape(aria)}'>"]

    for v in (0, 0.5, 1):
        parts.append(f"<line x1='{left}' x2='{left + plot_w}' y1='{y(v):.1f}' y2='{y(v):.1f}' class='grid'/>")
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
                             f"<title>{html.escape(s['name'])} {'到達' if r == 0 else f'{r:g} 秒'}: {v:.2f}</title></circle>")

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


def retro_section(curves: list[dict], targets: list[str], color_of: dict[str, int], args) -> str:
    points = retro_points(args)
    s = [f"<h3>4b 到達前の注視の時間変化</h3>"
         f"<p class='note'>注視スコア = その時刻までの {args.retro_step:g} 秒間に、その物体を見ていた割合(0〜1)。"
         f"到達 = 4 の接近({args.leave:g} m より離れていた後に {args.near:g} m 以内に入った時刻)。映っていない区間は点を打たない。</p>"]

    if not curves:
        s.append("<p class='note'>到達がありませんでした。</p>")
        return "".join(s)

    arrived = np.array([c["values"][c["target"]] for c in curves], dtype=float)
    others = np.array([c["values"][n] for c in curves for n in targets if n != c["target"]], dtype=float)

    with np.errstate(all="ignore"):
        mean_arrived = np.nanmean(arrived, axis=0) if len(arrived) else np.full(len(points), np.nan)
        mean_others = np.nanmean(others, axis=0) if len(others) else np.full(len(points), np.nan)

    s.append(f"<h4>全部の到達の平均({len(curves)} 回)</h4>")
    s.append("<div class='legend'><span><i style='background:var(--text)'></i>到達した物体</span>"
             "<span><i style='background:var(--muted)'></i>到達しなかった物体(平均)</span></div>")
    s.append("<div class='card'>" + retro_chart(points, [
        {"name": "到達した物体", "values": mean_arrived, "color": "var(--text)", "emphasized": True, "label": "到達した物体"},
        {"name": "到達しなかった物体", "values": mean_others, "color": "var(--muted)", "dashed": True, "label": "到達しなかった物体"},
    ], "全部の到達の平均の注視スコア") + "</div>")

    # 到達した物体ごとに、その物体に到達した回の平均と、1回ごとのグラフを並べる
    s.append(legend(targets, color_of))
    s.append("<p class='note'>色は物体ごとに固定。太線・大きい点・「(到達)」が付いているのが、到達した物体。</p>")

    def series_for(values: dict[str, np.ndarray], arrived_name: str) -> list[dict]:
        return [{"name": n, "values": values[n], "color": f"var(--series-{color_of[n] + 1})", "emphasized": n == arrived_name,
                 "label": f"{n}(到達)" if n == arrived_name else n} for n in targets]

    for name in targets:
        group = [c for c in curves if c["target"] == name]
        s.append(f"<h4>{html.escape(name)} に到達({len(group)} 回)</h4>")

        if not group:
            s.append("<p class='note'>到達がありませんでした。</p>")
            continue

        with np.errstate(all="ignore"):
            mean_values = {n: np.nanmean(np.array([c["values"][n] for c in group], dtype=float), axis=0) for n in targets}

        s.append("<div class='multiples'>")
        s.append(f"<div class='card mean'><div class='card-title'>平均({len(group)} 回)</div>"
                 + retro_chart(points, series_for(mean_values, name), f"{name} に到達した回の平均の注視スコア") + "</div>")

        for c in group:
            s.append(f"<div class='card'><div class='card-title'>{c['index']}回目({c['arriveSec']:.1f} 秒に到達)</div>"
                     + retro_chart(points, series_for(c["values"], name), f"{name} に到達した {c['index']} 回目の注視スコア") + "</div>")

        s.append("</div>")
    rows = [[f"{r:g}" if r else "到達", num(a, 2), num(o, 2)] for r, a, o in zip(points, mean_arrived, mean_others)]
    s.append(table_html(["到達からの時刻 (秒)", "到達した物体", "到達しなかった物体(平均)"], rows))
    return "".join(s)


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
    for (const k in d.dist) text += `<br>${k}: ${d.dist[k][i] == null ? '—' : d.dist[k][i].toFixed(2) + ' m'}`;
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


# ------------------------------------------------------------
# main
# ------------------------------------------------------------
def main() -> None:
    parser = argparse.ArgumentParser(description="シーン5の書き出し(注視・距離)を集計して HTML にする")
    parser.add_argument("experiment", nargs="?", help="実験の名前")
    parser.add_argument("subject", nargs="?", help="実験対象者")
    parser.add_argument("--source", help="書き出しの骨格データ (Analysis/Export/<骨格>。省略すると一覧から選ぶ)")
    parser.add_argument("--near", type=float, default=0.4, help="接近とする距離 (m)")
    parser.add_argument("--leave", type=float, default=1.0, help="離れているとする距離 (m)")
    parser.add_argument("--before", type=float, default=5.0, help="接近の前に注視を見る秒数")
    parser.add_argument("--merge-gap", type=float, default=0.3, help="これ以下の途切れは注視の1回としてつなげる (秒)")
    parser.add_argument("--min-episode", type=float, default=0.5, help="これ以上続いた注視を回数に数える (秒)")
    parser.add_argument("--retro-window", type=float, default=10.0, help="4b で到達の何秒前から見るか")
    parser.add_argument("--retro-step", type=float, default=2.0, help="4b で何秒ごとに点を打つか(各点はその時刻までのこの秒数の割合)")
    home_data_root.add_argument(parser)
    args = parser.parse_args()

    data_root = home_data_root.choose_data_root(args.data_root)

    if data_root is None:
        return

    experiment = args.experiment or choose("実験の名前", subdirs(data_root))
    if not experiment:
        return

    skeleton_dir = data_root / experiment / "Skeleton"
    subjects = subdirs(skeleton_dir)
    no_export = {s: "  (シーン5の書き出しなし)" for s in subjects if not find_sources(skeleton_dir / s / "Analysis" / "Export")}
    subject = args.subject or choose("実験対象者", subjects, no_export)
    if not subject:
        return

    subject_dir = skeleton_dir / subject
    sources = find_sources(subject_dir / "Analysis" / "Export")

    if not sources:
        print(f"シーン5(5GazeExport)の書き出しがありません。シーン5で書き出してください: {subject_dir / 'Analysis' / 'Export'}")
        return

    # 書き出しが1つだけならそれを使う(Tools/Backnumber/GazeTimeline と同じ)
    source = args.source or (sources[0] if len(sources) == 1 else choose("骨格データ", sources))
    if not source:
        return

    if len(sources) == 1:
        print(f"骨格データ: {source}")

    folder = subject_dir / "Analysis" / "Export" / source
    params = json.loads((folder / "export_params.json").read_text(encoding="utf-8")) if (folder / "export_params.json").exists() else {}
    names = params.get("persons") or sorted(p.name[:-len("_gaze.csv")] for p in folder.glob("*_gaze.csv"))
    persons = [Person(folder, n) for n in names]
    targets = params.get("targets") or [c[len("angle_"):] for c in persons[0].gaze if c.startswith("angle_")]
    color_of = {name: i % len(SERIES_LIGHT) for i, name in enumerate(targets + [p.name for p in persons])}
    light_log = load_light_log(subject_dir / "light_log.json")
    has_lights = light_log is not None and any(t.lower() in {k.lower() for k in light_log[1]} for t in targets)

    all_gaze, all_dist, all_episodes, all_approaches, all_retro = [], [], [], [], []
    sections = []

    for p in persons:
        others = [o.name for o in persons if o is not p]
        g_rows, e_rows = gaze_rows(p, p.target, targets, args)
        d_rows, a_rows = distance_rows(p, targets, args)
        all_gaze += g_rows
        all_dist += d_rows
        all_episodes += e_rows
        all_approaches += a_rows
        present_seconds = p.dt[p.present].sum()
        none_seconds = p.dt[p.present & (p.target == NO_HIT)].sum()
        nodata_seconds = p.dt[p.present & (p.target == NO_DATA)].sum()

        s = [f"<h2>{html.escape(p.name)}</h2>",
             f"<p class='meta'>映っていた時間 {present_seconds:.1f} 秒 / どの対象物体も見ていない {none_seconds:.1f} 秒 ({pct(none_seconds / present_seconds if present_seconds else np.nan)})"
             f" / 視線が取れない {nodata_seconds:.1f} 秒</p>"]

        s.append("<h3>時系列</h3><p class='note'>上: 見ていた対象(帯)。下: 対象物体までの水平距離。マウスを乗せると、その時刻の値が出る。</p>")
        s.append(legend(targets + (others if p.gaze_persons is not None else []), color_of))
        s.append(f"<div class='card'>{timeline(p, targets, others if p.gaze_persons is not None else [], color_of, 'tl-' + str(len(sections)))}</div>")

        s.append("<h3>1 注視の集計(対象物体だけ)</h3>")
        s.append(f"<div class='card'>{gaze_bars(g_rows, color_of)}</div>")
        s.append(table_html(["対象", "時間 (秒)", "割合", "回数", "平均 (秒)", "最長 (秒)", "最初 (秒)"],
                            [[html.escape(r["target"]), num(r["seconds"]), pct(r["ratio"]), r["episodes"], num(r["meanEpisodeSec"]),
                              num(r["longestEpisodeSec"]), num(r["firstTimeSec"])] for r in g_rows]))
        s.append(f"<p class='note'>割合 = 見ていた時間 / 映っていた時間。回数は {args.merge_gap:g} 秒以下の途切れをつなげ、{args.min_episode:g} 秒以上続いたもの。</p>")

        if p.gaze_persons is not None:
            gp_rows, _ = gaze_rows(p, p.gaze_persons["target"], targets + others, args)
            s.append("<h3>1b 注視の集計(相手の人物も含める)</h3>")
            s.append(f"<div class='card'>{gaze_bars(gp_rows, color_of)}</div>")
            s.append(table_html(["対象", "時間 (秒)", "割合", "回数", "平均 (秒)", "最長 (秒)"],
                                [[html.escape(r["target"]), num(r["seconds"]), pct(r["ratio"]), r["episodes"], num(r["meanEpisodeSec"]),
                                  num(r["longestEpisodeSec"])] for r in gp_rows]))

        s.append("<h3>2 距離の集計</h3>")
        s.append(table_html(["対象", "平均 (m)", "中央値 (m)", "最小 (m)", f"{args.near:g} m 以内 (秒)", "接近 (回)"],
                            [[html.escape(r["target"]), num(r["meanM"], 2), num(r["medianM"], 2), num(r["minM"], 2), num(r["nearSeconds"]), r["approaches"]]
                             for r in d_rows]))
        s.append(f"<p class='note'>距離は骨盤から対象物体の箱までの水平距離。接近は {args.leave:g} m より離れていたのを見た後に {args.near:g} m 以内に入ったら1回。</p>")

        s.append("<h3>3 距離と注視</h3>")
        bin_names = [f"{lo:g}〜{hi:g} m" if np.isfinite(hi) else f"{lo:g} m〜" for lo, hi in zip(DISTANCE_BINS[:-1], DISTANCE_BINS[1:])]
        s.append(table_html(["対象"] + bin_names, distance_bin_table(p, targets)))
        s.append("<p class='note'>その対象までの距離がこの区間にあったときに、その対象を見ていた割合(下の小さい数字はその区間にいた時間)。</p>")

        s.append("<h3>4 接近の前の注視</h3>")
        if a_rows:
            s.append(table_html(["対象", "着いた時刻 (秒)", f"着く {args.before:g} 秒前から見ていた割合", "ふだんの割合"],
                                [[html.escape(r["target"]), num(r["arriveSec"]), pct(r["lookRatioBefore"]), pct(r["lookRatioUsual"])] for r in a_rows]))
            for name in targets:
                rs = [r for r in a_rows if r["target"] == name and np.isfinite(r["lookRatioBefore"])]
                if rs:
                    s.append(f"<p class='note'>{html.escape(name)}: 接近 {len(rs)} 回の平均 {pct(np.mean([r['lookRatioBefore'] for r in rs]))}"
                             f"(ふだん {pct(rs[0]['lookRatioUsual'])})</p>")
        else:
            s.append("<p class='note'>接近がありませんでした。</p>")

        arrivals = sorted(((r["target"], r["arriveSec"]) for r in a_rows), key=lambda item: item[1])
        curves = retro_curves(p, targets, arrivals, args)
        s.append(retro_section(curves, targets, color_of, args))
        all_retro += [{"person": p.name, "event": c["index"], "arrivedTarget": c["target"], "arriveSec": c["arriveSec"],
                       "target": name, "arrived": int(name == c["target"]), "relTimeSec": float(r), "score": v}
                      for c in curves for name in targets for r, v in zip(retro_points(args), c["values"][name])]

        if has_lights:
            s.append("<h3>5 ライトの点灯と比べる</h3>")
            s.append(table_html(["対象", "点灯 (秒)", "点灯中に見ていた割合", "点灯中の距離 (m)", "消灯 (秒)", "消灯中に見ていた割合", "消灯中の距離 (m)"],
                                light_table(p, targets, light_log)))
            s.append("<p class='note'>light_log.json の unityTime を骨格の時刻と同じ時計として、その時刻の直前の記録の点灯状態を使う。</p>")

        sections.append("".join(s))

    if len(persons) >= 2:
        pair_html = ["<h2>6 2人のとき</h2>"]
        for i in range(len(persons)):
            for j in range(i + 1, len(persons)):
                a, b = persons[i], persons[j]
                pair_html.append(f"<h3>{html.escape(a.name)} と {html.escape(b.name)}</h3>")
                pair_html.append(table_html(["", "時間", "割合"], pair_rows(a, b)))
        pair_html.append("<p class='note'>割合 = 2人が同時に映っていた時間に対する割合。</p>")
        sections.append("".join(pair_html))

    light_vars = " ".join(f"--series-{i + 1}: {c};" for i, c in enumerate(SERIES_LIGHT))
    dark_vars = " ".join(f"--series-{i + 1}: {c};" for i, c in enumerate(SERIES_DARK))
    settings = (f"骨格 {source} / 視線コーン {params.get('coneAngle', float('nan')):.2f}° / 下向き補正 {params.get('downwardAngle', float('nan')):.1f}° / "
                f"接近 {args.near:g} m(離れる {args.leave:g} m)/ 書き出し {params.get('createdAt', '')}")
    page = (f"<!doctype html><html lang='ja'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width, initial-scale=1'>"
            f"<title>注視と距離の集計</title><style>{STYLE.replace('%LIGHT%', light_vars).replace('%DARK%', dark_vars)}</style>"
            f"<script>{SCRIPT}</script></head><body><main>"
            f"<h1>注視と距離の集計 — {html.escape(experiment)} / {html.escape(subject)}</h1><p class='meta'>{html.escape(settings)}</p>"
            f"<p class='meta'>人物: {html.escape(', '.join(names))} / 対象物体: {html.escape(', '.join(targets)) or 'なし'}</p>"
            + "".join(sections) + "</main></body></html>")

    out_dir = folder / "report"
    out_dir.mkdir(exist_ok=True)
    (out_dir / "report.html").write_text(page, encoding="utf-8")
    write_csv(out_dir / "gaze_summary.csv", all_gaze)
    write_csv(out_dir / "distance_summary.csv", all_dist)
    write_csv(out_dir / "episodes.csv", all_episodes)
    write_csv(out_dir / "approaches.csv", all_approaches)
    write_csv(out_dir / "retrospective.csv", all_retro)

    print("\n注視の集計(対象物体だけ):")
    for r in all_gaze:
        print(f"  {r['person']:>10s} {r['target']:>12s}  {r['seconds']:7.1f} 秒  {pct(r['ratio']):>4s}  {r['episodes']:3d} 回")
    print("\n距離の集計:")
    for r in all_dist:
        print(f"  {r['person']:>10s} {r['target']:>12s}  平均 {num(r['meanM'], 2)} m  最小 {num(r['minM'], 2)} m  接近 {r['approaches']} 回")
    print(f"\n→ {out_dir / 'report.html'}")


if __name__ == "__main__":
    main()
