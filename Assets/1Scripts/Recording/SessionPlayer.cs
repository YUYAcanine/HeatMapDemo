using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Azure.Kinect.BodyTracking;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;

// MultiIntegrationViewerシーン専用。Logger.csが書き出したNDJSON(.jsonl)ログを読み込み、
// スペースキーで再生の開始/停止をトグルする。
//
// NavSub.cs / SkeletonRealtimeSubscriber.cs / Subscriber.cs のライブMQTT受信には
// 依存せず、それぞれが行っていた「JSON→表示」の処理をこのクラス自身に持たせている。
// そのため、TestSubscribe配下のNavSub/SkeletonRealtimeSubscriber/TurtleSubscriberの
// コンポーネントを無効化(オフ)にしていてもSessionPlayer単体でログ再生が完結する。
public class SessionPlayer : MonoBehaviour
{
    [Header("Log File")]
    [Tooltip("特定の.jsonlファイルへのフルパス、またはディレクトリ(その中の最新の.jsonlを使う)。空の場合は Assets/Data/Logs を使う。")]
    [SerializeField] private string logFilePath = "Assets/Data/Logs";

    [Header("Playback")]
    [SerializeField] private KeyCode toggleKey = KeyCode.Space;

    [Header("NavMesh Visual")]
    [SerializeField] private int navAgentTypeId = 0;
    [SerializeField] private float navAgentRadius = 0.01f;
    [SerializeField] private float navAgentHeight = 0.05f;
    [SerializeField] private float navVoxelSize = 0.03f;
    [SerializeField] private float navMinRegionArea = 0f;
    [SerializeField] private float navBoundsMargin = 2f;
    [SerializeField] private bool showNavSourceMesh = false;
    [SerializeField] private Color navSourceMeshColor = new Color(0.15f, 0.9f, 0.25f, 0.35f);
    [SerializeField] private float navLinkWidth = 0.18f;

    [Header("Skeleton Visual (person_coordinate)")]
    [SerializeField] private Transform skeletonMarkerRoot;
    [SerializeField] private float skeletonMarkerScale = 0.2f;
    [SerializeField] private float skeletonMarkerLifetime = 2f;
    [SerializeField] private Color skeletonMarkerColor = Color.cyan;
    [SerializeField] private Color skeletonLabelColor = Color.white;
    [SerializeField] private bool skeletonShowLabel = true;
    [SerializeField] private float skeletonLabelCharacterSize = 0.08f;
    [Tooltip("複数人いる場合、headPelvisDistanceが最も短い人物のPelvisマーカーをこの色にする。")]
    [SerializeField] private Color shortestHeadPelvisColor = Color.red;
    [Tooltip("headPelvisDistanceがこの値未満の場合はトラッキング異常とみなし、色分け対象から除外する。")]
    [SerializeField] private float minValidHeadPelvisDistance = 0.05f;
    [SerializeField] private bool showHeadToNoseVector = true;
    [SerializeField] private Color headToNoseVectorColor = Color.yellow;
    [SerializeField] private float headToNoseVectorWidth = 0.015f;

    [Header("Object Visual (object_coordinate)")]
    [SerializeField] private Transform objectMarkerRoot;
    [Tooltip("MultiIntegrationLoggerシーンのTurtleSubscriberが持つ、部屋に対して校正済みのTransform。" +
        "設定すると、生座標をこのTransformのローカル座標として解釈し、Loggerのリアルタイム表示と同じワールド位置に変換する。" +
        "TestSubscribe配下は非表示のためobjectMarkerRootとしては使えないが、参照だけなら非アクティブでも問題ない。")]
    [SerializeField] private Transform objectCoordinateCalibration;
    [SerializeField] private float objectMarkerScale = 0.12f;
    [SerializeField] private float objectLabelVerticalPadding = 0.015f;
    [SerializeField] private float objectLabelCharacterSize = 0.08f;
    [SerializeField] private float objectCoordinateScale = 1.0f;
    [SerializeField] private Vector3 objectCoordinateOffset = Vector3.zero;
    [SerializeField] private Color objectMarkerColor = Color.yellow;
    [SerializeField] private Color objectLabelColor = Color.white;

