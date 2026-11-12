"""子どもが物体に到達したとき、それ以前の視線履歴に、到達した物体と他の物体を区別する特徴があるかを調べる。
あわせて、「この先数秒以内にどの物体へ近づくか」をリアルタイムで出すモデルを学習・評価し、JSON に書き出す。

読むもの: シーン5(5GazeExport)の書き出し(Skeleton/<実験対象者>/Analysis/Export/<骨格>/)。複数の実験・実験対象者をまとめて使える。
  gaze.csv の頭の位置・視線(頭→鼻)・肩の位置、distance.csv の物体までの水平距離、export_params.json の物体の中心と大きさ、
  あれば light_log.json(ライトの点灯)

やること(研究課題のポスターの番号):
  ② 注視可能性 … 視線を不確実性のある向きとして扱い、物体ごとに 0〜1 の注視可能性を出す
                  (視線と物体のふちとの角度に、推定誤差 --sigma 度のガウスをかける。物体の見かけの大きさも入る。遮蔽は考えない)
  ③ 到達からさかのぼる分析 … 到達(--leave m より離れていた後に --near m 以内に入った時刻)の --window 秒前から、
                  到達した物体と他の物体の注視可能性。到達ごとのヒートマップ
  ④ 特徴量 … 到達の --gap 秒前までの --window 秒間から、初期 / 最終 / 累積注視可能性・再注視回数・持続・最後に高かったときからの時間・
                  他の物体への分配と、空間情報(距離・近づく速さ・ライトの点灯。肩の向きは CSV に出すだけでモデルには使わない)。到達した物体と他の物体の差
  ⑤ 視線パターンの分類 … 到達した物体の注視可能性の時間変化を 持続 / 反復 / 初期 / 直前 / 低い に分ける
  ⑥ モデルの比較 … 到達ごとに、どの物体に到達したかを当てるモデル(条件付きロジット)
                  A 空間のみ / B 空間 + 視線 / C 空間 + 視線 + 体の向き / D 視線パターンのみ の正答率(1つ抜き交差検証)。
                  使った特徴量とアルゴリズムは report.html の「モデルの特徴量とアルゴリズム」に書く
                  何秒前の履歴まで使うかを変えたときの正答率も
  リアルタイムの予測 … --step 秒ごとに「この先 --horizon 秒以内にその物体へ到達するか」を当てるロジスティック回帰。
                  その人だけで学習(個人化)/ 他の人で学習(汎用)/ 両方 で AUC を比べる。
                  学習したモデルを models/*.json に書き出す(toolbox/arrival_model.py の OnlineArrivalPredictor で使える)

書き出すもの(毎回上書き。<データの場所>Reports/ArrivalAnalysis/。--out で別のフォルダ名にできる):
  report.html                 … 上の結果(ブラウザで開く)
  arrival_features.csv        … 到達ごと・物体ごとの特徴量
  patterns.csv                … 到達ごと・物体ごとの視線パターン
  identification.csv          … ⑥ の正答率(人物・学習のしかた・モデル)
  identification_lead.csv     … ⑥ を何秒前の履歴までにしたときの正答率
  realtime_eval.csv           … リアルタイムの予測の AUC
  realtime_predictions.csv    … リアルタイムの予測の確率(交差検証で、学習に使っていない時間の予測)
  models/realtime_<人物>.json … その人のデータだけで学習したモデル C(個人化)
  models/realtime_generic.json … 全員のデータで学習したモデル C(汎用。初めての人に使う)

使い方:
  python Tools/analyze_arrival.py
      データの場所 → シーン5の書き出しの一覧から番号で選ぶ(1,3 のように複数可。空欄で全部)
  python Tools/analyze_arrival.py --data-root assets --exports kodomobeyaOld/sou1_fix Oono/sora
  --persons child          … この名前の人物だけ使う(省略すると全員。例: 母親を除いて子どもだけ)
  --sigma 10.14 --window 10 --gap 1 --high 0.5 … 注視可能性と特徴量の設定(度, 秒, 秒, 0〜1)
  --near 0.4 --leave 1.0   … 到達の定義(m)。analyze_export.py と同じ
  --horizon 3 --step 0.5   … リアルタイムの予測で、この先何秒以内の到達を当てるか・何秒ごとに予測するか
numpy だけで動く。
"""

from __future__ import annotations

import argparse
import html
import json
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
from toolbox import home_data_root  # noqa: E402  Tools/toolbox/home_data_root.py
from toolbox.arrival_model import (  # noqa: E402  Tools/toolbox/arrival_model.py
    ARRIVAL_MODES, FEATURE_DEFINITIONS, FEATURE_LABELS, MODEL_FEATURES, MODEL_LABELS, Config, arrival_definition,
    candidate_definition, OnlineArrivalPredictor, PersonStream, Scaler, auc,
    fit_conditional_logit, fit_logistic, logistic_proba, matrix, object_radius, save_model, softmax_scores,
    stream_from_export,
)
from toolbox.export_data import (  # noqa: E402
    Person, box_distance, find_sources, load_env_objects, load_light_log, state_at, subdirs,
)
from toolbox.report_html import (  # noqa: E402  Tools/toolbox/report_html.py
    SCRIPT, SERIES_DARK, SERIES_LIGHT, STYLE, legend, num, pct, retro_chart, table_html, write_csv,
)

HEAT_BIN = 0.5
LEADS = [0.5, 1.0, 2.0, 3.0, 4.0, 5.0]
BLOCKS = 5
PATTERNS = [("Sustained", "持続", "高い状態が 3 秒以上続いた"),
            ("Recurrent", "反復", "高い状態が 2 回以上(他を見て戻った)"),
            ("Early", "初期", "高い状態が 1 回で、窓の前半"),
            ("Late", "直前", "高い状態が 1 回で、窓の後半"),
            ("Low", "低い", "高い状態が無い")]
# --arrival destination / touch のときに Config に渡す設定
EXTRA_CONFIG = ("arrival", "dwell", "face", "look", "touch_margin", "touch_min", "touch_gap")
MODEL_COLORS = {"A": "var(--muted)", "B": "var(--series-1)", "C": "var(--series-2)", "D": "var(--series-3)"}


# ------------------------------------------------------------
# 読み込み
# ------------------------------------------------------------
def list_exports(root: Path) -> list[tuple[str, str, str]]:
    found = []

    for experiment in subdirs(root):
        for subject in subdirs(root / experiment / "Skeleton"):
            for source in find_sources(root / experiment / "Skeleton" / subject / "Analysis" / "Export"):
                found.append((experiment, subject, source))

    return found


def choose_exports(found: list[tuple[str, str, str]]) -> list[tuple[str, str, str]] | None:
    print()
    for i, (e, s, src) in enumerate(found, 1):
        print(f"  {i}. {e} / {s} / {src}")

    try:
        answer = input("使う書き出しの番号 (1,3 のように複数可。空欄で全部): ").strip()
    except EOFError:
        answer = ""

    if not answer:
        return found

    chosen = []
    for part in answer.replace(",", " ").split():
        if not part.isdigit() or not 1 <= int(part) <= len(found):
            print(f"番号が正しくありません: {part}")
            return None
        chosen.append(found[int(part) - 1])

    return list(dict.fromkeys(chosen))


