using System.IO;
using UnityEngine;

// 部屋に設置したキネクト1台を表すコンポーネント。キネクトのGameObject(KinectA など)に付ける。
// kinectId は Env/Kinect<ID>_Transform.json と Skeleton/<実験対象者>/<ID>_skeleton.json のファイル名に使う。
public class HomeKinect : MonoBehaviour
{
    [Tooltip("キネクトの識別名。KinectA_Transform.json / A_skeleton.json の「A」の部分。")]
    [SerializeField] private string kinectId = "A";

    public string KinectId => kinectId;

    // このプレイ中に保存済みの位置姿勢ファイルを適用できたか
    public bool PoseLoaded { get; private set; }

    // 読み込んだ位置姿勢ファイルに記録されていたキネクト本体のシリアル番号(無ければ空)
    public string SavedSerialNumber { get; private set; } = "";

    public bool TryLoadPose(string path)
    {
        if (!File.Exists(path))
            return false;

        try
        {
            HomeKinectTransformData data =
                JsonUtility.FromJson<HomeKinectTransformData>(File.ReadAllText(path));

            transform.SetPositionAndRotation(data.position, data.GetRotation());
            PoseLoaded = true;
            SavedSerialNumber = data.serialNumber ?? "";

            Debug.Log($"[HomeKinect] Kinect{kinectId}: 位置姿勢を読み込みました: {path}");
            return true;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[HomeKinect] Kinect{kinectId}: 位置姿勢の読み込みに失敗しました: {path}\n{e}");
            return false;
        }
    }

    public void SavePose(string path, string serialNumber = null, int deviceIndex = -1)
    {
        HomeKinectTransformData data = new HomeKinectTransformData
        {
            position = transform.position,
            rotation = transform.eulerAngles,
            rotationQuaternion = transform.rotation,
            serialNumber = serialNumber ?? "",
            deviceIndex = deviceIndex
        };

        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, JsonUtility.ToJson(data, true));

        Debug.Log($"[HomeKinect] Kinect{kinectId}: 位置姿勢を保存しました: {path}");
    }
}
