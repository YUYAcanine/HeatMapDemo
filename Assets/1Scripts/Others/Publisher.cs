using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

public class Publisher : MonoBehaviour
{
    [Header("MQTT")]
    [SerializeField] private string mqttHost = "192.168.236.211";
    [SerializeField] private int mqttPort = 1883;
    [SerializeField] private string mqttUsername = "mqtt-user";
    [SerializeField] private string mqttPassword = "401402";
    [SerializeField] private string topic = "pathfinder/eta";
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
            PublishEtaResults(currentEtaCalculate);
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
            client.Connect(mqttHost, mqttPort);
            stream = client.GetStream();
            SendConnectPacket();
            Debug.Log($"Publisher: connecting to MQTT broker {mqttHost}:{mqttPort}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Publisher: connection failed. {ex.GetType().Name}: {ex.Message}");
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

    public void PublishEtaResults()
    {
        ETACalculate currentEtaCalculate = GetEtaCalculate();
        if (currentEtaCalculate != null)
            PublishEtaResults(currentEtaCalculate);
    }

    private void PublishEtaResults(ETACalculate source)
    {
        if (!IsConnected || string.IsNullOrWhiteSpace(topic))
            return;

        foreach (KeyValuePair<string, float> route in source.RouteLengths)
        {
            bool reachable = route.Value >= 0f;
            EtaMessage message = new EtaMessage
            {
                label = route.Key,
                eta = reachable ? route.Value / source.MovementSpeed : -1f,
                reachable = reachable
            };
            string json = JsonUtility.ToJson(message);
            Publish(topic, json);

            if (logPublishedMessage)
                Debug.Log($"Publisher: published topic={topic}, payload={json}");
        }

        lastPublishedEtaVersion = source.EtaVersion;
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
        WriteMqttString(body, "unity-publisher-" + Guid.NewGuid().ToString("N").Substring(0, 8));
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
            Debug.Log($"Publisher: connected to MQTT broker {mqttHost}:{mqttPort}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Publisher: MQTT acknowledgement failed. {ex.GetType().Name}: {ex.Message}");
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
    private class EtaMessage
    {
        public string label;
        public float eta;
        public bool reachable;
    }
}
