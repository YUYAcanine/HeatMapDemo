using System.Collections.Generic;
using System.IO;
using UnityEngine;

// Calibration シーン用。シーン0(HomeEnvSetup)と同じように、キネクトの表示/非表示・deviceIndex を
// Kinects の表で一括管理し、キャリブレーション後に Spaceキー で
//   - 各キネクトの位置姿勢 → Data/Calibration/Kinect<ID>_Transform.json (KinectTransformSaver の S キーと同じファイル)
//   - 各キネクトで撮った点群 → Data/Calibration/Kinect<ID>_PointCloud.ply (savePointClouds がオンのときだけ)
// を保存する。保存するのは表示中(Use がオン)のキネクトだけ。
// キャリブレーション自体(ICP.cs / KinectPointCloudOnce.cs)と保存の形式(KinectTransformSaver.cs)は従来のまま。
// KinectPointCloudOnce はデバイスを Start で開くので、それより前(Awake)に表の内容を反映する。
[DefaultExecutionOrder(-200)]
public class CalibrationKinectManager : MonoBehaviour
{
    [System.Serializable]
    public class KinectEntry
    {
        [Tooltip("キネクトのオブジェクト(KinectA など)。")]
        public KinectTransformer kinect;
        [Tooltip("オフでそのキネクトのオブジェクトを非表示にする(このマシンにつないでいないキネクト)。")]
        public bool use = true;
        [Tooltip("このPCにつながっている台数の中での通し番号 0〜")]
        public int deviceIndex;
    }

    [Header("Kinects")]
    [Tooltip("キネクトごとの設定。表を変えるとシーンのキネクトにすぐ反映される。表に無いキネクトは触らない。")]
    [SerializeField] private KinectEntry[] kinects = new KinectEntry[0];

    [Header("Save")]
    [Tooltip("各キネクトの KinectPointCloudOnce が撮った点群も Data/Calibration/Kinect<ID>_PointCloud.ply に保存する。")]
    [SerializeField] private bool savePointClouds = false;

    [Header("Input")]
    [SerializeField] private KeyCode saveKey = KeyCode.Space;

    private void Awake()
    {
        ApplyToScene();
        WarnDuplicates();
    }

    // シーン内のキネクトを集めて表を作り直す(今の表示/非表示と deviceIndex を表に取り込む)
    [ContextMenu("Collect Kinects")]
    private void Reset()
    {
        List<KinectEntry> entries = new List<KinectEntry>();

        foreach (KinectTransformer kinect in FindObjectsOfType<KinectTransformer>(true))
        {
            if (kinect.gameObject.scene != gameObject.scene)
                continue;

            KinectPointCloudOnce pointCloud = kinect.GetComponentInChildren<KinectPointCloudOnce>(true);

            entries.Add(new KinectEntry
            {
                kinect = kinect,
                use = kinect.gameObject.activeSelf,
                deviceIndex = pointCloud != null ? pointCloud.deviceIndex : 0
            });
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.kinect.fileName, b.kinect.fileName));
        kinects = entries.ToArray();
    }

#if UNITY_EDITOR
    [System.NonSerialized] private bool kinectTableLoaded;

    // OnValidate の中では SetActive できないので、次のエディタ更新で反映する。
    // シーンを開いたとき・スクリプトの再読み込み時の OnValidate では反映しない(開いただけでシーンを書き換えないため)。
    private void OnValidate()
    {
        if (!kinectTableLoaded)
        {
            kinectTableLoaded = true;
            return;
        }

        if (Application.isPlaying || !gameObject.scene.IsValid())
            return;

        UnityEditor.EditorApplication.delayCall += () =>
        {
            if (Application.isPlaying || this == null)
                return;

            ApplyToScene(target => UnityEditor.Undo.RecordObject(target, "Apply Kinect Settings"));
        };
    }
