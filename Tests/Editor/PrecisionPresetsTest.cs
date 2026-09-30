using NUnit.Framework;
using MeshDeletionTool;

// 「切り抜きの精度」のプリセットと境界の精度（テクセル）の値の対応、mm の表示のテスト
public class PrecisionPresetsTest
{
    [Test]
    public void Labels_ListThePresetsThenCustom()
    {
        CollectionAssert.AreEqual(new[] { "高精度（0.5）", "標準（1）", "軽量（2）", "最軽量（4）", "カスタム" }, PrecisionPresets.Labels());
        Assert.AreEqual(4, PrecisionPresets.Custom);
    }

    [Test]
    public void IndexOf_MatchesPresetValues()
    {
        Assert.AreEqual(0, PrecisionPresets.IndexOf(0.5f));
        Assert.AreEqual(1, PrecisionPresets.IndexOf(1f), "既定値（MeshDeletionForTexture / MeshDeletionOptions の 1）は「標準」");
        Assert.AreEqual(1, PrecisionPresets.IndexOf(new MeshDeletionOptions().BoundaryPrecisionTexels));
        Assert.AreEqual(2, PrecisionPresets.IndexOf(2f));
        Assert.AreEqual(3, PrecisionPresets.IndexOf(4f));
        Assert.AreEqual(1, PrecisionPresets.IndexOf(1.00001f), "シリアライズの誤差は同じ値とみなす");
    }

    [Test]
    public void IndexOf_OtherValuesAreCustom()
    {
        Assert.AreEqual(PrecisionPresets.Custom, PrecisionPresets.IndexOf(0.75f));
        Assert.AreEqual(PrecisionPresets.Custom, PrecisionPresets.IndexOf(1.5f));
        Assert.AreEqual(PrecisionPresets.Custom, PrecisionPresets.IndexOf(3.99f));
    }

    [Test]
    public void ValueFor_PresetsAndCustomRoundTrip()
    {
        for (int index = 0; index < PrecisionPresets.Values.Length; index++)
            Assert.AreEqual(index, PrecisionPresets.IndexOf(PrecisionPresets.ValueFor(index, 3f)));
        Assert.AreEqual(1.5f, PrecisionPresets.ValueFor(PrecisionPresets.Custom, 1.5f), "カスタムは今の値を保つ");
        Assert.AreEqual(4f, PrecisionPresets.ValueFor(PrecisionPresets.Custom, 10f), "範囲（0.5〜4）に収める");
        Assert.AreEqual(0.5f, PrecisionPresets.ValueFor(PrecisionPresets.Custom, 0f));
    }

    [Test]
    public void MillimeterHint_ScalesTheTexelSizeByThePrecision()
    {
        Assert.AreEqual("≈ 0.5〜0.9 mm 単位で輪郭に沿わせます", PrecisionPresets.MillimeterHint(0.52f, 0.88f, 1f));
        Assert.AreEqual("≈ 1.0〜1.8 mm 単位で輪郭に沿わせます", PrecisionPresets.MillimeterHint(0.52f, 0.88f, 2f));
        Assert.AreEqual("≈ 0.3 mm 単位で輪郭に沿わせます", PrecisionPresets.MillimeterHint(0.6f, 0.62f, 0.5f), "同じ表示になる範囲は 1 つの値");
        Assert.IsNull(PrecisionPresets.MillimeterHint(0f, 0f, 1f));
    }
}
