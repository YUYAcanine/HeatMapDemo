using System;
using System.Collections.Generic;
using UnityEngine;

// シーン8(8RouteToTargets)用。経路のスタートにしている人物(DefineStart がハイライト中の人)について、
// 行き先の予測に使う手がかりを全関節の骨格(pathfinder/person_coordinate_FULL, シーン6の SkeletonRealtimePublisher_FULL が送る)から求める。
//   - 頭部方向: 両耳の中点 → 鼻(耳が無ければ 頭 → 鼻)。HomeSkeletonBodyVisual(シーン2〜5の頭部方向)と同じ定義
//   - 体の向き: 左右の肩・左右の腰を結ぶ線に垂直な水平方向(肩と腰の平均)
//   - 移動: Pelvis の水平方向の速度(直近 Velocity Window 秒)
// FULL の人物ラベルは person_coordinate と別に振られるので、Pelvis が一番近い人物を同じ人とみなす。
public class HomeBodyCues : MonoBehaviour
{
    [Header("Source")]
    [Tooltip("空の場合はシーン内から自動で探す。")]
    [SerializeField] private SkeletonRealtimeSubscriber_FULL fullSubscriber;
    [Tooltip("経路のスタートにしている人物。空の場合はシーン内の DefineStart を探す。")]
    [SerializeField] private DefineStart start;
    [SerializeField] private SkeletonRealtimeSubscriber skeletonSubscriber;

    [Header("Matching")]
    [Tooltip("スタートの人物の Pelvis からこの距離(m)以内にいる FULL の人物を同じ人とみなす。")]
    [SerializeField] private float matchDistance = 0.8f;
    [Tooltip("この秒数より古い FULL の骨格は使わない。")]
    [SerializeField] private float staleSeconds = 1.0f;

    [Header("Body Direction")]
    [Tooltip("体の向きの矢印(水色)が背中側を向いてしまうときにオンにする。")]
    [SerializeField] private bool flipBodyForward = false;

    [Header("Smoothing")]
    [Tooltip("向きの平滑化の時定数(秒)。大きいほどゆっくり追従する。")]
    [SerializeField] private float directionSmoothingTime = 0.2f;
    [Tooltip("速度を求める時間幅(秒)。")]
    [SerializeField] private float velocityWindow = 0.6f;

    [Header("Visual")]
    [SerializeField] private bool showCues = true;
    [SerializeField] private float cueLength = 0.6f;
    [SerializeField] private float cueWidth = 0.02f;
    [SerializeField] private Color headColor = new Color(1f, 0.85f, 0.2f, 0.95f);
    [SerializeField] private Color bodyColor = new Color(0.3f, 0.85f, 1f, 0.95f);
    [SerializeField] private Color moveColor = new Color(0.6f, 1f, 0.4f, 0.95f);

    [Header("Skeleton")]
    [Tooltip("スタートの人物の骨格(全関節)を表示する。")]
    [SerializeField] private bool showSkeleton = true;
    [SerializeField] private Color skeletonColor = new Color(1f, 1f, 1f, 0.95f);
    [SerializeField] private float skeletonJointScale = 0.045f;
    [SerializeField] private float skeletonLineWidth = 0.02f;

    // 現在の手がかり(すべて水平・正規化済み。無いときは Has* が false)
    public bool HasPerson { get; private set; }
    public bool HasHead { get; private set; }
    public bool HasBody { get; private set; }
    public Vector3 HeadPosition { get; private set; }
    public Vector3 HeadForward { get; private set; }
    public Vector3 BodyForward { get; private set; }
    public Vector3 Pelvis { get; private set; }
    public Vector3 Velocity { get; private set; }
    public string MatchedLabel { get; private set; }

    [Serializable]
    private class JointSample
    {
        public string name;
        public float x;
        public float y;
        public float z;
    }

    [Serializable]
    private class FullMessage
    {
        public string label;
        public JointSample[] joints;
    }

    private class Person
    {
        public Dictionary<string, Vector3> joints = new Dictionary<string, Vector3>();
        public float receivedTime;
    }

    private readonly Dictionary<string, Person> persons = new Dictionary<string, Person>();
    private readonly Queue<KeyValuePair<float, Vector3>> pelvisHistory = new Queue<KeyValuePair<float, Vector3>>();
    private LineRenderer headLine;
    private LineRenderer bodyLine;
    private LineRenderer moveLine;
    private Material lineMaterial;
    private Person matchedPerson;
    private HomeSkeletonBodyVisual skeletonVisual;
    private readonly List<HomeSkeletonJoint> skeletonJoints = new List<HomeSkeletonJoint>();

