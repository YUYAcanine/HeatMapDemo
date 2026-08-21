using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using Button = UnityEngine.UI.Button;

public class RouteSub : MonoBehaviour
{
    [Header("Source")]
    [SerializeField] private Transform start;
    [SerializeField] private Subscriber subscriber;
    [SerializeField] private bool autoGenerateWhenMarkerUpdates = true;
    [SerializeField] private Button generateRouteButton;

    [Header("NavMesh Link")]
    [SerializeField] private PointNavLink pointNavLink;
    [SerializeField] private bool generateNavLinksBeforeRoute = true;

    [Header("Route")]
    [SerializeField] private Material routeLineMaterial;
    [SerializeField] private float startSearchRadius = 2.0f;
    [Tooltip("startSearchRadius以内にNavMeshが見つからない場合、この半径まで広げて最寄りの点を探す(スタート地点の真下にNavMeshが無いケースの救済用)。")]
    [SerializeField] private float startFallbackSearchRadius = 1000f;
    [SerializeField] private float goalSearchRadius = 0.8f;
    [SerializeField] private float routeLineWidth = 0.05f;
    [SerializeField] private float routeLineYOffset = 0.05f;
    [SerializeField] private float routeSampleInterval = 0.2f;
    [SerializeField] private bool logRouteSearch = true;

    [Header("Route - Parent")]
    [Tooltip("child(headPelvisDistance最短の人物)以外の全人物(=parent)のスタート位置。空の場合はシーン内から自動収集する。")]
    [SerializeField] private DefineParentStarts parentStarts;
    [Tooltip("parent用の経路ラインに使うマテリアル。childのrouteLineMaterialとは別の色にする。")]
    [SerializeField] private Material parentRouteLineMaterial;

    [Header("Start Marker")]
    [Tooltip("NavMesh上にスナップされたスタート地点に球マーカーを表示する。")]
    [SerializeField] private bool showStartMarker = true;
    [SerializeField] private float startMarkerScale = 0.05f;
    [SerializeField] private Color startMarkerColor = Color.white;

    private readonly Dictionary<string, RouteEntry> routesByLabel =
        new Dictionary<string, RouteEntry>();
    private readonly Dictionary<string, RouteEntry> parentRoutesByKey =
        new Dictionary<string, RouteEntry>();
    private readonly Dictionary<string, GameObject> parentStartMarkersByLabel =
        new Dictionary<string, GameObject>();
    private readonly Dictionary<string, GameObject> goalMarkersByLabel =
        new Dictionary<string, GameObject>();
    private int lastMarkerVersion = -1;
    private GameObject startMarker;
    public int RouteVersion { get; private set; }

    private void OnEnable()
    {
        if (generateRouteButton == null)
            return;

        generateRouteButton.onClick.RemoveListener(GenerateRoute);
        generateRouteButton.onClick.AddListener(GenerateRoute);
    }

    private void OnDisable()
    {
        if (generateRouteButton != null)
            generateRouteButton.onClick.RemoveListener(GenerateRoute);
    }

    private void Update()
    {
        Subscriber currentSubscriber =
            GetSubscriber();

        if (!autoGenerateWhenMarkerUpdates ||
            currentSubscriber == null ||
            !currentSubscriber.HasLatestMarker ||
            currentSubscriber.MarkerVersion == lastMarkerVersion)
        {
            return;
        }

        lastMarkerVersion = currentSubscriber.MarkerVersion;
        GenerateRoute();
    }

