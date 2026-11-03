"""視線と行動の関係についての仮説を、シーン6(6GazeBehaviorAnalyze)で保存した特徴量で確かめる。

シーン6で Analyze All(または Save)したときの
  Skeleton/<実験対象者>/Analysis/GazeBehavior/gaze_behavior_<骨格>_frames.csv      … フレーム × 人物ごとの特徴量
  Skeleton/<実験対象者>/Analysis/GazeBehavior/gaze_behavior_<骨格>_approaches.csv  … 危険物体への接近1回ごと
  Skeleton/<実験対象者>/Analysis/GazeBehavior/gaze_behavior_<骨格>_params.json     … 使ったパラメータ
を読み、次の仮説ごとに数字を出す(画面と HTML)。どれも「最初の当たりをつける」ための簡単な集計で、
効果がありそうなものを見つけたら、場面を絞る・人数を増やすなどして確かめ直す前提。

  H1 視線の先行   … 頭の向きは進行方向より先に変わる?
                    頭の向き(t) と 進行方向(t + lag) のなす角の平均を lag ごとに求め、最も小さくなる lag を見る
                    (lag > 0 なら、頭の向きが lag 秒後の進行方向に近い = 視線が先に動いている)
  H2 ねじれ = 興味 … 体(肩)はそのままで頭だけ向けているときは、対象物体を見ていることが多い? その後その物体に近づく?
  H3 視野の広さ   … 一点を見ている(頭の向きのばらつきが小さい)ときは対象物体を見ていることが多い? 接近の前は狭くなる?
  H4 接近前の注視 … 危険物体に近づく前に、その物体を見ている? 普段(離れているとき)と比べてどのくらい多い?
  H5 予知の手がかり … 「horizon 秒以内に危険物体に接近する」を、1つずつの特徴量でどのくらい見分けられるか(AUC)。
                    0.5 = 見分けられない、1.0 = 完全に見分けられる。予知のモデルに入れる特徴量の候補を選ぶため

使い方:
  python Tools/GazeBehavior/analyze_gaze_behavior.py <実験の名前> <実験対象者>
  (最初にデータの場所 Assets / D:\\HomeExperiment を選ぶ。--data-root D:\\HomeExperiment で指定もできる)
  (引数を省くと、フォルダの一覧から選ぶ。骨格データが2つ以上あるときは一覧から選ぶ。--source で指定もできる)
  --person child    … この人物だけ
  --horizon 3       … H5 の「何秒以内に接近するか」
  --twist 30        … H2 の「ねじれている」とする角度(省略時はシーン6の Twist Threshold)
書き出すもの(毎回上書き):
  Skeleton/<実験対象者>/Analysis/GazeBehavior/gaze_behavior_<骨格>_report.html
numpy が必要(Tools/GazePipeline の .venv にも入っている)。
"""

from __future__ import annotations

import argparse
import csv
import html
import json
import math
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import home_data_root  # noqa: E402  Tools/home_data_root.py

PREFIX = "gaze_behavior_"
FRAMES_SUFFIX = "_frames.csv"

# 文字のまま読む列(それ以外は数値。空欄は NaN)
TEXT_COLUMNS = {"person", "target"}


# ------------------------------------------------------------
# 選ぶ・読む
# ------------------------------------------------------------
def _choose(label: str, options: list[str]) -> str | None:
    if not options:
        print(f"{label} がありません。")
        return None
    if len(options) == 1:
        print(f"{label}: {options[0]}")
        return options[0]
    for i, name in enumerate(options, 1):
        print(f"  {i}. {name}")
    answer = input(f"{label} の番号: ").strip()
    if answer.isdigit() and 1 <= int(answer) <= len(options):
        return options[int(answer) - 1]
    return None


def _subdirs(path: Path) -> list[str]:
    return sorted(p.name for p in path.iterdir() if p.is_dir()) if path.exists() else []


