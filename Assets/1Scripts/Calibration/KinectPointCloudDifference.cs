using System.Collections.Generic;
using UnityEngine;
using Microsoft.Azure.Kinect.Sensor;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class KinectPointCloudDifference : MonoBehaviour
{
    [Header("Kinect")]
    public int deviceIndex = 0;

    [Header("Point Cloud")]
    public int stride = 4;

    [Header("Difference Detection")]
    public float voxelSize = 0.1f; // 10cm

    [Header("Colors")]
    public Color normalColor = new Color(0.5f, 0.5f, 0.5f, 1f);
    public Color differenceColor = Color.green;

    // 背景点群保存
    private HashSet<Vector3Int> backgroundVoxels =
        new HashSet<Vector3Int>();

    // =========================
    // 背景撮影ボタン
    // =========================
    public void CaptureBackground()
    {
        Debug.Log("Capture Background");

        backgroundVoxels.Clear();

        List<Vector3> points = CapturePointCloud();

        foreach (var p in points)
        {
            backgroundVoxels.Add(ToVoxel(p));
        }

        // 背景を表示
        List<Color32> colors = new List<Color32>();

        for (int i = 0; i < points.Count; i++)
        {
            colors.Add(normalColor);
        }

        CreateMesh(points, colors);

        Debug.Log("Background Saved : " + backgroundVoxels.Count);
    }

    // =========================
    // 差分検出ボタン
    // =========================
    public void DetectDifference()
    {
        Debug.Log("Detect Difference");

        List<Vector3> points = CapturePointCloud();

        List<Color32> colors =
            new List<Color32>();

        int diffCount = 0;

        foreach (var p in points)
        {
            Vector3Int voxel = ToVoxel(p);

            // 背景になければ差分
            if (backgroundVoxels.Contains(voxel))
            {
                colors.Add(normalColor);
            }
            else
            {
                colors.Add(differenceColor);
                diffCount++;
            }
        }

        CreateMesh(points, colors);

        Debug.Log("Difference Count : " + diffCount);
    }

    // =========================
    // 点群取得
    // =========================
    List<Vector3> CapturePointCloud()
    {
        List<Vector3> vertices =
            new List<Vector3>();

        Device dev = Device.Open(deviceIndex);

        dev.StartCameras(new DeviceConfiguration
        {
            ColorFormat = ImageFormat.ColorBGRA32,
            ColorResolution = ColorResolution.R720p,
            DepthMode = DepthMode.NFOV_2x2Binned,
            CameraFPS = FPS.FPS30,
            SynchronizedImagesOnly = true
        });

        using (Capture cap = dev.GetCapture())
        {
            Image depth = cap.Depth;

            Calibration calib =
                dev.GetCalibration();

            Transformation trans =
                calib.CreateTransformation();

            using (Image pointCloudImage =
                trans.DepthImageToPointCloud(depth))
            {
                var pointMem =
                    pointCloudImage.GetPixels<Short3>();

                var points = pointMem.Span;

                for (int i = 0;
                     i < points.Length;
                     i += stride)
                {
                    Short3 p = points[i];

                    if (p.Z <= 0)
                        continue;

                    Vector3 pos = new Vector3(
                        p.X / 1000f,
                        -p.Y / 1000f,
                        p.Z / 1000f
                    );

                    vertices.Add(pos);
                }
            }
        }

        dev.StopCameras();
        dev.Dispose();

        return vertices;
    }

    // =========================
    // voxel化
    // =========================
    Vector3Int ToVoxel(Vector3 p)
    {
        return new Vector3Int(
            Mathf.RoundToInt(p.x / voxelSize),
            Mathf.RoundToInt(p.y / voxelSize),
            Mathf.RoundToInt(p.z / voxelSize)
        );
    }

    // =========================
    // Mesh生成
    // =========================
    void CreateMesh(
        List<Vector3> pts,
        List<Color32> cols)
    {
        Mesh oldMesh =
            GetComponent<MeshFilter>().mesh;

        if (oldMesh != null)
        {
            Destroy(oldMesh);
        }

        Mesh mesh = new Mesh();

        mesh.indexFormat =
            UnityEngine.Rendering.IndexFormat.UInt32;

        mesh.vertices = pts.ToArray();

        mesh.colors32 = cols.ToArray();

        int[] indices = new int[pts.Count];

        for (int i = 0; i < indices.Length; i++)
        {
            indices[i] = i;
        }

        mesh.SetIndices(
            indices,
            MeshTopology.Points,
            0
        );

        GetComponent<MeshFilter>().mesh = mesh;

        // 頂点カラー対応
        Material mat = new Material(
            Shader.Find("Sprites/Default"));

        GetComponent<MeshRenderer>().material = mat;
    }
}