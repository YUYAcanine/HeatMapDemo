"""動画で確認した到達(物体に興味をもって近づき、触れたり遊んだりしたもの)だけを指定して、到達前の視線履歴をレポートにする。
あわせて、その到達を、他の到達で学習したモデルで前もって当てられるかを調べる。

到達は「時刻 物体」で指定する(時刻は骨格データの時刻 分:秒。例: 01:59 TV)。その時刻の前後 --snap 秒で、
物体まで --snap-radius m 以内に入った最初の時刻を到達時刻にする(指定した時刻のずれをそろえるため。入らなければ指定した時刻のまま)。

出すもの(<データの場所>Reports/Manual_<実験の名前>_<実験対象者>_<人物>/report.html):
  1 到達前の注視可能性 … 到達ごとに、到達の 10 秒前から到達までの物体ごとの注視可能性(到達した物体を強調)と、全部の到達の平均。
                         距離・体の向きの時間変化と、ヒートマップも
  2 前もって分かるか   … 到達の 5・3・2・1 秒前に、到達した物体が 注視可能性・距離・体の向き・近づく速さ で1位だったか
  3 モデルで当てられるか … 他の到達(この人の、指定した到達の前後 30 秒を除いた終着点への到達と、他の人の終着点への到達)で学習した
                         モデル A〜D(analyze_arrival.py と同じ)で、
                           ・到達の L 秒前に、どの物体へ到達するかを当てる(条件付きロジット)
                           ・その時刻ごとの「5 秒以内に到達する確率」(ロジスティック回帰)を図にする

使い方:
  (report.html のほかに、うまくいっている点だけをまとめた highlights.html も出す)
  python Tools/analyze_manual.py --data-root assets --export Oono/sora --person child --events "01:59 TV" "04:16 Tablet" "18:08 Tablet" "20:50 TV"
  (--events を省くと、1行に1つずつ入力する)
numpy だけで動く。
"""

from __future__ import annotations

import argparse
import html
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
from analyze_arrival import (  # noqa: E402
    EXTRA_STYLE, MODEL_COLORS, choose_exports, fit_rows, identify, list_exports, load_streams, realtime_samples, trial_heatmap,
)
from analyze_prediction import episode_chart, xy_chart  # noqa: E402
from toolbox import home_data_root  # noqa: E402
from toolbox.arrival_model import (  # noqa: E402
    FEATURE_DEFINITIONS, FEATURE_LABELS, MODEL_FEATURES, MODEL_LABELS, Config, PersonStream, arrival_definition, logistic_proba,
    matrix,
)
from toolbox.export_data import choose  # noqa: E402
from toolbox.report_html import SCRIPT, SERIES_DARK, SERIES_LIGHT, STYLE, legend, num, pct, retro_chart, table_html  # noqa: E402

LEADS = [5.0, 3.0, 2.0, 1.0]
POINTS = np.round(np.arange(-10.0, 1e-6, 1.0), 3)
EXCLUDE = 30.0
# 信号: 列名, 名前, 1位 = 最大(+1)/ 最小(-1)
RANK_SIGNALS = [("score", "注視可能性(直前1秒)", +1), ("dist", "距離", -1), ("body", "体の向き(肩の正面からの角度)", -1),
                ("approach", "近づく速さ", -1)]


def parse_time(text: str) -> float:
    parts = [float(p) for p in text.strip().split(":")]
    return parts[0] * 60 + parts[1] if len(parts) == 2 else parts[0]


def snap(stream: PersonStream, name: str, t: float, window: float, radius: float) -> float:
    d = stream.dist[name]
    i = np.flatnonzero((stream.time >= t - window) & (stream.time <= t + window) & np.isfinite(d) & (d <= radius))
    return float(stream.time[i[0]]) if len(i) else t


def signal_value(stream: PersonStream, key: str, name: str, t: float) -> float:
    if key == "score":
        return stream.mean_score(name, t - 1.0, t)
    if key == "approach":
        return stream.recent_mean(stream.dist[name], t - 0.5, t) - stream.recent_mean(stream.dist[name], t - 2.5, t - 2.0)
    values = stream.dist[name] if key == "dist" else stream.body[name]
    return stream.recent_mean(values, t - 0.5, t)


