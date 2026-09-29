"""シーン1 (Record Raw Mkv) で記録した生データ(MKV)から、キネクトの骨格推定 (Azure Kinect Body Tracking SDK) で
骨格データ <ID>_skeleton.json を作る (記録が終わった後に行う).

記録中に骨格推定を動かすと、Unity の描画・深度計算と GPU を取り合い、Body Tracking SDK (k4abt 1.1.2) の不具合で
Unity ごと落ちることがあるため、記録は映像だけにして (シーン1で Record Skeleton をオフ・Record Raw Mkv をオン)、
骨格はこのスクリプトで後から作る. 別のプログラムとして動くので、万一落ちても Unity や記録には影響しない (やり直せばよい).

出力はシーン1で記録していた <ID>_skeleton.json と同じ形式・同じ時間軸 (recordingTimeSec. <ID>_raw_index.json と同じ) で、
シーン2 (再生) / シーン3 (フィルタリング) / シーン4 (解析) でそのまま使える.
骨格は部屋座標 (シーン0で合わせたキネクトの位置姿勢. raw_index の depthToRoom) に変換して保存する.

使い方 (Tools/GazePipeline で):
  .venv\\Scripts\\python.exe run_body_tracking.py
      実験の名前・実験対象者を一覧から番号で選ぶ
  .venv\\Scripts\\python.exe run_body_tracking.py --experiment 0928sansouken --subject test1 --kinects B C

必要なもの: Azure Kinect Body Tracking SDK 1.1.x のインストール (C:\\Program Files\\Azure Kinect Body Tracking SDK).
その tools フォルダにある k4a.dll / k4arecord.dll / k4abt.dll / 骨格推定モデル / CUDA の DLL を使う.
"""

from __future__ import annotations

import argparse
import ctypes
import datetime
import json
import os
import sys
import time
from ctypes import POINTER, Structure, byref, c_char_p, c_float, c_int, c_int32, c_uint32, c_uint64, c_void_p
from pathlib import Path

from run_pipeline import DATA_ROOT, _choose, _list_experiments, _list_subjects

DEFAULT_SDK_TOOLS = Path(r"C:\Program Files\Azure Kinect Body Tracking SDK\tools")

# Microsoft.Azure.Kinect.BodyTracking.JointId の名前 (k4abt_joint_id_t の順). シーン1の記録と同じ文字列で保存する
JOINT_NAMES = [
    "Pelvis", "SpineNavel", "SpineChest", "Neck",
    "ClavicleLeft", "ShoulderLeft", "ElbowLeft", "WristLeft", "HandLeft", "HandTipLeft", "ThumbLeft",
    "ClavicleRight", "ShoulderRight", "ElbowRight", "WristRight", "HandRight", "HandTipRight", "ThumbRight",
    "HipLeft", "KneeLeft", "AnkleLeft", "FootLeft",
    "HipRight", "KneeRight", "AnkleRight", "FootRight",
    "Head", "Nose", "EyeLeft", "EarLeft", "EyeRight", "EarRight",
]

PROCESSING_MODES = {"cuda": 2, "directml": 4, "cpu": 1}  # k4abt_tracker_processing_mode_t

K4A_WAIT_INFINITE = -1
K4A_RESULT_SUCCEEDED = 0
K4A_WAIT_RESULT_SUCCEEDED = 0
K4A_WAIT_RESULT_TIMEOUT = 2
K4A_STREAM_RESULT_SUCCEEDED = 0
K4A_STREAM_RESULT_EOF = 2


class TrackerConfiguration(Structure):  # k4abt_tracker_configuration_t
    _fields_ = [("sensor_orientation", c_int), ("processing_mode", c_int),
                ("gpu_device_id", c_int32), ("model_path", c_char_p)]


class Joint(Structure):  # k4abt_joint_t
    _fields_ = [("position", c_float * 3), ("orientation", c_float * 4), ("confidence_level", c_int)]


class Skeleton(Structure):  # k4abt_skeleton_t
    _fields_ = [("joints", Joint * len(JOINT_NAMES))]


