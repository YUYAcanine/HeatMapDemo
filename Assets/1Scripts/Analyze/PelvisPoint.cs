using Microsoft.Azure.Kinect.BodyTracking;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TMPro;
using UnityEngine;

public class PelvisVectorArrow : MonoBehaviour
{
    [Header("JSON File Names")]
    [SerializeField] private string skeletonJson = "skeleton.json";
    [SerializeField] private string lightJson = "light.json";

    [Header("Line Settings")]
    [SerializeField] private Material lineMaterial;
    [SerializeField] private float lineWidth = 0.02f;

    [Header("Arrow Head Settings")]
    [SerializeField] private float headLength = 0.08f;
    [SerializeField] private float headAngle = 25f;

    [Header("Color Settings")]
    [SerializeField] private Color light1Color = new Color(1f, 0f, 0f, 1f);      // 赤
    [SerializeField] private Color light2Color = new Color(0f, 1f, 0f, 1f);      // 緑
    [SerializeField] private Color light3Color = new Color(0f, 0.6f, 1f, 1f);    // 青寄り
    [SerializeField] private Color defaultColor = Color.white;                    // 同時ON / OFF / 未一致

    [Header("Light Filter")]
    [SerializeField] private bool showAll = true;
    [SerializeField] private bool useLight1 = true;
    [SerializeField] private bool useLight2 = false;
    [SerializeField] private bool useLight3 = false;

    [Header("Matching Settings")]
    [SerializeField] private float timeTolerance = 0.1f;   // skeletonとlightの時間対応許容
    [SerializeField] private float minDistance = 0.01f;    // 微小ノイズ除去
    [SerializeField] private float maxDistance = 1.0f;     // 大きすぎる変位を除外

    [Header("Movement Direction Evaluation")]
    [SerializeField] private Transform[] targets;
    [SerializeField] private TMP_Text evaluationText;

    private List<LightFrame> lightFrames = new List<LightFrame>();
    private readonly List<TargetEvaluation> targetEvaluations =
        new List<TargetEvaluation>();

    void Start()
    {
        RegisterTargets();
        LoadLightJson();
        PlotPelvisVectors();
        UpdateEvaluationText();
    }

    // =========================================================
    // Light JSON 読み込み
    // =========================================================
    private void LoadLightJson()
    {
        string path = Path.Combine(Application.dataPath, "Data", lightJson);

        if (!File.Exists(path))
        {
            Debug.LogError($"Light JSON not found: {path}");
            return;
        }

        string json = File.ReadAllText(path);
        LightFrameList data = JsonUtility.FromJson<LightFrameList>(json);

        if (data == null || data.frames == null || data.frames.Count == 0)
        {
            Debug.LogError("Light JSON is empty or invalid.");
            return;
        }

        lightFrames = data.frames;
        Debug.Log($"Loaded Light frames: {lightFrames.Count}");
    }

    // =========================================================
    // Pelvis ベクトル描画
    // =========================================================
    private void PlotPelvisVectors()
    {
        string path = Path.Combine(Application.dataPath, "Data", skeletonJson);

        if (!File.Exists(path))
        {
            Debug.LogError($"Skeleton JSON not found: {path}");
            return;
        }

        string json = File.ReadAllText(path);
        FrameList data = JsonUtility.FromJson<FrameList>(json);

        if (data == null || data.frames == null || data.frames.Count == 0)
        {
            Debug.LogError("Skeleton JSON is empty or invalid.");
            return;
        }

        Vector3? prevPelvis = null;

        foreach (FrameData frame in data.frames)
        {
            if (frame.joints == null || frame.joints.Count == 0)
                continue;

            bool foundPelvis = false;
            Vector3 currentPelvis = Vector3.zero;

            foreach (JointPosition jp in frame.joints)
            {
                if (jp.jointId == JointId.Pelvis.ToString())
                {
                    currentPelvis = jp.position;
                    foundPelvis = true;
                    break;
                }
            }

            if (!foundPelvis)
                continue;

            if (prevPelvis != null)
            {
                float dist = Vector3.Distance(prevPelvis.Value, currentPelvis);

                bool withinMaxDistance =
                    maxDistance <= 0f ||
                    dist <= maxDistance;

                if (dist > minDistance && withinMaxDistance)
                {
                    LightFrame lightFrame =
                        GetClosestLight(frame.unityTime);

                    if (!MatchesSelectedLight(lightFrame))
                    {
                        prevPelvis = currentPelvis;
                        continue;
                    }

                    Vector3 movementVector =
                        currentPelvis - prevPelvis.Value;

                    EvaluateTargets(
                        currentPelvis,
                        movementVector
                    );

                    Color arrowColor = GetLightColor(lightFrame);
                    CreateArrow(prevPelvis.Value, currentPelvis, arrowColor);
                }
            }

            prevPelvis = currentPelvis;
        }

        Debug.Log("Pelvis vector arrows created.");
    }

