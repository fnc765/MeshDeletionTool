using UnityEngine;
using NUnit.Framework;
using MeshDeletionTool;

// AlphaMask（テクセル参照の規則）と AlphaSampling（境界判定・二分探索）のテスト
public class AlphaMaskTest
{
    // 幅 w のうち左半分 (x < w / 2) が透明、右半分が不透明のマスク
    private static AlphaMask HalfOpaque(int width, int height)
    {
        byte[] alpha = new byte[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                alpha[y * width + x] = x < width / 2 ? (byte)0 : (byte)255;
        return new AlphaMask(width, height, alpha);
    }

    [Test]
    public void FromPixels_KeepsAlphaAtXY()
    {
        Color32[] pixels = new Color32[3 * 2];
        pixels[1 * 3 + 2] = new Color32(0, 0, 0, 200);   // x = 2, y = 1
        AlphaMask mask = AlphaMask.FromPixels(pixels, 3, 2);
        Assert.AreEqual(200, mask.AlphaByte(2, 1));
        Assert.AreEqual(200 / 255f, mask.Alpha(2, 1));
        Assert.AreEqual(0, mask.AlphaByte(1, 1));
        Assert.AreEqual(2, mask.SampleCount + 0 - 1);   // 3 回参照した
    }

    [Test]
    public void TexelCoordinates_UseSizeMinusOne()
    {
        AlphaMask mask = HalfOpaque(256, 128);
        Assert.AreEqual(0, mask.TexelX(0f));
        Assert.AreEqual(255, mask.TexelX(1f));
        Assert.AreEqual(127, mask.TexelY(1f));
        Assert.AreEqual((int)(0.4f * 255), mask.TexelX(0.4f));
        // 従来の (int)(uv * (size - 1)) と同じ切り捨て
        Assert.AreEqual((int)(0.999f * 255f), mask.TexelX(0.999f));
    }

    [Test]
    public void OutOfRange_Repeat_Wraps()
    {
        AlphaMask mask = HalfOpaque(4, 4);   // x 0,1 透明 / 2,3 不透明
        Assert.AreEqual(255, mask.AlphaByte(-1, 0));   // -1 → 3
        Assert.AreEqual(0, mask.AlphaByte(4, 0));      // 4 → 0
        Assert.AreEqual(255, mask.AlphaByte(2, -5));   // y も同様
    }

    [Test]
    public void OutOfRange_Clamp_ClampsToEdge()
    {
        AlphaMask mask = HalfOpaque(4, 4);
        mask.WrapModeU = TextureWrapMode.Clamp;
        Assert.AreEqual(0, mask.AlphaByte(-7, 0));
        Assert.AreEqual(255, mask.AlphaByte(9, 0));
    }

    [Test]
    public void OutOfRange_Mirror_Reflects()
    {
        AlphaMask mask = HalfOpaque(4, 4);
        mask.WrapModeU = TextureWrapMode.Mirror;
        Assert.AreEqual(0, mask.AlphaByte(-1, 0));    // -1 → 0
        Assert.AreEqual(255, mask.AlphaByte(4, 0));   // 4 → 3
        Assert.AreEqual(0, mask.AlphaByte(7, 0));     // 7 → 0
        mask.WrapModeU = TextureWrapMode.MirrorOnce;
        Assert.AreEqual(255, mask.AlphaByte(9, 0));   // 一度だけ反転してから端に固定
    }

    [Test]
    public void Constructor_RejectsWrongLength()
    {
        Assert.Throws<System.ArgumentException>(() => new AlphaMask(2, 2, new byte[3]));
        Assert.Throws<System.ArgumentException>(() => AlphaMask.FromPixels(new Color32[4], 2, 3));
    }

    [Test]
    public void IsBoundaryEdge_RequiresOneSideStrictlyAboveAndOneBelow()
    {
        AlphaMask mask = HalfOpaque(64, 64);
        Vector2 left = new Vector2(0.1f, 0.5f), right = new Vector2(0.9f, 0.5f);
        Assert.IsTrue(AlphaSampling.IsBoundaryEdge(mask, left, right, 0.5f));
        Assert.IsTrue(AlphaSampling.IsBoundaryEdge(mask, right, left, 0.5f));
        Assert.IsFalse(AlphaSampling.IsBoundaryEdge(mask, left, left, 0.5f));
        Assert.IsFalse(AlphaSampling.IsBoundaryEdge(mask, right, right, 0.5f));
        // 閾値と等しいアルファ値は透明でも不透明でもない
        Assert.IsFalse(AlphaSampling.IsBoundaryEdge(mask, left, right, 1f));
        Assert.IsFalse(AlphaSampling.IsBoundaryEdge(mask, left, right, 0f));
        Assert.IsTrue(AlphaSampling.IsTransparent(mask, left, 0.5f));
        Assert.IsFalse(AlphaSampling.IsTransparent(mask, right, 0.5f));
    }

    [Test]
    public void FindAlphaBoundary_ConvergesToTheStep()
    {
        AlphaMask mask = HalfOpaque(256, 4);
        Vector2 left = new Vector2(0f, 0.5f), right = new Vector2(1f, 0.5f);
        float weight = AlphaSampling.FindAlphaBoundary(mask, left, right, 0.5f);
        // 境界はテクセル 127/128 の間（u ≈ 128 / 255）。二分探索 10 回なので 1/1024 以内
        float expected = 128f / 255f;
        Assert.AreEqual(expected, weight, 2f / 1024f);
        Assert.IsTrue(AlphaSampling.IsTransparent(mask, Vector2.Lerp(left, right, weight - 1f / 1024f), 0.5f));
        Assert.IsFalse(AlphaSampling.IsTransparent(mask, Vector2.Lerp(left, right, weight + 1f / 1024f), 0.5f));
        // 逆向きに辿っても境界は同じ場所
        float reversed = AlphaSampling.FindAlphaBoundary(mask, right, left, 0.5f);
        Assert.AreEqual(expected, 1f - reversed, 2f / 1024f);
    }

    [Test]
    public void BuildOpaqueTable_MatchesAlphaComparison()
    {
        AlphaMask mask = HalfOpaque(4, 4);
        foreach (float threshold in new[] { 0f, 0.001f, 0.5f, 127 / 255f, 128 / 255f, 1f })
        {
            bool[] table = mask.BuildOpaqueTable(threshold);
            Assert.AreEqual(256, table.Length);
            for (int value = 0; value < 256; value++)
            {
                Assert.AreEqual(value / 255f >= threshold, table[value], "value " + value + " threshold " + threshold);
            }
        }
        Assert.AreEqual(mask.AlphaByte(3, 1), mask.AlphaByteUnchecked(3, 1));
    }
}
