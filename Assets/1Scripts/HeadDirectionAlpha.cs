using Microsoft.Azure.Kinect.Sensor;
using Microsoft.Azure.Kinect.BodyTracking;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using TMPro;

using K4AImage = Microsoft.Azure.Kinect.Sensor.Image;

public class HeadDirectionAlpha_Absolute : MonoBehaviour
{
    [Header("Preview")] public RawImage viewerRawImage;
    [Header("Target Mesh")] public GameObject meshObject;

    [Header("Ray Settings")]
    public float rayDistance = 100f;
    public float rayInterval = 0.05f;

    [Header("Camera Follow (optional)")]
    public bool enableSmoothCamera = true;
    public float cameraLerpSpeed = 15f;

    [Header("Heat Parameters")]
    public float paintRadius = 0.3f;
    public float heatPerHit = 0.2f;
    public float heatDecayPerSec = 0.02f;
    public float colorUpdateInterval = 0.2f;
    public float maxHeatDisplay = 5f;

    [Header("Data Save & Scene")]
    public HeatMeshData heatDataAsset;

    [Header("Gaze Tracking (Integrated)")]
    public TMP_Text gazeText; // TextMeshPro対応
    public string[] gazeTargetNames; // 視線を記録したいオブジェクト名（例："SphereA","SphereB"）

    Device kinect;
    Tracker tracker;
    Vector3 headPos, viewDir;
    Quaternion headRot;

    Mesh targetMesh;
    Vector3[] verts;
    Color[] colors;
    float[] vertHeat;

    float nextRayT, nextClrT;
    Texture2D previewTex;

    // 視線記録
    Dictionary<string, float> gazeTimes = new Dictionary<string, float>();
    string currentTarget = null;
    float gazeStartTime = 0f;

    async void Start()
    {
        if (!InitMesh()) { enabled = false; return; }
        InitKinect();
        tracker = Tracker.Create(kinect.GetCalibration(), TrackerConfiguration.Default);

        // 初期化
        foreach (var name in gazeTargetNames)
            gazeTimes[name] = 0f;

        while (true)
        {
            try
            {
                using Capture cap = await Task.Run(() => kinect.GetCapture()).ConfigureAwait(true);
                ShowPreview(cap);
                UpdateSkeleton(cap);
            }
            catch { break; }

            if (enableSmoothCamera) SmoothCamera();
            HeatAndGazeLogic(); // ← 統合版

            await Task.Yield();
        }
    }

    void OnDestroy()
    {
        kinect?.StopCameras();
        kinect?.Dispose();
        tracker?.Dispose();
    }

    bool InitMesh()
    {
        if (!meshObject) return false;
        MeshFilter mf = meshObject.GetComponent<MeshFilter>();
        if (!mf) return false;

        targetMesh = mf.mesh;
        verts = targetMesh.vertices;
        colors = Enumerable.Repeat(new Color(0, 0, 0, 0), verts.Length).ToArray();
        vertHeat = new float[verts.Length];
        targetMesh.colors = colors;

        var col = meshObject.GetComponent<MeshCollider>() ?? meshObject.AddComponent<MeshCollider>();
        col.sharedMesh = targetMesh;
        return true;
    }

    void InitKinect()
    {
        kinect = Device.Open(0);
        kinect.StartCameras(new DeviceConfiguration
        {
            ColorFormat = ImageFormat.ColorBGRA32,
            ColorResolution = ColorResolution.R1080p,
            DepthMode = DepthMode.NFOV_2x2Binned,
            SynchronizedImagesOnly = true,
            CameraFPS = FPS.FPS15
        });
    }

    void ShowPreview(Capture cap)
    {
        if (!viewerRawImage) return;

        var img = cap.Color;
        int w = img.WidthPixels, h = img.HeightPixels;
        var src = img.GetPixels<BGRA>().ToArray();

        if (previewTex == null || previewTex.width != w || previewTex.height != h)
        {
            previewTex = new Texture2D(w, h, TextureFormat.BGRA32, false);
            viewerRawImage.texture = previewTex;
        }

        Color32[] dst = new Color32[src.Length];
        for (int i = 0; i < src.Length; ++i)
        {
            var p = src[src.Length - 1 - i];
            dst[i] = new Color32(p.R, p.G, p.B, p.A);
        }

        previewTex.SetPixels32(dst);
        previewTex.Apply();
    }

