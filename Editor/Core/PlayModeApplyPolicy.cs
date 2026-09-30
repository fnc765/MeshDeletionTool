namespace MeshDeletionTool
{
    // プレイモードに入るときに MeshDeletionForTexture を誰が適用するか
    internal enum PlayModeApplier
    {
        // 適用しない（NDMF の Apply on Play がオフ）
        None,
        // NDMF（アバターのビルド）が適用する
        Ndmf,
        // MeshDeletionTool の簡易適用（MeshDeletionPlayModeApplier）が適用する
        Fallback
    }

    // プレイモードでの適用の担当の決め方。Unity には依存しない（インスペクターの表示と簡易適用が同じ判定を使う）
    // NDMF の Apply on Play はアバターのルート（VRC Avatar Descriptor など、NDMF の RuntimeUtil.FindAvatarInParents が見つけるもの）の配下しか処理しないため、
    // 配下にないもの（衣装のプレハブ単体など）と NDMF が無いプロジェクトでは簡易適用が担当する。NDMF の Apply on Play がオフなら、利用者の設定に従いどちらも適用しない
    internal static class PlayModeApplyPolicy
    {
        internal static PlayModeApplier Decide(bool ndmfInstalled, bool ndmfApplyOnPlay, bool underAvatarRoot)
        {
            if (!ndmfInstalled)
                return PlayModeApplier.Fallback;
            if (!ndmfApplyOnPlay)
                return PlayModeApplier.None;
            return underAvatarRoot ? PlayModeApplier.Ndmf : PlayModeApplier.Fallback;
        }
    }
}
