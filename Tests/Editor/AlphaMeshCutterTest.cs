using System.Collections.Generic;
using UnityEngine;
using NUnit.Framework;
using MeshDeletionTool;

// AlphaMeshCutter（テクスチャの透明部分に基づく頂点の削除と切断。Mesh に依存しない）のテスト
public class AlphaMeshCutterTest
{
    // 1 枚の四角形 0(0,0) 1(1,0) 2(0,1) 3(1,1)、三角形 (0,1,2) (0,2,3)、uv = 位置。左半分 (u < 0.5) が透明
    private static MeshArrays Quad(int subMeshCount)
    {
        MeshArrays mesh = MeshCoreTestUtils.Grid(1);
        if (subMeshCount == 1)
        {
            mesh.SubMeshTriangles = new[] { new[] { 0, 1, 2, 0, 2, 3 } };
        }
        else
        {
            mesh.SubMeshTriangles = new[] { new[] { 0, 1, 2 }, new[] { 0, 2, 3 } };
        }
        return mesh;
    }

    [Test]
    public void GetVerticesToRemove_ReturnsTransparentVerticesDescending()
    {
        MeshArrays mesh = Quad(1);
        AlphaMask mask = MeshCoreTestUtils.LeftTransparent(8, 8);
        AlphaMeshCutter cutter = new AlphaMeshCutter { AlphaThreshold = 0.5f };
        List<int> removed = cutter.GetVerticesToRemove(mesh, new[] { mask });
        Assert.AreEqual(new List<int> { 2, 0 }, removed);
    }

    [Test]
    public void GetVerticesToRemove_IgnoresSubMeshesWithoutMask()
    {
        MeshArrays mesh = Quad(1);
        AlphaMeshCutter cutter = new AlphaMeshCutter();
        Assert.AreEqual(0, cutter.GetVerticesToRemove(mesh, new AlphaMask[] { null }).Count);
    }

    [Test]
    public void Cut_ReplacesRemovedCornersWithBoundaryVertices()
    {
        MeshArrays mesh = Quad(1);
        AlphaMask mask = MeshCoreTestUtils.LeftTransparent(8, 8);
        AlphaMeshCutter cutter = new AlphaMeshCutter { AlphaThreshold = 0.5f };
        List<int> removed = cutter.GetVerticesToRemove(mesh, new[] { mask });
        MeshArrays result = cutter.Cut(mesh, new[] { mask }, new[] { true }, removed, out List<int[]> sources);

        // 残る頂点 1, 3 と、辺 (0,1) (1,2) (2,3) (0,3) 上の境界点。三角形 (0,1,2) と (0,2,3) はそれぞれ 1 つになる
        Assert.AreEqual(6, result.VertexCount);
        Assert.AreEqual(2, result.TriangleCount);
        Assert.AreEqual(new[] { 0, 1 }, sources[0]);
        Assert.AreEqual(mesh.Vertices[1], result.Vertices[0]);
        Assert.AreEqual(mesh.Vertices[3], result.Vertices[1]);
        for (int i = 2; i < 6; i++)
        {
            // 境界点は透明・不透明の境目（u ≈ 4/7）の 1 テクセル以内
            Assert.AreEqual(4f / 7f, result.UV[i].x, 1f / 7f, "boundary vertex " + i);
        }
        MeshCoreTestUtils.AssertValidTriangles(result);
        MeshCoreTestUtils.AssertStreamLengths(result);
        Assert.AreEqual("", result.Name);
        Assert.AreSame(mesh.Bindposes, result.Bindposes);
    }

    [Test]
    public void Cut_InterpolatesBlendShapeDeltasLikePositions()
    {
        MeshArrays mesh = Quad(1);
        AlphaMask mask = MeshCoreTestUtils.LeftTransparent(8, 8);
        AlphaMeshCutter cutter = new AlphaMeshCutter { AlphaThreshold = 0.5f };
        List<int> removed = cutter.GetVerticesToRemove(mesh, new[] { mask });
        MeshArrays result = cutter.Cut(mesh, new[] { mask }, new[] { true }, removed, out _);

        Assert.AreEqual(1, result.BlendShapes.Count);
        Assert.AreEqual("shape", result.BlendShapes[0].Name);
        BlendShapeFrameData frame = result.BlendShapes[0].Frames[0];
        Assert.AreEqual(100f, frame.Weight);
        Assert.AreEqual(6, frame.DeltaVertices.Length);
        // 残った頂点の差分はそのまま（頂点 1 → (1,0,0)、頂点 3 → (3,0,0)）
        Assert.AreEqual(new Vector3(1, 0, 0), frame.DeltaVertices[0]);
        Assert.AreEqual(new Vector3(3, 0, 0), frame.DeltaVertices[1]);
        // 最初の境界点は辺 (0,1) 上: 差分 = lerp((0,0,0), (1,0,0), w) = 位置と同じ x
        Assert.AreEqual(result.Vertices[2].x, frame.DeltaVertices[2].x, 1e-6f);
    }

    [Test]
    public void Cut_ProtectsVerticesOfNonTargetSubMeshes()
    {
        MeshArrays mesh = Quad(2);
        AlphaMask mask = MeshCoreTestUtils.LeftTransparent(8, 8);
        AlphaMeshCutter cutter = new AlphaMeshCutter { AlphaThreshold = 0.5f };
        List<int> removed = cutter.GetVerticesToRemove(mesh, new[] { mask, mask });
        Assert.AreEqual(2, removed.Count);
        // サブメッシュ 1 (0,2,3) は対象外なので頂点 0 と 2 は残り、削除リストからも取り除かれる
        MeshArrays result = cutter.Cut(mesh, new[] { mask, mask }, new[] { true, false }, removed, out List<int[]> sources);
        Assert.AreEqual(0, removed.Count);
        Assert.AreEqual(4, result.VertexCount);
        Assert.AreEqual(new[] { 0, 1, 2 }, result.SubMeshTriangles[0]);
        Assert.AreEqual(new[] { 0, 2, 3 }, result.SubMeshTriangles[1]);
        Assert.AreEqual(new[] { 0 }, sources[0]);
        Assert.AreEqual(new[] { 0 }, sources[1]);
    }

    [Test]
    public void Cut_WithoutMask_DropsPartiallyRemovedTriangles()
    {
        MeshArrays mesh = Quad(1);
        List<int> removed = new List<int> { 3 };   // 頂点 3 だけを削除（マスク無し）
        AlphaMeshCutter cutter = new AlphaMeshCutter();
        MeshArrays result = cutter.Cut(mesh, new AlphaMask[] { null }, new[] { true }, removed, out List<int[]> sources);
        // 境界点が作れないため三角形 (0,2,3) は消え、(0,1,2) だけが残る
        Assert.AreEqual(3, result.VertexCount);
        Assert.AreEqual(new[] { 0, 1, 2 }, result.SubMeshTriangles[0]);
        Assert.AreEqual(new[] { 0 }, sources[0]);
    }
}
