using System.Collections.Generic;
using UnityEngine;

namespace MeshDeletionTool
{
    // 平面上の単純多角形（凹形状も可）を耳切り法で三角形分割するクラス
    // EarClipping3D は XY 平面に投影した凸包で頂点を並べ替えるため、凹多角形の凹みを埋めてしまい、
    // XZ / YZ 平面に近い多角形では投影が潰れて三角形を返さないことがあった。
    // 本クラスは基準法線から多角形自身の2次元基底を作って投影し、基準法線に対して反時計回りになる巻き順で三角形を返す。
    public static class EarClipping2D
    {
        // 頂点は外周順（時計回りでも反時計回りでも可）。戻り値は polygon のローカルインデックスを3つずつ並べた配列
        public static int[] Triangulate(IList<Vector3> polygon, Vector3 referenceNormal)
        {
            return Triangulate(polygon, referenceNormal, out _);
        }

        // 耳が見つからず扇状分割に切り替えた場合は usedFallback が true になる
        // 頂点数が3未満、または面積が0（全頂点が一直線上）の場合は空配列を返す
        public static int[] Triangulate(IList<Vector3> polygon, Vector3 referenceNormal, out bool usedFallback)
        {
            usedFallback = false;
            if (polygon.Count < 3)
            {
                return new int[0];
            }
            Vector2[] points = ProjectToPlane(polygon, referenceNormal);
            return Triangulate(points, out usedFallback);
        }

        // 基準法線に垂直な2次元基底 (e1, e2)（e1 × e2 = 法線）に多角形を投影する
        public static Vector2[] ProjectToPlane(IList<Vector3> polygon, Vector3 referenceNormal)
        {
            // Vector3.normalized は長さ 1e-5 未満で零ベクトルを返すため（小さな三角形の外積で起こる）、明示的に割って正規化する
            Vector3 normal = Normalize(referenceNormal);
            if (normal == Vector3.zero)
            {
                normal = Normalize(NewellNormal(polygon));
            }

            // 基底 e1: 先頭の頂点から最も遠い頂点への方向（法線成分を除く）
            Vector3 origin = polygon[0];
            Vector3 e1 = Vector3.zero;
            float farthest = -1f;
            for (int i = 1; i < polygon.Count; i++)
            {
                Vector3 d = polygon[i] - origin;
                d -= Vector3.Dot(d, normal) * normal;
                if (d.sqrMagnitude > farthest)
                {
                    farthest = d.sqrMagnitude;
                    e1 = d;
                }
            }
            e1 = Normalize(e1);
            if (e1 == Vector3.zero)
            {
                e1 = Normalize(Vector3.Cross(normal, Mathf.Abs(normal.x) < 0.9f ? Vector3.right : Vector3.up));
            }
            Vector3 e2 = Vector3.Cross(normal, e1);

            Vector2[] points = new Vector2[polygon.Count];
            for (int i = 0; i < polygon.Count; i++)
            {
                Vector3 d = polygon[i] - origin;
                points[i] = new Vector2(Vector3.Dot(d, e1), Vector3.Dot(d, e2));
            }
            return points;
        }

        // 2次元の単純多角形を三角形分割する。戻り値の三角形は反時計回り（面積が正）
        public static int[] Triangulate(IList<Vector2> points, out bool usedFallback)
        {
            usedFallback = false;
            int n = points.Count;
            List<int> triangles = new List<int>();
            if (n < 3)
            {
                return triangles.ToArray();
            }

            // 許容誤差は多角形の大きさに対する相対値
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                minX = Mathf.Min(minX, points[i].x); maxX = Mathf.Max(maxX, points[i].x);
                minY = Mathf.Min(minY, points[i].y); maxY = Mathf.Max(maxY, points[i].y);
            }
            float size = Mathf.Max(maxX - minX, maxY - minY);
            if (size <= 0f)
            {
                return triangles.ToArray();
            }
            float epsilonArea = 1e-9f * size * size;      // これ以下の外積は「一直線上」とみなす
            float epsilonInside = 1e-7f * size * size;    // 耳の内部判定の許容誤差（辺上の点も内部とみなす）
            float epsilonCoincident = 1e-12f * size * size;  // これ以下の距離の二乗の2点は同一点とみなす

            // 外周を反時計回りに揃える
            List<int> indices = new List<int>(n);
            for (int i = 0; i < n; i++)
            {
                indices.Add(i);
            }
            float area = SignedArea(points, indices);
            if (Mathf.Abs(area) <= epsilonArea)
            {
                return triangles.ToArray();
            }
            if (area < 0f)
            {
                indices.Reverse();
                area = -area;
            }

