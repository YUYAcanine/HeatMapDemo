using System.Collections.Generic;
using System.IO;
using UnityEngine;

// シーン0(0HomeSetup)用。キャリブレーション後に Spaceキー で
//   - 各キネクトの位置姿勢   → Env/Kinect<ID>_Transform.json
//   - 表示中の部屋オブジェクト → Env/RoomObjects.json (+ Env/Meshes)
//   - 部屋オブジェクトのさらに下に置いた物体 → Env/TargetObjects.json (注視などの対象物体。シーン4でスコアを数える)
// を Assets/Data/HomeExperiment/<実験の名前>/Env/ に保存する。
// キャリブレーション自体(ICP.cs / KinectPointCloudOnce.cs)は従来のまま。
public class HomeEnvSetup : MonoBehaviour, IHomeExperimentNameProvider
{
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
    [Tooltip("位置姿勢を保存するキネクト。空の場合はシーン内の HomeKinect を自動で集める。非表示のキネクトは保存しない。")]
    [SerializeField] private HomeKinect[] kinects;

    [Header("Start")]
    [SerializeField] private InitialKinectPose initialKinectPose = InitialKinectPose.EnvThenLegacyCalibration;

    [Header("Input")]
    [SerializeField] private KeyCode saveKey = KeyCode.Space;

    public string ExperimentName => experimentName;

    private void Start()
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

        foreach (HomeKinect kinect in GetKinects(false))
        {
            // 点群を撮ったキネクト本体のシリアル番号も残す(記録するシーンで同じ個体か確認するため)
            KinectPointCloudOnce pointCloud = kinect.GetComponentInChildren<KinectPointCloudOnce>();
            int deviceIndex = pointCloud != null ? pointCloud.deviceIndex : -1;
            string serialNumber = deviceIndex >= 0 ? TryReadSerialNumber(deviceIndex) : null;

            kinect.SavePose(HomeExperimentPaths.GetKinectTransformPath(experimentName, kinect.KinectId), serialNumber, deviceIndex);
            kinectCount++;
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

        if (roots.Count == 0)
            Debug.LogWarning("[HomeEnvSetup] roomRoots が設定されていないため、部屋オブジェクトは保存しません。");
        else
            roomObjectCount = HomeRoomObjectIO.Save(experimentName, roots, out targetObjectCount);

        HomeExperimentPaths.RefreshAssetDatabase();

        Debug.Log($"[HomeEnvSetup] 保存完了: キネクト {kinectCount} 台, 部屋オブジェクト {roomObjectCount} 個, 対象物体 {targetObjectCount} 個 → {envDir}");
#else
        Debug.LogError("[HomeEnvSetup] Envの保存はUnityエディタ上でのみ実行できます。");
#endif
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
