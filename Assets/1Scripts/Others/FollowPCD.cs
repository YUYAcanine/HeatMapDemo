using System.Collections.Generic;
using UnityEngine;

public class FollowPCD : MonoBehaviour
{
    private const int RecommendedMinDifferencePoints = 150;
    private const int RecommendedMinClusterPoints = 80;
    private const float RecommendedClusterVoxelSize = 0.12f;
    private const bool RecommendedUse26NeighborConnection = true;
    private const bool RecommendedLimitFollowJumpDistance = true;
    private const float RecommendedMaxFollowJumpDistance = 1.0f;
    private const bool RecommendedFollowPosition = true;
    private const bool RecommendedAlignTargetBoundsCenter = true;
    private const bool RecommendedKeepOriginalHeight = true;
    private const float RecommendedPositionSmoothTime = 0.05f;
    private const bool RecommendedDrawDebugGizmos = true;

    [Header("Point Cloud Difference")]
    public RealtimePointCloudDifference differenceSource;

    [Header("Follow Target")]
    public GameObject[] targetCandidates;

    [Header("Largest Cluster")]
    public int minDifferencePoints =
        RecommendedMinDifferencePoints;
    public int minClusterPoints =
        RecommendedMinClusterPoints;
    public float clusterVoxelSize =
        RecommendedClusterVoxelSize;
    public bool use26NeighborConnection =
        RecommendedUse26NeighborConnection;
    public bool limitFollowJumpDistance =
        RecommendedLimitFollowJumpDistance;
    public float maxFollowJumpDistance =
        RecommendedMaxFollowJumpDistance;

    [Header("Follow")]
    public bool followPosition =
        RecommendedFollowPosition;
    public bool alignTargetBoundsCenter =
        RecommendedAlignTargetBoundsCenter;
    public bool keepOriginalHeight =
        RecommendedKeepOriginalHeight;
    public float positionSmoothTime =
        RecommendedPositionSmoothTime;

    [Header("Debug")]
    public bool drawDebugGizmos =
        RecommendedDrawDebugGizmos;

    private GameObject currentTarget;
    private Vector3 followVelocity;
    private Vector3 targetBoundsOffset;
    private bool hasDebugClusterBounds;
    private Bounds debugClusterBoundsWorld;

    private readonly Dictionary<Vector3Int, VoxelData> voxels =
        new Dictionary<Vector3Int, VoxelData>();

    private readonly HashSet<Vector3Int> visited =
        new HashSet<Vector3Int>();

    private readonly Queue<Vector3Int> queue =
        new Queue<Vector3Int>();

    void Reset()
    {
        ApplyRecommendedDefaults();
    }

    [ContextMenu("Apply Recommended Defaults")]
    void ApplyRecommendedDefaults()
    {
        minDifferencePoints =
            RecommendedMinDifferencePoints;
        minClusterPoints =
            RecommendedMinClusterPoints;
        clusterVoxelSize =
            RecommendedClusterVoxelSize;
        use26NeighborConnection =
            RecommendedUse26NeighborConnection;
        limitFollowJumpDistance =
            RecommendedLimitFollowJumpDistance;
        maxFollowJumpDistance =
            RecommendedMaxFollowJumpDistance;
        followPosition =
            RecommendedFollowPosition;
        alignTargetBoundsCenter =
            RecommendedAlignTargetBoundsCenter;
        keepOriginalHeight =
            RecommendedKeepOriginalHeight;
        positionSmoothTime =
            RecommendedPositionSmoothTime;
        drawDebugGizmos =
            RecommendedDrawDebugGizmos;
    }

    void Start()
    {
        if (differenceSource == null)
        {
            differenceSource =
                FindObjectOfType<RealtimePointCloudDifference>();
        }

        currentTarget = GetFollowTarget();

        if (currentTarget != null)
        {
            targetBoundsOffset =
                GetBoundsOffset(currentTarget);
        }
    }

