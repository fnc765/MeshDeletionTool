using System;
using System.Collections.Generic;
using UnityEngine;

namespace MeshDeletionTool
{
    // 切断後のメッシュを、元の三角形ごとに再結合して三角形数を減らすクラス
    // 境界の細分化で分割された三角形は、切断後もそれぞれ独立した三角形のまま残る。本クラスは元の三角形ごとに
    // 生き残った出力三角形の和集合の外周を求め、外周上で一直線上に並ぶ頂点（細分化で生じた辺の中点など）を
    // 隣の三角形が必要としない限り取り除き、残った多角形を耳切り法で三角形分割し直す。
    // 頂点の追加・移動は行わない（参照されなくなった頂点はブレンドシェイプも含めて最後に詰める）ため、
    // 属性の再補間は不要で、隣接する元三角形との間に隙間（T字接合）も生じない。
    // さらに SimplifyToleranceTexels > 0 のとき、元三角形の内部だけを通る切り口の頂点列（隣の三角形と共有しない頂点）を
    // Douglas-Peucker 法でテクセル単位の許容誤差以内に間引く（頂点を取り除くだけで動かさない）。
    public class CutPolygonMerger
    {
        // 外周上の頂点を「一直線上」とみなす角度の許容値（sin）
        public float CollinearTolerance = 1e-4f;

        // 切り口の間引きの許容誤差（テクセル単位、0 で間引かない）
        public float SimplifyToleranceTexels = 0f;

        // 頂点が元の三角形の辺上にあるとみなす重心座標の許容値
        public float OnEdgeTolerance = 1e-4f;

        // 実行結果
        public int TriangleCountBefore;
        public int TriangleCountAfter;
        public int VertexCountBefore;
        public int VertexCountAfter;
        public int MergedGroupCount;               // 再結合した元三角形の数
        public int MultiPieceCount;                // 複数の離れた断片に分かれていた元三角形の数
        public int FallbackHoleCount;              // 穴のある形状のため再結合しなかった元三角形の数
        public int FallbackNonManifoldCount;       // 外周が一筆書きにならない（非多様体・頂点で接する）ため再結合しなかった元三角形の数
        public int FallbackTriangulationCount;     // 三角形分割に失敗したため再結合しなかった元三角形の数
        public int DroppedDegenerateLoopCount;     // 面積0になり捨てた外周の数
        public int RemovedFlatVertexCount;         // 取り除いた一直線上の頂点の数
        public int RemovedChainVertexCount;        // 間引いた切り口の頂点の数
        public int FallbackCount => FallbackHoleCount + FallbackNonManifoldCount + FallbackTriangulationCount;

        // 実行結果: サブメッシュ毎の、再結合後の三角形番号 → 元の三角形番号
        public List<int[]> ParentTriangleIndexPerSubMesh;

        // 実行結果（診断用）: 再結合後の頂点番号 → 入力（切断後）メッシュの頂点番号
        public int[] VertexSourceIndex;

        // 切断後のメッシュ cutMesh を再結合したメッシュを返す
        //   parentTriangleIndexPerSubMesh: サブメッシュ毎の、cutMesh の三角形番号 → originalMesh の三角形番号
        //   originalMesh: 細分化前の元のメッシュ（元の三角形の平面を得るために使う）
        //   protectedVertexCount: cutMesh の先頭からこの数の頂点は元のメッシュの頂点であり、決して取り除かない
        //   textureSizePerSubMesh: 切り口の間引きに使うサブメッシュ毎のテクスチャ解像度（null または 0 のサブメッシュは間引かない）
        public MeshArrays Merge(MeshArrays cutMesh, List<int[]> parentTriangleIndexPerSubMesh, MeshArrays originalMesh, int protectedVertexCount,
                                Vector2Int[] textureSizePerSubMesh = null)
        {
            int subMeshCount = cutMesh.SubMeshCount;
            int[][] triangles = new int[subMeshCount][];
            int[][] parents = new int[subMeshCount][];
            int[][] originalTriangles = new int[subMeshCount][];
            for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
            {
                triangles[subMeshIndex] = cutMesh.GetTriangles(subMeshIndex);
                parents[subMeshIndex] = parentTriangleIndexPerSubMesh[subMeshIndex];
                originalTriangles[subMeshIndex] = originalMesh.GetTriangles(subMeshIndex);
            }

            int[][] mergedTriangles = MergeTriangles(cutMesh.Vertices, cutMesh.UV, triangles, parents, originalMesh.Vertices, originalTriangles,
                                                     protectedVertexCount, textureSizePerSubMesh, out int[][] mergedParents);
            ParentTriangleIndexPerSubMesh = new List<int[]>(mergedParents);

            return CompactMesh(cutMesh, mergedTriangles);
        }

