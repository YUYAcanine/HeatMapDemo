using System.Collections.Generic;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;
using Button = UnityEngine.UI.Button;

[DefaultExecutionOrder(-250)]
public class PointNavLink : MonoBehaviour
{
    private const string GeneratedLinkPrefix = "AutoPointNavLink_";

    [Header("Build")]
    [SerializeField] private Button generateLinksButton;

    [Header("Link Conditions")]
    [SerializeField] private float maxHorizontalDistance = 0.45f;
    [SerializeField] private float maxHeightDifference = 0.35f;
    [SerializeField] private float climbCostPerMeter = 8.0f;

    [SerializeField, HideInInspector] private string generatedRootName = "PointCloudNavMeshSource";
    [SerializeField, HideInInspector] private string generatedMeshName = "SurfaceNavMesh";
    [SerializeField, HideInInspector] private bool includeInactiveSources = false;
    [SerializeField, HideInInspector] private bool useBakedNavMeshIslands = true;
    [SerializeField, HideInInspector] private float minHorizontalDistance = 0.01f;
    [SerializeField, HideInInspector] private float linkWidth = 0.18f;
    [SerializeField, HideInInspector] private int costModifier = 2;
    [SerializeField, HideInInspector] private bool bidirectional = true;
    [SerializeField, HideInInspector] private float clusterCellSize = 0.08f;
    [SerializeField, HideInInspector] private float clusterHeightStep = 0.12f;
    [SerializeField, HideInInspector] private int minClusterCells = 1;
    [SerializeField, HideInInspector] private int minNavMeshIslandTriangles = 1;
    [SerializeField, HideInInspector] private float edgeSampleSpacing = 0.12f;
    [SerializeField, HideInInspector] private float navMeshSampleRadius = 0.25f;
    [SerializeField, HideInInspector] private bool generateOnStart = false;
    [SerializeField, HideInInspector] private bool clearOldLinksOnGenerate = true;
    [SerializeField, HideInInspector] private bool logGeneratedLinks = true;

    private readonly HashSet<string> generatedPairKeys = new();

    public int LastSourceCellCount { get; private set; }
    public int LastClusterCount { get; private set; }
    public int LastGeneratedLinkCount { get; private set; }

    private void Start()
    {
        if (generateOnStart)
            GenerateLinks();
    }

    private void OnEnable()
    {
        if (generateLinksButton == null)
            return;

        generateLinksButton.onClick.RemoveListener(GenerateLinks);
        generateLinksButton.onClick.AddListener(GenerateLinks);
    }

    private void OnDisable()
    {
        if (generateLinksButton != null)
            generateLinksButton.onClick.RemoveListener(GenerateLinks);
    }

    public void GenerateLinks()
    {
        if (clearOldLinksOnGenerate)
            ClearGeneratedLinks();

        generatedPairKeys.Clear();

        List<SurfaceCluster> clusters;
        int sourceCount;

        if (useBakedNavMeshIslands)
        {
            clusters =
                BuildBakedNavMeshIslandClusters(out sourceCount);
        }
        else
        {
            List<SurfaceCell> cells =
                CollectSurfaceCells();
            clusters =
                BuildClusters(cells);
            sourceCount = cells.Count;
        }

        int generatedCount =
            0;

        for (int i = 0; i < clusters.Count; i++)
        {
            for (int j = i + 1; j < clusters.Count; j++)
            {
                if (TryCreateLink(clusters[i], clusters[j]))
                    generatedCount++;
            }
        }

        LastSourceCellCount = sourceCount;
        LastClusterCount = clusters.Count;
        LastGeneratedLinkCount = generatedCount;

        if (logGeneratedLinks)
        {
            Debug.Log(
                $"PointNavLink: generation finished. mode={(useBakedNavMeshIslands ? "BakedNavMeshIslands" : "SourceMeshCells")}, sourceItems={LastSourceCellCount}, clusters={LastClusterCount}, generatedLinks={LastGeneratedLinkCount}, maxHorizontalDistance={maxHorizontalDistance:F3}, maxHeightDifference={maxHeightDifference:F3}, sampleRadius={navMeshSampleRadius:F3}");
        }
    }

