using System;
using System.Collections.Generic;
using UnityEngine;

public class EarClipping3D
{
    public static int[] Triangulate(Vector3[] vertices, Vector3 normal)
    {
        if (vertices.Length < 3)
        {
            throw new ArgumentException("ポリゴンの頂点数は3以上である必要があります。");
        }

        // 順序を確認して反時計回りに揃える
        List<int> hull = FindConvexHull(vertices);

        // 法線ベクトルに基づいて順序を修正
        EnsureCorrectOrientation(vertices, hull, normal);

        List<int> triangles = new List<int>();
        List<int> indices = new List<int>(hull);

        int n = indices.Count;
        int count = 0;

        while (n > 3)
        {
            bool earFound = false;
            for (int i = 0; i < n; i++)
            {
                int prev = (i == 0) ? n - 1 : i - 1;
                int next = (i == n - 1) ? 0 : i + 1;

                if (IsEar(vertices, indices, prev, i, next))
                {
                    triangles.Add(indices[prev]);
                    triangles.Add(indices[i]);
                    triangles.Add(indices[next]);
                    indices.RemoveAt(i);
                    n--;
                    earFound = true;
                    break;
                }
            }

            if (!earFound)
            {
                // 耳が見つからなかった場合
                throw new InvalidOperationException("ポリゴンの三角形分割に失敗しました。入力データが正しいか確認してください。");
            }

            // 無限ループ防止
            if (++count > 3 * vertices.Length)
            {
                throw new InvalidOperationException("無限ループが検出されました。ポリゴンが正しくない可能性があります。");
            }
        }

        // 最後の三角形を追加
        if (n == 3)
        {
            triangles.Add(indices[0]);
            triangles.Add(indices[1]);
            triangles.Add(indices[2]);
        }

        return triangles.ToArray();
    }

    private static void EnsureCorrectOrientation(Vector3[] vertices, List<int> hull, Vector3 normal)
    {
        // Hullの法線ベクトルを計算
        Vector3 hullNormal = Vector3.zero;
        for (int i = 0; i < hull.Count - 2; i++)
        {
            Vector3 v0 = vertices[hull[i]];
            Vector3 v1 = vertices[hull[i + 1]];
            Vector3 v2 = vertices[hull[i + 2]];
            hullNormal += Vector3.Cross(v1 - v0, v2 - v0);
        }

        // 向きをチェックして必要なら反転
        if (Vector3.Dot(normal, hullNormal) < 0)
        {
            hull.Reverse();
        }
    }

    public static Vector3 CalculateNormal(Vector3[] vertices)
    {
        if (vertices.Length != 3)
        {
            throw new ArgumentException("三角形インデックスは3つの頂点インデックスである必要があります。");
        }

        Vector3 A = vertices[0];
        Vector3 B = vertices[1];
        Vector3 C = vertices[2];

        // 三角形の法線ベクトルを計算
        return Vector3.Cross(B - A, C - A).normalized;
    }

    private static List<int> FindConvexHull(Vector3[] points)
    {
        if (points.Length < 3)
        {
            throw new ArgumentException("頂点の数が3未満です。");
        }

        List<Vector3> sortedPoints = new List<Vector3>(points);
        int[] indices = new int[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            indices[i] = i;
        }

        Array.Sort(indices, (a, b) => points[a].x == points[b].x ? points[a].y.CompareTo(points[b].y) : points[a].x.CompareTo(points[b].x));

        List<int> hull = new List<int>();

        // Lower hull
        for (int i = 0; i < indices.Length; i++)
        {
            while (hull.Count >= 2 && Cross(points[hull[hull.Count - 2]], points[hull[hull.Count - 1]], points[indices[i]]) <= 0)
            {
                hull.RemoveAt(hull.Count - 1);
            }
            hull.Add(indices[i]);
        }

        // Upper hull
        int t = hull.Count + 1;
        for (int i = indices.Length - 2; i >= 0; i--)
        {
            while (hull.Count >= t && Cross(points[hull[hull.Count - 2]], points[hull[hull.Count - 1]], points[indices[i]]) <= 0)
            {
                hull.RemoveAt(hull.Count - 1);
            }
            hull.Add(indices[i]);
        }

        hull.RemoveAt(hull.Count - 1);

        return hull;
    }

    private static float Cross(Vector3 O, Vector3 A, Vector3 B)
    {
        return (A.x - O.x) * (B.y - O.y) - (A.y - O.y) * (B.x - O.x);
    }

    private static bool IsEar(Vector3[] vertices, List<int> indices, int prev, int i, int next)
    {
        Vector3 A = vertices[indices[prev]];
        Vector3 B = vertices[indices[i]];
        Vector3 C = vertices[indices[next]];

        if (!IsConvex(A, B, C))
        {
            return false;
        }

        for (int j = 0; j < indices.Count; j++)
        {
            if (j == prev || j == i || j == next)
            {
                continue;
            }

            Vector3 P = vertices[indices[j]];
            if (IsPointInTriangle(P, A, B, C))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsConvex(Vector3 A, Vector3 B, Vector3 C)
    {
        Vector3 crossProduct = Vector3.Cross(B - A, C - B);
        return Vector3.Dot(crossProduct, Vector3.Cross(C - B, A - C)) >= 0;
    }

    private static bool IsPointInTriangle(Vector3 P, Vector3 A, Vector3 B, Vector3 C)
    {
        Vector3 u = B - A;
        Vector3 v = C - A;
        Vector3 w = P - A;

        Vector3 n = Vector3.Cross(u, v);

        float gamma = Vector3.Dot(Vector3.Cross(u, w), n) / Vector3.Dot(n, n);
        float beta = Vector3.Dot(Vector3.Cross(w, v), n) / Vector3.Dot(n, n);
        float alpha = 1 - gamma - beta;

        return alpha >= 0 && beta >= 0 && gamma >= 0;
    }
}
