using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Azure.Kinect.BodyTracking;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// シーン4(4GazeAnalyze)用。骨格データから視線コーンを求め、
//   - 部屋メッシュのヒートマップ   → Skeleton/<実験対象者>/Analysis/HeatMap/heatmap_<骨格>.json
//   - 対象物体ごとのスコア/注視対象 → Skeleton/<実験対象者>/Analysis/Score/score_<骨格>.json
// を作る。視線コーンの判定は ConeHeatMapShow / ScoreHeat / ScoreHeatSummary と同じ。
//
// 骨格データは次のどれかを選ぶ(<骨格> の部分のファイル名になる):
//   Filtered … シーン3で作った人物ごとの骨格 Filtered/grouped_<Mode>.json (人物 = シーン3のグループ)
//   Image    … シーン3で作った人物ごとの骨格 Filtered/grouped_Image.json  (人物 = シーン3のグループ)
//   Kinect   … キネクト1台分の <ID>_skeleton.json                          (人物ID = bodyId)
// 信頼度の比較・人物IDのまとめ方はシーン3で済ませてあるので、ここでは読み込んだ骨格をそのまま使う。
//
// 部屋オブジェクトと対象物体は同じGameObjectの HomeEnvLoader が Env から再生成する。
// シーン0で部屋オブジェクトの下に置いた対象物体(Env/TargetObjects.json)は自動でスコアの対象になる。
// それ以外に数えたいものは名前で指定する(シーンに置いたオブジェクトも指定できる)。
//
// 操作:
//   Play / Stop(Space) … 今の再生位置から再生 / 一時停止(最後まで再生すると保存)
//   再生バー(画面下)    … ドラッグでその時刻の骨格を表示する
//   Heat from / to      … ヒートマップ・スコアに数える時間範囲。Follow playhead がオンなら範囲の始まりから再生位置まで、
//                         オフなら再生位置に関係なく範囲全体を数える(Full で全体に戻す)
//   Analyze All         … 再生せずにヒートの時間範囲全体を一括で解析して保存
//   Save                … その時点の結果を保存(数えた時間範囲は parameters の heatRangeStartSec / EndSec)
//   Normalize           … 停止中に押すと、その時点で表示中のヒート(選んだ人物・時間範囲)を面積で重み付けした合計で割って正規化し、
//                         最も見られた場所を最大の色にして表示する。部屋オブジェクト・対象物体ごとの割合も出す。
//                         押した時点で一度だけ計算する。もう一度押す・再生する・人物や時間範囲を変えると元の表示
//                         (maxHeatDisplay 基準)に戻るので、正規化し直すときはもう一度押す
[RequireComponent(typeof(HomeEnvLoader))]
public class HomeGazeAnalyzer : MonoBehaviour
{
    public enum SkeletonSource
    {
        // シーン3で作った人物ごとの骨格 (Filtered/grouped_<filteredMode>.json)
        Filtered,
        // キネクト1台分の骨格
        Kinect,
        // シーン1(Record Raw Mkv)の生データから画像で推定したもの (Tools/GazePipeline → シーン3 → Filtered/grouped_Image.json)
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
    [Tooltip("Filtered のとき: 読み込む grouped_<Mode>.json(シーン3の Confidence Mode)")]
    [SerializeField] private HomeSkeletonFilter.ConfidenceMode filteredMode = HomeSkeletonFilter.ConfidenceMode.HeadJoints;
    [Tooltip("Kinect のとき: 読み込む <ID>_skeleton.json のキネクトID")]
    [SerializeField] private string kinectId = "A";
    [Tooltip("Image のとき: 視線として使う向き")]
    [SerializeField] private ImageDirection imageDirection = ImageDirection.GazeElseHead;
    [Tooltip("解析する人物。Filtered / Image ならシーン3のグループの番号、Kinect なら bodyId。-1 なら全員。")]
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
    [Tooltip("ヒートマップを表示する人物ID(Filtered / Image ならシーン3のグループの番号、Kinect なら bodyId)。複数指定すると合計を表示する。空なら全員。画面右上の一覧でも選べる。")]
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
    [Tooltip("人物ごとの色(人物IDの順。シーン3のグループの色と同じ並び)")]
    [SerializeField] private Color[] personColors =
    {
        Color.red, Color.green, Color.blue, Color.yellow, Color.cyan, Color.magenta
    };
    [Tooltip("補正後の視線方向を線で表示する")]
    [SerializeField] private bool showGaze = true;
    [SerializeField] private float gazeLineLength = 1.5f;
    [SerializeField] private float gazeLineWidth = 0.015f;
    [Tooltip("骨格と視線の線を表示する人物ID(1人だけ)。-1 なら全員。画面右上の「Skeleton」の一覧でも選べ、\n" +
             "選んだ人物がその時刻に映っていなければ、最初に出てくる時刻へ移動する。表示だけで、解析は全員分を行う。")]
    [SerializeField] private int skeletonPersonId = -1;

    [Header("Playback")]
    [SerializeField] private float playbackSpeed = 1f;
    [SerializeField] private KeyCode playStopKey = KeyCode.Space;

    [Header("Timeline")]
    [Tooltip("画面下に再生バー(ドラッグで再生位置を動かす)とヒートマップの時間範囲のスライダーを出す")]
    [SerializeField] private bool showTimeline = true;
    [Tooltip("ヒートマップ・スコアに数える時間範囲の始まり(秒)")]
    [SerializeField] private float heatRangeStart = 0f;
    [Tooltip("ヒートマップ・スコアに数える時間範囲の終わり(秒)。負の値ならデータの最後まで")]
    [SerializeField] private float heatRangeEnd = -1f;
    [Tooltip("オン: 範囲の始まりから再生位置までを数える(再生するとヒートが溜まっていく)\n" +
             "オフ: 再生位置に関係なく、範囲全体を数える")]
    [SerializeField] private bool heatFollowsPlayhead = true;

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
    // ヒートマップの人物の一覧
    private RectTransform heatListRect;
    private RectTransform heatListContent;
    private Toggle heatAllToggle;
    private readonly List<KeyValuePair<DisplayUnit, Toggle>> heatUnitToggles = new List<KeyValuePair<DisplayUnit, Toggle>>();
    // ヒートマップに表示する人物(空なら全員)
    private readonly SortedSet<int> selectedPersonIds = new SortedSet<int>();
    private int[] appliedPersonIds = new int[0];
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
    // 今のヒート・スコアに数えているフレーム: samples[analyzedStart] 〜 samples[nextSample - 1]
    private int analyzedStart;
    private int nextSample;
    private int shownSample = -1;
    private float colorTimer;
    private bool colorsDirty;

    // 全フレームの視線コーンの結果(別スレッドで最初に一度だけ作る。コーンの設定を変えたら作り直す)
    private HeatCache cache;
    private volatile HeatCache builtCache;
    private volatile float buildProgress;
    private volatile int buildGeneration;
    private ConeParams activeParams;
    // 表示中のヒートを作り直す必要がある(キャッシュができた・人物の選択を変えた など)
    private bool displayDirty = true;
    // キャッシュができたら行う
    private bool pendingAnalyzeAll;
    private bool pendingSave;

