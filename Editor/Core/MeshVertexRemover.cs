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

            // サブメッシュ毎に、削除された頂点を使わない三角形だけを残す。三角形が全て消えたサブメッシュは空のまま残す
            // （サブメッシュの数と順番を保ち、マテリアルとの対応がずれないようにする。かつては空になったサブメッシュ 0 に全三角形が残っていた）
            newMesh.SubMeshTriangles = new int[originalMesh.SubMeshCount][];
            for (int subMeshIndex = 0; subMeshIndex < originalMesh.SubMeshCount; subMeshIndex++)
            {
                List<int> newTriangles = new List<int>();
                RemoveTriangles(originalMesh.GetTriangles(subMeshIndex), isRemoved, oldToNewIndexMap, newTriangles);
                newMesh.SubMeshTriangles[subMeshIndex] = newTriangles.ToArray();
            }
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
