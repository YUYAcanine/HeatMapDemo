"""頭の向き (6DRepNet360) と目の視線 (L2CS-Net, Gaze360 で学習) の推定.

どちらも頭の周りを切り出した画像 (224x224) を入力にする.
出力はカラーカメラ座標 (X右 Y下 Z前) の単位ベクトル「顔(目)が向いている方向」.
"""

from __future__ import annotations

from pathlib import Path

import cv2
import numpy as np
import torch
import torch.nn as nn
import torchvision

MODEL_DIR = Path(__file__).resolve().parent.parent / "models"
_MEAN = np.array([0.485, 0.456, 0.406], dtype=np.float32)
_STD = np.array([0.229, 0.224, 0.225], dtype=np.float32)


def _to_tensor(crops_bgr: list[np.ndarray], device: str) -> torch.Tensor:
    batch = []
    for crop in crops_bgr:
        rgb = cv2.cvtColor(cv2.resize(crop, (224, 224), interpolation=cv2.INTER_LINEAR), cv2.COLOR_BGR2RGB)
        batch.append(((rgb.astype(np.float32) / 255.0) - _MEAN) / _STD)
    return torch.from_numpy(np.stack(batch).transpose(0, 3, 1, 2)).to(device)


def _rotation_from_6d(x: torch.Tensor) -> torch.Tensor:
    """6DRepNet の compute_rotation_matrix_from_ortho6d と同じ (列が x, y, z 軸)."""
    a1, a2 = x[:, 0:3], x[:, 3:6]
    b1 = nn.functional.normalize(a1, dim=1)
    b3 = nn.functional.normalize(torch.cross(b1, a2, dim=1), dim=1)
    b2 = torch.cross(b3, b1, dim=1)
    return torch.stack([b1, b2, b3], dim=2)


def _euler_from_rotation(R: np.ndarray) -> np.ndarray:
    """6DRepNet の compute_euler_angles_from_rotation_matrices と同じ. (pitch, yaw, roll) rad."""
    sy = np.sqrt(R[:, 0, 0] ** 2 + R[:, 1, 0] ** 2)
    singular = sy < 1e-6
    x = np.where(singular, np.arctan2(-R[:, 1, 2], R[:, 1, 1]), np.arctan2(R[:, 2, 1], R[:, 2, 2]))
    y = np.arctan2(-R[:, 2, 0], sy)
    z = np.where(singular, 0.0, np.arctan2(R[:, 1, 0], R[:, 0, 0]))
    return np.stack([x, y, z], axis=1)


def facing_from_pitch_yaw(pitch: np.ndarray, yaw: np.ndarray) -> np.ndarray:
    """Hopenet/6DRepNet の draw_axis で「画面から出てくる軸」(= 顔の正面) をカメラ座標 (X右 Y下 Z前) にしたもの.

    draw_axis: yaw' = -yaw, 画面上の向き = (sin(yaw'), -cos(yaw') sin(pitch)).
    顔がカメラを向いている (yaw=pitch=0) とき正面は -Z.
    """
    yp = -yaw
    v = np.stack([np.sin(yp), -np.cos(yp) * np.sin(pitch), -np.cos(yp) * np.cos(pitch)], axis=1)
    return v / np.linalg.norm(v, axis=1, keepdims=True)


class HeadPoseModel:
    """6DRepNet360 (300W-LP + CMU Panoptic で学習, 後ろ向きを含む全方向)."""

    def __init__(self, device: str = "cuda"):
        self.device = device
        net = torchvision.models.resnet50()
        net.fc = nn.Linear(2048, 6)
        state = torch.load(MODEL_DIR / "6DRepNet360.pth", map_location="cpu", weights_only=False)
        state = {("fc." + k[len("linear_reg."):]) if k.startswith("linear_reg.") else k: v for k, v in state.items()}
        net.load_state_dict(state)
        # 6DRepNet360 は AvgPool2d(7) だが 224 入力では AdaptiveAvgPool と同じ
        self.net = net.eval().to(device)

    @torch.no_grad()
    def __call__(self, crops_bgr: list[np.ndarray]) -> tuple[np.ndarray, np.ndarray]:
        """→ (正面の向き (N,3) カメラ座標, (pitch, yaw, roll) rad (N,3))."""
        if not crops_bgr:
            return np.zeros((0, 3)), np.zeros((0, 3))
        R = _rotation_from_6d(self.net(_to_tensor(crops_bgr, self.device))).cpu().numpy()
        self.last_rotation = R
        euler = _euler_from_rotation(R)
        return facing_from_pitch_yaw(euler[:, 0], euler[:, 1]), euler


class L2CSGaze:
    """L2CS-Net (ResNet50, Gaze360 で学習). 90ビン x 4° で [-180, 180) の yaw / pitch を出す."""

    def __init__(self, device: str = "cuda"):
        from safetensors.torch import load_file

        self.device = device
        net = torchvision.models.resnet50()
        net.fc = nn.Identity()
        state = load_file(str(MODEL_DIR / "l2cs_gaze360_resnet50.safetensors"))
        backbone = {k: v for k, v in state.items() if not k.startswith("fc_")}
        net.load_state_dict(backbone, strict=True)
        self.net = net.eval().to(device)
        self.fc_yaw = nn.Linear(2048, 90).to(device)
        self.fc_pitch = nn.Linear(2048, 90).to(device)
        self.fc_yaw.load_state_dict({"weight": state["fc_yaw_gaze.weight"], "bias": state["fc_yaw_gaze.bias"]})
        self.fc_pitch.load_state_dict({"weight": state["fc_pitch_gaze.weight"], "bias": state["fc_pitch_gaze.bias"]})
        self.idx = torch.arange(90, dtype=torch.float32, device=device)

    @torch.no_grad()
    def __call__(self, crops_bgr: list[np.ndarray]) -> tuple[np.ndarray, np.ndarray]:
        """→ (yaw, pitch) rad (N,), (N,). 向きへの変換は gaze_vector で行う."""
        if not crops_bgr:
            return np.zeros(0), np.zeros(0)
        feat = self.net(_to_tensor(crops_bgr, self.device))
        yaw = (torch.softmax(self.fc_yaw(feat), dim=1) * self.idx).sum(1) * 4 - 180
        pitch = (torch.softmax(self.fc_pitch(feat), dim=1) * self.idx).sum(1) * 4 - 180
        return np.deg2rad(yaw.cpu().numpy()), np.deg2rad(pitch.cpu().numpy())


def gaze_vector(yaw: np.ndarray, pitch: np.ndarray, convention: tuple[int, int, bool] = (1, 1, False)) -> np.ndarray:
    """L2CS の (yaw, pitch) → カメラ座標 (X右 Y下 Z前) の視線方向.

    既定値は L2CS の gazeto3d と同じ: (-cos(p) sin(y), -sin(p), -cos(p) cos(y)).
    記録データ(カメラの方を向いている顔 216件)で、6DRepNet360 の頭の向きとの差が最も小さい
    (中央値 27.6°. 他の取り方は 30°以上)ことを 2026-09-25 のテスト記録で確かめた.
    convention = (x の符号, y の符号, yaw と pitch を入れ替えるか) は確認用.
    """
    sx, sy, swap = convention
    if swap:
        yaw, pitch = pitch, yaw
    v = np.stack([sx * -np.cos(pitch) * np.sin(yaw), sy * -np.sin(pitch), -np.cos(pitch) * np.cos(yaw)], axis=1)
    return v / np.linalg.norm(v, axis=1, keepdims=True)
