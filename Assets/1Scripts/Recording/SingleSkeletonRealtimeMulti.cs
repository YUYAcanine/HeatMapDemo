using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Microsoft.Azure.Kinect.Sensor;
using Microsoft.Azure.Kinect.BodyTracking;

public class SkeletonRealtimeMulti : MonoBehaviour
{
    [Header("Device")]
    public int deviceIndex = 0;

    [Header("Sync")]
    public WiredSyncMode syncMode = WiredSyncMode.Standalone;

    [Header("Output JSON")]
    [Tooltip("OFFにするとフレームをメモリに溜めず、終了時のJSON保存も行わない(LatestBodiesによるライブパブリッシュには影響しない)。")]
    public bool saveSkeletonJson = true;
    public string outputFile = "A_skeleton.json";

    [Header("Room Coordinate")]
    public Transform kinectTransform;

    [Header("Start Trigger (fallback for testing without a UI Button)")]
    public KeyCode startRecordingKey = KeyCode.Space;

    private Device dev;
    private Tracker tracker;

    private List<FrameData> frames = new List<FrameData>();

    private bool isReady = false;
    private bool isRecording = false;
    private long startTimestamp = -1;
    private bool recordingStarted = false;

    // 全レコーダー共通の開始トリガー。RequestStartAll() を一度呼ぶと
    // 購読中の全インスタンスが同じフレームで isRecording=true になる。
    private static event System.Action OnStartAllRequested;
    private static bool triggerFired = false;

    private string outputDir;

    public enum DebugLogMode
    {
        JsonRecordLog,
        PelvisHeadDistanceLog
    }

    [Header("Debug Log")]
    public DebugLogMode debugLogMode = DebugLogMode.PelvisHeadDistanceLog;
    public float pelvisHeadLogInterval = 1f;
    private float nextPelvisHeadLogTime = 0f;

    // このカメラが直近のフレームで検出したBodyの一覧（他スクリプトからの参照用）。
    // BodyId はカメラをまたいで同一人物を保証しないため、複数カメラを統合する側では
    // Pelvis位置などの空間情報で同一人物かどうかを判定すること。
    public IReadOnlyList<BodyData> LatestBodies => latestBodies;
    public int DeviceIndex => deviceIndex;
    private List<BodyData> latestBodies = new List<BodyData>();

    void OnEnable()
    {
        OnStartAllRequested += HandleStartRequested;
    }

    void OnDisable()
    {
        OnStartAllRequested -= HandleStartRequested;
    }

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

            tracker = Tracker.Create(
                dev.GetCalibration(),
                TrackerConfiguration.Default
            );

            isReady = true;

