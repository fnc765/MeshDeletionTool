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

        // 削除対象の頂点を除去した新しいメッシュを作成するメソッド
        private Mesh CreateMeshAfterVertexModification(Mesh originalMesh, Material[] originalMaterials, List<int> removeVerticesIndexs)
        {
            List<Vector3> newVertices = new List<Vector3>(originalMesh.vertices);
            List<Vector2> newUVs = new List<Vector2>(originalMesh.uv);
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

                    if (removeVerticesIndexs.Contains(v1) || removeVerticesIndexs.Contains(v2) || removeVerticesIndexs.Contains(v3))
                    {
                        Vector3 p1 = originalMesh.vertices[v1]; // オリジナルメッシュの座標取得
                        Vector3 p2 = originalMesh.vertices[v2];
                        Vector3 p3 = originalMesh.vertices[v3];

                        Vector2 uv1 = originalMesh.uv[v1]; // オリジナルメッシュのUV取得
                        Vector2 uv2 = originalMesh.uv[v2];
                        Vector2 uv3 = originalMesh.uv[v3];

                        Material material = originalMaterials[subMeshIndex];
                        Texture2D texture = material.mainTexture as Texture2D;

                        if (texture != null)
                        {
                            // エッジの交差点を追加
                            int newV1 = AddEdgeIntersectionPoints(texture, newVertices, newUVs, newVerticesMap, p1, p2, uv1, uv2);
                            int newV2 = AddEdgeIntersectionPoints(texture, newVertices, newUVs, newVerticesMap, p2, p3, uv2, uv3);
                            int newV3 = AddEdgeIntersectionPoints(texture, newVertices, newUVs, newVerticesMap, p3, p1, uv3, uv1);

                            // 新しい三角形を追加
                            if (newV1 != -1 && newV2 != -1) //newV3の辺は新しい頂点がない＝その辺はどちらも消えない頂点
                            {
                                newTriangles.Add(v1);
                                newTriangles.Add(newV1);   //三角ポリゴンの真ん中はクロスするため、もう一つの三角ポリゴンには使わない
                                newTriangles.Add(newV2);

                                newTriangles.Add(newV2);
                                newTriangles.Add(v3);
                                newTriangles.Add(v1);
                            }
                            if (newV2 != -1 && newV3 != -1) //newV1に指定した頂点はどちらも有効
                            {
                                newTriangles.Add(v2);
                                newTriangles.Add(newV2);
                                newTriangles.Add(newV3);

                                newTriangles.Add(newV3);
                                newTriangles.Add(v1);
                                newTriangles.Add(v2);
                            }
                            if (newV3 != -1 && newV1 != -1)
                            {
                                newTriangles.Add(v3);
                                newTriangles.Add(newV3);
                                newTriangles.Add(newV1);

                                newTriangles.Add(newV1);
                                newTriangles.Add(v2);
                                newTriangles.Add(v3);
                            }
                        }
                    }
                    else
                    {
                        // 削除対象でない三角形はそのまま追加
                        newTriangles.Add(v1);
                        newTriangles.Add(v2);
                        newTriangles.Add(v3);
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

        private int AddEdgeIntersectionPoints(Texture2D texture, List<Vector3> vertices, List<Vector2> uvs, Dictionary<Vector2, int> verticesMap, Vector3 p1, Vector3 p2, Vector2 uv1, Vector2 uv2)
        {
            // UV座標をピクセル座標に変換
            Vector2 pixelUV1 = new Vector2(uv1.x * (texture.width - 1), uv1.y * (texture.height - 1));
            Vector2 pixelUV2 = new Vector2(uv2.x * (texture.width - 1), uv2.y * (texture.height - 1));

            // 各ピクセルの色を取得
            Color color1 = texture.GetPixel((int)pixelUV1.x, (int)pixelUV1.y);
            Color color2 = texture.GetPixel((int)pixelUV2.x, (int)pixelUV2.y);

            // 境界エッジであるかどうかを判定
            if ((color1.a == 0 && color2.a != 0) || (color1.a != 0 && color2.a == 0))
            {
                // 透明・非透明が切り替わる座標を見つけるための二分探索
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
                Vector3 newVertex = Vector3.Lerp(p1, p2, 0.5f);

                // すでに存在するかチェック
                if (!verticesMap.TryGetValue(finalUV, out int newIndex))
                {
                    vertices.Add(newVertex);
                    uvs.Add(finalUV);
                    newIndex = vertices.Count - 1;
                    verticesMap[finalUV] = newIndex;
                }

                // 新しい頂点のインデックスを返す
                return newIndex;
            }

            // 境界エッジでない場合は-1を返す
            return -1;
        }
    }
}
