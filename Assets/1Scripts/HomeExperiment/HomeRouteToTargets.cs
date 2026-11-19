using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.AI;

// シーン8(8RouteToTargets)用。人の位置から、シーン0(0HomeSetup)で置いた対象物体(Env/TargetObjects.json)までの経路を
// NavMesh 上で計算して表示する。1PhilipDemo/MultiIntegrationLogger の RouteSub と同じ考え方で、ゴールを
// MQTT の物体マーカーではなく、同じGameObjectの HomeEnvLoader が再生成した対象物体にしたもの。
//   - 骨格: SkeletonRealtimeSubscriber が MQTT(pathfinder/person_coordinate, シーン6が送る)で受信する
//   - スタート: DefineStart(Start オブジェクト)がハイライト中の人物の Pelvis を床に下ろした位置
//   - NavMesh: NavSub が MQTT(pathfinder/navmesh, シーン7が送る)で受信して再現する
//   - ゴール: 対象物体の group ごとに1つ。group に属する物体全体のバウンディングボックスの中心から一番近い NavMesh 上の点
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
    [Tooltip("この秒数ごとに経路を計算し直す。0 で毎フレーム。")]
    [SerializeField, Min(0f)] private float routeUpdateInterval = 0.5f;
    [SerializeField] private Material routeLineMaterial;
    [Tooltip("一番近い(経路が一番短い)対象物体の経路に使うマテリアル。空なら Route Line Material。")]
    [SerializeField] private Material nearestRouteLineMaterial;
    [SerializeField] private float routeLineWidth = 0.05f;
    [SerializeField] private float nearestRouteLineWidth = 0.08f;
    [SerializeField] private float routeLineYOffset = 0.05f;
    [Tooltip("経路の長さから到着までの時間を出すときの移動速度(m/s)。")]
    [SerializeField, Min(0.01f)] private float movementSpeed = 0.5f;

    [Header("Markers")]
    [SerializeField] private bool showMarkers = true;
    [SerializeField] private float markerScale = 0.08f;
    [SerializeField] private Color startMarkerColor = Color.white;
    [SerializeField] private Color goalMarkerColor = new Color(1f, 0.85f, 0.1f);
    [SerializeField] private bool showGoalLabels = true;
    [SerializeField] private float goalLabelCharacterSize = 0.05f;

    [Header("Status")]
    [SerializeField] private bool showStatus = true;
    [SerializeField] private bool logRouteSearch = false;

    private HomeEnvLoader env;
    private SkeletonRealtimeSubscriber skeletonSubscriber;
    private NavSub navSubscriber;
    private readonly List<Goal> goals = new List<Goal>();
    private GameObject startMarker;
    private float lastRouteTime = float.NegativeInfinity;
    private bool hasStartPoint;
    private Goal nearestGoal;
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

    public int RouteVersion { get; private set; }

    private class Goal
    {
        public string group;
        public Vector3 center;
        public Vector3 navPoint;
        public bool onNavMesh;
        public bool hasPath;
        public float length;
        public NavMeshPath path = new NavMeshPath();
        public LineRenderer line;
        public GameObject marker;
    }

    private void Awake()
    {
        env = GetComponent<HomeEnvLoader>();
    }

    private void Start()
    {
        if (start == null)
            start = FindObjectOfType<DefineStart>();

        skeletonSubscriber = FindObjectOfType<SkeletonRealtimeSubscriber>();
        navSubscriber = FindObjectOfType<NavSub>();

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
        if (Time.unscaledTime - lastRouteTime < routeUpdateInterval)
            return;

        lastRouteTime = Time.unscaledTime;
        UpdateRoutes();
    }

    public void UpdateRoutes()
    {
        RouteVersion++;
        nearestGoal = null;
        hasStartPoint = false;

        if (start == null || !start.HasActivePerson || !TryFindStartPoint(start.transform.position, out Vector3 startPoint))
        {
            HideAll();
            return;
        }

        hasStartPoint = true;
        UpdateMarker(ref startMarker, "RouteStart", startPoint, startMarkerColor, null);

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

                if (nearestGoal == null || goal.length < nearestGoal.length)
                    nearestGoal = goal;
            }

            if (logRouteSearch)
                Debug.Log($"[HomeRouteToTargets] {goal.group}: onNavMesh={goal.onNavMesh}, status={goal.path.status}, length={(goal.hasPath ? goal.length : -1f):F2}, start={startPoint}, goal={goal.navPoint}");

            if (goal.onNavMesh)
                UpdateMarker(ref goal.marker, $"RouteGoal_{goal.group}", goal.navPoint, goalMarkerColor, goal.group);
            else if (goal.marker != null)
                goal.marker.SetActive(false);
        }

        foreach (Goal goal in goals)
            ApplyLine(goal, goal == nearestGoal);
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

    private void ApplyLine(Goal goal, bool nearest)
    {
        if (!goal.hasPath)
        {
            goal.line.positionCount = 0;
            goal.line.enabled = false;
            return;
        }

        Vector3[] corners = goal.path.corners;
        Vector3[] points = new Vector3[corners.Length];

        for (int i = 0; i < corners.Length; i++)
            points[i] = corners[i] + Vector3.up * routeLineYOffset;

        Material material = nearest && nearestRouteLineMaterial != null ? nearestRouteLineMaterial : routeLineMaterial;

        if (material != null)
            goal.line.sharedMaterial = material;

        float width = nearest ? nearestRouteLineWidth : routeLineWidth;
        goal.line.startWidth = width;
        goal.line.endWidth = width;
        goal.line.positionCount = points.Length;
        goal.line.SetPositions(points);
        goal.line.enabled = true;
    }

    private void UpdateMarker(ref GameObject marker, string name, Vector3 position, Color color, string label)
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
            marker.GetComponent<Renderer>().material.color = color;

            if (showGoalLabels && !string.IsNullOrEmpty(label))
            {
                GameObject text = new GameObject("Label");
                text.transform.SetParent(marker.transform, false);
                TextMesh mesh = text.AddComponent<TextMesh>();
                mesh.text = label;
                mesh.anchor = TextAnchor.LowerCenter;
                mesh.characterSize = goalLabelCharacterSize;
                mesh.fontSize = 48;
                mesh.color = color;
                text.AddComponent<HomeRouteLabelBillboard>();
            }
        }

        marker.transform.position = position;
        marker.transform.localScale = Vector3.one * Mathf.Max(markerScale, 0.01f);

        // ラベルはマーカーの拡大率に関係なく同じ大きさ・マーカーの少し上に出す
        if (marker.transform.childCount > 0)
        {
            Transform text = marker.transform.GetChild(0);
            text.localScale = Vector3.one / Mathf.Max(markerScale, 0.01f);
            text.localPosition = Vector3.up * 0.6f;
        }

        marker.SetActive(true);
    }

    private void HideAll()
    {
        if (startMarker != null)
            startMarker.SetActive(false);

        foreach (Goal goal in goals)
        {
            goal.hasPath = false;
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
        if (!showStatus)
            return;

        if (statusStyle == null)
            statusStyle = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 16, wordWrap = true };

        StringBuilder text = new StringBuilder();
        text.AppendLine($"実験: {env.ExperimentName}   人の位置 → 対象物体 の経路");

        if (skeletonSubscriber != null)
            text.AppendLine($"骨格(MQTT): {(skeletonSubscriber.IsConnected ? "接続中" : "未接続")}");

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
            foreach (Goal goal in goals)
            {
                string state = goal.hasPath
                    ? $"{goal.length:F2} m / {goal.length / Mathf.Max(movementSpeed, 0.01f):F1} 秒"
                    : goal.onNavMesh ? "届かない" : "近くに NavMesh がない";
                text.AppendLine($"{(goal == nearestGoal ? "★" : "　")}{goal.group}: {state}");
            }
        }

        GUIContent content = new GUIContent(text.ToString().TrimEnd());
        const float width = 560f;

        GUI.Box(new Rect(10, 10, width, statusStyle.CalcHeight(content, width)), content, statusStyle);
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
