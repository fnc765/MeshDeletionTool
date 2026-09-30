using UnityEngine;

namespace MeshDeletionTool
{
    // テクスチャのアルファ値だけを保持し、Texture2D.GetPixel と同じ規則でテクセルを参照するクラス
    // Texture2D の GetPixel は呼び出し毎にネイティブ呼び出しが必要で Unity の外では扱えないため、
    // 一度だけ読み出した結果を写し取り（GPU 経由の AlphaMaskReader、または GetPixels32 の MeshArraysUnityAdapter.FromTexture）、以後はこのクラスだけを参照する
    public class AlphaMask
    {
        public readonly int Width;
        public readonly int Height;

        // 範囲外のテクセル座標の扱い（Texture2D.GetPixel はテクスチャのラップモードに従う）
        public TextureWrapMode WrapModeU = TextureWrapMode.Repeat;
        public TextureWrapMode WrapModeV = TextureWrapMode.Repeat;

        // アルファ値（0〜255）。下の行から順に Width 個ずつ（GetPixels32 と同じ並び）
        private readonly byte[] alpha;

        // アルファ値の配列そのもの（GetPixels32 と同じ並び）。処理段のバックエンドがまとめて参照・転送するためのもので、変更しないこと
        public byte[] AlphaBytes => alpha;

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

        // RGBA32（1 テクセル 4 バイト、R, G, B, A の順。GetRawTextureData / ReadPixels の並び）からアルファ値だけを取り出す
        // 行の並びは GetPixels32 と同じ下の行から。flipY なら行を上下反転して取り込む（上の行から並ぶ読み出し結果を同じ並びに直す）
        public static AlphaMask FromRgba32(byte[] rgba, int width, int height, bool flipY)
        {
            if (rgba == null || rgba.Length != width * height * 4)
                throw new System.ArgumentException("RGBA32 配列の長さが width * height * 4 と一致しません。");
            byte[] alpha = new byte[width * height];
            for (int y = 0; y < height; y++)
            {
                int sourceRow = (flipY ? height - 1 - y : y) * width;
                int targetRow = y * width;
                for (int x = 0; x < width; x++)
                {
                    alpha[targetRow + x] = rgba[(sourceRow + x) * 4 + 3];
                }
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
}
