using Microsoft.Azure.Kinect.BodyTracking;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public class HeatMapMake : MonoBehaviour
{
    [Header("JSON")]
    [SerializeField] private string jsonFileName = "skeleton.json";

    [Header("Target Mesh")]
    [SerializeField] private GameObject meshObject;

    [Header("Playback")]
    [SerializeField] private float playbackSpeed = 1.0f;

    [Header("Ray Settings")]
    public float rayDistance = 100f;
    public float rayInterval = 0.05f;

    [Header("Heat")]
    public float paintRadius = 0.3f;
    public float heatPerHit = 0.2f;
    public float heatDecayPerSec = 0.02f;
    public float colorUpdateInterval = 0.2f;
    public float maxHeatDisplay = 5f;

    // ===== internal =====
    private List<FrameData> frames = new();
    private int frameIndex = 0;
    private float playbackTime = 0f;

    private Dictionary<JointId, Vector3> joints = new();

    // heat
    private Mesh targetMesh;
    private Vector3[] verts;
    private Color[] colors;
    private float[] heat;

    private float nextRayT;
    private float nextColorT;

    // =========================
    void Start()
    {
        LoadJSON();
        InitMesh();
    }

    void Update()
    {
        playbackTime += Time.deltaTime * playbackSpeed;

        UpdateFrame(playbackTime);
        ProcessGaze();
    }

    // =========================
    void LoadJSON()
    {
        string path = Path.Combine(Application.dataPath, "Data", jsonFileName);

        if (!File.Exists(path))
        {
            Debug.LogError($"JSON not found: {path}");
            return;
        }

        string json = File.ReadAllText(path);
        FrameList list = JsonUtility.FromJson<FrameList>(json);
        frames = list.frames;

        Debug.Log($"Loaded {frames.Count} frames");
    }

    // =========================
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
    void UpdateFrame(float timeSec)
    {
        if (frameIndex >= frames.Count) return;

        float frameTime = frames[frameIndex].normalizedTimestampTicks * 1e-7f;

        if (timeSec < frameTime) return;

        joints.Clear();

        var f = frames[frameIndex];

        if (f.joints != null)
        {
            foreach (var jp in f.joints)
            {
                if (System.Enum.TryParse(jp.jointId, out JointId id))
                {
                    joints[id] = jp.position;
                }
            }
        }

        frameIndex++;
    }

    // =========================
    void ProcessGaze()
    {
        if (!joints.ContainsKey(JointId.Head) || !joints.ContainsKey(JointId.Nose))
            return;

        Vector3 head = joints[JointId.Head];
        Vector3 nose = joints[JointId.Nose];

        Vector3 dir = (nose - head + new Vector3(0, -0.05f, 0)).normalized;

        // --- Ray ---
        if (Time.time >= nextRayT)
        {
            nextRayT = Time.time + rayInterval;

            Ray ray = new Ray(head, dir);

            Debug.DrawRay(head, dir * rayDistance, Color.red);

            if (Physics.Raycast(ray, out var hit, rayDistance))
            {
                if (hit.collider.transform == meshObject.transform)
                {
                    AddHeat(hit.point);
                }
            }
        }

        // --- Color ---
        if (Time.time >= nextColorT)
        {
            nextColorT = Time.time + colorUpdateInterval;

            DecayHeat();
            ApplyColor();
        }
    }

    // =========================
    void AddHeat(Vector3 hit)
    {
        float r2 = paintRadius * paintRadius;

        for (int i = 0; i < verts.Length; i++)
        {
            Vector3 world = meshObject.transform.TransformPoint(verts[i]);

            if ((world - hit).sqrMagnitude <= r2)
            {
                heat[i] += heatPerHit;
            }
        }
    }

    void DecayHeat()
    {
        float dec = heatDecayPerSec * colorUpdateInterval;

        for (int i = 0; i < heat.Length; i++)
        {
            heat[i] = Mathf.Max(0, heat[i] - dec);
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
}