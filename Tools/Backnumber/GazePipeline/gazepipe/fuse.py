"""全キネクトの画像解析の結果を三角測量でまとめ、人物ごとに追跡して Unity 用の JSON にする (深度は使わない).

出力は シーン3 の Filtered/filtered_<Mode>.json と同じ形式 (HomeFilteredSkeletonList) に、
人物ごとの headPosition / headDirection / gazeDirection を足したもの:
  Skeleton/<実験対象者>/Filtered/filtered_Image.json

まとめ方 (1/30秒ごと)
  1. 各キネクトの、時刻がいちばん近いフレームの人物を集める
  2. 別のキネクトの2人について、体の各点(鼻・目・耳・肩・肘・手首・腰・膝・足首)の光線どうしが
     どれだけ近くを通るかを調べ、中央値が ray_match_distance 以内なら同じ人とする
     (1本の光線が偶然交わるだけでは同じ人にならない)
  3. min_views 台以上のカメラで見えた人だけを残し、体の各点を三角測量する
       頭の位置 … 三角測量した両耳の中点(無ければ鼻・目・耳の平均)
       頭の向き … 顔がカメラを向いているキネクトほど重く
       視線     … 顔がカメラを向いていて顔の点が見えているキネクトだけ
  4. 頭の位置が近い人物トラックに引き継ぐ(HomeSkeletonFilter と同じ)
  5. トラックごとに向きを前後数フレームで滑らかにする
"""

from __future__ import annotations

import datetime
import json
from dataclasses import dataclass
from pathlib import Path

import numpy as np

from .process import KEYPOINT_SCORE_MIN, FrameObservation, PersonObservation

# COCO の17点 → Kinect の関節名(左右は本人から見た左右で同じ)
COCO_TO_KINECT = {
    0: "Nose", 1: "EyeLeft", 2: "EyeRight", 3: "EarLeft", 4: "EarRight",
    5: "ShoulderLeft", 6: "ShoulderRight", 7: "ElbowLeft", 8: "ElbowRight", 9: "WristLeft", 10: "WristRight",
    11: "HipLeft", 12: "HipRight", 13: "KneeLeft", 14: "KneeRight", 15: "AnkleLeft", 16: "AnkleRight",
}

GAZE_FACING_MIN = 0.3  # 視線を使うのは顔がこれ以上カメラを向いているとき
GAZE_FACE_SCORE_MIN = 4.5  # 視線を使うのは顔の点のスコアの平均がこれ以上のとき
HEAD_HEIGHT_RANGE = (0.3, 2.3)  # 頭の高さ(部屋座標の y, m)がこの範囲に入らないものは使わない


@dataclass
class Settings:
    sample_interval: float = 1.0 / 30.0
    max_frame_age: float = 0.05  # 時刻のずれがこれより大きいフレームは使わない
    min_views: int = 2  # 何台以上のカメラで見えた人を使うか
    ray_match_distance: float = 0.12  # 同じ人とみなす、体の各点の光線どうしの距離の中央値の上限(m)
    min_matched_points: int = 4  # 同じ人かを判定するのに使う、両方で見えている点の最小数
    ray_residual_max: float = 0.15  # 三角測量した点と各光線の距離の上限(m). 超える光線は外す
    track_match_distance: float = 1.0
    track_timeout: float = 2.0
    smooth_window: int = 5  # 向きを滑らかにするフレーム数(奇数)


@dataclass
class FusedPerson:
    head: np.ndarray
    head_dir: np.ndarray | None
    gaze_dir: np.ndarray | None
    gaze_confidence: float
    keypoints: np.ndarray  # (17,3) 三角測量できなかった点は NaN
    body_score: float
    kinects: list[str]
    track_id: int = -1


@dataclass
class _Track:
    id: int
    position: np.ndarray
    last_seen: float


