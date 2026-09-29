using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace MeshDeletionTool
{
    // テクスチャの透明部分に基づく頂点の削除と、辺上の境界点での三角形の切断（MeshDeletionToolForTexture の処理本体。Mesh には依存しない）
    // subMeshMasks[i] はサブメッシュ i のテクスチャのアルファ値（テクスチャが無ければ null）、targetSubMeshes[i] は処理対象かどうか
    public class AlphaMeshCutter
    {
        // アルファ値がこの値より小さいメッシュは削除する
        public float AlphaThreshold = 0.5F;

        // テクスチャに基づいて削除すべき頂点のインデックスを取得するメソッド（降順）
        public List<int> GetVerticesToRemove(MeshArrays originalMesh, AlphaMask[] subMeshMasks)
        {
            List<int> removeVerticesIndexs = new List<int>();

            int subMeshCount = originalMesh.SubMeshCount;
            List<HashSet<int>> subMeshTrianglesList = new List<HashSet<int>>();

            // 各サブメッシュの三角形リストを取得
            for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
            {
                int[] triangles = originalMesh.GetTriangles(subMeshIndex);
                HashSet<int> triangleSet = new HashSet<int>(triangles);
                subMeshTrianglesList.Add(triangleSet);
            }

            // 各頂点を確認し、削除対象かどうかを判定
            for (int vertexIndex = 0; vertexIndex < originalMesh.VertexCount; vertexIndex++)
            {
                bool vertexShouldBeRemoved = false;

                for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
                {
                    if (subMeshTrianglesList[subMeshIndex].Contains(vertexIndex))
                    {
                        Vector2 uv = originalMesh.UV[vertexIndex];
                        AlphaMask mask = subMeshIndex < subMeshMasks.Length ? subMeshMasks[subMeshIndex] : null;
                        if (mask != null)
                        {
                            // ピクセルのアルファ値がalphaThresholdより小さいなら頂点を削除対象とする
                            if (AlphaSampling.IsTransparent(mask, uv, AlphaThreshold))
                            {
                                vertexShouldBeRemoved = true;
                                break;
                            }
                        }
                    }
                }

                // 削除対象ならリストに追加
                if (vertexShouldBeRemoved)
                {
                    removeVerticesIndexs.Add(vertexIndex);
                }
            }

            // 削除対象のインデックスを降順にソート
            removeVerticesIndexs.Sort((a, b) => b - a);
            return removeVerticesIndexs;
        }

        // 頂点削除と頂点追加を行いテクスチャに合わせたメッシュ形状に編集する
        // removeVerticesIndexs から処理対象外のサブメッシュに含まれる頂点は取り除かれる（呼び出し側のリストを変更する）
        // sourceTriangleIndices にはサブメッシュ毎の、出力三角形番号 → originalMesh の三角形番号を返す
        public MeshArrays Cut(MeshArrays originalMesh, AlphaMask[] subMeshMasks, bool[] targetSubMeshes, List<int> removeVerticesIndexs,
                              out List<int[]> sourceTriangleIndices)
        {
            MeshData newMeshData = new MeshData();
            sourceTriangleIndices = new List<int[]>(originalMesh.SubMeshCount);

            // 新規追加頂点の重複を避けるためにマッピング（辺の頂点インデックスの昇順ペアをキーとし、辺を共有する三角形で同じ頂点を使う）
            Dictionary<(int, int), int> edgeVertexIndexMap = new Dictionary<(int, int), int>();

            // 新規追加頂点を補完するための２点頂点インデックスと重みを、新規頂点インデックスをキーとして保持
            Dictionary<int, (int, int, float)> vertexInterpolation = new Dictionary<int, (int, int, float)>();

            // 出力メッシュ（new Mesh() と同じく名前は空。インデックス形式は元のまま、頂点数が 65,535 を超えれば Mesh 作成時に 32 ビットになる）
            MeshArrays newMesh = new MeshArrays { IndexFormat = originalMesh.IndexFormat };


            // 処理されないサブメッシュに含まれる頂点インデックスを収集
            HashSet<int> nonRemoveVerticesIndexs = new HashSet<int>();
            for (int subMeshIndex = 0; subMeshIndex < originalMesh.SubMeshCount; subMeshIndex++)
            {
                if (targetSubMeshes[subMeshIndex] == false)
                {
                    int[] triangles = originalMesh.GetTriangles(subMeshIndex);
                    foreach (int index in triangles)
                    {
                        nonRemoveVerticesIndexs.Add(index);
                    }
                }
            }

            // removeVerticesIndexsからnonRemoveVerticesIndexsに含まれるインデックスを削除
            removeVerticesIndexs.RemoveAll(index => nonRemoveVerticesIndexs.Contains(index));

            // 不要頂点を削除する
            for (int index = 0; index < originalMesh.VertexCount; index++)
            {
                if (removeVerticesIndexs.Contains(index))
                    continue;
                newMeshData.AddElementFromMesh(originalMesh, index);
            }

            // インデックスマッピングの作成
            Dictionary<int, int> oldToNewIndexMap = CreateIndexMap(originalMesh, removeVerticesIndexs);


            int subMeshCount = originalMesh.SubMeshCount;
            List<List<int>> newSubMeshTrianglesList = new List<List<int>>(subMeshCount);

            // サブメッシュ毎に三角ポリゴンを処理する
            for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
            {
                AlphaMask mask = subMeshIndex < subMeshMasks.Length ? subMeshMasks[subMeshIndex] : null;

                int[] triangles = originalMesh.GetTriangles(subMeshIndex);
                List<int> newSubMeshTriangles = new List<int>();
                List<int> newSubMeshTriangleSources = new List<int>(triangles.Length / 3);

                // 現在のサブメッシュが処理対象なら
                if (targetSubMeshes[subMeshIndex] == true)
                {
                    // 各三角形を確認し、必要に応じて新しい頂点を追加
                    for (int i = 0; i < triangles.Length; i += 3)
                    {
                        int outputCountBefore = newSubMeshTriangles.Count / 3;
                        // 三角ポリゴンを構成する頂点インデックスと削除情報を含んだタプルを作成
                        List<(int index, bool isRemoved)> triangleIndexs = new List<(int index, bool isRemoved)>
                        {
                            (triangles[i], removeVerticesIndexs.Contains(triangles[i])),
                            (triangles[i + 1], removeVerticesIndexs.Contains(triangles[i + 1])),
                            (triangles[i + 2], removeVerticesIndexs.Contains(triangles[i + 2]))
                        };

                        // 全ての頂点が削除対象の場合、三角形を追加しない
                        if (triangleIndexs[0].isRemoved &&
                            triangleIndexs[1].isRemoved &&
                            triangleIndexs[2].isRemoved)
                        {
                            continue;
                        }
                        // いずれの頂点も削除対象でない場合、三角形をそのまま追加
                        else if ( !triangleIndexs[0].isRemoved &&
                                !triangleIndexs[1].isRemoved &&
                                !triangleIndexs[2].isRemoved)
                        {
                            // 先に不要頂点を削除しているため頂点インデックスを変換する必要がある
                            newSubMeshTriangles.Add(oldToNewIndexMap[triangleIndexs[0].index]);
                            newSubMeshTriangles.Add(oldToNewIndexMap[triangleIndexs[1].index]);
                            newSubMeshTriangles.Add(oldToNewIndexMap[triangleIndexs[2].index]);
                        }
                        // 一部の頂点が削除対象の場合
                        else
                        {
                            // 削除対象でない頂点を多角形頂点に追加
                            (List<Vector3> originVertices, List<int> polygonToGlobalIndexMap) =
                                addNonDeletableVertexToPolygon(originalMesh, oldToNewIndexMap, triangleIndexs);

                            // 辺上の新規頂点座標と、シェイプキー用補完重みを計算
                            (MeshData addMeshData, List<(int, int, float)> localVertexInterpolation, List<int> crossedSides) =
                                addNewVertexToEdge(originalMesh, mask, triangleIndexs);

                            // 追加頂点の中で重複が無いように全体メッシュへ頂点を追加する（既存頂点はシームなどで重複がある）
                            // シェイプキー用補完重みも同様に重複を排除する
                            addUniqueMeshData(addMeshData, newMeshData, polygonToGlobalIndexMap, edgeVertexIndexMap,
                                            localVertexInterpolation, vertexInterpolation);

                            // 処理対象の多角形の外形頂点としてまとめる（残す頂点、辺上の新規頂点の順）
                            List<Vector3> polygonVertices = new List<Vector3>();
                            polygonVertices.AddRange(originVertices);
                            polygonVertices.AddRange(addMeshData.Vertices);

                            // 多角形頂点を三角形の外周順（元の巻き順）に並べ替える
                            List<int> outline = createPolygonOutline(triangleIndexs, crossedSides);
                            List<Vector3> outlineVertices = outline.Select(k => polygonVertices[k]).ToList();
                            List<int> outlineToGlobalIndexMap = outline.Select(k => polygonToGlobalIndexMap[k]).ToList();

                            // 多角形頂点から三角ポリゴンに変換し頂点インデックス配列を返す
                            int[] triangulatedIndices = createTriangleFromPolygon(originalMesh, triangleIndexs, outlineVertices);
                            // 三角ポリゴンの頂点インデックス配列を全体頂点インデックスに変換する
                            List<int> polygonTriangles = convertIndexToGlobal(triangulatedIndices, outlineToGlobalIndexMap);
                            // サブメッシュの三角ポリゴン配列に追加
                            newSubMeshTriangles.AddRange(polygonTriangles);
                        }

                        // この三角形から生成された出力三角形の元の三角形番号を記録する
                        for (int k = outputCountBefore; k < newSubMeshTriangles.Count / 3; k++)
                        {
                            newSubMeshTriangleSources.Add(i / 3);
                        }
                    }
                }
                // 現在のサブメッシュが処理対象でないなら
                else
                {
                    // 三角ポリゴンのインデックスを新しい頂点インデックスに更新する
                    for (int i = 0; i < triangles.Length; i++)
                    {
                        newSubMeshTriangles.Add(oldToNewIndexMap[triangles[i]]);
                    }
                    for (int i = 0; i < triangles.Length / 3; i++)
                    {
                        newSubMeshTriangleSources.Add(i);
                    }
                }
                sourceTriangleIndices.Add(newSubMeshTriangleSources.ToArray());
                newSubMeshTrianglesList.Add(newSubMeshTriangles);
            }

            // 全サブメッシュの頂点が出揃ってから頂点属性と三角形を設定する
            newMeshData.CopyTo(newMesh);
            newMesh.QuantizeColors();   // Mesh と同じ 8 ビット精度にする
            newMesh.SubMeshTriangles = new int[subMeshCount][];
            for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
            {
                newMesh.SubMeshTriangles[subMeshIndex] = newSubMeshTrianglesList[subMeshIndex].ToArray();
            }
            newMesh.Bindposes = originalMesh.Bindposes;

            CompletionBlendShapes(originalMesh, removeVerticesIndexs, newMesh, vertexInterpolation);

            return newMesh;
        }

        // インデックスマッピングの作成
        private Dictionary<int, int> CreateIndexMap(MeshArrays originalMesh, List<int> removeVerticesIndexs)
        {
            Dictionary<int, int> oldToNewIndexMap = new Dictionary<int, int>();
            for (int oldIndex = 0, newIndex = 0; oldIndex < originalMesh.VertexCount; oldIndex++)
            {
                if (!removeVerticesIndexs.Contains(oldIndex))
                {
                    oldToNewIndexMap[oldIndex] = newIndex;
                    newIndex++;
                }
            }
            return oldToNewIndexMap;
        }

        // 削除対象でない頂点を多角形頂点に追加
        private (List<Vector3>, List<int>) addNonDeletableVertexToPolygon(MeshArrays originalMesh,
                                                                          Dictionary<int, int> oldToNewIndexMap,
                                                                          List<(int index, bool isRemoved)> triangleIndexs)
        {
            List<Vector3> originVertices = new List<Vector3>(); //処理対象の多角形の外形頂点
            List<int> polygonToGlobalIndexMap = new List<int>();

            for (int index = 0; index < 3; index++) {
                if (!triangleIndexs[index].isRemoved) // 削除対象でない頂点を多角形頂点に追加
                {
                    originVertices.Add(originalMesh.Vertices[triangleIndexs[index].index]);
                    polygonToGlobalIndexMap.Add(oldToNewIndexMap[triangleIndexs[index].index]);
                }
            }
            return (originVertices, polygonToGlobalIndexMap);
        }

        // 辺への新規頂点追加（境界点が見つかった辺の番号 0〜2 も返す）
        private (MeshData, List<(int, int, float)>, List<int>) addNewVertexToEdge(MeshArrays originalMesh, AlphaMask mask,
                                                                                  List<(int index, bool isRemoved)> triangleIndexs)
        {
            MeshData addMeshData = new MeshData();
            List<(int, int, float)> localVertexInterpolation = new List<(int, int, float)>();
            List<int> crossedSides = new List<int>();

            if (mask != null)
            {
                List<int[]> sideIndexs = new List<int[]>(){
                    new int[] { triangleIndexs[0].index, triangleIndexs[1].index },
                    new int[] { triangleIndexs[1].index, triangleIndexs[2].index },
                    new int[] { triangleIndexs[2].index, triangleIndexs[0].index }
                };
                // 三角形の各辺に対して、テクスチャ境界値の座標&UV座標の計算
                for (int triangleIndex = 0; triangleIndex < 3; triangleIndex++)
                {
                    (MeshData newMeshDataVertex, (int, int, float) interpolation) =
                        AddEdgeIntersectionPoints(originalMesh, mask, sideIndexs[triangleIndex]);
                    if (newMeshDataVertex.Vertices.Count > 0) // テクスチャ境界値があるなら
                    {
                        // ２つの頂点（インデックス昇順）と重みを保存
                        localVertexInterpolation.Add(interpolation);
                        addMeshData.Add(newMeshDataVertex); //多角形頂点に追加
                        crossedSides.Add(triangleIndex);
                    }
                }
            }
            return (addMeshData, localVertexInterpolation, crossedSides);
        }

        // originalMeshのエッジとテクスチャの境界点を検出し、新しい頂点のMeshDataと補完情報（両端の頂点インデックス昇順, 重み）を返す関数
        private (MeshData, (int, int, float)) AddEdgeIntersectionPoints(MeshArrays originalMesh, AlphaMask mask, int[] indexs)
        {
            // エッジの両端点を頂点インデックスの昇順に並べる
            // （辺を共有する三角形は辺を逆向きに辿るため、向きに依存する二分探索では境界点が1ulp程度ずれて別の頂点になっていた）
            int[] edge = indexs[0] < indexs[1] ? new int[] { indexs[0], indexs[1] } : new int[] { indexs[1], indexs[0] };

            // エッジの両端点のUV座標を取得
            Vector2 uv1 = originalMesh.UV[edge[0]];
            Vector2 uv2 = originalMesh.UV[edge[1]];
            MeshData newMeshDataVertex = new MeshData();
            float weight = 0;

            // エッジが境界エッジかどうかを判定
            if (AlphaSampling.IsBoundaryEdge(mask, uv1, uv2, AlphaThreshold))
            {
                // 境界エッジの場合、境界点のUV座標と頂点座標を計算
                weight = AlphaSampling.FindAlphaBoundary(mask, uv1, uv2, AlphaThreshold);
                newMeshDataVertex = VertexCompletion(originalMesh, edge, weight);
            }

            return (newMeshDataVertex, (edge[0], edge[1], weight));
        }

        // ２つの頂点インデックスと重みから線形補完したMeshDataを返す
        private MeshData VertexCompletion(MeshArrays originalMesh, int[] indexs, float weight)
        {
            MeshData newMeshDataVertex = new MeshData();

            newMeshDataVertex.Vertices.Add(Vector3.Lerp(originalMesh.Vertices[indexs[0]], originalMesh.Vertices[indexs[1]], weight));
            newMeshDataVertex.UV.Add(Vector2.Lerp(originalMesh.UV[indexs[0]], originalMesh.UV[indexs[1]], weight));

            // 法線は補間後に正規化し、接線は補間後の法線と直交させて正規化する。接線の w（従法線の向き）は補間せず重みの近い側の値を使う
            Vector3 normal = Vector3.zero;
            if (indexs[0] < originalMesh.Normals.Length && indexs[1] < originalMesh.Normals.Length)
            {
                normal = VertexAttributeUtils.LerpNormal(originalMesh.Normals[indexs[0]], originalMesh.Normals[indexs[1]], weight);
                newMeshDataVertex.Normals.Add(normal);
            }
            if (indexs[0] < originalMesh.Tangents.Length && indexs[1] < originalMesh.Tangents.Length)
                newMeshDataVertex.Tangents.Add(VertexAttributeUtils.LerpTangent(originalMesh.Tangents[indexs[0]], originalMesh.Tangents[indexs[1]], weight, normal));

            if (indexs[0] < originalMesh.UV2.Length && indexs[1] < originalMesh.UV2.Length)
                newMeshDataVertex.UV2.Add(Vector2.Lerp(originalMesh.UV2[indexs[0]], originalMesh.UV2[indexs[1]], weight));
            if (indexs[0] < originalMesh.UV3.Length && indexs[1] < originalMesh.UV3.Length)
                newMeshDataVertex.UV3.Add(Vector2.Lerp(originalMesh.UV3[indexs[0]], originalMesh.UV3[indexs[1]], weight));
            if (indexs[0] < originalMesh.UV4.Length && indexs[1] < originalMesh.UV4.Length)
                newMeshDataVertex.UV4.Add(Vector2.Lerp(originalMesh.UV4[indexs[0]], originalMesh.UV4[indexs[1]], weight));
            if (indexs[0] < originalMesh.UV5.Length && indexs[1] < originalMesh.UV5.Length)
                newMeshDataVertex.UV5.Add(Vector2.Lerp(originalMesh.UV5[indexs[0]], originalMesh.UV5[indexs[1]], weight));
            if (indexs[0] < originalMesh.UV6.Length && indexs[1] < originalMesh.UV6.Length)
                newMeshDataVertex.UV6.Add(Vector2.Lerp(originalMesh.UV6[indexs[0]], originalMesh.UV6[indexs[1]], weight));
            if (indexs[0] < originalMesh.UV7.Length && indexs[1] < originalMesh.UV7.Length)
                newMeshDataVertex.UV7.Add(Vector2.Lerp(originalMesh.UV7[indexs[0]], originalMesh.UV7[indexs[1]], weight));
            if (indexs[0] < originalMesh.UV8.Length && indexs[1] < originalMesh.UV8.Length)
                newMeshDataVertex.UV8.Add(Vector2.Lerp(originalMesh.UV8[indexs[0]], originalMesh.UV8[indexs[1]], weight));

            // 頂点カラーは Mesh 上では 8 ビットの1つのストリーム（colors と colors32 は同じデータ）なので Color として補間する
            if (indexs[0] < originalMesh.Colors.Length && indexs[1] < originalMesh.Colors.Length)
                newMeshDataVertex.Colors.Add(Color.Lerp(originalMesh.Colors[indexs[0]], originalMesh.Colors[indexs[1]], weight));

            if (indexs[0] < originalMesh.BoneWeights.Length && indexs[1] < originalMesh.BoneWeights.Length)
            {
                BoneWeight BoneWeightLerp= BoneWeightUtils.LerpBoneWeight(originalMesh.BoneWeights[indexs[0]], originalMesh.BoneWeights[indexs[1]], weight);
                newMeshDataVertex.BoneWeights.Add(BoneWeightLerp);
            }
            return newMeshDataVertex;
        }

        // 追加頂点の中で重複が無いように全体メッシュへ追加する
        // 同じ辺（頂点インデックスのペア）上の境界点は1つの頂点として共有する。辺を頂点インデックスで判定するため、
        // シーム（座標は同じだが頂点インデックスが異なる辺）の両側には別々の頂点が作られ、UVなどの属性は混ざらない
        private void addUniqueMeshData(MeshData addMeshData, MeshData newMeshData, List<int> polygonToGlobalIndexMap,
                                       Dictionary<(int, int), int> edgeVertexIndexMap,
                                       List<(int, int, float)> localVertexInterpolation,
                                       Dictionary<int, (int, int, float)> vertexInterpolation)
        {
            for (int j = 0; j < addMeshData.Vertices.Count; j++)
            {
                (int indexA, int indexB, float weight) = localVertexInterpolation[j];
                if (edgeVertexIndexMap.TryGetValue((indexA, indexB), out int existingIndex))
                {
                    // 既に同じ辺に頂点が追加されているならそれを使う
                    polygonToGlobalIndexMap.Add(existingIndex);
                }
                else
                {
                    newMeshData.Add(addMeshData.GetElementAt(j));
                    int newIndex = newMeshData.Vertices.Count - 1;
                    edgeVertexIndexMap[(indexA, indexB)] = newIndex;
                    polygonToGlobalIndexMap.Add(newIndex);
                    vertexInterpolation.Add(newIndex, (indexA, indexB, weight));
                }
            }
        }

        // 多角形頂点（残す頂点、辺上の新規頂点の順）を三角形の外周順に並べたインデックス列を返す
        // 三角形の頂点 i を巡りながら、残す頂点なら追加し、続く辺 (i, i+1) に境界点があればそれを追加する
        private List<int> createPolygonOutline(List<(int index, bool isRemoved)> triangleIndexs, List<int> crossedSides)
        {
            int keptCount = triangleIndexs.Count(t => !t.isRemoved);
            List<int> outline = new List<int>(keptCount + crossedSides.Count);
            int keptCursor = 0;
            for (int i = 0; i < 3; i++)
            {
                if (!triangleIndexs[i].isRemoved)
                {
                    outline.Add(keptCursor++);
                }
                int crossing = crossedSides.IndexOf(i);
                if (crossing >= 0)
                {
                    outline.Add(keptCount + crossing);
                }
            }
            return outline;
        }

        // 外周順の多角形頂点から三角ポリゴンに変換し頂点配列を返す
        private int[] createTriangleFromPolygon(MeshArrays originalMesh, List<(int index, bool isRemoved)> triangleIndexs, List<Vector3> polygonVertices)
        {
            // 処理対象の三角ポリゴンから法線ベクトルを計算し、面の向きを指定する
            Vector3 a = originalMesh.Vertices[triangleIndexs[0].index];
            Vector3 b = originalMesh.Vertices[triangleIndexs[1].index];
            Vector3 c = originalMesh.Vertices[triangleIndexs[2].index];
            Vector3 normal = Vector3.Cross(b - a, c - a);
            // 耳切り法により、多角形外周頂点から三角ポリゴンに分割し、そのインデックス番号順を返す（巻き順は元の三角形と同じ）
            int[] triangulatedIndices = EarClipping2D.Triangulate(polygonVertices, normal);
            return triangulatedIndices;
        }

        // 多角形ポリゴンの頂点インデックスを全体頂点インデックスに変換する
        private List<int> convertIndexToGlobal(int[] triangulatedIndices, List<int> polygonToGlobalIndexMap)
        {
            List<int> polygonTriangles = new List<int>();
            for (int j = 0; j < triangulatedIndices.Length; j++)
            {
                int polygonIndex = triangulatedIndices[j];
                // 三角ポリゴンのインデックス番号を変換して追加
                polygonTriangles.Add(polygonToGlobalIndexMap[polygonIndex]);
            }
            return polygonTriangles;
        }

        // ブレンドシェイプを全フレームコピーし、削除された頂点を取り除き、辺上の新規頂点の差分を2頂点の差分の線形補間で補完する
        protected void CompletionBlendShapes(MeshArrays originalMesh, List<int> removeVerticesIndexs, MeshArrays newMesh, Dictionary<int, (int, int, float)> blendShapeInterpolation)
        {
            // 1つの頂点に対して：ブレンドシェイプの数×ブレンドシェイプのフレーム分の頂点、法線、接線情報が必要
            // 元のメッシュの全ブレンドシェイプに対して処理を行う
            foreach (BlendShapeData shape in originalMesh.BlendShapes)
            {
                BlendShapeData newShape = new BlendShapeData { Name = shape.Name };

                // 各フレームに対して処理を行う
                foreach (BlendShapeFrameData frame in shape.Frames)
                {
                    // フレームの頂点、法線、接線
                    Vector3[] frameVertices = frame.DeltaVertices;
                    Vector3[] frameNormals = frame.DeltaNormals;
                    Vector3[] frameTangents = frame.DeltaTangents;

                    // 配列をリストに変換
                    List<Vector3> frameVerticesList = new List<Vector3>(frameVertices);
                    List<Vector3> frameNormalsList = new List<Vector3>(frameNormals);
                    List<Vector3> frameTangentsList = new List<Vector3>(frameTangents);

                    // 指定されたインデックスの頂点、法線、接線をリストから削除
                    foreach (int index in removeVerticesIndexs)
                    {
                        frameVerticesList.RemoveAt(index);
                        frameNormalsList.RemoveAt(index);
                        frameTangentsList.RemoveAt(index);
                    }

                    // 補完処理の追加（新規頂点は頂点インデックスの順に並べる。差分値なので正規化はしない）
                    foreach (var kvp in blendShapeInterpolation.OrderBy(kvp => kvp.Key))
                    {
                        (int indexA, int indexB, float weight) = kvp.Value;

                        // 頂点の補完
                        frameVerticesList.Add(Vector3.Lerp(frameVertices[indexA], frameVertices[indexB], weight));
                        // 法線の補完
                        frameNormalsList.Add(Vector3.Lerp(frameNormals[indexA], frameNormals[indexB], weight));
                        // 接線の補完
                        frameTangentsList.Add(Vector3.Lerp(frameTangents[indexA], frameTangents[indexB], weight));
                    }

                    // 新しいメッシュにブレンドシェイプのフレームを追加
                    newShape.Frames.Add(new BlendShapeFrameData
                    {
                        Weight = frame.Weight,
                        DeltaVertices = frameVerticesList.ToArray(),
                        DeltaNormals = frameNormalsList.ToArray(),
                        DeltaTangents = frameTangentsList.ToArray()
                    });
                }
                newMesh.BlendShapes.Add(newShape);
            }
        }
    }
}
