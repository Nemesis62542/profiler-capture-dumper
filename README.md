# Profiler Capture Dumper

Unity Profiler の `.data` キャプチャを CSV とテキストレポートに書き出すエディタ拡張。

`.data` は Unity 独自のバイナリなので外部ツールから読めない。このパッケージは
`ProfilerDriver` API 経由で読み込み、機械可読な CSV に落とす。解析スクリプトや
LLM にそのまま渡せる形式にすることが目的。

- 動作要件: Unity 2021.2 以上（`ProfilerModule` API を使うため）
- 検証済み: Unity 6000.3.9f1 (HDRP)

---

## 導入

### A. git URL から入れる（他プロジェクト・他の人向け／推奨）

Package Manager → **`+` → Add package from git URL**:

```
https://github.com/Nemesis62542/profiler-capture-dumper.git
```

バージョンを固定する場合はタグを付ける:

```
https://github.com/Nemesis62542/profiler-capture-dumper.git#v1.0.0
```

> **リポジトリが Private の場合**、初回に git の認証が必要になる。
> Git Credential Manager が設定されていれば通ることが多いが、通らない場合は
> リポジトリを Public にするのが最も手早い。

### B. ローカルパス参照（同じ PC の複数プロジェクト向け）

正本を1箇所に置き、各プロジェクトから参照する。1箇所直せば全プロジェクトに反映される。

`Packages/manifest.json` の `dependencies` に追記:

```json
"com.staroceanmemories.profiler-capture-dumper": "file:C:/Users/<user>/UnityPackages/com.staroceanmemories.profiler-capture-dumper"
```

Package Manager → **`+` → Install package from disk...** で `package.json` を
選んでも同じ行が書かれる。

> **注意**: パスがマシン依存になる。そのプロジェクトを git で他人と共有していると
> 相手の環境で必ず壊れるので、共有プロジェクトでは A を使うこと。

### C. フォルダごとコピー（埋め込みパッケージ）

`com.staroceanmemories.profiler-capture-dumper/` を対象プロジェクトの `Packages/` 直下に置く。
`manifest.json` の編集は不要（`Packages/` 直下は埋め込みパッケージとして自動認識される）。

最も壊れにくい代わりに、更新は手動コピーになる。

### 解析用の Claude Code skill（任意・別経路）

CSV を LLM に読ませて解析させる場合、解析手順を skill にまとめてある。
このパッケージとは配布経路が別なので、必要に応じて手動で配置する。

| 用途 | 置き場所 |
|---|---|
| 特定のプロジェクトだけ | そのプロジェクトの `.claude/skills/unity-profiler-analysis/` |
| 同じ PC の全プロジェクト | `~/.claude/skills/unity-profiler-analysis/` |

---

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
| `*_hotspots.txt` | 環境情報・自己診断・サマリ・スパイクの要因一覧。人間が読む用 |

`_markers.csv` に載るのは、CPU 時間が中央値の2倍を超えた「スパイク」フレーム全件
（安全弁として上限1000）と、参考用に「重い順」10フレーム。
各スレッドの self time 上位40マーカーを記録する。
万一スパイクが上限を超えた場合は SELF-CHECK が `[ERROR]` で知らせる。

`.data` は数百MB〜数GBになるので、`ProfilerCaptures/` は `.gitignore` に入れておくこと。

---

## 記録範囲の制約（重要）

Unity の Profiler は**直近 N フレームしか保持しないリングバッファ**で、上限は 2000 フレーム
（Preferences → Analysis → Profiler → Frame Count の最大値）。長く回してから保存しても、
残っているのは最後の一部だけで前半は上書きされている。

| フレームレート | 2000 フレームで録れる長さ |
|---|---|
| 240 fps | 約 11 秒 |
| 60 fps | 約 33 秒 |
| 30 fps | 約 67 秒 |

**調べたい現象が録画範囲の外にある可能性を必ず確認すること。**
`_hotspots.txt` の `SUMMARY` に `duration`（実時間）と `first frame` の判定を出している。
Play 開始直後はシーンロードと JIT で必ず突出して重くなるため、先頭フレームが平凡なら
そのキャプチャは途中から始まっている。SELF-CHECK でも警告する。

複数のキャプチャを比較するときは、**両方が同じ範囲（起動を含む / 含まない）か**を
揃えること。揃っていない比較は数値が歪む。

