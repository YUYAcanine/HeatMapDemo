using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Azure.Kinect.BodyTracking;
using UnityEngine;

// MultiIntegrationViewerシーン専用。Logger.csが書き出したNDJSON(.jsonl)ログを読み込み、
// スペースキーで再生の開始/停止をトグルする。各行の"data"を、ライブMQTT受信時と
// 同じ適用メソッド(NavSub.ApplyNavMeshJson / SkeletonRealtimeSubscriber.ApplyMessage /
// Subscriber.ApplyMessage)にそのまま渡すことで、記録時と同じ見た目を再現する。
// "skeleton_full"(32関節)についてはJointPositionLoader.csと同じ「関節=球、
// ボーン=LineRenderer」の可視化ロジックをこのクラス自身に持たせている(複数人物対応)。
public class SessionPlayer : MonoBehaviour
{
    [Header("Sources (空の場合はシーン内から自動収集)")]
    [SerializeField] private NavSub navSub;
    [SerializeField] private SkeletonRealtimeSubscriber skeletonSubscriber;
    [SerializeField] private Subscriber objectSubscriber;

    [Header("Log File")]
    [Tooltip("特定の.jsonlファイルへのフルパス、またはディレクトリ(その中の最新の.jsonlを使う)。空の場合は Assets/Data/Logs を使う。")]
    [SerializeField] private string logFilePath = "Assets/Data/Logs";

    [Header("Playback")]
    [SerializeField] private KeyCode toggleKey = KeyCode.Space;

    [Header("Skeleton_FULL Visual (JointPositionLoader相当)")]
    [Tooltip("空の場合は球プリミティブを自動生成する。")]
    [SerializeField] private GameObject jointPrefab;
    [Tooltip("空の場合はSprites/Defaultシェーダーのマテリアルを自動生成する。")]
    [SerializeField] private Material lineMaterial;
    [SerializeField] private float jointScale = 0.06f;
    [SerializeField] private float lineWidth = 0.02f;
    [Tooltip("この秒数以上メッセージが来なければそのラベルの骨格を消す。0以下で無効。")]
    [SerializeField] private float skeletonLifetime = 2f;

    private readonly List<LogEntry> entries = new List<LogEntry>();
    private int nextEntryIndex;
    private float playbackStartTime;

    private readonly Dictionary<string, PersonSkeleton> skeletonsByLabel = new Dictionary<string, PersonSkeleton>();
    private readonly Dictionary<JointId, Vector3> scratchPositions = new Dictionary<JointId, Vector3>();

    private static readonly JointId[] SpineChain =
    {
        JointId.Pelvis, JointId.SpineNavel, JointId.SpineChest,
        JointId.Neck, JointId.Head, JointId.Nose
    };

    private static readonly JointId[] LegChain =
    {
        JointId.FootRight, JointId.AnkleRight, JointId.KneeRight,
        JointId.HipRight, JointId.Pelvis,
        JointId.HipLeft, JointId.KneeLeft,
        JointId.AnkleLeft, JointId.FootLeft
    };

    private static readonly JointId[] ArmChain =
    {
        JointId.HandTipLeft, JointId.HandLeft, JointId.WristLeft,
        JointId.ElbowLeft, JointId.ShoulderLeft, JointId.ClavicleLeft,
        JointId.SpineChest,
        JointId.ClavicleRight, JointId.ShoulderRight, JointId.ElbowRight,
        JointId.WristRight, JointId.HandRight, JointId.HandTipRight
    };

    public bool IsPlaying { get; private set; }

    private void Start()
    {
        if (navSub == null)
            navSub = FindObjectOfType<NavSub>();

        if (skeletonSubscriber == null)
            skeletonSubscriber = FindObjectOfType<SkeletonRealtimeSubscriber>();

        if (objectSubscriber == null)
            objectSubscriber = FindObjectOfType<Subscriber>();
    }

