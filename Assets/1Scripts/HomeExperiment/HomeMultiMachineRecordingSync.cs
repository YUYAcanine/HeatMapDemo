using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Microsoft.Azure.Kinect.Sensor;
using UnityEngine;

// シーン1Multi(1HeadDirRecordingMulti)専用。複数マシンにこのシーンを開いておき、
// Master側の1台でSpaceキーを押すと、他の全マシン(Follower)も記録を開始/終了する。
// 骨格・MKVの記録自体は今までと同じように各マシンがローカルに行う(HomeSkeletonRecordingController が行う)。
//
// 他のマシンへの伝え方(Trigger)は2通り:
//   - Sync Cable(既定): キネクトの同期ケーブルだけで伝える。ネットワークは使わない。
//       Master のマシン … 記録開始まで Master のキネクトのカメラを止めておき、Spaceキーで記録開始と同時に動かす。
//                         もう一度 Spaceキーで記録を終了し、カメラを止める。
//       Follower のマシン … Subordinate のキネクトにフレームが届き始めたら記録を開始し、
//                         Sync Cable Stop Timeout 秒フレームが届かなければ終了して保存する。
//       ・Follower のキネクトは必ず Subordinate にする(Standalone だと再生してすぐ記録が始まる)
//       ・Master のマシンで Spaceキーを押す前に、Follower のマシンを再生しておく
//         (Master のキネクトは Space まで動かないので、再生する順番自体はどちらが先でもよい)
//       ・Master のキネクトは記録するまで動かないので、記録前の映像・骨格の確認はできない
//       ・Follower は最初のフレームが届いてから記録を始めるので、先頭の数フレームを取りこぼすことがある
//       ・記録の0秒(骨格・MKV の recordingTimeSec)は、各キネクトの「カメラが動き出して最初に届いたフレーム」にする。
//         Master が動き出した同じ瞬間なので、全マシンの骨格・映像の0秒がそろう
//         (Master のキネクトは最初の数フレームを渡さないので、Master の時刻には Master Stream Start Offset Sec を足して保存する)
//   - Network: UDPブロードキャストで開始/終了の信号を送る(同じLAN内・ファイアウォールでUDPを許可しておく)。
//
// 使い方:
//   - Role で操作役(Master)を決める。既定の Auto なら、HomeExperiment の Kinects の表で
//     Sync Mode を Master にしたキネクトを表示しているマシンが操作役になる(それ以外は Follower)。
//     同期ケーブルを使わないとき(全台 Standalone)や、キネクトをつながないマシンで操作するときは Master/Follower を手で選ぶ。
//     操作役は全マシンで1台だけにする。
//   - ここでの Master/Follower は「記録の開始/終了を伝える側/受ける側」で、キネクトの同期(Sync Mode)とは別の役割
//   - 全マシンで同じ port を使う(同じLAN/スイッチ内であること)
//   - 実験の名前・実験対象者は各マシンで同じ値を手動で入力しておく(このスクリプトはそこは同期しない)
//   - 撮影後、各マシンの Skeleton/<実験対象者>/ フォルダの中身を1つにまとめれば、
//     今までと同じように Tools/GazePipeline などの解析が行える
[RequireComponent(typeof(HomeSkeletonRecordingController))]
public class HomeMultiMachineRecordingSync : MonoBehaviour
{
    private const string StartCommand = "HOME_REC_START";
    private const string StopCommand = "HOME_REC_STOP";

    public enum Role
    {
        // 表示中のキネクトに Sync Mode が Master のものがあれば Master、無ければ Follower
        Auto,
        // Spaceキーで記録を開始/終了し、他の全マシンに伝える(全マシンで1台だけ)
        Master,
        // 自分のSpaceキーでは記録せず、Master からの信号だけで開始/終了する
        Follower
    }

    [Header("Role")]
    [Tooltip("Auto: 表示中のキネクトに Sync Mode が Master のものがあれば Master、無ければ Follower。\n" +
             "Master: Spaceキーで記録を開始/終了し、他の全マシンに伝える(全マシンで1台だけ)。\n" +
             "Follower: Master からの信号だけで記録を開始/終了する。\n" +
             "同期ケーブルを使わない(全台 Standalone)ときや、キネクトをつながないマシンで操作するときは Master/Follower を選ぶ。")]
    [SerializeField] private Role role = Role.Auto;

