using UnityEngine;
using UnityEditor;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MeshDeletionTool
{
    public class MeshDeletionToolForTexture : EditorWindow
    {
        // 対象オブジェクトのRendererを保持するための内部フィールド
        internal Renderer targetRenderer;

        // アルファ値がこの値より小さいメッシュは削除する
        private float alphaThreshold = 0.5F;

        // サブメッシュの表示フラグ
        private Dictionary<int, bool> subMeshVisibility = new Dictionary<int, bool>();

        // 境界の細分化: 削除処理の前にアルファ境界付近の三角形を細分化する（無効にすると従来の動作）
        private bool refineBoundary = true;

        // 境界の精度（テクセル）: 切り口がテクスチャの境界からずれてよい量。小さいほど正確だがポリゴンが増える
        // 細分化の許容誤差（この 2 倍）と切り口の間引きの許容誤差（この値）を決める
        private float boundaryPrecisionTexels = 1.0f;

        // 細分化の最大深さ（詳細設定）
        private int refineMaxDepth = 3;

        // 一部の頂点が透明な三角形の細分化（修正2）と、その際に許容する失われる不透明テクセル数（境界の精度から決まる）
        private bool refinePartiallyCutTriangles = true;
        private int refineChordToleranceTexels = 2;

        // 切断後の再結合: 細分化で増えた三角形を元の三角形ごとに結合し直す（詳細設定、細分化が有効なときのみ）
        private bool mergeCutPolygons = true;

        // 切り口の間引きの許容誤差（テクセル、境界の精度から決まる）
        private float simplifyToleranceTexels = 1.0f;

        // 詳細設定の折りたたみ
        private bool showAdvancedSettings = false;

        // テスト・計測用: null でなければ選択に関わらずこのバックエンドで処理する（呼び出し側が Dispose する）
        internal IAlphaStageBackend backendOverride;

        // 1 テクセルの大きさ（mm）の表示用の概算
        private readonly TexelSizeEstimator texelSizeEstimator = new TexelSizeEstimator();

        // 直前の実行結果: サブメッシュ毎の、出力メッシュの三角形番号 → 対象オブジェクトの元のメッシュの三角形番号（テスト・診断用）
        internal List<int[]> lastOutputTriangleParents;

        // メニューアイテムからツールを初期化してウィンドウを表示するメソッド
        [MenuItem("Tools/MeshDeletionToolForTexture")]
        private static void Init()
        {
            // ウィンドウを作成し表示する
            MeshDeletionToolForTexture window = (MeshDeletionToolForTexture)EditorWindow.GetWindow(typeof(MeshDeletionToolForTexture));
            window.titleContent = new GUIContent("MeshDeletionToolForTexture");
            window.Show();
        }

        // GUIを描画するためのメソッド
        private void OnGUI()
        {
            // ラベルを表示
            GUILayout.Label("①オブジェクトを選択", EditorStyles.boldLabel);

            // 対象オブジェクトを選択するためのフィールド
            targetRenderer = EditorGUILayout.ObjectField("対象オブジェクト", targetRenderer, typeof(Renderer), true) as Renderer;

            // 対象オブジェクトの Mesh とマテリアルは 1 回の描画で一度だけ取得する（無いときのエラーログを重ねて出さない）
            Mesh originalMesh = targetRenderer != null ? MeshDeletionRunner.GetOriginalMesh(targetRenderer) : null;
            Material[] originalMaterials = targetRenderer != null ? MeshDeletionRunner.GetOriginalMaterials(targetRenderer) : null;

            // アルファ閾値を指定するスライダーを追加
            GUILayout.Label("\n②アルファ閾値を設定", EditorStyles.boldLabel);
            alphaThreshold = EditorGUILayout.Slider("アルファ閾値", alphaThreshold, 0f, 1f);

            // 境界の細分化の設定
            refineBoundary = EditorGUILayout.Toggle("境界の細分化", refineBoundary);
            EditorGUI.BeginDisabledGroup(!refineBoundary);
            boundaryPrecisionTexels = EditorGUILayout.Slider("境界の精度（テクセル）", boundaryPrecisionTexels, 0.5f, 4f);
            refineChordToleranceTexels = Mathf.CeilToInt(2f * boundaryPrecisionTexels);
            simplifyToleranceTexels = boundaryPrecisionTexels;
            if (targetRenderer != null)
            {
                string texelSizeHint = texelSizeEstimator.GetHint(originalMesh, originalMaterials, subMeshIndex => subMeshVisibility.TryGetValue(subMeshIndex, out bool visible) && visible);
                if (texelSizeHint != null)
                {
                    EditorGUILayout.LabelField(" ", texelSizeHint, EditorStyles.miniLabel);
                }
            }
            showAdvancedSettings = EditorGUILayout.Foldout(showAdvancedSettings, "詳細設定");
            if (showAdvancedSettings)
            {
                EditorGUI.indentLevel++;
                refineMaxDepth = EditorGUILayout.IntSlider("細分化の最大深さ", refineMaxDepth, 0, 5);
                mergeCutPolygons = EditorGUILayout.Toggle("切断後の再結合", mergeCutPolygons);
                EditorGUI.indentLevel--;
            }
            EditorGUI.EndDisabledGroup();

            // サブメッシュを選択するリストを表示
            GUILayout.Label("\n③サブメッシュ一覧から処理対象を選択", EditorStyles.boldLabel);
            GUILayout.Label("(チェックで処理対象となる)", EditorStyles.boldLabel);

            if (targetRenderer != null)
            {
                if (originalMesh != null)
                {
                    for (int i = 0; i < originalMesh.subMeshCount; i++)
                    {
                        if (!subMeshVisibility.ContainsKey(i))
                        {
                            subMeshVisibility[i] = false; // デフォルトは処理対象外
                        }
                        
                        GUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField("サブメッシュ " + i, GUILayout.Width(80));
                        subMeshVisibility[i] = EditorGUILayout.Toggle(subMeshVisibility[i], GUILayout.Width(20));
                        // テクスチャの名前を表示
                        string textureName = GetTextureNameForSubMesh(originalMaterials, i);
                        if (GUILayout.Button("テクスチャ参照:" + textureName, new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleLeft }))
                        {
                            ShowTextureForSubMesh(originalMaterials, i);
                        }
                        
                        // GUILayout.Label("テクスチャ名:" + textureName, GUILayout.Width(200));
                        GUILayout.EndHorizontal();
                    }
                }
                else
                {
                    GUILayout.Label("選択されたオブジェクトに有効なメッシュがありません。");
                    Debug.LogWarning("選択されたオブジェクトに有効なメッシュがありません。");
                }
            }
            GUILayout.Label("\n④処理実行", EditorStyles.boldLabel);
            // ボタンをクリックしたらメッシュ削除処理を実行
            if (GUILayout.Button("テクスチャ透明部分のメッシュを削除"))
            {
                DeleteMeshesFromTexture();
            }
        }

        // サブメッシュのテクスチャを表示するメソッド
        private void ShowTextureForSubMesh(Material[] materials, int subMeshIndex)
        {
            if (materials.Length > subMeshIndex)
            {
                Material mat = materials[subMeshIndex];
                if (mat != null && mat.mainTexture != null)
                {
                    EditorGUIUtility.PingObject(mat.mainTexture);
                }
            }
        }

        // サブメッシュのテクスチャ名を取得するメソッド
        private string GetTextureNameForSubMesh(Material[] materials, int subMeshIndex)
        {
            if (materials.Length > subMeshIndex)
            {
                Material mat = materials[subMeshIndex];
                if (mat != null && mat.mainTexture != null)
                {
                    return mat.mainTexture.name;
                }
            }
            return "テクスチャなし";
        }

        // テクスチャの透明部分に基づいてメッシュを削除するメソッド
        // 設定の組み立てと結果の保存だけをここで行い、読み出しと処理は MeshDeletionRunner（処理本体は Unity に依存しない AlphaMeshDeletionPipeline）が行う
        private void DeleteMeshesFromTexture()
        {
            // 入力の検証
            if (!ValidateInputs(targetRenderer))
                return;
            Mesh originalMesh = MeshDeletionRunner.GetOriginalMesh(targetRenderer);
            if (originalMesh == null)
                return;
            bool[] targetSubMeshes = GetTargetSubMeshes(originalMesh.subMeshCount);

            // 要素毎の判定の実行先（細分化が有効で GPU が使えるときは GPU、それ以外は CPU。選んだ理由は 1 行のログに出す）
            string backendNote = null;
            IAlphaStageBackend backend = backendOverride ?? MeshDeletionRunner.CreateBackend(refineBoundary && refineMaxDepth > 0, out backendNote);
            if (backendOverride == null)
                Debug.Log(backendNote);

            // 細分化 → 削除する頂点の判定 → 切断 → 再結合
            AlphaMeshDeletionPipeline pipeline = new AlphaMeshDeletionPipeline
            {
                AlphaThreshold = alphaThreshold,
                RefineBoundary = refineBoundary,
                RefineMaxDepth = refineMaxDepth,
                RefinePartiallyCutTriangles = refinePartiallyCutTriangles,
                RefineChordToleranceTexels = refineChordToleranceTexels,
                MergeCutPolygons = mergeCutPolygons,
                SimplifyToleranceTexels = simplifyToleranceTexels,
                Backend = backend,
                MeasureTime = true,
                Log = Debug.Log
            };
            MeshArrays newArrays;
            try
            {
                newArrays = MeshDeletionRunner.Execute(targetRenderer, targetSubMeshes, pipeline);
                Debug.Log(MeshDeletionRunner.DescribeResult(originalMesh.name, MeshArraysUnityAdapter.FromMesh(originalMesh), newArrays, pipeline));
            }
            catch (ArgumentException e)
            {
                // 入力が処理できない形（三角形でないサブメッシュ、UV 無し）のときは 1 行のエラーで中止する
                Debug.LogError(e.Message);
                return;
            }
            finally
            {
                if (backendOverride == null)
                    backend.Dispose();
            }
            lastOutputTriangleParents = pipeline.OutputTriangleParents;

            // 新しいメッシュを作成して保存（メッシュ名は元のメッシュ名 + "_deleted"。アセットのパスは従来通り固定）
            Mesh newMesh = MeshArraysUnityAdapter.ToMesh(newArrays);
            newMesh.name = originalMesh.name + "_deleted";
            SaveNewMesh(newMesh);
        }

        // サブメッシュ毎の処理対象フラグ（チェックの無いサブメッシュは対象外）
        private bool[] GetTargetSubMeshes(int subMeshCount)
        {
            bool[] targets = new bool[subMeshCount];
            for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
            {
                targets[subMeshIndex] = subMeshVisibility.TryGetValue(subMeshIndex, out bool isTarget) && isTarget;
            }
            return targets;
        }

        // 出力メッシュを固定のパスに保存する（前回の出力は上書きされる）
        private static void SaveNewMesh(Mesh newMesh)
        {
            AssetDatabase.CreateAsset(newMesh, "Assets/NewMesh.asset");
            AssetDatabase.SaveAssets();
        }

        // 入力を検証するメソッド
        private static bool ValidateInputs(Renderer targetRenderer)
        {
            if (targetRenderer == null)
            {
                Debug.LogError("対象オブジェクトが選択されていません！");
                return false;
            }
            return true;
        }
    }
}
