using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Azure.Kinect.BodyTracking;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// シーン6(6GazeBehaviorAnalyze)用。子供と親の位置・頭の向きから、次の4つの仮説を決めた定義のまま計算し、場面を見て確かめる。
// (Tools/GazeBehavior/compare_hypotheses.py と同じ定義。Python はキネクトと画像の骨格をまとめて比べる用)
//
//   仮説1 子供の頭の向きは、進行方向より先に変わる
//         移動中の全フレームで、頭の水平の向き(t) と 進行方向(t + ずれ) のなす角の平均が最も小さくなるずれ(正なら頭が先)
//   仮説2 子供は、親がそばにいる物に近づく
//         接近のうち、着いた時刻に親がその物の parentNearDistance 以内にいた割合 − ふだん親がその物のそばにいる割合
//   仮説3 子供は、物に着く前の5秒間にその物の方を向いている
//         着く 5〜0 秒前にその物(頂点を囲む箱の中心)が子供の視野に入っていた割合 − ふだんの割合
//         (参考: 頭の向きの代わりに進行方向を使った同じ値。これと同じくらいなら、歩いている方を向いているだけかもしれない)
//   仮説4 子供は、親から離れた物へ向かう前に親の方を向く(社会的参照)
//         親から離れた物(着いた時刻に親がその物から parentNearDistance より外)への接近について、
//         着く 6〜2 秒前に親の頭が子供の視野に入っていた割合 − ふだんの割合
//
// 定義:
//   位置       … 骨盤の水平位置。物までの距離は、骨盤から物(メッシュの頂点を囲む床に平行な箱)までの水平の最短距離
//   頭の向き   … シーン4/5と同じ(キネクト: 頭→鼻を下へ downwardAngle 補正 / 画像: 推定した視線や頭の向き)
//   視野       … 子供の視野(左右 約90° / 上下 約70°)の半頂角。
//                左右だけ: 水平のなす角 ≤ viewHalfHorizontal
//                上下も含む: (水平のなす角 / viewHalfHorizontal)^2 + (上下のなす角 / viewHalfVertical)^2 ≤ 1 (楕円)
//   接近       … 子供の骨盤から物までが approachDistance 以下になった最初の時刻(着いた時刻)。
//                その前に leaveDistance より離れているのを見ていたときだけ数え、映っていない時間が trackGapSeconds を超えたら、
//                再び離れるのを見るまで数えない
//   進行方向   … 骨盤の前後 velocitySpan/2 秒の変位。速さ minMoveSpeed 以上を移動中、maxSpeed 超は計測エラー
//   ふだん     … 記録全体の割合
//   偶然かどうか … 95% 区間(接近を選び直す bootstrapCount 回)と p(接近の時刻を shiftMin〜shiftMax 秒でたらめにずらす shiftCount 回で、
//                  同じくらいの差が出る割合)
//
// 操作:
//   Play / Stop(Space)  … 再生 / 一時停止。子供の視野(床の扇形: 親が入ると黄)、親が視野に入っていれば子供→親の線(黄)、
//                          視野に入っている物への線(水色)、親がそばにいる物(青, 床)、子供が近い物(床, 黄 / 接近中は赤)、
//                          進行方向(緑, 床)、過去 trailSeconds 秒の子供の軌跡を表示する
//   < Prev / Next >      … 前 / 次の接近の 6 秒前へ移動する(再生バーの印: 青 = 親がそばの物 / 赤 = 親から離れた物)
//   Page                 … 右の表示を Now(今の状態)/ Hypotheses(4つの仮説の結果)/ Approaches(接近の一覧)で切り替える
//   Save / Analyze All   … Analysis/GazeBehavior/gaze_behavior_<骨格>_hypotheses_results.csv / _approaches.csv / _frames.csv / _params.json に保存
[RequireComponent(typeof(HomeEnvLoader))]
public class HomeGazeBehaviorAnalyzer : MonoBehaviour, IHomeDataRootProvider
{
    public enum Page
    {
        Now,
        Hypotheses,
        Approaches
    }

    [Header("Data Location")]
    [Tooltip("データのルートフォルダ。<ここ>/<実験の名前>/Skeleton/<実験対象者>/ から人物ごとの骨格を読み、Analysis/GazeBehavior/ に保存する。\n" +
             "空なら既定の Assets/Data/HomeExperiment。例: D:\\HomeExperiment\n" +
             "Env(キネクト位置・部屋オブジェクト)もここの <実験の名前>/Env/ から読む。シーン0と同じフォルダを指定する。")]
    [HomeDataRootFolder]
    [SerializeField] private string dataRootDirectory = "";

    [Header("Subject")]
    [HomeExperimentFolder(HomeExperimentFolderAttribute.Kind.Subject)]
    [SerializeField] private string subjectName = "";

    [Header("Skeleton (シーン4/5と同じ)")]
    [SerializeField] private HomeGazeAnalyzer.SkeletonSource skeletonSource = HomeGazeAnalyzer.SkeletonSource.Filtered;
    [Tooltip("Filtered のとき: 読み込む filtered_<Mode>.json")]
    [SerializeField] private HomeSkeletonFilter.ConfidenceMode filteredMode = HomeSkeletonFilter.ConfidenceMode.HeadJoints;
    [Tooltip("Filtered / Image のとき: 頭の向きを平滑化した骨格の名前(grouped_<Mode>_<名前>.json。例: smooth05)。空なら平滑化前")]
    [HomeSkeletonVariant]
    [SerializeField] private string skeletonVariant = "";
    [Tooltip("Kinect のとき: 読み込む <ID>_skeleton.json のキネクトID")]
    [SerializeField] private string kinectId = "A";
    [Tooltip("Image のとき: 視線として使う向き")]
    [SerializeField] private HomeGazeAnalyzer.ImageDirection imageDirection = HomeGazeAnalyzer.ImageDirection.GazeElseHead;
    [Tooltip("次のフレームまでの間隔がこれより長いときは、フレームの時間をこの秒数で打ち切る")]
    [SerializeField] private float maxFrameDuration = 0.2f;

    [Header("Gaze (シーン4/5と同じ)")]
    [SerializeField] private HomeGazeAnalyzer.HeadDirectionMethod headDirectionMethod = HomeGazeAnalyzer.HeadDirectionMethod.HeadToNose;
    [Tooltip("頭部方向を下へ補正する角度(度)")]
    [SerializeField] private float downwardAngle = 24.4f;

    [Header("Targets (シーン4/5と同じ)")]
    [Tooltip("シーン0で保存した対象物体(Env/TargetObjects.json)を対象にする。")]
    [SerializeField] private bool useEnvTargetObjects = true;
    [Tooltip("対象にする部屋オブジェクト/対象物体(またはその子)の名前")]
    [SerializeField] private string[] targetNames = new string[0];
    [Tooltip("対象にするシーン上のオブジェクト")]
    [SerializeField] private GameObject[] targetObjects = new GameObject[0];

    [Header("Persons")]
    [Tooltip("子供の人物の名前(シーン3のグループ名)")]
    [SerializeField] private string childName = "child";
    [Tooltip("親の人物の名前(シーン3のグループ名)")]
    [SerializeField] private string parentName = "mother";

    [Header("Field of View (子供の視野 左右 約90° / 上下 約70° の半頂角)")]
    [SerializeField] private float viewHalfHorizontal = 45f;
    [SerializeField] private float viewHalfVertical = 35f;

    [Header("Approach")]
    [Tooltip("骨盤から物までの水平距離(m)がこれ以下になったら着いたとする")]
    [SerializeField] private float approachDistance = 0.6f;
    [Tooltip("これより離れたら(m)、接近が終わったとする")]
    [SerializeField] private float leaveDistance = 1.0f;
    [Tooltip("映っていない時間がこれを超えたら、離れるのを見るまで接近を数えない(秒)")]
    [SerializeField] private float trackGapSeconds = 1f;
    [Tooltip("親の骨盤から物までの水平距離(m)がこれ以下なら「親がそばにいる物」")]
    [SerializeField] private float parentNearDistance = 1.0f;

    [Header("Motion")]
    [SerializeField] private float velocitySpan = 0.5f;
    [SerializeField] private float minMoveSpeed = 0.2f;
    [SerializeField] private float maxSpeed = 3f;

