using UnityEngine;
using NUnit.Framework;
using MeshDeletionTool;

// MeshArrays（Unity に依存しないメッシュ表現）のテスト
public class MeshArraysTest
{
    private static MeshArrays TwoSubMeshQuad()
    {
        return new MeshArrays
        {
            Vertices = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0) },
            SubMeshTriangles = new[] { new[] { 0, 1, 2 }, new[] { 0, 2, 3 } }
        };
    }

    [Test]
    public void Counts_FollowArrays()
    {
        MeshArrays mesh = TwoSubMeshQuad();
        Assert.AreEqual(4, mesh.VertexCount);
        Assert.AreEqual(2, mesh.SubMeshCount);
        Assert.AreEqual(2, mesh.TriangleCount);
        Assert.AreEqual(new[] { 0, 2, 3 }, mesh.GetTriangles(1));
    }

    [Test]
    public void GetAllTriangles_ConcatenatesSubMeshesInOrder()
    {
        Assert.AreEqual(new[] { 0, 1, 2, 0, 2, 3 }, TwoSubMeshQuad().GetAllTriangles());
        Assert.AreEqual(new int[0], new MeshArrays().GetAllTriangles());
    }

    [Test]
    public void UVChannels_MapToFields()
    {
        MeshArrays mesh = new MeshArrays();
        for (int channel = 0; channel < MeshArrays.UVChannelCount; channel++)
        {
            mesh.SetUV(channel, new[] { new Vector2(channel, 0f) });
        }
        Assert.AreEqual(0f, mesh.UV[0].x);
        Assert.AreEqual(1f, mesh.UV2[0].x);
        Assert.AreEqual(4f, mesh.UV5[0].x);
        Assert.AreEqual(7f, mesh.UV8[0].x);
        Assert.AreSame(mesh.UV3, mesh.GetUV(2));
        Assert.Throws<System.ArgumentOutOfRangeException>(() => mesh.GetUV(8));
    }

    [Test]
    public void SetUV_Null_BecomesEmpty()
    {
        MeshArrays mesh = new MeshArrays();
        mesh.SetUV(3, null);
        Assert.AreEqual(0, mesh.UV4.Length);
    }

    [Test]
    public void EmptyStreams_ByDefault()
    {
        MeshArrays mesh = new MeshArrays();
        Assert.AreEqual(0, mesh.Normals.Length);
        Assert.AreEqual(0, mesh.BoneWeights.Length);
        Assert.AreEqual(0, mesh.Bindposes.Length);
        Assert.AreEqual(0, mesh.BlendShapes.Count);
        Assert.IsFalse(mesh.Bounds.HasValue);
        Assert.AreEqual(UnityEngine.Rendering.IndexFormat.UInt16, mesh.IndexFormat);
    }

    [Test]
    public void CalculateBounds_IsMinMaxOfVertices()
    {
        MeshArrays mesh = new MeshArrays { Vertices = new[] { new Vector3(-1, 2, 0), new Vector3(3, -4, 5) } };
        Bounds bounds = mesh.CalculateBounds();
        Assert.AreEqual(new Vector3(-1, -4, 0), bounds.min);
        Assert.AreEqual(new Vector3(3, 2, 5), bounds.max);
        Assert.AreEqual(new Bounds(), new MeshArrays().CalculateBounds());
    }
}
