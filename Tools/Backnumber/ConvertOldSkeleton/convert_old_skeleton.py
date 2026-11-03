"""以前の形式の骨格データ(skeleton.json)を、シーン2/3が読める <キネクトID>_skeleton.json に書き換える。

  Skeleton/<実験対象者>/skeleton.json
を読み、次のように書き換えて
  Skeleton/<実験対象者>/A_skeleton.json
に保存する。関節の位置・信頼度・時刻の値は変えない(形だけを変える)。
  frames[].joints                  → frames[].bodies[0].joints(bodyId = 1。関節が0個のフレームは bodies を空にする)
  normalizedTimestampTicks(元の値) → deviceTimestampTicks
  最初のフレームを0にした値        → normalizedTimestampTicks
  unityTime                        → unityTime / recordingTimeSec の両方
  experimentName / subjectName / kinectId / deviceIndex を追加する(recordingStartedAt は不明なので空)

元のデータは同じ時計なので、途中から切り出した記録(naoto2_latter など)は 0秒から始まらない。
light_log.json と時刻がそろうように、recordingTimeSec は 0秒にずらさない。

元の skeleton.json は Skeleton/<実験対象者>/Original~/ へ移す(Unityは「~」で終わるフォルダを読み込まない)。
Assets/Data の Skeleton は git に入らないので、消すと戻せない。
もう一度実行すると Original~/ の skeleton.json から作り直す。

元の形式には無いもの:
  - 目と耳の関節(28関節)。シーン4以降の Head Direction Method は HeadToNose で使う
  - 2人目の骨格(1フレームに骨格が1つだけ)。シーン6は子供と親の両方が必要なので使えない

使い方:
  python Tools/ConvertOldSkeleton/convert_old_skeleton.py <実験の名前> [<実験対象者> ...]
  (実験対象者を省くと、skeleton.json がある実験対象者をすべて変換する)
  --kinect-id B  … 書き出すファイルの ID(省略時は A)
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import home_data_root  # noqa: E402  Tools/home_data_root.py

OLD_FILE_NAME = "skeleton.json"
ORIGINAL_FOLDER_NAME = "Original~"
BODY_ID = 1


def convert(old: dict, experiment: str, subject: str, kinect_id: str) -> dict:
    frames = old.get("frames")

    if not frames or "joints" not in frames[0] or "bodies" in frames[0]:
        raise ValueError("以前の形式(frames[].joints)ではありません")

    first_ticks = frames[0]["normalizedTimestampTicks"]
    converted = []

    for frame in frames:
        ticks = frame["normalizedTimestampTicks"]
        joints = frame["joints"]
        converted.append({
            "deviceTimestampTicks": ticks,
            "normalizedTimestampTicks": ticks - first_ticks,
            "unityTime": frame["unityTime"],
            "recordingTimeSec": frame["unityTime"],
            "bodies": [{"bodyId": BODY_ID, "joints": joints}] if joints else [],
        })

    return {
        "experimentName": experiment,
        "subjectName": subject,
        "kinectId": kinect_id,
        "deviceIndex": 0,
        "recordingStartedAt": "",
        "frames": converted,
    }


def convert_subject(subject_dir: Path, experiment: str, kinect_id: str) -> None:
    original_dir = subject_dir / ORIGINAL_FOLDER_NAME
    old_path = subject_dir / OLD_FILE_NAME
    kept_path = original_dir / OLD_FILE_NAME
    source = kept_path if kept_path.exists() else old_path

    if not source.exists():
        print(f"{subject_dir.name}: {OLD_FILE_NAME} がないのでスキップします")
        return

    with open(source, encoding="utf-8") as file:
        old = json.load(file)

    data = convert(old, experiment, subject_dir.name, kinect_id)

    # 書き出しに失敗しても元のファイルが残るように、書き終えてから移す。
    # 途中のファイルは Original~ に置く(Unityに読み込ませない)
    original_dir.mkdir(exist_ok=True)
    temp_path = original_dir / f"{kinect_id}_skeleton.json.tmp"

    with open(temp_path, "w", encoding="utf-8") as file:
        json.dump(data, file, ensure_ascii=False, separators=(",", ":"))

    output_path = subject_dir / f"{kinect_id}_skeleton.json"
    temp_path.replace(output_path)

    if source == old_path:
        old_path.replace(kept_path)

        # GUID を保ったまま .meta も新しいファイルへ付け替える
        old_meta = old_path.with_name(OLD_FILE_NAME + ".meta")
        new_meta = output_path.with_name(output_path.name + ".meta")

        if old_meta.exists() and not new_meta.exists():
            old_meta.replace(new_meta)

    frames = data["frames"]
    bodies = sum(len(frame["bodies"]) for frame in frames)
    print(f"{subject_dir.name}: {len(frames)} frames (骨格あり {bodies}), "
          f"{frames[0]['recordingTimeSec']:.2f} - {frames[-1]['recordingTimeSec']:.2f} s → {output_path}")


def main() -> None:
    parser = argparse.ArgumentParser(description="以前の形式の骨格データを <キネクトID>_skeleton.json に書き換える")
    parser.add_argument("experiment", help="実験の名前")
    parser.add_argument("subjects", nargs="*", help="実験対象者(省略時は skeleton.json がある全員)")
    parser.add_argument("--kinect-id", default="A", help="書き出すファイルの ID(省略時は A)")
    home_data_root.add_argument(parser)
    args = parser.parse_args()

    data_root = home_data_root.choose_data_root(args.data_root)

    if data_root is None:
        return

    skeleton_root = data_root / args.experiment / "Skeleton"

    if args.subjects:
        subject_dirs = [skeleton_root / subject for subject in args.subjects]
    else:
        subject_dirs = sorted(
            path for path in skeleton_root.iterdir()
            if (path / OLD_FILE_NAME).exists() or (path / ORIGINAL_FOLDER_NAME / OLD_FILE_NAME).exists())

    if not subject_dirs:
        sys.exit(f"{OLD_FILE_NAME} がある実験対象者が見つかりません: {skeleton_root}")

    for subject_dir in subject_dirs:
        convert_subject(subject_dir, args.experiment, args.kinect_id)


if __name__ == "__main__":
    main()
