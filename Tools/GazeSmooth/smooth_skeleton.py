"""人物ごとの骨格(grouped_<Mode>.json)の頭の向きを時間方向に平滑化して、シーン4/5で選べる骨格を作る。

シーン3で Save grouped した
  Skeleton/<実験対象者>/Filtered/grouped_<Mode>.json
を読み、頭の向き(Head→Nose)のビュンビュン跳ぶノイズを
  1. 中央値フィルタ … 一瞬だけ跳んだ向き(採用キネクトの切り替わりなど)を落とす
  2. 移動平均       … 残った細かい揺れをならす
で取り除いて(どちらも前後対称の窓なので遅れは出ない)
  Skeleton/<実験対象者>/Filtered/grouped_<Mode>_<名前>.json
に書き出す。シーン4/5のインスペクターの Skeleton Variant でその <名前> を選ぶと使える。

何も指定しないと、窓の長さを変えた3通りを作る(窓の長さで結論が変わらないかを確かめるため):
  smooth03 … 中央値 0.3秒 + 平均 0.2秒
  smooth05 … 中央値 0.5秒 + 平均 0.3秒
  smooth10 … 中央値 1.0秒 + 平均 0.5秒
シーン5でそれぞれ Save した後、compare_variants.py で集計を横に並べて比べられる。

平滑化のしかた:
  - 人物(グループ)ごとに、Head→Nose の単位ベクトルを平滑化する。記録が --max-gap 秒より長く途切れたところはまたがない
  - 平滑化の前後の向きの差だけ、頭の関節(Nose・目・耳)を Head を中心に回す。Head の位置と体の関節はそのまま
    (シーン4/5の Head Direction Method が HeadToNose でも EarsToNose でも平滑化した向きになる)
  - 画像で推定した骨格(grouped_Image.json)は headDirection / gazeDirection も同じように平滑化する
  - 中央値の窓の半分より短い視線の動きは消える(0.5秒なら約0.25秒未満)。出来事の始まり・終わりの時刻も少しぼやける

使い方:
  python Tools/GazeSmooth/smooth_skeleton.py <実験の名前> <実験対象者>
  (最初にデータの場所 Assets / D:\\HomeExperiment を選ぶ。--data-root D:\\HomeExperiment で指定もできる)
  (引数を省くと、フォルダの一覧から選ぶ)
  --mode HeadJoints            … 読み込む grouped_<Mode>.json (AllJoints / HeadJoints / Image)
  --median 0.5 --mean 0.3      … 窓の長さ(秒)を指定して1つだけ作る。名前は --name (省略時は custom)
numpy が必要(Tools/GazePipeline の .venv にも入っている)。
grouped_<Mode>.json を作り直したら、このスクリプトも実行し直す。
"""

from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import home_data_root  # noqa: E402  Tools/home_data_root.py

# Head を中心に回す関節(頭の向きの計算に使う関節)
ROTATED_JOINTS = ("Nose", "EyeLeft", "EyeRight", "EarLeft", "EarRight")

# (名前, 中央値の窓[秒], 平均の窓[秒])
DEFAULT_VARIANTS = [
    ("smooth03", 0.3, 0.2),
    ("smooth05", 0.5, 0.3),
    ("smooth10", 1.0, 0.5),
]


def _choose(label: str, options: list[str]) -> str | None:
    if not options:
        print(f"{label} がありません。")
        return None
    for i, name in enumerate(options, 1):
        print(f"  {i}. {name}")
    answer = input(f"{label} の番号: ").strip()
    if answer.isdigit() and 1 <= int(answer) <= len(options):
        return options[int(answer) - 1]
    return None


def _subdirs(path: Path) -> list[str]:
    return sorted(p.name for p in path.iterdir() if p.is_dir()) if path.exists() else []


def _unit(v: np.ndarray) -> np.ndarray:
    return v / np.maximum(np.linalg.norm(v, axis=-1, keepdims=True), 1e-12)


