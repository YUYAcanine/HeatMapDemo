"""キネクトどうしのフレームの時刻をそろえる.

- recording (既定): <ID>_raw_index.json の recordingTimeSec をそのまま使う. 骨格データ (<ID>_skeleton.json) の
  recordingTimeSec と同じ時間軸なので、シーン2の骨格の再生と0秒・時間軸がそろう.
  同期ケーブルだけで合わせた記録 (Unity の Sync Cable, raw_index の timeBase が StreamStart) では、
  0秒は「カメラが動き出して最初に届いたフレーム」で、全キネクトで同じ瞬間になっている.
- device: キネクト本体のタイムスタンプを使い、基準のキネクト (MASTER、無ければ最初の1台) の recordingTimeSec に合わせる.
  (確認用. 本体の時計が個体ごとにずれていることがあるので、ふつうは recording を使う)
"""

from __future__ import annotations

import dataclasses

import numpy as np

from .kinect import RawRecording
from .process import FrameObservation


def describe_recording_time(recs: dict[str, RawRecording]) -> None:
    """recordingTimeSec の0秒の決め方を表示する (キネクトごとに違う場合は注意を出す)."""
    bases = {kid: rec.index.get("timeBase") or "RecordingStart(古い記録)" for kid, rec in recs.items()}
    print("時刻合わせ: recordingTimeSec (骨格データと同じ時間軸)")
    for kid, rec in recs.items():
        print(f"  Kinect{kid} ({rec.wired_sync_mode}): 0秒 = {bases[kid]}")
    if len(set(bases.values())) > 1:
        print("  注意: キネクトによって0秒の決め方が違います。時刻がずれている可能性があります。")


def _recording_minus_device(rec: RawRecording) -> float | None:
    """recordingTimeSec - 本体の時刻 (秒) の中央値."""
    diffs = []
    for info in rec.index["frames"]:
        device = rec.device_time_sec(info["index"])
        if device is not None:
            diffs.append(info["recordingTimeSec"] - device)
    return float(np.median(diffs)) if diffs else None


def align_to_device_clock(per_kinect: dict[str, list[FrameObservation]],
                          recs: dict[str, RawRecording]) -> dict[str, list[FrameObservation]]:
    """各フレームの time_sec を「本体の時刻 + 基準のキネクトの recordingTimeSec とのずれ」に置き換える."""
    reference = next((kid for kid, rec in recs.items() if rec.wired_sync_mode == "MASTER"), next(iter(recs)))
    offsets = {kid: _recording_minus_device(rec) for kid, rec in recs.items()}
    base = offsets[reference]
    if base is None:
        raise ValueError(f"Kinect{reference} の raw_index に本体のタイムスタンプがありません")

    print(f"時刻合わせ: キネクト本体の時刻 (基準 Kinect{reference})")
    for kid in recs:
        if offsets[kid] is not None:
            # 記録上の時刻のままだった場合のずれ (+ ならそのキネクトの記録上の時刻が基準より進んでいた)
            print(f"  Kinect{kid} ({recs[kid].wired_sync_mode}): 記録上の時刻のずれ {offsets[kid] - base:+.3f} 秒")

    aligned = {}
    for kid, frames in per_kinect.items():
        rec = recs[kid]
        result = []
        for frame in frames:
            device = rec.device_time_sec(frame.index)
            if device is not None:
                result.append(dataclasses.replace(frame, time_sec=device + base))
        aligned[kid] = result
    return aligned
