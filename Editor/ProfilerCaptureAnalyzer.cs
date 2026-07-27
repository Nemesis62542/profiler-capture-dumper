using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal; // ProfilerDriver
using UnityEngine;

namespace StarOceanMemories.ProfilerCaptureDumper.Editor
{
    /// <summary>
    /// Profiler の .data キャプチャを読み込み、解析結果をファイルに書き出す。
    ///
    /// 出力は3種類。
    ///   _frames.csv  : 全フレームの時間とカウンタ。傾向を見る用（機械可読）
    ///   _markers.csv : 注目フレームのスレッド別マーカー内訳。原因の特定用（機械可読）
    ///   _hotspots.txt: サマリとスパイクの要因一覧。人間が読む用
    /// </summary>
    public static class ProfilerCaptureAnalyzer
    {
        /// <summary>
        /// キャプチャの既定の置き場所（プロジェクト直下）
        /// </summary>
        public const string DefaultCaptureDirName = "ProfilerCaptures";

        /// <summary>
        /// このパッケージのバージョン。取得できない場合は "unknown"。
        /// 出力に刻んでおくと、古い出力を読んだときに
        /// どの版が作ったものか判別できる
        /// </summary>
        public static string PackageVersion
        {
            get
            {
                try
                {
                    var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(
                        typeof(ProfilerCaptureAnalyzer).Assembly);
                    return info != null ? info.version : "unknown";
                }
                catch
                {
                    return "unknown";
                }
            }
        }

        /// <summary>
        /// _frames.csv に載せるカウンタ。存在しないものは自動でスキップされる
        /// </summary>
        static readonly string[] Counters =
        {
            "GC Allocated In Frame",
            "GC Reserved Memory",
            "GC Used Memory",
            "Total Reserved Memory",
            "Total Used Memory",
            "System Used Memory",
            "Draw Calls Count",
            "Batches Count",
            "SetPass Calls Count",
            "Triangles Count",
            "Vertices Count",
            "Shadow Casters Count",
            "Used Textures Count",
            "Used Textures Bytes",
            "Render Textures Count",
            "Render Textures Bytes",
            "Used Buffers Count",
            "Used Buffers Bytes",
            "Instantiated Object Count",
            "Object Count",
        };

        /// <summary>
        /// スレッド列挙の打ち切り上限
        /// </summary>
        const int MaxThreads = 128;

        /// <summary>
        /// 内訳を書き出すスパイクの上限。
        /// スパイクは取りこぼすと調査そのものが成立しないので、
        /// これは通常到達しない安全弁として大きめに取る
        /// </summary>
        const int MaxSpikeFrames = 1000;

        /// <summary>
        /// スパイクが無い/少ないときに参考として足す「重い順」のフレーム数。
        /// スパイクの枠を食わないよう少数に留める
        /// </summary>
        const int MaxFillerFrames = 10;

        /// <summary>
        /// 1スレッドあたりに残すマーカー数（self time の大きい順）
        /// </summary>
        const int MarkersPerThread = 40;

        /// <summary>
        /// これ未満の self time しか無いスレッドは出力しない
        /// </summary>
        const float ThreadNoiseFloorMs = 0.5f;

        /// <summary>
        /// 解析結果の出力先
        /// </summary>
        public struct Result
        {
            public bool Success;
            public string CapturePath;
            public string FramesCsvPath;
            public string MarkersCsvPath;
            public string ReportPath;
            public int FrameCount;
            public string Error;
        }

        /// <summary>
        /// プロジェクト直下の既定フォルダ
        /// </summary>
        public static string DefaultCaptureDir =>
            Path.Combine(Directory.GetParent(Application.dataPath).FullName, DefaultCaptureDirName);

        /// <summary>
        /// 既定フォルダで最も新しい .data キャプチャを返す（無ければ null）
        /// </summary>
        public static string FindLatestCapture()
        {
            if (!Directory.Exists(DefaultCaptureDir)) return null;

            return new DirectoryInfo(DefaultCaptureDir).GetFiles("*.data")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => f.FullName)
                .FirstOrDefault();
        }

