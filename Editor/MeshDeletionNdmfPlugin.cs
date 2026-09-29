// NDMF（nadena.dev.ndmf）がプロジェクトにあるときだけコンパイルされる（asmdef の versionDefines で NDMF が定義される）
#if NDMF
using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using nadena.dev.ndmf.localization;
using UnityEngine;
using Object = UnityEngine.Object;

[assembly: ExportsPlugin(typeof(MeshDeletionTool.MeshDeletionNdmfPlugin))]

namespace MeshDeletionTool
{
    // NDMF プラグイン: アバターのビルド（プレイモードに入るときの Apply on Play と、アップロード）のたびに、アバター内の MeshDeletionForTexture を処理する
    // Transforming フェーズで Modular Avatar の後に実行し、各コンポーネントの Renderer のメッシュを削った結果（メモリ上の新しい Mesh）に置き換え、
    // コンポーネントを取り除く。シーン上の元のメッシュ・アセットは変更しない
    // NDMF はプラグインを既定で VRChat アバター（nadena.dev.ndmf.vrchat.avatar3）でだけ実行する（それ以外のプラットフォームでは実行しない空の処理に置き換える）。
    // メッシュの削除はプラットフォームに依存しないので RunsOnAllPlatforms を付ける（NDMF 1.8.0 以降）
    // Modular Avatar が無いプロジェクトでも AfterPlugin("nadena.dev.modular-avatar") はエラーにならない（NDMF は存在しないプラグインへの順序指定を無視する）
    [RunsOnAllPlatforms]
    public class MeshDeletionNdmfPlugin : Plugin<MeshDeletionNdmfPlugin>
    {
        public override string QualifiedName => "com.ochoco.mesh-deletion-tool";
        public override string DisplayName => "MeshDeletionTool";

        // エラー報告（NDMF のエラーウィンドウ）に出す文言は MeshDeletionApplier.Messages（簡易適用の Console の文言と共通）
        private static readonly Localizer Localizer = new Localizer("ja-jp", () => new List<(string, Func<string, string>)>
        {
            ("ja-jp", key => MeshDeletionApplier.Messages.TryGetValue(key, out string value) ? value : null)
        });

        protected override void Configure()
        {
            InPhase(BuildPhase.Transforming)
                .AfterPlugin("nadena.dev.modular-avatar")
                .Run("MeshDeletionForTexture を適用", Apply);
        }

        // アバター内の全 MeshDeletionForTexture（無効なオブジェクトのものも含む）を処理する。1 つが失敗しても他は処理し、最後にコンポーネントを取り除く
        private static void Apply(BuildContext context)
        {
            MeshDeletionForTexture[] components = context.AvatarRootObject.GetComponentsInChildren<MeshDeletionForTexture>(true);
            foreach (MeshDeletionForTexture component in components)
            {
                if (component == null)
                    continue;
                using (ErrorReport.WithContextObject(component.gameObject))
                {
                    try
                    {
                        ApplyOne(context, component);
                    }
                    catch (Exception e)
                    {
                        ErrorReport.ReportException(e);
                    }
                }
                Object.DestroyImmediate(component);
            }
        }

        // 1 つのコンポーネント: 設定を読み、メッシュを削って Renderer に割り当てる（MeshDeletionApplier）。処理できない形なら NDMF のエラーとして報告する
        private static void ApplyOne(BuildContext context, MeshDeletionForTexture component)
        {
            GameObject gameObject = component.gameObject;
            MeshDeletionApplyResult result = MeshDeletionApplier.Apply(component);
            if (!result.Succeeded)
            {
                if (result.FailureDetail != null)
                    Debug.LogError("MeshDeletionForTexture '" + gameObject.name + "': " + result.FailureDetail, gameObject);
                ErrorReport.ReportError(Localizer, ErrorSeverity.NonFatal, result.FailureKey, gameObject);
                return;
            }

            // 置き換えを NDMF に登録し（エラー報告や他のプラグインが元のメッシュを引けるように）、生成したメッシュをビルドのアセットとして保存してもらう
            ObjectRegistry.RegisterReplacedObject(result.OriginalMesh, result.NewMesh);
            context.AssetSaver.SaveAsset(result.NewMesh);
            Debug.Log("[NDMF] " + result.Summary + "（'" + gameObject.name + "'）", gameObject);
        }
    }
}
#endif
