using System.Linq;
using UnityEditor;
using UnityEngine;

namespace MeshDeletionTool
{
    // MeshDeletionForTexture コンポーネントのインスペクター（設定項目の名前と意味はウィンドウ版と同じ）と、ヒエラルキーの右クリックメニュー
    [CustomEditor(typeof(MeshDeletionForTexture))]
    internal class MeshDeletionForTextureEditor : Editor
    {
        private SerializedProperty alphaThreshold;
        private SerializedProperty boundaryPrecisionTexels;
        private SerializedProperty refineBoundary;
        private SerializedProperty refineMaxDepth;
        private SerializedProperty mergeAfterCut;
        private SerializedProperty subMeshEnabled;
        private bool showAdvancedSettings;
        private readonly TexelSizeEstimator texelSizeEstimator = new TexelSizeEstimator();

        private void OnEnable()
        {
            alphaThreshold = serializedObject.FindProperty(nameof(MeshDeletionForTexture.alphaThreshold));
            boundaryPrecisionTexels = serializedObject.FindProperty(nameof(MeshDeletionForTexture.boundaryPrecisionTexels));
            refineBoundary = serializedObject.FindProperty(nameof(MeshDeletionForTexture.refineBoundary));
            refineMaxDepth = serializedObject.FindProperty(nameof(MeshDeletionForTexture.refineMaxDepth));
            mergeAfterCut = serializedObject.FindProperty(nameof(MeshDeletionForTexture.mergeAfterCut));
            subMeshEnabled = serializedObject.FindProperty(nameof(MeshDeletionForTexture.subMeshEnabled));
#if NDMF
            MeshDeletionPreviewFilter.Toggle.IsEnabled.OnChange += OnPreviewToggleChanged;
#endif
        }

#if NDMF
        private void OnDisable()
        {
            MeshDeletionPreviewFilter.Toggle.IsEnabled.OnChange -= OnPreviewToggleChanged;
        }

        // NDMF のプレビュー設定ウィンドウから切り替えられたときもボタンの表示を合わせる
        private void OnPreviewToggleChanged(bool enabled)
        {
            Repaint();
        }
#endif

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            MeshDeletionForTexture component = (MeshDeletionForTexture)target;

            // 対象の Renderer・メッシュ・マテリアル（無いときは警告を出す。ログは出さない）
            Renderer renderer = component.TargetRenderer;
            Mesh mesh = null;
            Material[] materials = null;
            if (renderer == null)
            {
                EditorGUILayout.HelpBox("このオブジェクトに SkinnedMeshRenderer または MeshRenderer が無いため処理できません。", MessageType.Warning);
            }
            else
            {
                mesh = MeshDeletionRunner.GetOriginalMesh(renderer, false);
                materials = MeshDeletionRunner.GetOriginalMaterials(renderer, false);
                if (mesh == null || materials == null)
                    EditorGUILayout.HelpBox("Renderer にメッシュが無いため処理できません。", MessageType.Warning);
                else if (!MeshDeletionRunner.SubMeshesWithTexture(mesh.subMeshCount, materials).Any(hasTexture => hasTexture))
                    EditorGUILayout.HelpBox("マテリアルにテクスチャ（Texture2D）が無いため処理できません。", MessageType.Warning);
                else if (MeshDeletionRunner.FindMeshProblem(mesh, true) is string problem)
                    EditorGUILayout.HelpBox(problem, MessageType.Error);
            }
            DrawPlayModeNote(component);
            DrawPreviewControls();

