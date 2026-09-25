using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Azure.Kinect.BodyTracking;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// シーン4(4GazeAnalyze)用。骨格データから視線コーンを求め、
//   - 部屋メッシュのヒートマップ   → Skeleton/<実験対象者>/Analysis/heatmap_<骨格>.json
//   - 対象物体ごとのスコア/注視対象 → Skeleton/<実験対象者>/Analysis/score_<骨格>.json
// を作る。視線コーンの判定は ConeHeatMapShow / ScoreHeat / ScoreHeatSummary と同じ。
//
// 骨格データは次のどちらかを選ぶ(<骨格> の部分のファイル名になる):
//   Filtered … シーン3の出力 Filtered/filtered_<Mode>.json (人物ID = trackId)
//   Kinect   … キネクト1台分の <ID>_skeleton.json          (人物ID = bodyId)
//
// 部屋オブジェクトと対象物体は同じGameObjectの HomeEnvLoader が Env から再生成する。
// シーン0で部屋オブジェクトの下に置いた対象物体(Env/TargetObjects.json)は自動でスコアの対象になる。
// それ以外に数えたいものは名前で指定する(シーンに置いたオブジェクトも指定できる)。
//
// 操作:
//   Play(Space) … 記録と同じ速さで再生しながらヒートを溜める(最後まで再生すると保存)
//   Analyze All … 再生せずに全フレームを一括で解析して保存
//   Save        … その時点の結果を保存
[RequireComponent(typeof(HomeEnvLoader))]
public class HomeGazeAnalyzer : MonoBehaviour
{
    public enum SkeletonSource
    {
        // シーン3で作った信頼度フィルタリング後の骨格
        Filtered,
        // キネクト1台分の骨格
        Kinect,
        // シーン1(Record Raw Mkv)の生データから画像で推定したもの (Tools/GazePipeline → Filtered/filtered_Image.json)
        // 頭の位置・頭の向き・目の視線が入っているので、関節から向きを計算しない
        Image
    }

    public enum ImageDirection
    {
        // 目の視線. 視線が取れないフレーム(顔が見えない)は頭の向きを使う
        GazeElseHead,
        // 頭の向きだけ
        Head,
        // 目の視線だけ(取れないフレームは視線なし)
        GazeOnly
    }

    public enum HeadDirectionMethod
    {
        // Head→Nose (ConeHeatMapShow / ScoreHeat と同じ)
        HeadToNose,
        // 両耳の中点→Nose (シーン2/3の頭部方向の線と同じ。耳が無ければ Head→Nose)
        EarsToNose
    }

    public enum HeatMapDisplay
    {
        // 部屋の上に半透明の赤を重ねる(ConeHeatMapShow の頂点カラー (1,0,0,heat) をそのまま描く)
        RedOverlay,
        // 2Analyze/ConeAnalyzeGaze と同じ Custom/TextureHeatAlpha。見ていない場所は青、見るほど赤
        Palette
    }

    private const string NoHitLabel = "None";
    private const string NoDataLabel = "NoData";

    [Header("Subject")]
    [HomeExperimentFolder(HomeExperimentFolderAttribute.Kind.Subject)]
    [SerializeField] private string subjectName = "";

    [Header("Skeleton")]
    [SerializeField] private SkeletonSource skeletonSource = SkeletonSource.Filtered;
    [Tooltip("Filtered のとき: 読み込む filtered_<Mode>.json")]
    [SerializeField] private HomeSkeletonFilter.ConfidenceMode filteredMode = HomeSkeletonFilter.ConfidenceMode.HeadJoints;
    [Tooltip("Kinect のとき: 読み込む <ID>_skeleton.json のキネクトID")]
    [SerializeField] private string kinectId = "A";
    [Tooltip("Image のとき: 視線として使う向き")]
    [SerializeField] private ImageDirection imageDirection = ImageDirection.GazeElseHead;
    [Tooltip("解析する人物。Filtered / Image なら trackId、Kinect なら bodyId。-1 なら全員。")]
    [SerializeField] private int personId = -1;
    [Tooltip("次のフレームまでの間隔がこれより長いときは、フレームの時間をこの秒数で打ち切る(記録の抜けを注視時間に数えない)。")]
    [SerializeField] private float maxFrameDuration = 0.2f;

    [Header("Gaze (ConeHeatMapShow / ScoreHeat と同じ)")]
    [SerializeField] private HeadDirectionMethod headDirectionMethod = HeadDirectionMethod.HeadToNose;
    [Tooltip("頭部方向を下へ補正する角度(度)")]
    [SerializeField] private float downwardAngle = 24.4f;
    [Tooltip("コーンの角度(度, 中心軸からの半角)")]
    [SerializeField] private float coneAngle = 10f;
    [Tooltip("コーンの長さ(m)")]
    [SerializeField] private float coneDistance = 5f;
    [Tooltip("コーンの中心ほどヒートを大きくする")]
    [SerializeField] private bool useCenterWeightedHeat = false;
    [SerializeField] private float heatPerHit = 1f;

    [Header("Heat Map")]
    [Tooltip("ヒートマップを作る部屋オブジェクト/対象物体の名前。空なら全部の部屋オブジェクトと対象物体。")]
    [SerializeField] private string[] heatMapTargetNames = new string[0];
    [Tooltip("ヒートマップの表示方法(再生中にも切り替えられる)\n" +
             "RedOverlay: 部屋の上に半透明の赤を重ねる(見ていない場所は元の部屋のまま)\n" +
             "Palette   : 2Analyze/ConeAnalyzeGaze と同じ。見ていない場所は青、見るほど 水色→黄→赤")]
    [SerializeField] private HeatMapDisplay heatMapDisplay = HeatMapDisplay.RedOverlay;
    [Tooltip("RedOverlay 用のマテリアル(頂点カラーを半透明で描く Custom/HeatOverlay)。空なら Custom/HeatOverlay から作る。")]
    [SerializeField] private Material heatMapMaterial;
    [Tooltip("Palette 用のマテリアル(Custom/TextureHeatAlpha)。部屋の元のテクスチャを引き継ぐ。空なら Custom/TextureHeatAlpha から作る。")]
    [SerializeField] private Material paletteMaterial;
    [Tooltip("ヒートマップを表示する人物ID(Filtered なら trackId、Kinect なら bodyId)。複数指定すると合計を表示する。空なら全員。画面右上の一覧でも選べる。")]
    [SerializeField] private int[] displayPersonIds = new int[0];
    [Tooltip("このヒートで最大の色になる")]
    [SerializeField] private float maxHeatDisplay = 50f;
    [Tooltip("再生中に頂点カラーを更新する間隔(秒)")]
    [SerializeField] private float colorUpdateInterval = 0.1f;

    [Header("Score")]
    [Tooltip("シーン0で保存した対象物体(Env/TargetObjects.json)をスコアの対象にする。部屋オブジェクトの下に置いた物体ごとに数える。")]
    [SerializeField] private bool useEnvTargetObjects = true;
    [Tooltip("スコアを数える部屋オブジェクト/対象物体(またはその子)の名前")]
    [SerializeField] private string[] scoreTargetNames = new string[0];
    [Tooltip("スコアを数えるシーン上のオブジェクト(部屋オブジェクト以外に置いた箱など)")]
    [SerializeField] private GameObject[] scoreTargets = new GameObject[0];

