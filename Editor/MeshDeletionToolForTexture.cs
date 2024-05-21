using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace MeshDeletionTool
{
    public class MeshDeletionToolForTexture : MeshDeletionTool
    {
        // 対象オブジェクトのRendererを保持するための内部フィールド
        internal Renderer targetRenderer;

        // メニューアイテムからツールを初期化してウィンドウを表示するメソッド
        [MenuItem("Tools/MeshDeletionToolForTexture")]
        private static void Init()
        {
            // ウィンドウを作成し表示する
            MeshDeletionToolForTexture window = (MeshDeletionToolForTexture)EditorWindow.GetWindow(typeof(MeshDeletionToolForTexture));
            window.titleContent = new GUIContent("MeshDeletionTool");
            window.Show();
        }

        // GUIを描画するためのメソッド
        private void OnGUI()
        {
            // ラベルを表示
            GUILayout.Label("オブジェクトを選択", EditorStyles.boldLabel);

            // 対象オブジェクトを選択するためのフィールド
            targetRenderer = EditorGUILayout.ObjectField("対象オブジェクト", targetRenderer, typeof(Renderer), true) as Renderer;

            // ボタンをクリックしたらメッシュ削除処理を実行
            if (GUILayout.Button("テクスチャ透明部分のメッシュを削除"))
            {
                DeleteMeshesFromTexture();
            }
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

            // 削除すべき頂点のインデックスを取得
            List<int> removeVerticesIndexs = GetVerticesToRemoveFromTexture(originalMesh, originalMaterials);
            // 新しいメッシュを作成
            Mesh newMesh = CreateMeshAfterVertexModification(originalMesh, originalMaterials, removeVerticesIndexs);
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

        // テクスチャに基づいて削除すべき頂点のインデックスを取得するメソッド
        private List<int> GetVerticesToRemoveFromTexture(Mesh originalMesh, Material[] originalMaterials)
        {
            List<int> removeVerticesIndexs = new List<int>();

            int subMeshCount = originalMesh.subMeshCount;
            List<HashSet<int>> subMeshTrianglesList = new List<HashSet<int>>();
            
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

                            // ピクセルのアルファ値が0なら頂点を削除対象とする
                            if (color.a == 0)
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

        private Mesh CreateMeshAfterVertexModification(Mesh originalMesh, Material[] originalMaterials, List<int> removeVerticesIndexs)
        {
            List<Vector3> newVertices = new List<Vector3>();
            List<Vector2> newUVs = new List<Vector2>();
            List<int> newTriangles = new List<int>();

            Dictionary<Vector2, int> newVerticesMap = new Dictionary<Vector2, int>();

            int subMeshCount = originalMesh.subMeshCount;

            // サブメッシュ毎に三角ポリゴンを処理する
            for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
            {
                int[] triangles = originalMesh.GetTriangles(subMeshIndex);

                // 各三角形を確認し、必要に応じて新しい頂点を追加
                for (int i = 0; i < triangles.Length; i += 3)
                {
                    int v1 = triangles[i];
                    int v2 = triangles[i + 1];
                    int v3 = triangles[i + 2];

                    bool v1Removed = removeVerticesIndexs.Contains(v1);
                    bool v2Removed = removeVerticesIndexs.Contains(v2);
                    bool v3Removed = removeVerticesIndexs.Contains(v3);

                    // 全ての頂点が削除対象の場合、三角形を追加しない
                    if (v1Removed && v2Removed && v3Removed)
                    {
                        continue;
                    }
                    // いずれの頂点も削除対象でない場合、三角形をそのまま追加
                    else if (!v1Removed && !v2Removed && !v3Removed)
                    {
                        newTriangles.Add(v1);
                        newTriangles.Add(v2);
                        newTriangles.Add(v3);
                    }
                    // 一部の頂点が削除対象の場合
                    else
                    {
                        List<Vector3> polygonVertices = new List<Vector3>();
                        List<Vector2> polygonUVs = new List<Vector2>();

                        if (!v1Removed)
                        {
                            polygonVertices.Add(originalMesh.vertices[v1]);
                            polygonUVs.Add(originalMesh.uv[v1]);
                        }
                        if (!v2Removed)
                        {
                            polygonVertices.Add(originalMesh.vertices[v2]);
                            polygonUVs.Add(originalMesh.uv[v2]);
                        }
                        if (!v3Removed)
                        {
                            polygonVertices.Add(originalMesh.vertices[v3]);
                            polygonUVs.Add(originalMesh.uv[v3]);
                        }

                        Material material = originalMaterials[subMeshIndex];
                        Texture2D texture = material.mainTexture as Texture2D;

                        if (texture != null)
                        {
                            Vector3? intersection1 = AddEdgeIntersectionPoints(texture, originalMesh.vertices[v1], originalMesh.vertices[v2], originalMesh.uv[v1], originalMesh.uv[v2]);
                            Vector3? intersection2 = AddEdgeIntersectionPoints(texture, originalMesh.vertices[v2], originalMesh.vertices[v3], originalMesh.uv[v2], originalMesh.uv[v3]);
                            Vector3? intersection3 = AddEdgeIntersectionPoints(texture, originalMesh.vertices[v3], originalMesh.vertices[v1], originalMesh.uv[v3], originalMesh.uv[v1]);

                            if (intersection1.HasValue) 
                            {
                                polygonVertices.Add(intersection1.Value);
                                polygonUVs.Add(Vector2.Lerp(originalMesh.uv[v1], originalMesh.uv[v2], 0.5f));  // UVも追加
                            }
                            if (intersection2.HasValue)
                            {
                                polygonVertices.Add(intersection2.Value);
                                polygonUVs.Add(Vector2.Lerp(originalMesh.uv[v2], originalMesh.uv[v3], 0.5f));  // UVも追加
                            }
                            if (intersection3.HasValue)
                            {
                                polygonVertices.Add(intersection3.Value);
                                polygonUVs.Add(Vector2.Lerp(originalMesh.uv[v3], originalMesh.uv[v1], 0.5f));  // UVも追加
                            }
                        }

                        // 頂点の重複を避けるためにマッピング
                        Dictionary<Vector3, int> vertexIndexMap = new Dictionary<Vector3, int>();
                        List<Vector3> uniqueVertices = new List<Vector3>();
                        List<Vector2> uniqueUVs = new List<Vector2>();

                        for (int j = 0; j < polygonVertices.Count; j++)
                        {
                            Vector3 vertex = polygonVertices[j];
                            Vector2 uv = polygonUVs[j];
                            if (!vertexIndexMap.ContainsKey(vertex))
                            {
                                uniqueVertices.Add(vertex);
                                uniqueUVs.Add(uv);
                                vertexIndexMap[vertex] = newVertices.Count;
                                newVertices.Add(vertex);
                                newUVs.Add(uv);
                            }
                        }

                        int[] triangulatedIndices = EarClipping3D.Triangulate(uniqueVertices.ToArray());

                        for (int j = 0; j < triangulatedIndices.Length; j++)
                        {
                            newTriangles.Add(vertexIndexMap[uniqueVertices[triangulatedIndices[j]]]);
                        }
                    }
                }
            }

            // 新しいメッシュを作成
            Mesh newMesh = new Mesh
            {
                vertices = newVertices.ToArray(),
                uv = newUVs.ToArray(),
                triangles = newTriangles.ToArray()
            };

            return newMesh;
        }

        private Vector3? AddEdgeIntersectionPoints(Texture2D texture, Vector3 p1, Vector3 p2, Vector2 uv1, Vector2 uv2)
        {
            if (IsBoundaryEdge(texture, uv1, uv2))
            {
                (Vector2 finalUV, Vector3 newVertex) = FindAlphaBoundary(texture, uv1, uv2, p1, p2);
                return newVertex;
            }

            return null;
        }

        private bool IsBoundaryEdge(Texture2D texture, Vector2 uv1, Vector2 uv2)
        {
            Vector2 pixelUV1 = new Vector2(uv1.x * (texture.width - 1), uv1.y * (texture.height - 1));
            Vector2 pixelUV2 = new Vector2(uv2.x * (texture.width - 1), uv2.y * (texture.height - 1));

            Color color1 = texture.GetPixel((int)pixelUV1.x, (int)pixelUV1.y);
            Color color2 = texture.GetPixel((int)pixelUV2.x, (int)pixelUV2.y);

            return (color1.a == 0 && color2.a != 0) || (color1.a != 0 && color2.a == 0);
        }

        private (Vector2 uv, Vector3 vertex) FindAlphaBoundary(Texture2D texture, Vector2 uv1, Vector2 uv2, Vector3 p1, Vector3 p2)
        {
            Vector2 pixelUV1 = new Vector2(uv1.x * (texture.width - 1), uv1.y * (texture.height - 1));
            Color color1 = texture.GetPixel((int)pixelUV1.x, (int)pixelUV1.y);

            for (int i = 0; i < 10; i++)
            {
                float t = 0.5f;
                Vector2 midUV = Vector2.Lerp(uv1, uv2, t);
                Vector2 midPixelUV = new Vector2(midUV.x * (texture.width - 1), midUV.y * (texture.height - 1));
                Color midColor = texture.GetPixel((int)midPixelUV.x, (int)midPixelUV.y);
                Vector3 midVertex = Vector3.Lerp(p1, p2, t);

                if ((color1.a == 0 && midColor.a != 0) || (color1.a != 0 && midColor.a == 0))
                {
                    uv2 = midUV;
                    p2 = midVertex;
                }
                else
                {
                    uv1 = midUV;
                    color1 = midColor;
                    p1 = midVertex;
                }
            }

            Vector2 finalUV = Vector2.Lerp(uv1, uv2, 0.5f);
            Vector3 finalVertex = Vector3.Lerp(p1, p2, 0.5f);
            return (finalUV, finalVertex);
        }
    }
}