def _angle_deg(a: np.ndarray, b: np.ndarray) -> np.ndarray:
    return np.degrees(np.arccos(np.clip((a * b).sum(-1), -1.0, 1.0)))


def _window_frames(seconds: float, interval: float) -> int:
    """窓の長さ(秒)をフレーム数(奇数)にする。1 なら何もしない"""
    frames = int(round(seconds / interval))
    if frames < 2:
        return 1
    return frames if frames % 2 == 1 else frames + 1


def _sliding(x: np.ndarray, frames: int, func) -> np.ndarray:
    """前後対称の窓で func(中央値・平均)を取る。端は窓に入る分だけで計算する"""
    if frames <= 1 or len(x) < 2:
        return x
    half = frames // 2
    padded = np.full((len(x) + 2 * half, x.shape[1]), np.nan)
    padded[half:half + len(x)] = x
    windows = np.lib.stride_tricks.sliding_window_view(padded, frames, axis=0)
    return func(windows, axis=2)


def smooth_directions(times: np.ndarray, dirs: np.ndarray, median_frames: int, mean_frames: int,
                      max_gap: float) -> np.ndarray:
    """単位ベクトルの時系列を、中央値 → 平均 の順に平滑化する。max_gap より長い途切れはまたがない"""
    out = dirs.copy()
    if len(dirs) == 0:
        return out

    breaks = np.where(np.diff(times) > max_gap)[0] + 1

    for seg in np.split(np.arange(len(times)), breaks):
        d = dirs[seg]
        m = _sliding(d, median_frames, np.nanmedian)
        norm = np.linalg.norm(m, axis=1, keepdims=True)
        # 窓の中の向きがばらばらで中央値がほぼ0になったフレームは元の向きのまま
        m = np.where(norm > 1e-6, m / np.maximum(norm, 1e-12), d)
        a = _sliding(m, mean_frames, np.nanmean)
        norm = np.linalg.norm(a, axis=1, keepdims=True)
        out[seg] = np.where(norm > 1e-6, a / np.maximum(norm, 1e-12), m)

    return out


def rotate_about(points: np.ndarray, d_from: np.ndarray, d_to: np.ndarray) -> np.ndarray:
    """d_from を d_to に重ねる最小の回転(フレームごと)を、points(N,3)にかける"""
    axis = np.cross(d_from, d_to)
    s = np.linalg.norm(axis, axis=1, keepdims=True)
    c = (d_from * d_to).sum(1, keepdims=True)
    k = axis / np.maximum(s, 1e-12)
    rotated = points * c + np.cross(k, points) * s + k * (k * points).sum(1, keepdims=True) * (1.0 - c)
    # ほぼ同じ向き(または正反対で軸が決まらない)なら回さない
    return np.where(s > 1e-9, rotated, points)


def _xyz(v: dict) -> tuple[float, float, float]:
    return (v["x"], v["y"], v["z"])


def _set_xyz(v: dict, p: np.ndarray) -> None:
    v["x"], v["y"], v["z"] = float(p[0]), float(p[1]), float(p[2])


