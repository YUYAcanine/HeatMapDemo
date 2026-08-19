using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using UnityEngine;
using Microsoft.Azure.Kinect.BodyTracking;

// 複数の Kinect (SkeletonRealtimeMulti) が検出した骨格を統合してMQTTにパブリッシュする。
//
// 同じ人物でもカメラごとに異なる BodyId が割り当てられるため、BodyId では同一人物か
// どうかを判定できない。そのため Pelvis のワールド座標（room座標系）が近いBody同士は
// 同一人物とみなし、その中でもっとも信頼度（Joint confidenceの平均）が高いBodyだけを
// 採用してパブリッシュする。
public class SkeletonRealtimePublisher : MonoBehaviour
{
    [Header("Camera Sources")]
    [Tooltip("空の場合はシーン内の SkeletonRealtimeMulti を自動収集する。")]
    [SerializeField] private SkeletonRealtimeMulti[] skeletonSources;

    [Header("Fusion")]
    [Tooltip("この距離(m)以内のPelvis位置同士は同一人物とみなす。")]
    [SerializeField] private float sameBodyDistance = 0.5f;
    [Tooltip("パブリッシュ済みの人物トラックをこの距離(m)以内なら同一人物として引き継ぐ。")]
    [SerializeField] private float trackMatchDistance = 1.0f;
    [Tooltip("この秒数以上マッチしなかったトラックは破棄する。")]
    [SerializeField] private float trackTimeout = 2f;
    [Tooltip("MQTTへのパブリッシュ間隔(秒)。")]
    [SerializeField] private float publishInterval = 0.1f;

    [Header("MQTT")]
    [SerializeField] private string mqttHost = "192.168.236.211";
    [SerializeField] private int mqttPort = 1883;
    [SerializeField] private string mqttUsername = "mqtt-user";
    [SerializeField] private string mqttPassword = "401402";
    [SerializeField] private string topic = "pathfinder/person_coordinate";
    [SerializeField] private bool connectOnStart = true;
    [SerializeField] private bool logPublishedMessage = true;

    private TcpClient client;
    private NetworkStream stream;
    private bool connectionAcknowledged;
    private float lastMqttSendTime;
    private float nextPublishTime;

    private readonly List<PersonTrack> tracks = new List<PersonTrack>();
    private int nextTrackId = 0;

    public bool IsConnected =>
        client != null && client.Connected && stream != null && connectionAcknowledged;

    private void Start()
    {
        if (skeletonSources == null || skeletonSources.Length == 0)
            skeletonSources = FindObjectsOfType<SkeletonRealtimeMulti>();

        if (connectOnStart)
            Connect();
    }

