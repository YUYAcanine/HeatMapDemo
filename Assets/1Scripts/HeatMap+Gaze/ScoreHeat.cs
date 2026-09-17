using Microsoft.Azure.Kinect.BodyTracking;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TMPro;
using UnityEngine;

public class ScoreHeat : MonoBehaviour
{
    [Header("JSON")]
    [SerializeField] private string skeletonJson = "skeleton.json";
    [SerializeField] private string lightJson = "light.json";

    [Header("Score Targets")]
    [SerializeField] private GameObject[] targets;
    [SerializeField] private TMP_Text scoreText;

    [Header("Playback")]
    [SerializeField] private float playbackSpeed = 1.0f;

    [Header("Gaze Angle Correction")]
    [Tooltip("Downward angle correction in degrees")]
    [SerializeField] private float downwardAngle = 35f;

    [Header("Cone Settings")]
    [Tooltip("Cone angle in degrees")]
    [SerializeField] private float coneAngle = 15f;

    [Tooltip("Maximum cone distance")]
    [SerializeField] private float coneDistance = 5f;

    [Tooltip("Apply more heat near the center of the cone")]
    [SerializeField] private bool useCenterWeightedHeat = true;

    [Header("Heat")]
    [SerializeField] private float heatPerHit = 0.2f;

    [Header("Light Filter")]
    [SerializeField] private bool showAll = true;
    [SerializeField] private bool useLight1 = true;
    [SerializeField] private bool useLight2 = false;
    [SerializeField] private bool useLight3 = false;

    [Header("Matching")]
    [SerializeField] private float timeTolerance = 0.1f;

    private readonly List<FrameData> frames = new();
    private readonly List<LightFrame> lightFrames = new();
    private readonly Dictionary<JointId, Vector3> joints = new();
    private readonly List<TargetScore> targetScores = new();

    private int frameIndex;
    private float playbackTime;
    private bool finished;
    private int totalGazeFrames;
    private int noTargetFrames;
    private float totalHeat;

    private void Start()
    {
        LoadSkeleton();
        LoadLight();
        RegisterTargets();
        UpdateScoreText();
    }

    private void Update()
    {
        if (finished)
            return;

        playbackTime += Time.deltaTime * playbackSpeed;

        if (UpdateFrame(playbackTime))
        {
            ProcessConeGaze();
            UpdateScoreText();
        }
    }

    private bool UpdateFrame(float timeSec)
    {
        if (frameIndex >= frames.Count)
        {
            finished = true;
            UpdateScoreText();
            Debug.Log("=== Heat score calculation finished ===");
            return false;
        }

        float frameTime =
            frames[frameIndex].normalizedTimestampTicks * 1e-7f;

        if (timeSec < frameTime)
            return false;

        joints.Clear();

        FrameData frame = frames[frameIndex];
        if (frame.joints != null)
        {
            foreach (JointPosition joint in frame.joints)
            {
                if (System.Enum.TryParse(joint.jointId, out JointId id))
                    joints[id] = joint.position;
            }
        }

        frameIndex++;
        return true;
    }

    private void ProcessConeGaze()
    {
        if (!joints.TryGetValue(JointId.Head, out Vector3 head) ||
            !joints.TryGetValue(JointId.Nose, out Vector3 nose))
        {
            return;
        }

        Vector3 rawDir = nose - head;
        if (rawDir.sqrMagnitude < 0.0001f)
            return;

        rawDir.Normalize();

        Vector3 rightAxis =
            Vector3.Cross(Vector3.up, rawDir).normalized;

        if (rightAxis.sqrMagnitude < 0.0001f)
            rightAxis = Vector3.right;

        Quaternion correction =
            Quaternion.AngleAxis(downwardAngle, rightAxis);

        Vector3 direction =
            (correction * rawDir).normalized;

        Debug.DrawRay(
            head,
            direction * coneDistance,
            Color.red
        );

        if (!showAll)
        {
            LightFrame lightFrame = GetClosestLight(playbackTime);
            if (lightFrame == null || !MatchLightCondition(lightFrame))
                return;
        }

        bool hitAnyTarget =
            AddConeScores(head, direction);

        totalGazeFrames++;

        if (!hitAnyTarget)
            noTargetFrames++;
    }

    private bool AddConeScores(Vector3 origin, Vector3 direction)
    {
        float maxAngleRad =
            Mathf.Max(coneAngle, 0.0001f) * Mathf.Deg2Rad;

        float cosThreshold =
            Mathf.Cos(maxAngleRad);

        bool hitAnyTarget = false;

        foreach (TargetScore targetScore in targetScores)
        {
            if (targetScore.Target == null || targetScore.Vertices == null)
                continue;

            Transform targetTransform = targetScore.Target.transform;
            bool hitThisTarget = false;

            foreach (Vector3 vertex in targetScore.Vertices)
            {
                Vector3 worldPosition =
                    targetTransform.TransformPoint(vertex);

                Vector3 toVertex =
                    worldPosition - origin;

                float distance =
                    toVertex.magnitude;

                if (distance <= 0f || distance > coneDistance)
                    continue;

                float dot =
                    Vector3.Dot(direction, toVertex / distance);

                if (dot < cosThreshold)
                    continue;

                float weight = 1f;

                if (useCenterWeightedHeat)
                {
                    float angle =
                        Mathf.Acos(Mathf.Clamp(dot, -1f, 1f));

                    float normalized =
                        angle / maxAngleRad;

                    weight = 1f - normalized;
                    weight *= weight;
                }

                float addedHeat =
                    heatPerHit * weight;

                targetScore.TotalHeat += addedHeat;
                totalHeat += addedHeat;
                hitThisTarget = true;
            }

            if (hitThisTarget)
            {
                targetScore.HitFrames++;
                hitAnyTarget = true;
            }
        }

        return hitAnyTarget;
    }