class PersonSeries:
    """1人分(1グループ)の、平滑化に使う時系列。書き込み先の dict への参照も持つ"""

    def __init__(self) -> None:
        self.times: list[float] = []
        self.head: list[tuple] = []
        self.joints: list[dict] = []            # フレームごと {関節名: (position の dict, 元の位置)}
        self.vec_times: dict[str, list[float]] = {"headDirection": [], "gazeDirection": []}
        self.vec_refs: dict[str, list[dict]] = {"headDirection": [], "gazeDirection": []}
        self.vec_raw: dict[str, list[tuple]] = {"headDirection": [], "gazeDirection": []}

    def add(self, t: float, person: dict) -> None:
        by_name = {j.get("jointId"): j for j in person.get("joints", [])}

        if "Head" in by_name and "Nose" in by_name:
            head = _xyz(by_name["Head"]["position"])
            nose = _xyz(by_name["Nose"]["position"])

            if sum((n - h) ** 2 for n, h in zip(nose, head)) >= 1e-8:
                self.times.append(t)
                self.head.append(head)
                self.joints.append({name: (by_name[name]["position"], _xyz(by_name[name]["position"]))
                                    for name in ROTATED_JOINTS if name in by_name})

        # 画像で推定した骨格だけ: 頭の向き・目の視線
        for key, flag in (("headDirection", "hasHeadDirection"), ("gazeDirection", "hasGazeDirection")):
            v = person.get(key)
            if person.get(flag) and isinstance(v, dict):
                raw = _xyz(v)
                if sum(c * c for c in raw) >= 1e-8:
                    self.vec_times[key].append(t)
                    self.vec_refs[key].append(v)
                    self.vec_raw[key].append(raw)

    def finish(self) -> None:
        self.t = np.array(self.times, dtype=float)
        self.h = np.array(self.head, dtype=float).reshape(-1, 3)
        nose = np.array([f["Nose"][1] for f in self.joints], dtype=float).reshape(-1, 3)
        self.raw_dir = _unit(nose - self.h)

    def apply(self, median_frames: int, mean_frames: int, max_gap: float) -> np.ndarray:
        """平滑化した向きを dict に書き込む(元の位置から毎回計算するので、何通りでも続けて呼べる)"""
        smooth = smooth_directions(self.t, self.raw_dir, median_frames, mean_frames, max_gap)

        for name in ROTATED_JOINTS:
            index = [i for i, f in enumerate(self.joints) if name in f]
            if not index:
                continue
            original = np.array([self.joints[i][name][1] for i in index], dtype=float)
            moved = self.h[index] + rotate_about(original - self.h[index], self.raw_dir[index], smooth[index])
            for row, i in enumerate(index):
                _set_xyz(self.joints[i][name][0], moved[row])

        for key in self.vec_times:
            if not self.vec_raw[key]:
                continue
            raw = np.array(self.vec_raw[key], dtype=float)
            length = np.linalg.norm(raw, axis=1, keepdims=True)
            result = smooth_directions(np.array(self.vec_times[key]), raw / length, median_frames, mean_frames,
                                       max_gap) * length
            for row, ref in enumerate(self.vec_refs[key]):
                _set_xyz(ref, result[row])

        return smooth


def jitter(times: np.ndarray, dirs: np.ndarray, max_gap: float) -> np.ndarray:
    """となりのフレームとの向きの差(度)。途切れをまたぐところは除く"""
    if len(times) < 2:
        return np.zeros(0)
    ok = np.diff(times) <= max_gap
    return _angle_deg(dirs[1:], dirs[:-1])[ok]


