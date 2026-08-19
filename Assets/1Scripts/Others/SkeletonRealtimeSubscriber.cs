using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

// SkeletonRealtimePublisher がパブリッシュする Pelvis 座標 (label, x, y, z, confidence, ...)
// をMQTTで受信し、人物ごとにマーカーを空間内に表示する。
// label (person_0, person_1, ...) はPublisher側でトラッキングされた人物IDに対応するため、
// 同じlabelを受信し続ける限り同じマーカーの位置だけを更新する。
public class SkeletonRealtimeSubscriber : MonoBehaviour
{
    [Header("MQTT")]
    [SerializeField] private string mqttHost = "192.168.236.211";
    [SerializeField] private int mqttPort = 1883;
    [SerializeField] private string mqttUsername = "mqtt-user";
    [SerializeField] private string mqttPassword = "401402";
    [SerializeField] private string topic = "pathfinder/person_coordinate";
    [SerializeField] private bool connectOnStart = true;
    [SerializeField] private bool logReceivedMessage = true;

    [Header("Marker")]
    [SerializeField] private Transform markerRoot;
    [SerializeField] private float markerScale = 0.2f;
    [SerializeField] private float markerLifetime = 2f;
    [SerializeField] private Color markerColor = Color.cyan;
    [SerializeField] private Color labelColor = Color.white;
    [SerializeField] private bool showLabel = true;
    [SerializeField] private float labelCharacterSize = 0.08f;

    [Header("Head-Pelvis Highlight")]
    [Tooltip("複数人いる場合、headPelvisDistanceが最も短い人物のPelvisマーカーをこの色にする。")]
    [SerializeField] private Color shortestHeadPelvisColor = Color.red;
    [Tooltip("headPelvisDistanceがこの値未満の場合はトラッキング異常とみなし、色分け対象から除外する。")]
    [SerializeField] private float minValidHeadPelvisDistance = 0.05f;

    [Header("Head-to-Nose Vector")]
    [SerializeField] private bool showHeadToNoseVector = true;
    [SerializeField] private Color headToNoseVectorColor = Color.yellow;
    [SerializeField] private float headToNoseVectorWidth = 0.015f;

    private TcpClient client;
    private NetworkStream stream;
    private readonly byte[] receiveBuffer = new byte[4096];
    private readonly List<byte> mqttBuffer = new List<byte>();
    private readonly Dictionary<string, PersonMarker> markersByLabel = new Dictionary<string, PersonMarker>();
    private float lastMqttSendTime;

    // ロギング用: 受信したトピック名と生JSONペイロードをそのまま流す。
    // MultiIntegrationLoggerシーンのLogger.csはこれを購読してタイムライン付きログに落とす。
    public event Action<string, string> OnMessageReceived;

    public bool IsConnected =>
        client != null && client.Connected;

    // headPelvisDistanceが最も短い(=ハイライトされている)人物。DefineStart等の外部スクリプトが
    // 経路生成のスタート位置を決めるために参照する。
    public string ShortestLabel { get; private set; }
    public Transform ShortestPersonTransform { get; private set; }
    public bool HasShortestPerson => ShortestPersonTransform != null;

    private void Start()
    {
        if (connectOnStart)
            Connect();
    }

    private void Update()
    {
        ReceiveAvailableMessages();
        RemoveStaleMarkers();

        if (IsConnected && Time.unscaledTime - lastMqttSendTime >= 30f)
            SendMqttPacket(0xC0, new List<byte>());
    }

