"""イベント(動画から書き出した場面)ごとに、対象物体への興味量と、内積による接近の度合いを表にする。

実験室(0Scenes/2Analyze)の「点灯条件ごとの表」を、家庭では「イベントごとの表」として同じ計算で作る:
  興味量     … GazeTable と同じ。イベントのあいだに、その対象物体が注視対象だったフレーム数と割合
               (視線コーンに入った対象物体のうち、コーンの軸に最も近い1つがそのフレームの注視対象。
                ScoreHeatSummary / シーン5 と同じ判定)
  接近の度合い … VectorSummary / MovementTable と同じ。Pelvis の移動ベクトル(--span 秒の区間の水平の変位)と
               対象物体への方向ベクトルの 内積の合計(Dot Sum) と コサイン類似度の平均(Cos Average)。
               速さが --max-speed を超えたフレームは計測エラーとして除く

読むもの:
  Skeleton/<実験対象者>/Input/<実験対象者>.csv … イベントの一覧。列は start,end,target,content
                                                (start/end は 分:秒 か 時:分:秒 か 秒数。target は対象物体の名前)
  Skeleton/<実験対象者>/Filtered/grouped_<Mode>.json … シーン3で作った人物ごとの骨格(シーン4/5と同じ)
  Env/TargetObjects.json, Env/Meshes/ … シーン0で保存した対象物体
書き出すもの(毎回上書き):
  Skeleton/<実験対象者>/Analysis/Events/event_table_<骨格>.html … 見やすい表(ブラウザで開く)。人物ごとに 興味量 / コサイン類似度 / 内積 の3つ
  Skeleton/<実験対象者>/Analysis/Events/event_table_<骨格>.md   … 同じ表の Markdown(チャットや文書に貼る用)
  Skeleton/<実験対象者>/Analysis/Events/event_table_<骨格>.csv  … イベント × 人物 × 対象物体 で1行(Excel などで集計する用)
  画面にも同じ表を出す。

表の見方:
  行 = Input の csv に書いたイベント(動画から書き出した場面。視線から自動で見つけた出来事ではない)。
       その開始〜終了の時間だけを切り出して、対象物体ごとに数える。最後の行「イベント以外」は、どのイベントにも入らない時間
  列 = 対象物体。* (HTML では色付き)は、そのイベントの対象物体(csv の target)
  その行で一番大きい値は [ ] で囲む(HTML・Markdown では太字)。
  イベントの対象物体(*)が一番大きければ、「その場面で、その対象物体を最もよく見た / その対象物体に最も近づいた」と読める

割合の分母は2通り出す(csv):
  interestRatio        … イベントの全フレーム(実験室の GazeTable と同じ。人物が映っていないフレームも含む。表はこちら)
  interestRatioPresent … そのうち人物が映っていたフレーム

使い方:
  python Tools/GazeEvents/make_event_table.py <実験の名前> <実験対象者>
  (引数を省くと、フォルダの一覧から選ぶ)
  --events <ファイル>   … イベントの一覧(省略時は Input/<実験対象者>.csv。無ければ Input にある唯一の csv)
  --mode HeadJoints     … 読み込む grouped_<Mode>.json
  --variant smooth05    … 平滑化した骨格 grouped_<Mode>_<名前>.json を使う
  --person child        … この人物だけ出す
numpy が必要(Tools/GazePipeline の .venv にも入っている)。
"""

from __future__ import annotations

import argparse
import csv
import html
import json
import sys
import unicodedata
from pathlib import Path

import numpy as np

PROJECT_ROOT = Path(__file__).resolve().parents[2]
DATA_ROOT = PROJECT_ROOT / "Assets" / "Data" / "HomeExperiment"

OUTSIDE_LABEL = "イベント以外"
CHUNK = 4096


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


def parse_time(text: str) -> float:
    """分:秒 / 時:分:秒 / 秒数 を秒にする"""
    seconds = 0.0
    for part in text.strip().split(":"):
        seconds = seconds * 60.0 + float(part)
    return seconds


def format_time(seconds: float) -> str:
    total = int(round(seconds))
    return f"{total // 60}:{total % 60:02d}"


