"""二人の注視対象の時系列を、重ねて1つのグラフ(HTML)にする。

シーン5(5InteractionAnalyze)で保存した
  Skeleton/<実験対象者>/Analysis/Interaction/interaction_<骨格>.json / interaction_<骨格>_episodes.csv
を読み、骨格データ(filtered_HeadJoints / filtered_HeadJoints_smooth05 / image_GazeElseHead など)ごとに1段ずつ並べて
  Skeleton/<実験対象者>/Analysis/Figures/gaze_timeline.html
に書き出す(毎回上書き)。ブラウザで開くと、帯にマウスを乗せて時刻を確認でき、上のボタンで時間範囲を絞れる。

行の構成(各行は上が1人目・下が2人目。同じ時刻に上下の帯がそろっていれば、2人が同時に同じものを見ている):
  対象物体(TV・Toy など) … TargetGaze
  相手の顔 / 相手の体     … LookAtFace / LookAtPerson
  2人の関係               … 同じ物を同時に見る(JointAttention)・目を合わせる(MutualGaze)・向かい合う(FaceToFace)
帯は episodes の出来事(0.5秒以上続いたもの。0.3秒以下の途切れはつなげる)で、シーン5の集計と同じ判定。

使い方:
  python Tools/GazeTimeline/make_gaze_timeline.py <実験の名前> <実験対象者>
  (最初にデータの場所 Assets / D:\\HomeExperiment を選ぶ。--data-root D:\\HomeExperiment で指定もできる)
  (引数を省くと、フォルダの一覧から選ぶ)
  骨格データが2つ以上あるとき(平滑化した骨格 Tools/GazeSmooth でもシーン5で Save したときなど)は、グラフにする骨格も一覧から選ぶ
  (1,3 のように複数選べる。何も入力しなければ全部)。--sources filtered_HeadJoints_smooth05 のように指定すれば選ばずに作る。
Python の標準ライブラリだけで動く。
"""

from __future__ import annotations

import argparse
import csv
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import home_data_root  # noqa: E402  Tools/home_data_root.py


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


def _choose_sources(names: list[str]) -> list[str] | None:
    """グラフにする骨格データを選ぶ(複数可)。何も入力しなければ全部"""
    for i, name in enumerate(names, 1):
        print(f"  {i}. {name}")
    answer = input("骨格データの番号 (1,3 のように複数可。空欄で全部): ").strip()
    if not answer:
        return names
    chosen = []
    for part in answer.replace(",", " ").split():
        if not part.isdigit() or not 1 <= int(part) <= len(names):
            print(f"番号が正しくありません: {part}")
            return None
        if names[int(part) - 1] not in chosen:
            chosen.append(names[int(part) - 1])
    return chosen


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
    home_data_root.add_argument(parser)
    parser.add_argument("experiment", nargs="?", help="実験の名前 (<データの場所>/<実験の名前>)")
    parser.add_argument("subject", nargs="?", help="実験対象者 (Skeleton/<実験対象者>)")
    parser.add_argument("--sources", nargs="+", metavar="骨格",
                        help="グラフにする骨格データ (interaction_<骨格>_episodes.csv の <骨格>。省略すると一覧から選ぶ)")
    args = parser.parse_args()

    data_root = home_data_root.choose_data_root(args.data_root)
    if data_root is None:
        return 1

    experiment = args.experiment or _choose("実験の名前", _subdirs(data_root))
    if not experiment:
        return 1

    subject = args.subject or _choose("実験対象者", _subdirs(data_root / experiment / "Skeleton"))
    if not subject:
        return 1

    analysis_dir = data_root / experiment / "Skeleton" / subject / "Analysis"
    interaction_dir = analysis_dir / "Interaction"
    names = sorted(p.name[len("interaction_"):-len("_episodes.csv")] for p in interaction_dir.glob("interaction_*_episodes.csv"))

    if args.sources:
        missing = [name for name in args.sources if name not in names]
        if missing:
            print(f"シーン5の結果がない骨格データです: {', '.join(missing)} (あるもの: {', '.join(names) or 'なし'})")
            return 1
        names = list(dict.fromkeys(args.sources))
    elif len(names) > 1 and sys.stdin.isatty():
        names = _choose_sources(names)
        if not names:
            return 1

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