    public void GenerateRoute()
    {
        Subscriber currentSubscriber =
            GetSubscriber();

        if (currentSubscriber == null)
        {
            Debug.LogWarning("RouteSub: Subscriber is not assigned and was not found in the scene.");
            ClearRoute();
            return;
        }

        if (currentSubscriber.MarkersByLabel.Count == 0)
        {
            Debug.LogWarning("RouteSub: Subscriber has no generated marker yet.");
            ClearRoute();
            return;
        }

        GenerateNavLinksIfNeeded();

        Vector3 requestedStart =
            start != null
                ? start.position
                : transform.position;
        HashSet<string> activeGoalKeys = new HashSet<string>();

        if (!TrySamplePoint(requestedStart, startSearchRadius, "start", out Vector3 startPoint) &&
            !TrySamplePoint(requestedStart, startFallbackSearchRadius, "start (fallback)", out startPoint))
        {
            SetStartMarkerVisible(false);
            MarkAllMarkersUnreachable(currentSubscriber);
            GenerateParentRoutes(currentSubscriber, activeGoalKeys);
            RemoveMarkersNotIn(goalMarkersByLabel, activeGoalKeys);
            RouteVersion++;
            return;
        }

        UpdateStartMarker(startPoint);

        HashSet<string> activeLabels = new HashSet<string>();
        foreach (KeyValuePair<string, GameObject> markerPair in currentSubscriber.MarkersByLabel)
        {
            string label = markerPair.Key;
            GameObject marker = markerPair.Value;
            if (marker == null)
                continue;

            activeLabels.Add(label);
            GenerateRouteForMarker(label, marker.transform.position, startPoint, activeGoalKeys);
        }

        RemoveRoutesNotIn(activeLabels);
        GenerateParentRoutes(currentSubscriber, activeGoalKeys);
        RemoveMarkersNotIn(goalMarkersByLabel, activeGoalKeys);
        RouteVersion++;
    }

    private void GenerateParentRoutes(Subscriber currentSubscriber, HashSet<string> activeGoalKeys)
    {
        DefineParentStarts starts = GetParentStarts();
        HashSet<string> activeKeys = new HashSet<string>();
        HashSet<string> activeParentStartKeys = new HashSet<string>();

        if (starts != null)
        {
            foreach (KeyValuePair<string, Transform> parentEntry in starts.StartsByLabel)
            {
                string parentLabel = parentEntry.Key;
                Transform parentStartTransform = parentEntry.Value;

                if (parentStartTransform == null)
                    continue;

                if (!TrySamplePoint(parentStartTransform.position, startSearchRadius, $"parent start ({parentLabel})", out Vector3 parentStartPoint) &&
                    !TrySamplePoint(parentStartTransform.position, startFallbackSearchRadius, $"parent start ({parentLabel}, fallback)", out parentStartPoint))
                {
                    continue;
                }

                activeParentStartKeys.Add(parentLabel);
                CreateOrUpdateMarker(parentStartMarkersByLabel, parentLabel, parentStartPoint);

                foreach (KeyValuePair<string, GameObject> markerPair in currentSubscriber.MarkersByLabel)
                {
                    if (markerPair.Value == null)
                        continue;

                    string key = $"{parentLabel}__{markerPair.Key}";
                    activeKeys.Add(key);
                    GenerateParentRouteForMarker(key, parentLabel, markerPair.Key, markerPair.Value.transform.position, parentStartPoint, activeGoalKeys);
                }
            }
        }

        RemoveParentRoutesNotIn(activeKeys);
        RemoveMarkersNotIn(parentStartMarkersByLabel, activeParentStartKeys);
    }

    private void GenerateParentRouteForMarker(string key, string parentLabel, string goalLabel, Vector3 requestedGoal, Vector3 startPoint, HashSet<string> activeGoalKeys)
    {
        RouteEntry route = GetOrCreateParentRoute(key);
        if (!TrySamplePoint(requestedGoal, goalSearchRadius, $"parent goal ({parentLabel}->{goalLabel})", out Vector3 goalPoint))
        {
            HideRoute(route);
            return;
        }

        activeGoalKeys.Add(goalLabel);
        CreateOrUpdateMarker(goalMarkersByLabel, goalLabel, goalPoint);

        bool calculated = NavMesh.CalculatePath(startPoint, goalPoint, NavMesh.AllAreas, route.Path);
        route.HasPath = calculated &&
            route.Path.status == NavMeshPathStatus.PathComplete &&
            route.Path.corners.Length >= 2;

        if (logRouteSearch)
            Debug.Log($"RouteSub: parent path result. parent={parentLabel}, goal={goalLabel}, calculated={calculated}, status={route.Path.status}, complete={route.HasPath}, corners={route.Path.corners.Length}, start={startPoint}, goalPoint={goalPoint}");

        ApplyRouteLine(route, parentRouteLineMaterial);
    }

    private DefineParentStarts GetParentStarts()
    {
        if (parentStarts != null)
            return parentStarts;

        parentStarts = FindObjectOfType<DefineParentStarts>();
        return parentStarts;
    }

