using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.AI;

// シーン8(8RouteToTargets)用。人の位置から、シーン0(0HomeSetup)で置いた対象物体(Env/TargetObjects.json)すべてへの経路を
// NavMesh 上で計算し、「どこへ向かいそうか」を重みづけして表示する。
// 1PhilipDemo/MultiIntegrationLogger の RouteSub と同じ考え方で、ゴールを同じGameObjectの HomeEnvLoader が再生成した対象物体にしたもの。
//   - 骨格: SkeletonRealtimeSubscriber が MQTT(pathfinder/person_coordinate, シーン6が送る)で受信する
//   - スタート: DefineStart(Start オブジェクト)がハイライト中の人物の Pelvis を床に下ろした位置
//   - NavMesh: NavSub が MQTT(pathfinder/navmesh, シーン7が送る)で受信して再現する
//              または HomeFakeFloorNavMesh がシーン0の補助の床(Env/FakeFloors.json)だけから作る(シーン7を動かさずに試すとき)
//   - ゴール: 対象物体の group ごとに1つ。group に属する物体全体のバウンディングボックスの中心から一番近い NavMesh 上の点
//
// 到達地点の予測(重みづけ):
//   各ゴールのスコア(初期値は 体の向き 3.0 > 移動 2.5 > 頭部方向 0.8 の順に強い) = Head Weight × cos(頭部方向 と 頭→物体 の角度)
//                    + Body Weight × cos(体の向き と 経路の出だしの方向 の角度)
//                    + Move Weight × (歩く速さ) × cos(移動方向 と 経路の出だしの方向 の角度)
//                    - Distance Weight × 経路の長さ(m)
//   スコアを softmax で確率にし(Temperature が大きいほど差が小さくなる)、時間方向に平滑化する。
//   頭部方向・体の向き・移動方向は HomeBodyCues が全関節の骨格(person_coordinate_FULL)から求める。無い手がかりは使わない。
// 表示: すべての経路を出し、確率が高いほど太く・濃い色にする。一番高いゴールを「予測到達地点」として強調する。
[RequireComponent(typeof(HomeEnvLoader))]
public class HomeRouteToTargets : MonoBehaviour, IHomeDataRootProvider
{
    [Header("Data Location")]
    [Tooltip("Env(部屋オブジェクト・対象物体)を読むデータのルートフォルダ。<ここ>/<実験の名前>/Env/ から読む。\n" +
             "空なら既定の Assets/Data/HomeExperiment。シーン0と同じフォルダを指定する。")]
    [HomeDataRootFolder]
    [SerializeField] private string dataRootDirectory = "";

    [Header("Start")]
    [Tooltip("経路のスタート(人の位置)。DefineStart を付けたオブジェクトを割り当てる。空の場合はシーン内の DefineStart を探す。")]
    [SerializeField] private DefineStart start;
    [Tooltip("スタート位置の近くこの半径(m)以内で NavMesh 上の点を探す。")]
    [SerializeField] private float startSearchRadius = 1.0f;
    [Tooltip("Start Search Radius で見つからないときに広げる半径(m)。スタート地点の真下に NavMesh が無いときの救済用。")]
    [SerializeField] private float startFallbackSearchRadius = 3.0f;

    [Header("Goals")]
    [Tooltip("経路を出す対象物体の group。空なら全ての対象物体。")]
    [SerializeField] private string[] targetGroups = new string[0];
    [Tooltip("物体の中心からこの半径(m)以内で NavMesh 上の点を探す(テーブルの上など、物体のすぐ近くの面)。")]
    [SerializeField] private float goalSearchRadius = 0.8f;
    [Tooltip("Goal Search Radius で見つからないとき、物体の真下(スタートと同じ高さ)からこの半径(m)以内で探す(棚・壁の物体など)。")]
    [SerializeField] private float goalFallbackSearchRadius = 1.5f;

    [Header("Route")]
    [Tooltip("この秒数ごとに経路を計算し直す。0 で毎フレーム。(予測と表示は毎フレーム更新する)")]
    [SerializeField, Min(0f)] private float routeUpdateInterval = 0.5f;
    [Tooltip("経路の出だしの方向を、スタートからこの距離(m)進んだ点への向きとする。")]
    [SerializeField] private float routeHeadingDistance = 0.6f;
    [Tooltip("経路の長さから到着までの時間を出すときの移動速度(m/s)。")]
    [SerializeField, Min(0.01f)] private float movementSpeed = 0.5f;

