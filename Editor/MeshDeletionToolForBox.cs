using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace MeshDeletionTool
{
    public class MeshDeletionToolForBox : MeshDeletionTool
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
            if (!ValidateInputs(targetRenderer, deletionBoundsObject))
                return;

            Bounds deletionBounds = GetObjectBounds(deletionBoundsObject);
            Mesh originalMesh = GetOriginalMesh(targetRenderer);
            if (originalMesh == null)
                return;

            List<int> removeVerticesIndexs = GetVerticesToRemove(targetRenderer, originalMesh, deletionBounds);
            Mesh newMesh = CreateMeshAfterVertexRemoval(originalMesh, removeVerticesIndexs);
            SaveNewMesh(newMesh);
        }

        private bool ValidateInputs(Renderer targetRenderer, GameObject deletionBoundsObject)
        {
            if (targetRenderer == null)
            {
                Debug.LogError("対象オブジェクトが選択されていません！");
                return false;
            }

            if (deletionBoundsObject == null)
            {
                Debug.LogError("削除範囲オブジェクトが選択されていません！");
                return false;
            }

            return true;
        }
    }
}