        // 再結合の本体（Mesh に依存しない）。サブメッシュ毎の新しい三角形配列を返す。頂点番号は入力のまま（詰めない）
        // uvs / textureSizes は切り口の間引きに使う（null なら間引かない）
        internal int[][] MergeTriangles(Vector3[] positions, Vector2[] uvs, int[][] triangles, int[][] parents,
                                        Vector3[] originalPositions, int[][] originalTriangles,
                                        int protectedVertexCount, Vector2Int[] textureSizes, out int[][] newParents)
        {
            ResetStatistics();
            TriangleCountBefore = 0;
            foreach (int[] t in triangles)
            {
                TriangleCountBefore += t.Length / 3;
            }

            // 1. 元の三角形ごとに出力三角形をまとめ、外周を求める
            List<Group> groups = new List<Group>();
            HashSet<int> wanted = new HashSet<int>();                                    // 取り除いてはいけない頂点
            Dictionary<int, List<Group>> groupsByVertex = new Dictionary<int, List<Group>>();   // 保護されていない頂点 → それを使う集まり
            for (int subMeshIndex = 0; subMeshIndex < triangles.Length; subMeshIndex++)
            {
                foreach (Group group in CollectGroups(subMeshIndex, triangles[subMeshIndex], parents[subMeshIndex]))
                {
                    groups.Add(group);
                    PrepareGroup(group, positions, originalPositions, originalTriangles[subMeshIndex], protectedVertexCount, wanted, groupsByVertex);
                }
            }
            // シーム（UVなどが異なる同じ座標の頂点）の両側で同じ判断になるよう、同じ座標の頂点は一方が必要なら他方も必要とする
            Dictionary<int, List<int>> twins = CollectPositionTwins(positions, groupsByVertex.Keys);
            foreach (int vertex in new List<int>(wanted))
            {
                AddTwins(vertex, twins, wanted);
            }

            // 切り口の間引きに使うテクセル座標（頂点UV × テクスチャ解像度）
            Vector2[] texelSizes = new Vector2[triangles.Length];
            for (int subMeshIndex = 0; subMeshIndex < triangles.Length; subMeshIndex++)
            {
                if (SimplifyToleranceTexels > 0f && uvs != null && uvs.Length == positions.Length &&
                    textureSizes != null && subMeshIndex < textureSizes.Length && textureSizes[subMeshIndex].x > 1 && textureSizes[subMeshIndex].y > 1)
                {
                    texelSizes[subMeshIndex] = new Vector2(textureSizes[subMeshIndex].x - 1, textureSizes[subMeshIndex].y - 1);
                }
            }

            // 2. 多角形を三角形分割する。失敗した集まりは元の三角形のまま残し、その頂点を必要とする集まりをやり直す
            HashSet<int> removedChain = new HashSet<int>();
            Queue<Group> queue = new Queue<Group>();
            foreach (Group group in groups)
            {
                if (group.Kind == GroupKind.Merge)
                {
                    queue.Enqueue(group);
                    group.Queued = true;
                }
            }
            while (queue.Count > 0)
            {
                Group group = queue.Dequeue();
                group.Queued = false;
                if (TriangulateGroup(group, protectedVertexCount, wanted, groupsByVertex, uvs, texelSizes[group.SubMeshIndex], removedChain))
                {
                    continue;
                }
                // 三角形分割に失敗: 元の三角形のまま残し、全ての頂点を必要とする
                group.Kind = GroupKind.Fallback;
                FallbackTriangulationCount++;
                List<int> newlyWanted = new List<int>();
                foreach (int vertex in group.Vertices)
                {
                    if (vertex >= protectedVertexCount && wanted.Add(vertex))
                    {
                        newlyWanted.Add(vertex);
                        AddTwins(vertex, twins, wanted, newlyWanted);
                    }
                }
                foreach (int vertex in newlyWanted)
                {
                    foreach (Group neighbour in groupsByVertex[vertex])
                    {
                        if (neighbour != group && neighbour.Kind == GroupKind.Merge && !neighbour.Queued)
                        {
                            queue.Enqueue(neighbour);
                            neighbour.Queued = true;
                        }
                    }
                }
            }

            // 3. 出力
            int[][] result = new int[triangles.Length][];
            newParents = new int[triangles.Length][];
            List<Group>[] groupsPerSubMesh = new List<Group>[triangles.Length];
            for (int i = 0; i < triangles.Length; i++)
            {
                groupsPerSubMesh[i] = new List<Group>();
            }
            foreach (Group group in groups)
            {
                groupsPerSubMesh[group.SubMeshIndex].Add(group);
            }
            HashSet<int> removedFlat = new HashSet<int>();
            TriangleCountAfter = 0;
            for (int subMeshIndex = 0; subMeshIndex < triangles.Length; subMeshIndex++)
            {
                List<int> output = new List<int>(triangles[subMeshIndex].Length);
                List<int> outputParents = new List<int>(triangles[subMeshIndex].Length / 3);
                foreach (Group group in groupsPerSubMesh[subMeshIndex])
                {
                    int countBefore = output.Count / 3;
                    if (group.Kind == GroupKind.Merge)
                    {
                        output.AddRange(group.Emitted);
                        MergedGroupCount++;
                        foreach (int vertex in group.Vertices)
                        {
                            if (vertex >= protectedVertexCount && !wanted.Contains(vertex) && !group.EmittedVertices.Contains(vertex))
                            {
                                removedFlat.Add(vertex);
                            }
                        }
                    }
                    else
                    {
                        output.AddRange(group.SourceTriangles);
                    }
                    for (int k = countBefore; k < output.Count / 3; k++)
                    {
                        outputParents.Add(group.Parent);
                    }
                }
                result[subMeshIndex] = output.ToArray();
                newParents[subMeshIndex] = outputParents.ToArray();
                TriangleCountAfter += output.Count / 3;
            }
            RemovedFlatVertexCount = removedFlat.Count;
            RemovedChainVertexCount = removedChain.Count;
            return result;
        }