    [Header("Visual")]
    [SerializeField] private GameObject jointPrefab;
    [Tooltip("空の場合は頂点カラー対応の Sprites/Default を使う。")]
    [SerializeField] private Material lineMaterial;
    [SerializeField] private float jointScale = 0.06f;
    [SerializeField] private float lineWidth = 0.02f;
    [Tooltip("人物ごとの色")]
    [SerializeField] private Color[] personColors =
    {
        Color.red, Color.green, Color.blue, Color.yellow, Color.cyan, Color.magenta
    };
    [Tooltip("補正後の視線方向を線で表示する")]
    [SerializeField] private bool showGaze = true;
    [SerializeField] private float gazeLineLength = 1.5f;
    [SerializeField] private float gazeLineWidth = 0.015f;

    [Header("Playback")]
    [SerializeField] private float playbackSpeed = 1f;
    [SerializeField] private KeyCode playStopKey = KeyCode.Space;

    [Header("Output")]
    [Tooltip("出力ファイル名の末尾に付ける文字(パラメータ違いで保存し分けるとき)。例: heatmap_filtered_HeadJoints_<label>.json")]
    [SerializeField] private string outputLabel = "";
    [Tooltip("最後まで解析したら自動で保存する")]
    [SerializeField] private bool saveOnFinish = true;

    [Header("UI")]
    [SerializeField] private Button playButton;
    [SerializeField] private Button stopButton;
    [SerializeField] private Button analyzeAllButton;
    [SerializeField] private Button saveButton;
    [SerializeField] private TMP_Text frameText;
    [SerializeField] private TMP_Text scoreText;

    private HomeEnvLoader env;
    private readonly List<Sample> samples = new List<Sample>();
    private readonly List<HeatMesh> heatMeshes = new List<HeatMesh>();
    private readonly HashSet<MeshFilter> overlayFilters = new HashSet<MeshFilter>();
    private Material overlayMaterial;
    private HeatMapDisplay appliedDisplay;
    // 骨格データに出てくる人物IDとそのフレーム数(画面右上の一覧の選択肢)
    private readonly SortedDictionary<int, int> personFrameCounts = new SortedDictionary<int, int>();
    // ヒートマップに表示する人物(空なら全員)
    private readonly SortedSet<int> selectedPersonIds = new SortedSet<int>();
    private int[] appliedPersonIds = new int[0];
    private readonly Dictionary<int, Toggle> personToggles = new Dictionary<int, Toggle>();
    private TMP_Text personHeaderText;
    private Sprite uiSprite;
    private Sprite checkmarkSprite;
    private readonly List<ScoreTarget> targets = new List<ScoreTarget>();
    private readonly List<HomeGazeFrameRecord> records = new List<HomeGazeFrameRecord>();

    private Transform visualRoot;
    private HomeSkeletonBodyVisual.Style visualStyle;
    private readonly Dictionary<int, PersonVisual> visuals = new Dictionary<int, PersonVisual>();

    private float duration;
    private float playbackTime;
    private bool isPlaying;
    private int nextSample;
    private int shownSample = -1;
    private float colorTimer;

    private int gazeFrames;
    private int noTargetFrames;
    private int noDataFrames;
    private float gazeSeconds;
    private float noTargetSeconds;

    private string SourceName =>
        skeletonSource == SkeletonSource.Filtered ? $"filtered_{filteredMode}" :
        skeletonSource == SkeletonSource.Image ? $"image_{imageDirection}" :
        $"Kinect{kinectId.Trim()}";

    private string OutputName =>
        string.IsNullOrWhiteSpace(outputLabel) ? SourceName : $"{SourceName}_{outputLabel.Trim()}";

    private void Awake()
    {
        env = GetComponent<HomeEnvLoader>();
    }

    private void Start()
    {
        visualRoot = new GameObject("GazePlayback").transform;
        visualStyle = new HomeSkeletonBodyVisual.Style
        {
            jointPrefab = jointPrefab,
            lineMaterial = lineMaterial,
            jointScale = jointScale,
            lineWidth = lineWidth,
            // 頭部方向の線の代わりに補正後の視線を自前で描く
            showHeadDirection = false
        };

        LoadSamples();
        SetupHeatMeshes();
        SetupScoreTargets();
        ApplyDisplayMode();
        SetupPersonSelector();
        ResetAnalysis();

        if (playButton != null)
            playButton.onClick.AddListener(Play);

        if (stopButton != null)
            stopButton.onClick.AddListener(Stop);

        if (analyzeAllButton != null)
            analyzeAllButton.onClick.AddListener(AnalyzeAll);

        if (saveButton != null)
            saveButton.onClick.AddListener(Save);

        UpdateText();
    }

    private void Update()
    {
        // インスペクターで表示方法や人物を変えたらすぐ反映する
        if (heatMapDisplay != appliedDisplay)
            ApplyDisplayMode();

        if (DisplayPersonsChanged())
            SetDisplayPersons(displayPersonIds);

        if (Input.GetKeyDown(playStopKey))
        {
            if (isPlaying)
                Stop();
            else
                Play();
        }

        if (!isPlaying)
            return;

        playbackTime += Time.deltaTime * playbackSpeed;

        while (nextSample < samples.Count && samples[nextSample].time <= playbackTime)
        {
            ProcessSample(samples[nextSample]);
            nextSample++;
        }

        ShowSample(nextSample - 1);

        colorTimer += Time.deltaTime;

        if (colorTimer >= colorUpdateInterval)
        {
            colorTimer = 0f;
            ApplyColors();
        }

        if (nextSample >= samples.Count)
        {
            isPlaying = false;
            Finish();
        }

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
        {
            Debug.LogWarning("[HomeGazeAnalyzer] 解析できる骨格データがありません。");
            return;
        }

        ResetAnalysis();
        playbackTime = 0f;
        isPlaying = true;
        Debug.Log("[HomeGazeAnalyzer] Playback started");
    }

    // 再生を止める。溜まったヒートとスコアは次に Play / Analyze All するまで残す。
    public void Stop()
    {
        isPlaying = false;
        ShowSample(-1);
        ApplyColors();
        UpdateText();
    }

    [ContextMenu("Analyze All")]
    public void AnalyzeAll()
    {
        if (samples.Count == 0)
        {
            Debug.LogWarning("[HomeGazeAnalyzer] 解析できる骨格データがありません。");
            return;
        }

        isPlaying = false;
        ResetAnalysis();

        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();

        foreach (Sample sample in samples)
            ProcessSample(sample);

        nextSample = samples.Count;
        playbackTime = duration;
        Debug.Log($"[HomeGazeAnalyzer] {samples.Count} samples を {stopwatch.ElapsedMilliseconds} ms で解析しました。");

        ShowSample(-1);
        Finish();
        UpdateText();
    }

