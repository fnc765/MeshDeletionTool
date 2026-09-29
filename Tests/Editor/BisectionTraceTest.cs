using System.Collections.Generic;
using UnityEngine;
using NUnit.Framework;
using MeshDeletionTool;

// 診断用の二分探索の途中経過（BisectionTrace / StageKernelContext.BisectEdgeTrace）と、出力頂点の由来の記録（RecordVertexOrigins）のテスト
public class BisectionTraceTest
{
    // 左が透明、右が不透明で、境界がギザギザのマスク（辺の途中で必ず分類が変わる）
    private static AlphaMask Mask(int width, int height, int seed)
    {
        System.Random random = new System.Random(seed);
        byte[] alpha = new byte[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                alpha[y * width + x] = x + (y % 3) - random.Next(0, 2) >= width / 2 ? (byte)(180 + random.Next(0, 70)) : (byte)random.Next(0, 100);
        return new AlphaMask(width, height, alpha);
    }

    [Test]
    public void BisectEdgeTrace_ReturnsSameWeightAsBisectEdges_AndRecordsEveryStep()
    {
        AlphaMask mask = Mask(64, 48, 5);
        System.Random random = new System.Random(3);
        CpuStageBackend backend = new CpuStageBackend();
        int traced = 0;
        for (int n = 0; n < 200; n++)
        {
            Vector2 a = new Vector2((float)random.NextDouble() * 0.5f, (float)random.NextDouble());
            Vector2 b = new Vector2(0.5f + (float)random.NextDouble() * 0.5f, (float)random.NextDouble());
            backend.BisectEdges(new[] { a, b }, new[] { new[] { 0, 1 } }, new[] { mask }, 0.5f, out bool[][] isBoundary, out float[][] weights);
            uint[] trace = backend.TraceBisection(a, b, mask, 0.5f, out string reason);
            Assert.IsNotNull(trace, reason);
            Assert.AreEqual(BisectionTrace.Words, trace.Length);
            Assert.AreEqual(BisectionTrace.Version, trace[0]);
            Assert.AreEqual(64, trace[4]);
            Assert.AreEqual(48, trace[5]);
            if (!isBoundary[0][0])
                continue;
            traced++;
            Assert.AreEqual(BisectionTrace.ToBits(weights[0][0]), BisectionTrace.ToBits(BisectionTrace.Weight(trace)), "重み");
            BisectionTrace.Step first = BisectionTrace.GetStep(trace, 0);
            Assert.AreEqual(0.5f, first.T);
            float tMin = 0f, tMax = 1f;
            for (int s = 0; s < BisectionTrace.Steps; s++)
            {
                BisectionTrace.Step step = BisectionTrace.GetStep(trace, s);
                Assert.AreEqual((tMin + tMax) / 2f, step.T, "段 " + s + " の t");
                // 中点 UV とテクセル座標は記録通りに再現できる
                Vector2 mid = new Vector2(a.x + (b.x - a.x) * step.T, a.y + (b.y - a.y) * step.T);
                Assert.AreEqual(BisectionTrace.ToBits(mid.x), BisectionTrace.ToBits(step.Mid.x), "段 " + s + " の中点 u");
                Assert.AreEqual(BisectionTrace.ToBits(mid.y), BisectionTrace.ToBits(step.Mid.y), "段 " + s + " の中点 v");
                Assert.AreEqual(mask.TexelX(mid.x), step.X, "段 " + s + " の x");
                Assert.AreEqual(mask.TexelY(mid.y), step.Y, "段 " + s + " の y");
                Assert.AreEqual(mask.Alpha(step.X, step.Y) < 0.5f ? 0u : 2u, step.Class, "段 " + s + " の分類");
                // この実行環境では厳密な単精度の再計算と一致する（違えば float が倍精度で計算されている）
                Assert.AreEqual(BisectionTrace.ToBits(step.Mid.x), BisectionTrace.ToBits(step.Alt.x), "段 " + s + " の別計算法 u");
                Assert.AreEqual(BisectionTrace.ToBits(step.Mid.y), BisectionTrace.ToBits(step.Alt.y), "段 " + s + " の別計算法 v");
                if (s + 1 < BisectionTrace.Steps)
                {
                    float next = BisectionTrace.GetStep(trace, s + 1).T;
                    if (next < step.T) tMax = step.T; else tMin = step.T;
                }
            }
        }
        Assert.Greater(traced, 100, "境界エッジの数");
    }

    [Test]
    public void Format_ListsTenSteps_AndNamesTheFirstDifference()
    {
        AlphaMask mask = Mask(32, 32, 9);
        CpuStageBackend backend = new CpuStageBackend();
        Vector2 a = new Vector2(0.1f, 0.3f), b = new Vector2(0.9f, 0.7f);
        uint[] cpu = backend.TraceBisection(a, b, mask, 0.5f, out _);
        uint[] same = (uint[])cpu.Clone();
        string text = BisectionTrace.Format(cpu, same, "CPU", "GPU");
        Assert.IsTrue(text.Contains("段 10:"), text);
        Assert.IsTrue(text.Contains("全段一致"), text);

        // 段 4 の中点 UV を 1 ulp ずらした記録は、段 4 の中点 UV の差として報告される
        uint[] other = (uint[])cpu.Clone();
        int b3 = BisectionTrace.HeaderWords + 3 * BisectionTrace.StepWords;
        other[b3 + 1] += 1;
        text = BisectionTrace.Format(cpu, other, "CPU", "GPU");
        Assert.IsTrue(text.Contains("最初に違うのは段 4"), text);
        Assert.IsTrue(text.Contains("中点 UV が違う"), text);
        Assert.IsTrue(text.Contains("mad に融合"), text);
    }

    [Test]
    public void Pipeline_RecordsVertexOrigins_ThatReproduceEveryOutputVertex()
    {
        foreach (bool merge in new[] { true, false })
        {
            AlphaMeshDeletionPipeline pipeline = new AlphaMeshDeletionPipeline
            {
                AlphaThreshold = 0.5f, RefineBoundary = true, RefineMaxDepth = 3, RefineChordToleranceTexels = 2, MergeCutPolygons = merge, RecordVertexOrigins = true
            };
            MeshArrays mesh = MeshCoreTestUtils.Grid(12);
            AlphaMask maskA = Mask(64, 64, 11), maskB = Mask(48, 40, 12);
            MeshArrays result = pipeline.Run(mesh, new[] { maskA, maskB }, new[] { true, true });

            Assert.IsNotNull(pipeline.OutputVertexOrigins);
            Assert.IsNotNull(pipeline.RefinedMesh);
            Assert.IsNotNull(pipeline.RefinedMidpointEdges);
            Assert.AreEqual(result.VertexCount, pipeline.OutputVertexOrigins.Length);
            Assert.Greater(pipeline.RefinedMesh.VertexCount, mesh.VertexCount, "細分化で頂点が増える");
            Assert.AreEqual(pipeline.RefinedMesh.VertexCount - mesh.VertexCount, pipeline.RefinedMidpointEdges.Count);

            MeshArrays refined = pipeline.RefinedMesh;
            int boundaryCount = 0, midpointCount = 0, originalCount = 0;
            for (int i = 0; i < result.VertexCount; i++)
            {
                VertexOrigin origin = pipeline.OutputVertexOrigins[i];
                Vector3 expected;
                switch (origin.Kind)
                {
                    case VertexOriginKind.Original:
                        originalCount++;
                        expected = mesh.Vertices[origin.Source];
                        Assert.AreEqual(-1, origin.SubMesh);
                        break;
                    case VertexOriginKind.Midpoint:
                        midpointCount++;
                        Assert.GreaterOrEqual(origin.Source, mesh.VertexCount);
                        expected = Vector3.Lerp(refined.Vertices[origin.ParentA], refined.Vertices[origin.ParentB], 0.5f);
                        Assert.AreEqual(0.5f, origin.Weight);
                        Assert.Less(origin.ParentA, origin.ParentB);
                        break;
                    default:
                        boundaryCount++;
                        Assert.Less(origin.ParentA, origin.ParentB, "親の辺は頂点番号の昇順");
                        Assert.IsTrue(origin.SubMesh == 0 || origin.SubMesh == 1);
                        Assert.Greater(origin.Weight, 0f);
                        Assert.Less(origin.Weight, 1f);
                        expected = Vector3.Lerp(refined.Vertices[origin.ParentA], refined.Vertices[origin.ParentB], origin.Weight);
                        break;
                }
                Assert.AreEqual(expected, result.Vertices[i], "頂点 " + i + " (" + origin.Kind + ")");
            }
            Assert.Greater(boundaryCount, 0, "境界点");
            Assert.Greater(midpointCount, 0, "中点");
            Assert.Greater(originalCount, 0, "元の頂点");

            // 由来の記録は結果を変えない
            AlphaMeshDeletionPipeline plain = new AlphaMeshDeletionPipeline
            {
                AlphaThreshold = 0.5f, RefineBoundary = true, RefineMaxDepth = 3, RefineChordToleranceTexels = 2, MergeCutPolygons = merge
            };
            MeshArrays plainResult = plain.Run(mesh, new[] { maskA, maskB }, new[] { true, true });
            Assert.IsNull(MeshArraysComparer.FirstDifference(plainResult, result), "merge=" + merge);
            Assert.IsNull(plain.OutputVertexOrigins);
        }
    }

    [Test]
    public void FirstDifferentVertex_FindsTheFirstPositionDifference()
    {
        MeshArrays a = MeshCoreTestUtils.Grid(4);
        MeshArrays b = MeshCoreTestUtils.Grid(4);
        Assert.AreEqual(-1, MeshArraysComparer.FirstDifferentVertex(a, b));
        b.Vertices[7].x += 1e-6f;
        Assert.AreEqual(7, MeshArraysComparer.FirstDifferentVertex(a, b));
        Assert.AreEqual(-1, MeshArraysComparer.FirstDifferentVertex(a, MeshCoreTestUtils.Grid(3)));
    }
}