    private void Start()
    {
        if (fullSubscriber == null)
            fullSubscriber = FindObjectOfType<SkeletonRealtimeSubscriber_FULL>();

        if (start == null)
            start = FindObjectOfType<DefineStart>();

        if (skeletonSubscriber == null)
            skeletonSubscriber = FindObjectOfType<SkeletonRealtimeSubscriber>();

        if (fullSubscriber != null)
            fullSubscriber.OnMessageReceived += HandleMessage;
        else
            Debug.LogWarning("[HomeBodyCues] SkeletonRealtimeSubscriber_FULL がないため、頭部方向・体の向きは使えません(位置と距離だけで予測します)。");
    }

    private void OnDestroy()
    {
        if (fullSubscriber != null)
            fullSubscriber.OnMessageReceived -= HandleMessage;
    }

    private void HandleMessage(string topic, string json)
    {
        FullMessage message;

        try
        {
            message = JsonUtility.FromJson<FullMessage>(json);
        }
        catch (Exception)
        {
            return;
        }

        if (message == null || string.IsNullOrEmpty(message.label) || message.joints == null)
            return;

        if (!persons.TryGetValue(message.label, out Person person))
        {
            person = new Person();
            persons.Add(message.label, person);
        }

        person.joints.Clear();

        foreach (JointSample joint in message.joints)
        {
            Vector3 position = new Vector3(joint.x, joint.y, joint.z);

            // 送信側で取れなかった関節は (0,0,0) になる
            if (joint.name != null && position.sqrMagnitude > 1e-8f)
                person.joints[joint.name] = position;
        }

        person.receivedTime = Time.unscaledTime;
    }

    private void Update()
    {
        UpdateCues();
        UpdateVisual();
    }

    private void UpdateCues()
    {
        HasPerson = false;
        HasHead = false;
        HasBody = false;
        matchedPerson = null;

        if (start == null || !start.HasActivePerson)
        {
            ResetHistory();
            return;
        }

        Vector3 reference = skeletonSubscriber != null && skeletonSubscriber.HasShortestPerson
            ? skeletonSubscriber.ShortestPersonTransform.position
            : start.transform.position;

        Person matched = null;
        string matchedLabel = null;
        float best = matchDistance;

        foreach (KeyValuePair<string, Person> pair in persons)
        {
            if (Time.unscaledTime - pair.Value.receivedTime > staleSeconds ||
                !pair.Value.joints.TryGetValue("Pelvis", out Vector3 pelvis))
                continue;

            float distance = Horizontal(pelvis - reference).magnitude;

            if (distance <= best)
            {
                best = distance;
                matched = pair.Value;
                matchedLabel = pair.Key;
            }
        }

        matchedPerson = matched;

        if (matched == null)
        {
            ResetHistory();
            return;
        }

        if (matchedLabel != MatchedLabel)
            ResetHistory();

        MatchedLabel = matchedLabel;
        HasPerson = true;
        Pelvis = matched.joints["Pelvis"];

        float blend = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(directionSmoothingTime, 0.001f));

        if (TryGetHeadForward(matched, out Vector3 headPosition, out Vector3 headForward))
        {
            HeadPosition = headPosition;
            HeadForward = SmoothDirection(HeadForward, headForward, blend);
            HasHead = true;
        }

        if (TryGetBodyForward(matched, out Vector3 bodyForward))
        {
            if (flipBodyForward)
                bodyForward = -bodyForward;

            BodyForward = SmoothDirection(BodyForward, bodyForward, blend);
            HasBody = true;
        }

