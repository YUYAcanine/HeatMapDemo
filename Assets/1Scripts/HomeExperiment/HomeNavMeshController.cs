using System.Collections.Generic;
using System.Text;
using UnityEngine;

// シーン7(7NavMeshBuild)用。キネクトの点群から平面を検知して NavMesh を作り、MQTT にパブリッシュする。
// 処理そのものは 1PhilipDemo/PlaneFinder と同じ PlaneFinder(点群 → NavMesh) / PointNavLink(段差のリンク) / NavPub(MQTT送信)。
// このコンポーネントは 4HomeExperiment の他のシーンと同じ方法でキネクトを設定する:
//   - キネクトの使う/使わない・deviceIndex は Kinects の表で決める(PlaneFinder の Kinect Sources はプレイ開始時にここから作り直す)
//   - キネクトの位置姿勢は同じGameObjectの HomeEnvLoader が Env/Kinect<ID>_Transform.json(シーン0で保存)から読む
//   - シーン0で置いた補助の床(Env/FakeFloors.json)を PlaneFinder の Auxiliary Floor Objects に加える(元の PlaneFinder シーンの FakeFloor と同じ使い方)
// 操作: PlaneFinder の Auto Update で一定間隔ごとに作り直す。R キーで今すぐ作り直す。
[DefaultExecutionOrder(-200)]
[RequireComponent(typeof(HomeEnvLoader))]
public class HomeNavMeshController : MonoBehaviour, IHomeDataRootProvider
{
    [Header("Data Location")]
    [Tooltip("Env(キネクト位置・部屋オブジェクト)を読むデータのルートフォルダ。<ここ>/<実験の名前>/Env/ から読む。\n" +
             "空なら既定の Assets/Data/HomeExperiment。シーン0と同じフォルダを指定する。")]
    [HomeDataRootFolder]
    [SerializeField] private string dataRootDirectory = "";

    [Header("Kinects")]
    [Tooltip("キネクトごとの設定。マシンごとに設定する。表を変えるとシーンのキネクトにすぐ反映される。\n" +
             "Use: オフでそのキネクトのオブジェクトを非表示にし、点群にも使わない(このマシンにつないでいないキネクト)\n" +
             "Device Index: このPCにつながっている台数の中での通し番号 0〜\n" +
             "Sync Mode: このシーンでは使わない(PlaneFinder が1台ずつ撮るため)\n" +
             "表に無いキネクトは触らない。")]
    [SerializeField] private HomeKinectDeviceIndex[] kinectDeviceIndices = HomeKinectDeviceIndex.CreateDefault();

    [Header("NavMesh")]
    [Tooltip("空の場合はシーン内から自動で探す。")]
    [SerializeField] private PlaneFinder planeFinder;
    [SerializeField] private NavPub navPublisher;

    [Header("Fake Floors")]
    [Tooltip("シーン0で置いた補助の床(Env/FakeFloors.json, HomeEnvLoader の Load Fake Floors で再生成)を PlaneFinder の Auxiliary Floor Objects に加える。\n" +
             "PlaneFinder の Auxiliary Floor Objects に直接入れたものもそのまま使う。")]
    [SerializeField] private bool useEnvFakeFloors = true;
    [Tooltip("補助の床を画面に表示する。オフにしても NavMesh には使う(PlaneFinder はメッシュの形だけを見る)。")]
    [SerializeField] private bool showFakeFloors = true;

    [Header("Status")]
    [SerializeField] private bool showStatus = true;

    private HomeEnvLoader env;
    private readonly List<string> usedKinects = new List<string>();
    private int pipelineCount;
    private int fakeFloorCount;
    private float lastPipelineTime = -1f;
    private GUIStyle statusStyle;

    public string DataRootDirectory => dataRootDirectory;

    private void Awake()
    {
        env = GetComponent<HomeEnvLoader>();

        // キネクトの表示/非表示を HomeEnvLoader(Awake で表示中のキネクトの位置姿勢を読む)より先に反映する
        HomeKinectDeviceIndex.ApplyToScene(kinectDeviceIndices);

        if (planeFinder == null)
            planeFinder = FindObjectOfType<PlaneFinder>();

        if (navPublisher == null)
            navPublisher = FindObjectOfType<NavPub>();

        if (planeFinder == null)
        {
            Debug.LogError("[HomeNavMeshController] シーンに PlaneFinder がありません。");
            return;
        }

        ApplyKinectsToPlaneFinder();
    }