def load_events(path: Path) -> list[dict]:
    events = []
    with path.open(encoding="utf-8-sig", newline="") as f:
        for line_number, row in enumerate(csv.DictReader(f), 2):
            if not (row.get("start") or "").strip():
                continue
            try:
                start, end = parse_time(row["start"]), parse_time(row["end"])
            except (ValueError, KeyError, AttributeError):
                raise SystemExit(f"{path.name} の {line_number} 行目: start / end を読めません: {row}")
            if end <= start:
                raise SystemExit(f"{path.name} の {line_number} 行目: end が start より後ではありません: {row}")
            events.append({
                "start": start,
                "end": end,
                "target": (row.get("target") or "").strip(),
                "content": (row.get("content") or "").strip(),
            })
    return events


def _quaternion_matrix(q: dict) -> np.ndarray:
    x, y, z, w = q["x"], q["y"], q["z"], q["w"]
    return np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)],
    ])


def load_targets(env_dir: Path) -> dict[str, dict]:
    """対象物体(グループごと)の頂点(部屋座標)と中心。{名前: {"vertices": (N,3), "center": (3,)}}"""
    path = env_dir / "TargetObjects.json"
    if not path.exists():
        raise SystemExit(f"対象物体がありません。シーン0で保存してください: {path}")

    data = json.loads(path.read_text(encoding="utf-8"))
    vertices: dict[str, list[np.ndarray]] = {}

    for obj in data.get("objects", []):
        name = obj.get("group") or obj.get("name")
        mesh_path = env_dir / "Meshes" / (obj.get("meshFile") or "")

        if not obj.get("meshFile") or not mesh_path.exists():
            print(f"  [注意] {obj.get('name')}: メッシュのファイルが無いので使いません"
                  f" (アセットを参照する対象物体はこのスクリプトでは読めません)")
            continue

        mesh = json.loads(mesh_path.read_text(encoding="utf-8"))
        local = np.array([[v["x"], v["y"], v["z"]] for v in mesh["vertices"]], dtype=float)
        scale = np.array([obj["scale"]["x"], obj["scale"]["y"], obj["scale"]["z"]])
        position = np.array([obj["position"]["x"], obj["position"]["y"], obj["position"]["z"]])
        world = (local * scale) @ _quaternion_matrix(obj["rotation"]).T + position
        vertices.setdefault(name, []).append(world)

    targets = {}
    for name, parts in vertices.items():
        points = np.concatenate(parts)
        targets[name] = {"vertices": points, "center": (points.min(axis=0) + points.max(axis=0)) * 0.5}
    return targets


def load_skeleton(path: Path) -> tuple[np.ndarray, dict[int, dict]]:
    """フレームの時刻と、人物ごとの Head / Nose / Pelvis(映っていないフレームは NaN)"""
    print(f"{path} を読み込んでいます ({path.stat().st_size / 1e6:.0f} MB。数十秒かかります)...")
    with path.open(encoding="utf-8") as f:
        data = json.load(f)

    frames = data.get("frames") or []
    times = np.array([frame["timeSec"] for frame in frames], dtype=float)
    persons: dict[int, dict] = {}

    for i, frame in enumerate(frames):
        for person in frame.get("persons", []):
            track_id = person.get("trackId", -1)
            p = persons.get(track_id)
            if p is None:
                p = persons[track_id] = {
                    "name": person.get("label") or f"person_{track_id}",
                    "present": np.zeros(len(frames), dtype=bool),
                    "Head": np.full((len(frames), 3), np.nan),
                    "Nose": np.full((len(frames), 3), np.nan),
                    "Pelvis": np.full((len(frames), 3), np.nan),
                }
            p["present"][i] = True
            for joint in person.get("joints", []):
                joint_id = joint.get("jointId")
                if joint_id in ("Head", "Nose", "Pelvis"):
                    v = joint["position"]
                    p[joint_id][i] = (v["x"], v["y"], v["z"])

    return times, persons


