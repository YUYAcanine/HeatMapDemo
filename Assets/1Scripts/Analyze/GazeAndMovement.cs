using System.Collections.Generic;
using System.IO;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// ScoreHeatSummary / VectorSummary / LightSummary が出力した 3 つの JSON を読み込み、
// 「ライトが点く -> そちらを見る -> そちらへ動く」の時系列連鎖を解析して TMP に出力する。
//
// 3 ファイルはすべて同じ骨格 JSON から作られていて frameIndex が 1 対 1 に対応しているため、
// 時刻での突き合わせは不要でそのまま同じ行を参照できる。
//
// 各試行(ライトの点灯区間)について求めるもの:
//   t_light : 点灯が始まった時刻                     (LightSummary の episodes)
//   t_gaze  : その対象を注視し始めた時刻             (gazeTarget が連続でその対象になる)
//   t_move  : その対象へ近づき始めた時刻             (closingSpeed が連続で閾値超え)
//   dt1 = t_gaze - t_light  検出潜時
//   dt2 = t_move - t_gaze   行動開始潜時
//
// さらに、注視した対象と移動した対象の対応を transition matrix にまとめる。
//
// 入力元: Assets/Data/Analyze/output/<dataFolder>/
public class GazeAndMovement : MonoBehaviour
{
    [Header("Input")]
    [Tooltip("読み込むフォルダ名。Assets/Data/Analyze/output/<dataFolder>/ から読む。")]
    [SerializeField] private string dataFolder = "sou1";

    [SerializeField] private string gazeJson = "gaze.json";
    [SerializeField] private string moveJson = "move.json";
    [SerializeField] private string lightJson = "lightframe.json";

    [Header("Trial Selection")]
    [Tooltip("単独点灯の区間だけを試行として扱う(同時点灯を除外)。")]
    [SerializeField] private bool singleLightOnly = true;

    [Tooltip("この秒数より短い点灯区間は試行として扱わない。0 ですべての点灯を試行にする。")]
    [SerializeField] private float minEpisodeDuration = 0f;

    [Tooltip("点灯開始から何秒後まで注視・接近を探すか。")]
    [SerializeField] private float searchWindow = 5f;

    [Tooltip(
        "次の点灯が始まったら探索を打ち切る。" +
        "点灯間隔が searchWindow より短い記録で、次の刺激への反応を" +
        "手前の試行に取り違えるのを防ぐ。"
    )]
    [SerializeField] private bool stopAtNextEpisode = true;

    [Header("Gaze Onset")]
    [Tooltip("注視開始とみなすために必要な連続ヒットフレーム数。")]
    [SerializeField] private int gazeHoldFrames = 3;

    [Header("Movement Onset")]
    [Tooltip("接近とみなす closingSpeed の閾値(m/s)。")]
    [SerializeField] private float approachSpeedThreshold = 0.15f;

    [Tooltip("接近開始とみなすために必要な連続フレーム数。")]
    [SerializeField] private int approachHoldFrames = 9;

    [Tooltip(
        "接近開始の直前に、閾値未満だった期間を要求するフレーム数。" +
        "0 にすると「点灯前から既に近づき続けていた」だけの試行も " +
        "movement onset として拾ってしまう。"
    )]
    [SerializeField] private int approachQuietFrames = 9;

    [Header("Gaze -> Move Matching")]
    [Tooltip(
        "注視開始から何秒以内に始まった接近を「見てから近づいた」とみなすか。" +
        "点灯区間の終わりに縛られず、注視した時点から測る。"
    )]
    [SerializeField] private float gazeToMoveWindow = 3f;

    [Header("Output")]
    [SerializeField] private TMP_Text resultText;

    [Tooltip("押すと解析を実行するボタン。OnClick に手動で登録済みなら空でよい。")]
    [SerializeField] private Button analyzeButton;

    [Tooltip("シーン再生時に自動で解析する。")]
    [SerializeField] private bool analyzeOnStart = true;

