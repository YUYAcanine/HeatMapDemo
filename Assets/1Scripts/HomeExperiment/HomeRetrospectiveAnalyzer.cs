using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Azure.Kinect.BodyTracking;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// シーン7(7RetrospectiveAnalyze)用。子供が物に着いた時刻からさかのぼって、それまでの各物体の「注視可能性」の時間変化を見る
// (到達時刻からのレトロスペクティブ分析)。
//
// 注視可能性スコア(0〜1):
//   頭部方向(シーン4〜6と同じ。キネクト: 頭→鼻を下へ補正 / 画像: 推定した視線や頭の向き)を、誤差を含む視線と考える。
//   誤差は頭部方向のまわりに標準偏差 uncertaintySigma 度で広がるとし、
//     θ = 頭部方向と、物(メッシュの頂点を囲む箱)のいちばん近い所とのなす角(頭部方向の線が箱に当たれば 0)
//     スコア = exp(−θ² / (2 σ²)) × 距離による減衰
//   とする。物の見込み角(大きさ・近さ)は「箱のいちばん近い所」で、距離による減衰は distanceDecay(0 なら減衰なし)で入る。
//   遮蔽(間に物があって見えない)はまだ考えていない。
//
// 到達: 子供(childName)の骨盤から物の箱までの水平距離が approachDistance 以下になった最初の時刻。
//       その前に leaveDistance より離れているのを見ていたときだけ数える(シーン6と同じ)。
//
// 画面:
//   Event モード   … 1回の到達について、到達の windowSeconds 秒前から到達までの、物ごとの注視可能性の折れ線グラフとヒートマップ
//                    (到達した物は太線と ● 。再生位置を縦線で示す)。左の表は物ごとの特徴量
//   Average モード … 全部の到達の平均。「到達した物」と「到達しなかった物(平均)」の注視可能性の時間変化
//   3D            … 骨格・子供の頭部方向と誤差の円錐(σ)・物の箱(注視可能性が高いほど赤)
//   < Prev / Next > で到達を選ぶ。Replay で到達の windowSeconds 秒前から到達まで再生する。
//   Save          … Analysis/Retrospective/retrospective_<骨格>_curves.csv(時間変化)/ _features.csv(特徴量)/ _params.json に保存
[RequireComponent(typeof(HomeEnvLoader))]
public class HomeRetrospectiveAnalyzer : MonoBehaviour, IHomeDataRootProvider
{
    public enum ChartMode
    {
        Event,
        Average
    }

    [Header("Data Location")]
    [Tooltip("データのルートフォルダ。<ここ>/<実験の名前>/Skeleton/<実験対象者>/ から人物ごとの骨格を読み、Analysis/Retrospective/ に保存する。\n" +
             "空なら既定の Assets/Data/HomeExperiment。Env もここの <実験の名前>/Env/ から読む。")]
    [HomeDataRootFolder]
    [SerializeField] private string dataRootDirectory = "";

    [Header("Subject")]
    [HomeExperimentFolder(HomeExperimentFolderAttribute.Kind.Subject)]
    [SerializeField] private string subjectName = "";

    [Header("Skeleton (シーン4〜6と同じ)")]
    [SerializeField] private HomeGazeAnalyzer.SkeletonSource skeletonSource = HomeGazeAnalyzer.SkeletonSource.Filtered;
    [SerializeField] private HomeSkeletonFilter.ConfidenceMode filteredMode = HomeSkeletonFilter.ConfidenceMode.HeadJoints;
    [Tooltip("頭の向きを平滑化した骨格の名前(grouped_<Mode>_<名前>.json。例: smooth05)。空なら平滑化前")]
    [HomeSkeletonVariant]
    [SerializeField] private string skeletonVariant = "";
    [SerializeField] private string kinectId = "A";
    [SerializeField] private HomeGazeAnalyzer.ImageDirection imageDirection = HomeGazeAnalyzer.ImageDirection.GazeElseHead;

    [Header("Gaze (シーン4〜6と同じ)")]
    [SerializeField] private HomeGazeAnalyzer.HeadDirectionMethod headDirectionMethod = HomeGazeAnalyzer.HeadDirectionMethod.HeadToNose;
    [Tooltip("頭部方向を下へ補正する角度(度)")]
    [SerializeField] private float downwardAngle = 24.4f;

    [Header("Targets (シーン4〜6と同じ)")]
    [SerializeField] private bool useEnvTargetObjects = true;
    [SerializeField] private string[] targetNames = new string[0];
    [SerializeField] private GameObject[] targetObjects = new GameObject[0];

    [Header("Person")]
    [Tooltip("子供の人物の名前(シーン3のグループ名)")]
    [SerializeField] private string childName = "child";

    [Header("Gaze Uncertainty (注視可能性スコア)")]
    [Tooltip("頭部方向の誤差の標準偏差(度)。シーン4の注視のコーンの半角と同じ 10.14 を既定にする")]
    [SerializeField] private float uncertaintySigma = 10.14f;
    [Tooltip("距離による減衰の長さ(m)。スコアに exp(−距離 / これ) を掛ける。0 なら減衰なし")]
    [SerializeField] private float distanceDecay = 0f;

    [Header("Arrival (シーン6と同じ)")]
    [SerializeField] private float approachDistance = 0.6f;
    [SerializeField] private float leaveDistance = 1.0f;
    [SerializeField] private float trackGapSeconds = 1f;

    [Header("Retrospective")]
    [Tooltip("到達の何秒前からを見るか")]
    [SerializeField] private float windowSeconds = 10f;
    [Tooltip("ヒートマップの横の区切りの数")]
    [SerializeField] private int heatmapBins = 100;
    [Tooltip("特徴量で「注視可能性が高い」とするスコア")]
    [SerializeField] private float highThreshold = 0.5f;
    [Tooltip("特徴量の「到達直前」とする時間(秒)")]
    [SerializeField] private float lateSeconds = 2f;
    [SerializeField] private ChartMode chartMode = ChartMode.Event;

    [Header("Visual")]
    [SerializeField] private GameObject jointPrefab;
    [Tooltip("空の場合は頂点カラー対応の Sprites/Default を使う。")]
    [SerializeField] private Material lineMaterial;
    [SerializeField] private float jointScale = 0.06f;
    [SerializeField] private float lineWidth = 0.02f;
    [SerializeField] private Color[] personColors =
    {
        Color.red, Color.green, Color.blue, Color.yellow, Color.cyan, Color.magenta
    };
    [Tooltip("物ごとの線の色(物の順)")]
    [SerializeField] private Color[] objectColors =
    {
        new Color(0.85f, 0.15f, 0.2f), new Color(0.15f, 0.45f, 0.9f), new Color(0.2f, 0.65f, 0.3f),
        new Color(0.9f, 0.6f, 0.1f), new Color(0.55f, 0.3f, 0.85f), new Color(0.4f, 0.4f, 0.4f)
    };
    [Tooltip("誤差の円錐の長さ(m)")]
    [SerializeField] private float coneLength = 1.5f;
    [Tooltip("グラフの枠の大きさ(画面の右上から)")]
    [SerializeField] private Vector2 chartPanelSize = new Vector2(760f, 620f);
    [SerializeField] private Vector2 chartPanelOffset = new Vector2(-20f, -20f);

    [Header("Playback")]
    [SerializeField] private float playbackSpeed = 1f;
    [SerializeField] private KeyCode playStopKey = KeyCode.Space;

    [Header("Output")]
    [SerializeField] private string outputLabel = "";

    [Header("UI")]
    [SerializeField] private Button playButton;
    [SerializeField] private Button stopButton;
    [SerializeField] private Button analyzeAllButton;
    [SerializeField] private Button saveButton;
    [SerializeField] private TMP_Text frameText;
    [SerializeField] private TMP_Text scoreText;

    private const float MaxFrameDuration = 0.2f;
    // 平均のグラフの時刻の刻み(秒)
    private const float AverageStep = 0.1f;
    private const int ConeSegments = 24;
    private const int ChartWidth = 680;
    private const int ChartHeight = 250;

    private HomeEnvLoader env;
    public string DataRootDirectory => dataRootDirectory;

    private readonly List<Sample> samples = new List<Sample>();
    private readonly List<Target> targets = new List<Target>();
    private readonly Dictionary<int, string> personNames = new Dictionary<int, string>();
    private readonly List<ArrivalEvent> events = new List<ArrivalEvent>();
    private ChildTrack child;
    private float duration;

    private float playbackTime;
    private bool isPlaying;
    private float stopAt = -1f;
    private int shownSample = -1;
    private int currentEvent = -1;

    // 3D
    private Transform visualRoot;
    private HomeSkeletonBodyVisual.Style visualStyle;
    private readonly Dictionary<int, HomeSkeletonBodyVisual> bodies = new Dictionary<int, HomeSkeletonBodyVisual>();
    private LineRenderer gazeLine;
    private LineRenderer coneRing;
    private readonly List<LineRenderer> coneEdges = new List<LineRenderer>();
    private readonly List<LineRenderer[]> boxLines = new List<LineRenderer[]>();
    private Material sharedLineMaterial;

    // グラフ
    private Sprite uiSprite;
    private GameObject chartPanel;
    private TMP_Text chartTitle;
    private RawImage lineChartImage;
    private RawImage heatmapImage;
    private Texture2D lineTexture;
    private Texture2D heatmapTexture;
    private RectTransform lineCursor;
    private RectTransform heatmapCursor;
    private TMP_Text legendText;
    private RectTransform heatmapLabels;
    private readonly List<TMP_Text> heatmapRowTexts = new List<TMP_Text>();
    private Slider timelineSlider;
    private TMP_Text playPauseText;
    private TMP_Text timeText;
    private TMP_Text eventText;
    private TMP_Text modeText;

