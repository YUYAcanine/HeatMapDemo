using System.Collections.Generic;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;

[DefaultExecutionOrder(-200)]
public class AutoNavMeshSurfaceGenerator : MonoBehaviour
{
    private const string GeneratedRootName = "AutoNavMeshSurfaces";
    private const string GeneratedSurfacePrefix = "AutoNavMeshSurface_";
    private const string GeneratedTopColliderName = "AutoTopCollider";

    [Header("Source")]
    [SerializeField] private string navMeshOnlyLayerName = "NavMeshOnly";
    [SerializeField] private bool ignoreChildObjectsOnSameLayer = true;

    [Header("Generated Top Surface")]
    [SerializeField] private float topSurfaceThickness = 0.02f;
    [SerializeField] private float topSurfaceYOffset = 0.0f;
    [SerializeField] private bool expandByAgentRadius = true;
    [SerializeField] private float topSurfaceExpansion = 0.0f;
    [SerializeField] private float boundsInset = 0.0f;

    [Header("Obstacle Split")]
    [SerializeField] private bool splitByNavMeshObstacles = true;
    [SerializeField] private float obstacleSplitPadding = 0.02f;
    [SerializeField] private float minSplitSurfaceSize = 0.05f;

    [Header("Build")]
    [SerializeField] private bool clearOldGeneratedSurfacesOnStart = true;
    [SerializeField] private bool buildOnStart = true;
    [SerializeField] private NavLinkGenerator navLinkGenerator;
    [SerializeField] private bool generateLinksAfterBuild = true;
    [SerializeField] private bool logGeneratedSurfaces = true;

    private Transform generatedRoot;

    private void Start()
    {
        if (buildOnStart)
            GenerateSurfaces();
    }

    public void GenerateSurfaces()
    {
        int layer =
            LayerMask.NameToLayer(navMeshOnlyLayerName);

        if (layer < 0)
        {
            Debug.LogWarning(
                $"AutoNavMeshSurfaceGenerator: layer '{navMeshOnlyLayerName}' does not exist."
            );
            return;
        }

        generatedRoot =
            GetOrCreateGeneratedRoot();

        if (clearOldGeneratedSurfacesOnStart)
            ClearGeneratedSurfaces();

        List<GameObject> sourceObjects =
            FindSourceObjects(layer);

        if (logGeneratedSurfaces)
        {
            Debug.Log(
                $"AutoNavMeshSurfaceGenerator: found {sourceObjects.Count} source objects on layer '{navMeshOnlyLayerName}'."
            );
        }

        int generatedCount = 0;

        foreach (GameObject sourceObject in sourceObjects)
        {
            if (!TryGetObjectBounds(sourceObject, out Bounds bounds))
            {
                if (logGeneratedSurfaces)
                {
                    Debug.LogWarning(
                        $"AutoNavMeshSurfaceGenerator: skip {sourceObject.name}. No Renderer or Collider bounds found."
                    );
                }

                continue;
            }

            generatedCount += CreateSurfacesForObject(sourceObject, bounds, layer);
        }

        if (logGeneratedSurfaces)
        {
            Debug.Log(
                $"AutoNavMeshSurfaceGenerator: generated {generatedCount} NavMeshSurface objects."
            );
        }

        if (generateLinksAfterBuild)
        {
            if (navLinkGenerator == null)
                navLinkGenerator = FindObjectOfType<NavLinkGenerator>();

            if (navLinkGenerator != null)
                navLinkGenerator.GenerateLinks();
            else if (logGeneratedSurfaces)
                Debug.LogWarning("AutoNavMeshSurfaceGenerator: NavLinkGenerator was not found.");
        }
    }