    void Update()
    {
        if (!followPosition ||
            differenceSource == null)
        {
            hasDebugClusterBounds = false;
            return;
        }

        if (currentTarget == null)
        {
            currentTarget = GetFollowTarget();

            if (currentTarget == null)
                return;

            targetBoundsOffset =
                GetBoundsOffset(currentTarget);
        }

        if (differenceSource.LatestDifferenceCount <
            minDifferencePoints)
        {
            hasDebugClusterBounds = false;
            return;
        }

        Bounds largestClusterBounds;
        int largestClusterPointCount;

        if (!TryGetLargestClusterBounds(
                out largestClusterBounds,
                out largestClusterPointCount))
        {
            hasDebugClusterBounds = false;
            return;
        }

        if (largestClusterPointCount < minClusterPoints)
        {
            hasDebugClusterBounds = false;
            return;
        }

        FollowCluster(largestClusterBounds);
    }

    GameObject GetFollowTarget()
    {
        if (targetCandidates == null ||
            targetCandidates.Length == 0)
        {
            return null;
        }

        return targetCandidates[0];
    }

    bool TryGetLargestClusterBounds(
        out Bounds largestBounds,
        out int largestPointCount)
    {
        IReadOnlyList<Vector3> points =
            differenceSource.LatestDifferencePoints;

        voxels.Clear();
        visited.Clear();
        queue.Clear();

        for (int i = 0;
             i < points.Count;
             i++)
        {
            Vector3 point =
                points[i];

            Vector3Int voxel =
                ToVoxel(point);

            VoxelData data;

            if (!voxels.TryGetValue(
                    voxel,
                    out data))
            {
                data = new VoxelData(point);
            }
            else
            {
                data.Add(point);
            }

            voxels[voxel] = data;
        }

        largestBounds = new Bounds();
        largestPointCount = 0;

        foreach (Vector3Int voxel in voxels.Keys)
        {
            if (visited.Contains(voxel))
                continue;

            Bounds clusterBounds;
            int clusterPointCount;

            FloodFillCluster(
                voxel,
                out clusterBounds,
                out clusterPointCount);

            if (!IsClusterAllowedToFollow(
                    clusterBounds))
            {
                continue;
            }

            if (clusterPointCount > largestPointCount)
            {
                largestPointCount =
                    clusterPointCount;

                largestBounds =
                    clusterBounds;
            }
        }

        return largestPointCount > 0;
    }

    bool IsClusterAllowedToFollow(Bounds clusterBounds)
    {
        if (!limitFollowJumpDistance ||
            currentTarget == null)
        {
            return true;
        }

        Bounds clusterBoundsWorld =
            TransformBoundsToWorld(clusterBounds);

        Vector3 clusterCenter =
            clusterBoundsWorld.center;

        Vector3 targetReference =
            GetCurrentTargetReferencePosition();

        if (keepOriginalHeight)
        {
            clusterCenter.y =
                targetReference.y;
        }

        return Vector3.Distance(
            clusterCenter,
            targetReference) <= maxFollowJumpDistance;
    }

    void FloodFillCluster(
        Vector3Int startVoxel,
        out Bounds clusterBounds,
        out int clusterPointCount)
    {
        VoxelData startData =
            voxels[startVoxel];

        clusterBounds =
            startData.bounds;

        clusterPointCount =
            0;

        visited.Add(startVoxel);
        queue.Enqueue(startVoxel);

        while (queue.Count > 0)
        {
            Vector3Int voxel =
                queue.Dequeue();

            VoxelData data =
                voxels[voxel];

            clusterPointCount +=
                data.pointCount;

            clusterBounds.Encapsulate(
                data.bounds);

            for (int x = -1; x <= 1; x++)
            {
                for (int y = -1; y <= 1; y++)
                {
                    for (int z = -1; z <= 1; z++)
                    {
                        if (x == 0 &&
                            y == 0 &&
                            z == 0)
                        {
                            continue;
                        }

                        if (!use26NeighborConnection &&
                            Mathf.Abs(x) +
                            Mathf.Abs(y) +
                            Mathf.Abs(z) != 1)
                        {
                            continue;
                        }

                        Vector3Int neighbor =
                            new Vector3Int(
                                voxel.x + x,
                                voxel.y + y,
                                voxel.z + z);

                        if (visited.Contains(neighbor) ||
                            !voxels.ContainsKey(neighbor))
                        {
                            continue;
                        }

                        visited.Add(neighbor);
                        queue.Enqueue(neighbor);
                    }
                }
            }
        }
    }

