using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Microsoft.Azure.Kinect.Sensor;
using Microsoft.Azure.Kinect.BodyTracking;

public class ChildRecorder : MonoBehaviour
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

    [Header("Child Filter")]
    [Tooltip("Head-Pelvis距離の最小値(m)")]
    public float minHeadPelvisDistance = 0.25f;

    [Tooltip("Head-Pelvis距離の最大値(m)")]
    public float maxHeadPelvisDistance = 0.55f;

    [Tooltip("trueなら子供のみ保存")]
    public bool saveOnlyChild = true;

    Device devA, devB, devC, devD;

    Tracker trackerA, trackerB, trackerC, trackerD;

    List<FrameData> framesA = new List<FrameData>();
    List<FrameData> framesB = new List<FrameData>();
    List<FrameData> framesC = new List<FrameData>();
    List<FrameData> framesD = new List<FrameData>();

    bool isRecording = true;

    // ============================
    // Timestamp base
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

        // =====================================
        // Kinect A
        // =====================================

        devA = Device.Open(0);

        devA.StartCameras(
            CreateConfig(WiredSyncMode.Master)
        );

        trackerA = Tracker.Create(
            devA.GetCalibration(),
            TrackerConfiguration.Default
        );

        // =====================================
        // Kinect B
        // =====================================

        devB = Device.Open(1);

        devB.StartCameras(
            CreateConfig(WiredSyncMode.Subordinate)
        );

        trackerB = Tracker.Create(
            devB.GetCalibration(),
            TrackerConfiguration.Default
        );

        // =====================================
        // Kinect C
        // =====================================

        devC = Device.Open(2);

        devC.StartCameras(
            CreateConfig(WiredSyncMode.Subordinate)
        );

        trackerC = Tracker.Create(
            devC.GetCalibration(),
            TrackerConfiguration.Default
        );

        // =====================================
        // Kinect D
        // =====================================

        devD = Device.Open(3);

        devD.StartCameras(
            CreateConfig(WiredSyncMode.Subordinate)
        );

        trackerD = Tracker.Create(
            devD.GetCalibration(),
            TrackerConfiguration.Default
        );

        Debug.Log("SkeletonRecorder started");
    }

    // ============================
    // Kinect Config
    // ============================

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
        if (!isRecording)
            return;

        framesA.Add(
            CaptureSkeleton(
                trackerA,
                devA,
                kinectATransform,
                ref startTimestampA
            )
        );

        framesB.Add(
            CaptureSkeleton(
                trackerB,
                devB,
                kinectBTransform,
                ref startTimestampB
            )
        );

        framesC.Add(
            CaptureSkeleton(
                trackerC,
                devC,
                kinectCTransform,
                ref startTimestampC
            )
        );

        framesD.Add(
            CaptureSkeleton(
                trackerD,
                devD,
                kinectDTransform,
                ref startTimestampD
            )
        );
    }

    // ============================
    // Capture Skeleton
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
            long rawTimestamp =
                cap.Depth.DeviceTimestamp.Ticks;

            if (startTimestamp < 0)
                startTimestamp = rawTimestamp;

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
                    Skeleton bestSkeleton = default;

                    float bestConfidence = -1f;

                    bool foundTarget = false;

                    // =====================================
                    // 全BODY確認
                    // =====================================

                    for (uint i = 0;
                         i < frame.NumberOfBodies;
                         i++)
                    {
                        Skeleton skeleton =
                            frame.GetBodySkeleton(i);

                        Vector3 headPos = Vector3.zero;
                        Vector3 pelvisPos = Vector3.zero;

                        bool hasHead = false;
                        bool hasPelvis = false;

                        float confidenceSum = 0f;
                        int confidenceCount = 0;

                        // -----------------------------
                        // Joint確認
                        // -----------------------------

                        for (JointId jointId = JointId.Pelvis;
                             jointId <= JointId.Nose;
                             jointId++)
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

                            confidenceSum +=
                                (int)joint.ConfidenceLevel;

                            confidenceCount++;

                            // =====================
                            // Head
                            // =====================

                            if (jointId == JointId.Head)
                            {
                                headPos = roomPos;
                                hasHead = true;
                            }

                            // =====================
                            // Pelvis
                            // =====================

                            if (jointId == JointId.Pelvis)
                            {
                                pelvisPos = roomPos;
                                hasPelvis = true;
                            }
                        }

                        // =====================================
                        // Head-Pelvis取得失敗
                        // =====================================

                        if (!hasHead || !hasPelvis)
                            continue;

                        // =====================================
                        // Head-Pelvis距離
                        // =====================================

                        float headPelvisDistance =
                            Vector3.Distance(
                                headPos,
                                pelvisPos
                            );

                        // =====================================
                        // 子供判定
                        // =====================================

                        bool isChild =
                            headPelvisDistance >=
                            minHeadPelvisDistance

                            &&

                            headPelvisDistance <=
                            maxHeadPelvisDistance;

                        // =====================================
                        // confidence平均
                        // =====================================

                        float avgConfidence =
                            confidenceSum /
                            confidenceCount;

                        // =====================================
                        // デバッグ表示
                        // =====================================

                        Debug.Log(
                            $"Body:{i} " +
                            $"HP:{headPelvisDistance:F2}m " +
                            $"Conf:{avgConfidence:F2} " +
                            $"Child:{isChild}"
                        );

                        // =====================================
                        // 子供のみ保存
                        // =====================================

                        if (saveOnlyChild && !isChild)
                            continue;

                        // =====================================
                        // 最もconfidenceが高いBODY採用
                        // =====================================

                        if (avgConfidence >
                            bestConfidence)
                        {
                            bestConfidence =
                                avgConfidence;

                            bestSkeleton =
                                skeleton;

                            foundTarget = true;
                        }
                    }

                    // =====================================
                    // 採用BODY保存
                    // =====================================

                    if (foundTarget)
                    {
                        for (JointId jointId =
                             JointId.Pelvis;

                             jointId <= JointId.Nose;

                             jointId++)
                        {
                            var joint =
                                bestSkeleton.GetJoint(
                                    jointId
                                );

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
                                        (int)joint
                                        .ConfidenceLevel
                                }
                            );
                        }
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

    // ============================
    // Save JSON
    // ============================

    void SaveJson(
        string filename,
        List<FrameData> data
    )
    {
        FrameList wrapper =
            new FrameList
            {
                frames = data
            };

        string json =
            JsonUtility.ToJson(
                wrapper,
                true
            );

        string path =
            Path.Combine(
                outputDir,
                filename
            );

        File.WriteAllText(path, json);

        Debug.Log($"Saved JSON: {path}");
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

        Debug.Log("Skeleton JSON saved");
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