    private int CreateSurfacesForObject(
        GameObject sourceObject,
        Bounds bounds,
        int layer
    )
    {
        float expansion =
            GetTopSurfaceExpansion();
        Vector3 fullSize =
            new(
                Mathf.Max(bounds.size.x + expansion * 2f - boundsInset * 2f, 0.01f),
                Mathf.Max(topSurfaceThickness, 0.001f),
                Mathf.Max(bounds.size.z + expansion * 2f - boundsInset * 2f, 0.01f)
            );
        Vector3 fullTopCenter =
            new(
                bounds.center.x,
                bounds.max.y + topSurfaceYOffset - fullSize.y * 0.5f,
                bounds.center.z
            );

        Bounds topBounds =
            new(fullTopCenter, fullSize);
        List<Bounds> splitBounds =
            splitByNavMeshObstacles
                ? BuildSplitTopBounds(topBounds)
                : new List<Bounds> { topBounds };

        int generatedCount = 0;

        for (int i = 0; i < splitBounds.Count; i++)
        {
            CreateSurfaceForBounds(
                sourceObject,
                splitBounds[i],
                layer,
                splitBounds.Count > 1 ? i + 1 : 0,
                expansion
            );
            generatedCount++;
        }

        return generatedCount;
    }

    private void CreateSurfaceForBounds(
        GameObject sourceObject,
        Bounds topBounds,
        int layer,
        int splitIndex,
        float expansion
    )
    {
        string splitSuffix =
            splitIndex > 0 ? $"_Split{splitIndex}" : "";

        GameObject surfaceObject =
            new($"{GeneratedSurfacePrefix}{sourceObject.name}{splitSuffix}");
        surfaceObject.transform.SetParent(generatedRoot, false);
        surfaceObject.transform.position = topBounds.center;
        surfaceObject.layer = layer;

        AutoGeneratedNavMeshSurfaceInfo info =
            surfaceObject.AddComponent<AutoGeneratedNavMeshSurfaceInfo>();
        info.Initialize(sourceObject);

        BoxCollider topCollider =
            new GameObject(GeneratedTopColliderName).AddComponent<BoxCollider>();
        topCollider.transform.SetParent(surfaceObject.transform, false);
        topCollider.transform.localPosition = Vector3.zero;
        topCollider.transform.localRotation = Quaternion.identity;
        topCollider.transform.localScale = Vector3.one;
        topCollider.gameObject.layer = layer;
        topCollider.size = topBounds.size;
        topCollider.center = Vector3.zero;

        NavMeshSurface surface =
            surfaceObject.AddComponent<NavMeshSurface>();
        surface.collectObjects = CollectObjects.Children;
        surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
        surface.layerMask = 1 << layer;
        surface.BuildNavMesh();

        if (logGeneratedSurfaces)
        {
            Debug.Log(
                $"AutoNavMeshSurfaceGenerator: built {surfaceObject.name}. source={sourceObject.name}, topY={(topBounds.max.y):F3}, expansion={expansion:F3}, size={topBounds.size}"
            );
        }
    }

    private float GetTopSurfaceExpansion()
    {
        if (!expandByAgentRadius)
            return Mathf.Max(topSurfaceExpansion, 0f);

        NavMeshBuildSettings buildSettings =
            NavMesh.GetSettingsByID(0);

        return Mathf.Max(
            buildSettings.agentRadius + topSurfaceExpansion,
            0f
        );
    }

    private List<Bounds> BuildSplitTopBounds(Bounds topBounds)
    {
        List<Bounds> obstacleBounds =
            CollectOverlappingObstacleBounds(topBounds);

        if (obstacleBounds.Count == 0)
            return new List<Bounds> { topBounds };

        List<Bounds> splitBounds = new()
        {
            topBounds
        };

        int maxSplitIterations =
            Mathf.Max(obstacleBounds.Count, 1);

        for (int i = 0; i < maxSplitIterations; i++)
        {
            int beforeCount =
                splitBounds.Count;
            splitBounds =
                SplitBoundsByBlockingObstacles(splitBounds, obstacleBounds);

            if (splitBounds.Count == beforeCount)
                break;
        }

        if (splitBounds.Count == 0)
        {
            if (logGeneratedSurfaces)
            {
                Debug.LogWarning(
                    $"AutoNavMeshSurfaceGenerator: obstacle split produced no cells. fallback to full top bounds. center={topBounds.center}, size={topBounds.size}"
                );
            }

            splitBounds.Add(topBounds);
        }

        if (logGeneratedSurfaces &&
            splitBounds.Count > 1)
        {
            Debug.Log(
                $"AutoNavMeshSurfaceGenerator: split top surface by obstacles. cells={splitBounds.Count}, obstacles={obstacleBounds.Count}, center={topBounds.center}, size={topBounds.size}"
            );
        }

        return splitBounds;
    }