def load_frames(path: Path) -> tuple[dict[str, dict[str, np.ndarray]], list[str]]:
    """人物ごとに、列名 → 配列(時刻の順)。対象物体の名前の一覧も返す"""
    with path.open(encoding="utf-8-sig", newline="") as f:
        reader = csv.reader(f)
        header = next(reader)
        rows_by_person: dict[str, list[list[str]]] = {}
        person_col = header.index("person")
        for row in reader:
            rows_by_person.setdefault(row[person_col], []).append(row)

    targets = [h[len("dist_"):] for h in header if h.startswith("dist_")]
    persons: dict[str, dict[str, np.ndarray]] = {}

    for person, rows in rows_by_person.items():
        columns: dict[str, np.ndarray] = {}
        for c, name in enumerate(header):
            values = [row[c] if c < len(row) else "" for row in rows]
            if name in TEXT_COLUMNS:
                columns[name] = np.array(values, dtype=object)
            else:
                columns[name] = np.array([float(v) if v else np.nan for v in values], dtype=float)
        order = np.argsort(columns["time"], kind="stable")
        persons[person] = {k: v[order] for k, v in columns.items()}

    return persons, targets


def load_approaches(path: Path) -> list[dict]:
    if not path.exists():
        return []
    with path.open(encoding="utf-8-sig", newline="") as f:
        rows = list(csv.DictReader(f))
    for row in rows:
        for key, value in list(row.items()):
            if key not in ("person", "target"):
                row[key] = float(value) if value else math.nan
    return rows


# ------------------------------------------------------------
# 計算の道具
# ------------------------------------------------------------
def wrap(angle: np.ndarray) -> np.ndarray:
    """角度の差を -180〜180 に"""
    return (angle + 180.0) % 360.0 - 180.0


def frame_weights(t: np.ndarray, max_gap: float) -> np.ndarray:
    """フレームの時間(次のフレームまで。max_gap で打ち切る)。割合を時間で重み付けするため"""
    if len(t) == 0:
        return t
    d = np.diff(t, append=t[-1])
    return np.clip(d, 0.0, max_gap)


def value_at(t: np.ndarray, values: np.ndarray, query: np.ndarray, tolerance: float) -> np.ndarray:
    """query の時刻に最も近いフレームの値(tolerance 秒より離れていれば NaN)"""
    idx = np.clip(np.searchsorted(t, query), 1, max(len(t) - 1, 1))
    left = idx - 1
    use_left = np.abs(query - t[left]) <= np.abs(t[idx] - query)
    nearest = np.where(use_left, left, idx)
    out = values[nearest].astype(float)
    out[np.abs(t[nearest] - query) > tolerance] = np.nan
    return out


def auc(scores: np.ndarray, labels: np.ndarray) -> float:
    """順位で求める AUC(同じ値は平均の順位)。NaN の行は除く"""
    ok = ~np.isnan(scores)
    scores, labels = scores[ok], labels[ok]
    n_pos = int(labels.sum())
    n_neg = len(labels) - n_pos
    if n_pos == 0 or n_neg == 0:
        return math.nan
    order = np.argsort(scores, kind="mergesort")
    sorted_scores = scores[order]
    ranks = np.empty(len(scores))
    i = 0
    while i < len(scores):
        j = i
        while j + 1 < len(scores) and sorted_scores[j + 1] == sorted_scores[i]:
            j += 1
        ranks[order[i:j + 1]] = (i + j) / 2.0 + 1.0
        i = j + 1
    return float((ranks[labels].sum() - n_pos * (n_pos + 1) / 2.0) / (n_pos * n_neg))


def pct(value: float) -> str:
    return "-" if value is None or math.isnan(value) else f"{value * 100:.0f}%"


def num(value: float, fmt: str = ".2f") -> str:
    return "-" if value is None or math.isnan(value) else format(value, fmt)


def unit(value: float, suffix: str, fmt: str = ".1f") -> str:
    """値に単位を付ける(値が無ければ - だけ)"""
    text = num(value, fmt)
    return text if text == "-" else text + suffix


def ratio(a: float, b: float) -> float:
    return a / b if b > 0 else math.nan