    void FollowCluster(Bounds clusterBounds)
    {
        Bounds clusterBoundsWorld =
            TransformBoundsToWorld(clusterBounds);

        hasDebugClusterBounds = true;
        debugClusterBoundsWorld =
            clusterBoundsWorld;

        Vector3 targetPosition =
            clusterBoundsWorld.center;

        if (alignTargetBoundsCenter)
        {
            targetPosition -= targetBoundsOffset;
        }

        if (keepOriginalHeight)
        {
            targetPosition.y =
                currentTarget.transform.position.y;
        }

        currentTarget.transform.position =
            Vector3.SmoothDamp(
                currentTarget.transform.position,
                targetPosition,
                ref followVelocity,
                positionSmoothTime);
    }

    Vector3 GetCurrentTargetReferencePosition()
    {
        Vector3 referencePosition =
            currentTarget.transform.position;

        if (alignTargetBoundsCenter)
        {
            referencePosition +=
                targetBoundsOffset;
        }

        return referencePosition;
    }

    Bounds TransformBoundsToWorld(Bounds localBounds)
    {
        Transform sourceTransform =
            differenceSource.transform;

        Vector3 center =
            sourceTransform.TransformPoint(
                localBounds.center);

        Bounds worldBounds =
            new Bounds(center, Vector3.zero);

        Vector3 extents =
            localBounds.extents;

        for (int x = -1; x <= 1; x += 2)
        {
            for (int y = -1; y <= 1; y += 2)
            {
                for (int z = -1; z <= 1; z += 2)
                {
                    Vector3 corner =
                        localBounds.center +
                        Vector3.Scale(
                            extents,
                            new Vector3(x, y, z));

                    worldBounds.Encapsulate(
                        sourceTransform.TransformPoint(
                            corner));
                }
            }
        }

        return worldBounds;
    }

    Vector3Int ToVoxel(Vector3 point)
    {
        float size =
            Mathf.Max(clusterVoxelSize, 0.001f);

        return new Vector3Int(
            Mathf.RoundToInt(point.x / size),
            Mathf.RoundToInt(point.y / size),
            Mathf.RoundToInt(point.z / size));
    }

    Vector3 GetBoundsOffset(GameObject target)
    {
        if (!alignTargetBoundsCenter)
            return Vector3.zero;

        Bounds bounds;

        if (!TryGetObjectBounds(target, out bounds))
            return Vector3.zero;

        Vector3 offset =
            bounds.center -
            target.transform.position;

        if (keepOriginalHeight)
        {
            offset.y = 0f;
        }

        return offset;
    }

    bool TryGetObjectBounds(
        GameObject target,
        out Bounds bounds)
    {
        Renderer[] renderers =
            target.GetComponentsInChildren<Renderer>(
                true);

        bool hasBounds = false;
        bounds = new Bounds();

        for (int i = 0;
             i < renderers.Length;
             i++)
        {
            if (!hasBounds)
            {
                bounds = renderers[i].bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(
                    renderers[i].bounds);
            }
        }

        if (hasBounds)
            return true;

        Collider[] colliders =
            target.GetComponentsInChildren<Collider>(
                true);

        for (int i = 0;
             i < colliders.Length;
             i++)
        {
            if (!colliders[i].enabled)
                continue;

            if (!hasBounds)
            {
                bounds = colliders[i].bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(
                    colliders[i].bounds);
            }
        }

        return hasBounds;
    }

    private struct VoxelData
    {
        public int pointCount;
        public Bounds bounds;

        public VoxelData(Vector3 point)
        {
            pointCount = 1;
            bounds = new Bounds(point, Vector3.zero);
        }

        public void Add(Vector3 point)
        {
            pointCount++;
            bounds.Encapsulate(point);
        }
    }

    void OnDrawGizmos()
    {
        if (!drawDebugGizmos ||
            !hasDebugClusterBounds)
        {
            return;
        }

        Gizmos.color =
            new Color(1f, 0.2f, 0.1f, 0.9f);

        Gizmos.DrawWireCube(
            debugClusterBoundsWorld.center,
            debugClusterBoundsWorld.size);

        Gizmos.DrawSphere(
            debugClusterBoundsWorld.center,
            0.08f);
    }
}
