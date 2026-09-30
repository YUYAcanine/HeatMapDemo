using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Azure.Kinect.BodyTracking;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// シーン3(3HeadDirFilter)用。骨格データを整えて、シーン4/5で使う「人物ごとの骨格」を作る。
// 信頼度の比較・人物IDのまとめ方はすべてここで行い、シーン4/5は出来上がったファイルを読むだけにする。
//
// 1. 骨格を用意する
//    Source = Kinect: 実験対象者のフォルダの *_skeleton.json(全キネクト分)を統合して
//                     Skeleton/<実験対象者>/Filtered/filtered_<ConfidenceMode>.json に保存する。
//                     保存済みのファイルが今の設定・今の作り方(FusionVersion)で作られていれば、統合し直さずに読み込む
//                     (Force Rebuild で作り直す。作り直すと人物IDが変わるので、グループも作り直す)。
//    Source = Image : Tools/GazePipeline が作った Filtered/filtered_Image.json を読み込むだけ(統合はしない)。
//
//    統合のロジック(SkeletonRealtimePublisher.FuseAndPublish と同じ考え方):
//      1. 各時刻で、全キネクトの最新フレームに写っている骨格を候補として集める
//      2. 候補を信頼度の高い順に並べる。信頼度は confidenceMode で選ぶ:
//           AllJoints  … 全関節の confidence の平均(Publisher と同じ)
//           HeadJoints … headJoints(頭部方向の推定に使う関節)だけの confidence の平均
//      3. 採用済みの骨格と Pelvis が sameBodyDistance 以内の候補は同一人物とみなして捨てる
//         (同一人物のうち信頼度の最も高い骨格だけが残る。sameBodyDistance より離れた骨格は別の人物として残る)
//      4. 採用した骨格を、trackMatchDistance 以内の既存の人物トラックに近い組から順に引き継ぐ(無ければ新しいトラック)。
//         1つのトラックは1時刻に1つの骨格にしか引き継がないので、同じ時刻に同じ人物IDが2つ付くことはない
//         (SkeletonRealtimePublisher は同じ人物IDが付くことがある)
//      5. trackTimeout 秒以上見つからなかったトラックは破棄する
//    リアルタイムの「最新フレーム」の代わりに、sampleInterval ごとの時刻でその時刻以前の最新フレームを使う。
//
// 2. グループ分け(画面右上の Groups)
//    同じ人に付いた人物IDをまとめる → Skeleton/<実験対象者>/Analysis/Groups/person_groups_<骨格>.json
//    グループに入れなかった人物IDは誤検出などとして使わない(灰色で表示する)。
//
// 3. 人物ごとの骨格(シーン4/5が読むファイル)
//    各時刻で、グループごとに頭の関節の信頼度(headJointConfidence)が最も高い骨格を1つだけ選び
//    Skeleton/<実験対象者>/Filtered/grouped_<Mode>.json に保存する(Save grouped ボタン。未保存のまま終了しても保存する)。
//    人物ID = グループの番号(0, 1, ...)、label = グループ名、sourceTrackId = 元の人物ID。
//    Image の headJointConfidence には、Tools/GazePipeline が出した体全体のスコアが入っている。
//
// 操作:
//   再生バー(画面下)  … 再生/一時停止(Space)・ドラッグで再生位置を動かす
//   Grouped only        … シーン4/5に渡す骨格(グループごとに1つ)だけを表示する
//   Skeleton(右上)    … 表示する人物(グループ)を1つ選ぶ。選ぶとその人が最初に出てくる時刻へ移動する
//   Groups(右上)      … 人物IDにチェックを入れて Make group / Ungroup でグループを解除
[RequireComponent(typeof(HomeEnvLoader))]
public class HomeSkeletonFilter : MonoBehaviour
{
    public enum ConfidenceMode
    {
        // 全関節の confidence の平均(SkeletonRealtimePublisher と同じ)
        AllJoints,
        // headJoints に指定した関節だけの confidence の平均
        HeadJoints
    }

    public enum Source
    {
        // 全キネクトの *_skeleton.json を統合する
        Kinect,
        // Tools/GazePipeline が作った filtered_Image.json を読み込む
        Image
    }

    // 統合の作り方の版。変えたら保存済みの filtered を作り直す
    //   1: 同じ時刻に同じ人物IDが2つ付くことがあった / 2: 人物IDは1時刻に1つ
    private const int FusionVersion = 2;

    [Header("Subject")]
    [HomeExperimentFolder(HomeExperimentFolderAttribute.Kind.Subject)]
    [SerializeField] private string subjectName = "";

    [Header("Source")]
    [Tooltip("Kinect: 全キネクトの骨格を統合する / Image: Tools/GazePipeline の filtered_Image.json を読み込む(統合はしない)")]
    [SerializeField] private Source source = Source.Kinect;
    [Tooltip("オン: 保存済みの filtered があっても統合し直す(人物IDが変わるので、グループも作り直す)")]
    [SerializeField] private bool forceRebuild = false;

    [Header("Confidence (Source = Kinect)")]
    [Tooltip("同一人物とみなした骨格のうち、どれを採用するかを決める信頼度の計算方法。")]
    [SerializeField] private ConfidenceMode confidenceMode = ConfidenceMode.AllJoints;
    [Tooltip("HeadJoints のときに信頼度の平均を取る関節(頭部方向の推定に使う関節)。")]
    [SerializeField] private JointId[] headJoints =
    {
        JointId.Head, JointId.Nose, JointId.EyeLeft, JointId.EyeRight, JointId.EarLeft, JointId.EarRight
    };

    [Header("Fusion (Source = Kinect)")]
    [Tooltip("この距離(m)以内のPelvis位置同士は同一人物とみなし、信頼度の最も高い骨格だけを採用する。\n" +
             "これより離れた骨格は別の人物(別の人物ID)として残す。")]
    [SerializeField] private float sameBodyDistance = 0.4f;
    [Tooltip("人物トラックをこの距離(m)以内なら同一人物として引き継ぐ(1つのトラックは1時刻に1つの骨格だけ)。")]
    [SerializeField] private float trackMatchDistance = 1.0f;
    [Tooltip("この秒数以上マッチしなかったトラックは破棄する。")]
    [SerializeField] private float trackTimeout = 2f;

    [Header("Sampling (Source = Kinect)")]
    [Tooltip("統合する時刻の間隔(秒)。Publisherのパブリッシュ間隔に相当。")]
    [SerializeField] private float sampleInterval = 1f / 30f;
    [Tooltip("各キネクトの最新フレームがこの秒数より古ければ、そのキネクトは使わない(記録が先に終わったキネクトなど)。")]
    [SerializeField] private float maxFrameAge = 0.2f;

