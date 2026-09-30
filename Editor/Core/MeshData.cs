using System.Collections.Generic;
using UnityEngine;

namespace MeshDeletionTool
{
    // 頂点属性を1頂点ずつ集めるためのリストの束（Mesh には依存しない）
    // 各リストは「無い（空）」か「頂点数と同じ長さ」のどちらかで、元のメッシュに無い属性は追加されない
    public class MeshData
    {
        public List<Vector3> Vertices { get; set; } = new List<Vector3>();
        public List<Vector3> Normals { get; set; } = new List<Vector3>();
        public List<Vector4> Tangents { get; set; } = new List<Vector4>();
        public List<Vector2> UV { get; set; } = new List<Vector2>();
        public List<Vector2> UV2 { get; set; } = new List<Vector2>();
        public List<Vector2> UV3 { get; set; } = new List<Vector2>();
        public List<Vector2> UV4 { get; set; } = new List<Vector2>();
        public List<Vector2> UV5 { get; set; } = new List<Vector2>();
        public List<Vector2> UV6 { get; set; } = new List<Vector2>();
        public List<Vector2> UV7 { get; set; } = new List<Vector2>();
        public List<Vector2> UV8 { get; set; } = new List<Vector2>();
        public List<Color> Colors { get; set; } = new List<Color>();
        public List<BoneWeight> BoneWeights { get; set; } = new List<BoneWeight>();

        public void Add(MeshData meshData)
        {
            if (meshData == null)
            {
                return;
            }

            if (meshData.Vertices != null && meshData.Vertices.Count > 0)
            {
                this.Vertices.AddRange(meshData.Vertices);
            }

            if (meshData.Normals != null && meshData.Normals.Count > 0)
            {
                this.Normals.AddRange(meshData.Normals);
            }

            if (meshData.Tangents != null && meshData.Tangents.Count > 0)
            {
                this.Tangents.AddRange(meshData.Tangents);
            }

            if (meshData.UV != null && meshData.UV.Count > 0)
            {
                this.UV.AddRange(meshData.UV);
            }

            if (meshData.UV2 != null && meshData.UV2.Count > 0)
            {
                this.UV2.AddRange(meshData.UV2);
            }

            if (meshData.UV3 != null && meshData.UV3.Count > 0)
            {
                this.UV3.AddRange(meshData.UV3);
            }

            if (meshData.UV4 != null && meshData.UV4.Count > 0)
            {
                this.UV4.AddRange(meshData.UV4);
            }

            if (meshData.UV5 != null && meshData.UV5.Count > 0)
            {
                this.UV5.AddRange(meshData.UV5);
            }

            if (meshData.UV6 != null && meshData.UV6.Count > 0)
            {
                this.UV6.AddRange(meshData.UV6);
            }

            if (meshData.UV7 != null && meshData.UV7.Count > 0)
            {
                this.UV7.AddRange(meshData.UV7);
            }

            if (meshData.UV8 != null && meshData.UV8.Count > 0)
            {
                this.UV8.AddRange(meshData.UV8);
            }

            if (meshData.Colors != null && meshData.Colors.Count > 0)
            {
                this.Colors.AddRange(meshData.Colors);
            }

            if (meshData.BoneWeights != null && meshData.BoneWeights.Count > 0)
            {
                this.BoneWeights.AddRange(meshData.BoneWeights);
            }
        }

        // 別の MeshData の index 番目の頂点の属性を追加する（途中の MeshData を作らない）
        public void AddElementAt(MeshData source, int index)
        {
            if (index < source.Vertices.Count) Vertices.Add(source.Vertices[index]);
            if (index < source.Normals.Count) Normals.Add(source.Normals[index]);
            if (index < source.Tangents.Count) Tangents.Add(source.Tangents[index]);
            if (index < source.UV.Count) UV.Add(source.UV[index]);
            if (index < source.UV2.Count) UV2.Add(source.UV2[index]);
            if (index < source.UV3.Count) UV3.Add(source.UV3[index]);
            if (index < source.UV4.Count) UV4.Add(source.UV4[index]);
            if (index < source.UV5.Count) UV5.Add(source.UV5[index]);
            if (index < source.UV6.Count) UV6.Add(source.UV6[index]);
            if (index < source.UV7.Count) UV7.Add(source.UV7[index]);
            if (index < source.UV8.Count) UV8.Add(source.UV8[index]);
            if (index < source.Colors.Count) Colors.Add(source.Colors[index]);
            if (index < source.BoneWeights.Count) BoneWeights.Add(source.BoneWeights[index]);
        }

        // メッシュの index 番目の頂点の属性を追加する（メッシュに無い属性は追加しない）
        public void AddElementFromMesh(MeshArrays mesh, int index)
        {
            if (mesh == null || index < 0)
            {
                return;
            }

            if (index < mesh.VertexCount)
            {
                Vertices.Add(mesh.Vertices[index]);
                if (mesh.Normals.Length > index)
                {
                    Normals.Add(mesh.Normals[index]);
                }
                if (mesh.Tangents.Length > index)
                {
                    Tangents.Add(mesh.Tangents[index]);
                }
                if (mesh.UV.Length > index)
                {
                    UV.Add(mesh.UV[index]);
                }
                if (mesh.UV2.Length > index)
                {
                    UV2.Add(mesh.UV2[index]);
                }
                if (mesh.UV3.Length > index)
                {
                    UV3.Add(mesh.UV3[index]);
                }
                if (mesh.UV4.Length > index)
                {
                    UV4.Add(mesh.UV4[index]);
                }
                if (mesh.UV5.Length > index)
                {
                    UV5.Add(mesh.UV5[index]);
                }
                if (mesh.UV6.Length > index)
                {
                    UV6.Add(mesh.UV6[index]);
                }
                if (mesh.UV7.Length > index)
                {
                    UV7.Add(mesh.UV7[index]);
                }
                if (mesh.UV8.Length > index)
                {
                    UV8.Add(mesh.UV8[index]);
                }
                if (mesh.Colors.Length > index)
                {
                    Colors.Add(mesh.Colors[index]);
                }
                if (mesh.BoneWeights.Length > index)
                {
                    BoneWeights.Add(mesh.BoneWeights[index]);
                }
            }
        }

        // 集めた頂点属性を配列として MeshArrays に書き込む（三角形・ブレンドシェイプ・ボーンの姿勢は変えない）
        public void CopyTo(MeshArrays mesh)
        {
            mesh.Vertices = Vertices.ToArray();
            mesh.Normals = Normals.ToArray();
            mesh.Tangents = Tangents.ToArray();
            mesh.UV = UV.ToArray();
            mesh.UV2 = UV2.ToArray();
            mesh.UV3 = UV3.ToArray();
            mesh.UV4 = UV4.ToArray();
            mesh.UV5 = UV5.ToArray();
            mesh.UV6 = UV6.ToArray();
            mesh.UV7 = UV7.ToArray();
            mesh.UV8 = UV8.ToArray();
            mesh.Colors = Colors.ToArray();
            mesh.BoneWeights = BoneWeights.ToArray();
        }
    }
}
