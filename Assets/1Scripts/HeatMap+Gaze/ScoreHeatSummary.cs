using Microsoft.Azure.Kinect.BodyTracking;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;

// フレームごとに「どの対象を見ていたか」を時系列 JSON として書き出すエクスポータ。
//
// ScoreHeat とは完全に独立して動作する。再生は一切行わず、ボタンを押した時点で
// 骨格 JSON の全フレームを一括処理して保存するだけ。
//
// コーン判定の中身は ScoreHeat と同一:
//   head -> nose ベクトルを Quaternion.AngleAxis(downwardAngle, cross(up, rawDir)) で補正し、
//   対象メッシュの頂点が coneAngle / coneDistance の内側に入っていればヒットとみなす。
// パラメータは下のインスペクター項目で個別に設定する。ScoreHeat 側と同じ値にすること。
// (出力 JSON のヘッダに実際に使った値が記録されるので、後から確認できる)
//
// 入力元: Assets/Data/Analyze/input/<inputFolder>/
// 出力先: Assets/Data/Analyze/output/<outputFolder>/<outputFileName>
public class ScoreHeatSummary : MonoBehaviour
{
    [Header("Input")]
    [Tooltip("入力フォルダ名。Assets/Data/Analyze/input/<inputFolder>/ から読む。")]
    [SerializeField] private string inputFolder = "sou1";

    [Tooltip("input フォルダ内の骨格 JSON ファイル名。")]
    [SerializeField] private string skeletonJson = "skeleton.json";

    [Tooltip("視線の対象。MeshFilter を持つオブジェクトを指定する。")]
    [SerializeField] private GameObject[] targets;

    [Header("Gaze Angle Correction")]
    [Tooltip("Downward angle correction in degrees. ScoreHeat と同じ値にすること。")]
    [SerializeField] private float downwardAngle = 24.4f;

    [Header("Cone Settings")]
    [Tooltip("Cone angle in degrees. ScoreHeat と同じ値にすること。")]
    [SerializeField] private float coneAngle = 10f;

    [Tooltip("Maximum cone distance. ScoreHeat と同じ値にすること。")]
    [SerializeField] private float coneDistance = 5f;

    [Tooltip("Apply more heat near the center of the cone. ScoreHeat と同じ値にすること。")]
    [SerializeField] private bool useCenterWeightedHeat = false;

    [SerializeField] private float heatPerHit = 1f;

    [Header("Output")]
    [Tooltip("出力フォルダ名。Assets/Data/Analyze/output/<outputFolder>/ に保存される。")]
    [FormerlySerializedAs("dataFolder")]
    [SerializeField] private string outputFolder = "sou1";

    [Tooltip("保存するファイル名。拡張子は省略可。")]
    [SerializeField] private string outputFileName = "gaze.json";

    [Tooltip("整形して出力する(ファイルサイズは大きくなる)。")]
    [SerializeField] private bool prettyPrint = true;

    [Header("UI")]
    [Tooltip("押すとエクスポートを実行するボタン。OnClick に手動で登録済みなら空でよい。")]
    [SerializeField] private Button exportButton;

    private const string InputRoot = "Data/Analyze/input";
    private const string OutputRoot = "Data/Analyze/output";

    // gazeTarget に書き込む特殊値。
    // NoHitLabel : 視線は有効だがどの対象にも当たらなかったフレーム
    // NoDataLabel: Head/Nose が取れず視線方向を計算できなかったフレーム
    private const string NoHitLabel = "None";
    private const string NoDataLabel = "NoData";

    private readonly List<FrameData> frames = new();
    private readonly Dictionary<JointId, Vector3> joints = new();
    private readonly List<TargetCache> targetCaches = new();

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
    [ContextMenu("Export Gaze Summary")]
    public void Export()
    {
        if (!LoadSkeleton())
            return;

        if (!RegisterTargets())
            return;

        WriteJson(Analyze());
    }

