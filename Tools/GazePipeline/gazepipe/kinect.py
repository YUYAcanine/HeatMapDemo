"""Azure Kinect の校正情報・MKV・座標変換.

座標系
------
- 深度カメラ座標 / カラーカメラ座標: Azure Kinect の座標 (mm, X右 Y下 Z前, 右手系)
- 部屋座標: Unity の座標 (m, Y上, 左手系). シーン0で合わせたキネクトの位置姿勢で決まる.
  room = depthToRoom @ (x_mm, y_mm, z_mm, 1)   (depthToRoom は <ID>_raw_index.json に入っている)

校正情報 (<ID>_calibration.json) は Azure Kinect SDK の k4a_calibration_t と同じ計算で
モードごとのカメラ行列・歪み係数に直す (transformation_get_mode_specific_camera_calibration).
"""

from __future__ import annotations

import io
import json
from dataclasses import dataclass
from pathlib import Path

import av
import cv2
import numpy as np
from PIL import Image

# モードごとの (校正画像を縮小したときの解像度, 切り出しの左上, 出力解像度)
_DEPTH_MODES = {
    "NFOV_2x2Binned": ((512, 512), (96, 112), (320, 288)),
    "NFOV_Unbinned": ((1024, 1024), (192, 224), (640, 576)),
    "WFOV_2x2Binned": ((512, 512), (0, 0), (512, 512)),
    "WFOV_Unbinned": ((1024, 1024), (0, 0), (1024, 1024)),
}

_COLOR_WIDTHS = {"R720p": 1280, "R1080p": 1920, "R1440p": 2560, "R1536p": 2048, "R2160p": 3840, "R3072p": 4096}


@dataclass
class CameraModel:
    width: int
    height: int
    K: np.ndarray  # 3x3
    dist: np.ndarray  # OpenCV の rational モデル [k1, k2, p1, p2, k3, k4, k5, k6]

    def project(self, points_mm: np.ndarray) -> np.ndarray:
        """カメラ座標 (N,3) mm → 画素 (N,2)."""
        points = np.asarray(points_mm, dtype=np.float64).reshape(-1, 1, 3)
        pixels, _ = cv2.projectPoints(points, np.zeros(3), np.zeros(3), self.K, self.dist)
        return pixels.reshape(-1, 2)

    def unproject_rays(self, pixels: np.ndarray) -> np.ndarray:
        """画素 (N,2) → z=1 の光線 (N,3). 歪みを取り除く."""
        pts = np.asarray(pixels, dtype=np.float64).reshape(-1, 1, 2)
        undist = cv2.undistortPointsIter(
            pts, self.K, self.dist, None, None, (cv2.TERM_CRITERIA_COUNT | cv2.TERM_CRITERIA_EPS, 20, 1e-6))
        rays = np.concatenate([undist.reshape(-1, 2), np.ones((len(pts), 1))], axis=1)
        return rays


def _camera_model(raw: dict, binned: tuple, crop: tuple, output: tuple) -> CameraModel:
    p = raw["Intrinsics"]["ModelParameters"]
    cx_n, cy_n, fx_n, fy_n, k1, k2, k3, k4, k5, k6, _codx, _cody, p2, p1 = p[:14]
    fx, fy = fx_n * binned[0], fy_n * binned[1]
    cx = cx_n * binned[0] - crop[0] - 0.5
    cy = cy_n * binned[1] - crop[1] - 0.5
    K = np.array([[fx, 0, cx], [0, fy, cy], [0, 0, 1]], dtype=np.float64)
    dist = np.array([k1, k2, p1, p2, k3, k4, k5, k6], dtype=np.float64)
    return CameraModel(output[0], output[1], K, dist)


