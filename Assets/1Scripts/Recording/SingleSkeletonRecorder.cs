using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Microsoft.Azure.Kinect.Sensor;
using Microsoft.Azure.Kinect.BodyTracking;

public class SkeletonRecorderSingle : MonoBehaviour
{
    [Header("Device")]
    public int deviceIndex = 0;

    [Header("Sync")]
    public WiredSyncMode syncMode = WiredSyncMode.Standalone;

    [Header("Output JSON")]
    public string outputFile = "A_skeleton.json";

    [Header("Room Coordinate")]
    public Transform kinectTransform;

    private Device dev;
    private Tracker tracker;

    private List<FrameData> frames = new List<FrameData>();

    private bool isRecording = true;
    private bool syncInitialized = false;
    private long startTimestamp = -1;


    private string outputDir;

    void Start()
    {
        outputDir = Path.Combine(Application.dataPath, "Data");

        if (!Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        Debug.Log($"Output directory: {outputDir}");

        try
        {
            dev = Device.Open(deviceIndex);

            dev.StartCameras(CreateConfig());

            Debug.Log(
                $"Camera started. Device={deviceIndex} Sync={syncMode}"
            );

            if (syncMode == WiredSyncMode.Subordinate)
            {
                Debug.Log(
                    "Waiting first sync pulse from master..."
                );

                using (Capture warmup = dev.GetCapture())
                {
                    startTimestamp =
                        warmup.Depth.DeviceTimestamp.Ticks;

                    syncInitialized = true;

                    Debug.Log(
                        $"Sync acquired. Timestamp={startTimestamp}"
                    );
                }
            }

            tracker = Tracker.Create(
                dev.GetCalibration(),
                TrackerConfiguration.Default
            );

            Debug.Log(
                $"SkeletonRecorder started. Device={deviceIndex} Sync={syncMode}"
            );
        }
        catch (System.Exception e)
        {
            Debug.LogError(
                $"Failed to start Kinect {deviceIndex}\n{e}"
            );
        }
    }
    DeviceConfiguration CreateConfig()
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

    void Update()
    {
        if (!isRecording)
            return;

        if (dev == null || tracker == null)
            return;

        FrameData frame = CaptureSkeleton();

        if (frame != null)
            frames.Add(frame);
    }

    void OnDestroy()
    {
        SaveJson();

        tracker?.Dispose();
        dev?.Dispose();

        Debug.Log(
            $"Saved {frames.Count} frames : {outputFile}"
        );
    }

    FrameData CaptureSkeleton()
    {
        if (kinectTransform == null)
        {
            Debug.LogWarning(
                $"{name}: Kinect Transform not assigned."
            );
            return null;
        }

        using (Capture cap = dev.GetCapture())
        {
            long rawTimestamp =
                cap.Depth.DeviceTimestamp.Ticks;

            if (
                startTimestamp < 0 &&
                !syncInitialized
            )
            {
                startTimestamp =
                    rawTimestamp;
            }

            long normalizedTimestamp =
                rawTimestamp - startTimestamp;

            tracker.EnqueueCapture(cap);

            List<JointPosition> joints =
                new List<JointPosition>();

            using (var frame = tracker.PopResult())
            {
                if (frame != null &&
                    frame.NumberOfBodies > 0)
                {
                    Skeleton skeleton =
                        frame.GetBodySkeleton(0);

                    for (
                        JointId jointId = JointId.Pelvis;
                        jointId <= JointId.Nose;
                        jointId++
                    )
                    {
                        var joint =
                            skeleton.GetJoint(jointId);

                        Vector3 unityLocal =
                            new Vector3(
                                joint.Position.X / 1000f,
                                -joint.Position.Y / 1000f,
                                joint.Position.Z / 1000f
                            );

                        Vector3 roomPos =
                            kinectTransform.TransformPoint(
                                unityLocal
                            );

                        joints.Add(
                            new JointPosition
                            {
                                jointId =
                                    jointId.ToString(),

                                position =
                                    roomPos,

                                confidence =
                                    (int)joint.ConfidenceLevel
                            }
                        );
                    }
                }
            }

            return new FrameData
            {
                deviceTimestampTicks =
                    rawTimestamp,

                normalizedTimestampTicks =
                    normalizedTimestamp,

                unityTime =
                    Time.time,

                joints =
                    joints
            };
        }
    }

    void SaveJson()
    {
        try
        {
            FrameList wrapper =
                new FrameList
                {
                    frames = frames
                };

            string json =
                JsonUtility.ToJson(
                    wrapper,
                    true
                );

            string path =
                Path.Combine(
                    outputDir,
                    outputFile
                );

            File.WriteAllText(
                path,
                json
            );

            Debug.Log(
                $"Saved JSON: {path}"
            );
        }
        catch (System.Exception e)
        {
            Debug.LogError(
                $"Save failed\n{e}"
            );
        }
    }

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