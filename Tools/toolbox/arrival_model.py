"""到達の予測に使う部品: 視線の不確実性を考えた注視可能性・視線履歴の特徴量・モデル(条件付きロジット / ロジスティック回帰)。
Tools/analyze_arrival.py が使う。直接は実行しない. numpy だけで動く.

リアルタイムで使うときも同じ計算になるように、特徴量はその時刻までのデータだけから求める
(頭や肩の向きの平滑化も、その時刻までの smooth 秒だけを使う)。OnlineArrivalPredictor が、届いたフレームを貯めて
学習したモデル(save_model で書き出した JSON)で「この先 horizon 秒以内にどの物体へ到達するか」の確率を返す。

注視可能性(物体ごと, 0〜1):
  視線(頭→鼻の向き)と、頭から物体への向きとの角度 θ から、物体の見かけの大きさ ρ = asin(r / 距離) を引いた
  「物体のふちまでの角度」e = max(0, θ − ρ) に、視線の推定誤差 σ 度のガウス exp(−e² / 2σ²) をかける。
  視線が物体に当たっていれば 1。物体は箱の3辺の平均の半分を半径 r とする球とみなす。遮蔽は考えない。
"""

from __future__ import annotations

import json
from dataclasses import asdict, dataclass
from pathlib import Path
from types import SimpleNamespace

import numpy as np

from .export_data import angle_between, body_forward, episodes, signed_yaw, smooth_vectors


@dataclass
class Config:
    sigma: float = 10.14       # 視線の推定誤差(度)。シーン5の視線コーンの半角と同じ
    window: float = 10.0       # 視線履歴を見る長さ T(秒)
    gap: float = 1.0           # 到達の識別で、到達の何秒前までの履歴を使うか(到達の瞬間は見ていて当然なので除く)
    high: float = 0.5          # 「注視可能性が高い」とする値
    merge: float = 0.3         # これ以下の途切れは高い状態の1回としてつなげる(秒)
    smooth: float = 0.3        # 頭と肩の向きを滑らかにする幅(秒。その時刻までの中央値 → 平均)
    max_gap: float = 0.25      # これより長い途切れはまたがない(秒)
    near: float = 0.4          # 到達とする距離(m)
    leave: float = 1.0         # 離れているとする距離(m)
    horizon: float = 3.0       # リアルタイムの予測: この先何秒以内の到達を当てるか
    step: float = 0.5          # リアルタイムの予測を学習・評価する間隔(秒)
    # 到達の決め方(arrival_definition に説明)
    arrival: str = "approach"  # approach(近づいた)/ destination(終着点: 近づいてとどまり、向くか見る)/ touch(手で触れた)
    dwell: float = 3.0         # destination: 到達の後、dwell_radius m 以内にとどまる秒数
    dwell_radius: float = 0.6  # destination: とどまっているとする距離(m)
    face: float = 60.0         # destination: 到達後 2 秒の、肩の正面から物体への角度の平均がこれ以下なら「向いている」(度)
    look: float = 0.3          # destination: 到達後 3 秒の注視可能性の平均がこれ以上なら「見ている」
    touch_margin: float = 0.0  # touch: 手(手・手先・親指)が物体の箱からこの距離以内なら触れている(m。0 = 箱の中に入ったら)
    touch_min: float = 0.3     # touch: 触れている状態がこれ以上続いたら1回(秒)
    touch_gap: float = 3.0     # touch: これ以下の間をおいて触れ直したときは同じ1回にする(秒。遊んでいる間の触れ直し)


ARRIVAL_MODES = ("approach", "destination", "touch")
HAND_JOINTS = ["HandLeft", "HandTipLeft", "ThumbLeft", "HandRight", "HandTipRight", "ThumbRight"]


def candidate_definition(cfg: Config) -> str:
    """到達しうる物体(候補)の説明"""
    if cfg.arrival == "touch":
        return "今は触れていない物体"
    return f"前に {cfg.leave:g} m より離れたのを見ている物体。今そばにいる物体は除く"


