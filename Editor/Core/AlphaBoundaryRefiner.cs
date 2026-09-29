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

        // 実行結果（診断用）: 多角形の内部にあるか判定したテクセルの数と、そのうち不透明か判定したテクセルの数
        public long RasterTexelTests;
        public long RasterInsideTexels;

        // 要素毎の判定（三角形の細分化判定）の実行先。未設定なら初めて使うときに CPU を作る。GPU（ComputeStageBackend）でも同じ結果になる
        private IAlphaStageBackend backend;
        public IAlphaStageBackend Backend
        {
            get => backend ?? (backend = new CpuStageBackend());
            set => backend = value;
        }

        // アルファ境界付近の三角形を細分化したメッシュを返す
        // subMeshMasks[i] が null のサブメッシュは判定対象外（隣接する辺の分割にのみ追従する）
        // 細分化が不要な場合は元のメッシュをそのまま返す
        public MeshArrays Refine(MeshArrays mesh, AlphaMask[] subMeshMasks, float alphaThreshold)
        {
            TriangleCountPerDepth.Clear();
            MarkedTriangleCountPerDepth.Clear();
            ParentTriangleIndexPerSubMesh = CreateIdentityParents(mesh);
            RasterTexelTests = RasterInsideTexels = 0;
            CpuStageBackend cpuBackend = Backend as CpuStageBackend;
            long rasterTests0 = cpuBackend != null ? cpuBackend.RasterTexelTests : 0;
            long rasterInside0 = cpuBackend != null ? cpuBackend.RasterInsideTexels : 0;

            MeshArrays currentMesh = mesh;
            for (int depth = 0; depth < MaxDepth; depth++)
            {
                HashSet<(int, int)> splitEdges = CollectEdgesToSplit(currentMesh, subMeshMasks, alphaThreshold, out int markedCount);
                if (splitEdges.Count == 0)
                {
                    break;
                }
                currentMesh = SplitEdges(currentMesh, splitEdges, ParentTriangleIndexPerSubMesh, out ParentTriangleIndexPerSubMesh);
                MarkedTriangleCountPerDepth.Add(markedCount);
                TriangleCountPerDepth.Add(currentMesh.TriangleCount);
            }
            if (cpuBackend != null)
            {
                RasterTexelTests = cpuBackend.RasterTexelTests - rasterTests0;
                RasterInsideTexels = cpuBackend.RasterInsideTexels - rasterInside0;
            }
            return currentMesh;
        }

        // 各サブメッシュの三角形番号をそのまま親とする対応表を作る
        private static List<int[]> CreateIdentityParents(MeshArrays mesh)
        {
            List<int[]> parents = new List<int[]>(mesh.SubMeshCount);
            for (int subMeshIndex = 0; subMeshIndex < mesh.SubMeshCount; subMeshIndex++)
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
        // 判定そのもの（3頂点の透明判定、修正1のテクセルのラスタライズ、修正2の境界点の二分探索と削除側多角形のテクセル数）は
        // 全サブメッシュの三角形をまとめてバックエンド（StageKernelContext.RefineTriangleTest / HLSL の同名カーネル）で行う
        private HashSet<(int, int)> CollectEdgesToSplit(MeshArrays mesh, AlphaMask[] subMeshMasks, float alphaThreshold, out int markedCount)
        {
            HashSet<(int, int)> splitEdges = new HashSet<(int, int)>();
            markedCount = 0;

            AlphaMask[] masks = new AlphaMask[mesh.SubMeshCount];
            for (int subMeshIndex = 0; subMeshIndex < mesh.SubMeshCount; subMeshIndex++)
            {
                masks[subMeshIndex] = subMeshIndex < subMeshMasks.Length ? subMeshMasks[subMeshIndex] : null;
            }
            RefineTestParams settings = new RefineTestParams
            {
                RefineFullyTransparentTriangles = RefineFullyTransparentTriangles,
                RefinePartiallyCutTriangles = RefinePartiallyCutTriangles,
                ChordToleranceTexels = ChordToleranceTexels,
                MaxRasterSize = MaxRasterSize
            };
            bool[][] marked = Backend.TestRefineTriangles(mesh.UV, mesh.SubMeshTriangles, masks, settings, alphaThreshold);

            for (int subMeshIndex = 0; subMeshIndex < mesh.SubMeshCount; subMeshIndex++)
            {
                bool[] markedInSubMesh = marked[subMeshIndex];
                if (markedInSubMesh == null)
                {
                    continue;
                }
                int[] triangles = mesh.GetTriangles(subMeshIndex);
                for (int i = 0; i < triangles.Length; i += 3)
                {
                    if (markedInSubMesh[i / 3])
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

        // 辺のキー（頂点インデックスの昇順ペア）
        private static (int, int) MakeEdgeKey(int indexA, int indexB)
        {
            return indexA < indexB ? (indexA, indexB) : (indexB, indexA);
        }

        // 指定した辺の中点に頂点を追加し、全ての三角形を分割された辺に合わせて再構成したメッシュを返す
        // parents は入力メッシュの三角形番号 → 元の三角形番号で、出力メッシュに合わせた対応表を newParents に返す
        private static MeshArrays SplitEdges(MeshArrays mesh, HashSet<(int, int)> splitEdges, List<int[]> parents, out List<int[]> newParents)
        {
            // 頂点属性をリストに写す（中点頂点を末尾に追加する）
            int originalVertexCount = mesh.VertexCount;
            List<Vector3> vertices = new List<Vector3>(mesh.Vertices);
            List<Vector3> normals = new List<Vector3>(mesh.Normals);
            List<Vector4> tangents = new List<Vector4>(mesh.Tangents);
            List<Vector2> uv = new List<Vector2>(mesh.UV);
            List<Vector2> uv2 = new List<Vector2>(mesh.UV2);
            List<Vector2> uv3 = new List<Vector2>(mesh.UV3);
            List<Vector2> uv4 = new List<Vector2>(mesh.UV4);
            List<Vector2> uv5 = new List<Vector2>(mesh.UV5);
            List<Vector2> uv6 = new List<Vector2>(mesh.UV6);
            List<Vector2> uv7 = new List<Vector2>(mesh.UV7);
            List<Vector2> uv8 = new List<Vector2>(mesh.UV8);
            List<Color> colors = new List<Color>(mesh.Colors);
            List<BoneWeight> boneWeights = new List<BoneWeight>(mesh.BoneWeights);

            // 分割する辺ごとに中点頂点を1つ追加する（辺を共有する三角形・サブメッシュ間で共通）
            List<(int, int)> edges = new List<(int, int)>(splitEdges);
            edges.Sort();
            Dictionary<(int, int), int> midpointIndexMap = new Dictionary<(int, int), int>();
            foreach ((int indexA, int indexB) in edges)
            {
                midpointIndexMap[(indexA, indexB)] = vertices.Count;
                AddMidpointVertex(indexA, indexB, vertices, normals, tangents, uv, uv2, uv3, uv4, uv5, uv6, uv7, uv8, colors, boneWeights);
            }

            // 出力メッシュ（インデックス形式は元のまま。頂点数が 65,535 を超えれば Mesh 作成時に 32 ビットになる）
            MeshArrays refinedMesh = new MeshArrays();
            refinedMesh.Name = mesh.Name;
            refinedMesh.IndexFormat = mesh.IndexFormat;
            refinedMesh.Vertices = vertices.ToArray();
            refinedMesh.Normals = normals.ToArray();
            refinedMesh.Tangents = tangents.ToArray();
            refinedMesh.UV = uv.ToArray();
            refinedMesh.UV2 = uv2.ToArray();
            refinedMesh.UV3 = uv3.ToArray();
            refinedMesh.UV4 = uv4.ToArray();
            refinedMesh.UV5 = uv5.ToArray();
            refinedMesh.UV6 = uv6.ToArray();
            refinedMesh.UV7 = uv7.ToArray();
            refinedMesh.UV8 = uv8.ToArray();
            refinedMesh.Colors = colors.ToArray();
            refinedMesh.QuantizeColors();   // Mesh と同じ 8 ビット精度にする（次の深さの補間の入力になる）
            refinedMesh.BoneWeights = boneWeights.ToArray();
            refinedMesh.Bindposes = mesh.Bindposes;

            // 各サブメッシュの三角形を、分割された辺の数に応じて再構成する（巻き順は保たれる）
            refinedMesh.SubMeshTriangles = new int[mesh.SubMeshCount][];
            newParents = new List<int[]>(mesh.SubMeshCount);
            for (int subMeshIndex = 0; subMeshIndex < mesh.SubMeshCount; subMeshIndex++)
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
                refinedMesh.SubMeshTriangles[subMeshIndex] = newTriangles.ToArray();
                newParents.Add(newTriangleParents.ToArray());
            }
            refinedMesh.Bounds = mesh.Bounds;

            CopyBlendShapes(mesh, refinedMesh, edges, originalVertexCount);

            return refinedMesh;
        }

        // 2頂点の中点の頂点属性を各リストに追加する（Mesh の属性配列は空か全頂点分なので、空でないものだけ追加する）
        private static void AddMidpointVertex(int indexA, int indexB,
                                              List<Vector3> vertices, List<Vector3> normals, List<Vector4> tangents,
                                              List<Vector2> uv, List<Vector2> uv2, List<Vector2> uv3, List<Vector2> uv4,
                                              List<Vector2> uv5, List<Vector2> uv6, List<Vector2> uv7, List<Vector2> uv8,
                                              List<Color> colors, List<BoneWeight> boneWeights)
        {
            vertices.Add(Vector3.Lerp(vertices[indexA], vertices[indexB], 0.5f));

            // 法線と接線は削除処理の境界点頂点と同じ規則で補間する（接線は補間後の法線と直交させ、w は先頭側の頂点の値）
            Vector3 normal = Vector3.zero;
            if (normals.Count > 0)
            {
                normal = VertexAttributeUtils.LerpNormal(normals[indexA], normals[indexB], 0.5f);
                normals.Add(normal);
            }
            if (tangents.Count > 0)
                tangents.Add(VertexAttributeUtils.LerpTangent(tangents[indexA], tangents[indexB], 0.5f, normal));
            if (uv.Count > 0)
                uv.Add(Vector2.Lerp(uv[indexA], uv[indexB], 0.5f));
            if (uv2.Count > 0)
                uv2.Add(Vector2.Lerp(uv2[indexA], uv2[indexB], 0.5f));
            if (uv3.Count > 0)
                uv3.Add(Vector2.Lerp(uv3[indexA], uv3[indexB], 0.5f));
            if (uv4.Count > 0)
                uv4.Add(Vector2.Lerp(uv4[indexA], uv4[indexB], 0.5f));
            if (uv5.Count > 0)
                uv5.Add(Vector2.Lerp(uv5[indexA], uv5[indexB], 0.5f));
            if (uv6.Count > 0)
                uv6.Add(Vector2.Lerp(uv6[indexA], uv6[indexB], 0.5f));
            if (uv7.Count > 0)
                uv7.Add(Vector2.Lerp(uv7[indexA], uv7[indexB], 0.5f));
            if (uv8.Count > 0)
                uv8.Add(Vector2.Lerp(uv8[indexA], uv8[indexB], 0.5f));
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
        private static void CopyBlendShapes(MeshArrays sourceMesh, MeshArrays targetMesh, List<(int, int)> midpointEdges, int originalVertexCount)
        {
            int newVertexCount = originalVertexCount + midpointEdges.Count;
            foreach (BlendShapeData shape in sourceMesh.BlendShapes)
            {
                BlendShapeData newShape = new BlendShapeData { Name = shape.Name };
                foreach (BlendShapeFrameData frame in shape.Frames)
                {
                    Vector3[] deltaVertices = frame.DeltaVertices;
                    Vector3[] deltaNormals = frame.DeltaNormals;
                    Vector3[] deltaTangents = frame.DeltaTangents;

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
                    newShape.Frames.Add(new BlendShapeFrameData { Weight = frame.Weight, DeltaVertices = newDeltaVertices, DeltaNormals = newDeltaNormals, DeltaTangents = newDeltaTangents });
                }
                targetMesh.BlendShapes.Add(newShape);
            }
        }
    }
}
