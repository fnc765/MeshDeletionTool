using System;
using System.Globalization;

namespace MeshDeletionTool
{
    // 「切り抜きの精度」の選択肢（境界の精度（テクセル）= MeshDeletionOptions.BoundaryPrecisionTexels の代表的な値）と、値との対応
    // インスペクターとウィンドウで共通。どのプリセットにも当たらない値は「カスタム」（Custom）。Unity には依存しない
    public static class PrecisionPresets
    {
        public static readonly float[] Values = { 0.5f, 1f, 2f, 4f };
        public static readonly string[] Names = { "高精度", "標準", "軽量", "最軽量" };

        // 選択肢の番号: 0〜3 がプリセット、Custom が「カスタム」
        public static int Custom => Values.Length;

        // 値の範囲（MeshDeletionForTexture.boundaryPrecisionTexels の Range と同じ）
        public const float Min = 0.5f;
        public const float Max = 4f;

        // 値がプリセットと一致すればその番号、しなければ Custom
        public static int IndexOf(float value)
        {
            for (int index = 0; index < Values.Length; index++)
            {
                if (Math.Abs(Values[index] - value) < 1e-4f)
                    return index;
            }
            return Custom;
        }

        // 選択肢の表示（「高精度（0.5）」…「最軽量（4）」「カスタム」）
        public static string[] Labels()
        {
            string[] labels = new string[Values.Length + 1];
            for (int index = 0; index < Values.Length; index++)
                labels[index] = Names[index] + "（" + Values[index].ToString("0.##", CultureInfo.InvariantCulture) + "）";
            labels[Values.Length] = "カスタム";
            return labels;
        }

        // 選んだ番号の値（Custom なら current をそのまま範囲に収めて返す）
        public static float ValueFor(int index, float current)
        {
            if (index >= 0 && index < Values.Length)
                return Values[index];
            return Math.Max(Min, Math.Min(Max, current));
        }

        // 輪郭に沿わせる単位の長さの表示: 1 テクセルの大きさ（mm、最小〜最大）× 精度（テクセル）。例「≈ 0.5〜0.9 mm 単位で輪郭に沿わせます」
        // 求められない（texelMax が 0 以下）なら null
        public static string MillimeterHint(float texelMinMillimeters, float texelMaxMillimeters, float precisionTexels)
        {
            if (!(texelMaxMillimeters > 0f))
                return null;
            string min = (texelMinMillimeters * precisionTexels).ToString("0.0", CultureInfo.InvariantCulture);
            string max = (texelMaxMillimeters * precisionTexels).ToString("0.0", CultureInfo.InvariantCulture);
            return "≈ " + (min == max ? min : min + "〜" + max) + " mm 単位で輪郭に沿わせます";
        }
    }
}
