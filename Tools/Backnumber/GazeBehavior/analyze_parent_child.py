"""子供と親の位置関係・頭の向きから、子供が物に近づく場面について簡単な考察を立てるための集計。

頭の向き(視線)は誤差が大きいので、細いコーン(シーン4〜6の注視判定)ではなく、
「相手が視野に入っているか(頭の向きから ±--view 度以内)」「相手に背を向けているか(--away 度以上)」という
大まかな向きと、より正確な位置(距離)を使う。

シーン6(6GazeBehaviorAnalyze)の Analyze All で保存した
  Skeleton/<実験対象者>/Analysis/GazeBehavior/gaze_behavior_<骨格>_frames.csv / _approaches.csv / _params.json
を読み、子供と親を同じ時刻で並べて次を出す(画面と HTML)。

  P1 ふだんの位置関係   … 親子の距離の分布、親の視野に子供が入っている割合、子供の視野に親が入っている割合
  P2 接近の前後の変化   … 子供が物に近づいた時刻(0秒)の前後 ±--window 秒で、親子の距離・親が子供を視野に入れている割合・
                         子供が親を視野に入れている割合・子供の速さの平均。ふだんの値(点線)と比べる。
                         接近の前に親が見ていない/離れる、接近の前に子供が親の方を見る(確認する)、接近の後に親が向く・近づく、などが読める
  P3 接近の分類         … 接近1回ごとに、親のそばだったか・親がその物の近くにいたか・親が見ていたか・子供が直前に親の方を見たか
  P4 地図               … 上から見た子供(色)と親(灰)の位置と、子供が物に近づいた場所

使い方:
  python Tools/GazeBehavior/analyze_parent_child.py <実験の名前> <実験対象者>
  --child child --parent mother … 人物の名前(シーン3のグループ名。省略時は child / mother、無ければ最初の2人)
  --view 60    … 視野に入っているとする角度(頭の向きから±度)
  --away 120   … 背を向けているとする角度
  --near 1.5   … 親のそばとする親子の距離(m)
  --window 10  … P2 で接近の前後に見る秒数
書き出すもの(毎回上書き):
  Skeleton/<実験対象者>/Analysis/GazeBehavior/gaze_behavior_<骨格>_parent_child.html
"""

from __future__ import annotations

import argparse
import html
import json
import math
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import home_data_root  # noqa: E402  Tools/home_data_root.py
from analyze_gaze_behavior import (  # noqa: E402  同じフォルダ
    FRAMES_SUFFIX, PALETTE, PREFIX, Section, _choose, _subdirs, frame_weights, load_approaches, load_frames,
    pct, print_sections, ratio, render_html, unit, value_at, wrap)


# ------------------------------------------------------------
# 子供の時刻に親を並べる
# ------------------------------------------------------------
def yaw_to(dx: np.ndarray, dz: np.ndarray) -> np.ndarray:
    return np.degrees(np.arctan2(dx, dz))


def join(child: dict, parent: dict, view: float, away: float) -> dict[str, np.ndarray]:
    """子供のフレームごとに、親の位置・向きと、2人の関係(距離・視野に入っているか)"""
    t = child["time"]
    j = {"time": t}
    for key in ("pelvisX", "pelvisZ", "headX", "headZ", "headYaw"):
        j[f"p_{key}"] = value_at(parent["time"], parent[key], t, 0.1)

    dx = j["p_pelvisX"] - child["pelvisX"]
    dz = j["p_pelvisZ"] - child["pelvisZ"]
    j["distance"] = np.hypot(dx, dz)

    # 頭の位置どうしの向き(頭の位置が無ければ骨盤)
    cx = np.where(np.isnan(child["headX"]), child["pelvisX"], child["headX"])
    cz = np.where(np.isnan(child["headZ"]), child["pelvisZ"], child["headZ"])
    px = np.where(np.isnan(j["p_headX"]), j["p_pelvisX"], j["p_headX"])
    pz = np.where(np.isnan(j["p_headZ"]), j["p_pelvisZ"], j["p_headZ"])

    # 親から見た子供の方向と親の頭の向きのなす角 / 子供から見た親の方向と子供の頭の向きのなす角
    parent_angle = np.abs(wrap(yaw_to(cx - px, cz - pz) - j["p_headYaw"]))
    child_angle = np.abs(wrap(yaw_to(px - cx, pz - cz) - child["headYaw"]))
    j["parentAngle"] = parent_angle
    j["childAngle"] = child_angle
    j["parentSees"] = np.where(np.isnan(parent_angle), np.nan, (parent_angle <= view).astype(float))
    j["parentAway"] = np.where(np.isnan(parent_angle), np.nan, (parent_angle >= away).astype(float))
    j["childSees"] = np.where(np.isnan(child_angle), np.nan, (child_angle <= view).astype(float))
    j["speed"] = child["speed"]
    return j