def _report_line(label: str, angles: np.ndarray) -> str:
    if len(angles) == 0:
        return f"    {label:<10} (データなし)"
    p50, p90, p99 = np.percentile(angles, [50, 90, 99])
    return (f"    {label:<10} 1フレームの向きの変化  中央値 {p50:5.2f}°  90% {p90:5.1f}°  99% {p99:5.1f}°  "
            f"10°以上 {np.mean(angles > 10.0) * 100:4.1f}%")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    home_data_root.add_argument(parser)
    parser.add_argument("experiment", nargs="?", help="実験の名前 (省略すると一覧から選ぶ)")
    parser.add_argument("subject", nargs="?", help="実験対象者 (省略すると一覧から選ぶ)")
    parser.add_argument("--mode", default="HeadJoints", help="読み込む grouped_<Mode>.json の Mode (既定 HeadJoints)")
    parser.add_argument("--median", type=float, help="中央値フィルタの窓(秒)。指定するとこの1通りだけ作る")
    parser.add_argument("--mean", type=float, help="移動平均の窓(秒)。--median と一緒に指定する(省略時 0 = 平均なし)")
    parser.add_argument("--name", help="--median を指定したときの出力の名前 (省略時 custom)")
    parser.add_argument("--max-gap", type=float, default=0.2, help="これより長い記録の途切れはまたがない(秒, 既定 0.2)")
    args = parser.parse_args()

    data_root = home_data_root.choose_data_root(args.data_root)
    if data_root is None:
        return 1

    grouped_name = f"grouped_{args.mode}.json"

    experiment = args.experiment or _choose("実験の名前", _subdirs(data_root))
    if not experiment:
        return 1

    skeleton_root = data_root / experiment / "Skeleton"
    subject = args.subject or _choose(
        "実験対象者", [s for s in _subdirs(skeleton_root) if (skeleton_root / s / "Filtered" / grouped_name).exists()])
    if not subject:
        return 1

    filtered_dir = skeleton_root / subject / "Filtered"
    source_path = filtered_dir / grouped_name

    if not source_path.exists():
        print(f"人物ごとの骨格がありません。シーン3でグループを作って Save grouped を押してください: {source_path}")
        return 1

    if args.median is not None:
        name = (args.name or "custom").strip()
        variants = [(name, args.median, args.mean or 0.0)]
    elif args.mean is not None or args.name is not None:
        print("--mean / --name は --median と一緒に指定してください。")
        return 1
    else:
        variants = DEFAULT_VARIANTS

    for name, _, _ in variants:
        if not name or any(c in name for c in '\\/:*?"<>| '):
            print(f"出力の名前に使えない文字があります: '{name}'")
            return 1

    print(f"{source_path} を読み込んでいます ({source_path.stat().st_size / 1e6:.0f} MB。数十秒かかります)...")
    started = time.time()
    with source_path.open(encoding="utf-8") as f:
        data = json.load(f)
    print(f"  読み込み {time.time() - started:.0f} 秒")

    frames = data.get("frames") or []
    series: dict[int, PersonSeries] = {}
    labels: dict[int, str] = {}

    for frame in frames:
        t = frame["timeSec"]
        for person in frame.get("persons", []):
            track_id = person.get("trackId", -1)
            series.setdefault(track_id, PersonSeries()).add(t, person)
            labels.setdefault(track_id, person.get("label") or f"person_{track_id}")

    for s in series.values():
        s.finish()

    interval = data.get("sampleInterval") or 0.0
    if interval <= 0.0 and len(frames) > 1:
        interval = float(np.median(np.diff([f["timeSec"] for f in frames])))
    if interval <= 0.0:
        print("フレームの間隔が分かりません(フレームが足りません)。")
        return 1

    print(f"  {len(frames)} フレーム, 間隔 {interval * 1000:.1f} ms, 人物 {len(series)} 人")
    print("  平滑化前:")
    for track_id, s in series.items():
        print(_report_line(labels[track_id], jitter(s.t, s.raw_dir, args.max_gap)))

    source_created_at = data.get("createdAt", "")

    for name, median_sec, mean_sec in variants:
        median_frames = _window_frames(median_sec, interval)
        mean_frames = _window_frames(mean_sec, interval)

        print(f"\n{name}: 中央値 {median_sec} 秒 ({median_frames} フレーム) + 平均 {mean_sec} 秒 ({mean_frames} フレーム)")

        for track_id, s in series.items():
            smooth = s.apply(median_frames, mean_frames, args.max_gap)
            print(_report_line(labels[track_id], jitter(s.t, smooth, args.max_gap)))

        data["createdAt"] = time.strftime("%Y-%m-%d %H:%M:%S")
        data["smoothing"] = {
            "source": grouped_name,
            "sourceCreatedAt": source_created_at,
            "name": name,
            "medianSeconds": median_sec,
            "meanSeconds": mean_sec,
            "medianFrames": median_frames,
            "meanFrames": mean_frames,
            "maxGapSeconds": args.max_gap,
            "rotatedJoints": list(ROTATED_JOINTS),
        }

        output_path = filtered_dir / f"grouped_{args.mode}_{name}.json"
        started = time.time()
        with output_path.open("w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, separators=(",", ":"))
        print(f"  → {output_path} ({time.time() - started:.0f} 秒)")

    print("\nシーン4/5のインスペクターの Skeleton Variant で名前を選ぶと使えます"
          f" ({', '.join(name for name, _, _ in variants)})。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
