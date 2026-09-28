using System.Collections.Generic;
using System.IO;
using UnityEngine;

// シーン0(0HomeSetup)用。キャリブレーション後に Spaceキー で
//   - 各キネクトの位置姿勢   → Env/Kinect<ID>_Transform.json
//   - 各キネクトで撮った点群 → Env/Kinect<ID>_PointCloud.ply (キネクトのローカル座標。Transform.json の位置姿勢を掛けるとワールド座標)
//   - 表示中の部屋オブジェクト → Env/RoomObjects.json (+ Env/Meshes)
//   - 部屋オブジェクトのさらに下に置いた物体 → Env/TargetObjects.json (注視などの対象物体。シーン4でスコアを数える)
//     (部屋オブジェクト・対象物体は saveRoomObjects がオンのときだけ)
// を Assets/Data/HomeExperiment/<実験の名前>/Env/ に保存する。
// キャリブレーション自体(ICP.cs / KinectPointCloudOnce.cs)は従来のまま。
//
// ICPの基準にするキネクト(referenceKinectId, 既定は A)は、Env に点群が保存済みなら
// ライブで撮らずに保存済みの点群を表示し、各 ICP の referenceRoot をそれに差し替える。
// (Aをつないでいないマシンでも B/C/D の位置合わせができる。保存時は基準キネクトのファイルを上書きしない)
public class HomeEnvSetup : MonoBehaviour, IHomeExperimentNameProvider
{
    public enum ReferencePointCloudSource
    {
        // Envに基準キネクトの点群が保存済みならそれを使い、無ければライブで撮る
        SavedEnvIfExists,
        // 従来どおり基準キネクトでライブで撮る
        LiveCapture
    }

    public enum InitialKinectPose
    {
        // この実験のEnvに保存済みならそれを、無ければ従来の Data/Calibration のファイルを使う
        EnvThenLegacyCalibration,
        // この実験のEnvに保存済みのものだけを使う(無ければシーン上の配置のまま)
        EnvOnly,
        // シーン上の配置のまま始める
        None
    }

    [Header("Experiment")]
    [Tooltip("Assets/Data/HomeExperiment/<実験の名前>/Env/ に保存する。")]
    [HomeExperimentFolder(HomeExperimentFolderAttribute.Kind.Experiment)]
    [SerializeField] private string experimentName = "";

    [Header("Targets")]
    [Tooltip("保存する部屋オブジェクトの親。この配下で表示中かつメッシュを持つオブジェクトを保存する。")]
    [SerializeField] private Transform[] roomRoots;
    [Tooltip("部屋オブジェクト・対象物体(RoomObjects.json / TargetObjects.json / Meshes)も保存する。\n" +
             "複数マシンでEnvを共有する場合は、部屋を担当する1台だけオンにする(他のマシンで上書きしないため)。")]
    [SerializeField] private bool saveRoomObjects = true;
    [Tooltip("位置姿勢を保存するキネクト。空の場合はシーン内の HomeKinect を自動で集める。非表示のキネクトは保存しない。")]
    [SerializeField] private HomeKinect[] kinects;

    [Header("Point Cloud")]
    [Tooltip("各キネクトの KinectPointCloudOnce が撮った点群も Env/Kinect<ID>_PointCloud.ply に保存する。")]
    [SerializeField] private bool savePointClouds = true;

    [Header("ICP Reference")]
    [Tooltip("ICPの基準にするキネクトのID。各 ICP の referenceRoot がこのキネクトになっているものを差し替える。")]
    [SerializeField] private string referenceKinectId = "A";
    [SerializeField] private ReferencePointCloudSource referencePointCloudSource = ReferencePointCloudSource.SavedEnvIfExists;

    [Header("Start")]
    [SerializeField] private InitialKinectPose initialKinectPose = InitialKinectPose.EnvThenLegacyCalibration;

    [Header("Input")]
    [SerializeField] private KeyCode saveKey = KeyCode.Space;

    public string ExperimentName => experimentName;

    // 保存済みの点群を基準に使っている基準キネクト(使っていなければ null)
    private HomeKinect savedReferenceKinect;

    private void Awake()
    {
        if (referencePointCloudSource != ReferencePointCloudSource.SavedEnvIfExists ||
            !HomeExperimentPaths.IsValidFolderName(experimentName, out _))
            return;

        HomeKinect reference = FindReferenceKinect();

        if (reference == null)
            return;

        string path = HomeExperimentPaths.GetKinectPointCloudPath(experimentName, reference.KinectId);

        if (!File.Exists(path))
        {
            Debug.Log($"[HomeEnvSetup] Kinect{reference.KinectId} の保存済み点群が無いため、ライブで撮ります: {path}");
            return;
        }

        // KinectPointCloudOnce は Start で撮るので、その前に止めてデバイスを開かないようにする
        foreach (KinectPointCloudOnce live in reference.GetComponentsInChildren<KinectPointCloudOnce>(true))
            live.enabled = false;

        savedReferenceKinect = reference;
    }

