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

        // 1頂点あたりのボーン数が4を超える頂点があれば警告する
        // 本ツールは4ボーン固定の Mesh.boneWeights で読み書きするため、5番目以降のウェイトは出力メッシュから失われる
        // （Mesh.GetBonesPerVertex は Unity 2019.1 以降）
        protected void WarnIfBonesPerVertexExceedFour(Mesh mesh)
        {
            if (mesh == null)
                return;

            var bonesPerVertex = mesh.GetBonesPerVertex();
            int exceedingCount = 0;
            int maxBones = 0;
            foreach (byte bones in bonesPerVertex)
            {
                if (bones > 4)
                {
                    exceedingCount++;
                    maxBones = Mathf.Max(maxBones, bones);
                }
            }
            if (exceedingCount > 0)
            {
                Debug.LogWarning("1頂点あたりのボーン数が4を超える頂点が " + exceedingCount + " 個あります（最大 " + maxBones + " ボーン）。" +
                                 "本ツールは1頂点あたり4ボーンまでしか扱えないため、5番目以降のウェイトは出力メッシュから失われます。" +
                                 "必要なら事前にウェイトを4ボーン以下に減らしてください。");
            }
        }

        // 指定した頂点を取り除いたメッシュを作る（処理本体は Mesh に依存しない MeshVertexRemover）
        protected Mesh CreateMeshAfterVertexRemoval(Mesh originalMesh, List<int> removeVerticesIndexs)
        {
            MeshArrays originalArrays = MeshArraysUnityAdapter.FromMesh(originalMesh);
            MeshArrays newArrays = MeshVertexRemover.RemoveVertices(originalArrays, removeVerticesIndexs);
            return MeshArraysUnityAdapter.ToMesh(newArrays);
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
    }
}
