using UnityEngine;
using NUnit.Framework;
using System.Collections.Generic;
using MeshDeletionTool;

// BoneWeightUtils.LerpBoneWeight（ボーンウェイトの線形補間）のテスト
public class BoneWeightUtilsTest
{
    private static BoneWeight Make(params (int bone, float weight)[] entries)
    {
        BoneWeight bw = new BoneWeight();
        for (int i = 0; i < entries.Length; i++)
        {
            switch (i)
            {
                case 0: bw.boneIndex0 = entries[i].bone; bw.weight0 = entries[i].weight; break;
                case 1: bw.boneIndex1 = entries[i].bone; bw.weight1 = entries[i].weight; break;
                case 2: bw.boneIndex2 = entries[i].bone; bw.weight2 = entries[i].weight; break;
                case 3: bw.boneIndex3 = entries[i].bone; bw.weight3 = entries[i].weight; break;
            }
        }
        return bw;
    }

    // ボーンインデックス → ウェイト（ウェイト0の項は無視）
    private static Dictionary<int, float> ToDictionary(BoneWeight bw)
    {
        Dictionary<int, float> result = new Dictionary<int, float>();
        void Add(int bone, float weight) { if (weight > 0f) result[bone] = result.TryGetValue(bone, out float w) ? w + weight : weight; }
        Add(bw.boneIndex0, bw.weight0);
        Add(bw.boneIndex1, bw.weight1);
        Add(bw.boneIndex2, bw.weight2);
        Add(bw.boneIndex3, bw.weight3);
        return result;
    }

    private static void AssertWeights(BoneWeight actual, params (int bone, float weight)[] expected)
    {
        Dictionary<int, float> weights = ToDictionary(actual);
        Assert.AreEqual(expected.Length, weights.Count, "ボーン数");
        foreach ((int bone, float weight) in expected)
        {
            Assert.IsTrue(weights.ContainsKey(bone), "ボーン " + bone + " が含まれること");
            Assert.AreEqual(weight, weights[bone], 1e-5f, "ボーン " + bone + " のウェイト");
        }
    }

    private static void AssertNormalizedAndSorted(BoneWeight bw)
    {
        Assert.AreEqual(1f, bw.weight0 + bw.weight1 + bw.weight2 + bw.weight3, 1e-5f, "合計が1");
        Assert.IsTrue(bw.weight0 >= bw.weight1 && bw.weight1 >= bw.weight2 && bw.weight2 >= bw.weight3, "大きい順");
        Assert.IsTrue(bw.weight0 >= 0f && bw.weight1 >= 0f && bw.weight2 >= 0f && bw.weight3 >= 0f, "負のウェイトが無い");
    }

    [Test]
    public void SingleBoneEndpoints_AreInterpolatedLinearly()
    {
        BoneWeight a = Make((0, 1f));
        BoneWeight b = Make((1, 1f));

        AssertWeights(BoneWeightUtils.LerpBoneWeight(a, b, 0f), (0, 1f));
        AssertWeights(BoneWeightUtils.LerpBoneWeight(a, b, 0.25f), (0, 0.75f), (1, 0.25f));
        AssertWeights(BoneWeightUtils.LerpBoneWeight(a, b, 0.5f), (0, 0.5f), (1, 0.5f));
        AssertWeights(BoneWeightUtils.LerpBoneWeight(a, b, 0.75f), (0, 0.25f), (1, 0.75f));
        AssertWeights(BoneWeightUtils.LerpBoneWeight(a, b, 1f), (1, 1f));
    }

    [Test]
    public void Endpoints_ReturnInputsUnchanged()
    {
        BoneWeight a = Make((3, 0.6f), (7, 0.4f));
        BoneWeight b = Make((7, 0.7f), (2, 0.3f));

        Assert.AreEqual(a, BoneWeightUtils.LerpBoneWeight(a, b, 0f));
        Assert.AreEqual(b, BoneWeightUtils.LerpBoneWeight(a, b, 1f));
    }

    [Test]
    public void SharedBones_AreMergedByIndex()
    {
        BoneWeight a = Make((0, 0.6f), (1, 0.4f));
        BoneWeight b = Make((1, 0.7f), (2, 0.3f));

        BoneWeight half = BoneWeightUtils.LerpBoneWeight(a, b, 0.5f);
        AssertWeights(half, (0, 0.3f), (1, 0.55f), (2, 0.15f));
        AssertNormalizedAndSorted(half);

        BoneWeight quarter = BoneWeightUtils.LerpBoneWeight(a, b, 0.25f);
        AssertWeights(quarter, (0, 0.45f), (1, 0.475f), (2, 0.075f));
        AssertNormalizedAndSorted(quarter);
    }

    [Test]
    public void EightDistinctBones_KeepFourLargestAndRenormalize()
    {
        BoneWeight a = Make((0, 0.4f), (1, 0.3f), (2, 0.2f), (3, 0.1f));
        BoneWeight b = Make((4, 0.4f), (5, 0.3f), (6, 0.2f), (7, 0.1f));

        // t = 0.25: a 側 0.3/0.225/0.15/0.075, b 側 0.1/0.075/0.05/0.025 → 上位4つ (b0, b1, b2, b4) の合計 0.775 で正規化
        BoneWeight result = BoneWeightUtils.LerpBoneWeight(a, b, 0.25f);
        AssertWeights(result, (0, 0.3f / 0.775f), (1, 0.225f / 0.775f), (2, 0.15f / 0.775f), (4, 0.1f / 0.775f));
        AssertNormalizedAndSorted(result);

        // t = 0.5: 同じウェイトが並ぶ → 各ボーンから2つずつ (b0, b4, b1, b5)
        BoneWeight middle = BoneWeightUtils.LerpBoneWeight(a, b, 0.5f);
        AssertWeights(middle, (0, 0.2f / 0.7f), (4, 0.2f / 0.7f), (1, 0.15f / 0.7f), (5, 0.15f / 0.7f));
        AssertNormalizedAndSorted(middle);
    }

    [Test]
    public void ResultIsSortedDescending()
    {
        BoneWeight a = Make((0, 0.1f), (1, 0.9f));
        BoneWeight b = Make((2, 0.5f), (3, 0.5f));

        BoneWeight result = BoneWeightUtils.LerpBoneWeight(a, b, 0.5f);
        AssertNormalizedAndSorted(result);
        Assert.AreEqual(1, result.boneIndex0);
        Assert.AreEqual(0.45f, result.weight0, 1e-5f);
    }

    [Test]
    public void AllZeroInput_IsHandled()
    {
        BoneWeight zero = new BoneWeight();
        BoneWeight b = Make((1, 1f));

        // 片方が全て0なら、もう片方を正規化したものになる
        AssertWeights(BoneWeightUtils.LerpBoneWeight(zero, b, 0.5f), (1, 1f));
        AssertWeights(BoneWeightUtils.LerpBoneWeight(b, zero, 0.5f), (1, 1f));
        // 両方とも全て0なら全て0のまま（例外を出さない）
        BoneWeight result = BoneWeightUtils.LerpBoneWeight(zero, zero, 0.5f);
        Assert.AreEqual(0f, result.weight0 + result.weight1 + result.weight2 + result.weight3);
    }
}
