# MeshDeletionTools
 
"MeshDeletionTool"はUnityで動作するメッシュ一部削除を目的としたエディタ拡張<br>
透明シェーダーが使えない環境にて透明テクスチャを用いて表現されているモデルに対して、
テクスチャ外形をベースにメッシュを加工し、透明部分を削除する<br>
**本リポジトリは上記機能実装に向けた各機能の勉強用として実装したコードを保存するために使用する**<br>
開発のきっかけとしてはVroidをQuest対応させるための海苔現象に対処するため<br>

# Functions list

* ***MeshDeletionTool***<br>
メッシュ処理に関連する一般的な機能を提供するクラス<br>

* ***MeshDeletionToolForBox***<br>
ボックスオブジェクトと削除対象オブジェクトを指定することでボックスオブジェクトに重なったメッシュを削除する<br>
MeshDeletionToolを継承している<br>

* ***MeshGetColorInfo***<br>
対象オブジェクトを指定することで、そのオブジェクトに含まれる各サブメッシュ毎に指定した頂点番号のテクスチャRGBA値をLogに出力する

* ***MeshDeletionToolForTexture***<br>
削除対象オブジェクトを指定することでそのオブジェクトが持っているテクスチャのアルファ値が0の頂点を削除する<br>
また削除頂点と、そうでない頂点を含む辺に対して辺上でのアルファ境界値を二部探索しそこを新たな頂点として登録する<br>
ポリゴンを再構成し新規メッシュを生成する<br>
SkinnedMeshRendererの補完に対応している<br>
既存辺上への頂点追加のみなので、細かい部分は消えてしまう問題がある<br>

# Features

テクスチャ外形に合わせて頂点を補完し、適切なメッシュへ変換できる<br>
（既存メッシュを削除するだけではない）
 
# Requirement

動作確認環境
* Unity 2022.3.6f1
 
# Usage
 
1. Unityにてプロジェクトを作成する
2. プロジェクトのAssetフォルダに移動し、以下コマンドを実行
```bash
cd <projectName>/Asset/
git clone https://github.com/fnc765/MeshDeletionTool.git
```
3. Unityプロジェクトを開き、メニューバーのToolから使いたい機能を呼び出す
 
# Note
 
実行前は必ずプロジェクトのバックアップを作成すること
 
# Author
 
* おちょこ
* https://twitter.com/ochoco0215

# License
 
"MeshDeletionTools" is under [MIT license](https://en.wikipedia.org/wiki/MIT_License).
