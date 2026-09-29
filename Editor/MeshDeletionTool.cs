using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace MeshDeletionTool
{
    public class MeshDeletionToolUtils : EditorWindow
    {
        protected static Mesh GetOriginalMesh(Renderer targetRenderer)
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

        protected static Material[] GetOriginalMaterials(Renderer targetRenderer)
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

        // メッシュが本ツールで扱える形かを確かめ、扱えなければその理由（1 行）を返す（扱えるなら null）
        // 出力は三角形のサブメッシュとして書き出すため、全サブメッシュが三角形である必要がある（Mesh.GetTriangles は三角形以外のサブメッシュを空にしてしまう）
        // requireUV: テクスチャに基づく処理には UV（UV0）が必要（無いと頂点をテクセルに対応付けられない）
        protected static string FindMeshProblem(Mesh mesh, bool requireUV)
        {
            for (int subMeshIndex = 0; subMeshIndex < mesh.subMeshCount; subMeshIndex++)
            {
                MeshTopology topology = mesh.GetTopology(subMeshIndex);
                if (topology != MeshTopology.Triangles)
                    return "メッシュ '" + mesh.name + "' のサブメッシュ " + subMeshIndex + " は三角形ではなく " + topology + " のため処理できません。";
            }
            if (requireUV && !mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord0))
                return "メッシュ '" + mesh.name + "' に UV（UV0）が無いため、テクスチャに基づく削除はできません。";
            return null;
        }

        // 1頂点あたりのボーン数が4を超える頂点があれば警告する
        // 本ツールは4ボーン固定の Mesh.boneWeights で読み書きするため、5番目以降のウェイトは出力メッシュから失われる
        // （Mesh.GetBonesPerVertex は Unity 2019.1 以降）
        protected static void WarnIfBonesPerVertexExceedFour(Mesh mesh)
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

        // テクスチャのインポート設定を読み取り可能にする（既に読み取り可能なら何もしない。設定は元に戻さない: 診断用の MeshGetColorInfo が使う。
        // メッシュ削除の処理は読み出す間だけ変更して元に戻す TemporaryReadableTextures を使う）
        protected void MakeTextureReadable(Texture2D texture)
        {
            if (texture == null || texture.isReadable)
                return;
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
                textureImporter.SaveAndReimport();
                Debug.Log($"テクスチャ '{texture.name}' の読み取り可能設定を有効にしました。");
            }
            else
            {
                Debug.LogError("TextureImporterが見つかりませんでした。");
            }
        }

        // 出力メッシュを固定のパスに保存する（前回の出力は上書きされる）
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