def series_values(stream: PersonStream, key: str, name: str, t: float) -> np.ndarray:
    if key == "score":
        return np.array([stream.mean_score(name, t + r - 1.0, t + r) for r in POINTS])
    values = stream.dist[name] if key == "dist" else stream.body[name]
    return np.array([stream.recent_mean(values, t + r - 0.5, t + r) for r in POINTS])


HIGHLIGHT_STYLE = """
.chart.retro { max-width: 560px; } .finding { border-left: 4px solid var(--series-1); padding-left: 12px; margin: 32px 0 10px; }
.finding h2 { border: 0; margin: 0 0 4px; padding: 0; } .finding p { margin: 0; color: var(--text-2); }
main { max-width: 1280px; } .event-title { font-weight: 600; margin: 18px 0 6px; }
.triple { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 10px; }
.triple .card { margin: 0; } .triple .chart.retro { max-width: none; }
@media (max-width: 760px) { .triple { grid-template-columns: 1fr; } }
"""


def bar_chart(groups: list[str], series: list[dict], aria: str, y_label: str, chance: float | None = None) -> str:
    """グループごとの縦棒(series: name / values / color)。chance があれば破線"""
    width, height, left, right, top, bottom = 600, 250, 46, 74, 22, 46
    plot_w, plot_h = width - left - right, height - top - bottom
    y = lambda v: top + (1 - v) * plot_h
    gw = plot_w / len(groups)
    bw = min(40, gw * 0.7 / len(series))
    parts = [f"<svg viewBox='0 0 {width} {height}' class='chart retro' role='img' aria-label='{html.escape(aria)}'>",
             f"<text x='{left}' y='{top - 8}' class='tick'>{html.escape(y_label)}</text>"]
    for v in (0, 0.5, 1):
        parts.append(f"<line x1='{left}' x2='{left + plot_w}' y1='{y(v):.1f}' y2='{y(v):.1f}' class='grid'/>"
                     f"<text x='{left - 6}' y='{y(v) + 4:.1f}' class='tick' text-anchor='end'>{v:g}</text>")
    for g, name in enumerate(groups):
        x0 = left + g * gw + (gw - bw * len(series)) / 2
        for k, sr in enumerate(series):
            v = sr["values"][g]
            if not np.isfinite(v):
                continue
            x = x0 + k * bw
            parts.append(f"<rect x='{x + 2:.1f}' y='{y(v):.1f}' width='{bw - 4:.1f}' height='{y(0) - y(v):.1f}' rx='3' fill='{sr['color']}'>"
                         f"<title>{html.escape(name)} {html.escape(sr['name'])}: {v:.2f}</title></rect>"
                         f"<text x='{x + bw / 2:.1f}' y='{y(v) - 5:.1f}' class='value' text-anchor='middle'>{v:.2f}</text>")
        parts.append(f"<text x='{left + g * gw + gw / 2:.1f}' y='{top + plot_h + 18}' class='label' text-anchor='middle'>{html.escape(name)}</text>")
    if chance is not None:
        parts.append(f"<line x1='{left}' x2='{left + plot_w}' y1='{y(chance):.1f}' y2='{y(chance):.1f}' class='axis' stroke-dasharray='5 4'/>"
                     f"<text x='{left + plot_w + 6}' y='{y(chance) + 4:.1f}' class='tick'>当て推量 {chance:.2f}</text>")
    parts.append("</svg>")
    return "".join(parts)


