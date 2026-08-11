using System.Collections.Generic;
using Unity.AI.Navigation;
using UnityEngine;

[DefaultExecutionOrder(-300)]
public class NavLinkGenerator : MonoBehaviour
{
    private const string GeneratedClimbLinkPrefix = "AutoClimbLink_";
    private const string GeneratedJumpLinkPrefix = "AutoJumpLink_";

    [SerializeField] private float maxClimbHeight = 0.5f;
    [SerializeField] private float maxHorizontalDistance = 1.0f;
    [SerializeField] private float jumpMaxHeightDiff = 0.2f;
    [SerializeField] private float jumpMinHorizontalDistance = 0.15f;
    [SerializeField] private float jumpMaxHorizontalDistance = 0.5f;
    [SerializeField] private float linkWidth = 0.5f;
    [SerializeField] private int climbLinkCostModifier = 8;
    [SerializeField] private int jumpLinkCostModifier = 1;
    [SerializeField] private float edgeSampleSpacing = 0.25f;
    [SerializeField] private float edgeInset = 0.05f;
    [SerializeField] private float edgeNavMeshSampleRadius = 0.05f;
    [SerializeField] private bool preventLinksBetweenSplitSurfaces = true;
    [SerializeField] private bool clearOldLinksOnStart = true;
    [SerializeField] private bool logGeneratedLinks = true;

    private readonly HashSet<string> generatedPairKeys = new();

    private void Start()
    {
        GenerateLinks();
    }

    public void GenerateLinks()
    {
        bool clearedOldLinks =
            clearOldLinksOnStart;

        if (clearedOldLinks)
            ClearGeneratedLinks();

        generatedPairKeys.Clear();

        if (!clearedOldLinks)
            CacheExistingGeneratedPairKeys();

        NavMeshSurface[] surfaces =
            FindObjectsOfType<NavMeshSurface>();

        if (logGeneratedLinks)
        {
            Debug.Log(
                $"NavLinkGenerator: found {surfaces.Length} NavMeshSurface objects."
            );
        }

        for (int i = 0; i < surfaces.Length; i++)
        {
            for (int j = i + 1; j < surfaces.Length; j++)
            {
                TryGenerateLink(surfaces[i], surfaces[j]);
            }
        }
    }

    private void TryGenerateLink(
        NavMeshSurface surfaceA,
        NavMeshSurface surfaceB
    )
    {
        if (surfaceA == null ||
            surfaceB == null ||
            surfaceA == surfaceB)
            return;

        if (preventLinksBetweenSplitSurfaces &&
            AreSplitSurfacesFromSameSource(surfaceA, surfaceB))
        {
            if (logGeneratedLinks)
            {
                Debug.Log(
                    $"NavLinkGenerator: skip split surfaces from same source. {surfaceA.name} <-> {surfaceB.name}"
                );
            }

            return;
        }

        string pairKey =
            GetPairKey(surfaceA, surfaceB);

        if (generatedPairKeys.Contains(pairKey))
            return;

        Bounds boundsA =
            GetSurfaceBounds(surfaceA);
        Bounds boundsB =
            GetSurfaceBounds(surfaceB);
        LinkCandidate candidate =
            FindBestLinkCandidate(boundsA, boundsB);
        Vector3 rawEdgeA =
            candidate.PointA;
        Vector3 rawEdgeB =
            candidate.PointB;

        float heightDiff =
            Mathf.Abs(rawEdgeA.y - rawEdgeB.y);
        float horizontalDistance =
            HorizontalDistance(rawEdgeA, rawEdgeB);

        GeneratedLinkType linkType =
            GetLinkType(heightDiff, horizontalDistance);

        if (linkType == GeneratedLinkType.None)
        {
            if (logGeneratedLinks)
            {
                Debug.Log(
                    $"NavLinkGenerator: skip {surfaceA.name} <-> {surfaceB.name}. source={candidate.Source}, heightDiff={heightDiff:F3}, horizontalDistance={horizontalDistance:F3}, climbLimit=height<={maxClimbHeight:F3}/horizontal<={maxHorizontalDistance:F3}, jumpLimit=height<={jumpMaxHeightDiff:F3}/horizontal={jumpMinHorizontalDistance:F3}-{jumpMaxHorizontalDistance:F3}"
                );
            }

            return;
        }

        Vector3 edgeA =
            SampleNavMeshEdgePoint(rawEdgeA);
        Vector3 edgeB =
            SampleNavMeshEdgePoint(rawEdgeB);

        float sampledHeightDiff =
            Mathf.Abs(edgeA.y - edgeB.y);
        float sampledHorizontalDistance =
            HorizontalDistance(edgeA, edgeB);

        GeneratedLinkType sampledLinkType =
            GetLinkType(sampledHeightDiff, sampledHorizontalDistance);

        if (sampledLinkType == GeneratedLinkType.None)
        {
            if (logGeneratedLinks)
            {
                Debug.Log(
                    $"NavLinkGenerator: skip {surfaceA.name} <-> {surfaceB.name} after NavMesh snap. source={candidate.Source}, rawHeightDiff={heightDiff:F3}, rawHorizontalDistance={horizontalDistance:F3}, sampledHeightDiff={sampledHeightDiff:F3}, sampledHorizontalDistance={sampledHorizontalDistance:F3}"
                );
            }

            return;
        }

        linkType = sampledLinkType;

        Vector3 lowCenter =
            edgeA.y <= edgeB.y ? edgeA : edgeB;
        Vector3 highCenter =
            edgeA.y <= edgeB.y ? edgeB : edgeA;

        CreateLink(
            surfaceA,
            surfaceB,
            pairKey,
            linkType,
            lowCenter,
            highCenter
        );

        generatedPairKeys.Add(pairKey);

        if (logGeneratedLinks)
        {
            Debug.Log(
                $"NavLinkGenerator: generated {linkType} {surfaceA.name} <-> {surfaceB.name}. source={candidate.Source}, start={lowCenter}, end={highCenter}, cost={GetLinkCostModifier(linkType)}, rawHeightDiff={heightDiff:F3}, rawHorizontalDistance={horizontalDistance:F3}, sampledHeightDiff={sampledHeightDiff:F3}, sampledHorizontalDistance={sampledHorizontalDistance:F3}"
            );
        }
    }

