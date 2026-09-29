using System;
using System.Collections.Generic;
using Stopwatch = System.Diagnostics.Stopwatch;
using UnityEngine;
using UnityEngine.Rendering;

namespace MeshDeletionTool
{
    // テクスチャのアルファ値（AlphaMask）の読み出し。テクスチャツール本体・ヘッドレスの入口・ベンチマークが共通に使う
    // 基本は GPU 経由: Texture2D を同じ大きさの RenderTexture に Blit し、ReadPixels で CPU に読み戻す。テクスチャが読み取り可能（isReadable）で
    // なくてもよく、インポート設定には触れない（再インポートしない）。圧縮テクスチャ（BC3/DXT5 など）のアルファ値は GPU が展開した値、
    // つまり描画に使われている値そのものになる（PNG のアルファ値と厳密に一致させたいときはテクスチャ側で非圧縮にする）
    // GPU が使えないとき（-nographics でグラフィックスデバイスが無い、Blit / ReadPixels の失敗、並びの自己検証の失敗）だけ、従来通り
    // TemporaryReadableTextures でインポート設定を一時的に変更して GetPixels32 で読む
    // どちらの経路でも AlphaMask の並びは同じ（下の行から Width 個ずつ、y * Width + x）。CPU / GPU の両バックエンドは同じ AlphaMask を参照するため、
    // 読み出し経路が結果の一致に影響することはない
    internal static class AlphaMaskReader
    {
        // GPU 経由の読み出しがこのセッションで使えるか（並びの自己検証の結果。null なら未検証）と、行を上下反転して取り込む必要があるか
        private static bool? gpuPathAvailable;
        private static bool gpuPathFlipY;
        private static string gpuPathUnavailableReason;

        // textures（重複なし）のアルファ値を読み出し、テクスチャ → マスク（読めなければ null）を返す。note に使った経路と時間を 1 行で返す
        public static Dictionary<Texture2D, AlphaMask> Read(IList<Texture2D> textures, out string note)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            Dictionary<Texture2D, AlphaMask> masks = new Dictionary<Texture2D, AlphaMask>();
            List<Texture2D> fallback = new List<Texture2D>();
            List<string> reasons = new List<string>();
            int gpuCount = 0;
            foreach (Texture2D texture in textures)
            {
                if (texture == null || masks.ContainsKey(texture))
                    continue;
                AlphaMask mask = ReadThroughGpu(texture, out string reason);
                if (mask != null)
                {
                    masks[texture] = mask;
                    gpuCount++;
                }
                else
                {
                    fallback.Add(texture);
                    if (reason != null && !reasons.Contains(reason))
                        reasons.Add(reason);
                }
            }
            double gpuMs = stopwatch.Elapsed.TotalMilliseconds;

            // GPU で読めなかったテクスチャは従来経路（インポート設定の一時変更）で読む
            int importCount = 0;
            if (fallback.Count > 0)
            {
                using (TemporaryReadableTextures readableTextures = new TemporaryReadableTextures(fallback))
                {
                    foreach (Texture2D texture in fallback)
                    {
                        AlphaMask mask = readableTextures.CanRead(texture) ? MeshArraysUnityAdapter.FromTexture(texture) : null;
                        masks[texture] = mask;   // 読めなかった理由は TemporaryReadableTextures が出している
                        if (mask != null)
                            importCount++;
                    }
                }
            }

            note = "テクスチャ読み出し: " + stopwatch.Elapsed.TotalMilliseconds.ToString("0.0") + " ms（GPU 経由 " + gpuCount + " 枚";
            if (gpuCount > 0 && fallback.Count > 0)
                note += " " + gpuMs.ToString("0.0") + " ms";
            if (fallback.Count > 0)
                note += ", インポート設定の一時変更 " + importCount + " 枚" + (fallback.Count > importCount ? ", 読めず " + (fallback.Count - importCount) + " 枚" : "");
            note += "）";
            if (reasons.Count > 0)
                note += " GPU 経由で読めなかった理由: " + string.Join(" / ", reasons);
            return masks;
        }

        // 1 枚のテクスチャを GPU 経由で読む。読めなければ null と理由を返す（インポート設定は変更しない）
        public static AlphaMask ReadThroughGpu(Texture2D texture, out string reason)
        {
            if (!EnsureGpuPath(out reason))
                return null;
            AlphaMask mask = BlitAndReadAlpha(texture, gpuPathFlipY, out reason);
            if (mask == null)
                return null;
            mask.WrapModeU = texture.wrapModeU;
            mask.WrapModeV = texture.wrapModeV;
            return mask;
        }