    private List<SurfaceCluster> BuildBakedNavMeshIslandClusters(
        out int triangleCount)
    {
        NavMeshTriangulation triangulation =
            NavMesh.CalculateTriangulation();
        Vector3[] vertices =
            triangulation.vertices;
        int[] indices =
            triangulation.indices;

        triangleCount =
            indices.Length / 3;

        Dictionary<int, List<int>> trianglesByVertex =
            new Dictionary<int, List<int>>();

        for (int triangleIndex = 0; triangleIndex < triangleCount; triangleIndex++)
        {
            for (int corner = 0; corner < 3; corner++)
            {
                int vertexIndex =
                    indices[triangleIndex * 3 + corner];

                if (!trianglesByVertex.TryGetValue(vertexIndex, out List<int> triangles))
                {
                    triangles = new List<int>();
                    trianglesByVertex.Add(vertexIndex, triangles);
                }

                triangles.Add(triangleIndex);
            }
        }

        bool[] visited =
            new bool[triangleCount];
        List<SurfaceCluster> clusters =
            new List<SurfaceCluster>();

        for (int i = 0; i < triangleCount; i++)
        {
            if (visited[i])
                continue;

            SurfaceCluster cluster =
                CollectNavMeshIslandCluster(vertices, indices, trianglesByVertex, visited, i);

            if (cluster.CellCount >= Mathf.Max(minNavMeshIslandTriangles, 1))
                clusters.Add(cluster);
        }

        return clusters;
    }

    private SurfaceCluster CollectNavMeshIslandCluster(
        Vector3[] vertices,
        int[] indices,
        Dictionary<int, List<int>> trianglesByVertex,
        bool[] visited,
        int startTriangleIndex)
    {
        Queue<int> queue =
            new Queue<int>();
        Bounds firstBounds =
            GetTriangleBounds(vertices, indices, startTriangleIndex);
        SurfaceCluster cluster =
            new SurfaceCluster(firstBounds, 1);

        visited[startTriangleIndex] = true;
        queue.Enqueue(startTriangleIndex);

        while (queue.Count > 0)
        {
            int currentTriangleIndex =
                queue.Dequeue();
            Vector3 currentCenter =
                GetTriangleCenter(vertices, indices, currentTriangleIndex);

            for (int corner = 0; corner < 3; corner++)
            {
                int vertexIndex =
                    indices[currentTriangleIndex * 3 + corner];

                if (!trianglesByVertex.TryGetValue(vertexIndex, out List<int> neighborTriangles))
                    continue;

                foreach (int neighborTriangleIndex in neighborTriangles)
                {
                    if (visited[neighborTriangleIndex])
                        continue;

                    Vector3 neighborCenter =
                        GetTriangleCenter(vertices, indices, neighborTriangleIndex);

                    if (Mathf.Abs(neighborCenter.y - currentCenter.y) > clusterHeightStep)
                        continue;

                    visited[neighborTriangleIndex] = true;
                    queue.Enqueue(neighborTriangleIndex);
                    cluster.Add(GetTriangleBounds(vertices, indices, neighborTriangleIndex));
                }
            }
        }

        return cluster;
    }

    private Bounds GetTriangleBounds(
        Vector3[] vertices,
        int[] indices,
        int triangleIndex)
    {
        Bounds bounds =
            new Bounds(
                vertices[indices[triangleIndex * 3]],
                Vector3.zero);

        bounds.Encapsulate(vertices[indices[triangleIndex * 3 + 1]]);
        bounds.Encapsulate(vertices[indices[triangleIndex * 3 + 2]]);
        return bounds;
    }

    private Vector3 GetTriangleCenter(
        Vector3[] vertices,
        int[] indices,
        int triangleIndex)
    {
        return
            (vertices[indices[triangleIndex * 3]] +
             vertices[indices[triangleIndex * 3 + 1]] +
             vertices[indices[triangleIndex * 3 + 2]]) / 3f;
    }

    private List<SurfaceCell> CollectSurfaceCells()
    {
        List<SurfaceCell> cells =
            new List<SurfaceCell>();
        MeshFilter[] meshFilters =
            FindObjectsOfType<MeshFilter>(includeInactiveSources);

        foreach (MeshFilter meshFilter in meshFilters)
        {
            if (meshFilter == null ||
                meshFilter.sharedMesh == null ||
                meshFilter.name != generatedMeshName ||
                !HasParentNamed(meshFilter.transform, generatedRootName))
            {
                continue;
            }

            AddMeshCells(meshFilter, cells);
        }

        return cells;
    }