def load_streams(root: Path, exports: list[tuple[str, str, str]], cfg: Config, only: list[str] | None) -> list[PersonStream]:
    streams = []

    for experiment, subject, source in exports:
        folder = root / experiment / "Skeleton" / subject / "Analysis" / "Export" / source
        params = json.loads((folder / "export_params.json").read_text(encoding="utf-8-sig"))
        targets = params.get("targets", [])
        centers = {n: np.array([c["x"], c["y"], c["z"]]) for n, c in zip(targets, params.get("targetCenters", []))}
        sizes = dict(zip(targets, params.get("targetSizes", [])))

        # シーン0で物体を変えた後、シーン5で書き出し直していない(Env の物体と書き出しの物体が違う)ときは、
        # Env の物体を使い、距離はここで計算する(シーン5の distance.csv と同じ計算)
        env = load_env_objects(root / experiment)
        use_env = env is not None and env[0] and env[0] != targets

        if use_env:
            print(f"  {experiment}/{subject}: シーン5の書き出しの物体({', '.join(targets) or 'なし'})が Env の物体と違うので、"
                  f"Env の物体({', '.join(env[0])})を使い、距離を計算し直す")
            targets, centers, sizes = env
        elif len(centers) != len(targets) or len(sizes) != len(targets):
            print(f"  {experiment}/{subject}: export_params.json に物体の中心と大きさがありません。シーン5で書き出し直してください。")
            continue

        light_log = load_light_log(root / experiment / "Skeleton" / subject / "light_log.json")

        for name in params.get("persons", []):
            if only and name not in only:
                continue

            person = Person(folder, name)

            if use_env:
                for n in targets:
                    person.distance[f"dist_{n}"] = box_distance(person, centers[n], sizes[n])

            if not person.has_shoulder_columns:
                print(f"  {experiment}/{subject}/{name}: 肩の位置がありません。シーン5で書き出し直してください。")
                continue

            led = {}
            if light_log is not None:
                by_lower = {k.lower(): k for k in light_log[1]}
                led = {n: state_at(light_log[0], light_log[1][by_lower[n.lower()]], person.time) for n in targets if n.lower() in by_lower}

            stream = stream_from_export(f"{subject}/{name}", person, targets, centers, sizes, led, cfg)
            stream.experiment = experiment
            streams.append(stream)
            print(f"  {stream.key}: {len(person.time)} フレーム / 到達 {len(stream.arrivals)} 回 / 物体 {', '.join(targets)}"
                  f"(半径 {', '.join(f'{object_radius(sizes[n]):.2f}' for n in targets)} m)")

    return streams


# ------------------------------------------------------------
# ④ 特徴量 / ⑤ 分類
# ------------------------------------------------------------
def candidates(stream: PersonStream, arrived: str, t: float) -> list[str]:
    """時刻 t に到達しうる物体(前に leave m より離れたのを見ている物体)。到達した物体は必ず入れる。
    今そばにいる物体(さっき到達した物体など)は候補にしない(そこへの「到達」は定義上起こらないので)"""
    i = max(np.searchsorted(stream.time, t, side="right") - 1, 0)
    return [n for n in stream.targets if n == arrived or stream.armed[n][i]]


def arrival_rows(stream: PersonStream, gap: float) -> list[dict]:
    """到達ごと・物体ごとの特徴量(到達の gap 秒前までの履歴)。candidate = その時刻に到達しうる物体か"""
    rows = []

    for index, (arrived, t) in enumerate(stream.arrivals, 1):
        feats = stream.features(t - gap)

        if feats is None:
            continue

        can = candidates(stream, arrived, t - gap)

        for name in stream.targets:
            rows.append({"person": stream.key, "event": index, "arriveSec": t, "arrivedTarget": arrived, "target": name,
                         "arrived": int(name == arrived), "candidate": int(name in can), **feats[name]})

    return rows


def paired(rows: list[dict], feature: str) -> tuple[np.ndarray, np.ndarray]:
    """到達ごとに、到達した物体の値と、到達しうる他の物体の平均"""
    arrived, others = [], []
    keys = sorted({(r["person"], r["event"]) for r in rows})

    for key in keys:
        group = [r for r in rows if (r["person"], r["event"]) == key and r["candidate"]]
        a = [r[feature] for r in group if r["arrived"]]
        o = [r[feature] for r in group if not r["arrived"] and np.isfinite(r[feature])]

        if a and np.isfinite(a[0]) and o:
            arrived.append(a[0])
            others.append(np.mean(o))

    return np.array(arrived), np.array(others)


def bootstrap_ci(d: np.ndarray, n: int = 2000) -> tuple[float, float]:
    if len(d) < 2:
        return np.nan, np.nan
    rng = np.random.default_rng(0)
    means = d[rng.integers(0, len(d), (n, len(d)))].mean(axis=1)
    return float(np.percentile(means, 2.5)), float(np.percentile(means, 97.5))


def feature_table(rows: list[dict]) -> list[list[str]]:
    table = []

    for f in MODEL_FEATURES["C"]:
        a, o = paired(rows, f)

        if len(a) == 0:
            continue

        d = a - o
        lo, hi = bootstrap_ci(d)
        digits = 0 if f == "body" else 2
        mark = " *" if np.isfinite(lo) and (lo > 0 or hi < 0) else ""
        table.append([html.escape(FEATURE_LABELS[f]), num(a.mean(), digits), num(o.mean(), digits),
                      f"{num(d.mean(), digits)}{mark}", f"[{num(lo, digits)}, {num(hi, digits)}]",
                      pct(np.mean(a > o)), str(len(a))])

    return table


def classify(values: np.ndarray, cfg: Config) -> str:
    high = np.nan_to_num(values) >= cfg.high
    runs, start = [], None

    for i, h in enumerate(list(high) + [False]):
        if h and start is None:
            start = i
        elif not h and start is not None:
            runs.append((start, i - start))
            start = None

    if not runs:
        return "Low"
    if max(length for _, length in runs) * HEAT_BIN >= 3.0:
        return "Sustained"
    if len(runs) >= 2:
        return "Recurrent"

    s, length = runs[0]
    return "Early" if (s + length / 2) / len(values) < 0.5 else "Late"


def pattern_rows(stream: PersonStream) -> list[dict]:
    cfg = stream.cfg
    rows = []

    for index, (arrived, t) in enumerate(stream.arrivals, 1):
        can = candidates(stream, arrived, t - cfg.gap)

        for name in can:
            v = stream.score_bins(name, t - cfg.window, t - cfg.gap, HEAT_BIN)
            rows.append({"person": stream.key, "event": index, "arriveSec": t, "target": name, "arrived": int(name == arrived),
                         "pattern": classify(v, cfg)})

    return rows


def pattern_table(rows: list[dict]) -> list[list[str]]:
    table = []
    na = sum(r["arrived"] for r in rows)
    no = len(rows) - na

    for key, label, note in PATTERNS:
        a = sum(1 for r in rows if r["arrived"] and r["pattern"] == key)
        o = sum(1 for r in rows if not r["arrived"] and r["pattern"] == key)
        table.append([f"{label}({key})<br><small>{note}</small>", f"{a}({pct(a / na if na else np.nan)})",
                      f"{o}({pct(o / no if no else np.nan)})"])

    return table