    private string SourceName =>
        skeletonSource == HomeGazeAnalyzer.SkeletonSource.Filtered ? $"filtered_{filteredMode}{VariantSuffix}" :
        skeletonSource == HomeGazeAnalyzer.SkeletonSource.Image ? $"image_{imageDirection}{VariantSuffix}" :
        $"Kinect{kinectId.Trim()}";

    private string VariantSuffix =>
        string.IsNullOrWhiteSpace(skeletonVariant) ? "" : $"_{skeletonVariant.Trim()}";

    private string OutputName =>
        string.IsNullOrWhiteSpace(outputLabel) ? SourceName : $"{SourceName}_{outputLabel.Trim()}";

    private void Awake()
    {
        env = GetComponent<HomeEnvLoader>();
    }

    private void Start()
    {
        visualRoot = new GameObject("RetrospectivePlayback").transform;
        visualStyle = new HomeSkeletonBodyVisual.Style
        {
            jointPrefab = jointPrefab,
            lineMaterial = lineMaterial,
            jointScale = jointScale,
            lineWidth = lineWidth,
            showHeadDirection = false
        };
        sharedLineMaterial = lineMaterial != null ? lineMaterial : new Material(Shader.Find("Sprites/Default"));

        LoadSamples();
        SetupTargets();
        BuildChildTrack();
        DetectArrivals();
        ComputeFeatures();
        CreateVisuals();
        SetupUI();

        if (playButton != null)
            playButton.onClick.AddListener(Play);

        if (stopButton != null)
            stopButton.onClick.AddListener(Stop);

        if (analyzeAllButton != null)
            analyzeAllButton.onClick.AddListener(Save);

        if (saveButton != null)
            saveButton.onClick.AddListener(Save);

        if (events.Count > 0)
            SelectEvent(0);

        RebuildCharts();
        ShowSample(FindSampleIndex(playbackTime));
        UpdateText();
    }

    private void Update()
    {
        if (Input.GetKeyDown(playStopKey))
            TogglePlay();

        if (isPlaying)
        {
            playbackTime += Time.deltaTime * playbackSpeed;

            if (stopAt >= 0f && playbackTime >= stopAt)
            {
                playbackTime = stopAt;
                isPlaying = false;
                stopAt = -1f;
            }

            if (playbackTime >= duration)
            {
                playbackTime = duration;
                isPlaying = false;
            }
        }

        ShowSample(FindSampleIndex(playbackTime));
        UpdateCursor();
        UpdateTimelineUI();
        UpdateText();
    }

    private void LateUpdate()
    {
        HomeExperimentPaths.ClearUISelection();
    }

    // ------------------------------------------------------------
    // Controls
    // ------------------------------------------------------------
    public void Play()
    {
        if (samples.Count == 0)
            return;

        if (playbackTime >= duration - 0.001f)
            playbackTime = 0f;

        stopAt = -1f;
        isPlaying = true;
    }

    public void Stop()
    {
        isPlaying = false;
        stopAt = -1f;
    }

    public void TogglePlay()
    {
        if (isPlaying)
            Stop();
        else
            Play();
    }

    public void Seek(float time)
    {
        playbackTime = Mathf.Clamp(time, 0f, duration);
        ShowSample(FindSampleIndex(playbackTime));
    }

    // 到達を選び、その windowSeconds 秒前へ移動する
    public void SelectEvent(int index)
    {
        if (events.Count == 0)
            return;

        currentEvent = Mathf.Clamp(index, 0, events.Count - 1);
        Stop();
        Seek(events[currentEvent].arriveTime - windowSeconds);
        RebuildCharts();
    }

    // 選んでいる到達の windowSeconds 秒前から到達まで再生する
    public void Replay()
    {
        if (currentEvent < 0)
            return;

        Seek(events[currentEvent].arriveTime - windowSeconds);
        isPlaying = true;
        stopAt = events[currentEvent].arriveTime;
    }

    public void ToggleMode()
    {
        chartMode = chartMode == ChartMode.Event ? ChartMode.Average : ChartMode.Event;
        RebuildCharts();
    }

    [ContextMenu("Save")]
    public void Save()
    {
        if (!HomeExperimentPaths.IsValidRootDirectory(dataRootDirectory, out _) ||
            !HomeExperimentPaths.IsValidFolderName(env.ExperimentName, out _) ||
            !HomeExperimentPaths.IsValidFolderName(subjectName, out _) || child == null)
        {
            Debug.LogError("[HomeRetrospectiveAnalyzer] データのフォルダ/実験の名前/実験対象者/人物が正しくないため保存できません。");
            return;
        }

        string folder = Path.Combine(HomeExperimentPaths.GetAnalysisDirectory(dataRootDirectory, env.ExperimentName, subjectName), "Retrospective");
        string curvesPath = Path.Combine(folder, $"retrospective_{OutputName}_curves.csv");
        string featuresPath = Path.Combine(folder, $"retrospective_{OutputName}_features.csv");
        string paramsPath = Path.Combine(folder, $"retrospective_{OutputName}_params.json");

        try
        {
            Directory.CreateDirectory(folder);
            WriteCurvesCsv(curvesPath);
            File.WriteAllText(featuresPath, CreateFeaturesCsv(), new UTF8Encoding(true));
            File.WriteAllText(paramsPath, JsonUtility.ToJson(CreateParameters(), true));
            HomeExperimentPaths.RefreshAssetDatabase();
            Debug.Log($"[HomeRetrospectiveAnalyzer] 保存しました:\n  {curvesPath}\n  {featuresPath}\n  {paramsPath}");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[HomeRetrospectiveAnalyzer] 保存に失敗しました: {curvesPath}\n{e}");
        }
    }

    // ------------------------------------------------------------
    // Load skeleton (シーン5/6と同じ)
    // ------------------------------------------------------------
    private void LoadSamples()
    {
        samples.Clear();
        personNames.Clear();

        if (!HomeExperimentPaths.IsValidRootDirectory(dataRootDirectory, out string error) ||
            !HomeExperimentPaths.IsValidFolderName(env.ExperimentName, out error) ||
            !HomeExperimentPaths.IsValidFolderName(subjectName, out error))
        {
            Debug.LogError($"[HomeRetrospectiveAnalyzer] データのフォルダ/実験の名前/実験対象者を正しく指定してください。{error}");
            return;
        }

        if (skeletonSource == HomeGazeAnalyzer.SkeletonSource.Filtered)
            LoadFiltered(filteredMode.ToString());
        else if (skeletonSource == HomeGazeAnalyzer.SkeletonSource.Image)
            LoadFiltered("Image");
        else
            LoadKinect();

        for (int i = 0; i < samples.Count; i++)
        {
            float interval = i + 1 < samples.Count
                ? samples[i + 1].time - samples[i].time
                : (i > 0 ? samples[i].time - samples[i - 1].time : 0f);
            samples[i].duration = Mathf.Clamp(interval, 0f, MaxFrameDuration);

            foreach (Person person in samples[i].persons)
            {
                if (!person.fixedGaze)
                    person.hasGaze = TryGetGaze(person.joints, out person.origin, out person.direction);

                if (person.joints != null && HomeSkeletonIO.TryGetJointPosition(person.joints, JointId.Pelvis, out Vector3 pelvis))
                {
                    person.hasPelvis = true;
                    person.pelvis = pelvis;
                }
            }
        }

        duration = samples.Count > 0 ? samples[samples.Count - 1].time : 0f;
        Debug.Log($"[HomeRetrospectiveAnalyzer] {SourceName}: {samples.Count} samples, {duration:F1}s を読み込みました。");
    }

    private void LoadFiltered(string modeName)
    {
        string path = HomeExperimentPaths.GetGroupedSkeletonPath(dataRootDirectory, env.ExperimentName, subjectName, modeName, skeletonVariant);

        if (!File.Exists(path))
        {
            Debug.LogError($"[HomeRetrospectiveAnalyzer] 人物ごとの骨格のファイルがありません(シーン3で Save grouped を押してください): {path}");
            return;
        }

        HomeFilteredSkeletonList data = JsonUtility.FromJson<HomeFilteredSkeletonList>(File.ReadAllText(path));

        if (data == null || data.frames == null)
            return;

        foreach (HomeFilteredFrame frame in data.frames)
        {
            Sample sample = new Sample { time = frame.timeSec };

            foreach (HomeFilteredPerson person in frame.persons)
            {
                if (!personNames.ContainsKey(person.trackId))
                    personNames[person.trackId] = person.label;

                Person p = new Person { id = person.trackId, joints = person.joints };

                if (skeletonSource == HomeGazeAnalyzer.SkeletonSource.Image)
                {
                    bool useGaze = imageDirection != HomeGazeAnalyzer.ImageDirection.Head && person.hasGazeDirection;
                    bool useHead = imageDirection != HomeGazeAnalyzer.ImageDirection.GazeOnly && person.hasHeadDirection;

                    p.fixedGaze = true;
                    p.origin = person.headPosition;
                    p.hasGaze = useGaze || useHead;
                    p.direction = useGaze ? person.gazeDirection.normalized : person.headDirection.normalized;
                }

                sample.persons.Add(p);
            }

            samples.Add(sample);
        }
    }

    private void LoadKinect()
    {
        foreach (HomeSkeletonTrack track in HomeSkeletonIO.LoadSubject(dataRootDirectory, env.ExperimentName, subjectName, "HomeRetrospectiveAnalyzer"))
        {
            if (track.kinectId != kinectId.Trim())
                continue;

            for (int i = 0; i < track.frames.Count; i++)
            {
                Sample sample = new Sample { time = track.times[i] };

                if (track.frames[i].bodies != null)
                {
                    foreach (HomeSkeletonBody body in track.frames[i].bodies)
                        sample.persons.Add(new Person { id = (int)body.bodyId, joints = body.joints });
                }

                samples.Add(sample);
            }

            return;
        }

        Debug.LogError($"[HomeRetrospectiveAnalyzer] Kinect{kinectId} の骨格データがありません。");
    }

