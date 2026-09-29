using UnityEngine;
using System;
using System.Collections.Generic;

namespace MeshDeletionTool
{
    // テクスチャの透明部分に基づくメッシュ削除の、Unity のオブジェクト（Renderer / Mesh / Material / Texture2D）に対する入口
    // Mesh とテクスチャの読み出しと結果の検証だけをここで行い、処理本体（細分化・判定・切断・再結合）は Unity に依存しない AlphaMeshDeletionPipeline が行う
    // ウィンドウ（MeshDeletionToolForTexture）とヘッドレスの呼び出しが共通に使う。保存はしない
    // MeshDeletionRunner.Run の結果
    internal sealed class MeshDeletionResult
    {
        // 削った結果のメッシュ（保存されていない）
        public Mesh Mesh;
        // 1 行の要約（頂点数・三角形数の変化と処理段毎の時間）
        public string Summary;
        // サブメッシュ毎の、出力メッシュの三角形番号 → 元のメッシュの三角形番号
        public List<int[]> OutputTriangleParents;
    }

    internal static class MeshDeletionRunner
    {
        // 設定に従って renderer のメッシュを削り、新しい Mesh（名前は元のメッシュ名 + "_deleted"。保存はしない）と要約を返す
        // 要素毎の判定の実行先は自動で選ぶ（backendOverride があればそれを使い、Dispose は呼び出し側が行う）
        // メッシュが処理できない形（メッシュ無し、三角形でないサブメッシュ、UV 無し）なら ArgumentException を投げる
        internal static MeshDeletionResult Run(Renderer renderer, MeshDeletionOptions options, IAlphaStageBackend backendOverride = null)
        {
            if (renderer == null)
                throw new ArgumentNullException(nameof(renderer));
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            Mesh originalMesh = GetOriginalMesh(renderer);
            if (originalMesh == null)
                throw new ArgumentException("対象オブジェクトに有効なメッシュがありません。", nameof(renderer));

            string backendNote = null;
            IAlphaStageBackend backend = backendOverride ?? CreateBackend(options.RefineEnabled, out backendNote);
            if (backendNote != null)
                Debug.Log(backendNote);
            AlphaMeshDeletionPipeline pipeline = options.CreatePipeline(backend, Debug.Log);
            MeshArrays sourceArrays, newArrays;
            try
            {
                newArrays = Execute(renderer, options.TargetSubMeshes, pipeline, out sourceArrays);
            }
            finally
            {
                if (backendOverride == null)
                    backend.Dispose();
            }

            Mesh newMesh = MeshArraysUnityAdapter.ToMesh(newArrays);
            newMesh.name = originalMesh.name + "_deleted";
            return new MeshDeletionResult
            {
                Mesh = newMesh,
                Summary = DescribeResult(originalMesh.name, sourceArrays, newArrays, pipeline),
                OutputTriangleParents = pipeline.OutputTriangleParents
            };
        }

        // MeshDeletionForTexture コンポーネントの設定を MeshDeletionOptions にする（subMeshCount は対象メッシュのサブメッシュ数）
        // subMeshEnabled が空ならテクスチャを持つ全サブメッシュ（TargetSubMeshes = null）
        internal static MeshDeletionOptions OptionsFromComponent(MeshDeletionForTexture component, int subMeshCount)
        {
            MeshDeletionOptions options = new MeshDeletionOptions
            {
                AlphaThreshold = component.alphaThreshold,
                RefineBoundary = component.refineBoundary,
                BoundaryPrecisionTexels = component.boundaryPrecisionTexels,
                RefineMaxDepth = component.refineMaxDepth,
                MergeAfterCut = component.mergeAfterCut
            };
            if (component.subMeshEnabled != null && component.subMeshEnabled.Length > 0)
            {
                options.TargetSubMeshes = new bool[subMeshCount];
                for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
                    options.TargetSubMeshes[subMeshIndex] = component.IsSubMeshEnabled(subMeshIndex);
            }
            return options;
        }