    public enum Trigger
    {
        // キネクトの同期ケーブルだけで記録の開始/終了を合わせる
        SyncCable,
        // UDPブロードキャストで記録の開始/終了を伝える
        Network
    }

    [Header("Trigger")]
    [Tooltip("Sync Cable: 同期ケーブルだけで合わせる(Master のキネクトは記録開始まで止めておき、Follower はフレームが届いたら記録する)。\n" +
             "Network: UDPで開始/終了の信号を送る。\n全マシンで同じにする。")]
    [SerializeField] private Trigger trigger = Trigger.SyncCable;
    [Tooltip("Sync Cable のとき、Follower がこの秒数フレームを受け取らなかったら記録を終了して保存する。")]
    [SerializeField] private float syncCableStopTimeoutSec = 2f;
    [Tooltip("Sync Cable のとき、Master のキネクトの記録の時刻(骨格・MKV の recordingTimeSec)に足す秒数。\n" +
             "Master のキネクトは動き出してから最初の数フレームを渡さないため、そのままだと Master だけ Subordinate より遅れる。\n" +
             "初期値 0.125 秒は 2026-09-29 の test1 で測ったずれ(骨格 0.100/0.133 秒, 画像 0.167/0.100 秒)の平均。Master のマシンで使う。")]
    [SerializeField] private float masterStreamStartOffsetSec = 0.125f;

    [Header("Network")]
    [Tooltip("全マシンで同じ値にする。他のUnityプロジェクト・アプリと被らない番号を選ぶ。")]
    [SerializeField] private int port = 50605;
    [Tooltip("UDPは取りこぼす可能性があるため、同じコマンドを複数回送って備える(Start/StopはSkeletonRecordingController側で二重に呼んでも無害)。")]
    [SerializeField] private int sendRepeatCount = 5;
    [SerializeField] private float sendRepeatIntervalSec = 0.02f;
    [Tooltip("ブロードキャストが届かないネットワーク(Wi-Fiの設定などで止められている)向けに、直接送る他マシンのIPアドレス(Masterのみ使う)。\n" +
             "各マシンのIPは再生開始時のログ「このマシンのIP」で確認できる。")]
    [SerializeField] private string[] additionalTargets;

    [Header("Input (Masterのみ使う)")]
    [SerializeField] private KeyCode toggleKey = KeyCode.Space;

    [Header("Status")]
    [SerializeField] private bool showStatus = true;

    private HomeSkeletonRecordingController controller;
    private UdpClient sender;
    private UdpClient receiver;
    private string lastStatus = "";
    private bool isMaster;
    private List<IPAddress> targets;
    private readonly string instanceId = Guid.NewGuid().ToString("N");

    // Sync Cable 用
    private readonly List<HomeSkeletonRecorder> deferredRecorders = new List<HomeSkeletonRecorder>();
    private bool wasRecording;
    private double lastAutoStopClock = -1;
    private bool waitForFramesToStop;

    private void Awake()
    {
        controller = GetComponent<HomeSkeletonRecordingController>();

        // HomeSkeletonRecordingController(DefaultExecutionOrder -200)が Kinects の表を反映した後なので、
        // キネクトの表示状態と Sync Mode はここで確定している
        isMaster = role == Role.Master || (role == Role.Auto && GetActiveRecorders(WiredSyncMode.Master).Count > 0);

        // Follower は自分の Space キーでは記録を始めず、Master からの信号(Sync Cable ではフレームの到着)だけで開始/終了する
        controller.KeyInputEnabled = isMaster;

        Debug.Log($"[HomeMultiMachineRecordingSync] このマシンは {(isMaster ? "Master" : "Follower")} です (Role: {role}, Trigger: {trigger})。");

        if (trigger == Trigger.SyncCable)
            SetupSyncCable();
    }

