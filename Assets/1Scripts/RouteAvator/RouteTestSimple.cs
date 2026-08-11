using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.EventSystems;
using Button = UnityEngine.UI.Button;

public class RouteTestSimple : MonoBehaviour
{
    [Header("Points")]
    [SerializeField] private Transform start;
    [SerializeField] private Transform goal;

    [Header("UI")]
    [SerializeField] private Button generateRouteButton;

    [Header("Click Selection")]
    [SerializeField] private bool enableClickPointSelection = true;
    [SerializeField] private Camera routeCamera;
    [SerializeField] private float clickNavMeshSearchRadius = 1.0f;
    [SerializeField] private bool autoGenerateRouteAfterGoalClick = true;

    [Header("NavMesh Link")]
    [SerializeField] private PointNavLink pointNavLink;
    [SerializeField] private bool generateNavLinksBeforeRoute = true;

    [Header("Route")]
    [SerializeField] private Material routeLineMaterial;
    [SerializeField] private float startSearchRadius = 2.0f;
    [SerializeField] private float goalSearchRadius = 0.5f;
    [SerializeField] private float routeLineWidth = 0.05f;
    [SerializeField] private float routeLineYOffset = 0.05f;
    [SerializeField] private float routeSampleInterval = 0.2f;
    [SerializeField] private bool rejectGoalSampleTooFar = true;
    [SerializeField] private float maxGoalSampleVerticalOffset = 0.3f;
    [SerializeField] private float maxGoalSampleHorizontalOffset = 0.6f;
    [SerializeField] private bool logRouteSearch = true;
    [SerializeField] private bool logRouteCorners = true;
    [SerializeField] private bool logDisplayPoints = false;

    private NavMeshPath path;
    private LineRenderer routeLine;
    private bool hasPath;
    private bool nextClickSetsStart = true;

    private void Awake()
    {
        path = new NavMeshPath();
        CreateRouteLine();
    }

    private void OnEnable()
    {
        if (generateRouteButton == null)
        {
            if (logRouteSearch)
                Debug.LogWarning("RouteTestSimple: generateRouteButton is not assigned. Call GenerateRoute manually or assign a UI Button.");

            return;
        }

        generateRouteButton.onClick.RemoveListener(GenerateRoute);
        generateRouteButton.onClick.AddListener(GenerateRoute);

        if (logRouteSearch)
            Debug.Log($"RouteTestSimple: registered button listener. button={generateRouteButton.name}");
    }

    private void OnDisable()
    {
        if (generateRouteButton != null)
            generateRouteButton.onClick.RemoveListener(GenerateRoute);
    }

    private void Update()
    {
        if (!enableClickPointSelection ||
            !Input.GetMouseButtonDown(0))
        {
            return;
        }

        if (EventSystem.current != null &&
            EventSystem.current.IsPointerOverGameObject())
        {
            if (logRouteSearch)
                Debug.Log("RouteTestSimple: click ignored because pointer is over UI.");

            return;
        }

        SelectRoutePointFromClick(Input.mousePosition);
    }

    public void GenerateRoute()
    {
        if (logRouteSearch)
        {
            Debug.Log(
                $"RouteTestSimple: GenerateRoute called. start={(start == null ? "null" : start.name)}, goal={(goal == null ? "null" : goal.name)}, routeLine={(routeLine == null ? "null" : routeLine.name)}, startRadius={startSearchRadius:F2}, goalRadius={goalSearchRadius:F2}");
        }

        if (start == null ||
            goal == null)
        {
            Debug.LogWarning("RouteTestSimple: start or goal is not assigned.");
            ClearRoute();
            return;
        }

        GenerateNavLinksIfNeeded();

        if (!TrySamplePoint(start.position, startSearchRadius, "start", out Vector3 startPoint) ||
            !TrySamplePoint(goal.position, goalSearchRadius, "goal", out Vector3 goalPoint))
        {
            ClearRoute();
            return;
        }

        if (rejectGoalSampleTooFar &&
            IsGoalSampleTooFar(goal.position, goalPoint))
        {
            ClearRoute();
            return;
        }

        bool calculated =
            NavMesh.CalculatePath(
                startPoint,
                goalPoint,
                NavMesh.AllAreas,
                path);

        hasPath =
            calculated &&
            path.status == NavMeshPathStatus.PathComplete &&
            path.corners.Length >= 2;

        if (logRouteSearch)
        {
            float startGoalDistance =
                Vector3.Distance(startPoint, goalPoint);
            Vector3 lastCorner =
                path.corners.Length > 0
                    ? path.corners[path.corners.Length - 1]
                    : Vector3.zero;
            float lastToGoalDistance =
                path.corners.Length > 0
                    ? Vector3.Distance(lastCorner, goalPoint)
                    : -1f;

            Debug.Log(
                $"RouteTestSimple: path result. calculated={calculated}, status={path.status}, complete={hasPath}, corners={path.corners.Length}, start={startPoint}, goal={goalPoint}, startGoalDistance={startGoalDistance:F3}, lastCorner={lastCorner}, lastToGoal={lastToGoalDistance:F3}");
        }

        if (logRouteCorners)
            LogPathCorners();

        if (!hasPath)
        {
            Debug.LogWarning(
                $"RouteTestSimple: route not generated. calculated={calculated}, status={path.status}, corners={path.corners.Length}. Check whether Start/Goal are on the same connected NavMesh island or NavMeshLink is generated.");
        }

        ApplyRouteLine();
    }