    private List<Bounds> SplitBoundsByBlockingObstacles(
        List<Bounds> sourceBounds,
        List<Bounds> obstacles
    )
    {
        List<Bounds> results = new();

        foreach (Bounds bounds in sourceBounds)
        {
            List<Bounds> overlappingObstacles =
                GetOverlappingObstacles(bounds, obstacles);

            if (overlappingObstacles.Count == 0)
            {
                results.Add(bounds);
                continue;
            }

            if (TryGetBlockingBand(
                bounds,
                overlappingObstacles,
                true,
                out float bandMin,
                out float bandMax
            ))
            {
                AddSplitBounds(
                    results,
                    bounds,
                    bounds.min.x,
                    bounds.max.x,
                    bounds.min.z,
                    Mathf.Clamp(bandMin, bounds.min.z, bounds.max.z)
                );
                AddSplitBounds(
                    results,
                    bounds,
                    bounds.min.x,
                    bounds.max.x,
                    Mathf.Clamp(bandMax, bounds.min.z, bounds.max.z),
                    bounds.max.z
                );
                continue;
            }

            if (TryGetBlockingBand(
                bounds,
                overlappingObstacles,
                false,
                out bandMin,
                out bandMax
            ))
            {
                AddSplitBounds(
                    results,
                    bounds,
                    bounds.min.x,
                    Mathf.Clamp(bandMin, bounds.min.x, bounds.max.x),
                    bounds.min.z,
                    bounds.max.z
                );
                AddSplitBounds(
                    results,
                    bounds,
                    Mathf.Clamp(bandMax, bounds.min.x, bounds.max.x),
                    bounds.max.x,
                    bounds.min.z,
                    bounds.max.z
                );
                continue;
            }

            results.Add(bounds);
        }

        return results;
    }

    private List<Bounds> GetOverlappingObstacles(
        Bounds bounds,
        List<Bounds> obstacles
    )
    {
        List<Bounds> results = new();

        foreach (Bounds obstacle in obstacles)
        {
            if (OverlapsXZ(bounds, obstacle))
                results.Add(obstacle);
        }

        return results;
    }

    private bool TryGetBlockingBand(
        Bounds bounds,
        List<Bounds> obstacles,
        bool checkXCoverage,
        out float bandMin,
        out float bandMax
    )
    {
        bandMin = 0f;
        bandMax = 0f;

        List<Bounds> remaining =
            new(obstacles);

        while (remaining.Count > 0)
        {
            List<Bounds> group = new()
            {
                remaining[0]
            };
            remaining.RemoveAt(0);

            bool changed = true;
            while (changed)
            {
                changed = false;

                for (int i = remaining.Count - 1; i >= 0; i--)
                {
                    if (!IsConnectedToGroup(
                        remaining[i],
                        group,
                        checkXCoverage
                    ))
                        continue;

                    group.Add(remaining[i]);
                    remaining.RemoveAt(i);
                    changed = true;
                }
            }

            if (!GroupBlocksBounds(
                bounds,
                group,
                checkXCoverage,
                out bandMin,
                out bandMax
            ))
                continue;

            return true;
        }

        return false;
    }

    private bool IsConnectedToGroup(
        Bounds candidate,
        List<Bounds> group,
        bool checkXCoverage
    )
    {
        float tolerance =
            GetObstacleConnectionTolerance();

        foreach (Bounds other in group)
        {
            bool overlapsAlongBand =
                checkXCoverage
                    ? candidate.min.z <= other.max.z + tolerance &&
                      candidate.max.z >= other.min.z - tolerance
                    : candidate.min.x <= other.max.x + tolerance &&
                      candidate.max.x >= other.min.x - tolerance;
            bool touchesAlongCoverage =
                checkXCoverage
                    ? candidate.min.x <= other.max.x + tolerance &&
                      candidate.max.x >= other.min.x - tolerance
                    : candidate.min.z <= other.max.z + tolerance &&
                      candidate.max.z >= other.min.z - tolerance;

            if (overlapsAlongBand &&
                touchesAlongCoverage)
                return true;
        }

        return false;
    }

