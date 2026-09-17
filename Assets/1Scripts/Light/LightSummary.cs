using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;

// ライトログを骨格フレームに合わせて整形し、時系列 JSON として書き出すエクスポータ。
//
// ライトログ(*_light.json)は LightControl が独自のタイミングで記録しているため、
// 0.02〜0.13 秒の不定間隔で骨格の 30fps と揃っていない。そのままでは
// ScoreHeatSummary / VectorSummary の出力と行が対応しないので、骨格 JSON の
// フレーム時刻ごとに最近傍のライト記録を引き直して、同じ行数・同じ frameIndex に揃える。
//
// これで 3 つの JSON(視線・接近・ライト)がすべて frameIndex で直接結合できる。
//
// ScoreHeatSummary / VectorSummary と同じく、再生は一切行わずボタンを押した時点で
// 全フレームを一括処理する。シーン上のオブジェクトは参照しないので、
// どのシーンの空オブジェクトに付けても動く。
//
// 入力元: Assets/Data/Analyze/input/<inputFolder>/
// 出力先: Assets/Data/Analyze/output/<outputFolder>/<outputFileName>
public class LightSummary : MonoBehaviour
{
    [Header("Input")]
    [Tooltip("入力フォルダ名。Assets/Data/Analyze/input/<inputFolder>/ から読む。")]
    [SerializeField] private string inputFolder = "sou1";

    [Tooltip("フレームの時間軸として使う骨格 JSON。input フォルダ内のファイル名。")]
    [SerializeField] private string skeletonJson = "skeleton.json";

    [Tooltip("整形するライトログ。input フォルダ内のファイル名。")]
    [SerializeField] private string lightJson = "light_log.json";

    [Header("Matching")]
    [Tooltip("骨格フレームとライト記録の時刻差の許容値(秒)。超えた場合は未取得として扱う。")]
    [SerializeField] private float timeTolerance = 0.1f;

    [Header("Output")]
    [Tooltip("出力フォルダ名。Assets/Data/Analyze/output/<outputFolder>/ に保存される。")]
    [FormerlySerializedAs("dataFolder")]
    [SerializeField] private string outputFolder = "sou1";

    [Tooltip("保存するファイル名。拡張子は省略可。")]
    [SerializeField] private string outputFileName = "lightframe.json";

    [Tooltip("整形して出力する(ファイルサイズは大きくなる)。")]
    [SerializeField] private bool prettyPrint = true;

    [Header("UI")]
    [Tooltip("押すとエクスポートを実行するボタン。OnClick に手動で登録済みなら空でよい。")]
    [SerializeField] private Button exportButton;

    private const string InputRoot = "Data/Analyze/input";
    private const string OutputRoot = "Data/Analyze/output";

    // lightState に書き込む特殊値。
    // AllOffLabel : 3 つとも消灯しているフレーム
    // NoDataLabel : timeTolerance 以内に対応するライト記録が無かったフレーム
    private const string AllOffLabel = "None";
    private const string NoDataLabel = "NoData";

    // 点灯ラベル。ScoreHeatSummary / VectorSummary の対象名と一致させてある。
    private static readonly string[] LightNames = { "Light1", "Light2", "Light3" };

    private readonly List<FrameData> frames = new();
    private readonly List<LightFrame> lightFrames = new();

    private void Start()
    {
        if (exportButton != null)
            exportButton.onClick.AddListener(Export);
    }

    private void OnDestroy()
    {
        if (exportButton != null)
            exportButton.onClick.RemoveListener(Export);
    }

    // =========================================================
    // エクスポート本体。
    // Button の OnClick から直接呼べるよう public。
    // 再生せずに実行したい場合はコンポーネントの右クリックメニューからも呼べる。
    // =========================================================
    [ContextMenu("Export Light Summary")]
    public void Export()
    {
        if (!LoadSkeleton())
            return;

        if (!LoadLight())
            return;

        WriteJson(Analyze());
    }

