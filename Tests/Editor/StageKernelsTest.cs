using System;
using System.Collections.Generic;
using UnityEngine;
using NUnit.Framework;
using MeshDeletionTool;

// StageKernelContext（CPU / GPU で共用するカーネルの C# 実装）が従来の要素毎の判定（ScalarStageOracle）と一致するかのテスト
// 乱数の UV・三角形・辺を、閾値と等しいアルファ値や範囲外の UV（各ラップモード）を含む合成マスク上で比べる
public class StageKernelsTest
{
    private const float Threshold = 0.5f;

    // 64x48 のマスク: 対角線の上が不透明、透明側に不透明の島、不透明側に透明の切れ込み、ギザギザの縁。
    // 閾値 0.5 と等しい値（128/255 は 0.50196 なので 127 と 128 の間に閾値がある。0.5 と等しくなる整数は無い）の代わりに
    // 閾値 0.2 の判定用に 51/255 = 0.2 を含めた値も混ぜる
    private static AlphaMask BuildMask(int width, int height, TextureWrapMode wrapU, TextureWrapMode wrapV)
    {
        byte[] alpha = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool opaque = x + y + ((x / 5) % 2) * 3 >= width;
                if (x >= width * 11 / 16 && x < width * 13 / 16 && y >= height * 11 / 16 && y < height * 13 / 16) opaque = false;
                if (x >= width * 3 / 16 && x < width * 5 / 16 && y >= height * 3 / 16 && y < height * 5 / 16) opaque = true;
                byte value = opaque ? (byte)(200 + (x * 3 + y) % 56) : (byte)((x * 7 + y * 3) % 120);
                if ((x + 2 * y) % 11 == 0) value = 51;   // 51/255 = 0.2（閾値 0.2 のテストで「等しい」になる）
                alpha[y * width + x] = value;
            }
        }
        return new AlphaMask(width, height, alpha) { WrapModeU = wrapU, WrapModeV = wrapV };
    }

    private static readonly TextureWrapMode[] WrapModes = { TextureWrapMode.Repeat, TextureWrapMode.Clamp, TextureWrapMode.Mirror, TextureWrapMode.MirrorOnce };

    // 主に 0〜1、時々範囲外（-1.5〜2.5）、時々テクセル境界上の UV
    private static Vector2 RandomUV(System.Random random, int width, int height)
    {
        double r = random.NextDouble();
        if (r < 0.15)
            return new Vector2((float)(random.NextDouble() * 4 - 1.5), (float)(random.NextDouble() * 4 - 1.5));
        if (r < 0.3)
            return new Vector2(random.Next(0, width) / (float)(width - 1), random.Next(0, height) / (float)(height - 1));
        return new Vector2((float)random.NextDouble(), (float)random.NextDouble());
    }

    private static StageKernelContext Context(AlphaMask mask, float threshold, Vector2[] uv)
    {
        return new StageKernelContext
        {
            Masks = new[] { MaskView.From(mask) },
            AlphaClass = StageKernelContext.BuildAlphaClassTable(threshold),
            UV = uv,
            VertexCount = uv.Length,
            RefineFull = 1,
            RefinePartial = 1,
            ChordTolerance = 2,
            MaxRasterSize = 512
        };
    }

    [Test]
    public void AlphaClassTable_MatchesThresholdComparisons()
    {
        foreach (float threshold in new[] { 0f, 0.2f, 0.5f, 0.75f, 1f })
        {
            byte[] table = StageKernelContext.BuildAlphaClassTable(threshold);
            bool[] opaque = new AlphaMask(1, 1, new byte[1]).BuildOpaqueTable(threshold);
            for (int value = 0; value < 256; value++)
            {
                float alpha = value / 255f;
                Assert.AreEqual(alpha < threshold, table[value] == 0, "透明 " + value + " @ " + threshold);
                Assert.AreEqual(alpha > threshold, table[value] == 2, "閾値より大きい " + value + " @ " + threshold);
                Assert.AreEqual(opaque[value], table[value] != 0, "不透明表 " + value + " @ " + threshold);
            }
        }
        Assert.AreEqual(1, StageKernelContext.BuildAlphaClassTable(0.2f)[51], "51/255 は 0.2 と等しい");
    }

    [Test]
    public void ClassifyVertices_MatchesScalarOracle_AllWrapModes()
    {
        System.Random random = new System.Random(1);
        foreach (TextureWrapMode wrapU in WrapModes)
        {
            foreach (TextureWrapMode wrapV in WrapModes)
            {
                AlphaMask mask = BuildMask(64, 48, wrapU, wrapV);
                Vector2[] uv = new Vector2[2000];
                for (int i = 0; i < uv.Length; i++) uv[i] = RandomUV(random, 64, 48);
                foreach (float threshold in new[] { 0.2f, 0.5f })
                {
                    StageKernelContext context = Context(mask, threshold, uv);
                    context.Count = uv.Length;
                    context.OutFlags = new int[uv.Length];
                    context.DispatchClassifyVertices();
                    for (int i = 0; i < uv.Length; i++)
                    {
                        Assert.AreEqual(ScalarStageOracle.IsTransparent(mask, uv[i], threshold), context.OutFlags[i] == 1,
                                        "uv " + uv[i] + " wrap " + wrapU + "/" + wrapV + " thr " + threshold);
                    }
                }
            }
        }
    }

    [Test]
    public void BisectEdges_MatchesScalarOracle_BitForBit()
    {
        System.Random random = new System.Random(2);
        int boundaryCount = 0;
        foreach (TextureWrapMode wrap in WrapModes)
        {
            AlphaMask mask = BuildMask(64, 48, wrap, wrap);
            Vector2[] uv = new Vector2[4000];
            for (int i = 0; i < uv.Length; i++) uv[i] = RandomUV(random, 64, 48);
            int[] items = new int[uv.Length];
            for (int e = 0; e < uv.Length / 2; e++)
            {
                int a = e * 2, b = e * 2 + 1;
                items[e * 2] = a; items[e * 2 + 1] = b;
            }
            StageKernelContext context = Context(mask, Threshold, uv);
            context.Items = items;
            context.ItemMask = new int[uv.Length / 2];
            context.Count = uv.Length / 2;
            context.OutFlags = new int[context.Count];
            context.OutWeights = new float[context.Count];
            context.DispatchBisectEdges();
            for (int e = 0; e < context.Count; e++)
            {
                bool expected = ScalarStageOracle.BisectEdge(mask, uv[items[e * 2]], uv[items[e * 2 + 1]], Threshold, out float weight);
                Assert.AreEqual(expected, context.OutFlags[e] == 1, "境界判定 辺 " + e + " wrap " + wrap);
                Assert.AreEqual(BitConverter.ToInt32(BitConverter.GetBytes(expected ? weight : 0f), 0), BitConverter.ToInt32(BitConverter.GetBytes(context.OutWeights[e]), 0),
                                "重み 辺 " + e + " wrap " + wrap);
                if (expected) boundaryCount++;
            }
        }
        Assert.Greater(boundaryCount, 200, "境界エッジが十分に含まれる");
    }

    [Test]
    public void RefineTriangleTest_MatchesScalarOracle()
    {
        System.Random random = new System.Random(3);
        int markedCount = 0, total = 0;
        foreach (TextureWrapMode wrap in WrapModes)
        {
            AlphaMask mask = BuildMask(64, 48, wrap, wrap);
            // 小さな三角形（数テクセル）と大きな三角形を混ぜる。頂点番号の順序（境界点の向き）も乱す
            int triangleCount = 1500;
            Vector2[] uv = new Vector2[triangleCount * 3];
            int[] items = new int[triangleCount * 3];
            for (int t = 0; t < triangleCount; t++)
            {
                Vector2 center = RandomUV(random, 64, 48);
                float size = random.NextDouble() < 0.5 ? (float)(random.NextDouble() * 0.06) : (float)(random.NextDouble() * 0.5);
                for (int k = 0; k < 3; k++)
                    uv[t * 3 + k] = center + new Vector2((float)(random.NextDouble() - 0.5), (float)(random.NextDouble() - 0.5)) * size;
                int[] order = { 0, 1, 2 };
                for (int k = 2; k > 0; k--) { int j = random.Next(k + 1); (order[k], order[j]) = (order[j], order[k]); }
                for (int k = 0; k < 3; k++) items[t * 3 + k] = t * 3 + order[k];
            }
            foreach ((bool full, bool partial, int tolerance) in new[] { (true, true, 2), (true, true, 0), (false, true, 1), (true, false, 0) })
            {
                RefineTestParams settings = new RefineTestParams
                {
                    RefineFullyTransparentTriangles = full, RefinePartiallyCutTriangles = partial, ChordToleranceTexels = tolerance, MaxRasterSize = 512
                };
                StageKernelContext context = Context(mask, Threshold, uv);
                context.RefineFull = full ? 1 : 0; context.RefinePartial = partial ? 1 : 0; context.ChordTolerance = tolerance;
                context.Items = items;
                context.ItemMask = new int[triangleCount];
                context.Count = triangleCount;
                context.OutFlags = new int[triangleCount];
                context.DispatchRefineTriangleTest();
                for (int t = 0; t < triangleCount; t++)
                {
                    bool expected = ScalarStageOracle.ShouldSubdivide(mask, uv, items[t * 3], items[t * 3 + 1], items[t * 3 + 2], Threshold, settings);
                    Assert.AreEqual(expected, context.OutFlags[t] == 1, "三角形 " + t + " wrap " + wrap + " 設定 " + full + "/" + partial + "/" + tolerance);
                    if (expected) markedCount++;
                    total++;
                }
            }
        }
        Assert.Greater(markedCount, total / 50, "細分化対象の三角形が十分に含まれる");
    }

    [Test]
    public void RefineTriangleTest_StrideAboveMaxRasterSize_MatchesScalarOracle()
    {
        // 幅 64 のマスクに対して MaxRasterSize を 10 にし、間引きサンプリングの経路も比べる
        System.Random random = new System.Random(4);
        AlphaMask mask = BuildMask(64, 48, TextureWrapMode.Repeat, TextureWrapMode.Repeat);
        int triangleCount = 400;
        Vector2[] uv = new Vector2[triangleCount * 3];
        int[] items = new int[triangleCount * 3];
        for (int i = 0; i < uv.Length; i++) { uv[i] = new Vector2((float)random.NextDouble(), (float)random.NextDouble()); items[i] = i; }
        RefineTestParams settings = new RefineTestParams { RefineFullyTransparentTriangles = true, RefinePartiallyCutTriangles = true, ChordToleranceTexels = 1, MaxRasterSize = 10 };
        StageKernelContext context = Context(mask, Threshold, uv);
        context.ChordTolerance = 1; context.MaxRasterSize = 10;
        context.Items = items; context.ItemMask = new int[triangleCount]; context.Count = triangleCount; context.OutFlags = new int[triangleCount];
        context.DispatchRefineTriangleTest();
        for (int t = 0; t < triangleCount; t++)
        {
            Assert.AreEqual(ScalarStageOracle.ShouldSubdivide(mask, uv, items[t * 3], items[t * 3 + 1], items[t * 3 + 2], Threshold, settings), context.OutFlags[t] == 1, "三角形 " + t);
        }
    }

    [Test]
    public void PackMasks_UnpackReturnsEveryByte_AndInfoMatches()
    {
        AlphaMask a = BuildMask(13, 7, TextureWrapMode.Clamp, TextureWrapMode.Mirror);   // 91 バイト（4 の倍数でない）
        AlphaMask b = BuildMask(6, 5, TextureWrapMode.Repeat, TextureWrapMode.MirrorOnce);
        uint[] words = StageBatch.PackMasks(new[] { a, b }, out int[] info);
        Assert.AreEqual(new[] { 0, 13, 7, (int)TextureWrapMode.Clamp, (int)TextureWrapMode.Mirror, 92, 6, 5, (int)TextureWrapMode.Repeat, (int)TextureWrapMode.MirrorOnce }, info);
        Assert.AreEqual((92 + 32) / 4, words.Length);
        for (int i = 0; i < a.AlphaBytes.Length; i++)
            Assert.AreEqual(a.AlphaBytes[i], StageBatch.UnpackAlpha(words, (uint)(info[0] + i)));
        for (int i = 0; i < b.AlphaBytes.Length; i++)
            Assert.AreEqual(b.AlphaBytes[i], StageBatch.UnpackAlpha(words, (uint)(info[5] + i)));
    }
}