# ------------------------------------------------------------
# ⑥ 到達した物体の識別
# ------------------------------------------------------------
def events(streams: list[PersonStream], gap: float) -> list[dict]:
    out = []

    for s in streams:
        for index, (arrived, t) in enumerate(s.arrivals, 1):
            feats = s.features(t - gap)

            if feats is None:
                continue

            can = candidates(s, arrived, t - gap)

            if len(can) < 2:
                continue

            out.append({"person": s.key, "event": index, "rows": [feats[n] for n in can], "chosen": can.index(arrived)})

    return out


def identify(train: list[dict], test: list[dict], model: str) -> list[np.ndarray]:
    names = MODEL_FEATURES[model]
    scaler = Scaler(np.vstack([matrix(e["rows"], names) for e in train]))
    w = fit_conditional_logit([scaler(matrix(e["rows"], names)) for e in train], [e["chosen"] for e in train])
    return [softmax_scores(scaler(matrix(e["rows"], names)), w) for e in test]


def identification(evs: list[dict], persons: list[str]) -> list[dict]:
    """人物ごとに 個人(その人の他の到達で学習・1つ抜き)/ 汎用(他の人で学習)/ 全員(他の全部の到達で学習・1つ抜き)"""
    results = []

    for person in persons + ["全員"]:
        mine = [e for e in evs if person == "全員" or e["person"] == person]

        if not mine:
            continue

        modes = ["全員"] if person == "全員" else ["個人", "汎用", "全員"]

        for mode in modes:
            for model in MODEL_FEATURES:
                probs = []

                for i, e in enumerate(mine):
                    if mode == "個人":
                        train = [x for j, x in enumerate(mine) if j != i]
                    elif mode == "汎用":
                        train = [x for x in evs if x["person"] != person]
                    else:
                        train = [x for x in evs if x is not e]

                    if len(train) < 5:
                        probs = []
                        break

                    probs.append(identify(train, [e], model)[0])

                if not probs:
                    continue

                chosen = [e["chosen"] for e in mine]
                results.append({
                    "person": person, "mode": mode, "model": model, "events": len(mine),
                    "accuracy": float(np.mean([np.argmax(p) == c for p, c in zip(probs, chosen)])),
                    "probChosen": float(np.mean([p[c] for p, c in zip(probs, chosen)])),
                    "logLoss": float(-np.mean([np.log(max(p[c], 1e-9)) for p, c in zip(probs, chosen)])),
                    "chance": float(np.mean([1 / len(p) for p in probs])),
                })

    return results


def coefficients(evs: list[dict]) -> dict[str, float]:
    names = MODEL_FEATURES["C"]
    scaler = Scaler(np.vstack([matrix(e["rows"], names) for e in evs]))
    w = fit_conditional_logit([scaler(matrix(e["rows"], names)) for e in evs], [e["chosen"] for e in evs])
    return dict(zip(names, w))


# ------------------------------------------------------------
# リアルタイムの予測
# ------------------------------------------------------------
def realtime_samples(stream: PersonStream) -> list[dict]:
    """step 秒ごと・到達しうる物体ごとに、その時刻の特徴量と「この先 horizon 秒以内にその物体へ到達したか」"""
    cfg = stream.cfg
    rows = []
    arrivals = {n: np.array([t for a, t in stream.arrivals if a == n]) for n in stream.targets}
    times = np.arange(stream.time[0] + cfg.window, stream.time[-1] - cfg.horizon, cfg.step)

    for t in times:
        feats = stream.features(t)

        if feats is None:
            continue

        i = max(np.searchsorted(stream.time, t, side="right") - 1, 0)

        for name in stream.targets:
            if not stream.armed[name][i]:
                continue

            label = int(np.any((arrivals[name] > t) & (arrivals[name] <= t + cfg.horizon)))
            rows.append({"person": stream.key, "timeSec": float(t), "target": name, "label": label, **feats[name]})

    return rows


def samples_step(rows: list[dict]) -> float:
    times = np.unique([r["timeSec"] for r in rows])
    return float(np.median(np.diff(times))) if len(times) > 1 else 1.0


def blocks_of(rows: list[dict]) -> np.ndarray:
    times = np.array([r["timeSec"] for r in rows])
    edges = np.quantile(times, np.linspace(0, 1, BLOCKS + 1))
    return np.clip(np.searchsorted(edges, times, side="right") - 1, 0, BLOCKS - 1)


def fit_rows(rows: list[dict], model: str) -> tuple[Scaler, np.ndarray]:
    names = MODEL_FEATURES[model]
    x = matrix(rows, names)
    scaler = Scaler(x)
    return scaler, fit_logistic(scaler(x), np.array([r["label"] for r in rows], dtype=float))


def realtime_eval(samples: list[dict], persons: list[str]) -> tuple[list[dict], list[dict]]:
    """人物ごと・学習のしかたごと・モデルごとの AUC と、交差検証での予測"""
    results, predictions = [], []

    for person in persons:
        mine = [r for r in samples if r["person"] == person]
        others = [r for r in samples if r["person"] != person]

        if not mine or sum(r["label"] for r in mine) == 0:
            continue

        block = blocks_of(mine)

        for mode in ["個人", "汎用", "汎用+個人"]:
            if mode != "個人" and sum(r["label"] for r in others) == 0:
                continue

            for model in MODEL_FEATURES:
                prob = np.full(len(mine), np.nan)

                for b in range(BLOCKS):
                    test = np.flatnonzero(block == b)
                    own = [mine[i] for i in np.flatnonzero(block != b)]
                    train = own if mode == "個人" else others if mode == "汎用" else others + own

                    if not test.size or sum(r["label"] for r in train) == 0:
                        continue

                    scaler, w = fit_rows(train, model)
                    prob[test] = logistic_proba(scaler(matrix([mine[i] for i in test], MODEL_FEATURES[model])), w)

                labels = np.array([r["label"] for r in mine])
                ok = np.isfinite(prob)
                results.append({"person": person, "mode": mode, "model": model, "samples": int(ok.sum()),
                                "positives": int(labels[ok].sum()), "auc": auc(prob[ok], labels[ok]),
                                "top1BeforeArrival": top1_before(mine, prob, samples_step(mine))})

                if model == "C":
                    predictions += [{"person": person, "mode": mode, "timeSec": r["timeSec"], "target": r["target"],
                                     "label": r["label"], "probability": float(p)} for r, p in zip(mine, prob)]

    return results, predictions


def top1_before(rows: list[dict], prob: np.ndarray, step: float) -> float:
    """到達の直前の予測で、到達した物体の確率が、到達しうる物体の中で最も高かった割合"""
    by_time: dict[float, dict[str, float]] = {}

    for r, p in zip(rows, prob):
        if np.isfinite(p):
            by_time.setdefault(r["timeSec"], {})[r["target"]] = p

    # 1回の到達のラベル 1 は horizon 秒のあいだ続くので、続いている最後の時刻(到達の直前)だけを使う
    groups: list[tuple[str, float]] = []

    for t, name in sorted((r["timeSec"], r["target"]) for r in rows if r["label"]):
        if groups and groups[-1][0] == name and t - groups[-1][1] <= step * 1.5:
            groups[-1] = (name, t)
        else:
            groups.append((name, t))

    hits = [max(by_time[t], key=by_time[t].get) == name for name, t in groups if name in by_time.get(t, {})]
    return float(np.mean(hits)) if hits else np.nan


