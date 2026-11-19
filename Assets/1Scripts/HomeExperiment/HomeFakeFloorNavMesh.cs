using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

// シーン8(8RouteToTargets)用。シーン0で置いた補助の床(Env/FakeFloors.json)だけから NavMesh を作る。
// シーン7(平面検知 → NavPub)を動かさずに経路を試すための代わり。同じGameObjectの HomeEnvLoader の
// Load Fake Floors をオンにして床を再生成させ、その MeshFilter を NavMesh の材料にする。
// NavMesh の作り方(エージェントの大きさ・ボクセルの細かさ)は NavSub と同じ。
// 床どうし(机の上と床など)は、端が水平に近ければ高さの差に関係なく NavMeshLink でつなぐ
// (PointNavLink は高さの差に上限があるが、補助の床は人が置いたものなので全部つながる前提にする)。
// シーン7の NavMesh を MQTT で受け取るときは、このコンポーネントをオフにして NavSubscriber を表示にする
// (両方使うと NavMesh が重なる)。
[RequireComponent(typeof(HomeEnvLoader))]
public class HomeFakeFloorNavMesh : MonoBehaviour
{
    [Header("NavMesh Build")]
    [SerializeField] private int agentTypeId = 0;
    [SerializeField] private float agentRadius = 0.01f;
    [SerializeField] private float agentHeight = 0.05f;
    [Tooltip("この高さ(m)までの段差は乗り越えられるものとしてつなぐ。床どうしの高さが少しずれていてもつながるように。")]
    [SerializeField] private float agentClimb = 0.1f;
    [SerializeField] private float voxelSize = 0.03f;
    [SerializeField] private float minRegionArea = 0f;
    [SerializeField] private float boundsMargin = 2f;

    [Header("Fake Floors")]
    [Tooltip("補助の床を画面に表示する。オフにしても NavMesh には使う。")]
    [SerializeField] private bool showFakeFloors = false;
    [Tooltip("補助の床に MeshCollider を付ける。スタート(DefineStart)が人の真下の床の高さを探すのに使う(表示しなくても効く)。")]
    [SerializeField] private bool addColliders = true;

    [Header("NavMesh Links")]
    [Tooltip("補助の床どうしを NavMeshLink でつなぐ。PointNavLink と違い高さの差は制限しない(机の上と床など、どれだけ高さが違ってもつなぐ)。")]
    [SerializeField] private bool linkFakeFloors = true;
    [Tooltip("床の端どうしが水平にこの距離(m)以内なら NavMeshLink でつなぐ。離れた床どうしはつながない。")]
    [SerializeField] private float maxLinkHorizontalDistance = 0.3f;
    [Tooltip("床の端を探すときの点の間隔(m)。")]
    [SerializeField] private float linkSampleSpacing = 0.05f;
    [SerializeField] private float linkWidth = 0.18f;
    [Tooltip("リンクを通るときの基本のコスト(PointNavLink と同じ)。")]
    [SerializeField] private int linkCostModifier = 2;
    [Tooltip("上りのリンクで、高さ1mあたりに足すコスト(PointNavLink と同じ)。登るより回り道を選びやすくなる。")]
    [SerializeField] private float climbCostPerMeter = 8f;

    [Header("Link Visual")]
    [SerializeField] private bool showLinks = true;
    [SerializeField] private Color linkColor = new Color(0.3f, 0.8f, 1f, 0.9f);
    [SerializeField] private float linkLineWidth = 0.015f;

    private NavMeshDataInstance navMeshDataInstance;
    private readonly List<NavMeshLinkInstance> linkInstances = new List<NavMeshLinkInstance>();
    private readonly List<GameObject> linkVisuals = new List<GameObject>();
    private Material linkMaterial;

    public int FloorCount { get; private set; }
    public int LinkCount => linkInstances.Count;
    public bool IsBuilt { get; private set; }

    private struct FloorInfo
    {
        public string name;
        public Bounds bounds;
    }

    // HomeEnvLoader が Awake で補助の床を再生成した後に作る
    private void Start()
    {
        Build();
    }

