"""シーン1 (Record Raw Mkv) で記録した生データ(MKV)から、画像で骨格・頭の向き・目の視線を推定する.

深度は使わず、2台以上のカメラに写った人を三角測量して3次元にする(1台にしか写っていない人は出力しない).

使い方 (Tools/GazePipeline で):
  .venv\\Scripts\\python.exe run_pipeline.py --experiment sansoken1 --subject Yoshi

入力: Assets/Data/HomeExperiment/<実験>/Skeleton/<対象者>/Raw~/<ID>.mkv, <ID>_raw_index.json, <ID>_calibration.json
出力: Assets/Data/HomeExperiment/<実験>/Skeleton/<対象者>/Filtered/filtered_Image.json
      (シーン3の filtered_*.json と同じ形式 + 頭の位置・頭の向き・視線. シーン4で Skeleton Source を Image にすると使える)
途中結果: Raw~/<ID>_image_pose.pkl (まとめ方だけ変えて作り直すときは --reuse で画像の解析を飛ばせる)
"""

from __future__ import annotations

import argparse
import sys
import time
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[2]
DATA_ROOT = PROJECT_ROOT / "Assets" / "Data" / "HomeExperiment"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--experiment", required=True)
    parser.add_argument("--subject", required=True)
    parser.add_argument("--kinects", nargs="*", help="解析するキネクト(省略時は Raw~ にある全部)")
    parser.add_argument("--step", type=int, default=1, help="何フレームごとに解析するか(2 なら 15fps. 速くなる)")
    parser.add_argument("--reuse", action="store_true", help="保存済みの画像解析の結果があれば使う")
    parser.add_argument("--min-views", type=int, default=2, help="何台以上のカメラに写った人を使うか(2以上)")
    parser.add_argument("--output-name", default="Image", help="filtered_<名前>.json の名前")
    args = parser.parse_args()

    import torch  # noqa: F401  CUDA の DLL を onnxruntime より先に読み込む
    from gazepipe.fuse import Settings, fuse, write_json
    from gazepipe.kinect import RawRecording
    from gazepipe.process import load_cache, process_recording, save_cache

    subject_dir = DATA_ROOT / args.experiment / "Skeleton" / args.subject
    raw_dir = subject_dir / "Raw~"
    if not raw_dir.exists():
        print(f"生データのフォルダがありません: {raw_dir}")
        return 1

    kinect_ids = args.kinects or sorted(p.name[:-len("_raw_index.json")] for p in raw_dir.glob("*_raw_index.json"))
    if not kinect_ids:
        print(f"*_raw_index.json がありません: {raw_dir}")
        return 1

    per_kinect = {}
    shared = {}
    for kid in kinect_ids:
        rec = RawRecording.load(raw_dir, kid)
        frames = load_cache(rec, args.step) if args.reuse else None
        if frames is None:
            print(f"Kinect{kid} (serial {rec.serial}): {len(rec.index['frames'])} frames を解析します", flush=True)
            if not shared:
                from rtmlib import Wholebody
                from gazepipe.models import HeadPoseModel, L2CSGaze
                shared = dict(detector=Wholebody(mode="performance", backend="onnxruntime", device="cuda"),
                              head_model=HeadPoseModel(), gaze_model=L2CSGaze())
            started = time.time()
            frames = process_recording(rec, step=args.step, **shared)
            print(f"Kinect{kid}: {time.time() - started:.0f}s → {save_cache(rec, frames, args.step)}", flush=True)
        else:
            print(f"Kinect{kid}: 保存済みの画像解析の結果を使います")
        per_kinect[kid] = frames

    if len(per_kinect) < 2:
        print("三角測量には2台以上のキネクトの生データが必要です。")
        return 1
    settings = Settings(sample_interval=args.step / 30.0, min_views=max(2, args.min_views))
    output = fuse(per_kinect, settings)
    out_path = subject_dir / "Filtered" / f"filtered_{args.output_name}.json"
    stats = write_json(out_path, output, per_kinect, settings, args.experiment, args.subject)

    print(f"保存しました: {out_path}")
    print(f"  {stats['samples']} samples, 人物 {stats['persons']} 件 (トラック {stats['tracks']} 個), 目の視線あり {stats['with_gaze']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
