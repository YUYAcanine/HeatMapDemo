# GazePipeline

シーン1 (`1HeadDirRecording`) で **Record Raw Mkv にチェックを入れて**記録した生データ (MKV) から、
**Kinect の骨格推定も深度も使わずに**、2台以上のカメラのカラー画像を三角測量して
人の骨格・頭の向き・目の視線を推定し、シーン4 で使える `filtered_Image.json` を作る。

**対象の人は必ず2台以上のカメラに写るようにして記録する**(1台にしか写っていない時間は出力されない)。

## 使い方

```
cd Tools/GazePipeline
.venv\Scripts\python.exe run_pipeline.py --experiment sansoken1 --subject Yoshi
```

- 出力: `Assets/Data/HomeExperiment/<実験>/Skeleton/<対象者>/Filtered/filtered_Image.json`
- シーン4 で `Skeleton Source = Image` にすると使える (`Image Direction` で目の視線 / 頭の向きを選ぶ)
- `--step 2` … 2フレームごと(15fps)に解析して時間を半分にする
- `--reuse` … 画像の解析結果 (`Raw~/<ID>_image_pose.pkl`) を使い回して、まとめ方だけやり直す
- `--min-views 3` … 3台以上に写った人だけにする(キネクトが3台以上あるとき)
- 速さの目安: RTX 3080 Laptop で 1台 5〜7 fps (写っている人数による)。2台 × 10分 ≒ 1.5時間

## 使うデータ (Raw~/ にキネクトごと)

| ファイル | 中身 |
|---|---|
| `A.mkv` | カラー(1080p MJPG)・深度・赤外線。**使うのはカラーだけ** |
| `A_calibration.json` | 工場の校正(カラーカメラのレンズの特性・深度カメラとの位置関係) |
| `A_raw_index.json` | フレームごとの時刻(全キネクト共通の時計)・深度カメラ→部屋の行列(シーン0の位置合わせ) |

## 処理の流れ

1. **キネクトごと** (`gazepipe/process.py`, `gazepipe/heads.py`)
   - RTMW (全身133点) で人を検出。同じ人の二重検出・物の誤検出を除く
   - 体の17点それぞれについて「カメラからその点への光線」を部屋座標で求める
   - 頭を切り出して **6DRepNet360** (頭の向き、後ろ向きも可) と **L2CS-Net / Gaze360** (目の視線)
   - 壊れた JPEG のフレームは飛ばす
2. **まとめる** (`gazepipe/fuse.py`, 1/30秒ごと)
   - 別のキネクトの2人は、**体の各点の光線どうしの距離の中央値が 12cm 以内**なら同じ人
   - **2台以上に写った人だけ**を残し、体の各点を三角測量(外れた光線は外してやり直す)
   - 頭の位置 = 三角測量した両耳の中点、頭の向き = 顔がカメラを向いているキネクトほど重く、
     目の視線 = 顔がカメラを向いていて顔が見えているキネクトだけ
   - 人物トラック(trackId)に引き継ぎ、向きを前後5フレームで滑らかにする

## 確認したこと (2026-09-25 のテスト記録)

- 校正: 2台から見た同じ人の頭の光線は 1cm 以内で交わる / 三角測量した骨格を各カメラに描くと人に重なる
- 目の視線の向きの取り方: カメラを向いた顔 216件で、頭の向きとの差が L2CS の gazeto3d の取り方で最小
  (中央値 27.6°、他の取り方は 30°以上)

## セットアップ(作り直すとき)

```
py -3.11 -m venv .venv
.venv\Scripts\python.exe -m pip install torch==2.6.0 torchvision==0.21.0 --index-url https://download.pytorch.org/whl/cu124
.venv\Scripts\python.exe -m pip install -r requirements.txt
```

モデル (`models/`, git 管理外):
- `6DRepNet360.pth` … https://cloud.ovgu.de/s/TewGC9TDLGgKkmS/download/6DRepNet360_Full-Rotation_300W_LP+Panoptic.pth
- `l2cs_gaze360_resnet50.safetensors` … https://huggingface.co/py-feat/l2cs (MIT, L2CS-Net 公式の重みの再配布)
- RTMW / YOLOX は rtmlib が初回に自動でダウンロードする (`~/.cache/rtmlib`)
