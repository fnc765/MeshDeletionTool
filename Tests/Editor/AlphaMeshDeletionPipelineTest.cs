using System.Collections.Generic;
using UnityEngine;
using NUnit.Framework;
using MeshDeletionTool;

// AlphaMeshDeletionPipeline（細分化 → 判定 → 切断 → 再結合の全体。Mesh に依存しない）のテスト
public class AlphaMeshDeletionPipelineTest
{
    private static AlphaMeshDeletionPipeline Pipeline(List<string> log, bool refine)
    {
        return new AlphaMeshDeletionPipeline
        {
            AlphaThreshold = 0.5f,
            RefineBoundary = refine,
            RefineMaxDepth = 2,
            RefineChordToleranceTexels = 2,
            MergeCutPolygons = true,
            SimplifyToleranceTexels = 1f,
            Log = log.Add
        };
    }

    [Test]
    public void Run_WithoutRefinement_CutsAndReportsParents()
    {
        MeshArrays mesh = MeshCoreTestUtils.Grid(4);
        AlphaMask mask = MeshCoreTestUtils.LeftTransparent(32, 32);
        List<string> log = new List<string>();
        AlphaMeshDeletionPipeline pipeline = Pipeline(log, false);
        MeshArrays result = pipeline.Run(mesh, new[] { mask, mask }, new[] { true, true });

        Assert.AreEqual(0, log.Count);
        Assert.AreEqual(2, pipeline.OutputTriangleParents.Count);
        Assert.AreEqual(result.SubMeshTriangles[0].Length / 3, pipeline.OutputTriangleParents[0].Length);
        Assert.AreEqual(result.SubMeshTriangles[1].Length / 3, pipeline.OutputTriangleParents[1].Length);
        // 透明側（u < 0.5、u = 0.5 の列もテクセル 15 で透明）の頂点は残らず、境界点は境目の 1 テクセル以内にある
        foreach (Vector2 uv in result.UV)
        {
            Assert.GreaterOrEqual(uv.x, 0.5f - 2f / 31f);
        }
        MeshCoreTestUtils.AssertValidTriangles(result);
        MeshCoreTestUtils.AssertStreamLengths(result);
    }

    [Test]
    public void Run_WithRefinement_LogsBothStagesAndKeepsParentsInRange()
    {
        MeshArrays mesh = MeshCoreTestUtils.Grid(4);
        AlphaMask mask = MeshCoreTestUtils.LeftTransparent(32, 32);
        List<string> log = new List<string>();
        AlphaMeshDeletionPipeline pipeline = Pipeline(log, true);
        MeshArrays result = pipeline.Run(mesh, new[] { mask, mask }, new[] { true, true });

        Assert.AreEqual(2, log.Count);
        StringAssert.StartsWith("境界の細分化: 三角形 32 → ", log[0]);
        StringAssert.StartsWith("切断後の再結合: 三角形 ", log[1]);
        for (int subMeshIndex = 0; subMeshIndex < 2; subMeshIndex++)
        {
            int originalTriangleCount = mesh.SubMeshTriangles[subMeshIndex].Length / 3;
            Assert.AreEqual(result.SubMeshTriangles[subMeshIndex].Length / 3, pipeline.OutputTriangleParents[subMeshIndex].Length);
            foreach (int parent in pipeline.OutputTriangleParents[subMeshIndex])
            {
                Assert.GreaterOrEqual(parent, 0);
                Assert.Less(parent, originalTriangleCount);
            }
        }
        foreach (Vector2 uv in result.UV)
        {
            Assert.GreaterOrEqual(uv.x, 0.5f - 2f / 31f);
        }
        MeshCoreTestUtils.AssertValidTriangles(result);
        MeshCoreTestUtils.AssertStreamLengths(result);
        Assert.AreEqual(1, result.BlendShapes.Count);
        Assert.AreEqual(mesh.Bindposes, result.Bindposes);
    }

    [Test]
    public void Run_NonTargetSubMesh_IsLeftUntouched()
    {
        MeshArrays mesh = MeshCoreTestUtils.Grid(4);
        AlphaMask mask = MeshCoreTestUtils.LeftTransparent(32, 32);
        AlphaMeshDeletionPipeline pipeline = Pipeline(new List<string>(), true);
        // 左半分（透明側）のサブメッシュ 0 を対象外にすると何も削除されない
        MeshArrays result = pipeline.Run(mesh, new[] { mask, mask }, new[] { false, true });
        Assert.AreEqual(mesh.VertexCount, result.VertexCount);
        Assert.AreEqual(mesh.TriangleCount, result.TriangleCount);
    }

    // 対象になっていてもマスク（テクスチャ）の無いサブメッシュは対象外として扱う（テクスチャの無いサブメッシュにチェックが入っていても壊れない）
    [Test]
    public void Run_TargetSubMeshWithoutMask_IsTreatedAsNonTarget()
    {
        MeshArrays mesh = MeshCoreTestUtils.Grid(4);
        AlphaMask mask = MeshCoreTestUtils.LeftTransparent(32, 32);
        bool[] targets = { true, true };
        MeshArrays withoutMask = Pipeline(new List<string>(), true).Run(mesh, new AlphaMask[] { null, mask }, targets);
        MeshArrays nonTarget = Pipeline(new List<string>(), true).Run(mesh, new[] { mask, mask }, new[] { false, true });

        Assert.IsNull(MeshArraysComparer.FirstDifference(nonTarget, withoutMask));
        Assert.AreEqual(mesh.VertexCount, withoutMask.VertexCount);
        Assert.AreEqual(new[] { true, true }, targets);   // 呼び出し側の配列は変えない
    }

    // UV の無いメッシュは判定できないので例外にする（GPU で黙って誤った結果になるのを防ぐ）
    [Test]
    public void Run_MeshWithoutUV_Throws()
    {
        MeshArrays mesh = MeshCoreTestUtils.Grid(2);
        mesh.UV = new Vector2[0];
        AlphaMask mask = MeshCoreTestUtils.LeftTransparent(8, 8);
        foreach (bool refine in new[] { false, true })
        {
            Assert.Throws<System.ArgumentException>(() => Pipeline(new List<string>(), refine).Run(mesh, new[] { mask, mask }, new[] { true, true }));
        }
    }
}
