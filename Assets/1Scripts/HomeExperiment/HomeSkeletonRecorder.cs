using System;
using System.Collections.Concurrent;
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
//   - recordRawMkv(シーン1の Record Raw Mkv にチェック)のときは、カラー(MJPG)・深度・赤外線もそのまま MKV に記録する(HomeMkvWriter)
[RequireComponent(typeof(HomeKinect))]
public class HomeSkeletonRecorder : MonoBehaviour
{
    [Header("Device")]
    public int deviceIndex = 0;
    public WiredSyncMode syncMode = WiredSyncMode.Standalone;

    // 生データ(MKV)も記録するか・そのときのカラー解像度。
    // HomeSkeletonRecordingController のチェックボックス(Record Raw Mkv)で全キネクトまとめて決める(Start より前に設定される)。
    [NonSerialized] public bool recordRawMkv;
    [NonSerialized] public ColorResolution rawColorResolution = ColorResolution.R1080p;
    // 骨格推定(Body Tracking)を行うか。false のときは Tracker を作らず、MKV(映像)だけを記録する。
    [NonSerialized] public bool recordSkeleton = true;
    // true のときは Start でカメラを動かさず、StartCameras() が呼ばれるまで待つ。
    // 同期ケーブルだけで記録を合わせるとき(HomeMultiMachineRecordingSync の Sync Cable)、Master のキネクトを記録開始まで止めておくのに使う。
    [NonSerialized] public bool deferCameraStart;

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

    private DeviceConfiguration configuration;
    private readonly object rawLock = new object();
    private volatile RawSession rawSession;

    public string KinectId => Kinect.KinectId;
    public HomeKinect Kinect => kinect != null ? kinect : (kinect = GetComponent<HomeKinect>());
    public bool IsReady { get; private set; }

    // シーン0で保存したシリアル番号と、開いたキネクトのシリアル番号が違う
    public bool SerialMismatch { get; private set; }
    public int LatestBodyCount => latestBodyCount;

    private volatile bool camerasRunning;
    public bool CamerasRunning => camerasRunning;

    // 最後にキャプチャを受け取った時刻(ClockSeconds)。まだ1枚も来ていなければ負の値
    private long lastCaptureClockBits = BitConverter.DoubleToInt64Bits(-1.0);
    public double LastCaptureClock => BitConverter.Int64BitsToDouble(Interlocked.Read(ref lastCaptureClockBits));

    public int RecordedFrameCount
    {
        get { lock (frameLock) return frames.Count; }
    }

