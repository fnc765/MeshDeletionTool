using System;
using System.Globalization;

namespace MeshDeletionTool
{
    // ポリゴン数の増減の大きさ（インスペクターの表示色に使う）
    public enum PolygonChangeLevel
    {
        // 減った（または変わらない）
        Decrease,
        // +20% 以下
        Small,
        // +50% 以下
        Medium,
        // +50% より多い
        Large
    }

    // 1 つの Renderer の処理前後のポリゴン数（三角形数）・頂点数と処理時間。プレビューがインスペクターに表示するために残す
    // Unity には依存しない（表示の文字列と色の段階は PolygonStatsFormat が作る）
    public sealed class PolygonStats
    {
        public int OriginalTriangles;
        public int GeneratedTriangles;
        public int OriginalVertices;
        public int GeneratedVertices;
        // サブメッシュ毎の処理前後の三角形数と、処理対象だったか（長さはサブメッシュ数）
        public int[] SubMeshTrianglesBefore = new int[0];
        public int[] SubMeshTrianglesAfter = new int[0];
        public bool[] SubMeshProcessed = new bool[0];
        // 処理全体の時間（テクスチャの読み出しを含む）と、要素毎の判定の実行先（"GPU" / "CPU"）
        public double ElapsedMilliseconds;
        public string Backend;

