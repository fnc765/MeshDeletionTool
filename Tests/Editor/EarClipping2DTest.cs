using UnityEngine;
using NUnit.Framework;
using System.Collections.Generic;
using MeshDeletionTool;

public class EarClipping2DTest
{
    // 三角形分割の結果を検証する: 三角形の数、面積の合計、巻き順（全て基準法線と同じ向き）、使われた頂点
    private static void AssertTriangulation(Vector3[] polygon, Vector3 normal, int expectedTriangles, float expectedArea, bool expectFallback = false)
    {
        int[] triangles = EarClipping2D.Triangulate(polygon, normal, out bool usedFallback);

        Assert.AreEqual(expectFallback, usedFallback, "扇状分割へのフォールバック");
        Assert.AreEqual(expectedTriangles * 3, triangles.Length, "インデックス数");

        float area = 0f;
        HashSet<int> used = new HashSet<int>();
        for (int i = 0; i < triangles.Length; i += 3)
        {
            Vector3 a = polygon[triangles[i]];
            Vector3 b = polygon[triangles[i + 1]];
            Vector3 c = polygon[triangles[i + 2]];
            Vector3 cross = Vector3.Cross(b - a, c - a);
            area += cross.magnitude * 0.5f;
            Assert.Greater(Vector3.Dot(cross, normal), 0f, "三角形 " + i / 3 + " の巻き順が基準法線と逆");
            used.Add(triangles[i]); used.Add(triangles[i + 1]); used.Add(triangles[i + 2]);
        }
        Assert.AreEqual(expectedArea, area, 1e-5f, "面積の合計");
        foreach (int index in triangles)
        {
            Assert.That(index, Is.InRange(0, polygon.Length - 1));
        }
    }

    [Test]
    public void ConvexQuad_TwoTriangles()
    {
        Vector3[] quad = { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0) };
        AssertTriangulation(quad, Vector3.forward, 2, 1f);
    }

    [Test]
    public void WindingFollowsReferenceNormal()
    {
        // 同じ頂点列でも、基準法線を反転すれば三角形の巻き順も反転する
        Vector3[] quad = { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0) };
        AssertTriangulation(quad, Vector3.forward, 2, 1f);
        AssertTriangulation(quad, Vector3.back, 2, 1f);
        // 時計回りの入力でも結果は基準法線に従う
        Vector3[] clockwise = { new Vector3(0, 1, 0), new Vector3(1, 1, 0), new Vector3(1, 0, 0), new Vector3(0, 0, 0) };
        AssertTriangulation(clockwise, Vector3.forward, 2, 1f);
    }

    [Test]
    public void ConcaveLShape_FourTriangles()
    {
        // 凹多角形の凹みは埋めない（面積 3 のまま）
        Vector3[] lShape =
        {
            new Vector3(0, 0, 0), new Vector3(2, 0, 0), new Vector3(2, 1, 0),
            new Vector3(1, 1, 0), new Vector3(1, 2, 0), new Vector3(0, 2, 0)
        };
        AssertTriangulation(lShape, Vector3.forward, 4, 3f);
    }

    [Test]
    public void PolygonInXZPlane()
    {
        // EarClipping3D は XY 平面へ投影していたため XZ 平面の多角形で失敗していた
        Vector3[] quad = { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 0, 1), new Vector3(0, 0, 1) };
        AssertTriangulation(quad, Vector3.down, 2, 1f);
        AssertTriangulation(quad, Vector3.up, 2, 1f);
    }

    [Test]
    public void PolygonInYZPlane()
    {
        Vector3[] pentagon = new Vector3[5];
        for (int i = 0; i < 5; i++)
        {
            float angle = i * 2f * Mathf.PI / 5f;
            pentagon[i] = new Vector3(0, Mathf.Cos(angle), Mathf.Sin(angle));
        }
        float expectedArea = 0.5f * 5f * Mathf.Sin(2f * Mathf.PI / 5f);
        AssertTriangulation(pentagon, Vector3.right, 3, expectedArea);
    }

    [Test]
    public void TinyTriangle_NormalBelowUnityEpsilon()
    {
        // 外積の長さが 1e-5 未満（Vector3.normalized が零を返す大きさ）でも分割できる
        Vector3 origin = new Vector3(0.07f, 1.31f, 0.04f);
        Vector3[] triangle = { origin, origin + new Vector3(0.002f, 0, 0.001f), origin + new Vector3(0.0005f, 0.002f, 0) };
        Vector3 normal = Vector3.Cross(triangle[1] - triangle[0], triangle[2] - triangle[0]);
        Assert.Less(normal.magnitude, 1e-5f);
        float area = normal.magnitude * 0.5f;
        AssertTriangulation(triangle, normal, 1, area);
    }

    [Test]
    public void CollinearVertexOnEdge_IsSkipped()
    {
        // 辺の途中に一直線上の頂点があっても面積0の三角形を出さない
        Vector3[] polygon = { new Vector3(0, 0, 0), new Vector3(0.5f, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0) };
        AssertTriangulation(polygon, Vector3.forward, 2, 0.5f);
    }

    [Test]
    public void DegenerateInput_ReturnsEmpty()
    {
        Assert.AreEqual(0, EarClipping2D.Triangulate(new[] { Vector3.zero, Vector3.right }, Vector3.forward).Length, "頂点が2つ");
        Vector3[] collinear = { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(2, 0, 0) };
        Assert.AreEqual(0, EarClipping2D.Triangulate(collinear, Vector3.forward).Length, "全て一直線上");
    }

    [Test]
    public void Triangulate2D_ReturnsCounterClockwise()
    {
        List<Vector2> clockwise = new List<Vector2> { new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0), new Vector2(0, 0) };
        int[] triangles = EarClipping2D.Triangulate(clockwise, out bool usedFallback);
        Assert.IsFalse(usedFallback);
        Assert.AreEqual(6, triangles.Length);
        for (int i = 0; i < triangles.Length; i += 3)
        {
            Vector2 a = clockwise[triangles[i]], b = clockwise[triangles[i + 1]], c = clockwise[triangles[i + 2]];
            Assert.Greater((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x), 0f);
        }
    }
}
