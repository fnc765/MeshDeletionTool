# MeshDeletionTools
 
"MeshDeletionTool"はUnityで動作するメッシュ一部削除を目的としたエディタ拡張<br>
透明シェーダーが使えない環境にて透明テクスチャを用いて表現されているモデルに対して、
テクスチャ外形をベースにメッシュを加工し、透明部分を削除する<br>
開発のきっかけとしてはVroidをQuest対応させるための海苔現象に対処するため<br>

# Functions list
 
* ***MeshDeletionToolForBox***<br>
ボックスオブジェクトと削除対象オブジェクトを指定することでボックスオブジェクトに重なったメッシュを削除する
 
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

プロプライエタリ・ソフトウェアとして管理する<br>
（将来的にboothでの販売を目的として開発する）

"MeshDeletionTool" is Confidential.