# ------------------------------------------------------------
# HTML
# ------------------------------------------------------------
def heat_strip(stream: PersonStream, chart_id: str) -> str:
    """② セッション全体の注視可能性(物体ごとの行。濃いほど高い)と到達の印"""
    t0, t1 = float(stream.time[0]), float(stream.time[-1])
    width, left, right, row_h = 960, 70, 12, 18
    cols = 480
    plot_w = width - left - right
    edges = np.linspace(t0, t1, cols + 1)
    x = lambda t: left + (t - t0) / max(t1 - t0, 1e-6) * plot_w
    top = 16
    height = top + len(stream.targets) * row_h + 30
    parts = [f"<svg viewBox='0 0 {width} {height}' class='chart' id='{chart_id}' role='img' aria-label='物体ごとの注視可能性の時系列'>"]

    for k, name in enumerate(stream.targets):
        y = top + k * row_h
        parts.append(f"<text x='{left - 6}' y='{y + row_h * 0.7}' class='label' text-anchor='end'>{html.escape(name)}</text>")
        values = [stream.mean_score(name, a, b) for a, b in zip(edges[:-1], edges[1:])]

        for a, b, v in zip(edges[:-1], edges[1:], values):
            if np.isfinite(v) and v >= 0.02:
                parts.append(f"<rect x='{x(a):.1f}' y='{y + 1}' width='{x(b) - x(a) + 0.3:.1f}' height='{row_h - 2}' "
                             f"fill='var(--series-{k + 1})' fill-opacity='{v:.2f}'><title>{html.escape(name)} {a:.0f}〜{b:.0f} 秒: {v:.2f}</title></rect>")

    for name, t in stream.arrivals:
        k = stream.targets.index(name)
        parts.append(f"<path d='M{x(t) - 4:.1f},{top - 10} L{x(t) + 4:.1f},{top - 10} L{x(t):.1f},{top - 3} Z' fill='var(--series-{k + 1})'>"
                     f"<title>{html.escape(name)} に到達 {t:.1f} 秒</title></path>")

    axis_y = top + len(stream.targets) * row_h
    for tick in np.linspace(t0, t1, 7):
        parts.append(f"<text x='{x(tick):.1f}' y='{axis_y + 16}' class='tick' text-anchor='middle'>{tick:.0f}</text>")
    parts.append(f"<text x='{width - right}' y='{axis_y + 28}' class='tick' text-anchor='end'>時刻 (秒)。▼ = 到達</text></svg>")
    return "".join(parts)


def trial_heatmap(stream: PersonStream, arrived: str, t: float) -> str:
    """③ 1回の到達の、到達前 window 秒の注視可能性(物体ごとの行)。破線より右は ④ の特徴量に使わない"""
    cfg = stream.cfg
    width, left, right, row_h = 260, 58, 6, 14
    plot_w = width - left - right
    n = int(round(cfg.window / HEAT_BIN))
    height = len(stream.targets) * row_h + 20
    parts = [f"<svg viewBox='0 0 {width} {height}' class='chart heat' role='img' aria-label='到達前の注視可能性'>"]

    for k, name in enumerate(stream.targets):
        y = k * row_h
        weight = " font-weight='700'" if name == arrived else ""
        parts.append(f"<text x='{left - 4}' y='{y + row_h * 0.75}' class='label' text-anchor='end'{weight}>{html.escape(name)}</text>")
        parts.append(f"<rect x='{left}' y='{y + 1}' width='{plot_w}' height='{row_h - 2}' fill='var(--grid)' fill-opacity='0.35'/>")

        for i, v in enumerate(stream.score_bins(name, t - cfg.window, t, HEAT_BIN)):
            if np.isfinite(v) and v >= 0.02:
                parts.append(f"<rect x='{left + i * plot_w / n:.1f}' y='{y + 1}' width='{plot_w / n + 0.3:.1f}' height='{row_h - 2}' "
                             f"fill='var(--series-{k + 1})' fill-opacity='{v:.2f}'><title>{html.escape(name)} "
                             f"{-cfg.window + i * HEAT_BIN:g}〜{-cfg.window + (i + 1) * HEAT_BIN:g} 秒: {v:.2f}</title></rect>")

    gx = left + (cfg.window - cfg.gap) / cfg.window * plot_w
    bottom = len(stream.targets) * row_h
    parts.append(f"<line x1='{gx:.1f}' x2='{gx:.1f}' y1='0' y2='{bottom}' class='axis' stroke-dasharray='3 2'/>")
    parts.append(f"<text x='{left}' y='{bottom + 14}' class='tick'>-{cfg.window:g}s</text>"
                 f"<text x='{width - right}' y='{bottom + 14}' class='tick' text-anchor='end'>到達</text></svg>")
    return "".join(parts)


def retro_section(stream: PersonStream) -> str:
    cfg = stream.cfg
    points = np.round(np.arange(-cfg.window, 1e-6, 1.0), 6)
    curves = []

    for arrived, t in stream.arrivals:
        values = {n: np.array([stream.mean_score(n, t + r - 1.0, t + r) for r in points]) for n in stream.targets}
        curves.append((arrived, t, values))

    if not curves:
        return "<p class='note'>到達がありませんでした。</p>"

    with np.errstate(all="ignore"):
        arrived_mean = np.nanmean([v[a] for a, _, v in curves], axis=0)
        others_mean = np.nanmean([v[n] for a, _, v in curves for n in stream.targets if n != a], axis=0)

    s = ["<div class='multiples'>"]
    s.append("<div class='card mean'><div class='card-title'>全部の到達の平均</div>" + retro_chart(points, [
        {"name": "到達した物体", "values": arrived_mean, "color": "var(--text)", "emphasized": True, "label": "到達した物体"},
        {"name": "到達しなかった物体", "values": others_mean, "color": "var(--muted)", "dashed": True, "label": "他の物体"},
    ], "到達した物体と他の物体の注視可能性の平均") + "</div>")

    for k, name in enumerate(stream.targets):
        group = [v for a, _, v in curves if a == name]

        if not group:
            continue

        with np.errstate(all="ignore"):
            mean = {n: np.nanmean([v[n] for v in group], axis=0) for n in stream.targets}

        series = [{"name": n, "values": mean[n], "color": f"var(--series-{j + 1})", "emphasized": n == name,
                   "label": f"{n}(到達)" if n == name else n} for j, n in enumerate(stream.targets)]
        s.append(f"<div class='card'><div class='card-title'>{html.escape(name)} に到達({len(group)} 回)の平均</div>"
                 + retro_chart(points, series, f"{name} に到達した回の注視可能性の平均") + "</div>")

    s.append("</div><h4>到達ごとのヒートマップ</h4>"
             f"<p class='note'>行 = 物体(太字が到達した物体)。濃いほど注視可能性が高い。破線より右(到達の {cfg.gap:g} 秒前から)は ④〜⑥ に使わない。</p>"
             "<div class='heat-grid'>")

    for index, (arrived, t, _) in enumerate(curves, 1):
        s.append(f"<div class='card'><div class='card-title'>{index}回目 {html.escape(arrived)}({t:.1f} 秒)</div>"
                 f"{trial_heatmap(stream, arrived, t)}</div>")

    s.append("</div>")
    return "".join(s)