    private void Start()
    {
        LoadInitialKinectPoses();

        if (savedReferenceKinect != null)
            ShowSavedReferencePointCloud(savedReferenceKinect);
    }

    private void LoadInitialKinectPoses()
    {
        if (initialKinectPose == InitialKinectPose.None)
            return;

        bool hasExperimentName = HomeExperimentPaths.IsValidFolderName(experimentName, out _);

        foreach (HomeKinect kinect in GetKinects(true))
        {
            if (hasExperimentName &&
                kinect.TryLoadPose(HomeExperimentPaths.GetKinectTransformPath(experimentName, kinect.KinectId)))
                continue;

            if (initialKinectPose == InitialKinectPose.EnvThenLegacyCalibration)
            {
                string legacyPath = Path.Combine(
                    Application.dataPath, "Data", "Calibration", $"Kinect{kinect.KinectId}_Transform.json");

                kinect.TryLoadPose(legacyPath);
            }
        }
    }

    // 保存済みの点群を基準キネクトの保存済み位置姿勢に置き、ICP の基準をそれに差し替える。
    // 基準キネクト本体が非表示(このマシンにつないでいない)でも表示できるよう、キネクトの子ではなく別のオブジェクトにする。
    private void ShowSavedReferencePointCloud(HomeKinect reference)
    {
        string path = HomeExperimentPaths.GetKinectPointCloudPath(experimentName, reference.KinectId);

        if (!HomePointCloudIO.TryLoad(path, out List<Vector3> points))
            return;

        // 点群はキネクトのローカル座標なので、一緒に保存した位置姿勢に合わせる
        if (!reference.TryLoadPose(HomeExperimentPaths.GetKinectTransformPath(experimentName, reference.KinectId)))
            Debug.LogWarning($"[HomeEnvSetup] Kinect{reference.KinectId} の位置姿勢ファイルが無いため、シーン上の配置に点群を置きます。");

        GameObject cloud = new GameObject($"Kinect{reference.KinectId}_SavedPointCloud");
        cloud.transform.SetPositionAndRotation(reference.transform.position, reference.transform.rotation);
        cloud.transform.localScale = reference.transform.lossyScale;

        cloud.AddComponent<MeshFilter>().sharedMesh =
            HomePointCloudIO.CreatePointMesh(points, cloud.name);

        MeshRenderer renderer = cloud.AddComponent<MeshRenderer>();
        KinectPointCloudOnce live = reference.GetComponentInChildren<KinectPointCloudOnce>(true);
        MeshRenderer liveRenderer = live != null ? live.GetComponent<MeshRenderer>() : null;

        if (liveRenderer != null)
            renderer.sharedMaterials = liveRenderer.sharedMaterials;

        int icpCount = 0;

        foreach (ICP icp in FindObjectsOfType<ICP>(true))
        {
            if (icp.ReferenceRoot != null && icp.ReferenceRoot.IsChildOf(reference.transform))
            {
                icp.ReferenceRoot = cloud.transform;
                icpCount++;
            }
        }

        Debug.Log($"[HomeEnvSetup] Kinect{reference.KinectId} の保存済み点群 {points.Count} 点を表示し、ICP {icpCount} 個の基準にしました: {path}");
    }

    private HomeKinect FindReferenceKinect()
    {
        foreach (HomeKinect kinect in GetKinects(true))
        {
            if (kinect.KinectId == referenceKinectId)
                return kinect;
        }

        Debug.LogWarning($"[HomeEnvSetup] 基準キネクト Kinect{referenceKinectId} がシーンにありません。");
        return null;
    }

    private void Update()
    {
        if (Input.GetKeyDown(saveKey))
            Save();
    }

    private void LateUpdate()
    {
        HomeExperimentPaths.ClearUISelection();
    }