    // HomeGazeAnalyzer.TryGetGaze と同じ視線方向の作り方
    private bool TryGetGaze(List<HomeSkeletonJoint> joints, out Vector3 head, out Vector3 direction)
    {
        direction = Vector3.zero;

        if (!HomeSkeletonIO.TryGetJointPosition(joints, JointId.Head, out head) ||
            !HomeSkeletonIO.TryGetJointPosition(joints, JointId.Nose, out Vector3 nose))
            return false;

        Vector3 from = head;

        if (headDirectionMethod == HomeGazeAnalyzer.HeadDirectionMethod.EarsToNose &&
            HomeSkeletonIO.TryGetJointPosition(joints, JointId.EarLeft, out Vector3 earLeft) &&
            HomeSkeletonIO.TryGetJointPosition(joints, JointId.EarRight, out Vector3 earRight))
        {
            from = (earLeft + earRight) * 0.5f;
        }

        Vector3 rawDir = nose - from;

        if (rawDir.sqrMagnitude < 0.0001f)
            return false;

        rawDir.Normalize();

        Vector3 rightAxis = Vector3.Cross(Vector3.up, rawDir).normalized;

        if (rightAxis.sqrMagnitude < 0.0001f)
            rightAxis = Vector3.right;

        direction = (Quaternion.AngleAxis(downwardAngle, rightAxis) * rawDir).normalized;
        return true;
    }

    private string GetPersonName(int id) =>
        personNames.TryGetValue(id, out string name) && !string.IsNullOrEmpty(name) ? name : $"person_{id}";

    // ------------------------------------------------------------
    // Targets(頂点を囲む箱)
    // ------------------------------------------------------------
    private void SetupTargets()
    {
        targets.Clear();

        if (useEnvTargetObjects)
        {
            List<string> groups = new List<string>();
            Dictionary<string, List<GameObject>> objectsByGroup = new Dictionary<string, List<GameObject>>();

            foreach (GameObject obj in env.TargetObjects)
            {
                HomeTargetObject marker = obj != null ? obj.GetComponent<HomeTargetObject>() : null;

                if (marker == null)
                    continue;

                if (!objectsByGroup.TryGetValue(marker.group, out List<GameObject> objects))
                {
                    objects = new List<GameObject>();
                    objectsByGroup[marker.group] = objects;
                    groups.Add(marker.group);
                }

                objects.Add(obj);
            }

            foreach (string group in groups)
                AddTarget(group, objectsByGroup[group]);
        }

        if (targetNames != null)
        {
            foreach (string targetName in targetNames)
            {
                if (string.IsNullOrWhiteSpace(targetName) || targets.Exists(t => t.name == targetName.Trim()))
                    continue;

                List<GameObject> found = FindRoomObjects(targetName.Trim());

                if (found.Count > 0)
                    AddTarget(targetName.Trim(), found);
                else
                    Debug.LogWarning($"[HomeRetrospectiveAnalyzer] 対象が部屋オブジェクト/対象物体にありません: {targetName}");
            }
        }

        if (targetObjects != null)
        {
            foreach (GameObject target in targetObjects)
            {
                if (target != null && !targets.Exists(t => t.name == target.name))
                    AddTarget(target.name, new List<GameObject> { target });
            }
        }

        Debug.Log($"[HomeRetrospectiveAnalyzer] 対象物体: {string.Join(", ", targets.ConvertAll(t => t.name))}");
    }

    private void AddTarget(string targetName, List<GameObject> objects)
    {
        Bounds bounds = new Bounds();
        bool hasBounds = false;

        foreach (GameObject obj in objects)
        {
            foreach (MeshFilter meshFilter in obj.GetComponentsInChildren<MeshFilter>())
            {
                if (!EnsureReadable(meshFilter))
                    continue;

                foreach (Vector3 local in meshFilter.sharedMesh.vertices)
                {
                    Vector3 position = meshFilter.transform.TransformPoint(local);

                    if (!hasBounds)
                    {
                        bounds = new Bounds(position, Vector3.zero);
                        hasBounds = true;
                    }
                    else
                    {
                        bounds.Encapsulate(position);
                    }
                }
            }
        }

        if (!hasBounds)
        {
            Debug.LogWarning($"[HomeRetrospectiveAnalyzer] 対象に頂点を読めるメッシュがありません: {targetName}");
            return;
        }

        // 箱の上の 3×3×3 の点(角・辺の中点・面の中心・中心)。頭部方向とのなす角のいちばん小さい所を探すのに使う
        Vector3[] points = new Vector3[27];
        int n = 0;

        for (int x = -1; x <= 1; x++)
        for (int y = -1; y <= 1; y++)
        for (int z = -1; z <= 1; z++)
            points[n++] = bounds.center + Vector3.Scale(bounds.extents, new Vector3(x, y, z));

        targets.Add(new Target { name = targetName, bounds = bounds, points = points });
    }

    private IEnumerable<GameObject> GetEnvObjects()
    {
        foreach (GameObject obj in env.RoomObjects)
            yield return obj;

        foreach (GameObject obj in env.TargetObjects)
            yield return obj;
    }

    private List<GameObject> FindRoomObjects(string targetName)
    {
        List<GameObject> found = new List<GameObject>();

        foreach (GameObject roomObject in GetEnvObjects())
        {
            if (roomObject == null)
                continue;

            foreach (Transform child in roomObject.GetComponentsInChildren<Transform>(true))
            {
                if (child.name == targetName)
                    found.Add(child.gameObject);
            }
        }

        return found;
    }

    private static bool EnsureReadable(MeshFilter meshFilter)
    {
        Mesh mesh = meshFilter.sharedMesh;

        if (mesh == null)
            return false;

        if (mesh.isReadable)
            return true;

#if UNITY_EDITOR
        string path = UnityEditor.AssetDatabase.GetAssetPath(mesh);
        string meshName = mesh.name;

        if (UnityEditor.AssetImporter.GetAtPath(path) is UnityEditor.ModelImporter importer)
        {
            if (!importer.isReadable)
            {
                importer.isReadable = true;
                importer.SaveAndReimport();
            }

            if (meshFilter.sharedMesh == null || !meshFilter.sharedMesh.isReadable)
            {
                foreach (Object asset in UnityEditor.AssetDatabase.LoadAllAssetsAtPath(path))
                {
                    if (asset is Mesh reloaded && reloaded.name == meshName)
                    {
                        meshFilter.sharedMesh = reloaded;
                        break;
                    }
                }
            }
        }

        return meshFilter.sharedMesh != null && meshFilter.sharedMesh.isReadable;
#else
        return false;
#endif
    }

    // ------------------------------------------------------------
    // 子供の時系列と注視可能性スコア
    // ------------------------------------------------------------
    private void BuildChildTrack()
    {
        List<Person> persons = new List<Person>();
        List<int> indices = new List<int>();

        for (int i = 0; i < samples.Count; i++)
        {
            foreach (Person p in samples[i].persons)
            {
                if (GetPersonName(p.id) != childName)
                    continue;

                persons.Add(p);
                indices.Add(i);
                break;
            }
        }

        if (persons.Count == 0)
        {
            Debug.LogError($"[HomeRetrospectiveAnalyzer] 子供({childName})の骨格がありません。" +
                           $"Child Name をシーン3のグループ名に合わせてください(あるもの: {string.Join(", ", new HashSet<string>(personNames.Values))})");
            return;
        }

        child = new ChildTrack(persons.Count, targets.Count);

        for (int i = 0; i < persons.Count; i++)
        {
            Person p = persons[i];
            child.time[i] = samples[indices[i]].time;
            child.duration[i] = samples[indices[i]].duration;
            child.sampleIndex[i] = indices[i];
            child.hasGaze[i] = p.hasGaze;
            child.origin[i] = p.origin;
            child.direction[i] = p.direction;
            child.pelvisX[i] = p.hasPelvis ? p.pelvis.x : float.NaN;
            child.pelvisZ[i] = p.hasPelvis ? p.pelvis.z : float.NaN;

            for (int k = 0; k < targets.Count; k++)
            {
                child.score[k][i] = p.hasGaze ? GazeScore(p.origin, p.direction, targets[k]) : float.NaN;
                child.distance[k][i] = p.hasPelvis ? HorizontalDistance(targets[k].bounds, p.pelvis) : float.NaN;
            }
        }

        Debug.Log($"[HomeRetrospectiveAnalyzer] 子供 {childName}: {child.Count} frames");
    }

    // 注視可能性スコア: 頭部方向と物(箱)のいちばん近い所とのなす角 θ から exp(−θ²/(2σ²))。頭部方向の線が箱に当たれば θ = 0
    private float GazeScore(Vector3 origin, Vector3 direction, Target target)
    {
        float theta = 0f;

        if (!target.bounds.Contains(origin) && !RayHitsBox(origin, direction, target.bounds))
        {
            float bestDot = -1f;

            foreach (Vector3 point in target.points)
            {
                Vector3 to = point - origin;
                float length = to.magnitude;

                if (length > 1e-4f)
                    bestDot = Mathf.Max(bestDot, Vector3.Dot(direction, to / length));
            }

            theta = Mathf.Acos(Mathf.Clamp(bestDot, -1f, 1f)) * Mathf.Rad2Deg;
        }

        float sigma = Mathf.Max(uncertaintySigma, 0.01f);
        float score = Mathf.Exp(-theta * theta / (2f * sigma * sigma));

        if (distanceDecay > 0f)
            score *= Mathf.Exp(-Vector3.Distance(origin, target.bounds.ClosestPoint(origin)) / distanceDecay);

        return score;
    }

