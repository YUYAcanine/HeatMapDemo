using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.AI;

public class RouteSearch : MonoBehaviour
{
    [Header("Attention")]
    [SerializeField] private AttentionRiskProvider attentionRiskProvider;
    [SerializeField] private int interestRank = 1;

    [Header("Route")]
    [SerializeField] private Material routeLineMaterial;
    [SerializeField] private float startSearchRadius = 2.0f;
    [SerializeField] private float goalSearchRadius = 0.3f;
    [SerializeField] private float routeLineWidth = 0.05f;
    [SerializeField] private float routeLineYOffset = 0.05f;
    [SerializeField] private float routeHeightSampleInterval = 0.2f;
    [SerializeField] private float routeHeightSampleRadius = 1.0f;
    [SerializeField] private bool logRouteSearch = true;

    [Header("Arrival Time")]
    [SerializeField] private bool showArrivalTimeText = true;
    [SerializeField] private float movementSpeedMetersPerSecond = 0.8f;
    [SerializeField] private float arrivalTimeTextYOffset = 0.2f;
    [SerializeField] private float arrivalTimeTextSize = 0.8f;

    private NavMeshPath attentionPath;
    private bool hasAttentionPath = false;
    private LineRenderer routeLine;
    private TextMeshPro arrivalTimeText;

    private int lastProviderFrameIndex = -1;

    private void Start()
    {
        if (attentionRiskProvider == null)
            attentionRiskProvider = FindObjectOfType<AttentionRiskProvider>();

        CreateRouteLine();
        CreateArrivalTimeText();
        attentionPath = new NavMeshPath();
    }

    private void Update()
    {
        UpdateRouteFromProvider();
        SyncArrivalTimeTextVisibility();
        FaceArrivalTimeTextToCamera();
    }

    private void CreateRouteLine()
    {
        routeLine = new GameObject($"AttentionRouteLine_Rank{Mathf.Max(1, interestRank)}").AddComponent<LineRenderer>();
        routeLine.material = routeLineMaterial;
        routeLine.startWidth = routeLineWidth;
        routeLine.endWidth = routeLineWidth;
        routeLine.positionCount = 0;
        routeLine.enabled = false;
    }

    private void CreateArrivalTimeText()
    {
        arrivalTimeText = new GameObject($"ArrivalTimeText_Rank{Mathf.Max(1, interestRank)}").AddComponent<TextMeshPro>();
        arrivalTimeText.alignment = TextAlignmentOptions.Center;
        arrivalTimeText.fontSize = arrivalTimeTextSize;
        arrivalTimeText.text = "";
        arrivalTimeText.enabled = false;
    }

    private void UpdateRouteFromProvider()
    {
        if (attentionRiskProvider == null ||
            !attentionRiskProvider.HasCurrentFrame ||
            !attentionRiskProvider.HasPelvisPosition)
        {
            ClearAttentionPath();
            return;
        }

        if (lastProviderFrameIndex == attentionRiskProvider.CurrentFrameIndex)
            return;

        lastProviderFrameIndex = attentionRiskProvider.CurrentFrameIndex;

        if (!attentionRiskProvider.TryGetTargetByRank(
            interestRank,
            out Transform target,
            out float attentionScore
        ))
        {
            if (logRouteSearch)
            {
                Debug.Log(
                    $"RouteSearch[rank {Mathf.Max(1, interestRank)}]: no route target."
                );
            }

            ClearAttentionPath();
            return;
        }

        UpdateAttentionPath(
            attentionRiskProvider.PelvisPosition,
            target,
            attentionScore
        );
    }

    private void UpdateAttentionPath(
        Vector3 pelvisPosition,
        Transform target,
        float attentionScore
    )
    {
        if (attentionScore <= 0f || target == null)
        {
            if (logRouteSearch)
            {
                Debug.Log(
                    $"RouteSearch[rank {Mathf.Max(1, interestRank)}]: no route. attentionScore={attentionScore:F2}, target={(target == null ? "null" : target.name)}"
                );
            }

            ClearAttentionPath();
            return;
        }

        if (logRouteSearch)
        {
            Debug.Log(
                $"RouteSearch[rank {Mathf.Max(1, interestRank)}]: trying route. pelvis={pelvisPosition}, target={target.name}, targetPosition={target.position}, attentionScore={attentionScore:F2}"
            );
        }

        if (!NavMesh.SamplePosition(
            pelvisPosition,
            out NavMeshHit startHit,
            startSearchRadius,
            NavMesh.AllAreas
        ))
        {
            if (logRouteSearch)
            {
                Debug.LogWarning(
                    $"RouteSearch[rank {Mathf.Max(1, interestRank)}]: start is not on NavMesh. pelvis={pelvisPosition}, searchRadius={startSearchRadius:F2}"
                );
            }

            ClearAttentionPath();
            return;
        }

        if (!NavMesh.SamplePosition(
            target.position,
            out NavMeshHit goalHit,
            goalSearchRadius,
            NavMesh.AllAreas
        ))
        {
            if (logRouteSearch)
            {
                Debug.LogWarning(
                    $"RouteSearch[rank {Mathf.Max(1, interestRank)}]: goal is not near NavMesh. target={target.name}, targetPosition={target.position}, searchRadius={goalSearchRadius:F2}"
                );
            }

            ClearAttentionPath();
            return;
        }

        bool calculated = NavMesh.CalculatePath(
            startHit.position,
            goalHit.position,
            NavMesh.AllAreas,
            attentionPath
        );

        hasAttentionPath =
            calculated &&
            attentionPath.status == NavMeshPathStatus.PathComplete;

        if (logRouteSearch)
        {
            Debug.Log(
                $"RouteSearch[rank {Mathf.Max(1, interestRank)}]: path result. calculated={calculated}, status={attentionPath.status}, corners={attentionPath.corners.Length}, visible={hasAttentionPath}"
            );
        }

        ApplyRouteLine();
    }

