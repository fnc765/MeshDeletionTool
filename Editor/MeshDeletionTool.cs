using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace MeshDeletionTool
{
    public class MeshDeletionToolUtils : EditorWindow
    {
        protected Mesh GetOriginalMesh(Renderer targetRenderer)
        {
            if (targetRenderer is SkinnedMeshRenderer skinnedMeshRenderer)
            {
                return skinnedMeshRenderer.sharedMesh;
            }
            else if (targetRenderer is MeshRenderer meshRenderer)
            {
                MeshFilter meshFilter = targetRenderer.GetComponent<MeshFilter>();
                if (meshFilter != null)
                {
                    return meshFilter.sharedMesh;
                }
            }

            Debug.LogError("対象オブジェクトに有効な SkinnedMeshRenderer または MeshRenderer コンポーネントがありません！");
            return null;
        }

        protected Material[] GetOriginalMaterials(Renderer targetRenderer)
        {
            if (targetRenderer is SkinnedMeshRenderer skinnedMeshRenderer)
            {
                return skinnedMeshRenderer.sharedMaterials;
            }
            else if (targetRenderer is MeshRenderer meshRenderer)
            {
                MeshFilter meshFilter = targetRenderer.GetComponent<MeshFilter>();
                if (meshFilter != null)
                {
                    return meshRenderer.sharedMaterials;
                }
            }

            Debug.LogError("対象オブジェクトに有効な SkinnedMeshRenderer または MeshRenderer コンポーネントがありません！");
            return null;
        }

        protected List<int> GetVerticesToRemove(Renderer targetRenderer, Mesh originalMesh, Bounds deletionBounds)
        {
            List<int> removeVerticesIndexs = new List<int>();

            for (int index = 0; index < originalMesh.vertices.Length; index++)
            {
                Vector3 verticesWorldPoints = targetRenderer.transform.TransformPoint(originalMesh.vertices[index]);
                if (deletionBounds.Contains(verticesWorldPoints))
                {
                    removeVerticesIndexs.Add(index);
                }
            }

            removeVerticesIndexs.Sort((a, b) => b - a);
            return removeVerticesIndexs;
        }

        protected Mesh CreateMeshAfterVertexRemoval(Mesh originalMesh, List<int> removeVerticesIndexs)
        {
            MeshData newMeshData = new MeshData();
            RemoveVerticesDatas(originalMesh, removeVerticesIndexs, newMeshData);

            Mesh newMesh = new Mesh
            {
                vertices = newMeshData.Vertices.ToArray(),
                normals = newMeshData.Normals.ToArray(),
                tangents = newMeshData.Tangents.ToArray(),
                uv = newMeshData.UV.ToArray(),
                uv2 = newMeshData.UV2.ToArray(),
                uv3 = newMeshData.UV3.ToArray(),
                uv4 = newMeshData.UV4.ToArray(),
                colors = newMeshData.Colors.ToArray(),
                colors32 = newMeshData.Colors32.ToArray(),
                boneWeights = newMeshData.BoneWeights.ToArray(),
                bindposes = originalMesh.bindposes
            };

            // インデックスマッピングの作成
            Dictionary<int, int> oldToNewIndexMap = new Dictionary<int, int>();
            for (int oldIndex = 0, newIndex = 0; oldIndex < originalMesh.vertexCount; oldIndex++)
            {
                if (!removeVerticesIndexs.Contains(oldIndex))
                {
                    oldToNewIndexMap[oldIndex] = newIndex;
                    newIndex++;
                }
            }

            RemoveTriangles(originalMesh, removeVerticesIndexs, oldToNewIndexMap, newMeshData.Triangles);
            newMesh.triangles = newMeshData.Triangles.ToArray();
            RemoveSubMeshes(originalMesh, removeVerticesIndexs, oldToNewIndexMap, newMesh);
            RemoveBlendShapes(originalMesh, removeVerticesIndexs, newMesh);

            return newMesh;
        }

        protected void RemoveVerticesDatas(Mesh originalMesh, List<int> removeVerticesIndexs, MeshData newMeshData)
        {
            HashSet<int> removeVerticesIndexsSet = new HashSet<int>(removeVerticesIndexs);

            for (int index = 0; index < originalMesh.vertexCount; index++)
            {
                if (removeVerticesIndexsSet.Contains(index))
                    continue;

                newMeshData.Vertices.Add(originalMesh.vertices[index]);
                if (index < originalMesh.normals.Length)
                    newMeshData.Normals.Add(originalMesh.normals[index]);
                if (index < originalMesh.tangents.Length)
                    newMeshData.Tangents.Add(originalMesh.tangents[index]);

                if (index < originalMesh.uv.Length)
                    newMeshData.UV.Add(originalMesh.uv[index]);
                if (index < originalMesh.uv2.Length)
                    newMeshData.UV2.Add(originalMesh.uv2[index]);
                if (index < originalMesh.uv3.Length)
                    newMeshData.UV3.Add(originalMesh.uv3[index]);
                if (index < originalMesh.uv4.Length)
                    newMeshData.UV4.Add(originalMesh.uv4[index]);

                if (index < originalMesh.colors.Length)
                    newMeshData.Colors.Add(originalMesh.colors[index]);
                if (index < originalMesh.colors32.Length)
                    newMeshData.Colors32.Add(originalMesh.colors32[index]);

                if (index < originalMesh.boneWeights.Length)
                    newMeshData.BoneWeights.Add(originalMesh.boneWeights[index]);
            }
        }

        protected void RemoveTriangles(Mesh originalMesh, List<int> removeVerticesIndexs, Dictionary<int, int> oldToNewIndexMap, List<int> newTrianglesList)
        {
            for (int i = 0; i < originalMesh.triangles.Length; i += 3)
            {
                int index0 = originalMesh.triangles[i];
                int index1 = originalMesh.triangles[i + 1];
                int index2 = originalMesh.triangles[i + 2];

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

        protected void RemoveSubMeshes(Mesh originalMesh, List<int> removeVerticesIndexs, Dictionary<int, int> oldToNewIndexMap, Mesh newMesh)
        {
            int subMeshCount = originalMesh.subMeshCount;
            newMesh.subMeshCount = subMeshCount;

            for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
            {
                int[] originalTriangles = originalMesh.GetTriangles(subMeshIndex);
                List<int> newTriangles = new List<int>();

                for (int i = 0; i < originalTriangles.Length; i += 3)
                {
                    int index0 = originalTriangles[i];
                    int index1 = originalTriangles[i + 1];
                    int index2 = originalTriangles[i + 2];

                    if (!removeVerticesIndexs.Contains(index0) &&
                        !removeVerticesIndexs.Contains(index1) &&
                        !removeVerticesIndexs.Contains(index2))
                    {
                        newTriangles.Add(oldToNewIndexMap[index0]);
                        newTriangles.Add(oldToNewIndexMap[index1]);
                        newTriangles.Add(oldToNewIndexMap[index2]);
                    }
                }

                if (newTriangles.Count > 0)
                {
                    newMesh.SetTriangles(newTriangles.ToArray(), subMeshIndex);
                }
            }
        }

        protected void RemoveBlendShapes(Mesh originalMesh, List<int> removeVerticesIndexs, Mesh newMesh)
        {
            // 1つの頂点に対して：ブレンドシェイプの数×ブレンドシェイプのフレーム分の頂点、法線、接線情報が必要
            // 元のメッシュの全ブレンドシェイプに対して処理を行う
            for (int i = 0; i < originalMesh.blendShapeCount; i++)
            {
                // 現在のブレンドシェイプの名前を取得
                string blendShapeName = originalMesh.GetBlendShapeName(i);
                // 現在のブレンドシェイプのフレーム数を取得
                int frameCount = originalMesh.GetBlendShapeFrameCount(i);

                // 各フレームに対して処理を行う
                for (int j = 0; j < frameCount; j++)
                {
                    // フレームのウェイトを取得
                    float frameWeight = originalMesh.GetBlendShapeFrameWeight(i, j);
                    // フレームの頂点、法線、接線を格納する配列を作成
                    Vector3[] frameVertices = new Vector3[originalMesh.vertexCount];
                    Vector3[] frameNormals = new Vector3[originalMesh.vertexCount];
                    Vector3[] frameTangents = new Vector3[originalMesh.vertexCount];
                    // フレームの頂点、法線、接線を取得
                    originalMesh.GetBlendShapeFrameVertices(i, j, frameVertices, frameNormals, frameTangents);

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

                    // 新しいメッシュにブレンドシェイプのフレームを追加
                    newMesh.AddBlendShapeFrame(blendShapeName, frameWeight, frameVerticesList.ToArray(), frameNormalsList.ToArray(), frameTangentsList.ToArray());
                }
            }
        }

        protected void MakeTextureReadable(Texture2D texture)
        {
            string path = AssetDatabase.GetAssetPath(texture);
            if (string.IsNullOrEmpty(path))
            {
                Debug.LogError("テクスチャのパスが見つかりません！");
                return;
            }

            TextureImporter textureImporter = AssetImporter.GetAtPath(path) as TextureImporter;
            if (textureImporter != null)
            {
                textureImporter.isReadable = true;
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                Debug.Log($"テクスチャ '{texture.name}' の読み取り可能設定を有効にしました。");
            }
            else
            {
                Debug.LogError("TextureImporterが見つかりませんでした。");
            }
        }

        protected void SaveNewMesh(Mesh newMesh)
        {
            AssetDatabase.CreateAsset(newMesh, "Assets/NewMesh.asset");
            AssetDatabase.SaveAssets();
        }

        protected Bounds GetObjectBounds(GameObject obj)
        {
            Renderer[] renderers = obj.GetComponentsInChildren<Renderer>();
            Bounds bounds = new Bounds();
            bool boundsInitialized = false;

            foreach (Renderer renderer in renderers)
            {
                Bounds rendererBounds = renderer.bounds;

                if (!boundsInitialized)
                {
                    bounds = rendererBounds;
                    boundsInitialized = true;
                }
                else
                {
                    bounds.Encapsulate(rendererBounds.min);
                    bounds.Encapsulate(rendererBounds.max);
                }
            }

            return bounds;
        }

        protected class MeshData
        {
            public List<Vector3> Vertices { get; set; } = new List<Vector3>();
            public List<Vector3> Normals { get; set; } = new List<Vector3>();
            public List<Vector4> Tangents { get; set; } = new List<Vector4>();
            public List<Vector2> UV { get; set; } = new List<Vector2>();
            public List<Vector2> UV2 { get; set; } = new List<Vector2>();
            public List<Vector2> UV3 { get; set; } = new List<Vector2>();
            public List<Vector2> UV4 { get; set; } = new List<Vector2>();
            public List<Color> Colors { get; set; } = new List<Color>();
            public List<Color32> Colors32 { get; set; } = new List<Color32>();
            public List<BoneWeight> BoneWeights { get; set; } = new List<BoneWeight>();
            public List<int> Triangles { get; set; } = new List<int>();

            public void Add(MeshData meshData)
            {
                if (meshData == null)
                {
                    return;
                }

                if (meshData.Vertices != null && meshData.Vertices.Count > 0)
                {
                    this.Vertices.AddRange(meshData.Vertices);
                }

                if (meshData.Normals != null && meshData.Normals.Count > 0)
                {
                    this.Normals.AddRange(meshData.Normals);
                }

                if (meshData.Tangents != null && meshData.Tangents.Count > 0)
                {
                    this.Tangents.AddRange(meshData.Tangents);
                }

                if (meshData.UV != null && meshData.UV.Count > 0)
                {
                    this.UV.AddRange(meshData.UV);
                }

                if (meshData.UV2 != null && meshData.UV2.Count > 0)
                {
                    this.UV2.AddRange(meshData.UV2);
                }

                if (meshData.UV3 != null && meshData.UV3.Count > 0)
                {
                    this.UV3.AddRange(meshData.UV3);
                }

                if (meshData.UV4 != null && meshData.UV4.Count > 0)
                {
                    this.UV4.AddRange(meshData.UV4);
                }

                if (meshData.Colors != null && meshData.Colors.Count > 0)
                {
                    this.Colors.AddRange(meshData.Colors);
                }

                if (meshData.Colors32 != null && meshData.Colors32.Count > 0)
                {
                    this.Colors32.AddRange(meshData.Colors32);
                }

                if (meshData.BoneWeights != null && meshData.BoneWeights.Count > 0)
                {
                    this.BoneWeights.AddRange(meshData.BoneWeights);
                }

                if (meshData.Triangles != null && meshData.Triangles.Count > 0)
                {
                    this.Triangles.AddRange(meshData.Triangles);
                }
            }

            public MeshData GetElementAt(int index)
            {
                var result = new MeshData();

                if (index < Vertices.Count)
                {
                    result.Vertices.Add(Vertices[index]);
                }
                if (index < Normals.Count)
                {
                    result.Normals.Add(Normals[index]);
                }
                if (index < Tangents.Count)
                {
                    result.Tangents.Add(Tangents[index]);
                }
                if (index < UV.Count)
                {
                    result.UV.Add(UV[index]);
                }
                if (index < UV2.Count)
                {
                    result.UV2.Add(UV2[index]);
                }
                if (index < UV3.Count)
                {
                    result.UV3.Add(UV3[index]);
                }
                if (index < UV4.Count)
                {
                    result.UV4.Add(UV4[index]);
                }
                if (index < Colors.Count)
                {
                    result.Colors.Add(Colors[index]);
                }
                if (index < Colors32.Count)
                {
                    result.Colors32.Add(Colors32[index]);
                }
                if (index < BoneWeights.Count)
                {
                    result.BoneWeights.Add(BoneWeights[index]);
                }
                if (index < Triangles.Count)
                {
                    result.Triangles.Add(Triangles[index]);
                }

                return result;
            }

            public void AddElementFromMesh(Mesh mesh, int index)
            {
                if (mesh == null || index < 0)
                {
                    return;
                }

                if (index < mesh.vertexCount)
                {
                    Vertices.Add(mesh.vertices[index]);
                    if (mesh.normals.Length > index)
                    {
                        Normals.Add(mesh.normals[index]);
                    }
                    if (mesh.tangents.Length > index)
                    {
                        Tangents.Add(mesh.tangents[index]);
                    }
                    if (mesh.uv.Length > index)
                    {
                        UV.Add(mesh.uv[index]);
                    }
                    if (mesh.uv2.Length > index)
                    {
                        UV2.Add(mesh.uv2[index]);
                    }
                    if (mesh.uv3.Length > index)
                    {
                        UV3.Add(mesh.uv3[index]);
                    }
                    if (mesh.uv4.Length > index)
                    {
                        UV4.Add(mesh.uv4[index]);
                    }
                    if (mesh.colors.Length > index)
                    {
                        Colors.Add(mesh.colors[index]);
                    }
                    if (mesh.colors32.Length > index)
                    {
                        Colors32.Add(mesh.colors32[index]);
                    }
                    if (mesh.boneWeights.Length > index)
                    {
                        BoneWeights.Add(mesh.boneWeights[index]);
                    }
                }

                if (mesh.triangles.Length > index)
                {
                    Triangles.Add(mesh.triangles[index]);
                }
            }
        }
    }
}