def gaze_directions(head: np.ndarray, nose: np.ndarray, downward_deg: float) -> np.ndarray:
    """HomeGazeAnalyzer.TryGetGaze と同じ。Head→Nose を下へ downward_deg 回した単位ベクトル(取れないフレームは NaN)"""
    raw = nose - head
    length = np.linalg.norm(raw, axis=1, keepdims=True)
    with np.errstate(invalid="ignore", divide="ignore"):
        ok = (length[:, 0] ** 2 >= 0.0001)
        raw = raw / length

        right = np.cross(np.array([0.0, 1.0, 0.0]), raw)
        right_length = np.linalg.norm(right, axis=1, keepdims=True)
        right = np.where(right_length ** 2 < 0.0001, np.array([1.0, 0.0, 0.0]), right / right_length)

        angle = np.radians(downward_deg)
        rotated = (raw * np.cos(angle) + np.cross(right, raw) * np.sin(angle)
                   + right * (right * raw).sum(1, keepdims=True) * (1.0 - np.cos(angle)))
        rotated = rotated / np.linalg.norm(rotated, axis=1, keepdims=True)

    rotated[~ok] = np.nan
    return rotated


def gaze_targets(head: np.ndarray, direction: np.ndarray, targets: dict[str, dict], cone_deg: float,
                 cone_distance: float) -> np.ndarray:
    """フレームごとの注視対象の番号(-1 = どれも見ていない, -2 = 視線が取れない)"""
    count = len(head)
    result = np.full(count, -2, dtype=int)
    valid = ~np.isnan(direction).any(axis=1) & ~np.isnan(head).any(axis=1)
    index = np.where(valid)[0]
    cos_threshold = np.cos(np.radians(cone_deg))

    for start in range(0, len(index), CHUNK):
        rows = index[start:start + CHUNK]
        best_dot = np.full(len(rows), -np.inf)
        best_target = np.full(len(rows), -1, dtype=int)

        for t, target in enumerate(targets.values()):
            to_vertex = target["vertices"][None, :, :] - head[rows][:, None, :]
            distance = np.linalg.norm(to_vertex, axis=2)
            with np.errstate(invalid="ignore", divide="ignore"):
                dot = (to_vertex * direction[rows][:, None, :]).sum(axis=2) / distance
            dot[(distance <= 0.0) | (distance > cone_distance)] = -np.inf
            # コーンに入った頂点のうち、軸に最も近いもの
            nearest = dot.max(axis=1)
            better = (nearest >= cos_threshold) & (nearest > best_dot)
            best_dot[better] = nearest[better]
            best_target[better] = t

        result[rows] = best_target

    return result


def movement(times: np.ndarray, pelvis: np.ndarray, targets: dict[str, dict], span: float,
             max_speed: float) -> dict:
    """VectorSummary と同じ。フレームごとの 内積・コサイン・対象方向への速さ(対象物体ごと)と、評価したフレームか"""
    count = len(times)
    flat = pelvis.copy()
    flat[:, 1] = 0.0
    has_pelvis = ~np.isnan(pelvis).any(axis=1)
    present = np.where(has_pelvis)[0]
    half = max(span, 0.0) * 0.5

    valid = np.zeros(count, dtype=bool)
    displacement = np.zeros((count, 3))
    delta_time = np.zeros(count)

    if len(present) > 0:
        present_times = times[present]
        # 前後 half 秒以内で Pelvis が取れている一番遠いフレームどうしの変位
        first = np.searchsorted(present_times, times - half - 1e-9, side="left")
        last = np.searchsorted(present_times, times + half + 1e-9, side="right") - 1
        ok = has_pelvis & (first <= last)
        a = present[np.clip(first, 0, len(present) - 1)]
        b = present[np.clip(last, 0, len(present) - 1)]
        delta_time = np.where(ok, times[b] - times[a], 0.0)
        ok &= delta_time > 0.0
        displacement[ok] = flat[b[ok]] - flat[a[ok]]
        valid = ok

    with np.errstate(invalid="ignore", divide="ignore"):
        speed = np.linalg.norm(displacement, axis=1) / delta_time
    if max_speed > 0.0:
        valid &= ~(speed > max_speed)

    move_magnitude = np.linalg.norm(displacement, axis=1)
    result = {"valid": valid, "dot": {}, "cos": {}, "closing": {}}

    for name, target in targets.items():
        center = target["center"].copy()
        center[1] = 0.0
        to_target = center - flat
        target_magnitude = np.linalg.norm(to_target, axis=1)
        usable = valid & (target_magnitude > 0.0001)

        dot = np.where(usable, (displacement * to_target).sum(axis=1), 0.0)
        with np.errstate(invalid="ignore", divide="ignore"):
            cosine = np.where(usable & (move_magnitude > 0.0001),
                              np.clip(dot / (move_magnitude * target_magnitude), -1.0, 1.0), 0.0)
            closing = np.where(usable, dot / (delta_time * target_magnitude), 0.0)

        result["dot"][name] = dot
        result["cos"][name] = cosine
        result["closing"][name] = closing

    return result


