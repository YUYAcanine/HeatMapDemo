"""シーン5(5GazeExport)の書き出しを読む部品と、頭の向きの計算。Tools/ の解析の実行ファイルが共通で使う。直接は実行しない.

  read_csv / Person      … <人物>_gaze.csv・<人物>_gaze_persons.csv・<人物>_distance.csv を読む
  Skeleton               … <人物>_skeleton.csv(全関節。Person.skeleton で使う)
  find_sources / choose / subdirs … 書き出しやフォルダを一覧から番号で選ぶ
  load_light_log / state_at       … light_log.json(ライトの点灯)
  episodes / approaches           … 続いている区間・物体への接近(到達)の時刻
  smooth_vectors / body_forward / span_pairs / signed_yaw / angle_between … 頭や肩の向きを滑らかにする・角度
"""

from __future__ import annotations

import csv
import json
from pathlib import Path

import numpy as np

NO_HIT = "None"
NO_DATA = "NoData"
# 映っていない時間がこれを超えたら、接近を数え直す(秒。シーン6と同じ)
TRACK_GAP = 1.0


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
        nan = np.full(len(self.time), np.nan)
        self.origin = np.column_stack([self.gaze.get(f"origin{a}", nan) for a in "XYZ"])
        self.direction = np.column_stack([self.gaze.get(f"direction{a}", nan) for a in "XYZ"])
        self._skeleton_path = folder / f"{name}_skeleton.csv"
        self._skeleton: Skeleton | None = None
        # 肩の位置(gaze.csv の列。無ければ skeleton.csv から。どちらも無い古い書き出しでは nan)
        self.has_shoulder_columns = "shoulderLeftX" in self.gaze

        if self.has_shoulder_columns:
            self.shoulder_left = np.column_stack([self.gaze[f"shoulderLeft{a}"] for a in "XYZ"])
            self.shoulder_right = np.column_stack([self.gaze[f"shoulderRight{a}"] for a in "XYZ"])
        elif self.skeleton is not None:
            self.has_shoulder_columns = True
            self.shoulder_left = self.skeleton.position("ShoulderLeft")
            self.shoulder_right = self.skeleton.position("ShoulderRight")
        else:
            self.shoulder_left = np.column_stack([nan, nan, nan])
            self.shoulder_right = np.column_stack([nan, nan, nan])

    @property
    def skeleton(self) -> Skeleton | None:
        """全関節(<人物>_skeleton.csv)。最初に使うときに読む。
        無い書き出し(古いシーン5)では、書き出し元の Filtered/grouped_*.json から同じ並びで読む。どちらも無ければ None"""
        if self._skeleton is None:
            if self._skeleton_path.exists():
                self._skeleton = Skeleton(self._skeleton_path)
            else:
                self._skeleton = Skeleton.from_grouped_json(self._skeleton_path.parent, self.name, len(self.time))
        return self._skeleton


class Skeleton:
    """シーン5の <人物>_skeleton.csv(全関節の位置 <関節>X,Y,Z と信頼度 <関節>C)。行は gaze.csv と同じ時刻の並び"""

    def __init__(self, path: Path):
        with path.open(encoding="utf-8-sig", newline="") as f:
            rows = list(csv.reader(f))

        header = rows[0]
        data = np.array(rows[1:], dtype=object)
        data[data == ""] = "nan"
        self.data = data.astype(float)
        self.column = {name: i for i, name in enumerate(header)}
        self.joints = [name[:-1] for name in header if name.endswith("C") and f"{name[:-1]}X" in self.column]

    @classmethod
    def from_grouped_json(cls, export_folder: Path, person_name: str, frame_count: int) -> "Skeleton | None":
        """skeleton.csv が無い古い書き出し用。<被験者>/Filtered/grouped_<骨格>.json から、gaze.csv と同じ行の並びで読む。
        書き出し元(export_params.json の skeletonSource)が分からない・フレーム数が合わないときは None"""
        try:
            params = json.loads((export_folder / "export_params.json").read_text(encoding="utf-8-sig"))
            source = params["skeletonSource"]
        except (OSError, KeyError, ValueError):
            return None

        path = export_folder.parents[2] / "Filtered" / f"grouped_{source.removeprefix('filtered_')}.json"
        if not path.exists():
            return None

        frames = json.loads(path.read_text(encoding="utf-8-sig"))["frames"]
        if len(frames) != frame_count:
            return None

        joints: list[str] = []
        values: dict[str, list[tuple[int, float, float, float, float]]] = {}
        for i, frame in enumerate(frames):
            person = next((p for p in frame["persons"] if p.get("label") == person_name), None)
            for j in person["joints"] if person else []:
                if j["jointId"] not in values:
                    joints.append(j["jointId"])
                    values[j["jointId"]] = []
                q = j["position"]
                values[j["jointId"]].append((i, q["x"], q["y"], q["z"], float(j.get("confidence", np.nan))))

        self = cls.__new__(cls)
        self.joints = joints
        self.column = {}
        self.data = np.full((frame_count, 4 * len(joints)), np.nan)
        for k, name in enumerate(joints):
            for a, axis in enumerate("XYZC"):
                self.column[f"{name}{axis}"] = 4 * k + a
            rows = np.array(values[name])
            self.data[rows[:, 0].astype(int), 4 * k:4 * k + 4] = rows[:, 1:]
        return self

    def position(self, joint: str) -> np.ndarray:
        """関節の位置(フレーム数 × 3。無いフレームは nan)"""
        return self.data[:, [self.column[f"{joint}{a}"] for a in "XYZ"]]

    def confidence(self, joint: str) -> np.ndarray:
        """関節の信頼度(0:None 1:Low 2:Medium 3:High。無いフレームは nan)"""
        return self.data[:, self.column[f"{joint}C"]]


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