def weighted_mean(values: np.ndarray, w: np.ndarray) -> float:
    ok = ~np.isnan(values)
    return ratio(float(np.sum(values[ok] * w[ok])), float(w[ok].sum()))


# ------------------------------------------------------------
# 集計
# ------------------------------------------------------------
def p1_overall(j: dict, names: tuple[str, str], view: float, away: float, near: float) -> tuple[Section, dict]:
    child_name, parent_name = names
    s = Section("P1", "ふだんの位置関係",
                f"2人とも映っている時間について。視野に入っている = 頭の向きから ±{view:.0f}° 以内、背を向けている = {away:.0f}° 以上。"
                "頭の向きの誤差が大きいので、細かい角度ではなく大まかな向きで見る。")
    w = frame_weights(j["time"], 0.2)
    both = ~np.isnan(j["distance"])
    wb = np.where(both, w, 0.0)
    together = float(wb.sum())

    bins = [0, 1, 1.5, 2, 3, 100]
    rows = []
    for lo, hi in zip(bins[:-1], bins[1:]):
        sel = both & (j["distance"] >= lo) & (j["distance"] < hi)
        rows.append([f"{lo}–{hi} m" if hi < 100 else f"{lo} m–", f"{w[sel].sum():.0f} s", pct(ratio(w[sel].sum(), together)),
                     pct(weighted_mean(np.where(sel, j["parentSees"], np.nan), w)),
                     pct(weighted_mean(np.where(sel, j["childSees"], np.nan), w))])
    s.tables.append(("親子の距離ごとの時間と、そのときの向き",
                     ["親子の距離", "時間", "割合", f"{parent_name} の視野に {child_name}", f"{child_name} の視野に {parent_name}"], rows))

    base = {
        "distance": weighted_mean(np.where(both, j["distance"], np.nan), w),
        "parentSees": weighted_mean(j["parentSees"], w),
        "parentAway": weighted_mean(j["parentAway"], w),
        "childSees": weighted_mean(j["childSees"], w),
        "speed": weighted_mean(np.where(both, j["speed"], np.nan), w),
    }
    near_ratio = ratio(float(w[both & (j["distance"] < near)].sum()), together)
    s.findings.append(f"2人とも映っている時間 {together:.0f} 秒。親子の距離の平均 {unit(base['distance'], ' m', '.2f')}、"
                      f"{near:.1f} m 以内にいる時間 {pct(near_ratio)}")
    s.findings.append(f"{parent_name} の視野に {child_name} が入っている {pct(base['parentSees'])}、"
                      f"背を向けている {pct(base['parentAway'])}、{child_name} の視野に {parent_name} が入っている {pct(base['childSees'])}")
    return s, base


