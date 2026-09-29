using UnityEngine;
using System.Collections.Generic;
using System.Linq;

namespace MeshDeletionTool
{
    public static class BoneWeightUtils
    {
        // 2つのボーンウェイトを重み t で線形補間する（t = 0 で bw1、t = 1 で bw2 をそのまま返す）
        // 同じボーンのウェイトは合算し、大きい順に最大4つを残して合計が1になるよう正規化する
        public static BoneWeight LerpBoneWeight(BoneWeight bw1, BoneWeight bw2, float t)
        {
            t = Mathf.Clamp01(t);
            if (t <= 0f)
            {
                return bw1;
            }
            if (t >= 1f)
            {
                return bw2;
            }

            // ボーンインデックスとウェイトを対応付けするための辞書を作成
            Dictionary<int, float> boneWeightDict = new Dictionary<int, float>();

            // bw1のボーンインデックスとウェイトを (1 - t) 倍して辞書に追加
            AddBoneWeightToDict(boneWeightDict, bw1.boneIndex0, bw1.weight0 * (1f - t));
            AddBoneWeightToDict(boneWeightDict, bw1.boneIndex1, bw1.weight1 * (1f - t));
            AddBoneWeightToDict(boneWeightDict, bw1.boneIndex2, bw1.weight2 * (1f - t));
            AddBoneWeightToDict(boneWeightDict, bw1.boneIndex3, bw1.weight3 * (1f - t));

            // bw2のボーンインデックスとウェイトを t 倍して辞書に追加（既存のインデックスなら加算）
            AddBoneWeightToDict(boneWeightDict, bw2.boneIndex0, bw2.weight0 * t);
            AddBoneWeightToDict(boneWeightDict, bw2.boneIndex1, bw2.weight1 * t);
            AddBoneWeightToDict(boneWeightDict, bw2.boneIndex2, bw2.weight2 * t);
            AddBoneWeightToDict(boneWeightDict, bw2.boneIndex3, bw2.weight3 * t);

            // ウェイトの大きい順（同じならボーンインデックスの小さい順）に最大4つを残す
            var interpolatedWeights = boneWeightDict
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key)
                .Take(4) // ボーンウェイトは最大4つまで
                .ToList();

            // ボーンインデックスとウェイトを結果に設定（両方とも空なら全て0のまま）
            BoneWeight result = new BoneWeight();
            for (int i = 0; i < interpolatedWeights.Count; i++)
            {
                switch (i)
                {
                    case 0:
                        result.boneIndex0 = interpolatedWeights[i].Key;
                        result.weight0 = interpolatedWeights[i].Value;
                        break;
                    case 1:
                        result.boneIndex1 = interpolatedWeights[i].Key;
                        result.weight1 = interpolatedWeights[i].Value;
                        break;
                    case 2:
                        result.boneIndex2 = interpolatedWeights[i].Key;
                        result.weight2 = interpolatedWeights[i].Value;
                        break;
                    case 3:
                        result.boneIndex3 = interpolatedWeights[i].Key;
                        result.weight3 = interpolatedWeights[i].Value;
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
