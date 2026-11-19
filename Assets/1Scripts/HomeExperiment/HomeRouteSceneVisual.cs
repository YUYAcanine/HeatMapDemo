using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;

// シーン8(8RouteToTargets)の見た目。歩行可能領域と経路を目立たせる。
//   - 部屋(Env/RoomObjects.json から再生成した家の点群メッシュ)を半透明にする
//   - 歩行可能領域(今ある NavMesh 全体)を色付きの面で表示する。NavMesh が作り直されたら表示も作り直す
// 同じGameObjectの HomeEnvLoader が Awake で部屋を再生成した後(Start)に反映する。
[RequireComponent(typeof(HomeEnvLoader))]
public class HomeRouteSceneVisual : MonoBehaviour
{
    [Header("Room")]
    [Tooltip("部屋オブジェクトを半透明にする。")]
    [SerializeField] private bool fadeRoom = true;
    [SerializeField, Range(0f, 1f)] private float roomAlpha = 0.6f;
    [Tooltip("対象物体(TargetObjects)も半透明にする。オフなら元の見た目のまま(部屋が透けて物体が見やすくなる)。")]
    [SerializeField] private bool fadeTargets = false;
    [SerializeField, Range(0f, 1f)] private float targetAlpha = 0.8f;

    [Header("Walkable Area")]
    [SerializeField] private bool showWalkableArea = true;
    [SerializeField] private Color walkableColor = new Color(0.2f, 0.95f, 0.55f, 0.35f);
    [Tooltip("床と重なってちらつかないよう、この高さ(m)だけ浮かせて描く。")]
    [SerializeField] private float walkableYOffset = 0.01f;
    [Tooltip("この秒数ごとに NavMesh が変わったか確かめ、変わっていれば描き直す(MQTT で NavMesh を受け取るとき用)。")]
    [SerializeField, Min(0.1f)] private float refreshInterval = 2f;

    private HomeEnvLoader env;
    private GameObject walkableObject;
    private Mesh walkableMesh;
    private Material walkableMaterial;
    private float nextRefreshTime;
    private int lastVertexCount = -1;
    private Vector3 lastFirstVertex;
    private Vector3 lastLastVertex;

    private void Awake()
    {
        env = GetComponent<HomeEnvLoader>();
    }

    private void Start()
    {
        if (fadeRoom)
        {
            foreach (GameObject room in env.RoomObjects)
                Fade(room, roomAlpha);
        }

        if (fadeTargets)
        {
            foreach (GameObject target in env.TargetObjects)
                Fade(target, targetAlpha);
        }
    }

    private void Update()
    {
        if (!showWalkableArea)
        {
            if (walkableObject != null)
                walkableObject.SetActive(false);
            return;
        }

        if (Time.unscaledTime < nextRefreshTime)
            return;

        nextRefreshTime = Time.unscaledTime + refreshInterval;
        RefreshWalkableArea();
    }

    // ------------------------------------------------------------
    // Room fade
    // ------------------------------------------------------------
    private static void Fade(GameObject root, float alpha)
    {
        if (root == null)
            return;

        foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>())
        {
            Material[] materials = renderer.materials;

            for (int i = 0; i < materials.Length; i++)
                materials[i] = CreateFadeMaterial(materials[i], alpha);

            renderer.materials = materials;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
        }
    }

    // Standard の Fade モードと同じ設定にする(テクスチャ・色はそのまま、アルファだけ下げる)。
    // Standard 以外のシェーダーは、テクスチャと色を引き継いだ Standard にしてから半透明にする。
    private static Material CreateFadeMaterial(Material source, float alpha)
    {
        Material material;

        if (source != null && source.shader != null && source.shader.name == "Standard")
        {
            material = source;
        }
        else
        {
            material = new Material(Shader.Find("Standard"));

            if (source != null)
            {
                if (source.HasProperty("_MainTex"))
                    material.mainTexture = source.mainTexture;

                if (source.HasProperty("_Color"))
                    material.color = source.color;
            }
        }

        Color color = material.color;
        color.a = alpha;
        material.color = color;

        material.SetFloat("_Mode", 2f);
        material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
        material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
        material.SetInt("_ZWrite", 0);
        material.DisableKeyword("_ALPHATEST_ON");
        material.EnableKeyword("_ALPHABLEND_ON");
        material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        material.renderQueue = (int)RenderQueue.Transparent;
        return material;
    }

    // ------------------------------------------------------------
    // Walkable area
    // ------------------------------------------------------------
    private void RefreshWalkableArea()
    {
        NavMeshTriangulation triangulation = NavMesh.CalculateTriangulation();
        Vector3[] vertices = triangulation.vertices;

        if (vertices.Length == 0)
        {
            if (walkableObject != null)
                walkableObject.SetActive(false);

            lastVertexCount = 0;
            return;
        }

        bool unchanged = vertices.Length == lastVertexCount &&
                         vertices[0] == lastFirstVertex &&
                         vertices[vertices.Length - 1] == lastLastVertex;

        if (unchanged && walkableObject != null)
        {
            walkableObject.SetActive(true);
            return;
        }

        lastVertexCount = vertices.Length;
        lastFirstVertex = vertices[0];
        lastLastVertex = vertices[vertices.Length - 1];

        for (int i = 0; i < vertices.Length; i++)
            vertices[i] += Vector3.up * walkableYOffset;

        if (walkableObject == null)
        {
            walkableObject = new GameObject("WalkableArea");
            walkableObject.transform.SetParent(transform, false);
            walkableMesh = new Mesh { name = "WalkableArea" };
            walkableObject.AddComponent<MeshFilter>().sharedMesh = walkableMesh;

            // 半透明の部屋より後・経路の線より前に描く
            walkableMaterial = new Material(Shader.Find("Sprites/Default"));
            walkableMaterial.renderQueue = (int)RenderQueue.Transparent + 10;
            MeshRenderer renderer = walkableObject.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = walkableMaterial;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        walkableMaterial.color = walkableColor;
        walkableMesh.Clear();
        walkableMesh.indexFormat = vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        walkableMesh.vertices = vertices;
        walkableMesh.triangles = triangulation.indices;
        walkableMesh.RecalculateBounds();
        walkableObject.SetActive(true);

        Debug.Log($"[HomeRouteSceneVisual] 歩行可能領域を描き直しました(三角形 {triangulation.indices.Length / 3})。");
    }
}
