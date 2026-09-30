using System.Linq;
using UnityEditor;
using UnityEngine;

namespace MeshDeletionTool
{
    // MeshDeletionForTexture コンポーネントのインスペクター（設定項目の表示は MeshDeletionSettingsGUI でウィンドウ版と共通）と、ヒエラルキーの右クリックメニュー
    // 上から (1) 状態（誰が置き換えるか）と説明、(2) プレビューとポリゴン数の変化、(3) 基本設定、(4) 処理対象のサブメッシュ、(5) 詳細設定
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
        private bool showExplanation;
        private bool showSubMeshStats;
        // 切り抜きの精度で「カスタム」を選んでいる（プリセットと同じ値でもスライダーを出したままにする）
        private bool customPrecision;
        private readonly TexelSizeEstimator texelSizeEstimator = new TexelSizeEstimator();

        private void OnEnable()
        {
            alphaThreshold = serializedObject.FindProperty(nameof(MeshDeletionForTexture.alphaThreshold));
            boundaryPrecisionTexels = serializedObject.FindProperty(nameof(MeshDeletionForTexture.boundaryPrecisionTexels));
            refineBoundary = serializedObject.FindProperty(nameof(MeshDeletionForTexture.refineBoundary));
            refineMaxDepth = serializedObject.FindProperty(nameof(MeshDeletionForTexture.refineMaxDepth));
            mergeAfterCut = serializedObject.FindProperty(nameof(MeshDeletionForTexture.mergeAfterCut));
            subMeshEnabled = serializedObject.FindProperty(nameof(MeshDeletionForTexture.subMeshEnabled));
            MeshDeletionPreviewStats.Changed += Repaint;
#if NDMF
            MeshDeletionPreviewFilter.Toggle.IsEnabled.OnChange += OnPreviewToggleChanged;
#endif
        }

        private void OnDisable()
        {
            MeshDeletionPreviewStats.Changed -= Repaint;
#if NDMF
            MeshDeletionPreviewFilter.Toggle.IsEnabled.OnChange -= OnPreviewToggleChanged;
#endif
        }

#if NDMF
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

            // (1) 状態（誰がいつ置き換えるか）と説明
            DrawStatus(component);

            // (2) プレビューとポリゴン数の変化
            EditorGUILayout.Space(4f);
            DrawPreviewControls(component, mesh, materials);

            // (3) 基本設定（値は SerializedProperty に書き戻す: Undo と NDMF のプレビューの更新が効く）
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("基本設定", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            float threshold = MeshDeletionSettingsGUI.AlphaThreshold(alphaThreshold.floatValue);
            if (EditorGUI.EndChangeCheck())
                alphaThreshold.floatValue = threshold;
            EditorGUILayout.Space(2f);
            EditorGUI.BeginChangeCheck();
            bool refine = MeshDeletionSettingsGUI.Refine(refineBoundary.boolValue);
            if (EditorGUI.EndChangeCheck())
                refineBoundary.boolValue = refine;
            EditorGUILayout.Space(2f);
            EditorGUI.BeginDisabledGroup(!refineBoundary.boolValue);
            EditorGUI.BeginChangeCheck();
            float precision = MeshDeletionSettingsGUI.Precision(boundaryPrecisionTexels.floatValue, ref customPrecision,
                MillimeterHint(mesh, materials, component, boundaryPrecisionTexels.floatValue));
            if (EditorGUI.EndChangeCheck())
                boundaryPrecisionTexels.floatValue = precision;
            EditorGUI.EndDisabledGroup();

            // (4) 処理対象のサブメッシュ
            if (mesh != null && materials != null)
            {
                EditorGUILayout.Space(8f);
                DrawSubMeshList(mesh, materials);
            }

            // (5) 詳細設定（既定で閉じる）
            EditorGUILayout.Space(4f);
            showAdvancedSettings = EditorGUILayout.Foldout(showAdvancedSettings, MeshDeletionSettingsGUI.AdvancedLabel, true);
            if (showAdvancedSettings)
            {
                EditorGUI.indentLevel++;
                EditorGUI.BeginDisabledGroup(!refineBoundary.boolValue);
                EditorGUI.BeginChangeCheck();
                int depth = MeshDeletionSettingsGUI.MaxDepth(refineMaxDepth.intValue);
                if (EditorGUI.EndChangeCheck())
                    refineMaxDepth.intValue = depth;
                EditorGUI.BeginChangeCheck();
                bool merge = MeshDeletionSettingsGUI.Merge(mergeAfterCut.boolValue);
                if (EditorGUI.EndChangeCheck())
                    mergeAfterCut.boolValue = merge;
                EditorGUI.EndDisabledGroup();
                if (!refineBoundary.boolValue)
                    MeshDeletionSettingsGUI.Help("「輪郭に沿って細かく切る」がオフのときは使われません。");
                EditorGUI.indentLevel--;
            }

            serializedObject.ApplyModifiedProperties();
        }