# ------------------------------------------------------------
# 表の出力
# ------------------------------------------------------------
# 表の種類: (見出し, 説明, セルの文字, 一番大きい値を決める数値, 右端の列の見出し, 右端の列の値)
TABLE_KINDS = [
    ("興味量",
     "イベントのあいだに、その対象物体が注視対象だったフレーム数(かっこ内はイベントの全フレームに対する割合)。大きいほどよく見ていた",
     lambda c: f"{c['gazeFrames']} ({c['ratio'] * 100:.1f}%)", lambda c: c["gazeFrames"],
     ["映っていた", "全フレーム"], lambda r: [str(r["presentFrames"]), str(r["eventFrames"])]),
    ("接近の度合い: コサイン類似度の平均",
     "移動の向きと対象物体への向きのコサイン類似度の平均(-1〜1)。正で大きいほどその対象物体に向かって動いた。0 付近は向かっても離れてもいない",
     lambda c: f"{c['cos']:+.3f}", lambda c: c["cos"],
     ["評価フレーム"], lambda r: [str(r["moveFrames"])]),
    ("接近の度合い: 内積の合計",
     "移動ベクトルと対象物体への方向ベクトルの内積の合計(m^2)。正で大きいほどその対象物体に向かって長く動いた(遠い対象物体ほど値が大きくなりやすい)",
     lambda c: f"{c['dot']:+.2f}", lambda c: c["dot"],
     ["評価フレーム"], lambda r: [str(r["moveFrames"])]),
]


def _best_target(row: dict, value) -> str | None:
    """その行で値が一番大きい対象物体(全部 0 なら None)"""
    values = {name: value(cell) for name, cell in row["cells"].items()}
    if all(v == 0 for v in values.values()):
        return None
    return max(values, key=values.get)


def _hit_summary(rows: list[dict], value) -> str:
    events = [r for r in rows if r["eventTarget"] in r["cells"]]
    hits = sum(1 for r in events if _best_target(r, value) == r["eventTarget"])
    return f"イベントの対象物体が一番大きかったイベント: {hits} / {len(events)}"


def _width(text: str) -> int:
    """画面での幅(全角は2)"""
    return sum(2 if unicodedata.east_asian_width(c) in "FWA" else 1 for c in text)


def _pad(text: str, width: int, left: bool = False) -> str:
    space = " " * max(width - _width(text), 0)
    return text + space if left else space + text


def render_console(report: dict[str, list[dict]], target_names: list[str]) -> str:
    out = ["", "表の見方: 行 = csv に書いたイベントの時間, 列 = 対象物体。 * = そのイベントの対象物体, [ ] = その行で一番大きい値"]

    for person, rows in report.items():
        out.append(f"\n===== {person} =====")

        for title, note, text, value, extra_header, extra in TABLE_KINDS:
            header = ["イベント", "対象"] + target_names + extra_header
            table = [header]

            for r in rows:
                best = _best_target(r, value)
                cells = []
                for name in target_names:
                    cell = text(r["cells"][name])
                    cell = f"[{cell}]" if name == best else f" {cell} "
                    cells.append(cell + ("*" if name == r["eventTarget"] else " "))
                table.append([r["label"], r["eventTarget"] or "-"] + cells + extra(r))

            widths = [max(_width(row[c]) for row in table) for c in range(len(header))]
            out.append(f"\n■ {title}\n  {note}")
            for row in table:
                out.append("  " + "  ".join(_pad(v, w, left=(c < 2)) for c, (v, w) in enumerate(zip(row, widths))))
            out.append("  " + _hit_summary(rows, value))

    return "\n".join(out)