        // Renderer が描いている Mesh（SkinnedMeshRenderer の sharedMesh、または MeshRenderer と同じオブジェクトの MeshFilter の sharedMesh）
        // 無ければ null（logError ならエラーも出す）
        internal static Mesh GetOriginalMesh(Renderer targetRenderer, bool logError = true)
        {
            if (targetRenderer is SkinnedMeshRenderer skinnedMeshRenderer)
            {
                return skinnedMeshRenderer.sharedMesh;
            }
            else if (targetRenderer is MeshRenderer meshRenderer)
            {
                MeshFilter meshFilter = targetRenderer.GetComponent<MeshFilter>();
                if (meshFilter != null)
                {
                    return meshFilter.sharedMesh;
                }
            }

            if (logError)
                Debug.LogError("対象オブジェクトに有効な SkinnedMeshRenderer または MeshRenderer コンポーネントがありません！");
            return null;
        }

        // Renderer のマテリアル（サブメッシュ順）。無ければ null（logError ならエラーも出す）
        internal static Material[] GetOriginalMaterials(Renderer targetRenderer, bool logError = true)
        {
            if (targetRenderer is SkinnedMeshRenderer skinnedMeshRenderer)
            {
                return skinnedMeshRenderer.sharedMaterials;
            }
            else if (targetRenderer is MeshRenderer meshRenderer)
            {
                MeshFilter meshFilter = targetRenderer.GetComponent<MeshFilter>();
                if (meshFilter != null)
                {
                    return meshRenderer.sharedMaterials;
                }
            }

            if (logError)
                Debug.LogError("対象オブジェクトに有効な SkinnedMeshRenderer または MeshRenderer コンポーネントがありません！");
            return null;
        }

        // メッシュが本ツールで扱える形かを確かめ、扱えなければその理由（1 行）を返す（扱えるなら null）
        // 出力は三角形のサブメッシュとして書き出すため、全サブメッシュが三角形である必要がある（Mesh.GetTriangles は三角形以外のサブメッシュを空にしてしまう）
        // requireUV: テクスチャに基づく処理には UV（UV0）が必要（無いと頂点をテクセルに対応付けられない）
        internal static string FindMeshProblem(Mesh mesh, bool requireUV)
        {
            for (int subMeshIndex = 0; subMeshIndex < mesh.subMeshCount; subMeshIndex++)
            {
                MeshTopology topology = mesh.GetTopology(subMeshIndex);
                if (topology != MeshTopology.Triangles)
                    return "メッシュ '" + mesh.name + "' のサブメッシュ " + subMeshIndex + " は三角形ではなく " + topology + " のため処理できません。";
            }
            if (requireUV && !mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord0))
                return "メッシュ '" + mesh.name + "' に UV（UV0）が無いため、テクスチャに基づく削除はできません。";
            return null;
        }

        // 1頂点あたりのボーン数が4を超える頂点があれば警告する
        // 本ツールは4ボーン固定の Mesh.boneWeights で読み書きするため、5番目以降のウェイトは出力メッシュから失われる
        // （Mesh.GetBonesPerVertex は Unity 2019.1 以降）
        internal static void WarnIfBonesPerVertexExceedFour(Mesh mesh)
        {
            if (mesh == null)
                return;

            var bonesPerVertex = mesh.GetBonesPerVertex();
            int exceedingCount = 0;
            int maxBones = 0;
            foreach (byte bones in bonesPerVertex)
            {
                if (bones > 4)
                {
                    exceedingCount++;
                    maxBones = Mathf.Max(maxBones, bones);
                }
            }
            if (exceedingCount > 0)
            {
                Debug.LogWarning("1頂点あたりのボーン数が4を超える頂点が " + exceedingCount + " 個あります（最大 " + maxBones + " ボーン）。" +
                                 "本ツールは1頂点あたり4ボーンまでしか扱えないため、5番目以降のウェイトは出力メッシュから失われます。" +
                                 "必要なら事前にウェイトを4ボーン以下に減らしてください。");
            }
        }

