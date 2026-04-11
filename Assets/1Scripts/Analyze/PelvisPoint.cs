using Microsoft.Azure.Kinect.BodyTracking;
using System.Collections.Generic;
using System.IO;
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

    [Header("Matching Settings")]
    [SerializeField] private float timeTolerance = 0.1f;   // skeletonとlightの時間対応許容
    [SerializeField] private float minDistance = 0.01f;    // 微小ノイズ除去

    private List<LightFrame> lightFrames = new List<LightFrame>();

    void Start()
    {
        LoadLightJson();
        PlotPelvisVectors();
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

                if (dist > minDistance)
                {
                    Color arrowColor = GetLightColor(frame.unityTime);
                    CreateArrow(prevPelvis.Value, currentPelvis, arrowColor);
                }
            }

            prevPelvis = currentPelvis;
        }

        Debug.Log("Pelvis vector arrows created.");
    }

    // =========================================================
    // 時刻に最も近いLight frameを探して色を決定
    // =========================================================
    private Color GetLightColor(float skeletonTime)
    {
        if (lightFrames == null || lightFrames.Count == 0)
            return defaultColor;

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
            return defaultColor;

        int onCount =
            (closest.light1 ? 1 : 0) +
            (closest.light2 ? 1 : 0) +
            (closest.light3 ? 1 : 0);

        // 2つ以上同時ONは無視
        if (onCount != 1)
            return defaultColor;

        if (closest.light1) return light1Color;
        if (closest.light2) return light2Color;
        if (closest.light3) return light3Color;

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
}