def render_markdown(title: str, settings: str, report: dict[str, list[dict]], target_names: list[str]) -> str:
    out = [f"# {title}", "", settings, "",
           "行 = csv に書いたイベントの時間、列 = 対象物体。★ = そのイベントの対象物体、**太字** = その行で一番大きい値。", ""]

    for person, rows in report.items():
        out += [f"## {person}", ""]

        for kind, note, text, value, extra_header, extra in TABLE_KINDS:
            header = ["イベント", "対象"] + target_names + extra_header + ["内容"]
            out += [f"### {kind}", "", note, "", "| " + " | ".join(header) + " |", "|" + "---|" * len(header)]

            for r in rows:
                best = _best_target(r, value)
                cells = []
                for name in target_names:
                    cell = text(r["cells"][name])
                    cell = f"**{cell}**" if name == best else cell
                    cells.append(cell + (" ★" if name == r["eventTarget"] else ""))
                line = [r["label"], r["eventTarget"] or "–"] + cells + extra(r) + [r["content"].replace("|", "/")]
                out.append("| " + " | ".join(line) + " |")

            out += ["", _hit_summary(rows, value), ""]

    return "\n".join(out)


HTML_STYLE = """
body { font-family: "Segoe UI", "Yu Gothic UI", "Meiryo", sans-serif; margin: 24px auto; max-width: 1200px; padding: 0 16px;
       color: #1f2328; background: #ffffff; line-height: 1.6; }
h1 { font-size: 22px; } h2 { font-size: 19px; margin-top: 36px; border-bottom: 2px solid #d0d7de; padding-bottom: 4px; }
h3 { font-size: 16px; margin-bottom: 2px; }
p.note, p.settings { color: #57606a; margin: 2px 0 8px; font-size: 14px; }
.guide { background: #f6f8fa; border: 1px solid #d0d7de; border-radius: 6px; padding: 10px 16px; font-size: 14px; }
.scroll { overflow-x: auto; }
table { border-collapse: collapse; font-size: 14px; margin: 6px 0 4px; }
th, td { border: 1px solid #d0d7de; padding: 4px 10px; text-align: right; white-space: nowrap; }
th { background: #f6f8fa; } td.left, th.left { text-align: left; }
td.content { white-space: normal; min-width: 260px; color: #57606a; }
td.target { background: #fff3c4; } td.best { font-weight: 700; } tr.outside td { color: #57606a; font-style: italic; }
p.summary { font-size: 14px; margin: 2px 0 20px; }
.key { display: inline-block; padding: 0 6px; border: 1px solid #d0d7de; }
"""