        // GUI を介さない処理の入口（ウィンドウの実行ボタンとテストが同じ処理を通る）
        // 対象オブジェクトの Mesh と処理対象サブメッシュのテクスチャを読み出し、pipeline（設定とバックエンドを持つ）で処理した結果を返す。保存はしない
        // targetSubMeshes[i] はサブメッシュ i を処理するかどうか（null ならテクスチャを持つ全サブメッシュ）。バックエンドの Dispose は呼び出し側が行う
        // 処理段の時間は pipeline.StageTimings と pipeline.Backend.Timings に、出力三角形 → 元の三角形の対応は pipeline.OutputTriangleParents に残る
        // メッシュが処理できない形（三角形でないサブメッシュ、UV 無し）なら、テクスチャを読む前に ArgumentException を投げる
        internal static MeshArrays Execute(Renderer renderer, bool[] targetSubMeshes, AlphaMeshDeletionPipeline pipeline)
        {
            return Execute(renderer, targetSubMeshes, pipeline, out MeshArrays _);
        }

        // sourceArrays に読み出した元のメッシュを返す版
        internal static MeshArrays Execute(Renderer renderer, bool[] targetSubMeshes, AlphaMeshDeletionPipeline pipeline, out MeshArrays sourceArrays)
        {
            if (renderer == null)
                throw new ArgumentNullException(nameof(renderer));
            Mesh originalMesh = GetOriginalMesh(renderer);
            Material[] originalMaterials = GetOriginalMaterials(renderer);
            if (originalMesh == null || originalMaterials == null)
                throw new ArgumentException("対象オブジェクトに有効なメッシュがありません。", nameof(renderer));
            string meshProblem = FindMeshProblem(originalMesh, true);
            if (meshProblem != null)
                throw new ArgumentException(meshProblem, nameof(renderer));
            WarnIfBonesPerVertexExceedFour(originalMesh);
            if (targetSubMeshes == null)
            {
                targetSubMeshes = SubMeshesWithTexture(originalMesh.subMeshCount, originalMaterials);
            }
            else if (targetSubMeshes.Length != originalMesh.subMeshCount)
            {
                throw new ArgumentException("targetSubMeshes の長さがサブメッシュ数と異なります。", nameof(targetSubMeshes));
            }
            else
            {
                // テクスチャを読めないサブメッシュは対象から外すので、呼び出し側の配列を変えないよう写しを使う
                targetSubMeshes = (bool[])targetSubMeshes.Clone();
            }

            // テクスチャのアルファ値とメッシュの頂点属性を一度だけ読み出す（テクスチャを読めないサブメッシュは処理対象から外れる）
            AlphaMask[] subMeshMasks = CollectAlphaMasks(originalMesh.subMeshCount, originalMaterials, targetSubMeshes);
            sourceArrays = MeshArraysUnityAdapter.FromMesh(originalMesh);

            // 細分化 → 削除する頂点の判定 → 切断 → 再結合
            return pipeline.Run(sourceArrays, subMeshMasks, targetSubMeshes);
        }

        // 要素毎の判定の実行先を自動で選び、選んだ理由を note に返す（1 行）
        // GPU で速くなるのは境界の細分化の判定なので、細分化が有効（refineEnabled）で Compute Shader（Editor/Shaders/MeshDeletionStages.compute）が使えるときは GPU、
        // それ以外（細分化が無効、Compute Shader 非対応、-nographics、シェーダーが見つからない）は CPU にする。結果はどちらでも同じ
        internal static IAlphaStageBackend CreateBackend(bool refineEnabled, out string note)
        {
            if (!refineEnabled)
            {
                note = "計算バックエンド: CPU（境界の細分化が無効のため。GPU で速くなるのは細分化の判定）";
                return new CpuStageBackend();
            }
            IAlphaStageBackend gpu = ComputeStageBackend.TryCreate(out string reason);
            if (gpu != null)
            {
                note = "計算バックエンド: " + gpu.Name;
                return gpu;
            }
            note = "計算バックエンド: CPU（" + reason + "）";
            return new CpuStageBackend();
        }

        // 実行結果の 1 行の要約（頂点数・三角形数の変化と処理段毎の時間）
        internal static string DescribeResult(string meshName, MeshArrays source, MeshArrays result, AlphaMeshDeletionPipeline pipeline)
        {
            System.Text.StringBuilder text = new System.Text.StringBuilder();
            double total = 0;
            foreach (StageTiming timing in pipeline.StageTimings)
            {
                text.Append(text.Length > 0 ? ", " : " (").Append(timing.Name).Append(' ').Append(timing.Milliseconds.ToString("0.0")).Append(" ms");
                total += timing.Milliseconds;
            }
            if (text.Length > 0)
                text.Append(')');
            return "MeshDeletionForTexture '" + meshName + "': 頂点 " + source.VertexCount + " → " + result.VertexCount +
                   ", 三角形 " + source.TriangleCount + " → " + result.TriangleCount + ", " + total.ToString("0.0") + " ms" + text;
        }


