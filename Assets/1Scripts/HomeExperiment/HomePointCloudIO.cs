using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

// シーン0で撮った点群(Env/Kinect<ID>_PointCloud.ply)の読み書き。
// 形式は float x, y, z だけを持つ binary_little_endian の PLY(CloudCompare / MeshLab で開ける)。
// 座標はキネクト(HomeKinect)のローカル座標(メートル, Unityの軸)。
public static class HomePointCloudIO
{
    public static void Save(string path, IList<Vector3> points, string comment)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));

        using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (BinaryWriter writer = new BinaryWriter(stream))
        {
            string header =
                "ply\n" +
                "format binary_little_endian 1.0\n" +
                (string.IsNullOrEmpty(comment) ? "" : $"comment {comment}\n") +
                $"element vertex {points.Count}\n" +
                "property float x\n" +
                "property float y\n" +
                "property float z\n" +
                "end_header\n";

            writer.Write(Encoding.ASCII.GetBytes(header));

            foreach (Vector3 p in points)
            {
                writer.Write(p.x);
                writer.Write(p.y);
                writer.Write(p.z);
            }
        }
    }

    // Save で書いた形式(頂点の property がすべて float で、先頭3つが x, y, z)だけを読む。
    public static bool TryLoad(string path, out List<Vector3> points)
    {
        points = null;

        if (!File.Exists(path))
            return false;

        try
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read))
            using (BinaryReader reader = new BinaryReader(stream))
            {
                int vertexCount = -1;
                int propertyCount = 0;
                bool inVertexElement = false;

                if (ReadHeaderLine(reader) != "ply")
                    throw new InvalidDataException("PLYファイルではありません。");

                while (true)
                {
                    string line = ReadHeaderLine(reader);

                    if (line == null)
                        throw new InvalidDataException("end_header がありません。");

                    if (line == "end_header")
                        break;

                    string[] tokens = line.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);

                    if (tokens.Length == 0)
                        continue;

                    if (tokens[0] == "format" && (tokens.Length < 2 || tokens[1] != "binary_little_endian"))
                        throw new InvalidDataException($"対応していない形式です: {line}");

                    if (tokens[0] == "element")
                    {
                        inVertexElement = tokens.Length >= 3 && tokens[1] == "vertex";

                        if (inVertexElement)
                            vertexCount = int.Parse(tokens[2]);
                    }
                    else if (tokens[0] == "property" && inVertexElement)
                    {
                        if (tokens.Length < 3 || tokens[1] != "float")
                            throw new InvalidDataException($"float以外の property には対応していません: {line}");

                        propertyCount++;
                    }
                }

                if (vertexCount < 0 || propertyCount < 3)
                    throw new InvalidDataException("vertex(x, y, z)の定義がありません。");

                points = new List<Vector3>(vertexCount);

                for (int i = 0; i < vertexCount; i++)
                {
                    Vector3 p = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

                    for (int k = 3; k < propertyCount; k++)
                        reader.ReadSingle();

                    points.Add(p);
                }
            }

            return true;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[HomePointCloudIO] 点群の読み込みに失敗しました: {path}\n{e}");
            points = null;
            return false;
        }
    }

    // KinectPointCloudOnce と同じ、点(MeshTopology.Points)だけのメッシュを作る。
    public static Mesh CreatePointMesh(List<Vector3> points, string name)
    {
        Mesh mesh = new Mesh { name = name };
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.SetVertices(points);

        int[] indices = new int[points.Count];
        for (int i = 0; i < indices.Length; i++) indices[i] = i;

        mesh.SetIndices(indices, MeshTopology.Points, 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    private static string ReadHeaderLine(BinaryReader reader)
    {
        StringBuilder builder = new StringBuilder();

        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            char c = (char)reader.ReadByte();

            if (c == '\n')
                return builder.ToString().TrimEnd('\r');

            builder.Append(c);
        }

        return builder.Length > 0 ? builder.ToString() : null;
    }
}
