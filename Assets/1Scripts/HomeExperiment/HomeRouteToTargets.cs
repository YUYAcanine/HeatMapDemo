using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.AI;

// シーン8(8RouteToTargets)用。人の位置から、シーン0(0HomeSetup)で置いた対象物体(Env/TargetObjects.json)すべてへの経路を
// NavMesh 上で計算し、「どこへ向かいそうか」を重みづけして表示する。
// 1PhilipDemo/MultiIntegrationLogger の RouteSub と同じ考え方で、ゴールを同じGameObjectの HomeEnvLoader が再生成した対象物体にしたもの。
//   - 骨格: SkeletonRealtimeSubscriber_FULL が MQTT(pathfinder/person_coordinate_FULL, シーン6が送る)で全関節を受信する
//   - スタート: HomeBodyCues が選んだ人物(頭〜Pelvis が一番短い人)の Pelvis を床に下ろした位置
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
    [SerializeField, Min(0f)] private float distanceWeight = 0.25f;
    [Tooltip("この速さ(m/s)で歩いているとき、移動方向の重みが最大になる。これより遅い動きは弱めに効く。")]
    [SerializeField, Min(0.01f)] private float walkingSpeed = 0.6f;
    [Tooltip("この速さ(m/s)未満は止まっているとみなし、移動方向を使わない。")]
    [SerializeField, Min(0f)] private float minMovingSpeed = 0.1f;
    [Tooltip("確率にするときの温度。大きいほど確率の差が小さく(なだらかに)なる。")]
    [SerializeField, Min(0.05f)] private float temperature = 1.5f;
    [Tooltip("確率の平滑化の時定数(秒)。大きいほど表示がゆっくり変わる。")]
    [SerializeField, Min(0f)] private float probabilitySmoothingTime = 1.5f;

    [Header("Prediction - Decay")]
    [Tooltip("時間がたつにつれて少しずつ確信を下げる(1位の確率が小さく減り、他が少し増える)。体の向きや動きに関係なく常に効く。\n" +
             "1位の物体が入れ替わったとき・人物が変わったときに元に戻す。")]
    [SerializeField] private bool decayOverTime = true;
    [Tooltip("1位の物体が決まってからこの秒数たったら減衰を始める。")]
    [SerializeField, Min(0f)] private float decayDelay = 2f;
    [Tooltip("減衰の速さ。1秒ごとに Temperature をこの割合だけ大きくする(0.05 なら10秒で1.5倍)。")]
    [SerializeField, Min(0f)] private float decayRate = 0.05f;
    [Tooltip("減衰で Temperature を最大で何倍まで大きくするか(これ以上は確率が平らにならない)。")]
    [SerializeField, Min(1f)] private float maxDecayTemperatureScale = 2f;
    [Tooltip("スタートの人物と手がかり(全関節の骨格から求める)。空の場合はシーン内から探す。")]
    [SerializeField] private HomeBodyCues bodyCues;

    [Header("Route Visual")]
    [SerializeField] private float minRouteLineWidth = 0.025f;
    [SerializeField] private float maxRouteLineWidth = 0.09f;
    [SerializeField] private float routeLineYOffset = 0.05f;
    [Tooltip("可能性がほとんどない(Possible Probability 未満の)経路の色")]
    [SerializeField] private Color lowProbabilityColor = new Color(0.45f, 0.75f, 1f, 0.75f);
    [Tooltip("この確率以上のゴールは「可能性あり」としてオレンジ系の経路で残す。")]
    [SerializeField, Range(0f, 1f)] private float possibleProbability = 0.05f;
    [Tooltip("可能性ありの経路のうち、確率が低い方の色(確率が上がるほど High Probability Color に近づく)")]
    [SerializeField] private Color possibleColor = new Color(1f, 0.95f, 0.35f, 0.8f);
    [Tooltip("可能性ありの経路の最小の太さ")]
    [SerializeField] private float possibleRouteLineWidth = 0.045f;
    [Tooltip("確率が高い経路の色")]
    [SerializeField] private Color highProbabilityColor = new Color(1f, 0.45f, 0f, 1f);
    [Tooltip("予測到達地点(一番確率が高いゴール)の経路の色")]
    [SerializeField] private Color predictedColor = new Color(1f, 0.15f, 0.2f, 1f);

    [Header("Markers")]
    [SerializeField] private bool showMarkers = true;
    [SerializeField] private float markerScale = 0.14f;
    [Tooltip("予測到達地点のマーカーをこの倍率で大きくする。")]
    [SerializeField] private float predictedMarkerScale = 1.8f;
    [SerializeField] private Color startMarkerColor = Color.white;
    [SerializeField] private bool showGoalLabels = true;
    [Tooltip("ラベルの文字の大きさ(画面上のピクセル)。遠近に関係なく同じ大きさで描く。")]
    [SerializeField] private int goalLabelFontSize = 26;
    [Tooltip("予測到達地点のラベルの文字の大きさ(ピクセル)。")]
    [SerializeField] private int predictedLabelFontSize = 34;
    [SerializeField] private Color labelBackgroundColor = new Color(0f, 0f, 0f, 0.7f);
    [Tooltip("この確率未満のゴールはラベルを薄くする。")]
    [SerializeField, Range(0f, 1f)] private float labelMinProbability = 0.05f;

    [Header("Status")]
    [SerializeField] private bool showStatus = true;
    [Tooltip("到達予測の表に出す物体の数(確率の高い順)。")]
    [SerializeField, Min(1)] private int statusTopCount = 3;
    [Tooltip("到達予測の表の文字の大きさ(ピクセル)。")]
    [SerializeField] private int statusFontSize = 40;
    [SerializeField] private float statusWidth = 640f;
    [Tooltip("オンで画面の右上、オフで左上に出す。")]
    [SerializeField] private bool statusAtRight = true;
    [Tooltip("人物・手がかりの角度・NavMesh などの詳細も小さく出す(調整用)。")]
    [SerializeField] private bool showStatusDetail = false;
    [SerializeField] private bool logRouteSearch = false;

    private HomeEnvLoader env;
    private NavSub navSubscriber;
    private HomeFakeFloorNavMesh fakeFloorNavMesh;
    private readonly List<Goal> goals = new List<Goal>();
    private GameObject startMarker;
    private Vector3 startPoint;
    private float lastRouteTime = float.NegativeInfinity;
    private bool hasStartPoint;
    private Goal predictedGoal;

    // 減衰用: 今の1位の物体・人物になってからの時間
    private float decayTime;
    private string decayGroup;
    private string decayPerson;
    private float decayTemperatureScale = 1f;
    private Material lineMaterial;
    private GUIStyle labelStyle;
    private Texture2D whiteTexture;
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
        if (bodyCues == null)
            bodyCues = FindObjectOfType<HomeBodyCues>();

        navSubscriber = FindObjectOfType<NavSub>();
        fakeFloorNavMesh = GetComponent<HomeFakeFloorNavMesh>();
        // 半透明の部屋・歩行可能領域(HomeRouteSceneVisual)より後に描いて、経路を一番上に見せる
        lineMaterial = new Material(Shader.Find("Sprites/Default")) { renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent + 20 };

        if (bodyCues == null)
            Debug.LogError("[HomeRouteToTargets] スタートの人物を決める HomeBodyCues がありません。");

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

    // 対象物体の表示を消していても(HomeRouteSceneVisual の Hide Targets)中心が取れるよう、メッシュの形から求める
    private static Bounds GetWorldBounds(GameObject target)
    {
        bool hasBounds = false;
        Bounds meshBounds = new Bounds();

        foreach (MeshFilter meshFilter in target.GetComponentsInChildren<MeshFilter>())
        {
            if (meshFilter.sharedMesh == null)
                continue;

            Bounds local = meshFilter.sharedMesh.bounds;
            Matrix4x4 matrix = meshFilter.transform.localToWorldMatrix;

            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = local.center + Vector3.Scale(local.extents,
                    new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                Vector3 world = matrix.MultiplyPoint3x4(corner);

                if (hasBounds)
                    meshBounds.Encapsulate(world);
                else
                    meshBounds = new Bounds(world, Vector3.zero);

                hasBounds = true;
            }
        }

        if (hasBounds)
            return meshBounds;

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

        if (bodyCues == null || !bodyCues.HasPerson || !TryFindStartPoint(bodyCues.StartPosition, out startPoint))
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

        UpdateDecay();
        float effectiveTemperature = temperature * decayTemperatureScale;

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
            goal.targetProbability = goal.hasPath ? Mathf.Exp((goal.score - maxScore) / effectiveTemperature) : 0f;
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

    // 時間がたつにつれて Temperature を少しずつ上げて確率をなだらかにする(状態に関係なく常に)。
    // 1位の物体が入れ替わった・人物が変わったときは元に戻す。predictedGoal は前のフレームの結果を見る。
    private void UpdateDecay()
    {
        string group = predictedGoal?.group;
        string person = bodyCues != null ? bodyCues.MatchedLabel : null;

        if (!decayOverTime || group != decayGroup || person != decayPerson)
        {
            decayTime = 0f;
            decayGroup = group;
            decayPerson = person;
        }
        else
        {
            decayTime += Time.deltaTime;
        }

        float decaying = Mathf.Max(decayTime - decayDelay, 0f);
        decayTemperatureScale = Mathf.Min(1f + decayRate * decaying, maxDecayTemperatureScale);
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
            // 一番高いものは赤、可能性があるものはオレンジ系(確率が高いほど濃く太く)、ほぼ無いものは細い水色
            bool possible = goal.probability >= possibleProbability;
            // 「可能性あり」の下限〜一番高い確率 を 0〜1 に広げて、黄色(低い)→オレンジ(高い)の差がはっきり出るようにする
            float possibleT = Mathf.Clamp01(Mathf.InverseLerp(possibleProbability, maxProbability, goal.probability));
            Color color = predicted ? predictedColor
                : possible ? Color.Lerp(possibleColor, highProbabilityColor, possibleT)
                : lowProbabilityColor;
            float width = predicted ? maxRouteLineWidth
                : possible ? Mathf.Lerp(possibleRouteLineWidth, maxRouteLineWidth, possibleT)
                : minRouteLineWidth;

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
        }
    }

    private void LateUpdate()
    {
        HomeExperimentPaths.ClearUISelection();
    }

    private void OnGUI()
    {
        if (Event.current.type != EventType.Repaint)
            return;

        EnsureGuiResources();
        DrawGoalLabels();

        if (showStatus)
            DrawStatusPanel();
    }

    private void EnsureGuiResources()
    {
        if (whiteTexture == null)
        {
            whiteTexture = new Texture2D(1, 1);
            whiteTexture.SetPixel(0, 0, Color.white);
            whiteTexture.Apply();
        }

        if (labelStyle == null)
        {
            labelStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
                padding = new RectOffset(8, 8, 3, 3),
                wordWrap = false
            };
            labelStyle.normal.background = whiteTexture;
        }

        if (statusStyle == null)
        {
            statusStyle = new GUIStyle(GUI.skin.label) { wordWrap = false, richText = true, clipping = TextClipping.Clip };
            statusStyle.normal.textColor = Color.white;
        }
    }

    // ------------------------------------------------------------
    // Status panel (top right): top N destinations with probability bars
    // ------------------------------------------------------------
    private void DrawStatusPanel()
    {
        float padding = statusFontSize * 0.45f;
        float width = statusWidth;
        float x = statusAtRight ? Screen.width - width - 12f : 12f;
        float y = 12f;
        int headerSize = Mathf.RoundToInt(statusFontSize * 0.75f);
        int smallSize = Mathf.Max(Mathf.RoundToInt(statusFontSize * 0.45f), 12);
        float rowHeight = statusFontSize * 1.35f;
        float barHeight = Mathf.Max(statusFontSize * 0.22f, 4f);

        string problem = GetStatusProblem();
        List<Goal> top = new List<Goal>();

        if (problem == null)
        {
            foreach (Goal goal in goals)
            {
                if (goal.hasPath)
                    top.Add(goal);
            }

            top.Sort((a, b) => b.probability.CompareTo(a.probability));

            if (top.Count > statusTopCount)
                top.RemoveRange(statusTopCount, top.Count - statusTopCount);

            if (top.Count == 0)
                problem = "No reachable targets";
        }

        string detail = showStatusDetail ? GetStatusDetail() : null;
        int detailLines = string.IsNullOrEmpty(detail) ? 0 : detail.Split('\n').Length;

        float height = padding * 2f + headerSize * 1.5f +
                       (problem != null ? smallSize * 1.6f : top.Count * (rowHeight + barHeight + 6f)) +
                       detailLines * smallSize * 1.4f;

        DrawRect(new Rect(x, y, width, height), new Color(0f, 0f, 0f, 0.72f));

        float cursor = y + padding;
        DrawText(new Rect(x + padding, cursor, width - padding * 2f, headerSize * 1.5f),
            "<b>Predicted destination</b>", headerSize, new Color(0.85f, 0.85f, 0.85f), TextAnchor.UpperLeft);
        cursor += headerSize * 1.5f;

        if (problem != null)
        {
            DrawText(new Rect(x + padding, cursor, width - padding * 2f, smallSize * 1.6f),
                problem, smallSize, new Color(1f, 0.7f, 0.7f), TextAnchor.UpperLeft);
            cursor += smallSize * 1.6f;
        }
        else
        {
            for (int i = 0; i < top.Count; i++)
            {
                Goal goal = top[i];
                Color color = i == 0 ? new Color(1f, 0.88f, 0.4f) : Color.white;
                Rect row = new Rect(x + padding, cursor, width - padding * 2f, rowHeight);

                DrawText(row, $"{i + 1}.  {goal.group}", statusFontSize, color, TextAnchor.MiddleLeft);
                DrawText(row, $"<b>{goal.probability * 100f:F0}%</b>", statusFontSize, color, TextAnchor.MiddleRight);
                cursor += rowHeight;

                Rect bar = new Rect(x + padding, cursor, width - padding * 2f, barHeight);
                DrawRect(bar, new Color(1f, 1f, 1f, 0.15f));
                DrawRect(new Rect(bar.x, bar.y, bar.width * Mathf.Clamp01(goal.probability), bar.height),
                    i == 0 ? predictedColor : highProbabilityColor);
                cursor += barHeight + 6f;
            }
        }

        if (detailLines > 0)
        {
            DrawText(new Rect(x + padding, cursor, width - padding * 2f, detailLines * smallSize * 1.4f),
                detail, smallSize, new Color(0.75f, 0.75f, 0.75f), TextAnchor.UpperLeft);
        }
    }

    // Reason why no prediction can be shown (null when everything is fine)
    private string GetStatusProblem()
    {
        if (bodyCues == null)
            return "HomeBodyCues is missing";

        if (!bodyCues.IsConnected)
            return "Not connected to skeleton (MQTT)";

        if (!bodyCues.HasPerson)
            return "No person detected";

        if (fakeFloorNavMesh != null && fakeFloorNavMesh.isActiveAndEnabled && !fakeFloorNavMesh.IsBuilt)
            return "NavMesh not built (see Console)";

        if (!hasStartPoint)
            return "No NavMesh near the person";

        return null;
    }

    // Details (only when Show Status Detail is on): person, cues, NavMesh
    private string GetStatusDetail()
    {
        StringBuilder text = new StringBuilder();

        if (bodyCues != null && bodyCues.HasPerson)
            text.AppendLine($"Person: {bodyCues.MatchedLabel}");

        if (hasStartPoint)
            text.AppendLine(GetCueSummary());

        if (fakeFloorNavMesh != null && fakeFloorNavMesh.isActiveAndEnabled && fakeFloorNavMesh.IsBuilt)
            text.AppendLine($"NavMesh: {fakeFloorNavMesh.FloorCount} floors / {fakeFloorNavMesh.LinkCount} links");

        if (navSubscriber != null)
            text.AppendLine($"NavMesh (MQTT): {(navSubscriber.IsConnected ? "connected" : "disconnected")}");

        if (predictedGoal != null)
            text.Append($"{predictedGoal.group}: {predictedGoal.length:F1} m / {predictedGoal.length / movementSpeed:F0} s{GetCueDetail(predictedGoal)}");

        return text.ToString().TrimEnd();
    }

    private string GetCueSummary()
    {
        if (bodyCues == null || !bodyCues.HasPerson)
            return "Cues: none (route length only)";

        float speed = bodyCues.Velocity.magnitude;
        string decay = decayTemperatureScale > 1.001f ? $" / decay {decayTime:F0} s (x{decayTemperatureScale:F2})" : "";
        return decay.Length == 0 ? CueText(speed) : CueText(speed) + decay;
    }

    private string CueText(float speed)
    {
        return $"Cues: head {(bodyCues.HasHead ? "OK" : "-")} / body {(bodyCues.HasBody ? "OK" : "-")} / " +
               $"move {speed:F2} m/s{(speed >= minMovingSpeed ? "" : " (still)")}";
    }

    private static string GetCueDetail(Goal goal)
    {
        StringBuilder detail = new StringBuilder();

        if (!float.IsNaN(goal.cosHead))
            detail.Append($"  head {Mathf.Acos(Mathf.Clamp(goal.cosHead, -1f, 1f)) * Mathf.Rad2Deg:F0}°");

        if (!float.IsNaN(goal.cosBody))
            detail.Append($"  body {Mathf.Acos(Mathf.Clamp(goal.cosBody, -1f, 1f)) * Mathf.Rad2Deg:F0}°");

        if (!float.IsNaN(goal.cosMove))
            detail.Append($"  move {Mathf.Acos(Mathf.Clamp(goal.cosMove, -1f, 1f)) * Mathf.Rad2Deg:F0}°");

        return detail.ToString();
    }

    // ------------------------------------------------------------
    // Goal labels: fixed pixel size, laid out so they never overlap
    // ------------------------------------------------------------
    private class LabelLayout
    {
        public Goal goal;
        public GUIContent content;
        public Rect rect;
        public Vector2 anchor;
        public bool predicted;
        public bool faint;
        public int fontSize;
    }

    // Labels are placed in priority order (predicted first, then higher probability). A label that would overlap an
    // already placed one is moved up/down step by step; a thin leader line then connects it to its sphere.
    private void DrawGoalLabels()
    {
        if (!showGoalLabels || !hasStartPoint)
            return;

        Camera camera = Camera.main;

        if (camera == null)
            return;

        List<Goal> order = new List<Goal>(goals);
        order.Sort((a, b) => b.probability.CompareTo(a.probability));
        List<LabelLayout> placed = new List<LabelLayout>();

        foreach (Goal goal in order)
        {
            if (!goal.hasPath || goal.marker == null || !goal.marker.activeSelf)
                continue;

            Vector3 world = goal.marker.transform.position + Vector3.up * (goal.marker.transform.localScale.y * 0.5f);
            Vector3 screen = camera.WorldToScreenPoint(world);

            if (screen.z <= 0f)
                continue;

            LabelLayout layout = new LabelLayout
            {
                goal = goal,
                predicted = goal == predictedGoal,
                faint = goal.probability < labelMinProbability && goal != predictedGoal,
                anchor = new Vector2(screen.x, Screen.height - screen.y)
            };

            layout.fontSize = layout.predicted ? predictedLabelFontSize : goalLabelFontSize;
            layout.content = new GUIContent($"{goal.group}  {goal.probability * 100f:F0}%");
            labelStyle.fontSize = layout.fontSize;
            Vector2 size = labelStyle.CalcSize(layout.content);

            // Default position: just above the sphere, centered
            Rect rect = new Rect(layout.anchor.x - size.x * 0.5f, layout.anchor.y - size.y - 6f, size.x, size.y);
            layout.rect = FindFreeRect(rect, placed);
            placed.Add(layout);
        }

        // Draw low priority first so the predicted label is on top of leader lines
        for (int i = placed.Count - 1; i >= 0; i--)
        {
            LabelLayout layout = placed[i];
            Color background = layout.predicted ? new Color(0.45f, 0.05f, 0.05f, 0.88f) : labelBackgroundColor;
            Color textColor = layout.faint ? new Color(0.8f, 0.8f, 0.8f) : (layout.predicted ? new Color(1f, 0.9f, 0.4f) : Color.white);

            float labelBottom = layout.rect.yMax;

            if (Mathf.Abs(labelBottom - (layout.anchor.y - 6f)) > 2f)
            {
                // Leader line from the sphere to the moved label
                float top = Mathf.Min(layout.anchor.y, layout.rect.center.y);
                float bottom = Mathf.Max(layout.anchor.y, layout.rect.center.y);
                DrawRect(new Rect(layout.anchor.x - 1f, top, 2f, bottom - top), new Color(background.r, background.g, background.b, 0.9f));
            }

            labelStyle.fontSize = layout.fontSize;
            labelStyle.normal.textColor = textColor;

            Color previous = GUI.backgroundColor;
            GUI.backgroundColor = background;
            GUI.Label(layout.rect, layout.content, labelStyle);
            GUI.backgroundColor = previous;
        }
    }

    private static Rect FindFreeRect(Rect rect, List<LabelLayout> placed)
    {
        const float gap = 3f;
        float step = rect.height + gap;

        for (int i = 0; i < 24; i++)
        {
            // 0, up 1, down 1, up 2, down 2, ...
            int k = (i + 1) / 2;
            float offset = (i % 2 == 1 ? -1f : 1f) * k * step;
            Rect candidate = new Rect(rect.x, rect.y + offset, rect.width, rect.height);

            if (!Overlaps(candidate, placed, gap))
                return candidate;
        }

        return rect;
    }

    private static bool Overlaps(Rect rect, List<LabelLayout> placed, float gap)
    {
        Rect expanded = new Rect(rect.x - gap, rect.y - gap, rect.width + gap * 2f, rect.height + gap * 2f);

        foreach (LabelLayout other in placed)
        {
            if (expanded.Overlaps(other.rect))
                return true;
        }

        return false;
    }

    private void DrawRect(Rect rect, Color color)
    {
        Color previous = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(rect, whiteTexture);
        GUI.color = previous;
    }

    private void DrawText(Rect rect, string text, int fontSize, Color color, TextAnchor alignment)
    {
        statusStyle.fontSize = fontSize;
        statusStyle.alignment = alignment;
        statusStyle.normal.textColor = color;
        GUI.Label(rect, text, statusStyle);
    }
}