    private static bool RayHitsBox(Vector3 origin, Vector3 direction, Bounds bounds)
    {
        return bounds.IntersectRay(new Ray(origin, direction), out float distance) && distance >= 0f;
    }

    private static float HorizontalDistance(Bounds bounds, Vector3 position)
    {
        float dx = Mathf.Max(bounds.min.x - position.x, 0f, position.x - bounds.max.x);
        float dz = Mathf.Max(bounds.min.z - position.z, 0f, position.z - bounds.max.z);
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    // ------------------------------------------------------------
    // 到達(シーン6の接近と同じ)
    // ------------------------------------------------------------
    private void DetectArrivals()
    {
        events.Clear();

        if (child == null)
            return;

        for (int k = 0; k < targets.Count; k++)
        {
            bool outside = false;
            bool inside = false;
            float last = float.NegativeInfinity;

            for (int i = 0; i < child.Count; i++)
            {
                if (child.time[i] - last > trackGapSeconds)
                {
                    outside = false;
                    inside = false;
                }

                last = child.time[i];
                float d = child.distance[k][i];

                if (float.IsNaN(d))
                    continue;

                if (d > leaveDistance)
                {
                    outside = true;
                    inside = false;
                }
                else if (d <= approachDistance && outside && !inside)
                {
                    events.Add(new ArrivalEvent { target = k, arriveTime = child.time[i], arriveIndex = i });
                    inside = true;
                    outside = false;
                }
            }
        }

        events.Sort((a, b) => a.arriveTime.CompareTo(b.arriveTime));
        Debug.Log($"[HomeRetrospectiveAnalyzer] 子供の到達: {events.Count} 回");
    }

    // ------------------------------------------------------------
    // 特徴量(到達ごと × 物ごと)
    // ------------------------------------------------------------
    private void ComputeFeatures()
    {
        foreach (ArrivalEvent e in events)
        {
            e.features = new ObjectFeatures[targets.Count];

            for (int k = 0; k < targets.Count; k++)
                e.features[k] = ComputeObjectFeatures(e, k);

            // 累積注視可能性が最も大きい物が、到達した物か
            int best = -1;

            for (int k = 0; k < targets.Count; k++)
            {
                if (!float.IsNaN(e.features[k].cumulative) && (best < 0 || e.features[k].cumulative > e.features[best].cumulative))
                    best = k;
            }

            e.arrivedRankedFirst = best == e.target;
        }
    }

    private ObjectFeatures ComputeObjectFeatures(ArrivalEvent e, int k)
    {
        ObjectFeatures f = new ObjectFeatures();
        float start = e.arriveTime - windowSeconds;
        double cumulative = 0.0, sum = 0.0, earlySum = 0.0, lateSum = 0.0;
        int count = 0, earlyCount = 0, lateCount = 0;
        bool high = false;
        float runStart = 0f;
        float firstHigh = float.NaN;
        float lastHigh = float.NaN;

        for (int i = LowerBound(child.time, start); i <= e.arriveIndex && i < child.Count; i++)
        {
            float s = child.score[k][i];
            float t = child.time[i];

            if (float.IsNaN(s))
            {
                // 頭部方向が求まらないフレームでは「高い状態」を切らない(つながりは続ける)
                continue;
            }

            cumulative += s * child.duration[i];
            sum += s;
            count++;
            f.max = Mathf.Max(f.max, s);

            if (t < start + windowSeconds * 0.5f)
            {
                earlySum += s;
                earlyCount++;
            }

            if (t >= e.arriveTime - lateSeconds)
            {
                lateSum += s;
                lateCount++;
            }

            bool isHigh = s >= highThreshold;

            if (isHigh && !high)
            {
                f.highEpisodes++;
                runStart = t;

                if (float.IsNaN(firstHigh))
                    firstHigh = t;
            }

            if (isHigh)
            {
                lastHigh = t;
                f.longestHigh = Mathf.Max(f.longestHigh, t - runStart + child.duration[i]);
            }

            high = isHigh;
        }

        f.frames = count;
        f.cumulative = count > 0 ? (float)cumulative : float.NaN;
        f.mean = count > 0 ? (float)(sum / count) : float.NaN;
        f.earlyMean = earlyCount > 0 ? (float)(earlySum / earlyCount) : float.NaN;
        f.lateMean = lateCount > 0 ? (float)(lateSum / lateCount) : float.NaN;
        f.firstHighLead = float.IsNaN(firstHigh) ? float.NaN : e.arriveTime - firstHigh;
        f.lastHighLead = float.IsNaN(lastHigh) ? float.NaN : e.arriveTime - lastHigh;
        f.revisits = Mathf.Max(0, f.highEpisodes - 1);
        return f;
    }

    // 到達 e の、物 k の時刻 relTime(到達からの秒、負)での値(tolerance 秒以内のフレームが無ければ NaN)
    private float ScoreAt(ArrivalEvent e, int k, float relTime, float tolerance)
    {
        int i = Nearest(child.time, e.arriveTime + relTime);
        return i >= 0 && Mathf.Abs(child.time[i] - (e.arriveTime + relTime)) <= tolerance ? child.score[k][i] : float.NaN;
    }

    // 平均のグラフ: 到達した物 / 到達しなかった物(到達ごとに平均してから、全部の到達で平均)
    private void ComputeAverageCurves(out float[] times, out float[] arrived, out float[] others)
    {
        int steps = Mathf.RoundToInt(windowSeconds / AverageStep);
        times = new float[steps + 1];
        arrived = new float[steps + 1];
        others = new float[steps + 1];

        for (int s = 0; s <= steps; s++)
        {
            float rel = -windowSeconds + s * AverageStep;
            times[s] = rel;
            double sumA = 0.0, sumO = 0.0;
            int countA = 0, countO = 0;

            foreach (ArrivalEvent e in events)
            {
                float a = ScoreAt(e, e.target, rel, AverageStep);

                if (!float.IsNaN(a))
                {
                    sumA += a;
                    countA++;
                }

                double o = 0.0;
                int n = 0;

                for (int k = 0; k < targets.Count; k++)
                {
                    if (k == e.target)
                        continue;

                    float v = ScoreAt(e, k, rel, AverageStep);

                    if (!float.IsNaN(v))
                    {
                        o += v;
                        n++;
                    }
                }

                if (n > 0)
                {
                    sumO += o / n;
                    countO++;
                }
            }

            arrived[s] = countA > 0 ? (float)(sumA / countA) : float.NaN;
            others[s] = countO > 0 ? (float)(sumO / countO) : float.NaN;
        }
    }

    // ------------------------------------------------------------
    // Output
    // ------------------------------------------------------------
    private void WriteCurvesCsv(string path)
    {
        using (StreamWriter writer = new StreamWriter(path, false, new UTF8Encoding(true)))
        {
            writer.WriteLine("event,arriveSec,arrivedTarget,target,arrived,relTime,score,childDistance");

            for (int j = 0; j < events.Count; j++)
            {
                ArrivalEvent e = events[j];

                for (int i = LowerBound(child.time, e.arriveTime - windowSeconds); i <= e.arriveIndex && i < child.Count; i++)
                {
                    for (int k = 0; k < targets.Count; k++)
                    {
                        writer.WriteLine(string.Join(",", (j + 1).ToString(CultureInfo.InvariantCulture), F(e.arriveTime), Csv(targets[e.target].name),
                            Csv(targets[k].name), k == e.target ? "1" : "0", F(child.time[i] - e.arriveTime), F(child.score[k][i]), F(child.distance[k][i])));
                    }
                }
            }
        }
    }

    private string CreateFeaturesCsv()
    {
        StringBuilder csv = new StringBuilder();
        csv.AppendLine("event,arriveSec,arrivedTarget,target,arrived,frames,cumulative,mean,max,earlyMean,lateMean," +
                       "highEpisodes,revisits,firstHighLeadSec,lastHighLeadSec,longestHighSec,arrivedRankedFirst");

        for (int j = 0; j < events.Count; j++)
        {
            ArrivalEvent e = events[j];

            for (int k = 0; k < targets.Count; k++)
            {
                ObjectFeatures f = e.features[k];
                csv.AppendLine(string.Join(",", (j + 1).ToString(CultureInfo.InvariantCulture), F(e.arriveTime), Csv(targets[e.target].name),
                    Csv(targets[k].name), k == e.target ? "1" : "0", f.frames.ToString(CultureInfo.InvariantCulture),
                    F(f.cumulative), F(f.mean), F(f.max), F(f.earlyMean), F(f.lateMean),
                    f.highEpisodes.ToString(CultureInfo.InvariantCulture), f.revisits.ToString(CultureInfo.InvariantCulture),
                    F(f.firstHighLead), F(f.lastHighLead), F(f.longestHigh), e.arrivedRankedFirst ? "1" : "0"));
            }
        }

        return csv.ToString();
    }

    private HomeRetrospectiveParameters CreateParameters()
    {
        HomeRetrospectiveParameters data = new HomeRetrospectiveParameters
        {
            experimentName = env.ExperimentName,
            subjectName = subjectName,
            createdAt = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            skeletonSource = SourceName,
            childName = childName,
            headDirectionMethod = headDirectionMethod.ToString(),
            downwardAngle = downwardAngle,
            uncertaintySigma = uncertaintySigma,
            distanceDecay = distanceDecay,
            approachDistance = approachDistance,
            leaveDistance = leaveDistance,
            trackGapSeconds = trackGapSeconds,
            windowSeconds = windowSeconds,
            highThreshold = highThreshold,
            lateSeconds = lateSeconds
        };

        foreach (Target target in targets)
            data.targets.Add(target.name);

        return data;
    }

    private static string F(float value) =>
        float.IsNaN(value) || float.IsInfinity(value) ? "" : value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string Csv(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        return value.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }

    // ------------------------------------------------------------
    // 3D
    // ------------------------------------------------------------
    private void CreateVisuals()
    {
        gazeLine = CreateLine("ChildGaze", Color.white, 0.015f);
        coneRing = CreateLine("ChildConeRing", new Color(1f, 0.85f, 0.1f, 0.9f), 0.006f);
        coneRing.loop = true;
        coneRing.positionCount = ConeSegments;

        for (int i = 0; i < 4; i++)
            coneEdges.Add(CreateLine("ChildConeEdge", new Color(1f, 0.85f, 0.1f, 0.6f), 0.004f));

        foreach (Target target in targets)
        {
            LineRenderer[] lines = new LineRenderer[12];

            for (int i = 0; i < 12; i++)
                lines[i] = CreateLine($"{target.name}_Box", Color.gray, 0.012f);

            Vector3 min = target.bounds.min, max = target.bounds.max;
            Vector3[] c =
            {
                new Vector3(min.x, min.y, min.z), new Vector3(max.x, min.y, min.z), new Vector3(max.x, min.y, max.z), new Vector3(min.x, min.y, max.z),
                new Vector3(min.x, max.y, min.z), new Vector3(max.x, max.y, min.z), new Vector3(max.x, max.y, max.z), new Vector3(min.x, max.y, max.z)
            };
            int[,] edges = { { 0, 1 }, { 1, 2 }, { 2, 3 }, { 3, 0 }, { 4, 5 }, { 5, 6 }, { 6, 7 }, { 7, 4 }, { 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 } };

            for (int i = 0; i < 12; i++)
            {
                lines[i].SetPosition(0, c[edges[i, 0]]);
                lines[i].SetPosition(1, c[edges[i, 1]]);
                lines[i].enabled = true;
            }

            boxLines.Add(lines);
        }
    }

    private void ShowSample(int index)
    {
        if (index == shownSample)
            return;

        shownSample = index;
        HashSet<int> shown = new HashSet<int>();

        if (index >= 0 && index < samples.Count)
        {
            foreach (Person person in samples[index].persons)
            {
                ShowPerson(person);
                shown.Add(person.id);
            }
        }

        foreach (KeyValuePair<int, HomeSkeletonBodyVisual> pair in bodies)
        {
            if (!shown.Contains(pair.Key))
                pair.Value.SetVisible(false);
        }

        // 子供の頭部方向・誤差の円錐と、物ごとの注視可能性(箱の色)
        int i = child != null ? child.IndexOfSample(index) : -1;
        bool hasGaze = i >= 0 && child.hasGaze[i];
        gazeLine.enabled = hasGaze;
        coneRing.enabled = hasGaze;

        foreach (LineRenderer edge in coneEdges)
            edge.enabled = hasGaze;

        if (hasGaze)
            ShowCone(child.origin[i], child.direction[i]);

        for (int k = 0; k < targets.Count; k++)
        {
            float score = i >= 0 ? child.score[k][i] : float.NaN;
            bool arrivedTarget = currentEvent >= 0 && chartMode == ChartMode.Event && events[currentEvent].target == k;
            Color color = float.IsNaN(score) ? new Color(0.5f, 0.5f, 0.5f) : HeatColor(score);
            float width = arrivedTarget ? 0.025f : 0.012f;

            foreach (LineRenderer line in boxLines[k])
            {
                line.startColor = color;
                line.endColor = color;
                line.startWidth = width;
                line.endWidth = width;
            }
        }
    }

    // 頭部方向の線と、σ の円錐(先の輪と4本の辺)
    private void ShowCone(Vector3 origin, Vector3 direction)
    {
        gazeLine.SetPosition(0, origin);
        gazeLine.SetPosition(1, origin + direction * coneLength);

        Vector3 up = Mathf.Abs(Vector3.Dot(direction, Vector3.up)) > 0.99f ? Vector3.forward : Vector3.up;
        Vector3 right = Vector3.Cross(up, direction).normalized;
        Vector3 ortho = Vector3.Cross(direction, right).normalized;
        float radius = Mathf.Tan(uncertaintySigma * Mathf.Deg2Rad) * coneLength;
        Vector3 center = origin + direction * coneLength;

        for (int s = 0; s < ConeSegments; s++)
        {
            float a = s * 2f * Mathf.PI / ConeSegments;
            coneRing.SetPosition(s, center + (right * Mathf.Cos(a) + ortho * Mathf.Sin(a)) * radius);
        }

        for (int s = 0; s < coneEdges.Count; s++)
        {
            float a = s * 0.5f * Mathf.PI;
            coneEdges[s].SetPosition(0, origin);
            coneEdges[s].SetPosition(1, center + (right * Mathf.Cos(a) + ortho * Mathf.Sin(a)) * radius);
        }
    }

    private void ShowPerson(Person person)
    {
        bool isChild = GetPersonName(person.id) == childName;
        Color color = personColors.Length > 0 ? personColors[Mathf.Abs(person.id) % personColors.Length] : Color.white;

        if (!isChild)
            color = Color.Lerp(color, new Color(0.5f, 0.5f, 0.5f), 0.6f);

        if (!bodies.TryGetValue(person.id, out HomeSkeletonBodyVisual body))
        {
            body = new HomeSkeletonBodyVisual(visualRoot, $"person_{person.id}", color, visualStyle);
            bodies[person.id] = body;
        }

        body.Apply(person.joints);
    }

    private LineRenderer CreateLine(string name, Color color, float width)
    {
        LineRenderer line = new GameObject(name).AddComponent<LineRenderer>();
        line.transform.SetParent(visualRoot, false);
        line.sharedMaterial = sharedLineMaterial;
        line.startColor = color;
        line.endColor = color;
        line.startWidth = width;
        line.endWidth = width;
        line.positionCount = 2;
        line.enabled = false;
        return line;
    }

    // 0 = 青、0.5 = 白、1 = 赤(図のヒートマップと同じ)
    private static Color HeatColor(float v)
    {
        v = Mathf.Clamp01(v);
        Color blue = new Color(0.2f, 0.45f, 0.95f);
        Color white = new Color(0.97f, 0.97f, 0.97f);
        Color red = new Color(0.9f, 0.15f, 0.2f);
        return v < 0.5f ? Color.Lerp(blue, white, v * 2f) : Color.Lerp(white, red, (v - 0.5f) * 2f);
    }

    private int FindSampleIndex(float time)
    {
        int low = 0, high = samples.Count - 1, result = -1;

        while (low <= high)
        {
            int mid = (low + high) / 2;

            if (samples[mid].time <= time)
            {
                result = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return result;
    }

    // ------------------------------------------------------------
    // グラフ(折れ線とヒートマップを Texture2D に描く)
    // ------------------------------------------------------------
    private void RebuildCharts()
    {
        // 到達した物の箱の太さを描き直す
        shownSample = -2;

        if (lineTexture == null)
            return;

        List<string> rowNames = new List<string>();
        List<Color> rowColors = new List<Color>();
        List<float[]> rowTimes = new List<float[]>();
        List<float[]> rowValues = new List<float[]>();
        List<bool> rowBold = new List<bool>();
        StringBuilder legend = new StringBuilder();

        if (chartMode == ChartMode.Event && currentEvent >= 0)
        {
            ArrivalEvent e = events[currentEvent];
            chartTitle.text = $"Gaze possibility before arrival #{currentEvent + 1}/{events.Count}: {targets[e.target].name} at {e.arriveTime:F1} s";
            int from = LowerBound(child.time, e.arriveTime - windowSeconds);
            int count = Mathf.Max(0, e.arriveIndex - from + 1);

            for (int k = 0; k < targets.Count; k++)
            {
                float[] t = new float[count];
                float[] v = new float[count];

                for (int j = 0; j < count; j++)
                {
                    t[j] = child.time[from + j] - e.arriveTime;
                    v[j] = child.score[k][from + j];
                }

                bool arrived = k == e.target;
                rowNames.Add($"{targets[k].name}{(arrived ? " (arrived)" : "")}");
                rowColors.Add(ObjectColor(k));
                rowTimes.Add(t);
                rowValues.Add(v);
                rowBold.Add(arrived);
            }
        }
        else if (chartMode == ChartMode.Average && events.Count > 0)
        {
            ComputeAverageCurves(out float[] times, out float[] arrived, out float[] others);
            chartTitle.text = $"Average over {events.Count} arrivals: arrived object vs other objects";
            rowNames.Add("Arrived object");
            rowColors.Add(new Color(0.85f, 0.15f, 0.2f));
            rowTimes.Add(times);
            rowValues.Add(arrived);
            rowBold.Add(true);
            rowNames.Add("Other objects (mean)");
            rowColors.Add(new Color(0.15f, 0.45f, 0.9f));
            rowTimes.Add(times);
            rowValues.Add(others);
            rowBold.Add(false);
        }
        else
        {
            chartTitle.text = "No arrivals";
        }

        DrawLineChart(rowColors, rowTimes, rowValues, rowBold);
        DrawHeatmap(rowNames, rowTimes, rowValues);

        for (int r = 0; r < rowNames.Count; r++)
        {
            Color c = rowColors[r];
            legend.Append($"<color=#{ColorUtility.ToHtmlStringRGB(c)}>{(rowBold[r] ? "<b>" : "")}— {rowNames[r]}{(rowBold[r] ? "</b>" : "")}</color>   ");
        }

        legendText.text = legend.ToString();
    }

    private Color ObjectColor(int k) => objectColors.Length > 0 ? objectColors[k % objectColors.Length] : Color.black;

    private void DrawLineChart(List<Color> colors, List<float[]> times, List<float[]> values, List<bool> bold)
    {
        Color32[] pixels = new Color32[ChartWidth * ChartHeight];
        Color32 background = new Color32(255, 255, 255, 255);
        Color32 grid = new Color32(225, 225, 225, 255);

        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = background;

        // 横の線(0, 0.25, 0.5, 0.75, 1)と縦の線(2 秒ごと)
        for (int g = 0; g <= 4; g++)
        {
            int y = Mathf.RoundToInt(g / 4f * (ChartHeight - 1));

            for (int x = 0; x < ChartWidth; x++)
                pixels[y * ChartWidth + x] = grid;
        }

        for (float t = 0f; t >= -windowSeconds - 0.001f; t -= 2f)
        {
            int x = ChartX(t);

            for (int y = 0; y < ChartHeight; y++)
                pixels[y * ChartWidth + x] = grid;
        }

        // 細い線を先に、到達した物(太線)を後に描く
        for (int pass = 0; pass < 2; pass++)
        {
            for (int r = 0; r < values.Count; r++)
            {
                if (bold[r] != (pass == 1))
                    continue;

                Color32 color = colors[r];
                int thickness = bold[r] ? 2 : 1;
                int prevX = -1, prevY = -1;

                for (int j = 0; j < values[r].Length; j++)
                {
                    if (float.IsNaN(values[r][j]))
                    {
                        prevX = -1;
                        continue;
                    }

                    int x = ChartX(times[r][j]);
                    int y = ChartY(values[r][j]);

                    if (prevX >= 0)
                        DrawSegment(pixels, prevX, prevY, x, y, thickness, color);

                    prevX = x;
                    prevY = y;
                }

                // 到達した物の到達時刻に ●
                if (bold[r] && prevX >= 0)
                    DrawDot(pixels, prevX, prevY, 5, color);
            }
        }

        lineTexture.SetPixels32(pixels);
        lineTexture.Apply();
    }

    private int ChartX(float relTime) => Mathf.Clamp(Mathf.RoundToInt((relTime + windowSeconds) / windowSeconds * (ChartWidth - 1)), 0, ChartWidth - 1);

    private static int ChartY(float value) => Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(value) * (ChartHeight - 1)), 0, ChartHeight - 1);

    private static void DrawSegment(Color32[] pixels, int x0, int y0, int x1, int y1, int thickness, Color32 color)
    {
        int steps = Mathf.Max(Mathf.Abs(x1 - x0), Mathf.Abs(y1 - y0), 1);

        for (int s = 0; s <= steps; s++)
        {
            int x = Mathf.RoundToInt(Mathf.Lerp(x0, x1, s / (float)steps));
            int y = Mathf.RoundToInt(Mathf.Lerp(y0, y1, s / (float)steps));
            DrawDot(pixels, x, y, thickness, color);
        }
    }

    private static void DrawDot(Color32[] pixels, int cx, int cy, int radius, Color32 color)
    {
        for (int dy = -radius; dy <= radius; dy++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                if (dx * dx + dy * dy > radius * radius + radius)
                    continue;

                int x = cx + dx, y = cy + dy;

                if (x >= 0 && x < ChartWidth && y >= 0 && y < ChartHeight)
                    pixels[y * ChartWidth + x] = color;
            }
        }
    }