    public void Connect()
    {
        Disconnect();

        try
        {
            client = new TcpClient { NoDelay = true };
            client.Connect(mqttHost, mqttPort);
            stream = client.GetStream();
            SendConnectPacket();
            Debug.Log($"SkeletonRealtimeSubscriber: connecting to MQTT broker {mqttHost}:{mqttPort}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"SkeletonRealtimeSubscriber: connection failed. {ex.GetType().Name}: {ex.Message}");
            Disconnect();
        }
    }

    public void Disconnect()
    {
        if (stream != null && client != null && client.Connected)
        {
            try { stream.WriteByte(0xE0); stream.WriteByte(0x00); }
            catch { }
        }

        stream?.Close();
        client?.Close();
        stream = null;
        client = null;
        mqttBuffer.Clear();
    }

    private void ReceiveAvailableMessages()
    {
        if (client == null || stream == null || !client.Connected)
            return;

        try
        {
            while (stream.DataAvailable)
            {
                int bytesRead = stream.Read(receiveBuffer, 0, receiveBuffer.Length);

                if (bytesRead <= 0)
                {
                    Disconnect();
                    return;
                }

                for (int i = 0; i < bytesRead; i++)
                    mqttBuffer.Add(receiveBuffer[i]);

                ProcessMqttPackets();
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"SkeletonRealtimeSubscriber: receive failed. {ex.GetType().Name}: {ex.Message}");
            Disconnect();
        }
    }

    private void ProcessMqttPackets()
    {
        while (mqttBuffer.Count >= 2)
        {
            if (!TryReadRemainingLength(mqttBuffer, out int remainingLength, out int lengthBytes))
                return;

            int packetLength = 1 + lengthBytes + remainingLength;
            if (mqttBuffer.Count < packetLength)
                return;

            byte header = mqttBuffer[0];
            byte[] body = mqttBuffer.GetRange(1 + lengthBytes, remainingLength).ToArray();
            mqttBuffer.RemoveRange(0, packetLength);
            HandleMqttPacket(header, body);
        }
    }

    private void HandleMqttPacket(byte header, byte[] body)
    {
        int packetType = header >> 4;
        if (packetType == 2)
        {
            if (body.Length < 2 || body[1] != 0)
                throw new IOException("MQTT broker rejected the connection.");
            SendSubscribePacket();
            return;
        }

        if (packetType != 3 || body.Length < 2)
            return;

        int topicLength = (body[0] << 8) | body[1];
        int payloadOffset = 2 + topicLength;
        if (topicLength <= 0 || payloadOffset > body.Length)
            return;

        int qos = (header >> 1) & 0x03;
        if (qos > 0)
            payloadOffset += 2;
        if (payloadOffset > body.Length)
            return;

        string json = Encoding.UTF8.GetString(body, payloadOffset, body.Length - payloadOffset);

        if (logReceivedMessage)
            Debug.Log($"SkeletonRealtimeSubscriber: received payload={json}");

        OnMessageReceived?.Invoke(topic, json);
        TryCreateOrUpdateMarker(json);
    }

    // ライブMQTT受信を経由せず、記録済みログのJSONを直接適用するための公開エントリポイント。
    // MultiIntegrationViewerシーンのSessionPlayer.csが再生時に呼び出す。
    public void ApplyMessage(string json) => TryCreateOrUpdateMarker(json);

    private void TryCreateOrUpdateMarker(string json)
    {
        PersonPositionMessage message;

        try
        {
            message = JsonUtility.FromJson<PersonPositionMessage>(json);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"SkeletonRealtimeSubscriber: json parse failed. {ex.GetType().Name}: {ex.Message}, json={json}");
            return;
        }

        if (string.IsNullOrWhiteSpace(message.label))
        {
            Debug.LogWarning($"SkeletonRealtimeSubscriber: message skipped because label is empty. json={json}");
            return;
        }

        Vector3 position = new Vector3(message.x, message.y, message.z);

        if (!markersByLabel.TryGetValue(message.label, out PersonMarker marker) || marker.root == null)
        {
            marker = CreateMarker(message.label);
            markersByLabel[message.label] = marker;
        }

        marker.root.position = position;
        marker.lastReceivedTime = Time.unscaledTime;
        marker.headPelvisDistance = message.headPelvisDistance;

        if (marker.labelText != null)
            marker.labelText.text = $"{message.label}\nconf={message.confidence:F1}";

        UpdateHeadToNoseVector(marker, message);
        UpdateHighlight();
    }

    private void UpdateHeadToNoseVector(PersonMarker marker, PersonPositionMessage message)
    {
        if (marker.vectorLine == null)
            return;

        bool hasVector = showHeadToNoseVector && message.headToNose.sqrMagnitude > 0f;

        marker.vectorLine.enabled = hasVector;

        if (!hasVector)
            return;

        Vector3 headPosition = message.head;
        Vector3 nosePosition = headPosition + message.headToNose;

        marker.vectorLine.SetPosition(0, headPosition);
        marker.vectorLine.SetPosition(1, nosePosition);
    }

    private void UpdateHighlight()
    {
        string shortestLabel = null;
        float shortestDistance = float.MaxValue;

        foreach (KeyValuePair<string, PersonMarker> entry in markersByLabel)
        {
            float distance = entry.Value.headPelvisDistance;

            if (distance < minValidHeadPelvisDistance)
                continue;

            if (distance < shortestDistance)
            {
                shortestDistance = distance;
                shortestLabel = entry.Key;
            }
        }

        foreach (KeyValuePair<string, PersonMarker> entry in markersByLabel)
        {
            if (entry.Value.sphereRenderer == null)
                continue;

            entry.Value.sphereRenderer.material.color =
                entry.Key == shortestLabel ? shortestHeadPelvisColor : markerColor;
        }

        ShortestLabel = shortestLabel;
        ShortestPersonTransform =
            shortestLabel != null && markersByLabel.TryGetValue(shortestLabel, out PersonMarker shortestMarker)
                ? shortestMarker.root
                : null;
    }

