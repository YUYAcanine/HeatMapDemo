"""2つの骨格データ(例: キネクトの filtered_HeadJoints と、画像から推定した image_GazeElseHead)で、
シーン6の特徴量と、そこから出した結果が似たものになるかを比べる。

シーン6で骨格データ(Skeleton Source)を切り替えて、それぞれ Analyze All で保存した
  Skeleton/<実験対象者>/Analysis/GazeBehavior/gaze_behavior_<骨格>_frames.csv / _approaches.csv / _params.json
を2つ読み、人物の名前(シーン3のグループ名)と時刻を合わせて比べる(画面と HTML)。

  S1 データの量     … 人物ごとに、映っていた時間・頭の向き/肩の向き/速さが求まった時間
  S2 同じ時刻の差   … 骨盤の位置・頭の向き(水平/上下)・肩の向き・ねじれ・速さ・視野の広さの差(中央値など)と相関
  S3 注視対象の一致 … 同じ時刻に同じ対象物体を見ていた(どちらも見ていないも含む)割合と κ 係数
  S4 接近の一致     … 片方で見つかった接近が、もう片方でも ±--match 秒以内に(同じ人物・同じ物で)見つかった割合
  S5 結果の比較     … analyze_gaze_behavior.py(H5 の AUC)と analyze_parent_child.py(P1 / P2 / P2b)の主な数字を並べる。
                      どちらのデータでも同じ向きの結果なら、その考察は骨格の推定方法によらないと言える

使い方:
  python Tools/GazeBehavior/compare_sources.py <実験の名前> <実験対象者> --a filtered_HeadJoints --b image_GazeElseHead
  (--a / --b を省くと一覧から選ぶ)
  --child child --parent mother … 親子の人物の名前(analyze_parent_child.py と同じ)
書き出すもの(毎回上書き):
  Skeleton/<実験対象者>/Analysis/GazeBehavior/compare_<a>_vs_<b>.html
"""

from __future__ import annotations

import argparse
import json
import math
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import home_data_root  # noqa: E402  Tools/home_data_root.py
from analyze_gaze_behavior import (  # noqa: E402
    FRAMES_SUFFIX, PREFIX, Section, _subdirs, _choose, frame_weights, h5_prediction, load_approaches, load_frames,
    num, pct, print_sections, ratio, render_html, unit, value_at, wrap)
from analyze_parent_child import join, p1_overall, p2_event_aligned, parent_at_target  # noqa: E402

# 同じ時刻とみなす差(秒)
TIME_TOLERANCE = 0.05


class Source:
    def __init__(self, folder: Path, name: str):
        self.name = name
        self.persons, self.targets = load_frames(folder / f"{PREFIX}{name}{FRAMES_SUFFIX}")
        self.approaches = load_approaches(folder / f"{PREFIX}{name}_approaches.csv")
        params_path = folder / f"{PREFIX}{name}_params.json"
        self.params = json.loads(params_path.read_text(encoding="utf-8")) if params_path.exists() else {}


def aligned(a: dict, b: dict, column: str) -> tuple[np.ndarray, np.ndarray]:
    """a の時刻に b の値を合わせた (a の値, b の値)"""
    return a[column], value_at(b["time"], b[column], a["time"], TIME_TOLERANCE)


# ------------------------------------------------------------
def s1_amount(A: Source, B: Source) -> Section:
    s = Section("S1", "データの量", "人物ごとに、映っていた時間と、それぞれの値が求まった時間(秒)。片方だけ極端に少ないと、比べる意味が薄い。")
    rows = []
    for person in sorted(set(A.persons) | set(B.persons)):
        row = [person]
        for src in (A, B):
            c = src.persons.get(person)
            if c is None:
                row += ["-", "-", "-", "-"]
                continue
            w = frame_weights(c["time"], 0.2)
            row += [f"{w.sum():.0f}"] + [f"{w[~np.isnan(c[k])].sum():.0f}" for k in ("headYaw", "shoulderYaw", "speed")]
        rows.append(row)
    s.tables.append(("", ["人物",
                          f"{A.name} 映っていた", "頭の向き", "肩の向き", "速さ",
                          f"{B.name} 映っていた", "頭の向き", "肩の向き", "速さ"], rows))
    return s


