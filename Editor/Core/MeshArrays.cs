using System.Collections.Generic;
using UnityEngine;

namespace MeshDeletionTool
{
    // ブレンドシェイプの1フレーム（差分配列の長さは頂点数と同じ）
    public class BlendShapeFrameData
    {
        public float Weight;
        public Vector3[] DeltaVertices;
        public Vector3[] DeltaNormals;
        public Vector3[] DeltaTangents;
    }

    // ブレンドシェイプ（名前と全フレーム）
    public class BlendShapeData
    {
        public string Name;
        public List<BlendShapeFrameData> Frames = new List<BlendShapeFrameData>();
    }

    // Unity の Mesh と同じ内容を配列だけで表したメッシュ
    // Mesh のプロパティは読むたびに配列をコピーし、UnityEngine のオブジェクトは Unity の外（テスト）で扱えないため、
    // 削除・細分化・再結合の処理はこのクラスに対して行い、Mesh との変換は MeshArraysUnityAdapter だけが行う
    // 頂点属性の配列は「無い（長さ0）」か「頂点数と同じ長さ」のどちらか
    public class MeshArrays
    {
        public const int UVChannelCount = 8;

        public string Name = "";

        // 元のメッシュのインデックス形式（Mesh に変換するとき、頂点数が 65,535 を超えれば UInt32 に置き換えられる）
        public UnityEngine.Rendering.IndexFormat IndexFormat = UnityEngine.Rendering.IndexFormat.UInt16;

        public Vector3[] Vertices = new Vector3[0];
        public Vector3[] Normals = new Vector3[0];
        public Vector4[] Tangents = new Vector4[0];
        public Vector2[] UV = new Vector2[0];
        public Vector2[] UV2 = new Vector2[0];
        public Vector2[] UV3 = new Vector2[0];
        public Vector2[] UV4 = new Vector2[0];
        public Vector2[] UV5 = new Vector2[0];
        public Vector2[] UV6 = new Vector2[0];
        public Vector2[] UV7 = new Vector2[0];
        public Vector2[] UV8 = new Vector2[0];
        public Color[] Colors = new Color[0];
        public BoneWeight[] BoneWeights = new BoneWeight[0];
        public Matrix4x4[] Bindposes = new Matrix4x4[0];

        // サブメッシュ毎の三角形の頂点番号（3つずつ）
        public int[][] SubMeshTriangles = new int[0][];

        public List<BlendShapeData> BlendShapes = new List<BlendShapeData>();

        // null なら Mesh に変換するときに頂点から計算される（Mesh.SetVertices / SetTriangles の既定の動作）
        public Bounds? Bounds;

        public int VertexCount => Vertices.Length;

        public int SubMeshCount => SubMeshTriangles.Length;

        // 全サブメッシュの三角形数
        public int TriangleCount
        {
            get
            {
                int count = 0;
                foreach (int[] triangles in SubMeshTriangles)
                {
                    count += triangles.Length / 3;
                }
                return count;
            }
        }

        public int[] GetTriangles(int subMeshIndex)
        {
            return SubMeshTriangles[subMeshIndex];
        }

        // 全サブメッシュの三角形を連結した配列（Mesh.triangles 相当）
        public int[] GetAllTriangles()
        {
            int[] all = new int[TriangleCount * 3];
            int offset = 0;
            foreach (int[] triangles in SubMeshTriangles)
            {
                System.Array.Copy(triangles, 0, all, offset, triangles.Length);
                offset += triangles.Length;
            }
            return all;
        }

        // UV チャンネル（0 = uv, 1 = uv2, ... 7 = uv8）
        public Vector2[] GetUV(int channel)
        {
            switch (channel)
            {
                case 0: return UV;
                case 1: return UV2;
                case 2: return UV3;
                case 3: return UV4;
                case 4: return UV5;
                case 5: return UV6;
                case 6: return UV7;
                case 7: return UV8;
                default: throw new System.ArgumentOutOfRangeException(nameof(channel));
            }
        }

        public void SetUV(int channel, Vector2[] uv)
        {
            uv = uv ?? new Vector2[0];
            switch (channel)
            {
                case 0: UV = uv; break;
                case 1: UV2 = uv; break;
                case 2: UV3 = uv; break;
                case 3: UV4 = uv; break;
                case 4: UV5 = uv; break;
                case 5: UV6 = uv; break;
                case 6: UV7 = uv; break;
                case 7: UV8 = uv; break;
                default: throw new System.ArgumentOutOfRangeException(nameof(channel));
            }
        }

        // 頂点カラーを Mesh に書き込んだときと同じ 8 ビットの値に丸める
        // （Mesh の頂点カラーは 8 ビットで保存されるため、Mesh を経由していた従来の処理では各段階の出力がこの精度になっていた）
        public void QuantizeColors()
        {
            for (int i = 0; i < Colors.Length; i++)
            {
                Colors[i] = (Color)(Color32)Colors[i];
            }
        }

        // 頂点の座標から境界ボックスを計算する（Mesh.RecalculateBounds 相当）
        public Bounds CalculateBounds()
        {
            if (Vertices.Length == 0)
            {
                return new Bounds();
            }
            Vector3 min = Vertices[0], max = Vertices[0];
            for (int i = 1; i < Vertices.Length; i++)
            {
                min = Vector3.Min(min, Vertices[i]);
                max = Vector3.Max(max, Vertices[i]);
            }
            Bounds bounds = new Bounds();
            bounds.SetMinMax(min, max);
            return bounds;
        }
    }
}
