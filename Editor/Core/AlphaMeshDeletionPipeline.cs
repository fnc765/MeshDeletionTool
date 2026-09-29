using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace MeshDeletionTool
{
    // テクスチャの透明部分に基づくメッシュ削除の処理全体（境界の細分化 → 削除する頂点の判定 → 切断 → 切断後の再結合）
    // Mesh / Texture2D には依存せず、MeshArrays と AlphaMask に対して処理する。EditorWindow 側は変換と保存だけを行う
    public class AlphaMeshDeletionPipeline
    {
        // アルファ値がこの値より小さいメッシュは削除する
        public float AlphaThreshold = 0.5f;

        // 境界の細分化: 削除処理の前にアルファ境界付近の三角形を細分化する（無効にすると従来の動作）
        public bool RefineBoundary = true;

        // 細分化の最大深さ
        public int RefineMaxDepth = 3;

        // 一部の頂点が透明な三角形の細分化（修正2）と、その際に許容する失われる不透明テクセル数
        public bool RefinePartiallyCutTriangles = true;
        public int RefineChordToleranceTexels = 2;

        // 切断後の再結合: 細分化で増えた三角形を元の三角形ごとに結合し直す（細分化が有効なときのみ）
        public bool MergeCutPolygons = true;

        // 切り口の間引きの許容誤差（テクセル）
        public float SimplifyToleranceTexels = 1.0f;

        // 進行状況の記録先（Debug.Log 相当。null なら記録しない）
        public Action<string> Log;

        // 実行結果: サブメッシュ毎の、出力メッシュの三角形番号 → 元のメッシュの三角形番号（テスト・診断用）
        public List<int[]> OutputTriangleParents;

        // subMeshMasks[i] はサブメッシュ i のテクスチャのアルファ値（テクスチャが無ければ null）、targetSubMeshes[i] は処理対象かどうか
        public MeshArrays Run(MeshArrays sourceMesh, AlphaMask[] subMeshMasks, bool[] targetSubMeshes)
        {
            MeshArrays originalMesh = sourceMesh;

            // 境界の細分化（削除処理の前に、アルファ境界付近の三角形を細分化したメッシュに置き換える）
            List<int[]> refinedTriangleParents = null;   // 細分化後の三角形番号 → 元の三角形番号
            if (RefineBoundary && RefineMaxDepth > 0)
            {
                originalMesh = RefineMeshAroundAlphaBoundary(originalMesh, subMeshMasks, targetSubMeshes, out refinedTriangleParents);
            }

            AlphaMeshCutter cutter = new AlphaMeshCutter { AlphaThreshold = AlphaThreshold };
            // 削除すべき頂点のインデックスを取得
            List<int> removeVerticesIndexs = cutter.GetVerticesToRemove(originalMesh, subMeshMasks);
            // 新しいメッシュを作成
            MeshArrays newMesh = cutter.Cut(originalMesh, subMeshMasks, targetSubMeshes, removeVerticesIndexs, out List<int[]> sourceTriangleIndices);
            // 出力三角形 → 元の三角形の対応を保持する
            OutputTriangleParents = ComposeTriangleParents(sourceTriangleIndices, refinedTriangleParents);

            // 切断後の再結合（細分化で増えた三角形を元の三角形ごとに結合し直す）
            if (refinedTriangleParents != null && MergeCutPolygons)
            {
                // 出力メッシュの先頭には元のメッシュの頂点（削除されなかったもの）が並ぶ。これらは再結合で取り除かない
                int keptOriginalVertexCount = sourceMesh.VertexCount - removeVerticesIndexs.Count(index => index < sourceMesh.VertexCount);
                newMesh = MergeCutPolygonsStep(newMesh, sourceMesh, subMeshMasks, targetSubMeshes, keptOriginalVertexCount);
            }
            return newMesh;
        }

        // アルファ境界付近の三角形を細分化したメッシュを返すメソッド（処理対象のサブメッシュのみ判定する）
        // parentTriangleIndexPerSubMesh には細分化後の三角形番号 → 元の三角形番号の対応を返す
        private MeshArrays RefineMeshAroundAlphaBoundary(MeshArrays originalMesh, AlphaMask[] subMeshMasks, bool[] targetSubMeshes,
                                                         out List<int[]> parentTriangleIndexPerSubMesh)
        {
            // 処理対象サブメッシュのテクスチャを集める（対象外は null）
            AlphaMask[] targetMasks = new AlphaMask[originalMesh.SubMeshCount];
            for (int subMeshIndex = 0; subMeshIndex < originalMesh.SubMeshCount; subMeshIndex++)
            {
                if (targetSubMeshes[subMeshIndex] && subMeshIndex < subMeshMasks.Length)
                {
                    targetMasks[subMeshIndex] = subMeshMasks[subMeshIndex];
                }
            }

            AlphaBoundaryRefiner refiner = new AlphaBoundaryRefiner
            {
                MaxDepth = RefineMaxDepth,
                RefinePartiallyCutTriangles = RefinePartiallyCutTriangles,
                ChordToleranceTexels = RefineChordToleranceTexels
            };
            MeshArrays refinedMesh = refiner.Refine(originalMesh, targetMasks, AlphaThreshold);
            parentTriangleIndexPerSubMesh = refiner.ParentTriangleIndexPerSubMesh;
            Log?.Invoke("境界の細分化: 三角形 " + originalMesh.TriangleCount + " → " + refinedMesh.TriangleCount +
                        " (深さ毎の三角形数: " + string.Join(", ", refiner.TriangleCountPerDepth) + ")");
            return refinedMesh;
        }

        // 切断後のメッシュを元の三角形ごとに再結合したメッシュを返すメソッド
        private MeshArrays MergeCutPolygonsStep(MeshArrays cutMesh, MeshArrays sourceMesh, AlphaMask[] subMeshMasks, bool[] targetSubMeshes,
                                                int keptOriginalVertexCount)
        {
            // 切り口の間引きは処理対象のサブメッシュのテクスチャ解像度に対するテクセル単位で行う（対象外・テクスチャ無しは 0）
            Vector2Int[] textureSizes = new Vector2Int[sourceMesh.SubMeshCount];
            for (int subMeshIndex = 0; subMeshIndex < textureSizes.Length; subMeshIndex++)
            {
                AlphaMask mask = subMeshIndex < subMeshMasks.Length ? subMeshMasks[subMeshIndex] : null;
                if (targetSubMeshes[subMeshIndex] && mask != null)
                    textureSizes[subMeshIndex] = new Vector2Int(mask.Width, mask.Height);
            }
            CutPolygonMerger merger = new CutPolygonMerger { SimplifyToleranceTexels = SimplifyToleranceTexels };
            MeshArrays mergedMesh = merger.Merge(cutMesh, OutputTriangleParents, sourceMesh, keptOriginalVertexCount, textureSizes);
            OutputTriangleParents = merger.ParentTriangleIndexPerSubMesh;
            Log?.Invoke("切断後の再結合: 三角形 " + merger.TriangleCountBefore + " → " + merger.TriangleCountAfter +
                        ", 頂点 " + merger.VertexCountBefore + " → " + merger.VertexCountAfter +
                        " (一直線上の頂点の削除 " + merger.RemovedFlatVertexCount + ", 切り口の間引き " + merger.RemovedChainVertexCount +
                        ", 再結合できなかった三角形 " + merger.FallbackCount + ": 穴 " + merger.FallbackHoleCount +
                        ", 非多様体 " + merger.FallbackNonManifoldCount + ", 分割失敗 " + merger.FallbackTriangulationCount + ")");
            return mergedMesh;
        }

        // 出力三角形 → 入力三角形の対応と、入力（細分化後）三角形 → 元の三角形の対応を合成する（細分化していなければそのまま）
        private static List<int[]> ComposeTriangleParents(List<int[]> sourceTriangleIndices, List<int[]> refinedTriangleParents)
        {
            if (refinedTriangleParents == null)
            {
                return sourceTriangleIndices;
            }
            List<int[]> parents = new List<int[]>(sourceTriangleIndices.Count);
            for (int subMeshIndex = 0; subMeshIndex < sourceTriangleIndices.Count; subMeshIndex++)
            {
                int[] sources = sourceTriangleIndices[subMeshIndex];
                int[] composed = new int[sources.Length];
                for (int i = 0; i < sources.Length; i++)
                {
                    composed[i] = refinedTriangleParents[subMeshIndex][sources[i]];
                }
                parents.Add(composed);
            }
            return parents;
        }
    }
}