    [Header("Prediction")]
    [Tooltip("頭部方向がその物体を向いているほど高くする重み。")]
    [SerializeField, Min(0f)] private float headWeight = 0.8f;
    [Tooltip("体の向きがその経路の出だしを向いているほど高くする重み。")]
    [SerializeField, Min(0f)] private float bodyWeight = 3.0f;
    [Tooltip("歩いている方向がその経路の出だしと合っているほど高くする重み(歩く速さに比例して効く)。")]
    [SerializeField, Min(0f)] private float moveWeight = 2.5f;
    [Tooltip("経路が長いほど低くする重み(1mあたり)。")]
    [SerializeField, Min(0f)] private float distanceWeight = 0.4f;
    [Tooltip("この速さ(m/s)で歩いているとき、移動方向の重みが最大になる。これより遅い動きは弱めに効く。")]
    [SerializeField, Min(0.01f)] private float walkingSpeed = 0.6f;
    [Tooltip("この速さ(m/s)未満は止まっているとみなし、移動方向を使わない。")]
    [SerializeField, Min(0f)] private float minMovingSpeed = 0.1f;
    [Tooltip("確率にするときの温度。大きいほど確率の差が小さく(なだらかに)なる。")]
    [SerializeField, Min(0.05f)] private float temperature = 1.5f;
    [Tooltip("確率の平滑化の時定数(秒)。大きいほど表示がゆっくり変わる。")]
    [SerializeField, Min(0f)] private float probabilitySmoothingTime = 1.5f;
    [Tooltip("空の場合は同じGameObject・シーン内から探す。")]
    [SerializeField] private HomeBodyCues bodyCues;

    [Header("Route Visual")]
    [SerializeField] private float minRouteLineWidth = 0.015f;
    [SerializeField] private float maxRouteLineWidth = 0.09f;
    [SerializeField] private float routeLineYOffset = 0.05f;
    [Tooltip("確率が低い経路の色")]
    [SerializeField] private Color lowProbabilityColor = new Color(0.55f, 0.65f, 0.85f, 0.25f);
    [Tooltip("確率が高い経路の色")]
    [SerializeField] private Color highProbabilityColor = new Color(1f, 0.45f, 0.15f, 0.95f);
    [Tooltip("予測到達地点(一番確率が高いゴール)の経路の色")]
    [SerializeField] private Color predictedColor = new Color(1f, 0.15f, 0.2f, 1f);

    [Header("Markers")]
    [SerializeField] private bool showMarkers = true;
    [SerializeField] private float markerScale = 0.08f;
    [Tooltip("予測到達地点のマーカーをこの倍率で大きくする。")]
    [SerializeField] private float predictedMarkerScale = 2.2f;
    [SerializeField] private Color startMarkerColor = Color.white;
    [SerializeField] private bool showGoalLabels = true;
    [SerializeField] private float goalLabelCharacterSize = 0.05f;
    [Tooltip("この確率未満のゴールはラベルを薄くする。")]
    [SerializeField, Range(0f, 1f)] private float labelMinProbability = 0.05f;

    [Header("Status")]
    [SerializeField] private bool showStatus = true;
    [SerializeField] private bool logRouteSearch = false;

    private HomeEnvLoader env;
    private SkeletonRealtimeSubscriber skeletonSubscriber;
    private NavSub navSubscriber;
    private HomeFakeFloorNavMesh fakeFloorNavMesh;
    private readonly List<Goal> goals = new List<Goal>();
    private GameObject startMarker;
    private Vector3 startPoint;
    private float lastRouteTime = float.NegativeInfinity;
    private bool hasStartPoint;
    private Goal predictedGoal;
    private Material lineMaterial;
    private GUIStyle statusStyle;

    public string DataRootDirectory => dataRootDirectory;

