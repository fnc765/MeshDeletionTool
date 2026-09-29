using System.Collections.Generic;
using UnityEngine;
using MeshDeletionTool;

// Unity に依存しない中核クラス（AlphaMeshCutter / MeshVertexRemover / AlphaMeshDeletionPipeline）のテスト用データ
public static class MeshCoreTestUtils
{
    // n×n の格子（XY 平面、uv = 位置）。左半分の四角形をサブメッシュ 0、右半分をサブメッシュ 1 にする
    // 全頂点属性を持ち、ブレンドシェイプ "shape" は差分 (頂点番号, 0, 0) を 1 フレーム持つ
    public static MeshArrays Grid(int n)
    {
        int stride = n + 1, count = stride * stride;
        MeshArrays mesh = new MeshArrays { Name = "grid" };
        mesh.Vertices = new Vector3[count];
        mesh.Normals = new Vector3[count];
        mesh.Tangents = new Vector4[count];
        mesh.UV = new Vector2[count];
        mesh.UV2 = new Vector2[count];
        mesh.Colors = new Color[count];
        mesh.BoneWeights = new BoneWeight[count];
        Vector3[] delta = new Vector3[count];
        for (int j = 0; j <= n; j++)
        {
            for (int i = 0; i <= n; i++)
            {
                int v = j * stride + i;
                float u = (float)i / n, w = (float)j / n;
                mesh.Vertices[v] = new Vector3(u, w, 0f);
                mesh.Normals[v] = Vector3.back;
                mesh.Tangents[v] = new Vector4(1f, 0f, 0f, 1f);
                mesh.UV[v] = new Vector2(u, w);
                mesh.UV2[v] = new Vector2(w, u);
                mesh.Colors[v] = new Color(u, w, 0f, 1f);
                mesh.BoneWeights[v] = new BoneWeight { boneIndex0 = i % 2, weight0 = 1f };
                delta[v] = new Vector3(v, 0f, 0f);
            }
        }
        List<int> left = new List<int>(), right = new List<int>();
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                int a = j * stride + i, b = a + 1, c = a + stride + 1, d = a + stride;
                List<int> target = i < n / 2 ? left : right;
                target.AddRange(new[] { a, b, c, a, c, d });
            }
        }
        mesh.SubMeshTriangles = new[] { left.ToArray(), right.ToArray() };
        mesh.Bindposes = new[] { Matrix4x4.identity, Matrix4x4.identity };
        BlendShapeData shape = new BlendShapeData { Name = "shape" };
        shape.Frames.Add(new BlendShapeFrameData { Weight = 100f, DeltaVertices = delta, DeltaNormals = new Vector3[count], DeltaTangents = new Vector3[count] });
        mesh.BlendShapes.Add(shape);
        return mesh;
    }

    // 幅 width のうち u < 0.5 が透明、それ以外が不透明のマスク
    public static AlphaMask LeftTransparent(int width, int height)
    {
        byte[] alpha = new byte[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                alpha[y * width + x] = x < width / 2 ? (byte)0 : (byte)255;
        return new AlphaMask(width, height, alpha);
    }

    // 三角形の頂点番号が全て範囲内で、縮退（同じ頂点を2つ含む）していないか
    public static void AssertValidTriangles(MeshArrays mesh)
    {
        foreach (int[] triangles in mesh.SubMeshTriangles)
        {
            NUnit.Framework.Assert.AreEqual(0, triangles.Length % 3);
            for (int i = 0; i < triangles.Length; i += 3)
            {
                for (int k = 0; k < 3; k++)
                {
                    NUnit.Framework.Assert.GreaterOrEqual(triangles[i + k], 0);
                    NUnit.Framework.Assert.Less(triangles[i + k], mesh.VertexCount);
                }
                NUnit.Framework.Assert.AreNotEqual(triangles[i], triangles[i + 1]);
                NUnit.Framework.Assert.AreNotEqual(triangles[i + 1], triangles[i + 2]);
                NUnit.Framework.Assert.AreNotEqual(triangles[i + 2], triangles[i]);
            }
        }
    }

    // 全ての頂点属性の長さが頂点数と一致するか（無い属性は長さ 0）
    public static void AssertStreamLengths(MeshArrays mesh)
    {
        int count = mesh.VertexCount;
        foreach (int length in new[] { mesh.Normals.Length, mesh.Tangents.Length, mesh.UV.Length, mesh.UV2.Length, mesh.Colors.Length, mesh.BoneWeights.Length })
        {
            NUnit.Framework.Assert.IsTrue(length == 0 || length == count, "attribute length " + length + " != " + count);
        }
        foreach (BlendShapeData shape in mesh.BlendShapes)
        {
            foreach (BlendShapeFrameData frame in shape.Frames)
            {
                NUnit.Framework.Assert.AreEqual(count, frame.DeltaVertices.Length);
                NUnit.Framework.Assert.AreEqual(count, frame.DeltaNormals.Length);
                NUnit.Framework.Assert.AreEqual(count, frame.DeltaTangents.Length);
            }
        }
    }
}