            Debug.Log(
                $"SkeletonRecorder ready (standby). Device={deviceIndex} Sync={syncMode}"
            );
        }
        catch (System.Exception e)
        {
            Debug.LogError(
                $"Failed to start Kinect {deviceIndex}\n{e}"
            );
        }
    }

    // UIボタンの OnClick や外部スクリプトから呼び出し、待機中の全レコーダーの記録を同時に開始する。
    public static void RequestStartAll()
    {
        if (triggerFired)
            return;

        triggerFired = true;
        OnStartAllRequested?.Invoke();
    }

    private void HandleStartRequested()
    {
        if (!isReady)
        {
            Debug.LogWarning(
                $"Kinect {deviceIndex} is not ready yet; cannot start recording."
            );
            return;
        }

        recordingStarted = false;
        startTimestamp = -1;
        isRecording = true;

        Debug.Log($"Recording triggered for device {deviceIndex}");
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
        if (!triggerFired && Input.GetKeyDown(startRecordingKey))
            RequestStartAll();

        if (!isRecording)
            return;

        if (dev == null || tracker == null)
            return;

        // CaptureSkeleton()はlatestBodies(ライブパブリッシュ用)の更新も兼ねているため、
        // saveSkeletonJsonがOFFでも必ず呼ぶ。溜めない/保存しないのはframesへの追加だけ。
        FrameData frame = CaptureSkeleton();

        if (saveSkeletonJson && frame != null)
            frames.Add(frame);
    }

    void OnDestroy()
    {
        if (saveSkeletonJson)
        {
            SaveJson();
            Debug.Log($"Saved {frames.Count} frames : {outputFile}");
        }
        else
        {
            Debug.Log($"Skeleton JSON saving is disabled; skipped saving for device {deviceIndex}.");
        }

        tracker?.Dispose();
        dev?.Dispose();
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

            if (!recordingStarted)
            {
                recordingStarted = true;

                startTimestamp =
                    rawTimestamp;

                Debug.Log(
                    $"Recording Start! Device={deviceIndex} Timestamp={rawTimestamp}"
                );
            }
            
            if (debugLogMode == DebugLogMode.JsonRecordLog && frames.Count < 30)
            {
                Debug.Log(
                    $"Device={deviceIndex} " +
                    $"Frame={frames.Count} " +
                    $"Raw={rawTimestamp}"
                );
            }

            long normalizedTimestamp =
                rawTimestamp - startTimestamp;

            if (debugLogMode == DebugLogMode.JsonRecordLog && frames.Count == 0)
            {
                Debug.Log(
                    $"Device={deviceIndex} FirstNormalized={normalizedTimestamp}"
                );
            }

            tracker.EnqueueCapture(cap);

            List<BodyData> bodies =
                new List<BodyData>();

            using (var frame = tracker.PopResult())
            {
                if (frame != null)
                {
                    for (
                        uint bodyIndex = 0;
                        bodyIndex < frame.NumberOfBodies;
                        bodyIndex++
                    )
                    {
                        Skeleton skeleton =
                            frame.GetBodySkeleton(bodyIndex);

                        uint trackingId =
                            frame.GetBodyId(bodyIndex);

                        List<JointPosition> joints =
                            new List<JointPosition>();

                        for (
                            JointId jointId = JointId.Pelvis;
                            jointId < JointId.Count;
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

                        bodies.Add(
                            new BodyData
                            {
                                bodyId =
                                    trackingId,

                                joints =
                                    joints
                            }
                        );
                    }
                }

                if (debugLogMode == DebugLogMode.PelvisHeadDistanceLog && Time.time >= nextPelvisHeadLogTime)
                {
                    nextPelvisHeadLogTime = Time.time + pelvisHeadLogInterval;

                    foreach (BodyData body in bodies)
                    {
                        Vector3? pelvisPos = null;
                        Vector3? headPos = null;
                        Vector3? nosePos = null;

                        foreach (JointPosition joint in body.joints)
                        {
                            if (joint.jointId == JointId.Pelvis.ToString())
                                pelvisPos = joint.position;
                            else if (joint.jointId == JointId.Head.ToString())
                                headPos = joint.position;
                            else if (joint.jointId == JointId.Nose.ToString())
                                nosePos = joint.position;
                        }

                        if (pelvisPos.HasValue && headPos.HasValue)
                        {
                            float headPelvisDistance =
                                Vector3.Distance(headPos.Value, pelvisPos.Value);

                            string headNoseLog = "";

                            if (nosePos.HasValue)
                            {
                                Vector3 headToNose =
                                    nosePos.Value - headPos.Value;

                                headNoseLog =
                                    $" HeadToNose={headToNose}";
                            }

                            Debug.Log(
                                $"Device={deviceIndex} BodyId={body.bodyId} " +
                                $"PelvisPos={pelvisPos.Value} " +
                                $"HeadPelvisDistance={headPelvisDistance:F3}" +
                                headNoseLog
                            );
                        }
                    }
                }

                latestBodies = bodies;
            }

            return new FrameData
            {
                deviceTimestampTicks =
                    rawTimestamp,

                normalizedTimestampTicks =
                    normalizedTimestamp,

                unityTime =
                    Time.time,

                bodies =
                    bodies
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

        public List<BodyData> bodies;
    }

    [System.Serializable]
    public class BodyData
    {
        public uint bodyId;

        public List<JointPosition> joints;

        public bool TryGetJointPosition(JointId jointId, out Vector3 position)
        {
            string name = jointId.ToString();

            for (int i = 0; i < joints.Count; i++)
            {
                if (joints[i].jointId == name)
                {
                    position = joints[i].position;
                    return true;
                }
            }

            position = Vector3.zero;
            return false;
        }

        public float GetAverageConfidence()
        {
            if (joints == null || joints.Count == 0)
                return 0f;

            int sum = 0;

            for (int i = 0; i < joints.Count; i++)
                sum += joints[i].confidence;

            return (float)sum / joints.Count;
        }
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