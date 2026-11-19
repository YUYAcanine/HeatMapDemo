using System;
using System.Collections.Generic;
using UnityEngine;

// シーン8(8RouteToTargets)用。全関節の骨格(pathfinder/person_coordinate_FULL, シーン6の SkeletonRealtimePublisher_FULL が送る)だけを使って、
// 経路のスタートにする人物と、行き先の予測に使う手がかりを求める。
//   - スタートの人物: 受信中の人物のうち、頭〜Pelvis の距離が一番短い人(DefineStart / SkeletonRealtimeSubscriber と同じ選び方 = 子供)
//   - スタート位置: その人の Pelvis の真下にある一番近い床(Collider。FakeFloor など)の高さに下ろした位置
//   - 頭部方向: 両耳の中点 → 鼻(耳が無ければ 頭 → 鼻)。HomeSkeletonBodyVisual(シーン2〜5の頭部方向)と同じ定義
//   - 体の向き: 左右の肩・左右の腰を結ぶ線に垂直な水平方向(肩と腰の平均)
//   - 体の向き・移動は直近 Cue Averaging Window 秒(既定 3 秒)の平均を使う
//     (体の向き = その間の向きの平均 / 移動 = その間に Pelvis が動いた量 ÷ 時間 = 平均の速度)
public class HomeBodyCues : MonoBehaviour
{
    [Header("Source")]
    [Tooltip("空の場合はシーン内から自動で探す。")]
    [SerializeField] private SkeletonRealtimeSubscriber_FULL fullSubscriber;

    [Header("Person Selection")]
    [Tooltip("この秒数より古い骨格は使わない(いなくなった人)。")]
    [SerializeField] private float staleSeconds = 1.0f;
    [Tooltip("頭〜Pelvis の距離がこれより短い骨格は誤検出とみなして選ばない(m)。")]
    [SerializeField] private float minValidHeadPelvisDistance = 0.05f;
    [Tooltip("今の人物より頭〜Pelvis の距離がこれ以上(m)短い人が来たときだけ切り替える(2人の距離が近いときにちらつかないように)。")]
    [SerializeField] private float switchMargin = 0.05f;

    [Header("Floor Snapping")]
    [Tooltip("スタート位置の高さを、Pelvis の真下にある一番近い床(Collider)の高さにする。オフなら Pelvis の高さのまま。")]
    [SerializeField] private bool snapToFloor = true;
    [SerializeField] private LayerMask floorLayerMask = ~0;
    [Tooltip("Pelvis からこの高さ(m)上から真下に床を探す。")]
    [SerializeField] private float raycastStartHeight = 1.5f;
    [SerializeField] private float floorSearchDistance = 5f;
    [Tooltip("Pelvis の真下だけでなく、この半径(m)の範囲も含めて床を探す。")]
    [SerializeField] private float floorSearchRadius = 0.3f;

    [Header("Body Direction")]
    [Tooltip("体の向きの矢印(水色)が背中側を向いてしまうときにオンにする。")]
    [SerializeField] private bool flipBodyForward = false;

    [Header("Averaging")]
    [Tooltip("頭部方向の平滑化の時定数(秒)。大きいほどゆっくり追従する。")]
    [SerializeField] private float directionSmoothingTime = 0.2f;
    [Tooltip("体の向きと移動(位置の変化)は、直近この秒数の平均を使う。")]
    [SerializeField, Min(0.1f)] private float cueAveragingWindow = 3f;

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
    // 経路のスタート位置(Pelvis を床に下ろした位置)
    public Vector3 StartPosition { get; private set; }
    public float HeadPelvisDistance { get; private set; }
    public bool IsConnected => fullSubscriber != null && fullSubscriber.IsConnected;

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
    private struct CueSample
    {
        public float time;
        public Vector3 pelvis;
        public Vector3 bodyForward;   // 取れなかったときは zero
    }

    private readonly Queue<CueSample> history = new Queue<CueSample>();
    private float lastSampleTime = float.NegativeInfinity;

    // 直近 Cue Averaging Window 秒の Pelvis の平均位置
    public Vector3 AveragePelvis { get; private set; }
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

