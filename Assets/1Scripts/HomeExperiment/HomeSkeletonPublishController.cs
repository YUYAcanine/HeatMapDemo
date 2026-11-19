using System.Text;
using UnityEngine;

// シーン6(6SkeletonPublish)用。キネクトの骨格をリアルタイムに推定し、MQTT にパブリッシュする。
// 1PhilipDemo/MultiPosition と同じ送信内容で、シーン8(8RouteToTargets)や MultiIntegration 系のシーンがそのまま受信できる。
//   - SkeletonRealtimePublisher      … 人物ごとの Pelvis/Head/Nose (pathfinder/person_coordinate)
//   - SkeletonRealtimePublisher_FULL … 人物ごとの全32関節 (pathfinder/person_coordinate_FULL)
// キネクトの使う/使わない・deviceIndex・syncMode は他の 4HomeExperiment のシーンと同じく Kinects の表で決め、
// キネクトの位置姿勢は同じGameObjectの HomeEnvLoader が Env/Kinect<ID>_Transform.json(シーン0で保存)から読む。
// キネクトの表示/非表示を HomeEnvLoader(DefaultExecutionOrder -100)が見る前に反映するため、それより先に Awake を実行する。
[DefaultExecutionOrder(-200)]
[RequireComponent(typeof(HomeEnvLoader))]
public class HomeSkeletonPublishController : MonoBehaviour, IHomeDataRootProvider
{
    [Header("Data Location")]
    [Tooltip("Env(キネクト位置・部屋オブジェクト)を読むデータのルートフォルダ。<ここ>/<実験の名前>/Env/ から読む。\n" +
             "空なら既定の Assets/Data/HomeExperiment。シーン0と同じフォルダを指定する。")]
    [HomeDataRootFolder]
    [SerializeField] private string dataRootDirectory = "";

    [Header("Kinects")]
    [Tooltip("キネクトごとの設定。マシンごとに設定する。表を変えるとシーンのキネクトにすぐ反映される。\n" +
             "Use: オフでそのキネクトのオブジェクトを非表示にする(このマシンにつないでいないキネクト)\n" +
             "Device Index: このPCにつながっている台数の中での通し番号 0〜\n" +
             "Sync Mode: 同期ケーブルでつなぐときは、同期信号を出す1台を Master、それ以外を Subordinate にする\n" +
             "表に無いキネクトは触らない。")]
    [SerializeField] private HomeKinectDeviceIndex[] kinectDeviceIndices = HomeKinectDeviceIndex.CreateDefault();
    [Tooltip("全キネクトの撮影フレームレート。Azure Kinect は 5 / 15 / 30 fps のみ。プレイ開始前に設定する。")]
    [SerializeField] private Microsoft.Azure.Kinect.Sensor.FPS cameraFps = Microsoft.Azure.Kinect.Sensor.FPS.FPS30;

    [Header("Publishers")]
    [Tooltip("空の場合はシーン内から自動で探す(状態表示用)。")]
    [SerializeField] private SkeletonRealtimePublisher publisher;
    [SerializeField] private SkeletonRealtimePublisher_FULL publisherFull;

    [Header("Status")]
    [SerializeField] private bool showStatus = true;

    private HomeEnvLoader env;
    private HomeSkeletonLive[] lives = new HomeSkeletonLive[0];
    private GUIStyle statusStyle;

    public string DataRootDirectory => dataRootDirectory;

    private void Awake()
    {
        env = GetComponent<HomeEnvLoader>();
        HomeGpuCheck.LogOnce(nameof(HomeSkeletonPublishController));

        // 各キネクトはデバイスを Start で開くので、それより前(Awake)に表示/非表示・deviceIndex・syncMode を反映する
        HomeKinectDeviceIndex.ApplyToScene(kinectDeviceIndices);
        HomeKinectDeviceIndex.WarnDuplicates(nameof(HomeSkeletonPublishController));

        lives = FindObjectsOfType<HomeSkeletonLive>();

        foreach (HomeSkeletonLive live in lives)
            live.cameraFps = cameraFps;

        if (lives.Length == 0)
            Debug.LogError("[HomeSkeletonPublishController] 使うキネクト(HomeSkeletonLive)がありません。Kinects の表の Use を確認してください。");

        if (publisher == null)
            publisher = FindObjectOfType<SkeletonRealtimePublisher>();

        if (publisherFull == null)
            publisherFull = FindObjectOfType<SkeletonRealtimePublisher_FULL>();
    }

#if UNITY_EDITOR
    [System.NonSerialized] private bool kinectTableLoaded;

    private void OnValidate()
    {
        HomeKinectDeviceIndex.ApplyToSceneInEditor(kinectDeviceIndices, this, ref kinectTableLoaded);
    }
#endif

    private void LateUpdate()
    {
        HomeExperimentPaths.ClearUISelection();
    }

    private void OnGUI()
    {
        if (!showStatus)
            return;

        if (statusStyle == null)
            statusStyle = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 16, wordWrap = true };

        StringBuilder text = new StringBuilder();
        text.AppendLine($"実験: {env.ExperimentName}   骨格をMQTTにパブリッシュ中");

        if (!env.IsLoaded)
            text.AppendLine("Env(キネクト位置)を読み込めていません。Console を確認してください");

        foreach (HomeSkeletonLive live in lives)
        {
            if (live == null || !live.isActiveAndEnabled)
                continue;

            double since = live.SecondsSinceLastFrame;
            string state = !live.IsReady ? "未接続" :
                since < 0 ? "フレーム待ち" :
                since > 1.0 ? $"フレームが {since:F1}s 来ていない" :
                $"検出 {live.LatestBodies.Count} 人 ({live.FrameCount} frames)";

            if (live.SerialMismatch)
                state += " / シリアル番号がシーン0と違う";

            text.AppendLine($"Kinect{live.KinectId} (device {live.deviceIndex}): {state}");
        }

        if (publisher != null)
            text.AppendLine($"Publisher: {(publisher.IsConnected ? "接続中" : "未接続")}");

        if (publisherFull != null)
            text.AppendLine($"Publisher_FULL: {(publisherFull.IsConnected ? "接続中" : "未接続")}");

        GUIContent content = new GUIContent(text.ToString().TrimEnd());
        const float width = 560f;

        GUI.Box(new Rect(10, 10, width, statusStyle.CalcHeight(content, width)), content, statusStyle);
    }
}
