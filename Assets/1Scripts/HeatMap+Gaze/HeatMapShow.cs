using Microsoft.Azure.Kinect.BodyTracking;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public class HeatMapShow : MonoBehaviour
{
    [Header("JSON")]
    [SerializeField] private string skeletonJson = "skeleton.json";
    [SerializeField] private string lightJson = "light.json";

    [Header("Target Mesh")]
    [SerializeField] private GameObject meshObject;

    [Header("Playback")]
    [SerializeField] private float playbackSpeed = 1.0f;

    [Header("Heat")]
    public float paintRadius = 0.3f;
    public float heatPerHit = 0.2f;
    public float maxHeatDisplay = 5f;

    [Header("Light Filter")]
    public bool showAll = true;   // ← 追加（最優先）
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

    // =========================
    void Start()
    {
        LoadSkeleton();
        LoadLight();
        InitMesh();
    }

    void Update()
    {
        if (finished) return;

        playbackTime += Time.deltaTime * playbackSpeed;

        bool updated = UpdateFrame(playbackTime);

        if (updated)
        {
            ProcessGaze();
            ApplyColor();
        }
    }

    // =========================
    bool UpdateFrame(float timeSec)
    {
        if (frameIndex >= frames.Count)
        {
            finished = true;
            Debug.Log("=== Heatmap generation finished ===");
            return false;
        }

        float frameTime = frames[frameIndex].normalizedTimestampTicks * 1e-7f;

        if (timeSec < frameTime) return false;

        joints.Clear();

        var f = frames[frameIndex];

        if (f.joints != null)
        {
            foreach (var jp in f.joints)
            {
                if (System.Enum.TryParse(jp.jointId, out JointId id))
                    joints[id] = jp.position;
            }
        }

        frameIndex++;
        return true;
    }

    // =========================
    void ProcessGaze()
    {
        if (!joints.ContainsKey(JointId.Head) || !joints.ContainsKey(JointId.Nose))
            return;

        Vector3 head = joints[JointId.Head];
        Vector3 nose = joints[JointId.Nose];

        Vector3 dir = (nose - head + new Vector3(0, -0.05f, 0)).normalized;

        Ray ray = new Ray(head, dir);

        if (Physics.Raycast(ray, out var hit, 100f))
        {
            if (hit.collider.transform != meshObject.transform)
                return;

            LightFrame lf = GetClosestLight(playbackTime);

            if (lf == null) return;

            // 🔥 フィルタ
            if (MatchLightCondition(lf))
            {
                AddHeat(hit.point);
            }
        }
    }

    // =========================
    bool MatchLightCondition(LightFrame lf)
    {
        // 👑 最優先：全部表示
        if (showAll)
            return true;

        // 完全一致フィルタ
        return
            lf.light1 == useLight1 &&
            lf.light2 == useLight2 &&
            lf.light3 == useLight3;
    }

    // =========================
    LightFrame GetClosestLight(float t)
    {
        LightFrame closest = null;
        float minDiff = float.MaxValue;

        foreach (var lf in lightFrames)
        {
            float diff = Mathf.Abs(lf.unityTime - t);

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

    // =========================
    void AddHeat(Vector3 hit)
    {
        float r2 = paintRadius * paintRadius;

        for (int i = 0; i < verts.Length; i++)
        {
            Vector3 w = meshObject.transform.TransformPoint(verts[i]);

            if ((w - hit).sqrMagnitude <= r2)
                heat[i] += heatPerHit;
        }
    }

    void ApplyColor()
    {
        for (int i = 0; i < verts.Length; i++)
        {
            float a = Mathf.Clamp01(heat[i] / maxHeatDisplay);
            colors[i] = new Color(1, 0, 0, a);
        }

        targetMesh.colors = colors;
    }

    // =========================
    void LoadSkeleton()
    {
        string path = Path.Combine(Application.dataPath, "Data", skeletonJson);
        string json = File.ReadAllText(path);

        FrameList list = JsonUtility.FromJson<FrameList>(json);
        frames = list.frames;
    }

    void LoadLight()
    {
        string path = Path.Combine(Application.dataPath, "Data", lightJson);
        string json = File.ReadAllText(path);

        LightFrameList list = JsonUtility.FromJson<LightFrameList>(json);
        lightFrames = list.frames;
    }

    void InitMesh()
    {
        MeshFilter mf = meshObject.GetComponent<MeshFilter>();
        targetMesh = mf.mesh;

        verts = targetMesh.vertices;
        colors = new Color[verts.Length];
        heat = new float[verts.Length];

        targetMesh.colors = colors;

        var col = meshObject.GetComponent<MeshCollider>() ?? meshObject.AddComponent<MeshCollider>();
        col.sharedMesh = targetMesh;
    }

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
}