長時間を記録したい場合は `Profiler.logFile` + `Profiler.enableBinaryLog` による
ファイルストリーミングを使う（リングバッファを経由しない）。ただし 1 時間で 100GB 規模に
なるため、症状が出る区間に絞る方が現実的。**このパッケージは未対応。**

---

## 読み方の注意

- **`gcMs` 列が付いているフレームは、`marker` 欄が原因ではない。** 停止型 GC は
  全スレッドを止めるため、停止時間はそのとき動いていたメソッドの self time に
  計上される。犯人は確保しているコード。
- **`EditorLoop` が要因のフレームはエディタ自身の負荷。** ビルド版では再現しない。
  エディタ実行では Profiler 自身の記録で GC ヒープが数 GB に膨れることがあり、
  確保量の絶対値は当てにならない。正確に測るなら Development Build +
  Autoconnect Profiler で取り直す。
- **`gpuMs` が全フレーム 0.00 の場合、GPU 側の要因は判定できない。**
  「GPU は問題ない」と結論してはいけない。
- 待機マーカー（`Idle`、`Semaphore.WaitForSignal`、`*WaitFor*` 系）は要因欄から
  除外している。メインスレッドのジョブ待ちを見たい場合は `_markers.csv` を直接見ること。

`_hotspots.txt` 冒頭の `SELF-CHECK` は上記の条件を機械的に点検した結果なので、
解析を読む前にまずそこを確認する。

---

## 自動実行（CI / エージェント向け）

エディタを直接操作できない環境向けに、ファイル経由のトリガーがある。

1. `ProfilerCaptures/dump_request.txt` を作る（中身は任意）
2. アセットの再インポートを起こす

`InitializeOnLoadMethod` と `AssetPostprocessor` の両方から拾うため、
ドメインリロードが省略された場合でも発火する。実行後にリクエストファイルは削除される。

> 新しいコードは `PostProcessAllAssets` の**後**にホットリロードされるので、
> スクリプトを変更した直後のインポートでは発火が1回遅れる。

---

## メンテナンス

### 依存している不安定な API

このパッケージは **公開 API ではない** 部分に依存している。Unity のバージョンを
上げたときはここが壊れる可能性が高い。

| API | 名前空間 | 備考 |
|---|---|---|
| `ProfilerDriver` | `UnityEditorInternal` | `internal` 扱いだが public。キャプチャの読み込みとフレーム取得の中核 |
| `HierarchyFrameDataView` | `UnityEditor.Profiling` | マーカー階層の走査 |
| `RawFrameDataView` | `UnityEditor.Profiling` | フレーム時間とカウンタの取得 |
| `ProfilerModule` | `Unity.Profiling.Editor` | Profiler ウィンドウへのモジュール追加。2021.2 以降 |

過去に踏んだ非互換:

- `RawFrameDataView.threadCount` は Unity 6.3 に存在しない。スレッド数を直接取る
  API が無いため、添字を進めて `valid` が false になるまで列挙している
- `ProfilerModule` はチャートカウンタを1つ以上持つ必要がある。空配列だと
  `cannot have no chart counters` で登録が拒否される

### 出力の妥当性チェック

解析ツールのバグは「結果がそれらしく見える」ため気づきにくい。実際、待機マーカーを
除外していなかった頃は要因欄が `Idle` で埋まっていたが、表としては成立していたので
一見して異常とは分からなかった。

`_hotspots.txt` の `SELF-CHECK` はこの種の異常を機械的に検出する。
現在の検査項目:

- フレームが読めていない / `_markers.csv` が空（API 変更の疑い）
- 要因を特定できなかったフレームがある（除外条件が広すぎる疑い）
- 全フレームの要因が同一マーカー（除外漏れの疑い）
- 内訳の対象が上限に達した（後半のスパイクを取りこぼしている）
- 停止型 GC の検出 / `EditorLoop` 主因 / GPU 未記録

新しい誤りのパターンを見つけたら、`BuildSelfCheck` に検査項目を足していく。

### 改善の記録

解析のたびに気づいた問題は、利用側リポジトリの
`.claude/skills/unity-profiler-analysis/ANALYSIS_LOG.md` に追記していく運用にしている。
同じ指摘が繰り返し出たら、このパッケージ側を直すサイン。