    // 他のスクリプトから参照する用: group ごとの経路長(m)。届かない対象物体は含まない
    public IEnumerable<KeyValuePair<string, float>> RouteLengths
    {
        get
        {
            foreach (Goal goal in goals)
            {
                if (goal.hasPath)
                    yield return new KeyValuePair<string, float>(goal.group, goal.length);
            }
        }
    }

    // 他のスクリプトから参照する用: group ごとの到達確率(平滑化後, 0〜1)
    public IEnumerable<KeyValuePair<string, float>> Probabilities
    {
        get
        {
            foreach (Goal goal in goals)
                yield return new KeyValuePair<string, float>(goal.group, goal.probability);
        }
    }

    // 予測到達地点の group(無ければ null)
    public string PredictedGroup => predictedGoal?.group;

    public int RouteVersion { get; private set; }

    private class Goal
    {
        public string group;
        public Vector3 center;
        public Vector3 navPoint;
        public bool onNavMesh;
        public bool hasPath;
        public float length;
        public Vector3 heading;
        public NavMeshPath path = new NavMeshPath();
        public Vector3[] linePoints = new Vector3[0];
        public LineRenderer line;
        public GameObject marker;
        public TextMesh label;

        // 予測
        public float score;
        public float targetProbability;
        public float probability;
        public float cosHead = float.NaN;
        public float cosBody = float.NaN;
        public float cosMove = float.NaN;
    }

    private void Awake()
    {
        env = GetComponent<HomeEnvLoader>();
    }

    private void Start()
    {
        if (start == null)
            start = FindObjectOfType<DefineStart>();

        if (bodyCues == null)
            bodyCues = FindObjectOfType<HomeBodyCues>();

        skeletonSubscriber = FindObjectOfType<SkeletonRealtimeSubscriber>();
        navSubscriber = FindObjectOfType<NavSub>();
        fakeFloorNavMesh = GetComponent<HomeFakeFloorNavMesh>();
        // 半透明の部屋・歩行可能領域(HomeRouteSceneVisual)より後に描いて、経路を一番上に見せる
        lineMaterial = new Material(Shader.Find("Sprites/Default")) { renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent + 20 };

        if (start == null)
            Debug.LogError("[HomeRouteToTargets] スタート(DefineStart)がありません。");

        CollectGoals();
    }

    // HomeEnvLoader が Awake で再生成した対象物体を group ごとにまとめる(物体は動かないので最初に1回だけ)
    private void CollectGoals()
    {
        HashSet<string> filter = new HashSet<string>();

        foreach (string group in targetGroups)
        {
            if (!string.IsNullOrWhiteSpace(group))
                filter.Add(group.Trim());
        }

        Dictionary<string, Bounds> boundsByGroup = new Dictionary<string, Bounds>();
        List<string> order = new List<string>();

        foreach (GameObject target in env.TargetObjects)
        {
            if (target == null)
                continue;

            HomeTargetObject marker = target.GetComponent<HomeTargetObject>();
            string group = marker != null && !string.IsNullOrEmpty(marker.group) ? marker.group : target.name;

            if (filter.Count > 0 && !filter.Contains(group))
                continue;

            Bounds bounds = GetWorldBounds(target);

            if (boundsByGroup.TryGetValue(group, out Bounds existing))
            {
                existing.Encapsulate(bounds);
                boundsByGroup[group] = existing;
            }
            else
            {
                boundsByGroup.Add(group, bounds);
                order.Add(group);
            }
        }

        foreach (string group in order)
        {
            Goal goal = new Goal { group = group, center = boundsByGroup[group].center };

            goal.line = new GameObject($"Route_{group}").AddComponent<LineRenderer>();
            goal.line.transform.SetParent(transform, false);
            goal.line.useWorldSpace = true;
            goal.line.sharedMaterial = lineMaterial;
            goal.line.numCornerVertices = 4;
            goal.line.numCapVertices = 4;
            goal.line.positionCount = 0;
            goal.line.enabled = false;
            goals.Add(goal);
        }

        if (goals.Count == 0)
            Debug.LogError("[HomeRouteToTargets] 対象物体がありません。シーン0で部屋オブジェクトの下に物体を置いて保存したか(Env/TargetObjects.json)、Target Groups を確認してください。");
        else
            Debug.Log($"[HomeRouteToTargets] 対象物体 {goals.Count} 個: {string.Join(", ", order)}");
    }

