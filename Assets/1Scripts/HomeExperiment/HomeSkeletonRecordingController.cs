using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

// シーン1(1HeadDirRecording)用。Spaceキーで全キネクトの骨格データ記録を同時に開始/終了し、
// Assets/Data/HomeExperiment/<実験の名前>/Skeleton/<実験対象者>/<ID>_skeleton.json に保存する。
// Record Raw Mkv にチェックを入れると、同じタイミングで全キネクトの生データ(カラー・深度・赤外線の MKV)も
// Raw~/ に記録する(画像での推定 Tools/GazePipeline 用)。
// 実験の名前は同じGameObjectの HomeEnvLoader から取得する。
[RequireComponent(typeof(HomeEnvLoader))]
public class HomeSkeletonRecordingController : MonoBehaviour
{
    [Header("Subject")]
    [HomeExperimentFolder(HomeExperimentFolderAttribute.Kind.Subject)]
    [SerializeField] private string subjectName = "";

    [Header("Recorders")]
    [Tooltip("空の場合はシーン内の表示中の HomeSkeletonRecorder を自動で集める。")]
    [SerializeField] private HomeSkeletonRecorder[] recorders;

    [Header("Raw Recording")]
    [Tooltip("骨格と一緒に、全キネクトのカラー(MJPG)・深度・赤外線をそのまま MKV に記録する(Skeleton/<実験対象者>/Raw~/)。\n" +
             "あとから Tools/GazePipeline で画像から骨格・頭の向き・視線を推定するため。プレイ開始前に設定する。")]
    [SerializeField] private bool recordRawMkv = false;
    [Tooltip("Record Raw Mkv のときのカラー解像度。骨格推定は深度だけを使うので、上げても Kinect の骨格の精度は変わらない(離れた人の顔が大きく写る)。")]
    [SerializeField] private Microsoft.Azure.Kinect.Sensor.ColorResolution rawColorResolution = Microsoft.Azure.Kinect.Sensor.ColorResolution.R1080p;

    [Header("Input")]
    [SerializeField] private KeyCode toggleKey = KeyCode.Space;

    [Header("Safety")]
    [Tooltip("Env(部屋オブジェクト・キネクト位置)を読み込めていない場合は記録を始めない。")]
    [SerializeField] private bool requireEnvLoaded = true;

    [Header("Status")]
    [SerializeField] private bool showStatus = true;

    private HomeEnvLoader env;
    private readonly List<HomeSkeletonRecorder> activeRecorders = new List<HomeSkeletonRecorder>();
    private bool isRecording;
    private double recordingStartClock;
    private string recordingStartedAt;
    private string lastMessage = "";
    private GUIStyle statusStyle;

    private void Awake()
    {
        env = GetComponent<HomeEnvLoader>();

        if (recorders == null || recorders.Length == 0)
            recorders = FindObjectsOfType<HomeSkeletonRecorder>();

        // 各キネクトはカメラを Start で開くので、それより前(Awake)に記録の設定を渡す
        foreach (HomeSkeletonRecorder recorder in recorders)
        {
            if (recorder == null)
                continue;

            recorder.recordRawMkv = recordRawMkv;
            recorder.rawColorResolution = rawColorResolution;
        }
    }

    private void Update()
    {
        if (!Input.GetKeyDown(toggleKey))
            return;

        if (isRecording)
            StopAndSave();
        else
            StartRecording();
    }

    private void LateUpdate()
    {
        HomeExperimentPaths.ClearUISelection();
    }

    private void OnApplicationQuit()
    {
        // 記録中にプレイを止めた場合もデータを失わないように保存する
        if (isRecording)
            StopAndSave();
    }

    public void StartRecording()
    {
        if (isRecording)
            return;

        if (!HomeExperimentPaths.IsValidFolderName(env.ExperimentName, out string error))
        {
            SetMessage($"実験の名前を正しく入力してください。{error}", true);
            return;
        }

        if (!HomeExperimentPaths.IsValidFolderName(subjectName, out error))
        {
            SetMessage($"実験対象者を正しく入力してください。{error}", true);
            return;
        }

        if (requireEnvLoaded && !env.IsLoaded)
        {
            SetMessage("Envを読み込めていないため記録を開始しません。コンソールのエラーを確認してください。", true);
            return;
        }

        activeRecorders.Clear();

        foreach (HomeSkeletonRecorder recorder in recorders)
        {
            if (recorder == null || !recorder.isActiveAndEnabled)
                continue;

            if (!recorder.IsReady)
            {
                Debug.LogWarning($"[HomeSkeletonRecordingController] Kinect{recorder.KinectId} は準備できていないため記録しません。");
                continue;
            }

            activeRecorders.Add(recorder);
        }

        if (activeRecorders.Count == 0)
        {
            SetMessage("記録できるキネクトがありません。", true);
            return;
        }

        recordingStartClock = HomeSkeletonRecorder.ClockSeconds;
        recordingStartedAt = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");

        string rawDir = HomeExperimentPaths.GetRawDirectory(env.ExperimentName, subjectName);
        bool anyRaw = activeRecorders.Exists(r => r.recordRawMkv);

        if (anyRaw)
            BackupPreviousRaw(rawDir);

        foreach (HomeSkeletonRecorder recorder in activeRecorders)
        {
            HomeRawIndex rawInfo = new HomeRawIndex
            {
                experimentName = env.ExperimentName,
                subjectName = subjectName,
                recordingStartedAt = recordingStartedAt
            };

            recorder.BeginRecording(recordingStartClock, anyRaw ? rawDir : null, rawInfo);
        }

        isRecording = true;
        SetMessage($"記録開始: {env.ExperimentName} / {subjectName} (キネクト {activeRecorders.Count} 台)", false);
    }

