using System.Collections.Generic;
using UnityEngine;

namespace MeshDeletionTool
{
    // 指定した頂点と、それを使う三角形を取り除いたメッシュを作る（MeshDeletionToolForBox の処理本体。Mesh には依存しない）
    public static class MeshVertexRemover
    {
        // removeVerticesIndexs は削除する頂点番号（降順）
        public static MeshArrays RemoveVertices(MeshArrays originalMesh, List<int> removeVerticesIndexs)
        {
            MeshData newMeshData = new MeshData();
            RemoveVerticesDatas(originalMesh, removeVerticesIndexs, newMeshData);

            MeshArrays newMesh = new MeshArrays
            {
                IndexFormat = originalMesh.IndexFormat,
                Bindposes = originalMesh.Bindposes
            };
            newMeshData.CopyTo(newMesh);

            // インデックスマッピングの作成
            Dictionary<int, int> oldToNewIndexMap = new Dictionary<int, int>();
            for (int oldIndex = 0, newIndex = 0; oldIndex < originalMesh.VertexCount; oldIndex++)
            {
                if (!removeVerticesIndexs.Contains(oldIndex))
                {
                    oldToNewIndexMap[oldIndex] = newIndex;
                    newIndex++;
                }
            }

            // 従来の処理は全サブメッシュの三角形を連結して Mesh.triangles に設定した後、三角形の残るサブメッシュだけを個別に設定していた。
            // そのため三角形が全て削除されたサブメッシュ 0 には連結した全三角形が残る。この振る舞いをそのまま保っている
            List<int> allTriangles = new List<int>();
            RemoveTriangles(originalMesh.GetAllTriangles(), removeVerticesIndexs, oldToNewIndexMap, allTriangles);
            newMesh.SubMeshTriangles = new int[originalMesh.SubMeshCount][];
            for (int subMeshIndex = 0; subMeshIndex < originalMesh.SubMeshCount; subMeshIndex++)
            {
                newMesh.SubMeshTriangles[subMeshIndex] = subMeshIndex == 0 ? allTriangles.ToArray() : new int[0];
            }
            RemoveSubMeshes(originalMesh, removeVerticesIndexs, oldToNewIndexMap, newMesh);
            RemoveBlendShapes(originalMesh, removeVerticesIndexs, newMesh);

            return newMesh;
        }

        private static void RemoveVerticesDatas(MeshArrays originalMesh, List<int> removeVerticesIndexs, MeshData newMeshData)
        {
            HashSet<int> removeVerticesIndexsSet = new HashSet<int>(removeVerticesIndexs);

            for (int index = 0; index < originalMesh.VertexCount; index++)
            {
                if (removeVerticesIndexsSet.Contains(index))
                    continue;

                newMeshData.AddElementFromMesh(originalMesh, index);
            }
        }

        private static void RemoveTriangles(int[] triangles, List<int> removeVerticesIndexs, Dictionary<int, int> oldToNewIndexMap, List<int> newTrianglesList)
        {
            for (int i = 0; i < triangles.Length; i += 3)
            {
                int index0 = triangles[i];
                int index1 = triangles[i + 1];
                int index2 = triangles[i + 2];

                if (!removeVerticesIndexs.Contains(index0) &&
                    !removeVerticesIndexs.Contains(index1) &&
                    !removeVerticesIndexs.Contains(index2))
                {
                    newTrianglesList.Add(oldToNewIndexMap[index0]);
                    newTrianglesList.Add(oldToNewIndexMap[index1]);
                    newTrianglesList.Add(oldToNewIndexMap[index2]);
                }
            }
        }

        private static void RemoveSubMeshes(MeshArrays originalMesh, List<int> removeVerticesIndexs, Dictionary<int, int> oldToNewIndexMap, MeshArrays newMesh)
        {
            int subMeshCount = originalMesh.SubMeshCount;

            for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
            {
                List<int> newTriangles = new List<int>();
                RemoveTriangles(originalMesh.GetTriangles(subMeshIndex), removeVerticesIndexs, oldToNewIndexMap, newTriangles);

                if (newTriangles.Count > 0)
                {
                    newMesh.SubMeshTriangles[subMeshIndex] = newTriangles.ToArray();
                }
            }
        }

        private static void RemoveBlendShapes(MeshArrays originalMesh, List<int> removeVerticesIndexs, MeshArrays newMesh)
        {
            // 1つの頂点に対して：ブレンドシェイプの数×ブレンドシェイプのフレーム分の頂点、法線、接線情報が必要
            // 元のメッシュの全ブレンドシェイプに対して処理を行う
            foreach (BlendShapeData shape in originalMesh.BlendShapes)
            {
                BlendShapeData newShape = new BlendShapeData { Name = shape.Name };

                // 各フレームに対して処理を行う
                foreach (BlendShapeFrameData frame in shape.Frames)
                {
                    // 配列をリストに変換
                    List<Vector3> frameVerticesList = new List<Vector3>(frame.DeltaVertices);
                    List<Vector3> frameNormalsList = new List<Vector3>(frame.DeltaNormals);
                    List<Vector3> frameTangentsList = new List<Vector3>(frame.DeltaTangents);

                    // 指定されたインデックスの頂点、法線、接線をリストから削除
                    foreach (int index in removeVerticesIndexs)
                    {
                        frameVerticesList.RemoveAt(index);
                        frameNormalsList.RemoveAt(index);
                        frameTangentsList.RemoveAt(index);
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