    private static Bounds GetWorldBounds(GameObject target)
    {
        Renderer[] renderers = target.GetComponentsInChildren<Renderer>();

        if (renderers.Length == 0)
            return new Bounds(target.transform.position, Vector3.zero);

        Bounds bounds = renderers[0].bounds;

        for (int i = 1; i < renderers.Length; i++)
            bounds.Encapsulate(renderers[i].bounds);

        return bounds;
    }

    private void Update()
    {
        if (Time.unscaledTime - lastRouteTime >= routeUpdateInterval)
        {
            lastRouteTime = Time.unscaledTime;
            UpdateRoutes();
        }

        if (!hasStartPoint)
            return;

        UpdatePrediction();
        UpdateVisuals();
    }

    // ------------------------------------------------------------
    // Routes
    // ------------------------------------------------------------
    public void UpdateRoutes()
    {
        RouteVersion++;
        hasStartPoint = false;

        if (start == null || !start.HasActivePerson || !TryFindStartPoint(start.transform.position, out startPoint))
        {
            HideAll();
            return;
        }

        hasStartPoint = true;

        foreach (Goal goal in goals)
        {
            goal.hasPath = false;
            goal.onNavMesh = TryFindGoalPoint(goal.center, startPoint.y, out goal.navPoint);

            if (goal.onNavMesh &&
                NavMesh.CalculatePath(startPoint, goal.navPoint, NavMesh.AllAreas, goal.path) &&
                goal.path.status == NavMeshPathStatus.PathComplete &&
                goal.path.corners.Length >= 2)
            {
                goal.hasPath = true;
                goal.length = GetPathLength(goal.path);
                goal.heading = GetHeading(goal.path.corners, routeHeadingDistance);

                Vector3[] corners = goal.path.corners;
                goal.linePoints = new Vector3[corners.Length];

                for (int i = 0; i < corners.Length; i++)
                    goal.linePoints[i] = corners[i] + Vector3.up * routeLineYOffset;
            }

            if (logRouteSearch)
                Debug.Log($"[HomeRouteToTargets] {goal.group}: onNavMesh={goal.onNavMesh}, status={goal.path.status}, length={(goal.hasPath ? goal.length : -1f):F2}, start={startPoint}, goal={goal.navPoint}");
        }
    }

    private bool TryFindStartPoint(Vector3 position, out Vector3 point)
    {
        return TrySample(position, startSearchRadius, out point) ||
               TrySample(position, startFallbackSearchRadius, out point);
    }

    private bool TryFindGoalPoint(Vector3 center, float floorY, out Vector3 point)
    {
        if (TrySample(center, goalSearchRadius, out point))
            return true;

        return TrySample(new Vector3(center.x, floorY, center.z), goalFallbackSearchRadius, out point);
    }

    private static bool TrySample(Vector3 position, float radius, out Vector3 point)
    {
        if (NavMesh.SamplePosition(position, out NavMeshHit hit, Mathf.Max(radius, 0.01f), NavMesh.AllAreas))
        {
            point = hit.position;
            return true;
        }

        point = position;
        return false;
    }

    private static float GetPathLength(NavMeshPath path)
    {
        float length = 0f;
        Vector3[] corners = path.corners;

        for (int i = 1; i < corners.Length; i++)
            length += Vector3.Distance(corners[i - 1], corners[i]);

        return length;
    }

    // スタートから経路に沿って distance 進んだ点への水平方向(経路が短ければ終点への方向)
    private static Vector3 GetHeading(Vector3[] corners, float distance)
    {
        Vector3 origin = corners[0];
        Vector3 target = corners[corners.Length - 1];
        float remaining = distance;

        for (int i = 1; i < corners.Length; i++)
        {
            float segment = Vector3.Distance(corners[i - 1], corners[i]);

            if (segment >= remaining)
            {
                target = Vector3.Lerp(corners[i - 1], corners[i], segment > 1e-5f ? remaining / segment : 0f);
                break;
            }

            remaining -= segment;
        }

        Vector3 heading = Horizontal(target - origin);
        return heading.sqrMagnitude > 1e-6f ? heading.normalized : Vector3.zero;
    }