    // =========================================================
    // 骨格フレームごとに最近傍のライト記録を引く。
    // ライトログは時刻の昇順なので、探索位置を前に進めるだけで済む。
    // =========================================================
    private LightSummaryData Analyze()
    {
        LightSummaryData summary = new LightSummaryData
        {
            skeletonJson = skeletonJson,
            lightJson = lightJson,
            timeTolerance = timeTolerance,
            allOffLabel = AllOffLabel,
            noDataLabel = NoDataLabel,
            lightSourceFrameCount = lightFrames.Count
        };

        foreach (string name in LightNames)
        {
            summary.lightNames.Add(name);
            summary.onFrames.Add(0);
        }

        int cursor = 0;

        foreach (FrameData frame in frames)
        {
            // ScoreHeatSummary / VectorSummary と同じ時刻の作り方
            float time = frame.normalizedTimestampTicks * 1e-7f;

            LightFrameRecord record = new LightFrameRecord
            {
                frameIndex = summary.frames.Count,
                unityTime = time,
                lightState = NoDataLabel
            };

            // time に最も近いライト記録まで cursor を進める
            while (cursor + 1 < lightFrames.Count &&
                   Mathf.Abs(lightFrames[cursor + 1].unityTime - time) <=
                   Mathf.Abs(lightFrames[cursor].unityTime - time))
            {
                cursor++;
            }

            LightFrame closest = lightFrames[cursor];

            float difference =
                Mathf.Abs(closest.unityTime - time);

            if (difference <= timeTolerance)
            {
                record.matched = true;
                record.sourceTime = closest.unityTime;
                record.timeDifference = difference;
                record.light1 = closest.light1;
                record.light2 = closest.light2;
                record.light3 = closest.light3;
                record.lightState = BuildState(closest, summary);

                if (difference > summary.maxTimeDifference)
                    summary.maxTimeDifference = difference;
            }
            else
            {
                summary.noDataFrames++;
            }

            summary.frames.Add(record);
        }

        summary.frameCount = summary.frames.Count;
        BuildEpisodes(summary);

        return summary;
    }

    // =========================================================
    // 点灯状態のラベルを作る。
    // 全消灯なら "None"、単独点灯なら "Light1"、複数なら "Light1+Light3"。
    // =========================================================
    private string BuildState(LightFrame frame, LightSummaryData summary)
    {
        bool[] states = { frame.light1, frame.light2, frame.light3 };

        string state = null;
        int onCount = 0;

        for (int i = 0; i < LightNames.Length; i++)
        {
            if (!states[i])
                continue;

            summary.onFrames[i]++;
            onCount++;

            state = state == null
                ? LightNames[i]
                : state + "+" + LightNames[i];
        }

        if (onCount == 0)
        {
            summary.allOffFrames++;
            return AllOffLabel;
        }

        if (onCount == 1)
            summary.singleOnFrames++;
        else
            summary.multiOnFrames++;

        return state;
    }

    // =========================================================
    // 点灯区間(消灯 -> 点灯 -> 消灯)をまとめる。
    // フレーム番号で持つので、視線 / 接近の JSON とそのまま突き合わせられる。
    // =========================================================
    private void BuildEpisodes(LightSummaryData summary)
    {
        LightEpisode current = null;

        foreach (LightFrameRecord record in summary.frames)
        {
            bool isOn =
                record.matched &&
                record.lightState != AllOffLabel;

            if (isOn && current != null && current.lightState != record.lightState)
            {
                CloseEpisode(summary, current, record.frameIndex);
                current = null;
            }

            if (isOn && current == null)
            {
                current = new LightEpisode
                {
                    lightState = record.lightState,
                    startFrame = record.frameIndex,
                    startTime = record.unityTime
                };
            }
            else if (!isOn && current != null)
            {
                CloseEpisode(summary, current, record.frameIndex);
                current = null;
            }
        }

        if (current != null)
        {
            LightFrameRecord last =
                summary.frames[summary.frames.Count - 1];

            current.endFrame = last.frameIndex;
            current.endTime = last.unityTime;
            current.duration = current.endTime - current.startTime;
            summary.episodes.Add(current);
        }
    }

    // terminatorIndex は点灯が途切れた最初のフレーム。
    // その 1 つ前(= 最後に点いていたフレーム)を区間の終端として記録する。
    private void CloseEpisode(
        LightSummaryData summary,
        LightEpisode episode,
        int terminatorIndex
    )
    {
        LightFrameRecord last =
            summary.frames[terminatorIndex - 1];

        episode.endFrame = last.frameIndex;
        episode.endTime = last.unityTime;
        episode.duration = episode.endTime - episode.startTime;
        summary.episodes.Add(episode);
    }

    // =========================================================
    // 読み込み
    // =========================================================
    private bool LoadSkeleton()
    {
        frames.Clear();

        string path =
            Path.Combine(
                Application.dataPath,
                InputRoot,
                (inputFolder ?? "").Trim().Trim('/', '\\'),
                skeletonJson
            );

        if (!File.Exists(path))
        {
            Debug.LogError($"Skeleton JSON が見つかりません: {path}", this);
            return false;
        }

        FrameList list =
            JsonUtility.FromJson<FrameList>(File.ReadAllText(path));

        if (list == null || list.frames == null || list.frames.Count == 0)
        {
            Debug.LogError($"Skeleton JSON を読み込めませんでした: {path}", this);
            return false;
        }

        frames.AddRange(list.frames);
        return true;
    }

