"""キネクト1台分の MKV のカラー画像を解析する(深度は使わない).

フレームごとに
  1. RTMW (全身133点) で人を見つける (Kinect の骨格推定は使わない)
  2. 頭を切り出して 6DRepNet360 (頭の向き) と L2CS-Net (目の視線) を推定する
  3. 体の17点それぞれについて「カメラからその点へ向かう光線」を部屋座標で求める
3次元の位置は fuse.py で、2台以上のカメラの光線を三角測量して求める.
結果は Raw~/<ID>_image_pose.pkl に保存する.
"""

from __future__ import annotations

import pickle
import time
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np

from .heads import crop, observe_head
from .kinect import RawRecording
from .models import HeadPoseModel, L2CSGaze, gaze_vector

CACHE_VERSION = 3
BODY_SCORE_MIN = 3.0  # 体の17点のスコアの平均がこれ未満の検出は使わない(物を人と間違えたもの)
KEYPOINT_SCORE_MIN = 3.0  # 三角測量に使うキーポイントのスコアの下限


@dataclass
class PersonObservation:
    body_score: float
    face_score: float
    keypoints_2d: np.ndarray  # (133,2) カラー画像
    scores: np.ndarray  # (133,)
    camera_room: np.ndarray  # カラーカメラの位置(部屋座標)
    keypoint_rays_room: np.ndarray  # (17,3) カメラから体の各点へ向かう光線(部屋座標, 単位ベクトル)
    head_ray_room: np.ndarray  # カメラから頭(鼻・目・耳の重み付き平均)へ向かう光線
    head_dir_room: np.ndarray  # 顔の正面の向き(部屋座標, 単位ベクトル)
    gaze_dir_room: np.ndarray  # 目の視線(部屋座標, 単位ベクトル)
    facing_camera: float  # 顔がこのカメラを向いている度合い (-1 背中〜 1 正面)


@dataclass
class FrameObservation:
    index: int
    time_sec: float
    persons: list[PersonObservation] = field(default_factory=list)


def _remove_duplicates(kpts: np.ndarray, scores: np.ndarray, iou_threshold: float = 0.5):
    """同じ人を2回検出したものを除く(キーポイントの外接矩形が重なるものはスコアが高い方だけ残す)."""
    order = np.argsort([-float(np.mean(s[:17])) for s in scores])
    boxes = []
    for i in order:
        visible = scores[i][:17] > KEYPOINT_SCORE_MIN
        pts = kpts[i][:17][visible] if visible.sum() >= 3 else kpts[i][:17]
        box = np.concatenate([pts.min(0), pts.max(0)])
        if any(_iou(box, b) > iou_threshold for b in boxes):
            continue
        boxes.append(box)
        yield kpts[i], scores[i]


def _iou(a: np.ndarray, b: np.ndarray) -> float:
    x0, y0 = max(a[0], b[0]), max(a[1], b[1])
    x1, y1 = min(a[2], b[2]), min(a[3], b[3])
    inter = max(0.0, x1 - x0) * max(0.0, y1 - y0)
    union = (a[2] - a[0]) * (a[3] - a[1]) + (b[2] - b[0]) * (b[3] - b[1]) - inter
    return inter / union if union > 0 else 0.0


def process_recording(rec: RawRecording, step: int = 1, device: str = "cuda", log_every: int = 100,
                      detector=None, head_model=None, gaze_model=None) -> list[FrameObservation]:
    import torch  # noqa: F401  CUDA の DLL を onnxruntime より先に読み込む
    from rtmlib import Wholebody

    detector = detector or Wholebody(mode="performance", backend="onnxruntime", device=device)
    head_model = head_model or HeadPoseModel(device)
    gaze_model = gaze_model or L2CSGaze(device)

    results: list[FrameObservation] = []
    total = len(rec.index["frames"])
    camera_room = rec.color_camera_position_room()
    started = time.time()

    for n, (idx, t, color, _depth) in enumerate(rec.frames(step=step, read_depth=False)):
        frame = FrameObservation(idx, t)
        results.append(frame)
        if color is None:
            continue  # 壊れたフレーム

        kpts, scores = detector(color)
        people, crops = [], []
        for k, s in _remove_duplicates(kpts, scores):
            if float(np.mean(s[:17])) < BODY_SCORE_MIN:
                continue
            head = observe_head(k, s, rec, color.shape)
            if head is None:
                continue
            people.append((k, s, head))
            crops.append(crop(color, head.box))

        if not people:
            continue

        facing_cam, _ = head_model(crops)
        gaze_yaw, gaze_pitch = gaze_model(crops)
        gaze_cam = gaze_vector(gaze_yaw, gaze_pitch)

        for i, (k, s, head) in enumerate(people):
            rays = rec.calibration.color.unproject_rays(k[:17])
            rays /= np.linalg.norm(rays, axis=1, keepdims=True)
            frame.persons.append(PersonObservation(
                body_score=head.body_score,
                face_score=head.face_score,
                keypoints_2d=k.astype(np.float32),
                scores=s.astype(np.float32),
                camera_room=camera_room,
                keypoint_rays_room=rec.color_dir_to_room(rays),
                head_ray_room=rec.color_dir_to_room(head.ray_color[None])[0],
                head_dir_room=rec.color_dir_to_room(facing_cam[i][None])[0],
                gaze_dir_room=rec.color_dir_to_room(gaze_cam[i][None])[0],
                # 顔の正面がカメラの方を向いているほど 1 (頭の向き・視線の信頼度に使う)
                facing_camera=float(-np.dot(facing_cam[i], head.ray_color)),
            ))

        if log_every and (n + 1) % log_every == 0:
            rate = (n + 1) / (time.time() - started)
            print(f"  Kinect{rec.kinect_id}: {min((n + 1) * step, total)}/{total} frames ({rate:.1f} fps)", flush=True)

    return results


def cache_path(rec: RawRecording) -> Path:
    return rec.raw_dir / f"{rec.kinect_id}_image_pose.pkl"


def save_cache(rec: RawRecording, frames: list[FrameObservation], step: int) -> Path:
    path = cache_path(rec)
    with open(path, "wb") as f:
        pickle.dump({"version": CACHE_VERSION, "kinect_id": rec.kinect_id, "step": step, "frames": frames}, f)
    return path


def load_cache(rec: RawRecording, step: int) -> list[FrameObservation] | None:
    path = cache_path(rec)
    if not path.exists():
        return None
    with open(path, "rb") as f:
        data = pickle.load(f)
    if data.get("version") != CACHE_VERSION or data.get("step") != step:
        return None
    return data["frames"]