# ------------------------------------------------------------
# 仮説ごとの集計。どれも (見出し, 説明, 表の行[列名→値], 補足の行) を返す
# ------------------------------------------------------------
class Section:
    def __init__(self, key: str, title: str, note: str):
        self.key = key
        self.title = title
        self.note = note
        self.tables: list[tuple[str, list[str], list[list[str]]]] = []
        self.findings: list[str] = []
        self.svg = ""


def h1_gaze_leads(persons: dict, max_gap: float) -> Section:
    s = Section("H1", "視線の先行: 頭の向きは進行方向より先に変わる?",
                "移動中のフレームで、頭の向き(t) と 進行方向(t + lag) のなす角の平均。"
                "最も小さくなる lag が正なら、頭が先に向き、その後に体が進む(視線が行動に先行している)。")
    lags = np.round(np.arange(-2.0, 2.0001, 0.1), 2)
    curves = {}
    rows = []

    for person, c in persons.items():
        t, head, move = c["time"], c["headYaw"], c["moveYaw"]
        base_ok = ~np.isnan(head)
        curve = []
        for lag in lags:
            shifted = value_at(t, move, t + lag, 0.1)
            ok = base_ok & ~np.isnan(shifted)
            curve.append(float(np.mean(np.abs(wrap(head[ok] - shifted[ok])))) if ok.sum() >= 30 else math.nan)
        curve = np.array(curve)
        curves[person] = curve
        if np.all(np.isnan(curve)):
            rows.append([person, "-", "-", "-", "0"])
            continue
        best = int(np.nanargmin(curve))
        zero = int(np.argmin(np.abs(lags)))
        moving = int(np.sum(~np.isnan(move) & base_ok))
        rows.append([person, f"{lags[best]:+.1f} s", unit(curve[best], "°"), unit(curve[zero], "°"), str(moving)])
        if lags[best] > 0.05:
            s.findings.append(f"{person}: 頭の向きは {lags[best]:.1f} 秒後の進行方向に最も近い(視線が先行している傾向)")
        elif lags[best] < -0.05:
            s.findings.append(f"{person}: 頭の向きは {-lags[best]:.1f} 秒前の進行方向に最も近い(視線が遅れてついてくる傾向)")
        else:
            s.findings.append(f"{person}: 頭の向きと進行方向はほぼ同時に変わる")

    s.tables.append(("", ["人物", "最も近い lag", "その角度", "lag 0 の角度", "移動中のフレーム"], rows))
    s.svg = line_chart(lags, curves, "lag (秒)  ← 頭が遅れる / 頭が先行 →", "頭の向きと進行方向のなす角 (°)")
    return s