    [Header("Skeleton_FULL Visual (JointPositionLoader相当)")]
    [Tooltip("空の場合は球プリミティブを自動生成する。")]
    [SerializeField] private GameObject jointPrefab;
    [Tooltip("空の場合はSprites/Defaultシェーダーのマテリアルを自動生成する。")]
    [SerializeField] private Material lineMaterial;
    [SerializeField] private float jointScale = 0.06f;
    [SerializeField] private float lineWidth = 0.02f;
    [Tooltip("この秒数以上メッセージが来なければそのラベルの骨格を消す。0以下で無効。")]
    [SerializeField] private float skeletonFullLifetime = 2f;

    private readonly List<LogEntry> entries = new List<LogEntry>();
    private int nextEntryIndex;
    private float playbackStartTime;

    // --- NavMesh state ---
    private GameObject navMeshSourceObject;
    private NavMeshDataInstance navMeshDataInstance;
    private readonly List<GameObject> navLinkObjects = new List<GameObject>();

    // --- Skeleton (person_coordinate) state ---
    private readonly Dictionary<string, PersonMarker> skeletonMarkersByLabel = new Dictionary<string, PersonMarker>();

    // --- Object (object_coordinate) state ---
    private readonly Dictionary<string, GameObject> objectMarkersByLabel = new Dictionary<string, GameObject>();

    // --- Skeleton_FULL (person_coordinate_FULL) state ---
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

    // headPelvisDistanceが最も短い(=ハイライトされている)人物。
    public string ShortestSkeletonLabel { get; private set; }
    public Transform ShortestSkeletonTransform { get; private set; }