    [Header("Hypotheses")]
    [Tooltip("仮説4: 親の方を向いていたかを見る時間帯(着いた時刻からの秒)")]
    [SerializeField] private Vector2 parentLookWindow = new Vector2(-6f, -2f);
    [Tooltip("仮説3: 物の方を向いていたかを見る時間帯(着いた時刻からの秒)")]
    [SerializeField] private Vector2 targetLookWindow = new Vector2(-5f, 0f);
    [Tooltip("仮説1: 調べるずれの範囲(±秒)と刻み")]
    [SerializeField] private float maxLag = 2f;
    [SerializeField] private float lagStep = 0.1f;
    [SerializeField] private int bootstrapCount = 1000;
    [SerializeField] private int shiftCount = 500;
    [SerializeField] private float shiftMin = 20f;
    [SerializeField] private float shiftMax = 200f;
    [SerializeField] private int randomSeed = 0;

    [Header("Visual")]
    [SerializeField] private GameObject jointPrefab;
    [Tooltip("空の場合は頂点カラー対応の Sprites/Default を使う。")]
    [SerializeField] private Material lineMaterial;
    [SerializeField] private float jointScale = 0.06f;
    [SerializeField] private float lineWidth = 0.02f;
    [Tooltip("人物ごとの色(人物IDの順。シーン3のグループの色と同じ並び)")]
    [SerializeField] private Color[] personColors =
    {
        Color.red, Color.green, Color.blue, Color.yellow, Color.cyan, Color.magenta
    };
    [SerializeField] private float gazeLineLength = 1.5f;
    [SerializeField] private float gazeLineWidth = 0.015f;
    [SerializeField] private float relationLineWidth = 0.02f;
    [Tooltip("子供の視野の扇形の半径(m)")]
    [SerializeField] private float viewFanLength = 1.5f;
    [Tooltip("進行方向の線の長さ = 速さ × この秒数")]
    [SerializeField] private float moveLineSeconds = 1f;
    [SerializeField] private float trailSeconds = 5f;
    [Tooltip("子供から物までがこれ以下なら床に線を出す(m)")]
    [SerializeField] private float showTargetDistance = 1.5f;

    [Header("Playback")]
    [SerializeField] private float playbackSpeed = 1f;
    [SerializeField] private KeyCode playStopKey = KeyCode.Space;
    [SerializeField] private Page page = Page.Hypotheses;

    [Header("Output")]
    [Tooltip("出力ファイル名の末尾に付ける文字。例: gaze_behavior_filtered_HeadJoints_<label>_hypotheses_results.csv")]
    [SerializeField] private string outputLabel = "";

    [Header("UI")]
    [SerializeField] private Button playButton;
    [SerializeField] private Button stopButton;
    [SerializeField] private Button analyzeAllButton;
    [SerializeField] private Button saveButton;
    [SerializeField] private TMP_Text frameText;
    [SerializeField] private TMP_Text scoreText;

    // 同じ時刻とみなす差(秒)。子供の時刻に親を並べるとき
    private const float AlignTolerance = 0.1f;
    // 窓の中にこれより多くの値があるときだけ割合を求める
    private const int MinWindowFrames = 3;
    private const int FanSegments = 16;

    private HomeEnvLoader env;
    public string DataRootDirectory => dataRootDirectory;

    private readonly List<Sample> samples = new List<Sample>();
    private readonly List<Target> targets = new List<Target>();
    private readonly Dictionary<int, string> personNames = new Dictionary<int, string>();
    private float duration;

    // 子供と親の時系列(子供の時刻に親を並べたものも持つ)
    private Track child;
    private Track parent;
    private ChildAligned aligned;
    private readonly List<ApproachEvent> approaches = new List<ApproachEvent>();
    private readonly List<HypothesisResult> results = new List<HypothesisResult>();
    private float pairStart;
    private float pairEnd;

    // 表示
    private float playbackTime;
    private bool isPlaying;
    private int shownSample = -1;
    private int currentApproach = -1;
    private Transform visualRoot;
    private HomeSkeletonBodyVisual.Style visualStyle;
    private readonly Dictionary<int, HomeSkeletonBodyVisual> bodies = new Dictionary<int, HomeSkeletonBodyVisual>();
    private readonly Dictionary<int, LineRenderer> gazeLines = new Dictionary<int, LineRenderer>();
    private readonly List<LineRenderer> relationLines = new List<LineRenderer>();
    private int usedRelationLines;
    private LineRenderer fanLine;
    private LineRenderer trailLine;
    private Material sharedLineMaterial;
    private string nowText = "";

    // 画面下のバー
    private Sprite uiSprite;
    private Slider timelineSlider;
    private TMP_Text playPauseText;
    private TMP_Text timeText;
    private TMP_Text pageText;
    private TMP_Text approachText;

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
        visualRoot = new GameObject("HypothesisPlayback").transform;
        visualStyle = new HomeSkeletonBodyVisual.Style
        {
            jointPrefab = jointPrefab,
            lineMaterial = lineMaterial,
            jointScale = jointScale,
            lineWidth = lineWidth,
            showHeadDirection = false
        };
        sharedLineMaterial = lineMaterial != null ? lineMaterial : new Material(Shader.Find("Sprites/Default"));
        fanLine = CreateLine("ChildViewFan", Color.white, relationLineWidth * 0.6f);
        trailLine = CreateLine("ChildTrail", Color.white, relationLineWidth * 0.6f);

        LoadSamples();
        SetupTargets();
        BuildTracks();
        DetectApproaches();
        ComputeHypotheses();
        SetupTimeline();

        if (playButton != null)
            playButton.onClick.AddListener(Play);

        if (stopButton != null)
            stopButton.onClick.AddListener(Stop);

        if (analyzeAllButton != null)
            analyzeAllButton.onClick.AddListener(Save);

        if (saveButton != null)
            saveButton.onClick.AddListener(Save);

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

