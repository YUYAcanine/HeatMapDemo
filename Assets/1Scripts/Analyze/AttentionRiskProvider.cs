using Microsoft.Azure.Kinect.BodyTracking;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class AttentionRiskProvider : MonoBehaviour
{
    [Header("JSON")]
    [SerializeField] private string jsonFileName = "skeleton.json";

    [Header("Skeleton Visual")]
    [SerializeField] private GameObject jointPrefab;
    [SerializeField] private Material lineMaterial;
    [SerializeField] private bool showSkeleton = true;

    [Header("Playback")]
    [SerializeField] private float playbackSpeed = 1.0f; // 1.0 = real time

    [Header("UI")]
    [SerializeField] private Button playButton;
    [SerializeField] private Button stopButton;

    [Header("Attention Targets")]
    [SerializeField] private Transform[] factors;
    [SerializeField] private TMP_Text riskText;

    [Header("Attention Settings")]
    [SerializeField] private float historySeconds = 5f;
    [SerializeField] private float attentionAngle = 20f;
    [SerializeField] private float downwardAngle = 15f;
    [SerializeField] private bool drawDebug = true;

    private readonly List<FrameData> frames = new();
    private readonly Dictionary<JointId, GameObject> jointObjects = new();
    private readonly List<LineData> lines = new();

    private float playbackTime = 0f;
    private bool isPlaying = false;
    private int frameIndex = 0;
    private FrameData lastValidFrame = null;

    private readonly Dictionary<Transform, Queue<float>> gazeHistories = new();
    private readonly Dictionary<Transform, float> gazeSums = new();
    private readonly List<AttentionTargetScore> rankedScores = new();

    public bool HasPelvisPosition { get; private set; }
    public Vector3 PelvisPosition { get; private set; }
    public bool HasCurrentFrame { get; private set; }
    public int CurrentFrameIndex { get; private set; } = -1;

    private void Start()
    {
        RegisterFactors();
        LoadFrames();
        CreateJointObjects();
        CreateLines();
        SetupUI();
    }

    private void Update()
    {
        if (!isPlaying)
            return;

        playbackTime += Time.deltaTime * playbackSpeed;
        UpdateSkeletonByTime(playbackTime);
    }

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
        CurrentFrameIndex = -1;
        lastValidFrame = null;
        ResetAttention();
        isPlaying = true;
        HideSkeleton();
        Debug.Log("Playback started");
    }

    public void Stop()
    {
        isPlaying = false;
        CurrentFrameIndex = -1;
        lastValidFrame = null;
        ResetAttention();
        HideSkeleton();
        Debug.Log("Playback stopped");
    }

    public void ResetAttention()
    {
        foreach (var history in gazeHistories.Values)
            history.Clear();

        foreach (var factor in gazeHistories.Keys.ToList())
            gazeSums[factor] = 0f;

        rankedScores.Clear();
        HasPelvisPosition = false;
        HasCurrentFrame = false;
        UpdateRiskText("");
    }

    public bool TryGetTargetByRank(
        int interestRank,
        out Transform target,
        out float attentionScore
    )
    {
        target = null;
        attentionScore = 0f;

        if (interestRank < 1)
            interestRank = 1;

        int index = interestRank - 1;
        if (index < 0 || index >= rankedScores.Count)
            return false;

        AttentionTargetScore score = rankedScores[index];
        if (score.Score <= 0f || score.Target == null)
            return false;

        target = score.Target;
        attentionScore = score.Score;
        return true;
    }

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

        frames.Clear();
        if (frameList != null && frameList.frames != null)
            frames.AddRange(frameList.frames);

        Debug.Log($"Loaded frames: {frames.Count}");
    }

    private void CreateJointObjects()
    {
        if (frames.Count == 0)
            return;

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
            ResetAttention();
            HideSkeleton();
        }
        else
        {
            ApplyFrame(current);
            lastValidFrame = current;
            CurrentFrameIndex = frameIndex;
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
                obj.SetActive(showSkeleton);
            }
        }

        foreach (var line in lines)
        {
            bool valid = true;

            for (int i = 0; i < line.joints.Length; i++)
            {
                if (jointObjects.TryGetValue(line.joints[i], out GameObject obj))
                {
                    line.line.SetPosition(i, obj.transform.position);
                }
                else
                {
                    valid = false;
                    break;
                }
            }

            line.line.enabled = showSkeleton && valid;
        }

        UpdateAttentionFromCurrentSkeleton();
    }

    private void HideSkeleton()
    {
        foreach (var obj in jointObjects.Values)
            obj.SetActive(false);

        foreach (var line in lines)
            line.line.enabled = false;
    }

    private void UpdateAttentionFromCurrentSkeleton()
    {
        if (!jointObjects.ContainsKey(JointId.Pelvis) ||
            !jointObjects.ContainsKey(JointId.Head) ||
            !jointObjects.ContainsKey(JointId.Nose))
        {
            ResetAttention();
            return;
        }

        Vector3 pelvis =
            jointObjects[JointId.Pelvis]
            .transform.position;

        Vector3 head =
            jointObjects[JointId.Head]
            .transform.position;

        Vector3 nose =
            jointObjects[JointId.Nose]
            .transform.position;

        PelvisPosition = pelvis;
        HasPelvisPosition = true;
        HasCurrentFrame = true;

        RegisterFactors();
        rankedScores.Clear();

        if (factors == null || factors.Length == 0)
        {
            UpdateRiskText("");
            return;
        }

        Vector3 rawDir =
            (nose - head).normalized;

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
                downwardAngle,
                rightAxis
            );

        Vector3 dir =
            (correction * rawDir)
            .normalized;

        string displayText = "";

        for (int i = 0; i < factors.Length; i++)
        {
            Transform factor = factors[i];

            if (factor == null)
                continue;

            EnsureFactor(factor);

            Vector3 toFactor =
                (
                    factor.position
                    - head
                ).normalized;

            float angle =
                Vector3.Angle(
                    dir,
                    toFactor
                );

            bool lookingAtFactor =
                angle < attentionAngle;

            float sample =
                lookingAtFactor ? 1f : 0f;

            gazeHistories[factor].Enqueue(sample);
            gazeSums[factor] += sample;

            int maxSamples =
                Mathf.RoundToInt(
                    historySeconds /
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

            rankedScores.Add(
                new AttentionTargetScore(
                    factor,
                    attentionScore
                )
            );

            if (drawDebug)
            {
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
            }

            displayText +=
                $@"Factor{i + 1}

AttentionScore : {attentionScore:F2}

";
        }

        rankedScores.Sort(
            (a, b) => b.Score.CompareTo(a.Score)
        );

        UpdateRiskText(displayText);
    }

    private void RegisterFactors()
    {
        if (factors == null)
            return;

        foreach (Transform factor in factors)
        {
            if (factor == null)
                continue;

            EnsureFactor(factor);
        }
    }

    private void EnsureFactor(Transform factor)
    {
        if (gazeHistories.ContainsKey(factor))
            return;

        gazeHistories[factor] = new Queue<float>();
        gazeSums[factor] = 0f;
    }

    private void UpdateRiskText(string displayText)
    {
        if (riskText != null)
        {
            riskText.text = displayText;
        }
    }

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

    private class AttentionTargetScore
    {
        public Transform Target { get; }
        public float Score { get; }

        public AttentionTargetScore(
            Transform target,
            float score
        )
        {
            Target = target;
            Score = score;
        }
    }
}