        private void ResetStatistics()
        {
            TriangleCountBefore = TriangleCountAfter = VertexCountBefore = VertexCountAfter = 0;
            MergedGroupCount = MultiPieceCount = 0;
            FallbackHoleCount = FallbackNonManifoldCount = FallbackTriangulationCount = 0;
            DroppedDegenerateLoopCount = RemovedFlatVertexCount = RemovedChainVertexCount = 0;
        }

        private enum GroupKind
        {
            Untouched,   // 元の頂点だけの三角形、または三角形が1つだけ: そのまま出力する
            Fallback,    // 再結合できない形状: 元の三角形のまま出力する
            Merge        // 外周を三角形分割し直す
        }

        // 同じ元三角形から生じた出力三角形の集まり
        private class Group
        {
            public int SubMeshIndex;
            public int Parent;
            public int[] TriangleIndices;              // 入力の三角形番号
            public int[] SourceTriangles;              // 入力の三角形の頂点番号（3つずつ）
            public GroupKind Kind;
            public bool Queued;
            public List<int> Vertices = new List<int>();          // 使っている頂点（重複なし）
            public Dictionary<int, Vector2> Planar = new Dictionary<int, Vector2>();   // 頂点 → 元三角形の平面上の2次元座標
            public List<int[]> Loops = new List<int[]>();          // 外周（反時計回り）
            public HashSet<int> Flat = new HashSet<int>();         // 外周上で一直線上に並ぶ頂点
            public HashSet<int> OnEdge = new HashSet<int>();       // 元の三角形の辺上にある頂点（間引かない）
            public List<int> Emitted = new List<int>();            // 再結合後の三角形
            public HashSet<int> EmittedVertices = new HashSet<int>();
        }

