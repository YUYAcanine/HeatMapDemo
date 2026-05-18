using System.Collections.Generic;
using UnityEngine;
using Microsoft.Azure.Kinect.Sensor;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class ColorKinectPointCloudOnce : MonoBehaviour
{
    public int deviceIndex = 0;

    [Header("Point Skip")]
    public int stride = 4;

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

            // =========================
            // 点群生成
            // =========================
            using (Image pointCloudImage =
                   trans.DepthImageToPointCloud(depth))
            {
                // =========================
                // ColorをDepth座標へ変換
                // =========================
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

                        vertices.Add(new Vector3(
                            p.X / 1000f,
                            -p.Y / 1000f,
                            p.Z / 1000f
                        ));

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

                    CreateMesh(vertices, colors);
                }
            }
        }

        dev.StopCameras();
        dev.Dispose();
    }

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

        mesh.SetIndices(indices,
            MeshTopology.Points,
            0);

        GetComponent<MeshFilter>().mesh = mesh;

        // 頂点カラー対応マテリアル
        Material mat = new Material(
            Shader.Find("Sprites/Default"));

        GetComponent<MeshRenderer>().material = mat;
    }
}