    // 再生バー(画面下)
    private Slider timelineSlider;
    private Slider rangeStartSlider;
    private Slider rangeEndSlider;
    private Toggle followToggle;
    private TMP_Text playPauseText;
    private TMP_Text timeText;
    private TMP_Text rangeStartText;
    private TMP_Text rangeEndText;
    private RectTransform rangeHighlight;
    private RectTransform analyzedHighlight;
    // スライダーをドラッグしている間はヒートを計算せず、離したときに反映する
    private bool isScrubbing;
    // 正規化表示(停止中に Normalize を押したとき)。押した時点のヒートで一度だけ計算し、ヒートが変わったら元の表示に戻す
    private bool normalizeHeat;
    private Image normalizeButtonImage;
    // 面積で重み付けしたヒートの合計 Σ heat × 面積 と、色が最大になるヒート
    private float normalizedTotalHeat;
    private float normalizedMaxHeat;
    // 正規化表示のときの、部屋オブジェクト/対象物体ごとのヒートの割合(大きい順)
    private readonly List<KeyValuePair<string, float>> normalizedMeshShares = new List<KeyValuePair<string, float>>();

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

        // 以前 Analysis/ の直下に置いていたグループ・ヒートマップ・スコアを種類ごとのフォルダへ移す
        HomeExperimentPaths.MigrateLegacyAnalysisFiles(env.ExperimentName, subjectName);
        LoadSamples();
        SetupHeatMeshes();
        SetupScoreTargets();
        ApplyDisplayMode();
        SetupPersonSelector();
        SetupSkeletonSelector(frameText != null ? frameText.canvas : FindObjectOfType<Canvas>(), 1);
        StartCacheBuild();
        SetupTimeline();
        ShowSample(FindSampleIndex(playbackTime));

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

        if (skeletonPersonId != appliedSkeletonPersonId)
            SetSkeletonPerson(skeletonPersonId);

        if (Input.GetKeyDown(playStopKey))
        {
            if (isPlaying)
                Stop();
            else
                Play();
        }

        bool reachedEnd = false;

        if (isPlaying)
        {
            playbackTime += Time.deltaTime * playbackSpeed;

            if (playbackTime >= duration)
            {
                playbackTime = duration;
                isPlaying = false;
                reachedEnd = true;
            }
        }

        ShowSample(FindSampleIndex(playbackTime));
        UpdateCache();
        UpdateAnalysis(false);

        // 再生中は頂点カラーの更新を間引く(止まっているときはすぐ反映する)
        colorTimer += Time.deltaTime;

        if (colorsDirty && (!isPlaying || colorTimer >= colorUpdateInterval))
        {
            colorTimer = 0f;
            ApplyColors();
        }

        if (reachedEnd)
            Finish();

        UpdateTimelineUI();
        UpdateText();
    }

    private void LateUpdate()
    {
        HomeExperimentPaths.ClearUISelection();
    }

    private void OnDestroy()
    {
        // 計算中の別スレッドを止める(世代が変わると途中で抜ける)
        buildGeneration++;
    }

    // ------------------------------------------------------------
    // Controls
    // ------------------------------------------------------------
    // 今の再生位置から再生する(最後まで再生し終わっていたら最初から)
    public void Play()
    {
        if (samples.Count == 0)
        {
            Debug.LogWarning("[HomeGazeAnalyzer] 解析できる骨格データがありません。");
            return;
        }

        if (playbackTime >= duration - 0.001f)
            playbackTime = 0f;

        // 正規化は停止中の表示なので、再生したら元の表示に戻す
        SetNormalizeHeat(false);
        isPlaying = true;
    }

    // 一時停止する(再生位置・骨格・ヒートはそのまま)
    public void Stop()
    {
        isPlaying = false;
        UpdateText();
    }

    public void TogglePlay()
    {
        if (isPlaying)
            Stop();
        else
            Play();
    }

    // 再生位置を動かす(再生中ならそこから再生を続ける)
    public void Seek(float time)
    {
        playbackTime = Mathf.Clamp(time, 0f, duration);
        ShowSample(FindSampleIndex(playbackTime));
    }

    // 停止中なら、今表示しているヒートを正規化して表示する(もう一度押すと元の表示に戻す。再生中はオンにできない)
    public void ToggleNormalizeHeat()
    {
        if (normalizeHeat)
        {
            SetNormalizeHeat(false);
            return;
        }

        if (isPlaying)
        {
            Debug.LogWarning("[HomeGazeAnalyzer] 正規化は停止中にだけできます。");
            return;
        }

        ComputeNormalization();
        SetNormalizeHeat(true);
    }

    private void SetNormalizeHeat(bool on)
    {
        if (normalizeHeat == on)
            return;

        normalizeHeat = on;
        colorsDirty = true;

        if (normalizeButtonImage != null)
            normalizeButtonImage.color = on ? new Color(1f, 0.75f, 0.4f) : Color.white;
    }

    // ヒートマップ・スコアに数える時間範囲を決める(end が負ならデータの最後まで)
    public void SetHeatRange(float start, float end)
    {
        heatRangeStart = Mathf.Max(0f, start);
        heatRangeEnd = end;
    }

    [ContextMenu("Analyze All")]
    public void AnalyzeAll()
    {
        if (samples.Count == 0)
        {
            Debug.LogWarning("[HomeGazeAnalyzer] 解析できる骨格データがありません。");
            return;
        }

        // 再生位置を最後にして、ヒートの時間範囲全体を解析する
        isPlaying = false;
        playbackTime = duration;
        ShowSample(FindSampleIndex(playbackTime));

        if (cache == null)
        {
            // 視線コーンの計算(別スレッド)が終わったら続きを行う
            pendingAnalyzeAll = true;
            Debug.Log("[HomeGazeAnalyzer] ヒートの準備が終わったら解析して保存します。");
            return;
        }

        UpdateAnalysis(true);
        Finish();
        UpdateTimelineUI();
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

        if (cache == null)
        {
            pendingSave = true;
            Debug.Log("[HomeGazeAnalyzer] ヒートの準備が終わったら保存します。");
            return;
        }

        UpdateAnalysis(true);
        GetHeatWindow(out float windowStart, out float windowEnd);
        Debug.Log($"[HomeGazeAnalyzer] {windowStart:F1}〜{windowEnd:F1} 秒の結果を保存します。");

        string heatMapPath = HomeExperimentPaths.GetGazeHeatMapPath(env.ExperimentName, subjectName, OutputName);
        string scorePath = HomeExperimentPaths.GetGazeScorePath(env.ExperimentName, subjectName, OutputName);

        try
        {
            // ヒートマップとスコアは別のフォルダ
            Directory.CreateDirectory(Path.GetDirectoryName(heatMapPath));
            Directory.CreateDirectory(Path.GetDirectoryName(scorePath));
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
        personNames.Clear();
        personFirstTimes.Clear();

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

                if (!personFirstTimes.ContainsKey(person.id))
                    personFirstTimes[person.id] = samples[i].time;
            }
        }

        duration = samples.Count > 0 ? samples[samples.Count - 1].time : 0f;

        StringBuilder text = new StringBuilder();
        text.Append($"[HomeGazeAnalyzer] {SourceName}: {samples.Count} samples, {duration:F1}s を読み込みました。人物 {personFrameCounts.Count} 人");

        foreach (KeyValuePair<int, int> pair in personFrameCounts)
            text.Append($"\n  {GetPersonName(pair.Key)}: {pair.Value} frames");

