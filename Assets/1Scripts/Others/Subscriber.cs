using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

public class Subscriber : MonoBehaviour
{
    [Header("MQTT")]
    [SerializeField] private string mqttHost = "192.168.236.211";
    [SerializeField] private int mqttPort = 1883;
    [SerializeField] private string mqttUsername = "mqtt-user";
    [SerializeField] private string mqttPassword = "401402";
    [SerializeField] private string[] topics =
    {
        "pathfinder/object_coordinate",
        "pathfinder/person_coordinate",
        "pathfinder/target_coordinate"
    };
    [SerializeField] private bool connectOnStart = true;
    [SerializeField] private bool logReceivedMessage = true;

    [Header("Object Marker")]
    [SerializeField] private bool createObjectMarkers = true;
    [SerializeField] private Transform markerRoot;
    [SerializeField] private float markerScale = 0.12f;
    [SerializeField] private float labelVerticalPadding = 0.015f;
    [SerializeField] private float labelCharacterSize = 0.08f;
    [SerializeField] private float coordinateScale = 1.0f;
    [SerializeField] private Vector3 coordinateOffset = Vector3.zero;
    [SerializeField] private Color markerColor = Color.yellow;
    [SerializeField] private Color labelColor = Color.white;

    private TcpClient client;
    private NetworkStream stream;
    private readonly byte[] receiveBuffer = new byte[4096];
    private readonly List<byte> mqttBuffer = new List<byte>();
    private readonly Dictionary<string, GameObject> markersByLabel = new Dictionary<string, GameObject>();
    private float lastMqttSendTime;

    // ロギング用: 受信したトピック名と生JSONペイロードをそのまま流す。
    // MultiIntegrationLoggerシーンのLogger.csはこれを購読してタイムライン付きログに落とす。
    public event Action<string, string> OnMessageReceived;

    public string LastMessage { get; private set; }
    public string LatestLabel { get; private set; }
    public Transform LatestMarkerTransform { get; private set; }
    public Vector3 LatestMarkerPosition { get; private set; }
    public Vector3 LatestMarkerLocalPosition { get; private set; }
    public int MarkerVersion { get; private set; }
    public IReadOnlyDictionary<string, GameObject> MarkersByLabel => markersByLabel;
    public bool HasLatestMarker =>
        LatestMarkerTransform != null;

    public bool IsConnected =>
        client != null &&
        client.Connected;

    private void Start()
    {
        if (connectOnStart)
            Connect();
    }

    private void Update()
    {
        ReceiveAvailableMessages();

        if (IsConnected && Time.unscaledTime - lastMqttSendTime >= 30f)
            SendMqttPacket(0xC0, new List<byte>());
    }