def load_env_objects(experiment_dir: Path) -> tuple[list[str], dict[str, np.ndarray], dict[str, dict]] | None:
    """<実験>/Env/TargetObjects.json と Meshes/ から、対象物体の名前・頂点を囲む箱(ワールド座標)の中心と大きさ。
    シーン5の export_params.json の targets / targetCenters / targetSizes と同じもの(シーン0で物体を変えた後、
    シーン5で書き出し直す前でも使えるように)。無ければ None"""
    path = experiment_dir / "Env" / "TargetObjects.json"

    if not path.exists():
        return None

    names, centers, sizes = [], {}, {}

    for obj in json.loads(path.read_text(encoding="utf-8-sig")).get("objects", []):
        mesh = experiment_dir / "Env" / "Meshes" / obj.get("meshFile", "")

        if not mesh.is_file():
            continue

        v = np.array([[p["x"], p["y"], p["z"]] for p in json.loads(mesh.read_text(encoding="utf-8-sig"))["vertices"]])
        v = v * np.array([obj["scale"][k] for k in "xyz"])
        qx, qy, qz, qw = (obj["rotation"][k] for k in "xyzw")
        rotation = np.array([[1 - 2 * (qy * qy + qz * qz), 2 * (qx * qy - qz * qw), 2 * (qx * qz + qy * qw)],
                             [2 * (qx * qy + qz * qw), 1 - 2 * (qx * qx + qz * qz), 2 * (qy * qz - qx * qw)],
                             [2 * (qx * qz - qy * qw), 2 * (qy * qz + qx * qw), 1 - 2 * (qx * qx + qy * qy)]])
        v = v @ rotation.T + np.array([obj["position"][k] for k in "xyz"])
        lo, hi = v.min(axis=0), v.max(axis=0)
        names.append(obj["name"])
        centers[obj["name"]] = (lo + hi) / 2
        sizes[obj["name"]] = dict(zip("xyz", (hi - lo).tolist()))

    return names, centers, sizes


def box_distance(person: Person, center: np.ndarray, size: dict) -> np.ndarray:
    """骨盤(無ければ頭)から物体の箱(床に平行)までの水平の最短距離。箱の中なら 0(シーン5の distance.csv と同じ)"""
    x, z = person.distance["positionX"], person.distance["positionZ"]
    dx = np.maximum(np.maximum(center[0] - size["x"] / 2 - x, x - (center[0] + size["x"] / 2)), 0)
    dz = np.maximum(np.maximum(center[2] - size["z"] / 2 - z, z - (center[2] + size["z"] / 2)), 0)
    return np.hypot(dx, dz)


def state_at(log_times: np.ndarray, states: np.ndarray, times: np.ndarray) -> np.ndarray:
    """各時刻の直前の記録の状態(記録より前は消灯とする)"""
    index = np.searchsorted(log_times, times, side="right") - 1
    return np.where(index >= 0, states[np.clip(index, 0, None)], False)


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