    private const string InputFolder = "Data/Analyze/output";
    private const string NoneLabel = "None";

    private GazeFile gaze;
    private MoveFile move;
    private LightFile light;

    private void Start()
    {
        if (analyzeButton != null)
            analyzeButton.onClick.AddListener(Analyze);

        if (analyzeOnStart)
            Analyze();
    }

    private void OnDestroy()
    {
        if (analyzeButton != null)
            analyzeButton.onClick.RemoveListener(Analyze);
    }

    // =========================================================
    // 解析本体
    // =========================================================
    [ContextMenu("Analyze")]
    public void Analyze()
    {
        if (!LoadAll())
            return;

        List<Trial> trials = BuildTrials();

        Report(BuildReport(trials));
    }

    // =========================================================
    // 試行ごとのイベント抽出
    // =========================================================
    private List<Trial> BuildTrials()
    {
        List<Trial> trials = new List<Trial>();

        int lastFrame = gaze.frames.Count - 1;

        for (int e = 0; e < light.episodes.Count; e++)
        {
            LightEpisode episode = light.episodes[e];

            if (episode.duration < minEpisodeDuration)
                continue;

            if (singleLightOnly && episode.lightState.Contains("+"))
                continue;

            int targetIndex =
                gaze.targetNames.IndexOf(episode.lightState);

            if (targetIndex < 0)
                continue;

            Trial trial = new Trial
            {
                lightName = episode.lightState,
                lightFrame = episode.startFrame,
                lightTime = episode.startTime
            };

            // 点灯開始から searchWindow 秒後まで(フレーム番号に換算)
            int searchEnd = lastFrame;

            for (int f = episode.startFrame; f <= lastFrame; f++)
            {
                if (gaze.frames[f].unityTime - episode.startTime > searchWindow)
                {
                    searchEnd = f;
                    break;
                }
            }

            // 次の点灯が始まったらそこで打ち切る
            if (stopAtNextEpisode && e + 1 < light.episodes.Count)
            {
                int nextStart = light.episodes[e + 1].startFrame - 1;

                if (nextStart < searchEnd)
                    searchEnd = nextStart;
            }

            if (searchEnd < episode.startFrame)
                continue;

            // 点灯した対象そのものへの注視 / 接近
            trial.gazeFrame =
                FindGazeOnset(targetIndex, episode.startFrame, searchEnd);

            trial.moveFrame =
                FindMoveOnset(targetIndex, episode.startFrame, searchEnd);

            // 対象を限定せず、最初に注視した / 最初に近づいた対象
            trial.firstGaze =
                FindFirstTarget(true, episode.startFrame, searchEnd);

            trial.firstMove =
                FindFirstTarget(false, episode.startFrame, searchEnd);

            // 注視が立った試行だけ、その注視以降の接近を探す。
            // 点灯区間ではなく注視時点から gazeToMoveWindow 秒を見る。
            int gazeMoveEnd =
                trial.firstGaze.frame >= 0
                    ? FrameAfter(trial.firstGaze.frame, gazeToMoveWindow)
                    : -1;

            if (stopAtNextEpisode && gazeMoveEnd >= 0 && e + 1 < light.episodes.Count)
            {
                int nextStart = light.episodes[e + 1].startFrame - 1;

                if (nextStart < gazeMoveEnd)
                    gazeMoveEnd = nextStart;
            }

            trial.gazeThenMove =
                trial.firstGaze.frame >= 0 && gazeMoveEnd >= trial.firstGaze.frame
                    ? FindFirstTarget(false, trial.firstGaze.frame, gazeMoveEnd)
                    : new TargetOnset { frame = -1, name = NoneLabel };

            trials.Add(trial);
        }

        return trials;
    }

    // from から seconds 秒後に相当するフレーム番号(記録の末尾で打ち切り)。
    private int FrameAfter(int from, float seconds)
    {
        int last = gaze.frames.Count - 1;
        float limit = gaze.frames[from].unityTime + seconds;

        for (int f = from; f <= last; f++)
        {
            if (gaze.frames[f].unityTime > limit)
                return f - 1;
        }

        return last;
    }