        // サブメッシュの三角形を元の三角形番号ごとにまとめる（出力順は最初の三角形の順）
        private static IEnumerable<Group> CollectGroups(int subMeshIndex, int[] triangles, int[] parents)
        {
            int triangleCount = triangles.Length / 3;
            Dictionary<int, List<int>> byParent = new Dictionary<int, List<int>>();
            List<int> order = new List<int>();
            for (int i = 0; i < triangleCount; i++)
            {
                int parent = i < parents.Length ? parents[i] : -1 - i;   // 対応が無い三角形はそれぞれ単独の集まりにする
                if (!byParent.TryGetValue(parent, out List<int> list))
                {
                    list = new List<int>();
                    byParent[parent] = list;
                    order.Add(parent);
                }
                list.Add(i);
            }
            foreach (int parent in order)
            {
                int[] triangleIndices = byParent[parent].ToArray();
                int[] sourceTriangles = new int[triangleIndices.Length * 3];
                for (int i = 0; i < triangleIndices.Length; i++)
                {
                    Array.Copy(triangles, triangleIndices[i] * 3, sourceTriangles, i * 3, 3);
                }
                yield return new Group { SubMeshIndex = subMeshIndex, Parent = parent, TriangleIndices = triangleIndices, SourceTriangles = sourceTriangles };
            }
        }

