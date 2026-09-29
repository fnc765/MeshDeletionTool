using UnityEngine;
using System;
using System.Collections.Generic;

namespace MeshDeletionTool
{
    // テクスチャのアルファ境界付近の三角形を、削除処理の前に細分化するクラス
    // 既存の削除処理は「頂点UVのアルファ値」だけで削除を判定し、新しい頂点を「既存の辺上」にしか追加できないため、
    //   1. 3頂点とも透明だが内部に不透明テクセルを含む三角形は丸ごと削除され、
    //   2. 一部が削除される三角形は2つの境界点を結ぶ直線で切られ、不透明部分の膨らみが削り取られる。
    // 本クラスはこれらに該当する三角形を辺の中点で分割（赤緑細分化）し、細分化済みのメッシュを返す。
    // 隣接する三角形も分割された辺に合わせて再分割するため、隙間（T字接合）は生じない。
    public class AlphaBoundaryRefiner
    {
        // 細分化の最大深さ（0で無効）
        public int MaxDepth = 3;

        // 修正1: 3頂点とも透明で内部に不透明テクセルを含む三角形を細分化する
        public bool RefineFullyTransparentTriangles = true;

        // 修正2: 直線で切ると不透明テクセルが失われる三角形（一部の頂点が透明）を細分化する
        public bool RefinePartiallyCutTriangles = true;

        // 修正2で許容する、失われる不透明テクセル数（0で厳密）
        public int ChordToleranceTexels = 0;

        // ラスタライズするバウンディングボックスの上限（テクセル）。超える場合は間引いてサンプリングする
        public int MaxRasterSize = 512;

        // 実行結果: 各深さの細分化後の三角形数
        public List<int> TriangleCountPerDepth = new List<int>();

        // 実行結果: 各深さで細分化対象となった三角形数
        public List<int> MarkedTriangleCountPerDepth = new List<int>();

        // 実行結果: サブメッシュ毎の、細分化後の三角形番号 → 元のメッシュの三角形番号（細分化が無ければ恒等）
        public List<int[]> ParentTriangleIndexPerSubMesh = new List<int[]>();

        // アルファ境界付近の三角形を細分化したメッシュを返す
        // subMeshTextures[i] が null のサブメッシュは判定対象外（隣接する辺の分割にのみ追従する）
        // 細分化が不要な場合は元のメッシュをそのまま返す
        public Mesh Refine(Mesh mesh, Texture2D[] subMeshTextures, float alphaThreshold)
        {
            TriangleCountPerDepth.Clear();
            MarkedTriangleCountPerDepth.Clear();
            ParentTriangleIndexPerSubMesh = CreateIdentityParents(mesh);

            Mesh currentMesh = mesh;
            for (int depth = 0; depth < MaxDepth; depth++)
            {
                HashSet<(int, int)> splitEdges = CollectEdgesToSplit(currentMesh, subMeshTextures, alphaThreshold, out int markedCount);
                if (splitEdges.Count == 0)
                {
                    break;
                }
                currentMesh = SplitEdges(currentMesh, splitEdges, ParentTriangleIndexPerSubMesh, out ParentTriangleIndexPerSubMesh);
                MarkedTriangleCountPerDepth.Add(markedCount);
                TriangleCountPerDepth.Add(currentMesh.triangles.Length / 3);
            }
            return currentMesh;
        }

        // 各サブメッシュの三角形番号をそのまま親とする対応表を作る
        private static List<int[]> CreateIdentityParents(Mesh mesh)
        {
            List<int[]> parents = new List<int[]>(mesh.subMeshCount);
            for (int subMeshIndex = 0; subMeshIndex < mesh.subMeshCount; subMeshIndex++)
            {
                int[] identity = new int[mesh.GetTriangles(subMeshIndex).Length / 3];
                for (int i = 0; i < identity.Length; i++)
                {
                    identity[i] = i;
                }
                parents.Add(identity);
            }
            return parents;
        }

