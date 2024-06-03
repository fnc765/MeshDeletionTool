using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using Unity.VisualScripting;

namespace MeshDeletionTool
{
    public class MeshDeletionToolForTexture : MeshDeletionToolUtils
    {
        // 対象オブジェクトのRendererを保持するための内部フィールド
        internal Renderer targetRenderer;

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

        // 頂点削除と頂点追加を行いテクスチャに合わせたメッシュ形状に編集する
        private Mesh CreateMeshAfterVertexModification(Mesh originalMesh, Material[] originalMaterials, List<int> removeVerticesIndexs)
        {
            MeshData newMeshData = new MeshData();

            // 新規追加頂点の重複を避けるためにマッピング
            Dictionary<Vector3, int> addVertexIndexMap = new Dictionary<Vector3, int>();

            Mesh newMesh = new Mesh();

            // 先に不要頂点を削除する
            for (int index = 0; index < originalMesh.vertexCount; index++)
            {
                if (removeVerticesIndexs.Contains(index))
                    continue;

                Vector3 vertex = originalMesh.vertices[index];
                newMeshData.Vertices.Add(vertex);
                newMeshData.UV.Add(originalMesh.uv[index]);
            }
            
            // 重複している頂点(シーム)のインデックスリストを作成
            HashSet<int> seamVertexIndex = CreateSeamIndex(originalMesh);

            // インデックスマッピングの作成
            Dictionary<int, int> oldToNewIndexMap = CreateIndexMap(originalMesh, removeVerticesIndexs);


            newMesh.subMeshCount = originalMesh.subMeshCount;
            int subMeshCount = originalMesh.subMeshCount;
            int addSubMeshIndex = 0;

            // サブメッシュ毎に三角ポリゴンを処理する
            for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
            {
                Material material = originalMaterials[subMeshIndex];
                Texture2D texture = material.mainTexture as Texture2D;
                
                int[] triangles = originalMesh.GetTriangles(subMeshIndex);
                List<int> newSubMeshTriangles = new List<int>();

                // 各三角形を確認し、必要に応じて新しい頂点を追加
                for (int i = 0; i < triangles.Length; i += 3)
                {
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

                        // 辺への新規頂点追加
                        MeshData addMeshData = addNewVertexToEdge(originalMesh, texture, triangleIndexs);

                        // 追加頂点の中で重複が無いように全体メッシュへ頂点を追加する（既存頂点はシームなどで重複がある）
                        addUniqueMeshData(addMeshData, triangleIndexs, seamVertexIndex,
                                          newMeshData, polygonToGlobalIndexMap, addVertexIndexMap);

                        // 処理対象の多角形の外形頂点としてまとめる
                        List<Vector3> polygonVertices = new List<Vector3>();
                        polygonVertices.AddRange(originVertices);
                        polygonVertices.AddRange(addMeshData.Vertices);

                        // 多角形頂点から三角ポリゴンに変換し頂点インデックス配列を返す
                        int[] triangulatedIndices = createTriangleFromPolygon(originalMesh, triangleIndexs, polygonVertices);
                        // 三角ポリゴンの頂点インデックス配列を全体頂点インデックスに変換する
                        List<int> polygonTriangles = convertIndexToGlobal(triangulatedIndices, polygonToGlobalIndexMap);
                        // サブメッシュの三角ポリゴン配列に追加
                        newSubMeshTriangles.AddRange(polygonTriangles);
                    }
                }
                newMesh.SetVertices(newMeshData.Vertices.ToList());
                newMesh.SetUVs(0, newMeshData.UV.ToList());
                newMesh.SetTriangles(newSubMeshTriangles, addSubMeshIndex++);
            }

            newMesh.subMeshCount = addSubMeshIndex;

            return newMesh;
        }

