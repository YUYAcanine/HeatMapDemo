using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Azure.Kinect.Sensor;
using UnityEngine;

// シーン0(0HomeSetup)用。キネクトの位置姿勢を保存した後に、本番(シーン1)と同じ設定で試し撮りをして、
// フレームの抜けが無いか(PCの書き込みが追いついているか)を確かめる。試し撮りのデータは保存しない。
//
// 使い方: 空の GameObject に付ける。
//   1. いつも通り Space で Env(キネクトの位置姿勢・部屋)を保存する(HomeEnvSetup)
//   2. もう一度 Space → 試し撮りを開始(表示中の全キネクトを Standalone で開き、MKV を一時フォルダに書く)
//   3. もう一度 Space(または Test Duration 秒たつ)→ 試し撮りを終了し、抜けを調べて Console と画面左下に結果を出す
//   2〜3 は何度でもできる。保存後は Space で Env を保存し直さない(保存し直すときは HomeEnvSetup の ContextMenu「Save Env」)。
//
// 結果は抜けを「PC 側(取りに行けなかった・書き込みが追いつかない)」と「キネクト側(フレームが来なかった)」に分けて出し、
// PC 側が 0 なら OK(= PC の処理は追いついている)。判定の仕方は Evaluate のコメント。
//
// 試し撮りのファイルは OS の一時フォルダ(<Temp>/HomeTestRecording/)に書き、シーンの終了時に全部消す。
// (Unity が落ちて消せなかった分も、次にこのシーンを再生したときに消す)。データのフォルダ(Env・Skeleton)には何も書かない。
public class HomeTestRecording : MonoBehaviour
{
    [Header("本番(シーン1の HomeSkeletonRecordingController)と同じにする")]
    [Tooltip("撮影フレームレート")]
    [SerializeField] private FPS cameraFps = FPS.FPS30;
    [Tooltip("カラー解像度(MJPG)")]
    [SerializeField] private ColorResolution rawColorResolution = ColorResolution.R1080p;
    [Tooltip("骨格推定(Body Tracking)も動かす。本番で Record Skeleton をオンにするならオン(GPUの負荷も含めて確かめる)")]
    [SerializeField] private bool recordSkeleton = false;

    [Header("Test")]
    [Tooltip("Space を押さなくても、この秒数たったら試し撮りを終える。0 なら Space を押すまで続ける")]
    [SerializeField] private float testDurationSec = 30f;
    [Tooltip("カメラが動き出してから記録を始めるまで待つ秒数(動き出し直後のフレームの乱れを数えないため)")]
    [SerializeField] private float warmupSec = 1f;
    [Tooltip("Env を保存する前でも試し撮りできるようにする(HomeEnvSetup が無いシーンで使うとき)")]
    [SerializeField] private bool allowBeforeSave = false;
    [SerializeField] private KeyCode toggleKey = KeyCode.Space;
    [SerializeField] private bool showStatus = true;

    private const string TempFolderName = "HomeTestRecording";

    private enum State
    {
        Idle,
        Opening,
        Recording
    }

    private class Unit
    {
        public HomeKinect kinect;
        public HomeSkeletonRecorder recorder;
        public double firstFrameClock = -1;
        public bool began;
        public int droppedByQueue;
    }

    private HomeEnvSetup envSetup;
    private State state = State.Idle;
    private readonly List<Unit> units = new List<Unit>();
    private string rootDir;
    private string testDir;
    private double recordStartClock;
    private float openedAt;
    private int testCount;
    private string lastResult = "";
    private GUIStyle style;

    private void Awake()
    {
        envSetup = FindObjectOfType<HomeEnvSetup>();
        rootDir = GetTempRoot();
        // 前のプレイで消せなかった試し撮りを消す
        DeleteTempFiles();
    }