    public void ResetClickPointSelection()
    {
        nextClickSetsStart = true;
        ClearRoute();

        if (logRouteSearch)
            Debug.Log("RouteTestSimple: click point selection reset. Next click sets start.");
    }

    private void SelectRoutePointFromClick(
        Vector3 screenPosition)
    {
        Camera cameraToUse =
            routeCamera != null
                ? routeCamera
                : Camera.main;

        if (cameraToUse == null)
        {
            Debug.LogWarning("RouteTestSimple: routeCamera is not assigned and Camera.main was not found.");
            return;
        }

        Ray ray =
            cameraToUse.ScreenPointToRay(screenPosition);

        if (!TryGetClickedNavMeshPoint(ray, out Vector3 navMeshPoint))
        {
            Debug.LogWarning(
                $"RouteTestSimple: clicked point is not on or near NavMesh. screen={screenPosition}, searchRadius={clickNavMeshSearchRadius:F2}");
            return;
        }

        if (nextClickSetsStart)
        {
            start =
                EnsurePointTransform(start, "RouteStartPoint", Color.green);
            start.position = navMeshPoint;
            nextClickSetsStart = false;
            ClearRoute();

            if (logRouteSearch)
                Debug.Log($"RouteTestSimple: start selected by click. position={navMeshPoint}. Next click sets goal.");
        }
        else
        {
            goal =
                EnsurePointTransform(goal, "RouteGoalPoint", Color.red);
            goal.position = navMeshPoint;
            nextClickSetsStart = true;

            if (logRouteSearch)
                Debug.Log($"RouteTestSimple: goal selected by click. position={navMeshPoint}. Next click sets start.");

            if (autoGenerateRouteAfterGoalClick)
                GenerateRoute();
        }
    }

    private bool TryGetClickedNavMeshPoint(
        Ray ray,
        out Vector3 navMeshPoint)
    {
        if (Physics.Raycast(ray, out RaycastHit hit, 1000f) &&
            NavMesh.SamplePosition(
                hit.point,
                out NavMeshHit navMeshHit,
                Mathf.Max(clickNavMeshSearchRadius, 0.01f),
                NavMesh.AllAreas))
        {
            navMeshPoint = navMeshHit.position;

            if (logRouteSearch)
            {
                Debug.Log(
                    $"RouteTestSimple: click sampled from physics hit. hit={hit.point}, navmesh={navMeshPoint}, distance={Vector3.Distance(hit.point, navMeshPoint):F3}");
            }

            return true;
        }

        return TryRaycastNavMeshTriangulation(ray, out navMeshPoint);
    }

