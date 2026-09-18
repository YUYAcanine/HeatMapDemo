using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Microsoft.Azure.Kinect.BodyTracking;
using Microsoft.Azure.Kinect.Sensor;
using UnityEngine;
using Debug = UnityEngine.Debug;

// シーン1(1HeadDirRecording)用。キネクト1台分の骨格データを記録する。キネクトのGameObjectに HomeKinect と一緒に付ける。
//
// 撮影のロジック(デバイス設定・関節座標の部屋座標への変換)は SkeletonRealtimeMulti と同じ。
// 変更点:
//   - キャプチャ取得と骨格推定をキネクトごとの別スレッドで行う
//     (メインスレッドで GetCapture/PopResult を待つと、台数分だけフレームレートが落ちて取りこぼすため)
//   - 信頼度での絞り込みやPublishはせず、検出した全員の全関節を記録する
//   - 記録の開始/終了は HomeSkeletonRecordingController が全台まとめて行う
[RequireComponent(typeof(HomeKinect))]
public class HomeSkeletonRecorder : MonoBehaviour
{
    [Header("Device")]
    public int deviceIndex = 0;
    public WiredSyncMode syncMode = WiredSyncMode.Standalone;

    // 全キネクト共通の時計
    private static readonly Stopwatch clock = Stopwatch.StartNew();
    public static double ClockSeconds => clock.Elapsed.TotalSeconds;

    private HomeKinect kinect;
    private Device device;
    private Tracker tracker;
    private Thread worker;
    private volatile bool running;

    private readonly object frameLock = new object();
    private List<HomeSkeletonFrame> frames = new List<HomeSkeletonFrame>();
    private bool isRecording;
    private double recordingStartClock;
    private long firstDeviceTimestamp = -1;
    private Matrix4x4 kinectToRoom = Matrix4x4.identity;

    private volatile int latestBodyCount;

    public string KinectId => Kinect.KinectId;
    public HomeKinect Kinect => kinect != null ? kinect : (kinect = GetComponent<HomeKinect>());
    public bool IsReady { get; private set; }
    public int LatestBodyCount => latestBodyCount;

    public int RecordedFrameCount
    {
        get { lock (frameLock) return frames.Count; }
    }

    private void Start()
    {
        try
        {
            device = Device.Open(deviceIndex);
            device.StartCameras(new DeviceConfiguration
            {
                ColorResolution = ColorResolution.R720p,
                DepthMode = DepthMode.NFOV_2x2Binned,
                CameraFPS = FPS.FPS30,
                SynchronizedImagesOnly = true,
                WiredSyncMode = syncMode
            });

            tracker = Tracker.Create(device.GetCalibration(), TrackerConfiguration.Default);
        }
        catch (Exception e)
        {
            Debug.LogError($"[HomeSkeletonRecorder] Kinect{KinectId} (device {deviceIndex}) を開始できませんでした。\n{e}");
            DisposeDevice();
            return;
        }

        running = true;
        worker = new Thread(CaptureLoop) { IsBackground = true, Name = $"HomeSkeletonRecorder_{KinectId}" };
        worker.Start();

        IsReady = true;
        Debug.Log($"[HomeSkeletonRecorder] Kinect{KinectId} (device {deviceIndex}) 準備完了");
    }

    private void OnDestroy()
    {
        running = false;

        if (worker != null && !worker.Join(3000))
            Debug.LogWarning($"[HomeSkeletonRecorder] Kinect{KinectId}: キャプチャスレッドの終了を待ちきれませんでした。");

        DisposeDevice();
    }

    private void DisposeDevice()
    {
        tracker?.Dispose();
        tracker = null;
        device?.Dispose();
        device = null;
        IsReady = false;
    }

    // ------------------------------------------------------------
    // Recording control (main thread)
    // ------------------------------------------------------------
    public void BeginRecording(double startClockSeconds)
    {
        lock (frameLock)
        {
            frames = new List<HomeSkeletonFrame>();
            firstDeviceTimestamp = -1;
            recordingStartClock = startClockSeconds;

            // 記録中はキネクトを動かさない前提で、部屋座標への変換行列をここで確定させる
            kinectToRoom = transform.localToWorldMatrix;
            isRecording = true;
        }
    }

