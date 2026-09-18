using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// シーン2(2HeadDirViewer)用。選んだ実験対象者のフォルダ
//   Assets/Data/HomeExperiment/<実験の名前>/Skeleton/<実験対象者>/*_skeleton.json
// をすべて読み込み、キネクトごとに色分けして同時に再生する。
// 骨格データは部屋座標で記録されているので、キネクトの位置は使わない。
// 実験の名前と部屋オブジェクトの再現は同じGameObjectの HomeEnvLoader が担当する。
[RequireComponent(typeof(HomeEnvLoader))]
public class HomeSkeletonPlayer : MonoBehaviour
{
    [Header("Subject")]
    [HomeExperimentFolder(HomeExperimentFolderAttribute.Kind.Subject)]
    [SerializeField] private string subjectName = "";

    [Header("Visual")]
    [SerializeField] private GameObject jointPrefab;
    [Tooltip("空の場合は頂点カラー対応の Sprites/Default を使い、キネクトごとの色で線を描く。")]
    [SerializeField] private Material lineMaterial;
    [SerializeField] private float jointScale = 0.06f;
    [SerializeField] private float lineWidth = 0.02f;
    [SerializeField] private Color[] kinectColors =
    {
        Color.red, Color.green, Color.blue, Color.yellow, Color.cyan, Color.magenta
    };

    [Header("Head Direction")]
    [Tooltip("両耳の中点→鼻 の向きを頭部方向として頭から線で表示する。")]
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
    private Transform visualRoot;
    private readonly List<Track> tracks = new List<Track>();
    private HomeSkeletonBodyVisual.Style visualStyle;
    private float duration;
    private float playbackTime;
    private bool isPlaying;

    private void Awake()
    {
        env = GetComponent<HomeEnvLoader>();
    }

    private void Start()
    {
        visualRoot = new GameObject("SkeletonPlayback").transform;
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

        LoadTracks();

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

        foreach (Track track in tracks)
            ShowFrameAt(track, playbackTime);

        UpdateText();
    }

    private void LateUpdate()
    {
        HomeExperimentPaths.ClearUISelection();
    }

    public void Play()
    {
        if (tracks.Count == 0)
        {
            Debug.LogWarning("[HomeSkeletonPlayer] 再生できる骨格データがありません。");
            return;
        }

        playbackTime = 0f;
        isPlaying = true;
        Debug.Log("[HomeSkeletonPlayer] Playback started");
    }

    public void Stop()
    {
        isPlaying = false;
        playbackTime = 0f;

        foreach (Track track in tracks)
            HideTrack(track);

        UpdateText();
        Debug.Log("[HomeSkeletonPlayer] Playback stopped");
    }

    // ------------------------------------------------------------
    // Load
    // ------------------------------------------------------------
    private void LoadTracks()
    {
        foreach (HomeSkeletonTrack source in HomeSkeletonIO.LoadSubject(env.ExperimentName, subjectName, "HomeSkeletonPlayer"))
        {
            tracks.Add(new Track
            {
                source = source,
                color = kinectColors.Length > 0 ? kinectColors[tracks.Count % kinectColors.Length] : Color.white
            });

            duration = Mathf.Max(duration, source.Duration);
        }
    }

    // ------------------------------------------------------------
    // Playback
    // ------------------------------------------------------------
    private void ShowFrameAt(Track track, float time)
    {
        int index = track.source.FindFrameIndex(time);

        if (index == track.shownIndex)
            return;

        track.shownIndex = index;

        if (index < 0)
        {
            HideTrack(track);
            return;
        }

        List<HomeSkeletonBody> bodies = track.source.frames[index].bodies;
        int bodyCount = bodies != null ? bodies.Count : 0;

        for (int i = 0; i < bodyCount; i++)
        {
            if (i >= track.visuals.Count)
            {
                track.visuals.Add(new HomeSkeletonBodyVisual(
                    visualRoot, $"Kinect{track.source.kinectId}_Body{i}", track.color, visualStyle));
            }

            track.visuals[i].Apply(bodies[i].joints);
        }

        for (int i = bodyCount; i < track.visuals.Count; i++)
            track.visuals[i].SetVisible(false);
    }

    private void HideTrack(Track track)
    {
        track.shownIndex = -1;

        foreach (HomeSkeletonBodyVisual visual in track.visuals)
            visual.SetVisible(false);
    }

    private void UpdateText()
    {
        if (frameText == null)
            return;

        System.Text.StringBuilder text = new System.Text.StringBuilder();
        text.Append($"{subjectName}  Time : {playbackTime:F1} / {duration:F1} s");

        foreach (Track track in tracks)
            text.Append($"\nKinect{track.source.kinectId} : {track.shownIndex + 1} / {track.source.frames.Count}");

        frameText.text = text.ToString();
    }

    private class Track
    {
        public HomeSkeletonTrack source;
        public Color color;
        public int shownIndex = -1;
        public readonly List<HomeSkeletonBodyVisual> visuals = new List<HomeSkeletonBodyVisual>();
    }
}
