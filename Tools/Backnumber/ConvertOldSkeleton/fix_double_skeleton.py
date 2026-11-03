"""1本の骨格データに混ざった2重の骨格(2台のキネクトの位置合わせのズレ)をそろえた、別の実験対象者を作る。

  Skeleton/<実験対象者>/A_skeleton.json
を読み、ズレた側のフレームの全関節に剛体変換を掛けて
  Skeleton/<実験対象者>_fix/A_skeleton.json
  Skeleton/<実験対象者>_fix/fix_params.json … 使った変換と、そろえた結果
に保存する。light_log.json もコピーする。元の実験対象者のフォルダは変更しない。

以前の形式の骨格は、複数のキネクトの骨格を1本にまとめたものなので、位置合わせのズレた2台が
混ざっていると、同じ人の骨格が2か所を行き来する(sou1 では約12度の回転 + 約0.3mの平行移動)。
半分近くのフレームがズレた側なので、消さずにそろえる。

そろえ方:
  1. Viterbi で各姿勢を2つの座標系(0 / 1)に振り分ける。そろえた後の腰の移動が最も短くなる並びを選ぶ
  2. 2つの座標系が切り替わる前後の体幹の関節から、1→0 の剛体変換を求める(Kabsch法)
  3. 1 と 2 を、振り分けが変わらなくなるまで繰り返す
  4. --keep-side の座標系を正しいものとし、もう片方に変換を掛ける

時刻・信頼度・骨格が無いフレームは変えない。正しい側のフレームの座標も変えない。

どちらの座標系が正しいかは、自動では決めない。sou1 は部屋メッシュと比べて larger-x を選んだ
(larger-x では腰と頭が家具や壁の中に入るフレームが無く、高さ0.37mの物の上に座る姿勢になる。
smaller-x では約11%のフレームが家具の中に入る)。足の高さだけで選ぶと逆の結果になる。

使い方:
  python Tools/ConvertOldSkeleton/fix_double_skeleton.py <実験の名前> <実験対象者>
  --keep-side smaller-x   … 正しい座標系。2か所のうち x が大きい側 / 小さい側(省略時は larger-x)
  --initial-offset X Y Z  … 2か所の差(m)の最初の見当。x が大きい側 → 小さい側(省略時は sou1 の値)
numpy が必要(Tools/GazePipeline の .venv にも入っている)。
"""

from __future__ import annotations

import argparse
import json
import shutil
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import home_data_root  # noqa: E402  Tools/home_data_root.py

SKELETON_FILE_NAME = "A_skeleton.json"
LIGHT_LOG_FILE_NAME = "light_log.json"
FIX_SUFFIX = "_fix"

# 変換を求めるのに使う関節(手足は動きが大きいので使わない)
CORE_JOINTS = ("Pelvis", "SpineNavel", "SpineChest", "Neck", "HipLeft", "HipRight", "ShoulderLeft", "ShoulderRight")
SWITCH_PENALTY = 0.05
# 切り替わりの前後の時間差がこれより長い組は、変換を求めるのに使わない(秒)
MAX_PAIR_GAP = 0.2
MAX_ITERATIONS = 10
DEFAULT_INITIAL_OFFSET = (-0.39, 0.08, 0.24)


def label_poses(pelvis: np.ndarray, rotation: np.ndarray, translation: np.ndarray) -> np.ndarray:
    """各姿勢が 0 / 1 のどちらの座標系かを返す。0 の座標系へそろえたときの腰の移動が最小になる並びを選ぶ"""
    aligned = np.stack([pelvis, pelvis @ rotation.T + translation])
    count = len(pelvis)
    cost = np.zeros(2)
    previous = np.zeros((count, 2), dtype=int)

    for i in range(1, count):
        new_cost = np.zeros(2)

        for state in range(2):
            options = [cost[other] + np.linalg.norm(aligned[state, i] - aligned[other, i - 1])
                       + (SWITCH_PENALTY if other != state else 0.0) for other in range(2)]
            previous[i, state] = int(np.argmin(options))
            new_cost[state] = min(options)

        cost = new_cost

    labels = np.zeros(count, dtype=int)
    labels[-1] = int(np.argmin(cost))

    for i in range(count - 1, 0, -1):
        labels[i - 1] = previous[i, labels[i]]

    return labels


