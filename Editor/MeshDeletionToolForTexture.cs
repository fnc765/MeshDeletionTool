using UnityEngine;
using UnityEditor;
using System;
using System.Collections.Generic;
using System.Linq;
using Unity.VisualScripting;

namespace MeshDeletionTool
{
    public class MeshDeletionToolForTexture : MeshDeletionToolUtils
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

        // 1 テクセルの大きさ（mm）の表示用キャッシュ
        private Mesh texelSizeCacheMesh;
        private Vector2Int[] texelSizeCacheTextureSizes;
        private float[] texelSizeCacheMillimeters;

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
                string texelSizeHint = GetTexelSizeHint(GetOriginalMesh(targetRenderer), GetOriginalMaterials(targetRenderer));
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
                Mesh originalMesh = GetOriginalMesh(targetRenderer);
                Material[] originalMaterials = GetOriginalMaterials(targetRenderer);

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

        // 「1 テクセル ≈ 0.6〜0.9 mm」のような表示文字列を返す（処理対象のサブメッシュの範囲。対象が無ければテクスチャを持つ全サブメッシュ）
        private string GetTexelSizeHint(Mesh mesh, Material[] materials)
        {
            if (mesh == null || materials == null)
                return null;
            Vector2Int[] textureSizes = GetTextureSizes(mesh.subMeshCount, materials);
            if (texelSizeCacheMesh != mesh || texelSizeCacheTextureSizes == null || !TextureSizesEqual(texelSizeCacheTextureSizes, textureSizes))
            {
                texelSizeCacheMesh = mesh;
                texelSizeCacheTextureSizes = textureSizes;
                texelSizeCacheMillimeters = ComputeTexelSizeMillimeters(mesh, textureSizes);
            }
            float min = float.MaxValue, max = 0f;
            for (int pass = 0; pass < 2 && max == 0f; pass++)
            {
                for (int subMeshIndex = 0; subMeshIndex < texelSizeCacheMillimeters.Length; subMeshIndex++)
                {
                    bool isTarget = subMeshVisibility.TryGetValue(subMeshIndex, out bool visible) && visible;
                    float size = texelSizeCacheMillimeters[subMeshIndex];
                    if ((pass == 0 && !isTarget) || size <= 0f)
                        continue;
                    min = Mathf.Min(min, size);
                    max = Mathf.Max(max, size);
                }
            }
            if (max == 0f)
                return null;
            string range = min.ToString("0.0") == max.ToString("0.0") ? min.ToString("0.0") : min.ToString("0.0") + "〜" + max.ToString("0.0");
            return "1 テクセル ≈ " + range + " mm（対象のテクスチャ解像度とメッシュから概算）";
        }

        // サブメッシュ毎のメインテクスチャの解像度（テクスチャが無ければ 0）
        private static Vector2Int[] GetTextureSizes(int subMeshCount, Material[] materials)
        {
            Vector2Int[] sizes = new Vector2Int[subMeshCount];
            for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
            {
                Texture texture = subMeshIndex < materials.Length && materials[subMeshIndex] != null ? materials[subMeshIndex].mainTexture : null;
                if (texture != null)
                    sizes[subMeshIndex] = new Vector2Int(texture.width, texture.height);
            }
            return sizes;
        }

