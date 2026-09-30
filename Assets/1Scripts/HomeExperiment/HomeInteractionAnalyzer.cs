using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Azure.Kinect.BodyTracking;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// シーン5(5InteractionAnalyze)用。骨格データの視線(シーン4と同じ補正後の頭部方向)から、
// 人物どうし・対象物体とのやり取りを時間範囲ごとに数える。
//
//   Targets(対象物体の注視)   … 人物ごとに、どの対象物体を何秒・何回見たか(シーン4の注視対象と同じ判定)
//   Joint attention(注視対象の一致) … 2人が同時に同じ対象物体を見ていた時間・回数(対象ごと・2人の組ごと)
//   Look at person(相手の方を見る)  … 視線が相手の体(頭〜骨盤)/ 顔(頭)の方向を向いていた時間
//   Mutual gaze(目を合わせる)       … 2人が同時にお互いの顔を見ていた時間・回数
//   Face to face(向かい合い)         … 2人が近く(faceToFaceDistance 以内)で向かい合っていた時間。会話の姿勢の目安
//                                     (骨格だけでは声は分からないので、話しているかどうかまでは判定しない)
//
// 骨格は、シーン3で作った人物ごとの骨格(Filtered/grouped_<Mode>.json)を読むだけ。人物 = シーン3のグループで、
// 信頼度の比較・人物IDのまとめ方(グループに入っていない人物IDを外すこと)はシーン3で済ませてある。
// 対象物体はシーン4と同じく、シーン0で置いた対象物体(Env/TargetObjects.json)と名前で指定したもの。
//
// 操作:
//   Play / Stop(Space) … 再生 / 一時停止。再生位置の骨格と、その瞬間の関係を線で表示する
//                         (黄の太線: 目を合わせている / 人物の色の線: 相手の顔を見ている / 白っぽい細線: 相手の体を見ている /
//                          マゼンタ: 同じ対象物体を見ている / 人物の色の細い線: 見ている対象物体 / 水色(床の上): 向かい合っている)
//   From / to, Full     … 数える時間範囲
//   Page                … 右の集計の表示を Targets / Joint attention / Persons で切り替える
//   Analyze All         … 時間範囲をデータ全体にして保存
//   Save                … 今の時間範囲の結果を Analysis/Interaction/interaction_<骨格>.json と CSV に保存
[RequireComponent(typeof(HomeEnvLoader))]
public class HomeInteractionAnalyzer : MonoBehaviour
{
    public enum MeasureType
    {
        TargetGaze,
        JointAttention,
        LookAtPerson,
        LookAtFace,
        MutualGaze,
        FaceToFace
    }

    public enum Page
    {
        Targets,
        JointAttention,
        Persons
    }

    [Header("Subject")]
    [HomeExperimentFolder(HomeExperimentFolderAttribute.Kind.Subject)]
    [SerializeField] private string subjectName = "";

    [Header("Skeleton (シーン4と同じ)")]
    [SerializeField] private HomeGazeAnalyzer.SkeletonSource skeletonSource = HomeGazeAnalyzer.SkeletonSource.Filtered;
    [Tooltip("Filtered のとき: 読み込む filtered_<Mode>.json")]
    [SerializeField] private HomeSkeletonFilter.ConfidenceMode filteredMode = HomeSkeletonFilter.ConfidenceMode.HeadJoints;
    [Tooltip("Kinect のとき: 読み込む <ID>_skeleton.json のキネクトID")]
    [SerializeField] private string kinectId = "A";
    [Tooltip("Image のとき: 視線として使う向き")]
    [SerializeField] private HomeGazeAnalyzer.ImageDirection imageDirection = HomeGazeAnalyzer.ImageDirection.GazeElseHead;
    [Tooltip("次のフレームまでの間隔がこれより長いときは、フレームの時間をこの秒数で打ち切る(記録の抜けを時間に数えない)。")]
    [SerializeField] private float maxFrameDuration = 0.2f;

    [Header("Gaze (シーン4と同じ)")]
    [SerializeField] private HomeGazeAnalyzer.HeadDirectionMethod headDirectionMethod = HomeGazeAnalyzer.HeadDirectionMethod.HeadToNose;
    [Tooltip("頭部方向を下へ補正する角度(度)")]
    [SerializeField] private float downwardAngle = 24.4f;
    [Tooltip("「見ている」とするコーンの角度(度, 中心軸からの半角)。対象物体の注視・相手の体/顔を見ている・目を合わせている の判定すべてに使う")]
    [SerializeField] private float coneAngle = 10.14f;
    [Tooltip("コーンの長さ(m)")]
    [SerializeField] private float coneDistance = 5f;

    [Header("Targets (シーン4と同じ)")]
    [Tooltip("シーン0で保存した対象物体(Env/TargetObjects.json)を対象にする。")]
    [SerializeField] private bool useEnvTargetObjects = true;
    [Tooltip("対象にする部屋オブジェクト/対象物体(またはその子)の名前")]
    [SerializeField] private string[] targetNames = new string[0];
    [Tooltip("対象にするシーン上のオブジェクト(部屋オブジェクト以外に置いた箱など)")]
    [SerializeField] private GameObject[] targetObjects = new GameObject[0];

    [Header("Person")]
    [Tooltip("相手がこれより遠いときは見ているとしない(m)")]
    [SerializeField] private float lookAtPersonDistance = 5f;
    [Tooltip("水平面で、自分の向きと相手への方向のなす角が2人ともこれ以下なら向かい合っているとする(度)")]
    [SerializeField] private float faceToFaceAngle = 30f;
    [Tooltip("向かい合っているとする2人の頭の間の水平距離の上限(m)")]
    [SerializeField] private float faceToFaceDistance = 2f;
    [Tooltip("映っていた時間の合計がこれより短い人物(誤検出など)は数えない(秒)。grouped の骨格はシーン3でまとめた人物だけなので、主に Kinect のとき用")]
    [SerializeField] private float minPersonSeconds = 1f;

    [Header("Episodes")]
    [Tooltip("途切れがこれ以下(秒)なら1回の出来事として続ける")]
    [SerializeField] private float mergeGapSeconds = 0.3f;
    [Tooltip("これより短い出来事(秒)は回数に数えない(時間には数える)")]
    [SerializeField] private float minEpisodeSeconds = 0.5f;

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
    [Tooltip("人物どうし・対象物体との関係を表す線の太さ")]
    [SerializeField] private float relationLineWidth = 0.02f;
    [Tooltip("骨格と視線の線を表示する人物ID(1人だけ)。-1 なら全員。画面右上の「Skeleton」の一覧でも選べ、\n" +
             "選んだ人物がその時刻に映っていなければ、最初に出てくる時刻へ移動する。表示だけで、解析は全員分を行う。")]
    [SerializeField] private int skeletonPersonId = -1;

    [Header("Playback")]
    [SerializeField] private float playbackSpeed = 1f;
    [SerializeField] private KeyCode playStopKey = KeyCode.Space;

    [Header("Range")]
    [Tooltip("数える時間範囲の始まり(秒)")]
    [SerializeField] private float rangeStart = 0f;
    [Tooltip("数える時間範囲の終わり(秒)。負の値ならデータの最後まで")]
    [SerializeField] private float rangeEnd = -1f;
    [SerializeField] private Page page = Page.Targets;

    [Header("Output")]
    [Tooltip("出力ファイル名の末尾に付ける文字(パラメータ違いで保存し分けるとき)。例: interaction_filtered_HeadJoints_<label>.json")]
    [SerializeField] private string outputLabel = "";

    [Header("UI")]
    [SerializeField] private Button playButton;
    [SerializeField] private Button stopButton;
    [SerializeField] private Button analyzeAllButton;
    [SerializeField] private Button saveButton;
    [SerializeField] private TMP_Text frameText;
    [SerializeField] private TMP_Text scoreText;

    // 対象物体の注視の計算(Update で少しずつ進める)に1フレームで使う時間(秒)
    private const float PrecomputeBudget = 0.02f;
    private const float NoTargetAngle = -1f;