        // 細分化対象の三角形を判定し、分割する辺（頂点インデックスの昇順ペア）の集合を返す
        private HashSet<(int, int)> CollectEdgesToSplit(Mesh mesh, Texture2D[] subMeshTextures, float alphaThreshold, out int markedCount)
        {
            HashSet<(int, int)> splitEdges = new HashSet<(int, int)>();
            markedCount = 0;
            Vector2[] uvs = mesh.uv;

            for (int subMeshIndex = 0; subMeshIndex < mesh.subMeshCount; subMeshIndex++)
            {
                Texture2D texture = subMeshIndex < subMeshTextures.Length ? subMeshTextures[subMeshIndex] : null;
                if (texture == null)
                {
                    continue;
                }

                int[] triangles = mesh.GetTriangles(subMeshIndex);
                for (int i = 0; i < triangles.Length; i += 3)
                {
                    if (ShouldSubdivide(texture, uvs, triangles[i], triangles[i + 1], triangles[i + 2], alphaThreshold))
                    {
                        markedCount++;
                        splitEdges.Add(MakeEdgeKey(triangles[i], triangles[i + 1]));
                        splitEdges.Add(MakeEdgeKey(triangles[i + 1], triangles[i + 2]));
                        splitEdges.Add(MakeEdgeKey(triangles[i + 2], triangles[i]));
                    }
                }
            }
            return splitEdges;
        }

        // 三角形を細分化すべきか判定する
        private bool ShouldSubdivide(Texture2D texture, Vector2[] uvs, int indexA, int indexB, int indexC, float alphaThreshold)
        {
            Vector2[] uv = { uvs[indexA], uvs[indexB], uvs[indexC] };

            // 最長辺が1テクセル未満の三角形はこれ以上細分化しない（終了保証）
            if (LongestEdgeInTexels(texture, uv) < 1f)
            {
                return false;
            }

            bool[] transparent =
            {
                IsTransparent(texture, uv[0], alphaThreshold),
                IsTransparent(texture, uv[1], alphaThreshold),
                IsTransparent(texture, uv[2], alphaThreshold)
            };
            int transparentCount = (transparent[0] ? 1 : 0) + (transparent[1] ? 1 : 0) + (transparent[2] ? 1 : 0);

            // 3頂点とも透明: 内部に不透明テクセルがあれば細分化する（修正1）
            if (transparentCount == 3)
            {
                return RefineFullyTransparentTriangles && CountOpaqueTexels(texture, uv, alphaThreshold, 1) > 0;
            }
            // 3頂点とも不透明: 既存処理でそのまま残るため対象外
            if (transparentCount == 0)
            {
                return false;
            }
            // 一部の頂点が透明: 既存処理で削除される側の多角形に不透明テクセルが含まれていれば細分化する（修正2）
            if (!RefinePartiallyCutTriangles)
            {
                return false;
            }
            int[] index = { indexA, indexB, indexC };
            List<Vector2> removedPolygon = BuildRemovedPolygon(texture, uv, index, transparent, alphaThreshold);
            return CountOpaqueTexels(texture, removedPolygon, alphaThreshold, ChordToleranceTexels + 1) > ChordToleranceTexels;
        }

        // 既存処理と同じ境界点（辺上の二分探索）を使い、三角形のうち削除される側の多角形（UV座標、外周順）を作る
        private static List<Vector2> BuildRemovedPolygon(Texture2D texture, Vector2[] uv, int[] index, bool[] transparent, float alphaThreshold)
        {
            List<Vector2> polygon = new List<Vector2>(4);
            for (int i = 0; i < 3; i++)
            {
                int j = (i + 1) % 3;
                if (transparent[i])
                {
                    polygon.Add(uv[i]);
                }
                if (MeshDeletionToolForTexture.IsBoundaryEdge(texture, uv[i], uv[j], alphaThreshold))
                {
                    // 辺の向きも既存処理と同じ（頂点インデックスの昇順）にして同一の境界点を得る
                    (Vector2 uvA, Vector2 uvB) = index[i] < index[j] ? (uv[i], uv[j]) : (uv[j], uv[i]);
                    float weight = MeshDeletionToolForTexture.FindAlphaBoundary(texture, uvA, uvB, alphaThreshold);
                    polygon.Add(Vector2.Lerp(uvA, uvB, weight));
                }
            }
            return polygon;
        }