    private void RegisterTargets()
    {
        targetScores.Clear();

        if (targets == null)
            return;

        foreach (GameObject target in targets)
        {
            if (target == null)
                continue;

            MeshFilter meshFilter = target.GetComponent<MeshFilter>();
            if (meshFilter == null || meshFilter.sharedMesh == null)
            {
                Debug.LogWarning(
                    $"Score target has no MeshFilter: {target.name}",
                    target
                );
                continue;
            }

            targetScores.Add(
                new TargetScore(
                    target,
                    meshFilter.sharedMesh.vertices
                )
            );
        }
    }

    private bool MatchLightCondition(LightFrame lightFrame)
    {
        return
            lightFrame.light1 == useLight1 &&
            lightFrame.light2 == useLight2 &&
            lightFrame.light3 == useLight3;
    }

    private LightFrame GetClosestLight(float time)
    {
        LightFrame closest = null;
        float minDifference = float.MaxValue;

        foreach (LightFrame lightFrame in lightFrames)
        {
            float difference =
                Mathf.Abs(lightFrame.unityTime - time);

            if (difference < minDifference)
            {
                minDifference = difference;
                closest = lightFrame;
            }
        }

        return minDifference <= timeTolerance ? closest : null;
    }

    private void UpdateScoreText()
    {
        if (scoreText == null)
            return;

        StringBuilder builder = new StringBuilder();

        foreach (TargetScore targetScore in targetScores)
        {
            if (targetScore.Target == null)
                continue;

            builder.Append(targetScore.Target.name);
            builder.AppendLine();
            builder.Append("  Heat : ");
            builder.AppendLine(targetScore.TotalHeat.ToString("F2"));
            builder.Append("  Hit Frames : ");
            builder.AppendLine(targetScore.HitFrames.ToString());
            builder.AppendLine();
        }

        builder.AppendLine("All Gaze");
        builder.Append("  Total Frames : ");
        builder.AppendLine(totalGazeFrames.ToString());
        builder.Append("  Any Target Frames : ");
        builder.AppendLine(
            (totalGazeFrames - noTargetFrames).ToString()
        );
        builder.Append("  No Target Frames : ");
        builder.AppendLine(noTargetFrames.ToString());
        builder.Append("  Total Heat : ");
        builder.AppendLine(totalHeat.ToString("F2"));

        scoreText.text = builder.ToString();
    }

    public float GetTotalHeat(GameObject target)
    {
        foreach (TargetScore targetScore in targetScores)
        {
            if (targetScore.Target == target)
                return targetScore.TotalHeat;
        }

        return 0f;
    }

    public int GetHitFrames(GameObject target)
    {
        foreach (TargetScore targetScore in targetScores)
        {
            if (targetScore.Target == target)
                return targetScore.HitFrames;
        }

        return 0;
    }

    public void ResetScores()
    {
        foreach (TargetScore targetScore in targetScores)
        {
            targetScore.TotalHeat = 0f;
            targetScore.HitFrames = 0;
        }

        totalGazeFrames = 0;
        noTargetFrames = 0;
        totalHeat = 0f;

        UpdateScoreText();
    }

    private void LoadSkeleton()
    {
        string path =
            Path.Combine(Application.dataPath, "Data", skeletonJson);

        if (!File.Exists(path))
        {
            Debug.LogError($"Skeleton JSON not found: {path}");
            return;
        }

        string json = File.ReadAllText(path);
        FrameList list = JsonUtility.FromJson<FrameList>(json);

        frames.Clear();
        if (list != null && list.frames != null)
            frames.AddRange(list.frames);
    }

    private void LoadLight()
    {
        lightFrames.Clear();

        if (showAll)
            return;

        string path =
            Path.Combine(Application.dataPath, "Data", lightJson);

        if (!File.Exists(path))
        {
            Debug.LogError($"Light JSON not found: {path}");
            return;
        }

        string json = File.ReadAllText(path);
        LightFrameList list = JsonUtility.FromJson<LightFrameList>(json);

        if (list != null && list.frames != null)
            lightFrames.AddRange(list.frames);
    }

    [System.Serializable]
    private class FrameList
    {
        public List<FrameData> frames;
    }

    [System.Serializable]
    private class FrameData
    {
        public long normalizedTimestampTicks;
        public List<JointPosition> joints;
    }

    [System.Serializable]
    private class JointPosition
    {
        public string jointId;
        public Vector3 position;
    }

    [System.Serializable]
    private class LightFrameList
    {
        public List<LightFrame> frames;
    }

    [System.Serializable]
    private class LightFrame
    {
        public float unityTime;
        public bool light1;
        public bool light2;
        public bool light3;
    }

    private class TargetScore
    {
        public GameObject Target { get; }
        public Vector3[] Vertices { get; }
        public float TotalHeat { get; set; }
        public int HitFrames { get; set; }

        public TargetScore(
            GameObject target,
            Vector3[] vertices
        )
        {
            Target = target;
            Vertices = vertices;
        }
    }
}