    [Header("Visual")]
    [SerializeField] private GameObject jointPrefab;
    [Tooltip("空の場合は頂点カラー対応の Sprites/Default を使う。")]
    [SerializeField] private Material lineMaterial;
    [SerializeField] private float jointScale = 0.06f;
    [SerializeField] private float lineWidth = 0.02f;
    [Tooltip("グループごとの色(グループの番号順。シーン4/5の人物の色と同じ並び)。グループに入っていない人物IDは灰色")]
    [SerializeField] private Color[] trackColors =
    {
        Color.red, Color.green, Color.blue, Color.yellow, Color.cyan, Color.magenta
    };
    [SerializeField] private bool showHeadDirection = true;
    [SerializeField] private float headDirectionLength = 0.4f;
    [SerializeField] private float headDirectionWidth = 0.015f;
    [Tooltip("オン: シーン4/5に渡す骨格(グループごとに頭の信頼度が最も高い1つ)だけを表示する")]
    [SerializeField] private bool groupedOnly = false;

    [Header("Playback")]
    [SerializeField] private float playbackSpeed = 1f;
    [SerializeField] private bool loop = false;
    [SerializeField] private KeyCode playStopKey = KeyCode.Space;

    [Header("UI")]
    [SerializeField] private Button playButton;
    [SerializeField] private Button stopButton;
    [SerializeField] private TMP_Text frameText;

    private static readonly Color UngroupedColor = new Color(0.55f, 0.55f, 0.55f);

    private HomeEnvLoader env;
    private HomeFilteredSkeletonList data;
    private float[] frameTimes = new float[0];
    private Transform visualRoot;
    private HomeSkeletonBodyVisual.Style visualStyle;
    private readonly Dictionary<int, HomeSkeletonBodyVisual> visuals = new Dictionary<int, HomeSkeletonBodyVisual>();
    private float duration;
    private float playbackTime;
    private bool isPlaying;
    private int shownIndex = -1;

    // 人物IDごとのフレーム数と、最初に出てくる時刻(秒)
    private readonly SortedDictionary<int, int> personFrameCounts = new SortedDictionary<int, int>();
    private readonly Dictionary<int, float> personFirstTimes = new Dictionary<int, float>();

    // 同じ人の人物IDをまとめたグループと、人物ID → グループの番号
    private readonly List<HomePersonGroup> personGroups = new List<HomePersonGroup>();
    private readonly Dictionary<int, int> groupOfPerson = new Dictionary<int, int>();
    // grouped_<Mode>.json が今のグループと合っていない
    private bool groupedDirty;

    // 表示する人物(null なら全員)と、その表示名
    private HashSet<int> skeletonIds;
    private string skeletonLabel = "All";

    // UI
    private Sprite uiSprite;
    private Sprite checkmarkSprite;
    private Slider timelineSlider;
    private TMP_Text playPauseText;
    private TMP_Text timeText;
    private Toggle groupedOnlyToggle;
    private TMP_Text saveStatusText;
    private RectTransform skeletonListRect;
    private RectTransform skeletonListContent;
    private TMP_Text skeletonHeaderText;
    private Toggle skeletonAllToggle;
    private readonly List<KeyValuePair<DisplayUnit, Toggle>> skeletonUnitToggles = new List<KeyValuePair<DisplayUnit, Toggle>>();
    private RectTransform groupListRect;
    private RectTransform groupListContent;
    private TMP_Text groupHeaderText;
    private TMP_InputField groupNameInput;
    private readonly HashSet<int> groupCandidateIds = new HashSet<int>();

    // filtered の <Mode>(filtered_<Mode>.json / grouped_<Mode>.json)
    private string ModeName => source == Source.Image ? "Image" : confidenceMode.ToString();

    // 人物IDの元になった骨格データ(グループのファイル名に使う)
    private string PersonIdSourceName => $"filtered_{ModeName}";

    private void Awake()
    {
        env = GetComponent<HomeEnvLoader>();
    }

    private void Start()
    {
        visualRoot = new GameObject("FilteredSkeletonPlayback").transform;
        visualStyle = new HomeSkeletonBodyVisual.Style
        {
            jointPrefab = jointPrefab,
            lineMaterial = lineMaterial,
            jointScale = jointScale,
            lineWidth = lineWidth,
            showHeadDirection = showHeadDirection,
            headDirectionLength = headDirectionLength,
            headDirectionWidth = headDirectionWidth
        };

        // 以前 Analysis/ の直下に置いていたグループなどを種類ごとのフォルダへ移す
        HomeExperimentPaths.MigrateLegacyAnalysisFiles(env.ExperimentName, subjectName);

        LoadOrBuild();
        LoadPersonGroups();
        CheckGroupedFile();
        SetupUI();

        if (playButton != null)
            playButton.onClick.AddListener(Play);

        if (stopButton != null)
            stopButton.onClick.AddListener(Stop);

        ShowFrameAt(playbackTime);
        UpdateText();
    }

    private void Update()
    {
        if (Input.GetKeyDown(playStopKey) && !IsTypingText())
            TogglePlay();

        if (isPlaying)
        {
            playbackTime += Time.deltaTime * playbackSpeed;

            if (playbackTime > duration)
            {
                if (loop)
                {
                    playbackTime = 0f;
                }
                else
                {
                    playbackTime = duration;
                    isPlaying = false;
                }
            }
        }

        if (groupedOnlyToggle != null && groupedOnlyToggle.isOn != groupedOnly)
            groupedOnlyToggle.SetIsOnWithoutNotify(groupedOnly);

        ShowFrameAt(playbackTime);
        UpdateTimelineUI();
        UpdateText();
    }

    private void LateUpdate()
    {
        // グループ名を入力中は選択を外さない(外すと入力できない)
        if (!IsTypingText())
            HomeExperimentPaths.ClearUISelection();
    }

    private void OnDestroy()
    {
        // グループを変えたのに保存していなければ、シーン4/5が古いグループの骨格を読まないように保存する
        if (groupedDirty && data != null && personGroups.Count > 0)
        {
            Debug.Log("[HomeSkeletonFilter] グループを変えた人物ごとの骨格が未保存なので保存します。");
            SaveGrouped();
        }
    }

    // ------------------------------------------------------------
    // Controls
    // ------------------------------------------------------------
    // 今の再生位置から再生する(最後まで再生し終わっていたら最初から)
    public void Play()
    {
        if (data == null || data.frames.Count == 0)
        {
            Debug.LogWarning("[HomeSkeletonFilter] 再生できる骨格データがありません。");
            return;
        }

        if (playbackTime >= duration - 0.001f)
            playbackTime = 0f;

        isPlaying = true;
    }