def line_chart(xs: list[float], series: list[dict], aria: str, x_label: str, y_label: str,
               y_range: tuple[float, float] = (0.0, 1.0)) -> str:
    width, height, left, right, top, bottom = 520, 240, 44, 110, 14, 40
    plot_w, plot_h = width - left - right, height - top - bottom
    lo, hi = y_range
    x = lambda v: left + (v - xs[0]) / max(xs[-1] - xs[0], 1e-6) * plot_w
    y = lambda v: top + (hi - min(max(v, lo), hi)) / (hi - lo) * plot_h
    parts = [f"<svg viewBox='0 0 {width} {height}' class='chart retro' role='img' aria-label='{html.escape(aria)}'>"]

    for v in np.linspace(lo, hi, 3):
        parts.append(f"<line x1='{left}' x2='{left + plot_w}' y1='{y(v):.1f}' y2='{y(v):.1f}' class='grid'/>"
                     f"<text x='{left - 6}' y='{y(v) + 4:.1f}' class='tick' text-anchor='end'>{v:g}</text>")

    for v in xs:
        parts.append(f"<text x='{x(v):.1f}' y='{top + plot_h + 16}' class='tick' text-anchor='middle'>{v:g}</text>")

    parts.append(f"<text x='{left + plot_w / 2:.1f}' y='{height - 6}' class='tick' text-anchor='middle'>{html.escape(x_label)}</text>")
    parts.append(f"<text x='{left}' y='{top - 4}' class='tick'>{html.escape(y_label)}</text>")
    labels = []

    for sr in series:
        pts = [(x(a), y(b)) for a, b in zip(xs, sr["values"]) if np.isfinite(b)]
        dash = " stroke-dasharray='5 4'" if sr.get("dashed") else ""
        if len(pts) > 1:
            parts.append(f"<polyline points='{' '.join(f'{a:.1f},{b:.1f}' for a, b in pts)}' fill='none' stroke='{sr['color']}' "
                         f"stroke-width='{2.5 if sr.get('emphasized') else 1.8}'{dash}/>")
        if not sr.get("dashed"):
            for (a, b), v in zip(pts, [v for v in sr["values"] if np.isfinite(v)]):
                parts.append(f"<circle cx='{a:.1f}' cy='{b:.1f}' r='3.5' fill='{sr['color']}' stroke='var(--surface)' stroke-width='1.5'>"
                             f"<title>{html.escape(sr['name'])}: {v:.2f}</title></circle>")
        if pts:
            labels.append([pts[-1][1], sr["name"]])

    labels.sort()
    for i in range(1, len(labels)):
        labels[i][0] = max(labels[i][0], labels[i - 1][0] + 13)
    for ly, text in labels:
        parts.append(f"<text x='{left + plot_w + 8}' y='{ly + 4:.1f}' class='label'>{html.escape(text)}</text>")

    parts.append("</svg>")
    return "".join(parts)


def coefficient_chart(weights: dict[str, float]) -> str:
    """モデル C の重み(標準化した特徴量 1 つ分あたり。+ は到達した物体ほど大きい)"""
    width, left, row_h = 640, 300, 22
    plot_w = width - left - 60
    top_v = max(max(abs(v) for v in weights.values()), 0.1)
    mid = left + plot_w / 2
    height = len(weights) * row_h + 24
    parts = [f"<svg viewBox='0 0 {width} {height}' class='chart' role='img' aria-label='モデル C の重み'>"]

    for i, (name, v) in enumerate(weights.items()):
        y = i * row_h
        w = v / top_v * plot_w / 2
        color = "var(--series-1)" if v >= 0 else "var(--series-8)"
        parts.append(f"<text x='{left - 8}' y='{y + row_h * 0.68}' class='label' text-anchor='end'>{html.escape(FEATURE_LABELS[name])}</text>")
        parts.append(f"<rect x='{min(mid, mid + w):.1f}' y='{y + 4}' width='{max(abs(w), 0.5):.1f}' height='{row_h - 8}' rx='2' fill='{color}'>"
                     f"<title>{v:+.2f}</title></rect>")
        parts.append(f"<text x='{mid + w + (6 if v >= 0 else -6):.1f}' y='{y + row_h * 0.68}' class='value' "
                     f"text-anchor='{'start' if v >= 0 else 'end'}'>{v:+.2f}</text>")

    parts.append(f"<line x1='{mid}' x2='{mid}' y1='0' y2='{len(weights) * row_h}' class='axis'/>")
    parts.append(f"<text x='{mid}' y='{height - 4}' class='tick' text-anchor='middle'>← 到達しにくい / 到達しやすい →</text></svg>")
    return "".join(parts)


def prob_timeline(stream: PersonStream, predictions: list[dict], mode: str) -> str:
    """リアルタイムの予測の確率(物体ごとの線)と到達の印"""
    rows = [p for p in predictions if p["person"] == stream.key and p["mode"] == mode and np.isfinite(p["probability"])]

    if not rows:
        return ""

    t0, t1 = float(stream.time[0]), float(stream.time[-1])
    width, left, right, top, plot_h = 960, 44, 12, 14, 150
    plot_w = width - left - right
    x = lambda t: left + (t - t0) / max(t1 - t0, 1e-6) * plot_w
    y = lambda v: top + (1 - v) * plot_h
    parts = [f"<svg viewBox='0 0 {width} {top + plot_h + 34}' class='chart' role='img' aria-label='到達の予測の確率の時系列'>"]

    for v in (0, 0.5, 1):
        parts.append(f"<line x1='{left}' x2='{width - right}' y1='{y(v):.1f}' y2='{y(v):.1f}' class='grid'/>"
                     f"<text x='{left - 6}' y='{y(v) + 4:.1f}' class='tick' text-anchor='end'>{v:g}</text>")

    step = stream.cfg.step

    for k, name in enumerate(stream.targets):
        pts = sorted((p["timeSec"], p["probability"]) for p in rows if p["target"] == name)
        seg, prev = [], None

        for t, v in pts + [(None, None)]:
            if t is not None and (prev is None or t - prev <= step * 1.5):
                seg.append(f"{x(t):.1f},{y(v):.1f}")
            else:
                if len(seg) > 1:
                    parts.append(f"<polyline points='{' '.join(seg)}' fill='none' stroke='var(--series-{k + 1})' stroke-width='1.4'/>")
                seg = [f"{x(t):.1f},{y(v):.1f}"] if t is not None else []
            prev = t

    for name, t in stream.arrivals:
        k = stream.targets.index(name)
        parts.append(f"<line x1='{x(t):.1f}' x2='{x(t):.1f}' y1='{top}' y2='{top + plot_h}' stroke='var(--series-{k + 1})' "
                     f"stroke-dasharray='2 2'><title>{html.escape(name)} に到達 {t:.1f} 秒</title></line>")

    for tick in np.linspace(t0, t1, 7):
        parts.append(f"<text x='{x(tick):.1f}' y='{top + plot_h + 16}' class='tick' text-anchor='middle'>{tick:.0f}</text>")
    parts.append(f"<text x='{width - right}' y='{top + plot_h + 30}' class='tick' text-anchor='end'>時刻 (秒)。縦の破線 = 到達</text></svg>")
    return "".join(parts)