    private bool LoadLight()
    {
        lightFrames.Clear();

        string path =
            Path.Combine(
                Application.dataPath,
                InputRoot,
                (inputFolder ?? "").Trim().Trim('/', '\\'),
                lightJson
            );

        if (!File.Exists(path))
        {
            Debug.LogError($"Light JSON が見つかりません: {path}", this);
            return false;
        }

        LightFrameList list =
            JsonUtility.FromJson<LightFrameList>(File.ReadAllText(path));

        if (list == null || list.frames == null || list.frames.Count == 0)
        {
            Debug.LogError($"Light JSON を読み込めませんでした: {path}", this);
            return false;
        }

        lightFrames.AddRange(list.frames);
        return true;
    }

    // =========================================================
    // 書き出し
    // =========================================================
    private void WriteJson(LightSummaryData summary)
    {
        string fileName = outputFileName;

        if (string.IsNullOrWhiteSpace(fileName))
            fileName = "lightframe.json";

        fileName = fileName.Trim();

        if (!fileName.EndsWith(".json", System.StringComparison.OrdinalIgnoreCase))
            fileName += ".json";

        string subFolder = (outputFolder ?? "").Trim().Trim('/', '\\');

        if (string.IsNullOrEmpty(subFolder))
        {
            Debug.LogError("Output Folder が空です。出力フォルダ名を指定してください。", this);
            return;
        }

        string folder =
            Path.Combine(Application.dataPath, OutputRoot, subFolder);

        if (!Directory.Exists(folder))
            Directory.CreateDirectory(folder);

        string path = Path.Combine(folder, fileName);

        File.WriteAllText(
            path,
            JsonUtility.ToJson(summary, prettyPrint)
        );

#if UNITY_EDITOR
        UnityEditor.AssetDatabase.Refresh();
#endif

        Debug.Log(
            $"保存しました: {path}\n" +
            $"  frames={summary.frameCount} " +
            $"allOff={summary.allOffFrames} " +
            $"singleOn={summary.singleOnFrames} " +
            $"multiOn={summary.multiOnFrames} " +
            $"noData={summary.noDataFrames}\n" +
            $"  episodes={summary.episodes.Count} " +
            $"maxTimeDiff={summary.maxTimeDifference:F4}s",
            this
        );
    }

    // =========================================================
    // 出力 JSON の構造
    // =========================================================
    [System.Serializable]
    private class LightSummaryData
    {
        public string skeletonJson;
        public string lightJson;
        public List<string> lightNames = new();
        public float timeTolerance;
        public string allOffLabel;
        public string noDataLabel;

        // 元のライトログの行数(整形前)。
        public int lightSourceFrameCount;

        public int frameCount;
        public int allOffFrames;
        public int singleOnFrames;
        public int multiOnFrames;
        public int noDataFrames;

        // 最近傍で引いたときの時刻差の最大値(秒)。timeTolerance の妥当性確認用。
        public float maxTimeDifference;

        // lightNames と同じ並び。そのライトが点いていたフレーム数。
        public List<int> onFrames = new();

        public List<LightEpisode> episodes = new();
        public List<LightFrameRecord> frames = new();
    }

    [System.Serializable]
    private class LightFrameRecord
    {
        public int frameIndex;
        public float unityTime;

        // timeTolerance 以内にライト記録が見つかったか。false のとき下の値は無効。
        public bool matched;

        // 参照したライト記録の時刻と、フレーム時刻との差(秒)。
        public float sourceTime;
        public float timeDifference;

        public bool light1;
        public bool light2;
        public bool light3;

        // "None" / "Light1" / "Light1+Light3" / "NoData"
        public string lightState;
    }

    [System.Serializable]
    private class LightEpisode
    {
        // "Light1" や "Light1+Light3"。この区間で点いていた組み合わせ。
        public string lightState;

        public int startFrame;
        public int endFrame;
        public float startTime;
        public float endTime;
        public float duration;
    }

    // =========================================================
    // 入力 JSON の構造
    // =========================================================
    [System.Serializable]
    private class FrameList
    {
        public List<FrameData> frames;
    }

    [System.Serializable]
    private class FrameData
    {
        public long normalizedTimestampTicks;
    }

    [System.Serializable]
    private class LightFrameList
    {
        public List<LightFrame> frames;
    }

    [System.Serializable]
    private class LightFrame
    {
        public float unityTime;
        public bool light1;
        public bool light2;
        public bool light3;
    }
}
