using UnityEngine;
using NUnit.Framework;
using MeshDeletionTool;

// VertexAttributeUtils（辺上の頂点の法線・接線の補間）のテスト
public class VertexAttributeUtilsTest
{
    [Test]
    public void LerpNormal_IsNormalized()
    {
        Vector3 a = Vector3.forward;
        Vector3 b = Vector3.right;
        Vector3 result = VertexAttributeUtils.LerpNormal(a, b, 0.5f);
        Assert.AreEqual(1f, result.magnitude, 1e-5f);
        Assert.AreEqual(0f, Vector3.Angle(result, new Vector3(1f, 0f, 1f)), 1e-3f);
    }

    [Test]
    public void LerpNormal_OppositeNormals_FallBackToFirst()
    {
        Vector3 result = VertexAttributeUtils.LerpNormal(Vector3.up, Vector3.down, 0.5f);
        Assert.AreEqual(Vector3.up, result);
    }

    [Test]
    public void LerpTangent_IsUnitLengthAndOrthogonalToNormal()
    {
        Vector3 normal = new Vector3(0.2f, 0.1f, 1f).normalized;
        Vector4 a = new Vector4(1f, 0f, 0f, 1f);
        Vector4 b = new Vector4(0f, 1f, 0f, 1f);
        Vector4 result = VertexAttributeUtils.LerpTangent(a, b, 0.3f, normal);
        Vector3 xyz = new Vector3(result.x, result.y, result.z);
        Assert.AreEqual(1f, xyz.magnitude, 1e-5f);
        Assert.AreEqual(0f, Vector3.Dot(xyz, normal), 1e-5f);
        Assert.AreEqual(1f, result.w);
    }

    [Test]
    public void LerpTangent_HandednessOfNearerEndpoint()
    {
        Vector4 a = new Vector4(1f, 0f, 0f, 1f);
        Vector4 b = new Vector4(1f, 0f, 0f, -1f);
        Assert.AreEqual(1f, VertexAttributeUtils.LerpTangent(a, b, 0.25f, Vector3.forward).w);
        Assert.AreEqual(-1f, VertexAttributeUtils.LerpTangent(a, b, 0.75f, Vector3.forward).w);
        // 同じ距離なら先頭側
        Assert.AreEqual(1f, VertexAttributeUtils.LerpTangent(a, b, 0.5f, Vector3.forward).w);
        Assert.AreEqual(-1f, VertexAttributeUtils.LerpTangent(b, a, 0.5f, Vector3.forward).w);
    }

    [Test]
    public void LerpTangent_HandednessIsNeverZero()
    {
        Vector4 a = new Vector4(1f, 0f, 0f, 0f);
        Vector4 b = new Vector4(0f, 1f, 0f, -1f);
        Assert.AreEqual(-1f, VertexAttributeUtils.LerpTangent(a, b, 0.25f, Vector3.forward).w);
        Assert.AreEqual(1f, VertexAttributeUtils.LerpTangent(a, new Vector4(0f, 1f, 0f, 0f), 0.5f, Vector3.forward).w);
    }

    [Test]
    public void LerpTangent_OppositeTangents_StillUnitLength()
    {
        Vector4 a = new Vector4(1f, 0f, 0f, 1f);
        Vector4 b = new Vector4(-1f, 0f, 0f, 1f);
        Vector4 result = VertexAttributeUtils.LerpTangent(a, b, 0.5f, Vector3.forward);
        Vector3 xyz = new Vector3(result.x, result.y, result.z);
        Assert.AreEqual(1f, xyz.magnitude, 1e-5f);
        Assert.AreEqual(0f, Vector3.Dot(xyz, Vector3.forward), 1e-5f);
        Assert.AreEqual(1f, result.w);
    }

    [Test]
    public void LerpTangent_WithoutNormal_IsNormalizedLerp()
    {
        Vector4 a = new Vector4(1f, 0f, 0f, 1f);
        Vector4 b = new Vector4(0f, 1f, 0f, 1f);
        Vector4 result = VertexAttributeUtils.LerpTangent(a, b, 0.5f, Vector3.zero);
        Assert.AreEqual(Mathf.Sqrt(0.5f), result.x, 1e-5f);
        Assert.AreEqual(Mathf.Sqrt(0.5f), result.y, 1e-5f);
        Assert.AreEqual(0f, result.z, 1e-5f);
    }
}