    private RouteEntry GetOrCreateParentRoute(string key)
    {
        if (parentRoutesByKey.TryGetValue(key, out RouteEntry existing))
            return existing;

        LineRenderer line = new GameObject($"RouteSubParentLine_{key}").AddComponent<LineRenderer>();
        line.transform.SetParent(transform, false);
        line.positionCount = 0;
        line.enabled = false;
        RouteEntry route = new RouteEntry { Path = new NavMeshPath(), Line = line };
        parentRoutesByKey[key] = route;
        return route;
    }

    private void RemoveParentRoutesNotIn(HashSet<string> activeKeys)
    {
        List<string> removedKeys = new List<string>();
        foreach (KeyValuePair<string, RouteEntry> pair in parentRoutesByKey)
        {
            if (activeKeys.Contains(pair.Key)) continue;
            if (pair.Value.Line != null) Destroy(pair.Value.Line.gameObject);
            removedKeys.Add(pair.Key);
        }
        foreach (string key in removedKeys)
            parentRoutesByKey.Remove(key);
    }

    private void MarkAllMarkersUnreachable(Subscriber currentSubscriber)
    {
        HashSet<string> activeLabels = new HashSet<string>();
        foreach (KeyValuePair<string, GameObject> markerPair in currentSubscriber.MarkersByLabel)
        {
            if (markerPair.Value == null)
                continue;

            activeLabels.Add(markerPair.Key);
            HideRoute(GetOrCreateRoute(markerPair.Key));
        }

        RemoveRoutesNotIn(activeLabels);
    }

    public void ClearRoute()
    {
        foreach (RouteEntry route in routesByLabel.Values)
        {
            if (route.Line != null)
                Destroy(route.Line.gameObject);
        }
        routesByLabel.Clear();

        foreach (RouteEntry route in parentRoutesByKey.Values)
        {
            if (route.Line != null)
                Destroy(route.Line.gameObject);
        }
        parentRoutesByKey.Clear();

        foreach (GameObject marker in parentStartMarkersByLabel.Values)
        {
            if (marker != null)
                Destroy(marker);
        }
        parentStartMarkersByLabel.Clear();

        foreach (GameObject marker in goalMarkersByLabel.Values)
        {
            if (marker != null)
                Destroy(marker);
        }
        goalMarkersByLabel.Clear();

        SetStartMarkerVisible(false);
        RouteVersion++;
    }

    private void UpdateStartMarker(Vector3 navMeshPosition)
    {
        if (!showStartMarker)
        {
            SetStartMarkerVisible(false);
            return;
        }

        if (startMarker == null)
        {
            startMarker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            startMarker.name = "RouteSub_StartMarker";

            Renderer markerRenderer = startMarker.GetComponent<Renderer>();
            if (markerRenderer != null)
                markerRenderer.material.color = startMarkerColor;
        }

        startMarker.transform.SetParent(null, true);
        startMarker.transform.position = navMeshPosition;
        startMarker.transform.localScale = Vector3.one * Mathf.Max(startMarkerScale, 0.01f);
        startMarker.SetActive(true);
    }

    private void SetStartMarkerVisible(bool visible)
    {
        if (startMarker != null)
            startMarker.SetActive(visible);
    }

    // parentのスタート地点/ゴール地点用の小さい白球マーカー。childのスタートマーカーと同じ見た目。
    private void CreateOrUpdateMarker(Dictionary<string, GameObject> markers, string key, Vector3 position)
    {
        if (!showStartMarker)
        {
            if (markers.TryGetValue(key, out GameObject existing) && existing != null)
                existing.SetActive(false);
            return;
        }

        if (!markers.TryGetValue(key, out GameObject marker) || marker == null)
        {
            marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = $"RouteSub_Marker_{key}";

            Renderer markerRenderer = marker.GetComponent<Renderer>();
            if (markerRenderer != null)
                markerRenderer.material.color = startMarkerColor;

            markers[key] = marker;
        }

        marker.transform.SetParent(null, true);
        marker.transform.position = position;
        marker.transform.localScale = Vector3.one * Mathf.Max(startMarkerScale, 0.01f);
        marker.SetActive(true);
    }

