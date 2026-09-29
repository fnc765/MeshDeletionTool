using UnityEngine;

namespace MeshDeletionTool
{
    // 2 つの MeshArrays が同じ内容か（ビット単位）を調べ、最初に異なる箇所を説明する文字列を返す（同じなら null）
    // バックエンド（CPU / GPU）の結果の一致確認に使う。頂点座標・UV・三角形に加え、法線・接線・ブレンドシェイプも比べる
    public static class MeshArraysComparer
    {
        public static string FirstDifference(MeshArrays a, MeshArrays b)
        {
            if (a.VertexCount != b.VertexCount)
                return "頂点数が異なります: " + a.VertexCount + " と " + b.VertexCount;
            if (a.SubMeshCount != b.SubMeshCount)
                return "サブメッシュ数が異なります: " + a.SubMeshCount + " と " + b.SubMeshCount;
            for (int i = 0; i < a.VertexCount; i++)
            {
                if (!Same(a.Vertices[i], b.Vertices[i]))
                    return "頂点 #" + i + " の座標が異なります: " + a.Vertices[i].ToString("R") + " と " + b.Vertices[i].ToString("R");
            }
            string difference = CompareUV(a, b);
            if (difference != null)
                return difference;
            for (int subMeshIndex = 0; subMeshIndex < a.SubMeshCount; subMeshIndex++)
            {
                int[] ta = a.GetTriangles(subMeshIndex), tb = b.GetTriangles(subMeshIndex);
                if (ta.Length != tb.Length)
                    return "サブメッシュ " + subMeshIndex + " の三角形数が異なります: " + ta.Length / 3 + " と " + tb.Length / 3;
                for (int i = 0; i < ta.Length; i++)
                {
                    if (ta[i] != tb[i])
                        return "サブメッシュ " + subMeshIndex + " の三角形 #" + i / 3 + " の頂点番号が異なります: " + ta[i] + " と " + tb[i];
                }
            }
            difference = CompareVector3(a.Normals, b.Normals, "法線");
            if (difference != null)
                return difference;
            if (a.Tangents.Length != b.Tangents.Length)
                return "接線の数が異なります";
            for (int i = 0; i < a.Tangents.Length; i++)
            {
                if (!Same(a.Tangents[i], b.Tangents[i]))
                    return "頂点 #" + i + " の接線が異なります";
            }
            if (a.BlendShapes.Count != b.BlendShapes.Count)
                return "ブレンドシェイプ数が異なります";
            for (int s = 0; s < a.BlendShapes.Count; s++)
            {
                if (a.BlendShapes[s].Frames.Count != b.BlendShapes[s].Frames.Count)
                    return "ブレンドシェイプ " + a.BlendShapes[s].Name + " のフレーム数が異なります";
                for (int f = 0; f < a.BlendShapes[s].Frames.Count; f++)
                {
                    difference = CompareVector3(a.BlendShapes[s].Frames[f].DeltaVertices, b.BlendShapes[s].Frames[f].DeltaVertices, "ブレンドシェイプ " + a.BlendShapes[s].Name + " の頂点差分");
                    if (difference != null)
                        return difference;
                }
            }
            return null;
        }

        // 座標が異なる最初の頂点番号（頂点数が同じで座標に差が無ければ -1。診断用）
        public static int FirstDifferentVertex(MeshArrays a, MeshArrays b)
        {
            if (a.VertexCount != b.VertexCount)
                return -1;
            for (int i = 0; i < a.VertexCount; i++)
            {
                if (!Same(a.Vertices[i], b.Vertices[i]))
                    return i;
            }
            return -1;
        }

        private static string CompareUV(MeshArrays a, MeshArrays b)
        {
            for (int channel = 0; channel < MeshArrays.UVChannelCount; channel++)
            {
                Vector2[] ua = a.GetUV(channel), ub = b.GetUV(channel);
                if (ua.Length != ub.Length)
                    return "uv" + (channel + 1) + " の数が異なります";
                for (int i = 0; i < ua.Length; i++)
                {
                    if (!Same(ua[i], ub[i]))
                        return "頂点 #" + i + " の uv" + (channel + 1) + " が異なります: " + ua[i].ToString("R") + " と " + ub[i].ToString("R");
                }
            }
            return null;
        }

        private static string CompareVector3(Vector3[] a, Vector3[] b, string what)
        {
            if (a.Length != b.Length)
                return what + "の数が異なります";
            for (int i = 0; i < a.Length; i++)
            {
                if (!Same(a[i], b[i]))
                    return "頂点 #" + i + " の" + what + "が異なります";
            }
            return null;
        }

        // float をビット単位で比べる（NaN 同士も同じ扱い）
        private static bool Same(float x, float y)
        {
            return System.BitConverter.ToInt32(System.BitConverter.GetBytes(x), 0) == System.BitConverter.ToInt32(System.BitConverter.GetBytes(y), 0);
        }

        private static bool Same(Vector2 x, Vector2 y) => Same(x.x, y.x) && Same(x.y, y.y);
        private static bool Same(Vector3 x, Vector3 y) => Same(x.x, y.x) && Same(x.y, y.y) && Same(x.z, y.z);
        private static bool Same(Vector4 x, Vector4 y) => Same(x.x, y.x) && Same(x.y, y.y) && Same(x.z, y.z) && Same(x.w, y.w);
    }
}
