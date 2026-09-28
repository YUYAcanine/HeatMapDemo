using System;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

// シーン1Multi(1HeadDirRecordingMulti)専用。同じLAN上で複数マシンにこのシーンを開いておき、
// Master側の1台でSpaceキーを押すと、UDPブロードキャストで他の全マシン(Follower)にも
// 記録の開始/終了を伝える。骨格・MKVの記録自体は今までと同じように各マシンがローカルに行う
// (HomeSkeletonRecordingController が行う)。
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

    [Header("Network")]
    [Tooltip("全マシンで同じ値にする。他のUnityプロジェクト・アプリと被らない番号を選ぶ。")]
    [SerializeField] private int port = 50605;
    [Tooltip("UDPは取りこぼす可能性があるため、同じコマンドを複数回送って備える(Start/StopはSkeletonRecordingController側で二重に呼んでも無害)。")]
    [SerializeField] private int sendRepeatCount = 5;
    [SerializeField] private float sendRepeatIntervalSec = 0.02f;

    [Header("Input (Masterのみ使う)")]
    [SerializeField] private KeyCode toggleKey = KeyCode.Space;

    [Header("Status")]
    [SerializeField] private bool showStatus = true;

    private HomeSkeletonRecordingController controller;
    private UdpClient sender;
    private UdpClient receiver;
    private string lastStatus = "";
    private bool isMaster;

    private void Awake()
    {
        controller = GetComponent<HomeSkeletonRecordingController>();

        // HomeSkeletonRecordingController(DefaultExecutionOrder -200)が Kinects の表を反映した後なので、
        // キネクトの表示状態と Sync Mode はここで確定している
        isMaster = role == Role.Master || (role == Role.Auto && HasActiveMasterKinect());

        // Follower は自分の Space キーでは記録を始めず、Master からの信号だけで開始/終了する
        controller.KeyInputEnabled = isMaster;

        Debug.Log($"[HomeMultiMachineRecordingSync] このマシンは {(isMaster ? "Master" : "Follower")} です (Role: {role})。");
    }

    private static bool HasActiveMasterKinect()
    {
        foreach (HomeSkeletonRecorder recorder in FindObjectsOfType<HomeSkeletonRecorder>())
        {
            if (recorder.enabled && recorder.syncMode == Microsoft.Azure.Kinect.Sensor.WiredSyncMode.Master)
                return true;
        }

        return false;
    }

    private void Start()
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
        }
        catch (Exception e)
        {
            Debug.LogError($"[HomeMultiMachineRecordingSync] 受信用ソケット(port {port})を開けませんでした。" +
                           $"他のアプリが同じポートを使っていないか確認してください。\n{e}");
        }
    }

    private void OnDestroy()
    {
        sender?.Close();
        receiver?.Close();
    }

    private void Update()
    {
        ReceiveCommands();
    }

    // HomeSkeletonRecordingController.Update() (同じ toggleKey を見て Start/Stop する) が
    // 先に処理された後の状態を見て送るため、LateUpdate で行う。
    private void LateUpdate()
    {
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

    private IEnumerator BroadcastRepeatedly(string command)
    {
        byte[] data = Encoding.UTF8.GetBytes(command);
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

        try
        {
            sender.Send(data, data.Length, new IPEndPoint(IPAddress.Broadcast, port));
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[HomeMultiMachineRecordingSync] ブロードキャストの送信に失敗しました。\n{e}");
        }
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
                HandleCommand(Encoding.UTF8.GetString(data));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HomeMultiMachineRecordingSync] 受信に失敗しました。\n{e}");
                return;
            }
        }
    }

    private void HandleCommand(string command)
    {
        switch (command)
        {
            case StartCommand:
                controller.StartRecording();
                SetStatus("他マシンからの開始信号を受信しました");
                break;

            case StopCommand:
                controller.StopAndSave();
                SetStatus("他マシンからの終了信号を受信しました");
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
        string text = $"[Multi-Machine Sync: {roleName}, port {port}] {lastStatus}";
        GUI.Label(new Rect(10, 590, 700, 24), text);
    }
}