    private void SetupSyncCable()
    {
        // 記録の0秒を「カメラが動き出して最初に届いたフレーム」にする(Master が動き出した同じ瞬間なので、全マシンでそろう)。
        // Master は Spaceキーを押してからカメラを動かし、Follower は最初のフレームが届いてから記録を始めるので、
        // 「記録を開始した瞬間」を0秒にするとマシンごとにずれる。
        foreach (HomeSkeletonRecorder recorder in GetActiveRecorders(null))
        {
            if (recorder.syncMode == WiredSyncMode.Standalone)
                continue;

            recorder.alignToStreamStart = true;

            // Master は最初の数フレームを渡さないので、その分だけ時刻を足して Subordinate とそろえる
            if (recorder.syncMode == WiredSyncMode.Master)
            {
                recorder.streamStartOffsetSec = masterStreamStartOffsetSec;
                Debug.Log($"[HomeMultiMachineRecordingSync] Kinect{recorder.KinectId} (Master) の記録の時刻に {masterStreamStartOffsetSec:F3} 秒足して保存します。");
            }
        }

        if (isMaster)
        {
            // Master のキネクトは記録開始まで止めておく(Start より前に設定する)。
            // 止めている間は同期信号が出ないので、Subordinate のキネクトにもフレームが来ない。
            deferredRecorders.AddRange(GetActiveRecorders(WiredSyncMode.Master));

            foreach (HomeSkeletonRecorder recorder in deferredRecorders)
                recorder.deferCameraStart = true;

            if (deferredRecorders.Count == 0)
                Debug.LogWarning("[HomeMultiMachineRecordingSync] Sync Cable ですが、このマシンに Sync Mode が Master のキネクトがありません。" +
                                 "他のマシンの記録は開始されません(Kinects の表を確認してください)。");
        }
        else
        {
            foreach (HomeSkeletonRecorder recorder in GetActiveRecorders(null))
            {
                if (recorder.syncMode != WiredSyncMode.Subordinate)
                    Debug.LogError($"[HomeMultiMachineRecordingSync] Sync Cable の Follower ですが、Kinect{recorder.KinectId} の Sync Mode が {recorder.syncMode} です。" +
                                   "Subordinate にしないと、Master を待たずに記録が始まります(Kinects の表を確認してください)。");
            }
        }
    }

    // syncMode が null なら表示中の全レコーダー
    private static List<HomeSkeletonRecorder> GetActiveRecorders(WiredSyncMode? syncMode)
    {
        List<HomeSkeletonRecorder> result = new List<HomeSkeletonRecorder>();

        foreach (HomeSkeletonRecorder recorder in FindObjectsOfType<HomeSkeletonRecorder>())
        {
            if (recorder.enabled && (syncMode == null || recorder.syncMode == syncMode))
                result.Add(recorder);
        }

        return result;
    }

    private void Start()
    {
        if (trigger == Trigger.Network)
            StartNetwork();
    }

    private void StartNetwork()
    {
        try
        {
            sender = new UdpClient { EnableBroadcast = true };
        }
        catch (Exception e)
        {
            Debug.LogError($"[HomeMultiMachineRecordingSync] 送信用ソケットを開けませんでした。\n{e}");
        }

        try
        {
            receiver = new UdpClient(port);
            Debug.Log($"[HomeMultiMachineRecordingSync] port {port} で受信待ちしています。このマシンのIP: {string.Join(", ", GetLocalAddresses())}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[HomeMultiMachineRecordingSync] 受信用ソケット(port {port})を開けませんでした。" +
                           $"他のアプリが同じポートを使っていないか確認してください。\n{e}");
        }

        if (isMaster)
        {
            targets = GetSendTargets();
            Debug.Log($"[HomeMultiMachineRecordingSync] 開始/終了の信号の送り先: {string.Join(", ", targets)} (port {port})");
        }
    }

    private void OnDestroy()
    {
        sender?.Close();
        receiver?.Close();
    }

    private void Update()
    {
        if (trigger == Trigger.Network)
            ReceiveCommands();
        else if (!isMaster)
            WatchSyncCableFrames();
    }