    [ContextMenu("Save")]
    public void Save()
    {
        if (!HomeExperimentPaths.IsValidFolderName(env.ExperimentName, out _) ||
            !HomeExperimentPaths.IsValidFolderName(subjectName, out _))
        {
            Debug.LogError("[HomeGazeAnalyzer] 実験の名前/実験対象者が正しくないため保存できません。");
            return;
        }

        if (nextSample < samples.Count)
            Debug.LogWarning($"[HomeGazeAnalyzer] 解析の途中({nextSample}/{samples.Count})の結果を保存します。");

        string heatMapPath = HomeExperimentPaths.GetGazeHeatMapPath(env.ExperimentName, subjectName, OutputName);
        string scorePath = HomeExperimentPaths.GetGazeScorePath(env.ExperimentName, subjectName, OutputName);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(heatMapPath));
            File.WriteAllText(heatMapPath, JsonUtility.ToJson(CreateHeatMapData()));
            File.WriteAllText(scorePath, JsonUtility.ToJson(CreateScoreData(), true));
            HomeExperimentPaths.RefreshAssetDatabase();
            Debug.Log($"[HomeGazeAnalyzer] 保存しました:\n  {heatMapPath}\n  {scorePath}");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[HomeGazeAnalyzer] 保存に失敗しました: {heatMapPath}\n{e}");
        }
    }

    private void Finish()
    {
        ApplyColors();
        LogSummary();

        if (saveOnFinish)
            Save();
    }

    // ------------------------------------------------------------
    // Load skeleton
    // ------------------------------------------------------------
    private void LoadSamples()
    {
        samples.Clear();
        personFrameCounts.Clear();

        if (!HomeExperimentPaths.IsValidFolderName(env.ExperimentName, out string error))
        {
            Debug.LogError($"[HomeGazeAnalyzer] 実験の名前を正しく入力してください。{error}");
            return;
        }

        if (!HomeExperimentPaths.IsValidFolderName(subjectName, out error))
        {
            Debug.LogError($"[HomeGazeAnalyzer] 実験対象者を正しく入力してください。{error}");
            return;
        }

        if (skeletonSource == SkeletonSource.Filtered)
            LoadFiltered(filteredMode.ToString());
        else if (skeletonSource == SkeletonSource.Image)
            LoadFiltered("Image");
        else
            LoadKinect();

        for (int i = 0; i < samples.Count; i++)
        {
            float interval = i + 1 < samples.Count
                ? samples[i + 1].time - samples[i].time
                : (i > 0 ? samples[i].time - samples[i - 1].time : 0f);

            samples[i].duration = Mathf.Clamp(interval, 0f, maxFrameDuration);

            foreach (Person person in samples[i].persons)
            {
                if (!person.fixedGaze)
                    person.hasGaze = TryGetGaze(person.joints, out person.origin, out person.direction);

                personFrameCounts.TryGetValue(person.id, out int count);
                personFrameCounts[person.id] = count + 1;
            }
        }

        duration = samples.Count > 0 ? samples[samples.Count - 1].time : 0f;

        StringBuilder text = new StringBuilder();
        text.Append($"[HomeGazeAnalyzer] {SourceName}: {samples.Count} samples, {duration:F1}s を読み込みました。人物 {personFrameCounts.Count} 人");

        foreach (KeyValuePair<int, int> pair in personFrameCounts)
            text.Append($"\n  person_{pair.Key}: {pair.Value} frames");

        Debug.Log(text.ToString());
    }

    private void LoadFiltered(string modeName)
    {
        string path = HomeExperimentPaths.GetFilteredSkeletonPath(env.ExperimentName, subjectName, modeName);

        if (!File.Exists(path))
        {
            string how = skeletonSource == SkeletonSource.Image
                ? "シーン1で Record Raw Mkv にチェックを入れて記録してから Tools/GazePipeline/run_pipeline.py で作ってください"
                : "先にシーン3で作ってください";
            Debug.LogError($"[HomeGazeAnalyzer] 骨格のファイルがありません。{how}: {path}");
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
                if (personId >= 0 && person.trackId != personId)
                    continue;

                Person p = new Person { id = person.trackId, joints = person.joints };

                if (skeletonSource == SkeletonSource.Image)
                {
                    // 画像で推定した頭の位置と向きをそのまま使う(関節からの計算・下向きの補正はしない)
                    bool useGaze = imageDirection != ImageDirection.Head && person.hasGazeDirection;
                    bool useHead = imageDirection != ImageDirection.GazeOnly && person.hasHeadDirection;

                    p.fixedGaze = true;
                    p.origin = person.headPosition;
                    p.hasGaze = useGaze || useHead;
                    p.direction = useGaze ? person.gazeDirection.normalized : person.headDirection.normalized;
                    p.usedEyeGaze = useGaze;
                }

                sample.persons.Add(p);
            }

            samples.Add(sample);
        }
    }

    private void LoadKinect()
    {
        foreach (HomeSkeletonTrack track in HomeSkeletonIO.LoadSubject(env.ExperimentName, subjectName, "HomeGazeAnalyzer"))
        {
            if (track.kinectId != kinectId.Trim())
                continue;

            for (int i = 0; i < track.frames.Count; i++)
            {
                Sample sample = new Sample { time = track.times[i] };

                if (track.frames[i].bodies != null)
                {
                    foreach (HomeSkeletonBody body in track.frames[i].bodies)
                    {
                        if (personId >= 0 && body.bodyId != personId)
                            continue;

                        sample.persons.Add(new Person { id = (int)body.bodyId, joints = body.joints });
                    }
                }

                samples.Add(sample);
            }

            return;
        }

        Debug.LogError($"[HomeGazeAnalyzer] Kinect{kinectId} の骨格データがありません。");
    }

    // ConeHeatMapShow.ProcessConeGaze / ScoreHeat.ProcessConeGaze と同じ視線方向の作り方
    private bool TryGetGaze(List<HomeSkeletonJoint> joints, out Vector3 head, out Vector3 direction)
    {
        direction = Vector3.zero;

        if (!HomeSkeletonIO.TryGetJointPosition(joints, JointId.Head, out head) ||
            !HomeSkeletonIO.TryGetJointPosition(joints, JointId.Nose, out Vector3 nose))
            return false;

        Vector3 from = head;

        if (headDirectionMethod == HeadDirectionMethod.EarsToNose &&
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

    // ------------------------------------------------------------
    // Targets
    // ------------------------------------------------------------
    private void SetupHeatMeshes()
    {
        heatMeshes.Clear();

        List<MeshFilter> meshFilters = new List<MeshFilter>();

        if (heatMapTargetNames == null || heatMapTargetNames.Length == 0)
        {
            foreach (GameObject roomObject in GetEnvObjects())
            {
                if (roomObject != null)
                    AddMeshFilters(roomObject, meshFilters);
            }
        }
        else
        {
            foreach (string targetName in heatMapTargetNames)
            {
                List<GameObject> found = FindRoomObjects(targetName);

                if (found.Count == 0)
                    Debug.LogWarning($"[HomeGazeAnalyzer] ヒートマップの対象が部屋オブジェクトにありません: {targetName}");

                foreach (GameObject obj in found)
                    AddMeshFilters(obj, meshFilters);
            }
        }

        foreach (MeshFilter meshFilter in meshFilters)
        {
            if (!EnsureReadable(meshFilter))
            {
                Debug.LogWarning($"[HomeGazeAnalyzer] {meshFilter.name}: 頂点を読めません(モデルの Read/Write を有効にしてください)。");
                continue;
            }

            HeatMesh heatMesh = CreateHeatOverlay(meshFilter);
            Vector3[] vertices = heatMesh.mesh.vertices;

            heatMesh.name = meshFilter.name;
            heatMesh.path = GetPath(meshFilter.transform);
            heatMesh.cloud = new VertexCloud(meshFilter.transform, vertices);
            heatMesh.heat = new float[vertices.Length];
            heatMesh.colors = new Color[vertices.Length];
            heatMeshes.Add(heatMesh);
        }

        int vertexCount = 0;

        foreach (HeatMesh heatMesh in heatMeshes)
            vertexCount += heatMesh.heat.Length;

        Debug.Log($"[HomeGazeAnalyzer] ヒートマップの対象: {heatMeshes.Count} メッシュ, {vertexCount} 頂点");
    }

    private void SetupScoreTargets()
    {
        targets.Clear();

        if (useEnvTargetObjects)
        {
            // シーン0で保存した対象物体を group ごとにまとめる(保存した順)
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
                AddScoreTarget(group, objectsByGroup[group]);
        }

        if (scoreTargetNames != null)
        {
            foreach (string targetName in scoreTargetNames)
            {
                if (string.IsNullOrWhiteSpace(targetName) || HasScoreTarget(targetName.Trim()))
                    continue;

                List<GameObject> found = FindRoomObjects(targetName);

                if (found.Count == 0)
                {
                    Debug.LogWarning($"[HomeGazeAnalyzer] スコアの対象が部屋オブジェクト/対象物体にありません: {targetName}");
                    continue;
                }

                AddScoreTarget(targetName.Trim(), found);
            }
        }

        if (scoreTargets != null)
        {
            foreach (GameObject target in scoreTargets)
            {
                if (target != null && !HasScoreTarget(target.name))
                    AddScoreTarget(target.name, new List<GameObject> { target });
            }
        }

        Debug.Log($"[HomeGazeAnalyzer] スコアの対象: {targets.Count} 個");
    }

    private bool HasScoreTarget(string targetName)
    {
        foreach (ScoreTarget target in targets)
        {
            if (target.name == targetName)
                return true;
        }

        return false;
    }

    private void AddScoreTarget(string targetName, List<GameObject> objects)
    {
        List<MeshFilter> meshFilters = new List<MeshFilter>();

        foreach (GameObject obj in objects)
            AddMeshFilters(obj, meshFilters);

        List<VertexCloud> clouds = new List<VertexCloud>();

        foreach (MeshFilter meshFilter in meshFilters)
        {
            if (!EnsureReadable(meshFilter))
                continue;

            Vector3[] vertices = meshFilter.sharedMesh.vertices;

            if (vertices.Length > 0)
                clouds.Add(new VertexCloud(meshFilter.transform, vertices));
        }

        if (clouds.Count == 0)
        {
            Debug.LogWarning($"[HomeGazeAnalyzer] スコアの対象に頂点を読めるメッシュがありません: {targetName}");
            return;
        }

        targets.Add(new ScoreTarget { name = targetName, clouds = clouds });
    }

    // Env から再生成した部屋オブジェクトと対象物体
    private IEnumerable<GameObject> GetEnvObjects()
    {
        foreach (GameObject obj in env.RoomObjects)
            yield return obj;

        foreach (GameObject obj in env.TargetObjects)
            yield return obj;
    }

    // 部屋オブジェクト/対象物体とその子から名前が一致するものを探す
    private List<GameObject> FindRoomObjects(string targetName)
    {
        List<GameObject> found = new List<GameObject>();

        if (string.IsNullOrWhiteSpace(targetName))
            return found;

        string trimmed = targetName.Trim();

        foreach (GameObject roomObject in GetEnvObjects())
        {
            if (roomObject == null)
                continue;

            foreach (Transform child in roomObject.GetComponentsInChildren<Transform>(true))
            {
                if (child.name == trimmed)
                    found.Add(child.gameObject);
            }
        }

        return found;
    }

    private void AddMeshFilters(GameObject obj, List<MeshFilter> meshFilters)
    {
        foreach (MeshFilter meshFilter in obj.GetComponentsInChildren<MeshFilter>())
        {
            // 自分で重ねたヒートマップ表示用のメッシュは対象にしない
            if (overlayFilters.Contains(meshFilter))
                continue;

            if (meshFilter.sharedMesh != null && !meshFilters.Contains(meshFilter))
                meshFilters.Add(meshFilter);
        }
    }

    // ConeHeatMapShow と同じく頂点カラー (1,0,0,heat) でヒートを表す。
    // 元のメッシュは書き換えず、同じ形のメッシュを子に作ってヒートマップの表示に使う。
    //   RedOverlay: 元の部屋はそのまま表示し、このメッシュを半透明の赤で上から重ねる
    //   Palette   : 元の部屋を隠し、このメッシュを元のテクスチャ + TextureHeatAlpha で描く
    private HeatMesh CreateHeatOverlay(MeshFilter source)
    {
        Mesh sourceMesh = source.sharedMesh;
        Mesh mesh = new Mesh
        {
            name = sourceMesh.name + "_Heat",
            indexFormat = sourceMesh.indexFormat
        };

        mesh.vertices = sourceMesh.vertices;

        // Palette はテクスチャとライティングを使うので UV と法線も写す
        Vector2[] uv = sourceMesh.uv;
        if (uv.Length == mesh.vertexCount)
            mesh.uv = uv;

        Vector3[] normals = sourceMesh.normals;
        bool hasNormals = normals.Length == mesh.vertexCount;
        if (hasNormals)
            mesh.normals = normals;

        mesh.subMeshCount = sourceMesh.subMeshCount;

        for (int i = 0; i < sourceMesh.subMeshCount; i++)
            mesh.SetIndices(sourceMesh.GetIndices(i), sourceMesh.GetTopology(i), i);

        if (!hasNormals)
            mesh.RecalculateNormals();

        mesh.colors = new Color[mesh.vertexCount];
        mesh.RecalculateBounds();

        GameObject overlay = new GameObject(source.name + "_HeatOverlay");
        overlay.layer = source.gameObject.layer;
        overlay.transform.SetParent(source.transform, false);

        MeshFilter meshFilter = overlay.AddComponent<MeshFilter>();
        meshFilter.sharedMesh = mesh;
        overlayFilters.Add(meshFilter);

        MeshRenderer meshRenderer = overlay.AddComponent<MeshRenderer>();
        meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        meshRenderer.receiveShadows = false;

        return new HeatMesh
        {
            mesh = mesh,
            sourceRenderer = source.GetComponent<MeshRenderer>(),
            overlayRenderer = meshRenderer
        };
    }

    // heatMapDisplay に合わせて、元の部屋の表示と重ねたメッシュのマテリアルを切り替える
    private void ApplyDisplayMode()
    {
        appliedDisplay = heatMapDisplay;

        if (overlayMaterial == null)
        {
            overlayMaterial = heatMapMaterial != null
                ? heatMapMaterial
                : new Material(Shader.Find("Custom/HeatOverlay"));
        }

        foreach (HeatMesh heatMesh in heatMeshes)
        {
            int count = Mathf.Max(heatMesh.mesh.subMeshCount, 1);
            bool palette = heatMapDisplay == HeatMapDisplay.Palette;

            if (palette && heatMesh.paletteMaterials == null)
                heatMesh.paletteMaterials = CreatePaletteMaterials(heatMesh.sourceRenderer, count);

            Material[] materials = new Material[count];

            for (int i = 0; i < count; i++)
                materials[i] = palette ? heatMesh.paletteMaterials[i] : overlayMaterial;

            heatMesh.overlayRenderer.sharedMaterials = materials;

            if (heatMesh.sourceRenderer != null)
                heatMesh.sourceRenderer.enabled = !palette;
        }
    }

    // 元のマテリアルのテクスチャを引き継いだ TextureHeatAlpha のマテリアルをサブメッシュごとに作る
    private Material[] CreatePaletteMaterials(MeshRenderer sourceRenderer, int count)
    {
        Material template = paletteMaterial != null
            ? paletteMaterial
            : new Material(Shader.Find("Custom/TextureHeatAlpha"));

        Material[] originals = sourceRenderer != null ? sourceRenderer.sharedMaterials : new Material[0];
        Material[] materials = new Material[count];

        for (int i = 0; i < count; i++)
        {
            materials[i] = new Material(template);
            Material original = i < originals.Length ? originals[i] : null;

            if (original != null && original.HasProperty("_MainTex") && materials[i].HasProperty("_MainTex"))
            {
                materials[i].mainTexture = original.mainTexture;
                materials[i].mainTextureScale = original.mainTextureScale;
                materials[i].mainTextureOffset = original.mainTextureOffset;
            }
        }

        return materials;
    }

    // 頂点を読むには、モデル(.obj/.fbx)の Import Settings で Read/Write が有効になっている必要がある。
    // エディタ上では無効なら自動で有効にして再インポートする(.meta の isReadable が 1 に変わる)。
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
                Debug.Log($"[HomeGazeAnalyzer] {path} の Read/Write を有効にして再インポートします。");
                importer.isReadable = true;
                importer.SaveAndReimport();
            }

            // 再インポート後のメッシュを取り直す
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

    private string GetPath(Transform node)
    {
        Transform root = env.RoomObjects.Count > 0 && env.RoomObjects[0] != null
            ? env.RoomObjects[0].transform.parent
            : null;

        StringBuilder path = new StringBuilder(node.name);

        for (Transform parent = node.parent; parent != null && parent != root; parent = parent.parent)
            path.Insert(0, parent.name + "/");

        return path.ToString();
    }

    // ------------------------------------------------------------
    // Analysis
    // ------------------------------------------------------------
    private void ResetAnalysis()
    {
        nextSample = 0;
        playbackTime = 0f;
        colorTimer = 0f;
        records.Clear();
        gazeFrames = 0;
        noTargetFrames = 0;
        noDataFrames = 0;
        gazeSeconds = 0f;
        noTargetSeconds = 0f;

        foreach (HeatMesh heatMesh in heatMeshes)
        {
            System.Array.Clear(heatMesh.heat, 0, heatMesh.heat.Length);
            heatMesh.personHeat.Clear();
        }

        foreach (ScoreTarget target in targets)
        {
            target.totalHeat = 0f;
            target.hitFrames = 0;
            target.hitSeconds = 0f;
            target.gazeFrames = 0;
            target.gazeSeconds = 0f;
        }

        ApplyColors();
    }

    private void ProcessSample(Sample sample)
    {
        Cone cone = new Cone(coneAngle, coneDistance);

        foreach (Person person in sample.persons)
        {
            HomeGazeFrameRecord record = new HomeGazeFrameRecord
            {
                timeSec = sample.time,
                durationSec = sample.duration,
                personId = person.id,
                gazeTarget = NoDataLabel,
                gazeAngle = -1f
            };

            for (int t = 0; t < targets.Count; t++)
            {
                record.heats.Add(0f);
                record.angles.Add(-1f);
            }

            records.Add(record);

            if (!person.hasGaze)
            {
                noDataFrames++;
                continue;
            }

            cone.Set(person.origin, person.direction);

            foreach (HeatMesh heatMesh in heatMeshes)
                AccumulateCone(heatMesh.cloud, cone, heatMesh.heat, heatMesh.GetPersonHeat(person.id), out _, out _);

            float bestAngle = float.MaxValue;
            int bestIndex = -1;

            for (int t = 0; t < targets.Count; t++)
            {
                ScoreTarget target = targets[t];
                float heat = 0f;
                float minAngle = float.MaxValue;

                foreach (VertexCloud cloud in target.clouds)
                {
                    if (AccumulateCone(cloud, cone, null, null, out float cloudHeat, out float cloudAngle))
                    {
                        heat += cloudHeat;
                        minAngle = Mathf.Min(minAngle, cloudAngle);
                    }
                }

                if (minAngle == float.MaxValue)
                    continue;

                float minAngleDeg = minAngle * Mathf.Rad2Deg;
                record.heats[t] = heat;
                record.angles[t] = minAngleDeg;
                record.hitTargets.Add(target.name);

                target.totalHeat += heat;
                target.hitFrames++;
                target.hitSeconds += sample.duration;

                if (minAngleDeg < bestAngle)
                {
                    bestAngle = minAngleDeg;
                    bestIndex = t;
                }
            }

            gazeFrames++;
            gazeSeconds += sample.duration;

            if (bestIndex >= 0)
            {
                record.gazeTarget = targets[bestIndex].name;
                record.gazeAngle = bestAngle;
                targets[bestIndex].gazeFrames++;
                targets[bestIndex].gazeSeconds += sample.duration;
            }
            else
            {
                record.gazeTarget = NoHitLabel;
                noTargetFrames++;
                noTargetSeconds += sample.duration;
            }
        }
    }

    // ScoreHeat.AddConeScores / ConeHeatMapShow.AddConeHeat と同じ判定。
    // heat / personHeat が null でなければ頂点ごとのヒートを足す(全員の合計と人物ごと)。コーンに入った頂点があれば true。
    private bool AccumulateCone(VertexCloud cloud, Cone cone, float[] heat, float[] personHeat, out float totalHeat, out float minAngleRad)
    {
        totalHeat = 0f;
        minAngleRad = float.MaxValue;

        if (!VertexCloud.MayIntersect(cloud.center, cloud.radius, cone))
            return false;

        bool hit = false;
        Vector3[] positions = cloud.positions;

        foreach (VertexCloud.Cell cell in cloud.cells)
        {
            if (!VertexCloud.MayIntersect(cell.center, cell.radius, cone))
                continue;

            foreach (int i in cell.indices)
            {
                Vector3 toVertex = positions[i] - cone.origin;
                float distance = toVertex.magnitude;

                if (distance <= 0f || distance > cone.distance)
                    continue;

                float dot = Vector3.Dot(cone.direction, toVertex / distance);

                if (dot < cone.cosAngle)
                    continue;

                float angle = Mathf.Acos(Mathf.Clamp(dot, -1f, 1f));
                float weight = 1f;

                if (useCenterWeightedHeat)
                {
                    weight = 1f - angle / cone.angleRad;
                    weight *= weight;
                }

                float added = heatPerHit * weight;
                totalHeat += added;

                if (heat != null)
                    heat[i] += added;

                if (personHeat != null)
                    personHeat[i] += added;

                if (angle < minAngleRad)
                    minAngleRad = angle;

                hit = true;
            }
        }

        return hit;
    }


    private void ApplyColors()
    {
        float max = Mathf.Max(maxHeatDisplay, 0.0001f);
        bool showAll = selectedPersonIds.Count == 0;

        foreach (HeatMesh heatMesh in heatMeshes)
        {
            // 選んだ人物のヒートの合計(まだ視線が無い人物は 0)。誰も選んでいなければ全員の合計
            float[] heat = heatMesh.heat;

            if (!showAll)
            {
                if (heatMesh.displayHeat == null)
                    heatMesh.displayHeat = new float[heatMesh.heat.Length];

                heat = heatMesh.displayHeat;
                System.Array.Clear(heat, 0, heat.Length);

                foreach (int id in selectedPersonIds)
                {
                    if (!heatMesh.personHeat.TryGetValue(id, out float[] personHeat))
                        continue;

                    for (int i = 0; i < heat.Length; i++)
                        heat[i] += personHeat[i];
                }
            }

            // RedOverlay も Palette も α をヒート量として使う(ConeHeatMapShow と同じ色)
            for (int i = 0; i < heatMesh.colors.Length; i++)
                heatMesh.colors[i] = new Color(1f, 0f, 0f, Mathf.Clamp01(heat[i] / max));

            heatMesh.mesh.colors = heatMesh.colors;
        }
    }

    // ------------------------------------------------------------
    // Person selection
    // ------------------------------------------------------------
    // 表示するヒートマップの人物をまとめて指定する(空なら全員)。
    // 追跡が途切れて同じ人に別のIDが付いたときは、そのIDをまとめて選ぶと1人分のヒートマップになる。
    public void SetDisplayPersons(IEnumerable<int> ids)
    {
        selectedPersonIds.Clear();

        if (ids != null)
        {
            foreach (int id in ids)
            {
                if (id >= 0)
                    selectedPersonIds.Add(id);
            }
        }

        displayPersonIds = new int[selectedPersonIds.Count];
        selectedPersonIds.CopyTo(displayPersonIds);
        appliedPersonIds = (int[])displayPersonIds.Clone();

        RefreshPersonToggles();
        ApplyColors();
        UpdateText();
    }

    private void OnPersonToggle(int id, bool isOn)
    {
        HashSet<int> ids = new HashSet<int>(selectedPersonIds);

        if (id < 0)
        {
            // All を選んだら個別の選択を外す
            if (isOn)
                ids.Clear();
        }
        else if (isOn)
        {
            ids.Add(id);
        }
        else
        {
            ids.Remove(id);
        }

        SetDisplayPersons(ids);
    }

    // インスペクターの displayPersonIds が変わったか
    private bool DisplayPersonsChanged()
    {
        if (displayPersonIds == null)
            displayPersonIds = new int[0];

        if (displayPersonIds.Length != appliedPersonIds.Length)
            return true;

        for (int i = 0; i < displayPersonIds.Length; i++)
        {
            if (displayPersonIds[i] != appliedPersonIds[i])
                return true;
        }

        return false;
    }

    private void RefreshPersonToggles()
    {
        foreach (KeyValuePair<int, Toggle> pair in personToggles)
        {
            bool isOn = pair.Key < 0 ? selectedPersonIds.Count == 0 : selectedPersonIds.Contains(pair.Key);
            pair.Value.SetIsOnWithoutNotify(isOn);
        }

        if (personHeaderText != null)
            personHeaderText.text = $"Heat Map : {GetDisplayPersonsLabel()}";
    }

    private string GetDisplayPersonsLabel()
    {
        if (selectedPersonIds.Count == 0)
            return "All";

        List<string> names = new List<string>();

        foreach (int id in selectedPersonIds)
            names.Add($"person_{id}");

        return string.Join(", ", names);
    }

    // 骨格を読み込んだ後に、出てきた人物でチェックボックスの一覧を作る(画面右上)
    //   [Heat Map : All  v]   … 押すと一覧を開閉する
    //   [x] All (123 frames)
    //   [ ] person_0 (100 frames)
    //   [ ] person_3 (23 frames)
    private void SetupPersonSelector()
    {
        Canvas canvas = frameText != null ? frameText.canvas : FindObjectOfType<Canvas>();

        if (canvas == null)
        {
            Debug.LogWarning("[HomeGazeAnalyzer] Canvas が無いため、人物を選ぶ一覧を作れません。インスペクターの displayPersonIds で選んでください。");
            SetDisplayPersons(displayPersonIds);
            return;
        }

#if UNITY_EDITOR
        // 普通のUIと同じ見た目にするため、エディタ組み込みのスプライトを使う
        uiSprite = UnityEditor.AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
        checkmarkSprite = UnityEditor.AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Checkmark.psd");
#endif

        const float width = 320f;
        const float rowHeight = 30f;
        const int maxVisibleRows = 12;

        RectTransform root = CreateRect("PersonSelector", canvas.transform);
        root.anchorMin = root.anchorMax = root.pivot = new Vector2(1f, 1f);
        root.anchoredPosition = new Vector2(-20f, -20f);
        root.sizeDelta = new Vector2(width, 40f);

        // 見出し(押すと一覧を開閉)
        Button header = root.gameObject.AddComponent<Button>();
        header.targetGraphic = AddImage(root.gameObject, uiSprite, Color.white);
        personHeaderText = CreateText("Label", root, TextAlignmentOptions.MidlineLeft);
        SetStretch(personHeaderText.rectTransform, new Vector2(10f, 0f), new Vector2(-10f, 0f));

        // 一覧(人数が多いときはスクロール)
        List<int> ids = new List<int> { -1 };
        ids.AddRange(personFrameCounts.Keys);

        foreach (int id in displayPersonIds)
        {
            if (id >= 0 && !ids.Contains(id))
                ids.Add(id);
        }

        RectTransform list = CreateRect("List", root);
        list.anchorMin = new Vector2(0f, 0f);
        list.anchorMax = new Vector2(1f, 0f);
        list.pivot = new Vector2(0.5f, 1f);
        list.anchoredPosition = new Vector2(0f, -2f);
        list.sizeDelta = new Vector2(0f, Mathf.Min(ids.Count, maxVisibleRows) * rowHeight);
        AddImage(list.gameObject, uiSprite, new Color(0.95f, 0.95f, 0.95f, 0.95f));
        list.gameObject.AddComponent<RectMask2D>();

        RectTransform content = CreateRect("Content", list);
        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = new Vector2(1f, 1f);
        content.pivot = new Vector2(0.5f, 1f);
        content.sizeDelta = new Vector2(0f, ids.Count * rowHeight);

        ScrollRect scroll = list.gameObject.AddComponent<ScrollRect>();
        scroll.content = content;
        scroll.viewport = list;
        scroll.horizontal = false;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = rowHeight;

        int total = 0;

        foreach (int count in personFrameCounts.Values)
            total += count;

        personToggles.Clear();

        for (int i = 0; i < ids.Count; i++)
        {
            int id = ids[i];
            personFrameCounts.TryGetValue(id, out int frames);
            string label = id < 0 ? $"All ({total} frames)" : $"person_{id} ({frames} frames)";

            Toggle toggle = CreateToggleRow(content, label, i * rowHeight, rowHeight);
            toggle.onValueChanged.AddListener(isOn => OnPersonToggle(id, isOn));
            personToggles[id] = toggle;
        }

        header.onClick.AddListener(() => list.gameObject.SetActive(!list.gameObject.activeSelf));
        list.gameObject.SetActive(false);

        SetDisplayPersons(displayPersonIds);
    }

    private Toggle CreateToggleRow(RectTransform parent, string label, float y, float height)
    {
        RectTransform row = CreateRect("Toggle", parent);
        row.anchorMin = new Vector2(0f, 1f);
        row.anchorMax = new Vector2(1f, 1f);
        row.pivot = new Vector2(0.5f, 1f);
        row.anchoredPosition = new Vector2(0f, -y);
        row.sizeDelta = new Vector2(0f, height);

        // 行のどこを押しても切り替わるように、透明な Image でクリックを受ける
        AddImage(row.gameObject, null, new Color(1f, 1f, 1f, 0f));

        RectTransform box = CreateRect("Box", row);
        box.anchorMin = box.anchorMax = new Vector2(0f, 0.5f);
        box.pivot = new Vector2(0f, 0.5f);
        box.anchoredPosition = new Vector2(10f, 0f);
        box.sizeDelta = new Vector2(20f, 20f);
        Image boxImage = AddImage(box.gameObject, uiSprite, Color.white);

        RectTransform check = CreateRect("Checkmark", box);
        SetStretch(check, Vector2.zero, Vector2.zero);
        Image checkImage = AddImage(check.gameObject, checkmarkSprite, checkmarkSprite != null ? Color.black : new Color(0.2f, 0.4f, 0.9f));

        if (checkmarkSprite == null)
            SetStretch(check, new Vector2(4f, 4f), new Vector2(-4f, -4f));

        TMP_Text text = CreateText("Label", row, TextAlignmentOptions.MidlineLeft);
        SetStretch(text.rectTransform, new Vector2(38f, 0f), new Vector2(-10f, 0f));
        text.text = label;

        Toggle toggle = row.gameObject.AddComponent<Toggle>();
        toggle.targetGraphic = boxImage;
        toggle.graphic = checkImage;
        return toggle;
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

        // シーンのテキストと同じフォントにそろえる
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

    // ------------------------------------------------------------
    // Output
    // ------------------------------------------------------------
    private HomeGazeParameters CreateParameters()
    {
        return new HomeGazeParameters
        {
            skeletonSource = SourceName,
            personId = personId,
            // Image では画像で推定した向きをそのまま使い、下向きの補正はしない
            headDirectionMethod = skeletonSource == SkeletonSource.Image ? $"Image_{imageDirection}" : headDirectionMethod.ToString(),
            downwardAngle = skeletonSource == SkeletonSource.Image ? 0f : downwardAngle,
            coneAngle = coneAngle,
            coneDistance = coneDistance,
            useCenterWeightedHeat = useCenterWeightedHeat,
            heatPerHit = heatPerHit
        };
    }

    private HomeGazeHeatMapList CreateHeatMapData()
    {
        HomeGazeHeatMapList data = new HomeGazeHeatMapList
        {
            experimentName = env.ExperimentName,
            subjectName = subjectName,
            createdAt = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            parameters = CreateParameters(),
            sampleCount = samples.Count,
            processedSamples = nextSample
        };

        foreach (HeatMesh heatMesh in heatMeshes)
        {
            GetHeatStats(heatMesh.heat, out float max, out float total);

            HomeGazeHeatMapMesh meshData = new HomeGazeHeatMapMesh
            {
                name = heatMesh.name,
                path = heatMesh.path,
                vertexCount = heatMesh.heat.Length,
                maxHeat = max,
                totalHeat = total,
                heat = heatMesh.heat
            };

            foreach (int id in personFrameCounts.Keys)
            {
                if (!heatMesh.personHeat.TryGetValue(id, out float[] personHeat))
                    continue;

                GetHeatStats(personHeat, out float personMax, out float personTotal);

                meshData.persons.Add(new HomeGazePersonHeat
                {
                    personId = id,
                    maxHeat = personMax,
                    totalHeat = personTotal,
                    heat = personHeat
                });
            }

            data.meshes.Add(meshData);
        }

        return data;
    }

    private static void GetHeatStats(float[] heat, out float max, out float total)
    {
        max = 0f;
        total = 0f;

        foreach (float value in heat)
        {
            max = Mathf.Max(max, value);
            total += value;
        }
    }

    private HomeGazeScoreList CreateScoreData()
    {
        HomeGazeScoreList data = new HomeGazeScoreList
        {
            experimentName = env.ExperimentName,
            subjectName = subjectName,
            createdAt = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            parameters = CreateParameters(),
            sampleCount = samples.Count,
            processedSamples = nextSample,
            noHitLabel = NoHitLabel,
            noDataLabel = NoDataLabel,
            frameCount = records.Count,
            gazeFrames = gazeFrames,
            noTargetFrames = noTargetFrames,
            noDataFrames = noDataFrames,
            gazeSeconds = gazeSeconds,
            noTargetSeconds = noTargetSeconds,
            frames = records
        };

        foreach (ScoreTarget target in targets)
        {
            data.targets.Add(new HomeGazeTargetScore
            {
                name = target.name,
                totalHeat = target.totalHeat,
                hitFrames = target.hitFrames,
                hitSeconds = target.hitSeconds,
                gazeFrames = target.gazeFrames,
                gazeSeconds = target.gazeSeconds
            });
        }

        return data;
    }

    private void LogSummary()
    {
        StringBuilder text = new StringBuilder();
        text.Append($"[HomeGazeAnalyzer] {subjectName} / {OutputName}: {records.Count} frames ");
        text.Append($"(視線あり {gazeFrames}, 対象なし {noTargetFrames}, 骨格なし {noDataFrames})\n");

        foreach (ScoreTarget target in targets)
        {
            text.Append($"  {target.name}: 注視 {target.gazeSeconds:F1}s ({target.gazeFrames} frames), ");
            text.Append($"ヒット {target.hitSeconds:F1}s ({target.hitFrames} frames), Heat {target.totalHeat:F1}\n");
        }

        Debug.Log(text.ToString());
    }

    // ------------------------------------------------------------
    // Display
    // ------------------------------------------------------------
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
                if (!visuals.TryGetValue(person.id, out PersonVisual visual))
                {
                    visual = CreatePersonVisual(person.id);
                    visuals[person.id] = visual;
                }

                visual.body.Apply(person.joints);

                if (visual.gaze != null)
                {
                    visual.gaze.enabled = person.hasGaze;

                    if (person.hasGaze)
                    {
                        visual.gaze.SetPosition(0, person.origin);
                        visual.gaze.SetPosition(1, person.origin + person.direction * gazeLineLength);

                        // 目の視線は黄色、頭の向き(関節から求めたもの含む)は人物の色
                        Color lineColor = person.usedEyeGaze ? Color.yellow : visual.headColor;
                        visual.gaze.startColor = lineColor;
                        visual.gaze.endColor = lineColor;
                    }
                }

                shown.Add(person.id);
            }
        }

        foreach (KeyValuePair<int, PersonVisual> pair in visuals)
        {
            if (shown.Contains(pair.Key))
                continue;

            pair.Value.body.SetVisible(false);

            if (pair.Value.gaze != null)
                pair.Value.gaze.enabled = false;
        }
    }

    private PersonVisual CreatePersonVisual(int id)
    {
        Color color = personColors.Length > 0 ? personColors[Mathf.Abs(id) % personColors.Length] : Color.white;
        PersonVisual visual = new PersonVisual
        {
            body = new HomeSkeletonBodyVisual(visualRoot, $"person_{id}", color, visualStyle)
        };

        if (showGaze)
        {
            LineRenderer line = new GameObject($"person_{id}_Gaze").AddComponent<LineRenderer>();
            line.transform.SetParent(visualRoot, false);
            line.sharedMaterial = lineMaterial != null ? lineMaterial : new Material(Shader.Find("Sprites/Default"));
            line.startColor = Color.Lerp(color, Color.white, 0.5f);
            visual.headColor = line.startColor;
            line.endColor = line.startColor;
            line.startWidth = gazeLineWidth;
            line.endWidth = gazeLineWidth;
            line.positionCount = 2;
            line.enabled = false;
            visual.gaze = line;
        }

        return visual;
    }

    private void UpdateText()
    {
        if (frameText != null)
        {
            frameText.text =
                $"{subjectName} ({OutputName})  Time : {playbackTime:F1} / {duration:F1} s\n" +
                $"Frame : {nextSample} / {samples.Count}\n" +
                $"Heat Map : {GetDisplayPersonsLabel()} ({heatMapDisplay})";
        }

        if (scoreText == null)
            return;

        StringBuilder text = new StringBuilder();

        foreach (ScoreTarget target in targets)
        {
            text.AppendLine(target.name);
            text.AppendLine($"  Gaze : {target.gazeSeconds:F1} s ({target.gazeFrames})");
            text.AppendLine($"  Hit : {target.hitSeconds:F1} s ({target.hitFrames})");
            text.AppendLine($"  Heat : {target.totalHeat:F1}");
        }

        text.AppendLine("All Gaze");
        text.AppendLine($"  Gaze Frames : {gazeFrames} ({gazeSeconds:F1} s)");
        text.AppendLine($"  No Target : {noTargetFrames} ({noTargetSeconds:F1} s)");
        text.AppendLine($"  No Data : {noDataFrames}");

        scoreText.text = text.ToString();
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
        // 向きがファイルで決まっている(Image). 関節から計算しない
        public bool fixedGaze;
        // Image で目の視線を使った(false なら頭の向き)
        public bool usedEyeGaze;
    }

    private class HeatMesh
    {
        public string name;
        public string path;
        public Mesh mesh;
        public VertexCloud cloud;
        // 全員の合計と人物ごとのヒート(頂点と同じ並び)
        public float[] heat;
        public readonly Dictionary<int, float[]> personHeat = new Dictionary<int, float[]>();
        // 選んだ人物のヒートの合計(表示用の作業領域)
        public float[] displayHeat;
        public Color[] colors;

        // 元の部屋のレンダラーと、上に重ねたヒートマップ表示用のレンダラー
        public MeshRenderer sourceRenderer;
        public MeshRenderer overlayRenderer;
        public Material[] paletteMaterials;

        public float[] GetPersonHeat(int personId)
        {
            if (!personHeat.TryGetValue(personId, out float[] values))
            {
                values = new float[heat.Length];
                personHeat[personId] = values;
            }

            return values;
        }
    }

    private class ScoreTarget
    {
        public string name;
        public List<VertexCloud> clouds;
        public float totalHeat;
        public int hitFrames;
        public float hitSeconds;
        public int gazeFrames;
        public float gazeSeconds;
    }

    private class PersonVisual
    {
        public HomeSkeletonBodyVisual body;
        public LineRenderer gaze;
        public Color headColor = Color.white;
    }

    private struct Cone
    {
        public Vector3 origin;
        public Vector3 direction;
        public readonly float distance;
        public readonly float angleRad;
        public readonly float cosAngle;

        public Cone(float angleDeg, float distance)
        {
            origin = Vector3.zero;
            direction = Vector3.forward;
            this.distance = distance;
            angleRad = Mathf.Max(angleDeg, 0.0001f) * Mathf.Deg2Rad;
            cosAngle = Mathf.Cos(angleRad);
        }

        public void Set(Vector3 origin, Vector3 direction)
        {
            this.origin = origin;
            this.direction = direction;
        }
    }

    // ワールド座標の頂点を格子に分けておき、コーンと交わらない格子をまとめて飛ばす。
    // (部屋メッシュは数万頂点あるので、フレームごとに全頂点を調べると一括解析が遅い)
    private class VertexCloud
    {
        private const float CellSize = 0.25f;

        public class Cell
        {
            public Vector3 center;
            public float radius;
            public int[] indices;
        }

        public readonly Vector3[] positions;
        public readonly List<Cell> cells = new List<Cell>();
        public readonly Vector3 center;
        public readonly float radius;

        // 部屋オブジェクトは動かないので、ワールド座標は最初に一度だけ計算する
        public VertexCloud(Transform transform, Vector3[] localVertices)
        {
            positions = new Vector3[localVertices.Length];
            Dictionary<Vector3Int, List<int>> grid = new Dictionary<Vector3Int, List<int>>();

            for (int i = 0; i < localVertices.Length; i++)
            {
                Vector3 position = transform.TransformPoint(localVertices[i]);
                positions[i] = position;

                Vector3Int key = Vector3Int.FloorToInt(position / CellSize);

                if (!grid.TryGetValue(key, out List<int> indices))
                {
                    indices = new List<int>();
                    grid[key] = indices;
                }

                indices.Add(i);
            }

            foreach (List<int> indices in grid.Values)
            {
                Bounds bounds = new Bounds(positions[indices[0]], Vector3.zero);

                foreach (int i in indices)
                    bounds.Encapsulate(positions[i]);

                cells.Add(new Cell
                {
                    center = bounds.center,
                    radius = bounds.extents.magnitude,
                    indices = indices.ToArray()
                });
            }

            Bounds all = new Bounds(positions.Length > 0 ? positions[0] : Vector3.zero, Vector3.zero);

            foreach (Vector3 position in positions)
                all.Encapsulate(position);

            center = all.center;
            radius = all.extents.magnitude;
        }

        // 中心 center・半径 radius の球がコーンと交わる可能性があるか(交わらないときだけ確実に false)
        public static bool MayIntersect(Vector3 sphereCenter, float sphereRadius, Cone cone)
        {
            Vector3 toCenter = sphereCenter - cone.origin;
            float distance = toCenter.magnitude;

            if (distance <= sphereRadius)
                return true;

            if (distance - sphereRadius > cone.distance)
                return false;

            float angle = Mathf.Acos(Mathf.Clamp(Vector3.Dot(cone.direction, toCenter / distance), -1f, 1f));
            float spread = Mathf.Asin(Mathf.Clamp01(sphereRadius / distance));

            return angle <= cone.angleRad + spread;
        }
    }
}
