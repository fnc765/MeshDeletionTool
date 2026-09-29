using UnityEngine;

namespace MeshDeletionTool
{
    // 辺上に追加する頂点の法線・接線を2頂点から補間するための共通処理
    // （境界の細分化の中点頂点と、削除処理の境界点頂点で同じ規則を使う）
    public static class VertexAttributeUtils
    {
        // 法線を線形補間して正規化する。打ち消し合って零になる場合は先頭側の法線を使う
        public static Vector3 LerpNormal(Vector3 normalA, Vector3 normalB, float t)
        {
            Vector3 normal = Vector3.Lerp(normalA, normalB, t);
            return normal.sqrMagnitude > 0f ? normal.normalized : normalA;
        }

        // 接線を補間する。xyz は線形補間した後、補間後の法線と直交させて正規化する（グラム・シュミット）
        // w（従法線の向き、±1）は補間できないため、t に近い側の端点の値を使う（同じ距離なら先頭側）。0 にはならない
        public static Vector4 LerpTangent(Vector4 tangentA, Vector4 tangentB, float t, Vector3 normal)
        {
            Vector3 xyzA = new Vector3(tangentA.x, tangentA.y, tangentA.z);
            Vector3 xyzB = new Vector3(tangentB.x, tangentB.y, tangentB.z);
            Vector3 xyz = Orthogonalize(Vector3.Lerp(xyzA, xyzB, t), normal);
            if (xyz.sqrMagnitude <= 1e-12f)
            {
                // 打ち消し合った、または法線と平行: 端点の接線を使い、それも無理なら法線に直交する任意の向き
                xyz = Orthogonalize(t <= 0.5f ? xyzA : xyzB, normal);
                if (xyz.sqrMagnitude <= 1e-12f)
                    xyz = Orthogonalize(t <= 0.5f ? xyzB : xyzA, normal);
                if (xyz.sqrMagnitude <= 1e-12f)
                    xyz = normal.sqrMagnitude > 0f ? Vector3.Cross(normal, Mathf.Abs(normal.y) < 0.9f ? Vector3.up : Vector3.right) : Vector3.right;
            }
            xyz.Normalize();

            float w = t <= 0.5f ? tangentA.w : tangentB.w;
            if (w == 0f)
                w = t <= 0.5f ? tangentB.w : tangentA.w;
            if (w == 0f)
                w = 1f;
            return new Vector4(xyz.x, xyz.y, xyz.z, w < 0f ? -1f : 1f);
        }

        // ベクトルから法線方向の成分を取り除く（法線が零なら何もしない）
        private static Vector3 Orthogonalize(Vector3 vector, Vector3 normal)
        {
            float normalLength = normal.sqrMagnitude;
            if (normalLength <= 0f)
                return vector;
            return vector - normal * (Vector3.Dot(vector, normal) / normalLength);
        }
    }
}
