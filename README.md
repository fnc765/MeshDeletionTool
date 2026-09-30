# MeshDeletionTool

"MeshDeletionTool" は Unity で動作する、テクスチャの透明部分に合わせてメッシュを削るエディタ拡張です。
透明シェーダーが使えない環境（VRChat の Quest 対応など）で、透明テクスチャによって形が表現されているモデルに対して、テクスチャの外形に沿ってメッシュを切り直し、透明な部分のメッシュを削除します。
NDMF（Non-Destructive Modular Framework）に対応しており、対象のオブジェクトにコンポーネントを付けておくだけで、プレイモードに入るときとアバターのアップロード時に削ったメッシュを生成して置き換えます（非破壊: シーン上の元のメッシュ・アセットは変更しません）。
開発のきっかけは、VRoid 製アバターを Quest 対応させたときに透明部分が黒く塗り潰されて見える「海苔現象」への対処です。

# はじめに（重要）

このリポジトリは **学習・参考のためのリファレンス実装** として公開しています。
アルファ境界に沿ったメッシュの切り直し、NDMF プラグイン・プレビューの組み方、Compute Shader と CPU で同じ結果を出す実装、Unity を起動せずに検証・パッケージングする手順などを、コードとテストとドキュメントで残すことが目的です。

**実際にアバターを Quest 対応させる目的で使う場合は、BOOTH で公開されている次のツールをお勧めします。**

