using System.Collections.Generic;
using UnityEngine;
using Microsoft.Azure.Kinect.Sensor;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class KinectPointCloudOnce : MonoBehaviour
{
    public int deviceIndex = 0;
    public int stride = 4; // ä‘à¯Ç´
    private bool captured = false;

    void Start()
    {
        CaptureOnce();
    }

    void CaptureOnce()
    {
        if (captured) return;
        captured = true;

        Device dev = Device.Open(deviceIndex);
        dev.StartCameras(new DeviceConfiguration
        {
            DepthMode = DepthMode.NFOV_2x2Binned,
            ColorResolution = ColorResolution.Off,
            CameraFPS = FPS.FPS30
        });

        using (Capture cap = dev.GetCapture())
        {
            var depth = cap.Depth;
            var calib = dev.GetCalibration();
            var trans = calib.CreateTransformation();

            using (Image cloudImage = trans.DepthImageToPointCloud(depth))
            {
                var cloudMem = cloudImage.GetPixels<Short3>();
                var cloud = cloudMem.Span;   // Åö Ç±Ç±Ç™èdóv

                List<Vector3> points = new List<Vector3>();

                for (int i = 0; i < cloud.Length; i += stride)
                {
                    var p = cloud[i];
                    if (p.Z <= 0) continue;

                    points.Add(new Vector3(
                        p.X / 1000f,
                        -p.Y / 1000f,
                        p.Z / 1000f
                    ));
                }

                CreateMesh(points);
            }

        }

        dev.Dispose();
    }


    void CreateMesh(List<Vector3> pts)
    {
        Mesh mesh = new Mesh();
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.vertices = pts.ToArray();

        int[] idx = new int[pts.Count];
        for (int i = 0; i < idx.Length; i++) idx[i] = i;

        mesh.SetIndices(idx, MeshTopology.Points, 0);
        GetComponent<MeshFilter>().mesh = mesh;
    }
}