def method_section(cfg: Config) -> str:
    """モデルに使った特徴量(どのモデルに入っているか)と、学習・評価のアルゴリズム"""
    used = [f for m in MODEL_FEATURES.values() for f in m]
    names = list(dict.fromkeys(used))
    group = {f: "空間" for f in MODEL_FEATURES["A"]} | {f: "視線" for f in MODEL_FEATURES["D"]} | {"body": "体の向き"}
    rows = [[html.escape(FEATURE_LABELS[f]), group.get(f, ""), f"<code>{f}</code>", html.escape(FEATURE_DEFINITIONS[f]),
             *("●" if f in MODEL_FEATURES[m] else "" for m in MODEL_FEATURES)] for f in names]
    s = ["<h2>モデルの特徴量とアルゴリズム</h2>",
         "<p class='note'>" + " / ".join(f"<b>{html.escape(MODEL_LABELS[m])}</b>({len(MODEL_FEATURES[m])} 個)" for m in MODEL_FEATURES) + "</p>",
         table_html(["特徴量", "種類", "列名", "求め方", *MODEL_FEATURES], rows),
         f"<p class='note'>s = 注視可能性 = exp(−e² / 2σ²)、e = max(0, 視線と物体の中心の角度 − asin(半径 / 距離))、σ = {cfg.sigma:g} 度。"
         f"視線は頭→鼻の向き(シーン5の書き出し。下向き補正済み)を、その時刻までの {cfg.smooth:g} 秒の中央値と平均で滑らかにしたもの。"
         f"物体は箱の3辺の平均の半分を半径とする球とみなす。T = {cfg.window:g} 秒、high = {cfg.high:g}、merge = {cfg.merge:g} 秒。</p>",
         "<h3>⑥ 到達した物体の識別</h3><ul class='note'>"
         f"<li>1 件 = 到達 1 回。特徴量を求める時刻 t = 到達の {cfg.gap:g} 秒前(グラフでは 0.5〜5 秒前も)。</li>"
         f"<li>{html.escape(arrival_definition(cfg))}。</li>"
         f"<li>候補 = t に到達しうる物体({candidate_definition(cfg)})。到達した物体は必ず入れる。候補が 1 つの到達は除く。</li>"
         "<li>アルゴリズム: 条件付きロジット(多項ロジットの、候補ごとに特徴量が違う形)。P(物体 j) = exp(x_j·w) / Σ_k exp(x_k·w)。"
         "全物体で共通の重み w を1組学習する(物体ごとの切片は無い)。</li>"
         "<li>前処理: 学習データの候補全体で各特徴量を平均 0・標準偏差 1 にそろえ、欠けている値(nan)は 0(=平均)にする。</li>"
         "<li>学習: 対数尤度 − λ/2 |w|²(L2 正則化、λ = 1)をニュートン法で最大化。</li>"
         "<li>評価: 個人 = その人の到達で1つ抜き交差検証 / 汎用 = 他の人の到達だけで学習 / 全員 = 全員の到達で1つ抜き交差検証。"
         "正答率 = 確率が最も高い候補が到達した物体だった割合。当て推量 = 1 / 候補の数 の平均。</li></ul>",
         "<h3>リアルタイムの予測</h3><ul class='note'>"
         f"<li>1 件 = {cfg.step:g} 秒ごとの時刻 t × その時刻に到達しうる物体。特徴量は ⑥ と同じ(t までのデータだけ)。</li>"
         f"<li>ラベル = t から {cfg.horizon:g} 秒以内にその物体へ到達したら 1、しなければ 0。</li>"
         "<li>アルゴリズム: 切片つきロジスティック回帰 P = 1 / (1 + exp(−(x·w + b)))。前処理は ⑥ と同じ。L2 正則化(λ = 1、切片にはかけない)でニュートン法。</li>"
         f"<li>評価: 個人 = その人の時間を {BLOCKS} つの連続した区間に分け、1 区間ずつ残りで学習して予測 / 汎用 = 他の人だけで学習 / "
         "汎用+個人 = 他の人 + その人の残りの区間で学習。AUC と、到達の直前に到達した物体の確率が1位だった割合。</li>"
         "<li>書き出すモデル(models/*.json)はモデル C。</li></ul>"]
    return "".join(s)


EXTRA_STYLE = """
ul.note { color: var(--text-2); font-size: 0.9rem; margin: 4px 0 10px; padding-left: 20px; } ul.note li { margin: 2px 0; }
code { font-size: 0.85em; }
.chart.heat { max-width: 260px; }
.heat-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(min(100%, 250px), 1fr)); gap: 8px; }
.heat-grid .card { margin: 0; padding: 8px 10px; }
.key { display: grid; grid-template-columns: repeat(auto-fill, minmax(min(100%, 220px), 1fr)); gap: 10px; }
.key .card { margin: 0; } .key b { display: block; font-size: 1.4rem; font-variant-numeric: tabular-nums; }
.key span { color: var(--text-2); font-size: 0.85rem; }
"""


