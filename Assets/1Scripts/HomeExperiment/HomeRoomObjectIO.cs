using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
#endif

// 部屋オブジェクト(メッシュを持つ表示中のオブジェクト)を Env フォルダへ保存/Envフォルダから再生成する。
//
// 保存の単位:
//   - モデル(.obj/.fbx)やPrefabのインスタンス … アセット参照(GUID/パス) + ワールドTransform
//   - それ以外のメッシュ(プリミティブの箱など)  … メッシュ自体を Env/Meshes に書き出し + ワールドTransform
// アセット参照での保存/再生成には AssetDatabase を使うため、Unityエディタ上での実行を前提とする。
public static class HomeRoomObjectIO
{
    // ------------------------------------------------------------
    // Save
    // ------------------------------------------------------------
#if UNITY_EDITOR
    // roots 配下の「表示中でメッシュを持つ」オブジェクトを保存する。保存した個数を返す。
    public static int Save(string experimentName, IEnumerable<Transform> roots)
    {
        string meshDir = HomeExperimentPaths.GetMeshDirectory(experimentName);

        // 前回保存したメッシュが残らないように作り直す
        if (Directory.Exists(meshDir))
            Directory.Delete(meshDir, true);

        HomeRoomObjectList list = new HomeRoomObjectList
        {
            experimentName = experimentName,
            savedAt = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        };

        foreach (Transform root in roots)
            Collect(root, list, meshDir);

        string path = HomeExperimentPaths.GetRoomObjectsPath(experimentName);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, JsonUtility.ToJson(list, true));

        foreach (HomeRoomObjectData data in list.objects)
        {
            string source = data.sourceType == HomeRoomObjectData.SourceAsset ? data.assetPath : $"Meshes/{data.meshFile}";
            Debug.Log($"[HomeRoomObjectIO]   - {data.name} ({source}) pos={data.position} rot={data.rotation.eulerAngles} scale={data.scale}");
        }