        // 「≈ 0.5〜0.9 mm 単位で輪郭に沿わせます」（1 テクセルの大きさの概算 × 精度。求められなければ null）
        private string MillimeterHint(Mesh mesh, Material[] materials, MeshDeletionForTexture component, float precision)
        {
            if (mesh == null || !texelSizeEstimator.TryGetRange(mesh, materials, component.IsSubMeshEnabled, out float min, out float max))
                return null;
            return PrecisionPresets.MillimeterHint(min, max, precision);
        }

        // 処理対象のサブメッシュ: マテリアル名（「 (Instance)」を除く）とテクスチャ名（灰色）。テクスチャの無いサブメッシュは処理できないので無効表示と警告アイコン
        private void DrawSubMeshList(Mesh mesh, Material[] materials)
        {
            bool[] hasTexture = MeshDeletionRunner.SubMeshesWithTexture(mesh.subMeshCount, materials);
            EnsureSubMeshArray(mesh.subMeshCount, hasTexture);

            Rect header = EditorGUILayout.GetControlRect();
            Rect noneRect = new Rect(header.xMax - 72f, header.y, 72f, header.height);
            Rect allRect = new Rect(noneRect.x - 74f, header.y, 72f, header.height);
            EditorGUI.LabelField(new Rect(header.x, header.y, allRect.x - header.x, header.height),
                new GUIContent("処理対象のサブメッシュ", "チェックしたサブメッシュ（マテリアル）だけを処理します。テクスチャの無いサブメッシュは処理できません。"), EditorStyles.boldLabel);
            if (GUI.Button(allRect, new GUIContent("すべて選択", "テクスチャのある全てのサブメッシュを処理対象にします。"), EditorStyles.miniButtonLeft))
                SetAllSubMeshes(hasTexture, true);
            if (GUI.Button(noneRect, new GUIContent("すべて解除", "全てのサブメッシュを処理対象から外します。"), EditorStyles.miniButtonRight))
                SetAllSubMeshes(hasTexture, false);

            for (int subMeshIndex = 0; subMeshIndex < mesh.subMeshCount; subMeshIndex++)
            {
                SerializedProperty element = subMeshEnabled.GetArrayElementAtIndex(subMeshIndex);
                Material material = subMeshIndex < materials.Length ? materials[subMeshIndex] : null;
                string materialName = material != null ? PolygonStatsFormat.MaterialDisplayName(material.name) : "（マテリアルなし）";
                string textureName = hasTexture[subMeshIndex] ? material.mainTexture.name : null;

                Rect row = EditorGUILayout.GetControlRect();
                float textureWidth = textureName != null ? Mathf.Min(GreyMiniLabel.CalcSize(new GUIContent(textureName)).x + 4f, row.width * 0.35f) : 0f;
                Rect textureRect = new Rect(row.xMax - textureWidth, row.y, textureWidth, row.height);
                Rect toggleRect = new Rect(row.x, row.y, row.width - textureWidth - 4f, row.height);
                if (hasTexture[subMeshIndex])
                {
                    string tooltip = "サブメッシュ " + subMeshIndex + ": " + materialName + "（テクスチャ " + textureName + "）";
                    EditorGUI.BeginChangeCheck();
                    bool enabled = EditorGUI.ToggleLeft(toggleRect, new GUIContent(subMeshIndex + "  " + materialName, tooltip), element.boolValue);
                    if (EditorGUI.EndChangeCheck())
                        element.boolValue = enabled;
                    EditorGUI.LabelField(textureRect, new GUIContent(textureName, "メインテクスチャ"), GreyMiniLabel);
                }
                else
                {
                    string tooltip = "メインテクスチャ（Texture2D）が無いため処理できません。";
                    EditorGUI.BeginDisabledGroup(true);
                    EditorGUI.ToggleLeft(toggleRect, new GUIContent(subMeshIndex + "  " + materialName, tooltip), false);
                    EditorGUI.EndDisabledGroup();
                    Rect iconRect = new Rect(row.xMax - 18f, row.y, 18f, row.height);
                    GUIContent warning = EditorGUIUtility.IconContent("console.warnicon.sml");
                    EditorGUI.LabelField(iconRect, new GUIContent(warning.image, tooltip));
                    if (element.boolValue)
                        element.boolValue = false;
                }
            }
        }