        // 集まりの種類を決め、再結合対象なら外周と一直線上の頂点を求める。必要な頂点を wanted に登録する
        private void PrepareGroup(Group group, Vector3[] positions, Vector3[] originalPositions, int[] originalTriangles, int protectedVertexCount,
                                  HashSet<int> wanted, Dictionary<int, List<Group>> groupsByVertex)
        {
            int[] triangles = group.SourceTriangles;
            bool allProtected = true;
            HashSet<int> seen = new HashSet<int>();
            foreach (int vertex in triangles)
            {
                if (!seen.Add(vertex))
                {
                    continue;
                }
                group.Vertices.Add(vertex);
                if (vertex >= protectedVertexCount)
                {
                    allProtected = false;
                    if (!groupsByVertex.TryGetValue(vertex, out List<Group> list))
                    {
                        list = new List<Group>();
                        groupsByVertex[vertex] = list;
                    }
                    list.Add(group);   // 集まりごとに1回だけ登録する（複数の集まりから使われる頂点 = 共有頂点）
                }
            }

            // 元の頂点だけの三角形、または三角形が1つだけの集まりは再結合の必要が無い
            if (allProtected || group.TriangleIndices.Length == 1 || group.Parent < 0 || group.Parent * 3 + 2 >= originalTriangles.Length)
            {
                group.Kind = GroupKind.Untouched;
                MarkAllWanted(group, protectedVertexCount, wanted);
                return;
            }

            // 元の三角形の平面に投影する（e1 × e2 が元の三角形の法線になる基底）
            Vector3 a = originalPositions[originalTriangles[group.Parent * 3]];
            Vector3 b = originalPositions[originalTriangles[group.Parent * 3 + 1]];
            Vector3 c = originalPositions[originalTriangles[group.Parent * 3 + 2]];
            Vector3 normal = Normalize(Vector3.Cross(b - a, c - a));
            Vector3 e1 = Normalize(b - a);
            Vector3 e2 = Vector3.Cross(normal, e1);
            if (normal == Vector3.zero || e1 == Vector3.zero)
            {
                group.Kind = GroupKind.Fallback;   // 潰れた元三角形
                FallbackNonManifoldCount++;
                MarkAllWanted(group, protectedVertexCount, wanted);
                return;
            }
            Vector2 planarB = new Vector2(Vector3.Dot(b - a, e1), 0f);
            Vector2 planarC = new Vector2(Vector3.Dot(c - a, e1), Vector3.Dot(c - a, e2));
            foreach (int vertex in group.Vertices)
            {
                Vector3 d = positions[vertex] - a;
                Vector2 planar = new Vector2(Vector3.Dot(d, e1), Vector3.Dot(d, e2));
                group.Planar[vertex] = planar;
                // 重心座標のいずれかが 0 に近ければ元の三角形の辺上（隣の三角形と共有される可能性がある）
                float gamma = planarC.y != 0f ? planar.y / planarC.y : 0f;
                float beta = planarB.x != 0f ? (planar.x - gamma * planarC.x) / planarB.x : 0f;
                float alpha = 1f - beta - gamma;
                if (Mathf.Min(alpha, Mathf.Min(beta, gamma)) < OnEdgeTolerance)
                {
                    group.OnEdge.Add(vertex);
                }
            }

            // 各三角形の向きを元の三角形に揃え、有向辺を数える
            Dictionary<(int, int), int> edgeCount = new Dictionary<(int, int), int>();
            for (int i = 0; i < triangles.Length; i += 3)
            {
                int v0 = triangles[i], v1 = triangles[i + 1], v2 = triangles[i + 2];
                if (Cross(group.Planar[v0], group.Planar[v1], group.Planar[v2]) < 0f)
                {
                    (v1, v2) = (v2, v1);
                }
                CountEdge(edgeCount, v0, v1);
                CountEdge(edgeCount, v1, v2);
                CountEdge(edgeCount, v2, v0);
            }

            // 外周（逆向きの辺が無い有向辺）を辿る
            Dictionary<int, int> next = new Dictionary<int, int>();
            foreach (KeyValuePair<(int, int), int> edge in edgeCount)
            {
                if (edge.Value > 1)
                {
                    group.Kind = GroupKind.Fallback;   // 同じ有向辺が複数回現れる（非多様体）
                    FallbackNonManifoldCount++;
                    MarkAllWanted(group, protectedVertexCount, wanted);
                    return;
                }
                (int from, int to) = edge.Key;
                if (edgeCount.ContainsKey((to, from)))
                {
                    continue;
                }
                if (next.ContainsKey(from))
                {
                    group.Kind = GroupKind.Fallback;   // 1つの頂点から外周が2方向に出る（頂点で接する断片）
                    FallbackNonManifoldCount++;
                    MarkAllWanted(group, protectedVertexCount, wanted);
                    return;
                }
                next[from] = to;
            }
            HashSet<int> visited = new HashSet<int>();
            foreach (int start in next.Keys)
            {
                if (visited.Contains(start))
                {
                    continue;
                }
                List<int> loop = new List<int>();
                int current = start;
                do
                {
                    loop.Add(current);
                    visited.Add(current);
                    current = next[current];
                } while (current != start && loop.Count <= next.Count);
                if (current != start)
                {
                    group.Kind = GroupKind.Fallback;   // 閉じない（起こらないはずだが念のため）
                    FallbackNonManifoldCount++;
                    MarkAllWanted(group, protectedVertexCount, wanted);
                    return;
                }
                float area = SignedArea(group.Planar, loop);
                if (area < 0f)
                {
                    group.Kind = GroupKind.Fallback;   // 時計回りの外周 = 穴
                    FallbackHoleCount++;
                    MarkAllWanted(group, protectedVertexCount, wanted);
                    return;
                }
                group.Loops.Add(loop.ToArray());
            }
            if (group.Loops.Count > 1)
            {
                MultiPieceCount++;
            }

            // 外周上で一直線上に並ぶ頂点を求め、それ以外の（保護されていない）頂点は必要な頂点として登録する
            group.Kind = GroupKind.Merge;
            foreach (int[] loop in group.Loops)
            {
                for (int k = 0; k < loop.Length; k++)
                {
                    int vertex = loop[k];
                    if (vertex < protectedVertexCount)
                    {
                        continue;
                    }
                    Vector2 previous = group.Planar[loop[(k + loop.Length - 1) % loop.Length]];
                    Vector2 point = group.Planar[vertex];
                    Vector2 following = group.Planar[loop[(k + 1) % loop.Length]];
                    Vector2 u = point - previous;
                    Vector2 w = following - point;
                    float sine = Mathf.Abs(u.x * w.y - u.y * w.x) / Mathf.Max(u.magnitude * w.magnitude, 1e-30f);
                    if (sine < CollinearTolerance && Vector2.Dot(u, w) > 0f)
                    {
                        group.Flat.Add(vertex);
                    }
                    else
                    {
                        wanted.Add(vertex);
                    }
                }
            }
        }

