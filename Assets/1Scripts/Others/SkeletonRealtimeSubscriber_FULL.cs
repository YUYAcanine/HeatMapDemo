using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

// SkeletonRealtimePublisher_FULL (別デバイス側のMultiPositionシーン) がパブリッシュする
// pathfinder/person_coordinate_FULL (32関節すべて) を受信し、生JSONをそのまま
// OnMessageReceivedで流す。可視化は行わず受信専用。MultiIntegrationLoggerシーンの
// Logger.csがこれを購読してログに保存する。
public class SkeletonRealtimeSubscriber_FULL : MonoBehaviour
{
    [Header("MQTT")]
    [SerializeField] private string mqttHost = "192.168.236.211";
    [SerializeField] private int mqttPort = 1883;
    [SerializeField] private string mqttUsername = "mqtt-user";
    [SerializeField] private string mqttPassword = "401402";
    [SerializeField] private string topic = "pathfinder/person_coordinate_FULL";
    [SerializeField] private bool connectOnStart = true;
    [SerializeField] private bool logReceivedMessage = false;

    private TcpClient client;
    private NetworkStream stream;
    private readonly byte[] receiveBuffer = new byte[8192];
    private readonly List<byte> mqttBuffer = new List<byte>();
    private float lastMqttSendTime;

    public event Action<string, string> OnMessageReceived;

    public bool IsConnected =>
        client != null && client.Connected;

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
            client = new TcpClient { NoDelay = true };
            mqttHost = MqttConfig.ResolveHost(mqttHost);
            mqttPort = MqttConfig.ResolvePort(mqttPort);
            client.Connect(mqttHost, mqttPort);
            stream = client.GetStream();
            SendConnectPacket();
            Debug.Log($"SkeletonRealtimeSubscriber_FULL: connecting to MQTT broker {mqttHost}:{mqttPort}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"SkeletonRealtimeSubscriber_FULL: connection failed. {ex.GetType().Name}: {ex.Message}");
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
            Debug.LogWarning($"SkeletonRealtimeSubscriber_FULL: receive failed. {ex.GetType().Name}: {ex.Message}");
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
                throw new System.IO.IOException("MQTT broker rejected the connection.");
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
            Debug.Log($"SkeletonRealtimeSubscriber_FULL: received payload bytes={Encoding.UTF8.GetByteCount(json)}");

        OnMessageReceived?.Invoke(topic, json);
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
        WriteMqttString(body, "unity-skeleton-subscriber-full-" + Guid.NewGuid().ToString("N").Substring(0, 8));
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
        Debug.Log($"SkeletonRealtimeSubscriber_FULL: subscribed to {topic}");
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
}
