using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

public class RouteTest : MonoBehaviour
{
    [Header("Points")]
    [SerializeField] private Transform start;
    [SerializeField] private Transform goal;

    [Header("Route")]
    [SerializeField] private Material routeLineMaterial;
    [SerializeField] private float startSearchRadius = 2.0f;
    [SerializeField] private float goalSearchRadius = 0.3f;
    [SerializeField] private float routeLineWidth = 0.05f;
    [SerializeField] private float routeLineYOffset = 0.05f;
    [SerializeField] private float routeHeightSampleInterval = 0.2f;
    [SerializeField] private float routeHeightSampleRadius = 1.0f;
    [SerializeField] private bool logRouteSearch = true;

    private NavMeshPath path;
    private LineRenderer routeLine;
    private bool hasPath = false;

    private void Start()
    {
        path = new NavMeshPath();
        CreateRouteLine();
        GenerateRoute();
    }

    private void CreateRouteLine()
    {
        routeLine = new GameObject("RouteTestLine").AddComponent<LineRenderer>();
        routeLine.material = routeLineMaterial;
        routeLine.startWidth = routeLineWidth;
        routeLine.endWidth = routeLineWidth;
        routeLine.positionCount = 0;
        routeLine.enabled = false;
    }

    private void GenerateRoute()
    {
        if (start == null || goal == null)
        {
            Debug.LogWarning("RouteTest: start or goal is not assigned.");
            ClearRoute();
            return;
        }

        if (!NavMesh.SamplePosition(
            start.position,
            out NavMeshHit startHit,
            startSearchRadius,
            NavMesh.AllAreas
        ))
        {
            Debug.LogWarning(
                $"RouteTest: start is not on NavMesh. start={start.position}, searchRadius={startSearchRadius:F2}"
            );
            ClearRoute();
            return;
        }

        if (!NavMesh.SamplePosition(
            goal.position,
            out NavMeshHit goalHit,
            goalSearchRadius,
            NavMesh.AllAreas
        ))
        {
            Debug.LogWarning(
                $"RouteTest: goal is not near NavMesh. goal={goal.position}, searchRadius={goalSearchRadius:F2}"
            );
            ClearRoute();
            return;
        }

        bool calculated = NavMesh.CalculatePath(
            startHit.position,
            goalHit.position,
            NavMesh.AllAreas,
            path
        );

        hasPath =
            calculated &&
            path.status == NavMeshPathStatus.PathComplete;

        if (logRouteSearch)
        {
            Debug.Log(
                $"RouteTest: path result. start={startHit.position}, goal={goalHit.position}, calculated={calculated}, status={path.status}, corners={path.corners.Length}, visible={hasPath}"
            );
        }

        ApplyRouteLine();
    }

    private void ClearRoute()
    {
        hasPath = false;

        if (path != null)
            path.ClearCorners();

        if (routeLine != null)
        {
            routeLine.positionCount = 0;
            routeLine.enabled = false;
        }
    }

    private void ApplyRouteLine()
    {
        if (!hasPath ||
            path == null ||
            path.corners.Length < 2 ||
            routeLine == null)
        {
            ClearRoute();
            return;
        }

        routeLine.material = routeLineMaterial;
        routeLine.startWidth = routeLineWidth;
        routeLine.endWidth = routeLineWidth;

        List<Vector3> displayPoints =
            BuildHeightAwareRoutePoints(path);

        if (displayPoints.Count < 2)
        {
            ClearRoute();
            return;
        }

        routeLine.positionCount = displayPoints.Count;
        routeLine.SetPositions(displayPoints.ToArray());
        routeLine.enabled = true;
    }

    private List<Vector3> BuildHeightAwareRoutePoints(NavMeshPath targetPath)
    {
        List<Vector3> points = new();
        Vector3[] corners = targetPath.corners;

        for (int i = 0; i < corners.Length - 1; i++)
        {
            Vector3 from = corners[i];
            Vector3 to = corners[i + 1];
            float distance = Vector3.Distance(from, to);
            int sampleCount = Mathf.Max(
                1,
                Mathf.CeilToInt(
                    distance /
                    Mathf.Max(routeHeightSampleInterval, 0.01f)
                )
            );

            for (int j = 0; j <= sampleCount; j++)
            {
                if (i > 0 && j == 0)
                    continue;

                Vector3 samplePoint =
                    Vector3.Lerp(
                        from,
                        to,
                        (float)j / sampleCount
                    );

                if (NavMesh.SamplePosition(
                    samplePoint,
                    out NavMeshHit heightHit,
                    routeHeightSampleRadius,
                    NavMesh.AllAreas
                ))
                {
                    points.Add(
                        heightHit.position +
                        Vector3.up * routeLineYOffset
                    );
                }
                else
                {
                    points.Add(
                        samplePoint +
                        Vector3.up * routeLineYOffset
                    );
                }
            }
        }

        return points;
    }

    private void OnDrawGizmos()
    {
        if (!hasPath ||
            path == null ||
            path.corners.Length < 2)
            return;

        List<Vector3> displayPoints =
            BuildHeightAwareRoutePoints(path);

        if (displayPoints.Count < 2)
            return;

        Gizmos.color = Color.cyan;

        for (int i = 0; i < displayPoints.Count - 1; i++)
        {
            Gizmos.DrawLine(
                displayPoints[i],
                displayPoints[i + 1]
            );
        }
    }
}