    // Sync Cable の Follower: Subordinate のキネクトにフレームが届き始めたら記録を開始し、届かなくなったら終了する
    private void WatchSyncCableFrames()
    {
        double now = HomeSkeletonRecorder.ClockSeconds;
        double last = -1;

        foreach (HomeSkeletonRecorder recorder in GetActiveRecorders(null))
        {
            if (recorder.IsReady)
                last = Math.Max(last, recorder.LastCaptureClock);
        }

        if (!controller.IsRecording)
        {
            // 開始できなかった(実験対象者が未入力など)ときは、一度フレームが止まるまで試さない
            if (waitForFramesToStop)
            {
                if (last < 0 || now - last > syncCableStopTimeoutSec)
                    waitForFramesToStop = false;

                return;
            }

            // 前の記録を終えた後に新しく届いたフレームだけを合図にする
            if (last >= 0 && last > lastAutoStopClock && now - last < 0.5)
            {
                controller.StartRecording();

                if (controller.IsRecording)
                {
                    SetStatus("Master のキネクトのフレームが届いたので記録を開始しました");
                }
                else
                {
                    waitForFramesToStop = true;
                    SetStatus("フレームが届きましたが記録を開始できませんでした(コンソールのエラーを確認してください)");
                }
            }
        }
        else if (last < 0 || now - last > syncCableStopTimeoutSec)
        {
            controller.StopAndSave();
            lastAutoStopClock = now;
            SetStatus($"フレームが {syncCableStopTimeoutSec} 秒届かないので記録を終了しました");
        }
    }

    // HomeSkeletonRecordingController.Update() (同じ toggleKey を見て Start/Stop する) が
    // 先に処理された後の状態を見て送るため、LateUpdate で行う。
    private void LateUpdate()
    {
        if (trigger == Trigger.SyncCable)
        {
            if (isMaster)
                UpdateSyncCableCameras();

            return;
        }

        if (!isMaster || !Input.GetKeyDown(toggleKey))
            return;

        if (controller.IsRecording)
        {
            StartCoroutine(BroadcastRepeatedly(StartCommand));
            SetStatus("他マシンへ開始信号を送信しました");
        }
        else
        {
            StartCoroutine(BroadcastRepeatedly(StopCommand));
            SetStatus("他マシンへ終了信号を送信しました");
        }
    }

    // Sync Cable の Master: 記録の開始/終了に合わせて Master のキネクトのカメラを動かす/止める
    private void UpdateSyncCableCameras()
    {
        bool recording = controller.IsRecording;

        if (recording == wasRecording)
            return;

        wasRecording = recording;

        foreach (HomeSkeletonRecorder recorder in deferredRecorders)
        {
            if (recording)
                recorder.StartCameras();
            else
                recorder.StopCameras();
        }

        SetStatus(recording
            ? "Master のキネクトを動かしました(同期ケーブルで他のマシンの記録が始まります)"
            : "Master のキネクトを止めました(他のマシンは数秒後に記録を終了します)");
    }

    private IEnumerator BroadcastRepeatedly(string command)
    {
        // 自分が送った信号を自分で受け取ったときに無視できるよう、送り主の識別子を付ける
        byte[] data = Encoding.UTF8.GetBytes($"{command}|{instanceId}");
        int count = Mathf.Max(1, sendRepeatCount);

        for (int i = 0; i < count; i++)
        {
            Broadcast(data);

            if (i < count - 1)
                yield return new WaitForSecondsRealtime(sendRepeatIntervalSec);
        }
    }

