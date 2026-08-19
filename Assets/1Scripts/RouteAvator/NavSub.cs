using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;

// NavPub がパブリッシュするNavMesh(頂点+三角形)とNavMeshLinkの情報をMQTTで受信し、
// このシーン上にそのまま再現する。受信するたびに直前に生成したNavMesh/リンクは
// 破棄してから作り直す(送信元カメラの部屋座標とこのシーンの部屋座標が一致している前提)。
public class NavSub : MonoBehaviour
{
    [Header("MQTT")]
    [SerializeField] private string mqttHost = "192.168.236.211";
    [SerializeField] private int mqttPort = 1883;
    [SerializeField] private string mqttUsername = "mqtt-user";
    [SerializeField] private string mqttPassword = "401402";
    [SerializeField] private string topic = "pathfinder/navmesh";
    [SerializeField] private bool connectOnStart = true;
    [SerializeField] private bool logReceivedMessage = true;

    [Header("NavMesh Build")]
    [SerializeField] private int agentTypeId = 0;
    [SerializeField] private float agentRadius = 0.01f;
    [SerializeField] private float agentHeight = 0.05f;
    [SerializeField] private float voxelSize = 0.03f;
    [SerializeField] private float minRegionArea = 0f;
    [SerializeField] private float boundsMargin = 2f;

    [Header("Source Mesh Visual")]
    [SerializeField] private bool showSourceMesh = false;
    [SerializeField] private Color sourceMeshColor = new Color(0.15f, 0.9f, 0.25f, 0.35f);

    [Header("NavMeshLink")]
    [SerializeField] private float linkWidth = 0.18f;

    private TcpClient client;
    private NetworkStream stream;
    private readonly byte[] receiveBuffer = new byte[4096];
    private readonly List<byte> mqttBuffer = new List<byte>();
    private float lastMqttSendTime;

    private GameObject navMeshSourceObject;
    private NavMeshDataInstance navMeshDataInstance;
    private readonly List<GameObject> linkObjects = new List<GameObject>();

    public bool IsConnected =>
        client != null && client.Connected;

    public int LastVertexCount { get; private set; }
    public int LastTriangleCount { get; private set; }
    public int LastLinkCount { get; private set; }

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
            client.Connect(mqttHost, mqttPort);
            stream = client.GetStream();
            SendConnectPacket();
            Debug.Log($"NavSub: connecting to MQTT broker {mqttHost}:{mqttPort}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"NavSub: connection failed. {ex.GetType().Name}: {ex.Message}");
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
            Debug.LogWarning($"NavSub: receive failed. {ex.GetType().Name}: {ex.Message}");
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
        {
            Debug.Log(
                $"NavSub: received payload. bytes={Encoding.UTF8.GetByteCount(json)}");
        }

        TryRebuildNavMesh(json);
    }

    private void TryRebuildNavMesh(string json)
    {
        NavMeshMessage message;

        try
        {
            message = JsonUtility.FromJson<NavMeshMessage>(json);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"NavSub: json parse failed. {ex.GetType().Name}: {ex.Message}");
            return;
        }

        if (message == null || message.vertices == null || message.triangles == null ||
            message.vertices.Count == 0 || message.triangles.Count == 0)
        {
            Debug.LogWarning("NavSub: received NavMesh message has no geometry.");
            return;
        }

        Mesh mesh = new Mesh
        {
            name = "NavSubSourceMesh",
            indexFormat = UnityEngine.Rendering.IndexFormat.UInt32,
            vertices = message.vertices.ToArray(),
            triangles = message.triangles.ToArray()
        };
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        RebuildNavMeshFromMesh(mesh);
        RebuildLinks(message.links);

        LastVertexCount = message.vertices.Count;
        LastTriangleCount = message.triangles.Count / 3;
        LastLinkCount = message.links?.Count ?? 0;

