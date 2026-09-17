using Microsoft.Azure.Kinect.BodyTracking;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;

// フレームごとに「各対象へ近づいたか遠ざかったか」を時系列 JSON として書き出すエクスポータ。
//
// PelvisVectorArrow(AnalyzeVector シーン)が矢印を描きながら評価していた
// 「Pelvis の移動ベクトルと対象方向の内積」を、集計値ではなくフレーム単位で出す。
// ScoreHeatSummary と同じく、再生は一切行わずボタンを押した時点で全フレームを一括処理する。
//
// 速度と移動量の求め方:
//   骨格 JSON は 30fps で記録されているが、Kinect の骨格推定は約 15Hz でしか
//   更新されておらず、全フレームの約半分は前フレームと座標が完全に同一である。
//   そのため隣接フレーム差分では「0 -> 実際の2倍 -> 0」と交互になり、
//   子どもの移動としてあり得ない速度が出る。
//   ここでは velocitySpan 秒の区間の両端をとって移動量とし、経過時間で割って速度とする。
//   平均も重み付けもしない、区間の端点だけを使った素の差分。
//
// あり得ない速度の除外:
//   子どもの移動速度として maxSpeed を超えた値が出たフレームは計測エラーとみなして
//   無効にする。統計的な基準ではなく物理的な基準なので、他の計測データにも
//   そのまま適用できる。
//
// PelvisVectorArrow との違い:
//   - ライトフィルタは掛けず、全フレームを出力する(絞り込みは解析側で行う)。
//   - 骨格が欠けたフレームもスキップせずレコードを出すので、frameIndex が
//     骨格 JSON および ScoreHeatSummary の出力と 1 対 1 で対応する。
//
// 入力元: Assets/Data/Analyze/input/<inputFolder>/
// 出力先: Assets/Data/Analyze/output/<outputFolder>/<outputFileName>
public class VectorSummary : MonoBehaviour
{
    [Header("Input")]
    [Tooltip("入力フォルダ名。Assets/Data/Analyze/input/<inputFolder>/ から読む。")]
    [SerializeField] private string inputFolder = "sou1";

    [Tooltip("input フォルダ内の骨格 JSON ファイル名。")]
    [SerializeField] private string skeletonJson = "skeleton.json";

    [Tooltip("接近を評価する対象。PelvisVectorArrow の Targets と同じものを指定する。")]
    [SerializeField] private Transform[] targets;

    [Header("Velocity")]
    [Tooltip(
        "移動量と速度を求める区間の長さ(秒)。この区間の移動量を経過時間で割って速度とする。" +
        "骨格の実効更新レートが約 15Hz なので、0.1 秒(3フレーム)以上にすること。"
    )]
    [SerializeField] private float velocitySpan = 0.2f;

    [Tooltip(
        "子どもの移動速度の上限(m/s)。これを超えたフレームは計測エラーとみなして" +
        "無効にする。0 以下で無効化。"
    )]
    [SerializeField] private float maxSpeed = 2.0f;

    [Tooltip("Y 軸を無視して水平面(XZ)だけで距離と速度を計算する。")]
    [SerializeField] private bool horizontalOnly = true;

    [Header("Approach")]
    [Tooltip("moveTarget を立てる接近速度の閾値(m/s)。データ自体は閾値なしで出力される。")]
    [SerializeField] private float approachSpeedThreshold = 0.15f;

    [Header("Output")]
    [Tooltip("出力フォルダ名。Assets/Data/Analyze/output/<outputFolder>/ に保存される。")]
    [FormerlySerializedAs("dataFolder")]
    [SerializeField] private string outputFolder = "sou1";

    [Tooltip("保存するファイル名。拡張子は省略可。")]
    [SerializeField] private string outputFileName = "move.json";

    [Tooltip("整形して出力する(ファイルサイズは大きくなる)。")]
    [SerializeField] private bool prettyPrint = true;