    private void Broadcast(byte[] data)
    {
        if (sender == null)
            return;

        foreach (IPAddress target in targets ?? (targets = GetSendTargets()))
        {
            try
            {
                sender.Send(data, data.Length, new IPEndPoint(target, port));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HomeMultiMachineRecordingSync] {target} への送信に失敗しました。\n{e.Message}");
            }
        }
    }

    // 255.255.255.255 だけだと、ネットワークが複数(有線+Wi-Fi など)あるマシンでは1つにしか出ないことがあるので、
    // 各ネットワークのブロードキャストアドレス(192.168.1.255 など)と、手で指定したIPにも送る
    private List<IPAddress> GetSendTargets()
    {
        List<IPAddress> result = new List<IPAddress> { IPAddress.Broadcast };

        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                foreach (UnicastIPAddressInformation info in nic.GetIPProperties().UnicastAddresses)
                {
                    if (info.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;

                    byte[] address = info.Address.GetAddressBytes();
                    byte[] mask = info.IPv4Mask != null && !info.IPv4Mask.Equals(IPAddress.Any)
                        ? info.IPv4Mask.GetAddressBytes()
                        : new byte[] { 255, 255, 255, 0 };

                    for (int i = 0; i < 4; i++)
                        address[i] = (byte)(address[i] | ~mask[i]);

                    AddUnique(result, new IPAddress(address));
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[HomeMultiMachineRecordingSync] ネットワークの一覧を取得できませんでした(255.255.255.255 にだけ送ります)。\n{e.Message}");
        }

        if (additionalTargets != null)
        {
            foreach (string text in additionalTargets)
            {
                if (IPAddress.TryParse(text?.Trim() ?? "", out IPAddress address))
                    AddUnique(result, address);
                else if (!string.IsNullOrWhiteSpace(text))
                    Debug.LogWarning($"[HomeMultiMachineRecordingSync] Additional Targets のIPアドレスが読めません: {text}");
            }
        }

        return result;
    }

    private static void AddUnique(List<IPAddress> list, IPAddress address)
    {
        if (!list.Contains(address))
            list.Add(address);
    }

    private static List<string> GetLocalAddresses()
    {
        List<string> result = new List<string>();

        try
        {
            foreach (IPAddress address in Dns.GetHostAddresses(Dns.GetHostName()))
            {
                if (address.AddressFamily == AddressFamily.InterNetwork)
                    result.Add(address.ToString());
            }
        }
        catch (Exception)
        {
        }

        return result;
    }

    private void ReceiveCommands()
    {
        if (receiver == null)
            return;

        while (receiver.Available > 0)
        {
            IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);

            try
            {
                byte[] data = receiver.Receive(ref remote);
                HandleCommand(Encoding.UTF8.GetString(data), remote);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HomeMultiMachineRecordingSync] 受信に失敗しました。\n{e}");
                return;
            }
        }
    }

    private void HandleCommand(string message, IPEndPoint remote)
    {
        string[] parts = message.Split('|');
        string command = parts[0];

        // 自分が送ったブロードキャストは自分にも届くので無視する
        if (parts.Length > 1 && parts[1] == instanceId)
            return;

        switch (command)
        {
            case StartCommand:
                if (controller.IsRecording)
                    return;

                controller.StartRecording();
                SetStatus($"{remote.Address} からの開始信号を受信しました");
                break;

            case StopCommand:
                if (!controller.IsRecording)
                    return;

                controller.StopAndSave();
                SetStatus($"{remote.Address} からの終了信号を受信しました");
                break;
        }
    }

    private void SetStatus(string message)
    {
        lastStatus = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        Debug.Log($"[HomeMultiMachineRecordingSync] {message}");
    }

    private void OnGUI()
    {
        if (!showStatus)
            return;

        string roleName = (isMaster ? "Master" : "Follower") + (role == Role.Auto ? " (Auto)" : "");
        string triggerName = trigger == Trigger.SyncCable ? "Sync Cable" : $"Network port {port}";
        string text = $"[Multi-Machine Sync: {roleName}, {triggerName}] {lastStatus}";
        GUI.Label(new Rect(10, 590, 700, 24), text);

        // Sync Cable の Follower: フレームが届いているかを確認できるようにする
        if (trigger == Trigger.SyncCable && !isMaster)
        {
            double now = HomeSkeletonRecorder.ClockSeconds;
            StringBuilder frames = new StringBuilder("最後のフレームから:");

            foreach (HomeSkeletonRecorder recorder in GetActiveRecorders(null))
            {
                double last = recorder.LastCaptureClock;
                frames.Append(last < 0 ? $" Kinect{recorder.KinectId}=未受信" : $" Kinect{recorder.KinectId}={now - last:F1}秒");
            }

            GUI.Label(new Rect(10, 614, 700, 24), frames.ToString());
        }
    }
}
