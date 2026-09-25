"""RTMW の全身キーポイントから頭を切り出す範囲を決める(深度は使わない).

RTMW (COCO-WholeBody 133点): 0 鼻, 1 左目, 2 右目, 3 左耳, 4 右耳, 5 左肩, 6 右肩, ... 23-90 顔の68点.
スコアは SimCC の値 (0〜10 程度, 3 以上ならだいたい信頼できる).
"""

from __future__ import annotations

from dataclasses import dataclass

import numpy as np

HEAD = [0, 1, 2, 3, 4]
FACE = list(range(23, 91))
FACE_POINT_THRESHOLD = 3.0  # 顔の点をこのスコア以上なら使う


@dataclass
class HeadObservation:
    box: tuple[int, int, int, int]  # 切り出す範囲 x0, y0, x1, y1 (カラー画像)
    face_score: float  # 顔の68点のスコアの平均(顔が見えているほど大きい)
    body_score: float  # 体の17点のスコアの平均
    ray_color: np.ndarray  # カラーカメラから頭(画像上の位置)へ向かう光線 (単位ベクトル)


def observe_head(kpts: np.ndarray, scores: np.ndarray, rec, image_shape) -> HeadObservation | None:
    h, w = image_shape[:2]
    face_s = float(np.mean(scores[FACE]))
    body_s = float(np.mean(scores[:17]))

    weights = np.clip(scores[HEAD], 0.1, None)
    center = (kpts[HEAD] * weights[:, None]).sum(0) / weights.sum()
    ray = rec.calibration.color.unproject_rays(center[None])[0]
    ray /= np.linalg.norm(ray)

    # 切り出しの大きさ:
    #   顔が見えている → 顔の68点の広がり(輪郭は耳から耳まで)の 1.7倍
    #   見えていない   → 耳の間隔・肩幅・目と鼻の広がりから
    face_visible = scores[FACE] > FACE_POINT_THRESHOLD
    if face_visible.sum() >= 20:
        fp = kpts[FACE][face_visible]
        center = (fp.min(0) + fp.max(0)) / 2
        size = 1.7 * float(np.ptp(fp, axis=0).max())
        cy = center[1] - 0.05 * size
    else:
        size = 1.8 * _pixel_size_guess(kpts, scores)
        cy = center[1] - 0.10 * size  # 目・鼻より少し上(額・頭頂)を中心にする
    size = float(np.clip(size, 24, min(h, w)))

    x0, y0 = int(round(center[0] - size / 2)), int(round(cy - size / 2))
    x1, y1 = x0 + int(round(size)), y0 + int(round(size))
    if x1 <= 0 or y1 <= 0 or x0 >= w or y0 >= h:
        return None

    return HeadObservation((x0, y0, x1, y1), face_s, body_s, ray)


def _pixel_size_guess(kpts: np.ndarray, scores: np.ndarray) -> float:
    """頭の大きさ(画素)のおおよその値. 耳の間隔・肩幅・目と鼻の広がりから求めた値のうち最大のもの
    (横顔では耳の間隔が、後ろ向きでは目と鼻の広がりが小さくなるので最大を取る)."""
    guesses = [30.0]
    if scores[3] > 2 and scores[4] > 2:
        guesses.append(float(np.linalg.norm(kpts[3] - kpts[4])) * 1.4)
    if scores[5] > 2 and scores[6] > 2:
        guesses.append(float(np.linalg.norm(kpts[5] - kpts[6])) * 0.55)
    guesses.append(float(np.ptp(kpts[HEAD], axis=0).max()) * 2.0)
    return max(guesses)


def crop(image: np.ndarray, box: tuple[int, int, int, int]) -> np.ndarray:
    """はみ出した部分を黒で埋めて四角く切り出す."""
    x0, y0, x1, y1 = box
    h, w = image.shape[:2]
    out = np.zeros((y1 - y0, x1 - x0, 3), dtype=image.dtype)
    sx0, sy0, sx1, sy1 = max(x0, 0), max(y0, 0), min(x1, w), min(y1, h)
    out[sy0 - y0:sy1 - y0, sx0 - x0:sx1 - x0] = image[sy0:sy1, sx0:sx1]
    return out
