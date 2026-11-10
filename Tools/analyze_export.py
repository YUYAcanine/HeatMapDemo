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
  7 頭と肩の向き   … 頭が肩よりどれだけ物体の方を向いているか(|肩の正面から物体への水平の角度| − |頭の向きから物体への水平の角度|)。
                     体は正面のまま頭だけを物体へ向けているほど大きい。物体ごとに、見ているとき / 見ていないとき / 到達の前の平均。
                     7b で到達の前の時間変化(4b と同じ形。平均だけ)
  8 頭の向きの変化 … 頭の向きが変わる速さ(度/秒)と、急に向きを変えたところ(--turn-speed 度/秒を超え、--turn-min-angle 度以上変わった)。
                     向いた先の物体ごとの回数・その後1秒以内にその物体を見たか・到達の前にあったか。
                     8b で到達の前の「頭を物体の方へ向ける速さ」の時間変化
                     (7・8 の頭と肩の向きは --smooth 秒幅の中央値で飛び値を消してから平均でならす。肩の位置と対象物体の中心は
                      シーン5の書き出しに入っている。古い書き出しには無いので、シーン5で書き出し直す)
  時系列の図       … 注視対象の帯と、対象物体までの距離の折れ線(マウスを乗せると時刻と値)。
                     頭の動きの図: 注視対象の帯・7 の値・頭の向きが変わる速さと急な変化

書き出すもの(毎回上書き。Skeleton/<実験対象者>/Analysis/Export/<骨格>/report/):
  report.html           … 上の集計と図(ブラウザで開く)
  gaze_summary.csv      … 1 の表(人物 × 対象)
  distance_summary.csv  … 2 の表(人物 × 対象)
  episodes.csv          … 注視の1回ごと(人物・対象・開始・終了)
  approaches.csv        … 接近の1回ごと(人物・対象・着いた時刻・接近の前に見ていた割合)
  retrospective.csv     … 4b・7b・8b の値(人物・metric(gazeScore / towardDeg / turnSpeedDegPerSec)・到達の番号・到達した物体・物体・
                          到達した物体か・到達からの時刻・値)
  head_motion.csv       … 7・8 のフレームごとの値(肩に対する頭の向き twistDeg・向きが変わる速さ・物体ごとの toward / angle / turn)
  quick_turns.csv       … 8 の急な変化の1回ごと(開始・終了・変わった角度・最大の速さ・向いた先・その後1秒以内に見た物体)

使い方:
  python Tools/analyze_export.py
      データの場所 (Assets / D:\\HomeExperiment) → 実験の名前 → 実験対象者 の順に一覧から番号で選ぶ
      (シーン5の書き出しが2つ以上あるときは、骨格データも選ぶ)
  python Tools/analyze_export.py <実験の名前> <実験対象者> --data-root assets --source filtered_HeadJoints
      指定したものは選ばずにそのまま使う(--data-root は assets / D:\\HomeExperiment / 任意のフォルダ)
  --near 0.4 --leave 1.0 --before 5 … 接近の定義(m, m, 秒。--near はシーン6/7(0.6)より近くした。--leave / --before はシーン6/7と同じ)
  --merge-gap 0.3 --min-episode 0.5 … 注視の1回の定義(秒。旧シーン5と同じ既定値)
  --retro-window 10 --retro-step 2  … 4b・7b・8b で到達の何秒前から見るか・何秒ごとに点を打つか
  --smooth 0.3 --turn-speed 150 --turn-min-angle 30 --toward-high 30 … 7・8 の設定(秒, 度/秒, 度, 度)