    private void Update()
    {
        if (Input.GetKeyDown(toggleKey))
        {
            if (IsPlaying)
                Stop();
            else
                Play();
        }

        RemoveStaleSkeletons();

        if (!IsPlaying)
            return;

        float elapsed = Time.unscaledTime - playbackStartTime;

        DispatchDueEntries(elapsed);

        if (nextEntryIndex >= entries.Count)
        {
            IsPlaying = false;
            Debug.Log("SessionPlayer: playback reached the end.");
        }
    }

    public void Play()
    {
        if (!LoadEntriesIfNeeded())
            return;

        nextEntryIndex = 0;
        playbackStartTime = Time.unscaledTime;
        IsPlaying = true;

        Debug.Log($"SessionPlayer: playback started. entries={entries.Count}");
    }

    public void Stop()
    {
        IsPlaying = false;
        Debug.Log("SessionPlayer: playback stopped.");
    }

    private bool LoadEntriesIfNeeded()
    {
        if (entries.Count > 0)
            return true;

        string path = ResolveLogFilePath();

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            Debug.LogWarning($"SessionPlayer: log file not found. path={path}");
            return false;
        }

        foreach (string line in File.ReadLines(path))
        {
            if (TryParseLine(line, out LogEntry entry))
                entries.Add(entry);
        }