    // 行 = 物(平均モードでは 到達した物 / それ以外)、列 = 時間の区切り。区切りの中の平均スコアの色
    private void DrawHeatmap(List<string> names, List<float[]> times, List<float[]> values)
    {
        int rows = Mathf.Max(1, names.Count);
        int bins = Mathf.Max(2, heatmapBins);

        if (heatmapTexture == null || heatmapTexture.width != bins || heatmapTexture.height != rows)
        {
            heatmapTexture = new Texture2D(bins, rows, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            heatmapImage.texture = heatmapTexture;
        }

        Color[] pixels = new Color[bins * rows];

        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = new Color(0.85f, 0.85f, 0.85f);

        for (int r = 0; r < names.Count; r++)
        {
            double[] sum = new double[bins];
            int[] count = new int[bins];

            for (int j = 0; j < values[r].Length; j++)
            {
                if (float.IsNaN(values[r][j]))
                    continue;

                int b = Mathf.Clamp(Mathf.FloorToInt((times[r][j] + windowSeconds) / windowSeconds * bins), 0, bins - 1);
                sum[b] += values[r][j];
                count[b]++;
            }

            // 上の行から 1 行目(テクスチャは下が 0 なので逆にする)
            int y = rows - 1 - r;

            for (int b = 0; b < bins; b++)
            {
                if (count[b] > 0)
                    pixels[y * bins + b] = HeatColor((float)(sum[b] / count[b]));
            }
        }

        heatmapTexture.SetPixels(pixels);
        heatmapTexture.Apply();

        // 行の名前
        for (int r = 0; r < heatmapRowTexts.Count; r++)
            heatmapRowTexts[r].gameObject.SetActive(r < names.Count);

        for (int r = 0; r < names.Count; r++)
        {
            if (r >= heatmapRowTexts.Count)
                heatmapRowTexts.Add(CreateText("Row", heatmapLabels, TextAlignmentOptions.MidlineRight));

            TMP_Text text = heatmapRowTexts[r];
            text.gameObject.SetActive(true);
            text.text = names[r];
            text.fontSize = 14f;
            RectTransform rect = text.rectTransform;
            rect.anchorMin = new Vector2(0f, 1f - (r + 1f) / names.Count);
            rect.anchorMax = new Vector2(1f, 1f - (float)r / names.Count);
            rect.offsetMin = rect.offsetMax = Vector2.zero;
        }
    }

    // 再生位置(到達からの秒)の縦線
    private void UpdateCursor()
    {
        if (lineCursor == null)
            return;

        bool visible = chartMode == ChartMode.Event && currentEvent >= 0;
        float rel = visible ? playbackTime - events[currentEvent].arriveTime : 0f;
        visible &= rel >= -windowSeconds && rel <= 0.001f;
        lineCursor.gameObject.SetActive(visible);
        heatmapCursor.gameObject.SetActive(visible);

        if (!visible)
            return;

        float x = Mathf.Clamp01((rel + windowSeconds) / windowSeconds);

        foreach (RectTransform cursor in new[] { lineCursor, heatmapCursor })
        {
            cursor.anchorMin = new Vector2(x, 0f);
            cursor.anchorMax = new Vector2(x, 1f);
            cursor.anchoredPosition = Vector2.zero;
        }
    }

    // ------------------------------------------------------------
    // Text
    // ------------------------------------------------------------
    private void UpdateText()
    {
        if (frameText != null)
        {
            frameText.text = $"{subjectName} ({OutputName})  Time : {playbackTime:F1} / {duration:F1} s\n" +
                             $"child {childName}  sigma {uncertaintySigma:F1}°  window {windowSeconds:F0} s  {events.Count} arrivals";
        }

        if (scoreText == null)
            return;

        StringBuilder text = new StringBuilder();

        if (chartMode == ChartMode.Event && currentEvent >= 0)
            AppendEventFeatures(text, events[currentEvent]);
        else
            AppendSummary(text);

        scoreText.text = text.ToString();
    }

    // 到達1回の、物ごとの特徴量
    private void AppendEventFeatures(StringBuilder text, ArrivalEvent e)
    {
        text.AppendLine($"<b>Arrival #{currentEvent + 1}</b> {targets[e.target].name} at {e.arriveTime:F1} s");
        text.AppendLine($"past {windowSeconds:F0} s, high = score >= {highThreshold:F2}");
        text.AppendLine();

        for (int k = 0; k < targets.Count; k++)
        {
            ObjectFeatures f = e.features[k];
            string name = k == e.target ? $"<b>{targets[k].name} (arrived)</b>" : targets[k].name;
            text.AppendLine($"<color=#{ColorUtility.ToHtmlStringRGB(ObjectColor(k))}>{name}</color>");
            text.AppendLine($"  cumulative {Num(f.cumulative, "F2")} s   mean {Num(f.mean, "F2")}   max {Num(f.max, "F2")}");
            text.AppendLine($"  early {Num(f.earlyMean, "F2")}   last {lateSeconds:F0} s {Num(f.lateMean, "F2")}");
            text.AppendLine($"  high episodes {f.highEpisodes} (revisits {f.revisits})   longest {Num(f.longestHigh, "F1")} s");
            text.AppendLine($"  last high {Num(f.lastHighLead, "F1")} s before   first high {Num(f.firstHighLead, "F1")} s before");
        }

        text.AppendLine();
        text.AppendLine(e.arrivedRankedFirst
            ? "Arrived object has the highest cumulative score."
            : "Another object has a higher cumulative score.");
    }

    // 全部の到達のまとめ: 到達した物の特徴量は、他の物より大きいか
    private void AppendSummary(StringBuilder text)
    {
        text.AppendLine($"<b>All arrivals</b> ({events.Count}, {targets.Count} objects)");

        if (events.Count == 0)
            return;

        int first = events.FindAll(e => e.arrivedRankedFirst).Count;
        text.AppendLine($"Arrived object has the highest cumulative score: {first}/{events.Count} ({first * 100f / events.Count:F0}%, chance {100f / Mathf.Max(1, targets.Count):F0}%)");
        text.AppendLine();
        text.AppendLine("Mean of features   arrived / others");

        foreach ((string label, System.Func<ObjectFeatures, float> get) in new (string, System.Func<ObjectFeatures, float>)[]
                 {
                     ("cumulative (s)", f => f.cumulative), ("mean", f => f.mean), ("early mean", f => f.earlyMean),
                     ($"last {lateSeconds:F0} s mean", f => f.lateMean), ("high episodes", f => f.highEpisodes),
                     ("longest high (s)", f => f.longestHigh), ("last high before (s)", f => f.lastHighLead)
                 })
        {
            MeanFeature(get, out float arrived, out float others);
            text.AppendLine($"  {label}: {Num(arrived, "F2")} / {Num(others, "F2")}");
        }
    }

    private void MeanFeature(System.Func<ObjectFeatures, float> get, out float arrived, out float others)
    {
        double sa = 0.0, so = 0.0;
        int na = 0, no = 0;

        foreach (ArrivalEvent e in events)
        {
            for (int k = 0; k < targets.Count; k++)
            {
                float v = get(e.features[k]);

                if (float.IsNaN(v))
                    continue;

                if (k == e.target)
                {
                    sa += v;
                    na++;
                }
                else
                {
                    so += v;
                    no++;
                }
            }
        }

        arrived = na > 0 ? (float)(sa / na) : float.NaN;
        others = no > 0 ? (float)(so / no) : float.NaN;
    }

    private static string Num(float v, string format) => float.IsNaN(v) ? "-" : v.ToString(format, CultureInfo.InvariantCulture);

    // ------------------------------------------------------------
    // UI
    // ------------------------------------------------------------
    //   右上: グラフの枠(タイトル / 折れ線 / 凡例 / ヒートマップ)
    //   下  : [>] 時刻 [再生バー(到達の印)]
    //         [< Prev] [Next >] [Replay] [Mode] [Charts]  到達の説明
    private void SetupUI()
    {
        Canvas canvas = frameText != null ? frameText.canvas : FindObjectOfType<Canvas>();

        if (canvas == null)
            return;

#if UNITY_EDITOR
        uiSprite = UnityEditor.AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
#endif

        SetupChartPanel(canvas);
        SetupTimeline(canvas);
    }

    private void SetupChartPanel(Canvas canvas)
    {
        RectTransform panel = CreateRect("RetrospectiveCharts", canvas.transform);
        panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(1f, 1f);
        panel.anchoredPosition = chartPanelOffset;
        panel.sizeDelta = chartPanelSize;
        AddImage(panel.gameObject, uiSprite, new Color(1f, 1f, 1f, 0.95f));
        chartPanel = panel.gameObject;

        chartTitle = CreateText("Title", panel, TextAlignmentOptions.MidlineLeft);
        Place(chartTitle.rectTransform, 12f, 8f, -12f, 30f);
        chartTitle.fontSize = 18f;
        chartTitle.fontStyle = FontStyles.Bold;

        // 折れ線: 左に縦軸の目盛り、下に横軸の目盛り
        float left = 70f, top = 44f, right = 16f, lineHeight = 250f;
        TMP_Text yLabel = CreateText("YLabel", panel, TextAlignmentOptions.Center);
        Place(yLabel.rectTransform, 0f, top, left - 40f, lineHeight);
        yLabel.text = "Gaze\npossibility";
        yLabel.fontSize = 12f;
        yLabel.enableWordWrapping = true;

        for (int g = 0; g <= 4; g++)
        {
            TMP_Text tick = CreateText("YTick", panel, TextAlignmentOptions.MidlineRight);
            float y = top + lineHeight * (1f - g / 4f);
            Place(tick.rectTransform, left - 40f, y - 10f, 34f, 20f);
            tick.text = (g / 4f).ToString("0.##", CultureInfo.InvariantCulture);
            tick.fontSize = 13f;
        }

        RectTransform lineRect = CreateRect("LineChart", panel);
        Place(lineRect, left, top, -right, lineHeight);
        lineChartImage = lineRect.gameObject.AddComponent<RawImage>();
        lineTexture = new Texture2D(ChartWidth, ChartHeight, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        lineChartImage.texture = lineTexture;
        lineChartImage.raycastTarget = false;
        lineCursor = CreateCursor(lineRect);

        float width = chartPanelSize.x - left - right;

        for (float t = 0f; t >= -windowSeconds - 0.001f; t -= 2f)
        {
            TMP_Text tick = CreateText("XTick", panel, TextAlignmentOptions.Center);
            float x = left + (t + windowSeconds) / windowSeconds * width;
            Place(tick.rectTransform, x - 30f, top + lineHeight + 2f, 60f, 20f);
            tick.text = t == 0f ? "arrival" : $"{t:0}s";
            tick.fontSize = 13f;
        }

        legendText = CreateText("Legend", panel, TextAlignmentOptions.MidlineLeft);
        Place(legendText.rectTransform, left, top + lineHeight + 24f, -right, 24f);
        legendText.fontSize = 14f;

        // ヒートマップ: 左に行の名前、右に色の目盛り
        float heatTop = top + lineHeight + 56f;
        float heatHeight = chartPanelSize.y - heatTop - 34f;
        TMP_Text heatTitle = CreateText("HeatTitle", panel, TextAlignmentOptions.MidlineLeft);
        Place(heatTitle.rectTransform, 12f, heatTop - 4f, -12f, 22f);
        heatTitle.text = "Heatmap (same window)";
        heatTitle.fontSize = 14f;

        heatmapLabels = CreateRect("RowLabels", panel);
        Place(heatmapLabels, 4f, heatTop + 20f, left - 10f, heatHeight - 20f);

        RectTransform heatRect = CreateRect("Heatmap", panel);
        Place(heatRect, left, heatTop + 20f, -right - 40f, heatHeight - 20f);
        heatmapImage = heatRect.gameObject.AddComponent<RawImage>();
        heatmapImage.raycastTarget = false;
        heatmapCursor = CreateCursor(heatRect);

        Texture2D bar = new Texture2D(1, 64, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };

        for (int y = 0; y < 64; y++)
            bar.SetPixel(0, y, HeatColor(y / 63f));

        bar.Apply();
        RectTransform barRect = CreateRect("ColorBar", panel);
        barRect.anchorMin = barRect.anchorMax = barRect.pivot = new Vector2(1f, 1f);
        barRect.anchoredPosition = new Vector2(-right - 14f, -(heatTop + 20f));
        barRect.sizeDelta = new Vector2(14f, heatHeight - 20f);
        RawImage barImage = barRect.gameObject.AddComponent<RawImage>();
        barImage.texture = bar;
        barImage.raycastTarget = false;

        TMP_Text axis = CreateText("HeatAxis", panel, TextAlignmentOptions.MidlineLeft);
        Place(axis.rectTransform, left, heatTop + heatHeight + 2f, -right - 40f, 20f);
        axis.text = $"-{windowSeconds:0}s";
        axis.fontSize = 13f;
        TMP_Text axisEnd = CreateText("HeatAxisEnd", panel, TextAlignmentOptions.MidlineRight);
        Place(axisEnd.rectTransform, left, heatTop + heatHeight + 2f, -right - 40f, 20f);
        axisEnd.text = "arrival   (blue 0 - white 0.5 - red 1)";
        axisEnd.fontSize = 13f;
    }

    // 枠の左上から (x, y) の位置に置く。width が負なら右端からの距離
    private static void Place(RectTransform rect, float x, float y, float width, float height)
    {
        if (width < 0f)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.offsetMin = new Vector2(x, -y - height);
            rect.offsetMax = new Vector2(width, -y);
        }
        else
        {
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
        }
    }

    private RectTransform CreateCursor(RectTransform parent)
    {
        RectTransform cursor = CreateRect("Cursor", parent);
        cursor.sizeDelta = new Vector2(2f, 0f);
        AddImage(cursor.gameObject, null, new Color(0.1f, 0.1f, 0.1f, 0.8f)).raycastTarget = false;
        cursor.gameObject.SetActive(false);
        return cursor;
    }

    private void SetupTimeline(Canvas canvas)
    {
        RectTransform root = CreateRect("Timeline", canvas.transform);
        root.anchorMin = new Vector2(0f, 0f);
        root.anchorMax = new Vector2(1f, 0f);
        root.pivot = new Vector2(0.5f, 0f);
        root.anchoredPosition = new Vector2(0f, 10f);
        root.sizeDelta = new Vector2(-40f, 96f);
        AddImage(root.gameObject, uiSprite, new Color(0.95f, 0.95f, 0.95f, 0.9f));

        RectTransform row1 = CreateRow(root, 0f, 48f);
        Button playPause = CreateButton(row1, ">", 0f, 50f, out playPauseText);
        playPause.onClick.AddListener(TogglePlay);

        timeText = CreateText("Time", row1, TextAlignmentOptions.MidlineLeft);
        PlaceLeft(timeText.rectTransform, 60f, 380f);

        timelineSlider = CreateSlider(row1, new Color(0.25f, 0.45f, 0.95f));
        RectTransform timelineRect = (RectTransform)timelineSlider.transform;
        timelineRect.anchorMin = new Vector2(0f, 0f);
        timelineRect.anchorMax = new Vector2(1f, 1f);
        timelineRect.offsetMin = new Vector2(450f, 10f);
        timelineRect.offsetMax = new Vector2(-10f, -10f);
        timelineSlider.minValue = 0f;
        timelineSlider.maxValue = Mathf.Max(duration, 0.001f);
        timelineSlider.onValueChanged.AddListener(Seek);

        RectTransform background = (RectTransform)timelineSlider.transform.Find("Background");

        foreach (ArrivalEvent e in events)
        {
            RectTransform marker = CreateRect("Arrival", background);
            float x = Mathf.Clamp01(e.arriveTime / Mathf.Max(duration, 0.001f));
            marker.anchorMin = new Vector2(x, -0.4f);
            marker.anchorMax = new Vector2(x, 1.4f);
            marker.sizeDelta = new Vector2(3f, 0f);
            marker.anchoredPosition = Vector2.zero;
            AddImage(marker.gameObject, null, ObjectColor(e.target)).raycastTarget = false;
        }

        RectTransform row2 = CreateRow(root, 48f, 44f);
        CreateButton(row2, "< Prev", 10f, 100f, out _).onClick.AddListener(() => SelectEvent(currentEvent - 1));
        CreateButton(row2, "Next >", 115f, 100f, out _).onClick.AddListener(() => SelectEvent(currentEvent + 1));
        CreateButton(row2, "Replay", 220f, 100f, out _).onClick.AddListener(Replay);
        CreateButton(row2, "", 325f, 170f, out modeText).onClick.AddListener(ToggleMode);
        CreateButton(row2, "Charts", 500f, 100f, out _).onClick.AddListener(() => chartPanel.SetActive(!chartPanel.activeSelf));

        eventText = CreateText("Event", row2, TextAlignmentOptions.MidlineLeft);
        PlaceLeft(eventText.rectTransform, 615f, 700f);

        UpdateTimelineUI();
    }

    private void UpdateTimelineUI()
    {
        if (timelineSlider == null)
            return;

        timelineSlider.SetValueWithoutNotify(playbackTime);
        playPauseText.text = isPlaying ? "||" : ">";
        timeText.text = $"{playbackTime:F1} / {duration:F1} s  ({HomeSkeletonIO.FormatClock(playbackTime)} / {HomeSkeletonIO.FormatClock(duration)})";
        modeText.text = $"Mode: {chartMode}";

        if (currentEvent >= 0)
        {
            ArrivalEvent e = events[currentEvent];
            float rel = playbackTime - e.arriveTime;
            eventText.text = $"Arrival #{currentEvent + 1}/{events.Count}: {targets[e.target].name} at {e.arriveTime:F1} s   (now {rel:+0.0;-0.0} s)";
        }
        else
        {
            eventText.text = "No arrivals";
        }
    }

    private static RectTransform CreateRow(RectTransform parent, float top, float height)
    {
        RectTransform row = CreateRect("Row", parent);
        row.anchorMin = new Vector2(0f, 1f);
        row.anchorMax = new Vector2(1f, 1f);
        row.pivot = new Vector2(0.5f, 1f);
        row.anchoredPosition = new Vector2(0f, -top);
        row.sizeDelta = new Vector2(0f, height);
        return row;
    }

    private static void PlaceLeft(RectTransform rect, float x, float width, float margin = 0f)
    {
        rect.anchorMin = new Vector2(0f, 0f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 0.5f);
        rect.offsetMin = new Vector2(x, margin);
        rect.offsetMax = new Vector2(x + width, -margin);
    }

    private Button CreateButton(RectTransform parent, string label, float x, float width, out TMP_Text text)
    {
        RectTransform rect = CreateRect("Button", parent);
        PlaceLeft(rect, x, width, 4f);
        Button button = rect.gameObject.AddComponent<Button>();
        button.targetGraphic = AddImage(rect.gameObject, uiSprite, Color.white);

        text = CreateText("Label", rect, TextAlignmentOptions.Center);
        SetStretch(text.rectTransform, new Vector2(4f, 0f), new Vector2(-4f, 0f));
        text.text = label;
        text.fontStyle = FontStyles.Bold;
        text.overflowMode = TextOverflowModes.Overflow;
        text.enableAutoSizing = true;
        text.fontSizeMin = 10f;
        text.fontSizeMax = 20f;
        return button;
    }

    private Slider CreateSlider(RectTransform parent, Color fillColor)
    {
        RectTransform root = CreateRect("Slider", parent);

        RectTransform background = CreateRect("Background", root);
        background.anchorMin = new Vector2(0f, 0.3f);
        background.anchorMax = new Vector2(1f, 0.7f);
        background.offsetMin = background.offsetMax = Vector2.zero;
        AddImage(background.gameObject, uiSprite, new Color(0.75f, 0.75f, 0.75f));

        RectTransform fillArea = CreateRect("Fill Area", root);
        fillArea.anchorMin = new Vector2(0f, 0.3f);
        fillArea.anchorMax = new Vector2(1f, 0.7f);
        fillArea.offsetMin = fillArea.offsetMax = Vector2.zero;

        RectTransform fill = CreateRect("Fill", fillArea);
        fill.offsetMin = fill.offsetMax = Vector2.zero;
        AddImage(fill.gameObject, uiSprite, new Color(fillColor.r, fillColor.g, fillColor.b, 0.35f)).raycastTarget = false;

        RectTransform handleArea = CreateRect("Handle Slide Area", root);
        SetStretch(handleArea, new Vector2(8f, 0f), new Vector2(-8f, 0f));

        RectTransform handle = CreateRect("Handle", handleArea);
        handle.sizeDelta = new Vector2(16f, 0f);
        Image handleImage = AddImage(handle.gameObject, uiSprite, fillColor);

        Slider slider = root.gameObject.AddComponent<Slider>();
        slider.fillRect = fill;
        slider.handleRect = handle;
        slider.targetGraphic = handleImage;
        slider.direction = Slider.Direction.LeftToRight;
        return slider;
    }

    private static RectTransform CreateRect(string name, Transform parent)
    {
        RectTransform rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.gameObject.layer = parent.gameObject.layer;
        return rect;
    }

    private static Image AddImage(GameObject obj, Sprite sprite, Color color)
    {
        Image image = obj.AddComponent<Image>();
        image.sprite = sprite;
        image.type = sprite != null ? Image.Type.Sliced : Image.Type.Simple;
        image.color = color;
        return image;
    }

    private TMP_Text CreateText(string name, RectTransform parent, TextAlignmentOptions alignment)
    {
        TextMeshProUGUI text = CreateRect(name, parent).gameObject.AddComponent<TextMeshProUGUI>();

        if (frameText != null)
            text.font = frameText.font;

        text.fontSize = 20f;
        text.color = new Color(0.2f, 0.2f, 0.2f);
        text.alignment = alignment;
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.raycastTarget = false;
        return text;
    }

    private static void SetStretch(RectTransform rect, Vector2 offsetMin, Vector2 offsetMax)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = offsetMin;
        rect.offsetMax = offsetMax;
    }