    private bool TryRaycastNavMeshTriangulation(
        Ray ray,
        out Vector3 navMeshPoint)
    {
        NavMeshTriangulation triangulation =
            NavMesh.CalculateTriangulation();
        Vector3[] vertices =
            triangulation.vertices;
        int[] indices =
            triangulation.indices;
        float bestDistance =
            float.PositiveInfinity;
        Vector3 bestPoint =
            Vector3.zero;

        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            if (!TryRayTriangle(
                ray,
                vertices[indices[i]],
                vertices[indices[i + 1]],
                vertices[indices[i + 2]],
                out float distance))
            {
                continue;
            }

            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestPoint = ray.GetPoint(distance);
            }
        }

        if (float.IsPositiveInfinity(bestDistance))
        {
            navMeshPoint = Vector3.zero;
            return false;
        }

        if (NavMesh.SamplePosition(
            bestPoint,
            out NavMeshHit hit,
            Mathf.Max(clickNavMeshSearchRadius, 0.01f),
            NavMesh.AllAreas))
        {
            navMeshPoint = hit.position;

            if (logRouteSearch)
            {
                Debug.Log(
                    $"RouteTestSimple: click sampled from NavMesh triangles. rayPoint={bestPoint}, navmesh={navMeshPoint}, distance={Vector3.Distance(bestPoint, navMeshPoint):F3}");
            }

            return true;
        }

        navMeshPoint = Vector3.zero;
        return false;
    }

    private bool TryRayTriangle(
        Ray ray,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        out float distance)
    {
        const float epsilon = 0.000001f;
        distance = 0f;
        Vector3 edge1 =
            b - a;
        Vector3 edge2 =
            c - a;
        Vector3 h =
            Vector3.Cross(ray.direction, edge2);
        float determinant =
            Vector3.Dot(edge1, h);

        if (determinant > -epsilon &&
            determinant < epsilon)
        {
            return false;
        }

        float inverseDeterminant =
            1f / determinant;
        Vector3 s =
            ray.origin - a;
        float u =
            inverseDeterminant * Vector3.Dot(s, h);

        if (u < 0f ||
            u > 1f)
        {
            return false;
        }

        Vector3 q =
            Vector3.Cross(s, edge1);
        float v =
            inverseDeterminant * Vector3.Dot(ray.direction, q);

        if (v < 0f ||
            u + v > 1f)
        {
            return false;
        }

        distance =
            inverseDeterminant * Vector3.Dot(edge2, q);
        return distance > epsilon;
    }

    private Transform EnsurePointTransform(
        Transform current,
        string objectName,
        Color color)
    {
        if (current != null)
            return current;

        GameObject marker =
            GameObject.CreatePrimitive(PrimitiveType.Sphere);
        marker.name = objectName;
        marker.transform.SetParent(transform, false);
        marker.transform.localScale = Vector3.one * 0.12f;

        Collider markerCollider =
            marker.GetComponent<Collider>();

        if (markerCollider != null)
            Destroy(markerCollider);

        Renderer renderer =
            marker.GetComponent<Renderer>();

        if (renderer != null)
            renderer.material.color = color;

        return marker.transform;
    }

    private void GenerateNavLinksIfNeeded()
    {
        if (!generateNavLinksBeforeRoute)
        {
            if (logRouteSearch)
                Debug.Log("RouteTestSimple: nav link generation skipped by setting.");

            return;
        }

        PointNavLink generator =
            pointNavLink;

        if (generator == null)
        {
            generator =
                FindObjectOfType<PointNavLink>();

            if (generator != null)
                pointNavLink = generator;
        }

        if (generator == null)
        {
            Debug.LogWarning("RouteTestSimple: PointNavLink is not assigned and was not found in the scene. Route will be calculated without generated NavMeshLinks.");
            return;
        }

        if (logRouteSearch)
            Debug.Log($"RouteTestSimple: generating NavMeshLinks before route. generator={generator.name}");

        generator.GenerateLinks();

        if (logRouteSearch)
        {
            Debug.Log(
                $"RouteTestSimple: NavMeshLink generation completed. generator={generator.name}, sourceCells={generator.LastSourceCellCount}, clusters={generator.LastClusterCount}, generatedLinks={generator.LastGeneratedLinkCount}");
        }
    }

    public void ClearRoute()
    {
        if (logRouteSearch)
            Debug.Log("RouteTestSimple: ClearRoute called.");

        hasPath = false;

        if (path != null)
            path.ClearCorners();

        if (routeLine == null)
        {
            if (logRouteSearch)
                Debug.LogWarning("RouteTestSimple: routeLine is null while clearing.");

            return;
        }

        routeLine.positionCount = 0;
        routeLine.enabled = false;
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
                    $"RouteTestSimple: {label} sampled on NavMesh. requested={source}, sampled={sampledPoint}, distance={Vector3.Distance(source, sampledPoint):F3}, mask={hit.mask}");
            }

            return true;
        }

        sampledPoint = source;
        Debug.LogWarning(
            $"RouteTestSimple: {label} is not near NavMesh. position={source}, searchRadius={radius:F2}");
        return false;
    }

    private bool IsGoalSampleTooFar(
        Vector3 requestedGoal,
        Vector3 sampledGoal)
    {
        float verticalOffset =
            Mathf.Abs(requestedGoal.y - sampledGoal.y);
        float horizontalOffset =
            HorizontalDistance(requestedGoal, sampledGoal);

        if (logRouteSearch)
        {
            Debug.Log(
                $"RouteTestSimple: goal sample. requested={requestedGoal}, sampled={sampledGoal}, verticalOffset={verticalOffset:F3}, horizontalOffset={horizontalOffset:F3}");
        }

        bool tooFar =
            verticalOffset > maxGoalSampleVerticalOffset ||
            horizontalOffset > maxGoalSampleHorizontalOffset;

        if (tooFar)
        {
            Debug.LogWarning(
                $"RouteTestSimple: sampled goal is too far. vertical={verticalOffset:F3}/{maxGoalSampleVerticalOffset:F3}, horizontal={horizontalOffset:F3}/{maxGoalSampleHorizontalOffset:F3}");
        }

        return tooFar;
    }

    private void ApplyRouteLine()
    {
        if (!hasPath ||
            routeLine == null)
        {
            if (logRouteSearch)
            {
                Debug.LogWarning(
                    $"RouteTestSimple: ApplyRouteLine skipped. hasPath={hasPath}, routeLine={(routeLine == null ? "null" : routeLine.name)}");
            }

            ClearRoute();
            return;
        }

        List<Vector3> points =
            BuildDisplayRoutePoints();

        if (points.Count < 2)
        {
            Debug.LogWarning(
                $"RouteTestSimple: display route has too few points. displayPoints={points.Count}, corners={path.corners.Length}");
            ClearRoute();
            return;
        }

        routeLine.material = routeLineMaterial;
        routeLine.startWidth = routeLineWidth;
        routeLine.endWidth = routeLineWidth;
        routeLine.positionCount = points.Count;
        routeLine.SetPositions(points.ToArray());
        routeLine.enabled = true;

        if (logRouteSearch)
        {
            Debug.Log(
                $"RouteTestSimple: route line applied. displayPoints={points.Count}, width={routeLineWidth:F3}, yOffset={routeLineYOffset:F3}, material={(routeLineMaterial == null ? "null" : routeLineMaterial.name)}, lineEnabled={routeLine.enabled}");
        }
    }

    private List<Vector3> BuildDisplayRoutePoints()
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
                Mathf.Max(
                    1,
                    Mathf.CeilToInt(distance / Mathf.Max(routeSampleInterval, 0.01f)));

            for (int j = 0; j <= sampleCount; j++)
            {
                if (i > 0 &&
                    j == 0)
                {
                    continue;
                }

                Vector3 sample =
                    Vector3.Lerp(from, to, (float)j / sampleCount);

                Vector3 point =
                    sample + Vector3.up * routeLineYOffset;
                points.Add(point);

                if (logDisplayPoints)
                {
                    Debug.Log(
                        $"RouteTestSimple: display point from path. segment={i}, sample={j}/{sampleCount}, pathPoint={sample}, displayPoint={point}");
                }
            }
        }

        return points;
    }

    private void CreateRouteLine()
    {
        routeLine =
            new GameObject("RouteTestSimpleLine").AddComponent<LineRenderer>();
        routeLine.transform.SetParent(transform, false);
        routeLine.material = routeLineMaterial;
        routeLine.startWidth = routeLineWidth;
        routeLine.endWidth = routeLineWidth;
        routeLine.positionCount = 0;
        routeLine.enabled = false;

        if (logRouteSearch)
        {
            Debug.Log(
                $"RouteTestSimple: route line created. object={routeLine.name}, material={(routeLineMaterial == null ? "null" : routeLineMaterial.name)}, width={routeLineWidth:F3}");
        }
    }

    private void LogPathCorners()
    {
        if (path == null)
        {
            Debug.LogWarning("RouteTestSimple: path is null.");
            return;
        }

        if (path.corners == null ||
            path.corners.Length == 0)
        {
            Debug.LogWarning("RouteTestSimple: path has no corners.");
            return;
        }

        for (int i = 0; i < path.corners.Length; i++)
        {
            float previousDistance =
                i > 0
                    ? Vector3.Distance(path.corners[i - 1], path.corners[i])
                    : 0f;

            Debug.Log(
                $"RouteTestSimple: corner[{i}]={path.corners[i]}, prevDistance={previousDistance:F3}");
        }
    }

    private float HorizontalDistance(
        Vector3 a,
        Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b);
    }

    private void OnDrawGizmos()
    {
        if (!hasPath ||
            path == null ||
            path.corners.Length < 2)
        {
            return;
        }

        Gizmos.color = Color.cyan;

        for (int i = 0; i < path.corners.Length - 1; i++)
        {
            Gizmos.DrawLine(
                path.corners[i] + Vector3.up * routeLineYOffset,
                path.corners[i + 1] + Vector3.up * routeLineYOffset);
        }
    }
}