    [ContextMenu("Rebuild NavMesh")]
    public void Build()
    {
        Clear();

        HomeEnvLoader env = GetComponent<HomeEnvLoader>();
        List<NavMeshBuildSource> sources = new List<NavMeshBuildSource>();
        List<FloorInfo> floors = new List<FloorInfo>();
        Bounds bounds = new Bounds();
        bool hasBounds = false;

        foreach (GameObject floor in env.FakeFloors)
        {
            if (floor == null)
                continue;

            foreach (MeshFilter meshFilter in floor.GetComponentsInChildren<MeshFilter>())
            {
                Mesh mesh = meshFilter.sharedMesh;

                if (mesh == null)
                    continue;

                sources.Add(new NavMeshBuildSource
                {
                    shape = NavMeshBuildSourceShape.Mesh,
                    sourceObject = mesh,
                    transform = meshFilter.transform.localToWorldMatrix,
                    area = 0
                });

                Bounds worldBounds = TransformBounds(mesh.bounds, meshFilter.transform.localToWorldMatrix);
                floors.Add(new FloorInfo { name = meshFilter.name, bounds = worldBounds });

                if (hasBounds)
                    bounds.Encapsulate(worldBounds);
                else
                    bounds = worldBounds;

                hasBounds = true;

                if (addColliders && meshFilter.GetComponent<Collider>() == null)
                    meshFilter.gameObject.AddComponent<MeshCollider>().sharedMesh = mesh;
            }

            foreach (MeshRenderer renderer in floor.GetComponentsInChildren<MeshRenderer>())
                renderer.enabled = showFakeFloors;
        }

        FloorCount = sources.Count;

        if (sources.Count == 0)
        {
            Debug.LogError("[HomeFakeFloorNavMesh] 補助の床がないため NavMesh を作れません。シーン0の FakeFloors の下に床を置いて保存し、" +
                           "HomeEnvLoader の Load Fake Floors がオンになっているか確認してください。");
            return;
        }

        NavMeshBuildSettings settings = NavMesh.GetSettingsByID(agentTypeId);
        settings.agentRadius = Mathf.Max(agentRadius, 0.001f);
        settings.agentHeight = Mathf.Max(agentHeight, 0.001f);
        settings.agentClimb = Mathf.Max(agentClimb, 0f);
        settings.agentSlope = 45f;
        settings.overrideVoxelSize = true;
        settings.voxelSize = Mathf.Max(voxelSize, 0.005f);
        settings.overrideTileSize = true;
        settings.tileSize = 64;
        settings.minRegionArea = Mathf.Max(minRegionArea, 0f);

        bounds.Expand(Vector3.one * boundsMargin);

        NavMeshData data = NavMeshBuilder.BuildNavMeshData(settings, sources, bounds, Vector3.zero, Quaternion.identity);

        if (data == null)
        {
            Debug.LogError("[HomeFakeFloorNavMesh] NavMesh を作れませんでした。");
            return;
        }

        navMeshDataInstance = NavMesh.AddNavMeshData(data);
        IsBuilt = true;

        if (linkFakeFloors)
            BuildLinks(floors);

        Debug.Log($"[HomeFakeFloorNavMesh] 補助の床 {sources.Count} 枚から NavMesh を作りました(NavMeshLink {linkInstances.Count} 本)。");
    }

    private void Clear()
    {
        if (navMeshDataInstance.valid)
            navMeshDataInstance.Remove();

        foreach (NavMeshLinkInstance link in linkInstances)
        {
            if (link.valid)
                NavMesh.RemoveLink(link);
        }

        linkInstances.Clear();

        foreach (GameObject visual in linkVisuals)
        {
            if (visual != null)
                Destroy(visual);
        }

        linkVisuals.Clear();
        IsBuilt = false;
    }

    // ------------------------------------------------------------
    // NavMesh Links
    // ------------------------------------------------------------
    // 床1枚ぶんの、NavMesh が乗っている点の格子(床の上面の高さで linkSampleSpacing ごとに調べる)
    private class FloorGrid
    {
        public string name;
        public float top;
        public float minX;
        public float minZ;
        public int countX;
        public int countZ;
        public bool[,] valid;
        public Vector3[,] points;
        public readonly List<Vector2Int> edges = new List<Vector2Int>();