    private bool GroupBlocksBounds(
        Bounds bounds,
        List<Bounds> group,
        bool checkXCoverage,
        out float bandMin,
        out float bandMax
    )
    {
        bandMin = float.PositiveInfinity;
        bandMax = float.NegativeInfinity;

        List<Vector2> intervals = new();

        foreach (Bounds obstacle in group)
        {
            if (checkXCoverage)
            {
                intervals.Add(
                    new Vector2(
                        Mathf.Clamp(obstacle.min.x, bounds.min.x, bounds.max.x),
                        Mathf.Clamp(obstacle.max.x, bounds.min.x, bounds.max.x)
                    )
                );
                bandMin = Mathf.Min(bandMin, obstacle.min.z);
                bandMax = Mathf.Max(bandMax, obstacle.max.z);
            }
            else
            {
                intervals.Add(
                    new Vector2(
                        Mathf.Clamp(obstacle.min.z, bounds.min.z, bounds.max.z),
                        Mathf.Clamp(obstacle.max.z, bounds.min.z, bounds.max.z)
                    )
                );
                bandMin = Mathf.Min(bandMin, obstacle.min.x);
                bandMax = Mathf.Max(bandMax, obstacle.max.x);
            }
        }

        intervals.Sort((a, b) => a.x.CompareTo(b.x));

        float coverageStart =
            checkXCoverage ? bounds.min.x : bounds.min.z;
        float coverageEnd =
            checkXCoverage ? bounds.max.x : bounds.max.z;
        float coveredUntil =
            coverageStart;
        float gapTolerance =
            Mathf.Max(obstacleSplitPadding, 0f) * 2f + 0.001f;

        foreach (Vector2 interval in intervals)
        {
            if (interval.y <= coveredUntil)
                continue;

            if (interval.x > coveredUntil + gapTolerance)
                return false;

            coveredUntil = interval.y;

            if (coveredUntil >= coverageEnd - gapTolerance)
                return true;
        }

        return coveredUntil >= coverageEnd - gapTolerance;
    }

    private float GetObstacleConnectionTolerance()
    {
        return Mathf.Max(obstacleSplitPadding, 0f) * 2f + 0.001f;
    }

    private void AddSplitBounds(
        List<Bounds> boundsList,
        Bounds originalBounds,
        float minX,
        float maxX,
        float minZ,
        float maxZ
    )
    {
        float minSize =
            Mathf.Max(minSplitSurfaceSize, 0.01f);

        if (maxX - minX < minSize ||
            maxZ - minZ < minSize)
            return;

        Vector3 center =
            new(
                (minX + maxX) * 0.5f,
                originalBounds.center.y,
                (minZ + maxZ) * 0.5f
            );
        Vector3 size =
            new(
                maxX - minX,
                originalBounds.size.y,
                maxZ - minZ
            );

        boundsList.Add(new Bounds(center, size));
    }

    private List<Bounds> CollectOverlappingObstacleBounds(Bounds topBounds)
    {
        List<Bounds> obstacleBounds = new();
        NavMeshObstacle[] obstacles =
            FindObjectsOfType<NavMeshObstacle>();

        foreach (NavMeshObstacle obstacle in obstacles)
        {
            if (obstacle == null ||
                !obstacle.enabled ||
                IsGeneratedObject(obstacle.transform))
                continue;

            Bounds bounds =
                GetObstacleBounds(obstacle);
            bounds.Expand(
                new Vector3(
                    Mathf.Max(obstacleSplitPadding, 0f) * 2f,
                    0f,
                    Mathf.Max(obstacleSplitPadding, 0f) * 2f
                )
            );

            if (!OverlapsXZ(topBounds, bounds))
                continue;

            obstacleBounds.Add(bounds);
        }

        return obstacleBounds;
    }

