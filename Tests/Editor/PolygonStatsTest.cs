using NUnit.Framework;
using MeshDeletionTool;

// プレビュー中のポリゴン数の表示（PolygonStats / PolygonStatsFormat）のテスト: 書式・丸め・符号・色の段階
public class PolygonStatsTest
{
    private static PolygonStats Stats(int trianglesBefore, int trianglesAfter, int verticesBefore = 100, int verticesAfter = 100)
    {
        return new PolygonStats
        {
            OriginalTriangles = trianglesBefore, GeneratedTriangles = trianglesAfter,
            OriginalVertices = verticesBefore, GeneratedVertices = verticesAfter
        };
    }

    [Test]
    public void TriangleLine_ShowsBeforeAfterDeltaAndPercent()
    {
        Assert.AreEqual("ポリゴン数（三角形）: 8,776 → 10,278（+1,502 / +17.1%）", PolygonStatsFormat.TriangleLine(Stats(8776, 10278)));
    }

    [Test]
    public void VertexAndTimeLines()
    {
        Assert.AreEqual("頂点数: 5,637 → 7,112（+26.2%）", PolygonStatsFormat.VertexLine(Stats(1, 1, 5637, 7112)));
        PolygonStats stats = Stats(1, 1);
        stats.ElapsedMilliseconds = 200.5;
        stats.Backend = "GPU";
        Assert.AreEqual("処理時間: 201 ms（GPU）", PolygonStatsFormat.TimeLine(stats));
        stats.ElapsedMilliseconds = 1234.4;
        stats.Backend = null;
        Assert.AreEqual("処理時間: 1,234 ms", PolygonStatsFormat.TimeLine(stats));
    }

    [Test]
    public void Decrease_UsesTheMinusSignAndSaysSo()
    {
        Assert.AreEqual("ポリゴン数（三角形）: 9,000 → 8,000（−1,000 / −11.1%、減少）", PolygonStatsFormat.TriangleLine(Stats(9000, 8000)));
        Assert.AreEqual("−3.2%", PolygonStatsFormat.SignedPercent(1000, 968));
        Assert.AreEqual(PolygonChangeLevel.Decrease, PolygonStatsFormat.Level(9000, 8000));
    }

    [Test]
    public void NoChange_UsesPlusMinus()
    {
        Assert.AreEqual("±0", PolygonStatsFormat.SignedCount(0));
        Assert.AreEqual("±0.0%", PolygonStatsFormat.SignedPercent(500, 500));
        Assert.AreEqual(PolygonChangeLevel.Decrease, PolygonStatsFormat.Level(500, 500), "変化なしは減少と同じ色（問題なし）");
    }

    [Test]
    public void Percent_RoundsHalfAwayFromZeroToOneDecimal()
    {
        // 1/8 = 12.5%、1/16 = 6.25% → 6.3%、1/2000 = 0.05% → 0.1%、1/40000 = 0.0025% → +0.0%（符号は丸める前の値で決める）
        Assert.AreEqual("+12.5%", PolygonStatsFormat.SignedPercent(8, 9));
        Assert.AreEqual("+6.3%", PolygonStatsFormat.SignedPercent(16, 17));
        Assert.AreEqual("+0.1%", PolygonStatsFormat.SignedPercent(2000, 2001));
        Assert.AreEqual("−0.1%", PolygonStatsFormat.SignedPercent(2000, 1999));
        Assert.AreEqual("+0.0%", PolygonStatsFormat.SignedPercent(40000, 40001));
    }

    [Test]
    public void ZeroBefore_IsNotADivisionByZero()
    {
        Assert.AreEqual("—", PolygonStatsFormat.SignedPercent(0, 10));
        Assert.AreEqual("±0.0%", PolygonStatsFormat.SignedPercent(0, 0));
        Assert.AreEqual(PolygonChangeLevel.Large, PolygonStatsFormat.Level(0, 10));
    }

    [Test]
    public void Level_ThresholdsAreInclusiveOnTheRoundedPercent()
    {
        Assert.AreEqual(PolygonChangeLevel.Small, PolygonStatsFormat.Level(1000, 1001));
        Assert.AreEqual(PolygonChangeLevel.Small, PolygonStatsFormat.Level(1000, 1200), "+20.0% は緑");
        Assert.AreEqual(PolygonChangeLevel.Small, PolygonStatsFormat.Level(100000, 120004), "+20.004% は +20.0% と表示されるので緑");
        Assert.AreEqual(PolygonChangeLevel.Medium, PolygonStatsFormat.Level(1000, 1201));
        Assert.AreEqual(PolygonChangeLevel.Medium, PolygonStatsFormat.Level(1000, 1500), "+50.0% は黄");
        Assert.AreEqual(PolygonChangeLevel.Large, PolygonStatsFormat.Level(1000, 1501));
        Assert.AreEqual(PolygonChangeLevel.Small, PolygonStatsFormat.Level(8776, 10278));
    }

    [Test]
    public void Count_UsesInvariantThousandsSeparators()
    {
        System.Globalization.CultureInfo saved = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.AreEqual("1,234,567", PolygonStatsFormat.Count(1234567));
            Assert.AreEqual("+17.1%", PolygonStatsFormat.SignedPercent(8776, 10278));
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = saved;
        }
        Assert.AreEqual("0", PolygonStatsFormat.Count(0));
        Assert.AreEqual("999", PolygonStatsFormat.Count(999));
    }

    [Test]
    public void FromMeshes_CountsPerSubMesh()
    {
        MeshArrays source = MeshCoreTestUtils.Grid(4);
        MeshArrays result = MeshCoreTestUtils.Grid(4);
        result.SubMeshTriangles[1] = new int[0];
        PolygonStats stats = PolygonStats.FromMeshes(source, result, new[] { false, true }, 12.5, "CPU");
        Assert.AreEqual(source.TriangleCount, stats.OriginalTriangles);
        Assert.AreEqual(result.TriangleCount, stats.GeneratedTriangles);
        Assert.AreEqual(source.VertexCount, stats.OriginalVertices);
        Assert.AreEqual(source.SubMeshTriangles[1].Length / 3, stats.SubMeshTrianglesBefore[1]);
        Assert.AreEqual(0, stats.SubMeshTrianglesAfter[1]);
        Assert.AreEqual("対象外", PolygonStatsFormat.SubMeshPercent(stats, 0));
        Assert.AreEqual("−100.0%", PolygonStatsFormat.SubMeshPercent(stats, 1));
        Assert.AreEqual(PolygonStatsFormat.Count(stats.SubMeshTrianglesBefore[1]) + " → 0", PolygonStatsFormat.SubMeshCounts(stats, 1));
        Assert.AreEqual("CPU", stats.Backend);
    }

    [Test]
    public void MaterialDisplayName_DropsTheInstanceSuffix()
    {
        Assert.AreEqual("N00_005_01_Tops_01_CLOTH", PolygonStatsFormat.MaterialDisplayName("N00_005_01_Tops_01_CLOTH (Instance)"));
        Assert.AreEqual("Body", PolygonStatsFormat.MaterialDisplayName("Body (Instance) (Instance)"));
        Assert.AreEqual("Body (Instanced)", PolygonStatsFormat.MaterialDisplayName("Body (Instanced)"));
        Assert.IsNull(PolygonStatsFormat.MaterialDisplayName(null));
    }
}
