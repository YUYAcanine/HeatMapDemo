using System.Collections.Generic;
using UnityEngine;

// 対象物体の表面(メッシュの三角形)と視線のコーンの判定。シーン4(HomeGazeAnalyzer)とシーン5(HomeGazeExporter)で使う。
// コーンの軸(視線)と表面との最小角度を求める。軸が表面を貫けば 0 度、外れていれば表面のうち軸に最も近い点までの角度。
// (頂点だけで調べると、円柱のように頂点が端にしかないメッシュを近くから見たとき、真ん中を見ていても外れてしまう)
// 遮蔽(間に物があって見えない)は考えない。
public class HomeConeSurface
{
    private const float CellSize = 0.25f;

    // 角度が同じとみなす差(度)。視線が2つの物を貫くとどちらも 0 度になるので、そのときは近い方を選ぶ
    public const float TieAngleDeg = 0.001f;

    private class Cell
    {
        public Vector3 center;
        public float radius;
        // positions の番号を三角形ごとに 3 つずつ
        public int[] triangles;
    }

    // ワールド座標の頂点(対象物体は動かないので、最初に一度だけ計算する)
    public readonly Vector3[] positions;
    private readonly List<Cell> cells = new List<Cell>();
    private readonly Vector3 center;
    private readonly float radius;

    public HomeConeSurface(Transform transform, Vector3[] localVertices, int[] triangles)
    {
        positions = new Vector3[localVertices.Length];

        for (int i = 0; i < localVertices.Length; i++)
            positions[i] = transform.TransformPoint(localVertices[i]);

        // 三角形の無いメッシュ(点や線だけ)は、頂点を大きさ 0 の三角形として扱う(頂点だけの判定と同じになる)
        if (triangles == null || triangles.Length < 3)
        {
            triangles = new int[positions.Length * 3];

            for (int i = 0; i < positions.Length; i++)
                triangles[3 * i] = triangles[3 * i + 1] = triangles[3 * i + 2] = i;
        }

        // 三角形を重心で格子に分け、コーンと交わらない格子をまとめて飛ばす
        Dictionary<Vector3Int, List<int>> grid = new Dictionary<Vector3Int, List<int>>();

        for (int k = 0; k + 2 < triangles.Length; k += 3)
        {
            Vector3 centroid = (positions[triangles[k]] + positions[triangles[k + 1]] + positions[triangles[k + 2]]) / 3f;
            Vector3Int key = Vector3Int.FloorToInt(centroid / CellSize);

            if (!grid.TryGetValue(key, out List<int> indices))
            {
                indices = new List<int>();
                grid[key] = indices;
            }

            indices.Add(triangles[k]);
            indices.Add(triangles[k + 1]);
            indices.Add(triangles[k + 2]);
        }

        foreach (List<int> indices in grid.Values)
        {
            Bounds bounds = new Bounds(positions[indices[0]], Vector3.zero);

            foreach (int i in indices)
                bounds.Encapsulate(positions[i]);

            cells.Add(new Cell { center = bounds.center, radius = bounds.extents.magnitude, triangles = indices.ToArray() });
        }

        Bounds all = new Bounds(positions.Length > 0 ? positions[0] : Vector3.zero, Vector3.zero);

        foreach (Vector3 position in positions)
            all.Encapsulate(position);

        center = all.center;
        radius = all.extents.magnitude;
    }

    // 始点 origin・向き direction(単位ベクトル)・半角 coneAngleRad・長さ coneDistance のコーンに表面が入れば true。
    // minAngleRad はコーン軸と表面との最小角度、hitDistance はその点までの距離(軸が貫くときは手前の面まで)。
    // 最も軸に近い点が coneDistance より遠いときは入っていないとする
    public bool MinConeAngle(Vector3 origin, Vector3 direction, float coneAngleRad, float coneDistance,
                             out float minAngleRad, out float hitDistance)
    {
        minAngleRad = float.MaxValue;
        hitDistance = float.MaxValue;

        if (!MayIntersect(center, radius, origin, direction, coneAngleRad, coneDistance))
            return false;

        foreach (Cell cell in cells)
        {
            if (!MayIntersect(cell.center, cell.radius, origin, direction, coneAngleRad, coneDistance))
                continue;

            int[] tri = cell.triangles;

            for (int k = 0; k + 2 < tri.Length; k += 3)
            {
                TriangleAngle(origin, direction, positions[tri[k]], positions[tri[k + 1]], positions[tri[k + 2]],
                              out float angle, out float distance);

                if (distance > 0f && distance <= coneDistance)
                    Keep(angle, distance, ref minAngleRad, ref hitDistance);
            }
        }

        return minAngleRad <= coneAngleRad;
    }