    [Header("UI")]
    [Tooltip("押すとエクスポートを実行するボタン。OnClick に手動で登録済みなら空でよい。")]
    [SerializeField] private Button exportButton;

    private const string InputRoot = "Data/Analyze/input";
    private const string OutputRoot = "Data/Analyze/output";

    // moveTarget に書き込む特殊値。
    // NoMoveLabel  : 速度は求まったが、閾値を超えて近づいた対象が無いフレーム
    // NoDataLabel  : Pelvis が取れず速度を計算できなかったフレーム
    // OverSpeedLabel: 子どもの移動としてあり得ない速度が出たフレーム(計測エラー)
    private const string NoMoveLabel = "None";
    private const string NoDataLabel = "NoData";
    private const string OverSpeedLabel = "OverSpeed";

    private readonly List<FrameData> frames = new();
    private readonly List<Transform> validTargets = new();

    // フレームごとの中間データ
    private readonly List<float> times = new();
    private readonly List<Vector3> rawPelvis = new();
    private readonly List<bool> hasPelvis = new();

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
    [ContextMenu("Export Vector Summary")]
    public void Export()
    {
        if (!LoadSkeleton())
            return;

        if (!RegisterTargets())
            return;

        BuildPelvisSeries();

        WriteJson(Analyze());
    }

    // =========================================================
    // Pelvis の生系列を作る(フレーム数と 1 対 1)
    // =========================================================
    private void BuildPelvisSeries()
    {
        times.Clear();
        rawPelvis.Clear();
        hasPelvis.Clear();

        foreach (FrameData frame in frames)
        {
            // ScoreHeatSummary と同じ時刻の作り方
            times.Add(frame.normalizedTimestampTicks * 1e-7f);

            Vector3 pelvis = Vector3.zero;
            bool found = false;

            if (frame.joints != null)
            {
                foreach (JointPosition joint in frame.joints)
                {
                    if (joint.jointId == JointId.Pelvis.ToString())
                    {
                        pelvis = joint.position;
                        found = true;
                        break;
                    }
                }
            }

            rawPelvis.Add(pelvis);
            hasPelvis.Add(found);
        }
    }