class KinectSdk:
    """k4a / k4arecord / k4abt の必要な関数だけを ctypes で読み込む."""

    def __init__(self, tools_dir: Path):
        if not (tools_dir / "k4abt.dll").exists():
            raise FileNotFoundError(f"Body Tracking SDK の tools フォルダが見つかりません: {tools_dir}")
        # onnxruntime が CUDA / DirectML の DLL を探せるように、tools フォルダを先頭にする
        os.environ["PATH"] = str(tools_dir) + os.pathsep + os.environ.get("PATH", "")
        os.add_dll_directory(str(tools_dir))

        self.k4a = ctypes.CDLL(str(tools_dir / "k4a.dll"))
        self.rec = ctypes.CDLL(str(tools_dir / "k4arecord.dll"))
        self.bt = ctypes.CDLL(str(tools_dir / "k4abt.dll"))
        self.tools_dir = tools_dir

        def fn(lib, name, restype, *argtypes):
            f = getattr(lib, name)
            f.restype = restype
            f.argtypes = list(argtypes)
            return f

        self.playback_open = fn(self.rec, "k4a_playback_open", c_int, c_char_p, POINTER(c_void_p))
        self.playback_close = fn(self.rec, "k4a_playback_close", None, c_void_p)
        # k4a_calibration_t (1032 バイト) はポインタで受け渡すだけなので、十分な大きさのバッファを使う
        self.playback_get_calibration = fn(self.rec, "k4a_playback_get_calibration", c_int, c_void_p, c_void_p)
        self.playback_get_next_capture = fn(self.rec, "k4a_playback_get_next_capture", c_int, c_void_p, POINTER(c_void_p))

        self.capture_release = fn(self.k4a, "k4a_capture_release", None, c_void_p)
        self.capture_get_depth_image = fn(self.k4a, "k4a_capture_get_depth_image", c_void_p, c_void_p)
        self.capture_get_ir_image = fn(self.k4a, "k4a_capture_get_ir_image", c_void_p, c_void_p)
        self.image_release = fn(self.k4a, "k4a_image_release", None, c_void_p)

        self.tracker_create = fn(self.bt, "k4abt_tracker_create", c_int, c_void_p, TrackerConfiguration, POINTER(c_void_p))
        self.tracker_destroy = fn(self.bt, "k4abt_tracker_destroy", None, c_void_p)
        self.tracker_shutdown = fn(self.bt, "k4abt_tracker_shutdown", None, c_void_p)
        self.tracker_enqueue = fn(self.bt, "k4abt_tracker_enqueue_capture", c_int, c_void_p, c_void_p, c_int32)
        self.tracker_pop = fn(self.bt, "k4abt_tracker_pop_result", c_int, c_void_p, POINTER(c_void_p), c_int32)
        self.frame_release = fn(self.bt, "k4abt_frame_release", None, c_void_p)
        self.frame_num_bodies = fn(self.bt, "k4abt_frame_get_num_bodies", c_uint32, c_void_p)
        self.frame_body_skeleton = fn(self.bt, "k4abt_frame_get_body_skeleton", c_int, c_void_p, c_uint32, POINTER(Skeleton))
        self.frame_body_id = fn(self.bt, "k4abt_frame_get_body_id", c_uint32, c_void_p, c_uint32)
        self.frame_timestamp = fn(self.bt, "k4abt_frame_get_device_timestamp_usec", c_uint64, c_void_p)


def _to_room(m: list[float], p) -> dict:
    """深度カメラ座標 (mm) → 部屋座標 (m). m は raw_index の depthToRoom (4x4, 行優先)."""
    x, y, z = p[0], p[1], p[2]
    return {"x": m[0] * x + m[1] * y + m[2] * z + m[3],
            "y": m[4] * x + m[5] * y + m[6] * z + m[7],
            "z": m[8] * x + m[9] * y + m[10] * z + m[11]}