    // 連続 hold フレーム満たした最初の位置(その連続の先頭フレーム)を返す。無ければ -1。
    private int FindGazeOnset(int targetIndex, int from, int to)
    {
        string targetName = gaze.targetNames[targetIndex];
        int run = 0;

        for (int f = from; f <= to; f++)
        {
            if (gaze.frames[f].gazeTarget == targetName)
            {
                run++;

                if (run >= gazeHoldFrames)
                    return f - gazeHoldFrames + 1;
            }
            else
            {
                run = 0;
            }
        }

        return -1;
    }

    private int FindMoveOnset(int targetIndex, int from, int to)
    {
        int run = 0;

        for (int f = from; f <= to; f++)
        {
            MoveFrame frame = move.frames[f];

            bool approaching =
                frame.valid &&
                targetIndex < frame.closingSpeeds.Count &&
                frame.closingSpeeds[targetIndex] >= approachSpeedThreshold;

            if (approaching)
            {
                run++;

                if (run >= approachHoldFrames)
                {
                    int onset = f - approachHoldFrames + 1;

                    // 直前が静止(または後退)していたときだけ「動き出し」とみなす
                    if (IsQuietBefore(targetIndex, onset))
                        return onset;
                }
            }
            else
            {
                run = 0;
            }
        }

        return -1;
    }

    // onset の直前 approachQuietFrames 分が閾値未満かどうか。
    // 記録の先頭に張り付いている場合は判定できないので false を返す。
    private bool IsQuietBefore(int targetIndex, int onset)
    {
        if (approachQuietFrames <= 0)
            return true;

        int from = onset - approachQuietFrames;

        if (from < 0)
            return false;

        for (int f = from; f < onset; f++)
        {
            MoveFrame frame = move.frames[f];

            if (!frame.valid || targetIndex >= frame.closingSpeeds.Count)
                continue;

            if (frame.closingSpeeds[targetIndex] >= approachSpeedThreshold)
                return false;
        }

        return true;
    }

    // 3 つの対象のうち、最も早くオンセットが立った対象を返す。
    private TargetOnset FindFirstTarget(bool useGaze, int from, int to)
    {
        TargetOnset best = new TargetOnset { frame = -1, name = NoneLabel };

        for (int t = 0; t < gaze.targetNames.Count; t++)
        {
            int onset =
                useGaze
                    ? FindGazeOnset(t, from, to)
                    : FindMoveOnset(t, from, to);

            if (onset < 0)
                continue;

            if (best.frame < 0 || onset < best.frame)
            {
                best.frame = onset;
                best.name = gaze.targetNames[t];
            }
        }

        return best;
    }

    // =========================================================
    // レポート生成
    // =========================================================
    private string BuildReport(List<Trial> trials)
    {
        StringBuilder text = new StringBuilder();

        text.AppendLine("<b>=== Gaze & Movement Analysis ===</b>");
        text.AppendLine(
            $"gaze  : {dataFolder}/{gazeJson}  (cone {gaze.coneAngle:F1}deg, down {gaze.downwardAngle:F1}deg)"
        );
        text.AppendLine(
            $"move  : {dataFolder}/{moveJson}  (span {move.velocitySpan:F2}s, maxSpeed {move.maxSpeed:F1}m/s, " +
            $"th {approachSpeedThreshold:F2}m/s x{approachHoldFrames}f, quiet {approachQuietFrames}f)"
        );
        text.AppendLine(
            $"light : {dataFolder}/{lightJson}  (episodes {light.episodes.Count})"
        );
        text.AppendLine(
            $"frames {gaze.frames.Count} / trials {trials.Count}" +
            (singleLightOnly ? " (single-light" : " (all") +
            $", >={minEpisodeDuration:F1}s" +
            (stopAtNextEpisode ? ", cut at next" : "") + ")"
        );
        text.AppendLine();

        AppendTrialTable(text, trials);
        AppendClassCounts(text, trials);
        AppendLatency(text, trials);
        AppendMatrix(text, trials, MatrixKind.LightGaze);
        AppendMatrix(text, trials, MatrixKind.GazeMove);
        AppendMatrix(text, trials, MatrixKind.GazeThenMove);

        return text.ToString();
    }