    // =========================================================
    // 全フレームを走査して接近／後退を求める
    // =========================================================
    private MoveSummary Analyze()
    {
        MoveSummary summary = new MoveSummary
        {
            skeletonJson = skeletonJson,
            velocitySpan = velocitySpan,
            maxSpeed = maxSpeed,
            horizontalOnly = horizontalOnly,
            approachSpeedThreshold = approachSpeedThreshold,
            noMoveLabel = NoMoveLabel,
            noDataLabel = NoDataLabel,
            overSpeedLabel = OverSpeedLabel
        };

        foreach (Transform target in validTargets)
        {
            summary.targetNames.Add(target.name);
            summary.approachFrames.Add(0);
            summary.recedeFrames.Add(0);
        }

        int count = frames.Count;
        int targetCount = validTargets.Count;

        for (int i = 0; i < count; i++)
        {
            MoveFrameRecord record = new MoveFrameRecord
            {
                frameIndex = i,
                unityTime = times[i],
                hasPelvis = hasPelvis[i],
                valid = false,
                moveTarget = NoDataLabel,
                speed = 0f,
                moveMagnitude = 0f
            };

            for (int t = 0; t < targetCount; t++)
            {
                record.distances.Add(-1f);
                record.dots.Add(0f);
                record.cosines.Add(0f);
                record.closingSpeeds.Add(0f);
            }

            if (!record.hasPelvis)
            {
                summary.noDataFrames++;
                summary.frames.Add(record);
                continue;
            }

            Vector3 position = Flatten(rawPelvis[i]);
            record.pelvis = position;

            // 距離は移動量と無関係なので、Pelvis が取れていれば常に埋める
            for (int t = 0; t < targetCount; t++)
            {
                record.distances[t] =
                    (Flatten(validTargets[t].position) - position).magnitude;
            }

            if (!TryDisplacement(i, out Vector3 displacement, out float deltaTime))
            {
                summary.noDataFrames++;
                summary.frames.Add(record);
                continue;
            }

            Vector3 velocity = displacement / deltaTime;
            float speed = velocity.magnitude;

            // 子どもの移動としてあり得ない速度は計測エラーとみなす
            if (maxSpeed > 0f && speed > maxSpeed)
            {
                record.moveTarget = OverSpeedLabel;
                record.speed = speed;
                summary.overSpeedFrames++;
                summary.frames.Add(record);
                continue;
            }

            float moveMagnitude = displacement.magnitude;

            record.valid = true;
            record.speed = speed;
            record.moveMagnitude = moveMagnitude;
            summary.validFrames++;

            float bestClosing = float.MinValue;
            int bestIndex = -1;

            for (int t = 0; t < targetCount; t++)
            {
                Vector3 targetVector =
                    Flatten(validTargets[t].position) - position;

                float targetMagnitude = targetVector.magnitude;

                if (targetMagnitude <= 0.0001f)
                    continue;

                // PelvisVectorArrow と同じ形の内積。
                // 移動ベクトルが隣接フレーム差分ではなく velocitySpan 区間の変位である点だけが違う。
                float dot =
                    Vector3.Dot(displacement, targetVector);

                record.dots[t] = dot;

                record.cosines[t] =
                    moveMagnitude > 0.0001f
                        ? Mathf.Clamp(
                            dot / (moveMagnitude * targetMagnitude),
                            -1f,
                            1f
                        )
                        : 0f;

                // 対象方向への速度成分。正 = 近づいている、負 = 遠ざかっている。
                float closing =
                    Vector3.Dot(velocity, targetVector / targetMagnitude);

                record.closingSpeeds[t] = closing;

                if (closing >= approachSpeedThreshold)
                    summary.approachFrames[t]++;
                else if (closing <= -approachSpeedThreshold)
                    summary.recedeFrames[t]++;

                if (closing > bestClosing)
                {
                    bestClosing = closing;
                    bestIndex = t;
                }
            }

            record.moveTarget =
                bestIndex >= 0 && bestClosing >= approachSpeedThreshold
                    ? validTargets[bestIndex].name
                    : NoMoveLabel;

            if (record.moveTarget == NoMoveLabel)
                summary.noMoveFrames++;

            summary.frames.Add(record);
        }

        summary.frameCount = summary.frames.Count;
        return summary;
    }

    // =========================================================
    // velocitySpan 秒の区間の両端をとって移動量を求める。
    //
    //   displacement = pos(b) - pos(a)
    //   deltaTime    = t(b) - t(a)
    //     a = index から velocitySpan/2 秒前までで、Pelvis が取れている最も古いフレーム
    //     b = index から velocitySpan/2 秒後までで、Pelvis が取れている最も新しいフレーム
    //
    // 平均も重み付けもしない、区間の端点だけを使った素の差分。
    // 区間を骨格の更新間隔より長く取ることで、
    // 「前フレームと同じ座標」による 0 と急増の交互出現を避ける。
    // =========================================================
    private bool TryDisplacement(
        int index,
        out Vector3 displacement,
        out float deltaTime
    )
    {
        displacement = Vector3.zero;
        deltaTime = 0f;

        float half = Mathf.Max(velocitySpan, 0f) * 0.5f;
        int count = times.Count;

        int a = index;

        for (int f = index; f >= 0; f--)
        {
            if (times[index] - times[f] > half)
                break;

            if (hasPelvis[f])
                a = f;
        }

        int b = index;

        for (int f = index; f < count; f++)
        {
            if (times[f] - times[index] > half)
                break;

            if (hasPelvis[f])
                b = f;
        }

        deltaTime = times[b] - times[a];

        if (deltaTime <= 0f)
            return false;

        displacement =
            Flatten(rawPelvis[b]) - Flatten(rawPelvis[a]);

        return true;
    }