    private void ClearAttentionPath()
    {
        hasAttentionPath = false;

        if (attentionPath != null)
            attentionPath.ClearCorners();

        if (routeLine != null)
        {
            routeLine.positionCount = 0;
            routeLine.enabled = false;
        }

        if (arrivalTimeText != null)
        {
            arrivalTimeText.text = "";
            arrivalTimeText.enabled = false;
        }
    }

    private void ApplyRouteLine()
    {
        if (!hasAttentionPath ||
            attentionPath == null ||
            attentionPath.corners.Length < 2 ||
            routeLine == null)
        {
            ClearAttentionPath();
            return;
        }

        routeLine.material = routeLineMaterial;
        routeLine.startWidth = routeLineWidth;
        routeLine.endWidth = routeLineWidth;

        List<Vector3> displayCorners =
            BuildHeightAwareRoutePoints(attentionPath);

        if (displayCorners.Count < 2)
        {
            ClearAttentionPath();
            return;
        }

        routeLine.positionCount = displayCorners.Count;
        routeLine.SetPositions(displayCorners.ToArray());
        routeLine.enabled = true;

        ApplyArrivalTimeText(displayCorners);

        if (logRouteSearch)
        {
            Debug.Log(
                $"RouteSearch[rank {Mathf.Max(1, interestRank)}]: route line updated. originalCorners={attentionPath.corners.Length}, displayPoints={displayCorners.Count}"
            );
        }
    }

    private void ApplyArrivalTimeText(List<Vector3> routePoints)
    {
        if (arrivalTimeText == null)
            return;

        if (!showArrivalTimeText ||
            routePoints == null ||
            routePoints.Count < 2 ||
            movementSpeedMetersPerSecond <= 0f)
        {
            arrivalTimeText.text = "";
            arrivalTimeText.enabled = false;
            return;
        }

        float routeLength = CalculateRouteLength(routePoints);
        float arrivalTimeSec =
            routeLength /
            movementSpeedMetersPerSecond;

        arrivalTimeText.fontSize = arrivalTimeTextSize;
        arrivalTimeText.color = GetRouteLineColor();
        arrivalTimeText.text =
            $"{arrivalTimeSec:F1}s\n{routeLength:F2}m";
        arrivalTimeText.transform.position =
            GetRouteMidpoint(routePoints) +
            Vector3.up * arrivalTimeTextYOffset;
        arrivalTimeText.enabled = true;
        FaceArrivalTimeTextToCamera();
    }

    private float CalculateRouteLength(List<Vector3> routePoints)
    {
        float length = 0f;

        for (int i = 0; i < routePoints.Count - 1; i++)
        {
            length += Vector3.Distance(
                routePoints[i],
                routePoints[i + 1]
            );
        }

        return length;
    }

    private Vector3 GetRouteMidpoint(List<Vector3> routePoints)
    {
        float totalLength = CalculateRouteLength(routePoints);
        float halfLength = totalLength * 0.5f;
        float walkedLength = 0f;

        for (int i = 0; i < routePoints.Count - 1; i++)
        {
            Vector3 from = routePoints[i];
            Vector3 to = routePoints[i + 1];
            float segmentLength = Vector3.Distance(from, to);

            if (walkedLength + segmentLength >= halfLength)
            {
                float t =
                    (halfLength - walkedLength) /
                    Mathf.Max(segmentLength, 0.0001f);

                return Vector3.Lerp(
                    from,
                    to,
                    t
                );
            }

            walkedLength += segmentLength;
        }

        return routePoints[routePoints.Count - 1];
    }

    private Color GetRouteLineColor()
    {
        if (routeLineMaterial != null &&
            routeLineMaterial.HasProperty("_Color"))
        {
            return routeLineMaterial.color;
        }

        if (routeLineMaterial != null &&
            routeLineMaterial.HasProperty("_BaseColor"))
        {
            return routeLineMaterial.GetColor("_BaseColor");
        }

        return Color.white;
    }

    private void SyncArrivalTimeTextVisibility()
    {
        if (showArrivalTimeText ||
            arrivalTimeText == null)
            return;

        arrivalTimeText.text = "";
        arrivalTimeText.enabled = false;
    }

    private void FaceArrivalTimeTextToCamera()
    {
        if (arrivalTimeText == null ||
            !arrivalTimeText.enabled ||
            Camera.main == null)
            return;

        arrivalTimeText.transform.rotation =
            Quaternion.LookRotation(
                arrivalTimeText.transform.position -
                Camera.main.transform.position
            );
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
        if (!hasAttentionPath ||
            attentionPath == null ||
            attentionPath.corners.Length < 2)
            return;

        List<Vector3> displayCorners =
            BuildHeightAwareRoutePoints(attentionPath);

        if (displayCorners.Count < 2)
            return;

        Gizmos.color = Color.cyan;

        for (int i = 0; i < displayCorners.Count - 1; i++)
        {
            Gizmos.DrawLine(
                displayCorners[i],
                displayCorners[i + 1]
            );
        }
    }
}