    private void AppendTrialTable(StringBuilder text, List<Trial> trials)
    {
        text.AppendLine("<b>--- Per Trial ---</b>");
        text.AppendLine("<mspace=0.55em>");
        text.AppendLine("  #  light   t_light  t_gaze  t_move    dt1    dt2  class");

        for (int i = 0; i < trials.Count; i++)
        {
            Trial trial = trials[i];

            text.AppendLine(
                Num(i + 1, 3) +
                "  " + trial.lightName.PadRight(7) +
                Time(trial.lightTime, 8) +
                Time(TimeOf(trial.gazeFrame), 8) +
                Time(TimeOf(trial.moveFrame), 8) +
                Time(trial.Dt1(this), 7) +
                Time(trial.Dt2(this), 7) +
                "  " + trial.Classify()
            );
        }

        text.AppendLine("</mspace>");
        text.AppendLine();
    }

    private void AppendClassCounts(StringBuilder text, List<Trial> trials)
    {
        Dictionary<string, int> counts = new Dictionary<string, int>();

        foreach (Trial trial in trials)
        {
            string key = trial.Classify();
            counts.TryGetValue(key, out int value);
            counts[key] = value + 1;
        }

        text.AppendLine("<b>--- Event Class ---</b>");
        text.AppendLine("<mspace=0.55em>");

        foreach (string key in Trial.ClassOrder)
        {
            counts.TryGetValue(key, out int value);
            text.AppendLine("  " + key.PadRight(20) + Num(value, 3));
        }

        text.AppendLine("</mspace>");
        text.AppendLine();
    }

    private void AppendLatency(StringBuilder text, List<Trial> trials)
    {
        List<float> dt1 = new List<float>();
        List<float> dt2 = new List<float>();
        List<float> total = new List<float>();

        foreach (Trial trial in trials)
        {
            float a = trial.Dt1(this);
            float b = trial.Dt2(this);

            if (!float.IsNaN(a))
                dt1.Add(a);

            // dt2 は「見てから動いた」順序が成立した試行のみ
            if (!float.IsNaN(b) && b >= 0f)
            {
                dt2.Add(b);
                total.Add(a + b);
            }
        }

        text.AppendLine("<b>--- Latency ---</b>");
        text.AppendLine("<mspace=0.55em>");
        AppendStat(text, "dt1 light->gaze ", dt1);
        AppendStat(text, "dt2 gaze->move  ", dt2);
        AppendStat(text, "    light->move ", total);
        text.AppendLine("</mspace>");
        text.AppendLine();
    }

    private void AppendStat(StringBuilder text, string label, List<float> values)
    {
        if (values.Count == 0)
        {
            text.AppendLine("  " + label + "  n=0");
            return;
        }

        values.Sort();

        float median = values[values.Count / 2];
        float sum = 0f;

        foreach (float value in values)
            sum += value;

        text.AppendLine(
            "  " + label +
            "  median " + median.ToString("F2") + "s" +
            "  mean " + (sum / values.Count).ToString("F2") + "s" +
            "  range " + values[0].ToString("F2") +
            "-" + values[values.Count - 1].ToString("F2") + "s" +
            "  n=" + values.Count
        );
    }

    private enum MatrixKind
    {
        // 点灯したライト(行) x 最初に注視した対象(列)。刺激を正しく検出できたか。
        LightGaze,

        // 最初に注視した対象(行) x 最初に接近した対象(列)。順序は問わない。
        GazeMove,

        // 最初に注視した対象(行) x その注視より後に始まった接近(列)。
        // 「発見してから近づいた」だけを数える。
        GazeThenMove
    }