# ------------------------------------------------------------
# 三角測量
# ------------------------------------------------------------
def _closest_points(o1, d1, o2, d2):
    """2本の光線の最も近い点どうしの中点と距離. 平行・カメラの後ろなら (None, inf)."""
    w = o1 - o2
    b = float(np.dot(d1, d2))
    denom = 1.0 - b * b
    if denom < 1e-6:
        return None, np.inf
    t1 = (b * np.dot(d2, w) - np.dot(d1, w)) / denom
    t2 = (np.dot(d2, w) - b * np.dot(d1, w)) / denom
    if t1 <= 0 or t2 <= 0:
        return None, np.inf
    p1, p2 = o1 + t1 * d1, o2 + t2 * d2
    return (p1 + p2) / 2, float(np.linalg.norm(p1 - p2))


def _triangulate(rays: list[tuple[np.ndarray, np.ndarray]], residual_max: float) -> np.ndarray | None:
    """複数の光線に最も近い点. 残差が大きい光線は外してやり直す(2本未満になったら None)."""
    rays = list(rays)
    while len(rays) >= 2:
        A = np.zeros((3, 3))
        b = np.zeros(3)
        for o, d in rays:
            P = np.eye(3) - np.outer(d, d)
            A += P
            b += P @ o
        if np.linalg.cond(A) > 1e6:
            return None  # 光線がほぼ平行
        x = np.linalg.solve(A, b)
        residuals = [np.linalg.norm((x - o) - np.dot(x - o, d) * d) if np.dot(x - o, d) > 0 else np.inf
                     for o, d in rays]
        worst = int(np.argmax(residuals))
        if residuals[worst] <= residual_max:
            return x
        rays.pop(worst)
    return None


def _match_distance(a: PersonObservation, b: PersonObservation, settings: Settings) -> float:
    """2人の体の各点の光線どうしの距離の中央値. 共通して見えている点が少なければ inf."""
    both = np.flatnonzero((a.scores[:17] >= KEYPOINT_SCORE_MIN) & (b.scores[:17] >= KEYPOINT_SCORE_MIN))
    if len(both) < settings.min_matched_points:
        return np.inf
    dists = []
    for j in both:
        point, dist = _closest_points(a.camera_room, a.keypoint_rays_room[j], b.camera_room, b.keypoint_rays_room[j])
        if point is None:
            return np.inf
        dists.append(dist)
    return float(np.median(dists))


def _fuse_cluster(members: list[tuple[str, PersonObservation]], settings: Settings) -> FusedPerson | None:
    keypoints = np.full((17, 3), np.nan)
    for j in range(17):
        rays = [(p.camera_room, p.keypoint_rays_room[j]) for _, p in members if p.scores[j] >= KEYPOINT_SCORE_MIN]
        x = _triangulate(rays, settings.ray_residual_max)
        if x is not None:
            keypoints[j] = x

    # 頭の中心: 両耳の中点 → 鼻・目・耳の平均 → 頭の光線の三角測量
    if not np.isnan(keypoints[[3, 4]]).any():
        head = (keypoints[3] + keypoints[4]) / 2
    elif not np.isnan(keypoints[:5, 0]).all():
        head = np.nanmean(keypoints[:5], axis=0)
    else:
        head = _triangulate([(p.camera_room, p.head_ray_room) for _, p in members], settings.ray_residual_max)
    if head is None or not HEAD_HEIGHT_RANGE[0] <= head[1] <= HEAD_HEIGHT_RANGE[1]:
        return None

    head_dir = _normalize(sum((0.3 + max(0.0, p.facing_camera)) * p.head_dir_room for _, p in members))

    gaze_dir, gaze_conf = None, 0.0
    gazes = [(p.facing_camera, p.gaze_dir_room) for _, p in members
             if p.facing_camera >= GAZE_FACING_MIN and p.face_score >= GAZE_FACE_SCORE_MIN]
    if gazes:
        gaze_dir = _normalize(sum(w * d for w, d in gazes))
        gaze_conf = float(max(w for w, _ in gazes))

    return FusedPerson(head, head_dir, gaze_dir, gaze_conf, keypoints,
                       float(max(p.body_score for _, p in members)), sorted({k for k, _ in members}))