def p2_event_aligned(j: dict, events: list[dict], base: dict, names: tuple[str, str], window: float,
                     key: str = "P2", subset: str = "") -> tuple[Section, dict]:
    child_name, parent_name = names
    title = f"{child_name} が物に近づく前後で、親子の関係はどう変わる?" + (f"({subset})" if subset else "")
    s = Section(key, title,
                f"{child_name} が物に近づいた時刻を 0 秒にそろえて、前後 {window:.0f} 秒の平均({len(events)} 回)。点線はふだん(P1)の値。"
                "0 秒より前に線が点線から離れていれば、接近の「前ぶれ」の候補。後なら、接近への親の反応。")
    lags = np.round(np.arange(-window, window + 0.001, 0.5), 2)
    t = j["time"]
    keys = ["distance", "parentSees", "childSees", "parentAway", "speed"]
    curves = {k: np.full(len(lags), np.nan) for k in keys}
    counts = np.zeros(len(lags), dtype=int)

    if events:
        arrive = np.array([e["arriveSec"] for e in events])
        for i, lag in enumerate(lags):
            for k in keys:
                values = value_at(t, j[k], arrive + lag, 0.2)
                if np.sum(~np.isnan(values)) >= 3:
                    curves[k][i] = float(np.nanmean(values))
            counts[i] = int(np.sum(~np.isnan(value_at(t, j["distance"], arrive + lag, 0.2))))

    s.svg = (event_chart(lags, {"親子の距離 (m)": curves["distance"]}, {"親子の距離 (m)": base["distance"]}, "親子の距離 (m)")
             + event_chart(lags, {f"{parent_name} の視野に {child_name}": curves["parentSees"] * 100,
                                  f"{child_name} の視野に {parent_name}": curves["childSees"] * 100,
                                  f"{parent_name} が背を向けている": curves["parentAway"] * 100},
                           {f"{parent_name} の視野に {child_name}": base["parentSees"] * 100,
                            f"{child_name} の視野に {parent_name}": base["childSees"] * 100,
                            f"{parent_name} が背を向けている": base["parentAway"] * 100}, "割合 (%)", y_max=100)
             + event_chart(lags, {f"{child_name} の速さ (m/s)": curves["speed"]}, {f"{child_name} の速さ (m/s)": base["speed"]}, "速さ (m/s)"))

    def at(k: str, lag: float) -> float:
        return curves[k][int(np.argmin(np.abs(lags - lag)))]

    rows = []
    for label, lag in [(f"−{window:.0f} s", -window), ("−5 s", -5), ("−2 s", -2), ("0 s(着いた)", 0), ("+2 s", 2), ("+5 s", 5), (f"+{window:.0f} s", window)]:
        rows.append([label, unit(at("distance", lag), " m", ".2f"), pct(at("parentSees", lag)), pct(at("parentAway", lag)),
                     pct(at("childSees", lag)), unit(at("speed", lag), " m/s", ".2f"),
                     str(counts[int(np.argmin(np.abs(lags - lag)))])])
    rows.append(["ふだん", unit(base["distance"], " m", ".2f"), pct(base["parentSees"]), pct(base["parentAway"]),
                 pct(base["childSees"]), unit(base["speed"], " m/s", ".2f"), "-"])
    s.tables.append(("", ["時刻", "親子の距離", f"{parent_name} の視野に {child_name}", f"{parent_name} が背を向けている",
                          f"{child_name} の視野に {parent_name}", f"{child_name} の速さ", "2人とも映っている回数"], rows))

    # 読み取り(差が大きいものだけ)
    def compare(k: str, lag: float, label: str, threshold: float, fmt) -> None:
        v, b = at(k, lag), base[k]
        if math.isnan(v) or math.isnan(b):
            return
        if v - b >= threshold:
            s.findings.append(f"{label}: ふだんより多い/大きい({fmt(v)}、ふだん {fmt(b)})")
        elif b - v >= threshold:
            s.findings.append(f"{label}: ふだんより少ない/小さい({fmt(v)}、ふだん {fmt(b)})")

    compare("distance", -2, "接近の2秒前の親子の距離", 0.3, lambda v: f"{v:.2f} m")
    compare("parentSees", -2, f"接近の2秒前に {parent_name} の視野に {child_name}", 0.1, pct)
    compare("childSees", -5, f"接近の5秒前に {child_name} の視野に {parent_name}", 0.1, pct)
    compare("childSees", -2, f"接近の2秒前に {child_name} の視野に {parent_name}", 0.1, pct)
    compare("parentSees", 5, f"接近の5秒後に {parent_name} の視野に {child_name}", 0.1, pct)
    compare("distance", 5, "接近の5秒後の親子の距離", 0.3, lambda v: f"{v:.2f} m")
    if len(events) < 20:
        s.findings.append(f"接近が {len(events)} 回しかないので、線のでこぼこは偶然の可能性が高い。")
    return s, curves


def parent_at_target(parent: dict, event: dict, distance: float = 1.0) -> bool:
    """子供が着いた時刻に、親がその物の distance m 以内にいたか"""
    column = parent.get(f"dist_{event['target']}")
    if column is None:
        return False
    d = float(value_at(parent["time"], column, np.array([event["arriveSec"]]), 0.2)[0])
    return not math.isnan(d) and d <= distance