#endif

    // beforeChange: エディタ上で Undo に記録するため、値を変える直前に対象を渡す(プレイ中は null)
    private void ApplyToScene(System.Action<Object> beforeChange = null)
    {
        foreach (KinectEntry entry in kinects)
        {
            if (entry == null || entry.kinect == null)
                continue;

            GameObject kinectObject = entry.kinect.gameObject;

            if (kinectObject.activeSelf != entry.use)
            {
                beforeChange?.Invoke(kinectObject);
                kinectObject.SetActive(entry.use);
            }

            foreach (KinectPointCloudOnce pointCloud in kinectObject.GetComponentsInChildren<KinectPointCloudOnce>(true))
            {
                if (pointCloud.deviceIndex != entry.deviceIndex)
                {
                    beforeChange?.Invoke(pointCloud);
                    pointCloud.deviceIndex = entry.deviceIndex;
                }
            }
        }
    }

    // 使う(表示中の)キネクトどうしで deviceIndex が重なっていないか確認する
    private void WarnDuplicates()
    {
        Dictionary<int, string> used = new Dictionary<int, string>();

        foreach (KinectEntry entry in kinects)
        {
            if (entry == null || entry.kinect == null || !entry.kinect.gameObject.activeInHierarchy)
                continue;

            if (used.TryGetValue(entry.deviceIndex, out string other))
                Debug.LogError($"[CalibrationKinectManager] {other} と {entry.kinect.name} が同じ deviceIndex {entry.deviceIndex} になっています。Kinects の表を確認してください。");
            else
                used.Add(entry.deviceIndex, entry.kinect.name);
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

    [ContextMenu("Save")]
    public void Save()
    {
#if UNITY_EDITOR
        int kinectCount = 0;
        int pointCloudCount = 0;

        foreach (KinectEntry entry in kinects)
        {
            if (entry == null || entry.kinect == null || !entry.kinect.gameObject.activeInHierarchy)
                continue;

            KinectTransformSaver saver = entry.kinect.GetComponentInChildren<KinectTransformSaver>();

            if (saver == null)
            {
                Debug.LogWarning($"[CalibrationKinectManager] {entry.kinect.name}: KinectTransformSaver が無いため保存しません。");
                continue;
            }

            saver.SaveTransform();
            kinectCount++;

            if (savePointClouds && SavePointCloud(entry.kinect, saver))
                pointCloudCount++;
        }

        HomeExperimentPaths.RefreshAssetDatabase();

        Debug.Log($"[CalibrationKinectManager] 保存完了: キネクト {kinectCount} 台, 点群 {pointCloudCount} 個");
#else
        Debug.LogError("[CalibrationKinectManager] 保存はUnityエディタ上でのみ実行できます。");
#endif
    }

    // 点群を、位置姿勢を保存したオブジェクト(KinectTransformSaver)のローカル座標(メートル, Unityの軸)でバイナリPLYに書き出す。
    // Transform.json の位置姿勢を掛けるとワールド座標になる。
    private static bool SavePointCloud(KinectTransformer kinect, KinectTransformSaver saver)
    {
        KinectPointCloudOnce pointCloud = kinect.GetComponentInChildren<KinectPointCloudOnce>();
        MeshFilter meshFilter = pointCloud != null ? pointCloud.GetComponent<MeshFilter>() : null;
        Mesh mesh = meshFilter != null ? meshFilter.sharedMesh : null;

        if (mesh == null || mesh.vertexCount == 0)
        {
            Debug.LogWarning($"[CalibrationKinectManager] {kinect.name}: 点群がないため保存しません。");
            return false;
        }

        Vector3[] vertices = mesh.vertices;
        Matrix4x4 toKinect = saver.transform.worldToLocalMatrix * pointCloud.transform.localToWorldMatrix;

        for (int i = 0; i < vertices.Length; i++)
            vertices[i] = toKinect.MultiplyPoint3x4(vertices[i]);

        string name = Path.GetFileNameWithoutExtension(saver.fileName);

        if (name.EndsWith("_Transform"))
            name = name.Substring(0, name.Length - "_Transform".Length);

        string path = Path.Combine(Application.dataPath, "Data/Calibration", name + "_PointCloud.ply");

        HomePointCloudIO.Save(path, vertices,
            $"{kinect.name}, device {pointCloud.deviceIndex}, kinect local coordinates (Unity axes, meters)");

        Debug.Log($"[CalibrationKinectManager] {kinect.name}: 点群 {vertices.Length} 点を保存しました: {path}");
        return true;
    }
}
