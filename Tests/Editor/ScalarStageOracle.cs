using System.Collections.Generic;
using UnityEngine;
using MeshDeletionTool;

// 処理段の参照実装（テスト用）: バックエンド導入前の AlphaBoundaryRefiner / AlphaMeshCutter が要素毎に行っていた判定をそのまま写したもの
// StageKernelContext（CPU / GPU で共用するカーネルの数式）が従来の判定と一致することを確かめる基準として使う
public static class ScalarStageOracle
{
    // 頂点の透明判定（AlphaMeshCutter.GetVerticesToRemove）
    public static bool IsTransparent(AlphaMask mask, Vector2 uv, float alphaThreshold)
    {
        return AlphaSampling.IsTransparent(mask, uv, alphaThreshold);
    }

    // 辺の境界判定と二分探索（AlphaMeshCutter.AddEdgeIntersectionPoints）
    public static bool BisectEdge(AlphaMask mask, Vector2 uv1, Vector2 uv2, float alphaThreshold, out float weight)
    {
        weight = 0f;
        if (!AlphaSampling.IsBoundaryEdge(mask, uv1, uv2, alphaThreshold))
            return false;
        weight = AlphaSampling.FindAlphaBoundary(mask, uv1, uv2, alphaThreshold);
        return true;
    }

    // 三角形を細分化すべきか（AlphaBoundaryRefiner.ShouldSubdivide）
    public static bool ShouldSubdivide(AlphaMask mask, Vector2[] uvs, int indexA, int indexB, int indexC, float alphaThreshold, RefineTestParams settings)
    {
        Vector2[] uv = { uvs[indexA], uvs[indexB], uvs[indexC] };

        // 最長辺が1テクセル未満の三角形はこれ以上細分化しない（終了保証）
        if (LongestEdgeInTexels(mask, uv) < 1f)
        {
            return false;
        }

        bool[] transparent =
        {
            AlphaSampling.IsTransparent(mask, uv[0], alphaThreshold),
            AlphaSampling.IsTransparent(mask, uv[1], alphaThreshold),
            AlphaSampling.IsTransparent(mask, uv[2], alphaThreshold)
        };
        int transparentCount = (transparent[0] ? 1 : 0) + (transparent[1] ? 1 : 0) + (transparent[2] ? 1 : 0);

        // 3頂点とも透明: 内部に不透明テクセルがあれば細分化する（修正1）
        if (transparentCount == 3)
        {
            return settings.RefineFullyTransparentTriangles && CountOpaqueTexels(mask, uv, alphaThreshold, 1, settings.MaxRasterSize) > 0;
        }
        // 3頂点とも不透明: 既存処理でそのまま残るため対象外
        if (transparentCount == 0)
        {
            return false;
        }
        // 一部の頂点が透明: 既存処理で削除される側の多角形に不透明テクセルが含まれていれば細分化する（修正2）
        if (!settings.RefinePartiallyCutTriangles)
        {
            return false;
        }
        int[] index = { indexA, indexB, indexC };
        List<Vector2> removedPolygon = BuildRemovedPolygon(mask, uv, index, transparent, alphaThreshold);
        return CountOpaqueTexels(mask, removedPolygon, alphaThreshold, settings.ChordToleranceTexels + 1, settings.MaxRasterSize) > settings.ChordToleranceTexels;
    }

    // 既存処理と同じ境界点（辺上の二分探索）を使い、三角形のうち削除される側の多角形（UV座標、外周順）を作る
    private static List<Vector2> BuildRemovedPolygon(AlphaMask mask, Vector2[] uv, int[] index, bool[] transparent, float alphaThreshold)
    {
        List<Vector2> polygon = new List<Vector2>(4);
        for (int i = 0; i < 3; i++)
        {
            int j = (i + 1) % 3;
            if (transparent[i])
            {
                polygon.Add(uv[i]);
            }
            if (AlphaSampling.IsBoundaryEdge(mask, uv[i], uv[j], alphaThreshold))
            {
                (Vector2 uvA, Vector2 uvB) = index[i] < index[j] ? (uv[i], uv[j]) : (uv[j], uv[i]);
                float weight = AlphaSampling.FindAlphaBoundary(mask, uvA, uvB, alphaThreshold);
                polygon.Add(Vector2.Lerp(uvA, uvB, weight));
            }
        }
        return polygon;
    }

    // 三角形の最長辺の長さ（テクセル単位）
    private static float LongestEdgeInTexels(AlphaMask mask, Vector2[] uv)
    {
        Vector2 scale = new Vector2(mask.Width - 1, mask.Height - 1);
        float longest = 0f;
        for (int i = 0; i < 3; i++)
        {
            Vector2 edge = Vector2.Scale(uv[(i + 1) % 3] - uv[i], scale);
            longest = Mathf.Max(longest, edge.magnitude);
        }
        return longest;
    }

