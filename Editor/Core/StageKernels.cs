using UnityEngine;

namespace MeshDeletionTool
{
    // 1 枚のマスクの参照情報（HLSL の MaskView に対応。HLSL では _MaskInfo の 5 要素と _Alpha への参照）
    public struct MaskView
    {
        // アルファ値（0〜255）。下の行から順に Width 個ずつ
        public byte[] Alpha;
        public int Width;
        public int Height;
        // TextureWrapMode の値（0: Repeat, 1: Clamp, 2: Mirror, 3: MirrorOnce）
        public int WrapU;
        public int WrapV;

        public static MaskView From(AlphaMask mask)
        {
            return new MaskView
            {
                Alpha = mask.AlphaBytes,
                Width = mask.Width,
                Height = mask.Height,
                WrapU = (int)mask.WrapModeU,
                WrapV = (int)mask.WrapModeV
            };
        }
    }

    // 処理段のカーネル（頂点の透明判定・三角形の細分化判定・辺上の境界点の二分探索）の C# 実装
    // Editor/Shaders/MeshDeletionStages.compute の HLSL と関数・行単位で対応させてあり、CPU バックエンドはこのクラスをそのまま実行する。
    // GPU と同じ結果（ビット単位）を得るための規則:
    //   - float の演算は HLSL と同じ順序・同じ型で書く。HLSL 側は 1 演算ずつ別の文にして precise を付け、融合（mad）と並べ替えを禁止する
    //     （.compute 先頭の「precise の規則」参照）。C# 側は式のまま書いてよい: Mono（x64、SSE）は float の各演算を単精度に丸め、融合はしないので
    //     文の分け方で結果は変わらず、分けない方が速い（分けた版は Unity 上で約 3 倍遅かった）
    //   - 閾値との比較は AlphaClass 表（アルファ値 → 閾値未満 / 等しい / より大きい）の参照だけで行い、float の比較を GPU に持ち込まない
    //   - テクセル座標は従来と同じ (int)(u * (幅 - 1))、ラップは同じ整数演算
    //   - 最長辺の判定は平方根を取らず 2 乗のまま比較する（正しく丸められた sqrt では sqrt(s) < 1 ⇔ s < 1）
    //   - 多角形の面積 0 の判定は Mathf.Approximately ではなく == 0（差は非正規化数の面積のみ。テクセル座標では起こらない）
    //   - バウンディングボックスは float のまま範囲内に制限してから floor する（|x| < 2^31 では従来の順序と同じ結果）
    // フィールド名は HLSL のバッファ・定数名（先頭の _ を除く）に合わせてある
    public sealed class StageKernelContext
    {
        public const int BisectionSteps = 10;
        public const int MaxPolygonPoints = 6;
        public const int WrapRepeat = 0, WrapClamp = 1, WrapMirror = 2, WrapMirrorOnce = 3;

        // _MaskInfo / _Alpha
        public MaskView[] Masks;
        // _AlphaClass: アルファ値（0〜255）→ 0: 閾値未満（透明）, 1: 閾値と等しい, 2: 閾値より大きい
        public byte[] AlphaClass;
        // _UV
        public Vector2[] UV;
        // _Items: 要素毎の頂点番号（RefineTriangleTest: 3 つずつ、BisectEdges: 昇順に 2 つずつ）
        public int[] Items;
        // _ItemMask: 要素毎のマスク番号
        public int[] ItemMask;
        // _OutFlags / _OutWeights
        public int[] OutFlags;
        public float[] OutWeights;

        // 定数
        // 要素番号の始まり（GPU では 1 回のディスパッチで扱える要素数を超えるとき分割して呼ぶ。CPU では常に 0）
        public int ItemOffset;
        public int Count;
        public int VertexCount;
        public int RefineFull;
        public int RefinePartial;
        public int ChordTolerance;
        public int MaxRasterSize;

        // 診断用: 多角形の内部にあるか判定したテクセルの数と、そのうち不透明か判定したテクセルの数（CPU 実行時のみ）
        public long RasterTexelTests;
        public long RasterInsideTexels;