    private void Start()
    {
        try
        {
            device = Device.Open(deviceIndex);
            configuration = new DeviceConfiguration
            {
                ColorResolution = ColorResolution.R720p,
                DepthMode = DepthMode.NFOV_2x2Binned,
                CameraFPS = FPS.FPS30,
                SynchronizedImagesOnly = true,
                WiredSyncMode = syncMode
            };

            if (recordRawMkv)
            {
                // カメラがJPEGにしたものをそのまま MKV に書くので、Unity側の圧縮の負荷がかからない
                configuration.ColorFormat = ImageFormat.ColorMJPG;
                configuration.ColorResolution = rawColorResolution;
            }

            // 映像のみ記録するとき(recordSkeleton=false)は Tracker を作らない。
            // 複数台の Kinect で Body Tracking の GPU 負荷をかけない分、安定して撮れる。
            // (カメラを後から動かす場合もあるので、校正情報は設定から取る)
            tracker = recordSkeleton
                ? Tracker.Create(device.GetCalibration(configuration.DepthMode, configuration.ColorResolution), TrackerConfiguration.Default)
                : null;

            if (!deferCameraStart)
            {
                device.StartCameras(configuration);
                camerasRunning = true;
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[HomeSkeletonRecorder] Kinect{KinectId} (device {deviceIndex}) を開始できませんでした。\n{e}");
            DisposeDevice();
            return;
        }

        // シーン0で位置を合わせたキネクトと別の個体が同じ deviceIndex に来ていないか(USBのつなぎ直しなどで入れ替わる)
        string savedSerial = Kinect.SavedSerialNumber;

        if (!string.IsNullOrEmpty(savedSerial) && savedSerial != device.SerialNum)
        {
            SerialMismatch = true;
            Debug.LogError($"[HomeSkeletonRecorder] Kinect{KinectId} (device {deviceIndex}): シリアル番号がシーン0と違います " +
                           $"(シーン0: {savedSerial}, 今: {device.SerialNum})。deviceIndex を入れ替えるか、シーン0で位置を合わせ直してください。");
        }

        running = true;
        worker = new Thread(CaptureLoop) { IsBackground = true, Name = $"HomeSkeletonRecorder_{KinectId}" };
        worker.Start();

        IsReady = true;
        Debug.Log($"[HomeSkeletonRecorder] Kinect{KinectId} (device {deviceIndex}, sync {syncMode}) 準備完了" +
                  (deferCameraStart ? "。記録開始までカメラは止めておきます" :
                   syncMode == WiredSyncMode.Subordinate ? "。Master のキネクトが撮り始めるまでフレームは来ません" : ""));
    }

    // deferCameraStart のときに記録開始に合わせて呼ぶ
    public void StartCameras()
    {
        if (device == null || camerasRunning)
            return;

        try
        {
            device.StartCameras(configuration);
            camerasRunning = true;
            Debug.Log($"[HomeSkeletonRecorder] Kinect{KinectId}: カメラを動かしました");
        }
        catch (Exception e)
        {
            Debug.LogError($"[HomeSkeletonRecorder] Kinect{KinectId}: カメラを動かせませんでした。\n{e}");
        }
    }

    public void StopCameras()
    {
        if (device == null || !camerasRunning)
            return;

        // 先にフラグを下ろしてから止める(GetCapture が止めたことによる例外で抜けるのを、エラー扱いしないため)
        camerasRunning = false;

        try
        {
            device.StopCameras();
            Debug.Log($"[HomeSkeletonRecorder] Kinect{KinectId}: カメラを止めました");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[HomeSkeletonRecorder] Kinect{KinectId}: カメラを止められませんでした。\n{e}");
        }
    }

    private void OnDestroy()
    {
        running = false;

        // CaptureLoop が device.GetCapture() でブロックしていると running が false になっても抜けられないので、
        // カメラを止めて GetCapture を例外で即座に抜けさせる(RealtimeView など他のスクリプトと同じ止め方)。
        try
        {
            device?.StopCameras();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[HomeSkeletonRecorder] Kinect{KinectId}: StopCameras に失敗しました。\n{e}");
        }

        CloseRawRecording();

        bool exited = worker == null || worker.Join(5000);

        if (!exited)
        {
            // キャプチャスレッドがまだ device/tracker のネイティブ呼び出しの中にいる可能性がある。
            // ここで Dispose すると、そのスレッドが解放済みのハンドルを使ってしまい Unity ごと落ちる
            // (HomeMkvWriter.cs のコメント参照: 過去に同じ形の use-after-free で落ちたことがある)。
            // 終了を待てない場合はハンドルを解放せずに残す(このプロセス内でこのKinectは使えなくなるが、
            // 落とすより安全)。
            Debug.LogError($"[HomeSkeletonRecorder] Kinect{KinectId}: キャプチャスレッドの終了を待ちきれなかったため、" +
                           "クラッシュを避けてデバイスの解放をスキップしました。");
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
    // Recording control (main thread)
    // ------------------------------------------------------------
    // rawDir: recordRawMkv のときに生データを書き出すフォルダ(Skeleton/<実験対象者>/Raw~)
    public void BeginRecording(double startClockSeconds, string rawDir = null, HomeRawIndex rawInfo = null)
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

        if (recordRawMkv && rawDir != null)
            OpenRawRecording(rawDir, rawInfo ?? new HomeRawIndex());
    }

    public List<HomeSkeletonFrame> EndRecording()
    {
        CloseRawRecording();

        lock (frameLock)
        {
            isRecording = false;
            List<HomeSkeletonFrame> recorded = frames;
            frames = new List<HomeSkeletonFrame>();
            return recorded;
        }
    }

    public int RawFrameCount
    {
        get { RawSession session = rawSession; return session != null ? session.writtenCount : 0; }
    }

    public bool IsRecordingRaw
    {
        get { RawSession session = rawSession; return session != null && !session.error; }
    }

    // ------------------------------------------------------------
    // Raw recording (MKV)
    // ------------------------------------------------------------
    // キャプチャのスレッドではフレームをコピーしてキューに入れるだけにし、MKV への書き込みは専用のスレッドで行う。
    // (トラッカーに渡すキャプチャのメモリを MKV の記録と共有しない / 書き込みでキャプチャの取得を遅らせない)
    private class RawSession
    {
        public HomeMkvWriter writer;
        public HomeRawIndex index;
        public string directory;
        public string finalPath;
        public BlockingCollection<RawItem> queue;
        public Thread thread;
        public volatile int writtenCount;
        public volatile int droppedCount;
        public volatile bool error;
    }

    private struct RawItem
    {
        public IntPtr capture;
        public long colorTimestampUsec;
        public long depthTimestampUsec;
        public float recordingTimeSec;
    }

    // 書き込みが追いつかないときにためておくフレーム数(約5秒分)。あふれたフレームは記録しない
    private const int RawQueueCapacity = 150;

    private void OpenRawRecording(string rawDir, HomeRawIndex info)
    {
        if (device == null)
            return;

        string mkvName = $"{KinectId}.mkv";
        string finalPath = System.IO.Path.Combine(rawDir, mkvName);
        // k4arecord.dll はパスを ANSI で受け取るので、日本語を含むパスには直接書けない。
        // その場合は英数字だけのフォルダに書いておき、終了時に移動する。
        string writePath = IsAscii(finalPath) ? finalPath : GetAsciiTempPath(mkvName);

        try
        {
            System.IO.Directory.CreateDirectory(rawDir);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(writePath));

            if (System.IO.File.Exists(writePath))
                System.IO.File.Delete(writePath);

            Matrix4x4 depthToRoom = kinectToRoom * Matrix4x4.Scale(new Vector3(0.001f, -0.001f, 0.001f));

            info.kinectId = KinectId;
            info.deviceIndex = deviceIndex;
            info.serialNumber = device.SerialNum;
            info.mkvFile = mkvName;
            info.calibrationFile = $"{KinectId}_calibration.json";
            info.colorFormat = configuration.ColorFormat.ToString();
            info.colorResolution = configuration.ColorResolution.ToString();
            info.depthMode = configuration.DepthMode.ToString();
            info.cameraFps = configuration.CameraFPS.ToString();
            info.kinectPosition = transform.position;
            info.kinectRotation = transform.rotation;

            for (int row = 0; row < 4; row++)
            {
                for (int column = 0; column < 4; column++)
                    info.depthToRoom[row * 4 + column] = depthToRoom[row, column];
            }

            // 校正情報(JSON)。MKV にも入っているが、SDK 無しでも読めるように別ファイルでも残す
            byte[] calibration = device.GetRawCalibration();
            int length = System.Array.IndexOf(calibration, (byte)0);

            if (length >= 0)
                System.Array.Resize(ref calibration, length);

            System.IO.File.WriteAllBytes(System.IO.Path.Combine(rawDir, info.calibrationFile), calibration);

            RawSession session = new RawSession
            {
                writer = new HomeMkvWriter(writePath, device, configuration,
                    ("K4A_HOME_KINECT_ID", KinectId),
                    ("K4A_HOME_EXPERIMENT", info.experimentName),
                    ("K4A_HOME_SUBJECT", info.subjectName),
                    ("K4A_HOME_STARTED_AT", info.recordingStartedAt)),
                index = info,
                directory = rawDir,
                finalPath = finalPath,
                queue = new BlockingCollection<RawItem>(RawQueueCapacity)
            };

            session.thread = new Thread(() => RawWriterLoop(session))
            {
                IsBackground = true,
                Name = $"HomeSkeletonRecorder_MKV_{KinectId}"
            };
            session.thread.Start();

            lock (rawLock)
                rawSession = session;

            Debug.Log($"[HomeSkeletonRecorder] Kinect{KinectId} (serial {info.serialNumber}): MKV の記録を開始しました: {writePath}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[HomeSkeletonRecorder] Kinect{KinectId}: MKV の記録を開始できませんでした: {writePath}\n{e}");
        }
    }

    private void CloseRawRecording()
    {
        RawSession session;

        lock (rawLock)
        {
            session = rawSession;
            rawSession = null;

            // これ以降キャプチャのスレッドはキューに入れない
            session?.queue.CompleteAdding();
        }

        if (session == null)
            return;

        // キューに残ったフレームを書き終えて MKV を閉じるまで待つ
        if (!session.thread.Join(60000))
        {
            // 書き込みスレッドがまだ MKV ファイルを開いたままの可能性がある。
            // ここで File.Move/File.Delete するとファイルを壊したり例外で以降の後処理を止めてしまうので、何もしない。
            Debug.LogError($"[HomeSkeletonRecorder] Kinect{KinectId}: MKV の書き込みが終わりません。" +
                           $"このKinectの生データは {session.writer.Path} に残っています(手動で確認してください)。");
            return;
        }

        try
        {
            if (session.writer.Path != session.finalPath)
            {
                if (System.IO.File.Exists(session.finalPath))
                    System.IO.File.Delete(session.finalPath);

                System.IO.File.Move(session.writer.Path, session.finalPath);
            }

            string indexPath = System.IO.Path.Combine(session.directory, $"{KinectId}_raw_index.json");
            System.IO.File.WriteAllText(indexPath, JsonUtility.ToJson(session.index));

            string dropped = session.droppedCount > 0 ? $", 書き込みが追いつかず記録しなかったフレーム {session.droppedCount}" : "";
            Debug.Log($"[HomeSkeletonRecorder] Kinect{KinectId}: MKV を保存しました ({session.index.frames.Count} frames{dropped}): {session.finalPath}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[HomeSkeletonRecorder] Kinect{KinectId}: MKV の保存に失敗しました: {session.finalPath}\n{e}");
        }
    }

    // キャプチャのスレッドから呼ぶ。フレームをコピーして記録用のキューに入れる
    private void WriteRaw(Capture capture, double captureClock)
    {
        lock (rawLock)
        {
            RawSession session = rawSession;

            if (session == null || session.error)
                return;

            RawItem item = new RawItem
            {
                recordingTimeSec = (float)(captureClock - recordingStartClock)
            };

            try
            {
                item.capture = HomeMkvWriter.CopyCapture(capture, out item.colorTimestampUsec, out item.depthTimestampUsec);
            }
            catch (Exception e)
            {
                session.error = true;
                Debug.LogError($"[HomeSkeletonRecorder] Kinect{KinectId}: フレームをコピーできないため、MKV の記録を止めます。\n{e}");
                return;
            }

            if (!session.queue.TryAdd(item))
            {
                HomeMkvWriter.ReleaseCapture(item.capture);
                session.droppedCount++;
            }
        }
    }

    // MKV 記録用のスレッド。キューのフレームを順に書き、終わったら MKV を閉じる
    private void RawWriterLoop(RawSession session)
    {
        foreach (RawItem item in session.queue.GetConsumingEnumerable())
        {
            try
            {
                if (!session.error)
                {
                    session.writer.Write(item.capture);
                    session.index.frames.Add(new HomeRawFrame
                    {
                        index = session.index.frames.Count,
                        colorTimestampUsec = item.colorTimestampUsec,
                        depthTimestampUsec = item.depthTimestampUsec,
                        recordingTimeSec = item.recordingTimeSec
                    });
                    session.writtenCount = session.index.frames.Count;
                }
            }
            catch (Exception e)
            {
                // 書き込みに失敗したら、骨格の記録は続けて MKV だけ止める
                session.error = true;
                Debug.LogError($"[HomeSkeletonRecorder] Kinect{KinectId}: MKV への書き込みに失敗したため、MKV の記録を止めます。\n{e}");
            }
            finally
            {
                HomeMkvWriter.ReleaseCapture(item.capture);
            }
        }

        try
        {
            session.writer.Dispose();
        }
        catch (Exception e)
        {
            Debug.LogError($"[HomeSkeletonRecorder] Kinect{KinectId}: MKV を閉じられませんでした。\n{e}");
        }
    }

    private static bool IsAscii(string text)
    {
        foreach (char c in text)
        {
            if (c > 127)
                return false;
        }

        return true;
    }

    private static string GetAsciiTempPath(string fileName)
    {
        string name = $"{System.IO.Path.GetFileNameWithoutExtension(fileName)}_{DateTime.Now:yyyyMMdd_HHmmss}.mkv";
        string[] candidates =
        {
            System.IO.Path.Combine(System.IO.Directory.GetParent(Application.dataPath).FullName, "RawRecordingTemp"),
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "HomeRawRecording")
        };

        foreach (string directory in candidates)
        {
            string path = System.IO.Path.Combine(directory, name);

            if (IsAscii(path))
                return path;
        }

        return System.IO.Path.Combine("C:\\HomeRawRecording", name);
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
            // カメラを後から動かす場合(deferCameraStart)、動くまで待つ
            if (!camerasRunning)
            {
                Thread.Sleep(10);
                continue;
            }

            try
            {
                using (Capture capture = device.GetCapture(captureTimeout))
                {
                    if (capture.Depth == null)
                        continue;

                    double receivedClock = ClockSeconds;
                    Interlocked.Exchange(ref lastCaptureClockBits, BitConverter.DoubleToInt64Bits(receivedClock));

                    // 骨格推定に渡す前に、生のキャプチャを MKV に書く(recordRawMkv で記録中のときだけ)
                    WriteRaw(capture, receivedClock);

                    // 映像のみ記録(recordSkeleton=false)のときは Tracker が無いので、ここで抜ける
                    if (!recordSkeleton)
                        continue;

                    captureTimes.Enqueue(new KeyValuePair<long, double>(capture.Depth.DeviceTimestamp.Ticks, receivedClock));

                    while (captureTimes.Count > 30)
                        captureTimes.Dequeue();

                    if (!recordRawMkv)
                    {
                        tracker.EnqueueCapture(capture, trackerTimeout);
                    }
                    else
                    {
                        // 生データの記録中: 骨格推定が追いつかない(キネクトを複数台動かすとGPUが足りない)ときは
                        // そのフレームの骨格推定だけ飛ばし、キャプチャの取得(= MKV の記録)は 30fps のまま続ける
                        try
                        {
                            tracker.EnqueueCapture(capture, TimeSpan.Zero);
                        }
                        catch (TimeoutException)
                        {
                        }
                    }
                }

                if (!recordSkeleton)
                    continue;

                // 通常は結果が出るまで待つ。生データの記録中は待たずに、出ている結果だけ受け取る
                TimeSpan popTimeout = recordRawMkv ? TimeSpan.Zero : trackerTimeout;
                Frame frame;

                while ((frame = tracker.PopResult(popTimeout, false)) != null)
                {
                    using (frame)
                    {
                        long deviceTimestamp = frame.DeviceTimestamp.Ticks;
                        double captureClock = FindCaptureClock(captureTimes, deviceTimestamp);

                        latestBodyCount = (int)frame.NumberOfBodies;

                        lock (frameLock)
                        {
                            if (isRecording)
                                frames.Add(CreateFrame(frame, deviceTimestamp, captureClock));
                        }
                    }

                    if (!recordRawMkv)
                        break;
                }
            }
            catch (TimeoutException)
            {
                // キャプチャが来なかっただけなので続ける
            }
            catch (Exception) when (running && !camerasRunning)
            {
                // StopCameras() でカメラを止めたため GetCapture が抜けただけ
            }
            catch (Exception e)
            {
                // 終了処理中(running=false)の例外も握り潰さずログに残す。原因調査のため。
                Debug.LogError($"[HomeSkeletonRecorder] Kinect{KinectId}: キャプチャ中にエラーが発生しました(running={running})。\n{e}");

                if (running)
                    Thread.Sleep(100);
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
