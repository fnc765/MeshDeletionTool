using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace MeshDeletionTool
{
    // 処理段を CPU で実行するバックエンド。カーネルの C# 実装（StageKernelContext）を要素毎に順に呼ぶ
    // GPU が使えないとき（Compute Shader 非対応、シェーダーが見つからない、テスト・ハーネス）の既定の実行先で、GPU と同じ結果を返す
    public class CpuStageBackend : IAlphaStageBackend
    {
        public string Name => "CPU";

        private readonly List<StageTiming> timings = new List<StageTiming>();
        public IReadOnlyList<StageTiming> Timings => timings;

        // 診断用: 多角形の内部にあるか判定したテクセルの数と、そのうち不透明か判定したテクセルの数
        public long RasterTexelTests;
        public long RasterInsideTexels;

        public bool[][] ClassifyVertices(Vector2[] uvs, AlphaMask[] masks, float alphaThreshold)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            AlphaMask[] distinct = StageBatch.DistinctMasks(masks, out int[] maskIndexPerSet);
            int vertexCount = uvs.Length;
            StageKernelContext context = new StageKernelContext
            {
                Masks = StageBatch.Views(distinct),
                AlphaClass = StageKernelContext.BuildAlphaClassTable(alphaThreshold),
                UV = uvs,
                Count = distinct.Length * vertexCount,
                VertexCount = vertexCount,
                OutFlags = new int[distinct.Length * vertexCount]
            };
            if (distinct.Length > 0 && vertexCount > 0)
            {
                context.DispatchClassifyVertices();
            }
            Record("ClassifyVertices", context.Count, stopwatch);
            return StageBatch.SplitVertexFlags(context.OutFlags, vertexCount, distinct.Length, maskIndexPerSet);
        }

        public bool[][] TestRefineTriangles(Vector2[] uvs, int[][] triangleSets, AlphaMask[] masks, RefineTestParams settings, float alphaThreshold)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            AlphaMask[] distinct = StageBatch.DistinctMasks(masks, out int[] maskIndexPerSet);
            int[] items = StageBatch.FlattenItems(triangleSets, maskIndexPerSet, 3, out int[] itemMask, out int[] setOffsets);
            StageKernelContext context = new StageKernelContext
            {
                Masks = StageBatch.Views(distinct),
                AlphaClass = StageKernelContext.BuildAlphaClassTable(alphaThreshold),
                UV = uvs,
                Items = items,
                ItemMask = itemMask,
                Count = itemMask.Length,
                VertexCount = uvs.Length,
                RefineFull = settings.RefineFullyTransparentTriangles ? 1 : 0,
                RefinePartial = settings.RefinePartiallyCutTriangles ? 1 : 0,
                ChordTolerance = settings.ChordToleranceTexels,
                MaxRasterSize = settings.MaxRasterSize,
                OutFlags = new int[itemMask.Length]
            };
            context.DispatchRefineTriangleTest();
            RasterTexelTests += context.RasterTexelTests;
            RasterInsideTexels += context.RasterInsideTexels;
            Record("RefineTriangleTest", context.Count, stopwatch);
            return StageBatch.SplitFlags(context.OutFlags, triangleSets, 3, setOffsets);
        }

        public void BisectEdges(Vector2[] uvs, int[][] edgeSets, AlphaMask[] masks, float alphaThreshold, out bool[][] isBoundary, out float[][] weights)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            AlphaMask[] distinct = StageBatch.DistinctMasks(masks, out int[] maskIndexPerSet);
            int[] items = StageBatch.FlattenItems(edgeSets, maskIndexPerSet, 2, out int[] itemMask, out int[] setOffsets);
            StageKernelContext context = new StageKernelContext
            {
                Masks = StageBatch.Views(distinct),
                AlphaClass = StageKernelContext.BuildAlphaClassTable(alphaThreshold),
                UV = uvs,
                Items = items,
                ItemMask = itemMask,
                Count = itemMask.Length,
                VertexCount = uvs.Length,
                OutFlags = new int[itemMask.Length],
                OutWeights = new float[itemMask.Length]
            };
            context.DispatchBisectEdges();
            Record("BisectEdges", context.Count, stopwatch);
            isBoundary = StageBatch.SplitFlags(context.OutFlags, edgeSets, 2, setOffsets);
            weights = StageBatch.SplitWeights(context.OutWeights, edgeSets, 2, setOffsets);
        }

        public void ResetTimings()
        {
            timings.Clear();
            RasterTexelTests = RasterInsideTexels = 0;
        }

        public void Dispose()
        {
        }

        private void Record(string name, long elements, Stopwatch stopwatch)
        {
            stopwatch.Stop();
            StageTiming timing = timings.Find(t => t.Name == name);
            if (timing == null)
            {
                timing = new StageTiming { Name = name };
                timings.Add(timing);
            }
            timing.Calls++;
            timing.Elements += elements;
            timing.Milliseconds += stopwatch.Elapsed.TotalMilliseconds;
        }
    }
}