    private void Update()
    {
        if (Input.GetKeyDown(toggleKey))
        {
            if (IsPlaying)
                Stop();
            else
                Play();
        }

        RemoveStaleSkeletonMarkers();
        RemoveStaleSkeletonFullMarkers();

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

    private void OnDestroy()
    {
        if (navMeshDataInstance.valid)
            navMeshDataInstance.Remove();
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
    // NavMeshLinkはDestroy()で古いものを破棄してから新規生成するが、Destroy()は
    // フレーム終端まで実際の破棄が遅延されるため、同一フレーム内でnavmesh適用を
    // 連続実行すると「NavMeshLinkが破棄済みなのにアクセスしようとした」というエラーに
    // なる。ライブMQTT受信時はメッセージが1通ずつ間隔を空けて届くため起きないが、
    // ログ再生は記録時の間隔をそのまま再現するので、記録時に短時間で連続していた
    // navmeshがまとめて1フレームに来ると発生し得る。
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
                ApplyNavMeshMessage(entry.data);
                break;
            case "skeleton":
                ApplySkeletonMessage(entry.data);
                break;
            case "skeleton_full":
                ApplySkeletonFullMessage(entry.data);
                break;
            case "object":
                ApplyObjectMessage(entry.data);
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
    // NavMesh visual (NavSub.cs相当)
    // =========================

    private void ApplyNavMeshMessage(string json)
    {
        NavMeshMessage message;

        try
        {
            message = JsonUtility.FromJson<NavMeshMessage>(json);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"SessionPlayer: navmesh json parse failed. {ex.GetType().Name}: {ex.Message}");
            return;
        }

        if (message == null || message.vertices == null || message.triangles == null ||
            message.vertices.Count == 0 || message.triangles.Count == 0)
        {
            Debug.LogWarning("SessionPlayer: received NavMesh message has no geometry.");
            return;
        }

        Mesh mesh = new Mesh
        {
            name = "SessionPlayerNavMeshSourceMesh",
            indexFormat = UnityEngine.Rendering.IndexFormat.UInt32,
            vertices = message.vertices.ToArray(),
            triangles = message.triangles.ToArray()
        };
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        RebuildNavMeshFromMesh(mesh);
        RebuildNavLinks(message.links);

        Debug.Log(
            $"SessionPlayer: NavMesh rebuilt. vertices={message.vertices.Count}, triangles={message.triangles.Count / 3}, links={message.links?.Count ?? 0}");
    }

    private void RebuildNavMeshFromMesh(Mesh mesh)
    {
        if (navMeshDataInstance.valid)
        {
            navMeshDataInstance.Remove();
            navMeshDataInstance = new NavMeshDataInstance();
        }

        if (navMeshSourceObject == null)
        {
            navMeshSourceObject = new GameObject("SessionPlayerNavMeshSourceMesh");
            navMeshSourceObject.transform.SetParent(transform, false);

            // 受信した頂点はワールド座標の絶対値なので、このオブジェクト自身は
            // 必ずワールド原点・無回転・無スケールに固定する。
            navMeshSourceObject.transform.position = Vector3.zero;
            navMeshSourceObject.transform.rotation = Quaternion.identity;
            navMeshSourceObject.transform.localScale = Vector3.one;
        }

        MeshFilter meshFilter = navMeshSourceObject.GetComponent<MeshFilter>();
        if (meshFilter == null)
            meshFilter = navMeshSourceObject.AddComponent<MeshFilter>();
        meshFilter.sharedMesh = mesh;

        MeshRenderer meshRenderer = navMeshSourceObject.GetComponent<MeshRenderer>();
        if (meshRenderer == null)
            meshRenderer = navMeshSourceObject.AddComponent<MeshRenderer>();
        meshRenderer.sharedMaterial = CreateTransparentMaterial(navSourceMeshColor);
        meshRenderer.enabled = showNavSourceMesh;

        NavMeshBuildSettings buildSettings = NavMesh.GetSettingsByID(navAgentTypeId);
        buildSettings.agentRadius = Mathf.Max(navAgentRadius, 0.001f);
        buildSettings.agentHeight = Mathf.Max(navAgentHeight, 0.001f);
        buildSettings.agentClimb = 100f;
        buildSettings.agentSlope = 89f;
        buildSettings.overrideVoxelSize = true;
        buildSettings.voxelSize = Mathf.Max(navVoxelSize, 0.005f);
        buildSettings.overrideTileSize = true;
        buildSettings.tileSize = 64;
        buildSettings.minRegionArea = Mathf.Max(navMinRegionArea, 0f);

        List<NavMeshBuildSource> sources = new List<NavMeshBuildSource>
        {
            new NavMeshBuildSource
            {
                shape = NavMeshBuildSourceShape.Mesh,
                sourceObject = mesh,
                transform = Matrix4x4.identity,
                area = 0
            }
        };

        Bounds bounds = mesh.bounds;
        bounds.Expand(Vector3.one * navBoundsMargin);

        NavMeshData navMeshData = NavMeshBuilder.BuildNavMeshData(
            buildSettings,
            sources,
            bounds,
            Vector3.zero,
            Quaternion.identity);

        if (navMeshData == null)
        {
            Debug.LogWarning("SessionPlayer: NavMesh build returned null.");
            return;
        }

        navMeshDataInstance = NavMesh.AddNavMeshData(navMeshData);
    }

    private void RebuildNavLinks(List<LinkMessage> links)
    {
        foreach (GameObject linkObject in navLinkObjects)
        {
            if (linkObject != null)
                Destroy(linkObject);
        }
        navLinkObjects.Clear();

        if (links == null)
            return;

        for (int i = 0; i < links.Count; i++)
        {
            LinkMessage linkMessage = links[i];

            GameObject linkObject = new GameObject($"SessionPlayerNavLink_{i}");
            linkObject.transform.SetParent(transform, false);
            linkObject.transform.rotation = Quaternion.identity;
            linkObject.transform.localScale = Vector3.one;
            linkObject.transform.position = linkMessage.start;

            NavMeshLink link = linkObject.AddComponent<NavMeshLink>();
            link.bidirectional = linkMessage.bidirectional;
            link.width = Mathf.Max(linkMessage.width > 0f ? linkMessage.width : navLinkWidth, 0.01f);
            link.costModifier = Mathf.Max(linkMessage.costModifier, 1);
            link.autoUpdate = true;
            link.startPoint = Vector3.zero;
            link.endPoint = linkObject.transform.InverseTransformPoint(linkMessage.end);

            navLinkObjects.Add(linkObject);
        }
    }

    private static Material CreateTransparentMaterial(Color color)
    {
        Material material = new Material(Shader.Find("Standard"));
        material.SetFloat("_Mode", 3);
        material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        material.SetInt("_ZWrite", 0);
        material.DisableKeyword("_ALPHATEST_ON");
        material.EnableKeyword("_ALPHABLEND_ON");
        material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        material.renderQueue = 3000;
        material.color = color;
        return material;
    }

    [Serializable]
    private class LinkMessage
    {
        public Vector3 start;
        public Vector3 end;
        public bool bidirectional;
        public int costModifier;
        public float width;
    }

    [Serializable]
    private class NavMeshMessage
    {
        public List<Vector3> vertices;
        public List<int> triangles;
        public List<LinkMessage> links;
        public int vertexCount;
        public int triangleCount;
        public int linkCount;
    }

    // =========================
    // Skeleton visual (SkeletonRealtimeSubscriber.cs相当、person_coordinate)
    // =========================

    private void ApplySkeletonMessage(string json)
    {
        PersonPositionMessage message;

        try
        {
            message = JsonUtility.FromJson<PersonPositionMessage>(json);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"SessionPlayer: skeleton json parse failed. {ex.GetType().Name}: {ex.Message}, json={json}");
            return;
        }

        if (message == null || string.IsNullOrWhiteSpace(message.label))
            return;

        Vector3 position = new Vector3(message.x, message.y, message.z);

        if (!skeletonMarkersByLabel.TryGetValue(message.label, out PersonMarker marker) || marker.root == null)
        {
            marker = CreateSkeletonMarker(message.label);
            skeletonMarkersByLabel[message.label] = marker;
        }

        marker.root.position = position;
        marker.lastReceivedTime = Time.unscaledTime;
        marker.headPelvisDistance = message.headPelvisDistance;

        if (marker.labelText != null)
            marker.labelText.text = $"{message.label}\nconf={message.confidence:F1}";

        UpdateHeadToNoseVector(marker, message);
        UpdateSkeletonHighlight();
    }

