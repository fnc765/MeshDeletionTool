using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace MeshDeletionTool
{
    public class MeshDeletionToolForTexture : MeshDeletionTool
    {
        internal Renderer targetRenderer;

        [MenuItem("Tools/MeshDeletionToolForTexture")]
        private static void Init()
        {
            MeshDeletionToolForTexture window = (MeshDeletionToolForTexture)EditorWindow.GetWindow(typeof(MeshDeletionToolForTexture));
            window.titleContent = new GUIContent("MeshDeletionTool");
            window.Show();
        }

        private void OnGUI()
        {
            GUILayout.Label("オブジェクトを選択", EditorStyles.boldLabel);

            targetRenderer = EditorGUILayout.ObjectField("対象オブジェクト", targetRenderer, typeof(Renderer), true) as Renderer;

            if (GUILayout.Button("テクスチャ透明部分のメッシュを削除"))
            {
                DeleteMeshesFromTexture();
            }
        }

        private void DeleteMeshesFromTexture()
        {
            if (!ValidateInputs(targetRenderer))
                return;

            Mesh originalMesh = GetOriginalMesh(targetRenderer);
            Material[] originalMaterials = GetOriginalMaterials(targetRenderer);
            if (originalMesh == null)
                return;

            List<int> removeVerticesIndexs = GetVerticesToRemoveFromTexture(originalMesh, originalMaterials);
            Mesh newMesh = CreateMeshAfterVertexRemoval(originalMesh, removeVerticesIndexs);
            SaveNewMesh(newMesh);
        }

        private bool ValidateInputs(Renderer targetRenderer)
        {
            if (targetRenderer == null)
            {
                Debug.LogError("対象オブジェクトが選択されていません！");
                return false;
            }
            return true;
        }

        private List<int> GetVerticesToRemoveFromTexture(Mesh originalMesh, Material[] originalMaterials)
        {
            List<int> removeVerticesIndexs = new List<int>();

            int subMeshCount = originalMesh.subMeshCount;
            List<HashSet<int>> subMeshTrianglesList = new List<HashSet<int>>();
            
            // サブメッシュのポリゴン配列をHashSetに格納し、それをListに保存する
            for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
            {
                int[] triangles = originalMesh.GetTriangles(subMeshIndex);
                HashSet<int> triangleSet = new HashSet<int>(triangles);
                subMeshTrianglesList.Add(triangleSet);
            }

            // 各頂点に対して
            for (int vertexIndex = 0; vertexIndex < originalMesh.vertices.Length; vertexIndex++)
            {
                bool vertexShouldBeRemoved = false;

                // その頂点がどのサブメッシュに属しているか検索する
                for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
                {
                    if (subMeshTrianglesList[subMeshIndex].Contains(vertexIndex))
                    {
                        // 属しているサブメッシュからマテリアルを特定する
                        Material material = originalMaterials[subMeshIndex];

                        // 頂点のUV座標を取得
                        Vector2 uv = originalMesh.uv[vertexIndex];

                        // マテリアルに割り当てられたテクスチャのRGBA値を取得する
                        Texture2D texture = material.mainTexture as Texture2D;
                        if (texture != null)
                        {
                            Vector2 pixelUV = new Vector2(uv.x * (texture.width - 1), uv.y * (texture.height - 1));
                            Color color = texture.GetPixel((int)pixelUV.x, (int)pixelUV.y);

                            // 任意の条件で頂点を削除リストに追加する
                            // (ここでは例としてアルファ値が0である場合に削除する)
                            if (color.a == 0)
                            {
                                vertexShouldBeRemoved = true;
                                break;
                            }
                        }
                    }
                }

                if (vertexShouldBeRemoved)
                {
                    removeVerticesIndexs.Add(vertexIndex);
                }
            }

            removeVerticesIndexs.Sort((a, b) => b - a);
            return removeVerticesIndexs;
        }

    }
}
