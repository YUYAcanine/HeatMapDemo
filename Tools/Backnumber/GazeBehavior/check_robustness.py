"""親子の位置関係・頭の向きから立てた仮説が、データのブレ(頭の向きの誤差・推定方法・パラメータ・偶然)に対して
どのくらい崩れにくいかを確かめる。

仮説(子供 = --child、親 = --parent。接近 = シーン6で見つけた、子供が物に approachDistance 以内に入った時刻):
  H1 社会的参照   … 子供は、親から離れた物へ向かう前(着く 6〜2 秒前)に親の方を向いている(ふだんより多く)
  H2 親の見落とし … 親から離れた物に子供が着いた後(1〜5 秒後)、親の視野に子供が入っている割合はふだんより少ない
  H3 親のもとへ   … 子供が近づく物の多くは、そのとき親がそばにいる物(ふだん親がその物のそばにいる割合より多い)
  H4 視線の先行   … 子供の頭の向きは、進行方向より先に変わる(頭の向きが最も近い進行方向の時刻が後ろにずれる)
  H5 物を見てから … 子供は、物に着く前(5〜0 秒前)にその物の方を向いている(ふだんより多く)

確かめ方(仮説ごとに、どれでも同じ向きの結果になれば「崩れにくい」。誤差は大きさごとに 1 票(80% 以上で同じ向きなら○)):
  偶然      … 接近を選び直したときのばらつき(ブートストラップ 95% 区間)と、
               接近の時刻をでたらめにずらしたとき(時間シフト)に同じくらいの差が出る確率 p
  頭の向きの誤差 … 頭の向きにゆっくり変わる誤差(±10/20/30° 程度)を足して 20 回計算し直す
  推定方法  … 頭の向き・位置を、キネクト(シーン6)/ キネクトの平滑化(smooth05)/ 画像の視線 / 画像の頭の向き に替える
               (シーン3の grouped_*.json から直接読む。接近の時刻はキネクトのものを使う)
  パラメータ … 視野とする角度(±45/60/75/90°)、親がそばとする距離(0.5/1.0/1.5 m)
  時間      … 記録の前半と後半に分ける

使い方:
  python Tools/GazeBehavior/check_robustness.py <実験の名前> <実験対象者>
  --source filtered_HeadJoints … 基準にするシーン6の結果(接近の時刻もここから)
  --child child --parent mother
  --shifts 500 --noise-repeats 20 … 時間シフト・誤差を足す回数
書き出すもの(毎回上書き):
  Skeleton/<実験対象者>/Analysis/GazeBehavior/robustness_<骨格>.html
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
    FRAMES_SUFFIX, PREFIX, Section, _choose, _subdirs, load_approaches, load_frames, print_sections, render_html,
    value_at, wrap)

RNG = np.random.default_rng(0)


# ------------------------------------------------------------
# データ: 人物ごとの 時刻・骨盤・頭の位置・頭の水平の向き
# ------------------------------------------------------------
def track_from_frames(c: dict) -> dict:
    return {k: c[k] for k in ("time", "pelvisX", "pelvisZ", "headX", "headZ", "headYaw")}


def tracks_from_grouped(path: Path, mode: str) -> dict[str, dict] | None:
    """grouped_*.json から人物(グループ名)ごとの時系列。mode: kinect(頭→鼻)/ image_gaze(視線、なければ頭)/ image_head"""
    if not path.exists():
        return None
    print(f"  読み込み中: {path.name} ({mode})")
    data = json.loads(path.read_text(encoding="utf-8"))
    rows: dict[str, list[tuple]] = {}
    for frame in data["frames"]:
        t = frame["timeSec"]
        for p in frame["persons"]:
            joints = {j["jointId"]: j["position"] for j in p.get("joints") or []}
            pelvis = joints.get("Pelvis")
            yaw = math.nan
            head = joints.get("Head")
            if mode == "kinect":
                nose = joints.get("Nose")
                if head and nose:
                    dx, dz = nose["x"] - head["x"], nose["z"] - head["z"]
                    if dx * dx + dz * dz > 1e-6:
                        yaw = math.degrees(math.atan2(dx, dz))
            else:
                if "headPosition" in p:
                    head = p["headPosition"]
                use_gaze = mode == "image_gaze" and p.get("hasGazeDirection")
                d = p.get("gazeDirection") if use_gaze else (p.get("headDirection") if p.get("hasHeadDirection") else None)
                # 水平の成分が小さい(真下・真上を向いている)ときは向きを決めない(シーン6と同じ)
                if d and d["x"] ** 2 + d["z"] ** 2 > 0.01 * (d["x"] ** 2 + d["y"] ** 2 + d["z"] ** 2):
                    yaw = math.degrees(math.atan2(d["x"], d["z"]))
            rows.setdefault(p["label"], []).append((
                t,
                pelvis["x"] if pelvis else math.nan, pelvis["z"] if pelvis else math.nan,
                head["x"] if head else math.nan, head["z"] if head else math.nan, yaw))
    tracks = {}
    for label, values in rows.items():
        a = np.array(sorted(values), dtype=float)
        # 同じ時刻に2つあれば最初のもの
        keep = np.concatenate([[True], np.diff(a[:, 0]) > 0])
        a = a[keep]
        tracks[label] = dict(zip(("time", "pelvisX", "pelvisZ", "headX", "headZ", "headYaw"), a.T))
    return tracks


# ------------------------------------------------------------
# 指標
# ------------------------------------------------------------
class Pair:
    """子供の時刻に親を並べ、互いに視野に入っているか(角度)を持つ"""

    def __init__(self, child: dict, parent: dict):
        self.child, self.parent = child, parent
        t = child["time"]
        self.t = t
        px = value_at(parent["time"], np.where(np.isnan(parent["headX"]), parent["pelvisX"], parent["headX"]), t, 0.1)
        pz = value_at(parent["time"], np.where(np.isnan(parent["headZ"]), parent["pelvisZ"], parent["headZ"]), t, 0.1)
        cx = np.where(np.isnan(child["headX"]), child["pelvisX"], child["headX"])
        cz = np.where(np.isnan(child["headZ"]), child["pelvisZ"], child["headZ"])
        self.to_parent = np.degrees(np.arctan2(px - cx, pz - cz))   # 子供から親への方向
        self.to_child = np.degrees(np.arctan2(cx - px, cz - pz))    # 親から子供への方向
        self.parent_yaw = value_at(parent["time"], parent["headYaw"], t, 0.1)
        self.child_yaw = child["headYaw"]

    def angles(self, child_noise: np.ndarray | None = None, parent_noise: np.ndarray | None = None):
        cy = self.child_yaw + (child_noise if child_noise is not None else 0.0)
        py = self.parent_yaw + (parent_noise if parent_noise is not None else 0.0)
        return np.abs(wrap(self.to_parent - cy)), np.abs(wrap(self.to_child - py))


def window_mean(t: np.ndarray, values: np.ndarray, centers: np.ndarray, start: float, end: float) -> np.ndarray:
    """centers ごとに、[center + start, center + end] の values(NaN を除く)の平均"""
    ok = ~np.isnan(values)
    cum_v = np.concatenate([[0.0], np.cumsum(np.where(ok, values, 0.0))])
    cum_n = np.concatenate([[0], np.cumsum(ok)])
    lo = np.searchsorted(t, centers + start)
    hi = np.searchsorted(t, centers + end, side="right")
    n = cum_n[hi] - cum_n[lo]
    with np.errstate(invalid="ignore", divide="ignore"):
        return np.where(n > 3, (cum_v[hi] - cum_v[lo]) / np.maximum(n, 1), np.nan)


def h1_value(pair: Pair, events: np.ndarray, view: float, child_noise=None, parent_noise=None) -> float:
    child_angle, _ = pair.angles(child_noise, parent_noise)
    sees = np.where(np.isnan(child_angle), np.nan, (child_angle <= view).astype(float))
    before = window_mean(pair.t, sees, events, -6.0, -2.0)
    return float(np.nanmean(before) - np.nanmean(sees)) if np.any(~np.isnan(before)) else math.nan


def h2_value(pair: Pair, events: np.ndarray, view: float, child_noise=None, parent_noise=None) -> float:
    _, parent_angle = pair.angles(child_noise, parent_noise)
    sees = np.where(np.isnan(parent_angle), np.nan, (parent_angle <= view).astype(float))
    after = window_mean(pair.t, sees, events, 1.0, 5.0)
    return float(np.nanmean(after) - np.nanmean(sees)) if np.any(~np.isnan(after)) else math.nan


def h3_value(parent_frames: dict, events: list[dict], times: np.ndarray, near: float) -> float:
    """接近のうち親がその物の near m 以内にいた割合 − ふだん親がその物の near m 以内にいる割合(接近した物で平均)"""
    hit, base = [], []
    for e, t in zip(events, times):
        column = parent_frames.get(f"dist_{e['target']}")
        if column is None:
            continue
        d = value_at(parent_frames["time"], column, np.array([t]), 0.2)[0]
        if not math.isnan(d):
            hit.append(d <= near)
            ok = ~np.isnan(column)
            base.append(float(np.mean(column[ok] <= near)))
    return float(np.mean(hit) - np.mean(base)) if hit else math.nan


def h4_value(child: dict, move_t: np.ndarray, move_yaw: np.ndarray, noise=None) -> float:
    """頭の向き(t) と 進行方向(t + lag) のなす角が最も小さくなる lag(秒)"""
    head = child["headYaw"] + (noise if noise is not None else 0.0)
    t = child["time"]
    best, best_lag = math.inf, math.nan
    for lag in np.round(np.arange(-2.0, 2.0001, 0.1), 2):
        m = value_at(move_t, move_yaw, t + lag, 0.1)
        ok = ~np.isnan(head) & ~np.isnan(m)
        if ok.sum() < 100:
            continue
        v = float(np.mean(np.abs(wrap(head[ok] - m[ok]))))
        if v < best:
            best, best_lag = v, float(lag)
    return best_lag


def h5_value(child_frames: dict, events: list[dict], times: np.ndarray, angle: float, noise=None) -> float:
    """着く 5〜0 秒前に、その物の方(なす角 ≤ angle)を向いていた割合 − ふだん(全体)その物の方を向いている割合"""
    t = child_frames["time"]
    diffs = []
    for e, center in zip(events, times):
        column = child_frames.get(f"bearing_{e['target']}")
        if column is None:
            continue
        b = column if noise is None else np.abs(wrap(column + noise))
        facing = np.where(np.isnan(b), np.nan, (b <= angle).astype(float))
        before = window_mean(t, facing, np.array([center]), -5.0, 0.0)[0]
        if not math.isnan(before):
            diffs.append(before - np.nanmean(facing))
    return float(np.mean(diffs)) if diffs else math.nan


# ------------------------------------------------------------
# 確かめ方
# ------------------------------------------------------------
def slow_noise(n: int, sigma: float, frames: int = 60) -> np.ndarray:
    """ゆっくり変わる誤差(約 frames フレームで変わる)。標準偏差 sigma 度"""
    if n == 0 or sigma == 0:
        return np.zeros(n)
    white = RNG.normal(0.0, 1.0, n + frames)
    smooth = np.convolve(white, np.ones(frames) / frames, mode="valid")[:n]
    return smooth / (np.std(smooth) + 1e-9) * sigma


def bootstrap(values_fn, n_events: int, repeats: int = 1000) -> tuple[float, float]:
    """接近を選び直したときの 95% 区間。values_fn(indices) → 値"""
    if n_events == 0:
        return math.nan, math.nan
    results = [values_fn(RNG.integers(0, n_events, n_events)) for _ in range(repeats)]
    results = [r for r in results if not math.isnan(r)]
    return (float(np.percentile(results, 2.5)), float(np.percentile(results, 97.5))) if results else (math.nan, math.nan)


def shift_p(values_fn, times: np.ndarray, observed: float, t0: float, t1: float, repeats: int, direction: int) -> float:
    """接近の時刻をでたらめに 20〜200 秒ずらして(記録の中で回して)計算したとき、observed 以上(direction=-1 なら以下)が出る割合"""
    if math.isnan(observed) or len(times) == 0:
        return math.nan
    span = t1 - t0
    count, total = 0, 0
    for _ in range(repeats):
        shift = RNG.uniform(20.0, 200.0) * RNG.choice([-1, 1])
        shifted = t0 + (times - t0 + shift) % span
        v = values_fn(shifted)
        if math.isnan(v):
            continue
        total += 1
        count += (v >= observed) if direction > 0 else (v <= observed)
    return (count + 1) / (total + 1) if total else math.nan


def fmt_points(v: float) -> str:
    return "-" if math.isnan(v) else f"{v * 100:+.0f} pt"


def fmt_lag(v: float) -> str:
    return "-" if math.isnan(v) else f"{v:+.1f} s"


def verdict(signs: list[bool]) -> str:
    if not signs:
        return "-"
    share = sum(signs) / len(signs)
    return f"{sum(signs)}/{len(signs)} で同じ向き → " + ("崩れにくい" if share >= 0.9 else "やや崩れる" if share >= 0.7 else "崩れやすい")


# ------------------------------------------------------------
def main() -> int:
    parser = argparse.ArgumentParser(description="親子の仮説が、データのブレに対して崩れにくいかを確かめる")
    home_data_root.add_argument(parser)
    parser.add_argument("experiment", nargs="?")
    parser.add_argument("subject", nargs="?")
    parser.add_argument("--source", default="filtered_HeadJoints", help="基準にするシーン6の結果")
    parser.add_argument("--child", default="child")
    parser.add_argument("--parent", default="mother")
    parser.add_argument("--shifts", type=int, default=500)
    parser.add_argument("--noise-repeats", type=int, default=20)
    args = parser.parse_args()

    data_root = home_data_root.choose_data_root(args.data_root)
    if data_root is None:
        return 1
    experiment = args.experiment or _choose("実験の名前", _subdirs(data_root))
    subject = args.subject or (_choose("実験対象者", _subdirs(data_root / experiment / "Skeleton")) if experiment else None)
    if not experiment or not subject:
        return 1

    subject_dir = data_root / experiment / "Skeleton" / subject
    folder = subject_dir / "Analysis" / "GazeBehavior"
    frames_path = folder / f"{PREFIX}{args.source}{FRAMES_SUFFIX}"
    if not frames_path.exists():
        print(f"シーン6の結果がありません: {frames_path}")
        return 1
    params_path = folder / f"{PREFIX}{args.source}_params.json"
    params = json.loads(params_path.read_text(encoding="utf-8")) if params_path.exists() else {}

    print(f"読み込み中: {frames_path}")
    persons, _ = load_frames(frames_path)
    child_frames, parent_frames = persons[args.child], persons[args.parent]
    events = [a for a in load_approaches(folder / f"{PREFIX}{args.source}_approaches.csv") if a["person"] == args.child]
    times = np.array([e["arriveSec"] for e in events])

    # 親がその物のそばにいたか(位置はどのデータでも正確なので、キネクトのもので分ける)
    def parent_near(e: dict, t: float, near: float = 1.0) -> bool:
        column = parent_frames.get(f"dist_{e['target']}")
        d = value_at(parent_frames["time"], column, np.array([t]), 0.2)[0] if column is not None else math.nan
        return not math.isnan(d) and d <= near

    away_idx = np.array([i for i, e in enumerate(events) if not parent_near(e, e["arriveSec"])], dtype=int)
    away_times = times[away_idx]
    t0 = float(max(child_frames["time"][0], parent_frames["time"][0]))
    t1 = float(min(child_frames["time"][-1], parent_frames["time"][-1]))
    half = (t0 + t1) / 2

    # 頭の向きの推定方法
    filtered = subject_dir / "Filtered"
    variants: dict[str, dict] = {"キネクト(シーン6)": {args.child: track_from_frames(child_frames), args.parent: track_from_frames(parent_frames)}}
    for label, path, mode in [("キネクト 平滑化 smooth05", filtered / "grouped_HeadJoints_smooth05.json", "kinect"),
                              ("画像 視線", filtered / "grouped_Image.json", "image_gaze"),
                              ("画像 頭の向き", filtered / "grouped_Image.json", "image_head")]:
        tracks = tracks_from_grouped(path, mode)
        if tracks and args.child in tracks and args.parent in tracks:
            variants[label] = tracks
    base_pair = Pair(variants["キネクト(シーン6)"][args.child], variants["キネクト(シーン6)"][args.parent])
    move_t, move_yaw = child_frames["time"], child_frames["moveYaw"]

    sections = []

    # ---------- H1 / H2(親子の向き) ----------
    for key, title, fn, direction, ev_times in [
        ("H1", "社会的参照: 子供は親から離れた物へ向かう前(6〜2 秒前)に親の方を向く", h1_value, +1, away_times),
        ("H2", "親の見落とし: 親から離れた物に子供が着いた後(1〜5 秒後)、親の視野に子供が入りにくい", h2_value, -1, away_times),
    ]:
        s = Section(key, title, "値 = 接近の前後の時間帯の割合 − ふだんの割合(ポイント)。"
                    f"親から離れた物(親がその物の 1 m より外)への接近 {len(ev_times)} 回。視野 = 頭の向きから ±角度。")
        observed = fn(base_pair, ev_times, 60.0)
        lo, hi = bootstrap(lambda idx: fn(base_pair, ev_times[idx], 60.0), len(ev_times))
        p = shift_p(lambda tt: fn(base_pair, tt, 60.0), ev_times, observed, t0, t1, args.shifts, direction)
        rows = [["基準(キネクト、±60°)", fmt_points(observed), f"95% 区間 {fmt_points(lo)} 〜 {fmt_points(hi)}", f"時間シフト p = {p:.3f}"]]
        signs = []

        def same(v: float) -> bool:
            return not math.isnan(v) and (v > 0) == (direction > 0) and abs(v) >= 0.03

        for sigma in (10, 20, 30):
            vals = [fn(base_pair, ev_times, 60.0, slow_noise(len(base_pair.t), sigma), slow_noise(len(base_pair.t), sigma))
                    for _ in range(args.noise_repeats)]
            ok = [v for v in vals if not math.isnan(v)]
            share = np.mean([same(v) for v in ok]) if ok else math.nan
            rows.append([f"頭の向きに誤差 ±{sigma}°", fmt_points(float(np.mean(ok)) if ok else math.nan),
                         f"{args.noise_repeats} 回中 {share * 100:.0f}% で同じ向き", ""])
            signs.append(bool(ok) and share >= 0.8)
        for label, tracks in variants.items():
            if label == "キネクト(シーン6)":
                continue
            v = fn(Pair(tracks[args.child], tracks[args.parent]), ev_times, 60.0)
            rows.append([f"推定方法: {label}", fmt_points(v), "", ""])
            signs.append(same(v))
        for view in (45, 75, 90):
            v = fn(base_pair, ev_times, float(view))
            rows.append([f"視野 ±{view}°", fmt_points(v), "", ""])
            signs.append(same(v))
        for label, sel in [("前半", ev_times < half), ("後半", ev_times >= half)]:
            v = fn(base_pair, ev_times[sel], 60.0)
            rows.append([f"{label}({int(sel.sum())} 回)", fmt_points(v), "", ""])
            signs.append(same(v))
        s.tables.append(("", ["確かめ方", "値", "ばらつき", "偶然の確率"], rows))
        s.findings.append(f"基準 {fmt_points(observed)}(p = {p:.3f})。ブレを入れた計算: {verdict(signs)}"
                          f"(同じ向き = {'ふだんより多い' if direction > 0 else 'ふだんより少ない'} かつ差が 3 pt 以上)")
        sections.append(s)

    # ---------- H3(位置だけ) ----------
    s = Section("H3", "親のもとへ: 子供が近づく物は、そのとき親がそばにいる物が多い",
                f"値 = 接近のうち親がその物のそばにいた割合 − ふだん親がその物のそばにいる割合。全接近 {len(events)} 回。位置だけを使う。")
    obs = h3_value(parent_frames, events, times, 1.0)
    lo, hi = bootstrap(lambda idx: h3_value(parent_frames, [events[i] for i in idx], times[idx], 1.0), len(events))
    p = shift_p(lambda tt: h3_value(parent_frames, events, tt, 1.0), times, obs, t0, t1, args.shifts, +1)
    rows = [["基準(そば = 1.0 m)", fmt_points(obs), f"95% 区間 {fmt_points(lo)} 〜 {fmt_points(hi)}", f"時間シフト p = {p:.3f}"]]
    signs = []
    for near in (0.5, 1.5):
        v = h3_value(parent_frames, events, times, near)
        rows.append([f"そば = {near} m", fmt_points(v), "", ""])
        signs.append(not math.isnan(v) and v >= 0.03)
    for label, sel in [("前半", times < half), ("後半", times >= half)]:
        v = h3_value(parent_frames, [e for e, k in zip(events, sel) if k], times[sel], 1.0)
        rows.append([f"{label}({int(sel.sum())} 回)", fmt_points(v), "", ""])
        signs.append(not math.isnan(v) and v >= 0.03)
    s.tables.append(("", ["確かめ方", "値", "ばらつき", "偶然の確率"], rows))
    s.findings.append(f"基準 {fmt_points(obs)}(p = {p:.3f})。ブレを入れた計算: {verdict(signs)}")
    sections.append(s)

    # ---------- H4(視線の先行) ----------
    s = Section("H4", "視線の先行: 子供の頭の向きは進行方向より先に変わる",
                "値 = 頭の向き(t) と進行方向(t + lag) のなす角が最も小さくなる lag。正なら頭が先。進行方向はキネクトの骨盤の動きから。")
    obs = h4_value(variants["キネクト(シーン6)"][args.child], move_t, move_yaw)
    rows = [["基準(キネクト)", fmt_lag(obs), "", ""]]
    signs = []
    for sigma in (10, 20, 30):
        vals = [h4_value(variants["キネクト(シーン6)"][args.child], move_t, move_yaw, slow_noise(len(child_frames["time"]), sigma))
                for _ in range(max(5, args.noise_repeats // 4))]
        rows.append([f"頭の向きに誤差 ±{sigma}°", fmt_lag(float(np.nanmean(vals))), f"{np.mean([v > 0 for v in vals]) * 100:.0f}% で正", ""])
        signs.append(float(np.mean([v > 0 for v in vals])) >= 0.8)
    for label, tracks in variants.items():
        if label != "キネクト(シーン6)":
            v = h4_value(tracks[args.child], move_t, move_yaw)
            rows.append([f"推定方法: {label}", fmt_lag(v), "", ""])
            signs.append(not math.isnan(v) and v > 0)
    for label, lo_t, hi_t in [("前半", t0, half), ("後半", half, t1)]:
        c = variants["キネクト(シーン6)"][args.child]
        sel = (c["time"] >= lo_t) & (c["time"] < hi_t)
        v = h4_value({k: v[sel] for k, v in c.items()}, move_t, move_yaw)
        rows.append([label, fmt_lag(v), "", ""])
        signs.append(not math.isnan(v) and v > 0)
    s.tables.append(("", ["確かめ方", "lag", "ばらつき", ""], rows))
    s.findings.append(f"基準 {fmt_lag(obs)}。ブレを入れた計算: {verdict(signs)}(同じ向き = lag が正)")
    sections.append(s)

    # ---------- H5(物の方を向く) ----------
    s = Section("H5", "物を見てから: 子供は物に着く前(5〜0 秒前)にその物の方を向いている",
                f"値 = 着く前にその物の方(頭の向きとのなす角 ≤ 30°)を向いていた割合 − ふだんその物の方を向いている割合。全接近 {len(events)} 回。"
                "物の位置はシーン6の対象物体なので、キネクトの頭の向きだけで確かめる。")
    obs = h5_value(child_frames, events, times, 30.0)
    lo, hi = bootstrap(lambda idx: h5_value(child_frames, [events[i] for i in idx], times[idx], 30.0), len(events))
    p = shift_p(lambda tt: h5_value(child_frames, events, tt, 30.0), times, obs, t0, t1, args.shifts, +1)
    rows = [["基準(キネクト、≤30°)", fmt_points(obs), f"95% 区間 {fmt_points(lo)} 〜 {fmt_points(hi)}", f"時間シフト p = {p:.3f}"]]
    signs = []
    for sigma in (10, 20, 30):
        vals = [h5_value(child_frames, events, times, 30.0, slow_noise(len(child_frames["time"]), sigma)) for _ in range(args.noise_repeats)]
        rows.append([f"頭の向きに誤差 ±{sigma}°", fmt_points(float(np.nanmean(vals))), f"{np.mean([v >= 0.03 for v in vals]) * 100:.0f}% で同じ向き", ""])
        signs.append(float(np.mean([v >= 0.03 for v in vals])) >= 0.8)
    for angle in (15, 45, 60):
        v = h5_value(child_frames, events, times, float(angle))
        rows.append([f"なす角 ≤ {angle}°", fmt_points(v), "", ""])
        signs.append(not math.isnan(v) and v >= 0.03)
    for label, sel in [("前半", times < half), ("後半", times >= half)]:
        v = h5_value(child_frames, [e for e, k in zip(events, sel) if k], times[sel], 30.0)
        rows.append([f"{label}({int(sel.sum())} 回)", fmt_points(v), "", ""])
        signs.append(not math.isnan(v) and v >= 0.03)
    s.tables.append(("", ["確かめ方", "値", "ばらつき", "偶然の確率"], rows))
    s.findings.append(f"基準 {fmt_points(obs)}(p = {p:.3f})。ブレを入れた計算: {verdict(signs)}")
    sections.append(s)

    meta = [
        f"{experiment} / {subject} / 基準 {args.source}(シーン6 {params.get('createdAt', '?')})",
        f"子供 {args.child} の接近 {len(events)} 回(うち親から離れた物 {len(away_times)} 回)、2人とも映っている {t0:.0f}〜{t1:.0f} 秒",
        "推定方法: " + " / ".join(variants),
        "p は「接近の時刻をでたらめにずらしても同じくらいの差が出る確率」。0.05 未満なら偶然とは考えにくい。",
    ]
    print_sections(sections)
    out_path = folder / f"robustness_{args.source}.html"
    out_path.write_text(render_html(f"仮説の崩れにくさ {subject}", meta, sections), encoding="utf-8")
    print(f"\n保存しました: {out_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
