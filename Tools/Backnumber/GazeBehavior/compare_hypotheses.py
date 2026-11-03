"""キネクトの骨格と、画像から推定した骨格で、同じ定義のまま4つの仮説を計算して並べる。

シーン3の人物ごとの骨格(Filtered/grouped_HeadJoints.json と grouped_Image.json)と、
シーン0の対象物体(Env/TargetObjects.json, Env/Meshes/)を直接読み、どちらのデータも同じ計算で
(シーン6 HomeGazeBehaviorAnalyzer と同じ定義で)位置・速さ・接近・頭の向きを求める。
シーン6は1つの骨格データで場面を見ながら確かめる用、こちらは複数の骨格データをまとめて比べる用。

視野: 子供の視野は 左右 約90° / 上下 約70° と言われるので、その半分(半頂角)を使う
  左右だけ   … 頭の水平の向きとのなす角が ±--view-h 度(既定 45)以内
  上下も含む … (左右のなす角 / view-h)^2 + (上下のなす角 / view-v)^2 ≤ 1 (楕円の視野。view-v 既定 35)

仮説(番号はシーン6と同じ):
  H1 子供の頭の向きは、進行方向より先に変わる
       値 = 頭の水平の向き(t) と 進行方向(t + lag) のなす角の平均が最も小さくなる lag(移動中のフレーム全部)
  H2 子供は、親がそばにいる物に近づく
       値 = 接近のうち、着いた時刻に親がその物の --near m 以内にいた割合 − ふだん親がその物の --near m 以内にいる割合
  H3 子供は、物に着く前の5秒間にその物の方を向いている
       値 = 着く 5〜0 秒前にその物(頂点を囲む箱の中心)が子供の視野に入っていた割合 − ふだんの割合
       参考: 頭の向きの代わりに進行方向を使った同じ値(これと同じくらいなら、歩いている方向を見ているだけかもしれない)
  H4 子供は、親から離れた物へ向かう前に親の方を向く(社会的参照)
       親から離れた物(着いた時刻に親がその物から --near m より外)への接近について、
       値 = 着く 6〜2 秒前に親(の頭)が子供の視野に入っていた割合 − ふだんの割合

接近(シーン6と同じ): 子供の骨盤から物(頂点を囲む箱)までの水平距離が 0.6 m 以下になった時刻。
  その前に 1.0 m より離れているのを見ていたときだけ数え、映っていない時間が 1 秒を超えたら離れるのを見るまで数えない。
偶然かどうか: 95% 区間(接近を選び直す)と p(接近の時刻をでたらめに 20〜200 秒ずらしても同じくらいの差が出る確率)。

使い方:
  python Tools/GazeBehavior/compare_hypotheses.py <実験の名前> <実験対象者>
  --view-h 45 --view-v 35 --near 1.0 --child child --parent mother
書き出すもの(毎回上書き):
  Skeleton/<実験対象者>/Analysis/GazeBehavior/hypotheses_kinect_vs_image.html
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
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "GazeEvents"))
import home_data_root  # noqa: E402  Tools/home_data_root.py
from analyze_gaze_behavior import Section, _choose, _subdirs, print_sections, render_html, value_at, wrap  # noqa: E402
from check_robustness import bootstrap, shift_p, window_mean  # noqa: E402
from make_event_table import load_targets  # noqa: E402  Tools/GazeEvents/make_event_table.py

DOWNWARD_DEG = 24.4      # キネクトの頭部方向の下向きの補正(シーン4〜6と同じ)
APPROACH, LEAVE, GAP = 0.6, 1.0, 1.0
VELOCITY_SPAN, MIN_MOVE, MAX_SPEED = 0.5, 0.2, 3.0


# ------------------------------------------------------------
# 骨格を読む
# ------------------------------------------------------------
def load_tracks(path: Path, mode: str) -> dict[str, dict[str, np.ndarray]]:
    """人物(グループ名)ごとに 時刻・骨盤・頭の位置・頭の向き(3次元の単位ベクトル)。
    mode: kinect(頭→鼻を下へ補正)/ image_gaze(画像の視線、なければ頭)/ image_head(画像の頭の向き)"""
    print(f"  読み込み中: {path.name} ({mode})")
    data = json.loads(path.read_text(encoding="utf-8"))
    rows: dict[str, list] = {}
    for frame in data["frames"]:
        t = frame["timeSec"]
        for p in frame["persons"]:
            joints = {j["jointId"]: j["position"] for j in p.get("joints") or []}
            pelvis = joints.get("Pelvis")
            head = joints.get("Head")
            direction = None
            if mode == "kinect":
                nose = joints.get("Nose")
                if head and nose:
                    d = np.array([nose["x"] - head["x"], nose["y"] - head["y"], nose["z"] - head["z"]])
                    if np.linalg.norm(d) > 1e-3:
                        direction = rotate_down(d / np.linalg.norm(d), DOWNWARD_DEG)
            else:
                head = p.get("headPosition", head)
                use_gaze = mode == "image_gaze" and p.get("hasGazeDirection")
                v = p.get("gazeDirection") if use_gaze else (p.get("headDirection") if p.get("hasHeadDirection") else None)
                if v:
                    d = np.array([v["x"], v["y"], v["z"]])
                    if np.linalg.norm(d) > 1e-6:
                        direction = d / np.linalg.norm(d)
            nan3 = (math.nan,) * 3
            rows.setdefault(p["label"], []).append((
                t,
                *((pelvis["x"], pelvis["z"]) if pelvis else (math.nan, math.nan)),
                *((head["x"], head["y"], head["z"]) if head else nan3),
                *(tuple(direction) if direction is not None else nan3)))
    tracks = {}
    for label, values in rows.items():
        a = np.array(sorted(values, key=lambda r: r[0]), dtype=float)
        a = a[np.concatenate([[True], np.diff(a[:, 0]) > 0])]
        tr = dict(zip(("time", "pelvisX", "pelvisZ", "headX", "headY", "headZ", "dirX", "dirY", "dirZ"), a.T))
        horizontal = np.hypot(tr["dirX"], tr["dirZ"])
        # 真下・真上を向いていて水平の向きが決まらないときは、向きを求めない(シーン6と同じ)
        tr["yaw"] = np.where(horizontal > 0.1, np.degrees(np.arctan2(tr["dirX"], tr["dirZ"])), np.nan)
        tr["pitch"] = np.degrees(np.arcsin(np.clip(tr["dirY"], -1, 1)))
        add_motion(tr)
        tracks[label] = tr
    return tracks


def rotate_down(d: np.ndarray, deg: float) -> np.ndarray:
    """水平の向きを変えずに、下へ deg 度回す(シーン4〜6の補正と同じ)"""
    pitch = math.asin(max(-1.0, min(1.0, d[1]))) - math.radians(deg)
    yaw = math.atan2(d[0], d[2])
    return np.array([math.cos(pitch) * math.sin(yaw), math.sin(pitch), math.cos(pitch) * math.cos(yaw)])


def add_motion(tr: dict) -> None:
    """骨盤の速さ・進行方向(前後 VELOCITY_SPAN/2 秒の変位。シーン6と同じ)"""
    t = tr["time"]
    half = VELOCITY_SPAN / 2
    lo = np.searchsorted(t, t - half)
    hi = np.searchsorted(t, t + half, side="right") - 1
    dt = t[hi] - t[lo]
    with np.errstate(invalid="ignore", divide="ignore"):
        vx = (tr["pelvisX"][hi] - tr["pelvisX"][lo]) / dt
        vz = (tr["pelvisZ"][hi] - tr["pelvisZ"][lo]) / dt
    speed = np.hypot(vx, vz)
    valid = (dt >= half) & (speed <= MAX_SPEED)
    tr["speed"] = np.where(valid, speed, np.nan)
    tr["moveYaw"] = np.where(valid & (speed >= MIN_MOVE), np.degrees(np.arctan2(vx, vz)), np.nan)


# ------------------------------------------------------------
# 物との関係・接近
# ------------------------------------------------------------
def box_distance(tr: dict, target: dict) -> np.ndarray:
    lo, hi = target["vertices"].min(axis=0), target["vertices"].max(axis=0)
    dx = np.maximum.reduce([lo[0] - tr["pelvisX"], np.zeros_like(tr["pelvisX"]), tr["pelvisX"] - hi[0]])
    dz = np.maximum.reduce([lo[2] - tr["pelvisZ"], np.zeros_like(tr["pelvisZ"]), tr["pelvisZ"] - hi[2]])
    return np.hypot(dx, dz)


def detect_approaches(tr: dict, distances: dict[str, np.ndarray]) -> list[dict]:
    events = []
    t = tr["time"]
    for name, dist in distances.items():
        state, last = "unknown", -math.inf
        for i in range(len(t)):
            if t[i] - last > GAP:
                state = "unknown"
            last = t[i]
            d = dist[i]
            if math.isnan(d):
                continue
            if d > LEAVE:
                state = "outside"
            elif d <= APPROACH and state == "outside":
                events.append({"target": name, "arriveSec": float(t[i])})
                state = "inside"
    return sorted(events, key=lambda e: e["arriveSec"])


def in_view(dyaw: np.ndarray, dpitch: np.ndarray | None, view_h: float, view_v: float) -> np.ndarray:
    """視野に入っているか(1/0、求まらなければ NaN)。dpitch が None なら左右だけ"""
    if dpitch is None:
        inside = np.abs(dyaw) <= view_h
        bad = np.isnan(dyaw)
    else:
        inside = (dyaw / view_h) ** 2 + (dpitch / view_v) ** 2 <= 1.0
        bad = np.isnan(dyaw) | np.isnan(dpitch)
    return np.where(bad, np.nan, inside.astype(float))


def angles_to(tr: dict, px: np.ndarray, py: np.ndarray, pz: np.ndarray, yaw: np.ndarray | None = None):
    """子供の頭から点 (px, py, pz) への方向と、頭の向きのなす角(左右, 上下)"""
    dx, dy, dz = px - tr["headX"], py - tr["headY"], pz - tr["headZ"]
    to_yaw = np.degrees(np.arctan2(dx, dz))
    to_pitch = np.degrees(np.arctan2(dy, np.hypot(dx, dz)))
    use_yaw = tr["yaw"] if yaw is None else yaw
    return wrap(to_yaw - use_yaw), to_pitch - tr["pitch"]


# ------------------------------------------------------------
# 仮説
# ------------------------------------------------------------
class Data:
    def __init__(self, label: str, tracks: dict, targets: dict, child: str, parent: str, near: float):
        self.label = label
        self.child, self.parent = tracks[child], tracks[parent]
        self.targets = targets
        self.near = near
        self.child_dist = {n: box_distance(self.child, tg) for n, tg in targets.items()}
        self.parent_dist = {n: box_distance(self.parent, tg) for n, tg in targets.items()}
        self.events = detect_approaches(self.child, self.child_dist)
        self.times = np.array([e["arriveSec"] for e in self.events])
        t = self.child["time"]
        self.t0 = max(t[0], self.parent["time"][0]) if len(t) else 0.0
        self.t1 = min(t[-1], self.parent["time"][-1]) if len(t) else 0.0
        # 親の頭(なければ骨盤)を子供の時刻に並べる
        p = self.parent
        self.px = value_at(p["time"], np.where(np.isnan(p["headX"]), p["pelvisX"], p["headX"]), t, 0.1)
        self.py = value_at(p["time"], np.where(np.isnan(p["headY"]), 1.4, p["headY"]), t, 0.1)
        self.pz = value_at(p["time"], np.where(np.isnan(p["headZ"]), p["pelvisZ"], p["headZ"]), t, 0.1)

    def parent_near(self, name: str, time: float) -> bool:
        d = value_at(self.parent["time"], self.parent_dist[name], np.array([time]), 0.2)[0]
        return not math.isnan(d) and d <= self.near

    # H3
    def h3(self, idx: np.ndarray | None = None, times: np.ndarray | None = None) -> float:
        idx = np.arange(len(self.events)) if idx is None else idx
        times = self.times[idx] if times is None else times
        hit, base = [], []
        for i, t in zip(idx, times):
            name = self.events[i]["target"]
            d = value_at(self.parent["time"], self.parent_dist[name], np.array([t]), 0.2)[0]
            if math.isnan(d):
                continue
            hit.append(d <= self.near)
            col = self.parent_dist[name]
            base.append(float(np.mean(col[~np.isnan(col)] <= self.near)))
        return float(np.mean(hit) - np.mean(base)) if hit else math.nan

    # H1
    def away_times(self) -> np.ndarray:
        return np.array([e["arriveSec"] for e in self.events if not self.parent_near(e["target"], e["arriveSec"])])

    def sees_parent(self, view_h: float, view_v: float, vertical: bool) -> np.ndarray:
        dyaw, dpitch = angles_to(self.child, self.px, self.py, self.pz)
        return in_view(dyaw, dpitch if vertical else None, view_h, view_v)

    def h1(self, times: np.ndarray, sees: np.ndarray) -> tuple[float, float, float]:
        before = window_mean(self.child["time"], sees, times, -6.0, -2.0)
        if not np.any(~np.isnan(before)):
            return math.nan, math.nan, math.nan
        b, base = float(np.nanmean(before)), float(np.nanmean(sees))
        return b - base, b, base

    # H4
    def h4(self) -> tuple[float, float, float]:
        t, head = self.child["time"], self.child["yaw"]
        best, best_lag, at0 = math.inf, math.nan, math.nan
        for lag in np.round(np.arange(-2.0, 2.0001, 0.1), 2):
            m = value_at(t, self.child["moveYaw"], t + lag, 0.1)
            ok = ~np.isnan(head) & ~np.isnan(m)
            if ok.sum() < 100:
                continue
            v = float(np.mean(np.abs(wrap(head[ok] - m[ok]))))
            if abs(lag) < 1e-6:
                at0 = v
            if v < best:
                best, best_lag = v, float(lag)
        return best_lag, best, at0

    # H5
    def facing_target(self, name: str, view_h: float, view_v: float, vertical: bool, use_move: bool = False) -> np.ndarray:
        c = self.targets[name]["center"]
        n = len(self.child["time"])
        dyaw, dpitch = angles_to(self.child, np.full(n, c[0]), np.full(n, c[1]), np.full(n, c[2]),
                                 self.child["moveYaw"] if use_move else None)
        return in_view(dyaw, None if (use_move or not vertical) else dpitch, view_h, view_v)

    def h5(self, facing: dict[str, np.ndarray], idx: np.ndarray | None = None, times: np.ndarray | None = None) -> tuple[float, float, float]:
        idx = np.arange(len(self.events)) if idx is None else idx
        times = self.times[idx] if times is None else times
        diffs, befores, bases = [], [], []
        for i, t in zip(idx, times):
            f = facing[self.events[i]["target"]]
            b = window_mean(self.child["time"], f, np.array([t]), -5.0, 0.0)[0]
            if not math.isnan(b):
                base = float(np.nanmean(f))
                diffs.append(b - base)
                befores.append(b)
                bases.append(base)
        if not diffs:
            return math.nan, math.nan, math.nan
        return float(np.mean(diffs)), float(np.mean(befores)), float(np.mean(bases))


def pt(v: float) -> str:
    return "-" if v is None or math.isnan(v) else f"{v * 100:+.0f} pt"


def pc(v: float) -> str:
    return "-" if v is None or math.isnan(v) else f"{v * 100:.0f}%"


def ci(lo_hi: tuple[float, float]) -> str:
    lo, hi = lo_hi
    return "-" if math.isnan(lo) else f"{lo * 100:+.0f} 〜 {hi * 100:+.0f} pt"


def pv(p: float) -> str:
    return "-" if p is None or math.isnan(p) else f"{p:.3f}"


# ------------------------------------------------------------
def main() -> int:
    parser = argparse.ArgumentParser(description="キネクトと画像推定の骨格で、同じ定義のまま4つの仮説を計算する")
    home_data_root.add_argument(parser)
    parser.add_argument("experiment", nargs="?")
    parser.add_argument("subject", nargs="?")
    parser.add_argument("--child", default="child")
    parser.add_argument("--parent", default="mother")
    parser.add_argument("--view-h", type=float, default=45.0, help="視野の左右の半頂角 (度)")
    parser.add_argument("--view-v", type=float, default=35.0, help="視野の上下の半頂角 (度)")
    parser.add_argument("--near", type=float, default=1.0, help="親がそばとする、親から物までの距離 (m)")
    parser.add_argument("--shifts", type=int, default=500)
    args = parser.parse_args()

    data_root = home_data_root.choose_data_root(args.data_root)
    if data_root is None:
        return 1
    experiment = args.experiment or _choose("実験の名前", _subdirs(data_root))
    subject = args.subject or (_choose("実験対象者", _subdirs(data_root / experiment / "Skeleton")) if experiment else None)
    if not experiment or not subject:
        return 1

    subject_dir = data_root / experiment / "Skeleton" / subject
    targets = load_targets(data_root / experiment / "Env")
    print(f"対象物体: {', '.join(targets)}")

    sources = []
    for label, file, mode in [("キネクト", "grouped_HeadJoints.json", "kinect"),
                              ("画像(視線)", "grouped_Image.json", "image_gaze"),
                              ("画像(頭の向き)", "grouped_Image.json", "image_head")]:
        path = subject_dir / "Filtered" / file
        if not path.exists():
            print(f"  ありません: {path}")
            continue
        tracks = load_tracks(path, mode)
        if args.child in tracks and args.parent in tracks:
            sources.append(Data(label, tracks, targets, args.child, args.parent, args.near))
    if not sources:
        return 1

    vh, vv = args.view_h, args.view_v
    names = [d.label for d in sources]

    # ---------- H3 ----------
    s3 = Section("H2", "子供は、親がそばにいる物に近づく",
                 f"接近のうち、着いた時刻に親がその物の {args.near:.1f} m 以内にいた割合と、ふだん親がその物の {args.near:.1f} m 以内にいる割合。"
                 "位置だけを使う。")
    rows = []
    for d in sources:
        hit = [d.parent_near(e["target"], e["arriveSec"]) for e in d.events]
        v = d.h3()
        rows.append([d.label, str(len(d.events)), pc(float(np.mean(hit)) if hit else math.nan), pc(float(np.mean(hit)) - v if hit else math.nan),
                     pt(v), ci(bootstrap(lambda idx, d=d: d.h3(idx), len(d.events))),
                     pv(shift_p(lambda tt, d=d: d.h3(times=tt), d.times, v, d.t0, d.t1, args.shifts, +1))])
    s3.tables.append(("", ["データ", "接近", "親がそば(接近時)", "ふだん", "差", "95% 区間", "p"], rows))

    # ---------- H1 ----------
    s1 = Section("H4", "子供は、親から離れた物へ向かう前に親の方を向く(社会的参照)",
                 f"親から離れた物(着いた時刻に親がその物から {args.near:.1f} m より外)への接近について、着く 6〜2 秒前に親の頭が子供の視野に入っていた割合と、ふだんの割合。"
                 f"視野 = 左右 ±{vh:.0f}°(上下も含むときは 上下 ±{vv:.0f}° の楕円)。偶然でも入る割合は 左右だけで {vh / 180 * 100:.0f}%。")
    rows = []
    for d in sources:
        away = d.away_times()
        for vertical in (False, True):
            sees = d.sees_parent(vh, vv, vertical)
            v, before, base = d.h1(away, sees)
            rows.append([d.label, "上下も含む" if vertical else "左右だけ", str(len(away)), pc(before), pc(base), pt(v),
                         ci(bootstrap(lambda idx, d=d, a=away, s=sees: d.h1(a[idx], s)[0], len(away))),
                         pv(shift_p(lambda tt, d=d, s=sees: d.h1(tt, s)[0], away, v, d.t0, d.t1, args.shifts, +1))])
    s1.tables.append(("", ["データ", "視野", "接近", "6〜2 秒前", "ふだん", "差", "95% 区間", "p"], rows))

    # ---------- H4 ----------
    s4 = Section("H1", "子供の頭の向きは、進行方向より先に変わる",
                 "頭の水平の向き(t) と 進行方向(t + lag) のなす角の平均が最も小さくなる lag(正なら頭が先)。進行方向はそれぞれのデータの骨盤の動きから。")
    rows = []
    for d in sources:
        lag, best, at0 = d.h4()
        rows.append([d.label, "-" if math.isnan(lag) else f"{lag:+.1f} s", f"{best:.1f}°", f"{at0:.1f}°",
                     f"{int(np.sum(~np.isnan(d.child['moveYaw'])))}"])
    s4.tables.append(("", ["データ", "最も近い lag", "そのときのなす角", "lag 0 のなす角", "移動中のフレーム"], rows))

    # ---------- H5 ----------
    s5 = Section("H3", "子供は、物に着く前の5秒間にその物の方を向いている",
                 f"着く 5〜0 秒前に、その物(頂点を囲む箱の中心)が子供の視野に入っていた割合と、ふだんの割合(その物について)。視野 = 左右 ±{vh:.0f}°"
                 f"(上下も含むときは 上下 ±{vv:.0f}° の楕円)。参考の「進行方向」は、頭の向きの代わりに歩いている方向を使ったもの。")
    rows = []
    for d in sources:
        for label, vertical, use_move in [("左右だけ", False, False), ("上下も含む", True, False), ("参考: 進行方向", False, True)]:
            facing = {n: d.facing_target(n, vh, vv, vertical, use_move) for n in targets}
            v, before, base = d.h5(facing)
            rows.append([d.label, label, str(len(d.events)), pc(before), pc(base), pt(v),
                         ci(bootstrap(lambda idx, d=d, f=facing: d.h5(f, idx)[0], len(d.events))),
                         pv(shift_p(lambda tt, d=d, f=facing: d.h5(f, times=tt)[0], d.times, v, d.t0, d.t1, args.shifts, +1))])
    s5.tables.append(("", ["データ", "向き", "接近", "5〜0 秒前", "ふだん", "差", "95% 区間", "p"], rows))

    sections = [s4, s3, s5, s1]
    meta = [
        f"{experiment} / {subject}  子供 {args.child} / 親 {args.parent}  データ: {' / '.join(names)}",
        f"視野 = 子供の視野(左右 約{vh * 2:.0f}° / 上下 約{vv * 2:.0f}°)の半頂角。接近 = 骨盤から物の箱まで {APPROACH} m 以下(その前に {LEAVE} m より外を見ていたとき)",
        "キネクトの頭の向き = 頭→鼻を下へ 24.4° 補正。画像(視線) = 推定した視線、なければ頭の向き。画像(頭の向き) = 推定した頭の向き。",
        "p = 接近の時刻をでたらめにずらしても同じくらいの差が出る確率(0.05 未満なら偶然とは考えにくい)",
    ]
    print_sections(sections)
    out_path = subject_dir / "Analysis" / "GazeBehavior" / "hypotheses_kinect_vs_image.html"
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(render_html(f"仮説の比較(キネクト / 画像) {subject}", meta, sections), encoding="utf-8")
    print(f"\n保存しました: {out_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