def h2_twist(persons: dict, targets: list[str], twist_threshold: float, max_gap: float) -> Section:
    s = Section("H2", "ねじれ = 興味: 肩はそのままで頭だけ向けているときは、物を見ていて、その後近づく?",
                f"ねじれ = 頭の向き − 肩の向き。|ねじれ| ≥ {twist_threshold:.0f}° を「ねじれている」とする。"
                "上の表: ねじれの大きさごとに、対象物体を見ていた時間の割合。"
                "下の表: 対象物体を見ていたフレームのうち、3秒以内にその物体へ 0.3 m 以上近づいた割合。")
    bins = [0, 15, 30, 45, 60, 180]
    rows = []
    approach_rows = []

    for person, c in persons.items():
        t, twist, target = c["time"], np.abs(c["twist"]), c["target"]
        w = frame_weights(t, max_gap)
        looking = np.array([bool(v) for v in target])
        ok = ~np.isnan(twist)
        row = [person]
        for lo, hi in zip(bins[:-1], bins[1:]):
            sel = ok & (twist >= lo) & (twist < hi)
            row.append(f"{pct(ratio(w[sel & looking].sum(), w[sel].sum()))} ({w[sel].sum():.0f}s)")
        rows.append(row)

        twisted = ok & (twist >= twist_threshold)
        straight = ok & (twist < twist_threshold)
        r_tw = ratio(w[twisted & looking].sum(), w[twisted].sum())
        r_st = ratio(w[straight & looking].sum(), w[straight].sum())
        if not math.isnan(r_tw) and not math.isnan(r_st):
            s.findings.append(f"{person}: 対象物体を見ていた割合 ねじれ {pct(r_tw)} / ねじれなし {pct(r_st)}"
                              + (" → ねじれているときの方が物を見ている" if r_tw > r_st * 1.2 else ""))

        # 見ていた物体に、その後近づいたか
        moved = {True: [0, 0], False: [0, 0]}
        for name in targets:
            dist = c.get(f"dist_{name}")
            if dist is None:
                continue
            sel = ok & (target == name)
            if not sel.any():
                continue
            later = value_at(t, dist, t[sel] + 3.0, 0.2)
            closer = (dist[sel] - later) >= 0.3
            valid = ~np.isnan(later)
            for flag in (True, False):
                mask = (twist[sel] >= twist_threshold) == flag
                moved[flag][0] += int(np.sum(closer & valid & mask))
                moved[flag][1] += int(np.sum(valid & mask))
        approach_rows.append([person,
                              f"{pct(ratio(*moved[True]))} ({moved[True][1]} frames)",
                              f"{pct(ratio(*moved[False]))} ({moved[False][1]} frames)"])

    s.tables.append(("|ねじれ| ごとの、対象物体を見ていた割合(その |ねじれ| だった時間)",
                     ["人物"] + [f"{lo}–{hi}°" if hi < 180 else f"{lo}°–" for lo, hi in zip(bins[:-1], bins[1:])], rows))
    s.tables.append(("対象物体を見ていたとき、3秒以内にその物体へ近づいた割合",
                     ["人物", f"ねじれ ≥ {twist_threshold:.0f}°", "ねじれなし"], approach_rows))
    return s


def h3_scan(persons: dict, approaches: list[dict], max_gap: float) -> Section:
    s = Section("H3", "視野の広さ: 一点を見ているときは物を見ている? 接近の前は狭くなる?",
                "視野の広さ = 直前の数秒の頭の向きのばらつき(円周標準偏差)。小さい = 一点を見ている。"
                "上の表: 視野の広さごとの、対象物体を見ていた割合。下の表: 接近の前の視野の広さと、全体の平均。")
    bins = [0, 10, 20, 40, 80, 360]
    rows = []
    compare = []

    for person, c in persons.items():
        t, scan, target = c["time"], c["scanWidth"], c["target"]
        w = frame_weights(t, max_gap)
        looking = np.array([bool(v) for v in target])
        ok = ~np.isnan(scan)
        row = [person]
        for lo, hi in zip(bins[:-1], bins[1:]):
            sel = ok & (scan >= lo) & (scan < hi)
            row.append(f"{pct(ratio(w[sel & looking].sum(), w[sel].sum()))} ({w[sel].sum():.0f}s)")
        rows.append(row)

        overall = ratio(float(np.sum(scan[ok] * w[ok])), float(w[ok].sum()))
        before = [a["meanScanWidth"] for a in approaches if a["person"] == person and not math.isnan(a["meanScanWidth"])]
        mean_before = float(np.mean(before)) if before else math.nan
        compare.append([person, unit(overall, "°"), unit(mean_before, "°"), str(len(before))])
        if before and not math.isnan(overall):
            s.findings.append(f"{person}: 接近前の視野の広さ {mean_before:.0f}° / 全体 {overall:.0f}°"
                              + (" → 接近の前は狭い(一点を見ている)" if mean_before < overall * 0.8 else
                                 " → 接近の前は広い(見回している)" if mean_before > overall * 1.2 else ""))

    s.tables.append(("視野の広さごとの、対象物体を見ていた割合(その広さだった時間)",
                     ["人物"] + [f"{lo}–{hi}°" if hi < 360 else f"{lo}°–" for lo, hi in zip(bins[:-1], bins[1:])], rows))
    s.tables.append(("接近の前と全体の視野の広さ", ["人物", "全体の平均", "接近の前の平均", "接近の回数"], compare))
    return s


