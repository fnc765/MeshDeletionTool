using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;
using MeshDeletionTool;

namespace MeshGetColorInfo
{
    public class MeshGetColorInfo : MeshDeletionToolUtils
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

            if (GUILayout.Button("テストオブジェクトの生成"))
            {
                MakeTestObject();
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

                if (!texture.isReadable)
                {
                    MakeTextureReadable(texture);
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

        private void MakeTestObject()
        {
            /*
            2(blue)   3(white)
                ┌────┐
                │    │
                └────┘
            0(red)    1(green)
            */
            GameObject testObject = new GameObject("TestObject");
            var meshRenderer = testObject.AddComponent<MeshRenderer>();
            var meshFilter = testObject.AddComponent<MeshFilter>();
            var mesh = new Mesh();

            // 頂点座標を四角形として設定
            mesh.vertices = new Vector3[]
            {
                new Vector3(0, 0, 0), // 0
                new Vector3(1, 0, 0), // 1
                new Vector3(0, 1, 0), // 2
                new Vector3(1, 1, 0)  // 3
            };

            // UV 座標を設定
            mesh.uv = new Vector2[]
            {
                new Vector2(0, 0), // 0
                new Vector2(1, 0), // 1
                new Vector2(0, 1), // 2
                new Vector2(1, 1)  // 3
            };

            // 四角形を2つの三角形として設定
            mesh.triangles = new int[]
            {
                0, 1, 2, // 第1三角形
                2, 1, 3  // 第2三角形
            };

            meshFilter.sharedMesh = mesh;

            Texture2D testTexture = new Texture2D(2, 2);
            testTexture.SetPixel(0, 0, Color.red);
            testTexture.SetPixel(1, 0, Color.green);
            testTexture.SetPixel(0, 1, Color.blue);
            testTexture.SetPixel(1, 1, Color.white);
            testTexture.Apply();

            var material = new Material(Shader.Find("Standard"));
            material.mainTexture = testTexture;
            meshRenderer.sharedMaterial = material;

            var path = "Assets/TestTexture.png";
            File.WriteAllBytes(path, testTexture.EncodeToPNG());
            AssetDatabase.ImportAsset(path);
            var textureImporter = AssetImporter.GetAtPath(path) as TextureImporter;
            textureImporter.isReadable = true;
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            testTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            material.mainTexture = testTexture;

            // テストオブジェクトを選択してシーンに表示
            Selection.activeGameObject = testObject;
        }
    }
}