        Debug.Log($"[HomeRoomObjectIO] 部屋オブジェクトを {list.objects.Count} 個保存しました: {path}");
        return list.objects.Count;
    }

    private static void Collect(Transform node, HomeRoomObjectList list, string meshDir)
    {
        GameObject go = node.gameObject;

        if (!go.activeInHierarchy)
            return;

        // キネクト本体(点群含む)とUIは部屋オブジェクトではない
        if (node.GetComponentInParent<HomeKinect>() != null ||
            node.GetComponentInParent<Canvas>() != null)
            return;

        if (PrefabUtility.IsOutermostPrefabInstanceRoot(go))
        {
            GameObject source = PrefabUtility.GetCorrespondingObjectFromOriginalSource(go);
            string assetPath = source != null ? AssetDatabase.GetAssetPath(source) : null;

            if (!string.IsNullOrEmpty(assetPath) && assetPath.StartsWith("Assets/"))
            {
                if (HasVisibleMesh(node))
                {
                    list.objects.Add(new HomeRoomObjectData
                    {
                        name = go.name,
                        sourceType = HomeRoomObjectData.SourceAsset,
                        assetGuid = AssetDatabase.AssetPathToGUID(assetPath),
                        assetPath = assetPath,
                        position = node.position,
                        rotation = node.rotation,
                        scale = node.lossyScale
                    });
                }

                // インスタンスの中身はアセットから再生成されるので子は見ない
                return;
            }
        }

        MeshFilter meshFilter = node.GetComponent<MeshFilter>();
        MeshRenderer meshRenderer = node.GetComponent<MeshRenderer>();

        if (meshFilter != null && meshFilter.sharedMesh != null &&
            meshRenderer != null && meshRenderer.enabled)
        {
            Mesh mesh = meshFilter.sharedMesh;

            HomeRoomObjectData data = new HomeRoomObjectData
            {
                name = go.name,
                sourceType = HomeRoomObjectData.SourceMesh,
                position = node.position,
                rotation = node.rotation,
                scale = node.lossyScale
            };

            if (TryGetAssetReference(mesh, out string meshGuid, out long meshLocalId, out string meshPath))
            {
                // モデル(.obj/.fbx)内のメッシュなどアセットになっているものは参照だけ保存する。
                // (Read/Write が無効なモデルは頂点を読めないため、書き出しはしない)
                data.meshAssetGuid = meshGuid;
                data.meshLocalId = meshLocalId;
                data.meshAssetPath = meshPath;
            }
            else if (mesh.isReadable)
            {
                data.meshFile = $"{list.objects.Count:D3}_{MakeSafeFileName(go.name)}.json";
                Directory.CreateDirectory(meshDir);
                File.WriteAllText(Path.Combine(meshDir, data.meshFile), JsonUtility.ToJson(ToMeshData(mesh)));
            }
            else
            {
                Debug.LogWarning($"[HomeRoomObjectIO] {go.name}: メッシュがアセットでなく、頂点も読めないため保存しません。");
                data = null;
            }

            if (data != null)
            {
                foreach (Material material in meshRenderer.sharedMaterials)
                    data.materials.Add(ToMaterialData(material));

                list.objects.Add(data);
            }
        }

        foreach (Transform child in node)
            Collect(child, list, meshDir);
    }

    private static bool HasVisibleMesh(Transform node)
    {
        foreach (MeshRenderer renderer in node.GetComponentsInChildren<MeshRenderer>())
        {
            MeshFilter meshFilter = renderer.GetComponent<MeshFilter>();

            if (renderer.enabled && meshFilter != null && meshFilter.sharedMesh != null)
                return true;
        }

        foreach (SkinnedMeshRenderer renderer in node.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            if (renderer.enabled && renderer.sharedMesh != null)
                return true;
        }

        return false;
    }

    private static HomeMeshData ToMeshData(Mesh mesh)
    {
        HomeMeshData data = new HomeMeshData
        {
            vertices = mesh.vertices,
            normals = mesh.normals,
            uv = mesh.uv,
            colors = mesh.colors
        };

        for (int i = 0; i < mesh.subMeshCount; i++)
        {
            data.subMeshes.Add(new HomeSubMeshData
            {
                topology = (int)mesh.GetTopology(i),
                indices = mesh.GetIndices(i)
            });
        }

        return data;
    }

    private static HomeMaterialData ToMaterialData(Material material)
    {
        HomeMaterialData data = new HomeMaterialData();

        if (material == null)
            return data;

        if (material.HasProperty("_Color"))
            data.color = material.color;

        // Assets配下のマテリアルだけ参照で保存する(組み込みマテリアルは色だけ)
        if (TryGetAssetReference(material, out string guid, out long localId, out string path))
        {
            data.assetGuid = guid;
            data.localId = localId;
            data.assetPath = path;
        }

        return data;
    }

    // Assets配下のアセット(サブアセット含む)なら GUID / ローカルID / パスを返す
    private static bool TryGetAssetReference(Object asset, out string guid, out long localId, out string path)
    {
        path = AssetDatabase.GetAssetPath(asset);

        if (!string.IsNullOrEmpty(path) && path.StartsWith("Assets/") &&
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out guid, out localId))
            return true;

        guid = null;
        localId = 0;
        path = null;
        return false;
    }

    private static string MakeSafeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');

        return name;
    }
#endif

    // ------------------------------------------------------------
    // Load
    // ------------------------------------------------------------
    // Env/RoomObjects.json を読み込み、parent の子として部屋オブジェクトを再生成する。
    // ファイルが無い場合は null を返す。
    public static List<GameObject> Load(string experimentName, Transform parent)
    {
        string path = HomeExperimentPaths.GetRoomObjectsPath(experimentName);

        if (!File.Exists(path))
        {
            Debug.LogError($"[HomeRoomObjectIO] 部屋オブジェクトのファイルがありません: {path}");
            return null;
        }

        HomeRoomObjectList list = JsonUtility.FromJson<HomeRoomObjectList>(File.ReadAllText(path));
        List<GameObject> created = new List<GameObject>();
        string meshDir = HomeExperimentPaths.GetMeshDirectory(experimentName);

        foreach (HomeRoomObjectData data in list.objects)
        {
            GameObject go = data.sourceType == HomeRoomObjectData.SourceMesh
                ? CreateFromMesh(data, meshDir)
                : CreateFromAsset(data);

            if (go == null)
                continue;

            go.name = data.name;
            go.transform.SetParent(parent, false);
            go.transform.SetPositionAndRotation(data.position, data.rotation);
            go.transform.localScale = DivideScale(data.scale, parent != null ? parent.lossyScale : Vector3.one);
            go.SetActive(true);

            created.Add(go);
        }

        Debug.Log($"[HomeRoomObjectIO] 部屋オブジェクトを {created.Count}/{list.objects.Count} 個再生成しました: {path}");
        return created;
    }

    private static GameObject CreateFromAsset(HomeRoomObjectData data)
    {
#if UNITY_EDITOR
        // アセットが移動していてもGUIDで追えるようにGUIDを優先する
        string assetPath = AssetDatabase.GUIDToAssetPath(data.assetGuid);

        if (string.IsNullOrEmpty(assetPath))
            assetPath = data.assetPath;

        GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);

        if (asset == null)
        {
            Debug.LogError($"[HomeRoomObjectIO] {data.name}: アセットが見つかりません (guid={data.assetGuid}, path={data.assetPath})");
            return null;
        }

        return Object.Instantiate(asset);