def h4_look_before(persons: dict, approaches: list[dict], hazards: list[str], params: dict) -> Section:
    lookback = float(params.get("lookbackSeconds", 5.0))
    leave = float(params.get("leaveDistance", 1.0))
    s = Section("H4", "接近前の注視: 危険物体に近づく前に、その物体を見ている?",
                f"接近の前 {lookback:.0f} 秒に、その物体が注視対象だった接近の割合と、最初に見てから着くまでの秒数(視線の先行時間)。"
                f"「普段」= その物体から {leave:.1f} m より離れているときの、任意の {lookback:.0f} 秒にその物体を見ていた割合(比べる基準)。")
    rows = []

    for person, c in persons.items():
        t, target = c["time"], c["target"]
        for name in hazards:
            dist = c.get(f"dist_{name}")
            events = [a for a in approaches if a["person"] == person and a["target"] == name]
            if dist is None or (not events and not np.any(target == name)):
                continue
            looked = [a for a in events if a["lookSeconds"] > 0]
            leads = [a["firstLookLeadSec"] for a in looked if not math.isnan(a["firstLookLeadSec"])]

            # 普段: 1秒おきの時刻で、離れているときに直前 lookback 秒にその物体を見ていたか
            looking = (target == name).astype(float)
            cum = np.concatenate([[0.0], np.cumsum(looking)])
            probe = np.arange(t[0] + lookback, t[-1], 1.0) if len(t) else np.array([])
            far = value_at(t, dist, probe, 0.2) > leave
            probe = probe[far]
            lo = np.searchsorted(t, probe - lookback)
            hi = np.searchsorted(t, probe, side="right")
            covered = (hi - lo) > 3
            base = float(np.mean((cum[hi] - cum[lo])[covered] > 0)) if covered.any() else math.nan

            rate = ratio(len(looked), len(events))
            rows.append([person, name, str(len(events)), pct(rate), pct(base),
                         unit(float(np.median(leads)) if leads else math.nan, " s")])
            if events and not math.isnan(base):
                s.findings.append(f"{person} → {name}: 接近前に見ていた {pct(rate)}(普段 {pct(base)})"
                                  + (" → 近づく前に見ている傾向" if rate > base * 1.5 else ""))

    s.tables.append(("", ["人物", "危険物体", "接近の回数", "接近前に見ていた", "普段", "先行時間(中央値)"], rows))
    return s