def arrival_definition(cfg: Config) -> str:
    approach = f"{cfg.leave:g} m より離れていた後に {cfg.near:g} m 以内に入った時刻(骨盤から物体の箱までの水平距離)"
    if cfg.arrival == "destination":
        return (f"終着点への到達 = {approach}のうち、その後 {cfg.dwell_radius:g} m 以内に {cfg.dwell:g} 秒以上とどまり、"
                f"かつ 体が物体を向いている(到達後 2 秒の肩の正面から物体への角度の平均 ≤ {cfg.face:g} 度)か "
                f"物体を見ている(到達後 3 秒の注視可能性の平均 ≥ {cfg.look:g})もの")
    if cfg.arrival == "touch":
        where = "物体の箱の中" if cfg.touch_margin <= 0 else f"物体の箱から {cfg.touch_margin:g} m 以内"
        return (f"触れた = 手・手先・親指のどれかが{where}に {cfg.touch_min:g} 秒以上入った時刻"
                f"({cfg.touch_gap:g} 秒以下の間をおいた触れ直しは同じ1回。手の位置はシーン5の skeleton.csv、無ければ書き出し元の grouped_*.json)")
    return f"到達 = {approach}"


FEATURE_LABELS = {
    "dist": "物体までの水平距離 (m)",
    "approach": "直前2秒の距離の変化 (m, 近づくと −)",
    "body": "肩の正面から物体への水平の角度 (度)",
    "led": "ライトが点いている (0/1)",
    "cum": "累積注視可能性 (秒)",
    "init": "初期注視可能性 (最初の 1/3)",
    "final": "最終注視可能性 (最後の 1 秒)",
    "revisits": "再注視回数 (高い状態の回数)",
    "persist": "注視の持続 (高い状態の最長, 秒)",
    "since": "最後に高かったときからの時間 (秒)",
    "share": "注視の分配 (全物体の累積のうちこの物体の割合)",
}

# 特徴量の求め方(PersonStream.features と同じ。t = 特徴量を求める時刻、T = window 秒、s = 注視可能性)
FEATURE_DEFINITIONS = {
    "dist": "t の直前 0.5 秒の、骨盤(無ければ頭)から物体の箱までの水平距離の平均(シーン5の distance.csv)",
    "approach": "dist −(t の 2.5〜2 秒前の距離の平均)。近づいていると −",
    "led": "t にその物体のライトが点いていれば 1(light_log.json。ライトでない物体・ログが無いときは 0)",
    "cum": "t−T〜t の s の時間積分(視線が取れていない時間を除いて平均し、T 秒あたりに直したもの)",
    "init": "t−T〜t−2T/3 の s の平均",
    "final": "t−1〜t の s の平均",
    "revisits": "t−T〜t に s ≥ high の区間が何回あったか(merge 秒以下の途切れはつなげる)",
    "persist": "t−T〜t の s ≥ high の区間の最長(秒)",
    "since": "t − 最後に s ≥ high だった時刻(秒。無ければ T)",
    "share": "この物体の cum / 全物体の cum の合計",
    "body": "t の直前 0.5 秒の、肩の正面(左右の肩を結ぶ線に垂直な水平の向き)から物体の中心への水平の角度の絶対値の平均",
}

SPATIAL = ["dist", "approach", "led"]
GAZE = ["cum", "init", "final", "revisits", "persist", "since", "share"]
BODY = ["body"]
MODEL_FEATURES = {
    "A": SPATIAL,
    "B": SPATIAL + GAZE,
    "C": SPATIAL + GAZE + BODY,
    "D": GAZE,
}
MODEL_LABELS = {"A": "A 空間のみ", "B": "B 空間 + 視線", "C": "C 空間 + 視線 + 体の向き", "D": "D 視線パターンのみ"}