    private void Update()
    {
        bool ready = allowBeforeSave || envSetup == null || envSetup.HasSaved;

        // 保存後は同じキーを試し撮りに使う(保存のキー入力と同じフレームでは始めない)
        if (envSetup != null && envSetup.HasSaved && envSetup.SaveKey == toggleKey)
            envSetup.SaveKeyEnabled = false;

        if (ready && Input.GetKeyDown(toggleKey) && (envSetup == null || envSetup.LastSavedFrame != Time.frameCount))
        {
            if (state == State.Idle)
                BeginTest();
            else
                EndTest();
        }

        if (state == State.Opening)
            UpdateOpening();
        else if (state == State.Recording && testDurationSec > 0f &&
                 HomeSkeletonRecorder.ClockSeconds - recordStartClock >= testDurationSec)
            EndTest();
    }

    // ------------------------------------------------------------
    // Test
    // ------------------------------------------------------------
    private void BeginTest()
    {
        units.Clear();

        foreach (HomeKinect kinect in FindObjectsOfType<HomeKinect>())
        {
            if (!kinect.isActiveAndEnabled)
                continue;

            // 点群(KinectPointCloudOnce)は Start で撮ってデバイスを閉じているので、同じキネクトを開ける
            KinectPointCloudOnce pointCloud = kinect.GetComponentInChildren<KinectPointCloudOnce>(true);
            HomeSkeletonRecorder recorder = kinect.gameObject.AddComponent<HomeSkeletonRecorder>();
            recorder.deviceIndex = pointCloud != null ? pointCloud.deviceIndex : 0;
            // このPCだけで試すので Standalone(Subordinate は Master が動かないとフレームが来ない)
            recorder.syncMode = WiredSyncMode.Standalone;
            recorder.recordRawMkv = true;
            recorder.rawColorResolution = rawColorResolution;
            recorder.recordSkeleton = recordSkeleton;
            recorder.cameraFps = cameraFps;
            // 抜けが PC 側かキネクト側かを調べるため、キャプチャを受け取った時刻を残す
            recorder.collectCaptureTimings = true;
            units.Add(new Unit { kinect = kinect, recorder = recorder });
        }

        if (units.Count == 0)
        {
            lastResult = "表示中のキネクトがありません。";
            Debug.LogWarning($"[HomeTestRecording] {lastResult}");
            return;
        }

        testCount++;
        testDir = Path.Combine(rootDir, $"test{testCount:00}_{System.DateTime.Now:HHmmss}");
        openedAt = Time.realtimeSinceStartup;
        state = State.Opening;
        lastResult = "";
        Debug.Log($"[HomeTestRecording] 試し撮り {testCount}: キネクト {units.Count} 台を {cameraFps} / カラー {rawColorResolution} / 骨格 {(recordSkeleton ? "あり" : "なし")} で開きます(データは保存しません)。");
    }

    // キネクトが開いてフレームが届き始め、warmupSec たったら記録を始める
    private void UpdateOpening()
    {
        double now = HomeSkeletonRecorder.ClockSeconds;
        bool allReady = true;

        foreach (Unit unit in units)
        {
            if (unit.recorder == null)
                continue;

            if (unit.recorder.LastCaptureClock >= 0 && unit.firstFrameClock < 0)
                unit.firstFrameClock = now;

            if (unit.firstFrameClock < 0 || now - unit.firstFrameClock < warmupSec)
                allReady = false;
        }

        if (allReady)
        {
            StartRecording();
            return;
        }

        if (Time.realtimeSinceStartup - openedAt > 15f)
        {
            List<string> missing = new List<string>();

            foreach (Unit unit in units)
            {
                if (unit.firstFrameClock < 0)
                    missing.Add($"Kinect{unit.kinect.KinectId}");
            }

            lastResult = $"フレームが届かないため試し撮りをやめました: {string.Join(", ", missing)}\n" +
                         "(キネクトを開けたか・他のアプリが使っていないか Console を確認)";
            Debug.LogError($"[HomeTestRecording] {lastResult}");
            ReleaseRecorders();
            state = State.Idle;
        }
    }

