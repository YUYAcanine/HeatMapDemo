using Microsoft.Azure.Kinect.BodyTracking;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class JointPositionLoaderMulti : MonoBehaviour
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
    [SerializeField] private bool showFrameNumber = true;
    [SerializeField] private TMP_Text frameText;

    [Header("Keyboard Shortcut (UIボタンが無い場合のフォールバック)")]
    [SerializeField] private KeyCode playStopKey = KeyCode.Space;

    // =========================
    // Internal data
    // =========================
    private List<FrameData> frames = new();

    // bodyId ごとに関節オブジェクト/ラインを保持(人数分だけ動的に生成)
    private Dictionary<uint, BodyVisual> bodyVisuals = new();

    private static readonly Color[] bodyColors = new Color[]
    {
        Color.red, Color.green, Color.blue,
        Color.yellow, Color.cyan, Color.magenta
    };

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
        SetupUI();
        UpdateFrameText();
    }

    void Update()
    {
        if (Input.GetKeyDown(playStopKey))
        {
            if (isPlaying)
                Stop();
            else
                Play();
        }

        UpdateFrameText();

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
        UpdateFrameText();
        Debug.Log("Playback started");
    }

    public void Stop()
    {
        isPlaying = false;
        lastValidFrame = null;
        HideSkeleton();
        UpdateFrameText();
        Debug.Log("Playback stopped");
    }

    // =========================
    // JSON Load�iAssets/Data�j
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
    // Skeleton Objects (bodyId ごとに遅延生成)
    // =========================
    private BodyVisual GetOrCreateBodyVisual(uint bodyId)
    {
        if (bodyVisuals.TryGetValue(bodyId, out BodyVisual existing))
            return existing;

        Color color = bodyColors[bodyVisuals.Count % bodyColors.Length];

        BodyVisual visual = new BodyVisual
        {
            jointObjects = new Dictionary<JointId, GameObject>(),
            lines = new List<LineData>()
        };

        for (JointId jointId = JointId.Pelvis; jointId <= JointId.Nose; jointId++)
        {
            GameObject obj = Instantiate(jointPrefab, Vector3.zero, Quaternion.identity);
            obj.transform.localScale = Vector3.one * 0.06f;
            obj.name = $"Body{bodyId}_{jointId}";

            Renderer renderer = obj.GetComponentInChildren<Renderer>();
            if (renderer != null)
                renderer.material.color = color;

            obj.SetActive(false);
            visual.jointObjects[jointId] = obj;
        }

        AddLine(visual, color, new JointId[] {
            JointId.Pelvis, JointId.SpineNavel, JointId.SpineChest,
            JointId.Neck, JointId.Head, JointId.Nose
        });

        AddLine(visual, color, new JointId[] {
            JointId.FootRight, JointId.AnkleRight, JointId.KneeRight,
            JointId.HipRight, JointId.Pelvis,
            JointId.HipLeft, JointId.KneeLeft,
            JointId.AnkleLeft, JointId.FootLeft
        });

        AddLine(visual, color, new JointId[] {
            JointId.HandTipLeft, JointId.HandLeft, JointId.WristLeft,
            JointId.ElbowLeft, JointId.ShoulderLeft, JointId.ClavicleLeft,
            JointId.SpineChest,
            JointId.ClavicleRight, JointId.ShoulderRight, JointId.ElbowRight,
            JointId.WristRight, JointId.HandRight, JointId.HandTipRight
        });

        bodyVisuals[bodyId] = visual;
        return visual;
    }

    private void AddLine(BodyVisual visual, Color color, JointId[] joints)
    {
        LineRenderer lr = new GameObject("Line").AddComponent<LineRenderer>();
        lr.material = lineMaterial;
        lr.startColor = color;
        lr.endColor = color;
        lr.startWidth = 0.02f;
        lr.endWidth = 0.02f;
        lr.positionCount = joints.Length;
        lr.enabled = false;

        visual.lines.Add(new LineData { joints = joints, line = lr });
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

        if (current.bodies == null || current.bodies.Count == 0)
        {
            HideSkeleton();
        }
        else
        {
            ApplyFrame(current);
            lastValidFrame = current;
        }

        frameIndex++;
        UpdateFrameText();
    }

    private void UpdateFrameText()
    {
        if (frameText == null)
            return;

        if (frameText.gameObject.activeSelf != showFrameNumber)
            frameText.gameObject.SetActive(showFrameNumber);

        if (!showFrameNumber)
            return;

        int displayedFrame =
            Mathf.Clamp(frameIndex, 0, frames.Count);

        frameText.text =
            $"Frame : {displayedFrame} / {frames.Count}";
    }

    private void ApplyFrame(FrameData frame)
    {
        HideSkeleton();

        if (frame.bodies == null)
            return;

        foreach (BodyData body in frame.bodies)
        {
            BodyVisual visual = GetOrCreateBodyVisual(body.bodyId);

            if (body.joints == null)
                continue;

            foreach (var jp in body.joints)
            {
                if (System.Enum.TryParse(jp.jointId, out JointId jointId) &&
                    visual.jointObjects.TryGetValue(jointId, out GameObject obj))
                {
                    obj.transform.position = jp.position;
                    obj.SetActive(true);
                }
            }

            foreach (var line in visual.lines)
            {
                bool valid = true;

                for (int i = 0; i < line.joints.Length; i++)
                {
                    if (visual.jointObjects.TryGetValue(line.joints[i], out GameObject obj) &&
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
    }

    private void HideSkeleton()
    {
        foreach (var visual in bodyVisuals.Values)
        {
            foreach (var obj in visual.jointObjects.Values)
                obj.SetActive(false);

            foreach (var line in visual.lines)
                line.line.enabled = false;
        }
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
        public List<BodyData> bodies;
    }

    [System.Serializable]
    private class BodyData
    {
        public uint bodyId;
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

    private class BodyVisual
    {
        public Dictionary<JointId, GameObject> jointObjects;
        public List<LineData> lines;
    }
}