def object_radius(size: dict | np.ndarray) -> float:
    """物体を球とみなした半径(箱の3辺の平均の半分)"""
    s = np.array([size["x"], size["y"], size["z"]]) if isinstance(size, dict) else np.asarray(size, dtype=float)
    return float(np.mean(s) / 2)


def gaze_possibility(origin: np.ndarray, direction: np.ndarray, center: np.ndarray, radius: float, sigma: float) -> np.ndarray:
    """注視可能性(0〜1)。direction が nan のフレームは nan"""
    to_target = center[None, :] - origin
    distance = np.linalg.norm(to_target, axis=1)
    theta = angle_between(direction, to_target)
    rho = np.degrees(np.arcsin(np.clip(radius / np.maximum(distance, 1e-6), 0, 1)))
    edge = np.maximum(theta - rho, 0)
    return np.exp(-edge ** 2 / (2 * sigma ** 2))


# ------------------------------------------------------------
# 1人分の時系列
# ------------------------------------------------------------
class PersonStream:
    """1人分の、物体ごとの注視可能性・距離・体の向き・ライトの点灯と、到達の時刻。特徴量は features() で求める

    key        … 人物を表す名前(<実験対象者>/<人物>)
    frames     … time, dt, present, has_gaze, origin, direction, shoulder_left, shoulder_right(シーン5の gaze.csv の列)
    distances  … 物体ごとの水平距離(シーン5の distance.csv の dist_<物体>)
    objects    … 物体ごとの中心と半径 {名前: (center, radius)}
    led        … 物体ごとのライトの点灯(フレームごとの bool。ライトでない物体は無くてよい)
    boxes      … 物体ごとの箱の中心と大きさ {名前: (center, size)}(arrival = touch のとき使う)
    frames["hands"] があれば手の関節の位置(フレーム数 × 関節の数 × 3。arrival = touch のとき使う)
    """

    def __init__(self, key: str, frames: dict, distances: dict[str, np.ndarray], objects: dict[str, tuple[np.ndarray, float]],
                 led: dict[str, np.ndarray], cfg: Config, boxes: dict[str, tuple[np.ndarray, np.ndarray]] | None = None):
        self.key, self.cfg = key, cfg
        self.objects = objects
        self.boxes = boxes or {}
        self.hands = np.asarray(frames["hands"], dtype=float) if frames.get("hands") is not None else None
        self.targets = list(objects)
        t = np.asarray(frames["time"], dtype=float)
        self.time, self.dt = t, np.asarray(frames["dt"], dtype=float)
        self.present = np.asarray(frames["present"], dtype=bool)
        direction = np.asarray(frames["direction"], dtype=float)
        origin = np.asarray(frames["origin"], dtype=float)
        self.valid = self.present & np.asarray(frames["has_gaze"], dtype=bool) & np.isfinite(direction).all(axis=1)

        # その時刻までのデータだけで滑らかにする(リアルタイムと同じ)
        self.direction = smooth_vectors(t, direction, self.valid, cfg.smooth, cfg.max_gap, causal=True)
        shim = SimpleNamespace(time=t, shoulder_left=np.asarray(frames["shoulder_left"], dtype=float),
                               shoulder_right=np.asarray(frames["shoulder_right"], dtype=float))
        forward = body_forward(shim, self.direction, self.valid, cfg.smooth, cfg.max_gap, causal=True)

        self.score, self.dist, self.body, self.led = {}, {}, {}, {}
        self.cum_score, self.high_eps = {}, {}
        weight = np.where(self.valid, self.dt, 0.0)
        self.cum_cov = np.concatenate([[0.0], np.cumsum(weight)])

        for name, (center, radius) in objects.items():
            s = np.where(self.valid, gaze_possibility(origin, self.direction, center, radius, cfg.sigma), np.nan)
            s = np.where(np.isfinite(s), s, np.nan)
            self.score[name] = s
            self.cum_score[name] = np.concatenate([[0.0], np.cumsum(np.nan_to_num(s) * weight)])
            self.high_eps[name] = np.array(episodes(self.valid & (np.nan_to_num(s) >= cfg.high), t, self.dt, cfg.merge)).reshape(-1, 2)
            d = distances.get(name)
            self.dist[name] = np.where(self.present, d, np.nan) if d is not None else np.full(len(t), np.nan)
            flat = (center[None, :] - origin) * np.array([1.0, 0.0, 1.0])
            self.body[name] = np.abs(signed_yaw(forward, flat))
            self.led[name] = np.asarray(led.get(name, np.zeros(len(t), dtype=bool)), dtype=float)

        if cfg.arrival == "touch":
            self.arrivals, self.armed = self._touches()
        else:
            self.arrivals, self.armed = self._approaches()

            if cfg.arrival == "destination":
                self.approach_arrivals = self.arrivals
                self.arrivals = [(n, t) for n, t in self.arrivals if self.is_destination(n, t)]

    def is_destination(self, name: str, t: float) -> bool:
        """近づいた後にとどまり(dwell)、体が向いているか見ているか(arrival_definition の destination)"""
        cfg = self.cfg
        d = self.dist[name]
        after = np.flatnonzero((self.time >= t) & np.isfinite(d))
        out = next((self.time[i] for i in after if d[i] > cfg.dwell_radius), self.time[after[-1]] if len(after) else t)

        if out - t < cfg.dwell:
            return False

        facing = self.recent_mean(self.body[name], t, t + 2.0) <= cfg.face
        looking = self.mean_score(name, t, t + 3.0) >= cfg.look
        return bool(facing or looking)

    def _touches(self) -> tuple[list[tuple[str, float]], dict[str, np.ndarray]]:
        """手で触れた時刻と、フレームごとに「この先触れうる」(今は触れていない)か"""
        cfg = self.cfg
        arrivals, armed = [], {}

        for name in self.targets:
            if self.hands is None or name not in self.boxes:
                armed[name] = np.zeros(len(self.time), dtype=bool)
                continue

            center, size = self.boxes[name]
            outside = np.maximum(np.abs(self.hands - center) - np.asarray(size) / 2, 0)
            gap = np.linalg.norm(outside, axis=2)
            touching = self.present & (np.nanmin(np.where(np.isfinite(gap), gap, np.inf), axis=1) <= cfg.touch_margin)
            arrivals += [(name, float(s)) for s, e in episodes(touching, self.time, self.dt, cfg.touch_gap) if e - s >= cfg.touch_min]
            armed[name] = self.present & ~touching

        return sorted(arrivals, key=lambda a: a[1]), armed

    def _approaches(self) -> tuple[list[tuple[str, float]], dict[str, np.ndarray]]:
        """近づいた時刻(leave m より離れていたのを見た後に near m 以内に入った時刻。映っていない時間が1秒を超えたら数え直す。
        シーン5の解析 analyze_export.py の接近と同じ)と、フレームごとに「この先到達しうる」(離れたのを見ている)か"""
        cfg = self.cfg
        arrivals, armed = [], {}

        for name in self.targets:
            d = self.dist[name]
            state, last_seen = False, -np.inf
            flags = np.zeros(len(self.time), dtype=bool)

            for i, (t, ok, v) in enumerate(zip(self.time, self.present, d)):
                if ok and np.isfinite(v):
                    if t - last_seen > 1.0:
                        state = False
                    last_seen = t

                    if v > cfg.leave:
                        state = True
                    elif v <= cfg.near and state:
                        arrivals.append((name, float(t)))
                        state = False

                flags[i] = state

            armed[name] = flags

        return sorted(arrivals, key=lambda a: a[1]), armed

    def mean_score(self, name: str, a: float, b: float) -> float:
        """a〜b 秒の注視可能性の平均(視線が取れていた時間で割る。無ければ nan)"""
        i, j = np.searchsorted(self.time, [a, b], side="right")
        cov = self.cum_cov[j] - self.cum_cov[i]
        return (self.cum_score[name][j] - self.cum_score[name][i]) / cov if cov > 1e-6 else np.nan

    def recent_mean(self, values: np.ndarray, a: float, b: float) -> float:
        """a〜b 秒の values の平均(nan を除く)"""
        i, j = np.searchsorted(self.time, [a, b], side="right")
        v = values[i:j]
        v = v[np.isfinite(v)]
        return float(v.mean()) if len(v) else np.nan

    def features(self, t_end: float) -> dict[str, dict[str, float]] | None:
        """時刻 t_end までの window 秒の視線履歴と、t_end の空間情報から、物体ごとの特徴量。
        視線が取れていた時間が window の 3 割に満たないときは None"""
        cfg = self.cfg
        t0 = t_end - cfg.window
        i0, i1 = np.searchsorted(self.time, [t0, t_end], side="right")
        coverage = self.cum_cov[i1] - self.cum_cov[i0]

        if coverage < 0.3 * cfg.window:
            return None

        out = {}

        for name in self.targets:
            cum = (self.cum_score[name][i1] - self.cum_score[name][i0]) / coverage * cfg.window
            eps = self.high_eps[name]
            starts, ends = np.maximum(eps[:, 0], t0), np.minimum(eps[:, 1], t_end)
            inside = ends > starts
            starts, ends = starts[inside], ends[inside]
            dist_now = self.recent_mean(self.dist[name], t_end - 0.5, t_end)
            dist_before = self.recent_mean(self.dist[name], t_end - 2.5, t_end - 2.0)
            out[name] = {
                "dist": dist_now,
                "approach": dist_now - dist_before,
                "body": self.recent_mean(self.body[name], t_end - 0.5, t_end),
                "led": float(self.led[name][max(i1 - 1, 0)]),
                "cum": cum,
                "init": self.mean_score(name, t0, t0 + cfg.window / 3),
                "final": self.mean_score(name, t_end - 1.0, t_end),
                "revisits": float(len(starts)),
                "persist": float((ends - starts).max()) if len(starts) else 0.0,
                "since": float(t_end - ends.max()) if len(starts) else cfg.window,
            }

        total = sum(f["cum"] for f in out.values())

        for f in out.values():
            f["share"] = f["cum"] / total if total > 1e-6 else 1.0 / len(out)

        return out

    def score_bins(self, name: str, a: float, b: float, width: float) -> np.ndarray:
        """a〜b 秒を width 秒ごとに区切った、注視可能性の平均(視線が無い区間は nan)"""
        edges = np.arange(a, b + 1e-9, width)
        return np.array([self.mean_score(name, lo, hi) for lo, hi in zip(edges[:-1], edges[1:])])


