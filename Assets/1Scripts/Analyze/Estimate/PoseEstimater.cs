using Microsoft.Azure.Kinect.BodyTracking;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class PoseEstimater : MonoBehaviour
{
    [Header("JSON")]
    [SerializeField] private string jsonFileName = "skeleton.json";

    [Header("Visual")]
    [SerializeField] private GameObject jointPrefab;

    [SerializeField] private Material lineMaterial;

    [Header("Playback")]
    [SerializeField] private float playbackSpeed = 1.0f;

    [Header("UI")]
    [SerializeField] private Button playButton;

    [SerializeField] private Button stopButton;

    [Header("Pose UI")]
    [SerializeField] private TMP_Text poseText;

    // =========================
    // Internal
    // =========================

    private List<FrameData> frames = new();

    private Dictionary<JointId, GameObject>
        jointObjects = new();

    private List<LineData> lines = new();

    private float playbackTime = 0f;

    private bool isPlaying = false;

    private int frameIndex = 0;

    private FrameData lastValidFrame = null;

    // =========================
    // Movement
    // =========================

    private Vector3 prevPelvisWorld;

    private bool hasPrevPelvis = false;

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

        playbackTime +=
            Time.deltaTime * playbackSpeed;

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

        hasPrevPelvis = false;

        isPlaying = true;

        HideSkeleton();

        SetPoseUI("START");

        Debug.Log("Playback started");
    }

    public void Stop()
    {
        isPlaying = false;

        HideSkeleton();

        SetPoseUI("STOP");

        Debug.Log("Playback stopped");
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
            Debug.LogError(
                $"JSON not found: {path}"
            );

            return;
        }

        string json =
            File.ReadAllText(path);

        FrameList frameList =
            JsonUtility.FromJson<FrameList>(json);

        frames = frameList.frames;

        Debug.Log(
            $"Loaded frames: {frames.Count}"
        );
    }

    // =========================
    // Create Joint Objects
    // =========================

    private void CreateJointObjects()
    {
        if (frames.Count == 0)
            return;

        FrameData firstValid =
            frames.Find(
                f =>
                    f.joints != null &&
                    f.joints.Count > 0
            );

        if (firstValid == null)
        {
            Debug.LogError(
                "No valid skeleton frame found."
            );

            return;
        }

        foreach (JointPosition jp in firstValid.joints)
        {
            if (
                System.Enum.TryParse(
                    jp.jointId,
                    out JointId jointId
                )
            )
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
    // Create Lines
    // =========================

    private void CreateLines()
    {
        AddLine(new JointId[]
        {
            JointId.Pelvis,
            JointId.SpineNavel,
            JointId.SpineChest,
            JointId.Neck,
            JointId.Head,
            JointId.Nose
        });

        AddLine(new JointId[]
        {
            JointId.FootRight,
            JointId.AnkleRight,
            JointId.KneeRight,
            JointId.HipRight,
            JointId.Pelvis,
            JointId.HipLeft,
            JointId.KneeLeft,
            JointId.AnkleLeft,
            JointId.FootLeft
        });

        AddLine(new JointId[]
        {
            JointId.HandTipLeft,
            JointId.HandLeft,
            JointId.WristLeft,
            JointId.ElbowLeft,
            JointId.ShoulderLeft,
            JointId.ClavicleLeft,
            JointId.SpineChest,
            JointId.ClavicleRight,
            JointId.ShoulderRight,
            JointId.ElbowRight,
            JointId.WristRight,
            JointId.HandRight,
            JointId.HandTipRight
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

        lines.Add(
            new LineData
            {
                joints = joints,
                line = lr
            }
        );
    }

    // =========================
    // Playback
    // =========================

    private void UpdateSkeletonByTime(float timeSec)
    {
        if (frameIndex >= frames.Count)
            return;

        float frameTime =
            frames[frameIndex]
            .normalizedTimestampTicks
            * 1e-7f;

        if (timeSec < frameTime)
        {
            if (lastValidFrame != null)
                ApplyFrame(lastValidFrame);

            return;
        }

        FrameData current =
            frames[frameIndex];

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

            EstimatePose(current);

            lastValidFrame = current;
        }

        frameIndex++;
    }

    // =========================
    // Pose Estimation
    // =========================

    private void EstimatePose(FrameData frame)
    {
        Dictionary<string, Vector3> joints =
            new Dictionary<string, Vector3>();

        foreach (var jp in frame.joints)
        {
            joints[jp.jointId] = jp.position;
        }

        // =========================
        // Required joints
        // =========================

        string[] required =
        {
            "Pelvis",
            "Head",
            "FootLeft",
            "FootRight",
            "HandLeft",
            "HandRight",
            "KneeLeft",
            "KneeRight"
        };

        foreach (string r in required)
        {
            if (!joints.ContainsKey(r))
                return;
        }

        // =========================
        // World pelvis
        // =========================

        Vector3 pelvisWorld =
            joints["Pelvis"];

        // =========================
        // Relative coordinates
        // =========================

        Vector3 head =
            joints["Head"] - pelvisWorld;

        Vector3 footL =
            joints["FootLeft"] - pelvisWorld;

        Vector3 footR =
            joints["FootRight"] - pelvisWorld;

        Vector3 handL =
            joints["HandLeft"] - pelvisWorld;

        Vector3 handR =
            joints["HandRight"] - pelvisWorld;

        Vector3 kneeL =
            joints["KneeLeft"] - pelvisWorld;

        Vector3 kneeR =
            joints["KneeRight"] - pelvisWorld;

        // =========================
        // Relative values
        // =========================

        float headY = head.y;

        float handLY = handL.y;
        float handRY = handR.y;

        float kneeLY = kneeL.y;
        float kneeRY = kneeR.y;

        float footAvgY =
            (footL.y + footR.y) * 0.5f;

        float verticalBodySize =
            headY - footAvgY;

        // =========================
        // Movement
        // =========================

        float horizontalSpeed = 0f;

        float verticalSpeed = 0f;

        if (hasPrevPelvis)
        {
            Vector3 delta =
                pelvisWorld - prevPelvisWorld;

            Vector2 horizontal =
                new Vector2(delta.x, delta.z);

            horizontalSpeed =
                horizontal.magnitude /
                Time.deltaTime;

            verticalSpeed =
                delta.y /
                Time.deltaTime;
        }

        prevPelvisWorld = pelvisWorld;

        hasPrevPelvis = true;

        // =========================
        // Crawling structure
        // =========================

        bool headForward =
            Mathf.Abs(head.z) > 0.15f;

        bool handsLow =
            handLY < -0.15f &&
            handRY < -0.15f;

        bool kneesLow =
            kneeLY < -0.10f &&
            kneeRY < -0.10f;

        bool headLow =
            headY < 0.45f;

        // =========================
        // Classification
        // =========================

        string pose = "Unknown";

        // =========================
        // Jump
        // =========================

        if (
            verticalSpeed > 1.2f
        )
        {
            pose = "Jump";
        }

        // =========================
        // Crawling
        // =========================

        else if (
            headLow &&
            handsLow &&
            kneesLow &&
            headForward &&
            horizontalSpeed > 0.03f
        )
        {
            pose = "Crawling";
        }

        // =========================
        // Sitting
        // =========================

        else if (
            verticalBodySize < 0.75f &&
            !headForward
        )
        {
            pose = "Sitting";
        }

        // =========================
        // Walking
        // =========================

        else if (
            verticalBodySize > 0.75f &&
            horizontalSpeed > 0.15f
        )
        {
            pose = "Walking";
        }

        // =========================
        // Standing
        // =========================

        else if (
            verticalBodySize > 0.75f
        )
        {
            pose = "Standing";
        }

        // =========================
        // UI表示
        // =========================

        SetPoseUI(pose);
    }

    // =========================
    // Pose UI
    // =========================

    private void SetPoseUI(string pose)
    {
        if (poseText == null)
            return;

        poseText.text = pose;

        switch (pose)
        {
            case "Standing":
                poseText.color = Color.green;
                break;

            case "Walking":
                poseText.color = Color.blue;
                break;

            case "Crawling":
                poseText.color = Color.yellow;
                break;

            case "Sitting":
                poseText.color = Color.cyan;
                break;

            case "Jump":
                poseText.color = Color.red;
                break;

            default:
                poseText.color = Color.white;
                break;
        }
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

            for (
                int i = 0;
                i < line.joints.Length;
                i++
            )
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