    private static Vector3 Horizontal(Vector3 v) => new Vector3(v.x, 0f, v.z);

    // ------------------------------------------------------------
    // Prediction
    // ------------------------------------------------------------
    private void UpdatePrediction()
    {
        bool hasHead = bodyCues != null && bodyCues.HasPerson && bodyCues.HasHead;
        bool hasBody = bodyCues != null && bodyCues.HasPerson && bodyCues.HasBody;
        Vector3 velocity = bodyCues != null && bodyCues.HasPerson ? bodyCues.Velocity : Vector3.zero;
        float speed = velocity.magnitude;
        bool moving = speed >= minMovingSpeed;
        float moveStrength = moving ? Mathf.Clamp01(speed / walkingSpeed) : 0f;

        float maxScore = float.NegativeInfinity;

        foreach (Goal goal in goals)
        {
            goal.cosHead = goal.cosBody = goal.cosMove = float.NaN;

            if (!goal.hasPath)
                continue;

            float score = -distanceWeight * goal.length;
            bool alreadyThere = goal.heading == Vector3.zero;

            if (hasHead)
            {
                Vector3 toObject = Horizontal(goal.center - bodyCues.HeadPosition);
                goal.cosHead = toObject.sqrMagnitude > 1e-6f ? Vector3.Dot(bodyCues.HeadForward, toObject.normalized) : 1f;
                score += headWeight * goal.cosHead;
            }

            if (hasBody)
            {
                goal.cosBody = alreadyThere ? 1f : Vector3.Dot(bodyCues.BodyForward, goal.heading);
                score += bodyWeight * goal.cosBody;
            }

            if (moving)
            {
                goal.cosMove = alreadyThere ? 1f : Vector3.Dot(velocity / speed, goal.heading);
                score += moveWeight * moveStrength * goal.cosMove;
            }

            goal.score = score;
            maxScore = Mathf.Max(maxScore, score);
        }

        // softmax(大きい値で exp があふれないよう最大値を引く)
        float sum = 0f;

        foreach (Goal goal in goals)
        {
            goal.targetProbability = goal.hasPath ? Mathf.Exp((goal.score - maxScore) / temperature) : 0f;
            sum += goal.targetProbability;
        }

        float blend = probabilitySmoothingTime <= 0f ? 1f : 1f - Mathf.Exp(-Time.deltaTime / probabilitySmoothingTime);
        float smoothedSum = 0f;

        foreach (Goal goal in goals)
        {
            if (sum > 0f)
                goal.targetProbability /= sum;

            goal.probability = Mathf.Lerp(goal.probability, goal.targetProbability, blend);
            smoothedSum += goal.probability;
        }

        predictedGoal = null;

        foreach (Goal goal in goals)
        {
            if (smoothedSum > 0f)
                goal.probability /= smoothedSum;

            if (goal.hasPath && (predictedGoal == null || goal.probability > predictedGoal.probability))
                predictedGoal = goal;
        }
    }

    // ------------------------------------------------------------
    // Visual
    // ------------------------------------------------------------
    private void UpdateVisuals()
    {
        UpdateMarker(ref startMarker, "RouteStart", startPoint, startMarkerColor, markerScale);

        float maxProbability = predictedGoal != null ? Mathf.Max(predictedGoal.probability, 1e-3f) : 1f;

        // 確率の低い順に描いて、高い経路を上に重ねる
        List<Goal> sorted = new List<Goal>(goals);
        sorted.Sort((a, b) => a.probability.CompareTo(b.probability));

        for (int rank = 0; rank < sorted.Count; rank++)
        {
            Goal goal = sorted[rank];

            if (!goal.hasPath)
            {
                goal.line.enabled = false;

                if (goal.marker != null)
                    goal.marker.SetActive(false);

                continue;
            }

            bool predicted = goal == predictedGoal;
            float t = Mathf.Clamp01(goal.probability / maxProbability);
            Color color = predicted ? predictedColor : Color.Lerp(lowProbabilityColor, highProbabilityColor, t * t);
            float width = Mathf.Lerp(minRouteLineWidth, maxRouteLineWidth, t);

            goal.line.positionCount = goal.linePoints.Length;
            goal.line.SetPositions(goal.linePoints);
            goal.line.startColor = color;
            goal.line.endColor = color;
            goal.line.startWidth = width;
            goal.line.endWidth = width;
            goal.line.sortingOrder = rank;
            goal.line.enabled = true;

            float scale = markerScale * (predicted ? predictedMarkerScale : Mathf.Lerp(0.6f, 1.4f, t));
            Color markerColor = new Color(color.r, color.g, color.b, Mathf.Max(color.a, 0.5f));
            UpdateMarker(ref goal.marker, $"RouteGoal_{goal.group}", goal.navPoint, markerColor, scale);
            UpdateLabel(goal, predicted, markerColor);
        }
    }