def stream_from_export(key: str, person, targets: list[str], centers: dict[str, np.ndarray], sizes: dict[str, dict],
                       led: dict[str, np.ndarray], cfg: Config) -> PersonStream:
    """シーン5の書き出し(toolbox.export_data.Person)から PersonStream を作る"""
    frames = {"time": person.time, "dt": person.dt, "present": person.present, "has_gaze": person.gaze["hasGaze"] == 1,
              "origin": person.origin, "direction": person.direction,
              "shoulder_left": person.shoulder_left, "shoulder_right": person.shoulder_right}
    distances = {n: person.distance[f"dist_{n}"] for n in targets if f"dist_{n}" in person.distance}
    objects = {n: (centers[n], object_radius(sizes[n])) for n in targets}
    boxes = {n: (centers[n], np.array([sizes[n]["x"], sizes[n]["y"], sizes[n]["z"]])) for n in targets}

    if cfg.arrival == "touch" and person.skeleton is not None:
        frames["hands"] = np.stack([person.skeleton.position(j) for j in HAND_JOINTS if f"{j}X" in person.skeleton.column], axis=1)

    stream = PersonStream(key, frames, distances, objects, led, cfg, boxes)
    stream.person = person
    return stream


# ------------------------------------------------------------
# モデル
# ------------------------------------------------------------
def matrix(rows: list[dict[str, float]], names: list[str]) -> np.ndarray:
    return np.array([[r.get(n, np.nan) for n in names] for r in rows], dtype=float).reshape(len(rows), len(names))


