using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

public class PCDLoaderAll : MonoBehaviour
{
    [Header("PCD Folder")]
    public string folderName = "KinectPCD";

    [Header("Point Size")]
    public float pointScale = 0.01f;

    private string folderPath;

    void Start()
    {
        folderPath = Path.Combine(
            Application.dataPath,
            "3DObject",
            folderName);

        if (!Directory.Exists(folderPath))
        {
            Debug.LogError(
                "フォルダが存在しません : " + folderPath);
            return;
        }

        string[] pcdFiles =
            Directory.GetFiles(folderPath, "*.pcd");

        Debug.Log($"PCD Files : {pcdFiles.Length}");

        foreach (string file in pcdFiles)
        {
            LoadPCD(file);
        }
    }

    void LoadPCD(string path)
    {
        List<Vector3> vertices =
            new List<Vector3>();

        List<Color32> colors =
            new List<Color32>();

        string[] lines =
            File.ReadAllLines(path);

        bool dataStart = false;

        foreach (string line in lines)
        {
            // DATA ascii の次から点データ
            if (line.StartsWith("DATA"))
            {
                dataStart = true;
                continue;
            }

            if (!dataStart)
                continue;

            if (string.IsNullOrWhiteSpace(line))
                continue;

            string[] sp =
                line.Split(' ');

            if (sp.Length < 4)
                continue;

            float x = float.Parse(
                sp[0],
                CultureInfo.InvariantCulture);

            float y = float.Parse(
                sp[1],
                CultureInfo.InvariantCulture);

            float z = float.Parse(
                sp[2],
                CultureInfo.InvariantCulture);

            uint rgb = uint.Parse(sp[3]);

            byte r =
                (byte)((rgb >> 16) & 0xFF);

            byte g =
                (byte)((rgb >> 8) & 0xFF);

            byte b =
                (byte)(rgb & 0xFF);

            // 元の座標 그대로
            vertices.Add(
                new Vector3(x, y, z));

            colors.Add(
                new Color32(r, g, b, 255));
        }

        CreatePointCloudObject(
            Path.GetFileNameWithoutExtension(path),
            vertices,
            colors);

        Debug.Log(
            $"Loaded : {Path.GetFileName(path)}  Points={vertices.Count}");
    }

    void CreatePointCloudObject(
        string objName,
        List<Vector3> pts,
        List<Color32> cols)
    {
        GameObject go =
            new GameObject(objName);

        go.transform.parent =
            transform;

        MeshFilter mf =
            go.AddComponent<MeshFilter>();

        MeshRenderer mr =
            go.AddComponent<MeshRenderer>();

        Mesh mesh =
            new Mesh();

        mesh.indexFormat =
            UnityEngine.Rendering.IndexFormat.UInt32;

        mesh.vertices =
            pts.ToArray();

        mesh.colors32 =
            cols.ToArray();

        int[] indices =
            new int[pts.Count];

        for (int i = 0; i < indices.Length; i++)
        {
            indices[i] = i;
        }

        mesh.SetIndices(
            indices,
            MeshTopology.Points,
            0);

        mf.mesh = mesh;

        Material mat =
            new Material(
                Shader.Find("Sprites/Default"));

        mr.material = mat;

        // 点サイズ調整
        go.transform.localScale =
            Vector3.one * pointScale;
    }
}