def s2_differences(A: Source, B: Source) -> Section:
    s = Section("S2", "同じ時刻の値の差",
                f"2つのデータの同じ人物・同じ時刻(±{TIME_TOLERANCE:.2f} 秒)のフレームどうしの差。"
                "角度は差の絶対値。相関は 1 に近いほど同じように変化している(角度は円周の相関)。")
    rows = []
    for person in sorted(set(A.persons) & set(B.persons)):
        a, b = A.persons[person], B.persons[person]

        ax, bx = aligned(a, b, "pelvisX")
        az, bz = aligned(a, b, "pelvisZ")
        pos = np.hypot(ax - bx, az - bz)
        pos = pos[~np.isnan(pos)]
        rows.append([person, "骨盤の水平位置", f"{len(pos)}", unit(np.median(pos) if len(pos) else math.nan, " m", ".2f"),
                     unit(np.percentile(pos, 90) if len(pos) else math.nan, " m", ".2f"), "-",
                     pct(float(np.mean(pos <= 0.2)) if len(pos) else math.nan) + " (≤0.2 m)"])

        for column, label in [("headYaw", "頭の向き(水平)"), ("headPitch", "頭の向き(上下)"), ("shoulderYaw", "肩の向き"), ("twist", "ねじれ")]:
            va, vb = aligned(a, b, column)
            ok = ~np.isnan(va) & ~np.isnan(vb)
            diff = np.abs(wrap(va[ok] - vb[ok]))
            corr = circular_corr(va[ok], vb[ok]) if column in ("headYaw", "shoulderYaw") else linear_corr(va[ok], vb[ok])
            rows.append([person, label, f"{ok.sum()}", unit(np.median(diff) if len(diff) else math.nan, "°"),
                         unit(np.percentile(diff, 90) if len(diff) else math.nan, "°"), num(corr, ".2f"),
                         pct(float(np.mean(diff <= 30)) if len(diff) else math.nan) + " (≤30°)"])

        for column, label, suffix in [("speed", "速さ", " m/s"), ("scanWidth", "視野の広さ", "°")]:
            va, vb = aligned(a, b, column)
            ok = ~np.isnan(va) & ~np.isnan(vb)
            diff = np.abs(va[ok] - vb[ok])
            rows.append([person, label, f"{ok.sum()}", unit(np.median(diff) if len(diff) else math.nan, suffix, ".2f"),
                         unit(np.percentile(diff, 90) if len(diff) else math.nan, suffix, ".2f"),
                         num(linear_corr(va[ok], vb[ok]), ".2f"), "-"])

        head = next(r for r in rows if r[0] == person and r[1] == "頭の向き(水平)")
        s.findings.append(f"{person}: 頭の水平の向きの差 中央値 {head[3]}、30°以内 {head[6].split(' ')[0]} / 骨盤の位置の差 中央値 "
                          + next(r for r in rows if r[0] == person and r[1] == "骨盤の水平位置")[3])

    s.tables.append(("", ["人物", "値", "比べたフレーム", "差の中央値", "差の90%点", "相関", "近い割合"], rows))
    return s


def s3_target(A: Source, B: Source) -> Section:
    s = Section("S3", "注視対象の一致",
                "同じ時刻に同じ対象物体を見ていた(どちらも見ていない も含む)割合と、偶然の一致を除いた κ 係数"
                "(0.2 以下: ほぼ一致しない、0.4 前後: 中くらい、0.6 以上: かなり一致)。")
    rows = []
    for person in sorted(set(A.persons) & set(B.persons)):
        a, b = A.persons[person], B.persons[person]
        idx = np.clip(np.searchsorted(b["time"], a["time"]), 1, len(b["time"]) - 1)
        left = idx - 1
        nearest = np.where(np.abs(a["time"] - b["time"][left]) <= np.abs(b["time"][idx] - a["time"]), left, idx)
        ok = np.abs(b["time"][nearest] - a["time"]) <= TIME_TOLERANCE
        ta = a["target"][ok]
        tb = b["target"][nearest][ok]
        agree = float(np.mean(ta == tb)) if len(ta) else math.nan
        labels = sorted(set(ta) | set(tb))
        pa = np.array([np.mean(ta == x) for x in labels])
        pb = np.array([np.mean(tb == x) for x in labels])
        chance = float(np.sum(pa * pb))
        kappa = (agree - chance) / (1 - chance) if len(ta) and chance < 1 else math.nan
        looking_a = float(np.mean(ta != "")) if len(ta) else math.nan
        looking_b = float(np.mean(tb != "")) if len(tb) else math.nan
        rows.append([person, str(int(ok.sum())), pct(agree), num(kappa, ".2f"), pct(looking_a), pct(looking_b)])
    s.tables.append(("", ["人物", "比べたフレーム", "一致", "κ", f"{A.name} で何かを見ている", f"{B.name} で何かを見ている"], rows))
    return s