class Scaler:
    """平均 0・標準偏差 1 にそろえる(nan は 0 = 平均にする)"""

    def __init__(self, x: np.ndarray):
        self.mean = np.nan_to_num(np.nanmean(x, axis=0)) if len(x) else np.zeros(x.shape[1])
        std = np.nanstd(x, axis=0) if len(x) else np.ones(x.shape[1])
        self.std = np.where(np.isfinite(std) & (std > 1e-9), std, 1.0)

    def __call__(self, x: np.ndarray) -> np.ndarray:
        return np.nan_to_num((x - self.mean) / self.std)


def fit_conditional_logit(groups: list[np.ndarray], chosen: list[int], l2: float = 1.0, iterations: int = 50) -> np.ndarray:
    """到達ごとに候補の物体(行)から1つ選ぶモデル P(j) = softmax(x_j · w)。L2 付きのニュートン法。
    候補の数が違う到達もまとめて計算できるように、足りない候補は mask で除く"""
    g, k = len(groups), groups[0].shape[1]
    j = max(x.shape[0] for x in groups)
    x = np.zeros((g, j, k))
    mask = np.zeros((g, j), dtype=bool)

    for i, gx in enumerate(groups):
        x[i, :len(gx)] = gx
        mask[i, :len(gx)] = True

    picked = x[np.arange(g), np.asarray(chosen)]
    w = np.zeros(k)

    for _ in range(iterations):
        u = np.where(mask, x @ w, -np.inf)
        p = np.exp(u - u.max(axis=1, keepdims=True))
        p /= p.sum(axis=1, keepdims=True)
        mean = np.einsum("gj,gjk->gk", p, x)
        grad = (picked - mean).sum(axis=0) - l2 * w
        hess = -(np.einsum("gj,gjk,gjl->kl", p, x, x) - mean.T @ mean) - l2 * np.eye(k)
        step = np.linalg.solve(hess, grad)
        w -= step

        if np.abs(step).max() < 1e-8:
            break

    return w