    public void Connect()
    {
        Disconnect();

        try
        {
            client = new TcpClient();
            client.NoDelay = true;
            client.Connect(mqttHost, mqttPort);
            stream = client.GetStream();
            SendConnectPacket();

            Debug.Log($"Subscriber: connected to MQTT broker {mqttHost}:{mqttPort}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Subscriber: connection failed. {ex.GetType().Name}: {ex.Message}");
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
        if (client == null ||
            stream == null ||
            !client.Connected)
        {
            return;
        }

        try
        {
            while (stream.DataAvailable)
            {
                int bytesRead =
                    stream.Read(receiveBuffer, 0, receiveBuffer.Length);

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
            Debug.LogWarning($"Subscriber: receive failed. {ex.GetType().Name}: {ex.Message}");
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

        string topic = Encoding.UTF8.GetString(body, 2, topicLength);
        int qos = (header >> 1) & 0x03;
        if (qos > 0)
            payloadOffset += 2;
        if (payloadOffset > body.Length)
            return;

        string json = Encoding.UTF8.GetString(body, payloadOffset, body.Length - payloadOffset);
        LastMessage = json;
        if (logReceivedMessage)
            Debug.Log($"Subscriber: received topic={topic}, payload={json}");
        OnMessageReceived?.Invoke(topic, json);
        if (createObjectMarkers)
            TryCreateOrUpdateMarker(json);
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
        WriteMqttString(body, "unity-" + Guid.NewGuid().ToString("N").Substring(0, 12));
        if (!string.IsNullOrEmpty(mqttUsername)) WriteMqttString(body, mqttUsername);
        if (!string.IsNullOrEmpty(mqttPassword)) WriteMqttString(body, mqttPassword);
        SendMqttPacket(0x10, body);
    }

    private void SendSubscribePacket()
    {
        List<byte> body = new List<byte> { 0, 1 };
        foreach (string topic in topics)
        {
            if (string.IsNullOrWhiteSpace(topic)) continue;
            WriteMqttString(body, topic);
            body.Add(0);
        }
        SendMqttPacket(0x82, body);
        Debug.Log($"Subscriber: subscribed to {string.Join(", ", topics)}");
    }

    private void SendMqttPacket(byte header, List<byte> body)
    {
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

    // ライブMQTT受信を経由せず、記録済みログのJSONを直接適用するための公開エントリポイント。
    // MultiIntegrationViewerシーンのSessionPlayer.csが再生時に呼び出す。
    public void ApplyMessage(string json) => TryCreateOrUpdateMarker(json);

    private void TryCreateOrUpdateMarker(
        string json)
    {
        ObjectTopicMessage message;

        try
        {
            message =
                JsonUtility.FromJson<ObjectTopicMessage>(json);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Subscriber: marker json parse failed. {ex.GetType().Name}: {ex.Message}, json={json}");
            return;
        }

        string label =
            GetLabel(message);

        if (string.IsNullOrWhiteSpace(label))
        {
            Debug.LogWarning($"Subscriber: marker skipped because label is empty. json={json}");
            return;
        }

        label = label.Trim();

        if (!TryGetPosition(json, message, out Vector3 rawPosition))
        {
            Debug.LogWarning($"Subscriber: marker skipped because position was not found. json={json}");
            return;
        }

        Vector3 localPosition =
            rawPosition * coordinateScale + coordinateOffset;

        bool markerAlreadyExists =
            markersByLabel.TryGetValue(label, out GameObject marker) &&
            marker != null;

        if (markerAlreadyExists)
        {
            marker.transform.localPosition = localPosition;
        }
        else
        {
            marker = CreateMarker(label, localPosition);
        }

        LatestLabel = label;
        LatestMarkerTransform = marker.transform;
        LatestMarkerPosition = marker.transform.position;
        LatestMarkerLocalPosition = localPosition;
        MarkerVersion++;

        if (logReceivedMessage)
        {
            Debug.Log(
                $"Subscriber: marker {(markerAlreadyExists ? "updated" : "created")}. label={label}, rawPosition={rawPosition}, inspectorPosition={localPosition}, worldPosition={marker.transform.position}");
        }
    }

    private GameObject CreateMarker(
        string label,
        Vector3 localPosition)
    {
        Transform parent =
            markerRoot != null
                ? markerRoot
                : transform;

        GameObject markerRootObject =
            new GameObject($"ObjectMarker_{label}");
        markerRootObject.transform.SetParent(parent, false);
        markerRootObject.transform.localPosition = localPosition;

        GameObject marker =
            GameObject.CreatePrimitive(PrimitiveType.Sphere);
        marker.name = "Sphere";
        marker.transform.SetParent(markerRootObject.transform, false);
        marker.transform.localPosition = Vector3.zero;
        marker.transform.localScale = Vector3.one * Mathf.Max(markerScale, 0.01f);

        Renderer markerRenderer =
            marker.GetComponent<Renderer>();

        if (markerRenderer != null)
            markerRenderer.material.color = markerColor;

        GameObject labelObject =
            new GameObject("Label");
        labelObject.transform.SetParent(marker.transform, false);
        labelObject.transform.localPosition =
            Vector3.up * (0.5f + Mathf.Max(labelVerticalPadding, 0f) / Mathf.Max(markerScale, 0.01f));
        labelObject.AddComponent<BillboardLabel>();

        TextMesh textMesh =
            labelObject.AddComponent<TextMesh>();
        textMesh.text = label;
        textMesh.characterSize = Mathf.Max(labelCharacterSize, 0.01f);
        textMesh.fontSize = 32;
        textMesh.anchor = TextAnchor.MiddleCenter;
        textMesh.alignment = TextAlignment.Center;
        textMesh.color = labelColor;

        MeshRenderer textRenderer =
            labelObject.GetComponent<MeshRenderer>();

        if (textRenderer != null)
            textRenderer.sortingOrder = 100;

        markersByLabel[label] = markerRootObject;
        return markerRootObject;
    }

    private void ClearMarkers()
    {
        foreach (GameObject marker in markersByLabel.Values)
        {
            if (marker != null)
                Destroy(marker);
        }

        markersByLabel.Clear();
        LatestMarkerTransform = null;
    }

    private string GetLabel(
        ObjectTopicMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.label))
            return message.label;

        if (!string.IsNullOrWhiteSpace(message.objectName))
            return message.objectName;

        if (!string.IsNullOrWhiteSpace(message.object_name))
            return message.object_name;

        if (!string.IsNullOrWhiteSpace(message.name))
            return message.name;

        return "object";
    }

    private bool TryGetPosition(
        string json,
        ObjectTopicMessage message,
        out Vector3 position)
    {
        if (TryGetPositionFromJsonText(json, out position))
            return true;

        if (message.position != null &&
            message.position.HasValue())
        {
            position = message.position.ToVector3();
            return true;
        }

        if (message.coordinate != null &&
            message.coordinate.HasValue())
        {
            position = message.coordinate.ToVector3();
            return true;
        }

        if (message.coordinates != null &&
            message.coordinates.HasValue())
        {
            position = message.coordinates.ToVector3();
            return true;
        }

        if (message.point != null &&
            message.point.HasValue())
        {
            position = message.point.ToVector3();
            return true;
        }

        if (message.center != null &&
            message.center.HasValue())
        {
            position = message.center.ToVector3();
            return true;
        }

        if (message.xyz != null &&
            message.xyz.Length >= 3)
        {
            position = new Vector3(message.xyz[0], message.xyz[1], message.xyz[2]);
            return true;
        }

        if (!Mathf.Approximately(message.x, 0f) ||
            !Mathf.Approximately(message.y, 0f) ||
            !Mathf.Approximately(message.z, 0f))
        {
            position = new Vector3(message.x, message.y, message.z);
            return true;
        }

        position = Vector3.zero;
        return false;
    }

    private bool TryGetPositionFromJsonText(
        string json,
        out Vector3 position)
    {
        const string number =
            @"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?";
        string arrayPattern =
            @"[""'](?:position|coordinate|coordinates|point|center|xyz)[""']\s*:\s*\[\s*(" + number + @")\s*,\s*(" + number + @")\s*,\s*(" + number + @")\s*\]";
        Match arrayMatch =
            Regex.Match(json, arrayPattern, RegexOptions.IgnoreCase);

        if (arrayMatch.Success &&
            TryParseFloat(arrayMatch.Groups[1].Value, out float arrayX) &&
            TryParseFloat(arrayMatch.Groups[2].Value, out float arrayY) &&
            TryParseFloat(arrayMatch.Groups[3].Value, out float arrayZ))
        {
            position = new Vector3(arrayX, arrayY, arrayZ);
            return true;
        }

        if (TryGetNamedNumber(json, "x", out float x) &&
            TryGetNamedNumber(json, "y", out float y) &&
            TryGetNamedNumber(json, "z", out float z))
        {
            position = new Vector3(x, y, z);
            return true;
        }

        position = Vector3.zero;
        return false;
    }

    private bool TryGetNamedNumber(
        string json,
        string key,
        out float value)
    {
        const string number =
            @"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?";
        Match match =
            Regex.Match(
                json,
                @"[""']" + Regex.Escape(key) + @"[""']\s*:\s*(" + number + @")",
                RegexOptions.IgnoreCase);

        if (match.Success)
            return TryParseFloat(match.Groups[1].Value, out value);

        value = 0f;
        return false;
    }

    private bool TryParseFloat(
        string text,
        out float value)
    {
        return float.TryParse(
            text,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value);
    }

    private void OnDestroy()
    {
        Disconnect();
    }

#pragma warning disable 0649
    [Serializable]
    private class ObjectTopicMessage
    {
        public string label;
        public string objectName;
        public string object_name;
        public string name;
        public float x;
        public float y;
        public float z;
        public float[] xyz;
        public VectorPayload position;
        public VectorPayload coordinate;
        public VectorPayload coordinates;
        public VectorPayload point;
        public VectorPayload center;
    }

    [Serializable]
    private class VectorPayload
    {
        public float x;
        public float y;
        public float z;

        public Vector3 ToVector3()
        {
            return new Vector3(x, y, z);
        }

        public bool HasValue()
        {
            return
                !Mathf.Approximately(x, 0f) ||
                !Mathf.Approximately(y, 0f) ||
                !Mathf.Approximately(z, 0f);
        }
    }
#pragma warning restore 0649

    private class BillboardLabel : MonoBehaviour
    {
        private void LateUpdate()
        {
            Camera targetCamera =
                Camera.main;

            if (targetCamera == null)
                return;

            transform.rotation =
                Quaternion.LookRotation(
                    transform.position - targetCamera.transform.position,
                    Vector3.up);
        }
    }
}