numpy が必要(Tools/Backnumber/GazePipeline の .venv にも入っている)。
"""

from __future__ import annotations

import argparse
import html
import json
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
from toolbox import home_data_root  # noqa: E402  Tools/toolbox/home_data_root.py
from toolbox.export_data import (  # noqa: E402  Tools/toolbox/export_data.py
    NO_DATA, NO_HIT, Person, angle_between, approaches, body_forward, choose, episodes, find_sources, load_light_log,
    signed_yaw, smooth_vectors, span_pairs, state_at, subdirs,
)
from toolbox.report_html import (  # noqa: E402  Tools/toolbox/report_html.py
    GAZE_SCALE, SCRIPT, SERIES_DARK, SERIES_LIGHT, STYLE, hover_line, legend, nice_range, num, pct, retro_chart,
    table_html, write_csv,
)

DISTANCE_BINS = [0.0, 0.5, 1.0, 1.5, 2.0, 3.0, float("inf")]


# ------------------------------------------------------------
# 集計
# ------------------------------------------------------------
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
# 頭の動き(7 頭と肩の向き / 8 頭の向きの変化)
# ------------------------------------------------------------
# 頭の向きが変わる速さを、何秒の間の向きの変化から求めるか(1フレームごとの差はキネクトの揺れで大きくなりすぎる)
SPEED_SPAN = 0.3


class HeadMotion:
    """1人分の頭の動き。値は視線があるフレームだけ(それ以外は nan)

    twist        … 肩の正面に対する頭の水平の向き(度。右が +)。肩の正面は左右の肩を結ぶ線に垂直な水平の向き
    toward[物体] … 頭が肩よりどれだけ物体の方を向いているか(度)
                   = |肩の正面から物体への水平の角度| - |頭の向きから物体への水平の角度|。
                   体は正面のまま頭だけを物体へ向けると大きくなる(頭がちょうど物体を向けば、肩の正面から物体までの角度と同じ)。
                   頭が物体と逆へ向くと -。物体が頭の真上・真下に近い(水平に 0.15 m 以内)ときは nan
    angle[物体]  … 頭の向き(視線)と物体の中心への向きの3次元の角度(度)
    speed        … 頭の向きが変わる速さ(度/秒。そのフレームの前後 SPEED_SPAN 秒の間に向きが変わった角度 / 時間)
    turn[物体]   … 頭が物体の方へ向いていく速さ(度/秒。同じ間に angle が小さくなった量 / 時間。離れていくときは -)
    """

    def __init__(self, person: Person, targets: list[str], centers: dict[str, np.ndarray], args):
        time = person.time
        self.valid = person.present & (person.gaze["hasGaze"] == 1) & np.isfinite(person.direction).all(axis=1)
        self.direction = smooth_vectors(time, person.direction, self.valid, args.smooth, args.max_gap)

        # 肩の正面(左右の肩を結ぶ線に垂直な水平の向き)
        forward = body_forward(person, self.direction, self.valid, args.smooth, args.max_gap)
        head_flat = self.direction * np.array([1.0, 0.0, 1.0])
        self.has_shoulders = bool(np.isfinite(forward).all(axis=1).any())
        self.twist = signed_yaw(forward, head_flat)

        self.toward, self.angle, self.turn = {}, {}, {}
        self.has_centers = bool(centers)
        # 速さは SPEED_SPAN 秒の間(そのフレームの前後)の向きの変化から求める
        self.before, self.after, seconds = span_pairs(time, self.valid, args.max_gap, SPEED_SPAN)
        span_ok = self.valid & np.isfinite(seconds)
        span_sec = np.where(span_ok, seconds, 1.0)
        self.speed = np.where(span_ok, angle_between(self.direction[self.before], self.direction[self.after]) / span_sec, np.nan)

        for name in targets:
            if name not in centers:
                continue

            to_target = centers[name][None, :] - person.origin
            flat = to_target * np.array([1.0, 0.0, 1.0])
            far_enough = np.linalg.norm(flat, axis=1) > 0.15
            body_to = np.abs(signed_yaw(forward, flat))
            head_to = np.abs(signed_yaw(head_flat, flat))
            self.toward[name] = np.where(far_enough, body_to - head_to, np.nan)
            self.angle[name] = angle_between(self.direction, to_target)
            self.turn[name] = np.where(span_ok, (self.angle[name][self.before] - self.angle[name][self.after]) / span_sec, np.nan)


def quick_turns(person: Person, motion: HeadMotion, targets: list[str], args) -> list[dict]:
    """頭の向きが急に変わったところ(速さが --turn-speed 度/秒を超え、その間に --turn-min-angle 度以上向きが変わった)"""
    fast = np.flatnonzero(np.nan_to_num(motion.speed) > args.turn_speed)
    groups: list[list[int]] = []

    for i in fast:
        if groups and person.time[i] - person.time[groups[-1][-1]] <= 0.15:
            groups[-1].append(i)
        else:
            groups.append([i])

    events = []

    for g in groups:
        # 速さが閾値を超えた最初のフレームの少し前から、最後のフレームの少し後まで(速さを求めた範囲)
        before, after = motion.before[g[0]], motion.after[g[-1]]
        change = float(angle_between(motion.direction[[before]], motion.direction[[after]])[0])

        if change < args.turn_min_angle:
            continue

        # 物体の方へ向いた量(向きが変わる前と後の angle の差)が最も大きい物体を「向いた先」にする
        closer = {name: float(motion.angle[name][before] - motion.angle[name][after]) for name in motion.angle}
        toward = max(closer, key=closer.get) if closer and max(closer.values()) >= args.turn_min_angle / 2 else NO_HIT
        t_end = float(person.time[after])
        later = person.present & (person.time > t_end) & (person.time <= t_end + 1.0) & np.isin(person.target, targets)
        looked = [str(v) for v in person.target[later]]
        events.append({
            "person": person.name, "startSec": float(person.time[before]), "endSec": t_end,
            "changeDeg": change, "peakSpeedDegPerSec": float(np.nanmax(motion.speed[g])),
            "toward": toward, "towardCloserDeg": closer.get(toward, np.nan),
            "lookedWithin1s": max(dict.fromkeys(looked), key=looked.count) if looked else NO_HIT,
            "lookedTowardWithin1s": int(toward in looked),
        })

    return events


def weighted_mean(values: np.ndarray, dt: np.ndarray, mask: np.ndarray) -> float:
    use = mask & np.isfinite(values)
    return float(np.sum(values[use] * dt[use]) / np.sum(dt[use])) if dt[use].sum() > 0 else np.nan


def toward_table(person: Person, motion: HeadMotion, targets: list[str], arrivals: list[tuple[str, float]], args) -> list[list[str]]:
    rows = []

    for name in targets:
        if name not in motion.toward:
            continue

        v = motion.toward[name]
        looking = person.target == name
        before = np.zeros(len(v), dtype=bool)

        for arrived, t in arrivals:
            if arrived == name:
                before |= (person.time >= t - args.before) & (person.time < t)

        valid = motion.valid & np.isfinite(v)
        high = person.dt[valid & (v >= args.toward_high)].sum() / person.dt[valid].sum() if person.dt[valid].sum() > 0 else np.nan
        rows.append([html.escape(name), num(weighted_mean(v, person.dt, valid & looking)), num(weighted_mean(v, person.dt, valid & ~looking)),
                     pct(high), num(weighted_mean(v, person.dt, valid & before)), num(weighted_mean(v, person.dt, valid))])

    return rows


def turn_table(person: Person, events: list[dict], targets: list[str], arrivals: list[tuple[str, float]], args) -> list[list[str]]:
    rows = []

    for name in targets:
        mine = [e for e in events if e["toward"] == name]
        looked = sum(e["lookedTowardWithin1s"] for e in mine)
        # その物体への到達のうち、到達の前 --before 秒にその物体の方への急な変化があった回数
        times = [t for arrived, t in arrivals if arrived == name]
        before = sum(1 for t in times if any(t - args.before <= e["endSec"] < t for e in mine))
        rows.append([html.escape(name), len(mine), f"{looked}({pct(looked / len(mine)) if mine else '—'})",
                     f"{before} / {len(times)} 回" if times else "—"])

    rest = [e for e in events if e["toward"] == NO_HIT]
    rows.append(["(どの物体の方でもない)", len(rest), "—", "—"])
    return rows


# ------------------------------------------------------------
# HTML
# ------------------------------------------------------------
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
            "lines": [hover_line(k, d[::step], "m", 2) for k, d in series.items()]}
    parts.append(f"<script>timelineHover('{chart_id}', {json.dumps(data, ensure_ascii=False)});</script>")
    return "".join(parts)


def motion_timeline(person: Person, motion: HeadMotion, events: list[dict], targets: list[str], color_of: dict[str, int],
                    chart_id: str, args) -> str:
    """見ていた対象の帯(上)・頭が肩より物体の方を向いている量(中)・頭の向きが変わる速さ(下)。同じ時間軸"""
    t0, t1 = float(np.nanmin(person.time)), float(np.nanmax(person.time + person.dt))
    width, left, right = 960, 60, 16
    plot_w = width - left - right
    band_h, gap, panel_h = 16, 30, 170
    x = lambda t: left + (t - t0) / max(t1 - t0, 1e-6) * plot_w
    top = 8
    toward_top = top + len(targets) * band_h + gap
    speed_top = toward_top + panel_h + gap
    height = speed_top + panel_h + 34
    step = max(1, len(person.time) // 1500)
    parts = [f"<svg viewBox='0 0 {width} {height}' class='chart timeline' id='{chart_id}' role='img' "
             f"aria-label='見ていた対象・頭が肩より物体の方を向いている量・頭の向きが変わる速さの時系列'>"]

    for i, name in enumerate(targets):
        y = top + i * band_h
        parts.append(f"<text x='{left - 6}' y='{y + band_h * 0.72}' class='label' text-anchor='end'>{html.escape(name)}</text>")

        for s, e in episodes(person.present & (person.target == name), person.time, person.dt, 0.0):
            parts.append(f"<rect x='{x(s):.1f}' y='{y + 2}' width='{max(x(e) - x(s), 0.8):.1f}' height='{band_h - 4}' rx='2' "
                         f"fill='var(--series-{color_of[name] + 1})'><title>{html.escape(name)} を見ていた {s:.1f}〜{e:.1f} 秒</title></rect>")

    def panel(panel_top: float, lo: float, hi: float, tick_step: float, title: str):
        y = lambda v: panel_top + (hi - np.clip(v, lo, hi)) / max(hi - lo, 1e-6) * panel_h

        for tick in np.arange(lo, hi + 1e-6, tick_step):
            cls = "axis" if tick == 0 else "grid"
            parts.append(f"<line x1='{left}' x2='{width - right}' y1='{y(tick):.1f}' y2='{y(tick):.1f}' class='{cls}'/>")
            parts.append(f"<text x='{left - 6}' y='{y(tick) + 4:.1f}' class='tick' text-anchor='end'>{tick:g}</text>")

        parts.append(f"<text x='{left}' y='{panel_top - 8}' class='tick'>{title}</text>")
        return y

    def polyline(values: np.ndarray, y, color: str, stroke: float = 1.6):
        segment = []

        for i in list(range(0, len(person.time), step)) + [None]:
            if i is not None and motion.valid[i] and np.isfinite(values[i]):
                segment.append(f"{x(person.time[i]):.1f},{y(values[i]):.1f}")
                continue

            if len(segment) > 1:
                parts.append(f"<polyline points='{' '.join(segment)}' fill='none' stroke='{color}' stroke-width='{stroke}' stroke-linejoin='round'/>")
            segment = []

    # 頭が肩より物体の方を向いている量
    lines = [motion.toward[n] for n in targets if n in motion.toward]
    lo, hi = nice_range(lines, 30, -30, 60, (-180, 180)) if lines else (-30.0, 60.0)
    y_toward = panel(toward_top, lo, hi, 30, "頭が肩より物体の方を向いている量 (度)")

    for name in targets:
        if name in motion.toward:
            polyline(motion.toward[name], y_toward, f"var(--series-{color_of[name] + 1})")

    # 頭の向きが変わる速さ
    s_hi = nice_range([motion.speed], 50, 0, args.turn_speed * 1.5, (0, 1000))[1]
    y_speed = panel(speed_top, 0, s_hi, 50 if s_hi <= 300 else 100,
                    f"頭の向きが変わる速さ (度/秒)。破線 = 急な変化の閾値 {args.turn_speed:g} 度/秒")
    polyline(motion.speed, y_speed, "var(--text-2)", 1.2)
    parts.append(f"<line x1='{left}' x2='{width - right}' y1='{y_speed(args.turn_speed):.1f}' y2='{y_speed(args.turn_speed):.1f}' "
                 f"class='axis' stroke-dasharray='4 3'/>")

    for e in events:
        color = f"var(--series-{color_of[e['toward']] + 1})" if e["toward"] in color_of else "var(--muted)"
        parts.append(f"<circle cx='{x(e['endSec']):.1f}' cy='{y_speed(e['peakSpeedDegPerSec']):.1f}' r='4.5' fill='{color}' "
                     f"stroke='var(--surface)' stroke-width='1.5'><title>{e['endSec']:.1f} 秒: {e['changeDeg']:.0f} 度向きが変わった"
                     f"(最大 {e['peakSpeedDegPerSec']:.0f} 度/秒)。向いた先: {html.escape(e['toward'])}</title></circle>")

    for tick in np.linspace(t0, t1, 7):
        parts.append(f"<text x='{x(tick):.1f}' y='{speed_top + panel_h + 18}' class='tick' text-anchor='middle'>{tick:.0f}</text>")

    parts.append(f"<text x='{width - right}' y='{speed_top + panel_h + 32}' class='tick' text-anchor='end'>時刻 (秒)</text>")
    parts.append(f"<line class='cross' x1='0' x2='0' y1='{top}' y2='{speed_top + panel_h}' visibility='hidden'/>")
    parts.append(f"<rect class='hit' x='{left}' y='{top}' width='{plot_w}' height='{speed_top + panel_h - top}' fill='transparent'/></svg>")
    parts.append(f"<div class='tooltip' id='{chart_id}-tip' hidden></div>")

    hover = [hover_line(f"{n} の方へ", motion.toward[n][::step], "度", 0) for n in targets if n in motion.toward]
    hover.append(hover_line("肩に対する頭の向き(右が +)", motion.twist[::step], "度", 0))
    hover.append(hover_line("向きが変わる速さ", motion.speed[::step], "度/秒", 0))
    data = {"t0": t0, "t1": t1, "left": left, "plotW": plot_w, "width": width,
            "time": [round(float(v), 3) for v in person.time[::step]],
            "target": [str(v) for v in person.target[::step]],
            "present": [bool(v) for v in person.present[::step]], "lines": hover}
    parts.append(f"<script>timelineHover('{chart_id}', {json.dumps(data, ensure_ascii=False)});</script>")
    return "".join(parts)


def retro_points(args) -> np.ndarray:
    """4b で点を打つ、到達からの時刻(-window, ..., -step, 0)"""
    return np.round(np.arange(-args.retro_window, 1e-6, args.retro_step), 6)


def retro_curves(person: Person, targets: list[str], arrivals: list[tuple[str, float]], args, metric) -> list[dict]:
    """到達ごとに、物体ごとの値(各時刻までの retro_step 秒間の値)。
    metric(物体, その時間の映っていたフレーム) が値を返す(4b は注視スコア = その物体を見ていた割合)"""
    points = retro_points(args)
    curves = []

    for index, (arrived, t) in enumerate(arrivals, 1):
        values = {}

        for name in targets:
            row = []

            for r in points:
                window = person.present & (person.time > t + r - args.retro_step) & (person.time <= t + r)
                row.append(metric(name, window) if person.dt[window].sum() > 0 else np.nan)

            values[name] = row

        curves.append({"index": index, "target": arrived, "arriveSec": t, "values": values})

    return curves


def gaze_score(person: Person):
    def metric(name: str, window: np.ndarray) -> float:
        return person.dt[window & (person.target == name)].sum() / person.dt[window].sum()

    return metric


def retro_scale(curves: list[dict], step: float, low: float, high: float, clip: tuple[float, float], digits: int, unit: str) -> dict:
    """平均のグラフだけを出すとき(7b・8b)の縦軸。到達した物体ごとの平均の線が入る範囲にする"""
    values = []

    with np.errstate(all="ignore"):
        for arrived in {c["target"] for c in curves}:
            group = [c for c in curves if c["target"] == arrived]
            values += [np.nanmean(np.array([c["values"][n] for c in group], dtype=float), axis=0) for n in group[0]["values"]]

    lo, hi = nice_range(values, step, low, high, clip) if values else (low, high)
    ticks = tuple(sorted({lo, hi} | ({0.0} if lo < 0 < hi else set())))
    return {"range": (lo, hi), "ticks": ticks, "digits": digits, "unit": unit}


def retro_section(curves: list[dict], targets: list[str], color_of: dict[str, int], args, title: str = "4b 到達前の注視の時間変化",
                  note: str = "", value_name: str = "注視スコア", scale: dict = GAZE_SCALE, per_event: bool = True) -> str:
    """到達からさかのぼった値: 全部の到達の平均と、到達した物体ごとの平均(per_event なら1回ごとのグラフも)"""
    points = retro_points(args)
    note = note or (f"注視スコア = その時刻までの {args.retro_step:g} 秒間に、その物体を見ていた割合(0〜1)。")
    s = [f"<h3>{html.escape(title)}</h3>"
         f"<p class='note'>{note}"
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
    ], f"全部の到達の平均の{value_name}", scale) + "</div>")

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
                 + retro_chart(points, series_for(mean_values, name), f"{name} に到達した回の平均の{value_name}", scale) + "</div>")

        for c in group if per_event else []:
            s.append(f"<div class='card'><div class='card-title'>{c['index']}回目({c['arriveSec']:.1f} 秒に到達)</div>"
                     + retro_chart(points, series_for(c["values"], name), f"{name} に到達した {c['index']} 回目の{value_name}", scale) + "</div>")

        s.append("</div>")
    rows = [[f"{r:g}" if r else "到達", num(a, scale["digits"]), num(o, scale["digits"])] for r, a, o in zip(points, mean_arrived, mean_others)]
    s.append(table_html(["到達からの時刻 (秒)", "到達した物体", "到達しなかった物体(平均)"], rows))
    return "".join(s)


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
    parser.add_argument("--smooth", type=float, default=0.3, help="7・8 で頭と肩の向きを滑らかにする幅 (秒。中央値で飛び値を消してから平均)")
    parser.add_argument("--max-gap", type=float, default=0.25, help="7・8 でこれより長い途切れはまたがない (秒)")
    parser.add_argument("--toward-high", type=float, default=30.0, help="7 で「頭が物体の方を大きく向いている」とする角度 (度)")
    parser.add_argument("--turn-speed", type=float, default=150.0, help="8 で急な変化とする頭の向きの速さ (度/秒)")
    parser.add_argument("--turn-min-angle", type=float, default=30.0, help="8 で急な変化とする、向きが変わった角度の下限 (度)")
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

    centers = {name: np.array([c["x"], c["y"], c["z"]], dtype=float)
               for name, c in zip(params.get("targets", []), params.get("targetCenters", []))}
    all_gaze, all_dist, all_episodes, all_approaches, all_retro, all_motion, all_turns = [], [], [], [], [], [], []
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
        curves = retro_curves(p, targets, arrivals, args, gaze_score(p))
        s.append(retro_section(curves, targets, color_of, args))
        retro_sets = [("gazeScore", curves)]

        if has_lights:
            s.append("<h3>5 ライトの点灯と比べる</h3>")
            s.append(table_html(["対象", "点灯 (秒)", "点灯中に見ていた割合", "点灯中の距離 (m)", "消灯 (秒)", "消灯中に見ていた割合", "消灯中の距離 (m)"],
                                light_table(p, targets, light_log)))
            s.append("<p class='note'>light_log.json の unityTime を骨格の時刻と同じ時計として、その時刻の直前の記録の点灯状態を使う。</p>")

        # 7・8 頭の動き
        motion = HeadMotion(p, targets, centers, args)
        events = quick_turns(p, motion, targets, args)
        all_turns += events
        missing = []

        if not motion.has_centers:
            missing.append("対象物体の中心(export_params.json の targetCenters)")
        if not p.has_shoulder_columns:
            missing.append("肩の位置(gaze.csv の shoulderLeft/Right)")

        s.append("<h3>頭の動きの時系列</h3>")
        if missing:
            s.append(f"<p class='note'>書き出しに {' と '.join(missing)} がありません。シーン5で書き出し直すと、7 と 8 の物体ごとの値が出ます。</p>")
        s.append("<p class='note'>上: 見ていた対象。中: 頭が肩より物体の方を向いている量(7)。下: 頭の向きが変わる速さと、急に変わったところ(8。点の色は向いた先の物体、灰色はどの物体の方でもない)。"
                 f"頭の向きと肩の向きは、{args.smooth:g} 秒幅の中央値で飛び値を消してから同じ幅の平均で滑らかにしてある"
                 f"(キネクトの頭→鼻の向きは1フレームごとに大きく揺れるため)。向きが変わる速さは {SPEED_SPAN:g} 秒の間の変化から求める。</p>")
        s.append(legend(targets, color_of))
        s.append(f"<div class='card'>{motion_timeline(p, motion, events, targets, color_of, 'mt-' + str(len(sections)), args)}</div>")

        s.append("<h3>7 頭と肩の向き(体は正面のまま、頭だけ物体の方へ向けているか)</h3>")
        if motion.has_shoulders and motion.toward:
            s.append(table_html(["物体", "その物体を見ているとき (度)", "見ていないとき (度)", f"{args.toward_high:g} 度以上の時間",
                                 f"到達の {args.before:g} 秒前 (度)", "全体 (度)"], toward_table(p, motion, targets, arrivals, args)))
            s.append("<p class='note'>値 = |肩の正面から物体への水平の角度| − |頭の向きから物体への水平の角度|(時間で重み付けした平均)。"
                     "体を物体へ向けず、頭だけを物体の方へ回しているほど大きい。頭がちょうど物体を向くと「肩の正面から物体への角度」と同じになり、逆を向くと −。"
                     f"肩の正面 = 左右の肩を結ぶ線に垂直な水平の向き。肩に対する頭の向き(右が +)の絶対値の平均は {num(weighted_mean(np.abs(motion.twist), p.dt, motion.valid))} 度。</p>")
            toward_curves = retro_curves(p, targets, arrivals, args,
                                         lambda name, w: weighted_mean(motion.toward[name], p.dt, w & motion.valid))
            s.append(retro_section(toward_curves, targets, color_of, args, "7b 到達前の、頭が物体の方を向いている量",
                                   f"値 = その時刻までの {args.retro_step:g} 秒間の、7 の値の平均(度)。",
                                   "頭が物体の方を向いている量", retro_scale(toward_curves, 15, -15, 30, (-180, 180), 0, " 度"), per_event=False))
            retro_sets.append(("towardDeg", toward_curves))
        else:
            s.append("<p class='note'>肩の位置か対象物体の中心が無いので出せません(画像で推定した骨格には肩がありません)。</p>")

        s.append("<h3>8 頭の向きの変化(急に向きを変えたところ)</h3>")
        minutes = p.dt[motion.valid].sum() / 60
        s.append(f"<p class='meta'>急な変化 {len(events)} 回(視線があった {minutes:.1f} 分あたり {len(events) / minutes if minutes else 0:.1f} 回)/ "
                 f"頭の向きが変わる速さの中央値 {num(np.nanmedian(motion.speed) if np.isfinite(motion.speed).any() else np.nan, 0)} 度/秒</p>")
        if motion.angle:
            s.append(table_html(["向いた先の物体", "急な変化 (回)", "1秒以内にその物体を見た", f"到達の {args.before:g} 秒前にその物体の方への急な変化があった到達"],
                                turn_table(p, events, targets, arrivals, args)))
        s.append(f"<p class='note'>急な変化 = 頭の向きが変わる速さが {args.turn_speed:g} 度/秒を超え、その間に {args.turn_min_angle:g} 度以上向きが変わったところ"
                 f"(速さは {SPEED_SPAN:g} 秒の間の変化から求める。0.15 秒以内の途切れはつなげる)。向いた先 = 変わる前と後で、視線と物体の中心との角度が最も小さくなった物体"
                 f"({args.turn_min_angle / 2:g} 度以上近づいたときだけ)。</p>")
        if motion.angle:
            turn_curves = retro_curves(p, targets, arrivals, args,
                                       lambda name, w: weighted_mean(np.clip(motion.turn[name], 0, None), p.dt, w & motion.valid))
            s.append(retro_section(turn_curves, targets, color_of, args, "8b 到達前の、頭を物体の方へ向ける速さ",
                                   f"値 = その時刻までの {args.retro_step:g} 秒間の、視線が物体の中心へ近づいていく速さ(度/秒。離れていくときは 0 として平均)。",
                                   "頭を物体の方へ向ける速さ", retro_scale(turn_curves, 10, 0, 20, (0, 1000), 1, " 度/秒"), per_event=False))
            retro_sets.append(("turnSpeedDegPerSec", turn_curves))

        all_retro += [{"person": p.name, "metric": metric, "event": c["index"], "arrivedTarget": c["target"], "arriveSec": c["arriveSec"],
                       "target": name, "arrived": int(name == c["target"]), "relTimeSec": float(r), "value": v}
                      for metric, cs in retro_sets for c in cs for name in targets for r, v in zip(retro_points(args), c["values"][name])]

        for i in np.flatnonzero(motion.valid):
            row = {"person": p.name, "timeSec": float(p.time[i]), "target": str(p.target[i]),
                   "twistDeg": float(motion.twist[i]), "headSpeedDegPerSec": float(motion.speed[i])}
            for name in targets:
                for key, values in (("toward", motion.toward), ("angle", motion.angle), ("turn", motion.turn)):
                    row[f"{key}_{name}"] = float(values[name][i]) if name in values else np.nan
            all_motion.append(row)

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
    write_csv(out_dir / "head_motion.csv", all_motion)
    write_csv(out_dir / "quick_turns.csv", all_turns)

    print("\n注視の集計(対象物体だけ):")
    for r in all_gaze:
        print(f"  {r['person']:>10s} {r['target']:>12s}  {r['seconds']:7.1f} 秒  {pct(r['ratio']):>4s}  {r['episodes']:3d} 回")
    print("\n距離の集計:")
    for r in all_dist:
        print(f"  {r['person']:>10s} {r['target']:>12s}  平均 {num(r['meanM'], 2)} m  最小 {num(r['minM'], 2)} m  接近 {r['approaches']} 回")
    print(f"\n→ {out_dir / 'report.html'}")


if __name__ == "__main__":
    main()