def fit_rigid(source: np.ndarray, target: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """target ≈ source @ rotation.T + translation になる剛体変換を返す(Kabsch法)"""
    source_center = source.mean(axis=0)
    target_center = target.mean(axis=0)
    u, _, vt = np.linalg.svd((source - source_center).T @ (target - target_center))
    sign = np.sign(np.linalg.det(vt.T @ u.T))
    rotation = vt.T @ np.diag([1.0, 1.0, sign]) @ u.T
    return rotation, target_center - source_center @ rotation.T


def switch_pairs(poses: np.ndarray, times: np.ndarray, labels: np.ndarray, core: list[int]) -> tuple[np.ndarray, np.ndarray]:
    """座標系が切り替わる前後の体幹の関節を (1 の側, 0 の側) で返す"""
    source, target = [], []

    for i in np.flatnonzero(labels[1:] != labels[:-1]):
        if times[i + 1] - times[i] > MAX_PAIR_GAP:
            continue

        one, zero = (i + 1, i) if labels[i] == 0 else (i, i + 1)
        source.append(poses[one][core])
        target.append(poses[zero][core])

    if not source:
        sys.exit("2つの座標系の切り替わりが見つかりません。2重の骨格が無いか、--initial-offset が合っていません。")

    return np.concatenate(source), np.concatenate(target)


def min_foot_height(poses: np.ndarray, feet: list[int]) -> float:
    return float(np.median(poses[:, feet, 1].min(axis=1)))


def jump_count(pelvis: np.ndarray, distance: float) -> int:
    return int((np.linalg.norm(np.diff(pelvis, axis=0), axis=1) > distance).sum())


def fix(data: dict, initial_offset: tuple[float, float, float], keep_side: str) -> tuple[dict, dict]:
    frames = data["frames"]
    with_body = [i for i, frame in enumerate(frames) if frame["bodies"]]
    joint_names = [joint["jointId"] for joint in frames[with_body[0]]["bodies"][0]["joints"]]
    positions = np.array([[[joint["position"][axis] for axis in "xyz"]
                           for joint in frames[i]["bodies"][0]["joints"]] for i in with_body])

    # 30fps で記録されているが同じ姿勢が2〜3フレーム続くので、振り分けは変わった姿勢だけで行う
    pose_of_frame = np.zeros(len(with_body), dtype=int)
    unique = [0]

    for i in range(1, len(positions)):
        if not np.array_equal(positions[i], positions[unique[-1]]):
            unique.append(i)

        pose_of_frame[i] = len(unique) - 1

    poses = positions[unique]
    times = np.array([frames[with_body[i]]["recordingTimeSec"] for i in unique])
    pelvis = poses[:, joint_names.index("Pelvis")]
    core = [joint_names.index(name) for name in CORE_JOINTS]
    feet = [joint_names.index("FootLeft"), joint_names.index("FootRight")]

    rotation = np.eye(3)
    translation = np.array(initial_offset)
    labels = label_poses(pelvis, rotation, translation)

    for _ in range(MAX_ITERATIONS):
        rotation, translation = fit_rigid(*switch_pairs(poses, times, labels, core))
        new_labels = label_poses(pelvis, rotation, translation)
        converged = np.array_equal(new_labels, labels)
        labels = new_labels

        if converged:
            break

    source, target = switch_pairs(poses, times, labels, core)
    residual = np.linalg.norm(source @ rotation.T + translation - target, axis=1)

    # 座標系 1 は initial_offset の向きから x が大きい側。ここからは 0 = そのまま / 1 = 変換を掛ける側にする
    if keep_side == "larger-x":
        rotation, translation = rotation.T, -translation @ rotation
        labels = 1 - labels

    corrected = positions.copy()
    frame_labels = labels[pose_of_frame]
    corrected[frame_labels == 1] = corrected[frame_labels == 1] @ rotation.T + translation

    for index, frame_index in enumerate(with_body):
        for joint, position in zip(frames[frame_index]["bodies"][0]["joints"], corrected[index]):
            joint["position"] = {"x": float(position[0]), "y": float(position[1]), "z": float(position[2])}

    fixed_pelvis = pelvis.copy()
    fixed_pelvis[labels == 1] = fixed_pelvis[labels == 1] @ rotation.T + translation

    report = {
        "source": data["subjectName"],
        "rotationDeg": float(np.degrees(np.arccos(np.clip((np.trace(rotation) - 1) / 2, -1, 1)))),
        "rotation": rotation.round(6).tolist(),
        "translation": translation.round(6).tolist(),
        "residualMedianMeters": float(np.median(residual)),
        "correctedFrames": int((frame_labels == 1).sum()),
        "framesWithSkeleton": len(with_body),
        "coordinateSwitches": int((labels[1:] != labels[:-1]).sum()),
        "jumpsOver30cmBefore": jump_count(pelvis, 0.3),
        "jumpsOver30cmAfter": jump_count(fixed_pelvis, 0.3),
        "minFootHeightKeptSide": min_foot_height(poses[labels == 0], feet),
        "minFootHeightCorrectedSideBefore": min_foot_height(poses[labels == 1], feet),
        "minFootHeightCorrectedSideAfter": min_foot_height(poses[labels == 1] @ rotation.T + translation, feet),
        "keepSide": keep_side,
    }
    return data, report


def main() -> None:
    parser = argparse.ArgumentParser(description="2重の骨格をそろえた <実験対象者>_fix を作る")
    parser.add_argument("experiment", help="実験の名前")
    parser.add_argument("subject", help="実験対象者")
    parser.add_argument("--initial-offset", type=float, nargs=3, default=DEFAULT_INITIAL_OFFSET, metavar=("X", "Y", "Z"),
                        help="2か所の差(m)の最初の見当")
    parser.add_argument("--keep-side", choices=["larger-x", "smaller-x"], default="larger-x",
                        help="正しい座標系。2か所のうち x が大きい側 / 小さい側")
    home_data_root.add_argument(parser)
    args = parser.parse_args()

    data_root = home_data_root.choose_data_root(args.data_root)

    if data_root is None:
        return

    subject_dir = data_root / args.experiment / "Skeleton" / args.subject
    output_dir = subject_dir.with_name(args.subject + FIX_SUFFIX)

    with open(subject_dir / SKELETON_FILE_NAME, encoding="utf-8") as file:
        data = json.load(file)

    data, report = fix(data, tuple(args.initial_offset), args.keep_side)
    data["subjectName"] = output_dir.name

    output_dir.mkdir(exist_ok=True)

    with open(output_dir / SKELETON_FILE_NAME, "w", encoding="utf-8") as file:
        json.dump(data, file, ensure_ascii=False, separators=(",", ":"))

    with open(output_dir / "fix_params.json", "w", encoding="utf-8") as file:
        json.dump(report, file, ensure_ascii=False, indent=2)

    if (subject_dir / LIGHT_LOG_FILE_NAME).exists():
        shutil.copy2(subject_dir / LIGHT_LOG_FILE_NAME, output_dir / LIGHT_LOG_FILE_NAME)

    print(json.dumps(report, ensure_ascii=False, indent=2))
    print(f"→ {output_dir}")


if __name__ == "__main__":
    main()