    private void RemoveMarkersNotIn(Dictionary<string, GameObject> markers, HashSet<string> activeKeys)
    {
        List<string> staleKeys = null;
        foreach (KeyValuePair<string, GameObject> pair in markers)
        {
            if (activeKeys.Contains(pair.Key)) continue;
            staleKeys ??= new List<string>();
            staleKeys.Add(pair.Key);
        }

        if (staleKeys == null) return;

        foreach (string key in staleKeys)
        {
            if (markers[key] != null) Destroy(markers[key]);
            markers.Remove(key);
        }
    }

    public void GetRouteLengths(Dictionary<string, float> destination)
    {
        if (destination == null)
            return;

        destination.Clear();
        foreach (KeyValuePair<string, RouteEntry> pair in routesByLabel)
        {
            RouteEntry route = pair.Value;
            if (!route.HasPath || route.Path == null || route.Path.corners.Length < 2)
            {
                destination[pair.Key] = -1f;
                continue;
            }

            float length = 0f;
            for (int i = 0; i < route.Path.corners.Length - 1; i++)
                length += Vector3.Distance(route.Path.corners[i], route.Path.corners[i + 1]);

            destination[pair.Key] = length;
        }
    }

    // parentRoutesByKeyのキーは "parentLabel__goalLabel" 形式。
    public void GetParentRouteLengths(Dictionary<string, float> destination)
    {
        if (destination == null)
            return;

        destination.Clear();
        foreach (KeyValuePair<string, RouteEntry> pair in parentRoutesByKey)
        {
            RouteEntry route = pair.Value;
            if (!route.HasPath || route.Path == null || route.Path.corners.Length < 2)
            {
                destination[pair.Key] = -1f;
                continue;
            }

            float length = 0f;
            for (int i = 0; i < route.Path.corners.Length - 1; i++)
                length += Vector3.Distance(route.Path.corners[i], route.Path.corners[i + 1]);

            destination[pair.Key] = length;
        }
    }

    private void GenerateRouteForMarker(string label, Vector3 requestedGoal, Vector3 startPoint, HashSet<string> activeGoalKeys)
    {
        RouteEntry route = GetOrCreateRoute(label);
        if (!TrySamplePoint(requestedGoal, goalSearchRadius, $"goal ({label})", out Vector3 goalPoint))
        {
            HideRoute(route);
            return;
        }

        activeGoalKeys.Add(label);
        CreateOrUpdateMarker(goalMarkersByLabel, label, goalPoint);

        bool calculated = NavMesh.CalculatePath(startPoint, goalPoint, NavMesh.AllAreas, route.Path);
        route.HasPath = calculated &&
            route.Path.status == NavMeshPathStatus.PathComplete &&
            route.Path.corners.Length >= 2;

        if (logRouteSearch)
            Debug.Log($"RouteSub: path result. label={label}, calculated={calculated}, status={route.Path.status}, complete={route.HasPath}, corners={route.Path.corners.Length}, start={startPoint}, goal={goalPoint}");

        if (!route.HasPath)
            Debug.LogWarning($"RouteSub: route not generated for {label}. status={route.Path.status}, corners={route.Path.corners.Length}.");

        ApplyRouteLine(route, routeLineMaterial);
    }

    private Subscriber GetSubscriber()
    {
        if (subscriber != null)
            return subscriber;

        subscriber = FindObjectOfType<Subscriber>();
        return subscriber;
    }

    private void GenerateNavLinksIfNeeded()
    {
        if (!generateNavLinksBeforeRoute)
            return;

        PointNavLink generator =
            pointNavLink;

        if (generator == null)
        {
            generator = FindObjectOfType<PointNavLink>();

            if (generator != null)
                pointNavLink = generator;
        }

        if (generator == null)
        {
            if (logRouteSearch)
                Debug.LogWarning("RouteSub: PointNavLink is not assigned and was not found.");

            return;
        }

        generator.GenerateLinks();
    }

    private bool TrySamplePoint(
        Vector3 source,
        float radius,
        string label,
        out Vector3 sampledPoint)
    {
        if (NavMesh.SamplePosition(
            source,
            out NavMeshHit hit,
            Mathf.Max(radius, 0.01f),
            NavMesh.AllAreas))
        {
            sampledPoint = hit.position;

            if (logRouteSearch)
            {
                Debug.Log(
                    $"RouteSub: {label} sampled on NavMesh. requested={source}, sampled={sampledPoint}, distance={Vector3.Distance(source, sampledPoint):F3}");
            }

            return true;
        }

        sampledPoint = source;
        Debug.LogWarning($"RouteSub: {label} is not near NavMesh. position={source}, radius={radius:F2}");
        return false;
    }

