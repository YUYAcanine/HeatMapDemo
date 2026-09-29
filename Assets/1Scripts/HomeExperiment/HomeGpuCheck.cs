using System;
using System.Runtime.InteropServices;
using UnityEngine;

// Unity とキネクトの骨格推定が使う GPU を確認する(シーン0: HomeEnvSetup / シーン1: HomeSkeletonRecordingController から呼ぶ)。
//
// GPU が2つあるノートPC(内蔵 GPU + NVIDIA など)では、
//   - Unity の描画 … Unity が自分で高性能な GPU を選ぶ(SystemInfo.graphicsDeviceName)
//   - 骨格推定(k4abt / DirectML) … Windows が列挙する0番の GPU(DXGI の EnumAdapters(0))を使う
// と別々に決まる。Windows の設定で Unity を「高パフォーマンス」にしていないと、0番は画面を出している内蔵 GPU になりやすく、
// 骨格推定が内蔵 GPU で動いて処理やメモリが足りず、GPU がリセットされて Unity ごと落ちる(k4abt.dll の c0000005)。
// 対策: Windows の「設定 > システム > ディスプレイ > グラフィック」で Unity.exe を「高パフォーマンス」にする。
public static class HomeGpuCheck
{
    // これより少ないビデオメモリの GPU は内蔵 GPU とみなす(MB)
    private const int MinDedicatedMemoryMB = 2048;

    private const string SettingHint =
        "Windows の「設定 > システム > ディスプレイ > グラフィック」で Unity.exe を「高パフォーマンス」(NVIDIA など)にして、Unity を再起動してください。";

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

        string unityName = SystemInfo.graphicsDeviceName;
        int unityMemoryMB = SystemInfo.graphicsMemorySize;
        string unityInfo = $"{unityName} (ビデオメモリ {unityMemoryMB} MB, {SystemInfo.graphicsDeviceType})";

        bool trackerKnown = TryGetFirstAdapter(out string trackerName, out long trackerMemoryBytes);
        int trackerMemoryMB = (int)(trackerMemoryBytes / (1024 * 1024));
        string trackerInfo = trackerKnown ? $"{trackerName} (ビデオメモリ {trackerMemoryMB} MB)" : "(確認できませんでした)";

        string message = $"[{owner}] GPU  Unity の描画: {unityInfo} / キネクトの骨格推定(0番の GPU): {trackerInfo}";

        if (trackerKnown && IsIntegrated(trackerName, trackerMemoryMB))
        {
            Debug.LogWarning(message + "\nキネクトの骨格推定が内蔵 GPU で動きます。処理が追いつかずに Unity ごと落ちることがあります。" + SettingHint);
        }
        else if (IsIntegrated(unityName, unityMemoryMB))
        {
            Debug.LogWarning(message + "\nUnity が内蔵 GPU で動いています。キネクトの深度エンジン・骨格推定が追いつかずに落ちることがあります。" + SettingHint);
        }
        else
        {
            Debug.Log(message);
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

    // ---- DXGI で0番の GPU を調べる(DirectML が既定で使う GPU) ----

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory(ref Guid riid, out IntPtr factory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdaptersDelegate(IntPtr factory, uint index, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDescDelegate(IntPtr adapter, out DxgiAdapterDesc desc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseDelegate(IntPtr obj);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDesc
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public uint AdapterLuidLow;
        public int AdapterLuidHigh;
    }

    // vtable の番号: IUnknown(0-2), IDXGIObject(3-6), IDXGIFactory::EnumAdapters = 7, IDXGIAdapter::GetDesc = 8
    private const int ReleaseSlot = 2;
    private const int EnumAdaptersSlot = 7;
    private const int GetDescSlot = 8;

    private static T GetMethod<T>(IntPtr comObject, int slot) where T : Delegate
    {
        IntPtr vtable = Marshal.ReadIntPtr(comObject);
        IntPtr method = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(method);
    }

    private static bool TryGetFirstAdapter(out string name, out long dedicatedMemoryBytes)
    {
        name = null;
        dedicatedMemoryBytes = 0;

        if (Application.platform != RuntimePlatform.WindowsEditor && Application.platform != RuntimePlatform.WindowsPlayer)
            return false;

        IntPtr factory = IntPtr.Zero;
        IntPtr adapter = IntPtr.Zero;

        try
        {
            Guid iidFactory = new Guid("7b7166ec-21c7-44ae-b21a-c9ae321ae369"); // IDXGIFactory

            if (CreateDXGIFactory(ref iidFactory, out factory) < 0 || factory == IntPtr.Zero)
                return false;

            if (GetMethod<EnumAdaptersDelegate>(factory, EnumAdaptersSlot)(factory, 0, out adapter) < 0 || adapter == IntPtr.Zero)
                return false;

            if (GetMethod<GetDescDelegate>(adapter, GetDescSlot)(adapter, out DxgiAdapterDesc desc) < 0)
                return false;

            name = desc.Description;
            dedicatedMemoryBytes = (long)desc.DedicatedVideoMemory.ToUInt64();
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[HomeGpuCheck] 0番の GPU を確認できませんでした。\n{e.Message}");
            return false;
        }
        finally
        {
            if (adapter != IntPtr.Zero)
                GetMethod<ReleaseDelegate>(adapter, ReleaseSlot)(adapter);
            if (factory != IntPtr.Zero)
                GetMethod<ReleaseDelegate>(factory, ReleaseSlot)(factory);
        }
    }
}