    private void AppendMatrix(StringBuilder text, List<Trial> trials, MatrixKind kind)
    {
        List<string> names = gaze.targetNames;
        int size = names.Count;

        int[,] matrix = new int[size, size + 1];
        int diagonal = 0;
        int total = 0;
        int rows = 0;

        foreach (Trial trial in trials)
        {
            string rowName =
                kind == MatrixKind.LightGaze
                    ? trial.lightName
                    : trial.firstGaze.name;

            string colName;

            switch (kind)
            {
                case MatrixKind.LightGaze:
                    colName = trial.firstGaze.name;
                    break;

                case MatrixKind.GazeMove:
                    colName = trial.firstMove.name;
                    break;

                default:
                    colName = trial.gazeThenMove.name;
                    break;
            }

            int row = names.IndexOf(rowName);

            if (row < 0)
                continue;

            rows++;

            int col = names.IndexOf(colName);

            matrix[row, col < 0 ? size : col]++;

            if (col < 0)
                continue;

            total++;

            if (row == col)
                diagonal++;
        }

        switch (kind)
        {
            case MatrixKind.LightGaze:
                text.AppendLine("<b>--- Matrix 1: Light x First Gaze ---</b>");
                text.AppendLine("  row = lit light,  col = first gazed target");
                break;

            case MatrixKind.GazeMove:
                text.AppendLine("<b>--- Matrix 2: First Gaze x First Move ---</b>");
                text.AppendLine(
                    "  row = first gazed target,  " +
                    "col = first approached target (order ignored)"
                );
                break;

            default:
                text.AppendLine("<b>--- Matrix 3: Gaze -> Move (discover then approach) ---</b>");
                text.AppendLine(
                    "  row = gazed target,  " +
                    $"col = target approached within {gazeToMoveWindow:F1}s after that gaze"
                );
                break;
        }

        text.AppendLine("<mspace=0.55em>");

        StringBuilder header = new StringBuilder("           ");

        foreach (string name in names)
            header.Append(Text(name, 7));

        header.Append(Text(NoneLabel, 7));
        text.AppendLine(header.ToString());

        for (int row = 0; row < size; row++)
        {
            StringBuilder line = new StringBuilder("  " + names[row].PadRight(9));

            for (int col = 0; col <= size; col++)
                line.Append(Num(matrix[row, col], 7));

            text.AppendLine(line.ToString());
        }

        text.AppendLine("</mspace>");

        if (total > 0)
        {
            float rate = 100f * diagonal / total;
            float chance = 100f / size;

            text.AppendLine(
                $"  diagonal {diagonal}/{total} ({rate:F1}%)  chance {chance:F1}%" +
                (rows > total ? $"  (+{rows - total} no-move)" : "")
            );
        }
        else
        {
            text.AppendLine("  (no matching trials)");
        }

        text.AppendLine();
    }

    // =========================================================
    // 表示補助
    // =========================================================
    private float TimeOf(int frame)
    {
        return frame < 0 ? float.NaN : gaze.frames[frame].unityTime;
    }

    private static string Num(int value, int width)
    {
        return value.ToString().PadLeft(width);
    }

    private static string Text(string value, int width)
    {
        return value.PadLeft(width);
    }

    private static string Time(float value, int width)
    {
        return (float.IsNaN(value) ? "-" : value.ToString("F2")).PadLeft(width);
    }

    private void Report(string message)
    {
        if (resultText != null)
            resultText.text = message;

        // TMP のリッチテキストタグを外してコンソールにも出す
        Debug.Log(
            message
                .Replace("<mspace=0.55em>", "")
                .Replace("</mspace>", "")
                .Replace("<b>", "")
                .Replace("</b>", ""),
            this
        );
    }