def softmax_scores(x: np.ndarray, w: np.ndarray) -> np.ndarray:
    u = x @ w
    p = np.exp(u - u.max())
    return p / p.sum()


def fit_logistic(x: np.ndarray, y: np.ndarray, l2: float = 1.0, iterations: int = 50) -> np.ndarray:
    """切片つきのロジスティック回帰(最後の要素が切片。切片には L2 をかけない)"""
    xb = np.hstack([x, np.ones((len(x), 1))])
    w = np.zeros(xb.shape[1])
    penalty = np.full(xb.shape[1], l2)
    penalty[-1] = 0.0

    for _ in range(iterations):
        p = 1 / (1 + np.exp(-np.clip(xb @ w, -30, 30)))
        grad = xb.T @ (y - p) - penalty * w
        hess = -(xb * (p * (1 - p))[:, None]).T @ xb - np.diag(penalty) - 1e-9 * np.eye(len(w))
        step = np.linalg.solve(hess, grad)
        w -= step

        if np.abs(step).max() < 1e-8:
            break

    return w


def logistic_proba(x: np.ndarray, w: np.ndarray) -> np.ndarray:
    return 1 / (1 + np.exp(-np.clip(np.hstack([x, np.ones((len(x), 1))]) @ w, -30, 30)))


def auc(scores: np.ndarray, labels: np.ndarray) -> float:
    """ROC 曲線の下の面積(同じ値は半分ずつ数える)"""
    labels = np.asarray(labels, dtype=bool)
    pos, neg = labels.sum(), (~labels).sum()

    if pos == 0 or neg == 0:
        return np.nan

    order = np.argsort(scores, kind="mergesort")
    ranks = np.empty(len(scores))
    s = np.asarray(scores)[order]
    i = 0

    while i < len(s):
        j = i
        while j + 1 < len(s) and s[j + 1] == s[i]:
            j += 1
        ranks[order[i:j + 1]] = (i + j) / 2 + 1
        i = j + 1

    return float((ranks[labels].sum() - pos * (pos + 1) / 2) / (pos * neg))