    private void CreateLink(
        NavMeshSurface surfaceA,
        NavMeshSurface surfaceB,
        string pairKey,
        GeneratedLinkType linkType,
        Vector3 startWorld,
        Vector3 endWorld
    )
    {
        string prefix =
            GetGeneratedLinkPrefix(linkType);
        GameObject linkObject =
            new GameObject(
                $"{prefix}{pairKey}_{surfaceA.name}_{surfaceB.name}"
            );
        linkObject.transform.SetParent(transform, false);
        linkObject.transform.position = startWorld;

        NavMeshLink link =
            linkObject.AddComponent<NavMeshLink>();
        link.bidirectional = true;
        link.width = linkWidth;
        link.costModifier =
            GetLinkCostModifier(linkType);
        link.autoUpdate = true;
        link.startPoint = Vector3.zero;
        link.endPoint =
            linkObject.transform.InverseTransformPoint(endWorld);
    }

    private GeneratedLinkType GetLinkType(
        float heightDiff,
        float horizontalDistance
    )
    {
        if (heightDiff <= jumpMaxHeightDiff &&
            horizontalDistance >= jumpMinHorizontalDistance &&
            horizontalDistance <= jumpMaxHorizontalDistance)
            return GeneratedLinkType.JumpLink;

        if (heightDiff <= maxClimbHeight &&
            horizontalDistance <= maxHorizontalDistance)
            return GeneratedLinkType.ClimbLink;

        return GeneratedLinkType.None;
    }

    private string GetGeneratedLinkPrefix(GeneratedLinkType linkType)
    {
        return linkType == GeneratedLinkType.JumpLink
            ? GeneratedJumpLinkPrefix
            : GeneratedClimbLinkPrefix;
    }

    private int GetLinkCostModifier(GeneratedLinkType linkType)
    {
        return linkType == GeneratedLinkType.JumpLink
            ? jumpLinkCostModifier
            : climbLinkCostModifier;
    }