        if (fullSubscriber != null)
            fullSubscriber.OnMessageReceived += HandleMessage;
        else
            Debug.LogError("[HomeBodyCues] SkeletonRealtimeSubscriber_FULL がないため、人物を受け取れません。");
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

        Person matched = SelectPerson(out string matchedLabel, out float headPelvis);
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
        HeadPelvisDistance = headPelvis;
        Pelvis = matched.joints["Pelvis"];
        StartPosition = SnapToFloor(Pelvis);

        float blend = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(directionSmoothingTime, 0.001f));

        if (TryGetHeadForward(matched, out Vector3 headPosition, out Vector3 headForward))
        {
            HeadPosition = headPosition;
            HeadForward = SmoothDirection(HeadForward, headForward, blend);
            HasHead = true;
        }

        Vector3 bodyForward = Vector3.zero;

        if (TryGetBodyForward(matched, out bodyForward) && flipBodyForward)
            bodyForward = -bodyForward;

        UpdateAverages(matched.receivedTime, Pelvis, bodyForward);
    }

    // 受信中の人物のうち、頭〜Pelvis の距離が一番短い人。今の人物は switchMargin だけ優先する
    private Person SelectPerson(out string label, out float headPelvis)
    {
        Person best = null;
        label = null;
        headPelvis = 0f;
        float bestScore = float.MaxValue;

        foreach (KeyValuePair<string, Person> pair in persons)
        {
            Person person = pair.Value;

            if (Time.unscaledTime - person.receivedTime > staleSeconds ||
                !person.joints.TryGetValue("Pelvis", out Vector3 pelvis) ||
                !person.joints.TryGetValue("Head", out Vector3 head))
                continue;

            float distance = Vector3.Distance(head, pelvis);

            if (distance < minValidHeadPelvisDistance)
                continue;

            float score = pair.Key == MatchedLabel ? distance - switchMargin : distance;

            if (score < bestScore)
            {
                bestScore = score;
                best = person;
                label = pair.Key;
                headPelvis = distance;
            }
        }

        return best;
    }

    // DefineStart と同じ: Pelvis の真下(半径 floorSearchRadius)で Pelvis より低い一番近い床の高さに下ろす
    private Vector3 SnapToFloor(Vector3 pelvis)
    {
        if (!snapToFloor)
            return pelvis;

        RaycastHit[] hits = Physics.SphereCastAll(
            pelvis + Vector3.up * raycastStartHeight,
            Mathf.Max(floorSearchRadius, 0.001f),
            Vector3.down,
            raycastStartHeight + floorSearchDistance,
            floorLayerMask,
            QueryTriggerInteraction.Ignore);

        float nearest = float.MaxValue;
        Vector3 result = pelvis;

        foreach (RaycastHit hit in hits)
        {
            if (hit.point.y >= pelvis.y || hit.distance >= nearest)
                continue;

            nearest = hit.distance;
            result.y = hit.point.y;
        }

        return result;
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

    // 新しい骨格が届いたときだけ記録し、直近 cueAveragingWindow 秒で平均する
    private void UpdateAverages(float time, Vector3 pelvis, Vector3 bodyForward)
    {
        if (time > lastSampleTime)
        {
            history.Enqueue(new CueSample { time = time, pelvis = pelvis, bodyForward = bodyForward });
            lastSampleTime = time;
        }

        while (history.Count > 1 && time - history.Peek().time > cueAveragingWindow)
            history.Dequeue();

        Vector3 forwardSum = Vector3.zero;
        Vector3 pelvisSum = Vector3.zero;

        foreach (CueSample sample in history)
        {
            forwardSum += sample.bodyForward;
            pelvisSum += sample.pelvis;
        }

        AveragePelvis = pelvisSum / history.Count;
        HasBody = forwardSum.sqrMagnitude > 1e-6f;
        BodyForward = HasBody ? forwardSum.normalized : Vector3.zero;

        CueSample oldest = history.Peek();
        float duration = time - oldest.time;
        Velocity = duration > 0.2f ? Horizontal(pelvis - oldest.pelvis) / duration : Vector3.zero;
    }

    private void ResetHistory()
    {
        history.Clear();
        lastSampleTime = float.NegativeInfinity;
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
        Vector3 floor = StartPosition + Vector3.up * 0.03f;
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