def highlights_page(stream: PersonStream, events: list[dict], hits: dict, id_prob: dict, colors: dict, key: str) -> str:
    """発表用: うまくいっている点だけをまとめたページ"""
    targets = stream.targets
    n = len(events)
    label_of = lambda e: f"{int(e['t'] // 60):02d}:{int(e['t'] % 60):02d} {e['target']}"
    s = [f"<h1>{html.escape(key)}: 動画で確認した到達 {n} 回でうまくいっていること</h1>",
         "<p class='meta'>到達の前の視線(頭→鼻の向き)・体(肩)の向き・距離から、どの物体に向かうかを調べた。"
         "到達 = 動画で、物体に興味をもって近づき関わったと確認したもの。</p>"]

    # 1 信号ごとの的中
    s.append("<div class='finding'><h2>1. 体の向きと視線は、動き出す前に到達先を指している</h2>"
             f"<p>到達の L 秒前に、到達した物体がその信号で1位だった割合({n} 回中)。距離(一番近い物体)ではほとんど当たらない。</p></div>")
    xs = np.array([-L for L in LEADS])
    order = np.argsort(xs)
    palette = {"score": "var(--series-3)", "dist": "var(--muted)", "body": "var(--series-2)"}
    short = {"score": "視線", "dist": "距離", "body": "体の向き"}
    sig_series = [{"name": label, "label": short[k], "values": np.array([hits[k][L] / n for L in LEADS])[order],
                   "color": palette[k], "emphasized": k != "dist"} for k, label, _ in RANK_SIGNALS if k in palette]
    sig_series.append({"name": "当て推量", "label": "当て推量", "values": np.full(len(LEADS), 1 / len(targets)),
                       "color": "var(--muted)", "dashed": True})
    s.append("<div class='card'>" + xy_chart(xs[order], sig_series, "信号ごとに到達先が1位だった割合", "到達からの時刻 (秒)",
                                               "到達先が1位だった割合", (0.0, 1.0), width=560, x_fmt=lambda v: f"{v:g} 秒") + "</div>")
    s.append(table_html(["信号", *[f"{L:g} 秒前" for L in LEADS]],
                        [[html.escape(label), *[f"{hits[k][L]} / {n}" for L in LEADS]] for k, label, _ in RANK_SIGNALS if k in palette]))

    # 2 モデル A・B・C
    compared = ["A", "B", "C"]
    model_colors = {"A": "var(--muted)", "B": "var(--series-1)", "C": "var(--series-2)"}
    usable = [e for e in events if all((e["index"], 1.0, m) in id_prob for m in compared)]
    if usable:
        s.append("<div class='finding'><h2>2. 視線と体の向きを足すと、到達先を当てる確率が上がる</h2>"
                 "<p>到達の 1 秒前に、モデルが到達した物体に付けた確率。学習データの特徴量は "
                 "A = 空間のみ(距離と近づく速さ)、B = 空間 + 視線、C = 空間 + 視線 + 体の向き。"
                 "どれも、この到達を含まない他の到達(終着点)だけで学習した。</p></div>")
        probs = {m: [id_prob[e["index"], 1.0, m][0] for e in usable] for m in compared}
        s.append("<div class='card'>" + bar_chart([label_of(e) for e in usable],
                                                   [{"name": MODEL_LABELS[m], "values": probs[m], "color": model_colors[m]} for m in compared],
                                                   "到達の1秒前に到達先に付けた確率", "到達した物体の確率", chance=1 / len(targets)) + "</div>")
        s.append("<div class='legend'>" + "".join(f"<span><i style='background:{model_colors[m]}'></i>{html.escape(MODEL_LABELS[m])}</span>"
                                                  for m in compared) + "</div>")
        s.append(table_html(["到達", *[MODEL_LABELS[m] for m in compared], "A からの上がり(C)"],
                            [[html.escape(label_of(e)),
                              *[f"{probs[m][i]:.2f}{'(1位)' if id_prob[e['index'], 1.0, m][1] else ''}" for m in compared],
                              f"+{probs['C'][i] - probs['A'][i]:.2f}"] for i, e in enumerate(usable)]))

    # 3 例
    shown = sorted(usable, key=lambda e: probs["A"][usable.index(e)] - probs["C"][usable.index(e)]) if usable else events
    s.append("<div class='finding'><h2>3. 到達ごとの視線・体の向き・距離の時間変化</h2>"
             "<p>到達の 10 秒前から到達までの、物体ごとの注視可能性(左)、肩の正面から物体への角度(中。0 度 = 体が物体を向いている)、"
             "物体までの距離(右。0 m = 物体のすぐそば)。体の向きと距離は 0 を上にしたので、どの図も上に行くほど到達に近い。"
             "太線が到達した物体。到達の 1〜2 秒前に、到達した物体の線が上に上がる(よく見る・体が向く・近づく)。</p></div>")
    s.append(legend(targets, colors))
    panels = (("score", "注視可能性", (0.0, 1.0), ""), ("body", "体の向き (度)", (0.0, 180.0), "上 = 体が向いている"),
              ("dist", "距離 (m)", (0.0, 4.0), "上 = 物体のすぐそば"))
    for e in shown:
        s.append(f"<div class='event-title'>{html.escape(label_of(e))} に到達</div><div class='triple'>")
        for sig, title, rng, note in panels:
            vals = {n_: series_values(stream, sig, n_, e["t"]) for n_ in targets}
            series = [{"name": n_, "values": vals[n_], "color": f"var(--series-{colors[n_] + 1})", "emphasized": n_ == e["target"],
                       "label": f"{n_}(到達)" if n_ == e["target"] else n_} for n_ in targets]
            chart = (retro_chart(POINTS, series, f"{label_of(e)} の{title}") if sig == "score" else
                     xy_chart(POINTS, series, f"{label_of(e)} の{title}", "到達からの時刻 (秒)", note, rng,
                              x_fmt=lambda v: "到達" if v == 0 else f"{v:g}s", invert=True))
            s.append(f"<div class='card'><div class='card-title'>{title}</div>{chart}</div>")
        s.append("</div>")

    s.append("<div class='finding'><h2>4. 興味をもった到達を自動で選べる</h2>"
             "<p>「物体に近づき、0.6 m 以内に 3 秒以上とどまり、体が物体を向いている(60 度以内)か物体を見ている」到達を自動で選ぶと、"
             "選ばれたものは動画でも興味をもって近づいた到達だった(近くを通り過ぎただけのものは選ばれない)。"
             "データが増えても、到達を手で選ばずに学習データを作れる。</p></div>")
    s.append(table_html(["自動で選んだ到達", "物体"], [[f"{int(t // 60):02d}:{int(t % 60):02d}", html.escape(n_)] for n_, t in stream.arrivals]))

    return ("<!doctype html><html lang='ja'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width, initial-scale=1'>"
            "<title>うまくいっていること</title><style>%STYLE%</style></head><body><main>" + "".join(s) + "</main></body></html>")


