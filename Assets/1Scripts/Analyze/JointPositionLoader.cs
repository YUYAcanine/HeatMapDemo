using Microsoft.Azure.Kinect.BodyTracking;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

public class JointPositionLoader : MonoBehaviour
{
    [Header("JSON")]
    [SerializeField] private string jsonFileName = "skeleton.json";

    [Header("Visual")]
    [SerializeField] private GameObject jointPrefab;
    [SerializeField] private Material lineMaterial;

    [Header("Playback")]
    [SerializeField] private float playbackSpeed = 1.0f; // 1.0 = real time

    [Header("UI")]
    [SerializeField] private Button playButton;
    [SerializeField] private Button stopButton;

    // =========================
    // Internal data
    // =========================
    private List<FrameData> frames = new();
    private Dictionary<JointId, GameObject> jointObjects = new();
    private List<LineData> lines = new();

    private float playbackTime = 0f;   // seconds
    private bool isPlaying = false;

    private int frameIndex = 0;
    private FrameData lastValidFrame = null;

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

    void Update()
    {
        if (!isPlaying) return;

        playbackTime += Time.deltaTime * playbackSpeed;
        UpdateSkeletonByTime(playbackTime);
    }

    // =========================
    // UI
    // =========================
    private void SetupUI()
    {
        if (playButton != null)
            playButton.onClick.AddListener(Play);

        if (stopButton != null)
            stopButton.onClick.AddListener(Stop);
    }

    public void Play()
    {
        playbackTime = 0f;
        frameIndex = 0;
        lastValidFrame = null;
        isPlaying = true;
        HideSkeleton();
        Debug.Log("Playback started");
    }

    public void Stop()
    {
        isPlaying = false;
        lastValidFrame = null;
        HideSkeleton();
        Debug.Log("Playback stopped");
    }

    // =========================
    // JSON Load（Assets/Data）
    // =========================
    private void LoadFrames()
    {
        string path = Path.Combine(Application.dataPath, "Data", jsonFileName);
        if (!File.Exists(path))
        {
            Debug.LogError($"JSON not found: {path}");
            return;
        }

        string json = File.ReadAllText(path);
        FrameList frameList = JsonUtility.FromJson<FrameList>(json);
        frames = frameList.frames;

        Debug.Log($"Loaded frames: {frames.Count}");
    }

    // =========================
    // Skeleton Objects
    // =========================
    private void CreateJointObjects()
    {
        if (frames.Count == 0) return;

        // joints が存在する最初のフレームを探す
        FrameData firstValid = frames.Find(f => f.joints != null && f.joints.Count > 0);
        if (firstValid == null)
        {
            Debug.LogError("No valid skeleton frame found.");
            return;
        }

        foreach (JointPosition jp in firstValid.joints)
        {
            if (System.Enum.TryParse(jp.jointId, out JointId jointId))
            {
                GameObject obj = Instantiate(jointPrefab, Vector3.zero, Quaternion.identity);
                obj.transform.localScale = Vector3.one * 0.06f;
                obj.name = jointId.ToString();
                jointObjects[jointId] = obj;
            }
        }

        HideSkeleton();
    }

    private void CreateLines()
    {
        AddLine(new JointId[] {
            JointId.Pelvis, JointId.SpineNavel, JointId.SpineChest,
            JointId.Neck, JointId.Head, JointId.Nose
        });

        AddLine(new JointId[] {
            JointId.FootRight, JointId.AnkleRight, JointId.KneeRight,
            JointId.HipRight, JointId.Pelvis,
            JointId.HipLeft, JointId.KneeLeft,
            JointId.AnkleLeft, JointId.FootLeft
        });

        AddLine(new JointId[] {
            JointId.HandTipLeft, JointId.HandLeft, JointId.WristLeft,
            JointId.ElbowLeft, JointId.ShoulderLeft, JointId.ClavicleLeft,
            JointId.SpineChest,
            JointId.ClavicleRight, JointId.ShoulderRight, JointId.ElbowRight,
            JointId.WristRight, JointId.HandRight, JointId.HandTipRight
        });
    }

    private void AddLine(JointId[] joints)
    {
        LineRenderer lr = new GameObject("Line").AddComponent<LineRenderer>();
        lr.material = lineMaterial;
        lr.startWidth = 0.02f;
        lr.endWidth = 0.02f;
        lr.positionCount = joints.Length;
        lr.enabled = false;

        lines.Add(new LineData { joints = joints, line = lr });
    }

    // =========================
    // Playback core
    // =========================
    private void UpdateSkeletonByTime(float timeSec)
    {
        if (frameIndex >= frames.Count)
            return;

        float frameTime =
            frames[frameIndex].normalizedTimestampTicks * 1e-7f;

        if (timeSec < frameTime)
        {
            if (lastValidFrame != null)
                ApplyFrame(lastValidFrame);
            return;
        }

        FrameData current = frames[frameIndex];

        if (current.joints == null || current.joints.Count == 0)
        {
            HideSkeleton();
        }
        else
        {
            ApplyFrame(current);
            lastValidFrame = current;
        }

        frameIndex++;
    }

    private void ApplyFrame(FrameData frame)
    {
        foreach (var obj in jointObjects.Values)
            obj.SetActive(false);

        foreach (var jp in frame.joints)
        {
            if (System.Enum.TryParse(jp.jointId, out JointId jointId) &&
                jointObjects.TryGetValue(jointId, out GameObject obj))
            {
                obj.transform.position = jp.position;
                obj.SetActive(true);
            }
        }

        foreach (var line in lines)
        {
            bool valid = true;

            for (int i = 0; i < line.joints.Length; i++)
            {
                if (jointObjects.TryGetValue(line.joints[i], out GameObject obj) &&
                    obj.activeSelf)
                {
                    line.line.SetPosition(i, obj.transform.position);
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

    private void HideSkeleton()
    {
        foreach (var obj in jointObjects.Values)
            obj.SetActive(false);

        foreach (var line in lines)
            line.line.enabled = false;
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
        public long deviceTimestampTicks;
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
