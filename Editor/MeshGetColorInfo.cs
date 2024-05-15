using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;

namespace MeshGetColorInfo
{
    public class MeshGetColorInfo : EditorWindow
    {
        internal GameObject targetObject;
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

            targetObject = EditorGUILayout.ObjectField("対象オブジェクト", targetObject, typeof(GameObject), true) as GameObject;

            vertexIndex = EditorGUILayout.IntField("頂点番号", vertexIndex);

            if (GUILayout.Button("頂点カラーの取得"))
            {
                GetColorInMeshes();
            }
        }

        private void GetColorInMeshes()
        {
            if (targetObject == null)
            {
                Debug.LogError("対象オブジェクトが選択されていません！");
                return;
            }

            SkinnedMeshRenderer skinnedMeshRenderer = targetObject.GetComponent<SkinnedMeshRenderer>();
            if (skinnedMeshRenderer == null || skinnedMeshRenderer.sharedMesh == null)
            {
                Debug.LogError("対象オブジェクトに有効な SkinnedMeshRenderer コンポーネントがありません！");
                return;
            }

            Material[] materials = skinnedMeshRenderer.sharedMaterials;
            for (int i = 0; i < materials.Length; i++)
            {
                Texture2D texture = materials[i].mainTexture as Texture2D;
                if (texture == null)
                {
                    Debug.LogError("マテリアルにテクスチャがアタッチされていません。");
                    continue;
                }

                Vector2[] uv = skinnedMeshRenderer.sharedMesh.uv;
                if (vertexIndex < 0 || vertexIndex >= uv.Length)
                {
                    Debug.LogError("指定された頂点番号が無効です。");
                    return;
                }

                Vector2 pixelUV = new Vector2(uv[vertexIndex].x * texture.width, uv[vertexIndex].y * texture.height);
                Color vertexColor = texture.GetPixel((int)pixelUV.x, (int)pixelUV.y);

                Debug.Log($"サブメッシュ {i} の頂点 {vertexIndex} のテクスチャRGB値: {vertexColor}, ピクセル座標: ({pixelUV.x:F1}, {pixelUV.y:F1})");
            }
        }
    }
}