        UpdateVelocity(matched.receivedTime, Pelvis);
    }

    private static bool TryGetHeadForward(Person person, out Vector3 headPosition, out Vector3 forward)
    {
        forward = Vector3.zero;

        if (!person.joints.TryGetValue("Head", out headPosition) ||
            !person.joints.TryGetValue("Nose", out Vector3 nose))
            return false;

        Vector3 from = person.joints.TryGetValue("EarLeft", out Vector3 earLeft) &&
                       person.joints.TryGetValue("EarRight", out Vector3 earRight)
            ? (earLeft + earRight) * 0.5f
            : headPosition;

        forward = Horizontal(nose - from);

        if (forward.sqrMagnitude < 1e-6f)
            return false;

        forward.Normalize();
        return true;
    }

    // 左→右の線と上向きの外積が体の正面(関節は部屋座標で、人物から見た左右)
    private static bool TryGetBodyForward(Person person, out Vector3 forward)
    {
        Vector3 sum = Vector3.zero;

        if (person.joints.TryGetValue("ShoulderLeft", out Vector3 shoulderLeft) &&
            person.joints.TryGetValue("ShoulderRight", out Vector3 shoulderRight))
            sum += Horizontal(Vector3.Cross(shoulderRight - shoulderLeft, Vector3.up));

        if (person.joints.TryGetValue("HipLeft", out Vector3 hipLeft) &&
            person.joints.TryGetValue("HipRight", out Vector3 hipRight))
            sum += Horizontal(Vector3.Cross(hipRight - hipLeft, Vector3.up));

        forward = sum.sqrMagnitude > 1e-8f ? sum.normalized : Vector3.zero;
        return sum.sqrMagnitude > 1e-8f;
    }

    private void UpdateVelocity(float time, Vector3 pelvis)
    {
        if (pelvisHistory.Count == 0 || time > LastHistoryTime())
            pelvisHistory.Enqueue(new KeyValuePair<float, Vector3>(time, pelvis));

        while (pelvisHistory.Count > 2 && time - pelvisHistory.Peek().Key > velocityWindow)
            pelvisHistory.Dequeue();

        KeyValuePair<float, Vector3> oldest = pelvisHistory.Peek();
        float duration = time - oldest.Key;
        Velocity = duration > 0.05f ? Horizontal(pelvis - oldest.Value) / duration : Vector3.zero;
    }

    private float LastHistoryTime()
    {
        float last = float.NegativeInfinity;

        foreach (KeyValuePair<float, Vector3> entry in pelvisHistory)
            last = entry.Key;

        return last;
    }

    private void ResetHistory()
    {
        pelvisHistory.Clear();
        Velocity = Vector3.zero;
        MatchedLabel = null;
    }

    private static Vector3 SmoothDirection(Vector3 current, Vector3 target, float blend)
    {
        if (current.sqrMagnitude < 1e-6f)
            return target;

        Vector3 result = Vector3.Slerp(current, target, blend);
        return result.sqrMagnitude > 1e-6f ? result.normalized : target;
    }

    private static Vector3 Horizontal(Vector3 v) => new Vector3(v.x, 0f, v.z);

    // ------------------------------------------------------------
    // Visual: 頭部方向(黄)・体の向き(水色)・移動方向(黄緑)の矢印
    // ------------------------------------------------------------
    private void UpdateVisual()
    {
        UpdateSkeleton();

        if (!showCues)
        {
            SetLine(ref headLine, "Cue_Head", false, Vector3.zero, Vector3.zero, headColor);
            SetLine(ref bodyLine, "Cue_Body", false, Vector3.zero, Vector3.zero, bodyColor);
            SetLine(ref moveLine, "Cue_Move", false, Vector3.zero, Vector3.zero, moveColor);
            return;
        }

        SetLine(ref headLine, "Cue_Head", HasPerson && HasHead,
            HeadPosition, HeadPosition + HeadForward * cueLength, headColor);
        SetLine(ref bodyLine, "Cue_Body", HasPerson && HasBody,
            Pelvis, Pelvis + BodyForward * cueLength, bodyColor);

        float speed = Velocity.magnitude;
        Vector3 floor = start != null ? start.transform.position + Vector3.up * 0.03f : Pelvis;
        SetLine(ref moveLine, "Cue_Move", HasPerson && speed > 0.1f,
            floor, floor + Velocity.normalized * cueLength * Mathf.Clamp01(speed / 1f), moveColor);
    }

    // スタートの人物の骨格。半透明の部屋・歩行可能領域より後に描く
    private void UpdateSkeleton()
    {
        bool visible = showSkeleton && HasPerson && matchedPerson != null;

        if (!visible)
        {
            skeletonVisual?.SetVisible(false);
            return;
        }

        if (skeletonVisual == null)
        {
            skeletonVisual = new HomeSkeletonBodyVisual(transform, "StartPersonSkeleton", skeletonColor,
                new HomeSkeletonBodyVisual.Style
                {
                    lineMaterial = GetLineMaterial(),
                    jointScale = skeletonJointScale,
                    lineWidth = skeletonLineWidth,
                    showHeadDirection = false
                });
        }

        skeletonJoints.Clear();

        foreach (KeyValuePair<string, Vector3> joint in matchedPerson.joints)
            skeletonJoints.Add(new HomeSkeletonJoint { jointId = joint.Key, position = joint.Value, confidence = 2 });

        skeletonVisual.Apply(skeletonJoints);
    }

    private Material GetLineMaterial()
    {
        if (lineMaterial == null)
            lineMaterial = new Material(Shader.Find("Sprites/Default")) { renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent + 30 };

        return lineMaterial;
    }

    private void SetLine(ref LineRenderer line, string name, bool visible, Vector3 from, Vector3 to, Color color)
    {
        if (line == null)
        {
            if (!visible)
                return;

            line = new GameObject(name).AddComponent<LineRenderer>();
            line.transform.SetParent(transform, false);
            line.useWorldSpace = true;
            line.sharedMaterial = GetLineMaterial();
            line.positionCount = 2;
            line.numCapVertices = 4;
        }

        line.enabled = visible;

        if (!visible)
            return;

        line.startColor = color;
        line.endColor = new Color(color.r, color.g, color.b, color.a * 0.3f);
        line.startWidth = cueWidth;
        line.endWidth = cueWidth * 0.3f;
        line.SetPosition(0, from);
        line.SetPosition(1, to);
    }
}