    private PersonMarker CreateMarker(string label)
    {
        Transform parent = markerRoot != null ? markerRoot : transform;

        GameObject root = new GameObject($"PersonMarker_{label}");
        root.transform.SetParent(parent, false);

        GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.name = "Sphere";
        sphere.transform.SetParent(root.transform, false);
        sphere.transform.localPosition = Vector3.zero;
        sphere.transform.localScale = Vector3.one * Mathf.Max(markerScale, 0.01f);

        Renderer sphereRenderer = sphere.GetComponent<Renderer>();
        if (sphereRenderer != null)
            sphereRenderer.material.color = markerColor;

        TextMesh labelText = null;

        if (showLabel)
        {
            GameObject labelObject = new GameObject("Label");
            labelObject.transform.SetParent(sphere.transform, false);
            labelObject.transform.localPosition = Vector3.up * 0.8f;
            labelObject.AddComponent<BillboardLabel>();

            labelText = labelObject.AddComponent<TextMesh>();
            labelText.text = label;
            labelText.characterSize = Mathf.Max(labelCharacterSize, 0.01f);
            labelText.fontSize = 32;
            labelText.anchor = TextAnchor.MiddleCenter;
            labelText.alignment = TextAlignment.Center;
            labelText.color = labelColor;
        }

        LineRenderer vectorLine = root.AddComponent<LineRenderer>();
        vectorLine.useWorldSpace = true;
        vectorLine.positionCount = 2;
        vectorLine.startWidth = headToNoseVectorWidth;
        vectorLine.endWidth = headToNoseVectorWidth;
        vectorLine.material = new Material(Shader.Find("Sprites/Default"));
        vectorLine.startColor = headToNoseVectorColor;
        vectorLine.endColor = headToNoseVectorColor;
        vectorLine.enabled = false;

        return new PersonMarker
        {
            root = root.transform,
            labelText = labelText,
            lastReceivedTime = Time.unscaledTime,
            sphereRenderer = sphereRenderer,
            vectorLine = vectorLine
        };
    }

    private void RemoveStaleMarkers()
    {
        if (markerLifetime <= 0f)
            return;

        List<string> staleLabels = null;

        foreach (KeyValuePair<string, PersonMarker> entry in markersByLabel)
        {
            if (Time.unscaledTime - entry.Value.lastReceivedTime <= markerLifetime)
                continue;

            staleLabels ??= new List<string>();
            staleLabels.Add(entry.Key);
        }

        if (staleLabels == null)
            return;

        foreach (string label in staleLabels)
        {
            if (markersByLabel[label].root != null)
                Destroy(markersByLabel[label].root.gameObject);

            markersByLabel.Remove(label);
        }

        UpdateHighlight();
    }

    private void SendConnectPacket()
    {
        List<byte> body = new List<byte>();
        WriteMqttString(body, "MQTT");
        body.Add(4);
        byte flags = 0x02;
        if (!string.IsNullOrEmpty(mqttUsername)) flags |= 0x80;
        if (!string.IsNullOrEmpty(mqttPassword)) flags |= 0x40;
        body.Add(flags);
        body.Add(0); body.Add(60);
        WriteMqttString(body, "unity-skeleton-subscriber-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        if (!string.IsNullOrEmpty(mqttUsername)) WriteMqttString(body, mqttUsername);
        if (!string.IsNullOrEmpty(mqttPassword)) WriteMqttString(body, mqttPassword);
        SendMqttPacket(0x10, body);
    }

    private void SendSubscribePacket()
    {
        List<byte> body = new List<byte> { 0, 1 };
        WriteMqttString(body, topic);
        body.Add(0);
        SendMqttPacket(0x82, body);
        Debug.Log($"SkeletonRealtimeSubscriber: subscribed to {topic}");
    }

    private void SendMqttPacket(byte header, List<byte> body)
    {
        if (stream == null)
            return;

        List<byte> packet = new List<byte> { header };
        int length = body.Count;
        do
        {
            int encoded = length % 128;
            length /= 128;
            if (length > 0) encoded |= 128;
            packet.Add((byte)encoded);
        } while (length > 0);
        packet.AddRange(body);
        stream.Write(packet.ToArray(), 0, packet.Count);
        lastMqttSendTime = Time.unscaledTime;
    }

    private static void WriteMqttString(List<byte> destination, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        destination.Add((byte)(bytes.Length >> 8));
        destination.Add((byte)bytes.Length);
        destination.AddRange(bytes);
    }

    private static bool TryReadRemainingLength(List<byte> data, out int value, out int bytesUsed)
    {
        value = 0;
        bytesUsed = 0;
        int multiplier = 1;
        for (int i = 1; i < data.Count && bytesUsed < 4; i++)
        {
            byte encoded = data[i];
            value += (encoded & 127) * multiplier;
            bytesUsed++;
            if ((encoded & 128) == 0) return true;
            multiplier *= 128;
        }
        return false;
    }

    private void OnDestroy()
    {
        Disconnect();
    }

    private class PersonMarker
    {
        public Transform root;
        public TextMesh labelText;
        public float lastReceivedTime;
        public Renderer sphereRenderer;
        public LineRenderer vectorLine;
        public float headPelvisDistance;
    }

#pragma warning disable 0649
    [Serializable]
    private class PersonPositionMessage
    {
        public string label;
        public float x;
        public float y;
        public float z;
        public float confidence;
        public int deviceIndex;
        public uint bodyId;
        public float headPelvisDistance;
        public Vector3 head;
        public Vector3 headToNose;
    }
#pragma warning restore 0649

    private class BillboardLabel : MonoBehaviour
    {
        private void LateUpdate()
        {
            Camera targetCamera = Camera.main;

            if (targetCamera == null)
                return;

            transform.rotation = Quaternion.LookRotation(
                transform.position - targetCamera.transform.position,
                Vector3.up);
        }
    }
}