def s4_approaches(A: Source, B: Source, match: float) -> Section:
    s = Section("S4", "接近の一致",
                f"片方のデータで見つかった接近(人物・物・着いた時刻)が、もう片方でも ±{match:.0f} 秒以内に見つかった割合。")
    rows = []
    for person in sorted(set(A.persons) | set(B.persons)):
        ea = [e for e in A.approaches if e["person"] == person]
        eb = [e for e in B.approaches if e["person"] == person]

        def found(e: dict, others: list[dict]) -> bool:
            return any(o["target"] == e["target"] and abs(o["arriveSec"] - e["arriveSec"]) <= match for o in others)

        a_in_b = sum(found(e, eb) for e in ea)
        b_in_a = sum(found(e, ea) for e in eb)
        rows.append([person, str(len(ea)), str(len(eb)), f"{a_in_b} ({pct(ratio(a_in_b, len(ea)))})", f"{b_in_a} ({pct(ratio(b_in_a, len(eb)))})"])
        s.findings.append(f"{person}: 接近 {A.name} {len(ea)} 回 / {B.name} {len(eb)} 回、"
                          f"{A.name} の接近のうち {B.name} でも見つかった {pct(ratio(a_in_b, len(ea)))}")
    s.tables.append(("", ["人物", f"{A.name} の接近", f"{B.name} の接近", f"{A.name} → {B.name} にもある", f"{B.name} → {A.name} にもある"], rows))
    return s


def s5_results(A: Source, B: Source, child_name: str, parent_name: str) -> Section:
    s = Section("S5", "結果の比較: 考察は骨格データによらず同じになる?",
                "analyze_gaze_behavior.py の H5(予知の手がかりの AUC)と、analyze_parent_child.py の主な数字を並べたもの。"
                "どちらのデータでも同じ向き(ふだんより多い/少ない、0.5 より上/下)なら、その考察は骨格の推定方法によらない。")

    # H5 の AUC
    auc_rows: dict[str, list[str]] = {}
    for src in (A, B):
        hazards = [h for h in src.params.get("hazardTargets", src.targets) if h in src.targets]
        section = h5_prediction(src.persons, src.approaches, hazards, src.params, 3.0)
        for _, _, rows in section.tables:
            for name, value in rows:
                auc_rows.setdefault(name, []).append(value)
    s.tables.append(("H5 予知の手がかり(3 秒以内に接近するか)の AUC",
                     ["特徴量", A.name, B.name], [[k] + v + ["-"] * (2 - len(v)) for k, v in auc_rows.items()]))

    # 親子
    numbers: list[list[str]] = []
    labels = [
        ("ふだん: 親の視野に子供", lambda base, all_, away_: pct(base["parentSees"])),
        ("ふだん: 子供の視野に親", lambda base, all_, away_: pct(base["childSees"])),
        ("全接近 −5 s: 子供の視野に親", lambda base, all_, away_: pct(at(all_, "childSees", -5))),
        ("全接近 +5 s: 子供の視野に親", lambda base, all_, away_: pct(at(all_, "childSees", 5))),
        ("親が物から離れた接近 −5 s: 子供の視野に親", lambda base, all_, away_: pct(at(away_, "childSees", -5))),
        ("親が物から離れた接近 +5 s: 子供の視野に親", lambda base, all_, away_: pct(at(away_, "childSees", 5))),
        ("親が物から離れた接近 +5 s: 親の視野に子供", lambda base, all_, away_: pct(at(away_, "parentSees", 5))),
        ("全接近 0 s: 親子の距離", lambda base, all_, away_: unit(at(all_, "distance", 0), " m", ".2f")),
    ]
    per_source = []
    counts = []
    for src in (A, B):
        if child_name not in src.persons or parent_name not in src.persons:
            per_source.append(None)
            counts.append("-")
            continue
        child, parent = src.persons[child_name], src.persons[parent_name]
        j = join(child, parent, 60.0, 120.0)
        events = [e for e in src.approaches if e["person"] == child_name]
        away = [e for e in events if not parent_at_target(parent, e)]
        _, base = p1_overall(j, (child_name, parent_name), 60.0, 120.0, 1.5)
        _, curves_all = p2_event_aligned(j, events, base, (child_name, parent_name), 10.0)
        _, curves_away = p2_event_aligned(j, away, base, (child_name, parent_name), 10.0)
        per_source.append((base, curves_all, curves_away))
        counts.append(f"{len(events)} / {len(away)}")
    for label, fn in labels:
        numbers.append([label] + [fn(*v) if v else "-" for v in per_source])
    numbers.append(["接近の回数(全部 / 親が物から離れた)"] + counts)
    s.tables.append((f"親子の位置関係({child_name} と {parent_name}、視野 = 頭の向きから ±60°)", ["値", A.name, B.name], numbers))

    # 考察1(親から離れた物へ行く前に親の方を見る)が両方で出ているか
    checks = []
    for src, v in zip((A, B), per_source):
        if v is None:
            continue
        base, _, away_curves = v
        before = at(away_curves, "childSees", -5)
        if not math.isnan(before) and not math.isnan(base["childSees"]):
            checks.append((src.name, before - base["childSees"]))
    if len(checks) == 2:
        same = all(d > 0.1 for _, d in checks) or all(d < -0.1 for _, d in checks)
        s.findings.append("親から離れた物へ近づく5秒前に子供が親の方を向く: "
                          + "、".join(f"{n} でふだんより {d * 100:+.0f} ポイント" for n, d in checks)
                          + (" → どちらのデータでも同じ傾向" if same else " → データによって違う(推定方法の影響を受けている)"))
    return s


