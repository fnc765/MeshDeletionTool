using System;
using System.Collections.Generic;

namespace MeshDeletionTool
{
    // バックエンド共通の入力の平坦化: サブメッシュ毎の配列を「要素毎のマスク番号」付きの 1 本の配列（1 回のディスパッチ）にまとめ、結果をサブメッシュ毎に戻す
    // 同じ AlphaMask を持つサブメッシュはマスクを 1 つにまとめる（GPU への転送を 1 回にする）
    public static class StageBatch
    {
        // 重複を除いたマスクと、各サブメッシュのマスク番号（マスクが無ければ -1）
        public static AlphaMask[] DistinctMasks(AlphaMask[] masks, out int[] maskIndexPerSet)
        {
            List<AlphaMask> distinct = new List<AlphaMask>();
            maskIndexPerSet = new int[masks.Length];
            for (int k = 0; k < masks.Length; k++)
            {
                AlphaMask mask = masks[k];
                if (mask == null)
                {
                    maskIndexPerSet[k] = -1;
                    continue;
                }
                int found = distinct.IndexOf(mask);
                if (found < 0)
                {
                    found = distinct.Count;
                    distinct.Add(mask);
                }
                maskIndexPerSet[k] = found;
            }
            return distinct.ToArray();
        }

        public static MaskView[] Views(AlphaMask[] masks)
        {
            MaskView[] views = new MaskView[masks.Length];
            for (int k = 0; k < masks.Length; k++)
            {
                views[k] = MaskView.From(masks[k]);
            }
            return views;
        }

        // サブメッシュ毎の要素配列（1 要素 = itemStride 個の頂点番号）を連結する。マスクの無いサブメッシュは飛ばす
        // setOffsets[k] は連結後のサブメッシュ k の先頭要素番号（飛ばしたものは -1）
        public static int[] FlattenItems(int[][] sets, int[] maskIndexPerSet, int itemStride, out int[] itemMask, out int[] setOffsets)
        {
            int total = 0;
            setOffsets = new int[sets.Length];
            for (int k = 0; k < sets.Length; k++)
            {
                bool used = maskIndexPerSet[k] >= 0 && sets[k] != null;
                setOffsets[k] = used ? total : -1;
                if (used) total += sets[k].Length / itemStride;
            }
            int[] items = new int[total * itemStride];
            itemMask = new int[total];
            for (int k = 0; k < sets.Length; k++)
            {
                if (setOffsets[k] < 0) continue;
                int count = sets[k].Length / itemStride;
                Array.Copy(sets[k], 0, items, setOffsets[k] * itemStride, count * itemStride);
                for (int i = 0; i < count; i++)
                {
                    itemMask[setOffsets[k] + i] = maskIndexPerSet[k];
                }
            }
            return items;
        }

        // 連結した結果（0/1）をサブメッシュ毎の bool 配列に戻す（飛ばしたサブメッシュは null）
        public static bool[][] SplitFlags(int[] flags, int[][] sets, int itemStride, int[] setOffsets)
        {
            bool[][] result = new bool[sets.Length][];
            for (int k = 0; k < sets.Length; k++)
            {
                if (setOffsets[k] < 0) continue;
                int count = sets[k].Length / itemStride;
                bool[] set = new bool[count];
                for (int i = 0; i < count; i++)
                {
                    set[i] = flags[setOffsets[k] + i] != 0;
                }
                result[k] = set;
            }
            return result;
        }

        public static float[][] SplitWeights(float[] weights, int[][] sets, int itemStride, int[] setOffsets)
        {
            float[][] result = new float[sets.Length][];
            for (int k = 0; k < sets.Length; k++)
            {
                if (setOffsets[k] < 0) continue;
                int count = sets[k].Length / itemStride;
                float[] set = new float[count];
                Array.Copy(weights, setOffsets[k], set, 0, count);
                result[k] = set;
            }
            return result;
        }

        // 頂点判定の結果（マスク番号 × 頂点数）をサブメッシュ毎に配る。同じマスクのサブメッシュは同じ配列を共有する
        public static bool[][] SplitVertexFlags(int[] flags, int vertexCount, int maskCount, int[] maskIndexPerSet)
        {
            bool[][] perMask = new bool[maskCount][];
            for (int k = 0; k < maskCount; k++)
            {
                bool[] set = new bool[vertexCount];
                for (int v = 0; v < vertexCount; v++)
                {
                    set[v] = flags[k * vertexCount + v] != 0;
                }
                perMask[k] = set;
            }
            bool[][] result = new bool[maskIndexPerSet.Length][];
            for (int k = 0; k < maskIndexPerSet.Length; k++)
            {
                if (maskIndexPerSet[k] >= 0) result[k] = perMask[maskIndexPerSet[k]];
            }
            return result;
        }

        // GPU 用: 全マスクのアルファ値を 4 バイト境界で連結し、4 バイトずつ uint に詰める（リトルエンディアン。先頭のバイトが下位）
        // maskInfo はマスク毎に 5 要素（バイトオフセット, 幅, 高さ, ラップ U, ラップ V）で、HLSL の _MaskInfo と同じ並び
        public static uint[] PackMasks(AlphaMask[] masks, out int[] maskInfo)
        {
            maskInfo = new int[masks.Length * 5];
            long totalBytes = 0;
            for (int k = 0; k < masks.Length; k++)
            {
                maskInfo[k * 5 + 0] = (int)totalBytes;
                maskInfo[k * 5 + 1] = masks[k].Width;
                maskInfo[k * 5 + 2] = masks[k].Height;
                maskInfo[k * 5 + 3] = (int)masks[k].WrapModeU;
                maskInfo[k * 5 + 4] = (int)masks[k].WrapModeV;
                totalBytes += (masks[k].AlphaBytes.Length + 3) / 4 * 4;
            }
            if (totalBytes > int.MaxValue)
                throw new InvalidOperationException("マスクの合計が 2 GB を超えています。");
            uint[] words = new uint[Math.Max(1, (int)(totalBytes / 4))];
            for (int k = 0; k < masks.Length; k++)
            {
                byte[] alpha = masks[k].AlphaBytes;
                Buffer.BlockCopy(alpha, 0, words, maskInfo[k * 5 + 0], alpha.Length);
            }
            return words;
        }

        // HLSL の LoadAlpha と同じ取り出し（テスト用）
        public static uint UnpackAlpha(uint[] words, uint byteIndex)
        {
            uint word = words[byteIndex >> 2];
            return (word >> (int)((byteIndex & 3u) * 8u)) & 0xFFu;
        }
    }
}