    // =========================================================
    // 全フレームを走査してフレームごとのラベルを決める
    // =========================================================
    private GazeSummary Analyze()
    {
        GazeSummary summary = new GazeSummary
        {
            skeletonJson = skeletonJson,
            coneAngle = coneAngle,
            coneDistance = coneDistance,
            downwardAngle = downwardAngle,
            useCenterWeightedHeat = useCenterWeightedHeat,
            heatPerHit = heatPerHit,
            noHitLabel = NoHitLabel,
            noDataLabel = NoDataLabel
        };

        foreach (TargetCache cache in targetCaches)
        {
            summary.targetNames.Add(cache.Name);
            summary.hitFrames.Add(0);
            summary.totalHeat.Add(0f);
        }

        float maxAngleRad =
            Mathf.Max(coneAngle, 0.0001f) * Mathf.Deg2Rad;

        float cosThreshold =
            Mathf.Cos(maxAngleRad);

        int targetCount = targetCaches.Count;

        for (int i = 0; i < frames.Count; i++)
        {
            FrameData frame = frames[i];

            GazeFrameRecord record = new GazeFrameRecord
            {
                frameIndex = i,

                // ScoreHeat の UpdateFrame と同じ時刻の作り方
                unityTime = frame.normalizedTimestampTicks * 1e-7f,

                gazeTarget = NoDataLabel,
                gazeAngle = -1f
            };

            for (int t = 0; t < targetCount; t++)
            {
                record.heats.Add(0f);
                record.angles.Add(-1f);
            }

            if (TryGetGazeDirection(frame, out Vector3 head, out Vector3 direction))
            {
                record.gazeTarget = NoHitLabel;

                float bestAngle = float.MaxValue;
                int bestIndex = -1;

                for (int t = 0; t < targetCount; t++)
                {
                    if (!EvaluateTarget(
                            targetCaches[t],
                            head,
                            direction,
                            cosThreshold,
                            maxAngleRad,
                            out float heat,
                            out float minAngleDeg))
                    {
                        continue;
                    }

                    record.heats[t] = heat;
                    record.angles[t] = minAngleDeg;
                    record.hitTargets.Add(targetCaches[t].Name);

                    summary.hitFrames[t]++;
                    summary.totalHeat[t] += heat;

                    if (minAngleDeg < bestAngle)
                    {
                        bestAngle = minAngleDeg;
                        bestIndex = t;
                    }
                }

                if (bestIndex >= 0)
                {
                    record.gazeTarget = targetCaches[bestIndex].Name;
                    record.gazeAngle = bestAngle;
                }
                else
                {
                    summary.noTargetFrames++;
                }

                summary.gazeFrames++;
            }
            else
            {
                summary.noDataFrames++;
            }

            summary.frames.Add(record);
        }

        summary.frameCount = summary.frames.Count;
        return summary;
    }

    // =========================================================
    // ScoreHeat.ProcessConeGaze と同じ視線方向の作り方
    // =========================================================
    private bool TryGetGazeDirection(
        FrameData frame,
        out Vector3 head,
        out Vector3 direction
    )
    {
        head = Vector3.zero;
        direction = Vector3.zero;

        joints.Clear();

        if (frame.joints != null)
        {
            foreach (JointPosition joint in frame.joints)
            {
                if (System.Enum.TryParse(joint.jointId, out JointId id))
                    joints[id] = joint.position;
            }
        }

        if (!joints.TryGetValue(JointId.Head, out head) ||
            !joints.TryGetValue(JointId.Nose, out Vector3 nose))
        {
            return false;
        }

        Vector3 rawDir = nose - head;
        if (rawDir.sqrMagnitude < 0.0001f)
            return false;

        rawDir.Normalize();

        Vector3 rightAxis =
            Vector3.Cross(Vector3.up, rawDir).normalized;

        if (rightAxis.sqrMagnitude < 0.0001f)
            rightAxis = Vector3.right;

        Quaternion correction =
            Quaternion.AngleAxis(downwardAngle, rightAxis);

        direction = (correction * rawDir).normalized;
        return true;
    }

