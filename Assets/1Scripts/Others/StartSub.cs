using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

// SkeletonRealtimePublisher がパブリッシュする人物のPelvis座標(pathfinder/person_coordinate)を
// サブスクライブし、このGameObjectのtransform.positionを「経路計算のスタート位置」として
// 更新し続ける。RouteSub の Start(Transform) にこのGameObjectを割り当てて使う。
//
// 複数人が同時に検出されている場合は、headPelvisDistance（Head-Pelvis間の距離。
// SkeletonRealtimePublisher が算出してメッセージに含めている）がもっとも短い人物を採用する。
public class StartSub : MonoBehaviour
{
    [Header("MQTT")]
    [SerializeField] private string mqttHost = "192.168.50.231";
    [SerializeField] private int mqttPort = 1883;
    [SerializeField] private string mqttUsername = "mqtt-user";
    [SerializeField] private string mqttPassword = "401402";
    [SerializeField] private string topic = "pathfinder/person_coordinate";
    [SerializeField] private bool connectOnStart = true;
    [SerializeField] private bool logReceivedMessage = true;

    [Header("Start Position Selection")]
    [Tooltip("この秒数以上メッセージが来なかった人物は選択対象から除外する。")]
    [SerializeField] private float personTimeout = 1.5f;
    [Tooltip("headPelvisDistanceがこの値未満の場合は姿勢の取得に失敗しているとみなし除外する。")]
    [SerializeField] private float minValidHeadPelvisDistance = 0.05f;

    [Header("Floor Snapping")]
    [Tooltip("PelvisのX/Z座標はそのまま使い、Y座標はPelvis直下にある一番近い平面(床のCollider)の高さに置き換える。オフの場合はsubscribeした位置(Pelvis位置)をそのまま使う。")]
    [SerializeField] private bool snapToNearestFloor = false;
    [Tooltip("床として判定するCollider(PlaneFinderが生成した歩行可能サーフェス等)のレイヤー。")]
    [SerializeField] private LayerMask floorLayerMask = ~0;
    [Tooltip("Raycastの開始点をPelvisからこの高さ(m)だけ上げる。Pelvisが床より僅かに低く計測された場合でも真下の床を検出できるようにするため。")]
    [SerializeField] private float raycastStartHeight = 1.5f;
    [Tooltip("Raycastの開始点から真下に向かって床を探す最大距離(m)。")]
    [SerializeField] private float floorSearchDistance = 5f;

    private TcpClient client;
    private NetworkStream stream;
    private readonly byte[] receiveBuffer = new byte[4096];
    private readonly List<byte> mqttBuffer = new List<byte>();
    private readonly Dictionary<string, PersonState> personsByLabel = new Dictionary<string, PersonState>();
    private float lastMqttSendTime;

    public bool IsConnected =>
        client != null && client.Connected;

    public bool HasActivePerson { get; private set; }
    public string ActiveLabel { get; private set; }
    public int StartPositionVersion { get; private set; }

    private void Start()
    {
        if (connectOnStart)
            Connect();
    }

    private void Update()
    {
        ReceiveAvailableMessages();
        RemoveStalePersons();
        UpdateStartPosition();

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
            Debug.Log($"StartSub: connecting to MQTT broker {mqttHost}:{mqttPort}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"StartSub: connection failed. {ex.GetType().Name}: {ex.Message}");
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

    private void UpdateStartPosition()
    {
        string bestLabel = null;
        PersonState bestPerson = null;

        foreach (KeyValuePair<string, PersonState> entry in personsByLabel)
        {
            PersonState person = entry.Value;

            if (person.headPelvisDistance < minValidHeadPelvisDistance)
                continue;

            if (bestPerson == null || person.headPelvisDistance < bestPerson.headPelvisDistance)
            {
                bestPerson = person;
                bestLabel = entry.Key;
            }
        }

        if (bestPerson == null)
        {
            HasActivePerson = false;
            ActiveLabel = null;
            return;
        }

        Vector3 startPosition = bestPerson.position;

        if (snapToNearestFloor && TryFindNearestFloorHeight(startPosition, out float floorY))
            startPosition.y = floorY;

        transform.position = startPosition;
        HasActivePerson = true;
        ActiveLabel = bestLabel;
        StartPositionVersion++;
    }

    private bool TryFindNearestFloorHeight(Vector3 pelvisPosition, out float floorY)
    {
        floorY = pelvisPosition.y;

        Vector3 origin = pelvisPosition + Vector3.up * raycastStartHeight;

        RaycastHit[] hits = Physics.RaycastAll(
            origin,
            Vector3.down,
            raycastStartHeight + floorSearchDistance,
            floorLayerMask,
            QueryTriggerInteraction.Ignore);

        if (hits.Length == 0)
            return false;

        float nearestDistance = float.MaxValue;
        bool found = false;

        foreach (RaycastHit hit in hits)
        {
            if (hit.distance >= nearestDistance)
                continue;

            nearestDistance = hit.distance;
            floorY = hit.point.y;
            found = true;
        }

        return found;
    }

    private void RemoveStalePersons()
    {
        List<string> staleLabels = null;

        foreach (KeyValuePair<string, PersonState> entry in personsByLabel)
        {
            if (Time.unscaledTime - entry.Value.lastReceivedTime <= personTimeout)
                continue;

            staleLabels ??= new List<string>();
            staleLabels.Add(entry.Key);
        }

        if (staleLabels == null)
            return;

        foreach (string label in staleLabels)
            personsByLabel.Remove(label);
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
            Debug.LogWarning($"StartSub: receive failed. {ex.GetType().Name}: {ex.Message}");
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
            Debug.Log($"StartSub: received payload={json}");

        TryUpdatePerson(json);
    }

    private void TryUpdatePerson(string json)
    {
        PersonPositionMessage message;

        try
        {
            message = JsonUtility.FromJson<PersonPositionMessage>(json);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"StartSub: json parse failed. {ex.GetType().Name}: {ex.Message}, json={json}");
            return;
        }

        if (string.IsNullOrWhiteSpace(message.label))
        {
            Debug.LogWarning($"StartSub: message skipped because label is empty. json={json}");
            return;
        }

        if (!personsByLabel.TryGetValue(message.label, out PersonState person))
        {
            person = new PersonState();
            personsByLabel[message.label] = person;
        }

        person.position = new Vector3(message.x, message.y, message.z);
        person.headPelvisDistance = message.headPelvisDistance;
        person.lastReceivedTime = Time.unscaledTime;
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
        WriteMqttString(body, "unity-start-sub-" + Guid.NewGuid().ToString("N").Substring(0, 8));
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
        Debug.Log($"StartSub: subscribed to {topic}");
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

    private class PersonState
    {
        public Vector3 position;
        public float headPelvisDistance;
        public float lastReceivedTime;
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
    }
#pragma warning restore 0649
}