    private void StartRecording()
    {
        recordStartClock = HomeSkeletonRecorder.ClockSeconds;
        string startedAt = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");

        foreach (Unit unit in units)
        {
            if (unit.recorder == null || !unit.recorder.IsReady)
                continue;

            unit.recorder.BeginRecording(recordStartClock, testDir, new HomeRawIndex
            {
                experimentName = "Test",
                subjectName = "Test",
                recordingStartedAt = startedAt
            });
            unit.began = true;
        }

        state = State.Recording;
        string until = testDurationSec > 0f ? $"{testDurationSec:F0} 秒たつか、" : "";
        Debug.Log($"[HomeTestRecording] 試し撮り {testCount} を開始しました。{until}{toggleKey} で終了します: {testDir}");
    }

    private void EndTest()
    {
        if (state == State.Opening)
        {
            lastResult = "キネクトの準備中に止めたため、試し撮りをやめました。";
            Debug.Log($"[HomeTestRecording] {lastResult}");
            ReleaseRecorders();
            state = State.Idle;
            return;
        }

        double seconds = HomeSkeletonRecorder.ClockSeconds - recordStartClock;
        StringBuilder result = new StringBuilder($"試し撮り {testCount} ({seconds:F1} 秒, {cameraFps}, カラー {rawColorResolution}, 骨格 {(recordSkeleton ? "あり" : "なし")}):");
        bool allOk = true;

        foreach (Unit unit in units)
        {
            if (!unit.began || unit.recorder == null)
            {
                result.Append($"\n  Kinect{unit.kinect.KinectId}: 記録できませんでした(Console を確認)");
                allOk = false;
                continue;
            }

            // EndRecording でキューの数が消えるので、その前に取る
            unit.droppedByQueue = unit.recorder.RawDroppedCount;
            unit.recorder.EndRecording();
            result.Append("\n  ").Append(Evaluate(unit, unit.recorder.TakeCaptureTimings(), out bool ok));
            allOk &= ok;
        }

        result.Append(allOk
            ? "\n→ PC の処理は追いついています。本番もこの設定で撮れます(キネクト側の抜けは PC では減らせません)。"
            : "\n→ PC の処理が追いついていません。カラー解像度・fps を下げる・骨格推定を切る・他のアプリを閉じるなどして試し直してください。");
        lastResult = result.ToString();

        if (allOk)
            Debug.Log($"[HomeTestRecording] {lastResult}");
        else
            Debug.LogWarning($"[HomeTestRecording] {lastResult}");

        ReleaseRecorders();
        state = State.Idle;
    }