def p3_classify(j: dict, child: dict, parent: dict, events: list[dict], names: tuple[str, str], near: float) -> Section:
    child_name, parent_name = names
    s = Section("P3", f"{child_name} の接近を、親との関係で分ける",
                f"着いた時刻で: 親のそば = 親子の距離 {near:.1f} m 以内、親が物の近く = 親からその物まで 1 m 以内、"
                "親が見ている = 親の視野に子供が入っている。子供が親を確認 = 着く前 5 秒の半分以上、子供の視野に親が入っていた(ふだんの割合は P1)。")
    t = j["time"]
    rows = []
    groups: dict[str, list[int]] = {}

    for e in events:
        a = e["arriveSec"]
        d = float(value_at(t, j["distance"], np.array([a]), 0.2)[0])
        sees = float(value_at(t, j["parentSees"], np.array([a]), 0.2)[0])
        parent_dist_target = float(value_at(parent["time"], parent.get(f"dist_{e['target']}", np.full(len(parent["time"]), np.nan)),
                                            np.array([a]), 0.2)[0])
        lo, hi = np.searchsorted(t, a - 5.0), np.searchsorted(t, a, side="right")
        window_sees = j["childSees"][lo:hi]
        checked = bool(np.sum(~np.isnan(window_sees)) > 0 and np.nanmean(window_sees) >= 0.5)

        near_parent = not math.isnan(d) and d <= near
        parent_at_target = not math.isnan(parent_dist_target) and parent_dist_target <= 1.0
        parent_sees = sees == 1.0
        rows.append([f"{a:.1f}", e["target"], unit(d, " m", ".2f"), "○" if parent_at_target else ("-" if math.isnan(parent_dist_target) else ""),
                     "○" if parent_sees else ("-" if math.isnan(sees) else ""), "○" if checked else "",
                     "○" if e["lookSeconds"] > 0 else ""])

        for key, flag in [("親のそば", near_parent), ("親から離れて", not near_parent and not math.isnan(d)),
                          ("親がその物の近く", parent_at_target), ("親が見ている", parent_sees),
                          ("親が見ていない", not parent_sees and not math.isnan(sees)), ("子供が直前に親を確認(5秒の半分以上)", checked),
                          ("親が映っていない", math.isnan(d))]:
            groups.setdefault(key, [0, 0])
            groups[key][0] += 1 if flag else 0
            groups[key][1] += 1

    total = len(events)
    summary = [[k, f"{v[0]} / {total}", pct(ratio(v[0], total))] for k, v in groups.items()]
    s.tables.append(("まとめ", ["分類", "回数", "割合"], summary))
    s.tables.append(("接近1回ごと(○ = あてはまる、- = 親が映っていない)",
                     ["着いた時刻 (s)", "物", "親子の距離", "親が物の近く", "親が見ている", "子供が親を確認", "子供が物を見ていた"], rows))

    if total:
        far = groups.get("親から離れて", [0, 0])[0]
        unseen = groups.get("親が見ていない", [0, 0])[0]
        s.findings.append(f"親から {near:.1f} m 以上離れた場所での接近 {far} 回、親が見ていない(視野の外の)接近 {unseen} 回 / 全 {total} 回")
    return s


def p4_map(child: dict, parent: dict, events: list[dict], names: tuple[str, str]) -> Section:
    child_name, parent_name = names
    s = Section("P4", "上から見た位置", f"{child_name}(色の薄い点)と {parent_name}(灰色の点)のいた場所と、{child_name} が物に近づいた場所(物ごとの色の丸)。")
    s.svg = map_chart(child, parent, events)
    return s