    private void RegisterTargets()
    {
        targetEvaluations.Clear();

        if (targets == null)
            return;

        foreach (Transform target in targets)
        {
            if (target == null)
                continue;

            targetEvaluations.Add(
                new TargetEvaluation(target)
            );
        }
    }

    private void EvaluateTargets(
        Vector3 currentPelvis,
        Vector3 movementVector
    )
    {
        movementVector.y = 0f;

        float movementMagnitude =
            movementVector.magnitude;

        if (movementMagnitude <= 0f)
            return;

        foreach (TargetEvaluation evaluation in targetEvaluations)
        {
            if (evaluation.Target == null)
                continue;

            Vector3 targetVector =
                evaluation.Target.position - currentPelvis;

            targetVector.y = 0f;

            float targetMagnitude =
                targetVector.magnitude;

            if (targetMagnitude <= 0f)
                continue;

            float dot =
                Vector3.Dot(
                    movementVector,
                    targetVector
                );

            float cosine =
                dot /
                (movementMagnitude * targetMagnitude);

            evaluation.DotSum += dot;
            evaluation.CosineSum +=
                Mathf.Clamp(cosine, -1f, 1f);
            evaluation.SampleCount++;
        }
    }

    private void UpdateEvaluationText()
    {
        if (evaluationText == null)
            return;

        StringBuilder builder = new StringBuilder();
        builder.Append("Filter : ");
        builder.AppendLine(GetFilterName());
        builder.AppendLine();

        foreach (TargetEvaluation evaluation in targetEvaluations)
        {
            if (evaluation.Target == null)
                continue;

            float averageCosine =
                evaluation.SampleCount > 0
                    ? evaluation.CosineSum / evaluation.SampleCount
                    : 0f;

            builder.AppendLine(evaluation.Target.name);
            builder.Append("  Dot Sum : ");
            builder.AppendLine(evaluation.DotSum.ToString("F4"));
            builder.Append("  Cos Average : ");
            builder.AppendLine(averageCosine.ToString("F4"));
            builder.Append("  Samples : ");
            builder.AppendLine(evaluation.SampleCount.ToString());
            builder.AppendLine();
        }

        evaluationText.text = builder.ToString();
    }

    // =========================================================
    // 時刻に最も近いLight frameを探して色を決定
    // =========================================================
    private LightFrame GetClosestLight(float skeletonTime)
    {
        if (lightFrames == null || lightFrames.Count == 0)
            return null;

        LightFrame closest = null;
        float minDiff = float.MaxValue;

        foreach (LightFrame lf in lightFrames)
        {
            float diff = Mathf.Abs(lf.unityTime - skeletonTime);

            if (diff < minDiff)
            {
                minDiff = diff;
                closest = lf;
            }
        }

        if (closest == null || minDiff > timeTolerance)
            return null;

        return closest;
    }

    private bool MatchesSelectedLight(LightFrame lightFrame)
    {
        if (showAll)
            return true;

        if (lightFrame == null)
            return false;

        return
            lightFrame.light1 == useLight1 &&
            lightFrame.light2 == useLight2 &&
            lightFrame.light3 == useLight3;
    }

