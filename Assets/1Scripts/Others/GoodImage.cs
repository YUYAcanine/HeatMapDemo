using Microsoft.Azure.Kinect.BodyTracking;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class GoodImage : MonoBehaviour
{
    [Header("JSON")]
    [SerializeField] private string skeletonJson = "skeleton.json";
    [SerializeField] private string lightJson = "light.json";

    [Header("Target Mesh")]
    [SerializeField] private GameObject meshObject;

    [Header("Skeleton Visual")]
    [SerializeField] private GameObject jointPrefab;
    [SerializeField] private Material lineMaterial;
    [SerializeField] private bool showSkeleton = true;
    [SerializeField] private float jointScale = 0.06f;
    [SerializeField] private float lineWidth = 0.02f;

    [Header("Light Objects")]
    [SerializeField] private Renderer light1Object;
    [SerializeField] private Renderer light2Object;
    [SerializeField] private Renderer light3Object;

    [Header("Light Colors")]
    [SerializeField] private Color light1OnColor = Color.red;
    [SerializeField] private Color light1OffColor = Color.gray;
    [SerializeField] private Color light2OnColor = Color.green;
    [SerializeField] private Color light2OffColor = Color.gray;
    [SerializeField] private Color light3OnColor = Color.blue;
    [SerializeField] private Color light3OffColor = Color.gray;

    [Header("Playback")]
    [SerializeField] private float playbackSpeed = 1.0f;
    [SerializeField] private bool playOnStart = true;

    [Header("UI")]
    [SerializeField] private Button playButton;
    [SerializeField] private Button stopButton;
    [SerializeField] private bool showFrameNumber = true;
    [SerializeField] private TMP_Text frameText;

    [Header("Gaze Angle Correction")]
    [Tooltip("Downward angle correction in degrees")]
    public float downwardAngle = 15f;

    [Header("Cone Settings")]
    [Tooltip("Cone angle in degrees")]
    public float coneAngle = 15f;

    [Tooltip("Maximum cone distance")]
    public float coneDistance = 5f;

    [Tooltip("Apply more heat near the center of the cone")]
    public bool useCenterWeightedHeat = true;

    [Header("Vector Visuals")]
    [SerializeField] private Material vectorLineMaterial;
    [SerializeField] private float vectorLineWidth = 0.02f;
    [SerializeField] private Color gazeVectorColor = Color.red;
    [SerializeField] private Color velocityVectorColor = Color.cyan;
    [SerializeField] private float velocityVectorScale = 0.25f;
    [SerializeField] private float maxVelocityVectorLength = 2f;
    [SerializeField] private float velocityLookAheadSeconds = 0.2f;
    [Range(0.01f, 1f)]
    [SerializeField] private float velocitySmoothing = 0.2f;
    [SerializeField] private float minVelocity = 0.05f;
    [SerializeField] private float velocityArrowHeadLength = 0.15f;
    [Range(1f, 89f)]
    [SerializeField] private float velocityArrowHeadAngle = 25f;

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
    private int lightFrameIndex = 0;
    private float playbackTime = 0f;

    private Dictionary<JointId, Vector3> joints = new();
    private Dictionary<JointId, GameObject> jointObjects = new();
    private List<LineData> lines = new();
    private LineRenderer gazeVectorLine;
    private LineRenderer velocityVectorLine;
    private LineRenderer velocityArrowHeadLeft;
    private LineRenderer velocityArrowHeadRight;
    private Vector3 smoothedVelocity;
    private bool hasSmoothedVelocity;

    // ===== heat =====
    private float[] heat;
    private Mesh targetMesh;
    private Vector3[] verts;
    private Color[] colors;

    private bool finished = false;
    private bool isPlaying = false;

    // =========================================================
    void Start()
    {
        LoadSkeleton();
        LoadLight();
        InitMesh();
        CreateJointObjects();
        CreateLines();
        CreateVectorLines();
        SetupUI();
        ResetLights();
        HideSkeleton();
        UpdateFrameText();

        if (playOnStart)
            Play();
    }

    // =========================================================
    void Update()
    {
        UpdateFrameText();

        if (!isPlaying || finished)
            return;

        playbackTime +=
            Time.deltaTime * playbackSpeed;

        bool updated =
            UpdateFrame(playbackTime);

        if (updated)
        {
            ApplySkeleton();
            UpdateVelocityVector();
            ProcessGaze();
            ApplyColor();
        }

        UpdateLight(playbackTime);
    }

    // =========================================================
    bool UpdateFrame(float timeSec)
    {
        if (frameIndex >= frames.Count)
        {
            finished = true;
            isPlaying = false;

            Debug.Log(
                "=== GoodImage playback finished ==="
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

    public void Play()
    {
        playbackTime = 0f;
        frameIndex = 0;
        lightFrameIndex = 0;
        finished = false;
        isPlaying = true;
        smoothedVelocity = Vector3.zero;
        hasSmoothedVelocity = false;

        ClearHeat();
        HideSkeleton();
        HideVectorLines();
        ResetLights();
        UpdateFrameText();
    }

    public void Stop()
    {
        isPlaying = false;
        smoothedVelocity = Vector3.zero;
        hasSmoothedVelocity = false;
        HideSkeleton();
        HideVectorLines();
        ResetLights();
        UpdateFrameText();
    }

    void SetupUI()
    {
        if (playButton != null)
            playButton.onClick.AddListener(Play);

        if (stopButton != null)
            stopButton.onClick.AddListener(Stop);
    }

    // =========================================================
    void ProcessGaze()
    {
        if (
            !joints.ContainsKey(JointId.Head)
            || !joints.ContainsKey(JointId.Nose)
        )
        {
            SetLineVisible(gazeVectorLine, false);
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
            nose - head;

        if (rawDir.sqrMagnitude < 0.0001f)
        {
            SetLineVisible(gazeVectorLine, false);
            return;
        }

        rawDir.Normalize();

        // =========================
        // Face-local downward correction
        // =========================

        Vector3 rightAxis =
            Vector3.Cross(
                Vector3.up,
                rawDir
            ).normalized;

        // 真上・真下向き対策
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

        UpdateVisualLine(
            gazeVectorLine,
            head,
            head + dir * coneDistance
        );

        if (!showAll)
        {
            LightFrame lightFrame =
                GetClosestLight(playbackTime);

            if (
                lightFrame == null ||
                !MatchLightCondition(lightFrame)
            )
            {
                return;
            }
        }

        AddConeHeat(head, dir);
    }

    // =========================================================
    bool MatchLightCondition(
        LightFrame lf
    )
    {
        // 最優先
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
    void AddConeHeat(
        Vector3 origin,
        Vector3 direction
    )
    {
        float maxAngleRad =
            Mathf.Max(coneAngle, 0.0001f) *
            Mathf.Deg2Rad;

        float cosThreshold =
            Mathf.Cos(maxAngleRad);

        for (
            int i = 0;
            i < verts.Length;
            i++
        )
        {
            Vector3 w =
                meshObject.transform
                .TransformPoint(
                    verts[i]
                );

            Vector3 toVertex =
                w - origin;

            float distance =
                toVertex.magnitude;

            if (
                distance <= 0f ||
                distance > coneDistance
            )
            {
                continue;
            }

            float dot =
                Vector3.Dot(
                    direction,
                    toVertex / distance
                );

            if (dot < cosThreshold)
                continue;

            float weight = 1f;

            if (useCenterWeightedHeat)
            {
                float angle =
                    Mathf.Acos(
                        Mathf.Clamp(dot, -1f, 1f)
                    );

                float normalized =
                    angle / maxAngleRad;

                weight = 1f - normalized;
                weight *= weight;
            }

            heat[i] +=
                heatPerHit * weight;
        }
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

    void ClearHeat()
    {
        if (heat == null || colors == null || targetMesh == null)
            return;

        System.Array.Clear(heat, 0, heat.Length);
        System.Array.Clear(colors, 0, colors.Length);
        targetMesh.colors = colors;
    }

    void CreateJointObjects()
    {
        if (jointPrefab == null || frames.Count == 0)
            return;

        FrameData firstValid =
            frames.Find(
                frame =>
                    frame.joints != null &&
                    frame.joints.Count > 0
            );

        if (firstValid == null)
            return;

        foreach (JointPosition joint in firstValid.joints)
        {
            if (!System.Enum.TryParse(joint.jointId, out JointId id))
                continue;

            GameObject jointObject =
                Instantiate(
                    jointPrefab,
                    Vector3.zero,
                    Quaternion.identity,
                    transform
                );

            jointObject.name = id.ToString();
            jointObject.transform.localScale =
                Vector3.one * jointScale;

            jointObjects[id] = jointObject;
        }
    }

    void CreateLines()
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

    void CreateVectorLines()
    {
        gazeVectorLine =
            CreateVectorLine(
                "GazeVector",
                gazeVectorColor
            );

        velocityVectorLine =
            CreateVectorLine(
                "VelocityVector",
                velocityVectorColor
            );

        velocityArrowHeadLeft =
            CreateVectorLine(
                "VelocityArrowHeadLeft",
                velocityVectorColor
            );

        velocityArrowHeadRight =
            CreateVectorLine(
                "VelocityArrowHeadRight",
                velocityVectorColor
            );
    }

    LineRenderer CreateVectorLine(
        string lineName,
        Color color
    )
    {
        GameObject lineObject =
            new GameObject(lineName);

        lineObject.transform.SetParent(transform);

        LineRenderer line =
            lineObject.AddComponent<LineRenderer>();

        Material material = vectorLineMaterial;

        if (material == null)
        {
            Shader shader =
                Shader.Find("Sprites/Default");

            if (shader != null)
                material = new Material(shader);
        }

        if (material != null)
            line.material = material;

        line.startWidth = vectorLineWidth;
        line.endWidth = vectorLineWidth;
        line.positionCount = 2;
        line.startColor = color;
        line.endColor = color;
        line.enabled = false;

        return line;
    }

    void UpdateVelocityVector()
    {
        if (
            velocityVectorLine == null ||
            !joints.TryGetValue(
                JointId.Pelvis,
                out Vector3 currentPelvis
            )
        )
        {
            SetVelocityArrowVisible(false);
            return;
        }

        int currentFrameIndex =
            frameIndex - 1;

        if (
            currentFrameIndex < 0 ||
            !TryGetFuturePelvis(
                currentFrameIndex,
                out Vector3 nextPelvis,
                out float deltaTime
            )
        )
        {
            SetVelocityArrowVisible(false);
            return;
        }

        Vector3 velocity =
            (nextPelvis - currentPelvis) /
            deltaTime;

        if (velocity.magnitude < minVelocity)
        {
            smoothedVelocity = Vector3.zero;
            hasSmoothedVelocity = false;
            SetVelocityArrowVisible(false);
            return;
        }

        if (!hasSmoothedVelocity)
        {
            smoothedVelocity = velocity;
            hasSmoothedVelocity = true;
        }
        else
        {
            smoothedVelocity =
                Vector3.Lerp(
                    smoothedVelocity,
                    velocity,
                    velocitySmoothing
                );
        }

        Vector3 displayedVector =
            smoothedVelocity *
            velocityVectorScale;

        if (
            maxVelocityVectorLength > 0f &&
            displayedVector.magnitude >
            maxVelocityVectorLength
        )
        {
            displayedVector =
                displayedVector.normalized *
                maxVelocityVectorLength;
        }

        UpdateVelocityArrow(
            currentPelvis,
            displayedVector
        );
    }

    void UpdateVelocityArrow(
        Vector3 start,
        Vector3 displayedVector
    )
    {
        if (displayedVector.sqrMagnitude < 0.000001f)
        {
            SetVelocityArrowVisible(false);
            return;
        }

        Vector3 end =
            start + displayedVector;

        Vector3 direction =
            displayedVector.normalized;

        Vector3 sideAxis =
            Vector3.Cross(direction, Vector3.up);

        if (sideAxis.sqrMagnitude < 0.0001f)
            sideAxis = Vector3.Cross(direction, Vector3.forward);

        sideAxis.Normalize();

        Vector3 backward =
            -direction;

        float angleRad =
            velocityArrowHeadAngle *
            Mathf.Deg2Rad;

        Vector3 leftDirection =
            (
                backward * Mathf.Cos(angleRad) +
                sideAxis * Mathf.Sin(angleRad)
            ).normalized;

        Vector3 rightDirection =
            (
                backward * Mathf.Cos(angleRad) -
                sideAxis * Mathf.Sin(angleRad)
            ).normalized;

        UpdateVisualLine(
            velocityVectorLine,
            start,
            end
        );

        UpdateVisualLine(
            velocityArrowHeadLeft,
            end,
            end +
            leftDirection *
            velocityArrowHeadLength
        );

        UpdateVisualLine(
            velocityArrowHeadRight,
            end,
            end +
            rightDirection *
            velocityArrowHeadLength
        );
    }

    void SetVelocityArrowVisible(bool visible)
    {
        SetLineVisible(velocityVectorLine, visible);
        SetLineVisible(velocityArrowHeadLeft, visible);
        SetLineVisible(velocityArrowHeadRight, visible);
    }

    bool TryGetFuturePelvis(
        int currentFrameIndex,
        out Vector3 nextPelvis,
        out float deltaTime
    )
    {
        nextPelvis = Vector3.zero;
        deltaTime = 0f;

        float currentTime =
            frames[currentFrameIndex]
            .normalizedTimestampTicks *
            1e-7f;

        Vector3 fallbackPelvis = Vector3.zero;
        float fallbackDeltaTime = 0f;
        bool hasFallback = false;

        for (
            int i = currentFrameIndex + 1;
            i < frames.Count;
            i++
        )
        {
            FrameData frame = frames[i];

            if (frame.joints == null)
                continue;

            foreach (JointPosition joint in frame.joints)
            {
                if (
                    joint.jointId !=
                    JointId.Pelvis.ToString()
                )
                {
                    continue;
                }

                float nextTime =
                    frame.normalizedTimestampTicks *
                    1e-7f;

                deltaTime =
                    nextTime - currentTime;

                if (deltaTime <= 0f)
                    return false;

                fallbackPelvis = joint.position;
                fallbackDeltaTime = deltaTime;
                hasFallback = true;

                if (
                    deltaTime >=
                    Mathf.Max(
                        velocityLookAheadSeconds,
                        0f
                    )
                )
                {
                    nextPelvis = joint.position;
                    return true;
                }

                break;
            }
        }

        if (!hasFallback)
            return false;

        nextPelvis = fallbackPelvis;
        deltaTime = fallbackDeltaTime;
        return true;
    }

    void UpdateVisualLine(
        LineRenderer line,
        Vector3 start,
        Vector3 end
    )
    {
        if (line == null)
            return;

        line.SetPosition(0, start);
        line.SetPosition(1, end);
        line.enabled = true;
    }

    void SetLineVisible(
        LineRenderer line,
        bool visible
    )
    {
        if (line != null)
            line.enabled = visible;
    }

    void HideVectorLines()
    {
        SetLineVisible(gazeVectorLine, false);
        SetVelocityArrowVisible(false);
    }

    void AddLine(JointId[] lineJoints)
    {
        if (lineMaterial == null)
            return;

        GameObject lineObject =
            new GameObject("SkeletonLine");

        lineObject.transform.SetParent(transform);

        LineRenderer line =
            lineObject.AddComponent<LineRenderer>();

        line.material = lineMaterial;
        line.startWidth = lineWidth;
        line.endWidth = lineWidth;
        line.positionCount = lineJoints.Length;
        line.enabled = false;

        lines.Add(
            new LineData
            {
                joints = lineJoints,
                line = line
            }
        );
    }

    void ApplySkeleton()
    {
        foreach (GameObject jointObject in jointObjects.Values)
            jointObject.SetActive(false);

        foreach (KeyValuePair<JointId, Vector3> joint in joints)
        {
            if (jointObjects.TryGetValue(joint.Key, out GameObject jointObject))
            {
                jointObject.transform.position = joint.Value;
                jointObject.SetActive(showSkeleton);
            }
        }

        foreach (LineData lineData in lines)
        {
            bool valid = showSkeleton;

            for (int i = 0; i < lineData.joints.Length; i++)
            {
                if (!joints.TryGetValue(
                    lineData.joints[i],
                    out Vector3 position
                ))
                {
                    valid = false;
                    break;
                }

                lineData.line.SetPosition(i, position);
            }

            lineData.line.enabled = valid;
        }
    }

    void HideSkeleton()
    {
        foreach (GameObject jointObject in jointObjects.Values)
            jointObject.SetActive(false);

        foreach (LineData lineData in lines)
            lineData.line.enabled = false;
    }

    void UpdateLight(float timeSec)
    {
        while (
            lightFrameIndex < lightFrames.Count &&
            lightFrames[lightFrameIndex].unityTime <= timeSec
        )
        {
            LightFrame lightFrame =
                lightFrames[lightFrameIndex];

            ApplyLight(
                light1Object,
                lightFrame.light1,
                light1OnColor,
                light1OffColor
            );

            ApplyLight(
                light2Object,
                lightFrame.light2,
                light2OnColor,
                light2OffColor
            );

            ApplyLight(
                light3Object,
                lightFrame.light3,
                light3OnColor,
                light3OffColor
            );

            lightFrameIndex++;
        }
    }

    void ApplyLight(
        Renderer targetRenderer,
        bool isOn,
        Color onColor,
        Color offColor
    )
    {
        if (targetRenderer == null)
            return;

        targetRenderer.material.color =
            isOn ? onColor : offColor;
    }

    void ResetLights()
    {
        ApplyLight(
            light1Object,
            false,
            light1OnColor,
            light1OffColor
        );

        ApplyLight(
            light2Object,
            false,
            light2OnColor,
            light2OffColor
        );

        ApplyLight(
            light3Object,
            false,
            light3OnColor,
            light3OffColor
        );
    }

    void UpdateFrameText()
    {
        if (frameText == null)
            return;

        if (frameText.gameObject.activeSelf != showFrameNumber)
            frameText.gameObject.SetActive(showFrameNumber);

        if (!showFrameNumber)
            return;

        frameText.text =
            $"Frame : {Mathf.Clamp(frameIndex, 0, frames.Count)} / {frames.Count}";
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

        var col =
            meshObject.GetComponent<MeshCollider>()
            ??
            meshObject.AddComponent<MeshCollider>();

        col.sharedMesh = targetMesh;
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

    private class LineData
    {
        public JointId[] joints;
        public LineRenderer line;
    }
}
