using NUnit.Framework;
using MeshDeletionTool;

// プレイモードで MeshDeletionForTexture を誰が適用するか（NDMF / 簡易適用 / しない）の判定のテスト
public class PlayModeApplyPolicyTest
{
    [Test]
    public void UnderAvatarRoot_IsLeftToNdmf()
    {
        Assert.AreEqual(PlayModeApplier.Ndmf, PlayModeApplyPolicy.Decide(true, true, true));
    }

    [Test]
    public void OutsideAvatarRoot_UsesTheFallback()
    {
        // 衣装のプレハブ単体など: NDMF の Apply on Play は処理しないので簡易適用が担当する
        Assert.AreEqual(PlayModeApplier.Fallback, PlayModeApplyPolicy.Decide(true, true, false));
    }

    [Test]
    public void ApplyOnPlayOff_AppliesNothing()
    {
        // NDMF の Apply on Play をオフにしている利用者の設定に従い、簡易適用もしない
        Assert.AreEqual(PlayModeApplier.None, PlayModeApplyPolicy.Decide(true, false, true));
        Assert.AreEqual(PlayModeApplier.None, PlayModeApplyPolicy.Decide(true, false, false));
    }

    [Test]
    public void WithoutNdmf_AlwaysUsesTheFallback()
    {
        Assert.AreEqual(PlayModeApplier.Fallback, PlayModeApplyPolicy.Decide(false, false, false));
        Assert.AreEqual(PlayModeApplier.Fallback, PlayModeApplyPolicy.Decide(false, true, true));
    }
}