        // 集まりの外周から不要な頂点を取り除いて三角形分割し、group.Emitted に格納する。失敗したら false
        private bool TriangulateGroup(Group group, int protectedVertexCount, HashSet<int> wanted, Dictionary<int, List<Group>> groupsByVertex,
                                      Vector2[] uvs, Vector2 texelSize, HashSet<int> removedChain)
        {
            group.Emitted.Clear();
            group.EmittedVertices.Clear();
            foreach (int[] loop in group.Loops)
            {
                // 一直線上にあり、他の集まりが必要としない頂点を取り除く
                List<int> polygon = new List<int>(loop.Length);
                foreach (int vertex in loop)
                {
                    if (vertex >= protectedVertexCount && group.Flat.Contains(vertex) && !wanted.Contains(vertex))
                    {
                        continue;
                    }
                    polygon.Add(vertex);
                }
                // この三角形の内部だけを通る切り口の頂点を間引く
                if (texelSize.x > 0f && polygon.Count >= 3)
                {
                    SimplifyChain(group, polygon, protectedVertexCount, groupsByVertex, uvs, texelSize, removedChain);
                }
                if (polygon.Count < 3)
                {
                    DroppedDegenerateLoopCount++;
                    continue;
                }
                List<Vector2> points = new List<Vector2>(polygon.Count);
                foreach (int vertex in polygon)
                {
                    points.Add(group.Planar[vertex]);
                }
                int[] local = EarClipping2D.Triangulate(points, out bool usedFallback);
                if (usedFallback)
                {
                    return false;
                }
                if (local.Length == 0)
                {
                    DroppedDegenerateLoopCount++;
                    continue;
                }
                foreach (int index in local)
                {
                    group.Emitted.Add(polygon[index]);
                    group.EmittedVertices.Add(polygon[index]);
                }
            }
            return true;
        }

        // 同じ座標にある（保護されていない）頂点同士の対応表を作る
        private static Dictionary<int, List<int>> CollectPositionTwins(Vector3[] positions, IEnumerable<int> vertices)
        {
            Dictionary<Vector3, List<int>> byPosition = new Dictionary<Vector3, List<int>>();
            foreach (int vertex in vertices)
            {
                if (!byPosition.TryGetValue(positions[vertex], out List<int> list))
                {
                    list = new List<int>();
                    byPosition[positions[vertex]] = list;
                }
                list.Add(vertex);
            }
            Dictionary<int, List<int>> twins = new Dictionary<int, List<int>>();
            foreach (List<int> list in byPosition.Values)
            {
                if (list.Count > 1)
                {
                    foreach (int vertex in list)
                    {
                        twins[vertex] = list;
                    }
                }
            }
            return twins;
        }

        // 頂点と同じ座標の頂点も必要な頂点として登録する
        private static void AddTwins(int vertex, Dictionary<int, List<int>> twins, HashSet<int> wanted, List<int> added = null)
        {
            if (!twins.TryGetValue(vertex, out List<int> list))
            {
                return;
            }
            foreach (int twin in list)
            {
                if (wanted.Add(twin))
                {
                    added?.Add(twin);
                }
            }
        }

