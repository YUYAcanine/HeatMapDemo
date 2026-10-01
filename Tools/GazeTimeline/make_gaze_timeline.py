"""二人の注視対象の時系列を、重ねて1つのグラフ(HTML)にする。

シーン5(5InteractionAnalyze)で保存した
  Skeleton/<実験対象者>/Analysis/Interaction/interaction_<骨格>.json / interaction_<骨格>_episodes.csv
を読み、骨格データ(filtered_HeadJoints / image_GazeElseHead など)ごとに1段ずつ並べて
  Skeleton/<実験対象者>/Analysis/Figures/gaze_timeline.html
に書き出す(毎回上書き)。ブラウザで開くと、帯にマウスを乗せて時刻を確認でき、上のボタンで時間範囲を絞れる。

行の構成(各行は上が1人目・下が2人目。同じ時刻に上下の帯がそろっていれば、2人が同時に同じものを見ている):
  対象物体(TV・Toy など) … TargetGaze
  相手の顔 / 相手の体     … LookAtFace / LookAtPerson
  2人の関係               … 同じ物を同時に見る(JointAttention)・目を合わせる(MutualGaze)・向かい合う(FaceToFace)
帯は episodes の出来事(0.5秒以上続いたもの。0.3秒以下の途切れはつなげる)で、シーン5の集計と同じ判定。

使い方:
  python Tools/GazeTimeline/make_gaze_timeline.py <実験の名前> <実験対象者>
  (引数を省くと、フォルダの一覧から選ぶ)
Python の標準ライブラリだけで動く。
"""

from __future__ import annotations

import argparse
import csv
import json
import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[2]
DATA_ROOT = PROJECT_ROOT / "Assets" / "Data" / "HomeExperiment"


def _choose(label: str, options: list[str]) -> str | None:
    if not options:
        print(f"{label} がありません。")
        return None
    for i, name in enumerate(options, 1):
        print(f"  {i}. {name}")
    answer = input(f"{label} の番号: ").strip()
    if answer.isdigit() and 1 <= int(answer) <= len(options):
        return options[int(answer) - 1]
    return None


def _subdirs(path: Path) -> list[str]:
    return sorted(p.name for p in path.iterdir() if p.is_dir()) if path.exists() else []


def load_source(interaction_dir: Path, source: str) -> dict | None:
    """interaction_<source>.json(人物・対象・時間範囲)と episodes.csv を読む"""
    summary_path = interaction_dir / f"interaction_{source}.json"
    episodes_path = interaction_dir / f"interaction_{source}_episodes.csv"

    if not summary_path.exists() or not episodes_path.exists():
        return None

    summary = json.loads(summary_path.read_text(encoding="utf-8"))
    params = summary.get("parameters", {})

    with episodes_path.open(encoding="utf-8-sig", newline="") as f:
        episodes = [
            {
                "type": row["type"],
                "person": row["person"],
                "other": row["other"],
                "target": row["target"],
                "s": round(float(row["startSec"]), 3),
                "e": round(float(row["endSec"]), 3),
            }
            for row in csv.DictReader(f)
        ]

    return {
        "name": source,
        "createdAt": summary.get("createdAt", ""),
        "rangeStart": params.get("rangeStartSec", 0.0),
        "rangeEnd": params.get("rangeEndSec", 0.0),
        "coneAngle": params.get("coneAngle", 0.0),
        "minEpisode": params.get("minEpisodeSeconds", 0.5),
        "persons": [p["name"] for p in summary.get("persons", [])],
        "targets": summary.get("targets", []),
        "episodes": episodes,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="二人の注視対象の時系列グラフ(HTML)を作る")
    parser.add_argument("experiment", nargs="?", help="実験の名前 (Assets/Data/HomeExperiment/<実験の名前>)")
    parser.add_argument("subject", nargs="?", help="実験対象者 (Skeleton/<実験対象者>)")
    args = parser.parse_args()

    experiment = args.experiment or _choose("実験の名前", _subdirs(DATA_ROOT))
    if not experiment:
        return 1

    subject = args.subject or _choose("実験対象者", _subdirs(DATA_ROOT / experiment / "Skeleton"))
    if not subject:
        return 1

    analysis_dir = DATA_ROOT / experiment / "Skeleton" / subject / "Analysis"
    interaction_dir = analysis_dir / "Interaction"
    names = sorted(p.name[len("interaction_"):-len("_episodes.csv")] for p in interaction_dir.glob("interaction_*_episodes.csv"))
    sources = [s for s in (load_source(interaction_dir, name) for name in names) if s is not None]

    if not sources:
        print(f"シーン5の結果(interaction_*_episodes.csv)がありません: {interaction_dir}")
        return 1

    data = {"experiment": experiment, "subject": subject, "sources": sources}
    template = (Path(__file__).parent / "gaze_timeline_template.html").read_text(encoding="utf-8")
    html = template.replace("/*__DATA__*/null", json.dumps(data, ensure_ascii=False))

    out_path = analysis_dir / "Figures" / "gaze_timeline.html"
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(html, encoding="utf-8")

    print(f"保存しました: {out_path}")
    for s in sources:
        print(f"  {s['name']}: 人物 {', '.join(s['persons'])} / 対象 {', '.join(s['targets'])} / 出来事 {len(s['episodes'])} 件")
    return 0


if __name__ == "__main__":
    sys.exit(main())
