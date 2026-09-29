using System;
using System.Collections.Generic;
using UnityEngine;

namespace MeshDeletionTool
{
    // 三角形の細分化判定の設定（AlphaBoundaryRefiner の設定のうち、要素毎の判定に必要なもの）
    public struct RefineTestParams
    {
        // 修正1: 3頂点とも透明で内部に不透明テクセルを含む三角形を細分化する
        public bool RefineFullyTransparentTriangles;

        // 修正2: 直線で切ると不透明テクセルが失われる三角形（一部の頂点が透明）を細分化する
        public bool RefinePartiallyCutTriangles;

        // 修正2で許容する、失われる不透明テクセル数
        public int ChordToleranceTexels;

        // ラスタライズするバウンディングボックスの上限（テクセル）。超える場合は間引いてサンプリングする
        public int MaxRasterSize;
    }

    // バックエンドが記録した処理段の時間（診断・ベンチマーク用）
    public class StageTiming
    {
        public string Name;
        public int Calls;
        public long Elements;
        public double Milliseconds;
    }

    // GUI の「計算バックエンド」の選択肢
    public enum StageBackendMode
    {
        // 境界の細分化が有効で GPU が使えれば GPU、それ以外は CPU
        Auto = 0,
        Cpu = 1,
        Gpu = 2
    }

    // 処理段の時間を 1 行の文字列にする（「時間を計測」のログとベンチマークで共用）
    public static class StageTimingReport
    {
        public static string Format(IEnumerable<StageTiming> stages, IEnumerable<StageTiming> kernels)
        {
            System.Text.StringBuilder text = new System.Text.StringBuilder();
            double total = 0;
            foreach (StageTiming timing in stages)
            {
                if (text.Length > 0) text.Append(", ");
                text.Append(timing.Name).Append(' ').Append(timing.Milliseconds.ToString("0.0")).Append(" ms");
                total += timing.Milliseconds;
            }
            text.Insert(0, "処理時間: ").Append(", 合計 ").Append(total.ToString("0.0")).Append(" ms");
            bool first = true;
            foreach (StageTiming timing in kernels)
            {
                text.Append(first ? " / カーネル: " : ", ");
                first = false;
                text.Append(timing.Name).Append(' ').Append(timing.Calls).Append(" 回 ").Append(timing.Elements.ToString("#,0")).Append(" 要素 ")
                    .Append(timing.Milliseconds.ToString("0.0")).Append(" ms");
            }
            return text.ToString();
        }
    }

    // テクスチャのアルファ値に対する要素毎に独立な処理段（頂点の透明判定・三角形の細分化判定・辺上の境界点の二分探索）の実行先
    // CPU 実装（CpuStageBackend）と Compute Shader 実装（ComputeStageBackend）は同じ入力に対して同じ結果を返す。
    // 判定の数式は StageKernels（C#）と MeshDeletionStages.compute（HLSL）で行単位に対応させてある
    // 引数の masks[k] はサブメッシュ k のマスク（無ければ null。null のサブメッシュの結果は null）
    public interface IAlphaStageBackend : IDisposable
    {
        // 表示用の名前（"CPU" / "GPU (…)"）
        string Name { get; }

        // 頂点毎の透明判定。結果 [k][v] はサブメッシュ k のマスクで頂点 v が透明（アルファ値 < 閾値）かどうか
        // 同じ AlphaMask を持つサブメッシュには同じ配列が返る
        bool[][] ClassifyVertices(Vector2[] uvs, AlphaMask[] masks, float alphaThreshold);

        // 三角形毎の細分化判定。triangleSets[k] はサブメッシュ k の三角形の頂点番号（3 つずつ）、結果 [k][t] は三角形 t を細分化するかどうか
        bool[][] TestRefineTriangles(Vector2[] uvs, int[][] triangleSets, AlphaMask[] masks, RefineTestParams settings, float alphaThreshold);

        // 辺毎の境界判定と境界点の重み。edgeSets[k] はサブメッシュ k の辺の頂点番号（昇順に 2 つずつ）
        // isBoundary[k][e] は辺 e が境界エッジ（片方の頂点が透明で他方が閾値より大きい）か、weights[k][e] はそのときの二分探索の重み（境界でなければ 0）
        void BisectEdges(Vector2[] uvs, int[][] edgeSets, AlphaMask[] masks, float alphaThreshold, out bool[][] isBoundary, out float[][] weights);

        // 記録した処理段の時間
        IReadOnlyList<StageTiming> Timings { get; }
        void ResetTimings();
    }
}