    private void UpdateHeadToNoseVector(PersonMarker marker, PersonPositionMessage message)
    {
        if (marker.vectorLine == null)
            return;

        bool hasVector = showHeadToNoseVector && message.headToNose.sqrMagnitude > 0f;

        marker.vectorLine.enabled = hasVector;

        if (!hasVector)
            return;

        Vector3 headPosition = message.head;
        Vector3 nosePosition = headPosition + message.headToNose;

        marker.vectorLine.SetPosition(0, headPosition);
        marker.vectorLine.SetPosition(1, nosePosition);
    }

    private void UpdateSkeletonHighlight()
    {
        string shortestLabel = null;
        float shortestDistance = float.MaxValue;

        foreach (KeyValuePair<string, PersonMarker> entry in skeletonMarkersByLabel)
        {
            float distance = entry.Value.headPelvisDistance;

            if (distance < minValidHeadPelvisDistance)
                continue;

            if (distance < shortestDistance)
            {
                shortestDistance = distance;
                shortestLabel = entry.Key;
            }
        }

        foreach (KeyValuePair<string, PersonMarker> entry in skeletonMarkersByLabel)
        {
            if (entry.Value.sphereRenderer == null)
                continue;

            entry.Value.sphereRenderer.material.color =
                entry.Key == shortestLabel ? shortestHeadPelvisColor : skeletonMarkerColor;
        }

        ShortestSkeletonLabel = shortestLabel;
        ShortestSkeletonTransform =
            shortestLabel != null && skeletonMarkersByLabel.TryGetValue(shortestLabel, out PersonMarker shortestMarker)
                ? shortestMarker.root
                : null;
    }