    private static readonly JointId[] BodyJoints =
    {
        JointId.Head, JointId.Neck, JointId.SpineChest, JointId.SpineNavel, JointId.Pelvis
    };

    private HomeEnvLoader env;
    private readonly List<Sample> samples = new List<Sample>();
    private readonly List<Target> targets = new List<Target>();
    // 人物IDごとの映っていた時間(秒)
    private readonly SortedDictionary<int, float> personSeconds = new SortedDictionary<int, float>();
    // 数える人物と、人物ID → その番号
    private readonly List<Unit> units = new List<Unit>();
    // 人物IDの名前(grouped の骨格ではシーン3のグループ名)
    private readonly Dictionary<int, string> personNames = new Dictionary<int, string>();
    // 人物IDが最初に出てくる時刻(秒)
    private readonly Dictionary<int, float> personFirstTimes = new Dictionary<int, float>();
    // 骨格を表示する人物の一覧(1人だけ選ぶ)
    private RectTransform skeletonListRect;
    private RectTransform skeletonListContent;
    private TMP_Text skeletonHeaderText;
    private Toggle skeletonAllToggle;
    private readonly List<KeyValuePair<int, Toggle>> skeletonToggles = new List<KeyValuePair<int, Toggle>>();
    private int appliedSkeletonPersonId = -1;
    private readonly Dictionary<int, int> unitOfPerson = new Dictionary<int, int>();

    // samples[0] 〜 samples[precomputed - 1] は注視している対象物体を求めてある
    private int precomputed;
    private bool IsReady => precomputed >= samples.Count;

    private float duration;
    private float playbackTime;
    private bool isPlaying;
    private int shownSample = -1;

    // 集計(時間範囲 samples[analyzedStart] 〜 samples[analyzedEnd - 1])
    private readonly Dictionary<long, Accumulator> results = new Dictionary<long, Accumulator>();
    private int analyzedStart = -1;
    private int analyzedEnd = -1;
    private bool analysisDirty = true;
    private float rangeSeconds;
    private float[] unitPresentSeconds = new float[0];
    private float[] unitGazeSeconds = new float[0];
    private float[,] coPresentSeconds = new float[0, 0];

    // 表示
    private Transform visualRoot;
    private HomeSkeletonBodyVisual.Style visualStyle;
    private readonly Dictionary<int, HomeSkeletonBodyVisual> bodies = new Dictionary<int, HomeSkeletonBodyVisual>();
    private readonly Dictionary<int, LineRenderer> gazeLines = new Dictionary<int, LineRenderer>();
    private readonly List<LineRenderer> relationLines = new List<LineRenderer>();
    private int usedRelationLines;
    private Material sharedLineMaterial;
    private string nowText = "";

    // 画面下のバー
    private Sprite uiSprite;
    private Slider timelineSlider;
    private Slider rangeStartSlider;
    private Slider rangeEndSlider;
    private TMP_Text playPauseText;
    private TMP_Text timeText;
    private TMP_Text rangeStartText;
    private TMP_Text rangeEndText;
    private TMP_Text pageText;
    private RectTransform rangeHighlight;
    private bool isScrubbing;
    private Sprite checkmarkSprite;

    private string SourceName =>
        skeletonSource == HomeGazeAnalyzer.SkeletonSource.Filtered ? $"filtered_{filteredMode}" :
        skeletonSource == HomeGazeAnalyzer.SkeletonSource.Image ? $"image_{imageDirection}" :
        $"Kinect{kinectId.Trim()}";

    private string OutputName =>
        string.IsNullOrWhiteSpace(outputLabel) ? SourceName : $"{SourceName}_{outputLabel.Trim()}";

    private void Awake()
    {
        env = GetComponent<HomeEnvLoader>();
    }

    private void Start()
    {
        visualRoot = new GameObject("InteractionPlayback").transform;
        visualStyle = new HomeSkeletonBodyVisual.Style
        {
            jointPrefab = jointPrefab,
            lineMaterial = lineMaterial,
            jointScale = jointScale,
            lineWidth = lineWidth,
            showHeadDirection = false
        };
        sharedLineMaterial = lineMaterial != null ? lineMaterial : new Material(Shader.Find("Sprites/Default"));

        // 以前 Analysis/ の直下に置いていた解析結果を種類ごとのフォルダへ移す
        HomeExperimentPaths.MigrateLegacyAnalysisFiles(env.ExperimentName, subjectName);
        LoadSamples();
        BuildUnits();
        SetupTargets();
        SetupTimeline();
        SetupSkeletonSelector(frameText != null ? frameText.canvas : FindObjectOfType<Canvas>(), 0);

        if (playButton != null)
            playButton.onClick.AddListener(Play);

        if (stopButton != null)
            stopButton.onClick.AddListener(Stop);

        if (analyzeAllButton != null)
            analyzeAllButton.onClick.AddListener(AnalyzeAll);

        if (saveButton != null)
            saveButton.onClick.AddListener(Save);

        ShowSample(FindSampleIndex(playbackTime));
        UpdateText();
    }