    public void StopAndSave()
    {
        if (!isRecording)
            return;

        isRecording = false;

        string subjectDir = HomeExperimentPaths.GetSubjectDirectory(env.ExperimentName, subjectName);
        Directory.CreateDirectory(subjectDir);

        string backupSuffix = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
        StringBuilder summary = new StringBuilder();

        foreach (HomeSkeletonRecorder recorder in activeRecorders)
        {
            List<HomeSkeletonFrame> frames = recorder.EndRecording();
            string path = HomeExperimentPaths.GetSkeletonPath(env.ExperimentName, subjectName, recorder.KinectId);

            // 同じ実験対象者で撮り直した場合、前のデータは上書きせず名前を変えて残す
            if (File.Exists(path))
            {
                string backupPath = Path.Combine(subjectDir, $"{recorder.KinectId}_skeleton_old_{backupSuffix}.json");
                File.Move(path, backupPath);
                Debug.LogWarning($"[HomeSkeletonRecordingController] 既存のファイルを退避しました: {backupPath}");
            }

            HomeSkeletonFrameList data = new HomeSkeletonFrameList
            {
                experimentName = env.ExperimentName,
                subjectName = subjectName,
                kinectId = recorder.KinectId,
                deviceIndex = recorder.deviceIndex,
                recordingStartedAt = recordingStartedAt,
                frames = frames
            };

            try
            {
                File.WriteAllText(path, JsonUtility.ToJson(data));
                summary.Append($" {recorder.KinectId}:{frames.Count}frames");
                Debug.Log($"[HomeSkeletonRecordingController] 保存しました ({frames.Count} frames): {path}");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[HomeSkeletonRecordingController] 保存に失敗しました: {path}\n{e}");
            }
        }

        double duration = HomeSkeletonRecorder.ClockSeconds - recordingStartClock;
        activeRecorders.Clear();
        HomeExperimentPaths.RefreshAssetDatabase();

        SetMessage($"記録終了 ({duration:F1}s):{summary} → {subjectDir}", false);
    }

    // 同じ実験対象者で撮り直した場合、前の生データは上書きせず Raw~/old_<日時>/ に移して残す
    private static void BackupPreviousRaw(string rawDir)
    {
        if (!Directory.Exists(rawDir))
            return;

        string[] files = Directory.GetFiles(rawDir);

        if (files.Length == 0)
            return;

        string backupDir = Path.Combine(rawDir, $"old_{System.DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(backupDir);

        foreach (string file in files)
            File.Move(file, Path.Combine(backupDir, Path.GetFileName(file)));

        Debug.LogWarning($"[HomeSkeletonRecordingController] 既存の生データを退避しました: {backupDir}");
    }

    private void SetMessage(string message, bool isError)
    {
        lastMessage = message;

        if (isError)
            Debug.LogError($"[HomeSkeletonRecordingController] {message}");
        else
            Debug.Log($"[HomeSkeletonRecordingController] {message}");
    }

    private void OnGUI()
    {
        if (!showStatus)
            return;

        if (statusStyle == null)
            statusStyle = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 16, wordWrap = true };

        StringBuilder text = new StringBuilder();
        text.AppendLine($"実験: {env.ExperimentName}   実験対象者: {subjectName}");

        if (isRecording)
            text.AppendLine($"● 記録中 {HomeSkeletonRecorder.ClockSeconds - recordingStartClock:F1}s   [{toggleKey}]で終了");
        else
            text.AppendLine($"待機中   [{toggleKey}]で記録開始");

        foreach (HomeSkeletonRecorder recorder in recorders)
        {
            if (recorder == null || !recorder.isActiveAndEnabled)
                continue;

            string state = !recorder.IsReady ? "未接続" :
                isRecording ? $"{recorder.RecordedFrameCount} frames" : "準備完了";

            if (recorder.SerialMismatch)
                state += " / シリアル番号がシーン0と違う";

            if (recorder.recordRawMkv && isRecording)
                state += recorder.IsRecordingRaw ? $" / MKV {recorder.RawFrameCount} frames" : " / MKV 停止(エラー)";
            else if (recorder.recordRawMkv)
                state += " / MKV 記録あり";

            text.AppendLine($"Kinect{recorder.KinectId}: {state} / 検出 {recorder.LatestBodyCount} 人");
        }

        if (!string.IsNullOrEmpty(lastMessage))
            text.Append(lastMessage);

        GUIContent content = new GUIContent(text.ToString());
        const float width = 560f;

        GUI.color = isRecording ? new Color(1f, 0.6f, 0.6f) : Color.white;
        GUI.Box(new Rect(10, 10, width, statusStyle.CalcHeight(content, width)), content, statusStyle);
    }
}
