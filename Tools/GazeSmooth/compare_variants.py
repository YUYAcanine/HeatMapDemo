"""骨格の違い(平滑化なし / smooth03 / smooth05 / smooth10 など)で、シーン5の集計がどれだけ変わるかを横に並べる。

シーン5(5InteractionAnalyze)で Skeleton Variant を変えてそれぞれ Save した
  Skeleton/<実験対象者>/Analysis/Interaction/interaction_<骨格>_summary.csv
を全部読み、指標(種類・人物・相手・対象)ごとに1行、骨格ごとに 秒数 と 回数 の列を並べて
  Skeleton/<実験対象者>/Analysis/Figures/variant_comparison.csv
に書き出す(毎回上書き)。画面にも秒数の表を出す。
平滑化の窓の長さを変えても結論が変わらないかを確かめるのに使う。

使い方:
  python Tools/GazeSmooth/compare_variants.py <実験の名前> <実験対象者>
  (最初にデータの場所 Assets / D:\\HomeExperiment を選ぶ。--data-root D:\\HomeExperiment で指定もできる)
  (引数を省くと、フォルダの一覧から選ぶ)
Python の標準ライブラリだけで動く。
"""

from __future__ import annotations

import argparse
import csv
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import home_data_root  # noqa: E402  Tools/home_data_root.py

KEY_COLUMNS = ["type", "person", "other", "target"]
PREFIX = "interaction_"
SUFFIX = "_summary.csv"


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


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    home_data_root.add_argument(parser)
    parser.add_argument("experiment", nargs="?", help="実験の名前 (省略すると一覧から選ぶ)")
    parser.add_argument("subject", nargs="?", help="実験対象者 (省略すると一覧から選ぶ)")
    args = parser.parse_args()

    data_root = home_data_root.choose_data_root(args.data_root)
    if data_root is None:
        return 1

    experiment = args.experiment or _choose("実験の名前", _subdirs(data_root))
    if not experiment:
        return 1

    skeleton_root = data_root / experiment / "Skeleton"
    subject = args.subject or _choose("実験対象者", _subdirs(skeleton_root))
    if not subject:
        return 1

    analysis_dir = skeleton_root / subject / "Analysis"
    interaction_dir = analysis_dir / "Interaction"
    sources = sorted(p.name[len(PREFIX):-len(SUFFIX)] for p in interaction_dir.glob(f"{PREFIX}*{SUFFIX}"))

    if not sources:
        print(f"シーン5で保存した集計がありません: {interaction_dir}")
        return 1

    # 指標ごとに {骨格: (秒数, 回数)}。行の順番は最初に出てきた順
    rows: dict[tuple, dict[str, tuple[str, str]]] = {}

    for source in sources:
        with (interaction_dir / f"{PREFIX}{source}{SUFFIX}").open(encoding="utf-8-sig", newline="") as f:
            for row in csv.DictReader(f):
                key = tuple(row[c] for c in KEY_COLUMNS)
                rows.setdefault(key, {})[source] = (row["seconds"], row["episodes"])

    figures_dir = analysis_dir / "Figures"
    figures_dir.mkdir(parents=True, exist_ok=True)
    output_path = figures_dir / "variant_comparison.csv"

    with output_path.open("w", encoding="utf-8-sig", newline="") as f:
        writer = csv.writer(f)
        writer.writerow(KEY_COLUMNS + [f"{s}_seconds" for s in sources] + [f"{s}_episodes" for s in sources])
        for key, values in rows.items():
            writer.writerow(list(key)
                            + [values.get(s, ("", ""))[0] for s in sources]
                            + [values.get(s, ("", ""))[1] for s in sources])

    print("骨格: " + ", ".join(f"[{i + 1}] {s}" for i, s in enumerate(sources)))
    print("秒数 (回数)")
    for key, values in rows.items():
        name = " ".join(part for part in key if part)
        cells = []
        for s in sources:
            seconds, episodes = values.get(s, ("", ""))
            cells.append(f"{float(seconds):8.1f} ({int(float(episodes)):3d})" if seconds else " " * 14)
        print(f"  {name:<34}" + "  ".join(cells))

    print(f"\n→ {output_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