        // 元のメッシュと結果のメッシュから作る（processed はサブメッシュ毎の処理対象。null なら全て対象外として扱う）
        public static PolygonStats FromMeshes(MeshArrays source, MeshArrays result, bool[] processed, double elapsedMilliseconds, string backend)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (result == null)
                throw new ArgumentNullException(nameof(result));
            int subMeshCount = source.SubMeshCount;
            PolygonStats stats = new PolygonStats
            {
                OriginalTriangles = source.TriangleCount,
                GeneratedTriangles = result.TriangleCount,
                OriginalVertices = source.VertexCount,
                GeneratedVertices = result.VertexCount,
                SubMeshTrianglesBefore = new int[subMeshCount],
                SubMeshTrianglesAfter = new int[subMeshCount],
                SubMeshProcessed = new bool[subMeshCount],
                ElapsedMilliseconds = elapsedMilliseconds,
                Backend = backend
            };
            for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
            {
                stats.SubMeshTrianglesBefore[subMeshIndex] = source.SubMeshTriangles[subMeshIndex].Length / 3;
                stats.SubMeshTrianglesAfter[subMeshIndex] = subMeshIndex < result.SubMeshCount ? result.SubMeshTriangles[subMeshIndex].Length / 3 : 0;
                stats.SubMeshProcessed[subMeshIndex] = processed != null && subMeshIndex < processed.Length && processed[subMeshIndex];
            }
            return stats;
        }
    }

    // ポリゴン数の変化の表示（インスペクターのプレビュー欄）。数値の書式と丸め、符号、色の段階を決める。Unity には依存しない
    // 数値は 3 桁区切り（カルチャに依存しない）、増減率は小数 1 桁（四捨五入）。減少は「−」（U+2212）、変化なしは「±」で表す
    public static class PolygonStatsFormat
    {
        // 色の段階の境目（増加率 %）
        public const double SmallIncreaseLimitPercent = 20.0;
        public const double MediumIncreaseLimitPercent = 50.0;

        public const string Minus = "−";

        // 増減率（%）。before が 0 なら、after も 0 のとき 0、それ以外は正の無限大
        public static double PercentChange(int before, int after)
        {
            if (before == 0)
                return after == 0 ? 0.0 : double.PositiveInfinity;
            return (after - before) * 100.0 / before;
        }

        // 増減率を小数 1 桁に丸めた値（表示と色の判定で同じ値を使う）
        public static double RoundedPercent(double percent)
        {
            if (double.IsInfinity(percent) || double.IsNaN(percent))
                return percent;
            return Math.Round(percent, 1, MidpointRounding.AwayFromZero);
        }

        // 3 桁区切りの数（例: 8,776）
        public static string Count(int value)
        {
            string digits = Math.Abs((long)value).ToString("#,0", CultureInfo.InvariantCulture);
            return value < 0 ? Minus + digits : digits;
        }

        // 符号付きの差（例: +1,502 / −310 / ±0）
        public static string SignedCount(int delta)
        {
            if (delta == 0)
                return "±0";
            return (delta > 0 ? "+" : Minus) + Math.Abs((long)delta).ToString("#,0", CultureInfo.InvariantCulture);
        }

        // 符号付きの増減率（例: +17.1% / −3.2% / ±0.0%）。符号は丸める前の値で決める（+0.04% は +0.0%）。before が 0 で増えたときは「—」
        public static string SignedPercent(int before, int after)
        {
            double percent = PercentChange(before, after);
            if (double.IsInfinity(percent))
                return "—";
            string digits = Math.Abs(RoundedPercent(percent)).ToString("0.0", CultureInfo.InvariantCulture) + "%";
            if (after == before)
                return "±" + digits;
            return (after > before ? "+" : Minus) + digits;
        }

        // 色の段階: 減少（変化なしを含む）/ +20% 以下 / +50% 以下 / それより多い（丸めた増加率で判定する）
        public static PolygonChangeLevel Level(int before, int after)
        {
            if (after <= before)
                return PolygonChangeLevel.Decrease;
            double percent = RoundedPercent(PercentChange(before, after));
            if (percent <= SmallIncreaseLimitPercent)
                return PolygonChangeLevel.Small;
            if (percent <= MediumIncreaseLimitPercent)
                return PolygonChangeLevel.Medium;
            return PolygonChangeLevel.Large;
        }

        // 「ポリゴン数（三角形）: 8,776 → 10,278（+1,502 / +17.1%）」。減ったときは末尾に「減少」を付ける
        public static string TriangleLine(PolygonStats stats)
        {
            int before = stats.OriginalTriangles, after = stats.GeneratedTriangles;
            string change = SignedCount(after - before) + " / " + SignedPercent(before, after) + (after < before ? "、減少" : "");
            return "ポリゴン数（三角形）: " + Count(before) + " → " + Count(after) + "（" + change + "）";
        }

        // 「頂点数: 5,637 → 7,112（+26.2%）」
        public static string VertexLine(PolygonStats stats)
        {
            return "頂点数: " + Count(stats.OriginalVertices) + " → " + Count(stats.GeneratedVertices) +
                   "（" + SignedPercent(stats.OriginalVertices, stats.GeneratedVertices) + "）";
        }

        // 「処理時間: 201 ms（GPU）」
        public static string TimeLine(PolygonStats stats)
        {
            string time = Math.Round(stats.ElapsedMilliseconds, MidpointRounding.AwayFromZero).ToString("#,0", CultureInfo.InvariantCulture) + " ms";
            return "処理時間: " + time + (string.IsNullOrEmpty(stats.Backend) ? "" : "（" + stats.Backend + "）");
        }

        // サブメッシュ別の表の 1 行の数値部分: 「1,024 → 1,310」と「+27.9%」（対象外は「対象外」）
        public static string SubMeshCounts(PolygonStats stats, int subMeshIndex)
        {
            return Count(stats.SubMeshTrianglesBefore[subMeshIndex]) + " → " + Count(stats.SubMeshTrianglesAfter[subMeshIndex]);
        }

        public static string SubMeshPercent(PolygonStats stats, int subMeshIndex)
        {
            if (!stats.SubMeshProcessed[subMeshIndex])
                return "対象外";
            return SignedPercent(stats.SubMeshTrianglesBefore[subMeshIndex], stats.SubMeshTrianglesAfter[subMeshIndex]);
        }

        // マテリアル名の表示用（Unity が複製したマテリアルに付ける「 (Instance)」を取り除く。何度付いていても全て）
        public static string MaterialDisplayName(string materialName)
        {
            if (materialName == null)
                return null;
            const string suffix = " (Instance)";
            string name = materialName;
            while (name.EndsWith(suffix, StringComparison.Ordinal))
                name = name.Substring(0, name.Length - suffix.Length);
            return name;
        }
    }
}
