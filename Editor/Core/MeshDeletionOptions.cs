using System;

namespace MeshDeletionTool
{
    // テクスチャの透明部分に基づくメッシュ削除の設定（ウィンドウと MeshDeletionForTexture コンポーネントで共通）と、AlphaMeshDeletionPipeline への対応付け
    // Unity には依存しない
    public class MeshDeletionOptions
    {
        // アルファ値がこの値より小さいメッシュは削除する
        public float AlphaThreshold = 0.5f;

        // 境界の細分化: 削除処理の前にアルファ境界付近の三角形を細分化する（無効にすると従来の動作）
        public bool RefineBoundary = true;

        // 境界の精度（テクセル、0.5〜4）: 切り口がテクスチャの境界からずれてよい量。小さいほど正確だがポリゴンが増える
        // 細分化の許容誤差（この 2 倍を切り上げた不透明テクセル数）と切り口の間引きの許容誤差（この値）を決める
        public float BoundaryPrecisionTexels = 1.0f;

        // 細分化の最大深さ（詳細設定）
        public int RefineMaxDepth = 3;

        // 切断後の再結合: 細分化で増えた三角形を元の三角形ごとに結合し直す（詳細設定、細分化が有効なときのみ）
        public bool MergeAfterCut = true;

        // サブメッシュ毎の処理対象フラグ（null ならテクスチャを持つ全サブメッシュ）
        public bool[] TargetSubMeshes;

        // 細分化が実際に行われるか（有効で深さが 1 以上）
        public bool RefineEnabled => RefineBoundary && RefineMaxDepth > 0;

        // 一部の頂点が透明な三角形の細分化で許容する、失われる不透明テクセル数（ウィンドウ版と同じ Mathf.CeilToInt(2f * 精度)）
        public int RefineChordToleranceTexels => (int)Math.Ceiling((double)(2f * BoundaryPrecisionTexels));

        // 切り口の間引きの許容誤差（テクセル）
        public float SimplifyToleranceTexels => BoundaryPrecisionTexels;

        // この設定に対応する処理（各処理段の時間を記録する）。backend は呼び出し側が所有・Dispose する
        public AlphaMeshDeletionPipeline CreatePipeline(IAlphaStageBackend backend, Action<string> log)
        {
            return new AlphaMeshDeletionPipeline
            {
                AlphaThreshold = AlphaThreshold,
                RefineBoundary = RefineBoundary,
                RefineMaxDepth = RefineMaxDepth,
                RefinePartiallyCutTriangles = true,
                RefineChordToleranceTexels = RefineChordToleranceTexels,
                MergeCutPolygons = MergeAfterCut,
                SimplifyToleranceTexels = SimplifyToleranceTexels,
                Backend = backend,
                MeasureTime = true,
                Log = log
            };
        }
    }
}