        // HLSL のローカル配列に相当する作業領域（単一スレッドで使う）
        private readonly float[] px = new float[MaxPolygonPoints];
        private readonly float[] py = new float[MaxPolygonPoints];
        private readonly float[] edgeDx = new float[MaxPolygonPoints];
        private readonly float[] edgeDy = new float[MaxPolygonPoints];
        private readonly Vector2[] polygon = new Vector2[MaxPolygonPoints];
        private readonly Vector2[] uv = new Vector2[3];
        private readonly int[] index = new int[3];
        private readonly byte[] cls = new byte[3];

        // アルファ値の分類表を作る（閾値との比較はここでだけ行う。value / 255f は AlphaMask.Alpha と同じ式）
        public static byte[] BuildAlphaClassTable(float alphaThreshold)
        {
            byte[] table = new byte[256];
            for (int value = 0; value < 256; value++)
            {
                float alpha = value / 255f;
                table[value] = alpha < alphaThreshold ? (byte)0 : (alpha > alphaThreshold ? (byte)2 : (byte)1);
            }
            return table;
        }

        // ---- ディスパッチ（HLSL では numthreads(64,1,1) のグループを ceil(Count / 64) 個。要素番号 id は 0 〜 Count-1）

        public void DispatchClassifyVertices()
        {
            for (int id = 0; id < Count; id++) ClassifyVertices(id);
        }

        public void DispatchRefineTriangleTest()
        {
            for (int id = 0; id < Count; id++) RefineTriangleTest(id);
        }

        public void DispatchBisectEdges()
        {
            for (int id = 0; id < Count; id++) BisectEdges(id);
        }

        // ---- カーネル本体

        // 要素 = (マスク番号, 頂点番号)。頂点が透明（アルファ値 < 閾値）なら 1
        public void ClassifyVertices(int id)
        {
            int item = id + ItemOffset;
            if (item >= Count) return;
            int maskIndex = item / VertexCount;
            int vertex = item - maskIndex * VertexCount;
            MaskView m = Masks[maskIndex];
            OutFlags[item] = SampleClass(m, UV[vertex]) == 0 ? 1 : 0;
        }

        // 要素 = 三角形。細分化するなら 1
        public void RefineTriangleTest(int id)
        {
            int item = id + ItemOffset;
            if (item >= Count) return;
            MaskView m = Masks[ItemMask[item]];
            int i0 = Items[item * 3 + 0];
            int i1 = Items[item * 3 + 1];
            int i2 = Items[item * 3 + 2];
            OutFlags[item] = RefineTest(m, UV[i0], UV[i1], UV[i2], i0, i1, i2) ? 1 : 0;
        }

        // 要素 = 辺（頂点番号の昇順）。境界エッジなら OutFlags = 1 と二分探索の重み、そうでなければ 0 と 0
        public void BisectEdges(int id)
        {
            int item = id + ItemOffset;
            if (item >= Count) return;
            MaskView m = Masks[ItemMask[item]];
            int a = Items[item * 2 + 0];
            int b = Items[item * 2 + 1];
            byte classA = SampleClass(m, UV[a]);
            byte classB = SampleClass(m, UV[b]);
            if (IsBoundaryClass(classA, classB))
            {
                OutFlags[item] = 1;
                OutWeights[item] = BisectEdge(m, UV[a], UV[b]);
            }
            else
            {
                OutFlags[item] = 0;
                OutWeights[item] = 0f;
            }
        }

        // ---- 共通関数（HLSL と同名）

        // テクセルのアルファ値（HLSL では 4 バイトを詰めた uint から取り出す）
        private static byte LoadAlpha(MaskView m, int x, int y)
        {
            return m.Alpha[y * m.Width + x];
        }

        // 範囲外のテクセル座標をラップモードに従って範囲内に写す（AlphaMask.Wrap と同じ結果）
        private static int WrapTexel(int i, int size, int mode)
        {
            if (mode == WrapClamp)
            {
                return i < 0 ? 0 : size - 1;
            }
            if (mode == WrapMirror)
            {
                int period = 2 * size;
                int m = i % period;
                if (m < 0) m += period;
                return m < size ? m : period - 1 - m;
            }
            if (mode == WrapMirrorOnce)
            {
                int m = i < 0 ? -1 - i : i;
                return m < size ? m : size - 1;
            }
            int r = i % size;
            if (r < 0) r += size;
            return r;
        }