            if (playbackTime >= duration)
            {
                playbackTime = duration;
                isPlaying = false;
            }
        }

        ShowSample(FindSampleIndex(playbackTime));
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

        isPlaying = true;
    }

    public void Stop()
    {
        isPlaying = false;
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

    public void NextPage()
    {
        page = (Page)(((int)page + 1) % System.Enum.GetValues(typeof(Page)).Length);
    }

    // 前 / 次の接近の、仮説4の時間帯の始まり(着く 6 秒前)へ移動する
    public void JumpApproach(int step)
    {
        if (approaches.Count == 0)
            return;

        currentApproach = currentApproach < 0
            ? (step > 0 ? 0 : approaches.Count - 1)
            : Mathf.Clamp(currentApproach + step, 0, approaches.Count - 1);
        Seek(approaches[currentApproach].arriveTime + Mathf.Min(parentLookWindow.x, targetLookWindow.x));
    }

    [ContextMenu("Save")]
    public void Save()
    {
        if (!HomeExperimentPaths.IsValidRootDirectory(dataRootDirectory, out _) ||
            !HomeExperimentPaths.IsValidFolderName(env.ExperimentName, out _) ||
            !HomeExperimentPaths.IsValidFolderName(subjectName, out _) || child == null)
        {
            Debug.LogError("[HomeGazeBehaviorAnalyzer] データのフォルダ/実験の名前/実験対象者/人物が正しくないため保存できません。");
            return;
        }

        string resultsPath = GetOutputPath("hypotheses_results.csv");
        string approachesPath = GetOutputPath("hypotheses_approaches.csv");
        string framesPath = GetOutputPath("hypotheses_frames.csv");
        string paramsPath = GetOutputPath("hypotheses_params.json");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(resultsPath));
            // Excel で文字化けしないように BOM 付きの UTF-8 で書く
            File.WriteAllText(resultsPath, CreateResultsCsv(), new UTF8Encoding(true));
            File.WriteAllText(approachesPath, CreateApproachesCsv(), new UTF8Encoding(true));
            WriteFramesCsv(framesPath);
            File.WriteAllText(paramsPath, JsonUtility.ToJson(CreateParameters(), true));
            HomeExperimentPaths.RefreshAssetDatabase();
            Debug.Log($"[HomeGazeBehaviorAnalyzer] 保存しました:\n  {resultsPath}\n  {approachesPath}\n  {framesPath}\n  {paramsPath}");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[HomeGazeBehaviorAnalyzer] 保存に失敗しました: {resultsPath}\n{e}");
        }
    }

    private string GetOutputPath(string fileKind) =>
        HomeExperimentPaths.GetGazeBehaviorPath(dataRootDirectory, env.ExperimentName, subjectName, OutputName, fileKind);

    // ------------------------------------------------------------
    // Load skeleton (シーン5と同じ)
    // ------------------------------------------------------------
    private void LoadSamples()
    {
        samples.Clear();
        personNames.Clear();

        if (!HomeExperimentPaths.IsValidRootDirectory(dataRootDirectory, out string error) ||
            !HomeExperimentPaths.IsValidFolderName(env.ExperimentName, out error) ||
            !HomeExperimentPaths.IsValidFolderName(subjectName, out error))
        {
            Debug.LogError($"[HomeGazeBehaviorAnalyzer] データのフォルダ/実験の名前/実験対象者を正しく指定してください。{error}");
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
            samples[i].duration = Mathf.Clamp(interval, 0f, maxFrameDuration);

            foreach (Person person in samples[i].persons)
            {
                if (!person.fixedGaze)
                    person.hasGaze = TryGetGaze(person.joints, out person.origin, out person.direction);

                if (!person.hasHead && person.joints != null && HomeSkeletonIO.TryGetJointPosition(person.joints, JointId.Head, out Vector3 head))
                {
                    person.hasHead = true;
                    person.head = head;
                }

                if (person.joints != null && HomeSkeletonIO.TryGetJointPosition(person.joints, JointId.Pelvis, out Vector3 pelvis))
                {
                    person.hasPelvis = true;
                    person.pelvis = pelvis;
                }
            }
        }

        duration = samples.Count > 0 ? samples[samples.Count - 1].time : 0f;
        Debug.Log($"[HomeGazeBehaviorAnalyzer] {SourceName}: {samples.Count} samples, {duration:F1}s を読み込みました。");
    }

    private void LoadFiltered(string modeName)
    {
        string path = HomeExperimentPaths.GetGroupedSkeletonPath(dataRootDirectory, env.ExperimentName, subjectName, modeName, skeletonVariant);

        if (!File.Exists(path))
        {
            Debug.LogError($"[HomeGazeBehaviorAnalyzer] 人物ごとの骨格のファイルがありません(シーン3で Save grouped を押してください): {path}");
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
                    // 画像で推定した頭の位置と向きをそのまま使う(下向きの補正はしない)
                    bool useGaze = imageDirection != HomeGazeAnalyzer.ImageDirection.Head && person.hasGazeDirection;
                    bool useHead = imageDirection != HomeGazeAnalyzer.ImageDirection.GazeOnly && person.hasHeadDirection;

                    p.fixedGaze = true;
                    p.origin = person.headPosition;
                    p.hasGaze = useGaze || useHead;
                    p.direction = useGaze ? person.gazeDirection.normalized : person.headDirection.normalized;
                    p.hasHead = true;
                    p.head = person.headPosition;
                }

                sample.persons.Add(p);
            }

            samples.Add(sample);
        }
    }

    private void LoadKinect()
    {
        foreach (HomeSkeletonTrack track in HomeSkeletonIO.LoadSubject(dataRootDirectory, env.ExperimentName, subjectName, "HomeGazeBehaviorAnalyzer"))
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

        Debug.LogError($"[HomeGazeBehaviorAnalyzer] Kinect{kinectId} の骨格データがありません。");
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
    // Targets (シーン4/5と同じ。距離と方向には頂点を囲む箱を使う)
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

                if (found.Count == 0)
                    Debug.LogWarning($"[HomeGazeBehaviorAnalyzer] 対象が部屋オブジェクト/対象物体にありません: {targetName}");
                else
                    AddTarget(targetName.Trim(), found);
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

        Debug.Log($"[HomeGazeBehaviorAnalyzer] 対象物体: {string.Join(", ", targets.ConvertAll(t => t.name))}");
    }

    // メッシュの頂点(ワールド座標)を囲む箱
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
            Debug.LogWarning($"[HomeGazeBehaviorAnalyzer] 対象に頂点を読めるメッシュがありません: {targetName}");
            return;
        }

        targets.Add(new Target { name = targetName, bounds = bounds });
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

    // 骨盤から箱までの水平の最短距離と、箱の上で最も近い点(床の高さ)
    private static float HorizontalDistance(Bounds bounds, Vector3 position, out Vector3 closest)
    {
        closest = new Vector3(Mathf.Clamp(position.x, bounds.min.x, bounds.max.x), 0f,
                              Mathf.Clamp(position.z, bounds.min.z, bounds.max.z));
        float dx = closest.x - position.x;
        float dz = closest.z - position.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    // ------------------------------------------------------------
    // 子供と親の時系列
    // ------------------------------------------------------------
    private static float Yaw(Vector3 v) => Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg;

    private static Vector3 YawToVector(float yaw) =>
        new Vector3(Mathf.Sin(yaw * Mathf.Deg2Rad), 0f, Mathf.Cos(yaw * Mathf.Deg2Rad));

    private void BuildTracks()
    {
        child = BuildTrack(childName);
        parent = BuildTrack(parentName);

        if (child == null || parent == null)
        {
            Debug.LogError($"[HomeGazeBehaviorAnalyzer] 子供({childName})と親({parentName})の骨格が両方必要です。" +
                           $"Persons の名前をシーン3のグループ名に合わせてください(あるもの: {string.Join(", ", new HashSet<string>(personNames.Values))})");
            child = null;
            parent = null;
            return;
        }

        pairStart = Mathf.Max(child.time[0], parent.time[0]);
        pairEnd = Mathf.Min(child.time[child.Count - 1], parent.time[parent.Count - 1]);
        aligned = new ChildAligned(this);
        Debug.Log($"[HomeGazeBehaviorAnalyzer] 子供 {childName}: {child.Count} frames / 親 {parentName}: {parent.Count} frames");
    }

    // 名前が name の人物の時系列(1フレームに2つあれば最初のもの)
    private Track BuildTrack(string name)
    {
        List<Person> persons = new List<Person>();
        List<int> sampleIndices = new List<int>();

        for (int i = 0; i < samples.Count; i++)
        {
            foreach (Person p in samples[i].persons)
            {
                if (GetPersonName(p.id) != name)
                    continue;

                persons.Add(p);
                sampleIndices.Add(i);
                break;
            }
        }

        if (persons.Count == 0)
            return null;

        Track track = new Track(persons.Count);

        for (int i = 0; i < persons.Count; i++)
        {
            Person p = persons[i];
            track.time[i] = samples[sampleIndices[i]].time;
            track.sampleIndex[i] = sampleIndices[i];
            track.pelvisX[i] = p.hasPelvis ? p.pelvis.x : float.NaN;
            track.pelvisZ[i] = p.hasPelvis ? p.pelvis.z : float.NaN;

            // 頭の位置(向きの起点)。無ければ骨盤
            Vector3 head = p.hasGaze ? p.origin : p.hasHead ? p.head : p.pelvis;
            bool hasHead = p.hasGaze || p.hasHead;
            track.headX[i] = hasHead || p.hasPelvis ? head.x : float.NaN;
            track.headY[i] = hasHead ? head.y : float.NaN;
            track.headZ[i] = hasHead || p.hasPelvis ? head.z : float.NaN;

            track.yaw[i] = float.NaN;
            track.pitch[i] = float.NaN;

            if (p.hasGaze)
            {
                // 真下・真上を向いていて水平の向きが決まらないときは、向きを求めない
                if (new Vector2(p.direction.x, p.direction.z).magnitude > 0.1f)
                    track.yaw[i] = Yaw(p.direction);

                track.pitch[i] = Mathf.Asin(Mathf.Clamp(p.direction.y, -1f, 1f)) * Mathf.Rad2Deg;
            }
        }

        // 速さ・進行方向: 前後 velocitySpan/2 秒の両端の差
        float half = velocitySpan * 0.5f;
        int lo = 0, hi = 0;

        for (int i = 0; i < track.Count; i++)
        {
            float t = track.time[i];

            while (track.time[lo] < t - half)
                lo++;

            if (hi < i)
                hi = i;

            while (hi + 1 < track.Count && track.time[hi + 1] <= t + half)
                hi++;

            float dt = track.time[hi] - track.time[lo];
            track.speed[i] = float.NaN;
            track.moveYaw[i] = float.NaN;
            track.velX[i] = float.NaN;
            track.velZ[i] = float.NaN;

            if (dt < half)
                continue;

            float vx = (track.pelvisX[hi] - track.pelvisX[lo]) / dt;
            float vz = (track.pelvisZ[hi] - track.pelvisZ[lo]) / dt;
            float speed = Mathf.Sqrt(vx * vx + vz * vz);

            if (float.IsNaN(speed) || speed > maxSpeed)
                continue;

            track.speed[i] = speed;
            track.velX[i] = vx;
            track.velZ[i] = vz;

            if (speed >= minMoveSpeed)
                track.moveYaw[i] = Mathf.Atan2(vx, vz) * Mathf.Rad2Deg;
        }

        // 物までの距離
        track.targetDistance = new float[targets.Count][];

        for (int k = 0; k < targets.Count; k++)
        {
            track.targetDistance[k] = new float[track.Count];

            for (int i = 0; i < track.Count; i++)
            {
                track.targetDistance[k][i] = float.IsNaN(track.pelvisX[i])
                    ? float.NaN
                    : HorizontalDistance(targets[k].bounds, new Vector3(track.pelvisX[i], 0f, track.pelvisZ[i]), out _);
            }
        }

        return track;
    }

    // ------------------------------------------------------------
    // 接近
    // ------------------------------------------------------------
    private void DetectApproaches()
    {
        approaches.Clear();

        if (child == null)
            return;

        for (int k = 0; k < targets.Count; k++)
        {
            bool outside = false;
            bool inside = false;
            float last = float.NegativeInfinity;

            for (int i = 0; i < child.Count; i++)
            {
                // 映っていない時間が長ければ、その間に離れたかどうか分からない
                if (child.time[i] - last > trackGapSeconds)
                {
                    outside = false;
                    inside = false;
                }

                last = child.time[i];
                float d = child.targetDistance[k][i];

                if (float.IsNaN(d))
                    continue;

                if (d > leaveDistance)
                {
                    outside = true;
                    inside = false;
                }
                else if (d <= approachDistance && outside && !inside)
                {
                    approaches.Add(new ApproachEvent { target = k, arriveTime = child.time[i], childIndex = i });
                    inside = true;
                    outside = false;
                }
            }
        }

        approaches.Sort((a, b) => a.arriveTime.CompareTo(b.arriveTime));

        foreach (ApproachEvent e in approaches)
        {
            e.parentDistance = parent.ValueAt(parent.targetDistance[e.target], e.arriveTime, 0.2f);
            e.parentNear = !float.IsNaN(e.parentDistance) && e.parentDistance <= parentNearDistance;
            float px = parent.ValueAt(parent.pelvisX, e.arriveTime, 0.2f);
            float pz = parent.ValueAt(parent.pelvisZ, e.arriveTime, 0.2f);
            e.childParentDistance = Mathf.Sqrt(Sq(px - child.pelvisX[e.childIndex]) + Sq(pz - child.pelvisZ[e.childIndex]));
            e.parentLookH = aligned.seesParentH.Mean(e.arriveTime, parentLookWindow);
            e.parentLookHV = aligned.seesParentHV.Mean(e.arriveTime, parentLookWindow);
            e.targetLookH = aligned.facesTargetH[e.target].Mean(e.arriveTime, targetLookWindow);
            e.targetLookHV = aligned.facesTargetHV[e.target].Mean(e.arriveTime, targetLookWindow);
            e.targetLookMove = aligned.movesToTarget[e.target].Mean(e.arriveTime, targetLookWindow);
        }

        int near = approaches.FindAll(e => e.parentNear).Count;
        Debug.Log($"[HomeGazeBehaviorAnalyzer] 子供の接近: {approaches.Count} 回(親がそばの物 {near} 回 / 親から離れた物 {approaches.Count - near} 回)");
    }

    private static float Sq(float v) => v * v;

    // ------------------------------------------------------------
    // 仮説
    // ------------------------------------------------------------
    private void ComputeHypotheses()
    {
        results.Clear();

        if (child == null)
            return;

        System.Random random = new System.Random(randomSeed);
        float[] times = approaches.ConvertAll(e => e.arriveTime).ToArray();
        int[] all = Indices(approaches.Count);

        // 仮説1 頭の向きの先行
        ComputeLag(out float lag, out float bestAngle, out float angleAtZero, out int movingFrames);
        results.Add(new HypothesisResult
        {
            key = "H1", title = "Head turns before walking direction", variant = "head",
            n = movingFrames, lag = lag, value = float.NaN, before = bestAngle, baseline = angleAtZero
        });

        // 仮説2 親がそばにいる物に近づく
        {
            float value = ParentNearValue(all, times);
            Bootstrap(random, approaches.Count, idx => ParentNearValue(idx, Select(times, idx)), out float ciLow, out float ciHigh);
            float p = ShiftP(random, times, value, shifted => ParentNearValue(all, shifted), +1);
            ParentNearRates(all, times, out float hit, out float baseRate);
            results.Add(new HypothesisResult
            {
                key = "H2", title = "Child approaches objects near the parent", variant = "position",
                n = approaches.Count, value = value, before = hit, baseline = baseRate, ciLow = ciLow, ciHigh = ciHigh, p = p, lag = float.NaN
            });
        }

        // 仮説3 着く前に物の方を向く(左右だけ / 上下も含む / 参考: 進行方向)
        foreach ((string variant, WindowSeries[] series) in new[]
                 {
                     ("horizontal", aligned.facesTargetH), ("horizontal+vertical", aligned.facesTargetHV), ("reference: walking direction", aligned.movesToTarget)
                 })
        {
            float value = TargetLookValue(series, all, times, out float before, out float baseRate);
            Bootstrap(random, approaches.Count, idx => TargetLookValue(series, idx, Select(times, idx), out _, out _), out float ciLow, out float ciHigh);
            float p = ShiftP(random, times, value, shifted => TargetLookValue(series, all, shifted, out _, out _), +1);
            results.Add(new HypothesisResult
            {
                key = "H3", title = "Child faces the object before arriving", variant = variant,
                n = approaches.Count, value = value, before = before, baseline = baseRate, ciLow = ciLow, ciHigh = ciHigh, p = p, lag = float.NaN
            });
        }

        // 仮説4 親から離れた物へ向かう前に親の方を向く
        List<float> awayList = new List<float>();

        foreach (ApproachEvent e in approaches)
        {
            if (!e.parentNear)
                awayList.Add(e.arriveTime);
        }

        float[] away = awayList.ToArray();

        foreach ((string variant, WindowSeries series) in new[] { ("horizontal", aligned.seesParentH), ("horizontal+vertical", aligned.seesParentHV) })
        {
            float value = ParentLookValue(series, away, out float before, out float baseRate);
            Bootstrap(random, away.Length, idx => ParentLookValue(series, Select(away, idx), out _, out _), out float ciLow, out float ciHigh);
            float p = ShiftP(random, away, value, shifted => ParentLookValue(series, shifted, out _, out _), +1);
            results.Add(new HypothesisResult
            {
                key = "H4", title = "Child looks at the parent before going to an object away from the parent", variant = variant,
                n = away.Length, value = value, before = before, baseline = baseRate, ciLow = ciLow, ciHigh = ciHigh, p = p, lag = float.NaN
            });
        }

        StringBuilder log = new StringBuilder("[HomeGazeBehaviorAnalyzer] 仮説の結果:");

        foreach (HypothesisResult r in results)
            log.Append($"\n  {r.key} {r.variant}: {FormatResult(r)}");

        Debug.Log(log.ToString());
    }

    // 仮説1: 頭の水平の向き(t) と 進行方向(t + lag) のなす角の平均が最も小さくなる lag
    private void ComputeLag(out float bestLag, out float bestAngle, out float angleAtZero, out int movingFrames)
    {
        bestLag = float.NaN;
        bestAngle = float.NaN;
        angleAtZero = float.NaN;
        movingFrames = 0;

        for (int i = 0; i < child.Count; i++)
        {
            if (!float.IsNaN(child.moveYaw[i]))
                movingFrames++;
        }

        float best = float.MaxValue;
        int steps = Mathf.RoundToInt(maxLag / lagStep);

        for (int s = -steps; s <= steps; s++)
        {
            float lag = s * lagStep;
            double sum = 0.0;
            int count = 0;

            for (int i = 0; i < child.Count; i++)
            {
                if (float.IsNaN(child.yaw[i]))
                    continue;

                float move = child.ValueAt(child.moveYaw, child.time[i] + lag, AlignTolerance);

                if (float.IsNaN(move))
                    continue;

                sum += Mathf.Abs(Mathf.DeltaAngle(move, child.yaw[i]));
                count++;
            }

            if (count < 100)
                continue;

            float mean = (float)(sum / count);

            if (s == 0)
                angleAtZero = mean;

            if (mean < best)
            {
                best = mean;
                bestLag = lag;
                bestAngle = mean;
            }
        }
    }

    // 仮説2: 接近のうち親がその物のそばにいた割合 − ふだん親がその物のそばにいる割合
    private float ParentNearValue(int[] idx, float[] times)
    {
        ParentNearRates(idx, times, out float hit, out float baseRate);
        return hit - baseRate;
    }

    private void ParentNearRates(int[] idx, float[] times, out float hit, out float baseRate)
    {
        int hits = 0, count = 0;
        double baseSum = 0.0;

        for (int j = 0; j < idx.Length; j++)
        {
            int k = approaches[idx[j]].target;
            float d = parent.ValueAt(parent.targetDistance[k], times[j], 0.2f);

            if (float.IsNaN(d))
                continue;

            count++;
            hits += d <= parentNearDistance ? 1 : 0;
            baseSum += parent.NearRate(k, parentNearDistance);
        }

        hit = count > 0 ? (float)hits / count : float.NaN;
        baseRate = count > 0 ? (float)(baseSum / count) : float.NaN;
    }

    // 仮説3: 着く前にその物の方を向いていた割合 − ふだんの割合(接近ごとの差の平均)
    private float TargetLookValue(WindowSeries[] series, int[] idx, float[] times, out float before, out float baseRate)
    {
        double diff = 0.0, beforeSum = 0.0, baseSum = 0.0;
        int count = 0;

        for (int j = 0; j < idx.Length; j++)
        {
            WindowSeries s = series[approaches[idx[j]].target];
            float b = s.Mean(times[j], targetLookWindow);

            if (float.IsNaN(b))
                continue;

            diff += b - s.overall;
            beforeSum += b;
            baseSum += s.overall;
            count++;
        }

        before = count > 0 ? (float)(beforeSum / count) : float.NaN;
        baseRate = count > 0 ? (float)(baseSum / count) : float.NaN;
        return count > 0 ? (float)(diff / count) : float.NaN;
    }

    // 仮説4: 着く 6〜2 秒前に親が視野に入っていた割合(接近の平均) − ふだんの割合
    private float ParentLookValue(WindowSeries series, float[] times, out float before, out float baseRate)
    {
        double sum = 0.0;
        int count = 0;

        foreach (float t in times)
        {
            float b = series.Mean(t, parentLookWindow);

            if (float.IsNaN(b))
                continue;

            sum += b;
            count++;
        }

        before = count > 0 ? (float)(sum / count) : float.NaN;
        baseRate = series.overall;
        return before - baseRate;
    }

    private void Bootstrap(System.Random random, int n, System.Func<int[], float> value, out float low, out float high)
    {
        low = float.NaN;
        high = float.NaN;

        if (n == 0)
            return;

        List<float> values = new List<float>();
        int[] idx = new int[n];

        for (int r = 0; r < bootstrapCount; r++)
        {
            for (int j = 0; j < n; j++)
                idx[j] = random.Next(n);

            float v = value(idx);

            if (!float.IsNaN(v))
                values.Add(v);
        }

        if (values.Count == 0)
            return;

        values.Sort();
        low = values[Mathf.Clamp(Mathf.FloorToInt(0.025f * (values.Count - 1)), 0, values.Count - 1)];
        high = values[Mathf.Clamp(Mathf.CeilToInt(0.975f * (values.Count - 1)), 0, values.Count - 1)];
    }

    // 接近の時刻をでたらめにずらして(2人とも映っている時間の中で回して)計算したとき、observed 以上(direction < 0 なら以下)が出る割合
    private float ShiftP(System.Random random, float[] times, float observed, System.Func<float[], float> value, int direction)
    {
        if (float.IsNaN(observed) || times.Length == 0 || pairEnd <= pairStart)
            return float.NaN;

        float span = pairEnd - pairStart;
        float[] shifted = new float[times.Length];
        int count = 0, total = 0;

        for (int r = 0; r < shiftCount; r++)
        {
            float shift = (float)(shiftMin + random.NextDouble() * (shiftMax - shiftMin)) * (random.Next(2) == 0 ? -1f : 1f);

            for (int j = 0; j < times.Length; j++)
                shifted[j] = pairStart + Mod(times[j] - pairStart + shift, span);

            float v = value(shifted);

            if (float.IsNaN(v))
                continue;

            total++;

            if (direction > 0 ? v >= observed : v <= observed)
                count++;
        }

        return total > 0 ? (count + 1f) / (total + 1f) : float.NaN;
    }

    private static float Mod(float a, float b) => a - b * Mathf.Floor(a / b);

    private static int[] Indices(int n)
    {
        int[] idx = new int[n];

        for (int i = 0; i < n; i++)
            idx[i] = i;

        return idx;
    }

    private static float[] Select(float[] values, int[] idx)
    {
        float[] selected = new float[idx.Length];

        for (int j = 0; j < idx.Length; j++)
            selected[j] = values[idx[j]];

        return selected;
    }

    // ------------------------------------------------------------
    // Output
    // ------------------------------------------------------------
    private string CreateResultsCsv()
    {
        StringBuilder csv = new StringBuilder();
        csv.AppendLine("hypothesis,title,variant,n,value,before,baseline,ciLow,ciHigh,p,lagSec");

        foreach (HypothesisResult r in results)
        {
            csv.AppendLine(string.Join(",", r.key, Csv(r.title), Csv(r.variant), r.n.ToString(CultureInfo.InvariantCulture),
                F(r.value), F(r.before), F(r.baseline), F(r.ciLow), F(r.ciHigh), F(r.p), F(r.lag)));
        }

        return csv.ToString();
    }

    private string CreateApproachesCsv()
    {
        StringBuilder csv = new StringBuilder();
        csv.AppendLine("arriveSec,target,parentNear,parentDistanceToTarget,childParentDistance," +
                       "parentInViewH,parentInViewHV,targetInViewH,targetInViewHV,walkingTowardTarget");

        foreach (ApproachEvent e in approaches)
        {
            csv.AppendLine(string.Join(",", F(e.arriveTime), Csv(targets[e.target].name), e.parentNear ? "1" : "0",
                F(e.parentDistance), F(e.childParentDistance), F(e.parentLookH), F(e.parentLookHV),
                F(e.targetLookH), F(e.targetLookHV), F(e.targetLookMove)));
        }

        return csv.ToString();
    }

    // 子供のフレームごとに1行(親は同じ時刻に並べたもの)
    private void WriteFramesCsv(string path)
    {
        using (StreamWriter writer = new StreamWriter(path, false, new UTF8Encoding(true)))
        {
            StringBuilder line = new StringBuilder(
                "time,childPelvisX,childPelvisZ,childHeadX,childHeadY,childHeadZ,childYaw,childPitch,childSpeed,childMoveYaw," +
                "parentHeadX,parentHeadY,parentHeadZ,parentInViewH,parentInViewHV");

            foreach (Target target in targets)
            {
                string n = target.name;
                line.Append($",{Csv("childDist_" + n)},{Csv("parentDist_" + n)},{Csv("inViewH_" + n)},{Csv("inViewHV_" + n)},{Csv("walkingToward_" + n)}");
            }

            writer.WriteLine(line.ToString());

            for (int i = 0; i < child.Count; i++)
            {
                line.Clear();
                line.Append(F(child.time[i]));

                foreach (float v in new[]
                         {
                             child.pelvisX[i], child.pelvisZ[i], child.headX[i], child.headY[i], child.headZ[i], child.yaw[i], child.pitch[i],
                             child.speed[i], child.moveYaw[i], aligned.parentX[i], aligned.parentY[i], aligned.parentZ[i],
                             aligned.seesParentH.values[i], aligned.seesParentHV.values[i]
                         })
                    line.Append(',').Append(F(v));

                for (int k = 0; k < targets.Count; k++)
                {
                    line.Append(',').Append(F(child.targetDistance[k][i]));
                    line.Append(',').Append(F(parent.ValueAt(parent.targetDistance[k], child.time[i], AlignTolerance)));
                    line.Append(',').Append(F(aligned.facesTargetH[k].values[i]));
                    line.Append(',').Append(F(aligned.facesTargetHV[k].values[i]));
                    line.Append(',').Append(F(aligned.movesToTarget[k].values[i]));
                }

                writer.WriteLine(line.ToString());
            }
        }
    }

    private HomeHypothesisParameters CreateParameters()
    {
        HomeHypothesisParameters data = new HomeHypothesisParameters
        {
            experimentName = env.ExperimentName,
            subjectName = subjectName,
            createdAt = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            skeletonSource = SourceName,
            childName = childName,
            parentName = parentName,
            headDirectionMethod = headDirectionMethod.ToString(),
            downwardAngle = downwardAngle,
            viewHalfHorizontal = viewHalfHorizontal,
            viewHalfVertical = viewHalfVertical,
            approachDistance = approachDistance,
            leaveDistance = leaveDistance,
            trackGapSeconds = trackGapSeconds,
            parentNearDistance = parentNearDistance,
            velocitySpan = velocitySpan,
            minMoveSpeed = minMoveSpeed,
            maxSpeed = maxSpeed,
            parentLookWindow = parentLookWindow,
            targetLookWindow = targetLookWindow,
            bootstrapCount = bootstrapCount,
            shiftCount = shiftCount,
            shiftMin = shiftMin,
            shiftMax = shiftMax,
            randomSeed = randomSeed
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
    // Display
    // ------------------------------------------------------------
    private void ShowSample(int index)
    {
        if (index == shownSample)
            return;

        shownSample = index;
        usedRelationLines = 0;
        fanLine.enabled = false;
        trailLine.enabled = false;
        HashSet<int> shown = new HashSet<int>();
        nowText = "";

        if (index >= 0 && index < samples.Count)
        {
            foreach (Person person in samples[index].persons)
            {
                ShowPerson(person);
                shown.Add(person.id);
            }

            if (child != null)
                ShowChild(index);
        }

        foreach (KeyValuePair<int, HomeSkeletonBodyVisual> pair in bodies)
        {
            if (!shown.Contains(pair.Key))
            {
                pair.Value.SetVisible(false);
                gazeLines[pair.Key].enabled = false;
            }
        }

        for (int i = usedRelationLines; i < relationLines.Count; i++)
            relationLines[i].enabled = false;
    }

    // 子供の視野・親や物との関係
    private void ShowChild(int sampleIndex)
    {
        int i = child.IndexOfSample(sampleIndex);
        StringBuilder now = new StringBuilder();
        int parentIndex = parent.IndexOfSample(sampleIndex);

        if (parentIndex >= 0)
        {
            // 親がそばにいる物(床, 青)
            for (int k = 0; k < targets.Count; k++)
            {
                float d = parent.targetDistance[k][parentIndex];

                if (float.IsNaN(d) || d > parentNearDistance)
                    continue;

                Vector3 pelvis = new Vector3(parent.pelvisX[parentIndex], 0f, parent.pelvisZ[parentIndex]);
                HorizontalDistance(targets[k].bounds, pelvis, out Vector3 closest);
                DrawRelation(new Vector3(pelvis.x, 0.02f, pelvis.z), new Vector3(closest.x, 0.02f, closest.z), new Color(0.2f, 0.4f, 1f), relationLineWidth * 1.5f);
            }
        }

        if (i < 0)
        {
            nowText = $"{childName}: not detected now";
            return;
        }

        Vector3 head = new Vector3(child.headX[i], child.headY[i], child.headZ[i]);
        bool parentInViewH = aligned.seesParentH.values[i] > 0.5f;
        bool parentInViewHV = aligned.seesParentHV.values[i] > 0.5f;

        // 視野の扇形(頭の高さ。親が入っていれば黄)
        if (!float.IsNaN(child.yaw[i]) && !float.IsNaN(head.y))
        {
            Color fanColor = parentInViewHV ? Color.yellow : parentInViewH ? new Color(1f, 0.95f, 0.6f) : new Color(1f, 1f, 1f, 0.6f);
            fanLine.enabled = true;
            fanLine.positionCount = FanSegments + 3;
            fanLine.startColor = fanColor;
            fanLine.endColor = fanColor;
            fanLine.SetPosition(0, head);

            for (int s = 0; s <= FanSegments; s++)
            {
                float a = child.yaw[i] - viewHalfHorizontal + 2f * viewHalfHorizontal * s / FanSegments;
                fanLine.SetPosition(s + 1, head + YawToVector(a) * viewFanLength);
            }

            fanLine.SetPosition(FanSegments + 2, head);
        }

        // 子供 → 親(視野に入っているとき)
        if (parentInViewH && !float.IsNaN(aligned.parentY[i]))
        {
            DrawRelation(head, new Vector3(aligned.parentX[i], aligned.parentY[i], aligned.parentZ[i]),
                         parentInViewHV ? Color.yellow : new Color(1f, 0.95f, 0.6f), relationLineWidth * (parentInViewHV ? 1.5f : 0.8f));
        }

        List<string> inView = new List<string>();
        List<string> parentNear = new List<string>();
        string nearest = "";
        float nearestDistance = float.MaxValue;

        for (int k = 0; k < targets.Count; k++)
        {
            bool viewH = aligned.facesTargetH[k].values[i] > 0.5f;
            bool viewHV = aligned.facesTargetHV[k].values[i] > 0.5f;

            // 視野に入っている物(水色)
            if (viewH)
            {
                DrawRelation(head, targets[k].bounds.center, viewHV ? Color.cyan : new Color(0.6f, 0.9f, 0.9f), relationLineWidth * (viewHV ? 1f : 0.5f));
                inView.Add(viewHV ? targets[k].name : targets[k].name + "(h)");
            }

            if (parentIndex >= 0 && parent.targetDistance[k][parentIndex] <= parentNearDistance)
                parentNear.Add(targets[k].name);

            // 子供が近い物(床, 黄 / 着いた距離以内は赤)
            float d = child.targetDistance[k][i];

            if (!float.IsNaN(d) && d < nearestDistance)
            {
                nearestDistance = d;
                nearest = targets[k].name;
            }

            if (!float.IsNaN(d) && d <= showTargetDistance)
            {
                Vector3 pelvis = new Vector3(child.pelvisX[i], 0f, child.pelvisZ[i]);
                HorizontalDistance(targets[k].bounds, pelvis, out Vector3 closest);
                bool arrived = d <= approachDistance;
                DrawRelation(new Vector3(pelvis.x, 0.03f, pelvis.z), new Vector3(closest.x, 0.03f, closest.z),
                             arrived ? Color.red : new Color(1f, 0.85f, 0.2f), relationLineWidth * (arrived ? 2f : 1f));
            }
        }

        // 進行方向(床, 緑)
        if (!float.IsNaN(child.moveYaw[i]))
        {
            Vector3 floor = new Vector3(child.pelvisX[i], 0.03f, child.pelvisZ[i]);
            DrawRelation(floor, floor + new Vector3(child.velX[i], 0f, child.velZ[i]) * moveLineSeconds, new Color(0.2f, 0.9f, 0.3f), relationLineWidth * 1.5f);
        }

        // 過去 trailSeconds 秒の軌跡
        List<Vector3> trail = new List<Vector3>();

        for (int j = i; j >= 0 && child.time[j] >= child.time[i] - trailSeconds; j--)
        {
            if (!float.IsNaN(child.pelvisX[j]))
                trail.Add(new Vector3(child.pelvisX[j], 0.01f, child.pelvisZ[j]));
        }

        if (trail.Count >= 2)
        {
            Color trailColor = ColorOf(childName);
            trailLine.enabled = true;
            trailLine.startColor = trailColor;
            trailLine.endColor = new Color(trailColor.r, trailColor.g, trailColor.b, 0.1f);
            trailLine.positionCount = trail.Count;
            trailLine.SetPositions(trail.ToArray());
        }

        now.AppendLine($"<b>{childName}</b>  speed {(float.IsNaN(child.speed[i]) ? "-" : $"{child.speed[i]:F2} m/s")}");
        now.AppendLine($"  {parentName} in view: {(parentInViewHV ? "yes (h+v)" : parentInViewH ? "horizontal only" : "no")}");
        now.AppendLine($"  objects in view: {(inView.Count > 0 ? string.Join(", ", inView) : "-")}");
        now.AppendLine($"  nearest object: {(nearest == "" ? "-" : $"{nearest} {nearestDistance:F2} m")}");
        now.AppendLine($"  {parentName} near: {(parentNear.Count > 0 ? string.Join(", ", parentNear) : "-")}");

        // 近くの接近
        foreach (ApproachEvent e in approaches)
        {
            float dt = child.time[i] - e.arriveTime;

            if (dt < Mathf.Min(parentLookWindow.x, targetLookWindow.x) - 1f || dt > 3f)
                continue;

            now.AppendLine();
            now.AppendLine($"<b>Approach #{approaches.IndexOf(e) + 1}</b> {targets[e.target].name} at {e.arriveTime:F1} s ({dt:+0.0;-0.0} s)");
            now.AppendLine($"  {(e.parentNear ? "object near the parent (H2)" : "object away from the parent (H4)")}");
            now.AppendLine($"  {parentName} in view {parentLookWindow.x:F0}..{parentLookWindow.y:F0} s: h {Pct(e.parentLookH)} / h+v {Pct(e.parentLookHV)}");
            now.AppendLine($"  object in view {targetLookWindow.x:F0}..{targetLookWindow.y:F0} s: h {Pct(e.targetLookH)} / h+v {Pct(e.targetLookHV)} / walking {Pct(e.targetLookMove)}");
        }

        nowText = now.ToString();
    }

    private Color ColorOf(string name)
    {
        foreach (KeyValuePair<int, string> pair in personNames)
        {
            if (pair.Value == name)
                return personColors.Length > 0 ? personColors[Mathf.Abs(pair.Key) % personColors.Length] : Color.white;
        }

        return Color.white;
    }

    private void ShowPerson(Person person)
    {
        string name = GetPersonName(person.id);
        bool counted = name == childName || name == parentName;
        Color color = counted && personColors.Length > 0 ? personColors[Mathf.Abs(person.id) % personColors.Length] : new Color(0.5f, 0.5f, 0.5f);

        if (!bodies.TryGetValue(person.id, out HomeSkeletonBodyVisual body))
        {
            body = new HomeSkeletonBodyVisual(visualRoot, $"person_{person.id}", color, visualStyle);
            bodies[person.id] = body;
            gazeLines[person.id] = CreateLine($"person_{person.id}_Gaze", Color.Lerp(color, Color.white, 0.5f), gazeLineWidth);
        }

        body.Apply(person.joints);

        LineRenderer gaze = gazeLines[person.id];
        gaze.enabled = person.hasGaze;

        if (person.hasGaze)
        {
            gaze.SetPosition(0, person.origin);
            gaze.SetPosition(1, person.origin + person.direction * gazeLineLength);
        }
    }

    private void DrawRelation(Vector3 from, Vector3 to, Color color, float width)
    {
        if (usedRelationLines >= relationLines.Count)
            relationLines.Add(CreateLine("Relation", color, width));

        LineRenderer line = relationLines[usedRelationLines++];
        line.enabled = true;
        line.positionCount = 2;
        line.SetPosition(0, from);
        line.SetPosition(1, to);
        line.startColor = color;
        line.endColor = color;
        line.startWidth = width;
        line.endWidth = width;
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
    // Text
    // ------------------------------------------------------------
    private void UpdateText()
    {
        if (frameText != null)
        {
            frameText.text = $"{subjectName} ({OutputName})  Time : {playbackTime:F1} / {duration:F1} s\n" +
                             $"child {childName} / parent {parentName}  view ±{viewHalfHorizontal:F0}° (h) ±{viewHalfVertical:F0}° (v)";
        }

        if (scoreText == null)
            return;

        StringBuilder text = new StringBuilder();

        switch (page)
        {
            case Page.Now:
                text.AppendLine("<b>Now</b>");
                text.Append(nowText);
                break;
            case Page.Hypotheses:
                AppendHypothesesPage(text);
                break;
            default:
                AppendApproachesPage(text);
                break;
        }

        scoreText.text = text.ToString();
    }

    private void AppendHypothesesPage(StringBuilder text)
    {
        text.AppendLine("<b>Hypotheses</b> (diff = window - usual, p = time-shift test)");
        string lastKey = "";

        foreach (HypothesisResult r in results)
        {
            if (r.key != lastKey)
            {
                text.AppendLine($"<b>{r.key}</b> {r.title}");
                lastKey = r.key;
            }

            text.AppendLine($"  {r.variant}: {FormatResult(r)}");
        }
    }

    private string FormatResult(HypothesisResult r)
    {
        if (!float.IsNaN(r.lag) || r.key == "H1")
            return float.IsNaN(r.lag) ? "-" : $"lag {r.lag:+0.0;-0.0} s (angle {r.before:F1}° vs {r.baseline:F1}° at 0 s, {r.n} moving frames)";

        return $"{Pct(r.before)} vs usual {Pct(r.baseline)} = {Points(r.value)} " +
               $"[{Points(r.ciLow)}, {Points(r.ciHigh)}] p={(float.IsNaN(r.p) ? "-" : r.p.ToString("0.000", CultureInfo.InvariantCulture))} (n={r.n})";
    }

    private void AppendApproachesPage(StringBuilder text)
    {
        text.AppendLine($"<b>Approaches</b> {approaches.Count} (blue = near parent, red = away)");

        for (int j = 0; j < approaches.Count; j++)
        {
            ApproachEvent e = approaches[j];
            string mark = j == currentApproach ? ">" : " ";
            string color = e.parentNear ? "#4060ff" : "#ff4040";
            text.AppendLine($"{mark}<color={color}>#{j + 1} {e.arriveTime:F1} s {targets[e.target].name}</color>" +
                            $"  parent {Pct(e.parentLookHV)}  object {Pct(e.targetLookHV)}");
        }
    }

    private static string Pct(float v) => float.IsNaN(v) ? "-" : $"{v * 100f:F0}%";

    private static string Points(float v) => float.IsNaN(v) ? "-" : $"{v * 100f:+0;-0;0} pt";

    // ------------------------------------------------------------
    // Timeline (画面下)
    // ------------------------------------------------------------
    //   [>]  12.3 / 60.0 s  [=========o--------|---|-----]   … 再生バー(印: 青 = 親がそばの物への接近 / 赤 = 親から離れた物への接近)
    //   [< Prev] [Next >]  Approach #3 ...                [Page: Hypotheses]
    private void SetupTimeline()
    {
        Canvas canvas = frameText != null ? frameText.canvas : FindObjectOfType<Canvas>();

        if (canvas == null)
            return;

#if UNITY_EDITOR
        uiSprite = UnityEditor.AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
#endif

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
        AddApproachMarkers((RectTransform)timelineSlider.transform.Find("Background"));

        RectTransform row2 = CreateRow(root, 48f, 44f);
        Button prev = CreateButton(row2, "< Prev", 10f, 100f, out _);
        prev.onClick.AddListener(() => JumpApproach(-1));
        Button next = CreateButton(row2, "Next >", 115f, 100f, out _);
        next.onClick.AddListener(() => JumpApproach(+1));

        approachText = CreateText("Approach", row2, TextAlignmentOptions.MidlineLeft);
        PlaceLeft(approachText.rectTransform, 230f, 680f);

        Button pageButton = CreateButton(row2, "", 920f, 230f, out pageText);
        pageButton.onClick.AddListener(NextPage);

        UpdateTimelineUI();
    }

    private void AddApproachMarkers(RectTransform background)
    {
        float max = Mathf.Max(duration, 0.001f);

        foreach (ApproachEvent e in approaches)
        {
            RectTransform marker = CreateRect("Approach", background);
            float x = Mathf.Clamp01(e.arriveTime / max);
            marker.anchorMin = new Vector2(x, -0.4f);
            marker.anchorMax = new Vector2(x, 1.4f);
            marker.sizeDelta = new Vector2(3f, 0f);
            marker.anchoredPosition = Vector2.zero;
            AddImage(marker.gameObject, null, e.parentNear ? new Color(0.2f, 0.4f, 1f, 0.9f) : new Color(0.9f, 0.1f, 0.1f, 0.9f)).raycastTarget = false;
        }
    }

    private void UpdateTimelineUI()
    {
        if (timelineSlider == null)
            return;

        timelineSlider.SetValueWithoutNotify(playbackTime);
        playPauseText.text = isPlaying ? "||" : ">";
        timeText.text = $"{playbackTime:F1} / {duration:F1} s  ({HomeSkeletonIO.FormatClock(playbackTime)} / {HomeSkeletonIO.FormatClock(duration)})";
        pageText.text = $"Page: {page}";

        if (currentApproach >= 0 && currentApproach < approaches.Count)
        {
            ApproachEvent e = approaches[currentApproach];
            approachText.text = $"Approach #{currentApproach + 1}/{approaches.Count}: {targets[e.target].name} at {e.arriveTime:F1} s " +
                                $"({(e.parentNear ? "near parent" : "away from parent")})";
        }
        else
        {
            approachText.text = $"{approaches.Count} approaches (Prev / Next to jump)";
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

    // Unity の既定のスライダーと同じ構成 (Background / Fill Area / Handle Slide Area)
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
        public bool hasHead;
        public Vector3 head;
        public bool hasPelvis;
        public Vector3 pelvis;
    }

    private class Target
    {
        public string name;
        // メッシュの頂点を囲む箱(距離と方向に使う)
        public Bounds bounds;
    }

    // 1人の時系列。値が求まらないときは NaN
    private class Track
    {
        public readonly int Count;
        public readonly float[] time;
        public readonly int[] sampleIndex;
        public readonly float[] pelvisX, pelvisZ, headX, headY, headZ, yaw, pitch, speed, velX, velZ, moveYaw;
        public float[][] targetDistance;
        private readonly Dictionary<int, float> nearRates = new Dictionary<int, float>();

        public Track(int n)
        {
            Count = n;
            time = new float[n];
            sampleIndex = new int[n];
            pelvisX = new float[n];
            pelvisZ = new float[n];
            headX = new float[n];
            headY = new float[n];
            headZ = new float[n];
            yaw = new float[n];
            pitch = new float[n];
            speed = new float[n];
            velX = new float[n];
            velZ = new float[n];
            moveYaw = new float[n];
        }

        // query に最も近いフレームの値(tolerance 秒より離れていれば NaN)
        public float ValueAt(float[] values, float query, float tolerance)
        {
            int i = Nearest(query);
            return i >= 0 && Mathf.Abs(time[i] - query) <= tolerance ? values[i] : float.NaN;
        }

        public int Nearest(float query)
        {
            if (Count == 0)
                return -1;

            int i = LowerBound(time, query);

            if (i >= Count)
                return Count - 1;

            if (i > 0 && query - time[i - 1] <= time[i] - query)
                return i - 1;

            return i;
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

        // 物 k の near m 以内にいた割合(記録全体。求まらないフレームは除く)
        public float NearRate(int k, float near)
        {
            if (nearRates.TryGetValue(k, out float rate))
                return rate;

            int hits = 0, count = 0;

            foreach (float d in targetDistance[k])
            {
                if (float.IsNaN(d))
                    continue;

                count++;
                hits += d <= near ? 1 : 0;
            }

            rate = count > 0 ? (float)hits / count : float.NaN;
            nearRates[k] = rate;
            return rate;
        }
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

    private static int UpperBound(float[] values, float query)
    {
        int low = 0, high = values.Length;

        while (low < high)
        {
            int mid = (low + high) / 2;

            if (values[mid] <= query)
                low = mid + 1;
            else
                high = mid;
        }

        return low;
    }

    // 子供の時刻ごとの 0/1(NaN は不明)と、時間帯の平均を速く求めるための累積和
    private class WindowSeries
    {
        public readonly float[] values;
        public readonly float overall;
        private readonly float[] time;
        private readonly double[] cumulative;
        private readonly int[] counts;

        public WindowSeries(float[] time, float[] values)
        {
            this.time = time;
            this.values = values;
            cumulative = new double[values.Length + 1];
            counts = new int[values.Length + 1];

            for (int i = 0; i < values.Length; i++)
            {
                bool ok = !float.IsNaN(values[i]);
                cumulative[i + 1] = cumulative[i] + (ok ? values[i] : 0.0);
                counts[i + 1] = counts[i] + (ok ? 1 : 0);
            }

            int n = counts[values.Length];
            overall = n > 0 ? (float)(cumulative[values.Length] / n) : float.NaN;
        }

        // [center + window.x, center + window.y] の平均
        public float Mean(float center, Vector2 window)
        {
            int lo = LowerBound(time, center + window.x);
            int hi = UpperBound(time, center + window.y);
            int n = counts[hi] - counts[lo];
            return n > MinWindowFrames ? (float)((cumulative[hi] - cumulative[lo]) / n) : float.NaN;
        }
    }

    // 子供の時刻に並べた親と、視野に入っているか
    private class ChildAligned
    {
        public readonly float[] parentX, parentY, parentZ;
        public readonly WindowSeries seesParentH, seesParentHV;
        public readonly WindowSeries[] facesTargetH, facesTargetHV, movesToTarget;

        public ChildAligned(HomeGazeBehaviorAnalyzer owner)
        {
            Track c = owner.child;
            Track p = owner.parent;
            int n = c.Count;
            parentX = new float[n];
            parentY = new float[n];
            parentZ = new float[n];
            float[] sh = new float[n], shv = new float[n];

            for (int i = 0; i < n; i++)
            {
                int j = p.Nearest(c.time[i]);
                bool ok = j >= 0 && Mathf.Abs(p.time[j] - c.time[i]) <= AlignTolerance;
                parentX[i] = ok ? p.headX[j] : float.NaN;
                // 親の頭の高さが無ければ 1.4 m とする
                parentY[i] = ok ? (float.IsNaN(p.headY[j]) ? 1.4f : p.headY[j]) : float.NaN;
                parentZ[i] = ok ? p.headZ[j] : float.NaN;
                owner.InView(c, i, new Vector3(parentX[i], parentY[i], parentZ[i]), c.yaw[i], out sh[i], out shv[i]);
            }

            seesParentH = new WindowSeries(c.time, sh);
            seesParentHV = new WindowSeries(c.time, shv);

            int targetCount = owner.targets.Count;
            facesTargetH = new WindowSeries[targetCount];
            facesTargetHV = new WindowSeries[targetCount];
            movesToTarget = new WindowSeries[targetCount];

            for (int k = 0; k < targetCount; k++)
            {
                Vector3 center = owner.targets[k].bounds.center;
                float[] fh = new float[n], fhv = new float[n], fm = new float[n];

                for (int i = 0; i < n; i++)
                {
                    owner.InView(c, i, center, c.yaw[i], out fh[i], out fhv[i]);
                    owner.InView(c, i, center, c.moveYaw[i], out fm[i], out _);
                }

                facesTargetH[k] = new WindowSeries(c.time, fh);
                facesTargetHV[k] = new WindowSeries(c.time, fhv);
                movesToTarget[k] = new WindowSeries(c.time, fm);
            }
        }
    }

    // 子供の頭から point が視野に入っているか(1/0。求まらなければ NaN)。yaw は向き(頭の向きか進行方向)
    private void InView(Track c, int i, Vector3 point, float yaw, out float horizontal, out float ellipse)
    {
        horizontal = float.NaN;
        ellipse = float.NaN;
        float dx = point.x - c.headX[i];
        float dz = point.z - c.headZ[i];

        if (float.IsNaN(dx) || float.IsNaN(dz) || float.IsNaN(yaw))
            return;

        float dYaw = Mathf.DeltaAngle(yaw, Mathf.Atan2(dx, dz) * Mathf.Rad2Deg);
        horizontal = Mathf.Abs(dYaw) <= viewHalfHorizontal ? 1f : 0f;

        float dy = point.y - c.headY[i];

        if (float.IsNaN(dy) || float.IsNaN(c.pitch[i]))
            return;

        float dPitch = Mathf.Atan2(dy, Mathf.Sqrt(dx * dx + dz * dz)) * Mathf.Rad2Deg - c.pitch[i];
        float e = Sq(dYaw / viewHalfHorizontal) + Sq(dPitch / viewHalfVertical);
        ellipse = e <= 1f ? 1f : 0f;
    }

    private class ApproachEvent
    {
        public int target;
        public float arriveTime;
        public int childIndex;
        public bool parentNear;
        public float parentDistance;
        public float childParentDistance;
        // 時間帯(仮説4 / 仮説3)の、視野に入っていた割合
        public float parentLookH, parentLookHV;
        public float targetLookH, targetLookHV, targetLookMove;
    }

    private class HypothesisResult
    {
        public string key;
        public string title;
        public string variant;
        public int n;
        public float value;
        public float before;
        public float baseline;
        public float ciLow = float.NaN;
        public float ciHigh = float.NaN;
        public float p = float.NaN;
        public float lag = float.NaN;
    }
}

// シーン6で保存するパラメータ(gaze_behavior_<骨格>_hypotheses_params.json)
[System.Serializable]
public class HomeHypothesisParameters
{
    public string experimentName;
    public string subjectName;
    public string createdAt;
    public string skeletonSource;
    public string childName;
    public string parentName;
    public string headDirectionMethod;
    public float downwardAngle;
    public float viewHalfHorizontal;
    public float viewHalfVertical;
    public float approachDistance;
    public float leaveDistance;
    public float trackGapSeconds;
    public float parentNearDistance;
    public float velocitySpan;
    public float minMoveSpeed;
    public float maxSpeed;
    public Vector2 parentLookWindow;
    public Vector2 targetLookWindow;
    public int bootstrapCount;
    public int shiftCount;
    public float shiftMin;
    public float shiftMax;
    public int randomSeed;
    public List<string> targets = new List<string>();
}