def h5_prediction(persons: dict, approaches: list[dict], hazards: list[str], params: dict, horizon: float) -> Section:
    approach_distance = float(params.get("approachDistance", 0.6))
    s = Section("H5", f"予知の手がかり: {horizon:.0f} 秒以内に危険物体へ接近するか、1つの特徴量でどのくらい見分けられる?",
                f"危険物体から {approach_distance:.1f} m より離れているフレーム(0.2秒おき)ごとに、{horizon:.0f} 秒以内にその物体への接近が始まるかを正解にし、"
                "特徴量1つだけで見分けたときの AUC(0.5 = 見分けられない、1.0 = 完全)。人物と危険物体をまとめて計算する。")
    names = ["近さ (−距離)", "近づく速さ", "物体の方を向いている (−なす角)", "見ている", "直前2秒に見た", "見ていて |ねじれ| 大", "視野が狭い (−視野の広さ)", "速さ"]
    scores = {n: [] for n in names}
    labels = []

    for person, c in persons.items():
        t = c["time"]
        if len(t) == 0:
            continue
        probe_idx = np.unique(np.searchsorted(t, np.arange(t[0], t[-1], 0.2)))
        probe_idx = probe_idx[probe_idx < len(t)]
        tp = t[probe_idx]
        for name in hazards:
            dist = c.get(f"dist_{name}")
            if dist is None:
                continue
            d = dist[probe_idx]
            sel = d > approach_distance
            if not sel.any():
                continue
            arrivals = np.array(sorted(a["arriveSec"] for a in approaches if a["person"] == person and a["target"] == name))
            k = np.searchsorted(arrivals, tp, side="right")
            nxt = np.where(k < len(arrivals), arrivals[np.minimum(k, len(arrivals) - 1)] if len(arrivals) else np.inf, np.inf)
            y = (nxt - tp) <= horizon

            looking_all = (c["target"] == name).astype(float)
            cum = np.concatenate([[0.0], np.cumsum(looking_all)])
            lo = np.searchsorted(t, tp - 2.0)
            recent = (cum[probe_idx + 1] - cum[lo]) > 0
            looking = looking_all[probe_idx] > 0
            twist = np.abs(c["twist"][probe_idx])

            feats = {
                "近さ (−距離)": -d,
                "近づく速さ": c[f"approach_{name}"][probe_idx],
                "物体の方を向いている (−なす角)": -c[f"bearing_{name}"][probe_idx],
                "見ている": looking.astype(float),
                "直前2秒に見た": recent.astype(float),
                "見ていて |ねじれ| 大": np.where(looking, np.nan_to_num(twist, nan=0.0), -1.0),
                "視野が狭い (−視野の広さ)": -c["scanWidth"][probe_idx],
                "速さ": c["speed"][probe_idx],
            }
            for n in names:
                scores[n].append(feats[n][sel])
            labels.append(y[sel])

    if not labels:
        s.findings.append("危険物体の距離の列がないため計算できません。")
        return s

    y = np.concatenate(labels).astype(bool)
    rows = []
    results = []
    for n in names:
        value = auc(np.concatenate(scores[n]).astype(float), y)
        results.append((n, value))
        rows.append([n, num(value, ".3f")])

    s.tables.append((f"正解(接近が始まる)のフレーム {int(y.sum())} / 全 {len(y)}", ["特徴量", "AUC"], rows))
    good = [n for n, v in sorted(results, key=lambda r: -(r[1] if not math.isnan(r[1]) else 0)) if not math.isnan(v) and v >= 0.65]
    if good:
        s.findings.append("手がかりになりそうな特徴量(AUC ≥ 0.65): " + "、".join(good))
    if y.sum() < 20:
        s.findings.append(f"接近の回数が少ない({int(y.sum())} フレーム)ので、値はあまり当てにならない。")
    return s


# ------------------------------------------------------------
# HTML
# ------------------------------------------------------------
PALETTE = ["#d1495b", "#2e86ab", "#3c9d4e", "#e3a008", "#8e5bd8", "#555555"]


def line_chart(x: np.ndarray, curves: dict[str, np.ndarray], x_label: str, y_label: str) -> str:
    width, height, left, right, top, bottom = 640, 300, 56, 16, 16, 44
    values = np.concatenate([v[~np.isnan(v)] for v in curves.values()]) if curves else np.array([])
    if len(values) == 0:
        return ""
    y_min, y_max = 0.0, float(np.max(values)) * 1.1
    sx = lambda v: left + (v - x[0]) / (x[-1] - x[0]) * (width - left - right)  # noqa: E731
    sy = lambda v: top + (1 - (v - y_min) / (y_max - y_min)) * (height - top - bottom)  # noqa: E731
    parts = [f'<svg viewBox="0 0 {width} {height}" class="chart" role="img">']
    for tick in np.linspace(y_min, y_max, 5):
        parts.append(f'<line x1="{left}" x2="{width - right}" y1="{sy(tick):.1f}" y2="{sy(tick):.1f}" class="grid"/>'
                     f'<text x="{left - 6}" y="{sy(tick) + 4:.1f}" text-anchor="end">{tick:.0f}</text>')
    for tick in np.arange(-2, 2.01, 0.5):
        parts.append(f'<text x="{sx(tick):.1f}" y="{height - bottom + 16}" text-anchor="middle">{tick:+.1f}</text>')
    parts.append(f'<line x1="{sx(0):.1f}" x2="{sx(0):.1f}" y1="{top}" y2="{height - bottom}" class="zero"/>')
    for i, (name, curve) in enumerate(curves.items()):
        color = PALETTE[i % len(PALETTE)]
        pts = " ".join(f"{sx(a):.1f},{sy(b):.1f}" for a, b in zip(x, curve) if not math.isnan(b))
        parts.append(f'<polyline points="{pts}" fill="none" stroke="{color}" stroke-width="2"/>')
        if not np.all(np.isnan(curve)):
            best = int(np.nanargmin(curve))
            parts.append(f'<circle cx="{sx(x[best]):.1f}" cy="{sy(curve[best]):.1f}" r="4" fill="{color}"/>')
        parts.append(f'<text x="{width - right - 4}" y="{top + 14 + i * 16}" text-anchor="end" fill="{color}">{html.escape(name)}</text>')
    parts.append(f'<text x="{(left + width - right) / 2}" y="{height - 6}" text-anchor="middle">{html.escape(x_label)}</text>')
    parts.append(f'<text x="14" y="{(top + height - bottom) / 2}" text-anchor="middle" transform="rotate(-90 14 {(top + height - bottom) / 2})">{html.escape(y_label)}</text>')
    parts.append("</svg>")
    return "".join(parts)