    private void AddMeshCells(
        MeshFilter meshFilter,
        List<SurfaceCell> cells)
    {
        Mesh mesh =
            meshFilter.sharedMesh;
        Vector3[] vertices =
            mesh.vertices;
        int[] triangles =
            mesh.triangles;
        Transform meshTransform =
            meshFilter.transform;

        for (int i = 0; i + 5 < triangles.Length; i += 6)
        {
            Bounds bounds =
                new Bounds(
                    meshTransform.TransformPoint(vertices[triangles[i]]),
                    Vector3.zero);

            for (int j = 1; j < 6; j++)
            {
                bounds.Encapsulate(
                    meshTransform.TransformPoint(vertices[triangles[i + j]]));
            }

            Vector3 center =
                bounds.center;
            cells.Add(
                new SurfaceCell(
                    center,
                    bounds,
                    Quantize(center.x, clusterCellSize),
                    Quantize(center.z, clusterCellSize)));
        }
    }

    private List<SurfaceCluster> BuildClusters(
        List<SurfaceCell> cells)
    {
        Dictionary<Vector2Int, List<int>> cellsByKey =
            new Dictionary<Vector2Int, List<int>>();

        for (int i = 0; i < cells.Count; i++)
        {
            Vector2Int key =
                cells[i].Key;

            if (!cellsByKey.TryGetValue(key, out List<int> indices))
            {
                indices = new List<int>();
                cellsByKey.Add(key, indices);
            }

            indices.Add(i);
        }

        bool[] visited =
            new bool[cells.Count];
        List<SurfaceCluster> clusters =
            new List<SurfaceCluster>();

        for (int i = 0; i < cells.Count; i++)
        {
            if (visited[i])
                continue;

            SurfaceCluster cluster =
                CollectCluster(cells, cellsByKey, visited, i);

            if (cluster.CellCount >= Mathf.Max(minClusterCells, 1))
                clusters.Add(cluster);
        }

        return clusters;
    }

    private SurfaceCluster CollectCluster(
        List<SurfaceCell> cells,
        Dictionary<Vector2Int, List<int>> cellsByKey,
        bool[] visited,
        int startIndex)
    {
        Queue<int> queue =
            new Queue<int>();
        SurfaceCluster cluster =
            new SurfaceCluster(cells[startIndex]);

        visited[startIndex] = true;
        queue.Enqueue(startIndex);

        while (queue.Count > 0)
        {
            int currentIndex =
                queue.Dequeue();
            SurfaceCell current =
                cells[currentIndex];

            foreach (int neighborIndex in GetNeighborCellIndices(cellsByKey, current))
            {
                if (visited[neighborIndex])
                    continue;

                SurfaceCell neighbor =
                    cells[neighborIndex];

                if (Mathf.Abs(neighbor.Center.y - current.Center.y) > clusterHeightStep)
                    continue;

                visited[neighborIndex] = true;
                queue.Enqueue(neighborIndex);
                cluster.Add(neighbor);
            }
        }

        return cluster;
    }

