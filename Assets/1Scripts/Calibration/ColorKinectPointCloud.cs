using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using Microsoft.Azure.Kinect.Sensor;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class ColorKinectPointCloudOnce : MonoBehaviour
{
    public int deviceIndex = 0;

    [Header("Point Skip")]
    public int stride = 4;

    [Header("Save Settings")]
    public string saveFileName = "pointcloud.pcd";

    // 保存先
    private string saveFolderPath;

    // 保存用
    private List<Vector3> savedVertices =
        new List<Vector3>();

    private List<Color32> savedColors =
        new List<Color32>();

    void Awake()
    {
        // Assets/3DObject/KinectPCD
        saveFolderPath = Path.Combine(
            Application.dataPath,
            "3DObject",
            "KinectPCD");

        // フォルダ自動生成
        if (!Directory.Exists(saveFolderPath))
        {
            Directory.CreateDirectory(saveFolderPath);
        }
    }

    // =========================
    // 点群取得
    // =========================
    public void CaptureOnce()
    {
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
            Image color = cap.Color;

            Calibration calib = dev.GetCalibration();
            Transformation trans = calib.CreateTransformation();

            using (Image pointCloudImage =
                   trans.DepthImageToPointCloud(depth))
            {
                using (Image transformedColor =
                    new Image(
                        ImageFormat.ColorBGRA32,
                        depth.WidthPixels,
                        depth.HeightPixels,
                        depth.WidthPixels * 4))
                {
                    trans.ColorImageToDepthCamera(
                        depth,
                        color,
                        transformedColor);

                    var pointMem =
                        pointCloudImage.GetPixels<Short3>();

                    var points = pointMem.Span;

                    byte[] colorBytes =
                        transformedColor.Memory.ToArray();

                    List<Vector3> vertices =
                        new List<Vector3>();

                    List<Color32> colors =
                        new List<Color32>();

                    for (int i = 0; i < points.Length; i += stride)
                    {
                        Short3 p = points[i];

                        if (p.Z <= 0)
                            continue;

                        Vector3 v = new Vector3(
                            p.X / 1000f,
                            -p.Y / 1000f,
                            p.Z / 1000f
                        );

                        vertices.Add(v);

                        int ci = i * 4;

                        if (ci + 3 < colorBytes.Length)
                        {
                            byte b = colorBytes[ci + 0];
                            byte g = colorBytes[ci + 1];
                            byte r = colorBytes[ci + 2];
                            byte a = colorBytes[ci + 3];

                            colors.Add(
                                new Color32(r, g, b, a));
                        }
                        else
                        {
                            colors.Add(Color.white);
                        }
                    }

                    // 保存用に保持
                    savedVertices = vertices;
                    savedColors = colors;

                    CreateMesh(vertices, colors);

                    Debug.Log(
                        $"PointCloud Captured : {vertices.Count} points");
                }
            }
        }

        dev.StopCameras();
        dev.Dispose();
    }

    // =========================
    // PCD保存
    // =========================
    public void SavePCD()
    {
        if (savedVertices == null ||
            savedVertices.Count == 0)
        {
            Debug.LogWarning(
                "保存する点群がありません。先にCaptureOnceしてください。");
            return;
        }

        string path =
            Path.Combine(
                saveFolderPath,
                saveFileName);

        StringBuilder sb = new StringBuilder();

        // =========================
        // PCD Header
        // =========================
        sb.AppendLine("# .PCD v0.7 - Point Cloud Data file format");
        sb.AppendLine("VERSION 0.7");
        sb.AppendLine("FIELDS x y z rgb");
        sb.AppendLine("SIZE 4 4 4 4");
        sb.AppendLine("TYPE F F F U");
        sb.AppendLine("COUNT 1 1 1 1");
        sb.AppendLine($"WIDTH {savedVertices.Count}");
        sb.AppendLine("HEIGHT 1");
        sb.AppendLine("VIEWPOINT 0 0 0 1 0 0 0");
        sb.AppendLine($"POINTS {savedVertices.Count}");
        sb.AppendLine("DATA ascii");

        // =========================
        // Point Data
        // =========================
        for (int i = 0; i < savedVertices.Count; i++)
        {
            Vector3 p = savedVertices[i];
            Color32 c = savedColors[i];

            uint rgb =
                ((uint)c.r << 16) |
                ((uint)c.g << 8) |
                c.b;

            sb.AppendLine(
                $"{p.x} {p.y} {p.z} {rgb}");
        }

        File.WriteAllText(path, sb.ToString());

        Debug.Log($"PCD Saved : {path}");
    }

    // =========================
    // Mesh生成
    // =========================
    void CreateMesh(
        List<Vector3> pts,
        List<Color32> cols)
    {
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
            0);

        GetComponent<MeshFilter>().mesh = mesh;

        Material mat = new Material(
            Shader.Find("Sprites/Default"));

        GetComponent<MeshRenderer>().material = mat;
    }
}