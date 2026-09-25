using System.Collections.Generic;
using UnityEngine;

// シーン1(1HeadDirRecording) / シーン2(2HeadDirViewer) / シーン3(3HeadDirFilter) / シーン4(4GazeAnalyze)用。
// シーン開始時に Assets/Data/HomeExperiment/<実験の名前>/Env/ から
//   - 部屋オブジェクト(RoomObjects.json)
//   - 対象物体(TargetObjects.json)  ※シーン0で部屋オブジェクトの下に物体を置いた場合のみ
//   - キネクトの位置姿勢(Kinect<ID>_Transform.json)  ※kinectsを設定した場合のみ
// を再現する。同じGameObjectの録画/再生コンポーネントはここから実験の名前を参照する。
[DefaultExecutionOrder(-100)]
public class HomeEnvLoader : MonoBehaviour, IHomeExperimentNameProvider
{
    [Header("Experiment")]
    [HomeExperimentFolder(HomeExperimentFolderAttribute.Kind.Experiment)]
    [SerializeField] private string experimentName = "";

    [Header("Room Objects")]
    [SerializeField] private bool loadRoomObjects = true;
    [Tooltip("再生成した部屋オブジェクトの親。空ならこのGameObjectの下に作る。")]
    [SerializeField] private Transform roomObjectParent;

    [Header("Kinects")]
    [Tooltip("位置姿勢を再現するキネクト。骨格データを再生するだけのシーンでは空でよい。")]
    [SerializeField] private HomeKinect[] kinects;

    public string ExperimentName => experimentName;

    public bool IsLoaded { get; private set; }

    // 再生成した部屋オブジェクト(Awakeで作られるので、Start以降で参照する)
    public List<GameObject> RoomObjects { get; private set; } = new List<GameObject>();

    // 再生成した対象物体。それぞれ group を持つ HomeTargetObject が付いている
    public List<GameObject> TargetObjects { get; private set; } = new List<GameObject>();

    private void Awake()
    {
        if (!HomeExperimentPaths.IsValidFolderName(experimentName, out string error))
        {
            Debug.LogError($"[HomeEnvLoader] 実験の名前を正しく入力してください。{error}");
            return;
        }

        string envDir = HomeExperimentPaths.GetEnvDirectory(experimentName);

        if (!System.IO.Directory.Exists(envDir))
        {
            Debug.LogError($"[HomeEnvLoader] Envフォルダがありません。先にシーン0で保存してください: {envDir}");
            return;
        }

        bool ok = true;

        if (loadRoomObjects)
        {
            Transform parent = roomObjectParent != null ? roomObjectParent : transform;
            List<GameObject> created = HomeRoomObjectIO.Load(experimentName, parent);

            if (created != null)
                RoomObjects = created;

            ok &= created != null;

            TargetObjects = HomeRoomObjectIO.LoadTargets(experimentName, parent);
        }

        if (kinects != null)
        {
            foreach (HomeKinect kinect in kinects)
            {
                // 使わない(非表示の)キネクトは読み込まない
                if (kinect == null || !kinect.gameObject.activeInHierarchy)
                    continue;

                string path = HomeExperimentPaths.GetKinectTransformPath(experimentName, kinect.KinectId);

                if (!kinect.TryLoadPose(path))
                {
                    Debug.LogError($"[HomeEnvLoader] Kinect{kinect.KinectId} の位置姿勢ファイルがありません: {path}");
                    ok = false;
                }
            }
        }

        IsLoaded = ok;
    }
}