def main() -> None:
    defaults = Config()
    parser = argparse.ArgumentParser(description="動画で確認した到達だけで、到達前の視線履歴のレポートを作る")
    parser.add_argument("--export", help="使う書き出し(実験/実験対象者)")
    parser.add_argument("--person", help="人物")
    parser.add_argument("--events", nargs="+", metavar="'分:秒 物体'", help="動画で確認した到達(例: '01:59 TV')")
    parser.add_argument("--snap", type=float, default=3.0, help="指定した時刻の前後何秒で到達時刻を探すか")
    parser.add_argument("--snap-radius", type=float, default=0.6, help="到達時刻 = 物体まで この距離(m)以内に入った最初の時刻")
    parser.add_argument("--horizon", type=float, default=5.0, help="この先何秒以内の到達を当てるか(図の確率)")
    home_data_root.add_argument(parser)
    args = parser.parse_args()

    root = home_data_root.choose_data_root(args.data_root)
    if root is None:
        return
    found = list_exports(root)
    exports = ([f for f in found if "/".join(f).startswith(args.export.strip("/"))][:1] if args.export
               else (choose_exports(found) or [])[:1])
    if not exports:
        print("書き出しが選ばれていません。")
        return

    # 学習用の到達は「終着点」(近づいてとどまり、向くか見る)にする(通り過ぎを除くため)
    cfg = Config(horizon=args.horizon, arrival="destination")
    streams = load_streams(root, exports, cfg, None)
    keys = [s.key for s in streams]
    key = next((k for k in keys if args.person and k.endswith("/" + args.person)), None) or choose("人物", keys)
    if key is None:
        return
    stream = next(s for s in streams if s.key == key)
    others = [s for s in streams if s.key != key]

    texts = args.events
    if not texts:
        print("動画で確認した到達を「分:秒 物体」で1行ずつ入力(空行で終わり):")
        texts = []
        while True:
            try:
                line = input("> ").strip()
            except EOFError:
                break
            if not line:
                break
            texts.append(line)

    events = []
    for text in texts:
        when, name = text.split()[0], text.split()[1]
        if name not in stream.targets:
            print(f"物体 {name} がありません({', '.join(stream.targets)})")
            return
        given = parse_time(when)
        events.append({"index": len(events) + 1, "target": name, "given": given, "t": snap(stream, name, given, args.snap, args.snap_radius)})

    print("指定した到達:")
    for e in events:
        print(f"  {e['index']}. {e['target']} 指定 {e['given']:.1f} 秒 → 到達時刻 {e['t']:.1f} 秒")

    targets = stream.targets
    colors = {n: k for k, n in enumerate(targets)}
    out_dir = home_data_root.report_root(root) / f"Manual_{stream.experiment}_{key.replace('/', '_')}"
    out_dir.mkdir(exist_ok=True)

    # ---- 1 到達前の注視可能性
    sec = ["<h2>1 到達前の注視可能性</h2>",
           "<p class='note'>注視可能性 = 視線(頭→鼻の向き)と物体のふちとの角度に、推定誤差 σ = "
           f"{cfg.sigma:g} 度のガウスをかけた値(0〜1。視線が物体に当たっていれば 1)。各点はその時刻までの 1 秒間の平均。"
           "太線・大きい点・「(到達)」が到達した物体。映っていない区間は点を打たない。</p>",
           legend(targets, colors), "<div class='multiples'>"]

    with np.errstate(all="ignore"):
        arrived_mean = np.nanmean([series_values(stream, "score", e["target"], e["t"]) for e in events], axis=0)
        others_mean = np.nanmean([series_values(stream, "score", n, e["t"]) for e in events for n in targets if n != e["target"]], axis=0)
    sec.append(f"<div class='card mean'><div class='card-title'>{len(events)} 回の平均</div>" + retro_chart(POINTS, [
        {"name": "到達した物体", "values": arrived_mean, "color": "var(--text)", "emphasized": True, "label": "到達した物体"},
        {"name": "他の物体", "values": others_mean, "color": "var(--muted)", "dashed": True, "label": "他の物体"}],
        "到達した物体と他の物体の注視可能性の平均") + "</div>")

    for e in events:
        values = {n: series_values(stream, "score", n, e["t"]) for n in targets}
        series = [{"name": n, "values": values[n], "color": f"var(--series-{colors[n] + 1})", "emphasized": n == e["target"],
                   "label": f"{n}(到達)" if n == e["target"] else n} for n in targets]
        m, s = divmod(e["t"], 60)
        sec.append(f"<div class='card'><div class='card-title'>{e['index']}. {html.escape(e['target'])} に到達({int(m):02d}:{s:04.1f})</div>"
                   + retro_chart(POINTS, series, f"{e['target']} に到達した回の注視可能性") + "</div>")
    sec.append("</div>")

    sec.append("<h3>距離と体の向き</h3><p class='note'>同じ到達の、物体までの距離(骨盤から物体の箱までの水平距離)と、"
               "肩の正面から物体への角度(0 度 = 体が物体を向いている)。どちらも 0 を上にした(上に行くほど到達に近い)。</p><div class='multiples'>")
    for e in events:
        for sig, label, rng in (("dist", "距離 (m)", (0.0, 4.0)), ("body", "体の向き (度)", (0.0, 180.0))):
            vals = {n: series_values(stream, sig, n, e["t"]) for n in targets}
            series = [{"name": n, "values": vals[n], "color": f"var(--series-{colors[n] + 1})", "emphasized": n == e["target"],
                       "label": f"{n}(到達)" if n == e["target"] else n} for n in targets]
            sec.append(f"<div class='card'><div class='card-title'>{e['index']}. {html.escape(e['target'])} — {label}</div>"
                       + xy_chart(POINTS, series, f"{label}の時間変化", "到達からの時刻 (秒)",
                                  "上 = 体が向いている" if sig == "body" else "上 = 物体のすぐそば", rng,
                                  x_fmt=lambda v: "到達" if v == 0 else f"{v:g}s", invert=True) + "</div>")
    sec.append("</div>")

    sec.append("<h3>ヒートマップ</h3><p class='note'>行 = 物体(太字が到達した物体)。濃いほど注視可能性が高い。</p><div class='heat-grid'>")
    for e in events:
        sec.append(f"<div class='card'><div class='card-title'>{e['index']}. {html.escape(e['target'])}</div>"
                   f"{trial_heatmap(stream, e['target'], e['t'])}</div>")
    sec.append("</div>")

    # ---- 2 前もって分かるか(信号ごとの1位)
    rows, hits = [], {k: {L: 0 for L in LEADS} for k, *_ in RANK_SIGNALS}
    for e in events:
        for key_s, label, sign in RANK_SIGNALS:
            cells = []
            for L in LEADS:
                vals = {n: signal_value(stream, key_s, n, e["t"] - L) for n in targets}
                ok = {n: v for n, v in vals.items() if np.isfinite(v)}
                if e["target"] not in ok or len(ok) < 2:
                    cells.append("—")
                    continue
                best = max(ok, key=lambda n: sign * ok[n])
                hit = best == e["target"]
                hits[key_s][L] += hit
                digits = 0 if key_s == "body" else 2
                cells.append(f"{'●' if hit else '○'} {ok[e['target']]:.{digits}f}<br><small>1位 {html.escape(best)}</small>")
            rows.append([f"{e['index']}. {html.escape(e['target'])}", html.escape(label), *cells])
    sec.append("<h2>2 前もって分かるか(信号ごと)</h2>"
               "<p class='note'>到達の L 秒前に、到達した物体がその信号で1位だったか(● = 1位、○ = 1位でない)。"
               "注視可能性は高いほど、距離・体の向きの角度・近づく速さ(距離の変化)は小さいほど1位。数字は到達した物体の値。"
               f"当て推量は 1 / {len(targets)}。</p>")
    sec.append(table_html(["到達", "信号", *[f"{L:g} 秒前" for L in LEADS]], rows))
    sec.append(table_html(["信号", *[f"{L:g} 秒前" for L in LEADS]],
                          [[html.escape(label), *[f"{hits[k][L]} / {len(events)}" for L in LEADS]] for k, label, _ in RANK_SIGNALS]))

    # ---- 3 モデル
    def far(t: float) -> bool:
        return all(abs(t - e["t"]) > EXCLUDE for e in events)

    train_events = []
    for s in [stream] + others:
        for arrived, t in s.arrivals:
            if s is stream and not far(t):
                continue
            for L in LEADS:
                feats = s.features(t - L)
                if feats is not None:
                    train_events.append({"lead": L, "person": s.key, "rows": [feats[n] for n in s.targets],
                                         "chosen": s.targets.index(arrived)})

    ident_rows = []
    id_prob: dict[tuple[int, float, str], tuple[float, bool]] = {}
    for e in events:
        for L in LEADS:
            feats = stream.features(e["t"] - L)
            if feats is None:
                continue
            test = {"rows": [feats[n] for n in targets], "chosen": targets.index(e["target"])}
            cells = []
            for m in MODEL_FEATURES:
                train = [x for x in train_events if x["lead"] == L]
                if len(train) < 5:
                    cells.append("—")
                    continue
                p = identify(train, [test], m)[0]
                best = targets[int(np.argmax(p))]
                id_prob[e["index"], L, m] = (float(p[test["chosen"]]), best == e["target"])
                cells.append(f"{'●' if best == e['target'] else '○'} {p[test['chosen']]:.2f}")
            ident_rows.append([f"{e['index']}. {html.escape(e['target'])}", f"{L:g} 秒前", *cells])

    n_train = sum(1 for x in train_events if x["lead"] == 1.0)
    sec.append("<h2>3 モデルで当てられるか</h2>"
               f"<p class='note'>学習データ = {html.escape(arrival_definition(cfg))}(この人の、指定した到達の前後 {EXCLUDE:g} 秒を除く + "
               f"{html.escape(', '.join(s.key for s in others) or '他の人なし')})。到達 {n_train} 回(到達の 1 秒前の時点で視線が取れていたもの)。"
               "指定した到達は学習に使っていない。</p>")
    sec.append("<h3>どの物体へ到達するか(到達の L 秒前)</h3>"
               "<p class='note'>条件付きロジット。● = 到達した物体の確率が1位、数字 = 到達した物体の確率(候補は全物体)。</p>")
    sec.append(table_html(["到達", "時点", *[MODEL_LABELS[m] for m in MODEL_FEATURES]], ident_rows))

    # 5 秒以内に到達する確率(ロジスティック回帰)
    train_samples = [r for s in [stream] + others for r in realtime_samples(s) if s is not stream or far(r["timeSec"])]
    models = {m: fit_rows(train_samples, m) for m in MODEL_FEATURES} if sum(r["label"] for r in train_samples) else {}
    samples = []
    for e in events:
        for t in np.arange(e["t"] - args.horizon - cfg.window - 1, e["t"] + 2, cfg.step):
            feats = stream.features(float(t))
            if feats is None:
                continue
            for n in targets:
                samples.append({"timeSec": round(float(t), 3), "target": n, "label": int(n == e["target"] and e["t"] - args.horizon <= t < e["t"]),
                                **feats[n]})
    probs = {m: logistic_proba(sc(matrix(samples, MODEL_FEATURES[m])), w) for m, (sc, w) in models.items()} if samples and models else {}

    sec.append(f"<h3>{args.horizon:g} 秒以内に到達する確率の時間変化</h3>"
               f"<p class='note'>上の 3 段が入力(太線が到達した物体)、一番下が「{args.horizon:g} 秒以内に到達する確率」"
               f"(実線 = C 空間 + 視線 + 体の向き / 破線 = A 空間のみ)。青い帯のデータから、オレンジの帯の間に到達するかを予測する。"
               "学習に使った到達では、確率が高くなるのは到達の直前の数秒だけなので、値の大きさより物体どうしの順番を見る。</p>")
    sec.append(legend(targets, colors))
    if probs:
        for e in events:
            sec.append(f"<h4>{e['index']}. {html.escape(e['target'])} に到達({e['t']:.1f} 秒)</h4>"
                       f"<div class='card'>{episode_chart(stream, e['target'], e['t'], targets, samples, probs, args.horizon)}</div>")

    sec.append("<h2>特徴量</h2>" + table_html(["特徴量", "列名", "求め方", *MODEL_FEATURES],
               [[html.escape(FEATURE_LABELS[f]), f"<code>{f}</code>", html.escape(FEATURE_DEFINITIONS[f]),
                 *("●" if f in MODEL_FEATURES[m] else "" for m in MODEL_FEATURES)]
                for f in dict.fromkeys(f for fs in MODEL_FEATURES.values() for f in fs)]))

    light_vars = " ".join(f"--series-{i + 1}: {c};" for i, c in enumerate(SERIES_LIGHT))
    dark_vars = " ".join(f"--series-{i + 1}: {c};" for i, c in enumerate(SERIES_DARK))
    listing = "".join(f"<li>{e['index']}. {html.escape(e['target'])} — 指定 {int(e['given'] // 60):02d}:{e['given'] % 60:04.1f} → "
                      f"到達時刻 {int(e['t'] // 60):02d}:{e['t'] % 60:04.1f}</li>" for e in events)
    page = (f"<!doctype html><html lang='ja'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width, initial-scale=1'>"
            f"<title>確認した到達</title><style>{STYLE.replace('%LIGHT%', light_vars).replace('%DARK%', dark_vars)}{EXTRA_STYLE}"
            ".chart.retro { max-width: 520px; }</style><script>" + SCRIPT + "</script></head><body><main>"
            f"<h1>{html.escape(key)}: 動画で確認した到達 {len(events)} 回の視線履歴</h1>"
            f"<p class='meta'>{html.escape('/'.join(exports[0]))}。到達時刻 = 指定した時刻の前後 {args.snap:g} 秒で、"
            f"物体まで {args.snap_radius:g} m 以内に入った最初の時刻(入らなければ指定した時刻)。</p><ul class='note'>{listing}</ul>"
            + "".join(sec) + "</main></body></html>")
    (out_dir / "report.html").write_text(page, encoding="utf-8")
    style = STYLE.replace("%LIGHT%", light_vars).replace("%DARK%", dark_vars) + EXTRA_STYLE + HIGHLIGHT_STYLE
    (out_dir / "highlights.html").write_text(highlights_page(stream, events, hits, id_prob, colors, key).replace("%STYLE%", style),
                                             encoding="utf-8")
    print(f"→ {out_dir / 'highlights.html'}")
    print(f"\n→ {out_dir / 'report.html'}")


if __name__ == "__main__":
    main()