        Debug.Log($"SessionPlayer: loaded {entries.Count} entries from {path}");
        return entries.Count > 0;
    }

    // logFilePathは「特定の.jsonlファイルへのフルパス」「ディレクトリ(中の最新.jsonlを使う)」
    // 「相対パス(Assets/Data/Logsなど、プロジェクトルート基準)」のいずれでも指定できる。
    // 空の場合はAssets/Data/Logsをデフォルトのディレクトリとして扱う。
    private string ResolveLogFilePath()
    {
        string path = string.IsNullOrWhiteSpace(logFilePath)
            ? Path.Combine(Application.dataPath, "Data", "Logs")
            : logFilePath;

        if (File.Exists(path))
            return path;

        if (Directory.Exists(path))
            return FindLatestJsonl(path);

        // 相対パス("Assets/Data/Logs"など)はプロジェクトルート基準として解釈し直す。
        string absolute = Path.Combine(Path.GetDirectoryName(Application.dataPath), path);

        if (File.Exists(absolute))
            return absolute;

        if (Directory.Exists(absolute))
            return FindLatestJsonl(absolute);

        return null;
    }

    private static string FindLatestJsonl(string directory)
    {
        string[] files = Directory.GetFiles(directory, "*.jsonl");

        if (files.Length == 0)
            return null;

        Array.Sort(files, (a, b) => File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));
        return files[0];
    }

    // 追いつき再生で複数行が同一フレームで期限到来した場合にまとめて処理する。
    // navmeshだけは特別扱いし、同一フレーム内に複数溜まっていたら最新の1件だけ適用する。
    // NavSub.RebuildLinks()はDestroy()で古いNavMeshLinkを破棄してから新規生成するが、
    // Destroy()はフレーム終端まで実際の破棄が遅延されるため、同一フレーム内で
    // navmesh適用を連続実行すると「NavMeshLinkが破棄済みなのにアクセスしようとした」
    // というエラーになる。ライブMQTT受信時はメッセージが1通ずつ間隔を空けて届くため
    // 起きないが、ログ再生は記録時の間隔をそのまま再現するので、記録時に短時間で
    // 連続していたnavmeshがまとめて1フレームに来ると発生し得る。
    private void DispatchDueEntries(float elapsed)
    {
        int dueEnd = nextEntryIndex;
        while (dueEnd < entries.Count && entries[dueEnd].t <= elapsed)
            dueEnd++;

        int lastNavMeshIndex = -1;
        for (int i = nextEntryIndex; i < dueEnd; i++)
        {
            if (entries[i].type == "navmesh")
                lastNavMeshIndex = i;
        }

        for (int i = nextEntryIndex; i < dueEnd; i++)
        {
            if (entries[i].type == "navmesh" && i != lastNavMeshIndex)
                continue;

            Dispatch(entries[i]);
        }

        nextEntryIndex = dueEnd;
    }

    private void Dispatch(LogEntry entry)
    {
        switch (entry.type)
        {
            case "navmesh":
                navSub?.ApplyNavMeshJson(entry.data);
                break;
            case "skeleton":
                skeletonSubscriber?.ApplyMessage(entry.data);
                break;
            case "skeleton_full":
                ApplySkeletonFullMessage(entry.data);
                break;
            case "object":
                objectSubscriber?.ApplyMessage(entry.data);
                break;
        }
    }

    // Logger.csが書き出す1行 {"t":..,"type":"...","topic":"...","data":{...}} を
    // dataの中身を再パースせずそのまま切り出す。data は常に最後のフィールドなので、
    // 行末の閉じ括弧を除いた残り全部がそのままdataの生JSONになる。
    private static bool TryParseLine(string line, out LogEntry entry)
    {
        entry = default;

        if (string.IsNullOrWhiteSpace(line))
            return false;

        const string tKey = "\"t\":";
        const string typeKey = "\"type\":\"";
        const string topicKey = "\"topic\":\"";
        const string dataKey = "\"data\":";

        int tIndex = line.IndexOf(tKey, StringComparison.Ordinal);
        int typeIndex = line.IndexOf(typeKey, StringComparison.Ordinal);
        int topicIndex = line.IndexOf(topicKey, StringComparison.Ordinal);
        int dataIndex = line.IndexOf(dataKey, StringComparison.Ordinal);

        if (tIndex < 0 || typeIndex < 0 || topicIndex < 0 || dataIndex < 0)
            return false;

        int tStart = tIndex + tKey.Length;
        int tEnd = line.IndexOf(',', tStart);
        if (tEnd < 0 || !float.TryParse(line.Substring(tStart, tEnd - tStart), NumberStyles.Float, CultureInfo.InvariantCulture, out float t))
            return false;

        int typeStart = typeIndex + typeKey.Length;
        int typeEnd = line.IndexOf('"', typeStart);
        if (typeEnd < 0)
            return false;
        string type = line.Substring(typeStart, typeEnd - typeStart);

        int topicStart = topicIndex + topicKey.Length;
        int topicEnd = FindUnescapedQuote(line, topicStart);
        if (topicEnd < 0)
            return false;
        string topic = UnescapeJsonString(line.Substring(topicStart, topicEnd - topicStart));

        int dataStart = dataIndex + dataKey.Length;
        int dataEnd = line.LastIndexOf('}');
        if (dataEnd <= dataStart)
            return false;
        string data = line.Substring(dataStart, dataEnd - dataStart);

        entry = new LogEntry { t = t, type = type, topic = topic, data = data };
        return true;
    }

    private static int FindUnescapedQuote(string text, int startIndex)
    {
        for (int i = startIndex; i < text.Length; i++)
        {
            if (text[i] != '"')
                continue;

            int backslashCount = 0;
            for (int j = i - 1; j >= startIndex && text[j] == '\\'; j--)
                backslashCount++;

            if (backslashCount % 2 == 0)
                return i;
        }

        return -1;
    }

    private static string UnescapeJsonString(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        StringBuilder builder = new StringBuilder(value.Length);

        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                i++;
                builder.Append(value[i]);
            }
            else
            {
                builder.Append(value[i]);
            }
        }

        return builder.ToString();
    }

    private struct LogEntry
    {
        public float t;
        public string type;
        public string topic;
        public string data;
    }

    // =========================
    // Skeleton_FULL visual (JointPositionLoader.cs相当、複数人物対応)
    // =========================

    private void ApplySkeletonFullMessage(string json)
    {
        PersonPositionFullMessage message;

        try
        {
            message = JsonUtility.FromJson<PersonPositionFullMessage>(json);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"SessionPlayer: skeleton_full json parse failed. {ex.GetType().Name}: {ex.Message}");
            return;
        }

        if (message == null || string.IsNullOrWhiteSpace(message.label) || message.joints == null)
            return;

        if (!skeletonsByLabel.TryGetValue(message.label, out PersonSkeleton skeleton) || skeleton.root == null)
        {
            skeleton = CreateSkeleton(message.label);
            skeletonsByLabel[message.label] = skeleton;
        }

        scratchPositions.Clear();

        foreach (JointSample joint in message.joints)
        {
            if (Enum.TryParse(joint.name, out JointId jointId))
                scratchPositions[jointId] = new Vector3(joint.x, joint.y, joint.z);
        }

        foreach (KeyValuePair<JointId, Transform> entry in skeleton.joints)
        {
            if (scratchPositions.TryGetValue(entry.Key, out Vector3 position))
            {
                entry.Value.position = position;
                entry.Value.gameObject.SetActive(true);
            }
            else
            {
                entry.Value.gameObject.SetActive(false);
            }
        }

        foreach (BoneLine bone in skeleton.bones)
        {
            bool valid = true;

            for (int i = 0; i < bone.chain.Length; i++)
            {
                if (scratchPositions.TryGetValue(bone.chain[i], out Vector3 position))
                {
                    bone.line.SetPosition(i, position);
                }
                else
                {
                    valid = false;
                    break;
                }
            }

            bone.line.enabled = valid;
        }

        skeleton.lastReceivedTime = Time.unscaledTime;
    }

    private void RemoveStaleSkeletons()
    {
        if (skeletonLifetime <= 0f)
            return;

        List<string> staleLabels = null;

        foreach (KeyValuePair<string, PersonSkeleton> entry in skeletonsByLabel)
        {
            if (Time.unscaledTime - entry.Value.lastReceivedTime <= skeletonLifetime)
                continue;

            staleLabels ??= new List<string>();
            staleLabels.Add(entry.Key);
        }

        if (staleLabels == null)
            return;

        foreach (string label in staleLabels)
        {
            if (skeletonsByLabel[label].root != null)
                Destroy(skeletonsByLabel[label].root.gameObject);

            skeletonsByLabel.Remove(label);
        }
    }

    private PersonSkeleton CreateSkeleton(string label)
    {
        GameObject root = new GameObject($"SkeletonFull_{label}");
        root.transform.SetParent(transform, false);

        Dictionary<JointId, Transform> joints = new Dictionary<JointId, Transform>();

        for (JointId jointId = JointId.Pelvis; jointId < JointId.Count; jointId++)
        {
            GameObject jointObject = jointPrefab != null
                ? Instantiate(jointPrefab)
                : GameObject.CreatePrimitive(PrimitiveType.Sphere);

            jointObject.name = jointId.ToString();
            jointObject.transform.SetParent(root.transform, false);
            jointObject.transform.localScale = Vector3.one * Mathf.Max(jointScale, 0.001f);
            jointObject.SetActive(false);

            joints[jointId] = jointObject.transform;
        }

        List<BoneLine> bones = new List<BoneLine>
        {
            CreateBone(root.transform, SpineChain),
            CreateBone(root.transform, LegChain),
            CreateBone(root.transform, ArmChain)
        };

        return new PersonSkeleton
        {
            root = root.transform,
            joints = joints,
            bones = bones,
            lastReceivedTime = Time.unscaledTime
        };
    }

    private BoneLine CreateBone(Transform parent, JointId[] chain)
    {
        GameObject lineObject = new GameObject("Bone");
        lineObject.transform.SetParent(parent, false);

        LineRenderer line = lineObject.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.material = lineMaterial != null ? lineMaterial : new Material(Shader.Find("Sprites/Default"));
        line.startWidth = lineWidth;
        line.endWidth = lineWidth;
        line.positionCount = chain.Length;
        line.enabled = false;

        return new BoneLine { chain = chain, line = line };
    }

    private class PersonSkeleton
    {
        public Transform root;
        public Dictionary<JointId, Transform> joints;
        public List<BoneLine> bones;
        public float lastReceivedTime;
    }

    private class BoneLine
    {
        public JointId[] chain;
        public LineRenderer line;
    }

    [Serializable]
    private class JointSample
    {
        public string name;
        public float x;
        public float y;
        public float z;
    }

    [Serializable]
    private class PersonPositionFullMessage
    {
        public string label;
        public int deviceIndex;
        public uint bodyId;
        public float confidence;
        public JointSample[] joints;
    }
}