    [ContextMenu("Save Env")]
    public void Save()
    {
        if (!HomeExperimentPaths.IsValidFolderName(experimentName, out string error))
        {
            Debug.LogError($"[HomeEnvSetup] 実験の名前を正しく入力してください。{error}");
            return;
        }

#if UNITY_EDITOR
        string envDir = HomeExperimentPaths.GetEnvDirectory(experimentName);
        Directory.CreateDirectory(envDir);

        int kinectCount = 0;
        int pointCloudCount = 0;

        foreach (HomeKinect kinect in GetKinects(false))
        {
            // 保存済みの点群を基準に使っている場合、基準キネクトはこのプレイで撮っていないので上書きしない
            if (kinect == savedReferenceKinect)
            {
                Debug.Log($"[HomeEnvSetup] Kinect{kinect.KinectId} は保存済みの点群を基準に使っているため、位置姿勢と点群を上書きしません。");
                continue;
            }

            // 点群を撮ったキネクト本体のシリアル番号も残す(記録するシーンで同じ個体か確認するため)
            KinectPointCloudOnce pointCloud = kinect.GetComponentInChildren<KinectPointCloudOnce>();
            int deviceIndex = pointCloud != null ? pointCloud.deviceIndex : -1;
            string serialNumber = deviceIndex >= 0 ? TryReadSerialNumber(deviceIndex) : null;

            kinect.SavePose(HomeExperimentPaths.GetKinectTransformPath(experimentName, kinect.KinectId), serialNumber, deviceIndex);
            kinectCount++;

            if (savePointClouds &&
                SavePointCloud(kinect, pointCloud, HomeExperimentPaths.GetKinectPointCloudPath(experimentName, kinect.KinectId)))
                pointCloudCount++;
        }

        List<Transform> roots = new List<Transform>();

        if (roomRoots != null)
        {
            foreach (Transform root in roomRoots)
            {
                if (root != null)
                    roots.Add(root);
            }
        }

        int roomObjectCount = 0;
        int targetObjectCount = 0;

        if (!saveRoomObjects)
            Debug.Log("[HomeEnvSetup] Save Room Objects がオフのため、部屋オブジェクト・対象物体は保存しません(Envの既存ファイルはそのまま)。");
        else if (roots.Count == 0)
            Debug.LogWarning("[HomeEnvSetup] roomRoots が設定されていないため、部屋オブジェクトは保存しません。");
        else
            roomObjectCount = HomeRoomObjectIO.Save(experimentName, roots, out targetObjectCount);

        HomeExperimentPaths.RefreshAssetDatabase();

        Debug.Log($"[HomeEnvSetup] 保存完了: キネクト {kinectCount} 台, 点群 {pointCloudCount} 個, 部屋オブジェクト {roomObjectCount} 個, 対象物体 {targetObjectCount} 個 → {envDir}");
#else
        Debug.LogError("[HomeEnvSetup] Envの保存はUnityエディタ上でのみ実行できます。");
#endif
    }

    // 点群を HomeKinect のローカル座標(メートル, Unityの軸)でバイナリPLYに書き出す。
    // 点群オブジェクトがキネクトの子で少しずれて置かれていても、キネクト基準の座標に直して保存する。
    private static bool SavePointCloud(HomeKinect kinect, KinectPointCloudOnce pointCloud, string path)
    {
        MeshFilter meshFilter = pointCloud != null ? pointCloud.GetComponent<MeshFilter>() : null;
        Mesh mesh = meshFilter != null ? meshFilter.sharedMesh : null;

        if (mesh == null || mesh.vertexCount == 0)
        {
            Debug.LogWarning($"[HomeEnvSetup] Kinect{kinect.KinectId}: 点群がないため保存しません。");
            return false;
        }

        Vector3[] vertices = mesh.vertices;
        Matrix4x4 toKinect = kinect.transform.worldToLocalMatrix * pointCloud.transform.localToWorldMatrix;

        for (int i = 0; i < vertices.Length; i++)
            vertices[i] = toKinect.MultiplyPoint3x4(vertices[i]);

        HomePointCloudIO.Save(path, vertices,
            $"kinect {kinect.KinectId}, device {pointCloud.deviceIndex}, kinect local coordinates (Unity axes, meters)");

        Debug.Log($"[HomeEnvSetup] Kinect{kinect.KinectId}: 点群 {vertices.Length} 点を保存しました: {path}");
        return true;
    }

    // KinectPointCloudOnce は点群を撮った後にデバイスを閉じているので、ここで開き直してシリアル番号だけ読む
    private static string TryReadSerialNumber(int deviceIndex)
    {
        try
        {
            using (Microsoft.Azure.Kinect.Sensor.Device device = Microsoft.Azure.Kinect.Sensor.Device.Open(deviceIndex))
                return device.SerialNum;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[HomeEnvSetup] device {deviceIndex} のシリアル番号を読めませんでした(シリアル番号なしで保存します)。\n{e.Message}");
            return null;
        }
    }

    private IEnumerable<HomeKinect> GetKinects(bool includeInactive)
    {
        HomeKinect[] targets = kinects != null && kinects.Length > 0
            ? kinects
            : FindObjectsOfType<HomeKinect>(true);

        foreach (HomeKinect kinect in targets)
        {
            if (kinect != null && (includeInactive || kinect.gameObject.activeInHierarchy))
                yield return kinect;
        }
    }
}
