using Microsoft.Azure.Kinect.Sensor;
using Microsoft.Azure.Kinect.BodyTracking;
using System.Threading.Tasks;
using UnityEngine;
using System.Net.Sockets;
using System.Text;

public class GazeLightControl : MonoBehaviour
{
    [Header("Kinect Transform")]
    public Transform kinectTransform;

    [Header("Ray Settings")]
    public float rayDistance = 100f;
    public float rayInterval = 0.05f;

    [Header("Camera Follow")]
    public bool enableCameraFollow = true;
    public float cameraLerpSpeed = 10f;

    [Header("Light Names")]
    public string light1Target = "Light1";
    public string light2Target = "Light2";
    public string light3Target = "Light3";

    Device kinect;
    Tracker tracker;

    Vector3 headPos;
    Vector3 viewDir;
    Quaternion headRot;

    float nextRayT;

    // ===== Light状態 =====
    bool light1 = false;
    bool light2 = false;
    bool light3 = false;

    string host = "127.0.0.1";
    int port = 9999;

    async void Start()
    {
        InitKinect();

        tracker = Tracker.Create(
            kinect.GetCalibration(),
            TrackerConfiguration.Default
        );

        while (true)
        {
            using Capture cap = await Task.Run(() => kinect.GetCapture());

            UpdateSkeleton(cap);

            if (enableCameraFollow)
                SmoothCamera();

            GazeLogic();

            await Task.Yield();
        }
    }

    void OnDestroy()
    {
        kinect?.StopCameras();
        kinect?.Dispose();
        tracker?.Dispose();
    }

    void InitKinect()
    {
        kinect = Device.Open(0);

        kinect.StartCameras(new DeviceConfiguration
        {
            ColorFormat = ImageFormat.ColorBGRA32,
            ColorResolution = ColorResolution.R720p,
            DepthMode = DepthMode.NFOV_2x2Binned, // ←ここ修正済み
            SynchronizedImagesOnly = true,
            CameraFPS = FPS.FPS30
        });
    }

    void UpdateSkeleton(Capture cap)
    {
        tracker.EnqueueCapture(cap);

        using var frame = tracker.PopResult();

        if (frame == null || frame.NumberOfBodies == 0)
            return;

        var sk = frame.GetBodySkeleton(0);

        Vector3 headLocal = new Vector3(
            sk.GetJoint(JointId.Head).Position.X / 1000f,
            -sk.GetJoint(JointId.Head).Position.Y / 1000f,
            sk.GetJoint(JointId.Head).Position.Z / 1000f
        );

        Vector3 noseLocal = new Vector3(
            sk.GetJoint(JointId.Nose).Position.X / 1000f,
            -sk.GetJoint(JointId.Nose).Position.Y / 1000f,
            sk.GetJoint(JointId.Nose).Position.Z / 1000f
        );

        headPos = ConvertToRoom(headLocal);
        Vector3 nose = ConvertToRoom(noseLocal);

        viewDir = (nose - headPos).normalized;
        headRot = Quaternion.LookRotation(viewDir, Vector3.up);
    }

    Vector3 ConvertToRoom(Vector3 local)
    {
        if (kinectTransform == null) return local;
        return kinectTransform.TransformPoint(local);
    }

    void SmoothCamera()
    {
        var cam = Camera.main;
        if (cam == null) return;

        cam.transform.position = Vector3.Lerp(
            cam.transform.position,
            headPos,
            Time.deltaTime * cameraLerpSpeed
        );

        cam.transform.rotation = Quaternion.Slerp(
            cam.transform.rotation,
            headRot,
            Time.deltaTime * cameraLerpSpeed
        );
    }

    void GazeLogic()
    {
        if (Time.time < nextRayT) return;

        nextRayT = Time.time + rayInterval;

        Ray ray = new Ray(headPos, viewDir);
        Debug.DrawRay(headPos, viewDir * rayDistance, Color.red);

        bool hitL1 = false;
        bool hitL2 = false;
        bool hitL3 = false;

        if (Physics.Raycast(ray, out var hit, rayDistance))
        {
            string name = hit.collider.gameObject.name;

            Debug.Log("Hit: " + name);

            if (name.Contains(light1Target)) hitL1 = true;
            if (name.Contains(light2Target)) hitL2 = true;
            if (name.Contains(light3Target)) hitL3 = true;
        }

        // ===== Light1 =====
        if (hitL1 && !light1)
        {
            Debug.Log("L1 ON");
            SendCommand("L1_ON");
            light1 = true;
        }
        if (!hitL1 && light1)
        {
            Debug.Log("L1 OFF");
            SendCommand("L1_OFF");
            light1 = false;
        }

        // ===== Light2 =====
        if (hitL2 && !light2)
        {
            Debug.Log("L2 ON");
            SendCommand("L2_ON");
            light2 = true;
        }
        if (!hitL2 && light2)
        {
            Debug.Log("L2 OFF");
            SendCommand("L2_OFF");
            light2 = false;
        }

        // ===== Light3 =====
        if (hitL3 && !light3)
        {
            Debug.Log("L3 ON");
            SendCommand("L3_ON");
            light3 = true;
        }
        if (!hitL3 && light3)
        {
            Debug.Log("L3 OFF");
            SendCommand("L3_OFF");
            light3 = false;
        }
    }

    void SendCommand(string msg)
    {
        try
        {
            TcpClient client = new TcpClient(host, port);
            NetworkStream stream = client.GetStream();

            byte[] data = Encoding.UTF8.GetBytes(msg);
            stream.Write(data, 0, data.Length);

            stream.Close();
            client.Close();

            Debug.Log("Sent: " + msg);
        }
        catch
        {
            Debug.Log("TCP失敗");
        }
    }
}