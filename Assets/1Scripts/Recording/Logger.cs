using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

// MultiIntegrationLoggerシーン専用のロガー。
// NavSub(navmesh) / SkeletonRealtimeSubscriber(骨格) / Subscriber(物体) が受信した
// 生JSONペイロードを、受信した瞬間のローカル時刻(録画開始からの経過秒)を軸にした
// 共通タイムラインでNDJSON(1行1JSON)として保存する。
// 元のペイロードはパースせずそのまま data フィールドに埋め込むため、後で同じ受信処理
// (各SubscriberのHandleMqttPacket相当)にそのまま読み戻せば再現できる。
public class Logger : MonoBehaviour
{
    [Header("Sources (空の場合はシーン内から自動収集)")]
    [SerializeField] private NavSub navSub;
    [SerializeField] private SkeletonRealtimeSubscriber skeletonSubscriber;
    [SerializeField] private SkeletonRealtimeSubscriber_FULL skeletonFullSubscriber;
    [SerializeField] private Subscriber objectSubscriber;

    [Header("Output")]
    [Tooltip("空の場合は Assets/Data/Logs に保存する。")]
    [SerializeField] private string outputDirectory = "";
    [SerializeField] private bool recordOnStart = true;
    [Tooltip("この秒数ごとにディスクへFlushする。")]
    [SerializeField] private float flushInterval = 1f;

    private StreamWriter writer;
    private float recordingStartTime;
    private float lastFlushTime;

    public bool IsRecording { get; private set; }
    public string CurrentLogPath { get; private set; }

    private void Start()
    {
        if (navSub == null)
            navSub = FindObjectOfType<NavSub>();

        if (skeletonSubscriber == null)
            skeletonSubscriber = FindObjectOfType<SkeletonRealtimeSubscriber>();

        if (skeletonFullSubscriber == null)
            skeletonFullSubscriber = FindObjectOfType<SkeletonRealtimeSubscriber_FULL>();

        if (objectSubscriber == null)
            objectSubscriber = FindObjectOfType<Subscriber>();

        if (recordOnStart)
            StartRecording();
    }

    public void StartRecording()
    {
        if (IsRecording)
            return;

        string directory = string.IsNullOrWhiteSpace(outputDirectory)
            ? Path.Combine(Application.dataPath, "Data", "Logs")
            : outputDirectory;

        if (!Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        CurrentLogPath = Path.Combine(directory, $"session_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
        writer = new StreamWriter(CurrentLogPath, false, new UTF8Encoding(false)) { AutoFlush = false };

        recordingStartTime = Time.unscaledTime;
        lastFlushTime = recordingStartTime;
        IsRecording = true;

        if (navSub != null)
            navSub.OnMessageReceived += HandleNavMeshMessage;

        if (skeletonSubscriber != null)
            skeletonSubscriber.OnMessageReceived += HandleSkeletonMessage;

        if (skeletonFullSubscriber != null)
            skeletonFullSubscriber.OnMessageReceived += HandleSkeletonFullMessage;

        if (objectSubscriber != null)
            objectSubscriber.OnMessageReceived += HandleObjectMessage;

        Debug.Log($"Logger: recording started. path={CurrentLogPath}");
    }

    public void StopRecording()
    {
        if (!IsRecording)
            return;

        if (navSub != null)
            navSub.OnMessageReceived -= HandleNavMeshMessage;

        if (skeletonSubscriber != null)
            skeletonSubscriber.OnMessageReceived -= HandleSkeletonMessage;

        if (skeletonFullSubscriber != null)
            skeletonFullSubscriber.OnMessageReceived -= HandleSkeletonFullMessage;

        if (objectSubscriber != null)
            objectSubscriber.OnMessageReceived -= HandleObjectMessage;

        writer?.Flush();
        writer?.Close();
        writer = null;
        IsRecording = false;

        Debug.Log($"Logger: recording stopped. path={CurrentLogPath}");
    }

    private void Update()
    {
        if (!IsRecording || writer == null)
            return;

        if (Time.unscaledTime - lastFlushTime >= flushInterval)
        {
            writer.Flush();
            lastFlushTime = Time.unscaledTime;
        }
    }

    private void HandleNavMeshMessage(string topic, string json) => WriteLine("navmesh", topic, json);
    private void HandleSkeletonMessage(string topic, string json) => WriteLine("skeleton", topic, json);
    private void HandleSkeletonFullMessage(string topic, string json) => WriteLine("skeleton_full", topic, json);
    private void HandleObjectMessage(string topic, string json) => WriteLine("object", topic, json);

    private void WriteLine(string type, string topic, string json)
    {
        if (writer == null || string.IsNullOrEmpty(json))
            return;

        float t = Time.unscaledTime - recordingStartTime;

        writer.WriteLine(
            "{\"t\":" + t.ToString("F4", CultureInfo.InvariantCulture) +
            ",\"type\":\"" + type + "\"" +
            ",\"topic\":\"" + EscapeJsonString(topic) + "\"" +
            ",\"data\":" + json + "}");
    }

    private static string EscapeJsonString(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        StringBuilder builder = new StringBuilder(value.Length);

        foreach (char c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                default: builder.Append(c); break;
            }
        }

        return builder.ToString();
    }

    private void OnDestroy()
    {
        StopRecording();
    }
}
