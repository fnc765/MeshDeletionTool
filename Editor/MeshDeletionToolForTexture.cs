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

        // アルファ値がこの値より小さいメッシュは削除する
        private float alphaThreshold = 0.5F;

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

            // アルファ閾値を指定するスライダーを追加
            GUILayout.Label("アルファ閾値を設定", EditorStyles.boldLabel);
            alphaThreshold = EditorGUILayout.Slider("アルファ閾値", alphaThreshold, 0f, 1f);

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
        private Mesh CreateMeshAfterVertexModification(Mesh originalMesh, Material[] originalMaterials, List<int> removeVerticesIndexs)
        {
            MeshData newMeshData = new MeshData();

            // 新規追加頂点の重複を避けるためにマッピング
            Dictionary<Vector3, int> addVertexIndexMap = new Dictionary<Vector3, int>();

            // 新規追加頂点を補完するための２点頂点インデックスと重みを、新規頂点インデックスをキーとして保持
            Dictionary<int, (int, int, float)> vertexInterpolation = new Dictionary<int, (int, int, float)>();

            Mesh newMesh = new Mesh();

            // 先に不要頂点を削除する
            for (int index = 0; index < originalMesh.vertexCount; index++)
            {
                if (removeVerticesIndexs.Contains(index))
                    continue;
                newMeshData.AddElementFromMesh(originalMesh, index);
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

                        // 辺上の新規頂点座標と、シェイプキー用補完重みを計算
                        (MeshData addMeshData, List<(int, int, float)> localVertexInterpolation) =
                            addNewVertexToEdge(originalMesh, texture, triangleIndexs);

                        // 追加頂点の中で重複が無いように全体メッシュへ頂点を追加する（既存頂点はシームなどで重複がある）
                        // シェイプキー用補完重みも同様に重複を排除する
                        addUniqueMeshData(addMeshData, triangleIndexs, seamVertexIndex,
                                          newMeshData, polygonToGlobalIndexMap, addVertexIndexMap,
                                          localVertexInterpolation, vertexInterpolation);

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
                newMesh.SetNormals(newMeshData.Normals.ToList());
                newMesh.SetTangents(newMeshData.Tangents.ToList());
                newMesh.SetUVs(0, newMeshData.UV.ToList());
                newMesh.SetUVs(1, newMeshData.UV2.ToList());
                newMesh.SetUVs(2, newMeshData.UV3.ToList());
                newMesh.SetUVs(3, newMeshData.UV4.ToList());
                newMesh.SetColors(newMeshData.Colors.ToList());
                newMesh.SetColors(newMeshData.Colors32.ToList());
                newMesh.boneWeights = newMeshData.BoneWeights.ToArray();
                newMesh.SetTriangles(newSubMeshTriangles, addSubMeshIndex++);
            }

            newMesh.subMeshCount = addSubMeshIndex;
            newMesh.bindposes = originalMesh.bindposes;

            CompletionBlendShapes(originalMesh, removeVerticesIndexs, newMesh, vertexInterpolation);

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
        private (MeshData, List<(int, int, float)>) addNewVertexToEdge(Mesh originalMesh, Texture2D texture,
                                                                       List<(int index, bool isRemoved)> triangleIndexs)
        {
            MeshData addMeshData = new MeshData();
            List<(int, int, float)> localVertexInterpolation = new List<(int, int, float)>();

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
                    (MeshData newMeshDataVertex, float wight) = 
                        AddEdgeIntersectionPoints(originalMesh, texture, sideIndexs[triangleIndex]);
                    if (newMeshDataVertex.Vertices.Count > 0) // テクスチャ境界値があるなら
                    {
                        // ２つの頂点と重みを保存（対象三角ポリゴンでのローカルな座標インデックスで管理）
                        localVertexInterpolation.Add((sideIndexs[triangleIndex][0], sideIndexs[triangleIndex][1], wight));
                        addMeshData.Add(newMeshDataVertex); //多角形頂点に追加   
                    }
                }
            }
            return (addMeshData, localVertexInterpolation);
        }

        // originalMeshのエッジとテクスチャの境界点を検出し、エッジ交点のUV座標と新しい頂点座標を返す関数
        private (MeshData, float) AddEdgeIntersectionPoints(Mesh originalMesh, Texture2D texture, int[] indexs)
        {
            // エッジの両端点のUV座標を取得
            Vector2 uv1 = originalMesh.uv[indexs[0]];
            Vector2 uv2 = originalMesh.uv[indexs[1]];
            MeshData newMeshDataVertex = new MeshData();
            float weight = 0;

            // エッジが境界エッジかどうかを判定
            if (IsBoundaryEdge(texture, uv1, uv2))
            {
                // 境界エッジの場合、境界点のUV座標と頂点座標を計算
                weight = FindAlphaBoundary(originalMesh, texture, indexs);
                newMeshDataVertex = VertexCompletion(originalMesh, indexs, weight);
            }

            return (newMeshDataVertex, weight);
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
            return (color1.a < alphaThreshold && color2.a > alphaThreshold) || (color1.a > alphaThreshold && color2.a < alphaThreshold);
        }

        // テクスチャのアルファ値に基づき、エッジ上の境界点のUV座標の補完用重みを求める
        private float FindAlphaBoundary(Mesh originalMesh, Texture2D texture, int[] indexs)
        {
            Vector2 uv1 = originalMesh.uv[indexs[0]];
            Vector2 uv2 = originalMesh.uv[indexs[1]];
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

            if (indexs[0] < originalMesh.normals.Length && indexs[1] < originalMesh.normals.Length)
                newMeshDataVertex.Normals.Add(Vector3.Lerp(originalMesh.normals[indexs[0]], originalMesh.normals[indexs[1]], weight));
            if (indexs[0] < originalMesh.tangents.Length && indexs[1] < originalMesh.tangents.Length)
                newMeshDataVertex.Tangents.Add(Vector3.Lerp(originalMesh.tangents[indexs[0]], originalMesh.tangents[indexs[1]], weight));
            
            if (indexs[0] < originalMesh.uv2.Length && indexs[1] < originalMesh.uv2.Length)
                newMeshDataVertex.UV2.Add(Vector2.Lerp(originalMesh.uv2[indexs[0]], originalMesh.uv2[indexs[1]], weight));
            if (indexs[0] < originalMesh.uv3.Length && indexs[1] < originalMesh.uv3.Length)
                newMeshDataVertex.UV3.Add(Vector2.Lerp(originalMesh.uv3[indexs[0]], originalMesh.uv3[indexs[1]], weight));
            if (indexs[0] < originalMesh.uv4.Length && indexs[1] < originalMesh.uv4.Length)
                newMeshDataVertex.UV4.Add(Vector2.Lerp(originalMesh.uv4[indexs[0]], originalMesh.uv4[indexs[1]], weight));
            
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

        // 追加頂点の中で重複が無いように全体メッシュへ追加する（既存頂点はシームなどで重複がある）
        private void addUniqueMeshData(MeshData addMeshData, List<(int index, bool isRemoved)> triangleIndexs,
                                       HashSet<int> seamVertexIndex,
                                       MeshData newMeshData, List<int> polygonToGlobalIndexMap, 
                                       Dictionary<Vector3, int> addVertexIndexMap,
                                       List<(int, int, float)> localVertexInterpolation,
                                       Dictionary<int, (int, int, float)> vertexInterpolation)
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
                    newMeshData.Add(addMeshData.GetElementAt(j));
                    polygonToGlobalIndexMap.Add(newMeshData.Vertices.Count - 1);
                    vertexInterpolation.Add(newMeshData.Vertices.Count - 1, localVertexInterpolation[j]);
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

                    // 補完処理の追加
                    foreach (var kvp in blendShapeInterpolation)
                    {
                        int newIndex = kvp.Key;
                        (int indexA, int indexB, float weight) = kvp.Value;

                        // 頂点の補完
                        frameVerticesList.Add(Vector3.Lerp(frameVertices[indexA], frameVertices[indexB], weight));
                        // 法線の補完
                        frameNormalsList.Add(Vector3.Lerp(frameNormals[indexA], frameNormals[indexB], weight).normalized);
                        // 接線の補完
                        frameTangentsList.Add(Vector3.Lerp(frameTangents[indexA], frameTangents[indexB], weight).normalized);
                    }

                    // 新しいメッシュにブレンドシェイプのフレームを追加
                    newMesh.AddBlendShapeFrame(blendShapeName, frameWeight, frameVerticesList.ToArray(), frameNormalsList.ToArray(), frameTangentsList.ToArray());
                }
            }
        }
    }
}
