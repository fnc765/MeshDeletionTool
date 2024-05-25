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
                boneWeights = newMeshData.BoneWeights.ToArray()
            };

            RemoveTriangles(originalMesh, removeVerticesIndexs, newMeshData.Vertices, newMeshData.Triangles);
            newMesh.triangles = newMeshData.Triangles.ToArray();
            RemoveSubMeshes(originalMesh, removeVerticesIndexs, newMeshData.Vertices, newMesh);
            RemoveBlendShapes(originalMesh, removeVerticesIndexs, newMesh);
            newMesh.bindposes = originalMesh.bindposes;

            return newMesh;
        }

        protected void RemoveVerticesDatas(Mesh originalMesh, List<int> removeVerticesIndexs, MeshData newMeshData)
        {
            for (int index = 0; index < originalMesh.vertices.Length; index++)
            {
                if (removeVerticesIndexs.Contains(index))
                    continue;
                
                newMeshData.Vertices.Add(originalMesh.vertices[index]);
                newMeshData.Normals.Add(originalMesh.normals[index]);
                newMeshData.Tangents.Add(originalMesh.tangents[index]);
                newMeshData.UV.Add(originalMesh.uv[index]);

                if (originalMesh.uv2.Length > index)
                    newMeshData.UV2.Add(originalMesh.uv2[index]);
                if (originalMesh.uv3.Length > index)
                    newMeshData.UV3.Add(originalMesh.uv3[index]);
                if (originalMesh.uv4.Length > index)
                    newMeshData.UV4.Add(originalMesh.uv4[index]);

                if (originalMesh.colors.Length > index)
                    newMeshData.Colors.Add(originalMesh.colors[index]);
                if (originalMesh.colors32.Length > index)
                    newMeshData.Colors32.Add(originalMesh.colors32[index]);

                newMeshData.BoneWeights.Add(originalMesh.boneWeights[index]);
            }
        }

        protected void RemoveTriangles(Mesh originalMesh, List<int> removeVerticesIndexs, List<Vector3> newVerticesList, List<int> newTrianglesList)
        {
            for (int index = 0; index < originalMesh.triangles.Length; index += 3)
            {
                int index0 = originalMesh.triangles[index];
                int index1 = originalMesh.triangles[index + 1];
                int index2 = originalMesh.triangles[index + 2];

                if (!removeVerticesIndexs.Contains(index0) &&
                    !removeVerticesIndexs.Contains(index1) &&
                    !removeVerticesIndexs.Contains(index2))
                {
                    int newIndex0 = newVerticesList.IndexOf(originalMesh.vertices[index0]);
                    int newIndex1 = newVerticesList.IndexOf(originalMesh.vertices[index1]);
                    int newIndex2 = newVerticesList.IndexOf(originalMesh.vertices[index2]);

                    newTrianglesList.Add(newIndex0);
                    newTrianglesList.Add(newIndex1);
                    newTrianglesList.Add(newIndex2);
                }
            }
        }

        protected void RemoveSubMeshes(Mesh originalMesh, List<int> removeVerticesIndexs, List<Vector3> newVerticesList, Mesh newMesh)
        {
            int subMeshCount = originalMesh.subMeshCount;
            newMesh.subMeshCount = subMeshCount;

            for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
            {
                int[] originalTriangles = originalMesh.GetTriangles(subMeshIndex);
                List<int> newTriangles = new List<int>();

                for (int index = 0; index < originalTriangles.Length; index += 3)
                {
                    int index0 = originalTriangles[index];
                    int index1 = originalTriangles[index + 1];
                    int index2 = originalTriangles[index + 2];

                    if (!removeVerticesIndexs.Contains(index0) &&
                        !removeVerticesIndexs.Contains(index1) &&
                        !removeVerticesIndexs.Contains(index2))
                    {
                        int newIndex0 = newVerticesList.IndexOf(originalMesh.vertices[index0]);
                        int newIndex1 = newVerticesList.IndexOf(originalMesh.vertices[index1]);
                        int newIndex2 = newVerticesList.IndexOf(originalMesh.vertices[index2]);

                        newTriangles.Add(newIndex0);
                        newTriangles.Add(newIndex1);
                        newTriangles.Add(newIndex2);
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
            for (int i = 0; i < originalMesh.blendShapeCount; i++)
            {
                string blendShapeName = originalMesh.GetBlendShapeName(i);
                int frameCount = originalMesh.GetBlendShapeFrameCount(i);

                for (int j = 0; j < frameCount; j++)
                {
                    float frameWeight = originalMesh.GetBlendShapeFrameWeight(i, j);
                    Vector3[] frameVertices = new Vector3[originalMesh.vertexCount];
                    Vector3[] frameNormals = new Vector3[originalMesh.vertexCount];
                    Vector3[] frameTangents = new Vector3[originalMesh.vertexCount];
                    originalMesh.GetBlendShapeFrameVertices(i, j, frameVertices, frameNormals, frameTangents);

                    List<Vector3> frameVerticesList = new List<Vector3>(frameVertices);
                    List<Vector3> frameNormalsList = new List<Vector3>(frameNormals);
                    List<Vector3> frameTangentsList = new List<Vector3>(frameTangents);

                    foreach (int index in removeVerticesIndexs)
                    {
                        frameVerticesList.RemoveAt(index);
                        frameNormalsList.RemoveAt(index);
                        frameTangentsList.RemoveAt(index);
                    }

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
        }
    }
}