        Debug.Log(
            $"NavSub: NavMesh rebuilt. vertices={LastVertexCount}, triangles={LastTriangleCount}, links={LastLinkCount}");
    }

    private void RebuildNavMeshFromMesh(Mesh mesh)
    {
        if (navMeshDataInstance.valid)
        {
            navMeshDataInstance.Remove();
            navMeshDataInstance = new NavMeshDataInstance();
        }

        if (navMeshSourceObject == null)
        {
            navMeshSourceObject = new GameObject("NavSubSourceMesh");
            navMeshSourceObject.transform.SetParent(transform, false);

            // 受信した頂点はワールド座標の絶対値なので、このオブジェクト自身は
            // 必ずワールド原点・無回転・無スケールに固定する。SetParent(parent, false)は
            // ローカル座標を維持するだけでワールド座標をゼロにはしてくれないため、
            // NavSubを置いた場所の分だけ二重にオフセットしてしまうのを防ぐ。
            navMeshSourceObject.transform.position = Vector3.zero;
            navMeshSourceObject.transform.rotation = Quaternion.identity;
            navMeshSourceObject.transform.localScale = Vector3.one;
        }

        MeshFilter meshFilter = navMeshSourceObject.GetComponent<MeshFilter>();
        if (meshFilter == null)
            meshFilter = navMeshSourceObject.AddComponent<MeshFilter>();
        meshFilter.sharedMesh = mesh;

        MeshRenderer meshRenderer = navMeshSourceObject.GetComponent<MeshRenderer>();
        if (meshRenderer == null)
            meshRenderer = navMeshSourceObject.AddComponent<MeshRenderer>();
        meshRenderer.sharedMaterial = CreateTransparentMaterial(sourceMeshColor);
        meshRenderer.enabled = showSourceMesh;

        NavMeshBuildSettings buildSettings = NavMesh.GetSettingsByID(agentTypeId);
        buildSettings.agentRadius = Mathf.Max(agentRadius, 0.001f);
        buildSettings.agentHeight = Mathf.Max(agentHeight, 0.001f);
        buildSettings.agentClimb = 100f;
        buildSettings.agentSlope = 89f;
        buildSettings.overrideVoxelSize = true;
        buildSettings.voxelSize = Mathf.Max(voxelSize, 0.005f);
        buildSettings.overrideTileSize = true;
        buildSettings.tileSize = 64;
        buildSettings.minRegionArea = Mathf.Max(minRegionArea, 0f);

        List<NavMeshBuildSource> sources = new List<NavMeshBuildSource>
        {
            new NavMeshBuildSource
            {
                shape = NavMeshBuildSourceShape.Mesh,
                sourceObject = mesh,
                transform = Matrix4x4.identity,
                area = 0
            }
        };

        Bounds bounds = mesh.bounds;
        bounds.Expand(Vector3.one * boundsMargin);

        NavMeshData navMeshData = NavMeshBuilder.BuildNavMeshData(
            buildSettings,
            sources,
            bounds,
            Vector3.zero,
            Quaternion.identity);

        if (navMeshData == null)
        {
            Debug.LogWarning("NavSub: NavMesh build returned null.");
            return;
        }

        navMeshDataInstance = NavMesh.AddNavMeshData(navMeshData);
    }

    private void RebuildLinks(List<LinkMessage> links)
    {
        foreach (GameObject linkObject in linkObjects)
        {
            if (linkObject != null)
                Destroy(linkObject);
        }
        linkObjects.Clear();

        if (links == null)
            return;

        for (int i = 0; i < links.Count; i++)
        {
            LinkMessage linkMessage = links[i];

            GameObject linkObject = new GameObject($"NavSubLink_{i}");
            linkObject.transform.SetParent(transform, false);
            linkObject.transform.rotation = Quaternion.identity;
            linkObject.transform.localScale = Vector3.one;
            linkObject.transform.position = linkMessage.start;

            NavMeshLink link = linkObject.AddComponent<NavMeshLink>();
            link.bidirectional = linkMessage.bidirectional;
            link.width = Mathf.Max(linkMessage.width > 0f ? linkMessage.width : linkWidth, 0.01f);
            link.costModifier = Mathf.Max(linkMessage.costModifier, 1);
            link.autoUpdate = true;
            link.startPoint = Vector3.zero;
            link.endPoint = linkObject.transform.InverseTransformPoint(linkMessage.end);

            linkObjects.Add(linkObject);
        }
    }

    private Material CreateTransparentMaterial(Color color)
    {
        Material material = new Material(Shader.Find("Standard"));
        material.SetFloat("_Mode", 3);
        material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        material.SetInt("_ZWrite", 0);
        material.DisableKeyword("_ALPHATEST_ON");
        material.EnableKeyword("_ALPHABLEND_ON");
        material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        material.renderQueue = 3000;
        material.color = color;
        return material;
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
        WriteMqttString(body, "unity-navmesh-subscriber-" + Guid.NewGuid().ToString("N").Substring(0, 8));
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
        Debug.Log($"NavSub: subscribed to {topic}");
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

        if (navMeshDataInstance.valid)
            navMeshDataInstance.Remove();
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
