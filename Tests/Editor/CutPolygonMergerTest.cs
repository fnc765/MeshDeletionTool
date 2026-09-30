using UnityEngine;
using NUnit.Framework;
using System.Collections.Generic;
using MeshDeletionTool;

// CutPolygonMerger の Mesh に依存しない部分（外周の構築、一直線上の頂点の削除、切り口の間引き）のテスト
public class CutPolygonMergerTest
{
    // 元の三角形 A(0,0,0) B(1,0,0) C(0,1,0) を辺の中点 m0(0.5,0,0) m1(0.5,0.5,0) m2(0,0.5,0) で4分割した頂点配列
    private static readonly Vector3[] RefinedPositions =
    {
        new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0),
        new Vector3(0.5f, 0, 0), new Vector3(0.5f, 0.5f, 0), new Vector3(0, 0.5f, 0)
    };
    private static readonly Vector3[] OriginalPositions = { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0) };
    private static readonly int[] OriginalTriangle = { 0, 1, 2 };
    private static readonly int[] FourSubTriangles = { 0, 3, 5, 3, 1, 4, 5, 4, 2, 3, 4, 5 };

    private static float SignedArea2D(Vector3[] positions, int[] triangles)
    {
        float area = 0f;
        for (int i = 0; i < triangles.Length; i += 3)
        {
            Vector3 a = positions[triangles[i]], b = positions[triangles[i + 1]], c = positions[triangles[i + 2]];
            area += 0.5f * ((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x));
        }
        return area;
    }

    // 辺（頂点番号の順不同ペア）ごとの使用回数
    private static Dictionary<(int, int), int> CountEdges(params int[][] triangleArrays)
    {
        Dictionary<(int, int), int> count = new Dictionary<(int, int), int>();
        foreach (int[] triangles in triangleArrays)
        {
            for (int i = 0; i < triangles.Length; i += 3)
            {
                for (int e = 0; e < 3; e++)
                {
                    int a = triangles[i + e], b = triangles[i + (e + 1) % 3];
                    (int, int) key = a < b ? (a, b) : (b, a);
                    count[key] = count.TryGetValue(key, out int c) ? c + 1 : 1;
                }
            }
        }
        return count;
    }

    [Test]
    public void RefinedTriangle_MergesBackToOne()
    {
        CutPolygonMerger merger = new CutPolygonMerger();
        int[][] result = merger.MergeTriangles(RefinedPositions, null, new[] { FourSubTriangles }, new[] { new[] { 0, 0, 0, 0 } },
                                               OriginalPositions, new[] { OriginalTriangle }, 3, null, out int[][] parents);

        Assert.AreEqual(3, result[0].Length, "1つの三角形に戻る");
        CollectionAssert.AreEquivalent(new[] { 0, 1, 2 }, result[0], "元の3頂点だけを使う");
        Assert.AreEqual(0.5f, SignedArea2D(RefinedPositions, result[0]), 1e-6f, "面積と巻き順（反時計回り）が保たれる");
        Assert.AreEqual(new[] { 0 }, parents[0]);
        Assert.AreEqual(3, merger.RemovedFlatVertexCount);
        Assert.AreEqual(0, merger.FallbackCount);
        Assert.AreEqual(1, merger.MergedGroupCount);
    }

    // 隣の元三角形 B D C（D = (1,1,0)）を持つ頂点配列。保護される頂点 A B C D を先頭に、中点 m0 m1 m2 を 4 5 6 に置く
    private static readonly Vector3[] TwoTrianglePositions =
    {
        new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 1, 0),
        new Vector3(0.5f, 0, 0), new Vector3(0.5f, 0.5f, 0), new Vector3(0, 0.5f, 0)
    };
    private static readonly Vector3[] TwoTriangleOriginalPositions = { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 1, 0) };
    private static readonly int[] TwoOriginalTriangles = { 0, 1, 2, 1, 3, 2 };
    private static readonly int[] RefinedFirstTriangle = { 0, 4, 6, 4, 1, 5, 6, 5, 2, 4, 5, 6 };   // 元三角形 0 の4分割

    [Test]
    public void MidpointUsedByNeighbour_IsKept()
    {
        // 隣の元三角形 1 は m1 で2分割された後、片側 (B, D, m1) だけが残った（もう片側は透明で削除された）
        // 残った1枚は m1 を必要とするので、元三角形 0 側でも m1 は取り除かれず、辺 B-m1 は両側から共有される（T字接合にならない）
        int[] neighbour = { 1, 3, 5 };
        int[] triangles = new int[RefinedFirstTriangle.Length + neighbour.Length];
        RefinedFirstTriangle.CopyTo(triangles, 0); neighbour.CopyTo(triangles, RefinedFirstTriangle.Length);
        int[] parents = { 0, 0, 0, 0, 1 };

        CutPolygonMerger merger = new CutPolygonMerger();
        int[][] result = merger.MergeTriangles(TwoTrianglePositions, null, new[] { triangles }, new[] { parents },
                                               TwoTriangleOriginalPositions, new[] { TwoOriginalTriangles }, 4, null, out int[][] newParents);

        Assert.AreEqual(3, result[0].Length / 3, "元三角形 0 は m1 を含む四角形として2枚、元三角形 1 は1枚のまま");
        Assert.AreEqual(0.5f + 0.25f, SignedArea2D(TwoTrianglePositions, result[0]), 1e-6f);
        Dictionary<(int, int), int> edges = CountEdges(result[0]);
        Assert.IsFalse(edges.ContainsKey((1, 2)), "B-C を直接結ぶ辺は無い（m1 で分かれている）");
        Assert.AreEqual(2, edges[(1, 5)], "B-m1 は両側から使われる");
        Assert.AreEqual(1, edges[(2, 5)], "m1-C は元三角形 0 側だけ（隣は削除済み）");
        Assert.AreEqual(2, merger.RemovedFlatVertexCount, "m0 と m2 だけ取り除かれる");
        CollectionAssert.AreEqual(new[] { 0, 0, 1 }, newParents[0]);
    }

    [Test]
    public void FlatMidpointUnusedOnBothSides_IsRemovedOnBothSides()
    {
        // 隣の元三角形 1 も m1 で2分割されたまま残っている場合、m1 は両側で一直線上なので両側とも取り除き、元の2枚に戻る
        int[] neighbour = { 1, 3, 5, 5, 3, 2 };
        int[] triangles = new int[RefinedFirstTriangle.Length + neighbour.Length];
        RefinedFirstTriangle.CopyTo(triangles, 0); neighbour.CopyTo(triangles, RefinedFirstTriangle.Length);
        int[] parents = { 0, 0, 0, 0, 1, 1 };

        CutPolygonMerger merger = new CutPolygonMerger();
        int[][] result = merger.MergeTriangles(TwoTrianglePositions, null, new[] { triangles }, new[] { parents },
                                               TwoTriangleOriginalPositions, new[] { TwoOriginalTriangles }, 4, null, out int[][] newParents);

        Assert.AreEqual(2, result[0].Length / 3);
        Assert.AreEqual(1f, SignedArea2D(TwoTrianglePositions, result[0]), 1e-6f);
        Assert.AreEqual(2, CountEdges(result[0])[(1, 2)], "B-C は両側から使われる");
        Assert.AreEqual(3, merger.RemovedFlatVertexCount);
        CollectionAssert.AreEqual(new[] { 0, 1 }, newParents[0]);
    }

    [Test]
    public void TriangleWithHole_IsLeftUnchanged()
    {
        // 内側の三角形 P Q R が削除されたリング状の形: 外周が2つ（外側は反時計回り、穴は時計回り）になるので再結合しない
        Vector3[] positions =
        {
            new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0),
            new Vector3(0.25f, 0.25f, 0), new Vector3(0.5f, 0.25f, 0), new Vector3(0.25f, 0.5f, 0)
        };
        int[] ring = { 0, 1, 4, 0, 4, 3, 1, 2, 5, 1, 5, 4, 2, 0, 3, 2, 3, 5 };
        CutPolygonMerger merger = new CutPolygonMerger();
        int[][] result = merger.MergeTriangles(positions, null, new[] { ring }, new[] { new[] { 0, 0, 0, 0, 0, 0 } },
                                               OriginalPositions, new[] { OriginalTriangle }, 3, null, out _);

        CollectionAssert.AreEqual(ring, result[0], "三角形はそのまま");
        Assert.AreEqual(1, merger.FallbackHoleCount);
        Assert.AreEqual(0, merger.MergedGroupCount);
    }

    [Test]
    public void ChainSimplification_RemovesVerticesWithinTolerance()
    {
        // 頂点 B から C へ、三角形の内部を通る切り口 x1 x2 x3 が弦 B-C からわずかに内側にずれて並ぶ（0.3〜0.4 テクセル、一直線上ではない）
        Vector3[] positions =
        {
            new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0),
            new Vector3(0.75f, 0.244f, 0), new Vector3(0.5f, 0.494f, 0), new Vector3(0.25f, 0.746f, 0)
        };
        Vector2[] uvs = new Vector2[positions.Length];
        for (int i = 0; i < positions.Length; i++) uvs[i] = new Vector2(positions[i].x, positions[i].y);
        // 外周 A B x1 x2 x3 C を扇状に分割した4枚
        int[] triangles = { 0, 1, 3, 0, 3, 4, 0, 4, 5, 0, 5, 2 };
        int[] parents = { 0, 0, 0, 0 };
        Vector2Int[] textureSizes = { new Vector2Int(101, 101) };   // 1 テクセル = 0.01

        CutPolygonMerger strict = new CutPolygonMerger { SimplifyToleranceTexels = 0f };
        int[][] kept = strict.MergeTriangles(positions, uvs, new[] { triangles }, new[] { parents }, OriginalPositions, new[] { OriginalTriangle }, 3, textureSizes, out _);
        Assert.AreEqual(4, kept[0].Length / 3, "許容誤差 0 では切り口の頂点を残す");
        Assert.AreEqual(0, strict.RemovedChainVertexCount);

        CutPolygonMerger simplified = new CutPolygonMerger { SimplifyToleranceTexels = 1f };
        int[][] result = simplified.MergeTriangles(positions, uvs, new[] { triangles }, new[] { parents }, OriginalPositions, new[] { OriginalTriangle }, 3, textureSizes, out _);
        Assert.AreEqual(1, result[0].Length / 3, "1 テクセル以内のずれの頂点は間引かれ、三角形 A B C だけになる");
        CollectionAssert.AreEquivalent(new[] { 0, 1, 2 }, result[0]);
        Assert.AreEqual(3, simplified.RemovedChainVertexCount);

        // ずれが許容誤差を超える頂点は残り、残った頂点との弦上にある頂点は間引かれる
        positions[4] = new Vector3(0.5f, 0.45f, 0);                                   // x2 を弦 B-C から約 3.5 テクセル内側へ
        positions[3] = (positions[1] + positions[4]) * 0.5f + new Vector3(0, -0.003f, 0); // x1 は弦 B-x2 から 0.3 テクセル以内
        positions[5] = (positions[4] + positions[2]) * 0.5f + new Vector3(0, -0.003f, 0); // x3 は弦 x2-C から 0.3 テクセル以内
        for (int i = 3; i < 6; i++) uvs[i] = new Vector2(positions[i].x, positions[i].y);
        CutPolygonMerger partial = new CutPolygonMerger { SimplifyToleranceTexels = 1f };
        int[][] result2 = partial.MergeTriangles(positions, uvs, new[] { triangles }, new[] { parents }, OriginalPositions, new[] { OriginalTriangle }, 3, textureSizes, out _);
        Assert.AreEqual(2, result2[0].Length / 3, "x2 だけ残り四角形 A B x2 C になる");
        Assert.AreEqual(2, partial.RemovedChainVertexCount);
    }

    [Test]
    public void VerticesOnOriginalEdge_AreNeverSimplified()
    {
        // 切り口が辺 B-C 上の e から入り、内部の x を通って辺 C-A 上の f へ抜ける: 残る多角形は A B e x f
        // e と f は元の三角形の辺上（隣の三角形と共有され得る）なので、許容誤差がどれほど大きくても間引かれない。x だけ間引かれる
        Vector3[] positions =
        {
            new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0),
            new Vector3(0.7f, 0.3f, 0), new Vector3(0.3f, 0.3f, 0), new Vector3(0, 0.4f, 0)
        };
        Vector2[] uvs = new Vector2[positions.Length];
        for (int i = 0; i < positions.Length; i++) uvs[i] = new Vector2(positions[i].x, positions[i].y);
        int[] triangles = { 0, 1, 3, 0, 3, 4, 0, 4, 5 };
        int[] parents = { 0, 0, 0 };
        CutPolygonMerger merger = new CutPolygonMerger { SimplifyToleranceTexels = 100f };
        int[][] result = merger.MergeTriangles(positions, uvs, new[] { triangles }, new[] { parents }, OriginalPositions, new[] { OriginalTriangle }, 3, new[] { new Vector2Int(101, 101) }, out _);
        Assert.AreEqual(1, merger.RemovedChainVertexCount, "内部の x だけ間引かれる");
        Assert.AreEqual(2, result[0].Length / 3, "四角形 A B e f");
        CollectionAssert.AreEquivalent(new[] { 0, 1, 2 + 1, 5 }, new HashSet<int>(result[0]), "e(3) と f(5) は残る");
    }
}