# ------------------------------------------------------------
# リアルタイムの予測
# ------------------------------------------------------------
def save_model(path: Path, kind: str, features: list[str], scaler: Scaler, weights: np.ndarray, cfg: Config, meta: dict) -> None:
    """学習したモデルを JSON に書き出す(OnlineArrivalPredictor や、今後 Unity 側で同じ計算をするときに読む)"""
    data = {"kind": kind, "features": features, "featureLabels": [FEATURE_LABELS[f] for f in features],
            "mean": scaler.mean.tolist(), "std": scaler.std.tolist(), "weights": weights.tolist(),
            "config": asdict(cfg), **meta}
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")


class OnlineArrivalPredictor:
    """リアルタイム用。フレームを1つずつ add_frame で渡し、predict で
    「この先 horizon 秒以内に、それぞれの物体へ到達する確率」を返す(save_model で書き出したロジスティック回帰の JSON を使う)。

    直近 window + 数秒分のフレームだけを貯め、そのたびに PersonStream を作り直して特徴量を求める
    (オフラインの解析と同じ計算になる)。まだ「離れたのを見ていない」物体(到達しえない物体)は 0 を返す。
    """

    def __init__(self, model_path: Path, objects: dict[str, tuple[np.ndarray, float]]):
        model = json.loads(Path(model_path).read_text(encoding="utf-8"))
        self.features = model["features"]
        self.mean, self.std = np.array(model["mean"]), np.array(model["std"])
        self.weights = np.array(model["weights"])
        self.cfg = Config(**model["config"])
        self.objects = objects
        self.buffer: list[dict] = []
        self.keep = self.cfg.window + 3.0

    def add_frame(self, time: float, origin, direction, shoulder_left, shoulder_right, distances: dict[str, float],
                  led: dict[str, bool] | None = None, present: bool = True, dt: float = 1 / 30) -> None:
        nan3 = [np.nan] * 3
        self.buffer.append({"time": time, "dt": dt, "present": present, "has_gaze": direction is not None,
                            "origin": origin if origin is not None else nan3, "direction": direction if direction is not None else nan3,
                            "shoulder_left": shoulder_left if shoulder_left is not None else nan3,
                            "shoulder_right": shoulder_right if shoulder_right is not None else nan3,
                            "dist": distances, "led": led or {}})

        while self.buffer and self.buffer[0]["time"] < time - self.keep:
            self.buffer.pop(0)

    def predict(self, armed: dict[str, bool] | None = None) -> dict[str, float]:
        """物体ごとの確率。armed(到達しうるか)を渡さなければ、貯めたフレームの中で leave m より離れていたことがあるかで決める"""
        if not self.buffer:
            return {n: 0.0 for n in self.objects}

        frames = {k: np.array([f[k] for f in self.buffer]) for k in
                  ("time", "dt", "present", "has_gaze", "origin", "direction", "shoulder_left", "shoulder_right")}
        distances = {n: np.array([f["dist"].get(n, np.nan) for f in self.buffer], dtype=float) for n in self.objects}
        led = {n: np.array([bool(f["led"].get(n, False)) for f in self.buffer]) for n in self.objects}
        stream = PersonStream("online", frames, distances, self.objects, led, self.cfg)
        feats = stream.features(float(frames["time"][-1]))

        if feats is None:
            return {n: 0.0 for n in self.objects}

        x = np.nan_to_num((matrix([feats[n] for n in self.objects], self.features) - self.mean) / self.std)
        p = logistic_proba(x, self.weights)
        out = {}

        for name, prob in zip(self.objects, p):
            can = armed[name] if armed is not None else bool(stream.armed[name][-1])
            out[name] = float(prob) if can else 0.0

        return out