    // アルファ値（0〜255）ごとに「alphaThreshold 以上（不透明）か」を表す 256 個の表
    public static bool[] BuildOpaqueTable(float alphaThreshold)
    {
        return ScalarStageOracleTables.BuildOpaqueTable(alphaThreshold);
    }

    // 凸多角形（UV座標）の内部にある不透明テクセルの数を数える（stopAt に達したら打ち切る）
    public static int CountOpaqueTexels(AlphaMask mask, IList<Vector2> polygon, float alphaThreshold, int stopAt, int maxRasterSize)
    {
        if (polygon.Count < 3)
        {
            return 0;
        }

        int width = mask.Width;
        int height = mask.Height;

        int pointCount = polygon.Count;
        Vector2[] points = new Vector2[pointCount];
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int i = 0; i < pointCount; i++)
        {
            points[i] = new Vector2(polygon[i].x * (width - 1), polygon[i].y * (height - 1));
            minX = Mathf.Min(minX, points[i].x);
            minY = Mathf.Min(minY, points[i].y);
            maxX = Mathf.Max(maxX, points[i].x);
            maxY = Mathf.Max(maxY, points[i].y);
        }

        float signedArea = 0f;
        for (int i = 0; i < pointCount; i++)
        {
            Vector2 p = points[i];
            Vector2 q = points[(i + 1) % pointCount];
            signedArea += p.x * q.y - q.x * p.y;
        }
        if (Mathf.Approximately(signedArea, 0f))
        {
            return 0;
        }
        float orientation = signedArea > 0f ? 1f : -1f;

        int x0 = Mathf.Clamp(Mathf.FloorToInt(minX), 0, width - 1);
        int x1 = Mathf.Clamp(Mathf.FloorToInt(maxX), 0, width - 1);
        int y0 = Mathf.Clamp(Mathf.FloorToInt(minY), 0, height - 1);
        int y1 = Mathf.Clamp(Mathf.FloorToInt(maxY), 0, height - 1);

        int stride = Mathf.Max(1, Mathf.CeilToInt(Mathf.Max(x1 - x0 + 1, y1 - y0 + 1) / (float)maxRasterSize));

        float[] edgeX = new float[pointCount], edgeY = new float[pointCount], edgeDx = new float[pointCount], edgeDy = new float[pointCount];
        for (int i = 0; i < pointCount; i++)
        {
            Vector2 p = points[i];
            Vector2 q = points[(i + 1) % pointCount];
            edgeX[i] = p.x;
            edgeY[i] = p.y;
            edgeDx[i] = q.x - p.x;
            edgeDy[i] = q.y - p.y;
        }
        bool[] opaque = BuildOpaqueTable(alphaThreshold);

        int count = 0;
        for (int y = y0; y <= y1; y += stride)
        {
            float centerY = y + 0.5f;
            for (int x = x0; x <= x1; x += stride)
            {
                float centerX = x + 0.5f;
                bool inside = true;
                for (int i = 0; i < pointCount; i++)
                {
                    float cross = edgeDx[i] * (centerY - edgeY[i]) - edgeDy[i] * (centerX - edgeX[i]);
                    if (cross * orientation < 0f)
                    {
                        inside = false;
                        break;
                    }
                }
                if (!inside)
                {
                    continue;
                }
                if (opaque[mask.AlphaByte(x, y)])
                {
                    count++;
                    if (count >= stopAt)
                    {
                        return count;
                    }
                }
            }
        }
        return count;
    }
}

// アルファ値（0〜255）ごとに「alphaThreshold 以上（不透明）か」を表す 256 個の表（StageKernelContext.BuildAlphaClassTable の参照実装）
public static class ScalarStageOracleTables
{
    public static bool[] BuildOpaqueTable(float alphaThreshold)
    {
        bool[] table = new bool[256];
        for (int value = 0; value < 256; value++)
        {
            table[value] = value / 255f >= alphaThreshold;
        }
        return table;
    }
}

// 従来の処理（バックエンド導入前）が使っていた、UV座標が示すテクセルのアルファ値の判定。製品コードでは StageKernelContext に置き換わり、
// ここでは参照実装（ScalarStageOracle）とテストだけが使う
// UV座標が示すテクセルのアルファ値の判定（削除処理・細分化・辺上の境界点の二分探索で共用）
public static class AlphaSampling
{
    // UV座標が示すテクセルのアルファ値
    public static float SampleAlpha(AlphaMask mask, Vector2 uv)
    {
        return mask.AlphaAt(uv);
    }