# ------------------------------------------------------------
# まとめる
# ------------------------------------------------------------
def _nearest_frame(frames: list[FrameObservation], times: np.ndarray, t: float, max_age: float):
    if len(times) == 0:
        return None
    i = int(np.searchsorted(times, t))
    best = None
    for j in (i - 1, i):
        if 0 <= j < len(times) and abs(times[j] - t) <= max_age:
            if best is None or abs(times[j] - t) < abs(times[best] - t):
                best = j
    return frames[best] if best is not None else None


def _normalize(v):
    n = np.linalg.norm(v)
    return v / n if n > 1e-9 else None


def _cluster(observations: list[tuple[str, PersonObservation]], settings: Settings) -> list[list[tuple[str, PersonObservation]]]:
    """別のキネクトどうしで、体の光線がよく一致する組から順に同じ人としてまとめる(1人あたり各キネクト1人まで)."""
    pairs = []
    for i in range(len(observations)):
        for j in range(i + 1, len(observations)):
            if observations[i][0] == observations[j][0]:
                continue
            d = _match_distance(observations[i][1], observations[j][1], settings)
            if d <= settings.ray_match_distance:
                pairs.append((d, i, j))
    pairs.sort()

    cluster_of = {i: {i} for i in range(len(observations))}
    for _, i, j in pairs:
        ci, cj = cluster_of[i], cluster_of[j]
        if ci is cj:
            continue
        kinects_i = {observations[m][0] for m in ci}
        if any(observations[m][0] in kinects_i for m in cj):
            continue
        merged = ci | cj
        for m in merged:
            cluster_of[m] = merged

    clusters, seen = [], set()
    for i in range(len(observations)):
        c = cluster_of[i]
        if id(c) not in seen:
            seen.add(id(c))
            clusters.append([observations[m] for m in sorted(c)])
    return clusters


def fuse(per_kinect: dict[str, list[FrameObservation]], settings: Settings) -> list[tuple[float, list[FusedPerson]]]:
    series = {}
    end = 0.0
    for kid, frames in per_kinect.items():
        frames = sorted(frames, key=lambda f: f.time_sec)
        times = np.array([f.time_sec for f in frames])
        series[kid] = (frames, times)
        if len(times):
            end = max(end, float(times[-1]))

    tracks: list[_Track] = []
    next_id = 0
    output = []

    for n in range(int(np.floor(end / settings.sample_interval)) + 1):
        t = n * settings.sample_interval
        observations = []
        for kid, (frames, times) in series.items():
            frame = _nearest_frame(frames, times, t, settings.max_frame_age)
            if frame is not None:
                observations.extend((kid, p) for p in frame.persons)

        persons = []
        for members in _cluster(observations, settings):
            if len(members) < settings.min_views:
                continue  # 1台でしか見えていない人は3次元の位置が分からないので使わない
            fp = _fuse_cluster(members, settings)
            if fp is not None:
                persons.append(fp)

        # 人物トラックに引き継ぐ(近い組から順に)
        pairs = sorted(((float(np.linalg.norm(tr.position - fp.head)), ti, pi)
                        for ti, tr in enumerate(tracks) for pi, fp in enumerate(persons)), key=lambda x: x[0])
        used_t, used_p = set(), set()
        for d, ti, pi in pairs:
            if d > settings.track_match_distance or ti in used_t or pi in used_p:
                continue
            used_t.add(ti)
            used_p.add(pi)
            persons[pi].track_id = tracks[ti].id
        for pi, fp in enumerate(persons):
            if pi not in used_p:
                tracks.append(_Track(next_id, fp.head.copy(), t))
                fp.track_id = next_id
                next_id += 1
        for fp in persons:
            tr = next(tr for tr in tracks if tr.id == fp.track_id)
            tr.position, tr.last_seen = fp.head.copy(), t
        tracks = [tr for tr in tracks if t - tr.last_seen <= settings.track_timeout]

        output.append((t, persons))

    _smooth(output, settings.smooth_window)
    return output


