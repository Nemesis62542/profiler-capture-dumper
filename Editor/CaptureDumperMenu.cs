using System.IO;
using UnityEditor;
using UnityEngine;

namespace StarOceanMemories.ProfilerCaptureDumper.Editor
{
    /// <summary>
    /// メニューからの実行と、ファイル経由での実行トリガー。
    ///
    /// リクエストファイル機構は、エディタを直接操作できない環境（CI や
    /// エージェントからの自動実行）で解析を走らせるためのもの。
    /// キャプチャフォルダに dump_request.txt を置いてスクリプトを再コンパイルさせると、
    /// 解析が1回だけ走ってファイルは削除される。
    /// </summary>
    public static class CaptureDumperMenu
    {
        const string RequestFileName = "dump_request.txt";

        [MenuItem("Tools/Profiler Capture Dumper/Dump Latest Capture")]
        public static void DumpLatest()
        {
            Report(ProfilerCaptureAnalyzer.AnalyzeLatest());
        }

        [MenuItem("Tools/Profiler Capture Dumper/Dump Capture...")]
        public static void DumpSelected()
        {
            var start = Directory.Exists(ProfilerCaptureAnalyzer.DefaultCaptureDir)
                ? ProfilerCaptureAnalyzer.DefaultCaptureDir
                : Directory.GetParent(Application.dataPath).FullName;

            var path = EditorUtility.OpenFilePanel("Profiler capture (.data)", start, "data");
            if (string.IsNullOrEmpty(path)) return;

            Report(ProfilerCaptureAnalyzer.Analyze(path));
        }

        static void Report(ProfilerCaptureAnalyzer.Result result)
        {
            if (!result.Success)
            {
                Debug.LogError($"[ProfilerCaptureDumper] {result.Error}");
                return;
            }

            Debug.Log($"[ProfilerCaptureDumper] {result.FrameCount} フレームを出力しました:\n" +
                      $"  {result.FramesCsvPath}\n" +
                      $"  {result.MarkersCsvPath}\n" +
                      $"  {result.ReportPath}");
        }

        // ------------------------------------------------------------------
        // ファイル経由のトリガー
        // ------------------------------------------------------------------

        [InitializeOnLoadMethod]
        static void ConsumeRequestOnLoad() => ConsumeRequest();

        /// <summary>
        /// リクエストファイルがあれば消費して解析を予約する。
        /// Unity 6 はホットリロードでドメインリロードを省略することがあるため、
        /// ドメインリロード時とアセットインポート時の両方から呼ぶ。
        /// </summary>
        static void ConsumeRequest()
        {
            var request = Path.Combine(ProfilerCaptureAnalyzer.DefaultCaptureDir, RequestFileName);
            if (!File.Exists(request)) return;

            // 再実行ループを避けるため、走らせる前に消す
            File.Delete(request);
            EditorApplication.delayCall += DumpLatest;
        }

        class RequestWatcher : AssetPostprocessor
        {
            static void OnPostprocessAllAssets(string[] imported, string[] deleted,
                string[] movedTo, string[] movedFrom)
            {
                ConsumeRequest();
            }
        }
    }
}
