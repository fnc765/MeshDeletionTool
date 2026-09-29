using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace MeshDeletionTool
{
    // 出力メッシュの頂点の由来（診断用。AlphaMeshDeletionPipeline.RecordVertexOrigins が有効なときに記録される）
    public enum VertexOriginKind
    {
        // 元のメッシュの頂点（削除されなかったもの）
        Original = 0,
        // 境界の細分化で辺の中点に追加された頂点
        Midpoint = 1,
        // 切断で辺上の境界点（二分探索の重み）に追加された頂点
        Boundary = 2
    }

    public struct VertexOrigin
    {
        public VertexOriginKind Kind;
        // Original: 元のメッシュの頂点番号。Midpoint: 細分化後メッシュでの自身の頂点番号。Boundary: -1
        public int Source;
        // Midpoint / Boundary: 親の辺の両端（細分化後メッシュの頂点番号、昇順）。Original: -1
        public int ParentA;
        public int ParentB;
        // Boundary: 二分探索の重み t（頂点 = ParentA + (ParentB - ParentA) * t）。Midpoint: 0.5。Original: 0
        public float Weight;
        // Boundary: 二分探索に使ったマスクのサブメッシュ番号。それ以外: -1
        public int SubMesh;
    }

    // 辺上の境界点の二分探索（BisectEdge）の途中経過の記録。CPU（StageKernelContext.BisectEdgeTrace）と GPU（BisectEdgesTrace カーネル）が
    // 同じ語の並びで書き、ここで読み解いて並べて表示する。CPU と GPU の結果が一致しないとき、どの段のどの値から違うかを突き止めるためのもの
    // 並び（uint、Words 語）:
    //   ヘッダ 8 語: [0] 版, [1] uv1 の分類, [2] asuint(重み), [3] uv2 の分類, [4] 幅, [5] 高さ, [6][7] 予備
    //   段 s（0〜9）の 10 語（HeaderWords + s * StepWords から）: [0] asuint(t), [1][2] asuint(中点 UV), [3][4] asuint(px, py), [5][6] テクセル x, y（ラップ後）,
    //     [7] 中点の分類, [8][9] asuint(別の計算法での中点 UV。CPU: 各演算に (float) を付けた厳密な単精度、GPU: 乗算結果に * _One を挟んで mad への融合を防いだもの）
    public static class BisectionTrace
    {
        public const int Version = 1;
        public const int HeaderWords = 8;
        public const int StepWords = 10;
        public const int Steps = StageKernelContext.BisectionSteps;
        public const int Words = HeaderWords + Steps * StepWords;

        public struct Step
        {
            public float T;
            public Vector2 Mid;
            public float Px, Py;
            public int X, Y;
            public uint Class;
            public Vector2 Alt;
        }

        public static uint InitialClass(uint[] words) => words[1];
        public static uint ClassOfEnd(uint[] words) => words[3];
        public static float Weight(uint[] words) => ToFloat(words[2]);

        public static Step GetStep(uint[] words, int step)
        {
            int b = HeaderWords + step * StepWords;
            return new Step
            {
                T = ToFloat(words[b + 0]),
                Mid = new Vector2(ToFloat(words[b + 1]), ToFloat(words[b + 2])),
                Px = ToFloat(words[b + 3]),
                Py = ToFloat(words[b + 4]),
                X = (int)words[b + 5],
                Y = (int)words[b + 6],
                Class = words[b + 7],
                Alt = new Vector2(ToFloat(words[b + 8]), ToFloat(words[b + 9]))
            };
        }

        public static float ToFloat(uint bits) => BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
        public static uint ToBits(float value) => BitConverter.ToUInt32(BitConverter.GetBytes(value), 0);

        // float を値とビット列（16 進）で表す
        public static string Show(float value) => value.ToString("R") + " (0x" + ToBits(value).ToString("X8") + ")";
        public static string Show(Vector2 value) => "(" + value.x.ToString("R") + ", " + value.y.ToString("R") + ") [0x" + ToBits(value.x).ToString("X8") + ", 0x" + ToBits(value.y).ToString("X8") + "]";

        public static string ClassName(uint value) => value == 0 ? "0=透明" : (value == 2 ? "2=不透明" : "1=閾値と同じ");

        // 2 つの記録（CPU と GPU）を段ごとに並べ、最初に違う段と違う値を示す（記録が片方しか無ければ片方だけ）
        public static string Format(uint[] cpu, uint[] gpu, string cpuLabel, string gpuLabel)
        {
            StringBuilder text = new StringBuilder();
            uint[] any = cpu ?? gpu;
            if (any == null)
                return "二分探索の途中経過はありません。";
            if (any[0] != Version)
                text.AppendLine("  記録の版が異なります: " + any[0] + "（期待 " + Version + "）");
            text.AppendLine("  端点の分類: uv1 " + ClassName(InitialClass(any)) + " / uv2 " + ClassName(ClassOfEnd(any)) + "、テクスチャ " + any[4] + "x" + any[5]);
            if (cpu != null && gpu != null && (InitialClass(cpu) != InitialClass(gpu) || ClassOfEnd(cpu) != ClassOfEnd(gpu)))
                text.AppendLine("  ※ 端点の分類が CPU と GPU で異なります（GPU: uv1 " + ClassName(InitialClass(gpu)) + " / uv2 " + ClassName(ClassOfEnd(gpu)) + "）");

            int firstDifference = -1;
            for (int s = 0; s < Steps; s++)
            {
                text.AppendLine("  段 " + (s + 1) + ":");
                if (cpu != null) text.AppendLine("    " + cpuLabel + ": " + FormatStep(GetStep(cpu, s)));
                if (gpu != null) text.AppendLine("    " + gpuLabel + ": " + FormatStep(GetStep(gpu, s)));
                if (cpu != null && gpu != null)
                {
                    string difference = Compare(GetStep(cpu, s), GetStep(gpu, s));
                    if (difference != null)
                    {
                        text.AppendLine("    → 不一致: " + difference);
                        if (firstDifference < 0)
                            firstDifference = s;
                    }
                }
            }
            if (cpu != null) text.AppendLine("  最終の重み " + cpuLabel + ": " + Show(Weight(cpu)));
            if (gpu != null) text.AppendLine("  最終の重み " + gpuLabel + ": " + Show(Weight(gpu)));
            if (cpu != null && gpu != null)
            {
                text.AppendLine(firstDifference < 0
                    ? (ToBits(Weight(cpu)) == ToBits(Weight(gpu))
                        ? "  途中経過は全段一致し、重みも同じです（この辺の二分探索は CPU と GPU で一致。差は別の頂点・別の段の処理にあります）。"
                        : "  途中経過は全段一致しましたが最終の重みが異なります（記録用カーネルと本番カーネルの違いを疑ってください）。")
                    : "  最初に違うのは段 " + (firstDifference + 1) + " です。" + Diagnose(GetStep(cpu, firstDifference), GetStep(gpu, firstDifference)));
            }
            return text.ToString();
        }

        private static string FormatStep(Step step)
        {
            return "t=" + Show(step.T) + " 中点 UV " + Show(step.Mid) + " → px, py = " + step.Px.ToString("R") + ", " + step.Py.ToString("R") +
                   " → テクセル (" + step.X + ", " + step.Y + ") 分類 " + ClassName(step.Class) +
                   (ToBits(step.Alt.x) == ToBits(step.Mid.x) && ToBits(step.Alt.y) == ToBits(step.Mid.y) ? "; 別計算法の中点 = 同じ" : "; 別計算法の中点 " + Show(step.Alt));
        }

        // 同じ段の CPU と GPU の値のうち最初に違うものを説明する（null なら一致）
        private static string Compare(Step a, Step b)
        {
            if (ToBits(a.T) != ToBits(b.T)) return "t が違う（前の段までの判定が違う）";
            if (ToBits(a.Mid.x) != ToBits(b.Mid.x) || ToBits(a.Mid.y) != ToBits(b.Mid.y)) return "中点 UV が違う（同じ t と端点から計算した a + (b - a) * t の結果が違う: 融合（mad）か演算精度の差）";
            if (ToBits(a.Px) != ToBits(b.Px) || ToBits(a.Py) != ToBits(b.Py)) return "px, py が違う（同じ中点 UV に (幅 - 1) を掛けた結果が違う）";
            if (a.X != b.X || a.Y != b.Y) return "テクセル座標が違う（同じ px, py の (int) 切り捨てかラップの結果が違う）";
            if (a.Class != b.Class) return "分類が違う（同じテクセルのアルファ値または分類表が違う: マスクの転送内容を疑う）";
            return null;
        }

        private static string Diagnose(Step cpu, Step gpu)
        {
            if (ToBits(cpu.Mid.x) == ToBits(gpu.Mid.x) && ToBits(cpu.Mid.y) == ToBits(gpu.Mid.y))
                return "";
            bool cpuStrict = ToBits(cpu.Mid.x) == ToBits(cpu.Alt.x) && ToBits(cpu.Mid.y) == ToBits(cpu.Alt.y);
            bool gpuBarrier = ToBits(gpu.Mid.x) == ToBits(gpu.Alt.x) && ToBits(gpu.Mid.y) == ToBits(gpu.Alt.y);
            bool gpuAltMatchesCpu = ToBits(gpu.Alt.x) == ToBits(cpu.Mid.x) && ToBits(gpu.Alt.y) == ToBits(cpu.Mid.y);
            StringBuilder text = new StringBuilder();
            text.Append(cpuStrict ? " CPU の中点は厳密な単精度の再計算と同じ。" : " CPU の中点は厳密な単精度の再計算と違う（Mono が float を倍精度で計算している疑い）。");
            text.Append(gpuBarrier ? " GPU の中点は融合防止版と同じ。" : " GPU の中点は融合防止版と違う（乗算と加算が mad に融合されている）。");
            if (gpuAltMatchesCpu) text.Append(" GPU の融合防止版は CPU の中点と一致する。");
            return text.ToString();
        }
    }
}
