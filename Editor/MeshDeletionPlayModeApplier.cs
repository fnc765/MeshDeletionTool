using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace MeshDeletionTool
{
    // プレイモードでの簡易適用: NDMF が処理しない MeshDeletionForTexture（アバターのルートの配下にないもの。衣装のプレハブ単体など）を、
    // プレイモードに入ったときに MeshDeletionTool が自分で適用する（メッシュを削って Renderer のメッシュを置き換え、コンポーネントを取り除く）
    // NDMF の Apply on Play は、アバターのルート（VRC Avatar Descriptor、NDMF Avatar Root など）を持つオブジェクトにだけ処理用のコンポーネントを付け、
    // その Awake でアバターをビルドする。EnteredPlayMode は全オブジェクトの Awake の後に来るので、この時点で NDMF が処理したものはコンポーネントが消えている。
    // それでも担当の判定（PlayModeApplyPolicy）でアバターの配下のものは常に NDMF に任せ、二重に処理しない
    // プレイモード中の変更は終了時に元に戻るので非破壊（生成したメッシュは保存せず、エディットモードに戻ったときに破棄する）
    [InitializeOnLoad]
    internal static class MeshDeletionPlayModeApplier
    {
        // 今回のプレイモードで生成したメッシュ（エディットモードに戻ったときに破棄する）
        private static readonly List<Mesh> GeneratedMeshes = new List<Mesh>();

        static MeshDeletionPlayModeApplier()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        // component をプレイモードで誰が適用するか（インスペクターの表示と共通）
        internal static PlayModeApplier ApplierFor(MeshDeletionForTexture component)
        {
#if NDMF
            return PlayModeApplyPolicy.Decide(true, nadena.dev.ndmf.config.Config.ApplyOnPlay, IsUnderAvatarRoot(component.transform));
#else
            return PlayModeApplyPolicy.Decide(false, false, false);
#endif
        }

        // NDMF がアバターのルートとして扱うオブジェクト（自身または親）があるか。NDMF の Apply on Play と同じ判定（RuntimeUtil.FindAvatarInParents）
        internal static bool IsUnderAvatarRoot(Transform transform)
        {
#if NDMF
            return nadena.dev.ndmf.runtime.RuntimeUtil.FindAvatarInParents(transform) != null;
#else
            return false;
#endif
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredPlayMode)
            {
                ApplyToLoadedScenes();
            }
            else if (change == PlayModeStateChange.EnteredEditMode)
            {
                foreach (Mesh mesh in GeneratedMeshes)
                {
                    if (mesh != null)
                        Object.DestroyImmediate(mesh);
                }
                GeneratedMeshes.Clear();
            }
        }

        // 読み込まれている全シーンの MeshDeletionForTexture（無効なオブジェクトのものも含む）のうち、簡易適用の担当のものを処理する
        private static void ApplyToLoadedScenes()
        {
            List<MeshDeletionForTexture> components = new List<MeshDeletionForTexture>();
            for (int sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
            {
                Scene scene = SceneManager.GetSceneAt(sceneIndex);
                if (!scene.IsValid() || !scene.isLoaded)
                    continue;
                foreach (GameObject root in scene.GetRootGameObjects())
                    components.AddRange(root.GetComponentsInChildren<MeshDeletionForTexture>(true));
            }

            foreach (MeshDeletionForTexture component in components)
            {
                // NDMF が処理して取り除いたもの、NDMF の担当のもの、Apply on Play がオフのときは何もしない
                if (component == null || ApplierFor(component) != PlayModeApplier.Fallback)
                    continue;
                GameObject gameObject = component.gameObject;
                try
                {
                    MeshDeletionApplyResult result = MeshDeletionApplier.Apply(component);
                    if (result.Succeeded)
                    {
                        result.NewMesh.hideFlags = HideFlags.DontSave;
                        GeneratedMeshes.Add(result.NewMesh);
                        Debug.Log("[簡易適用] " + result.Summary + "（'" + gameObject.name + "' は" + FallbackReason() + "、MeshDeletionTool が置き換えました。プレイモードを終えると元に戻ります）", gameObject);
                    }
                    else
                    {
                        Debug.LogError("[簡易適用] '" + gameObject.name + "': " + MeshDeletionApplier.Describe(result), gameObject);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError("[簡易適用] '" + gameObject.name + "' の処理に失敗しました: " + e.Message, gameObject);
                    Debug.LogException(e, gameObject);
                }
                // NDMF と同じく、処理の成否にかかわらずコンポーネントを取り除く（プレイモード中だけ）
                Object.DestroyImmediate(component);
            }
        }

        // 簡易適用が担当する理由（ログ用）
        private static string FallbackReason()
        {
#if NDMF
            return "アバター（VRC Avatar Descriptor など）の配下にないため NDMF が処理しないので";
#else
            return "NDMF 1.8.0 以降がプロジェクトに無いため";
#endif
        }
    }
}