    // 角度(度)が bestAngle より小さければ true。同じ(差が TieAngleDeg 以下)なら近い方を優先する
    public static bool IsBetter(float angleDeg, float distance, float bestAngleDeg, float bestDistance)
    {
        if (angleDeg < bestAngleDeg - TieAngleDeg)
            return true;

        return angleDeg <= bestAngleDeg + TieAngleDeg && distance < bestDistance;
    }

    // 三角形 abc とコーン軸との最小角度と、その点までの距離
    private static void TriangleAngle(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c,
                                      out float angle, out float distance)
    {
        // 軸が三角形を貫くか(Möller–Trumbore)
        Vector3 e1 = b - a;
        Vector3 e2 = c - a;
        Vector3 h = Vector3.Cross(direction, e2);
        float det = Vector3.Dot(e1, h);

        if (Mathf.Abs(det) > 1e-12f)
        {
            float f = 1f / det;
            Vector3 s = origin - a;
            float u = f * Vector3.Dot(s, h);

            if (u >= 0f && u <= 1f)
            {
                Vector3 q = Vector3.Cross(s, e1);
                float v = f * Vector3.Dot(direction, q);

                if (v >= 0f && u + v <= 1f)
                {
                    float t = f * Vector3.Dot(e2, q);

                    if (t > 0f)
                    {
                        angle = 0f;
                        distance = t;
                        return;
                    }
                }
            }
        }

        // 貫かなければ、最も軸に近い点は三角形の辺の上にある
        angle = float.MaxValue;
        distance = float.MaxValue;
        EdgeAngle(origin, direction, a, b, ref angle, ref distance);
        EdgeAngle(origin, direction, b, c, ref angle, ref distance);
        EdgeAngle(origin, direction, c, a, ref angle, ref distance);
    }

    // 線分 ab のうち、コーン軸との角度が最も小さい点
    private static void EdgeAngle(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, ref float angle, ref float distance)
    {
        Vector3 p = a - origin;
        Vector3 q = b - origin;
        KeepPoint(direction, p, ref angle, ref distance);
        KeepPoint(direction, q, ref angle, ref distance);

        Vector3 n = Vector3.Cross(p, q);
        float nn = n.sqrMagnitude;

        // 始点と線分が一直線に並ぶときは両端だけ
        if (nn <= 1e-12f * p.sqrMagnitude * q.sqrMagnitude)
            return;

        // 軸を「始点と線分を含む平面」に投影した向き u。u が p と q の間にあれば、線分の途中に最も近い点がある
        Vector3 u = direction - Vector3.Dot(direction, n) / nn * n;

        if (Vector3.Dot(Vector3.Cross(p, u), n) < 0f || Vector3.Dot(Vector3.Cross(u, q), n) < 0f)
            return;

        float denominator = Vector3.Dot(Vector3.Cross(q - p, u), n);

        if (Mathf.Abs(denominator) < 1e-20f)
            return;

        float s = -Vector3.Dot(Vector3.Cross(p, u), n) / denominator;
        KeepPoint(direction, p + Mathf.Clamp01(s) * (q - p), ref angle, ref distance);
    }

    private static void KeepPoint(Vector3 direction, Vector3 toPoint, ref float angle, ref float distance)
    {
        // Acos は 0 度付近で誤差が大きいので Atan2 で求める
        float a = Mathf.Atan2(Vector3.Cross(direction, toPoint).magnitude, Vector3.Dot(direction, toPoint));
        Keep(a, toPoint.magnitude, ref angle, ref distance);
    }

    private static void Keep(float a, float d, ref float angle, ref float distance)
    {
        if (a < angle - 1e-6f || (a <= angle + 1e-6f && d < distance))
        {
            angle = a;
            distance = d;
        }
    }

    // 中心 sphereCenter・半径 sphereRadius の球がコーンと交わる可能性があるか(交わらないときだけ確実に false)
    private static bool MayIntersect(Vector3 sphereCenter, float sphereRadius, Vector3 origin, Vector3 direction,
                                     float coneAngleRad, float coneDistance)
    {
        Vector3 toCenter = sphereCenter - origin;
        float distance = toCenter.magnitude;

        if (distance <= sphereRadius)
            return true;

        if (distance - sphereRadius > coneDistance)
            return false;

        float angle = Mathf.Acos(Mathf.Clamp(Vector3.Dot(direction, toCenter / distance), -1f, 1f));
        float spread = Mathf.Asin(Mathf.Clamp01(sphereRadius / distance));

        return angle <= coneAngleRad + spread;
    }
}
