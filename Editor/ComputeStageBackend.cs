using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace MeshDeletionTool
{
    // 処理段を Compute Shader（Editor/Shaders/MeshDeletionStages.compute）で実行するバックエンド
    // 入力の平坦化は CpuStageBackend と同じ StageBatch で行い、カーネルの数式は StageKernelContext と行単位で対応しているため、結果は CPU と同じになる。
    // マスク（アルファ値）は 1 回の処理の間キャッシュして GPU に一度だけ転送する。読み戻しは同期（GetData）で、エディタ拡張には十分
    public class ComputeStageBackend : IAlphaStageBackend
    {
        public const string ShaderAssetName = "MeshDeletionStages";
        private const int Threads = 64;
        // 1 回のディスパッチのグループ数の上限（D3D11 の上限 65,535）
        private const int MaxGroupsPerDispatch = 65535;

        private readonly ComputeShader shader;
        private readonly int classifyKernel, refineKernel, bisectKernel;

        // マスクのキャッシュ（連結して 1 つのバッファに詰めたもの）
        private readonly List<AlphaMask> packedMasks = new List<AlphaMask>();
        private ComputeBuffer alphaBuffer;
        private ComputeBuffer maskInfoBuffer;

        // UV のキャッシュ（同じ配列が続けて使われるとき、転送を省く）
        private Vector2[] uploadedUV;
        private ComputeBuffer uvBuffer;

        private readonly ComputeBuffer alphaClassBuffer;
        private readonly ComputeBuffer dummyBuffer;
        private float alphaClassThreshold = float.NaN;

        private readonly List<StageTiming> timings = new List<StageTiming>();
        public IReadOnlyList<StageTiming> Timings => timings;

        public string Name => "GPU (" + SystemInfo.graphicsDeviceName + ", " + SystemInfo.graphicsDeviceType + ")";

        private ComputeStageBackend(ComputeShader shader)
        {
            this.shader = shader;
            classifyKernel = shader.FindKernel("ClassifyVertices");
            refineKernel = shader.FindKernel("RefineTriangleTest");
            bisectKernel = shader.FindKernel("BisectEdges");
            alphaClassBuffer = new ComputeBuffer(256, sizeof(uint));
            dummyBuffer = new ComputeBuffer(1, sizeof(uint));
        }

        // この環境で使えるなら作る。使えなければ null と、その理由（1 行）を返す
        public static IAlphaStageBackend TryCreate(out string reason)
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                reason = "この環境は Compute Shader に対応していません（" + SystemInfo.graphicsDeviceType + "）";
                return null;
            }
            ComputeShader shader = FindShader();
            if (shader == null)
            {
                reason = "Compute Shader '" + ShaderAssetName + "' がプロジェクト内に見つかりません";
                return null;
            }
            if (!shader.HasKernel("ClassifyVertices") || !shader.HasKernel("RefineTriangleTest") || !shader.HasKernel("BisectEdges"))
            {
                reason = "Compute Shader '" + ShaderAssetName + "' のカーネルが見つかりません（コンパイルエラーを確認してください）";
                return null;
            }
            reason = null;
            return new ComputeStageBackend(shader);
        }

        // ツールは Assets 下の任意の場所に置かれるため、名前で探す
        private static ComputeShader FindShader()
        {
            foreach (string guid in AssetDatabase.FindAssets("t:ComputeShader " + ShaderAssetName))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path) != ShaderAssetName)
                    continue;
                ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
                if (shader != null)
                    return shader;
            }
            return null;
        }

        public bool[][] ClassifyVertices(Vector2[] uvs, AlphaMask[] masks, float alphaThreshold)
        {
            AlphaMask[] distinct = StageBatch.DistinctMasks(masks, out int[] maskIndexPerSet);
            int[] maskIndexInPack = EnsureMasks(distinct);
            int vertexCount = uvs.Length;
            int count = distinct.Length * vertexCount;
            int[] flags = new int[count];
            if (count > 0)
            {
                // 要素番号 = distinct 内のマスク番号 × 頂点数 + 頂点番号。カーネルは _ItemMask[distinct 内のマスク番号] で連結バッファ内のマスクを引くため、
                // 連結バッファに distinct 以外のマスクが残っていても（並びが違っても）結果は distinct の順に並ぶ
                Stopwatch stopwatch = Stopwatch.StartNew();
                EnsureUV(uvs);
                EnsureAlphaClass(alphaThreshold);
                using (ComputeBuffer itemMaskBuffer = new ComputeBuffer(maskIndexInPack.Length, sizeof(int)))
                using (ComputeBuffer outFlags = new ComputeBuffer(count, sizeof(int)))
                {
                    itemMaskBuffer.SetData(maskIndexInPack);
                    shader.SetInt("_VertexCount", vertexCount);
                    Bind(classifyKernel, dummyBuffer, itemMaskBuffer, outFlags, dummyBuffer);
                    Dispatch(classifyKernel, count);
                    outFlags.GetData(flags);
                }
                Record("ClassifyVertices", count, stopwatch);
            }
            return StageBatch.SplitVertexFlags(flags, vertexCount, distinct.Length, maskIndexPerSet);
        }

        public bool[][] TestRefineTriangles(Vector2[] uvs, int[][] triangleSets, AlphaMask[] masks, RefineTestParams settings, float alphaThreshold)
        {
            AlphaMask[] distinct = StageBatch.DistinctMasks(masks, out int[] maskIndexPerSet);
            int[] maskIndexInPack = EnsureMasks(distinct);
            int[] items = StageBatch.FlattenItems(triangleSets, RemapToPack(maskIndexPerSet, maskIndexInPack), 3, out int[] itemMask, out int[] setOffsets);
            int[] flags = new int[itemMask.Length];
            if (itemMask.Length > 0)
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                EnsureUV(uvs);
                EnsureAlphaClass(alphaThreshold);
                using (ComputeBuffer itemBuffer = new ComputeBuffer(items.Length, sizeof(int)))
                using (ComputeBuffer itemMaskBuffer = new ComputeBuffer(itemMask.Length, sizeof(int)))
                using (ComputeBuffer outFlags = new ComputeBuffer(itemMask.Length, sizeof(int)))
                {
                    itemBuffer.SetData(items);
                    itemMaskBuffer.SetData(itemMask);
                    shader.SetInt("_VertexCount", uvs.Length);
                    shader.SetInt("_RefineFull", settings.RefineFullyTransparentTriangles ? 1 : 0);
                    shader.SetInt("_RefinePartial", settings.RefinePartiallyCutTriangles ? 1 : 0);
                    shader.SetInt("_ChordTolerance", settings.ChordToleranceTexels);
                    shader.SetInt("_MaxRasterSize", settings.MaxRasterSize);
                    Bind(refineKernel, itemBuffer, itemMaskBuffer, outFlags, dummyBuffer);
                    Dispatch(refineKernel, itemMask.Length);
                    outFlags.GetData(flags);
                }
                Record("RefineTriangleTest", itemMask.Length, stopwatch);
            }
            return StageBatch.SplitFlags(flags, triangleSets, 3, setOffsets);
        }

        public void BisectEdges(Vector2[] uvs, int[][] edgeSets, AlphaMask[] masks, float alphaThreshold, out bool[][] isBoundary, out float[][] weights)
        {
            AlphaMask[] distinct = StageBatch.DistinctMasks(masks, out int[] maskIndexPerSet);
            int[] maskIndexInPack = EnsureMasks(distinct);
            int[] items = StageBatch.FlattenItems(edgeSets, RemapToPack(maskIndexPerSet, maskIndexInPack), 2, out int[] itemMask, out int[] setOffsets);
            int[] flags = new int[itemMask.Length];
            float[] weightValues = new float[itemMask.Length];
            if (itemMask.Length > 0)
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                EnsureUV(uvs);
                EnsureAlphaClass(alphaThreshold);
                using (ComputeBuffer itemBuffer = new ComputeBuffer(items.Length, sizeof(int)))
                using (ComputeBuffer itemMaskBuffer = new ComputeBuffer(itemMask.Length, sizeof(int)))
                using (ComputeBuffer outFlags = new ComputeBuffer(itemMask.Length, sizeof(int)))
                using (ComputeBuffer outWeights = new ComputeBuffer(itemMask.Length, sizeof(float)))
                {
                    itemBuffer.SetData(items);
                    itemMaskBuffer.SetData(itemMask);
                    shader.SetInt("_VertexCount", uvs.Length);
                    Bind(bisectKernel, itemBuffer, itemMaskBuffer, outFlags, outWeights);
                    Dispatch(bisectKernel, itemMask.Length);
                    outFlags.GetData(flags);
                    outWeights.GetData(weightValues);
                }
                Record("BisectEdges", itemMask.Length, stopwatch);
            }
            isBoundary = StageBatch.SplitFlags(flags, edgeSets, 2, setOffsets);
            weights = StageBatch.SplitWeights(weightValues, edgeSets, 2, setOffsets);
        }

        public void ResetTimings()
        {
            timings.Clear();
        }

        public void Dispose()
        {
            alphaBuffer?.Release();
            maskInfoBuffer?.Release();
            uvBuffer?.Release();
            alphaClassBuffer.Release();
            dummyBuffer.Release();
            alphaBuffer = maskInfoBuffer = uvBuffer = null;
            packedMasks.Clear();
            uploadedUV = null;
        }

        // 渡されたマスクが全て連結バッファに入っていることを保証し、各マスクの連結バッファ内の番号を返す
        // 新しいマスクがあれば、それまでのマスクに続けて全体を作り直す（同じ処理の中では同じマスクが繰り返し使われるので、転送は 1 回で済む）
        private int[] EnsureMasks(AlphaMask[] distinct)
        {
            bool missing = false;
            foreach (AlphaMask mask in distinct)
            {
                if (!packedMasks.Contains(mask))
                {
                    packedMasks.Add(mask);
                    missing = true;
                }
            }
            if (missing || alphaBuffer == null)
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                alphaBuffer?.Release();
                maskInfoBuffer?.Release();
                uint[] words = StageBatch.PackMasks(packedMasks.ToArray(), out int[] maskInfo);
                alphaBuffer = new ComputeBuffer(words.Length, sizeof(uint));
                alphaBuffer.SetData(words);
                maskInfoBuffer = new ComputeBuffer(Math.Max(1, maskInfo.Length), sizeof(int));
                maskInfoBuffer.SetData(maskInfo.Length > 0 ? maskInfo : new int[1]);
                Record("UploadMasks", (long)words.Length * 4, stopwatch);
            }
            int[] indexInPack = new int[distinct.Length];
            for (int k = 0; k < distinct.Length; k++)
            {
                indexInPack[k] = packedMasks.IndexOf(distinct[k]);
            }
            return indexInPack;
        }

        // サブメッシュ毎の distinct 内のマスク番号を、連結バッファ内の番号に写す
        private static int[] RemapToPack(int[] maskIndexPerSet, int[] maskIndexInPack)
        {
            int[] result = new int[maskIndexPerSet.Length];
            for (int k = 0; k < result.Length; k++)
            {
                result[k] = maskIndexPerSet[k] < 0 ? -1 : maskIndexInPack[maskIndexPerSet[k]];
            }
            return result;
        }

        private void EnsureUV(Vector2[] uvs)
        {
            if (uvBuffer != null && ReferenceEquals(uploadedUV, uvs))
                return;
            Stopwatch stopwatch = Stopwatch.StartNew();
            uvBuffer?.Release();
            uvBuffer = new ComputeBuffer(Math.Max(1, uvs.Length), sizeof(float) * 2);
            if (uvs.Length > 0)
                uvBuffer.SetData(uvs);
            uploadedUV = uvs;
            Record("UploadUV", uvs.Length, stopwatch);
        }

        // 閾値との比較の表（CPU で作る。GPU 側は表の参照だけ）
        private void EnsureAlphaClass(float alphaThreshold)
        {
            if (alphaThreshold == alphaClassThreshold)
                return;
            byte[] table = StageKernelContext.BuildAlphaClassTable(alphaThreshold);
            uint[] words = new uint[256];
            for (int value = 0; value < 256; value++)
            {
                words[value] = table[value];
            }
            alphaClassBuffer.SetData(words);
            alphaClassThreshold = alphaThreshold;
        }

        // カーネルが参照する全てのバッファを結び付ける（使わないものにはダミーを結ぶ）
        private void Bind(int kernel, ComputeBuffer items, ComputeBuffer itemMask, ComputeBuffer outFlags, ComputeBuffer outWeights)
        {
            shader.SetBuffer(kernel, "_Alpha", alphaBuffer);
            shader.SetBuffer(kernel, "_MaskInfo", maskInfoBuffer);
            shader.SetBuffer(kernel, "_AlphaClass", alphaClassBuffer);
            shader.SetBuffer(kernel, "_UV", uvBuffer);
            shader.SetBuffer(kernel, "_Items", items);
            shader.SetBuffer(kernel, "_ItemMask", itemMask);
            shader.SetBuffer(kernel, "_OutFlags", outFlags);
            shader.SetBuffer(kernel, "_OutWeights", outWeights);
        }

        // 要素数がグループ数の上限を超えるときは分割して呼ぶ
        private void Dispatch(int kernel, int count)
        {
            shader.SetInt("_Count", count);
            for (int offset = 0; offset < count; offset += MaxGroupsPerDispatch * Threads)
            {
                int remaining = Math.Min(count - offset, MaxGroupsPerDispatch * Threads);
                shader.SetInt("_ItemOffset", offset);
                shader.Dispatch(kernel, (remaining + Threads - 1) / Threads, 1, 1);
            }
        }

        private void Record(string name, long elements, Stopwatch stopwatch)
        {
            stopwatch.Stop();
            StageTiming timing = timings.Find(t => t.Name == name);
            if (timing == null)
            {
                timing = new StageTiming { Name = name };
                timings.Add(timing);
            }
            timing.Calls++;
            timing.Elements += elements;
            timing.Milliseconds += stopwatch.Elapsed.TotalMilliseconds;
        }
    }
}
