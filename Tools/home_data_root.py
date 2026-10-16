"""HomeExperiment のデータの場所 (Unity のインスペクターの Data Root Directory と同じもの) を選ぶ.

Tools/ の各スクリプトが最初に呼ぶ. 選んだフォルダの下は Unity と同じ構成
(<データの場所>/<実験の名前>/Env/, <データの場所>/<実験の名前>/Skeleton/<実験対象者>/ ...).

  1: Assets/Data/HomeExperiment (プロジェクト内. 既定)
  2: D:\\HomeExperiment          (ハードドライブ)
  それ以外のフォルダは、パスをそのまま入力する.

コマンドラインで --data-root を指定すると選ばずにそれを使う (assets / D:\\HomeExperiment / 任意のフォルダ).
"""

from __future__ import annotations

import argparse
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
ASSETS_DATA_ROOT = PROJECT_ROOT / "Assets" / "Data" / "HomeExperiment"
DRIVE_DATA_ROOT = Path("D:/HomeExperiment")

_CHOICES = [
    ("Assets/Data/HomeExperiment (プロジェクト内)", ASSETS_DATA_ROOT),
    (f"{DRIVE_DATA_ROOT} (ハードドライブ)", DRIVE_DATA_ROOT),
]


def add_argument(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--data-root",
                        help="データの場所. assets (Assets/Data/HomeExperiment) / D:\\HomeExperiment などのフォルダ "
                             "(省略すると最初に選ぶ)")


def _resolve(text: str) -> Path:
    text = text.strip().strip('"')
    if text.lower() in ("assets", "asset"):
        return ASSETS_DATA_ROOT
    return Path(text).expanduser()


def choose_data_root(arg: str | None = None) -> Path | None:
    """データの場所を返す. arg (--data-root) があればそれを使い、無ければ番号かパスを入力してもらう.

    フォルダが無い場合や中止した場合は None. 入力できない (標準入力が無い) ときは既定の Assets を使う.
    """
    if arg:
        root = _resolve(arg)
        if not root.is_dir():
            print(f"データの場所が見つかりません: {root}")
            return None
        print(f"データの場所: {root}")
        return root

    print("\nデータの場所を選んでください:")
    for i, (label, path) in enumerate(_CHOICES, 1):
        missing = "" if path.is_dir() else "  ※見つかりません"
        print(f"  {i}: {label}{missing}")
    print("  (他のフォルダはパスをそのまま入力)")

    while True:
        try:
            text = input("番号またはパス (空欄で 1) > ").strip()
        except EOFError:
            print(f"データの場所: {ASSETS_DATA_ROOT}")
            return ASSETS_DATA_ROOT

        if not text:
            root = ASSETS_DATA_ROOT
        elif text.isdigit():
            if not 1 <= int(text) <= len(_CHOICES):
                print(f"  1〜{len(_CHOICES)} の番号か、フォルダのパスを入力してください。")
                continue
            root = _CHOICES[int(text) - 1][1]
        else:
            root = _resolve(text)

        if root.is_dir():
            print(f"データの場所: {root}")
            return root
        print(f"  フォルダが見つかりません: {root}  (ドライブがつながっているか確認してください)")