        public bool IsValid(int x, int z) => x >= 0 && z >= 0 && x < countX && z < countZ && valid[x, z];

        public bool Covers(Vector3 position, float spacing)
        {
            int x = Mathf.RoundToInt((position.x - minX) / spacing);
            int z = Mathf.RoundToInt((position.z - minZ) / spacing);
            return IsValid(x, z);
        }
    }

    // 床どうしの端が水平に近い組ごとに、一番近い1か所を NavMeshLink でつなぐ。高さの差は問わない。
    private void BuildLinks(List<FloorInfo> floors)
    {
        float spacing = Mathf.Max(linkSampleSpacing, 0.01f);
        List<FloorGrid> grids = new List<FloorGrid>();

        foreach (FloorInfo floor in floors)
            grids.Add(CreateGrid(floor, spacing));

        for (int i = 0; i < grids.Count; i++)
        {
            for (int j = i + 1; j < grids.Count; j++)
            {
                bool found = FindClosest(grids[i], grids[j], spacing, out Vector3 a, out Vector3 b, out float distance);

                if (FindClosest(grids[j], grids[i], spacing, out Vector3 a2, out Vector3 b2, out float distance2) &&
                    (!found || distance2 < distance))
                {
                    found = true;
                    a = a2;
                    b = b2;
                    distance = distance2;
                }

                if (!found)
                    continue;

                float height = Mathf.Abs(a.y - b.y);

                // 同じ高さで端が接している床は、NavMesh そのものでつながっている
                if (height <= agentClimb && distance <= spacing * 1.5f)
                    continue;

                AddLinks(a, b);
                Debug.Log($"[HomeFakeFloorNavMesh] NavMeshLink: {grids[i].name} ⇔ {grids[j].name} (水平 {distance:F2} m, 高さの差 {height:F2} m)");
            }
        }
    }

    private FloorGrid CreateGrid(FloorInfo floor, float spacing)
    {
        Bounds bounds = floor.bounds;
        FloorGrid grid = new FloorGrid
        {
            name = floor.name,
            top = bounds.max.y,
            minX = bounds.min.x,
            minZ = bounds.min.z,
            countX = Mathf.Max(1, Mathf.FloorToInt(bounds.size.x / spacing) + 1),
            countZ = Mathf.Max(1, Mathf.FloorToInt(bounds.size.z / spacing) + 1)
        };

        grid.valid = new bool[grid.countX, grid.countZ];
        grid.points = new Vector3[grid.countX, grid.countZ];
        float heightTolerance = Mathf.Max(0.05f, voxelSize * 2f);

        for (int x = 0; x < grid.countX; x++)
        {
            for (int z = 0; z < grid.countZ; z++)
            {
                Vector3 probe = new Vector3(grid.minX + x * spacing, grid.top, grid.minZ + z * spacing);

                if (!NavMesh.SamplePosition(probe, out NavMeshHit hit, spacing, NavMesh.AllAreas))
                    continue;

                Vector2 horizontal = new Vector2(hit.position.x - probe.x, hit.position.z - probe.z);

                if (Mathf.Abs(hit.position.y - grid.top) > heightTolerance || horizontal.magnitude > spacing * 0.5f)
                    continue;

                grid.valid[x, z] = true;
                grid.points[x, z] = hit.position;
            }
        }

        // 4近傍のどこかに NavMesh が無い点が床の端
        for (int x = 0; x < grid.countX; x++)
        {
            for (int z = 0; z < grid.countZ; z++)
            {
                if (grid.valid[x, z] &&
                    (!grid.IsValid(x - 1, z) || !grid.IsValid(x + 1, z) || !grid.IsValid(x, z - 1) || !grid.IsValid(x, z + 1)))
                    grid.edges.Add(new Vector2Int(x, z));
            }
        }

        return grid;
    }