    // 抜けたフレームを数え、1か所ごとに PC 側かキネクト側かを判定する。
    //
    // 使うもの:
    //   raw_index.json … MKV に書いたフレーム(キネクト本体のタイムスタンプ)。全体の抜けの数
    //   CaptureTiming   … GetCapture で受け取った全キャプチャの「呼んだ時刻・戻った時刻」(MKV のキューであふれた分も含む)
    //
    // 受け取ったキャプチャの間でタイムスタンプが飛んでいる所ごとに:
    //   PC 側     … 次の GetCapture を呼ぶのが 1 フレーム分以上遅れた(処理・GC などで取りに行けなかった)、
    //               または飛んだ後のキャプチャが SDK の中で待たされていた(届いてから受け取るまでが、いつもより半フレーム以上遅い)
    //   キネクト側 … GetCapture の中で待っていて、届いたキャプチャもすぐ受け取れている(= キネクトからフレームが来なかった。
    //               カラーと深度がそろわず SDK が捨てた(暗い部屋の自動露出など)・USB など)
    //   MKV の書き込みが追いつかずキューからあふれた分も PC 側。
    // 合否は PC 側の抜けが 0 かどうか(= PC の処理が追いついているか)。
    private string Evaluate(Unit unit, List<HomeSkeletonRecorder.CaptureTiming> timings, out bool ok)
    {
        ok = false;
        string id = unit.kinect.KinectId;
        string indexPath = Path.Combine(testDir, $"{id}_raw_index.json");

        if (!File.Exists(indexPath))
            return $"Kinect{id}: 記録したファイルがありません(Console を確認)";

        HomeRawIndex index = JsonUtility.FromJson<HomeRawIndex>(File.ReadAllText(indexPath));

        if (index == null || index.frames == null || index.frames.Count < 2)
            return $"Kinect{id}: フレームがほとんど記録されていません({index?.frames?.Count ?? 0} frames)";

        double periodSec = 1.0 / FpsValue(cameraFps);

        // MKV に書いたフレームでの抜け(全体)
        int missing = 0;
        double maxGapMs = 0;

        for (int i = 1; i < index.frames.Count; i++)
        {
            double dt = (index.frames[i].depthTimestampUsec - index.frames[i - 1].depthTimestampUsec) * 1e-6;
            missing += System.Math.Max(0, (int)System.Math.Round(dt / periodSec) - 1);
            maxGapMs = System.Math.Max(maxGapMs, dt * 1000.0);
        }

        double duration = (index.frames[index.frames.Count - 1].depthTimestampUsec - index.frames[0].depthTimestampUsec) * 1e-6;
        double fps = duration > 0 ? (index.frames.Count - 1) / duration : 0;

        // 受け取ったキャプチャでの抜けを、PC 側とキネクト側に分ける
        int pcLost = 0, pcGaps = 0, kinectLost = 0, kinectGaps = 0;
        double maxBusyMs = 0, maxLateMs = 0;

        if (timings.Count >= 2)
        {
            // 届いてから受け取るまでの基準(キネクトの時計と PC の時計の差の最小値 = 一番すぐ受け取れたとき)
            double baseOffset = double.MaxValue;

            foreach (HomeSkeletonRecorder.CaptureTiming t in timings)
                baseOffset = System.Math.Min(baseOffset, t.returnClock - t.deviceTicks * 1e-7);

            for (int i = 1; i < timings.Count; i++)
            {
                // 前のキャプチャを受け取ってから、次の GetCapture を呼ぶまで(= 取りに行けなかった時間)
                double busy = timings[i].callClock - timings[i - 1].returnClock;
                // このキャプチャが届いてから受け取るまでの、いつもより余計な遅れ
                double late = timings[i].returnClock - timings[i].deviceTicks * 1e-7 - baseOffset;
                maxBusyMs = System.Math.Max(maxBusyMs, busy * 1000.0);
                maxLateMs = System.Math.Max(maxLateMs, late * 1000.0);

                double dt = (timings[i].deviceTicks - timings[i - 1].deviceTicks) * 1e-7;
                int lost = (int)System.Math.Round(dt / periodSec) - 1;

                if (lost <= 0)
                    continue;

                if (busy >= periodSec || late >= periodSec * 0.5)
                {
                    pcLost += lost;
                    pcGaps++;
                }
                else
                {
                    kinectLost += lost;
                    kinectGaps++;
                }
            }
        }

        int queueLost = unit.droppedByQueue;
        ok = timings.Count >= 2 && pcLost == 0 && queueLost == 0;

        StringBuilder text = new StringBuilder();
        text.Append($"Kinect{id}: {index.frames.Count} frames, 平均 {fps:F2} fps, 抜け {missing} フレーム, 最大間隔 {maxGapMs:F0} ms → {(ok ? "OK(PC の処理は追いついている)" : "NG")}");
        text.Append($"\n    PC 側: 取りに行けなかった {pcLost} フレーム / {pcGaps} か所, 書き込みが追いつかず捨てた {queueLost} フレーム");
        text.Append($"\n    キネクト側: 届かなかった {kinectLost} フレーム / {kinectGaps} か所");

        if (kinectLost > 0)
            text.Append("(PC の処理とは関係なし。カラーの露出(部屋の明るさ)・USB が原因になりやすい)");

        text.Append($"\n    余裕: 取りに行くまでの最大 {maxBusyMs:F0} ms / 受け取りの最大の遅れ {maxLateMs:F0} ms(1 フレーム = {periodSec * 1000.0:F0} ms)");

        if (timings.Count < 2)
            text.Append("\n    (キャプチャの時刻が取れなかったため、PC 側かキネクト側かを判定できませんでした)");

        return text.ToString();
    }