    // =========================================================
    // 読み込み
    // =========================================================
    private bool LoadAll()
    {
        gaze = Load<GazeFile>(gazeJson);
        move = Load<MoveFile>(moveJson);
        light = Load<LightFile>(lightJson);

        if (gaze == null || move == null || light == null)
            return false;

        if (gaze.frames.Count != move.frames.Count ||
            gaze.frames.Count != light.frames.Count)
        {
            Debug.LogError(
                "3 ファイルのフレーム数が一致しません: " +
                $"gaze={gaze.frames.Count} move={move.frames.Count} light={light.frames.Count}。" +
                "同じ骨格 JSON から出力し直してください。",
                this
            );

            return false;
        }

        if (gaze.targetNames.Count == 0)
        {
            Debug.LogError("gaze JSON に targetNames がありません。", this);
            return false;
        }

        return true;
    }

    private T Load<T>(string fileName) where T : class
    {
        string path =
            Path.Combine(
                Application.dataPath,
                InputFolder,
                (dataFolder ?? "").Trim().Trim('/', '\\'),
                fileName
            );

        if (!File.Exists(path))
        {
            Debug.LogError($"JSON が見つかりません: {path}", this);
            return null;
        }

        T result = JsonUtility.FromJson<T>(File.ReadAllText(path));

        if (result == null)
            Debug.LogError($"JSON を読み込めませんでした: {path}", this);

        return result;
    }

    // =========================================================
    // 試行
    // =========================================================
    private class TargetOnset
    {
        public int frame;
        public string name;
    }

    private class Trial
    {
        public static readonly string[] ClassOrder =
        {
            "gaze -> approach",
            "approach -> gaze",
            "gaze, no approach",
            "approach, no gaze",
            "no response"
        };

        public string lightName;
        public int lightFrame;
        public float lightTime;

        public int gazeFrame = -1;
        public int moveFrame = -1;

        public TargetOnset firstGaze;
        public TargetOnset firstMove;

        // firstGaze より後に始まった最初の接近。「発見 -> 接近」用。
        public TargetOnset gazeThenMove;

        public float Dt1(GazeAndMovement owner)
        {
            return gazeFrame < 0
                ? float.NaN
                : owner.TimeOf(gazeFrame) - lightTime;
        }

        public float Dt2(GazeAndMovement owner)
        {
            return gazeFrame < 0 || moveFrame < 0
                ? float.NaN
                : owner.TimeOf(moveFrame) - owner.TimeOf(gazeFrame);
        }

        public string Classify()
        {
            if (gazeFrame >= 0 && moveFrame >= 0)
                return moveFrame >= gazeFrame ? ClassOrder[0] : ClassOrder[1];

            if (gazeFrame >= 0)
                return ClassOrder[2];

            if (moveFrame >= 0)
                return ClassOrder[3];

            return ClassOrder[4];
        }
    }

    // =========================================================
    // 入力 JSON の構造(必要なフィールドだけ定義。他は無視される)
    // =========================================================
    [System.Serializable]
    private class GazeFile
    {
        public List<string> targetNames = new();
        public float coneAngle;
        public float downwardAngle;
        public List<GazeFrame> frames = new();
    }

    [System.Serializable]
    private class GazeFrame
    {
        public int frameIndex;
        public float unityTime;
        public string gazeTarget;
    }

    [System.Serializable]
    private class MoveFile
    {
        public List<string> targetNames = new();
        public float velocitySpan;
        public float maxSpeed;
        public List<MoveFrame> frames = new();
    }

    [System.Serializable]
    private class MoveFrame
    {
        public int frameIndex;
        public float unityTime;
        public bool valid;
        public float speed;
        public List<float> closingSpeeds = new();
        public string moveTarget;
    }

    [System.Serializable]
    private class LightFile
    {
        public List<string> lightNames = new();
        public List<LightEpisode> episodes = new();
        public List<LightFrame> frames = new();
    }

    [System.Serializable]
    private class LightEpisode
    {
        public string lightState;
        public int startFrame;
        public int endFrame;
        public float startTime;
        public float endTime;
        public float duration;
    }

    [System.Serializable]
    private class LightFrame
    {
        public int frameIndex;
        public float unityTime;
        public string lightState;
    }
}