def _smooth(output, window: int):
    """トラックごとに、頭の向きと視線を前後のフレームの平均で滑らかにする(欠けているフレームは使わない)."""
    if window <= 1:
        return
    half = window // 2
    by_track: dict[int, list[FusedPerson]] = {}
    for _, persons in output:
        for fp in persons:
            by_track.setdefault(fp.track_id, []).append(fp)
    for seq in by_track.values():
        for attr in ("head_dir", "gaze_dir"):
            raw = [getattr(fp, attr) for fp in seq]
            for i, fp in enumerate(seq):
                if raw[i] is None:
                    continue
                vs = [v for v in raw[max(0, i - half):i + half + 1] if v is not None]
                setattr(fp, attr, _normalize(np.sum(vs, axis=0)))


# ------------------------------------------------------------
# 書き出し
# ------------------------------------------------------------
def _vec(v) -> dict:
    return {"x": float(v[0]), "y": float(v[1]), "z": float(v[2])}


def _joints(fp: FusedPerson) -> list[dict]:
    kp = fp.keypoints
    conf = 3 if len(fp.kinects) > 2 else 2
    joints = []

    def add(name, pos):
        if pos is not None and not np.isnan(pos).any():
            joints.append({"jointId": name, "position": _vec(pos), "confidence": conf})

    def mid(a, b):
        if np.isnan(kp[a]).any() or np.isnan(kp[b]).any():
            return None
        return (kp[a] + kp[b]) / 2

    add("Head", fp.head)
    for i, name in COCO_TO_KINECT.items():
        add(name, kp[i])
    neck, pelvis = mid(5, 6), mid(11, 12)
    add("Neck", neck)
    add("Pelvis", pelvis)
    if neck is not None and pelvis is not None:
        add("SpineChest", neck + (pelvis - neck) * 0.25)
        add("SpineNavel", neck + (pelvis - neck) * 0.65)
    if neck is not None:
        if not np.isnan(kp[5]).any():
            add("ClavicleLeft", (neck + kp[5]) / 2)
        if not np.isnan(kp[6]).any():
            add("ClavicleRight", (neck + kp[6]) / 2)
    return joints


def write_json(path: Path, output, per_kinect: dict, settings: Settings, experiment: str, subject: str) -> dict:
    frames_json = []
    stats = {"samples": len(output), "persons": 0, "with_gaze": 0, "tracks": set()}
    for t, persons in output:
        pj = []
        for fp in persons:
            stats["persons"] += 1
            stats["tracks"].add(fp.track_id)
            stats["with_gaze"] += fp.gaze_dir is not None
            pj.append({
                "trackId": fp.track_id,
                "label": f"person_{fp.track_id}",
                "kinectId": "+".join(fp.kinects),
                "bodyId": 0,
                "confidence": fp.body_score,
                "allJointConfidence": fp.body_score,
                "headJointConfidence": fp.body_score,
                "joints": _joints(fp),
                "headPosition": _vec(fp.head),
                "hasHeadDirection": fp.head_dir is not None,
                "headDirection": _vec(fp.head_dir if fp.head_dir is not None else np.zeros(3)),
                "hasGazeDirection": fp.gaze_dir is not None,
                "gazeDirection": _vec(fp.gaze_dir if fp.gaze_dir is not None else np.zeros(3)),
                "gazeConfidence": fp.gaze_confidence,
            })
        frames_json.append({"timeSec": float(t), "persons": pj})

    data = {
        "experimentName": experiment,
        "subjectName": subject,
        "createdAt": datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
        "sourceKinects": sorted(per_kinect.keys()),
        "confidenceMode": "Image",
        "headJoints": [],
        "sameBodyDistance": settings.ray_match_distance,
        "trackMatchDistance": settings.track_match_distance,
        "trackTimeout": settings.track_timeout,
        "sampleInterval": settings.sample_interval,
        "maxFrameAge": settings.max_frame_age,
        "frames": frames_json,
    }
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, ensure_ascii=False), encoding="utf-8")
    stats["tracks"] = len(stats["tracks"])
    return stats