        // UV座標が示すテクセルのアルファ値を取得する（既存処理と同じテクセル座標の求め方）
        private static float SampleAlpha(Texture2D texture, Vector2 uv)
        {
            int x = (int)(uv.x * (texture.width - 1));
            int y = (int)(uv.y * (texture.height - 1));
            return texture.GetPixel(x, y).a;
        }

        // UV座標が示すテクセルが透明（削除対象）かどうか
        private static bool IsTransparent(Texture2D texture, Vector2 uv, float alphaThreshold)
        {
            return SampleAlpha(texture, uv) < alphaThreshold;
        }

        // 三角形の最長辺の長さ（テクセル単位）
        private static float LongestEdgeInTexels(Texture2D texture, Vector2[] uv)
        {
            Vector2 scale = new Vector2(texture.width - 1, texture.height - 1);
            float longest = 0f;
            for (int i = 0; i < 3; i++)
            {
                Vector2 edge = Vector2.Scale(uv[(i + 1) % 3] - uv[i], scale);
                longest = Mathf.Max(longest, edge.magnitude);
            }
            return longest;
        }

        // 凸多角形（UV座標）の内部にある不透明テクセルの数を数える（stopAt に達したら打ち切る）
        private int CountOpaqueTexels(Texture2D texture, IList<Vector2> polygon, float alphaThreshold, int stopAt)
        {
            if (polygon.Count < 3)
            {
                return 0;
            }

            int width = texture.width;
            int height = texture.height;

            // 既存処理と同じテクセル座標系 (x = u * (w - 1), y = v * (h - 1)) に変換し、バウンディングボックスを求める
            Vector2[] points = new Vector2[polygon.Count];
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < polygon.Count; i++)
            {
                points[i] = new Vector2(polygon[i].x * (width - 1), polygon[i].y * (height - 1));
                minX = Mathf.Min(minX, points[i].x);
                minY = Mathf.Min(minY, points[i].y);
                maxX = Mathf.Max(maxX, points[i].x);
                maxY = Mathf.Max(maxY, points[i].y);
            }

            // 多角形の向き（面積0なら内部のテクセルは無い）
            float signedArea = 0f;
            for (int i = 0; i < points.Length; i++)
            {
                Vector2 p = points[i];
                Vector2 q = points[(i + 1) % points.Length];
                signedArea += p.x * q.y - q.x * p.y;
            }
            if (Mathf.Approximately(signedArea, 0f))
            {
                return 0;
            }
            float orientation = signedArea > 0f ? 1f : -1f;

            // テクセル範囲（GetPixel と同様にテクスチャの範囲内に制限する）
            int x0 = Mathf.Clamp(Mathf.FloorToInt(minX), 0, width - 1);
            int x1 = Mathf.Clamp(Mathf.FloorToInt(maxX), 0, width - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(minY), 0, height - 1);
            int y1 = Mathf.Clamp(Mathf.FloorToInt(maxY), 0, height - 1);

            // 巨大な多角形は間引いてサンプリングする
            int stride = Mathf.Max(1, Mathf.CeilToInt(Mathf.Max(x1 - x0 + 1, y1 - y0 + 1) / (float)MaxRasterSize));

            int count = 0;
            for (int y = y0; y <= y1; y += stride)
            {
                for (int x = x0; x <= x1; x += stride)
                {
                    // テクセルの中心が多角形の内部にあるか
                    if (!IsInsideConvexPolygon(points, orientation, new Vector2(x + 0.5f, y + 0.5f)))
                    {
                        continue;
                    }
                    if (texture.GetPixel(x, y).a >= alphaThreshold)
                    {
                        count++;
                        if (count >= stopAt)
                        {
                            return count;
                        }
                    }
                }
            }
            return count;
        }

        // 点が凸多角形の内部（境界を含む）にあるか判定する
        private static bool IsInsideConvexPolygon(Vector2[] points, float orientation, Vector2 point)
        {
            for (int i = 0; i < points.Length; i++)
            {
                Vector2 p = points[i];
                Vector2 q = points[(i + 1) % points.Length];
                float cross = (q.x - p.x) * (point.y - p.y) - (q.y - p.y) * (point.x - p.x);
                if (cross * orientation < 0f)
                {
                    return false;
                }
            }
            return true;
        }