    private void ClearGeneratedLinks()
    {
        List<GameObject> childrenToDestroy = new();

        for (int i = 0; i < transform.childCount; i++)
        {
            Transform child = transform.GetChild(i);

            if (!IsGeneratedLinkName(child.name) ||
                child.GetComponent<NavMeshLink>() == null)
                continue;

            childrenToDestroy.Add(child.gameObject);
        }

        foreach (GameObject child in childrenToDestroy)
        {
            if (Application.isPlaying)
                Destroy(child);
            else
                DestroyImmediate(child);
        }

        if (logGeneratedLinks &&
            childrenToDestroy.Count > 0)
        {
            Debug.Log(
                $"NavLinkGenerator: cleared {childrenToDestroy.Count} generated NavMeshLink objects."
            );
        }
    }

    private Bounds GetSurfaceBounds(NavMeshSurface surface)
    {
        if (TryGetRendererBounds(surface, out Bounds rendererBounds))
            return rendererBounds;

        if (TryGetColliderBounds(surface, out Bounds colliderBounds))
            return colliderBounds;

        return new Bounds(surface.transform.position, Vector3.zero);
    }

    private LinkCandidate FindBestLinkCandidate(
        Bounds boundsA,
        Bounds boundsB
    )
    {
        LinkCandidate bestCandidate =
            new(boundsA.center, boundsB.center, "center-fallback");
        float bestDistance = float.PositiveInfinity;

        foreach (Vector3 pointA in GetSurfaceEdgeCandidates(boundsA))
        {
            Vector3 pointB =
                GetClosestSurfacePoint(boundsB, pointA);
            float distance =
                HorizontalDistance(pointA, pointB);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestCandidate =
                    new LinkCandidate(
                        pointA,
                        pointB,
                        "A-edge-to-B-surface"
                    );
            }
        }

