# Profiler Capture Dumper

Unity Profiler の `.data` キャプチャを CSV とテキストレポートに書き出すエディタ拡張。

`.data` は Unity 独自のバイナリなので外部ツールから読めない。このパッケージは
`ProfilerDriver` API 経由で読み込み、機械可読な CSV に落とす。解析スクリプトや
LLM にそのまま渡せる形式にすることが目的。

## 導入

### このプロジェクト内（埋め込みパッケージ）

`Packages/` 配下に置いてあるので、追加作業なしで動く。

### 他プロジェクトへ

`Packages/com.staroceanmemories.profiler-capture-dumper/` フォルダごと、
対象プロジェクトの `Packages/` にコピーする。`manifest.json` の編集は不要
（`Packages/` 直下のフォルダは埋め込みパッケージとして自動認識される）。

必要な Unity バージョンは 2021.2 以上（`ProfilerModule` API を使うため）。

## 使い方

1. Profiler でキャプチャし、`Save` でプロジェクト直下の `ProfilerCaptures/` に保存する
2. Profiler ウィンドウ左のモジュール一覧から **Capture Dumper** を選ぶ
3. 詳細ペインの **Dump Latest Capture** を押す

`Tools > Profiler Capture Dumper > Dump Latest Capture` からも実行できる。

出力は元の `.data` と同じフォルダに置かれる。

| ファイル | 内容 |
|---|---|
| `*_frames.csv` | 全フレームの `cpuMs` / `gpuMs` / `fps` + カウンタ最大20種。傾向把握用 |
| `*_markers.csv` | 注目フレームのスレッド別マーカー内訳（1行1マーカー）。原因特定用 |
| `*_hotspots.txt` | サマリと SPIKE ATTRIBUTION。人間が読む用 |

`_markers.csv` に載るのは、CPU 時間が中央値の2倍を超えた「スパイク」フレーム
（最大120フレーム）。各スレッドの self time 上位40マーカーを記録する。

## 読み方の注意

- **`GC.Collect` が出ているフレームは、そのマーカーが原因ではない。** GC は全スレッドを
  止めるため、停止時間はそのとき動いていたメソッドの self time に計上される。
  犯人は確保しているコード。
- **`EditorLoop` が上位に来るフレームはエディタ自身の負荷。** ビルド版では再現しない。
  エディタ実行では Profiler 自身の記録で GC ヒープが数 GB に膨れることがあり、
  確保量の絶対値は当てにならない。正確に測るなら Development Build +
  Autoconnect Profiler で取り直す。
- **`gpuMs` が全フレーム 0.00 の場合、GPU 側の要因は判定できない。**
  `_hotspots.txt` の `gpu times` 行に記載される。

## 自動実行（CI / エージェント向け）

エディタを直接操作できない環境向けに、ファイル経由のトリガーがある。

1. `ProfilerCaptures/dump_request.txt` を作る（中身は任意）
2. アセットの再インポートを起こす

`InitializeOnLoadMethod` と `AssetPostprocessor` の両方から拾うため、
ドメインリロードが省略された場合でも発火する。実行後にリクエストファイルは削除される。

なお新しいコードは `PostProcessAllAssets` の**後**にホットリロードされるので、
スクリプトを変更した直後のインポートでは発火が1回遅れる。