        // 辺のキー（頂点インデックスの昇順ペア）
        private static (int, int) MakeEdgeKey(int indexA, int indexB)
        {
            return indexA < indexB ? (indexA, indexB) : (indexB, indexA);
        }

        // 指定した辺の中点に頂点を追加し、全ての三角形を分割された辺に合わせて再構成したメッシュを返す
        // parents は入力メッシュの三角形番号 → 元の三角形番号で、出力メッシュに合わせた対応表を newParents に返す
        private static Mesh SplitEdges(Mesh mesh, HashSet<(int, int)> splitEdges, List<int[]> parents, out List<int[]> newParents)
        {
            // 頂点属性を読み出す（Unity の Mesh プロパティは呼び出し毎に配列をコピーするため一度だけ読む）
            int originalVertexCount = mesh.vertexCount;
            List<Vector3> vertices = new List<Vector3>(mesh.vertices);
            List<Vector3> normals = new List<Vector3>(mesh.normals);
            List<Vector4> tangents = new List<Vector4>(mesh.tangents);
            List<Vector2> uv = new List<Vector2>(mesh.uv);
            List<Vector2> uv2 = new List<Vector2>(mesh.uv2);
            List<Vector2> uv3 = new List<Vector2>(mesh.uv3);
            List<Vector2> uv4 = new List<Vector2>(mesh.uv4);
            List<Color> colors = new List<Color>(mesh.colors);
            List<BoneWeight> boneWeights = new List<BoneWeight>(mesh.boneWeights);

            // 分割する辺ごとに中点頂点を1つ追加する（辺を共有する三角形・サブメッシュ間で共通）
            List<(int, int)> edges = new List<(int, int)>(splitEdges);
            edges.Sort();
            Dictionary<(int, int), int> midpointIndexMap = new Dictionary<(int, int), int>();
            foreach ((int indexA, int indexB) in edges)
            {
                midpointIndexMap[(indexA, indexB)] = vertices.Count;
                AddMidpointVertex(indexA, indexB, vertices, normals, tangents, uv, uv2, uv3, uv4, colors, boneWeights);
            }

            Mesh refinedMesh = new Mesh();
            refinedMesh.name = mesh.name;
            refinedMesh.indexFormat = vertices.Count > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : mesh.indexFormat;
            refinedMesh.SetVertices(vertices);
            if (normals.Count > 0) refinedMesh.SetNormals(normals);
            if (tangents.Count > 0) refinedMesh.SetTangents(tangents);
            if (uv.Count > 0) refinedMesh.SetUVs(0, uv);
            if (uv2.Count > 0) refinedMesh.SetUVs(1, uv2);
            if (uv3.Count > 0) refinedMesh.SetUVs(2, uv3);
            if (uv4.Count > 0) refinedMesh.SetUVs(3, uv4);
            if (colors.Count > 0) refinedMesh.SetColors(colors);
            if (boneWeights.Count > 0) refinedMesh.boneWeights = boneWeights.ToArray();
            refinedMesh.bindposes = mesh.bindposes;

            // 各サブメッシュの三角形を、分割された辺の数に応じて再構成する（巻き順は保たれる）
            refinedMesh.subMeshCount = mesh.subMeshCount;
            newParents = new List<int[]>(mesh.subMeshCount);
            for (int subMeshIndex = 0; subMeshIndex < mesh.subMeshCount; subMeshIndex++)
            {
                int[] triangles = mesh.GetTriangles(subMeshIndex);
                List<int> newTriangles = new List<int>(triangles.Length * 2);
                List<int> newTriangleParents = new List<int>(triangles.Length);
                for (int i = 0; i < triangles.Length; i += 3)
                {
                    int countBefore = newTriangles.Count / 3;
                    EmitTriangle(triangles[i], triangles[i + 1], triangles[i + 2], midpointIndexMap, vertices, newTriangles);
                    for (int k = countBefore; k < newTriangles.Count / 3; k++)
                    {
                        newTriangleParents.Add(parents[subMeshIndex][i / 3]);
                    }
                }
                refinedMesh.SetTriangles(newTriangles, subMeshIndex);
                newParents.Add(newTriangleParents.ToArray());
            }
            refinedMesh.bounds = mesh.bounds;

            CopyBlendShapes(mesh, refinedMesh, edges, originalVertexCount);

            return refinedMesh;
        }

