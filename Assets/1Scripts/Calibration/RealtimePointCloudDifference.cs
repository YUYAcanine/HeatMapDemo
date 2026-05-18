using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Microsoft.Azure.Kinect.Sensor;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class RealtimePointCloudDifference : MonoBehaviour
{
    [Header("Kinect")]
    public int deviceIndex = 0;

    [Header("Point Cloud")]
    public int stride = 8;

    [Header("Difference")]
    public float voxelSize = 0.05f;

    [Header("Realtime")]
    public float updateInterval = 0.3f;

    [Header("Colors")]
    public Color normalColor =
        new Color(0.3f, 0.3f, 0.3f);

    public Color diffColor =
        Color.green;

    // 背景保存
    private HashSet<Vector3Int>
        backgroundVoxels =
        new HashSet<Vector3Int>();

    // Kinect
    private Device device;
    private Transformation transformation;

    // Mesh
    private Mesh mesh;

    // 状態管理
    private bool initialized = false;
    private Coroutine realtimeCoroutine;

    // =========================
    // 初期化
    // =========================
    void Start()
    {
        InitializeKinect();

        InitializeMesh();
    }

    // =========================
    // Enable
    // =========================
    void OnEnable()
    {
        // Startより先に来ることがあるので防御
        if (!initialized)
            return;

        StartRealtimeDifference();
    }

    // =========================
    // Disable
    // =========================
    void OnDisable()
    {
        StopRealtimeDifference();
    }

    // =========================
    // Kinect初期化
    // =========================
    void InitializeKinect()
    {
        if (initialized)
            return;

        Debug.Log(
            $"Open Kinect : {deviceIndex}");

        device = Device.Open(deviceIndex);

        device.StartCameras(
            new DeviceConfiguration
        {
            ColorFormat =
                ImageFormat.ColorMJPG,

            ColorResolution =
                ColorResolution.R720p,

            DepthMode =
                DepthMode.NFOV_2x2Binned,

            CameraFPS =
                FPS.FPS30,

            SynchronizedImagesOnly =
                true
        });

        Calibration calib =
            device.GetCalibration();

        transformation =
            calib.CreateTransformation();

        initialized = true;
    }

    // =========================
    // Mesh初期化
    // =========================
    void InitializeMesh()
    {
        mesh = new Mesh();

        mesh.indexFormat =
            UnityEngine.Rendering
            .IndexFormat.UInt32;

        GetComponent<MeshFilter>()
            .mesh = mesh;

        Material mat =
            new Material(
                Shader.Find(
                    "Sprites/Default"));

        GetComponent<MeshRenderer>()
            .material = mat;
    }

    // =========================
    // 背景保存
    // =========================
    public void CaptureBackground()
    {
        Debug.Log(
            $"Capture Background : {deviceIndex}");

        backgroundVoxels.Clear();

        List<Vector3> points =
            GetPointCloud();

        foreach (var p in points)
        {
            backgroundVoxels
                .Add(ToVoxel(p));
        }

        Debug.Log(
            $"Background Saved : {backgroundVoxels.Count}");
    }

    // =========================
    // リアルタイム開始
    // =========================
    public void StartRealtimeDifference()
    {
        if (realtimeCoroutine != null)
            return;

        realtimeCoroutine =
            StartCoroutine(
                RealtimeLoop());

        Debug.Log(
            $"Realtime START : {deviceIndex}");
    }

    // =========================
    // 停止
    // =========================
    public void StopRealtimeDifference()
    {
        if (realtimeCoroutine != null)
        {
            StopCoroutine(
                realtimeCoroutine);

            realtimeCoroutine = null;
        }

        Debug.Log(
            $"Realtime STOP : {deviceIndex}");
    }

    // =========================
    // ループ
    // =========================
    IEnumerator RealtimeLoop()
    {
        while (true)
        {
            UpdateDifference();

            yield return
                new WaitForSeconds(
                    updateInterval);
        }
    }

    // =========================
    // 差分更新
    // =========================
    void UpdateDifference()
    {
        if (backgroundVoxels.Count == 0)
            return;

        List<Vector3> vertices =
            new List<Vector3>();

        List<Color32> colors =
            new List<Color32>();

        List<Vector3> points =
            GetPointCloud();

        int diffCount = 0;

        foreach (var p in points)
        {
            Vector3Int voxel =
                ToVoxel(p);

            // 差分のみ表示
            if (!backgroundVoxels
                .Contains(voxel))
            {
                vertices.Add(p);

                colors.Add(diffColor);

                diffCount++;
            }
        }

        UpdateMesh(vertices, colors);

        Debug.Log(
            $"Diff : {diffCount}");
    }

    // =========================
    // 点群取得
    // =========================
    List<Vector3> GetPointCloud()
    {
        List<Vector3> vertices =
            new List<Vector3>();

        using (Capture cap =
            device.GetCapture())
        {
            Image depth = cap.Depth;

            using (Image pointCloudImage =
                transformation
                .DepthImageToPointCloud(depth))
            {
                var mem =
                    pointCloudImage
                    .GetPixels<Short3>();

                var points =
                    mem.Span;

                for (int i = 0;
                     i < points.Length;
                     i += stride)
                {
                    Short3 p = points[i];

                    if (p.Z <= 0)
                        continue;

                    Vector3 pos =
                        new Vector3(
                            p.X / 1000f,
                            -p.Y / 1000f,
                            p.Z / 1000f
                        );

                    vertices.Add(pos);
                }
            }
        }

        return vertices;
    }

    // =========================
    // voxel化
    // =========================
    Vector3Int ToVoxel(Vector3 p)
    {
        return new Vector3Int(
            Mathf.RoundToInt(
                p.x / voxelSize),

            Mathf.RoundToInt(
                p.y / voxelSize),

            Mathf.RoundToInt(
                p.z / voxelSize)
        );
    }

    // =========================
    // Mesh更新
    // =========================
    void UpdateMesh(
        List<Vector3> pts,
        List<Color32> cols)
    {
        mesh.Clear();

        mesh.vertices =
            pts.ToArray();

        mesh.colors32 =
            cols.ToArray();

        int[] indices =
            new int[pts.Count];

        for (int i = 0;
             i < indices.Length;
             i++)
        {
            indices[i] = i;
        }

        mesh.SetIndices(
            indices,
            MeshTopology.Points,
            0
        );
    }

    // =========================
    // 終了
    // =========================
    void OnDestroy()
    {
        StopRealtimeDifference();

        if (device != null)
        {
            Debug.Log(
                $"Close Kinect : {deviceIndex}");

            device.StopCameras();

            device.Dispose();

            device = null;
        }
    }
}