using System.Collections.Generic;
using UnityEngine;
using NUnit.Framework;
using MeshDeletionTool;

// MeshVertexRemover（範囲指定による頂点削除。Mesh に依存しない）のテスト
public class MeshVertexRemoverTest
{
    [Test]
    public void RemoveVertices_DropsVerticesAndTheirTriangles()
    {
        MeshArrays mesh = MeshCoreTestUtils.Grid(2);   // 9 頂点、サブメッシュ 0 = 左列 2 四角形、1 = 右列 2 四角形
        MeshArrays result = MeshVertexRemover.RemoveVertices(mesh, new List<int> { 8 });   // 右上の角

        Assert.AreEqual(8, result.VertexCount);
        for (int i = 0; i < 8; i++)
        {
            Assert.AreEqual(mesh.Vertices[i], result.Vertices[i]);
            Assert.AreEqual(mesh.UV2[i], result.UV2[i]);
            Assert.AreEqual(mesh.BoneWeights[i], result.BoneWeights[i]);
        }
        Assert.AreEqual(mesh.SubMeshTriangles[0], result.SubMeshTriangles[0]);   // 左列は頂点 8 を使わない
        Assert.AreEqual(2, result.SubMeshTriangles[1].Length / 3);                // 右列は頂点 8 を使う 2 三角形が消える
        MeshCoreTestUtils.AssertValidTriangles(result);
        MeshCoreTestUtils.AssertStreamLengths(result);
        Assert.AreSame(mesh.Bindposes, result.Bindposes);
        Assert.AreEqual(mesh.IndexFormat, result.IndexFormat);
    }

    [Test]
    public void RemoveVertices_CompactsBlendShapeFrames()
    {
        MeshArrays mesh = MeshCoreTestUtils.Grid(2);
        MeshArrays result = MeshVertexRemover.RemoveVertices(mesh, new List<int> { 4, 1 });   // 降順

        Assert.AreEqual(7, result.VertexCount);
        BlendShapeFrameData frame = result.BlendShapes[0].Frames[0];
        Assert.AreEqual("shape", result.BlendShapes[0].Name);
        Assert.AreEqual(100f, frame.Weight);
        // 差分 (頂点番号, 0, 0) から 1 と 4 が抜ける
        Assert.AreEqual(new float[] { 0, 2, 3, 5, 6, 7, 8 }, System.Array.ConvertAll(frame.DeltaVertices, d => d.x));
        MeshCoreTestUtils.AssertStreamLengths(result);
    }

    [Test]
    public void RemoveVertices_RemapsIndices()
    {
        MeshArrays mesh = MeshCoreTestUtils.Grid(1);
        mesh.SubMeshTriangles = new[] { new[] { 0, 1, 2 }, new[] { 0, 2, 3 } };
        MeshArrays result = MeshVertexRemover.RemoveVertices(mesh, new List<int> { 1 });
        Assert.AreEqual(3, result.VertexCount);
        Assert.AreEqual(new[] { 0, 1, 2 }, result.SubMeshTriangles[1]);   // (0,2,3) → (0,1,2)
    }

    // 従来の動作: 三角形が全て消えたサブメッシュ 0 には全サブメッシュの残った三角形が連結されて残る（Mesh.triangles に設定してから
    // 三角形の残るサブメッシュだけを個別に設定していたため）。互換性のために保っている
    [Test]
    public void RemoveVertices_EmptiedSubMeshZero_KeepsConcatenatedTriangles()
    {
        MeshArrays mesh = MeshCoreTestUtils.Grid(1);
        mesh.SubMeshTriangles = new[] { new[] { 0, 1, 2 }, new[] { 0, 2, 3 } };
        MeshArrays result = MeshVertexRemover.RemoveVertices(mesh, new List<int> { 1 });
        Assert.AreEqual(new[] { 0, 1, 2 }, result.SubMeshTriangles[0]);
    }
}