        // 2頂点の中点の頂点属性を各リストに追加する（Mesh の属性配列は空か全頂点分なので、空でないものだけ追加する）
        private static void AddMidpointVertex(int indexA, int indexB,
                                              List<Vector3> vertices, List<Vector3> normals, List<Vector4> tangents,
                                              List<Vector2> uv, List<Vector2> uv2, List<Vector2> uv3, List<Vector2> uv4,
                                              List<Color> colors, List<BoneWeight> boneWeights)
        {
            vertices.Add(Vector3.Lerp(vertices[indexA], vertices[indexB], 0.5f));

            if (normals.Count > 0)
            {
                Vector3 normal = Vector3.Lerp(normals[indexA], normals[indexB], 0.5f);
                normals.Add(normal.sqrMagnitude > 0f ? normal.normalized : normals[indexA]);
            }
            if (tangents.Count > 0)
            {
                // xyz のみ補間し、w（従法線の向き）は補間せず先頭側の頂点の値を使う
                Vector4 tangentA = tangents[indexA];
                Vector4 tangentB = tangents[indexB];
                Vector3 tangent = Vector3.Lerp(new Vector3(tangentA.x, tangentA.y, tangentA.z), new Vector3(tangentB.x, tangentB.y, tangentB.z), 0.5f);
                if (tangent.sqrMagnitude > 0f) tangent.Normalize();
                tangents.Add(new Vector4(tangent.x, tangent.y, tangent.z, tangentA.w));
            }
            if (uv.Count > 0)
                uv.Add(Vector2.Lerp(uv[indexA], uv[indexB], 0.5f));
            if (uv2.Count > 0)
                uv2.Add(Vector2.Lerp(uv2[indexA], uv2[indexB], 0.5f));
            if (uv3.Count > 0)
                uv3.Add(Vector2.Lerp(uv3[indexA], uv3[indexB], 0.5f));
            if (uv4.Count > 0)
                uv4.Add(Vector2.Lerp(uv4[indexA], uv4[indexB], 0.5f));
            if (colors.Count > 0)
                colors.Add(Color.Lerp(colors[indexA], colors[indexB], 0.5f));
            if (boneWeights.Count > 0)
                boneWeights.Add(BoneWeightUtils.LerpBoneWeight(boneWeights[indexA], boneWeights[indexB], 0.5f));
        }

        // 三角形を、分割された辺の数（0〜3）に応じて 1, 2, 3, 4 個の三角形として出力する
        private static void EmitTriangle(int indexA, int indexB, int indexC, Dictionary<(int, int), int> midpointIndexMap,
                                         List<Vector3> vertices, List<int> output)
        {
            // v[i] と v[(i + 1) % 3] を結ぶ辺の中点が m[i]（無ければ -1）
            int[] v = { indexA, indexB, indexC };
            int[] m =
            {
                GetMidpointIndex(midpointIndexMap, indexA, indexB),
                GetMidpointIndex(midpointIndexMap, indexB, indexC),
                GetMidpointIndex(midpointIndexMap, indexC, indexA)
            };
            int splitCount = (m[0] >= 0 ? 1 : 0) + (m[1] >= 0 ? 1 : 0) + (m[2] >= 0 ? 1 : 0);

            // 分割無し: そのまま
            if (splitCount == 0)
            {
                output.Add(v[0]); output.Add(v[1]); output.Add(v[2]);
                return;
            }
            // 3辺分割: 4分割
            if (splitCount == 3)
            {
                output.Add(v[0]); output.Add(m[0]); output.Add(m[2]);
                output.Add(m[0]); output.Add(v[1]); output.Add(m[1]);
                output.Add(m[2]); output.Add(m[1]); output.Add(v[2]);
                output.Add(m[0]); output.Add(m[1]); output.Add(m[2]);
                return;
            }

            // 1辺または2辺分割: 分割された辺が m[0]（2辺なら m[0] と m[1]）になるように回転する（巻き順は変わらない）
            int rotation = 0;
            if (splitCount == 1)
            {
                while (m[rotation] < 0) rotation++;
            }
            else
            {
                while (m[rotation] >= 0) rotation++;   // 分割されていない辺を探す
                rotation = (rotation + 1) % 3;
            }
            v = new[] { v[rotation], v[(rotation + 1) % 3], v[(rotation + 2) % 3] };
            m = new[] { m[rotation], m[(rotation + 1) % 3], m[(rotation + 2) % 3] };

            if (splitCount == 1)
            {
                // 2分割
                output.Add(v[0]); output.Add(m[0]); output.Add(v[2]);
                output.Add(m[0]); output.Add(v[1]); output.Add(v[2]);
            }
            else
            {
                // 3分割: 頂点 v[1] の角を切り出し、残る四角形 (v[0], m[0], m[1], v[2]) は短い方の対角線で分ける
                output.Add(m[0]); output.Add(v[1]); output.Add(m[1]);
                float diagonalA = (vertices[v[0]] - vertices[m[1]]).sqrMagnitude;
                float diagonalB = (vertices[m[0]] - vertices[v[2]]).sqrMagnitude;
                if (diagonalA <= diagonalB)
                {
                    output.Add(v[0]); output.Add(m[0]); output.Add(m[1]);
                    output.Add(v[0]); output.Add(m[1]); output.Add(v[2]);
                }
                else
                {
                    output.Add(v[0]); output.Add(m[0]); output.Add(v[2]);
                    output.Add(m[0]); output.Add(m[1]); output.Add(v[2]);
                }
            }
        }

