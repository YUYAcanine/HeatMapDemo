using System.Collections.Generic;
using System.IO;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// 点灯条件ごとに「どの対象が何フレーム注視されたか」の表を TMP に出力する。
//
// ScoreHeatSummary が出した視線 JSON と LightSummary が出したライト JSON を読み込む。
// 2 つは同じ骨格 JSON から作られていて frameIndex が 1 対 1 に対応しているため、
// 時刻での突き合わせは不要でそのまま同じ行を参照できる。
//
//   行 = 点灯条件(Light1 ON / Light2 ON / Light3 ON)
//   列 = その条件のあいだに注視されていた対象のフレーム数
//
// 単独点灯のフレームだけを対象にする。全消灯・同時点灯・未取得のフレームは除外。
//
// 入力元: Assets/Data/Analyze/output/<dataFolder>/
public class GazeTable : MonoBehaviour
{
    [Header("Input")]
    [Tooltip("読み込むフォルダ名。Assets/Data/Analyze/output/<dataFolder>/ から読む。")]
    [SerializeField] private string dataFolder = "sou1";

    [SerializeField] private string gazeJson = "gaze.json";
    [SerializeField] private string lightJson = "lightframe.json";

    [Header("Output")]
    [SerializeField] private TMP_Text resultText;

    [Tooltip("押すと集計を実行するボタン。OnClick に手動で登録済みなら空でよい。")]
    [SerializeField] private Button analyzeButton;

    [Tooltip("シーン再生時に自動で集計する。")]
    [SerializeField] private bool analyzeOnStart = true;

    private const string InputFolder = "Data/Analyze/output";

    private GazeFile gaze;
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
    // 集計本体
    // =========================================================
    [ContextMenu("Analyze")]
    public void Analyze()
    {
        if (!LoadAll())
            return;

        Report(BuildReport());
    }

    private string BuildReport()
    {
        List<string> names = gaze.targetNames;
        int size = names.Count;

        int[,] gazeCount = new int[size, size];
        int[] conditionFrames = new int[size];
        int[] noHitFrames = new int[size];
        int excluded = 0;

        for (int f = 0; f < gaze.frames.Count; f++)
        {
            int condition =
                SingleLightIndex(light.frames[f].lightState, names);

            if (condition < 0)
            {
                excluded++;
                continue;
            }

            conditionFrames[condition]++;

            int gazed = names.IndexOf(gaze.frames[f].gazeTarget);

            if (gazed >= 0)
                gazeCount[condition, gazed]++;
            else
                noHitFrames[condition]++;
        }

        StringBuilder text = new StringBuilder();

        text.AppendLine("<b>=== Gaze Table ===</b>");
        text.AppendLine($"gaze  : {dataFolder}/{gazeJson}  (cone {gaze.coneAngle:F1}deg, down {gaze.downwardAngle:F1}deg)");
        text.AppendLine($"light : {dataFolder}/{lightJson}");
        text.AppendLine($"frames {gaze.frames.Count}");
        text.AppendLine();

        text.AppendLine("<b>--- Gaze Frames by LED Condition ---</b>");
        text.AppendLine("  row = LED condition,  col = gazed target (frames)");
        text.AppendLine("<mspace=0.55em>");

        StringBuilder header = new StringBuilder("  Condition  ");

        foreach (string name in names)
            header.Append(Pad(name, 7));

        header.Append(Pad("NoHit", 8));
        header.Append(Pad("Total", 8));
        text.AppendLine(header.ToString());

        for (int c = 0; c < size; c++)
        {
            StringBuilder line =
                new StringBuilder("  " + (names[c] + " ON").PadRight(11));

            for (int t = 0; t < size; t++)
                line.Append(Pad(gazeCount[c, t].ToString(), 7));

            line.Append(Pad(noHitFrames[c].ToString(), 8));
            line.Append(Pad(conditionFrames[c].ToString(), 8));
            text.AppendLine(line.ToString());
        }

        text.AppendLine("</mspace>");
        text.AppendLine();

        // 割合(その条件のフレーム数を分母にした注視率)
        text.AppendLine("<b>--- Same, as % of condition frames ---</b>");
        text.AppendLine("<mspace=0.55em>");
        text.AppendLine(header.ToString());

        for (int c = 0; c < size; c++)
        {
            StringBuilder line =
                new StringBuilder("  " + (names[c] + " ON").PadRight(11));

            for (int t = 0; t < size; t++)
                line.Append(Pad(Percent(gazeCount[c, t], conditionFrames[c]), 7));

            line.Append(Pad(Percent(noHitFrames[c], conditionFrames[c]), 8));
            line.Append(Pad(conditionFrames[c].ToString(), 8));
            text.AppendLine(line.ToString());
        }

        text.AppendLine("</mspace>");
        text.AppendLine("  Total = frames in that condition (denominator)");
        text.AppendLine(
            $"  excluded {excluded} frames (all off / multi on / no data)"
        );

        return text.ToString();
    }

    // 単独点灯のときだけ条件のインデックスを返す。
    // 全消灯("None") / 未取得("NoData") / 同時点灯("Light1+Light3") は -1。
    private static int SingleLightIndex(string lightState, List<string> names)
    {
        if (string.IsNullOrEmpty(lightState) || lightState.Contains("+"))
            return -1;

        return names.IndexOf(lightState);
    }

    private static string Pad(string value, int width)
    {
        return value.PadLeft(width);
    }

    private static string Percent(int value, int total)
    {
        return total > 0
            ? (100f * value / total).ToString("F1")
            : "-";
    }

    private void Report(string message)
    {
        if (resultText != null)
            resultText.text = message;

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
        light = Load<LightFile>(lightJson);

        if (gaze == null || light == null)
            return false;

        if (gaze.frames.Count != light.frames.Count)
        {
            Debug.LogError(
                "フレーム数が一致しません: " +
                $"gaze={gaze.frames.Count} light={light.frames.Count}。" +
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
    private class LightFile
    {
        public List<string> lightNames = new();
        public List<LightFrame> frames = new();
    }

    [System.Serializable]
    private class LightFrame
    {
        public int frameIndex;
        public float unityTime;
        public string lightState;
    }
}