            // 設定（ウィンドウ版と同じ名前）
            EditorGUILayout.PropertyField(alphaThreshold, new GUIContent("アルファ閾値", alphaThreshold.tooltip));
            EditorGUILayout.PropertyField(refineBoundary, new GUIContent("境界の細分化", refineBoundary.tooltip));
            EditorGUI.BeginDisabledGroup(!refineBoundary.boolValue);
            EditorGUILayout.PropertyField(boundaryPrecisionTexels, new GUIContent("境界の精度（テクセル）", boundaryPrecisionTexels.tooltip));
            string texelSizeHint = mesh != null ? texelSizeEstimator.GetHint(mesh, materials, component.IsSubMeshEnabled) : null;
            if (texelSizeHint != null)
                EditorGUILayout.LabelField(" ", texelSizeHint, EditorStyles.miniLabel);
            showAdvancedSettings = EditorGUILayout.Foldout(showAdvancedSettings, "詳細設定");
            if (showAdvancedSettings)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(refineMaxDepth, new GUIContent("細分化の最大深さ", refineMaxDepth.tooltip));
                EditorGUILayout.PropertyField(mergeAfterCut, new GUIContent("切断後の再結合", mergeAfterCut.tooltip));
                EditorGUI.indentLevel--;
            }
            EditorGUI.EndDisabledGroup();