    private static int LowerBound(float[] values, float query)
    {
        int low = 0, high = values.Length;

        while (low < high)
        {
            int mid = (low + high) / 2;

            if (values[mid] < query)
                low = mid + 1;
            else
                high = mid;
        }

        return low;
    }

    private static int Nearest(float[] values, float query)
    {
        if (values.Length == 0)
            return -1;

        int i = LowerBound(values, query);

        if (i >= values.Length)
            return values.Length - 1;

        if (i > 0 && query - values[i - 1] <= values[i] - query)
            return i - 1;

        return i;
    }

    // ------------------------------------------------------------
    // Types
    // ------------------------------------------------------------
    private class Sample
    {
        public float time;
        public float duration;
        public readonly List<Person> persons = new List<Person>();
    }

    private class Person
    {
        public int id;
        public List<HomeSkeletonJoint> joints;
        public bool hasGaze;
        public Vector3 origin;
        public Vector3 direction;
        public bool fixedGaze;
        public bool hasPelvis;
        public Vector3 pelvis;
    }

    private class Target
    {
        public string name;
        public Bounds bounds;
        // 箱の上の 3×3×3 の点
        public Vector3[] points;
    }

    // 子供の時系列。score[物][フレーム] は注視可能性(頭部方向が無ければ NaN)、distance は骨盤から物までの水平距離
    private class ChildTrack
    {
        public readonly int Count;
        public readonly float[] time, duration, pelvisX, pelvisZ;
        public readonly int[] sampleIndex;
        public readonly bool[] hasGaze;
        public readonly Vector3[] origin, direction;
        public readonly float[][] score, distance;

