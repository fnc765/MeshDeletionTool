using System;
using UnityEngine;

namespace MeshDeletionTool
{
    // 1 テクセルの大きさ（mm）の概算（ウィンドウとインスペクターで共用。「≈ 0.5〜0.9 mm 単位で輪郭に沿わせます」の表示に使う）
    // サブメッシュ毎に sqrt(3D 面積の合計 / テクセル空間での UV 面積の合計) を求め、処理対象のサブメッシュの範囲（最小〜最大）を返す
    internal sealed class TexelSizeEstimator
    {
        // 直前に計算したメッシュとテクスチャ解像度（同じなら計算し直さない）
        private Mesh cacheMesh;
        private Vector2Int[] cacheTextureSizes;
        private float[] cacheMillimeters;

        // 処理対象（isTarget）のサブメッシュの 1 テクセルの大きさ（mm）の範囲。対象が無ければテクスチャを持つ全サブメッシュ。求められなければ false
        public bool TryGetRange(Mesh mesh, Material[] materials, Func<int, bool> isTarget, out float min, out float max)
        {
            min = 0f;
            max = 0f;
            if (mesh == null || materials == null)
                return false;
            Vector2Int[] textureSizes = GetTextureSizes(mesh.subMeshCount, materials);
            if (cacheMesh != mesh || cacheTextureSizes == null || !TextureSizesEqual(cacheTextureSizes, textureSizes))
            {
                cacheMesh = mesh;
                cacheTextureSizes = textureSizes;
                cacheMillimeters = ComputeTexelSizeMillimeters(mesh, textureSizes);
            }
            min = float.MaxValue;
            for (int pass = 0; pass < 2 && max == 0f; pass++)
            {
                for (int subMeshIndex = 0; subMeshIndex < cacheMillimeters.Length; subMeshIndex++)
                {
                    float size = cacheMillimeters[subMeshIndex];
                    if ((pass == 0 && !isTarget(subMeshIndex)) || size <= 0f)
                        continue;
                    min = Mathf.Min(min, size);
                    max = Mathf.Max(max, size);
                }
            }
            if (max == 0f)
            {
                min = 0f;
                return false;
            }
            return true;
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
    }
}