            // サブメッシュ毎の処理対象（マテリアル名で表示。テクスチャの無いサブメッシュは処理できないので無効表示）
            if (mesh != null && materials != null)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("処理対象のサブメッシュ", EditorStyles.boldLabel);
                bool[] hasTexture = MeshDeletionRunner.SubMeshesWithTexture(mesh.subMeshCount, materials);
                EnsureSubMeshArray(mesh.subMeshCount, hasTexture);
                for (int subMeshIndex = 0; subMeshIndex < mesh.subMeshCount; subMeshIndex++)
                {
                    SerializedProperty element = subMeshEnabled.GetArrayElementAtIndex(subMeshIndex);
                    Material material = subMeshIndex < materials.Length ? materials[subMeshIndex] : null;
                    string label = "サブメッシュ " + subMeshIndex + ": " + (material != null ? material.name : "（マテリアルなし）") +
                                   (hasTexture[subMeshIndex] ? "  [" + material.mainTexture.name + "]" : "  （テクスチャなし）");
                    EditorGUI.BeginDisabledGroup(!hasTexture[subMeshIndex]);
                    element.boolValue = EditorGUILayout.ToggleLeft(label, element.boolValue && hasTexture[subMeshIndex]);
                    EditorGUI.EndDisabledGroup();
                }
            }

            serializedObject.ApplyModifiedProperties();
        }

        // プレイモードで誰が置き換えるか（NDMF / 簡易適用 / しない）の注意書き。判定は簡易適用（MeshDeletionPlayModeApplier）と同じ
        private static void DrawPlayModeNote(MeshDeletionForTexture component)
        {
            switch (MeshDeletionPlayModeApplier.ApplierFor(component))
            {
                case PlayModeApplier.Ndmf:
                    EditorGUILayout.HelpBox(MeshDeletionForTexture.Note, MessageType.Info);
                    break;
                case PlayModeApplier.None:
                    EditorGUILayout.HelpBox("NDMF の Apply on Play（Tools/NDM Framework/Apply on Play）がオフのため、プレイモードでは置き換えません。アバターのアップロード時は NDMF が置き換えます。", MessageType.Warning);
                    break;
                default:
#if NDMF
                    EditorGUILayout.HelpBox("アバター（VRC Avatar Descriptor）の配下にないため、プレイモードでは MeshDeletionTool の簡易適用で置き換えます（プレイモードを終えると元に戻ります）。アップロード時の置き換えはアバターの配下でのみ行われます。", MessageType.Info);
#else
                    EditorGUILayout.HelpBox("NDMF（Non-Destructive Modular Framework）1.8.0 以降が見つかりません。プレイモードでは MeshDeletionTool の簡易適用で置き換えますが、アップロード時には置き換わりません。" +
                                            "VCC / ALCOM で nadena.dev.ndmf をプロジェクトに追加してください（Modular Avatar を入れていれば一緒に入ります）。", MessageType.Warning);
#endif
                    break;
            }
        }

        // プレビューのボタンと状態。NDMF のプレビュー（MeshDeletionPreviewFilter）を切り替える。シーン上の全ての MeshDeletionForTexture で共通
        private static void DrawPreviewControls()
        {
#if NDMF
            bool previewOn = MeshDeletionPreviewFilter.Toggle.IsEnabled.Value;
            EditorGUI.BeginDisabledGroup(EditorApplication.isPlayingOrWillChangePlaymode);
            bool requested = GUILayout.Toggle(previewOn, previewOn ? "プレビュー: オン" : "プレビュー: オフ", "Button");
            EditorGUI.EndDisabledGroup();
            if (requested != previewOn)
            {
                MeshDeletionPreviewFilter.Toggle.IsEnabled.Value = requested;
                SceneView.RepaintAll();
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorGUILayout.LabelField("プレイモード中はプレビューしません。", EditorStyles.miniLabel);
            }
            else if (!previewOn)
            {
                EditorGUILayout.LabelField("オンにすると、プレイモードに入らずに適用後の表示を確認できます。", EditorStyles.miniLabel);
            }
            else if (!Menu.GetChecked(MeshDeletionPreviewFilter.NdmfEnablePreviewsMenu))
            {
                EditorGUILayout.HelpBox("NDMF のプレビューが無効になっているため表示されません（" + MeshDeletionPreviewFilter.NdmfEnablePreviewsMenu + "）。", MessageType.Warning);
                if (GUILayout.Button("NDMF のプレビューを有効にする"))
                    EditorApplication.ExecuteMenuItem(MeshDeletionPreviewFilter.NdmfEnablePreviewsMenu);
            }
            else
            {
                EditorGUILayout.HelpBox("プレビュー中（NDMF）: シーン上の全ての MeshDeletionForTexture を適用した表示です。NDMF が作る表示用のコピーだけを変えるので、" +
                                        "元のメッシュ・シーン・プレハブは変更されず、保存もされません。", MessageType.Info);
            }
#else
            EditorGUI.BeginDisabledGroup(true);
            GUILayout.Toggle(false, "プレビュー: オフ", "Button");
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.LabelField("プレビューには NDMF 1.8.0 以降が必要です。", EditorStyles.miniLabel);
#endif
        }

        // subMeshEnabled の長さをサブメッシュ数に合わせる（空から広げるときはテクスチャを持つサブメッシュを対象に、途中から広げるときは対象にする）
        private void EnsureSubMeshArray(int subMeshCount, bool[] hasTexture)
        {
            int oldSize = subMeshEnabled.arraySize;
            if (oldSize == subMeshCount)
                return;
            subMeshEnabled.arraySize = subMeshCount;
            for (int subMeshIndex = oldSize; subMeshIndex < subMeshCount; subMeshIndex++)
                subMeshEnabled.GetArrayElementAtIndex(subMeshIndex).boolValue = oldSize == 0 ? hasTexture[subMeshIndex] : true;
        }

        // ヒエラルキーの右クリック: 選択中のオブジェクト（SkinnedMeshRenderer / MeshRenderer を持つもの）にコンポーネントを付ける
        private const string AddMenuPath = "GameObject/MeshDeletionTool/MeshDeletionForTexture を追加";

        [MenuItem(AddMenuPath, false, 10)]
        private static void AddComponentToSelection(MenuCommand command)
        {
            // GameObject メニューは選択したオブジェクトの数だけ呼ばれるので、最初の 1 回だけ処理する
            if (Selection.objects.Length > 1 && command.context != Selection.objects[0])
                return;
            foreach (GameObject gameObject in Selection.gameObjects)
            {
                if (!HasSupportedRenderer(gameObject))
                {
                    Debug.LogWarning("'" + gameObject.name + "' には SkinnedMeshRenderer または MeshRenderer が無いため MeshDeletionForTexture を付けませんでした。", gameObject);
                    continue;
                }
                if (gameObject.GetComponent<MeshDeletionForTexture>() != null)
                    continue;
                Undo.AddComponent<MeshDeletionForTexture>(gameObject);
            }
        }

        [MenuItem(AddMenuPath, true)]
        private static bool ValidateAddComponentToSelection()
        {
            return Selection.gameObjects.Any(HasSupportedRenderer);
        }

        private static bool HasSupportedRenderer(GameObject gameObject)
        {
            Renderer renderer = gameObject.GetComponent<Renderer>();
            return renderer is SkinnedMeshRenderer || renderer is MeshRenderer;
        }
    }
}
