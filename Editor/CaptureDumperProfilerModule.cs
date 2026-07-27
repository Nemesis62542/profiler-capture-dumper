using System;
using System.IO;
using Unity.Profiling;
using Unity.Profiling.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace StarOceanMemories.ProfilerCaptureDumper.Editor
{
    /// <summary>
    /// Profiler ウィンドウに "Capture Dumper" モジュールを追加する。
    ///
    /// Profiler ウィンドウのツールバーへ任意のボタンを足す公開 API は無いため、
    /// モジュールとして登録し、その詳細ペインに実行ボタンを置いている。
    /// </summary>
    [Serializable]
    [ProfilerModuleMetadata("Capture Dumper")]
    public class CaptureDumperProfilerModule : ProfilerModule
    {
        // モジュールはチャートカウンタを1つ以上持つ必要がある（空だと登録が拒否される）。
        // どうせ出すならスパイクの原因になりやすいものを並べておく
        static readonly ProfilerCounterDescriptor[] ChartCounters =
        {
            new ProfilerCounterDescriptor("GC Allocated In Frame", ProfilerCategory.Memory),
            new ProfilerCounterDescriptor("Draw Calls Count", ProfilerCategory.Render),
        };

        static readonly string[] AutoEnabledCategories =
        {
            ProfilerCategory.Memory.Name,
            ProfilerCategory.Render.Name,
        };

        public CaptureDumperProfilerModule()
            : base(ChartCounters, ProfilerModuleChartType.Line, AutoEnabledCategories) { }

        public override ProfilerModuleViewController CreateDetailsViewController()
        {
            return new CaptureDumperViewController(ProfilerWindow);
        }
    }

    /// <summary>
    /// モジュールの詳細ペインに表示する UI
    /// </summary>
    public class CaptureDumperViewController : ProfilerModuleViewController
    {
        Label statusLabel;
        Label capturePathLabel;
        string selectedCapturePath;

        public CaptureDumperViewController(ProfilerWindow profilerWindow) : base(profilerWindow) { }

        protected override VisualElement CreateView()
        {
            var root = new VisualElement();
            root.style.paddingLeft = 10;
            root.style.paddingRight = 10;
            root.style.paddingTop = 8;
            root.style.paddingBottom = 8;

            var title = new Label("Profiler Capture Dumper");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.marginBottom = 4;
            root.Add(title);

            var help = new Label(
                "保存済みの .data キャプチャを CSV とテキストレポートに書き出します。\n" +
                "出力は元の .data と同じフォルダに置かれます。");
            help.style.whiteSpace = WhiteSpace.Normal;
            help.style.marginBottom = 8;
            root.Add(help);

            capturePathLabel = new Label();
            capturePathLabel.style.whiteSpace = WhiteSpace.Normal;
            capturePathLabel.style.marginBottom = 8;
            root.Add(capturePathLabel);

            var buttonRow = new VisualElement();
            buttonRow.style.flexDirection = FlexDirection.Row;
            buttonRow.style.marginBottom = 8;
            root.Add(buttonRow);

            buttonRow.Add(new Button(DumpLatest)
            {
                text = "Dump Latest Capture",
                tooltip = $"{ProfilerCaptureAnalyzer.DefaultCaptureDirName} 内で最も新しい .data を解析します",
            });

            buttonRow.Add(new Button(DumpSelected)
            {
                text = "Select Capture…",
                tooltip = "解析する .data を選びます",
            });

            buttonRow.Add(new Button(OpenOutputFolder)
            {
                text = "Open Folder",
                tooltip = "出力先フォルダを開きます",
            });

            statusLabel = new Label();
            statusLabel.style.whiteSpace = WhiteSpace.Normal;
            root.Add(statusLabel);

            RefreshCapturePathLabel();
            return root;
        }

        void RefreshCapturePathLabel()
        {
            var latest = selectedCapturePath ?? ProfilerCaptureAnalyzer.FindLatestCapture();

            capturePathLabel.text = latest != null
                ? $"対象: {Path.GetFileName(latest)}"
                : $"対象: (見つかりません) — {ProfilerCaptureAnalyzer.DefaultCaptureDir} に .data を保存してください";
        }

        void DumpLatest()
        {
            selectedCapturePath = null;
            RefreshCapturePathLabel();
            Run(ProfilerCaptureAnalyzer.AnalyzeLatest());
        }

        void DumpSelected()
        {
            var start = Directory.Exists(ProfilerCaptureAnalyzer.DefaultCaptureDir)
                ? ProfilerCaptureAnalyzer.DefaultCaptureDir
                : Directory.GetParent(Application.dataPath).FullName;

            var path = EditorUtility.OpenFilePanel("Profiler capture (.data)", start, "data");
            if (string.IsNullOrEmpty(path)) return;

            selectedCapturePath = path;
            RefreshCapturePathLabel();
            Run(ProfilerCaptureAnalyzer.Analyze(path));
        }

        void OpenOutputFolder()
        {
            var dir = ProfilerCaptureAnalyzer.DefaultCaptureDir;
            if (selectedCapturePath != null) dir = Path.GetDirectoryName(selectedCapturePath);

            if (Directory.Exists(dir))
            {
                EditorUtility.RevealInFinder(dir);
            }
            else
            {
                SetStatus($"フォルダがありません: {dir}", true);
            }
        }

        void Run(ProfilerCaptureAnalyzer.Result result)
        {
            if (!result.Success)
            {
                SetStatus(result.Error, true);
                Debug.LogError($"[ProfilerCaptureDumper] {result.Error}");
                return;
            }

            var message =
                $"{result.FrameCount} フレームを書き出しました:\n" +
                $"  {Path.GetFileName(result.FramesCsvPath)}\n" +
                $"  {Path.GetFileName(result.MarkersCsvPath)}\n" +
                $"  {Path.GetFileName(result.ReportPath)}";

            SetStatus(message, false);
            Debug.Log($"[ProfilerCaptureDumper] 出力完了:\n" +
                      $"  {result.FramesCsvPath}\n" +
                      $"  {result.MarkersCsvPath}\n" +
                      $"  {result.ReportPath}");
        }

        void SetStatus(string message, bool isError)
        {
            if (statusLabel == null) return;

            statusLabel.text = message;
            statusLabel.style.color = isError
                ? new StyleColor(new Color(0.9f, 0.4f, 0.4f))
                : new StyleColor(StyleKeyword.Null);
        }
    }
}