* **[Yoridori Modifiers](https://yoridrill.booth.pm/items/8189252)**（ヨリドリガレージ 氏、無料）
  VRChat アバター向けの非破壊編集ツール群で、コンポーネントを付けるだけでアバターのアップロード時に処理が実行されます。メッシュの削除（トリミング）のほか、VRoid のなで肩調整や Quest 対応に関わる複数のツールが含まれ、右クリックメニューから VRoid 向けの初期値で追加できます。

こちらのツールの方が継続的に配布・更新されており、利用者も多いため、日常的な用途にはそちらを使ってください。
本リポジトリのコードも動作しますが、公開時点の状態で保守しており、下の「既知の制限・未対応」に挙げた問題は未修正です。

# 何ができるか

* **テクスチャの輪郭に沿った切断**: テクスチャのアルファ値が閾値より小さい頂点を削除し、削除される頂点と残る頂点を結ぶ辺の上でアルファ境界を二分探索して新しい頂点を追加し、ポリゴンを再構成する（既存のメッシュを削るだけではなく、テクスチャの外形に合わせて頂点を補完する）
* **境界の細分化と再結合**: アルファ境界付近の三角形を辺の中点で段階的に細分化してから削るため、既存の辺の上にしか頂点を追加できない方式では消えていた細部（3 頂点とも透明な三角形の内部や、切り口の直線で削られる膨らみ）が残る。切断後は元の三角形ごとにポリゴンを再結合し、切り口の頂点列を Douglas–Peucker で間引いて三角形の増加を抑える
* **頂点属性の引き継ぎ**: 新しい頂点には位置・法線・接線・頂点色・UV0〜UV7、SkinnedMeshRenderer のボーンウェイトとブレンドシェイプ（全フレーム）を補間して与える。サブメッシュの数と順番、マテリアルとの対応は保つ
* **GPU / CPU の自動選択**: 要素毎の判定（頂点の透明判定、三角形の細分化判定、辺上の境界点の二分探索）は Compute Shader が使える環境では GPU で、使えない環境では CPU で行う。両者は同じ数式で書かれ、出力メッシュが一致するように実装されている
* **テクスチャのインポート設定を変えない**: アルファ値は GPU 経由（RenderTexture への Blit と ReadPixels）で読むため、テクスチャが Read/Write 可能でなくてもよい
* **NDMF コンポーネント・プレビュー・プレイモードの簡易適用**: コンポーネントを付けるだけでアップロード時に処理され、Inspector のプレビューでプレイモードに入らずに結果とポリゴン数の変化を確認できる。アバターの配下にないオブジェクトはプレイモードでツール自身が置き換える
* **手動のウィンドウ**: NDMF が無いプロジェクトでも、ウィンドウから削ったメッシュをアセットとして書き出せる

# 導入

## 必要なもの

* Unity 2022.3.x（VRChat SDK 3 のプロジェクトで確認）
* NDMF（`nadena.dev.ndmf` **1.8.0 以降**）: 非破壊での適用（アップロード時の処理）とプレビューに必要。Modular Avatar を入れていれば一緒に入る。無くても手動のウィンドウとプレイモードでの簡易適用は使える
* VRChat SDK は必須ではない（あればコンポーネントは IEditorOnly として扱われ、アップロード時に取り除かれる）

## 入れ方

* **(a) unitypackage**: GitHub の Releases から `MeshDeletionTool_v*.unitypackage` を取得し、Assets > Import Package > Custom Package... で取り込む（`Assets/MeshDeletionTool/` に展開される）。テストも入れたい場合は `MeshDeletionTool_Tests_v*.unitypackage` を追加で取り込む。NDMF は別途 VCC / ALCOM で `nadena.dev.ndmf`（または Modular Avatar）を追加する
* **(b) UPM**: Unity の Package Manager で「Add package from git URL...」にこのリポジトリの URL を指定する。または Releases の `MeshDeletionTool_UPM_v*.zip` を展開してプロジェクトの `Packages/com.ochoco.mesh-deletion-tool/` に置く（`.meta` ファイルはリポジトリにコミットされているので、どちらの方法でも GUID は unitypackage と共通になる）。`Packages/` に置いた場合、テストを Test Runner に出すには `Packages/manifest.json` の `testables` に `com.ochoco.mesh-deletion-tool` を追加する
* **(c) VCC / ALCOM**: VPM リポジトリには登録していないため、VCC の一覧からは追加できない。(b) の zip を `Packages/` に手動で置く

# 使い方

## 非破壊で使う（NDMF）

1. Hierarchy で、メッシュを削りたいオブジェクト（SkinnedMeshRenderer、または MeshRenderer と MeshFilter を持つオブジェクト。例: Body）を右クリックし、**MeshDeletionTool > MeshDeletionForTexture を追加** を選ぶ。複数選択にも対応（Renderer の無いオブジェクトには付かない）。Add Component の MeshDeletionTool/MeshDeletionForTexture からも付けられる
2. Inspector で設定する。上から「状態（誰が置き換えるか）と説明」「プレビュー」「基本設定」「処理対象のサブメッシュ」「詳細設定」の順に並び、各項目にマウスを乗せると説明（ツールチップ）が出る。処理対象のサブメッシュはマテリアル名（右に灰色でテクスチャ名）で一覧され、チェックで選ぶ（既定はテクスチャを持つ全サブメッシュ。「すべて選択 / すべて解除」ボタンあり）
3. **プレビュー: オフ** ボタンを押して **プレビュー: オン** にすると、プレイモードに入らずに削った結果がシーンビュー・ゲームビューに表示され、ボタンの下にポリゴン数（三角形数）の変化が出る。例: 「ポリゴン数（三角形）: 8,776 → 10,278（+1,502 / +17.1%）」。色は緑が +20% 以下（または減少）、黄が +50% 以下、赤がそれより多い。その下に頂点数の変化、処理時間と計算の実行先（「処理時間: 201 ms（GPU）」）、「サブメッシュ別」の内訳が出る
4. プレイモードに入る（NDMF の Apply on Play）と、削ったメッシュが生成されて Renderer のメッシュが置き換わり、コンポーネントは取り除かれる。プレイモードを抜けると元に戻る
5. VRChat SDK からアップロードするときも同じ処理がビルドの途中（Modular Avatar の後）で行われ、アップロードされるアバターだけが削ったメッシュになる。シーン上のオブジェクト・元のメッシュアセットは変更されない

NDMF の Apply on Play とアップロード時の処理は、アバターのルート（VRC Avatar Descriptor、または NDMF Avatar Root を持つオブジェクト）の配下だけが対象になる。
アバターの配下にないもの（衣装のプレハブをシーンに単体で置いた場合など）は、プレイモードに入ったときに MeshDeletionTool が自分で同じ処理を行う（簡易適用。ログは `[簡易適用]` で始まる。NDMF の処理は `[NDMF]`）。簡易適用もプレイモードを抜けると元に戻る。Inspector の一番上に、そのオブジェクトをどちらが置き換えるかが 1 行で表示される。Tools/NDM Framework/Apply on Play をオフにしているときは、どちらも置き換えない。

処理できないメッシュ（Renderer が無い、メッシュが無い、三角形でないサブメッシュがある、UV0 が無い）は NDMF のエラーウィンドウと Console に 1 行で報告し、そのオブジェクトは元のまま残す。他のオブジェクトの処理は続ける。処理したオブジェクトごとに、頂点数・三角形数の変化と処理時間を 1 行のログに出す。

## プレビュー

Inspector の **プレビュー: オン / オフ** ボタンで切り替える（シーン上の全ての MeshDeletionForTexture に共通。NDMF のプレビュー設定 Tools/NDM Framework/Configure Previews にも同じスイッチが出る）。既定はオフで、Unity を再起動したりスクリプトを再コンパイルしたりするとオフに戻る。

* NDMF のプレビュー機能を使う。NDMF は元のオブジェクトを隠して表示用のコピー（プロキシ）を描き、このツールはそのコピーのメッシュだけを削った結果に差し替える。元のメッシュ・シーン・プレハブは変更されず、保存もされない
* アバターの配下にないオブジェクトもプレビューできる
* 設定を変えると作り直す（`[プレビュー]` で始まる 1 行のログが出る）。作り直している間は前の結果を薄く表示して「計算中…」と出る。同じ設定・同じ入力なら前の結果を使い回す
* 透明と不透明がはっきり分かれたテクスチャでは、「透明とみなす境界」を変えても切り口は 1 テクセル未満しか動かず、見た目もポリゴン数もほとんど変わらない。「輪郭に沿って細かく切る」や「切り抜きの精度」の違いも 1 mm 未満の輪郭の差なので、比べるときはポリゴン数の表示か、シーンビューの Shaded Wireframe 表示を使う
* NDMF 全体のプレビュー（Tools/NDM Framework/Enable Previews）がオフだと表示されない。そのときは Inspector に警告と有効にするボタンが出る
* プレイモード中はプレビューしない（プレイモードでは実際に置き換わる）

## 手動で使う（ウィンドウ）

メニュー **Tools/MeshDeletionToolForTexture** でウィンドウを開き、対象オブジェクトを指定し、設定（Inspector と同じ項目名・説明）とサブメッシュのチェックを行って「テクスチャ透明部分のメッシュを削除」を押す。結果は `Assets/NewMesh.asset`（メッシュ名は「元のメッシュ名_deleted」）に保存され、前回の出力を上書きする。NDMF が無いプロジェクトでも使える。処理の内容は NDMF 経由と同じ。

# 設定の意味

Inspector とウィンドウに表示される名前。括弧内は以前の名前（コンポーネントに保存される値の名前は変わっていないので、以前の設定はそのまま引き継がれる）。

| 項目 | 既定 | 意味 |
|---|---|---|
| 透明とみなす境界（アルファ閾値） | 0.5（0〜1） | アルファ値（不透明度）がこの値より小さい（より透明な）部分のメッシュを削除する。大きくすると半透明の部分も削除される。アルファ値は GPU が展開した値（描画に使われている値そのもの。圧縮テクスチャでは圧縮後の値）。PNG のアルファ値と厳密に一致させたいときはテクスチャのインポート設定を非圧縮にする |
| 輪郭に沿って細かく切る（境界の細分化） | オン | 透明との境目の近くのポリゴンを細かく分けてから切り、切り口をテクスチャの輪郭に沿わせる。オフにすると元のポリゴンの辺の上でしか切らないため、細い部分が欠けることがある（従来の動作） |
| 切り抜きの精度（境界の精度（テクセル）） | 標準（1） | 切り口がテクスチャの輪郭からずれてよい量。高精度（0.5）/ 標準（1）/ 軽量（2）/ 最軽量（4）から選ぶ。「カスタム」を選ぶと 0.5〜4 の任意の値をスライダーで指定できる。細かいほど輪郭に正確に沿うがポリゴンが増える。下に「≈ 0.5〜0.9 mm 単位で輪郭に沿わせます」のように、対象のメッシュとテクスチャ解像度から概算した長さが表示される。「輪郭に沿って細かく切る」がオフのときは使われない |
| 処理対象のサブメッシュ | テクスチャを持つ全て | チェックしたサブメッシュだけを処理する。テクスチャ（Texture2D）の無いサブメッシュは処理できない（警告アイコン付きで無効表示） |
| 細分化の最大深さ（詳細設定） | 3（0〜5） | 輪郭の近くのポリゴンを何段階まで細かく分けるか。大きいほど細い部分まで拾えるが、時間とポリゴン数が増える |
| 切断後に再結合する（詳細設定、切断後の再結合） | オン | 細かく分かれたポリゴンを元の面ごとにまとめ直し、切り口の頂点を間引いてポリゴン数を抑える |

## 動作の詳細

* 要素毎に独立な判定は、「境界の細分化」が有効で Compute Shader（`Editor/Shaders/MeshDeletionStages.compute`）が使える環境では GPU で、それ以外（細分化が無効、Compute Shader 非対応、`-nographics`、シェーダーが見つからない）では CPU で実行する。選んだ実行先は 1 行のログに出る。CPU と GPU は同じ数式（`Editor/Core/StageKernels.cs` と HLSL を行単位で対応、閾値との比較は 256 要素の表の参照のみ）で計算する
* テクスチャは処理対象のサブメッシュのものだけを読む。GPU が使えない環境では、読み出す間だけインポート設定を読み取り可能・非圧縮に変更し、読み終えたら元に戻す
* マテリアルやテクスチャの無いサブメッシュにチェックが入っていても例外にはせず、1 行の警告を出して処理対象から外す
* 1 頂点あたり 4 ボーンまで扱う。5 本以上のウェイトを持つ頂点があるときは警告を出し、5 番目以降のウェイトは失われる
* 出力の頂点数が 65,535 を超えるときは 32 ビットのインデックスにする
* 三角形が全て削除されたサブメッシュは空のまま残す（サブメッシュの数と順番、マテリアルとの対応を保つ）

# 性能の目安

VRoid 製アバターのサバゲ衣装（5,637 頂点 / 8,776 三角形 / 11 サブメッシュ / 72 ブレンドシェイプ）を既定の設定で処理したときの実測値です。環境: Unity 2022.3.22f1、Radeon RX 7900 XTX。

| 処理 | 時間 |
|---|---|
| メッシュ処理（CPU） | 約 361 ms |
| メッシュ処理（GPU、Compute Shader） | 約 201 ms |
| テクスチャのアルファ読み出し（Blit + ReadPixels、GPU / CPU 共通） | 約 196 ms |

配列化・GPU 化の前の実装（`Mesh` のプロパティと `Texture2D.GetPixel` を直接使う方式）は同じモデルで 7〜22 秒と見積もられ、この差のほとんどはメッシュ配列の読み出し回数と頂点探索の計算量の削減によるものです。数値はメッシュとテクスチャの解像度、GPU、Unity のバージョンで変わります。プレビューは同じ設定・同じ入力なら結果を使い回すため、2 回目以降の表示は即時です。

# 既知の制限・未対応

公開時点で分かっている問題と、対処していない点です。原因の分析と修正案は開発時の調査に基づきます。

* **不透明な三角形の内部にある透明な穴は削られない**: 細分化の判定（`StageKernels.RefineTest`）は「削られる側に不透明テクセルがあるか」だけを見ているため、3 頂点とも不透明な三角形の内部や、部分的に切られる三角形の残る側にある透明領域（ストラップ先端の D リングの穴など）は細分化されず、穴が小さく歪んだ形で残る。細分化の深さを上げても改善しない。「残る側に透明テクセルがあれば分割する」対称なルールの追加と、最近傍テクセルの 0/1 判定の代わりに 4 テクセルのバイリニア補間で境界を求める変更が修正案として残っているが、未実装
* **切り口に沿った 1 テクセル幅の暗い帯**: 切り口はテクセル格子の境界に落ちるため、残る側に幅 1 テクセル未満の透明テクセルの帯が残る。不透明シェーダー（VRChat/Mobile/Toon Lit など）ではこれが黒い縁として描かれる。カットアウト系（アルファテストあり）のシェーダーを使うか、「透明とみなす境界」を少し上げて対処する
* **最近傍テクセルによる階段状の切り口**: アルファは最近傍テクセルの値で 0/1 判定するため、なめらかな等高線に対して最大 1 テクセル弱の階段状のずれが出る。サンプリング位置も `(int)(u * (w - 1))` で GPU のテクセル格子より最大 1 テクセル弱ずれる
* **1 頂点 5 本以上のボーンウェイト**: 5 番目以降は失われる（VRChat のアバターは 4 本までなので影響しない）
* **アルファ値は GPU が展開した値**: DXT などの圧縮テクスチャでは元の PNG と異なるアルファ値で判定する
* **CPU と GPU の結果**: 同じ数式で書かれ一致するように設計しているが、浮動小数演算の違いにより二分探索の 1 ステップ分だけ新しい頂点の位置が異なる可能性がある
* **1 本の辺が境界を複数回横切る場合**: 辺上の境界点は 1 つしか求めない（二分探索）。細分化が有効ならその辺は分割されるため、多くの場合は問題にならない
* **UV の継ぎ目**: UV の継ぎ目で隣り合う三角形は UV 空間では離れているため、それぞれ独立に細分化・切断され、位置の一致しない頂点（T 字の接続）ができることがある
* 三角形以外のサブメッシュ（Quads / Lines / Points）や UV0 の無いメッシュは処理できない
* メインテクスチャ（`Material.mainTexture`）のアルファ値だけを見る。マスクテクスチャや複数枚のテクスチャの合成には対応しない
* 細分化・切断は UV 空間で行うため、UV が重なっている（ミラーリングなど）部分では両方の領域のアルファ値が影響する
* NDMF 経由で生成したメッシュはビルドの一時アセットで、シーンには保存されない。メッシュをアセットとして残したいときはウィンドウ版を使う

# 構成

Runtime / Editor / Tests の 3 つの asmdef を持ち、`Packages/` と `Assets/` のどちらに置いても動きます。

```
package.json                 UPM / VPM のパッケージ定義（com.ochoco.mesh-deletion-tool）
Runtime/                     MeshDeletionTool.Runtime
  MeshDeletionForTexture.cs    オブジェクトに付けるコンポーネント。設定値だけを持つ（VRChat SDK があれば IEditorOnly）
Editor/                      MeshDeletionTool.Editor（Unity・NDMF に依存する部分）
  MeshDeletionNdmfPlugin.cs    NDMF プラグイン。ビルド時に MeshDeletionForTexture を処理して取り除く
  MeshDeletionPreviewFilter.cs NDMF のプレビュー（IRenderFilter）。プロキシのメッシュを差し替える
  MeshDeletionPlayModeApplier.cs アバターの配下にないオブジェクトをプレイモードで置き換える簡易適用
  MeshDeletionApplier.cs       1 つの Renderer に対する「読む → 処理 → メッシュを作る」の共通手順と結果
  MeshDeletionRunner.cs        NDMF・簡易適用・ウィンドウが共有する入口。検証、テクスチャの収集、ログ
  MeshDeletionForTextureEditor.cs コンポーネントの Inspector と Hierarchy の右クリックメニュー
  MeshDeletionSettingsGUI.cs   Inspector とウィンドウで共通の設定 UI（ラベル、ツールチップ、精度プリセット）
  MeshDeletionPreviewStats.cs  プレビュー中のポリゴン数・頂点数の変化の表示
  MeshDeletionToolForTexture.cs 手動のウィンドウ（Tools/MeshDeletionToolForTexture）
  AlphaMaskReader.cs           テクスチャのアルファを GPU 経由（Blit + ReadPixels）で AlphaMask に読む
  TemporaryReadableTextures.cs GPU が使えないときにインポート設定を一時的に変えて読む
  ComputeStageBackend.cs       Compute Shader で要素毎の判定を行う IAlphaStageBackend
  MeshArraysUnityAdapter.cs    Mesh と MeshArrays の相互変換
  TexelSizeEstimator.cs        メッシュとテクスチャ解像度から 1 テクセルの長さ（mm）を概算する
  Shaders/MeshDeletionStages.compute  GPU 側のカーネル（StageKernels.cs と行単位で対応）
Editor/Core/                 Unity のオブジェクトに依存しない処理本体（UnityEngine の数学型のみ使用）
  AlphaMeshDeletionPipeline.cs 細分化 → 切断 → 再結合の全体の流れ
  AlphaBoundaryRefiner.cs      アルファ境界付近の三角形の適応的な細分化
  AlphaMeshCutter.cs           頂点の透明判定、辺上の二分探索、ポリゴンの再構成、頂点属性の補間
  CutPolygonMerger.cs          元の三角形ごとの再結合と切り口の間引き（Douglas–Peucker）
  EarClipping2D.cs             UV 空間での多角形の三角形分割
  StageKernels.cs              要素毎の判定の数式（CPU 側。HLSL と対応）
  CpuStageBackend.cs / IAlphaStageBackend.cs / StageBatch.cs  CPU バックエンドと共通インターフェース
  MeshArrays.cs / MeshData.cs  メッシュの配列表現（全ストリーム、ブレンドシェイプ、サブメッシュ）
  AlphaMask.cs                 テクスチャのアルファ値の配列と参照表
  BoneWeightUtils.cs / VertexAttributeUtils.cs  ボーンウェイト・法線・接線などの補間
  MeshDeletionOptions.cs / PrecisionPresets.cs  設定値とプリセット
  PolygonStats.cs              ポリゴン数の変化の集計と色分け
  PlayModeApplyPolicy.cs       誰が置き換えるか（NDMF / 簡易適用 / なし）の判定
  MeshArraysComparer.cs        メッシュの比較（テスト用）
Tests/Editor/                MeshDeletionTool.Tests（EditMode の NUnit テスト。ScalarStageOracle は GPU/CPU の参照実装）
build/                       Unity を使わないビルド・検証スクリプト（下記）
```

# ビルドとテスト

## Unity でテストを実行する

Window > General > Test Runner の EditMode タブに `MeshDeletionTool.Tests` が出ます。`Packages/` に置いた場合は `Packages/manifest.json` の `testables` に `com.ochoco.mesh-deletion-tool` を追加すると表示されます。テストは `Editor/Core/` の処理と、CPU バックエンドがスカラーの参照実装（`ScalarStageOracle`）と一致することを確認します。

## 配布ファイルを作る（Unity 不要）

Python 3 だけで unitypackage と UPM zip を作れます。

```
python3 build/unitypackage/build_unitypackage.py            # build/out/ に 3 つのファイルを出力
python3 build/unitypackage/verify_unitypackage.py build/out/MeshDeletionTool_v0.1.0.unitypackage
python3 build/unitypackage/verify_unitypackage.py build/out/MeshDeletionTool_UPM_v0.1.0.zip
```

unitypackage は Unity のエクスポートと同じ形式（`archtemp.tar` を gzip したもの。GUID ごとに `asset` / `asset.meta` / `pathname` を持つ GNU tar）で書き出します。GUID はリポジトリにコミットされている `.meta` から読むため、unitypackage・UPM zip・git URL のどの方法で入れても同じ GUID になります。詳細は `build/README.md` を参照してください。

## Unity を起動せずにコンパイルを確認する

開発は Unity のライセンスの無い環境で行ったため、Unity Editor に同梱されている Roslyn（`Data/DotNetSdkRoslyn/csc.dll`）で Editor の DLL を参照して 3 つのアセンブリをコンパイルし、Unity に依存しない `Editor/Core/` のテストは Editor 同梱の Mono で NUnit を直接実行して確認しました。`build/compile_check.sh` はそのコンパイル確認を `UNITY_EDITOR_DIR`（と任意の `NDMF_DIR` / `VRCSDK_BASE_DLL`）を指定して再現するスクリプトです。NDMF あり・なし、VRChat SDK あり・なしの各構成で 0 エラーになることを確認しています。

# 履歴

* 2024: メッシュの一部削除ツールとして開発（Box による削除、テクスチャの透明部分の削除、色情報の表示など）
* 2026-09: テクスチャの透明部分の削除に機能を絞り、境界の細分化・再結合、頂点属性の完全な引き継ぎ、GPU バックエンド、NDMF コンポーネント・プレビュー・プレイモードの簡易適用を追加。Unity を使わないビルド・検証手順を整備してリファレンス実装として公開（v0.1.0）

変更の詳細は [CHANGELOG.md](CHANGELOG.md) を参照してください。

# Note

実行前は必ずプロジェクトのバックアップを作成すること

# Author

* おちょこ
* https://twitter.com/ochoco0215

# License

著作権 (c) 2024 おちょこ<br>
無断転載・複製を禁じます。<br>

本ソフトウェアは、おちょこ（以下「著作権者」）が著作権を有します。著作権者は、本ソフトウェアの使用を以下の条件のもとに許可します。

1. 使用許可<br>
   本ソフトウェアは、事前の書面による許可を得た場合に限り、商用目的に使用することができます。

2. 禁止事項<br>
   a. 本ソフトウェアの全部または一部を無断で複製、改変、配布、再配布することを禁止します。<br>
   b. 本ソフトウェアをリバースエンジニアリング、逆コンパイル、逆アセンブルすることを禁止します。<br>
   c. 本ソフトウェアを第三者にサブライセンスすることを禁止します。

3. 免責事項<br>
   本ソフトウェアは「現状有姿」で提供されます。著作権者は、本ソフトウェアに関していかなる保証も行いません。著作権者は、本ソフトウェアの使用または使用不能から生じるいかなる損害についても責任を負いません。

4. その他<br>
   本契約のいかなる部分も、適用される法律に反する場合には、その部分のみが無効となり、それ以外の部分は引き続き有効とします。

詳細については、[おちょこのX(旧Twitter)アカウント](https://twitter.com/ochoco0215)までお問い合わせください。

著作権者：おちょこ<br>
連絡先：[おちょこのX(旧Twitter)アカウント](https://twitter.com/ochoco0215)

# English summary

MeshDeletionTool is a Unity editor extension that cuts a mesh along the alpha outline of its texture and deletes the transparent part, for platforms without transparent shaders (VRChat Quest avatars made with VRoid).
It is published as a reference implementation. For everyday use we recommend [Yoridori Modifiers](https://yoridrill.booth.pm/items/8189252) (Yoridori Garage, free on BOOTH), a maintained set of non-destructive VRChat avatar tools that includes mesh trimming.
What the code does: classifies vertices by texture alpha, bisects the alpha boundary on every crossing edge, adaptively refines triangles near the boundary, merges the cut back into one polygon per original triangle and simplifies the cut chain (Douglas–Peucker), and interpolates every vertex attribute (normals, tangents, colors, UV0–7, bone weights, blend shapes) for the new vertices.
The per-element stages run on a Compute Shader when available and on the CPU otherwise, written line for line from the same formulas.
Texture alpha is read through the GPU (Blit + ReadPixels), so import settings are left untouched.
The `MeshDeletionForTexture` component is applied non-destructively by an NDMF plugin at Play and upload time, previewed through NDMF's preview system with polygon statistics in the inspector, and applied by the tool itself in Play mode for objects outside an avatar root; a manual window (Tools/MeshDeletionToolForTexture) writes the result as an asset.
Requirements: Unity 2022.3, NDMF 1.8.0 or later for the non-destructive path (optional), VRChat SDK optional.
Known limitations: transparent holes inside fully opaque triangles are not carved (a symmetric refinement rule and bilinear alpha sampling are documented as future work), a sub-texel transparent sliver remains along cuts with opaque shaders, nearest-texel staircase up to one texel, at most four bone weights per vertex.
`build/` holds Python scripts that build and verify the unitypackages and the UPM zip without Unity, and a Roslyn-based compile check (`UNITY_EDITOR_DIR=... build/compile_check.sh`); `.meta` files are committed so every install method shares the same GUIDs.
See the License section above for the license terms.