def segments(time: np.ndarray, valid: np.ndarray, max_gap: float) -> list[np.ndarray]:
    """valid なフレームを、max_gap 秒より長い途切れで区切った番号の並び"""
    index = np.flatnonzero(valid)
    return np.split(index, np.flatnonzero(np.diff(time[index]) > max_gap) + 1) if len(index) else []


def smooth_vectors(time: np.ndarray, vectors: np.ndarray, valid: np.ndarray, window: float, max_gap: float,
                   causal: bool = False) -> np.ndarray:
    """valid なフレームの向きを滑らかにして長さ 1 にする(max_gap 秒より長い途切れはまたがない)。
    前後 window / 2 秒の中央値で飛び値(1フレームだけ大きくずれる向き)を消してから、同じ幅の平均でならす。
    causal のときは前後ではなく、その時刻までの window 秒だけを使う(リアルタイムで計算するときと同じになる)"""
    out = np.full(vectors.shape, np.nan)

    for seg in segments(time, valid, max_gap):
        t, v = time[seg], vectors[seg]

        if causal:
            lo = np.searchsorted(t, t - window, side="left")
            hi = np.arange(1, len(t) + 1)
        else:
            lo = np.searchsorted(t, t - window / 2, side="left")
            hi = np.searchsorted(t, t + window / 2, side="right")

        median = np.array([np.median(v[a:b], axis=0) for a, b in zip(lo, hi)])
        total = np.vstack([np.zeros((1, v.shape[1])), np.cumsum(median, axis=0)])
        mean = (total[hi] - total[lo]) / (hi - lo)[:, None]
        length = np.linalg.norm(mean, axis=1, keepdims=True)
        out[seg] = np.where(length > 1e-9, mean / np.maximum(length, 1e-9), np.nan)

    return out


def body_forward(person: Person, head_direction: np.ndarray, valid: np.ndarray, window: float, max_gap: float,
                 causal: bool = False) -> np.ndarray:
    """肩の正面(左右の肩を結ぶ線に垂直な水平の向き。長さ 1)。肩が無いフレームは nan。
    前後どちらかは、頭の向き(水平)と同じ側が多い方にそろえる(体の向きと頭の向きが 90 度以上ずれることはほとんど無いので)"""
    across = person.shoulder_right - person.shoulder_left
    across[:, 1] = 0
    has_body = valid & np.isfinite(across).all(axis=1) & (np.linalg.norm(across, axis=1) > 0.05)
    forward = np.column_stack([-across[:, 2], np.zeros(len(across)), across[:, 0]])
    forward = smooth_vectors(person.time, forward, has_body, window, max_gap, causal)
    agree = np.einsum("ij,ij->i", forward, head_direction * np.array([1.0, 0.0, 1.0]))

    if np.isfinite(agree).any() and np.nanmedian(agree) < 0:
        forward = -forward

    return forward


def span_pairs(time: np.ndarray, valid: np.ndarray, max_gap: float, span: float) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    """各フレームの span / 2 秒前と span / 2 秒後のフレームの番号と、その間の秒数(途切れをまたぐときや両端は秒数 nan)"""
    n = len(time)
    before, after, seconds = np.arange(n), np.arange(n), np.full(n, np.nan)

    for seg in segments(time, valid, max_gap):
        t = time[seg]
        lo = np.searchsorted(t, t - span / 2, side="left")
        hi = np.searchsorted(t, t + span / 2, side="right") - 1
        before[seg], after[seg] = seg[lo], seg[hi]
        sec = t[hi] - t[lo]
        seconds[seg] = np.where(sec >= span * 0.6, sec, np.nan)

    return before, after, seconds


def signed_yaw(forward: np.ndarray, v: np.ndarray) -> np.ndarray:
    """水平面で forward から v への角度(度)。右(Unity の +x 側)へ回ると +"""
    fx, fz, vx, vz = forward[:, 0], forward[:, 2], v[:, 0], v[:, 2]
    return np.degrees(np.arctan2(vx * fz - vz * fx, vx * fx + vz * fz))


def angle_between(a: np.ndarray, b: np.ndarray) -> np.ndarray:
    """3次元の角度(度)"""
    cross = np.linalg.norm(np.cross(a, b), axis=1)
    return np.degrees(np.arctan2(cross, np.einsum("ij,ij->i", a, b)))