        // UV座標が示すテクセル（(int)(u * (幅 - 1))、範囲外はラップ）のアルファ値の分類
        private byte SampleClass(MaskView m, Vector2 uv)
        {
            float px = uv.x * (float)(m.Width - 1);
            float py = uv.y * (float)(m.Height - 1);
            int x = (int)px;
            int y = (int)py;
            if (x < 0 || x >= m.Width) x = WrapTexel(x, m.Width, m.WrapU);
            if (y < 0 || y >= m.Height) y = WrapTexel(y, m.Height, m.WrapV);
            return AlphaClass[LoadAlpha(m, x, y)];
        }

        // 片方が閾値未満で他方が閾値より大きい（従来の AlphaSampling.IsBoundaryEdge の条件）
        private static bool IsBoundaryClass(byte class1, byte class2)
        {
            return (class1 == 0 && class2 == 2) || (class1 == 2 && class2 == 0);
        }

        // Vector2.Lerp と同じ式（t を 0〜1 に制限し、a + (b - a) * t）
        private static Vector2 LerpUV(Vector2 a, Vector2 b, float t)
        {
            t = t < 0f ? 0f : (t > 1f ? 1f : t);
            Vector2 r;
            r.x = a.x + (b.x - a.x) * t;
            r.y = a.y + (b.y - a.y) * t;
            return r;
        }

        // 辺上の境界点の重み（従来の AlphaSampling.FindAlphaBoundary と同じ 10 回の二分探索）
        private float BisectEdge(MaskView m, Vector2 uv1, Vector2 uv2)
        {
            byte class1 = SampleClass(m, uv1);
            float tMin = 0f;
            float tMax = 1f;
            for (int i = 0; i < BisectionSteps; i++)
            {
                float t = (tMin + tMax) / 2f;
                Vector2 midUV = LerpUV(uv1, uv2, t);
                byte midClass = SampleClass(m, midUV);
                if (IsBoundaryClass(class1, midClass))
                {
                    tMax = t;
                }
                else
                {
                    tMin = t;
                    class1 = midClass;
                }
            }
            return (tMin + tMax) / 2f;
        }

        // 凸多角形（UV座標、pointCount 個）の内部にある不透明テクセルの数（stopAt に達したら打ち切る）
        private int CountOpaqueTexels(MaskView m, Vector2[] polygon, int pointCount, int stopAt)
        {
            if (pointCount < 3) return 0;

            // テクセル座標系 (x = u * (w - 1), y = v * (h - 1)) に変換し、バウンディングボックスを求める
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < pointCount; i++)
            {
                px[i] = polygon[i].x * (float)(m.Width - 1);
                py[i] = polygon[i].y * (float)(m.Height - 1);
                minX = minX < px[i] ? minX : px[i];
                minY = minY < py[i] ? minY : py[i];
                maxX = maxX > px[i] ? maxX : px[i];
                maxY = maxY > py[i] ? maxY : py[i];
            }

            // 多角形の向き（面積 0 なら内部のテクセルは無い）
            float signedArea = 0f;
            for (int i = 0; i < pointCount; i++)
            {
                int j = (i + 1) % pointCount;
                signedArea += px[i] * py[j] - px[j] * py[i];
            }
            if (signedArea == 0f) return 0;
            float orientation = signedArea > 0f ? 1f : -1f;

            // テクセル範囲（テクスチャの範囲内に制限する）
            int x0 = FloorClamped(minX, m.Width - 1);
            int x1 = FloorClamped(maxX, m.Width - 1);
            int y0 = FloorClamped(minY, m.Height - 1);
            int y1 = FloorClamped(maxY, m.Height - 1);

            // 巨大な多角形は間引いてサンプリングする（ceil(n / MaxRasterSize) の整数演算）
            int stride = 1;
            if (MaxRasterSize >= 1)
            {
                int extent = x1 - x0 + 1 > y1 - y0 + 1 ? x1 - x0 + 1 : y1 - y0 + 1;
                stride = (extent + MaxRasterSize - 1) / MaxRasterSize;
                if (stride < 1) stride = 1;
            }

            // 各辺の方向
            for (int i = 0; i < pointCount; i++)
            {
                int j = (i + 1) % pointCount;
                edgeDx[i] = px[j] - px[i];
                edgeDy[i] = py[j] - py[i];
            }

