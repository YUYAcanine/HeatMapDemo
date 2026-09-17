using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;

// PlaneFinder の Reload(平面検知 -> NavMesh生成 -> PointNavLinkでリンク接続)パイプラインが
// 完了したタイミングで、生成されたNavMeshの三角形メッシュとNavMeshLinkをMQTTにパブリッシュする。
public class NavPub : MonoBehaviour
{
    [Header("MQTT")]
    [SerializeField] private string mqttHost = "192.168.236.211";
    [SerializeField] private int mqttPort = 1883;
    [SerializeField] private string mqttUsername = "mqtt-user";
    [SerializeField] private string mqttPassword = "401402";
    [SerializeField] private string topic = "pathfinder/navmesh";
    [SerializeField] private bool connectOnStart = true;
    [SerializeField] private bool logPublishedMessage = true;

    [Header("Source")]
    [Tooltip("生成されたNavMeshLinkを列挙するためのPointNavLink。空の場合はシーン内から自動検索する。")]
    [SerializeField] private PointNavLink pointNavLink;

    private TcpClient client;
    private NetworkStream stream;
    private bool connectionAcknowledged;
    private float lastMqttSendTime;

    public bool IsConnected =>
        client != null && client.Connected && stream != null && connectionAcknowledged;

    private void OnEnable()
    {
        PlaneFinder.OnNavMeshPipelineFinished += HandleNavMeshPipelineFinished;
    }

    private void OnDisable()
    {
        PlaneFinder.OnNavMeshPipelineFinished -= HandleNavMeshPipelineFinished;
    }

    private void Start()
    {
        if (connectOnStart)
            Connect();
    }

    private void Update()
    {
        ReceiveConnectionAcknowledgement();

        if (IsConnected && Time.unscaledTime - lastMqttSendTime >= 30f)
            SendMqttPacket(0xC0, new List<byte>());
    }

    private void HandleNavMeshPipelineFinished()
    {
        PublishNavMesh();
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
            Debug.Log($"NavPub: connecting to MQTT broker {mqttHost}:{mqttPort}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"NavPub: connection failed. {ex.GetType().Name}: {ex.Message}");
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

    public void PublishNavMesh()
    {
        if (!IsConnected)
        {
            Debug.LogWarning("NavPub: cannot publish NavMesh because MQTT is not connected.");
            return;
        }

        NavMeshTriangulation triangulation = NavMesh.CalculateTriangulation();

        NavMeshMessage message = new NavMeshMessage
        {
            vertices = new List<Vector3>(triangulation.vertices),
            triangles = new List<int>(triangulation.indices),
            links = CollectLinks(),
        };
        message.vertexCount = message.vertices.Count;
        message.triangleCount = message.triangles.Count / 3;
        message.linkCount = message.links.Count;

        string json = JsonUtility.ToJson(message);
        Publish(topic, json);

        if (logPublishedMessage)
        {
            Debug.Log(
                $"NavPub: published topic={topic}, vertices={message.vertexCount}, triangles={message.triangleCount}, links={message.linkCount}, payloadBytes={Encoding.UTF8.GetByteCount(json)}");
        }
    }

    private List<LinkMessage> CollectLinks()
    {
        List<LinkMessage> links = new List<LinkMessage>();

        PointNavLink generator = GetPointNavLink();
        if (generator == null)
            return links;

        NavMeshLink[] allLinks = FindObjectsOfType<NavMeshLink>();

        foreach (NavMeshLink link in allLinks)
        {
            if (link == null || !link.name.StartsWith(PointNavLink.GeneratedLinkPrefix))
                continue;

            links.Add(new LinkMessage
            {
                start = link.transform.TransformPoint(link.startPoint),
                end = link.transform.TransformPoint(link.endPoint),
                bidirectional = link.bidirectional,
                costModifier = link.costModifier,
                width = link.width
            });
        }

        return links;
    }

    private PointNavLink GetPointNavLink()
    {
        if (pointNavLink != null)
            return pointNavLink;

        pointNavLink = FindObjectOfType<PointNavLink>();
        return pointNavLink;
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
        WriteMqttString(body, "unity-navmesh-publisher-" + Guid.NewGuid().ToString("N").Substring(0, 8));
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
            Debug.Log($"NavPub: connected to MQTT broker {mqttHost}:{mqttPort}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"NavPub: MQTT acknowledgement failed. {ex.GetType().Name}: {ex.Message}");
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

    private void OnDestroy()
    {
        Disconnect();
    }

    [Serializable]
    private class LinkMessage
    {
        public Vector3 start;
        public Vector3 end;
        public bool bidirectional;
        public int costModifier;
        public float width;
    }

    [Serializable]
    private class NavMeshMessage
    {
        public List<Vector3> vertices;
        public List<int> triangles;
        public List<LinkMessage> links;
        public int vertexCount;
        public int triangleCount;
        public int linkCount;
    }
}
