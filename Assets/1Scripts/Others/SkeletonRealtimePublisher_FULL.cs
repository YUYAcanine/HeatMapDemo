using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using UnityEngine;
using Microsoft.Azure.Kinect.BodyTracking;

// SkeletonRealtimePublisher と同じ「Pelvis位置が近いBodyは同一人物とみなし、
// 信頼度の高い方だけ採用する」統合ロジックを使うが、Pelvis/Head/Noseだけでなく
// 32関節すべてを pathfinder/person_coordinate_FULL にパブリッシュする。
// 送信レートは独自に定義せず、SkeletonRealtimeMultiがKinectから新しいフレームを
// 受け取るたび(=カメラのCameraFPS設定なり)にそのままパブリッシュする。
// 既存の SkeletonRealtimePublisher (同じMultiPositionシーン内) とはトピックが別なので、
// 同一シーンで両方を同時に動かしても干渉しない。
public class SkeletonRealtimePublisher_FULL : MonoBehaviour
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

    [Header("MQTT")]
    [SerializeField] private string mqttHost = "192.168.236.211";
    [SerializeField] private int mqttPort = 1883;
    [SerializeField] private string mqttUsername = "mqtt-user";
    [SerializeField] private string mqttPassword = "401402";
    [SerializeField] private string topic = "pathfinder/person_coordinate_FULL";
    [SerializeField] private bool connectOnStart = true;
    [Tooltip("30FPSで毎フレームログすると重いのでデフォルトはOFF。")]
    [SerializeField] private bool logPublishedMessage = false;

    private TcpClient client;
    private NetworkStream stream;
    private bool connectionAcknowledged;
    private float lastMqttSendTime;

    private readonly List<PersonTrack> tracks = new List<PersonTrack>();
    private int nextTrackId = 0;

    private static readonly int JointCount = (int)JointId.Count;

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

        // Kinect側のCameraFPS(SkeletonRealtimeMultiでFPS30固定)に合わせて、
        // dev.GetCapture()が新しいフレームを受け取るたびにLatestBodiesが更新される。
        // そのため送信側で独自にFPSを定義してレート制限する必要はなく、
        // 毎Updateで最新のLatestBodiesをそのままパブリッシュすればカメラのFPSに追従する。
        if (IsConnected)
            FuseAndPublish();

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
            Debug.Log($"SkeletonRealtimePublisher_FULL: connecting to MQTT broker {mqttHost}:{mqttPort}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"SkeletonRealtimePublisher_FULL: connection failed. {ex.GetType().Name}: {ex.Message}");
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

                JointSample[] joints = new JointSample[JointCount];

                for (JointId jointId = JointId.Pelvis; jointId < JointId.Count; jointId++)
                {
                    body.TryGetJointPosition(jointId, out Vector3 position);
                    joints[(int)jointId] = new JointSample
                    {
                        name = jointId.ToString(),
                        x = position.x,
                        y = position.y,
                        z = position.z
                    };
                }

                candidates.Add(new Candidate
                {
                    deviceIndex = source.DeviceIndex,
                    bodyId = body.bodyId,
                    pelvisPosition = pelvisPosition,
                    confidence = body.GetAverageConfidence(),
                    joints = joints
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
        PersonPositionFullMessage message = new PersonPositionFullMessage
        {
            label = $"person_{trackId}",
            deviceIndex = candidate.deviceIndex,
            bodyId = candidate.bodyId,
            confidence = candidate.confidence,
            joints = candidate.joints
        };

        string json = JsonUtility.ToJson(message);
        Publish(topic, json);

        if (logPublishedMessage)
            Debug.Log($"SkeletonRealtimePublisher_FULL: published topic={topic}, bytes={Encoding.UTF8.GetByteCount(json)}");
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
        WriteMqttString(body, "unity-skeleton-publisher-full-" + Guid.NewGuid().ToString("N").Substring(0, 8));
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
            Debug.Log($"SkeletonRealtimePublisher_FULL: connected to MQTT broker {mqttHost}:{mqttPort}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"SkeletonRealtimePublisher_FULL: MQTT acknowledgement failed. {ex.GetType().Name}: {ex.Message}");
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
        public JointSample[] joints;
    }

    private class PersonTrack
    {
        public int id;
        public Vector3 position;
        public float lastSeenTime;
    }

    [Serializable]
    private class JointSample
    {
        public string name;
        public float x;
        public float y;
        public float z;
    }

    [Serializable]
    private class PersonPositionFullMessage
    {
        public string label;
        public int deviceIndex;
        public uint bodyId;
        public float confidence;
        public JointSample[] joints;
    }
}
