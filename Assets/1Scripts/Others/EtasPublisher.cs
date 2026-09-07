using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

// Publisher.cs (pathfinder/eta, child専用・eta秒数つき)とは別に、
// child/parent両方のreachable状況を "pathfinder/etas" へ配信する。
// 既存のpathfinder/etaはそのまま残し、こちらは独立したMQTT接続を持つ。
public class EtasPublisher : MonoBehaviour
{
    [Header("MQTT")]
    [SerializeField] private string mqttHost = "192.168.236.211";
    [SerializeField] private int mqttPort = 1883;
    [SerializeField] private string mqttUsername = "mqtt-user";
    [SerializeField] private string mqttPassword = "401402";
    [SerializeField] private string topic = "pathfinder/etas";
    [SerializeField] private bool connectOnStart = true;
    [SerializeField] private bool logPublishedMessage = true;

    [Header("Source")]
    [SerializeField] private ETACalculate etaCalculate;

    private TcpClient client;
    private NetworkStream stream;
    private int lastPublishedEtaVersion = -1;
    private float lastMqttSendTime;
    private bool connectionAcknowledged;

    public bool IsConnected =>
        client != null && client.Connected && stream != null && connectionAcknowledged;

    private void Start()
    {
        if (connectOnStart)
            Connect();
    }

    private void Update()
    {
        ReceiveConnectionAcknowledgement();

        ETACalculate currentEtaCalculate = GetEtaCalculate();
        if (IsConnected &&
            currentEtaCalculate != null &&
            currentEtaCalculate.EtaVersion != lastPublishedEtaVersion)
        {
            PublishEtasResults(currentEtaCalculate);
        }

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
            Debug.Log($"EtasPublisher: connecting to MQTT broker {mqttHost}:{mqttPort}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"EtasPublisher: connection failed. {ex.GetType().Name}: {ex.Message}");
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
        connectionAcknowledged = false;
    }

    public void PublishEtasResults()
    {
        ETACalculate currentEtaCalculate = GetEtaCalculate();
        if (currentEtaCalculate != null)
            PublishEtasResults(currentEtaCalculate);
    }

    private void PublishEtasResults(ETACalculate source)
    {
        if (!IsConnected || string.IsNullOrWhiteSpace(topic))
            return;

        Dictionary<string, (string Type, float Length)> closest =
            new Dictionary<string, (string Type, float Length)>();

        foreach (KeyValuePair<string, float> route in source.RouteLengths)
            ConsiderClosest(closest, route.Key, "child", route.Value);

        foreach (KeyValuePair<string, float> route in source.ParentRouteLengths)
            ConsiderClosest(closest, ETACalculate.ExtractGoalLabel(route.Key), "parent", route.Value);

        List<ClosestMessage> messages = new List<ClosestMessage>();
        foreach (KeyValuePair<string, (string Type, float Length)> entry in closest)
        {
            string type = entry.Value.Type ?? "none";

            // childが到達不可の場合は、parentが到達可能でもclosertypeはnoneにする。
            bool childReachable =
                source.RouteLengths.TryGetValue(entry.Key, out float childLength) && childLength >= 0f;

            if (!childReachable)
                type = "none";

            messages.Add(new ClosestMessage
            {
                label = entry.Key,
                closertype = type
            });
        }

        PublishEntries(messages);

        lastPublishedEtaVersion = source.EtaVersion;
    }

    private static void ConsiderClosest(Dictionary<string, (string Type, float Length)> closest, string label, string type, float routeLength)
    {
        if (!closest.TryGetValue(label, out (string Type, float Length) current))
            current = (null, float.PositiveInfinity);

        if (routeLength >= 0f && routeLength < current.Length)
            current = (type, routeLength);

        closest[label] = current;
    }

    private void PublishEntries(List<ClosestMessage> messages)
    {
        if (messages.Count == 0)
            return;

        StringBuilder json = new StringBuilder("[");
        for (int i = 0; i < messages.Count; i++)
        {
            if (i > 0) json.Append(',');
            json.Append(JsonUtility.ToJson(messages[i]));
        }
        json.Append(']');

        string payload = json.ToString();
        Publish(topic, payload);

        if (logPublishedMessage)
            Debug.Log($"EtasPublisher: published topic={topic}, payload={payload}");
    }

    public void Publish(string publishTopic, string payload)
    {
        if (!IsConnected || string.IsNullOrWhiteSpace(publishTopic))
            return;

        List<byte> body = new List<byte>();
        WriteMqttString(body, publishTopic);
        body.AddRange(Encoding.UTF8.GetBytes(payload ?? string.Empty));
        SendMqttPacket(0x30, body);
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
        body.Add(0);
        body.Add(60);
        WriteMqttString(body, "unity-etas-publisher-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        if (!string.IsNullOrEmpty(mqttUsername)) WriteMqttString(body, mqttUsername);
        if (!string.IsNullOrEmpty(mqttPassword)) WriteMqttString(body, mqttPassword);
        SendMqttPacket(0x10, body);
    }

    private void ReceiveConnectionAcknowledgement()
    {
        if (connectionAcknowledged || stream == null || !stream.DataAvailable)
            return;

        try
        {
            int header = stream.ReadByte();
            int remainingLength = stream.ReadByte();
            if (header != 0x20 || remainingLength != 2)
                return;

            int acknowledgeFlags = stream.ReadByte();
            int returnCode = stream.ReadByte();
            if (acknowledgeFlags < 0 || returnCode < 0)
                return;
            if (returnCode != 0)
                throw new InvalidOperationException($"MQTT broker rejected the connection (code {returnCode}).");

            connectionAcknowledged = true;
            Debug.Log($"EtasPublisher: connected to MQTT broker {mqttHost}:{mqttPort}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"EtasPublisher: MQTT acknowledgement failed. {ex.GetType().Name}: {ex.Message}");
            Disconnect();
        }
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

    private ETACalculate GetEtaCalculate()
    {
        if (etaCalculate == null)
            etaCalculate = FindObjectOfType<ETACalculate>();
        return etaCalculate;
    }

    private void OnDestroy()
    {
        Disconnect();
    }

    [Serializable]
    private class ClosestMessage
    {
        public string label;
        public string closertype;
    }
}