    private void UpdateMarker(ref GameObject marker, string name, Vector3 position, Color color, float scale)
    {
        if (!showMarkers)
        {
            if (marker != null)
                marker.SetActive(false);
            return;
        }

        if (marker == null)
        {
            marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = name;
            marker.transform.SetParent(transform, false);
            Destroy(marker.GetComponent<Collider>());
            marker.GetComponent<Renderer>().sharedMaterial = lineMaterial;
        }

        // Sprites/Default は頂点カラーではなく _Color で色を付ける(マテリアルを共有したまま色だけ変える)
        MaterialPropertyBlock block = new MaterialPropertyBlock();
        block.SetColor("_Color", color);
        marker.GetComponent<Renderer>().SetPropertyBlock(block);

        marker.transform.position = position;
        marker.transform.localScale = Vector3.one * Mathf.Max(scale, 0.01f);
        marker.SetActive(true);
    }

    private void UpdateLabel(Goal goal, bool predicted, Color color)
    {
        if (!showGoalLabels || goal.marker == null)
            return;

        if (goal.label == null)
        {
            GameObject text = new GameObject("Label");
            text.transform.SetParent(transform, false);
            goal.label = text.AddComponent<TextMesh>();
            goal.label.anchor = TextAnchor.LowerCenter;
            goal.label.alignment = TextAlignment.Center;
            goal.label.fontSize = 48;
            text.AddComponent<HomeRouteLabelBillboard>();
        }

        bool faint = goal.probability < labelMinProbability && !predicted;
        goal.label.text = predicted
            ? $"▼ {goal.group} {goal.probability * 100f:F0}%"
            : $"{goal.group} {goal.probability * 100f:F0}%";
        goal.label.characterSize = goalLabelCharacterSize * (predicted ? 1.4f : 1f);
        goal.label.color = faint ? new Color(1f, 1f, 1f, 0.35f) : (predicted ? Color.white : new Color(1f, 1f, 1f, 0.85f));
        goal.label.transform.position = goal.marker.transform.position + Vector3.up * (goal.marker.transform.localScale.y * 0.6f + 0.02f);
        goal.label.gameObject.SetActive(true);
    }

    private void HideAll()
    {
        if (startMarker != null)
            startMarker.SetActive(false);

        predictedGoal = null;

        foreach (Goal goal in goals)
        {
            goal.hasPath = false;
            goal.probability = 0f;
            goal.line.positionCount = 0;
            goal.line.enabled = false;

            if (goal.marker != null)
                goal.marker.SetActive(false);

            if (goal.label != null)
                goal.label.gameObject.SetActive(false);
        }
    }

    private void LateUpdate()
    {
        HomeExperimentPaths.ClearUISelection();
    }

    private void OnGUI()
    {
        if (!showStatus)
            return;

        if (statusStyle == null)
            statusStyle = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 16, wordWrap = true, richText = true };

        StringBuilder text = new StringBuilder();
        text.AppendLine($"実験: {env.ExperimentName}   人の位置 → 対象物体 の経路と到達予測");

        if (skeletonSubscriber != null)
            text.AppendLine($"骨格(MQTT): {(skeletonSubscriber.IsConnected ? "接続中" : "未接続")}");