        /// <summary>
        /// 最新のキャプチャを解析する
        /// </summary>
        public static Result AnalyzeLatest()
        {
            var latest = FindLatestCapture();
            if (latest == null)
            {
                return Fail(null, $".data キャプチャが見つかりません: {DefaultCaptureDir}");
            }

            return Analyze(latest);
        }

        /// <summary>
        /// 指定したキャプチャを解析し、CSV とレポートを同じフォルダに書き出す
        /// </summary>
        public static Result Analyze(string capturePath)
        {
            if (string.IsNullOrEmpty(capturePath) || !File.Exists(capturePath))
            {
                return Fail(capturePath, $"ファイルが見つかりません: {capturePath}");
            }

            // 読み込みは現在のプロファイラデータを置き換えるため、記録中なら止めておく
            ProfilerDriver.enabled = false;

            if (!ProfilerDriver.LoadProfile(capturePath, false))
            {
                return Fail(capturePath, $"キャプチャの読み込みに失敗しました: {capturePath}");
            }

            int first = ProfilerDriver.firstFrameIndex;
            int last = ProfilerDriver.lastFrameIndex;
            if (first < 0 || last < first)
            {
                return Fail(capturePath, $"フレームがありません (first={first}, last={last})");
            }

            var basePath = Path.ChangeExtension(capturePath, null);
            var result = new Result
            {
                CapturePath = capturePath,
                FramesCsvPath = basePath + "_frames.csv",
                MarkersCsvPath = basePath + "_markers.csv",
                ReportPath = basePath + "_hotspots.txt",
            };

            try
            {
                var frames = WriteFrameCsv(first, last, result.FramesCsvPath);
                result.FrameCount = frames.Count;

                var targets = SelectDetailFrames(frames, out var spikeFrames, out int totalSpikeCount);
                var attribution = WriteMarkerCsv(targets, result.MarkersCsvPath, out int markerRowCount);
                WriteReport(frames, targets, spikeFrames, totalSpikeCount, attribution, markerRowCount,
                    capturePath, result.ReportPath);

                result.Success = true;
            }
            catch (Exception e)
            {
                result.Success = false;
                result.Error = e.ToString();
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            return result;
        }

        static Result Fail(string capturePath, string error)
        {
            return new Result { Success = false, CapturePath = capturePath, Error = error };
        }

        // ------------------------------------------------------------------
        // フレーム単位の走査
        // ------------------------------------------------------------------

        /// <summary>
        /// 1フレーム分の概要
        /// </summary>
        public struct FrameInfo
        {
            public int Index;
            public float CpuMs;
            public float GpuMs;
        }

        static List<FrameInfo> WriteFrameCsv(int first, int last, string csvPath)
        {
            var frames = new List<FrameInfo>(last - first + 1);

            // 先頭フレームを見て、このキャプチャに存在するカウンタだけに絞る
            var activeCounters = new List<string>();
            using (var probe = ProfilerDriver.GetRawFrameDataView(first, 0))
            {
                if (probe.valid)
                {
                    foreach (var name in Counters)
                    {
                        if (probe.GetMarkerId(name) != FrameDataView.invalidMarkerId)
                        {
                            activeCounters.Add(name);
                        }
                    }
                }
            }

            var sb = new StringBuilder();
            sb.Append("frame,cpuMs,gpuMs,fps");
            foreach (var c in activeCounters) sb.Append(',').Append(c.Replace(' ', '_'));
            sb.AppendLine();

            int total = last - first + 1;
            for (int f = first; f <= last; f++)
            {
                if ((f - first) % 200 == 0 &&
                    EditorUtility.DisplayCancelableProgressBar("Profiler Capture Dumper",
                        $"フレーム走査 {f - first}/{total}", (float)(f - first) / total))
                {
                    break;
                }

                using (var raw = ProfilerDriver.GetRawFrameDataView(f, 0))
                {
                    if (!raw.valid) continue;

                    float cpu = raw.frameTimeMs;
                    float gpu = raw.frameGpuTimeMs;
                    frames.Add(new FrameInfo { Index = f, CpuMs = cpu, GpuMs = gpu });

                    sb.Append(f).Append(',')
                      .Append(F3(cpu)).Append(',')
                      .Append(F3(gpu)).Append(',')
                      .Append(F1(cpu > 0f ? 1000f / cpu : 0f));

                    foreach (var c in activeCounters)
                    {
                        int id = raw.GetMarkerId(c);
                        long v = id != FrameDataView.invalidMarkerId ? raw.GetCounterValueAsLong(id) : 0L;
                        sb.Append(',').Append(v);
                    }
                    sb.AppendLine();
                }
            }

            EditorUtility.ClearProgressBar();
            File.WriteAllText(csvPath, sb.ToString(), new UTF8Encoding(false));
            return frames;
        }

        /// <summary>
        /// 中央値の2倍を超えるフレームをスパイクとみなす閾値
        /// </summary>
        public static float SpikeThreshold(List<FrameInfo> frames)
        {
            if (frames.Count == 0) return 0f;

            var sorted = frames.Select(f => f.CpuMs).OrderBy(v => v).ToArray();
            float median = sorted[sorted.Length / 2];
            return Mathf.Max(median * 2f, median + 5f);
        }

        /// <summary>
        /// 内訳を書き出すフレームを選ぶ。
        ///
        /// スパイクは原則すべて記録する。以前は上限120で「重い順の埋め草」と
        /// 枠を共有していたため、スパイクが多いキャプチャでは後半のスパイクが
        /// 丸ごと欠落していた（調査対象そのものが消える）。
        ///
        /// 埋め草はスパイクが無いときに「最も重かったフレーム」を見るためのもので、
        /// 少数に限定してスパイクの枠を侵さないようにしている。
        /// </summary>
        /// <param name="spikeFrames">閾値を超えたフレーム（レポートで区別して出す）</param>
        /// <param name="totalSpikeCount">閾値を超えたフレームの総数。切り捨てが起きたかの判定に使う</param>
        static List<int> SelectDetailFrames(List<FrameInfo> frames,
            out HashSet<int> spikeFrames, out int totalSpikeCount)
        {
            var targets = new List<int>();
            spikeFrames = new HashSet<int>();
            totalSpikeCount = 0;
            if (frames.Count == 0) return targets;

            float threshold = SpikeThreshold(frames);

            // 時系列順に拾うので、切り捨てが起きても序盤の現象は残る
            var spikes = frames.Where(f => f.CpuMs > threshold).OrderBy(f => f.Index).ToList();
            totalSpikeCount = spikes.Count;

            foreach (var f in spikes)
            {
                if (targets.Count >= MaxSpikeFrames) break;
                targets.Add(f.Index);
                spikeFrames.Add(f.Index);
            }

            // 参考として重い順に少数だけ足す（閾値未満なので spikeFrames には入れない）
            int filler = 0;
            foreach (var f in frames.OrderByDescending(f => f.CpuMs))
            {
                if (filler >= MaxFillerFrames) break;
                if (spikeFrames.Contains(f.Index)) continue;

                targets.Add(f.Index);
                filler++;
            }

            targets.Sort();
            return targets;
        }

        // ------------------------------------------------------------------
        // マーカー単位の走査
        // ------------------------------------------------------------------

        class MarkerAgg
        {
            public string Name;
            public float SelfMs;
            public float TotalMs;
            public float GcBytes;
            public int Calls;
        }

        /// <summary>
        /// そのフレームで最も self time が大きかったマーカー（スレッドを問わず）。
        /// 待機マーカーは除外するので、実際に時間を使っていた処理が出る。
        ///
        /// GcCollectMs はそのフレームで観測された GC.Collect の最大 self time。
        /// 0 より大きければ、停止の正体は確保量であって Marker 自身ではない。
        /// </summary>
        public struct Attribution
        {
            public int Frame;
            public string Thread;
            public string Marker;
            public float SelfMs;
            public float GcCollectMs;
        }

        static Dictionary<int, Attribution> WriteMarkerCsv(List<int> targets, string csvPath,
            out int markerRowCount)
        {
            var attribution = new Dictionary<int, Attribution>();
            markerRowCount = 0;

            var sb = new StringBuilder();
            sb.AppendLine("frame,threadIndex,threadGroup,threadName,marker,selfMs,totalMs,calls,gcBytes");

            for (int i = 0; i < targets.Count; i++)
            {
                int frameIndex = targets[i];

                if (EditorUtility.DisplayCancelableProgressBar("Profiler Capture Dumper",
                        $"内訳ダンプ {i}/{targets.Count}", (float)i / targets.Count))
                {
                    break;
                }

                var best = new Attribution { Frame = frameIndex, SelfMs = -1f };

                // スレッド数を直接取る API が無いため、無効になるまで添字を進めて列挙する
                for (int t = 0; t < MaxThreads; t++)
                {
                    using (var view = ProfilerDriver.GetHierarchyFrameDataView(
                               frameIndex, t,
                               HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,
                               HierarchyFrameDataView.columnSelfTime, false))
                    {
                        if (!view.valid) break;

                        var agg = new Dictionary<int, MarkerAgg>();
                        Collect(view, view.GetRootItemID(), agg, 0);
                        if (agg.Count == 0) continue;

                        if (agg.Values.Sum(a => a.SelfMs) < ThreadNoiseFloorMs) continue;

                        string group = view.threadGroupName;
                        string name = view.threadName;

                        foreach (var a in agg.Values.OrderByDescending(a => a.SelfMs).Take(MarkersPerThread))
                        {
                            if (a.SelfMs < 0.01f) break;

                            sb.Append(frameIndex).Append(',')
                              .Append(t).Append(',')
                              .Append(Csv(group)).Append(',')
                              .Append(Csv(name)).Append(',')
                              .Append(Csv(a.Name)).Append(',')
                              .Append(F3(a.SelfMs)).Append(',')
                              .Append(F3(a.TotalMs)).Append(',')
                              .Append(a.Calls).Append(',')
                              .Append((long)a.GcBytes)
                              .AppendLine();
                            markerRowCount++;

                            // GC の停止は全スレッドに波及するため、別枠で記録しておく
                            if (IsGarbageCollectMarker(a.Name) && a.SelfMs > best.GcCollectMs)
                            {
                                best.GcCollectMs = a.SelfMs;
                            }

                            // 待機やスレッド名そのものは原因を示さないので除く
                            if (a.SelfMs > best.SelfMs && !IsUninformativeMarker(a.Name))
                            {
                                best.Thread = string.IsNullOrEmpty(group) ? name : $"{group}/{name}";
                                best.Marker = a.Name;
                                best.SelfMs = a.SelfMs;
                            }
                        }
                    }
                }

                if (best.SelfMs >= 0f) attribution[frameIndex] = best;
            }

            EditorUtility.ClearProgressBar();
            File.WriteAllText(csvPath, sb.ToString(), new UTF8Encoding(false));
            return attribution;
        }

        /// <summary>
        /// スレッド名やスレッドの入れ物そのものを指すマーカー
        /// </summary>
        static readonly HashSet<string> StructuralMarkers = new HashSet<string>
        {
            "Main Thread",
            "Render Thread",
            "PlayerLoop",
            "Root",
            "Idle",
        };

        /// <summary>
        /// 原因を示さないマーカーかどうか。
        ///
        /// 待機系（Idle、Semaphore.WaitForSignal、Gfx.WaitFor... など）は
        /// 「そのスレッドが暇だった時間」であって処理コストではない。
        /// これを除外しないと、他スレッドの待ち時間がフレーム時間と同じ長さで
        /// 計上され、原因の欄が待機マーカーで埋まってしまう。
        ///
        /// なおメインスレッドの WaitForJobGroupID などは
        /// 「ジョブ待ちで詰まっている」という有用な情報ではあるが、
        /// 原因そのものではないためここでは除く。詳細は _markers.csv を見ること。
        /// </summary>
        static bool IsUninformativeMarker(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            if (StructuralMarkers.Contains(name)) return true;

            // WaitForSignal / WaitForJobGroupID / Gfx.WaitForGfxCommandsFromMainThread など
            if (name.IndexOf("WaitFor", StringComparison.Ordinal) >= 0) return true;

            return false;
        }

        /// <summary>
        /// 停止型 GC のマーカーかどうか（インクリメンタルは含めない）
        /// </summary>
        static bool IsGarbageCollectMarker(string name)
        {
            return name == "GC.Collect";
        }

        static void Collect(HierarchyFrameDataView view, int id, Dictionary<int, MarkerAgg> agg, int depth)
        {
            if (depth > 128) return;

            int markerId = view.GetItemMarkerID(id);
            if (!agg.TryGetValue(markerId, out var entry))
            {
                entry = new MarkerAgg { Name = view.GetItemName(id) };
                agg[markerId] = entry;
            }

            entry.SelfMs += view.GetItemColumnDataAsSingle(id, HierarchyFrameDataView.columnSelfTime);
            entry.TotalMs += view.GetItemColumnDataAsSingle(id, HierarchyFrameDataView.columnTotalTime);
            entry.GcBytes += view.GetItemColumnDataAsSingle(id, HierarchyFrameDataView.columnGcMemory);
            entry.Calls += (int)view.GetItemColumnDataAsSingle(id, HierarchyFrameDataView.columnCalls);

            if (!view.HasItemChildren(id)) return;

            var children = new List<int>();
            view.GetItemChildren(id, children);
            foreach (var child in children) Collect(view, child, agg, depth + 1);
        }

        // ------------------------------------------------------------------
        // 人間向けレポート
        // ------------------------------------------------------------------

        static void AppendAttributionTable(StringBuilder sb, List<int> frameIndices,
            Dictionary<int, FrameInfo> byIndex, Dictionary<int, Attribution> attribution)
        {
            sb.AppendLine($"{"frame",8} {"cpuMs",10} {"topSelfMs",10} {"gcMs",9}  thread | marker");

            foreach (var frameIndex in frameIndices)
            {
                if (!byIndex.TryGetValue(frameIndex, out var info)) continue;

                if (!attribution.TryGetValue(frameIndex, out var a))
                {
                    sb.AppendLine($"{frameIndex,8} {info.CpuMs,10:F2} {"-",10} {"-",9}  (内訳なし)");
                    continue;
                }

                string gc = a.GcCollectMs > 0f ? a.GcCollectMs.ToString("F2") : "-";
                string marker = string.IsNullOrEmpty(a.Marker) ? "(該当なし)" : $"{a.Thread} | {a.Marker}";

                sb.AppendLine($"{frameIndex,8} {info.CpuMs,10:F2} {a.SelfMs,10:F2} {gc,9}  {marker}");
            }
        }

        /// <summary>
        /// 出力が妥当かどうかを機械的に点検する。
        ///
        /// 解析ツール自身のバグは「結果がそれらしく見える」ため気づきにくい。
        /// 実際、待機マーカーを除外していなかった頃は原因欄が Idle で埋まっていたが、
        /// 表としては成立していたので一見して異常とは分からなかった。
        /// 疑わしい兆候をここで明示しておく。
        /// </summary>
        static List<string> BuildSelfCheck(List<FrameInfo> frames, List<int> targets,
            HashSet<int> spikeFrames, int totalSpikeCount,
            Dictionary<int, Attribution> attribution, int markerRowCount)
        {
            var warnings = new List<string>();

            if (frames.Count == 0)
            {
                warnings.Add("[ERROR] フレームが1つも読めていない。キャプチャが壊れている可能性がある。");
                return warnings;
            }

            if (markerRowCount == 0)
            {
                warnings.Add("[ERROR] _markers.csv が空。マーカー走査の API が変わった可能性がある。");
            }

            if (!frames.Any(f => f.GpuMs > 0f))
            {
                warnings.Add("[WARN ] gpuMs が全フレーム 0。GPU 側の要因はこのキャプチャからは判定できない。");
            }

            // Profiler は直近 N フレームしか保持しないリングバッファなので、
            // 長く回してから保存すると前半が上書きされて消えている。
            // Play 開始直後はシーンロードと JIT で必ず突出して重くなるため、
            // 先頭フレームが平凡なら「途中から始まっている」と判断できる。
            var median = frames.Select(f => f.CpuMs).OrderBy(v => v).ElementAt(frames.Count / 2);
            if (median > 0f && frames[0].CpuMs < median * 10f)
            {
                warnings.Add(
                    $"[WARN ] 先頭フレームが {frames[0].CpuMs:F2} ms（中央値の " +
                    $"{frames[0].CpuMs / median:F1} 倍）で、Play 開始直後の特徴が無い。" +
                    "リングバッファが巻き戻り、これより前のフレームは失われている可能性が高い。" +
                    "調べたい現象が録画範囲の外にある場合は、短く回して録り直すこと。");
            }

            if (spikeFrames.Count == 0)
            {
                warnings.Add("[INFO ] 閾値を超えるスパイクなし。フレーム時間は安定している。");
            }

            if (totalSpikeCount > spikeFrames.Count)
            {
                warnings.Add($"[ERROR] スパイク {totalSpikeCount} 件のうち {spikeFrames.Count} 件しか" +
                             $"内訳を記録できていない（上限 {MaxSpikeFrames}）。" +
                             "後半のスパイクは _markers.csv に無い。");
            }

            int missing = targets.Count(t => !attribution.ContainsKey(t));
            if (missing > 0)
            {
                warnings.Add($"[WARN ] {missing}/{targets.Count} フレームで要因を特定できなかった。" +
                             "除外条件が広すぎる可能性がある。");
            }

            // 原因欄が全フレームで同じマーカーになる場合、除外漏れを疑う
            var attributed = attribution.Values.Where(a => !string.IsNullOrEmpty(a.Marker)).ToList();
            if (attributed.Count >= 5)
            {
                var top = attributed.GroupBy(a => a.Marker).OrderByDescending(g => g.Count()).First();
                if (top.Count() == attributed.Count)
                {
                    warnings.Add($"[WARN ] 全 {attributed.Count} フレームの要因が \"{top.Key}\" で同一。" +
                                 "待機マーカーの除外漏れを疑うこと。");
                }
            }

            int gcFrames = attribution.Values.Count(a => a.GcCollectMs > 0f);
            if (gcFrames > 0)
            {
                warnings.Add($"[WARN ] {gcFrames} フレームで停止型 GC を検出。" +
                             "スパイクの主因はほぼこれ。確保しているコードを探すこと。");
            }

            int editorFrames = attribution.Values.Count(a => a.Marker == "EditorLoop");
            if (editorFrames > 0)
            {
                warnings.Add($"[WARN ] {editorFrames} フレームの要因が EditorLoop（エディタ自身の負荷）。" +
                             "ビルド版の実態を知るには Development Build で取り直すこと。");
            }

            return warnings;
        }

        static void WriteReport(List<FrameInfo> frames, List<int> targets, HashSet<int> spikeFrames,
            int totalSpikeCount, Dictionary<int, Attribution> attribution, int markerRowCount,
            string capturePath, string reportPath)
        {
            var sb = new StringBuilder();

            // 出力を後から読んだときに、どの版が何を対象に作ったものか分かるようにする
            sb.AppendLine("=== ENVIRONMENT ===");
            sb.AppendLine($"dumper version : {PackageVersion}");
            sb.AppendLine($"unity version  : {Application.unityVersion}");
            sb.AppendLine($"generated at   : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"capture        : {Path.GetFileName(capturePath)}");
            sb.AppendLine($"settings       : maxSpikeFrames={MaxSpikeFrames}, " +
                          $"maxFillerFrames={MaxFillerFrames}, markersPerThread={MarkersPerThread}, " +
                          $"threadNoiseFloorMs={ThreadNoiseFloorMs}");
            sb.AppendLine();

            if (frames.Count == 0)
            {
                sb.AppendLine("=== SELF-CHECK ===");
                sb.AppendLine("  [ERROR] フレームが1つも読めていない。キャプチャが壊れている可能性がある。");
                File.WriteAllText(reportPath, sb.ToString(), new UTF8Encoding(false));
                return;
            }

            var sorted = frames.Select(f => f.CpuMs).OrderBy(v => v).ToArray();
            float median = sorted[sorted.Length / 2];
            float p95 = sorted[Mathf.Clamp((int)(sorted.Length * 0.95f), 0, sorted.Length - 1)];
            float max = sorted[sorted.Length - 1];
            bool gpuRecorded = frames.Any(f => f.GpuMs > 0f);

            // フレーム数だけでは何秒ぶんか分からないため、実時間も出す
            float wallClockSec = frames.Sum(f => f.CpuMs) / 1000f;

            sb.AppendLine("=== SUMMARY ===");
            sb.AppendLine($"frames     : {frames.Count} ({frames[0].Index}..{frames[frames.Count - 1].Index})");
            sb.AppendLine($"duration   : {wallClockSec:F1} 秒（Profiler は直近Nフレームしか保持しない）");
            sb.AppendLine($"first frame: {frames[0].CpuMs:F2} ms" +
                          $"{(frames[0].CpuMs > median * 10f ? "（Play開始から記録されている）" : "（起動フレームに見えない。SELF-CHECK参照）")}");
            sb.AppendLine($"cpu median : {median:F2} ms ({(median > 0f ? 1000f / median : 0f):F1} fps)");
            sb.AppendLine($"cpu p95    : {p95:F2} ms");
            sb.AppendLine($"cpu max    : {max:F2} ms");
            sb.AppendLine($"gpu times  : {(gpuRecorded ? "recorded" : "NOT recorded (GPU側の要因はこのキャプチャからは判定できません)")}");
            sb.AppendLine();

            sb.AppendLine("=== SELF-CHECK ===");
            sb.AppendLine("出力の妥当性を機械的に点検した結果。解析を読む前にここを確認すること。");
            sb.AppendLine();
            var checks = BuildSelfCheck(frames, targets, spikeFrames, totalSpikeCount,
                attribution, markerRowCount);
            if (checks.Count == 0)
            {
                sb.AppendLine("  指摘なし");
            }
            else
            {
                foreach (var c in checks) sb.AppendLine($"  {c}");
            }
            sb.AppendLine();

            var byIndex = frames.ToDictionary(f => f.Index, f => f);
            float threshold = SpikeThreshold(frames);

            var spikes = targets.Where(spikeFrames.Contains).ToList();
            var others = targets.Where(t => !spikeFrames.Contains(t)).ToList();

            sb.AppendLine($"=== SPIKES (> {threshold:F2} ms) : {spikes.Count} frames ===");
            sb.AppendLine("そのフレームで最も self time が大きかったマーカー（待機マーカーは除外）。");
            sb.AppendLine("gcMs が 0 より大きい場合、停止の正体は確保量であって marker 自身ではない。");
            sb.AppendLine();

            if (spikes.Count == 0)
            {
                sb.AppendLine("  (閾値を超えるフレームはありません)");
            }
            else
            {
                AppendAttributionTable(sb, spikes, byIndex, attribution);
            }

            if (others.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"=== OTHER HEAVY FRAMES : {others.Count} frames ===");
                sb.AppendLine("閾値未満だが上位に入ったフレーム。スパイクではないので参考程度に。");
                sb.AppendLine();
                AppendAttributionTable(sb, others, byIndex, attribution);
            }

            sb.AppendLine();
            sb.AppendLine("=== NOTES ===");
            sb.AppendLine("・EditorLoop が上位に来るフレームはエディタ自身の負荷。ビルド版では再現しない。");
            sb.AppendLine("・gcMs が付いているフレームは停止型 GC が走っている。全スレッドが止まるため、");
            sb.AppendLine("  停止時間はそのとき動いていたメソッドの self time に計上される。");
            sb.AppendLine("  犯人はそのメソッドではなく確保しているコード。");
            sb.AppendLine("・待機マーカー（Idle、Semaphore.WaitForSignal、*WaitFor* 系）は原因欄から除外している。");
            sb.AppendLine("  メインスレッドのジョブ待ちなどを確認したい場合は _markers.csv を直接見ること。");
            sb.AppendLine("・詳細な内訳は _markers.csv を参照（1行1マーカー）。");

            File.WriteAllText(reportPath, sb.ToString(), new UTF8Encoding(false));
        }

        // ------------------------------------------------------------------

        static string F1(float v) => v.ToString("F1", CultureInfo.InvariantCulture);
        static string F3(float v) => v.ToString("F3", CultureInfo.InvariantCulture);

        /// <summary>
        /// マーカー名にカンマや引用符が入るため、CSV としてエスケープする
        /// </summary>
        static string Csv(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return value;

            return '"' + value.Replace("\"", "\"\"") + '"';
        }
    }
}
