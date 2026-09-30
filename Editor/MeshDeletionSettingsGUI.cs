using UnityEditor;
using UnityEngine;

namespace MeshDeletionTool
{
    // 設定項目の表示（名前・説明・ツールチップ・操作）。MeshDeletionForTexture のインスペクターとウィンドウ（Tools/MeshDeletionToolForTexture）で共通
    // 値を受け取って新しい値を返す（インスペクターは SerializedProperty に書き戻すので Undo と NDMF のプレビューの更新が効く）
    // 名前は処理の仕組みを知らなくても分かる言葉にし、元の名前（アルファ閾値など）を括弧で残す
    internal static class MeshDeletionSettingsGUI
    {
        internal static readonly GUIContent AlphaThresholdLabel = new GUIContent("透明とみなす境界（アルファ閾値）",
            "テクスチャのアルファ値（不透明度、0〜1）がこの値より小さい部分を透明とみなし、そこにあるメッシュを削除します。" +
            "透明と不透明がはっきり分かれたテクスチャでは、値を変えても結果はほとんど変わりません。");
        internal const string AlphaThresholdHelp = "この値より透明な部分のメッシュを削除します。0.5 が標準。大きくすると半透明の部分も削除されます。";

        internal static readonly GUIContent RefineLabel = new GUIContent("輪郭に沿って細かく切る（境界の細分化）",
            "透明との境目の近くのポリゴンを細かく分けてから切り、切り口をテクスチャの輪郭に沿わせます。細い部分も残りますが、ポリゴンは増えます。");
        internal const string RefineHelp = "オフにすると元のポリゴンの辺の上でしか切らないため、細い部分が欠けることがあります（従来の動作）。";

        internal static readonly GUIContent PrecisionLabel = new GUIContent("切り抜きの精度",
            "切り口をテクスチャの輪郭にどれだけ正確に沿わせるか。細かいほど輪郭に正確に沿いますが、ポリゴンが増えます。" +
            "括弧内の数値は輪郭からずれてよい量（テクセル）です。");
        internal static readonly GUIContent PrecisionCustomLabel = new GUIContent("境界の精度（テクセル）",
            "輪郭からずれてよい量（テクセル、0.5〜4）。小さいほど細かく正確ですが、ポリゴンが増えます。");
        internal const string PrecisionFinerEnd = "← 細かい（ポリゴン多）";
        internal const string PrecisionLighterEnd = "軽い（ポリゴン少）→";

        internal static readonly GUIContent MaxDepthLabel = new GUIContent("細分化の最大深さ",
            "輪郭の近くのポリゴンを何段階まで細かく分けるか（0〜5）。0 にすると細分化しません。");
        internal const string MaxDepthHelp = "大きいほど細い部分まで拾えますが、時間とポリゴン数が増えます。通常は 3。";

        internal static readonly GUIContent MergeLabel = new GUIContent("切断後に再結合する",
            "細分化と切断で細かく分かれたポリゴンを、元の面ごとにまとめ直し、切り口の余分な頂点を間引きます。");
        internal const string MergeHelp = "ポリゴン数を抑えます。通常はオン。";

        internal static readonly GUIContent AdvancedLabel = new GUIContent("詳細設定", "通常は変更不要です。");

        private static string[] precisionLabels;
        private static GUIStyle helpStyle;

        // 項目の下の 1〜2 行の説明（灰色の小さい文字、折り返す）
        internal static void Help(string text)
        {
            if (helpStyle == null)
            {
                helpStyle = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };
                helpStyle.normal.textColor = EditorGUIUtility.isProSkin ? new Color(0.62f, 0.62f, 0.62f) : new Color(0.38f, 0.38f, 0.38f);
            }
            EditorGUILayout.LabelField(text, helpStyle);
        }

        // 名前が長い項目は、名前の行と操作の行（全幅）に分ける（インスペクターの幅で名前が切れないように）
        internal static float AlphaThreshold(float value)
        {
            EditorGUILayout.LabelField(AlphaThresholdLabel);
            value = EditorGUILayout.Slider(GUIContent.none, value, 0f, 1f);
            Help(AlphaThresholdHelp);
            return value;
        }

        internal static bool Refine(bool value)
        {
            value = EditorGUILayout.ToggleLeft(RefineLabel, value);
            Help(RefineHelp);
            return value;
        }

        // 切り抜きの精度: プリセット（高精度 0.5 / 標準 1 / 軽量 2 / 最軽量 4）とカスタム（スライダー）
        // custom はカスタムを選んだ状態（プリセットと同じ値にしてもスライダーを出したままにする）。millimeterHint は「≈ … mm 単位で輪郭に沿わせます」（無ければ null）
        internal static float Precision(float value, ref bool custom, string millimeterHint)
        {
            if (precisionLabels == null)
                precisionLabels = PrecisionPresets.Labels();
            if (PrecisionPresets.IndexOf(value) == PrecisionPresets.Custom)
                custom = true;
            int selected = custom ? PrecisionPresets.Custom : PrecisionPresets.IndexOf(value);
            EditorGUILayout.LabelField(PrecisionLabel);
            int chosen = EditorGUILayout.Popup(GUIContent.none, selected, ToContents(precisionLabels, PrecisionLabel.tooltip));
            if (chosen != selected)
            {
                custom = chosen == PrecisionPresets.Custom;
                value = PrecisionPresets.ValueFor(chosen, value);
            }
            if (custom)
            {
                value = EditorGUILayout.Slider(GUIContent.none, value, PrecisionPresets.Min, PrecisionPresets.Max);
                Rect ends = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight * 0.8f);
                ends.xMax -= EditorGUIUtility.fieldWidth + 4f;
                EditorGUI.LabelField(ends, PrecisionFinerEnd, EditorStyles.miniLabel);
                EditorGUI.LabelField(ends, PrecisionLighterEnd, new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight });
            }
            if (millimeterHint != null)
                Help(millimeterHint);
            return value;
        }

        internal static int MaxDepth(int value)
        {
            value = EditorGUILayout.IntSlider(MaxDepthLabel, value, 0, 5);
            Help(MaxDepthHelp);
            return value;
        }

        internal static bool Merge(bool value)
        {
            value = EditorGUILayout.ToggleLeft(MergeLabel, value);
            Help(MergeHelp);
            return value;
        }

        private static GUIContent[] ToContents(string[] labels, string tooltip)
        {
            GUIContent[] contents = new GUIContent[labels.Length];
            for (int index = 0; index < labels.Length; index++)
                contents[index] = new GUIContent(labels[index], tooltip);
            return contents;
        }
    }
}