            int count = 0;
            for (int y = y0; y <= y1; y += stride)
            {
                float centerY = (float)y + 0.5f;
                for (int x = x0; x <= x1; x += stride)
                {
                    // テクセルの中心が多角形の内部にあるか
                    float centerX = (float)x + 0.5f;
                    RasterTexelTests++;
                    bool inside = true;
                    for (int i = 0; i < pointCount; i++)
                    {
                        float cross = edgeDx[i] * (centerY - py[i]) - edgeDy[i] * (centerX - px[i]);
                        if (cross * orientation < 0f)
                        {
                            inside = false;
                            break;
                        }
                    }
                    if (!inside) continue;
                    RasterInsideTexels++;
                    if (AlphaClass[LoadAlpha(m, x, y)] != 0)
                    {
                        count++;
                        if (count >= stopAt) return count;
                    }
                }
            }
            return count;
        }

        // 0〜max に制限してから floor した整数（Mathf.Clamp(Mathf.FloorToInt(v), 0, max) と |v| < 2^31 で同じ）
        private static int FloorClamped(float v, int max)
        {
            float maxF = (float)max;
            if (v < 0f) v = 0f;
            else if (v > maxF) v = maxF;
            return (int)System.Math.Floor(v);
        }

        // 三角形を細分化すべきか（AlphaBoundaryRefiner.ShouldSubdivide と同じ判定）
        private bool RefineTest(MaskView m, Vector2 uv0, Vector2 uv1, Vector2 uv2, int i0, int i1, int i2)
        {
            uv[0] = uv0; uv[1] = uv1; uv[2] = uv2;
            index[0] = i0; index[1] = i1; index[2] = i2;

            // 最長辺が 1 テクセル未満の三角形はこれ以上細分化しない（2 乗のまま比較する）
            float longestSq = 0f;
            for (int i = 0; i < 3; i++)
            {
                int j = (i + 1) % 3;
                float ex = (uv[j].x - uv[i].x) * (float)(m.Width - 1);
                float ey = (uv[j].y - uv[i].y) * (float)(m.Height - 1);
                float sq = ex * ex + ey * ey;
                longestSq = longestSq > sq ? longestSq : sq;
            }
            if (longestSq < 1f) return false;

            cls[0] = SampleClass(m, uv[0]);
            cls[1] = SampleClass(m, uv[1]);
            cls[2] = SampleClass(m, uv[2]);
            int transparentCount = (cls[0] == 0 ? 1 : 0) + (cls[1] == 0 ? 1 : 0) + (cls[2] == 0 ? 1 : 0);

            // 3頂点とも透明: 内部に不透明テクセルがあれば細分化する（修正1）
            if (transparentCount == 3)
            {
                if (RefineFull == 0) return false;
                polygon[0] = uv[0]; polygon[1] = uv[1]; polygon[2] = uv[2];
                return CountOpaqueTexels(m, polygon, 3, 1) > 0;
            }
            // 3頂点とも不透明: 既存処理でそのまま残るため対象外
            if (transparentCount == 0) return false;
            // 一部の頂点が透明: 削除される側の多角形に不透明テクセルが含まれていれば細分化する（修正2）
            if (RefinePartial == 0) return false;

            // 削除される側の多角形（透明な頂点と、境界エッジ上の境界点を外周順に）
            int pointCount = 0;
            for (int i = 0; i < 3; i++)
            {
                int j = (i + 1) % 3;
                if (cls[i] == 0)
                {
                    polygon[pointCount++] = uv[i];
                }
                if (IsBoundaryClass(cls[i], cls[j]))
                {
                    // 辺の向きは頂点番号の昇順にして切断処理と同一の境界点を得る
                    Vector2 uvA = index[i] < index[j] ? uv[i] : uv[j];
                    Vector2 uvB = index[i] < index[j] ? uv[j] : uv[i];
                    float weight = BisectEdge(m, uvA, uvB);
                    polygon[pointCount++] = LerpUV(uvA, uvB, weight);
                }
            }
            return CountOpaqueTexels(m, polygon, pointCount, ChordTolerance + 1) > ChordTolerance;
        }
    }
}
