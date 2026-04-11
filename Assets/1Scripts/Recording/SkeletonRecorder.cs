using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Microsoft.Azure.Kinect.Sensor;
using Microsoft.Azure.Kinect.BodyTracking;

public class SkeletonRecorder : MonoBehaviour
{
    [Header("Output JSON")]
    public string outputA = "A_skeleton.json";
    public string outputB = "B_skeleton.json";
    public string outputC = "C_skeleton.json";
    public string outputD = "D_skeleton.json";

    [Header("Kinect Transforms (Room Coordinate)")]
    public Transform kinectATransform;
    public Transform kinectBTransform;
    public Transform kinectCTransform;
    public Transform kinectDTransform;

    Device devA, devB, devC, devD;
    Tracker trackerA, trackerB, trackerC, trackerD;

    List<FrameData> framesA = new List<FrameData>();
    List<FrameData> framesB = new List<FrameData>();
    List<FrameData> framesC = new List<FrameData>();
    List<FrameData> framesD = new List<FrameData>();

    bool isRecording = true;

    // ============================
    // Timestamp base (0 origin)
    // ============================
    long startTimestampA = -1;
    long startTimestampB = -1;
    long startTimestampC = -1;
    long startTimestampD = -1;

    // ============================
    // Output directory
    // ============================
    string outputDir;

    // ============================
    // Start
    // ============================
    void Start()
    {
        outputDir = Path.Combine(Application.dataPath, "Data");
        if (!Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        Debug.Log($"Output directory: {outputDir}");

        // -------- Kinect A (Master) --------
        devA = Device.Open(0);
        devA.StartCameras(CreateConfig(WiredSyncMode.Master));
        trackerA = Tracker.Create(devA.GetCalibration(), TrackerConfiguration.Default);

        // -------- Kinect B (Subordinate) --------
        devB = Device.Open(1);
        devB.StartCameras(CreateConfig(WiredSyncMode.Subordinate));
        trackerB = Tracker.Create(devB.GetCalibration(), TrackerConfiguration.Default);

        // -------- Kinect C (Subordinate) --------
        devC = Device.Open(2);
        devC.StartCameras(CreateConfig(WiredSyncMode.Subordinate));
        trackerC = Tracker.Create(devC.GetCalibration(), TrackerConfiguration.Default);

        // -------- Kinect D (Subordinate) --------
        devD = Device.Open(3);
        devD.StartCameras(CreateConfig(WiredSyncMode.Subordinate));
        trackerD = Tracker.Create(devD.GetCalibration(), TrackerConfiguration.Default);

        Debug.Log("SkeletonRecorder (4 Kinect / Room Coordinate / Normalized Timestamp) started");
    }

    DeviceConfiguration CreateConfig(WiredSyncMode syncMode)
    {
        return new DeviceConfiguration
        {
            ColorResolution = ColorResolution.R720p,
            DepthMode = DepthMode.NFOV_2x2Binned,
            CameraFPS = FPS.FPS30,
            SynchronizedImagesOnly = true,
            WiredSyncMode = syncMode
        };
    }

    // ============================
    // Update
    // ============================
    void Update()
    {
        if (!isRecording) return;

        framesA.Add(
            CaptureSkeleton(trackerA, devA, kinectATransform, ref startTimestampA)
        );

        framesB.Add(
            CaptureSkeleton(trackerB, devB, kinectBTransform, ref startTimestampB)
        );

        framesC.Add(
            CaptureSkeleton(trackerC, devC, kinectCTransform, ref startTimestampC)
        );

        framesD.Add(
            CaptureSkeleton(trackerD, devD, kinectDTransform, ref startTimestampD)
        );
    }

    // ============================
    // OnDestroy
    // ============================
    void OnDestroy()
    {
        SaveJson(outputA, framesA);
        SaveJson(outputB, framesB);
        SaveJson(outputC, framesC);
        SaveJson(outputD, framesD);

        trackerA?.Dispose();
        trackerB?.Dispose();
        trackerC?.Dispose();
        trackerD?.Dispose();

        devA?.Dispose();
        devB?.Dispose();
        devC?.Dispose();
        devD?.Dispose();

        Debug.Log("Skeleton JSON saved (Assets/Data)");
    }

    // ============================
    // Skeleton Capture
    // ============================
    FrameData CaptureSkeleton(
        Tracker tracker,
        Device dev,
        Transform kinectTransform,
        ref long startTimestamp
    )
    {
        if (kinectTransform == null)
            return null;

        using (Capture cap = dev.GetCapture())
        {
            long rawTimestamp = cap.Depth.DeviceTimestamp.Ticks;

            if (startTimestamp < 0)
                startTimestamp = rawTimestamp;

            long normalizedTimestamp = rawTimestamp - startTimestamp;

            tracker.EnqueueCapture(cap);

            List<JointPosition> joints = new List<JointPosition>();

            using (var frame = tracker.PopResult())
            {
                if (frame != null && frame.NumberOfBodies > 0)
                {
                    Skeleton skeleton = frame.GetBodySkeleton(0);

                    for (JointId jointId = JointId.Pelvis; jointId <= JointId.Nose; jointId++)
                    {
                        var joint = skeleton.GetJoint(jointId);

                        Vector3 unityLocal = new Vector3(
                            joint.Position.X / 1000f,
                            -joint.Position.Y / 1000f,
                            joint.Position.Z / 1000f
                        );

                        Vector3 roomPos =
                            kinectTransform.TransformPoint(unityLocal);

                        joints.Add(new JointPosition
                        {
                            jointId = jointId.ToString(),
                            position = roomPos,
                            confidence = (int)joint.ConfidenceLevel
                        });
                    }
                }
            }

            return new FrameData
            {
                deviceTimestampTicks = rawTimestamp,
                normalizedTimestampTicks = normalizedTimestamp,
                unityTime = Time.time,
                joints = joints
            };
        }
    }

    // ============================
    // JSON Save
    // ============================
    void SaveJson(string filename, List<FrameData> data)
    {
        FrameList wrapper = new FrameList { frames = data };
        string json = JsonUtility.ToJson(wrapper, true);

        string path = Path.Combine(outputDir, filename);
        File.WriteAllText(path, json);

        Debug.Log($"Saved JSON: {path}");
    }

    // ============================
    // Serializable
    // ============================
    [System.Serializable]
    public class FrameData
    {
        public long deviceTimestampTicks;
        public long normalizedTimestampTicks;
        public float unityTime;
        public List<JointPosition> joints;
    }

    [System.Serializable]
    public class JointPosition
    {
        public string jointId;
        public Vector3 position;
        public int confidence;
    }

    [System.Serializable]
    public class FrameList
    {
        public List<FrameData> frames;
    }
}