        // 辺の中点頂点インデックスを返す（分割されていない辺は -1）
        private static int GetMidpointIndex(Dictionary<(int, int), int> midpointIndexMap, int indexA, int indexB)
        {
            return midpointIndexMap.TryGetValue(MakeEdgeKey(indexA, indexB), out int midpointIndex) ? midpointIndex : -1;
        }

        // ブレンドシェイプを全フレームコピーし、中点頂点の差分を2頂点の差分の中間値で補完する
        private static void CopyBlendShapes(Mesh sourceMesh, Mesh targetMesh, List<(int, int)> midpointEdges, int originalVertexCount)
        {
            int newVertexCount = originalVertexCount + midpointEdges.Count;
            for (int i = 0; i < sourceMesh.blendShapeCount; i++)
            {
                string blendShapeName = sourceMesh.GetBlendShapeName(i);
                int frameCount = sourceMesh.GetBlendShapeFrameCount(i);
                for (int j = 0; j < frameCount; j++)
                {
                    float frameWeight = sourceMesh.GetBlendShapeFrameWeight(i, j);
                    // 頂点数0のフレームも、GetBlendShapeFrameVertices は全頂点分の（ゼロの）差分を返す
                    Vector3[] deltaVertices = new Vector3[originalVertexCount];
                    Vector3[] deltaNormals = new Vector3[originalVertexCount];
                    Vector3[] deltaTangents = new Vector3[originalVertexCount];
                    sourceMesh.GetBlendShapeFrameVertices(i, j, deltaVertices, deltaNormals, deltaTangents);

                    Vector3[] newDeltaVertices = new Vector3[newVertexCount];
                    Vector3[] newDeltaNormals = new Vector3[newVertexCount];
                    Vector3[] newDeltaTangents = new Vector3[newVertexCount];
                    Array.Copy(deltaVertices, newDeltaVertices, originalVertexCount);
                    Array.Copy(deltaNormals, newDeltaNormals, originalVertexCount);
                    Array.Copy(deltaTangents, newDeltaTangents, originalVertexCount);
                    for (int k = 0; k < midpointEdges.Count; k++)
                    {
                        (int indexA, int indexB) = midpointEdges[k];
                        int newIndex = originalVertexCount + k;
                        newDeltaVertices[newIndex] = Vector3.Lerp(deltaVertices[indexA], deltaVertices[indexB], 0.5f);
                        newDeltaNormals[newIndex] = Vector3.Lerp(deltaNormals[indexA], deltaNormals[indexB], 0.5f);
                        newDeltaTangents[newIndex] = Vector3.Lerp(deltaTangents[indexA], deltaTangents[indexB], 0.5f);
                    }
                    targetMesh.AddBlendShapeFrame(blendShapeName, frameWeight, newDeltaVertices, newDeltaNormals, newDeltaTangents);
                }
            }
        }
    }
}