        // 外周のうち固定されない頂点（元の頂点でも、元の三角形の辺上でも、他の集まりと共有でもない切り口の頂点）の連なりを、
        // 固定された頂点の間ごとに Douglas-Peucker 法でテクセル空間で間引く。頂点は取り除くだけで動かさない
        private void SimplifyChain(Group group, List<int> polygon, int protectedVertexCount, Dictionary<int, List<Group>> groupsByVertex,
                                   Vector2[] uvs, Vector2 texelSize, HashSet<int> removedChain)
        {
            int count = polygon.Count;
            Vector2[] texels = new Vector2[count];
            bool[] keep = new bool[count];
            List<int> anchors = new List<int>();
            for (int i = 0; i < count; i++)
            {
                int vertex = polygon[i];
                texels[i] = Vector2.Scale(uvs[vertex], texelSize);
                keep[i] = vertex < protectedVertexCount || group.OnEdge.Contains(vertex) ||
                          (groupsByVertex.TryGetValue(vertex, out List<Group> users) && users.Count > 1);
                if (keep[i])
                {
                    anchors.Add(i);
                }
            }
            if (anchors.Count == 0)
            {
                // 三角形の内部で閉じた切り口（島）: 先頭、先頭から最も遠い点、その弦から最も遠い点を固定する
                int farthest = 0;
                for (int i = 1; i < count; i++)
                {
                    if ((texels[i] - texels[0]).sqrMagnitude > (texels[farthest] - texels[0]).sqrMagnitude) farthest = i;
                }
                int offChord = 0;
                float maxDistance = -1f;
                for (int i = 0; i < count; i++)
                {
                    float distance = DistanceToSegment(texels[i], texels[0], texels[farthest]);
                    if (distance > maxDistance) { maxDistance = distance; offChord = i; }
                }
                keep[0] = keep[farthest] = keep[offChord] = true;
                anchors.Add(0);
                if (farthest != 0) anchors.Add(farthest);
                if (offChord != 0 && offChord != farthest) anchors.Add(offChord);
                anchors.Sort();
            }
            // 固定された頂点の間の連なりごとに間引く
            for (int k = 0; k < anchors.Count; k++)
            {
                int start = anchors[k];
                int end = anchors[(k + 1) % anchors.Count];
                int length = ((end - start) % count + count) % count;   // start から end までの辺の数（巡回）
                if (length < 2)
                {
                    continue;
                }
                List<int> run = new List<int>(length + 1);
                for (int i = 0; i <= length; i++)
                {
                    run.Add((start + i) % count);
                }
                DouglasPeucker(texels, run, 0, run.Count - 1, SimplifyToleranceTexels, keep);
            }
            for (int i = count - 1; i >= 0; i--)
            {
                if (!keep[i])
                {
                    removedChain.Add(polygon[i]);
                    polygon.RemoveAt(i);
                }
            }
        }

        // run[first] と run[last] を結ぶ線分から最も離れた点が許容誤差を超えていればその点を残して再帰する
        private static void DouglasPeucker(Vector2[] texels, List<int> run, int first, int last, float tolerance, bool[] keep)
        {
            if (last - first < 2)
            {
                return;
            }
            int farthest = -1;
            float maxDistance = tolerance;
            for (int i = first + 1; i < last; i++)
            {
                float distance = DistanceToSegment(texels[run[i]], texels[run[first]], texels[run[last]]);
                if (distance > maxDistance)
                {
                    maxDistance = distance;
                    farthest = i;
                }
            }
            if (farthest < 0)
            {
                return;
            }
            keep[run[farthest]] = true;
            DouglasPeucker(texels, run, first, farthest, tolerance, keep);
            DouglasPeucker(texels, run, farthest, last, tolerance, keep);
        }

        // 点から線分 ab を含む直線までの距離（a と b が同じ点なら a までの距離）
        private static float DistanceToSegment(Vector2 point, Vector2 a, Vector2 b)
        {
            Vector2 d = b - a;
            float length = d.magnitude;
            if (length <= 0f)
            {
                return (point - a).magnitude;
            }
            return Mathf.Abs((point.x - a.x) * d.y - (point.y - a.y) * d.x) / length;
        }

        private static void MarkAllWanted(Group group, int protectedVertexCount, HashSet<int> wanted)
        {
            foreach (int vertex in group.Vertices)
            {
                if (vertex >= protectedVertexCount)
                {
                    wanted.Add(vertex);
                }
            }
        }

        private static void CountEdge(Dictionary<(int, int), int> edgeCount, int from, int to)
        {
            edgeCount[(from, to)] = edgeCount.TryGetValue((from, to), out int count) ? count + 1 : 1;
        }

        private static float SignedArea(Dictionary<int, Vector2> planar, List<int> loop)
        {
            float sum = 0f;
            for (int i = 0; i < loop.Count; i++)
            {
                Vector2 p = planar[loop[i]];
                Vector2 q = planar[loop[(i + 1) % loop.Count]];
                sum += p.x * q.y - q.x * p.y;
            }
            return 0.5f * sum;
        }

