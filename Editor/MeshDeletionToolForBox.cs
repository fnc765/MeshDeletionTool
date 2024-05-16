using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace MeshDeletionToolForBox
{
    public class MeshDeletionToolForBox : EditorWindow
    {
        internal Renderer targetRenderer;
        private GameObject deletionBoundsObject;

        [MenuItem("Tools/MeshDeletionToolForBox")]
        private static void Init()
        {
            MeshDeletionToolForBox window = (MeshDeletionToolForBox)EditorWindow.GetWindow(typeof(MeshDeletionToolForBox));
            window.titleContent = new GUIContent("MeshDeletionToolForBox");
            window.Show();
        }

        private void OnGUI()
        {
            GUILayout.Label("オブジェクトと削除範囲を選択", EditorStyles.boldLabel);

            targetRenderer = EditorGUILayout.ObjectField("対象オブジェクト", targetRenderer, typeof(Renderer), true) as Renderer;
            deletionBoundsObject = EditorGUILayout.ObjectField("削除範囲オブジェクト", deletionBoundsObject, typeof(GameObject), true) as GameObject;

            if (GUILayout.Button("削除範囲内のメッシュを削除"))
            {
                DeleteMeshesInBounds();
            }
        }

        private void DeleteMeshesInBounds()
        {
            if (targetRenderer == null)
            {
                Debug.LogError("対象オブジェクトが選択されていません！");
                return;
            }

            if (deletionBoundsObject == null)
            {
                Debug.LogError("削除範囲オブジェクトが選択されていません！");
                return;
            }

            // 削除範囲オブジェクトから境界ボックスを取得
            Bounds deletionBounds = GetObjectBounds(deletionBoundsObject);
            

            Mesh originalMesh = null;
            if (targetRenderer is SkinnedMeshRenderer skinnedMeshRenderer)
            {
                originalMesh = skinnedMeshRenderer.sharedMesh;
            }
            else if (targetRenderer is MeshRenderer meshRenderer)
            {
                MeshFilter meshFilter = targetRenderer.GetComponent<MeshFilter>();
                if (meshFilter != null)
                {
                    originalMesh = meshFilter.sharedMesh;
                }
            }

            if (originalMesh == null)
            {
                Debug.LogError("対象オブジェクトに有効な SkinnedMeshRenderer または MeshRenderer コンポーネントがありません！");
                return;
            }

            List<int> removeVerticesIndexs = new List<int>();
            List<Vector3> newVerticesList = new List<Vector3>();
            List<Vector3> newNormalsList = new List<Vector3>();
            List<Vector4> newTangentsList = new List<Vector4>();
            List<Vector2> newUvList = new List<Vector2>();
            List<BoneWeight> newBoneWeight = new List<BoneWeight>();
            List<int> newTrianglesList = new List<int>();

            Mesh newMesh = new Mesh();

            // 削除する頂点のindex番号のリスト
            for (int index = 0; index < originalMesh.vertices.Length; index++) {
                Vector3 verticesWorldPoints = targetRenderer.transform.TransformPoint(originalMesh.vertices[index]);
                if (deletionBounds.Contains(verticesWorldPoints) ) {
                    removeVerticesIndexs.Add(index);
                }
            }
            // 削除する頂点のindex番号のリストを降順でソートする
            // 既存配列から削除していく際に、大きいindex番号からでないとずれていくため
            removeVerticesIndexs.Sort((a, b) => b - a);

            // 頂点、法線、接線、UV、ボーンウェイトをコピーする
            for (int index = 0; index < originalMesh.vertices.Length; index++) {
                if (removeVerticesIndexs.Contains(index))
                    continue;
                newVerticesList.Add(originalMesh.vertices[index]);
                newNormalsList.Add(originalMesh.normals[index]);
                newTangentsList.Add(originalMesh.tangents[index]);
                newUvList.Add(originalMesh.uv[index]);
                newBoneWeight.Add(originalMesh.boneWeights[index]);
            }

            newMesh.vertices = newVerticesList.ToArray();
            newMesh.normals = newNormalsList.ToArray();
            newMesh.tangents = newTangentsList.ToArray();
            newMesh.uv = newUvList.ToArray();
            newMesh.uv2 = originalMesh.uv2;
            newMesh.uv3 = originalMesh.uv3;
            newMesh.uv4 = originalMesh.uv4;
            newMesh.boneWeights = newBoneWeight.ToArray();

            // 色情報をコピーする
            if (originalMesh.colors != null && originalMesh.colors.Length > 0)
            {
                newMesh.colors = originalMesh.colors;
            }
            else if (originalMesh.colors32 != null && originalMesh.colors32.Length > 0)
            {
                newMesh.colors32 = originalMesh.colors32;
            }

            // 三角形情報をコピーする
            for (int index = 0; index < originalMesh.triangles.Length; index += 3) {
                int index0 = originalMesh.triangles[index];
                int index1 = originalMesh.triangles[index + 1];
                int index2 = originalMesh.triangles[index + 2];

                // いずれかの頂点が削除されていない三角形を新しいメッシュに追加
                if (!removeVerticesIndexs.Contains(index0) &&
                    !removeVerticesIndexs.Contains(index1) &&
                    !removeVerticesIndexs.Contains(index2)) {
                    // 削除されなかった頂点の新しいインデックスを取得
                    int newIndex0 = newVerticesList.IndexOf(originalMesh.vertices[index0]);
                    int newIndex1 = newVerticesList.IndexOf(originalMesh.vertices[index1]);
                    int newIndex2 = newVerticesList.IndexOf(originalMesh.vertices[index2]);

                    // 新しいインデックスで三角形を追加
                    newTrianglesList.Add(newIndex0);
                    newTrianglesList.Add(newIndex1);
                    newTrianglesList.Add(newIndex2);
                }
            }
            newMesh.triangles = newTrianglesList.ToArray();

            // 元のメッシュからサブメッシュ情報を取得
            int subMeshCount = originalMesh.subMeshCount;
            newMesh.subMeshCount = subMeshCount;
            for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
            {
                // サブメッシュの三角形を取得
                int[] originalTriangles = originalMesh.GetTriangles(subMeshIndex);

                // 新しいメッシュに追加する三角形のインデックスを設定する
                List<int> newTriangles = new List<int>();
                for (int index = 0; index < originalTriangles.Length; index += 3) {
                    int index0 = originalTriangles[index];
                    int index1 = originalTriangles[index + 1];
                    int index2 = originalTriangles[index + 2];

                    // いずれかの頂点が削除されていない三角形を新しいメッシュに追加
                    if (!removeVerticesIndexs.Contains(index0) &&
                        !removeVerticesIndexs.Contains(index1) &&
                        !removeVerticesIndexs.Contains(index2)) {
                        // 削除されなかった頂点の新しいインデックスを取得
                        int newIndex0 = newVerticesList.IndexOf(originalMesh.vertices[index0]);
                        int newIndex1 = newVerticesList.IndexOf(originalMesh.vertices[index1]);
                        int newIndex2 = newVerticesList.IndexOf(originalMesh.vertices[index2]);
                        // 新しいインデックスで三角形を追加
                        newTriangles.Add(newIndex0);
                        newTriangles.Add(newIndex1);
                        newTriangles.Add(newIndex2);
                    }
                }

                // 新しいメッシュにサブメッシュ情報がある場合にのみ、三角形を追加する
                if (newTriangles.Count > 0) {
                    // 新しいメッシュにサブメッシュ情報を設定
                    newMesh.SetTriangles(newTriangles.ToArray(), subMeshIndex);
                }

                
            }

            // ブレンドシェイプをコピーする
            for (int i = 0; i < originalMesh.blendShapeCount; i++)
            {
                string blendShapeName = originalMesh.GetBlendShapeName(i);
                int frameCount = originalMesh.GetBlendShapeFrameCount(i);
                for (int j = 0; j < frameCount; j++)
                {
                    float frameWeight = originalMesh.GetBlendShapeFrameWeight(i, j);
                    Vector3[] frameVertices = new Vector3[originalMesh.vertexCount];
                    Vector3[] frameNormals = new Vector3[originalMesh.vertexCount];
                    Vector3[] frameTangents = new Vector3[originalMesh.vertexCount];
                    originalMesh.GetBlendShapeFrameVertices(i, j, frameVertices, frameNormals, frameTangents);
                    
                    List<Vector3> frameVerticesList = new List<Vector3>(frameVertices);
                    List<Vector3> frameNormalsList = new List<Vector3>(frameNormals);
                    List<Vector3> frameTangentsList = new List<Vector3>(frameTangents);
                    foreach (int index in removeVerticesIndexs) {
                        frameVerticesList.RemoveAt(index);
                        frameNormalsList.RemoveAt(index);
                        frameTangentsList.RemoveAt(index);
                    }
                    newMesh.AddBlendShapeFrame(blendShapeName, frameWeight, frameVerticesList.ToArray(),
                                                frameNormalsList.ToArray(), frameTangentsList.ToArray());
                }
            }

            // バインドポーズをコピーする
            newMesh.bindposes = originalMesh.bindposes;

            // 新しいメッシュをアセットとして保存
            AssetDatabase.CreateAsset(newMesh, "Assets/NewMesh.asset");
            AssetDatabase.SaveAssets();
        }

        // オブジェクトの境界ボックスを取得するヘルパーメソッド
        private Bounds GetObjectBounds(GameObject obj)
        {
            // 指定されたオブジェクトとその子孫オブジェクトに含まれるRendererコンポーネントの配列を取得
            Renderer[] renderers = obj.GetComponentsInChildren<Renderer>();

            // 新しい境界ボックスを作成
            Bounds bounds = new Bounds();

            bool boundsInitialized = false; // 境界ボックスが初期化されたかどうかを示すフラグ

            // 各Rendererコンポーネントの境界ボックスを考慮して、全体の境界ボックスを計算
            foreach (Renderer renderer in renderers)
            {
                // Rendererが描画するオブジェクトの境界ボックスを取得
                Bounds rendererBounds = renderer.bounds;

                // 境界ボックスが初期化されていない場合、Rendererの境界ボックスをそのまま設定
                if (!boundsInitialized)
                {
                    bounds = rendererBounds;
                    boundsInitialized = true;
                }
                else
                {
                    // 既存の境界ボックスとRendererの境界ボックスを合成して新しい境界ボックスを設定
                    bounds.Encapsulate(rendererBounds.min);
                    bounds.Encapsulate(rendererBounds.max);
                }
            }

            // 計算された全体の境界ボックスを返す
            return bounds;
        }
    }

}