    private string GetFilterName()
    {
        if (showAll)
            return "All";

        if (useLight1 && !useLight2 && !useLight3)
            return "Light1";

        if (!useLight1 && useLight2 && !useLight3)
            return "Light2";

        if (!useLight1 && !useLight2 && useLight3)
            return "Light3";

        if (!useLight1 && !useLight2 && !useLight3)
            return "All Lights Off";

        StringBuilder builder = new StringBuilder();

        if (useLight1)
            builder.Append("Light1 ");

        if (useLight2)
            builder.Append("Light2 ");

        if (useLight3)
            builder.Append("Light3 ");

        return builder.ToString().TrimEnd();
    }

    private Color GetLightColor(LightFrame lightFrame)
    {
        if (lightFrame == null)
            return defaultColor;

        int onCount =
            (lightFrame.light1 ? 1 : 0) +
            (lightFrame.light2 ? 1 : 0) +
            (lightFrame.light3 ? 1 : 0);

        // 2つ以上同時ONは無視
        if (onCount != 1)
            return defaultColor;

        if (lightFrame.light1) return light1Color;
        if (lightFrame.light2) return light2Color;
        if (lightFrame.light3) return light3Color;

        return defaultColor;
    }

    // =========================================================
    // 矢印生成
    // =========================================================
    private void CreateArrow(Vector3 start, Vector3 end, Color color)
    {
        GameObject arrowParent = new GameObject("PelvisArrow");

        // 本体
        LineRenderer body = arrowParent.AddComponent<LineRenderer>();
        SetupLineRenderer(body, color);
        body.positionCount = 2;
        body.SetPosition(0, start);
        body.SetPosition(1, end);

        // 矢印ヘッド
        Vector3 dir = (end - start).normalized;

        if (dir.sqrMagnitude < 1e-8f)
            return;

        Vector3 rightDir = Quaternion.LookRotation(dir) *
                           Quaternion.Euler(0f, 180f + headAngle, 0f) *
                           Vector3.forward;

        Vector3 leftDir = Quaternion.LookRotation(dir) *
                          Quaternion.Euler(0f, 180f - headAngle, 0f) *
                          Vector3.forward;

        CreateHeadLine(arrowParent.transform, end, end + rightDir * headLength, color);
        CreateHeadLine(arrowParent.transform, end, end + leftDir * headLength, color);
    }

    private void CreateHeadLine(Transform parent, Vector3 start, Vector3 end, Color color)
    {
        GameObject headObj = new GameObject("Head");
        headObj.transform.SetParent(parent);

        LineRenderer lr = headObj.AddComponent<LineRenderer>();
        SetupLineRenderer(lr, color);
        lr.positionCount = 2;
        lr.SetPosition(0, start);
        lr.SetPosition(1, end);
    }

    // =========================================================
    // LineRenderer 共通設定
    // =========================================================
    private void SetupLineRenderer(LineRenderer lr, Color color)
    {
        lr.startWidth = lineWidth;
        lr.endWidth = lineWidth;
        lr.useWorldSpace = true;
        lr.numCapVertices = 4;
        lr.numCornerVertices = 4;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;

        Material mat;

        if (lineMaterial != null)
        {
            mat = new Material(lineMaterial);
        }
        else
        {
            Shader fallbackShader = Shader.Find("Sprites/Default");
            if (fallbackShader == null)
            {
                Debug.LogError("No lineMaterial assigned and fallback shader not found.");
                return;
            }
            mat = new Material(fallbackShader);
        }

        // 色をできるだけ確実に反映
        if (mat.HasProperty("_BaseColor"))
            mat.SetColor("_BaseColor", color);

        if (mat.HasProperty("_Color"))
            mat.SetColor("_Color", color);

        mat.color = color;

        lr.material = mat;
        lr.startColor = color;
        lr.endColor = color;
    }

    // =========================================================
    // JSON Classes
    // =========================================================
    [System.Serializable]
    private class FrameList
    {
        public List<FrameData> frames;
    }

    [System.Serializable]
    private class FrameData
    {
        public float unityTime;
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

    private class TargetEvaluation
    {
        public Transform Target { get; }
        public float DotSum { get; set; }
        public float CosineSum { get; set; }
        public int SampleCount { get; set; }

        public TargetEvaluation(Transform target)
        {
            Target = target;
        }
    }
}