# ------------------------------------------------------------
# main
# ------------------------------------------------------------
def main() -> None:
    defaults = Config()
    parser = argparse.ArgumentParser(description="到達した物体と他の物体を、到達前の視線履歴で区別できるかを調べ、リアルタイムの予測モデルを作る")
    parser.add_argument("--exports", nargs="+", metavar="実験/実験対象者[/骨格]", help="使う書き出し(省略すると一覧から選ぶ)")
    parser.add_argument("--persons", nargs="+", help="この名前の人物だけ使う(例: child)")
    parser.add_argument("--out", default="ArrivalAnalysis", help="書き出すフォルダの名前(<データの場所>Reports/<ここ>。例: ArrivalAnalysis_sora)")
    for name in ("sigma", "window", "gap", "high", "near", "leave", "horizon", "step", "smooth"):
        parser.add_argument(f"--{name}", type=float, default=getattr(defaults, name))
    parser.add_argument("--arrival", choices=ARRIVAL_MODES, default=defaults.arrival,
                        help="到達の決め方: approach 近づいた / destination 終着点(近づいてとどまり、向くか見る)/ touch 手で触れた")
    for name in ("dwell", "face", "look", "touch_margin", "touch_min", "touch_gap"):
        parser.add_argument(f"--{name.replace('_', '-')}", dest=name, type=float, default=getattr(defaults, name))
    home_data_root.add_argument(parser)
    args = parser.parse_args()

    root = home_data_root.choose_data_root(args.data_root)
    if root is None:
        return

    found = list_exports(root)
    if not found:
        print("シーン5(5GazeExport)の書き出しがありません。")
        return

    if args.exports:
        exports = [f for f in found if any(f"{e}/{s}/{src}".startswith(x.strip("/")) for x in args.exports for e, s, src in [f])]
    else:
        exports = choose_exports(found)
    if not exports:
        print("書き出しが選ばれていません。")
        return

    cfg = Config(**{k: getattr(args, k) for k in ("sigma", "window", "gap", "high", "near", "leave", "horizon", "step", "smooth", *EXTRA_CONFIG)})
    print("\n読み込み:")
    streams = load_streams(root, exports, cfg, args.persons)
    streams = [s for s in streams if s.arrivals]
    if not streams:
        print("到達のある人物がいません。")
        return

    persons = [s.key for s in streams]
    out_dir = home_data_root.report_root(root) / (args.out if args.out != "ArrivalAnalysis" or cfg.arrival == "approach" else f"ArrivalAnalysis_{cfg.arrival}")
    (out_dir / "models").mkdir(parents=True, exist_ok=True)

    # ④ ⑤
    feature_rows = [r for s in streams for r in arrival_rows(s, cfg.gap)]
    patterns = [r for s in streams for r in pattern_rows(s)]

    # ⑥
    print("\n⑥ 到達した物体の識別 …")
    evs = events(streams, cfg.gap)
    ident = identification(evs, persons)
    lead_rows = []
    for g in LEADS:
        ev_g = events(streams, g)
        for r in identification(ev_g, []):
            lead_rows.append({**r, "gapSec": g})
    weights = coefficients(evs)

    # リアルタイム
    print("リアルタイムの予測 …")
    samples = [r for s in streams for r in realtime_samples(s)]
    rt_results, rt_predictions = realtime_eval(samples, persons)

    for s in streams:
        own = [r for r in samples if r["person"] == s.key]
        if sum(r["label"] for r in own):
            scaler, w = fit_rows(own, "C")
            save_model(out_dir / "models" / f"realtime_{s.key.replace('/', '_')}.json", "logistic", MODEL_FEATURES["C"], scaler, w, cfg,
                       {"person": s.key, "trainedOn": [s.key], "samples": len(own)})
    scaler, w = fit_rows(samples, "C")
    save_model(out_dir / "models" / "realtime_generic.json", "logistic", MODEL_FEATURES["C"], scaler, w, cfg,
               {"person": "generic", "trainedOn": persons, "samples": len(samples)})

    # リアルタイム用のクラスが、オフラインの計算と同じ確率を出すか(最初の人の 60 秒)
    online_note = check_online(streams[0], out_dir / "models" / f"realtime_{streams[0].key.replace('/', '_')}.json")

    # ---- HTML
    color_note = "色は物体ごとに固定(データごとに 1 番目・2 番目・3 番目の物体)。"
    pooled = [r for r in ident if r["person"] == "全員"]
    best = {r["model"]: r for r in pooled}
    key = ["<div class='key'>"]
    for model in MODEL_FEATURES:
        if model in best:
            key.append(f"<div class='card'><span>⑥ モデル {model} の正答率(全員・1つ抜き)</span><b>{pct(best[model]['accuracy'])}</b>"
                       f"<span>当て推量 {pct(best[model]['chance'])} / {best[model]['events']} 回の到達</span></div>")
    rt_c = [r for r in rt_results if r["model"] == "C" and r["mode"] == "汎用+個人"]
    if rt_c:
        key.append(f"<div class='card'><span>リアルタイム予測 C の AUC(汎用+個人, 人物の平均)</span>"
                   f"<b>{num(np.nanmean([r['auc'] for r in rt_c]), 2)}</b><span>0.5 = 当て推量 / 1 = 完全</span></div>")
    key.append("</div>")

    sec = [f"<h2>① データ</h2><p class='note'>{len(streams)} 人・到達 {sum(len(s.arrivals) for s in streams)} 回。"
           f"{html.escape(arrival_definition(cfg))}。</p>"]
    sec.append(table_html(["人物", "時間 (秒)", "物体", "到達 (回)", "物体ごとの到達"],
                          [[html.escape(s.key), num(s.time[-1] - s.time[0], 0), html.escape(", ".join(s.targets)), str(len(s.arrivals)),
                            ", ".join(f"{html.escape(n)} {sum(1 for a, _ in s.arrivals if a == n)}" for n in s.targets)] for s in streams]))

    sec.append("<h2>② 視線の不確実性を考えた注視可能性</h2>"
               f"<p class='note'>注視可能性 = exp(−e² / 2σ²)、e = max(0, 視線と物体の中心の角度 − 物体の見かけの半径)、σ = {cfg.sigma:g} 度。"
               "視線(頭→鼻の向き)が物体に当たっていれば 1。物体の見かけの大きさ(近いほど大きい)も入る。遮蔽は考えない。"
               f"視線は、その時刻までの {cfg.smooth:g} 秒の中央値と平均で滑らかにしてある(リアルタイムと同じ計算)。{color_note}</p>")
    for i, s in enumerate(streams):
        sec.append(f"<h3>{html.escape(s.key)}</h3>" + legend(s.targets, {n: k for k, n in enumerate(s.targets)})
                   + f"<div class='card'>{heat_strip(s, f'hs-{i}')}</div>")

    sec.append(f"<h2>③ 到達時刻からさかのぼる分析</h2><p class='note'>各点 = その時刻までの 1 秒間の注視可能性の平均。{color_note}</p>")
    for s in streams:
        sec.append(f"<h3>{html.escape(s.key)}</h3>" + legend(s.targets, {n: k for k, n in enumerate(s.targets)}) + retro_section(s))

    sec.append(f"<h2>④ 視線パターンの特徴量</h2><p class='note'>到達の {cfg.gap:g} 秒前までの {cfg.window:g} 秒間の視線履歴と、その時刻の空間情報。"
               f"他の物体 = その時刻に到達しうる物体({candidate_definition(cfg)})。"
               "到達ごとに「到達した物体の値 − 他の物体の平均」を出し、その平均と 95% 信頼区間(到達を選び直すブートストラップ)。"
               "* は信頼区間が 0 をまたがないもの。「到達した物体の方が大きい」= 到達ごとに到達した物体の値が他の平均より大きかった割合。</p>")
    header = ["特徴量", "到達した物体", "他の物体", "差", "差の 95% 区間", "到達した物体の方が大きい", "到達 (回)"]
    for person in persons + ["全員"]:
        rows = [r for r in feature_rows if person == "全員" or r["person"] == person]
        sec.append(f"<h3>{html.escape(person)}</h3>" + table_html(header, feature_table(rows)))

    sec.append(f"<h2>⑤ 視線パターンの分類</h2><p class='note'>到達の {cfg.window:g} 秒前から {cfg.gap:g} 秒前までの注視可能性を {HEAT_BIN:g} 秒ごとに平均し、"
               f"{cfg.high:g} 以上を「高い」として分ける(上から順に当てはめる)。到達しなかった物体(到達しうる物体だけ)も同じ時間で分けて比べる。</p>")
    for person in persons + ["全員"]:
        rows = [r for r in patterns if person == "全員" or r["person"] == person]
        sec.append(f"<h3>{html.escape(person)}</h3>" + table_html(["パターン", "到達した物体", "到達しなかった物体"], pattern_table(rows)))

    sec.append(method_section(cfg))
    sec.append("<h2>⑥ モデルによる比較(到達した物体の識別)</h2>"
               "<p class='note'>到達ごとに、候補の物体(到達しうる物体。候補が 1 つの到達は除く)のどれに到達したかを当てる(条件付きロジット。特徴量は標準化し、L2 正則化)。"
               "個人 = その人の他の到達で学習(1つ抜き)/ 汎用 = 他の人の到達だけで学習 / 全員 = 他の全部の到達で学習(1つ抜き)。"
               "正答率 = 確率が最も高い物体が当たった割合。到達した物体の確率 = その平均。</p>")
    sec.append(table_html(["人物", "学習", "モデル", "到達 (回)", "正答率", "到達した物体の確率", "当て推量"],
                          [[html.escape(r["person"]), r["mode"], r["model"], str(r["events"]), pct(r["accuracy"]), num(r["probChosen"], 2),
                            pct(r["chance"])] for r in ident]))
    sec.append("<h3>何秒前までの履歴で当てられるか(全員・1つ抜き)</h3>")
    sec.append("<div class='card'>" + line_chart(LEADS, [
        {"name": MODEL_LABELS[m][:1], "values": [next((r["accuracy"] for r in lead_rows if r["gapSec"] == g and r["model"] == m), np.nan) for g in LEADS],
         "color": MODEL_COLORS[m], "emphasized": m == "C"} for m in MODEL_FEATURES] + [
        {"name": "当て推量", "values": [next((r["chance"] for r in lead_rows if r["gapSec"] == g), np.nan) for g in LEADS],
         "color": "var(--muted)", "dashed": True}], "到達の何秒前までの履歴を使うかと正答率", "到達の何秒前まで (秒)", "正答率") + "</div>")
    sec.append("<div class='legend'>" + "".join(f"<span><i style='background:{MODEL_COLORS[m]}'></i>{html.escape(MODEL_LABELS[m])}</span>"
                                                for m in MODEL_FEATURES) + "</div>")
    sec.append("<h3>モデル C の重み(全員の到達で学習)</h3><p class='note'>特徴量を標準化したときの係数。+ は値が大きい物体ほど到達先に選ばれやすい。</p>")
    sec.append(f"<div class='card'>{coefficient_chart(weights)}</div>")

    sec.append("<h2>リアルタイムの予測(今後のシステム)</h2>"
               f"<p class='note'>{cfg.step:g} 秒ごとに、到達しうる物体({candidate_definition(cfg)})それぞれについて、"
               f"「この先 {cfg.horizon:g} 秒以内に到達するか」をロジスティック回帰で当てる。特徴量はその時刻までのデータだけから求める。"
               f"個人 = その人の他の時間({BLOCKS} 分割)で学習 / 汎用 = 他の人だけで学習 / 汎用+個人 = 両方。"
               "AUC = 到達する物体の確率が、しない物体より高く出る割合(0.5 で当て推量)。"
               "到達直前の 1 位 = 到達の直前の予測で、到達した物体の確率が最も高かった割合。</p>")
    sec.append(table_html(["人物", "学習", "モデル", "予測の数", "到達の数(ラベル 1)", "AUC", "到達直前の 1 位"],
                          [[html.escape(r["person"]), r["mode"], r["model"], str(r["samples"]), str(r["positives"]), num(r["auc"], 2),
                            pct(r["top1BeforeArrival"])] for r in rt_results]))
    sec.append(f"<p class='note'>{html.escape(online_note)}</p>")
    for s in streams:
        timeline = prob_timeline(s, rt_predictions, "汎用+個人")
        if timeline:
            sec.append(f"<h3>{html.escape(s.key)}(モデル C・汎用+個人。学習に使っていない時間の予測)</h3>"
                       + legend(s.targets, {n: k for k, n in enumerate(s.targets)}) + f"<div class='card'>{timeline}</div>")
    sec.append("<p class='note'>書き出したモデル: models/realtime_&lt;人物&gt;.json(個人化)、models/realtime_generic.json(汎用)。"
               "Tools/toolbox/arrival_model.py の OnlineArrivalPredictor に渡すと、フレームを入れるたびに物体ごとの確率を返す。</p>")

    light_vars = " ".join(f"--series-{i + 1}: {c};" for i, c in enumerate(SERIES_LIGHT))
    dark_vars = " ".join(f"--series-{i + 1}: {c};" for i, c in enumerate(SERIES_DARK))
    settings = (f"σ {cfg.sigma:g}° / 履歴 {cfg.window:g} 秒(到達の {cfg.gap:g} 秒前まで)/ 高い ≥ {cfg.high:g} / 到達 {cfg.near:g} m(離れる {cfg.leave:g} m)/ "
                f"リアルタイム {cfg.horizon:g} 秒先・{cfg.step:g} 秒ごと")
    page = (f"<!doctype html><html lang='ja'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width, initial-scale=1'>"
            f"<title>到達前の視線履歴</title><style>{STYLE.replace('%LIGHT%', light_vars).replace('%DARK%', dark_vars)}{EXTRA_STYLE}</style>"
            f"<script>{SCRIPT}</script></head><body><main>"
            f"<h1>到達前の視線履歴 — 到達した物体と他の物体を区別できるか</h1><p class='meta'>{html.escape(settings)}</p>"
            f"<p class='meta'>使った書き出し: {html.escape(', '.join('/'.join(e) for e in exports))}</p>"
            + "".join(key) + "".join(sec) + "</main></body></html>")
    (out_dir / "report.html").write_text(page, encoding="utf-8")

    write_csv(out_dir / "arrival_features.csv", feature_rows)
    write_csv(out_dir / "patterns.csv", patterns)
    write_csv(out_dir / "identification.csv", ident)
    write_csv(out_dir / "identification_lead.csv", lead_rows)
    write_csv(out_dir / "realtime_eval.csv", rt_results)
    write_csv(out_dir / "realtime_predictions.csv", rt_predictions)

    print("\n⑥ 到達した物体の識別(正答率):")
    for r in ident:
        print(f"  {r['person']:>22s} {r['mode']:>3s} {r['model']}  {pct(r['accuracy']):>4s}  (当て推量 {pct(r['chance'])}, {r['events']} 回)")
    print("\nリアルタイムの予測(AUC):")
    for r in rt_results:
        print(f"  {r['person']:>22s} {r['mode']:>6s} {r['model']}  {num(r['auc'], 2)}")
    print(f"\n{online_note}")
    print(f"\n→ {out_dir / 'report.html'}")


