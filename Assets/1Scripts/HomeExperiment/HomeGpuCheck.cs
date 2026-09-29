using UnityEngine;

// Unity が使っている GPU を確認する(シーン0: HomeEnvSetup / シーン1: HomeSkeletonRecordingController から呼ぶ)。
//
// GPU が2つあるノートPC(内蔵 GPU + NVIDIA など)では、Unity が画面を出している内蔵 GPU で動くことがある。
// そのとき、Azure Kinect の深度エンジンと骨格推定(k4abt, DirectML の0番の GPU)も内蔵 GPU で動き、
// 処理やメモリが足りずに GPU がリセットされて Unity ごと落ちる(k4abt.dll の c0000005 で落ちるクラッシュ)。
// 対策: Windows の「設定 > システム > ディスプレイ > グラフィック」で Unity.exe を「高パフォーマンス」にする。
public static class HomeGpuCheck
{
    // これより少ないビデオメモリの GPU は内蔵 GPU とみなす(MB)
    private const int MinDedicatedMemoryMB = 2048;

    private static bool checkedThisPlay;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetOnPlay()
    {
        checkedThisPlay = false;
    }

    public static void LogOnce(string owner)
    {
        if (checkedThisPlay)
            return;

        checkedThisPlay = true;

        string name = SystemInfo.graphicsDeviceName;
        int memoryMB = SystemInfo.graphicsMemorySize;
        string info = $"{name} ({SystemInfo.graphicsDeviceVendor}, ビデオメモリ {memoryMB} MB, {SystemInfo.graphicsDeviceType})";

        if (IsIntegrated(name, memoryMB))
        {
            Debug.LogWarning($"[{owner}] Unity が内蔵 GPU で動いています: {info}\n" +
                             "キネクトの深度エンジンと骨格推定も内蔵 GPU で動き、処理が追いつかずに Unity ごと落ちることがあります。" +
                             "Windows の「設定 > システム > ディスプレイ > グラフィック」で Unity.exe を「高パフォーマンス」(NVIDIA など)にして、Unity を再起動してください。");
        }
        else
        {
            Debug.Log($"[{owner}] Unity が使っている GPU: {info}");
        }
    }

    private static bool IsIntegrated(string name, int memoryMB)
    {
        string n = name.ToLowerInvariant();

        bool integratedName =
            n.Contains("radeon(tm) graphics") || n.Contains("radeon graphics") || n.Contains("vega") ||
            (n.Contains("intel") && !n.Contains("arc")) ||
            n.Contains("microsoft basic render");

        return integratedName || (memoryMB > 0 && memoryMB < MinDedicatedMemoryMB);
    }
}