        Debug.Log(text.ToString());
    }

    // シーン3で作った人物ごとの骨格(grouped_<Mode>.json)を読み込む
    private void LoadFiltered(string modeName)
    {
        string path = HomeExperimentPaths.GetGroupedSkeletonPath(env.ExperimentName, subjectName, modeName);

        if (!File.Exists(path))
        {
            string how = skeletonSource == SkeletonSource.Image
                ? "Tools/GazePipeline で filtered_Image.json を作ってから、シーン3(Source = Image)でグループを作って Save grouped を押してください"
                : $"シーン3(Source = Kinect, Confidence Mode = {modeName})でグループを作って Save grouped を押してください";
            Debug.LogError($"[HomeGazeAnalyzer] 人物ごとの骨格のファイルがありません。{how}: {path}");
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

                if (!personNames.ContainsKey(person.trackId))
                    personNames[person.trackId] = person.label;

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
    // ヒートマップ・スコアに数える時間範囲(秒)。heatFollowsPlayhead なら終わりは再生位置まで
    private void GetHeatWindow(out float start, out float end)
    {
        float rangeEnd = heatRangeEnd < 0f ? duration : Mathf.Min(heatRangeEnd, duration);
        start = Mathf.Clamp(heatRangeStart, 0f, rangeEnd);
        end = heatFollowsPlayhead ? Mathf.Clamp(playbackTime, start, rangeEnd) : rangeEnd;
    }

    // time 以下で最も新しいフレームの番号(無ければ -1)
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
    // 視線コーンのキャッシュ(別スレッドで全フレームを一度だけ計算する)
    // ------------------------------------------------------------
    // 小計を作るフレーム数(30 = 約1秒)。時間範囲のヒートは、範囲に丸ごと入る小計を足し、端のフレームだけコーンを計算する
    private const int CacheChunkSize = 30;
    // 範囲の変化がこのフレーム数以下なら、出入りしたフレームだけを足し引きする(それより多いと小計から作り直す)
    private const int DifferentialLimit = CacheChunkSize * 3;
    // 正規化表示のとき、画面に割合を出す部屋オブジェクト/対象物体の数(割合の大きい順)
    private const int NormalizedShareLines = 8;

    private ConeParams CurrentConeParams()
    {
        return new ConeParams(coneAngle, coneDistance, useCenterWeightedHeat, heatPerHit);
    }

    private void StartCacheBuild()
    {
        activeParams = CurrentConeParams();
        int generation = ++buildGeneration;
        cache = null;
        builtCache = null;
        buildProgress = 0f;
        displayDirty = true;
        ClearDisplayedAnalysis();

        // 別スレッドからは読むだけ(読み込み後に変わらないもの)
        List<Sample> sampleList = samples;
        ConeParams parameters = activeParams;
        VertexCloud[] heatClouds = new VertexCloud[heatMeshes.Count];

        for (int m = 0; m < heatMeshes.Count; m++)
            heatClouds[m] = heatMeshes[m].cloud;

        List<VertexCloud>[] targetClouds = new List<VertexCloud>[targets.Count];
        string[] targetNames = new string[targets.Count];

        for (int t = 0; t < targets.Count; t++)
        {
            targetClouds[t] = targets[t].clouds;
            targetNames[t] = targets[t].name;
        }

        System.Threading.Thread thread = new System.Threading.Thread(
            () => BuildCache(generation, parameters, sampleList, heatClouds, targetClouds, targetNames))
        {
            IsBackground = true,
            Name = "HomeGazeAnalyzer_HeatCache",
            Priority = System.Threading.ThreadPriority.BelowNormal
        };
        thread.Start();
    }

    // 毎フレーム呼ぶ。コーンの設定が変わったら作り直し、キャッシュができたら表示に使う
    private void UpdateCache()
    {
        if (!CurrentConeParams().Equals(activeParams))
        {
            Debug.Log("[HomeGazeAnalyzer] コーンの設定が変わったので、ヒートを計算し直します。");
            StartCacheBuild();
            return;
        }

        HeatCache built = builtCache;

        if (built == null || built.generation != buildGeneration)
            return;

        cache = built;
        builtCache = null;
        displayDirty = true;
        Debug.Log($"[HomeGazeAnalyzer] ヒートの準備ができました ({samples.Count} samples, {built.buildMilliseconds} ms)。");

        UpdateAnalysis(true);

        if (pendingAnalyzeAll)
        {
            pendingAnalyzeAll = false;
            pendingSave = false;
            AnalyzeAll();
        }
        else if (pendingSave)
        {
            pendingSave = false;
            Save();
        }
    }

    // 別スレッド。全フレームの注視記録と、CacheChunkSize フレームごとの人物別ヒートの小計(当たった頂点だけ)を作る
    private void BuildCache(int generation, ConeParams parameters, List<Sample> sampleList, VertexCloud[] heatClouds,
                            List<VertexCloud>[] targetClouds, string[] targetNames)
    {
        try
        {
            System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
            int count = sampleList.Count;
            int chunkCount = (count + CacheChunkSize - 1) / CacheChunkSize;

            HeatCache result = new HeatCache
            {
                generation = generation,
                parameters = parameters,
                chunks = new HeatChunk[chunkCount],
                records = new HomeGazeFrameRecord[count][],
                bestTargets = new int[count][]
            };

            Dictionary<int, ScratchHeat> scratch = new Dictionary<int, ScratchHeat>();
            Cone cone = new Cone(parameters.angleDeg, parameters.distance);

            for (int c = 0; c < chunkCount; c++)
            {
                if (generation != buildGeneration)
                    return; // 設定が変わって作り直しになった

                int start = c * CacheChunkSize;
                int end = Mathf.Min(start + CacheChunkSize, count);

                for (int i = start; i < end; i++)
                    ComputeSample(i, sampleList[i], ref cone, parameters, heatClouds, targetClouds, targetNames, scratch, result);

                HeatChunk chunk = new HeatChunk();

                foreach (KeyValuePair<int, ScratchHeat> pair in scratch)
                {
                    if (pair.Value.HasData)
                        chunk.persons[pair.Key] = pair.Value.Flush();
                }

                result.chunks[c] = chunk;
                buildProgress = (float)end / Mathf.Max(count, 1);
            }

            result.buildMilliseconds = stopwatch.ElapsedMilliseconds;

            if (generation == buildGeneration)
                builtCache = result;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[HomeGazeAnalyzer] ヒートの計算に失敗しました。\n{e}");
        }
    }

    // 1フレーム分の注視記録を作り、人物ごとのヒートを scratch に足す(別スレッドから呼ぶ)
    private static void ComputeSample(int index, Sample sample, ref Cone cone, ConeParams parameters, VertexCloud[] heatClouds,
                                      List<VertexCloud>[] targetClouds, string[] targetNames,
                                      Dictionary<int, ScratchHeat> scratch, HeatCache result)
    {
        HomeGazeFrameRecord[] frameRecords = new HomeGazeFrameRecord[sample.persons.Count];
        int[] best = new int[sample.persons.Count];

        for (int p = 0; p < sample.persons.Count; p++)
        {
            Person person = sample.persons[p];
            HomeGazeFrameRecord record = new HomeGazeFrameRecord
            {
                timeSec = sample.time,
                durationSec = sample.duration,
                personId = person.id,
                gazeTarget = NoDataLabel,
                gazeAngle = -1f
            };

            for (int t = 0; t < targetClouds.Length; t++)
            {
                record.heats.Add(0f);
                record.angles.Add(-1f);
            }

            frameRecords[p] = record;
            best[p] = -1;

            if (!person.hasGaze)
                continue;

            cone.Set(person.origin, person.direction);

            if (!scratch.TryGetValue(person.id, out ScratchHeat personScratch))
            {
                personScratch = new ScratchHeat(heatClouds);
                scratch[person.id] = personScratch;
            }

            for (int m = 0; m < heatClouds.Length; m++)
                AccumulateCone(heatClouds[m], cone, parameters, personScratch.values[m], 1f, personScratch.touched[m], false, out _, out _);

            float bestAngle = float.MaxValue;
            int bestIndex = -1;

            for (int t = 0; t < targetClouds.Length; t++)
            {
                float heat = 0f;
                float minAngle = float.MaxValue;

                foreach (VertexCloud cloud in targetClouds[t])
                {
                    if (AccumulateCone(cloud, cone, parameters, null, 1f, null, true, out float cloudHeat, out float cloudAngle))
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
                record.hitTargets.Add(targetNames[t]);

                if (minAngleDeg < bestAngle)
                {
                    bestAngle = minAngleDeg;
                    bestIndex = t;
                }
            }

            if (bestIndex >= 0)
            {
                record.gazeTarget = targetNames[bestIndex];
                record.gazeAngle = bestAngle;
            }
            else
            {
                record.gazeTarget = NoHitLabel;
            }

            best[p] = bestIndex;
        }

        result.records[index] = frameRecords;
        result.bestTargets[index] = best;
    }

    // ------------------------------------------------------------
    // 表示中のヒート・スコア(ヒートの時間範囲に合わせる)
    // ------------------------------------------------------------
    private void ClearDisplayedAnalysis()
    {
        analyzedStart = 0;
        nextSample = 0;

        foreach (HeatMesh heatMesh in heatMeshes)
            System.Array.Clear(heatMesh.heat, 0, heatMesh.heat.Length);

        RecomputeScores();
        colorsDirty = true;
        SetNormalizeHeat(false);
    }

    // 表示中のヒートとスコアを、ヒートの時間範囲に合わせる。
    //   ・範囲が少し動いただけ(再生で進んだ など)… 出入りしたフレームだけ足し引きする
    //   ・大きく動いた・人物の選択を変えた … 小計から作り直す
    //   ・スライダーをドラッグしている間 … 何もしない(離したときに force で呼ぶ)
    private void UpdateAnalysis(bool force)
    {
        if (cache == null)
            return;

        if (isScrubbing && !force)
            return;

        GetHeatWindow(out float windowStart, out float windowEnd);

        int start = FindSampleIndex(windowStart - 0.0001f) + 1; // windowStart 以上の最初のフレーム
        int end = Mathf.Max(start, FindSampleIndex(windowEnd) + 1);  // windowEnd 以下の最後のフレームの次

        if (!displayDirty && start == analyzedStart && end == nextSample)
            return;

        bool overlap = start < nextSample && analyzedStart < end;
        int change = Mathf.Abs(start - analyzedStart) + Mathf.Abs(end - nextSample);
        float[][] display = GetDisplayArrays();
        ICollection<int> persons = GetDisplayPersonFilter();

        if (displayDirty || !overlap || change > DifferentialLimit)
        {
            foreach (float[] heat in display)
                System.Array.Clear(heat, 0, heat.Length);

            AddWindowHeat(start, end, display, persons);
        }
        else
        {
            if (start > analyzedStart)
                AddSamplesHeat(analyzedStart, start, -1f, display, persons);
            else if (start < analyzedStart)
                AddSamplesHeat(start, analyzedStart, 1f, display, persons);

            if (end > nextSample)
                AddSamplesHeat(nextSample, end, 1f, display, persons);
            else if (end < nextSample)
                AddSamplesHeat(end, nextSample, -1f, display, persons);
        }

        analyzedStart = start;
        nextSample = end;
        displayDirty = false;
        RecomputeScores();
        colorsDirty = true;
        // 正規化は押した時点のヒートで計算しているので、ヒートが変わったら元の表示に戻す
        SetNormalizeHeat(false);
    }

    private float[][] GetDisplayArrays()
    {
        float[][] arrays = new float[heatMeshes.Count][];

        for (int m = 0; m < heatMeshes.Count; m++)
            arrays[m] = heatMeshes[m].heat;

        return arrays;
    }

    // 表示する人物(null なら全員)
    private ICollection<int> GetDisplayPersonFilter()
    {
        return selectedPersonIds.Count == 0 ? null : selectedPersonIds;
    }

    // samples[start] 〜 samples[end - 1] のヒートを arrays に足す。範囲に丸ごと入る小計はそのまま足し、端のフレームだけコーンを計算する
    private void AddWindowHeat(int start, int end, float[][] arrays, ICollection<int> persons)
    {
        int firstChunk = (start + CacheChunkSize - 1) / CacheChunkSize;
        int lastChunk = end / CacheChunkSize; // [firstChunk, lastChunk) が範囲に丸ごと入る

        if (firstChunk >= lastChunk)
        {
            AddSamplesHeat(start, end, 1f, arrays, persons);
            return;
        }

        AddSamplesHeat(start, firstChunk * CacheChunkSize, 1f, arrays, persons);

        for (int c = firstChunk; c < lastChunk; c++)
        {
            foreach (KeyValuePair<int, SparseHeat[]> pair in cache.chunks[c].persons)
            {
                if (persons != null && !persons.Contains(pair.Key))
                    continue;

                for (int m = 0; m < arrays.Length; m++)
                {
                    SparseHeat sparse = pair.Value[m];
                    float[] heat = arrays[m];

                    for (int k = 0; k < sparse.indices.Length; k++)
                        heat[sparse.indices[k]] += sparse.values[k];
                }
            }
        }

        AddSamplesHeat(lastChunk * CacheChunkSize, end, 1f, arrays, persons);
    }

    // samples[start] 〜 samples[end - 1] の視線コーンを計算して、ヒートに sign 倍して足す(-1 で引く)
    private void AddSamplesHeat(int start, int end, float sign, float[][] arrays, ICollection<int> persons)
    {
        Cone cone = new Cone(cache.parameters.angleDeg, cache.parameters.distance);

        for (int i = start; i < end; i++)
        {
            foreach (Person person in samples[i].persons)
            {
                if (!person.hasGaze || (persons != null && !persons.Contains(person.id)))
                    continue;

                cone.Set(person.origin, person.direction);

                for (int m = 0; m < arrays.Length; m++)
                    AccumulateCone(heatMeshes[m].cloud, cone, cache.parameters, arrays[m], sign, null, false, out _, out _);
            }
        }
    }

    // 今の時間範囲の注視記録からスコアを数え直す(キャッシュの記録を足すだけなので軽い)
    private void RecomputeScores()
    {
        records.Clear();
        gazeFrames = 0;
        noTargetFrames = 0;
        noDataFrames = 0;
        gazeSeconds = 0f;
        noTargetSeconds = 0f;

        foreach (ScoreTarget target in targets)
        {
            target.totalHeat = 0f;
            target.hitFrames = 0;
            target.hitSeconds = 0f;
            target.gazeFrames = 0;
            target.gazeSeconds = 0f;
        }

        if (cache == null)
            return;

        for (int i = analyzedStart; i < nextSample; i++)
        {
            HomeGazeFrameRecord[] frameRecords = cache.records[i];
            int[] best = cache.bestTargets[i];

            for (int p = 0; p < frameRecords.Length; p++)
            {
                HomeGazeFrameRecord record = frameRecords[p];
                records.Add(record);

                if (record.gazeTarget == NoDataLabel)
                {
                    noDataFrames++;
                    continue;
                }

                gazeFrames++;
                gazeSeconds += record.durationSec;

                for (int t = 0; t < targets.Count && t < record.angles.Count; t++)
                {
                    if (record.angles[t] < 0f)
                        continue;

                    targets[t].totalHeat += record.heats[t];
                    targets[t].hitFrames++;
                    targets[t].hitSeconds += record.durationSec;
                }

                if (best[p] >= 0 && best[p] < targets.Count)
                {
                    targets[best[p]].gazeFrames++;
                    targets[best[p]].gazeSeconds += record.durationSec;
                }
                else
                {
                    noTargetFrames++;
                    noTargetSeconds += record.durationSec;
                }
            }
        }
    }

    // ScoreHeat.AddConeScores / ConeHeatMapShow.AddConeHeat と同じ判定(別スレッドからも呼ぶので、インスタンスのフィールドは使わない)。
    // heat が null でなければ頂点ごとのヒートを scale 倍して足す(touched があれば、0 から増えた頂点の番号を入れる)。
    // needAngle のときだけコーン軸との最小角度を求める。コーンに入った頂点があれば true。
    private static bool AccumulateCone(VertexCloud cloud, Cone cone, ConeParams parameters, float[] heat, float scale,
                                       List<int> touched, bool needAngle, out float totalHeat, out float minAngleRad)
    {
        totalHeat = 0f;
        minAngleRad = float.MaxValue;

        if (!VertexCloud.MayIntersect(cloud.center, cloud.radius, cone))
            return false;

        bool hit = false;
        float bestDot = -1f;
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

                float weight = 1f;

                // 角度(Acos)は中心ほど重くするときだけ頂点ごとに求める
                if (parameters.centerWeighted)
                {
                    float angle = Mathf.Acos(Mathf.Clamp(dot, -1f, 1f));
                    weight = 1f - angle / cone.angleRad;
                    weight *= weight;
                }

                float added = parameters.heatPerHit * weight;
                totalHeat += added;

                if (heat != null)
                {
                    if (touched != null && heat[i] == 0f)
                        touched.Add(i);

                    heat[i] += added * scale;
                }

                if (dot > bestDot)
                    bestDot = dot;

                hit = true;
            }
        }

        if (hit && needAngle)
            minAngleRad = Mathf.Acos(Mathf.Clamp(bestDot, -1f, 1f));

        return hit;
    }

    private void ApplyColors()
    {
        colorsDirty = false;
        float max = normalizeHeat ? normalizedMaxHeat : Mathf.Max(maxHeatDisplay, 0.0001f);

        foreach (HeatMesh heatMesh in heatMeshes)
        {
            // heat は選んだ人物(誰も選んでいなければ全員)の今の時間範囲のヒート
            // RedOverlay も Palette も α をヒート量として使う(ConeHeatMapShow と同じ色)
            float[] heat = heatMesh.heat;

            for (int i = 0; i < heatMesh.colors.Length; i++)
                heatMesh.colors[i] = new Color(1f, 0f, 0f, Mathf.Clamp01(heat[i] / max));

            heatMesh.mesh.colors = heatMesh.colors;
        }
    }

    // 正規化: 今表示しているヒートを、面積で重み付けした合計で割る(Normalize を押したときに一度だけ計算する)。
    //   頂点 i の面積 A_i = その頂点を含む三角形の面積(ワールド座標)の 1/3 の合計
    //   合計 H = Σ heat_i × A_i                … 部屋全体で見られた量(頂点の細かさに左右されない)
    //   密度 d_i = heat_i / H                  … 単位面積あたりの見られた割合(全体で積分すると 1)
    //   部屋オブジェクト/対象物体の割合 = Σ(そのメッシュ) heat_i × A_i / H
    // 色は d_i / (d の最大) = heat_i / (heat の最大) で、最も見られた場所が最大の色になる
    private void ComputeNormalization()
    {
        double total = 0.0;
        float maxHeat = 0f;
        normalizedMeshShares.Clear();

        foreach (HeatMesh heatMesh in heatMeshes)
        {
            // 頂点の面積は最初に正規化したときに一度だけ求める
            if (heatMesh.vertexAreas == null)
                heatMesh.vertexAreas = ComputeVertexAreas(heatMesh);

            double meshTotal = 0.0;
            float[] heat = heatMesh.heat;

            for (int i = 0; i < heat.Length; i++)
            {
                // 差分で足し引きした誤差で少しだけ負になることがあるので数えない
                if (heat[i] <= 0f)
                    continue;

                meshTotal += heat[i] * heatMesh.vertexAreas[i];

                if (heat[i] > maxHeat)
                    maxHeat = heat[i];
            }

            total += meshTotal;
            normalizedMeshShares.Add(new KeyValuePair<string, float>(heatMesh.name, (float)meshTotal));
        }

        normalizedTotalHeat = (float)total;

        for (int i = 0; i < normalizedMeshShares.Count; i++)
        {
            float share = total > 0.0 ? (float)(normalizedMeshShares[i].Value / total) : 0f;
            normalizedMeshShares[i] = new KeyValuePair<string, float>(normalizedMeshShares[i].Key, share);
        }

        normalizedMeshShares.Sort((a, b) => b.Value.CompareTo(a.Value));

        // 誰も見ていなければ何も塗らない
        normalizedMaxHeat = maxHeat > 0f ? maxHeat : float.MaxValue;
    }

    // 頂点ごとの面積(m²): 頂点を含む三角形の面積(ワールド座標)の 1/3 を足す
    private static float[] ComputeVertexAreas(HeatMesh heatMesh)
    {
        Mesh mesh = heatMesh.mesh;
        Vector3[] vertices = mesh.vertices;
        Matrix4x4 toWorld = heatMesh.overlayRenderer.transform.localToWorldMatrix;
        float[] areas = new float[vertices.Length];

        for (int i = 0; i < vertices.Length; i++)
            vertices[i] = toWorld.MultiplyPoint3x4(vertices[i]);

        for (int sub = 0; sub < mesh.subMeshCount; sub++)
        {
            if (mesh.GetTopology(sub) != MeshTopology.Triangles)
                continue;

            int[] indices = mesh.GetIndices(sub);

            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                int a = indices[t];
                int b = indices[t + 1];
                int c = indices[t + 2];
                float third = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]).magnitude / 6f;
                areas[a] += third;
                areas[b] += third;
                areas[c] += third;
            }
        }

        return areas;
    }

    // ------------------------------------------------------------
    // Persons (人物のまとめ方はシーン3で済ませてある。grouped の骨格では人物ID = シーン3のグループの番号)
    // ------------------------------------------------------------
    // 一覧に出す単位(人物1人)
    private class DisplayUnit
    {
        public string name;
        public int[] ids;
    }

    private List<DisplayUnit> GetDisplayUnits()
    {
        List<DisplayUnit> units = new List<DisplayUnit>();

        foreach (int id in personFrameCounts.Keys)
            units.Add(new DisplayUnit { name = GetPersonName(id), ids = new[] { id } });

        return units;
    }

    // 人物の名前(grouped の骨格ではシーン3のグループ名、それ以外は person_<ID>)
    private string GetPersonName(int id) =>
        personNames.TryGetValue(id, out string name) && !string.IsNullOrEmpty(name) ? name : $"person_{id}";

    private string GetUnitLabel(DisplayUnit unit)
    {
        int frames = 0;

        foreach (int id in unit.ids)
        {
            if (personFrameCounts.TryGetValue(id, out int count))
                frames += count;
        }

        return $"{unit.name} ({frames} frames)";
    }

    // 人物の色(人物IDの順。grouped の骨格ではシーン3のグループの色と同じ)
    private Color GetPersonColor(int personId)
    {
        return personColors.Length > 0 ? personColors[Mathf.Abs(personId) % personColors.Length] : Color.white;
    }

    // ------------------------------------------------------------
    // Person selection (ヒートマップ。複数選べる)
    // ------------------------------------------------------------
    // 表示するヒートマップの人物をまとめて指定する(空なら全員)。
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

        // 表示中のヒートを、選んだ人物の分でキャッシュから作り直す
        displayDirty = true;
        UpdateAnalysis(true);
        ApplyColors();
        UpdateText();
    }

    private void OnHeatUnitToggle(DisplayUnit unit, bool isOn)
    {
        HashSet<int> ids = new HashSet<int>(selectedPersonIds);

        if (unit == null)
        {
            // All を選んだら個別の選択を外す(All を外そうとしたときは何もしない)
            if (isOn)
                ids.Clear();
        }
        else if (isOn)
        {
            ids.UnionWith(unit.ids);
        }
        else
        {
            ids.ExceptWith(unit.ids);
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
        if (heatAllToggle != null)
            heatAllToggle.SetIsOnWithoutNotify(selectedPersonIds.Count == 0);

        foreach (KeyValuePair<DisplayUnit, Toggle> pair in heatUnitToggles)
            pair.Value.SetIsOnWithoutNotify(selectedPersonIds.Count > 0 && ContainsAll(selectedPersonIds, pair.Key.ids));

        if (personHeaderText != null)
            personHeaderText.text = $"Heat Map : {GetDisplayPersonsLabel()}";
    }

    private static bool ContainsAll(ICollection<int> set, int[] ids)
    {
        foreach (int id in ids)
        {
            if (!set.Contains(id))
                return false;
        }

        return ids.Length > 0;
    }

    // 選んでいる人物の表示名
    private string GetDisplayPersonsLabel()
    {
        if (selectedPersonIds.Count == 0)
            return "All";

        List<string> names = new List<string>();
        HashSet<int> named = new HashSet<int>();

        foreach (DisplayUnit unit in GetDisplayUnits())
        {
            if (ContainsAll(selectedPersonIds, unit.ids))
            {
                names.Add(unit.name);
                named.UnionWith(unit.ids);
            }
        }

        foreach (int id in selectedPersonIds)
        {
            if (!named.Contains(id))
                names.Add(GetPersonName(id));
        }

        return string.Join(", ", names);
    }

    // 骨格を読み込んだ後に、ヒートマップの人物の一覧を作る(画面右上)
    //   [Heat Map : All  v]   … 押すと一覧を開閉する
    //   [x] All (123 frames)
    //   [ ] mother (100 frames)   … 人物(シーン3のグループ)
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

        RectTransform root = CreateDropdown(canvas, "PersonSelector", 0, out personHeaderText, out heatListRect, out heatListContent);
        root.GetComponent<Button>().onClick.AddListener(() => heatListRect.gameObject.SetActive(!heatListRect.gameObject.activeSelf));
        heatListRect.gameObject.SetActive(false);

        RebuildHeatRows();
        SetDisplayPersons(displayPersonIds);
    }

    private void RebuildHeatRows()
    {
        if (heatListContent == null)
            return;

        ClearChildren(heatListContent);
        heatUnitToggles.Clear();

        List<DisplayUnit> units = GetDisplayUnits();
        int total = 0;

        foreach (int count in personFrameCounts.Values)
            total += count;

        heatAllToggle = CreateToggleRow(heatListContent, $"All ({total} frames)", 0f, DropdownRowHeight);
        heatAllToggle.onValueChanged.AddListener(isOn => OnHeatUnitToggle(null, isOn));

        for (int i = 0; i < units.Count; i++)
        {
            DisplayUnit unit = units[i];
            Toggle toggle = CreateToggleRow(heatListContent, GetUnitLabel(unit), (i + 1) * DropdownRowHeight, DropdownRowHeight);
            toggle.onValueChanged.AddListener(isOn => OnHeatUnitToggle(unit, isOn));
            heatUnitToggles.Add(new KeyValuePair<DisplayUnit, Toggle>(unit, toggle));
        }

        SetDropdownRows(heatListRect, heatListContent, units.Count + 1);
        RefreshPersonToggles();
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

        foreach (KeyValuePair<int, int> pair in personFrameCounts)
        {
            int id = pair.Key;
            personFirstTimes.TryGetValue(id, out float firstTime);
            Toggle toggle = CreateToggleRow(skeletonListContent, $"{GetPersonName(id)} ({firstTime:F1} s -, {pair.Value} frames)",
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

    // ------------------------------------------------------------
    // Dropdown helpers (画面右上に右から並べる一覧)
    // ------------------------------------------------------------
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

    private static void ClearChildren(RectTransform parent)
    {
        for (int i = parent.childCount - 1; i >= 0; i--)
        {
            Transform child = parent.GetChild(i);
            child.SetParent(null, false);
            Destroy(child.gameObject);
        }
    }

    // ------------------------------------------------------------
    // Timeline (画面下)
    // ------------------------------------------------------------
    //   [>]  12.3 / 60.0 s  [=========o-----------------]   … 再生バー。ドラッグで再生位置を動かす
    //                        (オレンジ: ヒートの時間範囲 / 赤: 今ヒートに数えている部分)
    //   Heat from [--o------] 5.0 s   to [-------o--] 40.0 s   [x] Follow playhead   [Full]
    private void SetupTimeline()
    {
        if (!showTimeline)
            return;

        Canvas canvas = frameText != null ? frameText.canvas : FindObjectOfType<Canvas>();

        if (canvas == null)
        {
            Debug.LogWarning("[HomeGazeAnalyzer] Canvas が無いため、再生バーを作れません。");
            return;
        }

        if (uiSprite == null)
        {
#if UNITY_EDITOR
            uiSprite = UnityEditor.AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
            checkmarkSprite = UnityEditor.AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Checkmark.psd");
#endif
        }

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

        // 再生バーの下地に、ヒートの時間範囲(オレンジ)と今数えている部分(赤)を重ねる
        RectTransform background = (RectTransform)timelineSlider.transform.Find("Background");
        rangeHighlight = CreateRect("HeatRange", background);
        AddImage(rangeHighlight.gameObject, null, new Color(1f, 0.6f, 0.1f, 0.45f)).raycastTarget = false;
        analyzedHighlight = CreateRect("HeatCounted", background);
        AddImage(analyzedHighlight.gameObject, null, new Color(0.9f, 0.1f, 0.1f, 0.55f)).raycastTarget = false;

        // 2行目: ヒートの時間範囲
        RectTransform row2 = CreateRow(root, 48f, 44f);

        TMP_Text fromLabel = CreateText("From", row2, TextAlignmentOptions.MidlineLeft);
        fromLabel.text = "Heat from";
        PlaceLeft(fromLabel.rectTransform, 10f, 95f);

        rangeStartSlider = CreateSlider(row2, new Color(1f, 0.6f, 0.1f));
        PlaceLeft((RectTransform)rangeStartSlider.transform, 105f, 260f, 10f);
        rangeStartSlider.onValueChanged.AddListener(value =>
        {
            float end = heatRangeEnd < 0f ? duration : heatRangeEnd;
            heatRangeStart = Mathf.Min(value, end);
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
            float end = Mathf.Max(value, heatRangeStart);
            heatRangeEnd = end >= duration - 0.001f ? -1f : end;
        });

        rangeEndText = CreateText("ToValue", row2, TextAlignmentOptions.MidlineLeft);
        PlaceLeft(rangeEndText.rectTransform, 750f, 80f);

        followToggle = CreateToggleRow(row2, "Follow playhead", 0f, 44f);
        RectTransform followRect = (RectTransform)followToggle.transform;
        followRect.anchorMin = followRect.anchorMax = new Vector2(0f, 0.5f);
        followRect.pivot = new Vector2(0f, 0.5f);
        followRect.anchoredPosition = new Vector2(835f, 0f);
        followRect.sizeDelta = new Vector2(210f, 36f);
        followToggle.SetIsOnWithoutNotify(heatFollowsPlayhead);
        followToggle.onValueChanged.AddListener(isOn => heatFollowsPlayhead = isOn);

        Button full = CreateButton(row2, "Full", 1055f, 70f, out _);
        full.onClick.AddListener(() => SetHeatRange(0f, -1f));

        Button normalize = CreateButton(row2, "Normalize", 1135f, 120f, out _);
        normalizeButtonImage = (Image)normalize.targetGraphic;
        normalize.onClick.AddListener(ToggleNormalizeHeat);

        foreach (Slider slider in new[] { timelineSlider, rangeStartSlider, rangeEndSlider })
        {
            slider.minValue = 0f;
            slider.maxValue = Mathf.Max(duration, 0.001f);
            AddScrubEvents(slider.gameObject);
        }

        UpdateTimelineUI();
    }

    // ドラッグ中はヒートの計算し直しを間引き、離したらすぐ作り直す
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
        up.callback.AddListener(_ =>
        {
            isScrubbing = false;
            UpdateAnalysis(true);
        });
        trigger.triggers.Add(up);
    }

    private void UpdateTimelineUI()
    {
        if (timelineSlider == null)
            return;

        GetHeatWindow(out float windowStart, out float windowEnd);
        float rangeEnd = heatRangeEnd < 0f ? duration : Mathf.Min(heatRangeEnd, duration);
        float max = Mathf.Max(duration, 0.001f);

        timelineSlider.SetValueWithoutNotify(playbackTime);
        rangeStartSlider.SetValueWithoutNotify(windowStart);
        rangeEndSlider.SetValueWithoutNotify(rangeEnd);
        followToggle.SetIsOnWithoutNotify(heatFollowsPlayhead);

        playPauseText.text = isPlaying ? "||" : ">";
        timeText.text = cache == null
            ? $"{playbackTime:F1} s (heat {buildProgress * 100f:F0}%)"
            : $"{playbackTime:F1} / {duration:F1} s";
        rangeStartText.text = $"{windowStart:F1} s";
        rangeEndText.text = $"{rangeEnd:F1} s";

        SetHorizontalSpan(rangeHighlight, windowStart / max, rangeEnd / max);

        // 赤: 実際にヒートに数えている部分(ドラッグ中は離すまで前の範囲のまま)
        if (nextSample > analyzedStart)
            SetHorizontalSpan(analyzedHighlight, samples[analyzedStart].time / max, samples[nextSample - 1].time / max);
        else
            SetHorizontalSpan(analyzedHighlight, 0f, 0f);
    }

    private static void SetHorizontalSpan(RectTransform rect, float from, float to)
    {
        rect.anchorMin = new Vector2(Mathf.Clamp01(from), 0f);
        rect.anchorMax = new Vector2(Mathf.Clamp01(Mathf.Max(from, to)), 1f);
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    // top: 親の上端からの位置
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

    // 行の左端から x の位置に幅 width で置く(縦は行いっぱい - margin)
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
        return CreateButton(parent, label, x, width, out text, Color.white, new Color(0.2f, 0.2f, 0.2f));
    }

    private Button CreateButton(RectTransform parent, string label, float x, float width, out TMP_Text text,
                                Color background, Color textColor)
    {
        RectTransform rect = CreateRect("Button", parent);
        PlaceLeft(rect, x, width, 4f);
        Button button = rect.gameObject.AddComponent<Button>();
        button.targetGraphic = AddImage(rect.gameObject, uiSprite, background);

        text = CreateText("Label", rect, TextAlignmentOptions.Center);
        SetStretch(text.rectTransform, new Vector2(4f, 0f), new Vector2(-4f, 0f));
        text.text = label;
        text.color = textColor;
        text.fontStyle = FontStyles.Bold;

        // ボタンが低い(一覧の行の中など)と、Ellipsis のままでは1行が入り切らず文字が全部消えるので、
        // はみ出しても消さず、ボタンの大きさに合わせて文字を小さくする
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
        Image fillImage = AddImage(fill.gameObject, uiSprite, new Color(fillColor.r, fillColor.g, fillColor.b, 0.35f));
        fillImage.raycastTarget = false;

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
        GetHeatWindow(out float windowStart, out float windowEnd);

        return new HomeGazeParameters
        {
            heatRangeStartSec = windowStart,
            heatRangeEndSec = windowEnd,
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
            processedSamples = nextSample - analyzedStart
        };

        // 表示中のヒートは選んだ人物だけのことがあるので、保存用に今の時間範囲の全員の合計と人物ごとのヒートを作る
        float[][] allHeat = NewHeatArrays();
        AddWindowHeat(analyzedStart, nextSample, allHeat, null);

        Dictionary<int, float[][]> personHeats = new Dictionary<int, float[][]>();

        foreach (int id in personFrameCounts.Keys)
        {
            float[][] arrays = NewHeatArrays();
            AddWindowHeat(analyzedStart, nextSample, arrays, new HashSet<int> { id });
            personHeats[id] = arrays;
        }

        for (int m = 0; m < heatMeshes.Count; m++)
        {
            HeatMesh heatMesh = heatMeshes[m];
            GetHeatStats(allHeat[m], out float max, out float total);

            HomeGazeHeatMapMesh meshData = new HomeGazeHeatMapMesh
            {
                name = heatMesh.name,
                path = heatMesh.path,
                vertexCount = allHeat[m].Length,
                maxHeat = max,
                totalHeat = total,
                heat = allHeat[m]
            };

            foreach (int id in personFrameCounts.Keys)
            {
                float[] personHeat = personHeats[id][m];
                GetHeatStats(personHeat, out float personMax, out float personTotal);

                // この時間範囲で視線が無い人物は書かない
                if (personTotal <= 0f)
                    continue;

                meshData.persons.Add(new HomeGazePersonHeat
                {
                    personId = id,
                    name = GetPersonName(id),
                    maxHeat = personMax,
                    totalHeat = personTotal,
                    heat = personHeat
                });
            }

            data.meshes.Add(meshData);
        }

        return data;
    }

    private float[][] NewHeatArrays()
    {
        float[][] arrays = new float[heatMeshes.Count][];

        for (int m = 0; m < heatMeshes.Count; m++)
            arrays[m] = new float[heatMeshes[m].heat.Length];

        return arrays;
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
        // フレームごとの記録に、人物の名前を書く
        foreach (HomeGazeFrameRecord record in records)
            record.personName = GetPersonName(record.personId);

        HomeGazeScoreList data = new HomeGazeScoreList
        {
            experimentName = env.ExperimentName,
            subjectName = subjectName,
            createdAt = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            parameters = CreateParameters(),
            sampleCount = samples.Count,
            processedSamples = nextSample - analyzedStart,
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

        data.people = CreatePeopleScores();
        return data;
    }

    // 人物ごとのスコア。今の時間範囲の注視記録を数える(RecomputeScores と同じ数え方)
    private List<HomeGazePersonScore> CreatePeopleScores()
    {
        List<HomeGazePersonScore> people = new List<HomeGazePersonScore>();
        Dictionary<int, HomeGazePersonScore> byPersonId = new Dictionary<int, HomeGazePersonScore>();

        foreach (DisplayUnit unit in GetDisplayUnits())
        {
            HomeGazePersonScore score = new HomeGazePersonScore
            {
                name = unit.name,
                personIds = new List<int>(unit.ids)
            };

            foreach (ScoreTarget target in targets)
                score.targets.Add(new HomeGazeTargetScore { name = target.name });

            foreach (int id in unit.ids)
                byPersonId[id] = score;

            people.Add(score);
        }

        if (cache == null)
            return people;

        for (int i = analyzedStart; i < nextSample; i++)
        {
            HomeGazeFrameRecord[] frameRecords = cache.records[i];
            int[] best = cache.bestTargets[i];

            for (int p = 0; p < frameRecords.Length; p++)
            {
                HomeGazeFrameRecord record = frameRecords[p];

                if (!byPersonId.TryGetValue(record.personId, out HomeGazePersonScore score))
                    continue;

                score.frameCount++;

                if (record.gazeTarget == NoDataLabel)
                {
                    score.noDataFrames++;
                    continue;
                }

                score.gazeFrames++;
                score.gazeSeconds += record.durationSec;

                for (int t = 0; t < score.targets.Count && t < record.angles.Count; t++)
                {
                    if (record.angles[t] < 0f)
                        continue;

                    score.targets[t].totalHeat += record.heats[t];
                    score.targets[t].hitFrames++;
                    score.targets[t].hitSeconds += record.durationSec;
                }

                if (best[p] >= 0 && best[p] < score.targets.Count)
                {
                    score.targets[best[p]].gazeFrames++;
                    score.targets[best[p]].gazeSeconds += record.durationSec;
                }
                else
                {
                    score.noTargetFrames++;
                    score.noTargetSeconds += record.durationSec;
                }
            }
        }

        return people;
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
                // Skeleton の一覧で人物を選んでいれば、その人だけ表示する
                if (skeletonPersonId >= 0 && person.id != skeletonPersonId)
                    continue;

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
        Color color = GetPersonColor(id);
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
            GetHeatWindow(out float windowStart, out float windowEnd);
            string heatRange = cache == null
                ? $"Heat : preparing {buildProgress * 100f:F0}% (heat map appears when ready)"
                : $"Heat Range : {windowStart:F1} - {windowEnd:F1} s ({nextSample - analyzedStart} frames)";
            frameText.text =
                $"{subjectName} ({OutputName})  Time : {playbackTime:F1} / {duration:F1} s\n" +
                $"{heatRange}\n" +
                $"Heat Map : {GetDisplayPersonsLabel()} ({heatMapDisplay}{(normalizeHeat ? ", Normalized" : "")})  Skeleton : {SkeletonLabel}";
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

        // 正規化したときの、部屋オブジェクト/対象物体ごとの割合(面積で重み付け)
        if (normalizeHeat)
        {
            text.AppendLine($"Heat Map Share (area-weighted, total {normalizedTotalHeat:F2})");

            for (int i = 0; i < normalizedMeshShares.Count && i < NormalizedShareLines; i++)
            {
                if (normalizedMeshShares[i].Value <= 0f)
                    break;

                text.AppendLine($"  {normalizedMeshShares[i].Key} : {normalizedMeshShares[i].Value * 100f:F1}%");
            }
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
        // 表示中のヒート(選んだ人物・今の時間範囲。頂点と同じ並び)
        public float[] heat;
        public Color[] colors;
        // 頂点ごとの面積(m²。正規化で使う。最初に正規化したときに作る)
        public float[] vertexAreas;

        // 元の部屋のレンダラーと、上に重ねたヒートマップ表示用のレンダラー
        public MeshRenderer sourceRenderer;
        public MeshRenderer overlayRenderer;
        public Material[] paletteMaterials;
    }

    // 視線コーンの設定(これが変わったらキャッシュを作り直す)
    private struct ConeParams : System.IEquatable<ConeParams>
    {
        public readonly float angleDeg;
        public readonly float distance;
        public readonly bool centerWeighted;
        public readonly float heatPerHit;

        public ConeParams(float angleDeg, float distance, bool centerWeighted, float heatPerHit)
        {
            this.angleDeg = angleDeg;
            this.distance = distance;
            this.centerWeighted = centerWeighted;
            this.heatPerHit = heatPerHit;
        }

        public bool Equals(ConeParams other) =>
            angleDeg == other.angleDeg && distance == other.distance &&
            centerWeighted == other.centerWeighted && heatPerHit == other.heatPerHit;

        public override bool Equals(object obj) => obj is ConeParams other && Equals(other);

        public override int GetHashCode() => angleDeg.GetHashCode() ^ distance.GetHashCode() ^ heatPerHit.GetHashCode();
    }

    // 全フレームの視線コーンの結果
    private class HeatCache
    {
        public int generation;
        public ConeParams parameters;
        public long buildMilliseconds;
        // CacheChunkSize フレームごとの人物別ヒートの小計
        public HeatChunk[] chunks;
        // フレームごとの注視記録(人物ごと)と、注視対象の番号(-1 は対象なし・視線なし)
        public HomeGazeFrameRecord[][] records;
        public int[][] bestTargets;
    }

    private class HeatChunk
    {
        // 人物ID → ヒートマップのメッシュごとの小計
        public readonly Dictionary<int, SparseHeat[]> persons = new Dictionary<int, SparseHeat[]>();
    }

    // ヒートが入った頂点だけの一覧
    private class SparseHeat
    {
        public int[] indices;
        public float[] values;
    }

    // キャッシュを作るときの人物ごとの作業領域(使い回す)
    private class ScratchHeat
    {
        public readonly float[][] values;
        public readonly List<int>[] touched;

        public ScratchHeat(VertexCloud[] clouds)
        {
            values = new float[clouds.Length][];
            touched = new List<int>[clouds.Length];

            for (int m = 0; m < clouds.Length; m++)
            {
                values[m] = new float[clouds[m].positions.Length];
                touched[m] = new List<int>();
            }
        }

        public bool HasData
        {
            get
            {
                foreach (List<int> list in touched)
                {
                    if (list.Count > 0)
                        return true;
                }

                return false;
            }
        }

        // 溜まったヒートを取り出して作業領域を空にする
        public SparseHeat[] Flush()
        {
            SparseHeat[] result = new SparseHeat[values.Length];

            for (int m = 0; m < values.Length; m++)
            {
                List<int> list = touched[m];
                float[] heat = values[m];
                SparseHeat sparse = new SparseHeat { indices = list.ToArray(), values = new float[list.Count] };

                for (int k = 0; k < list.Count; k++)
                {
                    int i = list[k];
                    sparse.values[k] = heat[i];
                    heat[i] = 0f; // 同じ番号が2回入っていても、2回目は 0 になる
                }

                list.Clear();
                result[m] = sparse;
            }

            return result;
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