    // HomeEnvLoader が Awake で補助の床を再生成した後に渡す(PlaneFinder は撮影時に Auxiliary Floor Objects を見る)
    private void Start()
    {
        if (planeFinder == null || !useEnvFakeFloors)
            return;

        if (planeFinder.auxiliaryFloorObjects == null)
            planeFinder.auxiliaryFloorObjects = new List<MeshFilter>();

        int added = 0;

        foreach (GameObject floor in env.FakeFloors)
        {
            if (floor == null)
                continue;

            foreach (MeshFilter meshFilter in floor.GetComponentsInChildren<MeshFilter>())
            {
                if (planeFinder.auxiliaryFloorObjects.Contains(meshFilter))
                    continue;

                planeFinder.auxiliaryFloorObjects.Add(meshFilter);
                added++;
            }

            foreach (MeshRenderer renderer in floor.GetComponentsInChildren<MeshRenderer>())
                renderer.enabled = showFakeFloors;
        }

        fakeFloorCount = planeFinder.auxiliaryFloorObjects.Count;

        if (added > 0 && !planeFinder.useAuxiliaryFloorPoints)
            Debug.LogWarning("[HomeNavMeshController] PlaneFinder の Use Auxiliary Floor Points がオフのため、補助の床は使われません。");

        Debug.Log($"[HomeNavMeshController] 補助の床を {added} 枚 PlaneFinder に渡しました(合計 {fakeFloorCount} 枚)。");
    }

    private void OnEnable()
    {
        PlaneFinder.OnNavMeshPipelineFinished += HandlePipelineFinished;
    }

    private void OnDisable()
    {
        PlaneFinder.OnNavMeshPipelineFinished -= HandlePipelineFinished;
    }

    // 表示中(Use オン)のキネクトを PlaneFinder の Kinect Sources にする。点群はキネクトの位置姿勢で部屋座標に変換される。
    private void ApplyKinectsToPlaneFinder()
    {
        List<PlaneFinder.KinectCaptureSource> sources = new List<PlaneFinder.KinectCaptureSource>();
        HashSet<int> deviceIndices = new HashSet<int>();
        usedKinects.Clear();

        foreach (HomeKinect kinect in FindObjectsOfType<HomeKinect>())
        {
            HomeKinectDeviceIndex entry = HomeKinectDeviceIndex.Find(kinectDeviceIndices, kinect.KinectId);

            if (entry == null)
            {
                Debug.LogWarning($"[HomeNavMeshController] Kinect{kinect.KinectId} が Kinects の表にないため、点群に使いません。");
                continue;
            }

            if (!deviceIndices.Add(entry.deviceIndex))
            {
                Debug.LogError($"[HomeNavMeshController] Kinect{kinect.KinectId} の deviceIndex {entry.deviceIndex} が他のキネクトと重なっています。Kinects の表を確認してください。");
                continue;
            }

            sources.Add(new PlaneFinder.KinectCaptureSource
            {
                enabled = true,
                deviceIndex = entry.deviceIndex,
                kinectTransform = kinect.transform
            });

            usedKinects.Add($"Kinect{kinect.KinectId}(device {entry.deviceIndex})");
        }

        usedKinects.Sort();
        planeFinder.useMultipleKinects = true;
        planeFinder.kinectSources = sources.ToArray();

        if (sources.Count == 0)
            Debug.LogError("[HomeNavMeshController] 使うキネクトがありません。Kinects の表の Use を確認してください。");
        else
            Debug.Log($"[HomeNavMeshController] 点群に使うキネクト: {string.Join(", ", usedKinects)}");
    }

    private void HandlePipelineFinished()
    {
        pipelineCount++;
        lastPipelineTime = Time.realtimeSinceStartup;
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
        text.AppendLine($"実験: {env.ExperimentName}   平面検知 → NavMesh をMQTTにパブリッシュ");

        if (!env.IsLoaded)
            text.AppendLine("Env(キネクト位置)を読み込めていません。Console を確認してください");

        text.AppendLine($"キネクト: {(usedKinects.Count > 0 ? string.Join(", ", usedKinects) : "なし")}");
        text.AppendLine($"補助の床(FakeFloor): {fakeFloorCount} 枚");

        if (planeFinder != null)
        {
            string auto = planeFinder.autoUpdateCaptureBuildNavMeshAndLinks
                ? $"{Mathf.Max(planeFinder.autoUpdateIntervalSeconds, 1f):F0}秒ごとに自動更新"
                : "自動更新オフ";
            text.AppendLine($"[R] で今すぐ作り直す / {auto}");
        }

        text.AppendLine(pipelineCount == 0
            ? "NavMesh: まだ作っていません"
            : $"NavMesh: {pipelineCount} 回作成 (最後は {Time.realtimeSinceStartup - lastPipelineTime:F0} 秒前)");

        if (navPublisher != null)
            text.AppendLine($"NavPub: {(navPublisher.IsConnected ? "接続中" : "未接続")}");

        GUIContent content = new GUIContent(text.ToString().TrimEnd());
        const float width = 560f;

        GUI.Box(new Rect(10, 10, width, statusStyle.CalcHeight(content, width)), content, statusStyle);
    }
}