# ------------------------------------------------------------
# 図
# ------------------------------------------------------------
def event_chart(x: np.ndarray, curves: dict[str, np.ndarray], baselines: dict[str, float], y_label: str, y_max: float | None = None) -> str:
    width, height, left, right, top, bottom = 640, 240, 56, 16, 16, 40
    values = np.concatenate([v[~np.isnan(v)] for v in curves.values()] + [np.array([b for b in baselines.values() if not math.isnan(b)])])
    if len(values) == 0:
        return ""
    y_min = 0.0
    y_top = y_max if y_max is not None else max(float(np.max(values)) * 1.15, 1e-6)
    sx = lambda v: left + (v - x[0]) / (x[-1] - x[0]) * (width - left - right)  # noqa: E731
    sy = lambda v: top + (1 - (v - y_min) / (y_top - y_min)) * (height - top - bottom)  # noqa: E731
    parts = [f'<svg viewBox="0 0 {width} {height}" class="chart" role="img">']
    for tick in np.linspace(y_min, y_top, 5):
        parts.append(f'<line x1="{left}" x2="{width - right}" y1="{sy(tick):.1f}" y2="{sy(tick):.1f}" class="grid"/>'
                     f'<text x="{left - 6}" y="{sy(tick) + 4:.1f}" text-anchor="end">{tick:.1f}</text>')
    step = 2.0 if x[-1] - x[0] <= 20 else 5.0
    for tick in np.arange(math.ceil(x[0] / step) * step, x[-1] + 0.001, step):
        parts.append(f'<text x="{sx(tick):.1f}" y="{height - bottom + 16}" text-anchor="middle">{tick:+.0f}</text>')
    parts.append(f'<line x1="{sx(0):.1f}" x2="{sx(0):.1f}" y1="{top}" y2="{height - bottom}" class="zero"/>')
    for i, (name, curve) in enumerate(curves.items()):
        color = PALETTE[i % len(PALETTE)]
        b = baselines.get(name, math.nan)
        if not math.isnan(b):
            parts.append(f'<line x1="{left}" x2="{width - right}" y1="{sy(b):.1f}" y2="{sy(b):.1f}" stroke="{color}" stroke-dasharray="5 4" opacity="0.7"/>')
        pts = " ".join(f"{sx(a):.1f},{sy(v):.1f}" for a, v in zip(x, curve) if not math.isnan(v))
        parts.append(f'<polyline points="{pts}" fill="none" stroke="{color}" stroke-width="2"/>')
        parts.append(f'<text x="{width - right - 4}" y="{top + 14 + i * 16}" text-anchor="end" fill="{color}">{html.escape(name)}</text>')
    parts.append(f'<text x="{(left + width - right) / 2}" y="{height - 6}" text-anchor="middle">接近までの時刻 (秒)  ← 前 / 後 →</text>')
    parts.append(f'<text x="14" y="{(top + height - bottom) / 2}" text-anchor="middle" transform="rotate(-90 14 {(top + height - bottom) / 2})">{html.escape(y_label)}</text>')
    parts.append("</svg>")
    return "".join(parts)


