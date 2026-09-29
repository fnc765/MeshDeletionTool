using System;
using System.Collections.Generic;
using UnityEngine;
using NUnit.Framework;
using MeshDeletionTool;

// CpuStageBackend（サブメッシュ毎の入力のまとめ方と結果の配り方）と、パイプライン全体が従来の判定（ScalarOracleBackend）と一致するかのテスト
public class StageBackendTest
{
    private static AlphaMask Mask(int width, int height, int seed)
    {
        System.Random random = new System.Random(seed);
        byte[] alpha = new byte[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                alpha[y * width + x] = (byte)((x + y) % 5 == 0 ? random.Next(256) : (x < width / 2 ? 0 : 255));
        return new AlphaMask(width, height, alpha);
    }

    [Test]
    public void CpuBackend_SharesMasksAndSkipsNull_MatchesOracle()
    {
        System.Random random = new System.Random(5);
        AlphaMask maskA = Mask(32, 32, 1), maskB = Mask(16, 24, 2);
        AlphaMask[] masks = { maskA, null, maskB, maskA };
        Vector2[] uv = new Vector2[300];
        for (int i = 0; i < uv.Length; i++) uv[i] = new Vector2((float)(random.NextDouble() * 1.4 - 0.2), (float)(random.NextDouble() * 1.4 - 0.2));
        int[][] triangles = new int[4][];
        int[][] edges = new int[4][];
        for (int k = 0; k < 4; k++)
        {
            triangles[k] = new int[60 * 3];
            for (int i = 0; i < triangles[k].Length; i++) triangles[k][i] = random.Next(uv.Length);
            edges[k] = new int[40 * 2];
            for (int e = 0; e < 40; e++) { int a = random.Next(uv.Length), b = random.Next(uv.Length); if (a > b) (a, b) = (b, a); edges[k][e * 2] = a; edges[k][e * 2 + 1] = b; }
        }
        RefineTestParams settings = new RefineTestParams { RefineFullyTransparentTriangles = true, RefinePartiallyCutTriangles = true, ChordToleranceTexels = 1, MaxRasterSize = 512 };

        using (CpuStageBackend cpu = new CpuStageBackend())
        using (ScalarOracleBackend oracle = new ScalarOracleBackend())
        {
            bool[][] cpuVertices = cpu.ClassifyVertices(uv, masks, 0.5f);
            bool[][] oracleVertices = oracle.ClassifyVertices(uv, masks, 0.5f);
            Assert.IsNull(cpuVertices[1]);
            Assert.AreSame(cpuVertices[0], cpuVertices[3], "同じマスクのサブメッシュは同じ配列");
            for (int k = 0; k < 4; k++) Assert.AreEqual(oracleVertices[k], cpuVertices[k], "頂点判定 サブメッシュ " + k);

            bool[][] cpuMarked = cpu.TestRefineTriangles(uv, triangles, masks, settings, 0.5f);
            bool[][] oracleMarked = oracle.TestRefineTriangles(uv, triangles, masks, settings, 0.5f);
            Assert.IsNull(cpuMarked[1]);
            for (int k = 0; k < 4; k++) Assert.AreEqual(oracleMarked[k], cpuMarked[k], "細分化判定 サブメッシュ " + k);

            cpu.BisectEdges(uv, edges, masks, 0.5f, out bool[][] cpuBoundary, out float[][] cpuWeights);
            oracle.BisectEdges(uv, edges, masks, 0.5f, out bool[][] oracleBoundary, out float[][] oracleWeights);
            Assert.IsNull(cpuBoundary[1]);
            Assert.IsNull(cpuWeights[1]);
            for (int k = 0; k < 4; k++)
            {
                Assert.AreEqual(oracleBoundary[k], cpuBoundary[k], "境界判定 サブメッシュ " + k);
                Assert.AreEqual(oracleWeights[k], cpuWeights[k], "重み サブメッシュ " + k);
            }

            Assert.AreEqual(3, cpu.Timings.Count);
            Assert.AreEqual("ClassifyVertices", cpu.Timings[0].Name);
            Assert.AreEqual(2 * uv.Length, cpu.Timings[0].Elements, "2 枚のマスク × 頂点数");
            Assert.AreEqual(3 * 60, cpu.Timings[1].Elements);
            Assert.AreEqual(3 * 40, cpu.Timings[2].Elements);
        }
    }

    [Test]
    public void Pipeline_CpuBackend_EqualsScalarOracle_OnSyntheticGrid()
    {
        foreach (bool refine in new[] { true, false })
        {
            MeshArrays cpuResult = RunPipeline(new CpuStageBackend(), refine, out List<int[]> cpuParents);
            MeshArrays oracleResult = RunPipeline(new ScalarOracleBackend(), refine, out List<int[]> oracleParents);
            Assert.IsNull(MeshArraysComparer.FirstDifference(oracleResult, cpuResult), "refine=" + refine);
            Assert.AreEqual(oracleParents.Count, cpuParents.Count);
            for (int k = 0; k < cpuParents.Count; k++) Assert.AreEqual(oracleParents[k], cpuParents[k]);
        }
    }

    [Test]
    public void Pipeline_RecordsStageTimings_WhenMeasuring()
    {
        AlphaMeshDeletionPipeline pipeline = new AlphaMeshDeletionPipeline { RefineBoundary = true, RefineMaxDepth = 2, MeasureTime = true };
        MeshArrays mesh = MeshCoreTestUtils.Grid(8);
        AlphaMask mask = Mask(64, 64, 7);
        pipeline.Run(mesh, new[] { mask, mask }, new[] { true, true });
        List<string> names = pipeline.StageTimings.ConvertAll(t => t.Name);
        Assert.AreEqual(new List<string> { "細分化", "頂点判定", "切断", "再結合" }, names);
        Assert.Greater(pipeline.Backend.Timings.Count, 0);
    }

    private static MeshArrays RunPipeline(IAlphaStageBackend backend, bool refine, out List<int[]> parents)
    {
        using (backend)
        {
            AlphaMeshDeletionPipeline pipeline = new AlphaMeshDeletionPipeline
            {
                AlphaThreshold = 0.5f, RefineBoundary = refine, RefineMaxDepth = 3, RefineChordToleranceTexels = 2, MergeCutPolygons = true, Backend = backend
            };
            MeshArrays mesh = MeshCoreTestUtils.Grid(12);
            AlphaMask maskA = Mask(64, 64, 11), maskB = Mask(48, 40, 12);
            MeshArrays result = pipeline.Run(mesh, new[] { maskA, maskB }, new[] { true, true });
            parents = pipeline.OutputTriangleParents;
            return result;
        }
    }
}