    private PersonMarker CreateSkeletonMarker(string label)
    {
        Transform parent = skeletonMarkerRoot != null ? skeletonMarkerRoot : transform;

        GameObject root = new GameObject($"PersonMarker_{label}");
        root.transform.SetParent(parent, false);

        GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.name = "Sphere";
        sphere.transform.SetParent(root.transform, false);
        sphere.transform.localPosition = Vector3.zero;
        sphere.transform.localScale = Vector3.one * Mathf.Max(skeletonMarkerScale, 0.01f);

        Renderer sphereRenderer = sphere.GetComponent<Renderer>();
        if (sphereRenderer != null)
            sphereRenderer.material.color = skeletonMarkerColor;

        TextMesh labelText = null;

        if (skeletonShowLabel)
        {
            GameObject labelObject = new GameObject("Label");
            labelObject.transform.SetParent(sphere.transform, false);
            labelObject.transform.localPosition = Vector3.up * 0.8f;
            labelObject.AddComponent<BillboardLabel>();

            labelText = labelObject.AddComponent<TextMesh>();
            labelText.text = label;
            labelText.characterSize = Mathf.Max(skeletonLabelCharacterSize, 0.01f);
            labelText.fontSize = 32;
            labelText.anchor = TextAnchor.MiddleCenter;
            labelText.alignment = TextAlignment.Center;
            labelText.color = skeletonLabelColor;
        }

        LineRenderer vectorLine = root.AddComponent<LineRenderer>();
        vectorLine.useWorldSpace = true;
        vectorLine.positionCount = 2;
        vectorLine.startWidth = headToNoseVectorWidth;
        vectorLine.endWidth = headToNoseVectorWidth;
        vectorLine.material = new Material(Shader.Find("Sprites/Default"));
        vectorLine.startColor = headToNoseVectorColor;
        vectorLine.endColor = headToNoseVectorColor;
        vectorLine.enabled = false;

        return new PersonMarker
        {
            root = root.transform,
            labelText = labelText,
            lastReceivedTime = Time.unscaledTime,
            sphereRenderer = sphereRenderer,
            vectorLine = vectorLine
        };
    }

    private void RemoveStaleSkeletonMarkers()
    {
        if (skeletonMarkerLifetime <= 0f)
            return;

        List<string> staleLabels = null;

        foreach (KeyValuePair<string, PersonMarker> entry in skeletonMarkersByLabel)
        {
            if (Time.unscaledTime - entry.Value.lastReceivedTime <= skeletonMarkerLifetime)
                continue;

            staleLabels ??= new List<string>();
            staleLabels.Add(entry.Key);
        }

        if (staleLabels == null)
            return;

        foreach (string label in staleLabels)
        {
            if (skeletonMarkersByLabel[label].root != null)
                Destroy(skeletonMarkersByLabel[label].root.gameObject);

            skeletonMarkersByLabel.Remove(label);
        }

        UpdateSkeletonHighlight();
    }

    private class PersonMarker
    {
        public Transform root;
        public TextMesh labelText;
        public float lastReceivedTime;
        public Renderer sphereRenderer;
        public LineRenderer vectorLine;
        public float headPelvisDistance;
    }

#pragma warning disable 0649
    [Serializable]
    private class PersonPositionMessage
    {
        public string label;
        public float x;
        public float y;
        public float z;
        public float confidence;
        public int deviceIndex;
        public uint bodyId;
        public float headPelvisDistance;
        public Vector3 head;
        public Vector3 headToNose;
    }
#pragma warning restore 0649

    // =========================
    // Object visual (Subscriber.cs相当、object_coordinate)
    // =========================