    private IEnumerable<int> GetNeighborCellIndices(
        Dictionary<Vector2Int, List<int>> cellsByKey,
        SurfaceCell cell)
    {
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dz = -1; dz <= 1; dz++)
            {
                Vector2Int key =
                    new Vector2Int(
                        cell.GridX + dx,
                        cell.GridZ + dz);

                if (!cellsByKey.TryGetValue(key, out List<int> indices))
                    continue;

                foreach (int index in indices)
                    yield return index;
            }
        }
    }

    private bool TryCreateLink(
        SurfaceCluster clusterA,
        SurfaceCluster clusterB)
    {
        string pairKey =
            GetPairKey(clusterA, clusterB);

        if (generatedPairKeys.Contains(pairKey))
            return false;

        LinkCandidate candidate =
            FindBestLinkCandidate(clusterA.Bounds, clusterB.Bounds);
        float heightDifference =
            Mathf.Abs(candidate.PointA.y - candidate.PointB.y);
        float horizontalDistance =
            HorizontalDistance(candidate.PointA, candidate.PointB);

        if (heightDifference > maxHeightDifference ||
            horizontalDistance < minHorizontalDistance ||
            horizontalDistance > maxHorizontalDistance)
        {
            return false;
        }

        if (!TrySampleNavMesh(candidate.PointA, out Vector3 start) ||
            !TrySampleNavMesh(candidate.PointB, out Vector3 end))
        {
            return false;
        }

        heightDifference =
            Mathf.Abs(start.y - end.y);
        horizontalDistance =
            HorizontalDistance(start, end);

        if (heightDifference > maxHeightDifference ||
            horizontalDistance < minHorizontalDistance ||
            horizontalDistance > maxHorizontalDistance)
        {
            return false;
        }

        int createdLinkObjects =
            CreateLinkObjects(pairKey, start, end);
        generatedPairKeys.Add(pairKey);

        if (logGeneratedLinks)
        {
            Debug.Log(
                $"PointNavLink: generated link {pairKey}. start={start}, end={end}, height={heightDifference:F3}, distance={horizontalDistance:F3}, linkObjects={createdLinkObjects}, climbCostPerMeter={climbCostPerMeter:F2}");
        }

        return true;
    }

    private LinkCandidate FindBestLinkCandidate(
        Bounds boundsA,
        Bounds boundsB)
    {
        LinkCandidate best =
            new LinkCandidate(boundsA.center, boundsB.center);
        float bestDistance =
            float.PositiveInfinity;

        foreach (Vector3 pointA in GetEdgeCandidates(boundsA))
        {
            Vector3 pointB =
                GetClosestSurfacePoint(boundsB, pointA);
            float distance =
                HorizontalDistance(pointA, pointB);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = new LinkCandidate(pointA, pointB);
            }
        }

        foreach (Vector3 pointB in GetEdgeCandidates(boundsB))
        {
            Vector3 pointA =
                GetClosestSurfacePoint(boundsA, pointB);
            float distance =
                HorizontalDistance(pointA, pointB);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = new LinkCandidate(pointA, pointB);
            }
        }

        return best;
    }

    private List<Vector3> GetEdgeCandidates(
        Bounds bounds)
    {
        List<Vector3> points =
            new List<Vector3>();
        float spacing =
            Mathf.Max(edgeSampleSpacing, 0.01f);
        float y =
            bounds.max.y;

        AddEdgeSamples(points, bounds.min.x, bounds.max.x, bounds.min.z, bounds.min.z, y, spacing);
        AddEdgeSamples(points, bounds.min.x, bounds.max.x, bounds.max.z, bounds.max.z, y, spacing);
        AddEdgeSamples(points, bounds.min.x, bounds.min.x, bounds.min.z, bounds.max.z, y, spacing);
        AddEdgeSamples(points, bounds.max.x, bounds.max.x, bounds.min.z, bounds.max.z, y, spacing);

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
        float spacing)
    {
        Vector2 start =
            new Vector2(startX, startZ);
        Vector2 end =
            new Vector2(endX, endZ);
        float length =
            Vector2.Distance(start, end);
        int sampleCount =
            Mathf.Max(1, Mathf.CeilToInt(length / spacing));

        for (int i = 0; i <= sampleCount; i++)
        {
            float t =
                (float)i / sampleCount;
            points.Add(
                new Vector3(
                    Mathf.Lerp(startX, endX, t),
                    y,
                    Mathf.Lerp(startZ, endZ, t)));
        }
    }

    private Vector3 GetClosestSurfacePoint(
        Bounds bounds,
        Vector3 target)
    {
        return new Vector3(
            Mathf.Clamp(target.x, bounds.min.x, bounds.max.x),
            bounds.max.y,
            Mathf.Clamp(target.z, bounds.min.z, bounds.max.z));
    }

    private bool TrySampleNavMesh(
        Vector3 point,
        out Vector3 sampledPoint)
    {
        if (NavMesh.SamplePosition(
            point,
            out NavMeshHit hit,
            Mathf.Max(navMeshSampleRadius, 0.01f),
            NavMesh.AllAreas))
        {
            sampledPoint = hit.position;
            return true;
        }

        sampledPoint = point;
        return false;
    }

    private int CreateLinkObjects(
        string pairKey,
        Vector3 start,
        Vector3 end)
    {
        float heightDifference =
            Mathf.Abs(start.y - end.y);

        if (heightDifference <= 0.001f)
        {
            CreateLinkObject($"{pairKey}_flat", start, end, costModifier, true);
            return 1;
        }

        Vector3 lower =
            start.y <= end.y
                ? start
                : end;
        Vector3 higher =
            start.y <= end.y
                ? end
                : start;
        int uphillCost =
            GetLinkCostModifier(heightDifference);

        CreateLinkObject($"{pairKey}_up", lower, higher, uphillCost, false);
        CreateLinkObject($"{pairKey}_down", higher, lower, costModifier, false);
        return 2;
    }

    private int GetLinkCostModifier(
        float upwardHeight)
    {
        return Mathf.Max(
            1,
            costModifier + Mathf.CeilToInt(Mathf.Max(upwardHeight, 0f) * Mathf.Max(climbCostPerMeter, 0f)));
    }

    private void CreateLinkObject(
        string linkKey,
        Vector3 start,
        Vector3 end,
        int linkCostModifier,
        bool isBidirectional)
    {
        GameObject linkObject =
            new GameObject($"{GeneratedLinkPrefix}{linkKey}");
        linkObject.transform.SetParent(transform, false);
        linkObject.transform.position = start;

        NavMeshLink link =
            linkObject.AddComponent<NavMeshLink>();
        link.bidirectional = isBidirectional && bidirectional;
        link.width = Mathf.Max(linkWidth, 0.01f);
        link.costModifier = Mathf.Max(linkCostModifier, 1);
        link.autoUpdate = true;
        link.startPoint = Vector3.zero;
        link.endPoint =
            linkObject.transform.InverseTransformPoint(end);

        if (logGeneratedLinks)
        {
            Debug.Log(
                $"PointNavLink: link object created. object={linkObject.name}, start={start}, end={end}, upwardHeight={Mathf.Max(end.y - start.y, 0f):F3}, costModifier={link.costModifier}, bidirectional={link.bidirectional}");
        }
    }

    public void ClearGeneratedLinks()
    {
        List<GameObject> childrenToDestroy =
            new List<GameObject>();

        for (int i = 0; i < transform.childCount; i++)
        {
            Transform child =
                transform.GetChild(i);

            if (!child.name.StartsWith(GeneratedLinkPrefix) ||
                child.GetComponent<NavMeshLink>() == null)
            {
                continue;
            }

            childrenToDestroy.Add(child.gameObject);
        }

        foreach (GameObject child in childrenToDestroy)
        {
            DestroyImmediate(child);
        }

        LastSourceCellCount = 0;
        LastClusterCount = 0;
        LastGeneratedLinkCount = 0;

        if (logGeneratedLinks && childrenToDestroy.Count > 0)
            Debug.Log($"PointNavLink: cleared generated links. count={childrenToDestroy.Count}");
    }

    private bool HasParentNamed(
        Transform target,
        string parentName)
    {
        Transform current =
            target;

        while (current != null)
        {
            if (current.name == parentName)
                return true;

            current = current.parent;
        }

        return false;
    }

    private string GetPairKey(
        SurfaceCluster a,
        SurfaceCluster b)
    {
        return a.Id < b.Id
            ? $"{a.Id}_{b.Id}"
            : $"{b.Id}_{a.Id}";
    }

    private float HorizontalDistance(
        Vector3 a,
        Vector3 b)
    {
        return Vector2.Distance(
            new Vector2(a.x, a.z),
            new Vector2(b.x, b.z));
    }

    private int Quantize(
        float value,
        float size)
    {
        return Mathf.RoundToInt(value / Mathf.Max(size, 0.001f));
    }

    private readonly struct SurfaceCell
    {
        public readonly Vector3 Center;
        public readonly Bounds Bounds;
        public readonly int GridX;
        public readonly int GridZ;

        public Vector2Int Key =>
            new Vector2Int(GridX, GridZ);

        public SurfaceCell(
            Vector3 center,
            Bounds bounds,
            int gridX,
            int gridZ)
        {
            Center = center;
            Bounds = bounds;
            GridX = gridX;
            GridZ = gridZ;
        }
    }

    private struct SurfaceCluster
    {
        private static int nextId;

        public readonly int Id;
        public Bounds Bounds;
        public int CellCount;

        public SurfaceCluster(
            SurfaceCell firstCell)
        {
            Id = ++nextId;
            Bounds = firstCell.Bounds;
            CellCount = 1;
        }

        public SurfaceCluster(
            Bounds firstBounds,
            int firstCellCount)
        {
            Id = ++nextId;
            Bounds = firstBounds;
            CellCount = Mathf.Max(firstCellCount, 1);
        }

        public void Add(
            SurfaceCell cell)
        {
            Bounds.Encapsulate(cell.Bounds);
            CellCount++;
        }

        public void Add(
            Bounds bounds)
        {
            Bounds.Encapsulate(bounds);
            CellCount++;
        }
    }

    private readonly struct LinkCandidate
    {
        public readonly Vector3 PointA;
        public readonly Vector3 PointB;

        public LinkCandidate(
            Vector3 pointA,
            Vector3 pointB)
        {
            PointA = pointA;
            PointB = pointB;
        }
    }
}