        private void SetAllSubMeshes(bool[] hasTexture, bool value)
        {
            for (int subMeshIndex = 0; subMeshIndex < hasTexture.Length && subMeshIndex < subMeshEnabled.arraySize; subMeshIndex++)
                subMeshEnabled.GetArrayElementAtIndex(subMeshIndex).boolValue = value && hasTexture[subMeshIndex];
        }

        private static GUIStyle greyMiniLabel;
        private static GUIStyle GreyMiniLabel
        {
            get
            {
                if (greyMiniLabel == null)
                {
                    greyMiniLabel = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight };
                    greyMiniLabel.normal.textColor = EditorGUIUtility.isProSkin ? new Color(0.58f, 0.58f, 0.58f) : new Color(0.42f, 0.42f, 0.42f);
                }
                return greyMiniLabel;
            }
        }

        // (1) 状態: プレイモード・アップロードで誰が置き換えるか（NDMF / 簡易適用 / しない）を 1 行で。判定は簡易適用（MeshDeletionPlayModeApplier）と同じ
        // 詳しい説明は「説明」の折りたたみに入れる
        private void DrawStatus(MeshDeletionForTexture component)
        {
            PlayModeApplier applier = MeshDeletionPlayModeApplier.ApplierFor(component);
            switch (applier)
            {
                case PlayModeApplier.Ndmf:
                    StatusLine("プレイモードとアップロードのときに NDMF が置き換えます（元のメッシュは変更しません）", MessageType.Info);
                    break;
                case PlayModeApplier.None:
                    EditorGUILayout.HelpBox("NDMF の Apply on Play がオフのため、プレイモードでは置き換えません（アップロード時は置き換えます）。", MessageType.Warning);
                    break;
                default:
#if NDMF
                    StatusLine("アバターの外にあるため、プレイモードでは簡易適用で置き換えます", MessageType.Info);
#else
                    EditorGUILayout.HelpBox("NDMF 1.8.0 以降が見つかりません。アップロード時には置き換わりません（プレイモードは簡易適用）。", MessageType.Warning);
#endif
                    break;
            }

            showExplanation = EditorGUILayout.Foldout(showExplanation, "説明", true);
            if (showExplanation)
            {
                EditorGUI.indentLevel++;
                MeshDeletionSettingsGUI.Help("テクスチャの透明な部分に合わせてメッシュを削ります。付けただけでは何も変わらず、" + MeshDeletionForTexture.Note);
                switch (applier)
                {
                    case PlayModeApplier.None:
                        MeshDeletionSettingsGUI.Help("プレイモードで置き換えるには Tools/NDM Framework/Apply on Play をオンにしてください。");
                        break;
                    case PlayModeApplier.Fallback:
#if NDMF
                        MeshDeletionSettingsGUI.Help("アバター（VRC Avatar Descriptor）の配下にないものは、プレイモードでは MeshDeletionTool の簡易適用が置き換えます" +
                                                     "（プレイモードを終えると元に戻ります）。アップロード時の置き換えはアバターの配下でのみ行われます。");
#else
                        MeshDeletionSettingsGUI.Help("NDMF（Non-Destructive Modular Framework）が無いため、プレイモードでは MeshDeletionTool の簡易適用が置き換えます。" +
                                                     "アップロード時にも置き換えるには、VCC / ALCOM で nadena.dev.ndmf をプロジェクトに追加してください（Modular Avatar を入れていれば一緒に入ります）。");
#endif
                        break;
                }
                MeshDeletionSettingsGUI.Help("プレビューは NDMF が作る表示用のコピーだけを変えるので、元のメッシュ・シーン・プレハブは変更されず、保存もされません。" +
                                             "シーン上の全ての MeshDeletionForTexture がまとめてプレビューされます。");
                EditorGUI.indentLevel--;
            }
        }

        private static void StatusLine(string text, MessageType type)
        {
            string icon = type == MessageType.Warning ? "console.warnicon.sml" : "console.infoicon.sml";
            GUIStyle style = new GUIStyle(EditorStyles.label) { wordWrap = true };
            EditorGUILayout.LabelField(new GUIContent(text, EditorGUIUtility.IconContent(icon).image), style);
        }

        // (2) プレビューのボタンと状態。NDMF のプレビュー（MeshDeletionPreviewFilter）を切り替える。シーン上の全ての MeshDeletionForTexture で共通
        // オンのときはこの Renderer のポリゴン数の変化を出す
        private void DrawPreviewControls(MeshDeletionForTexture component, Mesh mesh, Material[] materials)
        {
            const string previewTooltip = "プレイモードに入らずに、適用後の表示をシーンビューで確認します。NDMF が作る表示用のコピーだけを変えるので、" +
                                          "元のメッシュ・シーン・プレハブは変更されず、保存もされません。";
#if NDMF
            bool previewOn = MeshDeletionPreviewFilter.Toggle.IsEnabled.Value;
            EditorGUI.BeginDisabledGroup(EditorApplication.isPlayingOrWillChangePlaymode);
            bool requested = GUILayout.Toggle(previewOn, new GUIContent(previewOn ? "プレビュー: オン" : "プレビュー: オフ", previewTooltip), "Button", GUILayout.Height(24f));
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
                MeshDeletionSettingsGUI.Help("プレビューをオンにするとポリゴン数の変化が表示されます。");
            }
            else if (!Menu.GetChecked(MeshDeletionPreviewFilter.NdmfEnablePreviewsMenu))
            {
                EditorGUILayout.HelpBox("NDMF のプレビューが無効です（" + MeshDeletionPreviewFilter.NdmfEnablePreviewsMenu + "）。", MessageType.Warning);
                if (GUILayout.Button("NDMF のプレビューを有効にする"))
                    EditorApplication.ExecuteMenuItem(MeshDeletionPreviewFilter.NdmfEnablePreviewsMenu);
            }
            else
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                DrawPolygonStats(component, mesh, materials);
                EditorGUILayout.EndVertical();
            }
