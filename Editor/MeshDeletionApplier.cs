using System.Collections.Generic;
using UnityEngine;

namespace MeshDeletionTool
{
    // MeshDeletionApplier.Apply の結果
    internal sealed class MeshDeletionApplyResult
    {
        // 処理できなかった理由のキー（Messages のキー。処理できたなら null）と、Console に出す詳しい理由（無ければ null）
        public string FailureKey;
        public string FailureDetail;
        // 置き換える前のメッシュと、置き換えた後のメッシュ（保存されていない）
        public Mesh OriginalMesh;
        public Mesh NewMesh;
        // 1 行の要約（MeshDeletionRunner.Run の Summary）
        public string Summary;

        public bool Succeeded => FailureKey == null;
    }

    // MeshDeletionForTexture 1 つを適用する（設定を読み、メッシュを削って Renderer のメッシュを置き換える）。コンポーネントは取り除かない
    // NDMF プラグイン（アバターのビルド）と、アバターの配下にないもののプレイモードでの簡易適用（MeshDeletionPlayModeApplier）が共通に使う
    internal static class MeshDeletionApplier
    {
        internal const string NoRenderer = "mesh-deletion.no-renderer";
        internal const string NoMesh = "mesh-deletion.no-mesh";
        internal const string UnsupportedMesh = "mesh-deletion.unsupported-mesh";

        // 処理できなかったときの文言（NDMF のエラーウィンドウと Console で共通）。":description" が付いたキーが説明
        internal static readonly Dictionary<string, string> Messages = new Dictionary<string, string>
        {
            { NoRenderer, "MeshDeletionForTexture: SkinnedMeshRenderer または MeshRenderer が無い" },
            { NoRenderer + ":description", "MeshDeletionForTexture はメッシュを描いているオブジェクト（SkinnedMeshRenderer、または MeshRenderer と MeshFilter）に付けてください。このオブジェクトは処理しませんでした。" },
            { NoMesh, "MeshDeletionForTexture: Renderer にメッシュが無い" },
            { NoMesh + ":description", "Renderer にメッシュが設定されていないため処理しませんでした。" },
            { UnsupportedMesh, "MeshDeletionForTexture: 処理できないメッシュ" },
            { UnsupportedMesh + ":description", "三角形でないサブメッシュがある、または UV（UV0）が無いメッシュは処理できません。理由は Console のエラーに出ています。このオブジェクトは処理しませんでした。" },
        };

        // 処理できなかった理由の 1 行（Console 用）
        internal static string Describe(MeshDeletionApplyResult result)
        {
            if (result.Succeeded)
                return null;
            string text = Messages[result.FailureKey] + "。" + Messages[result.FailureKey + ":description"];
            return result.FailureDetail != null ? text + " " + result.FailureDetail : text;
        }

        // component の設定でメッシュを削り、Renderer（SkinnedMeshRenderer の sharedMesh、または MeshFilter の sharedMesh）に割り当てる
        // 処理できない形なら何も変更せず、FailureKey を設定した結果を返す。処理中の例外（テクスチャの読み出しの失敗など）は呼び出し側へ投げる
        internal static MeshDeletionApplyResult Apply(MeshDeletionForTexture component)
        {
            MeshDeletionApplyResult result = new MeshDeletionApplyResult();
            Renderer renderer = component.TargetRenderer;
            if (renderer == null)
            {
                result.FailureKey = NoRenderer;
                return result;
            }
            Mesh originalMesh = MeshDeletionRunner.GetOriginalMesh(renderer, false);
            if (originalMesh == null)
            {
                result.FailureKey = NoMesh;
                return result;
            }
            string problem = MeshDeletionRunner.FindMeshProblem(originalMesh, true);
            if (problem != null)
            {
                result.FailureKey = UnsupportedMesh;
                result.FailureDetail = problem;
                return result;
            }

            MeshDeletionOptions options = MeshDeletionRunner.OptionsFromComponent(component, originalMesh.subMeshCount);
            MeshDeletionResult deletion = MeshDeletionRunner.Run(renderer, options);
            result.OriginalMesh = originalMesh;
            result.NewMesh = deletion.Mesh;
            result.Summary = deletion.Summary;
            if (renderer is SkinnedMeshRenderer skinnedMeshRenderer)
            {
                skinnedMeshRenderer.sharedMesh = deletion.Mesh;
            }
            else
            {
                renderer.GetComponent<MeshFilter>().sharedMesh = deletion.Mesh;
            }
            return result;
        }
    }
}