    private void Update()
    {
        if (Input.GetKeyDown(playStopKey))
            TogglePlay();

        if (skeletonPersonId != appliedSkeletonPersonId)
            SetSkeletonPerson(skeletonPersonId);

        if (isPlaying)
        {
            playbackTime += Time.deltaTime * playbackSpeed;

            if (playbackTime >= duration)
            {
                playbackTime = duration;
                isPlaying = false;
            }
        }

        StepPrecompute();

        if (IsReady && !isScrubbing)
        {
            GetRangeIndices(out int start, out int end);

            if (analysisDirty || start != analyzedStart || end != analyzedEnd)
                RunAnalysis(start, end);
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
        {
            Debug.LogWarning("[HomeInteractionAnalyzer] 解析できる骨格データがありません。");
            return;
        }

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

    // 数える時間範囲を決める(end が負ならデータの最後まで)
    public void SetRange(float start, float end)
    {
        rangeStart = Mathf.Max(0f, start);
        rangeEnd = end;
    }

    public void NextPage()
    {
        page = (Page)(((int)page + 1) % System.Enum.GetValues(typeof(Page)).Length);
    }

    // 時間範囲をデータ全体にして保存する
    [ContextMenu("Analyze All")]
    public void AnalyzeAll()
    {
        SetRange(0f, -1f);
        Save();
    }

    [ContextMenu("Save")]
    public void Save()
    {
        if (!HomeExperimentPaths.IsValidFolderName(env.ExperimentName, out _) ||
            !HomeExperimentPaths.IsValidFolderName(subjectName, out _))
        {
            Debug.LogError("[HomeInteractionAnalyzer] 実験の名前/実験対象者が正しくないため保存できません。");
            return;
        }

        // 対象物体の注視の計算が終わっていなければ、ここで最後まで計算する
        FinishPrecompute();
        GetRangeIndices(out int start, out int end);
        RunAnalysis(start, end);

        string jsonPath = HomeExperimentPaths.GetInteractionPath(env.ExperimentName, subjectName, OutputName);
        string summaryPath = HomeExperimentPaths.GetInteractionSummaryCsvPath(env.ExperimentName, subjectName, OutputName);
        string episodesPath = HomeExperimentPaths.GetInteractionEpisodesCsvPath(env.ExperimentName, subjectName, OutputName);

        try
        {
            HomeInteractionList data = CreateOutput();
            Directory.CreateDirectory(Path.GetDirectoryName(jsonPath));
            File.WriteAllText(jsonPath, JsonUtility.ToJson(data, true));
            // Excel で文字化けしないように BOM 付きの UTF-8 で書く
            File.WriteAllText(summaryPath, CreateSummaryCsv(data), new UTF8Encoding(true));
            File.WriteAllText(episodesPath, CreateEpisodesCsv(data), new UTF8Encoding(true));
            HomeExperimentPaths.RefreshAssetDatabase();
            Debug.Log($"[HomeInteractionAnalyzer] {data.parameters.rangeStartSec:F1}〜{data.parameters.rangeEndSec:F1} 秒の結果を保存しました:\n" +
                      $"  {jsonPath}\n  {summaryPath}\n  {episodesPath}");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[HomeInteractionAnalyzer] 保存に失敗しました: {jsonPath}\n{e}");
        }
    }

    // ------------------------------------------------------------
    // Load skeleton (シーン4と同じ)
    // ------------------------------------------------------------
    private void LoadSamples()
    {
        samples.Clear();
        personSeconds.Clear();
        personNames.Clear();
        personFirstTimes.Clear();

        if (!HomeExperimentPaths.IsValidFolderName(env.ExperimentName, out string error))
        {
            Debug.LogError($"[HomeInteractionAnalyzer] 実験の名前を正しく入力してください。{error}");
            return;
        }

        if (!HomeExperimentPaths.IsValidFolderName(subjectName, out error))
        {
            Debug.LogError($"[HomeInteractionAnalyzer] 実験対象者を正しく入力してください。{error}");
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

                SetupBodyPoints(person);
                personSeconds.TryGetValue(person.id, out float seconds);
                personSeconds[person.id] = seconds + samples[i].duration;

                if (!personFirstTimes.ContainsKey(person.id))
                    personFirstTimes[person.id] = samples[i].time;
            }
        }

        duration = samples.Count > 0 ? samples[samples.Count - 1].time : 0f;

        StringBuilder text = new StringBuilder();
        text.Append($"[HomeInteractionAnalyzer] {SourceName}: {samples.Count} samples, {duration:F1}s を読み込みました。人物ID {personSeconds.Count} 個");

        foreach (KeyValuePair<int, float> pair in personSeconds)
            text.Append($"\n  person_{pair.Key}: {pair.Value:F1} s");

        Debug.Log(text.ToString());
    }

    private void LoadFiltered(string modeName)
    {
        // シーン3で作った人物ごとの骨格
        string path = HomeExperimentPaths.GetGroupedSkeletonPath(env.ExperimentName, subjectName, modeName);

        if (!File.Exists(path))
        {
            Debug.LogError($"[HomeInteractionAnalyzer] 人物ごとの骨格のファイルがありません。シーン3でグループを作って Save grouped を押してください: {path}");
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
                    // 画像で推定した頭の位置と向きをそのまま使う(関節からの計算・下向きの補正はしない)
                    bool useGaze = imageDirection != HomeGazeAnalyzer.ImageDirection.Head && person.hasGazeDirection;
                    bool useHead = imageDirection != HomeGazeAnalyzer.ImageDirection.GazeOnly && person.hasHeadDirection;

                    p.fixedGaze = true;
                    p.origin = person.headPosition;
                    p.hasGaze = useGaze || useHead;
                    p.direction = useGaze ? person.gazeDirection.normalized : person.headDirection.normalized;
                    p.usedEyeGaze = useGaze;
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
        foreach (HomeSkeletonTrack track in HomeSkeletonIO.LoadSubject(env.ExperimentName, subjectName, "HomeInteractionAnalyzer"))
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

        Debug.LogError($"[HomeInteractionAnalyzer] Kinect{kinectId} の骨格データがありません。");
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

    // 見られる側の頭の位置と、体(頭〜骨盤)の線上の点
    private static void SetupBodyPoints(Person person)
    {
        if (!person.hasHead && person.joints != null && HomeSkeletonIO.TryGetJointPosition(person.joints, JointId.Head, out Vector3 head))
        {
            person.hasHead = true;
            person.head = head;
        }

        if (person.joints == null)
            return;

        List<Vector3> joints = new List<Vector3>();

        if (person.hasHead)
            joints.Add(person.head);

        foreach (JointId jointId in BodyJoints)
        {
            if (jointId != JointId.Head && HomeSkeletonIO.TryGetJointPosition(person.joints, jointId, out Vector3 position))
                joints.Add(position);
        }

        if (joints.Count == 0)
            return;

        // 関節の間も 1/4 ずつ点を置く(体のどこかを見ていれば相手の方を見ているとする)
        List<Vector3> points = new List<Vector3>();

        for (int i = 0; i + 1 < joints.Count; i++)
        {
            for (int k = 0; k < 4; k++)
                points.Add(Vector3.Lerp(joints[i], joints[i + 1], k / 4f));
        }

        points.Add(joints[joints.Count - 1]);
        person.body = points.ToArray();
    }

    // ------------------------------------------------------------
    // Persons (人物のまとめ方はシーン3で済ませてある。grouped の骨格では人物ID = シーン3のグループの番号)
    // ------------------------------------------------------------
    private void BuildUnits()
    {
        units.Clear();
        unitOfPerson.Clear();

        foreach (int id in personSeconds.Keys)
            AddUnit(GetPersonName(id), id);

        StringBuilder text = new StringBuilder($"[HomeInteractionAnalyzer] 数える人物: {units.Count} 人");

        foreach (Unit unit in units)
            text.Append($"\n  {unit.name}: {unit.seconds:F1} s");

        Debug.Log(text.ToString());
    }

    private void AddUnit(string name, int id)
    {
        personSeconds.TryGetValue(id, out float seconds);

        // 短い人物(誤検出など)は数えない(骨格は灰色で表示する)
        if (seconds < minPersonSeconds)
            return;

        int index = units.Count;
        units.Add(new Unit
        {
            name = name,
            ids = new[] { id },
            seconds = seconds,
            // シーン3のグループの色と同じ並び(人物IDの順)
            color = personColors.Length > 0 ? personColors[Mathf.Abs(id) % personColors.Length] : Color.white
        });

        unitOfPerson[id] = index;
    }

    // 人物の名前(grouped の骨格ではシーン3のグループ名、それ以外は person_<ID>)
    private string GetPersonName(int id) =>
        personNames.TryGetValue(id, out string name) && !string.IsNullOrEmpty(name) ? name : $"person_{id}";

    // ------------------------------------------------------------
    // Targets (シーン4のスコアの対象と同じ)
    // ------------------------------------------------------------
    private void SetupTargets()
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
                {
                    Debug.LogWarning($"[HomeInteractionAnalyzer] 対象が部屋オブジェクト/対象物体にありません: {targetName}");
                    continue;
                }

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

        Debug.Log($"[HomeInteractionAnalyzer] 対象物体: {targets.Count} 個");
    }

    private void AddTarget(string targetName, List<GameObject> objects)
    {
        List<VertexCloud> clouds = new List<VertexCloud>();
        Bounds bounds = new Bounds();
        bool hasBounds = false;

        foreach (GameObject obj in objects)
        {
            foreach (MeshFilter meshFilter in obj.GetComponentsInChildren<MeshFilter>())
            {
                if (!EnsureReadable(meshFilter))
                    continue;

                Vector3[] vertices = meshFilter.sharedMesh.vertices;

                if (vertices.Length == 0)
                    continue;

                VertexCloud cloud = new VertexCloud(meshFilter.transform, vertices);
                clouds.Add(cloud);

                foreach (Vector3 position in cloud.positions)
                {
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

        if (clouds.Count == 0)
        {
            Debug.LogWarning($"[HomeInteractionAnalyzer] 対象に頂点を読めるメッシュがありません: {targetName}");
            return;
        }

        targets.Add(new Target { name = targetName, clouds = clouds, center = bounds.center });
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
                Debug.Log($"[HomeInteractionAnalyzer] {path} の Read/Write を有効にして再インポートします。");
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
    // 注視している対象物体(全フレーム分を最初に少しずつ求める)
    // ------------------------------------------------------------
    // シーン4の gazeTarget と同じ: コーンに頂点が入った対象のうち、コーン軸との角度が最も小さいもの
    private void StepPrecompute()
    {
        if (IsReady)
            return;

        float startTime = Time.realtimeSinceStartup;

        while (!IsReady && Time.realtimeSinceStartup - startTime < PrecomputeBudget)
            PrecomputeSample(samples[precomputed++]);

        if (IsReady)
        {
            Debug.Log($"[HomeInteractionAnalyzer] 対象物体の注視を求めました({samples.Count} samples)。");
            analysisDirty = true;
            shownSample = -2; // 対象物体の線を描き直す
        }
    }

    private void FinishPrecompute()
    {
        while (!IsReady)
            PrecomputeSample(samples[precomputed++]);
    }

    private void PrecomputeSample(Sample sample)
    {
        Cone cone = new Cone(coneAngle, coneDistance);

        foreach (Person person in sample.persons)
        {
            person.target = -1;
            person.targetAngle = NoTargetAngle;

            if (!person.hasGaze)
                continue;

            cone.Set(person.origin, person.direction);
            float bestAngle = float.MaxValue;

            for (int t = 0; t < targets.Count; t++)
            {
                foreach (VertexCloud cloud in targets[t].clouds)
                {
                    if (cloud.MinConeAngle(cone, out float angleRad) && angleRad < bestAngle)
                    {
                        bestAngle = angleRad;
                        person.target = t;
                    }
                }
            }

            if (person.target >= 0)
                person.targetAngle = bestAngle * Mathf.Rad2Deg;
        }
    }

    // ------------------------------------------------------------
    // 人物どうしの判定
    // ------------------------------------------------------------
    // viewer の視線が other の頭(顔)の方向を向いているか(対象物体と同じコーンの角度 coneAngle で判定する)
    private bool LooksAtFace(Person viewer, Person other)
    {
        if (!viewer.hasGaze || !other.hasHead)
            return false;

        Vector3 toHead = other.head - viewer.origin;
        float distance = toHead.magnitude;

        return distance > 0.05f && distance <= lookAtPersonDistance &&
               Vector3.Angle(viewer.direction, toHead) <= coneAngle;
    }

    // viewer の視線が other の体(頭〜骨盤)のどこかの方向を向いているか。point は最も視線に近い点
    private bool LooksAtBody(Person viewer, Person other, out Vector3 point)
    {
        point = Vector3.zero;

        if (!viewer.hasGaze || other.body == null)
            return false;

        float bestAngle = float.MaxValue;

        foreach (Vector3 bodyPoint in other.body)
        {
            Vector3 toPoint = bodyPoint - viewer.origin;
            float distance = toPoint.magnitude;

            if (distance <= 0.05f || distance > lookAtPersonDistance)
                continue;

            float angle = Vector3.Angle(viewer.direction, toPoint);

            if (angle < bestAngle)
            {
                bestAngle = angle;
                point = bodyPoint;
            }
        }

        return bestAngle <= coneAngle;
    }

    // 2人が近くで向かい合っているか(水平面での向き。上下の向きは見ない)
    private bool FacingEachOther(Person a, Person b)
    {
        if (!a.hasGaze || !b.hasGaze || !a.hasHead || !b.hasHead)
            return false;

        Vector3 between = b.head - a.head;
        between.y = 0f;
        float distance = between.magnitude;

        if (distance < 0.05f || distance > faceToFaceDistance)
            return false;

        Vector3 dirA = new Vector3(a.direction.x, 0f, a.direction.z);
        Vector3 dirB = new Vector3(b.direction.x, 0f, b.direction.z);

        // 真下・真上を向いていて水平の向きが決まらないときは数えない
        if (dirA.sqrMagnitude < 0.01f || dirB.sqrMagnitude < 0.01f)
            return false;

        return Vector3.Angle(dirA, between) <= faceToFaceAngle && Vector3.Angle(dirB, -between) <= faceToFaceAngle;
    }

    // ------------------------------------------------------------
    // 集計
    // ------------------------------------------------------------
    private void GetRangeWindow(out float start, out float end)
    {
        end = rangeEnd < 0f ? duration : Mathf.Min(rangeEnd, duration);
        start = Mathf.Clamp(rangeStart, 0f, end);
    }

    private void GetRangeIndices(out int start, out int end)
    {
        GetRangeWindow(out float windowStart, out float windowEnd);
        start = FindSampleIndex(windowStart - 0.0001f) + 1; // windowStart 以上の最初のフレーム
        end = Mathf.Max(start, FindSampleIndex(windowEnd) + 1); // windowEnd 以下の最後のフレームの次
    }

    private void RunAnalysis(int start, int end)
    {
        results.Clear();
        analyzedStart = start;
        analyzedEnd = end;
        analysisDirty = false;
        rangeSeconds = 0f;

        int n = units.Count;
        unitPresentSeconds = new float[n];
        unitGazeSeconds = new float[n];
        coPresentSeconds = new float[n, n];

        Person[] present = new Person[n];
        List<int> presentUnits = new List<int>();
        int[] targetLookers = new int[targets.Count];

        for (int i = start; i < end; i++)
        {
            Sample sample = samples[i];
            float time = sample.time;
            float d = sample.duration;
            rangeSeconds += d;

            GetPresentUnits(sample, present, presentUnits);
            System.Array.Clear(targetLookers, 0, targetLookers.Length);

            foreach (int u in presentUnits)
            {
                Person p = present[u];
                unitPresentSeconds[u] += d;

                if (p.hasGaze)
                    unitGazeSeconds[u] += d;

                if (p.target >= 0)
                {
                    Add(MeasureType.TargetGaze, u, -1, p.target, time, d);
                    targetLookers[p.target]++;
                }
            }

            // 2人以上が同時に見ていた対象物体
            for (int t = 0; t < targetLookers.Length; t++)
            {
                if (targetLookers[t] >= 2)
                    Add(MeasureType.JointAttention, -1, -1, t, time, d);
            }

            for (int x = 0; x < presentUnits.Count; x++)
            {
                for (int y = x + 1; y < presentUnits.Count; y++)
                {
                    int a = presentUnits[x];
                    int b = presentUnits[y];
                    Person pa = present[a];
                    Person pb = present[b];

                    coPresentSeconds[a, b] += d;
                    coPresentSeconds[b, a] += d;

                    bool faceAB = LooksAtFace(pa, pb);
                    bool faceBA = LooksAtFace(pb, pa);

                    if (LooksAtBody(pa, pb, out _))
                        Add(MeasureType.LookAtPerson, a, b, -1, time, d);

                    if (LooksAtBody(pb, pa, out _))
                        Add(MeasureType.LookAtPerson, b, a, -1, time, d);

                    if (faceAB)
                        Add(MeasureType.LookAtFace, a, b, -1, time, d);

                    if (faceBA)
                        Add(MeasureType.LookAtFace, b, a, -1, time, d);

                    if (faceAB && faceBA)
                        Add(MeasureType.MutualGaze, a, b, -1, time, d);

                    if (FacingEachOther(pa, pb))
                        Add(MeasureType.FaceToFace, a, b, -1, time, d);

                    if (pa.target >= 0 && pa.target == pb.target)
                        Add(MeasureType.JointAttention, a, b, pa.target, time, d);
                }
            }
        }

        foreach (Accumulator accumulator in results.Values)
            accumulator.Close();
    }

    // フレームに映っている人物(数えない短い人物は除く)。presentUnits は番号の小さい順
    private void GetPresentUnits(Sample sample, Person[] present, List<int> presentUnits)
    {
        System.Array.Clear(present, 0, present.Length);
        presentUnits.Clear();

        foreach (Person person in sample.persons)
        {
            if (unitOfPerson.TryGetValue(person.id, out int u) && present[u] == null)
            {
                present[u] = person;
                presentUnits.Add(u);
            }
        }

        presentUnits.Sort();
    }

    private static long Key(MeasureType type, int a, int b, int target) =>
        ((long)type << 48) | ((long)(a + 1) << 32) | ((long)(b + 1) << 16) | (long)(target + 1);

    private void Add(MeasureType type, int a, int b, int target, float time, float d)
    {
        long key = Key(type, a, b, target);

        if (!results.TryGetValue(key, out Accumulator accumulator))
        {
            accumulator = new Accumulator { type = type, a = a, b = b, target = target };
            results[key] = accumulator;
        }

        accumulator.Add(time, d, mergeGapSeconds);
    }

    private Accumulator Get(MeasureType type, int a, int b, int target) =>
        results.TryGetValue(Key(type, a, b, target), out Accumulator accumulator) ? accumulator : null;

    // 割合の分母
    private float GetBaseSeconds(Accumulator accumulator)
    {
        if (accumulator.type == MeasureType.TargetGaze)
            return unitPresentSeconds[accumulator.a];

        if (accumulator.a < 0)
            return rangeSeconds;

        return coPresentSeconds[accumulator.a, accumulator.b];
    }

    private int CountEpisodes(Accumulator accumulator, out float mean, out float longest)
    {
        int count = 0;
        float total = 0f;
        longest = 0f;

        foreach (Vector2 episode in accumulator.episodes)
        {
            float length = episode.y - episode.x;

            if (length < minEpisodeSeconds)
                continue;

            count++;
            total += length;
            longest = Mathf.Max(longest, length);
        }

        mean = count > 0 ? total / count : 0f;
        return count;
    }

    // ------------------------------------------------------------
    // Output
    // ------------------------------------------------------------
    private HomeInteractionList CreateOutput()
    {
        GetRangeWindow(out float windowStart, out float windowEnd);

        HomeInteractionList data = new HomeInteractionList
        {
            experimentName = env.ExperimentName,
            subjectName = subjectName,
            createdAt = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            sampleCount = analyzedEnd - analyzedStart,
            rangeSeconds = rangeSeconds,
            parameters = new HomeInteractionParameters
            {
                skeletonSource = SourceName,
                headDirectionMethod = headDirectionMethod.ToString(),
                downwardAngle = downwardAngle,
                coneAngle = coneAngle,
                coneDistance = coneDistance,
                lookAtPersonDistance = lookAtPersonDistance,
                faceToFaceAngle = faceToFaceAngle,
                faceToFaceDistance = faceToFaceDistance,
                minPersonSeconds = minPersonSeconds,
                mergeGapSeconds = mergeGapSeconds,
                minEpisodeSeconds = minEpisodeSeconds,
                rangeStartSec = windowStart,
                rangeEndSec = windowEnd
            }
        };

        foreach (Target target in targets)
            data.targets.Add(target.name);

        for (int u = 0; u < units.Count; u++)
        {
            data.persons.Add(new HomeInteractionPerson
            {
                name = units[u].name,
                personIds = new List<int>(units[u].ids),
                presentSeconds = unitPresentSeconds[u],
                gazeSeconds = unitGazeSeconds[u]
            });
        }

        List<Accumulator> sorted = new List<Accumulator>(results.Values);
        sorted.Sort((x, y) =>
            x.type != y.type ? x.type.CompareTo(y.type) :
            x.a != y.a ? x.a.CompareTo(y.a) :
            x.b != y.b ? x.b.CompareTo(y.b) :
            x.target.CompareTo(y.target));

        foreach (Accumulator accumulator in sorted)
        {
            string person = accumulator.a >= 0 ? units[accumulator.a].name : "";
            string other = accumulator.b >= 0 ? units[accumulator.b].name : "";
            string target = accumulator.target >= 0 ? targets[accumulator.target].name : "";
            float baseSeconds = GetBaseSeconds(accumulator);
            int episodes = CountEpisodes(accumulator, out float mean, out float longest);

            data.measures.Add(new HomeInteractionMeasure
            {
                type = accumulator.type.ToString(),
                person = person,
                other = other,
                target = target,
                seconds = accumulator.seconds,
                frames = accumulator.frames,
                baseSeconds = baseSeconds,
                ratio = baseSeconds > 0f ? accumulator.seconds / baseSeconds : 0f,
                episodes = episodes,
                meanEpisodeSeconds = mean,
                longestEpisodeSeconds = longest,
                firstTimeSec = accumulator.firstTime
            });

            foreach (Vector2 episode in accumulator.episodes)
            {
                if (episode.y - episode.x < minEpisodeSeconds)
                    continue;

                data.episodes.Add(new HomeInteractionEpisode
                {
                    type = accumulator.type.ToString(),
                    person = person,
                    other = other,
                    target = target,
                    startSec = episode.x,
                    endSec = episode.y,
                    durationSec = episode.y - episode.x
                });
            }
        }

        data.episodes.Sort((x, y) => x.startSec.CompareTo(y.startSec));
        return data;
    }

    private static string CreateSummaryCsv(HomeInteractionList data)
    {
        StringBuilder csv = new StringBuilder();
        csv.AppendLine("type,person,other,target,seconds,frames,baseSeconds,ratio,episodes,meanEpisodeSeconds,longestEpisodeSeconds,firstTimeSec");

        foreach (HomeInteractionMeasure m in data.measures)
        {
            csv.AppendLine(string.Join(",",
                m.type, Csv(m.person), Csv(m.other), Csv(m.target),
                F(m.seconds), m.frames.ToString(CultureInfo.InvariantCulture), F(m.baseSeconds), F(m.ratio),
                m.episodes.ToString(CultureInfo.InvariantCulture), F(m.meanEpisodeSeconds), F(m.longestEpisodeSeconds), F(m.firstTimeSec)));
        }

        return csv.ToString();
    }

    private static string CreateEpisodesCsv(HomeInteractionList data)
    {
        StringBuilder csv = new StringBuilder();
        csv.AppendLine("type,person,other,target,startSec,endSec,durationSec");

        foreach (HomeInteractionEpisode e in data.episodes)
        {
            csv.AppendLine(string.Join(",",
                e.type, Csv(e.person), Csv(e.other), Csv(e.target), F(e.startSec), F(e.endSec), F(e.durationSec)));
        }

        return csv.ToString();
    }

    private static string F(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Csv(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        return value.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }

    // ------------------------------------------------------------
    // Display
    // ------------------------------------------------------------
    // 再生位置の骨格・視線と、その瞬間の関係を線で表示する
    // (Skeleton の一覧で人物を選んでいれば、その人の骨格と、その人が関わる関係だけを表示する)
    private void ShowSample(int index)
    {
        if (index == shownSample)
            return;

        shownSample = index;
        usedRelationLines = 0;
        HashSet<int> shown = new HashSet<int>();
        StringBuilder now = new StringBuilder();

        if (index >= 0 && index < samples.Count)
        {
            Sample sample = samples[index];
            bool hasTargets = index < precomputed;
            Person[] present = new Person[units.Count];
            List<int> presentUnits = new List<int>();
            GetPresentUnits(sample, present, presentUnits);

            // 選んだ人物(-1 なら全員)
            int selected = skeletonPersonId >= 0 && unitOfPerson.TryGetValue(skeletonPersonId, out int selectedUnit) ? selectedUnit : -1;
            bool filtered = skeletonPersonId >= 0;

            foreach (Person person in sample.persons)
            {
                if (filtered && person.id != skeletonPersonId)
                    continue;

                ShowPerson(person);
                shown.Add(person.id);
            }

            // 見ている対象物体(2人以上が同じ対象を見ていればマゼンタ)
            if (hasTargets)
            {
                Dictionary<int, List<int>> lookers = new Dictionary<int, List<int>>();

                foreach (int u in presentUnits)
                {
                    if (present[u].target < 0)
                        continue;

                    if (!lookers.TryGetValue(present[u].target, out List<int> list))
                    {
                        list = new List<int>();
                        lookers[present[u].target] = list;
                    }

                    list.Add(u);
                }

                foreach (KeyValuePair<int, List<int>> pair in lookers)
                {
                    if (filtered && !pair.Value.Contains(selected))
                        continue;

                    bool joint = pair.Value.Count >= 2;
                    List<string> names = new List<string>();

                    foreach (int u in pair.Value)
                    {
                        Color color = joint ? Color.magenta : Color.Lerp(units[u].color, Color.white, 0.3f);
                        DrawRelation(present[u].origin, targets[pair.Key].center, color, joint ? relationLineWidth : relationLineWidth * 0.5f);
                        names.Add(units[u].name);
                    }

                    now.AppendLine(joint
                        ? $"Joint attention : {string.Join(" & ", names)} → {targets[pair.Key].name}"
                        : $"{names[0]} → {targets[pair.Key].name}");
                }
            }

            for (int x = 0; x < presentUnits.Count; x++)
            {
                for (int y = x + 1; y < presentUnits.Count; y++)
                {
                    int a = presentUnits[x];
                    int b = presentUnits[y];

                    if (filtered && a != selected && b != selected)
                        continue;

                    Person pa = present[a];
                    Person pb = present[b];
                    bool faceAB = LooksAtFace(pa, pb);
                    bool faceBA = LooksAtFace(pb, pa);

                    if (faceAB && faceBA)
                    {
                        DrawRelation(pa.head, pb.head, Color.yellow, relationLineWidth * 2f);
                        now.AppendLine($"Mutual gaze : {units[a].name} ↔ {units[b].name}");
                    }
                    else
                    {
                        ShowOneWayLook(a, b, pa, pb, faceAB, now);
                        ShowOneWayLook(b, a, pb, pa, faceBA, now);
                    }

                    if (FacingEachOther(pa, pb))
                    {
                        // 床の上に水色の線
                        Vector3 floorA = new Vector3(pa.head.x, 0.02f, pa.head.z);
                        Vector3 floorB = new Vector3(pb.head.x, 0.02f, pb.head.z);
                        DrawRelation(floorA, floorB, Color.cyan, relationLineWidth * 2f);
                        now.AppendLine($"Face to face : {units[a].name} - {units[b].name}");
                    }
                }
            }
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

        nowText = now.ToString();
    }

    private void ShowOneWayLook(int viewer, int other, Person pv, Person po, bool face, StringBuilder now)
    {
        if (face)
        {
            DrawRelation(pv.origin, po.head, units[viewer].color, relationLineWidth);
            now.AppendLine($"{units[viewer].name} → {units[other].name} (face)");
        }
        else if (LooksAtBody(pv, po, out Vector3 point))
        {
            DrawRelation(pv.origin, point, Color.Lerp(units[viewer].color, Color.white, 0.5f), relationLineWidth * 0.6f);
            now.AppendLine($"{units[viewer].name} → {units[other].name} (body)");
        }
    }

    private void ShowPerson(Person person)
    {
        bool counted = unitOfPerson.TryGetValue(person.id, out int u);
        // 数えない短い人物は灰色
        Color color = counted ? units[u].color : new Color(0.5f, 0.5f, 0.5f);

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
            Color lineColor = person.usedEyeGaze ? Color.yellow : Color.Lerp(color, Color.white, 0.5f);
            gaze.startColor = lineColor;
            gaze.endColor = lineColor;
        }
    }

    private void DrawRelation(Vector3 from, Vector3 to, Color color, float width)
    {
        if (usedRelationLines >= relationLines.Count)
            relationLines.Add(CreateLine("Relation", color, width));

        LineRenderer line = relationLines[usedRelationLines++];
        line.enabled = true;
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
        int low = 0;
        int high = samples.Count - 1;
        int result = -1;

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
            GetRangeWindow(out float windowStart, out float windowEnd);
            string status = IsReady
                ? $"Range : {windowStart:F1} - {windowEnd:F1} s ({analyzedEnd - analyzedStart} frames)"
                : $"Preparing target gaze {(samples.Count > 0 ? precomputed * 100f / samples.Count : 0f):F0}%";

            frameText.text =
                $"{subjectName} ({OutputName})  Time : {playbackTime:F1} / {duration:F1} s\n" +
                $"{status}  Skeleton : {SkeletonLabel}\n" +
                nowText;
        }

        if (scoreText == null)
            return;

        if (!IsReady)
        {
            scoreText.text = "Preparing...";
            return;
        }

        StringBuilder text = new StringBuilder();

        switch (page)
        {
            case Page.Targets:
                AppendTargetPage(text);
                break;
            case Page.JointAttention:
                AppendJointAttentionPage(text);
                break;
            default:
                AppendPersonsPage(text);
                break;
        }

        scoreText.text = text.ToString();
    }

    // 人物ごとに、見た対象物体(時間 / 映っていた時間に対する割合 / 回数)
    private void AppendTargetPage(StringBuilder text)
    {
        text.AppendLine("<b>Targets</b> (time, % of present, looks)");

        for (int u = 0; u < units.Count; u++)
        {
            if (unitPresentSeconds[u] <= 0f)
                continue;

            text.AppendLine($"{units[u].name} (present {unitPresentSeconds[u]:F1} s)");

            for (int t = 0; t < targets.Count; t++)
            {
                Accumulator a = Get(MeasureType.TargetGaze, u, -1, t);

                if (a != null)
                    text.AppendLine($"  {targets[t].name} : {FormatMeasure(a)}");
            }
        }
    }

    // 対象物体ごとに、2人以上が同時に見ていた時間と、2人の組ごとの時間
    private void AppendJointAttentionPage(StringBuilder text)
    {
        text.AppendLine("<b>Joint attention</b> (same target at the same time)");

        for (int t = 0; t < targets.Count; t++)
        {
            Accumulator any = Get(MeasureType.JointAttention, -1, -1, t);

            if (any == null)
                continue;

            text.AppendLine($"{targets[t].name} : {FormatMeasure(any)}");

            for (int a = 0; a < units.Count; a++)
            {
                for (int b = a + 1; b < units.Count; b++)
                {
                    Accumulator pair = Get(MeasureType.JointAttention, a, b, t);

                    if (pair != null)
                        text.AppendLine($"  {units[a].name} & {units[b].name} : {FormatMeasure(pair)}");
                }
            }
        }
    }

    // 相手を見る(体 / 顔)・目を合わせる・向かい合う
    private void AppendPersonsPage(StringBuilder text)
    {
        text.AppendLine("<b>Look at person</b> (% of time together)");

        for (int a = 0; a < units.Count; a++)
        {
            for (int b = 0; b < units.Count; b++)
            {
                if (a == b)
                    continue;

                Accumulator body = Get(MeasureType.LookAtPerson, a, b, -1);
                Accumulator face = Get(MeasureType.LookAtFace, a, b, -1);

                if (body == null && face == null)
                    continue;

                text.AppendLine($"{units[a].name} → {units[b].name}");

                if (body != null)
                    text.AppendLine($"  body : {FormatMeasure(body)}");

                if (face != null)
                    text.AppendLine($"  face : {FormatMeasure(face)}");
            }
        }

        text.AppendLine("<b>Mutual gaze</b>");
        AppendPairs(text, MeasureType.MutualGaze);
        text.AppendLine("<b>Face to face</b>");
        AppendPairs(text, MeasureType.FaceToFace);
    }

    private void AppendPairs(StringBuilder text, MeasureType type)
    {
        for (int a = 0; a < units.Count; a++)
        {
            for (int b = a + 1; b < units.Count; b++)
            {
                Accumulator pair = Get(type, a, b, -1);

                if (pair != null)
                    text.AppendLine($"  {units[a].name} - {units[b].name} : {FormatMeasure(pair)}");
            }
        }
    }

    private string FormatMeasure(Accumulator accumulator)
    {
        float baseSeconds = GetBaseSeconds(accumulator);
        float percent = baseSeconds > 0f ? accumulator.seconds / baseSeconds * 100f : 0f;
        int episodes = CountEpisodes(accumulator, out _, out _);
        return $"{accumulator.seconds:F1} s ({percent:F0}%) {episodes}x";
    }

    // ------------------------------------------------------------
    // Timeline (画面下)
    // ------------------------------------------------------------
    //   [>]  12.3 / 60.0 s  [=========o-----------------]   … 再生バー(オレンジ: 数える時間範囲)
    //   From [--o------] 5.0 s   to [-------o--] 40.0 s   [Full]  [Page: Targets]
    private void SetupTimeline()
    {
        Canvas canvas = frameText != null ? frameText.canvas : FindObjectOfType<Canvas>();

        if (canvas == null)
        {
            Debug.LogWarning("[HomeInteractionAnalyzer] Canvas が無いため、再生バーを作れません。");
            return;
        }

#if UNITY_EDITOR
        uiSprite = UnityEditor.AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
        checkmarkSprite = UnityEditor.AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Checkmark.psd");
#endif

        RectTransform root = CreateRect("Timeline", canvas.transform);
        root.anchorMin = new Vector2(0f, 0f);
        root.anchorMax = new Vector2(1f, 0f);
        root.pivot = new Vector2(0.5f, 0f);
        root.anchoredPosition = new Vector2(0f, 10f);
        root.sizeDelta = new Vector2(-40f, 96f);
        AddImage(root.gameObject, uiSprite, new Color(0.95f, 0.95f, 0.95f, 0.9f));

        // 1行目: 再生/一時停止・時刻・再生バー
        RectTransform row1 = CreateRow(root, 0f, 48f);

        Button playPause = CreateButton(row1, ">", 0f, 50f, out playPauseText);
        playPause.onClick.AddListener(TogglePlay);

        timeText = CreateText("Time", row1, TextAlignmentOptions.MidlineLeft);
        PlaceLeft(timeText.rectTransform, 60f, 160f);

        timelineSlider = CreateSlider(row1, new Color(0.25f, 0.45f, 0.95f));
        RectTransform timelineRect = (RectTransform)timelineSlider.transform;
        timelineRect.anchorMin = new Vector2(0f, 0f);
        timelineRect.anchorMax = new Vector2(1f, 1f);
        timelineRect.offsetMin = new Vector2(230f, 10f);
        timelineRect.offsetMax = new Vector2(-10f, -10f);
        timelineSlider.onValueChanged.AddListener(Seek);

        RectTransform background = (RectTransform)timelineSlider.transform.Find("Background");
        rangeHighlight = CreateRect("Range", background);
        AddImage(rangeHighlight.gameObject, null, new Color(1f, 0.6f, 0.1f, 0.45f)).raycastTarget = false;

        // 2行目: 数える時間範囲・表示の切り替え
        RectTransform row2 = CreateRow(root, 48f, 44f);

        TMP_Text fromLabel = CreateText("From", row2, TextAlignmentOptions.MidlineLeft);
        fromLabel.text = "From";
        PlaceLeft(fromLabel.rectTransform, 10f, 95f);

        rangeStartSlider = CreateSlider(row2, new Color(1f, 0.6f, 0.1f));
        PlaceLeft((RectTransform)rangeStartSlider.transform, 105f, 260f, 10f);
        rangeStartSlider.onValueChanged.AddListener(value =>
        {
            float end = rangeEnd < 0f ? duration : rangeEnd;
            rangeStart = Mathf.Min(value, end);
        });

        rangeStartText = CreateText("FromValue", row2, TextAlignmentOptions.MidlineLeft);
        PlaceLeft(rangeStartText.rectTransform, 370f, 80f);

        TMP_Text toLabel = CreateText("To", row2, TextAlignmentOptions.MidlineLeft);
        toLabel.text = "to";
        PlaceLeft(toLabel.rectTransform, 455f, 30f);

        rangeEndSlider = CreateSlider(row2, new Color(1f, 0.6f, 0.1f));
        PlaceLeft((RectTransform)rangeEndSlider.transform, 485f, 260f, 10f);
        rangeEndSlider.onValueChanged.AddListener(value =>
        {
            float end = Mathf.Max(value, rangeStart);
            rangeEnd = end >= duration - 0.001f ? -1f : end;
        });

        rangeEndText = CreateText("ToValue", row2, TextAlignmentOptions.MidlineLeft);
        PlaceLeft(rangeEndText.rectTransform, 750f, 80f);

        Button full = CreateButton(row2, "Full", 840f, 70f, out _);
        full.onClick.AddListener(() => SetRange(0f, -1f));

        Button pageButton = CreateButton(row2, "", 920f, 230f, out pageText);
        pageButton.onClick.AddListener(NextPage);

        foreach (Slider slider in new[] { timelineSlider, rangeStartSlider, rangeEndSlider })
        {
            slider.minValue = 0f;
            slider.maxValue = Mathf.Max(duration, 0.001f);
        }

        // 時間範囲のスライダーはドラッグ中は集計せず、離したときに集計する
        AddScrubEvents(rangeStartSlider.gameObject);
        AddScrubEvents(rangeEndSlider.gameObject);

        UpdateTimelineUI();
    }

    private void AddScrubEvents(GameObject target)
    {
        UnityEngine.EventSystems.EventTrigger trigger = target.AddComponent<UnityEngine.EventSystems.EventTrigger>();

        UnityEngine.EventSystems.EventTrigger.Entry down = new UnityEngine.EventSystems.EventTrigger.Entry
        {
            eventID = UnityEngine.EventSystems.EventTriggerType.PointerDown
        };
        down.callback.AddListener(_ => isScrubbing = true);
        trigger.triggers.Add(down);

        UnityEngine.EventSystems.EventTrigger.Entry up = new UnityEngine.EventSystems.EventTrigger.Entry
        {
            eventID = UnityEngine.EventSystems.EventTriggerType.PointerUp
        };
        up.callback.AddListener(_ => isScrubbing = false);
        trigger.triggers.Add(up);
    }

    private void UpdateTimelineUI()
    {
        if (timelineSlider == null)
            return;

        GetRangeWindow(out float windowStart, out float windowEnd);
        float max = Mathf.Max(duration, 0.001f);

        timelineSlider.SetValueWithoutNotify(playbackTime);
        rangeStartSlider.SetValueWithoutNotify(windowStart);
        rangeEndSlider.SetValueWithoutNotify(windowEnd);

        playPauseText.text = isPlaying ? "||" : ">";
        timeText.text = $"{playbackTime:F1} / {duration:F1} s";
        rangeStartText.text = $"{windowStart:F1} s";
        rangeEndText.text = $"{windowEnd:F1} s";
        pageText.text = $"Page: {page}";

        rangeHighlight.anchorMin = new Vector2(Mathf.Clamp01(windowStart / max), 0f);
        rangeHighlight.anchorMax = new Vector2(Mathf.Clamp01(windowEnd / max), 1f);
        rangeHighlight.offsetMin = Vector2.zero;
        rangeHighlight.offsetMax = Vector2.zero;
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
    // Skeleton selection (表示する人物を1人だけ選ぶ。表示だけで、解析には関係しない)
    // ------------------------------------------------------------
    //   [Skeleton : All  v]
    //   (o) All
    //   ( ) kawabe (0.1 s -, 17000 frames)
    public void SetSkeletonPerson(int id)
    {
        skeletonPersonId = id;
        appliedSkeletonPersonId = id;

        // 選んだ人物が今の時刻に映っていなければ、最初に出てくる時刻へ移動する
        if (id >= 0 && !IsPersonInSample(FindSampleIndex(playbackTime), id) && personFirstTimes.TryGetValue(id, out float firstTime))
            Seek(firstTime);

        shownSample = -2; // 同じフレームでも描き直す
        ShowSample(FindSampleIndex(playbackTime));
        RefreshSkeletonToggles();
        UpdateTimelineUI();
        UpdateText();
    }

    private bool IsPersonInSample(int index, int id)
    {
        if (index < 0 || index >= samples.Count)
            return false;

        foreach (Person person in samples[index].persons)
        {
            if (person.id == id)
                return true;
        }

        return false;
    }

    private string SkeletonLabel => skeletonPersonId < 0 ? "All" : GetPersonName(skeletonPersonId);

    private void SetupSkeletonSelector(Canvas canvas, int slot)
    {
        if (canvas == null)
            return;

        RectTransform root = CreateDropdown(canvas, "SkeletonSelector", slot, out skeletonHeaderText, out skeletonListRect, out skeletonListContent);
        root.GetComponent<Button>().onClick.AddListener(() => skeletonListRect.gameObject.SetActive(!skeletonListRect.gameObject.activeSelf));
        skeletonListRect.gameObject.SetActive(false);

        skeletonAllToggle = CreateToggleRow(skeletonListContent, "All", 0f, DropdownRowHeight);
        skeletonAllToggle.onValueChanged.AddListener(isOn => OnSkeletonToggle(-1, isOn));

        int row = 1;

        foreach (KeyValuePair<int, float> pair in personSeconds)
        {
            int id = pair.Key;
            personFirstTimes.TryGetValue(id, out float firstTime);
            Toggle toggle = CreateToggleRow(skeletonListContent, $"{GetPersonName(id)} ({firstTime:F1} s -, {pair.Value:F1} s)",
                                            row++ * DropdownRowHeight, DropdownRowHeight);
            toggle.onValueChanged.AddListener(isOn => OnSkeletonToggle(id, isOn));
            skeletonToggles.Add(new KeyValuePair<int, Toggle>(id, toggle));
        }

        SetDropdownRows(skeletonListRect, skeletonListContent, row);
        RefreshSkeletonToggles();
    }

    private void OnSkeletonToggle(int id, bool isOn)
    {
        // 1つだけ選ぶ。選び直すと一覧を閉じる(選んでいるものを外そうとしたときは選んだままにする)
        if (isOn)
        {
            SetSkeletonPerson(id);
            skeletonListRect.gameObject.SetActive(false);
        }
        else
        {
            RefreshSkeletonToggles();
        }
    }

    private void RefreshSkeletonToggles()
    {
        if (skeletonAllToggle != null)
            skeletonAllToggle.SetIsOnWithoutNotify(skeletonPersonId < 0);

        foreach (KeyValuePair<int, Toggle> pair in skeletonToggles)
            pair.Value.SetIsOnWithoutNotify(pair.Key == skeletonPersonId);

        if (skeletonHeaderText != null)
            skeletonHeaderText.text = $"Skeleton : {SkeletonLabel}";
    }

    // 画面右上に右から並べる一覧(シーン4と同じ)
    private const float DropdownWidth = 320f;
    private const float DropdownRowHeight = 30f;
    private const int DropdownMaxVisibleRows = 14;

    // slot: 右から何番目か(0 = 一番右)。見出しのボタンと、開閉する一覧(スクロール)を作る
    private RectTransform CreateDropdown(Canvas canvas, string name, int slot, out TMP_Text headerText,
                                         out RectTransform list, out RectTransform content)
    {
        RectTransform root = CreateRect(name, canvas.transform);
        root.anchorMin = root.anchorMax = root.pivot = new Vector2(1f, 1f);
        root.anchoredPosition = new Vector2(-20f - slot * (DropdownWidth + 10f), -20f);
        root.sizeDelta = new Vector2(DropdownWidth, 40f);

        Button header = root.gameObject.AddComponent<Button>();
        header.targetGraphic = AddImage(root.gameObject, uiSprite, Color.white);
        headerText = CreateText("Label", root, TextAlignmentOptions.MidlineLeft);
        SetStretch(headerText.rectTransform, new Vector2(10f, 0f), new Vector2(-10f, 0f));

        list = CreateRect("List", root);
        list.anchorMin = new Vector2(0f, 0f);
        list.anchorMax = new Vector2(1f, 0f);
        list.pivot = new Vector2(0.5f, 1f);
        list.anchoredPosition = new Vector2(0f, -2f);
        AddImage(list.gameObject, uiSprite, new Color(0.95f, 0.95f, 0.95f, 0.95f));
        list.gameObject.AddComponent<RectMask2D>();

        content = CreateRect("Content", list);
        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = new Vector2(1f, 1f);
        content.pivot = new Vector2(0.5f, 1f);

        ScrollRect scroll = list.gameObject.AddComponent<ScrollRect>();
        scroll.content = content;
        scroll.viewport = list;
        scroll.horizontal = false;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = DropdownRowHeight;
        return root;
    }

    private static void SetDropdownRows(RectTransform list, RectTransform content, int rows)
    {
        list.sizeDelta = new Vector2(0f, Mathf.Min(rows, DropdownMaxVisibleRows) * DropdownRowHeight);
        content.sizeDelta = new Vector2(0f, rows * DropdownRowHeight);
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
        public bool usedEyeGaze;

        // 見られる側としての頭の位置と、体(頭〜骨盤)の線上の点
        public bool hasHead;
        public Vector3 head;
        public Vector3[] body;

        // 注視している対象物体の番号(-1 はなし)と、コーン軸との角度(度)
        public int target = -1;
        public float targetAngle = NoTargetAngle;
    }

    // 数える人物(grouped の骨格ではシーン3のグループ)
    private class Unit
    {
        public string name;
        public int[] ids;
        public float seconds;
        public Color color;
    }

    private class Target
    {
        public string name;
        public List<VertexCloud> clouds;
        // 線を引く先(頂点を囲む箱の中心)
        public Vector3 center;
    }

    // 1つの関係(種類・人物・相手・対象)の時間と、ひと続きの出来事
    private class Accumulator
    {
        public MeasureType type;
        public int a;
        public int b;
        public int target;
        public float seconds;
        public int frames;
        public float firstTime = -1f;
        // (開始, 終了) の秒
        public readonly List<Vector2> episodes = new List<Vector2>();

        private bool open;
        private float openStart;
        private float openEnd;

        public void Add(float time, float duration, float mergeGap)
        {
            seconds += duration;
            frames++;

            if (firstTime < 0f)
                firstTime = time;

            if (open && time - openEnd <= mergeGap)
            {
                openEnd = Mathf.Max(openEnd, time + duration);
                return;
            }

            Close();
            open = true;
            openStart = time;
            openEnd = time + duration;
        }

        public void Close()
        {
            if (!open)
                return;

            episodes.Add(new Vector2(openStart, openEnd));
            open = false;
        }
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

    // HomeGazeAnalyzer.VertexCloud と同じ。ワールド座標の頂点を格子に分け、コーンと交わらない格子をまとめて飛ばす
    private class VertexCloud
    {
        private const float CellSize = 0.25f;

        private class Cell
        {
            public Vector3 center;
            public float radius;
            public int[] indices;
        }

        public readonly Vector3[] positions;
        private readonly List<Cell> cells = new List<Cell>();
        private readonly Vector3 center;
        private readonly float radius;

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

                cells.Add(new Cell { center = bounds.center, radius = bounds.extents.magnitude, indices = indices.ToArray() });
            }

            Bounds all = new Bounds(positions.Length > 0 ? positions[0] : Vector3.zero, Vector3.zero);

            foreach (Vector3 position in positions)
                all.Encapsulate(position);

            center = all.center;
            radius = all.extents.magnitude;
        }

        // コーンに入った頂点があれば true。minAngleRad はコーン軸との最小角度
        public bool MinConeAngle(Cone cone, out float minAngleRad)
        {
            minAngleRad = float.MaxValue;

            if (!MayIntersect(center, radius, cone))
                return false;

            float bestDot = -2f;

            foreach (Cell cell in cells)
            {
                if (!MayIntersect(cell.center, cell.radius, cone))
                    continue;

                foreach (int i in cell.indices)
                {
                    Vector3 toVertex = positions[i] - cone.origin;
                    float distance = toVertex.magnitude;

                    if (distance <= 0f || distance > cone.distance)
                        continue;

                    float dot = Vector3.Dot(cone.direction, toVertex / distance);

                    if (dot >= cone.cosAngle && dot > bestDot)
                        bestDot = dot;
                }
            }

            if (bestDot < -1f)
                return false;

            minAngleRad = Mathf.Acos(Mathf.Clamp(bestDot, -1f, 1f));
            return true;
        }

        private static bool MayIntersect(Vector3 sphereCenter, float sphereRadius, Cone cone)
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
