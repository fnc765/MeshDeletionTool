using UnityEngine;

namespace MeshDeletionTool
{
    // テクスチャの透明部分に合わせて、このオブジェクトの Renderer（SkinnedMeshRenderer、または MeshRenderer と MeshFilter）のメッシュを削る設定
    // 付けただけでは何もしない。NDMF がプレイモードに入るときとアバターのアップロード時に、削った結果のメッシュを生成して Renderer のメッシュを置き換え、
    // このコンポーネントを取り除く（非破壊: 元のメッシュ・アセットは変更しない）。設定の意味はウィンドウ版（Tools/MeshDeletionToolForTexture）と同じ
    // アバター（VRC Avatar Descriptor など）の配下にないものは、プレイモードでは MeshDeletionTool の簡易適用が置き換える。インスペクターのプレビューで、
    // プレイモードに入らずに結果を表示できる（NDMF のプレビュー。シーンは変更しない）
    // VRChat SDK があるときは IEditorOnly（アップロード時に取り除かれる）、NDMF があるときは INDMFEditorOnly を実装する
    [AddComponentMenu("MeshDeletionTool/MeshDeletionForTexture")]
    [DisallowMultipleComponent]
    public class MeshDeletionForTexture : MonoBehaviour
#if VRC_SDK_VRCSDK3
        , VRC.SDKBase.IEditorOnly
#endif
#if NDMF
        , nadena.dev.ndmf.INDMFEditorOnly
#endif
    {
        // インスペクターに表示する注意書き
        public const string Note = "メッシュはプレイモードに入るときとアバターのアップロード時に NDMF が生成して置き換えます。元のメッシュは変更しません。";

        [Tooltip("アルファ値がこの値より小さいテクセルを透明として削除する")]
        [Range(0f, 1f)]
        public float alphaThreshold = 0.5f;

        [Tooltip("切り口がテクスチャの境界からずれてよい量（テクセル）。小さいほど正確だがポリゴンが増える")]
        [Range(0.5f, 4f)]
        public float boundaryPrecisionTexels = 1f;

        [Tooltip("アルファ境界付近の三角形を辺の中点で細分化してから削る（細部が残る）。無効にすると既存の辺上にしか頂点を追加しない")]
        public bool refineBoundary = true;

        [Tooltip("細分化の最大深さ（詳細設定）")]
        [Range(0, 5)]
        public int refineMaxDepth = 3;

        [Tooltip("切断後に元の三角形ごとにポリゴンを結合し直し、切り口の頂点を間引く（詳細設定）")]
        public bool mergeAfterCut = true;

        [Tooltip("サブメッシュ毎に処理するかどうか。空なら、テクスチャを持つ全サブメッシュを処理する")]
        public bool[] subMeshEnabled = new bool[0];

        // 処理対象の Renderer（同じオブジェクトの SkinnedMeshRenderer または MeshRenderer。無ければ null）
        public Renderer TargetRenderer
        {
            get
            {
                Renderer renderer = GetComponent<Renderer>();
                return renderer is SkinnedMeshRenderer || renderer is MeshRenderer ? renderer : null;
            }
        }

        // サブメッシュ subMeshIndex を処理するか（subMeshEnabled が空なら全て。配列より後ろのサブメッシュも処理する）
        public bool IsSubMeshEnabled(int subMeshIndex)
        {
            if (subMeshEnabled == null || subMeshEnabled.Length == 0 || subMeshIndex >= subMeshEnabled.Length)
                return true;
            return subMeshIndex >= 0 && subMeshEnabled[subMeshIndex];
        }
    }
}
