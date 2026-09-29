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
            // 削除対象かどうかを頂点番号で直接参照できるようにする（リストの線形探索を三角形毎に繰り返さない）
            bool[] isRemoved = new bool[originalMesh.VertexCount];
            foreach (int index in removeVerticesIndexs)
            {
                isRemoved[index] = true;
            }

            MeshData newMeshData = new MeshData();
            for (int index = 0; index < originalMesh.VertexCount; index++)
            {
                if (isRemoved[index])
                    continue;

                newMeshData.AddElementFromMesh(originalMesh, index);
            }

            MeshArrays newMesh = new MeshArrays
            {
                IndexFormat = originalMesh.IndexFormat,
                Bindposes = originalMesh.Bindposes
            };
            newMeshData.CopyTo(newMesh);

            // インデックスマッピングの作成（元の頂点番号 → 削除後の頂点番号。削除された頂点は -1）
            int[] oldToNewIndexMap = new int[originalMesh.VertexCount];
            int[] keptIndices = new int[newMesh.VertexCount];
            for (int oldIndex = 0, newIndex = 0; oldIndex < originalMesh.VertexCount; oldIndex++)
            {
                if (!isRemoved[oldIndex])
                {
                    oldToNewIndexMap[oldIndex] = newIndex;
                    keptIndices[newIndex] = oldIndex;
                    newIndex++;
                }
                else
                {
                    oldToNewIndexMap[oldIndex] = -1;
                }
            }

            // 従来の処理は全サブメッシュの三角形を連結して Mesh.triangles に設定した後、三角形の残るサブメッシュだけを個別に設定していた。
            // そのため三角形が全て削除されたサブメッシュ 0 には連結した全三角形が残る。この振る舞いをそのまま保っている
            List<int> allTriangles = new List<int>();
            RemoveTriangles(originalMesh.GetAllTriangles(), isRemoved, oldToNewIndexMap, allTriangles);
            newMesh.SubMeshTriangles = new int[originalMesh.SubMeshCount][];
            for (int subMeshIndex = 0; subMeshIndex < originalMesh.SubMeshCount; subMeshIndex++)
            {
                newMesh.SubMeshTriangles[subMeshIndex] = subMeshIndex == 0 ? allTriangles.ToArray() : new int[0];
            }
            RemoveSubMeshes(originalMesh, isRemoved, oldToNewIndexMap, newMesh);
            RemoveBlendShapes(originalMesh, keptIndices, newMesh);

            return newMesh;
        }

        private static void RemoveTriangles(int[] triangles, bool[] isRemoved, int[] oldToNewIndexMap, List<int> newTrianglesList)
        {
            for (int i = 0; i < triangles.Length; i += 3)
            {
                int index0 = triangles[i];
                int index1 = triangles[i + 1];
                int index2 = triangles[i + 2];

                if (!isRemoved[index0] &&
                    !isRemoved[index1] &&
                    !isRemoved[index2])
                {
                    newTrianglesList.Add(oldToNewIndexMap[index0]);
                    newTrianglesList.Add(oldToNewIndexMap[index1]);
                    newTrianglesList.Add(oldToNewIndexMap[index2]);
                }
            }
        }

        private static void RemoveSubMeshes(MeshArrays originalMesh, bool[] isRemoved, int[] oldToNewIndexMap, MeshArrays newMesh)
        {
            int subMeshCount = originalMesh.SubMeshCount;

            for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
            {
                List<int> newTriangles = new List<int>();
                RemoveTriangles(originalMesh.GetTriangles(subMeshIndex), isRemoved, oldToNewIndexMap, newTriangles);

                if (newTriangles.Count > 0)
                {
                    newMesh.SubMeshTriangles[subMeshIndex] = newTriangles.ToArray();
                }
            }
        }

        // ブレンドシェイプを全フレームコピーし、削除された頂点の差分を取り除く（残る頂点の並びは元の頂点番号の昇順）
        private static void RemoveBlendShapes(MeshArrays originalMesh, int[] keptIndices, MeshArrays newMesh)
        {
            foreach (BlendShapeData shape in originalMesh.BlendShapes)
            {
                BlendShapeData newShape = new BlendShapeData { Name = shape.Name };
                foreach (BlendShapeFrameData frame in shape.Frames)
                {
                    newShape.Frames.Add(new BlendShapeFrameData
                    {
                        Weight = frame.Weight,
                        DeltaVertices = Compact(frame.DeltaVertices, keptIndices),
                        DeltaNormals = Compact(frame.DeltaNormals, keptIndices),
                        DeltaTangents = Compact(frame.DeltaTangents, keptIndices)
                    });
                }
                newMesh.BlendShapes.Add(newShape);
            }
        }

        private static Vector3[] Compact(Vector3[] deltas, int[] keptIndices)
        {
            Vector3[] result = new Vector3[keptIndices.Length];
            for (int k = 0; k < keptIndices.Length; k++)
            {
                result[k] = deltas[keptIndices[k]];
            }
            return result;
        }
    }
}
