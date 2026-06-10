using Microsoft.Azure.Kinect.BodyTracking;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public class ConeHeatMapShow : MonoBehaviour
{
    [Header("JSON")]
    [SerializeField] private string skeletonJson = "skeleton.json";
    [SerializeField] private string lightJson = "light.json";

    [Header("Target Mesh")]
    [SerializeField] private GameObject meshObject;

    [Header("Playback")]
    [SerializeField] private float playbackSpeed = 1.0f;

    [Header("Gaze Angle Correction")]
    [Tooltip("Downward angle correction in degrees")]
    public float downwardAngle = 35f;

    [Header("Cone Settings")]
    [Tooltip("Cone angle in degrees")]
    public float coneAngle = 15f;

    [Tooltip("Maximum cone distance")]
    public float coneDistance = 5f;

    [Header("Heat")]
    public float heatPerHit = 0.2f;
    public float maxHeatDisplay = 5f;

    [Header("Light Filter")]
    public bool showAll = true;
    public bool useLight1 = true;
    public bool useLight2 = false;
    public bool useLight3 = false;

    [Header("Matching")]
    public float timeTolerance = 0.1f;

    // ===== data =====
    private List<FrameData> frames = new();
    private List<LightFrame> lightFrames = new();

    private int frameIndex = 0;
    private float playbackTime = 0f;

    private Dictionary<JointId, Vector3> joints = new();

    // ===== heat =====
    private float[] heat;
    private Mesh targetMesh;
    private Vector3[] verts;
    private Color[] colors;

    private bool finished = false;

    // =========================================================
    void Start()
    {
        LoadSkeleton();
        LoadLight();
        InitMesh();
    }

    // =========================================================
    void Update()
    {
        if (finished)
            return;

        playbackTime +=
            Time.deltaTime * playbackSpeed;

        bool updated =
            UpdateFrame(playbackTime);

        if (updated)
        {
            ProcessConeGaze();

            ApplyColor();
        }
    }

    // =========================================================
    bool UpdateFrame(float timeSec)
    {
        if (frameIndex >= frames.Count)
        {
            finished = true;

            Debug.Log(
                "=== Heatmap generation finished ==="
            );

            return false;
        }

        float frameTime =
            frames[frameIndex]
            .normalizedTimestampTicks
            * 1e-7f;

        if (timeSec < frameTime)
            return false;

        joints.Clear();

        var f = frames[frameIndex];

        if (f.joints != null)
        {
            foreach (var jp in f.joints)
            {
                if (
                    System.Enum.TryParse(
                        jp.jointId,
                        out JointId id
                    )
                )
                {
                    joints[id] = jp.position;
                }
            }
        }

        frameIndex++;

        return true;
    }

    // =========================================================
    void ProcessConeGaze()
    {
        if (
            !joints.ContainsKey(JointId.Head)
            || !joints.ContainsKey(JointId.Nose)
        )
        {
            return;
        }

        Vector3 head =
            joints[JointId.Head];

        Vector3 nose =
            joints[JointId.Nose];

        // =========================
        // Raw Direction
        // =========================

        Vector3 rawDir =
            (nose - head).normalized;

        // =========================
        // Face-local downward correction
        // =========================

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
            (correction * rawDir).normalized;

        // =========================

        Debug.DrawRay(
            head,
            dir * coneDistance,
            Color.red
        );

        // =====================================================
        // showAll の場合は
        // Light同期を完全スキップ
        // =====================================================

        if (showAll)
        {
            AddConeHeat(head, dir);
            return;
        }

        // =====================================================
        // Light filtering
        // =====================================================

        LightFrame lf =
            GetClosestLight(
                playbackTime
            );

        if (lf == null)
            return;

        if (!MatchLightCondition(lf))
            return;

        AddConeHeat(head, dir);
    }

    // =========================================================
    void AddConeHeat(
        Vector3 origin,
        Vector3 dir
    )
    {
        float maxAngleRad =
            coneAngle * Mathf.Deg2Rad;

        float cosThreshold =
            Mathf.Cos(maxAngleRad);

        for (
            int i = 0;
            i < verts.Length;
            i++
        )
        {
            Vector3 worldPos =
                meshObject.transform
                .TransformPoint(
                    verts[i]
                );

            Vector3 toVertex =
                worldPos - origin;

            float distance =
                toVertex.magnitude;

            // 距離制限
            if (
                distance > coneDistance
            )
                continue;

            Vector3 toVertexDir =
                toVertex.normalized;

            float dot =
                Vector3.Dot(
                    dir,
                    toVertexDir
                );

            // 円錐外
            if (dot < cosThreshold)
                continue;

            // =========================
            // 角度計算
            // =========================

            float angle =
                Mathf.Acos(
                    Mathf.Clamp(dot, -1f, 1f)
                );

            // 0=center
            // 1=edge
            float normalized =
                angle / maxAngleRad;

            // =========================
            // 中央強調ウェイト
            // =========================

            float weight =
                1f - normalized;

            // 中央を強く
            weight *= weight;

            // =========================

            heat[i] +=
                heatPerHit * weight;
        }
    }

    // =========================================================
    bool MatchLightCondition(
        LightFrame lf
    )
    {
        if (showAll)
            return true;

        return
            lf.light1 == useLight1
            &&
            lf.light2 == useLight2
            &&
            lf.light3 == useLight3;
    }

    // =========================================================
    LightFrame GetClosestLight(float t)
    {
        LightFrame closest = null;

        float minDiff =
            float.MaxValue;

        foreach (var lf in lightFrames)
        {
            float diff =
                Mathf.Abs(
                    lf.unityTime - t
                );

            if (diff < minDiff)
            {
                minDiff = diff;
                closest = lf;
            }
        }

        if (minDiff > timeTolerance)
            return null;

        return closest;
    }

    // =========================================================
    void ApplyColor()
    {
        for (
            int i = 0;
            i < verts.Length;
            i++
        )
        {
            float a =
                Mathf.Clamp01(
                    heat[i]
                    / maxHeatDisplay
                );

            colors[i] =
                new Color(
                    1,
                    0,
                    0,
                    a
                );
        }

        targetMesh.colors = colors;
    }

    // =========================================================
    void LoadSkeleton()
    {
        string path =
            Path.Combine(
                Application.dataPath,
                "Data",
                skeletonJson
            );

        string json =
            File.ReadAllText(path);

        FrameList list =
            JsonUtility.FromJson<FrameList>(
                json
            );

        frames = list.frames;
    }

    // =========================================================
    void LoadLight()
    {
        string path =
            Path.Combine(
                Application.dataPath,
                "Data",
                lightJson
            );

        string json =
            File.ReadAllText(path);

        LightFrameList list =
            JsonUtility.FromJson<LightFrameList>(
                json
            );

        lightFrames = list.frames;
    }

    // =========================================================
    void InitMesh()
    {
        MeshFilter mf =
            meshObject.GetComponent<MeshFilter>();

        targetMesh = mf.mesh;

        verts =
            targetMesh.vertices;

        colors =
            new Color[verts.Length];

        heat =
            new float[verts.Length];

        targetMesh.colors = colors;
    }

    // =========================================================
    [System.Serializable]
    private class FrameList
    {
        public List<FrameData> frames;
    }

    // =========================================================
    [System.Serializable]
    private class FrameData
    {
        public long normalizedTimestampTicks;

        public List<JointPosition> joints;
    }

    // =========================================================
    [System.Serializable]
    private class JointPosition
    {
        public string jointId;

        public Vector3 position;
    }

    // =========================================================
    [System.Serializable]
    private class LightFrameList
    {
        public List<LightFrame> frames;
    }

    // =========================================================
    [System.Serializable]
    private class LightFrame
    {
        public float unityTime;

        public bool light1;
        public bool light2;
        public bool light3;
    }
// =========================================================
    public float[] GetHeatData()
    {
        return heat;
    }

    // =========================================================
    public string GetMeshName()
    {
        if (meshObject == null)
            return "UnknownMesh";

        return meshObject.name;
    }
}