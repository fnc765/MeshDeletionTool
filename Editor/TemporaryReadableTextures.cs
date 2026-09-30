using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace MeshDeletionTool
{
    // テクスチャのピクセルを読み出す間だけ、インポート設定を「読み取り可能・非圧縮」に変更する。Dispose で記録しておいた元の設定に戻し、再インポートする
    // GPU 経由の読み出し（AlphaMaskReader）が使えないとき（-nographics、Blit / ReadPixels の失敗）だけ使う従来経路。
    // 圧縮されたテクスチャ（DXT5 など）の GetPixels32 は圧縮を展開した近似値を返すため、アルファ値を正確に読むには一時的に非圧縮にする必要がある
    // 既に読み取り可能で非圧縮なテクスチャは変更しない。アセットでないテクスチャ（TextureImporter が無いもの）は設定を変えられないため、
    // 読み取り可能ならそのまま読み、読み取り不可なら読めないもの（CanRead が false）として扱う
    internal sealed class TemporaryReadableTextures : IDisposable
    {
        // 変更したテクスチャの元の設定
        private class SavedSettings
        {
            public string Path;
            public string Name;
            public bool IsReadable;
            public TextureImporterCompression? Compression;               // 変更したときだけ
            public TextureImporterPlatformSettings PlatformOverride;      // 現在のビルドターゲットの上書き設定を外したときだけ
        }

        private readonly List<SavedSettings> changed = new List<SavedSettings>();
        private readonly HashSet<Texture2D> readable = new HashSet<Texture2D>();

        // textures のインポート設定をまとめて変更し、一度だけ再インポートする
        public TemporaryReadableTextures(IEnumerable<Texture2D> textures)
        {
            List<Texture2D> pending = new List<Texture2D>();
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (Texture2D texture in textures)
                {
                    if (texture == null || readable.Contains(texture) || pending.Contains(texture))
                        continue;
                    if (Prepare(texture))
                        pending.Add(texture);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            // 再インポート後に読めるようになったことを確かめる（プラットフォーム設定などで圧縮が残った場合は近似値になることを知らせる）
            foreach (Texture2D texture in pending)
            {
                if (!texture.isReadable)
                {
                    Debug.LogError($"テクスチャ '{texture.name}' を読み取り可能にできませんでした。このテクスチャを使うサブメッシュは処理しません。");
                    continue;
                }
                if (GraphicsFormatUtility.IsCompressedFormat(texture.format))
                {
                    Debug.LogWarning($"テクスチャ '{texture.name}' を非圧縮にできませんでした（{texture.format}）。アルファ値は圧縮を展開した近似値になります。");
                }
                readable.Add(texture);
            }
        }

        // ピクセルを読めるテクスチャか
        public bool CanRead(Texture2D texture)
        {
            return readable.Contains(texture);
        }

        // 必要なら設定を変更して再インポートを予約し、true を返す。変更が要らなければ readable に加えて false を返す
        private bool Prepare(Texture2D texture)
        {
            string path = AssetDatabase.GetAssetPath(texture);
            TextureImporter importer = string.IsNullOrEmpty(path) ? null : AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
            {
                if (texture.isReadable)
                {
                    Debug.Log($"テクスチャ '{texture.name}' はアセットではないためインポート設定を変更しません（読み取り可能なのでそのまま読みます）。");
                    readable.Add(texture);
                }
                else
                {
                    Debug.LogError($"テクスチャ '{texture.name}' はアセットではなく読み取り可能でもないため、アルファ値を読めません。このテクスチャを使うサブメッシュは処理しません。");
                }
                return false;
            }

            bool needReadable = !importer.isReadable;
            bool needUncompressed = GraphicsFormatUtility.IsCompressedFormat(texture.format);
            if (!needReadable && !needUncompressed)
            {
                readable.Add(texture);
                return false;
            }

            SavedSettings saved = new SavedSettings { Path = path, Name = texture.name, IsReadable = importer.isReadable };
            importer.isReadable = true;
            if (needUncompressed)
            {
                saved.Compression = importer.textureCompression;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                // 現在のビルドターゲットに上書き設定があると既定の圧縮設定は使われないため、その間だけ上書きを外す
                TextureImporterPlatformSettings platformSettings = importer.GetPlatformTextureSettings(ActivePlatformName());
                if (platformSettings.overridden)
                {
                    saved.PlatformOverride = platformSettings;
                    TextureImporterPlatformSettings temporary = importer.GetPlatformTextureSettings(platformSettings.name);
                    temporary.overridden = false;
                    importer.SetPlatformTextureSettings(temporary);
                }
            }
            changed.Add(saved);
            importer.SaveAndReimport();
            Debug.Log($"テクスチャ '{texture.name}' のインポート設定を一時的に変更しました（読み取り可能: {(saved.IsReadable ? "有効" : "無効")} → 有効" +
                      (needUncompressed ? $", 圧縮: {saved.Compression} → 非圧縮" : "") + "）。読み出し後に元へ戻します。");
            return true;
        }

        // 変更したテクスチャの設定を元に戻し、まとめて再インポートする
        public void Dispose()
        {
            if (changed.Count == 0)
                return;
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (SavedSettings saved in changed)
                {
                    TextureImporter importer = AssetImporter.GetAtPath(saved.Path) as TextureImporter;
                    if (importer == null)
                    {
                        Debug.LogError($"テクスチャ '{saved.Name}' の TextureImporter が見つからず、インポート設定を元に戻せませんでした（{saved.Path}）。");
                        continue;
                    }
                    importer.isReadable = saved.IsReadable;
                    if (saved.Compression.HasValue)
                        importer.textureCompression = saved.Compression.Value;
                    if (saved.PlatformOverride != null)
                        importer.SetPlatformTextureSettings(saved.PlatformOverride);
                    importer.SaveAndReimport();
                    Debug.Log($"テクスチャ '{saved.Name}' のインポート設定を元に戻しました。");
                }
            }
            finally
            {
                changed.Clear();
                AssetDatabase.StopAssetEditing();
            }
        }

        // TextureImporter のプラットフォーム名（エディタは現在のビルドターゲットの設定でインポートする。BuildTargetGroup の名前と異なるものがある）
        private static string ActivePlatformName()
        {
            BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);
            switch (group)
            {
                case BuildTargetGroup.iOS: return "iPhone";
                case BuildTargetGroup.WSA: return "Windows Store Apps";
                default: return group.ToString();
            }
        }
    }
}