#else
        Debug.LogError($"[HomeRoomObjectIO] {data.name}: アセット参照の部屋オブジェクトはUnityエディタ上でのみ再生成できます。");
        return null;
#endif
    }

    private static GameObject CreateFromMesh(HomeRoomObjectData data, string meshDir)
    {
        Mesh mesh = !string.IsNullOrEmpty(data.meshAssetGuid)
            ? LoadSubAsset<Mesh>(data.meshAssetGuid, data.meshAssetPath, data.meshLocalId)
            : LoadMeshFile(Path.Combine(meshDir, data.meshFile ?? ""), data.name);

        if (mesh == null)
        {
            Debug.LogError($"[HomeRoomObjectIO] {data.name}: メッシュを読み込めません (asset={data.meshAssetPath}, file={data.meshFile})");
            return null;
        }

        GameObject go = new GameObject(data.name);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;

        Material[] materials = new Material[Mathf.Max(data.materials.Count, 1)];

        for (int i = 0; i < materials.Length; i++)
            materials[i] = i < data.materials.Count ? LoadMaterial(data.materials[i]) : CreateColorMaterial(Color.white);

        go.AddComponent<MeshRenderer>().sharedMaterials = materials;
        return go;
    }

    private static Mesh LoadMeshFile(string meshPath, string name)
    {
        if (!File.Exists(meshPath))
            return null;

        HomeMeshData meshData = JsonUtility.FromJson<HomeMeshData>(File.ReadAllText(meshPath));

        if (meshData.vertices == null || meshData.vertices.Length == 0)
            return null;

        Mesh mesh = new Mesh { name = name };

        if (meshData.vertices.Length > 65535)
            mesh.indexFormat = IndexFormat.UInt32;

        mesh.vertices = meshData.vertices;

        if (meshData.normals != null && meshData.normals.Length == meshData.vertices.Length)
            mesh.normals = meshData.normals;

        if (meshData.uv != null && meshData.uv.Length == meshData.vertices.Length)
            mesh.uv = meshData.uv;

        if (meshData.colors != null && meshData.colors.Length == meshData.vertices.Length)
            mesh.colors = meshData.colors;

        mesh.subMeshCount = meshData.subMeshes.Count;

        for (int i = 0; i < meshData.subMeshes.Count; i++)
            mesh.SetIndices(meshData.subMeshes[i].indices, (MeshTopology)meshData.subMeshes[i].topology, i);

        if (meshData.normals == null || meshData.normals.Length != meshData.vertices.Length)
            mesh.RecalculateNormals();

        mesh.RecalculateBounds();
        return mesh;
    }

    private static Material LoadMaterial(HomeMaterialData data)
    {
        Material material = LoadSubAsset<Material>(data.assetGuid, data.assetPath, data.localId);
        return material != null ? material : CreateColorMaterial(data.color);
    }

    // GUID(移動していても追える)またはパスのアセットから、ローカルIDが一致するサブアセットを取り出す。
    // ローカルIDが無い(古い形式の)場合は最初に見つかった型の一致するアセットを返す。
    private static T LoadSubAsset<T>(string guid, string path, long localId) where T : Object
    {
#if UNITY_EDITOR
        if (string.IsNullOrEmpty(guid) && string.IsNullOrEmpty(path))
            return null;

        string assetPath = string.IsNullOrEmpty(guid) ? null : AssetDatabase.GUIDToAssetPath(guid);

        if (string.IsNullOrEmpty(assetPath))
            assetPath = path;

        if (string.IsNullOrEmpty(assetPath))
            return null;

        if (localId == 0)
            return AssetDatabase.LoadAssetAtPath<T>(assetPath);

        foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(assetPath))
        {
            if (asset is T typed &&
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out _, out long id) &&
                id == localId)
                return typed;
        }
#endif
        return null;
    }

    private static Material CreateColorMaterial(Color color)
    {
        return new Material(Shader.Find("Standard")) { color = color };
    }

    private static Vector3 DivideScale(Vector3 scale, Vector3 parentScale)
    {
        return new Vector3(
            parentScale.x != 0f ? scale.x / parentScale.x : scale.x,
            parentScale.y != 0f ? scale.y / parentScale.y : scale.y,
            parentScale.z != 0f ? scale.z / parentScale.z : scale.z);
    }
}