@dataclass
class KinectCalibration:
    depth: CameraModel
    color: CameraModel
    # 深度カメラ座標 → カラーカメラ座標 (mm): p_color = R @ p_depth + t
    R_depth_to_color: np.ndarray
    t_depth_to_color_mm: np.ndarray

    @staticmethod
    def load(path: Path, depth_mode: str, color_resolution: str) -> "KinectCalibration":
        data = json.loads(Path(path).read_text(encoding="utf-8"))["CalibrationInformation"]
        cams = {c["Location"]: c for c in data["Cameras"]}
        raw_depth = cams["CALIBRATION_CameraLocationD0"]
        raw_color = cams["CALIBRATION_CameraLocationPV0"]

        depth = _camera_model(raw_depth, *_DEPTH_MODES[depth_mode])

        width = _COLOR_WIDTHS[color_resolution]
        if color_resolution == "R3072p":
            binned, crop, output = (4096, 3072), (0, 0), (4096, 3072)
        elif color_resolution == "R1536p":  # 4:3
            binned, crop, output = (2048, 1536), (0, 0), (2048, 1536)
        else:  # 16:9 は 4:3 のセンサーの上下を切り出す
            height = width * 9 // 16
            binned, crop, output = (width, width * 3 // 4), (0, (width * 3 // 4 - height) // 2), (width, height)
        color = _camera_model(raw_color, binned, crop, output)

        # Rt は「深度カメラ → そのカメラ」(深度カメラ自身は単位行列). Translation は m
        R = np.array(raw_color["Rt"]["Rotation"], dtype=np.float64).reshape(3, 3)
        t = np.array(raw_color["Rt"]["Translation"], dtype=np.float64) * 1000.0
        return KinectCalibration(depth, color, R, t)

    def depth_to_color(self, points_mm: np.ndarray) -> np.ndarray:
        return points_mm @ self.R_depth_to_color.T + self.t_depth_to_color_mm

    def color_to_depth(self, points_mm: np.ndarray) -> np.ndarray:
        return (points_mm - self.t_depth_to_color_mm) @ self.R_depth_to_color


def decode_jpeg(data: bytes) -> np.ndarray | None:
    """MJPG の1フレーム → BGR. 途中で切れている(USB の帯域不足などで壊れた)フレームは None.

    OpenCV は壊れたフレームも警告だけで灰色の帯の入った画像にしてしまうので、PIL で厳密に読む.
    """
    try:
        with Image.open(io.BytesIO(data)) as im:
            rgb = np.asarray(im.convert("RGB"))
    except (OSError, SyntaxError, ValueError):
        return None
    return np.ascontiguousarray(rgb[..., ::-1])


@dataclass
class RawRecording:
    """シーン1 (Record Raw Mkv) で記録したキネクト1台分 (A.mkv / A_raw_index.json / A_calibration.json)."""

    kinect_id: str
    raw_dir: Path
    index: dict
    calibration: KinectCalibration
    depth_to_room: np.ndarray  # 4x4

    @staticmethod
    def load(raw_dir: Path, kinect_id: str) -> "RawRecording":
        raw_dir = Path(raw_dir)
        index = json.loads((raw_dir / f"{kinect_id}_raw_index.json").read_text(encoding="utf-8"))
        calib = KinectCalibration.load(raw_dir / index["calibrationFile"], index["depthMode"], index["colorResolution"])
        return RawRecording(kinect_id, raw_dir, index, calib, np.array(index["depthToRoom"], dtype=np.float64).reshape(4, 4))

    @property
    def mkv_path(self) -> Path:
        return self.raw_dir / self.index["mkvFile"]

    @property
    def serial(self) -> str:
        return self.index.get("serialNumber", "")

    # ---- 座標変換 ----
    def depth_to_room_points(self, points_mm: np.ndarray) -> np.ndarray:
        p = np.asarray(points_mm, dtype=np.float64)
        return p @ self.depth_to_room[:3, :3].T + self.depth_to_room[:3, 3]

    def room_to_depth_points(self, points_room: np.ndarray) -> np.ndarray:
        inv = np.linalg.inv(self.depth_to_room)
        p = np.asarray(points_room, dtype=np.float64)
        return p @ inv[:3, :3].T + inv[:3, 3]

    def color_dir_to_room(self, directions: np.ndarray) -> np.ndarray:
        """カラーカメラ座標の向き (N,3) → 部屋座標の向き (正規化)."""
        d = np.asarray(directions, dtype=np.float64) @ self.calibration.R_depth_to_color  # color→depth の回転
        d = d @ self.depth_to_room[:3, :3].T
        return d / np.linalg.norm(d, axis=-1, keepdims=True)

    def color_camera_position_room(self) -> np.ndarray:
        origin_depth = self.calibration.color_to_depth(np.zeros((1, 3)))
        return self.depth_to_room_points(origin_depth)[0]

    # ---- フレームの読み込み ----
    def frames(self, start: int = 0, stop: int | None = None, step: int = 1, decode_color: bool = True,
               read_depth: bool = True):
        """(index, recordingTimeSec, color BGR or None, depth uint16 or None) を順に返す.

        raw_index の frames[i] と MKV の i 番目のキャプチャは同じ順番 (MKV にはこのキャプチャだけを書いている).
        """
        frames_info = self.index["frames"]
        container = av.open(str(self.mkv_path))
        start_offset_us = int(container.metadata.get("K4A_START_OFFSET_NS", "0")) // 1000
        streams = {s.metadata.get("title"): s for s in container.streams if s.type == "video"}
        color_stream = streams.get("COLOR")
        depth_stream = streams.get("DEPTH") if read_depth else None

        # デバイスのタイムスタンプ(μs) → フレーム番号
        by_color = {f["colorTimestampUsec"]: f for f in frames_info if f["colorTimestampUsec"] >= 0}
        by_depth = {f["depthTimestampUsec"]: f for f in frames_info if f["depthTimestampUsec"] >= 0}

        pending: dict[int, dict] = {}
        wanted = set(range(start, len(frames_info) if stop is None else min(stop, len(frames_info)), step))
        if not wanted:
            return

        demux_streams = [s for s in (color_stream, depth_stream) if s is not None]
        for packet in container.demux(*demux_streams):
            if packet.pts is None:
                continue
            ts = int(packet.pts * packet.time_base * 1_000_000 + 0.5) + start_offset_us
            info = by_color.get(ts) if packet.stream is color_stream else by_depth.get(ts)
            if info is None:
                # 端数の丸めで 1μs ずれることがある
                table = by_color if packet.stream is color_stream else by_depth
                info = table.get(ts - 1) or table.get(ts + 1)
            if info is None or info["index"] not in wanted:
                continue

            entry = pending.setdefault(info["index"], {"info": info})
            if packet.stream is color_stream:
                entry["color"] = decode_jpeg(bytes(packet)) if decode_color else None
            else:
                # b16g は big-endian で書かれている
                arr = np.frombuffer(bytes(packet), dtype=">u2").reshape(
                    self.calibration.depth.height, self.calibration.depth.width)
                entry["depth"] = arr.astype(np.uint16)

            need_color = color_stream is not None and info["colorTimestampUsec"] >= 0
            need_depth = depth_stream is not None and info["depthTimestampUsec"] >= 0
            if (not need_color or "color" in entry) and (not need_depth or "depth" in entry):
                del pending[info["index"]]
                yield info["index"], info["recordingTimeSec"], entry.get("color"), entry.get("depth")

        container.close()
