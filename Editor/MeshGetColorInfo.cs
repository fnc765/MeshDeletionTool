using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;

namespace MeshGetColorInfo
{
    public class MeshGetColorInfo : EditorWindow
    {
        internal Renderer targetRenderer;
        internal int vertexIndex = 0;

        [MenuItem("Tools/MeshGetColorInfo")]
        private static void Init()
        {
            MeshGetColorInfo window = (MeshGetColorInfo)EditorWindow.GetWindow(typeof(MeshGetColorInfo));
            window.titleContent = new GUIContent("MeshGetColorInfo");
            window.Show();
        }

        private void OnGUI()
        {
            GUILayout.Label("オブジェクトを選択", EditorStyles.boldLabel);

            targetRenderer = EditorGUILayout.ObjectField("対象オブジェクト", targetRenderer, typeof(Renderer), true) as Renderer;

            vertexIndex = EditorGUILayout.IntField("頂点番号", vertexIndex);

            if (GUILayout.Button("頂点カラーの取得"))
            {
                GetColorInMeshes();
            }
        }

        private void GetColorInMeshes()
        {
            if (targetRenderer == null)
            {
                Debug.LogError("対象オブジェクトが選択されていません！");
                return;
            }

            Mesh mesh = null;
            Material[] materials = null;

            if (targetRenderer is SkinnedMeshRenderer skinnedMeshRenderer)
            {
                mesh = skinnedMeshRenderer.sharedMesh;
                materials = skinnedMeshRenderer.sharedMaterials;
            }
            else if (targetRenderer is MeshRenderer meshRenderer)
            {
                MeshFilter meshFilter = targetRenderer.GetComponent<MeshFilter>();
                if (meshFilter != null)
                {
                    mesh = meshFilter.sharedMesh;
                    materials = meshRenderer.sharedMaterials;
                }
            }

            if (mesh == null)
            {
                Debug.LogError("対象オブジェクトに有効な SkinnedMeshRenderer または MeshRenderer コンポーネントがありません！");
                return;
            }

            for (int i = 0; i < materials.Length; i++)
            {
                Texture2D texture = materials[i].mainTexture as Texture2D;
                if (texture == null)
                {
                    Debug.LogError("マテリアルにテクスチャがアタッチされていません。");
                    continue;
                }

                Vector2[] uv = mesh.uv;
                if (vertexIndex < 0 || vertexIndex >= uv.Length)
                {
                    Debug.LogError("指定された頂点番号が無効です。");
                    return;
                }

                Vector2 pixelUV = new Vector2(uv[vertexIndex].x * (texture.width - 1), uv[vertexIndex].y * (texture.height - 1));
                Color vertexColor = texture.GetPixel((int)pixelUV.x, (int)pixelUV.y);

                Debug.Log($"サブメッシュ {i} の頂点 {vertexIndex} のテクスチャRGB値: {vertexColor}, ピクセル座標: ({pixelUV.x:F1}, {pixelUV.y:F1})");
            }
        }
    }
}