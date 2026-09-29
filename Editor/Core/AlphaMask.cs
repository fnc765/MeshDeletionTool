using UnityEngine;

namespace MeshDeletionTool
{
    // テクスチャのアルファ値だけを保持し、Texture2D.GetPixel と同じ規則でテクセルを参照するクラス
    // Texture2D の GetPixel は呼び出し毎にネイティブ呼び出しが必要で Unity の外では扱えないため、
    // GetPixels32 で一度だけ読み出した結果を写し取り（MeshArraysUnityAdapter.FromTexture）、以後はこのクラスだけを参照する
    public class AlphaMask
    {
        public readonly int Width;
        public readonly int Height;

        // 範囲外のテクセル座標の扱い（Texture2D.GetPixel はテクスチャのラップモードに従う）
        public TextureWrapMode WrapModeU = TextureWrapMode.Repeat;
        public TextureWrapMode WrapModeV = TextureWrapMode.Repeat;

        // アルファ値（0〜255）。下の行から順に Width 個ずつ（GetPixels32 と同じ並び）
        private readonly byte[] alpha;

        // 診断用: 参照したテクセルの数
        public long SampleCount;

        public AlphaMask(int width, int height, byte[] alpha)
        {
            if (width <= 0 || height <= 0)
                throw new System.ArgumentException("テクスチャの大きさは 1 以上である必要があります。");
            if (alpha == null || alpha.Length != width * height)
                throw new System.ArgumentException("アルファ値の配列の長さが width * height と一致しません。");
            Width = width;
            Height = height;
            this.alpha = alpha;
        }

        // GetPixels32 の結果からアルファ値だけを取り出す
        public static AlphaMask FromPixels(Color32[] pixels, int width, int height)
        {
            if (pixels == null || pixels.Length != width * height)
                throw new System.ArgumentException("ピクセル配列の長さが width * height と一致しません。");
            byte[] alpha = new byte[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                alpha[i] = pixels[i].a;
            }
            return new AlphaMask(width, height, alpha);
        }

        // Texture2D.GetPixel(x, y).a と同じ値（Color32 → Color の変換と同じく 255 で割る）
        public float Alpha(int x, int y)
        {
            return AlphaByte(x, y) / 255f;
        }

        public byte AlphaByte(int x, int y)
        {
            SampleCount++;
            if (x < 0 || x >= Width)
                x = Wrap(x, Width, WrapModeU);
            if (y < 0 || y >= Height)
                y = Wrap(y, Height, WrapModeV);
            return alpha[y * Width + x];
        }

        // UV座標が示すテクセルのアルファ値（テクセル座標の求め方は既存処理と同じ: (int)(u * (幅 - 1))）
        public float AlphaAt(Vector2 uv)
        {
            return Alpha(TexelX(uv.x), TexelY(uv.y));
        }

        public int TexelX(float u)
        {
            return (int)(u * (Width - 1));
        }

        public int TexelY(float v)
        {
            return (int)(v * (Height - 1));
        }

        // 範囲外の座標をラップモードに従って範囲内に写す
        private static int Wrap(int i, int size, TextureWrapMode mode)
        {
            switch (mode)
            {
                case TextureWrapMode.Clamp:
                    return i < 0 ? 0 : size - 1;
                case TextureWrapMode.Mirror:
                {
                    int period = 2 * size;
                    int m = ((i % period) + period) % period;
                    return m < size ? m : period - 1 - m;
                }
                case TextureWrapMode.MirrorOnce:
                {
                    int m = i < 0 ? -1 - i : i;
                    return m < size ? m : size - 1;
                }
                default:
                    return ((i % size) + size) % size;
            }
        }
    }

    // UV座標が示すテクセルのアルファ値の判定（削除処理・細分化・辺上の境界点の二分探索で共用）
    public static class AlphaSampling
    {
        // UV座標が示すテクセルのアルファ値
        public static float SampleAlpha(AlphaMask mask, Vector2 uv)
        {
            return mask.AlphaAt(uv);
        }

        // UV座標が示すテクセルが透明（削除対象）かどうか
        public static bool IsTransparent(AlphaMask mask, Vector2 uv, float alphaThreshold)
        {
            return SampleAlpha(mask, uv) < alphaThreshold;
        }

        // UV座標が示すテクスチャのピクセルが境界エッジかどうかを判定する関数
        public static bool IsBoundaryEdge(AlphaMask mask, Vector2 uv1, Vector2 uv2, float alphaThreshold)
        {
            // 両端点のピクセルのアルファ値を取得
            float alpha1 = SampleAlpha(mask, uv1);
            float alpha2 = SampleAlpha(mask, uv2);

            // 片方のピクセルが透明で、もう片方が透明でない場合は境界エッジとする
            return (alpha1 < alphaThreshold && alpha2 > alphaThreshold) || (alpha1 > alphaThreshold && alpha2 < alphaThreshold);
        }

        // テクスチャのアルファ値に基づき、エッジ上の境界点のUV座標の補完用重みを求める
        public static float FindAlphaBoundary(AlphaMask mask, Vector2 uv1, Vector2 uv2, float alphaThreshold)
        {
            // 開始点のアルファ値を取得
            float alpha1 = SampleAlpha(mask, uv1);

            float tMin = 0.0f;
            float tMax = 1.0f;

            // 二分探索を用いて境界点を探す
            for (int i = 0; i < 10; i++)
            {
                float t = (tMin + tMax) / 2.0f;  // 中間点の係数
                // UV座標の中間点のアルファ値を取得
                Vector2 midUV = Vector2.Lerp(uv1, uv2, t);
                float midAlpha = SampleAlpha(mask, midUV);

                // 境界条件に応じて探索範囲を狭める
                if ((alpha1 < alphaThreshold && midAlpha > alphaThreshold) || (alpha1 > alphaThreshold && midAlpha < alphaThreshold))
                {
                    tMax = t; // 境界があると考えられる範囲を左側に絞り込む
                }
                else
                {
                    tMin = t; // 境界があると考えられる範囲を右側に絞り込む
                    alpha1 = midAlpha;
                }
            }

            // 最終的な境界点の重みを返す
            float weight = (tMin + tMax) / 2.0f;
            return weight;
        }
    }
}