def track_kinect(sdk: KinectSdk, raw_dir: Path, kinect_id: str, mode: str, gpu_id: int, model: Path,
                 max_frames: int | None) -> dict:
    index = json.loads((raw_dir / f"{kinect_id}_raw_index.json").read_text(encoding="utf-8"))
    depth_to_room = index["depthToRoom"]
    # 骨格の時刻 = 元になった深度のタイムスタンプ. raw_index と同じ recordingTimeSec にする
    time_by_depth = {f["depthTimestampUsec"]: f["recordingTimeSec"] for f in index["frames"] if f["depthTimestampUsec"] >= 0}
    total = len(index["frames"])

    playback = c_void_p()
    mkv = str(raw_dir / index["mkvFile"])
    if sdk.playback_open(mkv.encode("mbcs"), byref(playback)) != K4A_RESULT_SUCCEEDED:
        raise RuntimeError(f"MKV を開けませんでした: {mkv}")

    tracker = c_void_p()
    frames: list[dict] = []
    first_timestamp = None
    unmatched = 0
    popped = 0

    def drain(timeout_ms: int) -> bool:
        """結果を取り出す. 1つ以上取れたら True."""
        nonlocal first_timestamp, unmatched, popped
        got = False
        while True:
            body_frame = c_void_p()
            result = sdk.tracker_pop(tracker, byref(body_frame), timeout_ms)
            if result != K4A_WAIT_RESULT_SUCCEEDED:
                return got
            got = True
            popped += 1
            timeout_ms = 0  # 1つ取れたら、残りは待たずに取り出す
            try:
                usec = sdk.frame_timestamp(body_frame)
                rec_time = time_by_depth.get(usec, time_by_depth.get(usec - 1, time_by_depth.get(usec + 1)))
                if rec_time is None:
                    unmatched += 1
                    continue
                if first_timestamp is None:
                    first_timestamp = usec
                bodies = []
                for b in range(sdk.frame_num_bodies(body_frame)):
                    skeleton = Skeleton()
                    if sdk.frame_body_skeleton(body_frame, b, byref(skeleton)) != K4A_RESULT_SUCCEEDED:
                        continue
                    bodies.append({
                        "bodyId": int(sdk.frame_body_id(body_frame, b)),
                        "joints": [{"jointId": name,
                                    "position": _to_room(depth_to_room, skeleton.joints[j].position),
                                    "confidence": int(skeleton.joints[j].confidence_level)}
                                   for j, name in enumerate(JOINT_NAMES)],
                    })
                frames.append({
                    "deviceTimestampTicks": usec * 10,
                    "normalizedTimestampTicks": (usec - first_timestamp) * 10,
                    "unityTime": float(rec_time),
                    "recordingTimeSec": float(rec_time),
                    "bodies": bodies,
                })
            finally:
                sdk.frame_release(body_frame)

    try:
        calibration = ctypes.create_string_buffer(4096)
        if sdk.playback_get_calibration(playback, calibration) != K4A_RESULT_SUCCEEDED:
            raise RuntimeError("MKV から校正情報を読めませんでした")

        print(f"  骨格推定のモデルを読み込んでいます ({mode}) ...", flush=True)
        created = time.time()
        config = TrackerConfiguration(0, PROCESSING_MODES[mode], gpu_id, str(model).encode("mbcs"))
        if sdk.tracker_create(calibration, config, byref(tracker)) != K4A_RESULT_SUCCEEDED:
            raise RuntimeError(f"骨格推定を開始できませんでした (mode={mode}, gpu={gpu_id}). --mode を変えて試してください")
        print(f"  読み込み完了 ({time.time() - created:.0f}s)", flush=True)

        started = time.time()
        n = 0
        while max_frames is None or n < max_frames:
            capture = c_void_p()
            result = sdk.playback_get_next_capture(playback, byref(capture))
            if result == K4A_STREAM_RESULT_EOF:
                break
            if result != K4A_STREAM_RESULT_SUCCEEDED:
                raise RuntimeError("MKV の読み込みに失敗しました")
            try:
                depth = sdk.capture_get_depth_image(capture)
                ir = sdk.capture_get_ir_image(capture)
                has_images = bool(depth) and bool(ir)
                if depth:
                    sdk.image_release(depth)
                if ir:
                    sdk.image_release(ir)
                if not has_images:
                    continue  # 骨格推定には深度と赤外線の両方が要る
                # 入力の枠が一杯なら、結果を取り出して枠を空けてから渡し直す
                # (結果を取り出さずに待ち続けると、出力の枠も一杯のまま止まってしまう)
                while True:
                    result = sdk.tracker_enqueue(tracker, capture, 0)
                    if result == K4A_WAIT_RESULT_SUCCEEDED:
                        break
                    if result != K4A_WAIT_RESULT_TIMEOUT:
                        raise RuntimeError("骨格推定にフレームを渡せませんでした")
                    drain(50)
            finally:
                sdk.capture_release(capture)
            n += 1
            drain(0)
            if n % 100 == 0:
                print(f"  Kinect{kinect_id}: {n}/{total} frames ({n / (time.time() - started):.1f} fps)", flush=True)

        # 渡したフレームの結果がすべて出るまで取り出す
        # (shutdown の後に無期限で待つと、この SDK では戻ってこないことがあるので、先に取り出し切ってから shutdown する)
        waited = 0.0
        while popped < n and waited < 30.0:
            if drain(1000):
                waited = 0.0
            else:
                waited += 1.0
        if popped < n:
            print(f"  注意: {n - popped} フレームの結果が出ませんでした")
        sdk.tracker_shutdown(tracker)
    finally:
        if tracker:
            sdk.tracker_destroy(tracker)
        sdk.playback_close(playback)

    frames.sort(key=lambda f: f["recordingTimeSec"])
    if unmatched:
        print(f"  注意: raw_index に無いタイムスタンプの結果が {unmatched} 件ありました (使いません)")

    return {
        "experimentName": index.get("experimentName", ""),
        "subjectName": index.get("subjectName", ""),
        "kinectId": kinect_id,
        "deviceIndex": index.get("deviceIndex", -1),
        "recordingStartedAt": index.get("recordingStartedAt", ""),
        "frames": frames,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--experiment", help="実験の名前 (省略すると一覧から選ぶ)")
    parser.add_argument("--subject", help="実験対象者 (省略すると一覧から選ぶ)")
    parser.add_argument("--kinects", nargs="*", help="骨格を作るキネクト (省略時は Raw~ にある全部)")
    parser.add_argument("--mode", choices=list(PROCESSING_MODES), default="cuda",
                        help="骨格推定を動かす方法. cuda (既定, NVIDIA GPU) / directml / cpu (とても遅い)")
    parser.add_argument("--gpu-id", type=int, default=0,
                        help="使う GPU の番号. cuda では NVIDIA GPU の番号, directml では Windows の GPU の列挙順")
    parser.add_argument("--lite", action="store_true", help="軽い骨格推定モデルを使う (速いが精度が少し落ちる)")
    parser.add_argument("--sdk-tools", type=Path, default=DEFAULT_SDK_TOOLS, help="Body Tracking SDK の tools フォルダ")
    parser.add_argument("--max-frames", type=int, help="確認用: 先頭の何フレームだけ処理するか")
    parser.add_argument("--output-suffix", default="",
                        help="確認用: <ID>_skeleton<これ>.json に書く (例 _test. シーン2〜4 は読み込まない)")
    args = parser.parse_args()

    if not args.experiment:
        args.experiment = _choose("実験の名前", _list_experiments())
        if args.experiment is None:
            return 1
    if not args.subject:
        args.subject = _choose(f"実験対象者 ({args.experiment})", _list_subjects(args.experiment))
        if args.subject is None:
            return 1

    subject_dir = DATA_ROOT / args.experiment / "Skeleton" / args.subject
    raw_dir = subject_dir / "Raw~"
    kinect_ids = args.kinects or sorted(p.name[:-len("_raw_index.json")] for p in raw_dir.glob("*_raw_index.json"))
    if not kinect_ids:
        print(f"*_raw_index.json がありません: {raw_dir}")
        return 1

    if args.lite:
        model = args.sdk_tools.parent / "sdk" / "windows-desktop" / "amd64" / "release" / "bin" / "dnn_model_2_0_lite_op11.onnx"
    else:
        model = args.sdk_tools / "dnn_model_2_0_op11.onnx"
    if not model.exists():
        print(f"骨格推定のモデルがありません: {model}")
        return 1

    print(f"\n{args.experiment} / {args.subject} の骨格を作ります (キネクト {', '.join(kinect_ids)}, {args.mode})\n", flush=True)
    sdk = KinectSdk(args.sdk_tools)
    backup_suffix = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")

    for kid in kinect_ids:
        print(f"Kinect{kid}: 骨格推定を始めます", flush=True)
        started = time.time()
        data = track_kinect(sdk, raw_dir, kid, args.mode, args.gpu_id, model, args.max_frames)

        out = subject_dir / f"{kid}_skeleton{args.output_suffix}.json"
        if out.exists():
            # シーン1と同じく、前のデータは上書きせず名前を変えて残す
            backup = subject_dir / f"{kid}_skeleton_old_{backup_suffix}.json"
            out.rename(backup)
            print(f"  既存のファイルを退避しました: {backup.name}")
        out.write_text(json.dumps(data, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")

        with_body = sum(1 for f in data["frames"] if f["bodies"])
        print(f"Kinect{kid}: {time.time() - started:.0f}s, {len(data['frames'])} frames (人が写っている {with_body}) → {out}", flush=True)

    return 0


if __name__ == "__main__":
    sys.exit(main())