        // GPU 経由の読み出しが使えるかを一度だけ調べる: 4x2 の既知のアルファ値を持つ読み取り可能なテクスチャを同じ経路で読み、
        // AlphaMask の並び（下の行から、y * Width + x）と一致することを確かめる。上下が反転して返る環境では反転して取り込む
        private static bool EnsureGpuPath(out string reason)
        {
            reason = null;
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                reason = "グラフィックスデバイスが無い（-nographics）";
                return false;
            }
            if (gpuPathAvailable.HasValue)
            {
                reason = gpuPathUnavailableReason;
                return gpuPathAvailable.Value;
            }

            const int width = 4, height = 2;
            byte[] expected = new byte[width * height];
            Color32[] pixels = new Color32[width * height];
            for (int i = 0; i < pixels.Length; i++)
            {
                expected[i] = (byte)(10 + i * 30);   // 行・列のどちらを入れ替えても一致しない値
                pixels[i] = new Color32((byte)(i * 20), (byte)(255 - i * 20), 128, expected[i]);
            }
            Texture2D probe = null;
            try
            {
                probe = new Texture2D(width, height, TextureFormat.RGBA32, false, true) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                probe.SetPixels32(pixels);
                probe.Apply(false, false);
                AlphaMask read = BlitAndReadAlpha(probe, false, out reason);
                if (read == null)
                {
                    gpuPathAvailable = false;
                }
                else if (SameAlpha(read, expected, false))
                {
                    gpuPathAvailable = true;
                    gpuPathFlipY = false;
                }
                else if (SameAlpha(read, expected, true))
                {
                    gpuPathAvailable = true;
                    gpuPathFlipY = true;
                    Debug.Log("テクスチャの GPU 経由の読み出しは行が上下反転して返るため、反転して取り込みます。");
                }
                else
                {
                    gpuPathAvailable = false;
                    reason = "GPU 経由で読んだテクセルの並びが GetPixels32 と一致しない";
                }
            }
            finally
            {
                if (probe != null)
                    UnityEngine.Object.DestroyImmediate(probe);
            }
            gpuPathUnavailableReason = gpuPathAvailable.Value ? null : reason;
            return gpuPathAvailable.Value;
        }

        private static bool SameAlpha(AlphaMask mask, byte[] expected, bool flipY)
        {
            for (int y = 0; y < mask.Height; y++)
            {
                int sourceY = flipY ? mask.Height - 1 - y : y;
                for (int x = 0; x < mask.Width; x++)
                {
                    if (mask.AlphaByte(x, sourceY) != expected[y * mask.Width + x])
                        return false;
                }
            }
            return true;
        }

        // Blit → ReadPixels → アルファ値の取り出し。RenderTexture.active は元に戻し、一時オブジェクトは解放する
        private static AlphaMask BlitAndReadAlpha(Texture2D texture, bool flipY, out string reason)
        {
            reason = null;
            int width = texture.width, height = texture.height;
            RenderTexture previous = RenderTexture.active;
            RenderTexture target = null;
            Texture2D readback = null;
            try
            {
                // 色空間の変換を避けるため Linear の RenderTexture に描く（使うのはアルファ値だけなので、どちらでも結果は同じ）
                target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                if (target == null)
                {
                    reason = "RenderTexture を確保できない（" + width + "x" + height + "）";
                    return null;
                }
                // Blit はミップ 0 を同じ大きさに写す（テクセル中心のサンプリングなので値はそのまま。圧縮テクスチャは GPU が展開する）
                Graphics.Blit(texture, target);
                RenderTexture.active = target;
                // ReadPixels の Rect の原点は左下で、読み込んだ Texture2D の並びは GetPixels32 と同じ（下の行から）
                readback = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
                readback.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                byte[] rgba = readback.GetRawTextureData();
                return AlphaMask.FromRgba32(rgba, width, height, flipY);
            }
            catch (Exception e)
            {
                reason = "Blit / ReadPixels に失敗（" + e.GetType().Name + ": " + e.Message + "）";
                return null;
            }
            finally
            {
                RenderTexture.active = previous;
                if (readback != null)
                    UnityEngine.Object.DestroyImmediate(readback);
                if (target != null)
                    RenderTexture.ReleaseTemporary(target);
            }
        }
    }
}
