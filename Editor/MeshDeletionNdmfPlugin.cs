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

        // エラー報告（NDMF のエラーウィンドウ）に出す文言。キーは ReportError に渡すもので、":description" が説明
        private static readonly Dictionary<string, string> Strings = new Dictionary<string, string>
        {
            { "mesh-deletion.no-renderer", "MeshDeletionForTexture: SkinnedMeshRenderer または MeshRenderer が無い" },
            { "mesh-deletion.no-renderer:description", "MeshDeletionForTexture はメッシュを描いているオブジェクト（SkinnedMeshRenderer、または MeshRenderer と MeshFilter）に付けてください。このオブジェクトは処理しませんでした。" },
            { "mesh-deletion.no-mesh", "MeshDeletionForTexture: Renderer にメッシュが無い" },
            { "mesh-deletion.no-mesh:description", "Renderer にメッシュが設定されていないため処理しませんでした。" },
            { "mesh-deletion.unsupported-mesh", "MeshDeletionForTexture: 処理できないメッシュ" },
            { "mesh-deletion.unsupported-mesh:description", "三角形でないサブメッシュがある、または UV（UV0）が無いメッシュは処理できません。理由は Console のエラーに出ています。このオブジェクトは処理しませんでした。" },
        };

        private static readonly Localizer Localizer = new Localizer("ja-jp", () => new List<(string, Func<string, string>)>
        {
            ("ja-jp", key => Strings.TryGetValue(key, out string value) ? value : null)
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

        // 1 つのコンポーネント: 設定を読み、メッシュを削って Renderer に割り当てる
        private static void ApplyOne(BuildContext context, MeshDeletionForTexture component)
        {
            GameObject gameObject = component.gameObject;
            Renderer renderer = component.TargetRenderer;
            if (renderer == null)
            {
                ErrorReport.ReportError(Localizer, ErrorSeverity.NonFatal, "mesh-deletion.no-renderer", gameObject);
                return;
            }
            Mesh originalMesh = MeshDeletionRunner.GetOriginalMesh(renderer, false);
            if (originalMesh == null)
            {
                ErrorReport.ReportError(Localizer, ErrorSeverity.NonFatal, "mesh-deletion.no-mesh", gameObject);
                return;
            }
            string problem = MeshDeletionRunner.FindMeshProblem(originalMesh, true);
            if (problem != null)
            {
                Debug.LogError("MeshDeletionForTexture '" + gameObject.name + "': " + problem, gameObject);
                ErrorReport.ReportError(Localizer, ErrorSeverity.NonFatal, "mesh-deletion.unsupported-mesh", gameObject);
                return;
            }

            MeshDeletionOptions options = MeshDeletionRunner.OptionsFromComponent(component, originalMesh.subMeshCount);
            MeshDeletionResult result = MeshDeletionRunner.Run(renderer, options);
            Mesh newMesh = result.Mesh;

            // 置き換えを NDMF に登録し（エラー報告や他のプラグインが元のメッシュを引けるように）、生成したメッシュをビルドのアセットとして保存してもらう
            ObjectRegistry.RegisterReplacedObject(originalMesh, newMesh);
            context.AssetSaver.SaveAsset(newMesh);
            if (renderer is SkinnedMeshRenderer skinnedMeshRenderer)
            {
                skinnedMeshRenderer.sharedMesh = newMesh;
            }
            else
            {
                renderer.GetComponent<MeshFilter>().sharedMesh = newMesh;
            }
            Debug.Log("MeshDeletionForTexture '" + gameObject.name + "': " + result.Summary, gameObject);
        }
    }
}
#endif