def at(curves: dict, key: str, lag: float) -> float:
    lags = np.round(np.arange(-10.0, 10.001, 0.5), 2)
    return float(curves[key][int(np.argmin(np.abs(lags - lag)))])


def linear_corr(a: np.ndarray, b: np.ndarray) -> float:
    if len(a) < 3 or np.std(a) == 0 or np.std(b) == 0:
        return math.nan
    return float(np.corrcoef(a, b)[0, 1])


def circular_corr(a_deg: np.ndarray, b_deg: np.ndarray) -> float:
    """円周の相関係数(Fisher & Lee)"""
    if len(a_deg) < 3:
        return math.nan
    a, b = np.radians(a_deg), np.radians(b_deg)
    ma = math.atan2(np.mean(np.sin(a)), np.mean(np.cos(a)))
    mb = math.atan2(np.mean(np.sin(b)), np.mean(np.cos(b)))
    sa, sb = np.sin(a - ma), np.sin(b - mb)
    denom = math.sqrt(float(np.sum(sa ** 2) * np.sum(sb ** 2)))
    return float(np.sum(sa * sb) / denom) if denom > 0 else math.nan


# ------------------------------------------------------------
def main() -> int:
    parser = argparse.ArgumentParser(description="2つの骨格データで、シーン6の特徴量と結果を比べる")
    home_data_root.add_argument(parser)
    parser.add_argument("experiment", nargs="?")
    parser.add_argument("subject", nargs="?")
    parser.add_argument("--a", help="1つ目の骨格データ (例: filtered_HeadJoints)")
    parser.add_argument("--b", help="2つ目の骨格データ (例: image_GazeElseHead)")
    parser.add_argument("--child", default="child")
    parser.add_argument("--parent", default="mother")
    parser.add_argument("--match", type=float, default=2.0, help="S4: 同じ接近とみなす時刻の差 (秒)")
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
    if len(sources) < 2:
        print(f"シーン6の結果が2つ以上ありません(あるもの: {', '.join(sources) or 'なし'})。"
              f"シーン6で Skeleton Source を切り替えて、それぞれ Analyze All を押してください: {folder}")
        return 1
    name_a = args.a or _choose("1つ目の骨格データ", sources)
    name_b = args.b or _choose("2つ目の骨格データ", [x for x in sources if x != name_a])
    for name in (name_a, name_b):
        if name not in sources:
            print(f"シーン6の結果がない骨格データです: {name} (あるもの: {', '.join(sources)})")
            return 1

    print(f"読み込み中: {name_a} / {name_b}")
    A, B = Source(folder, name_a), Source(folder, name_b)

    sections = [s1_amount(A, B), s2_differences(A, B), s3_target(A, B), s4_approaches(A, B, args.match),
                s5_results(A, B, args.child, args.parent)]
    meta = [
        f"{experiment} / {subject}:  A = {name_a}(シーン6 {A.params.get('createdAt', '?')})、B = {name_b}(シーン6 {B.params.get('createdAt', '?')})",
        "頭の向きは、キネクトでは頭→鼻の向きを下へ補正したもの、画像では推定した視線(なければ頭)の向き。同じものを測っているわけではない点に注意。",
    ]
    print_sections(sections)
    out_path = folder / f"compare_{name_a}_vs_{name_b}.html"
    out_path.write_text(render_html(f"骨格データの比較 {subject}", meta, sections), encoding="utf-8")
    print(f"\n保存しました: {out_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