def check_online(stream: PersonStream, model_path: Path, seconds: float = 60.0) -> str:
    """OnlineArrivalPredictor にフレームを1つずつ入れたときの確率が、オフラインの計算と合うかを確かめる"""
    if not model_path.exists():
        return ""

    model = json.loads(model_path.read_text(encoding="utf-8"))
    names = model["features"]
    mean, std, w = np.array(model["mean"]), np.array(model["std"]), np.array(model["weights"])
    predictor = OnlineArrivalPredictor(model_path, stream.objects)
    p = stream.person
    start = stream.time[0] + stream.cfg.window + 5
    diffs = []
    next_check = start

    for i in np.flatnonzero((stream.time >= stream.time[0]) & (stream.time <= start + seconds)):
        valid_dir = bool(p.gaze["hasGaze"][i] == 1) and np.isfinite(p.direction[i]).all()
        predictor.add_frame(float(stream.time[i]), p.origin[i], p.direction[i] if valid_dir else None,
                            p.shoulder_left[i], p.shoulder_right[i],
                            {n: float(stream.dist[n][i]) for n in stream.targets},
                            {n: bool(stream.led[n][i]) for n in stream.targets}, bool(p.present[i]), float(p.dt[i]))

        if stream.time[i] >= next_check:
            next_check += 1.0
            online = predictor.predict(armed={n: True for n in stream.targets})
            feats = stream.features(float(stream.time[i]))
            if feats is None:
                continue
            x = np.nan_to_num((matrix([feats[n] for n in stream.targets], names) - mean) / std)
            offline = logistic_proba(x, w)
            diffs += [abs(online[n] - o) for n, o in zip(stream.targets, offline)]

    if not diffs:
        return ""
    return (f"リアルタイム用の OnlineArrivalPredictor の確認({stream.key} の {seconds:g} 秒・1 秒ごと): "
            f"オフラインの計算との差 平均 {np.mean(diffs):.3f} / 最大 {np.max(diffs):.3f}"
            "(直近のフレームだけで計算するため、体の向きの前後のそろえ方などでわずかに違うことがある)")


if __name__ == "__main__":
    main()
