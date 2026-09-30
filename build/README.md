# build/

Unity を起動せずに配布ファイルを作り、コンパイルを確認するためのスクリプトです。Python 3 と bash だけで動きます。

## 配布ファイルを作る

```
python3 build/unitypackage/build_unitypackage.py [--repo DIR] [--out DIR] [--version V] [--only main,tests,upm]
```

`build/out/` に次のファイルを出力します（バージョンは `package.json` の `version`）。

| ファイル | 内容 |
|---|---|
| `MeshDeletionTool_v<ver>.unitypackage` | `Assets/MeshDeletionTool/` に展開される本体: package.json, README.md, CHANGELOG.md, LICENSE, Runtime/, Editor/ |
| `MeshDeletionTool_Tests_v<ver>.unitypackage` | 同じ場所に展開される Tests/（本体の上に追加で取り込む） |
| `MeshDeletionTool_UPM_v<ver>.zip` | `com.ochoco.mesh-deletion-tool/` フォルダ。`Packages/` に置いて使う。Tests/ と全ての `.meta` を含む |
| `guids_*.json` | パスと GUID の対応表（参照用） |

### GUID と .meta

各アセットの GUID は、リポジトリにコミットされている隣の `.meta` ファイルから読みます。`.meta` が無いアセットには `md5("MeshDeletionTool:Assets/MeshDeletionTool/<パス>")` で決まる GUID を使い、`--write-metas` を付けて実行するとその `.meta` をリポジトリに書き出します（既存の `.meta` は変更しません）。

```
python3 build/unitypackage/build_unitypackage.py --write-metas   # 新しく追加したファイルの .meta を生成する
```

このため、unitypackage で入れても、UPM zip や git URL で入れても、同じアセットは同じ GUID になります。ファイルを追加したら `--write-metas` を実行して生成された `.meta` もコミットしてください（Unity で開いて生成させた `.meta` をコミットしても構いません。ビルドはそれを読みます）。

### unitypackage の形式

Unity 2022.3 のエクスポートと同じ形式です: `archtemp.tar` という名前の GNU tar（magic `ustar  \0`、mode 0777、uid/gid 0）を gzip したもので、GUID ごとに `<guid>/`（ディレクトリ）、`<guid>/asset`（ファイルのみ）、`<guid>/asset.meta`、`<guid>/pathname`（改行なし）の順に並び、末尾はゼロブロック 2 個です。`.cs` は MonoImporter、`.compute` は ComputeShaderImporter、`.asmdef` は AssemblyDefinitionImporter、フォルダは DefaultImporter（`folderAsset: yes`）、拡張子の無いファイル（LICENSE）は DefaultImporter、その他は TextScriptImporter の `.meta` になります。

## 検証する

```
python3 build/unitypackage/verify_unitypackage.py build/out/MeshDeletionTool_v0.1.0.unitypackage [reference.unitypackage]
python3 build/unitypackage/verify_unitypackage.py build/out/MeshDeletionTool_UPM_v0.1.0.zip
```

unitypackage は gzip のファイル名、tar のヘッダー、GUID ごとのエントリの構成と順序、pathname、GUID の形式と一意性、末尾のゼロブロックを確認します。Unity でエクスポートした unitypackage を 2 番目の引数に渡すと、ヘッダーの各フィールドをそれと比較します。UPM zip は、最上位フォルダ名と `package.json` の `name` の一致、全てのファイル・フォルダに `.meta` があること、GUID が一意であることを確認します。

## コンパイルを確認する（Unity のライセンス不要）

```
UNITY_EDITOR_DIR=/path/to/Unity/2022.3.xf1 build/compile_check.sh [out-dir]
```

Unity Editor に同梱されている Roslyn（`Data/DotNetSdkRoslyn/csc.dll`）で、Editor の参照アセンブリ（`UnityReferenceAssemblies/unity-4.8-api` と `Managed/UnityEngine`）を参照し、Runtime → Editor → Tests の 3 つのアセンブリを Unity と同じ分け方でコンパイルします。エラーは csc の形式で出力され、全ての構成で `LAYOUT-OK` が出れば成功です。

| 環境変数 | 意味 |
|---|---|
| `UNITY_EDITOR_DIR` | Unity Editor のインストール先（Linux: `Editor/Data` を含むフォルダ、Windows: `Editor\Data` を含むフォルダ、macOS: `Unity.app/Contents`）。必須 |
| `NDMF_DIR` | `nadena.dev.ndmf.dll` と `nadena.dev.ndmf.runtime.dll` があるフォルダ（プロジェクトの `Library/ScriptAssemblies` など）。`System.Collections.Immutable.dll` も同じフォルダか `NDMF_IMMUTABLE_DLL` で指定する。指定すると `NDMF` 定義ありの構成も確認する |
| `VRCSDK_BASE_DLL` | `VRCSDKBase.dll` のパス。指定すると `NDMF;VRC_SDK_VRCSDK3` の構成も確認する |
| `NUNIT_DLL` | `nunit.framework.dll` のパス（既定は Editor のテンプレートキャッシュとプロジェクトの `Library/PackageCache/com.unity.ext.nunit@*` を探す）。TestRunner の DLL は Editor のテンプレートキャッシュから取る。見つからないときは Tests アセンブリを飛ばす |

`Editor/Core/` のテストを Unity の外で実行する手順（Editor 同梱の Mono で NUnit を直接動かす）は開発時の作業用で、リポジトリには含めていません。
