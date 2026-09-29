using System.Collections.Generic;
using UnityEngine;

namespace MeshDeletionTool
{
    // Mesh / Texture2D と、Unity に依存しない MeshArrays / AlphaMask との変換
    // Mesh のプロパティは呼び出し毎に配列をコピーするため、それぞれ一度だけ読む。処理本体はここで作った配列に対して行う
    public static class MeshArraysUnityAdapter
    {
        // Mesh の全ての頂点属性・サブメッシュ・ブレンドシェイプを配列に読み出す
        public static MeshArrays FromMesh(Mesh mesh)
        {
            MeshArrays arrays = new MeshArrays
            {
                Name = mesh.name,
                IndexFormat = mesh.indexFormat,
                Vertices = mesh.vertices,
                Normals = mesh.normals,
                Tangents = mesh.tangents,
                UV = mesh.uv,
                UV2 = mesh.uv2,
                UV3 = mesh.uv3,
                UV4 = mesh.uv4,
                UV5 = mesh.uv5,
                UV6 = mesh.uv6,
                UV7 = mesh.uv7,
                UV8 = mesh.uv8,
                Colors = mesh.colors,
                BoneWeights = mesh.boneWeights,
                Bindposes = mesh.bindposes,
                Bounds = mesh.bounds
            };

            int subMeshCount = mesh.subMeshCount;
            arrays.SubMeshTriangles = new int[subMeshCount][];
            for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
            {
                arrays.SubMeshTriangles[subMeshIndex] = mesh.GetTriangles(subMeshIndex);
            }

            int vertexCount = arrays.VertexCount;
            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                BlendShapeData shape = new BlendShapeData { Name = mesh.GetBlendShapeName(i) };
                int frameCount = mesh.GetBlendShapeFrameCount(i);
                for (int j = 0; j < frameCount; j++)
                {
                    // 頂点数0のフレームも、GetBlendShapeFrameVertices は全頂点分の（ゼロの）差分を返す
                    BlendShapeFrameData frame = new BlendShapeFrameData
                    {
                        Weight = mesh.GetBlendShapeFrameWeight(i, j),
                        DeltaVertices = new Vector3[vertexCount],
                        DeltaNormals = new Vector3[vertexCount],
                        DeltaTangents = new Vector3[vertexCount]
                    };
                    mesh.GetBlendShapeFrameVertices(i, j, frame.DeltaVertices, frame.DeltaNormals, frame.DeltaTangents);
                    shape.Frames.Add(frame);
                }
                arrays.BlendShapes.Add(shape);
            }
            return arrays;
        }

        // 配列から新しい Mesh を作る。無い（長さ0の）頂点属性は設定しない
        // 頂点数が 65,535 を超える場合は 16 ビットのインデックスでは参照できないため 32 ビットにする
        public static Mesh ToMesh(MeshArrays arrays)
        {
            Mesh mesh = new Mesh();
            mesh.name = arrays.Name;
            mesh.indexFormat = arrays.VertexCount > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : arrays.IndexFormat;
            mesh.SetVertices(new List<Vector3>(arrays.Vertices));
            if (arrays.Normals.Length > 0) mesh.SetNormals(new List<Vector3>(arrays.Normals));
            if (arrays.Tangents.Length > 0) mesh.SetTangents(new List<Vector4>(arrays.Tangents));
            for (int channel = 0; channel < MeshArrays.UVChannelCount; channel++)
            {
                Vector2[] uv = arrays.GetUV(channel);
                if (uv.Length > 0) mesh.SetUVs(channel, new List<Vector2>(uv));
            }
            if (arrays.Colors.Length > 0) mesh.SetColors(new List<Color>(arrays.Colors));
            if (arrays.BoneWeights.Length > 0) mesh.boneWeights = arrays.BoneWeights;
            mesh.bindposes = arrays.Bindposes;

            mesh.subMeshCount = arrays.SubMeshCount;
            for (int subMeshIndex = 0; subMeshIndex < arrays.SubMeshCount; subMeshIndex++)
            {
                mesh.SetTriangles(arrays.SubMeshTriangles[subMeshIndex], subMeshIndex);
            }
            if (arrays.Bounds.HasValue)
            {
                mesh.bounds = arrays.Bounds.Value;
            }

            foreach (BlendShapeData shape in arrays.BlendShapes)
            {
                foreach (BlendShapeFrameData frame in shape.Frames)
                {
                    mesh.AddBlendShapeFrame(shape.Name, frame.Weight, frame.DeltaVertices, frame.DeltaNormals, frame.DeltaTangents);
                }
            }
            return mesh;
        }

        // テクスチャのアルファ値を一度だけ読み出す（テクスチャは読み取り可能である必要がある）
        public static AlphaMask FromTexture(Texture2D texture)
        {
            AlphaMask mask = AlphaMask.FromPixels(texture.GetPixels32(), texture.width, texture.height);
            mask.WrapModeU = texture.wrapModeU;
            mask.WrapModeV = texture.wrapModeV;
            return mask;
        }
    }
}
