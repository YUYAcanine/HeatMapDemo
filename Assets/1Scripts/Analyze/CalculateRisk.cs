using Microsoft.Azure.Kinect.BodyTracking;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Linq;

public class CalculateRisk : MonoBehaviour
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

    [Header("Risk")]
    [SerializeField] private Transform[] factors;
    [SerializeField] private TMP_Text riskText;

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
    private Vector3 previousPelvis;
    private bool firstPelvisFrame = true;

    private Dictionary<Transform, Queue<float>> gazeHistories = new();
    private Dictionary<Transform, float> gazeSums = new();

    // =========================
    // Start
    // =========================
    void Start()
    {
        if (factors != null)
        {
            foreach (Transform factor in factors)
            {
                if (factor == null)
                    continue;

                gazeHistories[factor] = new Queue<float>();
                gazeSums[factor] = 0f;
            }
        }

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
    // Skeleton Objects
    // =========================
    private void CreateJointObjects()
    {
        if (frames.Count == 0) return;

        // joints �����݂���ŏ��̃t���[����T��
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
        UpdateRisk();
    }

    private void HideSkeleton()
    {
        foreach (var obj in jointObjects.Values)
            obj.SetActive(false);

        foreach (var line in lines)
            line.line.enabled = false;
    }

    private void UpdateRisk()
    {
        if (factors == null || factors.Length == 0)
            return;

        if (!jointObjects.ContainsKey(JointId.Pelvis))
            return;

        if (!jointObjects.ContainsKey(JointId.Head))
            return;

        if (!jointObjects.ContainsKey(JointId.Nose))
            return;

        Vector3 pelvis =
            jointObjects[JointId.Pelvis]
            .transform.position;

        Vector3 head =
            jointObjects[JointId.Head]
            .transform.position;

        Vector3 nose =
            jointObjects[JointId.Nose]
            .transform.position;

        //--------------------------------
        // Velocity
        //--------------------------------

        Vector3 velocity =
            Vector3.zero;

        if (!firstPelvisFrame)
        {
            velocity =
                (
                    pelvis
                    - previousPelvis
                )
                /
                Time.deltaTime;
        }

        previousPelvis =
            pelvis;

        firstPelvisFrame = false;

        //--------------------------------
        // Attention (Cone)
        //--------------------------------

        Vector3 rawDir =
            (nose - head).normalized;

        // HeatMapと同じ補正
        Vector3 rightAxis =
            Vector3.Cross(
                Vector3.up,
                rawDir
            ).normalized;

        if (rightAxis.sqrMagnitude < 0.0001f)
        {
            rightAxis = Vector3.right;
        }

        Quaternion correction =
            Quaternion.AngleAxis(
                15f, // downwardAngle
                rightAxis
            );

        Vector3 dir =
            (correction * rawDir)
            .normalized;

        // Head to each factor
        string displayText = "";

        for (int i = 0; i < factors.Length; i++)
        {
            Transform factor = factors[i];

            if (factor == null)
                continue;

            if (!gazeHistories.ContainsKey(factor))
            {
                gazeHistories[factor] = new Queue<float>();
                gazeSums[factor] = 0f;
            }

            float distance =
                Vector3.Distance(
                    pelvis,
                    factor.position
                );

            float distanceScore =
                Mathf.Clamp01(
                    1f - distance / 1.5f
                );

            Vector3 toObject =
                (
                    factor.position
                    - pelvis
                ).normalized;

            float approachSpeed =
                Vector3.Dot(
                    velocity,
                    toObject
                );

            float velocityScore =
                Mathf.Clamp01(
                    approachSpeed / 0.3f
                );

            Vector3 toFactor =
                (
                    factor.position
                    - head
                ).normalized;

        // 角度計算
            float angle =
                Vector3.Angle(
                    dir,
                    toFactor
                );

        // 円錐内なら見ている
            bool lookingAtFactor =
                angle < 20f;
        
            float sample =
                lookingAtFactor ? 1f : 0f;

            gazeHistories[factor].Enqueue(sample);

            gazeSums[factor] += sample;

            int maxSamples =
                Mathf.RoundToInt(
                    3f /
                    Mathf.Max(
                        Time.deltaTime,
                        0.0001f
                    )
                );

            while (
                gazeHistories[factor].Count >
                maxSamples
            )
            {
                gazeSums[factor] -=
                    gazeHistories[factor].Dequeue();
            }

            float attentionScore = 0f;

            if (gazeHistories[factor].Count > 0)
            {
                attentionScore =
                    gazeSums[factor] /
                    gazeHistories[factor].Count;
            }

        // デバッグ表示
            Debug.DrawRay(
                head,
                dir * 3f,
                Color.red
            );

            Debug.DrawLine(
                head,
                factor.position,
                Color.green
            );

        //--------------------------------
        // Risk
        //--------------------------------

            float risk =
                (
                    distanceScore +
                    velocityScore +
                    attentionScore
                ) / 3f;

        //--------------------------------
        // UI
        //--------------------------------

            displayText +=
                $@"Factor{i + 1}

Risk : {risk:F2}

DistanceScore : {distanceScore:F2}

VelocityScore : {velocityScore:F2}

AttentionScore : {attentionScore:F2}

";
        }

        if (riskText != null)
        {
            riskText.text = displayText;
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