    public List<HomeSkeletonFrame> EndRecording()
    {
        lock (frameLock)
        {
            isRecording = false;
            List<HomeSkeletonFrame> recorded = frames;
            frames = new List<HomeSkeletonFrame>();
            return recorded;
        }
    }

    // ------------------------------------------------------------
    // Capture (worker thread)
    // ------------------------------------------------------------
    private void CaptureLoop()
    {
        TimeSpan captureTimeout = TimeSpan.FromMilliseconds(1000);
        TimeSpan trackerTimeout = TimeSpan.FromMilliseconds(1000);

        // PopResult で返ってくる骨格フレームと、キャプチャを受け取った時刻を対応づける
        Queue<KeyValuePair<long, double>> captureTimes = new Queue<KeyValuePair<long, double>>();

        while (running)
        {
            try
            {
                using (Capture capture = device.GetCapture(captureTimeout))
                {
                    if (capture.Depth == null)
                        continue;

                    captureTimes.Enqueue(new KeyValuePair<long, double>(capture.Depth.DeviceTimestamp.Ticks, ClockSeconds));

                    while (captureTimes.Count > 30)
                        captureTimes.Dequeue();

                    tracker.EnqueueCapture(capture, trackerTimeout);
                }

                using (Frame frame = tracker.PopResult(trackerTimeout, false))
                {
                    if (frame == null)
                        continue;

                    long deviceTimestamp = frame.DeviceTimestamp.Ticks;
                    double captureClock = FindCaptureClock(captureTimes, deviceTimestamp);

                    latestBodyCount = (int)frame.NumberOfBodies;

                    lock (frameLock)
                    {
                        if (isRecording)
                            frames.Add(CreateFrame(frame, deviceTimestamp, captureClock));
                    }
                }
            }
            catch (TimeoutException)
            {
                // キャプチャが来なかっただけなので続ける
            }
            catch (Exception e)
            {
                if (running)
                {
                    Debug.LogError($"[HomeSkeletonRecorder] Kinect{KinectId}: キャプチャ中にエラーが発生しました。\n{e}");
                    Thread.Sleep(100);
                }
            }
        }
    }

    private static double FindCaptureClock(Queue<KeyValuePair<long, double>> captureTimes, long deviceTimestamp)
    {
        foreach (KeyValuePair<long, double> pair in captureTimes)
        {
            if (pair.Key == deviceTimestamp)
                return pair.Value;
        }

        return ClockSeconds;
    }

    // frameLock の内側で呼ぶ
    private HomeSkeletonFrame CreateFrame(Frame frame, long deviceTimestamp, double captureClock)
    {
        if (firstDeviceTimestamp < 0)
            firstDeviceTimestamp = deviceTimestamp;

        HomeSkeletonFrame data = new HomeSkeletonFrame
        {
            deviceTimestampTicks = deviceTimestamp,
            normalizedTimestampTicks = deviceTimestamp - firstDeviceTimestamp,
            unityTime = (float)captureClock,
            recordingTimeSec = (float)(captureClock - recordingStartClock)
        };

        for (uint bodyIndex = 0; bodyIndex < frame.NumberOfBodies; bodyIndex++)
        {
            Skeleton skeleton = frame.GetBodySkeleton(bodyIndex);
            HomeSkeletonBody body = new HomeSkeletonBody { bodyId = frame.GetBodyId(bodyIndex) };

            for (JointId jointId = JointId.Pelvis; jointId < JointId.Count; jointId++)
            {
                var joint = skeleton.GetJoint(jointId);

                // Kinectのカメラ座標(mm, Y下向き) → Unityのローカル座標(m, Y上向き) → 部屋座標
                Vector3 local = new Vector3(
                    joint.Position.X / 1000f,
                    -joint.Position.Y / 1000f,
                    joint.Position.Z / 1000f);

                body.joints.Add(new HomeSkeletonJoint
                {
                    jointId = jointId.ToString(),
                    position = kinectToRoom.MultiplyPoint3x4(local),
                    confidence = (int)joint.ConfidenceLevel
                });
            }

            data.bodies.Add(body);
        }

        return data;
    }
}
