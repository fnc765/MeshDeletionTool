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
    public class CutPolygonMerger
    {
        // 外周上の頂点を「一直線上」とみなす角度の許容値（sin）
        public float CollinearTolerance = 1e-4f;

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
        public int FallbackCount => FallbackHoleCount + FallbackNonManifoldCount + FallbackTriangulationCount;

        // 実行結果: サブメッシュ毎の、再結合後の三角形番号 → 元の三角形番号
        public List<int[]> ParentTriangleIndexPerSubMesh;

        // 切断後のメッシュ cutMesh を再結合したメッシュを返す
        //   parentTriangleIndexPerSubMesh: サブメッシュ毎の、cutMesh の三角形番号 → originalMesh の三角形番号
        //   originalMesh: 細分化前の元のメッシュ（元の三角形の平面を得るために使う）
        //   protectedVertexCount: cutMesh の先頭からこの数の頂点は元のメッシュの頂点であり、決して取り除かない
        public Mesh Merge(Mesh cutMesh, List<int[]> parentTriangleIndexPerSubMesh, Mesh originalMesh, int protectedVertexCount)
        {
            int subMeshCount = cutMesh.subMeshCount;
            int[][] triangles = new int[subMeshCount][];
            int[][] parents = new int[subMeshCount][];
            int[][] originalTriangles = new int[subMeshCount][];
            for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
            {
                triangles[subMeshIndex] = cutMesh.GetTriangles(subMeshIndex);
                parents[subMeshIndex] = parentTriangleIndexPerSubMesh[subMeshIndex];
                originalTriangles[subMeshIndex] = originalMesh.GetTriangles(subMeshIndex);
            }

            int[][] mergedTriangles = MergeTriangles(cutMesh.vertices, triangles, parents, originalMesh.vertices, originalTriangles,
                                                     protectedVertexCount, out int[][] mergedParents);
            ParentTriangleIndexPerSubMesh = new List<int[]>(mergedParents);

            return CompactMesh(cutMesh, mergedTriangles);
        }

        // 再結合の本体（Mesh に依存しない）。サブメッシュ毎の新しい三角形配列を返す。頂点番号は入力のまま（詰めない）
        internal int[][] MergeTriangles(Vector3[] positions, int[][] triangles, int[][] parents,
                                        Vector3[] originalPositions, int[][] originalTriangles,
                                        int protectedVertexCount, out int[][] newParents)
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

            // 2. 多角形を三角形分割する。失敗した集まりは元の三角形のまま残し、その頂点を必要とする集まりをやり直す
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
                if (TriangulateGroup(group, positions, protectedVertexCount, wanted))
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
            return result;
        }

        private void ResetStatistics()
        {
            TriangleCountBefore = TriangleCountAfter = VertexCountBefore = VertexCountAfter = 0;
            MergedGroupCount = MultiPieceCount = 0;
            FallbackHoleCount = FallbackNonManifoldCount = FallbackTriangulationCount = 0;
            DroppedDegenerateLoopCount = RemovedFlatVertexCount = 0;
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
                if (seen.Add(vertex))
                {
                    group.Vertices.Add(vertex);
                }
                if (vertex >= protectedVertexCount)
                {
                    allProtected = false;
                    if (!groupsByVertex.TryGetValue(vertex, out List<Group> list))
                    {
                        list = new List<Group>();
                        groupsByVertex[vertex] = list;
                    }
                    list.Add(group);
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
            foreach (int vertex in group.Vertices)
            {
                Vector3 d = positions[vertex] - a;
                group.Planar[vertex] = new Vector2(Vector3.Dot(d, e1), Vector3.Dot(d, e2));
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
        private bool TriangulateGroup(Group group, Vector3[] positions, int protectedVertexCount, HashSet<int> wanted)
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
        private Mesh CompactMesh(Mesh cutMesh, int[][] mergedTriangles)
        {
            int vertexCount = cutMesh.vertexCount;
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

            Mesh mergedMesh = new Mesh();
            mergedMesh.name = cutMesh.name;
            mergedMesh.indexFormat = newVertexCount > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : cutMesh.indexFormat;
            mergedMesh.SetVertices(Compact(cutMesh.vertices, newIndex, newVertexCount));
            List<Vector3> normals = Compact(cutMesh.normals, newIndex, newVertexCount);
            if (normals != null) mergedMesh.SetNormals(normals);
            List<Vector4> tangents = Compact(cutMesh.tangents, newIndex, newVertexCount);
            if (tangents != null) mergedMesh.SetTangents(tangents);
            List<Vector2> uv = Compact(cutMesh.uv, newIndex, newVertexCount);
            if (uv != null) mergedMesh.SetUVs(0, uv);
            List<Vector2> uv2 = Compact(cutMesh.uv2, newIndex, newVertexCount);
            if (uv2 != null) mergedMesh.SetUVs(1, uv2);
            List<Vector2> uv3 = Compact(cutMesh.uv3, newIndex, newVertexCount);
            if (uv3 != null) mergedMesh.SetUVs(2, uv3);
            List<Vector2> uv4 = Compact(cutMesh.uv4, newIndex, newVertexCount);
            if (uv4 != null) mergedMesh.SetUVs(3, uv4);
            List<Color> colors = Compact(cutMesh.colors, newIndex, newVertexCount);
            if (colors != null) mergedMesh.SetColors(colors);
            List<BoneWeight> boneWeights = Compact(cutMesh.boneWeights, newIndex, newVertexCount);
            if (boneWeights != null) mergedMesh.boneWeights = boneWeights.ToArray();
            mergedMesh.bindposes = cutMesh.bindposes;

            mergedMesh.subMeshCount = mergedTriangles.Length;
            for (int subMeshIndex = 0; subMeshIndex < mergedTriangles.Length; subMeshIndex++)
            {
                int[] triangles = mergedTriangles[subMeshIndex];
                List<int> remapped = new List<int>(triangles.Length);
                foreach (int vertex in triangles)
                {
                    remapped.Add(newIndex[vertex]);
                }
                mergedMesh.SetTriangles(remapped, subMeshIndex);
            }

            // ブレンドシェイプ（頂点を取り除くだけで差分は変えない）
            for (int i = 0; i < cutMesh.blendShapeCount; i++)
            {
                string blendShapeName = cutMesh.GetBlendShapeName(i);
                int frameCount = cutMesh.GetBlendShapeFrameCount(i);
                for (int j = 0; j < frameCount; j++)
                {
                    float frameWeight = cutMesh.GetBlendShapeFrameWeight(i, j);
                    Vector3[] deltaVertices = new Vector3[vertexCount];
                    Vector3[] deltaNormals = new Vector3[vertexCount];
                    Vector3[] deltaTangents = new Vector3[vertexCount];
                    cutMesh.GetBlendShapeFrameVertices(i, j, deltaVertices, deltaNormals, deltaTangents);
                    mergedMesh.AddBlendShapeFrame(blendShapeName, frameWeight,
                                                  Compact(deltaVertices, newIndex, newVertexCount).ToArray(),
                                                  Compact(deltaNormals, newIndex, newVertexCount).ToArray(),
                                                  Compact(deltaTangents, newIndex, newVertexCount).ToArray());
                }
            }
            mergedMesh.bounds = cutMesh.bounds;
            return mergedMesh;
        }

        // 頂点属性の配列を新しい頂点番号で詰める（属性が無い = 長さが頂点数と異なる場合は null）
        private static List<T> Compact<T>(T[] source, int[] newIndex, int newVertexCount)
        {
            if (source == null || source.Length != newIndex.Length)
            {
                return null;
            }
            List<T> result = new List<T>(newVertexCount);
            for (int i = 0; i < newVertexCount; i++)
            {
                result.Add(default(T));
            }
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