    // UV座標が示すテクセルが透明（削除対象）かどうか
    public static bool IsTransparent(AlphaMask mask, Vector2 uv, float alphaThreshold)
    {
        return SampleAlpha(mask, uv) < alphaThreshold;
    }

    // UV座標が示すテクスチャのピクセルが境界エッジかどうかを判定する関数
    public static bool IsBoundaryEdge(AlphaMask mask, Vector2 uv1, Vector2 uv2, float alphaThreshold)
    {
        // 両端点のピクセルのアルファ値を取得
        float alpha1 = SampleAlpha(mask, uv1);
        float alpha2 = SampleAlpha(mask, uv2);

        // 片方のピクセルが透明で、もう片方が透明でない場合は境界エッジとする
        return (alpha1 < alphaThreshold && alpha2 > alphaThreshold) || (alpha1 > alphaThreshold && alpha2 < alphaThreshold);
    }

    // テクスチャのアルファ値に基づき、エッジ上の境界点のUV座標の補完用重みを求める
    public static float FindAlphaBoundary(AlphaMask mask, Vector2 uv1, Vector2 uv2, float alphaThreshold)
    {
        // 開始点のアルファ値を取得
        float alpha1 = SampleAlpha(mask, uv1);

        float tMin = 0.0f;
        float tMax = 1.0f;

        // 二分探索を用いて境界点を探す
        for (int i = 0; i < 10; i++)
        {
            float t = (tMin + tMax) / 2.0f;  // 中間点の係数
            // UV座標の中間点のアルファ値を取得
            Vector2 midUV = Vector2.Lerp(uv1, uv2, t);
            float midAlpha = SampleAlpha(mask, midUV);

            // 境界条件に応じて探索範囲を狭める
            if ((alpha1 < alphaThreshold && midAlpha > alphaThreshold) || (alpha1 > alphaThreshold && midAlpha < alphaThreshold))
            {
                tMax = t; // 境界があると考えられる範囲を左側に絞り込む
            }
            else
            {
                tMin = t; // 境界があると考えられる範囲を右側に絞り込む
                alpha1 = midAlpha;
            }
        }

        // 最終的な境界点の重みを返す
        float weight = (tMin + tMax) / 2.0f;
        return weight;
    }
}

// 参照実装をバックエンドとして包んだもの（パイプライン全体を従来の判定で実行し、CpuStageBackend の結果と比べるためのもの）
public class ScalarOracleBackend : IAlphaStageBackend
{
    public string Name => "Scalar oracle";
    private readonly List<StageTiming> timings = new List<StageTiming>();
    public IReadOnlyList<StageTiming> Timings => timings;
    public void ResetTimings() { timings.Clear(); }
    public void Dispose() { }

    public bool[][] ClassifyVertices(Vector2[] uvs, AlphaMask[] masks, float alphaThreshold)
    {
        bool[][] result = new bool[masks.Length][];
        for (int k = 0; k < masks.Length; k++)
        {
            if (masks[k] == null) continue;
            result[k] = new bool[uvs.Length];
            for (int v = 0; v < uvs.Length; v++)
                result[k][v] = ScalarStageOracle.IsTransparent(masks[k], uvs[v], alphaThreshold);
        }
        return result;
    }

    public bool[][] TestRefineTriangles(Vector2[] uvs, int[][] triangleSets, AlphaMask[] masks, RefineTestParams settings, float alphaThreshold)
    {
        bool[][] result = new bool[triangleSets.Length][];
        for (int k = 0; k < triangleSets.Length; k++)
        {
            if (masks[k] == null || triangleSets[k] == null) continue;
            int[] triangles = triangleSets[k];
            result[k] = new bool[triangles.Length / 3];
            for (int i = 0; i < triangles.Length; i += 3)
                result[k][i / 3] = ScalarStageOracle.ShouldSubdivide(masks[k], uvs, triangles[i], triangles[i + 1], triangles[i + 2], alphaThreshold, settings);
        }
        return result;
    }

    public void BisectEdges(Vector2[] uvs, int[][] edgeSets, AlphaMask[] masks, float alphaThreshold, out bool[][] isBoundary, out float[][] weights)
    {
        isBoundary = new bool[edgeSets.Length][];
        weights = new float[edgeSets.Length][];
        for (int k = 0; k < edgeSets.Length; k++)
        {
            if (masks[k] == null || edgeSets[k] == null) continue;
            int[] edges = edgeSets[k];
            isBoundary[k] = new bool[edges.Length / 2];
            weights[k] = new float[edges.Length / 2];
            for (int e = 0; e < edges.Length; e += 2)
                isBoundary[k][e / 2] = ScalarStageOracle.BisectEdge(masks[k], uvs[edges[e]], uvs[edges[e + 1]], alphaThreshold, out weights[k][e / 2]);
        }
    }
}