#else
            EditorGUI.BeginDisabledGroup(true);
            GUILayout.Toggle(false, new GUIContent("プレビュー: オフ", previewTooltip), "Button", GUILayout.Height(24f));
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.LabelField("プレビューには NDMF 1.8.0 以降が必要です。", EditorStyles.miniLabel);
#endif
        }

        // プレビュー中の結果のポリゴン数（三角形数）の変化。MeshDeletionPreviewFilter が残した、この Renderer の結果を表示する
        // 結果がまだ無い、または現在の設定と違う設定の結果なら「計算中…」を出す（結果が届くと MeshDeletionPreviewStats.Changed で再描画する）
        private void DrawPolygonStats(MeshDeletionForTexture component, Mesh mesh, Material[] materials)
        {
            MeshDeletionPreviewStats.Record record = MeshDeletionPreviewStats.Get(component.TargetRenderer);
            string currentKey = mesh != null ? MeshDeletionRunner.OptionsFromComponent(component, mesh.subMeshCount).SettingsKey() : null;
            if (record == null)
            {
                EditorGUILayout.LabelField("計算中…", EditorStyles.miniBoldLabel);
                return;
            }
            if (record.Problem != null)
            {
                EditorGUILayout.HelpBox(record.Problem, MessageType.Warning);
                return;
            }
            bool stale = currentKey != null && record.SettingsKey != currentKey;
            PolygonStats stats = record.Stats;
            if (stats == null)
                return;

            EditorGUI.BeginDisabledGroup(stale);
            GUIStyle headline = new GUIStyle(EditorStyles.boldLabel) { fontSize = EditorStyles.boldLabel.fontSize + 1, wordWrap = true };
            headline.normal.textColor = LevelColor(PolygonStatsFormat.Level(stats.OriginalTriangles, stats.GeneratedTriangles));
            EditorGUILayout.LabelField(new GUIContent(PolygonStatsFormat.TriangleLine(stats),
                "Unity と VRChat はポリゴン数を三角形の数で数えます。緑: +20% 以下（または減少）、黄: +50% 以下、赤: それより多い"), headline);
            EditorGUILayout.LabelField(PolygonStatsFormat.VertexLine(stats), EditorStyles.miniLabel);
            EditorGUILayout.LabelField(PolygonStatsFormat.TimeLine(stats) + "　" + record.UpdatedAt.ToString("HH:mm:ss") + " 更新", EditorStyles.miniLabel);
            EditorGUI.EndDisabledGroup();
            if (stale)
                EditorGUILayout.LabelField("計算中…（設定を変更しました）", EditorStyles.miniBoldLabel);

            showSubMeshStats = EditorGUILayout.Foldout(showSubMeshStats, "サブメッシュ別", true);
            if (showSubMeshStats)
            {
                EditorGUI.indentLevel++;
                for (int subMeshIndex = 0; subMeshIndex < stats.SubMeshTrianglesBefore.Length; subMeshIndex++)
                {
                    Material material = materials != null && subMeshIndex < materials.Length ? materials[subMeshIndex] : null;
                    string name = subMeshIndex + ": " + (material != null ? PolygonStatsFormat.MaterialDisplayName(material.name) : "（マテリアルなし）");
                    Rect row = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight * 0.9f);
                    Rect percentRect = new Rect(row.xMax - 56f, row.y, 56f, row.height);
                    Rect countsRect = new Rect(percentRect.x - 112f, row.y, 108f, row.height);
                    Rect nameRect = new Rect(row.x, row.y, Mathf.Max(0f, countsRect.x - row.x - 4f), row.height);
                    EditorGUI.LabelField(nameRect, new GUIContent(name, name), EditorStyles.miniLabel);
                    EditorGUI.LabelField(countsRect, PolygonStatsFormat.SubMeshCounts(stats, subMeshIndex), RightMiniLabel);
                    GUIStyle percentStyle = new GUIStyle(RightMiniLabel);
                    if (stats.SubMeshProcessed[subMeshIndex])
                        percentStyle.normal.textColor = LevelColor(PolygonStatsFormat.Level(stats.SubMeshTrianglesBefore[subMeshIndex], stats.SubMeshTrianglesAfter[subMeshIndex]));
                    EditorGUI.LabelField(percentRect, PolygonStatsFormat.SubMeshPercent(stats, subMeshIndex), percentStyle);
                }
                EditorGUI.indentLevel--;
            }
        }

        private static GUIStyle rightMiniLabel;
        private static GUIStyle RightMiniLabel => rightMiniLabel ?? (rightMiniLabel = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight });

        // 増減の段階の色（ダーク・ライトのどちらのスキンでも読める控えめな色）
        private static Color LevelColor(PolygonChangeLevel level)
        {
            bool dark = EditorGUIUtility.isProSkin;
            switch (level)
            {
                case PolygonChangeLevel.Large:
                    return dark ? new Color(0.96f, 0.45f, 0.40f) : new Color(0.70f, 0.13f, 0.10f);
                case PolygonChangeLevel.Medium:
                    return dark ? new Color(0.93f, 0.78f, 0.30f) : new Color(0.55f, 0.40f, 0.00f);
                default:
                    return dark ? new Color(0.45f, 0.82f, 0.48f) : new Color(0.10f, 0.45f, 0.16f);
            }
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