def render_html(title: str, meta: list[str], sections: list[Section]) -> str:
    body = []
    for s in sections:
        body.append(f'<section><h2><span class="key">{s.key}</span> {html.escape(s.title)}</h2>')
        body.append(f'<p class="note">{html.escape(s.note)}</p>')
        if s.findings:
            body.append("<ul class=\"findings\">" + "".join(f"<li>{html.escape(f)}</li>" for f in s.findings) + "</ul>")
        if s.svg:
            body.append(s.svg)
        for caption, header, rows in s.tables:
            body.append("<table>")
            if caption:
                body.append(f"<caption>{html.escape(caption)}</caption>")
            body.append("<tr>" + "".join(f"<th>{html.escape(h)}</th>" for h in header) + "</tr>")
            for row in rows:
                body.append("<tr>" + "".join(f"<td>{html.escape(v)}</td>" for v in row) + "</tr>")
            body.append("</table>")
        body.append("</section>")

    return f"""<!doctype html>
<html lang="ja"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>{html.escape(title)}</title>
<style>
:root {{ --bg:#fafafa; --fg:#222; --muted:#666; --line:#ddd; --card:#fff; --accent:#2e86ab; }}
@media (prefers-color-scheme: dark) {{ :root {{ --bg:#16181b; --fg:#e8e8e8; --muted:#9aa; --line:#333; --card:#1f2226; --accent:#6cb6d9; }} }}
body {{ background:var(--bg); color:var(--fg); font-family:system-ui, "Yu Gothic UI", sans-serif; margin:0 auto; max-width:960px; padding:16px; }}
h1 {{ font-size:1.4em; }} h2 {{ font-size:1.1em; margin-top:0; }}
.key {{ color:var(--accent); }}
section {{ background:var(--card); border:1px solid var(--line); border-radius:8px; padding:16px; margin:16px 0; }}
.note, .meta {{ color:var(--muted); font-size:0.9em; }}
.findings li {{ margin:4px 0; }}
table {{ border-collapse:collapse; margin:12px 0; font-size:0.9em; display:block; overflow-x:auto; }}
caption {{ text-align:left; color:var(--muted); padding-bottom:4px; }}
th, td {{ border:1px solid var(--line); padding:4px 8px; text-align:right; white-space:nowrap; }}
th:first-child, td:first-child {{ text-align:left; }}
.chart {{ width:100%; max-width:640px; }}
.chart text {{ fill:var(--muted); font-size:11px; }}
.chart .grid {{ stroke:var(--line); }} .chart .zero {{ stroke:var(--muted); stroke-dasharray:4 3; }}
</style></head><body>
<h1>{html.escape(title)}</h1>
<p class="meta">{"<br>".join(html.escape(m) for m in meta)}</p>
{"".join(body)}
</body></html>
"""


def print_sections(sections: list[Section]) -> None:
    for s in sections:
        print(f"\n== {s.key} {s.title}")
        for f in s.findings:
            print(f"  * {f}")
        for caption, header, rows in s.tables:
            if caption:
                print(f"  [{caption}]")
            widths = [max(len(str(x)) for x in col) for col in zip(header, *rows)]
            print("  " + "  ".join(h.ljust(w) for h, w in zip(header, widths)))
            for row in rows:
                print("  " + "  ".join(v.ljust(w) for v, w in zip(row, widths)))