        if (fakeFloorNavMesh != null && fakeFloorNavMesh.isActiveAndEnabled)
            text.AppendLine(fakeFloorNavMesh.IsBuilt
                ? $"NavMesh(FakeFloor): 補助の床 {fakeFloorNavMesh.FloorCount} 枚から作成 / リンク {fakeFloorNavMesh.LinkCount} 本"
                : "NavMesh(FakeFloor): 作れていません。Console を確認してください");

        if (navSubscriber != null)
            text.AppendLine($"NavMesh(MQTT): {(navSubscriber.IsConnected ? "接続中" : "未接続")}" +
                            (navSubscriber.LastTriangleCount > 0 ? $" / 三角形 {navSubscriber.LastTriangleCount}, リンク {navSubscriber.LastLinkCount}" : " / まだ受信していません"));

        if (start == null || !start.HasActivePerson)
            text.AppendLine("スタート: 人物を受信していません");
        else if (!hasStartPoint)
            text.AppendLine($"スタート: {start.ActiveLabel} の近くに NavMesh がありません");
        else
            text.AppendLine($"スタート: {start.ActiveLabel}");

        if (hasStartPoint)
        {
            text.AppendLine(GetCueSummary());

            if (predictedGoal != null)
                text.AppendLine($"<b>予測到達地点: {predictedGoal.group}  {predictedGoal.probability * 100f:F0}%</b>");

            List<Goal> sorted = new List<Goal>(goals);
            sorted.Sort((a, b) => b.probability.CompareTo(a.probability));

            foreach (Goal goal in sorted)
            {
                if (!goal.hasPath)
                {
                    text.AppendLine($"<color=#888888>　{goal.group}: {(goal.onNavMesh ? "届かない" : "近くに NavMesh がない")}</color>");
                    continue;
                }

                int bars = Mathf.RoundToInt(goal.probability * 20f);
                string bar = new string('|', bars).PadRight(20, '.');
                string mark = goal == predictedGoal ? "★" : "　";
                text.AppendLine($"{mark}{goal.group}  {goal.probability * 100f,3:F0}% [{bar}]  {goal.length:F1}m / {goal.length / movementSpeed:F0}秒{GetCueDetail(goal)}");
            }
        }

        GUIContent content = new GUIContent(text.ToString().TrimEnd());
        const float width = 640f;

        GUI.Box(new Rect(10, 10, width, statusStyle.CalcHeight(content, width)), content, statusStyle);
    }

    private string GetCueSummary()
    {
        if (bodyCues == null)
            return "手がかり: HomeBodyCues がありません(経路の長さだけで予測)";

        if (!bodyCues.HasPerson)
            return "手がかり: 全関節の骨格(FULL)を受信していません(経路の長さだけで予測)";

        float speed = bodyCues.Velocity.magnitude;
        return $"手がかり: 頭部方向 {(bodyCues.HasHead ? "○" : "×")} / 体の向き {(bodyCues.HasBody ? "○" : "×")} / " +
               $"移動 {speed:F2} m/s{(speed >= minMovingSpeed ? "" : "(停止)")}";
    }

    private static string GetCueDetail(Goal goal)
    {
        StringBuilder detail = new StringBuilder();

        if (!float.IsNaN(goal.cosHead))
            detail.Append($" 頭{Mathf.Acos(Mathf.Clamp(goal.cosHead, -1f, 1f)) * Mathf.Rad2Deg:F0}°");

        if (!float.IsNaN(goal.cosBody))
            detail.Append($" 体{Mathf.Acos(Mathf.Clamp(goal.cosBody, -1f, 1f)) * Mathf.Rad2Deg:F0}°");

        if (!float.IsNaN(goal.cosMove))
            detail.Append($" 移{Mathf.Acos(Mathf.Clamp(goal.cosMove, -1f, 1f)) * Mathf.Rad2Deg:F0}°");

        return detail.Length > 0 ? $"<color=#AAAAAA>{detail}</color>" : "";
    }
}

// ゴールのラベルを常にカメラの方へ向ける
public class HomeRouteLabelBillboard : MonoBehaviour
{
    private void LateUpdate()
    {
        Camera camera = Camera.main;

        if (camera != null)
            transform.rotation = Quaternion.LookRotation(transform.position - camera.transform.position, camera.transform.up);
    }
}