    private void ApplyRouteLine(RouteEntry route, Material material)
    {
        if (!route.HasPath || route.Line == null)
        {
            HideRoute(route);
            return;
        }

        List<Vector3> points =
            BuildDisplayRoutePoints(route.Path);

        if (points.Count < 2)
        {
            HideRoute(route);
            return;
        }

        route.Line.material = material;
        route.Line.startWidth = routeLineWidth;
        route.Line.endWidth = routeLineWidth;
        route.Line.positionCount = points.Count;
        route.Line.SetPositions(points.ToArray());
        route.Line.enabled = true;
    }

    private List<Vector3> BuildDisplayRoutePoints(NavMeshPath path)
    {
        List<Vector3> points =
            new List<Vector3>();
        Vector3[] corners =
            path.corners;

        for (int i = 0; i < corners.Length - 1; i++)
        {
            Vector3 from =
                corners[i];
            Vector3 to =
                corners[i + 1];
            float distance =
                Vector3.Distance(from, to);
            int sampleCount =
                Mathf.Max(1, Mathf.CeilToInt(distance / Mathf.Max(routeSampleInterval, 0.01f)));

            for (int j = 0; j <= sampleCount; j++)
            {
                if (i > 0 &&
                    j == 0)
                {
                    continue;
                }

                Vector3 sample =
                    Vector3.Lerp(from, to, (float)j / sampleCount);
                points.Add(sample + Vector3.up * routeLineYOffset);
            }
        }

        return points;
    }

    private RouteEntry GetOrCreateRoute(string label)
    {
        if (routesByLabel.TryGetValue(label, out RouteEntry existing))
            return existing;

        LineRenderer line = new GameObject($"RouteSubLine_{label}").AddComponent<LineRenderer>();
        line.transform.SetParent(transform, false);
        line.positionCount = 0;
        line.enabled = false;
        RouteEntry route = new RouteEntry { Path = new NavMeshPath(), Line = line };
        routesByLabel[label] = route;
        return route;
    }

    private void HideRoute(RouteEntry route)
    {
        route.HasPath = false;
        if (route.Line == null) return;
        route.Line.positionCount = 0;
        route.Line.enabled = false;
    }

    private void RemoveRoutesNotIn(HashSet<string> activeLabels)
    {
        List<string> removedLabels = new List<string>();
        foreach (KeyValuePair<string, RouteEntry> pair in routesByLabel)
        {
            if (activeLabels.Contains(pair.Key)) continue;
            if (pair.Value.Line != null) Destroy(pair.Value.Line.gameObject);
            removedLabels.Add(pair.Key);
        }
        foreach (string label in removedLabels)
            routesByLabel.Remove(label);
    }

    private void OnDestroy()
    {
        if (startMarker != null)
            Destroy(startMarker);

        foreach (GameObject marker in parentStartMarkersByLabel.Values)
        {
            if (marker != null)
                Destroy(marker);
        }

        foreach (GameObject marker in goalMarkersByLabel.Values)
        {
            if (marker != null)
                Destroy(marker);
        }
    }

    private void OnDrawGizmos()
    {
        if (routesByLabel == null)
        {
            return;
        }

        Gizmos.color = Color.cyan;
        foreach (RouteEntry route in routesByLabel.Values)
        {
            if (!route.HasPath || route.Path == null) continue;
            for (int i = 0; i < route.Path.corners.Length - 1; i++)
                Gizmos.DrawLine(route.Path.corners[i] + Vector3.up * routeLineYOffset,
                    route.Path.corners[i + 1] + Vector3.up * routeLineYOffset);
        }

        Gizmos.color = Color.blue;
        foreach (RouteEntry route in parentRoutesByKey.Values)
        {
            if (!route.HasPath || route.Path == null) continue;
            for (int i = 0; i < route.Path.corners.Length - 1; i++)
                Gizmos.DrawLine(route.Path.corners[i] + Vector3.up * routeLineYOffset,
                    route.Path.corners[i + 1] + Vector3.up * routeLineYOffset);
        }
    }

    private class RouteEntry
    {
        public NavMeshPath Path;
        public LineRenderer Line;
        public bool HasPath;
    }
}
