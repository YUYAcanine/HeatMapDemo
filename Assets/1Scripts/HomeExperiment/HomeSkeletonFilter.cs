using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Azure.Kinect.BodyTracking;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// シーン3(3HeadDirFilter)用。実験対象者のフォルダの *_skeleton.json(全キネクト分)を読み込み、
// SkeletonRealtimePublisher と同じロジックで骨格を統合(信頼度フィルタリング)して
//   Skeleton/<実験対象者>/Filtered/filtered_<ConfidenceMode>.json
// に保存し、結果を再生する。
//
// 統合のロジック(SkeletonRealtimePublisher.FuseAndPublish と同じ):
//   1. 各時刻で、全キネクトの最新フレームに写っている骨格を候補として集める
//   2. 候補を信頼度の高い順に並べる。信頼度は confidenceMode で選ぶ:
//        AllJoints  … 全関節の confidence の平均(Publisher と同じ)
//        HeadJoints … headJoints(頭部方向の推定に使う関節)だけの confidence の平均
//   3. 採用済みの骨格と Pelvis が sameBodyDistance 以内の候補は同一人物とみなして捨てる
//   4. 採用した骨格を、trackMatchDistance 以内で最も近い既存の人物トラックに引き継ぐ(無ければ新しいトラック)
//   5. trackTimeout 秒以上見つからなかったトラックは破棄する
// リアルタイムの「最新フレーム」の代わりに、sampleInterval ごとの時刻でその時刻以前の最新フレームを使う。
[RequireComponent(typeof(HomeEnvLoader))]
public class HomeSkeletonFilter : MonoBehaviour
{
    [Header("Subject")]
    [HomeExperimentFolder(HomeExperimentFolderAttribute.Kind.Subject)]
    [SerializeField] private string subjectName = "";

    public enum ConfidenceMode
    {
        // 全関節の confidence の平均(SkeletonRealtimePublisher と同じ)
        AllJoints,
        // headJoints に指定した関節だけの confidence の平均
        HeadJoints
    }

    [Header("Confidence")]
    [Tooltip("同一人物とみなした骨格のうち、どれを採用するかを決める信頼度の計算方法。")]
    [SerializeField] private ConfidenceMode confidenceMode = ConfidenceMode.AllJoints;
    [Tooltip("HeadJoints のときに信頼度の平均を取る関節(頭部方向の推定に使う関節)。")]
    [SerializeField] private JointId[] headJoints =
    {
        JointId.Head, JointId.Nose, JointId.EyeLeft, JointId.EyeRight, JointId.EarLeft, JointId.EarRight
    };

    [Header("Fusion (SkeletonRealtimePublisher と同じ)")]
    [Tooltip("この距離(m)以内のPelvis位置同士は同一人物とみなす。")]
    [SerializeField] private float sameBodyDistance = 0.5f;
    [Tooltip("人物トラックをこの距離(m)以内なら同一人物として引き継ぐ。")]
    [SerializeField] private float trackMatchDistance = 1.0f;
    [Tooltip("この秒数以上マッチしなかったトラックは破棄する。")]
    [SerializeField] private float trackTimeout = 2f;

    [Header("Sampling")]
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
    [Tooltip("人物トラックごとの色")]
    [SerializeField] private Color[] trackColors =
    {
        Color.red, Color.green, Color.blue, Color.yellow, Color.cyan, Color.magenta
    };
    [SerializeField] private bool showHeadDirection = true;
    [SerializeField] private float headDirectionLength = 0.4f;
    [SerializeField] private float headDirectionWidth = 0.015f;

    [Header("Playback")]
    [SerializeField] private float playbackSpeed = 1f;
    [SerializeField] private bool loop = false;
    [SerializeField] private KeyCode playStopKey = KeyCode.Space;

    [Header("UI")]
    [SerializeField] private Button playButton;
    [SerializeField] private Button stopButton;
    [SerializeField] private TMP_Text frameText;

    private HomeEnvLoader env;
    private HomeFilteredSkeletonList result;
    private float[] frameTimes = new float[0];
    private Transform visualRoot;
    private HomeSkeletonBodyVisual.Style visualStyle;
    private readonly Dictionary<int, HomeSkeletonBodyVisual> visuals = new Dictionary<int, HomeSkeletonBodyVisual>();
    private float duration;
    private float playbackTime;
    private bool isPlaying;
    private int shownIndex = -1;

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

        RunFilter();

        if (playButton != null)
            playButton.onClick.AddListener(Play);

