using System.Collections.Generic;
using Microsoft.Azure.Kinect.Sensor;
using UnityEngine;

// キネクト1台ごとの設定(使うか・deviceIndex・syncMode)。マシンごとに設定する。
// シーン0(HomeEnvSetup) / シーン1・1Multi(HomeSkeletonRecordingController) のインスペクター(HomeExperiment)で一括管理し、
//   - エディタ上: 表を変えるとすぐ、キネクトのオブジェクトの表示/非表示と各コンポーネントの値に反映する
//   - プレイ開始時(Awake): 他のスクリプトより先に同じ内容を反映する(デバイスは各コンポーネントの Start で開く)
//
// use        … オフにするとそのキネクトのオブジェクトを非表示にする(このマシンにつないでいないキネクト)
// deviceIndex … Azure Kinect SDK の Device.Open に渡す番号。「このPCにつながっている台数の中での通し番号(0〜台数-1)」
// syncMode   … 同期ケーブルでつないだときの役割(シーン1のみ使う)
//   Master      … 同期信号を出す1台
//   Subordinate … Master の同期信号が来るまで撮らない(Standalone のままだと Master を待たずに撮り始める)
// 同期するときは Subordinate 側のマシンを先に再生し、Master 側を後から再生する(Azure Kinect の仕様)。
[System.Serializable]
public class HomeKinectDeviceIndex
{
    public enum SyncMode
    {
        // HomeSkeletonRecorder に設定されている値のまま
        KeepComponent,
        Standalone,
        Master,
        Subordinate
    }

    public string kinectId;
    public bool use = true;
    public int deviceIndex;
    public SyncMode syncMode = SyncMode.KeepComponent;

    public HomeKinectDeviceIndex()
    {
    }

    public HomeKinectDeviceIndex(string kinectId, int deviceIndex)
    {
        this.kinectId = kinectId;
        this.deviceIndex = deviceIndex;
    }

    public static HomeKinectDeviceIndex[] CreateDefault() => new[]
    {
        new HomeKinectDeviceIndex("A", 0),
        new HomeKinectDeviceIndex("B", 1),
        new HomeKinectDeviceIndex("C", 2),
        new HomeKinectDeviceIndex("D", 3)
    };

    public static HomeKinectDeviceIndex Find(HomeKinectDeviceIndex[] table, string kinectId)
    {
        if (table != null)
        {
            foreach (HomeKinectDeviceIndex entry in table)
            {
                if (entry != null && entry.kinectId == kinectId)
                    return entry;
            }
        }

        return null;
    }

    // シーン内の全キネクト(非表示含む)に表の内容を反映する。表に無いキネクトは触らない。
    // beforeChange: エディタ上で Undo に記録するため、値を変える直前に対象を渡す(プレイ中は null)
    public static void ApplyToScene(HomeKinectDeviceIndex[] table, System.Action<Object> beforeChange = null)
    {
        foreach (HomeKinect kinect in Object.FindObjectsOfType<HomeKinect>(true))
        {
            HomeKinectDeviceIndex entry = Find(table, kinect.KinectId);

            if (entry == null)
                continue;

            if (kinect.gameObject.activeSelf != entry.use)
            {
                beforeChange?.Invoke(kinect.gameObject);
                kinect.gameObject.SetActive(entry.use);
            }

            foreach (KinectPointCloudOnce pointCloud in kinect.GetComponentsInChildren<KinectPointCloudOnce>(true))
            {
                if (pointCloud.deviceIndex != entry.deviceIndex)
                {
                    beforeChange?.Invoke(pointCloud);
                    pointCloud.deviceIndex = entry.deviceIndex;
                }
            }

            foreach (HomeSkeletonRecorder recorder in kinect.GetComponentsInChildren<HomeSkeletonRecorder>(true))
            {
                WiredSyncMode syncMode = entry.syncMode == SyncMode.KeepComponent
                    ? recorder.syncMode
                    : ToWiredSyncMode(entry.syncMode);

                if (recorder.deviceIndex != entry.deviceIndex || recorder.syncMode != syncMode)
                {
                    beforeChange?.Invoke(recorder);
                    recorder.deviceIndex = entry.deviceIndex;
                    recorder.syncMode = syncMode;
                }
            }
        }
    }

    // 使う(表示中の)キネクトどうしで deviceIndex が重なっていないか確認する。
    // skip: デバイスを開かないキネクト(保存済みの点群を使う基準キネクトなど)
    public static void WarnDuplicates(string owner, HomeKinect skip = null)
    {
        Dictionary<int, string> used = new Dictionary<int, string>();

        foreach (HomeKinect kinect in Object.FindObjectsOfType<HomeKinect>())
        {
            if (kinect == skip)
                continue;

            int deviceIndex;
            KinectPointCloudOnce pointCloud = kinect.GetComponentInChildren<KinectPointCloudOnce>();
            HomeSkeletonRecorder recorder = kinect.GetComponentInChildren<HomeSkeletonRecorder>();

            if (recorder != null && recorder.enabled)
                deviceIndex = recorder.deviceIndex;
            else if (pointCloud != null && pointCloud.enabled)
                deviceIndex = pointCloud.deviceIndex;
            else
                continue;

            if (used.TryGetValue(deviceIndex, out string other))
                Debug.LogError($"[{owner}] Kinect{other} と Kinect{kinect.KinectId} が同じ deviceIndex {deviceIndex} になっています。HomeExperiment の Kinects の表を確認してください。");
            else
                used.Add(deviceIndex, kinect.KinectId);
        }
    }

    public static WiredSyncMode ToWiredSyncMode(SyncMode mode)
    {
        switch (mode)
        {
            case SyncMode.Master:
                return WiredSyncMode.Master;
            case SyncMode.Subordinate:
                return WiredSyncMode.Subordinate;
            default:
                return WiredSyncMode.Standalone;
        }
    }

#if UNITY_EDITOR
    // OnValidate から呼ぶ。OnValidate の中では SetActive できないので、次のエディタ更新で反映する。
    // シーンを開いたとき・スクリプトの再読み込み時の OnValidate では反映しない(開いただけでシーンを書き換えないため)。
    // loaded: 呼び出し側の [NonSerialized] の bool(再読み込みで false に戻る)
    public static void ApplyToSceneInEditor(HomeKinectDeviceIndex[] table, MonoBehaviour owner, ref bool loaded)
    {
        if (!loaded)
        {
            loaded = true;
            return;
        }

        if (Application.isPlaying || owner == null || !owner.gameObject.scene.IsValid())
            return;

        UnityEditor.EditorApplication.delayCall += () =>
        {
            if (Application.isPlaying || owner == null)
                return;

            ApplyToScene(table, target => UnityEditor.Undo.RecordObject(target, "Apply Kinect Settings"));
        };
    }
#endif
}