    // from の端の点から、to の NavMesh 上の点で水平に一番近いものを探す。
    // from の真上/真下にある to の点(机の下の床など)は選ばず、from の外側の点にする。
    private bool FindClosest(FloorGrid from, FloorGrid to, float spacing, out Vector3 bestFrom, out Vector3 bestTo, out float bestDistance)
    {
        bestFrom = bestTo = Vector3.zero;
        bestDistance = float.MaxValue;
        bool found = false;
        int range = Mathf.CeilToInt(maxLinkHorizontalDistance / spacing) + 1;

        foreach (Vector2Int edge in from.edges)
        {
            Vector3 a = from.points[edge.x, edge.y];
            int centerX = Mathf.RoundToInt((a.x - to.minX) / spacing);
            int centerZ = Mathf.RoundToInt((a.z - to.minZ) / spacing);

            for (int x = centerX - range; x <= centerX + range; x++)
            {
                for (int z = centerZ - range; z <= centerZ + range; z++)
                {
                    if (!to.IsValid(x, z))
                        continue;

                    Vector3 b = to.points[x, z];

                    if (Mathf.Abs(b.y - a.y) > agentClimb && from.Covers(b, spacing))
                        continue;

                    float distance = new Vector2(b.x - a.x, b.z - a.z).magnitude;

                    if (distance > maxLinkHorizontalDistance || distance >= bestDistance)
                        continue;

                    bestDistance = distance;
                    bestFrom = a;
                    bestTo = b;
                    found = true;
                }
            }
        }

        return found;
    }

    // PointNavLink と同じく、ほぼ同じ高さなら双方向1本、高さが違うなら下り(基本コスト)と上り(高さに応じて高いコスト)の2本
    private void AddLinks(Vector3 a, Vector3 b)
    {
        float height = Mathf.Abs(a.y - b.y);

        if (height <= agentClimb)
        {
            AddLink(a, b, linkCostModifier, true);
        }
        else
        {
            Vector3 higher = a.y > b.y ? a : b;
            Vector3 lower = a.y > b.y ? b : a;
            AddLink(higher, lower, linkCostModifier, false);
            AddLink(lower, higher, linkCostModifier + Mathf.CeilToInt(height * Mathf.Max(climbCostPerMeter, 0f)), false);
        }

        if (showLinks)
            CreateLinkVisual(a, b);
    }

    private void AddLink(Vector3 start, Vector3 end, int cost, bool bidirectional)
    {
        NavMeshLinkInstance instance = NavMesh.AddLink(new NavMeshLinkData
        {
            startPosition = start,
            endPosition = end,
            width = linkWidth,
            costModifier = Mathf.Max(cost, 1),
            bidirectional = bidirectional,
            area = 0,
            agentTypeID = agentTypeId
        });

        if (instance.valid)
            linkInstances.Add(instance);
        else
            Debug.LogWarning($"[HomeFakeFloorNavMesh] NavMeshLink を追加できませんでした: {start} → {end}");
    }

    private void CreateLinkVisual(Vector3 a, Vector3 b)
    {
        if (linkMaterial == null)
            linkMaterial = new Material(Shader.Find("Sprites/Default"));

        LineRenderer line = new GameObject("FakeFloorLink").AddComponent<LineRenderer>();
        line.transform.SetParent(transform, false);
        line.useWorldSpace = true;
        line.sharedMaterial = linkMaterial;
        line.startColor = line.endColor = linkColor;
        line.startWidth = line.endWidth = linkLineWidth;
        line.positionCount = 2;
        line.SetPositions(new[] { a + Vector3.up * 0.01f, b + Vector3.up * 0.01f });
        linkVisuals.Add(line.gameObject);
    }

    private void OnDestroy()
    {
        Clear();
    }

    private static Bounds TransformBounds(Bounds local, Matrix4x4 matrix)
    {
        Bounds result = new Bounds(matrix.MultiplyPoint3x4(local.center), Vector3.zero);
        Vector3 extents = local.extents;

        for (int i = 0; i < 8; i++)
        {
            Vector3 corner = local.center + new Vector3(
                (i & 1) == 0 ? -extents.x : extents.x,
                (i & 2) == 0 ? -extents.y : extents.y,
                (i & 4) == 0 ? -extents.z : extents.z);
            result.Encapsulate(matrix.MultiplyPoint3x4(corner));
        }

        return result;
    }
}
