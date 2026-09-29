using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace MeshDeletionTool
{
    // ベンチマークの「診断」: CPU と GPU の出力の座標が最初に異なる頂点について、その由来（元の頂点 / 細分化の中点 / 切断の境界点）、
    // 親の辺の両端の頂点番号と UV、テクスチャ（サブメッシュ）と大きさ、CPU と GPU の重み t、そして二分探索の 10 段の途中経過
    // （CPU: StageKernelContext.BisectEdgeTrace、GPU: BisectEdgesTrace カーネル）をコピーしやすい平文で返す
    // 入力の由来（AlphaMeshDeletionPipeline.RecordVertexOrigins）を記録した 2 回の実行結果が必要
    internal static class BisectionDiagnostics
    {
        public sealed class RunInfo
        {
            public string Label;
            public MeshArrays Output;
            public VertexOrigin[] Origins;
            public MeshArrays RefinedMesh;
            public List<(int A, int B)> MidpointEdges;
            public int SourceVertexCount;
        }

        public static string Describe(int vertex, RunInfo cpu, RunInfo gpu, AlphaMask[] subMeshMasks, float alphaThreshold)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("診断: 頂点 #" + vertex + "（座標が最初に異なる頂点）");
            if (cpu.Origins == null || gpu.Origins == null || vertex >= cpu.Origins.Length || vertex >= gpu.Origins.Length)
            {
                text.AppendLine("  頂点の由来が記録されていないため診断できません。");
                return text.ToString();
            }
            text.AppendLine("  座標: " + cpu.Label + " " + cpu.Output.Vertices[vertex].ToString("R") + " / " + gpu.Label + " " + gpu.Output.Vertices[vertex].ToString("R"));

            VertexOrigin cpuOrigin = cpu.Origins[vertex];
            VertexOrigin gpuOrigin = gpu.Origins[vertex];
            text.AppendLine("  由来 (" + cpu.Label + "): " + DescribeOrigin(cpuOrigin, cpu));
            if (!SameParents(cpuOrigin, gpuOrigin))
            {
                text.AppendLine("  由来 (" + gpu.Label + "): " + DescribeOrigin(gpuOrigin, gpu));
                text.AppendLine("  ※ 頂点の由来そのものが CPU と GPU で異なります。二分探索の前（頂点の透明判定か細分化の判定 RefineTriangleTest）で結果が分かれています。");
                text.AppendLine("    細分化後の三角形数: " + cpu.Label + " " + cpu.RefinedMesh.TriangleCount + " / " + gpu.Label + " " + gpu.RefinedMesh.TriangleCount +
                                ", 頂点数 " + cpu.RefinedMesh.VertexCount + " / " + gpu.RefinedMesh.VertexCount);
                return text.ToString();
            }
            if (cpuOrigin.Kind != VertexOriginKind.Boundary)
            {
                text.AppendLine("  二分探索の頂点ではないため途中経過は出しません（" + (cpuOrigin.Kind == VertexOriginKind.Midpoint
                    ? "中点の座標は Vector3.Lerp(a, b, 0.5) で、親の頂点が同じなら CPU と GPU で同じになるはずです。親の頂点の座標を確かめてください"
                    : "元の頂点の座標はコピーされるだけなので、CPU と GPU で異なるのは頂点番号の対応がずれている（細分化・切断の結果が違う）ときです") + "）。");
                return text.ToString();
            }

            // 境界点: 親の辺の UV と重み
            MeshArrays refined = cpu.RefinedMesh;
            Vector2 uvA = refined.UV[cpuOrigin.ParentA];
            Vector2 uvB = refined.UV[cpuOrigin.ParentB];
            AlphaMask mask = cpuOrigin.SubMesh >= 0 && cpuOrigin.SubMesh < subMeshMasks.Length ? subMeshMasks[cpuOrigin.SubMesh] : null;
            text.AppendLine("  親の辺: 細分化後メッシュの頂点 " + cpuOrigin.ParentA + " → " + cpuOrigin.ParentB + "（頂点番号の昇順。切断処理と同じ向き）");
            text.AppendLine("    " + cpuOrigin.ParentA + ": " + DescribeRefinedVertex(cpuOrigin.ParentA, cpu) + ", UV " + BisectionTrace.Show(uvA) + ", 座標 " + refined.Vertices[cpuOrigin.ParentA].ToString("R"));
            text.AppendLine("    " + cpuOrigin.ParentB + ": " + DescribeRefinedVertex(cpuOrigin.ParentB, cpu) + ", UV " + BisectionTrace.Show(uvB) + ", 座標 " + refined.Vertices[cpuOrigin.ParentB].ToString("R"));
            text.AppendLine("  テクスチャ: サブメッシュ " + cpuOrigin.SubMesh + (mask != null ? "（" + mask.Width + "x" + mask.Height + ", ラップ " + mask.WrapModeU + "/" + mask.WrapModeV + "）" : "（マスク無し）") + ", 閾値 " + alphaThreshold);
            uint cpuBits = BisectionTrace.ToBits(cpuOrigin.Weight), gpuBits = BisectionTrace.ToBits(gpuOrigin.Weight);
            float delta = gpuOrigin.Weight - cpuOrigin.Weight;
            text.AppendLine("  重み t: " + cpu.Label + " " + BisectionTrace.Show(cpuOrigin.Weight) + " / " + gpu.Label + " " + BisectionTrace.Show(gpuOrigin.Weight) +
                            (cpuBits == gpuBits ? "（同じ。座標の差は補間側の問題）" : "（差 " + delta.ToString("R") + " = 1/1024 の " + (delta * 1024f).ToString("0.###") + " 倍）"));
            if (mask == null)
                return text.ToString();

            // 二分探索の途中経過
            text.AppendLine("二分探索の途中経過（CPU: StageKernels.BisectEdgeTrace、GPU: BisectEdgesTrace カーネル。同じ辺を改めて 1 本だけ計算）");
            uint[] cpuTrace, gpuTrace;
            using (CpuStageBackend cpuBackend = new CpuStageBackend())
            {
                cpuTrace = cpuBackend.TraceBisection(uvA, uvB, mask, alphaThreshold, out string cpuReason);
                if (cpuTrace == null)
                    text.AppendLine("  CPU の途中経過を取れません: " + cpuReason);
            }
            IAlphaStageBackend gpuBackend = ComputeStageBackend.TryCreate(out string gpuReason);
            if (gpuBackend == null)
            {
                gpuTrace = null;
                text.AppendLine("  GPU の途中経過を取れません: " + gpuReason);
            }
            else
            {
                using (gpuBackend)
                {
                    gpuTrace = ((IBisectionTracer)gpuBackend).TraceBisection(uvA, uvB, mask, alphaThreshold, out gpuReason);
                    if (gpuTrace == null)
                        text.AppendLine("  GPU の途中経過を取れません: " + gpuReason);
                }
            }
            text.Append(BisectionTrace.Format(cpuTrace, gpuTrace, cpu.Label, gpu.Label));
            if (cpuTrace != null && BisectionTrace.ToBits(BisectionTrace.Weight(cpuTrace)) != cpuBits)
                text.AppendLine("  ※ CPU の途中経過の重みが本番の重みと異なります（記録用の関数と本番の関数で結果が違う）。");
            if (gpuTrace != null && BisectionTrace.ToBits(BisectionTrace.Weight(gpuTrace)) != gpuBits)
                text.AppendLine("  ※ GPU の途中経過の重みが本番の重み（" + BisectionTrace.Show(gpuOrigin.Weight) + "）と異なります。記録用カーネル（BisectEdgesTrace）と本番カーネル（BisectEdges）で" +
                                "コンパイル結果が違うということで、それ自体がコンパイラ依存の丸めの証拠です。");
            return text.ToString();
        }

        private static bool SameParents(VertexOrigin a, VertexOrigin b)
        {
            return a.Kind == b.Kind && a.Source == b.Source && a.ParentA == b.ParentA && a.ParentB == b.ParentB && a.SubMesh == b.SubMesh;
        }

        private static string DescribeOrigin(VertexOrigin origin, RunInfo run)
        {
            switch (origin.Kind)
            {
                case VertexOriginKind.Original:
                    return "元のメッシュの頂点 " + origin.Source;
                case VertexOriginKind.Midpoint:
                    return "細分化の中点（細分化後メッシュの頂点 " + origin.Source + "、親の辺 " + origin.ParentA + " - " + origin.ParentB + "）";
                default:
                    return "切断の境界点（二分探索）。親の辺 " + origin.ParentA + " → " + origin.ParentB + ", 重み " + BisectionTrace.Show(origin.Weight) + ", サブメッシュ " + origin.SubMesh;
            }
        }

        // 細分化後メッシュの頂点を、元の頂点か中点（親を再帰的に）として説明する
        private static string DescribeRefinedVertex(int refinedIndex, RunInfo run, int depth = 0)
        {
            VertexOrigin origin = AlphaMeshDeletionPipeline.DescribeRefinedVertex(refinedIndex, run.SourceVertexCount, run.MidpointEdges);
            if (origin.Kind == VertexOriginKind.Original)
                return "元の頂点 " + origin.Source;
            if (depth >= 3)
                return "中点（" + origin.ParentA + ", " + origin.ParentB + "）";
            return "中点（" + DescribeRefinedVertex(origin.ParentA, run, depth + 1) + " と " + DescribeRefinedVertex(origin.ParentB, run, depth + 1) + "）";
        }
    }
}