        public ChildTrack(int n, int targetCount)
        {
            Count = n;
            time = new float[n];
            duration = new float[n];
            pelvisX = new float[n];
            pelvisZ = new float[n];
            sampleIndex = new int[n];
            hasGaze = new bool[n];
            origin = new Vector3[n];
            direction = new Vector3[n];
            score = new float[targetCount][];
            distance = new float[targetCount][];

            for (int k = 0; k < targetCount; k++)
            {
                score[k] = new float[n];
                distance[k] = new float[n];
            }
        }

        public int IndexOfSample(int sample)
        {
            int low = 0, high = Count - 1;

            while (low <= high)
            {
                int mid = (low + high) / 2;

                if (sampleIndex[mid] == sample)
                    return mid;

                if (sampleIndex[mid] < sample)
                    low = mid + 1;
                else
                    high = mid - 1;
            }

            return -1;
        }
    }

    private class ArrivalEvent
    {
        public int target;
        public float arriveTime;
        public int arriveIndex;
        public ObjectFeatures[] features;
        public bool arrivedRankedFirst;
    }

    // 到達の windowSeconds 秒前から到達までの、1つの物の注視可能性の特徴量
    private class ObjectFeatures
    {
        public int frames;
        public float cumulative = float.NaN;   // 累積注視可能性(スコア × 時間, 秒)
        public float mean = float.NaN;
        public float max;
        public float earlyMean = float.NaN;    // 前半の平均(初期注視可能性)
        public float lateMean = float.NaN;     // 到達直前 lateSeconds 秒の平均(最終注視可能性)
        public int highEpisodes;               // 高い状態(highThreshold 以上)になった回数
        public int revisits;                   // 再注視回数(高い状態に戻った回数 = highEpisodes − 1)
        public float firstHighLead = float.NaN; // 最初に高くなったのは到達の何秒前か
        public float lastHighLead = float.NaN;  // 最後に高かったのは到達の何秒前か(最終注視までの時間)
        public float longestHigh;              // 高い状態が続いた最長の秒数(注視の持続性)
    }
}

// シーン7で保存するパラメータ(retrospective_<骨格>_params.json)
[System.Serializable]
public class HomeRetrospectiveParameters
{
    public string experimentName;
    public string subjectName;
    public string createdAt;
    public string skeletonSource;
    public string childName;
    public string headDirectionMethod;
    public float downwardAngle;
    public float uncertaintySigma;
    public float distanceDecay;
    public float approachDistance;
    public float leaveDistance;
    public float trackGapSeconds;
    public float windowSeconds;
    public float highThreshold;
    public float lateSeconds;
    public List<string> targets = new List<string>();
}