    private void ApplyObjectMessage(string json)
    {
        ObjectTopicMessage message;

        try
        {
            message = JsonUtility.FromJson<ObjectTopicMessage>(json);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"SessionPlayer: object json parse failed. {ex.GetType().Name}: {ex.Message}, json={json}");
            return;
        }

        string label = GetObjectLabel(message);

        if (string.IsNullOrWhiteSpace(label))
            return;

        label = label.Trim();

        if (!TryGetObjectPosition(json, message, out Vector3 rawPosition))
            return;

        Vector3 calibratedPosition = rawPosition * objectCoordinateScale + objectCoordinateOffset;

        // objectCoordinateCalibrationはMultiIntegrationLoggerのTurtleSubscriberが実際に
        // マーカーを配置しているTransform(部屋に対して校正済み)を指す。これを介してワールド
        // 座標に変換することで、Logger側のリアルタイム表示と同じ位置に描画される。
        // 未設定の場合は従来通りobjectMarkerRoot(またはこのオブジェクト自身)のローカル座標として扱う。
        Vector3 worldPosition = objectCoordinateCalibration != null
            ? objectCoordinateCalibration.TransformPoint(calibratedPosition)
            : (objectMarkerRoot != null ? objectMarkerRoot : transform).TransformPoint(calibratedPosition);

        bool markerAlreadyExists =
            objectMarkersByLabel.TryGetValue(label, out GameObject marker) && marker != null;

