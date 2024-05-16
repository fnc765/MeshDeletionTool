using UnityEngine;
using UnityEditor;
using UnityEngine.TestTools;
using NUnit.Framework;
using System.Collections;
using System.IO;
using MeshGetColorInfo;

public class MeshGetColorInfoTest
{
    private GameObject testObject;
    private Texture2D testTexture;

    [SetUp]
    public void SetUp()
    {
        /*
        2(blue)   3(white)
            ┌────┐
            │    │
            └────┘
        0(red)    1(green)
        */
        testObject = new GameObject("TestObject");
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

        testTexture = new Texture2D(2, 2);
        testTexture.SetPixel(0, 0, Color.red);      // 0
        testTexture.SetPixel(1, 0, Color.green);    // 1
        testTexture.SetPixel(0, 1, Color.blue);     // 2
        testTexture.SetPixel(1, 1, Color.white);    // 3
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
    }

    [TearDown]
    public void TearDown()
    {
        Object.DestroyImmediate(testObject);
        var path = "Assets/TestTexture.png";
        AssetDatabase.DeleteAsset(path);
    }

    [Test]
    public void TestGetColorInMeshes_Vertex0()
    {
        TestGetColorInMeshesForVertex(0, Color.red, 0, 0);
    }

    [Test]
    public void TestGetColorInMeshes_Vertex1()
    {
        TestGetColorInMeshesForVertex(1, Color.green, 1, 0);
    }

    [Test]
    public void TestGetColorInMeshes_Vertex2()
    {
        TestGetColorInMeshesForVertex(2, Color.blue, 0, 1);
    }

    [Test]
    public void TestGetColorInMeshes_Vertex3()
    {
        TestGetColorInMeshesForVertex(3, Color.white, 1, 1);
    }

    private void TestGetColorInMeshesForVertex(int vertexIndex, Color expectedColor, int expectedX, int expectedY)
    {
        var window = EditorWindow.GetWindow<MeshGetColorInfo.MeshGetColorInfo>();
        window.targetRenderer = testObject.GetComponent<Renderer>();
        window.vertexIndex = vertexIndex;

        var method = typeof(MeshGetColorInfo.MeshGetColorInfo).GetMethod("GetColorInMeshes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        method.Invoke(window, null);

        LogAssert.Expect(LogType.Log, $"サブメッシュ 0 の頂点 {vertexIndex} のテクスチャRGB値: RGBA({expectedColor.r:F3}, {expectedColor.g:F3}, {expectedColor.b:F3}, {expectedColor.a:F3}), ピクセル座標: ({expectedX}.0, {expectedY}.0)");
    }
}