        // 重複している頂点(シーム)のインデックスリストを作成 
        private HashSet<int> CreateSeamIndex(Mesh mesh)
        {
            Dictionary<Vector3, int> seamVertex = new Dictionary<Vector3, int>();
            HashSet<int> seamVertexIndex = new HashSet<int>();

            for (int index = 0; index < mesh.vertexCount; index++)
            {
                Vector3 vertex = mesh.vertices[index];
                // シームの頂点かどうかをチェックし、インデックスを格納
                if (seamVertex.ContainsKey(vertex))
                {
                    seamVertexIndex.Add(seamVertex[vertex]); // 既に存在する頂点なので、そのインデックスを追加
                    seamVertexIndex.Add(index); // 現在のインデックスも追加
                }
                else
                {
                    seamVertex.Add(vertex, index); // 新しい頂点を追加
                }
            }
            return seamVertexIndex;
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

        // 辺への新規頂点追加
        private MeshData addNewVertexToEdge(Mesh originalMesh, Texture2D texture,
                                            List<(int index, bool isRemoved)> triangleIndexs)
        {
            MeshData addMeshData = new MeshData();
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
                    (Vector2? newUV, Vector3? newVertex) = 
                        AddEdgeIntersectionPoints(originalMesh, texture, sideIndexs[triangleIndex]);
                    if (newVertex.HasValue) // テクスチャ境界値があるなら
                    {
                        addMeshData.Vertices.Add(newVertex.Value); //多角形頂点に追加
                        addMeshData.UV.Add(newUV.Value);  // UVも追加
                    }
                }
            }
            return addMeshData;
        }

        // originalMeshのエッジとテクスチャの境界点を検出し、エッジ交点のUV座標と新しい頂点座標を返す関数
        private (Vector2? finalUV, Vector3? newVertex) AddEdgeIntersectionPoints(Mesh originalMesh, Texture2D texture, int[] indexs)
        {
            // エッジの両端点の3D座標とUV座標を取得
            Vector3 p1 = originalMesh.vertices[indexs[0]];
            Vector3 p2 = originalMesh.vertices[indexs[1]];
            Vector2 uv1 = originalMesh.uv[indexs[0]];
            Vector2 uv2 = originalMesh.uv[indexs[1]];

            // エッジが境界エッジかどうかを判定
            if (IsBoundaryEdge(texture, uv1, uv2))
            {
                // 境界エッジの場合、境界点のUV座標と頂点座標を計算
                (Vector2? finalUV, Vector3 newVertex) = FindAlphaBoundary(texture, uv1, uv2, p1, p2);
                return (finalUV, newVertex);
            }

            // 境界エッジでない場合はnullを返す
            return (null, null);
        }

        // UV座標が示すテクスチャのピクセルが境界エッジかどうかを判定する関数
        private bool IsBoundaryEdge(Texture2D texture, Vector2 uv1, Vector2 uv2)
        {
            // UV座標をピクセル座標に変換
            Vector2 pixelUV1 = new Vector2(uv1.x * (texture.width - 1), uv1.y * (texture.height - 1));
            Vector2 pixelUV2 = new Vector2(uv2.x * (texture.width - 1), uv2.y * (texture.height - 1));

            // 両端点のピクセルの色を取得
            Color color1 = texture.GetPixel((int)pixelUV1.x, (int)pixelUV1.y);
            Color color2 = texture.GetPixel((int)pixelUV2.x, (int)pixelUV2.y);

            // 片方のピクセルが透明で、もう片方が透明でない場合は境界エッジとする
            return (color1.a == 0 && color2.a != 0) || (color1.a != 0 && color2.a == 0);
        }

        // テクスチャのアルファ値に基づき、エッジ上の境界点のUV座標と頂点座標を探す関数
        private (Vector2 uv, Vector3 vertex) FindAlphaBoundary(Texture2D texture, Vector2 uv1, Vector2 uv2, Vector3 p1, Vector3 p2)
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
                if ((color1.a == 0 && midColor.a != 0) || (color1.a != 0 && midColor.a == 0))
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
            float finalT = (tMin + tMax) / 2.0f;
            Vector2 finalUV = Vector2.Lerp(uv1, uv2, finalT);
            Vector3 finalVertex = Vector3.Lerp(p1, p2, finalT);
            return (finalUV, finalVertex);
        }

        // 追加頂点の中で重複が無いように全体メッシュへ追加する（既存頂点はシームなどで重複がある）
        private void addUniqueMeshData(MeshData addMeshData, List<(int index, bool isRemoved)> triangleIndexs,
                                       HashSet<int> seamVertexIndex,
                                       MeshData newMeshData, List<int> polygonToGlobalIndexMap, 
                                       Dictionary<Vector3, int> addVertexIndexMap)
        {
            for (int j = 0; j < addMeshData.Vertices.Count; j++)
            {
                Vector3 vertex = addMeshData.Vertices[j];
                Vector2 uv = addMeshData.UV[j];
                if (!addVertexIndexMap.ContainsKey(vertex)) {//追加頂点の座標マップに含まれない座標の場合
                    //TODO: 本来は継ぎ目の辺へ新規頂点追加時の判定が必要だが、簡易的に対象ポリゴン頂点がシーム頂点に含まれているかで判定している
                    //継ぎ目の辺でないなら
                    if (!seamVertexIndex.Contains(triangleIndexs[0].index) &&
                        !seamVertexIndex.Contains(triangleIndexs[1].index) &&
                        !seamVertexIndex.Contains(triangleIndexs[2].index)) {
                        addVertexIndexMap[vertex] = newMeshData.Vertices.Count;  //追加頂点の重複防止Mapに追加
                    }
                    newMeshData.Vertices.Add(vertex);
                    newMeshData.UV.Add(uv);
                    polygonToGlobalIndexMap.Add(newMeshData.Vertices.Count - 1);
                } else {
                    polygonToGlobalIndexMap.Add(addVertexIndexMap[vertex]);
                }
            }
        }

        // 多角形頂点から三角ポリゴンに変換し頂点配列を返す
        private int[] createTriangleFromPolygon(Mesh originalMesh, List<(int index, bool isRemoved)> triangleIndexs, List<Vector3> polygonVertices)
        {
            // 処理対象の三角ポリゴンから法線ベクトルを計算し、面の向きを指定する
            Vector3[] basisVertices = {originalMesh.vertices[triangleIndexs[0].index],
                                        originalMesh.vertices[triangleIndexs[1].index],
                                        originalMesh.vertices[triangleIndexs[2].index]};
            Vector3 normal = EarClipping3D.CalculateNormal(basisVertices);
            // 耳切り法により、多角形外周頂点から三角ポリゴンに分割し、そのインデックス番号順を返す
            int[] triangulatedIndices = EarClipping3D.Triangulate(polygonVertices.ToArray(), normal);
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
    }
}