        foreach (Vector3 pointB in GetSurfaceEdgeCandidates(boundsB))
        {
            Vector3 pointA =
                GetClosestSurfacePoint(boundsA, pointB);
            float distance =
                HorizontalDistance(pointA, pointB);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestCandidate =
                    new LinkCandidate(
                        pointA,
                        pointB,
                        "B-edge-to-A-surface"
                    );
            }
        }

        return bestCandidate;
    }

    private Vector3 GetClosestSurfacePoint(
        Bounds bounds,
        Vector3 target
    )
    {
        return new Vector3(
            ClampToInsetRange(target.x, bounds.min.x, bounds.max.x),
            bounds.max.y,
            ClampToInsetRange(target.z, bounds.min.z, bounds.max.z)
        );
    }

    private float ClampToInsetRange(
        float value,
        float min,
        float max
    )
    {
        float insetMin = min + edgeInset;
        float insetMax = max - edgeInset;

        if (insetMin > insetMax)
            return (min + max) * 0.5f;

        return Mathf.Clamp(value, insetMin, insetMax);
    }

    private Vector3 SampleNavMeshEdgePoint(Vector3 edgePoint)
    {
        if (UnityEngine.AI.NavMesh.SamplePosition(
            edgePoint,
            out UnityEngine.AI.NavMeshHit hit,
            edgeNavMeshSampleRadius,
            UnityEngine.AI.NavMesh.AllAreas
        ))
        {
            return hit.position;
        }

        return edgePoint;
    }

    private List<Vector3> GetSurfaceEdgeCandidates(Bounds bounds)
    {
        List<Vector3> points = new();
        float minX = bounds.min.x + edgeInset;
        float maxX = bounds.max.x - edgeInset;
        float minZ = bounds.min.z + edgeInset;
        float maxZ = bounds.max.z - edgeInset;
        float y = bounds.max.y;
        float spacing = Mathf.Max(edgeSampleSpacing, 0.01f);

        AddEdgeSamples(points, minX, maxX, minZ, minZ, y, spacing);
        AddEdgeSamples(points, minX, maxX, maxZ, maxZ, y, spacing);
        AddEdgeSamples(points, minX, minX, minZ, maxZ, y, spacing);
        AddEdgeSamples(points, maxX, maxX, minZ, maxZ, y, spacing);

        if (points.Count == 0)
            points.Add(bounds.center);

        return points;
    }

    private void AddEdgeSamples(
        List<Vector3> points,
        float startX,
        float endX,
        float startZ,
        float endZ,
        float y,
        float spacing
    )
    {
        Vector2 start = new(startX, startZ);
        Vector2 end = new(endX, endZ);
        float length = Vector2.Distance(start, end);
        int sampleCount =
            Mathf.Max(1, Mathf.CeilToInt(length / spacing));

        for (int i = 0; i <= sampleCount; i++)
        {
            float t = (float)i / sampleCount;
            points.Add(
                new Vector3(
                    Mathf.Lerp(startX, endX, t),
                    y,
                    Mathf.Lerp(startZ, endZ, t)
                )
            );
        }
    }

    private void CacheExistingGeneratedPairKeys()
    {
        for (int i = 0; i < transform.childCount; i++)
        {
            Transform child = transform.GetChild(i);

            if (!IsGeneratedLinkName(child.name) ||
                child.GetComponent<NavMeshLink>() == null)
                continue;

            string rest =
                RemoveGeneratedLinkPrefix(child.name);
            int separatorIndex =
                rest.IndexOf('_');

            if (separatorIndex < 0)
                continue;

            separatorIndex =
                rest.IndexOf('_', separatorIndex + 1);

            if (separatorIndex < 0)
                continue;

            generatedPairKeys.Add(rest.Substring(0, separatorIndex));
        }
    }

    private bool IsGeneratedLinkName(string objectName)
    {
        return objectName.StartsWith(GeneratedClimbLinkPrefix) ||
            objectName.StartsWith(GeneratedJumpLinkPrefix);
    }

    private string RemoveGeneratedLinkPrefix(string objectName)
    {
        if (objectName.StartsWith(GeneratedClimbLinkPrefix))
            return objectName.Substring(GeneratedClimbLinkPrefix.Length);

        if (objectName.StartsWith(GeneratedJumpLinkPrefix))
            return objectName.Substring(GeneratedJumpLinkPrefix.Length);

        return objectName;
    }

    private bool TryGetRendererBounds(
        NavMeshSurface surface,
        out Bounds bounds
    )
    {
        Renderer[] renderers =
            surface.GetComponentsInChildren<Renderer>();
        bool hasBounds = false;
        bounds = new Bounds(surface.transform.position, Vector3.zero);

        foreach (Renderer renderer in renderers)
        {
            if (renderer == null ||
                !renderer.enabled)
                continue;

            if (!hasBounds)
            {
                bounds = renderer.bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(renderer.bounds);
            }
        }

        return hasBounds;
    }

    private bool TryGetColliderBounds(
        NavMeshSurface surface,
        out Bounds bounds
    )
    {
        Collider[] colliders =
            surface.GetComponentsInChildren<Collider>();
        bool hasBounds = false;
        bounds = new Bounds(surface.transform.position, Vector3.zero);

        foreach (Collider collider in colliders)
        {
            if (collider == null ||
                !collider.enabled)
                continue;

            if (!hasBounds)
            {
                bounds = collider.bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(collider.bounds);
            }
        }

        return hasBounds;
    }

    private string GetPairKey(
        NavMeshSurface surfaceA,
        NavMeshSurface surfaceB
    )
    {
        int idA = surfaceA.GetInstanceID();
        int idB = surfaceB.GetInstanceID();

        return idA < idB
            ? $"{idA}_{idB}"
            : $"{idB}_{idA}";
    }

    private bool AreSplitSurfacesFromSameSource(
        NavMeshSurface surfaceA,
        NavMeshSurface surfaceB
    )
    {
        AutoGeneratedNavMeshSurfaceInfo infoA =
            surfaceA.GetComponent<AutoGeneratedNavMeshSurfaceInfo>();
        AutoGeneratedNavMeshSurfaceInfo infoB =
            surfaceB.GetComponent<AutoGeneratedNavMeshSurfaceInfo>();

        if (infoA == null ||
            infoB == null ||
            infoA.SourceInstanceId == 0 ||
            infoB.SourceInstanceId == 0)
            return false;

        return infoA.SourceInstanceId == infoB.SourceInstanceId;
    }

    private float HorizontalDistance(Vector3 a, Vector3 b)
    {
        return Vector2.Distance(
            new Vector2(a.x, a.z),
            new Vector2(b.x, b.z)
        );
    }

    private enum GeneratedLinkType
    {
        None,
        ClimbLink,
        JumpLink
    }

    private readonly struct LinkCandidate
    {
        public LinkCandidate(
            Vector3 pointA,
            Vector3 pointB,
            string source
        )
        {
            PointA = pointA;
            PointB = pointB;
            Source = source;
        }

        public Vector3 PointA { get; }
        public Vector3 PointB { get; }
        public string Source { get; }
    }
}
