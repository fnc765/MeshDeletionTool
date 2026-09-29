using NUnit.Framework;
using MeshDeletionTool;

// MeshDeletionOptions（ウィンドウ・コンポーネント共通の設定）→ AlphaMeshDeletionPipeline の対応付けのテスト（Mesh に依存しない）
public class MeshDeletionOptionsTest
{
    [Test]
    public void Defaults_MatchTheWindowDefaults()
    {
        MeshDeletionOptions options = new MeshDeletionOptions();
        Assert.AreEqual(0.5f, options.AlphaThreshold);
        Assert.IsTrue(options.RefineBoundary);
        Assert.AreEqual(1f, options.BoundaryPrecisionTexels);
        Assert.AreEqual(3, options.RefineMaxDepth);
        Assert.IsTrue(options.MergeAfterCut);
        Assert.IsNull(options.TargetSubMeshes, "null はテクスチャを持つ全サブメッシュ");
        Assert.IsTrue(options.RefineEnabled);
    }

    [Test]
    public void Tolerances_DeriveFromBoundaryPrecision()
    {
        // 従来のウィンドウと同じ: 細分化の許容テクセル数 = ceil(2 × 精度)、切り口の間引きの許容誤差 = 精度
        Assert.AreEqual(2, new MeshDeletionOptions { BoundaryPrecisionTexels = 1f }.RefineChordToleranceTexels);
        Assert.AreEqual(1, new MeshDeletionOptions { BoundaryPrecisionTexels = 0.5f }.RefineChordToleranceTexels);
        Assert.AreEqual(3, new MeshDeletionOptions { BoundaryPrecisionTexels = 1.25f }.RefineChordToleranceTexels);
        Assert.AreEqual(8, new MeshDeletionOptions { BoundaryPrecisionTexels = 4f }.RefineChordToleranceTexels);
        Assert.AreEqual(2.5f, new MeshDeletionOptions { BoundaryPrecisionTexels = 2.5f }.SimplifyToleranceTexels);
    }

    [Test]
    public void RefineEnabled_RequiresTheToggleAndAPositiveDepth()
    {
        Assert.IsFalse(new MeshDeletionOptions { RefineBoundary = false }.RefineEnabled);
        Assert.IsFalse(new MeshDeletionOptions { RefineMaxDepth = 0 }.RefineEnabled);
        Assert.IsTrue(new MeshDeletionOptions { RefineMaxDepth = 1 }.RefineEnabled);
    }

    [Test]
    public void CreatePipeline_CopiesEverySetting()
    {
        MeshDeletionOptions options = new MeshDeletionOptions
        {
            AlphaThreshold = 0.25f, RefineBoundary = false, BoundaryPrecisionTexels = 1.5f, RefineMaxDepth = 4, MergeAfterCut = false
        };
        using (CpuStageBackend backend = new CpuStageBackend())
        {
            string logged = null;
            AlphaMeshDeletionPipeline pipeline = options.CreatePipeline(backend, message => logged = message);
            Assert.AreEqual(0.25f, pipeline.AlphaThreshold);
            Assert.IsFalse(pipeline.RefineBoundary);
            Assert.AreEqual(4, pipeline.RefineMaxDepth);
            Assert.IsTrue(pipeline.RefinePartiallyCutTriangles);
            Assert.AreEqual(3, pipeline.RefineChordToleranceTexels);
            Assert.AreEqual(1.5f, pipeline.SimplifyToleranceTexels);
            Assert.IsFalse(pipeline.MergeCutPolygons);
            Assert.AreSame(backend, pipeline.Backend);
            Assert.IsTrue(pipeline.MeasureTime, "実行結果のログのために処理段の時間を常に記録する");
            pipeline.Log("x");
            Assert.AreEqual("x", logged);
        }
    }

    [Test]
    public void CreatePipeline_ProducesTheSameMeshAsTheHandWrittenSettings()
    {
        // 既定の設定から作った処理と、ウィンドウが従来直接組み立てていた設定の処理が同じ結果になる
        MeshArrays mesh = MeshCoreTestUtils.Grid(8);
        AlphaMask mask = MeshCoreTestUtils.LeftTransparent(64, 64);
        AlphaMask[] masks = { mask, mask };
        bool[] targets = { true, true };
        MeshArrays fromOptions = new MeshDeletionOptions().CreatePipeline(new CpuStageBackend(), null).Run(mesh, masks, targets);
        MeshArrays reference = new AlphaMeshDeletionPipeline
        {
            AlphaThreshold = 0.5f, RefineBoundary = true, RefineMaxDepth = 3, RefinePartiallyCutTriangles = true, RefineChordToleranceTexels = 2,
            MergeCutPolygons = true, SimplifyToleranceTexels = 1f
        }.Run(mesh, masks, targets);
        Assert.IsNull(MeshArraysComparer.FirstDifference(reference, fromOptions));
        Assert.Less(fromOptions.VertexCount, mesh.VertexCount + 200);
    }
}