        if (markerAlreadyExists)
            marker.transform.position = worldPosition;
        else
            CreateObjectMarker(label, worldPosition);
    }

    private void CreateObjectMarker(string label, Vector3 worldPosition)
    {
        Transform parent = objectMarkerRoot != null ? objectMarkerRoot : transform;

        GameObject markerRootObject = new GameObject($"ObjectMarker_{label}");
        markerRootObject.transform.SetParent(parent, false);
        markerRootObject.transform.position = worldPosition;

        GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        marker.name = "Sphere";
        marker.transform.SetParent(markerRootObject.transform, false);
        marker.transform.localPosition = Vector3.zero;
        marker.transform.localScale = Vector3.one * Mathf.Max(objectMarkerScale, 0.01f);

        Renderer markerRenderer = marker.GetComponent<Renderer>();
        if (markerRenderer != null)
            markerRenderer.material.color = objectMarkerColor;

        GameObject labelObject = new GameObject("Label");
        labelObject.transform.SetParent(marker.transform, false);
        labelObject.transform.localPosition =
            Vector3.up * (0.5f + Mathf.Max(objectLabelVerticalPadding, 0f) / Mathf.Max(objectMarkerScale, 0.01f));
        labelObject.AddComponent<BillboardLabel>();

        TextMesh textMesh = labelObject.AddComponent<TextMesh>();
        textMesh.text = label;
        textMesh.characterSize = Mathf.Max(objectLabelCharacterSize, 0.01f);
        textMesh.fontSize = 32;
        textMesh.anchor = TextAnchor.MiddleCenter;
        textMesh.alignment = TextAlignment.Center;
        textMesh.color = objectLabelColor;

        MeshRenderer textRenderer = labelObject.GetComponent<MeshRenderer>();
        if (textRenderer != null)
            textRenderer.sortingOrder = 100;

        objectMarkersByLabel[label] = markerRootObject;
    }

    private static string GetObjectLabel(ObjectTopicMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.label))
            return message.label;

        if (!string.IsNullOrWhiteSpace(message.objectName))
            return message.objectName;

        if (!string.IsNullOrWhiteSpace(message.object_name))
            return message.object_name;

        if (!string.IsNullOrWhiteSpace(message.name))
            return message.name;

        return "object";
    }

    private static bool TryGetObjectPosition(string json, ObjectTopicMessage message, out Vector3 position)
    {
        if (TryGetPositionFromJsonText(json, out position))
            return true;

        if (message.position != null && message.position.HasValue())
        {
            position = message.position.ToVector3();
            return true;
        }

        if (message.coordinate != null && message.coordinate.HasValue())
        {
            position = message.coordinate.ToVector3();
            return true;
        }

        if (message.coordinates != null && message.coordinates.HasValue())
        {
            position = message.coordinates.ToVector3();
            return true;
        }

        if (message.point != null && message.point.HasValue())
        {
            position = message.point.ToVector3();
            return true;
        }

        if (message.center != null && message.center.HasValue())
        {
            position = message.center.ToVector3();
            return true;
        }

        if (message.xyz != null && message.xyz.Length >= 3)
        {
            position = new Vector3(message.xyz[0], message.xyz[1], message.xyz[2]);
            return true;
        }

        if (!Mathf.Approximately(message.x, 0f) ||
            !Mathf.Approximately(message.y, 0f) ||
            !Mathf.Approximately(message.z, 0f))
        {
            position = new Vector3(message.x, message.y, message.z);
            return true;
        }

        position = Vector3.zero;
        return false;
    }

    private static bool TryGetPositionFromJsonText(string json, out Vector3 position)
    {
        const string number = @"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?";
        string arrayPattern =
            @"[""'](?:position|coordinate|coordinates|point|center|xyz)[""']\s*:\s*\[\s*(" + number + @")\s*,\s*(" + number + @")\s*,\s*(" + number + @")\s*\]";
        Match arrayMatch = Regex.Match(json, arrayPattern, RegexOptions.IgnoreCase);

        if (arrayMatch.Success &&
            TryParseFloat(arrayMatch.Groups[1].Value, out float arrayX) &&
            TryParseFloat(arrayMatch.Groups[2].Value, out float arrayY) &&
            TryParseFloat(arrayMatch.Groups[3].Value, out float arrayZ))
        {
            position = new Vector3(arrayX, arrayY, arrayZ);
            return true;
        }

        if (TryGetNamedNumber(json, "x", out float x) &&
            TryGetNamedNumber(json, "y", out float y) &&
            TryGetNamedNumber(json, "z", out float z))
        {
            position = new Vector3(x, y, z);
            return true;
        }

        position = Vector3.zero;
        return false;
    }

    private static bool TryGetNamedNumber(string json, string key, out float value)
    {
        const string number = @"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?";
        Match match = Regex.Match(
            json,
            @"[""']" + Regex.Escape(key) + @"[""']\s*:\s*(" + number + @")",
            RegexOptions.IgnoreCase);

        if (match.Success)
            return TryParseFloat(match.Groups[1].Value, out value);

        value = 0f;
        return false;
    }

    private static bool TryParseFloat(string text, out float value)
    {
        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

#pragma warning disable 0649
    [Serializable]
    private class ObjectTopicMessage
    {
        public string label;
        public string objectName;
        public string object_name;
        public string name;
        public float x;
        public float y;
        public float z;
        public float[] xyz;
        public VectorPayload position;
        public VectorPayload coordinate;
        public VectorPayload coordinates;
        public VectorPayload point;
        public VectorPayload center;
    }

    [Serializable]
    private class VectorPayload
    {
        public float x;
        public float y;
        public float z;

        public Vector3 ToVector3() => new Vector3(x, y, z);

        public bool HasValue() =>
            !Mathf.Approximately(x, 0f) || !Mathf.Approximately(y, 0f) || !Mathf.Approximately(z, 0f);
    }
#pragma warning restore 0649

    private class BillboardLabel : MonoBehaviour
    {
        private void LateUpdate()
        {
            Camera targetCamera = Camera.main;

            if (targetCamera == null)
                return;

            transform.rotation = Quaternion.LookRotation(
                transform.position - targetCamera.transform.position,
                Vector3.up);
        }
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

    private void RemoveStaleSkeletonFullMarkers()
    {
        if (skeletonFullLifetime <= 0f)
            return;

        List<string> staleLabels = null;

        foreach (KeyValuePair<string, PersonSkeleton> entry in skeletonsByLabel)
        {
            if (Time.unscaledTime - entry.Value.lastReceivedTime <= skeletonFullLifetime)
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