        private static float Cross(Vector2 a, Vector2 b, Vector2 c)
        {
            return (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
        }

        // 長さで割って正規化する（Vector3.normalized は小さなベクトルで零を返すため使わない）
        private static Vector3 Normalize(Vector3 v)
        {
            float magnitude = Mathf.Sqrt(v.sqrMagnitude);
            return magnitude > 0f ? v / magnitude : Vector3.zero;
        }

        // 参照されなくなった頂点を全ての頂点属性・ボーンウェイト・ブレンドシェイプから取り除いたメッシュを作る
        private MeshArrays CompactMesh(MeshArrays cutMesh, int[][] mergedTriangles)
        {
            int vertexCount = cutMesh.VertexCount;
            VertexCountBefore = vertexCount;
            int[] newIndex = new int[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                newIndex[i] = -1;
            }
            foreach (int[] triangles in mergedTriangles)
            {
                foreach (int vertex in triangles)
                {
                    newIndex[vertex] = 0;
                }
            }
            int newVertexCount = 0;
            for (int i = 0; i < vertexCount; i++)
            {
                if (newIndex[i] == 0)
                {
                    newIndex[i] = newVertexCount++;
                }
            }
            VertexCountAfter = newVertexCount;
            VertexSourceIndex = new int[newVertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                if (newIndex[i] >= 0)
                    VertexSourceIndex[newIndex[i]] = i;
            }

            // 出力メッシュ（インデックス形式は元のまま。頂点数が 65,535 を超えれば Mesh 作成時に 32 ビットになる）
            MeshArrays mergedMesh = new MeshArrays();
            mergedMesh.Name = cutMesh.Name;
            mergedMesh.IndexFormat = cutMesh.IndexFormat;
            mergedMesh.Vertices = Compact(cutMesh.Vertices, newIndex, newVertexCount);
            mergedMesh.Normals = Compact(cutMesh.Normals, newIndex, newVertexCount);
            mergedMesh.Tangents = Compact(cutMesh.Tangents, newIndex, newVertexCount);
            for (int channel = 0; channel < MeshArrays.UVChannelCount; channel++)
            {
                mergedMesh.SetUV(channel, Compact(cutMesh.GetUV(channel), newIndex, newVertexCount));
            }
            mergedMesh.Colors = Compact(cutMesh.Colors, newIndex, newVertexCount);
            mergedMesh.BoneWeights = Compact(cutMesh.BoneWeights, newIndex, newVertexCount);
            mergedMesh.Bindposes = cutMesh.Bindposes;

            mergedMesh.SubMeshTriangles = new int[mergedTriangles.Length][];
            for (int subMeshIndex = 0; subMeshIndex < mergedTriangles.Length; subMeshIndex++)
            {
                int[] triangles = mergedTriangles[subMeshIndex];
                int[] remapped = new int[triangles.Length];
                for (int i = 0; i < triangles.Length; i++)
                {
                    remapped[i] = newIndex[triangles[i]];
                }
                mergedMesh.SubMeshTriangles[subMeshIndex] = remapped;
            }

            // ブレンドシェイプ（頂点を取り除くだけで差分は変えない）
            foreach (BlendShapeData shape in cutMesh.BlendShapes)
            {
                BlendShapeData newShape = new BlendShapeData { Name = shape.Name };
                foreach (BlendShapeFrameData frame in shape.Frames)
                {
                    newShape.Frames.Add(new BlendShapeFrameData
                    {
                        Weight = frame.Weight,
                        DeltaVertices = Compact(frame.DeltaVertices, newIndex, newVertexCount),
                        DeltaNormals = Compact(frame.DeltaNormals, newIndex, newVertexCount),
                        DeltaTangents = Compact(frame.DeltaTangents, newIndex, newVertexCount)
                    });
                }
                mergedMesh.BlendShapes.Add(newShape);
            }
            mergedMesh.Bounds = cutMesh.Bounds;
            return mergedMesh;
        }

        // 頂点属性の配列を新しい頂点番号で詰める（属性が無い = 長さが頂点数と異なる場合は空の配列）
        private static T[] Compact<T>(T[] source, int[] newIndex, int newVertexCount)
        {
            if (source == null || source.Length != newIndex.Length)
            {
                return new T[0];
            }
            T[] result = new T[newVertexCount];
            for (int i = 0; i < source.Length; i++)
            {
                if (newIndex[i] >= 0)
                {
                    result[newIndex[i]] = source[i];
                }
            }
            return result;
        }
    }
}
