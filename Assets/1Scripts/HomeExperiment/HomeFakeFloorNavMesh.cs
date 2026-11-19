using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

// シーン8(8RouteToTargets)用。シーン0で置いた補助の床(Env/FakeFloors.json)だけから NavMesh を作る。
// シーン7(平面検知 → NavPub)を動かさずに経路を試すための代わり。同じGameObjectの HomeEnvLoader の
// Load Fake Floors をオンにして床を再生成させ、その MeshFilter を NavMesh の材料にする。
// NavMesh の作り方(エージェントの大きさ・ボクセルの細かさ)は NavSub と同じ。
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
    [SerializeField] private bool showFakeFloors = true;
    [Tooltip("補助の床に MeshCollider を付ける。スタート(DefineStart)が人の真下の床の高さを探すのに使う。")]
    [SerializeField] private bool addColliders = true;

    private NavMeshDataInstance navMeshDataInstance;

    public int FloorCount { get; private set; }
    public bool IsBuilt { get; private set; }

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
        Debug.Log($"[HomeFakeFloorNavMesh] 補助の床 {sources.Count} 枚から NavMesh を作りました。");
    }

    private void Clear()
    {
        if (navMeshDataInstance.valid)
            navMeshDataInstance.Remove();

        IsBuilt = false;
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
