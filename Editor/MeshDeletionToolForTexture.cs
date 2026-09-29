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
            Mesh sourceMesh = originalMesh;   // 細分化前の元のメッシュ

            // 境界の細分化（削除処理の前に、アルファ境界付近の三角形を細分化したメッシュに置き換える）
            List<int[]> refinedTriangleParents = null;   // 細分化後の三角形番号 → 元の三角形番号
            if (refineBoundary && refineMaxDepth > 0)
            {
                originalMesh = RefineMeshAroundAlphaBoundary(originalMesh, originalMaterials, out refinedTriangleParents);
            }

            // 削除すべき頂点のインデックスを取得
            List<int> removeVerticesIndexs = GetVerticesToRemoveFromTexture(originalMesh, originalMaterials);
            // 新しいメッシュを作成
            Mesh newMesh = CreateMeshAfterVertexModification(originalMesh, originalMaterials, removeVerticesIndexs, out List<int[]> sourceTriangleIndices);
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

        // テクスチャに基づいて削除すべき頂点のインデックスを取得するメソッド
        private List<int> GetVerticesToRemoveFromTexture(Mesh originalMesh, Material[] originalMaterials)
        {
            List<int> removeVerticesIndexs = new List<int>();

            int subMeshCount = originalMesh.subMeshCount;
            List<HashSet<int>> subMeshTrianglesList = new List<HashSet<int>>();

            // メッシュに使用されているテクスチャ読み取りの有効化
            for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
            {
                Material material = originalMaterials[subMeshIndex];
                Texture2D texture = material.mainTexture as Texture2D;
                MakeTextureReadable(texture);   //テクスチャ読み取り有効化
            }
            
            // 各サブメッシュの三角形リストを取得
            for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
            {
                int[] triangles = originalMesh.GetTriangles(subMeshIndex);
                HashSet<int> triangleSet = new HashSet<int>(triangles);
                subMeshTrianglesList.Add(triangleSet);
            }

            // 各頂点を確認し、削除対象かどうかを判定
            for (int vertexIndex = 0; vertexIndex < originalMesh.vertices.Length; vertexIndex++)
            {
                bool vertexShouldBeRemoved = false;

                for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
                {
                    if (subMeshTrianglesList[subMeshIndex].Contains(vertexIndex))
                    {
                        Material material = originalMaterials[subMeshIndex];
                        Vector2 uv = originalMesh.uv[vertexIndex];
                        Texture2D texture = material.mainTexture as Texture2D;
                        if (texture != null)
                        {
                            Vector2 pixelUV = new Vector2(uv.x * (texture.width - 1), uv.y * (texture.height - 1));
                            Color color = texture.GetPixel((int)pixelUV.x, (int)pixelUV.y);

                            // ピクセルのアルファ値がalphaThresholdより小さいなら頂点を削除対象とする
                            if (color.a < alphaThreshold)
                            {
                                vertexShouldBeRemoved = true;
                                break;
                            }
                        }
                    }
                }

                // 削除対象ならリストに追加
                if (vertexShouldBeRemoved)
                {
                    removeVerticesIndexs.Add(vertexIndex);
                }
            }

            // 削除対象のインデックスを降順にソート
            removeVerticesIndexs.Sort((a, b) => b - a);
            return removeVerticesIndexs;
        }

        // 頂点削除と頂点追加を行いテクスチャに合わせたメッシュ形状に編集する
        // sourceTriangleIndices にはサブメッシュ毎の、出力三角形番号 → originalMesh の三角形番号を返す
        private Mesh CreateMeshAfterVertexModification(Mesh originalMesh, Material[] originalMaterials, List<int> removeVerticesIndexs,
                                                       out List<int[]> sourceTriangleIndices)
        {
            MeshData newMeshData = new MeshData();
            sourceTriangleIndices = new List<int[]>(originalMesh.subMeshCount);

            // 新規追加頂点の重複を避けるためにマッピング（辺の頂点インデックスの昇順ペアをキーとし、辺を共有する三角形で同じ頂点を使う）
            Dictionary<(int, int), int> edgeVertexIndexMap = new Dictionary<(int, int), int>();

            // 新規追加頂点を補完するための２点頂点インデックスと重みを、新規頂点インデックスをキーとして保持
            Dictionary<int, (int, int, float)> vertexInterpolation = new Dictionary<int, (int, int, float)>();

            Mesh newMesh = new Mesh();


            // 処理されないサブメッシュに含まれる頂点インデックスを収集
            HashSet<int> nonRemoveVerticesIndexs = new HashSet<int>();
            for (int subMeshIndex = 0; subMeshIndex < originalMesh.subMeshCount; subMeshIndex++)
            {
                if (subMeshVisibility[subMeshIndex] == false)
                {
                    int[] triangles = originalMesh.GetTriangles(subMeshIndex);
                    foreach (int index in triangles)
                    {
                        nonRemoveVerticesIndexs.Add(index);
                    }
                }
            }

            // removeVerticesIndexsからnonRemoveVerticesIndexsに含まれるインデックスを削除
            removeVerticesIndexs.RemoveAll(index => nonRemoveVerticesIndexs.Contains(index));

            // 不要頂点を削除する
            for (int index = 0; index < originalMesh.vertexCount; index++)
            {
                if (removeVerticesIndexs.Contains(index))
                    continue;
                newMeshData.AddElementFromMesh(originalMesh, index);
            }
            
            // インデックスマッピングの作成
            Dictionary<int, int> oldToNewIndexMap = CreateIndexMap(originalMesh, removeVerticesIndexs);


            newMesh.subMeshCount = originalMesh.subMeshCount;
            int subMeshCount = originalMesh.subMeshCount;
            List<List<int>> newSubMeshTrianglesList = new List<List<int>>(subMeshCount);

            // サブメッシュ毎に三角ポリゴンを処理する
            for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
            {
                Material material = originalMaterials[subMeshIndex];
                Texture2D texture = material.mainTexture as Texture2D;
                
                int[] triangles = originalMesh.GetTriangles(subMeshIndex);
                List<int> newSubMeshTriangles = new List<int>();
                List<int> newSubMeshTriangleSources = new List<int>(triangles.Length / 3);

                // 現在のサブメッシュが処理対象なら
                if (subMeshVisibility[subMeshIndex] == true)
                {
                    // 各三角形を確認し、必要に応じて新しい頂点を追加
                    for (int i = 0; i < triangles.Length; i += 3)
                    {
                        int outputCountBefore = newSubMeshTriangles.Count / 3;
                        // 三角ポリゴンを構成する頂点インデックスと削除情報を含んだタプルを作成
                        List<(int index, bool isRemoved)> triangleIndexs = new List<(int index, bool isRemoved)>
                        {
                            (triangles[i], removeVerticesIndexs.Contains(triangles[i])),
                            (triangles[i + 1], removeVerticesIndexs.Contains(triangles[i + 1])),
                            (triangles[i + 2], removeVerticesIndexs.Contains(triangles[i + 2]))
                        };

                        // 全ての頂点が削除対象の場合、三角形を追加しない
                        if (triangleIndexs[0].isRemoved &&
                            triangleIndexs[1].isRemoved &&
                            triangleIndexs[2].isRemoved)
                        {
                            continue;
                        }
                        // いずれの頂点も削除対象でない場合、三角形をそのまま追加
                        else if ( !triangleIndexs[0].isRemoved &&
                                !triangleIndexs[1].isRemoved &&
                                !triangleIndexs[2].isRemoved)
                        {
                            // 先に不要頂点を削除しているため頂点インデックスを変換する必要がある
                            newSubMeshTriangles.Add(oldToNewIndexMap[triangleIndexs[0].index]);
                            newSubMeshTriangles.Add(oldToNewIndexMap[triangleIndexs[1].index]);
                            newSubMeshTriangles.Add(oldToNewIndexMap[triangleIndexs[2].index]);
                        }
                        // 一部の頂点が削除対象の場合
                        else
                        {
                            // 削除対象でない頂点を多角形頂点に追加
                            (List<Vector3> originVertices, List<int> polygonToGlobalIndexMap) =
                                addNonDeletableVertexToPolygon(originalMesh, oldToNewIndexMap, triangleIndexs);         

                            // 辺上の新規頂点座標と、シェイプキー用補完重みを計算
                            (MeshData addMeshData, List<(int, int, float)> localVertexInterpolation, List<int> crossedSides) =
                                addNewVertexToEdge(originalMesh, texture, triangleIndexs);

                            // 追加頂点の中で重複が無いように全体メッシュへ頂点を追加する（既存頂点はシームなどで重複がある）
                            // シェイプキー用補完重みも同様に重複を排除する
                            addUniqueMeshData(addMeshData, newMeshData, polygonToGlobalIndexMap, edgeVertexIndexMap,
                                            localVertexInterpolation, vertexInterpolation);

                            // 処理対象の多角形の外形頂点としてまとめる（残す頂点、辺上の新規頂点の順）
                            List<Vector3> polygonVertices = new List<Vector3>();
                            polygonVertices.AddRange(originVertices);
                            polygonVertices.AddRange(addMeshData.Vertices);

                            // 多角形頂点を三角形の外周順（元の巻き順）に並べ替える
                            List<int> outline = createPolygonOutline(triangleIndexs, crossedSides);
                            List<Vector3> outlineVertices = outline.Select(k => polygonVertices[k]).ToList();
                            List<int> outlineToGlobalIndexMap = outline.Select(k => polygonToGlobalIndexMap[k]).ToList();

                            // 多角形頂点から三角ポリゴンに変換し頂点インデックス配列を返す
                            int[] triangulatedIndices = createTriangleFromPolygon(originalMesh, triangleIndexs, outlineVertices);
                            // 三角ポリゴンの頂点インデックス配列を全体頂点インデックスに変換する
                            List<int> polygonTriangles = convertIndexToGlobal(triangulatedIndices, outlineToGlobalIndexMap);
                            // サブメッシュの三角ポリゴン配列に追加
                            newSubMeshTriangles.AddRange(polygonTriangles);
                        }

                        // この三角形から生成された出力三角形の元の三角形番号を記録する
                        for (int k = outputCountBefore; k < newSubMeshTriangles.Count / 3; k++)
                        {
                            newSubMeshTriangleSources.Add(i / 3);
                        }
                    }
                }
                // 現在のサブメッシュが処理対象でないなら
                else
                {
                    // 三角ポリゴンのインデックスを新しい頂点インデックスに更新する
                    for (int i = 0; i < triangles.Length; i++)
                    {
                        newSubMeshTriangles.Add(oldToNewIndexMap[triangles[i]]);
                    }
                    for (int i = 0; i < triangles.Length / 3; i++)
                    {
                        newSubMeshTriangleSources.Add(i);
                    }
                }
                sourceTriangleIndices.Add(newSubMeshTriangleSources.ToArray());
                newSubMeshTrianglesList.Add(newSubMeshTriangles);
            }

            // 全サブメッシュの頂点が出揃ってから頂点属性と三角形を設定する
            // （頂点数が 65,535 を超える場合は 16 ビットのインデックスでは参照できないため 32 ビットにする）
            newMesh.indexFormat = newMeshData.Vertices.Count > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : originalMesh.indexFormat;
            newMesh.SetVertices(newMeshData.Vertices.ToList());
            newMesh.SetNormals(newMeshData.Normals.ToList());
            newMesh.SetTangents(newMeshData.Tangents.ToList());
            newMesh.SetUVs(0, newMeshData.UV.ToList());
            newMesh.SetUVs(1, newMeshData.UV2.ToList());
            newMesh.SetUVs(2, newMeshData.UV3.ToList());
            newMesh.SetUVs(3, newMeshData.UV4.ToList());
            newMesh.SetUVs(4, newMeshData.UV5.ToList());
            newMesh.SetUVs(5, newMeshData.UV6.ToList());
            newMesh.SetUVs(6, newMeshData.UV7.ToList());
            newMesh.SetUVs(7, newMeshData.UV8.ToList());
            newMesh.SetColors(newMeshData.Colors.ToList());
            newMesh.SetColors(newMeshData.Colors32.ToList());
            newMesh.boneWeights = newMeshData.BoneWeights.ToArray();
            for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
            {
                newMesh.SetTriangles(newSubMeshTrianglesList[subMeshIndex], subMeshIndex);
            }

            newMesh.subMeshCount = subMeshCount;
            newMesh.bindposes = originalMesh.bindposes;

            CompletionBlendShapes(originalMesh, removeVerticesIndexs, newMesh, vertexInterpolation);

            return newMesh;
        }

        // インデックスマッピングの作成
        private Dictionary<int, int> CreateIndexMap(Mesh originalMesh, List<int> removeVerticesIndexs)
        {
            Dictionary<int, int> oldToNewIndexMap = new Dictionary<int, int>();
            for (int oldIndex = 0, newIndex = 0; oldIndex < originalMesh.vertexCount; oldIndex++)
            {
                if (!removeVerticesIndexs.Contains(oldIndex))
                {
                    oldToNewIndexMap[oldIndex] = newIndex;
                    newIndex++;
                }
            }
            return oldToNewIndexMap;
        }

        // 削除対象でない頂点を多角形頂点に追加
        private (List<Vector3>, List<int>) addNonDeletableVertexToPolygon(Mesh originalMesh,
                                                                          Dictionary<int, int> oldToNewIndexMap,
                                                                          List<(int index, bool isRemoved)> triangleIndexs)
        {
            List<Vector3> originVertices = new List<Vector3>(); //処理対象の多角形の外形頂点
            List<int> polygonToGlobalIndexMap = new List<int>();

            for (int index = 0; index < 3; index++) {
                if (!triangleIndexs[index].isRemoved) // 削除対象でない頂点を多角形頂点に追加
                {
                    originVertices.Add(originalMesh.vertices[triangleIndexs[index].index]);
                    polygonToGlobalIndexMap.Add(oldToNewIndexMap[triangleIndexs[index].index]);
                }
            }
            return (originVertices, polygonToGlobalIndexMap);
        }

        // 辺への新規頂点追加（境界点が見つかった辺の番号 0〜2 も返す）
        private (MeshData, List<(int, int, float)>, List<int>) addNewVertexToEdge(Mesh originalMesh, Texture2D texture,
                                                                                  List<(int index, bool isRemoved)> triangleIndexs)
        {
            MeshData addMeshData = new MeshData();
            List<(int, int, float)> localVertexInterpolation = new List<(int, int, float)>();
            List<int> crossedSides = new List<int>();

            if (texture != null)
            {
                List<int[]> sideIndexs = new List<int[]>(){
                    new int[] { triangleIndexs[0].index, triangleIndexs[1].index },
                    new int[] { triangleIndexs[1].index, triangleIndexs[2].index },
                    new int[] { triangleIndexs[2].index, triangleIndexs[0].index }
                };
                // 三角形の各辺に対して、テクスチャ境界値の座標&UV座標の計算
                for (int triangleIndex = 0; triangleIndex < 3; triangleIndex++)
                {
                    (MeshData newMeshDataVertex, (int, int, float) interpolation) =
                        AddEdgeIntersectionPoints(originalMesh, texture, sideIndexs[triangleIndex]);
                    if (newMeshDataVertex.Vertices.Count > 0) // テクスチャ境界値があるなら
                    {
                        // ２つの頂点（インデックス昇順）と重みを保存
                        localVertexInterpolation.Add(interpolation);
                        addMeshData.Add(newMeshDataVertex); //多角形頂点に追加   
                        crossedSides.Add(triangleIndex);
                    }
                }
            }
            return (addMeshData, localVertexInterpolation, crossedSides);
        }

        // originalMeshのエッジとテクスチャの境界点を検出し、新しい頂点のMeshDataと補完情報（両端の頂点インデックス昇順, 重み）を返す関数
        private (MeshData, (int, int, float)) AddEdgeIntersectionPoints(Mesh originalMesh, Texture2D texture, int[] indexs)
        {
            // エッジの両端点を頂点インデックスの昇順に並べる
            // （辺を共有する三角形は辺を逆向きに辿るため、向きに依存する二分探索では境界点が1ulp程度ずれて別の頂点になっていた）
            int[] edge = indexs[0] < indexs[1] ? new int[] { indexs[0], indexs[1] } : new int[] { indexs[1], indexs[0] };

            // エッジの両端点のUV座標を取得
            Vector2 uv1 = originalMesh.uv[edge[0]];
            Vector2 uv2 = originalMesh.uv[edge[1]];
            MeshData newMeshDataVertex = new MeshData();
            float weight = 0;

            // エッジが境界エッジかどうかを判定
            if (IsBoundaryEdge(texture, uv1, uv2))
            {
                // 境界エッジの場合、境界点のUV座標と頂点座標を計算
                weight = FindAlphaBoundary(originalMesh, texture, edge);
                newMeshDataVertex = VertexCompletion(originalMesh, edge, weight);
            }

            return (newMeshDataVertex, (edge[0], edge[1], weight));
        }

        // UV座標が示すテクスチャのピクセルが境界エッジかどうかを判定する関数
        private bool IsBoundaryEdge(Texture2D texture, Vector2 uv1, Vector2 uv2)
        {
            return IsBoundaryEdge(texture, uv1, uv2, alphaThreshold);
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

        // テクスチャのアルファ値に基づき、エッジ上の境界点のUV座標の補完用重みを求める
        private float FindAlphaBoundary(Mesh originalMesh, Texture2D texture, int[] indexs)
        {
            return FindAlphaBoundary(texture, originalMesh.uv[indexs[0]], originalMesh.uv[indexs[1]], alphaThreshold);
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

        // ２つの頂点インデックスと重みから線形補完したMeshDataを返す
        private MeshData VertexCompletion(Mesh originalMesh, int[] indexs, float weight)
        {
            MeshData newMeshDataVertex = new MeshData();

            newMeshDataVertex.Vertices.Add(Vector3.Lerp(originalMesh.vertices[indexs[0]], originalMesh.vertices[indexs[1]], weight));
            newMeshDataVertex.UV.Add(Vector2.Lerp(originalMesh.uv[indexs[0]], originalMesh.uv[indexs[1]], weight));

            // 法線は補間後に正規化し、接線は補間後の法線と直交させて正規化する。接線の w（従法線の向き）は補間せず重みの近い側の値を使う
            Vector3 normal = Vector3.zero;
            if (indexs[0] < originalMesh.normals.Length && indexs[1] < originalMesh.normals.Length)
            {
                normal = VertexAttributeUtils.LerpNormal(originalMesh.normals[indexs[0]], originalMesh.normals[indexs[1]], weight);
                newMeshDataVertex.Normals.Add(normal);
            }
            if (indexs[0] < originalMesh.tangents.Length && indexs[1] < originalMesh.tangents.Length)
                newMeshDataVertex.Tangents.Add(VertexAttributeUtils.LerpTangent(originalMesh.tangents[indexs[0]], originalMesh.tangents[indexs[1]], weight, normal));
            
            if (indexs[0] < originalMesh.uv2.Length && indexs[1] < originalMesh.uv2.Length)
                newMeshDataVertex.UV2.Add(Vector2.Lerp(originalMesh.uv2[indexs[0]], originalMesh.uv2[indexs[1]], weight));
            if (indexs[0] < originalMesh.uv3.Length && indexs[1] < originalMesh.uv3.Length)
                newMeshDataVertex.UV3.Add(Vector2.Lerp(originalMesh.uv3[indexs[0]], originalMesh.uv3[indexs[1]], weight));
            if (indexs[0] < originalMesh.uv4.Length && indexs[1] < originalMesh.uv4.Length)
                newMeshDataVertex.UV4.Add(Vector2.Lerp(originalMesh.uv4[indexs[0]], originalMesh.uv4[indexs[1]], weight));
            if (indexs[0] < originalMesh.uv5.Length && indexs[1] < originalMesh.uv5.Length)
                newMeshDataVertex.UV5.Add(Vector2.Lerp(originalMesh.uv5[indexs[0]], originalMesh.uv5[indexs[1]], weight));
            if (indexs[0] < originalMesh.uv6.Length && indexs[1] < originalMesh.uv6.Length)
                newMeshDataVertex.UV6.Add(Vector2.Lerp(originalMesh.uv6[indexs[0]], originalMesh.uv6[indexs[1]], weight));
            if (indexs[0] < originalMesh.uv7.Length && indexs[1] < originalMesh.uv7.Length)
                newMeshDataVertex.UV7.Add(Vector2.Lerp(originalMesh.uv7[indexs[0]], originalMesh.uv7[indexs[1]], weight));
            if (indexs[0] < originalMesh.uv8.Length && indexs[1] < originalMesh.uv8.Length)
                newMeshDataVertex.UV8.Add(Vector2.Lerp(originalMesh.uv8[indexs[0]], originalMesh.uv8[indexs[1]], weight));
            
            if (indexs[0] < originalMesh.colors.Length && indexs[1] < originalMesh.colors.Length)
                newMeshDataVertex.Colors.Add(Color.Lerp(originalMesh.colors[indexs[0]], originalMesh.colors[indexs[1]], weight));
            if (indexs[0] < originalMesh.colors32.Length && indexs[1] < originalMesh.colors32.Length)
                newMeshDataVertex.Colors32.Add(Color.Lerp(originalMesh.colors32[indexs[0]], originalMesh.colors32[indexs[1]], weight));

            if (indexs[0] < originalMesh.boneWeights.Length && indexs[1] < originalMesh.boneWeights.Length)
            {
                BoneWeight BoneWeightLerp= BoneWeightUtils.LerpBoneWeight(originalMesh.boneWeights[indexs[0]], originalMesh.boneWeights[indexs[1]], weight);
                newMeshDataVertex.BoneWeights.Add(BoneWeightLerp);
            }
            return newMeshDataVertex;
        }

        // 追加頂点の中で重複が無いように全体メッシュへ追加する
        // 同じ辺（頂点インデックスのペア）上の境界点は1つの頂点として共有する。辺を頂点インデックスで判定するため、
        // シーム（座標は同じだが頂点インデックスが異なる辺）の両側には別々の頂点が作られ、UVなどの属性は混ざらない
        private void addUniqueMeshData(MeshData addMeshData, MeshData newMeshData, List<int> polygonToGlobalIndexMap,
                                       Dictionary<(int, int), int> edgeVertexIndexMap,
                                       List<(int, int, float)> localVertexInterpolation,
                                       Dictionary<int, (int, int, float)> vertexInterpolation)
        {
            for (int j = 0; j < addMeshData.Vertices.Count; j++)
            {
                (int indexA, int indexB, float weight) = localVertexInterpolation[j];
                if (edgeVertexIndexMap.TryGetValue((indexA, indexB), out int existingIndex))
                {
                    // 既に同じ辺に頂点が追加されているならそれを使う
                    polygonToGlobalIndexMap.Add(existingIndex);
                }
                else
                {
                    newMeshData.Add(addMeshData.GetElementAt(j));
                    int newIndex = newMeshData.Vertices.Count - 1;
                    edgeVertexIndexMap[(indexA, indexB)] = newIndex;
                    polygonToGlobalIndexMap.Add(newIndex);
                    vertexInterpolation.Add(newIndex, (indexA, indexB, weight));
                }
            }
        }

        // 多角形頂点（残す頂点、辺上の新規頂点の順）を三角形の外周順に並べたインデックス列を返す
        // 三角形の頂点 i を巡りながら、残す頂点なら追加し、続く辺 (i, i+1) に境界点があればそれを追加する
        private List<int> createPolygonOutline(List<(int index, bool isRemoved)> triangleIndexs, List<int> crossedSides)
        {
            int keptCount = triangleIndexs.Count(t => !t.isRemoved);
            List<int> outline = new List<int>(keptCount + crossedSides.Count);
            int keptCursor = 0;
            for (int i = 0; i < 3; i++)
            {
                if (!triangleIndexs[i].isRemoved)
                {
                    outline.Add(keptCursor++);
                }
                int crossing = crossedSides.IndexOf(i);
                if (crossing >= 0)
                {
                    outline.Add(keptCount + crossing);
                }
            }
            return outline;
        }

        // 外周順の多角形頂点から三角ポリゴンに変換し頂点配列を返す
        private int[] createTriangleFromPolygon(Mesh originalMesh, List<(int index, bool isRemoved)> triangleIndexs, List<Vector3> polygonVertices)
        {
            // 処理対象の三角ポリゴンから法線ベクトルを計算し、面の向きを指定する
            Vector3 a = originalMesh.vertices[triangleIndexs[0].index];
            Vector3 b = originalMesh.vertices[triangleIndexs[1].index];
            Vector3 c = originalMesh.vertices[triangleIndexs[2].index];
            Vector3 normal = Vector3.Cross(b - a, c - a);
            // 耳切り法により、多角形外周頂点から三角ポリゴンに分割し、そのインデックス番号順を返す（巻き順は元の三角形と同じ）
            int[] triangulatedIndices = EarClipping2D.Triangulate(polygonVertices, normal);
            return triangulatedIndices;
        }

        // 多角形ポリゴンの頂点インデックスを全体頂点インデックスに変換する
        private List<int> convertIndexToGlobal(int[] triangulatedIndices, List<int> polygonToGlobalIndexMap)
        {
            List<int> polygonTriangles = new List<int>();
            for (int j = 0; j < triangulatedIndices.Length; j++)
            {
                int polygonIndex = triangulatedIndices[j];
                // 三角ポリゴンのインデックス番号を変換して追加
                polygonTriangles.Add(polygonToGlobalIndexMap[polygonIndex]);
            }
            return polygonTriangles;
        }

        protected void CompletionBlendShapes(Mesh originalMesh, List<int> removeVerticesIndexs, Mesh newMesh, Dictionary<int, (int, int, float)> blendShapeInterpolation)
        {
            // 1つの頂点に対して：ブレンドシェイプの数×ブレンドシェイプのフレーム分の頂点、法線、接線情報が必要
            // 元のメッシュの全ブレンドシェイプに対して処理を行う
            for (int i = 0; i < originalMesh.blendShapeCount; i++)
            {
                // 現在のブレンドシェイプの名前を取得
                string blendShapeName = originalMesh.GetBlendShapeName(i);
                // 現在のブレンドシェイプのフレーム数を取得
                int frameCount = originalMesh.GetBlendShapeFrameCount(i);

                // 各フレームに対して処理を行う
                for (int j = 0; j < frameCount; j++)
                {
                    // フレームのウェイトを取得
                    float frameWeight = originalMesh.GetBlendShapeFrameWeight(i, j);
                    // フレームの頂点、法線、接線を格納する配列を作成
                    Vector3[] frameVertices = new Vector3[originalMesh.vertexCount];
                    Vector3[] frameNormals = new Vector3[originalMesh.vertexCount];
                    Vector3[] frameTangents = new Vector3[originalMesh.vertexCount];
                    // フレームの頂点、法線、接線を取得
                    originalMesh.GetBlendShapeFrameVertices(i, j, frameVertices, frameNormals, frameTangents);

                    // 配列をリストに変換
                    List<Vector3> frameVerticesList = new List<Vector3>(frameVertices);
                    List<Vector3> frameNormalsList = new List<Vector3>(frameNormals);
                    List<Vector3> frameTangentsList = new List<Vector3>(frameTangents);

                    // 指定されたインデックスの頂点、法線、接線をリストから削除
                    foreach (int index in removeVerticesIndexs)
                    {
                        frameVerticesList.RemoveAt(index);
                        frameNormalsList.RemoveAt(index);
                        frameTangentsList.RemoveAt(index);
                    }

                    // 補完処理の追加（新規頂点は頂点インデックスの順に並べる。差分値なので正規化はしない）
                    foreach (var kvp in blendShapeInterpolation.OrderBy(kvp => kvp.Key))
                    {
                        (int indexA, int indexB, float weight) = kvp.Value;

                        // 頂点の補完
                        frameVerticesList.Add(Vector3.Lerp(frameVertices[indexA], frameVertices[indexB], weight));
                        // 法線の補完
                        frameNormalsList.Add(Vector3.Lerp(frameNormals[indexA], frameNormals[indexB], weight));
                        // 接線の補完
                        frameTangentsList.Add(Vector3.Lerp(frameTangents[indexA], frameTangents[indexB], weight));
                    }

                    // 新しいメッシュにブレンドシェイプのフレームを追加
                    newMesh.AddBlendShapeFrame(blendShapeName, frameWeight, frameVerticesList.ToArray(), frameNormalsList.ToArray(), frameTangentsList.ToArray());
                }
            }
        }
    }
}