    private Bounds GetObstacleBounds(NavMeshObstacle obstacle)
    {
        Collider collider =
            obstacle.GetComponent<Collider>();

        if (collider != null)
            return collider.bounds;

        Renderer renderer =
            obstacle.GetComponent<Renderer>();

        if (renderer != null)
            return renderer.bounds;

        return new Bounds(obstacle.transform.position, obstacle.size);
    }

    private bool OverlapsXZ(Bounds a, Bounds b)
    {
        return a.min.x < b.max.x &&
            a.max.x > b.min.x &&
            a.min.z < b.max.z &&
            a.max.z > b.min.z;
    }

    private Transform GetOrCreateGeneratedRoot()
    {
        Transform existingRoot =
            transform.Find(GeneratedRootName);

        if (existingRoot != null)
            return existingRoot;

        GameObject root =
            new(GeneratedRootName);
        root.transform.SetParent(transform, false);
        return root.transform;
    }

    private void ClearGeneratedSurfaces()
    {
        if (generatedRoot == null)
            return;

        List<GameObject> childrenToDestroy = new();

        for (int i = 0; i < generatedRoot.childCount; i++)
        {
            Transform child =
                generatedRoot.GetChild(i);

            if (!child.name.StartsWith(GeneratedSurfacePrefix))
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

        if (logGeneratedSurfaces &&
            childrenToDestroy.Count > 0)
        {
            Debug.Log(
                $"AutoNavMeshSurfaceGenerator: cleared {childrenToDestroy.Count} generated surfaces."
            );
        }
    }

    private List<GameObject> FindSourceObjects(int layer)
    {
        List<GameObject> sourceObjects = new();
        Transform[] transforms =
            FindObjectsOfType<Transform>();

        foreach (Transform target in transforms)
        {
            if (target == null ||
                target.gameObject.layer != layer ||
                IsGeneratedObject(target))
                continue;

            if (ignoreChildObjectsOnSameLayer &&
                HasParentOnLayer(target, layer))
                continue;

            sourceObjects.Add(target.gameObject);
        }

        return sourceObjects;
    }

    private bool HasParentOnLayer(
        Transform target,
        int layer
    )
    {
        Transform parent =
            target.parent;

        while (parent != null)
        {
            if (parent.gameObject.layer == layer)
                return true;

            parent = parent.parent;
        }

        return false;
    }

    private bool IsGeneratedObject(Transform target)
    {
        Transform current =
            target;

        while (current != null)
        {
            if (current.name == GeneratedRootName ||
                current.name.StartsWith(GeneratedSurfacePrefix) ||
                current.name == GeneratedTopColliderName)
                return true;

            current = current.parent;
        }

        return false;
    }

    private bool TryGetObjectBounds(
        GameObject sourceObject,
        out Bounds bounds
    )
    {
        if (TryGetRendererBounds(sourceObject, out bounds))
            return true;

        return TryGetColliderBounds(sourceObject, out bounds);
    }

    private bool TryGetRendererBounds(
        GameObject sourceObject,
        out Bounds bounds
    )
    {
        Renderer[] renderers =
            sourceObject.GetComponentsInChildren<Renderer>();
        bool hasBounds = false;
        bounds = new Bounds(sourceObject.transform.position, Vector3.zero);

        foreach (Renderer renderer in renderers)
        {
            if (renderer == null ||
                !renderer.enabled ||
                IsGeneratedObject(renderer.transform))
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
        GameObject sourceObject,
        out Bounds bounds
    )
    {
        Collider[] colliders =
            sourceObject.GetComponentsInChildren<Collider>();
        bool hasBounds = false;
        bounds = new Bounds(sourceObject.transform.position, Vector3.zero);

        foreach (Collider collider in colliders)
        {
            if (collider == null ||
                !collider.enabled ||
                IsGeneratedObject(collider.transform))
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
}