def render_html(title: str, settings: str, report: dict[str, list[dict]], target_names: list[str]) -> str:
    e = html.escape
    out = ["<!doctype html>", '<html lang="ja"><head><meta charset="utf-8">',
           '<meta name="viewport" content="width=device-width, initial-scale=1">',
           f"<title>{e(title)}</title><style>{HTML_STYLE}</style></head><body>",
           f"<h1>{e(title)}</h1>", f'<p class="settings">{e(settings)}</p>',
           '<div class="guide"><b>表の見方</b><br>'
           "行 = csv に書いたイベント(動画から書き出した場面)。その開始〜終了の時間だけを切り出して数えています。"
           "最後の行「イベント以外」は、どのイベントにも入らない時間です。<br>"
           '列 = 対象物体。<span class="key" style="background:#fff3c4">色付き</span> = そのイベントの対象物体、'
           '<span class="key"><b>太字</b></span> = その行で一番大きい値。<br>'
           "色付きのセルが太字なら、「その場面で、その対象物体を最もよく見た / その対象物体に最も近づいた」と読めます。</div>"]

    for person, rows in report.items():
        out.append(f"<h2>{e(person)}</h2>")

        for kind, note, text, value, extra_header, extra in TABLE_KINDS:
            out.append(f'<h3>{e(kind)}</h3><p class="note">{e(note)}</p><div class="scroll"><table><tr>')
            out.append('<th class="left">イベント</th><th class="left">対象</th>'
                       + "".join(f"<th>{e(n)}</th>" for n in target_names + extra_header)
                       + '<th class="left">内容</th></tr>')

            for r in rows:
                best = _best_target(r, value)
                row_class = ' class="outside"' if r["start"] is None else ""
                cells = ""
                for name in target_names:
                    classes = " ".join(c for c, on in (("target", name == r["eventTarget"]), ("best", name == best)) if on)
                    cells += f'<td class="{classes}">{e(text(r["cells"][name]))}</td>'
                out.append(f'<tr{row_class}><td class="left">{e(r["label"])}</td>'
                           f'<td class="left">{e(r["eventTarget"] or "–")}</td>'
                           + cells + "".join(f"<td>{e(v)}</td>" for v in extra(r))
                           + f'<td class="left content">{e(r["content"])}</td></tr>')

            out.append(f'</table></div><p class="summary">{e(_hit_summary(rows, value))}</p>')

    out.append("</body></html>")
    return "\n".join(out)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("experiment", nargs="?", help="実験の名前 (省略すると一覧から選ぶ)")
    parser.add_argument("subject", nargs="?", help="実験対象者 (省略すると一覧から選ぶ)")
    parser.add_argument("--events", help="イベントの一覧の csv (省略時は Input/<実験対象者>.csv)")
    parser.add_argument("--mode", default="HeadJoints", help="読み込む grouped_<Mode>.json の Mode (既定 HeadJoints)")
    parser.add_argument("--variant", default="", help="平滑化した骨格の名前 (grouped_<Mode>_<名前>.json。省略時は平滑化前)")
    parser.add_argument("--person", help="この人物だけ出す (例: child)")
    parser.add_argument("--downward", type=float, default=24.4, help="頭部方向を下へ補正する角度(度, 既定 24.4)")
    parser.add_argument("--cone", type=float, default=10.14, help="視線コーンの半角(度, 既定 10.14)")
    parser.add_argument("--distance", type=float, default=5.0, help="視線コーンの長さ(m, 既定 5)")
    parser.add_argument("--span", type=float, default=0.2, help="移動ベクトルを求める区間の長さ(秒, 既定 0.2)")
    parser.add_argument("--max-speed", type=float, default=2.0, help="これを超える速さのフレームは除く(m/s, 既定 2。0 で除かない)")
    args = parser.parse_args()

    experiment = args.experiment or _choose("実験の名前", _subdirs(DATA_ROOT))
    if not experiment:
        return 1

    skeleton_root = DATA_ROOT / experiment / "Skeleton"
    subject = args.subject or _choose("実験対象者", _subdirs(skeleton_root))
    if not subject:
        return 1

    subject_dir = skeleton_root / subject
    input_dir = subject_dir / "Input"

    if args.events:
        events_path = Path(args.events)
        if not events_path.is_absolute() and not events_path.exists():
            events_path = input_dir / args.events
    else:
        events_path = input_dir / f"{subject}.csv"
        if not events_path.exists():
            candidates = sorted(input_dir.glob("*.csv")) if input_dir.exists() else []
            if len(candidates) == 1:
                events_path = candidates[0]

    if not events_path.exists():
        print(f"イベントの一覧がありません (列: start,end,target,content): {events_path}")
        return 1

    variant = args.variant.strip()
    source_name = f"{args.mode}_{variant}" if variant else args.mode
    skeleton_path = subject_dir / "Filtered" / f"grouped_{source_name}.json"

    if not skeleton_path.exists():
        print(f"人物ごとの骨格がありません: {skeleton_path}")
        return 1

    events = load_events(events_path)
    if not events:
        print(f"イベントがありません: {events_path}")
        return 1

    targets = load_targets(DATA_ROOT / experiment / "Env")
    if not targets:
        print("使える対象物体がありません。")
        return 1
    target_names = list(targets)

    for event in events:
        if event["target"] not in targets:
            print(f"  [注意] {format_time(event['start'])}-{format_time(event['end'])} の target '{event['target']}' は"
                  f"対象物体にありません (あるもの: {', '.join(target_names)})。対象物体の印を付けずに出します")

    times, persons = load_skeleton(skeleton_path)
    if len(times) == 0 or not persons:
        print("骨格のフレームがありません。")
        return 1

    # フレームの長さ(次のフレームまでの間隔。シーン4/5と同じく 0.2 秒で打ち切る)
    duration = np.diff(times, append=times[-1] + (times[-1] - times[-2] if len(times) > 1 else 0.0))
    duration = np.clip(duration, 0.0, 0.2)

    in_any_event = np.zeros(len(times), dtype=bool)
    rows_def = []
    for event in events:
        mask = (times >= event["start"]) & (times < event["end"])
        in_any_event |= mask
        rows_def.append((f"{format_time(event['start'])}-{format_time(event['end'])}", event["target"], event["content"],
                         mask, event["start"], event["end"]))
    rows_def.append((OUTSIDE_LABEL, "", "どのイベントにも入らない時間", ~in_any_event, None, None))

    title = f"イベントごとの興味量と接近の度合い: {experiment} / {subject}"
    settings = (f"骨格 grouped_{source_name} / イベント {events_path.name} ({len(events)} 件) / "
                f"視線: 下向き {args.downward}°, コーン半角 {args.cone}°, 長さ {args.distance} m / "
                f"移動: 区間 {args.span} 秒, 速さの上限 {args.max_speed} m/s")

    csv_rows = []
    report: dict[str, list[dict]] = {}

    for track_id in sorted(persons):
        person = persons[track_id]
        if args.person and person["name"] != args.person:
            continue

        direction = gaze_directions(person["Head"], person["Nose"], args.downward)
        gazed = gaze_targets(person["Head"], direction, targets, args.cone, args.distance)
        move = movement(times, person["Pelvis"], targets, args.span, args.max_speed)
        rows = report[person["name"]] = []

        for label, event_target, content, mask, start, end in rows_def:
            event_frames = int(mask.sum())
            present_frames = int((mask & person["present"]).sum())
            move_mask = mask & move["valid"]
            move_frames = int(move_mask.sum())
            row = {"label": label, "eventTarget": event_target, "content": content, "start": start,
                   "eventFrames": event_frames, "presentFrames": present_frames, "moveFrames": move_frames, "cells": {}}
            rows.append(row)

            for t, name in enumerate(target_names):
                hit = mask & (gazed == t)
                gaze_frames = int(hit.sum())
                ratio = gaze_frames / event_frames if event_frames else 0.0
                ratio_present = gaze_frames / present_frames if present_frames else 0.0
                dot_sum = float(move["dot"][name][move_mask].sum())
                cos_mean = float(move["cos"][name][move_mask].mean()) if move_frames else 0.0
                closing_mean = float(move["closing"][name][move_mask].mean()) if move_frames else 0.0
                row["cells"][name] = {"gazeFrames": gaze_frames, "ratio": ratio, "cos": cos_mean, "dot": dot_sum}

                csv_rows.append({
                    "event": label,
                    "startSec": "" if start is None else round(start, 3),
                    "endSec": "" if end is None else round(end, 3),
                    "eventTarget": event_target,
                    "content": content,
                    "person": person["name"],
                    "target": name,
                    "isEventTarget": int(name == event_target),
                    "eventFrames": event_frames,
                    "personFrames": present_frames,
                    "gazeFrames": gaze_frames,
                    "gazeSeconds": round(float(duration[hit].sum()), 3),
                    "interestRatio": round(ratio, 4),
                    "interestRatioPresent": round(ratio_present, 4),
                    "moveFrames": move_frames,
                    "dotSum": round(dot_sum, 4),
                    "cosMean": round(cos_mean, 4),
                    "closingSpeedMean": round(closing_mean, 4),
                })

    if not csv_rows:
        print(f"人物 '{args.person}' がいません (いる人物: {', '.join(p['name'] for p in persons.values())})")
        return 1

    output_dir = subject_dir / "Analysis" / "Events"
    output_dir.mkdir(parents=True, exist_ok=True)
    csv_path = output_dir / f"event_table_{source_name}.csv"
    html_path = csv_path.with_suffix(".html")
    markdown_path = csv_path.with_suffix(".md")

    with csv_path.open("w", encoding="utf-8-sig", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=list(csv_rows[0]))
        writer.writeheader()
        writer.writerows(csv_rows)

    html_path.write_text(render_html(title, settings, report, target_names), encoding="utf-8")
    markdown_path.write_text(render_markdown(title, settings, report, target_names), encoding="utf-8")

    print(f"\n{title}\n{settings}")
    print(render_console(report, target_names))
    print(f"\n保存しました:\n  {html_path}  (ブラウザで開く見やすい表)\n  {markdown_path}\n  {csv_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