    // =========================================================
    // ScoreHeat.AddConeScores と同じコーン判定を 1 ターゲット分だけ行う
    // =========================================================
    private bool EvaluateTarget(
        TargetCache cache,
        Vector3 origin,
        Vector3 direction,
        float cosThreshold,
        float maxAngleRad,
        out float heat,
        out float minAngleDeg
    )
    {
        heat = 0f;
        minAngleDeg = -1f;

        if (cache.Target == null || cache.Vertices == null)
            return false;

        Transform targetTransform = cache.Target.transform;
        bool hit = false;
        float minAngleRad = float.MaxValue;

        foreach (Vector3 vertex in cache.Vertices)
        {
            Vector3 worldPosition =
                targetTransform.TransformPoint(vertex);

            Vector3 toVertex =
                worldPosition - origin;

            float distance =
                toVertex.magnitude;

            if (distance <= 0f || distance > coneDistance)
                continue;

            float dot =
                Vector3.Dot(direction, toVertex / distance);

            if (dot < cosThreshold)
                continue;

            float angle =
                Mathf.Acos(Mathf.Clamp(dot, -1f, 1f));

            float weight = 1f;

            if (useCenterWeightedHeat)
            {
                float normalized = angle / maxAngleRad;
                weight = 1f - normalized;
                weight *= weight;
            }

            heat += heatPerHit * weight;

            if (angle < minAngleRad)
                minAngleRad = angle;

            hit = true;
        }

        if (!hit)
            return false;

        minAngleDeg = minAngleRad * Mathf.Rad2Deg;
        return true;
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
            Report($"Skeleton JSON が見つかりません: {path}", true);
            return false;
        }

        FrameList list =
            JsonUtility.FromJson<FrameList>(File.ReadAllText(path));

        if (list == null || list.frames == null || list.frames.Count == 0)
        {
            Report($"Skeleton JSON を読み込めませんでした: {path}", true);
            return false;
        }

        frames.AddRange(list.frames);
        return true;
    }

    private bool RegisterTargets()
    {
        targetCaches.Clear();

        if (targets == null || targets.Length == 0)
        {
            Report("Targets が空です。", true);
            return false;
        }

        foreach (GameObject target in targets)
        {
            if (target == null)
                continue;

            MeshFilter meshFilter = target.GetComponent<MeshFilter>();

            if (meshFilter == null || meshFilter.sharedMesh == null)
            {
                Debug.LogWarning(
                    $"Target has no MeshFilter: {target.name}",
                    target
                );
                continue;
            }

            targetCaches.Add(
                new TargetCache(
                    target,
                    meshFilter.sharedMesh.vertices
                )
            );
        }

        if (targetCaches.Count == 0)
        {
            Report("有効な Target がありません。", true);
            return false;
        }

        return true;
    }

    // =========================================================
    // 書き出し
    // =========================================================
    private void WriteJson(GazeSummary summary)
    {
        string fileName = outputFileName;

        if (string.IsNullOrWhiteSpace(fileName))
            fileName = "gaze.json";

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

        Report(
            $"保存しました: {path}\n" +
            $"  frames={summary.frameCount} " +
            $"gaze={summary.gazeFrames} " +
            $"noTarget={summary.noTargetFrames} " +
            $"noData={summary.noDataFrames}",
            false
        );
    }

    private void Report(string message, bool isError)
    {
        if (isError)
            Debug.LogError(message, this);
        else
            Debug.Log(message, this);
    }

    // =========================================================
    // 出力 JSON の構造
    // =========================================================
    [System.Serializable]
    private class GazeSummary
    {
        public string skeletonJson;
        public List<string> targetNames = new();
        public float coneAngle;
        public float coneDistance;
        public float downwardAngle;
        public bool useCenterWeightedHeat;
        public float heatPerHit;
        public string noHitLabel;
        public string noDataLabel;
        public int frameCount;
        public int gazeFrames;
        public int noTargetFrames;
        public int noDataFrames;
        public List<int> hitFrames = new();
        public List<float> totalHeat = new();
        public List<GazeFrameRecord> frames = new();
    }

    [System.Serializable]
    private class GazeFrameRecord
    {
        public int frameIndex;
        public float unityTime;

        // どの対象を見ていたか。対象名 / noHitLabel / noDataLabel のいずれか。
        public string gazeTarget;

        // 複数の対象にコーンが当たったフレームでは全部入る。
        public List<string> hitTargets = new();

        // targetNames と同じ並び。当たっていない対象は 0。
        public List<float> heats = new();

        // targetNames と同じ並び。コーン軸との最小角度(度)。未ヒットは -1。
        public List<float> angles = new();

        // gazeTarget に対する最小角度(度)。未ヒットは -1。
        public float gazeAngle;
    }

    // =========================================================
    // 入力 JSON の構造(ScoreHeat と同じ)
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

    private class TargetCache
    {
        public GameObject Target { get; }
        public Vector3[] Vertices { get; }
        public string Name { get; }

        public TargetCache(GameObject target, Vector3[] vertices)
        {
            Target = target;
            Vertices = vertices;
            Name = target.name;
        }
    }
}