    private void Update()
    {
        ReceiveConnectionAcknowledgement();

        if (IsConnected && Time.unscaledTime >= nextPublishTime)
        {
            nextPublishTime = Time.unscaledTime + publishInterval;
            FuseAndPublish();
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
            Debug.Log($"SkeletonRealtimePublisher: connecting to MQTT broker {mqttHost}:{mqttPort}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"SkeletonRealtimePublisher: connection failed. {ex.GetType().Name}: {ex.Message}");
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

    private void FuseAndPublish()
    {
        List<Candidate> candidates = CollectCandidates();

        // 信頼度の高い順に処理し、近い場所に既に採用済みのBodyがあればそのBodyは捨てる。
        // これにより「近い場所で検出された骨格は信頼度の高い方を優先する」動作になる。
        candidates.Sort((a, b) => b.confidence.CompareTo(a.confidence));

        List<Candidate> selected = new List<Candidate>();

        foreach (Candidate candidate in candidates)
        {
            bool duplicate = false;

            foreach (Candidate existing in selected)
            {
                if (Vector3.Distance(existing.pelvisPosition, candidate.pelvisPosition) <= sameBodyDistance)
                {
                    duplicate = true;
                    break;
                }
            }

            if (!duplicate)
                selected.Add(candidate);
        }

        foreach (Candidate candidate in selected)
        {
            PersonTrack track = MatchOrCreateTrack(candidate.pelvisPosition);
            track.position = candidate.pelvisPosition;
            track.lastSeenTime = Time.unscaledTime;

            PublishCandidate(candidate, track.id);
        }

        tracks.RemoveAll(t => Time.unscaledTime - t.lastSeenTime > trackTimeout);
    }

    private List<Candidate> CollectCandidates()
    {
        List<Candidate> candidates = new List<Candidate>();

        foreach (SkeletonRealtimeMulti source in skeletonSources)
        {
            if (source == null)
                continue;

            foreach (SkeletonRealtimeMulti.BodyData body in source.LatestBodies)
            {
                if (!body.TryGetJointPosition(JointId.Pelvis, out Vector3 pelvisPosition))
                    continue;

                float headPelvisDistance = -1f;
                Vector3 headPosition = Vector3.zero;
                Vector3 headToNose = Vector3.zero;

                if (body.TryGetJointPosition(JointId.Head, out headPosition))
                {
                    headPelvisDistance = Vector3.Distance(headPosition, pelvisPosition);

                    if (body.TryGetJointPosition(JointId.Nose, out Vector3 nosePosition))
                        headToNose = nosePosition - headPosition;
                }

                candidates.Add(new Candidate
                {
                    deviceIndex = source.DeviceIndex,
                    bodyId = body.bodyId,
                    pelvisPosition = pelvisPosition,
                    confidence = body.GetAverageConfidence(),
                    headPelvisDistance = headPelvisDistance,
                    headPosition = headPosition,
                    headToNose = headToNose
                });
            }
        }

        return candidates;
    }

    private PersonTrack MatchOrCreateTrack(Vector3 position)
    {
        PersonTrack closest = null;
        float closestDistance = trackMatchDistance;

        foreach (PersonTrack track in tracks)
        {
            float distance = Vector3.Distance(track.position, position);

            if (distance <= closestDistance)
            {
                closest = track;
                closestDistance = distance;
            }
        }

        if (closest != null)
            return closest;

        PersonTrack newTrack = new PersonTrack
        {
            id = nextTrackId++,
            position = position,
            lastSeenTime = Time.unscaledTime
        };

        tracks.Add(newTrack);
        return newTrack;
    }

    private void PublishCandidate(Candidate candidate, int trackId)
    {
        PersonPositionMessage message = new PersonPositionMessage
        {
            label = $"person_{trackId}",
            x = candidate.pelvisPosition.x,
            y = candidate.pelvisPosition.y,
            z = candidate.pelvisPosition.z,
            confidence = candidate.confidence,
            deviceIndex = candidate.deviceIndex,
            bodyId = candidate.bodyId,
            headPelvisDistance = candidate.headPelvisDistance,
            head = candidate.headPosition,
            headToNose = candidate.headToNose
        };

        string json = JsonUtility.ToJson(message);
        Publish(topic, json);

        if (logPublishedMessage)
            Debug.Log($"SkeletonRealtimePublisher: published topic={topic}, payload={json}");
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
        WriteMqttString(body, "unity-skeleton-publisher-" + Guid.NewGuid().ToString("N").Substring(0, 8));
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
            Debug.Log($"SkeletonRealtimePublisher: connected to MQTT broker {mqttHost}:{mqttPort}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"SkeletonRealtimePublisher: MQTT acknowledgement failed. {ex.GetType().Name}: {ex.Message}");
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

    private class Candidate
    {
        public int deviceIndex;
        public uint bodyId;
        public Vector3 pelvisPosition;
        public float confidence;
        public float headPelvisDistance;
        public Vector3 headPosition;
        public Vector3 headToNose;
    }

    private class PersonTrack
    {
        public int id;
        public Vector3 position;
        public float lastSeenTime;
    }

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
}
