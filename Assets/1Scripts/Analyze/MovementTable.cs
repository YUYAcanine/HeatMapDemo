using System.Collections.Generic;
using System.IO;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// 点灯条件ごとに「移動方向と各対象方向の内積」の表を TMP に出力する。
//
// VectorSummary が出した接近 JSON と LightSummary が出したライト JSON を読み込む。
// 2 つは同じ骨格 JSON から作られていて frameIndex が 1 対 1 に対応しているため、
// 時刻での突き合わせは不要でそのまま同じ行を参照できる。
//
//   行 = 点灯条件(Light1 ON / Light2 ON / Light3 ON)
//   列 = 各対象への Dot Sum と Cos Average
//
// このスクリプトは一切データ加工をしない。
// VectorSummary が valid とマークしたフレーム(Pelvis が取れ、速度が maxSpeed 以内)の
// dots / cosines を、点灯条件ごとに足して数えるだけ。追加の閾値や除外は持たない。
//
// 単独点灯のフレームだけを行にする。全消灯・同時点灯・未取得のフレームは条件が
// 定まらないので集計対象外。
//
// 入力元: Assets/Data/Analyze/output/<dataFolder>/
public class MovementTable : MonoBehaviour
{
    [Header("Input")]
    [Tooltip("読み込むフォルダ名。Assets/Data/Analyze/output/<dataFolder>/ から読む。")]
    [SerializeField] private string dataFolder = "sou1";

    [SerializeField] private string moveJson = "move.json";
    [SerializeField] private string lightJson = "lightframe.json";

    [Header("Output")]
    [SerializeField] private TMP_Text resultText;

    [Tooltip("押すと集計を実行するボタン。OnClick に手動で登録済みなら空でよい。")]
    [SerializeField] private Button analyzeButton;

    [Tooltip("シーン再生時に自動で集計する。")]
    [SerializeField] private bool analyzeOnStart = true;

    private const string InputFolder = "Data/Analyze/output";

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
        List<string> names = move.targetNames;
        int size = names.Count;

        float[,] dotSum = new float[size, size];
        float[,] cosineSum = new float[size, size];
        int[] sampleCount = new int[size];

        int[] conditionFrames = new int[size];
        int outsideCondition = 0;
        int notValid = 0;

        for (int f = 0; f < move.frames.Count; f++)
        {
            int condition =
                SingleLightIndex(light.frames[f].lightState, names);

            if (condition < 0)
            {
                outsideCondition++;
                continue;
            }

            conditionFrames[condition]++;

            MoveFrame frame = move.frames[f];

            // VectorSummary が評価したフレームだけを、そのまま足す
            if (!frame.valid)
            {
                notValid++;
                continue;
            }

            sampleCount[condition]++;

            for (int t = 0; t < size; t++)
            {
                if (t < frame.dots.Count)
                    dotSum[condition, t] += frame.dots[t];

                if (t < frame.cosines.Count)
                    cosineSum[condition, t] += frame.cosines[t];
            }
        }

        StringBuilder text = new StringBuilder();

        text.AppendLine("<b>=== Movement Table ===</b>");
        text.AppendLine(
            $"move  : {dataFolder}/{moveJson}  " +
            $"(span {move.velocitySpan:F2}s, maxSpeed {move.maxSpeed:F1}m/s)"
        );
        text.AppendLine($"light : {dataFolder}/{lightJson}");
        text.AppendLine($"frames {move.frames.Count}");
        text.AppendLine();

        // ---- Cos Average ----
        text.AppendLine("<b>--- Cos Average by LED Condition ---</b>");
        text.AppendLine(
            "  row = LED condition,  col = target,  " +
            "mean cos between movement and target direction"
        );
        text.AppendLine("<mspace=0.55em>");
        text.AppendLine(Header(names));

        for (int c = 0; c < size; c++)
        {
            StringBuilder line = Row(names[c]);

            for (int t = 0; t < size; t++)
            {
                line.Append(
                    Pad(
                        sampleCount[c] > 0
                            ? (cosineSum[c, t] / sampleCount[c]).ToString("F3")
                            : "-",
                        9
                    )
                );
            }

            line.Append(Pad(sampleCount[c].ToString(), 9));
            line.Append(Pad(conditionFrames[c].ToString(), 9));
            text.AppendLine(line.ToString());
        }

        text.AppendLine("</mspace>");
        text.AppendLine();

        // ---- Dot Sum ----
        text.AppendLine("<b>--- Dot Sum by LED Condition ---</b>");
        text.AppendLine(
            "  row = LED condition,  col = target,  " +
            "sum of Vector3.Dot(movement, target)"
        );
        text.AppendLine("<mspace=0.55em>");
        text.AppendLine(Header(names));

        for (int c = 0; c < size; c++)
        {
            StringBuilder line = Row(names[c]);

            for (int t = 0; t < size; t++)
                line.Append(Pad(dotSum[c, t].ToString("F4"), 9));

            line.Append(Pad(sampleCount[c].ToString(), 9));
            line.Append(Pad(conditionFrames[c].ToString(), 9));
            text.AppendLine(line.ToString());
        }

        text.AppendLine("</mspace>");
        text.AppendLine("  positive = moving toward target / negative = moving away");
        text.AppendLine("  Samples = evaluated frames,  Frames = frames in that condition");
        text.AppendLine(
            $"  outside condition {outsideCondition} frames (all off / multi on / no data),  " +
            $"not evaluated {notValid} frames (over max speed / no pelvis)"
        );

        return text.ToString();
    }

    private static string Header(List<string> names)
    {
        StringBuilder header = new StringBuilder("  Condition  ");

        foreach (string name in names)
            header.Append(Pad(name, 9));

        header.Append(Pad("Samples", 9));
        header.Append(Pad("Frames", 9));
        return header.ToString();
    }

    private static StringBuilder Row(string name)
    {
        return new StringBuilder("  " + (name + " ON").PadRight(11));
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
        move = Load<MoveFile>(moveJson);
        light = Load<LightFile>(lightJson);

        if (move == null || light == null)
            return false;

        if (move.frames.Count != light.frames.Count)
        {
            Debug.LogError(
                "フレーム数が一致しません: " +
                $"move={move.frames.Count} light={light.frames.Count}。" +
                "同じ骨格 JSON から出力し直してください。",
                this
            );

            return false;
        }

        if (move.targetNames.Count == 0)
        {
            Debug.LogError("move JSON に targetNames がありません。", this);
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
        public List<float> dots = new();
        public List<float> cosines = new();
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