            // 耳切り: 凸で内部（辺上を含む）に他の頂点を含まない耳のうち、最も形の良いものから切る
            // 切り取ると残りの多角形が潰れる耳は、他に耳が無い場合（残りが一直線上の頂点だけの場合）にのみ使う
            while (indices.Count > 3)
            {
                int m = indices.Count;
                int bestPosition = -1, bestDegeneratePosition = -1;
                float bestQuality = -1f, bestDegenerateQuality = -1f;
                for (int k = 0; k < m; k++)
                {
                    Vector2 a = points[indices[(k + m - 1) % m]];
                    Vector2 b = points[indices[k]];
                    Vector2 c = points[indices[(k + 1) % m]];
                    float cross = Cross(a, b, c);
                    if (cross <= 2f * epsilonArea)
                    {
                        continue;   // 凹頂点または一直線上の頂点は耳にならない
                    }
                    if (ContainsOtherVertex(points, indices, k, a, b, c, epsilonInside, epsilonCoincident))
                    {
                        continue;
                    }
                    // 形の良さ: 面積 / 辺の長さの二乗和（正三角形で最大）
                    float earArea = 0.5f * cross;
                    float quality = earArea / Mathf.Max((b - a).sqrMagnitude + (c - b).sqrMagnitude + (a - c).sqrMagnitude, 1e-30f);
                    if (area - earArea <= 2f * epsilonArea)
                    {
                        // 残りの多角形が潰れてしまう耳
                        if (quality > bestDegenerateQuality)
                        {
                            bestDegenerateQuality = quality;
                            bestDegeneratePosition = k;
                        }
                        continue;
                    }
                    if (quality > bestQuality)
                    {
                        bestQuality = quality;
                        bestPosition = k;
                    }
                }
                if (bestPosition < 0)
                {
                    bestPosition = bestDegeneratePosition;
                }

                if (bestPosition < 0)
                {
                    // 耳が見つからない（数値誤差や自己交差）: 扇状分割で残りを埋める
                    usedFallback = true;
                    for (int k = 1; k + 1 < m; k++)
                    {
                        AddTriangle(points, triangles, indices[0], indices[k], indices[k + 1], epsilonArea);
                    }
                    return triangles.ToArray();
                }

                int prev = indices[(bestPosition + m - 1) % m];
                int current = indices[bestPosition];
                int next = indices[(bestPosition + 1) % m];
                triangles.Add(prev); triangles.Add(current); triangles.Add(next);
                area -= 0.5f * Cross(points[prev], points[current], points[next]);
                indices.RemoveAt(bestPosition);
            }

            // 最後の三角形（面積0なら捨てる）
            AddTriangle(points, triangles, indices[0], indices[1], indices[2], epsilonArea);
            return triangles.ToArray();
        }

        // 面積が正の三角形だけを追加する
        private static void AddTriangle(IList<Vector2> points, List<int> triangles, int a, int b, int c, float epsilonArea)
        {
            if (Cross(points[a], points[b], points[c]) > epsilonArea)
            {
                triangles.Add(a); triangles.Add(b); triangles.Add(c);
            }
        }

        // 耳 (a, b, c) の内部または辺上に他の頂点があるか（耳の頂点と同一点の頂点は除く）
        // 辺上の頂点も含めるのは、凹頂点を通る対角線を切り出して残りの多角形を自己接触させないため
        private static bool ContainsOtherVertex(IList<Vector2> points, List<int> indices, int earPosition, Vector2 a, Vector2 b, Vector2 c,
                                                float epsilonInside, float epsilonCoincident)
        {
            int m = indices.Count;
            int prevPosition = (earPosition + m - 1) % m;
            int nextPosition = (earPosition + 1) % m;
            for (int j = 0; j < m; j++)
            {
                if (j == prevPosition || j == earPosition || j == nextPosition)
                {
                    continue;
                }
                Vector2 p = points[indices[j]];
                if ((p - a).sqrMagnitude <= epsilonCoincident || (p - b).sqrMagnitude <= epsilonCoincident || (p - c).sqrMagnitude <= epsilonCoincident)
                {
                    continue;
                }
                if (Cross(a, b, p) >= -epsilonInside && Cross(b, c, p) >= -epsilonInside && Cross(c, a, p) >= -epsilonInside)
                {
                    return true;
                }
            }
            return false;
        }

        // 多角形の符号付き面積（反時計回りで正）
        private static float SignedArea(IList<Vector2> points, List<int> indices)
        {
            float sum = 0f;
            for (int i = 0; i < indices.Count; i++)
            {
                Vector2 p = points[indices[i]];
                Vector2 q = points[indices[(i + 1) % indices.Count]];
                sum += p.x * q.y - q.x * p.y;
            }
            return 0.5f * sum;
        }

        // (b - a) × (c - a) の z 成分
        private static float Cross(Vector2 a, Vector2 b, Vector2 c)
        {
            return (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
        }

        // Newell の方法による多角形の法線
        private static Vector3 NewellNormal(IList<Vector3> polygon)
        {
            Vector3 normal = Vector3.zero;
            for (int i = 0; i < polygon.Count; i++)
            {
                Vector3 p = polygon[i];
                Vector3 q = polygon[(i + 1) % polygon.Count];
                normal += Vector3.Cross(p, q);
            }
            return normal;
        }

        // 長さで割って正規化する（零ベクトルはそのまま返す）
        private static Vector3 Normalize(Vector3 v)
        {
            float magnitude = Mathf.Sqrt(v.sqrMagnitude);
            return magnitude > 0f ? v / magnitude : Vector3.zero;
        }
    }
}