        private static bool TextureSizesEqual(Vector2Int[] a, Vector2Int[] b)
        {
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                    return false;
            }
            return true;
        }

        // サブメッシュ毎に 1 テクセルあたりの大きさ（mm）を求める: sqrt(3D 面積の合計 / テクセル空間での UV 面積の合計)
        private static float[] ComputeTexelSizeMillimeters(Mesh mesh, Vector2Int[] textureSizes)
        {
            Vector3[] vertices = mesh.vertices;
            Vector2[] uvs = mesh.uv;
            float[] result = new float[mesh.subMeshCount];
            if (uvs.Length != vertices.Length)
                return result;
            for (int subMeshIndex = 0; subMeshIndex < mesh.subMeshCount; subMeshIndex++)
            {
                Vector2Int size = textureSizes[subMeshIndex];
                if (size.x <= 1 || size.y <= 1)
                    continue;
                Vector2 scale = new Vector2(size.x - 1, size.y - 1);
                int[] triangles = mesh.GetTriangles(subMeshIndex);
                double area3D = 0.0, areaTexel = 0.0;
                for (int i = 0; i + 2 < triangles.Length; i += 3)
                {
                    Vector3 a = vertices[triangles[i]], b = vertices[triangles[i + 1]], c = vertices[triangles[i + 2]];
                    area3D += 0.5 * Vector3.Cross(b - a, c - a).magnitude;
                    Vector2 ta = Vector2.Scale(uvs[triangles[i]], scale), tb = Vector2.Scale(uvs[triangles[i + 1]], scale), tc = Vector2.Scale(uvs[triangles[i + 2]], scale);
                    areaTexel += 0.5 * Mathf.Abs((tb.x - ta.x) * (tc.y - ta.y) - (tb.y - ta.y) * (tc.x - ta.x));
                }
                if (areaTexel > 0.0)
                    result[subMeshIndex] = (float)Math.Sqrt(area3D / areaTexel) * 1000f;
            }
            return result;
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
        private void DeleteMeshesFromTexture()
        {
            // 入力の検証
            if (!ValidateInputs(targetRenderer))
                return;

            // 元のメッシュとマテリアルを取得
            Mesh originalMesh = GetOriginalMesh(targetRenderer);
            Material[] originalMaterials = GetOriginalMaterials(targetRenderer);
            if (originalMesh == null)
                return;
            WarnIfBonesPerVertexExceedFour(originalMesh);
            Mesh sourceMesh = originalMesh;   // 細分化前の元のメッシュ

            // 境界の細分化（削除処理の前に、アルファ境界付近の三角形を細分化したメッシュに置き換える）
            List<int[]> refinedTriangleParents = null;   // 細分化後の三角形番号 → 元の三角形番号
            if (refineBoundary && refineMaxDepth > 0)
            {
                originalMesh = RefineMeshAroundAlphaBoundary(originalMesh, originalMaterials, out refinedTriangleParents);
            }

            // テクスチャのアルファ値とメッシュの頂点属性を一度だけ読み出し、以後の処理は Unity に依存しない配列に対して行う
            AlphaMask[] subMeshMasks = CollectAlphaMasks(originalMesh.subMeshCount, originalMaterials);
            bool[] targetSubMeshes = GetTargetSubMeshes(originalMesh.subMeshCount);
            MeshArrays originalArrays = MeshArraysUnityAdapter.FromMesh(originalMesh);
            AlphaMeshCutter cutter = new AlphaMeshCutter { AlphaThreshold = alphaThreshold };

            // 削除すべき頂点のインデックスを取得
            List<int> removeVerticesIndexs = cutter.GetVerticesToRemove(originalArrays, subMeshMasks);
            // 新しいメッシュを作成
            MeshArrays cutArrays = cutter.Cut(originalArrays, subMeshMasks, targetSubMeshes, removeVerticesIndexs, out List<int[]> sourceTriangleIndices);
            Mesh newMesh = MeshArraysUnityAdapter.ToMesh(cutArrays);
            // 出力三角形 → 元の三角形の対応を保持する
            lastOutputTriangleParents = ComposeTriangleParents(sourceTriangleIndices, refinedTriangleParents);

            // 切断後の再結合（細分化で増えた三角形を元の三角形ごとに結合し直す）
            if (refinedTriangleParents != null && mergeCutPolygons)
            {
                // 出力メッシュの先頭には元のメッシュの頂点（削除されなかったもの）が並ぶ。これらは再結合で取り除かない
                int keptOriginalVertexCount = sourceMesh.vertexCount - removeVerticesIndexs.Count(index => index < sourceMesh.vertexCount);
                newMesh = MergeCutPolygons(newMesh, sourceMesh, originalMaterials, keptOriginalVertexCount);
            }
            // 新しいメッシュを保存
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

        // メッシュに使用されているテクスチャ読み取りの有効化と、サブメッシュ毎のテクスチャのアルファ値の読み出し
        // （Texture2D.GetPixel は呼び出し毎にネイティブ呼び出しになるため、GetPixels32 で一度だけ読む。同じテクスチャは一度だけ読む）
        private AlphaMask[] CollectAlphaMasks(int subMeshCount, Material[] originalMaterials)
        {
            AlphaMask[] subMeshMasks = new AlphaMask[subMeshCount];
            Dictionary<Texture2D, AlphaMask> maskCache = new Dictionary<Texture2D, AlphaMask>();
            for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
            {
                Material material = originalMaterials[subMeshIndex];
                Texture2D texture = material.mainTexture as Texture2D;
                MakeTextureReadable(texture);   //テクスチャ読み取り有効化
                if (texture == null)
                    continue;
                if (!maskCache.TryGetValue(texture, out AlphaMask mask))
                {
                    mask = MeshArraysUnityAdapter.FromTexture(texture);
                    maskCache[texture] = mask;
                }
                subMeshMasks[subMeshIndex] = mask;
            }
            return subMeshMasks;
        }

        // 入力を検証するメソッド
        private bool ValidateInputs(Renderer targetRenderer)
        {
            if (targetRenderer == null)
            {
                Debug.LogError("対象オブジェクトが選択されていません！");
                return false;
            }
            return true;
        }

        // 切断後のメッシュを元の三角形ごとに再結合したメッシュを返すメソッド
        private Mesh MergeCutPolygons(Mesh cutMesh, Mesh sourceMesh, Material[] originalMaterials, int keptOriginalVertexCount)
        {
            // 切り口の間引きは処理対象のサブメッシュのテクスチャ解像度に対するテクセル単位で行う
            Vector2Int[] textureSizes = GetTextureSizes(sourceMesh.subMeshCount, originalMaterials);
            for (int subMeshIndex = 0; subMeshIndex < textureSizes.Length; subMeshIndex++)
            {
                if (!subMeshVisibility.TryGetValue(subMeshIndex, out bool isTarget) || !isTarget)
                    textureSizes[subMeshIndex] = Vector2Int.zero;
            }
            CutPolygonMerger merger = new CutPolygonMerger { SimplifyToleranceTexels = simplifyToleranceTexels };
            Mesh mergedMesh = merger.Merge(cutMesh, lastOutputTriangleParents, sourceMesh, keptOriginalVertexCount, textureSizes);
            lastOutputTriangleParents = merger.ParentTriangleIndexPerSubMesh;
            Debug.Log("切断後の再結合: 三角形 " + merger.TriangleCountBefore + " → " + merger.TriangleCountAfter +
                      ", 頂点 " + merger.VertexCountBefore + " → " + merger.VertexCountAfter +
                      " (一直線上の頂点の削除 " + merger.RemovedFlatVertexCount + ", 切り口の間引き " + merger.RemovedChainVertexCount +
                      ", 再結合できなかった三角形 " + merger.FallbackCount + ": 穴 " + merger.FallbackHoleCount +
                      ", 非多様体 " + merger.FallbackNonManifoldCount + ", 分割失敗 " + merger.FallbackTriangulationCount + ")");
            return mergedMesh;
        }

        // 出力三角形 → 入力三角形の対応と、入力（細分化後）三角形 → 元の三角形の対応を合成する（細分化していなければそのまま）
        private static List<int[]> ComposeTriangleParents(List<int[]> sourceTriangleIndices, List<int[]> refinedTriangleParents)
        {
            if (refinedTriangleParents == null)
            {
                return sourceTriangleIndices;
            }
            List<int[]> parents = new List<int[]>(sourceTriangleIndices.Count);
            for (int subMeshIndex = 0; subMeshIndex < sourceTriangleIndices.Count; subMeshIndex++)
            {
                int[] sources = sourceTriangleIndices[subMeshIndex];
                int[] composed = new int[sources.Length];
                for (int i = 0; i < sources.Length; i++)
                {
                    composed[i] = refinedTriangleParents[subMeshIndex][sources[i]];
                }
                parents.Add(composed);
            }
            return parents;
        }

        // アルファ境界付近の三角形を細分化したメッシュを返すメソッド（処理対象のサブメッシュのみ判定する）
        // parentTriangleIndexPerSubMesh には細分化後の三角形番号 → 元の三角形番号の対応を返す
        private Mesh RefineMeshAroundAlphaBoundary(Mesh originalMesh, Material[] originalMaterials, out List<int[]> parentTriangleIndexPerSubMesh)
        {
            // 処理対象サブメッシュのテクスチャを集める（対象外は null）
            Texture2D[] subMeshTextures = new Texture2D[originalMesh.subMeshCount];
            for (int subMeshIndex = 0; subMeshIndex < originalMesh.subMeshCount; subMeshIndex++)
            {
                if (!subMeshVisibility.TryGetValue(subMeshIndex, out bool isTarget) || !isTarget)
                    continue;
                Material material = originalMaterials[subMeshIndex];
                Texture2D texture = material != null ? material.mainTexture as Texture2D : null;
                if (texture == null)
                    continue;
                MakeTextureReadable(texture);   //テクスチャ読み取り有効化
                subMeshTextures[subMeshIndex] = texture;
            }

            AlphaBoundaryRefiner refiner = new AlphaBoundaryRefiner
            {
                MaxDepth = refineMaxDepth,
                RefinePartiallyCutTriangles = refinePartiallyCutTriangles,
                ChordToleranceTexels = refineChordToleranceTexels
            };
            Mesh refinedMesh = refiner.Refine(originalMesh, subMeshTextures, alphaThreshold);
            parentTriangleIndexPerSubMesh = refiner.ParentTriangleIndexPerSubMesh;
            Debug.Log("境界の細分化: 三角形 " + originalMesh.triangles.Length / 3 + " → " + refinedMesh.triangles.Length / 3 +
                      " (深さ毎の三角形数: " + string.Join(", ", refiner.TriangleCountPerDepth) + ")");
            return refinedMesh;
        }

        // UV座標が示すテクスチャのピクセルが境界エッジかどうかを判定する関数（細分化処理と共用）
        internal static bool IsBoundaryEdge(Texture2D texture, Vector2 uv1, Vector2 uv2, float alphaThreshold)
        {
            // UV座標をピクセル座標に変換
            Vector2 pixelUV1 = new Vector2(uv1.x * (texture.width - 1), uv1.y * (texture.height - 1));
            Vector2 pixelUV2 = new Vector2(uv2.x * (texture.width - 1), uv2.y * (texture.height - 1));

            // 両端点のピクセルの色を取得
            Color color1 = texture.GetPixel((int)pixelUV1.x, (int)pixelUV1.y);
            Color color2 = texture.GetPixel((int)pixelUV2.x, (int)pixelUV2.y);

            // 片方のピクセルが透明で、もう片方が透明でない場合は境界エッジとする
            return (color1.a < alphaThreshold && color2.a > alphaThreshold) || (color1.a > alphaThreshold && color2.a < alphaThreshold);
        }

        // テクスチャのアルファ値に基づき、エッジ上の境界点のUV座標の補完用重みを求める（細分化処理と共用）
        internal static float FindAlphaBoundary(Texture2D texture, Vector2 uv1, Vector2 uv2, float alphaThreshold)
        {
            // UV座標をピクセル座標に変換し、開始点の色を取得
            Vector2 pixelUV1 = new Vector2(uv1.x * (texture.width - 1), uv1.y * (texture.height - 1));
            Color color1 = texture.GetPixel((int)pixelUV1.x, (int)pixelUV1.y);

            float tMin = 0.0f;
            float tMax = 1.0f;
            
            // 二分探索を用いて境界点を探す
            for (int i = 0; i < 10; i++)
            {
                float t = (tMin + tMax) / 2.0f;  // 中間点の係数
                // UV座標と頂点座標の中間点を計算
                Vector2 midUV = Vector2.Lerp(uv1, uv2, t);
                Vector2 midPixelUV = new Vector2(midUV.x * (texture.width - 1), midUV.y * (texture.height - 1));
                Color midColor = texture.GetPixel((int)midPixelUV.x, (int)midPixelUV.y);

                // 境界条件に応じて探索範囲を狭める
                if ((color1.a < alphaThreshold && midColor.a > alphaThreshold) || (color1.a > alphaThreshold && midColor.a < alphaThreshold))
                {
                    tMax = t; // 境界があると考えられる範囲を左側に絞り込む
                }
                else
                {
                    tMin = t; // 境界があると考えられる範囲を右側に絞り込む
                    color1 = midColor;
                }
            }

            // 最終的な境界点のUV座標と頂点座標を計算して返す
            float weight = (tMin + tMax) / 2.0f;
            return weight;
        }
    }
}