        if (stopButton != null)
            stopButton.onClick.AddListener(Stop);

        UpdateText();
    }

    private void Update()
    {
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

        ShowFrameAt(playbackTime);
        UpdateText();
    }

    private void LateUpdate()
    {
        HomeExperimentPaths.ClearUISelection();
    }

    // ------------------------------------------------------------
    // Filter
    // ------------------------------------------------------------
    [ContextMenu("Run Filter")]
    public void RunFilter()
    {
        Stop();

        List<HomeSkeletonTrack> sources = HomeSkeletonIO.LoadSubject(env.ExperimentName, subjectName, "HomeSkeletonFilter");

        if (sources.Count == 0)
        {
            result = null;
            frameTimes = new float[0];
            duration = 0f;
            return;
        }

        result = Filter(sources);

        frameTimes = new float[result.frames.Count];
        for (int i = 0; i < frameTimes.Length; i++)
            frameTimes[i] = result.frames[i].timeSec;

        duration = frameTimes.Length > 0 ? frameTimes[frameTimes.Length - 1] : 0f;

        Save(result);
        LogSummary(result, sources);
    }

    private HomeFilteredSkeletonList Filter(List<HomeSkeletonTrack> sources)
    {
        HomeFilteredSkeletonList filtered = new HomeFilteredSkeletonList
        {
            experimentName = env.ExperimentName,
            subjectName = subjectName,
            createdAt = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
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

            foreach (Candidate candidate in selected)
            {
                PersonTrack track = MatchOrCreateTrack(tracks, candidate.pelvisPosition, time, ref nextTrackId);
                track.position = candidate.pelvisPosition;
                track.lastSeenTime = time;

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

    private PersonTrack MatchOrCreateTrack(List<PersonTrack> tracks, Vector3 position, float time, ref int nextTrackId)
    {
        PersonTrack closest = null;
        float closestDistance = trackMatchDistance;

        foreach (PersonTrack track in tracks)
        {
            float distance = Vector3.Distance(track.position, position);

            if (distance <= closestDistance)
            {
                closest = track;
                closestDistance = distance;
            }
        }

        if (closest != null)
            return closest;

        PersonTrack newTrack = new PersonTrack
        {
            id = nextTrackId++,
            position = position,
            lastSeenTime = time
        };

        tracks.Add(newTrack);
        return newTrack;
    }

    private void Save(HomeFilteredSkeletonList data)
    {
        string path = HomeExperimentPaths.GetFilteredSkeletonPath(env.ExperimentName, subjectName, data.confidenceMode);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(data));
            HomeExperimentPaths.RefreshAssetDatabase();
            Debug.Log($"[HomeSkeletonFilter] 保存しました ({data.frames.Count} frames): {path}");
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
    // Playback
    // ------------------------------------------------------------
    public void Play()
    {
        if (result == null || result.frames.Count == 0)
        {
            Debug.LogWarning("[HomeSkeletonFilter] 再生できる骨格データがありません。");
            return;
        }

        playbackTime = 0f;
        shownIndex = -1;
        isPlaying = true;
    }

    public void Stop()
    {
        isPlaying = false;
        playbackTime = 0f;
        shownIndex = -1;

        foreach (HomeSkeletonBodyVisual visual in visuals.Values)
            visual.SetVisible(false);

        UpdateText();
    }

    private void ShowFrameAt(float time)
    {
        int index = FindFrameIndex(time);

        if (index == shownIndex)
            return;

        shownIndex = index;

        HashSet<int> shown = new HashSet<int>();

        if (index >= 0)
        {
            foreach (HomeFilteredPerson person in result.frames[index].persons)
            {
                if (!visuals.TryGetValue(person.trackId, out HomeSkeletonBodyVisual visual))
                {
                    Color color = trackColors.Length > 0 ? trackColors[person.trackId % trackColors.Length] : Color.white;
                    visual = new HomeSkeletonBodyVisual(visualRoot, person.label, color, visualStyle);
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
        text.Append($"{subjectName} (Filtered: {confidenceMode})  Time : {playbackTime:F1} / {duration:F1} s");

        if (result != null && shownIndex >= 0 && shownIndex < result.frames.Count)
        {
            foreach (HomeFilteredPerson person in result.frames[shownIndex].persons)
                text.Append($"\n{person.label} : Kinect{person.kinectId} conf={person.confidence:F2}");
        }

        frameText.text = text.ToString();
    }

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