def map_chart(child: dict, parent: dict, events: list[dict]) -> str:
    xs = np.concatenate([child["pelvisX"], parent["pelvisX"]])
    zs = np.concatenate([child["pelvisZ"], parent["pelvisZ"]])
    ok = ~np.isnan(xs) & ~np.isnan(zs)
    if not ok.any():
        return ""
    x0, x1 = np.percentile(xs[ok], [0.5, 99.5])
    z0, z1 = np.percentile(zs[ok], [0.5, 99.5])
    pad = 0.3
    x0, x1, z0, z1 = x0 - pad, x1 + pad, z0 - pad, z1 + pad
    size = 560
    scale = size / max(x1 - x0, z1 - z0)
    width, height = (x1 - x0) * scale + 20, (z1 - z0) * scale + 40
    sx = lambda v: 10 + (v - x0) * scale  # noqa: E731
    sz = lambda v: 10 + (z1 - v) * scale  # noqa: E731  奥(+Z)を上に
    parts = [f'<svg viewBox="0 0 {width:.0f} {height:.0f}" class="chart" style="max-width:{width:.0f}px" role="img">']
    for data, color, r in [(parent, "#888888", 1.2), (child, PALETTE[0], 1.2)]:
        step = max(1, len(data["time"]) // 4000)
        for x, z in zip(data["pelvisX"][::step], data["pelvisZ"][::step]):
            if not (math.isnan(x) or math.isnan(z)):
                parts.append(f'<circle cx="{sx(x):.1f}" cy="{sz(z):.1f}" r="{r}" fill="{color}" opacity="0.18"/>')
    targets = sorted({e["target"] for e in events})
    t = child["time"]
    for e in events:
        k = int(np.clip(np.searchsorted(t, e["arriveSec"]), 0, len(t) - 1))
        x, z = child["pelvisX"][k], child["pelvisZ"][k]
        if math.isnan(x) or math.isnan(z):
            continue
        color = PALETTE[(targets.index(e["target"]) + 1) % len(PALETTE)]
        parts.append(f'<circle cx="{sx(x):.1f}" cy="{sz(z):.1f}" r="5" fill="none" stroke="{color}" stroke-width="2"/>')
    for i, name in enumerate(targets):
        color = PALETTE[(i + 1) % len(PALETTE)]
        parts.append(f'<text x="{14 + i * 90}" y="{height - 12:.0f}" fill="{color}">○ {html.escape(name)}</text>')
    parts.append(f'<text x="{width - 10:.0f}" y="{height - 12:.0f}" text-anchor="end">1 m = {scale:.0f} px、上が +Z</text>')
    parts.append("</svg>")
    return "".join(parts)


# ------------------------------------------------------------
def main() -> int:
    parser = argparse.ArgumentParser(description="子供と親の位置関係・頭の向きから、接近の場面の考察を立てる")
    home_data_root.add_argument(parser)
    parser.add_argument("experiment", nargs="?")
    parser.add_argument("subject", nargs="?")
    parser.add_argument("--source", help="骨格データ (gaze_behavior_<骨格>_frames.csv の <骨格>)")
    parser.add_argument("--child", default="child")
    parser.add_argument("--parent", default="mother")
    parser.add_argument("--view", type=float, default=60.0, help="視野に入っているとする角度 (頭の向きから±度)")
    parser.add_argument("--away", type=float, default=120.0, help="背を向けているとする角度")
    parser.add_argument("--near", type=float, default=1.5, help="親のそばとする距離 (m)")
    parser.add_argument("--window", type=float, default=10.0, help="P2 で接近の前後に見る秒数")
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

    params_path = folder / f"{PREFIX}{source}_params.json"
    params = json.loads(params_path.read_text(encoding="utf-8")) if params_path.exists() else {}
    print(f"読み込み中: {folder / (PREFIX + source + FRAMES_SUFFIX)}")
    persons, _ = load_frames(folder / f"{PREFIX}{source}{FRAMES_SUFFIX}")
    approaches = load_approaches(folder / f"{PREFIX}{source}_approaches.csv")

    child_name, parent_name = args.child, args.parent
    if child_name not in persons or parent_name not in persons:
        if len(persons) < 2:
            print(f"人物が2人いません: {', '.join(persons)}")
            return 1
        child_name, parent_name = list(persons)[:2]
        print(f"--child / --parent の人物がいないので {child_name} / {parent_name} を使います(いるのは {', '.join(persons)})")

    names = (child_name, parent_name)
    child, parent = persons[child_name], persons[parent_name]
    events = [a for a in approaches if a["person"] == child_name]
    j = join(child, parent, args.view, args.away)

    s1, base = p1_overall(j, names, args.view, args.away, args.near)
    s2, _ = p2_event_aligned(j, events, base, names, args.window)
    # 親がその物のそばにいるときは、物の方を見ると親も視野に入るので、親から離れた物への接近と分けて見る
    with_parent = [e for e in events if parent_at_target(parent, e)]
    without_parent = [e for e in events if not parent_at_target(parent, e)]
    s2a, _ = p2_event_aligned(j, with_parent, base, names, args.window, "P2a", f"{parent_name} がその物の 1 m 以内")
    s2b, _ = p2_event_aligned(j, without_parent, base, names, args.window, "P2b", f"{parent_name} がその物から離れている")
    sections = [s1, s2, s2a, s2b, p3_classify(j, child, parent, events, names, args.near), p4_map(child, parent, events, names)]

    meta = [
        f"{experiment} / {subject} / {source}  (シーン6 {params.get('createdAt', '?')})",
        f"子供: {child_name}  親: {parent_name}  / {child_name} の接近 {len(events)} 回"
        f"(物まで {params.get('approachDistance', 0.6):.1f} m 以内。物: {', '.join(params.get('hazardTargets', []))})",
        f"視野に入っている = 頭の向きから ±{args.view:.0f}°、背を向けている = {args.away:.0f}° 以上、親のそば = {args.near:.1f} m 以内",
        "頭の向きは誤差が大きいので、割合の差が10ポイント未満くらいのものは誤差かもしれない。気になる場面はシーン6と動画で確かめる。",
    ]
    print_sections(sections)
    out_path = folder / f"{PREFIX}{source}_parent_child.html"
    out_path.write_text(render_html(f"親子の位置関係と向き {subject}", meta, sections), encoding="utf-8")
    print(f"\n保存しました: {out_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