        // メインテクスチャ（Texture2D）を持つサブメッシュを処理対象にしたフラグ（マテリアルの数がサブメッシュ数より少ない・空のスロットは対象外）
        internal static bool[] SubMeshesWithTexture(int subMeshCount, Material[] materials)
        {
            bool[] targets = new bool[subMeshCount];
            for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
            {
                Material material = subMeshIndex < materials.Length ? materials[subMeshIndex] : null;
                targets[subMeshIndex] = material != null && material.mainTexture is Texture2D;
            }
            return targets;
        }

        // 処理対象サブメッシュのテクスチャのアルファ値の読み出し（対象外のサブメッシュとテクスチャの無いサブメッシュは null）
        // マテリアルが無い（数が足りない・空のスロット）、テクスチャが無い、テクスチャを読めないサブメッシュは例外にせず、1 行のログを出して
        // targetSubMeshes から外す（処理本体はテクスチャの無いサブメッシュを対象にできないため、対象と読み出し結果を常に一致させる）
        // 読み出しは AlphaMaskReader（GPU 経由。インポート設定は変更しない。GPU が使えないときだけインポート設定の一時変更）で、同じテクスチャは一度だけ読む
        // 使った経路と時間は 1 行のログに出す
        internal static AlphaMask[] CollectAlphaMasks(int subMeshCount, Material[] originalMaterials, bool[] targetSubMeshes)
        {
            AlphaMask[] masks = CollectAlphaMasks(subMeshCount, originalMaterials, targetSubMeshes, out string readNote);
            if (readNote != null)
                Debug.Log(readNote);
            return masks;
        }

        // readNote に読み出しの経路と時間（1 行。読むテクスチャが無ければ null）を返す版
        internal static AlphaMask[] CollectAlphaMasks(int subMeshCount, Material[] originalMaterials, bool[] targetSubMeshes, out string readNote)
        {
            readNote = null;
            Texture2D[] subMeshTextures = new Texture2D[subMeshCount];
            List<Texture2D> distinctTextures = new List<Texture2D>();
            for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
            {
                if (!targetSubMeshes[subMeshIndex])
                    continue;
                Material material = subMeshIndex < originalMaterials.Length ? originalMaterials[subMeshIndex] : null;
                if (material == null)
                {
                    Debug.LogWarning("サブメッシュ " + subMeshIndex + " にはマテリアルが無いため処理対象から外します。");
                    targetSubMeshes[subMeshIndex] = false;
                    continue;
                }
                Texture2D texture = material.mainTexture as Texture2D;
                if (texture == null)
                {
                    Debug.LogWarning("サブメッシュ " + subMeshIndex + " のマテリアル '" + material.name + "' にはテクスチャ（Texture2D）が無いため処理対象から外します。");
                    targetSubMeshes[subMeshIndex] = false;
                    continue;
                }
                subMeshTextures[subMeshIndex] = texture;
                if (!distinctTextures.Contains(texture))
                    distinctTextures.Add(texture);
            }

            AlphaMask[] subMeshMasks = new AlphaMask[subMeshCount];
            if (distinctTextures.Count == 0)
                return subMeshMasks;
            Dictionary<Texture2D, AlphaMask> maskCache = AlphaMaskReader.Read(distinctTextures, out readNote);
            for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
            {
                Texture2D texture = subMeshTextures[subMeshIndex];
                if (texture == null)
                    continue;
                maskCache.TryGetValue(texture, out AlphaMask mask);
                subMeshMasks[subMeshIndex] = mask;
                if (mask == null)
                    targetSubMeshes[subMeshIndex] = false;   // 読めなかった理由は AlphaMaskReader / TemporaryReadableTextures が出している
            }
            return subMeshMasks;
        }

    }
}
