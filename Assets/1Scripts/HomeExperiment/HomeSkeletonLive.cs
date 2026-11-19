using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Azure.Kinect.BodyTracking;
using Microsoft.Azure.Kinect.Sensor;
using UnityEngine;

// シーン6(6SkeletonPublish)用。キネクト1台分の骨格をリアルタイムに推定し、最新の骨格(部屋座標)を持っておく。
// キネクトのGameObjectに HomeKinect と一緒に付ける。SkeletonRealtimePublisher(_FULL) がシーン内のこれを集めて MQTT に送る。
//
// 撮影のロジック(デバイス設定・関節座標の部屋座標への変換)は SkeletonRealtimeMulti / HomeSkeletonRecorder と同じ。
//   - キャプチャ取得と骨格推定はキネクトごとの別スレッドで行う(HomeSkeletonRecorder と同じ。台数分フレームレートが落ちないように)
//   - 記録はしない。Space キーを待たずにプレイ開始からずっと推定する
//   - deviceIndex / syncMode は HomeSkeletonPublishController の Kinects の表で決める
[RequireComponent(typeof(HomeKinect))]
public class HomeSkeletonLive : MonoBehaviour, ISkeletonRealtimeSource
{
    [Header("Device")]
    public int deviceIndex = 0;
    public WiredSyncMode syncMode = WiredSyncMode.Standalone;

    // 撮影フレームレート。HomeSkeletonPublishController の Camera Fps で全キネクトまとめて決める(Start より前に設定される)。
    [NonSerialized] public FPS cameraFps = FPS.FPS30;

    private HomeKinect kinect;
    private Device device;
    private Tracker tracker;
    private Thread worker;
    private volatile bool running;

    private readonly object transformLock = new object();
    private Matrix4x4 kinectToRoom = Matrix4x4.identity;

    // キャプチャのスレッドが丸ごと差し替える(読む側はコピーせずにそのまま使える)
    private volatile List<SkeletonRealtimeMulti.BodyData> latestBodies = new List<SkeletonRealtimeMulti.BodyData>();
    private volatile int frameCount;
    private long lastFrameClockBits = BitConverter.DoubleToInt64Bits(-1.0);

    private static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

    public HomeKinect Kinect => kinect != null ? kinect : (kinect = GetComponent<HomeKinect>());
    public string KinectId => Kinect.KinectId;
    public int DeviceIndex => deviceIndex;
    public IReadOnlyList<SkeletonRealtimeMulti.BodyData> LatestBodies => latestBodies;
    public bool IsReady { get; private set; }
    public bool SerialMismatch { get; private set; }
    public int FrameCount => frameCount;

    // 最後に骨格を受け取ってからの秒数(まだ1枚も来ていなければ負の値)
    public double SecondsSinceLastFrame
    {
        get
        {
            double last = BitConverter.Int64BitsToDouble(Interlocked.Read(ref lastFrameClockBits));
            return last < 0 ? -1 : clock.Elapsed.TotalSeconds - last;
        }
    }

    private void Start()
    {
        // 位置姿勢は HomeEnvLoader が Awake で読み込み済み
        UpdateKinectToRoom();

        try
        {
            device = Device.Open(deviceIndex);
            DeviceConfiguration configuration = new DeviceConfiguration
            {
                ColorResolution = ColorResolution.R720p,
                DepthMode = DepthMode.NFOV_2x2Binned,
                CameraFPS = cameraFps,
                SynchronizedImagesOnly = true,
                WiredSyncMode = syncMode
            };

            device.StartCameras(configuration);
            tracker = Tracker.Create(device.GetCalibration(configuration.DepthMode, configuration.ColorResolution), TrackerConfiguration.Default);
        }
        catch (Exception e)
        {
            Debug.LogError($"[HomeSkeletonLive] Kinect{KinectId} (device {deviceIndex}) を開始できませんでした。\n{e}");
            DisposeDevice();
            return;
        }

        string savedSerial = Kinect.SavedSerialNumber;

        if (!string.IsNullOrEmpty(savedSerial) && savedSerial != device.SerialNum)
        {
            SerialMismatch = true;
            Debug.LogError($"[HomeSkeletonLive] Kinect{KinectId} (device {deviceIndex}): シリアル番号がシーン0と違います " +
                           $"(シーン0: {savedSerial}, 今: {device.SerialNum})。deviceIndex を入れ替えるか、シーン0で位置を合わせ直してください。");
        }

        running = true;
        worker = new Thread(CaptureLoop) { IsBackground = true, Name = $"HomeSkeletonLive_{KinectId}" };
        worker.Start();

        IsReady = true;
        Debug.Log($"[HomeSkeletonLive] Kinect{KinectId} (device {deviceIndex}, sync {syncMode}, {cameraFps}) 準備完了" +
                  (syncMode == WiredSyncMode.Subordinate ? "。Master のキネクトが撮り始めるまでフレームは来ません" : ""));
    }

