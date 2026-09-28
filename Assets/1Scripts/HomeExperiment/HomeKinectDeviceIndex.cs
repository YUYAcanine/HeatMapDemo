using System.Collections.Generic;
using UnityEngine;

// キネクトID と deviceIndex(Azure Kinect SDK の Device.Open に渡す番号)の対応。
// deviceIndex は「そのPCにつながっている台数の中での通し番号(0〜台数-1)」なので、マシンごとに設定する。
// シーン0(HomeEnvSetup) / シーン1(HomeSkeletonRecordingController) のインスペクターで一括管理し、
// Awake で各キネクトの KinectPointCloudOnce / HomeSkeletonRecorder に配る(それらはデバイスを Start で開く)。
[System.Serializable]
public class HomeKinectDeviceIndex
{
    public string kinectId;
    public int deviceIndex;

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

    public static bool TryFind(HomeKinectDeviceIndex[] table, string kinectId, out int deviceIndex)
    {
        if (table != null)
        {
            foreach (HomeKinectDeviceIndex entry in table)
            {
                if (entry != null && entry.kinectId == kinectId)
                {
                    deviceIndex = entry.deviceIndex;
                    return true;
                }
            }
        }

        deviceIndex = -1;
        return false;
    }

    // 使う(表示中の)キネクトどうしで deviceIndex が重なっていないか確認する。
    // kinects: (キネクトID, deviceIndex) の組
    public static void WarnDuplicates(IEnumerable<KeyValuePair<string, int>> kinects, string owner)
    {
        Dictionary<int, string> used = new Dictionary<int, string>();

        foreach (KeyValuePair<string, int> kinect in kinects)
        {
            if (used.TryGetValue(kinect.Value, out string other))
                Debug.LogError($"[{owner}] Kinect{other} と Kinect{kinect.Key} が同じ deviceIndex {kinect.Value} になっています。Kinect Device Indices を確認してください。");
            else
                used.Add(kinect.Value, kinect.Key);
        }
    }
}