    // 一時停止する
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
        ShowFrameAt(playbackTime);
    }

    // ------------------------------------------------------------
    // 1. 骨格を用意する
    // ------------------------------------------------------------
    [ContextMenu("Rebuild Filtered")]
    public void RebuildFiltered()
    {
        forceRebuild = true;
        LoadOrBuild();
        forceRebuild = false;

        if (timelineSlider != null)
            timelineSlider.maxValue = Mathf.Max(duration, 0.001f);

        LoadPersonGroups();
        CheckGroupedFile();
        OnPersonGroupsChanged(false);
    }

    private void LoadOrBuild()
    {
        Stop();
        data = null;

        if (!HomeExperimentPaths.IsValidFolderName(env.ExperimentName, out string error) ||
            !HomeExperimentPaths.IsValidFolderName(subjectName, out error))
        {
            Debug.LogError($"[HomeSkeletonFilter] 実験の名前/実験対象者を正しく入力してください。{error}");
        }
        else if (source == Source.Image)
        {
            data = LoadFilteredFile(ModeName);

            if (data == null)
                Debug.LogError("[HomeSkeletonFilter] filtered_Image.json がありません。シーン1で Record Raw Mkv にチェックを入れて記録してから " +
                               "Tools/GazePipeline/run_pipeline.py で作ってください。");
        }
        else
        {
            HomeFilteredSkeletonList saved = forceRebuild ? null : LoadFilteredFile(ModeName);

            if (saved != null && MatchesSettings(saved))
            {
                data = saved;
                Debug.Log($"[HomeSkeletonFilter] 保存済みの filtered_{ModeName}.json を読み込みました(設定が同じなので統合し直しません)。");
            }
            else
            {
                if (saved != null)
                    Debug.Log($"[HomeSkeletonFilter] 保存済みの filtered_{ModeName}.json と設定・作り方が違うので統合し直します(人物IDが変わります)。");

                List<HomeSkeletonTrack> sources = HomeSkeletonIO.LoadSubject(env.ExperimentName, subjectName, "HomeSkeletonFilter");

                if (sources.Count > 0)
                {
                    data = Filter(sources);
                    SaveJson(HomeExperimentPaths.GetFilteredSkeletonPath(env.ExperimentName, subjectName, ModeName), data, "filtered");
                    LogSummary(data, sources);
                }
            }
        }

        personFrameCounts.Clear();
        personFirstTimes.Clear();

        if (data == null)
        {
            frameTimes = new float[0];
            duration = 0f;
            return;
        }

        frameTimes = new float[data.frames.Count];

        for (int i = 0; i < frameTimes.Length; i++)
        {
            HomeFilteredFrame frame = data.frames[i];
            frameTimes[i] = frame.timeSec;

            foreach (HomeFilteredPerson person in frame.persons)
            {
                personFrameCounts.TryGetValue(person.trackId, out int count);
                personFrameCounts[person.trackId] = count + 1;

                if (!personFirstTimes.ContainsKey(person.trackId))
                    personFirstTimes[person.trackId] = frame.timeSec;
            }
        }

        duration = frameTimes.Length > 0 ? frameTimes[frameTimes.Length - 1] : 0f;
        playbackTime = Mathf.Min(playbackTime, duration);
        shownIndex = -2;
    }

    private HomeFilteredSkeletonList LoadFilteredFile(string modeName)
    {
        string path = HomeExperimentPaths.GetFilteredSkeletonPath(env.ExperimentName, subjectName, modeName);

        if (!File.Exists(path))
            return null;

        try
        {
            HomeFilteredSkeletonList loaded = JsonUtility.FromJson<HomeFilteredSkeletonList>(File.ReadAllText(path));
            return loaded != null && loaded.frames != null ? loaded : null;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[HomeSkeletonFilter] 読み込めませんでした: {path}\n{e.Message}");
            return null;
        }
    }

    // 保存済みの filtered が今の設定・今の作り方で作られたか
    private bool MatchesSettings(HomeFilteredSkeletonList saved)
    {
        if (saved.fusionVersion != FusionVersion ||
            saved.confidenceMode != confidenceMode.ToString() ||
            !Mathf.Approximately(saved.sameBodyDistance, sameBodyDistance) ||
            !Mathf.Approximately(saved.trackMatchDistance, trackMatchDistance) ||
            !Mathf.Approximately(saved.trackTimeout, trackTimeout) ||
            !Mathf.Approximately(saved.sampleInterval, sampleInterval) ||
            !Mathf.Approximately(saved.maxFrameAge, maxFrameAge) ||
            saved.headJoints == null || saved.headJoints.Count != headJoints.Length)
            return false;

        for (int i = 0; i < headJoints.Length; i++)
        {
            if (saved.headJoints[i] != headJoints[i].ToString())
                return false;
        }

        return true;
    }

    private HomeFilteredSkeletonList Filter(List<HomeSkeletonTrack> sources)
    {
        HomeFilteredSkeletonList filtered = new HomeFilteredSkeletonList
        {
            experimentName = env.ExperimentName,
            subjectName = subjectName,
            createdAt = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            fusionVersion = FusionVersion,
            confidenceMode = confidenceMode.ToString(),
            sameBodyDistance = sameBodyDistance,
            trackMatchDistance = trackMatchDistance,
            trackTimeout = trackTimeout,
            sampleInterval = sampleInterval,
            maxFrameAge = maxFrameAge
        };

        HashSet<string> headJointNames = new HashSet<string>();

        foreach (JointId jointId in headJoints)
        {
            headJointNames.Add(jointId.ToString());
            filtered.headJoints.Add(jointId.ToString());
        }

        float endTime = 0f;

        foreach (HomeSkeletonTrack source in sources)
        {
            filtered.sourceKinects.Add(source.kinectId);
            endTime = Mathf.Max(endTime, source.Duration);
        }

        float interval = Mathf.Max(sampleInterval, 0.001f);
        int sampleCount = Mathf.FloorToInt(endTime / interval) + 1;

        List<PersonTrack> tracks = new List<PersonTrack>();
        int nextTrackId = 0;

        for (int sample = 0; sample < sampleCount; sample++)
        {
            float time = sample * interval;
            List<Candidate> candidates = CollectCandidates(sources, time, headJointNames);

            // 信頼度の高い順に処理し、近い場所に既に採用済みのBodyがあればそのBodyは捨てる
            candidates.Sort((a, b) => b.confidence.CompareTo(a.confidence));

            List<Candidate> selected = new List<Candidate>();

            foreach (Candidate candidate in candidates)
            {
                bool duplicate = false;

                foreach (Candidate existing in selected)
                {
                    if (Vector3.Distance(existing.pelvisPosition, candidate.pelvisPosition) <= sameBodyDistance)
                    {
                        duplicate = true;
                        break;
                    }
                }

                if (!duplicate)
                    selected.Add(candidate);
            }

            HomeFilteredFrame frame = new HomeFilteredFrame { timeSec = time };
            PersonTrack[] assigned = AssignTracks(tracks, selected, time, ref nextTrackId);

            for (int i = 0; i < selected.Count; i++)
            {
                Candidate candidate = selected[i];
                PersonTrack track = assigned[i];

                frame.persons.Add(new HomeFilteredPerson
                {
                    trackId = track.id,
                    label = $"person_{track.id}",
                    kinectId = candidate.kinectId,
                    bodyId = candidate.bodyId,
                    confidence = candidate.confidence,
                    allJointConfidence = candidate.allJointConfidence,
                    headJointConfidence = candidate.headJointConfidence,
                    joints = candidate.joints
                });
            }

            tracks.RemoveAll(t => time - t.lastSeenTime > trackTimeout);
            filtered.frames.Add(frame);
        }

        return filtered;
    }

    private List<Candidate> CollectCandidates(List<HomeSkeletonTrack> sources, float time, HashSet<string> headJointNames)
    {
        List<Candidate> candidates = new List<Candidate>();

        foreach (HomeSkeletonTrack source in sources)
        {
            int index = source.FindFrameIndex(time);

            if (index < 0 || time - source.times[index] > maxFrameAge)
                continue;

            List<HomeSkeletonBody> bodies = source.frames[index].bodies;

            if (bodies == null)
                continue;

            foreach (HomeSkeletonBody body in bodies)
            {
                if (!HomeSkeletonIO.TryGetJointPosition(body.joints, JointId.Pelvis, out Vector3 pelvisPosition))
                    continue;

                float allJointConfidence = HomeSkeletonIO.GetAverageConfidence(body.joints);
                float headJointConfidence = HomeSkeletonIO.GetAverageConfidence(body.joints, headJointNames);

                candidates.Add(new Candidate
                {
                    kinectId = source.kinectId,
                    bodyId = body.bodyId,
                    pelvisPosition = pelvisPosition,
                    confidence = confidenceMode == ConfidenceMode.HeadJoints ? headJointConfidence : allJointConfidence,
                    allJointConfidence = allJointConfidence,
                    headJointConfidence = headJointConfidence,
                    joints = body.joints
                });
            }
        }

        return candidates;
    }

    // 採用した骨格(selected)に人物トラックを割り当てる(戻り値は selected と同じ並び)。
    // 既存のトラックと骨格の組を Pelvis の距離が近い順に見て、trackMatchDistance 以内なら引き継ぐ。
    // 1つのトラックは1時刻に1つの骨格にしか引き継がない(同じ時刻に同じ人物IDが2つ付かない)。
    // 引き継げなかった骨格は新しいトラック(新しい人物ID)にする。
    private PersonTrack[] AssignTracks(List<PersonTrack> tracks, List<Candidate> selected, float time, ref int nextTrackId)
    {
        List<(float distance, int track, int candidate)> pairs = new List<(float, int, int)>();

        for (int t = 0; t < tracks.Count; t++)
        {
            for (int c = 0; c < selected.Count; c++)
            {
                float distance = Vector3.Distance(tracks[t].position, selected[c].pelvisPosition);

                if (distance <= trackMatchDistance)
                    pairs.Add((distance, t, c));
            }
        }

        pairs.Sort((a, b) => a.distance.CompareTo(b.distance));

        PersonTrack[] assigned = new PersonTrack[selected.Count];
        bool[] usedTrack = new bool[tracks.Count];

        foreach ((float _, int t, int c) in pairs)
        {
            if (usedTrack[t] || assigned[c] != null)
                continue;

            usedTrack[t] = true;
            assigned[c] = tracks[t];
        }

        for (int c = 0; c < selected.Count; c++)
        {
            if (assigned[c] == null)
            {
                assigned[c] = new PersonTrack { id = nextTrackId++ };
                tracks.Add(assigned[c]);
            }

            assigned[c].position = selected[c].pelvisPosition;
            assigned[c].lastSeenTime = time;
        }

        return assigned;
    }

    private static void SaveJson(string path, HomeFilteredSkeletonList list, string kind)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(list));
            HomeExperimentPaths.RefreshAssetDatabase();
            Debug.Log($"[HomeSkeletonFilter] {kind} を保存しました ({list.frames.Count} frames): {path}");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[HomeSkeletonFilter] 保存に失敗しました: {path}\n{e}");
        }
    }

    private static void LogSummary(HomeFilteredSkeletonList data, List<HomeSkeletonTrack> sources)
    {
        Dictionary<int, int> framesPerTrack = new Dictionary<int, int>();
        Dictionary<string, int> personsPerKinect = new Dictionary<string, int>();
        int rawBodies = 0;
        int keptBodies = 0;

        foreach (HomeSkeletonTrack source in sources)
        {
            foreach (HomeSkeletonFrame frame in source.frames)
                rawBodies += frame.bodies != null ? frame.bodies.Count : 0;
        }

        foreach (HomeFilteredFrame frame in data.frames)
        {
            foreach (HomeFilteredPerson person in frame.persons)
            {
                keptBodies++;
                framesPerTrack.TryGetValue(person.trackId, out int trackCount);
                framesPerTrack[person.trackId] = trackCount + 1;
                personsPerKinect.TryGetValue(person.kinectId, out int kinectCount);
                personsPerKinect[person.kinectId] = kinectCount + 1;
            }
        }

        StringBuilder text = new StringBuilder();
        text.Append($"[HomeSkeletonFilter] {data.confidenceMode}: {data.frames.Count} samples ({data.sampleInterval:F3}s間隔), ");
        text.Append($"元の骨格 {rawBodies} 件 → 採用 {keptBodies} 件, 人物トラック {framesPerTrack.Count} 個\n");

        foreach (KeyValuePair<int, int> pair in framesPerTrack)
            text.Append($"  person_{pair.Key}: {pair.Value} samples\n");

        foreach (KeyValuePair<string, int> pair in personsPerKinect)
            text.Append($"  Kinect{pair.Key} から採用: {pair.Value} 件\n");

        Debug.Log(text.ToString());
    }

    // ------------------------------------------------------------
    // 2. グループ分け
    // ------------------------------------------------------------
    private void LoadPersonGroups()
    {
        personGroups.Clear();

        if (data == null)
        {
            RebuildGroupIndex();
            return;
        }

        string path = HomeExperimentPaths.GetPersonGroupsPath(env.ExperimentName, subjectName, PersonIdSourceName);

        if (File.Exists(path))
        {
            try
            {
                HomePersonGroupList list = JsonUtility.FromJson<HomePersonGroupList>(File.ReadAllText(path));

                if (list != null && list.groups != null)
                {
                    foreach (HomePersonGroup group in list.groups)
                    {
                        if (group != null && group.personIds != null && group.personIds.Count > 0)
                            personGroups.Add(group);
                    }

                    // 骨格を作り直すと人物IDが変わるので、グループを作ったときの骨格と違えば知らせる
                    if (personGroups.Count > 0 && list.filteredCreatedAt != data.createdAt)
                    {
                        Debug.LogWarning($"[HomeSkeletonFilter] グループを作ったときと骨格データが違います" +
                                         $"(グループ: {(string.IsNullOrEmpty(list.filteredCreatedAt) ? "記録なし" : list.filteredCreatedAt)} / " +
                                         $"骨格: {data.createdAt})。人物IDが変わっている可能性があるので、グループを確認してください。");
                    }
                }

                Debug.Log($"[HomeSkeletonFilter] 人物のグループを {personGroups.Count} 個読み込みました: {path}");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[HomeSkeletonFilter] 人物のグループを読み込めませんでした: {path}\n{e.Message}");
            }
        }

        RebuildGroupIndex();
    }

    private void SavePersonGroups()
    {
        string path = HomeExperimentPaths.GetPersonGroupsPath(env.ExperimentName, subjectName, PersonIdSourceName);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            HomePersonGroupList list = new HomePersonGroupList
            {
                skeletonSource = PersonIdSourceName,
                filteredCreatedAt = data != null ? data.createdAt : "",
                groups = personGroups
            };
            File.WriteAllText(path, JsonUtility.ToJson(list, true));
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[HomeSkeletonFilter] 人物のグループを保存できませんでした: {path}\n{e}");
        }
    }

    private void RebuildGroupIndex()
    {
        groupOfPerson.Clear();

        for (int g = 0; g < personGroups.Count; g++)
        {
            foreach (int id in personGroups[g].personIds)
                groupOfPerson[id] = g;
        }
    }

    // ids を1つのグループにする。ほかのグループに入っていたIDは移す。
    // 同じ名前のグループがあればそこに足す。name が空なら group_<番号>
    public void CreatePersonGroup(string name, IEnumerable<int> ids)
    {
        List<int> members = new List<int>();

        foreach (int id in ids)
        {
            if (id >= 0 && !members.Contains(id))
                members.Add(id);
        }

        if (members.Count == 0)
        {
            Debug.LogWarning("[HomeSkeletonFilter] グループにする人物IDを選んでください。");
            return;
        }

        foreach (HomePersonGroup group in personGroups)
            group.personIds.RemoveAll(members.Contains);

        personGroups.RemoveAll(group => group.personIds.Count == 0);

        name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();

        if (name == null)
        {
            int number = personGroups.Count + 1;

            while (personGroups.Exists(group => group.name == $"group_{number}"))
                number++;

            name = $"group_{number}";
        }

        HomePersonGroup target = personGroups.Find(group => group.name == name);

        if (target == null)
        {
            target = new HomePersonGroup { name = name };
            personGroups.Add(target);
        }

        target.personIds.AddRange(members);
        target.personIds.Sort();
        OnPersonGroupsChanged(true);
    }

    public void RemovePersonGroup(string name)
    {
        if (personGroups.RemoveAll(group => group.name == name) > 0)
            OnPersonGroupsChanged(true);
    }

    // グループを変えたら保存し、一覧と骨格の色を作り直す(人物ごとの骨格は Save grouped で保存する)
    private void OnPersonGroupsChanged(bool save)
    {
        RebuildGroupIndex();

        if (save)
        {
            SavePersonGroups();
            groupedDirty = true;
        }

        RebuildSkeletonRows();
        RebuildGroupPanel();

        foreach (KeyValuePair<int, HomeSkeletonBodyVisual> pair in visuals)
            pair.Value.SetColor(GetPersonColor(pair.Key));

        shownIndex = -2;
        ShowFrameAt(playbackTime);
        UpdateText();
    }

    private HomePersonGroup FindGroup(int personId) =>
        groupOfPerson.TryGetValue(personId, out int g) ? personGroups[g] : null;

    // グループの色(グループの番号順)。グループに入っていない人物IDは灰色
    private Color GetPersonColor(int personId)
    {
        if (!groupOfPerson.TryGetValue(personId, out int g))
            return UngroupedColor;

        return trackColors.Length > 0 ? trackColors[g % trackColors.Length] : Color.white;
    }

    // ------------------------------------------------------------
    // 3. 人物ごとの骨格(シーン4/5が読む grouped_<Mode>.json)
    // ------------------------------------------------------------
    // グループごとに、頭の関節の信頼度(headJointConfidence)が最も高い骨格を1つ選ぶ(同じなら先に書かれたもの)。
    // 戻り値の番号 = グループの番号(映っていないグループは null)。表示の Grouped only と保存で同じものを使う
    private HomeFilteredPerson[] SelectGroupPersons(List<HomeFilteredPerson> persons)
    {
        HomeFilteredPerson[] best = new HomeFilteredPerson[personGroups.Count];

        foreach (HomeFilteredPerson person in persons)
        {
            if (!groupOfPerson.TryGetValue(person.trackId, out int g))
                continue;

            if (best[g] == null || person.headJointConfidence > best[g].headJointConfidence)
                best[g] = person;
        }

        return best;
    }

    [ContextMenu("Save Grouped")]
    public void SaveGrouped()
    {
        if (data == null)
            return;

        if (personGroups.Count == 0)
        {
            Debug.LogWarning("[HomeSkeletonFilter] グループが無いので人物ごとの骨格を保存できません。右上の Groups で人物IDをまとめてください。");
            return;
        }

        HomeFilteredSkeletonList grouped = new HomeFilteredSkeletonList
        {
            experimentName = data.experimentName,
            subjectName = data.subjectName,
            createdAt = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            sourceKinects = data.sourceKinects,
            fusionVersion = data.fusionVersion,
            confidenceMode = data.confidenceMode,
            headJoints = data.headJoints,
            sameBodyDistance = data.sameBodyDistance,
            trackMatchDistance = data.trackMatchDistance,
            trackTimeout = data.trackTimeout,
            sampleInterval = data.sampleInterval,
            maxFrameAge = data.maxFrameAge,
            grouped = true,
            sourceCreatedAt = data.createdAt,
            groups = personGroups
        };

        foreach (HomeFilteredFrame frame in data.frames)
        {
            HomeFilteredFrame groupedFrame = new HomeFilteredFrame { timeSec = frame.timeSec };
            HomeFilteredPerson[] chosen = SelectGroupPersons(frame.persons);

            for (int g = 0; g < chosen.Length; g++)
            {
                if (chosen[g] != null)
                    groupedFrame.persons.Add(CopyAsGroup(chosen[g], g));
            }

            grouped.frames.Add(groupedFrame);
        }

        SaveJson(HomeExperimentPaths.GetGroupedSkeletonPath(env.ExperimentName, subjectName, ModeName), grouped, "人物ごとの骨格");
        groupedDirty = false;
    }

    private HomeFilteredPerson CopyAsGroup(HomeFilteredPerson person, int group)
    {
        return new HomeFilteredPerson
        {
            trackId = group,
            label = personGroups[group].name,
            sourceTrackId = person.trackId,
            kinectId = person.kinectId,
            bodyId = person.bodyId,
            confidence = person.confidence,
            allJointConfidence = person.allJointConfidence,
            headJointConfidence = person.headJointConfidence,
            joints = person.joints,
            headPosition = person.headPosition,
            hasHeadDirection = person.hasHeadDirection,
            headDirection = person.headDirection,
            hasGazeDirection = person.hasGazeDirection,
            gazeDirection = person.gazeDirection,
            gazeConfidence = person.gazeConfidence
        };
    }

    // grouped_<Mode>.json が今の骨格・グループより古ければ未保存として扱う(終了時に保存する)
    private void CheckGroupedFile()
    {
        groupedDirty = false;

        if (data == null || personGroups.Count == 0)
            return;

        string groupedPath = HomeExperimentPaths.GetGroupedSkeletonPath(env.ExperimentName, subjectName, ModeName);
        string filteredPath = HomeExperimentPaths.GetFilteredSkeletonPath(env.ExperimentName, subjectName, ModeName);
        string groupsPath = HomeExperimentPaths.GetPersonGroupsPath(env.ExperimentName, subjectName, PersonIdSourceName);

        if (!File.Exists(groupedPath))
        {
            groupedDirty = true;
            return;
        }

        System.DateTime groupedTime = File.GetLastWriteTime(groupedPath);
        groupedDirty = (File.Exists(filteredPath) && File.GetLastWriteTime(filteredPath) > groupedTime) ||
                       (File.Exists(groupsPath) && File.GetLastWriteTime(groupsPath) > groupedTime);
    }

    // ------------------------------------------------------------
    // Display
    // ------------------------------------------------------------
    private void ShowFrameAt(float time)
    {
        int index = FindFrameIndex(time);

        if (index == shownIndex)
            return;

        shownIndex = index;

        HashSet<int> shown = new HashSet<int>();

        if (data != null && index >= 0)
        {
            List<HomeFilteredPerson> persons = data.frames[index].persons;
            IEnumerable<HomeFilteredPerson> visible = groupedOnly ? SelectGroupPersons(persons) : (IEnumerable<HomeFilteredPerson>)persons;

            foreach (HomeFilteredPerson person in visible)
            {
                if (person == null || shown.Contains(person.trackId))
                    continue;

                // Skeleton の一覧で人物を選んでいれば、その人だけ表示する
                if (skeletonIds != null && !skeletonIds.Contains(person.trackId))
                    continue;

                if (!visuals.TryGetValue(person.trackId, out HomeSkeletonBodyVisual visual))
                {
                    visual = new HomeSkeletonBodyVisual(visualRoot, $"person_{person.trackId}", GetPersonColor(person.trackId), visualStyle);
                    visuals[person.trackId] = visual;
                }

                visual.Apply(person.joints);
                shown.Add(person.trackId);
            }
        }

        foreach (KeyValuePair<int, HomeSkeletonBodyVisual> pair in visuals)
        {
            if (!shown.Contains(pair.Key))
                pair.Value.SetVisible(false);
        }
    }

    private int FindFrameIndex(float time)
    {
        int low = 0;
        int high = frameTimes.Length - 1;
        int found = -1;

        while (low <= high)
        {
            int mid = (low + high) / 2;

            if (frameTimes[mid] <= time)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return found;
    }

    private void UpdateText()
    {
        if (frameText == null)
            return;

        StringBuilder text = new StringBuilder();
        text.Append($"{subjectName} (filtered_{ModeName})  Time : {playbackTime:F1} / {duration:F1} s  Skeleton : {skeletonLabel}");
        text.Append(groupedOnly ? "  [Grouped only]" : "");

        if (data != null && shownIndex >= 0 && shownIndex < data.frames.Count)
        {
            List<HomeFilteredPerson> persons = data.frames[shownIndex].persons;
            HomeFilteredPerson[] chosen = SelectGroupPersons(persons);

            foreach (HomeFilteredPerson person in persons)
            {
                HomePersonGroup group = FindGroup(person.trackId);
                bool used = group != null && System.Array.IndexOf(chosen, person) >= 0;
                string state = group == null ? "(not grouped)" : used ? $"[{group.name}] used" : $"[{group.name}] not used";
                text.Append($"\nperson_{person.trackId} {state} : Kinect{person.kinectId} conf={person.confidence:F2} head={person.headJointConfidence:F2}");
            }
        }

        frameText.text = text.ToString();
    }

    // ------------------------------------------------------------
    // UI
    // ------------------------------------------------------------
    private void SetupUI()
    {
        Canvas canvas = frameText != null ? frameText.canvas : FindObjectOfType<Canvas>();

        if (canvas == null)
        {
            Debug.LogWarning("[HomeSkeletonFilter] Canvas が無いため、再生バーと一覧を作れません。");
            return;
        }

#if UNITY_EDITOR
        // 普通のUIと同じ見た目にするため、エディタ組み込みのスプライトを使う
        uiSprite = UnityEditor.AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
        checkmarkSprite = UnityEditor.AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Checkmark.psd");
#endif

        SetupTimeline(canvas);
        SetupSkeletonSelector(canvas);
        SetupGroupPanel(canvas);
    }

    //   [>]  12.3 / 60.0 s  [=========o-----------------]   … 再生バー。ドラッグで再生位置を動かす
    //   [x] Grouped only   [Save grouped]  saved / NOT SAVED
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
        PlaceLeft(timeText.rectTransform, 60f, 160f);

        timelineSlider = CreateSlider(row1, new Color(0.25f, 0.45f, 0.95f));
        RectTransform timelineRect = (RectTransform)timelineSlider.transform;
        timelineRect.anchorMin = new Vector2(0f, 0f);
        timelineRect.anchorMax = new Vector2(1f, 1f);
        timelineRect.offsetMin = new Vector2(230f, 10f);
        timelineRect.offsetMax = new Vector2(-10f, -10f);
        timelineSlider.minValue = 0f;
        timelineSlider.maxValue = Mathf.Max(duration, 0.001f);
        timelineSlider.onValueChanged.AddListener(Seek);

        RectTransform row2 = CreateRow(root, 48f, 44f);

        groupedOnlyToggle = CreateToggleRow(row2, "Grouped only", 0f, 44f);
        RectTransform toggleRect = (RectTransform)groupedOnlyToggle.transform;
        toggleRect.anchorMin = toggleRect.anchorMax = new Vector2(0f, 0.5f);
        toggleRect.pivot = new Vector2(0f, 0.5f);
        toggleRect.anchoredPosition = new Vector2(0f, 0f);
        toggleRect.sizeDelta = new Vector2(200f, 36f);
        groupedOnlyToggle.SetIsOnWithoutNotify(groupedOnly);
        groupedOnlyToggle.onValueChanged.AddListener(isOn =>
        {
            groupedOnly = isOn;
            shownIndex = -2;
            ShowFrameAt(playbackTime);
        });

        Button save = CreateButton(row2, "Save grouped", 210f, 160f, out _, new Color(0.2f, 0.45f, 0.9f), Color.white);
        save.onClick.AddListener(SaveGrouped);

        saveStatusText = CreateText("SaveStatus", row2, TextAlignmentOptions.MidlineLeft);
        PlaceLeft(saveStatusText.rectTransform, 380f, 700f);

        UpdateTimelineUI();
    }

    private void UpdateTimelineUI()
    {
        if (timelineSlider == null)
            return;

        timelineSlider.SetValueWithoutNotify(playbackTime);
        playPauseText.text = isPlaying ? "||" : ">";
        timeText.text = $"{playbackTime:F1} / {duration:F1} s";

        saveStatusText.text =
            personGroups.Count == 0 ? $"grouped_{ModeName}.json : make groups first" :
            groupedDirty ? $"grouped_{ModeName}.json : NOT SAVED (saved on exit)" :
            $"grouped_{ModeName}.json : saved";
        saveStatusText.color = groupedDirty ? new Color(0.8f, 0.2f, 0.2f) : new Color(0.2f, 0.2f, 0.2f);
    }

    // 一覧に出す単位: グループ(複数の人物ID)と、グループに入っていない人物
    private class DisplayUnit
    {
        public string name;
        public bool isGroup;
        public int[] ids;
    }

    private List<DisplayUnit> GetDisplayUnits()
    {
        List<DisplayUnit> units = new List<DisplayUnit>();

        foreach (HomePersonGroup group in personGroups)
            units.Add(new DisplayUnit { name = group.name, isGroup = true, ids = group.personIds.ToArray() });

        foreach (int id in personFrameCounts.Keys)
        {
            if (!groupOfPerson.ContainsKey(id))
                units.Add(new DisplayUnit { name = $"person_{id}", ids = new[] { id } });
        }

        return units;
    }

    private string GetUnitLabel(DisplayUnit unit)
    {
        int frames = 0;
        float firstTime = float.MaxValue;

        foreach (int id in unit.ids)
        {
            if (personFrameCounts.TryGetValue(id, out int count))
                frames += count;

            if (personFirstTimes.TryGetValue(id, out float time))
                firstTime = Mathf.Min(firstTime, time);
        }

        string members = unit.isGroup ? $" [{string.Join(",", unit.ids)}]" : "";
        string first = firstTime == float.MaxValue ? "-" : $"{firstTime:F1} s -";
        return $"{unit.name}{members} ({first}, {frames} frames)";
    }

    // 表示する人物(グループなら全員)を選ぶ(null なら全員)。選ぶと、その人が最初に出てくる時刻へ移動する
    private void SetSkeletonUnit(DisplayUnit unit)
    {
        skeletonIds = unit == null ? null : new HashSet<int>(unit.ids);
        skeletonLabel = unit == null ? "All" : unit.name;

        if (unit != null)
        {
            float firstTime = float.MaxValue;

            foreach (int id in unit.ids)
            {
                if (personFirstTimes.TryGetValue(id, out float time))
                    firstTime = Mathf.Min(firstTime, time);
            }

            if (firstTime != float.MaxValue)
                playbackTime = Mathf.Clamp(firstTime, 0f, duration);
        }

        shownIndex = -2;
        ShowFrameAt(playbackTime);
        RefreshSkeletonToggles();
        UpdateTimelineUI();
        UpdateText();
    }

    private bool IsSkeletonUnit(DisplayUnit unit)
    {
        if (skeletonIds == null || skeletonIds.Count != unit.ids.Length)
            return false;

        foreach (int id in unit.ids)
        {
            if (!skeletonIds.Contains(id))
                return false;
        }

        return true;
    }

    private void RefreshSkeletonToggles()
    {
        if (skeletonAllToggle != null)
            skeletonAllToggle.SetIsOnWithoutNotify(skeletonIds == null);

        foreach (KeyValuePair<DisplayUnit, Toggle> pair in skeletonUnitToggles)
            pair.Value.SetIsOnWithoutNotify(IsSkeletonUnit(pair.Key));

        if (skeletonHeaderText != null)
            skeletonHeaderText.text = $"Skeleton : {skeletonLabel}";
    }

    //   [Skeleton : All  v]
    //   (o) All
    //   ( ) mother [0,3,7] (0.1 s -, 100 frames)
    //   ( ) person_5 (12.3 s -, 23 frames)
    private void SetupSkeletonSelector(Canvas canvas)
    {
        RectTransform root = CreateDropdown(canvas, "SkeletonSelector", 0, out skeletonHeaderText, out skeletonListRect, out skeletonListContent);
        root.GetComponent<Button>().onClick.AddListener(() => skeletonListRect.gameObject.SetActive(!skeletonListRect.gameObject.activeSelf));
        skeletonListRect.gameObject.SetActive(false);

        RebuildSkeletonRows();
    }

    private void RebuildSkeletonRows()
    {
        if (skeletonListContent == null)
            return;

        ClearChildren(skeletonListContent);
        skeletonUnitToggles.Clear();

        // グループを変えると選んでいた人物の単位が変わるので、全員の表示に戻す
        skeletonIds = null;
        skeletonLabel = "All";

        List<DisplayUnit> units = GetDisplayUnits();

        skeletonAllToggle = CreateToggleRow(skeletonListContent, "All", 0f, DropdownRowHeight);
        skeletonAllToggle.onValueChanged.AddListener(isOn => OnSkeletonToggle(null, isOn));

        for (int i = 0; i < units.Count; i++)
        {
            DisplayUnit unit = units[i];
            Toggle toggle = CreateToggleRow(skeletonListContent, GetUnitLabel(unit), (i + 1) * DropdownRowHeight, DropdownRowHeight);
            toggle.onValueChanged.AddListener(isOn => OnSkeletonToggle(unit, isOn));
            skeletonUnitToggles.Add(new KeyValuePair<DisplayUnit, Toggle>(unit, toggle));
        }

        SetDropdownRows(skeletonListRect, skeletonListContent, units.Count + 1);
        RefreshSkeletonToggles();
    }

    private void OnSkeletonToggle(DisplayUnit unit, bool isOn)
    {
        // 1つだけ選ぶ。選び直すと一覧を閉じる(選んでいるものを外そうとしたときは選んだままにする)
        if (isOn)
        {
            SetSkeletonUnit(unit);
            skeletonListRect.gameObject.SetActive(false);
        }
        else
        {
            RefreshSkeletonToggles();
        }
    }

    //   [Groups (1)  - click to edit]            … 押すと下を開閉する
    //   ┌ 固定(スクロールしない) ───────────────┐
    //   │ 1. Check IDs below  2. Name  3. Make    │
    //   │ [group name (optional)_____] [Make group]│ … 青いボタン
    //   └──────────────────────────────┘
    //   Groups:
    //     mother: 0, 3, 7                [Ungroup]  … 赤いボタンで解除
    //   Person IDs (check to group):
    //   [x] person_0 (0.1 s -, 100 frames)  [mother]
    //   [ ] person_5 (12.3 s -, 23 frames)
    private void SetupGroupPanel(Canvas canvas)
    {
        RectTransform root = CreateDropdown(canvas, "GroupPanel", 1, out groupHeaderText, out groupListRect, out groupListContent);

        // 見出しのすぐ下に、スクロールしない入力欄とボタンを置き、一覧はその下にずらす
        const float toolsHeight = DropdownRowHeight * 2f + 8f;
        RectTransform tools = CreateRect("Tools", root);
        tools.anchorMin = new Vector2(0f, 0f);
        tools.anchorMax = new Vector2(1f, 0f);
        tools.pivot = new Vector2(0.5f, 1f);
        tools.anchoredPosition = new Vector2(0f, -2f);
        tools.sizeDelta = new Vector2(0f, toolsHeight);
        AddImage(tools.gameObject, uiSprite, new Color(0.85f, 0.92f, 1f, 0.98f));
        groupListRect.anchoredPosition = new Vector2(0f, -2f - toolsHeight);

        RectTransform hintRow = CreateLabelRow(tools, "1. Check IDs below  2. Name  3. Make group", 0, 0f);
        hintRow.GetComponentInChildren<TMP_Text>().fontSize = 16f;

        RectTransform inputRow = CreateLabelRow(tools, "", 1, 0f);
        groupNameInput = CreateInputField(inputRow, "group name (optional)", 8f, DropdownWidth - 138f);
        Button make = CreateButton(inputRow, "Make group", DropdownWidth - 125f, 118f, out _,
                                   new Color(0.2f, 0.45f, 0.9f), Color.white);
        make.onClick.AddListener(OnMakeGroup);

        GameObject toolsObject = tools.gameObject;
        root.GetComponent<Button>().onClick.AddListener(() =>
        {
            bool open = !groupListRect.gameObject.activeSelf;
            groupListRect.gameObject.SetActive(open);
            toolsObject.SetActive(open);
        });
        groupListRect.gameObject.SetActive(false);
        toolsObject.SetActive(false);

        RebuildGroupPanel();
    }

    private void RebuildGroupPanel()
    {
        if (groupListContent == null)
            return;

        ClearChildren(groupListContent);
        int row = 0;

        // 今あるグループ(右の赤いボタンで解除)
        TMP_Text groupsTitle = CreateLabelRow(groupListContent, personGroups.Count > 0 ? "Groups:" : "Groups: (none yet)", row++, 0f)
            .GetComponentInChildren<TMP_Text>();
        groupsTitle.fontStyle = FontStyles.Bold;

        foreach (HomePersonGroup group in personGroups)
        {
            RectTransform line = CreateLabelRow(groupListContent, $"  {group.name}: {string.Join(", ", group.personIds)}", row++, 100f);
            string name = group.name;
            Button ungroup = CreateButton(line, "Ungroup", DropdownWidth - 95f, 88f, out _,
                                          new Color(0.85f, 0.3f, 0.3f), Color.white);
            ungroup.onClick.AddListener(() => RemovePersonGroup(name));
        }

        TMP_Text idsTitle = CreateLabelRow(groupListContent, "Person IDs (check to group):", row++, 0f).GetComponentInChildren<TMP_Text>();
        idsTitle.fontStyle = FontStyles.Bold;

        // まとめる人物を選ぶ
        foreach (int id in personFrameCounts.Keys)
        {
            personFrameCounts.TryGetValue(id, out int frames);
            personFirstTimes.TryGetValue(id, out float firstTime);
            HomePersonGroup group = FindGroup(id);
            string label = $"person_{id} ({firstTime:F1} s -, {frames} frames){(group != null ? $"  [{group.name}]" : "")}";

            Toggle toggle = CreateToggleRow(groupListContent, label, row++ * DropdownRowHeight, DropdownRowHeight);
            toggle.SetIsOnWithoutNotify(groupCandidateIds.Contains(id));
            toggle.onValueChanged.AddListener(isOn =>
            {
                if (isOn)
                    groupCandidateIds.Add(id);
                else
                    groupCandidateIds.Remove(id);
            });
        }

        SetDropdownRows(groupListRect, groupListContent, row);
        groupHeaderText.text = $"Groups ({personGroups.Count})  - click to edit";
    }

    private void OnMakeGroup()
    {
        if (groupCandidateIds.Count == 0)
        {
            Debug.LogWarning("[HomeSkeletonFilter] まとめる人物IDに、下の一覧でチェックを入れてください。");
            return;
        }

        string groupName = groupNameInput != null ? groupNameInput.text : "";
        List<int> ids = new List<int>(groupCandidateIds);
        groupCandidateIds.Clear();

        if (groupNameInput != null)
            groupNameInput.text = "";

        CreatePersonGroup(groupName, ids);
    }

    // ------------------------------------------------------------
    // UI helpers (シーン4と同じ見た目)
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

    // 文字だけの行(rightMargin: 右に置くボタンの分だけ文字を短くする)
    private RectTransform CreateLabelRow(RectTransform parent, string label, int row, float rightMargin)
    {
        RectTransform line = CreateRect("Row", parent);
        line.anchorMin = new Vector2(0f, 1f);
        line.anchorMax = new Vector2(1f, 1f);
        line.pivot = new Vector2(0.5f, 1f);
        line.anchoredPosition = new Vector2(0f, -row * DropdownRowHeight);
        line.sizeDelta = new Vector2(0f, DropdownRowHeight);

        TMP_Text text = CreateText("Label", line, TextAlignmentOptions.MidlineLeft);
        SetStretch(text.rectTransform, new Vector2(10f, 0f), new Vector2(-10f - rightMargin, 0f));
        text.text = label;
        return line;
    }

    private TMP_InputField CreateInputField(RectTransform parent, string placeholderText, float x, float width)
    {
        RectTransform rect = CreateRect("Input", parent);
        PlaceLeft(rect, x, width, 3f);
        AddImage(rect.gameObject, uiSprite, Color.white);

        // 背景と見分けやすいように枠線を付ける
        Outline outline = rect.gameObject.AddComponent<Outline>();
        outline.effectColor = new Color(0.35f, 0.35f, 0.35f);
        outline.effectDistance = new Vector2(1.5f, -1.5f);

        RectTransform area = CreateRect("Text Area", rect);
        SetStretch(area, new Vector2(8f, 2f), new Vector2(-8f, -2f));
        area.gameObject.AddComponent<RectMask2D>();

        TMP_Text placeholder = CreateText("Placeholder", area, TextAlignmentOptions.MidlineLeft);
        SetStretch(placeholder.rectTransform, Vector2.zero, Vector2.zero);
        placeholder.text = placeholderText;
        placeholder.fontStyle = FontStyles.Italic;
        placeholder.color = new Color(0.5f, 0.5f, 0.5f);

        TMP_Text text = CreateText("Text", area, TextAlignmentOptions.MidlineLeft);
        SetStretch(text.rectTransform, Vector2.zero, Vector2.zero);
        // 入力中の文字は省略記号(…)にせず、はみ出した分は入力欄の中でスクロールさせる
        text.overflowMode = TextOverflowModes.Overflow;

        // TMP_InputField は有効になったとき(OnEnable)に textComponent を使ってカーソルなどを準備する。
        // 追加した直後に有効になると textComponent がまだ無く準備されずに、カーソルの描画でエラーになるので、
        // いったん無効にしてから設定し、最後に有効にする
        bool wasActive = rect.gameObject.activeSelf;
        rect.gameObject.SetActive(false);

        TMP_InputField input = rect.gameObject.AddComponent<TMP_InputField>();
        input.textViewport = area;
        input.textComponent = text;
        input.placeholder = placeholder;
        input.fontAsset = text.font;
        input.pointSize = text.fontSize;
        input.text = "";

        rect.gameObject.SetActive(wasActive);
        return input;
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

    // 文字を入力中か(入力中は Space で再生しない・選択を外さない)
    private static bool IsTypingText()
    {
        GameObject selected = UnityEngine.EventSystems.EventSystem.current != null
            ? UnityEngine.EventSystems.EventSystem.current.currentSelectedGameObject
            : null;

        return selected != null && selected.GetComponent<TMP_InputField>() != null;
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
    // Types
    // ------------------------------------------------------------
    private class Candidate
    {
        public string kinectId;
        public uint bodyId;
        public Vector3 pelvisPosition;
        public float confidence;
        public float allJointConfidence;
        public float headJointConfidence;
        public List<HomeSkeletonJoint> joints;
    }

    private class PersonTrack
    {
        public int id;
        public Vector3 position;
        public float lastSeenTime;
    }
}
