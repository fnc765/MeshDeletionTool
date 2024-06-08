using UnityEngine;
using System.Collections.Generic;
using System.Linq;

namespace MeshDeletionTool
{
    public static class BoneWeightUtils
    {
        public static BoneWeight LerpBoneWeight(BoneWeight bw1, BoneWeight bw2, float t)
        {
            // 初期化
            BoneWeight result = new BoneWeight();

            // ボーンインデックスとウェイトを対応付けするための辞書を作成
            Dictionary<int, float> boneWeightDict = new Dictionary<int, float>();

            // bw1のボーンインデックスとウェイトを辞書に追加
            AddBoneWeightToDict(boneWeightDict, bw1.boneIndex0, bw1.weight0);
            AddBoneWeightToDict(boneWeightDict, bw1.boneIndex1, bw1.weight1);
            AddBoneWeightToDict(boneWeightDict, bw1.boneIndex2, bw1.weight2);
            AddBoneWeightToDict(boneWeightDict, bw1.boneIndex3, bw1.weight3);

            // bw2のボーンインデックスとウェイトを辞書に追加（既存のインデックスなら加算）
            AddBoneWeightToDict(boneWeightDict, bw2.boneIndex0, bw2.weight0);
            AddBoneWeightToDict(boneWeightDict, bw2.boneIndex1, bw2.weight1);
            AddBoneWeightToDict(boneWeightDict, bw2.boneIndex2, bw2.weight2);
            AddBoneWeightToDict(boneWeightDict, bw2.boneIndex3, bw2.weight3);

            // 補間後のボーンウェイトを設定するためのリストを作成
            var interpolatedWeights = boneWeightDict
                .Select(pair => new { pair.Key, Weight = Mathf.Lerp(0, pair.Value, t) })
                .Where(x => x.Weight > 0)
                .OrderByDescending(x => x.Weight)
                .Take(4) // ボーンウェイトは最大4つまで
                .ToList();

            // ボーンインデックスとウェイトを結果に設定
            for (int i = 0; i < interpolatedWeights.Count; i++)
            {
                switch (i)
                {
                    case 0:
                        result.boneIndex0 = interpolatedWeights[i].Key;
                        result.weight0 = interpolatedWeights[i].Weight;
                        break;
                    case 1:
                        result.boneIndex1 = interpolatedWeights[i].Key;
                        result.weight1 = interpolatedWeights[i].Weight;
                        break;
                    case 2:
                        result.boneIndex2 = interpolatedWeights[i].Key;
                        result.weight2 = interpolatedWeights[i].Weight;
                        break;
                    case 3:
                        result.boneIndex3 = interpolatedWeights[i].Key;
                        result.weight3 = interpolatedWeights[i].Weight;
                        break;
                }
            }

            // 正規化
            NormalizeBoneWeight(ref result);

            return result;
        }

        private static void AddBoneWeightToDict(Dictionary<int, float> dict, int boneIndex, float weight)
        {
            if (boneIndex >= 0 && weight > 0)
            {
                if (dict.ContainsKey(boneIndex))
                {
                    dict[boneIndex] += weight;
                }
                else
                {
                    dict.Add(boneIndex, weight);
                }
            }
        }

        private static void NormalizeBoneWeight(ref BoneWeight bw)
        {
            float totalWeight = bw.weight0 + bw.weight1 + bw.weight2 + bw.weight3;

            if (totalWeight > 0)
            {
                bw.weight0 /= totalWeight;
                bw.weight1 /= totalWeight;
                bw.weight2 /= totalWeight;
                bw.weight3 /= totalWeight;
            }
        }
    }
}