    void UpdateSkeleton(Capture cap)
    {
        tracker.EnqueueCapture(cap);
        using var fr = tracker.PopResult();
        if (fr is not { NumberOfBodies: > 0 }) return;

        var sk = fr.GetBodySkeleton(0);
        headPos = new Vector3(sk.GetJoint(JointId.Head).Position.X / 1000f,
                              -sk.GetJoint(JointId.Head).Position.Y / 1000f + 1.31f,
                               sk.GetJoint(JointId.Head).Position.Z / 1000f);
        Vector3 nose = new Vector3(sk.GetJoint(JointId.Nose).Position.X / 1000f,
                                  -sk.GetJoint(JointId.Nose).Position.Y / 1000f + 1.31f,
                                   sk.GetJoint(JointId.Nose).Position.Z / 1000f);
        viewDir = (nose - headPos + new Vector3(0, -0.05f, 0)).normalized;
        headRot = Quaternion.LookRotation(viewDir, Vector3.up);

        // Kinect未接続でテストしたい場合は以下を有効化
        // headPos = Camera.main.transform.position;
        // viewDir = Camera.main.transform.forward;
    }

    void SmoothCamera()
    {
        var cam = Camera.main;
        cam.transform.position = Vector3.Lerp(cam.transform.position, headPos, Time.deltaTime * cameraLerpSpeed);
        cam.transform.rotation = Quaternion.Slerp(cam.transform.rotation, headRot, Time.deltaTime * cameraLerpSpeed);
    }

    // 🔥 統合版（ヒートマップ + 視線計測）
    void HeatAndGazeLogic()
    {
        if (Time.time >= nextRayT)
        {
            nextRayT = Time.time + rayInterval;
            Ray ray = new Ray(headPos, viewDir);
            Debug.DrawRay(headPos, viewDir * rayDistance, Color.red);

            bool hitSomething = false;

            if (Physics.Raycast(ray, out var hit, rayDistance))
            {
                hitSomething = true;

                // --- ヒートマップ処理 ---
                if (meshObject && hit.collider.transform == meshObject.transform)
                {
                    AddHeat(hit.point);
                }

                // --- 視線オブジェクト検出処理 ---
                string targetName = hit.collider.gameObject.name.Trim();

                if (gazeTargetNames.Any(n => n.Trim() == targetName))
                {
                    if (currentTarget != targetName)
                    {
                        // 前ターゲットを確定
                        if (!string.IsNullOrEmpty(currentTarget) && gazeTimes.ContainsKey(currentTarget))
                            gazeTimes[currentTarget] += Time.time - gazeStartTime;

                        currentTarget = targetName;
                        gazeStartTime = Time.time;

                        if (!gazeTimes.ContainsKey(currentTarget))
                            gazeTimes[currentTarget] = 0f;
                    }
                }
            }

            // 👇 何にも当たらなかった場合、または対象外の物体を見ているとき
            if (!hitSomething ||
                (hitSomething && !gazeTargetNames.Any(n => n.Trim() == hit.collider.gameObject.name.Trim())))
            {
                if (!string.IsNullOrEmpty(currentTarget) && gazeTimes.ContainsKey(currentTarget))
                {
                    gazeTimes[currentTarget] += Time.time - gazeStartTime;
                    currentTarget = null;
                }
            }
        }

        // --- ヒートマップ更新 ---
        if (Time.time >= nextClrT)
        {
            nextClrT = Time.time + colorUpdateInterval;
            DecayHeat();
            ApplyHeatColors();
        }

        // --- テキスト更新（1行に統一） ---
        if (gazeText)
        {
            gazeText.text = "Gaze Time\n";

            var grouped = gazeTimes
                .GroupBy(kv => kv.Key.Trim())
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Value));

            foreach (var kv in grouped)
            {
                float total = kv.Value;
                bool isTracking = (!string.IsNullOrEmpty(currentTarget) && kv.Key == currentTarget.Trim());
                if (isTracking)
                    total += Time.time - gazeStartTime;

                gazeText.text += $"{kv.Key}: {total:F1}s";
                if (isTracking) gazeText.text += " (tracking)";
                gazeText.text += "\n";
            }
        }
    }





    void AddHeat(Vector3 hitW)
    {
        float r2 = paintRadius * paintRadius;
        for (int i = 0; i < verts.Length; ++i)
        {
            Vector3 w = meshObject.transform.TransformPoint(verts[i]);
            if ((w - hitW).sqrMagnitude <= r2) vertHeat[i] += heatPerHit;
        }
    }

    void DecayHeat()
    {
        float dec = heatDecayPerSec * colorUpdateInterval;
        for (int i = 0; i < vertHeat.Length; ++i)
            vertHeat[i] = Mathf.Max(0, vertHeat[i] - dec);
    }

    void ApplyHeatColors()
    {
        for (int i = 0; i < verts.Length; ++i)
            colors[i].a = Mathf.Clamp01(vertHeat[i] / maxHeatDisplay);
        targetMesh.colors = colors;
    }

    public void OnExportAndSwitchScene()
    {
        for (int i = 0; i < colors.Length; ++i)
            colors[i].a = Mathf.Clamp01(vertHeat[i] / maxHeatDisplay);

        if (heatDataAsset != null)
            heatDataAsset.vertexColors = colors;

        SceneManager.LoadScene("6Preview");
    }
}




