using Microsoft.Azure.Kinect.BodyTracking;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class PoseLabeler : MonoBehaviour
{
    [Header("JSON")]
    [SerializeField] private string jsonFileName = "M_skeleton.json";

    [Header("Visual")]
    [SerializeField] private GameObject jointPrefab;
    [SerializeField] private Material lineMaterial;

    [Header("Playback")]
    [SerializeField] private float playbackSpeed = 1.0f;

    [Header("UI Buttons")]
    [SerializeField] private Button playButton;
    [SerializeField] private Button stopButton;

    [SerializeField] private Button standButton;
    [SerializeField] private Button walkButton;
    [SerializeField] private Button crawlButton;
    [SerializeField] private Button sitButton;
    [SerializeField] private Button jumpButton;
    [SerializeField] private Button transitionButton;

    [SerializeField] private Button saveButton;

    [Header("UI Text")]
    [SerializeField] private TMP_Text labelText;
    [SerializeField] private TMP_Text frameText;

    // =========================
    // Internal
    // =========================

    private List<FrameData> frames = new();

    private Dictionary<JointId, GameObject> jointObjects = new();

    private List<LineData> lines = new();

    private float playbackTime = 0f;

    private bool isPlaying = false;

    private int frameIndex = 0;

    private FrameData lastValidFrame = null;

    // =========================
    // Label Segment
    // =========================

    [System.Serializable]
    public class LabelSegment
    {
        public int startFrame;
        public int endFrame;
        public string label;
    }

    private List<LabelSegment> segments = new();

    private string currentLabel = "none";

    private int currentSegmentStart = 0;

    // =========================
    // Start
    // =========================

    void Start()
    {
        LoadFrames();

        CreateJointObjects();

        CreateLines();

        SetupUI();
    }

    // =========================
    // Update
    // =========================

    void Update()
    {
        if (!isPlaying)
            return;

        playbackTime += Time.deltaTime * playbackSpeed;

        UpdateSkeletonByTime(playbackTime);
    }

    // =========================
    // UI Setup
    // =========================

    private void SetupUI()
    {
        playButton.onClick.AddListener(Play);
        stopButton.onClick.AddListener(Stop);

        standButton.onClick.AddListener(() => ChangeLabel("stand"));
        walkButton.onClick.AddListener(() => ChangeLabel("walk"));
        crawlButton.onClick.AddListener(() => ChangeLabel("crawl"));
        sitButton.onClick.AddListener(() => ChangeLabel("sit"));
        jumpButton.onClick.AddListener(() => ChangeLabel("jump"));
        transitionButton.onClick.AddListener(() => ChangeLabel("transition"));

        saveButton.onClick.AddListener(SaveLabelsCSV);
    }

    // =========================
    // Playback
    // =========================

    public void Play()
    {
        playbackTime = 0f;

        frameIndex = 0;

        lastValidFrame = null;

        isPlaying = true;

        HideSkeleton();

        Debug.Log("Playback Started");
    }

    public void Stop()
    {
        isPlaying = false;

        HideSkeleton();

        Debug.Log("Playback Stopped");
    }

    // =========================
    // Change Label
    // =========================

    private void ChangeLabel(string newLabel)
    {
        // 最初のラベル設定
        if (segments.Count == 0)
        {
            currentSegmentStart = frameIndex;
        }
        else
        {
            // 直前セグメント終了
            segments[segments.Count - 1].endFrame = frameIndex - 1;
        }

        // 新セグメント開始
        LabelSegment seg = new LabelSegment
        {
            startFrame = frameIndex,
            endFrame = frameIndex,
            label = newLabel
        };

        segments.Add(seg);

        currentLabel = newLabel;

        UpdateUI();

        Debug.Log($"Label Changed : {newLabel} @ {frameIndex}");
    }

    // =========================
    // Update UI
    // =========================

    private void UpdateUI()
    {
        if (labelText != null)
        {
            labelText.text = $"Label : {currentLabel}";
        }

        if (frameText != null)
        {
            frameText.text = $"Frame : {frameIndex}";
        }
    }

    // =========================
    // JSON Load
    // =========================

    private void LoadFrames()
    {
        string path =
            Path.Combine(
                Application.dataPath,
                "Data",
                jsonFileName
            );

        if (!File.Exists(path))
        {
            Debug.LogError($"JSON not found : {path}");
            return;
        }

        string json = File.ReadAllText(path);

        FrameList frameList =
            JsonUtility.FromJson<FrameList>(json);

        frames = frameList.frames;

        Debug.Log($"Loaded Frames : {frames.Count}");
    }

    // =========================
    // Joint Visual
    // =========================

    private void CreateJointObjects()
    {
        if (frames.Count == 0)
            return;

        FrameData firstValid =
            frames.Find(f =>
                f.joints != null &&
                f.joints.Count > 0);

        if (firstValid == null)
            return;

        foreach (JointPosition jp in firstValid.joints)
        {
            if (System.Enum.TryParse(
                jp.jointId,
                out JointId jointId))
            {
                GameObject obj =
                    Instantiate(
                        jointPrefab,
                        Vector3.zero,
                        Quaternion.identity
                    );

                obj.transform.localScale =
                    Vector3.one * 0.06f;

                obj.name = jointId.ToString();

                jointObjects[jointId] = obj;
            }
        }

        HideSkeleton();
    }

    // =========================
    // Lines
    // =========================

    private void CreateLines()
    {
        AddLine(new JointId[]
        {
            JointId.Pelvis,
            JointId.SpineNavel,
            JointId.SpineChest,
            JointId.Neck,
            JointId.Head
        });

        AddLine(new JointId[]
        {
            JointId.ShoulderLeft,
            JointId.ElbowLeft,
            JointId.WristLeft
        });

        AddLine(new JointId[]
        {
            JointId.ShoulderRight,
            JointId.ElbowRight,
            JointId.WristRight
        });

        AddLine(new JointId[]
        {
            JointId.HipLeft,
            JointId.KneeLeft,
            JointId.AnkleLeft,
            JointId.FootLeft
        });

        AddLine(new JointId[]
        {
            JointId.HipRight,
            JointId.KneeRight,
            JointId.AnkleRight,
            JointId.FootRight
        });
    }

    private void AddLine(JointId[] joints)
    {
        LineRenderer lr =
            new GameObject("Line")
            .AddComponent<LineRenderer>();

        lr.material = lineMaterial;

        lr.startWidth = 0.02f;
        lr.endWidth = 0.02f;

        lr.positionCount = joints.Length;

        lr.enabled = false;

        lines.Add(new LineData
        {
            joints = joints,
            line = lr
        });
    }

    // =========================
    // Playback Core
    // =========================

    private void UpdateSkeletonByTime(float timeSec)
    {
        if (frameIndex >= frames.Count)
            return;

        float frameTime =
            frames[frameIndex]
            .normalizedTimestampTicks * 1e-7f;

        if (timeSec < frameTime)
        {
            if (lastValidFrame != null)
                ApplyFrame(lastValidFrame);

            return;
        }

        FrameData current = frames[frameIndex];

        if (
            current.joints == null ||
            current.joints.Count == 0
        )
        {
            HideSkeleton();
        }
        else
        {
            ApplyFrame(current);

            lastValidFrame = current;
        }

        UpdateUI();

        frameIndex++;
    }

    // =========================
    // Apply Frame
    // =========================

    private void ApplyFrame(FrameData frame)
    {
        foreach (var obj in jointObjects.Values)
            obj.SetActive(false);

        foreach (var jp in frame.joints)
        {
            if (
                System.Enum.TryParse(
                    jp.jointId,
                    out JointId jointId
                ) &&
                jointObjects.TryGetValue(
                    jointId,
                    out GameObject obj
                )
            )
            {
                obj.transform.position =
                    jp.position;

                obj.SetActive(true);
            }
        }

        foreach (var line in lines)
        {
            bool valid = true;

            for (int i = 0; i < line.joints.Length; i++)
            {
                if (
                    jointObjects.TryGetValue(
                        line.joints[i],
                        out GameObject obj
                    ) &&
                    obj.activeSelf
                )
                {
                    line.line.SetPosition(
                        i,
                        obj.transform.position
                    );
                }
                else
                {
                    valid = false;
                    break;
                }
            }

            line.line.enabled = valid;
        }
    }

    // =========================
    // Hide
    // =========================

    private void HideSkeleton()
    {
        foreach (var obj in jointObjects.Values)
            obj.SetActive(false);

        foreach (var line in lines)
            line.line.enabled = false;
    }

    // =========================
    // Save CSV
    // =========================

    private void SaveLabelsCSV()
    {
        // 最後のセグメント終了
        if (segments.Count > 0)
        {
            segments[segments.Count - 1].endFrame =
                frameIndex - 1;
        }

        string path =
            Path.Combine(
                Application.dataPath,
                "Data",
                "labels_segments.csv"
            );

        List<string> csv = new();

        csv.Add("start_frame,end_frame,label");

        foreach (var seg in segments)
        {
            csv.Add(
                $"{seg.startFrame}," +
                $"{seg.endFrame}," +
                $"{seg.label}"
            );
        }

        File.WriteAllLines(path, csv);

        Debug.Log($"Saved CSV : {path}");
    }

    // =========================
    // JSON Structures
    // =========================

    [System.Serializable]
    private class FrameList
    {
        public List<FrameData> frames;
    }

    [System.Serializable]
    private class FrameData
    {
        public long normalizedTimestampTicks;

        public float unityTime;

        public List<JointPosition> joints;
    }

    [System.Serializable]
    private class JointPosition
    {
        public string jointId;

        public Vector3 position;
    }

    private class LineData
    {
        public JointId[] joints;
        public LineRenderer line;
    }
}