    // 試し撮り用に付けた HomeSkeletonRecorder を外す(OnDestroy でカメラを止め、デバイスを閉じる)
    private void ReleaseRecorders()
    {
        foreach (Unit unit in units)
        {
            if (unit.recorder != null)
                Destroy(unit.recorder);
        }

        units.Clear();
    }

    private void OnDestroy()
    {
        // シーンの終了時: 記録中なら MKV を閉じてから(書き込み中のファイルは消せない)、試し撮りのファイルを全部消す
        foreach (Unit unit in units)
        {
            if (unit.began && unit.recorder != null)
                unit.recorder.EndRecording();
        }

        DeleteTempFiles();
    }

    private void DeleteTempFiles()
    {
        if (string.IsNullOrEmpty(rootDir) || !Directory.Exists(rootDir))
            return;

        try
        {
            Directory.Delete(rootDir, true);
            Debug.Log($"[HomeTestRecording] 試し撮りのファイルを消しました: {rootDir}");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[HomeTestRecording] 試し撮りのファイルを消せませんでした(次にこのシーンを再生したときにもう一度消します): {rootDir}\n{e.Message}");
        }
    }

    // ------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------
    // k4arecord.dll はパスを ANSI で受け取るので、英数字だけのフォルダにする
    private static string GetTempRoot()
    {
        string path = Path.Combine(Path.GetTempPath(), TempFolderName);

        foreach (char c in path)
        {
            if (c > 127)
                return Path.Combine("C:\\", TempFolderName);
        }

        return path;
    }

    private static int FpsValue(FPS fps) =>
        fps == FPS.FPS5 ? 5 : fps == FPS.FPS15 ? 15 : 30;

    private void OnGUI()
    {
        if (!showStatus)
            return;

        if (style == null)
            style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 14, wordWrap = true };

        string text;

        if (envSetup != null && !envSetup.HasSaved && !allowBeforeSave)
        {
            text = $"[試し撮り] {envSetup.SaveKey} で Env を保存した後、もう一度 {toggleKey} で試し撮り(保存しない)";
        }
        else if (state == State.Opening)
        {
            text = "[試し撮り] キネクトを準備中…";
        }
        else if (state == State.Recording)
        {
            double elapsed = HomeSkeletonRecorder.ClockSeconds - recordStartClock;
            StringBuilder sb = new StringBuilder($"[試し撮り {testCount}] 記録中 {elapsed:F1} 秒" +
                                                 (testDurationSec > 0f ? $" / {testDurationSec:F0} 秒" : "") +
                                                 $"  ({toggleKey} で終了)");
            int expected = (int)(elapsed * FpsValue(cameraFps));

            foreach (Unit unit in units)
            {
                if (unit.recorder == null)
                    continue;

                sb.Append($"\n  Kinect{unit.kinect.KinectId}: {unit.recorder.RawFrameCount} / 約 {expected} frames");

                if (unit.recorder.RawDroppedCount > 0)
                    sb.Append($"  書き込みが追いつかず {unit.recorder.RawDroppedCount} 捨てた");
            }

            text = sb.ToString();
        }
        else
        {
            text = $"[試し撮り] {toggleKey} で開始(保存しない)" + (string.IsNullOrEmpty(lastResult) ? "" : "\n" + lastResult);
        }

        GUIContent content = new GUIContent(text);
        float width = 640f;
        float height = style.CalcHeight(content, width) + 8f;
        GUI.Box(new Rect(10, Screen.height - height - 10, width, height), content, style);
    }
}