# ------------------------------------------------------------
def main() -> int:
    parser = argparse.ArgumentParser(description="視線と行動の関係の仮説を、シーン6の特徴量で確かめる")
    home_data_root.add_argument(parser)
    parser.add_argument("experiment", nargs="?", help="実験の名前 (<データの場所>/<実験の名前>)")
    parser.add_argument("subject", nargs="?", help="実験対象者 (Skeleton/<実験対象者>)")
    parser.add_argument("--source", help="骨格データ (gaze_behavior_<骨格>_frames.csv の <骨格>。省略すると一覧から選ぶ)")
    parser.add_argument("--person", help="この人物だけ")
    parser.add_argument("--horizon", type=float, default=3.0, help="H5: 何秒以内に接近するか (既定 3)")
    parser.add_argument("--twist", type=float, help="H2: ねじれているとする角度 (省略時はシーン6の Twist Threshold)")
    args = parser.parse_args()

    data_root = home_data_root.choose_data_root(args.data_root)
    if data_root is None:
        return 1

    experiment = args.experiment or _choose("実験の名前", _subdirs(data_root))
    if not experiment:
        return 1

    subject = args.subject or _choose("実験対象者", _subdirs(data_root / experiment / "Skeleton"))
    if not subject:
        return 1

    folder = data_root / experiment / "Skeleton" / subject / "Analysis" / "GazeBehavior"
    sources = sorted(p.name[len(PREFIX):-len(FRAMES_SUFFIX)] for p in folder.glob(f"{PREFIX}*{FRAMES_SUFFIX}"))
    source = args.source or _choose("骨格データ", sources)
    if not source:
        print(f"シーン6の結果(gaze_behavior_*_frames.csv)がありません。シーン6で Analyze All を押してください: {folder}")
        return 1

    frames_path = folder / f"{PREFIX}{source}{FRAMES_SUFFIX}"
    if not frames_path.exists():
        print(f"ファイルがありません: {frames_path}")
        return 1

    params_path = folder / f"{PREFIX}{source}_params.json"
    params = json.loads(params_path.read_text(encoding="utf-8")) if params_path.exists() else {}

    print(f"読み込み中: {frames_path}")
    persons, targets = load_frames(frames_path)
    approaches = load_approaches(folder / f"{PREFIX}{source}_approaches.csv")

    if args.person:
        if args.person not in persons:
            print(f"人物がいません: {args.person} (いるのは {', '.join(persons)})")
            return 1
        persons = {args.person: persons[args.person]}
        approaches = [a for a in approaches if a["person"] == args.person]

    hazards = [h for h in params.get("hazardTargets", targets) if h in targets]
    twist_threshold = args.twist if args.twist is not None else float(params.get("twistThreshold", 30.0))
    max_gap = 0.2

    sections = [
        h1_gaze_leads(persons, max_gap),
        h2_twist(persons, targets, twist_threshold, max_gap),
        h3_scan(persons, approaches, max_gap),
        h4_look_before(persons, approaches, hazards, params),
        h5_prediction(persons, approaches, hazards, params, args.horizon),
    ]

    meta = [
        f"{experiment} / {subject} / {source}  (シーン6 {params.get('createdAt', '?')}、"
        f"{params.get('rangeStartSec', 0):.0f}–{params.get('rangeEndSec', 0):.0f} 秒)",
        "人物: " + ", ".join(f"{p} ({len(c['time'])} frames)" for p, c in persons.items()),
        f"対象物体: {', '.join(targets)} / 危険物体: {', '.join(hazards) or 'なし'} / 接近 {len(approaches)} 回",
        "頭の向き(視線)は大雑把なので、差が小さいものは誤差の可能性が高い。動画で場面を確かめること。",
    ]

    print_sections(sections)
    out_path = folder / f"{PREFIX}{source}_report.html"
    out_path.write_text(render_html(f"視線と行動 {subject}", meta, sections), encoding="utf-8")
    print(f"\n保存しました: {out_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