    private Vector3 Flatten(Vector3 value)
    {
        if (horizontalOnly)
            value.y = 0f;

        return value;
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

    private bool RegisterTargets()
    {
        validTargets.Clear();

        if (targets == null || targets.Length == 0)
        {
            Debug.LogError("Targets が空です。", this);
            return false;
        }

        foreach (Transform target in targets)
        {
            if (target != null)
                validTargets.Add(target);
        }

        if (validTargets.Count == 0)
        {
            Debug.LogError("有効な Target がありません。", this);
            return false;
        }

        return true;
    }

    // =========================================================
    // 書き出し
    // =========================================================
    private void WriteJson(MoveSummary summary)
    {
        string fileName = outputFileName;

        if (string.IsNullOrWhiteSpace(fileName))
            fileName = "move.json";

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
            $"valid={summary.validFrames} " +
            $"noMove={summary.noMoveFrames} " +
            $"overSpeed={summary.overSpeedFrames} " +
            $"noData={summary.noDataFrames}",
            this
        );
    }

    // =========================================================
    // 出力 JSON の構造
    // =========================================================
    [System.Serializable]
    private class MoveSummary
    {
        public string skeletonJson;
        public List<string> targetNames = new();
        public float velocitySpan;
        public float maxSpeed;
        public bool horizontalOnly;
        public float approachSpeedThreshold;
        public string noMoveLabel;
        public string noDataLabel;
        public string overSpeedLabel;
        public int frameCount;
        public int validFrames;
        public int noMoveFrames;
        public int noDataFrames;

        // maxSpeed を超えて無効にしたフレーム数
        public int overSpeedFrames;

        // targetNames と同じ並び。closingSpeed が閾値を超えた／下回ったフレーム数。
        public List<int> approachFrames = new();
        public List<int> recedeFrames = new();

        public List<MoveFrameRecord> frames = new();
    }

    [System.Serializable]
    private class MoveFrameRecord
    {
        public int frameIndex;
        public float unityTime;

        // Pelvis が取れたフレームか。false のとき pelvis と distances も無効。
        public bool hasPelvis;

        // 速度が使えるフレームか。
        // hasPelvis かつ maxSpeed 以内のときだけ true。
        // false のとき speed / dots / cosines / closingSpeeds は無効(distances は有効)。
        public bool valid;

        // Pelvis 位置(horizontalOnly のとき y = 0)。平滑化していない生の値。
        public Vector3 pelvis;

        // 水平移動速度の大きさ(m/s)。
        public float speed;

        // velocitySpan 区間の移動量(m)。内積の分母に使われる値。
        public float moveMagnitude;

        // targetNames と同じ並び。Pelvis から対象中心までの距離(m)。
        // hasPelvis が false のときだけ -1。
        public List<float> distances = new();

        // targetNames と同じ並び。Vector3.Dot(displacement, targetVector)。
        // PelvisVectorArrow の Dot Sum と同じ形(単位は m^2)。
        // 移動ベクトルが velocitySpan 区間の変位である点だけが違う。
        public List<float> dots = new();

        // targetNames と同じ並び。移動方向と対象方向のなす角の cos(-1..1)。
        // PelvisVectorArrow の Cos Average と同じ量。
        public List<float> cosines = new();

        // targetNames と同じ並び。対象方向への速度成分(m/s)。
        // 正 = 近づいている、負 = 遠ざかっている。
        public List<float> closingSpeeds = new();

        // closingSpeed が最大かつ閾値以上の対象名。
        // 無ければ noMoveLabel / noDataLabel / overSpeedLabel。
        public string moveTarget;
    }

    // =========================================================
    // 入力 JSON の構造(ScoreHeatSummary と同じ)
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
        public List<JointPosition> joints;
    }

    [System.Serializable]
    private class JointPosition
    {
        public string jointId;
        public Vector3 position;
    }
}