    private void Update()
    {
        // エディタ上でキネクトを動かしたときも反映する
        if (transform.hasChanged)
        {
            transform.hasChanged = false;
            UpdateKinectToRoom();
        }
    }

    private void UpdateKinectToRoom()
    {
        lock (transformLock)
            kinectToRoom = transform.localToWorldMatrix;
    }

    private void OnDestroy()
    {
        running = false;

        // GetCapture でブロックしているスレッドを抜けさせるため、先にカメラを止める(HomeSkeletonRecorder と同じ止め方)
        try
        {
            device?.StopCameras();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[HomeSkeletonLive] Kinect{KinectId}: StopCameras に失敗しました。\n{e}");
        }

        if (worker != null && !worker.Join(5000))
        {
            // スレッドがまだネイティブ呼び出しの中にいる可能性があるので、解放せずに残す(落ちるより安全)
            Debug.LogError($"[HomeSkeletonLive] Kinect{KinectId}: キャプチャスレッドの終了を待ちきれなかったため、デバイスの解放をスキップしました。");
            return;
        }

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
    // Capture (worker thread)
    // ------------------------------------------------------------
    private void CaptureLoop()
    {
        TimeSpan timeout = TimeSpan.FromMilliseconds(1000);

        while (running)
        {
            try
            {
                using (Capture capture = device.GetCapture(timeout))
                {
                    if (capture.Depth == null)
                        continue;

                    tracker.EnqueueCapture(capture, timeout);
                }

                using (Frame frame = tracker.PopResult(timeout, false))
                {
                    if (frame == null)
                        continue;

                    latestBodies = CreateBodies(frame);
                    frameCount++;
                    Interlocked.Exchange(ref lastFrameClockBits, BitConverter.DoubleToInt64Bits(clock.Elapsed.TotalSeconds));
                }
            }
            catch (TimeoutException)
            {
                // キャプチャが来なかっただけなので続ける
            }
            catch (Exception e)
            {
                if (!running)
                    break;

                Debug.LogError($"[HomeSkeletonLive] Kinect{KinectId}: キャプチャ中にエラーが発生しました。\n{e}");
                Thread.Sleep(100);
            }
        }
    }

    private List<SkeletonRealtimeMulti.BodyData> CreateBodies(Frame frame)
    {
        Matrix4x4 toRoom;

        lock (transformLock)
            toRoom = kinectToRoom;

        List<SkeletonRealtimeMulti.BodyData> bodies = new List<SkeletonRealtimeMulti.BodyData>();

        for (uint bodyIndex = 0; bodyIndex < frame.NumberOfBodies; bodyIndex++)
        {
            Skeleton skeleton = frame.GetBodySkeleton(bodyIndex);
            List<SkeletonRealtimeMulti.JointPosition> joints = new List<SkeletonRealtimeMulti.JointPosition>((int)JointId.Count);

            for (JointId jointId = JointId.Pelvis; jointId < JointId.Count; jointId++)
            {
                var joint = skeleton.GetJoint(jointId);

                // Kinectのカメラ座標(mm, Y下向き) → Unityのローカル座標(m, Y上向き) → 部屋座標
                Vector3 local = new Vector3(
                    joint.Position.X / 1000f,
                    -joint.Position.Y / 1000f,
                    joint.Position.Z / 1000f);

                joints.Add(new SkeletonRealtimeMulti.JointPosition
                {
                    jointId = jointId.ToString(),
                    position = toRoom.MultiplyPoint3x4(local),
                    confidence = (int)joint.ConfidenceLevel
                });
            }

            bodies.Add(new SkeletonRealtimeMulti.BodyData
            {
                bodyId = frame.GetBodyId(bodyIndex),
                joints = joints
            });
        }

        return bodies;
    }
}
